using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 投射物命中反馈的**引擎适配层**（<see cref="IProjectileHitPort"/> 与
    /// <see cref="IProjectileHitHostSink"/> 的实现，组合根注入）。
    ///
    ///   · 主机：<see cref="SuSubsystemProjectiles.Update"/> 用本类包住 <c>base.Update</c>，
    ///     命中前拍下"每个玩家拥有的快投射物会命中谁 + 命中前血量"，命中后按
    ///     <c>(before - after) * AttackResilience</c> 算伤害（与引擎 ComponentMiner.cs:520 同一公式）。
    ///     这是唯一能拿到"引擎实际扣了多少血"的位置：投射物命中不走任何回执消息。
    ///   · 客户端：<see cref="Present"/> 只创建一个 <c>HitValueParticleSystem</c>
    ///     （与引擎 ComponentMiner.cs:524 的位置 / 速度 / 文字逐项一致），并且先做两道过滤：
    ///     本地可见范围、区块是否已载入（世界分区：客户端只有自己那一圈地形）。
    ///     这里没有 Injure / ChangeCell / 实体生成调用路径。
    ///
    /// 无静态状态：化身表 / 诊断都由组合根以实例注入。
    /// </summary>
    public sealed class ProjectileHitReplicaEngine : IProjectileHitPort, IProjectileHitHostSink
    {
        private const int ProblemLogMs = 10000;

        /// <summary>
        /// 引擎在 AttackBody 里对投射物命中做的门槛：只处理"够快"的投射物
        /// （旧实现同为 <c>Velocity.LengthSquared() &gt; 100f</c>）。
        /// Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemProjectiles.cs:76-82
        /// </summary>
        private const float MinProjectileSpeedSquared = 100f;

        /// <summary>身体射线半径（引擎 SubsystemProjectiles.cs:221 的 BodyInflateAmount）。</summary>
        private const float BodyInflateAmount = 0.2f;

        /// <summary>命中点 → 伤害数字的偏移（引擎 ComponentMiner.cs:524 的 0.75f * hitDirection）。</summary>
        private const float HitMarkerDirectionOffset = 0.75f;

        private struct HitCandidate
        {
            public ComponentHealth Health;
            public float HealthBefore;
            public Vector3 HitPoint;
            public Vector3 Direction;
            public Vector3 AttackerVelocity;
        }

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly List<HitCandidate> m_candidates = new List<HitCandidate>();
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private Project m_project;
        private SuSubsystemProjectiles m_projectiles;
        private bool m_clientReplica;
        private bool m_windowOpen;
        private long m_nextEventId;
        private long m_lastEmittedTick;

        private long m_captureWindows;
        private long m_captureIdle;
        private long m_captureEmpty;
        private long m_captureRejects;
        private long m_captureOverlap;
        private long m_captureCandidates;
        private long m_hitsEmitted;
        private long m_damageBelowThreshold;
        private long m_hostDropped;
        private long m_emitWithoutListener;
        private long m_emitFailures;

        private long m_presentCalls;
        private long m_presented;
        private long m_skippedDistance;
        private long m_skippedChunk;
        private long m_noListener;
        private long m_particleFaults;
        private long m_resets;

        public ProjectileHitReplicaEngine(NetworkAvatarSpawner spawner,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner;
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "projectile-hit" : ownerId;
        }

        public event Action<ProjectileHitData> HostProjectileHit;

        public bool IsClientReplica => m_clientReplica;

        /// <summary>
        /// 由命中反馈模块在 Tick 里维护：只有"主机 + 至少一个玩家端在线"才需要打开捕获窗口。
        /// false 时 <see cref="BeginHostHitCapture"/> 直接返回，不做每帧射线（纯性能）。
        /// </summary>
        public bool WantHostCapture { get; set; }

        // ================================================================= 泵 / 挂接

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                DetachShell();
                m_project = project;
                m_resets++;
            }
            if (project == null)
            {
                m_clientReplica = false;
                return;
            }
            var shell = project.FindSubsystem<SubsystemProjectiles>(false) as SuSubsystemProjectiles;
            if (!ReferenceEquals(shell, m_projectiles))
            {
                DetachShell();
                m_projectiles = shell;
            }
            if (m_projectiles != null) m_projectiles.HitSink = this;
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            m_clientReplica = terrain is SuSubsystemTerrain terrainShell && terrainShell.ClientReplica;
        }

        private void DetachShell()
        {
            if (m_projectiles != null && ReferenceEquals(m_projectiles.HitSink, this))
                m_projectiles.HitSink = null;
            m_projectiles = null;
        }

        public void Shutdown()
        {
            DetachShell();
            m_project = null;
            m_clientReplica = false;
            m_windowOpen = false;
            m_candidates.Clear();
        }

        // ================================================================= 主机：捕获窗口

        public void BeginHostHitCapture(float deltaTime)
        {
            if (m_windowOpen)
            {
                // 上一次窗口没有关（调用方在 base.Update 前抛过异常等）：旧候选作废，
                // 绝不跨帧带出去（跨帧的血量差会把别处的伤害算到这次命中上）。
                m_captureOverlap++;
                m_windowOpen = false;
                LogProblem("capture-overlap", "命中捕获窗口重入（上一个窗口未关闭）→ 旧候选作废" +
                    "（累计 projectileHitCaptureOverlap=" + m_captureOverlap + "）");
            }
            m_candidates.Clear();
            if (!WantHostCapture)
            {
                // 主机没人加入 / 本端不是主机：这一帧不需要命中捕获（模块在 Tick 里维护）。
                m_captureIdle++;
                return;
            }
            if (m_clientReplica)
            {
                // 客户端副本不模拟投射物；这条分支只可能出现在"主机刚转客户端"的过渡帧。
                m_captureRejects++;
                return;
            }
            if (!(deltaTime > 0f))
            {
                m_captureRejects++;
                return;
            }
            // Project / 子系统外壳换新后必须先重新挂接（模块 Tick 与子系统 Update 的相对顺序
            // 不保证，这里按需泵一次；引用没变时只是两次比较）。
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project)) Pump();
            if (m_clientReplica)
            {
                m_captureRejects++;
                return;
            }
            if (m_project == null || m_projectiles == null) return;
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(false);
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (bodies == null || terrain == null)
            {
                LogOnce("no-subsystem", "找不到 SubsystemBodies / SubsystemTerrain → 主机不做投射物命中捕获" +
                    "（伤害数字与命中事件都不产生）");
                return;
            }
            m_windowOpen = true;
            m_captureWindows++;
            ReadOnlyList<Projectile> projectiles = m_projectiles.Projectiles;
            for (int i = 0; i < projectiles.Count; i++)
            {
                Projectile projectile = projectiles[i];
                if (projectile == null || projectile.ToRemove) continue;
                // 引擎对"所在区块未载入"的投射物整段跳过（SubsystemProjectiles.cs:207-214）：
                // 它这一帧不移动也不命中，捕获也不该为它记候选。
                if (projectile.NoChunk) continue;
                if (projectile.Velocity.LengthSquared() <= MinProjectileSpeedSquared) continue;
                // 引擎只在"攻击者是玩家"时建伤害数字（ComponentMiner.cs:521）；
                // 动物 / 陷阱射出的投射物不产生数字，这里同一门槛。
                if (!(projectile.Owner is ComponentPlayer attacker)) continue;
                Vector3 direction = Vector3.Normalize(projectile.Velocity);
                Block block = BlocksManager.Blocks[Game.Terrain.ExtractContents(projectile.Value)];
                if (block == null) continue;
                Vector3 tipOffset = block.ProjectileTipOffset * direction;
                Vector3 start = projectile.Position + tipOffset;
                Vector3 end = projectile.Position + projectile.Velocity * deltaTime + tipOffset;
                BodyRaycastResult? bodyHit = bodies.Raycast(start, end, BodyInflateAmount,
                    (ComponentBody body, float distance) => true);
                if (!bodyHit.HasValue) continue;
                // 被地形挡住的投射物不会命中身体（引擎先做地形射线再处理身体）。
                TerrainRaycastResult? terrainHit = terrain.Raycast(start, end,
                    useInteractionBoxes: false, skipAirBlocks: true,
                    (int value, float distance) =>
                        BlocksManager.Blocks[Game.Terrain.ExtractContents(value)].IsCollidable);
                if (terrainHit.HasValue && bodyHit.Value.Distance >= terrainHit.Value.Distance)
                    continue;
                ComponentHealth health = bodyHit.Value.ComponentBody.Entity
                    .FindComponent<ComponentHealth>();
                if (health == null) continue;
                m_candidates.Add(new HitCandidate
                {
                    Health = health,
                    HealthBefore = health.Health,
                    HitPoint = bodyHit.Value.HitPoint(),
                    Direction = direction,
                    AttackerVelocity = attacker.ComponentBody?.Velocity ?? Vector3.Zero
                });
            }
            m_captureCandidates += m_candidates.Count;
        }

        public void EndHostHitCapture()
        {
            if (!m_windowOpen) return;
            m_windowOpen = false;
            if (m_candidates.Count == 0)
            {
                m_captureEmpty++;
                return;
            }
            int limit = m_candidates.Count < ProjectileHitPolicy.MaxEventsPerHostUpdate
                ? m_candidates.Count
                : ProjectileHitPolicy.MaxEventsPerHostUpdate;
            for (int i = 0; i < m_candidates.Count; i++)
            {
                HitCandidate candidate = m_candidates[i];
                if (candidate.Health == null) continue;
                float damage = (candidate.HealthBefore - candidate.Health.Health) *
                    candidate.Health.AttackResilience;
                if (!(damage > ProjectileHitPolicy.MinDamage))
                {
                    m_damageBelowThreshold++;
                    continue;
                }
                if (i >= limit)
                {
                    m_hostDropped++;
                    LogProblem("capture-limit", "一次 Update 内投射物命中超过 " +
                        ProjectileHitPolicy.MaxEventsPerHostUpdate + " 条 → 多出的不中继" +
                        "（累计 projectileHitDropped=" + m_hostDropped + "）");
                    continue;
                }
                Action<ProjectileHitData> handler = HostProjectileHit;
                if (handler == null)
                {
                    m_emitWithoutListener++;
                    LogOnce("no-listener", "投射物命中没有监听者（命中反馈模块未装配）→ 只计数，" +
                        "不广播（累计 " + m_emitWithoutListener + " 条）");
                    continue;
                }
                var data = new ProjectileHitData(CurrentTick(), NextEventId(), candidate.HitPoint,
                    candidate.Direction, candidate.AttackerVelocity, damage);
                try
                {
                    handler(data);
                }
                catch (Exception e)
                {
                    m_emitFailures++;
                    LogProblem("emit:" + e.GetType().Name, "投射物命中广播处理器异常（" +
                        data.Describe() + "）：" + e.GetType().Name + " " + e.Message);
                    continue;
                }
                m_hitsEmitted++;
            }
            m_candidates.Clear();
        }

        // ================================================================= 客户端：只建伤害数字

        public bool Present(in ProjectileHitData data, out string reason)
        {
            reason = null;
            m_presentCalls++;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            if (!ReferenceEquals(project, m_project)) Pump();
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            SubsystemParticles particles = project.FindSubsystem<SubsystemParticles>(false);
            if (particles == null)
            {
                reason = "no-particles-subsystem";
                return false;
            }
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null)
            {
                reason = "no-terrain-subsystem";
                return false;
            }
            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(false);
            if (sky == null)
            {
                reason = "no-sky-subsystem";
                return false;
            }
            if (!TryGetListenerPosition(project, out Vector3 observer, out reason))
            {
                m_noListener++;
                return false;
            }
            // 世界分区①：本地可见范围之外的命中不表现（主机已按兴趣窗口筛过一轮，
            // 这里防"窗口边界抖动 / 客户端视野设置更小"）。
            if (!ProjectileHitViewPolicy.IsWithinClientView(data.Position, observer,
                    sky.VisibilityRange, out _))
            {
                m_skippedDistance++;
                // 原因串保持稳定（不带距离数字）：模块按原因当限流 key，动态串会让 key 无限增长。
                reason = "distance";
                return false;
            }
            // 世界分区②：命中点所在区块必须已经在本地载入，否则不建粒子。
            // Source: Survivalcraft/Game/SubsystemTerrain.cs:Terrain.GetChunkAtCell
            int cellX = (int)Math.Floor(data.Position.X);
            int cellZ = (int)Math.Floor(data.Position.Z);
            TerrainChunk chunk = terrain.Terrain.GetChunkAtCell(cellX, cellZ);
            if (chunk == null || chunk.State <= TerrainChunkState.InvalidContents4)
            {
                m_skippedChunk++;
                reason = "chunk-not-ready";
                return false;
            }
            try
            {
                // 与引擎 ComponentMiner.cs:524 逐项一致：
                //   位置 = hitPoint + 0.75f * hitDirection
                //   速度 = hitDirection + attacker.ComponentBody.Velocity
                //   颜色 = 白，文字 = -damage（"0" 格式）
                var system = new HitValueParticleSystem(
                    data.Position + HitMarkerDirectionOffset * data.Direction,
                    data.Direction + data.Velocity, Color.White,
                    (-data.Damage).ToString("0", CultureInfo.InvariantCulture));
                particles.AddParticleSystem(system);
            }
            catch (Exception e)
            {
                m_particleFaults++;
                reason = "particle-" + e.GetType().Name;
                LogProblem("particle:" + e.GetType().Name,
                    "客户端创建伤害数字失败（" + data.Describe() + "）：" + e.GetType().Name + " " +
                    e.Message + "（累计 projectileHitParticleFaults=" + m_particleFaults + "）");
                return false;
            }
            m_presented++;
            return true;
        }

        private bool TryGetListenerPosition(Project project, out Vector3 position, out string reason)
        {
            position = Vector3.Zero;
            reason = null;
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            if (audio != null)
            {
                ReadOnlyList<Vector3> listeners = audio.ListenerPositions;
                if (listeners.Count > 0)
                {
                    position = listeners[0];
                    return true;
                }
            }
            if (m_spawner != null &&
                m_spawner.TryGetLocalPlayer(project, out PlayerData localPlayer, out _))
            {
                ComponentPlayer player = localPlayer?.ComponentPlayer;
                if (player?.ComponentBody != null)
                {
                    position = player.ComponentBody.Position;
                    return true;
                }
            }
            reason = "no-listener";
            return false;
        }

        // ================================================================= 时间 / id

        private uint NextEventId()
        {
            uint id = (uint)(++m_nextEventId);
            if (id == 0) id = (uint)(++m_nextEventId);
            return id;
        }

        private long CurrentTick()
        {
            SubsystemGameInfo info = m_project?.FindSubsystem<SubsystemGameInfo>(false);
            long tick = info == null ? 0L : (long)(info.TotalElapsedGameTime * 1000.0);
            // 世界重载会让引擎时钟回到 0；事件 id 单调，server_tick 也必须单调
            // （否则客户端会把它们当过期的丢掉）。
            if (tick < m_lastEmittedTick) tick = m_lastEmittedTick;
            m_lastEmittedTick = tick;
            return tick;
        }

        // ================================================================= 诊断

        public string Describe() => "hitCaptureWindows=" + m_captureWindows +
            " hitCaptureIdle=" + m_captureIdle +
            " hitCaptureCandidates=" + m_captureCandidates +
            " hitCaptureEmpty=" + m_captureEmpty +
            " hitCaptureRejected=" + m_captureRejects +
            " hitDamageBelowThreshold=" + m_damageBelowThreshold +
            " hitDropped=" + m_hostDropped +
            " hitEmitted=" + m_hitsEmitted +
            " hitEmitWithoutListener=" + m_emitWithoutListener +
            " hitEmitFailures=" + m_emitFailures +
            " · hitPresentCalls=" + m_presentCalls +
            " hitPresented=" + m_presented +
            " hitSkippedDistance=" + m_skippedDistance +
            " hitSkippedChunk=" + m_skippedChunk +
            " hitNoListener=" + m_noListener +
            " hitParticleFaults=" + m_particleFaults +
            " hitResets=" + m_resets;

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}

using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 烧尽表现的**引擎适配层**（<see cref="IBurnAwayEffectPort"/> 的实现，组合根注入）。
    ///
    ///   · 主机：<see cref="Pump"/> 把本适配器挂到注入后的
    ///     <see cref="SuSubsystemFireBlockBehavior"/> 的 <c>BurnAwayReplica</c> 上；火外壳在
    ///     5 秒烧尽分支结束、确认 <c>m_toBurnAway</c> 被引擎处理完后，只上报一批
    ///     （去重 + 封顶）已经烧掉的格。这里分配单调 event_id / server_tick 并抛给模块广播。
    ///   · 客户端：<see cref="Present"/> 只做两件事 —— `BurntDebrisParticleSystem` 与
    ///     `Audio/Sizzles`。没有 ChangeCell / DestroyCell / 实体生成 / 伤害调用路径；
    ///     距离与每批 15 / 4 的上限由 <see cref="BurnAwayPresentationPolicy"/> 计算。
    ///
    /// 无静态状态：反射入口 / 化身表 / 诊断都由组合根以实例注入。
    /// </summary>
    public sealed class BurnAwayEffectReplicaEngine : IBurnAwayEffectPort, IBurnAwayEffectSink
    {
        private const int ProblemLogMs = 5000;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly global::Engine.Random m_random = new global::Engine.Random();
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private Project m_project;
        private SuSubsystemFireBlockBehavior m_fire;
        private bool m_clientReplica;
        private long m_nextEventId;
        /// <summary>上一次发出的 server_tick（世界重载后引擎时钟会回 0，这里保证不下退）。</summary>
        private long m_lastEmittedTick;

        // ---------------------------------------------------------------- 主机计数
        private long m_hostBatches;
        private long m_hostCells;
        private long m_hostDropped;
        private long m_hostEmitted;
        private long m_emitWithoutListener;
        private long m_emitFailures;
        private long m_hostRejected;

        // ---------------------------------------------------------------- 客户端计数
        private long m_presentBatches;
        private long m_presentCells;
        private long m_debris;
        private long m_sounds;
        private long m_skippedDistance;
        private long m_skippedCap;
        private long m_missingAudio;
        private long m_particleFaults;
        private long m_audioFaults;
        private long m_resets;

        public BurnAwayEffectReplicaEngine(NetworkAvatarSpawner spawner,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner;
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "effects-sync" : ownerId;
        }

        public event Action<BurnAwayEffectData> HostBurnAwayBatch;

        public bool IsClientReplica => m_clientReplica;

        // ------------------------------------------------------------ 挂接

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                DetachFire();
                m_project = project;
                m_resets++;
            }
            if (project == null) return;

            m_clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                              shell.ClientReplica;
            SuSubsystemFireBlockBehavior fire =
                project.FindSubsystem<SuSubsystemFireBlockBehavior>(false);
            if (fire == null)
            {
                LogOnce("fire-subsystem-missing",
                    "没有找到注入后的 SuSubsystemFireBlockBehavior（引擎注入可能未生效）→ " +
                    "烧尽表现同步无法挂接（主机照常本地烧尽，客户端不会有碎屑/声音）");
                return;
            }
            if (!ReferenceEquals(fire, m_fire))
            {
                DetachFire();
                m_fire = fire;
                m_fire.BurnAwayReplica = this;
                Log("已挂接注入后的火子系统（" + (m_clientReplica ? "客户端=表现" : "主机=权威") +
                    "）：" + fire.GetType().Name);
            }
            else if (m_fire.BurnAwayReplica == null)
            {
                // 外壳重载后挂接点会被换掉：重新挂上，避免"看起来挂接成功却不再上报"。
                m_fire.BurnAwayReplica = this;
                Log("火子系统的烧尽上报点被清空 → 已重新挂接：" + m_fire.GetType().Name);
            }
        }

        /// <summary>Mod 卸载：摘掉火外壳挂接点，避免重载后旧适配器还挂着。</summary>
        public void Shutdown()
        {
            DetachFire();
            m_project = null;
            m_clientReplica = false;
        }

        private void DetachFire()
        {
            if (m_fire == null) return;
            try { m_fire.BurnAwayReplica = null; } catch (Exception) { }
            m_fire = null;
        }

        // ------------------------------------------------------------ 主机：火外壳上报

        /// <summary>
        /// 由 <c>SuSubsystemFireBlockBehavior</c> 在 5 秒烧尽分支后调用（每批最多一次）。
        /// <paramref name="droppedCells"/> = 超出单条消息 / 快照上限而没带上的格数。
        /// </summary>
        public void OnHostBurnAwayBatch(BurnAwayCellData[] cells, int droppedCells)
        {
            if (cells == null || cells.Length == 0) return;
            m_hostBatches++;
            m_hostCells += cells.Length;
            if (droppedCells > 0)
            {
                m_hostDropped += droppedCells;
                LogProblem("host-dropped", "烧尽批次超过单条消息上限：" + cells.Length +
                    " 格已上报，" + droppedCells + " 格本批未带上（累计丢弃 " + m_hostDropped +
                    "；引擎本端每批也只表现 15 碎屑 / 4 声音，计数 burnEffectHostDropped）");
            }
            if (m_clientReplica)
            {
                m_hostRejected++;
                LogProblem("host-batch-on-client", "客户端副本上的火外壳上报了烧尽批次（" +
                    cells.Length + " 格）→ 丢弃（客户端绝不广播）");
                return;
            }
            uint eventId = (uint)(++m_nextEventId);
            if (eventId == 0) eventId = (uint)(++m_nextEventId);
            long tick = CurrentTick();
            var data = new BurnAwayEffectData(0, tick, eventId, cells);
            Action<BurnAwayEffectData> handler = HostBurnAwayBatch;
            if (handler == null)
            {
                m_emitWithoutListener++;
                LogOnce("no-listener", "火外壳上报了烧尽批次，但还没有监听者（模块未装配）→ " +
                    "本批只计数，不广播（累计 " + m_emitWithoutListener + " 批）");
                return;
            }
            try
            {
                handler(data);
                m_hostEmitted++;
            }
            catch (Exception e)
            {
                m_emitFailures++;
                LogProblem("emit:" + e.GetType().Name, "烧尽批次广播处理器异常（" +
                    data.Describe() + "）：" + e.GetType().Name + " " + e.Message +
                    " → 本帧其余批次与本地引擎表现不受影响");
            }
        }

        // ------------------------------------------------------------ 客户端：只做表现

        public bool Present(in BurnAwayEffectData effect, out BurnAwayPresentationResult result,
            out string reason)
        {
            result = default;
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            SubsystemParticles particles = project.FindSubsystem<SubsystemParticles>(false);
            if (particles == null)
            {
                reason = "no-particles";
                return false;
            }
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null)
            {
                reason = "no-terrain";
                return false;
            }
            if (m_spawner == null)
            {
                reason = "no-spawner";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData local, out reason))
                return false;
            if (local.ComponentPlayer?.ComponentBody == null)
            {
                reason = "no-local-body";
                return false;
            }
            Vector3 listener = local.ComponentPlayer.ComponentBody.Position;
            BurnAwayPresentationPlan plan = BurnAwayPresentationPolicy.Plan(effect.Cells, listener);

            var performed = new HashSet<Point3>();
            int debris = 0;
            BurnAwayCellData[] debrisCells = plan.DebrisCells;
            for (int i = 0; i < debrisCells.Length; i++)
            {
                BurnAwayCellData cell = debrisCells[i];
                try
                {
                    particles.AddParticleSystem(
                        new BurntDebrisParticleSystem(terrain, cell.X, cell.Y, cell.Z));
                    debris++;
                    performed.Add(new Point3(cell.X, cell.Y, cell.Z));
                }
                catch (Exception e)
                {
                    m_particleFaults++;
                    LogProblem("particle-fault:" + e.GetType().Name,
                        "烧尽碎屑粒子创建失败（" + cell.Describe() + "）：" +
                        e.GetType().Name + " " + e.Message + " → 本格没有碎屑（明确计数）");
                }
            }

            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            int sounds = 0;
            int missingAudio = 0;
            BurnAwayCellData[] soundCells = plan.SoundCells;
            for (int i = 0; i < soundCells.Length; i++)
            {
                BurnAwayCellData cell = soundCells[i];
                if (audio == null)
                {
                    missingAudio++;
                    continue;
                }
                try
                {
                    // 与引擎完全一致：Audio/Sizzles，音量 1，音高 ±0.25，最小距离 3，自动延迟。
                    // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:248
                    audio.PlayRandomSound("Audio/Sizzles", 1f, m_random.Float(-0.25f, 0.25f),
                        new Vector3(cell.X, cell.Y, cell.Z), 3f, autoDelay: true);
                    sounds++;
                    performed.Add(new Point3(cell.X, cell.Y, cell.Z));
                }
                catch (Exception e)
                {
                    m_audioFaults++;
                    LogProblem("audio-fault:" + e.GetType().Name,
                        "烧尽嘶嘶声播放失败（" + cell.Describe() + "）：" +
                        e.GetType().Name + " " + e.Message + " → 本格没有声音（明确计数）");
                }
            }

            result = new BurnAwayPresentationResult(performed.Count, debris, sounds,
                plan.SkippedDistance, plan.SkippedCap, missingAudio);
            m_presentBatches++;
            m_presentCells += effect.Cells.Length;
            m_debris += debris;
            m_sounds += sounds;
            m_skippedDistance += plan.SkippedDistance;
            m_skippedCap += plan.SkippedCap;
            m_missingAudio += missingAudio;
            reason = null;
            return true;
        }

        // ------------------------------------------------------------ 诊断

        public string Describe() => "burnAwayEffect host batches=" + m_hostBatches +
            " cells=" + m_hostCells + " emitted=" + m_hostEmitted +
            " dropped=" + m_hostDropped + " emitFailed=" + m_emitFailures +
            " noListener=" + m_emitWithoutListener + " rejected=" + m_hostRejected +
            " · client batches=" + m_presentBatches + " cells=" + m_presentCells +
            " debris=" + m_debris + " sounds=" + m_sounds +
            " skippedDistance=" + m_skippedDistance + " skippedCap=" + m_skippedCap +
            " missingAudio=" + m_missingAudio + " particleFaults=" + m_particleFaults +
            " audioFaults=" + m_audioFaults + " resets=" + m_resets;

        private long CurrentTick()
        {
            SubsystemGameInfo info = m_project?.FindSubsystem<SubsystemGameInfo>(false);
            long tick = info == null ? 0L : (long)(info.TotalElapsedGameTime * 1000.0);
            if (tick < m_lastEmittedTick) tick = m_lastEmittedTick;
            m_lastEmittedTick = tick;
            return tick;
        }

        private void Log(string message) => m_diagnostics?.Info(m_ownerId, message);

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}

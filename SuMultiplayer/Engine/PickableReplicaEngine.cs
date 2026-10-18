using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 拾取物（Pickable）/ 投射物（Projectile）复制的**引擎适配层**
    /// （<see cref="IEntityReplicaPort"/> 的实现，组合根注入）。
    ///
    /// 主机：
    ///   · 每 32Hz 脉冲抓 <c>SubsystemPickables.Pickables</c> / <c>SubsystemProjectiles.Projectiles</c>，
    ///     按对象分配稳定的网络 id（对象消失即从映射里剪掉，模块据此发 REMOVE）；
    ///   · 执行客户端拾取请求：距离 / 创建宽限 / 背包容量都由本端引擎判断，
    ///     用引擎自己的 <c>ComponentInventoryBase.AcquireItems</c> 落地，并回传稀疏背包增量；
    ///   · 执行客户端丢弃 / 投掷 / 开火请求：校验玩家端 / 活跃槽 / 同种物品 / 距离，用引擎自己的
    ///     <c>SubsystemPickables.AddPickable</c> / <c>SubsystemProjectiles.FireProjectile</c> 生成权威实体，
    ///     消费道具 / 耐久后回传稀疏背包增量（与对应方块行为的 OnAim(Completed) 同形）；
    ///   · 把远端化身的“原生拾取”从引擎的拾取列表里摘掉：客户端拾取必须走请求链，
    ///     否则主机引擎会直接把掉落物塞进远端化身背包，客户端看不到背包变化、也没有回执。
    /// 客户端：
    ///   · 只创建 / 更新 / 移除引擎里的 Pickable / Projectile **副本**；
    ///   · 原生拾取由 <c>SuSubsystemPickables</c>（客户端清空玩家列表）抑制；
    ///   · 丢弃输入（Q / 手柄 B）由 <c>SuComponentInput</c> 在 ComponentPlayer.Update 之前拦下，
    ///     只发请求、**不本地扣物品**；投掷 / 开火由方块行为外壳只发请求、**不本地生成投射物**；
    ///   · 投射物**不跑** <c>SubsystemProjectiles.Update</c>（它会在客户端 DestroyCell / 伤人 /
    ///     生成掉落物 / 触发爆炸），位置由本适配层按主机快照插值；
    ///   · 扫描附近副本只产生 <see cref="IEntityReplicaPort.ClientPickupSubmitted"/>，不本地结算。
    ///
    /// 无静态状态：反射入口 / 化身表 / 计数器都挂在实例上。
    /// </summary>
    public sealed class PickableReplicaEngine : IEntityReplicaPort, IEntityDropInputSink
    {
        private const float RestingPositionEpsilonSquared = 0.0004f;
        private const float PickupAttractDistanceSquared = 3.0625f;   // 1.75m（引擎吸引范围）
        private const float PickupAcquireDistanceSquared = 1.0f;      // 1.0m（引擎直接拾取）
        private const double PickupScanIntervalSeconds = 0.05;        // 20Hz 扫描
        private const double PickupRequestIntervalSeconds = 0.75;     // 同一副本的重试间隔
        private const int ProblemLogMs = 5000;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly Func<ComponentPlayer, bool> m_remoteAvatarFilter;

        // ---- 客户端丢弃输入拦截（SuComponentInput 每帧调用）
        /// <summary>
        /// ComponentInput.m_playerInput 的 ref 入口（BindFieldRef 一次绑定、每帧无反射）：
        /// 采集到丢弃意图后把 Drop 标志清掉，引擎的 ComponentPlayer.Update 因此不会本地扣物品 +
        /// 本地 AddPickable（那会生成一个被"外来拾取物"清理掉的幽灵，物品直接消失）。
        /// </summary>
        private readonly ModFieldRef<ComponentInput, PlayerInput> m_inputRef;
        private IEntityRequestSink m_requestSink;
        private SuComponentInput m_dropInput;
        private bool m_dropInputInjectionMissingLogged;
        private long m_dropIntentSubmissions;
        private long m_dropIntentFailures;
        private long m_fireIntentSubmissions;
        private long m_fireIntentFailures;
        private long m_dropAccepted;
        private long m_dropRejected;
        private long m_fireAccepted;
        private long m_fireRejected;
        private readonly global::Engine.Random m_requestRandom = new global::Engine.Random();

        // ---- 引擎挂接
        private Project m_project;
        private SubsystemPickables m_pickables;
        private SubsystemProjectiles m_projectiles;
        private SuSubsystemPickables m_pickableShell;
        private bool m_clientReplica;

        // ---- 主机：对象 → 网络 id
        private struct HostKey
        {
            public int Id;
            public long Generation;
        }

        private readonly Dictionary<Pickable, HostKey> m_pickableKeys =
            new Dictionary<Pickable, HostKey>();
        private readonly Dictionary<int, Pickable> m_pickableById =
            new Dictionary<int, Pickable>();
        private readonly Dictionary<int, Vector3> m_publishedPickablePositions =
            new Dictionary<int, Vector3>();
        private readonly Dictionary<Projectile, HostKey> m_projectileKeys =
            new Dictionary<Projectile, HostKey>();
        private readonly Dictionary<int, Projectile> m_projectileById =
            new Dictionary<int, Projectile>();
        private long m_pickableGeneration;
        private long m_projectileGeneration;
        private int m_nextPickableId = 1;
        private int m_nextProjectileId = 1;

        // ---- 客户端：副本
        private sealed class ClientPickableReplica
        {
            public Pickable Pickable;
            public Vector3 TargetPosition;
            public Vector3 TargetVelocity;
            public bool TargetHasFlyTo;
            public Vector3 TargetFlyTo;
            public Vector3 PreviousTargetPosition;
            public bool HasPreviousTargetPosition;
            public Vector3 PresentationVelocity;
            public bool Initialized;
            public double LastUpdateTime;
        }

        private sealed class ClientProjectileReplica
        {
            public Projectile Projectile;
            public Vector3 TargetPosition;
            public Vector3 TargetVelocity;
            public Vector3 TargetRotation;
            public Vector3 TargetAngularVelocity;
            public Vector3 TargetTrailOffset;
            public bool TargetStuck;
            public bool TargetIncendiary;
            public bool Initialized;
            public double LastUpdateTime;
        }

        private readonly Dictionary<int, ClientPickableReplica> m_clientPickables =
            new Dictionary<int, ClientPickableReplica>();
        private readonly Dictionary<int, ClientProjectileReplica> m_clientProjectiles =
            new Dictionary<int, ClientProjectileReplica>();
        private readonly Dictionary<int, double> m_lastPickupRequestMs =
            new Dictionary<int, double>();
        private double m_nextPickupScanTime;

        // ---- 反射：SubsystemProjectiles 的私有列表（客户端删除副本时必须用）
        private List<Projectile> m_projectileList;
        private SubsystemProjectiles m_projectileListOwner;
        private List<Pickable> m_pickableList;
        private SubsystemPickables m_pickableListOwner;

        // ---- 计数
        private long m_hostCaptures;
        private long m_idAssignments;
        private long m_replicasCreated;
        private long m_replicasUpdated;
        private long m_replicasRemoved;
        private long m_applyFailures;
        private long m_acquireAccepted;
        private long m_acquireRejected;
        private long m_pickupCandidates;
        private long m_projectileListFailures;
        private long m_pickableListFailures;
        private long m_foreignProjectilesRemoved;
        private long m_foreignPickablesRemoved;
        private long m_remoteAvatarFilterCalls;

        public PickableReplicaEngine(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner;
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "entity-replication" : ownerId;
            m_remoteAvatarFilter = IsRemoteAvatar;
            // 丢弃拦截依赖 ComponentInput.m_playerInput：绑不上就是明确的装配失败（绝不静默）：
            // 没有这条路径，客户端按 Q 会走引擎原生掉落 → 本地扣除 + 本地拾取物被清理 → 物品消失。
            try
            {
                m_inputRef = m_parentField.BindFieldRef<ComponentInput, PlayerInput>("m_playerInput");
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "无法绑定 ComponentInput.m_playerInput：客户端丢弃请求无法在本地扣除之前拦截" +
                    "（引擎版本不匹配？）", e);
            }
        }

        public event Action<PickablePickupCandidate> ClientPickupSubmitted;

        public bool IsClientReplica => m_clientReplica;

        public bool IsReplicaReady => m_project != null && m_pickables != null &&
            m_projectiles != null;

        /// <summary>模块在 Attach 里挂上的请求入口（客户端丢弃 / 投掷 / 开火）。</summary>
        public IEntityRequestSink RequestSink
        {
            get => m_requestSink;
            set => m_requestSink = value;
        }

        public long ForeignPickablesRemoved => m_foreignPickablesRemoved;

        public long ForeignProjectilesRemoved => m_foreignProjectilesRemoved;

        // ================================================================ 挂接

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                ResetEngineState("project-change");
                m_project = project;
            }
            if (project == null) return;

            bool clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain
                shell && shell.ClientReplica;
            if (clientReplica != m_clientReplica)
            {
                ResetEngineState("role-change");
                m_clientReplica = clientReplica;
            }

            SubsystemPickables pickables = project.FindSubsystem<SubsystemPickables>(false);
            if (!ReferenceEquals(pickables, m_pickables))
            {
                m_pickables = pickables;
                m_pickableShell = pickables as SuSubsystemPickables;
                m_pickableList = null;
                m_pickableListOwner = null;
                m_pickableById.Clear();
                m_pickableKeys.Clear();
                m_publishedPickablePositions.Clear();
                Log("已挂接拾取物子系统：" + (pickables == null
                    ? "缺失（注入可能未生效）"
                    : pickables.GetType().Name));
                if (pickables != null && !(pickables is SuSubsystemPickables))
                {
                    LogProblemOnce("pickable-shell-missing",
                        (m_clientReplica ? "客户端" : "主机") +
                        "没有注入 SuSubsystemPickables（当前：" + pickables.GetType().Name +
                        "）→ " + (m_clientReplica
                            ? "引擎会本地拾取主机物品"
                            : "远端化身会被引擎原生拾取（绕过请求链）") +
                        "；请检查 Game.SubsystemPickables 注入");
                }
            }
            SubsystemProjectiles projectiles =
                project.FindSubsystem<SubsystemProjectiles>(false);
            if (!ReferenceEquals(projectiles, m_projectiles))
            {
                m_projectiles = projectiles;
                if (m_projectiles is SuSubsystemProjectiles projectileShell)
                    projectileShell.Replica = this;
                m_projectileList = null;
                m_projectileListOwner = null;
                m_projectileById.Clear();
                m_projectileKeys.Clear();
                Log("已挂接投射物子系统：" + (projectiles == null
                    ? "缺失（注入可能未生效）"
                    : projectiles.GetType().Name));
                if (projectiles != null && !(projectiles is SuSubsystemProjectiles))
                {
                    LogProblemOnce("projectile-shell-missing",
                        (m_clientReplica ? "客户端" : "主机") +
                        "没有注入 SuSubsystemProjectiles（当前：" +
                        projectiles.GetType().Name + "）→ " + (m_clientReplica
                            ? "引擎会在客户端跑投射物模拟（写地形 / 伤人 / 生成掉落物）"
                            : "主机投射物照常模拟") + "；请检查注入");
                }
            }
            if (m_projectiles is SuSubsystemProjectiles shellProjectiles)
                shellProjectiles.Replica = this;
            // 主机：远端化身不是引擎的“拾取者”。客户端由 SuSubsystemPickables 自己按
            // ReplicaGates.IsClient 清空玩家列表；这里只给主机装远端化身过滤器。
            if (m_pickableShell != null)
            {
                m_pickableShell.ParentField = m_parentField;
                m_pickableShell.Replica = this;
                m_pickableShell.IsRemotePlayer = m_clientReplica ? null : m_remoteAvatarFilter;
            }
            // 客户端：把丢弃输入钩子挂到本机玩家的 SuComponentInput 上（主机 / 单机不挂）。
            UpdateDropInput(project);
        }

        /// <summary>
        /// 客户端丢弃输入钩子的挂接：找到本机玩家的 <see cref="SuComponentInput"/> 并把本适配器
        /// 作为 sink 写进去（实例属性，**没有任何静态状态**）。主机会摘掉它（主机自己的丢弃
        /// 走原生路径，由引擎权威执行后被实体复制抓走）。
        /// </summary>
        private void UpdateDropInput(Project project)
        {
            if (!m_clientReplica)
            {
                DetachDropInput();
                return;
            }
            if (project == null || m_spawner == null)
            {
                DetachDropInput();
                return;
            }
            ComponentPlayer local = null;
            if (m_spawner.TryGetLocalPlayer(project, out PlayerData localData, out _))
                local = localData?.ComponentPlayer;
            ComponentInput input = local?.Entity?.FindComponent<ComponentInput>();
            if (input == null)
            {
                DetachDropInput();
                return;
            }
            if (!(input is SuComponentInput shell))
            {
                if (!m_dropInputInjectionMissingLogged)
                {
                    m_dropInputInjectionMissingLogged = true;
                    LogProblemOnce("drop-input-injection-missing",
                        "客户端没有注入 SuComponentInput（当前：" + input.GetType().Name +
                        "）→ 丢弃无法在本地扣除之前拦截，物品可能被引擎原生掉落 + 清理（请检查 " +
                        "Game.ComponentInput 注入）");
                }
                DetachDropInput();
                return;
            }
            if (!ReferenceEquals(shell, m_dropInput))
            {
                DetachDropInput();
                m_dropInput = shell;
            }
            shell.DropSink = this;
        }

        private void DetachDropInput()
        {
            if (m_dropInput == null) return;
            if (ReferenceEquals(m_dropInput.DropSink, this)) m_dropInput.DropSink = null;
            m_dropInput = null;
        }

        private void ResetEngineState(string reason)
        {
            DetachDropInput();
            RemoveAllClientReplicas(reason);
            m_pickableKeys.Clear();
            m_pickableById.Clear();
            m_publishedPickablePositions.Clear();
            m_projectileKeys.Clear();
            m_projectileById.Clear();
            m_pickableGeneration = 0;
            m_projectileGeneration = 0;
            m_nextPickableId = 1;
            m_nextProjectileId = 1;
            m_projectileList = null;
            m_projectileListOwner = null;
            m_pickableList = null;
            m_pickableListOwner = null;
            m_pickables = null;
            m_projectiles = null;
            m_pickableShell = null;
        }

        /// <summary>
        /// 主机过滤器：这个 ComponentPlayer 是不是某个远端玩家端的联机化身。
        /// true ⇒ 在 <c>SuSubsystemPickables</c> 的 base.Update 期间把它从玩家列表里摘掉，
        /// 于是主机不会替客户端“本地拾取”；客户端只能通过请求链拾取。
        /// </summary>
        private bool IsRemoteAvatar(ComponentPlayer player)
        {
            m_remoteAvatarFilterCalls++;
            if (player?.PlayerData == null || m_spawner == null) return false;
            foreach (int clientId in m_spawner.ClientIds)
            {
                if (!m_spawner.TryGet(clientId, out PlayerData data) || data == null) continue;
                if (ReferenceEquals(data, player.PlayerData)) return true;
            }
            return false;
        }

        // ================================================================ 主机：抓取

        public bool TryCaptureHostPickables(List<PickableStateData> target, out string reason)
        {
            target.Clear();
            if (m_clientReplica)
            {
                reason = "client-replica";
                return false;
            }
            if (m_pickables == null)
            {
                reason = "no-pickables-subsystem";
                return false;
            }
            m_hostCaptures++;
            m_pickableGeneration++;
            ReadOnlyList<Pickable> list = m_pickables.Pickables;
            for (int i = 0; i < list.Count; i++)
            {
                Pickable pickable = list[i];
                if (pickable == null || pickable.ToRemove) continue;
                int id = EnsurePickableId(pickable);
                Vector3 velocity = pickable.Velocity;
                // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:203-215
                // 引擎静止的拾取物 velocity 仍有 ±0.1~0.5 m/s 残余；位置不变时按 0 发布，
                // 否则客户端外推会“往地下沉、再被拉回”。
                if (!pickable.FlyToPosition.HasValue &&
                    m_publishedPickablePositions.TryGetValue(id, out Vector3 previous) &&
                    Vector3.DistanceSquared(previous, pickable.Position) <
                        RestingPositionEpsilonSquared)
                    velocity = Vector3.Zero;
                m_publishedPickablePositions[id] = pickable.Position;
                bool hasStuck = pickable.StuckMatrix.HasValue;
                Vector3 stuckPosition = Vector3.Zero;
                Quaternion stuckRotation = Quaternion.Identity;
                if (hasStuck)
                {
                    Matrix matrix = pickable.StuckMatrix.Value;
                    stuckPosition = matrix.Translation;
                    stuckRotation = Quaternion.CreateFromRotationMatrix(matrix);
                }
                target.Add(new PickableStateData(id, EntityActions.Add, pickable.Value,
                    pickable.Count, pickable.Position, velocity, hasStuck, stuckPosition,
                    stuckRotation, pickable.FlyToPosition.HasValue,
                    pickable.FlyToPosition ?? Vector3.Zero, -1));
            }
            PrunePickableKeys();
            reason = null;
            return true;
        }

        public bool TryCaptureHostProjectiles(List<ProjectileStateData> target, out string reason)
        {
            target.Clear();
            if (m_clientReplica)
            {
                reason = "client-replica";
                return false;
            }
            if (m_projectiles == null)
            {
                reason = "no-projectiles-subsystem";
                return false;
            }
            m_hostCaptures++;
            m_projectileGeneration++;
            ReadOnlyList<Projectile> list = m_projectiles.Projectiles;
            for (int i = 0; i < list.Count; i++)
            {
                Projectile projectile = list[i];
                if (projectile == null || projectile.ToRemove) continue;
                int id = EnsureProjectileId(projectile);
                int ownerClientId = ResolveOwnerClientId(projectile);
                bool stuck = projectile.Velocity.LengthSquared() < 0.25f;
                target.Add(new ProjectileStateData(id, EntityActions.Add, projectile.Value,
                    projectile.Position, projectile.Velocity, projectile.Rotation,
                    projectile.AngularVelocity, ownerClientId, stuck, projectile.IsIncendiary,
                    projectile.TrailOffset));
            }
            PruneProjectileKeys();
            reason = null;
            return true;
        }

        private int EnsurePickableId(Pickable pickable)
        {
            if (m_pickableKeys.TryGetValue(pickable, out HostKey entry))
            {
                entry.Generation = m_pickableGeneration;
                m_pickableKeys[pickable] = entry;
                return entry.Id;
            }
            int id = NextId(ref m_nextPickableId, m_pickableById);
            m_pickableKeys[pickable] = new HostKey { Id = id, Generation = m_pickableGeneration };
            m_pickableById[id] = pickable;
            m_idAssignments++;
            return id;
        }

        private int EnsureProjectileId(Projectile projectile)
        {
            if (m_projectileKeys.TryGetValue(projectile, out HostKey entry))
            {
                entry.Generation = m_projectileGeneration;
                m_projectileKeys[projectile] = entry;
                return entry.Id;
            }
            int id = NextId(ref m_nextProjectileId, m_projectileById);
            m_projectileKeys[projectile] = new HostKey
            {
                Id = id,
                Generation = m_projectileGeneration
            };
            m_projectileById[id] = projectile;
            m_idAssignments++;
            return id;
        }

        private static int NextId<T>(ref int next, Dictionary<int, T> used)
        {
            int id = next;
            if (id <= 0 || id >= int.MaxValue) id = 1;
            while (id <= 0 || id > EntityReplicationLimits.MaxNetworkId ||
                used.ContainsKey(id))
            {
                id++;
                if (id > EntityReplicationLimits.MaxNetworkId) id = 1;
            }
            next = id + 1;
            return id;
        }

        private void PrunePickableKeys()
        {
            List<Pickable> stale = null;
            foreach (KeyValuePair<Pickable, HostKey> item in m_pickableKeys)
            {
                if (item.Value.Generation == m_pickableGeneration) continue;
                if (stale == null) stale = new List<Pickable>();
                stale.Add(item.Key);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
            {
                if (!m_pickableKeys.TryGetValue(stale[i], out HostKey entry)) continue;
                m_pickableKeys.Remove(stale[i]);
                m_pickableById.Remove(entry.Id);
                m_publishedPickablePositions.Remove(entry.Id);
            }
        }

        private void PruneProjectileKeys()
        {
            List<Projectile> stale = null;
            foreach (KeyValuePair<Projectile, HostKey> item in m_projectileKeys)
            {
                if (item.Value.Generation == m_projectileGeneration) continue;
                if (stale == null) stale = new List<Projectile>();
                stale.Add(item.Key);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
            {
                if (!m_projectileKeys.TryGetValue(stale[i], out HostKey entry)) continue;
                m_projectileKeys.Remove(stale[i]);
                m_projectileById.Remove(entry.Id);
            }
        }

        private int ResolveOwnerClientId(Projectile projectile)
        {
            ComponentPlayer owner = projectile?.Owner?.Entity?.FindComponent<ComponentPlayer>();
            if (owner == null || owner.PlayerData == null || m_spawner == null ||
                m_project == null)
                return -1;
            foreach (int clientId in m_spawner.ClientIds)
            {
                if (m_spawner.TryGet(clientId, out PlayerData data) &&
                    ReferenceEquals(data, owner.PlayerData))
                    return clientId;
            }
            if (m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _) &&
                ReferenceEquals(local, owner.PlayerData))
                return 0;
            return -1;
        }

        // ================================================================ 主机：执行拾取

        public bool TryAcquire(int clientId, in PickableAcquireRequestData request,
            out PickableAcquireOutcome outcome, out string reason)
        {
            try
            {
                bool accepted = TryAcquireCore(clientId, in request, out outcome, out reason);
                if (!accepted) m_acquireRejected++;
                return accepted;
            }
            catch (Exception e)
            {
                // 引擎的背包 API 抛异常也必须变成**明确拒绝**（回执带原因），
                // 不能让模块 tick 异常中断、客户端干等超时。
                outcome = default;
                reason = "host-exception-" + e.GetType().Name;
                m_acquireRejected++;
                LogRateLimited("acquire-exception:" + e.GetType().Name,
                    "执行拾取请求时引擎异常（clientId=" + clientId + " pickable=" +
                    request.PickableId + "）：" + e.GetType().Name + " " + e.Message +
                    " → 明确拒绝（不静默成功）");
                return false;
            }
        }

        private bool TryAcquireCore(int clientId, in PickableAcquireRequestData request,
            out PickableAcquireOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (clientId <= 0 || m_spawner == null)
            {
                reason = "not-player-peer";
                return false;
            }
            if (!m_pickableById.TryGetValue(request.PickableId, out Pickable pickable) ||
                pickable == null || pickable.ToRemove)
            {
                reason = "unknown-pickable";
                return false;
            }
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer == null)
            {
                reason = "no-avatar";
                return false;
            }
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentHealth health = player.ComponentHealth;
            if (health == null)
            {
                reason = "no-health";
                return false;
            }
            if (health.Health <= 0f)
            {
                reason = "dead";
                return false;
            }
            ComponentBody body = player.ComponentBody;
            if (body == null)
            {
                reason = "no-body";
                return false;
            }
            SubsystemGameInfo gameInfo = m_project.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo == null)
            {
                reason = "no-game-info";
                return false;
            }
            // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
            // 创建后 0.5s 内不允许拾取（与引擎同一条宽限）。
            if (gameInfo.TotalElapsedGameTime - pickable.CreationTime <= 0.5)
            {
                reason = "creation-grace";
                return false;
            }
            Vector3 eye = body.Position + new Vector3(0f, 0.75f, 0f);
            // 1.5m：客户端在 1.0m 内才发请求，但两端插值 + 一个 RTT 的移动量可能超过 1.1m；
            // 仍然只按**主机自己的**化身位置判断，客户端请求里的 position 不作数。
            // Source: Mod/ScMultiplayer/Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs:263-300
            //         （旧实现允许 request.Position 与化身相差 2m，但 pickable 与 request 只差 1.1m）
            if (Vector3.DistanceSquared(eye, pickable.Position) > 2.25f)
            {
                reason = "distance";
                return false;
            }
            int contents = Game.Terrain.ExtractContents(pickable.Value);
            if (contents <= 0 || BlocksManager.Blocks == null ||
                contents >= BlocksManager.Blocks.Length)
            {
                reason = "item-illegal";
                return false;
            }
            if (contents == 248)
            {
                // 经验币：引擎口径是 AddExperience(count)，不进背包。
                // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
                if (player.ComponentLevel == null)
                {
                    reason = "no-level";
                    return false;
                }
                player.ComponentLevel.AddExperience(pickable.Count, playSound: true);
                pickable.Count = 0;
                m_acquireAccepted++;
                bool removedCoin = RemoveHostPickable(request.PickableId, pickable);
                outcome = new PickableAcquireOutcome(0, removedCoin, Array.Empty<int>(),
                    Array.Empty<int>(), Array.Empty<int>(), CurrentInventoryVersion());
                reason = null;
                return true;
            }
            ComponentMiner miner = player.ComponentMiner;
            IInventory inventory = miner?.Inventory;
            if (inventory == null)
            {
                reason = "no-inventory";
                return false;
            }
            int acquireSlot = ComponentInventoryBase.FindAcquireSlotForItem(inventory,
                pickable.Value);
            if (acquireSlot < 0)
            {
                reason = "inventory-full";
                return false;
            }
            int slots = inventory.SlotsCount;
            var beforeValues = new int[slots];
            var beforeCounts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                beforeValues[i] = inventory.GetSlotValue(i);
                beforeCounts[i] = inventory.GetSlotCount(i);
            }
            int previousCount = pickable.Count;
            // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.AcquireItems
            pickable.Count = ComponentInventoryBase.AcquireItems(inventory, pickable.Value,
                pickable.Count);
            if (pickable.Count >= previousCount)
            {
                reason = "acquire-refused";
                return false;
            }
            var afterValues = new int[slots];
            var afterCounts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                afterValues[i] = inventory.GetSlotValue(i);
                afterCounts[i] = inventory.GetSlotCount(i);
            }
            BuildInventoryDelta(beforeValues, beforeCounts, afterValues, afterCounts,
                acquireSlot, out int[] indices, out int[] values, out int[] counts);
            bool removed = pickable.Count == 0;
            if (removed) RemoveHostPickable(request.PickableId, pickable);
            m_acquireAccepted++;
            outcome = new PickableAcquireOutcome(pickable.Count, removed, indices, values, counts,
                CurrentInventoryVersion());
            reason = null;
            return true;
        }

        private bool RemoveHostPickable(int id, Pickable pickable)
        {
            if (pickable != null) pickable.ToRemove = true;
            m_pickableById.Remove(id);
            if (pickable != null) m_pickableKeys.Remove(pickable);
            m_publishedPickablePositions.Remove(id);
            return pickable != null;
        }

        private int m_inventoryVersion;

        private int CurrentInventoryVersion() => ++m_inventoryVersion;

        /// <summary>
        /// 稀疏背包增量：只把真的变了的格子发回拾取者（与旧实现 TryBuildInventoryDelta 同口径）。
        /// 增量算不出来（同值格被写成同值，例如创造模式无限仓库）时，至少回“该物品所在格”的权威值。
        /// Source: Mod/ScMultiplayer/Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs:345-395
        /// </summary>
        private static void BuildInventoryDelta(int[] beforeValues, int[] beforeCounts,
            int[] afterValues, int[] afterCounts, int fallbackSlot, out int[] indices,
            out int[] values, out int[] counts)
        {
            var changedIndices = new List<int>();
            var changedValues = new List<int>();
            var changedCounts = new List<int>();
            int slots = Math.Min(beforeValues.Length,
                Math.Min(beforeCounts.Length, Math.Min(afterValues.Length, afterCounts.Length)));
            for (int i = 0; i < slots; i++)
            {
                if (beforeValues[i] == afterValues[i] && beforeCounts[i] == afterCounts[i])
                    continue;
                changedIndices.Add(i);
                changedValues.Add(afterValues[i]);
                changedCounts.Add(afterCounts[i]);
            }
            if (changedIndices.Count == 0 && fallbackSlot >= 0 && fallbackSlot < slots)
            {
                changedIndices.Add(fallbackSlot);
                changedValues.Add(afterValues[fallbackSlot]);
                changedCounts.Add(afterCounts[fallbackSlot]);
            }
            indices = changedIndices.ToArray();
            values = changedValues.ToArray();
            counts = changedCounts.ToArray();
        }

        // ================================================================ 客户端：丢弃 / 投掷 / 开火意图

        /// <summary>
        /// 客户端丢弃输入（由 <see cref="SuComponentInput"/> 在 base.Update 之后调用）。
        ///
        /// 关键口径：**本地绝不扣除**。这里只采集意图交给模块发 PickableDropRequest，然后把
        /// 引擎这一帧的 <c>PlayerInput.Drop</c> 清掉，让 <c>ComponentPlayer.Update</c> 的
        /// 原生掉落分支（RemoveSlotItems + AddPickable）不执行 —— 否则客户端会扣掉物品并生成
        /// 一个被"外来拾取物"清理掉的幽灵，表现为"按 Q 东西直接消失"。
        ///
        /// 发送失败也照样吞掉原生掉落：宁可这一下没丢出去（有明确日志 + 计数），也不能丢物品。
        /// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        /// </summary>
        bool IEntityDropInputSink.TryAcceptDrop(ComponentInput input, ComponentPlayer player)
        {
            if (!m_clientReplica || input == null || player == null || m_project == null)
                return false;
            // 只处理**本机玩家**：远端化身的 Updateable 在联机角色创建时已被摘掉，这里仍显式校验。
            if (m_spawner == null ||
                !m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _) ||
                !ReferenceEquals(local?.ComponentPlayer, player))
                return false;
            ComponentMiner miner = player.ComponentMiner;
            IInventory inventory = miner?.Inventory;
            if (inventory == null) return false;
            int slot = inventory.ActiveSlotIndex;
            if (slot < 0 || slot >= inventory.SlotsCount) return false;
            int value = inventory.GetSlotValue(slot);
            int count = inventory.GetSlotCount(slot);
            if (value == 0 || count <= 0) return false;   // 空手：原生分支本来也不会掉东西
            ComponentBody body = player.ComponentBody;
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (body == null || model == null) return false;

            // 与引擎 Drop 分支完全相同的落点与速度（方向 = 眼睛朝向）。
            Vector3 position = body.Position +
                new Vector3(0f, body.StanceBoxSize.Y * 0.66f, 0f) + 0.25f * body.Matrix.Forward;
            Vector3 direction = Matrix.CreateFromQuaternion(model.EyeRotation).Forward;
            var intent = new PickableDropIntent(slot, value, count, position, direction);

            IEntityRequestSink sink = m_requestSink;
            bool submitted;
            string reason;
            if (sink == null)
            {
                submitted = false;
                reason = "no-request-sink";
            }
            else
            {
                try
                {
                    submitted = sink.TrySubmitDrop(in intent, out reason);
                }
                catch (Exception e)
                {
                    submitted = false;
                    reason = "submit-exception-" + e.GetType().Name;
                }
            }
            bool accept = submitted;
            if (accept)
            {
                m_dropIntentSubmissions++;
            }
            else
            {
                m_dropIntentFailures++;
                LogProblemOnce("drop-submit:" + reason,
                    "客户端丢弃意图没有发出去（" + reason + "）→ 本机**不扣物品**、也不本地生成拾取物" +
                    "（物品仍在背包里；可重试）");
            }
            // 无论发没发出去都吞掉引擎原生掉落：绝不本地扣除。
            m_inputRef(input).Drop = false;
            return accept;
        }

        /// <summary>
        /// 客户端投掷 / 开火入口（由方块行为外壳调用）。
        /// 成功 = 请求已入队发送；客户端不会本地生成投射物，也不扣道具 / 耐久。
        /// </summary>
        public bool SubmitClientProjectileFire(in ProjectileFireIntent intent, out string reason)
        {
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            IEntityRequestSink sink = m_requestSink;
            bool submitted;
            if (sink == null)
            {
                submitted = false;
                reason = "no-request-sink";
            }
            else
            {
                try
                {
                    submitted = sink.TrySubmitProjectileFire(in intent, out reason);
                }
                catch (Exception e)
                {
                    submitted = false;
                    reason = "submit-exception-" + e.GetType().Name;
                }
            }
            if (submitted)
            {
                m_fireIntentSubmissions++;
                return true;
            }
            m_fireIntentFailures++;
            LogProblemOnce("fire-submit:" + reason,
                "客户端开火意图没有发出去 " + intent.Describe() + "（" + reason +
                "）→ 本机**不生成投射物**、也不扣道具 / 耐久");
            return false;
        }

        // ================================================================ 主机：执行丢弃请求

        public bool TryDrop(int clientId, in PickableDropRequestData request,
            out PickableDropOutcome outcome, out string reason)
        {
            try
            {
                bool accepted = TryDropCore(clientId, in request, out outcome, out reason);
                if (accepted) m_dropAccepted++;
                else m_dropRejected++;
                return accepted;
            }
            catch (Exception e)
            {
                outcome = default;
                reason = "host-exception-" + e.GetType().Name;
                m_dropRejected++;
                LogRateLimited("drop-exception:" + e.GetType().Name,
                    "执行丢弃请求时引擎异常（clientId=" + clientId + " " + request.Describe() +
                    "）：" + e.GetType().Name + " " + e.Message + " → 明确拒绝（不静默成功）");
                return false;
            }
        }

        private bool TryDropCore(int clientId, in PickableDropRequestData request,
            out PickableDropOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (m_pickables == null)
            {
                reason = "no-pickables";
                return false;
            }
            if (!TryResolveHostActor(m_project, clientId, out ComponentPlayer player, out _,
                    out reason))
                return false;
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory == null)
            {
                reason = "no-inventory";
                return false;
            }
            int slot = request.ActiveSlot;
            if (slot < 0 || slot >= inventory.SlotsCount)
            {
                reason = "slot-range";
                return false;
            }
            int value = inventory.GetSlotValue(slot);
            int count = inventory.GetSlotCount(slot);
            if (value == 0 || count <= 0)
            {
                reason = "empty-slot";
                return false;
            }
            // 槽内容必须是**同一种物品**（contents 完全一致）。data 位（拉弓进度 / 击锤 /
            // 装填状态 / 家具款式）在客户端本地会随操作每 100ms 变化，而主机镜像要等一次上传；
            // 要求整值完全相等会把合法丢弃判成 slot-mismatch。执行时一律使用**主机权威值**，
            // 客户端上报的 data 只作诊断（不一致时限流记一行，不静默）。
            if (value == 0 || request.ItemValue == 0 ||
                global::Game.Terrain.ExtractContents(value) !=
                global::Game.Terrain.ExtractContents(request.ItemValue))
            {
                reason = "slot-mismatch";
                return false;
            }
            if (value != request.ItemValue)
            {
                LogRateLimited("drop-data-mismatch:" + clientId,
                    "clientId=" + clientId + " 丢弃请求的物品 data 与主机镜像不同（请求 " +
                    request.ItemValue + " / 权威 " + value + "）：按主机权威值丢弃" +
                    "（draw/击锤等本地状态位允许一个 RTT 的滞后）");
            }
            Block block = ResolveBlock(value);
            if (block == null)
            {
                reason = "item-illegal";
                return false;
            }
            ComponentBody body = player.ComponentBody;
            if (Vector3.DistanceSquared(body.Position, request.Position) > 36f)
            {
                reason = "distance";
                return false;
            }
            Vector3 direction = Vector3.Normalize(request.Direction);
            CaptureInventorySlots(inventory, out int[] beforeValues, out int[] beforeCounts);
            int removed = inventory.RemoveSlotItems(slot, count);
            if (removed <= 0)
            {
                reason = "remove-refused";
                return false;
            }
            // 权威落点 / 速度与引擎原生 Drop 分支同形（位置用主机化身，不用客户端上报值）。
            Vector3 position = body.Position +
                new Vector3(0f, body.StanceBoxSize.Y * 0.66f, 0f) + 0.25f * body.Matrix.Forward;
            Vector3 velocity = 8f * direction;
            Pickable pickable;
            try
            {
                pickable = m_pickables.AddPickable(value, removed, position, velocity, null);
            }
            catch (Exception e)
            {
                pickable = null;
                LogRateLimited("drop-add-exception:" + e.GetType().Name,
                    "主机 AddPickable 抛异常（clientId=" + clientId + " value=" + value +
                    " count=" + removed + "）：" + e.GetType().Name + " " + e.Message);
            }
            if (pickable == null)
            {
                // 生成失败 → 把物品放回权威背包（绝不静默吞物品）。
                int leftover = ComponentInventoryBase.AcquireItems(inventory, value, removed);
                if (leftover > 0)
                {
                    LogRateLimited("drop-rollback-incomplete:" + clientId,
                        "丢弃生成失败后回滚背包不完整（clientId=" + clientId + " value=" + value +
                        " 未放回 " + leftover + "/" + removed + " 个）→ 物品可能丢失，请人工检查背包");
                }
                reason = "spawn-failed";
                return false;
            }
            CaptureInventorySlots(inventory, out int[] afterValues, out int[] afterCounts);
            BuildInventoryDelta(beforeValues, beforeCounts, afterValues, afterCounts, slot,
                out int[] indices, out int[] values, out int[] counts);
            outcome = new PickableDropOutcome(value, removed, pickable.Position, pickable.Velocity,
                indices, values, counts, CurrentInventoryVersion());
            return true;
        }

        // ================================================================ 主机：执行投掷 / 开火请求

        public bool TryFireProjectile(int clientId, in ProjectileFireRequestData request,
            out ProjectileFireOutcome outcome, out string reason)
        {
            try
            {
                bool accepted = TryFireProjectileCore(clientId, in request, out outcome,
                    out reason);
                if (accepted) m_fireAccepted++;
                else m_fireRejected++;
                return accepted;
            }
            catch (Exception e)
            {
                outcome = default;
                reason = "host-exception-" + e.GetType().Name;
                m_fireRejected++;
                LogRateLimited("fire-exception:" + e.GetType().Name,
                    "执行开火请求时引擎异常（clientId=" + clientId + " " + request.Describe() +
                    "）：" + e.GetType().Name + " " + e.Message + " → 明确拒绝（不静默成功）");
                return false;
            }
        }

        private bool TryFireProjectileCore(int clientId, in ProjectileFireRequestData request,
            out ProjectileFireOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (m_projectiles == null)
            {
                reason = "no-projectiles";
                return false;
            }
            if (!TryResolveHostActor(m_project, clientId, out ComponentPlayer player, out _,
                    out reason))
                return false;
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory == null)
            {
                reason = "no-inventory";
                return false;
            }
            int slot = request.ActiveSlot;
            if (slot < 0 || slot >= inventory.SlotsCount)
            {
                reason = "slot-range";
                return false;
            }
            // 引擎只在活跃槽上消费 / 扣耐久，所以请求槽必须与主机权威活跃槽一致。
            if (inventory.ActiveSlotIndex != slot)
            {
                reason = "active-slot-changed";
                return false;
            }
            if (inventory.GetSlotCount(slot) <= 0)
            {
                reason = "empty-slot";
                return false;
            }
            int itemValue = inventory.GetSlotValue(slot);
            // 同 TryDropCore：只要求同一种物品（contents 一致），data 位允许本地状态滞后；
            // 执行用的 item value 一律取主机权威值（客户端上报值只作诊断）。
            if (itemValue == 0 || request.ItemValue == 0 ||
                global::Game.Terrain.ExtractContents(itemValue) !=
                global::Game.Terrain.ExtractContents(request.ItemValue))
            {
                reason = "slot-mismatch";
                return false;
            }
            if (itemValue != request.ItemValue)
            {
                LogRateLimited("fire-data-mismatch:" + clientId,
                    "clientId=" + clientId + " 开火请求的物品 data 与主机镜像不同（请求 " +
                    request.ItemValue + " / 权威 " + itemValue + "）：按主机权威值执行");
            }
            ComponentBody body = player.ComponentBody;
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model == null)
            {
                reason = "no-model";
                return false;
            }
            if (Vector3.DistanceSquared(model.EyePosition, request.Origin) > 36f)
            {
                reason = "distance";
                return false;
            }
            // 编解码已经归一化并校验长度；这里再取一次，避免任何空向量的浮点噪声。
            Vector3 direction = Vector3.Normalize(request.Direction);
            Block block = ResolveBlock(itemValue);
            if (block == null)
            {
                reason = "item-illegal";
                return false;
            }
            CaptureInventorySlots(inventory, out int[] beforeValues, out int[] beforeCounts);
            int spawned;
            Vector3 firstPosition;
            Vector3 firstVelocity;
            switch (request.Kind)
            {
                case ProjectileFireKinds.Throwable:
                    if (!TryFireThrowable(player, inventory, slot, itemValue, block,
                            direction, out spawned, out firstPosition, out firstVelocity,
                            out reason))
                        return false;
                    break;
                case ProjectileFireKinds.Bow:
                    if (!TryFireBow(player, inventory, slot, itemValue, block, direction,
                            out spawned, out firstPosition, out firstVelocity, out reason))
                        return false;
                    break;
                case ProjectileFireKinds.Crossbow:
                    if (!TryFireCrossbow(player, inventory, slot, itemValue, block,
                            direction, out spawned, out firstPosition, out firstVelocity,
                            out reason))
                        return false;
                    break;
                case ProjectileFireKinds.Musket:
                    if (!TryFireMusket(player, inventory, slot, itemValue, block,
                            direction, out spawned, out firstPosition, out firstVelocity,
                            out reason))
                        return false;
                    break;
                default:
                    reason = "kind-unknown";
                    return false;
            }
            CaptureInventorySlots(inventory, out int[] afterValues, out int[] afterCounts);
            BuildInventoryDelta(beforeValues, beforeCounts, afterValues, afterCounts, slot,
                out int[] indices, out int[] values, out int[] counts);
            outcome = new ProjectileFireOutcome(request.Kind, spawned, itemValue,
                firstPosition, firstVelocity, indices, values, counts, CurrentInventoryVersion());
            return true;
        }

        /// <summary>
        /// 投掷：与 <c>SubsystemThrowableBlockBehavior.OnAim(Completed)</c> 同形。
        /// 先 FireProjectile（null = 被地形挡住 → 明确拒绝、不扣道具），成功后扣 1 个。
        /// </summary>
        private bool TryFireThrowable(ComponentPlayer player, IInventory inventory, int slot,
            int itemValue, Block block, Vector3 direction, out int spawned,
            out Vector3 firstPosition, out Vector3 firstVelocity, out string reason)
        {
            spawned = 0;
            firstPosition = Vector3.Zero;
            firstVelocity = Vector3.Zero;
            reason = null;
            // 爆炸物必须走 ExplosionRequest（否则同一个投掷物会既生成普通投射物又引爆）。
            if (block is BombBlock || block is IncendiaryBombBlock || block is FireworksBlock)
            {
                reason = "use-explosion-request";
                return false;
            }
            if (!block.IsAimable || block.ProjectileSpeed <= 0f)
            {
                reason = "item-not-throwable";
                return false;
            }
            ComponentBody body = player.ComponentBody;
            ComponentCreatureModel model = player.ComponentCreatureModel;
            Vector3 origin = model.EyePosition + body.Matrix.Right * 0.4f;
            float speed = block.ProjectileSpeed;
            if (player.ComponentLevel != null)
                speed *= 0.5f * (player.ComponentLevel.StrengthFactor - 1f) + 1f;
            Vector3 velocity = body.Velocity + direction * speed;
            Projectile projectile = m_projectiles.FireProjectile(itemValue, origin, velocity,
                m_requestRandom.Vector3(5f, 10f), player);
            if (projectile == null)
            {
                reason = "projectile-blocked";
                return false;
            }
            int removed = inventory.RemoveSlotItems(slot, 1);
            if (removed <= 0)
            {
                // 理论上不可能（前面校验过 count>0）：把刚生成的投射物撤掉，绝不白送。
                projectile.ToRemove = true;
                reason = "consume-refused";
                return false;
            }
            spawned = 1;
            firstPosition = projectile.Position;
            firstVelocity = projectile.Velocity;
            return true;
        }

        /// <summary>
        /// 弓：与 <c>SubsystemBowBlockBehavior.OnAim(Completed)</c> 同形。
        /// 有箭才生成箭投射物；**无论是否射出**都重置 draw=0 并扣耐久
        /// （射出的箭类型同时从弓的 data 里清掉，与引擎一致）。
        /// </summary>
        private bool TryFireBow(ComponentPlayer player, IInventory inventory, int slot,
            int itemValue, Block block, Vector3 direction, out int spawned,
            out Vector3 firstPosition, out Vector3 firstVelocity, out string reason)
        {
            spawned = 0;
            firstPosition = Vector3.Zero;
            firstVelocity = Vector3.Zero;
            reason = null;
            if (!(block is BowBlock))
            {
                reason = "item-not-bow";
                return false;
            }
            int data = global::Game.Terrain.ExtractData(itemValue);
            int draw = MathUtils.Clamp(BowBlock.GetDraw(data), 0, 15);
            ArrowBlock.ArrowType? arrowType = BowBlock.GetArrowType(data);
            Projectile spawnedProjectile = null;
            if (arrowType.HasValue)
            {
                ComponentBody body = player.ComponentBody;
                ComponentCreatureModel model = player.ComponentCreatureModel;
                Vector3 origin = model.EyePosition + body.Matrix.Right * 0.3f -
                    body.Matrix.Up * 0.2f;
                Vector3 spread = Vector3.Zero;
                if (arrowType == ArrowBlock.ArrowType.WoodenArrow)
                    spread = new Vector3(0.025f, 0.025f, 0.025f);
                else if (arrowType == ArrowBlock.ArrowType.StoneArrow)
                    spread = new Vector3(0.01f, 0.01f, 0.01f);
                float speed = MathUtils.Lerp(0f, 28f, MathUtils.Pow((float)draw / 15f, 0.75f));
                if (player.ComponentLevel != null)
                    speed *= 0.5f * (player.ComponentLevel.StrengthFactor - 1f) + 1f;
                Vector3 right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
                Vector3 up = Vector3.Normalize(Vector3.Cross(direction, right));
                Vector3 noise = m_requestRandom.Float(-spread.X, spread.X) * right +
                    m_requestRandom.Float(-spread.Y, spread.Y) * up +
                    m_requestRandom.Float(-spread.Z, spread.Z) * direction;
                Vector3 velocity = body.Velocity + (direction + noise) * speed;
                int arrowValue = global::Game.Terrain.MakeBlockValue(ArrowBlock.Index, 0,
                    ArrowBlock.SetArrowType(0, arrowType.Value));
                spawnedProjectile = m_projectiles.FireProjectile(arrowValue, origin, velocity,
                    Vector3.Zero, player);
                if (spawnedProjectile != null)
                {
                    spawned = 1;
                    firstPosition = spawnedProjectile.Position;
                    firstVelocity = spawnedProjectile.Velocity;
                    data = BowBlock.SetArrowType(data, null);
                }
            }
            int damage = draw >= 15 ? 2 : (draw >= 4 ? 1 : 0);
            int resetValue = global::Game.Terrain.MakeBlockValue(BowBlock.Index, 0, BowBlock.SetDraw(data, 0));
            if (!TryReplaceHeldStack(inventory, slot, resetValue, out reason))
            {
                if (spawnedProjectile != null) spawnedProjectile.ToRemove = true;
                spawned = 0;
                return false;
            }
            player.ComponentMiner?.DamageActiveTool(damage);
            return true;
        }

        /// <summary>
        /// 弩：与 <c>SubsystemCrossbowBlockBehavior.OnAim(Completed)</c> 同形。
        /// 只有 draw==15 且装了弩箭才生成投射物；无论是否射出都重置 draw 并扣 1 点耐久（draw&gt;0）。
        /// </summary>
        private bool TryFireCrossbow(ComponentPlayer player, IInventory inventory, int slot,
            int itemValue, Block block, Vector3 direction, out int spawned,
            out Vector3 firstPosition, out Vector3 firstVelocity, out string reason)
        {
            spawned = 0;
            firstPosition = Vector3.Zero;
            firstVelocity = Vector3.Zero;
            reason = null;
            if (!(block is CrossbowBlock))
            {
                reason = "item-not-crossbow";
                return false;
            }
            int data = global::Game.Terrain.ExtractData(itemValue);
            int draw = CrossbowBlock.GetDraw(data);
            ArrowBlock.ArrowType? arrowType = CrossbowBlock.GetArrowType(data);
            Projectile spawnedProjectile = null;
            if (draw == 15 && arrowType.HasValue)
            {
                ComponentBody body = player.ComponentBody;
                ComponentCreatureModel model = player.ComponentCreatureModel;
                Vector3 origin = model.EyePosition + body.Matrix.Right * 0.3f -
                    body.Matrix.Up * 0.2f;
                Vector3 velocity = body.Velocity + 38f * direction;
                int boltValue = global::Game.Terrain.MakeBlockValue(ArrowBlock.Index, 0,
                    ArrowBlock.SetArrowType(0, arrowType.Value));
                spawnedProjectile = m_projectiles.FireProjectile(boltValue, origin, velocity,
                    Vector3.Zero, player);
                if (spawnedProjectile != null)
                {
                    spawned = 1;
                    firstPosition = spawnedProjectile.Position;
                    firstVelocity = spawnedProjectile.Velocity;
                    data = CrossbowBlock.SetArrowType(data, null);
                }
            }
            int resetValue = global::Game.Terrain.MakeBlockValue(CrossbowBlock.Index, 0,
                CrossbowBlock.SetDraw(data, 0));
            if (!TryReplaceHeldStack(inventory, slot, resetValue, out reason))
            {
                if (spawnedProjectile != null) spawnedProjectile.ToRemove = true;
                spawned = 0;
                return false;
            }
            if (draw > 0) player.ComponentMiner?.DamageActiveTool(1);
            return true;
        }

        /// <summary>
        /// 火枪：与 <c>SubsystemMusketBlockBehavior.OnAim(Completed)</c> 同形。
        /// 空包弹 / 装填不足时引擎也会消耗装填并扣耐久，只是不生成投射物。
        /// </summary>
        private bool TryFireMusket(ComponentPlayer player, IInventory inventory, int slot,
            int itemValue, Block block, Vector3 direction, out int spawned,
            out Vector3 firstPosition, out Vector3 firstVelocity, out string reason)
        {
            spawned = 0;
            firstPosition = Vector3.Zero;
            firstVelocity = Vector3.Zero;
            reason = null;
            if (!(block is MusketBlock))
            {
                reason = "item-not-musket";
                return false;
            }
            int data = global::Game.Terrain.ExtractData(itemValue);
            if (!MusketBlock.GetHammerState(data))
            {
                reason = "hammer-not-cocked";
                return false;
            }
            MusketBlock.LoadState loadState = MusketBlock.GetLoadState(data);
            BulletBlock.BulletType? bulletType = MusketBlock.GetBulletType(data);
            bool fired = false;
            int bulletValue = 0;
            int bulletCount = 0;
            float bulletSpeed = 0f;
            Vector3 bulletSpread = Vector3.Zero;
            switch (loadState)
            {
                case MusketBlock.LoadState.Empty:
                    // 没装火药：引擎只显示提示并放下击锤，不生成投射物、不扣耐久。
                    break;
                case MusketBlock.LoadState.Gunpowder:
                case MusketBlock.LoadState.Wad:
                    // 空包弹：有枪声/烟，没有投射物，但仍消耗装填 + 扣 1 点耐久。
                    fired = true;
                    break;
                case MusketBlock.LoadState.Loaded:
                    fired = true;
                    if (bulletType == BulletBlock.BulletType.Buckshot)
                    {
                        bulletValue = global::Game.Terrain.MakeBlockValue(BulletBlock.Index, 0,
                            BulletBlock.SetBulletType(0, BulletBlock.BulletType.BuckshotBall));
                        bulletCount = 8;
                        bulletSpread = new Vector3(0.04f, 0.04f, 0.25f);
                        bulletSpeed = 80f;
                    }
                    else if (bulletType == BulletBlock.BulletType.BuckshotBall)
                    {
                        bulletValue = global::Game.Terrain.MakeBlockValue(BulletBlock.Index, 0,
                            BulletBlock.SetBulletType(0, BulletBlock.BulletType.BuckshotBall));
                        bulletCount = 1;
                        bulletSpread = new Vector3(0.06f, 0.06f, 0f);
                        bulletSpeed = 60f;
                    }
                    else if (bulletType.HasValue)
                    {
                        bulletValue = global::Game.Terrain.MakeBlockValue(BulletBlock.Index, 0,
                            BulletBlock.SetBulletType(0, bulletType.Value));
                        bulletCount = 1;
                        bulletSpeed = 120f;
                    }
                    break;
            }
            var spawnedProjectiles = new List<Projectile>();
            if (fired)
            {
                ComponentBody body = player.ComponentBody;
                ComponentCreatureModel model = player.ComponentCreatureModel;
                Vector3 origin = model.EyePosition + body.Matrix.Right * 0.3f -
                    body.Matrix.Up * 0.2f;
                // 枪管进水：引擎的哑火分支只有声音/烟，不生成投射物（装填照样消耗）。
                bool misfire = body.ImmersionFactor > 0.4f;
                if (!misfire && bulletCount > 0)
                {
                    Vector3 right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
                    Vector3 up = Vector3.Normalize(Vector3.Cross(direction, right));
                    for (int i = 0; i < bulletCount; i++)
                    {
                        Vector3 noise = m_requestRandom.Float(-bulletSpread.X, bulletSpread.X) *
                            right + m_requestRandom.Float(-bulletSpread.Y, bulletSpread.Y) * up +
                            m_requestRandom.Float(-bulletSpread.Z, bulletSpread.Z) * direction;
                        Vector3 velocity = body.Velocity + bulletSpeed * (direction + noise);
                        Projectile projectile = m_projectiles.FireProjectile(bulletValue, origin,
                            velocity, Vector3.Zero, player);
                        if (projectile == null) continue;
                        projectile.ProjectileStoppedAction = ProjectileStoppedAction.Disappear;
                        spawnedProjectiles.Add(projectile);
                        if (spawned == 0)
                        {
                            firstPosition = projectile.Position;
                            firstVelocity = projectile.Velocity;
                        }
                        spawned++;
                    }
                }
                data = MusketBlock.SetLoadState(data, MusketBlock.LoadState.Empty);
            }
            data = MusketBlock.SetHammerState(data, false);
            int resetValue = global::Game.Terrain.MakeBlockValue(MusketBlock.Index, 0, data);
            if (!TryReplaceHeldStack(inventory, slot, resetValue, out reason))
            {
                for (int i = 0; i < spawnedProjectiles.Count; i++)
                    spawnedProjectiles[i].ToRemove = true;
                spawned = 0;
                return false;
            }
            if (fired) player.ComponentMiner?.DamageActiveTool(1);
            return true;
        }

        // ================================================================ 主机：执行辅助

        private bool TryResolveHostActor(Project project, int clientId, out ComponentPlayer player,
            out PlayerData playerData, out string reason)
        {
            player = null;
            playerData = null;
            reason = null;
            if (m_spawner == null)
            {
                reason = "no-spawner";
                return false;
            }
            if (clientId <= 0)
            {
                reason = "not-player-peer";
                return false;
            }
            if (!m_spawner.TryGet(clientId, out playerData) || playerData == null)
            {
                reason = "no-avatar";
                return false;
            }
            player = playerData.ComponentPlayer;
            if (player == null)
            {
                reason = "no-avatar";
                return false;
            }
            if (player.ComponentBody == null)
            {
                reason = "no-body";
                return false;
            }
            if (player.ComponentHealth == null)
            {
                reason = "no-health";
                return false;
            }
            if (player.ComponentHealth.Health <= 0f)
            {
                reason = "dead";
                return false;
            }
            return true;
        }

        private static Block ResolveBlock(int value)
        {
            int contents = global::Game.Terrain.ExtractContents(value);
            if (contents <= 0 || BlocksManager.Blocks == null ||
                contents >= BlocksManager.Blocks.Length)
                return null;
            return BlocksManager.Blocks[contents];
        }

        private static void CaptureInventorySlots(IInventory inventory, out int[] values,
            out int[] counts)
        {
            int slots = inventory.SlotsCount;
            values = new int[slots];
            counts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                values[i] = inventory.GetSlotValue(i);
                counts[i] = inventory.GetSlotCount(i);
            }
        }

        /// <summary>
        /// 把活跃槽里的 1 件物品换成新值（弓 / 弩 / 火枪的重置与耐久损耗都走这条）。
        /// 返回 false + reason = 明确的引擎拒绝（调用方必须回滚它刚生成的投射物）。
        /// </summary>
        private static bool TryReplaceHeldStack(IInventory inventory, int slot, int newValue,
            out string reason)
        {
            reason = null;
            int removed = inventory.RemoveSlotItems(slot, 1);
            if (removed <= 0)
            {
                reason = "consume-refused";
                return false;
            }
            inventory.AddSlotItems(slot, newValue, 1);
            if (inventory.GetSlotCount(slot) != 1 || inventory.GetSlotValue(slot) != newValue)
            {
                reason = "replace-refused";
                return false;
            }
            return true;
        }

        // ================================================================ 客户端：应用副本

        public bool TryApplyPickable(in PickableStateData state, out string reason)
        {
            reason = null;
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            if (m_pickables == null)
            {
                reason = "no-pickables-subsystem";
                return false;
            }
            if (state.Action == EntityActions.Remove)
            {
                if (m_clientPickables.TryGetValue(state.Id, out ClientPickableReplica removed))
                {
                    RemovePickableReplica(removed);
                    m_clientPickables.Remove(state.Id);
                    m_lastPickupRequestMs.Remove(state.Id);
                    m_replicasRemoved++;
                }
                return true;
            }
            if (m_clientPickables.TryGetValue(state.Id, out ClientPickableReplica replica) &&
                replica.Pickable != null && !replica.Pickable.ToRemove)
            {
                ApplyPickableTarget(replica, state);
                m_replicasUpdated++;
                return true;
            }
            Matrix? stuckMatrix = state.HasStuckMatrix
                ? CreateStuckMatrix(state.StuckPosition, state.StuckRotation)
                : (Matrix?)null;
            Pickable pickable;
            try
            {
                pickable = m_pickables.AddPickable(state.Value, state.Count, state.Position,
                    state.Velocity, stuckMatrix);
            }
            catch (Exception e)
            {
                reason = "add-threw-" + e.GetType().Name;
                m_applyFailures++;
                return false;
            }
            if (pickable == null)
            {
                reason = "add-returned-null";
                m_applyFailures++;
                return false;
            }
            replica = new ClientPickableReplica
            {
                Pickable = pickable,
                LastUpdateTime = Time.RealTime
            };
            ApplyPickableTarget(replica, state);
            m_clientPickables[state.Id] = replica;
            m_replicasCreated++;
            return true;
        }

        private static Matrix CreateStuckMatrix(Vector3 position, Quaternion rotation)
        {
            Matrix matrix = Matrix.CreateFromQuaternion(rotation);
            matrix.Translation = position;
            return matrix;
        }

        private static void ApplyPickableTarget(ClientPickableReplica replica,
            in PickableStateData state)
        {
            replica.PreviousTargetPosition = replica.TargetPosition;
            replica.HasPreviousTargetPosition = replica.Initialized;
            replica.TargetPosition = state.Position;
            replica.TargetVelocity = state.Velocity;
            replica.TargetHasFlyTo = state.HasFlyTo;
            replica.TargetFlyTo = state.FlyTo;
            replica.LastUpdateTime = Time.RealTime;
            Pickable pickable = replica.Pickable;
            if (pickable == null) return;
            pickable.Value = state.Value;
            pickable.Count = state.Count;
            pickable.StuckMatrix = state.HasStuckMatrix
                ? CreateStuckMatrix(state.StuckPosition, state.StuckRotation)
                : (Matrix?)null;
        }

        public bool TryApplyProjectile(in ProjectileStateData state, out string reason)
        {
            reason = null;
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            if (m_projectiles == null)
            {
                reason = "no-projectiles-subsystem";
                return false;
            }
            if (state.Action == EntityActions.Remove)
            {
                if (m_clientProjectiles.TryGetValue(state.Id, out ClientProjectileReplica removed))
                {
                    RemoveProjectileReplica(removed);
                    m_clientProjectiles.Remove(state.Id);
                    m_replicasRemoved++;
                }
                return true;
            }
            if (m_clientProjectiles.TryGetValue(state.Id, out ClientProjectileReplica replica) &&
                replica.Projectile != null && !replica.Projectile.ToRemove)
            {
                ApplyProjectileTarget(replica, state);
                m_replicasUpdated++;
                return true;
            }
            Projectile projectile;
            try
            {
                projectile = m_projectiles.AddProjectile(state.Value, state.Position,
                    state.Velocity, state.AngularVelocity, null);
            }
            catch (Exception e)
            {
                reason = "add-threw-" + e.GetType().Name;
                m_applyFailures++;
                return false;
            }
            if (projectile == null)
            {
                reason = "add-returned-null";
                m_applyFailures++;
                return false;
            }
            replica = new ClientProjectileReplica
            {
                Projectile = projectile,
                LastUpdateTime = Time.RealTime
            };
            ApplyProjectileTarget(replica, state);
            m_clientProjectiles[state.Id] = replica;
            m_replicasCreated++;
            return true;
        }

        private static void ApplyProjectileTarget(ClientProjectileReplica replica,
            in ProjectileStateData state)
        {
            replica.TargetPosition = state.Position;
            replica.TargetVelocity = state.Velocity;
            replica.TargetRotation = state.Rotation;
            replica.TargetAngularVelocity = state.AngularVelocity;
            replica.TargetTrailOffset = state.TrailOffset;
            replica.TargetStuck = state.Stuck;
            replica.TargetIncendiary = state.Incendiary;
            replica.LastUpdateTime = Time.RealTime;
            if (replica.Projectile == null) return;
            replica.Projectile.Value = state.Value;
            replica.Projectile.IsIncendiary = state.Incendiary;
            replica.Projectile.TrailOffset = state.TrailOffset;
        }

        // ================================================================ 客户端：每帧插值 + 扫描

        public void TickClient(float deltaTime)
        {
            if (!m_clientReplica) return;
            float step = MathUtils.Clamp(deltaTime, 0f, 0.05f);
            double now = Time.RealTime;
            UpdatePickableReplicas(step, now);
            UpdateProjectileReplicas(step, now);
            ScanPickupCandidates(now);
        }

        private void UpdatePickableReplicas(float step, double now)
        {
            List<int> stale = null;
            foreach (KeyValuePair<int, ClientPickableReplica> item in m_clientPickables)
            {
                Pickable pickable = item.Value.Pickable;
                if (pickable == null || pickable.ToRemove)
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(item.Key);
                    continue;
                }
                double age = MathUtils.Clamp(now - item.Value.LastUpdateTime, 0.0, 0.25);
                Vector3 predicted = item.Value.TargetPosition +
                    item.Value.TargetVelocity * (float)age;
                // 引擎静止判据：位置两帧不动就不能再按 velocity 外推（旧实现同一条注释）。
                bool hostPositionStable = item.Value.HasPreviousTargetPosition &&
                    Vector3.DistanceSquared(item.Value.TargetPosition,
                        item.Value.PreviousTargetPosition) < RestingPositionEpsilonSquared;
                bool resting = !item.Value.TargetHasFlyTo &&
                    (item.Value.TargetVelocity.LengthSquared() < 0.04f || hostPositionStable);
                if (resting)
                {
                    pickable.Position = item.Value.TargetPosition;
                    pickable.Velocity = Vector3.Zero;
                    item.Value.PresentationVelocity = Vector3.Zero;
                    item.Value.Initialized = true;
                }
                else if (!item.Value.Initialized)
                {
                    pickable.Position = item.Value.TargetPosition;
                    pickable.Velocity = item.Value.TargetVelocity;
                    item.Value.PresentationVelocity = item.Value.TargetVelocity;
                    item.Value.Initialized = true;
                }
                else
                {
                    Vector3 error = predicted - pickable.Position;
                    float blend = 1f - (float)Math.Exp(-12f * step);
                    Vector3 correction = error * blend;
                    float maxStep = 24f * step;
                    float length = correction.Length();
                    if (length > maxStep) correction *= maxStep / length;
                    pickable.Position += correction;
                    if (Vector3.DistanceSquared(pickable.Position, predicted) < 0.0004f)
                        pickable.Position = predicted;
                    Vector3 desired = item.Value.TargetVelocity + error / 0.18f;
                    float response = 10f;
                    float velocityBlend = 1f - (float)Math.Exp(-response * step);
                    item.Value.PresentationVelocity = Vector3.Lerp(
                        item.Value.PresentationVelocity, desired, velocityBlend);
                    pickable.Velocity = item.Value.PresentationVelocity;
                }
                // 主机磁吸目标优先；本地扫描（拾取动画表现）在下一段覆盖它。
                pickable.FlyToPosition = item.Value.TargetHasFlyTo
                    ? item.Value.TargetFlyTo
                    : (Vector3?)null;
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
            {
                m_clientPickables.Remove(stale[i]);
                m_lastPickupRequestMs.Remove(stale[i]);
            }
        }

        private void UpdateProjectileReplicas(float step, double now)
        {
            List<int> stale = null;
            foreach (KeyValuePair<int, ClientProjectileReplica> item in m_clientProjectiles)
            {
                Projectile projectile = item.Value.Projectile;
                if (projectile == null || projectile.ToRemove)
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(item.Key);
                    continue;
                }
                double age = MathUtils.Clamp(now - item.Value.LastUpdateTime, 0.0, 0.35);
                float ageFloat = (float)age;
                Vector3 targetPosition = item.Value.TargetPosition +
                    item.Value.TargetVelocity * ageFloat;
                targetPosition.Y -= 5f * ageFloat * ageFloat;
                Vector3 targetVelocity = item.Value.TargetVelocity +
                    new Vector3(0f, -10f * ageFloat, 0f);
                if (item.Value.TargetStuck)
                {
                    targetPosition = item.Value.TargetPosition;
                    targetVelocity = Vector3.Zero;
                }
                if (!item.Value.Initialized)
                {
                    projectile.Position = targetPosition;
                    projectile.Velocity = targetVelocity;
                    projectile.Rotation = item.Value.TargetRotation;
                    projectile.AngularVelocity = item.Value.TargetAngularVelocity;
                    item.Value.Initialized = true;
                }
                else
                {
                    float distanceSquared = Vector3.DistanceSquared(projectile.Position,
                        targetPosition);
                    if (distanceSquared > 25f) projectile.Position = targetPosition;
                    else projectile.Position = Vector3.Lerp(projectile.Position, targetPosition,
                        0.35f);
                    projectile.Velocity = Vector3.Lerp(projectile.Velocity, targetVelocity, 0.5f);
                    projectile.Rotation = Vector3.Lerp(projectile.Rotation,
                        item.Value.TargetRotation, 0.5f);
                    projectile.AngularVelocity = Vector3.Lerp(projectile.AngularVelocity,
                        item.Value.TargetAngularVelocity, 0.5f);
                }
                projectile.TrailOffset = item.Value.TargetTrailOffset;
                projectile.IsIncendiary = item.Value.TargetIncendiary;
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) m_clientProjectiles.Remove(stale[i]);
        }

        /// <summary>
        /// 客户端扫描：与引擎相同的吸引/拾取距离，但**不**调用 AcquireItems；
        /// 只把候选交给模块发请求（主机校验后才真正拾取）。
        /// Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        /// Source: Mod/ScMultiplayer/Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs:170-260
        /// </summary>
        private void ScanPickupCandidates(double now)
        {
            if (now < m_nextPickupScanTime) return;
            m_nextPickupScanTime = now + PickupScanIntervalSeconds;
            if (m_spawner == null || m_project == null) return;
            if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _)) return;
            ComponentPlayer player = local?.ComponentPlayer;
            if (player?.ComponentBody == null || player.ComponentHealth == null ||
                player.ComponentHealth.Health <= 0f)
                return;
            SubsystemGameInfo gameInfo = m_project.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo == null) return;
            Vector3 eye = player.ComponentBody.Position + new Vector3(0f, 0.75f, 0f);
            Vector3 bodyVelocity = player.ComponentBody.Velocity;
            foreach (KeyValuePair<int, ClientPickableReplica> item in m_clientPickables)
            {
                Pickable pickable = item.Value.Pickable;
                if (pickable == null || pickable.ToRemove || pickable.StuckMatrix.HasValue)
                    continue;
                if (gameInfo.TotalElapsedGameTime - pickable.CreationTime <= 0.5) continue;
                float distanceSquared = Vector3.DistanceSquared(eye, pickable.Position);
                if (distanceSquared >= PickupAttractDistanceSquared) continue;
                // 表现：让副本被“吸”向本机玩家（引擎自己也会做，但它的玩家列表在客户端被清空了）。
                pickable.FlyToPosition = eye + 0.1f * MathUtils.Sqrt(distanceSquared) *
                    bodyVelocity;
                if (distanceSquared >= PickupAcquireDistanceSquared) continue;
                if (m_lastPickupRequestMs.TryGetValue(item.Key, out double lastRequest) &&
                    now - lastRequest < PickupRequestIntervalSeconds)
                    continue;
                m_lastPickupRequestMs[item.Key] = now;
                m_pickupCandidates++;
                try
                {
                    ClientPickupSubmitted?.Invoke(new PickablePickupCandidate(item.Key,
                        pickable.Position, eye));
                }
                catch (Exception e)
                {
                    LogProblemOnce("pickup-submit:" + e.GetType().Name,
                        "拾取候选处理器异常（id=" + item.Key + "）：" + e.GetType().Name + " " +
                        e.Message + " → 本次不重试，下一轮扫描会再上报");
                }
            }
        }

        /// <summary>
        /// 客户端：清掉**不是**主机复制来的拾取物（客户端本地预测 / UI 掉落 / 方块行为掉落）。
        /// 客户端不允许自己创建拾取物；这些对象若保留，会与主机的权威掉落重复或永远不消失。
        /// 明确计数 + 限流日志（不是静默兜底）。
        /// </summary>
        internal void CullForeignClientPickables()
        {
            if (!m_clientReplica || m_pickables == null) return;
            ReadOnlyList<Pickable> list = m_pickables.Pickables;
            if (list.Count == 0) return;
            var owned = new HashSet<Pickable>();
            foreach (KeyValuePair<int, ClientPickableReplica> item in m_clientPickables)
                if (item.Value.Pickable != null) owned.Add(item.Value.Pickable);
            List<Pickable> foreign = null;
            for (int i = 0; i < list.Count; i++)
            {
                Pickable pickable = list[i];
                if (pickable == null || owned.Contains(pickable)) continue;
                if (foreign == null) foreign = new List<Pickable>();
                foreign.Add(pickable);
            }
            if (foreign == null) return;
            for (int i = 0; i < foreign.Count; i++)
            {
                foreign[i].ToRemove = true;
                if (TryGetPickableList(out List<Pickable> raw)) raw.Remove(foreign[i]);
                m_foreignPickablesRemoved++;
            }
            LogRateLimited("foreign-pickable",
                "客户端清除了 " + foreign.Count + " 个本地生成的拾取物（累计 " +
                m_foreignPickablesRemoved + "）：拾取物只能由主机创建并经实体复制下发");
        }

        /// <summary>
        /// 客户端：清掉**不是**主机复制来的投射物（通常是客户端武器本地预测）。
        /// 客户端不允许自己模拟投射物；这些对象如果没有主机快照就会永远停在空中。
        /// 明确计数 + 限流日志（不是静默兜底）。
        /// </summary>
        internal void CullForeignClientProjectiles()
        {
            if (!m_clientReplica || m_projectiles == null) return;
            ReadOnlyList<Projectile> list = m_projectiles.Projectiles;
            if (list.Count == 0) return;
            var owned = new HashSet<Projectile>();
            foreach (KeyValuePair<int, ClientProjectileReplica> item in m_clientProjectiles)
                if (item.Value.Projectile != null) owned.Add(item.Value.Projectile);
            List<Projectile> foreign = null;
            for (int i = 0; i < list.Count; i++)
            {
                Projectile projectile = list[i];
                if (projectile == null || owned.Contains(projectile)) continue;
                if (foreign == null) foreign = new List<Projectile>();
                foreign.Add(projectile);
            }
            if (foreign == null) return;
            for (int i = 0; i < foreign.Count; i++)
            {
                RemoveForeignProjectile(foreign[i]);
                m_foreignProjectilesRemoved++;
            }
            LogRateLimited("foreign-projectile",
                "客户端清除了 " + foreign.Count + " 个本地生成的投射物（累计 " +
                m_foreignProjectilesRemoved + "）：客户端不模拟投射物；" +
                "武器开火必须由主机创建并经实体复制下发");
        }

        private void RemoveForeignProjectile(Projectile projectile)
        {
            if (projectile == null) return;
            projectile.ToRemove = true;
            if (TryGetProjectileList(out List<Projectile> list)) list.Remove(projectile);
        }

        // ================================================================ 客户端：清理

        public bool ResetClientReplicas(string reason)
        {
            RemoveAllClientReplicas(reason);
            return true;
        }

        private void RemoveAllClientReplicas(string reason)
        {
            if (m_clientPickables.Count == 0 && m_clientProjectiles.Count == 0) return;
            int pickables = m_clientPickables.Count;
            int projectiles = m_clientProjectiles.Count;
            foreach (KeyValuePair<int, ClientPickableReplica> item in m_clientPickables)
                RemovePickableReplica(item.Value);
            m_clientPickables.Clear();
            foreach (KeyValuePair<int, ClientProjectileReplica> item in m_clientProjectiles)
                RemoveProjectileReplica(item.Value);
            m_clientProjectiles.Clear();
            m_lastPickupRequestMs.Clear();
            m_replicasRemoved += pickables + projectiles;
            Log("清空实体副本（" + reason + "）：拾取物 " + pickables + " / 投射物 " + projectiles);
        }

        private void RemovePickableReplica(ClientPickableReplica replica)
        {
            if (replica?.Pickable == null) return;
            Pickable pickable = replica.Pickable;
            pickable.ToRemove = true;
            if (!TryGetPickableList(out List<Pickable> list))
            {
                m_pickableListFailures++;
                LogProblemOnce("pickable-list-remove",
                    "无法访问 SubsystemPickables.m_pickables → 拾取物副本只能置 ToRemove" +
                    "（若 base.Update 不跑就会残留；计数 " + m_pickableListFailures + "）");
                return;
            }
            list.Remove(pickable);
        }

        private bool TryGetPickableList(out List<Pickable> list)
        {
            list = null;
            if (m_pickables == null) return false;
            if (m_pickableList != null && ReferenceEquals(m_pickableListOwner, m_pickables))
            {
                list = m_pickableList;
                return true;
            }
            try
            {
                m_pickableList = m_parentField.GetParentField<List<Pickable>>(m_pickables,
                    "m_pickables", typeof(SubsystemPickables));
                m_pickableListOwner = m_pickables;
            }
            catch (Exception e)
            {
                m_pickableList = null;
                m_pickableListOwner = null;
                LogProblemOnce("pickable-list-field:" + e.GetType().Name,
                    "反射读取 SubsystemPickables.m_pickables 失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 客户端无法从列表移除拾取物副本");
                return false;
            }
            if (m_pickableList == null)
            {
                LogProblemOnce("pickable-list-null",
                    "SubsystemPickables.m_pickables 为空（引擎版本差异）→ 无法移除拾取物副本");
                return false;
            }
            list = m_pickableList;
            return true;
        }

        private void RemoveProjectileReplica(ClientProjectileReplica replica)
        {
            if (replica?.Projectile == null) return;
            Projectile projectile = replica.Projectile;
            projectile.ToRemove = true;
            if (projectile.TrailParticleSystem != null)
            {
                try
                {
                    projectile.TrailParticleSystem.IsStopped = true;
                    SubsystemParticles particles =
                        m_project?.FindSubsystem<SubsystemParticles>(false);
                    // Source: Survivalcraft/Game/SubsystemProjectiles.cs:RemoveTrail
                    // 引擎自己也是 (ParticleSystemBase) 强转后交给 SubsystemParticles。
                    if (projectile.TrailParticleSystem is ParticleSystemBase trail &&
                        particles != null && particles.ContainsParticleSystem(trail))
                        particles.RemoveParticleSystem(trail);
                }
                catch (Exception)
                {
                    // 粒子清理失败不影响副本移除；下一帧重试不会造成逻辑错误。
                }
                projectile.TrailParticleSystem = null;
            }
            if (!TryGetProjectileList(out List<Projectile> list))
            {
                // 拿不到私有列表：ToRemove 已经置位；若 base.Update 永不运行就无法真正移除。
                m_projectileListFailures++;
                LogProblemOnce("projectile-list-remove",
                    "无法访问 SubsystemProjectiles.m_projectiles → 投射物副本只能置 ToRemove，" +
                    "列表里可能残留（计数可审计：" + m_projectileListFailures + "）");
                return;
            }
            list.Remove(projectile);
        }

        private bool TryGetProjectileList(out List<Projectile> list)
        {
            list = null;
            if (m_projectiles == null) return false;
            if (m_projectileList != null &&
                ReferenceEquals(m_projectileListOwner, m_projectiles))
            {
                list = m_projectileList;
                return true;
            }
            try
            {
                m_projectileList = m_parentField.GetParentField<List<Projectile>>(m_projectiles,
                    "m_projectiles", typeof(SubsystemProjectiles));
                m_projectileListOwner = m_projectiles;
            }
            catch (Exception e)
            {
                m_projectileList = null;
                m_projectileListOwner = null;
                LogProblemOnce("projectile-list-field:" + e.GetType().Name,
                    "反射读取 SubsystemProjectiles.m_projectiles 失败（" + e.GetType().Name +
                    " " + e.Message + "）→ 客户端无法从列表移除投射物副本");
                return false;
            }
            if (m_projectileList == null)
            {
                LogProblemOnce("projectile-list-null",
                    "SubsystemProjectiles.m_projectiles 为空（引擎版本差异）→ 客户端无法移除投射物副本");
                return false;
            }
            list = m_projectileList;
            return true;
        }

        // ================================================================ 诊断

        public string Describe() => "clientReplica=" + m_clientReplica +
            " ready=" + IsReplicaReady + " pickables=" + m_clientPickables.Count +
            " projectiles=" + m_clientProjectiles.Count +
            " hostCaptures=" + m_hostCaptures + " ids=" + m_idAssignments +
            " created=" + m_replicasCreated + " updated=" + m_replicasUpdated +
            " removed=" + m_replicasRemoved + " applyFailures=" + m_applyFailures +
            " acquireAccepted=" + m_acquireAccepted + " acquireRejected=" + m_acquireRejected +
            " pickupCandidates=" + m_pickupCandidates +
            " projectileListFailures=" + m_projectileListFailures +
            " foreignCulled=" + (m_foreignPickablesRemoved + m_foreignProjectilesRemoved) +
            " foreignPickablesRemoved=" + m_foreignPickablesRemoved +
            " foreignProjectilesRemoved=" + m_foreignProjectilesRemoved +
            " pickableListFailures=" + m_pickableListFailures +
            " dropIntents=" + m_dropIntentSubmissions + "/" + m_dropIntentFailures +
            " fireIntents=" + m_fireIntentSubmissions + "/" + m_fireIntentFailures +
            " hostDrop=" + m_dropAccepted + "/" + m_dropRejected +
            " hostFire=" + m_fireAccepted + "/" + m_fireRejected +
            " remoteAvatarFilterCalls=" + m_remoteAvatarFilterCalls;

        private void Log(string message) => m_diagnostics?.Info(m_ownerId, message);

        private void LogProblemOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void LogRateLimited(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            if (m_lastProblemMs.Count > 512) m_lastProblemMs.Clear();
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}

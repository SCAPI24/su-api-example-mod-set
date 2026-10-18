using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using PE = SuMultiplayer.Protocol.Entities;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 拾取物（掉落物 / 爆炸碎片 / 投掷物落地）与投射物（箭 / 子弹 / 炸弹）复制模块。
    ///
    /// 需求口径（主机权威）：
    ///   · 主机是**唯一**创建 / 更新 / 删除这两类实体的地方（挖方块掉落、爆炸碎片、投掷、
    ///     动物产物……都发生在主机引擎里）；
    ///   · 客户端把它们当**副本**：只创建引擎 Pickable / Projectile 用于渲染与播放，
    ///     不参与拾取结算，不写地形，不造成伤害；
    ///   · 客户端拾取 = 请求：<c>PickableAcquireRequest</c> → 主机校验 / 落地 → 广播
    ///     <c>EntityState(REMOVE)</c> 给所有端 + 只给拾取者一条带稀疏背包增量的回执；
    ///   · 加入 / 重连 / 主动 resync → <c>EntitySnapshot</c>；complete=true 时客户端用快照
    ///     替换副本集合，保证不会出现重复或幽灵副本。
    ///
    /// 节奏与预算（与旧实现的 8Hz 快照 / 32Hz 脉冲同一条思路，但改成**变化驱动 + 合并**）：
    ///   · 32Hz 脉冲里最多 2 条 EntityState；每条最多 128 条拾取物 + 64 条投射物；
    ///   · 待发队列有界，超出时按状态（REMOVE &gt; ADD &gt; UPDATE）保序丢弃并计数；
    ///   · 快照有界（512 / 256 条），截断时 complete=false（客户端据此不删除未列出的副本）。
    ///
    /// 旧实现来源：
    ///   Source: Mod/ScMultiplayer/Message/PickableSyncMessage.cs:PickableSyncMessage
    ///   Source: Mod/ScMultiplayer/Message/ProjectileSyncMessage.cs:ProjectileSyncMessage
    ///   Source: Mod/ScMultiplayer/Func/WorldObjectSynchronizer.cs（主机分配 id / 发布 / 删除）
    ///   Source: Mod/ScMultiplayer/Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs
    ///           （Create / UpdatePosition / Delete / RequestAcquire / Acquire + 稀疏背包增量）
    ///   Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:146-300
    ///           （PickableAdded/PickableRemoved 生命周期 + 静止拾取物零速度）
    ///
    /// 本模块**无静态状态**，只依赖端口（引擎侧实现见 Engine/PickableReplicaEngine.cs）。
    /// </summary>
    public sealed class EntityReplicationModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const float PulseDuration = 1f / 32f;
        private const int MaxStateMessagesPerPulse = EntityReplicationLimits.MaxStateMessagesPerPulse;
        private const int MaxPickableRecordsPerMessage =
            EntityReplicationLimits.MaxPickableRecordsPerMessage;
        private const int MaxProjectileRecordsPerMessage =
            EntityReplicationLimits.MaxProjectileRecordsPerMessage;
        private const int MaxTrackedPickables = EntityReplicationLimits.MaxTrackedPickables;
        private const int MaxTrackedProjectiles = EntityReplicationLimits.MaxTrackedProjectiles;
        private const int MaxPendingPickables = EntityReplicationLimits.MaxPendingPickableChanges;
        private const int MaxPendingProjectiles = EntityReplicationLimits.MaxPendingProjectileChanges;
        private const int MaxSnapshotPickables = EntityReplicationLimits.MaxSnapshotPickables;
        private const int MaxSnapshotProjectiles = EntityReplicationLimits.MaxSnapshotProjectiles;

        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;
        private const double StaleResyncSeconds = 15.0;
        private const double ResyncCooldownSeconds = 5.0;
        private const double PickupTimeoutSeconds = 5.0;
        private const double PickupRetryCooldownSeconds = 0.75;
        private const double SnapshotQueueTimeoutSeconds = 10.0;
        private const int MaxPendingPickups = 64;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IEntityReplicaPort m_port;
        private readonly IPlayerActionPort m_actionPort;
        private readonly IPlayerInventoryStore m_inventoryStore;
        private readonly EntityReplicationCounters m_counters = new EntityReplicationCounters();
        private readonly EntityStateOrderer m_orderer = new EntityStateOrderer();

        // ---- 主机：已发布状态 / 待发合并队列
        private readonly Dictionary<int, PickableStateData> m_lastPickables =
            new Dictionary<int, PickableStateData>();
        private readonly Dictionary<int, ProjectileStateData> m_lastProjectiles =
            new Dictionary<int, ProjectileStateData>();
        private readonly Dictionary<int, PickableStateData> m_pendingPickables =
            new Dictionary<int, PickableStateData>();
        private readonly Dictionary<int, ProjectileStateData> m_pendingProjectiles =
            new Dictionary<int, ProjectileStateData>();
        private readonly Dictionary<int, double> m_pendingSnapshots = new Dictionary<int, double>();
        private readonly Dictionary<int, PickupHistory> m_pickupHistory =
            new Dictionary<int, PickupHistory>();
        private readonly List<PickableStateData> m_capturePickables = new List<PickableStateData>();
        private readonly List<ProjectileStateData> m_captureProjectiles =
            new List<ProjectileStateData>();
        private readonly HashSet<int> m_capturePickableIds = new HashSet<int>();
        private readonly HashSet<int> m_captureProjectileIds = new HashSet<int>();
        private readonly List<int> m_removalScratch = new List<int>();
        private readonly List<int> m_batchPickableIds = new List<int>();
        private readonly List<int> m_batchProjectileIds = new List<int>();

        // ---- 客户端：副本集合 / 请求
        private readonly HashSet<int> m_clientPickableIds = new HashSet<int>();
        private readonly HashSet<int> m_clientProjectileIds = new HashSet<int>();
        private readonly Dictionary<int, PendingPickup> m_pendingPickups =
            new Dictionary<int, PendingPickup>();
        private readonly Dictionary<int, double> m_pickupCooldown = new Dictionary<int, double>();

        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;
        private IMessageRegistryPort m_messages;
        private Action<int, string, string, bool> m_clientJoinedHandler;
        private Action<int, string> m_clientLeftHandler;
        private bool m_attached;
        private bool m_hosting;
        private bool m_wasConnected;
        private bool m_roleInitialized;
        private bool m_clientReady;
        private bool m_snapshotCaptureOk;

        private double m_lastHostPulse;
        private long m_serverTickMs;
        private uint m_stateVersion;
        private uint m_snapshotId;
        private long m_nextPickupSequence;
        private int m_inventoryVersion;
        private double m_lastEntityMessageMs;
        private double m_lastResyncRequestMs;
        /// <summary>最近一次 tick 的时间（模块内所有超时 / 冷却都用它，不读墙钟）。</summary>
        private double m_now;
        private long m_lastSummaryMs;

        private sealed class PickupHistory
        {
            public long LastSequence;
            public bool HasResult;
            public PickableAcquireResultData LastResult;
        }

        private readonly struct PendingPickup
        {
            public PendingPickup(long sequence, double sentMs)
            {
                Sequence = sequence;
                SentMs = sentMs;
            }

            public long Sequence { get; }
            public double SentMs { get; }
        }

        public EntityReplicationModule(IEntityReplicaPort port, IPlayerActionPort actionPort,
            IPlayerInventoryStore inventoryStore)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_actionPort = actionPort ?? throw new ArgumentNullException(nameof(actionPort));
            m_inventoryStore = inventoryStore ??
                throw new ArgumentNullException(nameof(inventoryStore));
            m_descriptor = new ModuleDescriptor("entity-replication",
                "拾取物 / 投射物复制（主机权威）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "entity.replication" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        /// <summary>自检 / 控制台可读的计数器快照（实例对象，非静态）。</summary>
        public EntityReplicationCounters Counters => m_counters;

        public string StatusLine() => "实体复制：" +
            (m_hosting ? "主机权威" : "客户端副本") +
            " pickableAdd=" + m_counters.PickableAdd +
            " pickableUpdate=" + m_counters.PickableUpdate +
            " pickableRemove=" + m_counters.PickableRemove +
            " pickableRejected=" + m_counters.PickableRejected +
            " pickableDropped=" + m_counters.PickableDropped +
            " · projectileAdd=" + m_counters.ProjectileAdd +
            " projectileUpdate=" + m_counters.ProjectileUpdate +
            " projectileRemove=" + m_counters.ProjectileRemove +
            " projectileRejected=" + m_counters.ProjectileRejected +
            " projectileDropped=" + m_counters.ProjectileDropped +
            " · stateMsg=" + m_counters.StateMessagesSent +
            " stateFail=" + m_counters.StateSendFailures +
            " stateDropped=" + m_counters.StateDropped +
            " stateRecv=" + m_counters.StateReceived +
            " snapshotSent=" + m_counters.SnapshotsSent +
            " snapshotRecv=" + m_counters.SnapshotsReceived +
            " · pickupReq/acc/rej=" + m_counters.PickupRequestsSent + "/" +
            m_counters.PickupAccepted + "/" + m_counters.PickupRejected +
            " persisted=" + m_counters.PickupPersisted +
            " persistFail=" + m_counters.PickupPersistFailures +
            " resync=" + m_counters.ResyncRequestsSent +
            " · payload=" + m_pendingPickables.Count + "p/" + m_pendingProjectiles.Count +
            "j · " + m_port.Describe();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 拾取物 / 投射物复制不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "实体复制不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_broadcast) || m_broadcast == null)
            {
                Log("警告：没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把实体状态广播给所有客户端");
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到实体状态 / 拾取请求");
            }
            m_port.ClientPickupSubmitted += OnClientPickupSubmitted;
            m_clientJoinedHandler = HandleClientJoined;
            m_clientLeftHandler = HandleClientLeft;
            m_transport.ClientJoined += m_clientJoinedHandler;
            m_transport.ClientLeft += m_clientLeftHandler;
            m_attached = true;
            Log("实体复制模块就绪：主机 = 引擎权威（32Hz 变化驱动 + 合并 + 有界队列）；" +
                "客户端 = 只建副本、拾取走请求（不本地结算）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.EntityState, PE.EntityState.Parser,
                new DelegateMessageHandler<PE.EntityState>(OnEntityState), ModuleId);
            registry.Register((int)MessageType.EntitySnapshot, PE.EntitySnapshot.Parser,
                new DelegateMessageHandler<PE.EntitySnapshot>(OnEntitySnapshot), ModuleId);
            registry.Register((int)MessageType.PickableAcquireRequest,
                PE.PickableAcquireRequest.Parser,
                new DelegateMessageHandler<PE.PickableAcquireRequest>(OnPickableAcquireRequest),
                ModuleId);
            registry.Register((int)MessageType.PickableAcquireResult,
                PE.PickableAcquireResult.Parser,
                new DelegateMessageHandler<PE.PickableAcquireResult>(OnPickableAcquireResult),
                ModuleId);
            registry.Register((int)MessageType.EntityResyncRequest, PE.EntityResyncRequest.Parser,
                new DelegateMessageHandler<PE.EntityResyncRequest>(OnEntityResyncRequest), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = StatusLine();
            m_port.ClientPickupSubmitted -= OnClientPickupSubmitted;
            if (m_transport != null)
            {
                if (m_clientJoinedHandler != null)
                    m_transport.ClientJoined -= m_clientJoinedHandler;
                if (m_clientLeftHandler != null)
                    m_transport.ClientLeft -= m_clientLeftHandler;
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            try { m_port.ResetClientReplicas("detach"); } catch (Exception) { }
            m_lastPickables.Clear();
            m_lastProjectiles.Clear();
            m_pendingPickables.Clear();
            m_pendingProjectiles.Clear();
            m_pendingSnapshots.Clear();
            m_pickupHistory.Clear();
            m_capturePickables.Clear();
            m_captureProjectiles.Clear();
            m_clientPickableIds.Clear();
            m_clientProjectileIds.Clear();
            m_pendingPickups.Clear();
            m_pickupCooldown.Clear();
            m_orderer.Reset("detach");
            m_attached = false;
            Log("实体复制模块下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_now = tick.Now;
            m_port.Pump();
            bool hosting = m_control != null && m_control.IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            if (!m_roleInitialized || hosting != m_hosting || connected != m_wasConnected)
                HandleRoleOrConnectionChange(hosting, connected, tick.Now);
            if (!connected) return;
            if (m_hosting) TickHost(tick.Now);
            else TickClient(tick);
            MaybeLogSummary(tick.Now);
        }

        // ================================================================ 角色 / 连接

        private void HandleRoleOrConnectionChange(bool hosting, bool connected, double now)
        {
            bool wasConnected = m_wasConnected;
            m_hosting = hosting;
            m_wasConnected = connected;
            m_roleInitialized = true;
            if (hosting)
            {
                m_orderer.Reset("host");
                m_clientPickableIds.Clear();
                m_clientProjectileIds.Clear();
                m_pendingPickups.Clear();
                m_pickupCooldown.Clear();
                m_pendingSnapshots.Clear();
                m_lastHostPulse = 0.0;
                m_serverTickMs = (long)(now * 1000.0);
                m_stateVersion = 0;
                m_snapshotId = 0;
                m_lastPickables.Clear();
                m_lastProjectiles.Clear();
                m_pendingPickables.Clear();
                m_pendingProjectiles.Clear();
                m_pickupHistory.Clear();
                Log("本端 = 主机：拾取物 / 投射物由本端引擎创建与删除，" +
                    "客户端只能请求拾取（32Hz 变化驱动广播）");
                return;
            }
            if (!connected)
            {
                bool hadReplicas = m_clientPickableIds.Count > 0 || m_clientProjectileIds.Count > 0;
                m_clientPickableIds.Clear();
                m_clientProjectileIds.Clear();
                m_pendingPickups.Clear();
                m_pickupCooldown.Clear();
                m_clientReady = false;
                m_orderer.Reset("disconnect");
                m_pendingSnapshots.Clear();
                if (hadReplicas || wasConnected)
                    m_port.ResetClientReplicas("disconnect");
                if (wasConnected) Log("与主机的连接断开 → 已清空拾取物 / 投射物副本");
                return;
            }
            // 连接上了（客户端）
            m_orderer.Reset("connect");
            m_clientReady = false;
            m_lastEntityMessageMs = now;
            m_lastResyncRequestMs = 0.0;
            m_nextPickupSequence = 0;
            m_pendingPickups.Clear();
            m_pickupCooldown.Clear();
            m_pendingSnapshots.Clear();
            m_lastPickables.Clear();
            m_lastProjectiles.Clear();
            m_pendingPickables.Clear();
            m_pendingProjectiles.Clear();
            m_clientPickableIds.Clear();
            m_clientProjectileIds.Clear();
            Log("本端 = 客户端副本：不创建 / 不结算拾取物与投射物；" +
                "世界就绪后请求一次完整快照");
        }

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_hosting) return;
            m_pendingSnapshots[clientId] = NowSeconds();
            m_pickupHistory.Remove(clientId);
            Log("客户端 " + clientId + " 已加入（" + (name ?? string.Empty) + "）→ 排队完整实体快照" +
                (rejoin ? "（重连）" : string.Empty));
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            m_pendingSnapshots.Remove(clientId);
            m_pickupHistory.Remove(clientId);
        }

        // ================================================================ 主机

        private void TickHost(double now)
        {
            if (m_lastHostPulse == 0.0 || now - m_lastHostPulse >= PulseDuration)
            {
                m_lastHostPulse = now;
                PumpHost(now);
            }
        }

        private void PumpHost(double now)
        {
            m_serverTickMs = Math.Max(m_serverTickMs, (long)(now * 1000.0));
            m_capturePickables.Clear();
            m_captureProjectiles.Clear();
            bool okPickables;
            bool okProjectiles;
            string pickableReason;
            string projectileReason;
            try
            {
                okPickables = m_port.TryCaptureHostPickables(m_capturePickables,
                    out pickableReason);
            }
            catch (Exception e)
            {
                okPickables = false;
                pickableReason = "host-exception-" + e.GetType().Name;
            }
            try
            {
                okProjectiles = m_port.TryCaptureHostProjectiles(m_captureProjectiles,
                    out projectileReason);
            }
            catch (Exception e)
            {
                okProjectiles = false;
                projectileReason = "host-exception-" + e.GetType().Name;
            }
            m_snapshotCaptureOk = okPickables && okProjectiles;
            if (!okPickables)
            {
                m_counters.CaptureFailures++;
                LogRateLimited("capture-pickables:" + pickableReason,
                    "主机抓取拾取物失败（" + pickableReason + "）→ 本拍不广播拾取物（不把空列表当成世界空了）");
            }
            if (!okProjectiles)
            {
                m_counters.CaptureFailures++;
                LogRateLimited("capture-projectiles:" + projectileReason,
                    "主机抓取投射物失败（" + projectileReason + "）→ 本拍不广播投射物");
            }
            if (okPickables) ApplyCapturedPickables(m_capturePickables);
            if (okProjectiles) ApplyCapturedProjectiles(m_captureProjectiles);

            bool hasPeers = m_broadcast != null && m_broadcast.PlayerPeerCount > 0;
            if (!hasPeers)
            {
                DropPendingStates("no-player-peers");
                return;
            }
            FlushSnapshots(now);
            FlushPendingStates();
        }

        private void ApplyCapturedPickables(List<PickableStateData> captured)
        {
            bool truncated = captured.Count > MaxTrackedPickables;
            int count = truncated ? MaxTrackedPickables : captured.Count;
            if (truncated)
            {
                int dropped = captured.Count - MaxTrackedPickables;
                m_counters.PickableDropped += dropped;
                LogRateLimited("tracked-pickables-cap",
                    "主机拾取物数量 " + captured.Count + " 超过跟踪上限 " + MaxTrackedPickables +
                    " → 本拍忽略 " + dropped + " 条（不静默截断，计数可审计）");
            }
            if (!truncated)
            {
                m_capturePickableIds.Clear();
                for (int i = 0; i < count; i++) m_capturePickableIds.Add(captured[i].Id);
                m_removalScratch.Clear();
                foreach (int id in m_lastPickables.Keys)
                    if (!m_capturePickableIds.Contains(id)) m_removalScratch.Add(id);
                for (int i = 0; i < m_removalScratch.Count; i++)
                {
                    int id = m_removalScratch[i];
                    PickableStateData last = m_lastPickables[id];
                    m_lastPickables.Remove(id);
                    EnqueuePickable(last.WithAction(EntityActions.Remove).WithCollector(-1));
                }
            }
            for (int i = 0; i < count; i++)
            {
                PickableStateData current = captured[i].WithAction(EntityActions.Add);
                if (!m_lastPickables.TryGetValue(current.Id, out PickableStateData previous))
                    EnqueuePickable(current);
                else if (!current.EqualsState(previous))
                    EnqueuePickable(current.WithAction(EntityActions.Update));
                m_lastPickables[current.Id] = current;
            }
        }

        private void ApplyCapturedProjectiles(List<ProjectileStateData> captured)
        {
            bool truncated = captured.Count > MaxTrackedProjectiles;
            int count = truncated ? MaxTrackedProjectiles : captured.Count;
            if (truncated)
            {
                int dropped = captured.Count - MaxTrackedProjectiles;
                m_counters.ProjectileDropped += dropped;
                LogRateLimited("tracked-projectiles-cap",
                    "主机投射物数量 " + captured.Count + " 超过跟踪上限 " +
                    MaxTrackedProjectiles + " → 本拍忽略 " + dropped + " 条");
            }
            if (!truncated)
            {
                m_captureProjectileIds.Clear();
                for (int i = 0; i < count; i++) m_captureProjectileIds.Add(captured[i].Id);
                m_removalScratch.Clear();
                foreach (int id in m_lastProjectiles.Keys)
                    if (!m_captureProjectileIds.Contains(id)) m_removalScratch.Add(id);
                for (int i = 0; i < m_removalScratch.Count; i++)
                {
                    int id = m_removalScratch[i];
                    ProjectileStateData last = m_lastProjectiles[id];
                    m_lastProjectiles.Remove(id);
                    EnqueueProjectile(last.WithAction(EntityActions.Remove));
                }
            }
            for (int i = 0; i < count; i++)
            {
                ProjectileStateData current = captured[i].WithAction(EntityActions.Add);
                if (!m_lastProjectiles.TryGetValue(current.Id, out ProjectileStateData previous))
                    EnqueueProjectile(current);
                else if (!current.EqualsState(previous))
                    EnqueueProjectile(current.WithAction(EntityActions.Update));
                m_lastProjectiles[current.Id] = current;
            }
        }

        private void FlushPendingStates()
        {
            int messages = 0;
            while (messages < MaxStateMessagesPerPulse)
            {
                m_batchPickableIds.Clear();
                m_batchProjectileIds.Clear();
                var state = BuildStateMessage(m_batchPickableIds, m_batchProjectileIds);
                if (state.Pickables.Count == 0 && state.Projectiles.Count == 0) return;
                byte[] payload = EntityCodec.EncodeStateBytes(state);
                int sent = m_broadcast.BroadcastToPlayers(MessageType.EntityState, payload);
                if (sent <= 0)
                {
                    m_counters.StateSendFailures++;
                    LogRateLimited("state-send",
                        "EntityState 广播没有入队（玩家端 " + m_broadcast.PlayerPeerCount +
                        " 个，可能背压）→ 本拍保留 " + state.Pickables.Count + " 拾取物 + " +
                        state.Projectiles.Count + " 投射物，下一拍重试");
                    return;
                }
                m_counters.StateMessagesSent++;
                m_counters.StateRecordsSent += state.Pickables.Count + state.Projectiles.Count;
                CountSentPickables(state.Pickables);
                CountSentProjectiles(state.Projectiles);
                for (int i = 0; i < m_batchPickableIds.Count; i++)
                    m_pendingPickables.Remove(m_batchPickableIds[i]);
                for (int i = 0; i < m_batchProjectileIds.Count; i++)
                    m_pendingProjectiles.Remove(m_batchProjectileIds[i]);
                messages++;
            }
        }

        private EntityStateData BuildStateMessage(List<int> pickableIds, List<int> projectileIds)
        {
            var state = new EntityStateData(NextStateVersion(), m_serverTickMs);
            int pickableCount = 0;
            // 优先级：REMOVE > ADD > UPDATE（生命周期边沿先到，避免客户端先看到 add 又立刻 remove）。
            foreach (KeyValuePair<int, PickableStateData> item in m_pendingPickables)
            {
                if (pickableCount >= MaxPickableRecordsPerMessage) break;
                if (item.Value.Action != EntityActions.Remove) continue;
                state.Pickables.Add(item.Value);
                pickableIds.Add(item.Key);
                pickableCount++;
            }
            foreach (KeyValuePair<int, PickableStateData> item in m_pendingPickables)
            {
                if (pickableCount >= MaxPickableRecordsPerMessage) break;
                if (item.Value.Action != EntityActions.Add) continue;
                state.Pickables.Add(item.Value);
                pickableIds.Add(item.Key);
                pickableCount++;
            }
            foreach (KeyValuePair<int, PickableStateData> item in m_pendingPickables)
            {
                if (pickableCount >= MaxPickableRecordsPerMessage) break;
                if (item.Value.Action != EntityActions.Update) continue;
                state.Pickables.Add(item.Value);
                pickableIds.Add(item.Key);
                pickableCount++;
            }
            int projectileCount = 0;
            foreach (KeyValuePair<int, ProjectileStateData> item in m_pendingProjectiles)
            {
                if (projectileCount >= MaxProjectileRecordsPerMessage) break;
                if (item.Value.Action != EntityActions.Remove) continue;
                state.Projectiles.Add(item.Value);
                projectileIds.Add(item.Key);
                projectileCount++;
            }
            foreach (KeyValuePair<int, ProjectileStateData> item in m_pendingProjectiles)
            {
                if (projectileCount >= MaxProjectileRecordsPerMessage) break;
                if (item.Value.Action != EntityActions.Add) continue;
                state.Projectiles.Add(item.Value);
                projectileIds.Add(item.Key);
                projectileCount++;
            }
            foreach (KeyValuePair<int, ProjectileStateData> item in m_pendingProjectiles)
            {
                if (projectileCount >= MaxProjectileRecordsPerMessage) break;
                if (item.Value.Action != EntityActions.Update) continue;
                state.Projectiles.Add(item.Value);
                projectileIds.Add(item.Key);
                projectileCount++;
            }
            return state;
        }

        private void FlushSnapshots(double now)
        {
            if (m_pendingSnapshots.Count == 0) return;
            if (!m_snapshotCaptureOk)
            {
                // 有一类实体没抓到：快照会缺项，complete 绝不能为 true。等下一拍。
                return;
            }
            List<int> expired = null;
            foreach (KeyValuePair<int, double> item in m_pendingSnapshots)
            {
                if (now - item.Value <= SnapshotQueueTimeoutSeconds) continue;
                if (expired == null) expired = new List<int>();
                expired.Add(item.Key);
            }
            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                {
                    m_pendingSnapshots.Remove(expired[i]);
                    m_counters.SnapshotSendFailures++;
                    LogRateLimited("snapshot-expired:" + expired[i],
                        "客户端 " + expired[i] + " 的实体快照排队超过 " +
                        SnapshotQueueTimeoutSeconds + "s 仍未发出 → 丢弃排队（它可以用 " +
                        "EntityResyncRequest 重新请求）");
                }
            }
            if (m_pendingSnapshots.Count == 0) return;
            var snapshot = BuildSnapshot();
            byte[] payload = EntityCodec.EncodeSnapshot(snapshot).ToByteArray();
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, double> item in m_pendingSnapshots)
                m_removalScratch.Add(item.Key);
            for (int i = 0; i < m_removalScratch.Count; i++)
            {
                int clientId = m_removalScratch[i];
                if (!m_transport.TrySendTo(clientId, MessageType.EntitySnapshot, payload))
                {
                    m_counters.SnapshotSendFailures++;
                    LogRateLimited("snapshot-send:" + clientId,
                        "给客户端 " + clientId + " 发实体快照失败（通道背压/已断开）→ 保留下次重试");
                    continue;
                }
                m_pendingSnapshots.Remove(clientId);
                m_counters.SnapshotsSent++;
                m_counters.SnapshotRecords += snapshot.Pickables.Count + snapshot.Projectiles.Count;
            }
        }

        private EntitySnapshotData BuildSnapshot()
        {
            bool truncated = m_capturePickables.Count > MaxSnapshotPickables ||
                m_captureProjectiles.Count > MaxSnapshotProjectiles;
            int pickableCount = Math.Min(m_capturePickables.Count, MaxSnapshotPickables);
            int projectileCount = Math.Min(m_captureProjectiles.Count, MaxSnapshotProjectiles);
            var snapshot = new EntitySnapshotData(NextSnapshotId(), m_serverTickMs, !truncated);
            for (int i = 0; i < pickableCount; i++)
                snapshot.Pickables.Add(m_capturePickables[i].WithAction(EntityActions.Add)
                    .WithCollector(-1));
            for (int i = 0; i < projectileCount; i++)
                snapshot.Projectiles.Add(m_captureProjectiles[i].WithAction(EntityActions.Add));
            if (truncated)
            {
                int dropped = m_capturePickables.Count - pickableCount +
                    m_captureProjectiles.Count - projectileCount;
                m_counters.SnapshotDropped += dropped;
                LogRateLimited("snapshot-truncated",
                    "实体快照超过上限（拾取物 " + m_capturePickables.Count + "/" +
                    MaxSnapshotPickables + "，投射物 " + m_captureProjectiles.Count + "/" +
                    MaxSnapshotProjectiles + "）→ 截断 " + dropped +
                    " 条并置 complete=false（客户端不会删除未列出的副本；其余走增量）");
            }
            return snapshot;
        }

        private void EnqueuePickable(PickableStateData state)
        {
            if (m_pendingPickables.Count >= MaxPendingPickables &&
                !m_pendingPickables.ContainsKey(state.Id))
            {
                if (state.Action == EntityActions.Remove)
                {
                    if (!TryEvictPendingPickable(false))
                    {
                        m_counters.PickableDropped++;
                        LogRateLimited("pending-pickables-cap-remove",
                            "待发拾取物队列已满且没有可丢弃的 UPDATE/ADD → 丢弃一条 REMOVE（id=" +
                            state.Id + "）");
                        return;
                    }
                }
                else
                {
                    if (!TryEvictPendingPickable(true))
                    {
                        m_counters.PickableDropped++;
                        LogRateLimited("pending-pickables-cap",
                            "待发拾取物队列已满 → 丢弃一条 " +
                            EntityActions.Name(state.Action) + "（id=" + state.Id + "）");
                        return;
                    }
                }
            }
            m_pendingPickables[state.Id] = state;
        }

        /// <summary>true = 成功腾出一格；updateOnly=true 时只丢 UPDATE（不丢还没发出的 ADD）。</summary>
        private bool TryEvictPendingPickable(bool updateOnly)
        {
            int evict = 0;
            foreach (KeyValuePair<int, PickableStateData> item in m_pendingPickables)
            {
                if (item.Value.Action != EntityActions.Update) continue;
                evict = item.Key;
                break;
            }
            if (evict == 0 && !updateOnly)
            {
                foreach (KeyValuePair<int, PickableStateData> item in m_pendingPickables)
                {
                    if (item.Value.Action != EntityActions.Add) continue;
                    evict = item.Key;
                    break;
                }
            }
            if (evict == 0) return false;
            m_pendingPickables.Remove(evict);
            m_counters.PickableDropped++;
            return true;
        }

        private void EnqueueProjectile(ProjectileStateData state)
        {
            if (m_pendingProjectiles.Count >= MaxPendingProjectiles &&
                !m_pendingProjectiles.ContainsKey(state.Id))
            {
                if (state.Action == EntityActions.Remove)
                {
                    if (!TryEvictPendingProjectile(false))
                    {
                        m_counters.ProjectileDropped++;
                        LogRateLimited("pending-projectiles-cap-remove",
                            "待发投射物队列已满且没有可丢弃的 UPDATE/ADD → 丢弃一条 REMOVE（id=" +
                            state.Id + "）");
                        return;
                    }
                }
                else
                {
                    if (!TryEvictPendingProjectile(true))
                    {
                        m_counters.ProjectileDropped++;
                        LogRateLimited("pending-projectiles-cap",
                            "待发投射物队列已满 → 丢弃一条 " +
                            EntityActions.Name(state.Action) + "（id=" + state.Id + "）");
                        return;
                    }
                }
            }
            m_pendingProjectiles[state.Id] = state;
        }

        private bool TryEvictPendingProjectile(bool updateOnly)
        {
            int evict = 0;
            foreach (KeyValuePair<int, ProjectileStateData> item in m_pendingProjectiles)
            {
                if (item.Value.Action != EntityActions.Update) continue;
                evict = item.Key;
                break;
            }
            if (evict == 0 && !updateOnly)
            {
                foreach (KeyValuePair<int, ProjectileStateData> item in m_pendingProjectiles)
                {
                    if (item.Value.Action != EntityActions.Add) continue;
                    evict = item.Key;
                    break;
                }
            }
            if (evict == 0) return false;
            m_pendingProjectiles.Remove(evict);
            m_counters.ProjectileDropped++;
            return true;
        }

        private void DropPendingStates(string reason)
        {
            int pickables = m_pendingPickables.Count;
            int projectiles = m_pendingProjectiles.Count;
            if (pickables == 0 && projectiles == 0) return;
            m_pendingPickables.Clear();
            m_pendingProjectiles.Clear();
            m_counters.StateDropped += pickables + projectiles;
            LogRateLimited("drop-pending:" + reason,
                "没有在线玩家端 → 丢弃 " + pickables + " 条拾取物 / " + projectiles +
                " 条投射物待发变化（" + reason + "；下次加入用完整快照恢复，状态行计数可审计）");
        }

        private void CountSentPickables(List<PickableStateData> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                switch (records[i].Action)
                {
                    case EntityActions.Add: m_counters.PickableAdd++; break;
                    case EntityActions.Update: m_counters.PickableUpdate++; break;
                    case EntityActions.Remove: m_counters.PickableRemove++; break;
                }
            }
        }

        private void CountSentProjectiles(List<ProjectileStateData> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                switch (records[i].Action)
                {
                    case EntityActions.Add: m_counters.ProjectileAdd++; break;
                    case EntityActions.Update: m_counters.ProjectileUpdate++; break;
                    case EntityActions.Remove: m_counters.ProjectileRemove++; break;
                }
            }
        }

        // ================================================================ 客户端

        private void TickClient(in ModuleTickContext tick)
        {
            m_port.TickClient(tick.DeltaTime);
            double now = tick.Now;
            bool ready = m_port.IsReplicaReady;
            if (ready && !m_clientReady)
            {
                m_clientReady = true;
                RequestResync(now, "replica-ready");
            }
            else if (!ready)
            {
                m_clientReady = false;
            }
            PumpPendingPickups(now);
            if (m_clientReady && now - m_lastEntityMessageMs >= StaleResyncSeconds &&
                (m_clientPickableIds.Count > 0 || m_clientProjectileIds.Count > 0))
            {
                RequestResync(now, "stale");
            }
        }

        private void RequestResync(double now, string reason)
        {
            if (m_hosting || m_transport == null || !m_transport.IsConnected) return;
            if (m_lastResyncRequestMs != 0.0 &&
                now - m_lastResyncRequestMs < ResyncCooldownSeconds) return;
            m_lastResyncRequestMs = now;
            var request = new PE.EntityResyncRequest { Reason = reason ?? string.Empty };
            if (m_transport.TrySend(MessageType.EntityResyncRequest, request.ToByteArray()))
            {
                m_counters.ResyncRequestsSent++;
                Log("请求主机下发完整实体快照（" + reason + "）");
            }
            else
            {
                m_counters.PickupRequestFailures++;
                LogRateLimited("resync-send",
                    "实体快照请求发送失败（通道背压/未连接）→ 下一拍重试");
            }
        }

        private void PumpPendingPickups(double now)
        {
            if (m_pendingPickups.Count == 0) return;
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, PendingPickup> item in m_pendingPickups)
            {
                if (now - item.Value.SentMs <= PickupTimeoutSeconds) continue;
                m_removalScratch.Add(item.Key);
            }
            for (int i = 0; i < m_removalScratch.Count; i++)
            {
                int id = m_removalScratch[i];
                m_pendingPickups.Remove(id);
                m_pickupCooldown[id] = now + PickupRetryCooldownSeconds;
                m_counters.PickupTimeouts++;
                LogRateLimited("pickup-timeout:" + id,
                    "拾取请求 id=" + id + " 超过 " + PickupTimeoutSeconds +
                    "s 没有回执 → 视为超时（稍后重试，不本地删除副本）");
            }
            if (m_pickupCooldown.Count > 256)
            {
                m_removalScratch.Clear();
                foreach (KeyValuePair<int, double> item in m_pickupCooldown)
                    if (item.Value <= now) m_removalScratch.Add(item.Key);
                for (int i = 0; i < m_removalScratch.Count; i++)
                    m_pickupCooldown.Remove(m_removalScratch[i]);
                if (m_pickupCooldown.Count > 512) m_pickupCooldown.Clear();
            }
        }

        private void OnClientPickupSubmitted(PickablePickupCandidate candidate)
        {
            if (!m_attached || m_hosting) return;
            if (m_transport == null || !m_transport.IsConnected || !m_clientReady)
            {
                m_counters.PickupSuppressed++;
                return;
            }
            if (m_pendingPickups.ContainsKey(candidate.PickableId))
            {
                m_counters.PickupSuppressed++;
                return;
            }
            double now = NowSeconds();
            if (m_pickupCooldown.TryGetValue(candidate.PickableId, out double until) && now < until)
            {
                m_counters.PickupSuppressed++;
                return;
            }
            if (m_pendingPickups.Count >= MaxPendingPickups)
            {
                m_counters.PickupSuppressed++;
                LogRateLimited("pickup-pending-cap",
                    "同时待回执的拾取请求已达 " + MaxPendingPickups + " → 暂不请求 id=" +
                    candidate.PickableId);
                return;
            }
            long sequence = ++m_nextPickupSequence;
            if (sequence <= 0) sequence = ++m_nextPickupSequence;
            var request = new PickableAcquireRequestData(sequence, candidate.PickableId,
                candidate.Target);
            byte[] payload = EntityCodec.EncodeRequest(in request).ToByteArray();
            if (!m_transport.TrySend(MessageType.PickableAcquireRequest, payload))
            {
                m_counters.PickupRequestFailures++;
                LogRateLimited("pickup-send",
                    "拾取请求发送失败（通道背压/未连接）：" + request.Describe());
                return;
            }
            m_pendingPickups[candidate.PickableId] = new PendingPickup(sequence, now);
            m_counters.PickupRequestsSent++;
        }

        private void OnPickableAcquireResult(MessageContext context, PE.PickableAcquireResult message)
        {
            if (!m_attached) return;
            if (m_hosting)
            {
                m_counters.PickableRejected++;
                LogRateLimited("result-on-host",
                    "主机收到 PickableAcquireResult（主机不该是接收方）→ 丢弃");
                return;
            }
            if (!EntityCodec.TryDecodeResult(message, out PickableAcquireResultData result,
                    out string error))
            {
                m_counters.PickableRejected++;
                LogRateLimited("result-invalid:" + error,
                    "PickableAcquireResult 载荷非法（" + error + "）→ 整条丢弃");
                return;
            }
            if (!m_pendingPickups.TryGetValue(result.PickableId,
                    out PendingPickup pending) || pending.Sequence != result.Sequence)
            {
                m_counters.PickupResultUnknown++;
                LogRateLimited("result-unknown:" + result.PickableId,
                    "收到未知拾取回执（pickable=" + result.PickableId + " seq=" +
                    result.Sequence + "）→ 丢弃（可能已超时；副本仍由主机状态决定）");
                return;
            }
            m_pendingPickups.Remove(result.PickableId);
            if (!result.Accepted)
            {
                m_counters.PickupRejected++;
                m_pickupCooldown[result.PickableId] = NowSeconds() +
                    PickupRetryCooldownSeconds;
                LogRateLimited("pickup-rejected:" + result.PickableId,
                    "主机拒绝拾取 id=" + result.PickableId + "： " + result.Reason);
                return;
            }
            m_counters.PickupAccepted++;
            if (result.HasInventory) ApplyPickupInventory(result);
            else m_counters.PickupNoInventory++;
            if (!result.Removed) return;
            var removal = new PickableStateData(result.PickableId, EntityActions.Remove, 0,
                result.RemainingCount, Vector3.Zero, Vector3.Zero, false, Vector3.Zero,
                Quaternion.Identity, false, Vector3.Zero, m_transport != null
                    ? m_transport.LocalClientId : 0);
            if (m_port.TryApplyPickable(in removal, out string removeError))
                m_clientPickableIds.Remove(result.PickableId);
            else
                LogRateLimited("pickup-remove:" + result.PickableId,
                    "回执成功后移除本地副本失败（" + removeError + "）→ 等待主机的 EntityState(REMOVE)");
        }

        private void ApplyPickupInventory(PickableAcquireResultData result)
        {
            if (m_transport == null)
            {
                m_counters.PickupInventoryFailures++;
                return;
            }
            int localClientId = m_transport.LocalClientId;
            if (localClientId < 0)
            {
                m_counters.PickupInventoryFailures++;
                LogRateLimited("pickup-inventory-local-id",
                    "拾取回执带背包增量，但本端还没有握手编号 → 无法把增量写进本机背包");
                return;
            }
            PlayerInventoryDelta delta = result.ToInventoryDelta(localClientId, 0);
            if (!m_actionPort.TryApplyInventory(localClientId, delta, out string reason))
            {
                m_counters.PickupInventoryFailures++;
                LogRateLimited("pickup-inventory-apply",
                    "把拾取回执的背包增量写进本机背包失败（" + reason + "）→ 物品仍由主机权威持有，" +
                    "下一次背包同步会对账");
                return;
            }
            m_counters.PickupInventoryApplied++;
        }

        private void OnEntityState(MessageContext context, PE.EntityState message)
        {
            if (!m_attached) return;
            m_counters.StateReceived++;
            if (m_hosting)
            {
                m_counters.ClientStateOnHostRejected++;
                m_counters.StateRejected++;
                LogRateLimited("state-on-host",
                    "主机收到客户端发来的 EntityState（客户端无权发布实体）→ 丢弃");
                return;
            }
            if (!m_clientReady)
            {
                m_counters.StateDropped++;
                return;
            }
            if (!EntityCodec.TryDecodeState(message, out EntityStateData state,
                    out string error))
            {
                m_counters.StateRejected++;
                m_counters.PickableRejected++;
                LogRateLimited("state-invalid:" + error,
                    "EntityState 载荷非法（" + error + "）→ 整条丢弃（不半条应用）");
                return;
            }
            if (!m_orderer.TryAcceptState(state.Version, out string orderReason))
            {
                m_counters.StateDropped++;
                if (orderReason == "stale" || orderReason == "duplicate")
                {
                    // 正常网络乱序：计数即可，不刷日志。
                    return;
                }
                LogRateLimited("state-order:" + orderReason,
                    "EntityState 被排序器拒绝：" + orderReason);
                return;
            }
            ApplyStateRecords(state);
        }

        private void OnEntitySnapshot(MessageContext context, PE.EntitySnapshot message)
        {
            if (!m_attached) return;
            m_counters.SnapshotsReceived++;
            if (m_hosting)
            {
                m_counters.ClientStateOnHostRejected++;
                m_counters.StateRejected++;
                LogRateLimited("snapshot-on-host",
                    "主机收到客户端发来的 EntitySnapshot（客户端无权发布实体）→ 丢弃");
                return;
            }
            if (!m_clientReady)
            {
                m_counters.SnapshotDropped++;
                LogRateLimited("snapshot-not-ready",
                    "收到实体快照时本端世界还没就绪 → 丢弃；世界就绪后会主动请求一次 resync");
                return;
            }
            if (!EntityCodec.TryDecodeSnapshot(message, out EntitySnapshotData snapshot,
                    out string error))
            {
                m_counters.StateRejected++;
                LogRateLimited("snapshot-invalid:" + error,
                    "EntitySnapshot 载荷非法（" + error + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAcceptSnapshot(snapshot.SnapshotId, out string orderReason))
            {
                m_counters.StateDropped++;
                if (orderReason == "snapshot-duplicate") return;
                LogRateLimited("snapshot-order:" + orderReason,
                    "EntitySnapshot 被排序器拒绝：" + orderReason);
                return;
            }
            var seenPickables = new HashSet<int>();
            var seenProjectiles = new HashSet<int>();
            for (int i = 0; i < snapshot.Pickables.Count; i++)
            {
                PickableStateData record = snapshot.Pickables[i];
                seenPickables.Add(record.Id);
                ApplyPickableRecord(record);
            }
            for (int i = 0; i < snapshot.Projectiles.Count; i++)
            {
                ProjectileStateData record = snapshot.Projectiles[i];
                seenProjectiles.Add(record.Id);
                ApplyProjectileRecord(record);
            }
            m_counters.SnapshotRecords += snapshot.Pickables.Count + snapshot.Projectiles.Count;
            if (snapshot.Complete)
            {
                m_removalScratch.Clear();
                foreach (int id in m_clientPickableIds)
                    if (!seenPickables.Contains(id)) m_removalScratch.Add(id);
                for (int i = 0; i < m_removalScratch.Count; i++)
                {
                    int id = m_removalScratch[i];
                    var removal = new PickableStateData(id, EntityActions.Remove, 0, 0,
                        Vector3.Zero, Vector3.Zero, false, Vector3.Zero, Quaternion.Identity,
                        false, Vector3.Zero, -1);
                    if (m_port.TryApplyPickable(in removal, out _))
                    {
                        m_clientPickableIds.Remove(id);
                        m_counters.SnapshotStaleRemoved++;
                    }
                }
                m_removalScratch.Clear();
                foreach (int id in m_clientProjectileIds)
                    if (!seenProjectiles.Contains(id)) m_removalScratch.Add(id);
                for (int i = 0; i < m_removalScratch.Count; i++)
                {
                    int id = m_removalScratch[i];
                    var removal = new ProjectileStateData(id, EntityActions.Remove, 0,
                        Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, -1, false,
                        false, Vector3.Zero);
                    if (m_port.TryApplyProjectile(in removal, out _))
                    {
                        m_clientProjectileIds.Remove(id);
                        m_counters.SnapshotStaleRemoved++;
                    }
                }
            }
            m_lastEntityMessageMs = NowSeconds();
        }

        private void ApplyStateRecords(EntityStateData state)
        {
            for (int i = 0; i < state.Pickables.Count; i++)
                ApplyPickableRecord(state.Pickables[i]);
            for (int i = 0; i < state.Projectiles.Count; i++)
                ApplyProjectileRecord(state.Projectiles[i]);
            m_lastEntityMessageMs = NowSeconds();
        }

        private void ApplyPickableRecord(PickableStateData record)
        {
            m_counters.PickableReceived++;
            if (!m_port.TryApplyPickable(in record, out string reason))
            {
                m_counters.PickableRejected++;
                LogRateLimited("apply-pickable:" + record.Id + ":" + reason,
                    "应用拾取物副本 " + record.Describe() + " 失败：" + reason);
                return;
            }
            m_counters.PickableApplied++;
            if (record.Action == EntityActions.Remove) m_clientPickableIds.Remove(record.Id);
            else m_clientPickableIds.Add(record.Id);
        }

        private void ApplyProjectileRecord(ProjectileStateData record)
        {
            m_counters.ProjectileReceived++;
            if (!m_port.TryApplyProjectile(in record, out string reason))
            {
                m_counters.ProjectileRejected++;
                LogRateLimited("apply-projectile:" + record.Id + ":" + reason,
                    "应用投射物副本 " + record.Describe() + " 失败：" + reason);
                return;
            }
            m_counters.ProjectileApplied++;
            if (record.Action == EntityActions.Remove) m_clientProjectileIds.Remove(record.Id);
            else m_clientProjectileIds.Add(record.Id);
        }

        // ================================================================ 主机：拾取请求

        private void OnPickableAcquireRequest(MessageContext context, PE.PickableAcquireRequest message)
        {
            if (!m_attached) return;
            if (!m_hosting)
            {
                m_counters.PickableRejected++;
                LogRateLimited("request-on-client",
                    "客户端收到 PickableAcquireRequest（客户端不该是接收方）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || m_broadcast == null || !m_broadcast.IsPlayerClient(clientId))
            {
                m_counters.PickableRejected++;
                LogRateLimited("request-peer:" + clientId,
                    "拾取请求来源不是玩家端（clientId=" + clientId + "）→ 拒绝");
                if (clientId > 0) SendPickupRejection(clientId, message, "not-player-peer");
                return;
            }
            if (!EntityCodec.TryDecodeRequest(message, out PickableAcquireRequestData request,
                    out string error))
            {
                m_counters.PickableRejected++;
                LogRateLimited("request-invalid:" + error,
                    "PickableAcquireRequest 载荷非法（" + error + "）→ 整条丢弃");
                // 序号 / id 还能读出来就回一条明确拒绝（客户端不用干等超时）。
                if (message != null && message.Sequence > 0 && message.PickableId > 0)
                    SendPickupRejection(clientId, message, "invalid-payload-" + error);
                return;
            }
            m_counters.PickupRequestsReceived++;
            if (m_pickupHistory.TryGetValue(clientId, out PickupHistory history))
            {
                if (request.Sequence < history.LastSequence)
                {
                    m_counters.PickupDuplicate++;
                    return;
                }
                if (request.Sequence == history.LastSequence)
                {
                    m_counters.PickupDuplicate++;
                    if (history.HasResult &&
                        history.LastResult.PickableId == request.PickableId)
                        SendPickupResult(clientId, history.LastResult);
                    return;
                }
            }
            bool accepted = m_port.TryAcquire(clientId, in request,
                out PickableAcquireOutcome outcome, out string reason);
            PickableAcquireResultData result;
            if (accepted)
            {
                m_inventoryVersion++;
                result = new PickableAcquireResultData(request.Sequence, request.PickableId, true,
                    string.Empty, outcome.RemainingCount, outcome.Removed, outcome.Indices,
                    outcome.Values, outcome.Counts, m_inventoryVersion);
                m_counters.PickupAccepted++;
                PersistPickupInventory(clientId);
                EnqueueAcquireRemoval(request.PickableId, clientId, outcome);
            }
            else
            {
                m_counters.PickupRejected++;
                result = new PickableAcquireResultData(request.Sequence, request.PickableId, false,
                    reason ?? "rejected", 0, false, Array.Empty<int>(), Array.Empty<int>(),
                    Array.Empty<int>(), 0);
                LogRateLimited("pickup-reject:" + clientId + ":" + reason,
                    "拒绝 clientId=" + clientId + " 的拾取请求 " + request.Describe() + "：" + reason);
            }
            if (m_pickupHistory.TryGetValue(clientId, out PickupHistory updated))
            {
                updated.LastSequence = request.Sequence;
                updated.LastResult = result;
                updated.HasResult = true;
            }
            else
            {
                m_pickupHistory[clientId] = new PickupHistory
                {
                    LastSequence = request.Sequence,
                    LastResult = result,
                    HasResult = true
                };
            }
            SendPickupResult(clientId, result);
        }

        /// <summary>
        /// 主机拾取落地后立刻把该玩家端的权威背包写进角色记录（重连 / 加入关键帧的唯一来源）。
        /// 失败必须显式记录：绝不把“拾取成功”当成“已经持久化”。
        /// Source: Engine/AvatarInventoryStore.cs:StoreInventory
        ///         Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPickableProjectileHandlers.cs:376-395
        /// </summary>
        private void PersistPickupInventory(int clientId)
        {
            if (!m_actionPort.TryCaptureInventory(clientId, out int[] values, out int[] counts,
                    out int activeSlot, out string captureReason))
            {
                m_counters.PickupPersistFailures++;
                LogRateLimited("pickup-persist-capture:" + clientId,
                    "拾取成功后读不回 clientId=" + clientId + " 的化身背包（" + captureReason +
                    "）→ 本次不落盘（下一次背包上报仍会落盘）");
                return;
            }
            if (!m_inventoryStore.StoreInventory(clientId, values, counts, activeSlot,
                    m_inventoryVersion, out string storeReason))
            {
                m_counters.PickupPersistFailures++;
                LogRateLimited("pickup-persist-store:" + clientId,
                    "拾取成功后写角色记录失败（clientId=" + clientId + "）：" + storeReason +
                    " → 本次不落盘（下一次背包上报仍会落盘）");
                return;
            }
            m_counters.PickupPersisted++;
        }

        private void SendPickupRejection(int clientId, PE.PickableAcquireRequest message,
            string reason)
        {
            if (message == null || message.Sequence <= 0) return;
            var result = new PickableAcquireResultData(message.Sequence, message.PickableId,
                false, reason, 0, false, Array.Empty<int>(), Array.Empty<int>(),
                Array.Empty<int>(), 0);
            SendPickupResult(clientId, result);
        }

        private void SendPickupResult(int clientId, in PickableAcquireResultData result)
        {
            byte[] payload = EntityCodec.EncodeResult(in result).ToByteArray();
            if (!m_transport.TrySendTo(clientId, MessageType.PickableAcquireResult, payload))
            {
                m_counters.StateSendFailures++;
                LogRateLimited("pickup-result-send:" + clientId,
                    "拾取回执发送失败（clientId=" + clientId + " seq=" + result.Sequence +
                    "）→ 客户端会超时重试（重复序号会重发缓存回执）");
            }
        }

        private void EnqueueAcquireRemoval(int pickableId, int collectorClientId,
            in PickableAcquireOutcome outcome)
        {
            if (!outcome.Removed)
            {
                if (m_lastPickables.TryGetValue(pickableId, out PickableStateData partial))
                {
                    m_lastPickables[pickableId] = partial;
                    EnqueuePickable(partial.WithCount(outcome.RemainingCount)
                        .WithAction(EntityActions.Update).WithCollector(-1));
                }
                return;
            }
            if (m_lastPickables.TryGetValue(pickableId, out PickableStateData last))
            {
                m_lastPickables.Remove(pickableId);
                EnqueuePickable(last.WithAction(EntityActions.Remove)
                    .WithCollector(collectorClientId));
                return;
            }
            var removal = new PickableStateData(pickableId, EntityActions.Remove, 0,
                outcome.RemainingCount, Vector3.Zero, Vector3.Zero, false, Vector3.Zero,
                Quaternion.Identity, false, Vector3.Zero, collectorClientId);
            EnqueuePickable(removal);
        }

        // ================================================================ resync 请求（主机）

        private void OnEntityResyncRequest(MessageContext context, PE.EntityResyncRequest message)
        {
            if (!m_attached) return;
            if (!m_hosting)
            {
                m_counters.StateRejected++;
                LogRateLimited("resync-on-client",
                    "客户端收到 EntityResyncRequest（只有主机处理）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || m_broadcast == null || !m_broadcast.IsPlayerClient(clientId))
            {
                m_counters.StateRejected++;
                LogRateLimited("resync-peer:" + clientId,
                    "实体快照请求来源不是玩家端（clientId=" + clientId + "）→ 拒绝");
                return;
            }
            m_counters.ResyncRequestsReceived++;
            m_pendingSnapshots[clientId] = NowSeconds();
            Log("客户端 " + clientId + " 请求完整实体快照（" +
                (message?.Reason ?? string.Empty) + "）→ 已排队");
        }

        // ================================================================ 工具

        private uint NextStateVersion()
        {
            uint version = ++m_stateVersion;
            if (version == 0) version = ++m_stateVersion;
            return version;
        }

        private uint NextSnapshotId()
        {
            uint id = ++m_snapshotId;
            if (id == 0) id = ++m_snapshotId;
            return id;
        }

        /// <summary>
        /// 模块自己的时间基准（= 最近一次 <see cref="ModuleTickContext.Now"/>）。
        /// 不读 <c>Time.RealTime</c>：自检没有引擎主循环，墙钟与 tick 时间不一致会让
        /// 拾取超时 / 冷却判据失真（生产里两者同源）。
        /// </summary>
        private double NowSeconds() => m_now;

        private void MaybeLogSummary(double now)
        {
            long ms = (long)(now * 1000.0);
            if (ms - m_lastSummaryMs < SummaryLogMs) return;
            m_lastSummaryMs = ms;
            Log(StatusLine());
        }

        private void Log(string message) => m_context?.Diagnostics?.Info(ModuleId, message);

        private void LogRateLimited(string key, string message)
        {
            long nowMs = (long)(m_now * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            if (m_lastProblemMs.Count > 512) m_lastProblemMs.Clear();
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics?.Warn(ModuleId, message);
        }
    }

    /// <summary>
    /// <see cref="PickableStateData"/> 的只读扩展（模块内部用；不改变端口契约）。
    /// </summary>
    internal static class PickableStateDataExtensions
    {
        public static PickableStateData WithCount(this PickableStateData data, int count) =>
            new PickableStateData(data.Id, data.Action, data.Value, count, data.Position,
                data.Velocity, data.HasStuckMatrix, data.StuckPosition, data.StuckRotation,
                data.HasFlyTo, data.FlyTo, data.CollectorClientId);
    }
}

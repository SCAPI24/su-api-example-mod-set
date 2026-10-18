using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

using SuMultiplayer.Protocol;
using PC = SuMultiplayer.Protocol.Creature;

using Google.Protobuf;
namespace SuMultiplayer.Creatures
{
    /// <summary>
    /// 生物同步模块：**主机权威生成 + 按客户端区域分配 + 客户端只做复制**。
    ///
    /// 用户口径（逐条落地）：
    ///   · 动物由主机生成，客户端绝不跑本地随机生成（客户端两个生成子系统被抑制，
    ///     并且每帧把不在主机分配集合里的生物实体清掉）；
    ///   · 主机看到所有端的动物；每个客户端只拿到主机分配给它的那一份；
    ///   · 同一只动物落在两个客户端区域里时，主机同时发给两端 → 两端都能显示。
    ///
    /// 旧实现对照（本模块移植的是同一条链路，不是新发明）：
    ///   · 主机：SuSubsystemSpawn / SuSubsystemCreatureSpawn 照常跑原生生成模型
    ///     （Mod/ScMultiplayer/Func/Subsystem/SuSubsystemSpawn.cs、
    ///       Mod/ScMultiplayer/Func/Subsystem/SuSubsystemCreatureSpawn.cs）；
    ///   · 主机：MaintainRemoteCreatureSpawning 给远端角色补足生物
    ///     （Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:869-971）；
    ///   · 同步：SendWorldObjects 分配网络 id、SendEntityMessage(Add/Remove) +
    ///     BodyUpdateMessage（位置/姿态/速度/生命）
    ///     （Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:SendWorldObjects；
    ///       Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:HandleAnimalBodyUpdate）；
    ///   · 客户端：EnsureRemoteAnimal 创建复制体并摘掉 AI/移动/生命/变形/生成状态机
    ///     （Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:EnsureRemoteAnimal）。
    ///
    /// 没有静态状态；所有状态都在实例字段里。模块只通过端口与引擎交互。
    /// </summary>
    public sealed class CreatureSpawnModule : IMultiplayerModule, ICreatureSender, ISuMPMessageModule
    {
        public sealed class CreatureSpawnOptions
        {
            /// <summary>每个客户端分到的生物区域半径（米；旧实现远端维持用 68）。</summary>
            public float RegionRadius = CreatureAllocation.DefaultRegionRadiusMeters;

            /// <summary>每个客户端最多复制多少只生物（旧实现远端维持上限 26）。</summary>
            public int PerClientLimit = CreatureAllocation.DefaultPerClientLimit;

            /// <summary>
            /// 主机扫描间隔（毫秒）。**20 ms = 50 Hz**：动态档位最快是 62 ms（约 16 Hz），
            /// 扫描必须比它快，否则最高档会被扫描周期吃掉（旧的 100 ms 只能到 10 Hz）。
            /// </summary>
            public int HostPumpIntervalMs = 20;

            /// <summary>
            /// 每个客户端每次扫描最多发多少条记录（Add + Update）。超出部分不丢，留到下一次扫描重试；
            /// 超出数量计入 budgetSkip。默认 64 = <see cref="CreatureSyncCodec.MaxRecordsPerBatch"/>。
            /// </summary>
            public int MaxRecordsPerClientPump = CreatureSyncCodec.MaxRecordsPerBatch;

            /// <summary>客户端清理"不是主机分配来的生物实体"的间隔（毫秒）。</summary>
            public int ClientTrimIntervalMs = 1000;

            /// <summary>客户端请求主机重发全量快照的最小间隔（毫秒）。</summary>
            public int ResyncRequestIntervalMs = 2000;
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly ICreatureWorld m_world;
        private readonly ICreatureRegionSource m_regions;
        private readonly CreatureSpawnOptions m_options;
        private readonly Dictionary<string, long> m_lastLogTimes = new Dictionary<string, long>(StringComparer.Ordinal);

        // 主机：Token（Entity）→ 网络键；网络键由主机分配，客户端只认它。
        private readonly Dictionary<object, int> m_keyByToken =
            new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, HashSet<int>> m_sentByClient = new Dictionary<int, HashSet<int>>();
        private readonly Dictionary<int, Dictionary<int, HostSendState>> m_sendStatesByClient =
            new Dictionary<int, Dictionary<int, HostSendState>>();
        private readonly HashSet<int> m_fullSnapshotPending = new HashSet<int>();
        private readonly Dictionary<int, Vector3> m_clientPositions = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, CreatureStateRecord> m_creaturesByKey =
            new Dictionary<int, CreatureStateRecord>();
        private readonly List<CreatureSyncEntry>[] m_tierBuckets =
        {
            new List<CreatureSyncEntry>(), new List<CreatureSyncEntry>(),
            new List<CreatureSyncEntry>(), new List<CreatureSyncEntry>(),
            new List<CreatureSyncEntry>()
        };
        private readonly List<CreatureStateRecord> m_hostCapture = new List<CreatureStateRecord>();
        private int m_nextKey = 1;

        /// <summary>
        /// 主机：每客户端每只生物的发送节奏（下一次允许发送的时间、上一份已发送值、档位）。
        /// **按 (clientId, key) 分开**：同一只生物对不同客户端的距离不同，档位也必须不同。
        /// </summary>
        private sealed class HostSendState
        {
            public bool HasSent;
            public CreatureSyncTier Tier;
            public long LastSendMs;
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.Identity;
            public Vector3 Velocity;
            public float Health = 1f;
        }

        // 客户端：key → 最近一次权威记录（用于幂等更新 / 全量裁剪）。
        private readonly Dictionary<int, CreatureStateRecord> m_replicas =
            new Dictionary<int, CreatureStateRecord>();

        /// <summary>客户端表现层插值（每帧推进；收到记录只更新锚点，不写引擎位置）。</summary>
        private readonly CreatureReplicaInterpolation m_interpolation = new CreatureReplicaInterpolation();
        private readonly List<CreatureSnapEvent> m_snapEvents = new List<CreatureSnapEvent>();
        private readonly List<int> m_presentationKeys = new List<int>();

        private readonly HashSet<int> m_failedKeys = new HashSet<int>();
        private int m_fullSnapshotId;
        private HashSet<int> m_fullSnapshotKeys;
        private bool m_worldReady;
        private bool m_resyncWhenWorldReady;
        private bool m_snapshotResyncRequired;
        private string m_resyncWhenWorldReadyReason = string.Empty;

        // 客户端：世界没就绪时收到的记录**缓存起来**（不是丢掉）。世界就绪后先按缓存建好复制体，
        // 再请求一次全量刷新位置 —— 否则"世界就绪 → 第一次裁剪"之间的空窗里，世界里一只生物都没有
        // （旧实机现象：计数器有 20 个复制体，桥的 world entities 里 0 个生物）。
        // 缓存有硬上限；溢出/不完整一律显式计数 + 日志，世界就绪后按全量快照重建。
        private const int MaxDeferredRecords = 512;
        private int m_deferredSnapshotId;
        private bool m_deferredSnapshotComplete;
        private readonly Dictionary<int, CreatureStateRecord> m_deferredAdds =
            new Dictionary<int, CreatureStateRecord>();
        private readonly Dictionary<int, CreatureStateRecord> m_deferredUpdates =
            new Dictionary<int, CreatureStateRecord>();
        private readonly HashSet<int> m_deferredRemoves = new HashSet<int>();
        private long m_deferredDropped;
        private long m_deferredApplied;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_replicaClient;
        private bool m_host;
        private double m_now;
        private long m_nextHostPumpMs;
        private long m_lastClientTrimMs;
        private long m_lastResyncRequestMs;
        private int m_serverTick;

        private long m_regionUnavailable;
        private long m_captureFailures;
        private long m_recordsSkipped;
        private long m_addsSent;
        private long m_updatesSent;
        private long m_removesSent;
        private long m_sendFailures;
        private long m_encodeFailures;
        private long m_wrongDirectionFrames;
        private long m_fullSnapshotsSent;
        private long m_clientJoins;
        private long m_clientLeaves;

        private long m_addsApplied;
        private long m_updatesApplied;
        private long m_removesApplied;
        private long m_duplicateAdds;
        private long m_orphanUpdates;
        private long m_orphanUpdatesRecovered;
        private long m_orphanRemoves;
        private long m_createFailures;
        private long m_applyFailures;
        private long m_removeFailures;
        private long m_unassignedTrimmed;
        private long m_decodeFailures;
        private long m_resyncRequests;
        private long m_snapshotRestarts;
        private long m_preWorldBatches;
        private long m_preWorldRecords;
        private long m_worldReadyResyncs;
        private long m_presentationFailures;
        private long m_interpolationSnaps;

        // 主机：档位计数 / 发送原因计数 / 每秒发送率（5 秒状态行里可审计）。
        private readonly long[] m_tierSends = new long[CreatureSyncCadence.TierCount];
        private long m_changeSends;
        private long m_heartbeatSends;
        private long m_stateChangeSends;
        private long m_initialSends;
        private long m_budgetSkipped;
        private long m_rateWindowStartMs = -1;
        private long m_rateWindowSends;
        private long m_lastSendRatePerSecond;

        public CreatureSpawnModule(ICreatureWorld world, ICreatureRegionSource regions,
            CreatureSpawnOptions options = null)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_regions = regions;   // 客户端不需要区域来源（只复制主机记录），允许为 null。
            m_options = options ?? new CreatureSpawnOptions();
            m_descriptor = new ModuleDescriptor("creature-spawn", "生物生成与同步")
            {
                RequiresCapabilities = new[] { "transport" },
                PreferredCapabilities = new[] { "player.avatars" },
                ProvidesCapabilities = new[] { "creature.sync" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        /// <summary>生物记录 / 全量重发请求走注册表（与其它消息同一条强类型分发路径）。</summary>
        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.CreatureState, PC.CreatureState.Parser,
                new DelegateMessageHandler<PC.CreatureState>(OnCreatureState), ModuleId);
            registry.Register((int)MessageType.CreatureResyncRequest, PC.CreatureResyncRequest.Parser,
                new DelegateMessageHandler<PC.CreatureResyncRequest>(OnCreatureResyncRequest), ModuleId);
        }

        private void OnCreatureState(MessageContext context, PC.CreatureState message)
        {
            bool clientMode = !m_host && m_transport != null && m_transport.IsConnected;
            if (!clientMode)
            {
                m_wrongDirectionFrames++;
                LogThrottled("record-on-host", "主机收到了生物记录帧（方向错误）→ 丢弃（客户端不应发送这类帧）", 5000);
                return;
            }
            ApplyBatch(message);
        }

        private void OnCreatureResyncRequest(MessageContext context, PC.CreatureResyncRequest message)
        {
            // 主机侧才会收到这个方向；不依赖"本帧 tick 已经把 m_host 置位"，
            // 用"不是复制客户端"判定，避免第一帧之前到达的请求被丢掉。
            if (m_replicaClient) return;
            m_fullSnapshotPending.Add(context.ClientId);
            LogThrottled("resync-req:" + context.ClientId, "clientId=" + context.ClientId +
                " 请求生物全量重发（" + (message.Reason ?? string.Empty) + "）→ 下一次扫描给它发全量快照", 2000);
        }

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport))
            {
                Error("没有拿到传输端口（transport 能力缺失）→ 生物不会同步；" +
                    "主机仍按原生模型生成，客户端会明确抑制本地生成但收不到复制记录");
                return;
            }
            if (context.TryGetPort(out IMessageRegistryPort messages) && messages != null)
            {
                m_messages = messages;
                messages.Registry.RegisterModule(this);
            }
            else
            {
                Warn("没有拿到 IMessageRegistryPort → 生物记录收不到（请组合根注册 IMessageRegistryPort）");
            }
            m_transport.ClientJoined += HandleClientJoined;
            m_transport.ClientLeft += HandleClientLeft;
            m_attached = true;
            // 只用于"复制体是否真的落在本机玩家周围"的可见性对账（不改变主机 68m/26 的分配规则）。
            m_world.ConfigureClientRegion(m_options.RegionRadius);
            Info("生物同步模块就绪：主机权威生成 · 每客户端 " + m_options.PerClientLimit +
                " 只 / " + m_options.RegionRadius + " 米区域 · 客户端只做复制（不跑本地随机生成）");
            if (m_regions == null)
            {
                Warn("没有接上客户端区域来源（联机角色模块缺失）→ 主机不知道玩家端位置，不会给任何客户端分配生物" +
                    "（不按区块中心或 index 猜位置）");
            }
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_now = tick.Now;
            m_host = tick.IsHost;
            bool connected = m_transport != null && m_transport.IsConnected;
            m_replicaClient = !m_host && connected;
            if (!m_world.BeginTick(m_host, m_replicaClient, m_now, out string worldReason))
            {
                m_worldReady = false;
                if (!m_host && m_replicas.Count > 0)
                {
                    // Project 已经卸载/换新：实体随旧 Project 一起销毁，这里只丢引用。
                    // 世界重新就绪后会请求一次全量快照（不在旧记录上继续插值）。
                    ForgetClientReplicas("世界不可用（Project 为空/已卸载），清掉旧世界的复制体引用");
                }
                LogThrottled("world-unavailable", "生物世界当前不可操作：" + worldReason, 5000, error: false);
                return;
            }
            m_worldReady = true;
            if (m_host)
            {
                TickHost();
                return;
            }
            if (m_replicaClient)
            {
                // 世界就绪的第一帧先把"世界传输期间收到的记录"落成实体（缓存的那份不是丢掉）。
                // 顺序很关键：创建 → 再请求全量刷新 → 再跑 TickClient 的裁剪，
                // 这样裁剪看到的 m_replicas 已经包含这些键，不会把刚建好的复制体当"未分配"清掉。
                ApplyDeferredBatches();
                if (m_resyncWhenWorldReady)
                {
                    m_resyncWhenWorldReady = false;
                    m_worldReadyResyncs++;
                    // 世界就绪后重拿一次全量：缓存里的位置可能已经过期（世界传输可能持续几十秒），
                    // 但实体已经先按缓存建好，所以不会出现"世界里一只都没有"的空窗。
                    SendResyncRequest(m_resyncWhenWorldReadyReason, force: true);
                }
                TickClient(tick.DeltaTime);
                return;
            }
            if (m_replicas.Count > 0)
            {
                ClearClientReplicas("不在联机会话（通道未连接），清掉上一次会话的复制体");
            }
        }

        // ------------------------------------------------------------------ 主机

        private void TickHost()
        {
            long nowMs = (long)(m_now * 1000.0);
            if (nowMs - m_nextHostPumpMs < m_options.HostPumpIntervalMs) return;
            m_nextHostPumpMs = nowMs;
            m_serverTick++;

            List<CreatureRegion> regions = BuildRegions(out HashSet<int> unavailable);
            // 旧实现的远端维持先跑：它会在远端角色周围补足生物，随后同一帧的主体枚举就能看到它们。
            m_world.MaintainHostPopulation(regions, m_now, out string maintainReason);
            if (!string.IsNullOrEmpty(maintainReason))
            {
                LogThrottled("maintain:" + maintainReason, "主机远端生物维持未执行：" + maintainReason, 5000);
            }

            // 复用同一批记录对象（50 Hz 扫描下不制造垃圾）；适配器负责按本次枚举结果覆盖并截断。
            List<CreatureStateRecord> creatures = m_hostCapture;
            int captured = m_world.CaptureHostCreatures(creatures, out string captureReason);
            if (captured < 0)
            {
                m_captureFailures++;
                LogThrottled("capture-failed", "主机枚举生物失败（本帧不下发、不猜）：" + captureReason, 5000, error: true);
                return;
            }

            SynchronizeKeys(creatures);
            Dictionary<int, List<int>> assignments = CreatureAllocation.Compute(
                creatures, regions, m_options.RegionRadius, m_options.PerClientLimit);

            var clients = new HashSet<int>(m_sentByClient.Keys);
            m_clientPositions.Clear();
            for (int i = 0; i < regions.Count; i++)
            {
                clients.Add(regions[i].ClientId);
                m_clientPositions[regions[i].ClientId] = regions[i].Position;
            }
            m_creaturesByKey.Clear();
            for (int i = 0; i < creatures.Count; i++)
            {
                CreatureStateRecord record = creatures[i];
                if (record == null || record.Key == 0) continue;
                m_creaturesByKey[record.Key] = record;
            }
            UpdateSendRate(nowMs);

            foreach (int clientId in clients)
            {
                if (unavailable.Contains(clientId))
                {
                    // 位置未知：保留上一次已发送的集合，不新增也不删除（避免位置抖动引起整片重建）。
                    continue;
                }
                assignments.TryGetValue(clientId, out List<int> desiredList);
                m_clientPositions.TryGetValue(clientId, out Vector3 clientPosition);
                var desired = new HashSet<int>(desiredList ?? new List<int>());
                m_sentByClient.TryGetValue(clientId, out HashSet<int> confirmed);
                // 主机"认为客户端已经有的 key"：只有真正发送成功的才算（失败下一帧重试，
                // 新 key 仍然走 Add —— 绝不能因为发失败就改发 Update）。
                var known = confirmed == null ? new HashSet<int>() : new HashSet<int>(confirmed);

                if (m_fullSnapshotPending.Remove(clientId))
                {
                    if (SendFullSnapshot(clientId, desiredList ?? new List<int>(), creatures))
                    {
                        m_fullSnapshotsSent++;
                        // 全量快照就是一次完整状态：按快照时刻重新初始化每只生物的发送节奏，
                        // 避免快照刚发完又因为"档位变化"整批补发一遍。
                        ResetSendStates(clientId, desiredList ?? new List<int>(), clientPosition, nowMs);
                        known = desired;
                        m_sentByClient[clientId] = known;
                    }
                    else
                    {
                        m_fullSnapshotPending.Add(clientId);   // 发送失败：下一次继续发全量
                    }
                    continue;
                }

                var removes = new List<CreatureSyncEntry>();
                foreach (int key in known)
                {
                    if (!desired.Contains(key))
                    {
                        removes.Add(new CreatureSyncEntry(CreatureSyncAction.Remove,
                            new CreatureStateRecord { Key = key }));
                    }
                }
                if (removes.Count > 0)
                {
                    if (!SendEntries(clientId, removes, fullSnapshot: false))
                    {
                        // 发送失败：保留旧的已发送集合，下一帧重试（客户端对重复 Remove 幂等）。
                        continue;
                    }
                    m_removesSent += removes.Count;
                    for (int i = 0; i < removes.Count; i++)
                    {
                        known.Remove(removes[i].Record.Key);
                        RemoveSendState(clientId, removes[i].Record.Key);
                    }
                    // 删除已经发出：即使随后的更新发送失败，也必须记住这些 key 已经不在客户端，
                    // 否则下一拍会重发一次 Remove（客户端只会多记一次 orphanRemove）。
                    m_sentByClient[clientId] = known;
                }

                List<CreatureSyncEntry> upserts = PlanClientUpserts(clientId, desiredList, known,
                    clientPosition, nowMs);
                if (upserts.Count > 0)
                {
                    if (!SendEntries(clientId, upserts, fullSnapshot: false)) continue;
                    CommitSentEntries(clientId, upserts, clientPosition, nowMs);
                    for (int i = 0; i < upserts.Count; i++) known.Add(upserts[i].Record.Key);
                }
                m_sentByClient[clientId] = known;
            }
        }

        /// <summary>
        /// 动态频率的核心：对每个客户端的每只生物，先算档位（距离 + 状态），再决定这一拍发不发：
        ///   · 新 key → 立即 Add（含模板名，客户端据此创建复制体）；
        ///   · 档位/状态变化 → 立即发（不必等心跳，攻击/逃跑/受伤要马上反映）；
        ///   · 值变化超过 epsilon 且已过本档最小间隔 → 发（运动更新）；
        ///   · 值没变化但心跳间隔到了 → 发（保持客户端插值锚点，不让远处/静止生物冻结）。
        /// 预算：按档位从高到低收，最多 <see cref="CreatureSpawnOptions.MaxRecordsPerClientPump"/> 条；
        /// 超出的**不丢**（不推进发送状态），下一拍重试，并计入 budgetSkip。
        /// </summary>
        private List<CreatureSyncEntry> PlanClientUpserts(int clientId, List<int> desiredList,
            HashSet<int> known, Vector3 clientPosition, long nowMs)
        {
            for (int tier = 0; tier < CreatureSyncCadence.TierCount; tier++) m_tierBuckets[tier].Clear();
            Dictionary<int, HostSendState> states = GetSendStates(clientId);
            if (desiredList != null)
            {
                for (int i = 0; i < desiredList.Count; i++)
                {
                    int key = desiredList[i];
                    if (!m_creaturesByKey.TryGetValue(key, out CreatureStateRecord record)) continue;
                    if (!states.TryGetValue(key, out HostSendState state))
                    {
                        state = new HostSendState();
                        states.Add(key, state);
                    }
                    CreatureSyncTier tier = CreatureSyncCadence.SelectTier(record.Activity,
                        CreatureSyncCadence.HorizontalDistanceSquared(record.Position, clientPosition));
                    bool isNew = !known.Contains(key);
                    bool tierChanged = state.HasSent && state.Tier != tier;
                    bool changed = state.HasSent && CreatureSyncCadence.ValuesDiffer(
                        state.Position, state.Rotation, state.Velocity, state.Health, record);
                    bool changeDue = !state.HasSent ||
                        nowMs - state.LastSendMs >= CreatureSyncCadence.IntervalMs(tier);
                    bool heartbeatDue = !state.HasSent || nowMs - state.LastSendMs >=
                        Math.Max(CreatureSyncCadence.IntervalMs(tier),
                            CreatureSyncCadence.HeartbeatIntervalMs);
                    if (!isNew && !tierChanged && !(changed && changeDue) && !heartbeatDue) continue;
                    m_tierBuckets[(int)tier].Add(new CreatureSyncEntry(
                        isNew ? CreatureSyncAction.Add : CreatureSyncAction.Update, record));
                }
            }

            int budget = Math.Max(1, m_options.MaxRecordsPerClientPump);
            var result = new List<CreatureSyncEntry>(Math.Min(budget, desiredList?.Count ?? 0));
            for (int tier = CreatureSyncCadence.TierCount - 1; tier >= 0; tier--)
            {
                List<CreatureSyncEntry> bucket = m_tierBuckets[tier];
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (result.Count >= budget)
                    {
                        m_budgetSkipped += bucket.Count - i;
                        break;
                    }
                    result.Add(bucket[i]);
                }
            }
            return result;
        }

        /// <summary>发送成功后推进节奏状态：记录"最后发送值 + 档位 + 时间"，并做计数。</summary>
        private void CommitSentEntries(int clientId, List<CreatureSyncEntry> entries,
            Vector3 clientPosition, long nowMs)
        {
            Dictionary<int, HostSendState> states = GetSendStates(clientId);
            for (int i = 0; i < entries.Count; i++)
            {
                CreatureSyncEntry entry = entries[i];
                CreatureStateRecord record = entry.Record;
                if (!states.TryGetValue(record.Key, out HostSendState state))
                {
                    state = new HostSendState();
                    states.Add(record.Key, state);
                }
                CreatureSyncTier tier = CreatureSyncCadence.SelectTier(record.Activity,
                    CreatureSyncCadence.HorizontalDistanceSquared(record.Position, clientPosition));
                bool changed = state.HasSent && CreatureSyncCadence.ValuesDiffer(
                    state.Position, state.Rotation, state.Velocity, state.Health, record);
                if (entry.Action == CreatureSyncAction.Add)
                {
                    m_addsSent++;
                    if (!state.HasSent) m_initialSends++;
                }
                else
                {
                    m_updatesSent++;
                    if (!state.HasSent) m_initialSends++;
                    else if (state.Tier != tier) m_stateChangeSends++;
                    else if (changed) m_changeSends++;
                    else m_heartbeatSends++;
                }
                m_tierSends[(int)tier]++;
                m_rateWindowSends++;
                state.HasSent = true;
                state.Tier = tier;
                state.LastSendMs = nowMs;
                state.Position = record.Position;
                state.Rotation = record.Rotation;
                state.Velocity = record.Velocity;
                state.Health = record.Health;
            }
        }

        /// <summary>全量快照发送成功后：每只生物按快照时刻重置节奏（下一次按档位/心跳到点再发）。</summary>
        private void ResetSendStates(int clientId, List<int> desiredList, Vector3 clientPosition,
            long nowMs)
        {
            Dictionary<int, HostSendState> states = GetSendStates(clientId);
            states.Clear();
            if (desiredList == null) return;
            for (int i = 0; i < desiredList.Count; i++)
            {
                if (!m_creaturesByKey.TryGetValue(desiredList[i], out CreatureStateRecord record))
                    continue;
                CreatureSyncTier tier = CreatureSyncCadence.SelectTier(record.Activity,
                    CreatureSyncCadence.HorizontalDistanceSquared(record.Position, clientPosition));
                states[record.Key] = new HostSendState
                {
                    HasSent = true,
                    Tier = tier,
                    LastSendMs = nowMs,
                    Position = record.Position,
                    Rotation = record.Rotation,
                    Velocity = record.Velocity,
                    Health = record.Health
                };
            }
        }

        private Dictionary<int, HostSendState> GetSendStates(int clientId)
        {
            if (!m_sendStatesByClient.TryGetValue(clientId, out Dictionary<int, HostSendState> states))
            {
                states = new Dictionary<int, HostSendState>();
                m_sendStatesByClient.Add(clientId, states);
            }
            return states;
        }

        private void RemoveSendState(int clientId, int key)
        {
            if (m_sendStatesByClient.TryGetValue(clientId, out Dictionary<int, HostSendState> states))
                states.Remove(key);
        }

        /// <summary>每秒发送条数（给状态行用；5 秒一行日志里能直接看节奏是否正确）。</summary>
        private void UpdateSendRate(long nowMs)
        {
            if (m_rateWindowStartMs < 0)
            {
                m_rateWindowStartMs = nowMs;
                return;
            }
            long elapsed = nowMs - m_rateWindowStartMs;
            if (elapsed < 1000) return;
            m_lastSendRatePerSecond = m_rateWindowSends * 1000 / Math.Max(1, elapsed);
            m_rateWindowStartMs = nowMs;
            m_rateWindowSends = 0;
        }

        private List<CreatureRegion> BuildRegions(out HashSet<int> unavailable)
        {
            var regions = new List<CreatureRegion>();
            unavailable = new HashSet<int>();
            if (m_regions == null) return regions;
            IReadOnlyList<int> clientIds;
            try
            {
                clientIds = m_regions.ClientIds;
            }
            catch (Exception e)
            {
                m_regionUnavailable++;
                LogThrottled("region-list-failed", "读取客户端列表失败：" + e.GetType().Name + " " + e.Message, 5000, error: true);
                return regions;
            }
            if (clientIds == null) return regions;
            for (int i = 0; i < clientIds.Count; i++)
            {
                int clientId = clientIds[i];
                if (!m_regions.TryGetClientPosition(clientId, out Vector3 position, out string reason))
                {
                    unavailable.Add(clientId);
                    m_regionUnavailable++;
                    LogThrottled("region:" + clientId, "clientId=" + clientId +
                        " 的联机角色位置未知（" + reason + "）→ 本帧保留它上一次的分配，不新增也不删除", 5000);
                    continue;
                }
                regions.Add(new CreatureRegion(clientId, position, m_options.RegionRadius));
            }
            return regions;
        }

        private void SynchronizeKeys(List<CreatureStateRecord> creatures)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < creatures.Count; i++)
            {
                CreatureStateRecord record = creatures[i];
                if (record == null) continue;
                object token = record.Token;
                if (token == null)
                {
                    m_recordsSkipped++;
                    LogThrottled("record-no-token", "主机生物记录缺少身份令牌（模板 " +
                        (record.TemplateName ?? string.Empty) + "）→ 跳过它（不伪造网络 id）", 5000, error: true);
                    continue;
                }
                if (!m_keyByToken.TryGetValue(token, out int key))
                {
                    key = m_nextKey++;
                    m_keyByToken.Add(token, key);
                }
                record.Key = key;
                seen.Add(token);
            }
            var removedTokens = new List<object>();
            foreach (KeyValuePair<object, int> pair in m_keyByToken)
            {
                if (!seen.Contains(pair.Key)) removedTokens.Add(pair.Key);
            }
            for (int i = 0; i < removedTokens.Count; i++) m_keyByToken.Remove(removedTokens[i]);
        }

        private static CreatureStateRecord FindByKey(List<CreatureStateRecord> creatures, int key)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                if (creatures[i] != null && creatures[i].Key == key) return creatures[i];
            }
            return null;
        }

        private bool SendFullSnapshot(int clientId, List<int> keys, List<CreatureStateRecord> creatures)
        {
            var entries = new List<CreatureSyncEntry>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                CreatureStateRecord record = FindByKey(creatures, keys[i]);
                if (record == null) continue;
                entries.Add(new CreatureSyncEntry(CreatureSyncAction.Add, record));
            }
            return SendEntries(clientId, entries, fullSnapshot: true);
        }

        private bool SendEntries(int clientId, List<CreatureSyncEntry> entries, bool fullSnapshot)
        {
            if (!fullSnapshot && (entries == null || entries.Count == 0)) return true;
            int snapshotId = fullSnapshot ? ++m_snapshotSequence : 0;
            int offset = 0;
            bool allSent = true;
            do
            {
                int count = entries == null ? 0 : Math.Min(CreatureSyncCodec.MaxRecordsPerBatch,
                    entries.Count - offset);
                var chunk = new List<CreatureSyncEntry>(count);
                for (int i = 0; i < count; i++) chunk.Add(entries[offset + i]);
                bool last = entries == null || offset + count >= entries.Count;
                byte flags = 0;
                if (fullSnapshot) flags |= CreatureSyncCodec.FlagFullSnapshot;
                if (last) flags |= CreatureSyncCodec.FlagLastBatch;
                byte[] payload;
                try
                {
                    payload = CreatureSyncCodec.Encode(chunk, m_serverTick, flags, snapshotId);
                }
                catch (Exception e)
                {
                    m_encodeFailures++;
                    LogThrottled("encode-failed", "生物同步载荷编码失败（本批不发）：" +
                        e.GetType().Name + " " + e.Message, 5000, error: true);
                    return false;
                }
                if (!TrySendTo(clientId, payload))
                {
                    m_sendFailures++;
                    LogThrottled("send:" + clientId, "发给 clientId=" + clientId +
                        " 的生物记录发送失败（背压/连接不可用）→ 下一帧重试，不假装已发送", 3000, error: true);
                    allSent = false;
                    break;
                }
                if (entries == null || entries.Count == 0) break;
                offset += count;
            } while (offset < entries.Count);
            return allSent;
        }

        private int m_snapshotSequence;

        public bool TrySendTo(int clientId, byte[] payload) =>
            m_transport != null && m_transport.TrySendTo(clientId, MessageType.CreatureState, payload);

        // ------------------------------------------------------------------ 客户端

        private void TickClient(float deltaTime)
        {
            long nowMs = (long)(m_now * 1000.0);
            // 插值/外推**每帧**推进（不是每 100 ms）：这是客户端看起来平滑的唯一来源。
            AdvanceReplicaPresentations(deltaTime);
            if (nowMs - m_lastClientTrimMs < m_options.ClientTrimIntervalMs) return;
            m_lastClientTrimMs = nowMs;
            int removed = m_world.TrimReplicasTo(new HashSet<int>(m_replicas.Keys), out string reason);
            if (removed > 0) m_unassignedTrimmed += removed;
            if (removed < 0)
            {
                LogThrottled("trim-failed", "清理非主机分配的生物失败：" + reason, 5000, error: true);
                return;
            }
            // 不再保留的复制体也丢掉表现状态（否则残留的 key 会一直占着插值表）。
            m_interpolation.CopyKeys(m_presentationKeys);
            for (int i = 0; i < m_presentationKeys.Count; i++)
            {
                if (!m_replicas.ContainsKey(m_presentationKeys[i]))
                    m_interpolation.Remove(m_presentationKeys[i]);
            }
        }

        /// <summary>
        /// 把插值器的表现位置/姿态/速度写进复制体。实体消失（模板重建 / 被清掉）→ 计数、
        /// 丢掉引用并请求全量，绝不留下一个卡住的复制体。
        /// </summary>
        private void AdvanceReplicaPresentations(float deltaTime)
        {
            if (m_replicas.Count == 0) return;
            m_snapEvents.Clear();
            m_interpolation.Advance(m_now, deltaTime, m_snapEvents);
            for (int i = 0; i < m_snapEvents.Count; i++)
            {
                CreatureSnapEvent snap = m_snapEvents[i];
                m_interpolationSnaps++;
                LogThrottled("interp-snap:" + snap.Key, "生物插值误差 " +
                    snap.ErrorMeters.ToString("0.##") + " m 超过对齐阈值 " +
                    CreatureReplicaInterpolation.SnapDistanceMeters + " m → key=" + snap.Key +
                    " 直接对齐一次（丢包过久 / 传送 / 模板重建）", 5000);
            }
            m_interpolation.CopyKeys(m_presentationKeys);
            for (int i = 0; i < m_presentationKeys.Count; i++)
            {
                int key = m_presentationKeys[i];
                if (!m_replicas.ContainsKey(key)) continue;
                if (!m_interpolation.TryGetPresentation(key, out Vector3 position,
                    out Quaternion rotation, out Vector3 velocity)) continue;
                if (m_world.TryApplyReplicaPresentation(key, position, rotation, velocity,
                    out string reason)) continue;
                m_presentationFailures++;
                m_replicas.Remove(key);
                m_interpolation.Remove(key);
                LogThrottled("presentation-failed:" + key, "施加生物插值失败 key=" + key + "：" +
                    reason + " → 丢掉本地引用并请求主机重发全量", 5000, error: true);
                RequestResync("施加插值失败 key=" + key);
            }
        }

        private void ApplyBatch(PC.CreatureState message)
        {
            if (!CreatureSyncCodec.TryConvert(message, out CreatureSyncCodec.CreatureBatch batch, out string reason))
            {
                m_decodeFailures++;
                LogThrottled("decode-failed", "生物记录解码失败（整批丢弃）：" + reason, 5000, error: true);
                RequestResync("解码失败");
                return;
            }
            // 世界传输 / 导入期间会先收到主机按"加入"发的全量快照：这时 Project 还不存在，
            // 逐条创建只会得到 "当前没有世界"（实机 createFail=50），随后这些 key 的更新
            // 又变成 orphanUpdate=550。整批延后 + 世界就绪后重拿一次全量，才是正确路径。
            if (!m_worldReady || !m_world.HasWorld)
            {
                DeferBatch(batch, m_worldReady
                    ? "世界当前不可操作（Project 已卸载/换新）"
                    : "世界还没加载完（世界传输/导入中）");
                return;
            }
            if (batch.IsFullSnapshot)
            {
                // 实时全量快照到了 ⇒ 缓存的旧快照被取代（否则世界就绪时会拿旧记录去建实体）。
                if (HasDeferredRecords())
                {
                    bool sameSnapshot = m_deferredSnapshotId != 0 &&
                        batch.SnapshotId == m_deferredSnapshotId;
                    ClearDeferredRecords("实时全量快照已到达（缓存的旧快照被取代）");
                    if (sameSnapshot)
                    {
                        // 世界就绪发生在快照中途：前几批被缓存、这一批是实时应用 ⇒ 这份快照在
                        // 客户端不完整。落地完这一份之后必须请求一次完整重发（明确计数，不假装完整）。
                        m_snapshotResyncRequired = true;
                    }
                }
                if (batch.SnapshotId != m_fullSnapshotId)
                {
                    if (m_fullSnapshotId != 0) m_snapshotRestarts++;
                    m_fullSnapshotId = batch.SnapshotId;
                    m_fullSnapshotKeys = new HashSet<int>();
                }
                if (m_fullSnapshotKeys == null) m_fullSnapshotKeys = new HashSet<int>();
            }

            for (int i = 0; i < batch.Entries.Count; i++)
            {
                CreatureSyncEntry entry = batch.Entries[i];
                CreatureStateRecord record = entry.Record;
                bool applied = true;
                switch (entry.Action)
                {
                    case CreatureSyncAction.Add:
                        applied = ApplyAdd(record);
                        break;
                    case CreatureSyncAction.Update:
                        ApplyUpdate(record);
                        break;
                    case CreatureSyncAction.Remove:
                        ApplyRemove(record.Key);
                        break;
                }
                if (batch.IsFullSnapshot && applied) m_fullSnapshotKeys.Add(record.Key);
            }

            if (batch.IsFullSnapshot && batch.IsLastBatch)
            {
                int trimmed = m_world.TrimReplicasTo(m_fullSnapshotKeys, out string trimReason);
                if (trimmed > 0) m_unassignedTrimmed += trimmed;
                if (trimmed < 0)
                {
                    LogThrottled("snapshot-trim-failed", "全量快照后清理失败：" + trimReason, 5000, error: true);
                }
                foreach (int key in new List<int>(m_replicas.Keys))
                {
                    if (m_fullSnapshotKeys.Contains(key)) continue;
                    m_replicas.Remove(key);
                    m_interpolation.Remove(key);
                }
                m_fullSnapshotId = 0;
                m_fullSnapshotKeys = null;
                if (m_snapshotResyncRequired)
                {
                    m_snapshotResyncRequired = false;
                    RequestResync("全量快照有批次在世界就绪前被缓存过，需要完整重发一次");
                }
            }
        }

        /// <summary>
        /// 世界没就绪时整批**缓存**（明确计数 + 限流原因），世界就绪后先按它建实体、再重拿一次全量。
        /// 绝不逐条创建（旧实机 createFail=50），也绝不静默丢弃（那会留下"世界里一只都没有"的空窗）。
        /// </summary>
        private void DeferBatch(CreatureSyncCodec.CreatureBatch batch, string reason)
        {
            m_preWorldBatches++;
            m_preWorldRecords += batch.Entries.Count;
            m_resyncWhenWorldReady = true;
            m_resyncWhenWorldReadyReason = reason;
            if (batch.IsFullSnapshot)
            {
                // 新快照 ⇒ 旧缓存作废（快照 id 变了说明主机重新发了一份完整状态）。
                if (batch.SnapshotId != m_deferredSnapshotId)
                {
                    ClearDeferredRecords("开始缓存新的全量快照 id=" + batch.SnapshotId);
                    m_deferredSnapshotId = batch.SnapshotId;
                }
                AppendDeferred(batch.Entries);
                if (batch.IsLastBatch) m_deferredSnapshotComplete = true;
            }
            else
            {
                AppendDeferred(batch.Entries);
            }
            LogThrottled("pre-world-batch", "收到生物记录批次（" + batch.Entries.Count +
                " 条 / 全量=" + batch.IsFullSnapshot + "）但" + reason +
                " → 整批缓存，等世界就绪后落地（已累计 " + m_preWorldBatches + " 批 / " +
                m_preWorldRecords + " 条；缓存 " + DeferredRecordCount() + " 条）；世界就绪后还会重拿一次全量刷新位置", 5000);
        }

        private int DeferredRecordCount() =>
            m_deferredAdds.Count + m_deferredUpdates.Count + m_deferredRemoves.Count;

        private bool HasDeferredRecords() => DeferredRecordCount() > 0;

        private void ClearDeferredRecords(string reason)
        {
            int count = DeferredRecordCount();
            m_deferredAdds.Clear();
            m_deferredUpdates.Clear();
            m_deferredRemoves.Clear();
            m_deferredSnapshotId = 0;
            m_deferredSnapshotComplete = false;
            if (count > 0)
            {
                Info("清掉缓存中的 " + count + " 条生物记录（" + reason + "）");
            }
        }

        private void AppendDeferred(List<CreatureSyncEntry> entries)
        {
            int count = entries?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                CreatureSyncEntry entry = entries[i];
                CreatureStateRecord record = entry.Record;
                if (record == null) continue;
                if (DeferredRecordCount() >= MaxDeferredRecords)
                {
                    // 溢出：显式丢弃整份缓存（世界就绪后靠全量快照重建），绝不静默截断。
                    m_deferredDropped += DeferredRecordCount();
                    int remaining = count - i;
                    m_deferredDropped += remaining;
                    ClearDeferredRecords("缓存超过上限 " + MaxDeferredRecords + " 条");
                    m_resyncWhenWorldReady = true;
                    m_resyncWhenWorldReadyReason = "世界就绪前的生物记录缓存溢出（已丢弃 " +
                        m_deferredDropped + " 条）";
                    LogThrottled("deferred-overflow", "世界就绪前缓存的生物记录超过上限 " +
                        MaxDeferredRecords + " 条 → 丢掉缓存（世界就绪后按全量快照重建），累计丢弃 " +
                        m_deferredDropped + " 条", 5000, error: true);
                    return;
                }
                switch (entry.Action)
                {
                    case CreatureSyncAction.Remove:
                        m_deferredAdds.Remove(record.Key);
                        m_deferredUpdates.Remove(record.Key);
                        m_deferredRemoves.Add(record.Key);
                        break;
                    case CreatureSyncAction.Add:
                        m_deferredRemoves.Remove(record.Key);
                        m_deferredUpdates.Remove(record.Key);
                        m_deferredAdds[record.Key] = record;
                        break;
                    default:
                        // Update：同一 key 只会保留最新一条（缓存只用于"世界就绪后先落地一次"）。
                        if (!m_deferredAdds.ContainsKey(record.Key))
                            m_deferredUpdates[record.Key] = record;
                        break;
                }
            }
        }

        /// <summary>
        /// 世界就绪后把缓存记录落成实体。只有"一份完整全量快照"才按它裁剪集合；
        /// 不完整（世界就绪时快照还没发完 / 只有零散更新）→ 丢掉缓存并请求全量，
        /// 绝不拿半个集合去裁剪世界里的实体。
        /// </summary>
        private void ApplyDeferredBatches()
        {
            if (!HasDeferredRecords()) return;
            if (!m_deferredSnapshotComplete)
            {
                int dropped = DeferredRecordCount();
                m_deferredDropped += dropped;
                ClearDeferredRecords("世界就绪时缓存的记录不是一份完整全量快照");
                m_resyncWhenWorldReady = true;
                m_resyncWhenWorldReadyReason = "世界就绪时缓存的生物记录不完整（缺全量快照尾批）";
                LogThrottled("deferred-incomplete", "世界就绪时缓存的生物记录不是一份完整全量快照（" +
                    dropped + " 条）→ 丢掉缓存并请求全量（不拿半个集合裁剪世界实体），累计丢弃 " +
                    m_deferredDropped + " 条", 5000, error: true);
                return;
            }

            var appliedKeys = new HashSet<int>();
            int applied = 0;
            if (m_deferredAdds.Count > 0)
            {
                var pending = new List<CreatureStateRecord>(m_deferredAdds.Values);
                for (int i = 0; i < pending.Count; i++)
                {
                    CreatureStateRecord record = pending[i];
                    if (!ApplyAdd(record)) continue;
                    appliedKeys.Add(record.Key);
                    applied++;
                }
            }
            if (m_deferredUpdates.Count > 0)
            {
                var pending = new List<CreatureStateRecord>(m_deferredUpdates.Values);
                for (int i = 0; i < pending.Count; i++)
                {
                    CreatureStateRecord record = pending[i];
                    if (appliedKeys.Contains(record.Key)) continue;   // Add 已经是最新一条
                    ApplyUpdate(record);
                    appliedKeys.Add(record.Key);
                    applied++;
                }
            }
            if (m_deferredRemoves.Count > 0)
            {
                foreach (int key in new List<int>(m_deferredRemoves)) ApplyRemove(key);
            }
            m_deferredApplied += applied;
            ClearDeferredRecords("缓存记录已落地");

            // 快照即权威集合：一次裁剪把"不在快照里"的旧复制体裁掉（保留集合 = 本次成功应用的键）。
            int trimmed = m_world.TrimReplicasTo(appliedKeys, out string trimReason);
            if (trimmed > 0) m_unassignedTrimmed += trimmed;
            if (trimmed < 0)
            {
                LogThrottled("deferred-trim-failed", "缓存记录落地后清理失败：" + trimReason, 5000, error: true);
            }
            LogThrottled("deferred-applied", "世界就绪 → 已把缓存的 " + applied +
                " 条生物记录落成复制体（replicas=" + m_replicas.Count + "，其中 " +
                appliedKeys.Count + " 条来自这份快照）；随后再请求一次全量刷新位置", 5000);
        }

        private bool ApplyAdd(CreatureStateRecord record)
        {
            bool existed = m_replicas.ContainsKey(record.Key);
            if (!m_world.TryCreateReplica(record, out string reason))
            {
                m_createFailures++;
                if (m_failedKeys.Add(record.Key))
                {
                    LogThrottled("create:" + record.Key, "创建生物复制体失败 key=" + record.Key +
                        " 模板=" + (record.TemplateName ?? string.Empty) + "：" + reason +
                        " → 请求主机重发全量（不伪造本地生物）", 5000, error: true);
                }
                // 创建失败 ⇒ 本地不能留这个 key（留着就是"模块有引用、世界里没有实体"的悬空键，
                // 后续 Update 还会走 apply 失败路径）。丢掉后下一次 Update 会按记录自愈创建。
                m_replicas.Remove(record.Key);
                m_interpolation.Remove(record.Key);
                RequestResync("创建复制体失败 key=" + record.Key);
                return false;
            }
            m_failedKeys.Remove(record.Key);
            m_replicas[record.Key] = record.Clone();
            // 实体按这条权威记录创建/已存在：没有表现状态就从这里精确起步（第一次不算瞬移）；
            // 重连时的重复 Add 已经有表现状态 → 走 Observe，让大位移仍按"对齐一次 + 限流日志"
            // 的路径处理，而不是无声瞬移。
            if (m_interpolation.Contains(record.Key)) m_interpolation.Observe(record, m_now);
            else m_interpolation.Seed(record, m_now);
            if (existed) m_duplicateAdds++; else m_addsApplied++;
            return true;
        }

        private void ApplyUpdate(CreatureStateRecord record)
        {
            if (!m_replicas.ContainsKey(record.Key))
            {
                // 未知 key 的更新：记录里带着模板名 + 完整位置/姿态/速度，等价于主机的 Add。
                // 直接按它自愈创建（幂等、不用本地 RNG），不再让这些 key 永久变成 orphan。
                string createReason = "记录没有生物模板名（无法创建复制体）";
                if (!string.IsNullOrWhiteSpace(record.TemplateName) &&
                    m_world.TryCreateReplica(record, out createReason))
                {
                    m_replicas[record.Key] = record.Clone();
                    m_interpolation.Seed(record, m_now);
                    m_orphanUpdatesRecovered++;
                    LogThrottled("orphan-recovered:" + record.Key, "收到未知生物 key=" + record.Key +
                        " 的更新（模板=" + record.TemplateName +
                        "）→ 按记录里的完整状态自愈创建复制体（已累计 " +
                        m_orphanUpdatesRecovered + " 次；不请求全量、不跑本地生成）", 5000);
                    return;
                }
                m_orphanUpdates++;
                LogThrottled("orphan-update:" + record.Key, "收到未知生物 key=" + record.Key +
                    " 的状态更新（本端还没有复制体）：" + createReason + " → 忽略并请求主机重发全量", 5000);
                RequestResync("未知复制体的更新 key=" + record.Key);
                return;
            }
            if (!m_world.TryApplyReplicaState(record, out string reason))
            {
                m_applyFailures++;
                m_replicas.Remove(record.Key);
                m_interpolation.Remove(record.Key);
                LogThrottled("apply:" + record.Key, "施加生物状态失败 key=" + record.Key + "：" + reason +
                    " → 丢掉本地引用并请求主机重发全量（不保留一个卡住的复制体）", 5000, error: true);
                RequestResync("施加状态失败 key=" + record.Key);
                return;
            }
            m_replicas[record.Key] = record.Clone();
            // 只更新外推锚点；真正写进引擎的是每帧的插值结果（不是这条记录的位置）。
            m_interpolation.Observe(record, m_now);
            m_updatesApplied++;
        }

        private void ApplyRemove(int key)
        {
            if (!m_replicas.Remove(key))
            {
                m_orphanRemoves++;
                LogThrottled("orphan-remove:" + key, "收到未知生物 key=" + key +
                    " 的删除（本端没有它的复制体）→ 忽略（无需请求全量；删除本身是幂等的）", 5000);
                return;
            }
            m_interpolation.Remove(key);
            if (!m_world.TryRemoveReplica(key, out string reason))
            {
                m_removeFailures++;
                LogThrottled("remove:" + key, "删除生物复制体失败 key=" + key + "：" + reason, 5000, error: true);
                return;
            }
            m_removesApplied++;
        }

        private void RequestResync(string reason)
        {
            SendResyncRequest(reason, force: false);
        }

        private void SendResyncRequest(string reason, bool force)
        {
            if (m_transport == null || !m_transport.IsConnected) return;
            long nowMs = (long)(m_now * 1000.0);
            if (!force && nowMs - m_lastResyncRequestMs < m_options.ResyncRequestIntervalMs) return;
            m_lastResyncRequestMs = nowMs;
            m_resyncRequests++;
            var request = new PC.CreatureResyncRequest { Reason = reason ?? string.Empty };
            if (!m_transport.TrySend(MessageType.CreatureResyncRequest, request.ToByteArray()))
            {
                LogThrottled("resync-send-failed", "生物全量重发请求发送失败（" + reason + "）", 3000, error: true);
            }
            else
            {
                LogThrottled("resync-sent", "已请求主机重发生物全量（" + reason + "）", 2000);
            }
        }

        /// <summary>只丢引用、不碰引擎（Project 已经卸载时实体早已随旧 Project 销毁）。</summary>
        private void ForgetClientReplicas(string reason)
        {
            int count = m_replicas.Count;
            m_replicas.Clear();
            m_interpolation.Clear();
            m_fullSnapshotId = 0;
            m_fullSnapshotKeys = null;
            m_snapshotResyncRequired = false;
            ClearDeferredRecords("世界不可用，丢掉旧世界的缓存记录");
            m_resyncWhenWorldReady = true;
            m_resyncWhenWorldReadyReason = reason;
            if (count > 0)
            {
                Info("清掉 " + count + " 个生物复制体引用（" + reason + "）；世界就绪后请求全量");
            }
        }

        private void ClearClientReplicas(string reason)
        {
            int removed = m_world.TrimReplicasTo(new HashSet<int>(), out string trimReason);
            if (removed < 0)
            {
                LogThrottled("clear-failed", "清理复制体失败：" + trimReason, 5000, error: true);
            }
            int count = m_replicas.Count;
            m_replicas.Clear();
            m_interpolation.Clear();
            m_fullSnapshotId = 0;
            m_fullSnapshotKeys = null;
            m_snapshotResyncRequired = false;
            ClearDeferredRecords("清掉复制体时一起丢掉缓存记录");
            if (count > 0) Info("清掉 " + count + " 个生物复制体：" + reason);
        }

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            // 不按 m_host 过滤：事件只由主机侧传输端口抛出；若它比第一帧 tick 更早到达，
            // 过滤会让这个客户端错过全量快照（重连时尤其明显）。
            m_clientJoins++;
            m_sentByClient.Remove(clientId);
            m_sendStatesByClient.Remove(clientId);
            m_fullSnapshotPending.Add(clientId);
            Info("玩家端加入 clientId=" + clientId + "（" + (name ?? string.Empty) + "）→ " +
                "下一次扫描给它发区域内的全量生物记录" + (rejoin ? "（重连：客户端可能已有复制体，Add 幂等去重）" : string.Empty));
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            m_clientLeaves++;
            m_sentByClient.Remove(clientId);
            m_sendStatesByClient.Remove(clientId);
            m_fullSnapshotPending.Remove(clientId);
            Warn("玩家端离开 clientId=" + clientId + "（" + (reason ?? string.Empty) +
                "）→ 忘掉它的生物分配（不向已断开的连接发送删除）");
        }

        // ------------------------------------------------------------------ 诊断

        public string StatusLine()
        {
            if (m_host)
            {
                return "creature-spawn: host creatures=" + m_keyByToken.Count +
                    " clients=" + m_sentByClient.Count +
                    " adds=" + m_addsSent + " updates=" + m_updatesSent + " removes=" + m_removesSent +
                    " full=" + m_fullSnapshotsSent +
                    " sendFail=" + m_sendFailures +
                    " encodeFail=" + m_encodeFailures +
                    " wrongDirection=" + m_wrongDirectionFrames +
                    " regionUnknown=" + m_regionUnavailable +
                    " captureFail=" + m_captureFailures +
                    " skipped=" + m_recordsSkipped +
                    " tierIdle=" + m_tierSends[(int)CreatureSyncTier.Idle] +
                    " tierFar=" + m_tierSends[(int)CreatureSyncTier.Far] +
                    " tierMid=" + m_tierSends[(int)CreatureSyncTier.Mid] +
                    " tierNear=" + m_tierSends[(int)CreatureSyncTier.Near] +
                    " tierPriority=" + m_tierSends[(int)CreatureSyncTier.Priority] +
                    " sendRate=" + m_lastSendRatePerSecond + "/s" +
                    " initial=" + m_initialSends + " stateChange=" + m_stateChangeSends +
                    " change=" + m_changeSends + " heartbeat=" + m_heartbeatSends +
                    " budgetSkip=" + m_budgetSkipped +
                    " joins=" + m_clientJoins + " leaves=" + m_clientLeaves;
            }
            return "creature-spawn: client replicas=" + m_replicas.Count +
                " adds=" + m_addsApplied + " updates=" + m_updatesApplied + " removes=" + m_removesApplied +
                " duplicateAdds=" + m_duplicateAdds +
                " createFail=" + m_createFailures + " applyFail=" + m_applyFailures +
                " removeFail=" + m_removeFailures +
                " orphanUpdate=" + m_orphanUpdates + " orphanRecovered=" + m_orphanUpdatesRecovered +
                " orphanRemove=" + m_orphanRemoves +
                " trimmed=" + m_unassignedTrimmed +
                " decodeFail=" + m_decodeFailures + " resync=" + m_resyncRequests +
                " preWorldBatches=" + m_preWorldBatches + " preWorldRecords=" + m_preWorldRecords +
                " deferredApplied=" + m_deferredApplied + " deferredDropped=" + m_deferredDropped +
                " worldReadyResync=" + m_worldReadyResyncs +
                " snaps=" + m_interpolationSnaps + " presentFail=" + m_presentationFailures +
                " snapshotRestarts=" + m_snapshotRestarts;
        }

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                try { m_transport.ClientJoined -= HandleClientJoined; } catch (Exception) { }
                try { m_transport.ClientLeft -= HandleClientLeft; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                m_messages.Registry.UnregisterOwner(ModuleId);
                m_messages = null;
            }
            if (m_replicas.Count > 0) ClearClientReplicas("模块下线");
            // 模块下线后必须把引擎的"客户端只做复制"抑制开关关掉：否则两个生成子系统会一直
            // 保持静默，客户端世界里既没有主机记录、也没有原生生成（明确记录，不留下半死状态）。
            try
            {
                m_world.BeginTick(false, false, m_now, out string worldReason);
                Warn("模块下线 → 已关闭客户端复制抑制（引擎原生生成恢复；生物同步不再工作：" +
                    worldReason + "）");
            }
            catch (Exception e)
            {
                Error("模块下线时关闭复制抑制失败（引擎会保持当前的生成抑制状态）：" +
                    e.GetType().Name + " " + e.Message);
            }
            m_sentByClient.Clear();
            m_sendStatesByClient.Clear();
            m_fullSnapshotPending.Clear();
            m_keyByToken.Clear();
            m_hostCapture.Clear();
            m_interpolation.Clear();
            ClearDeferredRecords("模块下线");
            m_transport = null;
            m_attached = false;
            Info("生物同步模块下线（" + reason + "）：只清理本模块自己的状态");
        }

        private void Info(string message) => m_context?.Diagnostics.Info(m_descriptor.Id, message);

        private void Warn(string message) => m_context?.Diagnostics.Warn(m_descriptor.Id, message);

        private void Error(string message) => m_context?.Diagnostics.Error(m_descriptor.Id, message, null);

        private void LogThrottled(string key, string message, int intervalMs, bool error = false)
        {
            long nowMs = (long)(m_now * 1000.0);
            if (m_lastLogTimes.TryGetValue(key, out long last) && nowMs - last < intervalMs) return;
            m_lastLogTimes[key] = nowMs;
            if (error) Error(message); else Info(message);
        }
    }
}

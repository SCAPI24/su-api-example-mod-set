using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Furniture;
using SuMultiplayer.Net;
using P = SuMultiplayer.Protocol;
using MessageType = SuMultiplayer.Protocol.MessageType;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 家具同步模块（**新文件**）：主机权威的设计表 + 家具库存内容。
    ///
    /// 主机：
    ///   · 世界里的 <c>SubsystemFurnitureBlockBehavior</c> 是唯一权威设计表；
    ///   · 客户端创建 / 编辑 / 上色的请求在这里校验（序号、结构、距离、手持物、源格、
    ///     设计值来源）后由端口落地：安装设计、销毁源方块、生成家具物品、改格上色；
    ///   · 轮询权威设计表指纹（默认 500 ms），只有变化时才抓完整记录，按 <b>有界批次</b>
    ///     广播 <c>FurnitureDesignState</c>，随后广播 <c>FurnitureContentState</c>（内容表）；
    ///   · 加入 / 重连的客户端发 <c>FurnitureDesignSyncRequest</c> → 主机回全量快照；
    ///     只有同步过的客户端才收增量（没同步的客户端会自己再请求）。
    ///
    /// 客户端：
    ///   · **不**在本地新增设计：锤子 / 刷子外壳把意图交给本模块，模块只发请求；
    ///   · 主机记录按主机索引装进一个**只读副本表**（<see cref="FurnitureDesignStore"/>），
    ///     引擎设计表槽位只是渲染查找表；客户端 Save 由外壳写成空表（不污染家具库）；
    ///   · 本地任何引擎路径新增的设计由 leak sweep 移除并计数（clientLibraryRejected）；
    ///   · 内容表对账：主机没有的本地设计回滚（rollback），主机有而本地缺的请求全量同步；
    ///   · 内容应用后刷新本机家具库存（ComponentFurnitureInventory.FillSlots），
    ///     所以"家具面板里的设计（put/take）"始终跟主机权威表一致。
    ///
    /// 为什么家具内容用"设计表驱动"的专用路径（而不是复用容器同步）：
    ///   · 引擎里没有"家具方块实体 + IInventory"这种容器；ComponentFurnitureInventory 是
    ///     玩家身上的**派生只读库存**（槽位 = 世界设计表里的设计，取之不尽），没有可编辑的
    ///     AddSlotItems / RemoveSlotItems 语义（引擎实现是空操作）。
    ///   · 因此家具库存的"内容"就是主机设计表：put = 主机新增设计（Create / Paint / Rename /
    ///     合成配方），take = 主机设计表移除（GC / 套装删除）；请求 = FurnitureDesignRequest，
    ///     结果 = FurnitureDesignResult，广播 = FurnitureDesignState + FurnitureContentState，
    ///     回滚 = 客户端把 Host 内容表里没有的设计从副本表移除（client rollback）。
    ///   · 这样家具面板显示的每个设计都确实存在于主机的世界设计表里，且客户端不写自己的存档。
    ///
    /// 客户端设计新增路径审计（全部被本模块或外壳接管；漏网的由 leak sweep 明确移除并计数）：
    ///   1) 锤子 ScanDesign            → SuSubsystemHammerBlockBehavior → 设计请求；
    ///   2) FurnitureBlock.Paint       → SuSubsystemBucketBlockBehavior（颜料桶 + 家具）→ 上色请求；
    ///   3) GetAdHocCraftingRecipe     → SuFurnitureBlock 客户端分支返回 null（主机配方权威）；
    ///   4) FurnitureInventoryPanel 套装导入 / 新建 → 本地新增会被 leak sweep 移除并记录；
    ///   5) 其它 Mod / 未来引擎路径   → 同上（clientLibraryRejected / clientLibraryLeaks）。
    ///
    /// 没有静态状态；计数器与在途请求都挂在实例上。日志 10 s 限流（问题与汇总分开键）。
    /// </summary>
    public sealed class FurnitureSyncModule : IMultiplayerModule, ISuMPMessageModule,
        IFurnitureDesignRequestSink
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;
        private const int HostPollIntervalMs = 500;
        private const int SyncRetryMs = 2000;
        private const int SyncWarnMs = 10000;
        private const int RequestTimeoutMs = 5000;
        private const int MaxPendingRequests = 32;
        private const int LeakSweepIntervalMs = 1000;
        private const int MaxStatePayloadBytes = 48 * 1024;
        private const int MaxHostDesigns = FurnitureDesignStore.MaxDesigns;

        private sealed class PendingDesignRequest
        {
            public long Sequence;
            public long SentMs;
            public int Op;
            public string Name;
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly IFurniturePort m_port;
        private readonly Func<double> m_clock;
        private readonly FurnitureDesignStore m_store = new FurnitureDesignStore();
        private readonly Dictionary<long, PendingDesignRequest> m_pending =
            new Dictionary<long, PendingDesignRequest>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_rejectedByReason =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<FurnitureDesignRecord> m_hostCapture =
            new List<FurnitureDesignRecord>(MaxHostDesigns);
        private readonly Dictionary<int, FurnitureDesignRecord> m_lastHostRecords =
            new Dictionary<int, FurnitureDesignRecord>();
        private readonly HashSet<int> m_syncedClients = new HashSet<int>();
        private readonly Dictionary<int, long> m_lastRequestSequence = new Dictionary<int, long>();
        private readonly List<FurnitureDesignRecord> m_publishUpserts =
            new List<FurnitureDesignRecord>(8);
        private readonly List<int> m_publishRemoved = new List<int>(8);
        private readonly List<int> m_contentIndices = new List<int>(MaxHostDesigns);
        private readonly List<long> m_contentHashes = new List<long>(MaxHostDesigns);
        private readonly List<int> m_leakRemoved = new List<int>(8);
        private readonly List<int> m_reinstalled = new List<int>(8);
        private readonly List<long> m_expiredPending = new List<long>(4);
        private readonly StringBuilder m_builder = new StringBuilder(512);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private int m_lastLocalClientId = int.MinValue;
        private int m_lastSessionSerial = int.MinValue;
        private long m_nextSummaryMs;

        // ---------------------------------------------------------------- 主机
        private bool m_hostBaselineValid;
        private bool m_hostUnavailableLogged;
        private int m_hostRevision;
        private long m_hostFingerprint;
        private int m_hostDesignCount;
        private long m_nextHostPollMs;

        // ---------------------------------------------------------------- 客户端
        private bool m_clientAdopted;
        private bool m_clientSynced;
        private int m_clientSessionSerial = int.MinValue;
        private long m_nextSyncRequestMs;
        private long m_nextSyncWarnMs;
        private long m_clientSyncFirstRequestMs;
        private long m_nextLeakSweepMs;

        // ---------------------------------------------------------------- 计数：设计
        private long m_designSent;
        private long m_designRecv;
        private long m_designApplied;
        private long m_designAccepted;
        private long m_designRejected;
        private long m_designMalformed;
        private long m_designDuplicate;
        private long m_designRemoved;
        private long m_designTimeout;
        private long m_designSendFailed;
        private long m_designDirectionErrors;
        private long m_designStateStale;
        private long m_designStateConflict;
        private long m_designStateMissing;
        private long m_designSyncRequests;
        private long m_designSyncTimeout;
        private long m_designResultUnknown;
        private long m_designPickableMissing;

        // ---------------------------------------------------------------- 计数：内容
        private long m_contentSent;
        private long m_contentRecv;
        private long m_contentApplied;
        private long m_contentRejected;
        private long m_contentStale;
        private long m_contentMalformed;
        private long m_contentRolledBack;
        private long m_contentPut;
        private long m_contentTaken;
        private long m_contentMissing;
        private long m_contentSendFailed;
        private long m_contentDirectionErrors;
        private long m_contentRefreshed;
        private long m_contentRefreshFailed;

        public FurnitureSyncModule(IFurniturePort port, Func<double> clock = null)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_clock = clock != null ? clock : new Func<double>(delegate { return Time.RealTime; });
            m_descriptor = new ModuleDescriptor("furniture-sync", "家具设计 / 内容同步（主机权威）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "furniture.sync" },
                // 不允许运行时关闭：客户端存档污染保护依赖子系统外壳与模块的角色一致性。
                HotPluggable = false
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        /// <summary>只读副本表的条数（自检 / 状态行；主机侧为 0，主机用引擎设计表）。</summary>
        public int ReplicaDesignCount => m_store.Count;

        /// <summary>本会话客户端设计表是否已经由主机同步（未同步时地形编辑闸门放行）。</summary>
        public bool IsSynced => !IsHosting && m_clientSynced;

        /// <summary>只读副本索引快照（自检用）。</summary>
        public IReadOnlyCollection<int> ReplicaDesignIndices => m_store.Indices;

        public bool TryGetReplicaDesign(int index, out FurnitureDesignRecord record) =>
            m_store.TryGet(index, out record);

        public bool IsReplicaDesign(int designIndex) => m_store.Contains(designIndex);

        public string StatusLine() => "furniture-sync: " + Summary() + " · " + m_port.Describe();

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 家具同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "家具同步不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到家具设计 / 内容消息");
            }
            m_transport.ClientJoined += HandleClientJoined;
            m_transport.ClientLeft += HandleClientLeft;
            m_port.SetRequestSink(this);
            m_lastLocalClientId = int.MinValue;
            m_lastSessionSerial = int.MinValue;
            m_attached = true;
            Log("家具同步就绪：客户端 = 请求 + 副本设计表（不写存档）；主机 = 校验 + 权威设计表 + 有界广播");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.FurnitureDesignRequest,
                P.FurnitureDesignRequest.Parser,
                new DelegateMessageHandler<P.FurnitureDesignRequest>(OnDesignRequest), ModuleId);
            registry.Register((int)MessageType.FurnitureDesignResult,
                P.FurnitureDesignResult.Parser,
                new DelegateMessageHandler<P.FurnitureDesignResult>(OnDesignResult), ModuleId);
            registry.Register((int)MessageType.FurnitureDesignState,
                P.FurnitureDesignState.Parser,
                new DelegateMessageHandler<P.FurnitureDesignState>(OnDesignState), ModuleId);
            registry.Register((int)MessageType.FurnitureContentState,
                P.FurnitureContentState.Parser,
                new DelegateMessageHandler<P.FurnitureContentState>(OnContentState), ModuleId);
            registry.Register((int)MessageType.FurnitureDesignSyncRequest,
                P.FurnitureDesignSyncRequest.Parser,
                new DelegateMessageHandler<P.FurnitureDesignSyncRequest>(OnSyncRequest), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_port.SetRequestSink(null);
            if (m_transport != null)
            {
                m_transport.ClientJoined -= HandleClientJoined;
                m_transport.ClientLeft -= HandleClientLeft;
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            try { m_port.Shutdown(); } catch (Exception) { }
            ResetClientRequests("detach");
            m_store.Reset();
            m_pending.Clear();
            m_lastHostRecords.Clear();
            m_syncedClients.Clear();
            m_lastRequestSequence.Clear();
            m_lastProblemMs.Clear();
            m_hostBaselineValid = false;
            m_hostRevision = 0;
            m_hostFingerprint = 0;
            m_hostDesignCount = 0;
            m_attached = false;
            Log("家具同步下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            long nowMs = (long)(tick.Now * 1000.0);
            bool hosting = IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            if (m_port.SessionSerial != m_lastSessionSerial)
            {
                OnSessionChanged(hosting, m_port.SessionSerial);
            }
            if (hosting)
            {
                if (connected) HostTick(nowMs);
            }
            else
            {
                ClientTick(nowMs, connected);
            }
            m_wasConnected = connected;
            if (nowMs - m_nextSummaryMs >= SummaryLogMs)
            {
                m_nextSummaryMs = nowMs + SummaryLogMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        private void OnSessionChanged(bool hosting, int serial)
        {
            m_lastSessionSerial = serial;
            m_clientSessionSerial = serial;
            ResetClientRequests("session-change");
            m_store.Reset();
            m_clientAdopted = false;
            m_clientSynced = false;
            m_clientSyncFirstRequestMs = 0;
            m_hostBaselineValid = false;
            m_hostRevision = 0;
            m_hostFingerprint = 0;
            m_hostDesignCount = 0;
            m_lastHostRecords.Clear();
            m_syncedClients.Clear();
            m_lastRequestSequence.Clear();
            m_nextHostPollMs = 0;
            m_nextSyncRequestMs = 0;
            m_nextSyncWarnMs = 0;
            Log("家具同步会话换代（serial=" + serial + " · " +
                (hosting ? "host" : "client") + "）→ 设计表基线 / 客户端副本全部作废，重新同步");
        }

        private void HostTick(long nowMs)
        {
            if (!m_hostBaselineValid)
            {
                if (nowMs < m_nextHostPollMs) return;
                m_nextHostPollMs = nowMs + HostPollIntervalMs;
                if (TryCaptureHostBaseline(nowMs)) return;
                return;
            }
            if (nowMs < m_nextHostPollMs) return;
            m_nextHostPollMs = nowMs + HostPollIntervalMs;
            if (!m_port.TryCaptureHostFingerprint(out long fingerprint, out int count,
                    out string reason))
            {
                LogProblem("host-fingerprint:" + reason,
                    "主机抓不到家具设计表指纹（" + reason + "）→ 本拍不广播（客户端仍会按请求做全量同步）");
                return;
            }
            if (fingerprint == m_hostFingerprint && count == m_hostDesignCount) return;
            PublishHostDesignChanges(nowMs, "fingerprint-change");
        }

        private bool TryCaptureHostBaseline(long nowMs)
        {
            m_hostCapture.Clear();
            int count = m_port.CaptureHostDesigns(m_hostCapture, MaxHostDesigns,
                out long fingerprint, out string reason);
            if (count < 0)
            {
                if (!m_hostUnavailableLogged)
                {
                    m_hostUnavailableLogged = true;
                    LogProblem("host-baseline:" + reason,
                        "主机暂时抓不到家具设计表（" + reason + "）→ 等世界加载完成后再建立基线");
                }
                return false;
            }
            m_hostUnavailableLogged = false;
            m_lastHostRecords.Clear();
            for (int i = 0; i < m_hostCapture.Count; i++)
            {
                FurnitureDesignRecord record = m_hostCapture[i];
                if (record == null) continue;
                m_lastHostRecords[record.Index] = record;
            }
            m_hostFingerprint = fingerprint;
            m_hostDesignCount = count;
            m_hostRevision = Math.Max(1, m_hostRevision + 1);
            m_hostBaselineValid = true;
            Log("主机的家具设计表基线已建立：designs=" + count + " rev=" + m_hostRevision +
                "（客户端加入时按请求发全量快照）");
            return true;
        }

        private void ClientTick(long nowMs, bool connected)
        {
            if (!m_port.IsClientReplica)
            {
                if (m_clientAdopted)
                {
                    ResetClientRequests("role-change");
                    m_store.Reset();
                    m_clientAdopted = false;
                    m_clientSynced = false;
                    Log("本端不再是联机客户端 → 家具副本表已清空（引擎设计表保持当前世界内容）");
                }
                return;
            }
            if (m_port.SessionSerial != m_clientSessionSerial)
            {
                m_clientSessionSerial = m_port.SessionSerial;
                m_clientAdopted = false;
                m_clientSynced = false;
                m_nextSyncRequestMs = 0;
            }
            if (!m_clientAdopted)
            {
                m_hostCapture.Clear();
                if (!m_port.TryAdoptClientLibrary(m_hostCapture, out string adoptReason))
                {
                    LogProblem("client-adopt:" + adoptReason,
                        "客户端接管已加载的家具设计失败（" + adoptReason + "）→ 本拍不建立副本表");
                    return;
                }
                int adopted = m_store.Adopt(m_hostCapture, 0, out int conflicts);
                m_clientAdopted = true;
                m_clientSynced = false;
                Log("客户端接管世界里的家具设计为**副本**：designs=" + adopted +
                    (conflicts > 0 ? "（同槽位不同内容 " + conflicts + " 条，等主机全量覆盖）" : "") +
                    " · 这些设计**不写客户端存档**");
            }

            int localId = m_transport?.LocalClientId ?? -1;
            if (localId != m_lastLocalClientId)
            {
                bool reconnected = m_lastLocalClientId != int.MinValue;
                m_lastLocalClientId = localId;
                ResetClientRequests(reconnected ? "client-id-change" : "first-connect");
                m_nextSyncRequestMs = 0;
                if (reconnected)
                {
                    LogProblem("client-reconnect", "客户端连接已更换（LocalClientId → " + localId +
                        "）→ 作废在途请求并按新连接重新申请设计表全量同步");
                }
            }

            if (!connected)
            {
                if (m_wasConnected)
                {
                    ResetClientRequests("disconnected");
                    Log("客户端断开 → 作废在途家具请求；副本设计表保留（世界仍在渲染），" +
                        "重连后重新申请全量同步");
                }
                return;
            }

            ExpirePendingRequests(nowMs);

            if (m_port.IsWorldReady && !m_clientSynced && nowMs >= m_nextSyncRequestMs)
            {
                m_nextSyncRequestMs = nowMs + SyncRetryMs;
                if (m_clientSyncFirstRequestMs == 0) m_clientSyncFirstRequestMs = nowMs;
                RequestFullSync("join");
                if (nowMs - m_clientSyncFirstRequestMs >= SyncWarnMs &&
                    nowMs >= m_nextSyncWarnMs)
                {
                    m_nextSyncWarnMs = nowMs + SyncWarnMs;
                    m_designSyncTimeout++;
                    LogProblem("sync-timeout:" + m_designSyncTimeout,
                        "客户端已等待 " + SyncWarnMs + "ms 仍未收到主机家具设计表全量（已请求 " +
                        m_designSyncTimeout + " 次）→ 继续每 " + (SyncRetryMs / 1000.0) +
                        "s 重试；家具面板此时可能为空（明确失败，不伪造设计）");
                }
            }

            if (nowMs >= m_nextLeakSweepMs)
            {
                m_nextLeakSweepMs = nowMs + LeakSweepIntervalMs;
                SweepClientLibrary(nowMs);
            }
        }

        // ================================================================= 主机：客户端加入 / 同步

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!IsHosting) return;
            m_lastRequestSequence.Remove(clientId);
            if (m_syncedClients.Remove(clientId))
            {
                Log("客户端 clientId=" + clientId + "（" + (name ?? string.Empty) + "，" +
                    (rejoin ? "重连" : "加入") + "）→ 移出已同步集合，等它发全量同步请求");
            }
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            if (!IsHosting) return;
            m_lastRequestSequence.Remove(clientId);
            m_syncedClients.Remove(clientId);
        }

        private void OnSyncRequest(MessageContext context, P.FurnitureDesignSyncRequest message)
        {
            if (!IsHosting)
            {
                m_designDirectionErrors++;
                LogProblem("sync-on-client", "客户端收到了 FurnitureDesignSyncRequest（方向错误）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0)
            {
                m_designRejected++;
                AddReason("sync-source");
                LogProblem("sync-source", "FurnitureDesignSyncRequest 来源非法（clientId=" +
                    clientId + "）→ 拒绝");
                return;
            }
            m_designSyncRequests++;
            if (!m_hostBaselineValid)
            {
                LogProblem("sync-before-baseline", "clientId=" + clientId +
                    " 请求全量家具设计表，但主机的设计表基线还没建立 → 本拍不回（客户端会重试）");
                return;
            }
            SendFullSnapshot(clientId, message.Reason ?? "sync-request");
        }

        private void SendFullSnapshot(int clientId, string reason)
        {
            m_hostCapture.Clear();
            int count = m_port.CaptureHostDesigns(m_hostCapture, MaxHostDesigns,
                out long fingerprint, out string captureReason);
            if (count < 0)
            {
                LogProblem("snapshot-capture:" + captureReason,
                    "主机抓取家具设计表失败（" + captureReason + "）→ clientId=" + clientId +
                    " 收不到全量（客户端会按 2s 重试）");
                return;
            }
            var sorted = new List<FurnitureDesignRecord>(m_hostCapture);
            sorted.Sort((a, b) => a.Index.CompareTo(b.Index));
            // 快照即当前主机真相：同步更新内容表源，避免“设计状态有、内容表旧”
            // 导致客户端在应用后又把刚装好的设计当成 extra 回滚。
            m_lastHostRecords.Clear();
            for (int i = 0; i < sorted.Count; i++)
                m_lastHostRecords[sorted[i].Index] = sorted[i];
            int batch = 0;
            int sent = 0;
            while (sent < sorted.Count)
            {
                int batchBytes = 0;
                var chunk = new List<FurnitureDesignRecord>(FurnitureDesignCodec.MaxDesignsPerState);
                while (sent < sorted.Count && chunk.Count < FurnitureDesignCodec.MaxDesignsPerState)
                {
                    int size = FurnitureDesignCodec.EstimateRecordBytes(sorted[sent]);
                    if (chunk.Count > 0 && batchBytes + size > MaxStatePayloadBytes) break;
                    batchBytes += size;
                    chunk.Add(sorted[sent]);
                    sent++;
                }
                batch++;
                if (!SendDesignStateTo(clientId, chunk, null, m_hostRevision, full: true,
                        reason + " batch=" + batch))
                {
                    LogProblem("snapshot-send:" + clientId,
                        "全量家具设计表第 " + batch + " 批发送失败（clientId=" + clientId +
                        " designs=" + chunk.Count + "）→ 客户端会继续请求");
                    return;
                }
            }
            if (sorted.Count == 0)
            {
                // 空表也必须发一条 full=true，客户端才知道"主机确实没有设计"。
                if (!SendDesignStateTo(clientId, Array.Empty<FurnitureDesignRecord>(), null,
                        m_hostRevision, full: true, reason + " empty"))
                    return;
            }
            SendContentTo(clientId, full: true, reason + " content");
            m_syncedClients.Add(clientId);
            m_hostFingerprint = fingerprint;
            m_hostDesignCount = count;
            Log("已向 clientId=" + clientId + " 发送家具设计表全量：designs=" + sorted.Count +
                " rev=" + m_hostRevision + " · " + reason);
        }

        // ================================================================= 主机：设计表变化广播

        private void PublishHostDesignChanges(long nowMs, string why)
        {
            m_hostCapture.Clear();
            int count = m_port.CaptureHostDesigns(m_hostCapture, MaxHostDesigns,
                out long fingerprint, out string reason);
            if (count < 0)
            {
                LogProblem("host-capture:" + reason,
                    "主机抓不到家具设计表（" + reason + "）→ 本拍不广播");
                return;
            }
            var current = new Dictionary<int, FurnitureDesignRecord>(m_hostCapture.Count);
            for (int i = 0; i < m_hostCapture.Count; i++)
            {
                FurnitureDesignRecord record = m_hostCapture[i];
                if (record == null) continue;
                current[record.Index] = record;
            }
            m_publishUpserts.Clear();
            m_publishRemoved.Clear();
            foreach (KeyValuePair<int, FurnitureDesignRecord> item in current)
            {
                if (!m_lastHostRecords.TryGetValue(item.Key, out FurnitureDesignRecord last) ||
                    !last.SameContent(item.Value))
                {
                    m_publishUpserts.Add(item.Value);
                }
            }
            foreach (KeyValuePair<int, FurnitureDesignRecord> item in m_lastHostRecords)
            {
                if (!current.ContainsKey(item.Key)) m_publishRemoved.Add(item.Key);
            }
            m_publishUpserts.Sort((a, b) => a.Index.CompareTo(b.Index));
            m_publishRemoved.Sort();
            if (m_publishUpserts.Count == 0 && m_publishRemoved.Count == 0)
            {
                m_hostFingerprint = fingerprint;
                m_hostDesignCount = count;
                return;
            }

            int revision = NextRevision();
            m_hostFingerprint = fingerprint;
            m_hostDesignCount = count;
            m_lastHostRecords.Clear();
            foreach (KeyValuePair<int, FurnitureDesignRecord> item in current)
                m_lastHostRecords[item.Key] = item.Value;

            if (m_syncedClients.Count > 0)
            {
                var peers = new List<int>(m_syncedClients);
                for (int i = 0; i < peers.Count; i++)
                {
                    int clientId = peers[i];
                    if (!m_transport.IsClientSendable(clientId))
                    {
                        m_syncedClients.Remove(clientId);
                        LogProblem("peer-gone:" + clientId, "clientId=" + clientId +
                            " 已不可发送 → 移出已同步集合（重连后重新全量同步）");
                        continue;
                    }
                    SendDesignBatchesTo(clientId, m_publishUpserts, m_publishRemoved, revision,
                        "change");
                    SendContentTo(clientId, full: false, "change");
                }
            }

            m_designApplied += m_publishUpserts.Count;
            m_designRemoved += m_publishRemoved.Count;
            if (m_publishRemoved.Count > 0)
            {
                m_builder.Clear();
                for (int i = 0; i < m_publishRemoved.Count && i < 8; i++)
                {
                    if (m_builder.Length > 0) m_builder.Append(',');
                    m_builder.Append(m_publishRemoved[i]);
                }
                Log("主机家具设计表移除 " + m_publishRemoved.Count + " 条（index=" +
                    m_builder + "）→ 已广播（客户端会从副本表删除）· " + why);
            }
            if (m_publishUpserts.Count > 0)
            {
                Log("主机家具设计表更新 " + m_publishUpserts.Count + " 条 → 已广播 rev=" +
                    revision + " · " + why);
            }
        }

        private int NextRevision()
        {
            m_hostRevision++;
            if (m_hostRevision <= 0) m_hostRevision = 1;
            return m_hostRevision;
        }

        // ================================================================= 主机：设计请求

        private void OnDesignRequest(MessageContext context, P.FurnitureDesignRequest message)
        {
            m_designRecv++;
            if (!IsHosting)
            {
                m_designDirectionErrors++;
                LogProblem("design-request-on-client",
                    "客户端收到了 FurnitureDesignRequest（方向错误）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || context.Type != MessageType.FurnitureDesignRequest)
            {
                m_designRejected++;
                AddReason("request-source");
                LogProblem("design-source", "FurnitureDesignRequest 来源非法（clientId=" + clientId +
                    "）→ 拒绝");
                return;
            }
            if (context.PayloadBytes > FurnitureDesignCodec.MaxPayloadBytes)
            {
                m_designMalformed++;
                LogProblem("design-payload:" + context.PayloadBytes,
                    "FurnitureDesignRequest 载荷 " + context.PayloadBytes + " 字节 > 上限 " +
                    FurnitureDesignCodec.MaxPayloadBytes + "（clientId=" + clientId +
                    "）→ 整条拒绝");
                SendResult(clientId, new FurnitureDesignResultData(message.Sequence, false,
                    "payload-too-large", 0, 0, false, m_hostRevision, false));
                return;
            }
            if (!FurnitureDesignCodec.TryDecodeRequest(message,
                    out FurnitureDesignRequestData request, out string decodeError))
            {
                m_designMalformed++;
                LogProblem("design-decode:" + decodeError,
                    "FurnitureDesignRequest 载荷非法（" + decodeError + "，clientId=" + clientId +
                    "）→ 整条拒绝");
                SendResult(clientId, new FurnitureDesignResultData(message.Sequence, false,
                    "malformed:" + decodeError, 0, 0, false, m_hostRevision, false));
                return;
            }
            if (request.Sequence <= 0)
            {
                m_designMalformed++;
                LogProblem("design-sequence:0",
                    "FurnitureDesignRequest sequence <= 0（clientId=" + clientId +
                    "）→ 整条拒绝（回执无法关联）");
                return;
            }
            if (!FurnitureDesignCodec.TryValidateRequest(in request, m_port.BlockCount,
                    out string validationError))
            {
                m_designMalformed++;
                LogProblem("design-validate:" + validationError,
                    "FurnitureDesignRequest 结构校验失败（" + validationError + "，clientId=" +
                    clientId + " " + request.Describe() + "）→ 拒绝");
                SendResult(clientId, new FurnitureDesignResultData(request.Sequence, false,
                    "malformed:" + validationError, 0, 0, false, m_hostRevision, false));
                return;
            }
            m_lastRequestSequence.TryGetValue(clientId, out long lastSequence);
            if (request.Sequence <= lastSequence)
            {
                m_designRejected++;
                AddReason("duplicate-request");
                LogProblem("design-seq:" + clientId,
                    "clientId=" + clientId + " 的设计请求序号 " + request.Sequence +
                    " <= 已处理 " + lastSequence + " → 拒绝（不重复落地）");
                SendResult(clientId, new FurnitureDesignResultData(request.Sequence, false,
                    "duplicate-request", 0, 0, false, m_hostRevision, false));
                return;
            }
            m_lastRequestSequence[clientId] = request.Sequence;

            bool applyAccepted;
            FurnitureDesignResultData result;
            FurnitureDesignRecord installed;
            string applyReason;
            try
            {
                applyAccepted = m_port.TryApplyHostDesignRequest(clientId, in request,
                    out result, out installed, out applyReason);
            }
            catch (Exception error)
            {
                m_designRejected++;
                AddReason("host-exception");
                LogProblem("design-exception:" + error.GetType().Name,
                    "主机落地家具设计请求抛异常（clientId=" + clientId + " " +
                    request.Describe() + "）：" + error.GetType().Name + " " + error.Message +
                    " → 回明确失败，不假装成功");
                SendResult(clientId, new FurnitureDesignResultData(request.Sequence, false,
                    "host-exception:" + error.GetType().Name, 0, 0, false, m_hostRevision,
                    false));
                return;
            }
            if (!applyAccepted)
            {
                m_designRejected++;
                AddReason(ReasonCode(applyReason));
                LogProblem("design-apply:" + ReasonCode(applyReason),
                    "主机拒绝 clientId=" + clientId + " 的家具设计请求 " + request.Describe() +
                    "（" + applyReason + "）→ 客户端不会新增任何设计");
                SendResult(clientId, new FurnitureDesignResultData(request.Sequence, false,
                    applyReason, 0, 0, false, m_hostRevision, false));
                return;
            }

            m_designAccepted++;
            m_designApplied++;
            m_designDuplicate += result.Duplicate ? 1 : 0;
            m_designPickableMissing += result.PickableSpawned ? 0 : 1;
            if (installed != null)
            {
                int revision = NextRevision();
                m_lastHostRecords[installed.Index] = installed;
                // 请求者先拿记录（即使还没做全量同步），再拿结果；其它已同步客户端收广播。
                SendDesignStateTo(clientId, new[] { installed }, null, revision, full: false,
                    "request");
                var peers = new List<int>(m_syncedClients);
                for (int i = 0; i < peers.Count; i++)
                {
                    if (peers[i] == clientId) continue;
                    if (!m_transport.IsClientSendable(peers[i])) continue;
                    SendDesignStateTo(peers[i], new[] { installed }, null, revision,
                        full: false, "request-broadcast");
                }
                SendContentTo(clientId, full: false, "request");
                for (int i = 0; i < peers.Count; i++)
                {
                    if (peers[i] == clientId) continue;
                    if (!m_transport.IsClientSendable(peers[i])) continue;
                    SendContentTo(peers[i], full: false, "request-broadcast");
                }
            }
            result = new FurnitureDesignResultData(result.Sequence, true, string.Empty,
                result.DesignIndex, result.ContentHash, result.Duplicate, m_hostRevision,
                result.PickableSpawned);
            // 请求落地后立即同步主机指纹 / 数量：否则下一拍会把这次变化
            // 当成“基线没变”而跳过，且下一次删除又回到基线时指纹碰巧相同。
            if (m_port.TryCaptureHostFingerprint(out long requestFingerprint,
                    out int requestCount, out _))
            {
                m_hostFingerprint = requestFingerprint;
                m_hostDesignCount = requestCount;
            }
            SendResult(clientId, result);
            Log("主机接受 clientId=" + clientId + " 的家具设计请求：index=" + result.DesignIndex +
                " duplicate=" + result.Duplicate + " pickable=" + result.PickableSpawned +
                " rev=" + m_hostRevision + " · " + request.Describe());
        }

        private void SendResult(int clientId, in FurnitureDesignResultData result)
        {
            if (m_transport == null || result.Sequence <= 0) return;
            byte[] payload = FurnitureDesignCodec.EncodeResultBytes(in result);
            if (!m_transport.TrySendTo(clientId, MessageType.FurnitureDesignResult, payload))
            {
                m_designSendFailed++;
                LogProblem("result-send:" + clientId,
                    "FurnitureDesignResult 发送失败（clientId=" + clientId + " seq=" +
                    result.Sequence + " accepted=" + result.Accepted + "）→ 客户端会按请求超时处理");
            }
        }

        // ================================================================= 客户端：请求

        public bool TrySubmitDesignRequest(in FurnitureDesignRequestData request,
            out string reason)
        {
            reason = null;
            if (!m_attached)
            {
                reason = "module-detached";
                return false;
            }
            if (IsHosting)
            {
                reason = "hosting";
                return false;
            }
            if (m_transport == null || !m_transport.IsConnected)
            {
                reason = "not-connected";
                return false;
            }
            if (request.Op != FurnitureDesignOps.Create &&
                request.Op != FurnitureDesignOps.Paint &&
                request.Op != FurnitureDesignOps.Rename)
            {
                m_designMalformed++;
                reason = "op:" + request.Op.ToString(CultureInfo.InvariantCulture);
                LogProblem("submit-op:" + request.Op, "家具请求操作码非法（" + reason + "）→ 不发送");
                return false;
            }
            if (!FurnitureDesignCodec.TryValidateRequest(in request, m_port.BlockCount,
                    out string validationError))
            {
                m_designMalformed++;
                reason = validationError;
                LogProblem("submit-validate:" + validationError,
                    "家具设计请求结构校验失败（" + validationError + "）→ 不发送（明确失败）");
                return false;
            }
            long sequence = NextSequence();
            FurnitureDesignRequestData sequenced = request.WithSequence(sequence);
            if (!FurnitureDesignCodec.TryEncodeRequest(in sequenced,
                    out P.FurnitureDesignRequest encoded, out string encodeError))
            {
                m_designMalformed++;
                reason = encodeError;
                LogProblem("submit-encode:" + encodeError,
                    "家具设计请求编码失败（" + encodeError + "）→ 不发送");
                return false;
            }
            byte[] payload = encoded.ToByteArray();
            if (payload.Length > FurnitureDesignCodec.MaxPayloadBytes)
            {
                m_designMalformed++;
                reason = "payload:" + payload.Length;
                LogProblem("submit-payload:" + payload.Length,
                    "家具设计请求编码后 " + payload.Length + " 字节 > 上限 " +
                    FurnitureDesignCodec.MaxPayloadBytes + " → 不发送（明确失败）");
                return false;
            }
            if (!m_transport.TrySend(MessageType.FurnitureDesignRequest, payload))
            {
                m_designSendFailed++;
                reason = "send-failed";
                LogProblem("request-send", "家具设计请求发送失败（" + sequenced.Describe() +
                    "）→ 客户端**不新增**本地设计（本次操作无效果），累计失败 " + m_designSendFailed);
                return false;
            }
            m_designSent++;
            m_pending[sequence] = new PendingDesignRequest
            {
                Sequence = sequence,
                SentMs = (long)(m_clock() * 1000.0),
                Op = sequenced.Op,
                Name = sequenced.Name
            };
            if (m_pending.Count > MaxPendingRequests)
            {
                long oldest = long.MaxValue;
                foreach (KeyValuePair<long, PendingDesignRequest> item in m_pending)
                {
                    if (item.Key < oldest) oldest = item.Key;
                }
                if (oldest != long.MaxValue)
                {
                    m_pending.Remove(oldest);
                    m_designTimeout++;
                    LogProblem("pending-overflow", "在途家具请求超过 " + MaxPendingRequests +
                        " 条 → 最旧的一条按超时处理（不静默丢）");
                }
            }
            Log("客户端提交家具设计请求 seq=" + sequence + " · " + sequenced.Describe());
            return true;
        }

        private long m_nextSequence;

        private long NextSequence()
        {
            m_nextSequence++;
            if (m_nextSequence <= 0) m_nextSequence = 1;
            return m_nextSequence;
        }

        // ================================================================= 客户端：收结果 / 状态

        private void OnDesignResult(MessageContext context, P.FurnitureDesignResult message)
        {
            if (IsHosting)
            {
                m_designDirectionErrors++;
                LogProblem("result-on-host", "主机收到了 FurnitureDesignResult（方向错误）→ 丢弃");
                return;
            }
            if (!FurnitureDesignCodec.TryDecodeResult(message,
                    out FurnitureDesignResultData result, out string error))
            {
                m_designMalformed++;
                LogProblem("result-decode:" + error,
                    "FurnitureDesignResult 载荷非法（" + error + "）→ 丢弃");
                return;
            }
            if (!m_pending.TryGetValue(result.Sequence, out PendingDesignRequest pending))
            {
                m_designResultUnknown++;
                LogProblem("result-unknown:" + result.Sequence,
                    "收到未知序号的家具设计回执 seq=" + result.Sequence +
                    "（重连 / 重复 / 主机重启）→ 不应用");
                return;
            }
            m_pending.Remove(result.Sequence);
            if (!result.Accepted)
            {
                m_designRejected++;
                AddReason(ReasonCode(result.Reason));
                LogProblem("result-rejected:" + ReasonCode(result.Reason),
                    "家具设计请求被主机拒绝 seq=" + result.Sequence + "（" + result.Reason +
                    "）→ 客户端没有新增任何本地设计");
                m_port.TryShowClientMessage("家具设计未通过主机校验：" + result.Reason);
                return;
            }
            m_designAccepted++;
            if (!m_store.Contains(result.DesignIndex))
            {
                m_designStateMissing++;
                LogProblem("result-state-missing:" + result.DesignIndex,
                    "主机接受了家具设计 index=" + result.DesignIndex +
                    "，但客户端副本表里还没有该记录 → 请求全量同步（不伪造设计）");
                m_nextSyncRequestMs = 0;
            }
            if (!result.PickableSpawned)
            {
                m_port.TryShowClientMessage("主机已保存设计，但没能为你在世界里生成家具物品");
            }
            Log("客户端收到家具设计回执 seq=" + result.Sequence + " accepted=true index=" +
                result.DesignIndex + " duplicate=" + result.Duplicate + " rev=" +
                result.Revision);
        }

        private void OnDesignState(MessageContext context, P.FurnitureDesignState message)
        {
            if (IsHosting)
            {
                m_designDirectionErrors++;
                LogProblem("state-on-host", "主机收到了 FurnitureDesignState（方向错误）→ 丢弃");
                return;
            }
            if (context.PayloadBytes > FurnitureDesignCodec.MaxPayloadBytes)
            {
                m_designMalformed++;
                LogProblem("state-payload:" + context.PayloadBytes,
                    "FurnitureDesignState 载荷 " + context.PayloadBytes + " 字节 > 上限 → 整条拒绝");
                return;
            }
            if (!FurnitureDesignCodec.TryDecodeState(message,
                    out FurnitureDesignStateData state, out string error))
            {
                m_designMalformed++;
                LogProblem("state-decode:" + error,
                    "FurnitureDesignState 载荷非法（" + error + "）→ 整条拒绝");
                return;
            }
            if (state.Revision < m_store.Revision)
            {
                m_designStateStale++;
                LogProblem("state-stale:" + state.Revision,
                    "丢弃过期的家具设计状态 rev=" + state.Revision +
                    "（已应用 " + m_store.Revision + "）→ 不安装任何记录");
                return;
            }
            var install = new List<FurnitureDesignRecord>(state.Designs.Count);
            for (int i = 0; i < state.Designs.Count; i++)
            {
                FurnitureDesignRecord record = state.Designs[i];
                if (!FurnitureDesignCodec.TryValidateRecord(record, m_port.BlockCount,
                        out string recordError))
                {
                    m_designMalformed++;
                    LogProblem("record-invalid:" + recordError,
                        "主机下发的家具设计记录非法（" + recordError + " " +
                        (record == null ? "<null>" : record.Describe()) + "）→ 整条拒绝（不部分应用）");
                    return;
                }
                install.Add(record);
            }
            var installedRecords = new List<FurnitureDesignRecord>(install.Count);
            for (int i = 0; i < install.Count; i++)
            {
                if (!m_port.TryInstallReplica(install[i], out string installReason))
                {
                    m_designRejected++;
                    AddReason("install-" + ReasonCode(installReason));
                    LogProblem("install:" + ReasonCode(installReason),
                        "客户端安装家具设计副本失败 " + install[i].Describe() + "（" +
                        installReason + "）→ 该条不进入副本表（内容对账会请求重发）");
                    continue;
                }
                installedRecords.Add(install[i]);
            }
            for (int i = 0; i < state.Removed.Count; i++)
            {
                if (!m_port.TryRemoveReplica(state.Removed[i], out string removeReason))
                {
                    LogProblem("remove:" + ReasonCode(removeReason),
                        "客户端移除家具设计副本失败 index=" + state.Removed[i] + "（" +
                        removeReason + "）→ 继续对账");
                }
            }
            FurnitureDesignApplyResult applied = m_store.Apply(installedRecords,
                state.Removed, state.Revision);
            if (applied.Stale)
            {
                m_designStateStale++;
                LogProblem("state-stale:" + state.Revision,
                    "丢弃过期的家具设计状态 rev=" + state.Revision + "（已应用 " +
                    m_store.Revision + "）");
                return;
            }
            m_designApplied += applied.Added + applied.Updated;
            m_contentPut += applied.Added;
            m_contentTaken += applied.Removed;
            m_designDuplicate += applied.Duplicate;
            m_designStateConflict += applied.Conflict;
            m_designRemoved += applied.Removed;
            if (applied.Conflict > 0)
            {
                LogProblem("state-conflict",
                    "家具设计状态与本地副本冲突 " + applied.Conflict +
                    " 条 → 按主机内容覆盖（冲突计数 " + m_designStateConflict + "）");
            }
            Log("客户端应用家具设计状态 rev=" + state.Revision +
                (state.Full ? " full" : " delta") + " added=" + applied.Added +
                " updated=" + applied.Updated + " duplicate=" + applied.Duplicate +
                " removed=" + applied.Removed + " · " + (state.Reason ?? string.Empty));
        }

        private void OnContentState(MessageContext context, P.FurnitureContentState message)
        {
            if (IsHosting)
            {
                m_contentDirectionErrors++;
                LogProblem("content-on-host", "主机收到了 FurnitureContentState（方向错误）→ 丢弃");
                return;
            }
            m_contentRecv++;
            if (!FurnitureDesignCodec.TryDecodeContent(message, out FurnitureContentData content,
                    out string error))
            {
                m_contentMalformed++;
                m_contentRejected++;
                LogProblem("content-decode:" + error,
                    "FurnitureContentState 载荷非法（" + error + "）→ 整条拒绝");
                return;
            }
            FurnitureContentReconcileResult reconciled = m_store.Reconcile(content.Indices,
                content.Hashes, content.Revision);
            if (reconciled.Malformed)
            {
                m_contentMalformed++;
                m_contentRejected++;
                LogProblem("content-malformed:" + content.Revision,
                    "家具内容表非法（索引越界 / 重复 / 数组长度不符）rev=" +
                    content.Revision + " → 整条拒绝并请求全量同步");
                m_nextSyncRequestMs = 0;
                return;
            }
            if (reconciled.Stale)
            {
                m_contentStale++;
                LogProblem("content-stale:" + content.Revision,
                    "丢弃过期 / 非法的家具内容表 rev=" + content.Revision + "（已应用 " +
                    m_store.Revision + "）→ 请求全量同步");
                m_nextSyncRequestMs = 0;
                return;
            }
            if (reconciled.Extra > 0 && reconciled.RolledBack != null)
            {
                for (int i = 0; i < reconciled.RolledBack.Count; i++)
                {
                    int index = reconciled.RolledBack[i];
                    if (!m_port.TryRemoveReplica(index, out string removeReason))
                    {
                        LogProblem("rollback-remove:" + ReasonCode(removeReason),
                            "回滚移除客户端家具副本失败 index=" + index + "（" + removeReason +
                            "）→ 继续对账");
                    }
                }
                m_contentRolledBack += reconciled.Extra;
                m_contentTaken += reconciled.Extra;
                LogProblem("content-rollback",
                    "客户端副本表里有 " + reconciled.Extra +
                    " 条主机内容表没有的家具设计 → 已回滚移除（rollback 计数 " +
                    m_contentRolledBack + "）");
            }
            m_contentMissing += reconciled.Missing;
            if (reconciled.Missing > 0)
            {
                m_nextSyncRequestMs = 0;
                LogProblem("content-missing:" + reconciled.Missing,
                    "主机内容表有 " + reconciled.Missing +
                    " 条设计不在客户端副本表（或内容哈希不符）→ 请求全量同步（不显示缺失设计）");
            }
            else
            {
                m_contentApplied++;
                if (content.Full)
                {
                    m_clientSynced = true;
                    m_clientSyncFirstRequestMs = 0;
                    m_nextSyncWarnMs = 0;
                }
            }
            m_contentRefreshFurnitureInventory();
            {
                // 刷新成功：家具面板的槽位 = 主机权威设计表。
            }
            Log("客户端应用家具内容表 rev=" + content.Revision +
                (content.Full ? " full" : " delta") + " matched=" + reconciled.Matched +
                " missing=" + reconciled.Missing + " extra=" + reconciled.Extra +
                " designs=" + m_store.Count + " · " + (content.Reason ?? string.Empty));
        }

        private bool m_contentRefreshFurnitureInventory()
        {
            if (!m_port.TryRefreshFurnitureInventory(out int slotCount, out string reason))
            {
                m_contentRefreshFailed++;
                LogProblem("inventory-refresh:" + ReasonCode(reason),
                    "刷新本机家具库存失败（" + reason + "）→ 面板内容可能滞后（明确失败）");
                return false;
            }
            m_contentRefreshed++;
            return true;
        }

        // ================================================================= 客户端：同步 / 泄漏扫除

        private void RequestFullSync(string reason)
        {
            if (m_transport == null || !m_transport.IsConnected) return;
            var request = new P.FurnitureDesignSyncRequest
            {
                KnownRevision = m_store.Revision,
                Reason = reason ?? string.Empty
            };
            if (!m_transport.TrySend(MessageType.FurnitureDesignSyncRequest,
                    request.ToByteArray()))
            {
                m_designSendFailed++;
                LogProblem("sync-send", "家具设计表全量同步请求发送失败（" + reason +
                    "）→ 下个周期重试");
                return;
            }
            m_designSyncRequests++;
            Log("客户端请求家具设计表全量同步（knownRev=" + m_store.Revision + " · " +
                reason + "）");
        }

        private void SweepClientLibrary(long nowMs)
        {
            m_reinstalled.Clear();
            int reinstalled = m_port.EnsureReplicaIntegrity(m_store.SnapshotSorted(),
                m_reinstalled);
            m_clientLibrarySweeps++;
            m_clientLibraryReinstalled += reinstalled;
            if (reinstalled > 0)
            {
                LogProblem("replica-reinstall:" + reinstalled,
                    "客户端家具副本有 " + reinstalled +
                    " 条被引擎 GC / 其它路径清掉 → 已按主机记录重装");
            }
            m_leakRemoved.Clear();
            int removed = m_port.SweepClientLibraryLeaks(m_store.Indices, m_leakRemoved);
            if (removed > 0)
            {
                m_clientLibraryRejected += removed;
                m_clientLibraryLeaks++;
                m_builder.Clear();
                for (int i = 0; i < m_leakRemoved.Count && i < 8; i++)
                {
                    if (m_builder.Length > 0) m_builder.Append(',');
                    m_builder.Append(m_leakRemoved[i]);
                }
                LogProblem("client-library-leak:" + m_clientLibraryLeaks,
                    "客户端家具库里出现 " + removed + " 条**不是主机下发**的设计（index=" +
                    m_builder + "）→ 已移除；多人家具设计只能由主机创建（clientLibraryRejected=" +
                    m_clientLibraryRejected + "）");
            }
        }

        // ================================================================= 发送辅助

        private bool SendDesignBatchesTo(int clientId,
            IReadOnlyList<FurnitureDesignRecord> upserts, IReadOnlyList<int> removed,
            int revision, string reason)
        {
            int sent = 0;
            while (sent < upserts.Count)
            {
                int bytes = 0;
                var chunk = new List<FurnitureDesignRecord>(FurnitureDesignCodec.MaxDesignsPerState);
                while (sent < upserts.Count && chunk.Count < FurnitureDesignCodec.MaxDesignsPerState)
                {
                    int size = FurnitureDesignCodec.EstimateRecordBytes(upserts[sent]);
                    if (chunk.Count > 0 && bytes + size > MaxStatePayloadBytes) break;
                    bytes += size;
                    chunk.Add(upserts[sent]);
                    sent++;
                }
                bool last = sent >= upserts.Count;
                if (!SendDesignStateTo(clientId, chunk, last ? removed : null, revision,
                        full: false, reason))
                    return false;
            }
            if (upserts.Count == 0 && removed != null && removed.Count > 0)
            {
                return SendDesignStateTo(clientId, Array.Empty<FurnitureDesignRecord>(),
                    removed, revision, full: false, reason);
            }
            return true;
        }

        private bool SendDesignStateTo(int clientId,
            IReadOnlyList<FurnitureDesignRecord> designs, IReadOnlyList<int> removed,
            int revision, bool full, string reason)
        {
            if (m_transport == null) return false;
            var state = new FurnitureDesignStateData(revision, full, reason, designs, removed);
            if (!FurnitureDesignCodec.TryEncodeState(state, out P.FurnitureDesignState encoded,
                    out string error))
            {
                m_designSendFailed++;
                LogProblem("state-encode:" + error,
                    "FurnitureDesignState 编码失败（" + error + "）→ 本批不发送（明确失败）");
                return false;
            }
            byte[] payload = encoded.ToByteArray();
            if (payload.Length > FurnitureDesignCodec.MaxPayloadBytes)
            {
                m_designSendFailed++;
                LogProblem("state-too-large:" + payload.Length,
                    "FurnitureDesignState 编码后 " + payload.Length + " 字节 > 上限 " +
                    FurnitureDesignCodec.MaxPayloadBytes + " → 本批不发送（明确失败）");
                return false;
            }
            if (!m_transport.TrySendTo(clientId, MessageType.FurnitureDesignState, payload))
            {
                m_designSendFailed++;
                LogProblem("state-send:" + clientId,
                    "FurnitureDesignState 发送失败（clientId=" + clientId + " designs=" +
                    designs.Count + " removed=" + (removed == null ? 0 : removed.Count) +
                    "）");
                return false;
            }
            m_designSent += designs.Count;
            return true;
        }

        private bool SendContentTo(int clientId, bool full, string reason)
        {
            if (m_transport == null) return false;
            m_contentIndices.Clear();
            m_contentHashes.Clear();
            var sorted = new List<FurnitureDesignRecord>(m_lastHostRecords.Values);
            sorted.Sort((a, b) => a.Index.CompareTo(b.Index));
            for (int i = 0; i < sorted.Count; i++)
            {
                m_contentIndices.Add(sorted[i].Index);
                m_contentHashes.Add(sorted[i].ContentHash);
            }
            var content = new FurnitureContentData(m_hostRevision, full, reason,
                m_contentIndices, m_contentHashes);
            if (!FurnitureDesignCodec.TryEncodeContent(content,
                    out P.FurnitureContentState encoded, out string error))
            {
                m_contentSendFailed++;
                LogProblem("content-encode:" + error,
                    "FurnitureContentState 编码失败（" + error + "）→ 不发送（明确失败）");
                return false;
            }
            if (!m_transport.TrySendTo(clientId, MessageType.FurnitureContentState,
                    encoded.ToByteArray()))
            {
                m_contentSendFailed++;
                LogProblem("content-send:" + clientId,
                    "FurnitureContentState 发送失败（clientId=" + clientId + " designs=" +
                    m_contentIndices.Count + "）");
                return false;
            }
            m_contentSent++;
            return true;
        }

        // ================================================================= 在途 / 会话清理

        private void ExpirePendingRequests(long nowMs)
        {
            if (m_pending.Count == 0) return;
            m_expiredPending.Clear();
            foreach (KeyValuePair<long, PendingDesignRequest> item in m_pending)
            {
                if (nowMs - item.Value.SentMs < RequestTimeoutMs) continue;
                m_expiredPending.Add(item.Key);
            }
            if (m_expiredPending.Count == 0) return;
            for (int i = 0; i < m_expiredPending.Count; i++)
            {
                m_pending.Remove(m_expiredPending[i]);
                m_designTimeout++;
                LogProblem("request-timeout", "家具设计请求超时 seq=" + m_expiredPending[i] +
                    "（" + RequestTimeoutMs + "ms 没有回执）→ 客户端没有新增任何本地设计；" +
                    "累计超时 " + m_designTimeout);
            }
            m_port.TryShowClientMessage("家具设计请求超时：主机没有回执，本次没有创建设计");
        }

        private void ResetClientRequests(string reason)
        {
            if (m_pending.Count > 0)
            {
                Log("作废在途家具请求 " + m_pending.Count + " 条（" + reason + "）");
                m_pending.Clear();
            }
            m_clientSynced = false;
            m_clientSyncFirstRequestMs = 0;
            m_nextSyncRequestMs = 0;
            // 重连 / 主机重启：主机 revision 域可能从 1 重新开始，
            // 保留旧 revision 会把合法的新快照当成 stale。设计本体保留，等全量对账。
            m_store.ResetRevision();
        }

        private bool IsHosting => m_control?.IsHosting == true;

        // ================================================================= 诊断

        private void AddReason(string reason)
        {
            string code = ReasonCode(reason);
            m_rejectedByReason.TryGetValue(code, out long count);
            m_rejectedByReason[code] = count + 1;
        }

        private static string ReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "none";
            int colon = reason.IndexOf(':');
            string code = colon > 0 ? reason.Substring(0, colon) : reason;
            code = code.Trim();
            if (code.Length == 0) return "none";
            return code.Length > 48 ? code.Substring(0, 48) : code;
        }

        private string RejectedReasons()
        {
            if (m_rejectedByReason.Count == 0) return "none";
            m_builder.Clear();
            foreach (KeyValuePair<string, long> item in m_rejectedByReason)
            {
                if (m_builder.Length > 0) m_builder.Append(',');
                m_builder.Append(item.Key).Append('=').Append(item.Value);
            }
            return m_builder.ToString();
        }

        private string Summary()
        {
            return "furnitureDesignSent=" + m_designSent +
                " furnitureDesignRecv=" + m_designRecv +
                " furnitureDesignApplied=" + m_designApplied +
                " furnitureDesignAccepted=" + m_designAccepted +
                " furnitureDesignRejected=" + m_designRejected +
                " furnitureDesignRejectedReasons=[" + RejectedReasons() + "]" +
                " furnitureDesignMalformed=" + m_designMalformed +
                " furnitureDesignDuplicate=" + m_designDuplicate +
                " furnitureDesignRemoved=" + m_designRemoved +
                " furnitureDesignTimeout=" + m_designTimeout +
                " furnitureDesignSendFailed=" + m_designSendFailed +
                " furnitureDesignDirErr=" + m_designDirectionErrors +
                " furnitureDesignStateStale=" + m_designStateStale +
                " furnitureDesignStateConflict=" + m_designStateConflict +
                " furnitureDesignStateMissing=" + m_designStateMissing +
                " furnitureDesignSyncRequests=" + m_designSyncRequests +
                " furnitureDesignSyncTimeout=" + m_designSyncTimeout +
                " furnitureDesignResultUnknown=" + m_designResultUnknown +
                " furnitureDesignPickableMissing=" + m_designPickableMissing +
                " · furnitureContentSyncSent=" + m_contentSent +
                " furnitureContentSyncRecv=" + m_contentRecv +
                " furnitureContentSyncApplied=" + m_contentApplied +
                " furnitureContentSyncRejected=" + m_contentRejected +
                " furnitureContentSyncStale=" + m_contentStale +
                " furnitureContentSyncMalformed=" + m_contentMalformed +
                " furnitureContentSyncRolledBack=" + m_contentRolledBack +
                " furnitureContentSyncPut=" + m_contentPut +
                " furnitureContentSyncTaken=" + m_contentTaken +
                " furnitureContentSyncMissing=" + m_contentMissing +
                " furnitureContentSyncSendFailed=" + m_contentSendFailed +
                " furnitureContentSyncDirErr=" + m_contentDirectionErrors +
                " furnitureContentSyncRefreshed=" + m_contentRefreshed +
                " furnitureContentSyncRefreshFailed=" + m_contentRefreshFailed +
                " · clientLibraryReplicaDesigns=" + m_store.Count +
                " clientLibraryAdopted=" + m_clientAdopted +
                " clientLibrarySynced=" + m_clientSynced +
                " clientLibraryRejected=" + m_clientLibraryRejected +
                " clientLibraryLeaks=" + m_clientLibraryLeaks +
                " clientLibrarySweeps=" + m_clientLibrarySweeps +
                " clientLibraryReinstalled=" + m_clientLibraryReinstalled +
                " · rev=" + (IsHosting ? m_hostRevision : m_store.Revision);
        }

        private long m_clientLibraryRejected;
        private long m_clientLibraryLeaks;
        private long m_clientLibrarySweeps;
        private long m_clientLibraryReinstalled;

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(m_clock() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }
    }
}

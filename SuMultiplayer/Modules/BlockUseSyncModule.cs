using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 方块使用 / 交互 + 容器（方块实体库存）同步模块（**新文件**）。
    ///
    /// 主机权威模型（与地形编辑 / 爆炸请求同一条纪律）：
    ///   · 客户端**不**在本地跑交互：输入外壳在 <c>ComponentPlayer.Update</c> 之前把
    ///     <c>PlayerInput.Interact</c> 吞掉，改成 <c>BlockUseRequest</c>（每连接单调序号）；
    ///   · 主机校验（已加入的玩家端、距离、方块存在且内容一致、方块行为 / 方块实体库存）
    ///     后**由主机执行**：门 / 按钮 / 家具等走引擎方块行为（结果随既有 CellChange 下发）；
    ///     工作台 / 箱子 / 熔炉 / 分配器走方块实体库存 —— 主机下发权威 <c>ContainerState</c>，
    ///     客户端用副本面板显示，绝不本地改权威库存；
    ///   · 客户端面板里的槽位变化 → <c>ContainerEditRequest</c>（带 base 权威快照 + 玩家背包
    ///     快照）→ 主机校验并落地 → 广播 <c>ContainerState</c> + <c>ContainerEditResult</c>；
    ///     拒绝 / 超时 → 客户端回滚到主机权威值，并在日志里写明确原因；
    ///   · 主机自己打开的面板 / 熔炉烧炼等变化由主机轮询"被客户端打开的容器"后广播。
    ///
    /// 无静态状态；引擎类型全部在 <see cref="IBlockUsePort"/> 实现里。
    /// </summary>
    public sealed class BlockUseSyncModule : IMultiplayerModule, ISuMPMessageModule,
        IBlockUseRequestSink
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;
        private const int HostPollIntervalMs = 200;
        /// <summary>熔炉炉况的主机轮询间隔：进度条 / 火焰要跟手，200ms 与容器轮询同频。</summary>
        private const int FurnacePollIntervalMs = 200;
        /// <summary>一拍最多下发多少个熔炉（被打开的 + 附近燃烧的）。</summary>
        private const int MaxFurnaceTargets = 64;
        /// <summary>客户端 / 主机缓存的熔炉状态键上限（超出整表清空并写一行日志，不是静默丢弃消息）。</summary>
        private const int MaxTrackedFurnaceStates = 256;
        /// <summary>打开面板时最多接受多久之前收到的炉况（超过就明确失败，不拿陈旧值冒充当前）。</summary>
        private const int FurnaceStateMaxAgeMs = 2000;
        private const int RequestTimeoutMs = 5000;
        private const int EditTimeoutMs = 5000;
        private const int MaxPendingUses = 64;
        private const int MaxOpenContainers = 64;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IBlockUsePort m_port;
        private readonly Func<double> m_clock;
        private readonly Dictionary<long, long> m_pendingUses = new Dictionary<long, long>();
        private readonly Dictionary<int, ContainerKey> m_openContainers =
            new Dictionary<int, ContainerKey>();
        private readonly List<ContainerKey> m_pollKeys = new List<ContainerKey>(16);
        private readonly List<ContainerStateData> m_pollStates = new List<ContainerStateData>(16);
        /// <summary>主机：本拍要下发炉况的熔炉（被客户端打开的 + 附近燃烧的），有界。</summary>
        private readonly List<ContainerKey> m_openFurnaceKeys = new List<ContainerKey>(8);
        private readonly List<FurnacePollTarget> m_furnaceTargets =
            new List<FurnacePollTarget>(16);
        /// <summary>主机：最近一次下发过的炉况（变化才发；打开面板时无条件发一次）。</summary>
        private readonly Dictionary<ContainerKey, FurnaceStateData> m_lastFurnaceStates =
            new Dictionary<ContainerKey, FurnaceStateData>();
        /// <summary>客户端：最近收到的炉况（打开面板时重放，解决"炉况先于面板打开到达"的顺序）。</summary>
        private readonly Dictionary<ContainerKey, PendingFurnaceState> m_furnacePending =
            new Dictionary<ContainerKey, PendingFurnaceState>();
        /// <summary>客户端：每个熔炉已应用的 revision（旧状态按 stale 拒绝）。</summary>
        private readonly Dictionary<ContainerKey, int> m_furnaceAppliedRevisions =
            new Dictionary<ContainerKey, int>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        /// <summary>
        /// 远程动作声音中继（可选端口，实现 = Modules/RemoteAudioSyncModule.cs）。
        /// 主机执行远端 BlockUseRequest / 容器编辑的同步窗口内捕获引擎在本端播放的声音，
        /// 补发给请求者与附近客户端；缺失时只记录一次明确警告，不影响方块使用本身。
        /// </summary>
        private IRemoteAudioRelayPort m_remoteAudio;
        private Action<int, string, string, bool> m_clientJoinedHandler;
        private Action<int, string> m_clientLeftHandler;
        private bool m_attached;
        private bool m_wasConnected;
        private int m_lastLocalClientId = int.MinValue;
        private long m_nextUseSequence;
        private long m_nextEditSequence;
        private long m_lastSummaryMs;
        private long m_nextHostPollMs;
        private long m_nextHostFurnacePollMs;

        // 客户端当前打开的副本面板（key + 最近一次应用的权威状态 + 玩家背包基线）
        private ContainerKey? m_openKey;
        private int m_openRevision;
        private int[] m_openValues;
        private int[] m_openCounts;
        private PlayerInventorySnapshot m_playerBase;

        // 客户端最近一条 ContainerState（主机先发状态、再发 BlockUseResult；结果用它打开面板）
        private ContainerStateData m_pendingContainerState;
        private long m_pendingContainerStateMs;

        // 客户端在途的容器编辑（同一时刻最多一条；超时回滚）
        private bool m_pendingEditActive;
        private long m_pendingEditSequence;
        private long m_pendingEditSentMs;
        private ContainerKey m_pendingEditKey;
        private int m_pendingEditBaseRevision;
        private int[] m_pendingEditBaseValues;
        private int[] m_pendingEditBaseCounts;
        private int[] m_pendingEditDesiredValues;
        private int[] m_pendingEditDesiredCounts;
        private PlayerInventorySnapshot m_pendingEditPlayerBase;

        // 刚被主机拒绝的那一次编辑（指纹相同就**不再重发**：防止拒绝 → 回滚 → 再发同一条的死循环）
        private bool m_lastRejectedValid;
        private ContainerKey m_lastRejectedKey;
        private int m_lastRejectedBaseRevision;
        private long m_lastRejectedFingerprint;

        // ---------------------------------------------------------------- 计数
        private long m_useSubmitted;
        private long m_useSent;
        private long m_useSendFailures;
        private long m_useReceived;
        private long m_useAccepted;
        private long m_useRejected;
        private long m_useTimeouts;
        private long m_useResultsReceived;
        private long m_useResultsAccepted;
        private long m_useResultsRejected;
        private long m_useResultsUnknown;
        private long m_useResultsMalformed;
        private long m_useDirectionErrors;

        private long m_containerStatesSent;
        private long m_containerStateSendFailures;
        private long m_containerStatesReceived;
        private long m_containerStatesApplied;
        private long m_containerStatesStale;
        private long m_containerStatesMalformed;
        private long m_containerStatesWithoutOpen;
        private long m_containerBroadcastPeers;

        // ---------------------------------------------------------------- 熔炉炉况（FURNACE_STATE）
        private long m_furnaceStatesSent;
        private long m_furnaceStateSendFailures;
        private long m_furnaceStatesReceived;
        private long m_furnaceStatesApplied;
        private long m_furnaceStatesRejected;
        private long m_furnaceStatesStale;
        private long m_furnaceStatesMalformed;
        private long m_furnaceStateDirectionErrors;
        /// <summary>主机：因"不在任何客户端附近且没人打开"而跳过的燃烧熔炉数（每拍累计）。</summary>
        private long m_furnaceStateSkippedDistance;
        /// <summary>主机：抓不到（方块实体 / 区块卸载 / 方块已变）而跳过的熔炉数。</summary>
        private long m_furnaceStateSkippedUnloaded;
        /// <summary>客户端：真的落地了燃烧外观格子值的次数。</summary>
        private long m_furnaceLitApplied;
        /// <summary>客户端：真的把热量 / 进度 / 余火写进副本组件的次数。</summary>
        private long m_furnaceProgressApplied;
        /// <summary>客户端：打开熔炉时没有可用的权威炉况（明确失败）的次数。</summary>
        private long m_furnaceOpenStateMissing;
        /// <summary>主机：本拍纳入目标的熔炉数 / 其中"关闭但可见（附近）"的熔炉数。</summary>
        private long m_furnaceTargetsPolled;
        private long m_furnaceNearbyTargets;
        /// <summary>主机：数值未变化而未下发的轮询次数。</summary>
        private long m_furnaceStatesSuppressed;

        private long m_editSubmitted;
        private long m_editSent;
        private long m_editSendFailures;
        private long m_editReceived;
        private long m_editApplied;
        private long m_editRejected;
        private long m_editTimeouts;
        private long m_editResultsReceived;
        private long m_editResultsRejected;
        private long m_editResultsUnknown;
        private long m_editResultsMalformed;
        private long m_editDirectionErrors;
        /// <summary>客户端把"被拒绝 / 超时"的编辑回滚到权威状态的次数（含背包恢复）。</summary>
        private long m_editReverts;
        /// <summary>与刚被拒绝的编辑完全相同的重发被抑制的次数（防死循环）。</summary>
        private long m_editSuppressed;
        /// <summary>主机落地了的配方格变化次数（工作台）。</summary>
        private long m_craftGridApplied;
        /// <summary>主机取走合成产物的次数 / 总个数。</summary>
        private long m_resultTaken;
        private long m_resultTakenItems;
        /// <summary>主机落地的"同物品两槽拆分"次数（面板里的拆分不再被回滚）。</summary>
        private long m_splitApplied;
        /// <summary>主机落地的容器内物品移动次数（非拆分、非取产物）。</summary>
        private long m_moveApplied;
        /// <summary>主机落地的"取产物 + 放进容器"同一条编辑次数。</summary>
        private long m_takeAndPlaceApplied;
        /// <summary>客户端本地预测的产物与主机重算不一致的次数（以主机为准）。</summary>
        private long m_craftPredictionMismatch;
        /// <summary>主机侧看到"请求者是创造仓库"的编辑次数（接受 + 因 creative-* 规则被拒）。</summary>
        private long m_creativeInventoryDetected;
        /// <summary>创造请求者的编辑被接受（守恒被创造规则替代）的次数。</summary>
        private long m_creativeTransferAllowed;
        /// <summary>
        /// 创造模式放行的 data 变体件数（完整值不在创造仓库，但 contents 由创造仓库提供：
        /// 内存条地图 / 告示牌文字 / 家具设计 id）。允许计数，不是拒绝原因。
        /// </summary>
        private long m_creativeDataVariantAllowed;
        /// <summary>创造模式放行的"请求者权威背包里已有该物品"件数（移动，不是创造）。</summary>
        private long m_creativeInventoryMoveAllowed;
        private long m_playerLedgerRemoved;
        private long m_playerLedgerAdded;
        private long m_playerLedgerLeftover;
        private long m_containerStatesDeferred;
        private string m_lastRevertReason;
        private readonly Dictionary<string, long> m_editRejectedByReason =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private long m_containerViewsOpened;
        private long m_containerViewsClosed;
        private long m_containerViewOpenFailures;
        private long m_containerViewStateMissing;
        private long m_craftingViewOpened;
        private long m_craftingViewItems;

        public BlockUseSyncModule(IBlockUsePort port, Func<double> clock = null)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_clock = clock ?? (() => Time.RealTime);
            m_descriptor = new ModuleDescriptor("block-use", "方块使用 / 容器同步（主机权威）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "block.use" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "方块使用：" + Summary() + " · " + m_port.Describe();

        /// <summary>客户端缓存的"刚收到的炉况"：打开面板时重放（带接收时间，过期不冒充当前值）。</summary>
        private readonly struct PendingFurnaceState
        {
            public PendingFurnaceState(FurnaceStateData state, long receivedMs)
            {
                State = state;
                ReceivedMs = receivedMs;
            }

            public FurnaceStateData State { get; }
            public long ReceivedMs { get; }
        }

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 方块使用 / 容器同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "方块使用 / 容器同步不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到方块使用 / 容器消息");
            }
            // 远程动作声音中继（可选）：主机替远端玩家执行交互时，方块行为只在主机调用
            // SubsystemAudio（SwitchElectricElement.cs:27 等），请求者本来什么都听不到。
            // 端口缺失时明确记录一次；方块使用本身照常工作（不是功能兜底）。
            if (context.TryGetPort(out m_remoteAudio) && m_remoteAudio != null)
            {
                Log("远程动作声音中继已接入：主机执行远端交互期间捕获声音，请求者与附近客户端可听到");
            }
            else
            {
                m_remoteAudio = null;
                Log("警告：没有远程动作声音中继端口（IRemoteAudioRelayPort）→ 客户端听不到主机执行交互的声音");
            }
            m_clientJoinedHandler = HandleClientJoined;
            m_clientLeftHandler = HandleClientLeft;
            m_transport.ClientJoined += m_clientJoinedHandler;
            m_transport.ClientLeft += m_clientLeftHandler;
            m_port.SetRequestSink(this);
            // 首次 Tick 前不把"还没握手"的 -1 当成一次连接更换（避免一条无意义的重置日志）。
            m_lastLocalClientId = int.MinValue;
            m_attached = true;
            Log("方块使用 / 容器同步就绪：客户端 = 请求 + 副本面板；主机 = 校验 + 引擎执行 + 权威库存");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.BlockUseRequest, P.BlockUseRequest.Parser,
                new DelegateMessageHandler<P.BlockUseRequest>(OnBlockUseRequest), ModuleId);
            registry.Register((int)MessageType.BlockUseResult, P.BlockUseResult.Parser,
                new DelegateMessageHandler<P.BlockUseResult>(OnBlockUseResult), ModuleId);
            registry.Register((int)MessageType.ContainerState, P.ContainerState.Parser,
                new DelegateMessageHandler<P.ContainerState>(OnContainerState), ModuleId);
            registry.Register((int)MessageType.FurnaceState, P.FurnaceState.Parser,
                new DelegateMessageHandler<P.FurnaceState>(OnFurnaceState), ModuleId);
            registry.Register((int)MessageType.ContainerEditRequest,
                P.ContainerEditRequest.Parser,
                new DelegateMessageHandler<P.ContainerEditRequest>(OnContainerEditRequest), ModuleId);
            registry.Register((int)MessageType.ContainerEditResult,
                P.ContainerEditResult.Parser,
                new DelegateMessageHandler<P.ContainerEditResult>(OnContainerEditResult), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_port.SetRequestSink(null);
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
            ResetClientSession("detach");
            m_openContainers.Clear();
            m_pendingUses.Clear();
            m_lastProblemMs.Clear();
            m_lastFurnaceStates.Clear();
            m_furnacePending.Clear();
            m_furnaceAppliedRevisions.Clear();
            m_furnaceTargets.Clear();
            m_openFurnaceKeys.Clear();
            m_attached = false;
            Log("方块使用 / 容器同步下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            long nowMs = (long)(tick.Now * 1000.0);
            bool hosting = IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            if (hosting)
            {
                if (connected && nowMs >= m_nextHostPollMs)
                {
                    m_nextHostPollMs = nowMs + HostPollIntervalMs;
                    PollHostContainers();
                }
                // 熔炉炉况单独节拍（同一 200ms 口径）：只发给"打开熔炉的客户端 + 附近看得见
                // 燃烧熔炉的客户端"，数值变化才发；关闭但可见的燃烧熔炉也在这里同步外观。
                if (connected && nowMs >= m_nextHostFurnacePollMs)
                {
                    m_nextHostFurnacePollMs = nowMs + FurnacePollIntervalMs;
                    PollHostFurnaces(nowMs);
                }
            }
            else
            {
                // 连接换了（重连 = 新连接，序号从 1 重新开始）：旧连接的在途请求全部作废。
                int localClientId = m_transport?.LocalClientId ?? -1;
                if (localClientId != m_lastLocalClientId)
                {
                    if (m_lastLocalClientId != int.MinValue)
                    {
                        int droppedUses = m_pendingUses.Count;
                        bool hadEdit = m_pendingEditActive;
                        ResetClientSession("client-id-change");
                        LogProblem("client-id-change", "客户端连接已更换（LocalClientId " +
                            m_lastLocalClientId + " → " + localClientId + "）→ 作废在途使用请求 " +
                            droppedUses + " 条 / 在途编辑=" + hadEdit +
                            "（旧序号的回执不再匹配）");
                    }
                    m_lastLocalClientId = localClientId;
                }
                if (connected && !m_wasConnected)
                {
                    m_nextUseSequence = 0;
                    m_nextEditSequence = 0;
                    m_pendingUses.Clear();
                    m_pendingEditActive = false;
                }
                if (connected) PumpClientContainer(nowMs);
                ExpirePendingUses(nowMs);
            }
            m_wasConnected = connected;
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ================================================================= 客户端 -> 主机：使用请求

        /// <summary>
        /// 输入外壳的提交入口（Engine/ContainerReplicaEngine.cs 的 IEntityUseInputSink 调到这里）。
        /// 返回 true = 已入队发送；调用方随后必须吞掉原生输入（客户端不产生本地交互效果）。
        /// </summary>
        public bool TrySubmitUse(in BlockUseRequestData request, out string reason)
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
            m_useSubmitted++;
            long sequence = NextUseSequence();
            BlockUseRequestData sequenced = request.WithSequence(sequence);
            byte[] payload = BlockUseCodec.EncodeRequestBytes(in sequenced);
            if (!m_transport.TrySend(MessageType.BlockUseRequest, payload))
            {
                m_useSendFailures++;
                reason = "send-failed";
                LogProblem("use-send", "方块使用请求发送失败（" + sequenced.Describe() +
                    "）→ 客户端**不执行**本地交互（本次点击无效果），累计失败 " + m_useSendFailures);
                return false;
            }
            m_useSent++;
            m_pendingUses[sequence] = (long)(m_clock() * 1000.0);
            if (m_pendingUses.Count > MaxPendingUses)
            {
                long oldest = long.MaxValue;
                foreach (KeyValuePair<long, long> item in m_pendingUses)
                {
                    if (item.Key < oldest) oldest = item.Key;
                }
                if (oldest != long.MaxValue)
                {
                    m_pendingUses.Remove(oldest);
                    m_useTimeouts++;
                    LogProblem("use-timeout-overflow", "在途使用请求超过 " + MaxPendingUses +
                        " 条 → 最旧的一条按超时处理（不静默丢）");
                }
            }
            return true;
        }

        private void SendCloseRequest(ContainerKey key)
        {
            if (m_transport == null || !m_transport.IsConnected) return;
            long sequence = NextUseSequence();
            var request = new BlockUseRequestData(sequence, key.X, key.Y, key.Z, 0, 0,
                BlockUseActions.Close, 0f, 0f, 0f, 0f, 0f, 0f);
            byte[] payload = BlockUseCodec.EncodeRequestBytes(in request);
            if (!m_transport.TrySend(MessageType.BlockUseRequest, payload))
            {
                m_useSendFailures++;
                LogProblem("close-send", "关闭容器请求发送失败（" + key.Describe() +
                    "）→ 主机保留打开记录到该客户端断开/打开其它容器为止");
                return;
            }
            m_useSent++;
        }

        private long NextUseSequence()
        {
            m_nextUseSequence++;
            if (m_nextUseSequence <= 0) m_nextUseSequence = 1;
            return m_nextUseSequence;
        }

        private long NextEditSequence()
        {
            m_nextEditSequence++;
            if (m_nextEditSequence <= 0) m_nextEditSequence = 1;
            return m_nextEditSequence;
        }

        // ================================================================= 主机：收到使用请求

        private void OnBlockUseRequest(MessageContext context, P.BlockUseRequest message)
        {
            m_useReceived++;
            if (!IsHosting)
            {
                m_useDirectionErrors++;
                LogProblem("use-request-on-client", "客户端收到了 BlockUseRequest（方向错误）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || context.Type != MessageType.BlockUseRequest)
            {
                m_useRejected++;
                LogProblem("use-source", "BlockUseRequest 来源非法（clientId=" + clientId + "）→ 拒绝");
                return;
            }
            if (!BlockUseCodec.TryDecodeRequest(message, out BlockUseRequestData request,
                    out string decodeError))
            {
                m_useRejected++;
                LogProblem("use-malformed:" + decodeError,
                    "BlockUseRequest 载荷非法（" + decodeError + "，clientId=" + clientId +
                    "）→ 整条拒绝");
                SendUseResult(clientId, new BlockUseResultData(0, false, "malformed:" + decodeError,
                    (int)message.Action, ContainerKinds.None, message.X, message.Y, message.Z));
                return;
            }

            if (request.Action == BlockUseActions.Close)
            {
                bool removed = false;
                if (m_openContainers.TryGetValue(clientId, out ContainerKey openKey) &&
                    openKey.X == request.X && openKey.Y == request.Y &&
                    openKey.Z == request.Z)
                {
                    m_openContainers.Remove(clientId);
                    removed = true;
                }
                if (removed) m_containerViewsClosed++;
                SendUseResult(clientId, new BlockUseResultData(request.Sequence, true,
                    string.Empty, BlockUseActions.Close, ContainerKinds.None, request.X,
                    request.Y, request.Z));
                return;
            }

            // 远程动作声音：只在这段同步执行期间打开捕获窗口（引擎方块行为在主机本端播放
            // 声音）。finally 保证动作抛异常时窗口也会关闭 —— 不允许泄漏捕获状态。
            var anchor = new Vector3(request.X, request.Y, request.Z);
            m_remoteAudio?.BeginRemoteAction(clientId, anchor, "block-use");
            bool useAccepted;
            BlockUseHostOutcome useOutcome;
            string useReason;
            try
            {
                useAccepted = m_port.TryExecuteUse(clientId, in request, out useOutcome,
                    out useReason);
            }
            finally
            {
                m_remoteAudio?.EndRemoteAction(clientId, "block-use");
            }
            if (!useAccepted)
            {
                m_useRejected++;
                LogProblem("use-rejected:" + useReason, "主机拒绝 clientId=" + clientId + " 的方块使用 " +
                    request.Describe() + "（" + useReason + "）→ 客户端不产生任何本地效果");
                SendUseResult(clientId, new BlockUseResultData(request.Sequence, false, useReason,
                    request.Action, ContainerKinds.None, request.X, request.Y, request.Z));
                return;
            }
            BlockUseHostOutcome outcome = useOutcome;

            m_useAccepted++;
            if (outcome == null || outcome.Kind == BlockUseTargetKind.Native)
            {
                SendUseResult(clientId, new BlockUseResultData(request.Sequence, true,
                    string.Empty, request.Action, ContainerKinds.None, request.X, request.Y,
                    request.Z));
                return;
            }

            ContainerStateData state = outcome.State;
            if (state == null || !state.Key.Equals(new ContainerKey(request.X, request.Y,
                    request.Z, outcome.ContainerKind)))
            {
                m_useRejected++;
                const string missing = "container-state-unavailable";
                LogProblem("use-state-missing", "主机接受了 clientId=" + clientId + " 的容器使用 " +
                    request.Describe() + " 但没有拿到权威库存状态 → 按拒绝回执（客户端不打开面板）");
                SendUseResult(clientId, new BlockUseResultData(request.Sequence, false, missing,
                    request.Action, outcome.ContainerKind, request.X, request.Y, request.Z));
                return;
            }

            if (!m_openContainers.ContainsKey(clientId) &&
                m_openContainers.Count >= MaxOpenContainers)
            {
                m_useRejected++;
                const string full = "open-container-limit";
                LogProblem("open-limit", "主机的容器打开记录已达上限 " + MaxOpenContainers +
                    "（clientId=" + clientId + "）→ 拒绝打开（客户端不会显示半同步面板）");
                SendUseResult(clientId, new BlockUseResultData(request.Sequence, false, full,
                    request.Action, outcome.ContainerKind, request.X, request.Y, request.Z));
                return;
            }
            // 先发权威状态、再发结果：客户端收到结果时副本面板已经有正确内容（TCP 有序）。
            if (!SendContainerStateTo(clientId, state))
            {
                m_useRejected++;
                const string sendFailed = "container-state-send-failed";
                SendUseResult(clientId, new BlockUseResultData(request.Sequence, false, sendFailed,
                    request.Action, outcome.ContainerKind, request.X, request.Y, request.Z));
                return;
            }
            if (m_openContainers.Count < MaxOpenContainers || m_openContainers.ContainsKey(clientId))
                m_openContainers[clientId] = state.Key;
            // 熔炉：在结果之前补发一条权威炉况。客户端收到结果才打开面板，但此时炉况已经在手，
            // 面板第一帧就能显示火焰 / 进度（不会先闪一帧默认值）。打开时**无条件**发一次，
            // 这样关闭后重开 / 重连都能立刻拿到当前状态。
            if (state.ContainerKind == ContainerKinds.Furnace)
                SendFurnaceStateForOpen(clientId, state.Key);
            SendUseResult(clientId, new BlockUseResultData(request.Sequence, true, string.Empty,
                request.Action, outcome.ContainerKind, request.X, request.Y, request.Z));
        }

        private void SendUseResult(int clientId, in BlockUseResultData result)
        {
            if (m_transport == null || result.Sequence == 0) return;
            byte[] payload = BlockUseCodec.EncodeResultBytes(in result);
            if (!m_transport.TrySendTo(clientId, MessageType.BlockUseResult, payload))
            {
                LogProblem("use-result-send", "BlockUseResult 发送失败（clientId=" + clientId +
                    " seq=" + result.Sequence + " accepted=" + result.Accepted +
                    "）→ 客户端会按请求超时处理（不猜结果）");
            }
        }

        // ================================================================= 客户端：处理使用结果

        private void OnBlockUseResult(MessageContext context, P.BlockUseResult message)
        {
            m_useResultsReceived++;
            if (IsHosting)
            {
                m_useDirectionErrors++;
                LogProblem("use-result-on-host", "主机收到了 BlockUseResult（方向错误）→ 丢弃");
                return;
            }
            if (!BlockUseCodec.TryConvertResult(message, out BlockUseResultData result,
                    out string error))
            {
                m_useResultsMalformed++;
                LogProblem("use-result-malformed:" + error,
                    "BlockUseResult 载荷非法（" + error + "）→ 丢弃");
                return;
            }
            if (!m_pendingUses.Remove(result.Sequence))
            {
                m_useResultsUnknown++;
                LogProblem("use-result-unknown", "收到未知序号的使用回执 seq=" + result.Sequence +
                    "（重连 / 重复 / 主机重启）→ 不应用");
                return;
            }
            if (!result.Accepted)
            {
                m_useResultsRejected++;
                m_useRejected++;
                m_pendingContainerState = null;
                LogProblem("use-denied:" + result.Reason, "方块使用被主机拒绝 seq=" +
                    result.Sequence + " @ " + result.X + "," + result.Y + "," + result.Z +
                    "（" + result.Reason + "）→ 客户端没有本地效果");
                return;
            }
            m_useResultsAccepted++;
            if (!result.OpensContainer)
            {
                // 原生交互（门 / 按钮 / 家具 / ...）：结果随既有 CellChange 下发，这里不做本地预测。
                return;
            }
            ContainerStateData state = m_pendingContainerState;
            long stateAge = (long)(m_clock() * 1000.0) - m_pendingContainerStateMs;
            if (state != null && stateAge > 2000)
            {
                m_pendingContainerState = null;
                state = null;
            }
            if (state == null || state.ContainerKind != result.ContainerKind ||
                state.X != result.X || state.Y != result.Y || state.Z != result.Z)
            {
                m_containerViewStateMissing++;
                m_containerViewOpenFailures++;
                LogProblem("open-state-missing", "主机接受了打开容器 seq=" + result.Sequence +
                    " @ " + result.X + "," + result.Y + "," + result.Z + " kind=" +
                    ContainerKinds.Name(result.ContainerKind) +
                    "，但没有收到对应的 ContainerState（或坐标不符）→ 本次不打开面板（明确失败）");
                m_pendingContainerState = null;
                return;
            }
            m_pendingContainerState = null;
            OpenContainerView(state);
        }

        private void OpenContainerView(ContainerStateData state)
        {
            if (!m_port.TryOpenContainerView(state, out string reason))
            {
                m_containerViewOpenFailures++;
                LogProblem("open-failed:" + reason, "打开容器副本面板失败 " +
                    state.Describe() + "（" + reason + "）→ 客户端没有面板（明确失败）");
                return;
            }
            m_openKey = state.Key;
            m_openRevision = state.Revision;
            m_openValues = (int[])state.Values.Clone();
            m_openCounts = (int[])state.Counts.Clone();
            m_lastRejectedValid = false;
            m_containerViewsOpened++;
            if (state.ContainerKind == ContainerKinds.CraftingTable)
            {
                m_craftingViewOpened++;
                m_craftingViewItems += state.NonEmptySlots;
            }
            if (m_port.TryCapturePlayerInventory(out PlayerInventorySnapshot player,
                    out string playerReason))
            {
                m_playerBase = player;
            }
            else
            {
                m_playerBase = null;
                LogProblem("open-player-base", "打开容器后取不到本机玩家背包（" + playerReason +
                    "）→ 仍显示权威内容，但槽位编辑会因缺少守恒基线被拒绝（不猜背包）");
            }
            Log("客户端打开容器面板 " + state.Describe() + "（" +
                ContainerKinds.Name(state.ContainerKind) + "）· 主机权威库存");
            // 熔炉：主机在 BlockUseResult 之前发了 FurnaceState；这里把它写进刚建好的副本组件，
            // 面板第一帧就是正确的火焰 / 进度（客户端不跑烧炼）。
            if (state.ContainerKind == ContainerKinds.Furnace)
                ApplyPendingFurnaceState(state.Key);
        }

        // ================================================================= 主机：容器状态

        private bool SendContainerStateTo(int clientId, ContainerStateData state)
        {
            if (m_transport == null || state == null) return false;
            byte[] payload = BlockUseCodec.EncodeContainerStateBytes(state);
            if (!m_transport.TrySendTo(clientId, MessageType.ContainerState, payload))
            {
                m_containerStateSendFailures++;
                LogProblem("state-send:" + clientId, "ContainerState 发送失败（clientId=" +
                    clientId + " " + state.Describe() + "）");
                return false;
            }
            m_containerStatesSent++;
            return true;
        }

        private void BroadcastContainerState(ContainerKey key, ContainerStateData state,
            int exceptClientId)
        {
            if (m_transport == null || state == null) return;
            byte[] payload = BlockUseCodec.EncodeContainerStateBytes(state);
            foreach (KeyValuePair<int, ContainerKey> item in m_openContainers)
            {
                if (item.Key == exceptClientId || !item.Value.Equals(key)) continue;
                if (!m_transport.TrySendTo(item.Key, MessageType.ContainerState, payload))
                {
                    m_containerStateSendFailures++;
                    LogProblem("state-broadcast:" + item.Key, "ContainerState 广播失败（clientId=" +
                        item.Key + " " + state.Describe() + "）");
                    continue;
                }
                m_containerStatesSent++;
                m_containerBroadcastPeers++;
            }
        }

        private void PollHostContainers()
        {
            if (m_openContainers.Count == 0) return;
            m_pollKeys.Clear();
            foreach (KeyValuePair<int, ContainerKey> item in m_openContainers)
            {
                bool known = false;
                for (int i = 0; i < m_pollKeys.Count; i++)
                {
                    if (m_pollKeys[i].Equals(item.Value)) { known = true; break; }
                }
                if (!known) m_pollKeys.Add(item.Value);
            }
            m_pollStates.Clear();
            int changed = m_port.PollContainerStates(m_pollKeys, m_pollStates);
            if (changed <= 0 || m_pollStates.Count == 0) return;
            for (int i = 0; i < m_pollStates.Count; i++)
            {
                ContainerStateData state = m_pollStates[i];
                BroadcastContainerState(state.Key, state, -1);
            }
        }

        // ================================================================= 主机：熔炉炉况

        /// <summary>
        /// 主机轮询熔炉：目标 = 被客户端打开的熔炉（记录里的打开者必收）+ 任一连机角色
        /// 32 格内的燃烧熔炉（关闭但可见也有正确外观）。一拍最多 <see cref="MaxFurnaceTargets"/>
        /// 个；数值没变不发；被打开时由 <see cref="SendFurnaceStateForOpen"/> 无条件发一次。
        /// </summary>
        private void PollHostFurnaces(long nowMs)
        {
            m_openFurnaceKeys.Clear();
            foreach (KeyValuePair<int, ContainerKey> item in m_openContainers)
            {
                if (item.Value.Kind != ContainerKinds.Furnace) continue;
                bool known = false;
                for (int i = 0; i < m_openFurnaceKeys.Count; i++)
                {
                    if (m_openFurnaceKeys[i].Equals(item.Value)) { known = true; break; }
                }
                if (!known) m_openFurnaceKeys.Add(item.Value);
            }
            m_furnaceTargets.Clear();
            for (int i = 0; i < m_openFurnaceKeys.Count && i < MaxFurnaceTargets; i++)
                m_furnaceTargets.Add(new FurnacePollTarget(m_openFurnaceKeys[i]));
            foreach (KeyValuePair<int, ContainerKey> item in m_openContainers)
            {
                if (item.Value.Kind != ContainerKinds.Furnace) continue;
                FurnacePollTarget target = FurnacePollTarget.Find(m_furnaceTargets, item.Value);
                target?.AddClient(item.Key);
            }
            int openedTargets = m_furnaceTargets.Count;
            int skippedDistance = m_port.AddNearbyFurnaceTargets(m_furnaceTargets,
                MaxFurnaceTargets);
            int nearbyTargets = m_furnaceTargets.Count - openedTargets;
            if (nearbyTargets > 0) m_furnaceNearbyTargets += nearbyTargets;
            if (skippedDistance > 0)
            {
                m_furnaceStateSkippedDistance += skippedDistance;
                LogProblem("furnace-skip-distance",
                    "主机本拍有 " + skippedDistance + " 个燃烧熔炉不在任何联机角色附近且没有客户端打开" +
                    " → 不下发炉况（外观 / 火焰只在本端；累计跳过 " + m_furnaceStateSkippedDistance +
                    " 个·拍）");
            }
            if (m_furnaceTargets.Count == 0) return;
            m_furnaceTargetsPolled += m_furnaceTargets.Count;
            for (int i = 0; i < m_furnaceTargets.Count; i++)
            {
                FurnacePollTarget target = m_furnaceTargets[i];
                if (!m_port.TryCaptureFurnaceState(target.Key, out FurnaceStateData state,
                        out string reason))
                {
                    m_furnaceStateSkippedUnloaded++;
                    // 方块实体 / 区块没了：把上次快照丢掉，等它回来时按"新状态"重新下发。
                    m_lastFurnaceStates.Remove(target.Key);
                    LogProblem("furnace-capture:" + reason + ":" + target.Key.Describe(),
                        "主机抓不到熔炉炉况 " + target.Key.Describe() + "（" + reason +
                        "）→ 本拍不下发（累计 " + m_furnaceStateSkippedUnloaded +
                        " 次；区块恢复后会重新下发）");
                    continue;
                }
                if (m_lastFurnaceStates.TryGetValue(target.Key, out FurnaceStateData last) &&
                    last.SameValues(state))
                {
                    m_furnaceStatesSuppressed++;
                    continue;
                }
                m_lastFurnaceStates[target.Key] = state;
                for (int c = 0; c < target.ClientIds.Count; c++)
                {
                    SendFurnaceStateTo(target.ClientIds[c], state);
                }
            }
            if (m_lastFurnaceStates.Count > MaxTrackedFurnaceStates)
            {
                // 只是"变化才发"的缓存：清空只会导致下一拍多一次幂等重发，不会丢消息。
                int dropped = m_lastFurnaceStates.Count;
                m_lastFurnaceStates.Clear();
                Log("主机熔炉炉况缓存超过 " + MaxTrackedFurnaceStates + " 项（" + dropped +
                    "）→ 清空（下一拍按当前状态重发一次）");
            }
        }

        /// <summary>主机：打开熔炉时无条件把当前权威炉况发给该客户端（发送失败只记录，不影响打开）。</summary>
        private void SendFurnaceStateForOpen(int clientId, ContainerKey key)
        {
            if (!m_port.TryCaptureFurnaceState(key, out FurnaceStateData state,
                    out string reason))
            {
                m_furnaceOpenStateMissing++;
                LogProblem("furnace-open-capture:" + reason + ":" + key.Describe(),
                    "主机打开熔炉 " + key.Describe() + " 但抓不到炉况（" + reason +
                    "）→ 客户端面板只剩槽位，火焰/进度保持默认值（明确失败，等轮询恢复）；累计 " +
                    m_furnaceOpenStateMissing + " 次");
                return;
            }
            m_lastFurnaceStates[key] = state;
            SendFurnaceStateTo(clientId, state);
        }

        /// <summary>主机 -> 指定客户端：一条权威炉况（失败明确计数 + 限流日志）。</summary>
        private bool SendFurnaceStateTo(int clientId, FurnaceStateData state)
        {
            if (m_transport == null || state == null) return false;
            byte[] payload = BlockUseCodec.EncodeFurnaceStateBytes(state);
            if (!m_transport.TrySendTo(clientId, MessageType.FurnaceState, payload))
            {
                m_furnaceStateSendFailures++;
                LogProblem("furnace-send:" + clientId,
                    "FurnaceState 发送失败（clientId=" + clientId + " " + state.Describe() +
                    "）→ 客户端火焰/进度会停在旧值，下一次数值变化或重开面板会补发；累计失败 " +
                    m_furnaceStateSendFailures);
                return false;
            }
            m_furnaceStatesSent++;
            return true;
        }

        // ================================================================= 客户端：权威状态

        private void OnContainerState(MessageContext context, P.ContainerState message)
        {
            m_containerStatesReceived++;
            if (IsHosting)
            {
                m_useDirectionErrors++;
                LogProblem("state-on-host", "主机收到了 ContainerState（方向错误）→ 丢弃");
                return;
            }
            if (!BlockUseCodec.TryConvertContainerState(message, out ContainerStateData state,
                    out string error))
            {
                m_containerStatesMalformed++;
                LogProblem("state-malformed:" + error,
                    "ContainerState 载荷非法（" + error + "）→ 丢弃");
                return;
            }
            if (m_openKey == null)
            {
                // 还没打开面板：保留给紧随其后的 BlockUseResult（主机先发状态、再发结果）。
                m_pendingContainerState = state;
                m_pendingContainerStateMs = (long)(m_clock() * 1000.0);
                m_containerStatesWithoutOpen++;
                return;
            }
            if (!m_openKey.Value.Equals(state.Key))
            {
                m_containerStatesWithoutOpen++;
                LogProblem("state-key", "收到非当前打开容器的 ContainerState " + state.Describe() +
                    "（当前 " + m_openKey.Value.Describe() + "）→ 丢弃");
                return;
            }
            if (state.Revision < m_openRevision)
            {
                m_containerStatesStale++;
                LogProblem("state-stale", "丢弃过期 ContainerState rev=" + state.Revision +
                    " < 已应用 rev=" + m_openRevision + "（" + state.Describe() + "）");
                return;
            }
            if (m_pendingEditActive && state.Revision <= m_pendingEditBaseRevision)
            {
                // 在途编辑 = 本地待确认的叠加层：revision 没有前进的权威状态（含主机拒绝时补发的
                // "当前状态"）不覆盖玩家正在看的面板；回滚由 Reject/Timeout 用 m_openValues 完成。
                m_containerStatesDeferred++;
                return;
            }
            ApplyAuthoritativeState(state);
        }

        private void ApplyAuthoritativeState(ContainerStateData state)
        {
            m_port.ApplyContainerStateView(state);
            m_openRevision = state.Revision;
            m_openValues = (int[])state.Values.Clone();
            m_openCounts = (int[])state.Counts.Clone();
            m_containerStatesApplied++;
            // 权威状态前进了：之前被拒绝的那条编辑不再是"当前面板"，允许重新提交。
            m_lastRejectedValid = false;
            // 注意：**不要**在这里清掉在途编辑 —— 拒绝回执随后到达时还要用它的背包基线做回滚。
            // 只有"接受回执"或超时才结束一条在途编辑（见 OnContainerEditResult / Expire）。
            if (m_port.TryCapturePlayerInventory(out PlayerInventorySnapshot player,
                    out string reason))
            {
                m_playerBase = player;
            }
            else
            {
                m_playerBase = null;
                LogProblem("apply-player-base", "应用容器权威状态后取不到本机玩家背包（" +
                    reason + "）→ 下一次槽位编辑会被拒绝（不猜背包）");
            }
        }

        // ================================================================= 客户端：熔炉炉况

        /// <summary>
        /// 客户端收到主机权威炉况：校验 -> 过期判定 -> 写副本组件（热量/进度/余火）+ 落地
        /// 燃烧外观格子值。客户端绝不推进烧炼、不消耗燃料、不写权威库存。
        /// </summary>
        private void OnFurnaceState(MessageContext context, P.FurnaceState message)
        {
            m_furnaceStatesReceived++;
            if (IsHosting)
            {
                m_furnaceStateDirectionErrors++;
                m_furnaceStatesRejected++;
                LogProblem("furnace-state-on-host",
                    "主机收到了 FurnaceState（方向错误）→ 丢弃（客户端不能向主机发炉况）");
                return;
            }
            if (!BlockUseCodec.TryConvertFurnaceState(message, out FurnaceStateData state,
                    out string error))
            {
                m_furnaceStatesMalformed++;
                m_furnaceStatesRejected++;
                LogProblem("furnace-state-malformed:" + error,
                    "FurnaceState 载荷非法（" + error + " @" + message.X + "," + message.Y + "," +
                    message.Z + "）→ 整条拒绝");
                return;
            }
            ContainerKey key = state.Key;
            if (m_furnaceAppliedRevisions.TryGetValue(key, out int appliedRevision) &&
                state.Revision < appliedRevision)
            {
                m_furnaceStatesStale++;
                m_furnaceStatesRejected++;
                LogProblem("furnace-state-stale:" + key.Describe(),
                    "丢弃过期 FurnaceState rev=" + state.Revision + " < 已应用 rev=" +
                    appliedRevision + "（" + state.Describe() + "）");
                return;
            }
            bool applied = m_port.ApplyFurnacePresentation(state, out bool litApplied,
                out bool progressApplied, out string reason);
            if (!applied)
            {
                m_furnaceStatesRejected++;
                LogProblem("furnace-state-apply:" + key.Describe(),
                    "应用 FurnaceState 失败（" + reason + "）→ " + state.Describe() +
                    "；不推进 revision，等主机下一次状态");
                return;
            }
            m_furnaceStatesApplied++;
            if (litApplied) m_furnaceLitApplied++;
            if (progressApplied) m_furnaceProgressApplied++;
            RememberFurnaceState(key, state, (long)(m_clock() * 1000.0));
            m_furnaceAppliedRevisions[key] = state.Revision;
            if (!litApplied)
            {
                LogProblem("furnace-lit-partial:" + key.Describe(),
                    "FurnaceState 已应用但燃烧外观格子没落地（" + reason + "）→ " +
                    state.Describe());
            }
            else if (!progressApplied)
            {
                // 面板还没打开 → 还没有副本组件。炉况会暂存，打开面板时重放；不是静默丢弃。
                LogThrottledInfo("furnace-progress-pending:" + key.Describe(),
                    "客户端收到炉况但还没有熔炉副本组件（面板未打开）→ 热量/进度暂存，打开面板时重放；" +
                    "燃烧外观已落地（" + state.Describe() + "）");
            }
        }

        /// <summary>客户端：记住最近炉况（打开面板时重放）；缓存有界，超出时清空并写明。</summary>
        private void RememberFurnaceState(ContainerKey key, FurnaceStateData state, long nowMs)
        {
            if (!m_furnacePending.ContainsKey(key) &&
                m_furnacePending.Count >= MaxTrackedFurnaceStates)
            {
                int dropped = m_furnacePending.Count;
                m_furnacePending.Clear();
                Log("客户端熔炉待打开炉况缓存超过 " + MaxTrackedFurnaceStates + " 项（" +
                    dropped + "）→ 清空（之后再收到状态会重新缓存，不会把旧值当当前值）");
            }
            if (!m_furnaceAppliedRevisions.ContainsKey(key) &&
                m_furnaceAppliedRevisions.Count >= MaxTrackedFurnaceStates)
            {
                int dropped = m_furnaceAppliedRevisions.Count;
                m_furnaceAppliedRevisions.Clear();
                Log("客户端熔炉 revision 表超过 " + MaxTrackedFurnaceStates + " 项（" + dropped +
                    "）→ 清空（之后的同 revision 状态按幂等重放，不丢内容）");
            }
            m_furnacePending[key] = new PendingFurnaceState(state, nowMs);
        }

        /// <summary>
        /// 客户端：面板刚打开时重放"打开前收到的炉况"。主机在 BlockUseResult 之前发炉况，
        /// 所以正常路径一定命中；没有 / 过期 → 明确计数 + 日志（不拿默认值冒充主机状态）。
        /// </summary>
        private void ApplyPendingFurnaceState(ContainerKey key)
        {
            if (!m_furnacePending.TryGetValue(key, out PendingFurnaceState pending))
            {
                m_furnaceOpenStateMissing++;
                LogProblem("open-furnace-state-missing:" + key.Describe(),
                    "打开熔炉 " + key.Describe() +
                    " 前没有收到对应的 FurnaceState → 面板热量/进度/火焰保持默认值（明确失败；" +
                    "主机应在 BlockUseResult 之前下发）；累计 " + m_furnaceOpenStateMissing);
                return;
            }
            m_furnacePending.Remove(key);
            long nowMs = (long)(m_clock() * 1000.0);
            long age = nowMs - pending.ReceivedMs;
            if (age > FurnaceStateMaxAgeMs)
            {
                m_furnaceOpenStateMissing++;
                LogProblem("open-furnace-state-stale:" + key.Describe(),
                    "打开熔炉 " + key.Describe() + " 时缓存的 FurnaceState 已过期（" + age +
                    "ms > " + FurnaceStateMaxAgeMs + "ms）→ 不用它冒充当前值（明确失败）；累计 " +
                    m_furnaceOpenStateMissing);
                return;
            }
            bool applied = m_port.ApplyFurnacePresentation(pending.State, out bool litApplied,
                out bool progressApplied, out string reason);
            if (litApplied) m_furnaceLitApplied++;
            if (progressApplied) m_furnaceProgressApplied++;
            if (!applied)
            {
                m_furnaceStatesRejected++;
                LogProblem("open-furnace-apply:" + key.Describe(),
                    "打开熔炉后应用缓存炉况失败（" + reason + "）→ " + pending.State.Describe());
                return;
            }
            m_furnaceAppliedRevisions[key] = pending.State.Revision;
            Log("客户端打开熔炉时应用权威炉况 " + pending.State.Describe() +
                (age > 0 ? "（" + age + "ms 前收到）" : "（随打开一起收到）"));
        }

        // ================================================================= 客户端：容器编辑

        private void PumpClientContainer(long nowMs)
        {
            if (m_openKey == null) return;
            if (!m_port.IsContainerViewOpen)
            {
                ContainerKey closed = m_openKey.Value;
                m_openKey = null;
                m_playerBase = null;
                m_openValues = null;
                m_openCounts = null;
                m_openRevision = 0;
                m_pendingEditActive = false;
                m_pendingEditPlayerBase = null;
                m_pendingContainerState = null;
                m_lastRejectedValid = false;
                m_containerViewsClosed++;
                if (closed.Kind == ContainerKinds.Furnace)
                {
                    // 关闭后重开：主机打开时会无条件重发当前炉况；清掉本地 revision / 暂存，
                    // 让重发的那条按"新的权威状态"应用（不会被同 revision 的旧判定挡住）。
                    m_furnaceAppliedRevisions.Remove(closed);
                    m_furnacePending.Remove(closed);
                }
                SendCloseRequest(closed);
                return;
            }
            if (m_pendingEditActive)
            {
                if (nowMs - m_pendingEditSentMs >= EditTimeoutMs)
                {
                    m_editTimeouts++;
                    if (m_openRevision > m_pendingEditBaseRevision)
                    {
                        // 权威状态已经前进（大概率是我们这条编辑被接受、只是回执丢了）：
                        // 此时回滚背包会把已进容器的物品又退回去 → 只结束在途编辑，不回滚。
                        m_pendingEditActive = false;
                        m_pendingEditPlayerBase = null;
                        LogProblem("edit-timeout-applied", "容器编辑回执超时 seq=" +
                            m_pendingEditSequence + "，但权威 revision " +
                            m_pendingEditBaseRevision + " → " + m_openRevision +
                            " 已前进 → 按已应用处理（不回滚背包，避免物品重复）");
                    }
                    else
                    {
                        RollbackPendingEdit("timeout");
                    }
                }
                return;
            }
            if (!m_port.TryCaptureOpenContainer(out int[] values, out int[] counts,
                    out string captureReason))
            {
                LogProblem("capture-container:" + captureReason,
                    "取不到本地副本面板槽位（" + captureReason + "）→ 本拍不提交编辑");
                return;
            }
            if (ContainerStateData.SameArrays(values, m_openValues) &&
                ContainerStateData.SameArrays(counts, m_openCounts))
            {
                // 容器没有变化：把玩家背包基线刷新到当前值。这样"先捡到物品、再放进容器"
                // 不会因为基线过期被守恒校验误拒；同时"不减少背包却往容器里加物品"仍会被拒。
                if (m_port.TryCapturePlayerInventory(out PlayerInventorySnapshot idlePlayer,
                        out _))
                {
                    m_playerBase = idlePlayer;
                }
                return;
            }
            if (!m_port.TryCapturePlayerInventory(out PlayerInventorySnapshot playerDesired,
                    out string playerReason))
            {
                LogProblem("capture-player:" + playerReason,
                    "取不到本机玩家背包（" + playerReason + "）→ 本拍不提交容器编辑");
                return;
            }
            if (m_playerBase == null)
            {
                // 没有守恒基线：不允许提交（否则无法证明容器没有凭空多出物品）。
                LogProblem("no-player-base", "没有玩家背包守恒基线 → 拒绝提交容器编辑（先打开一次容器让主机下发权威状态）");
                return;
            }
            long fingerprint = Fingerprint(values, counts);
            if (m_lastRejectedValid && m_lastRejectedKey.Equals(m_openKey.Value) &&
                m_lastRejectedBaseRevision == m_openRevision &&
                m_lastRejectedFingerprint == fingerprint)
            {
                // 同一条被主机拒绝的编辑（base 与内容都没变）不重发：否则会形成
                // "提交 → 拒绝 → 回滚 → 同一状态又提交"的死循环。玩家改动面板后指纹变化，自动恢复。
                m_editSuppressed++;
                LogProblem("edit-suppressed:" + m_openKey.Value.Describe(),
                    "容器编辑与刚被主机拒绝的那条完全相同 @" + m_openKey.Value.Describe() +
                    "（base rev=" + m_openRevision + "）→ 抑制重发（防回滚死循环；改动面板后自动重试）");
                return;
            }
            if (m_lastRejectedValid && m_lastRejectedFingerprint != fingerprint)
                m_lastRejectedValid = false;
            long sequence = NextEditSequence();
            var request = new ContainerEditRequestData(sequence, m_openKey.Value.X,
                m_openKey.Value.Y, m_openKey.Value.Z, m_openKey.Value.Kind, m_openRevision,
                m_openValues, m_openCounts, values, counts, m_playerBase.Values,
                m_playerBase.Counts, playerDesired.Values, playerDesired.Counts);
            byte[] payload = BlockUseCodec.EncodeContainerEditBytes(in request);
            m_editSubmitted++;
            if (!m_transport.TrySend(MessageType.ContainerEditRequest, payload))
            {
                m_editSendFailures++;
                LogProblem("edit-send", "容器编辑发送失败（" + request.Describe() +
                    "）→ 下一拍重试（本地副本保持乐观显示，主机仍是权威）");
                return;
            }
            m_editSent++;
            m_pendingEditActive = true;
            m_pendingEditSequence = sequence;
            m_pendingEditSentMs = nowMs;
            m_pendingEditKey = m_openKey.Value;
            m_pendingEditBaseRevision = m_openRevision;
            m_pendingEditBaseValues = m_openValues;
            m_pendingEditBaseCounts = m_openCounts;
            m_pendingEditDesiredValues = values;
            m_pendingEditDesiredCounts = counts;
            m_pendingEditPlayerBase = m_playerBase;
        }

        private void RollbackPendingEdit(string reason)
        {
            if (!m_pendingEditActive) return;
            ContainerKey key = m_pendingEditKey;
            PlayerInventorySnapshot playerBase = m_pendingEditPlayerBase;
            m_pendingEditActive = false;
            m_pendingEditPlayerBase = null;
            m_pendingEditBaseValues = null;
            m_pendingEditBaseCounts = null;
            m_pendingEditDesiredValues = null;
            m_pendingEditDesiredCounts = null;
            if (m_openValues != null && m_openCounts != null && m_openKey != null &&
                m_openKey.Value.Equals(key))
            {
                var authoritative = new ContainerStateData(key.X, key.Y, key.Z, key.Kind,
                    m_openRevision, m_openValues, m_openCounts);
                m_port.ApplyContainerStateView(authoritative);
            }
            if (playerBase != null)
                m_port.RestorePlayerInventory(playerBase, reason);
            m_editReverts++;
            m_lastRevertReason = ContainerEditPolicy.ReasonCode(reason);
            LogProblem("edit-rollback:" + reason, "容器编辑未获主机确认（" + reason +
                "）→ 已回滚到主机权威库存" + (playerBase == null ? "（玩家背包基线缺失，未回滚背包）" : "并把玩家背包恢复到编辑前快照") +
                " @ " + key.Describe() + " · 累计回滚 " + m_editReverts);
        }

        /// <summary>面板槽位指纹（FNV-1a）：只用于判定"是不是刚被拒绝的那条编辑"。</summary>
        private static long Fingerprint(int[] values, int[] counts)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                int length = Math.Min(values?.Length ?? 0, counts?.Length ?? 0);
                for (int i = 0; i < length; i++)
                {
                    hash = (hash ^ values[i]) * 1099511628211L;
                    hash = (hash ^ counts[i]) * 1099511628211L;
                }
                return hash;
            }
        }

        // ================================================================= 主机：容器编辑

        private void OnContainerEditRequest(MessageContext context, P.ContainerEditRequest message)
        {
            m_editReceived++;
            if (!IsHosting)
            {
                m_editDirectionErrors++;
                LogProblem("edit-on-client", "客户端收到了 ContainerEditRequest（方向错误）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0)
            {
                m_editRejected++;
                LogProblem("edit-source", "ContainerEditRequest 来源非法（clientId=" + clientId +
                    "）→ 拒绝");
                return;
            }
            if (!BlockUseCodec.TryConvertContainerEdit(message,
                    out ContainerEditRequestData request, out string error))
            {
                m_editRejected++;
                NoteEditRejection("malformed:" + error);
                LogProblem("edit-malformed:" + error, "ContainerEditRequest 载荷非法（" + error +
                    "）→ 拒绝");
                SendEditResult(clientId, new ContainerEditResultData(0, false,
                    "malformed:" + error, 0, (int)message.ContainerKind));
                return;
            }
            if (!m_openContainers.TryGetValue(clientId, out ContainerKey openKey) ||
                !openKey.Equals(request.Key))
            {
                m_editRejected++;
                const string notOpen = "container-not-open: 编辑者不是该容器的打开者（或已关闭）";
                NoteEditRejection(notOpen);
                LogProblem("edit-not-open", "clientId=" + clientId + " 的容器编辑没有对应的打开记录（" +
                    request.Describe() + "）→ 拒绝");
                SendEditResult(clientId, new ContainerEditResultData(request.Sequence, false,
                    notOpen, 0, request.ContainerKind));
                return;
            }
            // 容器编辑同样可能触发方块行为的声音（例如分配器 / 家具）；用同一套捕获窗口，
            // 与方块使用走同一条中继（不写第二套逻辑）。finally 保证窗口关闭。
            var editAnchor = new Vector3(request.Key.X, request.Key.Y, request.Key.Z);
            m_remoteAudio?.BeginRemoteAction(clientId, editAnchor, "container-edit");
            bool editAccepted;
            ContainerEditOutcome outcome;
            string reason;
            try
            {
                editAccepted = m_port.TryApplyContainerEdit(clientId, in request, out outcome,
                    out reason);
            }
            finally
            {
                m_remoteAudio?.EndRemoteAction(clientId, "container-edit");
            }
            if (!editAccepted)
            {
                m_editRejected++;
                NoteEditRejection(reason);
                LogProblem("edit-reject:" + ContainerEditPolicy.ReasonCode(reason),
                    "主机拒绝 clientId=" + clientId + " 的容器编辑 " + request.Describe() + "（" +
                    reason + "）→ 下发权威状态供客户端回滚");
                if (m_port.TryCaptureContainerState(request.Key, out ContainerStateData current,
                        out string captureReason))
                {
                    SendContainerStateTo(clientId, current);
                    SendEditResult(clientId, new ContainerEditResultData(request.Sequence,
                        false, reason, current.Revision, current.ContainerKind));
                }
                else
                {
                    SendEditResult(clientId, new ContainerEditResultData(request.Sequence,
                        false, reason + ";" + captureReason, 0, request.ContainerKind));
                }
                return;
            }
            if (outcome?.State == null)
            {
                // 端口契约：接受时必须给出权威状态；给不出就明确拒绝（不拿半条结果打开/广播）。
                const string noState = "outcome-missing: 主机端口接受了编辑却没有返回权威状态";
                m_editRejected++;
                NoteEditRejection(noState);
                LogProblem("edit-outcome-missing", "主机接受了 clientId=" + clientId +
                    " 的容器编辑但没有返回权威状态 → 按拒绝回执（客户端回滚）");
                SendEditResult(clientId, new ContainerEditResultData(request.Sequence, false,
                    noState, 0, request.ContainerKind));
                return;
            }
            ContainerStateData state = outcome.State;
            m_editApplied++;
            if (outcome.CreativeInventory)
            {
                // 创造请求者：守恒被创造模式规则替代（引擎侧计数同样会进 StatusLine 的 port 段）。
                m_creativeInventoryDetected++;
                m_creativeTransferAllowed++;
                m_creativeDataVariantAllowed += outcome.CreativeDataVariantAllowed;
                m_creativeInventoryMoveAllowed += outcome.CreativeInventoryMoveAllowed;
            }
            if (outcome.CraftGridApplied) m_craftGridApplied++;
            if (outcome.ResultTakenCount > 0)
            {
                m_resultTaken++;
                m_resultTakenItems += outcome.ResultTakenCount;
            }
            if (outcome.ResultPredictionMismatch) m_craftPredictionMismatch++;
            if (outcome.SplitApplied) m_splitApplied++;
            if (outcome.MoveApplied) m_moveApplied++;
            if (outcome.TakeAndPlaceApplied) m_takeAndPlaceApplied++;
            m_playerLedgerRemoved += outcome.PlayerRemoved;
            m_playerLedgerAdded += outcome.PlayerAdded;
            m_playerLedgerLeftover += outcome.PlayerLeftover;
            // 权威状态先发给发起者（回执需要 revision），再广播给同容器的其他打开者。
            SendContainerStateTo(clientId, state);
            BroadcastContainerState(state.Key, state, clientId);
            SendEditResult(clientId, new ContainerEditResultData(request.Sequence, true,
                string.Empty, state.Revision, state.ContainerKind));
        }

        private void NoteEditRejection(string reason)
        {
            string code = ContainerEditPolicy.ReasonCode(reason);
            m_editRejectedByReason.TryGetValue(code, out long count);
            m_editRejectedByReason[code] = count + 1;
            // creative-* 拒绝原因只可能来自"请求者是创造仓库"的判定 → 也算一次创造仓库检测。
            // （确认性计数：接受路径由 outcome.CreativeInventory 计数，见 OnContainerEditRequest）
            if (code.StartsWith("creative-", StringComparison.Ordinal))
                m_creativeInventoryDetected++;
        }

        private void SendEditResult(int clientId, in ContainerEditResultData result)
        {
            if (m_transport == null || result.Sequence == 0) return;
            byte[] payload = BlockUseCodec.EncodeContainerEditResultBytes(in result);
            if (!m_transport.TrySendTo(clientId, MessageType.ContainerEditResult, payload))
            {
                LogProblem("edit-result-send", "ContainerEditResult 发送失败（clientId=" + clientId +
                    " seq=" + result.Sequence + " accepted=" + result.Accepted +
                    "）→ 客户端会按编辑超时回滚");
            }
        }

        private void OnContainerEditResult(MessageContext context, P.ContainerEditResult message)
        {
            m_editResultsReceived++;
            if (IsHosting)
            {
                m_editDirectionErrors++;
                LogProblem("edit-result-on-host", "主机收到了 ContainerEditResult（方向错误）→ 丢弃");
                return;
            }
            if (!BlockUseCodec.TryConvertContainerEditResult(message,
                    out ContainerEditResultData result, out string error))
            {
                m_editResultsMalformed++;
                LogProblem("edit-result-malformed:" + error,
                    "ContainerEditResult 载荷非法（" + error + "）→ 丢弃");
                return;
            }
            if (!m_pendingEditActive || result.Sequence != m_pendingEditSequence)
            {
                m_editResultsUnknown++;
                LogProblem("edit-result-unknown", "收到未知序号的容器编辑回执 seq=" +
                    result.Sequence + "（重连 / 重复）→ 不应用");
                return;
            }
            if (!result.Accepted)
            {
                m_editResultsRejected++;
                m_editRejected++;
                NoteEditRejection(result.Reason);
                m_lastRejectedValid = true;
                m_lastRejectedKey = m_pendingEditKey;
                m_lastRejectedBaseRevision = m_pendingEditBaseRevision;
                m_lastRejectedFingerprint = Fingerprint(m_pendingEditDesiredValues,
                    m_pendingEditDesiredCounts);
                LogProblem("edit-denied:" + ContainerEditPolicy.ReasonCode(result.Reason),
                    "容器编辑被主机拒绝 seq=" + result.Sequence + "（" + result.Reason +
                    "）→ 回滚到主机权威库存（同一条编辑不会重发） @" +
                    m_pendingEditKey.Describe());
                RollbackPendingEdit("rejected:" + result.Reason);
                return;
            }
            // 接受：权威 ContainerState 已经（或即将）到达并应用；这里结束在途编辑。
            m_pendingEditActive = false;
            m_pendingEditPlayerBase = null;
            m_pendingEditBaseValues = null;
            m_pendingEditBaseCounts = null;
            m_pendingEditDesiredValues = null;
            m_pendingEditDesiredCounts = null;
            m_lastRejectedValid = false;
        }

        // ================================================================= 客户端：超时 / 会话

        private void ExpirePendingUses(long nowMs)
        {
            if (m_pendingUses.Count == 0) return;
            List<long> expired = null;
            foreach (KeyValuePair<long, long> item in m_pendingUses)
            {
                if (nowMs - item.Value < RequestTimeoutMs) continue;
                (expired ?? (expired = new List<long>(4))).Add(item.Key);
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                m_pendingUses.Remove(expired[i]);
                m_useTimeouts++;
                LogProblem("use-timeout", "方块使用请求超时 seq=" + expired[i] +
                    "（" + RequestTimeoutMs + "ms 没有回执）→ 客户端没有执行任何本地交互");
            }
        }

        private void ResetClientSession(string reason)
        {
            m_pendingUses.Clear();
            if (m_pendingEditActive) RollbackPendingEdit("session-reset:" + reason);
            m_openKey = null;
            m_openRevision = 0;
            m_openValues = null;
            m_openCounts = null;
            m_playerBase = null;
            m_pendingContainerState = null;
            m_nextUseSequence = 0;
            m_nextEditSequence = 0;
            m_lastRejectedValid = false;
            // 重连 = 新连接：熔炉 revision / 暂存全部作废；主机打开时会重新下发当前炉况。
            m_furnaceAppliedRevisions.Clear();
            m_furnacePending.Clear();
            m_port.CloseContainerView(reason);
        }

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!IsHosting) return;
            m_openContainers.Remove(clientId);
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            if (!IsHosting) return;
            if (m_openContainers.Remove(clientId))
            {
                LogProblem("client-left:" + clientId, "clientId=" + clientId + " 离开（" + reason +
                    "）→ 移除它的容器打开记录（库存本身留在主机世界里）");
            }
        }

        private bool IsHosting => m_control?.IsHosting == true;

        // ================================================================= 诊断

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(m_clock() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        /// <summary>
        /// 限流的信息级日志（与 <see cref="LogProblem"/> 共用限流表，10s 一条）：用于"炉况已收到
        /// 但面板还没打开"这类**正常但必须可解释**的情况（计数仍在状态行，不静默）。
        /// </summary>
        private void LogThrottledInfo(string key, string message)
        {
            long nowMs = (long)(m_clock() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < SummaryLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Info(ModuleId, message);
        }

        private string Summary() =>
            "useRequest(sent=" + m_useSent + " accepted=" + m_useAccepted + " rejected=" +
            m_useRejected + " timeout=" + m_useTimeouts + " recv=" + m_useReceived +
            " results=" + m_useResultsReceived + "/" + m_useResultsAccepted + "/" +
            m_useResultsRejected + " unknown=" + m_useResultsUnknown + " malformed=" +
            m_useResultsMalformed + " submit=" + m_useSubmitted + " sendFailed=" +
            m_useSendFailures + " dirErr=" + m_useDirectionErrors + ")" +
            " · containerSync(sent=" + m_containerStatesSent + " recv=" +
            m_containerStatesReceived + " applied=" + m_containerStatesApplied + " rejected=" +
            m_editRejected + " stale=" + m_containerStatesStale + " deferred=" +
            m_containerStatesDeferred + " malformed=" +
            m_containerStatesMalformed + " noOpen=" + m_containerStatesWithoutOpen +
            " broadcastPeers=" + m_containerBroadcastPeers + " sendFailed=" +
            m_containerStateSendFailures + ")" +
            " · furnaceState(furnaceStateSent=" + m_furnaceStatesSent +
            " furnaceStateRecv=" + m_furnaceStatesReceived + " furnaceStateApplied=" +
            m_furnaceStatesApplied + " furnaceStateRejected=" + m_furnaceStatesRejected +
            " furnaceStateStale=" + m_furnaceStatesStale + " furnaceStateMalformed=" +
            m_furnaceStatesMalformed + " furnaceStateSkippedDistance=" +
            m_furnaceStateSkippedDistance + " furnaceStateSkippedUnloaded=" +
            m_furnaceStateSkippedUnloaded + " furnaceLitApplied=" + m_furnaceLitApplied +
            " furnaceProgressApplied=" + m_furnaceProgressApplied +
            " furnaceStateOpenMissing=" + m_furnaceOpenStateMissing +
            " furnaceStateTargets=" + m_furnaceTargetsPolled + " furnaceStateNearby=" +
            m_furnaceNearbyTargets + " furnaceStateSuppressed=" + m_furnaceStatesSuppressed +
            " furnaceStateSendFailed=" + m_furnaceStateSendFailures +
            " furnaceStateDirErr=" + m_furnaceStateDirectionErrors + ")" +
            " · containerEdit(sent=" + m_editSent + " recv=" + m_editReceived +
            " applied=" + m_editApplied + " rejected=" + m_editRejected +
            " rejected[" + RejectedReasons() + "]" +
            " timeout=" + m_editTimeouts + " results=" + m_editResultsReceived + "/" +
            m_editResultsRejected + " unknown=" + m_editResultsUnknown +
            " malformed=" + m_editResultsMalformed + " submit=" + m_editSubmitted +
            " sendFailed=" + m_editSendFailures + " dirErr=" + m_editDirectionErrors +
            " revertCount=" + m_editReverts + " suppressed=" + m_editSuppressed +
            " lastRevert=" + (m_lastRevertReason ?? "none") +
            " craftGridApplied=" + m_craftGridApplied + " resultTaken=" + m_resultTaken + "/" +
            m_resultTakenItems + " predMismatch=" + m_craftPredictionMismatch +
            " splitApplied=" + m_splitApplied + " moveApplied=" + m_moveApplied +
            " takeAndPlaceApplied=" + m_takeAndPlaceApplied +
            " creativeInventoryDetected=" + m_creativeInventoryDetected +
            " creativeTransferAllowed=" + m_creativeTransferAllowed +
            " creativeDataVariantAllowed=" + m_creativeDataVariantAllowed +
            " creativeInventoryMoveAllowed=" + m_creativeInventoryMoveAllowed +
            " playerLedger=" + m_playerLedgerRemoved + "/" + m_playerLedgerAdded +
            " leftover=" + m_playerLedgerLeftover + ")" +
            " · craftingView(opened=" + m_craftingViewOpened + " items=" + m_craftingViewItems +
            " views=" + m_containerViewsOpened + " closed=" + m_containerViewsClosed +
            " openFailed=" + m_containerViewOpenFailures + " stateMissing=" +
            m_containerViewStateMissing + ")";

        /// <summary>按稳定原因 code 汇总拒绝次数（<c>code=count</c>，逗号分隔）；空 = none。</summary>
        private string RejectedReasons()
        {
            if (m_editRejectedByReason.Count == 0) return "none";
            var builder = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, long> item in m_editRejectedByReason)
            {
                if (builder.Length > 0) builder.Append(',');
                builder.Append(item.Key).Append('=').Append(item.Value);
            }
            return builder.ToString();
        }
    }
}

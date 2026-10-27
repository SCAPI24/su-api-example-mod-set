using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 可编辑方块内容同步模块（真值表电路 / 内存条地图；**新文件**）。
    ///
    /// 引擎事实：
    ///   · <c>SubsystemEditableItemBehavior&lt;T&gt;</c> 把方块内容存在私有
    ///     <c>Dictionary&lt;Point3,T&gt; m_blocksData</c> 里（Source: SubsystemEditableItemBehavior.cs:14-39）；
    ///   · <c>OnEditBlock</c> 打开 <c>EditTruthTableDialog</c> / <c>EditMemoryBankDialog</c>，
    ///     对话框 OK 的提交回调在**运行该行为的一侧**调 <c>SetBlockData</c>
    ///     （Source: SubsystemTruthTableCircuitBlockBehavior.cs:38-53 /
    ///      SubsystemMemoryBankBlockBehavior.cs:38-53）；
    ///   · 客户端副本上的这次写入只改本地，主机永远收不到 —— 正是"客户端看到改了、主机还是旧内容"。
    ///
    /// 权威模型：
    ///   · 客户端外壳接管 OnEditBlock：对话框改的是**副本**，提交只发 EDITABLE_EDIT_REQUEST
    ///     （方块格 + 起始内容 + 新内容），**绝不 SetBlockData**；
    ///   · 主机校验（请求者身份 / 距离 / 格仍是同一种方块 / 内容长度与取值域 / 起始内容仍是权威内容）
    ///     后落地到权威 <c>SubsystemEditableItemBehavior</c>，然后广播 EDITABLE_STATE；
    ///   · 客户端把权威内容写进副本（纯表现，不排队电路模拟）；被拒绝 → 主机先发权威内容，
    ///     客户端按它回退，并显示 / 记录明确原因；
    ///   · 主机本机玩家的对话框提交走 <see cref="IEditableBlockPort.TryApplyHostLocalEdit"/>，
    ///     端口触发 <see cref="IEditableBlockPort.HostStateChanged"/>，本模块把这些内容广播给附近客户端。
    ///
    /// 无静态状态；只依赖端口 + Net/EditableNet.cs 的纯逻辑。
    /// </summary>
    public sealed class EditableSyncModule : IMultiplayerModule, ISuMPMessageModule,
        IEditableRequestSink
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;
        private const int RequestTimeoutMs = 5000;
        /// <summary>同一时刻最多几条在途提交（对话框是玩家动作，正常远小于此）。</summary>
        private const int MaxPendingRequests = 8;
        /// <summary>一条广播最多发给多少个附近的其它客户端（超出计入 skipped 并写日志）。</summary>
        private const int MaxRecipients = 64;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IEditableBlockPort m_port;
        private readonly Func<double> m_clock;
        private readonly Dictionary<long, PendingRequest> m_pending =
            new Dictionary<long, PendingRequest>();
        private readonly List<int> m_recipients = new List<int>(8);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_rejectedByReason =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private Action<EditableStateData> m_hostStateHandler;
        private bool m_attached;
        private bool m_wasConnected;
        private int m_lastLocalClientId = int.MinValue;
        private long m_nextSequence;
        private long m_lastSummaryMs;

        // ---------------------------------------------------------------- 计数（请求）
        private long m_reqSent;
        private long m_reqSendFailures;
        private long m_reqLocalRejected;
        private long m_reqReceived;
        private long m_reqAccepted;
        private long m_reqRejected;
        private long m_reqTimeout;
        private long m_reqMalformed;
        private long m_reqDirectionErrors;
        private long m_reqResultsReceived;
        private long m_reqResultsRejected;
        private long m_reqResultsMalformed;
        private long m_reqResultsUnknown;
        private long m_reqResultSendFailures;
        private long m_reqSessionDropped;

        // ---------------------------------------------------------------- 计数（权威内容）
        private long m_stateSent;
        private long m_stateSendFailures;
        private long m_stateReceived;
        private long m_stateApplied;
        private long m_stateStale;
        private long m_stateMalformed;
        private long m_stateApplyFailures;
        private long m_stateDirectionErrors;
        private long m_stateSkippedDistance;
        private long m_stateSkippedBlockChanged;
        private long m_stateBroadcastPeers;
        private long m_stateRecipientCapHits;
        private long m_stateHostLocalIdle;
        private long m_stateHostLocalNoPeers;

        public EditableSyncModule(IEditableBlockPort port, Func<double> clock = null)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_clock = clock ?? (() => Time.RealTime);
            m_descriptor = new ModuleDescriptor("editable-sync",
                "可编辑方块内容同步（真值表 / 内存条；主机权威）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "editable.sync" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "editable: " + Summary() + " · " + m_port.Describe();

        /// <summary>一条在途提交：超时后要能对玩家说明是哪一格、哪一种内容。</summary>
        private readonly struct PendingRequest
        {
            public PendingRequest(int kind, int x, int y, int z, long sentMs)
            {
                Kind = kind;
                X = x;
                Y = y;
                Z = z;
                SentMs = sentMs;
            }

            public int Kind { get; }
            public int X { get; }
            public int Y { get; }
            public int Z { get; }
            public long SentMs { get; }
        }

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 可编辑方块内容同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "可编辑方块内容同步不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到可编辑方块内容消息");
            }
            m_hostStateHandler = HandleHostStateChanged;
            m_port.HostStateChanged += m_hostStateHandler;
            m_port.SetRequestSink(this);
            m_lastLocalClientId = int.MinValue;
            m_attached = true;
            Log("可编辑方块内容同步就绪：客户端 = 对话框提交请求 + 只应用主机内容；" +
                "主机 = 校验 + 落地权威内容 + 广播");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.EditableEditRequest,
                P.EditableEditRequest.Parser,
                new DelegateMessageHandler<P.EditableEditRequest>(OnEditableEditRequest), ModuleId);
            registry.Register((int)MessageType.EditableEditResult,
                P.EditableEditResult.Parser,
                new DelegateMessageHandler<P.EditableEditResult>(OnEditableEditResult), ModuleId);
            registry.Register((int)MessageType.EditableState,
                P.EditableState.Parser,
                new DelegateMessageHandler<P.EditableState>(OnEditableState), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            if (m_hostStateHandler != null)
            {
                m_port.HostStateChanged -= m_hostStateHandler;
                m_hostStateHandler = null;
            }
            m_port.SetRequestSink(null);
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            ResetClientSession("detach:" + reason);
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("可编辑方块内容同步下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            long nowMs = (long)(tick.Now * 1000.0);
            bool connected = m_transport != null && m_transport.IsConnected;
            if (!IsHosting)
            {
                int localClientId = m_transport?.LocalClientId ?? -1;
                if (localClientId != m_lastLocalClientId)
                {
                    if (m_lastLocalClientId != int.MinValue)
                        ResetClientSession("client-id-change " + m_lastLocalClientId + " → " +
                            localClientId);
                    m_lastLocalClientId = localClientId;
                }
                if (connected && !m_wasConnected)
                {
                    // 新连接的序号从 1 重新开始；旧连接的在途请求已经作废（上面按 client-id 处理）。
                    m_nextSequence = 0;
                }
                ExpirePending(nowMs);
            }
            m_wasConnected = connected;
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ================================================================= 客户端：提交

        /// <summary>
        /// 客户端对话框提交入口（Engine/EditableBlockSubsystems.cs 调用）。
        /// 返回 true = 已入队发送；客户端**没有**写过本地副本，内容只等主机广播。
        /// </summary>
        public bool TrySubmitBlockEdit(in EditableEditRequestData request, out string reason)
        {
            reason = null;
            if (!m_attached)
            {
                m_reqLocalRejected++;
                reason = "module-detached";
                return false;
            }
            if (IsHosting)
            {
                m_reqLocalRejected++;
                LogProblem("submit-on-host", "主机收到了本机对话框提交（方向错误）→ 拒绝（主机编辑走 " +
                    "TryApplyHostLocalEdit）");
                reason = "hosting";
                return false;
            }
            if (m_transport == null || !m_transport.IsConnected)
            {
                m_reqLocalRejected++;
                LogProblem("submit-not-connected", "编辑提交失败：通道未连接（" +
                    EditableKinds.Name(request.Kind) + " @" + request.X + "," + request.Y + "," +
                    request.Z + "）→ 客户端不写本地副本（明确失败）");
                reason = "not-connected";
                return false;
            }
            if (!EditableCodec.ValidateCell(request.X, request.Y, request.Z, request.Contents,
                    EditableKinds.BlockContents(request.Kind), out string cellError))
            {
                m_reqLocalRejected++;
                LogProblem("submit-cell:" + cellError, "编辑提交的格子非法（" + cellError +
                    "）→ 不发送（客户端不写本地副本）");
                reason = cellError;
                return false;
            }
            string detail = null;
            if (!EditableCodec.ValidateContent(request.Kind, request.BaseData, "base_data",
                    out string baseError))
            {
                detail = baseError;
            }
            else if (!EditableCodec.ValidateContent(request.Kind, request.Data, "data",
                         out string dataError))
            {
                detail = dataError;
            }
            if (detail != null)
            {
                m_reqLocalRejected++;
                LogProblem("submit-content:" + detail, "编辑提交的内容形状非法（" + detail +
                    "）→ 不发送（客户端不写本地副本）");
                reason = detail;
                return false;
            }
            long sequence = NextSequence();
            EditableEditRequestData sequenced = request.WithSequence(sequence);
            byte[] payload = EditableCodec.EncodeRequestBytes(in sequenced);
            if (!m_transport.TrySend(MessageType.EditableEditRequest, payload))
            {
                m_reqSendFailures++;
                reason = "send-failed";
                LogProblem("req-send", "可编辑内容提交发送失败（" + sequenced.Describe() +
                    "）→ 客户端不写本地副本（明确失败；累计 " + m_reqSendFailures + "）");
                return false;
            }
            m_reqSent++;
            long nowMs = (long)(m_clock() * 1000.0);
            m_pending[sequence] = new PendingRequest(request.Kind, request.X, request.Y,
                request.Z, nowMs);
            Log("客户端提交可编辑内容 " + sequenced.Describe() + "（等主机权威内容）");
            if (m_pending.Count > MaxPendingRequests)
            {
                long oldest = long.MaxValue;
                foreach (KeyValuePair<long, PendingRequest> item in m_pending)
                    if (item.Key < oldest) oldest = item.Key;
                if (oldest != long.MaxValue && m_pending.TryGetValue(oldest,
                        out PendingRequest dropped))
                {
                    m_pending.Remove(oldest);
                    m_reqTimeout++;
                    LogProblem("req-pending-overflow", "在途可编辑内容提交超过 " +
                        MaxPendingRequests + " 条 → 最旧的一条 seq=" + oldest + "（" +
                        EditableKinds.Name(dropped.Kind) + " @" + dropped.X + "," + dropped.Y +
                        "," + dropped.Z + "）按超时处理（不静默丢）");
                    m_port.NotifyEditRejected(dropped.Kind, dropped.X, dropped.Y, dropped.Z,
                        "timeout: 在途请求过多，最旧的一条按超时处理");
                }
            }
            return true;
        }

        private long NextSequence()
        {
            m_nextSequence++;
            if (m_nextSequence <= 0) m_nextSequence = 1;
            return m_nextSequence;
        }

        private void ExpirePending(long nowMs)
        {
            if (m_pending.Count == 0) return;
            List<long> expired = null;
            foreach (KeyValuePair<long, PendingRequest> item in m_pending)
            {
                if (nowMs - item.Value.SentMs < RequestTimeoutMs) continue;
                (expired ?? (expired = new List<long>(2))).Add(item.Key);
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                if (!m_pending.TryGetValue(expired[i], out PendingRequest pending)) continue;
                m_pending.Remove(expired[i]);
                m_reqTimeout++;
                LogProblem("req-timeout", "可编辑内容提交超时 seq=" + expired[i] + "（" +
                    RequestTimeoutMs + "ms 没有回执）@" + pending.X + "," + pending.Y + "," +
                    pending.Z + " · " + EditableKinds.Name(pending.Kind) +
                    " → 客户端没有本地写入（副本保持最后一帧权威内容）");
                m_port.NotifyEditRejected(pending.Kind, pending.X, pending.Y, pending.Z,
                    "timeout: " + RequestTimeoutMs + "ms 没有收到主机裁决");
            }
        }

        private void ResetClientSession(string reason)
        {
            int dropped = m_pending.Count;
            m_pending.Clear();
            m_nextSequence = 0;
            if (dropped > 0)
            {
                m_reqSessionDropped += dropped;
                LogProblem("req-session-drop", "会话更换（" + reason + "）→ 作废在途可编辑内容提交 " +
                    dropped + " 条（旧序号的回执不再匹配；客户端不写本地副本）");
            }
        }

        // ================================================================= 主机：客户端提交

        private void OnEditableEditRequest(MessageContext context, P.EditableEditRequest message)
        {
            m_reqReceived++;
            if (!IsHosting)
            {
                m_reqDirectionErrors++;
                LogProblem("req-on-client", "客户端收到了 EditableEditRequest（方向错误）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || context.Type != MessageType.EditableEditRequest)
            {
                m_reqRejected++;
                NoteRejection("bad-source");
                LogProblem("req-source", "EditableEditRequest 来源非法（clientId=" + clientId +
                    "）→ 拒绝");
                return;
            }
            if (!EditableCodec.TryConvertRequest(message, out EditableEditRequestData request,
                    out string decodeError))
            {
                m_reqMalformed++;
                m_reqRejected++;
                NoteRejection("malformed:" + decodeError);
                LogProblem("req-malformed:" + decodeError, "EditableEditRequest 载荷非法（" +
                    decodeError + "，clientId=" + clientId + "）→ 整条拒绝");
                SendEditResult(clientId, new EditableEditResultData(message.Sequence, false,
                    "malformed:" + decodeError, (int)message.Kind, message.X, message.Y,
                    message.Z, 0));
                return;
            }
            if (!m_port.TryApplyEditableEdit(clientId, in request, out EditableHostOutcome outcome,
                    out string reason))
            {
                m_reqRejected++;
                NoteRejection(reason);
                string code = EditableCodec.ReasonCode(reason);
                LogProblem("req-reject:" + code, "主机拒绝 clientId=" + clientId + " 的可编辑内容提交 " +
                    request.Describe() + "（" + reason + "）→ 下发权威内容供客户端回退");
                int revision = 0;
                if (m_port.TryCaptureEditableState(request.X, request.Y, request.Z, request.Kind,
                        out EditableStateData current, out string captureReason))
                {
                    revision = current.Revision;
                    SendStateTo(clientId, current);
                }
                else
                {
                    LogProblem("req-reject-no-state:" + captureReason,
                        "主机拒绝后可编辑内容无法抓取（" + captureReason + "）@" + request.X + "," +
                        request.Y + "," + request.Z + " → 只发拒绝回执" +
                        (code == "block-changed"
                            ? "（客户端会丢弃该格副本内容）"
                            : "（客户端副本保持最后一帧权威内容）"));
                }
                SendEditResult(clientId, new EditableEditResultData(request.Sequence, false,
                    reason, request.Kind, request.X, request.Y, request.Z, revision));
                return;
            }
            if (outcome?.State == null)
            {
                const string noState = "outcome-missing: 主机端口接受了提交却没有返回权威内容";
                m_reqRejected++;
                NoteRejection(noState);
                LogProblem("req-outcome-missing", "主机接受了 clientId=" + clientId +
                    " 的可编辑内容提交但没有返回权威内容 → 按拒绝回执（客户端不猜测）");
                SendEditResult(clientId, new EditableEditResultData(request.Sequence, false,
                    noState, request.Kind, request.X, request.Y, request.Z, 0));
                return;
            }
            m_reqAccepted++;
            EditableStateData state = outcome.State;
            // 请求者必收（即使距离检查在主机侧刚通过；状态也要回到它的副本）。
            SendStateTo(clientId, state);
            BroadcastStateToNearby(state, clientId);
            SendEditResult(clientId, new EditableEditResultData(request.Sequence, true,
                string.Empty, request.Kind, request.X, request.Y, request.Z, state.Revision));
        }

        private void BroadcastStateToNearby(EditableStateData state, int exceptClientId)
        {
            m_recipients.Clear();
            int skipped = m_port.CollectEditableStateRecipients(state.X, state.Y, state.Z,
                exceptClientId, m_recipients);
            if (skipped > 0)
            {
                m_stateSkippedDistance += skipped;
                LogProblem("state-skip-distance:" + state.X + "," + state.Y + "," + state.Z,
                    "主机有 " + skipped + " 个客户端因距离过远 / 取不到角色位置而收不到可编辑内容 " +
                    EditableKinds.Name(state.Kind) + " @" + state.X + "," + state.Y + "," +
                    state.Z + "（累计跳过 " + m_stateSkippedDistance + " 个·次）");
            }
            int sent = 0;
            for (int i = 0; i < m_recipients.Count; i++)
            {
                int clientId = m_recipients[i];
                if (clientId == exceptClientId) continue;
                if (sent >= MaxRecipients)
                {
                    m_stateRecipientCapHits++;
                    LogProblem("state-recipient-cap", "一次可编辑内容广播的其它客户端超过 " +
                        MaxRecipients + " → 剩余客户端本轮不发（累计 " + m_stateRecipientCapHits +
                        " 次；它们下次提交 / 重开对话框会被主机拒绝并补发权威内容）");
                    break;
                }
                if (SendStateTo(clientId, state))
                {
                    sent++;
                    m_stateBroadcastPeers++;
                }
            }
        }

        private bool SendStateTo(int clientId, EditableStateData state)
        {
            if (m_transport == null || state == null) return false;
            byte[] payload = EditableCodec.EncodeStateBytes(state);
            if (!m_transport.TrySendTo(clientId, MessageType.EditableState, payload))
            {
                m_stateSendFailures++;
                LogProblem("state-send:" + clientId, "EditableState 发送失败（clientId=" +
                    clientId + " " + state.Describe() + "）→ 该客户端副本保持旧内容；" +
                    "累计失败 " + m_stateSendFailures);
                return false;
            }
            m_stateSent++;
            return true;
        }

        private void SendEditResult(int clientId, in EditableEditResultData result)
        {
            if (m_transport == null || result.Sequence == 0) return;
            byte[] payload = EditableCodec.EncodeResultBytes(in result);
            if (!m_transport.TrySendTo(clientId, MessageType.EditableEditResult, payload))
            {
                m_reqResultSendFailures++;
                LogProblem("result-send:" + clientId, "EditableEditResult 发送失败（clientId=" +
                    clientId + " seq=" + result.Sequence + " accepted=" + result.Accepted +
                    "）→ 客户端会按提交超时处理（不猜结果）；累计 " + m_reqResultSendFailures);
            }
        }

        // ================================================================= 主机：本机玩家提交

        /// <summary>
        /// 主机本机玩家在对话框里提交（端口已落到权威内容）→ 广播给该格附近的客户端。
        /// 单机 / 没有客户端时只计数（不是错误；主机自己的对话框已经看到内容）。
        /// </summary>
        private void HandleHostStateChanged(EditableStateData state)
        {
            if (state == null) return;
            if (!IsHosting)
            {
                m_stateHostLocalIdle++;
                return;
            }
            m_recipients.Clear();
            int skipped = m_port.CollectEditableStateRecipients(state.X, state.Y, state.Z, -1,
                m_recipients);
            if (skipped > 0)
            {
                m_stateSkippedDistance += skipped;
                LogProblem("state-host-skip:" + state.X + "," + state.Y + "," + state.Z,
                    "主机本机编辑后有 " + skipped + " 个客户端因距离过远 / 取不到角色位置而收不到内容 " +
                    EditableKinds.Name(state.Kind) + " @" + state.X + "," + state.Y + "," +
                    state.Z + "（累计跳过 " + m_stateSkippedDistance + " 个·次）");
            }
            if (m_recipients.Count == 0)
            {
                m_stateHostLocalNoPeers++;
                return;
            }
            int sent = 0;
            for (int i = 0; i < m_recipients.Count; i++)
            {
                if (sent >= MaxRecipients)
                {
                    m_stateRecipientCapHits++;
                    break;
                }
                if (SendStateTo(m_recipients[i], state)) sent++;
            }
        }

        // ================================================================= 客户端：权威内容 / 裁决

        private void OnEditableState(MessageContext context, P.EditableState message)
        {
            m_stateReceived++;
            if (IsHosting)
            {
                m_stateDirectionErrors++;
                LogProblem("state-on-host", "主机收到了 EditableState（方向错误）→ 丢弃" +
                    "（客户端不能向主机发内容）");
                return;
            }
            if (!EditableCodec.TryConvertState(message, out EditableStateData state,
                    out string error))
            {
                m_stateMalformed++;
                LogProblem("state-malformed:" + error, "EditableState 载荷非法（" + error +
                    " @" + message.X + "," + message.Y + "," + message.Z + "）→ 整条拒绝");
                return;
            }
            EditableStateApplyOutcome outcome = m_port.ApplyEditableState(state, out string reason);
            switch (outcome)
            {
                case EditableStateApplyOutcome.Applied:
                    m_stateApplied++;
                    break;
                case EditableStateApplyOutcome.Stale:
                    m_stateStale++;
                    LogProblem("state-stale:" + state.X + "," + state.Y + "," + state.Z,
                        "丢弃过期可编辑内容 " + state.Describe() + "（" + reason + "）");
                    break;
                case EditableStateApplyOutcome.BlockChanged:
                    m_stateSkippedBlockChanged++;
                    LogProblem("state-block-changed:" + state.X + "," + state.Y + "," + state.Z,
                        "可编辑内容对应的方块已不存在 " + state.Describe() + "（" + reason +
                        "）→ 丢弃该格副本内容");
                    break;
                default:
                    m_stateApplyFailures++;
                    LogProblem("state-apply:" + state.X + "," + state.Y + "," + state.Z,
                        "应用可编辑内容失败 " + state.Describe() + "（" + reason +
                        "）→ 副本保持旧内容（明确失败）");
                    break;
            }
        }

        private void OnEditableEditResult(MessageContext context, P.EditableEditResult message)
        {
            m_reqResultsReceived++;
            if (IsHosting)
            {
                m_reqDirectionErrors++;
                LogProblem("result-on-host", "主机收到了 EditableEditResult（方向错误）→ 丢弃");
                return;
            }
            if (!EditableCodec.TryConvertResult(message, out EditableEditResultData result,
                    out string error))
            {
                m_reqResultsMalformed++;
                LogProblem("result-malformed:" + error, "EditableEditResult 载荷非法（" + error +
                    "）→ 丢弃");
                return;
            }
            if (!m_pending.TryGetValue(result.Sequence, out PendingRequest pending))
            {
                m_reqResultsUnknown++;
                LogProblem("result-unknown", "收到未知序号的编辑回执 seq=" + result.Sequence +
                    "（重连 / 重复 / 主机重启）→ 不应用");
                return;
            }
            m_pending.Remove(result.Sequence);
            if (!result.Accepted)
            {
                m_reqResultsRejected++;
                m_reqRejected++;
                NoteRejection(result.Reason);
                string code = EditableCodec.ReasonCode(result.Reason);
                LogProblem("edit-rejected:" + code, "可编辑内容提交被主机拒绝 seq=" +
                    result.Sequence + "（" + result.Reason + "）@" + result.X + "," + result.Y +
                    "," + result.Z + " · " + EditableKinds.Name(result.Kind) +
                    " → 副本按主机权威内容回退（权威内容随拒绝先到）");
                if (code == "block-changed")
                {
                    m_stateSkippedBlockChanged++;
                    m_port.DropReplicaContent(result.Kind, result.X, result.Y, result.Z,
                        result.Reason);
                }
                m_port.NotifyEditRejected(pending.Kind, pending.X, pending.Y, pending.Z,
                    result.Reason);
                return;
            }
            m_reqAccepted++;
            Log("可编辑内容已被主机接受 seq=" + result.Sequence + " → rev=" + result.Revision +
                " @" + result.X + "," + result.Y + "," + result.Z + " · " +
                EditableKinds.Name(result.Kind));
        }

        // ================================================================= 诊断

        private void NoteRejection(string reason)
        {
            string code = EditableCodec.ReasonCode(reason);
            m_rejectedByReason.TryGetValue(code, out long count);
            m_rejectedByReason[code] = count + 1;
        }

        private bool IsHosting => m_control?.IsHosting == true;

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(m_clock() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        private string Summary() =>
            "editableReqSent=" + m_reqSent +
            " editableReqRecv=" + m_reqReceived +
            " editableReqAccepted=" + m_reqAccepted +
            " editableReqRejected=" + m_reqRejected +
            " editableReqRejected[" + RejectedReasons() + "]" +
            " editableReqTimeout=" + m_reqTimeout +
            " editableReqResults=" + m_reqResultsReceived + "/" + m_reqResultsRejected +
            " unknown=" + m_reqResultsUnknown +
            " malformed=" + m_reqResultsMalformed +
            " editableReqMalformed=" + m_reqMalformed +
            " editableReqLocalRejected=" + m_reqLocalRejected +
            " editableReqSendFailed=" + m_reqSendFailures +
            " editableReqResultSendFailed=" + m_reqResultSendFailures +
            " editableReqSessionDropped=" + m_reqSessionDropped +
            " editableReqDirErr=" + m_reqDirectionErrors +
            " · editableStateSent=" + m_stateSent +
            " editableStateRecv=" + m_stateReceived +
            " editableStateApplied=" + m_stateApplied +
            " editableStateStale=" + m_stateStale +
            " editableStateMalformed=" + m_stateMalformed +
            " editableStateSkippedDistance=" + m_stateSkippedDistance +
            " editableStateSkippedBlockChanged=" + m_stateSkippedBlockChanged +
            " editableStateApplyFailed=" + m_stateApplyFailures +
            " editableStateSendFailed=" + m_stateSendFailures +
            " editableStateDirErr=" + m_stateDirectionErrors +
            " editableStateBroadcastPeers=" + m_stateBroadcastPeers +
            " recipientCapHits=" + m_stateRecipientCapHits +
            " hostLocalIdle=" + m_stateHostLocalIdle +
            " hostLocalNoPeers=" + m_stateHostLocalNoPeers;

        /// <summary>按稳定原因 code 汇总拒绝次数（<c>code=count</c>，逗号分隔）；空 = none。</summary>
        private string RejectedReasons()
        {
            if (m_rejectedByReason.Count == 0) return "none";
            var builder = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, long> item in m_rejectedByReason)
            {
                if (builder.Length > 0) builder.Append(',');
                builder.Append(item.Key).Append('=').Append(item.Value);
            }
            return builder.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol.Remotesound;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 远程动作声音中继（**纯表现**，主机权威）。
    ///
    /// 实机问题：客户端点开关 / 按钮 / 拉杆 / 电路元件时，状态是对的（BlockUseRequest 路径），
    /// 但客户端听不到声音；主机能听到。原因是主机替远端玩家执行交互时，方块行为在**主机本端**
    /// 调 <c>SubsystemAudio</c>（Source: Survivalcraft/Game/SwitchElectricElement.cs:27,
    /// ButtonElectricElement.cs:26, SubsystemDoorBlockBehavior.cs:24,
    /// SubsystemCampfireBlockBehavior.cs:209/213），这段表现不在任何同步流里。
    ///
    /// 本模块与 Engine/RemoteActionAudioReplica.cs 的分工：
    ///   · 主机：BlockUseSyncModule 在执行 TryExecuteUse / TryApplyContainerEdit 前后调用
    ///     <see cref="IRemoteAudioRelayPort"/> 开 / 关捕获窗口（finally 保证关）；
    ///     引擎适配层把窗口内新排进音频队列的声音交给本模块；本模块**每个动作只发一条**
    ///     <c>RemoteActionSound</c>（多条声音合并），修整边界后：
    ///       - 请求者恒定收到；
    ///       - 其他玩家端读得到位置且 ≤ MaxRelayDistance(48 m) 才收到；
    ///       - 窗口外的本机动作不采集（主机自己听得到，绝不回声）。
    ///   · 客户端：严格解码 → 事件 id 去重 / 拒绝重放 → 每秒限速 → 按本机监听者距离裁剪 →
    ///     只调 SubsystemAudio.PlaySound（不写格 / 不生成实体 / 不伤害）。
    ///
    /// 其它客户端本地表现（粒子的 <c>SubsystemParticles</c> 等）**暂不复制**：引擎的粒子 API
    /// 没有统一的"播放列表"可采，且任意复制会偏离主机画面；本模块只做音频，明确记录在案。
    ///
    /// 无静态状态；只依赖端口与 Net/RemoteAudioNet.cs 的纯逻辑。
    /// </summary>
    public sealed class RemoteAudioSyncModule : IMultiplayerModule, ISuMPMessageModule,
        IRemoteAudioRelayPort
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IRemoteAudioPort m_port;
        private readonly RemoteSoundOrderer m_orderer = new RemoteSoundOrderer();
        private readonly RemoteSoundRateLimiter m_sendLimiter =
            new RemoteSoundRateLimiter(RemoteSoundPolicy.MaxEventsPerSecond);
        private readonly RemoteSoundRateLimiter m_recvLimiter =
            new RemoteSoundRateLimiter(RemoteSoundPolicy.MaxReceiveEventsPerSecond);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<RemoteSoundPeer> m_peerScratch = new List<RemoteSoundPeer>(16);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private uint m_nextEventId;
        private long m_lastSummaryMs;

        // 当前捕获窗口（主机；同一时刻最多一个 —— 多窗口嵌套会被明确拒绝，不猜）
        private bool m_captureActive;
        private int m_captureClientId;
        private Vector3 m_captureAnchor;
        private string m_captureAction;
        /// <summary>
        /// 被拒绝的嵌套窗口数。嵌套的 End 先于外层 End 到达（LIFO）时只消耗这个计数，
        /// 绝不关闭外层窗口 —— 否则内层动作一结束就会把外层的捕获数据提前发出去。
        /// </summary>
        private int m_nestedDepth;

        // ---------------------------------------------------------------- 主机计数
        private long m_windows;
        private long m_emptyWindows;
        private long m_captured;
        private long m_relayed;
        private long m_eventsSent;
        private long m_captureFailures;
        private long m_windowRateSkipped;
        private long m_nestedWindowSkipped;
        private long m_windowMismatch;
        private long m_capDropped;
        private long m_sendFailures;
        private long m_noRecipients;
        private long m_skippedDistance;
        private long m_skippedNoPosition;
        private long m_peerCollectionFailures;
        private long m_encodeFailures;
        private long m_hostEchoSkipped;
        private long m_directionErrors;

        // ---------------------------------------------------------------- 客户端计数
        private long m_eventsReceived;
        private long m_malformed;
        private long m_replay;
        private long m_stale;
        private long m_rateSkipped;
        private long m_noListener;
        private long m_eventsPresented;
        private long m_presented;
        private long m_presentFailures;

        public RemoteAudioSyncModule(IRemoteAudioPort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("remote-audio",
                "远程动作声音中继（主机捕获 / 客户端播放）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "effects.remote-audio" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "远程动作声音：" + Summary() + " · " + m_port.Describe();

        public bool IsAvailable => m_attached && m_port != null &&
            m_control != null && m_control.IsHosting;

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 远程动作声音中继不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "远程动作声音中继不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到远程动作声音事件");
            }
            m_lastSummaryMs = 0;
            m_wasConnected = false;
            m_attached = true;
            Log("远程动作声音中继就绪：主机 = 远端动作窗口内捕获 + 每动作一条事件；客户端 = 解码 / " +
                "去重 / 限速 / 距离裁剪后只播放（≤" + RemoteSoundPolicy.MaxSoundsPerEvent +
                " 条/事件，" + RemoteSoundPolicy.MaxRelayDistance.ToString("F0") + " m）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.RemoteActionSound, P.RemoteActionSound.Parser,
                new DelegateMessageHandler<P.RemoteActionSound>(OnRemoteActionSound), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            if (m_captureActive)
            {
                // 下线时关闭窗口（丢弃数据）：绝不留下跨会话的捕获状态。
                m_captureActive = false;
                m_nestedDepth = 0;
                try
                {
                    if (!m_port.EndCapture(out _, out string closeReason))
                        LogProblem("detach-end:" + closeReason,
                            "下线时关闭捕获窗口返回失败（" + closeReason + "）→ 已丢弃本次数据");
                }
                catch (Exception e)
                {
                    LogProblem("detach-end-exception",
                        "下线时关闭捕获窗口抛异常（" + e.GetType().Name + " " + e.Message +
                        "）→ 已丢弃本次数据");
                }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_orderer.Reset();
            m_sendLimiter.Reset();
            m_recvLimiter.Reset();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("远程动作声音中继下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            bool connected = m_transport != null && m_transport.IsConnected;
            if (connected && !m_wasConnected)
            {
                m_orderer.Reset();
                m_sendLimiter.Reset();
                m_recvLimiter.Reset();
                m_nextEventId = 0;
                Log("远程动作声音：连接已建立 → event_id / 去重 / 限速已重置（旧会话的 id 不再复用）");
            }
            m_wasConnected = connected;
            long nowMs = (long)(tick.Now * 1000.0);
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ================================================================= 主机：捕获窗口

        void IRemoteAudioRelayPort.BeginRemoteAction(int requesterClientId, Vector3 anchor,
            string action) => BeginRemoteAction(requesterClientId, anchor, action);

        void IRemoteAudioRelayPort.EndRemoteAction(int requesterClientId, string action) =>
            EndRemoteAction(requesterClientId, action);

        /// <summary>
        /// 主机：远端请求即将执行。任何失败都只影响"这次声音是否中继"，绝不阻断动作本身
        /// （调用方在 finally 里调 <see cref="EndRemoteAction"/>）。
        /// </summary>
        public void BeginRemoteAction(int requesterClientId, Vector3 anchor, string action)
        {
            if (!m_attached) return;
            if (m_control == null || !m_control.IsHosting)
            {
                // 客户端 / 单机不会走远端请求；真走到这里说明接线错了 —— 明确计数，不猜。
                m_directionErrors++;
                LogProblem("begin-not-host", "非主机上调用了远程动作捕获（clientId=" +
                    requesterClientId + " action=" + action + "）→ 不打开窗口");
                return;
            }
            if (requesterClientId <= 0)
            {
                // 主机的本机动作：本来就不采集（主机自己听得到），绝不为它开窗口。
                m_hostEchoSkipped++;
                LogProblem("begin-host-echo", "主机本机动作请求了远程声音捕获（clientId=" +
                    requesterClientId + " action=" + action + "）→ 跳过（不会产生回声）");
                return;
            }
            if (m_captureActive)
            {
                m_nestedWindowSkipped++;
                m_nestedDepth++;
                LogProblem("nested-window", "已有捕获窗口 clientId=" + m_captureClientId +
                    " action=" + m_captureAction + "，又请求 clientId=" + requesterClientId +
                    " action=" + action + " → 拒绝嵌套（外层窗口继续；内层 End 只消耗嵌套计数）");
                return;
            }
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (!m_sendLimiter.TryAccept(nowMs))
            {
                m_windowRateSkipped++;
                LogProblem("window-rate", "远端动作捕获超过限速 " +
                    RemoteSoundPolicy.MaxEventsPerSecond + " 次/秒（clientId=" + requesterClientId +
                    " action=" + action + "）→ 本次不采集（动作照常执行）");
                return;
            }
            if (!m_port.TryBeginCapture(requesterClientId, anchor, out string reason))
            {
                m_captureFailures++;
                LogProblem("capture-begin:" + reason, "打开远程动作声音捕获失败（clientId=" +
                    requesterClientId + " action=" + action + " pos=(" + anchor.X.ToString("F2") +
                    "," + anchor.Y.ToString("F2") + "," + anchor.Z.ToString("F2") + ")）：" +
                    reason + " → 本次声音不会中继（动作照常执行）");
                return;
            }
            m_captureActive = true;
            m_captureClientId = requesterClientId;
            m_captureAnchor = anchor;
            m_captureAction = string.IsNullOrEmpty(action) ? "remote-action" : action;
        }

        /// <summary>
        /// 主机：远端请求执行完（含抛异常路径）。关闭窗口并发送一条合并事件。
        /// 本方法必须不抛异常（调用方的 finally 依赖它）。
        /// </summary>
        public void EndRemoteAction(int requesterClientId, string action)
        {
            if (!m_captureActive)
            {
                // 没有窗口（Begin 失败 / 已关闭）时 End 是合法的空操作；嵌套计数在这里清掉。
                m_nestedDepth = 0;
                return;
            }
            if (m_nestedDepth > 0)
            {
                // 内层的 End 先到：只消耗被拒绝的嵌套层数，外层窗口继续采集。
                m_nestedDepth--;
                return;
            }
            int capturedClientId = m_captureClientId;
            Vector3 anchor = m_captureAnchor;
            string capturedAction = m_captureAction;
            m_captureActive = false;
            m_captureClientId = 0;
            m_captureAnchor = Vector3.Zero;
            m_captureAction = null;
            m_nestedDepth = 0;
            m_windows++;
            try
            {
                if (capturedClientId != requesterClientId)
                {
                    m_windowMismatch++;
                    LogProblem("window-mismatch", "捕获窗口打开者 clientId=" + capturedClientId +
                        "，关闭者 clientId=" + requesterClientId + "（action=" + capturedAction +
                        "）→ 仍按窗口实际数据关闭并计数");
                }
                if (!m_port.EndCapture(out RemoteSoundClipData[] sounds, out string reason))
                {
                    m_captureFailures++;
                    LogProblem("capture-end:" + reason, "关闭远程动作声音捕获失败（clientId=" +
                        capturedClientId + " action=" + capturedAction + "）：" + reason +
                        " → 本次没有声音中继");
                    return;
                }
                if (sounds == null || sounds.Length == 0)
                {
                    m_emptyWindows++;
                    return;
                }
                m_captured += sounds.Length;
                int usable = Math.Min(sounds.Length, RemoteSoundPolicy.MaxSoundsPerEvent);
                if (sounds.Length > usable)
                {
                    m_capDropped += sounds.Length - usable;
                    LogProblem("batch-cap", "一次远程动作产生了 " + sounds.Length +
                        " 条声音，超过单条事件上限 " + RemoteSoundPolicy.MaxSoundsPerEvent +
                        " → 多出的 " + (sounds.Length - usable) + " 条明确丢弃（clientId=" +
                        capturedClientId + " action=" + capturedAction + "）");
                }
                var batch = new RemoteSoundClipData[usable];
                Array.Copy(sounds, batch, usable);

                m_peerScratch.Clear();
                if (!m_port.TryCollectPeers(m_peerScratch, out string peerReason))
                {
                    m_peerCollectionFailures++;
                    LogProblem("peer-collect:" + peerReason,
                        "收集玩家端位置失败（" + peerReason + "）→ 本次只有请求者会收到（其他端无从判断距离）");
                }
                RemoteSoundRecipientPlan plan = RemoteSoundRelayPolicy.Plan(capturedClientId,
                    m_peerScratch, anchor, RemoteSoundPolicy.MaxRelayDistance);
                m_skippedDistance += plan.SkippedDistance;
                m_skippedNoPosition += plan.SkippedNoPosition;
                if (plan.SkippedDistance > 0 || plan.SkippedNoPosition > 0)
                {
                    LogProblem("recipient-skip", "远程动作声音收件人裁剪（clientId=" +
                        capturedClientId + " action=" + capturedAction + "）：超出 " +
                        RemoteSoundPolicy.MaxRelayDistance.ToString("F0") + " m 跳过 " +
                        plan.SkippedDistance + " 端；读不到位置跳过 " + plan.SkippedNoPosition +
                        " 端（请求者仍恒定收到）");
                }

                var data = new RemoteSoundEventData(capturedClientId, NowMs(),
                    NextEventId(), batch);
                if (!RemoteSoundCodec.TryEncode(in data, out P.RemoteActionSound message,
                        out string encodeReason))
                {
                    m_encodeFailures++;
                    LogProblem("encode:" + encodeReason, "远程动作声音编码失败（" + encodeReason +
                        "）：" + data.Describe() + " → 本次没有中继");
                    return;
                }
                byte[] payload = message.ToByteArray();
                for (int i = 0; i < plan.Recipients.Length; i++)
                {
                    int recipient = plan.Recipients[i];
                    if (!m_transport.TrySendTo(recipient, MessageType.RemoteActionSound, payload))
                    {
                        m_sendFailures++;
                        LogProblem("send:" + recipient, "远程动作声音发送失败（clientId=" +
                            recipient + " " + data.Describe() + "）→ 该端听不到这次声音");
                        continue;
                    }
                    m_eventsSent++;
                    m_relayed += usable;
                }
                if (plan.Recipients.Length == 0)
                {
                    m_noRecipients++;
                    LogProblem("no-recipients", "远程动作声音没有收件人（" + data.Describe() +
                        "；请求者 clientId=" + capturedClientId + "）");
                }
            }
            catch (Exception e)
            {
                m_captureFailures++;
                LogProblem("end-exception:" + e.GetType().Name, "远程动作声音中继异常（" +
                    e.GetType().Name + " " + e.Message + "；clientId=" + capturedClientId +
                    " action=" + capturedAction + "）→ 本次没有中继（动作本身不受影响）");
            }
        }

        private uint NextEventId()
        {
            m_nextEventId++;
            if (m_nextEventId == 0) m_nextEventId = 1;
            return m_nextEventId;
        }

        private static long NowMs() => (long)(Time.RealTime * 1000.0);

        // ================================================================= 客户端：播放

        private void OnRemoteActionSound(MessageContext context, P.RemoteActionSound message)
        {
            if (!m_attached) return;
            m_eventsReceived++;
            if (m_control != null && m_control.IsHosting)
            {
                // 主机不是接收方：中继事件回到主机只可能是接线错误 —— 明确跳过（回声）。
                m_hostEchoSkipped++;
                LogProblem("event-on-host", "主机收到了 RemoteActionSound（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃（主机已经在本端听过这些声音）");
                return;
            }
            if (!m_port.IsClientReplica)
            {
                m_directionErrors++;
                LogProblem("event-not-client", "非客户端副本收到了 RemoteActionSound（本端既不是主机" +
                    "也不是联机客户端）→ 丢弃");
                return;
            }
            if (!RemoteSoundCodec.TryDecode(message, out RemoteSoundEventData data,
                    out string reason))
            {
                m_malformed++;
                LogProblem("malformed:" + reason, "RemoteActionSound 载荷非法（" + reason +
                    "，clientId=" + context.ClientId + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAccept(in data, out string orderReason))
            {
                if (orderReason == "duplicate") m_replay++;
                else m_stale++;
                LogProblem("order:" + orderReason, "远程动作声音 " + data.Describe() +
                    " 被拒绝（" + orderReason + "，last id=" + m_orderer.LastEventId +
                    "）→ 不重复播放");
                return;
            }
            long nowMs = NowMs();
            if (!m_recvLimiter.TryAccept(nowMs))
            {
                m_rateSkipped++;
                LogProblem("recv-rate", "远程动作声音 " + data.Describe() + " 到达过快（限速 " +
                    RemoteSoundPolicy.MaxReceiveEventsPerSecond + " 条/秒）→ 本事件不播放");
                return;
            }
            if (!m_port.TryGetLocalListenerPosition(out Vector3 listener, out string listenerReason))
            {
                m_noListener++;
                LogProblem("no-listener", "读不到本机监听者位置（" + listenerReason +
                    "）→ 远程动作声音 " + data.Describe() + " 无法按距离播放（计数，不猜原点）");
                return;
            }
            RemoteSoundPresentationPlan plan = RemoteSoundPresentationPolicy.Plan(
                data.Sounds, listener, RemoteSoundPolicy.MaxRelayDistance);
            m_skippedDistance += plan.SkippedDistance;
            if (plan.Clips.Length == 0)
            {
                LogProblem("present-all-skipped", "远程动作声音 " + data.Describe() +
                    " 全部超出 " + RemoteSoundPolicy.MaxRelayDistance.ToString("F0") +
                    " m → 本次不播放（skippedDistance=" + plan.SkippedDistance + "）");
                return;
            }
            int presented;
            string presentReason;
            try
            {
                presented = m_port.Present(plan.Clips, out presentReason);
            }
            catch (Exception e)
            {
                presented = 0;
                presentReason = "exception-" + e.GetType().Name + ": " + e.Message;
            }
            if (presented <= 0)
            {
                m_presentFailures++;
                LogProblem("present:" + presentReason, "远程动作声音播放失败 " +
                    data.Describe() + "：" + presentReason + " → 本次没有声音（明确计数）");
                return;
            }
            m_eventsPresented++;
            m_presented += presented;
            if (presented < plan.Clips.Length)
            {
                m_presentFailures++;
                LogProblem("present-partial", "远程动作声音 " + data.Describe() + " 只播放了 " +
                    presented + "/" + plan.Clips.Length + " 条（" + presentReason + "）");
            }
        }

        // ================================================================= 日志 / 摘要

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        private string Summary() => "windows=" + m_windows +
            " emptyWindows=" + m_emptyWindows +
            " remoteSoundCaptured=" + m_captured +
            " remoteSoundRelayed=" + m_relayed +
            " remoteSoundRecv=" + m_eventsReceived +
            " remoteSoundPresented=" + m_presented +
            " remoteSoundSkippedDistance=" + m_skippedDistance +
            " remoteSoundMalformed=" + m_malformed +
            " remoteSoundHostEchoSkipped=" + m_hostEchoSkipped +
            " remoteSoundCaptureFailed=" + m_captureFailures +
            " remoteSoundCapDropped=" + m_capDropped +
            " remoteSoundSendFailed=" + m_sendFailures +
            " remoteSoundNoRecipients=" + m_noRecipients +
            " remoteSoundSkippedNoPosition=" + m_skippedNoPosition +
            " remoteSoundReplay=" + m_replay +
            " remoteSoundStale=" + m_stale +
            " remoteSoundGaps=" + m_orderer.Gaps +
            " remoteSoundRateSkipped=" + m_rateSkipped +
            " remoteSoundWindowRateSkipped=" + m_windowRateSkipped +
            " remoteSoundNestedWindowSkipped=" + m_nestedWindowSkipped +
            " remoteSoundWindowMismatch=" + m_windowMismatch +
            " remoteSoundNoListener=" + m_noListener +
            " remoteSoundEventsSent=" + m_eventsSent +
            " remoteSoundEventsPresented=" + m_eventsPresented +
            " remoteSoundPeerCollectFailed=" + m_peerCollectionFailures +
            " remoteSoundEncodeFailed=" + m_encodeFailures +
            " remoteSoundDirectionErrors=" + m_directionErrors;
    }
}

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
    /// 远程 / 全局主机声音中继（**纯表现**，主机权威）。
    ///
    /// 两条数据流共用同一条 <c>RemoteActionSound</c>（消息 id 68，append-only）：
    ///   1. **远端动作窗口**（原有行为，保持不变）：主机替远端玩家执行 TryExecuteUse /
    ///      TryApplyContainerEdit 时，BlockUseSyncModule 在前后开 / 关捕获窗口；窗口内新增的
    ///      声音合成一条事件发给请求者 + 附近端，位置用动作锚点（精确）。
    ///   2. **全局连续捕获**（新增）：主机每帧（SuSubsystemAudio 在 base.Update 排空前）
    ///      把引擎音频队列的新增条目交给本模块；本模块按
    ///      <see cref="HostSoundRelayScheduler"/> 批量发送：
    ///        - 每 100 ms 一轮，每端每条消息 ≤ <see cref="RemoteSoundPolicy.MaxHostSoundsPerMessage"/> 条；
    ///        - 每端每秒 ≤ <see cref="RemoteSoundPolicy.MaxHostSoundsPerSecondPerPeer"/> 条；
    ///        - 只发给"客户端角色到（推断的）声源距离 ≤ 48 m"的端；读不到位置的端不发；
    ///        - 待发上限 / 超时 / 环缓冲满全部有计数器 + 限流日志，绝不静默丢；
    ///        - 新接入的端从当前队尾开始：重连不补发（客户端另有 (tick,event_id) 重放保护）。
    ///
    /// 防双播：
    ///   · 远端动作窗口捕获过的条目由引擎适配层登记进 covered 账本，全局捕获不会重复发送；
    ///   · 营火 / 烧尽 / 拾取 / 水花 / 爆炸 / 武器开火等定向事件同样登记；
    ///   · 客户端本机播过的同名声音（自己的方块动作 / UI / 本地生物 / 预测表现）在表现前
    ///     被 <see cref="IRemoteAudioPort.TryConsumeLocalEcho"/> 消费并跳过（LocalEchoSkipped）；
    ///   · 客户端**绝不产生**中继事件：本模块在客户端只解码 / 去重 / 限速 / 裁剪 / 播放。
    ///
    /// 客户端顺序：重放保护 → 严格解码 → event_id 排序去重 → 每秒事件限速 → 本机监听距离
    /// 裁剪（48 m）→ 本机回声过滤 → 只调 SubsystemAudio.PlaySound。
    ///
    /// 无静态状态；只依赖端口与 Net/RemoteAudioNet.cs / Net/HostAudioRelayNet.cs 的纯逻辑。
    /// </summary>
    public sealed class RemoteAudioSyncModule : IMultiplayerModule, ISuMPMessageModule,
        IRemoteAudioRelayPort
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IRemoteAudioPort m_port;
        private readonly RemoteSoundOrderer m_orderer = new RemoteSoundOrderer();
        private readonly RemoteSoundReplayGuard m_replayGuard = new RemoteSoundReplayGuard();
        private readonly RemoteSoundRateLimiter m_sendLimiter =
            new RemoteSoundRateLimiter(RemoteSoundPolicy.MaxEventsPerSecond);
        private readonly RemoteSoundRateLimiter m_recvLimiter =
            new RemoteSoundRateLimiter(RemoteSoundPolicy.MaxReceiveEventsPerSecond);
        private readonly HostSoundRelayScheduler m_hostRelay = new HostSoundRelayScheduler();
        private readonly List<RemoteSoundPeer> m_peerScratch = new List<RemoteSoundPeer>(16);
        private readonly List<RemoteSoundClipData> m_captureScratch =
            new List<RemoteSoundClipData>(256);
        private readonly List<HostSoundPeerBatch> m_batchScratch =
            new List<HostSoundPeerBatch>(8);
        private readonly List<RemoteSoundClipData> m_presentScratch =
            new List<RemoteSoundClipData>(16);
        /// <summary>客户端本机所有本地角色（分裂屏 / 多角色）的听音位置快照。</summary>
        private readonly List<Vector3> m_listenerScratch = new List<Vector3>(4);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private uint m_nextEventId;
        private long m_lastSummaryMs;
        private long m_lastHostRoundMs;
        private bool m_hostCaptureEnabled;

        // 当前捕获窗口（主机；同一时刻最多一个 —— 多窗口嵌套会被明确拒绝，不猜）
        private bool m_captureActive;
        private int m_captureClientId;
        private Vector3 m_captureAnchor;
        private string m_captureAction;
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

        // ---------------------------------------------------------------- 全局中继计数（任务要求的口径）
        private long m_audioSent;
        private long m_audioSounds;
        private long m_audioPeers;
        private long m_audioRecv;
        private long m_audioPresented;
        private long m_audioSkippedDistance;
        private long m_audioSkippedRate;
        private long m_audioCapDropped;
        private long m_audioMalformed;
        private long m_audioDuplicate;
        private long m_audioHostEchoSkipped;
        private long m_audioLocalEchoSkipped;
        private long m_audioExpired;
        private long m_audioNoPeersRounds;
        private long m_audioCaptureDisabled;
        private long m_audioFaults;

        // ---------------------------------------------------------------- 本机多角色槽位归属计数
        private long m_audioSlotAttributed;
        private long m_audioSlotUnattributed;
        private long m_audioSlotSkippedDistance;
        private long m_audioSlotPeers;
        private long m_lastReportedSchedulerCap;
        private long m_lastReportedSchedulerExpired;
        private long m_lastReportedSchedulerNoPeers;

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
                "远程动作 / 全局主机声音中继（主机捕获 / 客户端播放）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "effects.remote-audio" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "remote-audio: " + Summary() + " · " + m_port.Describe();

        public bool IsAvailable => m_attached && m_port != null &&
            m_control != null && m_control.IsHosting;

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 远程声音中继不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "远程声音中继不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到远程声音事件");
            }
            m_lastSummaryMs = 0;
            m_lastHostRoundMs = 0;
            m_wasConnected = false;
            m_attached = true;
            Log("远程声音中继就绪：主机 = 远端动作窗口 + 每帧全局捕获（≤" +
                RemoteSoundPolicy.MaxHostSoundsPerMessage + " 条/消息，" +
                RemoteSoundPolicy.MaxHostSoundsPerSecondPerPeer + " 条/秒/端，" +
                RemoteSoundPolicy.MaxRelayDistance.ToString("F0") + " m）；客户端 = 解码 / " +
                "重放保护 / 去重 / 限速 / 距离裁剪 / 本机回声过滤后只播放");
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
            try { m_port.SetHostCaptureEnabled(false); } catch (Exception) { }
            m_hostCaptureEnabled = false;
            m_audioExpired += m_hostRelay.PendingCount;
            m_hostRelay.Clear();
            m_captureScratch.Clear();
            m_batchScratch.Clear();
            m_presentScratch.Clear();
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_orderer.Reset();
            m_replayGuard.Reset();
            m_sendLimiter.Reset();
            m_recvLimiter.Reset();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("远程声音中继下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            bool connected = m_transport != null && m_transport.IsConnected;
            if (connected && !m_wasConnected)
            {
                m_orderer.Reset();
                m_replayGuard.NewConnection();
                m_sendLimiter.Reset();
                m_recvLimiter.Reset();
                m_hostRelay.Clear();
                m_nextEventId = 0;
                Log("远程声音：连接已建立 → event_id / 去重 / 限速已重置（旧会话的 id 不再复用；" +
                    "重放保护保留历史，重连不重播）");
            }
            if (!connected && m_wasConnected)
            {
                m_port.SetHostCaptureEnabled(false);
                m_hostCaptureEnabled = false;
                m_hostRelay.Clear();
                m_captureScratch.Clear();
                Log("远程声音：连接断开 → 全局捕获停止、待发声音清空（客户端重连后绝不补发）");
            }
            m_wasConnected = connected;
            long nowMs = (long)(tick.Now * 1000.0);
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting && connected)
            {
                m_peerScratch.Clear();
                bool peersOk = m_port.TryCollectPeers(m_peerScratch, out string peerReason);
                if (!peersOk && m_peerScratch.Count == 0 &&
                    !string.Equals(peerReason, "no-avatars", StringComparison.Ordinal))
                {
                    m_peerCollectionFailures++;
                    LogProblem("relay-peers:" + peerReason,
                        "全局声音中继收集玩家端位置失败（" + peerReason + "）→ 本轮不发送" +
                        "（不会往原点 / 主机自己的位置猜）");
                }
                m_audioPeers = m_peerScratch.Count;
                bool shouldCapture = m_peerScratch.Count > 0 && !m_port.IsClientReplica;
                SetHostCapture(shouldCapture, nowMs);
                if (shouldCapture)
                {
                    PumpHostRelay(nowMs);
                }
            }
            else
            {
                if (m_hostCaptureEnabled)
                {
                    SetHostCapture(false, nowMs);
                }
                else
                {
                    m_peerScratch.Clear();
                    m_audioPeers = 0;
                }
            }
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        private void SetHostCapture(bool enabled, long nowMs)
        {
            if (m_hostCaptureEnabled == enabled) return;
            bool previous = m_hostCaptureEnabled;
            try { m_port.SetHostCaptureEnabled(enabled); }
            catch (Exception e)
            {
                m_hostCaptureEnabled = previous;
                m_audioFaults++;
                LogProblem("capture-toggle:" + e.GetType().Name,
                    "切换全局声音捕获失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 本端全局中继" + (enabled ? "未启用" : "可能残留一帧"));
                return;
            }
            m_hostCaptureEnabled = enabled;
            if (enabled)
            {
                m_lastHostRoundMs = nowMs;
                Log("全局主机声音中继：开始连续捕获（每 " +
                    RemoteSoundPolicy.HostBatchIntervalMs + " ms 一轮，每端 ≤" +
                    RemoteSoundPolicy.MaxHostSoundsPerSecondPerPeer + " 条/秒，" +
                    RemoteSoundPolicy.MaxRelayDistance.ToString("F0") + " m）");
            }
            else
            {
                if (m_hostRelay.PendingCount > 0)
                {
                    m_audioExpired += m_hostRelay.PendingCount;
                    LogProblem("relay-disabled-pending",
                        "停止全局中继时还有 " + m_hostRelay.PendingCount +
                        " 条声音未发出 → 明确丢弃（不跨连接补发）");
                }
                m_hostRelay.Clear();
                m_captureScratch.Clear();
                Log("全局主机声音中继：停止连续捕获（没有可定位玩家端 / 非主机 / 单机）");
            }
        }

        // ================================================================= 主机：全局批量中继

        private void PumpHostRelay(long nowMs)
        {
            // 先同步玩家端游标：新端从"当前队尾"开始，绝不补发连接前的声音；
            // 然后再把本帧捕获的声音入队（这些才会发给新端）。
            m_hostRelay.SyncPeers(m_peerScratch);
            m_captureScratch.Clear();
            int droppedCap = 0;
            if (m_port.TryDrainHostSounds(m_captureScratch, out droppedCap, out string reason))
            {
                if (droppedCap > 0)
                {
                    m_audioCapDropped += droppedCap;
                    LogProblem("capture-ring-cap", "全局音频捕获环缓冲满 → 丢弃最旧 " + droppedCap +
                        " 条（累计 " + m_audioCapDropped + "；明确计数，不静默）");
                }
                for (int i = 0; i < m_captureScratch.Count; i++)
                {
                    RemoteSoundClipData clip = m_captureScratch[i];
                    // 槽位归属 = 主机引擎层用"本机角色更新窗口"把声音绑定到了某个角色的
                    // 真实位置（SlotSource）；其余仍是今天的世界声音（RelativeToHost /
                    // NonPositional，锚在主机监听者）。两条路径互斥：槽位声音绝不会再走全局。
                    if (clip.Attenuation == RemoteSoundAttenuation.SlotSource)
                        m_audioSlotAttributed++;
                    else
                        m_audioSlotUnattributed++;
                    m_hostRelay.Enqueue(in clip, nowMs);
                }
            }
            else if (reason != "capture-disabled")
            {
                m_audioCaptureDisabled++;
                LogProblem("capture-drain:" + reason,
                    "排空全局音频捕获失败（" + reason + "）→ 本轮不发送（明确计数）");
            }
            if (m_port.TryTakeHostCaptureFault(out string fault))
            {
                m_audioFaults++;
                LogProblem("capture-fault:" + fault,
                    "全局音频捕获故障：" + fault + " → 持续出现时全局中继会不完整（明确计数）");
            }
            ReportSchedulerProblems();
            if (nowMs - m_lastHostRoundMs < RemoteSoundPolicy.HostBatchIntervalMs) return;
            m_lastHostRoundMs = nowMs;
            m_batchScratch.Clear();
            int batches = m_hostRelay.Plan(m_peerScratch, nowMs,
                RemoteSoundPolicy.MaxRelayDistance, m_batchScratch);
            if (batches == 0) return;
            for (int i = 0; i < m_batchScratch.Count; i++)
            {
                HostSoundPeerBatch batch = m_batchScratch[i];
                m_audioSkippedDistance += batch.SkippedDistance;
                m_audioSkippedRate += batch.SkippedRate;
                // 槽位声音的"每端距离裁剪"：调度器按该端到**声源角色真实位置**的
                // 距离判定（> 48 m 丢弃），与全世界声音共用同一轮询但不共用计数。
                m_audioSlotSkippedDistance += batch.SlotSkippedDistance;
                if (batch.SlotSounds > 0) m_audioSlotPeers++;
                if (batch.Sounds.Length == 0) continue;
                var data = new RemoteSoundEventData(0, NowMs(), NextEventId(), batch.Sounds);
                if (!RemoteSoundCodec.TryEncode(in data, out P.RemoteActionSound message,
                        out string encodeReason))
                {
                    m_encodeFailures++;
                    LogProblem("host-encode:" + encodeReason,
                        "全局声音批量编码失败（" + encodeReason + "）：" + data.Describe() +
                        " → 本批 " + batch.Sounds.Length + " 条没有中继");
                    continue;
                }
                byte[] payload = message.ToByteArray();
                if (!m_transport.TrySendTo(batch.ClientId, MessageType.RemoteActionSound,
                        payload))
                {
                    m_sendFailures++;
                    LogProblem("host-send:" + batch.ClientId,
                        "全局声音发送失败（clientId=" + batch.ClientId + " " + data.Describe() +
                        "）→ 该端听不到这批声音");
                    continue;
                }
                m_audioSent++;
                m_audioSounds += batch.Sounds.Length;
                m_eventsSent++;
                m_relayed += batch.Sounds.Length;
            }
        }

        private void ReportSchedulerProblems()
        {
            long cap = m_hostRelay.CapDropped;
            long expired = m_hostRelay.Expired;
            long noPeers = m_hostRelay.DroppedNoPeers;
            if (cap > m_lastReportedSchedulerCap)
            {
                LogProblem("host-cap",
                    "全局声音待发队列达到上限 " + RemoteSoundPolicy.MaxPendingHostSounds +
                    " → 丢弃最旧 " + (cap - m_lastReportedSchedulerCap) + " 条（累计 " + cap + "）");
                m_lastReportedSchedulerCap = cap;
            }
            if (expired > m_lastReportedSchedulerExpired)
            {
                m_audioExpired += expired - m_lastReportedSchedulerExpired;
                LogProblem("host-expired",
                    "有 " + (expired - m_lastReportedSchedulerExpired) + " 条全局声音在 " +
                    RemoteSoundPolicy.HostPendingRetentionMs + " ms 内没能发出（对端限速 / 断线）" +
                    " → 明确丢弃（累计 " + expired + "）");
                m_lastReportedSchedulerExpired = expired;
            }
            if (noPeers > m_lastReportedSchedulerNoPeers)
            {
                m_audioNoPeersRounds++;
                m_audioCapDropped += noPeers - m_lastReportedSchedulerNoPeers;
                LogProblem("host-no-peers",
                    "没有可定位玩家端时仍有 " + (noPeers - m_lastReportedSchedulerNoPeers) +
                    " 条待发声音 → 清空并计数（不会在重连后补发）");
                m_lastReportedSchedulerNoPeers = noPeers;
            }
        }

        // ================================================================= 主机：远端动作捕获窗口

        void IRemoteAudioRelayPort.BeginRemoteAction(int requesterClientId, Vector3 anchor,
            string action) => BeginRemoteAction(requesterClientId, anchor, action);

        void IRemoteAudioRelayPort.EndRemoteAction(int requesterClientId, string action) =>
            EndRemoteAction(requesterClientId, action);

        public void BeginRemoteAction(int requesterClientId, Vector3 anchor, string action)
        {
            if (!m_attached) return;
            if (m_control == null || !m_control.IsHosting)
            {
                m_directionErrors++;
                LogProblem("begin-not-host", "非主机上调用了远程动作捕获（clientId=" +
                    requesterClientId + " action=" + action + "）→ 不打开窗口");
                return;
            }
            if (requesterClientId <= 0)
            {
                m_hostEchoSkipped++;
                LogProblem("begin-host-echo", "主机本机动作请求了远程声音捕获（clientId=" +
                    requesterClientId + " action=" + action + "）→ 跳过（本机动作由全局捕获负责）");
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

        public void EndRemoteAction(int requesterClientId, string action)
        {
            if (!m_captureActive)
            {
                m_nestedDepth = 0;
                return;
            }
            if (m_nestedDepth > 0)
            {
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
                m_audioSkippedDistance += plan.SkippedDistance;
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
                    m_audioSent++;
                    m_audioSounds += usable;
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
            m_audioRecv++;
            if (m_control != null && m_control.IsHosting)
            {
                m_hostEchoSkipped++;
                m_audioHostEchoSkipped++;
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
                m_audioMalformed++;
                LogProblem("malformed:" + reason, "RemoteActionSound 载荷非法（" + reason +
                    "，clientId=" + context.ClientId + "）→ 整条丢弃");
                return;
            }
            if (!m_replayGuard.TryAccept(in data))
            {
                m_replay++;
                m_audioDuplicate++;
                LogProblem("replay-guard", "RemoteActionSound 重放（tick=" + data.ServerTickMs +
                    " id=" + data.EventId + "，clientId=" + context.ClientId +
                    "）→ 已在旧会话播放过，绝不重播");
                return;
            }
            if (!m_orderer.TryAccept(in data, out string orderReason))
            {
                if (orderReason == "duplicate")
                {
                    m_replay++;
                    m_audioDuplicate++;
                }
                else
                {
                    m_stale++;
                }
                LogProblem("order:" + orderReason, "远程声音 " + data.Describe() +
                    " 被拒绝（" + orderReason + "，last id=" + m_orderer.LastEventId +
                    "）→ 不重复播放");
                return;
            }
            long nowMs = NowMs();
            if (!m_recvLimiter.TryAccept(nowMs))
            {
                m_rateSkipped++;
                m_audioSkippedRate++;
                LogProblem("recv-rate", "远程声音 " + data.Describe() + " 到达过快（限速 " +
                    RemoteSoundPolicy.MaxReceiveEventsPerSecond + " 条/秒）→ 本事件不播放");
                return;
            }
            m_listenerScratch.Clear();
            if (!m_port.TryGetLocalListenerPositions(m_listenerScratch,
                    out string listenerReason) || m_listenerScratch.Count == 0)
            {
                m_noListener++;
                LogProblem("no-listener", "读不到本机监听者位置（" + listenerReason +
                    "）→ 远程声音 " + data.Describe() + " 无法按距离播放（计数，不猜原点）");
                return;
            }
            // 多本机角色（分裂屏）：每条声音按"离它最近的本机角色"算距离 / 重投影；
            // 槽位声音（SlotSource）的位置就是主机角色的真实位置，距离是精确值。
            RemoteSoundPresentationPlan plan = RemoteSoundPresentationPolicy.Plan(
                data.Sounds, m_listenerScratch, RemoteSoundPolicy.MaxRelayDistance);
            m_skippedDistance += plan.SkippedDistance;
            m_audioSkippedDistance += plan.SkippedDistance;
            if (plan.Clips.Length == 0)
            {
                LogProblem("present-all-skipped", "远程声音 " + data.Describe() +
                    " 全部超出 " + RemoteSoundPolicy.MaxRelayDistance.ToString("F0") +
                    " m → 本次不播放（skippedDistance=" + plan.SkippedDistance + "）");
                return;
            }
            m_presentScratch.Clear();
            int localEcho = 0;
            for (int i = 0; i < plan.Clips.Length; i++)
            {
                RemoteSoundClipData clip = plan.Clips[i];
                if (m_port.TryConsumeLocalEcho(clip.Sound))
                {
                    localEcho++;
                    continue;
                }
                m_presentScratch.Add(clip);
            }
            if (localEcho > 0)
            {
                m_audioLocalEchoSkipped += localEcho;
                LogProblem("local-echo", "远程声音 " + data.Describe() + " 中有 " + localEcho +
                    " 条与本机刚播过的声音同名 → 跳过（客户端自己的动作 / UI / 本地表现不重复）");
            }
            if (m_presentScratch.Count == 0)
            {
                return;
            }
            int presented;
            string presentReason;
            try
            {
                presented = m_port.Present(m_presentScratch.ToArray(), out presentReason);
            }
            catch (Exception e)
            {
                presented = 0;
                presentReason = "exception-" + e.GetType().Name + ": " + e.Message;
            }
            if (presented <= 0)
            {
                m_presentFailures++;
                LogProblem("present:" + presentReason, "远程声音播放失败 " +
                    data.Describe() + "：" + presentReason + " → 本次没有声音（明确计数）");
                return;
            }
            m_eventsPresented++;
            m_presented += presented;
            m_audioPresented += presented;
            if (presented < m_presentScratch.Count)
            {
                m_presentFailures++;
                LogProblem("present-partial", "远程声音 " + data.Describe() + " 只播放了 " +
                    presented + "/" + m_presentScratch.Count + " 条（" + presentReason + "）");
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
            " remoteSoundDirectionErrors=" + m_directionErrors +
            // 任务口径的 10 个计数（短名，便于日志 grep / 自检断言）
            " audioSent=" + m_audioSent +
            " audioSounds=" + m_audioSounds +
            " audioPeers=" + m_audioPeers +
            " audioRecv=" + m_audioRecv +
            " audioPresented=" + m_audioPresented +
            " audioSkippedDistance=" + m_audioSkippedDistance +
            " audioSkippedRate=" + m_audioSkippedRate +
            " audioCapDropped=" + m_audioCapDropped +
            " audioMalformed=" + m_audioMalformed +
            " audioDuplicate=" + m_audioDuplicate +
            " audioHostEchoSkipped=" + m_audioHostEchoSkipped +
            " audioLocalEchoSkipped=" + m_audioLocalEchoSkipped +
            " audioExpired=" + m_audioExpired +
            " audioNoPeersRounds=" + m_audioNoPeersRounds +
            " audioCaptureDisabled=" + m_audioCaptureDisabled +
            " audioFaults=" + m_audioFaults +
            // 本机多角色槽位归属（任务口径）：归属成功 / 仍按全局 / 每端距离裁剪 / 载有槽位
            // 声音的端批次数。前两项之和 = 主机排空的声音总数（互斥）。
            " audioSlotAttributed=" + m_audioSlotAttributed +
            " audioSlotUnattributed=" + m_audioSlotUnattributed +
            " audioSlotSkippedDistance=" + m_audioSlotSkippedDistance +
            " audioSlotPeers=" + m_audioSlotPeers +
            " hostRelayPending=" + m_hostRelay.PendingCount +
            " hostRelayCapDropped=" + m_hostRelay.CapDropped +
            " hostRelayExpired=" + m_hostRelay.Expired +
            " hostRelaySkippedDistance=" + m_hostRelay.SkippedDistance +
            " hostRelaySkippedRate=" + m_hostRelay.SkippedRate +
            " hostRelayReconnects=" + m_hostRelay.Reconnects;
    }
}

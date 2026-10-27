using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol.Effects;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 水花表现同步模块（**主机权威，客户端纯表现**）。
    ///
    /// 实机问题：拾取物 / 投射物落水时，引擎在**主机本端**创建
    /// <c>WaterSplashParticleSystem</c> 并播放 <c>Audio/Splashes</c>
    /// （Source: Survivalcraft/Game/SubsystemPickables.cs:325-330,
    /// Survivalcraft/Game/SubsystemProjectiles.cs:383-390）；这条表现不在 EntityState 里，
    /// 所以客户端既看不到水花也听不到声音。
    ///
    /// 主机侧：
    ///   · 两个子系统外壳（<c>SuSubsystemPickables</c> / <c>SuSubsystemProjectiles</c>）在
    ///     base.Update 前后开 / 关捕获窗口；引擎适配层把窗口内新增的
    ///     <c>WaterSplashParticleSystem</c> 交给本模块（位置 = 引擎构造参数，large = 构造参数，
    ///     sound = 引擎同一分支也排了 Audio/Splashes）；
    ///   · 本模块按 tick 分批（每批 ≤ <see cref="WaterSplashPolicy.MaxSplashesPerEvent"/>），
    ///     按每个客户端角色位置到每个水花的距离裁剪（48 m），只发给命中者；
    ///   · 本机玩家由引擎原生表现，绝不发给自己（没有回声）。
    ///
    /// 客户端侧：
    ///   · 严格解码 → event_id 去重 / 拒绝重放 → 每秒限速 → 按本机监听者距离裁剪 →
    ///     只调 <c>AddParticleSystem(new WaterSplashParticleSystem(...))</c> 与
    ///     <c>PlayRandomSound("Audio/Splashes", ...)</c>；不写格 / 不生成实体 / 不伤害。
    ///
    /// 无静态状态；只依赖端口与 Net/WaterSplashNet.cs 的纯逻辑。
    /// </summary>
    public sealed class WaterSplashSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IWaterSplashPort m_port;
        private readonly WaterSplashOrderer m_orderer = new WaterSplashOrderer();
        private readonly WaterSplashRateLimiter m_sendLimiter =
            new WaterSplashRateLimiter(WaterSplashPolicy.MaxEventsPerSecond);
        private readonly WaterSplashRateLimiter m_recvLimiter =
            new WaterSplashRateLimiter(WaterSplashPolicy.MaxEventsPerSecond);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<WaterSplashData> m_pending = new List<WaterSplashData>(64);
        private readonly List<RemoteSoundPeer> m_peerScratch = new List<RemoteSoundPeer>(16);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private bool m_captureEnabled;
        private uint m_nextEventId;
        private long m_lastSummaryMs;

        // ---------------------------------------------------------------- 主机计数
        private long m_hostSplashes;
        private long m_hostIdle;
        private long m_hostRejected;
        private long m_pendingOverflow;
        private long m_rateSkipped;
        private long m_eventSent;
        private long m_eventSendFailed;
        private long m_skippedDistance;
        private long m_skippedNoPosition;
        private long m_noRecipients;
        private long m_peerFailures;
        private long m_captureFailures;
        private long m_encodeFailures;

        // ---------------------------------------------------------------- 客户端计数
        private long m_eventRecv;
        private long m_eventApplied;
        private long m_eventMalformed;
        private long m_eventDuplicate;
        private long m_eventStale;
        private long m_eventRateSkipped;
        private long m_directionErrors;
        private long m_presentFailures;
        private long m_particlesCreated;
        private long m_soundsPlayed;
        private long m_clientSkippedDistance;
        private long m_missingAudio;

        public WaterSplashSyncModule(IWaterSplashPort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("water-splash",
                "水花表现同步（主机捕获 / 客户端重建）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "effects.water-splash" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "water-splash: " + Summary() + " · " + m_port.Describe();

        // 状态行 / 自检可读的精确计数（名字即需求里的计数名）。
        public long SplashEventSent => m_eventSent;
        public long SplashEventRecv => m_eventRecv;
        public long SplashEventApplied => m_eventApplied;
        public long SplashEventSkippedDistance => m_skippedDistance + m_clientSkippedDistance;
        public long SplashEventMalformed => m_eventMalformed;
        public long SplashEventSendFailed => m_eventSendFailed;
        public long SplashParticlesCreated => m_particlesCreated;
        public long SplashSoundsPlayed => m_soundsPlayed;

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 水花表现同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "水花表现同步不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到水花事件");
            }
            m_port.HostSplash += OnHostSplash;
            m_pending.Clear();
            m_nextEventId = 0;
            m_captureEnabled = false;
            m_wasConnected = false;
            m_attached = true;
            Log("水花表现同步就绪：主机 = 两个子系统外壳的 Update 窗口内捕获粒子差集，" +
                "按客户端距离（" + WaterSplashPolicy.MaxRelayDistance.ToString("F0") +
                " m）分批下发（≤" + WaterSplashPolicy.MaxSplashesPerEvent +
                " 个/事件，" + WaterSplashPolicy.MaxEventsPerSecond +
                " 事件/秒）；客户端 = 解码 / 去重 / 限速 / 距离裁剪后重建同一粒子系统" +
                (WaterSplashPolicy.SplashSoundAutoDelay ? "与声音" : string.Empty));
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.WaterSplashEvent,
                P.WaterSplashEvent.Parser,
                new DelegateMessageHandler<P.WaterSplashEvent>(OnWaterSplashEvent), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_port.HostSplash -= OnHostSplash;
            if (m_captureEnabled)
            {
                m_port.SetHostCaptureEnabled(false, out _);
                m_captureEnabled = false;
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_pending.Clear();
            m_orderer.Reset();
            m_sendLimiter.Reset();
            m_recvLimiter.Reset();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("水花表现同步模块下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            long nowMs = (long)(tick.Now * 1000.0);
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting)
            {
                bool wantCapture = m_control.ConnectedPlayerCount > 0;
                if (wantCapture)
                {
                    // 每帧重申一次：引擎适配层在 Project / 角色切换时会重置捕获标志，
                    // 这里重申保证世界重载后仍能继续捕获（幂等，失败只计数 + 限流日志）。
                    if (m_port.SetHostCaptureEnabled(true, out string reason))
                    {
                        m_captureEnabled = true;
                    }
                    else
                    {
                        m_captureFailures++;
                        LogProblem("capture:" + reason,
                            "主机开启水花捕获失败（" + reason + "）→ 本帧继续按未捕获运行" +
                            "（客户端看不到水花；计数 splashCaptureFailures=" + m_captureFailures + "）");
                    }
                }
                else if (m_captureEnabled)
                {
                    m_port.SetHostCaptureEnabled(false, out _);
                    m_captureEnabled = false;
                }
                FlushHost(nowMs);
            }
            else
            {
                if (m_captureEnabled)
                {
                    m_port.SetHostCaptureEnabled(false, out _);
                    m_captureEnabled = false;
                }
                // 客户端 / 单机不保留主机待发数据：绝不在切角色后把旧水花补发出去。
                m_pending.Clear();
            }

            bool connected = m_transport != null && m_transport.IsConnected;
            if (connected && !m_wasConnected)
            {
                m_orderer.Reset();
                m_recvLimiter.Reset();
                // 重连不补发：旧会话的水花留在上一会话，清掉待发队列。
                m_pending.Clear();
                Log("water-splash: 连接已建立 → 去重闸门 / 限速已重置，待发水花已清空（不重放）");
            }
            m_wasConnected = connected;

            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ------------------------------------------------------------ 主机：捕获 → 按距离分批下发

        private void OnHostSplash(WaterSplashData splash)
        {
            m_hostSplashes++;
            if (m_control?.IsHosting != true)
            {
                // 单机 / 尚未建房：捕获窗口不会打开；真正打开后这里不该出现。
                if (m_port.IsClientReplica)
                {
                    m_hostRejected++;
                    LogProblem("splash-on-client", "客户端副本上报了水花（" +
                        splash.Describe() + "）→ 丢弃（客户端绝不广播）");
                }
                else
                {
                    m_hostIdle++;
                }
                return;
            }
            if (m_pending.Count >= WaterSplashPolicy.MaxPendingSplashes)
            {
                m_pendingOverflow++;
                LogProblem("pending-overflow", "主机待发水花超过上限 " +
                    WaterSplashPolicy.MaxPendingSplashes + " → 丢弃最早的水花" +
                    "（累计 splashPendingOverflow=" + m_pendingOverflow + "）");
                m_pending.RemoveAt(0);
            }
            m_pending.Add(splash);
        }

        private void FlushHost(long nowMs)
        {
            if (m_pending.Count == 0) return;
            if (!m_sendLimiter.TryAccept(nowMs))
            {
                int dropped = m_pending.Count;
                m_rateSkipped += dropped;
                m_pending.Clear();
                LogProblem("send-rate", "水花发送限速 " +
                    WaterSplashPolicy.MaxEventsPerSecond + " 事件/秒：" + dropped +
                    " 个水花本拍不发（累计 splashRateSkipped=" + m_rateSkipped + "）");
                return;
            }
            int count = Math.Min(m_pending.Count, WaterSplashPolicy.MaxSplashesPerEvent);
            var splashes = new WaterSplashData[count];
            for (int i = 0; i < count; i++) splashes[i] = m_pending[i];
            m_pending.RemoveRange(0, count);

            if (!m_port.TryCollectPeers(m_peerScratch, out string peerReason))
            {
                m_peerFailures++;
                m_noRecipients += count;
                LogProblem("peers:" + peerReason, "主机有 " + count +
                    " 个水花但收集不到玩家端（" + peerReason + "）→ 本批不下发" +
                    "（累计 splashPeerFailures=" + m_peerFailures + "）");
                return;
            }
            WaterSplashRelayPlan plan = WaterSplashRelayPolicy.Plan(m_peerScratch, splashes,
                WaterSplashPolicy.MaxRelayDistance);
            m_skippedDistance += plan.SkippedDistance;
            m_skippedNoPosition += plan.SkippedNoPosition;
            if (plan.Recipients.Length == 0)
            {
                m_noRecipients += count;
                LogProblem("no-recipients", "本批 " + count +
                    " 个水花没有客户端在 " +
                    WaterSplashPolicy.MaxRelayDistance.ToString("F0") + " m 内" +
                    "（超出 " + plan.SkippedDistance + " 对 / 无位置 " +
                    plan.SkippedNoPosition + " 对）→ 不下发" +
                    "（累计 splashNoRecipients=" + m_noRecipients + "）");
                return;
            }

            uint eventId = NextEventId();
            for (int i = 0; i < plan.Recipients.Length; i++)
            {
                WaterSplashRecipient recipient = plan.Recipients[i];
                var data = new WaterSplashEventData(nowMs, eventId, recipient.Splashes);
                if (!WaterSplashCodec.TryEncode(in data, out P.WaterSplashEvent message,
                        out string encodeReason))
                {
                    m_encodeFailures++;
                    LogProblem("encode:" + encodeReason, "水花事件 " + data.Describe() +
                        " 编码失败（" + encodeReason + "）→ 本端不下发（不会静默）");
                    continue;
                }
                if (!m_transport.TrySendTo(recipient.ClientId, MessageType.WaterSplashEvent,
                        message.ToByteArray()))
                {
                    m_eventSendFailed++;
                    LogProblem("send:" + recipient.ClientId, "水花事件 " + data.Describe() +
                        " 发给 clientId=" + recipient.ClientId + " 失败（传输未入队）");
                    continue;
                }
                m_eventSent++;
            }
        }

        private uint NextEventId()
        {
            m_nextEventId++;
            if (m_nextEventId == 0) m_nextEventId++;
            return m_nextEventId;
        }

        // ------------------------------------------------------------ 客户端：只做表现

        private void OnWaterSplashEvent(MessageContext context, P.WaterSplashEvent message)
        {
            m_eventRecv++;
            if (m_control?.IsHosting == true)
            {
                m_directionErrors++;
                LogProblem("event-on-host", "主机收到了 WaterSplashEvent（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            if (!WaterSplashCodec.TryDecode(message, out WaterSplashEventData data,
                    out string reason))
            {
                m_eventMalformed++;
                LogProblem("malformed:" + reason, "WaterSplashEvent 解码失败（" + reason +
                    "，clientId=" + context.ClientId + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAccept(in data, out string orderReason))
            {
                if (orderReason == "duplicate")
                {
                    m_eventDuplicate++;
                    LogProblem("duplicate", "重复的 WaterSplashEvent " + data.Describe() +
                        " → 只表现一次（deduped）");
                }
                else
                {
                    m_eventStale++;
                    LogProblem("stale", "过期/越序的 WaterSplashEvent " + data.Describe() +
                        "（last id=" + m_orderer.LastEventId + "）→ 丢弃");
                }
                return;
            }
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (!m_recvLimiter.TryAccept(nowMs))
            {
                m_eventRateSkipped++;
                LogProblem("recv-rate", "WaterSplashEvent " + data.Describe() +
                    " 到达过快（限速 " + WaterSplashPolicy.MaxEventsPerSecond +
                    " 事件/秒）→ 本事件不表现（计数 splashEventRateSkipped=" +
                    m_eventRateSkipped + "）");
                return;
            }
            if (!m_port.Present(in data, out WaterSplashPresentationResult result,
                    out string presentReason))
            {
                m_presentFailures++;
                LogProblem("present:" + presentReason, "水花表现失败 " + data.Describe() +
                    "（" + presentReason + "）→ 本次没有画面 / 声音（明确计数，不静默）");
                return;
            }
            m_particlesCreated += result.ParticlesCreated;
            m_soundsPlayed += result.SoundsPlayed;
            m_clientSkippedDistance += result.SkippedDistance;
            m_missingAudio += result.MissingAudio;
            if (result.Presented > 0) m_eventApplied++;
        }

        // ------------------------------------------------------------ 日志 / 摘要

        private void Log(string message) =>
            m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        private string Summary() => "host splashEventSent=" + m_eventSent +
            " splashEventSendFailed=" + m_eventSendFailed +
            " splashEventSkippedDistance=" + m_skippedDistance +
            " splashSkippedNoPosition=" + m_skippedNoPosition +
            " splashNoRecipients=" + m_noRecipients +
            " splashHostSplashes=" + m_hostSplashes +
            " splashHostIdle=" + m_hostIdle +
            " splashHostRejected=" + m_hostRejected +
            " splashPendingOverflow=" + m_pendingOverflow +
            " splashRateSkipped=" + m_rateSkipped +
            " splashPeerFailures=" + m_peerFailures +
            " splashCaptureFailures=" + m_captureFailures +
            " splashEncodeFailures=" + m_encodeFailures +
            " · client splashEventRecv=" + m_eventRecv +
            " splashEventApplied=" + m_eventApplied +
            " splashEventMalformed=" + m_eventMalformed +
            " splashEventDuplicate=" + m_eventDuplicate +
            " splashEventStale=" + m_eventStale +
            " splashEventRateSkipped=" + m_eventRateSkipped +
            " splashEventDirectionErrors=" + m_directionErrors +
            " splashPresentFailures=" + m_presentFailures +
            " splashParticlesCreated=" + m_particlesCreated +
            " splashSoundsPlayed=" + m_soundsPlayed +
            " splashClientSkippedDistance=" + m_clientSkippedDistance +
            " splashMissingAudio=" + m_missingAudio;
    }
}

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
    /// 营火**熄灭火声**同步模块（主机权威，客户端纯表现）。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemCampfireBlockBehavior.cs）：
    ///   · 主机每 5 游戏秒把营火燃料 data -1；降到 0 时格子仍是营火 209（灰烬），
    ///     并在本端 <c>ResizeCampfire</c> 里播 <c>Audio/Sizzles</c>；
    ///   · 这条音效不在 CellChange 流里 → 其它客户端听不到。
    ///
    /// 主机侧：
    ///   · 营火外壳在 base.Update 前后快照 engine Campfires，找出"刚烧尽"的格；
    ///   · 这里编码 <c>CampfireExtinguishEvent</c> 并按**兴趣窗口**投递
    ///     （<see cref="IWindowBroadcastPort"/>，与地形同一份窗口口径）：只有窗口包含该格
    ///     中心的端才需要播这条火声；主机本机的表现由引擎自己执行、不走网络；
    ///   · 地形仍走既有 CellChange，本模块绝不改格 / 不生成实体。
    ///
    /// 客户端侧：
    ///   · 严格解码 → 按 (server_tick, event_id) 排序去重 → 限速（200 ms）→ 只播 Audio/Sizzles。
    ///
    /// 无静态状态；只依赖端口与 Net/CampfireNet.cs 的纯逻辑。
    /// </summary>
    public sealed class CampfireSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly ICampfireEffectPort m_port;
        private readonly CampfireExtinguishOrderer m_orderer = new CampfireExtinguishOrderer();
        private readonly CampfireExtinguishRateLimiter m_rateLimiter =
            new CampfireExtinguishRateLimiter();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IWindowBroadcastPort m_window;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private long m_lastSummaryMs;

        private long m_hostEvents;
        private long m_hostRejected;
        private long m_hostIdle;
        private long m_relayed;
        private long m_relayPeers;
        private long m_sendFailures;
        private long m_encodeFailures;
        // 按兴趣窗口投递的"未送达原因"（区分：没人上报窗口 / 有窗口但都不包含该格中心）。
        private long m_noWindowClients;
        private long m_outOfRange;

        private long m_received;
        private long m_malformed;
        private long m_rejected;
        private long m_duplicates;
        private long m_outOfOrder;
        private long m_rateSkipped;
        private long m_presentFailures;
        private long m_presented;

        public CampfireSyncModule(ICampfireEffectPort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("campfire-sync", "营火熄灭火声同步（主机权威 / 客户端表现）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "effects.campfire" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "campfire: " + Summary() + " · " + m_port.Describe();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 营火熄灭火声同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "营火熄灭火声同步不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_window) || m_window == null)
            {
                // 端口缺失是硬失败：不猜目标、不改回"发给全体"；每次主机事件都会明确计数失败。
                LogProblem("no-window-port", "警告：没有拿到兴趣窗口投递端口（IWindowBroadcastPort）→ " +
                    "主机无法把营火烧尽事件按窗口下发给客户端（发送一律计失败，绝不回退全体广播）");
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到营火烧尽事件");
            }
            m_port.HostCampfireExtinguished += OnHostCampfireExtinguished;
            m_attached = true;
            Log("营火熄灭火声同步模块就绪：主机 = 营火烧尽后按兴趣窗口投递（只有加载了该格的端才收到）；" +
                "客户端 = 解码 / 排序去重 / 限速后只播 Audio/Sizzles（200 ms 限速）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.CampfireExtinguishEvent,
                P.CampfireExtinguishEvent.Parser,
                new DelegateMessageHandler<P.CampfireExtinguishEvent>(OnCampfireExtinguish), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_port.HostCampfireExtinguished -= OnHostCampfireExtinguished;
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_orderer.Reset();
            m_rateLimiter.Reset();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("营火熄灭火声同步模块下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            bool connected = m_transport != null && m_transport.IsConnected;
            if (connected && !m_wasConnected)
            {
                m_orderer.Reset();
                m_rateLimiter.Reset();
                Log("营火熄灭火声：连接已建立 → 事件闸门 / 限速已重置（旧会话的 event_id 不再复用）");
            }
            m_wasConnected = connected;
            long nowMs = (long)(tick.Now * 1000.0);
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ------------------------------------------------------------ 主机：引擎已烧尽

        private void OnHostCampfireExtinguished(CampfireExtinguishData data)
        {
            m_hostEvents++;
            if (m_control?.IsHosting != true)
            {
                // 单机 / 尚未建房：引擎照常烧尽，这是正常状态，不写告警。
                if (m_port.IsClientReplica)
                {
                    m_hostRejected++;
                    LogProblem("host-on-client", "客户端副本上报了营火烧尽事件（" + data.Describe() +
                        "）→ 丢弃（客户端绝不广播）");
                }
                else
                {
                    m_hostIdle++;
                }
                return;
            }
            if (m_window == null)
            {
                m_sendFailures++;
                LogProblem("no-window-port", "主机有营火烧尽事件 " + data.Describe() +
                    "，但没有兴趣窗口投递端口（IWindowBroadcastPort）→ 没有任何客户端能听到" +
                    "（明确计数，绝不回退全体广播）");
                return;
            }
            if (!CampfireExtinguishCodec.TryEncode(in data,
                    out P.CampfireExtinguishEvent message, out string reason))
            {
                m_encodeFailures++;
                LogProblem("encode:" + reason, "营火烧尽事件 " + data.Describe() +
                    " 编码失败（" + reason + "）→ 本条不下发（不会静默）");
                return;
            }
            // 坐标字段确认：CampfireExtinguishData.X/Y/Z 是**格坐标**（Net/CampfireNet.cs:313-337，
            // 且 Point3 Cell => new Point3(X, Y, Z)），不是世界坐标 → 投递位置取格中心
            // = 格坐标 + (0.5, 0.5, 0.5)（引擎 CellCenter 惯用法，与 ClientWindowView.Contains
            // 的区块换算 Floor(world / 16) 同一口径）。
            var cellCenter = new Vector3(data.X + 0.5f, data.Y + 0.5f, data.Z + 0.5f);
            int peers = m_window.BroadcastToPlayersAt(MessageType.CampfireExtinguishEvent,
                message.ToByteArray(), cellCenter, 0, out int candidates);
            if (peers <= 0)
            {
                if (candidates <= 0)
                {
                    m_noWindowClients++;
                    LogProblem("no-window-clients", "主机有营火烧尽事件 " + data.Describe() +
                        "，此刻没有任何在线玩家端上报兴趣窗口 → 没有下发目标" +
                        "（计数 campfireSoundNoWindowClients，绝不回退全体广播）");
                }
                else
                {
                    m_outOfRange++;
                    LogProblem("out-of-range", "主机有营火烧尽事件 " + data.Describe() +
                        "，有 " + candidates + " 个端上报了兴趣窗口但都不包含该格中心 → 入队 0" +
                        "（计数 campfireSoundOutOfRange，绝不回退全体广播）");
                }
                return;
            }
            m_relayed++;
            m_relayPeers += peers;
        }

        // ------------------------------------------------------------ 客户端：只做声音

        private void OnCampfireExtinguish(MessageContext context, P.CampfireExtinguishEvent message)
        {
            m_received++;
            if (m_control?.IsHosting == true)
            {
                m_rejected++;
                LogProblem("event-on-host", "主机收到了 CampfireExtinguishEvent（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            if (!CampfireExtinguishCodec.TryDecode(message, out CampfireExtinguishData data,
                    out string reason))
            {
                m_malformed++;
                LogProblem("event-malformed:" + reason, "CampfireExtinguishEvent 解码失败（" +
                    reason + "，clientId=" + context.ClientId + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAccept(in data, out string orderReason))
            {
                if (orderReason == "duplicate")
                {
                    m_duplicates++;
                    LogProblem("event-duplicate", "重复的 CampfireExtinguishEvent " + data.Describe() +
                        " → 只播一次（deduped）");
                }
                else
                {
                    m_outOfOrder++;
                    LogProblem("event-stale", "过期/越序的 CampfireExtinguishEvent " + data.Describe() +
                        "（last tick=" + m_orderer.LastTick + " id=" +
                        m_orderer.LastEventId + "）→ 丢弃");
                }
                return;
            }
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (!m_rateLimiter.TryAccept(nowMs))
            {
                m_rateSkipped++;
                LogProblem("event-rate", "CampfireExtinguishEvent " + data.Describe() +
                    " 到达过快（限速 " + CampfireSoundPolicy.MinClientIntervalMs +
                    " ms/条）→ 本条不播（计数 campfireSoundRateSkipped）");
                return;
            }
            if (!m_port.Present(in data, out string presentReason))
            {
                m_presentFailures++;
                LogProblem("event-present-failed:" + presentReason, "营火熄灭火声播放失败 " +
                    data.Describe() + "（" + presentReason + "）→ 本条没有声音（明确计数，不静默）");
                return;
            }
            m_presented++;
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

        private string Summary() =>
            "campfireSoundRelayed=" + m_relayed +
            " relayPeers=" + m_relayPeers +
            " hostEvents=" + m_hostEvents +
            " hostIdle=" + m_hostIdle +
            " hostRejected=" + m_hostRejected +
            " sendFailed=" + m_sendFailures +
            " noWindowClients=" + m_noWindowClients +
            " outOfRange=" + m_outOfRange +
            " encodeFailed=" + m_encodeFailures +
            " · campfireSoundRecv=" + m_received +
            " presented=" + m_presented +
            " malformed=" + m_malformed +
            " rejected=" + m_rejected +
            " duplicate=" + m_duplicates +
            " stale=" + m_outOfOrder +
            " rateSkipped=" + m_rateSkipped +
            " presentFailed=" + m_presentFailures;
    }
}

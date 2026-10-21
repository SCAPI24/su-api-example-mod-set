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
    /// 投射物命中反馈模块（主机权威 → 客户端纯表现）。
    ///
    /// 引擎事实：
    ///   · 伤害数字只由 <c>ComponentMiner.AttackBody</c> 创建（ComponentMiner.cs:506-526），
    ///     投射物命中生物也走它（SubsystemProjectiles.cs:221-240）；
    ///   · 客户端副本**不跑** <c>SubsystemProjectiles.Update</c>，所以永远不会有这个数字。
    ///
    /// 主机侧：
    ///   · 命中捕获窗口在 <c>SuSubsystemProjectiles.Update</c> 里（引擎适配层实现）；
    ///   · 这里把命中编码成 <c>ProjectileHitEvent</c>，**只发给兴趣窗口覆盖命中点的玩家端**
    ///     （世界分区：客户端只载入自己那一圈区块，远处命中不该占用它的带宽、也不该在它的
    ///     世界里凭空冒数字）；读不到窗口 → 不发并计数，绝不"广播给所有人再让客户端自己丢"。
    ///
    /// 客户端侧：
    ///   · 严格解码 → 按 (server_tick, event_id) 排序去重 → 交给引擎适配层；
    ///   · 适配层再按本地可见范围 + 区块已载入双重过滤后建粒子。
    ///
    /// 无静态状态；只依赖端口与 Net/ProjectileHitNet.cs 的纯逻辑。
    /// </summary>
    public sealed class ProjectileHitModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IProjectileHitPort m_port;
        private readonly IClientInterestWindowPort m_interest;
        private readonly ProjectileHitOrderer m_orderer = new ProjectileHitOrderer();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<int> m_clients = new HashSet<int>();
        private readonly List<int> m_clientList = new List<int>();

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private long m_lastSummaryMs;

        private long m_hostHits;
        private long m_hostRejected;
        private long m_encoded;
        private long m_encodeFailures;
        private long m_relayed;
        private long m_relayPeers;
        private long m_skippedNoWindow;
        private long m_skippedOutsideWindow;
        private long m_noRecipients;
        private long m_sendFailures;

        private long m_received;
        private long m_malformed;
        private long m_rejected;
        private long m_duplicates;
        private long m_outOfOrder;
        private long m_presentFailures;
        private long m_presented;

        public ProjectileHitModule(IProjectileHitPort port, IClientInterestWindowPort interest)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_interest = interest ?? throw new ArgumentNullException(nameof(interest));
            m_descriptor = new ModuleDescriptor("projectile-hit",
                "投射物命中伤害数字（主机按兴趣窗口中继 / 客户端只飘字）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "effects.projectile-hit" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "投射物命中：" + Summary() + " · " + m_port.Describe();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 投射物命中反馈不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "投射物命中反馈不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到 ProjectileHitEvent");
            }
            m_transport.ClientJoined += OnClientJoined;
            m_transport.ClientLeft += OnClientLeft;
            m_port.HostProjectileHit += OnHostProjectileHit;
            m_attached = true;
            Log("投射物命中反馈模块就绪：主机 = 命中后只发给兴趣窗口覆盖命中点的玩家端；" +
                "客户端 = 解码 / 排序去重 / 可见范围与区块载入双重过滤后建伤害数字");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.ProjectileHitEvent,
                PE.ProjectileHitEvent.Parser,
                new DelegateMessageHandler<PE.ProjectileHitEvent>(OnProjectileHit), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_port.HostProjectileHit -= OnHostProjectileHit;
            // 下线后不再需要引擎做每帧命中捕获（否则只会累计"没有监听者"）。
            m_port.WantHostCapture = false;
            if (m_transport != null)
            {
                m_transport.ClientJoined -= OnClientJoined;
                m_transport.ClientLeft -= OnClientLeft;
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_orderer.Reset();
            m_clients.Clear();
            m_clientList.Clear();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("投射物命中反馈模块下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            bool connected = m_transport != null && m_transport.IsConnected;
            if (connected && !m_wasConnected)
            {
                m_orderer.Reset();
                Log("投射物命中：连接已建立 → 事件闸门已重置（旧会话的 event_id 不再复用）");
            }
            m_wasConnected = connected;
            // 只有"主机 + 至少一个玩家端"才需要引擎做每帧命中捕获（单机 / 主机独自时省掉射线）。
            m_port.WantHostCapture = connected && m_control?.IsHosting == true && m_clients.Count > 0;
            long nowMs = (long)(tick.Now * 1000.0);
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ------------------------------------------------------------ 主机：按兴趣窗口逐个发

        private void OnHostProjectileHit(ProjectileHitData data)
        {
            m_hostHits++;
            if (m_control?.IsHosting != true)
            {
                m_hostRejected++;
                LogProblem("host-on-client", "客户端副本上报了投射物命中（" + data.Describe() +
                    "）→ 丢弃（客户端绝不广播）");
                return;
            }
            if (m_clients.Count == 0)
            {
                m_noRecipients++;
                return;
            }
            if (!ProjectileHitCodec.TryEncode(in data, out PE.ProjectileHitEvent message,
                    out string reason))
            {
                m_encodeFailures++;
                LogProblem("encode", "投射物命中 " + data.Describe() +
                    " 编码失败（" + reason + "）→ 本条不下发（不会静默）");
                return;
            }
            m_encoded++;
            byte[] payload = message.ToByteArray();
            RebuildClientList();
            int sent = 0;
            for (int i = 0; i < m_clientList.Count; i++)
            {
                int clientId = m_clientList[i];
                if (!m_interest.TryGetClientRange(clientId, out int centerChunkX,
                        out int centerChunkZ, out int radiusChunks))
                {
                    // 世界分区：窗口未知就不知道该不该发 —— 不发，并明确计数（绝不改成广播）。
                    m_skippedNoWindow++;
                    LogProblem("no-window:" + clientId, "玩家端 clientId=" + clientId +
                        " 还没上报兴趣窗口 → 命中 " + data.Describe() +
                        " 不发给它（累计 projectileHitNoWindow=" + m_skippedNoWindow + "）");
                    continue;
                }
                if (!ProjectileHitViewPolicy.IsCellInInterestWindow(data.Position, centerChunkX,
                        centerChunkZ, radiusChunks))
                {
                    m_skippedOutsideWindow++;
                    continue;
                }
                if (!m_transport.TrySendTo(clientId, MessageType.ProjectileHitEvent, payload))
                {
                    m_sendFailures++;
                    LogProblem("send:" + clientId, "投射物命中 " + data.Describe() +
                        " 发给 clientId=" + clientId + " 失败（连接不可达 / 背压失败）");
                    continue;
                }
                sent++;
            }
            if (sent > 0)
            {
                m_relayed++;
                m_relayPeers += sent;
            }
        }

        // ------------------------------------------------------------ 客户端：只建伤害数字

        private void OnProjectileHit(MessageContext context, PE.ProjectileHitEvent message)
        {
            m_received++;
            if (m_control?.IsHosting == true)
            {
                m_rejected++;
                LogProblem("event-on-host", "主机收到了 ProjectileHitEvent（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            if (!ProjectileHitCodec.TryDecode(message, out ProjectileHitData data, out string reason))
            {
                m_malformed++;
                LogProblem("event-malformed", "ProjectileHitEvent 解码失败（" + reason +
                    "，clientId=" + context.ClientId + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAccept(in data, out string orderReason))
            {
                if (orderReason == "duplicate")
                {
                    m_duplicates++;
                    LogProblem("event-duplicate", "重复的 ProjectileHitEvent " + data.Describe() +
                        " → 只表现一次（deduped）");
                }
                else
                {
                    m_outOfOrder++;
                    LogProblem("event-stale", "过期/越序的 ProjectileHitEvent " + data.Describe() +
                        "（last tick=" + m_orderer.LastTick + " id=" +
                        m_orderer.LastEventId + "）→ 丢弃");
                }
                return;
            }
            if (!m_port.Present(in data, out string presentReason))
            {
                m_presentFailures++;
                LogProblem("event-present-failed", "投射物命中表现失败 " +
                    data.Describe() + "（" + presentReason + "）→ 本条没有伤害数字（明确计数，不静默）");
                return;
            }
            m_presented++;
        }

        // ------------------------------------------------------------ 玩家端集合

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (clientId <= 0) return;
            m_clients.Add(clientId);
        }

        private void OnClientLeft(int clientId, string reason)
        {
            m_clients.Remove(clientId);
            m_clientList.Remove(clientId);
        }

        private void RebuildClientList()
        {
            m_clientList.Clear();
            foreach (int clientId in m_clients)
                if (clientId > 0) m_clientList.Add(clientId);
            m_clientList.Sort();
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
            "hitRelayed=" + m_relayed +
            " relayPeers=" + m_relayPeers +
            " hostHits=" + m_hostHits +
            " hostRejected=" + m_hostRejected +
            " noWindow=" + m_skippedNoWindow +
            " outsideWindow=" + m_skippedOutsideWindow +
            " noRecipients=" + m_noRecipients +
            " sendFailed=" + m_sendFailures +
            " encodeFailed=" + m_encodeFailures +
            " · hitRecv=" + m_received +
            " presented=" + m_presented +
            " malformed=" + m_malformed +
            " rejected=" + m_rejected +
            " duplicate=" + m_duplicates +
            " stale=" + m_outOfOrder +
            " presentFailed=" + m_presentFailures;
    }
}

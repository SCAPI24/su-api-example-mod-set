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
    /// 烧尽表现同步模块（**主机权威，客户端纯表现**）。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:227-256）：
    /// 主机每 5 游戏秒消费 <c>m_toBurnAway</c>，把方块替换成空气，并在本端生成
    /// <c>BurntDebrisParticleSystem</c>（最近视点 &lt; 24 m，最多 15 个）与
    /// <c>Audio/Sizzles</c>（&lt; 16 m，最多 4 次）。这些表现不在 CellChange 流里。
    ///
    /// 主机侧：
    ///   · 火外壳（Engine/ClientReplicaSubsystems.cs 的 SuSubsystemFireBlockBehavior）在一次
    ///     烧尽批次结束时上报**已经处理完**的格（已去重、已封顶）；
    ///   · 这里编码一条 <c>BurnAwayEffect</c> 广播给所有玩家端（含本机；本机由引擎自己表现）；
    ///   · 地形仍走既有主机同步（CellChange），本模块绝不改格 / 不生成实体。
    ///
    /// 客户端侧：
    ///   · 收到后先严格解码，再按 (server_tick, event_id) 排序、按 event_id 去重；
    ///   · 再走限速（约 10 批/秒），最后交给引擎适配层只做表现：
    ///     每格 &lt; 24 m 且本批前 15 个 → 碎屑；&lt; 16 m 且本格 sizzle 且本批前 4 个 → 嘶嘶声；
    ///     超出距离 / 上限的格明确计数。
    ///
    /// 无静态状态；只依赖端口与 Net/EffectsNet.cs 的纯逻辑。
    /// </summary>
    public sealed class EffectsSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IBurnAwayEffectPort m_port;
        private readonly BurnAwayEffectOrderer m_orderer = new BurnAwayEffectOrderer();
        private readonly BurnAwayEffectRateLimiter m_rateLimiter = new BurnAwayEffectRateLimiter();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private long m_lastSummaryMs;

        // ---------------------------------------------------------------- 主机计数（状态行可审计）
        private long m_hostBatches;
        private long m_hostBatchesRejected;
        private long m_hostBatchesIdle;
        private long m_hostCells;
        private long m_effectsSent;
        private long m_effectsSentCells;
        private long m_effectsSentPeers;
        private long m_sendFailures;
        private long m_noRecipients;

        // ---------------------------------------------------------------- 客户端计数
        private long m_effectsReceived;
        private long m_effectsMalformed;
        private long m_effectsRejected;
        private long m_effectsDuplicate;
        private long m_effectsOutOfOrder;
        private long m_rateSkipped;
        private long m_presentFailures;
        private long m_effectsPresented;
        private long m_cellsReceived;
        private long m_cellsPresented;
        private long m_debris;
        private long m_sounds;
        private long m_skippedDistance;
        private long m_skippedCap;
        private long m_missingAudio;

        public EffectsSyncModule(IBurnAwayEffectPort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("effects-sync", "烧尽表现同步（主机权威 / 客户端表现）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "effects.burnaway" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "烧尽表现：" + Summary() + " · " + m_port.Describe();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 烧尽表现同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "烧尽表现同步不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_broadcast) || m_broadcast == null)
            {
                Log("警告：没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把烧尽批次广播给客户端");
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到烧尽批次");
            }
            m_port.HostBurnAwayBatch += OnHostBurnAwayBatch;
            m_attached = true;
            Log("烧尽表现同步模块就绪：主机 = 引擎烧尽批次后广播；客户端 = 解码 / 排序 / 去重 / 限速后" +
                "只做碎屑粒子与嘶嘶声（距离 24 m / 16 m，每批 15 / 4）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.BurnAwayEffect, P.BurnAwayEffect.Parser,
                new DelegateMessageHandler<P.BurnAwayEffect>(OnBurnAwayEffect), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_port.HostBurnAwayBatch -= OnHostBurnAwayBatch;
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_orderer.Reset();
            m_rateLimiter.Reset();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("烧尽表现同步模块下线（" + reason + "）：" + summary);
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
                Log("烧尽表现：连接已建立 → 事件闸门 / 限速已重置（旧会话的 event_id 不再复用）");
            }
            m_wasConnected = connected;
            long nowMs = (long)(tick.Now * 1000.0);
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ------------------------------------------------------------ 主机：引擎已处理的烧尽批次

        private void OnHostBurnAwayBatch(BurnAwayEffectData batch)
        {
            m_hostBatches++;
            if (batch.Cells == null || batch.Cells.Length == 0) return;
            m_hostCells += batch.Cells.Length;
            if (m_control?.IsHosting != true)
            {
                // 单机 / 尚未建房：火外壳照常上报，这是正常状态，不写告警（否则每次烧尽都刷一行）。
                // 客户端副本上收到上报才是方向错误（客户端绝不广播）。
                if (m_port.IsClientReplica)
                {
                    m_hostBatchesRejected++;
                    LogProblem("batch-on-client", "客户端副本上的火外壳上报了烧尽批次（" +
                        batch.Describe() + "）→ 丢弃（客户端绝不广播）");
                }
                else
                {
                    m_hostBatchesIdle++;
                }
                return;
            }
            if (m_broadcast == null)
            {
                m_sendFailures++;
                LogProblem("no-broadcast-port", "主机有烧尽批次 " + batch.Describe() +
                    "，但没有广播端口 → 没有客户端能看到（明确计数，不静默）");
                return;
            }
            if (m_broadcast.PlayerPeerCount <= 0)
            {
                m_noRecipients++;
                LogProblem("no-peers", "主机有烧尽批次 " + batch.Describe() +
                    "，此刻没有在线玩家端 → 没有下发目标（计数 burnEffectNoRecipients）");
                return;
            }
            if (!BurnAwayEffectCodec.TryEncode(in batch, out P.BurnAwayEffect message,
                    out string reason))
            {
                m_sendFailures++;
                LogProblem("encode:" + reason, "烧尽批次 " + batch.Describe() +
                    " 编码失败（" + reason + "）→ 本批不下发（不会静默）");
                return;
            }
            int peers = m_broadcast.BroadcastToPlayers(MessageType.BurnAwayEffect,
                message.ToByteArray());
            if (peers <= 0)
            {
                m_sendFailures++;
                LogProblem("send-failed", "烧尽批次 " + batch.Describe() + " 广播失败（" +
                    m_broadcast.PlayerPeerCount + " 个玩家端，入队 0）");
                return;
            }
            m_effectsSent++;
            m_effectsSentCells += batch.Cells.Length;
            m_effectsSentPeers += peers;
        }

        // ------------------------------------------------------------ 客户端：表现

        private void OnBurnAwayEffect(MessageContext context, P.BurnAwayEffect message)
        {
            m_effectsReceived++;
            if (m_control?.IsHosting == true)
            {
                m_effectsRejected++;
                LogProblem("effect-on-host", "主机收到了 BurnAwayEffect（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            if (!BurnAwayEffectCodec.TryDecode(message, out BurnAwayEffectData effect,
                    out string reason))
            {
                m_effectsMalformed++;
                LogProblem("effect-malformed:" + reason, "BurnAwayEffect 解码失败（" + reason +
                    "，clientId=" + context.ClientId + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAccept(in effect, out string orderReason))
            {
                if (orderReason == "duplicate")
                {
                    m_effectsDuplicate++;
                    LogProblem("effect-duplicate", "重复的 BurnAwayEffect " + effect.Describe() +
                        " → 只表现一次（deduped）");
                }
                else
                {
                    m_effectsOutOfOrder++;
                    LogProblem("effect-stale", "过期/越序的 BurnAwayEffect " + effect.Describe() +
                        "（last tick=" + m_orderer.LastTick + " id=" +
                        m_orderer.LastEventId + "）→ 丢弃");
                }
                return;
            }
            m_cellsReceived += effect.Cells.Length;
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (!m_rateLimiter.TryAccept(nowMs))
            {
                m_rateSkipped++;
                LogProblem("effect-rate", "BurnAwayEffect " + effect.Describe() +
                    " 到达过快（限速 " + BurnAwayEffectPolicy.MinBatchIntervalMs +
                    " ms/批）→ 本批不表现（计数 burnEffectRateSkipped）");
                return;
            }
            if (!m_port.Present(in effect, out BurnAwayPresentationResult result,
                    out string presentReason))
            {
                m_presentFailures++;
                LogProblem("effect-present-failed:" + presentReason, "烧尽表现失败 " +
                    effect.Describe() + "（" + presentReason + "）→ 本次没有画面（明确计数，不静默）");
                return;
            }
            m_effectsPresented++;
            m_cellsPresented += result.Presented;
            m_debris += result.Debris;
            m_sounds += result.Sounds;
            m_skippedDistance += result.SkippedDistance;
            m_skippedCap += result.SkippedCap;
            m_missingAudio += result.MissingAudio;
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

        private string Summary() => "host burnEffectSent=" + m_effectsSent +
            " burnEffectCells=" + m_effectsSentCells +
            " burnEffectPeers=" + m_effectsSentPeers +
            " burnEffectSendFailed=" + m_sendFailures +
            " burnEffectNoRecipients=" + m_noRecipients +
            " burnEffectHostBatches=" + m_hostBatches +
            " burnEffectHostCells=" + m_hostCells +
            " burnEffectHostRejected=" + m_hostBatchesRejected +
            " burnEffectHostIdle=" + m_hostBatchesIdle +
            " · client burnEffectRecv=" + m_effectsReceived +
            " burnEffectCells=" + m_cellsReceived +
            " burnEffectPresented=" + m_cellsPresented +
            " burnEffectDebris=" + m_debris +
            " burnEffectSounds=" + m_sounds +
            " burnEffectSkippedDistance=" + m_skippedDistance +
            " burnEffectSkippedCap=" + m_skippedCap +
            " burnEffectMalformed=" + m_effectsMalformed +
            " burnEffectRejected=" + m_effectsRejected +
            " burnEffectDuplicate=" + m_effectsDuplicate +
            " burnEffectStale=" + m_effectsOutOfOrder +
            " burnEffectRateSkipped=" + m_rateSkipped +
            " burnEffectPresentFailed=" + m_presentFailures +
            " burnEffectMissingAudio=" + m_missingAudio +
            " burnEffectBatches=" + m_effectsPresented;
    }
}

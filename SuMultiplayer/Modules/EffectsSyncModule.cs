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
    ///   · 这里按**每个端的兴趣窗口**逐端裁剪这批格（<see cref="IWindowBroadcastPort"/> 与地形
    ///     同一份窗口口径）：一批格里只有落在该端窗口内的才需要表现，对每个端过滤后用现有
    ///     编码器重新编码一份再点对点下发；主机本机的表现由引擎自己执行、不走网络；
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
        // 逐端过滤的复用容器（烧尽批次约 5 游戏秒一次，不在热路径；避免每批给 GC 添垃圾）。
        private readonly List<ClientWindowView> m_clientWindows = new List<ClientWindowView>();
        private readonly List<BurnAwayCellData> m_filteredCells =
            new List<BurnAwayCellData>(BurnAwayEffectPolicy.MaxCellsPerMessage);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IWindowBroadcastPort m_window;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private long m_lastSummaryMs;

        // ---------------------------------------------------------------- 主机计数（状态行可审计）
        private long m_hostBatches;
        private long m_hostBatchesRejected;
        private long m_hostBatchesIdle;
        private long m_hostCells;
        // 批级：至少有一个端成功入队的批次数；cells 按端累加（同一格发给 N 个端计 N）。
        private long m_effectsSent;
        private long m_effectsSentCells;
        private long m_sendFailures;
        // 逐端窗口过滤 / 投递明细（区分：没人上报窗口 / 格都在某端窗口外 / 单端编码或发送失败）。
        private long m_clientsSent;
        private long m_noWindowClients;
        private long m_windowedCellsDropped;
        private long m_clientBatchesOutside;
        private long m_perClientEncodeFailures;
        private long m_perClientSendFailures;

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
            if (!context.TryGetPort(out m_window) || m_window == null)
            {
                // 端口缺失是硬失败：不猜目标、不改回"发给全体"；每次主机批次都会明确计数失败。
                LogProblem("no-window-port", "警告：没有拿到兴趣窗口投递端口（IWindowBroadcastPort）→ " +
                    "主机无法把烧尽批次按窗口下发给客户端（发送一律计失败，绝不回退全体广播）");
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
            Log("烧尽表现同步模块就绪：主机 = 引擎烧尽批次后按各端兴趣窗口逐端裁剪 / 重新编码后点对点下发；" +
                "客户端 = 解码 / 排序 / 去重 / 限速后只做碎屑粒子与嘶嘶声（距离 24 m / 16 m，每批 15 / 4）");
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
            if (m_window == null)
            {
                m_sendFailures++;
                LogProblem("no-window-port", "主机有烧尽批次 " + batch.Describe() +
                    "，但没有兴趣窗口投递端口（IWindowBroadcastPort）→ 没有任何客户端能收到" +
                    "（明确计数，绝不回退全体广播）");
                return;
            }
            // 整批先做一次编码校验：保留改动前的严格语义 —— 非法批次（坏坐标 / 批内重复）整批
            // 不下发并明确失败，绝不靠逐端窗口过滤把非法格"顺手丢掉"；合法批次随后再按各端
            // 窗口过滤、各自重新编码一份子集。
            if (!BurnAwayEffectCodec.TryEncode(in batch, out _, out string reason))
            {
                m_sendFailures++;
                LogProblem("encode:" + reason, "烧尽批次 " + batch.Describe() +
                    " 编码失败（" + reason + "）→ 本批不下发（不会静默）");
                return;
            }
            // 逐端过滤：客户端只加载自己周围那片地形（兴趣窗口与地形同步同一口径），
            // 一批格里只有落在该端窗口内的格才需要表现；每个端重新编码一份自己的子集。
            int windowedClients = m_window.CaptureClientWindows(m_clientWindows);
            if (windowedClients <= 0)
            {
                m_noWindowClients++;
                LogProblem("no-window-clients", "主机有烧尽批次 " + batch.Describe() +
                    "，此刻没有任何在线玩家端上报兴趣窗口 → 没有下发目标" +
                    "（计数 burnEffectNoWindowClients，绝不回退全体广播）");
                return;
            }
            int deliveredClients = 0;
            int deliveredCells = 0;
            for (int i = 0; i < m_clientWindows.Count; i++)
            {
                ClientWindowView windowView = m_clientWindows[i];
                m_filteredCells.Clear();
                for (int c = 0; c < batch.Cells.Length; c++)
                {
                    BurnAwayCellData cell = batch.Cells[c];
                    // 格坐标 → 世界坐标：引擎惯用格中心 = 格坐标 + (0.5, 0.5, 0.5)，
                    // 与 ClientWindowView.Contains 的区块换算（Floor(world / 16)）同一口径。
                    var cellCenter = new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
                    if (windowView.Contains(cellCenter, 0)) m_filteredCells.Add(cell);
                    else m_windowedCellsDropped++;
                }
                if (m_filteredCells.Count == 0)
                {
                    m_clientBatchesOutside++;
                    LogProblem("client-batch-outside", "烧尽批次 " + batch.Describe() +
                        " 的全部格子都在客户端 " + windowView.ClientId + " 的兴趣窗口外 → " +
                        "这个端本批不下发（逐端过滤；计数 burnEffectClientBatchesOutside）");
                    continue;
                }
                var filtered = new BurnAwayEffectData(batch.ClientId, batch.ServerTick,
                    batch.EventId, m_filteredCells.ToArray());
                if (!BurnAwayEffectCodec.TryEncode(in filtered, out P.BurnAwayEffect message,
                        out string encodeReason))
                {
                    m_perClientEncodeFailures++;
                    LogProblem("client-encode:" + encodeReason, "烧尽批次 " + batch.Describe() +
                        " 给客户端 " + windowView.ClientId + " 的窗口过滤批次（" +
                        m_filteredCells.Count + " 格）编码失败（" + encodeReason +
                        "）→ 这个端本批不下发（明确计数，不静默）");
                    continue;
                }
                if (!m_window.SendToClient(windowView.ClientId, MessageType.BurnAwayEffect,
                        message.ToByteArray()))
                {
                    m_perClientSendFailures++;
                    LogProblem("client-send-failed", "烧尽批次 " + batch.Describe() +
                        " 发给客户端 " + windowView.ClientId + " 失败（连接已不在线 / 已不是玩家端 / " +
                        "背压拒绝；过滤后 " + m_filteredCells.Count +
                        " 格）→ 这个端本批没收到（明确计数，不静默）");
                    continue;
                }
                m_clientsSent++;
                deliveredClients++;
                deliveredCells += m_filteredCells.Count;
            }
            if (deliveredClients <= 0)
            {
                // 每个端都有自己的原因分支（全在窗口外 / 编码失败 / 发送失败，已计数并限流日志）；
                // 这里不猜目标、不回退全体广播。
                return;
            }
            m_effectsSent++;
            m_effectsSentCells += deliveredCells;
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
            " burnEffectClientsSent=" + m_clientsSent +
            " burnEffectSendFailed=" + m_sendFailures +
            " burnEffectNoWindowClients=" + m_noWindowClients +
            " burnEffectClientBatchesOutside=" + m_clientBatchesOutside +
            " burnEffectWindowedCellsDropped=" + m_windowedCellsDropped +
            " burnEffectPerClientSendFailed=" + m_perClientSendFailures +
            " burnEffectPerClientEncodeFailed=" + m_perClientEncodeFailures +
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

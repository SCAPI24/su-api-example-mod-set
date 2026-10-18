using System;
using System.Collections.Generic;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;
using SuMultiplayer.Transfer;

using SuMultiplayer.Protocol;
using PTransfer = SuMultiplayer.Protocol.Transfer;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 世界传输模块：主机把当前世界发给刚加入的客户端；客户端收到后**导入成自己的世界**。
    /// 这就是旧实现"点击房间 → 下载地图 → 进入"里的下载那一段。
    ///
    /// 解耦方式：
    ///   · 连接不属于本模块 —— 通过 <see cref="ITransportPort"/> 端口拿（地形模块提供）；
    ///   · "谁是主机"不属于本模块 —— 通过 <see cref="ITransportControlPort"/> 问（玩家按了 CR）；
    ///   · 世界的导出/导入不属于本模块 —— 通过 <see cref="IWorldExportSource"/> /
    ///     <see cref="IWorldImporter"/> 注入（引擎适配器实现）；
    ///   · **切屏不属于本模块** —— 导入完成后只往 <see cref="IWorldReadyPort"/> 发布"下载世界就绪"，
    ///     由 Play 屏（加入房间的那块界面）切 GameLoading，和旧实现 SuPlayScreen.Play 一致。
    ///
    /// 多客户端不变量（本版修复的实机缺陷：两个客户端加入同一主机，只有第一个收到世界，
    /// 第二个永远停在 Play 屏的 BusyDialog 上）：
    ///   · 主机侧的发送状态**按 clientId 分开** —— 每个加入一条 <see cref="SendState"/> + 一条发送队列，
    ///     绝不是"本次会话已经发过一次就再也不发"的全局标志；
    ///   · 每个**非重连**加入恰好一次 WorldBegin + 全部分片 + 一次 WorldEnd；重连（alreadyHasWorld=true）零字节；
    ///   · 一条 TCP 通道上同一时刻只推进一条传输：新加入**排队等待**，既不取消/复位前一条传输，
    ///     也不会被前一条永远堵住（前一条完成 / 失败 / 对端离开后立刻接着发）；
    ///   · 分片**定向发送**（<see cref="ITransportPort.TrySendTo"/>）：绝不发给"最后加入的那个"，
    ///     否则先加入的客户端的分片会被后加入的连接抢走；
    ///   · 世界按**会话**导出一次，多个客户端复用同一份快照（收到的字节完全一致）；
    ///     会话变了立刻丢弃重导，绝不把旧会话的世界发出去。
    ///
    /// 演示页标记：XFR05。
    /// </summary>
    public sealed class WorldTransferModule : IMultiplayerModule, ISuMPMessageModule
    {
        /// <summary>世界导出重试间隔（主机还没进世界时属正常等待，不刷日志）。</summary>
        private const int ExportRetryMs = 2000;
        /// <summary>导出失败日志限流：同一个原因最多 10s 一行（失败绝不被静默吞掉）。</summary>
        private const int ExportFailureLogMs = 10000;
        /// <summary>发送进度日志间隔。</summary>
        private const int SendProgressLogMs = 3000;
        /// <summary>
        /// 一个客户端多久没有任何分片进展就算卡死并让位。
        /// 没有这个判定时，一个"连接还在但永远不消费"的客户端会把发送队列永远堵住。
        /// </summary>
        private const int SendStallMs = 30000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly SyncConfig m_config;
        private readonly IWorldExportSource m_exporter;
        private readonly IWorldImporter m_importer;
        private readonly ReadyPortImpl m_ready = new ReadyPortImpl();
        private readonly IDownloadedWorldStore m_store;
        private readonly CleanupPortImpl m_cleanup;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private IMessageRegistryPort m_messages;
        private ITransportControlPort m_control;
        private IWorldSessionPort m_session;
        private WorldTransferHost m_host;
        private WorldTransferClient m_client;
        /// <summary>主机侧发送通道：把共享通道绑定到"这一次发送的目标 clientId"。</summary>
        private readonly TargetedChannel m_sendChannel;
        private long m_lastEndMs;
        /// <summary>已安装过一次（避免重复导入同一个世界）。</summary>
        private bool InstalledOnce;
        /// <summary>本次会话是否已经开始（收到 Begin / 发出世界 / 安装过世界）。</summary>
        private bool m_sessionActive;

        /// <summary>主机侧：每个玩家端"这一次加入"的整图发送状态（按 clientId 分开，绝不共用）。</summary>
        private readonly Dictionary<int, SendState> m_sends = new Dictionary<int, SendState>();
        /// <summary>主机侧：等待发送的 clientId（FIFO；同一时刻只发一个客户端）。</summary>
        private readonly List<int> m_sendQueue = new List<int>();
        /// <summary>主机侧：此刻正在发送的 clientId（0 = 没有）。</summary>
        private int m_activeSendClientId;
        private long m_lastExportAttemptMs = long.MinValue / 2;
        private long m_lastExportFailureLogMs = long.MinValue / 2;
        private string m_lastExportFailure = string.Empty;
        private byte[] m_cachedPak;
        private string m_cachedWorldName = string.Empty;
        private string m_cachedDirectory = string.Empty;
        /// <summary>缓存 pak 对应的导出基线序号（与缓存 pak 同生共死；每个加入方看到的清单都一致）。</summary>
        private long m_cachedBaseSequence;
        /// <summary>缓存 pak 所属的主机会话标识（会话变了 ⇒ 旧缓存作废，绝不带着旧会话发出去）。</summary>
        private string m_cachedSessionId = string.Empty;
        /// <summary>缓存 pak 对应的随机数种子 / 状态表（与 pak 同生共死：导出那一刻抓一次）。</summary>
        private int m_cachedRandomSeed;
        private Dictionary<string, long> m_cachedRandomStates =
            new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>随机数状态端口（可为空：离线自检 / 未装配时明确写一行，不假装同步）。</summary>
        private IRandomStatePort m_rng;
        private bool m_rngMissingLogged;

        public WorldTransferModule(SyncConfig config, IWorldExportSource exporter, IWorldImporter importer,
            IDownloadedWorldStore store)
        {
            m_config = config ?? new SyncConfig();
            m_exporter = exporter;
            m_importer = importer;
            m_store = store;
            m_cleanup = new CleanupPortImpl(this);
            m_sendChannel = new TargetedChannel(this);
            m_descriptor = new ModuleDescriptor("world-transfer", "世界传输")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "world.transfer" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        /// <summary>
        /// 世界传输的入站消息（WorldBegin/Chunk/End）。注册表是唯一分发入口：
        /// 本模块不再自己订阅 FrameArrived，第三方与内置消息走同一条路径。
        /// </summary>
        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.WorldBegin, PTransfer.WorldBegin.Parser,
                new DelegateMessageHandler<PTransfer.WorldBegin>(OnWorldBegin), ModuleId);
            registry.Register((int)MessageType.WorldChunk, PTransfer.WorldChunk.Parser,
                new DelegateMessageHandler<PTransfer.WorldChunk>(OnWorldChunk), ModuleId);
            registry.Register((int)MessageType.WorldEnd, PTransfer.WorldEnd.Parser,
                new DelegateMessageHandler<PTransfer.WorldEnd>(OnWorldEnd), ModuleId);
            registry.Register((int)MessageType.WorldMissing, PTransfer.WorldMissing.Parser,
                new DelegateMessageHandler<PTransfer.WorldMissing>(OnWorldMissing), ModuleId);
        }

        /// <summary>
        /// 客户端报告缺片 → 主机按索引重发。只在"这个客户端正是当前传输目标"时重发；
        /// 其它连接（或多个客户端排队时）明确忽略并限流写一行，绝不把分片串给别的连接。
        /// </summary>
        private void OnWorldMissing(MessageContext context, PTransfer.WorldMissing message)
        {
            if (m_control?.IsHosting != true || m_host == null) return;
            if (context.ClientId != m_activeSendClientId)
            {
                LogWarning("clientId=" + context.ClientId + " 请求缺片，但它不是当前传输目标（目标=" +
                    m_activeSendClientId + "）→ 忽略（不把分片发给别的连接）");
                return;
            }
            int sent = m_host.Resend(message.Indexes);
            if (sent > 0)
                Log("clientId=" + context.ClientId + " 请求缺片 " + message.Indexes.Count +
                    " 片 → 已重发 " + sent + " 片");
        }

        private void OnWorldBegin(MessageContext context, PTransfer.WorldBegin message)
        {
            // 主机自己不收世界包（只有客户端会收到 World*）。
            if (m_client == null || m_control?.IsHosting == true) return;
            m_sessionActive = true;
            if (m_client.HandleBegin(message))
                Log("收到世界信息：" + m_client.WorldName + " · " + m_client.Total + " 片，开始接收");
        }

        private void OnWorldChunk(MessageContext context, PTransfer.WorldChunk message)
        {
            if (m_client == null || m_control?.IsHosting == true) return;
            m_client.HandleChunk(message);
        }

        private void OnWorldEnd(MessageContext context, PTransfer.WorldEnd message)
        {
            if (m_client == null || m_control?.IsHosting == true) return;
            m_lastEndMs = Environment.TickCount64;
            if (m_client.Complete) return;          // 已收齐：重复 End 直接忽略
            if (m_client.HandleEnd(message, m_lastEndMs))
            {
                Log("世界接收完成：" + m_client.Received + "/" + m_client.Total + " 片 · " +
                    (m_client.Assembled?.Length ?? 0) + " B");
                Install();
            }
            else
            {
                // 明确失败（缺片就请主机重发），不静默
                Log("世界接收未完成：" + m_client.Error + " → 已请求缺片重发");
            }
        }

        public int InstalledFiles { get; private set; }
        public long InstalledBytes { get; private set; }
        public string InstalledDirectory { get; private set; }
        /// <summary>最近一次导入的世界所归属的主机会话标识（空 = 旧主机 / 清单没带）。</summary>
        public string InstalledSessionId { get; private set; } = string.Empty;
        /// <summary>最近一次导入的世界清单里的导出基线序号。</summary>
        public long InstalledBaseSequence { get; private set; }
        /// <summary>组合根把它交给 Play 屏：下载来的世界一就绪就在那里进入。</summary>
        public IWorldReadyPort ReadyPort => m_ready;
        /// <summary>组合根把它交给 Play 屏：回到世界列表时清掉上一次下载的世界。</summary>
        public IDownloadedWorldPort DownloadedWorldPort => m_cleanup;

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out ITransportPort port))
            {
                // 端口没有就是依赖缺失：明确失败，不假装能传世界
                Log("没有拿到传输端口（transport 能力缺失）→ 世界传输不启用");
                return;
            }
            context.TryGetPort(out m_control);
            // 随机数同步端口（可选）：拿不到 = 世界清单不带 RNG 状态，主机明确写一行。
            context.TryGetPort(out m_rng);
            if (m_rng == null && !m_rngMissingLogged)
            {
                m_rngMissingLogged = true;
                LogWarning("没有拿到随机数状态端口（IRandomStatePort）→ 世界清单不带随机数种子/状态，" +
                    "客户端无法与主机 RNG 对齐（其余地形同步不受影响）");
            }
            m_transport = port;
            // 主机会话标识 / 改动水位是**世界清单的必填语义**（客户端靠它判断"同一会话"）：
            // 拿不到就不能假装能正确重连 —— 明确失败，让组合根把端口注册上。
            if (!context.TryGetPort(out IWorldSessionPort session) || session == null)
            {
                Log("错误：没有拿到会话标识端口（IWorldSessionPort）→ 世界传输不启用" +
                    "（否则下发的世界没有会话标识，每次加入都会被当成首次加入、整图重下）");
                return;
            }
            m_session = session;
            if (context.TryGetPort(out IMessageRegistryPort messages) && messages != null)
            {
                m_messages = messages;
                messages.Registry.RegisterModule(this);
            }
            else
            {
                // 没有注册表 = 入站消息无处分发。明确写一行（不假装能收世界）；
                // 主机侧发送不依赖注册表，仍可工作。
                LogWarning("没有拿到 IMessageRegistryPort（消息注册表）→ 世界传输收不到 WorldBegin/Chunk/End" +
                    "（主机侧发送不受影响；请组合根注册 IMessageRegistryPort）");
            }
            m_transport.ClientJoined += HandleClientJoined;
            // 客户端离开必须清掉**它自己**的发送状态：否则队列会停在一个永远发不出去的目标上，
            // 后面的客户端（以及它自己重连/再加入）都排不到。
            m_transport.ClientLeft += HandleClientLeft;
            // 两个方向的对象都建好：本端到底是主机还是客户端，由玩家操作决定（CR / 点房间行），
            // 不是由启动配置决定，所以这里不预先二选一。
            m_host = new WorldTransferHost(m_sendChannel);
            m_client = new WorldTransferClient(port);
            Log("世界传输就绪（通道 " + port.StateText + "；主机/客户端身份等玩家操作）");
        }

        /// <summary>
        /// 只记下"有客户端加入"。**不在这里导出** —— 真实顺序是：
        /// 客户端在菜单加入 → 主机随后创建房间/进入世界 → 那时才有东西可发。
        /// 所以发送条件 = 已建房 + 该客户端在队列里 + 主机有已加载世界（见 Tick）。
        ///
        /// 这里是本次握手的**唯一决策**（每个握手一行日志），而且只动**这个 clientId** 的状态：
        ///   · rejoin（Hello alreadyHasWorld=true）⇒ 不变量①：对它绝不导出/发送整图，
        ///     只取消它自己残留的待发状态；**绝不动别的客户端正在进行的传输**；
        ///   · 非 rejoin ⇒ 不变量②：这一次加入进发送队列，恰好发送一次整图。
        /// </summary>
        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (rejoin)
            {
                // 对端明说"我已经有过这个世界"（断线重连）：**绝不能**再发一遍整图 ——
                // 主机的每一次发送都会重新导出并从头传，已经进了世界的客户端收到 WorldBegin
                // 会重新下载并导入一遍（世界被重装、正在玩的状态被打断），而它其实什么都不缺。
                // 只取消**它自己**的待发状态：旧实现这里调 AbortPendingSend 把整个会话的传输都丢了，
                // 另一个客户端正在收的世界会因此断在半路（多客户端缺陷之一）。
                string cancelled = CancelSendFor(clientId);
                Log("握手（role=host）：客户端 " + name + " clientId=" + clientId +
                    " 重连（Hello alreadyHasWorld=true：它正在游玩这个世界）" +
                    " · 世界传输：不导出、不发送整图" +
                    (cancelled.Length > 0
                        ? "（已取消它自己的整图发送：" + cancelled + "；其它客户端的传输不受影响）"
                        : string.Empty) +
                    " · 只恢复地形/位置同步");
                return;
            }
            SendState existing = FindSend(clientId);
            if (existing != null && !existing.Aborted && existing.Started && !existing.EndSent)
            {
                // 同一条连接上重复握手，而这一条传输正在发：保持现状（不重来、不重复排队）。
                Log("握手（role=host）：客户端 " + name + " clientId=" + clientId +
                    " 重复握手（首次加入）→ 保持正在进行的整图发送（" + ProgressText() + "）");
                return;
            }
            if (existing != null)
            {
                // 上一次已经发完 / 已失败：这一次握手是**又一次加入** ⇒ 重新发一份（每次非重连加入恰好一次）。
                RemoveSend(clientId, "同一客户端再次握手，重新开始整图发送");
            }
            var send = new SendState { ClientId = clientId, ClientName = name };
            m_sends[clientId] = send;
            m_sendQueue.Add(clientId);
            bool waiting = m_activeSendClientId > 0 && m_activeSendClientId != clientId;
            string queueNote = string.Empty;
            if (waiting)
            {
                queueNote = "（当前正在向 clientId=" + m_activeSendClientId + " 发送世界：" + ProgressText() +
                            "。它排在第 " + m_sendQueue.Count + " 位，等那一条发完立即开始 —— " +
                            "绝不取消/复位前一条传输，前一条也不会把它挡掉）";
            }
            Log("握手（role=host）：客户端 " + name + " clientId=" + clientId +
                " 首次加入（Hello alreadyHasWorld=false）" +
                " · 世界传输：将在主机进入世界后**恰好发送一次**整图" + queueNote);
        }

        /// <summary>
        /// 一个玩家端的连接已经不可达：清掉**它自己**的发送状态（排队中的 / 正在发的），
        /// 队列里后面的客户端立刻能开始。不清理就会让队列停在一个永远发不出去的目标上。
        /// </summary>
        private void HandleClientLeft(int clientId, string reason)
        {
            if (m_control?.IsHosting != true) return;
            string detail = CancelSendFor(clientId);
            if (detail.Length == 0) return;   // 它没有待发的世界（已发完 / 从未进发送队列）
            Log("世界发送已中止 clientId=" + clientId + "（" + reason + "）：" + detail +
                " · 队列里还有 " + m_sendQueue.Count + " 个客户端，继续发送");
        }

        /// <summary>
        /// 主机侧发送泵：同一时刻只推进**一条**传输（一条通道），这条结束（完成/失败）后
        /// 立刻接着发队列里的下一个。
        /// 多客户端缺陷的根因就在这里：旧实现用 m_sendStarted/m_beginSent/m_endSent 这些**会话级**标志，
        /// 给第一个客户端发完后就永远 return，第二个客户端的加入再也开不了头。
        /// </summary>
        private void PumpHostSend(long nowMs)
        {
            if (m_host == null) return;
            if (m_activeSendClientId > 0)
            {
                SendState active = FindSend(m_activeSendClientId);
                if (active == null || active.Aborted)
                {
                    // 状态被别处清掉了（重连 / 离开）：丢弃已准备的整图，让位给队列里的下一个。
                    m_activeSendClientId = 0;
                    m_host.AbortPendingSend();
                }
                else
                {
                    ContinueSend(active, nowMs);
                    if (m_activeSendClientId == active.ClientId) return;   // 这一条还在发
                }
            }
            TryStartNextSend(nowMs);
        }

        /// <summary>
        /// 从队列里取下一个客户端开始一条新传输。
        /// 导出按**会话**只做一次（多个客户端复用同一份快照 → 收到的字节完全一致）。
        /// </summary>
        private void TryStartNextSend(long nowMs)
        {
            while (m_sendQueue.Count > 0)
            {
                int clientId = m_sendQueue[0];
                SendState send = FindSend(clientId);
                if (send == null || send.Aborted)
                {
                    m_sendQueue.RemoveAt(0);
                    continue;
                }
                if (!CanSendToClient(clientId))
                {
                    FailSend(send, "排队等待期间通道已不可达（对端已断开）");
                    continue;
                }
                if (!EnsureWorldExport(nowMs))
                {
                    // 主机还没进世界（正常等待）/ 导出失败（已限流写清原因）：保持排队，下一 tick 再试。
                    return;
                }
                if (!m_host.PrepareFromPak(m_cachedPak, m_cachedWorldName, m_cachedDirectory,
                        m_cachedSessionId, m_cachedBaseSequence, 48 * 1024,
                        m_cachedRandomSeed, m_cachedRandomStates))
                {
                    FailSend(send, "准备世界分片失败（导出的 pak 为空）");
                    continue;
                }
                m_sendQueue.RemoveAt(0);
                m_host.Restart();
                // 定向：这一条传输从 Begin 到 End 只发给这个 clientId（绝不发给"最后加入的那个"）。
                m_sendChannel.TargetClientId = clientId;
                m_activeSendClientId = clientId;
                send.Started = true;
                send.LastProgressMs = nowMs;
                send.LastProgressLogMs = nowMs;
                send.LastSentChunks = 0;
                send.BeginSent = m_host.SendBegin();
                m_sessionActive = true;
                Log("开始发送世界 clientId=" + clientId + "（" + send.ClientName + "）：" +
                    m_host.ChunkCount + " 片 · " + m_cachedPak.Length + " B · 会话 " +
                    SessionIdentity.Short(m_cachedSessionId) + " · 基线序号 " + m_cachedBaseSequence +
                    " · 复用本次会话导出的同一份世界快照" +
                    (m_sendQueue.Count > 0 ? " · 队列里还有 " + m_sendQueue.Count + " 个客户端等待" : string.Empty));
                if (!send.BeginSent)
                {
                    LogWarning("clientId=" + clientId + " 的 WorldBegin 还没进队：" +
                        "下一 tick 用同一个 transferId 重试（绝不重新导出，也绝不改投别的客户端）");
                }
                return;
            }
        }

        /// <summary>
        /// 保证本次会话有一份可发的世界快照：按会话缓存（多个客户端复用同一份 → 收到的字节完全一致）；
        /// 会话变了就丢弃重导（绝不把旧会话的世界发出去）；主机还没进世界时每 2s 重试，不刷日志。
        /// </summary>
        private bool EnsureWorldExport(long nowMs)
        {
            // 世界清单必须带**本次房间的会话标识**：空标识意味着客户端无法判断"是不是同一会话"
            // （之后每次加入都会整图重下）。这不是可降级项 —— 明确失败，不发送没有会话标签的世界。
            string sessionId = m_session?.SessionId ?? string.Empty;
            if (string.IsNullOrEmpty(sessionId))
            {
                LogExportFailure(nowMs, "主机会话标识为空（IWorldSessionPort 没有可用会话）→ 本次不发送世界");
                return false;
            }
            if (m_cachedPak != null && !string.Equals(m_cachedSessionId, sessionId, StringComparison.Ordinal))
            {
                // 房间会话变了：旧的 pak 属于旧会话，绝不能当成本次的世界发出去。
                Log("主机会话已变化（缓存世界属于 " + SessionIdentity.Short(m_cachedSessionId) +
                    "，当前 " + SessionIdentity.Short(sessionId) + "）→ 丢弃缓存的世界，重新导出");
                ClearCachedWorld();
            }
            if (m_cachedPak != null) return true;
            if (nowMs - m_lastExportAttemptMs < ExportRetryMs) return false;
            m_lastExportAttemptMs = nowMs;
            // 导出基线序号必须**在导出开始前**取：导出期间产生的改动要么已在快照里（重放幂等），
            // 要么会被当成"导出后改动"在下一次追帧里补上。取在导出之后则可能漏掉这段。
            long baseSequence = m_session.CurrentChangeSequence;
            if (!m_exporter.TryExport(out byte[] pak, out string worldName, out string directory, out string reason))
            {
                if (reason != null && reason.Contains("尚未加载世界")) return false;   // 正常等待，不写日志
                LogExportFailure(nowMs, "世界导出失败：" + reason);
                return false;
            }
            m_cachedPak = pak;
            m_cachedWorldName = worldName;
            m_cachedDirectory = directory;
            m_cachedBaseSequence = baseSequence;
            m_cachedSessionId = sessionId;
            // 随机数状态必须和**这一份** pak 同生共死：导出成功后立刻抓一次（不是发送时才抓，
            // 否则多个客户端之间 RNG 状态会跟着主机时间漂移，两个副本的初始状态就不一致了）。
            if (m_rng != null)
            {
                m_cachedRandomSeed = m_rng.SessionSeed;
                m_cachedRandomStates = m_rng.CaptureHostStates();
            }
            else
            {
                m_cachedRandomSeed = 0;
                m_cachedRandomStates = new Dictionary<string, long>(StringComparer.Ordinal);
            }
            m_lastExportFailure = string.Empty;
            Log("世界已导出：" + worldName + " · " + pak.Length + " B（目录 " + directory +
                " · 会话 " + SessionIdentity.Short(sessionId) + " · 基线序号 " + baseSequence +
                " · RNG " + RandomStateCodec.Describe(m_cachedRandomSeed, m_cachedRandomStates) + "）");
            return true;
        }

        /// <summary>推进当前这一条传输：Begin 重试 → 分片 → 一次 End；全程只发给这一个 clientId。</summary>
        private void ContinueSend(SendState send, long nowMs)
        {
            if (!CanSendToClient(send.ClientId))
            {
                FailSend(send, "通道已不可达（WorldBegin/分片发不出去）");
                return;
            }
            if (!send.BeginSent)
            {
                send.BeginSent = m_host.SendBegin();
                if (send.BeginSent) Log("世界发送已开始 clientId=" + send.ClientId + "（WorldBegin 已入队）");
            }
            // 分片必须跟在**已送达的** Begin 后面：Begin 没进队就先不发分片（下一 tick 重试同一 transferId）。
            if (send.BeginSent) m_host.Tick();
            // 世界**只发一次**：全部分片发完才发一次 End，之后停手
            //（之前每个 tick 都发 End → 客户端反复重装同一张地图）
            if (!send.EndSent && m_host.ChunkCount > 0 && !m_host.InProgress &&
                m_host.ChunksSent >= m_host.ChunkCount)
            {
                send.EndSent = m_host.SendEnd(true, "ok");
                if (send.EndSent)
                {
                    CompleteSend(send);
                    return;
                }
            }
            int total = m_host.ChunkCount;
            int sent = (int)m_host.ChunksSent;   // PrepareFromPak 每次都清零 ⇒ 就是"本次传输已发"
            if (sent != send.LastSentChunks)
            {
                send.LastSentChunks = sent;
                send.LastProgressMs = nowMs;
            }
            else if (nowMs - send.LastProgressMs >= SendStallMs)
            {
                FailSend(send, "已经 " + (SendStallMs / 1000) +
                    "s 没有任何分片进展（对端不消费 / 链路卡死），已发 " + sent + "/" + total + " 片");
                return;
            }
            if (nowMs - send.LastProgressLogMs >= SendProgressLogMs && total > 0 && !send.EndSent)
            {
                send.LastProgressLogMs = nowMs;
                Log("世界发送进度 clientId=" + send.ClientId + "（" + send.ClientName + "）· " +
                    sent + "/" + total + " 片 · " + m_host.BytesSent + "/" + (m_cachedPak?.Length ?? 0) + " B");
            }
        }

        /// <summary>一条传输成功结束（End 已入队）：清掉它自己的状态，队列里下一个立刻开始。</summary>
        private void CompleteSend(SendState send)
        {
            int chunks = (int)m_host.ChunksSent;
            int total = m_host.ChunkCount;
            m_sends.Remove(send.ClientId);
            m_sendQueue.Remove(send.ClientId);
            m_activeSendClientId = 0;
            // pak 用完即弃（缓存 m_cachedPak 仍在：下一个客户端复用同一份快照，不重新导出）
            m_host.AbortPendingSend();
            Log("世界发送完成 clientId=" + send.ClientId + "（" + send.ClientName + "）：" + chunks + "/" + total +
                " 片 · 本次会话导出 " + (m_cachedPak?.Length ?? 0) + " B · " +
                (m_sendQueue.Count > 0
                    ? "队列里还有 " + m_sendQueue.Count + " 个客户端，立即接着发"
                    : "队列已空（这条传输不会重发）"));
        }

        /// <summary>一条传输**明确失败**（不可达 / 卡死 / pak 准备失败）：写清原因，让位给队列里的下一个。</summary>
        private void FailSend(SendState send, string reason)
        {
            long sent = m_host?.ChunksSent ?? 0;
            int total = m_host?.ChunkCount ?? 0;
            bool wasActive = m_activeSendClientId == send.ClientId;
            send.Aborted = true;
            m_sends.Remove(send.ClientId);
            m_sendQueue.Remove(send.ClientId);
            if (wasActive)
            {
                m_activeSendClientId = 0;
                m_host?.AbortPendingSend();
            }
            LogWarning("世界发送失败 clientId=" + send.ClientId + "（" + send.ClientName + "）：" + reason +
                " · 已发 " + sent + "/" + total + " 片" +
                (m_sendQueue.Count > 0
                    ? " · 队列里还有 " + m_sendQueue.Count + " 个客户端，继续发送"
                    : " · 该客户端需要重新加入（或重连）才能再收到世界"));
        }

        /// <summary>
        /// 取消**某一个**客户端的世界发送（重连 / 离开 / 再次握手）：从队列摘掉、清掉它自己的状态；
        /// 若它正是当前发送目标，丢弃已准备的整图并把通道让给队列里的下一个客户端。
        /// 返回非空字符串 = 确实取消了什么（进度 / 队列位置，写日志用）。
        /// </summary>
        private string CancelSendFor(int clientId)
        {
            bool wasActive = m_activeSendClientId == clientId;
            string detail = string.Empty;
            SendState send = FindSend(clientId);
            int queuedAt = m_sendQueue.IndexOf(clientId);
            if (send == null && !wasActive && queuedAt < 0) return detail;   // 它没有待发的世界
            if (wasActive) detail = "正在发送 " + ProgressText();
            else if (queuedAt >= 0) detail = "还在发送队列里（第 " + (queuedAt + 1) + " 位，尚未开始）";
            else detail = "已开始但未完成";
            if (send != null) send.Aborted = true;
            m_sends.Remove(clientId);
            m_sendQueue.Remove(clientId);
            if (wasActive)
            {
                m_activeSendClientId = 0;
                m_host?.AbortPendingSend();
            }
            return detail;
        }

        /// <summary>清掉某个客户端的发送状态（只动它自己，不影响别的客户端）。</summary>
        private void RemoveSend(int clientId, string why)
        {
            m_sends.Remove(clientId);
            m_sendQueue.Remove(clientId);
            if (m_activeSendClientId == clientId)
            {
                m_activeSendClientId = 0;
                m_host?.AbortPendingSend();
                Log("世界发送已取消 clientId=" + clientId + "（" + why + "）");
            }
        }

        /// <summary>清空全部发送状态（会话复位 / 下线）：当前传输丢弃，队列作废。返回清掉的客户端数。</summary>
        private int ClearAllSends()
        {
            int count = m_sends.Count;
            if (m_activeSendClientId > 0 && !m_sends.ContainsKey(m_activeSendClientId)) count++;
            m_sends.Clear();
            m_sendQueue.Clear();
            m_activeSendClientId = 0;
            m_host?.AbortPendingSend();
            return count;
        }

        private SendState FindSend(int clientId) =>
            m_sends.TryGetValue(clientId, out SendState send) ? send : null;

        /// <summary>
        /// 这个玩家端此刻能不能收世界分片（**定向**判定，绝不用"最后加入的那个"顶替）。
        /// 短暂背压（发送队列满）算"可发"：下一帧重试即可，不能把慢客户端误判成离开。
        /// </summary>
        private bool CanSendToClient(int clientId) =>
            clientId > 0 && m_transport != null && m_transport.IsClientSendable(clientId);

        private string ProgressText()
        {
            if (m_host == null || m_host.ChunkCount <= 0) return "尚未开始";
            return m_host.ChunksSent + "/" + m_host.ChunkCount + " 片";
        }

        private string PendingClientsText()
        {
            if (m_sendQueue.Count == 0) return "(无)";
            var parts = new List<string>();
            for (int i = 0; i < m_sendQueue.Count; i++) parts.Add("clientId=" + m_sendQueue[i]);
            return string.Join(",", parts);
        }

        /// <summary>导出失败：同一个原因最多 10s 一行，并列出正在等待的客户端（明确失败，不静默）。</summary>
        private void LogExportFailure(long nowMs, string message)
        {
            if (string.Equals(message, m_lastExportFailure, StringComparison.Ordinal) &&
                nowMs - m_lastExportFailureLogMs < ExportFailureLogMs) return;
            m_lastExportFailure = message;
            m_lastExportFailureLogMs = nowMs;
            LogWarning(message + " · 等待中的客户端：" + PendingClientsText());
        }

        private void Install()
        {
            if (m_importer == null || m_client?.Assembled == null || InstalledOnce) return;
            if (m_importer.TryImport(m_client.Assembled, out string directory, out string reason))
            {
                InstalledOnce = true;
                InstalledDirectory = directory;
                InstalledBytes = m_client.Assembled.Length;
                InstalledSessionId = m_client.SessionId ?? string.Empty;
                InstalledBaseSequence = m_client.BaseSequence;
                m_sessionActive = true;
                if (string.IsNullOrEmpty(InstalledSessionId))
                {
                    // 旧主机 / 清单缺字段：世界是导入了，但**明确**说明之后每次加入都会整图重下，
                    // 绝不拿一个默认会话去骗重连判定。
                    LogWarning("世界清单没有主机会话标识（旧主机构建？）→ 世界已导入，但之后每次加入都按首次加入处理（整图重下）");
                }
                // 与世界**登记在一起**：之后 Hello 的 alreadyHasWorld 判定要用它（会话 + 基线序号）。
                if (m_store == null)
                {
                    LogWarning("没有下载世界存储适配器 → 无法把会话标识与下载世界登记在一起；" +
                        "之后每次加入都会按首次加入处理（整图重下）");
                }
                else
                {
                    m_store.RegisterSession(directory, InstalledSessionId, InstalledBaseSequence);
                }
                Log("世界已导入：" + directory + "（pak " + InstalledBytes + " B · 会话 " +
                    SessionIdentity.Short(InstalledSessionId) + " · 基线序号 " + InstalledBaseSequence + "）");
                // 通知地形侧：这个世界的"上次应用序号"从 baseSequence 起算；会话标识记为最近一次 HelloAck 的主机会话。
                m_session?.OnClientWorldInstalled(directory, InstalledSessionId, InstalledBaseSequence);
                // 随机数状态：登记给引擎侧，由它**每个 Project 只应用一次**（世界导入完成、进入世界后）。
                if (m_rng != null)
                {
                    if (m_client.RandomSeed != 0 && m_client.RandomStates != null &&
                        m_client.RandomStates.Count > 0)
                    {
                        m_rng.ApplyClientStates(m_client.RandomSeed, m_client.RandomStates);
                        Log("随机数状态已登记：seed=" + m_client.RandomSeed + " · 字段 " +
                            m_client.RandomStates.Count + "（进入世界后每个 Project 应用一次）");
                    }
                    else
                    {
                        LogWarning("世界清单没有随机数状态（seed=" + m_client.RandomSeed + "，字段=" +
                            (m_client.RandomStates?.Count ?? 0) + "）→ 客户端不应用 RNG 状态" +
                            (string.IsNullOrEmpty(m_client.RandomStateProblem)
                                ? "（旧主机 / 未装配随机数端口）"
                                : "（解码原因：" + m_client.RandomStateProblem + "）"));
                    }
                }
                // 落盘完成 → 交给 Play 屏 / 游戏内刷新入口进入（切屏由界面负责，本模块不碰 ScreensManager）。
                m_ready.Publish(directory, m_client.WorldName);
                Log("已通知界面：下载来的世界就绪（" + m_client.WorldName + " → " + directory + "）");
            }
            else
            {
                Log("世界导入失败：" + reason);
            }
        }

        private void ClearCachedWorld()
        {
            m_cachedPak = null;
            m_cachedWorldName = string.Empty;
            m_cachedDirectory = string.Empty;
            m_cachedBaseSequence = 0L;
            m_cachedSessionId = string.Empty;
            m_cachedRandomSeed = 0;
            m_cachedRandomStates = new Dictionary<string, long>(StringComparer.Ordinal);
        }

        public void Tick(in ModuleTickContext tick)
        {
            long nowMs = (long)(tick.Now * 1000.0);
            // 通道被关掉（玩家离开房间 / 回到世界列表）→ 本模块的会话状态必须一起复位：
            // 否则再点一次房间行会被当成"已经收过了"，世界永远不会重新下载；
            // 主机侧不清缓存则会把上一次导出的世界当成这次的发出去。
            if (m_sessionActive && m_control?.IsChannelRunning != true)
                ResetSession("通道已关闭（已离开房间）");
            if (m_host != null && m_control?.IsHosting == true)
            {
                // 一个 tick 推进当前这一条传输；它结束（完成/失败/对端离开）后立刻接着发队列里的下一个。
                PumpHostSend(nowMs);
            }
            if (m_client != null && m_control?.IsHosting != true)
            {
                m_client.RequestMissing(nowMs);
                // End 到了但缺片 → 重传补齐后再组装一次
                if (m_client.Total > 0 && m_client.Received >= m_client.Total && m_lastEndMs > 0 &&
                    !m_client.Complete && !InstalledOnce)
                {
                    // 缺片补齐后再组装一次（End 已经到过）
                    m_client.HandleEnd(new PTransfer.WorldEnd { TransferId = 0, Ok = true }, nowMs);
                    if (m_client.Complete) Install();
                }
            }
        }

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                m_transport.ClientJoined -= HandleClientJoined;
                m_transport.ClientLeft -= HandleClientLeft;
            }
            if (m_messages != null)
            {
                m_messages.Registry.UnregisterOwner(ModuleId);
                m_messages = null;
            }
            ClearAllSends();
            m_transport = null;
            m_control = null;
            m_host = null;
            m_client = null;
            Log("世界传输下线（" + reason + "）");
        }

        public string StatusLine()
        {
            if (m_control?.IsHosting == true)
                return "world-transfer: host cached=" + (m_cachedPak != null ? m_cachedPak.Length + "B" : "none") +
                       " session=" + SessionIdentity.Short(m_cachedSessionId) +
                       " base=" + m_cachedBaseSequence +
                       " active=" + (m_activeSendClientId > 0
                           ? "clientId=" + m_activeSendClientId + " " +
                             (m_host?.ChunksSent ?? 0) + "/" + (m_host?.ChunkCount ?? 0)
                           : "none") +
                       " queued=" + m_sendQueue.Count;
            if (m_client != null)
                return "world-transfer: client " + m_client.Received + "/" + m_client.Total +
                       " session=" + SessionIdentity.Short(m_client.SessionId) +
                       " base=" + m_client.BaseSequence +
                       (InstalledOnce ? " 已导入 " + InstalledDirectory
                           : (m_client.Complete ? " 已收齐待安装" : " 接收中"));
            return "world-transfer: 未启用";
        }

        /// <summary>会话复位：清空"这一次加入"的全部痕迹（主机的世界缓存也要清，否则发的是上一次的）。</summary>
        private void ResetSession(string why)
        {
            m_sessionActive = false;
            InstalledOnce = false;
            m_lastEndMs = 0;
            int aborted = ClearAllSends();
            // 会话复位 = 上一次准备的整图作废：新会话必须重新导出，绝不能把旧 pak 当成这一次的发出去。
            ClearCachedWorld();
            InstalledDirectory = null;
            InstalledSessionId = string.Empty;
            InstalledBaseSequence = 0L;
            Log("世界传输会话已复位（" + why + "）：下次加入会重新导出/接收世界" +
                (aborted > 0 ? "（已取消 " + aborted + " 个客户端的待发世界）" : string.Empty));
        }

        /// <summary>
        /// 清掉上一次下载来的世界（旧实现 CleanupDownloadedWorldsIfIdle 的契约）。
        /// 只在**没有联机世界里**被调用（Play 屏进入时），正在接收世界时不清理。
        /// </summary>
        private bool TryCleanupDownloadedWorlds(out string note)
        {
            note = null;
            if (m_store == null)
            {
                note = "没有世界存储适配器，无法清理下载来的世界";
                return false;
            }
            if (m_client != null && m_client.Active && !m_client.Complete)
            {
                note = "正在接收世界，暂不清理下载来的世界";
                return false;
            }
            IReadOnlyList<string> registered = m_store.Registered;
            if (registered == null || registered.Count == 0)
            {
                note = "没有需要清理的下载世界";
                return false;
            }
            var failed = new List<string>();
            int deleted = 0;
            for (int i = 0; i < registered.Count; i++)
            {
                string directory = registered[i];
                if (m_store.Delete(directory, out string reason)) deleted++;
                else
                {
                    failed.Add(directory);
                    Log("删除下载来的世界失败：" + directory + " → " + reason);
                }
            }
            m_store.SaveRegistry(failed);
            note = "已清理 " + deleted + " 个下载来的世界（失败 " + failed.Count + " 个）";
            Log(note);
            return deleted > 0;
        }

        /// <summary>
        /// 这个世界目录是不是本机下载来的联机世界（登记表由世界导入时写入）。
        /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:RegisterDownloadedWorld
        /// </summary>
        private bool IsDownloadedWorld(string worldDirectory)
        {
            if (string.IsNullOrEmpty(worldDirectory)) return false;
            if (m_store == null)
            {
                Log("没有世界存储适配器，无法判断当前世界是不是下载来的联机世界（不会建主机角色）");
                return false;
            }
            IReadOnlyList<string> registered = m_store.Registered;
            for (int i = 0; i < registered.Count; i++)
            {
                if (string.Equals(registered[i], worldDirectory, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 一个玩家端"这一次加入"的整图发送状态。
        /// **按 clientId 分开**：第二个客户端的加入有自己的 Started/BeginSent/EndSent，
        /// 不会被第一个客户端"已经发过"的状态挡住（这正是实机缺陷的根因）。
        /// </summary>
        private sealed class SendState
        {
            public int ClientId;
            public string ClientName = string.Empty;
            /// <summary>已经 Prepare + 发过 Begin（这一条传输已经开始）。</summary>
            public bool Started;
            public bool BeginSent;
            public bool EndSent;
            /// <summary>明确取消：一个字节都不再发（重连 / 离开 / 失败 / 会话复位）。</summary>
            public bool Aborted;
            public long LastProgressMs;
            public long LastProgressLogMs;
            public int LastSentChunks;
        }

        /// <summary>
        /// 把共享通道**绑定到这一次发送的目标客户端**：世界传输走的是同一条 TCP 通道，
        /// 多个客户端时用默认 TrySend（"最后加入的那个"）会把分片串到别的客户端连接上。
        /// 目标未设置 / 端口不具备定向能力 = **明确失败**（绝不退回默认目标）。
        /// </summary>
        private sealed class TargetedChannel : ITransferChannel
        {
            private readonly WorldTransferModule m_owner;
            public int TargetClientId;

            public TargetedChannel(WorldTransferModule owner) { m_owner = owner; }

            public bool TrySend(MessageType type, byte[] payload)
            {
                if (TargetClientId <= 0)
                {
                    m_owner.LogWarning("世界传输内部错误：发送目标未设置（clientId=0）→ 丢弃本次 " + type +
                        "（绝不发给\"最后加入的那个\"）");
                    return false;
                }
                if (m_owner.m_transport == null) return false;
                return m_owner.m_transport.TrySendTo(TargetClientId, type, payload);
            }

            public bool IsConnected =>
                TargetClientId > 0 && m_owner.m_transport != null &&
                m_owner.m_transport.IsClientSendable(TargetClientId);

            public string StateText => "target=" + TargetClientId;
        }

        /// <summary>清理端口的实现：Play 屏进入世界列表时调用。</summary>
        private sealed class CleanupPortImpl : IDownloadedWorldPort
        {
            private readonly WorldTransferModule m_owner;
            public CleanupPortImpl(WorldTransferModule owner) { m_owner = owner; }
            public bool TryCleanup(out string note) => m_owner.TryCleanupDownloadedWorlds(out note);

            public bool IsDownloadedWorld(string worldDirectory) =>
                m_owner.IsDownloadedWorld(worldDirectory);

            public bool TryGetWorldSession(string worldDirectory, out string sessionId, out long baseSequence)
            {
                sessionId = string.Empty;
                baseSequence = 0L;
                if (m_owner.m_store == null)
                {
                    m_owner.Log("没有世界存储适配器 → 读不到下载世界的会话标识（本次 Hello 按首次加入处理）");
                    return false;
                }
                return m_owner.m_store.TryGetSession(worldDirectory, out sessionId, out baseSequence);
            }

            public void ForgetWorldSession(string worldDirectory)
            {
                if (m_owner.m_store == null)
                {
                    m_owner.LogWarning("没有世界存储适配器 → 无法作废下载世界的会话标识" +
                        "（之后的重连仍会带着旧会话，由主机判定后整图重下）");
                    return;
                }
                m_owner.m_store.ForgetSession(worldDirectory);
                m_owner.Log("已作废下载世界的会话标识：" + worldDirectory +
                    "（之后以首次加入身份重连，等主机下发整图）");
            }
        }

        /// <summary>下载世界就绪的单槽信箱（主线程发布/消费，加锁只是防御性写法）。</summary>
        private sealed class ReadyPortImpl : IWorldReadyPort
        {
            private readonly object m_gate = new object();
            private string m_directory;
            private string m_worldName;

            public void Publish(string directory, string worldName)
            {
                lock (m_gate)
                {
                    m_directory = directory;
                    m_worldName = worldName;
                }
            }

            public bool TryTake(out string directory, out string worldName)
            {
                lock (m_gate)
                {
                    directory = m_directory;
                    worldName = m_worldName;
                    m_directory = null;
                    m_worldName = null;
                    return !string.IsNullOrEmpty(directory);
                }
            }
        }

        private void Log(string message)
        {
            if (m_context != null) m_context.Diagnostics.Info(m_descriptor.Id, message);
            else global::Engine.Log.Information("[SuMP] " + message);
        }

        /// <summary>需要用户看见的显式失败（不静默、也不当普通信息淹没）。</summary>
        private void LogWarning(string message)
        {
            if (m_context != null) m_context.Diagnostics.Warn(m_descriptor.Id, message);
            else global::Engine.Log.Warning("[SuMP] " + message);
        }
    }
}

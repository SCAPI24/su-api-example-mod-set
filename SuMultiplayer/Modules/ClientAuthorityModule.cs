using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;

using SuMultiplayer.Protocol;
using PA = SuMultiplayer.Protocol.Authority;
using PWI = SuMultiplayer.Protocol.WorldInfo;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 客户端权威模块（**新文件**）：把"主机唯一权威 / 客户端是副本"落到协议上。
    ///
    ///   · **主机**：约 2 Hz 广播 <see cref="WorldInfoSnapshot"/>（时间 / 降水 / 雾 / 闪电 /
    ///     WorldSettings / 时间加速）；收到 <c>CellEditRequest</c> 后校验（区块已加载 / 距离 /
    ///     方块合法性 / 只收玩家端）→ 走主机权威路径落地 → 回 <c>CellEditResult</c>；
    ///     落地产生的格变动由既有 <c>CellChange</c> 广播链发给所有客户端。
    ///   · **客户端**：把引擎捕获到的本地写入变成 <c>CellEditRequest</c>（带每连接单调序号）；
    ///     收到拒绝 / 超时就回到主机最后的值（拒绝回执带权威值；超时走强制核对 = 主机回传整块）；
    ///     收到 WorldInfo 就整体覆盖本地时间 / 天气；每个 Project 应用一次主机下发的随机数状态。
    ///
    /// 模块只依赖端口与纯策略层（不引用 Game）：所以 2 Hz 广播 / 编辑裁决 / 回滚的**编解码与策略**
    /// 都能在 P:\tmp\tsverify 里用真实 TCP 跑，不需要启动游戏。
    /// </summary>
    public sealed class ClientAuthorityModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int WorldInfoIntervalMs = 500;
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly ClientEditTracker m_tracker = new ClientEditTracker();
        private readonly ClientWorldInfoReplica m_weatherReplica = new ClientWorldInfoReplica();

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IClientEditPort m_edit;
        private IWorldInfoPort m_worldInfo;
        private IRandomStatePort m_rng;
        private IPeerBroadcastPort m_broadcast;
        private IChunkReverifyPort m_reverify;

        private long m_frame;
        private long m_worldInfoRevision;
        private long m_nextWorldInfoMs;
        private long m_lastSummaryMs;
        private long m_lastProblemMs;
        private long m_worldInfoSent;
        private long m_worldInfoSendFailures;
        private long m_worldInfoReceived;
        private long m_worldInfoMalformed;
        private long m_editSendFailures;
        private long m_editResultsReceived;
        private long m_editRollbacks;
        private long m_editReverifyRequests;
        private long m_editReverifyUnavailable;
        private long m_editCaptureIgnored;
        /// <summary>从捕获入口进来的本地编辑数（= 引擎侧成功读值并抛事件的格数）。</summary>
        private long m_editReceived;
        /// <summary>成功交给传输层的编辑请求数（首次 + 重发）。</summary>
        private long m_editSent;
        private long m_editDroppedHosting;
        private long m_editDroppedNoTransport;
        private long m_editDroppedNotReplica;
        private long m_editDroppedOverflow;
        private long m_editDroppedClientReset;
        private long m_editDroppedDetached;
        private long m_clientResetCount;
        private int m_lastLocalClientId = int.MinValue;
        private bool m_attached;
        private IMessageRegistryPort m_messages;
        private Action<int, string, string, bool> m_clientJoinedHandler;
        private Action<int, string> m_clientLeftHandler;
        private Action<ClientCellEdit> m_localEditHandler;

        public ClientAuthorityModule()
        {
            m_descriptor = new ModuleDescriptor("client-authority", "客户端编辑 / 世界信息")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "client.authority" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.CellEditRequest, PA.CellEditRequest.Parser,
                new DelegateMessageHandler<PA.CellEditRequest>(OnCellEditRequest), ModuleId);
            registry.Register((int)MessageType.CellEditResult, PA.CellEditResult.Parser,
                new DelegateMessageHandler<PA.CellEditResult>(OnCellEditResult), ModuleId);
            registry.Register((int)MessageType.WorldInfo, PWI.WorldInfo.Parser,
                new DelegateMessageHandler<PWI.WorldInfo>(OnWorldInfo), ModuleId);
        }

        private void OnCellEditRequest(MessageContext context, PA.CellEditRequest message)
        {
            if (!m_attached || m_control?.IsHosting != true) return;
            HandleEditRequest(context, message);
        }

        private void OnCellEditResult(MessageContext context, PA.CellEditResult message)
        {
            if (!m_attached || m_control?.IsHosting == true) return;
            HandleEditResult(message);
        }

        private void OnWorldInfo(MessageContext context, PWI.WorldInfo message)
        {
            if (!m_attached || m_control?.IsHosting == true) return;
            HandleWorldInfo(message);
        }

        /// <summary>组合根周期打印的一行（可审计：世界信息 / 编辑请求 / 回滚 / 副本闸门）。</summary>
        public string StatusLine() => "client-authority: " + Summary();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out ITransportPort transport) || transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 客户端权威模块不启用");
                return;
            }
            context.TryGetPort(out m_control);
            context.TryGetPort(out m_edit);
            context.TryGetPort(out m_worldInfo);
            context.TryGetPort(out m_rng);
            context.TryGetPort(out m_broadcast);
            context.TryGetPort(out m_reverify);
            m_transport = transport;
            if (context.TryGetPort(out IMessageRegistryPort messages) && messages != null)
            {
                m_messages = messages;
                messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到 IMessageRegistryPort → 编辑请求 / 世界信息收不到" +
                    "（请组合根注册 IMessageRegistryPort）");
            }
            m_clientJoinedHandler = HandleClientJoined;
            m_transport.ClientJoined += m_clientJoinedHandler;
            m_clientLeftHandler = HandleClientLeft;
            m_transport.ClientLeft += m_clientLeftHandler;
            if (m_edit != null)
            {
                m_localEditHandler = HandleLocalEdit;
                m_edit.LocalEditCaptured += m_localEditHandler;
            }
            m_tracker.ResendIntervalMs = 250;
            m_tracker.TimeoutMs = 5000;
            m_attached = true;
            Log("客户端权威模块就绪：" + DescribePorts());
        }

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                if (m_clientJoinedHandler != null) m_transport.ClientJoined -= m_clientJoinedHandler;
                if (m_clientLeftHandler != null) m_transport.ClientLeft -= m_clientLeftHandler;
            }
            if (m_messages != null)
            {
                m_messages.Registry.UnregisterOwner(ModuleId);
                m_messages = null;
            }
            if (m_edit != null && m_localEditHandler != null)
                m_edit.LocalEditCaptured -= m_localEditHandler;
            m_editDroppedDetached += m_tracker.PendingCount;
            m_tracker.Clear();
            m_attached = false;
            Log("客户端权威模块下线（" + reason + "）：" + Summary());
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_frame = tick.FrameIndex;
            bool hosting = m_control?.IsHosting == true;
            bool connected = m_transport != null && m_transport.IsConnected;
            long nowMs = (long)(tick.Now * 1000.0);
            if (hosting)
            {
                if (connected && nowMs >= m_nextWorldInfoMs)
                {
                    m_nextWorldInfoMs = nowMs + WorldInfoIntervalMs;
                    BroadcastWorldInfo();
                }
            }
            else if (connected)
            {
                PumpClientEdits(nowMs);
                m_rng?.PumpApply();
                // 连接换了（重连 = 新连接，序号从 1 重新开始）：旧连接的在途请求全部作废。
                int localClientId = m_transport?.LocalClientId ?? -1;
                if (localClientId >= 0 && localClientId != m_lastLocalClientId)
                {
                    if (m_lastLocalClientId != int.MinValue)
                    {
                        int dropped = m_tracker.PendingCount;
                        m_tracker.Clear();
                        m_clientResetCount++;
                        m_editDroppedClientReset += dropped;
                        Log("客户端连接已更换（LocalClientId " + m_lastLocalClientId + " → " +
                            localClientId + "）→ 清空旧连接的在途编辑请求（" + dropped +
                            " 条；累计 droppedClientReset=" + m_editDroppedClientReset +
                            "；旧序号的回执不再匹配）");
                    }
                    m_lastLocalClientId = localClientId;
                    // 新连接 = 新主机会话：主机的 WorldTimeRevision 可能从 1 重新开始。
                    // 不复位副本会把所有新快照当成 stale 丢弃，天气/雾就永远停在下载世界里的旧值。
                    if (m_weatherReplica.HasAuthoritative) m_weatherReplica.Reset();
                }
            }
            else
            {
                m_lastLocalClientId = int.MinValue;
                if (m_weatherReplica.HasAuthoritative) m_weatherReplica.Reset();
            }
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + (m_edit?.EditStats ?? "no-edit-port") + " · " +
                    (m_weatherReplica.HasAuthoritative ? m_weatherReplica.Describe() : "worldinfo=waiting"));
            }
        }

        // ---------------------------------------------------------------- 主机侧

        private void BroadcastWorldInfo()
        {
            if (m_worldInfo == null || m_broadcast == null || m_edit == null) return;
            if (!m_worldInfo.TryCaptureHost(out WorldInfoSnapshot snapshot) || snapshot == null)
            {
                NoteProblem("世界信息抓取失败（主机还没进世界？）→ 本拍不发送（不伪造时间/天气）");
                return;
            }
            snapshot.ServerTick = (int)Math.Min(int.MaxValue, m_frame);
            snapshot.WorldTimeRevision = ++m_worldInfoRevision;
            byte[] payload = WorldInfoCodec.Encode(snapshot);
            int sent = m_broadcast.BroadcastToPlayers(MessageType.WorldInfo, payload);
            if (sent > 0)
            {
                m_worldInfoSent += sent;
            }
            else if (m_broadcast.PlayerPeerCount > 0)
            {
                m_worldInfoSendFailures++;
                NoteProblem("世界信息广播没有入队（玩家端 " + m_broadcast.PlayerPeerCount +
                    " 个，可能是发送队列背压）→ 下一拍重试，不丢");
            }
        }

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (m_control?.IsHosting != true) return;
            m_edit?.ResetEditClient(clientId);
            if (m_worldInfo == null || m_edit == null) return;
            if (!m_worldInfo.TryCaptureHost(out WorldInfoSnapshot snapshot) || snapshot == null) return;
            snapshot.ServerTick = (int)Math.Min(int.MaxValue, m_frame);
            snapshot.WorldTimeRevision = ++m_worldInfoRevision;
            if (!m_transport.TrySendTo(clientId, MessageType.WorldInfo,
                    WorldInfoCodec.Encode(snapshot)))
            {
                m_worldInfoSendFailures++;
                NoteProblem("加入时给 clientId=" + clientId + " 的 WorldInfo 没有入队 → 下一拍 2 Hz 广播补上");
            }
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            m_edit?.ResetEditClient(clientId);
        }

        private void HandleEditRequest(MessageContext context, PA.CellEditRequest message)
        {
            if (m_edit == null)
            {
                NoteProblem("收到 CellEditRequest 但没有编辑端口（引擎未装配）→ 忽略");
                return;
            }
            bool isPlayer = m_broadcast == null || m_broadcast.IsPlayerClient(context.ClientId);
            bool hasRange = m_edit.TryGetClientRange(context.ClientId, out int centerX, out int centerZ,
                out int radius);
            // 距离校验用**格坐标**：窗口中心是区块坐标，换算成该区块中心格（*16+8）。
            ClientEditDecision decision = m_edit.HandleEditRequest(context.ClientId, message,
                isPlayer, hasRange, centerX * 16 + 8, centerZ * 16 + 8, radius);
            byte[] payload = CellEditResultCodec.Encode(decision.Sequence, decision.Accepted,
                decision.X, decision.Y, decision.Z, decision.Contents, decision.Data,
                decision.Reason, decision.HasValue);
            if (!m_transport.TrySendTo(context.ClientId, MessageType.CellEditResult, payload))
            {
                m_editSendFailures++;
                NoteProblem("给 clientId=" + context.ClientId + " 的 CellEditResult 没有入队（背压）→ " +
                    "客户端会重发请求，主机按序号去重（不会重复落地）");
            }
            if (!decision.Accepted)
            {
                NoteProblem("拒绝编辑 clientId=" + context.ClientId + " cell=" +
                    decision.X.ToString(CultureInfo.InvariantCulture) + "," +
                    decision.Y.ToString(CultureInfo.InvariantCulture) + "," +
                    decision.Z.ToString(CultureInfo.InvariantCulture) + " 原因=" + decision.Reason);
            }
        }

        // ---------------------------------------------------------------- 客户端侧

        private void HandleLocalEdit(ClientCellEdit edit)
        {
            // 每一级都要有计数：captured（received）→ queued（tracker.Submitted）→ sent → acked（confirmed）。
            // 任何一级丢弃都必须有明确原因，禁止像旧实现那样在主机角色下静默 return。
            m_editReceived++;
            if (m_control?.IsHosting == true)
            {
                m_editDroppedHosting++;
                NoteProblem("本地编辑捕获发生在主机角色 → 不上报（主机本身就是权威）；" +
                    "累计 droppedHosting=" + m_editDroppedHosting);
                return;
            }
            if (m_transport == null || !m_transport.IsConnected)
            {
                m_editCaptureIgnored++;
                m_editDroppedNoTransport++;
                return;
            }
            if (m_edit == null || m_edit.IsClientReplica == false)
            {
                // 不是副本（单机 / 还没确定角色）：本地写入就是最终值，不上报。
                m_editCaptureIgnored++;
                m_editDroppedNotReplica++;
                return;
            }
            long nowMs = (long)(Time.RealTime * 1000.0);
            // 每次本地编辑都带上**本端此刻**的有效交互距离（创造模式 SettingsManager.CreativeReach
            // 可调，只在加入时上报会过期）。主机按 min(reach+容差, 主机上限) 校验。
            ClientEditRequest request = m_tracker.Begin(edit.X, edit.Y, edit.Z,
                edit.Contents, edit.Data, nowMs, hasReach: true, reach: m_edit.LocalReach);
            if (request == null)
            {
                m_editDroppedOverflow++;
                NoteProblem("在途编辑请求已达上限 " + ClientEditTracker.MaxPending +
                    "（本格没有上报；累计 droppedOverflow=" + m_editDroppedOverflow +
                    "，tracker overflow=" + m_tracker.Overflow + "）→ " +
                    "主机下一次核对会把它纠正回来");
                return;
            }
            if (m_transport.TrySend(MessageType.CellEditRequest,
                    CellEditRequestCodec.Encode(request)))
            {
                m_editSent++;
            }
            else
            {
                m_editSendFailures++;
                NoteProblem("CellEditRequest 发送失败（背压）→ 留在待确认表里按 250ms 重发");
            }
        }

        private void PumpClientEdits(long nowMs)
        {
            if (m_control?.IsHosting == true || m_edit == null) return;
            IReadOnlyList<ClientEditRequest> due = m_tracker.CollectDue(nowMs);
            for (int i = 0; i < due.Count; i++)
            {
                ClientEditRequest request = due[i];
                if (m_transport == null || !m_transport.TrySend(MessageType.CellEditRequest,
                        CellEditRequestCodec.Encode(request)))
                {
                    m_editSendFailures++;
                }
                else
                {
                    m_editSent++;
                }
            }
            IReadOnlyList<ClientEditRequest> expired = m_tracker.CollectExpired(nowMs);
            for (int i = 0; i < expired.Count; i++)
            {
                ClientEditRequest request = expired[i];
                // 超时回滚：请求主机回传这一整块（本端按预测内容重算摘要 → 主机不一致就回传权威值）。
                bool requested = m_reverify != null &&
                    m_reverify.RequestReverification(request.X >> 4, request.Z >> 4);
                if (requested)
                {
                    m_editReverifyRequests++;
                    m_editRollbacks++;
                    NoteProblem("编辑请求超时 seq=" + request.Sequence + " cell=" +
                        request.X.ToString(CultureInfo.InvariantCulture) + "," +
                        request.Y.ToString(CultureInfo.InvariantCulture) + "," +
                        request.Z.ToString(CultureInfo.InvariantCulture) + " 区块=" +
                        (request.X >> 4).ToString(CultureInfo.InvariantCulture) + "," +
                        (request.Z >> 4).ToString(CultureInfo.InvariantCulture) +
                        " 已发 " + request.Attempts + " 次 → 已请求主机回传该块（回到主机最后的值）");
                }
                else
                {
                    m_editReverifyUnavailable++;
                    NoteProblem("编辑请求超时 seq=" + request.Sequence + " cell=" +
                        request.X.ToString(CultureInfo.InvariantCulture) + "," +
                        request.Y.ToString(CultureInfo.InvariantCulture) + "," +
                        request.Z.ToString(CultureInfo.InvariantCulture) +
                        " 已发 " + request.Attempts + " 次，但**拿不到强制核对通道**" +
                        "（该块不在内存 / 同步未就绪）→ 本格暂时保持预测值，等主机周期核对纠正");
                }
            }
        }

        /// <summary>拒绝 / 无权威值时的明确回滚通道：请求主机回传整块（不一致就会被写回权威值）。</summary>
        private void RequestReverify(int chunkX, int chunkZ, string why)
        {
            if (m_reverify != null && m_reverify.RequestReverification(chunkX, chunkZ))
            {
                m_editReverifyRequests++;
                return;
            }
            m_editReverifyUnavailable++;
            NoteProblem("无法请求主机回传区块 " + chunkX.ToString(CultureInfo.InvariantCulture) + "," +
                chunkZ.ToString(CultureInfo.InvariantCulture) + "（" + why +
                "）→ 该块暂时保持本地值，等主机周期核对纠正");
        }

        private void HandleEditResult(PA.CellEditResult message)
        {
            m_editResultsReceived++;
            long sequence = message.Sequence;
            bool accepted = message.Accepted;
            int x = message.X, y = message.Y, z = message.Z;
            int contents = message.Contents, data = message.Data;
            string reason = message.Reason ?? string.Empty;
            bool hasValue = message.HasValue;
            if (sequence <= 0)
            {
                NoteProblem("CellEditResult 载荷非法（sequence=" + sequence + "）→ 丢弃（不猜是哪条请求）");
                return;
            }
            if (accepted)
            {
                m_tracker.Confirm(sequence);
                // 主机决策后的权威值可能和本地预测不同（方块行为改写了它）：立刻以主机值为准。
                if (hasValue) m_edit?.TryApplyReplicaValue(x, y, z, contents, data);
                else RequestReverify(x >> 4, z >> 4, "编辑已批准但没有权威值");
                return;
            }
            if (!m_tracker.Reject(sequence) && sequence > 0)
            {
                m_tracker.NoteUnknownResult(sequence);
            }
            bool applied = hasValue && m_edit != null &&
                m_edit.TryApplyReplicaValue(x, y, z, contents, data);
            if (applied) m_editRollbacks++;
            else RequestReverify(x >> 4, z >> 4, "拒绝回执没有权威值 / 回滚失败");
            NoteProblem("编辑被主机拒绝 seq=" + sequence + " cell=" + x + "," + y + "," + z +
                " 原因=" + (reason ?? "") + " → " +
                (applied ? "已回滚到主机权威值" : "已请求主机回传该块（不能拿 0/0 猜回滚值）"));
        }

        private void HandleWorldInfo(PWI.WorldInfo message)
        {
            if (!WorldInfoCodec.TryConvert(message, out WorldInfoSnapshot snapshot, out string reason))
            {
                m_worldInfoMalformed++;
                NoteProblem("WorldInfo 载荷非法：" + reason + " → 丢弃（不猜时间/天气）");
                return;
            }
            m_worldInfoReceived++;
            if (!m_weatherReplica.Apply(snapshot)) return;             // 乱序 / 重复的旧快照
            try
            {
                m_worldInfo?.ApplyClient(snapshot);
            }
            catch (Exception e)
            {
                NoteProblem("应用 WorldInfo 异常：" + e.GetType().Name + " " + e.Message);
            }
        }

        // ---------------------------------------------------------------- 诊断

        private string DescribePorts()
        {
            return "transport=yes control=" + (m_control != null ? "yes" : "no") +
                " edit=" + (m_edit != null ? "yes" : "no") +
                " worldinfo=" + (m_worldInfo != null ? "yes" : "no") +
                " rng=" + (m_rng != null ? "yes" : "no") +
                " broadcast=" + (m_broadcast != null ? "yes" : "no") +
                " reverify=" + (m_reverify != null ? "yes" : "no");
        }

        private string Summary()
        {
            return "worldinfo sent=" + m_worldInfoSent.ToString(CultureInfo.InvariantCulture) +
                " recv=" + m_worldInfoReceived.ToString(CultureInfo.InvariantCulture) +
                " malformed=" + m_worldInfoMalformed.ToString(CultureInfo.InvariantCulture) +
                " sendFailures=" + (m_worldInfoSendFailures + m_editSendFailures)
                    .ToString(CultureInfo.InvariantCulture) +
                " edit=" + m_tracker.Describe() +
                " received=" + m_editReceived.ToString(CultureInfo.InvariantCulture) +
                " sent=" + m_editSent.ToString(CultureInfo.InvariantCulture) +
                " dropped(hosting=" + m_editDroppedHosting.ToString(CultureInfo.InvariantCulture) +
                ",noTransport=" + m_editDroppedNoTransport.ToString(CultureInfo.InvariantCulture) +
                ",notReplica=" + m_editDroppedNotReplica.ToString(CultureInfo.InvariantCulture) +
                ",overflow=" + m_editDroppedOverflow.ToString(CultureInfo.InvariantCulture) +
                ",clientReset=" + m_editDroppedClientReset.ToString(CultureInfo.InvariantCulture) +
                ",detached=" + m_editDroppedDetached.ToString(CultureInfo.InvariantCulture) + ")" +
                " capture=[" + (m_edit == null ? "n/a" : m_edit.CaptureStats) + "]" +
                " results=" + m_editResultsReceived.ToString(CultureInfo.InvariantCulture) +
                " rollbacks=" + m_editRollbacks.ToString(CultureInfo.InvariantCulture) +
                " reverify=" + m_editReverifyRequests.ToString(CultureInfo.InvariantCulture) +
                " reverifyUnavailable=" + m_editReverifyUnavailable.ToString(CultureInfo.InvariantCulture) +
                " captureIgnored=" + m_editCaptureIgnored.ToString(CultureInfo.InvariantCulture) +
                " clientResets=" + m_clientResetCount.ToString(CultureInfo.InvariantCulture) +
                " gate=" + (m_edit == null ? "n/a" : (m_edit.IsClientReplica ? "client" : "authority")) +
                (m_weatherReplica.HasAuthoritative ? " · " + m_weatherReplica.Describe()
                    : " · worldinfo=waiting");
        }

        private void NoteProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (nowMs - m_lastProblemMs < ProblemLogMs) return;
            m_lastProblemMs = nowMs;
            Log("警告：" + message);
        }

        private void Log(string message)
        {
            m_context?.Diagnostics?.Info(m_descriptor.Id, message);
        }
    }
}

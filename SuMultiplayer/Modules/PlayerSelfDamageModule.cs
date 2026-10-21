using System;
using System.Collections.Generic;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.Protocol;
using SD = SuMultiplayer.Protocol.SelfDamage;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 角色信息界面"骷髅按钮"（<c>ChokeButton</c>）的**主机裁决自我伤害**（106 / 107）。
    ///
    /// 为什么需要这个模块（引擎事实，逐字对照）：
    ///   · 骷髅按钮跑在**持有控件的那一端**：
    ///     <c>Survivalcraft/Game/VitalStatsWidget.cs:176-179</c>
    ///     <code>
    ///     if (m_chokeButton.IsClicked)
    ///         m_componentPlayer.ComponentHealth.Injure(0.1f, null,
    ///             ignoreInvulnerability: true, "Choked");
    ///     </code>
    ///     它只改本地 <c>ComponentHealth.Health</c>；受伤音效 / 红屏 / 血条闪烁在
    ///     <c>ComponentHealth.Update</c>（<c>ComponentHealth.cs:251-266</c>）；
    ///   · 联机客户端本机角色的 <c>ComponentHealth.Update</c> 已被
    ///     <see cref="IPlayerVitalsPort.DisableLocalSimulation"/> 摘掉（客户端只跟随主机权威），
    ///     所以客户端本地 <c>Injure</c> 是**纯预测**：没有音效 / 红屏，下一拍主机 95 上报
    ///     又把生命写回旧值 → 用户看到"点了不掉血、没受伤表现"；更糟的是本地预测可能把
    ///     <c>Health</c> 写成 0，让客户端 <c>PlayerData</c> 自己判死（主机侧化身还活着）。
    ///
    /// 本模块的三条链路：
    ///   ① **客户端采集**：每帧读 <c>ComponentGui.ModalPanelWidget is VitalStatsWidget</c> 的
    ///      <c>ChokeButton.IsClicked</c>，用 <see cref="PlayerSelfDamagePressGate"/> 做按下沿
    ///      （一次按下只发一条 106）；没有主机权威生命时不发（否则无法安全回滚本地预测）。
    ///      引擎控件那一行本地 <c>Injure</c> 不回滚 —— 由引擎端口的
    ///      <c>SubsystemUpdate</c> 守卫在 <c>PlayerData.Update</c> 之前写回主机权威值；
    ///   ② **主机裁决**：只接受 <c>message.client_id == sourceClientId</c> 且 source &gt; 0 的
    ///      请求；校验白名单 cause / 数值 / 角色存活 / 冷却，然后在**主机侧客户端化身**上执行
    ///      与单机同一行 <c>ComponentHealth.Injure(amount, null, ignoreInvulnerability: true,
    ///      "Choked")</c>；既有的 5 Hz 权威体征上报把新血量推回客户端；
    ///   ③ **客户端表现**：107 接受结果驱动受伤音效 + 红屏 + 血条闪烁（因为客户端
    ///      <c>ComponentHealth.Update</c> 不跑），并按 <c>health_after</c> 立刻回写权威生命；
    ///      拒绝结果按最近一份权威生命回滚，并计数原因码。
    ///
    /// 模块层不引用 Game：控件 / 引擎 Injure / 表现都在 <see cref="IPlayerSelfDamagePort"/>
    /// 后面（实现 = <c>Engine/PlayerVitalsReplica.cs</c>），自检用假端口驱动同一份源码。
    /// 无静态状态；所有失败都有计数 + 10 s 限流日志。
    /// </summary>
    public sealed class PlayerSelfDamageModule : IMultiplayerModule, ISuMPMessageModule
    {
        /// <summary>模块 id（也是消息处理器归属）。</summary>
        public const string ModuleIdText = "self-damage";

        /// <summary>10 秒一条状态行（计数器）。</summary>
        private const int SummaryLogMs = 10000;

        /// <summary>同一条问题日志的最小间隔。</summary>
        private const int ProblemLogMs = 10000;

        /// <summary>
        /// 客户端发出 106 之后等 107 的上限。超时不是"当成成功"：计数 + 明确日志，
        /// 并释放这次请求（本地权威生命已由守卫 / 95 上报覆盖；下一次点击可重试）。
        /// </summary>
        private const int PendingTimeoutMs = 5000;

        /// <summary>未决请求清扫间隔（不需要每帧遍历字典）。</summary>
        private const int PendingSweepMs = 250;

        /// <summary>一个玩家端一次未决请求。</summary>
        private sealed class PendingRequest
        {
            public long Sequence;
            public long SentAtMs;
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly IPlayerSelfDamagePort m_port;
        private readonly Func<double> m_now;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_worldReady;

        /// <summary>客户端本机槽位发现表（0..3）。</summary>
        private readonly HashSet<int> m_localSlots = new HashSet<int>();
        /// <summary>客户端本机槽位的按下沿门控（一次按下最多一条 106）。</summary>
        private readonly PlayerSelfDamagePressGate m_gate = new PlayerSelfDamagePressGate();
        /// <summary>已发出、还没收到 107 的请求（按本机玩家键）。</summary>
        private readonly Dictionary<PlayerKey, PendingRequest> m_pending =
            new Dictionary<PlayerKey, PendingRequest>();
        private readonly List<PlayerKey> m_pendingKeys = new List<PlayerKey>(PlayerKey.SlotCount);

        /// <summary>主机在线玩家端（状态行 / 客户端离开时清理冷却）。</summary>
        private readonly HashSet<int> m_clients = new HashSet<int>();
        /// <summary>主机侧每键最近一次成功施加时刻（<c>cooldown</c> 判据）。</summary>
        private readonly Dictionary<PlayerKey, long> m_lastHostApplyMs =
            new Dictionary<PlayerKey, long>();
        private readonly Dictionary<string, long> m_hostRejectedByReason =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_clientRejectedByReason =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private long m_nextSequence;
        private long m_lastPendingSweepMs;
        private long m_lastSummaryMs;

        // ---------------------------------------------------------------- 计数（10 s 状态行）
        // 客户端
        private long m_sent;
        private long m_sendFailures;
        private long m_pollFailures;
        private long m_localSlotAppears;
        private long m_suppressedSamePress;
        private long m_suppressedDead;
        private long m_suppressedAlreadySent;
        private long m_suppressedNoAuthority;
        private long m_suppressedInvalidSequence;
        private long m_suppressedPending;
        private long m_timeouts;
        private long m_resultRecv;
        private long m_resultDecodeFailures;
        private long m_staleResults;
        private long m_clientAccepted;
        private long m_clientRejected;
        private long m_applyFailures;
        private long m_rollbacks;
        private long m_rollbackFailures;
        private long m_feedbackSound;
        private long m_redoutArmed;
        private long m_redoutFrames;
        private long m_redoutRefreshFailures;
        // 主机
        private long m_received;
        private long m_requestsMalformed;
        private long m_wrongOwner;
        private long m_wrongDirection;
        private long m_notAlive;
        private long m_unknownPlayer;
        private long m_invalid;
        private long m_cooldown;
        private long m_applyFailed;
        private long m_hostAccepted;
        private long m_resultsSent;
        private long m_resultSendFailures;
        private long m_resultEncodeFailures;
        // 两端（方向错误）
        private long m_requestsOnClient;
        private long m_resultsOnHost;

        public PlayerSelfDamageModule(IPlayerSelfDamagePort port, Func<double> now)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_now = now ?? throw new ArgumentNullException(nameof(now));
            m_descriptor = new ModuleDescriptor(ModuleIdText,
                "自我伤害（骷髅按钮）：客户端只发请求，主机裁决后在主机侧 Injure，结果驱动表现")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "player.selfdamage" },
                HotPluggable = true
            };
        }

        /// <summary>时间源（秒）；测试可注入，生产 = Time.RealTime。</summary>
        public Func<double> Now => m_now;

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => ModuleIdText;

        // 测试 / 诊断只读计数
        public long Sent => m_sent;
        public long Received => m_received;
        public long HostAccepted => m_hostAccepted;
        public long HostRejected =>
            m_wrongOwner + m_notAlive + m_unknownPlayer + m_invalid + m_cooldown + m_applyFailed;
        public long ClientAccepted => m_clientAccepted;
        public long ClientRejected => m_clientRejected;
        public long FeedbackSound => m_feedbackSound;
        public long RedoutArmed => m_redoutArmed;
        public long Pending => m_pending.Count;
        public long Timeouts => m_timeouts;

        // ---------------------------------------------------------------- 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Error("没有拿到传输端口（transport 能力缺失）→ 自我伤害同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Error("没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端角色，" +
                    "自我伤害同步不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_messages) || m_messages == null)
            {
                Error("没有拿到消息注册表（IMessageRegistryPort）→ 收不到主机裁决 / 无法发送请求");
                return;
            }
            m_transport.ClientJoined += OnClientJoined;
            m_transport.ClientLeft += OnClientLeft;
            m_messages.Registry.RegisterModule(this);
            m_attached = true;
            Info("自我伤害同步就绪：客户端只发 PLAYER_SELF_DAMAGE_REQUEST（106），主机校验归属 / " +
                "存活 / 数值 / 冷却后在主机侧客户端化身上执行 ComponentHealth.Injure 0.1f, null, " +
                "ignoreInvulnerability: true, \"Choked\")；107 结果驱动受伤音效 + 红屏；" +
                "客户端本地预测由 SubsystemUpdate 守卫在 PlayerData.Update 之前回滚");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.PlayerSelfDamageRequest,
                SD.PlayerSelfDamageRequest.Parser,
                new DelegateMessageHandler<SD.PlayerSelfDamageRequest>(OnRequestMessage), ModuleId);
            registry.Register((int)MessageType.PlayerSelfDamageResult,
                SD.PlayerSelfDamageResult.Parser,
                new DelegateMessageHandler<SD.PlayerSelfDamageResult>(OnResultMessage), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = StatusLine();
            if (m_transport != null)
            {
                try { m_transport.ClientJoined -= OnClientJoined; } catch (Exception) { }
                try { m_transport.ClientLeft -= OnClientLeft; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
            }
            if (m_port != null)
            {
                try { m_port.DetachSelfDamage(); } catch (Exception) { }
            }
            m_localSlots.Clear();
            m_gate.Clear();
            m_pending.Clear();
            m_pendingKeys.Clear();
            m_clients.Clear();
            m_lastHostApplyMs.Clear();
            m_hostRejectedByReason.Clear();
            m_clientRejectedByReason.Clear();
            m_lastProblemMs.Clear();
            m_messages = null;
            m_transport = null;
            m_control = null;
            m_worldReady = false;
            m_attached = false;
            Info("自我伤害同步下线（" + reason + "）：" + summary);
        }

        // ---------------------------------------------------------------- 每帧

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            long nowMs = (long)(m_now() * 1000.0);
            bool hosting = m_control != null && m_control.IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            bool inWorld = tick.IsConnected;
            bool clientRole = !hosting && connected && inWorld;
            if (!clientRole)
            {
                if (m_worldReady) ClearClientState(hosting ? "本端成为主机" : "离开世界 / 断线");
            }
            else
            {
                m_worldReady = true;
                TickClient(nowMs, tick.DeltaTime);
            }
            LogSummaryIfDue(nowMs, hosting, connected);
        }

        private void TickClient(long nowMs, float deltaTime)
        {
            SweepPending(nowMs);
            for (int slot = 0; slot < PlayerKey.SlotCount; slot++)
            {
                PlayerKey key = new PlayerKey(0, slot);
                if (!m_port.TryPollLocalSelfDamageClick(key,
                        out PlayerSelfDamagePressState state, out string pollReason))
                {
                    if (m_localSlots.Remove(slot))
                    {
                        m_pollFailures++;
                        Problem("poll:" + key, "读取本机槽位 " + slot +
                            " 的骷髅按钮失败：" + (pollReason ?? "端口没有给原因") +
                            " → 本帧不判定点击（不猜输入）");
                    }
                    continue;
                }
                if (m_localSlots.Add(slot)) m_localSlotAppears++;
                long sequence = m_nextSequence + 1;
                PlayerSelfDamageGateAction action = m_gate.OnSample(key, in state, sequence,
                    out string suppressReason);
                switch (action)
                {
                    case PlayerSelfDamageGateAction.None:
                        break;
                    case PlayerSelfDamageGateAction.SendRequest:
                        if (m_pending.ContainsKey(key))
                        {
                            // 上一发还没裁决：不覆盖未决序号（否则旧 107 会被当成陈旧结果）。
                            m_suppressedPending++;
                            Problem("pending:" + key, "玩家键 " + key +
                                " 的上一次自我伤害请求还没收到 107 → 本次按下不发" +
                                "（等待裁决或 5 s 超时；不重发）");
                            break;
                        }
                        m_nextSequence = sequence;
                        if (SendRequest(key, sequence, out string sendReason))
                        {
                            m_pending[key] = new PendingRequest
                            {
                                Sequence = sequence,
                                SentAtMs = nowMs
                            };
                        }
                        else
                        {
                            Problem("send:" + key, "自我伤害请求发送失败（玩家键 " + key +
                                " seq=" + sequence + "）：" + sendReason +
                                " → 同一次按下不重发（重新点一次即可）");
                        }
                        break;
                    case PlayerSelfDamageGateAction.SuppressedSamePress:
                        m_suppressedSamePress++;
                        break;
                    case PlayerSelfDamageGateAction.SuppressedDead:
                        m_suppressedDead++;
                        break;
                    case PlayerSelfDamageGateAction.SuppressedAlreadySent:
                        m_suppressedAlreadySent++;
                        break;
                    case PlayerSelfDamageGateAction.SuppressedNoAuthority:
                        m_suppressedNoAuthority++;
                        Problem("no-authority:" + key, "本机槽位 " + slot +
                            " 按了骷髅按钮，但还没有主机权威生命 → 不发 106（不能证明本地预测" +
                            "能回滚；客户端不自行判死）");
                        break;
                    case PlayerSelfDamageGateAction.SuppressedInvalidSequence:
                        m_suppressedInvalidSequence++;
                        Problem("invalid-sequence:" + key, "自我伤害请求序号 " + sequence +
                            " 非法 → 不发（序列上限 " + PlayerSelfDamageLimits.MaxSequence + "）");
                        break;
                }
            }
            int redoutFrames = m_port.RefreshLocalSelfDamagePresentation(deltaTime,
                out string redoutReason);
            m_redoutFrames += redoutFrames;
            if (!string.IsNullOrEmpty(redoutReason))
            {
                m_redoutRefreshFailures++;
                Problem("redout-refresh", "本机自我伤害红屏刷新失败：" + redoutReason +
                    "（本拍应用 " + redoutFrames + " 个槽位；不静默跳过）");
            }
        }

        /// <summary>离开世界 / 断线 / 本端变主机：清掉客户端侧的所有未决与边沿状态。</summary>
        private void ClearClientState(string why)
        {
            m_worldReady = false;
            int pending = m_pending.Count;
            m_pending.Clear();
            m_pendingKeys.Clear();
            m_localSlots.Clear();
            m_gate.Clear();
            if (pending > 0)
            {
                Info("清空自我伤害未决请求（" + why + "）：" + pending +
                    " 条没有等到 107（不重发；下一局重新点击）");
            }
        }

        private void SweepPending(long nowMs)
        {
            if (m_pending.Count == 0) return;
            if (nowMs - m_lastPendingSweepMs < PendingSweepMs) return;
            m_lastPendingSweepMs = nowMs;
            m_pendingKeys.Clear();
            foreach (PlayerKey key in m_pending.Keys) m_pendingKeys.Add(key);
            for (int i = 0; i < m_pendingKeys.Count; i++)
            {
                PlayerKey key = m_pendingKeys[i];
                if (!m_pending.TryGetValue(key, out PendingRequest pending)) continue;
                if (nowMs - pending.SentAtMs <= PendingTimeoutMs) continue;
                m_pending.Remove(key);
                m_timeouts++;
                Problem("timeout:" + key, "自我伤害请求 " + pending.Sequence + " 在 " +
                    PendingTimeoutMs + " ms 内没有收到 107（玩家键 " + key +
                    "）→ 释放本次请求；本地生命由守卫 / 95 上报覆盖（不重发）");
            }
        }

        // ---------------------------------------------------------------- 客户端：采集 / 发送 / 结果

        private bool SendRequest(PlayerKey key, long sequence, out string reason)
        {
            reason = null;
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (localClientId <= 0)
            {
                m_sendFailures++;
                reason = "本端传输编号=" + localClientId + "（还没拿到主机分配的 clientId）";
                return false;
            }
            var data = new PlayerSelfDamageRequestData(localClientId, key.Slot, sequence,
                PlayerSelfDamageCauses.Choked, PlayerSelfDamageLimits.ChokedAmount);
            SD.PlayerSelfDamageRequest message;
            try
            {
                message = PlayerSelfDamageCodec.Encode(in data);
            }
            catch (Exception e)
            {
                m_sendFailures++;
                reason = "请求编码失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            bool sent;
            try
            {
                sent = m_messages.Sender.Send(0, message);
            }
            catch (Exception e)
            {
                m_sendFailures++;
                reason = "请求发送异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            if (!sent)
            {
                m_sendFailures++;
                reason = "消息层返回发送失败（通道不可用 / 队列满）";
                return false;
            }
            m_sent++;
            Info("角色信息界面点击骷髅按钮 → 已发送自我伤害请求 " + data.Describe() +
                "（主机裁决；本地预测由守卫回滚）");
            return true;
        }

        private void OnRequestMessage(MessageContext context, SD.PlayerSelfDamageRequest message)
        {
            if (!m_attached) return;
            bool hosting = m_control != null && m_control.IsHosting;
            if (!hosting)
            {
                m_requestsOnClient++;
                Problem("request-on-client", "客户端收到来自 clientId=" + context.ClientId +
                    " 的 PLAYER_SELF_DAMAGE_REQUEST（只有客户端能发）→ 整条拒绝");
                return;
            }
            int source = context.ClientId;
            if (source <= 0)
            {
                m_wrongDirection++;
                Problem("request-no-source", "主机收到 sourceClientId=" + source +
                    " 的自我伤害请求 → 没有可回复/可归属的玩家端，整条拒绝");
                return;
            }
            if (!PlayerSelfDamageCodec.TryDecode(message, out PlayerSelfDamageRequestData data,
                    out string decodeError))
            {
                m_requestsMalformed++;
                Problem("malformed-request", "自我伤害请求解码失败（source=" + source + "）：" +
                    decodeError + " → 整条拒绝");
                SendMalformedReject(source, message);
                return;
            }
            m_received++;
            if (data.ClientId != source)
            {
                m_wrongOwner++;
                Reject(source, data.Slot, data.Sequence, data.Cause,
                    PlayerSelfDamageReasons.WrongOwner,
                    "message.client_id=" + data.ClientId + " 与 sourceClientId=" + source +
                    " 不一致（只能请求自己那台设备的角色）");
                return;
            }
            long nowMs = (long)(m_now() * 1000.0);
            if (m_lastHostApplyMs.TryGetValue(data.Key, out long lastAt) &&
                nowMs - lastAt < PlayerSelfDamageLimits.HostRequestGapMs)
            {
                m_cooldown++;
                Reject(source, data.Slot, data.Sequence, data.Cause,
                    PlayerSelfDamageReasons.Cooldown,
                    "距上一次成功施加只有 " + (nowMs - lastAt) + " ms（门槛 " +
                    PlayerSelfDamageLimits.HostRequestGapMs + " ms）");
                return;
            }
            PlayerSelfDamageApplyStatus status = m_port.TryApplyAuthoritySelfDamage(data.Key,
                data.Amount, data.Cause, out float healthAfter, out string applyReason);
            switch (status)
            {
                case PlayerSelfDamageApplyStatus.Applied:
                    m_hostAccepted++;
                    m_lastHostApplyMs[data.Key] = nowMs;
                    SendAccepted(source, data, healthAfter);
                    Info("主机裁决自我伤害 " + data.Describe() + " → 接受；主机侧角色 " +
                        data.Key + " 权威生命=" + healthAfter.ToString("0.###") +
                        "（由既有 5 Hz 体征上报下发）");
                    return;
                case PlayerSelfDamageApplyStatus.NoAvatar:
                    m_unknownPlayer++;
                    Reject(source, data.Slot, data.Sequence, data.Cause,
                        PlayerSelfDamageReasons.UnknownPlayer,
                        applyReason ?? "主机上找不到这个玩家键的化身");
                    return;
                case PlayerSelfDamageApplyStatus.NotAlive:
                    m_notAlive++;
                    Reject(source, data.Slot, data.Sequence, data.Cause,
                        PlayerSelfDamageReasons.NotAlive, applyReason ?? "角色已经死亡");
                    return;
                case PlayerSelfDamageApplyStatus.Invalid:
                    m_invalid++;
                    Reject(source, data.Slot, data.Sequence, data.Cause,
                        PlayerSelfDamageReasons.Invalid, applyReason ?? "数值 / cause 非法");
                    return;
                default:
                    m_applyFailed++;
                    Reject(source, data.Slot, data.Sequence, data.Cause,
                        PlayerSelfDamageReasons.ApplyFailed,
                        applyReason ?? ("端口返回 " + status));
                    return;
            }
        }

        private void OnResultMessage(MessageContext context, SD.PlayerSelfDamageResult message)
        {
            if (!m_attached) return;
            if (m_control != null && m_control.IsHosting)
            {
                m_resultsOnHost++;
                Problem("result-on-host", "主机收到来自 clientId=" + context.ClientId +
                    " 的 PLAYER_SELF_DAMAGE_RESULT（只有主机能下发）→ 整条拒绝");
                return;
            }
            if (context.ClientId != 0)
            {
                m_wrongDirection++;
                Problem("result-sender", "客户端收到来自 clientId=" + context.ClientId +
                    " 的自我伤害结果（只有主机能下发）→ 整条拒绝");
                return;
            }
            if (!PlayerSelfDamageCodec.TryDecodeResult(message,
                    out PlayerSelfDamageResultData data, out string decodeError))
            {
                m_resultDecodeFailures++;
                Problem("malformed-result", "自我伤害结果解码失败：" + decodeError +
                    " → 整条丢弃");
                return;
            }
            m_resultRecv++;
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (localClientId <= 0 || data.ClientId != localClientId)
            {
                m_staleResults++;
                Problem("result-owner", "自我伤害结果的 client_id=" + data.ClientId +
                    " 不是本端 " + localClientId + " → 丢弃（不应用到别的角色）");
                return;
            }
            // 107 的 client_id 是**连接编号**（本端 = 2），而本机角色键固定 clientId=0：
            // 未决表 / 端口一律用本机键，绝不拿连接编号去找本机角色。
            PlayerKey localKey = new PlayerKey(0, data.Slot);
            if (!m_pending.TryGetValue(localKey, out PendingRequest pending))
            {
                m_staleResults++;
                Problem("result-unsolicited:" + localKey, "收到玩家键 " + localKey +
                    " 的自我伤害结果 " + data.Sequence +
                    "，但没有对应的未决请求 → 丢弃（不猜是哪次点击）");
                return;
            }
            if (data.Sequence != pending.Sequence)
            {
                m_staleResults++;
                Problem("result-sequence:" + localKey, "自我伤害结果序号 " + data.Sequence +
                    " 与未决请求 " + pending.Sequence + " 不一致 → 丢弃");
                return;
            }
            m_pending.Remove(localKey);
            if (data.Accepted)
            {
                m_clientAccepted++;
                bool applied = m_port.TryApplyLocalSelfDamageResult(localKey, in data,
                    out bool playedSound, out bool redoutArmed, out string applyReason);
                if (!applied)
                {
                    m_applyFailures++;
                    Problem("apply-result:" + localKey,
                        "应用主机接受的自我伤害结果失败（玩家键 " + localKey + "）：" +
                        (applyReason ?? "端口没有给原因"));
                }
                if (playedSound) m_feedbackSound++;
                if (redoutArmed) m_redoutArmed++;
                Info("主机接受自我伤害（" + data.Describe() + "）→ 客户端按权威生命回写，" +
                    (playedSound ? "已播放受伤音效 / 红屏" : "数值已由 95 上报先行写入（不重复表现）"));
                return;
            }
            m_clientRejected++;
            Bump(m_clientRejectedByReason, PlayerSelfDamageReasons.Key(data.Reason));
            if (m_port.TryRestoreLocalAuthorityHealth(localKey, out float authority,
                    out string restoreReason))
            {
                m_rollbacks++;
            }
            else
            {
                m_rollbackFailures++;
                Problem("rollback:" + localKey,
                    "主机拒绝自我伤害后回滚本地生命失败（玩家键 " + localKey +
                    "）：" + (restoreReason ?? "端口没有给原因") +
                    " → 等下一份 95 上报考正");
            }
            Info("主机拒绝自我伤害（" + data.Describe() + "）→ 按权威生命回滚（" +
                authority.ToString("0.###") + "）；原因已计数");
        }

        // ---------------------------------------------------------------- 主机：回执

        private void SendAccepted(int targetClientId, in PlayerSelfDamageRequestData request,
            float healthAfter)
        {
            var result = new PlayerSelfDamageResultData(request.ClientId, request.Slot,
                request.Sequence, true, PlayerSelfDamageReasons.Accepted, request.Cause,
                request.Amount, healthAfter);
            SendResult(targetClientId, in result);
        }

        private void Reject(int targetClientId, int slot, long sequence, string cause,
            string reasonCode, string detail)
        {
            Bump(m_hostRejectedByReason, reasonCode);
            var result = new PlayerSelfDamageResultData(targetClientId, slot, sequence, false,
                reasonCode, cause ?? string.Empty, 0f, 0f);
            bool sent = SendResult(targetClientId, in result);
            Info("主机拒绝自我伤害（clientId=" + targetClientId + " slot=" + slot + " seq=" +
                sequence + " reason=" + reasonCode + "）：" + detail +
                (sent ? " → 已回 107" : " → 107 发送失败（已计数）"));
        }

        private void SendMalformedReject(int source, SD.PlayerSelfDamageRequest message)
        {
            if (message == null)
            {
                Problem("malformed-null", "自我伤害请求为空，无法回执");
                return;
            }
            ulong sequence = message.Sequence;
            if (message.Slot > PlayerKey.MaxSlot || sequence == 0 ||
                sequence > (ulong)PlayerSelfDamageLimits.MaxSequence)
            {
                Problem("malformed-fields", "自我伤害请求的 slot / sequence 也非法（slot=" +
                    message.Slot + " seq=" + sequence + "）→ 无法回一条可匹配的 107");
                return;
            }
            string cause = message.Cause ?? string.Empty;
            if (cause.Length > PlayerSelfDamageLimits.MaxCauseLength) cause = string.Empty;
            var result = new PlayerSelfDamageResultData(source, (int)message.Slot,
                (long)sequence, false, PlayerSelfDamageReasons.Malformed, cause, 0f, 0f);
            SendResult(source, in result);
        }

        private bool SendResult(int targetClientId, in PlayerSelfDamageResultData result)
        {
            if (m_transport == null)
            {
                m_resultSendFailures++;
                return false;
            }
            byte[] payload;
            try
            {
                payload = PlayerSelfDamageCodec.EncodeResultBytes(in result);
            }
            catch (Exception e)
            {
                m_resultEncodeFailures++;
                Problem("result-encode", "自我伤害结果编码失败（" + result.Describe() + "）：" +
                    e.GetType().Name + " " + e.Message);
                return false;
            }
            bool sent;
            try
            {
                sent = m_transport.TrySendTo(targetClientId,
                    MessageType.PlayerSelfDamageResult, payload);
            }
            catch (Exception e)
            {
                sent = false;
                Problem("result-send-exception", "自我伤害结果发送异常（" + result.Describe() +
                    "）：" + e.GetType().Name + " " + e.Message);
            }
            if (!sent)
            {
                m_resultSendFailures++;
                Problem("result-send", "自我伤害结果发送失败（" + result.Describe() +
                    "，目标 clientId=" + targetClientId + "）");
                return false;
            }
            m_resultsSent++;
            return true;
        }

        // ---------------------------------------------------------------- 客户端离开 / 诊断

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (clientId <= 0 || m_control == null || !m_control.IsHosting) return;
            m_clients.Add(clientId);
            Info("自我伤害：玩家端 clientId=" + clientId + (rejoin ? "（重连）" : "") +
                " 上线（主机接受其自我伤害请求；" + m_clients.Count + " 个玩家端）");
        }

        private void OnClientLeft(int clientId, string reason)
        {
            m_clients.Remove(clientId);
            m_pendingKeys.Clear();
            foreach (PlayerKey key in m_lastHostApplyMs.Keys)
            {
                if (key.ClientId == clientId) m_pendingKeys.Add(key);
            }
            for (int i = 0; i < m_pendingKeys.Count; i++)
                m_lastHostApplyMs.Remove(m_pendingKeys[i]);
            m_pendingKeys.Clear();
            Info("自我伤害：玩家端 clientId=" + clientId + " 离开（" + reason +
                "）→ 清掉它的施加冷却");
        }

        private static void Bump(Dictionary<string, long> counters, string key)
        {
            if (counters == null) return;
            string k = string.IsNullOrEmpty(key) ? "unknown" : key;
            counters.TryGetValue(k, out long value);
            counters[k] = value + 1;
        }

        private static string Detail(Dictionary<string, long> counters)
        {
            if (counters == null || counters.Count == 0) return "{}";
            var builder = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (KeyValuePair<string, long> pair in counters)
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append(pair.Key).Append('=').Append(pair.Value);
            }
            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>状态行（10 s 一条；测试也用它取计数）。</summary>
        public string StatusLine()
        {
            string mode = m_control != null && m_control.IsHosting
                ? "host"
                : (m_transport != null && m_transport.IsConnected ? "client" : "idle");
            return "selfDamage[" + mode + "]: selfDamageSent=" + m_sent +
                " selfDamageSendFail=" + m_sendFailures +
                " selfDamagePollFail=" + m_pollFailures +
                " selfDamageLocalSlots=" + m_localSlots.Count +
                " selfDamageSlotAppears=" + m_localSlotAppears +
                " selfDamageSuppressedSamePress=" + m_suppressedSamePress +
                " selfDamageSuppressedDead=" + m_suppressedDead +
                " selfDamageSuppressedAlreadySent=" + m_suppressedAlreadySent +
                " selfDamageSuppressedNoAuthority=" + m_suppressedNoAuthority +
                " selfDamageSuppressedInvalidSequence=" + m_suppressedInvalidSequence +
                " selfDamageSuppressedPending=" + m_suppressedPending +
                " selfDamagePending=" + m_pending.Count +
                " selfDamageTimeouts=" + m_timeouts +
                " selfDamageRecv=" + m_received +
                " selfDamageMalformed=" + m_requestsMalformed +
                " selfDamageWrongOwner=" + m_wrongOwner +
                " selfDamageWrongDirection=" + m_wrongDirection +
                " selfDamageNotAlive=" + m_notAlive +
                " selfDamageUnknownPlayer=" + m_unknownPlayer +
                " selfDamageInvalid=" + m_invalid +
                " selfDamageCooldown=" + m_cooldown +
                " selfDamageApplyFailed=" + m_applyFailed +
                " selfDamageHostAccepted=" + m_hostAccepted +
                " selfDamageAccepted=" + (m_hostAccepted + m_clientAccepted) +
                " selfDamageRejected=" + (HostRejected + m_clientRejected) +
                " selfDamageRejectedBy=" + HostRejectedDetail() +
                " selfDamageHostRejectedBy=" + Detail(m_hostRejectedByReason) +
                " selfDamageResultsSent=" + m_resultsSent +
                " selfDamageResultSendFail=" + m_resultSendFailures +
                " selfDamageResultEncodeFail=" + m_resultEncodeFailures +
                " selfDamageResultRecv=" + m_resultRecv +
                " selfDamageResultDecodeFail=" + m_resultDecodeFailures +
                " selfDamageStaleResults=" + m_staleResults +
                " selfDamageRequestsOnClient=" + m_requestsOnClient +
                " selfDamageResultsOnHost=" + m_resultsOnHost +
                " selfDamageClientAccepted=" + m_clientAccepted +
                " selfDamageClientRejected=" + m_clientRejected +
                " selfDamageClientRejectedBy=" + Detail(m_clientRejectedByReason) +
                " selfDamageApplyFail=" + m_applyFailures +
                " selfDamageRollbacks=" + m_rollbacks +
                " selfDamageRollbackFail=" + m_rollbackFailures +
                " selfDamageFeedbackSound=" + m_feedbackSound +
                " selfDamageClientFeedbackSound=" + m_feedbackSound +
                " selfDamageRedoutArmed=" + m_redoutArmed +
                " selfDamageRedoutApplied=" + m_redoutArmed +
                " selfDamageRedoutFrames=" + m_redoutFrames +
                " selfDamageRedoutRefreshFail=" + m_redoutRefreshFailures +
                " selfDamageClients=" + m_clients.Count +
                " engine=" + (m_port != null ? m_port.DescribeSelfDamage() : "none");
        }

        private string HostRejectedDetail() =>
            "{host:" + Detail(m_hostRejectedByReason) + ",client:" +
            Detail(m_clientRejectedByReason) + "}";

        private void LogSummaryIfDue(long nowMs, bool hosting, bool connected)
        {
            if (nowMs - m_lastSummaryMs < SummaryLogMs) return;
            m_lastSummaryMs = nowMs;
            if (hosting || connected || m_sent > 0 || m_received > 0 || m_resultRecv > 0)
                Info(StatusLine());
        }

        private void Info(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void Problem(string key, string message)
        {
            long now = Environment.TickCount64;
            if (m_lastProblemMs.TryGetValue(key, out long last) && now - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = now;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        private void Error(string message) => m_context?.Diagnostics.Error(ModuleId, message, null);
    }
}

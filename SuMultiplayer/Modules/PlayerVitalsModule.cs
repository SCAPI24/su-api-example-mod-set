using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.Protocol;
using V = SuMultiplayer.Protocol.Vitals;
using R = SuMultiplayer.Protocol.Respawn;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 玩家生命 / 体征 / 死亡状态同步（审计缺陷 F14/F7；2026-10 改为**主机权威**）。
    ///
    /// 产品口径（不可回退）：
    ///   · **主机模拟每一个角色**：主机本机角色（PlayerKey(0, slot)）+ 主机侧每个客户端化身
    ///     （PlayerKey(clientId, slot)）。主机不再摘掉远端化身的生命 / 体征 / 睡眠 Update，
    ///     也不再对账回滚主机侧伤害 —— 生物攻击 / 爆炸 / 摔落 / 溺水 / 火焰 / 岩浆 / 饥饿 /
    ///     疾病在主机上按单机规则生效；H3 调试镜像读到的就是这份权威状态。
    ///   · **主机下发，客户端只跟随**：客户端**绝不**上报自己的生命 / 体征；客户端本机角色的
    ///     ComponentHealth / ComponentVitalStats / ComponentSleep / ComponentFlu /
    ///     ComponentSickness / ComponentOnFire 的 Update 被摘掉，只应用主机下发的值
    ///     （见 <see cref="IPlayerVitalsPort.DisableLocalSimulation"/> / ApplyLocalVitals）。
    ///   · **按 (clientId, slot) 定向 + 观察者转发**：主机给每个在线玩家端各发一份
    ///     `PLAYER_VITALS_REPORT`，内容 = 该端自己的权威状态（owner 条目）+ 其它角色
    ///     （观察者条目，用于姿势 / HUD）。TCP 按连接保序，同一份载荷可以发给多个端；
    ///     每个端的 sequence 单调，revision 仍是每键单调。
    ///   · **死亡 / 复活由主机裁决，但复活必须由玩家端请求**：主机检测死亡边沿 →
    ///     立刻发 Death 事件（死因）→ 标记该客户端化身死亡 → **等待 owner 的
    ///     PLAYER_RESPAWN_REQUEST**（主机不再有自动复活计时器）。客户端显示引擎等价的
    ///     死亡屏（大消息 + DeathCamera）与 1.5 秒点击门槛，点击后最多发一次请求；
    ///     主机校验 owner / 仍死亡 / 模式规则后按它自己的出生点复活，发 Respawn 事件
    ///     （落点）+ 权威状态，并回 PLAYER_RESPAWN_RESULT。本机角色仍由引擎 PlayerData
    ///     的死亡 / 复活流程处理（主机本机角色点击自己的死亡屏）。
    ///   · **线路 append-only**：复用 id 95（PLAYER_VITALS_REPORT）/ 96（PLAYER_VITALS_EVENTS），
    ///     只改方向语义为 **host -> client**；新增 101（PLAYER_RESPAWN_REQUEST）/
    ///     102（PLAYER_RESPAWN_RESULT），不重编号。
    ///
    /// 节奏与上限：
    ///   · 有变化 → 最多 5 Hz（200 ms）；没变化 → 1 Hz 心跳；死亡 / 复活 / 新端加入 → 立刻；
    ///   · 一份上报 ≤ <see cref="PlayerVitalsLimits.MaxReportEntries"/> 条、≤ 8 KB；
    ///   · 每条条目带**每键单调 revision**：比已应用的小 = 陈旧（丢弃并计数），相等 = 心跳重复（忽略）。
    ///
    /// 无静态状态；模块层不引用 Game（引擎动作全在 <see cref="IPlayerVitalsPort"/> 后面，
    /// P:\tmp\tsverify 用同一份源码 + 假端口直接编译）。
    /// </summary>
    public sealed class PlayerVitalsModule : IMultiplayerModule, ISuMPMessageModule
    {
        /// <summary>模块 id（也是消息处理器归属）。</summary>
        public const string ModuleIdText = "player-vitals";

        /// <summary>有变化时的下发间隔（5 Hz）。</summary>
        private const int ReportIntervalMs = 200;

        /// <summary>没有变化时的心跳间隔（丢了 / 新对端加入靠它补齐）。</summary>
        private const int HeartbeatMs = 1000;

        /// <summary>客户端本机模拟闸门的重扫间隔（新角色 / 复活换实体后 0.5 秒内补摘）。</summary>
        private const int LocalSimIntervalMs = 500;

        /// <summary>客户端远端化身模拟抑制的重扫间隔（新化身 1 秒内补摘）。</summary>
        private const int SuppressIntervalMs = 1000;

        /// <summary>
        /// 主机裁决客户端化身复活的等待：**没有自动复活**。这个常量只是请求校验的
        /// 冷却下界（与引擎 1.5 秒点击门槛同一个值，见 PlayerRespawnRules.HostGuardMs）。
        /// </summary>
        private const int HostRespawnGuardMs = PlayerRespawnRules.HostGuardMs;

        /// <summary>同一死亡期内两次请求之间的最小间隔（防连发；也用于重复请求拒绝）。</summary>
        private const int HostRespawnRequestGapMs = 250;

        /// <summary>
        /// 同一睡眠期内两次唤醒请求之间的最小间隔（防连发）。与复活请求同一个量级；
        /// 客户端本来每次睡眠最多发一条（<c>PlayerSleepGate</c>），这里是主机的第二道口径。
        /// </summary>
        private const int HostWakeRequestGapMs = 250;

        /// <summary>10 秒一条状态行（计数器）。</summary>
        private const int SummaryLogMs = 10000;

        /// <summary>同一条问题日志的最小间隔。</summary>
        private const int ProblemLogMs = 10000;

        /// <summary>一次最多发几条死亡 / 复活事件（超过就分多条）。</summary>
        private const int MaxEventsPerMessage = 8;

        /// <summary>主机侧一个角色的发送 / 边沿状态（主机本机角色与客户端化身共用）。</summary>
        private sealed class HostCharacterState
        {
            public bool HasSample;
            public bool Dead;
            public int Token;
            /// <summary>每键单调状态版本（0 = 还没发过；第一次发之前会置 1）。</summary>
            public uint Revision;
            public bool HasSent;
            public PlayerVitalsSnapshot LastSent;
            /// <summary>死亡边沿时刻（0 = 活着；只用于请求冷却，不再用于自动复活）。</summary>
            public long DeathAtMs;
            /// <summary>最近一次复活请求时刻（0 = 本次死亡还没请求过）。</summary>
            public long LastRequestAtMs;
            /// <summary>最近一次唤醒请求时刻（0 = 本睡眠期还没请求过；<c>cooldown</c> 判据）。</summary>
            public long LastWakeRequestAtMs;
            /// <summary>最近一次唤醒请求针对的睡眠期（主机 <c>sleep_start_time</c>；换期即清冷却）。</summary>
            public double LastWakeRequestStartTime;
            /// <summary>上一拍是否处于强制睡眠（<c>sleepForced</c> 只在边沿 +1）。</summary>
            public bool WasForcedSleep;
        }

        /// <summary>客户端侧一个玩家键的接收状态。</summary>
        private sealed class ClientKeyState
        {
            public uint LastRevision;
            public bool HasApplied;
            public PlayerVitalsSnapshot LastApplied;
            public uint LastEventRevision;
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly Func<double> m_now;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private IPeerBroadcastPort m_broadcastPort;
        private IPlayerVitalsPort m_port;
        /// <summary>睡眠表现 / 点击唤醒引擎端口（同一实现 = PlayerVitalsReplica）。</summary>
        private IPlayerSleepPort m_sleepPort;
        private bool m_attached;
        private bool m_wasHosting;
        private bool m_wasConnected;
        private bool m_worldReady;

        /// <summary>主机侧：在线玩家端 clientId（ClientJoined / ClientLeft 维护；方向裁决靠它）。</summary>
        private readonly HashSet<int> m_clients = new HashSet<int>();
        private readonly List<int> m_clientList = new List<int>(4);
        private readonly Dictionary<PlayerKey, HostCharacterState> m_hostCharacters =
            new Dictionary<PlayerKey, HostCharacterState>();
        private readonly List<PlayerVitalsSnapshot> m_authorityCapture =
            new List<PlayerVitalsSnapshot>(PlayerVitalsLimits.MaxReportEntries);
        private readonly List<PlayerVitalsSnapshot> m_sendEntries =
            new List<PlayerVitalsSnapshot>(PlayerVitalsLimits.MaxReportEntries);
        private readonly List<PlayerVitalsSlotSample> m_samples =
            new List<PlayerVitalsSlotSample>(PlayerVitalsLimits.MaxReportEntries);

        /// <summary>客户端侧：收到 / 应用过的每键状态。</summary>
        private readonly Dictionary<PlayerKey, ClientKeyState> m_clientCharacters =
            new Dictionary<PlayerKey, ClientKeyState>();

        /// <summary>
        /// 客户端侧：每个本机角色的死亡 / 复活请求状态机（1.5 秒门槛、每次死亡最多一个请求、
        /// 结果 / 超时后不再响应点击）。纯逻辑在 Net/PlayerRespawnNet.cs，可无设备自检。
        /// </summary>
        private readonly PlayerRespawnGate m_respawnGate = new PlayerRespawnGate();
        /// <summary>已对哪个死亡期执行过不可复活模式的点击动作（Cruel 菜单 / Adventure 说明）。</summary>
        private readonly HashSet<PlayerKey> m_terminalDeathHandled = new HashSet<PlayerKey>();
        /// <summary>每帧遍历死亡屏的复用缓冲。</summary>
        private readonly List<PlayerKey> m_respawnKeys = new List<PlayerKey>(PlayerKey.SlotCount);
        private long m_respawnRequestSequence;

        /// <summary>
        /// 客户端睡眠点击门控（每次睡眠最多一条 PLAYER_WAKE_REQUEST）。纯逻辑在
        /// Net/PlayerSleepNet.cs：门槛 10 s / 强制睡眠不可唤醒 / 时钟安全余量都在那里。
        /// </summary>
        private readonly PlayerSleepGate m_sleepGate = new PlayerSleepGate();
        /// <summary>每帧遍历睡眠键的复用缓冲。</summary>
        private readonly List<PlayerKey> m_sleepKeys = new List<PlayerKey>(PlayerKey.SlotCount);
        private long m_sleepRequestSequence;

        private readonly List<PlayerVitalEventSnapshot> m_pendingEvents =
            new List<PlayerVitalEventSnapshot>(4);
        private readonly List<PlayerVitalEventSnapshot> m_eventBatch =
            new List<PlayerVitalEventSnapshot>(MaxEventsPerMessage);
        private readonly Dictionary<int, uint> m_senderSequences = new Dictionary<int, uint>();
        private readonly Dictionary<int, uint> m_senderEventSequences = new Dictionary<int, uint>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> m_reportedOnce = new HashSet<string>(StringComparer.Ordinal);

        private uint m_reportSequence;
        private uint m_eventSequence;
        private long m_lastReportAttemptMs;
        private long m_lastReportSentMs;
        private long m_lastSummaryMs;
        private long m_lastLocalSimMs;
        private long m_lastSuppressMs;
        private bool m_forceSend;

        // ---------------------------------------------------------------- 计数（10 s 状态行）
        // 主机
        private long m_vitalsSent;
        private long m_vitalsApplied;
        private long m_broadcast;
        private long m_deathEventsSent;
        private long m_respawnEventsSent;
        private long m_reportsSent;
        private long m_sendFailures;
        private long m_captureFailures;
        private long m_pollFailures;
        private long m_encodeFailures;
        private long m_eventSendFailures;
        private long m_hostRespawnFailures;
        private long m_noRecipients;
        // 客户端
        private long m_vitalsRecv;
        private long m_clientVitalsApplied;
        private long m_vitalsStale;
        private long m_vitalsMalformed;
        private long m_vitalsUnchanged;
        private long m_vitalsNoAvatar;
        private long m_applyFailures;
        private long m_deathApplied;
        private long m_respawnApplied;
        private long m_clientLocalSimDisabled;
        private long m_notReadyDrops;
        private long m_decodeFailures;
        private long m_suppressNew;
        private long m_suppressedTotal;
        private long m_suppressFailures;
        private long m_hudRefreshes;
        private long m_hudRefreshFailures;
        // 两端
        private long m_clientReportRejected;
        // 死亡屏 / 复活请求（客户端）
        private long m_deathScreensShown;
        private long m_respawnTapCaptured;
        private long m_respawnReqSent;
        private long m_respawnReqSendFailures;
        private long m_respawnReqTimeouts;
        private long m_respawnResultRecv;
        private long m_respawnAccepted;
        private long m_respawnRejected;
        private long m_respawnSuppressed;
        private long m_respawnResultSendFailures;
        private readonly Dictionary<string, long> m_respawnRejectReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_respawnSuppressedReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
        // 死亡屏 / 复活请求（主机）
        private long m_respawnReqRecv;
        private long m_respawnReqAccepted;
        private long m_respawnReqRejected;
        private long m_respawnReqMalformed;
        private long m_respawnReqWrongDirection;
        private readonly Dictionary<string, long> m_hostRespawnRejectReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
        // 睡眠表现 / 点击唤醒（客户端 + 主机 H3 分屏镜像）
        private long m_sleepOverlayApplied;
        private long m_sleepZzz;
        private long m_sleepBlackout;
        private long m_sleepForced;
        private long m_sleepManualWakeDisabled;
        private long m_sleepLegacy;
        private long m_sleepMirrorApplied;
        private long m_sleepMirrorCleared;
        private long m_sleepTapCaptured;
        private long m_sleepSuppressed;
        private long m_sleepMissing;
        private long m_sleepPresentationFailures;
        private readonly Dictionary<string, long> m_sleepSuppressedReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private long m_sleepWakeReqSent;
        private long m_sleepWakeReqSendFailures;
        private long m_sleepWakeConfirmed;
        private long m_sleepWakeReqRecv;
        private long m_sleepWakeReqAccepted;
        private long m_sleepWakeReqRejected;
        private long m_sleepWakeReqMalformed;
        private long m_sleepWakeReqWrongDirection;
        private readonly Dictionary<string, long> m_sleepWakeRejectReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>
        /// 旧自动复活路径已删除：状态行固定输出 <c>autoRespawnRemoved=1</c>，防止有人把
        /// 2.5 秒计时器加回来而没有被发现（源码守卫 + 状态行双重痕迹）。
        /// </summary>
        private const int AutoRespawnRemoved = 1;

        public PlayerVitalsModule(Func<double> now)
        {
            m_now = now ?? throw new ArgumentNullException(nameof(now));
            m_descriptor = new ModuleDescriptor(ModuleIdText,
                "玩家生命 / 体征 / 死亡状态同步（主机模拟并下发，客户端只跟随）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "player.vitals" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => ModuleIdText;

        // ---------------------------------------------------------------- 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Error("没有拿到传输端口（transport 能力缺失）→ 玩家生命 / 体征同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Error("没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端角色，不启用" +
                    "（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_messages) || m_messages == null)
            {
                Error("没有拿到消息注册表（IMessageRegistryPort）→ 收不到主机下发的生命 / 体征");
                return;
            }
            if (!context.TryGetPort(out m_port) || m_port == null)
            {
                Error("没有拿到生命 / 体征引擎端口（IPlayerVitalsPort）→ 不启用" +
                    "（组合根必须注册 PlayerVitalsReplica；不直接读写 Game 组件）");
                return;
            }
            if (!context.TryGetPort(out m_sleepPort) || m_sleepPort == null)
            {
                Error("没有拿到睡眠引擎端口（IPlayerSleepPort）→ 睡眠表现 / 点击唤醒不启用" +
                    "（组合根必须用 PlayerVitalsReplica 同时注册 IPlayerVitalsPort 与 " +
                    "IPlayerSleepPort；模块层不直接读写 Game 组件）");
                return;
            }
            if (!context.TryGetPort(out m_broadcastPort) || m_broadcastPort == null)
            {
                // 仅用于诊断在线人数；真正的下发走 ITransportPort.TrySendTo（按 clientId 定向）。
                Error("没有拿到玩家端广播端口（IPeerBroadcastPort）→ 无法核对在线玩家端人数，" +
                    "生命 / 体征同步不启用（不猜发送目标）");
                return;
            }
            m_transport.ClientJoined += OnClientJoined;
            m_transport.ClientLeft += OnClientLeft;
            m_messages.Registry.RegisterModule(this);
            m_attached = true;
            Info("玩家生命 / 体征同步就绪：主机模拟全部角色并 5 Hz 变化 / 1 Hz 心跳下发（死亡与复活立刻），" +
                "客户端只应用、本机生命 / 体征 / 睡眠 / 病程 / 火焰模拟被摘掉；客户端不再上报");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.PlayerVitalsReport, V.PlayerVitalsReport.Parser,
                new DelegateMessageHandler<V.PlayerVitalsReport>(OnReportMessage), ModuleId);
            registry.Register((int)MessageType.PlayerVitalsEvents, V.PlayerVitalsEvents.Parser,
                new DelegateMessageHandler<V.PlayerVitalsEvents>(OnEventsMessage), ModuleId);
            registry.Register((int)MessageType.PlayerRespawnRequest, R.PlayerRespawnRequest.Parser,
                new DelegateMessageHandler<R.PlayerRespawnRequest>(OnRespawnRequestMessage), ModuleId);
            registry.Register((int)MessageType.PlayerRespawnResult, R.PlayerRespawnResult.Parser,
                new DelegateMessageHandler<R.PlayerRespawnResult>(OnRespawnResultMessage), ModuleId);
            registry.Register((int)MessageType.PlayerWakeRequest, V.PlayerWakeRequest.Parser,
                new DelegateMessageHandler<V.PlayerWakeRequest>(OnWakeRequestMessage), ModuleId);
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
                try { m_port.EnableLocalSimulation(out _); } catch (Exception) { }
            }
            m_clients.Clear();
            m_hostCharacters.Clear();
            m_clientCharacters.Clear();
            m_pendingEvents.Clear();
            m_respawnGate.Clear();
            m_terminalDeathHandled.Clear();
            m_sleepGate.Clear();
            m_sleepKeys.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_lastProblemMs.Clear();
            m_reportedOnce.Clear();
            m_messages = null;
            m_port = null;
            m_sleepPort = null;
            m_broadcastPort = null;
            m_control = null;
            m_transport = null;
            m_attached = false;
            LogInfo("玩家生命 / 体征同步下线（" + reason + "）：" + summary);
        }

        // ---------------------------------------------------------------- 每帧

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            long nowMs = (long)(tick.Now * 1000.0);
            bool hosting = m_control != null && m_control.IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            bool inWorld = tick.IsConnected;
            if (hosting && !m_wasHosting) BeginHosting();
            if (!hosting && m_wasHosting) EndHosting(connected);
            if (connected && !m_wasConnected) OnConnectionEstablished(hosting);
            if (!connected && m_wasConnected) OnConnectionLost(hosting);
            m_wasHosting = hosting;
            m_wasConnected = connected;

            if (!hosting && connected && inWorld && !m_worldReady &&
                m_transport != null && m_transport.LocalClientId >= 0)
                OnClientWorldReady();
            if (!inWorld) m_worldReady = false;

            if (hosting) TickHost(nowMs, tick.DeltaTime);
            else if (connected && inWorld) TickClient(nowMs, tick.DeltaTime);
            else if (inWorld && !hosting && m_port != null)
            {
                // 没有可跟随的主机（断线 / 还没进世界）：本机角色必须恢复自己的模拟，
                // 否则离开房间后角色永远不动 / 不掉血。
                TryEnableLocalSimulation("没有主机可跟随");
            }
            LogSummaryIfDue(nowMs);
        }

        private void TickHost(long nowMs, float deltaTime)
        {
            // H3 第三分屏（replace 模式）的睡眠表现镜像：每帧写（帧末，晚于
            // ComponentScreenOverlays.Update 的每帧 Reset）。必须在客户端数 == 0 的早退之前
            // 也跑：客户端离开后 H3 的 SleepFactor 要写回 0，否则第三分屏控件永远隐藏。
            TickHostMirrorSleep(deltaTime);
            if (m_clients.Count == 0)
            {
                if (m_broadcastPort != null && m_broadcastPort.PlayerPeerCount > 0)
                {
                    LogOnce("clients-untracked",
                        "主机有 " + m_broadcastPort.PlayerPeerCount + " 个在线玩家端，但模块没有它们的 " +
                        "clientId（加入事件早于模块装配）→ 生命 / 体征本轮不下发（不猜目标）");
                }
                m_pendingEvents.Clear();
                return;
            }

            TickHostEdges(nowMs);
            FlushPendingEvents();
            TickHostReport(nowMs);
        }

        private void TickClient(long nowMs, float deltaTime)
        {
            if (!m_worldReady) return;
            if (nowMs - m_lastLocalSimMs >= LocalSimIntervalMs)
            {
                m_lastLocalSimMs = nowMs;
                EnsureLocalSimulationDisabled();
            }
            if (nowMs - m_lastSuppressMs >= SuppressIntervalMs)
            {
                m_lastSuppressMs = nowMs;
                TickRemoteSuppression();
            }
            // 体征 HUD 必须**每帧**刷新：ComponentVitalStats.Update 已被摘掉，
            // 而 ComponentScreenOverlays.Update 每帧把 IceFactor 清 0（温度条贴图内部去重）。
            // 这里只写显示属性，不写组件字段、不跑模拟。
            int hudRefreshed = m_port.RefreshLocalPresentation(out string hudReason);
            m_hudRefreshes += hudRefreshed;
            if (!string.IsNullOrEmpty(hudReason))
            {
                m_hudRefreshFailures++;
                Problem("hud-refresh", "本机体征 HUD 刷新失败：" + hudReason +
                    "（本拍刷新 " + hudRefreshed + " 个槽位；不静默跳过）");
            }
            // 睡眠表现也必须**每帧**刷新：ComponentScreenOverlays.Update（UpdateOrder.Reset）
            // 每帧把 Blackout / FloatingMessage / Message 清 0，而客户端本机角色的
            // ComponentSleep.Update 已被摘掉且绝不重新启用 —— 只写显示层，不做唤醒决定。
            PlayerSleepPresentationCounts sleepCounts =
                m_sleepPort.RefreshSleepPresentation(deltaTime, out string sleepReason);
            AccumulateSleepCounts(in sleepCounts);
            if (!string.IsNullOrEmpty(sleepReason))
            {
                m_sleepPresentationFailures++;
                Problem("sleep-refresh", "本机睡眠表现刷新失败：" + sleepReason +
                    "（本拍应用 " + sleepCounts.Applied + " 个槽位；不静默跳过）");
            }
            TickClientSleep(nowMs);
            TickClientRespawn(nowMs);
        }

        // ---------------------------------------------------------------- 主机：边沿 / 复活

        /// <summary>
        /// 每帧廉价轮询主机模拟的每个角色：死亡 / 复活（含"死亡后实体被替换"）边沿 →
        /// 立刻排事件 + 下一次上报强制发出。玩家键 = (0, slot) 是主机本机角色，
        /// (clientId, slot) 是客户端化身。
        /// </summary>
        private void TickHostEdges(long nowMs)
        {
            m_samples.Clear();
            if (m_port.PollAuthoritySlots(m_samples, out string reason) <= 0)
            {
                m_pollFailures++;
                Problem("poll", "主机角色轮询失败：" + (reason ?? "端口没有给原因") +
                    " → 本帧不检测死亡 / 复活边沿");
                return;
            }
            for (int i = 0; i < m_samples.Count; i++)
            {
                PlayerVitalsSlotSample sample = m_samples[i];
                if (!sample.Key.IsValid) continue;
                if (sample.Key.ClientId > 0 && !m_clients.Contains(sample.Key.ClientId))
                    continue;   // 已经离开的客户端：不拿它残留的化身当权威角色
                HostCharacterState state = GetOrCreateHostCharacter(sample.Key);
                if (!sample.HasHealth)
                {
                    // 不可观测 = 不做任何边沿判断（绝不把"读不到"当成死亡或复活）。
                    continue;
                }
                if (!state.HasSample)
                {
                    state.HasSample = true;
                    state.Dead = sample.Dead;
                    state.Token = sample.EntityToken;
                    // 首次观察时客户端化身已经死了（模块晚装配 / 主机重载）：仍要发一条
                    // Death 边沿，让 owner 有机会点击请求复活。**不排任何自动复活**。
                    if (sample.Dead && sample.Key.ClientId > 0)
                        QueueHostDeath(sample, state, nowMs);
                    else if (!sample.Dead)
                        state.DeathAtMs = 0;
                    continue;
                }
                bool deathEdge = sample.Dead && !state.Dead;
                bool respawnEdge = !sample.Dead && state.Dead;
                state.Dead = sample.Dead;
                state.Token = sample.EntityToken;
                if (deathEdge) QueueHostDeath(sample, state, nowMs);
                else if (respawnEdge) QueueHostRespawn(sample, state);
            }
        }

        private void QueueHostDeath(in PlayerVitalsSlotSample sample, HostCharacterState state,
            long nowMs)
        {
            state.Revision = NextRevision(state.Revision);
            state.DeathAtMs = nowMs;
            state.LastRequestAtMs = 0;
            string cause = sample.CauseOfDeath;
            m_pendingEvents.Add(new PlayerVitalEventSnapshot(sample.Key, state.Revision,
                PlayerVitalEventKind.Death, cause, false, Vector3.Zero));
            // 事件之后的报告必须 revision 更大：强制下一次抓取递增。
            state.HasSent = false;
            m_deathEventsSent++;
            m_forceSend = true;
            Info("主机角色 " + sample.Key + " 死亡（rev=" + state.Revision + "，死因=" +
                (string.IsNullOrEmpty(cause) ? "未记录" : cause) + "）→ 立刻发死亡事件" +
                (sample.Key.ClientId > 0
                    ? "；主机**不再自动复活**，等待该玩家端点击后发 PLAYER_RESPAWN_REQUEST"
                    : "（主机本机角色由引擎 PlayerData 死亡 / 点击复活流程处理）"));
        }

        private void QueueHostRespawn(in PlayerVitalsSlotSample sample, HostCharacterState state)
        {
            state.DeathAtMs = 0;
            state.LastRequestAtMs = 0;
            if (!sample.HasFinitePosition)
            {
                Problem("respawn-no-position:" + sample.Key,
                    "主机角色 " + sample.Key + " 复活了，但 ComponentBody.Position 不可用 → 不发复活事件" +
                    "（不拿零/旧位置顶替；随后那份 revision 更大的上报仍会把活着带给客户端）");
                state.HasSent = false;
                m_forceSend = true;
                return;
            }
            state.Revision = NextRevision(state.Revision);
            m_pendingEvents.Add(new PlayerVitalEventSnapshot(sample.Key, state.Revision,
                PlayerVitalEventKind.Respawn, string.Empty, true, sample.Position));
            state.HasSent = false;
            m_respawnEventsSent++;
            m_forceSend = true;
            Info("主机角色 " + sample.Key + " 复活（rev=" + state.Revision + " · 落点 " +
                sample.Position + "）→ 立刻发复活事件");
        }

        private void FlushPendingEvents()
        {
            if (m_pendingEvents.Count == 0) return;
            if (m_clients.Count == 0) return;
            int index = 0;
            int sentClients = 0;
            int failedClients = 0;
            while (index < m_pendingEvents.Count)
            {
                int take = Math.Min(MaxEventsPerMessage, m_pendingEvents.Count - index);
                m_eventBatch.Clear();
                for (int i = 0; i < take; i++) m_eventBatch.Add(m_pendingEvents[index + i]);
                m_eventSequence = NextRevision(m_eventSequence);
                byte[] payload = PlayerVitalsCodec.EncodeEvents(m_eventSequence, m_eventBatch,
                    out int dropped, out string error);
                if (payload == null)
                {
                    m_encodeFailures++;
                    Problem("encode-events", "死亡 / 复活事件编码失败（" + (error ?? "未知") +
                        "）→ 本批丢弃，计数 " + m_encodeFailures);
                    break;
                }
                if (dropped > 0)
                {
                    Problem("events-overflow", "死亡 / 复活事件超过单条上限 " +
                        PlayerVitalsLimits.MaxReportEntries + " → 丢弃 " + dropped +
                        " 条（不静默截断）");
                }
                CollectClients();
                for (int c = 0; c < m_clientList.Count; c++)
                {
                    int clientId = m_clientList[c];
                    if (!m_transport.TrySendTo(clientId, MessageType.PlayerVitalsEvents, payload))
                    {
                        failedClients++;
                        m_eventSendFailures++;
                        continue;
                    }
                    sentClients++;
                }
                index += take;
            }
            // 有端没发出去就保留事件，下一帧重试（同 revision 的重复事件由客户端按 revision 去重）。
            if (failedClients > 0)
            {
                Problem("events-send", "死亡 / 复活事件有 " + failedClients +
                    " 个玩家端发送失败 → 保留并下一帧重试（客户端按 revision 去重，不重复应用）");
                return;
            }
            if (sentClients > 0) m_pendingEvents.Clear();
        }

        // ---------------------------------------------------------------- 主机：权威状态下发

        /// <summary>
        /// 抓主机模拟的全部角色 → 逐键递增 revision（只在值变化 / 首份 / 边沿后）→
        /// 给每个在线玩家端各发一份报告（自己的 owner 条目 + 其它角色的观察者条目）。
        /// </summary>
        private void TickHostReport(long nowMs)
        {
            bool due = m_forceSend || nowMs - m_lastReportAttemptMs >= ReportIntervalMs;
            bool heartbeat = nowMs - m_lastReportSentMs >= HeartbeatMs;
            if (!due && !heartbeat) return;

            m_authorityCapture.Clear();
            int captured = m_port.CaptureAuthorityVitals(m_authorityCapture, out string reason);
            if (captured <= 0 && m_authorityCapture.Count == 0)
            {
                m_captureFailures++;
                Problem("capture", "主机角色生命 / 体征抓取失败：" + (reason ?? "端口没有给原因") +
                    " → 本拍不下发（绝不发空值 / 别的角色的值）");
                m_lastReportAttemptMs = nowMs;
                return;
            }

            m_sendEntries.Clear();
            bool changed = false;
            for (int i = 0; i < m_authorityCapture.Count; i++)
            {
                PlayerVitalsSnapshot snapshot = m_authorityCapture[i];
                if (snapshot.Key.ClientId > 0 && !m_clients.Contains(snapshot.Key.ClientId))
                    continue;   // 已经离开的客户端：不把残留化身下发给别人
                HostCharacterState state = GetOrCreateHostCharacter(snapshot.Key);
                // 强制睡眠（昏迷）只按**进入边沿**计数：睡眠期间 elapsed 一直在涨，
                // 不能每拍 +1。
                bool forcedSleep = snapshot.Sleeping && snapshot.ForcedSleep;
                if (forcedSleep && !state.WasForcedSleep) m_sleepForced++;
                state.WasForcedSleep = forcedSleep;
                if (!state.HasSent || snapshot.HasMeaningfulChangeFrom(state.LastSent, out _))
                {
                    state.Revision = NextRevision(state.Revision);
                    state.HasSent = true;
                    state.LastSent = snapshot.WithRevision(state.Revision);
                    changed = true;
                    m_vitalsApplied++;
                }
                PlayerVitalsSnapshot withRevision = snapshot.WithRevision(state.Revision);
                state.LastSent = withRevision;
                m_sendEntries.Add(withRevision);
            }
            if (m_sendEntries.Count == 0)
            {
                m_lastReportAttemptMs = nowMs;
                return;
            }
            if (!changed && !heartbeat && !m_forceSend)
            {
                // 这一拍没有要发的东西：把"上次尝试"推进到此刻，让完整抓取保持 5 Hz。
                m_lastReportAttemptMs = nowMs;
                return;
            }

            m_lastReportAttemptMs = nowMs;
            m_reportSequence = NextRevision(m_reportSequence);
            byte[] payload = PlayerVitalsCodec.EncodeReport(m_reportSequence, m_sendEntries,
                out int dropped, out string error);
            if (payload == null)
            {
                m_encodeFailures++;
                Problem("encode-report", "生命 / 体征上报编码失败（" + (error ?? "未知") +
                    "）→ 本拍不发（绝不夹紧 / 补默认值）");
                return;
            }
            if (dropped > 0)
            {
                Problem("report-overflow", "生命 / 体征上报超过单份上限 " +
                    PlayerVitalsLimits.MaxReportEntries + " 条 → 丢弃 " + dropped + " 条");
            }

            CollectClients();
            int delivered = 0;
            int failures = 0;
            for (int c = 0; c < m_clientList.Count; c++)
            {
                int clientId = m_clientList[c];
                if (!m_transport.TrySendTo(clientId, MessageType.PlayerVitalsReport, payload))
                {
                    failures++;
                    m_sendFailures++;
                    continue;
                }
                delivered++;
                // owner 条目 = 这个端自己的角色；其余 = 观察者转发（姿势 / HUD）。
                for (int e = 0; e < m_sendEntries.Count; e++)
                {
                    if (m_sendEntries[e].Key.ClientId == clientId) m_vitalsSent++;
                    else m_broadcast++;
                }
            }
            if (delivered > 0)
            {
                m_reportsSent++;
                m_lastReportSentMs = nowMs;
            }
            else
            {
                m_noRecipients++;
            }
            if (failures > 0)
            {
                // 失败不要每帧重试：保持 5 Hz / 心跳节奏（事件流有自己的重试）。
                Problem("send-report", "生命 / 体征上报有 " + failures +
                    " 个玩家端发送失败 → 下一拍（≤5 Hz）重试（不缓存陈旧值）");
            }
            m_forceSend = false;
        }

        // ---------------------------------------------------------------- 接收：方向裁决

        private void OnReportMessage(MessageContext context, V.PlayerVitalsReport message)
        {
            if (!m_attached) return;
            if (m_control != null && m_control.IsHosting)
            {
                // 主机是唯一权威：**任何**客户端上报都必须拒绝。
                m_clientReportRejected++;
                Problem("client-report", "主机收到 clientId=" + context.ClientId +
                    " 的生命 / 体征上报（客户端不得上报自己的状态）→ 整条拒绝，共 " +
                    CountEntries(message) + " 条");
                return;
            }
            if (context.ClientId != 0)
            {
                m_clientReportRejected++;
                Problem("client-report-sender", "客户端收到来自 clientId=" + context.ClientId +
                    " 的生命 / 体征上报（只有主机能下发）→ 整条拒绝，共 " +
                    CountEntries(message) + " 条");
                return;
            }
            if (!m_worldReady)
            {
                m_notReadyDrops++;
                return;
            }
            if (!PlayerVitalsCodec.TryConvertReport(message, out uint sequence,
                    out List<PlayerVitalsSnapshot> entries, out string error))
            {
                m_decodeFailures++;
                m_vitalsMalformed++;
                Problem("malformed-report", "主机下发的生命 / 体征上报解码失败：" + error +
                    "（整条丢弃）");
                return;
            }
            int sender = 0;
            if (m_senderSequences.TryGetValue(sender, out uint lastSequence) &&
                sequence <= lastSequence)
            {
                m_vitalsStale += entries.Count;
                Problem("stale-report",
                    "丢弃主机的陈旧上报（sequence=" + sequence + " ≤ 已处理 " + lastSequence +
                    "，共 " + entries.Count + " 条）");
                return;
            }
            m_senderSequences[sender] = sequence;

            for (int i = 0; i < entries.Count; i++)
            {
                m_vitalsRecv++;
                PlayerVitalsSnapshot snapshot = entries[i];
                if (!snapshot.TryValidate(out string itemError))
                {
                    m_vitalsMalformed++;
                    Problem("malformed-entry:" + snapshot.Key,
                        "主机下发的第 " + (i + 1) + " 条状态非法：" + itemError + " → 丢弃该条");
                    continue;
                }
                ClientKeyState state = GetOrCreateClientCharacter(snapshot.Key);
                if (state.LastRevision > 0 && snapshot.Revision < state.LastRevision)
                {
                    m_vitalsStale++;
                    Problem("stale-entry:" + snapshot.Key,
                        "丢弃玩家键 " + snapshot.Key + " 的陈旧状态（revision=" +
                        snapshot.Revision + " < 已应用 " + state.LastRevision + "）");
                    continue;
                }
                bool local = IsLocalKey(snapshot.Key);
                if (local && m_sleepPort != null)
                {
                    // 心跳（同 revision）也携带新的 sleep_elapsed：先校准客户端外推起点，
                    // 再决定这一条是否值得应用到引擎组件。
                    if (!m_sleepPort.NoteLocalSleepReport(snapshot.Key, in snapshot,
                            out string sleepNoteReason))
                    {
                        Problem("sleep-note:" + snapshot.Key, "记录本机睡眠状态失败：" +
                            (sleepNoteReason ?? "端口没有给原因") +
                            " → 本拍睡眠表现不跟随这一条（计数后继续）");
                    }
                }
                if (state.LastRevision > 0 && snapshot.Revision == state.LastRevision)
                {
                    m_vitalsUnchanged++;
                    continue;
                }
                if (local) EnsureLocalSimulationDisabled();
                string reason;
                PlayerVitalsApplyStatus status = local
                    ? m_port.ApplyLocalVitals(snapshot.Key, in snapshot, out reason)
                    : m_port.ApplyRemoteVitals(snapshot.Key, in snapshot, out reason);
                if (status == PlayerVitalsApplyStatus.NoAvatar)
                {
                    m_vitalsNoAvatar++;
                    Problem("no-avatar:" + snapshot.Key, "收到玩家键 " + snapshot.Key +
                        " 的生命 / 体征，但本机没有它的" +
                        (local ? "本机角色" : "联机角色") + "（" + reason + "）→ 丢弃该条" +
                        "（不拿别的角色顶替；角色就绪后会按下一拍心跳重新接收）");
                    continue;
                }
                if (status == PlayerVitalsApplyStatus.Rejected)
                {
                    m_applyFailures++;
                    Problem("apply-rejected:" + snapshot.Key, "应用玩家键 " + snapshot.Key +
                        " 的生命 / 体征被拒绝：" + (reason ?? "端口没有给原因"));
                    continue;
                }
                bool wasSleeping = state.HasApplied && state.LastApplied.Sleeping;
                state.HasApplied = true;
                state.LastApplied = snapshot;
                state.LastRevision = snapshot.Revision;
                m_clientVitalsApplied++;
                if (local)
                {
                    if (snapshot.Sleeping)
                    {
                        if (m_sleepGate.OnSleep(snapshot.Key, snapshot.SleepStartTime))
                        {
                            Info("本机角色 " + snapshot.Key + " 进入睡眠（主机权威：start=" +
                                snapshot.SleepStartTime.ToString("0.##") + " · " +
                                (snapshot.ForcedSleep
                                    ? "强制 / 昏迷：不提供唤醒路径"
                                    : "允许手动唤醒：>10 s 点击可唤醒") + "）→ 开始睡眠表现");
                        }
                        if (snapshot.ForcedSleep && !wasSleeping) m_sleepForced++;
                    }
                    else
                    {
                        if (m_sleepGate.OnWake(snapshot.Key))
                        {
                            m_sleepWakeConfirmed++;
                            Info("主机已接受本机角色 " + snapshot.Key +
                                " 的唤醒请求（权威上报 sleeping=false）→ 黑屏淡出");
                        }
                    }
                    // 死亡时睡眠期结束（尸体不能被点击唤醒），但不算"唤醒成功"。
                    if (snapshot.Dead) m_sleepGate.OnWake(snapshot.Key);
                    // 死亡屏 / 复活门由**状态**也兜底一次：Death 事件丢失或晚到时，
                    // 死着的权威快照同样要能进入"点击请求复活"路径；活着的快照清掉死亡门。
                    if (snapshot.Dead)
                        ArmLocalDeath(snapshot.Key, snapshot.Revision, snapshot.CauseOfDeath,
                            NowMs(), isEventEdge: false);
                    else
                        ClearLocalDeath(snapshot.Key, "权威状态显示已复活");
                }
            }
        }

        private void OnEventsMessage(MessageContext context, V.PlayerVitalsEvents message)
        {
            if (!m_attached) return;
            if (m_control != null && m_control.IsHosting)
            {
                m_clientReportRejected++;
                Problem("client-events", "主机收到 clientId=" + context.ClientId +
                    " 的死亡 / 复活事件（客户端不得上报状态）→ 整条拒绝，共 " +
                    CountEvents(message) + " 条");
                return;
            }
            if (context.ClientId != 0)
            {
                m_clientReportRejected++;
                Problem("client-events-sender", "客户端收到来自 clientId=" + context.ClientId +
                    " 的死亡 / 复活事件（只有主机能下发）→ 整条拒绝，共 " +
                    CountEvents(message) + " 条");
                return;
            }
            if (!m_worldReady)
            {
                m_notReadyDrops++;
                return;
            }
            if (!PlayerVitalsCodec.TryConvertEvents(message, out uint sequence,
                    out List<PlayerVitalEventSnapshot> entries, out string error))
            {
                m_decodeFailures++;
                m_vitalsMalformed++;
                Problem("malformed-events", "主机下发的死亡 / 复活事件解码失败：" + error +
                    "（整条丢弃）");
                return;
            }
            int sender = 0;
            if (m_senderEventSequences.TryGetValue(sender, out uint lastSequence) &&
                sequence <= lastSequence)
            {
                m_vitalsStale += entries.Count;
                Problem("stale-events", "丢弃主机的陈旧死亡 / 复活事件（sequence=" + sequence +
                    " ≤ 已处理 " + lastSequence + "，共 " + entries.Count + " 条）");
                return;
            }
            m_senderEventSequences[sender] = sequence;

            for (int i = 0; i < entries.Count; i++)
            {
                PlayerVitalEventSnapshot evt = entries[i];
                if (!evt.TryValidate(out string itemError))
                {
                    m_vitalsMalformed++;
                    Problem("malformed-event:" + evt.Key,
                        "主机下发的第 " + (i + 1) + " 条事件非法：" + itemError + " → 丢弃该条");
                    continue;
                }
                ClientKeyState state = GetOrCreateClientCharacter(evt.Key);
                if (state.LastEventRevision > 0 && evt.Revision <= state.LastEventRevision)
                {
                    if (evt.Revision < state.LastEventRevision) m_vitalsStale++;
                    else m_vitalsUnchanged++;
                    continue;
                }
                bool local = IsLocalKey(evt.Key);
                if (local) EnsureLocalSimulationDisabled();
                string reason;
                PlayerVitalsApplyStatus status = local
                    ? m_port.ApplyLocalVitalEvent(in evt, out reason)
                    : m_port.ApplyVitalEvent(in evt, out reason);
                if (status == PlayerVitalsApplyStatus.NoAvatar)
                {
                    m_vitalsNoAvatar++;
                    Problem("event-no-avatar:" + evt.Key, "收到玩家键 " + evt.Key + " 的 " +
                        evt.Kind + " 事件，但本机没有它的" + (local ? "本机角色" : "联机角色") +
                        "（" + reason + "）→ 丢弃（不猜目标）");
                    continue;
                }
                if (status == PlayerVitalsApplyStatus.Rejected)
                {
                    m_applyFailures++;
                    Problem("event-rejected:" + evt.Key, "应用玩家键 " + evt.Key + " 的 " +
                        evt.Kind + " 事件被拒绝：" + (reason ?? "端口没有给原因"));
                    continue;
                }
                state.LastEventRevision = evt.Revision;
                if (evt.Revision > state.LastRevision) state.LastRevision = evt.Revision;
                if (evt.IsDeath)
                {
                    m_deathApplied++;
                    // 死亡 = 睡眠期结束：清掉唤醒门（尸体不能被点击唤醒）。
                    if (local) m_sleepGate.OnWake(evt.Key);
                    if (local) ArmLocalDeath(evt.Key, evt.Revision, evt.CauseOfDeath, NowMs(), isEventEdge: true);
                    Info("主机裁决玩家键 " + evt.Key + " 死亡（rev=" + evt.Revision + "，死因=" +
                        (string.IsNullOrEmpty(evt.CauseOfDeath) ? "未记录" : evt.CauseOfDeath) +
                        "）→ 客户端显示死亡提示" + (local ? "（本机玩家，不删实体）" : "（化身姿势）"));
                }
                else
                {
                    m_respawnApplied++;
                    if (local)
                    {
                        m_sleepGate.OnWake(evt.Key);
                        ClearLocalDeath(evt.Key, "主机复活事件");
                    }
                    Info("主机裁决玩家键 " + evt.Key + " 复活（rev=" + evt.Revision + " · 落点=" +
                        (evt.HasPosition ? evt.Position.ToString() : "未提供") +
                        "）→ 客户端按主机落点复位" + (local ? "（本机玩家）" : "（化身）"));
                }
            }
        }

        // ---------------------------------------------------------------- 客户端：死亡屏 / 复活请求

        /// <summary>
        /// 登记一次本机角色死亡：显示屏幕由端口在 ApplyLocalVitals / ApplyLocalVitalEvent
        /// 里完成（引擎等价的 YOU HAVE DIED + DeathCamera），这里只负责 1.5 秒门槛、每次死亡
        /// 最多一个请求、结果 / 超时后不再响应的状态机。重复的死亡快照 / 事件不会重复计数。
        /// </summary>
        private void ArmLocalDeath(PlayerKey key, uint revision, string cause, long nowMs,
            bool isEventEdge)
        {
            if (m_port == null) return;
            PlayerRespawnMode mode = m_port.GetRespawnMode(out string modeReason);
            if (mode == PlayerRespawnMode.Unknown)
            {
                Problem("respawn-mode:" + key, "读不到世界复活模式（" + (modeReason ?? "端口没有给原因") +
                    "）→ 显示死亡屏但不提供请求路径（不猜模式）");
            }
            if (!m_respawnGate.OnDeath(key, revision, mode, nowMs, isEventEdge)) return;
            m_terminalDeathHandled.Remove(key);
            m_deathScreensShown++;
            Info("本机角色 " + key + " 死亡屏已显示（rev=" + revision + "，模式=" +
                PlayerRespawnRules.Describe(mode) + "，死因=" +
                (string.IsNullOrEmpty(cause) ? "未记录" : cause) + "）：引擎 1.5 秒点击门槛后" +
                (PlayerRespawnRules.AllowsRequest(mode)
                    ? "由玩家点击发一次复活请求（主机不再自动复活）"
                    : "点击只走引擎等价终止路径，不发复活请求"));
        }

        private void ClearLocalDeath(PlayerKey key, string why)
        {
            m_respawnGate.OnRespawn(key);
            m_terminalDeathHandled.Remove(key);
        }

        /// <summary>
        /// 每帧：死亡屏上的输入（tap / click / key）→ 1.5 秒门槛 → 每次死亡最多一个请求。
        /// 门槛前 / 已发过 / 模式禁止 / 已解决都明确计数（<c>respawnSuppressed</c>）。
        /// </summary>
        private void TickClientRespawn(long nowMs)
        {
            if (m_respawnGate.Count == 0) return;
            m_respawnKeys.Clear();
            IReadOnlyList<PlayerKey> keys = m_respawnGate.Keys;
            for (int i = 0; i < keys.Count; i++) m_respawnKeys.Add(keys[i]);
            for (int i = 0; i < m_respawnKeys.Count; i++)
            {
                PlayerKey key = m_respawnKeys[i];
                if (!m_respawnGate.TryGet(key, out PlayerRespawnPending pending)) continue;
                PlayerRespawnGateAction timeout = m_respawnGate.OnTick(key, nowMs,
                    out string timeoutReason);
                if (timeout == PlayerRespawnGateAction.Timeout)
                {
                    m_respawnReqTimeouts++;
                    Problem("respawn-timeout:" + key,
                        "复活请求 " + pending.RequestSequence + " 在 " +
                        PlayerRespawnRules.RequestTimeoutMs + " ms 内没有收到主机结果（" +
                        timeoutReason + "）→ 本次死亡不再重发（每次死亡最多一个请求）");
                }
                if (!m_port.TryPollLocalDeathTap(key, out bool tapped, out string pollReason))
                {
                    Problem("respawn-tap-poll:" + key, "读取死亡屏输入失败：" +
                        (pollReason ?? "端口没有给原因") + " → 本帧不判定点击（不猜输入）");
                    continue;
                }
                if (!tapped) continue;
                m_respawnTapCaptured++;
                long sequence = m_respawnRequestSequence + 1;
                PlayerRespawnGateAction action = m_respawnGate.OnTap(key, nowMs, sequence,
                    out string tapReason);
                switch (action)
                {
                    case PlayerRespawnGateAction.SendRequest:
                        m_respawnRequestSequence = sequence;
                        if (!SendRespawnRequest(key, sequence, pending.DeathRevision,
                                pending.DeathAtMs))
                        {
                            m_respawnGate.OnSendFailed(key);
                        }
                        break;
                    case PlayerRespawnGateAction.SuppressedEarly:
                        Bump(m_respawnSuppressedReasons, tapReason);
                        m_respawnSuppressed++;
                        break;
                    case PlayerRespawnGateAction.SuppressedMode:
                        Bump(m_respawnSuppressedReasons, tapReason);
                        m_respawnSuppressed++;
                        if (!m_terminalDeathHandled.Add(key)) break;
                        if (!m_port.TryHandleTerminalDeathTap(key, pending.Mode,
                                out string terminalReason))
                        {
                            Problem("respawn-terminal:" + key,
                                "不可复活模式的点击处理失败（" +
                                PlayerRespawnRules.Describe(pending.Mode) + "）：" +
                                (terminalReason ?? "端口没有给原因"));
                        }
                        else
                        {
                            Info("不可复活模式 " + PlayerRespawnRules.Describe(pending.Mode) +
                                " 的点击已按引擎等价路径处理（玩家键 " + key + "）" +
                                (string.IsNullOrEmpty(terminalReason) ? "" : "：" + terminalReason));
                        }
                        break;
                    case PlayerRespawnGateAction.SuppressedAlreadyRequested:
                    case PlayerRespawnGateAction.SuppressedResolved:
                        Bump(m_respawnSuppressedReasons, tapReason);
                        m_respawnSuppressed++;
                        break;
                }
            }
        }

        private bool SendRespawnRequest(PlayerKey key, long sequence, uint deathRevision,
            long deathAtMs)
        {
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (localClientId <= 0 || m_messages == null)
            {
                m_respawnReqSendFailures++;
                Problem("respawn-send-nochannel", "无法发送复活请求（玩家键 " + key +
                    "）：" + (m_messages == null ? "没有消息层" : "本端传输编号=" + localClientId) +
                    " → 已释放本次请求（下一次点击可重试）");
                return false;
            }
            var data = new PlayerRespawnRequestData(localClientId, key.Slot, deathRevision,
                sequence, deathAtMs / 1000.0);
            R.PlayerRespawnRequest message;
            try
            {
                message = PlayerRespawnCodec.EncodeRequest(in data);
            }
            catch (Exception e)
            {
                m_respawnReqSendFailures++;
                Problem("respawn-send-encode", "复活请求编码失败（" + data.Describe() + "）：" +
                    e.GetType().Name + " " + e.Message);
                return false;
            }
            bool sent;
            try
            {
                sent = m_messages.Sender.Send(0, message);
            }
            catch (Exception e)
            {
                sent = false;
                Problem("respawn-send-exception", "复活请求发送异常（" + data.Describe() + "）：" +
                    e.GetType().Name + " " + e.Message);
            }
            if (!sent)
            {
                m_respawnReqSendFailures++;
                Problem("respawn-send-failed", "复活请求发送失败（" + data.Describe() +
                    "）→ 已释放本次请求（下一次点击可重试；通道恢复前不会假装成功）");
                return false;
            }
            m_respawnReqSent++;
            Info("死亡屏点击 → 已发送复活请求 " + data.Describe() +
                "（每次死亡只发一次；等待主机 PLAYER_RESPAWN_RESULT）");
            return true;
        }

        // ---------------------------------------------------------------- 客户端：睡眠点击 → 唤醒请求

        /// <summary>主机侧 H3 分屏镜像的睡眠表现（每帧；失败只计数 + 限流日志）。</summary>
        private void TickHostMirrorSleep(float deltaTime)
        {
            if (m_sleepPort == null) return;
            PlayerSleepPresentationCounts counts =
                m_sleepPort.RefreshMirrorSleepPresentation(deltaTime, out string reason);
            AccumulateSleepCounts(in counts);
            if (!string.IsNullOrEmpty(reason))
            {
                m_sleepPresentationFailures++;
                Problem("sleep-mirror-refresh", "H3 第三分屏的睡眠表现刷新失败：" + reason +
                    "（本拍应用 " + counts.MirrorApplied + " 个视图；不静默跳过）");
            }
        }

        /// <summary>把端口返回的一帧计数累加进状态行计数器（10 s 汇总）。</summary>
        private void AccumulateSleepCounts(in PlayerSleepPresentationCounts counts)
        {
            m_sleepOverlayApplied += counts.Applied;
            m_sleepZzz += counts.Zzz;
            m_sleepBlackout += counts.Blackout;
            m_sleepForced += counts.Forced;
            m_sleepManualWakeDisabled += counts.ManualWakeDisabled;
            m_sleepLegacy += counts.Legacy;
            m_sleepMirrorApplied += counts.MirrorApplied;
            m_sleepMirrorCleared += counts.MirrorCleared;
            m_sleepMissing += counts.Missing;
            m_sleepPresentationFailures += counts.Failures;
        }

        /// <summary>
        /// 每帧：睡眠中的输入 → 门控（10 s / 强制睡眠不可唤醒 / 每次睡眠最多一条）→
        /// 发 <c>PLAYER_WAKE_REQUEST</c>。客户端不发任何本地唤醒；主机接受后由权威上报
        /// （sleeping=false）驱动黑屏淡出。
        /// </summary>
        private void TickClientSleep(long nowMs)
        {
            if (m_sleepGate.Count == 0 || m_sleepPort == null) return;
            m_sleepGate.CopyKeys(m_sleepKeys);
            for (int i = 0; i < m_sleepKeys.Count; i++)
            {
                PlayerKey key = m_sleepKeys[i];
                if (!m_sleepPort.TryReadLocalSleep(key, out PlayerSleepViewState state,
                        out string readReason))
                {
                    Problem("sleep-read:" + key, "读取本机睡眠状态失败：" +
                        (readReason ?? "端口没有给原因") + " → 本帧不判定点击（不猜状态）");
                    continue;
                }
                if (!state.Sleeping)
                {
                    if (m_sleepGate.OnWake(key))
                    {
                        m_sleepWakeConfirmed++;
                        Info("主机已接受本机角色 " + key +
                            " 的唤醒请求（本机状态已醒）→ 黑屏淡出");
                    }
                    continue;
                }
                if (!m_sleepPort.TryPollLocalSleepTap(key, out bool tapped,
                        out string pollReason))
                {
                    Problem("sleep-tap:" + key, "读取睡眠输入失败：" +
                        (pollReason ?? "端口没有给原因") + " → 本帧不判定点击（不猜输入）");
                    continue;
                }
                if (!tapped) continue;
                m_sleepTapCaptured++;
                long sequence = m_sleepRequestSequence + 1;
                PlayerSleepGateAction action = m_sleepGate.OnTap(key, in state, sequence,
                    PlayerSleepLimits.ClockSkewAllowanceSeconds, out string tapReason);
                switch (action)
                {
                    case PlayerSleepGateAction.SendRequest:
                        m_sleepRequestSequence = sequence;
                        if (SendWakeRequest(key, sequence, in state))
                        {
                            // 引擎在 WakeUp() 前清输入：避免同一个点击又触发别的操作。
                            if (!m_sleepPort.TryConsumeLocalSleepInput(key,
                                    out string consumeReason))
                            {
                                Problem("sleep-input-clear:" + key, "清睡眠输入失败：" +
                                    (consumeReason ?? "端口没有给原因"));
                            }
                        }
                        else
                        {
                            m_sleepGate.OnSendFailed(key);
                        }
                        break;
                    case PlayerSleepGateAction.SuppressedForced:
                        m_sleepManualWakeDisabled++;
                        Bump(m_sleepSuppressedReasons, tapReason);
                        m_sleepSuppressed++;
                        break;
                    case PlayerSleepGateAction.SuppressedEarly:
                    case PlayerSleepGateAction.SuppressedAlreadyRequested:
                    case PlayerSleepGateAction.SuppressedNotSleeping:
                    case PlayerSleepGateAction.SuppressedDead:
                    case PlayerSleepGateAction.SuppressedNoPresentation:
                    case PlayerSleepGateAction.SuppressedNoEpisode:
                    case PlayerSleepGateAction.SuppressedInvalidSequence:
                        Bump(m_sleepSuppressedReasons, tapReason);
                        m_sleepSuppressed++;
                        break;
                }
            }
        }

        private bool SendWakeRequest(PlayerKey key, long sequence,
            in PlayerSleepViewState state)
        {
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (localClientId <= 0 || m_messages == null)
            {
                m_sleepWakeReqSendFailures++;
                Problem("sleep-send-nochannel", "无法发送唤醒请求（玩家键 " + key + "）：" +
                    (m_messages == null ? "没有消息层" : "本端传输编号=" + localClientId) +
                    " → 已释放本次请求（下一次点击可重试）");
                return false;
            }
            var data = new PlayerWakeRequestData(localClientId, key.Slot, sequence,
                state.ElapsedSeconds, state.SleepStartTime);
            V.PlayerWakeRequest message;
            try
            {
                message = PlayerWakeRequestCodec.Encode(in data);
            }
            catch (Exception e)
            {
                m_sleepWakeReqSendFailures++;
                Problem("sleep-send-encode", "唤醒请求编码失败（" + data.Describe() + "）：" +
                    e.GetType().Name + " " + e.Message);
                return false;
            }
            bool sent;
            try
            {
                sent = m_messages.Sender.Send(0, message);
            }
            catch (Exception e)
            {
                sent = false;
                Problem("sleep-send-exception", "唤醒请求发送异常（" + data.Describe() + "）：" +
                    e.GetType().Name + " " + e.Message);
            }
            if (!sent)
            {
                m_sleepWakeReqSendFailures++;
                Problem("sleep-send-failed", "唤醒请求发送失败（" + data.Describe() +
                    "）→ 已释放本次请求（下一次点击可重试；通道恢复前不会假装成功）");
                return false;
            }
            m_sleepWakeReqSent++;
            Info("睡眠点击 → 已发送唤醒请求 " + data.Describe() +
                "（每次睡眠只发一次；等待主机权威上报 sleeping=false）");
            return true;
        }

        // ---------------------------------------------------------------- 主机：唤醒请求校验 + 引擎 WakeUp

        /// <summary>
        /// 主机：客户端"点击唤醒"。只接受**当前睡眠角色的 owner**；显式拒绝原因：
        /// <c>wrong-owner / unknown-player / not-sleeping / forced-sleep / too-early /
        /// cooldown</c>（引擎 <c>WakeUp()</c> 失败另加 <c>wake-failed</c>）。门槛只用主机自己的
        /// <c>ComponentSleep</c>，绝不信任客户端上报的 elapsed；<c>cooldown</c> 是同一睡眠期内
        /// 两次请求之间的最小间隔（防连发；客户端本来每次睡眠也只发一条）。接受后调用引擎
        /// <c>WakeUp()</c> 并强制下一拍上报（sleeping=false）。
        /// </summary>
        private void OnWakeRequestMessage(MessageContext context, V.PlayerWakeRequest message)
        {
            if (!m_attached) return;
            m_sleepWakeReqRecv++;
            if (m_control == null || !m_control.IsHosting)
            {
                m_sleepWakeReqWrongDirection++;
                Problem("sleep-req-client", "客户端收到了 PLAYER_WAKE_REQUEST（方向错误，来源 " +
                    "clientId=" + context.ClientId + "）→ 忽略");
                return;
            }
            if (!PlayerWakeRequestCodec.TryDecode(message, out PlayerWakeRequestData request,
                    out string decodeReason))
            {
                m_sleepWakeReqMalformed++;
                Problem("sleep-req-malformed", "唤醒请求畸形（" + decodeReason +
                    "，来源 clientId=" + context.ClientId + "）→ 整条拒绝");
                return;
            }
            if (request.ClientId != context.ClientId)
            {
                RejectWakeRequest(context.ClientId, in request, PlayerSleepReasons.WrongOwner,
                    "请求里的 client_id=" + request.ClientId + " 与连接 " + context.ClientId +
                    " 不一致");
                return;
            }
            if (context.ClientId <= 0 || !m_clients.Contains(context.ClientId))
            {
                RejectWakeRequest(context.ClientId, in request,
                    PlayerSleepReasons.UnknownPlayer,
                    "连接没有登记为在线玩家端（clientId=" + context.ClientId + "）");
                return;
            }
            if (m_sleepPort == null)
            {
                RejectWakeRequest(context.ClientId, in request,
                    PlayerSleepReasons.UnknownPlayer, "睡眠端口未接（组合根没有注册）");
                return;
            }
            // 同一睡眠期内两次请求的最小间隔（防连发）。用请求自带的 sleep_start_time 识别睡眠期，
            // 与客户端的"每次睡眠最多一条"是同一件事的两端口径。
            HostCharacterState cooldownState = GetOrCreateHostCharacter(request.Key);
            if (cooldownState.LastWakeRequestAtMs > 0 &&
                cooldownState.LastWakeRequestStartTime.Equals(request.SleepStartTime) &&
                NowMs() - cooldownState.LastWakeRequestAtMs < HostWakeRequestGapMs)
            {
                RejectWakeRequest(context.ClientId, in request, PlayerSleepReasons.Cooldown,
                    "距同一睡眠期的上一次请求 " +
                    (NowMs() - cooldownState.LastWakeRequestAtMs) + " ms < " +
                    HostWakeRequestGapMs + " ms（sleep_start_time=" +
                    request.SleepStartTime.ToString("0.##") + "）");
                return;
            }
            if (!m_sleepPort.TryReadAuthoritySleep(request.Key,
                    out PlayerSleepAuthorityState authority, out string readReason))
            {
                RejectWakeRequest(context.ClientId, in request,
                    PlayerSleepReasons.UnknownPlayer,
                    "主机没有这个玩家键的睡眠权威（" + (readReason ?? "端口没有给原因") + "）");
                return;
            }
            if (!PlayerSleepRules.ValidateWakeAuthority(in authority, out string rejectReason))
            {
                RejectWakeRequest(context.ClientId, in request, rejectReason,
                    "主机权威：" + authority + "（客户端估算 elapsed=" +
                    request.SleepElapsed.ToString("0.##") + "）");
                return;
            }
            if (!m_sleepPort.TryWakeAuthorityPlayer(request.Key, out string wakeReason))
            {
                RejectWakeRequest(context.ClientId, in request,
                    PlayerSleepReasons.WakeFailed, wakeReason ?? "端口没有给原因");
                return;
            }
            cooldownState.LastWakeRequestAtMs = NowMs();
            cooldownState.LastWakeRequestStartTime = request.SleepStartTime;
            m_sleepWakeReqAccepted++;
            HostCharacterState state = cooldownState;
            // 睡眠状态变了：下一次抓取必须递增 revision 并立刻发出（客户端据此淡出黑屏）。
            state.HasSent = false;
            state.WasForcedSleep = false;
            m_forceSend = true;
            Info("主机接受唤醒请求（" + request.Describe() +
                "）：已调用引擎 ComponentSleep.WakeUp() → 立刻下发 sleeping=false" +
                "（客户端淡出黑屏）");
        }

        private void RejectWakeRequest(int clientId, in PlayerWakeRequestData request,
            string reasonCode, string detail)
        {
            m_sleepWakeReqRejected++;
            Bump(m_sleepWakeRejectReasons, PlayerSleepReasons.Key(reasonCode));
            Problem("sleep-reject:" + PlayerSleepReasons.Key(reasonCode),
                "拒绝唤醒请求（clientId=" + clientId + " 玩家键=" + request.Key + " seq=" +
                request.RequestSequence + "）：" + reasonCode + " —— " + detail);
        }

        /// <summary>
        /// 主机：客户端请求复活。只接受**当前死亡角色的 owner** 的请求；显式拒绝原因：
        /// <c>unknown-player / wrong-owner / not-dead / respawn-forbidden / cooldown</c>
        /// （引擎复位失败另加 <c>respawn-failed</c>）。接受后按 PlayerData.SpawnPosition 复活，
        /// 排 Respawn 事件 + 权威状态，并回 PLAYER_RESPAWN_RESULT。
        /// </summary>
        private void OnRespawnRequestMessage(MessageContext context, R.PlayerRespawnRequest message)
        {
            if (!m_attached) return;
            m_respawnReqRecv++;
            if (m_control == null || !m_control.IsHosting)
            {
                m_respawnReqWrongDirection++;
                Problem("respawn-req-client", "客户端收到了 PLAYER_RESPAWN_REQUEST（方向错误，" +
                    "来源 clientId=" + context.ClientId + "）→ 忽略");
                return;
            }
            if (!PlayerRespawnCodec.TryDecodeRequest(message, out PlayerRespawnRequestData request,
                    out string decodeReason))
            {
                m_respawnReqMalformed++;
                Problem("respawn-req-malformed", "复活请求畸形（" + decodeReason +
                    "，来源 clientId=" + context.ClientId + "）→ 整条拒绝");
                return;
            }
            if (request.ClientId != context.ClientId)
            {
                RejectRespawnRequest(context.ClientId, request.Key, request.RequestSequence,
                    PlayerRespawnReasons.WrongOwner,
                    "请求里的 client_id=" + request.ClientId + " 与连接 " + context.ClientId +
                    " 不一致");
                return;
            }
            if (context.ClientId <= 0 || !m_clients.Contains(context.ClientId))
            {
                RejectRespawnRequest(context.ClientId, request.Key, request.RequestSequence,
                    PlayerRespawnReasons.UnknownPlayer,
                    "连接没有登记为在线玩家端（clientId=" + context.ClientId + "）");
                return;
            }
            PlayerKey key = request.Key;
            if (!m_hostCharacters.TryGetValue(key, out HostCharacterState state))
            {
                RejectRespawnRequest(context.ClientId, key, request.RequestSequence,
                    PlayerRespawnReasons.UnknownPlayer,
                    "主机没有这个玩家键的角色状态（可能已离开 / 从未建立）");
                return;
            }
            if (!state.Dead)
            {
                RejectRespawnRequest(context.ClientId, key, request.RequestSequence,
                    PlayerRespawnReasons.NotDead, "该角色当前不是死亡状态");
                return;
            }
            PlayerRespawnMode mode = m_port.GetRespawnMode(out string modeReason);
            if (mode != PlayerRespawnMode.TapToRespawn)
            {
                RejectRespawnRequest(context.ClientId, key, request.RequestSequence,
                    PlayerRespawnReasons.RespawnForbidden,
                    "世界模式不允许复活（" + PlayerRespawnRules.Describe(mode) + "：" +
                    (modeReason ?? "主机模式规则拒绝") + "）");
                return;
            }
            long nowMs = NowMs();
            if (state.DeathAtMs > 0 && nowMs - state.DeathAtMs < HostRespawnGuardMs)
            {
                RejectRespawnRequest(context.ClientId, key, request.RequestSequence,
                    PlayerRespawnReasons.Cooldown,
                    "距死亡边沿 " + (nowMs - state.DeathAtMs) + " ms < 门槛 " +
                    HostRespawnGuardMs + " ms");
                return;
            }
            if (state.LastRequestAtMs > 0 &&
                nowMs - state.LastRequestAtMs < HostRespawnRequestGapMs)
            {
                RejectRespawnRequest(context.ClientId, key, request.RequestSequence,
                    PlayerRespawnReasons.Cooldown,
                    "距上一次请求 " + (nowMs - state.LastRequestAtMs) + " ms < " +
                    HostRespawnRequestGapMs + " ms");
                return;
            }
            state.LastRequestAtMs = nowMs;
            if (!m_port.TryRespawnAvatar(key, out Vector3 position, out string respawnReason))
            {
                m_hostRespawnFailures++;
                RejectRespawnRequest(context.ClientId, key, request.RequestSequence,
                    PlayerRespawnReasons.RespawnFailed,
                    respawnReason ?? "端口没有给原因");
                return;
            }
            // 复位成功：清死亡标记，排 Respawn 事件（落点）+ 权威状态。
            state.Dead = false;
            state.DeathAtMs = 0;
            state.LastRequestAtMs = 0;
            state.Revision = NextRevision(state.Revision);
            m_pendingEvents.Add(new PlayerVitalEventSnapshot(key, state.Revision,
                PlayerVitalEventKind.Respawn, string.Empty, true, position));
            state.HasSent = false;
            m_respawnEventsSent++;
            m_respawnReqAccepted++;
            m_forceSend = true;
            SendRespawnResult(context.ClientId, key, request.RequestSequence, true,
                PlayerRespawnReasons.Accepted, true, position);
            Info("主机接受复活请求（" + request.Describe() + "，模式=" +
                PlayerRespawnRules.Describe(mode) + "）→ 已按 SpawnPosition 复活玩家键 " + key +
                "，落点 " + position + "；立刻发 Respawn 事件 + 权威状态");
        }

        private void RejectRespawnRequest(int clientId, PlayerKey key, long sequence,
            string reasonCode, string detail)
        {
            m_respawnReqRejected++;
            Bump(m_hostRespawnRejectReasons, PlayerRespawnReasons.Key(reasonCode));
            Problem("respawn-reject:" + PlayerRespawnReasons.Key(reasonCode),
                "拒绝复活请求（clientId=" + clientId + " 玩家键=" + key + " seq=" + sequence +
                "）：" + reasonCode + " —— " + detail);
            SendRespawnResult(clientId, key, sequence, false, reasonCode, false, Vector3.Zero);
        }

        private void SendRespawnResult(int clientId, PlayerKey key, long sequence,
            bool accepted, string reason, bool hasPosition, Vector3 position)
        {
            byte[] payload;
            try
            {
                var data = new PlayerRespawnResultData(clientId, key.Slot, sequence, accepted,
                    reason, hasPosition, position);
                payload = PlayerRespawnCodec.EncodeResultBytes(in data);
            }
            catch (Exception e)
            {
                m_respawnResultSendFailures++;
                Problem("respawn-result-encode", "复活结果编码失败（clientId=" + clientId +
                    " 玩家键=" + key + " seq=" + sequence + "）：" + e.GetType().Name + " " +
                    e.Message);
                return;
            }
            if (m_transport == null || !m_transport.TrySendTo(clientId,
                    MessageType.PlayerRespawnResult, payload))
            {
                m_respawnResultSendFailures++;
                Problem("respawn-result-send", "复活结果发送失败（clientId=" + clientId +
                    " 玩家键=" + key + " seq=" + sequence + " reason=" + reason +
                    "）→ 客户端会在 " + PlayerRespawnRules.RequestTimeoutMs + " ms 后超时（不假装已回执）");
            }
        }

        private void OnRespawnResultMessage(MessageContext context, R.PlayerRespawnResult message)
        {
            if (!m_attached) return;
            m_respawnResultRecv++;
            if (m_control != null && m_control.IsHosting)
            {
                m_respawnReqWrongDirection++;
                Problem("respawn-result-host", "主机收到了 PLAYER_RESPAWN_RESULT（方向错误，" +
                    "来源 clientId=" + context.ClientId + "）→ 忽略");
                return;
            }
            if (context.ClientId != 0)
            {
                m_respawnReqWrongDirection++;
                Problem("respawn-result-sender", "客户端收到来自 clientId=" + context.ClientId +
                    " 的复活结果（只有主机能回）→ 忽略");
                return;
            }
            if (!m_worldReady)
            {
                m_notReadyDrops++;
                return;
            }
            if (!PlayerRespawnCodec.TryDecodeResult(message, out PlayerRespawnResultData result,
                    out string decodeReason))
            {
                m_vitalsMalformed++;
                Problem("respawn-result-malformed", "主机下发的复活结果畸形（" + decodeReason +
                    "）→ 整条丢弃");
                return;
            }
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (result.ClientId != localClientId)
            {
                Problem("respawn-result-owner", "复活结果不属于本端（result clientId=" +
                    result.ClientId + "，本端=" + localClientId + "）→ 忽略");
                return;
            }
            PlayerRespawnGateAction action = m_respawnGate.OnResult(result.Key,
                result.RequestSequence, result.Accepted, result.Reason, out string staleReason);
            if (action == PlayerRespawnGateAction.ResultStale ||
                action == PlayerRespawnGateAction.IgnoredNoDeath)
            {
                Problem("respawn-result-stale", "丢弃陈旧 / 不匹配的复活结果（玩家键 " +
                    result.Key + " seq=" + result.RequestSequence + "）：" + staleReason);
                return;
            }
            if (action == PlayerRespawnGateAction.ResultAccepted)
            {
                m_respawnAccepted++;
                Info("主机接受复活请求（玩家键 " + result.Key + " seq=" +
                    result.RequestSequence + " · 落点=" +
                    (result.HasPosition ? result.Position.ToString() : "未提供") +
                    "）→ 等待 Respawn 事件 + 权威状态");
            }
            else
            {
                m_respawnRejected++;
                Bump(m_respawnRejectReasons, PlayerRespawnReasons.Key(result.Reason));
                Problem("respawn-rejected", "主机拒绝复活请求（玩家键 " + result.Key + " seq=" +
                    result.RequestSequence + "）：" + result.Reason);
                if (!m_port.TryShowLocalRespawnRejected(result.Key, result.Reason,
                        out string showReason))
                {
                    Problem("respawn-rejected-ui", "显示复活拒绝原因失败：" +
                        (showReason ?? "端口没有给原因"));
                }
            }
        }

        private static int CountEntries(V.PlayerVitalsReport message) =>
            message?.Entries?.Count ?? 0;

        private static int CountEvents(V.PlayerVitalsEvents message) =>
            message?.Entries?.Count ?? 0;

        private bool IsLocalKey(PlayerKey key) =>
            m_transport != null && m_transport.LocalClientId >= 0 &&
            key.ClientId == m_transport.LocalClientId;

        // ---------------------------------------------------------------- 客户端：本地模拟闸门

        private void EnsureLocalSimulationDisabled()
        {
            if (m_port == null) return;
            int added = m_port.DisableLocalSimulation(out int total, out string reason);
            if (reason != null)
            {
                Problem("local-sim", "摘掉本机角色的生命 / 体征 / 睡眠 / 病程 / 火焰模拟失败：" +
                    reason + "（本机角色可能仍在本地模拟）");
                return;
            }
            m_clientLocalSimDisabled = total;
            if (added > 0)
            {
                Info("已摘掉 " + added + " 个本机角色的生命 / 体征 / 睡眠 / 病程 / 火焰模拟" +
                    "（累计 " + total + " 个）：客户端只跟随主机下发的值");
            }
        }

        /// <summary>
        /// 客户端：远端化身（主机角色 / 其它客户端角色）也不跑生命 / 体征 / 睡眠模拟。
        /// 主机侧**绝不**调用（主机要自己模拟它们，H3 读的就是这份状态）。
        /// </summary>
        private void TickRemoteSuppression()
        {
            if (m_port == null) return;
            int added = m_port.SuppressRemoteAvatars(out int total, out string reason);
            if (reason != null)
            {
                m_suppressFailures++;
                Problem("suppress", "抑制远端化身的生命 / 体征模拟失败：" + reason +
                    " → 本端可能仍会按自己的环境伤害远端化身（计数 " + m_suppressFailures + "）");
                return;
            }
            m_suppressedTotal = total;
            if (added > 0)
            {
                m_suppressNew += added;
                Info("已抑制 " + added + " 个远端化身的生命 / 体征 / 睡眠模拟（当前 " + total +
                    " 个）：客户端不再模拟它们的伤害 / 饥饿 / 窒息");
            }
        }

        private void TryEnableLocalSimulation(string why)
        {
            if (m_port == null) return;
            if (m_clientLocalSimDisabled == 0) return;
            if (!m_port.EnableLocalSimulation(out string reason))
            {
                Problem("local-sim-enable", "恢复本机角色的生命 / 体征模拟失败（" + why + "）：" +
                    (reason ?? "端口没有给原因"));
                return;
            }
            if (m_clientLocalSimDisabled > 0)
                Info("已恢复本机角色的生命 / 体征 / 睡眠 / 病程 / 火焰模拟（" + why + "）");
            m_clientLocalSimDisabled = 0;
            m_worldReady = false;
        }

        // ---------------------------------------------------------------- 会话边沿

        private void BeginHosting()
        {
            m_clients.Clear();
            m_hostCharacters.Clear();
            m_clientCharacters.Clear();
            m_pendingEvents.Clear();
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_reportSequence = 0;
            m_eventSequence = 0;
            m_lastReportSentMs = 0;
            m_lastReportAttemptMs = 0;
            m_forceSend = true;
            // 主机自己必须跑完整模拟：如果本进程之前当过客户端（本机模拟被摘掉），先恢复。
            m_port?.EnableLocalSimulation(out _);
            m_clientLocalSimDisabled = 0;
            Info("开始主持房间：主机接管每个角色的生命 / 体征模拟与下发（本机角色模拟已恢复），" +
                "客户端上报一律拒绝");
        }

        private void EndHosting(bool connected)
        {
            m_clients.Clear();
            m_hostCharacters.Clear();
            m_pendingEvents.Clear();
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_forceSend = false;
            Info("房间已关闭：主机权威状态表已清空" + (connected ? "" : "（通道未连接）"));
        }

        private void OnConnectionEstablished(bool hosting)
        {
            m_clientCharacters.Clear();
            m_hostCharacters.Clear();
            m_pendingEvents.Clear();
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_forceSend = true;
            m_lastReportAttemptMs = 0;
            m_lastReportSentMs = 0;
            if (hosting)
            {
                m_clients.Clear();
                Info("通道已就绪（主机）：等待玩家端加入；加入时立刻重发当前权威状态");
            }
            else
            {
                Info("已连上主机：客户端清空接收状态；等世界就绪后应用主机下发的状态" +
                    "（本机角色不再上报）");
            }
        }

        private void OnConnectionLost(bool hosting)
        {
            m_clientCharacters.Clear();
            m_hostCharacters.Clear();
            m_pendingEvents.Clear();
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_worldReady = false;
            m_forceSend = false;
            m_lastSuppressMs = 0;
            if (!hosting) TryEnableLocalSimulation("与主机断开");
            Info("通道断开：本端状态表已清空" +
                (hosting ? "（主机侧等待玩家端重连）" : "（本机角色恢复本地模拟，等待重连）"));
        }

        private void OnClientWorldReady()
        {
            m_worldReady = true;
            m_clientCharacters.Clear();
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_lastSuppressMs = 0;
            EnsureLocalSimulationDisabled();
            Info("客户端世界已就绪：开始应用主机下发的生命 / 体征状态" +
                "（重连时主机会重发当前状态，1 Hz 心跳补齐漏包）");
        }

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_attached) return;
            m_clients.Add(clientId);
            DropClientState(clientId);
            m_forceSend = true;
            Info("玩家端 clientId=" + clientId + "（" + (name ?? "无名字") + "）加入" +
                (rejoin ? "（重连）" : "") + "：立刻重发当前权威状态（主机模拟它的角色）");
        }

        /// <summary>
        /// 主机：玩家端连接不可达。**断线期间没有额外的死亡保留窗口**：现有 ClientLeft 行为
        /// （Modules/PlayerAvatarModule.cs:504-560 → Engine/NetworkAvatarSpawner.Remove）
        /// 会立刻摘掉该端的联机角色实体，本模块随之删掉它的权威角色状态（包括 Dead /
        /// DeathAtMs）。断开中的 owner 无法点击，也没有可复活的化身；重连按今天的流程
        /// 重新加入 / 新建角色（新角色是活的），不会把旧死亡硬套到新角色上。
        /// 死亡状态在"死亡边沿 → ClientLeft"这段连接存活期内一直保留，等待 owner 请求。
        /// </summary>
        private void OnClientLeft(int clientId, string reason)
        {
            if (!m_attached) return;
            m_clients.Remove(clientId);
            DropClientState(clientId);
            Info("玩家端 clientId=" + clientId + " 离开（" + reason +
                "）：它的角色状态（含死亡标记）已随化身一起从主机权威表里移除" +
                "（现有 ClientLeft 行为 = 立即摘化身，没有断线宽限期；重连按新加入处理）");
        }

        private void DropClientState(int clientId)
        {
            var stale = new List<PlayerKey>();
            foreach (PlayerKey key in m_hostCharacters.Keys)
                if (key.ClientId == clientId) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) m_hostCharacters.Remove(stale[i]);
            m_senderSequences.Remove(clientId);
            m_senderEventSequences.Remove(clientId);
        }

        private void CollectClients()
        {
            m_clientList.Clear();
            foreach (int clientId in m_clients)
                if (clientId > 0) m_clientList.Add(clientId);
            m_clientList.Sort();
        }

        // ---------------------------------------------------------------- 工具 / 诊断

        private HostCharacterState GetOrCreateHostCharacter(PlayerKey key)
        {
            if (m_hostCharacters.TryGetValue(key, out HostCharacterState state)) return state;
            state = new HostCharacterState();
            m_hostCharacters[key] = state;
            return state;
        }

        private ClientKeyState GetOrCreateClientCharacter(PlayerKey key)
        {
            if (m_clientCharacters.TryGetValue(key, out ClientKeyState state)) return state;
            state = new ClientKeyState();
            m_clientCharacters[key] = state;
            return state;
        }

        private static uint NextRevision(uint current) => current == uint.MaxValue ? 1 : current + 1;

        private long NowMs() => (long)(m_now() * 1000.0);

        private static void Bump(Dictionary<string, long> counters, string key)
        {
            string code = string.IsNullOrEmpty(key) ? "unknown" : key;
            counters.TryGetValue(code, out long count);
            counters[code] = count + 1;
        }

        /// <summary>限流 / 状态行用的稳定原因计数：<c>{reason:count,...}</c>（按 key 排序）。</summary>
        private static string FormatReasons(Dictionary<string, long> counters)
        {
            if (counters == null || counters.Count == 0) return "{}";
            var keys = new List<string>(counters.Count);
            foreach (string key in counters.Keys) keys.Add(key);
            keys.Sort(StringComparer.Ordinal);
            var text = new System.Text.StringBuilder();
            text.Append('{');
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(keys[i]).Append(':').Append(counters[keys[i]]);
            }
            text.Append('}');
            return text.ToString();
        }

        /// <summary>
        /// 状态行（组合根启动日志 + 10 秒汇总）。计数单位 = 条目（一次角色状态 / 一条边沿）。
        /// 主机：
        ///   → vitalsSent     = 发给 owner 的本人条目数；
        ///   → vitalsApplied  = 主机引擎已模拟、被这次抓取认定为权威变化的条目数
        ///                      （主机不做网络写入，所以这里统计"已经为真的权威值"）；
        ///   → Broadcast      = 作为观察者转发给其它玩家端的条目数（姿势 / HUD）；
        ///   → DeathEvents / RespawnEvents = 主机裁决并排队的边沿数；
        ///   → respawnReqRecv / Accepted / Rejected(reason) = 主机收到的复活请求 / 接受 / 按原因拒绝；
        ///   → autoRespawnRemoved = 1（旧的 2.5 秒自动复活路径已删除）。
        ///   → clientReportRejected        = 拒绝的客户端上报 / 事件消息数。
        /// 客户端：
        ///   → vitalsRecv / vitalsApplied  = 收到 / 写进本机玩家或远端化身的条目数；
        ///   → vitalsStale / vitalsMalformed = 陈旧 / 畸形的条目（或消息）数；
        ///   → DeathApplied / RespawnApplied = 应用的死亡 / 复活边沿数；
        ///   → deathScreenShown / respawnTapCaptured = 显示的死亡屏 / 捕获的点击次数；
        ///   → respawnReqSent / ResultRecv / Accepted / Rejected(reason) = 请求与结果；
        ///   → respawnSuppressed{reason}   = 1.5 秒门槛前 / 已发过 / 模式禁止 / 已解决的点击；
        ///   → clientLocalSimDisabled      = 当前被摘掉生命 / 体征 / 睡眠 / 病程 / 火焰模拟的本机角色数；
        ///   → clientReportRejected        = 拒绝的非主机上报 / 事件消息数。
        /// </summary>
        public string StatusLine()
        {
            bool hosting = m_control != null && m_control.IsHosting;
            string counters = hosting
                ? "vitalsSent=" + m_vitalsSent +
                  " vitalsApplied=" + m_vitalsApplied +
                  " DeathEvents=" + m_deathEventsSent +
                  " RespawnEvents=" + m_respawnEventsSent +
                  " respawnReqRecv=" + m_respawnReqRecv +
                  " respawnReqAccepted=" + m_respawnReqAccepted +
                  " respawnReqRejected=" + m_respawnReqRejected +
                  " respawnReqRejectedBy=" + FormatReasons(m_hostRespawnRejectReasons) +
                  " respawnReqMalformed=" + m_respawnReqMalformed +
                  " respawnReqWrongDirection=" + m_respawnReqWrongDirection +
                  " autoRespawnRemoved=" + AutoRespawnRemoved +
                  " Broadcast=" + m_broadcast +
                  " clientReportRejected=" + m_clientReportRejected +
                  " reports=" + m_reportsSent +
                  " clients=" + m_clients.Count +
                  " noRecipients=" + m_noRecipients +
                  " fails={send:" + m_sendFailures + " event:" + m_eventSendFailures +
                  " encode:" + m_encodeFailures + " capture:" + m_captureFailures +
                  " poll:" + m_pollFailures + " respawn:" + m_hostRespawnFailures +
                  " result:" + m_respawnResultSendFailures + "}"
                : "vitalsRecv=" + m_vitalsRecv +
                  " vitalsApplied=" + m_clientVitalsApplied +
                  " vitalsStale=" + m_vitalsStale +
                  " vitalsMalformed=" + m_vitalsMalformed +
                  " DeathApplied=" + m_deathApplied +
                  " RespawnApplied=" + m_respawnApplied +
                  " deathScreenShown=" + m_deathScreensShown +
                  " respawnTapCaptured=" + m_respawnTapCaptured +
                  " respawnReqSent=" + m_respawnReqSent +
                  " respawnReqSendFail=" + m_respawnReqSendFailures +
                  " respawnReqTimeout=" + m_respawnReqTimeouts +
                  " respawnResultRecv=" + m_respawnResultRecv +
                  " respawnAccepted=" + m_respawnAccepted +
                  " respawnRejected=" + m_respawnRejected +
                  " respawnRejectedBy=" + FormatReasons(m_respawnRejectReasons) +
                  " respawnSuppressed=" + m_respawnSuppressed +
                  " respawnSuppressedBy=" + FormatReasons(m_respawnSuppressedReasons) +
                  " autoRespawnRemoved=" + AutoRespawnRemoved +
                  " clientLocalSimDisabled=" + m_clientLocalSimDisabled +
                  " clientReportRejected=" + m_clientReportRejected +
                  " unchanged=" + m_vitalsUnchanged +
                  " noAvatar=" + m_vitalsNoAvatar +
                  " notReady=" + m_notReadyDrops +
                  " suppressed=" + m_suppressedTotal + " suppressNew=" + m_suppressNew +
                  " hudRefresh=" + m_hudRefreshes + " hudFail=" + m_hudRefreshFailures +
                  " fails={apply:" + m_applyFailures + " decode:" + m_decodeFailures +
                  " suppress:" + m_suppressFailures +
                  " result:" + m_respawnResultSendFailures + "}";
            return "玩家生命/体征同步[" + (hosting ? "host" : "client") + "]：" + counters +
                " " + SleepStatusCounters() +
                " · " + (m_port != null ? m_port.Describe() : "端口未接");
        }

        /// <summary>
        /// 睡眠表现 / 点击唤醒的 10 秒计数器（两种角色的状态行都输出同一组名字，
        /// 不适用的那一侧为 0 —— 排查时不用记"这个名字只在哪一端出现"）。
        ///
        ///   · <c>sleepOverlayApplied</c>   = 写入睡眠表现的角色-帧数（本机 + H3 镜像）；
        ///   · <c>sleepZzz</c> / <c>sleepBlackout</c> = 其中写 "Zzz..." / 黑屏的角色-帧数；
        ///   · <c>sleepForced</c>           = 进入强制睡眠（昏迷）的睡眠期数；
        ///   · <c>sleepManualWakeDisabled</c> = 强制睡眠期间被抑制的点击次数（没有唤醒路径）；
        ///   · <c>sleepWakeReqSent</c>      = 客户端发出的唤醒请求数（每次睡眠最多一条）；
        ///   · <c>sleepWakeReqRecv</c> / <c>Accepted</c> / <c>RejectedBy</c> = 主机收到的 / 接受 /
        ///     按原因拒绝（not-sleeping / forced-sleep / too-early / wrong-owner / unknown-player /
        ///     wake-failed / malformed）；
        ///   · <c>sleepWakeConfirmed</c>     = 客户端观察到"请求之后主机上报 sleeping=false"的次数；
        ///     没有单独的结果消息，接受就是这样确认的；
        ///   · <c>sleepLegacy</c>            = 老版本主机（没有 sleep_start_time）的睡眠期数：
        ///     仍显示淡入 + Zzz / 黑屏，但没有提示文本与唤醒路径；
        ///   · <c>sleepMirrorApplied/Cleared</c> = H3 第三分屏镜像的角色-帧 / 解绑写回次数；
        ///   · <c>sleepSuppressedBy</c> / <c>sleepFail</c> / <c>sleepMissing</c> = 点击抑制原因 /
        ///     表现写入失败 / 本拍找不到角色。
        /// </summary>
        private string SleepStatusCounters()
        {
            return "sleepOverlayApplied=" + m_sleepOverlayApplied +
                " sleepZzz=" + m_sleepZzz +
                " sleepBlackout=" + m_sleepBlackout +
                " sleepForced=" + m_sleepForced +
                " sleepManualWakeDisabled=" + m_sleepManualWakeDisabled +
                " sleepWakeReqSent=" + m_sleepWakeReqSent +
                " sleepWakeReqSendFail=" + m_sleepWakeReqSendFailures +
                " sleepWakeReqRecv=" + m_sleepWakeReqRecv +
                " sleepWakeReqAccepted=" + m_sleepWakeReqAccepted +
                " sleepWakeReqRejected=" + m_sleepWakeReqRejected +
                " sleepWakeReqRejectedBy=" + FormatReasons(m_sleepWakeRejectReasons) +
                " sleepWakeReqMalformed=" + m_sleepWakeReqMalformed +
                " sleepWakeReqWrongDirection=" + m_sleepWakeReqWrongDirection +
                " sleepWakeConfirmed=" + m_sleepWakeConfirmed +
                " sleepTapCaptured=" + m_sleepTapCaptured +
                " sleepSuppressed=" + m_sleepSuppressed +
                " sleepSuppressedBy=" + FormatReasons(m_sleepSuppressedReasons) +
                " sleepLegacy=" + m_sleepLegacy +
                " sleepMirrorApplied=" + m_sleepMirrorApplied +
                " sleepMirrorCleared=" + m_sleepMirrorCleared +
                " sleepMissing=" + m_sleepMissing +
                " sleepFail=" + m_sleepPresentationFailures;
        }

        private void LogSummaryIfDue(long nowMs)
        {
            if (nowMs - m_lastSummaryMs < SummaryLogMs) return;
            m_lastSummaryMs = nowMs;
            Info(StatusLine());
        }

        private void Info(string message) => m_context?.Diagnostics.Info(ModuleIdText, message);

        private void Error(string message) =>
            m_context?.Diagnostics.Error(ModuleIdText, message, null);

        private void LogInfo(string message) =>
            m_context?.Diagnostics.Info(ModuleIdText, message);

        private void LogOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            Info(message);
        }

        private void Problem(string key, string message)
        {
            long now = (long)(m_now() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && now - last < ProblemLogMs) return;
            m_lastProblemMs[key] = now;
            m_context?.Diagnostics.Warn(ModuleIdText, message);
        }
    }
}

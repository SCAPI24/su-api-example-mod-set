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
    ///     `PLAYER_VITALS_REPORT`，内容 = 该端自己的权威状态（owner 条目，**永远每拍到**）+
    ///     其它角色（观察者条目，用于姿势 / HUD）。观察者条目只发给"该端兴趣窗口包含它"的端
    ///     （窗口 = 客户端上报的中心区块 + 半径，与地形同一份口径），并按"该端自己角色 → 它"
    ///     的水平距离分级限流（≤32 m 每拍 / 32–64 m 每 2 拍 / &gt;64 m 每 4 拍）；死亡 / 死因变化 /
    ///     进入强制睡眠这三个关键边沿与心跳 / 强制整表重发**不受限流**。每个端各自编码一份，
    ///     sequence 仍按拍单调，revision 仍是每键单调。
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

        /// <summary>
        /// 观察者条目的距离分级（米，水平 XZ 距离）：≤32 m 每拍都发；32–64 m 每 2 拍 1 次；
        /// &gt;64 m 每 4 拍 1 次。窗口本身只按区块 X/Z 判定（<see cref="ClientWindowView.Contains"/>），
        /// 距离也用水平口径，避免"人在正上方 / 正下方"被垂直差误判成远档。
        /// </summary>
        private const float ObserverNearRangeM = 32f;
        private const float ObserverMidRangeM = 64f;

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

        /// <summary>
        /// 客户端发出 <c>PLAYER_SLEEP_REQUEST</c> 之后等主机裁决的上限。超时不是"当成成功"：
        /// 只计数 + 明确日志，并释放门控让下一次点击能重试（否则一次丢包会让玩家
        /// 永远点不动睡眠按钮）。
        /// </summary>
        private const int SleepEntryPendingTimeoutMs = 3000;

        /// <summary>10 秒一条状态行（计数器）。</summary>
        private const int SummaryLogMs = 10000;

        /// <summary>同一条问题日志的最小间隔。</summary>
        private const int ProblemLogMs = 10000;

        /// <summary>一次最多发几条死亡 / 复活事件（超过就分多条）。</summary>
        private const int MaxEventsPerMessage = 8;

        /// <summary>
        /// 客户端一次死亡 / 复活的本地状态（方案 A：引擎原生死亡流程 + 主机批准后才推进）。
        /// 只在**主机 Death 事件 / 主机裁决的死亡快照**到达时创建，复活重放完成后销毁。
        /// </summary>
        private sealed class LocalRespawnState
        {
            public PlayerKey Key;
            /// <summary>这次死亡的 revision（主机 Death 事件里的那个）。</summary>
            public uint DeathRevision;
            /// <summary>主机 Respawn 事件的 revision（可能比死亡更新；0 = 还没收到）。</summary>
            public uint RespawnRevision;
            /// <summary>主机裁决的落点（from result 或 respawn event）。</summary>
            public bool HasPosition;
            public Vector3 Position;
            /// <summary>主机已批准（= 可以执行引擎 RemoveEntity）的时刻；0 = 还没批准。</summary>
            public long AcceptedAtMs;
            /// <summary>已经执行过引擎 RemoveEntity。</summary>
            public bool Executed;
            /// <summary>已经完成出生后权威重放。</summary>
            public bool ReplayApplied;
            /// <summary>第一次发现"实体还没出生"的时刻（诊断用，0 = 没等过）。</summary>
            public long WaitingSinceMs;
            /// <summary>等待出生期间累计的帧数（诊断用）。</summary>
            public long WaitFrames;
        }

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
        /// <summary>
        /// 主机：兴趣窗口端口（<see cref="IWindowBroadcastPort"/>，实现 = 地形模块，与地形下发
        /// 同一份窗口）。软依赖：缺失时生命 / 体征同步照常（owner 条目 / 死亡 / 复活都不依赖它），
        /// 只是**观察者条目一律不下发**并计 <see cref="m_observerWindowUnknown"/> ——
        /// 绝不因此改回"发给全体"。
        /// </summary>
        private IWindowBroadcastPort m_windowPort;
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
        /// <summary>
        /// 与 <see cref="m_sendEntries"/> 一一对应的"永不限流"标记（死亡 / 死因变化 /
        /// 进入强制睡眠的边沿，以及首份样本）。观察者条目被距离分级限流时，命中这里的条目照发。
        /// </summary>
        private readonly List<bool> m_sendEntryUrgent =
            new List<bool>(PlayerVitalsLimits.MaxReportEntries);
        /// <summary>逐端过滤后的上报条目（复用；每个端重新填充后各编码一份 95 上报）。</summary>
        private readonly List<PlayerVitalsSnapshot> m_clientSendEntries =
            new List<PlayerVitalsSnapshot>(PlayerVitalsLimits.MaxReportEntries);
        /// <summary>
        /// 本拍该端 owner 条目的位置（距离分级的参照；复用，一台设备 ≤4 个本机槽位）。
        /// 分屏时取最近的那个本机角色 —— 窗口 / 距离都是"整个端"的口径。
        /// </summary>
        private readonly List<Vector3> m_ownerPositions = new List<Vector3>(PlayerKey.SlotCount);
        /// <summary>本拍的客户端兴趣窗口（复用；CaptureClientWindows 填充，≤4 端）。</summary>
        private readonly List<ClientWindowView> m_clientWindows = new List<ClientWindowView>(4);
        /// <summary>
        /// (玩家端 clientId, 被观察玩家键) → 距上次发送已经过的"机会数"。
        /// 只在 32–64 m（每 2 拍）/ &gt;64 m（每 4 拍）档位累积；发送 / 边沿 / 心跳 / 整表重发后归 0。
        /// 按端分字典：端离开时整只移除（见 <see cref="DropClientState"/>），不会无限增长。
        /// </summary>
        private readonly Dictionary<int, Dictionary<PlayerKey, uint>> m_observerSendCounts =
            new Dictionary<int, Dictionary<PlayerKey, uint>>();
        private readonly List<PlayerVitalsSlotSample> m_samples =
            new List<PlayerVitalsSlotSample>(PlayerVitalsLimits.MaxReportEntries);

        /// <summary>客户端侧：收到 / 应用过的每键状态。</summary>
        private readonly Dictionary<PlayerKey, ClientKeyState> m_clientCharacters =
            new Dictionary<PlayerKey, ClientKeyState>();

        /// <summary>
        /// 客户端侧：每个本机角色的死亡 / 复活请求状态机（1.5 秒门槛、每次死亡最多一个请求、
        /// 结果 / 超时后不再响应点击）。纯逻辑在 Net/PlayerRespawnNet.cs，可无设备自检。
        /// </summary>
        private readonly PlayerRespawnGate m_respawnGate;
        /// <summary>已对哪个死亡期执行过不可复活模式的点击动作（Cruel 菜单 / Adventure 说明）。</summary>
        private readonly HashSet<PlayerKey> m_terminalDeathHandled = new HashSet<PlayerKey>();
        /// <summary>每帧遍历死亡屏的复用缓冲。</summary>
        private readonly List<PlayerKey> m_respawnKeys = new List<PlayerKey>(PlayerKey.SlotCount);
        private long m_respawnRequestSequence;

        /// <summary>
        /// 客户端本机角色：死亡 → 点击 → 主机批准 → 引擎删实体 → 新实体出生后权威重放。
        /// 方案 A 的状态全在这里（纯决策，不含 Game 类型）；引擎侧动作通过
        /// <see cref="IPlayerVitalsPort.TryExecuteLocalRespawn"/> /
        /// <see cref="IPlayerVitalsPort.TryReplayLocalRespawn"/> 完成。
        /// </summary>
        private readonly Dictionary<PlayerKey, LocalRespawnState> m_localRespawns =
            new Dictionary<PlayerKey, LocalRespawnState>();
        /// <summary>每帧遍历待重放的复用缓冲。</summary>
        private readonly List<PlayerKey> m_localRespawnKeys = new List<PlayerKey>(PlayerKey.SlotCount);
        /// <summary>
        /// 每键死亡 epoch（= 最近一次复活裁决的 revision）。revision 不新于它的 Dead 快照 /
        /// Death 事件一律丢弃：那些是复活之前就在网络里飞着的旧状态，写进新实体会让引擎
        /// 立刻再判一次死（见 <see cref="PlayerRespawnEpoch"/> 的注释）。
        /// </summary>
        private readonly Dictionary<PlayerKey, uint> m_localRespawnEpoch =
            new Dictionary<PlayerKey, uint>();

        /// <summary>
        /// 客户端睡眠点击门控（每次睡眠最多一条 PLAYER_WAKE_REQUEST）。纯逻辑在
        /// Net/PlayerSleepNet.cs：门槛 10 s / 强制睡眠不可唤醒 / 时钟安全余量都在那里。
        /// </summary>
        private readonly PlayerSleepGate m_sleepGate = new PlayerSleepGate();
        /// <summary>
        /// 客户端入睡申请门控（每次"按下"最多一条 PLAYER_SLEEP_REQUEST）。纯逻辑在
        /// Net/PlayerSleepNet.cs：按下沿必须被释放过 / 已经在睡就不发 / 发过就不重复。
        /// 这正是"进入睡眠的那次点击不能被当成第二次点击"的落点。
        /// </summary>
        private readonly PlayerSleepRequestGate m_sleepRequestGate = new PlayerSleepRequestGate();
        /// <summary>每帧遍历睡眠键的复用缓冲。</summary>
        private readonly List<PlayerKey> m_sleepKeys = new List<PlayerKey>(PlayerKey.SlotCount);
        private long m_sleepRequestSequence;
        /// <summary>入睡请求的单调序号（与唤醒请求分开编号，日志里不混淆）。</summary>
        private long m_sleepEntrySequence;

        private readonly List<PlayerVitalEventSnapshot> m_pendingEvents =
            new List<PlayerVitalEventSnapshot>(4);
        private readonly List<PlayerVitalEventSnapshot> m_eventBatch =
            new List<PlayerVitalEventSnapshot>(MaxEventsPerMessage);
        private readonly Dictionary<int, uint> m_senderSequences = new Dictionary<int, uint>();
        private readonly Dictionary<int, uint> m_senderEventSequences = new Dictionary<int, uint>();
        // 表现事件（PLAYER_EFFECT_EVENTS = 109）：主机每帧轮询产生的待发事件 + 发送批。
        private readonly List<PlayerEffectEventSnapshot> m_effectPoll =
            new List<PlayerEffectEventSnapshot>(PlayerVitalsLimits.MaxEffectEntries);
        private readonly List<PlayerEffectEventSnapshot> m_pendingEffects =
            new List<PlayerEffectEventSnapshot>(PlayerVitalsLimits.MaxEffectEntries);
        private readonly List<PlayerEffectEventSnapshot> m_effectBatch =
            new List<PlayerEffectEventSnapshot>(PlayerVitalsLimits.MaxEffectEntries);
        private readonly Dictionary<int, uint> m_senderEffectSequences =
            new Dictionary<int, uint>();
        private uint m_effectSequence;
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
        // 95 上报的逐端过滤 / 距离分级（主机）
        /// <summary>因"该端兴趣窗口不含该角色"而未下发的观察者条目数。</summary>
        private long m_observerFilteredByWindow;
        /// <summary>窗口内但本拍被距离分级限流的观察者条目数（32–64 m / &gt;64 m 档）。</summary>
        private long m_observerThrottled;
        /// <summary>该端还没有上报兴趣窗口 → 观察者条目全部未下发（owner 条目照发）的条目数。</summary>
        private long m_observerWindowUnknown;
        /// <summary>该端没有 owner 条目（距离未知）→ 按最近一档下发的观察者条目数。</summary>
        private long m_observerOwnerPositionUnknown;
        /// <summary>因死亡 / 死因变化 / 进入强制睡眠边沿而绕过距离限流的观察者条目数。</summary>
        private long m_observerUrgent;
        /// <summary>成功入队的 owner 条目数（每端自己的权威状态，每拍必到）。</summary>
        private long m_ownerEntriesSent;
        /// <summary>逐端编码 95 上报失败的端次数（只跳过该端，不影响其它端）。</summary>
        private long m_perClientEncodeFailures;
        /// <summary>逐端过滤后条目为空而跳过编码的端次数（= 该端的 owner 条目还没被抓到）。</summary>
        private long m_perClientEmptyReports;
        // 表现事件（109）：主机产出 / 下发 / 失败；客户端收到 / 播放 / 拒绝。
        private long m_fluEffectCaptured;
        private long m_fluEffectSent;
        private long m_fluEffectSendFailures;
        private long m_fluEffectEncodeFailures;
        private long m_fluEffectDropped;
        private long m_fluEffectOverflow;
        private long m_fluEffectNoRecipients;
        private long m_effectPollFailures;
        private long m_fluEffectRecv;
        private long m_fluEffectPresented;
        private long m_fluEffectRejected;
        private long m_fluEffectDuplicate;
        private long m_fluEffectStale;
        private long m_fluEffectNoAvatar;
        private long m_fluEffectNonOwner;
        private long m_fluEffectMalformed;
        private long m_fluEffectWrongDirection;
        private readonly Dictionary<string, long> m_fluEffectRejectReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
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
        /// <summary>死亡屏输入仍是**同一个按下**（按住 / 键盘自动重复）而没有新按下沿的帧数。</summary>
        private long m_respawnSamePress;
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
        // 复活（客户端，引擎原生流程 + 主机批准 + 出生后权威重放）
        /// <summary>被主机以 <c>cooldown</c> 拒绝后重新武装、允许再点一次的死亡期数。</summary>
        private long m_respawnRetryArmed;
        /// <summary><c>TryExecuteLocalRespawn</c> 成功的次数（= mod 执行了引擎的 RemoveEntity）。</summary>
        private long m_engineRespawnExecuted;
        /// <summary><c>TryExecuteLocalRespawn</c> 失败的次数（每条都有原因日志）。</summary>
        private long m_engineRespawnFailures;
        /// <summary>新实体出生后权威重放完成的次数。</summary>
        private long m_respawnReplayApplied;
        /// <summary>权威重放失败的次数（原因已写日志）。</summary>
        private long m_respawnReplayFailures;
        /// <summary>等引擎新实体出生的帧数（PrepareSpawn / WaitForTerrain 期间）。</summary>
        private long m_respawnReplayWaitFrames;
        /// <summary>被死亡 epoch 丢弃的陈旧 Dead 快照数。</summary>
        private long m_respawnStaleDeadDropped;
        /// <summary>被死亡 epoch 丢弃的陈旧 Death 事件数。</summary>
        private long m_respawnStaleDeathDropped;
        /// <summary>Respawn 事件先于 Result 到达的次数（落点照收，执行仍等结果）。</summary>
        private long m_respawnEventBeforeResult;
        /// <summary>请求超时但主机 Respawn 事件已到达 → 按事件推进执行的次数。</summary>
        private long m_respawnExecutedWithoutResult;
        // 复活窗口（respawnInProgress）：RemoveEntity 之后 → 第一份主机权威存活快照之前。
        /// <summary>本机玩家键 → 复活窗口（Death 事件 / RemoveEntity / 第一份存活快照的门控）。</summary>
        private readonly Dictionary<PlayerKey, PlayerRespawnWindow> m_respawnWindows =
            new Dictionary<PlayerKey, PlayerRespawnWindow>();
        /// <summary>进入复活窗口的次数（= 主机批准后成功执行 RemoveEntity 的次数）。</summary>
        private long m_respawnWindowOpened;
        /// <summary>窗口内被拒绝的 Dead 快照 / Death 事件数（绝不写进引擎）。</summary>
        private long m_respawnWindowDeadSuppressed;
        /// <summary>窗口被主机权威存活快照关闭的次数（清理死亡表现成功的次数）。</summary>
        private long m_respawnWindowClosed;
        /// <summary>复活之后到达的旧死亡包（rev ≤ 复活 epoch）被丢弃的条数（窗口内外都算）。</summary>
        private long m_respawnWindowStaleDrops;
        /// <summary>关闭窗口时清理死亡表现失败的次数（端口返回 Rejected：窗口保持，下一拍重试）。</summary>
        private long m_respawnWindowCleanupFailures;
        /// <summary>
        /// 窗口内收到**比复活裁决更新**的死亡（rev &gt; respawn rev）= 复活后真正的新死亡：
        /// 窗口结束并按真实死亡应用（旧死亡 rev ≤ 复活裁决仍全部拒绝，绝不把真实死亡永久挡住）。
        /// </summary>
        private long m_respawnWindowNewDeath;
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

        // 入睡请求（id 104/105）：客户端申请 → 主机用自己引擎的 CanSleep + Sleep 裁决。
        private long m_sleepEntryTapCaptured;
        private long m_sleepEntrySent;
        private long m_sleepEntrySendFailures;
        private long m_sleepEntrySuppressed;
        private readonly Dictionary<string, long> m_sleepEntrySuppressedReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private long m_sleepEntryRecv;
        private long m_sleepEntryAccepted;
        private long m_sleepEntryRejected;
        private long m_sleepEntryMalformed;
        private long m_sleepEntryWrongDirection;
        private readonly Dictionary<string, long> m_sleepEntryRejectReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private long m_sleepEntryResultRecv;
        private long m_sleepEntryResultStale;
        private long m_sleepEntryResultSendFailures;
        private readonly Dictionary<string, long> m_sleepEntryObservedRejectReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// 客户端最近一次入睡申请的序号与时刻：用于"主机拒绝了，但客户端没有收到结果"的
        /// 超时判定（<see cref="SleepEntryPendingTimeoutMs"/>）。0 = 当前没有未决申请。
        /// </summary>
        private long m_sleepEntryPendingSequence;
        private long m_sleepEntryPendingAtMs;
        private long m_sleepEntryTimeouts;
        private readonly Dictionary<long, bool> m_sleepEntryHandled =
            new Dictionary<long, bool>();
        /// <summary>
        /// 旧自动复活路径已删除：状态行固定输出 <c>autoRespawnRemoved=1</c>，防止有人把
        /// 2.5 秒计时器加回来而没有被发现（源码守卫 + 状态行双重痕迹）。
        /// </summary>
        private const int AutoRespawnRemoved = 1;

        public PlayerVitalsModule(Func<double> now)
        {
            m_now = now ?? throw new ArgumentNullException(nameof(now));
            // 门控要能测"两次采样之间的间隔"（掉帧补偿 / 双击）：喂同一个时钟。
            m_respawnGate = new PlayerRespawnGate(NowMs);
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
            if (!context.TryGetPort(out m_windowPort) || m_windowPort == null)
            {
                // 软依赖：窗口端口缺失不能让整个生命 / 体征同步停掉（owner 条目 / 死亡 / 复活
                // 都不依赖它），但也**绝不**把观察者条目发回全体。明确退化为"只发 owner 条目"，
                // 每条被拦下的观察者条目都计入 observerWindowUnknown（状态行可见）。
                m_windowPort = null;
                Problem("window-port", "没有拿到兴趣窗口端口（IWindowBroadcastPort）→ 观察者条目" +
                    "一律不下发（只发每端自己的 owner 条目；被拦下的条目计 observerWindowUnknown，" +
                    "绝不改回发给全体）");
            }
            m_transport.ClientJoined += OnClientJoined;
            m_transport.ClientLeft += OnClientLeft;
            m_messages.Registry.RegisterModule(this);
            m_attached = true;
            Info("玩家生命 / 体征同步就绪：主机模拟全部角色并 5 Hz 变化 / 1 Hz 心跳下发（冻结进度 / 阶段随 95，" +
                "死亡与复活立刻），客户端只应用、本机生命 / 体征 / 睡眠 / 病程 / 火焰模拟被摘掉；" +
                "主机每帧轮询客户端化身的咳嗽 / 呕吐 / 着火表现并按 109 广播，客户端不再上报；" +
                "复活：主机批准 PLAYER_RESPAWN_REQUEST 后立即 TryRespawnAvatar，回执 + Respawn 事件 + 权威状态");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.PlayerVitalsReport, V.PlayerVitalsReport.Parser,
                new DelegateMessageHandler<V.PlayerVitalsReport>(OnReportMessage), ModuleId);
            registry.Register((int)MessageType.PlayerVitalsEvents, V.PlayerVitalsEvents.Parser,
                new DelegateMessageHandler<V.PlayerVitalsEvents>(OnEventsMessage), ModuleId);
            registry.Register((int)MessageType.PlayerEffectEvents, V.PlayerEffectEvents.Parser,
                new DelegateMessageHandler<V.PlayerEffectEvents>(OnEffectEventsMessage), ModuleId);
            registry.Register((int)MessageType.PlayerRespawnRequest, R.PlayerRespawnRequest.Parser,
                new DelegateMessageHandler<R.PlayerRespawnRequest>(OnRespawnRequestMessage), ModuleId);
            registry.Register((int)MessageType.PlayerRespawnResult, R.PlayerRespawnResult.Parser,
                new DelegateMessageHandler<R.PlayerRespawnResult>(OnRespawnResultMessage), ModuleId);
            registry.Register((int)MessageType.PlayerWakeRequest, V.PlayerWakeRequest.Parser,
                new DelegateMessageHandler<V.PlayerWakeRequest>(OnWakeRequestMessage), ModuleId);
            registry.Register((int)MessageType.PlayerSleepRequest, V.PlayerSleepRequest.Parser,
                new DelegateMessageHandler<V.PlayerSleepRequest>(OnSleepRequestMessage), ModuleId);
            registry.Register((int)MessageType.PlayerSleepResult, V.PlayerSleepResult.Parser,
                new DelegateMessageHandler<V.PlayerSleepResult>(OnSleepResultMessage), ModuleId);
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
            m_effectPoll.Clear();
            m_pendingEffects.Clear();
            m_effectBatch.Clear();
            m_senderEffectSequences.Clear();
            ResetLocalRespawns("模块下线");
            m_respawnGate.Clear();
            m_terminalDeathHandled.Clear();
            m_sleepGate.Clear();
            m_sleepRequestGate.Clear();
            m_sleepEntryHandled.Clear();
            m_sleepEntryPendingSequence = 0;
            m_sleepEntryPendingAtMs = 0;
            m_sleepKeys.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_clientWindows.Clear();
            m_clientSendEntries.Clear();
            m_sendEntryUrgent.Clear();
            m_ownerPositions.Clear();
            m_observerSendCounts.Clear();
            m_lastProblemMs.Clear();
            m_reportedOnce.Clear();
            m_messages = null;
            m_port = null;
            m_sleepPort = null;
            m_broadcastPort = null;
            m_windowPort = null;
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
            if (!inWorld)
            {
                m_worldReady = false;
                // 离开世界：死亡 / 复活状态不能留在内存里（它的 GuiWidget 已经不在了）。
                ResetLocalRespawns("离开世界");
            }

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
                if (m_pendingEffects.Count > 0)
                {
                    m_fluEffectNoRecipients += m_pendingEffects.Count;
                    m_pendingEffects.Clear();
                }
                return;
            }

            TickHostEdges(nowMs);
            FlushPendingEvents();
            TickHostEffects();
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
            int hudRefreshed = m_port.RefreshLocalPresentation(deltaTime, out string hudReason);
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
            TickLocalRespawnReplay(nowMs);
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

        /// <summary>
        /// 主机侧每帧：轮询**主机模拟的客户端化身**的表现上升沿（咳嗽 / 喷嚏 / 恶心 / 呕吐 /
        /// 着火 / 熄灭），排队后立刻广播（PLAYER_EFFECT_EVENTS = 109）。
        ///
        /// 为什么不用 5 Hz 的体征抓取：喷嚏的计时只有 1 秒、咳嗽动作 4 秒，5 Hz 抓取可能整段
        /// 错过；每帧读几个私有计时字段是廉价且确定的。扣血仍由 95 上报下发，本消息只描述表现。
        /// </summary>
        private void TickHostEffects()
        {
            if (m_port == null) return;
            m_effectPoll.Clear();
            int captured = m_port.PollAuthorityEffects(m_effectPoll, out string reason);
            if (!string.IsNullOrEmpty(reason))
            {
                m_effectPollFailures++;
                Problem("effect-poll", "主机表现事件轮询失败：" + reason +
                    "（本拍没有新的表现事件；扣血仍由体征上报下发）");
            }
            if (captured > 0 && m_effectPoll.Count > 0)
            {
                m_fluEffectCaptured += m_effectPoll.Count;
                m_pendingEffects.AddRange(m_effectPoll);
            }
            int cap = PlayerVitalsLimits.MaxEffectEntries * 4;
            if (m_pendingEffects.Count > cap)
            {
                int drop = m_pendingEffects.Count - cap;
                m_pendingEffects.RemoveRange(0, drop);
                m_fluEffectOverflow += drop;
                Problem("effect-overflow", "表现事件积压 " + m_pendingEffects.Count +
                    " 条超过上限 " + cap + " → 丢弃最旧 " + drop +
                    " 条（客户端本来也会按 TTL 丢弃过期事件）");
            }
            if (m_clients.Count == 0)
            {
                if (m_pendingEffects.Count > 0)
                {
                    m_fluEffectNoRecipients += m_pendingEffects.Count;
                    m_pendingEffects.Clear();
                }
                return;
            }
            FlushPendingEffects();
        }

        /// <summary>
        /// 把待发表情事件广播给每个在线玩家端（单条 ≤ <see cref="PlayerVitalsLimits.MaxEffectEntries"/>
        /// 条）。发送失败**保留**待发事件下一帧重试（客户端按每键 sequence 去重，重复不会重播）。
        /// </summary>
        private void FlushPendingEffects()
        {
            if (m_pendingEffects.Count == 0) return;
            int index = 0;
            int deliveredClients = 0;
            int failedClients = 0;
            int deliveredEvents = 0;
            while (index < m_pendingEffects.Count)
            {
                int take = Math.Min(PlayerVitalsLimits.MaxEffectEntries,
                    m_pendingEffects.Count - index);
                m_effectBatch.Clear();
                for (int i = 0; i < take; i++) m_effectBatch.Add(m_pendingEffects[index + i]);
                m_effectSequence = NextRevision(m_effectSequence);
                byte[] payload = PlayerVitalsCodec.EncodeEffects(m_effectSequence, m_effectBatch,
                    out int dropped, out string error);
                if (payload == null)
                {
                    m_fluEffectEncodeFailures++;
                    Problem("encode-effects", "表现事件编码失败（" + (error ?? "未知") +
                        "）→ 本批丢弃，计数 " + m_fluEffectEncodeFailures);
                    break;
                }
                if (dropped > 0)
                {
                    m_fluEffectDropped += dropped;
                    Problem("effects-overflow", "表现事件超过单条上限 " +
                        PlayerVitalsLimits.MaxEffectEntries + " → 丢弃 " + dropped +
                        " 条（不静默截断）");
                }
                CollectClients();
                int batchDelivered = 0;
                for (int c = 0; c < m_clientList.Count; c++)
                {
                    int clientId = m_clientList[c];
                    if (!m_transport.TrySendTo(clientId, MessageType.PlayerEffectEvents, payload))
                    {
                        failedClients++;
                        m_fluEffectSendFailures++;
                        continue;
                    }
                    deliveredClients++;
                    batchDelivered++;
                }
                if (batchDelivered > 0) deliveredEvents += take;
                index += take;
            }
            if (failedClients > 0)
            {
                Problem("effects-send", "表现事件有 " + failedClients +
                    " 个玩家端发送失败 → 保留并下一帧重试（客户端按每键 sequence 去重）");
                return;
            }
            if (deliveredEvents > 0)
            {
                m_fluEffectSent += deliveredEvents;
                m_pendingEffects.Clear();
            }
            else
            {
                m_fluEffectNoRecipients += m_pendingEffects.Count;
                m_pendingEffects.Clear();
            }
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
        /// **逐端**构造"自己的 owner 条目 + 窗口内观察者条目（按距离分级限流）"，
        /// 每个端各自编码一份 95 上报后定向发送。
        ///
        /// 收件人 / 条目口径（2026-10 本次改动）：
        ///   · owner 条目（<c>entry.Key.ClientId == clientId</c>）= 该端自己的权威状态，
        ///     **永远包含**、不看窗口、不受限流（死亡屏 / HUD / 复活都靠它，迟一拍都是故障）；
        ///   · 观察者条目只发给"该端兴趣窗口 Contains(entry.Position, 0)"的端（与地形下发 /
        ///     实体同步同一份窗口）；该端还没上报窗口 → 观察者条目全部不发，计
        ///     observerWindowUnknown（绝不改回发给全体）；
        ///   · 观察者条目按"该端自己角色位置 → 条目位置"的水平距离分级限流：≤32 m 每拍、
        ///     32–64 m 每 2 拍、&gt;64 m 每 4 拍；heartbeat / m_forceSend 整表重发，
        ///     死亡 / 死因变化 / 进入强制睡眠这三个边沿也绕过限流（见 <see cref="IsUnthrottledEdge"/>）。
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
            m_sendEntryUrgent.Clear();
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
                // 边沿必须在覆盖 LastSent 之前判定（首份样本也算边沿：加入 / 重连后的第一份必须完整）。
                bool urgent = !state.HasSent || IsUnthrottledEdge(snapshot, state.LastSent);
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
                m_sendEntryUrgent.Add(urgent);
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
            // 本拍窗口快照：主机玩家端 ≤4，线性查找即可（不每拍建字典 / 不分配大对象）。
            if (m_windowPort != null)
            {
                m_windowPort.CaptureClientWindows(m_clientWindows);
            }
            else
            {
                // 端口缺失：观察者条目在下面按"窗口未知"逐条拦下并计数（owner 条目照发）。
                m_clientWindows.Clear();
            }
            bool bypassThrottle = heartbeat || m_forceSend;
            CollectClients();
            int delivered = 0;
            int failures = 0;
            int encodeFailures = 0;
            for (int c = 0; c < m_clientList.Count; c++)
            {
                int clientId = m_clientList[c];
                bool hasWindow = TryGetClientWindow(clientId, out ClientWindowView window);
                bool warnedWindowUnknown = false;
                bool warnedOwnerPositionUnknown = false;
                m_clientSendEntries.Clear();
                m_ownerPositions.Clear();
                // 先收 owner 条目：权威抓取顺序是"主机本机角色在前、客户端化身在后"，一趟循环时
                // owner 的位置可能还没填进 m_ownerPositions，前面的观察者条目会被误判成"距离未知"。
                for (int e = 0; e < m_sendEntries.Count; e++)
                {
                    PlayerVitalsSnapshot entry = m_sendEntries[e];
                    if (entry.Key.ClientId != clientId) continue;
                    // owner 条目：该端自己的权威状态永远每拍到（不看窗口、不受限流）。
                    m_clientSendEntries.Add(entry);
                    m_ownerPositions.Add(entry.Position);
                }
                for (int e = 0; e < m_sendEntries.Count; e++)
                {
                    PlayerVitalsSnapshot entry = m_sendEntries[e];
                    if (entry.Key.ClientId == clientId) continue;
                    if (!hasWindow)
                    {
                        // 该端还没上报窗口（或窗口端口缺失）→ 观察者条目一条都不发。
                        m_observerWindowUnknown++;
                        if (!warnedWindowUnknown)
                        {
                            warnedWindowUnknown = true;
                            Problem("observer-window-unknown:" + clientId, "玩家端 clientId=" +
                                clientId + " 还没有可用的兴趣窗口 → 本拍观察者条目全部不下发" +
                                "（owner 条目照发；窗口端口" +
                                (m_windowPort == null ? "缺失" : "在线，等它上报窗口") + "）");
                        }
                        continue;
                    }
                    if (!window.Contains(entry.Position, 0))
                    {
                        m_observerFilteredByWindow++;
                        continue;
                    }
                    bool urgent = bypassThrottle || m_sendEntryUrgent[e];
                    if (!urgent)
                    {
                        float distance = ClosestOwnerDistance(entry.Position,
                            out bool ownerPositionKnown);
                        if (!ownerPositionKnown)
                        {
                            m_observerOwnerPositionUnknown++;
                            if (!warnedOwnerPositionUnknown)
                            {
                                warnedOwnerPositionUnknown = true;
                                Problem("observer-owner-unknown:" + clientId, "玩家端 clientId=" +
                                    clientId + " 没有 owner 条目（主机还没抓到它的角色位置）→ " +
                                    "观察者条目按最近一档下发（每拍都发，宁可多发也不能把状态卡住）");
                            }
                        }
                        uint interval = ObserverSendInterval(distance);
                        if (interval > 1 &&
                            NextObserverOpportunity(clientId, entry.Key) < interval)
                        {
                            m_observerThrottled++;
                            continue;
                        }
                        ResetObserverOpportunity(clientId, entry.Key);
                    }
                    else
                    {
                        if (!bypassThrottle) m_observerUrgent++;
                        ResetObserverOpportunity(clientId, entry.Key);
                    }
                    m_clientSendEntries.Add(entry);
                }
                if (m_clientSendEntries.Count == 0)
                {
                    // owner 条目本应永远非空；这里出现 = 主机还没抓到该端的角色（化身未出生）。
                    // 不编码空上报（没有意义），计数 + 限流日志，绝不当成成功。
                    m_perClientEmptyReports++;
                    Problem("report-client-empty:" + clientId, "玩家端 clientId=" + clientId +
                        " 本拍没有任何可发条目（没有它的 owner 条目：主机还没抓到它的化身）" +
                        "→ 跳过该端（不编码空上报；下一拍重试）");
                    continue;
                }
                byte[] payload = PlayerVitalsCodec.EncodeReport(m_reportSequence,
                    m_clientSendEntries, out int dropped, out string encodeError);
                if (payload == null)
                {
                    encodeFailures++;
                    m_perClientEncodeFailures++;
                    m_encodeFailures++;
                    Problem("encode-report:" + clientId, "玩家端 clientId=" + clientId +
                        " 的生命 / 体征上报编码失败（" + (encodeError ?? "未知") +
                        "）→ 只跳过该端（其它端照发；绝不夹紧 / 补默认值）");
                    continue;
                }
                if (dropped > 0)
                {
                    Problem("report-overflow:" + clientId, "玩家端 clientId=" + clientId +
                        " 的上报条目超过单份上限 " + PlayerVitalsLimits.MaxReportEntries +
                        " → 丢弃 " + dropped + " 条");
                }
                if (!m_transport.TrySendTo(clientId, MessageType.PlayerVitalsReport, payload))
                {
                    failures++;
                    m_sendFailures++;
                    continue;
                }
                delivered++;
                // owner 条目 = 这个端自己的角色；其余 = 观察者转发（姿势 / HUD）。
                for (int e = 0; e < m_clientSendEntries.Count; e++)
                {
                    if (m_clientSendEntries[e].Key.ClientId == clientId)
                    {
                        m_vitalsSent++;
                        m_ownerEntriesSent++;
                    }
                    else
                    {
                        m_broadcast++;
                    }
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
            // 编码失败保留 m_forceSend：下一拍整表重试（与原"全局编码失败下一拍重发"同口径）。
            // 只跳过受影响的那一端，其它端已经收到，不受影响。
            if (encodeFailures == 0) m_forceSend = false;
        }

        /// <summary>
        /// "永不限流"的观察者边沿：死亡 / 复活（<see cref="PlayerVitalsSnapshot.Dead"/> 翻转）、
        /// 死因变化、进入强制睡眠（Sleeping &amp;&amp; ForcedSleep 从 false 变 true）。
        ///
        /// 依据：这三个字段是**离散边沿**（不是每拍变化的连续值），所以"本拍快照 vs 上一次
        /// 抓取（<see cref="HostCharacterState.LastSent"/>）"的差异等价于"与上次已发条目"的差异；
        /// 首份样本由调用方按 <see cref="HostCharacterState.HasSent"/> == false 处理。
        /// 连续值（health / temperature / sleep_elapsed …）**不**走这里，它们照距离分级限流。
        /// </summary>
        private static bool IsUnthrottledEdge(in PlayerVitalsSnapshot current,
            in PlayerVitalsSnapshot previous)
        {
            if (current.Dead != previous.Dead) return true;
            if (!string.Equals(current.CauseOfDeath, previous.CauseOfDeath,
                    StringComparison.Ordinal)) return true;
            bool forcedSleep = current.Sleeping && current.ForcedSleep;
            bool wasForcedSleep = previous.Sleeping && previous.ForcedSleep;
            return forcedSleep && !wasForcedSleep;
        }

        /// <summary>线性查找某个端的兴趣窗口（≤4 端；不建字典 / 不分配）。</summary>
        private bool TryGetClientWindow(int clientId, out ClientWindowView window)
        {
            for (int i = 0; i < m_clientWindows.Count; i++)
            {
                if (m_clientWindows[i].ClientId != clientId) continue;
                window = m_clientWindows[i];
                return true;
            }
            window = default;
            return false;
        }

        /// <summary>
        /// 观察者条目到"该端自己角色"的最近水平距离（米）。分屏时一个端可能有多个 owner 条目：
        /// 取最近的（= 最需要新鲜状态的那个本机视角）。距离未知（该端没有 owner 条目 /
        /// 还没抓到它的角色）时返回 false，调用方按最近一档处理并计数。
        /// </summary>
        private float ClosestOwnerDistance(Vector3 position, out bool ownerKnown)
        {
            ownerKnown = m_ownerPositions.Count > 0;
            if (!ownerKnown) return 0f;
            float bestSquared = float.MaxValue;
            for (int i = 0; i < m_ownerPositions.Count; i++)
            {
                Vector3 owner = m_ownerPositions[i];
                float dx = position.X - owner.X;
                float dz = position.Z - owner.Z;
                float squared = dx * dx + dz * dz;
                if (squared < bestSquared) bestSquared = squared;
            }
            return (float)Math.Sqrt(bestSquared);
        }

        /// <summary>距离 → 发送节拍：1 = 每拍，2 = 每 2 拍，4 = 每 4 拍（≤32 m / ≤64 m / 更远）。</summary>
        private static uint ObserverSendInterval(float distance)
        {
            if (distance <= ObserverNearRangeM) return 1;
            if (distance <= ObserverMidRangeM) return 2;
            return 4;
        }

        /// <summary>该 (端, 被观察角色) 的"距上次发送机会数" +1（只在 2 拍 / 4 拍档位用）。</summary>
        private uint NextObserverOpportunity(int clientId, PlayerKey key)
        {
            if (!m_observerSendCounts.TryGetValue(clientId,
                    out Dictionary<PlayerKey, uint> counts))
            {
                counts = new Dictionary<PlayerKey, uint>(PlayerKey.SlotCount * 2);
                m_observerSendCounts[clientId] = counts;
            }
            counts.TryGetValue(key, out uint count);
            count++;
            counts[key] = count;
            return count;
        }

        /// <summary>这一条观察者条目本拍发了（或走了不限流路径）→ 节拍计数归零。</summary>
        private void ResetObserverOpportunity(int clientId, PlayerKey key)
        {
            if (!m_observerSendCounts.TryGetValue(clientId,
                    out Dictionary<PlayerKey, uint> counts))
                return;
            if (counts.TryGetValue(key, out uint count) && count != 0) counts[key] = 0;
        }

        /// <summary>
        /// 某个玩家端离开 / 它的化身被移除 → 清掉各端为它攒的"距离分级节拍"键，
        /// 防止长会话里 (端, 已离开角色) 的键无限累积。只在成员变化时调用，热路径不分配。
        /// </summary>
        private void DropObservedSendCounts(int observedClientId)
        {
            if (m_observerSendCounts.Count == 0) return;
            var stale = new List<PlayerKey>();
            foreach (Dictionary<PlayerKey, uint> counts in m_observerSendCounts.Values)
            {
                stale.Clear();
                foreach (PlayerKey key in counts.Keys)
                    if (key.ClientId == observedClientId) stale.Add(key);
                for (int i = 0; i < stale.Count; i++) counts.Remove(stale[i]);
            }
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
                bool local = IsLocalKey(snapshot.Key);
                // 先把本机模拟摘掉（幂等）：即使这一条是被复活窗口拒绝的死亡状态，新实体也已经
                // 出生，不能让它的生命 / 体征在本端继续模拟。
                if (local) EnsureLocalSimulationDisabled();
                // 复活窗口（respawnInProgress）：mod 已执行 RemoveEntity，新实体还没有拿到第一份
                // 主机权威存活快照。窗口内任何 Dead 状态都可能是复活之前那次死亡的迟到包
                // （实机：Death rev=3513 与 Respawn rev=3514 交错），一律拒绝，绝不再挂死亡屏。
                if (local && snapshot.Dead &&
                    TrySuppressRespawnWindowDeath(snapshot.Key, snapshot.Revision,
                        "权威死亡状态", snapshot.CauseOfDeath, isEvent: false))
                    continue;
                if (state.LastRevision > 0 && snapshot.Revision < state.LastRevision)
                {
                    m_vitalsStale++;
                    if (local && snapshot.Dead && LocalEpoch(snapshot.Key) != 0)
                        m_respawnWindowStaleDrops++;
                    Problem("stale-entry:" + snapshot.Key,
                        "丢弃玩家键 " + snapshot.Key + " 的陈旧状态（revision=" +
                        snapshot.Revision + " < 已应用 " + state.LastRevision + "）");
                    continue;
                }
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
                    if (local && snapshot.Dead && LocalEpoch(snapshot.Key) != 0)
                        m_respawnWindowStaleDrops++;
                    continue;
                }
                // 死亡 epoch：复活之前产生的 Dead 快照不许写进新实体。
                // 引擎 PlayerData.Playing 只看 Health <= 0，写进去就会让刚出生的角色立刻再判一次死
                // （见 PlayerRespawnEpoch 的注释）。这里丢弃 + 计数 + 限流日志，不猜。
                // epoch 统一为"主机复活 revision"（PlayerRespawnEpoch.ForRespawn），与端口 / 日志同源。
                if (local && snapshot.Dead &&
                    PlayerRespawnEpoch.IsStaleDead(LocalEpoch(snapshot.Key), snapshot.Revision))
                {
                    m_respawnStaleDeadDropped++;
                    m_respawnWindowStaleDrops++;
                    Problem("respawn-stale-dead:" + snapshot.Key,
                        "丢弃玩家键 " + snapshot.Key + " 的陈旧死亡状态（revision=" +
                        snapshot.Revision + " <= 复活 epoch " + LocalEpoch(snapshot.Key) +
                        "）：它是复活之前的状态，写进新实体会被引擎再判一次死（已丢弃 " +
                        m_respawnStaleDeadDropped + " 条）");
                    continue;
                }
                // 关闭窗口的判定必须在**应用之前**取：应用后窗口状态 / 快照都会被覆盖。
                PlayerRespawnWindow window = null;
                bool closingWindow = local && !snapshot.Dead &&
                    TryGetActiveRespawnWindow(snapshot.Key, out window) &&
                    window.CanCloseOnAlive(snapshot.Revision, snapshot.Health);
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
                    if (closingWindow)
                    {
                        m_respawnWindowCleanupFailures++;
                        Problem("respawn-window-cleanup:" + snapshot.Key,
                            "复活窗口关闭失败：清理死亡表现被端口拒绝（玩家键 " + snapshot.Key +
                            " rev=" + snapshot.Revision + "）：" + (reason ?? "端口没有给原因") +
                            " → 保持窗口并下一拍重试（累计 " + m_respawnWindowCleanupFailures +
                            " 次；挡门框 / 死亡屏可能仍在）");
                    }
                    continue;
                }
                bool wasSleeping = state.HasApplied && state.LastApplied.Sleeping;
                state.HasApplied = true;
                state.LastApplied = snapshot;
                state.LastRevision = snapshot.Revision;
                m_clientVitalsApplied++;
                if (closingWindow) CloseRespawnWindow(snapshot.Key, window, snapshot.Revision);
                if (local)
                {
                    if (snapshot.Sleeping)
                    {
                        // 主机已确认这一次睡眠：入睡申请门控收工（本次按下不再产生新请求），
                        // 未决序号也清掉 —— 否则会被 3 秒超时误判成"主机没裁决"。
                        m_sleepRequestGate.OnSleepConfirmed(snapshot.Key);
                        if (m_sleepEntryPendingSequence != 0)
                        {
                            m_sleepEntryPendingSequence = 0;
                            m_sleepEntryPendingAtMs = 0;
                        }
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
                bool local = IsLocalKey(evt.Key);
                if (local) EnsureLocalSimulationDisabled();
                // 复活窗口（respawnInProgress）：窗口内任何 Death 事件都可能是复活之前那次死亡的
                // 迟到包，一律拒绝（不 ArmLocalDeath、不挂挡门框 / 死亡屏）。
                if (local && evt.IsDeath &&
                    TrySuppressRespawnWindowDeath(evt.Key, evt.Revision, "Death 事件",
                        evt.CauseOfDeath, isEvent: true))
                    continue;
                if (state.LastEventRevision > 0 && evt.Revision <= state.LastEventRevision)
                {
                    if (evt.Revision < state.LastEventRevision) m_vitalsStale++;
                    else m_vitalsUnchanged++;
                    if (local && evt.IsDeath && LocalEpoch(evt.Key) != 0)
                        m_respawnWindowStaleDrops++;
                    continue;
                }
                // 死亡 epoch：复活之前产生的 Death 事件不能再把新实体推进 PlayerDead。
                // （epoch = 主机复活裁决 revision；复活后的真正新死亡 revision 一定大于它）。
                if (local && evt.IsDeath &&
                    PlayerRespawnEpoch.IsStaleDead(LocalEpoch(evt.Key), evt.Revision))
                {
                    m_respawnStaleDeathDropped++;
                    m_respawnWindowStaleDrops++;
                    state.LastEventRevision = evt.Revision;
                    Problem("respawn-stale-death:" + evt.Key,
                        "丢弃玩家键 " + evt.Key + " 的陈旧 Death 事件（rev=" + evt.Revision +
                        " <= 复活 epoch " + LocalEpoch(evt.Key) +
                        "）：它属于复活之前的那次死亡（已丢弃 " +
                        m_respawnStaleDeathDropped + " 条）");
                    continue;
                }
                // 本机角色的复活事件：不写旧实体（它已经 / 正在被删），
                // 只记落点 + 推进 death epoch；真正的落点 / 数值重放在新实体出生后做
                // （TickLocalRespawnReplay → TryReplayLocalRespawn）。
                if (local && !evt.IsDeath)
                {
                    state.LastEventRevision = evt.Revision;
                    if (evt.Revision > state.LastRevision) state.LastRevision = evt.Revision;
                    RecordLocalRespawnEvent(in evt);
                    m_respawnApplied++;
                    Info("主机裁决玩家键 " + evt.Key + " 复活（rev=" + evt.Revision +
                        " · 落点=" + (evt.HasPosition ? evt.Position.ToString() : "未提供") +
                        "）→ 落点已记下，等引擎新实体出生后整体重放");
                    continue;
                }
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
                    Info("主机裁决玩家键 " + evt.Key + " 复活（rev=" + evt.Revision + " · 落点=" +
                        (evt.HasPosition ? evt.Position.ToString() : "未提供") +
                        "）→ 远端化身按主机落点复位（本机角色的复活走出生后权威重放）");
                }
            }
        }

        // ---------------------------------------------------------------- 表现事件（109）

        /// <summary>
        /// 客户端收到主机广播的表现事件：校验方向 / 消息序号 / 每条值的合法性 → 只对**自己的**
        /// 玩家键播放（observer 的键只计数 <c>fluEffectNonOwner</c>，本次修复范围是客户端自己的
        /// 化身）→ 交给端口播放（音效 / 粒子 / 覆盖层）。去重 / 过期判定在端口里做，模块只计数。
        /// </summary>
        private void OnEffectEventsMessage(MessageContext context, V.PlayerEffectEvents message)
        {
            if (!m_attached) return;
            if (m_control != null && m_control.IsHosting)
            {
                m_fluEffectWrongDirection++;
                Problem("client-effects", "主机收到 clientId=" + context.ClientId +
                    " 的表现事件（客户端不得上报表现）→ 整条拒绝，共 " +
                    CountEffectEntries(message) + " 条");
                return;
            }
            if (context.ClientId != 0)
            {
                m_fluEffectWrongDirection++;
                Problem("client-effects-sender", "客户端收到来自 clientId=" + context.ClientId +
                    " 的表现事件（只有主机能下发）→ 整条拒绝，共 " +
                    CountEffectEntries(message) + " 条");
                return;
            }
            if (!m_worldReady)
            {
                m_notReadyDrops++;
                return;
            }
            if (!PlayerVitalsCodec.TryConvertEffects(message, out uint sequence,
                    out List<PlayerEffectEventSnapshot> entries, out string error))
            {
                m_decodeFailures++;
                m_fluEffectMalformed += CountEffectEntries(message);
                Problem("malformed-effects", "主机下发的表现事件解码失败：" + error + "（整条丢弃）");
                return;
            }
            int sender = 0;
            if (m_senderEffectSequences.TryGetValue(sender, out uint lastSequence) &&
                sequence <= lastSequence)
            {
                m_fluEffectStale += entries.Count;
                Problem("stale-effects", "丢弃主机的陈旧表现事件（sequence=" + sequence +
                    " ≤ 已处理 " + lastSequence + "，共 " + entries.Count + " 条）");
                return;
            }
            m_senderEffectSequences[sender] = sequence;
            for (int i = 0; i < entries.Count; i++)
            {
                PlayerEffectEventSnapshot evt = entries[i];
                m_fluEffectRecv++;
                if (!evt.TryValidate(out string itemError))
                {
                    m_fluEffectMalformed++;
                    Problem("malformed-effect:" + evt.Key, "主机下发的第 " + (i + 1) +
                        " 条表现事件非法：" + itemError + " → 丢弃该条");
                    continue;
                }
                if (!IsLocalKey(evt.Key))
                {
                    // 其它玩家端化身的同类表现不在本次范围（要发声也得有一套 observer 表现，
                    // 不能半实现）：显式计数，不静默播放到错误的目标身上。
                    m_fluEffectNonOwner++;
                    continue;
                }
                PlayerEffectApplyStatus status = m_port.ApplyEffect(in evt, out string reason);
                switch (status)
                {
                    case PlayerEffectApplyStatus.Applied:
                        m_fluEffectPresented++;
                        break;
                    case PlayerEffectApplyStatus.Duplicate:
                        m_fluEffectDuplicate++;
                        break;
                    case PlayerEffectApplyStatus.Stale:
                        m_fluEffectStale++;
                        Problem("effect-stale:" + evt.Key, "丢弃过期的表现事件（" +
                            evt.Describe() + "）：" + (reason ?? "端口没有给原因"));
                        break;
                    case PlayerEffectApplyStatus.NoAvatar:
                        m_fluEffectNoAvatar++;
                        Problem("effect-no-avatar:" + evt.Key, "收到玩家键 " + evt.Key +
                            " 的表现事件，但本机没有它的本机角色（" +
                            (reason ?? "端口没有给原因") + "）→ 丢弃（不猜目标）");
                        break;
                    default:
                        m_fluEffectRejected++;
                        Bump(m_fluEffectRejectReasons, reason);
                        Problem("effect-rejected:" + evt.Key, "播放玩家键 " + evt.Key +
                            " 的表现事件失败：" + (reason ?? "端口没有给原因"));
                        break;
                }
            }
        }

        private static int CountEffectEntries(V.PlayerEffectEvents message) =>
            message?.Entries?.Count ?? 0;

        // ---------------------------------------------------------------- 客户端：死亡屏 / 复活请求

        /// <summary>
        /// 登记一次本机角色死亡。死亡画面本身现在完全由**引擎原生**负责：端口写
        /// <c>CauseOfDeath</c> + <c>Health = 0</c> 之后，引擎 <c>PlayerData.Playing</c> 自己进入
        /// <c>PlayerDead</c>（DeathCamera + “YOU HAVE DIED” + 等级减半，<c>PlayerData.cs:270-300</c>）。
        /// 这里只负责：记下这次死亡的 revision（后续复活 epoch 的下界）、1.5 秒点击门槛、
        /// 每次死亡最多一个请求、结果 / 超时后不再响应的状态机。重复的死亡快照 / 事件不重复计数。
        /// </summary>
        private void ArmLocalDeath(PlayerKey key, uint revision, string cause, long nowMs,
            bool isEventEdge)
        {
            if (m_port == null) return;
            PlayerRespawnMode mode = m_port.GetRespawnMode(out string modeReason);
            if (mode == PlayerRespawnMode.Unknown)
            {
                Problem("respawn-mode:" + key, "读不到世界复活模式（" + (modeReason ?? "端口没有给原因") +
                    "）→ 死亡不提供请求路径（不猜模式）");
            }
            if (!m_respawnGate.OnDeath(key, revision, mode, nowMs, isEventEdge)) return;
            // **新的死亡期**：上一轮复活可能还没走完（真实新死亡提前到来）。必须作废旧状态，
            // 否则 ExecuteLocalRespawn 会因 Executed=true 直接返回、窗口会带着上一轮的
            // RespawnRevision（把本次死亡的迟到包误判成"复活后新死亡"而放行）。
            if (m_localRespawns.TryGetValue(key, out LocalRespawnState previous) &&
                (previous.Executed || previous.AcceptedAtMs != 0 || previous.ReplayApplied))
            {
                m_localRespawns.Remove(key);
                m_respawnWindows.Remove(key);
                Info("玩家键 " + key + " 的新死亡作废上一轮未完成的复活状态（Executed=" +
                    previous.Executed + "，AcceptedAt=" + previous.AcceptedAtMs +
                    "，ReplayApplied=" + previous.ReplayApplied + "）→ 本次死亡重新走请求 / 批准");
            }
            LocalRespawnState local = GetOrCreateLocalRespawn(key);
            local.DeathRevision = PlayerRespawnEpoch.Advance(local.DeathRevision, revision);
            local.RespawnRevision = 0;
            local.HasPosition = false;
            local.Position = Vector3.Zero;
            local.AcceptedAtMs = 0;
            local.Executed = false;
            local.ReplayApplied = false;
            local.WaitingSinceMs = 0;
            local.WaitFrames = 0;
            m_terminalDeathHandled.Remove(key);
            m_deathScreensShown++;
            Info("本机角色 " + key + " 死亡（rev=" + revision + "，模式=" +
                PlayerRespawnRules.Describe(mode) + "，死因=" +
                (string.IsNullOrEmpty(cause) ? "未记录" : cause) +
                "）→ 引擎原生死亡画面；1.5 秒门槛后" +
                (PlayerRespawnRules.AllowsRequest(mode)
                    ? "玩家点击发一次复活请求，主机批准后才由 mod 执行 RemoveEntity"
                    : "点击走不可复活模式的终止路径，不发复活请求"));
        }

        private void ClearLocalDeath(PlayerKey key, string why)
        {
            m_respawnGate.OnRespawn(key);
            m_terminalDeathHandled.Remove(key);
        }

        // ---------------------------------------------------------------- 客户端：复活执行 / 出生后重放

        /// <summary>本槽位的死亡 epoch（0 = 还没复活过）。</summary>
        private uint LocalEpoch(PlayerKey key) =>
            m_localRespawnEpoch.TryGetValue(key, out uint epoch) ? epoch : 0u;

        private PlayerRespawnWindow GetOrCreateRespawnWindow(PlayerKey key)
        {
            if (!m_respawnWindows.TryGetValue(key, out PlayerRespawnWindow window))
            {
                window = new PlayerRespawnWindow();
                m_respawnWindows[key] = window;
            }
            return window;
        }

        private bool TryGetActiveRespawnWindow(PlayerKey key,
            out PlayerRespawnWindow window)
        {
            if (m_respawnWindows.TryGetValue(key, out window) && window.Active) return true;
            window = null;
            return false;
        }

        /// <summary>
        /// 复活窗口内收到死亡状态 / Death 事件：**默认拒绝**（返回 true = 已丢弃，调用方 continue）。
        /// 计数 <c>respawnWindowDeadSuppressed</c>；rev ≤ epoch 的迟到旧包另计
        /// <c>respawnWindowStaleDrops</c>，并与 <c>respawnStaleDead/StaleDeath</c> 同源计数
        /// （保持既有状态行语义）。唯一的出口是"比复活裁决更新"的真实新死亡
        /// （<see cref="PlayerRespawnWindow.IsDeathAfterRespawn"/>）：结束窗口并**按真实死亡应用**。
        ///
        /// 为什么真实新死亡必须放行：如果连 rev &gt; respawn 的死亡也一直拒绝，玩家复活后立刻
        /// 再次死亡时客户端会永远停在"活着"的画面，主机却已经判死（真实边界，非兜底）。
        /// 旧死亡（rev ≤ 复活裁决 rev）仍然一条都进不来。
        /// </summary>
        private bool TrySuppressRespawnWindowDeath(PlayerKey key, uint revision, string kind,
            string cause, bool isEvent)
        {
            if (!TryGetActiveRespawnWindow(key, out PlayerRespawnWindow window)) return false;
            if (window.IsDeathAfterRespawn(revision))
            {
                m_respawnWindowNewDeath++;
                window.Close();
                m_respawnWindows.Remove(key);
                Problem("respawn-window-new-death:" + key,
                    "复活窗口内收到**比复活裁决更新**的" + kind + "（玩家键 " + key + " rev=" +
                    revision + " > respawn=" + window.RespawnRevision + "，死因=" +
                    (string.IsNullOrEmpty(cause) ? "未记录" : cause) +
                    "）= 复活后真正的新死亡 → 结束窗口并按真实新死亡应用（旧死亡 rev ≤ respawn " +
                    "仍然一条都进不来；累计 " + m_respawnWindowNewDeath + " 次）");
                return false;
            }
            m_respawnWindowDeadSuppressed++;
            bool stale = window.IsStale(revision);
            if (stale)
            {
                m_respawnWindowStaleDrops++;
                if (isEvent) m_respawnStaleDeathDropped++;
                else m_respawnStaleDeadDropped++;
            }
            Problem("respawn-window-dead:" + key,
                "复活窗口拒绝" + kind + "（玩家键 " + key + " rev=" + revision +
                (string.IsNullOrEmpty(cause) ? string.Empty : "，死因=" + cause) + "）：" +
                window.Describe() +
                (stale
                    ? "；rev ≤ 复活 epoch（复活之前的旧死亡）"
                    : "；复活裁决未到 / 无法证明比裁决新 → 按旧死亡拒绝") +
                " → 不写引擎、绝不挂挡门框 / 死亡屏（累计拒绝 " +
                m_respawnWindowDeadSuppressed + " 条）");
            return true;
        }

        /// <summary>主机权威存活快照关闭窗口（清理死亡表现已由端口的 ApplyLocalVitals 完成）。</summary>
        private void CloseRespawnWindow(PlayerKey key, PlayerRespawnWindow window,
            uint revision)
        {
            window.Close();
            m_respawnWindows.Remove(key);
            m_respawnWindowClosed++;
            Info("玩家键 " + key + " 的复活窗口关闭（respawnInProgress 结束）：主机权威存活 rev=" +
                revision + " > 复活 epoch=" + window.Epoch + "（死亡 rev=" +
                window.DeathRevision + "）→ 已确认存活，死亡表现由端口清除");
        }

        private LocalRespawnState GetOrCreateLocalRespawn(PlayerKey key)
        {
            if (!m_localRespawns.TryGetValue(key, out LocalRespawnState state))
            {
                state = new LocalRespawnState { Key = key };
                m_localRespawns[key] = state;
            }
            return state;
        }

        /// <summary>
        /// 主机**批准**之后的唯一推进动作：关掉挡门对话框，并由 mod 执行引擎
        /// <c>PlayerData.PlayerDead</c> 点击分支里的那一行
        /// <c>m_project.RemoveEntity(ComponentPlayer.Entity, disposeEntity: true)</c>
        /// （<c>Survivalcraft/Game/PlayerData.cs:318</c>）。
        /// 之后的 PrepareSpawn → SpawnDialog → WaitForTerrain → SpawnPlayer → FppCamera
        /// 全是引擎原生流程（<c>PlayerData.cs:163-256 / 404-421</c>）。
        /// 失败**不静默**：计数 + 限流日志 + 保持死亡屏，下一帧重试。
        /// </summary>
        private void ExecuteLocalRespawn(PlayerKey key, long nowMs)
        {
            if (m_port == null) return;
            LocalRespawnState state = GetOrCreateLocalRespawn(key);
            if (state.AcceptedAtMs == 0) state.AcceptedAtMs = nowMs;
            if (state.Executed) return;
            if (!m_port.TryExecuteLocalRespawn(key, out string reason))
            {
                m_engineRespawnFailures++;
                Problem("respawn-execute:" + key,
                    "主机已批准复活，但执行引擎复活流程失败（玩家键 " + key + "）：" +
                    (reason ?? "端口没有给原因") +
                    " → 保持死亡屏并每帧重试（已失败 " + m_engineRespawnFailures + " 次）");
                return;
            }
            state.Executed = true;
            m_engineRespawnExecuted++;
            // 复活窗口（respawnInProgress）：从引擎 RemoveEntity 到第一份主机权威存活快照。
            // 窗口内任何 Dead 状态都可能是复活之前那次死亡的迟到包（实机 rev=3513 vs 复活 3514），
            // 一律拒绝，绝不再挂挡门框 / 死亡屏；存活快照到达后由端口主动清理死亡表现。
            PlayerRespawnWindow window = GetOrCreateRespawnWindow(key);
            window.Open(state.DeathRevision, state.RespawnRevision);
            m_respawnWindowOpened++;
            Info("玩家键 " + key + " 进入复活窗口（respawnInProgress）：死亡 rev=" +
                state.DeathRevision + " / 复活 rev=" +
                (state.RespawnRevision != 0
                    ? state.RespawnRevision.ToString()
                    : "0（复活裁决事件未到，先以死亡 revision 为下界）") +
                " / epoch=" + window.Epoch +
                " → 第一份权威存活快照之前拒绝任何死亡状态（旧死亡不得再挂死亡屏）");
            // 本次死亡期结束：不再接受点击（门控条目移除 = 死亡屏输入轮询停止）。
            m_respawnGate.OnRespawn(key);
            m_terminalDeathHandled.Remove(key);
            Info("主机批准玩家键 " + key + " 复活 → 已执行引擎 RemoveEntity，引擎开始原生" +
                " PrepareSpawn → SpawnDialog → WaitForTerrain → SpawnPlayer" +
                "（出生后由本模块按主机权威数据整体重放）");
        }

        /// <summary>
        /// 收到主机的 Respawn 事件（本机键）：记下落点 + 推进死亡 epoch。
        /// **不写任何组件状态**（旧实体已经 / 正在被删除，新实体还没出生）——
        /// 落点在 <see cref="TickLocalRespawnReplay"/> 里对新实体一次性应用。
        /// </summary>
        private void RecordLocalRespawnEvent(in PlayerVitalEventSnapshot evt)
        {
            // 复活已经执行 / 重放完成后，模块会把待重放状态移除；此时主机**迟到的**复活事件
            // 只应推进 epoch / 窗口，绝不能再创建一个永不执行的待重放状态
            //（实机日志里"复活事件比结果先到"就是在这种迟到的情形下被误打印的）。
            bool pending = m_localRespawns.ContainsKey(evt.Key) ||
                m_respawnGate.TryGet(evt.Key, out _);
            if (!pending)
            {
                m_localRespawnEpoch[evt.Key] = PlayerRespawnEpoch.Advance(LocalEpoch(evt.Key),
                    evt.Revision);
                if (m_respawnWindows.TryGetValue(evt.Key, out PlayerRespawnWindow lateWindow) &&
                    lateWindow.Active && lateWindow.NoteRespawnRevision(evt.Revision))
                {
                    Info("玩家键 " + evt.Key + " 迟到的复活裁决 rev=" + evt.Revision +
                        " → 窗口 epoch=" + lateWindow.Epoch +
                        "（复活已执行 / 重放完成，只推进 epoch）");
                }
                m_sleepGate.OnWake(evt.Key);
                m_respawnApplied++;
                Info("主机复活事件 rev=" + evt.Revision + "（玩家键 " + evt.Key +
                    "）属于已完成的复活期 → 只推进 epoch，不创建待重放状态" +
                    "（避免 respawnPending 残留 + 误报『事件比结果先到』）");
                return;
            }
            LocalRespawnState state = GetOrCreateLocalRespawn(evt.Key);
            state.RespawnRevision = PlayerRespawnEpoch.Advance(state.RespawnRevision, evt.Revision);
            if (evt.HasPosition)
            {
                state.HasPosition = true;
                state.Position = evt.Position;
            }
            m_localRespawnEpoch[evt.Key] = PlayerRespawnEpoch.Advance(LocalEpoch(evt.Key),
                state.RespawnRevision);
            if (m_respawnWindows.TryGetValue(evt.Key, out PlayerRespawnWindow window) &&
                window.Active && window.NoteRespawnRevision(evt.Revision))
            {
                Info("玩家键 " + evt.Key + " 的复活窗口收到主机复活裁决 rev=" + evt.Revision +
                    " → epoch=" + window.Epoch + "（死亡 rev=" + window.DeathRevision +
                    "）；窗口内死亡状态继续拒绝，等第一份权威存活快照");
            }
            m_sleepGate.OnWake(evt.Key);
            if (state.AcceptedAtMs == 0 && !state.Executed)
            {
                m_respawnEventBeforeResult++;
                Info("主机下发玩家键 " + evt.Key + " 的复活事件（rev=" + evt.Revision +
                    "，落点=" + (evt.HasPosition ? evt.Position.ToString() : "未提供") +
                    "）比复活结果先到 → 落点先记下，执行仍等主机结果（不猜）");
            }
        }

        /// <summary>
        /// 每帧：对"主机已批准、引擎已开始重建角色"的死亡期做出生后**权威重放**（第三个闸门）。
        ///
        /// 为什么必须重放：引擎 <c>SpawnPlayer</c> 建的是**全新实体** —— 模板血量、空背包、
        /// 空衣服（<c>PlayerData.cs:568-640</c>），落点还会被 <c>FindNoIntroSpawnPosition</c>
        /// 偏移最多 8 格（<c>PlayerData.cs:454-477</c>）。所以：等新实体出生 → 端口用主机权威
        /// 落点 / 模拟闸门 / HUD 覆盖 → 这里再用最近一条主机快照补写五项体征 → 清掉死亡期状态。
        /// 背包 / 手持 / 衣服 / 合成格**不恢复**：引擎死亡分支已把它们掉在死亡地点（单机行为），
        /// 复活后背包为空，由玩家自己捡回（否则会凭空多一份，见 TryReplayLocalRespawn）。
        /// 引擎还在 PrepareSpawn / WaitForTerrain 时端口返回 <c>Waiting</c>，
        /// 只计数 + 首次一条日志，不做任何写入。
        /// </summary>
        private void TickLocalRespawnReplay(long nowMs)
        {
            if (m_localRespawns.Count == 0) return;
            m_localRespawnKeys.Clear();
            foreach (PlayerKey key in m_localRespawns.Keys) m_localRespawnKeys.Add(key);
            for (int i = 0; i < m_localRespawnKeys.Count; i++)
            {
                PlayerKey key = m_localRespawnKeys[i];
                if (!m_localRespawns.TryGetValue(key, out LocalRespawnState state)) continue;
                // 主机批准了但上一帧执行失败（例如引擎不在 PlayerDead）：本帧再试一次。
                if (!state.Executed)
                {
                    if (state.AcceptedAtMs > 0) ExecuteLocalRespawn(key, nowMs);
                    continue;
                }
                if (state.ReplayApplied) continue;
                if (m_port == null) continue;
                // epoch 必须在重放 / ReapplyAuthoritativeVitals **之前**推进成同一个数：
                // 旧实现把推进放在 Reapply 之后，日志里 Reapply 打的是上一次复活周期的 epoch
                // （实机：死亡 rev=3513 却打印 "≤ epoch 479"），看起来像 uint 截断 / 另一套变量。
                uint epoch = PlayerRespawnEpoch.Advance(LocalEpoch(key),
                    PlayerRespawnEpoch.ForRespawn(state.DeathRevision, state.RespawnRevision));
                m_localRespawnEpoch[key] = epoch;
                PlayerRespawnReplayRequest request = new PlayerRespawnReplayRequest(
                    state.DeathRevision, state.RespawnRevision, state.HasPosition, state.Position,
                    epoch);
                PlayerRespawnReplayStatus status = m_port.TryReplayLocalRespawn(key, in request,
                    out string replayReason);
                if (status == PlayerRespawnReplayStatus.Waiting)
                {
                    if (state.WaitingSinceMs == 0) state.WaitingSinceMs = nowMs;
                    state.WaitFrames++;
                    m_respawnReplayWaitFrames++;
                    if (state.WaitFrames == 1)
                    {
                        Info("玩家键 " + key + " 的复活重放：等引擎新实体出生（PrepareSpawn / " +
                            "WaitForTerrain 中，落点=" +
                            (state.HasPosition ? state.Position.ToString() : "未提供") + "）");
                    }
                    continue;
                }
                if (status == PlayerRespawnReplayStatus.NoPending)
                {
                    // 端口已经对这个实体重放过（重复调用）：不重复写，只清状态。
                    state.ReplayApplied = true;
                    m_localRespawns.Remove(key);
                    Info("玩家键 " + key + " 的复活重放已经由端口完成过（重复调用返回 " +
                        "NoPending）→ 清掉死亡期状态");
                    continue;
                }
                if (status != PlayerRespawnReplayStatus.Applied)
                {
                    m_respawnReplayFailures++;
                    Problem("respawn-replay:" + key,
                        "出生后权威重放失败（玩家键 " + key + "）：" +
                        (replayReason ?? "端口没有给原因") +
                        " → 保留死亡期状态，下一帧重试（已失败 " + m_respawnReplayFailures +
                        " 次）");
                    continue;
                }
                state.ReplayApplied = true;
                m_respawnReplayApplied++;
                ReapplyAuthoritativeVitals(key);
                // 这次死亡周期到此结束：epoch 已在重放前推进为"主机复活 revision"
                //（ForRespawn；复活事件未到时是这次死亡的 revision 下界）。
                // 此后"复活之前产生"的 Dead 快照 / Death 事件一律丢弃
                //（否则会把刚出生的角色再写死一次，见 PlayerRespawnEpoch）。
                m_localRespawnEpoch[key] = PlayerRespawnEpoch.Advance(LocalEpoch(key), epoch);
                ClearLocalDeath(key, "复活重放完成");
                m_localRespawns.Remove(key);
                Info("玩家键 " + key + " 复活重放完成（落点=" +
                    (state.HasPosition ? state.Position.ToString() : "未提供") + "，等待 " +
                    state.WaitFrames + " 帧，死亡 rev=" + state.DeathRevision + " / 复活 rev=" +
                    (state.RespawnRevision != 0
                        ? state.RespawnRevision.ToString()
                        : "0（事件未到）") + " / 复活 epoch=" + LocalEpoch(key) + "）");
            }
        }

        /// <summary>
        /// 五项体征的整体重放：新实体出生后立刻把**最近一条主机权威快照**写进它。
        /// 主机在复活边沿会强制立即重发一条（<c>m_forceSend</c>），但那是下一拍的事 ——
        /// 不重放的话新角色会以模板血量站到那条上报到达为止（中间态，产品不允许）。
        ///
        /// 快照本身是"死亡状态"（<c>Dead = true</c>）或 revision 不新于死亡 epoch 时**不重放**：
        /// 那会把刚出生的角色立刻再写死一次（引擎 <c>PlayerData.Playing</c> 只看 Health ≤ 0）。
        /// </summary>
        private void ReapplyAuthoritativeVitals(PlayerKey key)
        {
            if (m_port == null) return;
            if (!m_clientCharacters.TryGetValue(key, out ClientKeyState state) || !state.HasApplied)
            {
                Info("玩家键 " + key + " 复活重放：还没有可用的主机权威快照 → 等主机下一拍上报" +
                    "（端口已完成落点 / 模拟闸门；背包由引擎死亡掉落，不在重放中恢复）");
                return;
            }
            PlayerVitalsSnapshot snapshot = state.LastApplied;
            if (snapshot.Dead || PlayerRespawnEpoch.IsStaleDead(LocalEpoch(key), snapshot.Revision))
            {
                Info("玩家键 " + key + " 复活重放：最近一条快照是复活之前的死亡状态（rev=" +
                    snapshot.Revision + " ≤ epoch " + LocalEpoch(key) +
                    "）→ 不写进新实体，等主机下一拍复活后的权威上报");
                state.HasApplied = false;
                state.LastRevision = 0;
                return;
            }
            PlayerVitalsApplyStatus status = m_port.ApplyLocalVitals(key, in snapshot,
                out string reason);
            if (status == PlayerVitalsApplyStatus.Applied)
            {
                m_clientVitalsApplied++;
                return;
            }
            m_applyFailures++;
            Problem("respawn-replay-vitals:" + key,
                "复活重放时应用主机权威体征失败（玩家键 " + key + "）：" +
                (reason ?? ("端口返回 " + status)) + " → 等主机下一拍上报补齐");
        }

        /// <summary>
        /// 离开世界 / 断线 / 下线：清空本机死亡 / 复活状态表（不留待重放项，也不留死亡
        /// epoch —— 重连后主机 revision 从头开始，旧 epoch 会误丢新状态）。
        /// </summary>
        private void ResetLocalRespawns(string why)
        {
            if (m_localRespawns.Count == 0 && m_localRespawnEpoch.Count == 0 &&
                m_localRespawnKeys.Count == 0 && m_respawnWindows.Count == 0)
                return;
            Info("清空本机死亡 / 复活状态表（" + why + "）：待重放 " + m_localRespawns.Count +
                " 项，死亡 epoch " + m_localRespawnEpoch.Count + " 项，复活窗口 " +
                m_respawnWindows.Count + " 项");
            m_localRespawns.Clear();
            m_localRespawnEpoch.Clear();
            m_localRespawnKeys.Clear();
            m_respawnWindows.Clear();
        }

        /// <summary>
        /// 每帧：死亡屏上的**按下沿** → 1.5 秒门槛 → 每次死亡最多一个请求。
        /// 门槛前 / 已发过 / 模式禁止 / 已解决都明确计数（<c>respawnSuppressed</c>）。
        ///
        /// 点击口径 = **按下沿**，不是 <c>Input.Any</c> 电平（根因）：<c>WidgetInput.Any</c> 在
        /// **键盘自动重复**时每帧都会重新为真（<c>WidgetInput.cs:704</c> 读 <c>Keyboard.LastKey</c>，
        /// 而 <c>Keyboard.cs:170</c> 在重复事件里也无条件置 <c>m_lastKey</c>），所以"死亡瞬间正按着
        /// 方向键 / 挖键"会让门槛一过就自动发一次复活请求（实测故障：刚死就瞬间复活，玩家没点）。
        /// 这里两道闸：
        ///   · 端口 <c>TryPollLocalDeathTap</c> 只报**新的按下**（<c>PlayerDeathInputSample.IsFreshPress</c>）；
        ///   · 门控 <see cref="PlayerRespawnGate.NoteInput"/> 再做一层"必须先松手"的按下沿判定，
        ///     同一个按下的后续帧记 <c>respawnSamePress</c>，绝不当第二次点击。
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
                    // 结果包丢了，但主机 Respawn 事件已经到了：主机**确实**裁决了复活。
                    // 按已确认的主机状态推进引擎流程（显式计数 + 日志，不是兜底猜测）。
                    if (m_localRespawns.TryGetValue(key, out LocalRespawnState timedOut) &&
                        timedOut.RespawnRevision > 0 && !timedOut.Executed)
                    {
                        m_respawnExecutedWithoutResult++;
                        Info("玩家键 " + key + " 的复活请求超时，但主机 Respawn 事件已到达" +
                            "（rev=" + timedOut.RespawnRevision + "）→ 按主机已裁决的状态执行" +
                            "引擎复活流程");
                        ExecuteLocalRespawn(key, nowMs);
                    }
                }
                if (!m_port.TryPollLocalDeathTap(key, out bool pressed, out string pollReason))
                {
                    Problem("respawn-tap-poll:" + key, "读取死亡屏输入失败：" +
                        (pollReason ?? "端口没有给原因") + " → 本帧不判定点击（不猜输入）");
                    continue;
                }
                // 按下沿判定必须在 OnTap 之前：先记"这一帧有没有输入"，只有"上一帧没有输入、
                // 这一帧出现输入"才是新点击。按住 / 键盘自动重复的持续为真到此为止。
                // NoteInput 返回 true = 出现了一次边沿；**松手沿**同样返回 true（"重新允许
                // 下一次按下"），所以必须先看 pressed —— 松手帧只更新门控状态，绝不是点击。
                bool edge = m_respawnGate.NoteInput(key, pressed);
                if (!pressed) continue;
                if (!edge)
                {
                    m_respawnSamePress++;
                    Problem("respawn-same-press:" + key,
                        "玩家键 " + key + " 的死亡屏输入仍是**同一个按下**（没有松手 / " +
                        "键盘自动重复）→ 不当作点击、不发复活请求（累计 " +
                        m_respawnSamePress + " 帧）；必须松手后再按下");
                    continue;
                }
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
                        // Cruel：引擎自己的 PlayerDead 点击分支就是"打开 GameMenuDialog"
                        //（PlayerData.cs:308-311），挡门对话框也不挂 —— mod 不重复打开菜单、不发请求。
                        if (pending.Mode == PlayerRespawnMode.Cruel)
                        {
                            Info("玩家键 " + key + " 在 Cruel 模式点击死亡屏：交给引擎原生分支" +
                                "（GameMenuDialog，PlayerData.cs:308-311），mod 不重复处理、不发请求");
                            break;
                        }
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
                "（每次死亡最多一次；等待主机 PLAYER_RESPAWN_RESULT）");
            // 只做表现：死亡屏上加一行"已请求复活"，不改变任何请求状态。
            if (!m_port.TryShowLocalRespawnPending(key, out string pendingReason))
            {
                Problem("respawn-pending-ui:" + key,
                    "显示\"已请求复活\"提示失败（玩家键 " + key + "）：" +
                    (pendingReason ?? "端口没有给原因") + " → 请求本身已发送，不影响裁决");
            }
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
            if (m_sleepPort == null) return;
            TickClientSleepEntry(nowMs);
            if (m_sleepGate.Count == 0) return;
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

        // ---------------------------------------------------------------- 客户端：入睡申请（104）

        /// <summary>
        /// 客户端每帧：睡眠按钮的**按下沿** → 发 <c>PLAYER_SLEEP_REQUEST</c>（每次按下最多一条）。
        ///
        /// 为什么客户端不能自己睡：引擎唯一的入睡入口是 <c>ClothingWidget.Update</c>
        /// （Source: Survivalcraft/Game/ClothingWidget.cs:78-90），它跑在**持有控件的那一端**。
        /// 客户端就地 <c>Sleep(true)</c> 只改了客户端自己的 <c>ComponentSleep</c>；主机那边的角色
        /// 根本没睡，下一份权威上报（sleeping=false）会通过 <c>ApplyLocalSleep</c> 把
        /// <c>m_sleepStartTime</c> 写回 null —— 表现就是"躺下 → 瞬间站起"。
        /// 所以入睡也必须走"客户端申请 / 主机裁决"，与唤醒（103）对称。
        ///
        /// 按下沿纪律（防止"进入睡眠的那次点击被当成第二次点击"）：
        ///   · 端口每帧把 <c>GameWidget.Input.Any</c> 的**释放**记进门控
        ///     （<see cref="PlayerSleepRequestGate.NoteInput"/>）；
        ///   · 只有"已释放过之后的一次按下"才算新请求；
        ///   · 发出请求后本帧清输入（与引擎 <c>WakeUp()</c> 前同一手法，避免同一次点击又触发别的操作）。
        /// </summary>
        private void TickClientSleepEntry(long nowMs)
        {
            if (!TryRegisterLocalSleepKeys(out string registerReason))
            {
                Problem("sleep-entry-keys", "登记本机睡眠槽位失败：" +
                    (registerReason ?? "端口没有给原因") + " → 本帧不判定入睡申请");
                return;
            }
            if (m_sleepRequestGate.Count == 0) return;
            m_sleepRequestGate.CopyKeys(m_sleepKeys);
            for (int i = 0; i < m_sleepKeys.Count; i++)
            {
                PlayerKey key = m_sleepKeys[i];
                if (!m_sleepPort.TryPollLocalSleepRequest(key,
                        out PlayerSleepRequestViewState state, out bool requested,
                        out string pollReason))
                {
                    Problem("sleep-entry-poll:" + key, "读取本机入睡状态失败：" +
                        (pollReason ?? "端口没有给原因") + " → 本帧不判定入睡申请");
                    continue;
                }
                // 先记录"本帧有没有输入"：没有输入 = 这一次按下已经松手，下一次按下才是新请求。
                m_sleepRequestGate.NoteInput(key, requested);
                if (!requested) continue;
                m_sleepEntryTapCaptured++;
                long sequence = m_sleepEntrySequence + 1;
                PlayerSleepRequestAction action = m_sleepRequestGate.OnTap(key, in state,
                    sequence, out string tapReason);
                switch (action)
                {
                    case PlayerSleepRequestAction.SendRequest:
                        m_sleepEntrySequence = sequence;
                        if (SendSleepRequest(key, sequence))
                        {
                            m_sleepEntryPendingSequence = sequence;
                            m_sleepEntryPendingAtMs = nowMs;
                            if (!m_sleepPort.TryConsumeLocalSleepRequestInput(key,
                                    out string consumeReason))
                            {
                                Problem("sleep-entry-input-clear:" + key, "清入睡输入失败：" +
                                    (consumeReason ?? "端口没有给原因"));
                            }
                        }
                        else
                        {
                            m_sleepRequestGate.OnSendFailed(key);
                        }
                        break;
                    case PlayerSleepRequestAction.SuppressedSleeping:
                    case PlayerSleepRequestAction.SuppressedDead:
                    case PlayerSleepRequestAction.SuppressedSamePress:
                    case PlayerSleepRequestAction.SuppressedAlreadyRequested:
                    case PlayerSleepRequestAction.SuppressedInvalidSequence:
                        Bump(m_sleepEntrySuppressedReasons, tapReason);
                        m_sleepEntrySuppressed++;
                        break;
                }
            }
            // 未决超时：主机既没接受也没拒绝（丢包 / 通道抖动）。明确记一行并释放门控，
            // 否则一次丢包会让睡眠按钮永远点不动（不把超时当成成功）。
            if (m_sleepEntryPendingSequence > 0 &&
                nowMs - m_sleepEntryPendingAtMs >= SleepEntryPendingTimeoutMs)
            {
                m_sleepEntryTimeouts++;
                Problem("sleep-entry-timeout", "入睡请求 seq=" + m_sleepEntryPendingSequence +
                    " 超过 " + SleepEntryPendingTimeoutMs + " ms 没有任何裁决（既没收到 " +
                    "PLAYER_SLEEP_RESULT，权威上报也没变成 sleeping=true）→ 释放门控，" +
                    "下一次点击可重试");
                ReleaseSleepEntryRequests();
            }
        }

        /// <summary>
        /// 登记本机槽位（端口只按槽位解析）。端口没有"枚举本机槽位"的读法，所以按引擎上限
        /// （<see cref="PlayerKey.SlotCount"/>）逐个探测：读得到状态的就是本机槽位。
        /// 一个都没有 = 明确失败（不假装已登记）。
        ///
        /// **键必须是 <c>(LocalClientId, slot)</c>，不能是 <c>(0, slot)</c>**：
        /// <c>PLAYER_SLEEP_RESULT</c> 的 <c>client_id</c> 是本端传输编号，解出的
        /// <see cref="PlayerSleepResultData.Key"/> 是 <c>(clientId, slot)</c>；权威上报的键同样是
        /// <c>(clientId, slot)</c>。门控若登记成主机键 <c>(0, slot)</c>，
        /// <see cref="PlayerSleepRequestGate.OnRejected"/> /
        /// <see cref="PlayerSleepRequestGate.OnSleepConfirmed"/> 会作用在**另一个**字典条目上而
        /// 静默不生效（实测故障：被拒后下一次点击仍被判 same-press、按钮点不动）。
        /// </summary>
        private bool TryRegisterLocalSleepKeys(out string reason)
        {
            reason = null;
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (localClientId <= 0)
            {
                // 键必须带上本端真实 clientId（见方法注释）。0 是主机键；拿它登记客户端
                // 门控条目会让主机的回执 / 权威快照落在另一个字典条目上（静默不生效）。
                reason = "本端传输编号=" + localClientId + " 非法（客户端入睡键的 clientId 必须 > 0）";
                return false;
            }
            for (int slot = 0; slot < PlayerKey.SlotCount; slot++)
            {
                var key = new PlayerKey(localClientId, slot);
                if (m_sleepRequestGate.Contains(key)) continue;
                // 用**无副作用**的探测做发现：带边沿的读法会把玩家的第一次点击吃掉。
                if (!m_sleepPort.TryDescribeLocalSleep(key, out _)) continue;
                // **不能**在这里 NoteInput(..., false)：那会把"按下沿已释放"每帧重新置位，
                // 于是按住不放的按钮会被当成"每帧一次新按下"（重复发请求）。
                // 登记本身不碰按下沿；门控条目初始就是"已释放"（Entry.PressReleased = true）。
                m_sleepRequestGate.Register(key);
            }
            if (m_sleepRequestGate.Count == 0)
            {
                reason = "本机没有任何可用的睡眠槽位（角色还没建好 / 不在世界）";
                return false;
            }
            return true;
        }

        private bool SendSleepRequest(PlayerKey key, long sequence)
        {
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (localClientId <= 0 || m_messages == null)
            {
                m_sleepEntrySendFailures++;
                Problem("sleep-entry-nochannel", "无法发送入睡请求（玩家键 " + key + "）：" +
                    (m_messages == null ? "没有消息层" : "本端传输编号=" + localClientId) +
                    " → 已释放本次请求（下一次点击可重试）");
                return false;
            }
            var data = new PlayerSleepRequestData(localClientId, key.Slot, sequence,
                allowManualWake: true);
            V.PlayerSleepRequest message;
            try
            {
                message = PlayerSleepRequestCodec.Encode(in data);
            }
            catch (Exception e)
            {
                m_sleepEntrySendFailures++;
                Problem("sleep-entry-encode", "入睡请求编码失败（" + data.Describe() + "）：" +
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
                Problem("sleep-entry-exception", "入睡请求发送异常（" + data.Describe() + "）：" +
                    e.GetType().Name + " " + e.Message);
            }
            if (!sent)
            {
                m_sleepEntrySendFailures++;
                Problem("sleep-entry-send", "入睡请求发送失败（" + data.Describe() +
                    "）→ 已释放本次请求（下一次点击可重试；通道恢复前不会假装睡着）");
                return false;
            }
            m_sleepEntrySent++;
            Info("睡眠按钮 → 已发送入睡请求 " + data.Describe() +
                "（主机用引擎 CanSleep 复检后 Sleep(true)；客户端不本地入睡）");
            return true;
        }

        /// <summary>释放所有未决的入睡请求（发送失败 / 超时 / 主机拒绝）。</summary>
        private void ReleaseSleepEntryRequests()
        {
            m_sleepEntryPendingSequence = 0;
            m_sleepEntryPendingAtMs = 0;
            m_sleepRequestGate.CopyKeys(m_sleepKeys);
            for (int i = 0; i < m_sleepKeys.Count; i++)
                m_sleepRequestGate.OnTimeout(m_sleepKeys[i]);
        }

        /// <summary>
        /// 主机：客户端请求入睡。只接受**该角色的 owner**；用**主机自己的**引擎
        /// <c>CanSleep()</c> + <c>Sleep(true)</c> 裁决（干地 / 舒适度 / 太困 / 太湿 / 掩体，
        /// Source: Survivalcraft/Game/ComponentSleep.cs:41-94），接受后立刻下发权威状态
        /// （sleeping=true + 主机 <c>sleep_start_time</c>），客户端据此躺下。
        /// </summary>
        private void OnSleepRequestMessage(MessageContext context, V.PlayerSleepRequest message)
        {
            if (!m_attached) return;
            m_sleepEntryRecv++;
            if (m_control == null || !m_control.IsHosting)
            {
                m_sleepEntryWrongDirection++;
                Problem("sleep-entry-client", "客户端收到了 PLAYER_SLEEP_REQUEST（方向错误，" +
                    "来源 clientId=" + context.ClientId + "）→ 忽略");
                return;
            }
            if (!PlayerSleepRequestCodec.TryDecode(message, out PlayerSleepRequestData request,
                    out string decodeReason))
            {
                m_sleepEntryMalformed++;
                Problem("sleep-entry-malformed", "入睡请求畸形（" + decodeReason +
                    "，来源 clientId=" + context.ClientId + "）→ 整条拒绝");
                return;
            }
            if (request.ClientId != context.ClientId)
            {
                RejectSleepRequest(context.ClientId, in request, PlayerSleepReasons.WrongOwner,
                    "请求里的 client_id=" + request.ClientId + " 与连接 " + context.ClientId +
                    " 不一致");
                return;
            }
            if (context.ClientId <= 0 || !m_clients.Contains(context.ClientId))
            {
                RejectSleepRequest(context.ClientId, in request,
                    PlayerSleepReasons.UnknownPlayer,
                    "连接没有登记为在线玩家端（clientId=" + context.ClientId + "）");
                return;
            }
            if (m_sleepPort == null)
            {
                RejectSleepRequest(context.ClientId, in request,
                    PlayerSleepReasons.SleepFailed, "睡眠端口未接（组合根没有注册）");
                return;
            }
            // 已处理的序号：同一次按下重复到达（重传 / 双击）不重复执行引擎 Sleep()。
            string handledKey = context.ClientId + ":" + request.Slot;
            if (m_sleepEntryHandled.TryGetValue(request.RequestSequence, out bool duplicate) &&
                duplicate)
            {
                RejectSleepRequest(context.ClientId, in request, PlayerSleepReasons.AlreadySleeping,
                    "序号 seq=" + request.RequestSequence + " 已经处理过（重复投递，不重复 Sleep()）");
                return;
            }
            m_sleepEntryHandled[request.RequestSequence] = true;
            if (m_sleepEntryHandled.Count > 64) m_sleepEntryHandled.Clear();
            if (!m_sleepPort.TrySleepAuthorityPlayer(request.Key, request.AllowManualWake,
                    out string rejectCode, out string detail, out string sleepReason))
            {
                RejectSleepRequest(context.ClientId, in request,
                    rejectCode ?? PlayerSleepReasons.SleepFailed,
                    sleepReason ?? "端口没有给原因", detail);
                return;
            }
            HostCharacterState state = GetOrCreateHostCharacter(request.Key);
            // 睡眠状态变了：下一次抓取必须递增 revision 并立刻发出。
            state.HasSent = false;
            state.WasForcedSleep = false;
            m_forceSend = true;
            m_sleepEntryAccepted++;
            SendSleepResult(context.ClientId, in request, true, PlayerSleepReasons.Accepted,
                string.Empty);
            Info("主机接受入睡请求（" + request.Describe() + " · 玩家键 " + request.Key +
                "）：已调用引擎 ComponentSleep.Sleep(" + request.AllowManualWake +
                ") → 立刻下发 sleeping=true（客户端躺下）" +
                (string.IsNullOrEmpty(handledKey) ? "" : "") );
        }

        private void RejectSleepRequest(int clientId, in PlayerSleepRequestData request,
            string reasonCode, string detail, string hudText = null)
        {
            m_sleepEntryRejected++;
            Bump(m_sleepEntryRejectReasons, PlayerSleepReasons.Key(reasonCode));
            Problem("sleep-entry-reject:" + PlayerSleepReasons.Key(reasonCode),
                "拒绝入睡请求（clientId=" + clientId + " 玩家键=" + request.Key + " seq=" +
                request.RequestSequence + "）：" + reasonCode + " —— " + detail);
            SendSleepResult(clientId, in request, false, reasonCode,
                hudText ?? detail);
        }

        private void SendSleepResult(int clientId, in PlayerSleepRequestData request,
            bool accepted, string reasonCode, string detail)
        {
            if (m_transport == null || m_messages == null)
            {
                m_sleepEntryResultSendFailures++;
                Problem("sleep-entry-result-channel", "无法发送入睡结果（clientId=" + clientId +
                    " 玩家键=" + request.Key + "）：没有传输 / 消息层 → 客户端会在 " +
                    SleepEntryPendingTimeoutMs + " ms 后超时（不假装已回执）");
                return;
            }
            byte[] payload;
            try
            {
                var data = new PlayerSleepResultData(clientId, request.Slot,
                    request.RequestSequence, accepted, reasonCode, detail);
                payload = PlayerSleepRequestCodec.EncodeResultBytes(in data);
            }
            catch (Exception e)
            {
                m_sleepEntryResultSendFailures++;
                Problem("sleep-entry-result-encode", "入睡结果编码失败（clientId=" + clientId +
                    " 玩家键=" + request.Key + "）：" + e.GetType().Name + " " + e.Message);
                return;
            }
            // 与模块里其它主机 → 玩家端的定向消息同一条路径（ITransportPort.TrySendTo）：
            // 主机必须指名目标，不能走"猜目标"的发送表面。
            if (!m_transport.TrySendTo(clientId, MessageType.PlayerSleepResult, payload))
            {
                m_sleepEntryResultSendFailures++;
                Problem("sleep-entry-result-send", "入睡结果发送失败（clientId=" + clientId +
                    " 玩家键=" + request.Key + " seq=" + request.RequestSequence +
                    "）→ 客户端会在 " + SleepEntryPendingTimeoutMs + " ms 后超时");
            }
        }

        private void OnSleepResultMessage(MessageContext context, V.PlayerSleepResult message)
        {
            if (!m_attached) return;
            m_sleepEntryResultRecv++;
            if (m_control != null && m_control.IsHosting)
            {
                m_sleepEntryWrongDirection++;
                Problem("sleep-entry-result-host", "主机收到了 PLAYER_SLEEP_RESULT（方向错误，" +
                    "来源 clientId=" + context.ClientId + "）→ 忽略");
                return;
            }
            if (context.ClientId != 0)
            {
                m_sleepEntryWrongDirection++;
                Problem("sleep-entry-result-sender", "客户端收到来自 clientId=" +
                    context.ClientId + " 的入睡结果（只有主机能回）→ 忽略");
                return;
            }
            if (!m_worldReady)
            {
                m_notReadyDrops++;
                return;
            }
            if (!PlayerSleepRequestCodec.TryDecodeResult(message,
                    out PlayerSleepResultData result, out string decodeReason))
            {
                m_sleepEntryMalformed++;
                Problem("sleep-entry-result-malformed", "主机下发的入睡结果畸形（" +
                    decodeReason + "）→ 整条丢弃");
                return;
            }
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (result.ClientId != localClientId)
            {
                Problem("sleep-entry-result-owner", "入睡结果不属于本端（result clientId=" +
                    result.ClientId + "，本端=" + localClientId + "）→ 忽略");
                return;
            }
            if (m_sleepEntryPendingSequence != 0 &&
                result.RequestSequence != m_sleepEntryPendingSequence)
            {
                m_sleepEntryResultStale++;
                Problem("sleep-entry-result-stale", "丢弃陈旧的入睡结果（玩家键 " +
                    result.Key + " seq=" + result.RequestSequence + "，当前未决 seq=" +
                    m_sleepEntryPendingSequence + "）");
                return;
            }
            m_sleepEntryPendingSequence = 0;
            m_sleepEntryPendingAtMs = 0;
            if (result.Accepted)
            {
                m_sleepRequestGate.OnSleepConfirmed(result.Key);
                Info("主机接受入睡请求（玩家键 " + result.Key + " seq=" +
                    result.RequestSequence + "）→ 等待权威上报 sleeping=true 驱动躺下");
                return;
            }
            m_sleepRequestGate.OnRejected(result.Key);
            Bump(m_sleepEntryObservedRejectReasons, PlayerSleepReasons.Key(result.Reason));
            Problem("sleep-entry-rejected", "主机拒绝入睡请求（玩家键 " + result.Key + " seq=" +
                result.RequestSequence + "）：" + result.Reason +
                (string.IsNullOrEmpty(result.Detail) ? "" : "（" + result.Detail + "）"));
            if (!m_sleepPort.TryShowLocalSleepRejected(result.Key, result.Detail,
                    out string showReason))
            {
                Problem("sleep-entry-rejected-ui", "显示入睡被拒原因失败：" +
                    (showReason ?? "端口没有给原因"));
            }
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
                LocalRespawnState accepted = GetOrCreateLocalRespawn(result.Key);
                accepted.AcceptedAtMs = NowMs();
                if (result.HasPosition)
                {
                    accepted.HasPosition = true;
                    accepted.Position = result.Position;
                }
                Info("主机接受复活请求（玩家键 " + result.Key + " seq=" +
                    result.RequestSequence + " · 落点=" +
                    (result.HasPosition ? result.Position.ToString() : "未提供") +
                    "）→ 由 mod 执行引擎 RemoveEntity，出生后按主机权威数据整体重放");
                // 唯一的执行点：主机批准之后（引擎自己要等玩家点击才删实体）。
                ExecuteLocalRespawn(result.Key, NowMs());
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
                // cooldown（客户端比主机的 1.5 s 门槛点得早）是**可恢复**的：重新武装门控，
                // RetryAfterRejectMs 之后允许再点一次（显式计数 + 日志）。
                // 其它拒绝原因都是终局，保持"本次死亡已解决"，不重发。
                if (m_respawnGate.OnRejected(result.Key, NowMs(), out long retryAtMs,
                        out string retryReason))
                {
                    m_respawnRetryArmed++;
                    Info("玩家键 " + result.Key + " 的复活请求被主机以 cooldown 拒绝 → 已重新" +
                        "允许一次点击（" + PlayerRespawnRules.RetryAfterRejectMs +
                        " ms 后，最早 " + retryAtMs + " ms）：" + retryReason);
                }
                else if (!string.IsNullOrEmpty(retryReason))
                {
                    Info("玩家键 " + result.Key + " 的复活请求被拒绝且不重试：" + retryReason);
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
            m_pendingEffects.Clear();
            m_effectBatch.Clear();
            m_senderEffectSequences.Clear();
            m_effectSequence = 0;
            ResetLocalRespawns("关闭主持的房间");
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepRequestGate.Clear();
            m_sleepEntryHandled.Clear();
            m_sleepEntryPendingSequence = 0;
            m_sleepEntryPendingAtMs = 0;
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_observerSendCounts.Clear();
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
            m_sleepRequestGate.Clear();
            m_sleepEntryHandled.Clear();
            m_sleepEntryPendingSequence = 0;
            m_sleepEntryPendingAtMs = 0;
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_observerSendCounts.Clear();
            m_forceSend = false;
            Info("房间已关闭：主机权威状态表已清空" + (connected ? "" : "（通道未连接）"));
        }

        private void OnConnectionEstablished(bool hosting)
        {
            m_clientCharacters.Clear();
            m_hostCharacters.Clear();
            m_pendingEvents.Clear();
            ResetLocalRespawns("通道重新建立");
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepRequestGate.Clear();
            m_sleepEntryHandled.Clear();
            m_sleepEntryPendingSequence = 0;
            m_sleepEntryPendingAtMs = 0;
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_observerSendCounts.Clear();
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
            ResetLocalRespawns("与主机断开");
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepRequestGate.Clear();
            m_sleepEntryHandled.Clear();
            m_sleepEntryPendingSequence = 0;
            m_sleepEntryPendingAtMs = 0;
            m_sleepKeys.Clear();
            m_terminalDeathHandled.Clear();
            m_senderSequences.Clear();
            m_senderEventSequences.Clear();
            m_observerSendCounts.Clear();
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
            ResetLocalRespawns("进入新世界");
            m_respawnGate.Clear();
            m_sleepGate.Clear();
            m_sleepRequestGate.Clear();
            m_sleepEntryHandled.Clear();
            m_sleepEntryPendingSequence = 0;
            m_sleepEntryPendingAtMs = 0;
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
            int dropped = m_pendingEffects.RemoveAll(evt => evt.Key.ClientId == clientId);
            if (dropped > 0)
            {
                m_fluEffectNoRecipients += dropped;
                Problem("effects-client-left", "玩家端 clientId=" + clientId + " 离开 → 丢弃 " +
                    dropped + " 条发给它的待发表情事件（不再广播给别的端）");
            }
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
            // 该端的距离分级节拍不能跨会话复用（clientId 会被重新分配）；同时清掉别人
            // 为它攒的节拍键（它的化身已经移除，不会再出现在上报里）。
            m_observerSendCounts.Remove(clientId);
            DropObservedSendCounts(clientId);
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
        ///   → ownerEntriesSent = 成功入队的 owner 条目数（每端自己的权威状态，每拍必到）；
        ///   → observerFilteredByWindow = 该端窗口不含该角色 → 未下发的观察者条目数；
        ///   → observerThrottled        = 窗口内但本拍被距离分级限流（不发）的观察者条目数；
        ///   → observerWindowUnknown    = 该端还没上报窗口 → 观察者条目全部不发（owner 照发）的条目数；
        ///   → observerOwnerPositionUnknown = 该端没有 owner 条目（距离未知）→ 按最近一档下发的条目数；
        ///   → observerUrgent              = 死亡 / 死因 / 强制睡眠边沿绕过限流而发出的观察者条目数；
        ///   → perClientEncodeFailures / perClientEmptyReports =
        ///     逐端编码失败 / 过滤后为空而跳过的端次数；
        ///   → DeathEvents / RespawnEvents = 主机裁决并排队的边沿数；
        ///   → respawnReqRecv / Accepted / Rejected(reason) = 主机收到的复活请求 / 接受 / 按原因拒绝；
        ///   → autoRespawnRemoved = 1（旧的 2.5 秒自动复活路径已删除）。
        ///   → clientReportRejected        = 拒绝的客户端上报 / 事件消息数。
        /// 客户端：
        ///   → vitalsRecv / vitalsApplied  = 收到 / 写进本机玩家或远端化身的条目数；
        ///   → vitalsStale / vitalsMalformed = 陈旧 / 畸形的条目（或消息）数；
        ///   → DeathApplied / RespawnApplied = 应用的死亡 / 复活边沿数；
        ///   → deathScreenShown / respawnTapCaptured = 登记的死亡（引擎原生死亡画面）/ 捕获的点击次数；
        ///   → respawnReqSent / ResultRecv / Accepted / Rejected(reason) = 请求与结果；
        ///   → respawnSuppressed{reason}   = 1.5 秒门槛前 / 已发过 / 模式禁止 / 已解决的点击；
        ///   → respawnRetryArmed           = 被主机以 cooldown 拒绝后重新允许一次点击的次数；
        ///   → engineRespawnExec / Fail    = mod 执行引擎 RemoveEntity 的成功 / 失败次数
        ///                                   （失败 = 引擎不在 PlayerDead 或没有 ComponentPlayer，每帧重试）；
        ///   → respawnReplayApplied / Fail = 新实体出生后权威重放的成功 / 失败次数；
        ///   → respawnReplayWaitFrames     = 等引擎新实体出生的帧数（PrepareSpawn / WaitForTerrain）；
        ///   → respawnStaleDead / StaleDeath = 被死亡 epoch 丢弃的陈旧 Dead 快照 / Death 事件数；
        ///   → respawnWindowOpened            = 进入复活窗口（respawnInProgress）的次数
        ///                                      （= 主机批准后成功执行 RemoveEntity）；
        ///   → respawnWindowDeadSuppressed    = 窗口内被拒绝、绝不写进引擎的 Dead 快照 / Death 事件数；
        ///   → respawnWindowClosed            = 被主机权威存活快照关闭的窗口数（死亡表现清理成功）；
        ///   → respawnWindowStaleDrops        = 复活之后到达的旧死亡包（rev ≤ 复活 epoch）数
        ///                                      （窗口内外都算，与 respawnStale* 同源）；
        ///   → respawnWindowCleanupFailures   = 关闭窗口时清理死亡表现失败的次数（端口 Rejected；
        ///                                      窗口保持，下一拍重试，绝不假装清掉）；
        ///   → respawnWindowNewDeath          = 窗口内收到比复活裁决更新的死亡（真实新死亡）而结束
        ///                                      窗口的次数（按真实死亡应用；旧死亡仍全部拒绝）；
        ///   → respawnWindowActive            = 当前仍在 respawnInProgress 的玩家键数；
        ///   → respawnEventBeforeResult    = Respawn 事件先于结果到达的次数（落点照收，执行仍等结果）；
        ///   → respawnExecNoResult         = 结果包丢了但 Respawn 事件已到 → 仍然执行的次数；
        ///   → respawnPending / respawnEpochs = 当前待重放的死亡期数 / 死亡 epoch 表大小；
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
                  " ownerEntriesSent=" + m_ownerEntriesSent +
                  " observerFilteredByWindow=" + m_observerFilteredByWindow +
                  " observerThrottled=" + m_observerThrottled +
                  " observerWindowUnknown=" + m_observerWindowUnknown +
                  " observerOwnerPositionUnknown=" + m_observerOwnerPositionUnknown +
                  " observerUrgent=" + m_observerUrgent +
                  " perClientEncodeFailures=" + m_perClientEncodeFailures +
                  " perClientEmptyReports=" + m_perClientEmptyReports +
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
                  " respawnSamePress=" + m_respawnSamePress +
                  " respawnReqSent=" + m_respawnReqSent +
                  " respawnReqSendFail=" + m_respawnReqSendFailures +
                  " respawnReqTimeout=" + m_respawnReqTimeouts +
                  " respawnResultRecv=" + m_respawnResultRecv +
                  " respawnAccepted=" + m_respawnAccepted +
                  " respawnRejected=" + m_respawnRejected +
                  " respawnRejectedBy=" + FormatReasons(m_respawnRejectReasons) +
                  " respawnSuppressed=" + m_respawnSuppressed +
                  " respawnSuppressedBy=" + FormatReasons(m_respawnSuppressedReasons) +
                  " respawnRetryArmed=" + m_respawnRetryArmed +
                  " engineRespawnExec=" + m_engineRespawnExecuted +
                  " engineRespawnFail=" + m_engineRespawnFailures +
                  " respawnReplayApplied=" + m_respawnReplayApplied +
                  " respawnReplayFail=" + m_respawnReplayFailures +
                  " respawnReplayWaitFrames=" + m_respawnReplayWaitFrames +
                  " respawnStaleDead=" + m_respawnStaleDeadDropped +
                  " respawnStaleDeath=" + m_respawnStaleDeathDropped +
                  " respawnWindowOpened=" + m_respawnWindowOpened +
                  " respawnWindowDeadSuppressed=" + m_respawnWindowDeadSuppressed +
                  " respawnWindowClosed=" + m_respawnWindowClosed +
                  " respawnWindowStaleDrops=" + m_respawnWindowStaleDrops +
                  " respawnWindowCleanupFailures=" + m_respawnWindowCleanupFailures +
                  " respawnWindowNewDeath=" + m_respawnWindowNewDeath +
                  " respawnWindowActive=" + m_respawnWindows.Count +
                  " respawnEventBeforeResult=" + m_respawnEventBeforeResult +
                  " respawnExecNoResult=" + m_respawnExecutedWithoutResult +
                  " respawnPending=" + m_localRespawns.Count +
                  " respawnEpochs=" + m_localRespawnEpoch.Count +
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
            return "player-vitals[" + (hosting ? "host" : "client") + "]：" + counters +
                " " + SleepStatusCounters() + " " + FeedbackStatusCounters() +
                " · " + (m_port != null ? m_port.Describe() : "端口未接");
        }

        /// <summary>
        /// 表现事件（109）的 10 秒计数器（主机 / 客户端都输出同一组名字，不适用的那侧为 0）：
        ///   · 主机：<c>fluEffectCaptured</c>（轮询识别出的上升沿）/ <c>fluEffectSent</c>（成功下发的
        ///     条数，不按玩家端重复计）/ <c>fluEffectSendFail</c> / <c>fluEffectEncodeFail</c> /
        ///     <c>fluEffectDropped</c>（单条超限）/ <c>fluEffectOverflow</c>（积压丢弃最旧）/
        ///     <c>fluEffectNoRecipients</c> / <c>fluEffectPollFail</c>；
        ///   · 客户端：<c>fluEffectRecv</c>（收到的条数）/ <c>fluEffectPresented</c>（真的播放）/
        ///     <c>fluEffectRejected</c>(+By 原因) / <c>fluEffectDuplicate</c>（同键旧序号）/
        ///     <c>fluEffectStale</c>（超过 TTL 或消息序号陈旧）/ <c>fluEffectNoAvatar</c> /
        ///     <c>fluEffectNonOwner</c>（其它玩家端化身的事件：显式忽略）/
        ///     <c>fluEffectMalformed</c> / <c>fluEffectWrongDirection</c>。
        /// </summary>
        private string FeedbackStatusCounters()
        {
            return "fluEffectCaptured=" + m_fluEffectCaptured +
                " fluEffectSent=" + m_fluEffectSent +
                " fluEffectSendFail=" + m_fluEffectSendFailures +
                " fluEffectEncodeFail=" + m_fluEffectEncodeFailures +
                " fluEffectDropped=" + m_fluEffectDropped +
                " fluEffectOverflow=" + m_fluEffectOverflow +
                " fluEffectNoRecipients=" + m_fluEffectNoRecipients +
                " fluEffectPollFail=" + m_effectPollFailures +
                " fluEffectRecv=" + m_fluEffectRecv +
                " fluEffectPresented=" + m_fluEffectPresented +
                " fluEffectRejected=" + m_fluEffectRejected +
                " fluEffectRejectedBy=" + FormatReasons(m_fluEffectRejectReasons) +
                " fluEffectDuplicate=" + m_fluEffectDuplicate +
                " fluEffectStale=" + m_fluEffectStale +
                " fluEffectNoAvatar=" + m_fluEffectNoAvatar +
                " fluEffectNonOwner=" + m_fluEffectNonOwner +
                " fluEffectMalformed=" + m_fluEffectMalformed +
                " fluEffectWrongDirection=" + m_fluEffectWrongDirection;
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
                " sleepFail=" + m_sleepPresentationFailures +
                // 入睡申请（104 / 105）：与唤醒（103）分开计数，状态行里一眼能分清
                // "主机不让睡"和"主机不让醒"。
                " sleepEntryTapCaptured=" + m_sleepEntryTapCaptured +
                " sleepEntrySent=" + m_sleepEntrySent +
                " sleepEntrySendFail=" + m_sleepEntrySendFailures +
                " sleepEntrySuppressed=" + m_sleepEntrySuppressed +
                " sleepEntrySuppressedBy=" + FormatReasons(m_sleepEntrySuppressedReasons) +
                " sleepEntryRecv=" + m_sleepEntryRecv +
                " sleepEntryAccepted=" + m_sleepEntryAccepted +
                " sleepEntryRejected=" + m_sleepEntryRejected +
                " sleepEntryRejectedBy=" + FormatReasons(m_sleepEntryRejectReasons) +
                " sleepEntryMalformed=" + m_sleepEntryMalformed +
                " sleepEntryWrongDirection=" + m_sleepEntryWrongDirection +
                " sleepEntryResultRecv=" + m_sleepEntryResultRecv +
                " sleepEntryResultStale=" + m_sleepEntryResultStale +
                " sleepEntryResultSendFail=" + m_sleepEntryResultSendFailures +
                " sleepEntryObservedRejectedBy=" +
                    FormatReasons(m_sleepEntryObservedRejectReasons) +
                " sleepEntryTimeouts=" + m_sleepEntryTimeouts;
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

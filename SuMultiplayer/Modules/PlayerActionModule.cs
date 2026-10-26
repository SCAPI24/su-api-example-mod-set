using System;
using System.Collections.Generic;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using PA = SuMultiplayer.Protocol.Action;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 玩家**动作 / 动画状态 / 手持物 / 背包**复制模块（主机权威，客户端只应用）。
    ///
    /// 需求（用户口径，逐条对应）：
    ///   ① 角色动作同步：远端化身按本人的真实动作播放 —— 走/跑/跳、瞄准/挥击、挖掘、进食、
    ///      游泳、受伤、蹲下、飞行等（动画状态位 + 抬手相位）；
    ///   ② 背包同步：主机权威；客户端把变更上报主机 → 主机应用 + 落盘 + 广播 → 重连/加入恢复；
    ///   ③ 手持物显示：远端化身显示本人手里的东西（槽位 + 内容 + 数据 + 堆叠），切换/使用/破坏/放置都跟着变；
    ///   ④ 投掷/交互抬手：边沿动作（挖掘/放置/使用/投掷/近战/瞄准释放）当帧复制，不攒到下个周期。
    ///
    /// 方向与权威（照旧实现）：
    ///   · 客户端 → 主机：本机玩家的动作边沿 / 动画状态 / 手持物 / 背包变更（**上报**）；
    ///   · 主机 → 客户端：主机自己的（client_id=0）+ 每个玩家端的权威动作/动画/手持物发给所有玩家端；
    ///     背包（PLAYER_INVENTORY=32）按收件人裁剪：**所有者端**（权威背包对账）+ 兴趣窗口包含其
    ///     化身位置的观察者端（客户端把别人的背包应用到远端化身做镜像；窗口外的端看不到它）。
    ///   客户端绝不模拟别的玩家：只把状态写进远端化身的**表现字段**（强制字段，见
    ///   <see cref="RemoteAvatarPresentation"/>）；位置/物理仍由位置快照的插值驱动。
    ///
    /// 旧实现对应处：
    ///   · 动作边沿 + 抬手相位：ScMultiplayerPlayerSync.cs:222-256（BroadcastPlayerPokeIfStarted /
    ///     BroadcastHostPlayerPokes）+ ScMultiplayerMessageHandlers.cs:78-160（接收侧强制字段）；
    ///   · 瞄准姿态：ScMultiplayerPlayerSync.cs:137-190（ApplyPersistentAimPresentation）+
    ///     ScMultiplayerMessageHandlers.cs:96-152；
    ///   · 动作广播：NetworkMessageSender.BroadcastPlayerPoke（PlayerActionMessage Poke）；
    ///   · 手持物/背包：PlayerEquipmentMessage + ScMultiplayerPlayerSync.cs:320-604
    ///     （SynchronizePlayerEquipment / HandlePlayerEquipmentMessage / ApplyEquipmentSnapshot）；
    ///   · 落盘/恢复：ScMultiplayerProfileHandlers.cs:576-618（RestorePlayerRecordInventory）。
    ///
    /// 本模块**无静态状态**，只通过端口与纯逻辑层（Net/）工作，不引用 Game。
    /// </summary>
    public sealed class PlayerActionModule : IMultiplayerModule, ISuMPMessageModule
    {
        /// <summary>主机自己的通道槽位键（= PlayerAvatarModule.HostAvatarSlotKey；只是本机索引，不是身份）。</summary>
        public const int HostSlotKey = 0;

        /// <summary>与位置同步同一条 32Hz 脉冲（旧实现 SyncBaseRate = 32）。</summary>
        public const float PulseDuration = 1f / 32f;
        private const int MaxPulsesPerFrame = 4;
        private const int SummaryLogMs = 10000;
        /// <summary>客户端请求背包关键帧的最小间隔（本机玩家就绪但还没收到关键帧时）。</summary>
        private const double KeyframeRequestIntervalSeconds = 2.0;
        private const int ProblemLogMs = 5000;
        /// <summary>化身消失多久后丢掉它的动画状态（防止 clientId 复用把旧动作演到新化身身上）。</summary>
        private const double AvatarMissingGraceSeconds = 2.0;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IPlayerActionPort m_port;
        private readonly IPlayerInventoryStore m_store;
        private readonly Func<double> m_clock;
        private readonly RemoteAnimationAdapter m_adapter;

        /// <summary>乘骑控制桥（2026-10-26）：组合根把它接到 NetworkAvatarSpawner.RidingInputResolver。</summary>
        public RemoteAnimationAdapter AnimationAdapter { get; private set; }
        private readonly PlayerActionDetector m_detector = new PlayerActionDetector();
        private readonly List<int> m_presentationScratch = new List<int>();

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;
        /// <summary>
        /// 主机：纯表现 / 镜像类事件（背包增量等）按客户端兴趣窗口投递（一次编码、并发入队）。
        /// 窗口只有地形模块这一处真相（客户端上报的中心区块 + 半径，与地形完全相同）；
        /// 拿不到端口时明确逐次计失败，绝不退回"发给所有人"。
        /// </summary>
        private IWindowBroadcastPort m_window;
        private IMessageRegistryPort m_messages;
        private bool m_attached;

        // ---------------------------------------------------------------- 本机（发送端）状态
        private LocalPlayerSignals m_localSignals;
        private bool m_hasLocalSignals;
        private PlayerMotionSnapshot m_sentMotion;
        private bool m_hasSentMotion;
        private PlayerHeldItemSnapshot m_sentHeldItem;
        private bool m_hasSentHeldItem;
        private int[] m_sentInventoryValues;
        private int[] m_sentInventoryCounts;
        private int m_sentInventoryActiveSlot = -1;
        private int m_localInventoryRevision;
        private int m_localInventoryVersion;
        private int m_localEquipmentRevision;
        /// <summary>
        /// 客户端：本机背包是否已经收到主机的权威关键帧。
        /// 在收到之前**不上传**背包/手持物 —— 否则会把下载世界里的临时背包覆盖到主机角色记录上。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:350-353
        ///         （m_equipmentSynchronizedClients：新加入的客户端不得先发布它的空占位背包）
        /// </summary>
        private bool m_localInventorySynchronized;
        private int m_lastLocalClientId = int.MinValue;
        private double m_lastKeyframeRequestSeconds;
        private long m_keyframeRequests;

        // ---------------------------------------------------------------- 脉冲 / 序号
        private double m_lastPulseTime;
        private float m_pulseAccumulator;
        private int m_serverTick;

        // ---------------------------------------------------------------- 主机（每个玩家端）状态
        private readonly Dictionary<int, int> m_clientHeldRevisions = new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_clientInventoryRevisions = new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_clientInventoryVersions = new Dictionary<int, int>();
        private readonly Dictionary<int, PlayerHeldItemSnapshot> m_clientHeldItems =
            new Dictionary<int, PlayerHeldItemSnapshot>();
        private readonly Dictionary<int, PlayerMotionSnapshot> m_clientMotions =
            new Dictionary<int, PlayerMotionSnapshot>();
        private readonly Dictionary<int, string> m_peerIdentities = new Dictionary<int, string>();
        private readonly Dictionary<int, string> m_peerNames = new Dictionary<int, string>();
        private readonly Dictionary<int, double> m_avatarMissingSince = new Dictionary<int, double>();
        /// <summary>主机：已经给这个玩家端发过背包关键帧（化身建好后发一次；重连/离开时清掉）。</summary>
        private readonly HashSet<int> m_clientKeyframesSent = new HashSet<int>();

        // ---------------------------------------------------------------- 计数（状态行）
        private long m_actionsSent;
        private long m_actionsReceived;
        private long m_actionsApplied;
        private long m_actionsRejected;
        private long m_actionsDropped;
        private long m_motionSent;
        private long m_motionReceived;
        private long m_motionApplied;
        private long m_motionDropped;
        private long m_equipmentSent;
        private long m_equipmentReceived;
        private long m_equipmentApplied;
        private long m_equipmentRejected;
        private long m_equipmentDropped;
        private long m_inventorySent;
        /// <summary>主机：背包投递时化身位置未知（主机还没有该玩家端的化身实体）的条数 —— 只发了它自己的端。</summary>
        private long m_inventoryAvatarCellUnknown;
        /// <summary>主机：背包投递时没有任何观察者端上报兴趣窗口（candidates=0）的条数。</summary>
        private long m_inventoryNoWindowObservers;
        /// <summary>主机：背包投递失败条数（缺 client_id / 端口缺失 / 入队 0 个端）—— 每次都明确计数。</summary>
        private long m_inventorySendFailures;
        /// <summary>主机：本机玩家背包投递到"窗口内观察者端"的入队数（主机自己没有网络收件人）。</summary>
        private long m_inventoryLocalHostObservers;
        private long m_inventoryReceived;
        private long m_inventoryApplied;
        private long m_inventoryRejected;
        private long m_inventoryDropped;
        private long m_inventoryKeyframesSent;
        private long m_inventoryPersisted;
        private long m_inventoryPersistFailures;
        private long m_inventoryUploadsGated;
        private long m_presentationsApplied;
        private long m_presentationsRejected;
        private long m_ownEchoSkipped;
        private long m_captureFailures;
        private long m_peerRejected;

        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private long m_lastSummaryMs;

        public PlayerActionModule(IPlayerActionPort port, IPlayerInventoryStore store, Func<double> clock)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_store = store;    // 客户端没有角色记录表：允许为空（只明确记录，不假装落盘）
            m_clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_adapter = new RemoteAnimationAdapter(clock);
            AnimationAdapter = m_adapter;
            m_descriptor = new ModuleDescriptor("player-action", "角色动作 / 手持物 / 背包")
            {
                // 硬依赖：传输（收发）与联机角色（远端化身表 + 本机玩家判定）都在同一条链上。
                RequiresCapabilities = new[] { "transport", "player.avatars" },
                ProvidesCapabilities = new[] { "player.actions" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        // ---------------------------------------------------------------- 端口装配

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 动作/背包同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "动作/背包同步不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_broadcast) || m_broadcast == null)
            {
                Log("警告：没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把客户端动作/背包广播给所有人");
            }
            if (!context.TryGetPort(out m_window) || m_window == null)
            {
                Log("警告：没有拿到窗口投递端口（IWindowBroadcastPort）→ 主机无法把背包按" +
                    "所有者 + 观察者兴趣窗口投递（每次广播计入失败，绝不退回发给所有人）");
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到动作/动画/手持物/背包消息");
            }
            m_transport.ClientJoined += HandleClientJoined;
            m_transport.ClientLeft += HandleClientLeft;
            m_attached = true;
            Log("角色动作/手持物/背包模块就绪：主机权威（客户端上报 → 主机应用+落盘+广播），" +
                "客户端只把状态写进远端化身；32Hz 脉冲 + 变化驱动");
        }

        /// <summary>消息注册（<see cref="ISuMPMessageModule"/>）：组合根通过注册表调用，自检也可直接调。</summary>
        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.PlayerAction, PA.PlayerAction.Parser,
                new DelegateMessageHandler<PA.PlayerAction>(OnPlayerAction), ModuleId);
            registry.Register((int)MessageType.PlayerMotionState, PA.PlayerMotionState.Parser,
                new DelegateMessageHandler<PA.PlayerMotionState>(OnPlayerMotionState), ModuleId);
            registry.Register((int)MessageType.PlayerEquipment, PA.PlayerEquipment.Parser,
                new DelegateMessageHandler<PA.PlayerEquipment>(OnPlayerEquipment), ModuleId);
            registry.Register((int)MessageType.PlayerInventory, PA.PlayerInventory.Parser,
                new DelegateMessageHandler<PA.PlayerInventory>(OnPlayerInventory), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = StatusLine();
            if (m_transport != null)
            {
                try { m_transport.ClientJoined -= HandleClientJoined; } catch (Exception) { }
                try { m_transport.ClientLeft -= HandleClientLeft; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_adapter.Clear();
            m_detector.Reset();
            m_clientHeldRevisions.Clear();
            m_clientInventoryRevisions.Clear();
            m_clientInventoryVersions.Clear();
            m_clientHeldItems.Clear();
            m_clientMotions.Clear();
            m_peerIdentities.Clear();
            m_peerNames.Clear();
            m_avatarMissingSince.Clear();
            m_clientKeyframesSent.Clear();
            m_hasLocalSignals = false;
            m_hasSentMotion = false;
            m_hasSentHeldItem = false;
            m_sentInventoryValues = null;
            m_sentInventoryCounts = null;
            m_sentInventoryActiveSlot = -1;
            m_localInventorySynchronized = false;
            m_control = null;
            m_broadcast = null;
            m_window = null;
            m_transport = null;
            m_attached = false;
            Log("角色动作/手持物/背包模块下线（" + reason + "）：" + summary);
        }

        // ---------------------------------------------------------------- 帧驱动

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            bool hosting = m_control != null && m_control.IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            if (hosting)
            {
                if (connected)
                {
                    CaptureLocal();
                    if (m_hasLocalSignals &&
                        m_detector.TryDetect(in m_localSignals, CurrentServerTick(),
                            out PlayerActionSnapshot action))
                    {
                        // 动作是边沿：当帧广播（旧实现 BroadcastPlayerPoke 同样立即发，不等周期）。
                        BroadcastAction(HostSlotKey, in action);
                    }
                    if (PulseDue(tick.Now))
                    {
                        PushLocalState(hosting: true);
                        PumpClientKeyframes();
                    }
                }
            }
            else if (connected)
            {
                int localClientId = m_transport.LocalClientId;
                if (localClientId != m_lastLocalClientId)
                {
                    // 重连 = 新的通道编号：上一轮的"背包已同步"不再成立（要等主机的新关键帧）。
                    if (m_lastLocalClientId != int.MinValue)
                    {
                        Log("本机通道编号变化（" + m_lastLocalClientId + " → " + localClientId +
                            "）→ 背包/手持物上传暂停，等主机重新下发权威背包关键帧");
                        m_localInventorySynchronized = false;
                        m_hasSentMotion = false;
                        m_hasSentHeldItem = false;
                        m_sentInventoryValues = null;
                        m_sentInventoryCounts = null;
                        m_sentInventoryActiveSlot = -1;
                    }
                    m_lastLocalClientId = localClientId;
                }
                CaptureLocal();
                if (m_hasLocalSignals &&
                    m_detector.TryDetect(in m_localSignals, CurrentServerTick(),
                        out PlayerActionSnapshot action))
                {
                    SendAction(in action);
                }
                if (PulseDue(tick.Now))
                {
                    PushLocalState(hosting: false);
                    if (!m_localInventorySynchronized) PumpKeyframeRequest(tick.Now);
                }
            }
            else
            {
                // 通道没开：本机状态基线作废，下一次连接从干净状态开始（不发陈旧动作）。
                if (m_hasLocalSignals)
                {
                    m_hasLocalSignals = false;
                    m_hasSentMotion = false;
                    m_hasSentHeldItem = false;
                    m_detector.Reset();
                }
                m_lastLocalClientId = int.MinValue;
                m_localInventorySynchronized = false;
            }
            // 两端都每帧推进远端表现（主机也要把客户端的动作演在主机世界里的化身上）。
            AdvancePresentations(tick.DeltaTime, tick.Now);
            ReportSummary(tick.Now);
        }

        private void CaptureLocal()
        {
            if (m_port.TryCaptureLocalSignals(out LocalPlayerSignals signals, out string reason))
            {
                m_localSignals = signals;
                m_hasLocalSignals = true;
                return;
            }
            if (m_hasLocalSignals)
            {
                m_hasLocalSignals = false;
                m_detector.Reset();
                LogOnce("capture-lost", "本机玩家的动作事实取不到了（" + reason +
                    "）→ 停止上报动作/动画状态（本机角色就绪后自动恢复；不拿默认值顶替）");
            }
            else
            {
                m_captureFailures++;
                LogRateLimited("capture-fail", "本机玩家的动作事实取不到（" + reason + "）→ 本帧不上报");
            }
        }

        /// <summary>
        /// 客户端：本机玩家已经就绪、但还没收到主机的权威背包关键帧时，**显式请求**一次。
        /// 主机的加入关键帧可能早于客户端进入世界（世界下载/加载期间没人接收），
        /// 所以这条请求是"重连/首次加入一定恢复背包"的兜底入口 —— 不是静默重试：
        /// 每次请求都计数，日志限流。
        /// </summary>
        private void PumpKeyframeRequest(double now)
        {
            if (m_transport == null || !m_transport.IsConnected) return;
            if (now - m_lastKeyframeRequestSeconds < KeyframeRequestIntervalSeconds) return;
            // 本机玩家还没就绪时不要请求：主机不知道往谁身上应用，请求只会被当成"没有化身"丢掉。
            if (!m_port.TryCaptureLocalInventory(out _, out _, out _, out _)) return;
            m_lastKeyframeRequestSeconds = now;
            m_keyframeRequests++;
            PlayerInventoryDelta request = PlayerInventoryDelta.CreateKeyframeRequest(0,
                m_localInventoryRevision);
            if (!m_transport.TrySend(MessageType.PlayerInventory, PlayerInventoryCodec.Encode(request)))
            {
                NoteProblem("请求背包关键帧发送失败（通道背压/未连接）→ 下一拍重试");
                return;
            }
            LogRateLimited("keyframe-request",
                "本机背包还没收到主机的权威关键帧 → 已显式请求（第 " + m_keyframeRequests +
                " 次；主机回 full=true 后开始同步）");
        }

        /// <summary>
        /// 32Hz 脉冲上的"变化驱动"上报：动画状态 / 手持物 / 背包各最多一条。
        /// 没变化就**什么都不发**（"(e) 未变化不发包"）。
        /// </summary>
        private void PushLocalState(bool hosting)
        {
            if (!m_hasLocalSignals) return;
            int slotKey = hosting ? HostSlotKey : 0;
            PlayerMotionSnapshot motion = PlayerActionDetector.BuildMotion(in m_localSignals,
                CurrentServerTick());
            if (!m_hasSentMotion || !motion.EqualsState(m_sentMotion))
            {
                if (hosting) BroadcastMotion(HostSlotKey, in motion);
                else SendMotion(in motion);
                m_sentMotion = motion;
                m_hasSentMotion = true;
            }

            // 背包/手持物：客户端在收到主机权威关键帧之前不上传（旧实现 m_equipmentSynchronizedClients）。
            if (!hosting && !m_localInventorySynchronized)
            {
                m_inventoryUploadsGated++;
                LogOnce("inventory-gated",
                    "本机背包还没收到主机的权威关键帧 → 暂停上传背包/手持物" +
                    "（避免把下载世界里的临时背包覆盖到主机角色记录上；关键帧到达后自动开始同步）");
                return;
            }

            var probe = new PlayerHeldItemSnapshot(0, 0, m_localSignals.ActiveSlotIndex,
                m_localSignals.ActiveSlotIndex, m_localSignals.HeldItemValue,
                m_localSignals.HeldItemCount, m_localSignals.HeldAimKind, m_localSignals.Aiming);
            if (!m_hasSentHeldItem || !probe.EqualsState(m_sentHeldItem))
            {
                int equipmentRevision = hosting
                    ? NextHostRevision(m_clientHeldRevisions, HostSlotKey)
                    : NextClientRevision(ref m_localEquipmentRevision);
                int equipmentVersion = hosting
                    ? CurrentInventoryVersion(HostSlotKey)
                    : m_localInventoryVersion;
                var held = new PlayerHeldItemSnapshot(equipmentRevision, equipmentVersion,
                    m_localSignals.ActiveSlotIndex, m_localSignals.ActiveSlotIndex,
                    m_localSignals.HeldItemValue, m_localSignals.HeldItemCount,
                    m_localSignals.HeldAimKind, m_localSignals.Aiming);
                if (hosting) BroadcastHeldItem(HostSlotKey, in held);
                else SendHeldItem(in held);
                m_sentHeldItem = held;
                m_hasSentHeldItem = true;
            }

            if (!m_port.TryCaptureLocalInventory(out int[] values, out int[] counts,
                    out int activeSlot, out string inventoryReason))
            {
                m_captureFailures++;
                LogRateLimited("capture-inventory",
                    "本机玩家的背包取不到（" + inventoryReason + "）→ 本拍不上报背包");
                return;
            }
            int revision = hosting
                ? NextHostRevision(m_clientInventoryRevisions, slotKey)
                : NextClientRevision(ref m_localInventoryRevision);
            int version = hosting ? NextHostInventoryVersion(HostSlotKey) : ++m_localInventoryVersion;
            PlayerInventoryDelta delta = PlayerInventoryMath.BuildDelta(slotKey, revision, version,
                activeSlot, m_sentInventoryValues, m_sentInventoryCounts, m_sentInventoryActiveSlot,
                values, counts);
            if (delta == null) return;
            m_sentInventoryValues = values;
            m_sentInventoryCounts = counts;
            m_sentInventoryActiveSlot = activeSlot;
            if (hosting) BroadcastInventory(delta);
            else SendInventory(delta);
        }

        /// <summary>
        /// 主机：玩家端的联机角色建好后，给它发一次**权威背包关键帧**（来自角色记录 / 首次出生）。
        /// 这既是客户端解锁上传的信号，也是重连后恢复背包的唯一入口。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:366-382（SynchronizeHostEquipment 首次广播）
        /// </summary>
        private void PumpClientKeyframes()
        {
            if (m_peerIdentities.Count == 0) return;
            foreach (KeyValuePair<int, string> peer in m_peerIdentities)
            {
                int clientId = peer.Key;
                if (m_clientKeyframesSent.Contains(clientId)) continue;
                if (!m_port.HasAvatar(clientId)) continue;
                SendKeyframeTo(clientId, "化身已建好");
            }
        }

        // ---------------------------------------------------------------- 发送（主机广播 / 客户端上报）

        private void BroadcastAction(int clientId, in PlayerActionSnapshot action)
        {
            if (m_broadcast == null)
            {
                LogOnce("no-broadcast-action",
                    "没有广播端口 → 动作无法发给其他客户端（主机自己的动作只在本地播）");
                return;
            }
            byte[] payload = PlayerActionCodec.Encode(clientId, in action);
            int sent = m_broadcast.BroadcastToPlayers(MessageType.PlayerAction, payload);
            m_actionsSent += sent;
            if (sent == 0 && m_broadcast.PlayerPeerCount > 0)
            {
                NoteProblem("动作广播没有入队（玩家端 " + m_broadcast.PlayerPeerCount +
                    " 个，可能背压）：kind=" + action.Kind + " seq=" + action.Sequence);
            }
        }

        private void BroadcastMotion(int clientId, in PlayerMotionSnapshot motion)
        {
            int sent = m_broadcast?.BroadcastToPlayers(MessageType.PlayerMotionState,
                PlayerMotionCodec.Encode(clientId, in motion)) ?? 0;
            m_motionSent += sent;
        }

        private void BroadcastHeldItem(int clientId, in PlayerHeldItemSnapshot item)
        {
            int sent = m_broadcast?.BroadcastToPlayers(MessageType.PlayerEquipment,
                PlayerEquipmentCodec.Encode(clientId, in item)) ?? 0;
            m_equipmentSent += sent;
        }

        /// <summary>
        /// 主机：把权威背包增量 / 关键帧投给"**所有者端** + 兴趣窗口包含其化身位置的**观察者端**"。
        ///
        /// 收件人依据（读同文件客户端处理器 <see cref="OnPlayerInventory"/>）：
        ///   (a) 所有者端（载荷里的 <c>delta.ClientId</c> 自己的端）：客户端把它当主机权威背包应用到
        ///       本机（客户端分支里的 <c>m_port.IsLocalSlot(localCandidate)</c>）→ 对账 + 解锁上传，必须到；
        ///   (b) 观察者端：客户端对 <c>HasAvatar</c> 的远端化身同样调用
        ///       <c>m_port.TryApplyInventory(clientId, delta)</c> 做镜像（手持物 / 展示一致性）→
        ///       只有兴趣窗口包含该化身的端才看得见它，窗口外的端不需要这份背包。
        /// 化身位置取自主机侧化身实体（<see cref="IWindowBroadcastPort.BroadcastToPlayersAtAvatar"/>，
        /// 与地形 / 实体同步同一份客户端上报窗口）；位置未知时只发它自己的端，观察者一个不发。
        /// **绝不退回"发给所有人"** —— 那正是本次要消掉的带宽浪费。
        /// </summary>
        private void BroadcastInventory(PlayerInventoryDelta delta)
        {
            if (delta.ClientId < 0)
            {
                // 负 client_id = 载荷没有可定位的所有者端（协议异常）：明确计数，不发给全体。
                m_inventorySendFailures++;
                NoteProblem("背包广播缺少 client_id（clientId=" + delta.ClientId +
                    "）→ 不入队（不发给所有人）");
                return;
            }
            if (m_window == null)
            {
                // Attach 已经明确记录过一次；这里逐次计入失败，绝不退回 BroadcastToPlayers。
                m_inventorySendFailures++;
                NoteProblem("没有窗口投递端口（IWindowBroadcastPort）→ clientId=" + delta.ClientId +
                    " 的背包无法投递（计入失败，绝不改发所有人）");
                return;
            }
            byte[] payload = PlayerInventoryCodec.Encode(delta);
            int sent = m_window.BroadcastToPlayersAtAvatar(MessageType.PlayerInventory, payload,
                delta.ClientId, 0, out int candidates, out bool avatarCellKnown);
            m_inventorySent += sent;
            if (!avatarCellKnown)
            {
                // 主机还没有这个化身的实体 → 端口只发它自己的端，观察者端一个都不发。
                m_inventoryAvatarCellUnknown++;
                NoteProblem("背包投递时 clientId=" + delta.ClientId +
                    " 的化身位置未知（主机还没有它的实体）→ 只发它自己的端，观察者端本次不发（绝不改发所有人）");
            }
            if (candidates == 0)
            {
                m_inventoryNoWindowObservers++;
                if (avatarCellKnown)
                {
                    NoteProblem("背包投递时没有任何观察者端上报兴趣窗口（candidates=0，clientId=" +
                        delta.ClientId + "）→ 观察者端本次不发（等它们上报窗口后自然收到）");
                }
            }
            if (delta.ClientId == 0)
            {
                // 主机本机玩家：没有"自己的端"（本地引擎已经应用）。伙伴端在兴趣窗口里能
                // 看到主机化身 → 需要这份背包做镜像（手持物 / 展示）；窗口外不需要。
                // 因此只有观察者投递，"入队 0"不是失败，但必须可审计。
                m_inventoryLocalHostObservers += sent;
                if (sent == 0 && candidates == 0)
                {
                    NoteProblem("主机本机背包没有需要镜像的观察者端（candidates=0）→ 本次不入队" +
                        "（本地已应用，不发给所有人）");
                }
                return;
            }
            if (sent == 0)
            {
                m_inventorySendFailures++;
                NoteProblem("背包投递入队 0 个端（clientId=" + delta.ClientId + "，candidates=" +
                    candidates + "，化身位置已知=" + avatarCellKnown +
                    "）→ 所有者端不在线/背压，计入失败（绝不改发所有人）");
            }
        }

        private void SendAction(in PlayerActionSnapshot action)
        {
            int sequence = action.Sequence;
            if (!m_transport.TrySend(MessageType.PlayerAction,
                    PlayerActionCodec.Encode(0, in action)))
            {
                // 动作是最新状态：不缓存重发（下一次动作会带上新相位）。
                NoteProblem("动作上报失败（通道背压/未连接）：kind=" + action.Kind +
                    " seq=" + sequence + " → 下一拍的新动作会覆盖，不重发陈旧边沿");
                return;
            }
            m_actionsSent++;
        }

        private void SendMotion(in PlayerMotionSnapshot motion)
        {
            if (m_transport.TrySend(MessageType.PlayerMotionState,
                    PlayerMotionCodec.Encode(0, in motion))) m_motionSent++;
            else NoteProblem("动画状态上报失败（通道背压/未连接）");
        }

        private void SendHeldItem(in PlayerHeldItemSnapshot item)
        {
            if (m_transport.TrySend(MessageType.PlayerEquipment,
                    PlayerEquipmentCodec.Encode(0, in item))) m_equipmentSent++;
            else NoteProblem("手持物上报失败（通道背压/未连接）");
        }

        private void SendInventory(PlayerInventoryDelta delta)
        {
            if (m_transport.TrySend(MessageType.PlayerInventory,
                    PlayerInventoryCodec.Encode(delta))) m_inventorySent++;
            else NoteProblem("背包上报失败（通道背压/未连接）：" + delta.Describe());
        }

        private bool SendTo(int clientId, MessageType type, byte[] payload)
        {
            return m_transport != null && m_transport.TrySendTo(clientId, type, payload);
        }

        // ---------------------------------------------------------------- 接收（主机应用+广播 / 客户端应用）

        private void OnPlayerAction(MessageContext context, PA.PlayerAction message)
        {
            if (!m_attached) return;
            if (!PlayerActionCodec.TryConvert(message, out int claimedClientId,
                    out PlayerActionSnapshot action, out string error))
            {
                m_actionsRejected++;
                NoteProblem("PlayerAction 载荷非法：" + error + " → 丢弃（clientId=" + context.ClientId + "）");
                return;
            }
            m_actionsReceived++;
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting)
            {
                if (!ValidatePeer(in context, claimedClientId, "PlayerAction", out int clientId)) return;
                if (!m_adapter.ApplyAction(clientId, in action, m_clock(), out string dropReason))
                {
                    m_actionsDropped++;
                    NoteProblem("丢弃 clientId=" + clientId + " 的动作：" + dropReason);
                    return;
                }
                m_actionsApplied++;
                BroadcastAction(clientId, in action);
                return;
            }
            if (claimedClientId < 0)
            {
                m_actionsRejected++;
                NoteProblem("主机广播的动作缺少 client_id → 丢弃");
                return;
            }
            if (m_port.IsLocalSlot(claimedClientId))
            {
                // 旧实现实机缺陷：把本机矿工相位写成主机那份 → 松手不收回、补播抬手被抹平。
                // Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:110-129
                m_ownEchoSkipped++;
                return;
            }
            if (!m_adapter.ApplyAction(claimedClientId, in action, m_clock(), out string reason))
            {
                m_actionsDropped++;
                NoteProblem("丢弃 clientId=" + claimedClientId + " 的动作：" + reason);
                return;
            }
            m_actionsApplied++;
        }

        private void OnPlayerMotionState(MessageContext context, PA.PlayerMotionState message)
        {
            if (!m_attached) return;
            if (!PlayerMotionCodec.TryConvert(message, out int claimedClientId,
                    out PlayerMotionSnapshot motion, out string error))
            {
                m_motionDropped++;
                NoteProblem("PlayerMotionState 载荷非法：" + error + " → 丢弃");
                return;
            }
            m_motionReceived++;
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting)
            {
                if (!ValidatePeer(in context, claimedClientId, "PlayerMotionState", out int clientId)) return;
                if (!m_adapter.ApplyMotion(clientId, in motion, m_clock(), out string reason))
                {
                    m_motionDropped++;
                    NoteProblem("丢弃 clientId=" + clientId + " 的动画状态：" + reason);
                    return;
                }
                m_motionApplied++;
                m_clientMotions[clientId] = motion;
                BroadcastMotion(clientId, in motion);
                return;
            }
            if (claimedClientId < 0)
            {
                m_motionDropped++;
                NoteProblem("主机广播的动画状态缺少 client_id → 丢弃");
                return;
            }
            if (m_port.IsLocalSlot(claimedClientId))
            {
                m_ownEchoSkipped++;
                return;
            }
            if (!m_adapter.ApplyMotion(claimedClientId, in motion, m_clock(), out string clientReason))
            {
                m_motionDropped++;
                NoteProblem("丢弃 clientId=" + claimedClientId + " 的动画状态：" + clientReason);
                return;
            }
            m_motionApplied++;
        }

        private void OnPlayerEquipment(MessageContext context, PA.PlayerEquipment message)
        {
            if (!m_attached) return;
            if (!PlayerEquipmentCodec.TryConvert(message, out int claimedClientId,
                    out PlayerHeldItemSnapshot item, out string error))
            {
                m_equipmentRejected++;
                NoteProblem("PlayerEquipment 载荷非法：" + error + " → 丢弃");
                return;
            }
            m_equipmentReceived++;
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting)
            {
                if (!ValidatePeer(in context, claimedClientId, "PlayerEquipment", out int clientId)) return;
                if (!m_port.HasAvatar(clientId))
                {
                    // 化身还没建好（关键帧之前客户端不会上传，正常情况下不会走到这里）。
                    m_equipmentDropped++;
                    LogRateLimited("equipment-no-avatar:" + clientId,
                        "收到 clientId=" + clientId + " 的手持物，但它的联机角色还没建好 → 本次不应用" +
                        "（不写进任何别的角色；下一次变化会重新上报）");
                    return;
                }
                if (!m_port.TryApplyHeldItem(clientId, in item, out string applyReason))
                {
                    m_equipmentDropped++;
                    NoteProblem("应用 clientId=" + clientId + " 的手持物失败：" + applyReason);
                    return;
                }
                m_equipmentApplied++;
                m_adapter.ApplyHeldItem(clientId, in item, out _);
                PlayerHeldItemSnapshot authoritative = CaptureAuthoritativeHeldItem(clientId, in item);
                m_clientHeldItems[clientId] = authoritative;
                BroadcastHeldItem(clientId, in authoritative);
                return;
            }
            if (claimedClientId < 0)
            {
                m_equipmentRejected++;
                NoteProblem("主机广播的手持物缺少 client_id → 丢弃");
                return;
            }
            if (m_port.IsLocalSlot(claimedClientId))
            {
                m_ownEchoSkipped++;
                return;
            }
            if (!m_port.HasAvatar(claimedClientId))
            {
                m_equipmentDropped++;
                LogRateLimited("equipment-no-avatar:" + claimedClientId,
                    "收到 clientId=" + claimedClientId + " 的手持物，但本机还没有它的化身 → 本次不应用" +
                    "（化身建好后的下一次变化会重新到达）");
                return;
            }
            if (!m_port.TryApplyHeldItem(claimedClientId, in item, out string reason))
            {
                m_equipmentDropped++;
                NoteProblem("应用 clientId=" + claimedClientId + " 的手持物失败：" + reason);
                return;
            }
            m_adapter.ApplyHeldItem(claimedClientId, in item, out _);
            m_equipmentApplied++;
        }

        private void OnPlayerInventory(MessageContext context, PA.PlayerInventory message)
        {
            if (!m_attached) return;
            if (!PlayerInventoryCodec.TryConvert(message, out PlayerInventoryDelta delta, out string error))
            {
                m_inventoryRejected++;
                NoteProblem("PlayerInventory 载荷非法：" + error + " → 丢弃");
                return;
            }
            m_inventoryReceived++;
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting)
            {
                if (!ValidatePeer(in context, delta.ClientId, "PlayerInventory", out int clientId)) return;
                if (delta.RequestKeyframe)
                {
                    // 显式关键帧请求：立刻回一条 full=true（角色记录优先，其次化身当前背包）。
                    SendKeyframeTo(clientId, "客户端请求");
                    return;
                }
                if (m_clientInventoryRevisions.TryGetValue(clientId, out int applied) &&
                    delta.Revision <= applied)
                {
                    m_inventoryDropped++;
                    NoteProblem("丢弃 clientId=" + clientId + " 的背包变更：序号不新（收到 " +
                        delta.Revision + "，已应用 " + applied + "）");
                    return;
                }
                if (!m_port.HasAvatar(clientId))
                {
                    m_inventoryDropped++;
                    LogRateLimited("inventory-no-avatar:" + clientId,
                        "收到 clientId=" + clientId + " 的背包变更，但它的联机角色还没建好 → 本次不应用" +
                        "（角色建好时按角色记录恢复；关键帧之后的变化会继续上报）");
                    return;
                }
                if (!m_port.TryApplyInventory(clientId, delta, out string applyReason))
                {
                    m_inventoryRejected++;
                    NoteProblem("应用 clientId=" + clientId + " 的背包变更失败：" + applyReason);
                    return;
                }
                m_inventoryApplied++;
                m_clientInventoryRevisions[clientId] = delta.Revision;
                int version = NextHostInventoryVersion(clientId);
                // 回读权威值（容量/方块行为可能拒绝部分槽位）→ 落盘 + 广播权威结果。
                if (!m_port.TryCaptureInventory(clientId, out int[] values, out int[] counts,
                        out int activeSlot, out string captureReason))
                {
                    m_inventoryPersistFailures++;
                    NoteProblem("clientId=" + clientId + " 的背包落地后读不回来（" + captureReason +
                        "）→ 本次不落盘也不广播（不拿请求值假冒权威值）");
                    return;
                }
                PersistInventory(clientId, values, counts, activeSlot, version);
                PlayerInventoryDelta authoritative = BuildAuthoritativeInventory(clientId, values,
                    counts, activeSlot, version, delta);
                if (authoritative != null) BroadcastInventory(authoritative);
                return;
            }
            int localCandidate = delta.ClientId;
            if (localCandidate < 0)
            {
                m_inventoryRejected++;
                NoteProblem("主机广播的背包缺少 client_id → 丢弃");
                return;
            }
            if (m_port.IsLocalSlot(localCandidate))
            {
                // 本机玩家自己的背包：主机权威结果 → 对账（旧实现同样把权威背包应用到本机玩家）。
                if (delta.Revision <= m_localInventoryRevision)
                {
                    m_inventoryDropped++;
                    return;
                }
                if (!m_port.TryApplyInventory(localCandidate, delta, out string reason))
                {
                    m_inventoryRejected++;
                    NoteProblem("把主机的权威背包应用到本机玩家失败：" + reason);
                    return;
                }
                m_localInventoryRevision = delta.Revision;
                if (delta.InventoryVersion > m_localInventoryVersion)
                    m_localInventoryVersion = delta.InventoryVersion;
                m_inventoryApplied++;
                if (!m_localInventorySynchronized)
                {
                    m_localInventorySynchronized = true;
                    Log("已收到主机的权威背包关键帧（rev=" + delta.Revision + " · " +
                        delta.Indices.Length + " 槽）→ 本机背包对账完成，开始上传后续变化");
                }
                // 以**落地后的引擎状态**作为新的发送基线：避免把"旧预测"当成变化重新上传（也避免来回刷）。
                m_sentInventoryValues = null;
                m_sentInventoryCounts = null;
                m_sentInventoryActiveSlot = -1;
                if (m_port.TryCaptureLocalInventory(out int[] baselineValues, out int[] baselineCounts,
                        out int baselineActiveSlot, out string baselineReason))
                {
                    m_sentInventoryValues = baselineValues;
                    m_sentInventoryCounts = baselineCounts;
                    m_sentInventoryActiveSlot = baselineActiveSlot;
                }
                else
                {
                    LogRateLimited("baseline-capture",
                        "本机背包对账后取不回基线（" + baselineReason + "）→ 下一拍会重新采样");
                }
                return;
            }
            if (!m_port.HasAvatar(localCandidate))
            {
                m_inventoryDropped++;
                LogRateLimited("inventory-no-avatar:" + localCandidate,
                    "收到 clientId=" + localCandidate + " 的背包，但本机还没有它的化身 → 本次不应用");
                return;
            }
            if (!m_port.TryApplyInventory(localCandidate, delta, out string avatarApplyReason))
            {
                m_inventoryRejected++;
                NoteProblem("应用 clientId=" + localCandidate + " 的背包失败：" + avatarApplyReason);
                return;
            }
            m_inventoryApplied++;
        }

        // ---------------------------------------------------------------- 校验 / 权威回读

        /// <summary>
        /// 主机侧身份校验：来源必须是玩家端，载荷里的 client_id 只能为空(0)或等于来源连接。
        /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:3422-3426
        ///         （旧实现同样要求 msg.PlayerIndex == 0 或 sourceClientId）
        /// </summary>
        private bool ValidatePeer(in MessageContext context, int claimedClientId, string kind,
            out int clientId)
        {
            clientId = context.ClientId;
            if (context.ClientId <= 0)
            {
                m_peerRejected++;
                NoteProblem(kind + " 来源非法（clientId=" + context.ClientId +
                    "）→ 丢弃（主机不接受非连接来源的动作/状态）");
                return false;
            }
            if (m_broadcast != null && !m_broadcast.IsPlayerClient(context.ClientId))
            {
                m_peerRejected++;
                NoteProblem(kind + " 来源 clientId=" + context.ClientId + " 不是玩家端（调试端只读）→ 丢弃");
                return false;
            }
            if (claimedClientId != 0 && claimedClientId != context.ClientId)
            {
                m_peerRejected++;
                NoteProblem(kind + " 自称 clientId=" + claimedClientId + "，实际来源 " + context.ClientId +
                    " → 丢弃（不允许冒领另一个玩家端的动作/背包）");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 主机：开火后从**权威化身背包**回读手持物并立即广播复位（draw=0 / 箭弹已清 / Aiming=false）。
        /// 这是"释放后远端化身仍满弓"的显式修复：主机执行了引擎 Completed 的复位，
        /// 但开火者的客户端背包增量可能还没到，其他客户端更不会自动知道；
        /// 主动重播一条权威 PlayerEquipment 让所有端同时复位。
        /// </summary>
        public bool TryBroadcastHeldItem(int clientId, HeldItemAimKind aimKind, out string reason)
        {
            reason = null;
            if (!m_attached)
            {
                reason = "not-attached";
                return false;
            }
            if (m_control == null || !m_control.IsHosting)
            {
                reason = "not-host";
                return false;
            }
            if (!m_port.HasAvatar(clientId))
            {
                reason = "no-avatar";
                return false;
            }
            if (m_broadcast == null)
            {
                reason = "no-broadcast";
                return false;
            }
            if (!m_port.TryCaptureInventory(clientId, out int[] values, out int[] counts,
                    out int activeSlot, out reason))
                return false;
            int slot = activeSlot >= 0 && activeSlot < values.Length ? activeSlot : -1;
            int value = slot >= 0 ? values[slot] : 0;
            int count = slot >= 0 ? counts[slot] : 0;
            int revision = NextHostRevision(m_clientHeldRevisions, clientId);
            int version = NextHostInventoryVersion(clientId);
            var held = new PlayerHeldItemSnapshot(revision, version, activeSlot, slot, value,
                count, aimKind, aiming: false);
            int sent = m_broadcast.BroadcastToPlayers(MessageType.PlayerEquipment,
                PlayerEquipmentCodec.Encode(clientId, in held));
            if (sent <= 0)
            {
                reason = "no-recipients";
                return false;
            }
            m_equipmentSent += sent;
            m_clientHeldItems[clientId] = held;
            return true;
        }

        private PlayerHeldItemSnapshot CaptureAuthoritativeHeldItem(int clientId,
            in PlayerHeldItemSnapshot claimed)
        {
            int revision = NextHostRevision(m_clientHeldRevisions, clientId);
            if (!m_port.TryCaptureInventory(clientId, out int[] values, out int[] counts,
                    out int activeSlot, out string reason))
            {
                LogRateLimited("equipment-capture:" + clientId,
                    "clientId=" + clientId + " 的手持物落地后读不回背包（" + reason +
                    "）→ 回执按客户端上报值广播（明确记录：这不是权威回读）");
                return new PlayerHeldItemSnapshot(revision, claimed.InventoryVersion,
                    claimed.ActiveSlotIndex, claimed.SlotIndex, claimed.ItemValue,
                    claimed.ItemCount, claimed.AimKind, claimed.Aiming);
            }
            int slot = activeSlot >= 0 && activeSlot < values.Length ? activeSlot : -1;
            int value = slot >= 0 ? values[slot] : 0;
            int count = slot >= 0 ? counts[slot] : 0;
            return new PlayerHeldItemSnapshot(revision, claimed.InventoryVersion, activeSlot, slot,
                value, count, claimed.AimKind, claimed.Aiming);
        }

        private PlayerInventoryDelta BuildAuthoritativeInventory(int clientId, int[] values,
            int[] counts, int activeSlot, int version, PlayerInventoryDelta requested)
        {
            int revision = NextHostRevision(m_clientInventoryRevisions, clientId);
            if (requested.Full)
            {
                return PlayerInventoryDelta.CreateFull(clientId, revision, version, activeSlot,
                    values, counts);
            }
            // 只回执客户端报过的那些槽位（值取落地后的权威值）。
            var indices = new List<int>(requested.Indices.Length);
            var slotValues = new List<int>(requested.Indices.Length);
            var slotCounts = new List<int>(requested.Indices.Length);
            for (int i = 0; i < requested.Indices.Length; i++)
            {
                int index = requested.Indices[i];
                if (index < 0 || index >= values.Length) continue;
                indices.Add(index);
                slotValues.Add(values[index]);
                slotCounts.Add(counts[index]);
            }
            if (indices.Count == 0)
            {
                NoteProblem("clientId=" + clientId + " 的背包回执没有任何可回读的槽位（请求 " +
                    requested.Indices.Length + " 槽，本端同步范围 " + values.Length +
                    " 槽）→ 不回执（不拿空结果当已同步）");
                return null;
            }
            return PlayerInventoryDelta.CreateDelta(clientId, revision, version, activeSlot,
                indices.ToArray(), slotValues.ToArray(), slotCounts.ToArray());
        }

        private void PersistInventory(int clientId, int[] values, int[] counts, int activeSlot,
            int version)
        {
            if (m_store == null)
            {
                m_inventoryPersistFailures++;
                LogOnce("no-inventory-store",
                    "没有角色记录存储端口（主机侧 IPlayerInventoryStore 未注入）→ 客户端背包变化只应用不落盘；" +
                    "重连恢复会依赖主机 5 秒周期的角色记录抓取（明确记录，不假装已落盘）");
                return;
            }
            if (!m_store.StoreInventory(clientId, values, counts, activeSlot, version,
                    out string reason))
            {
                m_inventoryPersistFailures++;
                NoteProblem("clientId=" + clientId + " 的背包落盘失败：" + reason);
                return;
            }
            m_inventoryPersisted++;
        }

        // ---------------------------------------------------------------- 加入 / 离开

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_attached || m_control == null || !m_control.IsHosting) return;
            m_peerIdentities[clientId] = identity ?? string.Empty;
            m_peerNames[clientId] = name ?? string.Empty;
            // 新连接 = 新基线：旧连接的序号/状态全部作废（重连是同一个身份、新的 clientId）。
            m_adapter.Remove(clientId);
            m_clientHeldRevisions.Remove(clientId);
            m_clientInventoryRevisions.Remove(clientId);
            m_clientInventoryVersions.Remove(clientId);
            m_clientHeldItems.Remove(clientId);
            m_clientMotions.Remove(clientId);
            m_avatarMissingSince.Remove(clientId);
            m_clientKeyframesSent.Remove(clientId);
            // 角色记录里已经有背包（重连 / 老玩家）→ 立刻给关键帧，不等化身建好；
            // 首次加入时记录还没有背包，关键帧在化身建好后由 PumpClientKeyframes 发出。
            SendKeyframeTo(clientId, rejoin ? "重连" : "加入");
            SendCatchUpState(clientId, rejoin);
        }

        /// <summary>
        /// 给某个客户端发**权威背包关键帧**（full=true）：
        ///   · 优先角色记录（化身可能还没建好；重连/老玩家的来源）；
        ///   · 否则读化身当前背包（首次加入：角色记录由建角色流程写好，读化身即可）。
        /// 发成功才记入 m_clientKeyframesSent；失败由下一拍 / 下一次请求重试。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:576-618
        /// </summary>
        private void SendKeyframeTo(int clientId, string why)
        {
            int[] values = null;
            int[] counts = null;
            int activeSlot = -1;
            string source = null;
            string failure = null;
            if (m_store != null)
            {
                m_peerIdentities.TryGetValue(clientId, out string identity);
                m_peerNames.TryGetValue(clientId, out string name);
                if (m_store.TryLoadInventory(identity ?? string.Empty, name ?? string.Empty,
                        out values, out counts, out activeSlot, out string recordReason))
                {
                    source = "角色记录";
                }
                else
                {
                    failure = recordReason;
                }
            }
            else
            {
                failure = "没有角色记录存储端口";
            }
            if (source == null && m_port.HasAvatar(clientId) &&
                m_port.TryCaptureInventory(clientId, out values, out counts, out activeSlot,
                    out string avatarReason))
            {
                source = "化身当前背包";
            }
            else if (source == null && failure == null)
            {
                failure = "化身还没建好";
            }
            if (source == null)
            {
                LogOnce("keyframe-source:" + clientId,
                    "clientId=" + clientId + " 的背包关键帧暂时没有来源（" + (failure ?? "未知") +
                    "）→ 化身建好后的下一拍再发（不拿空背包当已同步）");
                return;
            }
            int revision = NextHostRevision(m_clientInventoryRevisions, clientId);
            int version = NextHostInventoryVersion(clientId);
            PlayerInventoryDelta full = PlayerInventoryDelta.CreateFull(clientId, revision, version,
                activeSlot, values, counts);
            if (!SendTo(clientId, MessageType.PlayerInventory, PlayerInventoryCodec.Encode(full)))
            {
                NoteProblem("给 clientId=" + clientId + " 的背包关键帧没有入队（背压）→ 下一拍/下次请求重试");
                return;
            }
            m_clientKeyframesSent.Add(clientId);
            m_inventoryKeyframesSent++;
            m_inventorySent++;
            Log("已给 clientId=" + clientId + " 发权威背包关键帧（" + why + " · 来源 " + source +
                " · " + values.Length + " 槽 · 活跃槽 " + activeSlot + "）");
        }

        /// <summary>
        /// 追帧：把**主机与每个已知玩家端当前的动画状态 / 手持物**补发给刚加入或重连的客户端，
        /// 并把其他玩家端的背包整包发给它 —— 否则新客户端里的远端化身要等到下一次"变化"
        /// 才看得到别人手里的东西。
        /// </summary>
        private void SendCatchUpState(int clientId, bool rejoin)
        {
            int sent = 0;
            if (m_hasSentMotion &&
                SendTo(clientId, MessageType.PlayerMotionState,
                    PlayerMotionCodec.Encode(HostSlotKey, in m_sentMotion))) sent++;
            if (m_hasSentHeldItem &&
                SendTo(clientId, MessageType.PlayerEquipment,
                    PlayerEquipmentCodec.Encode(HostSlotKey, in m_sentHeldItem))) sent++;
            foreach (KeyValuePair<int, PlayerMotionSnapshot> pair in m_clientMotions)
            {
                if (pair.Key == clientId) continue;
                PlayerMotionSnapshot motion = pair.Value;
                if (SendTo(clientId, MessageType.PlayerMotionState,
                        PlayerMotionCodec.Encode(pair.Key, in motion))) sent++;
            }
            foreach (KeyValuePair<int, PlayerHeldItemSnapshot> pair in m_clientHeldItems)
            {
                if (pair.Key == clientId) continue;
                PlayerHeldItemSnapshot item = pair.Value;
                if (SendTo(clientId, MessageType.PlayerEquipment,
                        PlayerEquipmentCodec.Encode(pair.Key, in item))) sent++;
            }
            foreach (int peerId in new List<int>(m_peerIdentities.Keys))
            {
                if (peerId == clientId) continue;
                if (!m_port.HasAvatar(peerId)) continue;
                if (!m_port.TryCaptureInventory(peerId, out int[] values, out int[] counts,
                        out int activeSlot, out string reason))
                {
                    LogRateLimited("catchup-peer-inventory:" + peerId,
                        "追帧时读不到 clientId=" + peerId + " 的背包（" + reason +
                        "）→ 它的手持物要等下一次变化才到达新客户端");
                    continue;
                }
                int revision = NextHostRevision(m_clientInventoryRevisions, peerId);
                int version = NextHostInventoryVersion(peerId);
                PlayerInventoryDelta full = PlayerInventoryDelta.CreateFull(peerId, revision, version,
                    activeSlot, values, counts);
                if (SendTo(clientId, MessageType.PlayerInventory, PlayerInventoryCodec.Encode(full)))
                {
                    sent++;
                    m_inventorySent++;
                }
            }
            Log("已给 clientId=" + clientId + (rejoin ? "（重连）" : "（首次加入）") +
                " 发追帧状态 " + sent + " 条（主机/其他玩家端的动画状态与手持物 + 其他玩家端背包）");
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            if (!m_attached || m_control == null || !m_control.IsHosting) return;
            m_adapter.Remove(clientId);
            m_clientHeldRevisions.Remove(clientId);
            m_clientInventoryRevisions.Remove(clientId);
            m_clientInventoryVersions.Remove(clientId);
            m_clientHeldItems.Remove(clientId);
            m_clientMotions.Remove(clientId);
            m_avatarMissingSince.Remove(clientId);
            m_clientKeyframesSent.Remove(clientId);
            m_peerIdentities.Remove(clientId);
            m_peerNames.Remove(clientId);
            Log("玩家端 clientId=" + clientId + " 离开（" + (reason ?? string.Empty) +
                "）→ 已丢掉它的动作/动画/背包同步状态（背包已在离开时由角色记录落盘）");
        }

        // ---------------------------------------------------------------- 每帧表现写回

        private void AdvancePresentations(float deltaTime, double now)
        {
            m_adapter.Advance(deltaTime, now);
            if (m_adapter.Count == 0) return;
            m_presentationScratch.Clear();
            foreach (int clientId in m_adapter.ClientIds) m_presentationScratch.Add(clientId);
            for (int i = 0; i < m_presentationScratch.Count; i++)
            {
                int clientId = m_presentationScratch[i];
                if (m_port.IsLocalSlot(clientId))
                {
                    // 自己的动作绝不写回自己（否则本机矿工相位被网络值钉住）。
                    continue;
                }
                if (!m_port.HasAvatar(clientId))
                {
                    if (!m_avatarMissingSince.TryGetValue(clientId, out double since))
                    {
                        m_avatarMissingSince[clientId] = now;
                    }
                    else if (now - since >= AvatarMissingGraceSeconds)
                    {
                        m_adapter.Remove(clientId);
                        m_avatarMissingSince.Remove(clientId);
                        LogRateLimited("present-drop:" + clientId,
                            "clientId=" + clientId + " 的化身已不存在超过 " + AvatarMissingGraceSeconds +
                            " 秒 → 丢掉它的动画状态（避免 clientId 复用时把旧动作演到新化身身上）");
                    }
                    continue;
                }
                m_avatarMissingSince.Remove(clientId);
                if (!m_adapter.TryGetPresentation(clientId, out RemoteAvatarPresentation presentation))
                    continue;
                if (!m_port.TryApplyPresentation(clientId, in presentation, out string reason))
                {
                    m_presentationsRejected++;
                    LogRateLimited("present-fail:" + clientId,
                        "把动画表现写进 clientId=" + clientId + " 的化身失败：" + reason);
                    continue;
                }
                m_presentationsApplied++;
            }
        }

        // ---------------------------------------------------------------- 序号 / 版本 / 脉冲

        private int CurrentServerTick() => m_serverTick;

        private static int NextClientRevision(ref int counter)
        {
            counter = counter == int.MaxValue ? 1 : counter + 1;
            return counter;
        }

        private static int NextHostRevision(Dictionary<int, int> table, int clientId)
        {
            table.TryGetValue(clientId, out int current);
            int next = current == int.MaxValue ? 1 : current + 1;
            table[clientId] = next;
            return next;
        }

        private int CurrentInventoryVersion(int clientId)
        {
            return m_clientInventoryVersions.TryGetValue(clientId, out int value) ? value : 0;
        }

        private int NextHostInventoryVersion(int clientId)
        {
            m_clientInventoryVersions.TryGetValue(clientId, out int current);
            int next = current == int.MaxValue ? 1 : current + 1;
            m_clientInventoryVersions[clientId] = next;
            return next;
        }

        /// <summary>
        /// 32Hz 相位（与 TerrainSyncModule 的位置脉冲同一条节奏；一帧最多补 4 拍）。
        /// 返回 true = 本帧到点了，可以做"变化驱动"的上报/广播。
        /// </summary>
        private bool PulseDue(double now)
        {
            if (m_lastPulseTime <= 0.0) m_lastPulseTime = now;
            double elapsed = now - m_lastPulseTime;
            m_lastPulseTime = now;
            if (elapsed < 0.0) elapsed = 0.0;
            m_pulseAccumulator += (float)elapsed;
            int pulses = 0;
            while (m_pulseAccumulator >= PulseDuration && pulses < MaxPulsesPerFrame)
            {
                m_pulseAccumulator -= PulseDuration;
                pulses++;
            }
            if (m_pulseAccumulator > PulseDuration * MaxPulsesPerFrame)
            {
                m_pulseAccumulator = 0f;
                LogOnce("pulse-behind", "动作同步相位落后超过 " + MaxPulsesPerFrame +
                    " 拍（单帧卡顿）→ 丢弃积压相位，不补发陈旧状态（与位置同步同一口径）");
            }
            if (pulses == 0) return false;
            m_serverTick = m_serverTick == int.MaxValue ? 1 : m_serverTick + 1;
            return true;
        }

        // ---------------------------------------------------------------- 诊断 / 计数

        /// <summary>自检/诊断：某个远端化身最近应用的动作（种类 + 相位）。</summary>
        public bool TryGetRemoteActionState(int clientId, out PlayerActionKind kind, out float phase) =>
            m_adapter.TryGetLastAction(clientId, out kind, out phase);

        /// <summary>自检/诊断：某个远端化身当前的动画状态位。</summary>
        public bool TryGetRemoteMotion(int clientId, out PlayerMotionSnapshot motion) =>
            m_adapter.TryGetMotion(clientId, out motion);

        public long ActionsSent => m_actionsSent;
        public long MotionSent => m_motionSent;
        public long EquipmentSent => m_equipmentSent;
        public long InventorySent => m_inventorySent;
        public long PresentationsApplied => m_presentationsApplied;
        public long ActionsDropped => m_actionsDropped;
        public long ActionsReceived => m_actionsReceived;
        public long PeerRejected => m_peerRejected;
        public long InventoryReceived => m_inventoryReceived;
        public long InventoryRejected => m_inventoryRejected;
        public long EquipmentDropped => m_equipmentDropped;
        public long InventoryDropped => m_inventoryDropped;
        public long InventoryApplied => m_inventoryApplied;
        public long InventoryPersisted => m_inventoryPersisted;
        public long OwnEchoSkipped => m_ownEchoSkipped;
        public long InventoryUploadsGated => m_inventoryUploadsGated;
        public bool LocalInventorySynchronized => m_localInventorySynchronized;

        public string StatusLine() =>
            "player-action: action(sent=" + m_actionsSent + " recv=" + m_actionsReceived +
            " applied=" + m_actionsApplied + " rejected=" + m_actionsRejected +
            " dropped=" + m_actionsDropped + ")" +
            " motion(sent=" + m_motionSent + " recv=" + m_motionReceived +
            " applied=" + m_motionApplied + " dropped=" + m_motionDropped + ")" +
            " equipment(sent=" + m_equipmentSent + " recv=" + m_equipmentReceived +
            " applied=" + m_equipmentApplied + " rejected=" + m_equipmentRejected +
            " dropped=" + m_equipmentDropped + ")" +
            " inventory(sent=" + m_inventorySent + " recv=" + m_inventoryReceived +
            " applied=" + m_inventoryApplied + " rejected=" + m_inventoryRejected +
            " dropped=" + m_inventoryDropped + " keyframes=" + m_inventoryKeyframesSent +
            " avatarCellUnknown=" + m_inventoryAvatarCellUnknown +
            " noWindowObservers=" + m_inventoryNoWindowObservers +
            " sendFailures=" + m_inventorySendFailures +
            " localHostObservers=" + m_inventoryLocalHostObservers +
            " persisted=" + m_inventoryPersisted + " persistFailures=" + m_inventoryPersistFailures +
            " gated=" + m_inventoryUploadsGated +
            " keyframeRequests=" + m_keyframeRequests + ")" +
            " presentations=" + m_presentationsApplied + "/" + m_presentationsRejected +
            " ownEchoSkipped=" + m_ownEchoSkipped +
            " captureFailures=" + m_captureFailures +
            " peerRejected=" + m_peerRejected +
            " " + m_adapter.Describe() +
            " " + (m_port?.Describe() ?? "port=none");

        private void ReportSummary(double now)
        {
            long nowMs = (long)(now * 1000.0);
            if (nowMs - m_lastSummaryMs < SummaryLogMs) return;
            m_lastSummaryMs = nowMs;
            Log(StatusLine());
        }

        private void Log(string message) => m_context?.Diagnostics?.Info(m_descriptor.Id, message);

        private void NoteProblem(string message)
        {
            long nowMs = (long)(m_clock() * 1000.0);
            string key = message.Length > 96 ? message.Substring(0, 96) : message;
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics?.Warn(m_descriptor.Id, message);
        }

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            Log(message);
        }

        private void LogRateLimited(string key, string message)
        {
            long nowMs = (long)(m_clock() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            Log(message);
        }
    }
}

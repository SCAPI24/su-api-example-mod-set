using System;
using System.Collections.Generic;
using System.Text;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol.UiState;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 客户端界面层 → 主机分屏视图的镜像模块（<c>CLIENT_UI_STATE</c>，id 100）。
    ///
    /// 为什么需要它：主机跟随某个客户端的分屏视图（<c>Engine/ClientViewEngine.cs</c> 的自动
    /// 客户端视图 / <c>Engine/MirrorCharacterEngine.cs</c> 的 H3 replace 模式）把
    /// <c>GameWidget.Target</c> 绑到客户端化身的 <c>ComponentPlayer</c>，所以**画面**已经是客户端
    /// 第一人称；但视图的 HUD 属于视图自己的本机玩家（H3 / 临时壳玩家），因此输入形态、HUD 显隐、
    /// 打开的界面全都是主机的，而不是客户端的。
    ///
    /// 方向与语义：
    ///   · **客户端 → 主机**（只有客户端发）：每帧只读采集一份快照（
    ///     <see cref="IClientUiStateSource"/>），**变化才发 + 2 s 心跳**；
    ///   · **主机施加**（<see cref="IUiMirrorPort"/>）：把快照施加到"相机 Target = 该客户端
    ///     ComponentPlayer"的那个 GameWidget —— 自动视图与 H3 replace 模式是同一条绑定。
    ///     客户端自己的 UI / 玩法**一个字段都不写**（只读客户端状态，不远程操作）。
    ///
    /// 可精确镜像 / 明确不支持的清单（不静默跳过，全部计数 + 限流日志
    /// <c>uiMirrorUnsupported:&lt;type&gt;</c>）：
    ///   · 输入形态：精确。引擎里"某个视图显示不显示触摸控件"的唯一开关是
    ///     <c>ComponentInput.IsControlledByTouch</c>（Source: ComponentGui.cs:395-419），
    ///     主机只写这个属性；不重建 GameWidget 输入面（<c>WidgetInput.Devices</c> 由
    ///     <c>GameWidget.DetermineInputDevices</c> 每帧从**本机全部玩家**派生且恒含 Touch，
    ///     Source: GameWidget.cs:101-162），也不改 <c>PlayerData.InputDevice</c>（它是存盘字段，
    ///     并且会改变主机自己视图的设备划分，Source: PlayerData.cs:388,401）。
    ///   · HUD 显隐：精确。切换视图的 <c>GameWidget.GuiWidget.IsVisible</c>（视图自己的 HUD 根）。
    ///   · 模态面板（背包 / 创造背包 / 衣物 / 箱子 / 熔炉 / 工作台 / 分配器）：用**引擎自己的
    ///     面板类**围绕主机侧已同步的化身背包 / 权威容器库存实例化，纯显示
    ///     （整棵子树 IsHitTestVisible=false，主机点不到、也不会产生任何主机侧编辑）。
    ///     物品槽的实时同步范围按 <c>PlayerInventoryMath.SyncSlotsFor</c> 决定：普通背包（26 槽）
    ///     **全部槽位实时同步**，创造仓库仍只同步前 <c>PlayerInventoryMath.MaxSyncSlots</c> 槽
    ///     （历史网络尖峰上限）。面板绑定化身实体的 ComponentMiner.Inventory（见
    ///     <c>UiMirrorEngine</c> 的说明）。
    ///   · 对话框：只镜像**能从类名重建的引擎对话框**（GameMenuDialog / KeyboardHelpDialog /
    ///     GamepadHelpDialog）；带参数的消息框 / 文本输入框 / 列表选择 / 编辑框**不支持**，
    ///     按类型计数（没有类名就重建不出来，绝不摆假框）。
    ///   · 全屏 Screen：**不支持**（ScreensManager 全局单实例，Source: ScreensManager.cs:46,
    ///     83-113；在单个 GameWidget 里再造一个 Screen 会执行它的 Update/Leave 副作用）。
    ///     游戏屏（"GameScreen"）本身无遮罩 = 正常镜像，计数 uiMirrorScreen。
    ///   · 聊天输入行：**不支持**（mod 自己的 ChatOverlay 是每个 GameWidget 一份、由键盘/触摸
    ///     直接驱动输入；在镜像视图里替客户端开输入行会变成主机侧输入）。字段仍然上报并计数。
    ///   · 本机视角模式（<c>camera_mode</c> = 18）：**转发给相机所有者**（
    ///     <see cref="IViewCameraModePort"/>，实现 = <c>Engine/MirrorCharacterEngine.cs</c>）。
    ///     客户端 V 键 / 视角按钮在 Fpp → Tpp → Orbit → Fixed 之间循环
    ///     （Source: Survivalcraft/Game/ComponentGui.cs:645-670），按 ActiveCamera 的实际类型上报
    ///     1..4（Fpp / Tpp / Orbit / Fixed）；其余相机（Death / Intro / Loading）上报 0 =
    ///     不改变，主机保留最后已知模式。未知值（>4）整条快照拒绝（uiStateMalformed）；
    ///     第三屏死亡时仍以 DeathCamera 优先，复活后回到客户端最后上报的模式。端口缺失 / 被拒绝
    ///     都显式计数 + 10 秒限流日志（绝不静默丢掉）。
    ///
    /// 生命周期：连接建立 / 断开、客户端加入 / 离开、离开房间、世界卸载、模块下线都会
    /// 清空镜像（客户端重置发送快照与 revision，主机 ResetAll/ResetClient 并恢复视图原值）。
    ///
    /// 计数（10 秒限流日志）：uiStateSent/Recv/Applied/Stale/Unsupported、
    /// uiMirrorScreen/Dialog/Modal/Closed、uiTouchMirror/KeyboardMirror、hudHidden、
    /// uiStateMalformed/SendFailures/EncodeFailures/ApplyFailures/DirectionErrors/
    /// CaptureFailures、uiMirrorNoView/Retries、uiInputReasserts、
    /// uiViewCameraModeForwarded/Rejected/PortMissing/Unknown。
    /// </summary>
    public sealed class UiStateSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;
        /// <summary>Re-apply cached snapshots when the view appears / the engine reset the view.</summary>
        private const int RetryIntervalMs = 1000;
        /// <summary>Bounded per-host table (one entry per connected client; engine max is far lower).</summary>
        private const int MaxTrackedClients = 64;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IClientUiStateSource m_source;
        private readonly IUiMirrorPort m_mirror;
        private readonly Dictionary<int, HostEntry> m_hostStates = new Dictionary<int, HostEntry>();
        private readonly Dictionary<int, uint> m_hostRevisions = new Dictionary<int, uint>();
        private readonly Dictionary<string, long> m_unsupportedByType =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<int> m_retryScratch = new List<int>(8);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private IUiMirrorTickPort m_presentation;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private IViewCameraModePort m_viewCameraMode;
        private bool m_attached;
        private bool m_wasConnected;
        private bool m_hostResetForMissingWorld;

        // ---------------------------------------------------------------- 客户端发送侧
        private bool m_hasLastSent;
        private ClientUiStateData m_lastSent;
        private long m_lastSentMs;
        private uint m_nextRevision;

        // ---------------------------------------------------------------- 时间片
        private long m_lastSummaryMs;
        private long m_lastRetryMs;

        // ---------------------------------------------------------------- 计数（任务口径）
        private long m_sent;
        private long m_recv;
        private long m_applied;
        private long m_stale;
        private long m_unsupported;
        private long m_screenMirror;
        private long m_dialogMirror;
        private long m_modalMirror;
        private long m_closed;
        private long m_touchMirror;
        private long m_keyboardMirror;
        private long m_hudHidden;

        // ---------------------------------------------------------------- 其它明确失败 / 诊断
        private long m_heartbeats;
        private long m_changes;
        private long m_malformed;
        private long m_sendFailures;
        private long m_encodeFailures;
        private long m_applyFailures;
        private long m_directionErrors;
        private long m_captureFailures;
        private long m_noView;
        private long m_retries;
        private long m_inputReasserts;
        private long m_presentFailures;
        private long m_clientLimitHits;
        private long m_hostResets;
        private long m_summaryRounds;

        // ---------------------------------------------------------------- 第三屏视角模式（字段 18）
        private long m_cameraModeForwarded;
        private long m_cameraModeRejected;
        private long m_cameraModePortMissing;
        private long m_cameraModeUnknown;

        public UiStateSyncModule(IClientUiStateSource source, IUiMirrorPort mirror)
        {
            m_source = source ?? throw new ArgumentNullException(nameof(source));
            m_mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
            m_descriptor = new ModuleDescriptor("ui-state",
                "客户端界面层镜像（输入形态 / HUD / 打开的界面 → 主机分屏视图）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "ui.mirror" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "ui-state: " + Summary() + " · " + m_mirror.Describe();

        public string DescribeUnsupported()
        {
            if (m_unsupportedByType.Count == 0) return "none";
            var text = new StringBuilder();
            foreach (KeyValuePair<string, long> pair in m_unsupportedByType)
            {
                if (text.Length > 0) text.Append(',');
                text.Append(pair.Key).Append('=').Append(pair.Value);
                if (text.Length > 240) break;
            }
            return text.ToString();
        }

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ UI 镜像不启用（明确失败，不假装同步）");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "UI 镜像不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到 CLIENT_UI_STATE");
            }
            m_presentation = m_mirror as IUiMirrorTickPort;
            if (m_presentation == null)
            {
                Log("ui-state: 镜像端口没有每帧呈现能力（IUiMirrorTickPort）→ 第三屏的左侧按钮 / " +
                    "冒泡消息 / 覆盖文字不会跟随客户端（输入形态 / HUD / 打开的界面仍然镜像；" +
                    "明确原因，不是静默跳过）");
            }
            m_transport.ClientJoined += OnClientJoined;
            m_transport.ClientLeft += OnClientLeft;
            ResetClientSendState();
            ResetHostState();
            m_viewCameraMode = null;
            m_wasConnected = false;
            m_attached = true;
            Log("UI 镜像就绪：客户端 = 只读采集界面层（变化才发 + " +
                UiStatePolicy.HeartbeatMs + " ms 心跳）· 主机 = 施加到相机 Target 为该客户端 " +
                "ComponentPlayer 的 GameWidget（自动客户端视图 / H3 replace 模式）· " +
                "可镜像：输入形态 / HUD 显隐 / 背包·创造背包·衣物·容器·熔炉·工作台·分配器面板 / " +
                "GameMenu·键盘帮助·手柄帮助对话框 / 第三屏视角模式（camera_mode 18 → " +
                "IViewCameraModePort）· 不支持（显式计数 uiMirrorUnsupported:<type>）：" +
                "全屏 Screen、带参数的对话框、聊天输入行");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.ClientUiState, P.ClientUiState.Parser,
                new DelegateMessageHandler<P.ClientUiState>(OnClientUiState), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            try
            {
                if (m_transport != null)
                {
                    m_transport.ClientJoined -= OnClientJoined;
                    m_transport.ClientLeft -= OnClientLeft;
                }
            }
            catch (Exception) { }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            try { m_mirror.ResetAll("module-detach:" + reason); }
            catch (Exception e) { LogProblem("detach-reset", "模块下线时清空镜像失败：" + Describe(e)); }
            ResetHostState();
            ResetClientSendState();
            m_viewCameraMode = null;
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("UI 镜像下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            bool connected = m_transport != null && m_transport.IsConnected;
            bool hosting = m_control != null && m_control.IsHosting;
            bool inWorld = tick.IsConnected;
            long nowMs = (long)(tick.Now * 1000.0);

            if (connected && !m_wasConnected)
            {
                ResetClientSendState();
                ResetHostState();
                m_hostResetForMissingWorld = false;
                if (hosting)
                {
                    try { m_mirror.ResetAll("reconnected"); }
                    catch (Exception e) { LogProblem("reset-reconnect", "重连清空镜像失败：" + Describe(e)); }
                    m_hostResets++;
                    Log("ui-state: 连接已建立（主机）→ 待客户端重新上报（每端首条快照都会重新施加）");
                }
                else
                {
                    Log("ui-state: 连接已建立（客户端）→ 下一帧发送首条界面层快照");
                }
            }
            else if (!connected && m_wasConnected)
            {
                ResetClientSendState();
                ResetHostState();
                m_hostResetForMissingWorld = false;
                try { m_mirror.ResetAll("disconnected"); }
                catch (Exception e) { LogProblem("reset-disconnect", "断开清空镜像失败：" + Describe(e)); }
                m_hostResets++;
                Log("ui-state: 连接断开 → 客户端发送快照 / 主机镜像状态已清空（重连后重建）");
            }
            m_wasConnected = connected;

            if (hosting)
            {
                if (!inWorld)
                {
                    // 世界卸载：GameWidget 树随 Project 释放；仍然要摘掉我们挂上去的面板并恢复
                    // 视图原值（世界已卸载时引擎适配器只清引用，不触碰已释放对象）。
                    if (!m_hostResetForMissingWorld && m_hostStates.Count > 0)
                    {
                        m_hostResetForMissingWorld = true;
                        ResetHostState();
                        try { m_mirror.ResetAll("world-unloaded"); }
                        catch (Exception e) { LogProblem("reset-world", "世界卸载清空镜像失败：" + Describe(e)); }
                        m_hostResets++;
                        Log("ui-state: 世界已卸载 → 镜像状态已清空（重进世界后按客户端上报重建）");
                    }
                }
                else
                {
                    m_hostResetForMissingWorld = false;
                    if (connected)
                    {
                        PumpHost(nowMs);
                        // 每帧呈现：引擎本帧已经重写过按钮 IsChecked / 清空覆盖文字，
                        // 必须在 Draw 之前补写（Frame.Update；Program.cs:146-147）。
                        PresentHost();
                    }
                }
            }
            else
            {
                // 房间结束 / 本端不是主机：不允许留下任何镜像。
                if (m_hostStates.Count > 0)
                {
                    ResetHostState();
                    try { m_mirror.ResetAll("not-hosting"); }
                    catch (Exception e) { LogProblem("reset-room-end", "离开房间清空镜像失败：" + Describe(e)); }
                    m_hostResets++;
                    Log("ui-state: 本端已不是主机（离开房间 / 房间结束）→ 镜像状态已清空");
                }
                if (inWorld && connected)
                {
                    PumpClient(nowMs);
                }
                else
                {
                    // 离开世界 / 断线：丢弃发送基线，重进世界后第一条快照必须重发完整状态。
                    ResetClientSendState();
                }
            }

            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                m_summaryRounds++;
                Log(Summary() + " · unsupportedTypes=" + DescribeUnsupported() +
                    " · " + m_mirror.Describe());
            }
        }

        /// <summary>
        /// 主机每帧表现施加（按钮 / 冒泡消息 / 覆盖文字）。异常显式计数 + 10 s 限流日志，
        /// 绝不让一次表现失败静默地停掉第三屏跟随。
        /// </summary>
        private void PresentHost()
        {
            if (m_presentation == null) return;
            try
            {
                m_presentation.Present();
            }
            catch (Exception e)
            {
                m_presentFailures++;
                LogProblem("present",
                    "第三屏 HUD 每帧呈现失败（按钮 / 冒泡消息 / 覆盖文字可能停更）：" + Describe(e) +
                    "（累计 uiMirrorPresentFailures=" + m_presentFailures + "）");
            }
        }

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            m_hostStates.Remove(clientId);
            m_hostRevisions.Remove(clientId);
            try { m_mirror.ResetClient(clientId, rejoin ? "client-rejoined" : "client-joined"); }
            catch (Exception e) { LogProblem("reset-join:" + clientId, "客户端加入时清空镜像失败：" + Describe(e)); }
            Log("ui-state: clientId=" + clientId + "（" + name + "）" +
                (rejoin ? "重连" : "加入") + " → 清掉它的已应用状态（下一条快照重新施加）");
        }

        private void OnClientLeft(int clientId, string reason)
        {
            m_hostStates.Remove(clientId);
            m_hostRevisions.Remove(clientId);
            try { m_mirror.ResetClient(clientId, "client-left:" + reason); }
            catch (Exception e) { LogProblem("reset-leave:" + clientId, "客户端离开时清空镜像失败：" + Describe(e)); }
            Log("ui-state: clientId=" + clientId + " 离开（" + reason + "）→ 清掉它的界面镜像");
        }

        // ================================================================= 客户端：变化驱动 + 心跳

        private void PumpClient(long nowMs)
        {
            if (!m_source.TryCapture(out ClientUiStateData data, out string reason))
            {
                m_captureFailures++;
                LogProblem("capture:" + reason,
                    "客户端界面层采集失败（" + reason + "）→ 本帧不发送，保留上一条快照" +
                    "（累计 " + m_captureFailures + "；不猜状态）");
                return;
            }
            if (!UiStateDiff.ShouldSend(m_hasLastSent, in m_lastSent, m_lastSentMs, in data,
                    nowMs, out UiSendReason sendReason))
            {
                return;
            }
            uint revision = m_nextRevision + 1;
            if (revision == 0) revision = 1;
            if (!UiStateCodec.TryEncode(revision, in data, out P.ClientUiState message,
                    out reason))
            {
                m_encodeFailures++;
                LogProblem("encode:" + reason,
                    "CLIENT_UI_STATE 编码失败（" + reason + "）：" + data.Describe() +
                    " → 本帧不发送（累计 " + m_encodeFailures + "）");
                return;
            }
            if (!m_transport.TrySend(MessageType.ClientUiState, message.ToByteArray()))
            {
                m_sendFailures++;
                LogProblem("send",
                    "CLIENT_UI_STATE 发送失败（" + data.Describe() + "）→ 下一帧重试" +
                    "（累计 " + m_sendFailures + "）");
                return;
            }
            m_nextRevision = revision;
            m_hasLastSent = true;
            m_lastSent = data;
            m_lastSentMs = nowMs;
            m_sent++;
            if (sendReason == UiSendReason.First) m_changes++;
            else if (sendReason == UiSendReason.Heartbeat) m_heartbeats++;
            else m_changes++;
        }

        // ================================================================= 主机：施加 + 补施加

        private void PumpHost(long nowMs)
        {
            if (nowMs - m_lastRetryMs < RetryIntervalMs) return;
            m_lastRetryMs = nowMs;
            if (m_hostStates.Count == 0) return;
            m_retryScratch.Clear();
            foreach (KeyValuePair<int, HostEntry> pair in m_hostStates) m_retryScratch.Add(pair.Key);
            for (int i = 0; i < m_retryScratch.Count; i++)
            {
                int clientId = m_retryScratch[i];
                if (!m_hostStates.TryGetValue(clientId, out HostEntry entry)) continue;
                bool applied;
                try { applied = m_mirror.IsApplied(clientId); }
                catch (Exception e)
                {
                    m_applyFailures++;
                    LogProblem("is-applied:" + clientId, "查询镜像视图状态失败：" + Describe(e));
                    continue;
                }
                if (applied) continue;
                m_retries++;
                ClientUiStateData state = entry.State;
                Apply(clientId, in state, entry.Revision);
            }
        }

        private void OnClientUiState(MessageContext context, P.ClientUiState message)
        {
            m_recv++;
            if (m_control == null || !m_control.IsHosting)
            {
                m_directionErrors++;
                LogProblem("event-on-client",
                    "客户端收到了 CLIENT_UI_STATE（方向错误，来源 clientId=" + context.ClientId +
                    "）→ 丢弃（该消息只允许客户端 → 主机）");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0)
            {
                m_malformed++;
                LogProblem("bad-source",
                    "CLIENT_UI_STATE 来源 clientId=" + clientId + " 非法 → 整条丢弃");
                return;
            }
            if (!UiStateCodec.TryDecode(message, out ClientUiStateData data, out uint revision,
                    out string reason))
            {
                m_malformed++;
                LogProblem("malformed:" + reason,
                    "CLIENT_UI_STATE 载荷非法（" + reason + "，clientId=" + clientId +
                    "）→ 整条丢弃（累计 " + m_malformed + "）");
                return;
            }
            if (m_hostRevisions.TryGetValue(clientId, out uint lastRevision) &&
                revision <= lastRevision)
            {
                m_stale++;
                LogProblem("stale:" + clientId,
                    "CLIENT_UI_STATE revision=" + revision + " 不大于已应用 " + lastRevision +
                    "（clientId=" + clientId + "）→ 丢弃（重复 / 越序 / 重放）");
                return;
            }
            if (!m_hostRevisions.ContainsKey(clientId) && m_hostRevisions.Count >= MaxTrackedClients)
            {
                m_clientLimitHits++;
                LogProblem("client-limit",
                    "主机 UI 镜像跟踪表已满 " + MaxTrackedClients + " 端 → 拒绝 clientId=" + clientId +
                    "（其它端不受影响）");
                return;
            }
            m_hostRevisions[clientId] = revision;
            m_hostStates[clientId] = new HostEntry(data, revision);
            Apply(clientId, in data, revision);
        }

        private bool Apply(int clientId, in ClientUiStateData data, uint revision)
        {
            // 第三屏视角模式先交给相机所有者（与 HUD 镜像是否成功无关：H3 replace 的相机
            // 绑定和 HUD 面板是两条独立的引擎路径，视图缺失不应该挡住宿主切相机）。
            ForwardClientCameraMode(clientId, in data);
            UiMirrorOutcome outcome;
            string reason;
            bool ok;
            try
            {
                ok = m_mirror.TryApply(clientId, in data, out outcome, out reason);
            }
            catch (Exception e)
            {
                m_applyFailures++;
                LogProblem("apply-throw:" + clientId,
                    "施加界面层状态时异常（clientId=" + clientId + " rev=" + revision +
                    "）：" + Describe(e));
                return false;
            }
            if ((outcome & UiMirrorOutcome.ViewMissing) != 0)
            {
                m_noView++;
                LogProblem("no-view:" + clientId,
                    "clientId=" + clientId + " 还没有跟随它的分屏视图（" +
                    (reason ?? "view-missing") + "）→ 状态已缓存，视图出现后自动补施加" +
                    "（累计 " + m_noView + "）");
                return false;
            }
            if (!ok)
            {
                m_applyFailures++;
                LogProblem("apply:" + clientId,
                    "施加界面层状态失败（clientId=" + clientId + " rev=" + revision + "）：" +
                    (reason ?? "unknown") + " · " + data.Describe() +
                    "（累计 " + m_applyFailures + "）");
                return false;
            }
            m_applied++;
            if ((outcome & (UiMirrorOutcome.Input | UiMirrorOutcome.InputReasserted)) != 0)
            {
                if (data.InputKind == UiInputKind.Touch) m_touchMirror++;
                else if (data.InputKind == UiInputKind.KeyboardMouse ||
                    data.InputKind == UiInputKind.Gamepad) m_keyboardMirror++;
            }
            if ((outcome & UiMirrorOutcome.InputReasserted) != 0) m_inputReasserts++;
            if ((outcome & UiMirrorOutcome.Hud) != 0 && !data.HudVisible) m_hudHidden++;
            if ((outcome & UiMirrorOutcome.Screen) != 0) m_screenMirror++;
            if ((outcome & UiMirrorOutcome.DialogOpened) != 0) m_dialogMirror++;
            if ((outcome & UiMirrorOutcome.ModalOpened) != 0) m_modalMirror++;
            if ((outcome & (UiMirrorOutcome.DialogClosed | UiMirrorOutcome.ModalClosed)) != 0)
                m_closed++;
            if ((outcome & UiMirrorOutcome.UnsupportedScreen) != 0)
                Unsupported("screen", data.Screen, clientId);
            if ((outcome & UiMirrorOutcome.UnsupportedDialog) != 0)
                Unsupported("dialog", data.DialogType, clientId);
            if ((outcome & UiMirrorOutcome.UnsupportedModal) != 0)
                Unsupported("modal", data.ModalType, clientId);
            if ((outcome & UiMirrorOutcome.UnsupportedChat) != 0)
                Unsupported("chat", "ChatInput", clientId);
            if ((outcome & UiMirrorOutcome.InputUnknown) != 0)
                Unsupported("input", "unknown", clientId);
            if ((outcome & UiMirrorOutcome.UnsupportedInput) != 0)
                Unsupported("input", "IsControlledByTouch", clientId);
            return true;
        }

        /// <summary>
        /// 把客户端上报的视角模式交给第三分屏的相机所有者（<see cref="IViewCameraModePort"/>，
        /// 实现 = <c>Engine/MirrorCharacterEngine.cs</c>）。
        ///
        /// 0（<see cref="UiCameraMode.Unknown"/>）= **不改变**：客户端停在死亡相机 / 引擎屏上，
        /// 不转发（主机保留最后已知模式，复活后按它恢复）。1..4（Fpp / Tpp / Orbit / Fixed）
        /// 原样转发，由相机所有者选相机。
        /// 端口缺失 / 被拒绝都显式计数 + 10 秒限流日志 —— 绝不静默当成已同步。
        /// </summary>
        private void ForwardClientCameraMode(int clientId, in ClientUiStateData data)
        {
            if (data.CameraMode == UiCameraMode.Unknown)
            {
                m_cameraModeUnknown++;
                return;
            }
            if (m_viewCameraMode == null &&
                (m_context == null || !m_context.TryGetPort(out m_viewCameraMode) ||
                 m_viewCameraMode == null))
            {
                m_cameraModePortMissing++;
                LogProblem("camera-mode-port",
                    "第三屏视角模式无法施加：没有 IViewCameraModePort（组合根未注册）→ clientId=" +
                    clientId + " camera_mode=" + (int)data.CameraMode + " 未生效（累计 " +
                    m_cameraModePortMissing + "；不静默当成已同步）");
                return;
            }
            try
            {
                if (!m_viewCameraMode.TrySetClientCameraMode(clientId, data.CameraMode,
                        out string reason))
                {
                    m_cameraModeRejected++;
                    LogProblem("camera-mode:" + clientId,
                        "第三屏视角模式被拒绝（clientId=" + clientId + " camera_mode=" +
                        (int)data.CameraMode + "）：" + (reason ?? "原因未知") + "（累计 " +
                        m_cameraModeRejected + "；主机相机不变）");
                    return;
                }
                m_cameraModeForwarded++;
            }
            catch (Exception e)
            {
                m_cameraModeRejected++;
                LogProblem("camera-mode-throw:" + clientId,
                    "转交第三屏视角模式时异常（clientId=" + clientId + " camera_mode=" +
                    (int)data.CameraMode + "）：" + Describe(e) + "（累计 " +
                    m_cameraModeRejected + "）");
            }
        }

        private void Unsupported(string kind, string typeName, int clientId)
        {
            m_unsupported++;
            string name = string.IsNullOrEmpty(typeName) ? "?" : typeName;
            string key = kind + ":" + name;
            m_unsupportedByType.TryGetValue(key, out long count);
            m_unsupportedByType[key] = count + 1;
            // 日志标记固定为 uiMirrorUnsupported:<type>（类型名在前，kind 附在后面），
            // 计数表用 kind:type 区分同名类（屏幕 / 对话框 / 面板可能同名）。
            LogProblem("unsupported:" + key,
                "uiMirrorUnsupported:" + name + "（kind=" + kind + " clientId=" + clientId +
                "）→ 该界面类型无法在单个 GameWidget 里安全镜像（原因见模块文档），" +
                "只计数不摆假界面（累计 " + (count + 1) + "）");
        }

        // ================================================================= 状态重置

        private void ResetClientSendState()
        {
            m_hasLastSent = false;
            m_lastSent = default;
            m_lastSentMs = 0;
            m_nextRevision = 0;
        }

        private void ResetHostState()
        {
            m_hostStates.Clear();
            m_hostRevisions.Clear();
            m_lastRetryMs = 0;
        }

        // ================================================================= 日志 / 摘要

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        private static string Describe(Exception e) =>
            e == null ? "null" : e.GetType().Name + " " + e.Message;

        private string Summary() =>
            "uiStateSent=" + m_sent +
            " uiStateRecv=" + m_recv +
            " uiStateApplied=" + m_applied +
            " uiStateStale=" + m_stale +
            " uiStateUnsupported=" + m_unsupported +
            " uiStateChanges=" + m_changes +
            " uiStateHeartbeats=" + m_heartbeats +
            " uiStateMalformed=" + m_malformed +
            " uiStateSendFailures=" + m_sendFailures +
            " uiStateEncodeFailures=" + m_encodeFailures +
            " uiStateApplyFailures=" + m_applyFailures +
            " uiStateDirectionErrors=" + m_directionErrors +
            " uiStateCaptureFailures=" + m_captureFailures +
            " uiStateClients=" + m_hostStates.Count +
            " uiStateClientLimitHits=" + m_clientLimitHits +
            " uiMirrorScreen=" + m_screenMirror +
            " uiMirrorDialog=" + m_dialogMirror +
            " uiMirrorModal=" + m_modalMirror +
            " uiMirrorClosed=" + m_closed +
            " uiTouchMirror=" + m_touchMirror +
            " uiKeyboardMirror=" + m_keyboardMirror +
            " hudHidden=" + m_hudHidden +
            " uiInputReasserts=" + m_inputReasserts +
            " uiMirrorNoView=" + m_noView +
            " uiMirrorRetries=" + m_retries +
            " uiMirrorPresentFailures=" + m_presentFailures +
            " uiMirrorHostResets=" + m_hostResets +
            " uiViewCameraModeForwarded=" + m_cameraModeForwarded +
            " uiViewCameraModeRejected=" + m_cameraModeRejected +
            " uiViewCameraModePortMissing=" + m_cameraModePortMissing +
            " uiViewCameraModeUnknown=" + m_cameraModeUnknown +
            " uiMirrorSummaryRounds=" + m_summaryRounds;

        private readonly struct HostEntry
        {
            public HostEntry(ClientUiStateData state, uint revision)
            {
                State = state;
                Revision = revision;
            }

            public ClientUiStateData State { get; }
            public uint Revision { get; }
        }
    }
}

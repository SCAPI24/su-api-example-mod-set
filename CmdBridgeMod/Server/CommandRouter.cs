using Engine;
using Engine.Graphics;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// 命令分发。obs.* 只读；act.* 走输入注入白名单。
    /// 所有触碰游戏对象的操作都在游戏线程（下一帧帧首）执行。
    ///
    /// ==================================================================================
    /// 铁律（用户 2026-10-17 定）：**命令行桥存在时禁止抢占真实鼠标与窗口；请在后台用虚拟输入完成。**
    /// 原因：用户明确要求；抢真实窗口/鼠标会打断用户操作，且所有操作在后台虚拟输入下都能完成。
    ///
    /// 因此本文件里：
    ///   · `focus.recover` **只回报**焦点/挂接状态（外加把策略交回 `follow`）；它不再调用
    ///     `RestoreNaturalFocus()`，也不再触碰任何窗口状态。调用方显式要求抢前台时走
    ///     `FocusPolicy.RejectRealWindowRequest` 明确报错（错误码 `forbidden_real_window_activation`）。
    ///   · `focus.auto/follow/attach/detach` 只改**引擎内部**的虚拟焦点状态（`Window.m_state`
    ///     + 软光标），不碰真实窗口/真实光标 —— 这是"在后台操作"能成立的基础，必须保留。
    ///   · 规则 1 的"点世界内 HUD 前先强制展开 HUD"在 `ui.click` / `ui.clickelement` / `act.uiclick`
    ///     的点击路径里自动生效（`UiService.EnsureHudTargetRevealed`），不需要额外参数。
    /// 完整禁令清单见 `Server/FocusPolicy.cs` 类头部的铁律块。
    /// ==================================================================================
    /// </summary>
    internal sealed class CommandRouter
    {
        /// <summary>
        /// 自报版本：`cmd.status` 与 `CmdBridge.runtime.json` 的 `modVersion` 都由它来。
        ///
        /// ⚠️ **必须与 `ModInfo.xml` 的 `&lt;Version&gt;` 同步**：漏同步就会出现
        /// "装的是 1.1.20、状态里报 1.1.15"（本文件实际踩过一次，排查时容易误判成旧包装没生效）。
        /// </summary>
        internal const string ModVersion = "1.1.38";

        /// <summary>
        /// 内建命令名（`cmd.list` 用）。加命令时**必须同步这里**：
        /// 它是 CLI 帮助与"这个实例支持哪些命令"的唯一清单（扩展命令从注册表读）。
        /// </summary>
        internal static readonly string[] BuiltInCommands =
        {
            "ping", "status", "cmd.list", "obs.selftest",
            "obs.snapshot", "obs.ui", "obs.player", "obs.input", "obs.aim", "obs.events",
            // 枚举**每一个**玩家角色：PlayersData（主机分屏 H1/H2/H3，含未生成身体的）
            // + ComponentPlayers（当前 SuMultiplayer 只把远端 avatar 留在这里），去重合并。
            // `players.all` 是主名，`obs.players` 是只读观察面的别名。
            "players.all", "obs.players",
            "obs.world.blocks", "obs.world.entities", "obs.world.time", "obs.messages", "obs.chat",
            "obs.dialogs",
            "obs.waitfor", "ui.elements", "ui.reachability",
            // 调试/验收用的"凭空塞一条聊天"（只进观察层队列，不发网络消息、不碰游戏状态）
            "dev.chat.inject",
            // SuMultiplayer 聊天注入（反射 SuMultiplayerMod.ChatAutomation）：
            // 与手打同一条 ChatModule 净化 / 限速 / 网络链；没装 SuMultiplayer 时明确 chat_unavailable。
            "chat.open", "chat.text", "chat.submit", "chat.send", "chat.cancel",
            "act.look", "act.lookdelta", "act.lookat", "act.key", "act.hold", "act.chord",
            "act.mouse", "act.wheel", "act.uiclick", "act.text",
            // 对话框文本与接受/取消：文本走 `TextBoxWidget.Text` setter（触发 TextChanged，
            // 对话框内部状态同步）；接受 = 在 OK 按钮上合成一次引擎自己的 Tap+Click，
            // 让 `Update` 里的 `IsClicked` 提交分支跑起来（`Dismiss` 本身不提交数据）。
            "act.settext", "act.accept", "act.dialog",
            "act.releaseall",
            // 玩家交互输入（右键 = 引擎的"使用/交互/放置/瞄准"）：act.use / act.interact /
            // act.place 是同一个 `PlayerInput.Interact` 边沿，act.aim 是按住 `PlayerInput.Aim`。
            // 详见 `Server/PlayerInteractionInjector.cs` 类头。
            "act.use", "act.interact", "act.place", "act.aim",
            // 关掉挡着世界输入的界面（对话框按接受按钮 / 模态面板置 null / MoreContents 收起）。
            // 它也是所有世界内输入命令注入前的自动检查（见 `Server/UiBlocker.cs`）。
            "act.closeui",
            "act.enter",
            "ui.locate", "ui.clickelement", "ui.marker",
            "ui.session.begin", "ui.session.end", "ui.session.status",
            "ui.cursor", "ui.press", "ui.release", "ui.move", "ui.click",
            "focus.status", "focus.auto", "focus.follow", "focus.attach", "focus.detach", "focus.recover",
            "focus.merge", "focus.lookhold", "look.owner", "hotkey.status",
            "jump.status", "jump.buffer"
        };

        // 条件等待： 0ms 轮询一次（远快于人类的反应，也远慢于每帧，避免给游戏线程添压力）。
        private const int WaitPollIntervalMs = 40;
        private const int WaitMaxTimeoutMs = 120000;

        /// <summary>
        /// 键盘脉冲命令（`act.key` / `act.chord`）的 holdMs 默认值：
        /// **一帧量级**（60fps 一帧 ≈ 16.7ms），固定值，不随时钟抖动。
        ///
        /// 为什么默认不能是 0：press / release 是两次独立的 `Dispatcher.Dispatch`
        /// （`GameThreadInvoker.cs:56-66`），跨不过帧边界时会在**同一次** `Dispatcher.BeforeFrame`
        /// 批次里连续执行（`Engine/Engine/Dispatcher.cs:79-105`），于是整个帧体里
        /// `IsKeyDown` 恒为 false —— 只有 `IsKeyDownOnce` 活下来
        /// （移动类消费者读的就是 `IsKeyDown`：`Survivalcraft/Game/ComponentInput.cs:172-177`）。
        /// 40ms 足够让帧体至少看到一次按住，又短到不改变"轻点一下"的手感。
        /// **调用方显式传 holdMs 时原样尊重**（含显式 0）。
        /// `act.mouse` / `act.uiclick` 不走这里，它们的默认值保持 0。
        /// </summary>
        private const int DefaultKeyPulseHoldMs = InputInjector.DefaultPulseHoldMs;

        private readonly CmdBridgeConfig m_config;
        private readonly GameThreadInvoker m_invoker;
        private readonly InputInjector m_injector;
        private readonly EventRecorder m_recorder;
        private readonly JumpAssist m_jumpAssist;

        /// <summary>
        /// 交互输入注入（`act.use` / `act.interact` / `act.place` / `act.aim`）。
        /// 与 `InputInjector` 共用同一份鼠标数组与帧首调度（`RunOnFrame`），所以
        /// `act.releaseall` 的数组清零天然覆盖它 —— 但本类自己的瞄准簿记要另外清
        /// （见 `act.releaseall` 分支，见 `PlayerInteractionInjector.ResetAimSession`）。
        /// </summary>
        private readonly PlayerInteractionInjector m_playerActions;

        // ------------------------------------------------ 阻塞界面自动关闭的累计计数
        // 只用于 `status` 里的累计读数（"这个实例一共替自动化关过多少界面"）。
        // 单次命令的计数在 `UiBlocker.Report` 里，随回包返回；这里是**实例**字段，不是静态状态。
        private int m_uiDismissedDialogs;
        private int m_uiDismissedModalPanels;
        private int m_uiDismissedHudPanels;

        public CommandRouter(
            CmdBridgeConfig config,
            GameThreadInvoker invoker,
            InputInjector injector,
            EventRecorder recorder,
            JumpAssist jumpAssist)
        {
            m_config = config;
            m_invoker = invoker;
            m_injector = injector;
            m_recorder = recorder;
            m_jumpAssist = jumpAssist;
            m_playerActions = new PlayerInteractionInjector(injector);
        }

        /// <summary>
        /// 命令总入口：先做**注入前 UI 检查**（世界内输入命令才做），再执行命令，最后把
        /// "关掉了哪些挡路的界面"合并进回包（`dismissedUi` / `dismissedCount` / `dismissedByKind`）。
        ///
        /// 为什么放在这一层：所有会往世界灌输入的命令（`act.move` / `act.lookat` / `act.key` /
        /// `act.dig` / `act.use` / `act.interact` / `act.place` / `act.aim` …）的注入点都在下面的
        /// switch 里，但注入点是**逐命令**的、返回值也各不相同。在这一层统一"先关界面 → 再执行 →
        /// 回包带上结果"，就不会漏掉某条命令，也不需要把每条命令的实现拆开。
        /// 规则与引擎依据见 `Server/UiBlocker.cs` 类头。
        /// </summary>
        public object Execute(BridgeRequest request)
        {
            UiBlocker.Report uiReport = PrepareForWorldInput(request);
            object result = ExecuteCore(request);
            return AttachUiReport(result, uiReport);
        }

        /// <summary>真正的命令分发（原 `Execute` 的 switch 原样保留）。</summary>
        private object ExecuteCore(BridgeRequest request)
        {
            switch (request.Command)
            {
                // ---------------------------------------------------------- 只读
                case "ping":
                    // ping 完全在服务器线程回答：启动瞬间也能用，正好作为"游戏是否可接受命令"的探针。
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["pong"] = true,
                        ["instanceId"] = m_config.InstanceId,
                        ["modVersion"] = ModVersion,
                        ["dispatcherReady"] = GameThreadInvoker.IsDispatcherReady()
                    };
                case "status":
                    return OnGameThread(BuildStatus);
                case "obs.snapshot":
                    return OnGameThread(() => BuildSnapshot(request));
                case "obs.ui":
                case "ui.elements":
                    return OnGameThread(() => UiInspector.Describe(
                        m_config,
                        request.GetBoolean("all", false),
                        request.GetInteger("maxElements", m_config.MaxElements),
                        request.GetString("filter", null),
                        ReadGameWidget(request)));
                case "obs.player":
                    return OnGameThread(PlayerObserver.Describe);
                // 每个玩家角色一条记录（主机本地分屏 + 远端 avatar）。
                // PlayersData 与 ComponentPlayers 合并去重：当前 SuMultiplayer 把远端 avatar
                // 从 PlayersData 移除、只保留 ComponentPlayer，只看一个集合都会漏人。
                // 参数：index=-1 全部 / max=0 全部 / slots=每个背包最多返回几个非空格子。
                case "players.all":
                case "obs.players":
                    return OnGameThread(() => AllPlayersObserver.Describe(
                        request.GetInteger("index", -1),
                        request.GetInteger("max", 0),
                        request.GetInteger("slots", 16)));
                case "obs.input":
                    return OnGameThread(PlayerObserver.DescribeRawInput);
                case "obs.aim":
                    return OnGameThread(() => AimObserver.Describe(request.GetFloat("maxDistance", 8f)));
                case "obs.events":
                    return m_recorder.Read(
                        request.GetInteger("sinceSeq", 0),
                        request.GetInteger("max", 200));
                case "obs.world.blocks":
                    return OnGameThread(() => DescribeBlocks(request));
                case "obs.world.entities":
                    return OnGameThread(() =>
                    {
                        Vector3? center = request.TryGetInteger("x", out int ex) &&
                            request.TryGetInteger("y", out int ey) &&
                            request.TryGetInteger("z", out int ez)
                                ? new Vector3(ex + 0.5f, ey + 0.5f, ez + 0.5f)
                                : (Vector3?)null;
                        return WorldObserver.DescribeEntities(
                            request.GetFloat("radius", 64f),
                            request.GetInteger("max", 64),
                            center);
                    });
                case "obs.world.time":
                    return OnGameThread(WorldObserver.DescribeTime);
                case "obs.world.fire":
                    return OnGameThread(WorldObserver.DescribeFire);
                case "obs.messages":
                    return OnGameThread(MessageObserver.Describe);
                case "obs.chat":
                    // 聊天（只读）：把"玩家可见的小提示"里属于聊天的那些结构化出来。
                    // ScMP 的聊天本来就是 `ComponentGui.DisplaySmallMessage("名字: 正文")`，
                    // 所以这里**不需要**碰 ScMultiplayer（见 `ChatObserver` 的注释）。
                    return OnGameThread(() => ChatObserver.Describe(
                        request.GetInteger("max", 16)));
                case "dev.chat.inject":
                    // ⚠️ **调试/验收专用**（与 `ai.laya.ask` 同类）：凭空塞一条聊天行，
                    // 好让"监听聊天 → 走过去"这条链能被反复、确定地验证。
                    // 它只写观察层队列：不发网络消息、不改游戏状态、不产生输入。
                    // 命令行里 sender 带空格时要加引号。
                    return OnGameThread(() =>
                    {
                        ChatObserver.Line injected = ChatObserver.InjectLine(
                            request.GetString("sender", "Tester"),
                            request.GetString("text", null),
                            request.GetBoolean("self", false),
                            request.GetBoolean("system", false));
                        if (injected == null)
                            throw new BridgeCommandException("invalid_argument",
                                "text is required (use: dev.chat.inject text=过来 [sender=名字])");
                        return new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["seq"] = injected.Seq,
                            ["sender"] = injected.Sender,
                            ["text"] = injected.Text,
                            ["isSelf"] = injected.IsSelf,
                            ["isSystem"] = injected.IsSystem,
                            ["note"] = "injected into the chat observer only; no network message was sent"
                        };
                    });
                // ---------------------------------------------------------- 聊天注入（SuMultiplayer）
                // 这三条不是"观察层注入"：它们经反射调用 SuMultiplayer 的
                // SuMultiplayerMod.ChatAutomation，走与手打完全相同的 ChatModule 发送链
                // （净化 / 限速 / 客户端请求 / 主机校验 / 广播）。没装 SuMultiplayer 或
                // 装的是没有钩子的旧包 → chat_unavailable，不做任何替代路径。
                case "chat.open":
                    return OnGameThread(ChatOpen);
                case "chat.text":
                    return OnGameThread(() => ChatText(request));
                case "chat.submit":
                    return OnGameThread(ChatSubmit);
                case "chat.send":
                    return OnGameThread(() => ChatSend(request));
                case "chat.cancel":
                    return OnGameThread(ChatCancel);
                case "obs.dialogs":
                    return OnGameThread(DescribeDialogs);
                case "ui.reachability":
                    return OnGameThread(() => UiInspector.Reachability(m_config));
                case "obs.selftest":
                    return OnGameThread(SelfTest);
                case "obs.waitfor":
                    return WaitForConditions(request);

                // ---------------------------------------------------------- 动作（输入层）
                case "act.look":
                    return m_injector.Look(
                        request.GetFloat("yaw", 0f), request.GetFloat("pitch", 0f));
                case "act.lookdelta":
                    return m_injector.LookDelta(
                        request.GetFloat("dx", 0f), request.GetFloat("dy", 0f));
                case "act.lookat":
                    return m_injector.LookAt(
                        request.GetFloat("x", 0f),
                        request.GetFloat("y", 0f),
                        request.GetFloat("z", 0f));
                case "act.key":
                    // 未显式给 holdMs 时用一帧量级的默认值（40ms）；
                    // 显式传 holdMs=0 仍然尊重，不会偷偷替换成 40。
                    return m_injector.KeyPulse(
                        request.GetString("key", null),
                        request.GetInteger("holdMs", DefaultKeyPulseHoldMs));
                case "act.hold":
                    return m_injector.KeyHold(
                        request.GetString("key", null), request.GetBoolean("down", true));
                case "act.chord":
                    // 与 `act.key` 完全同源（R2）：press/release 是两次独立 Dispatch，
                    // holdMs=0 时会在同一次 Dispatcher.BeforeFrame 里按下又抬起，帧体看不到按下。
                    // 因此共用同一个一帧量级默认值；显式传 0 仍然尊重。
                    return m_injector.KeyChord(
                        request.GetStringArray("modifiers").ToArray(),
                        request.GetString("key", null),
                        request.GetInteger("holdMs", DefaultKeyPulseHoldMs));
                case "act.mouse":
                    // 注意：`act.mouse` 的 holdMs 默认值**保持 0 不变**（本次不在范围内）。
                    return m_injector.MouseAction(
                        request.GetString("button", "left"),
                        request.GetString("action", "click"),
                        request.GetInteger("holdMs", 0));
                case "act.wheel":
                    return m_injector.Wheel(request.GetInteger("delta", 1));
                case "act.uiclick":
                {
                    float clickX = 0f;
                    float clickY = 0f;
                    bool hasPoint = request.TryGetFloat("x", out clickX) &&
                        request.TryGetFloat("y", out clickY);
                    // 列表行：`row=3` 或 `text=世界名` —— 坐标在点击这一刻现算，
                    // 录制端因此能记下"哪一行"而不是"哪个像素"（用户要求：改了窗口大小也别点空）。
                    int rowIndex = request.GetInteger("row", -1);
                    string rowText = request.GetString("text", null);
                    // 纯选择器点击优先 direct（单帧合成 Tap+Click，写在目标控件自己的输入面），
                    // 世界内 GameWidget 层级下 CM-1 会话派生不出 Click ——
                    // `ComponentInput.UpdateInputFromMouseAndKeyboard` 每帧重写该层输入状态，
                    // GameMenuDialog 的按钮用会话点毫无反应、用 direct 立刻回到主菜单（实测）。
                    // 带坐标、列表行、或显式 `mode=input` 时仍走会话，行为不变。
                    string directSelector = request.GetString("selector", null);
                    string clickMode = request.GetString("mode", null);
                    // 用户定（2026-10-17）：`mode=input`（多帧软光标会话点击）**没有应用场景**，已删除。
                    // 它在出屏 HUD 目标上必然报 `element_occluded`（会话在游戏线程逐帧跑，无法跨帧等侧栏滑入），
                    // 而且全仓没有任何调用方使用它。这里明确报错，不静默走别的路径。
                    if (string.Equals(clickMode, "input", StringComparison.OrdinalIgnoreCase))
                        throw new BridgeCommandException("removed_click_mode",
                            "`mode=input` 已删除（无应用场景）：请用默认 direct 路径；需要逐步驱动时用 "
                            + "ui.session.begin / ui.press / ui.release / ui.move。");
                    if (!hasPoint && rowIndex < 0 && string.IsNullOrEmpty(rowText)
                        && !string.IsNullOrEmpty(directSelector)
                        && !string.Equals(clickMode, "input", StringComparison.OrdinalIgnoreCase))
                    {
                        return m_injector.Ui.Click(directSelector, "direct",
                            request.GetInteger("holdMs", 0), false,
                            UiMarker.DefaultSeconds, UiMarker.DefaultDiameterPixels,
                            ReadGameWidget(request));
                    }
                    // 走 CM-1 会话（一步一帧）：直注入的"按下+抬起"会落在同一帧，界面纹丝不动。
                    return m_injector.UiClickSession(
                        request.GetString("selector", null),
                        hasPoint, clickX, clickY, rowIndex, rowText,
                        request.GetInteger("holdMs", 0), ReadGameWidget(request));
                }
                case "act.text":
                    // 只输入到"当前获得焦点"的 TextBoxWidget；没有焦点就明确报 no_focused_textbox
                    // （旧实现把字符写进 Keyboard.m_lastChar 就返回，界面一个字都不会变）。
                    return m_injector.TypeText(request.GetString("text", string.Empty));
                case "act.settext":
                    // 用 TextBoxWidget.Text 的公开 setter 替换整个值：TextChanged 与键盘路径是
                    // 同一个事件，对话框的临时状态（内存条 16 行 / 真值表）会一起更新。
                    return m_injector.Dialogs.SetText(
                        request.GetString("selector", request.GetString("target", null)),
                        request.GetString("text", string.Empty));
                case "act.accept":
                    // 接受当前（或指定）对话框：在它的 OK 按钮上合成一次引擎自己的 Tap+Click，
                    // 让对话框 Update 的 IsClicked 分支执行"提交 + Dismiss"。
                    return m_injector.Dialogs.Accept(
                        request.GetString("dialog", request.GetString("target", null)),
                        request.GetString("button", null));
                case "act.dialog":
                {
                    // `act.dialog { action: "ok"|"cancel", dialog?, target?, button? }`
                    string dialogAction = (request.GetString("action", null) ?? string.Empty)
                        .Trim().ToLowerInvariant();
                    string dialogTarget = request.GetString("dialog", request.GetString("target", null));
                    string buttonTarget = request.GetString("button", null);
                    if (string.Equals(dialogAction, "ok", StringComparison.Ordinal)
                        || string.Equals(dialogAction, "accept", StringComparison.Ordinal))
                    {
                        return m_injector.Dialogs.Accept(dialogTarget, buttonTarget);
                    }
                    if (string.Equals(dialogAction, "cancel", StringComparison.Ordinal))
                        return m_injector.Dialogs.Cancel(dialogTarget, buttonTarget);
                    throw new BridgeCommandException(
                        "invalid_argument",
                        "act.dialog action must be 'ok' (or 'accept') or 'cancel' (got '"
                        + dialogAction + "').");
                }
                case "act.releaseall":
                case "act.releaseAll":
                    // 输入数组整体清零（含 `act.aim start` 按住的右键），本类自己的瞄准簿记也要失效，
                    // 否则下一次 `act.aim start` 会误报 `already_aiming`。
                    m_playerActions.ResetAimSession();
                    return m_injector.ReleaseAll();

                // ------------------------------------- 玩家交互输入（使用 / 交互 / 放置 / 瞄准）
                // 引擎事实（详见 `Server/PlayerInteractionInjector.cs` 类头）：
                //   · 使用 / 交互 / 放置共用 `PlayerInput.Interact`（右键**边沿**），
                //     `ComponentPlayer.Update` 按 Use → Interact → Place 的固定顺序消费（151-181）；
                //   · 瞄准是 `PlayerInput.Aim`（右键**按住**），松开 → `AimState.Completed`（219-227）。
                // holdMs 语义：`act.use/interact/place` 的 hold 只是"按住多久"（边沿动作只看按下那一帧；
                // 按久一点会同时让 `PlayerInput.Aim` 有值，和真人的右键一致）；`act.aim start --hold N`
                // 是"按住 N 毫秒后自动松开"（= 放箭）。
                case "act.use":
                    return m_playerActions.Use(request.GetInteger("holdMs", 0));
                case "act.interact":
                    return m_playerActions.Interact(request.GetInteger("holdMs", 0));
                case "act.place":
                    return m_playerActions.Place(request.GetInteger("holdMs", 0));
                case "act.aim":
                {
                    // 兼容 `action=` 与 `state=` 两种写法：CLI 用 `act.aim start|release|cancel`。
                    string aimAction = request.GetString("action", null);
                    if (string.IsNullOrEmpty(aimAction))
                        aimAction = request.GetString("state", null);
                    return m_playerActions.Aim(aimAction, request.GetInteger("holdMs", 0));
                }

                // ------------------------------------------------------ 关掉挡路的界面（新增 1.1.29）
                // 语义：**现在就**把挡着世界输入的界面收掉，并回报收掉了什么。
                //   · MessageDialog → 按它自己的接受按钮（`MessageDialog.Dismiss(Button1)`，
                //     与引擎的 `Input.Ok` 分支同一条路径；`button=2` 时走 Cancel 分支）；
                //   · TextBoxDialog → `Dismiss(null)`（引擎的 Cancel 分支，不发送内容）；
                //   · 其它 Dialog → `DialogsManager.HideDialog`（引擎关 GameMenuDialog 的同一句）；
                //   · 背包 / 创造背包 / 衣物 / 容器面板 → `ComponentGui.ModalPanelWidget = null`；
                //   · HUD `MoreContents` → `MoreButton.IsChecked = false`。
                // 什么都不挡时**明确报错**（`ui_not_blocking`），不返回"成功但什么都没做"。
                // 细则与引擎依据见 `Server/UiBlocker.cs` 类头。
                case "act.closeui":
                {
                    int messageButton = request.GetInteger("button", 1);
                    if (messageButton != 1 && messageButton != 2)
                        throw new BridgeCommandException("invalid_argument",
                            "act.closeui button must be 1 (accept, the default) or 2 (cancel).");

                    UiBlocker.Report report = (UiBlocker.Report)OnGameThread(
                        () => UiBlocker.Dismiss(messageButton));

                    if (report.Skipped == "screen_not_game")
                    {
                        // 世界还在、但当前不是游戏画面（设置/帮助/玩家列表…）：这确实挡着世界输入，
                        // 但**切屏是导航、不是关界面**，本命令不替调用方做这个决定 —— 明确报出来。
                        throw new BridgeCommandException("ui_blocking",
                            "The current screen is '" + report.SkipDetail + "', not the game screen; "
                            + "this command only closes in-world UI (dialogs / modal panels / the HUD "
                            + "MoreContents panel). Go back to the game screen first "
                            + "(act.key escape / ui.clickelement on the back button), then retry. "
                            + "Current UI: " + DescribeCurrentUiSafe());
                    }
                    if (report.Skipped == "player_not_found")
                    {
                        throw new BridgeCommandException("player_not_found",
                            "The world is loaded but no local player is available, so there is no "
                            + "player-owned UI to close. Current UI: " + DescribeCurrentUiSafe());
                    }
                    if (report.Skipped != null)
                    {
                        throw new BridgeCommandException("ui_not_blocking",
                            "There is no in-world UI to close (" + report.Skipped + "). "
                            + "Current UI: " + DescribeCurrentUiSafe());
                    }

                    if (report.WorldUnloaded)
                    {
                        // 世界被对话框 handler 卸载/重载了（ScMP 的 `CR` → `Create`）：界面确实关掉了，
                        // 但这一刻既没有稳定的界面可复查、也没有世界可注入 —— 如实回报，不假装还在原地。
                        if (report.Items.Count == 0)
                        {
                            throw new BridgeCommandException("ui_not_blocking",
                                "The world changed while checking the UI ("
                                + (report.SkipDetail ?? "world unloaded") + "), so there was nothing "
                                + "to close in a stable state. Current UI: " + DescribeCurrentUiSafe());
                        }
                        AccumulateUiDismissals(report);
                        Dictionary<string, object> changed = report.ToDictionary();
                        changed["completed"] = true;
                        changed["action"] = "act.closeui";
                        changed["messageButton"] = messageButton;
                        return changed;
                    }

                    if (report.Remaining.Count > 0)
                    {
                        throw new BridgeCommandException("ui_blocking",
                            "Could not dismiss: " + string.Join(", ", report.Remaining.ToArray())
                            + ". Current UI: " + DescribeCurrentUiSafe());
                    }

                    if (report.Items.Count == 0)
                    {
                        throw new BridgeCommandException("ui_not_blocking",
                            "There is no blocking UI to close (no dialogs, no modal panel, and the HUD "
                            + UiInspector.MoreContentsName + " panel is already collapsed). "
                            + "Current UI: " + DescribeCurrentUiSafe());
                    }

                    // 模态面板关掉后有一段收起动画（期间 ModalPanelWidget 仍非 null），
                    // 这里等它真的消失，否则下一条短脉冲命令会正好落在动画里。
                    WaitForModalPanelHidden(report);
                    AccumulateUiDismissals(report);

                    Dictionary<string, object> result = report.ToDictionary();
                    result["completed"] = true;
                    result["action"] = "act.closeui";
                    result["messageButton"] = messageButton;
                    return result;
                }

                // ---------------------------------------------------- 进入世界（自动化一步）
                // 菜单链：主菜单 → 世界列表 →(可选选中某世界)→ 创建/加载 → **角色创建/选择** → 进入游戏。
                // 每次只点一个控件、只返回一步；请由调用方循环调用（`sccmd enter` 就是那个循环），
                // 避免在游戏线程里做等待，也避免"同一帧连点"导致界面还没切就点空。
                // 参数： select（列表行目标，如 `list:WorldsList#0`）/ world（世界名）/ row（行号）
                case "act.enter":
                    return EnterWorldStep(request);

                // ------------------------------------------------ UI 定位 / 点击**服务**（UI-1）
                // 用户要求： 把相应的方法做成 CmdBridgeMod 能提供的服务，在行为树编辑器中，
                // 要能够使用来获取坐标或点击对象 ——编辑器的 `/api/game/ui/*` 与行为树
                // `Task.UiClick` 都转发到这两条命令，于是"编辑器里试一下"和"树里跑一下
                // 是同一份实现（不会出现"编辑器能点、回放点空"）。
                //
                // 目标写法（语义优先，坐标只是兜底）：
                //   `Play` / `[MainMenuScreen#0]/…/Play` / `list:WorldsList@Rebritish` / `list:WorldsList#0`
                //   `1010.6,64.83`（不推荐：窗口尺寸一变就点到别处）
                case "ui.locate":
                    return m_injector.Ui.Locate(
                        request.GetString("target", request.GetString("selector", null)),
                        request.GetBoolean("mark", false),
                        request.GetFloat("markMs", UiMarker.DefaultSeconds * 1000f) / 1000f,
                        request.GetFloat("markPx", UiMarker.DefaultDiameterPixels),
                        ReadGameWidget(request));
                case "ui.clickelement":
                    return m_injector.Ui.Click(
                        request.GetString("target", request.GetString("selector", null)),
                        request.GetString("mode", "direct"),
                        request.GetInteger("holdMs", 0),
                        request.GetBoolean("mark", false),
                        request.GetFloat("markMs", UiMarker.DefaultSeconds * 1000f) / 1000f,
                        request.GetFloat("markPx", UiMarker.DefaultDiameterPixels),
                        ReadGameWidget(request));
                // 落点标记的自查（只读）：`drawFrames > 0` 证明引擎真的调用过它的 Draw）
                // 而不是"命令返回了、屏幕上什么都没有"。
                case "ui.marker":
                    return UiMarker.Describe();

                // ------------------------------------------------------ 虚拟 UI 鼠标会话（CM-1）
                // 用引擎内的软光标做点击/拖拽：物理鼠标完全不动，也不需要窗口在前台。
                case "ui.session.begin":
                    return UiSessionCommand(request,
                        () => m_injector.Session.Begin(request.GetBoolean("mask", false)));
                case "ui.session.end":
                    return UiSessionCommand(request, () => m_injector.Session.End());
                case "ui.session.status":
                    return m_injector.Session.Describe();
                case "ui.cursor":
                {
                    float cx = 0f;
                    float cy = 0f;
                    bool hasPoint = request.TryGetFloat("x", out cx) && request.TryGetFloat("y", out cy);
                    string selector = request.GetString("selector", null);
                    int steps = request.GetInteger("steps", 1);
                    if (!hasPoint && string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.cursor needs x/y or selector.");
                    return UiSessionCommand(request, () => hasPoint
                        ? m_injector.Session.MoveTo(new Vector2(cx, cy), steps)
                        : m_injector.Session.MoveToElement(selector, steps));
                }
                case "ui.press":
                {
                    // 接口自洽：给了 x/y 就先挪到该点再按下。会话的 `Press` 本身不带坐标，
                    // 不先定位的话按下会落在"上一次操作结束的位置"（实测：移动/视角会互换）。
                    float px = 0f;
                    float py = 0f;
                    bool hasPoint = request.TryGetFloat("x", out px) && request.TryGetFloat("y", out py);
                    int steps = request.GetInteger("steps", 1);
                    return UiSessionCommand(request, () =>
                    {
                        if (hasPoint)
                            m_injector.Session.MoveTo(new Vector2(px, py), steps);
                        return m_injector.Session.Press(UiMouseSession.ToMouseButton(
                            request.GetString("button", "left"), MouseButton.Left));
                    });
                }
                case "ui.release":
                {
                    float rx = 0f;
                    float ry = 0f;
                    bool hasPoint = request.TryGetFloat("x", out rx) && request.TryGetFloat("y", out ry);
                    int steps = request.GetInteger("steps", 1);
                    return UiSessionCommand(request, () =>
                    {
                        if (hasPoint)
                            m_injector.Session.MoveTo(new Vector2(rx, ry), steps);
                        return m_injector.Session.Release(UiMouseSession.ToMouseButton(
                            request.GetString("button", "left"), MouseButton.Left));
                    });
                }
                case "ui.move":
                {
                    float mx = 0f;
                    float my = 0f;
                    if (!request.TryGetFloat("x", out mx) || !request.TryGetFloat("y", out my))
                        throw new BridgeCommandException("invalid_argument", "ui.move needs x and y.");
                    int steps = request.GetInteger("steps", 8);
                    return UiSessionCommand(request, () => m_injector.Session.MoveTo(new Vector2(mx, my), steps));
                }

                // ------------------------------------------------ Android 语义动作（手指语义）
                // 陌生 AI 只要知道"往前走 / 转头 / 跳"，不需要知道坐标与矩形；规则见 `guide.android`。
                // 引擎侧实测语义：左下 `Move` 区按住再拖 = 移动；无按钮区按住拖 = 视角，
                // 无按钮区按住不动 ≈0.2~0.5s = 挖掘；在 `Move` / `Look` 上轻点一下 = 跳跃。
                case "act.move":
                {
                    ContainerWidget root = ScreensManager.RootWidget;
                    Widget pad = root == null ? null : UiInspector.Resolve(root, "Move", false, default(Vector2));
                    if (pad == null)
                        throw new BridgeCommandException("element_missing",
                            "The Android move pad ('Move') was not found on the current screen.");
                    Vector2 from = UiInspector.CenterOf(pad);
                    float distance = request.GetFloat("distance", 90f);
                    string dir = (request.GetString("dir", "forward") ?? "forward").ToLowerInvariant();
                    float dx = dir == "left" ? -distance : (dir == "right" ? distance : 0f);
                    float dy = dir == "back" ? distance : (dir == "forward" ? -distance : 0f);
                    Vector2 to = new Vector2(from.X + dx, from.Y + dy);
                    int holdMs = request.GetInteger("holdMs", 1200);
                    m_injector.Session.Begin(false);
                    m_injector.Session.MoveTo(from, 1);
                    m_injector.Session.Press(MouseButton.Left);
                    int frames = Math.Max(2, holdMs / 16);
                    for (int i = 0; i < frames; i++)
                        m_injector.Session.MoveTo(to, 1); // 每帧重申位置 = 保持按住
                    m_injector.Session.Release(MouseButton.Left);
                    m_injector.Session.End();
                    m_injector.Session.WaitUntilIdle(4000 + holdMs);
                    m_injector.NoteUiAction("touch:move." + dir);
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["completed"] = true, ["action"] = "act.move", ["dir"] = dir,
                        ["from"] = from.ToString(), ["to"] = to.ToString(), ["holdMs"] = holdMs
                    };
                }
                case "act.jump":
                {
                    string padName = request.GetString("pad", "Move");
                    ContainerWidget root = ScreensManager.RootWidget;
                    Widget pad = root == null ? null : UiInspector.Resolve(root, padName, false, default(Vector2));
                    if (pad == null)
                        throw new BridgeCommandException("element_missing",
                            "The Android touch pad '" + padName + "' was not found on the current screen.");
                    Vector2 at = UiInspector.CenterOf(pad);
                    int holdMs = request.GetInteger("holdMs", 140); // 轻点一下 = 跳跃
                    m_injector.Session.Begin(false);
                    m_injector.Session.MoveTo(at, 1);
                    m_injector.Session.Press(MouseButton.Left);
                    int frames = Math.Max(2, holdMs / 16);
                    for (int i = 0; i < frames; i++)
                        m_injector.Session.MoveTo(at, 1);
                    m_injector.Session.Release(MouseButton.Left);
                    m_injector.Session.End();
                    m_injector.Session.WaitUntilIdle(4000 + holdMs);
                    m_injector.NoteUiAction("touch:jump." + padName);
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["completed"] = true, ["action"] = "act.jump", ["pad"] = padName,
                        ["at"] = at.ToString(), ["holdMs"] = holdMs
                    };
                }
                case "act.dig":
                {
                    // 创造模式挖掘时间 = 0（`ComponentMiner.CalculateDigTime`：Creative 且可挖 → 0f），
                    // 所以只按"最小帧数"；按久会顺着射线连挖一串（实测：0.36s 挖了 4~7 格）。
                    // 成败判定：挖前/挖后各取一次"命中观测"与该格**真实地形值**：
                    // removed=true 表示这一处确实变了；nowAir=true 表示整格清空
                    // （导线按面存储：只掉一面时 removed=true 而 nowAir=false）。
                    // 默认落点必须是**屏幕中心**，而屏幕中心要用 `Window.Size`（像素/视口空间，
                    // 去算：Android 与 `Touch.Position` 与 `Camera.ScreenToWorld` 同属**视口像素空间**
                    // （`Touch.Android.cs:27-53` 直接取 `MotionEvent.GetX/GetY`），触摸点决定挖掘射线
                    // （`ComponentInput.cs:446-457`：`Dig = ScreenToWorld(触摸点)`）。
                    // 而 `root.ActualSize` 是**设计尺寸**（平板实测 1000×600，视口是 2000×1200），
                    // 拿它的一半 (500,300) 当落点＝屏幕左上角，射线射向天空 —— 实测按满 6 秒一格不掉，
                    // 换到 (1000,600) 同一视角立刻挖掉两格。这是"挖不动"的真正根因。
                    float px = request.GetFloat("x", DefaultDigPointX());
                    float py = request.GetFloat("y", DefaultDigPointY());
                    int holdMs = request.GetInteger("holdMs", 600);
                    float maxDistance = request.GetFloat("maxDistance", 8f);
                    var beforeAim = AimObserver.Describe(maxDistance) as Dictionary<string, object>;
                    int cellX = 0;
                    int cellY = 0;
                    int cellZ = 0;
                    bool hasCell = false;
                    // `AimObserver.Describe` 把命中信息放在 **`target`** 键下（`target.cell` 才是格子），
                    // 顶层没有 `cell` —— 之前一直取顶层所以恒为 null（实测两处坑：键名与嵌套）。
                    var aimMap = beforeAim as System.Collections.IDictionary;
                    object targetObj = aimMap != null && aimMap.Contains("target")
                        ? aimMap["target"] : null;
                    var targetMap = targetObj as System.Collections.IDictionary;
                    object cellObj = targetMap != null && targetMap.Contains("cell")
                        ? targetMap["cell"]
                        : (aimMap != null && aimMap.Contains("cell") ? aimMap["cell"] : null);
                    var beforeCell = cellObj as System.Collections.IDictionary;
                    if (beforeCell != null && beforeCell.Contains("x") &&
                        beforeCell.Contains("y") && beforeCell.Contains("z"))
                    {
                        cellX = Convert.ToInt32(beforeCell["x"]);
                        cellY = Convert.ToInt32(beforeCell["y"]);
                        cellZ = Convert.ToInt32(beforeCell["z"]);
                        hasCell = true;
                    }
                    int hitFace = targetMap != null && targetMap.Contains("face")
                        ? Convert.ToInt32(targetMap["face"]) : -1;
                    // ---- 显式目标格：**只挖你指定的那一格**，且必须真的对着它 ----
                    // 为什么必须有这条：导线是"贴在某个面上的薄片"，格子中心往往是空的，
                    // 对着格心挖会**穿过它打到后面**的方块（实测：目标导线一格没掉，后面那片草地
                    // 被连挖 5 格）。所以给了 `cell=` 时先做**面扫描**：逐个面心转视角，
                    // 只有准星真的命中该格才开挖；六个面都不命中就直接失败返回，绝不误挖后面的方块。
                    int wantX;
                    int wantY;
                    int wantZ;
                    int chosenFace = -1;
                    if (TryGetTargetCell(request, out wantX, out wantY, out wantZ))
                    {
                        if (hasCell && cellX == wantX && cellY == wantY && cellZ == wantZ)
                        {
                            chosenFace = hitFace;
                        }
                        else
                        {
                            chosenFace = AimAtCellFace(wantX, wantY, wantZ, maxDistance);
                            if (chosenFace < 0)
                            {
                                return new Dictionary<string, object>(StringComparer.Ordinal)
                                {
                                    ["completed"] = false,
                                    ["action"] = "act.dig",
                                    ["aimMiss"] = true,
                                    ["cell"] = wantX + "," + wantY + "," + wantZ,
                                    ["hint"] = "no face of the target cell is hit by the crosshair; "
                                        + "nothing was dug (the wire plate may be on another face or hidden)",
                                    ["beforeAim"] = beforeAim
                                };
                            }
                            beforeAim = AimObserver.Describe(maxDistance) as Dictionary<string, object>;
                        }
                        cellX = wantX;
                        cellY = wantY;
                        cellZ = wantZ;
                        hasCell = true;
                    }
                    SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
                    int beforeValue = hasCell
                        ? terrain.Terrain.GetCellValue(cellX, cellY, cellZ) : 0;
                    bool useDirectAndroidTouch = false; // 直接落触点挖不动（引擎不认这条路径），固定走会话
                    if (useDirectAndroidTouch && AndroidTouch.Available)
                    {
                        // Android（**直接落触点**（不经过会话的 MoveTo）。会话路径会先发一次
                        // `MoveTo(落点)`，那在 Android 上等于一次大幅 look 拖拽 —— 实测表现为
                        // "低头对着目标挖 → 挖掘瞬间跳成平视 → 下一瞬看天"。直接新建触点没有移动增量，
                        // 视角就不会被拖走；按住时长用帧数表达（保持期只是等帧，不写任何位置）。
                        AndroidTouch.Press(new Vector2(px, py));
                        int releaseFrames = Math.Max(1, holdMs / 16);
                        for (int i = 0; i < releaseFrames; i++)
                        {
                            // 每帧对**同一点**发一次 Move：同点 零位移（不会拖视角），
                            // 但引擎需要触摸点进入 `Moved` 才会启动挖掘——只按不动的话
                            // 触摸点永远是 `Pressed`，挖掘不生效（实测 600/1500ms 都挖不掉）。
                            m_injector.Pump.Enqueue(delegate
                            {
                                AndroidTouch.Move(new Vector2(px, py));
                            });
                        }
                        m_injector.Pump.Enqueue(delegate
                        {
                            AndroidTouch.Release(new Vector2(px, py));
                        });
                        m_injector.NoteUiAction("touch:dig@" + (int)px + "," + (int)py);
                        int afterValueDirect = hasCell && terrain != null
                            ? terrain.Terrain.GetCellValue(cellX, cellY, cellZ) : 0;
                        return new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["completed"] = true,
                            ["action"] = "act.dig",
                            ["mode"] = "android-touch-direct",
                            ["point"] = new Dictionary<string, object>
                            {
                                ["x"] = px, ["y"] = py
                            },
                            ["holdMs"] = holdMs,
                            ["pendingFrames"] = releaseFrames + 1,
                            ["cell"] = hasCell ? cellX + "," + cellY + "," + cellZ : null,
                            ["beforeValue"] = beforeValue,
                            ["afterValue"] = afterValueDirect,
                            ["removed"] = hasCell && afterValueDirect != beforeValue,
                            ["nowAir"] = hasCell && terrain != null &&
                                Terrain.ExtractContents(afterValueDirect) == 0,
                            ["beforeAim"] = beforeAim
                        };
                    }
                    // 进度驱动：一直按住，直到**目标格真的变了**才松手（上限 maxHoldMs）。
                    // 为什么不能只按固定时长：挖掘是否完成取决于 `ComponentMiner.CalculateDigTime`
                    // —— 创造模式是 0（瞬间完成），生存模式则要按住整段挖掘时间；固定 holdMs 会在
                    // "已经出现挖掘动作、方块还没掉"的时候松手，进度归零、这一下白挖（实测症状）。
                    int maxHoldMs = request.GetInteger("maxHoldMs", 6000);
                    int attempts = 0;
                    int afterValue = 0;
                    int heldFrames = 0;
                    for (attempts = 1; attempts <= 2; attempts++)
                    {
                        // 去耦：开始前只定位一次（设置触摸落点），保持期**不再写位置**——
                        // Android 上无按钮区的触摸同时是视角摇杆，反复写位置 拖拽会把镜头转走，
                        // 导致"按下瞬间对着的是下一根导线，实际挖到别处"。
                        m_injector.Session.Begin(false);
                        // 只设落点、不排队 Move 步：避免 Android 上 定位"被当成一次 look 拖拽
                        // （低头→平视→看天），同时让 Press 落在正确的位置上（引擎认这条路径）。
                        m_injector.Session.PreparePoint(new Vector2(px, py));
                        m_injector.Session.Press(MouseButton.Left);
                        heldFrames = HoldUntilChanged(terrain, hasCell, cellX, cellY, cellZ,
                            beforeValue, px, py, holdMs, maxHoldMs);
                        m_injector.Session.Release(MouseButton.Left);
                        m_injector.Session.End();
                        m_injector.Session.WaitUntilIdle(3000 + maxHoldMs);
                        afterValue = hasCell && terrain != null
                            ? terrain.Terrain.GetCellValue(cellX, cellY, cellZ) : 0;
                        if (hasCell && afterValue != beforeValue)
                            break;
                    }
                    var afterAim = AimObserver.Describe(maxDistance) as Dictionary<string, object>;
                    m_injector.NoteUiAction("touch:dig@" + (int)px + "," + (int)py);
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["completed"] = true,
                        ["action"] = "act.dig",
                        ["point"] = new Dictionary<string, object>
                        {
                            ["x"] = px, ["y"] = py
                        },
                        ["holdMs"] = holdMs,
                        ["maxHoldMs"] = maxHoldMs,
                        ["heldFrames"] = heldFrames,
                        ["heldMs"] = heldFrames * 16,
                        ["attempts"] = attempts,
                        ["cell"] = hasCell
                            ? cellX + "," + cellY + "," + cellZ : null,
                        ["dugFace"] = chosenFace,
                        ["remainingContents"] = hasCell && terrain != null
                            ? Terrain.ExtractContents(afterValue) : 0,
                        ["beforeValue"] = beforeValue,
                        ["afterValue"] = afterValue,
                        ["removed"] = hasCell && afterValue != beforeValue,
                        ["nowAir"] = hasCell && terrain != null &&
                            Terrain.ExtractContents(afterValue) == 0,
                        ["afterBlockType"] = afterAim != null &&
                            afterAim.TryGetValue("blockType", out object abt) ? abt : null,
                        ["beforeAim"] = beforeAim,
                        ["afterAim"] = afterAim
                    };
                }
                case "guide.android":
                    return "Android touch control (CmdBridge semantic actions)\r\n"
                        + "  act.move  dir=forward|back|left|right holdMs=1200  -> hold the bottom-left Move pad and drag that way\r\n"
                        + "  act.dig   [cell=x,y,z] [x= y=] [holdMs=600] [maxHoldMs=6000] [maxDistance=8]"
                        + "  -> dig at the crosshair (Windows) / touch point (Android, default = viewport center)."
                        + " It HOLDS until the target cell actually changes (progress-driven), so it works in"
                        + " both creative (dig time 0) and survival (real dig time); returns"
                        + " cell/dugFace/beforeValue/afterValue/removed/nowAir/heldMs."
                        + " With cell=x,y,z it aims at that cell's own face first (face scan) and REFUSES to dig"
                        + " (completed=false, aimMiss=true) if no face is hit - a thin plate like a wire is not at"
                        + " the cell center, so digging the cell center would hit the block BEHIND it.\r\n"
                        + "  act.jump  pad=Move|Look holdMs=140                 -> quick tap on Move (or Look) = jump\r\n"
                        + "  act.look  yawDeg=.. pitchDeg=..  / lookdelta dYaw dPitch -> turn the camera (engine level, precise)\r\n"
                        + "  touch-drag look: ui.session.begin; ui.move x y (center, button-free); ui.press; ui.move ...; ui.release; ui.session.end\r\n"
                        + "  dig                                                -> hold still (~0.2-0.5s) in a button-free area\r\n"
                        + "  low level: ui.session.begin; ui.move x y (LOCATE FIRST); ui.press; ui.move ...; ui.release; ui.session.end\r\n"
                        + "  ui.press/ui.release also accept x/y now (they locate before pressing/releasing).\r\n"
                        + "  act.closeui [button=1|2]  -> close blocking UI now: MessageDialog -> its own accept/cancel"
                        + " path (Dismiss), other Dialog -> DialogsManager.HideDialog, inventory/creative panel ->"
                        + " ComponentGui.ModalPanelWidget = null, HUD MoreContents -> MoreButton.IsChecked = false."
                        + " Every world-input command (act.move/lookat/key/dig/use/interact/place/aim) runs this"
                        + " automatically before injecting and reports dismissedUi/dismissedCount/dismissedByKind;"
                        + " if a blocker cannot be dismissed it fails with ui_blocking instead of injecting a no-op.\r\n"
                        + "  preconditions: game in foreground, soft keyboard hidden; Android draws no cursor.";

                case "ui.click":
                {
                    string selector = request.GetString("selector", request.GetString("target", null));
                    if (string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.click needs a selector.");
                    string clickMode = request.GetString("mode", "direct");
                    // 用户定（2026-10-17）：`mode=input`（多帧软光标会话点击）**没有应用场景**，已删除。
                    // 它在出屏 HUD 目标上必然报 `element_occluded`（会话在游戏线程逐帧跑，无法跨帧等侧栏滑入），
                    // 而且全仓没有任何调用方使用它。这里明确报错，不静默走别的路径。
                    if (string.Equals(clickMode, "input", StringComparison.OrdinalIgnoreCase))
                        throw new BridgeCommandException("removed_click_mode",
                            "`mode=input` 已删除（无应用场景）：请用默认 direct 路径；需要逐步驱动时用 "
                            + "ui.session.begin / ui.press / ui.release / ui.move。");
                    float clickX;
                    float clickY;
                    bool hasPoint = request.TryGetFloat("x", out clickX)
                        && request.TryGetFloat("y", out clickY);
                    // 纯选择器点击优先走 direct（单帧合成 Tap+Click，写在目标控件自己的输入面），
                    // 世界内 GameWidget 层级下老的软光标多帧会话派生不出 Click ——
                    // `ComponentInput.UpdateInputFromMouseAndKeyboard` 每帧重写该层输入状态，
                    // GameMenuDialog 的按钮用会话点完全没反应、用 direct 立刻回到主菜单（实测）。
                    // 带坐标的点击（虚拟列表行/坐标点击）与显式 `mode=input` 仍走会话，行为不变。
                    if (!hasPoint && !string.Equals(clickMode, "input", StringComparison.OrdinalIgnoreCase))
                    {
                        return m_injector.Ui.Click(selector, "direct",
                            request.GetInteger("holdMs", 0), request.GetBoolean("mark", false),
                            request.GetFloat("markMs", UiMarker.DefaultSeconds * 1000f) / 1000f,
                            request.GetFloat("markPx", UiMarker.DefaultDiameterPixels),
                            ReadGameWidget(request));
                    }
                    return UiSessionCommand(request, () => m_injector.Session.Click(selector));
                }
                // ------------------------------------------------------ 空格/跳跃审计与缓冲（跳跃手感）
                case "jump.status":
                    return m_jumpAssist.Describe();
                case "jump.buffer":
                {
                    string requested = request.GetString("state", "toggle");
                    bool enable;
                    if (string.Equals(requested, "on", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(requested, "true", StringComparison.OrdinalIgnoreCase))
                        enable = true;
                    else if (string.Equals(requested, "off", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(requested, "false", StringComparison.OrdinalIgnoreCase))
                        enable = false;
                    else if (string.Equals(requested, "toggle", StringComparison.OrdinalIgnoreCase))
                        enable = !m_jumpAssist.Enabled;
                    else
                        throw new BridgeCommandException("invalid_argument",
                            "jump.buffer expects on / off / toggle.");
                    m_jumpAssist.SetEnabled(enable);
                    // 只改本次运行：开关默认写在代码里（恒久打开），不再写盘、不再有配置项。
                    return m_jumpAssist.Describe();
                }
                // ------------------------------------------------------ 焦点策略与共控（CM-2）
                case "focus.status":
                    return m_injector.Focus.Describe();
                case "focus.auto":
                case "focus.follow":
                case "focus.attach":
                case "focus.detach":
                {
                    string requested = request.Command.Substring("focus.".Length);
                    FocusMode mode;
                    if (!FocusPolicy.TryParseMode(requested, out mode))
                        throw new BridgeCommandException("invalid_argument", "Unknown focus mode: " + requested);
                    m_injector.Focus.Mode = mode;
                    Log.Information("[CmdBridge] focus mode -> " + m_injector.Focus.ModeName);
                    return m_injector.Focus.Describe();
                }
                case "focus.recover":
                {
                    // 规则 2（用户 2026-10-17 定）：**命令行桥存在时禁止抢占真实鼠标与窗口；请在后台用虚拟输入完成。**
                    // 所以这条命令现在只做两件安全事，并**只回报**状态：
                    //   ① 把焦点策略交回"跟随引擎"（`Follow`，虚拟输入照旧工作）；
                    //   ② 回报焦点/挂接状态（只读）。
                    // 它**不再**去动任何窗口状态：`RestoreNaturalFocus()` 已经从这条路上摘掉，
                    // 因为 `FocusPolicy.ApplyFrameStart` 每帧本来就会在非脱离状态下把 `Window.m_state`
                    // 写回真实焦点值 —— 交还这件事不需要、也不允许由一条命令"抢占式"地做。
                    // 调用方若显式要求抢前台/激活窗口，一律明确报错（不静默退回旧行为）。
                    bool wantsRealWindow =
                        request.GetBoolean("activate", false)
                        || request.GetBoolean("foreground", false)
                        || request.GetBoolean("window", false)
                        || request.GetBoolean("steal", false)
                        || string.Equals(request.GetString("mode", null), "foreground",
                            StringComparison.OrdinalIgnoreCase);
                    m_injector.Focus.Mode = FocusMode.Follow;
                    Dictionary<string, object> focusResult = m_injector.Focus.Describe();
                    if (wantsRealWindow)
                    {
                        // 用户定（2026-10-17）：不写拦截/护栏，也不静默 —— 把这次请求的信息回报给调用者。
                        FocusPolicy.FillRealWindowRequestInfo(focusResult,
                            "focus.recover activate/foreground/window/steal");
                        Log.Information("[CmdBridge] focus.recover: caller asked for real window activation; "
                            + "not executed (background-only), reported to caller");
                    }
                    Log.Information("[CmdBridge] focus.recover: follow mode restored, report-only "
                        + "(rule 2: no real window/mouse seizure, virtual input only)");
                    return focusResult;
                }
                case "focus.merge":
                    m_injector.Focus.MergeEnabled = request.GetBoolean("enabled", true);
                    return m_injector.Focus.Describe();
                case "focus.lookhold":
                    m_injector.Focus.LookHoldSeconds = request.GetFloat("seconds", 0.4f);
                    return m_injector.Focus.Describe();
                case "hotkey.status":
                    return m_injector.Hotkeys.Describe();
                case "cmd.list":
                    return ListCommands();
                case "look.owner":
                {
                    LookOwnerMode owner;
                    if (!FocusPolicy.TryParseLookOwner(request.GetString("owner", null), out owner))
                        throw new BridgeCommandException(
                            "invalid_argument", "look.owner must be auto|user|ai|shared.");
                    m_injector.Focus.LookOwner = owner;
                    return m_injector.Focus.Describe();
                }

                default:
                {
                    // 扩展命令（别的 Mod 注册的，例如 PlayerAiMod 的 ai.*）：
                    // 内建命令优先，扩展命令不能覆盖内建行为。
                    CommandExtension extension;
                    if (m_injector.Commands.TryGet(request.Command, out extension))
                        return ExecuteExtension(extension, request);

                    throw new BridgeCommandException(
                        "unknown_command", "Unknown command '" + request.Command + "'.");
                }
            }
        }

        /// <summary>`CellFace` 的六个面法向（`Game/CellFace.cs`） =+Z 1=+X 2=-Z 3=-X 4=+Y 5=-Y）。</summary>
        private static readonly float[] FaceNormalX = { 0f, 1f, 0f, -1f, 0f, 0f };
        private static readonly float[] FaceNormalY = { 0f, 0f, 0f, 0f, 1f, -1f };
        private static readonly float[] FaceNormalZ = { 1f, 0f, -1f, 0f, 0f, 0f };

        /// <summary>
        /// 从请求里取"要挖哪一格"：`cell=x,y,z` 字符串，或 `cellX/cellY/cellZ` 三个整数、
        /// 不给就返回 false（此时按准星/触摸点命中的那一格挖）。
        /// </summary>
        private static bool TryGetTargetCell(BridgeRequest request, out int x, out int y, out int z)
        {
            x = 0;
            y = 0;
            z = 0;
            string cell = request.GetString("cell", null);
            if (!string.IsNullOrEmpty(cell))
            {
                string[] parts = cell.Split(',');
                return parts.Length == 3 &&
                    int.TryParse(parts[0].Trim(), out x) &&
                    int.TryParse(parts[1].Trim(), out y) &&
                    int.TryParse(parts[2].Trim(), out z);
            }
            return request.TryGetInteger("cellX", out x) &&
                request.TryGetInteger("cellY", out y) &&
                request.TryGetInteger("cellZ", out z);
        }

        /// <summary>准星当前命中的格子是不是指定格（只看格子，不看方块类型）。</summary>
        private static bool AimHitsCell(int cellX, int cellY, int cellZ, float maxDistance)
        {
            var aim = AimObserver.Describe(maxDistance) as System.Collections.IDictionary;
            object target = aim != null && aim.Contains("target") ? aim["target"] : null;
            var targetMap = target as System.Collections.IDictionary;
            object cell = targetMap != null && targetMap.Contains("cell") ? targetMap["cell"] : null;
            var cellMap = cell as System.Collections.IDictionary;
            if (cellMap == null || !cellMap.Contains("x"))
                return false;
            return Convert.ToInt32(cellMap["x"]) == cellX &&
                Convert.ToInt32(cellMap["y"]) == cellY &&
                Convert.ToInt32(cellMap["z"]) == cellZ;
        }

        /// <summary>
        /// 逐个面心转视角，返回**第一个能让准星命中该格**的面号；六个面都不命中返回 -1。
        ///
        /// 用途：导线/告示牌这类 贴在面上的薄片 必须对着它所在的那个面挖 ——
        /// 对着格心挖会穿过去打到后面的方块（实测：目标导线没掉，后面的草地被连挖 5 格）。
        /// 面心 = 格中心 + 0.5 × 面法向（`CellFace.FaceToVector3` 的同一约定）。
        /// </summary>
        private int AimAtCellFace(int cellX, int cellY, int cellZ, float maxDistance)
        {
            for (int face = 0; face < 6; face++)
            {
                m_injector.LookAt(
                    cellX + 0.5f + FaceNormalX[face] * 0.5f,
                    cellY + 0.5f + FaceNormalY[face] * 0.5f,
                    cellZ + 0.5f + FaceNormalZ[face] * 0.5f);
                WaitGameFrames(2); // 视角写完之后相机要出帧才会更新 ViewDirection
                if (AimHitsCell(cellX, cellY, cellZ, maxDistance))
                    return face;
            }
            return -1;
        }

        /// <summary>等游戏出 <paramref name="frames"/> 帧（游戏不出帧时靠超时兜底，不会卡死）。</summary>
        private void WaitGameFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                int before = m_injector.Pump.FramesPumped;
                var stopwatch = Stopwatch.StartNew();
                while (m_injector.Pump.FramesPumped == before && stopwatch.ElapsedMilliseconds < 200)
                    System.Threading.Thread.Sleep(2);
            }
        }

        /// <summary>
        /// 挖掘默认落点的 X = **视口中心**（`Window.Size` 的一半）。
        /// 为什么不用 `ScreensManager.RootWidget.ActualSize`：那是设计尺寸，Android 上是视口的一半
        /// （实测视口 2000×1200、根控件 1000×600），用它算出来的"中心"(500,300) 在引擎眼里是屏幕
        /// 左上角，挖掘射线射向天空，怎么按都挖不掉。取不到尺寸时回退到 1000（常见视口中心）。
        /// </summary>
        private static float DefaultDigPointX()
        {
            try
            {
                Point2 size = Window.Size;
                if (size.X > 0)
                    return size.X * 0.5f;
            }
            catch
            {
            }
            return 1000f;
        }

        /// <summary>挖掘默认落点的 Y，见 <see cref="DefaultDigPointX"/>。</summary>
        private static float DefaultDigPointY()
        {
            try
            {
                Point2 size = Window.Size;
                if (size.Y > 0)
                    return size.Y * 0.5f;
            }
            catch
            {
            }
            return 600f;
        }

        /// <summary>
        /// 保持按住（左键/触摸）直到**目标格地形值真的变化**，返回按住的总帧数。
        ///
        /// 为什么要"按到变了才松手"而不是按时长：挖掘进度由 `ComponentMiner` 的挖掘时间决定
        /// （`ComponentMiner.CalculateDigTime`：创造模式 0、生存模式为方块硬度决定的正数），
        /// 固定 holdMs 会出现"画面已经在挖、方块还没掉就松手"——进度归零，这一下白挖（实测症状）。
        ///
        /// 保持期**只推进帧，绝不写位置**（`HoldNoMove`）：
        ///   · 引擎（Android）把"无按钮区按住不动"当作挖掘，靠触点**停在原地**来维持；
        ///   · 一旦每帧都写一次位置（哪怕是同一个点），触摸状态机认为手指还在移动，
        ///     永远进不了"按住不动"那一档 —— 实测按住 12 秒（两次 6 秒、每帧同点 MoveTo）
        ///     **一格都没挖掉**；改回纯帧推进后同格同视角立刻挖掉（05:2x 被主机接受的那批挖掘
        ///     跑的正是纯帧推进的版本，后来的"每帧同点 Move"是我自己引入的回归）。
        ///   · 不写位置也顺带保证镜头不会被拖走（Δpitch = 0.000）。
        ///
        /// **滚动窗口**：队列里始终预排 <see cref="WindowFrames"/> 个帧推进，避免出现"空帧"
        /// 打断挖掘；同时每帧都读一次目标格 —— 创造模式挖掘时间 0，判定晚一帧就多挖一格。
        /// </summary>
        private int HoldUntilChanged(SubsystemTerrain terrain, bool hasCell, int cellX, int cellY,
            int cellZ, int beforeValue, float px, float py, int minHoldMs, int maxHoldMs)
        {
            const int WindowFrames = 3;
            int minFrames = Math.Max(1, minHoldMs / 16);
            int maxFrames = Math.Max(minFrames, maxHoldMs / 16);
            int frames = 0;
            for (int i = 0; i < WindowFrames; i++)
                m_injector.Session.HoldNoMove();

            while (frames < maxFrames)
            {
                // 等"这一帧"被执行：`ActionsExecuted` 由帧首泵在真正执行动作时自增：
                // 比 `WaitUntilIdle` 更贴近"这一帧已经落到引擎上了"。游戏不出帧时靠超时兜底
                // （最小化/暂停不会把命令线程永久卡住）。
                int executed = m_injector.Pump.ActionsExecuted;
                var stopwatch = Stopwatch.StartNew();
                while (m_injector.Pump.ActionsExecuted == executed && stopwatch.ElapsedMilliseconds < 250)
                    System.Threading.Thread.Sleep(2);

                frames++;
                if (!hasCell || terrain == null)
                {
                    // 没瞄到格子（例如对着空气）：退化为"至少按满 minHoldMs"。
                    if (frames >= minFrames)
                        break;
                }
                else if (terrain.Terrain.GetCellValue(cellX, cellY, cellZ) != beforeValue)
                {
                    break;
                }

                // 补上刚消耗掉的那一帧，保持窗口不空（这一步会落在下一帧）。
                m_injector.Session.HoldNoMove();
            }
            return frames;
        }

        /// <summary>
        /// 执行扩展命令。默认在**游戏线程**执行（触碰游戏对象/输入的命令必须如此），
        /// 扩展命令抛出的 <see cref="CmdBridgeCommandException"/> 直接映射成错误码返回给客户端。
        /// </summary>
        private object ExecuteExtension(CommandExtension extension, BridgeRequest request)
        {
            var view = new CmdBridgeCommandRequest(request);
            Func<object> action = () =>
            {
                try
                {
                    object result = extension.Handler(view);
                    return result ?? new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["ok"] = true,
                        ["command"] = extension.Name
                    };
                }
                catch (CmdBridgeCommandException exception)
                {
                    throw new BridgeCommandException(exception.Code, exception.Message);
                }
            };

            return extension.RunOnGameThread ? OnGameThread(action) : action();
        }

        /// <summary>列出内建命令与扩展命令（谁注册的一目了然，便于排查"命令没生效"）。</summary>
        private object ListCommands()
        {
            List<CommandExtension> extensions = m_injector.Commands.List();
            var builtIn = new List<string>(BuiltInCommands);
            var external = new List<Dictionary<string, object>>();
            for (int i = 0; i < extensions.Count; i++)
            {
                external.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = extensions[i].Name,
                    ["owner"] = extensions[i].Owner,
                    ["description"] = extensions[i].Description,
                    ["gameThread"] = extensions[i].RunOnGameThread
                });
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["builtIn"] = builtIn,
                ["extensions"] = external,
                ["counts"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["builtIn"] = builtIn.Count,
                    ["extensions"] = external.Count
                }
            };
        }

        /// <summary>
        /// 执行一条"虚拟 UI 鼠标会话"命令：默认等手势在帧首逐帧跑完再返回，
        /// 于是脚本/AI 拿到返回值时动作已经落地（等价于真人松手那一刻）。
        /// </summary>
        private object UiSessionCommand(BridgeRequest request, Func<Dictionary<string, object>> action)
        {
            action();

            bool wait = request.GetBoolean("wait", true);
            bool completed = true;
            if (wait)
            {
                int timeout = request.GetInteger("timeoutMs", 5000);
                completed = m_injector.Session.WaitUntilIdle(Math.Max(200, timeout));
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["completed"] = completed,
                ["session"] = m_injector.Session.Describe()
            };
            if (!completed)
                result["hint"] = "The gesture is still running; poll ui.session.status or retry with a larger timeoutMs.";
            return result;
        }

        private object OnGameThread(Func<object> action)
        {
            return m_invoker.Invoke(action);
        }

        /// <summary>
        /// 可选的 `gameWidget` 目标（`gameWidget=N` / CLI `--gamewidget N`）：**分屏槽位号**，
        /// 就是 `GameWidget.GameWidgetIndex`（H1=0 / H2=1 / H3=2…），也是 `obs.ui` 每个元素的
        /// `gameWidget` 字段与 `views[]` 里的索引。不传 = 用主视图（规则见
        /// `UiSplitView.SelectPrimaryView`：接受鼠标的最小槽位）。
        /// 只做"读出来并校验存在"，其它语义全部在 `UiInspector` / `UiService` 里，只有一份实现。
        /// </summary>
        private static int? ReadGameWidget(BridgeRequest request)
        {
            int value;
            if (!request.TryGetInteger("gameWidget", out value))
                return null;
            if (value < 0)
            {
                throw new BridgeCommandException("invalid_argument",
                    "gameWidget must be >= 0 (it is the split-screen slot: H1=0, H2=1, H3=2, H4=3).");
            }
            UiInspector.RequireKnownGameWidget(value);
            return value;
        }

        // ---------------------------------------------------------------- 注入前 UI 检查

        /// <summary>
        /// 哪些命令在注入前必须先关掉挡路的界面。
        ///
        /// 判据只有一条：**这条命令是往世界灌玩家输入的**（移动 / 视角 / 键 / 挖掘 / 使用 / 交互 /
        /// 放置 / 瞄准）。它们被 `ComponentGui.ModalPanelWidget` / `DialogsManager.HasDialogs(GuiWidget)`
        /// 整段吃掉（`ComponentInput.cs:143-152`），所以必须先收界面。
        ///
        /// **故意排除**这些：
        ///  · `act.uiclick` / `ui.*` / `act.text` —— 它们的**目标就是界面本身**，替它们关界面等于
        ///    "把要点的东西关掉"；
        ///  · `act.mouse` / `act.wheel` —— 既能点世界也能拖滑块/滚列表，属于界面点击家族，
        ///    自动化里用它们点界面时不能被自动关闭打断（要关界面用 `act.closeui`）；
        ///  · `act.releaseall` —— 安全释放，任何时候都必须原样执行。
        /// </summary>
        private static bool IsWorldInputCommand(string command)
        {
            switch (command)
            {
                case "act.look":
                case "act.lookdelta":
                case "act.lookat":
                case "act.key":
                case "act.hold":
                case "act.chord":
                case "act.move":
                case "act.jump":
                case "act.dig":
                case "act.use":
                case "act.interact":
                case "act.place":
                case "act.aim":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Escape / Back 是**引擎自己的**取消/返回通道（`WidgetInput.UpdateInputFromKeyboard`：
        /// `IsKeyDownOnce(Key.Escape)` → `Back = Cancel = true`），`MessageDialog` 用它走 Cancel、
        /// `ComponentGui.HandleInput` 用它关模态面板、`GameMenuDialog` 用它关菜单。
        ///
        /// 所以这两种键**不做**注入前自动关闭：否则"用 esc 取消一个 Are you sure? 对话框"
        /// 会被我们先替成"确定"，语义完全反了。让引擎自己处理，命令照常注入。
        /// </summary>
        private static bool IsEngineCloseKey(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName))
                return false;
            Key key;
            return Enum.TryParse(keyName.Trim(), true, out key)
                && (key == Key.Escape || key == Key.Back);
        }

        /// <summary>这条命令这次要不要做注入前 UI 检查；不要时返回 null。</summary>
        private UiBlocker.Report PrepareForWorldInput(BridgeRequest request)
        {
            string command = request.Command;
            if (!IsWorldInputCommand(command))
                return null;

            // 注入被配置关掉时命令自己会报 `injection_disabled`：别在报错前先动界面。
            if (!m_config.EnableInputInjection)
                return null;

            // 真实鼠标正在被用户使用、视角归用户时，look 系列会被注入器跳过（`skipped` 回包）：
            // 命令本来就不注入，别顺手替用户关界面。
            if ((command == "act.look" || command == "act.lookdelta" || command == "act.lookat")
                && m_injector.Focus != null && m_injector.Focus.LookOwnedByUser)
            {
                return null;
            }

            // Escape/Back 由引擎自己关界面（见 IsEngineCloseKey）：不替它们"接受"任何对话框。
            if (command == "act.key" || command == "act.hold")
            {
                if (IsEngineCloseKey(request.GetString("key", null)))
                    return null;
            }
            else if (command == "act.chord")
            {
                if (IsEngineCloseKey(request.GetString("key", null)))
                    return null;
            }

            bool onGameThread = GameThreadInvoker.IsGameThread();
            UiBlocker.Report report = (UiBlocker.Report)OnGameThread(() => UiBlocker.Dismiss(1));
            AccumulateUiDismissals(report);

            // 对话框 handler 把世界卸载/重载了（ScMP 的 `CR` → `Create` 就是这样）：
            // 本帧已经无法再检查/注入。**不能**把这次注入当成功——世界都没了，写进去也读不到
            // （那正是"命令成功、人不动"）。明确报 `world_not_active` 并给出重试条件。
            if (report.WorldUnloaded)
            {
                throw new BridgeCommandException("world_not_active",
                    "A dialog was dismissed, but its accept handler unloaded/reloaded the world "
                    + "(ScMP's CR -> Create does GameManager.DisposeProject + SwitchScreen(\"GameLoading\"); "
                    + "Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:"
                    + "CreateRoomFromCurrentWorld). The world is gone, so '" + command + "' was NOT "
                    + "injected (writing input now would be a silent no-op). Run "
                    + "`waitfor world.loaded`, `waitfor screen.is:Game` and retry. "
                    + "dismissed: " + report.Summary()
                    + (report.SkipDetail != null ? " (" + report.SkipDetail + ")" : ""));
            }

            if (report.Remaining.Count > 0)
            {
                // 关不掉就是关不掉：绝不"注入一下看看"，那样只会得到一个静默空操作。
                throw new BridgeCommandException("ui_blocking",
                    "A blocking UI could not be dismissed: "
                    + string.Join(", ", report.Remaining.ToArray())
                    + ". The engine ignores world input while it is up "
                    + "(ComponentInput.cs:143-152), so '" + command + "' was NOT injected. "
                    + "Current UI: " + DescribeCurrentUiSafe());
            }

            WaitForModalPanelHidden(report, onGameThread, command);
            return report;
        }

        /// <summary>
        /// 错误信息里"当前界面长什么样"的补充读数。这段只是附加信息，本身在游戏线程上求值
        /// （控件树不是线程安全的）；求值失败也不影响主错误。
        /// </summary>
        private string DescribeCurrentUiSafe()
        {
            try
            {
                return (string)OnGameThread(() => UiBlocker.DescribeCurrentUi());
            }
            catch (Exception exception)
            {
                return "<unavailable: " + exception.GetType().Name + ">";
            }
        }

        /// <summary>
        /// 模态面板关掉后等它**真的**从 `ComponentGui.ModalPanelWidget` 上消失。
        ///
        /// 为什么必须等：`ComponentGui.ModalPanelWidget = null` 只是启动收起动画
        /// （`ComponentGui.UpdateModalPanelAnimation`，6*dt 一帧，约 10 帧 ≈ 170ms），
        /// 动画期间 getter 仍返回旧面板，而 `ComponentInput`（UpdateOrder.Input = -10）
        /// 在 `ComponentGui`（Default = 0）**之前**更新 —— 于是这一帧仍按"模态"处理，
        /// `act.key` 的 40ms 脉冲会正好落在里面（"关掉了但键没生效"）。
        ///
        /// 命令线程上可以等（游戏线程继续出帧）；**游戏线程上不能等**（等就是卡住整帧），
        /// 那种情况下明确报 `ui_dismiss_pending` 让调用方重发，绝不假装成功。
        /// </summary>
        private void WaitForModalPanelHidden(UiBlocker.Report report)
        {
            WaitForModalPanelHidden(report, GameThreadInvoker.IsGameThread(), "act.closeui");
        }

        private void WaitForModalPanelHidden(UiBlocker.Report report, bool onGameThread, string command)
        {
            if (report == null || !report.ClosedModalPanel)
                return;

            if (onGameThread)
            {
                throw new BridgeCommandException("ui_dismiss_pending",
                    "Closed the modal panel, but its close animation keeps "
                    + "ComponentGui.ModalPanelWidget non-null for a few frames and ComponentInput still "
                    + "treats that as modal (ComponentInput.cs:143-152). This call is on the game "
                    + "thread and cannot wait for frames -> '" + command + "' was NOT injected; retry "
                    + "it on the next frame.");
            }

            // 800ms 足够 10 帧的动画（60fps ≈ 170ms）；游戏不出帧时（暂停/最小化）超时并明确报错。
            long start = Environment.TickCount64;
            while (true)
            {
                bool open = (bool)OnGameThread(() =>
                {
                    ComponentPlayer player = GetPlayerOrNull();
                    ComponentGui gui = player != null ? player.ComponentGui : null;
                    return (object)(gui != null && gui.ModalPanelWidget != null);
                });
                if (!open)
                    return;
                if (Environment.TickCount64 - start > 800)
                {
                    throw new BridgeCommandException("ui_dismiss_pending",
                        "The modal panel was closed, but ComponentGui.ModalPanelWidget is still non-null "
                        + "after 800ms (the close animation needs frames to advance; the game may be "
                        + "paused). '" + command + "' was NOT injected; retry once the game is running.");
                }
                Thread.Sleep(10);
            }
        }

        /// <summary>把一次关闭累加进 `status` 的累计计数（实例字段；并发命令用 Interlocked）。</summary>
        private void AccumulateUiDismissals(UiBlocker.Report report)
        {
            if (report == null)
                return;
            if (report.DialogCount > 0)
                Interlocked.Add(ref m_uiDismissedDialogs, report.DialogCount);
            if (report.ModalPanelCount > 0)
                Interlocked.Add(ref m_uiDismissedModalPanels, report.ModalPanelCount);
            if (report.HudPanelCount > 0)
                Interlocked.Add(ref m_uiDismissedHudPanels, report.HudPanelCount);
        }

        /// <summary>
        /// 把"关掉了哪些界面"合并进命令回包。
        ///
        /// 每条世界内输入命令的成功回包都会带 `dismissedUi` / `dismissedCount` / `dismissedByKind`
        /// （没关东西时是空表 + 0）—— 自动化据此就能区分"界面本来就干净"和"这次替我把对话框按掉了"。
        /// 回包不是字典时（理论上不会发生）包一层 `result`，绝不丢信息。
        /// </summary>
        private static object AttachUiReport(object result, UiBlocker.Report report)
        {
            if (report == null)
                return result;

            Dictionary<string, object> extra = report.ToDictionary();
            var dictionary = result as IDictionary<string, object>;
            if (dictionary != null)
            {
                foreach (KeyValuePair<string, object> pair in extra)
                {
                    if (!dictionary.ContainsKey(pair.Key))
                        dictionary[pair.Key] = pair.Value;
                }
                return result;
            }

            var wrapper = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result
            };
            foreach (KeyValuePair<string, object> pair in extra)
                wrapper[pair.Key] = pair.Value;
            return wrapper;
        }

        // ---------------------------------------------------------------- 只读实现

        /// <summary>
        /// 条件等待：在游戏线程上周期性求值，直到全部条件成立或超时。
        ///
        /// 为什么需要它：屏幕切换、世界加载、面板弹出都是跨帧完成的——SwitchScreen 会立即更新
        /// CurrentScreen，但新屏幕的控件要等转屏动画中段才进入 RootWidget.Children
        /// （ScreensManager.cs:85, 302-309）；对话框也是异步关闭的。客户端靠固定 sleep 必然要么太慢、
        /// 要么在竞态里读到空界面。有了它，AI 可以写"按键开背包 → 等槽位可点"，语义明确、不靠猜。
        ///
        /// 支持的条件（大小写不敏感）：
        ///   screen.animating.false / screen.animating.true / screen.is:&lt;name&gt;
        ///   element.present:&lt;selector&gt; / element.hittable:&lt;selector&gt; / element.clickable:&lt;selector&gt;
        ///   modal.none / modal.is:&lt;TypeName&gt; / dialog.none / dialog.present
        ///   world.loaded / world.unloaded / player.alive / player.dead
        ///   player.sleeping / player.awake / events.since:&lt;seq&gt;
        /// </summary>
        private object WaitForConditions(BridgeRequest request)
        {
            string single = request.GetString("condition", null);
            List<string> conditions = request.GetStringArray("conditions");
            if (!string.IsNullOrWhiteSpace(single))
                conditions.Insert(0, single);
            if (conditions.Count == 0)
            {
                throw new BridgeCommandException(
                    "invalid_argument", "condition or conditions is required.");
            }

            int timeoutMs = MathUtils.Clamp(
                request.GetInteger("timeoutMs", 10000), 0, WaitMaxTimeoutMs);
            long startTicks = Environment.TickCount64;
            int polls = 0;

            while (true)
            {
                string pending = (string)m_invoker.Invoke(() =>
                {
                    // 条件求值必须整批在同一帧完成，否则多个条件可能落在不同帧上互相矛盾。
                    for (int i = 0; i < conditions.Count; i++)
                    {
                        string failed = EvaluateCondition(conditions[i]);
                        if (failed != null)
                            return failed;
                    }
                    return null;
                });
                polls++;

                if (pending == null)
                {
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["satisfied"] = true,
                        ["conditions"] = conditions,
                        ["elapsedMs"] = Environment.TickCount64 - startTicks,
                        ["polls"] = polls
                    };
                }

                if (Environment.TickCount64 - startTicks >= timeoutMs)
                {
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["satisfied"] = false,
                        ["pendingCondition"] = pending,
                        ["conditions"] = conditions,
                        ["elapsedMs"] = Environment.TickCount64 - startTicks,
                        ["polls"] = polls,
                        ["timeoutMs"] = timeoutMs
                    };
                }

                Thread.Sleep(WaitPollIntervalMs);
            }
        }

        /// <summary>返回 null 表示条件成立；否则返回该条件（供超时报告）。</summary>
        private string EvaluateCondition(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
                throw new BridgeCommandException("invalid_argument", "condition cannot be empty.");
            string c = condition.Trim();

            if (string.Equals(c, "screen.animating.false", StringComparison.OrdinalIgnoreCase))
                return ScreensManager.IsAnimating ? c : null;
            if (string.Equals(c, "screen.animating.true", StringComparison.OrdinalIgnoreCase))
                return ScreensManager.IsAnimating ? null : c;

            const string screenPrefix = "screen.is:";
            if (c.StartsWith(screenPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string expected = c.Substring(screenPrefix.Length).Trim();
                string actual = UiInspector.ScreenName();
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? null : c;
            }

            if (c.StartsWith("element.", StringComparison.OrdinalIgnoreCase))
                return EvaluateElementCondition(c);

            if (string.Equals(c, "modal.none", StringComparison.OrdinalIgnoreCase))
                return CurrentModalPanel() == null ? null : c;
            const string modalPrefix = "modal.is:";
            if (c.StartsWith(modalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string expected = c.Substring(modalPrefix.Length).Trim();
                string actual = CurrentModalPanel();
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? null : c;
            }

            if (string.Equals(c, "dialog.none", StringComparison.OrdinalIgnoreCase))
                return DialogsManager.Dialogs.Count == 0 ? null : c;
            if (string.Equals(c, "dialog.present", StringComparison.OrdinalIgnoreCase))
                return DialogsManager.Dialogs.Count > 0 ? null : c;

            if (string.Equals(c, "world.loaded", StringComparison.OrdinalIgnoreCase))
                return GameManager.Project != null ? null : c;
            if (string.Equals(c, "world.unloaded", StringComparison.OrdinalIgnoreCase))
                return GameManager.Project == null ? null : c;

            if (string.Equals(c, "player.alive", StringComparison.OrdinalIgnoreCase))
            {
                ComponentPlayer player = GetPlayerOrNull();
                return player != null && player.ComponentHealth != null &&
                    player.ComponentHealth.Health > 0f ? null : c;
            }
            if (string.Equals(c, "player.dead", StringComparison.OrdinalIgnoreCase))
            {
                ComponentPlayer player = GetPlayerOrNull();
                return player != null && player.ComponentHealth != null &&
                    player.ComponentHealth.Health <= 0f ? null : c;
            }
            if (string.Equals(c, "player.sleeping", StringComparison.OrdinalIgnoreCase))
                return IsPlayerSleeping() ? null : c;
            if (string.Equals(c, "player.awake", StringComparison.OrdinalIgnoreCase))
                return IsPlayerSleeping() ? c : null;

            const string eventsPrefix = "events.since:";
            if (c.StartsWith(eventsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string raw = c.Substring(eventsPrefix.Length).Trim();
                if (!long.TryParse(raw, out long seq))
                {
                    throw new BridgeCommandException(
                        "invalid_wait_condition", "events.since requires a numeric sequence.");
                }
                return m_recorder.LastSeq > seq ? null : c;
            }

            throw new BridgeCommandException(
                "invalid_wait_condition", "Unknown wait condition '" + c + "'.");
        }

        private string EvaluateElementCondition(string condition)
        {
            int colon = condition.IndexOf(':');
            if (colon < 0)
            {
                throw new BridgeCommandException(
                    "invalid_wait_condition",
                    "element conditions look like element.clickable:<selector>.");
            }
            string kind = condition.Substring(0, colon).Trim().ToLowerInvariant();
            string selector = condition.Substring(colon + 1).Trim();
            if (selector.Length == 0)
            {
                throw new BridgeCommandException(
                    "invalid_wait_condition", "element condition needs a selector.");
            }

            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                return condition;   // 界面尚未就绪，继续等待

            Widget widget;
            try
            {
                widget = UiInspector.Resolve(root, selector, false, Vector2.Zero);
            }
            catch (BridgeCommandException exception) when (exception.Code == "element_missing")
            {
                return condition;   // 还没出现，继续等待
            }

            switch (kind)
            {
                case "element.present":
                    return null;
                case "element.hittable":
                    return UiInspector.IsHittable(widget) ? null : condition;
                case "element.clickable":
                    return UiInspector.IsClickable(widget) ? null : condition;
                default:
                    throw new BridgeCommandException(
                        "invalid_wait_condition", "Unknown element condition '" + kind + "'.");
            }
        }

        private static string CurrentModalPanel()
        {
            ComponentPlayer player = GetPlayerOrNull();
            if (player == null)
                return null;
            try
            {
                ComponentGui gui = player.ComponentGui;
                return gui != null && gui.ModalPanelWidget != null
                    ? gui.ModalPanelWidget.GetType().Name
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPlayerSleeping()
        {
            ComponentPlayer player = GetPlayerOrNull();
            if (player == null)
                return false;
            try
            {
                ComponentSleep sleep = player.Entity.FindComponent<ComponentSleep>(false);
                return sleep != null && sleep.IsSleeping;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// `act.enter` 的一步：把"进世界"这条菜单链往前推一格。
        ///
        /// 屏幕 → 动作：
        ///   MainMenu  → 点 `Play`（进世界列表）
        ///   Play      → 先选世界（可用 world/row/select 指定）或直接点 `Play`（开始选中世界）
        ///   NewWorld  → 点 `Play`（用当前默认参数创建并加载）
        ///   Player    → 点 `PlayButton`（**角色创建/选择界面**，没有角色的世界一定会经过这里）
        ///   Game      → done
        ///
        /// 返回 `screen / clicked / done / reason`；调用方循环调用直到 `done == true`。
        /// 屏幕在切换动画中时返回 `reason=screen-animating`（这一帧不点，等下一次）。
        /// </summary>
        private Dictionary<string, object> EnterWorldStep(BridgeRequest request)
        {
            string screen = OnGameThread(() => UiInspector.ScreenName()) as string;
            bool animating = OnGameThread(() => ScreensManager.IsAnimating) is bool b && b;

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["screen"] = screen,
                ["animating"] = animating,
                ["done"] = string.Equals(screen, "Game", StringComparison.Ordinal),
                ["clicked"] = null
            };
            if ((bool)result["done"])
            {
                return result;
            }
            if (animating)
            {
                result["reason"] = "screen-animating";
                return result;
            }

            string target;
            if (string.Equals(screen, "MainMenu", StringComparison.Ordinal))
            {
                target = "Play";
            }
            else if (string.Equals(screen, "Play", StringComparison.Ordinal))
            {
                bool createNew = request.GetBoolean("new", false);
                string select = request.GetString("select", null);
                string world = request.GetString("world", null);
                int row = request.GetInteger("row", -1);
                if (createNew)
                {
                    target = "NewWorld";                 // 新建世界（用当前默认参数）
                }
                else
                {
                    if (!string.IsNullOrEmpty(world))
                    {
                        select = "list:WorldsList@" + world;
                    }
                    else if (row >= 0 && string.IsNullOrEmpty(select))
                    {
                        select = "list:WorldsList#" + row;
                    }
                    target = string.IsNullOrEmpty(select) ? "Play" : select;
                }
            }
            else if (string.Equals(screen, "NewWorld", StringComparison.Ordinal))
            {
                target = "Play";
            }
            else if (string.Equals(screen, "Player", StringComparison.Ordinal))
            {
                target = "PlayButton";
            }
            else
            {
                result["reason"] = "unknown-screen";
                return result;
            }

            result["clicked"] = target;
            result["clickResult"] = m_injector.Ui.Click(
                target, "auto", 0, false, UiMarker.DefaultSeconds, UiMarker.DefaultDiameterPixels);
            return result;
        }

        private Dictionary<string, object> BuildStatus()
        {
            var status = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["modVersion"] = ModVersion,
                ["pid"] = Environment.ProcessId,
                ["instanceId"] = m_config.InstanceId,
                ["screen"] = UiInspector.ScreenName(),
                ["screenType"] = ScreensManager.CurrentScreen != null
                    ? ScreensManager.CurrentScreen.GetType().Name
                    : null,
                ["animating"] = ScreensManager.IsAnimating,
                ["frameIndex"] = Time.FrameIndex,
                ["worldLoaded"] = GameManager.Project != null,
                ["inputInjection"] = m_config.EnableInputInjection,
                // 注入前 UI 检查的累计读数（本次运行）：一共替自动化关掉了多少挡路的界面。
                // 单次命令的明细在回包的 `dismissedUi` 里；这里只给总量，便于"到底有没有生效"的复核。
                ["uiDismissed"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["dialogs"] = Volatile.Read(ref m_uiDismissedDialogs),
                    ["modalPanels"] = Volatile.Read(ref m_uiDismissedModalPanels),
                    ["hudPanels"] = Volatile.Read(ref m_uiDismissedHudPanels),
                    ["total"] = Volatile.Read(ref m_uiDismissedDialogs)
                        + Volatile.Read(ref m_uiDismissedModalPanels)
                        + Volatile.Read(ref m_uiDismissedHudPanels)
                }
            };

            ContainerWidget root = ScreensManager.RootWidget;
            bool layoutValid = false;
            if (root != null)
            {
                BoundingRectangle bounds = root.GlobalBounds;
                layoutValid = bounds.Max.X - bounds.Min.X > 1f && bounds.Max.Y - bounds.Min.Y > 1f;
            }
            status["layoutValid"] = layoutValid;

            try
            {
                Point2 position = Window.Position;
                Point2 size = Window.Size;
                status["window"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["position"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = position.X,
                        ["y"] = position.Y
                    },
                    ["clientSize"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = size.X,
                        ["y"] = size.Y
                    }
                };
            }
            catch
            {
                status["window"] = null;
            }

            try
            {
                status["viewport"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = Display.Viewport.Width,
                    ["y"] = Display.Viewport.Height
                };
            }
            catch
            {
                status["viewport"] = null;
            }

            try
            {
                status["uiScale"] = SettingsManager.UIScale;
                status["upsideDownLayout"] = SettingsManager.UpsideDownLayout;
            }
            catch
            {
            }

            try
            {
                status["serverError"] = null;
            }
            catch
            {
            }

            return status;
        }

        private Dictionary<string, object> BuildSnapshot(BridgeRequest request)
        {
            var snapshot = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["status"] = BuildStatus(),
                ["ui"] = UiInspector.Describe(
                    m_config,
                    request.GetBoolean("all", false),
                    request.GetInteger("maxElements", m_config.MaxElements),
                    request.GetString("filter", null)),
                ["player"] = PlayerObserver.Describe(),
                ["input"] = PlayerObserver.DescribeRawInput(),
                ["aim"] = AimObserver.Describe(request.GetFloat("maxDistance", 8f)),
                ["world"] = GameManager.Project != null ? WorldObserver.DescribeTime() : null,
                ["messages"] = MessageObserver.Describe(),
                ["dialogs"] = DescribeDialogs(),
                ["events"] = m_recorder.Read(
                    request.GetInteger("sinceSeq", 0),
                    request.GetInteger("maxEvents", 64))
            };
            return snapshot;
        }

        /// <summary>
        /// 区域方块扫描。未给中心时以玩家所在格为中心，避免 AI 必须自己换算格子坐标。
        /// </summary>
        private Dictionary<string, object> DescribeBlocks(BridgeRequest request)
        {
            int x = 0;
            int y = 0;
            int z = 0;
            bool hasCenter = request.TryGetInteger("x", out x) &&
                request.TryGetInteger("y", out y) &&
                request.TryGetInteger("z", out z);
            if (!hasCenter)
            {
                ComponentPlayer player = GetPlayerOrNull();
                if (player == null)
                    throw new BridgeCommandException("world_not_loaded", "No player is available.");
                Vector3 position = player.ComponentBody.Position;
                x = Terrain.ToCell(position.X);
                y = Terrain.ToCell(position.Y);
                z = Terrain.ToCell(position.Z);
            }
            return WorldObserver.DescribeBlocks(
                x, y, z,
                request.GetInteger("radius", 4),
                request.GetInteger("max", 256));
        }

        private static ComponentPlayer GetPlayerOrNull()
        {
            if (GameManager.Project == null)
                return null;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || players.ComponentPlayers.Count == 0)
                return null;
            return players.ComponentPlayers[0];
        }

        // ---------------------------------------------------------------- 聊天注入（SuMultiplayer）

        /// <summary>`chat.open`：打开聊天输入行；已打开时幂等成功。</summary>
        private static object ChatOpen()
        {
            SuChatAutomation chat = SuChatAutomation.Resolve();
            bool alreadyOpen = chat.IsChatInputOpen;
            chat.OpenChatInput();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["open"] = chat.IsChatInputOpen,
                ["alreadyOpen"] = alreadyOpen,
                ["inSession"] = chat.IsInSession,
                ["hook"] = "SuMultiplayer.SuMultiplayerMod.ChatAutomation"
            };
        }

        /// <summary>
        /// `chat.text`：只追加到**已经打开**的输入行（等价于往输入框里打字）。
        /// 没打开 → chat_not_open；空 / 超长 / 原生软键盘占用 → chat_inject_failed。
        /// </summary>
        private static object ChatText(BridgeRequest request)
        {
            string text = request.GetString("text", null);
            if (text == null)
            {
                throw new BridgeCommandException("invalid_argument",
                    "text is required (use: chat.text text=你好 or sccmd chat.text 你好).");
            }
            SuChatAutomation chat = SuChatAutomation.Resolve();
            if (!chat.IsChatInputOpen)
            {
                throw new BridgeCommandException("chat_not_open",
                    "The SuMultiplayer chat input line is closed. Run chat.open first "
                    + "(or use chat.send, which opens + injects + submits in one command).");
            }
            chat.InjectChatText(text);
            string inputText = chat.CurrentChatInputText;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["injected"] = text,
                ["inputText"] = inputText,
                ["utf16Length"] = inputText.Length,
                ["open"] = chat.IsChatInputOpen
            };
        }

        /// <summary>
        /// `chat.submit`：把当前输入缓冲区按回车路径提交（不追加文本）。
        /// 没打开 → chat_not_open；空消息 / 限速 / 主机拒绝 → 明确失败。
        /// </summary>
        private static object ChatSubmit()
        {
            SuChatAutomation chat = SuChatAutomation.Resolve();
            if (!chat.IsChatInputOpen)
            {
                throw new BridgeCommandException("chat_not_open",
                    "The SuMultiplayer chat input line is closed; run chat.open first.");
            }
            string text = chat.CurrentChatInputText;
            chat.SubmitChatInput();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sent"] = true,
                ["text"] = text,
                ["utf16Length"] = text.Length,
                ["inSession"] = chat.IsInSession,
                ["note"] = "submitted through ChatModule.TrySendLocalText; "
                    + "the sender still waits for the host echo"
            };
        }

        /// <summary>
        /// `chat.send`：打开（若还没打开）→ 注入 → 提交，最终走 ChatModule 的真实发送链。
        /// 失败时如果输入行是这条命令打开的，会先关掉，保持可重试的稳定状态。
        /// </summary>
        private static object ChatSend(BridgeRequest request)
        {
            string text = request.GetString("text", null);
            if (text == null)
            {
                throw new BridgeCommandException("invalid_argument",
                    "text is required (use: chat.send text=你好 or sccmd chat.send 你好).");
            }
            SuChatAutomation chat = SuChatAutomation.Resolve();
            bool openedHere = false;
            if (!chat.IsChatInputOpen)
            {
                chat.OpenChatInput();
                openedHere = true;
            }
            try
            {
                chat.InjectChatText(text);
                string sanitized = chat.CurrentChatInputText;
                chat.SubmitChatInput();
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["sent"] = true,
                    ["text"] = sanitized,
                    ["utf16Length"] = sanitized.Length,
                    ["openedByCommand"] = openedHere,
                    ["inSession"] = chat.IsInSession,
                    ["note"] = "submitted through ChatModule.TrySendLocalText; "
                        + "the sender still waits for the host echo"
                };
            }
            catch (BridgeCommandException failure)
            {
                if (openedHere && chat.IsChatInputOpen)
                {
                    try
                    {
                        chat.CancelChatInput();
                    }
                    catch (BridgeCommandException cleanup)
                    {
                        throw new BridgeCommandException(failure.Code,
                            failure.Message + " (additionally, closing the chat input failed: "
                            + cleanup.Message + ")");
                    }
                }
                throw;
            }
        }

        /// <summary>`chat.cancel`：关闭输入行并清空缓冲区（等价按 Esc）；没打开 → chat_not_open。</summary>
        private static object ChatCancel()
        {
            SuChatAutomation chat = SuChatAutomation.Resolve();
            if (!chat.IsChatInputOpen)
            {
                throw new BridgeCommandException("chat_not_open",
                    "The SuMultiplayer chat input line is already closed; nothing to cancel.");
            }
            chat.CancelChatInput();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["cancelled"] = true,
                ["open"] = chat.IsChatInputOpen
            };
        }

        private List<Dictionary<string, object>> DescribeDialogs()
        {
            var dialogs = new List<Dictionary<string, object>>();
            try
            {
                ReadOnlyList<Dialog> all = DialogsManager.Dialogs;
                for (int i = 0; i < all.Count; i++)
                {
                    Dialog dialog = all[i];
                    dialogs.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["index"] = i,
                        ["type"] = dialog.GetType().Name,
                        ["parent"] = dialog.ParentWidget != null
                            ? dialog.ParentWidget.GetType().Name
                            : null
                    });
                }
            }
            catch
            {
            }
            return dialogs;
        }

        /// <summary>
        /// 自检：确认白名单注入点全部可用（不写入任何值），并复查可达性统计。
        /// 用于 P2 阶段验证"帧首缝隙 + 注入点"是否成立。
        /// </summary>
        private Dictionary<string, object> SelfTest()
        {
            var checks = new List<Dictionary<string, object>>();
            IModParentField fields = ModManager.Instance.ModParentField;

            AddCheck(checks, "Keyboard." + InputWhitelist.KeyboardDownArray, () =>
            {
                bool[] array = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Keyboard." + InputWhitelist.KeyboardDownOnceArray, () =>
            {
                bool[] array = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownOnceArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Keyboard." + InputWhitelist.KeyboardRepeatArray, () =>
            {
                double[] array = fields.GetStaticField<double[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardRepeatArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Mouse." + InputWhitelist.MouseDownArray, () =>
            {
                bool[] array = fields.GetStaticField<bool[]>(
                    typeof(Mouse), InputWhitelist.MouseDownArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Mouse." + InputWhitelist.MouseDownOnceArray, () =>
            {
                bool[] array = fields.GetStaticField<bool[]>(
                    typeof(Mouse), InputWhitelist.MouseDownOnceArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Mouse." + InputWhitelist.MouseWheelMovement, () =>
            {
                object value = fields.GetStaticField(
                    typeof(Mouse), InputWhitelist.MouseWheelMovement);
                return value is int;
            });
            AddCheck(checks, "Mouse." + InputWhitelist.MouseLastWheelValue, () =>
            {
                object value = fields.GetStaticField(
                    typeof(Mouse), InputWhitelist.MouseLastWheelValue);
                return value == null || value is int;
            });
            AddCheck(checks, "ScreensManager.m_screens", () =>
            {
                Dictionary<string, Screen> screens = fields.GetStaticField<Dictionary<string, Screen>>(
                    typeof(ScreensManager), "m_screens");
                return screens != null;
            });
            AddCheck(checks, "window.position", () =>
            {
                Point2 ignored = Window.Position;
                return true;
            });

            // 扩展命令注册表：上层 Mod（如 PlayerAiMod 的 ai.*）靠它挂命令，不该出现"注册不上/摘不掉"。
            AddCheck(checks, "commands.register", () =>
            {
                string error;
                bool ok = m_injector.Commands.Register("selftest.probe", delegate { return null; },
                    "CmdBridgeMod", "selftest probe", false, out error);
                CommandExtension probe;
                return ok && m_injector.Commands.TryGet("selftest.probe", out probe);
            });
            AddCheck(checks, "commands.duplicateRejected", () =>
            {
                string error;
                bool again = m_injector.Commands.Register("selftest.probe", delegate { return null; },
                    "someone.else", null, false, out error);
                return !again && !string.IsNullOrEmpty(error);
            });
            AddCheck(checks, "commands.ownerGuard", () =>
            {
                string error;
                CommandExtension probe;
                bool stolen = m_injector.Commands.Unregister("selftest.probe", "someone.else", out error);
                return !stolen && !string.IsNullOrEmpty(error)
                    && m_injector.Commands.TryGet("selftest.probe", out probe);
            });
            AddCheck(checks, "commands.unregisterAll", () =>
            {
                int removed = m_injector.Commands.UnregisterAll("CmdBridgeMod");
                CommandExtension probe;
                return removed >= 1 && !m_injector.Commands.TryGet("selftest.probe", out probe);
            });
            AddCheck(checks, "commands.builtInListed", () =>
            {
                for (int i = 0; i < BuiltInCommands.Length; i++)
                {
                    if (string.Equals(BuiltInCommands[i], "cmd.list", StringComparison.Ordinal))
                        return true;
                }
                return false;
            });

            // ---- 聊天观察（只读）：切分 / 去重 / seq / 自检不污染消费方 ----
            //
            // 这几条都是"静默失效最贵"的地方：切错名字 → AI 听不懂；去重错 → 重复触发动作；
            // seq 归零 → 消费方永远收不到新消息（自检跑完就聋了）。
            AddCheck(checks, "ChatObserver.split.playerNameWithSpace", () =>
            {
                string sender, text;
                bool isSystem;
                bool ok = ChatObserver.TrySplitForTest("Android User: 过来",
                    new[] { "Android User", "Basil", "ScMP" }, out sender, out text, out isSystem);
                return ok && sender == "Android User" && text == "过来" && !isSystem;
            });
            AddCheck(checks, "ChatObserver.split.longestNameWins", () =>
            {
                string sender, text;
                bool isSystem;
                bool ok = ChatObserver.TrySplitForTest("Basil Two: hi",
                    new[] { "Basil", "Basil Two" }, out sender, out text, out isSystem);
                return ok && sender == "Basil Two" && text == "hi";
            });
            AddCheck(checks, "ChatObserver.split.systemSenderIsChat", () =>
            {
                string sender, text;
                bool isSystem;
                bool ok = ChatObserver.TrySplitForTest("ScMP: 这块区域属于 Android User（领地 #11），修改已否决",
                    new[] { "Basil", "ScMP" }, out sender, out text, out isSystem);
                return ok && sender == "ScMP" && isSystem && text.StartsWith("这块区域", StringComparison.Ordinal);
            });
            AddCheck(checks, "ChatObserver.split.gameHintIsNotChat", () =>
            {
                string sender, text;
                bool isSystem;
                return !ChatObserver.TrySplitForTest("You will faint, go to sleep!",
                    new[] { "Basil", "ScMP" }, out sender, out text, out isSystem);
            });
            AddCheck(checks, "ChatObserver.split.unknownSenderStillYieldsText", () =>
            {
                string sender, text;
                bool isSystem;
                bool ok = ChatObserver.TrySplitForTest("Stranger: come here",
                    new[] { "Basil" }, out sender, out text, out isSystem);
                return ok && sender == "Stranger" && text == "come here" && !isSystem;
            });
            AddCheck(checks, "ChatObserver.seq.oneBubbleSeenManyFramesCountsOnce", () =>
            {
                long before = ChatObserver.Sequence;
                string[] frame = { "Basil: 过来", "ScMP: hello" };
                string[] names = { "Basil", "ScMP" };
                ChatObserver.FeedForTest(frame, names);   // 第 1 帧：2 条新
                long afterFirst = ChatObserver.Sequence;
                ChatObserver.FeedForTest(frame, names);   // 第 2..4 帧：一条都不该重复
                ChatObserver.FeedForTest(frame, names);
                ChatObserver.FeedForTest(frame, names);
                return afterFirst == before + 2 && ChatObserver.Sequence == afterFirst;
            });
            AddCheck(checks, "ChatObserver.seq.sameTextAfterLeavingIsNew", () =>
            {
                long before = ChatObserver.Sequence;
                ChatObserver.FeedForTest(new string[0], new[] { "Basil" });        // 气泡消失
                ChatObserver.FeedForTest(new[] { "Basil: 过来" }, new[] { "Basil" });
                return ChatObserver.Sequence == before + 1;
            });
            AddCheck(checks, "ChatObserver.self.ownMessageIsFlagged", () =>
            {
                ChatObserver.SetLocalNameForTest("Basil");
                ChatObserver.FeedForTest(new[] { "Basil: 我发的" }, new[] { "Basil" });
                ChatObserver.Line line = ChatObserver.RecentForTest(0);
                return line != null && line.IsSelf && line.Text == "我发的";
            });
            AddCheck(checks, "*** ChatObserver.reset.keepsSeqMonotonic (consumers must not go deaf) ***", () =>
            {
                long before = ChatObserver.Sequence;
                ChatObserver.Reset();
                return ChatObserver.Sequence == before && ChatObserver.RecentCountForTest() == 0;
            });

            bool injectionPointOk = true;
            if (GameManager.Project != null)
            {                try
                {
                    SubsystemPlayers players =
                        GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
                    if (players != null && players.ComponentPlayers.Count > 0)
                    {
                        ComponentLocomotion locomotion = players.ComponentPlayers[0].ComponentLocomotion;
                        AddCheck(checks, "ComponentLocomotion." + InputWhitelist.LocomotionLookAngles, () =>
                        {
                            Vector2 angles = fields.GetParentField<Vector2>(
                                locomotion,
                                InputWhitelist.LocomotionLookAngles,
                                typeof(ComponentLocomotion));
                            return true;
                        });
                    }
                }
                catch
                {
                    injectionPointOk = false;
                }
            }

            bool allOk = injectionPointOk;
            for (int i = 0; i < checks.Count; i++)
            {
                if (!(bool)checks[i]["ok"])
                    allOk = false;
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["allOk"] = allOk,
                ["checks"] = checks,
                ["whitelist"] = InputWhitelist.All,
                ["reachability"] = UiInspector.Reachability(m_config)
            };
        }

        private static void AddCheck(
            List<Dictionary<string, object>> checks, string name, Func<bool> probe)
        {
            var entry = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = name
            };
            try
            {
                entry["ok"] = probe();
            }
            catch (Exception exception)
            {
                entry["ok"] = false;
                entry["error"] = exception.GetType().Name + ": " + exception.Message;
            }
            checks.Add(entry);
        }
    }
}



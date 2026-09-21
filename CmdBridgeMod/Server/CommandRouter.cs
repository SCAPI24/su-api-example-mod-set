using Engine;
using Engine.Graphics;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// 命令分发。obs.* 只读；act.* 走输入注入白名单。
    /// 所有触碰游戏对象的操作都在游戏线程（下一帧帧首）执行。
    /// </summary>
    internal sealed class CommandRouter
    {
        internal const string ModVersion = "1.0.0";

        /// <summary>
        /// 内建命令名（`cmd.list` 用）。加命令时**必须同步这里**：
        /// 它是 CLI 帮助与"这个实例支持哪些命令"的唯一清单（扩展命令从注册表读）。
        /// </summary>
        internal static readonly string[] BuiltInCommands =
        {
            "ping", "status", "cmd.list", "obs.selftest",
            "obs.snapshot", "obs.ui", "obs.player", "obs.input", "obs.aim", "obs.events",
            "obs.world.blocks", "obs.world.entities", "obs.world.time", "obs.messages", "obs.dialogs",
            "obs.waitfor", "ui.elements", "ui.reachability",
            "act.look", "act.lookdelta", "act.lookat", "act.key", "act.hold", "act.chord",
            "act.mouse", "act.wheel", "act.uiclick", "act.text", "act.releaseall",
            "ui.locate", "ui.clickelement", "ui.marker",
            "ui.session.begin", "ui.session.end", "ui.session.status",
            "ui.cursor", "ui.press", "ui.release", "ui.move", "ui.click",
            "ui.rightclick", "ui.shiftclick", "ui.drag", "ui.split",
            "focus.status", "focus.auto", "focus.follow", "focus.attach", "focus.detach", "focus.recover",
            "focus.merge", "focus.lookhold", "look.owner", "hotkey.status"
        };

        // 条件等待：40ms 轮询一次（远快于人类的反应，也远慢于每帧，避免给游戏线程添压力）。
        private const int WaitPollIntervalMs = 40;
        private const int WaitMaxTimeoutMs = 120000;

        private readonly CmdBridgeConfig m_config;
        private readonly GameThreadInvoker m_invoker;
        private readonly InputInjector m_injector;
        private readonly EventRecorder m_recorder;

        public CommandRouter(
            CmdBridgeConfig config,
            GameThreadInvoker invoker,
            InputInjector injector,
            EventRecorder recorder)
        {
            m_config = config;
            m_invoker = invoker;
            m_injector = injector;
            m_recorder = recorder;
        }

        public object Execute(BridgeRequest request)
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
                        request.GetString("filter", null)));
                case "obs.player":
                    return OnGameThread(PlayerObserver.Describe);
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
                    return OnGameThread(() => WorldObserver.DescribeEntities(
                        request.GetFloat("radius", 64f),
                        request.GetInteger("max", 64)));
                case "obs.world.time":
                    return OnGameThread(WorldObserver.DescribeTime);
                case "obs.messages":
                    return OnGameThread(MessageObserver.Describe);
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
                    return m_injector.KeyPulse(
                        request.GetString("key", null), request.GetInteger("holdMs", 0));
                case "act.hold":
                    return m_injector.KeyHold(
                        request.GetString("key", null), request.GetBoolean("down", true));
                case "act.chord":
                    return m_injector.KeyChord(
                        request.GetStringArray("modifiers").ToArray(),
                        request.GetString("key", null),
                        request.GetInteger("holdMs", 0));
                case "act.mouse":
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
                    // 纯选择器点击优先 direct（单帧合成 Tap+Click，写在目标控件自己的输入面）：
                    // 世界内 GameWidget 层级下 CM-1 会话派生不出 Click ——
                    // `ComponentInput.UpdateInputFromMouseAndKeyboard` 每帧重写该层输入状态，
                    // GameMenuDialog 的按钮用会话点毫无反应、用 direct 立刻回到主菜单（实测）。
                    // 带坐标、列表行、或显式 `mode=input` 时仍走会话，行为不变。
                    string directSelector = request.GetString("selector", null);
                    string clickMode = request.GetString("mode", null);
                    if (!hasPoint && rowIndex < 0 && string.IsNullOrEmpty(rowText)
                        && !string.IsNullOrEmpty(directSelector)
                        && !string.Equals(clickMode, "input", StringComparison.OrdinalIgnoreCase))
                    {
                        return m_injector.Ui.Click(directSelector, "direct",
                            request.GetInteger("holdMs", 0), false,
                            UiMarker.DefaultSeconds, UiMarker.DefaultDiameterPixels);
                    }
                    // 走 CM-1 会话（一步一帧）：直注入的"按下+抬起"会落在同一帧，界面纹丝不动。
                    return m_injector.UiClickSession(
                        request.GetString("selector", null),
                        hasPoint, clickX, clickY, rowIndex, rowText,
                        request.GetInteger("holdMs", 0));
                }
                case "act.text":
                    return m_injector.TypeText(request.GetString("text", string.Empty));
                case "act.releaseall":
                case "act.releaseAll":
                    return m_injector.ReleaseAll();

                // ------------------------------------------------ UI 定位 / 点击**服务**（UI-1）
                // 用户要求："把相应的方法做成 CmdBridgeMod 能提供的服务，在行为树编辑器中，
                // 要能够使用来获取坐标或点击对象"——编辑器的 `/api/game/ui/*` 与行为树
                // `Task.UiClick` 都转发到这两条命令，于是"编辑器里试一下"和"树里跑一下"
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
                        request.GetFloat("markPx", UiMarker.DefaultDiameterPixels));
                case "ui.clickelement":
                    return m_injector.Ui.Click(
                        request.GetString("target", request.GetString("selector", null)),
                        request.GetString("mode", "direct"),
                        request.GetInteger("holdMs", 0),
                        request.GetBoolean("mark", false),
                        request.GetFloat("markMs", UiMarker.DefaultSeconds * 1000f) / 1000f,
                        request.GetFloat("markPx", UiMarker.DefaultDiameterPixels));
                // 落点标记的自查（只读）：`drawFrames > 0` 证明引擎真的调用过它的 Draw，
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
                // 引擎侧实测语义：左下 `Move` 区按住再拖 = 移动；无按钮区按住拖 = 视角；
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
                    // 成败判定：挖前/挖后各取一次"命中观测"与该格**真实地形值**，
                    // removed=true 表示这一处确实变了；nowAir=true 表示整格清空
                    // （导线按面存储：只掉一面时 removed=true 而 nowAir=false）。
                    ContainerWidget root = ScreensManager.RootWidget;
                    float px = request.GetFloat("x", root != null ? root.ActualSize.X * 0.5f : 1000f);
                    float py = request.GetFloat("y", root != null ? root.ActualSize.Y * 0.5f : 600f);
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
                    SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
                    int beforeValue = hasCell
                        ? terrain.Terrain.GetCellValue(cellX, cellY, cellZ) : 0;
                    m_injector.Session.Begin(false);
                    m_injector.Session.MoveTo(new Vector2(px, py), 1);
                    m_injector.Session.Press(MouseButton.Left);
                    int digFrames = Math.Max(1, holdMs / 16);
                    for (int i = 0; i < digFrames; i++)
                        m_injector.Session.MoveTo(new Vector2(px, py), 1);
                    m_injector.Session.Release(MouseButton.Left);
                    m_injector.Session.End();
                    m_injector.Session.WaitUntilIdle(3000 + holdMs);
                    int afterValue = hasCell && terrain != null
                        ? terrain.Terrain.GetCellValue(cellX, cellY, cellZ) : 0;
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
                        ["cell"] = hasCell
                            ? cellX + "," + cellY + "," + cellZ : null,
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
                        + "  act.dig   [x= y=] [holdMs=34] [maxDistance=8]      -> dig ONE block at the crosshair (Windows) / touch point (Android);"
                        + " returns cell/beforeValue/afterValue/removed/nowAir. Creative dig time is 0, so holding longer digs a CHAIN.\r\n"
                        + "  act.jump  pad=Move|Look holdMs=140                 -> quick tap on Move (or Look) = jump\r\n"
                        + "  act.look  yawDeg=.. pitchDeg=..  / lookdelta dYaw dPitch -> turn the camera (engine level, precise)\r\n"
                        + "  touch-drag look: ui.session.begin; ui.move x y (center, button-free); ui.press; ui.move ...; ui.release; ui.session.end\r\n"
                        + "  dig                                                -> hold still (~0.2-0.5s) in a button-free area\r\n"
                        + "  low level: ui.session.begin; ui.move x y (LOCATE FIRST); ui.press; ui.move ...; ui.release; ui.session.end\r\n"
                        + "  ui.press/ui.release also accept x/y now (they locate before pressing/releasing).\r\n"
                        + "  preconditions: game in foreground, soft keyboard hidden; Android draws no cursor.";

                case "ui.click":
                {
                    string selector = request.GetString("selector", request.GetString("target", null));
                    if (string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.click needs a selector.");
                    string clickMode = request.GetString("mode", "direct");
                    float clickX;
                    float clickY;
                    bool hasPoint = request.TryGetFloat("x", out clickX)
                        && request.TryGetFloat("y", out clickY);
                    // 纯选择器点击优先走 direct（单帧合成 Tap+Click，写在目标控件自己的输入面）：
                    // 世界内 GameWidget 层级下老的软光标多帧会话派生不出 Click ——
                    // `ComponentInput.UpdateInputFromMouseAndKeyboard` 每帧重写该层输入状态，
                    // GameMenuDialog 的按钮用会话点完全没反应、用 direct 立刻回到主菜单（实测）。
                    // 带坐标的点击（虚拟列表行/坐标点击）与显式 `mode=input` 仍走会话，行为不变。
                    if (!hasPoint && !string.Equals(clickMode, "input", StringComparison.OrdinalIgnoreCase))
                    {
                        return m_injector.Ui.Click(selector, "direct",
                            request.GetInteger("holdMs", 0), request.GetBoolean("mark", false),
                            request.GetFloat("markMs", UiMarker.DefaultSeconds * 1000f) / 1000f,
                            request.GetFloat("markPx", UiMarker.DefaultDiameterPixels));
                    }
                    return UiSessionCommand(request, () => m_injector.Session.Click(selector));
                }
                case "ui.rightclick":
                {
                    string selector = request.GetString("selector", null);
                    if (string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.rightclick needs a selector.");
                    return UiSessionCommand(request, () => m_injector.Session.RightClick(selector));
                }
                case "ui.shiftclick":
                {
                    string selector = request.GetString("selector", null);
                    if (string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.shiftclick needs a selector.");
                    return UiSessionCommand(request, () => m_injector.Session.ShiftClick(selector));
                }
                case "ui.drag":
                case "ui.split":
                {
                    int steps = request.GetInteger("steps", 8);
                    int holdMs = request.GetInteger("holdMs", request.Command == "ui.split" ? 600 : 0);
                    float x1 = 0f;
                    float y1 = 0f;
                    float x2 = 0f;
                    float y2 = 0f;
                    bool hasPoints = request.TryGetFloat("x1", out x1) && request.TryGetFloat("y1", out y1)
                        && request.TryGetFloat("x2", out x2) && request.TryGetFloat("y2", out y2);
                    string from = request.GetString("from", null);
                    string to = request.GetString("to", null);
                    if (!hasPoints && (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)))
                    {
                        throw new BridgeCommandException(
                            "invalid_argument", "ui.drag needs x1/y1/x2/y2 or from/to selectors.");
                    }

                    UiMouseSession.Endpoint start = hasPoints
                        ? UiMouseSession.Endpoint.At(new Vector2(x1, y1))
                        : UiMouseSession.Endpoint.At(from);
                    UiMouseSession.Endpoint end = hasPoints
                        ? UiMouseSession.Endpoint.At(new Vector2(x2, y2))
                        : UiMouseSession.Endpoint.At(to);
                    return UiSessionCommand(request,
                        () => m_injector.Session.Drag(start, end, steps, holdMs));
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
                    // 一键自救：万一焦点策略把鼠标卡住了（视角转不动 / 光标跑到程序外），
                    // 这条命令立刻回到"跟随引擎"并把窗口状态恢复成真实值。
                    m_injector.Focus.Mode = FocusMode.Follow;
                    m_injector.Focus.RestoreNaturalFocus();
                    Log.Information("[CmdBridge] focus recovered to follow mode");
                    return m_injector.Focus.Describe();
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

        /// <summary>
        /// 执行扩展命令。默认在**游戏线程**执行（触碰游戏对象/输入的命令必须如此）；
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
                ["inputInjection"] = m_config.EnableInputInjection
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

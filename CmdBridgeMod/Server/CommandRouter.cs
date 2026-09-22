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
    /// 鍛戒护鍒嗗彂銆俹bs.* 鍙锛沘ct.* 璧拌緭鍏ユ敞鍏ョ櫧鍚嶅崟銆?
    /// 鎵€鏈夎Е纰版父鎴忓璞＄殑鎿嶄綔閮藉湪娓告垙绾跨▼锛堜笅涓€甯у抚棣栵級鎵ц銆?
    /// </summary>
    internal sealed class CommandRouter
    {
        internal const string ModVersion = "1.1.10";

        /// <summary>
        /// 鍐呭缓鍛戒护鍚嶏紙`cmd.list` 鐢級銆傚姞鍛戒护鏃?*蹇呴』鍚屾杩欓噷**锛?
        /// 瀹冩槸 CLI 甯姪涓?杩欎釜瀹炰緥鏀寔鍝簺鍛戒护"鐨勫敮涓€娓呭崟锛堟墿灞曞懡浠や粠娉ㄥ唽琛ㄨ锛夈€?
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
            "focus.merge", "focus.lookhold", "look.owner", "hotkey.status",
            "jump.status", "jump.buffer"
        };

        // 鏉′欢绛夊緟锛?0ms 杞涓€娆★紙杩滃揩浜庝汉绫荤殑鍙嶅簲锛屼篃杩滄參浜庢瘡甯э紝閬垮厤缁欐父鎴忕嚎绋嬫坊鍘嬪姏锛夈€?
        private const int WaitPollIntervalMs = 40;
        private const int WaitMaxTimeoutMs = 120000;

        /// <summary>
        /// 閿洏鑴夊啿鍛戒护锛坄act.key` / `act.chord`锛夌殑 holdMs 榛樿鍊硷細
        /// **涓€甯ч噺绾?*锛?0fps 涓€甯?鈮?16.7ms锛夛紝鍥哄畾鍊硷紝涓嶉殢鏃堕挓鎶栧姩銆?
        ///
        /// 涓轰粈涔堥粯璁や笉鑳芥槸 0锛歱ress / release 鏄袱娆＄嫭绔嬬殑 `Dispatcher.Dispatch`
        /// 锛坄GameThreadInvoker.cs:56-66`锛夛紝璺ㄤ笉杩囧抚杈圭晫鏃朵細鍦?*鍚屼竴娆?* `Dispatcher.BeforeFrame`
        /// 鎵规閲岃繛缁墽琛岋紙`Engine/Engine/Dispatcher.cs:79-105`锛夛紝浜庢槸鏁翠釜甯т綋閲?
        /// `IsKeyDown` 鎭掍负 false 鈥斺€?鍙湁 `IsKeyDownOnce` 娲讳笅鏉?
        /// 锛堢Щ鍔ㄧ被娑堣垂鑰呰鐨勫氨鏄?`IsKeyDown`锛歚Survivalcraft/Game/ComponentInput.cs:172-177`锛夈€?
        /// 40ms 瓒冲璁╁抚浣撹嚦灏戠湅鍒颁竴娆℃寜浣忥紝鍙堢煭鍒颁笉鏀瑰彉"杞荤偣涓€涓?鐨勬墜鎰熴€?
        /// **璋冪敤鏂规樉寮忎紶 holdMs 鏃跺師鏍峰皧閲?*锛堝惈鏄惧紡 0锛夈€?
        /// `act.mouse` / `act.uiclick` 涓嶈蛋杩欓噷锛屽畠浠殑榛樿鍊间繚鎸?0銆?
        /// </summary>
        private const int DefaultKeyPulseHoldMs = InputInjector.DefaultPulseHoldMs;

        private readonly CmdBridgeConfig m_config;
        private readonly GameThreadInvoker m_invoker;
        private readonly InputInjector m_injector;
        private readonly EventRecorder m_recorder;
        private readonly JumpAssist m_jumpAssist;

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
        }

        public object Execute(BridgeRequest request)
        {
            switch (request.Command)
            {
                // ---------------------------------------------------------- 鍙
                case "ping":
                    // ping 瀹屽叏鍦ㄦ湇鍔″櫒绾跨▼鍥炵瓟锛氬惎鍔ㄧ灛闂翠篃鑳界敤锛屾濂戒綔涓?娓告垙鏄惁鍙帴鍙楀懡浠?鐨勬帰閽堛€?
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

                // ---------------------------------------------------------- 鍔ㄤ綔锛堣緭鍏ュ眰锛?
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
                    // 鏈樉寮忕粰 holdMs 鏃剁敤涓€甯ч噺绾х殑榛樿鍊硷紙40ms锛夛紱
                    // 鏄惧紡浼?holdMs=0 浠嶇劧灏婇噸锛屼笉浼氬伔鍋锋浛鎹㈡垚 40銆?
                    return m_injector.KeyPulse(
                        request.GetString("key", null),
                        request.GetInteger("holdMs", DefaultKeyPulseHoldMs));
                case "act.hold":
                    return m_injector.KeyHold(
                        request.GetString("key", null), request.GetBoolean("down", true));
                case "act.chord":
                    // 涓?`act.key` 瀹屽叏鍚屾簮锛圧2锛夛細press/release 鏄袱娆＄嫭绔?Dispatch锛?
                    // holdMs=0 鏃朵細鍦ㄥ悓涓€娆?Dispatcher.BeforeFrame 閲屾寜涓嬪張鎶捣锛屽抚浣撶湅涓嶅埌鎸変笅銆?
                    // 鍥犳鍏辩敤鍚屼竴涓竴甯ч噺绾ч粯璁ゅ€硷紱鏄惧紡浼?0 浠嶇劧灏婇噸銆?
                    return m_injector.KeyChord(
                        request.GetStringArray("modifiers").ToArray(),
                        request.GetString("key", null),
                        request.GetInteger("holdMs", DefaultKeyPulseHoldMs));
                case "act.mouse":
                    // 娉ㄦ剰锛歚act.mouse` 鐨?holdMs 榛樿鍊?*淇濇寔 0 涓嶅彉**锛堟湰娆′笉鍦ㄨ寖鍥村唴锛夈€?
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
                    // 鍒楄〃琛岋細`row=3` 鎴?`text=涓栫晫鍚峘 鈥斺€?鍧愭爣鍦ㄧ偣鍑昏繖涓€鍒荤幇绠楋紝
                    // 褰曞埗绔洜姝よ兘璁颁笅"鍝竴琛?鑰屼笉鏄?鍝釜鍍忕礌"锛堢敤鎴疯姹傦細鏀逛簡绐楀彛澶у皬涔熷埆鐐圭┖锛夈€?
                    int rowIndex = request.GetInteger("row", -1);
                    string rowText = request.GetString("text", null);
                    // 绾€夋嫨鍣ㄧ偣鍑讳紭鍏?direct锛堝崟甯у悎鎴?Tap+Click锛屽啓鍦ㄧ洰鏍囨帶浠惰嚜宸辩殑杈撳叆闈級锛?
                    // 涓栫晫鍐?GameWidget 灞傜骇涓?CM-1 浼氳瘽娲剧敓涓嶅嚭 Click 鈥斺€?
                    // `ComponentInput.UpdateInputFromMouseAndKeyboard` 姣忓抚閲嶅啓璇ュ眰杈撳叆鐘舵€侊紝
                    // GameMenuDialog 鐨勬寜閽敤浼氳瘽鐐规鏃犲弽搴斻€佺敤 direct 绔嬪埢鍥炲埌涓昏彍鍗曪紙瀹炴祴锛夈€?
                    // 甯﹀潗鏍囥€佸垪琛ㄨ銆佹垨鏄惧紡 `mode=input` 鏃朵粛璧颁細璇濓紝琛屼负涓嶅彉銆?
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
                    // 璧?CM-1 浼氳瘽锛堜竴姝ヤ竴甯э級锛氱洿娉ㄥ叆鐨?鎸変笅+鎶捣"浼氳惤鍦ㄥ悓涓€甯э紝鐣岄潰绾逛笣涓嶅姩銆?
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

                // ------------------------------------------------ UI 瀹氫綅 / 鐐瑰嚮**鏈嶅姟**锛圲I-1锛?
                // 鐢ㄦ埛瑕佹眰锛?鎶婄浉搴旂殑鏂规硶鍋氭垚 CmdBridgeMod 鑳芥彁渚涚殑鏈嶅姟锛屽湪琛屼负鏍戠紪杈戝櫒涓紝
                // 瑕佽兘澶熶娇鐢ㄦ潵鑾峰彇鍧愭爣鎴栫偣鍑诲璞?鈥斺€旂紪杈戝櫒鐨?`/api/game/ui/*` 涓庤涓烘爲
                // `Task.UiClick` 閮借浆鍙戝埌杩欎袱鏉″懡浠わ紝浜庢槸"缂栬緫鍣ㄩ噷璇曚竴涓?鍜?鏍戦噷璺戜竴涓?
                // 鏄悓涓€浠藉疄鐜帮紙涓嶄細鍑虹幇"缂栬緫鍣ㄨ兘鐐广€佸洖鏀剧偣绌?锛夈€?
                //
                // 鐩爣鍐欐硶锛堣涔変紭鍏堬紝鍧愭爣鍙槸鍏滃簳锛夛細
                //   `Play` / `[MainMenuScreen#0]/鈥?Play` / `list:WorldsList@Rebritish` / `list:WorldsList#0`
                //   `1010.6,64.83`锛堜笉鎺ㄨ崘锛氱獥鍙ｅ昂瀵镐竴鍙樺氨鐐瑰埌鍒锛?
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
                // 钀界偣鏍囪鐨勮嚜鏌ワ紙鍙锛夛細`drawFrames > 0` 璇佹槑寮曟搸鐪熺殑璋冪敤杩囧畠鐨?Draw锛?
                // 鑰屼笉鏄?鍛戒护杩斿洖浜嗐€佸睆骞曚笂浠€涔堥兘娌℃湁"銆?
                case "ui.marker":
                    return UiMarker.Describe();

                // ------------------------------------------------------ 铏氭嫙 UI 榧犳爣浼氳瘽锛圕M-1锛?
                // 鐢ㄥ紩鎿庡唴鐨勮蒋鍏夋爣鍋氱偣鍑?鎷栨嫿锛氱墿鐞嗛紶鏍囧畬鍏ㄤ笉鍔紝涔熶笉闇€瑕佺獥鍙ｅ湪鍓嶅彴銆?
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
                    // 鎺ュ彛鑷唇锛氱粰浜?x/y 灏卞厛鎸埌璇ョ偣鍐嶆寜涓嬨€備細璇濈殑 `Press` 鏈韩涓嶅甫鍧愭爣锛?
                    // 涓嶅厛瀹氫綅鐨勮瘽鎸変笅浼氳惤鍦?涓婁竴娆℃搷浣滅粨鏉熺殑浣嶇疆"锛堝疄娴嬶細绉诲姩/瑙嗚浼氫簰鎹級銆?
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

                // ------------------------------------------------ Android 璇箟鍔ㄤ綔锛堟墜鎸囪涔夛級
                // 闄岀敓 AI 鍙鐭ラ亾"寰€鍓嶈蛋 / 杞ご / 璺?锛屼笉闇€瑕佺煡閬撳潗鏍囦笌鐭╁舰锛涜鍒欒 `guide.android`銆?
                // 寮曟搸渚у疄娴嬭涔夛細宸︿笅 `Move` 鍖烘寜浣忓啀鎷?= 绉诲姩锛涙棤鎸夐挳鍖烘寜浣忔嫋 = 瑙嗚锛?
                // 鏃犳寜閽尯鎸変綇涓嶅姩 鈮?.2~0.5s = 鎸栨帢锛涘湪 `Move` / `Look` 涓婅交鐐逛竴涓?= 璺宠穬銆?
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
                        m_injector.Session.MoveTo(to, 1); // 姣忓抚閲嶇敵浣嶇疆 = 淇濇寔鎸変綇
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
                    int holdMs = request.GetInteger("holdMs", 140); // 杞荤偣涓€涓?= 璺宠穬
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
                    // 鍒涢€犳ā寮忔寲鎺樻椂闂?= 0锛坄ComponentMiner.CalculateDigTime`锛欳reative 涓斿彲鎸?鈫?0f锛夛紝
                    // 鎵€浠ュ彧鎸?鏈€灏忓抚鏁?锛涙寜涔呬細椤虹潃灏勭嚎杩炴寲涓€涓诧紙瀹炴祴锛?.36s 鎸栦簡 4~7 鏍硷級銆?
                    // 鎴愯触鍒ゅ畾锛氭寲鍓?鎸栧悗鍚勫彇涓€娆?鍛戒腑瑙傛祴"涓庤鏍?*鐪熷疄鍦板舰鍊?*锛?
                    // removed=true 琛ㄧず杩欎竴澶勭‘瀹炲彉浜嗭紱nowAir=true 琛ㄧず鏁存牸娓呯┖
                    // 锛堝绾挎寜闈㈠瓨鍌細鍙帀涓€闈㈡椂 removed=true 鑰?nowAir=false锛夈€?
                    // 榛樿钀界偣蹇呴』鏄?*灞忓箷涓績**锛岃€屽睆骞曚腑蹇冭鐢?`Window.Size`锛堝儚绱?瑙嗗彛绌洪棿锛?
                    // 鍘荤畻锛欰ndroid 涓?`Touch.Position` 涓?`Camera.ScreenToWorld` 鍚屽睘**瑙嗗彛鍍忕礌绌洪棿**
                    // 锛坄Touch.Android.cs:27-53` 鐩存帴鍙?`MotionEvent.GetX/GetY`锛夛紝瑙︽懜鐐瑰喅瀹氭寲鎺樺皠绾?
                    // 锛坄ComponentInput.cs:446-457`锛歚Dig = ScreenToWorld(瑙︽懜鐐?`锛夈€?
                    // 鑰?`root.ActualSize` 鏄?*璁捐灏哄**锛堝钩鏉垮疄娴?1000脳600锛岃鍙ｆ槸 2000脳1200锛夛紝
                    // 鎷垮畠鐨勪竴鍗?(500,300) 褰撹惤鐐癸紳灞忓箷宸︿笂瑙掞紝灏勭嚎灏勫悜澶╃┖ 鈥斺€?瀹炴祴鎸夋弧 6 绉掍竴鏍间笉鎺夛紝
                    // 鎹㈠埌 (1000,600) 鍚屼竴瑙嗚绔嬪埢鎸栨帀涓ゆ牸銆傝繖鏄?鎸栦笉鍔?鐨勭湡姝ｆ牴鍥犮€?
                    float px = request.GetFloat("x", DefaultDigPointX());
                    float py = request.GetFloat("y", DefaultDigPointY());
                    int holdMs = request.GetInteger("holdMs", 600);
                    float maxDistance = request.GetFloat("maxDistance", 8f);
                    var beforeAim = AimObserver.Describe(maxDistance) as Dictionary<string, object>;
                    int cellX = 0;
                    int cellY = 0;
                    int cellZ = 0;
                    bool hasCell = false;
                    // `AimObserver.Describe` 鎶婂懡涓俊鎭斁鍦?**`target`** 閿笅锛坄target.cell` 鎵嶆槸鏍煎瓙锛夛紝
                    // 椤跺眰娌℃湁 `cell` 鈥斺€?涔嬪墠涓€鐩村彇椤跺眰鎵€浠ユ亽涓?null锛堝疄娴嬩袱澶勫潙锛氶敭鍚嶄笌宓屽锛夈€?
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
                    // ---- 鏄惧紡鐩爣鏍硷細**鍙寲浣犳寚瀹氱殑閭ｄ竴鏍?*锛屼笖蹇呴』鐪熺殑瀵圭潃瀹?----
                    // 涓轰粈涔堝繀椤绘湁杩欐潯锛氬绾挎槸"璐村湪鏌愪釜闈笂鐨勮杽鐗?锛屾牸瀛愪腑蹇冨線寰€鏄┖鐨勶紝
                    // 瀵圭潃鏍煎績鎸栦細**绌胯繃瀹冩墦鍒板悗闈?*鐨勬柟鍧楋紙瀹炴祴锛氱洰鏍囧绾夸竴鏍兼病鎺夛紝鍚庨潰閭ｇ墖鑽夊湴
                    // 琚繛鎸?5 鏍硷級銆傛墍浠ョ粰浜?`cell=` 鏃跺厛鍋?*闈㈡壂鎻?*锛氶€愪釜闈㈠績杞瑙掞紝
                    // 鍙湁鍑嗘槦鐪熺殑鍛戒腑璇ユ牸鎵嶅紑鎸栵紱鍏釜闈㈤兘涓嶅懡涓氨鐩存帴澶辫触杩斿洖锛岀粷涓嶈鎸栧悗闈㈢殑鏂瑰潡銆?
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
                    bool useDirectAndroidTouch = false; // 鐩存帴钀借Е鐐规寲涓嶅姩锛堝紩鎿庝笉璁よ繖鏉¤矾寰勶級锛屽浐瀹氳蛋浼氳瘽
                    if (useDirectAndroidTouch && AndroidTouch.Available)
                    {
                        // Android锛?*鐩存帴钀借Е鐐?*锛堜笉缁忚繃浼氳瘽鐨?MoveTo锛夈€備細璇濊矾寰勪細鍏堝彂涓€娆?
                        // `MoveTo(钀界偣)`锛岄偅鍦?Android 涓婄瓑浜庝竴娆″ぇ骞?look 鎷栨嫿 鈥斺€?瀹炴祴琛ㄧ幇涓?
                        // "浣庡ご瀵圭潃鐩爣鎸?鈫?鎸栨帢鐬棿璺虫垚骞宠 鈫?涓嬩竴鐬湅澶?銆傜洿鎺ユ柊寤鸿Е鐐规病鏈夌Щ鍔ㄥ閲忥紝
                        // 瑙嗚灏变笉浼氳鎷栬蛋锛涙寜浣忔椂闀跨敤甯ф暟琛ㄨ揪锛堜繚鎸佹湡鍙槸绛夊抚锛屼笉鍐欎换浣曚綅缃級銆?
                        AndroidTouch.Press(new Vector2(px, py));
                        int releaseFrames = Math.Max(1, holdMs / 16);
                        for (int i = 0; i < releaseFrames; i++)
                        {
                            // 姣忓抚瀵?*鍚屼竴鐐?*鍙戜竴娆?Move锛氬悓鐐?闆朵綅绉伙紙涓嶄細鎷栬瑙掞級锛?
                            // 浣嗗紩鎿庨渶瑕佽Е鎽哥偣杩涘叆 `Moved` 鎵嶄細鍚姩鎸栨帢鈥斺€斿彧鎸変笉鍔ㄧ殑璇?
                            // 瑙︽懜鐐规案杩滄槸 `Pressed`锛屾寲鎺樹笉鐢熸晥锛堝疄娴?600/1500ms 閮芥寲涓嶆帀锛夈€?
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
                    // 杩涘害椹卞姩锛氫竴鐩存寜浣忥紝鐩村埌**鐩爣鏍肩湡鐨勫彉浜?*鎵嶆澗鎵嬶紙涓婇檺 maxHoldMs锛夈€?
                    // 涓轰粈涔堜笉鑳藉彧鎸夊浐瀹氭椂闀匡細鎸栨帢鏄惁瀹屾垚鍙栧喅浜?`ComponentMiner.CalculateDigTime`
                    // 鈥斺€?鍒涢€犳ā寮忔槸 0锛堢灛闂村畬鎴愶級锛岀敓瀛樻ā寮忓垯瑕佹寜浣忔暣娈垫寲鎺樻椂闂达紱鍥哄畾 holdMs 浼氬湪
                    // "宸茬粡鍑虹幇鎸栨帢鍔ㄤ綔銆佹柟鍧楄繕娌℃帀"鐨勬椂鍊欐澗鎵嬶紝杩涘害褰掗浂銆佽繖涓€涓嬬櫧鎸栵紙瀹炴祴鐥囩姸锛夈€?
                    int maxHoldMs = request.GetInteger("maxHoldMs", 6000);
                    int attempts = 0;
                    int afterValue = 0;
                    int heldFrames = 0;
                    for (attempts = 1; attempts <= 2; attempts++)
                    {
                        // 鍘昏€︼細寮€濮嬪墠鍙畾浣嶄竴娆★紙璁剧疆瑙︽懜钀界偣锛夛紝淇濇寔鏈?*涓嶅啀鍐欎綅缃?*鈥斺€?
                        // Android 涓婃棤鎸夐挳鍖虹殑瑙︽懜鍚屾椂鏄瑙掓憞鏉嗭紝鍙嶅鍐欎綅缃?鎷栨嫿浼氭妸闀滃ご杞蛋锛?
                        // 瀵艰嚧"鎸変笅鐬棿瀵圭潃鐨勬槸涓嬩竴鏍瑰绾匡紝瀹為檯鎸栧埌鍒"銆?
                        m_injector.Session.Begin(false);
                        // 鍙钀界偣銆佷笉鎺掗槦 Move 姝ワ細閬垮厤 Android 涓?瀹氫綅"琚綋鎴愪竴娆?look 鎷栨嫿
                        // 锛堜綆澶粹啋骞宠鈫掔湅澶╋級锛屽悓鏃惰 Press 钀藉湪姝ｇ‘鐨勪綅缃笂锛堝紩鎿庤杩欐潯璺緞锛夈€?
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
                    // 绾€夋嫨鍣ㄧ偣鍑讳紭鍏堣蛋 direct锛堝崟甯у悎鎴?Tap+Click锛屽啓鍦ㄧ洰鏍囨帶浠惰嚜宸辩殑杈撳叆闈級锛?
                    // 涓栫晫鍐?GameWidget 灞傜骇涓嬭€佺殑杞厜鏍囧甯т細璇濇淳鐢熶笉鍑?Click 鈥斺€?
                    // `ComponentInput.UpdateInputFromMouseAndKeyboard` 姣忓抚閲嶅啓璇ュ眰杈撳叆鐘舵€侊紝
                    // GameMenuDialog 鐨勬寜閽敤浼氳瘽鐐瑰畬鍏ㄦ病鍙嶅簲銆佺敤 direct 绔嬪埢鍥炲埌涓昏彍鍗曪紙瀹炴祴锛夈€?
                    // 甯﹀潗鏍囩殑鐐瑰嚮锛堣櫄鎷熷垪琛ㄨ/鍧愭爣鐐瑰嚮锛変笌鏄惧紡 `mode=input` 浠嶈蛋浼氳瘽锛岃涓轰笉鍙樸€?
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

                // ------------------------------------------------------ 绌烘牸/璺宠穬瀹¤涓庣紦鍐诧紙璺宠穬鎵嬫劅锛?
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
                // ------------------------------------------------------ 鐒︾偣绛栫暐涓庡叡鎺э紙CM-2锛?
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
                    // 涓€閿嚜鏁戯細涓囦竴鐒︾偣绛栫暐鎶婇紶鏍囧崱浣忎簡锛堣瑙掕浆涓嶅姩 / 鍏夋爣璺戝埌绋嬪簭澶栵級锛?
                    // 杩欐潯鍛戒护绔嬪埢鍥炲埌"璺熼殢寮曟搸"骞舵妸绐楀彛鐘舵€佹仮澶嶆垚鐪熷疄鍊笺€?
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
                    // 鎵╁睍鍛戒护锛堝埆鐨?Mod 娉ㄥ唽鐨勶紝渚嬪 PlayerAiMod 鐨?ai.*锛夛細
                    // 鍐呭缓鍛戒护浼樺厛锛屾墿灞曞懡浠や笉鑳借鐩栧唴寤鸿涓恒€?
                    CommandExtension extension;
                    if (m_injector.Commands.TryGet(request.Command, out extension))
                        return ExecuteExtension(extension, request);

                    throw new BridgeCommandException(
                        "unknown_command", "Unknown command '" + request.Command + "'.");
                }
            }
        }

        /// <summary>`CellFace` 鐨勫叚涓潰娉曞悜锛坄Game/CellFace.cs`锛?=+Z 1=+X 2=-Z 3=-X 4=+Y 5=-Y锛夈€?/summary>
        private static readonly float[] FaceNormalX = { 0f, 1f, 0f, -1f, 0f, 0f };
        private static readonly float[] FaceNormalY = { 0f, 0f, 0f, 0f, 1f, -1f };
        private static readonly float[] FaceNormalZ = { 1f, 0f, -1f, 0f, 0f, 0f };

        /// <summary>
        /// 浠庤姹傞噷鍙?瑕佹寲鍝竴鏍?锛歚cell=x,y,z` 瀛楃涓诧紝鎴?`cellX/cellY/cellZ` 涓変釜鏁存暟銆?
        /// 涓嶇粰灏辫繑鍥?false锛堟鏃舵寜鍑嗘槦/瑙︽懜鐐瑰懡涓殑閭ｄ竴鏍兼寲锛夈€?
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

        /// <summary>鍑嗘槦褰撳墠鍛戒腑鐨勬牸瀛愭槸涓嶆槸鎸囧畾鏍硷紙鍙湅鏍煎瓙锛屼笉鐪嬫柟鍧楃被鍨嬶級銆?/summary>
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
        /// 閫愪釜闈㈠績杞瑙掞紝杩斿洖**绗竴涓兘璁╁噯鏄熷懡涓鏍?*鐨勯潰鍙凤紱鍏釜闈㈤兘涓嶅懡涓繑鍥?-1銆?
        ///
        /// 鐢ㄩ€旓細瀵肩嚎/鍛婄ず鐗岃繖绫?璐村湪闈笂鐨勮杽鐗?蹇呴』瀵圭潃瀹冩墍鍦ㄧ殑閭ｄ釜闈㈡寲 鈥斺€?
        /// 瀵圭潃鏍煎績鎸栦細绌胯繃鍘绘墦鍒板悗闈㈢殑鏂瑰潡锛堝疄娴嬶細鐩爣瀵肩嚎娌℃帀锛屽悗闈㈢殑鑽夊湴琚繛鎸?5 鏍硷級銆?
        /// 闈㈠績 = 鏍间腑蹇?+ 0.5 脳 闈㈡硶鍚戯紙`CellFace.FaceToVector3` 鐨勫悓涓€绾﹀畾锛夈€?
        /// </summary>
        private int AimAtCellFace(int cellX, int cellY, int cellZ, float maxDistance)
        {
            for (int face = 0; face < 6; face++)
            {
                m_injector.LookAt(
                    cellX + 0.5f + FaceNormalX[face] * 0.5f,
                    cellY + 0.5f + FaceNormalY[face] * 0.5f,
                    cellZ + 0.5f + FaceNormalZ[face] * 0.5f);
                WaitGameFrames(2); // 瑙嗚鍐欏畬涔嬪悗鐩告満瑕佸嚭甯ф墠浼氭洿鏂?ViewDirection
                if (AimHitsCell(cellX, cellY, cellZ, maxDistance))
                    return face;
            }
            return -1;
        }

        /// <summary>绛夋父鎴忓嚭 <paramref name="frames"/> 甯э紙娓告垙涓嶅嚭甯ф椂闈犺秴鏃跺厹搴曪紝涓嶄細鍗℃锛夈€?/summary>
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
        /// 鎸栨帢榛樿钀界偣鐨?X = **瑙嗗彛涓績**锛坄Window.Size` 鐨勪竴鍗婏級銆?
        /// 涓轰粈涔堜笉鐢?`ScreensManager.RootWidget.ActualSize`锛氶偅鏄璁″昂瀵革紝Android 涓婃槸瑙嗗彛鐨勪竴鍗?
        /// 锛堝疄娴嬭鍙?2000脳1200銆佹牴鎺т欢 1000脳600锛夛紝鐢ㄥ畠绠楀嚭鏉ョ殑"涓績"(500,300) 鍦ㄥ紩鎿庣溂閲屾槸灞忓箷
        /// 宸︿笂瑙掞紝鎸栨帢灏勭嚎灏勫悜澶╃┖锛屾€庝箞鎸夐兘鎸栦笉鎺夈€傚彇涓嶅埌灏哄鏃跺洖閫€鍒?1000锛堝父瑙佽鍙ｄ腑蹇冿級銆?
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

        /// <summary>鎸栨帢榛樿钀界偣鐨?Y锛岃 <see cref="DefaultDigPointX"/>銆?/summary>
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
        /// 淇濇寔鎸変綇锛堝乏閿?瑙︽懜锛夌洿鍒?*鐩爣鏍煎湴褰㈠€肩湡鐨勫彉鍖?*锛岃繑鍥炴寜浣忕殑鎬诲抚鏁般€?
        ///
        /// 涓轰粈涔堣"鎸夊埌鍙樹簡鎵嶆澗鎵?鑰屼笉鏄寜鏃堕暱锛氭寲鎺樿繘搴︾敱 `ComponentMiner` 鐨勬寲鎺樻椂闂村喅瀹?
        /// 锛坄ComponentMiner.CalculateDigTime`锛氬垱閫犳ā寮?0銆佺敓瀛樻ā寮忎负鏂瑰潡纭害鍐冲畾鐨勬鏁帮級锛?
        /// 鍥哄畾 holdMs 浼氬嚭鐜?鐢婚潰宸茬粡鍦ㄦ寲銆佹柟鍧楄繕娌℃帀灏辨澗鎵?鈥斺€旇繘搴﹀綊闆讹紝杩欎竴涓嬬櫧鎸栵紙瀹炴祴鐥囩姸锛夈€?
        ///
        /// 淇濇寔鏈?*鍙帹杩涘抚锛岀粷涓嶅啓浣嶇疆**锛坄HoldNoMove`锛夛細
        ///   路 寮曟搸锛圓ndroid锛夋妸"鏃犳寜閽尯鎸変綇涓嶅姩"褰撲綔鎸栨帢锛岄潬瑙︾偣**鍋滃湪鍘熷湴**鏉ョ淮鎸侊紱
        ///   路 涓€鏃︽瘡甯ч兘鍐欎竴娆′綅缃紙鍝€曟槸鍚屼竴涓偣锛夛紝瑙︽懜鐘舵€佹満璁や负鎵嬫寚杩樺湪绉诲姩锛?
        ///     姘歌繙杩涗笉浜?鎸変綇涓嶅姩"閭ｄ竴妗?鈥斺€?瀹炴祴鎸変綇 12 绉掞紙涓ゆ 6 绉掋€佹瘡甯у悓鐐?MoveTo锛?
        ///     **涓€鏍奸兘娌℃寲鎺?*锛涙敼鍥炵函甯ф帹杩涘悗鍚屾牸鍚岃瑙掔珛鍒绘寲鎺夛紙05:2x 琚富鏈烘帴鍙楃殑閭ｆ壒鎸栨帢
        ///     璺戠殑姝ｆ槸绾抚鎺ㄨ繘鐨勭増鏈紝鍚庢潵鐨?姣忓抚鍚岀偣 Move"鏄垜鑷繁寮曞叆鐨勫洖褰掞級銆?
        ///   路 涓嶅啓浣嶇疆涔熼『甯︿繚璇侀暅澶翠笉浼氳鎷栬蛋锛埼攑itch = 0.000锛夈€?
        ///
        /// **婊氬姩绐楀彛**锛氶槦鍒楅噷濮嬬粓棰勬帓 <see cref="WindowFrames"/> 涓抚鎺ㄨ繘锛岄伩鍏嶅嚭鐜?绌哄抚"
        /// 鎵撴柇鎸栨帢锛涘悓鏃舵瘡甯ч兘璇讳竴娆＄洰鏍囨牸 鈥斺€?鍒涢€犳ā寮忔寲鎺樻椂闂?0锛屽垽瀹氭櫄涓€甯у氨澶氭寲涓€鏍笺€?
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
                // 绛?杩欎竴甯?琚墽琛岋細`ActionsExecuted` 鐢卞抚棣栨车鍦ㄧ湡姝ｆ墽琛屽姩浣滄椂鑷锛?
                // 姣?`WaitUntilIdle` 鏇磋创杩?杩欎竴甯у凡缁忚惤鍒板紩鎿庝笂浜?銆傛父鎴忎笉鍑哄抚鏃堕潬瓒呮椂鍏滃簳
                // 锛堟渶灏忓寲/鏆傚仠涓嶄細鎶婂懡浠ょ嚎绋嬫案涔呭崱浣忥級銆?
                int executed = m_injector.Pump.ActionsExecuted;
                var stopwatch = Stopwatch.StartNew();
                while (m_injector.Pump.ActionsExecuted == executed && stopwatch.ElapsedMilliseconds < 250)
                    System.Threading.Thread.Sleep(2);

                frames++;
                if (!hasCell || terrain == null)
                {
                    // 娌＄瀯鍒版牸瀛愶紙渚嬪瀵圭潃绌烘皵锛夛細閫€鍖栦负"鑷冲皯鎸夋弧 minHoldMs"銆?
                    if (frames >= minFrames)
                        break;
                }
                else if (terrain.Terrain.GetCellValue(cellX, cellY, cellZ) != beforeValue)
                {
                    break;
                }

                // 琛ヤ笂鍒氭秷鑰楁帀鐨勯偅涓€甯э紝淇濇寔绐楀彛涓嶇┖锛堣繖涓€姝ヤ細钀藉湪涓嬩竴甯э級銆?
                m_injector.Session.HoldNoMove();
            }
            return frames;
        }

        /// <summary>
        /// 鎵ц鎵╁睍鍛戒护銆傞粯璁ゅ湪**娓告垙绾跨▼**鎵ц锛堣Е纰版父鎴忓璞?杈撳叆鐨勫懡浠ゅ繀椤诲姝わ級锛?
        /// 鎵╁睍鍛戒护鎶涘嚭鐨?<see cref="CmdBridgeCommandException"/> 鐩存帴鏄犲皠鎴愰敊璇爜杩斿洖缁欏鎴风銆?
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

        /// <summary>鍒楀嚭鍐呭缓鍛戒护涓庢墿灞曞懡浠わ紙璋佹敞鍐岀殑涓€鐩簡鐒讹紝渚夸簬鎺掓煡"鍛戒护娌＄敓鏁?锛夈€?/summary>
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
        /// 鎵ц涓€鏉?铏氭嫙 UI 榧犳爣浼氳瘽"鍛戒护锛氶粯璁ょ瓑鎵嬪娍鍦ㄥ抚棣栭€愬抚璺戝畬鍐嶈繑鍥烇紝
        /// 浜庢槸鑴氭湰/AI 鎷垮埌杩斿洖鍊兼椂鍔ㄤ綔宸茬粡钀藉湴锛堢瓑浠蜂簬鐪熶汉鏉炬墜閭ｄ竴鍒伙級銆?
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

        // ---------------------------------------------------------------- 鍙瀹炵幇

        /// <summary>
        /// 鏉′欢绛夊緟锛氬湪娓告垙绾跨▼涓婂懆鏈熸€ф眰鍊硷紝鐩村埌鍏ㄩ儴鏉′欢鎴愮珛鎴栬秴鏃躲€?
        ///
        /// 涓轰粈涔堥渶瑕佸畠锛氬睆骞曞垏鎹€佷笘鐣屽姞杞姐€侀潰鏉垮脊鍑洪兘鏄法甯у畬鎴愮殑鈥斺€擲witchScreen 浼氱珛鍗虫洿鏂?
        /// CurrentScreen锛屼絾鏂板睆骞曠殑鎺т欢瑕佺瓑杞睆鍔ㄧ敾涓鎵嶈繘鍏?RootWidget.Children
        /// 锛圫creensManager.cs:85, 302-309锛夛紱瀵硅瘽妗嗕篃鏄紓姝ュ叧闂殑銆傚鎴风闈犲浐瀹?sleep 蹇呯劧瑕佷箞澶參銆?
        /// 瑕佷箞鍦ㄧ珵鎬侀噷璇诲埌绌虹晫闈€傛湁浜嗗畠锛孉I 鍙互鍐?鎸夐敭寮€鑳屽寘 鈫?绛夋Ы浣嶅彲鐐?锛岃涔夋槑纭€佷笉闈犵寽銆?
        ///
        /// 鏀寔鐨勬潯浠讹紙澶у皬鍐欎笉鏁忔劅锛夛細
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
                    // 鏉′欢姹傚€煎繀椤绘暣鎵瑰湪鍚屼竴甯у畬鎴愶紝鍚﹀垯澶氫釜鏉′欢鍙兘钀藉湪涓嶅悓甯т笂浜掔浉鐭涚浘銆?
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

        /// <summary>杩斿洖 null 琛ㄧず鏉′欢鎴愮珛锛涘惁鍒欒繑鍥炶鏉′欢锛堜緵瓒呮椂鎶ュ憡锛夈€?/summary>
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
                return condition;   // 鐣岄潰灏氭湭灏辩华锛岀户缁瓑寰?

            Widget widget;
            try
            {
                widget = UiInspector.Resolve(root, selector, false, Vector2.Zero);
            }
            catch (BridgeCommandException exception) when (exception.Code == "element_missing")
            {
                return condition;   // 杩樻病鍑虹幇锛岀户缁瓑寰?
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
        /// 鍖哄煙鏂瑰潡鎵弿銆傛湭缁欎腑蹇冩椂浠ョ帺瀹舵墍鍦ㄦ牸涓轰腑蹇冿紝閬垮厤 AI 蹇呴』鑷繁鎹㈢畻鏍煎瓙鍧愭爣銆?
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
        /// 鑷锛氱‘璁ょ櫧鍚嶅崟娉ㄥ叆鐐瑰叏閮ㄥ彲鐢紙涓嶅啓鍏ヤ换浣曞€硷級锛屽苟澶嶆煡鍙揪鎬х粺璁°€?
        /// 鐢ㄤ簬 P2 闃舵楠岃瘉"甯ч缂濋殭 + 娉ㄥ叆鐐?鏄惁鎴愮珛銆?
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

            // 鎵╁睍鍛戒护娉ㄥ唽琛細涓婂眰 Mod锛堝 PlayerAiMod 鐨?ai.*锛夐潬瀹冩寕鍛戒护锛屼笉璇ュ嚭鐜?娉ㄥ唽涓嶄笂/鎽樹笉鎺?銆?
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



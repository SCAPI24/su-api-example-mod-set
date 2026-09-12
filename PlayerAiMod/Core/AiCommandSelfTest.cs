using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 控制面命令层自检（P0-7）：**纯逻辑、不依赖游戏**。
    ///
    /// 覆盖：命令表与实现一一对应、`ai.status` 汇总、暂停/恢复、接管/放弃（含释放输入）、
    /// 黑板读/写/全列/类型校验、树列表/装载/校验/通知/重载排队、缺参与非就绪错误码，
    /// 以及 `bt.selftest` 自身。用假宿主 + 真重载器（真磁盘临时目录），
    /// 于是"命令 → 装载 → 运行"这条链在没有游戏的情况下也能跑通。
    /// </summary>
    public static class AiCommandSelfTest
    {
        private const string InstanceDir = "C:/pai-cmd-selftest/instance";
        private const string ModsDir = "C:/pai-cmd-selftest/mods";

        private sealed class FakeContext : IAiCommandContext
        {
            public FakeContext()
            {
                Recording = new AiRecordingSession(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pai-cmd-recording"));
                EventLog = new AiEventLog(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pai-cmd-logs"),
                    AiEventLog.DefaultFileName, 8192, 2, 16);
            }

            /// <summary>真事件日志（写临时目录）—— 命令层自检顺带覆盖 ai.logs。</summary>
            public AiEventLog EventLog { get; }

            /// <summary>动作包播放（假实现：只记调用，不动真输入）。</summary>
            private readonly FakeActionPlayer m_actionPlayer = new FakeActionPlayer();

            public IAiActionPlayer ActionPlayer
            {
                get { return m_actionPlayer; }
            }

            public FakeActionPlayer Actions
            {
                get { return m_actionPlayer; }
            }

            /// <summary>宿主（假件是通用宿主；测控制器宿主时换成 <see cref="ControllerTreeHost"/>）。</summary>
            public IAiTreeHost Host { get; set; }

            public PackageReloader Reloader { get; set; }

            /// <summary>真录制会话（纯逻辑，写临时目录）—— 命令层自检顺带覆盖 ai.record.*。</summary>
            public AiRecordingSession Recording { get; }

            /// <summary>真树库（配合真重载器）—— 命令层自检顺带覆盖 ai.tree.prepare/switch。</summary>
            public TreeLibrary Library { get; set; }

            IAiRecordingControl IAiCommandContext.Recording
            {
                get { return Recording; }
            }

            public bool Paused { get; private set; }

            public string PauseReason { get; private set; }

            public bool InputAvailable
            {
                get { return true; }
            }

            public void Pause(string reason)
            {
                Paused = true;
                PauseReason = reason;
            }

            public void Resume(string reason)
            {
                Paused = false;
                PauseReason = reason;
            }

            // 接口返回 IAiTreeHost；具体类型属性不能隐式实现（C# 不支持协变返回的接口实现），故显式实现。
            IAiTreeHost IAiCommandContext.Host
            {
                get { return Host; }
            }
        }

        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "AiCommandSelfTest" };
            try
            {
                CommandTable(result);
                StatusAndSwitches(result);
                Blackboard(result);
                TreeCommands(result);
                RecordingCommands(result);
                ObservabilityCommands(result);
            try { UiClickTargets(result); } catch (Exception e) { result.Check("case:UI click targets", false, e.Message); }
                ErrorCodes(result);
                SelfTestCommand(result);
            }
            catch (Exception exception)
            {
                result.Check("AiCommandSelfTest no unexpected exception", false,
                    exception.GetType().Name + ": " + exception.Message);
            }
            return result;
        }

        // ---------------------------------------------------------------- 用例

        private static void CommandTable(BtSelfTest.TestResult result)
        {
            Dictionary<string, Func<AiCommandRequest, IAiCommandContext, object>> handlers =
                AiCommandSet.BuildHandlers();

            result.Check("every advertised command has a handler",
                handlers.Count == AiCommandSet.CommandNames.Length,
                "handlers=" + handlers.Count + " names=" + AiCommandSet.CommandNames.Length);

            bool missing = false;
            for (int i = 0; i < AiCommandSet.CommandNames.Length; i++)
            {
                if (!handlers.ContainsKey(AiCommandSet.CommandNames[i]))
                    missing = true;
                if (!AiCommandSet.CommandDescriptions.ContainsKey(AiCommandSet.CommandNames[i]))
                    missing = true;
            }
            result.Check("every command has a description", !missing, "missing description or handler");

            bool allPrefixed = true;
            for (int i = 0; i < AiCommandSet.CommandNames.Length; i++)
            {
                string name = AiCommandSet.CommandNames[i];
                if (!name.StartsWith("ai.", StringComparison.Ordinal)
                    && !name.StartsWith("bt.", StringComparison.Ordinal))
                {
                    allPrefixed = false;
                }
            }
            result.Check("commands are namespaced (ai.* / bt.*)", allPrefixed, "unexpected command name");
        }

        private static void StatusAndSwitches(BtSelfTest.TestResult result)
        {
            var context = new FakeContext();
            object status = AiCommandSet.Execute(new AiCommandRequest("ai.status"), context);
            var map = status as Dictionary<string, object>;
            result.Check("ai.status works without a host",
                map != null && Equals(map["mode"], "inactive") && map.ContainsKey("packages"),
                Describe(status));

            var host = new AiTestHost();
            context.Host = host;
            map = AiCommandSet.Execute(new AiCommandRequest("ai.status"), context) as Dictionary<string, object>;
            var hostInfo = map != null ? map["host"] as Dictionary<string, object> : null;
            result.Check("ai.status reports the host",
                hostInfo != null && Equals(hostInfo["name"], "selftest-host")
                && Equals(hostInfo["hasTree"], false) && Equals(map["mode"], "idle"),
                Describe(status));

            map = AiCommandSet.Execute(new AiCommandRequest("ai.pause"), context) as Dictionary<string, object>;
            result.Check("ai.pause sets the paused flag",
                context.Paused && Equals(map["paused"], true), Describe(map));

            AiCommandSet.Execute(new AiCommandRequest("ai.resume"), context);
            result.Check("ai.resume clears the paused flag", !context.Paused, context.PauseReason);

            AiCommandSet.Execute(new AiCommandRequest("ai.disable"), context);
            result.Check("ai.disable turns the host off and releases input",
                !host.Enabled && host.ReleaseCount == 1,
                "enabled=" + host.Enabled + " release=" + host.ReleaseCount);

            AiCommandSet.Execute(new AiCommandRequest("ai.enable"), context);
            result.Check("ai.enable turns the host back on", host.Enabled, "enabled=false");

            AiCommandSet.Execute(new AiCommandRequest("ai.input.release"), context);
            result.Check("ai.input.release releases input", host.ReleaseCount == 2,
                "release=" + host.ReleaseCount);
        }

        private static void Blackboard(BtSelfTest.TestResult result)
        {
            var host = new AiTestHost();
            var context = new FakeContext { Host = host };

            IDictionary<string, object> map = AiCommandSet.Execute(
                AiCommandRequest.FromArgs("ai.blackboard", "key", "count", "value", 7L, "type", "int"),
                context) as IDictionary<string, object>;
            int stored;
            bool written = host.Blackboard.TryGet(new AiBlackboardKey<int>("count"), out stored);
            result.Check("ai.blackboard writes a typed value",
                map != null && Equals(map["written"], true) && written && stored == 7,
                "written=" + written + " value=" + stored);

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.blackboard", "key", "count"), context)
                as IDictionary<string, object>;
            result.Check("ai.blackboard reads it back",
                map != null && Equals(map["found"], true) && Equals(map["value"], 7),
                Describe(map));

            AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.blackboard", "key", "flag", "value", true,
                "type", "bool"), context);
            AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.blackboard", "key", "note", "value", "hi",
                "type", "string"), context);

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.blackboard", "all", true), context)
                as IDictionary<string, object>;
            var values = map != null ? map["values"] as IDictionary<string, object> : null;
            result.Check("ai.blackboard all=true lists every value",
                values != null && values.Count == 3 && Equals(values["note"], "hi"),
                Describe(map));

            bool badType = false;
            try
            {
                AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.blackboard", "key", "x", "value", 1,
                    "type", "actor"), context);
            }
            catch (AiCommandException exception)
            {
                badType = exception.Code == "invalid_argument";
            }
            result.Check("ai.blackboard rejects an unsupported type", badType, "no invalid_argument");
        }

        private static void TreeCommands(BtSelfTest.TestResult result)
        {
            // 真磁盘临时目录：命令层要能列出/装载/重载真实文件
            string directory = Path.Combine(Path.GetTempPath(), "pai-cmd-selftest");
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception)
            {
            }
            Directory.CreateDirectory(directory);

            var roots = new PackageRoots(Path.Combine(directory, "instance"));
            List<string> installed;
            string installError;
            PackageTemplates.Install(roots, out installed, out installError);

            var reloader = new PackageReloader(roots, new PackageLoadOptions { Roots = roots });
            var host = new AiTestHost();
            var context = new FakeContext { Host = host, Reloader = reloader };

            IDictionary<string, object> map = AiCommandSet.Execute(new AiCommandRequest("ai.tree.list"), context)
                as IDictionary<string, object>;
            var packages = map != null ? map["packages"] as List<Dictionary<string, object>> : null;
            result.Check("ai.tree.list enumerates both folders",
                packages != null && packages.Count == PackageTemplates.All().Count
                && Equals(map["active"], null),
                Describe(map));
            if (packages != null && packages.Count > 0)
            {
                result.Check("ai.tree.list marks the source folder",
                    Equals(packages[0]["source"], "mod") || Equals(packages[0]["source"], "instance"),
                    Describe(packages[0]));
            }

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.load", "name", "demo.greet"),
                context) as IDictionary<string, object>;
            result.Check("ai.tree.load loads the package into the host",
                map != null && Equals(map["replaced"], true) && host.HasTree && host.Enabled,
                Describe(map));
            result.Check("ai.tree.load reports node/package counts",
                map != null && Equals(map["nodes"], 8) && Equals(map["packages"], 2),
                Describe(map));
            result.Check("loaded tree is running", host.Tree.IsRunning, "tree not started");

            map = AiCommandSet.Execute(new AiCommandRequest("ai.status"), context) as IDictionary<string, object>;
            var tree = map != null ? map["tree"] as IDictionary<string, object> : null;
            result.Check("ai.status shows the loaded tree",
                tree != null && Equals(tree["id"], "demo.greet") && Equals(tree["running"], true),
                Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.validate", "name", "demo.greet"),
                context) as IDictionary<string, object>;
            result.Check("ai.tree.validate accepts a good package",
                map != null && Equals(map["ok"], true), Describe(map));

            // ---- 树库：预编译 + 毫秒级切换（P0-11）
            // 容量给够：`ai.tree.prepare all=true` 要能装下**每一棵出厂包**（生产容量是 8）
            context.Library = new TreeLibrary(reloader, PackageTemplates.All().Count + 4);

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.prepare", "name", "demo.greet"),
                context) as IDictionary<string, object>;
            result.Check("ai.tree.prepare compiles into the resident library",
                map != null && Equals(map["ready"], true) && Equals(map["nodes"], 8),
                Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.switch", "name", "demo.greet"),
                context) as IDictionary<string, object>;
            result.Check("ai.tree.switch uses the prepared copy",
                map != null && Equals(map["switched"], true) && Equals(map["usedPrepared"], true),
                Describe(map));
            result.Check("ai.tree.switch reports its own switch time",
                map != null && Convert.ToDouble(map["switchMs"]) >= 0.0
                && Convert.ToDouble(map["switchMs"]) <= Convert.ToDouble(map["compileMs"]),
                Describe(map));

            map = AiCommandSet.Execute(new AiCommandRequest("ai.status"), context)
                as IDictionary<string, object>;
            var library = map != null ? map["library"] as IDictionary<string, object> : null;
            var preparedEntry = library != null
                ? library["prepared"] as List<Dictionary<string, object>> : null;
            result.Check("ai.status exposes the tree library",
                library != null && Equals(library["available"], true)
                && preparedEntry != null && preparedEntry.Count == 1,
                Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.switch", "name", "ghost.tree"),
                context) as IDictionary<string, object>;
            result.Check("ai.tree.switch reports a missing package",
                map != null && Equals(map["switched"], false)
                && Convert.ToString(map["reason"]).Contains("not found"),
                Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.prepare", "all", true),
                context) as IDictionary<string, object>;
            result.Check("ai.tree.prepare all=true prepares every package",
                map != null && Convert.ToInt32(map["count"]) == PackageTemplates.All().Count
                && Convert.ToInt32(map["errors"]) == 0,
                Describe(map));

            // ---- 无角色宿主（主菜单）：世界没加载也能跑树、也能被监视
            // 用户的原话："进入游戏这个动作包，就是游戏启动后、不进入世界就要跑的，
            // 实时监视也需要一开始就能监听。" 这一段就是钉这两件事。
            {
                var menuActuator = new BtTestActuator();
                var menuHost = new ControllerTreeHost(menuActuator);
                // **自己一份重载器 + 树库**：树库切换会把活动树 Adopt 进重载器，
                // 共用上面那个会把后面几条"活动树是 demo.greet"的断言弄坏（踩过）。
                var menuReloader = new PackageReloader(roots, new PackageLoadOptions { Roots = roots });
                var menuContext = new FakeContext { Host = menuHost, Reloader = menuReloader };
                menuContext.Library = new TreeLibrary(menuReloader, 4);

                result.Check("the host is the controller (not a character), and says where it operates",
                    menuHost.HostKind == "controller" && menuHost.Situation == "menu",
                    menuHost.ToString());

                // 「进入游戏」那种包整段在主菜单里跑：这里用出厂示例 test.action（它里面就有
                // PlayActionPackage(sample_walk)）当靶子，走的是同一条链。
                map = AiCommandSet.Execute(
                    AiCommandRequest.FromArgs("ai.tree.switch", "name", "test.action"),
                    menuContext) as IDictionary<string, object>;
                result.Check("a tree can be switched in with no world and no player",
                    map != null && Equals(map["switched"], true) && menuHost.HasTree,
                    Describe(map));

                for (int i = 0; i < 12; i++)
                    menuHost.Tick(1f / 60f);

                result.Check("ticking the controller host really advances the tree",
                    menuHost.Tree.TickCount > 0 && menuHost.Mode == AiMode.Tree,
                    "ticks=" + menuHost.Tree.TickCount + " mode=" + menuHost.Mode.Describe());

                // 世界外**只允许点 UI**：动作包里的按键/视角是空实现（不假装成功）。
                // 这条以前写反了（断言"按键到了注入器"）—— 那正是"世界外假装在走"的坏味道。
                result.Check("at the menu the world-only input from the action package is dropped",
                    menuActuator.HeldKeysSeen.Count == 0 && menuActuator.LookDeltaCalls == 0
                    && menuActuator.PulsedKeys.Count == 0,
                    "lookDelta=" + menuActuator.LookDeltaCalls + " keys="
                    + string.Join("/", menuActuator.HeldKeysSeen.ToArray()));

                map = AiCommandSet.Execute(new AiCommandRequest("ai.tree.snapshot"), menuContext)
                    as IDictionary<string, object>;
                result.Check("live monitoring answers at the menu (snapshot works with no player)",
                    map != null && Equals(map["running"], true)
                    && Convert.ToString(map["source"]).EndsWith("test.action.scbtpak",
                        StringComparison.OrdinalIgnoreCase)
                    && Convert.ToInt32(map["ticks"]) > 0,
                    Describe(map));

                map = AiCommandSet.Execute(new AiCommandRequest("ai.status"), menuContext)
                    as IDictionary<string, object>;
                var menuHostInfo = map != null ? map["host"] as IDictionary<string, object> : null;
                result.Check("ai.status reports host kind=controller + situation=menu",
                    menuHostInfo != null && Equals(menuHostInfo["kind"], "controller")
                    && Equals(menuHostInfo["situation"], "menu")
                    && Equals(menuHostInfo["ready"], true),
                    Describe(map));

                menuHost.StopTree("selftest done");
                result.Check("stopping the controller tree releases the injector and clears it",
                    !menuHost.HasTree && menuActuator.ReleaseAllCalls > 0,
                    "release=" + menuActuator.ReleaseAllCalls);
            }

                // ---- 控制器宿主的核心承诺：绑定/解绑世界输入来源时，**树不重跑、运行态保留**
                // （用户原话："行为树不应该绑定到角色上，而是角色的控制器……退出世界了，
                // 也是要执行其他操作的"。所以世界来了只换输入路由，不换宿主、不重装树。）
                {
                    var routeActuator = new BtTestActuator();
                    var routeHost = new ControllerTreeHost(routeActuator);
                    var routeReloader = new PackageReloader(roots,
                        new PackageLoadOptions { Roots = roots });
                    var routeContext = new FakeContext { Host = routeHost, Reloader = routeReloader };
                    routeContext.Library = new TreeLibrary(routeReloader, 4);

                    map = AiCommandSet.Execute(
                        AiCommandRequest.FromArgs("ai.tree.switch", "name", "test.action"),
                        routeContext) as IDictionary<string, object>;
                    for (int i = 0; i < 8; i++)
                        routeHost.Tick(1f / 60f);
                    long ticksBeforeBind = routeHost.Tree.TickCount;

                    result.Check("setup: the tree runs with no world attached",
                        map != null && Equals(map["switched"], true) && ticksBeforeBind > 0,
                        "ticks=" + ticksBeforeBind);

                    // 世界来了：接上玩家输入来源
                    var worldProvider = new AiTestHost("selftest-player");
                    routeHost.Bind(worldProvider);
                    for (int i = 0; i < 8; i++)
                        routeHost.Tick(1f / 60f);

                    result.Check("binding a player keeps the same tree running (no restart)",
                        routeHost.HasTree && routeHost.Tree.TickCount > ticksBeforeBind,
                        "before=" + ticksBeforeBind + " after=" + routeHost.Tree.TickCount);
                    result.Check("the controller now reports situation=world with the player name",
                        routeHost.Situation == "world"
                        && Equals(routeHost.PlayerName, "selftest-player"),
                        routeHost.ToString() + " player=" + routeHost.PlayerName);

                    // 世界里：输入走玩家的执行器（而不是 UI 兜底）
                    routeHost.Actuators.HoldKey("W", true);
                    result.Check("in-world input goes to the player's actuator (not the UI fallback)",
                        worldProvider.TestActuator.HeldKeys.Contains("W")
                        && routeActuator.HeldKeysSeen.Count == 0,
                        "world=" + string.Join("/", worldProvider.TestActuator.HeldKeys.ToArray())
                        + " ui=" + routeActuator.HeldKeysSeen.Count);
                    result.Check("in-world sensors are forwarded (drift checks work again)",
                        routeHost.Sensors.IsReady
                        && routeHost.Sensors.PlayerName == worldProvider.TestSensor.PlayerName,
                        "sensor=" + routeHost.Sensors.PlayerName);
                    routeHost.Actuators.ReleaseAll();

                    // 退出世界：摘掉输入来源，树**继续跑**（用户要的"退出世界还能干别的"）
                    long ticksBeforeUnbind = routeHost.Tree.TickCount;
                    routeHost.Bind(null);
                    for (int i = 0; i < 8; i++)
                        routeHost.Tick(1f / 60f);

                    result.Check("unbinding the player keeps the tree running too (ticks keep rising)",
                        routeHost.HasTree && routeHost.Tree.TickCount > ticksBeforeUnbind,
                        "before=" + ticksBeforeUnbind + " after=" + routeHost.Tree.TickCount);
                    result.Check("back to menu: situation=menu and the world sensors report not ready",
                        routeHost.Situation == "menu" && !routeHost.Sensors.IsReady,
                        routeHost.ToString());

                    // ---- 任务级释放**不许**取消 UI 注入（用户实测的 bug：光标移过去、界面纹丝不动）
                    // 一次软光标点击要跨好几帧（移动 → 按下 → 抬起），中途被 ReleaseAll 清一次
                    // 就永远派生不出 Click。所以只有显式的"停止/禁用/释放输入"才许撤 UI 注入。
                    {
                        int uiReleases = routeActuator.ReleaseAllCalls;
                        routeHost.Actuators.ReleaseAll();
                        result.Check("a task-level ReleaseAll does not cancel the UI injection",
                            routeActuator.ReleaseAllCalls == uiReleases,
                            "ui releases " + uiReleases + " -> " + routeActuator.ReleaseAllCalls);

                        routeHost.ReleaseInput();
                        result.Check("an explicit ReleaseInput does cancel it (stop/disable paths)",
                            routeActuator.ReleaseAllCalls > uiReleases,
                            "ui releases " + uiReleases + " -> " + routeActuator.ReleaseAllCalls);
                    }

                    routeHost.Actuators.HoldKey("W", true);
                    result.Check("at the menu the key injection is a no-op (no world, no pretending)",
                        worldProvider.TestActuator.HeldKeys.Count == 0,
                        "world keys=" + worldProvider.TestActuator.HeldKeys.Count);
                    routeHost.StopTree("selftest done");
                }

            // ---- `ai.tree.stop`：停止 = 卸下树（用户要的"重置"入口）
            // 与"暂停"的区别就是这条命令存在的理由：暂停保留运行态，停止回到"什么都没跑"，
            // 之后再 switch 才是干净地从根开始。
            {
                var stopActuator = new BtTestActuator();
                var stopHost = new ControllerTreeHost(stopActuator);
                var stopReloader = new PackageReloader(roots, new PackageLoadOptions { Roots = roots });
                var stopContext = new FakeContext { Host = stopHost, Reloader = stopReloader };
                stopContext.Library = new TreeLibrary(stopReloader, 4);

                map = AiCommandSet.Execute(
                    AiCommandRequest.FromArgs("ai.tree.switch", "name", "test.action"),
                    stopContext) as IDictionary<string, object>;
                result.Check("ai.tree.stop setup: a tree is running in the controller host",
                    map != null && Equals(map["switched"], true) && stopHost.HasTree,
                    Describe(map));

                for (int i = 0; i < 6; i++)
                    stopHost.Tick(1f / 60f);
                int ticksBeforeStop = (int)stopHost.Tree.TickCount;

                map = AiCommandSet.Execute(new AiCommandRequest("ai.tree.stop"), stopContext)
                    as IDictionary<string, object>;
                result.Check("ai.tree.stop unloads the tree and releases input",
                    map != null && Equals(map["stopped"], true) && !stopHost.HasTree
                    && Equals(map["mode"], "idle") && stopActuator.ReleaseAllCalls > 0,
                    Describe(map));

                map = AiCommandSet.Execute(new AiCommandRequest("ai.tree.stop"), stopContext)
                    as IDictionary<string, object>;
                result.Check("stopping twice is honest (nothing to stop)",
                    map != null && Equals(map["stopped"], false)
                    && Convert.ToString(map["reason"]).Contains("no tree"),
                    Describe(map));

                // 暂停 → 停止：**暂停也要一起清掉**。用户实测："播放→暂停→停止→再播放，
                // 显示的还是已暂停"（暂停是全局的，帧首先看它；留着它树装进去也不会跑）。
                map = AiCommandSet.Execute(new AiCommandRequest("ai.pause"), stopContext)
                    as IDictionary<string, object>;
                result.Check("ai.tree.stop setup: the runtime is paused",
                    map != null && Equals(map["paused"], true), Describe(map));

                map = AiCommandSet.Execute(new AiCommandRequest("ai.tree.stop"), stopContext)
                    as IDictionary<string, object>;
                result.Check("ai.tree.stop also clears the global pause (stop = full reset)",
                    map != null && Equals(map["resumed"], true) && Equals(map["paused"], false)
                    && !stopHost.HasTree,
                    Describe(map));
                result.Check("the pause flag really went away (a following switch will tick)",
                    !stopContext.Paused, "paused=" + stopContext.Paused);

                // 再切一次 = 从根开始：tick 计数归零（用户要的"重置"）
                map = AiCommandSet.Execute(
                    AiCommandRequest.FromArgs("ai.tree.switch", "name", "test.action"),
                    stopContext) as IDictionary<string, object>;
                result.Check("switching after a stop starts a fresh run (ticks reset)",
                    map != null && Equals(map["switched"], true) && stopHost.HasTree
                    && stopHost.Tree.TickCount == 0 && ticksBeforeStop > 0,
                    "before=" + ticksBeforeStop + " after=" + stopHost.Tree.TickCount);
                stopHost.StopTree("selftest done");
            }

            // ---- 内存态改写 + 导出（P0-12 / P0-13）
            string demoFile = Path.Combine(roots.InstanceRoot.Path, PackageTemplates.DemoFile);
            string sourceHashBefore = PackageLoader.ComputeHash(File.ReadAllBytes(demoFile));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.edit.set", "id", "move",
                "name", "acceptableRadius", "value", 6f, "type", "float"), context)
                as IDictionary<string, object>;
            result.Check("ai.edit.set changes a parameter in the live tree",
                map != null && Equals(map["applied"], true) && Equals(map["dirty"], true),
                Describe(map));
            var edited = host.Tree.Root.Find("move") as BtMoveToTargetTask;
            result.Check("the edited value is live", edited != null
                && Math.Abs(edited.AcceptableRadius - 6f) < 1e-4f,
                edited == null ? "<missing>" : edited.AcceptableRadius.ToString());

            result.Check("*** ai.edit.* never touches the original package ***",
                string.Equals(PackageLoader.ComputeHash(File.ReadAllBytes(demoFile)),
                    sourceHashBefore, StringComparison.OrdinalIgnoreCase),
                "source hash changed after a command-level edit");

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.edit.set", "id", "move",
                "name", "timeOut", "value", 9f), context) as IDictionary<string, object>;
            result.Check("ai.edit.set rejects a mistyped property name",
                map != null && Equals(map["applied"], false), Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.edit.insert", "parent", "greet",
                "json", @"{ ""id"": ""w_cmd"", ""type"": ""Task.Wait"", ""properties"": { ""seconds"": 1 } }"),
                context) as IDictionary<string, object>;
            result.Check("ai.edit.insert adds a node from a json definition",
                map != null && Equals(map["applied"], true)
                && host.Tree.Root.Find("w_cmd") != null, Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.edit.move", "id", "w_cmd",
                "parent", "sel"), context) as IDictionary<string, object>;
            result.Check("ai.edit.move re-parents a node",
                map != null && Equals(map["applied"], true), Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.edit.remove", "id", "w_cmd"),
                context) as IDictionary<string, object>;
            result.Check("ai.edit.remove takes the node out",
                map != null && Equals(map["applied"], true)
                && host.Tree.Root.Find("w_cmd") == null, Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.export", "name", "cmd_export"),
                context) as IDictionary<string, object>;
            result.Check("ai.tree.export writes a new package with the live edits",
                map != null && Equals(map["exported"], true)
                && File.Exists(Convert.ToString(map["path"])),
                Describe(map));
            result.Check("exporting does not touch the source package either",
                string.Equals(PackageLoader.ComputeHash(File.ReadAllBytes(demoFile)),
                    sourceHashBefore, StringComparison.OrdinalIgnoreCase),
                "source hash changed after export");

            bool exportExists = false;
            try
            {
                AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.export", "name", "cmd_export"),
                    context);
            }
            catch (AiCommandException exception)
            {
                exportExists = exception.Code == "already_exists";
            }
            result.Check("ai.tree.export refuses to overwrite unless told to", exportExists,
                "no already_exists");

            // ---- 动作包（P1）：列表 / 校验 / 回放 / 停止
            System.IO.Directory.CreateDirectory(context.Actions.Directory);
            string actionPath = Path.Combine(context.Actions.Directory, "cmd_action.scatpak");
            PackageWriter.TryWriteFile(actionPath, PlayerAiPackages.BuildSampleActionPackage(), out string actionError);

            map = AiCommandSet.Execute(new AiCommandRequest("ai.action.list"), context)
                as IDictionary<string, object>;
            var actions = map != null ? map["actions"] as List<Dictionary<string, object>> : null;
            result.Check("ai.action.list enumerates the action folder",
                map != null && Convert.ToInt32(map["count"]) == 1 && actions != null
                && Equals(actions[0]["replayable"], true),
                Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.action.validate", "name",
                "cmd_action"), context) as IDictionary<string, object>;
            result.Check("ai.action.validate accepts a P1 action package",
                map != null && Equals(map["ok"], true) && Equals(map["replayable"], true),
                Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.action.play", "name",
                "cmd_action", "repeat", 2), context) as IDictionary<string, object>;
            result.Check("ai.action.play hands the package to the player",
                context.Actions.PlayCount == 1 && context.Actions.Played == "cmd_action",
                Describe(map));

            map = AiCommandSet.Execute(new AiCommandRequest("ai.action.stop"), context)
                as IDictionary<string, object>;
            result.Check("ai.action.stop stops playback", context.Actions.Stopped, Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.action.validate", "name",
                "ghost"), context) as IDictionary<string, object>;
            result.Check("ai.action.validate reports a missing package",
                map != null && Equals(map["ok"], false), Describe(map));

            // 出厂示例（`sample_walk.scatpak`）必须看得见 —— 它是"开箱可跑"的那一份，
            // 现在装在**唯一的包目录**里（以前在 Mod 分发目录，2026-09-12 合并掉了）。
            PackageWriter.TryWriteFile(Path.Combine(context.Actions.Directory,
                "sample_walk.scatpak"), PlayerAiPackages.BuildSampleActionPackage(), out actionError);

            map = AiCommandSet.Execute(new AiCommandRequest("ai.action.list"), context)
                as IDictionary<string, object>;
            actions = map != null ? map["actions"] as List<Dictionary<string, object>> : null;
            Dictionary<string, object> sampleEntry = null;
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    if (Equals(actions[i]["file"], "sample_walk.scatpak"))
                        sampleEntry = actions[i];
                }
            }
            result.Check("ai.action.list finds the factory sample in the package folder",
                map != null && Convert.ToInt32(map["count"]) == 2 && sampleEntry != null
                && Equals(sampleEntry["source"], "instance") && Equals(sampleEntry["writable"], true),
                Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.action.validate", "name",
                "sample_walk"), context) as IDictionary<string, object>;
            result.Check("ai.action.validate resolves the factory sample",
                map != null && Equals(map["ok"], true) && Equals(map["source"], "instance")
                && Equals(map["replayable"], true),
                Describe(map));

            try
            {
                System.IO.File.Delete(Path.Combine(context.Actions.Directory, "sample_walk.scatpak"));
            }
            catch (Exception)
            {
            }

            map = AiCommandSet.Execute(new AiCommandRequest("ai.tree.reload"), context)
                as IDictionary<string, object>;
            reloader.ApplyPending(host.Tree);
            var afterReload = host.Tree.Root.Find("move") as BtMoveToTargetTask;
            result.Check("reload discards the in-memory edits (package is the baseline)",
                afterReload != null && Math.Abs(afterReload.AcceptableRadius - 3f) < 1e-4f
                && !host.Tree.IsDirty,
                afterReload == null ? "<missing>" : afterReload.AcceptableRadius.ToString());

            // 通知排队 → 在"tick 边界"应用（这里手动调用，等价于 PlayerAiRuntime 的帧首钩子）
            string demoPath = Path.Combine(roots.InstanceRoot.Path, PackageTemplates.DemoFile);
            string hash = PackageLoader.ComputeHash(File.ReadAllBytes(demoPath));
            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.tree.notify", "path", demoPath,
                "hash", "sha256:" + hash), context) as IDictionary<string, object>;
            result.Check("ai.tree.notify queues a reload",
                map != null && Equals(map["queued"], true) && reloader.PendingCount == 1,
                Describe(map));

            reloader.PendingCount.ToString();
            bool applied = reloader.ApplyPending(host.Tree);
            result.Check("queued reload is a no-op when content is unchanged",
                !applied && reloader.IgnoredCount == 1, reloader.LastResult.Describe());

            map = AiCommandSet.Execute(new AiCommandRequest("ai.tree.reload"), context) as IDictionary<string, object>;
            result.Check("ai.tree.reload queues a manual reload",
                map != null && Equals(map["queued"], true) && reloader.PendingCount == 1,
                Describe(map));

            int reloadsBefore = reloader.ReloadCount;
            applied = reloader.ApplyPending(host.Tree);
            result.Check("manual reload actually replaces the tree",
                applied && reloader.ReloadCount == reloadsBefore + 1,
                reloader.LastResult.Describe());
            result.Check("reload kept the tree running", host.Tree.IsRunning, "tree stopped after reload");

            bool corrupted = false;
            try
            {
                File.WriteAllBytes(Path.Combine(roots.InstanceRoot.Path, PackageTemplates.CommonFile),
                    new UTF8Encoding(false).GetBytes("not a zip"));
                map = AiCommandSet.Execute(
                    AiCommandRequest.FromArgs("ai.tree.validate", "name", "common.scbtpak"), context)
                    as IDictionary<string, object>;
                corrupted = map != null && Equals(map["ok"], false)
                    && Convert.ToInt32(map["errors"]) > 0;
            }
            catch (Exception)
            {
            }
            result.Check("ai.tree.validate reports a broken package", corrupted, "no errors reported");
        }

        /// <summary>假动作包播放器：命令层自检用（不碰真输入）。</summary>
        private sealed class FakeActionPlayer : IAiActionPlayer
        {
            public string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pai-cmd-actions");

            public string Played;

            public int PlayCount;

            public bool Stopped;

            public string ActionDirectory
            {
                get { return Directory; }
            }

            public List<string> ActionSearchDirectories
            {
                get { return new List<string> { Directory }; }
            }

            public string Play(string nameOrPath, int repeat)
            {
                Played = nameOrPath;
                PlayCount++;
                return "playing " + nameOrPath + " x" + repeat;
            }

            public string Stop()
            {
                Stopped = true;
                return "stopped";
            }

            public Dictionary<string, object> Status()
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["available"] = true,
                    ["playing"] = PlayCount > 0 && !Stopped,
                    ["name"] = Played
                };
            }
        }

        private static void RecordingCommands(BtSelfTest.TestResult result)
        {
            var context = new FakeContext();
            var host = new AiTestHost();
            context.Host = host;

            // 用例可重复运行：先清掉上一次留下的动作包（否则第二轮会撞 already_exists）
            try
            {
                if (System.IO.Directory.Exists(context.Recording.TargetDirectory))
                    System.IO.Directory.Delete(context.Recording.TargetDirectory, true);
                System.IO.Directory.CreateDirectory(context.Recording.TargetDirectory);
            }
            catch (Exception)
            {
                // 清理失败就让断言去暴露问题，不在这里中断自检
            }

            IDictionary<string, object> map = AiCommandSet.Execute(
                AiCommandRequest.FromArgs("ai.record.start", "name", "cmd_rec"), context)
                as IDictionary<string, object>;
            result.Check("ai.record.start begins a recording",
                map != null && Equals(context.Recording.Status.Phase, RecordingPhase.Recording)
                && Equals(context.Recording.Status.Name, "cmd_rec"),
                Describe(map));

            map = AiCommandSet.Execute(new AiCommandRequest("ai.record.pause"), context)
                as IDictionary<string, object>;
            result.Check("ai.record.pause toggles to paused",
                context.Recording.Status.Phase == RecordingPhase.Paused, Describe(map));

            AiCommandSet.Execute(new AiCommandRequest("ai.record.pause"), context);

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.record.stop", "name", "cmd_rec"),
                context) as IDictionary<string, object>;
            result.Check("ai.record.stop with a name saves in one call",
                map != null && Equals(map["saved"], true)
                && context.Recording.Status.Phase == RecordingPhase.Idle,
                Describe(map));
            result.Check("saved action package exists on disk",
                context.Recording.Status.LastSavedPath != null
                && System.IO.File.Exists(context.Recording.Status.LastSavedPath),
                context.Recording.Status.LastSavedPath ?? "<none>");

            bool duplicate = false;
            try
            {
                AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.record.start", "name", "cmd_rec"),
                    context);
                AiCommandSet.Execute(new AiCommandRequest("ai.record.stop"), context);
                AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.record.save", "name", "cmd_rec"),
                    context);
            }
            catch (AiCommandException exception)
            {
                duplicate = exception.Code == "already_exists";
            }
            result.Check("ai.record.save reports already_exists for a duplicate name", duplicate,
                "no already_exists");

            map = AiCommandSet.Execute(new AiCommandRequest("ai.record.discard"), context)
                as IDictionary<string, object>;
            result.Check("ai.record.discard clears the pending capture",
                context.Recording.Status.Phase == RecordingPhase.Idle, Describe(map));

            map = AiCommandSet.Execute(new AiCommandRequest("ai.status"), context)
                as IDictionary<string, object>;
            var recording = map != null ? map["recording"] as IDictionary<string, object> : null;
            result.Check("ai.status reports the recording block",
                recording != null && Equals(recording["available"], true)
                && Equals(recording["phase"], "idle"),
                Describe(map));
        }

        private static void ObservabilityCommands(BtSelfTest.TestResult result)
        {
            var context = new FakeContext();
            var host = new AiTestHost();
            context.Host = host;

            // 快照：手搭一棵最小的树就行（不必真的读包）
            var root = new BtRootNode { Id = "root" };
            var sequence = new BtSequenceNode { Id = "seq" };
            sequence.AddChild(new BtWaitTask { Id = "w", Seconds = 5f });
            root.AddChild(sequence);
            var compiled = new CompiledTree
            {
                Root = root,
                PackageId = "obs.pkg",
                EntryId = "root",
                SourcePath = "obs.pkg.scbtpak",
                SourceHash = "hash-obs",
                NodeCount = 3
            };
            host.LoadTree(compiled, true);
            host.Tree.Tick(1f / 60f);

            IDictionary<string, object> map = AiCommandSet.Execute(new AiCommandRequest("ai.tree.snapshot"),
                context) as IDictionary<string, object>;
            var path = map != null ? map["path"] as List<Dictionary<string, object>> : null;
            result.Check("ai.tree.snapshot describes the active path",
                map != null && path != null && path.Count >= 3
                && Convert.ToString(path[path.Count - 1]["id"]) == "w"
                && map.ContainsKey("blackboard"),
                Describe(map));
            result.Check("ai.tree.snapshot carries per-node state",
                path != null && path.Count > 0 && path[0].ContainsKey("result")
                && path[0].ContainsKey("activeTime") && path[0].ContainsKey("activations"),
                "missing per-node fields");

            // 日志：写一条 → 能读回来
            context.EventLog.Write("selftest-case", "hello from the self-test");
            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.logs", "count", 5), context)
                as IDictionary<string, object>;
            var recent = map != null ? map["recent"] as List<string> : null;
            result.Check("ai.logs returns recent entries",
                map != null && recent != null && recent.Count >= 1
                && recent[recent.Count - 1].Contains("hello from the self-test")
                && Convert.ToString(recent[recent.Count - 1]).Contains("[selftest-case]"),
                Describe(map));
            result.Check("the event log writes to a real file",
                context.EventLog.Path != null && System.IO.File.Exists(context.EventLog.Path),
                context.EventLog.Path ?? "<none>");

            map = AiCommandSet.Execute(new AiCommandRequest("ai.status"), context)
                as IDictionary<string, object>;
            var logs = map != null ? map["logs"] as IDictionary<string, object> : null;
            result.Check("ai.status reports the event log",
                logs != null && Equals(logs["available"], true)
                && Convert.ToInt64(logs["entries"]) >= 1,
                Describe(map));

            map = AiCommandSet.Execute(AiCommandRequest.FromArgs("ai.logs", "clear", true), context)
                as IDictionary<string, object>;
            result.Check("ai.logs clear=true empties the log",
                map != null && Convert.ToInt32(Convert.ToString(map["entries"])) >= 1,
                Describe(map));
        }

        private static void ErrorCodes(BtSelfTest.TestResult result)
        {
            var context = new FakeContext();

            string code = null;
            try
            {
                AiCommandSet.Execute(new AiCommandRequest("ai.nope"), context);
            }
            catch (AiCommandException exception)
            {
                code = exception.Code;
            }
            result.Check("unknown command reports unknown_command", code == "unknown_command", code ?? "<none>");

            code = null;
            try
            {
                AiCommandSet.Execute(new AiCommandRequest("ai.enable"), context);
            }
            catch (AiCommandException exception)
            {
                code = exception.Code;
            }
            result.Check("commands needing a host report not_ready", code == "not_ready", code ?? "<none>");

            code = null;
            try
            {
                AiCommandSet.Execute(new AiCommandRequest("ai.tree.list"), context);
            }
            catch (AiCommandException exception)
            {
                code = exception.Code;
            }
            result.Check("commands needing packages report not_ready", code == "not_ready", code ?? "<none>");

            var host = new AiTestHost();
            context.Host = host;

            // 要让"缺参"这条路径可达，重载器必须先就绪 —— 否则先撞上的是 not_ready（那本身也是对的）。
            // 参数校验发生在读文件之前，所以这里给一个指向临时目录的空重载器就够了。
            var roots = new PackageRoots(Path.Combine(Path.GetTempPath(), "pai-cmd-selftest-args"));
            context.Reloader = new PackageReloader(roots, new PackageLoadOptions { Roots = roots });

            code = null;
            try
            {
                AiCommandSet.Execute(new AiCommandRequest("ai.tree.load"), context);
            }
            catch (AiCommandException exception)
            {
                code = exception.Code;
            }
            result.Check("missing required argument reports invalid_argument",
                code == "invalid_argument", code ?? "<none>");
        }

        private static void SelfTestCommand(BtSelfTest.TestResult result)
        {
            // 用合成的三份结果验证"汇总"本身（不去真的再跑套件 —— 那会自递归）。
            var behaviour = new BtSelfTest.TestResult { Label = "behaviour-stub" };
            behaviour.Check("stub ok", true);
            behaviour.Check("stub fails", false, "intentional");

            var packages = new BtSelfTest.TestResult { Label = "packages-stub" };
            packages.Check("stub ok", true);

            var commands = new BtSelfTest.TestResult { Label = "commands-stub" };
            commands.Check("stub ok", true);

            var modes = new BtSelfTest.TestResult { Label = "modes-stub" };
            modes.Check("stub ok", true);

            Dictionary<string, object> map = AiCommandSet.RunSelfTests(behaviour, packages, commands, modes);
            var suites = map["suites"] as Dictionary<string, object>;
            var failures = map["failures"] as List<string>;

            result.Check("bt.selftest aggregation counts passes and failures",
                Equals(map["passed"], 4) && Equals(map["failed"], 1) && Equals(map["allPassed"], false),
                Describe(map));
            result.Check("bt.selftest aggregation reports per-suite tallies",
                suites != null && Equals(suites["behaviour-stub"], "1/2")
                && Equals(suites["packages-stub"], "1/1") && Equals(suites["commands-stub"], "1/1")
                && Equals(suites["modes-stub"], "1/1"),
                Describe(suites));
            result.Check("bt.selftest aggregation lists failures with the suite label",
                failures != null && failures.Count == 1
                && failures[0].StartsWith("behaviour-stub:", StringComparison.Ordinal),
                failures != null && failures.Count > 0 ? failures[0] : "<none>");

            // 命令层自检自身**不得**调用 bt.selftest：否则 bt.selftest → 命令自检 → bt.selftest 无限递归。
            var empty = AiCommandSet.RunSelfTests(new BtSelfTest.TestResult(),
                new BtSelfTest.TestResult(), new BtSelfTest.TestResult(),
                new BtSelfTest.TestResult());
            result.Check("aggregation is pure (no nested suite execution)",
                Equals(empty["passed"], 0) && Equals(empty["allPassed"], true),
                Describe(empty));
        }

        private static string Describe(object value)
        {
            var map = value as IDictionary<string, object>;
            if (map == null)
                return value == null ? "<null>" : value.ToString();

            var builder = new StringBuilder();
            foreach (KeyValuePair<string, object> pair in map)
            {
                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(pair.Key).Append('=').Append(pair.Value == null ? "<null>" : pair.Value.ToString());
                if (builder.Length > 220)
                    break;
            }
            return builder.ToString();
        }
        /// <summary>
        /// 动作包里的 UI 点击目标怎么解析（用户要求："能获取 UI 的时候就尽可能用 UI 的真实位置，
        /// 别因为窗口尺寸变了就点空"）。这里钉住解析规则本身：
        /// 列表行（按行号 / 按文字）、控件路径（路径里本来就有 `[Type#id]` 的 `#`）、坐标兜底。
        ///
        /// 解析实现只有一份：`CmdBridgeMod/Server/UiTarget.cs`（运行时就是它；它是纯 System 代码，
        /// 所以能编进这个离线自检里）。早先 PlayerAiMod 也有一份自己的解析，两边漂移过一次 —— 已删。
        /// </summary>
        private static void UiClickTargets(BtSelfTest.TestResult result)
        {
            CmdBridgeMod.UiTarget.Parsed parsed;

            result.Check("a list row by index is recognised",
                CmdBridgeMod.UiTarget.TryParse("list:WorldsList#3", out parsed)
                && parsed.Kind == CmdBridgeMod.UiTarget.TargetKind.ListRow
                && parsed.Selector == "WorldsList" && parsed.RowIndex == 3 && parsed.RowText == null,
                parsed.Selector + " / " + parsed.RowIndex + " / " + parsed.RowText);

            result.Check("a list row by text is recognised",
                CmdBridgeMod.UiTarget.TryParse("list:WorldsList@Rebritish", out parsed)
                && parsed.Selector == "WorldsList" && parsed.RowIndex == -1
                && parsed.RowText == "Rebritish",
                parsed.Selector + " / " + parsed.RowIndex + " / " + parsed.RowText);

            result.Check("a widget path stays a selector (paths contain '#' and '/')",
                CmdBridgeMod.UiTarget.TryParse(
                    "[MainMenuScreen#0]/[StackPanelWidget#1]/[StackPanelWidget#5]/Play", out parsed)
                && parsed.Kind == CmdBridgeMod.UiTarget.TargetKind.Selector
                && parsed.Selector.StartsWith("[MainMenuScreen#0]", StringComparison.Ordinal),
                parsed.Kind + " / " + parsed.Selector);

            result.Check("a widget name is a selector, not a list row",
                CmdBridgeMod.UiTarget.TryParse("Play", out parsed)
                && parsed.Kind == CmdBridgeMod.UiTarget.TargetKind.Selector
                && parsed.Selector == "Play", parsed.Kind + " / " + parsed.Selector);

            result.Check("a recorded client point still parses as a point (last-resort fallback)",
                CmdBridgeMod.UiTarget.TryParse("1010.6,64.83", out parsed)
                && parsed.Kind == CmdBridgeMod.UiTarget.TargetKind.Point
                && Math.Abs(parsed.X - 1010.6f) < 0.01f && Math.Abs(parsed.Y - 64.83f) < 0.01f,
                parsed.Kind + " / " + parsed.X + "," + parsed.Y);

            result.Check("formatting a list row round-trips (the recorder writes this form)",
                CmdBridgeMod.UiTarget.TryParse(
                    CmdBridgeMod.UiTarget.FormatListRow("WorldsList", -1, "Rebritish"), out parsed)
                && parsed.RowText == "Rebritish"
                && CmdBridgeMod.UiTarget.TryParse(
                    CmdBridgeMod.UiTarget.FormatListRow("WorldsList", 2, null), out parsed)
                && parsed.RowIndex == 2,
                CmdBridgeMod.UiTarget.FormatListRow("WorldsList", -1, "Rebritish"));

            result.Check("a malformed list target is refused (no guessing)",
                !CmdBridgeMod.UiTarget.TryParse("list:WorldsList@", out parsed)
                && !CmdBridgeMod.UiTarget.TryParse("list:#2", out parsed),
                "list:WorldsList@ / list:#2");
        }

    }
}

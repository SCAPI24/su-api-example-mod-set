using Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PlayerAiMod.Editor
{
    /// <summary>
    /// 编辑器无头自检：起一个真 HTTP 服务，用 HttpClient 把 API 全跑一遍。
    ///
    /// 覆盖：静态资源内嵌可用、schema 与游戏注册表同源、列包/读包、**用游戏同一份校验器**
    /// 校验（好包/坏包）、保存（原子写 + 只读目录拒绝 + 覆盖保护）、保存后能重新装载、
    /// 以及"游戏没在跑时通知热重载要如实报错"（不假装成功）。
    /// 不需要浏览器、不需要游戏。
    /// </summary>
    internal static class EditorSelfTest
    {
        private static int m_passed;
        private static int m_failed;

        public static int Run(EditorApi api, string root)
        {
            m_passed = 0;
            m_failed = 0;

            string instanceRoot = Path.Combine(Path.GetTempPath(), "pai-editor-selftest");
            try
            {
                if (Directory.Exists(instanceRoot))
                    Directory.Delete(instanceRoot, true);
            }
            catch (Exception)
            {
            }
            Directory.CreateDirectory(instanceRoot);

            // 出厂内容：示例树 + 示例动作包（编辑器要能列出它们）—— 全部装在**唯一的包目录**里
            string instanceDirectory = Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees");
            var roots = new PackageRoots(instanceDirectory);
            List<string> installed;
            string installError;
            int installedCount = PackageTemplates.Install(roots, out installed, out installError);
            Check("factory content is installed for the self-test",
                installedCount >= 4 && installError == null,
                "written=" + installedCount + " " + (installError ?? string.Empty));

            var testApi = new EditorApi(instanceRoot);
            var router = new EditorRouter(testApi);

            int port;
            using (var server = new HttpServer(0, router.Handle))
            {
                server.Start();
                port = server.Port;
                Check("editor server starts on loopback", port > 0, "port=" + port);

                using (var client = new HttpClient())
                {
                    client.BaseAddress = new Uri("http://127.0.0.1:" + port);
                    client.Timeout = TimeSpan.FromSeconds(30);

                    StaticAssets(client);
                    Schema(client);
                    Packages(client, instanceRoot, instanceDirectory);
                    Actions(client, instanceRoot);
                    BlankTree(client, instanceDirectory);
                    Subtree(client, instanceRoot);
                    LiveMonitor(client);
                    GameProcess(client, instanceRoot);
                }
            }

            Console.WriteLine();
            Console.WriteLine("PlayerAiEditor 自检：" + m_passed + "/" + (m_passed + m_failed)
                + (m_failed == 0 ? "  ALL PASS" : "  " + m_failed + " FAILED"));
            return m_failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 各项

        private static void StaticAssets(HttpClient client)
        {
            string html = Get(client, "/");
            Check("index.html is embedded and served",
                html != null && html.Contains("PlayerAi") && html.Contains("palette")
                && html.Contains("inspector"),
                Short(html));

            string js = Get(client, "/app.js");
            Check("app.js is embedded and served",
                js != null && js.Contains("renderTree") && js.Contains("PlayerAiLowcode"),
                Short(js));
            Check("app.js wires action packages into the palette and the toolbar",
                js != null && js.Contains("/api/action/play") && js.Contains("packagesField")
                && js.Contains("useAction"),
                Short(js));
            Check("app.js carries undo/redo (snapshot stack + shortcuts)",
                js != null && js.Contains("pushHistory") && js.Contains("function undo")
                && js.Contains("ctrlKey"),
                Short(js));
            Check("drag & drop is implemented with plain mouse events (works in webviews)",
                js != null && js.Contains("function beginMouseDrag")
                && js.Contains("function handleMouseDragEnd") && js.Contains("elementFromPoint")
                && js.Contains("function nodeIdFromPoint"),
                Short(js));
            Check("drag sources turn HTML5 dragging off explicitly",
                js != null && js.Contains("element.draggable = false"), Short(js));
            Check("app.js can jump from a validation issue to its node",
                js != null && js.Contains("issueNodeId") && js.Contains("selectNode"),
                Short(js));
            Check("app.js can create a brand new tree",
                js != null && js.Contains("function newTree") && js.Contains("instanceRoot"),
                Short(js));
            Check("app.js has the live monitor (poll + highlight + pause/resume)",
                js != null && js.Contains("function pollLive") && js.Contains("activeNodeIds")
                && js.Contains("live-active") && js.Contains("/api/game/live"),
                Short(js));
            Check("app.js folds nested packages (Task.Subtree) in place",
                js != null && js.Contains("parseSubtreeRef") && js.Contains("toggleSubtree")
                && js.Contains("/api/subtree") && js.Contains("renderFoldedTree"),
                Short(js));
            Check("app.js supports copy/cut/paste/duplicate/delete of subtrees",
                js != null && js.Contains("function pasteSubtree") && js.Contains("cloneSubtreeForPaste")
                && js.Contains("function duplicateNode") && js.Contains("function deleteNode"),
                Short(js));
            Check("app.js can search nodes and jump to a match",
                js != null && js.Contains("function searchNodes") && js.Contains("jumpToFirstMatch")
                && js.Contains("search-hit"),
                Short(js));
            Check("app.js offers a declared-references picker for Task.Subtree",
                js != null && js.Contains("function packageRefField") && js.Contains("manifest.references"),
                Short(js));
            Check("app.js has a pure tree layout (foundation for the node graph)",
                js != null && js.Contains("function layoutTree") && js.Contains("edges.push"),
                Short(js));
            Check("app.js draws the node graph view (geometry + wires + boxes)",
                js != null && js.Contains("function graphGeometry") && js.Contains("function renderGraph")
                && js.Contains("createElementNS") && js.Contains("graph-wire")
                && js.Contains("graph-order"),
                Short(js));
            Check("app.js can pan the graph by dragging",
                js != null && js.Contains("function makePannable")
                && js.Contains("scrollAfterDrag") && js.Contains("panning"),
                Short(js));
            Check("dragging a node does not get swallowed by the pan handler",
                js != null && js.Contains("function panIntent") && js.Contains("nodeAncestorOf"),
                Short(js));
            Check("the graph re-adapts when the window is resized",
                js != null && js.Contains("function handleWindowResize")
                && js.Contains("graphAutoFit") && js.Contains("'resize'"),
                Short(js));
            Check("the canvas container itself is observed for size changes",
                js != null && js.Contains("ResizeObserver") && js.Contains("observeCanvasResize"),
                Short(js));
            Check("nodes can be restructured without HTML5 drag and drop",
                js != null && js.Contains("function beginMove") && js.Contains("function completeMove")
                && js.Contains("pendingMove"),
                Short(js));
            Check("the graph pans/zooms with the wheel",
                js != null && js.Contains("function wheelIntent") && js.Contains("function makeWheelable")
                && js.Contains("passive: false"),
                Short(js));
            Check("app.js can switch between the tree and graph views",
                js != null && js.Contains("function setView") && js.Contains("function zoomGraph")
                && js.Contains("function fitScale") && js.Contains("fitGraphToWindow"),
                Short(js));
            Check("app.js can export a subtree as its own package",
                js != null && js.Contains("function subtreeExportPayload")
                && js.Contains("function exportSubtree") && js.Contains("/api/package?path="),
                Short(js));
            Check("app.js lists the referenced package's nodes for the Subtree entry picker",
                js != null && js.Contains("function ensureSubtreeEntries")
                && js.Contains("subtreeEntries"),
                Short(js));
            Check("app.js supports multi-select with batch delete and wrapping",
                js != null && js.Contains("function handleSelectClick")
                && js.Contains("function deleteSelection") && js.Contains("function wrapSelection")
                && js.Contains("multi-selected"),
                Short(js));

            string css = Get(client, "/app.css");
            Check("app.css is embedded and served", css != null && css.Contains(".node-row"), Short(css));
            Check("app.css styles the action-package materials",
                css != null && css.Contains(".palette-item.action") && css.Contains(".packages-box"),
                Short(css));
            Check("app.css styles drop targets and issue flags",
                css != null && css.Contains(".drop-child") && css.Contains(".issue-flag"),
                Short(css));
            Check("app.css styles the live monitor highlights",
                css != null && css.Contains(".live-active") && css.Contains(".bb-row"),
                Short(css));
            Check("app.css styles the folded nested-package view",
                css != null && css.Contains(".folded-head") && css.Contains(".node-ref"),
                Short(css));
            Check("app.css styles the node graph (boxes, wires, drop targets)",
                css != null && css.Contains(".gnode") && css.Contains(".graph-wire")
                && css.Contains(".gnode.drop-child") && css.Contains(".graph-order"),
                Short(css));
            Check("app.css styles search hits",
                css != null && css.Contains(".search-hit"), Short(css));
            Check("index.html exposes the node search box",
                html != null && html.Contains("nodeSearch"), Short(html));

            html = Get(client, "/");
            Check("index.html exposes the new/undo/redo toolbar",
                html != null && html.Contains("btnNew") && html.Contains("btnUndo")
                && html.Contains("btnRedo"),
                Short(html));
            // 「构建」这四个字现在包在一个 data-i18n 的 <span> 里（切英文时它是 "Build"），
            // 所以时间戳和它之间隔着 `</span>` —— 断言要认这个结构，不能只找 "构建 "。
            Check("index.html carries a real build stamp (not the placeholder)",
                html != null && html.Contains("buildBadge") && !html.Contains("<!--BUILDSTAMP-->")
                && html.Contains("badge.build")
                && System.Text.RegularExpressions.Regex.IsMatch(
                    html, @"构建\s*(?:</span>)?\s*[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9:]"),
                Short(html));
            Check("the page shows a size diagnostic line",
                html != null && html.Contains("diagLine") && js != null
                && js.Contains("function diagText") && js.Contains("function updateDiag"),
                Short(html));
            Check("responses are never cached (no-store), so a stale app.js cannot linger",
                CachedControl(client), "cache-control header");

            string adapter = Get(client, "/engine-adapter.js");
            Check("lowcode adapter seam is embedded and documents the spike verdict",
                adapter != null && adapter.Contains("lowcode") && adapter.Contains("toMaterials"),
                Short(adapter));
            Check("the adapter maps action packages to materials too",
                adapter != null && adapter.Contains("actionsToMaterials")
                && adapter.Contains("Task.PlayActionPackage"),
                Short(adapter));

            var assets = Program.ListEmbeddedAssets();
            Check("all four web assets are embedded in the exe",
                assets.Count >= 4, string.Join(",", assets.ToArray()));
        }

        private static void Schema(HttpClient client)
        {
            string json = Get(client, "/api/schema");
            PackageValue schema = Parse(json);
            Check("schema lists node types from the game registry",
                schema != null && schema.Get("nodes").Count >= 8,
                "nodes=" + (schema != null ? schema.Get("nodes").Count : -1));

            string types = "";
            for (int i = 0; i < schema.Get("nodes").Count; i++)
                types += schema.Get("nodes").Item(i).Get("type").AsString("") + ",";

            Check("schema carries composite and task shapes",
                types.Contains("Selector") && types.Contains("Task.Wait")
                && types.Contains("Task.PlayActionPackage"), types);
            Check("schema lists decorators and services",
                schema.Get("decorators").Count >= 5 && schema.Get("services").Count >= 1,
                "decorators=" + schema.Get("decorators").Count
                + " services=" + schema.Get("services").Count);
            Check("schema excludes code-only types (no lambdas in the palette)",
                types.IndexOf("Task.Lambda", StringComparison.Ordinal) < 0, types);

            // 属性表要带类型/默认值/枚举候选（前端据此生成控件）
            PackageValue wait = FindNodeType(schema, "Task.Wait");
            PackageValue seconds = wait != null ? FindProperty(wait, "seconds") : null;
            Check("property specs carry kind and default",
                seconds != null && seconds.Get("kind").AsString(null) == "float"
                && seconds.Get("default").AsString(null) == "1",
                seconds != null ? seconds.Preview(80) : "<missing>");

            PackageValue play = FindNodeType(schema, "Task.PlayActionPackage");
            PackageValue mode = play != null ? FindProperty(play, "mode") : null;
            Check("enum properties carry their allowed values",
                mode != null && mode.Get("allowed").Count >= 2,
                mode != null ? mode.Preview(100) : "<missing>");

            // ---- 补上的两个装饰器（用户点名："装饰器是否有缺失"）
            string decorators = "";
            for (int i = 0; i < schema.Get("decorators").Count; i++)
                decorators += schema.Get("decorators").Item(i).Get("type").AsString("") + ",";
            Check("schema carries the ForceFailure and CompareBBEntries decorators",
                decorators.Contains("ForceFailure") && decorators.Contains("CompareBBEntries"),
                decorators);

            // ---- 黑板键属性要标成 blackboardkey（编辑器据此给下拉、校验器据此查"有没有声明"）
            PackageValue blackboardDecorator = FindDecorator(schema, "Blackboard");
            PackageValue keySpec = blackboardDecorator != null
                ? FindProperty(blackboardDecorator, "key") : null;
            Check("blackboard key properties are marked as blackboardkey (so the editor can offer a dropdown)",
                keySpec != null && keySpec.Get("kind").AsString(null) == "blackboardkey"
                && keySpec.Get("required").AsBool(false),
                keySpec != null ? keySpec.Preview(90) : "<missing>");

            PackageValue compare = FindDecorator(schema, "CompareBBEntries");
            PackageValue keyA = compare != null ? FindProperty(compare, "keyA") : null;
            PackageValue op = compare != null ? FindProperty(compare, "operator") : null;
            Check("CompareBBEntries exposes keyA/keyB + operator",
                keyA != null && keyA.Get("kind").AsString(null) == "blackboardkey"
                && FindProperty(compare, "keyB") != null
                && op != null && op.Get("allowed").Count >= 6,
                compare != null ? compare.Preview(160) : "<missing>");

            // ---- 传感器服务族 + 两个新任务（用户要求"服务缺失也要补"）
            string services = "";
            for (int i = 0; i < schema.Get("services").Count; i++)
                services += schema.Get("services").Item(i).Get("type").AsString("") + ",";
            Check("schema carries the sensor service family",
                services.Contains("Service.UpdateSelf")
                && services.Contains("Service.UpdateNearestCreature")
                && services.Contains("Service.UpdateNearestPickable")
                && services.Contains("Service.UpdateBlockAhead")
                && services.Contains("Service.UpdateLineOfSight"),
                services);
            Check("schema carries Task.FaceEntity and Task.Emit",
                types.Contains("Task.FaceEntity") && types.Contains("Task.Emit"), types);

            // 服务的黑板键属性也要标成 blackboardkey（否则属性区给不出下拉）
            PackageValue selfService = FindService(schema, "Service.UpdateSelf");
            Check("service blackboard-key properties are marked as blackboardkey",
                selfService != null
                && FindProperty(selfService, "prefix") != null
                && FindProperty(FindService(schema, "Service.UpdateNearestCreature"), "targetKey")
                    .Get("kind").AsString(null) == "blackboardkey",
                selfService != null ? selfService.Preview(120) : "<missing>");

            KernelBehaviour();
        }

        /// <summary>服务的属性规格（`schema.services` 里按类型找）。</summary>
        private static PackageValue FindService(PackageValue schema, string type)
        {
            if (schema == null || !schema.Get("services").IsArray)
                return null;
            for (int i = 0; i < schema.Get("services").Count; i++)
            {
                PackageValue entry = schema.Get("services").Item(i);
                if (string.Equals(entry.Get("type").AsString(null), type, StringComparison.Ordinal))
                    return entry;
            }
            return null;
        }

        /// <summary>
        /// **内核行为**自检：直接造树、直接 tick，验证新装饰器的语义与新服务的降级行为。
        ///
        /// 为什么放在编辑器自检里：编辑器链接了 `PlayerAiMod/Bt/**` 与 `Package/**` 的源码
        /// （见 csproj），所以这些是**同一份实现**；而内核自检（`BtSelfTest`）要跑 Python 临时工程
        /// 或在游戏里执行 —— 这里能用一条 `--selftest` 就把最关键的语义钉住。
        /// 桩的写法保证"没有游戏也能跑"：传感器一律 not-ready，方块射线能力缺失。
        /// </summary>
        private static void KernelBehaviour()
        {
            // ---- ForceFailure：把 Succeeded 改成 Failed
            {
                var root = new BtRootNode();
                var child = new BtLogTask { Id = "log", Message = "x", Succeed = true };
                child.AddDecorator(new BtForceFailureDecorator { Id = "d" });
                root.AddChild(child);
                var runtime = new BtRuntime(new AiBlackboard(), new NullSensor(), new NullActuator());
                runtime.SetRoot(root, "selftest.forceFailure");
                runtime.Start();
                runtime.Tick(0.1f);
                Check("ForceFailure turns a succeeding task into failure",
                    runtime.LastResult == BtResult.Failed,
                    runtime.LastResult.ToString());
            }

            // ---- CompareBBEntries：数值比较（int 与 float 混用也要能比）
            {
                var blackboard = new AiBlackboard();
                blackboard.Set(new AiBlackboardKey<float>("nearDistance"), 3f);
                blackboard.Set(new AiBlackboardKey<int>("alertDistance"), 10);
                var less = new BtCompareBlackboardDecorator
                { Id = "d", KeyA = "nearDistance", KeyB = "alertDistance", Operator = "<" };
                var greater = new BtCompareBlackboardDecorator
                { Id = "d2", KeyA = "nearDistance", KeyB = "alertDistance", Operator = ">" };
                var runtime = new BtRuntime(blackboard, new NullSensor(), new NullActuator());
                var context = runtime.Context;
                Check("CompareBBEntries compares a float key against an int key",
                    less.CheckCondition(context) && !greater.CheckCondition(context),
                    "3 < 10 = " + less.CheckCondition(context) + ", 3 > 10 = " + greater.CheckCondition(context));

                // 缺一边就是不成立（不是"拿默认值瞎比"）
                blackboard.Remove(new AiBlackboardKey<int>("alertDistance"));
                Check("CompareBBEntries is false when one side is missing",
                    !less.CheckCondition(context), "alertDistance removed");
            }

            // ---- 服务在没有扩展观察能力时**跳过而不抛**（纯逻辑自检环境就是这样）
            {
                var blackboard = new AiBlackboard();
                var runtime = new BtRuntime(blackboard, new NullSensor(), new NullActuator());
                var self = new BtUpdateSelfService { Interval = 0f };
                var block = new BtUpdateBlockAheadService { Interval = 0f };
                var sight = new BtUpdateLineOfSightService { Interval = 0f };
                bool threw = false;
                try
                {
                    self.Tick(runtime.Context);
                    block.Tick(runtime.Context);
                    sight.Tick(runtime.Context);
                }
                catch (Exception exception)
                {
                    threw = true;
                    Check("sensor services degrade gracefully without a world sensor", false,
                        exception.GetType().Name + ": " + exception.Message);
                }
                if (!threw)
                {
                    Check("sensor services degrade gracefully without a world sensor",
                        blackboard.Count == 0 && sight is BtUpdateLineOfSightService,
                        "wrote " + blackboard.Count + " keys");
                }
            }

            // ---- 传感器服务真的会写黑板（用一个最小的扩展传感器桩）
            {
                var blackboard = new AiBlackboard();
                var runtime = new BtRuntime(blackboard, new StubWorldSensor(), new NullActuator());
                var self = new BtUpdateSelfService { Interval = 0f, Prefix = "self." };
                self.Tick(runtime.Context);
                float food;
                Check("Service.UpdateSelf writes the blackboard (self.Food)",
                    blackboard.TryGet("self.Food", out food) && Math.Abs(food - 0.42f) < 1e-4f
                    && blackboard.Has("self.OnGround"),
                    "self.Food=" + (blackboard.TryGet("self.Food", out food) ? food.ToString("0.00") : "<missing>"));
            }

            // ---- Task.FaceEntity：没转到位就继续转（并真的调了执行器的 Look），转到位才成功
            {
                var blackboard = new AiBlackboard();
                // 目标在 +X 方向（yaw 应为 π/2），而桩传感器报的 yaw 是 0.5 → 差得远
                blackboard.Set(new AiBlackboardKey<AiActorView>("wolf"), new AiActorView
                {
                    Name = "wolf", Kind = "creature", Position = new Vector3(20f, 64f, 20f)
                });
                var sensor = new StubWorldSensor();
                var actuator = new RecordingActuator();
                var runtime = new BtRuntime(blackboard, sensor, actuator);
                var face = new BtFaceEntityTask { Id = "face", TargetKey = "wolf", ToleranceDegrees = 10f };
                var root = new BtRootNode();
                root.AddChild(face);
                runtime.SetRoot(root, "selftest.face");
                runtime.Start();
                runtime.Tick(0.05f);
                Check("Task.FaceEntity keeps turning while the yaw is off (and calls the actuator)",
                    face.LastResult == BtResult.InProgress && actuator.LookCount > 0,
                    "result=" + face.LastResult + " lookCalls=" + actuator.LookCount);

                // 把传感器摆到"已经面向目标"（yaw = π/2）→ 下一次 tick 应该成功。
                // 注意断言的对象：树一旦**完成**，`BtRuntime.Tick` 会 `ResetSubtreeState()`，
                // 把每个节点的 `LastResult` 复位成 Failed、`ActiveTime` 归零 —— 所以要看
                // `runtime.LastResult`（运行时在复位**之前**记下的那一份），
                // 看节点自己只会看到一片"Failed"（这个坑第一次写这条断言时就踩了）。
                sensor.Yaw = MathUtils.PI / 2f;
                runtime.Tick(0.05f);
                Check("Task.FaceEntity succeeds once the yaw is within tolerance",
                    runtime.LastResult == BtResult.Succeeded,
                    "tree=" + runtime.LastResult + " yaw=" + sensor.Yaw.ToString("0.000"));
                Check("a completed tree resets per-node state (node LastResult is not the tree verdict)",
                    face.LastResult == BtResult.Failed && face.ActiveTime == 0f,
                    "node=" + face.LastResult + " activeTime=" + face.ActiveTime.ToString("0.000"));

                // 目标丢了（服务会清键）→ 失败，让树走备用分支
                blackboard.Remove(new AiBlackboardKey<AiActorView>("wolf"));
                var face2 = new BtFaceEntityTask { Id = "face2", TargetKey = "wolf" };
                var runtime2 = new BtRuntime(blackboard, sensor, actuator);
                var root2 = new BtRootNode();
                root2.AddChild(face2);
                runtime2.SetRoot(root2, "selftest.face2");
                runtime2.Start();
                runtime2.Tick(0.05f);
                Check("Task.FaceEntity fails when the target is gone",
                    runtime2.LastResult == BtResult.Failed, "tree=" + runtime2.LastResult);
            }

            // ---- Task.NavigateTo：用预置路径走完（近→远），到位即成功，并且真的按住前进键
            {
                var blackboard = new AiBlackboard();
                blackboard.Set(new AiBlackboardKey<AiActorView>("target"), new AiActorView
                {
                    Name = "target", Kind = "creature", Position = new Vector3(16f, 64f, 20f)
                });
                var sensor = new StubWorldSensor
                {
                    Path = new[]
                    {
                        new Vector3(12f, 64f, 20f), new Vector3(14f, 64f, 20f),
                        new Vector3(16f, 64f, 20f)
                    }
                };
                var actuator = new RecordingActuator();
                var navigate = new BtNavigateToTask
                { Id = "nav", TargetKey = "target", AcceptableRadius = 2.5f };
                var root = new BtRootNode();
                root.AddChild(navigate);
                var runtime = new BtRuntime(blackboard, sensor, actuator);
                runtime.SetRoot(root, "selftest.navigate");
                runtime.Start();
                runtime.Tick(0.1f);
                Check("Task.NavigateTo requests exactly one path and holds the forward key",
                    sensor.PathRequestCount == 1 && actuator.LastHeldKey == "w" && actuator.LastHeldDown
                    && actuator.LookAtCount > 0,
                    "requests=" + sensor.PathRequestCount + " held=" + actuator.LastHeldKey
                    + "/" + actuator.LastHeldDown + " lookAt=" + actuator.LookAtCount);

                // 走到第二个航点附近 → 仍在跟随后面的航点（不重新请求：路径还新鲜）
                sensor.BodyPosition = new Vector3(12.2f, 64f, 20f);
                runtime.Tick(0.1f);
                Check("Task.NavigateTo keeps following the same path (no request storm)",
                    sensor.PathRequestCount == 1 && runtime.LastResult == BtResult.InProgress,
                    "requests=" + sensor.PathRequestCount + " tree=" + runtime.LastResult);

                // 走到目标附近 → 成功
                sensor.BodyPosition = new Vector3(15.5f, 64f, 20f);
                runtime.Tick(0.1f);
                Check("Task.NavigateTo succeeds on arrival",
                    runtime.LastResult == BtResult.Succeeded, "tree=" + runtime.LastResult);

                // 队列满（游戏给"空且已完成"）→ 不判失败，退化成直线走
                var blackboard2 = new AiBlackboard();
                blackboard2.Set(new AiBlackboardKey<AiActorView>("target"), new AiActorView
                {
                    Name = "target", Kind = "creature", Position = new Vector3(30f, 64f, 20f)
                });
                var sensor2 = new StubWorldSensor { PathQueueFull = true, PathReady = false };
                var actuator2 = new RecordingActuator();
                var navigate2 = new BtNavigateToTask { Id = "nav2", TargetKey = "target" };
                var root3 = new BtRootNode();
                root3.AddChild(navigate2);
                var runtime3 = new BtRuntime(blackboard2, sensor2, actuator2);
                runtime3.SetRoot(root3, "selftest.navigate2");
                runtime3.Start();
                runtime3.Tick(0.1f);
                Check("Task.NavigateTo falls back to walking straight when no path is available",
                    runtime3.LastResult == BtResult.InProgress && actuator2.LastHeldDown,
                    "tree=" + runtime3.LastResult + " held=" + actuator2.LastHeldDown);
            }

            // ---- Task.NavigateTo 的坐标模式（source=cell）：目标来自三个 int 键，不是黑板里的 actor
            {
                var blackboard = new AiBlackboard();
                blackboard.Set(new AiBlackboardKey<int>("goalX"), 40);
                blackboard.Set(new AiBlackboardKey<int>("goalY"), 64);
                blackboard.Set(new AiBlackboardKey<int>("goalZ"), 20);
                var sensor = new StubWorldSensor
                {
                    BodyPosition = new Vector3(30f, 64f, 20f),
                    Path = new[] { new Vector3(38f, 64f, 20f), new Vector3(40.5f, 64f, 20.5f) }
                };
                var actuator = new RecordingActuator();
                var navigate = new BtNavigateToTask
                {
                    Id = "navCell", Source = "cell",
                    XKey = "goalX", YKey = "goalY", ZKey = "goalZ", AcceptableRadius = 2.5f
                };
                var root = new BtRootNode();
                root.AddChild(navigate);
                var runtime = new BtRuntime(blackboard, sensor, actuator);
                runtime.SetRoot(root, "selftest.navigate.cell");
                runtime.Start();
                runtime.Tick(0.1f);
                Vector3 requested = sensor.LastPathDestination;
                Check("Task.NavigateTo (source=cell) requests a path to the cell centre",
                    sensor.PathRequestCount == 1
                    && Math.Abs(requested.X - 40.5f) < 1e-3f && Math.Abs(requested.Y - 64f) < 1e-3f
                    && Math.Abs(requested.Z - 20.5f) < 1e-3f,
                    "requests=" + sensor.PathRequestCount + " dest=" + requested.X.ToString("0.0")
                    + "," + requested.Y.ToString("0.0") + "," + requested.Z.ToString("0.0"));

                // 走不到那一格：清掉一个坐标键 → 直接失败（而不是傻站着走直线）
                var blackboard4 = new AiBlackboard();
                blackboard4.Set(new AiBlackboardKey<int>("goalX"), 40);
                blackboard4.Set(new AiBlackboardKey<int>("goalY"), 64);
                var sensor4 = new StubWorldSensor { BodyPosition = new Vector3(30f, 64f, 20f) };
                var navigate4 = new BtNavigateToTask
                {
                    Id = "navCell2", Source = "cell",
                    XKey = "goalX", YKey = "goalY", ZKey = "goalZ"
                };
                var root4 = new BtRootNode();
                root4.AddChild(navigate4);
                var runtime4 = new BtRuntime(blackboard4, sensor4, new RecordingActuator());
                runtime4.SetRoot(root4, "selftest.navigate.cellMissing");
                runtime4.Start();
                runtime4.Tick(0.1f);
                Check("Task.NavigateTo (source=cell) fails when a goal coordinate is missing",
                    runtime4.LastResult == BtResult.Failed && sensor4.PathRequestCount == 0,
                    "tree=" + runtime4.LastResult + " requests=" + sensor4.PathRequestCount);
            }

            // ---- 交互任务族：判据都取自只读观察
            {
                var blackboard = new AiBlackboard();
                blackboard.Set(new AiBlackboardKey<int>("mineX"), 10);
                blackboard.Set(new AiBlackboardKey<int>("mineY"), 63);
                blackboard.Set(new AiBlackboardKey<int>("mineZ"), 20);
                var sensor = new StubWorldSensor { BlockContents = 1 };
                var actuator = new RecordingActuator();
                var mine = new BtMineBlockTask { Id = "mine" };
                var root = new BtRootNode();
                root.AddChild(mine);
                var runtime = new BtRuntime(blackboard, sensor, actuator);
                runtime.SetRoot(root, "selftest.mine");
                runtime.Start();
                runtime.Tick(0.1f);
                Check("Task.Mine holds the left button while the block is still there",
                    mine.LastResult == BtResult.InProgress && actuator.LastMouseButton == "left"
                    && actuator.MouseButtonCount > 0 && actuator.LookAtCount > 0,
                    "result=" + mine.LastResult + " button=" + actuator.LastMouseButton);

                sensor.BlockContents = 0;                     // 挖掉了
                runtime.Tick(0.1f);
                Check("Task.Mine succeeds when the cell becomes air (and releases the button)",
                    runtime.LastResult == BtResult.Succeeded, "tree=" + runtime.LastResult);

                // 攻击：目标还在就连点，目标从黑板消失即成功
                var blackboard2 = new AiBlackboard();
                blackboard2.Set(new AiBlackboardKey<AiActorView>("target"), new AiActorView
                {
                    Name = "wolf", Kind = "creature", Position = new Vector3(11f, 64f, 20f), Distance = 1f
                });
                var sensor2 = new StubWorldSensor();
                var actuator2 = new RecordingActuator();
                var attack = new BtAttackTask { Id = "attack" };
                var root4 = new BtRootNode();
                root4.AddChild(attack);
                var runtime4 = new BtRuntime(blackboard2, sensor2, actuator2);
                runtime4.SetRoot(root4, "selftest.attack");
                runtime4.Start();
                runtime4.Tick(0.1f);
                Check("Task.Attack clicks the mouse and keeps looking at the target",
                    attack.LastResult == BtResult.InProgress && actuator2.MouseClickCount > 0
                    && actuator2.LastMouseButton == "left" && actuator2.LookAtCount > 0,
                    "result=" + attack.LastResult + " clicks=" + actuator2.MouseClickCount);

                blackboard2.Remove(new AiBlackboardKey<AiActorView>("target"));
                runtime4.Tick(0.1f);
                Check("Task.Attack succeeds once the target disappears",
                    runtime4.LastResult == BtResult.Succeeded, "tree=" + runtime4.LastResult);

                // 交互（对坐标点右键）与选快捷栏（按数字键）
                var blackboard3 = new AiBlackboard();
                blackboard3.Set(new AiBlackboardKey<int>("interactX"), 10);
                blackboard3.Set(new AiBlackboardKey<int>("interactY"), 63);
                blackboard3.Set(new AiBlackboardKey<int>("interactZ"), 20);
                var actuator3 = new RecordingActuator();
                var interact = new BtInteractTask { Id = "interact" };
                var slot = new BtSelectSlotTask { Id = "slot", Slot = 3 };
                var root5 = new BtRootNode();
                root5.AddChild(interact);
                root5.AddChild(slot);
                var runtime5 = new BtRuntime(blackboard3, new StubWorldSensor(), actuator3);
                runtime5.SetRoot(root5, "selftest.interact");
                runtime5.Start();
                runtime5.Tick(0.1f);
                Check("Task.Interact right-clicks the target cell",
                    actuator3.MouseClickCount > 0 && actuator3.LastMouseButton == "right",
                    "clicks=" + actuator3.MouseClickCount + " button=" + actuator3.LastMouseButton);
                runtime5.Tick(0.1f);
                Check("Task.SelectSlot pulses the hotbar digit key",
                    actuator3.PulseKeyCount > 0 && actuator3.LastPulseKey == "3",
                    "pulses=" + actuator3.PulseKeyCount + " key=" + actuator3.LastPulseKey);
            }

            // ---- 用手上的物品 / 跳：这两条是"没有目标格"的输入动作（吃/喝、跳台阶）
            {
                var actuator = new RecordingActuator();
                var use = new BtUseItemTask { Id = "use", HoldSeconds = 0.5f, Repeat = 1 };
                var root = new BtRootNode();
                root.AddChild(use);
                var runtime = new BtRuntime(new AiBlackboard(), new StubWorldSensor(), actuator);
                runtime.SetRoot(root, "selftest.useitem");
                runtime.Start();
                runtime.Tick(0.1f);
                Check("Task.UseItem holds the right button across frames (eating is a hold, not a click)",
                    use.LastResult == BtResult.InProgress && actuator.LastMouseButton == "right"
                    && actuator.LastMouseDown && actuator.MouseClickCount == 0,
                    "result=" + use.LastResult + " button=" + actuator.LastMouseButton
                    + "/" + actuator.LastMouseDown + " clicks=" + actuator.MouseClickCount);

                float elapsed = 0.1f;
                while (elapsed < 0.7f && use.LastResult == BtResult.InProgress)
                {
                    runtime.Tick(0.1f);
                    elapsed += 0.1f;
                }
                Check("Task.UseItem releases the button and succeeds after the hold",
                    runtime.LastResult == BtResult.Succeeded && !actuator.LastMouseDown,
                    "tree=" + runtime.LastResult + " down=" + actuator.LastMouseDown
                    + " elapsed=" + elapsed.ToString("0.0"));

                // 失焦（输入不被接受）时必须**如实失败**，不能"按住了一段就报成功"
                var blind = new StubWorldSensor { InputAccepted = false };
                var actuator2 = new RecordingActuator();
                var use2 = new BtUseItemTask { Id = "use2" };
                var root2 = new BtRootNode();
                root2.AddChild(use2);
                var runtime2 = new BtRuntime(new AiBlackboard(), blind, actuator2);
                runtime2.SetRoot(root2, "selftest.useitem.blind");
                runtime2.Start();
                runtime2.Tick(0.1f);
                Check("Task.UseItem fails honestly when the window does not accept input",
                    runtime2.LastResult == BtResult.Failed, "tree=" + runtime2.LastResult);

                // 跳：脉冲跳跃键；alsoForward 时按住前进键，做完要松开
                var actuator3 = new RecordingActuator();
                var jump = new BtJumpTask { Id = "jump", Times = 1 };
                var root3 = new BtRootNode();
                root3.AddChild(jump);
                var runtime3 = new BtRuntime(new AiBlackboard(), new StubWorldSensor(), actuator3);
                runtime3.SetRoot(root3, "selftest.jump");
                runtime3.Start();
                runtime3.Tick(0.1f);
                Check("Task.Jump pulses the jump key",
                    runtime3.LastResult == BtResult.Succeeded && actuator3.PulseKeyCount == 1
                    && actuator3.LastPulseKey == "space",
                    "tree=" + runtime3.LastResult + " pulses=" + actuator3.PulseKeyCount
                    + " key=" + actuator3.LastPulseKey);

                var actuator4 = new RecordingActuator();
                var jump2 = new BtJumpTask
                { Id = "jump2", Times = 2, Interval = 0.05f, AlsoForward = true };
                var root4 = new BtRootNode();
                root4.AddChild(jump2);
                var runtime4 = new BtRuntime(new AiBlackboard(), new StubWorldSensor(), actuator4);
                runtime4.SetRoot(root4, "selftest.jump.forward");
                runtime4.Start();
                runtime4.Tick(0.1f);
                Check("Task.Jump (alsoForward) holds the forward key while jumping",
                    actuator4.LastHeldKey == "w" && actuator4.LastHeldDown
                    && actuator4.PulseKeyCount == 1,
                    "held=" + actuator4.LastHeldKey + "/" + actuator4.LastHeldDown
                    + " pulses=" + actuator4.PulseKeyCount);

                runtime4.Tick(0.1f);
                Check("Task.Jump (alsoForward) releases the forward key when done",
                    runtime4.LastResult == BtResult.Succeeded && actuator4.PulseKeyCount == 2
                    && actuator4.LastHeldKey == "w" && !actuator4.LastHeldDown,
                    "tree=" + runtime4.LastResult + " pulses=" + actuator4.PulseKeyCount
                    + " held=" + actuator4.LastHeldKey + "/" + actuator4.LastHeldDown);
            }

            // ---- 示例·盯住最近的玩家（su.watch）：反应式三件套的语义钉死
            //
            // 这是"目标会动 + 可见性会翻转"的那类树，四条语义必须成立：
            //   ① 看得见时**每个 tick 都对准他当前的位置**（用瞄准点断言，不只看次数）；
            //   ② 他动了 → 瞄准点跟着变（不是记住旧位置）；
            //   ③ 看不见时**一次对准都不再发出**（"视线不动"写成断言）；
            //   ④ 重新看得见 → 立刻恢复，且用的是新位置。
            {
                var sensor = new StubWorldSensor
                {
                    BodyPosition = new Vector3(0f, 64f, 0f),
                    PlayerFound = true,
                    LineOfSight = true,
                    NearestPlayer = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(10f, 64f, 0f), Distance = 10f
                    }
                };
                var actuator = new RecordingActuator();
                var blackboard = new AiBlackboard();

                var seq = new BtSequenceNode { Id = "seq" };
                seq.AddService(new BtUpdateNearestPlayerService
                { Id = "svc_player", TargetKey = "player", ClearWhenMissing = true, Interval = 0f });
                seq.AddService(new BtUpdateLineOfSightService
                {
                    Id = "svc_los", TargetKey = "player", Key = "canSee",
                    MaxDistance = 48f, Interval = 0f
                });

                var watch = new BtSequenceNode { Id = "watch" };
                watch.AddDecorator(new BtBlackboardDecorator
                {
                    Id = "d_cansee", Key = "canSee", Query = "Compare",
                    ValueKind = "bool", Operator = "==", BoolValue = true,
                    Abort = BtAbortMode.LowerPriority
                });
                var look = new BtLookAtTargetTask
                { Id = "look", TargetKey = "player", EyeHeight = 1.55f, Seconds = 0.1f };
                watch.AddChild(look);

                var selector = new BtSelectorNode { Id = "sel" };
                selector.AddChild(watch);
                selector.AddChild(new BtWaitTask { Id = "idle", Seconds = 0.1f });

                seq.AddChild(selector);
                var root = new BtRootNode { Id = "root", Loop = true };
                root.AddChild(seq);

                var runtime = new BtRuntime(blackboard, sensor, actuator);
                runtime.SetRoot(root, "selftest.watch");
                runtime.Start();

                runtime.Tick(0.05f);
                Vector3 aim = actuator.LastLookAtPoint;
                Check("watch: aims at the player's eye height while he is visible",
                    actuator.HasLookAtPoint && actuator.LookAtCount > 0
                    && Math.Abs(aim.X - 10f) < 1e-3f && Math.Abs(aim.Y - 65.55f) < 1e-3f
                    && Math.Abs(aim.Z) < 1e-3f,
                    "lookAt=" + actuator.LookAtCount + " aim=" + aim.X.ToString("0.00") + ","
                    + aim.Y.ToString("0.00") + "," + aim.Z.ToString("0.00"));

                // 他动了 → 下一个 tick 的瞄准点必须跟着变
                sensor.NearestPlayer = new AiActorView
                {
                    Name = "client", Kind = "player", IsPlayer = true,
                    Position = new Vector3(4f, 64f, 6f), Distance = 7.2f
                };
                runtime.Tick(0.05f);
                Check("watch: re-aims every tick as the player moves",
                    Math.Abs(actuator.LastLookAtPoint.X - 4f) < 1e-3f
                    && Math.Abs(actuator.LastLookAtPoint.Z - 6f) < 1e-3f,
                    "aim=" + actuator.LastLookAtPoint.X.ToString("0.00") + ","
                    + actuator.LastLookAtPoint.Z.ToString("0.00"));

                // 被挡住 → 不能再有任何对准（"视线停在原地"）
                sensor.LineOfSight = false;
                runtime.Tick(0.05f);
                int frozen = actuator.LookAtCount;
                for (int i = 0; i < 6; i++)
                    runtime.Tick(0.05f);
                Check("watch: issues no look command at all while the player is hidden",
                    actuator.LookAtCount == frozen,
                    "before=" + frozen + " after=" + actuator.LookAtCount);

                // 重新看得见（而且他换了个位置）→ 立刻恢复对准新位置
                sensor.NearestPlayer = new AiActorView
                {
                    Name = "client", Kind = "player", IsPlayer = true,
                    Position = new Vector3(-8f, 64f, 2f), Distance = 8.2f
                };
                sensor.LineOfSight = true;
                bool resumed = false;
                for (int i = 0; i < 4 && !resumed; i++)
                {
                    runtime.Tick(0.05f);
                    resumed = actuator.LookAtCount > frozen;
                }
                Check("watch: resumes tracking as soon as he is visible again",
                    resumed && Math.Abs(actuator.LastLookAtPoint.X + 8f) < 1e-3f
                    && Math.Abs(actuator.LastLookAtPoint.Z - 2f) < 1e-3f,
                    "lookAt=" + actuator.LookAtCount + " aim="
                    + actuator.LastLookAtPoint.X.ToString("0.00") + ","
                    + actuator.LastLookAtPoint.Z.ToString("0.00"));

                // 目标消失（客户端断开）→ 清键 → 仍然只是"不动"，不会去盯旧坐标
                sensor.PlayerFound = false;
                runtime.Tick(0.05f);
                int afterLeave = actuator.LookAtCount;
                for (int i = 0; i < 4; i++)
                    runtime.Tick(0.05f);
                bool canSee;
                blackboard.TryGet(new AiBlackboardKey<bool>("canSee"), out canSee);
                Check("watch: a vanished player clears the key and the gaze stays put",
                    actuator.LookAtCount == afterLeave
                    && !blackboard.Has("player") && !canSee,
                    "lookAt=" + actuator.LookAtCount + " hasPlayer=" + blackboard.Has("player")
                    + " canSee=" + canSee);
            }

            // ---- 视线服务的"瞄哪里"：只露出头也必须算看得见
            //
            // 用户实测报的问题："客户端只露出头的时候，本地看不到。"
            // 根因：第一版拿 `target.Position`（**脚底**）打射线，躲在 1 格高的墙后时
            // 到脚的射线被挡住 → canSee=false → 盯人树把视线冻住，可人明明露着头。
            // 修好后的语义：瞄**头**（脚底 + targetEyeHeight），再顺带瞄**胸口**，任一通畅即算看得见。
            {
                var sensor = new StubWorldSensor
                {
                    BodyPosition = new Vector3(0f, 64f, 0f),
                    PlayerFound = true,
                    LineOfSight = true,
                    NearestPlayer = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(6f, 64f, 0f), Distance = 6f
                    }
                };
                var blackboard = new AiBlackboard();
                var los = new BtUpdateLineOfSightService
                { Id = "los", TargetKey = "player", Key = "canSee", Interval = 0f };
                var seq = new BtSequenceNode { Id = "seq" };
                seq.AddService(new BtUpdateNearestPlayerService
                { Id = "svc_player", TargetKey = "player", ClearWhenMissing = true, Interval = 0f });
                seq.AddService(los);
                seq.AddChild(new BtWaitTask { Id = "idle", Seconds = 0.05f });
                var root = new BtRootNode { Id = "root", Loop = true };
                root.AddChild(seq);
                var runtime = new BtRuntime(blackboard, sensor, new RecordingActuator());
                runtime.SetRoot(root, "selftest.los");
                runtime.Start();

                // 脚底 64、胸口 64.9、头 65.55
                // 窗口 1「只露头」：1 格高的墙挡到 65 → 头这条通就够了
                sensor.LowestVisibleY = 65f;
                sensor.HighestVisibleY = float.MaxValue;
                runtime.Tick(0.05f);
                bool headOnly;
                blackboard.TryGet(new AiBlackboardKey<bool>("canSee"), out headOnly);
                Check("line of sight: a target showing only his head still counts as visible",
                    headOnly, "canSee=" + headOnly);

                // 窗口 2「头顶有屋檐」：头那条被挡、胸口看得见 → 备用射线救回来，且两条都问过
                sensor.LineOfSightPoints.Clear();
                sensor.LowestVisibleY = float.MinValue;
                sensor.HighestVisibleY = 65f;
                runtime.Tick(0.05f);
                bool bodySaved;
                blackboard.TryGet(new AiBlackboardKey<bool>("canSee"), out bodySaved);
                bool askedHead = false;
                bool askedBody = false;
                for (int i = 0; i < sensor.LineOfSightPoints.Count; i++)
                {
                    float height = sensor.LineOfSightPoints[i].Y - 64f;
                    if (Math.Abs(height - 1.55f) < 1e-3f)
                        askedHead = true;
                    if (Math.Abs(height - 0.9f) < 1e-3f)
                        askedBody = true;
                }
                Check("line of sight: the rays are aimed at the head and then the chest (never the feet)",
                    bodySaved && askedHead && askedBody,
                    "canSee=" + bodySaved + " points=" + sensor.LineOfSightPoints.Count
                    + " head=" + askedHead + " body=" + askedBody);

                // 窗口 3：整个人都被挡住（66 以上才可见）→ 两条都不通 → 不可见
                sensor.LowestVisibleY = 66f;
                sensor.HighestVisibleY = float.MaxValue;
                runtime.Tick(0.05f);
                bool blocked;
                blackboard.TryGet(new AiBlackboardKey<bool>("canSee"), out blocked);
                Check("line of sight: fully blocked still reads as not visible",
                    !blocked, "canSee=" + blocked);

                // 窗口 4：同样的"头顶有屋檐"，但关掉胸口那条 → 不再算看得见
                // （证明"救回来"的确实是那条备用射线，不是别的原因）
                sensor.LowestVisibleY = float.MinValue;
                sensor.HighestVisibleY = 65f;
                los.AlsoCheckBody = false;
                runtime.Tick(0.05f);
                bool headOnlyNoBody;
                blackboard.TryGet(new AiBlackboardKey<bool>("canSee"), out headOnlyNoBody);
                Check("line of sight: alsoCheckBody=false falls back to the single head ray",
                    !headOnlyNoBody, "canSee=" + headOnlyNoBody);
                los.AlsoCheckBody = true;
            }

            // ---- 通用件三件套：模型节点跟踪 + 通用射线（Probe）+ 看向任意点
            //
            // 用户要求："射线检测是否能加入，或者增加模型节点跟踪，这样就可以根据射线检测来查看
            // 类似这种能看到头的定位，或者模型的其他节点的跟踪。"
            // 这一段的断言就是那句话的可执行版本：
            //   ① 骨骼位置 = 目标脚底 + 该骨骼的静止姿态偏移；
            //   ② 从眼睛打向该骨骼的射线，"通畅"= 看得见（mode=clear 的主键就是这个意思）；
            //   ③ 射线被挡在更近处 → 立刻变成"看不见"；
            //   ④ LookAtPoint 每 tick 对准那个点（头动了也跟着动）；
            //   ⑤ 骨骼名字写错 → Exists=false 且坐标键被清掉（不会拿旧坐标去瞄）。
            {
                var sensor = new StubWorldSensor
                {
                    BodyPosition = new Vector3(0f, 64f, 0f),
                    PlayerFound = true,
                    NearestPlayer = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(8f, 65f, 0f), Distance = 8f
                    }
                };
                sensor.Bones["Head"] = new Vector3(0f, 1.55f, 0f);
                sensor.Bones["Body"] = new Vector3(0f, 0.9f, 0f);

                var actuator = new RecordingActuator();
                var blackboard = new AiBlackboard();
                var seq = new BtSequenceNode { Id = "seq" };
                seq.AddService(new BtUpdateNearestPlayerService
                { Id = "svc_player", TargetKey = "player", ClearWhenMissing = true, Interval = 0f });

                var modelNode = new BtUpdateModelNodeService
                {
                    Id = "svc_head", TargetKey = "player", NodeName = "Head", Prefix = "node.",
                    Interval = 0f
                };
                seq.AddService(modelNode);

                var probe = new BtProbeService
                {
                    Id = "svc_see", From = "eye", To = "point", Mode = "clear", Key = "seeHead",
                    ToXKey = "node.X", ToYKey = "node.Y", ToZKey = "node.Z",
                    MaxDistance = 48f, Interval = 0f
                };
                seq.AddService(probe);

                var watch = new BtSequenceNode { Id = "watch" };
                watch.AddDecorator(new BtBlackboardDecorator
                {
                    Id = "d_see", Key = "seeHead", Query = "Compare", ValueKind = "bool",
                    Operator = "==", BoolValue = true, Abort = BtAbortMode.LowerPriority
                });
                watch.AddChild(new BtLookAtPointTask
                { Id = "look", XKey = "node.X", YKey = "node.Y", ZKey = "node.Z", Seconds = 0.1f });

                var selector = new BtSelectorNode { Id = "sel" };
                selector.AddChild(watch);
                selector.AddChild(new BtWaitTask { Id = "idle", Seconds = 0.1f });
                seq.AddChild(selector);

                var root = new BtRootNode { Id = "root", Loop = true };
                root.AddChild(seq);
                var runtime = new BtRuntime(blackboard, sensor, actuator);
                runtime.SetRoot(root, "selftest.modelnode");
                runtime.Start();
                runtime.Tick(0.05f);

                float nodeX;
                float nodeY;
                float nodeZ;
                bool exists;
                bool seeHead;
                blackboard.TryGet("node.X", out nodeX);
                blackboard.TryGet("node.Y", out nodeY);
                blackboard.TryGet("node.Z", out nodeZ);
                blackboard.TryGet("node.Exists", out exists);
                blackboard.TryGet("seeHead", out seeHead);
                Check("model node: the head position is the body position plus the bone offset",
                    exists && Math.Abs(nodeX - 8f) < 1e-3f && Math.Abs(nodeY - 66.55f) < 1e-3f
                    && Math.Abs(nodeZ) < 1e-3f,
                    "exists=" + exists + " node=(" + nodeX.ToString("0.00") + ","
                    + nodeY.ToString("0.00") + "," + nodeZ.ToString("0.00") + ")");
                Check("probe: eye → head bone reads as visible while nothing blocks it",
                    seeHead && actuator.HasLookAtPoint
                    && Math.Abs(actuator.LastLookAtPoint.Y - 66.55f) < 1e-3f,
                    "seeHead=" + seeHead + " aim=" + actuator.LastLookAtPoint.Y.ToString("0.00"));

                // 墙出现在 3 m 处（比到头的 8 m 更近）→ 看不见 → 视线冻结
                sensor.RayHitDistance = 3f;
                runtime.Tick(0.05f);
                int frozen = actuator.LookAtCount;
                for (int i = 0; i < 4; i++)
                    runtime.Tick(0.05f);
                blackboard.TryGet("seeHead", out seeHead);
                Check("probe: a wall closer than the head turns 'seeHead' false and freezes the gaze",
                    !seeHead && actuator.LookAtCount == frozen,
                    "seeHead=" + seeHead + " lookAt=" + actuator.LookAtCount + "/" + frozen);

                // 墙挪到 20 m（比目标远）→ 又看得见，而且瞄准点跟着目标的新位置
                sensor.RayHitDistance = 20f;
                sensor.NearestPlayer = new AiActorView
                {
                    Name = "client", Kind = "player", IsPlayer = true,
                    Position = new Vector3(5f, 65f, 3f), Distance = 5.8f
                };
                runtime.Tick(0.05f);
                runtime.Tick(0.05f);
                blackboard.TryGet("seeHead", out seeHead);
                Check("probe: a wall behind the head keeps it visible (reached, not blocked)",
                    seeHead, "seeHead=" + seeHead);
                Check("model node + LookAtPoint follow the target as he moves",
                    Math.Abs(actuator.LastLookAtPoint.X - 5f) < 1e-3f
                    && Math.Abs(actuator.LastLookAtPoint.Z - 3f) < 1e-3f
                    && Math.Abs(actuator.LastLookAtPoint.Y - 66.55f) < 1e-3f,
                    "aim=(" + actuator.LastLookAtPoint.X.ToString("0.00") + ","
                    + actuator.LastLookAtPoint.Y.ToString("0.00") + ","
                    + actuator.LastLookAtPoint.Z.ToString("0.00") + ")");

                // 名字写错 → Exists=false、坐标键被清、seeHead 如实变成 false（不是留着旧的 true）
                sensor.RayHitDistance = 0f;
                modelNode.NodeName = "Nose";
                runtime.Tick(0.05f);
                runtime.Tick(0.05f);
                blackboard.TryGet("node.Exists", out exists);
                blackboard.TryGet("seeHead", out seeHead);
                bool hasX = blackboard.Has("node.X");
                Check("model node: a wrong bone name clears the point and reports Exists=false",
                    !exists && !hasX && !seeHead,
                    "exists=" + exists + " hasX=" + hasX + " seeHead=" + seeHead);
            }

            // ---- Task.FollowEntity：先靠近 → 踩他走过的格子 → 走不通就借 A* 绕 → 保持水平间隔
            //
            // 用户要求："跟随另一个角色，先靠近；跟随分两种：先尽可能走他走过的方块，不行的时候
            // 可以绕着过去（A*）；跟随的时候保持两格的水平间隔。"
            // 用户后来明确："跟随角色走，只需要镜头一直锁在身上就行了" —— 所以这一组断言里有
            // **镜头锁死**（走路方向与视线方向故意错开）与**侧向走位**（按 A/D 平移）两条。
            {
                var sensor = new StubWorldSensor
                {
                    BodyPosition = new Vector3(0f, 64f, 0f),
                    PlayerFound = true,
                    NearestPlayer = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(2f, 64f, 2f), Distance = 2.8f
                    }
                };
                var actuator = new RecordingActuator();
                var blackboard = new AiBlackboard();
                blackboard.Set(new AiBlackboardKey<AiActorView>("target"), sensor.NearestPlayer);

                var follow = new BtFollowEntityTask
                {
                    Id = "follow", TargetKey = "target", KeepDistance = 2f,
                    Mode = "auto", TrailMaxAge = 5f, TrailStuckSeconds = 0.5f
                };
                var root = new BtRootNode { Id = "root", Loop = true };
                root.AddChild(follow);
                var runtime = new BtRuntime(blackboard, sensor, actuator);
                runtime.SetRoot(root, "selftest.follow");
                runtime.Start();

                // 目标走一条"拐弯"的路线：先往 +Z 走 6 格，再往 +X 走 6 格。
                // 于是"第一个还没踩过的脚印"(2.5,2.5) 与"目标的当前位置"(2,4) 方向/位置都不同 ——
                // 断言瞄准的是**脚印**而不是目标，就证明它在走他走过的路；同时断言**视线**在目标身上。
                //
                // 关键：跟随者必须**真的在移动**（桩里按瞄准点每次挪 0.3 m）。桩若站着不动，
                // 卡住判定会（正确地）判定"追不动"→ 丢脚印改走 A*，那就测不到脚印这条腿了。
                // 另外桩的身体朝向每帧对准目标 —— 任务每次 `LookAt(目标)` 带来的副作用就是它。
                int tick = 0;
                Vector3 firstFootstepAim = Vector3.Zero;
                Vector3 firstGaze = Vector3.Zero;
                for (int i = 0; i < 7; i++)
                {
                    AiActorView target = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(2f, 64f, 2f + i), Distance = 8f
                    };
                    sensor.NearestPlayer = target;
                    sensor.Yaw = YawFacing(sensor.BodyPosition, target.Position);
                    blackboard.Set(new AiBlackboardKey<AiActorView>("target"), target);
                    runtime.Tick(0.05f);
                    if (tick++ == 2)
                    {
                        firstFootstepAim = follow.LastAim;
                        firstGaze = actuator.LastLookAtPoint;
                    }
                    StepStubTowardsAim(sensor, follow.LastAim, 0.3f);
                }
                Check("follow: walks the target's footsteps (aims at the oldest un-walked cell, not at him)",
                    actuator.HasLookAtPoint
                    && Math.Abs(firstFootstepAim.X - 2.5f) < 0.6f
                    && Math.Abs(firstFootstepAim.Z - 2.5f) < 0.6f
                    && sensor.PathRequestCount == 0,
                    "aim=(" + firstFootstepAim.X.ToString("0.00") + ","
                    + firstFootstepAim.Z.ToString("0.00")
                    + ") pathRequests=" + sensor.PathRequestCount);

                // 镜头锁死：脚下瞄的是脚印 (2.5,2.5)，视线却落在**目标本体** (2,64+1.35,4) 上
                Check("follow: the camera stays locked on the target while the feet walk his footsteps",
                    Math.Abs(firstGaze.X - 2f) < 0.05f
                    && Math.Abs(firstGaze.Z - 4f) < 0.05f
                    && Math.Abs(firstGaze.Y - 65.35f) < 0.05f,
                    "gaze=(" + firstGaze.X.ToString("0.00") + ","
                    + firstGaze.Y.ToString("0.00") + ","
                    + firstGaze.Z.ToString("0.00") + ")");

                // 他拐弯往 +X 走：脚印被一格格消费掉 → 瞄准点应当顺着足迹往前挪
                float beforeAimZ = firstFootstepAim.Z;
                for (int i = 0; i < 7; i++)
                {
                    AiActorView target = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(2f + i, 64f, 8f), Distance = 8f
                    };
                    sensor.NearestPlayer = target;
                    sensor.Yaw = YawFacing(sensor.BodyPosition, target.Position);
                    blackboard.Set(new AiBlackboardKey<AiActorView>("target"), target);
                    runtime.Tick(0.05f);
                    StepStubTowardsAim(sensor, follow.LastAim, 0.3f);
                }
                Check("follow: consumes footsteps as it reaches them (aim moves along the trail)",
                    follow.LastAim.Z > beforeAimZ + 0.5f, "aimZ="
                    + follow.LastAim.Z.ToString("0.00") + " before="
                    + beforeAimZ.ToString("0.00"));

                // 目标凑到 1.5 格（水平）内 → **站住**：松开全部移动键，只盯着他
                sensor.NearestPlayer = new AiActorView
                {
                    Name = "client", Kind = "player", IsPlayer = true,
                    Position = sensor.BodyPosition + new Vector3(1.5f, 0f, 0f), Distance = 1.5f
                };
                sensor.Yaw = YawFacing(sensor.BodyPosition, sensor.NearestPlayer.Position);
                blackboard.Set(new AiBlackboardKey<AiActorView>("target"), sensor.NearestPlayer);
                runtime.Tick(0.05f);
                Check("follow: keeps the 2 block horizontal gap (stops instead of pushing into him)",
                    !actuator.AnyKeyHeld && runtime.LastResult == BtResult.InProgress,
                    "held=" + JoinKeys(actuator) + " tree=" + runtime.LastResult);

                // 目标瞬移到 40 格外、并把脚印有效期压到 0 → **确定**没有可用脚印，
                // auto 必须回退到 A*（不这么设的话，目标瞬移会在新位置记一条新脚印，
                // 走的还是"脚印"那条腿 —— 那是在测另一件事）
                follow.TrailMaxAge = 0.01f;
                for (int i = 0; i < 6; i++)
                {
                    AiActorView target = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(40f, 64f, 0f), Distance = 40f
                    };
                    sensor.NearestPlayer = target;
                    sensor.Yaw = YawFacing(sensor.BodyPosition, target.Position);
                    blackboard.Set(new AiBlackboardKey<AiActorView>("target"), target);
                    runtime.Tick(0.5f);                          // 大步长把脚印催过期
                    StepStubTowardsAim(sensor, follow.LastAim, 0.3f);
                }
                Check("follow: falls back to A* once the trail is stale (auto mode)",
                    sensor.PathRequestCount > 0, "requests=" + sensor.PathRequestCount);

                // 卡住（追着走但位置不动）→ 丢脚印、继续用 A* 重算
                // 注意要**两次测量**才判得出卡住：第一次只是把基线挪到当前位置（0.5s），
                // 第二次（再 0.5s）才量到"没动过"。所以这里要跑够 1.5s。
                int beforeStuck = sensor.PathRequestCount;
                for (int i = 0; i < 30; i++)
                    runtime.Tick(0.05f);                          // 不调 StepStubTowardsAim = 卡住
                Check("follow: being stuck keeps using A* (trail dropped, position frozen)",
                    sensor.PathRequestCount > beforeStuck,
                    "before=" + beforeStuck + " after=" + sensor.PathRequestCount);

                // 目标从黑板消失 → Failed（树去决定怎么办），并且松开全部移动键
                blackboard.Remove(new AiBlackboardKey<AiActorView>("target"));
                runtime.Tick(0.05f);
                Check("follow: a vanished target fails and releases every movement key",
                    runtime.LastResult == BtResult.Failed && !actuator.AnyKeyHeld,
                    "tree=" + runtime.LastResult + " held=" + JoinKeys(actuator));

                // `mode=path`：用户明确只要"绕着过去"（纯 A*），就不该去踩脚印；
                // 视线照样锁在目标身上（航点在 (8,4)，视线在 (30,10)）
                var sensor3 = new StubWorldSensor
                {
                    BodyPosition = new Vector3(0f, 64f, 0f),
                    PlayerFound = true,
                    NearestPlayer = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(30f, 64f, 10f), Distance = 31f
                    },
                    Path = new[] { new Vector3(8f, 64f, 4f), new Vector3(20f, 64f, 8f) }
                };
                var actuator3 = new RecordingActuator();
                var blackboard3 = new AiBlackboard();
                blackboard3.Set(new AiBlackboardKey<AiActorView>("target"), sensor3.NearestPlayer);
                var follow3 = new BtFollowEntityTask
                {
                    Id = "follow3", TargetKey = "target", KeepDistance = 2f, Mode = "path"
                };
                var root3 = new BtRootNode { Id = "root3", Loop = true };
                root3.AddChild(follow3);
                var runtime3 = new BtRuntime(blackboard3, sensor3, actuator3);
                runtime3.SetRoot(root3, "selftest.follow.pathonly");
                runtime3.Start();
                runtime3.Tick(0.05f);
                runtime3.Tick(0.05f);
                Check("follow: mode=path walks the game's A* waypoints while the camera stays on him",
                    sensor3.PathRequestCount == 1 && actuator3.AnyKeyHeld
                    && Math.Abs(follow3.LastAim.X - 8f) < 0.6f
                    && Math.Abs(follow3.LastAim.Z - 4f) < 0.6f
                    && Math.Abs(actuator3.LastLookAtPoint.X - 30f) < 0.05f
                    && Math.Abs(actuator3.LastLookAtPoint.Z - 10f) < 0.05f,
                    "requests=" + sensor3.PathRequestCount + " aim=("
                    + follow3.LastAim.X.ToString("0.00") + ","
                    + follow3.LastAim.Z.ToString("0.00") + ") gaze=("
                    + actuator3.LastLookAtPoint.X.ToString("0.00") + ","
                    + actuator3.LastLookAtPoint.Z.ToString("0.00") + ")");

                // 寻路**一直给不出航点**（空路径/队列满/不可达）时，绝不能每帧重算：
                // 游戏那条队列只有 10 个名额、后台每 250ms 处理一条，每帧重算会把别的寻路挤掉。
                var sensor4 = new StubWorldSensor
                {
                    BodyPosition = new Vector3(0f, 64f, 0f),
                    PlayerFound = true,
                    PathReady = false,
                    NearestPlayer = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(30f, 64f, 0f), Distance = 30f
                    }
                };
                var actuator4 = new RecordingActuator();
                var blackboard4 = new AiBlackboard();
                blackboard4.Set(new AiBlackboardKey<AiActorView>("target"), sensor4.NearestPlayer);
                var follow4 = new BtFollowEntityTask
                { Id = "follow4", TargetKey = "target", Mode = "path", KeepDistance = 2f };
                var root4 = new BtRootNode { Id = "root4", Loop = true };
                root4.AddChild(follow4);
                var runtime4 = new BtRuntime(blackboard4, sensor4, actuator4);
                runtime4.SetRoot(root4, "selftest.follow.nopath");
                runtime4.Start();
                for (int i = 0; i < 40; i++)                     // 2 秒 = 40 帧
                    runtime4.Tick(0.05f);
                Check("follow: an empty path does not turn into a per-frame request storm",
                    sensor4.PathRequestCount <= 10 && actuator4.AnyKeyHeld,
                    "requests in 2s=" + sensor4.PathRequestCount + " held=" + JoinKeys(actuator4));

                // ---- 镜头不许"反复切"：站↔走加迟滞 + 站/走都锁定他（用户实测报的问题）
                {
                    var sensor5 = new StubWorldSensor
                    {
                        BodyPosition = new Vector3(0f, 64f, 0f),
                        PlayerFound = true,
                        NearestPlayer = new AiActorView
                        {
                            Name = "client", Kind = "player", IsPlayer = true,
                            Position = new Vector3(6f, 64f, 0f), Distance = 6f
                        }
                    };
                    var actuator5 = new RecordingActuator();
                    var blackboard5 = new AiBlackboard();
                    blackboard5.Set(new AiBlackboardKey<AiActorView>("target"), sensor5.NearestPlayer);
                    var follow5 = new BtFollowEntityTask
                    {
                        Id = "follow5", TargetKey = "target", KeepDistance = 2f,
                        StandHysteresis = 0.75f, Mode = "trail"
                    };
                    var root5 = new BtRootNode { Id = "root5", Loop = true };
                    root5.AddChild(follow5);
                    var runtime5 = new BtRuntime(blackboard5, sensor5, actuator5);
                    runtime5.SetRoot(root5, "selftest.follow.gaze");
                    runtime5.Start();
                    runtime5.Tick(0.05f);

                    // 目标在 (6,64,0)，它所在的格心是 (6.5,0.5)：脚印 = 格心。
                    // 期望：**视线落在目标本体 z=0**，而**脚下朝格心 z=0.5 走** —— 两者差 0.5 正好证明解耦。
                    Check("follow: the gaze stays on the target while walking toward his footsteps",
                        Math.Abs(actuator5.LastLookAtPoint.X - 6f) < 0.4f
                        && Math.Abs(actuator5.LastLookAtPoint.Z) < 0.3f
                        && Math.Abs(follow5.LastAim.X - 6.5f) < 0.4f
                        && Math.Abs(follow5.LastAim.Z - 0.5f) < 0.3f,
                        "gaze=(" + actuator5.LastLookAtPoint.X.ToString("0.00") + ","
                        + actuator5.LastLookAtPoint.Z.ToString("0.00") + ") walkAim=("
                        + follow5.LastAim.X.ToString("0.00") + ","
                        + follow5.LastAim.Z.ToString("0.00") + ")");

                    // 距离在 keepDistance 上下抖动 8 次：迟滞必须让"在走/站住"**翻转不超过两次**
                    int flips = 0;
                    bool previous = actuator5.AnyKeyHeld;
                    for (int i = 0; i < 16; i++)
                    {
                        float gap = (i % 2 == 0) ? 1.9f : 2.4f;   // 抖动：1.9 ↔ 2.4
                        AiActorView target = new AiActorView
                        {
                            Name = "client", Kind = "player", IsPlayer = true,
                            Position = new Vector3(gap, 64f, 0f), Distance = gap
                        };
                        sensor5.NearestPlayer = target;
                        sensor5.Yaw = YawFacing(sensor5.BodyPosition, target.Position);
                        blackboard5.Set(new AiBlackboardKey<AiActorView>("target"), target);
                        runtime5.Tick(0.05f);
                        if (actuator5.AnyKeyHeld != previous)
                        {
                            flips++;
                            previous = actuator5.AnyKeyHeld;
                        }
                    }
                    Check("follow: walking/standing does not flip-flop while the gap hovers at keepDistance",
                        flips <= 2, "flips=" + flips + " held=" + JoinKeys(actuator5));

                    // 站住时（上面最后一次是 2.4 格 → 迟滞范围内仍然站着）镜头也必须锁在他身上
                    Check("follow: the camera is locked on him even while standing",
                        Math.Abs(actuator5.LastLookAtPoint.X - 2.4f) < 0.05f
                        && Math.Abs(actuator5.LastLookAtPoint.Z) < 0.05f,
                        "gaze=(" + actuator5.LastLookAtPoint.X.ToString("0.00") + ","
                        + actuator5.LastLookAtPoint.Z.ToString("0.00") + ")");
                }

                // ---- 镜头锁死也不耽误走位：脚印在**侧面**（≈90°）时脚下的键自动变成平移键
                //
                // 这正是"镜头一直锁着他、人却朝他走过的路走"能成立的原因：身体基上的 w/a/s/d
                // 投影决定走路方向（Source: ComponentLocomotion.cs:452），相机不参与。
                {
                    var sensor6 = new StubWorldSensor
                    {
                        BodyPosition = new Vector3(0f, 64f, 0f),
                        PlayerFound = true,
                        NearestPlayer = new AiActorView
                        {
                            Name = "client", Kind = "player", IsPlayer = true,
                            Position = new Vector3(0.5f, 64f, 6f), Distance = 6f
                        }
                    };
                    var actuator6 = new RecordingActuator();
                    var blackboard6 = new AiBlackboard();
                    blackboard6.Set(new AiBlackboardKey<AiActorView>("target"), sensor6.NearestPlayer);
                    var follow6 = new BtFollowEntityTask
                    {
                        Id = "follow6", TargetKey = "target", KeepDistance = 2f, Mode = "trail"
                    };
                    var root6 = new BtRootNode { Id = "root6", Loop = true };
                    root6.AddChild(follow6);
                    var runtime6 = new BtRuntime(blackboard6, sensor6, actuator6);
                    runtime6.SetRoot(root6, "selftest.follow.gaze.turn");
                    runtime6.Start();

                    // 目标先往 +Z 走：留下来的脚印格心是 (0.5,6.5)
                    sensor6.Yaw = YawFacing(sensor6.BodyPosition, sensor6.NearestPlayer.Position);
                    runtime6.Tick(0.05f);

                    // 目标瞬移到 +X 方向 → 身体跟着转过去（相机锁着他），脚印却还在 +Z 侧后方
                    AiActorView target = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(6f, 64f, 0f), Distance = 6f
                    };
                    sensor6.NearestPlayer = target;
                    sensor6.Yaw = YawFacing(sensor6.BodyPosition, target.Position);
                    blackboard6.Set(new AiBlackboardKey<AiActorView>("target"), target);
                    runtime6.Tick(0.05f);
                    Check("follow: with the camera locked on him, the feet still walk the side footstep (strafe)",
                        Math.Abs(actuator6.LastLookAtPoint.X - 6f) < 0.1f
                        && Math.Abs(actuator6.LastLookAtPoint.Z) < 0.1f
                        && Math.Abs(follow6.LastAim.Z - 6.5f) < 0.4f
                        && (actuator6.IsHolding("a") || actuator6.IsHolding("d"))
                        && !actuator6.IsHolding("w") && !actuator6.IsHolding("s"),
                        "gaze=(" + actuator6.LastLookAtPoint.X.ToString("0.00") + ","
                        + actuator6.LastLookAtPoint.Z.ToString("0.00") + ") walkAim=("
                        + follow6.LastAim.X.ToString("0.00") + ","
                        + follow6.LastAim.Z.ToString("0.00") + ") held=" + JoinKeys(actuator6));
                }

                // ---- 黑板里躺的是**旧快照**也必须锁得住（用户实测："一段一段地在跳"）
                //
                // 真机根因：黑板存的是 `AiActorView` 值类型快照，由 `Service.UpdateNearestPlayer`
                // 按 `interval` 写入 —— 出厂跟随示例原来写的是 0.2s，于是镜头每 0.2 秒才收到一个新位置，
                // 追起来就是"一段一段地跳"。修法两条：① 示例服务改成 `interval=0`；
                // ② 任务自己每 tick 按名字重取一次真位置（这条断言钉的就是 ②）。
                {
                    var sensor7 = new StubWorldSensor
                    {
                        BodyPosition = new Vector3(0f, 64f, 0f),
                        PlayerFound = true,
                        NearestPlayer = new AiActorView
                        {
                            Name = "client", Kind = "player", IsPlayer = true,
                            Position = new Vector3(6f, 64f, 0f), Distance = 6f
                        }
                    };
                    var actuator7 = new RecordingActuator();
                    var blackboard7 = new AiBlackboard();
                    // 只写**一次**快照，之后再也不写 = 树上服务 0.2s 才刷新一次的情形
                    blackboard7.Set(new AiBlackboardKey<AiActorView>("target"), sensor7.NearestPlayer);
                    var follow7 = new BtFollowEntityTask
                    { Id = "follow7", TargetKey = "target", Mode = "trail", KeepDistance = 2f };
                    var root7 = new BtRootNode { Id = "root7", Loop = true };
                    root7.AddChild(follow7);
                    var runtime7 = new BtRuntime(blackboard7, sensor7, actuator7);
                    runtime7.SetRoot(root7, "selftest.follow.live");
                    runtime7.Start();
                    runtime7.Tick(0.05f);

                    // 他瞬移 4 米：黑板里的快照还停在 6.00，传感器里的**真位置**已经是 10.00
                    sensor7.NearestPlayer = new AiActorView
                    {
                        Name = "client", Kind = "player", IsPlayer = true,
                        Position = new Vector3(10f, 64f, 0f), Distance = 10f
                    };
                    sensor7.Yaw = YawFacing(sensor7.BodyPosition, sensor7.NearestPlayer.Position);
                    runtime7.Tick(0.05f);
                    Check("follow: re-reads the target's live position (a stale blackboard snapshot cannot step the camera)",
                        Math.Abs(actuator7.LastLookAtPoint.X - 10f) < 0.05f
                        && Math.Abs(actuator7.LastLookAtPoint.Z) < 0.05f,
                        "gaze=(" + actuator7.LastLookAtPoint.X.ToString("0.00") + ","
                        + actuator7.LastLookAtPoint.Z.ToString("0.00")
                        + ") 黑板快照还在 6.00");
                }

                // ---- 键位映射本身的正确性：8 个方向各来一次，把"按住的键"反解成世界方向，
                //      必须与"想走的方向"一致（8 方向量化固有误差 ≤ 22.5°）
                {
                    int bad = 0;
                    string worst = "";
                    float worstDeg = 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        float angle = k * (float)Math.PI / 4f;
                        Vector3 self = new Vector3(0f, 64f, 0f);
                        Vector3 aim = new Vector3(
                            (float)Math.Round(6f * Math.Cos(angle)) + 0.5f, 64f,
                            (float)Math.Round(6f * Math.Sin(angle)) + 0.5f);
                        var sensorK = new StubWorldSensor
                        {
                            BodyPosition = self,
                            PlayerFound = true,
                            Path = new[] { aim },
                            NearestPlayer = new AiActorView
                            {
                                Name = "client", Kind = "player", IsPlayer = true,
                                Position = new Vector3(6f, 64f, 0f), Distance = 6f
                            }
                        };
                        // 镜头锁在 +X 的目标上 → 身体朝 +X；想走的方向却可能是任意一个
                        sensorK.Yaw = YawFacing(self, sensorK.NearestPlayer.Position);
                        var actuatorK = new RecordingActuator();
                        var blackboardK = new AiBlackboard();
                        blackboardK.Set(new AiBlackboardKey<AiActorView>("target"), sensorK.NearestPlayer);
                        var followK = new BtFollowEntityTask
                        { Id = "followK", TargetKey = "target", Mode = "path", KeepDistance = 2f };
                        var rootK = new BtRootNode { Id = "rootK", Loop = true };
                        rootK.AddChild(followK);
                        var runtimeK = new BtRuntime(blackboardK, sensorK, actuatorK);
                        runtimeK.SetRoot(rootK, "selftest.follow.keymap");
                        runtimeK.Start();
                        runtimeK.Tick(0.05f);
                        runtimeK.Tick(0.05f);

                        float errorDegrees;
                        Vector3 walked = KeyWorldDirection(sensorK.Yaw, actuatorK);
                        if (!ReconstructDirection(aim - self, walked, out errorDegrees))
                            errorDegrees = 180f;
                        if (errorDegrees > worstDeg)
                        {
                            worstDeg = errorDegrees;
                            worst = "aim=(" + aim.X.ToString("0.0") + "," + aim.Z.ToString("0.0")
                                + ") held=" + JoinKeys(actuatorK);
                        }
                        if (errorDegrees > 23.5f)
                            bad++;
                    }
                    Check("follow: w/a/s/d pressed in the body frame reproduce the wanted walk direction",
                        bad == 0, "wrong=" + bad + "/8 worst=" + worstDeg.ToString("0.0")
                        + "deg " + worst);
                }
            }
        }

        /// <summary>
        /// 让"跟随者"桩真的朝当前瞄准点挪一段 —— 不这么做的话，卡住判定会（正确地）判定
        /// "追不动"→ 丢脚印改走 A*，于是永远测不到"踩脚印"那条腿。真人也是这么走的。
        ///
        /// 注意朝向**目标**（任务的 `LookAt`）与走**瞄准点**（脚印/航点）是两件事：
        /// 所以移动由这里显式喂给桩，而不是靠"视线方向 = 走路方向"这个（已经废掉的）假设。
        /// </summary>
        private static void StepStubTowardsAim(StubWorldSensor sensor, Vector3 aim, float step)
        {
            Vector3 position = sensor.BodyPosition;
            Vector3 direction = new Vector3(aim.X - position.X, 0f, aim.Z - position.Z);
            if (direction.LengthSquared() < 1e-4f)
                return;
            sensor.BodyPosition = position + Vector3.Normalize(direction) * step;
        }

        /// <summary>
        /// 身体朝向某个方向的 yaw —— 与游戏一致：`Matrix.CreateFromAxisAngle(UnitY, yaw).Forward`
        /// 就指向该方向（Source: Survivalcraft/Game/ComponentLocomotion.cs:309 身体旋转唯一的写入点；
        /// 本项目注视链路实测标定 yaw = atan2(-dx, -dz)）。
        /// </summary>
        private static float YawFacing(Vector3 from, Vector3 to)
        {
            float dx = to.X - from.X;
            float dz = to.Z - from.Z;
            if (dx * dx + dz * dz < 1e-6f)
                return 0f;
            return MathUtils.Atan2(0f - dx, 0f - dz);
        }

        private static Vector3 Flatten(Vector3 value)
        {
            return new Vector3(value.X, 0f, value.Z);
        }

        /// <summary>把当前按住的移动键列成 `w+d` 这种字符串（断言失败时能一眼看出按了什么）。</summary>
        private static string JoinKeys(RecordingActuator actuator)
        {
            var keys = new System.Collections.Generic.List<string>(actuator.HeldKeys);
            keys.Sort(StringComparer.Ordinal);
            return keys.Count == 0 ? "-" : string.Join("+", keys);
        }

        /// <summary>
        /// 把"按住的键"还原成世界方向 —— 与任务里的投影互为逆运算（用来钉住键位映射没接反）。
        /// Source: Survivalcraft/Game/ComponentLocomotion.cs:452（走路 = 右 * WalkOrder.X + 前 * WalkOrder.Y）。
        /// </summary>
        private static Vector3 KeyWorldDirection(float yawRadians, RecordingActuator actuator)
        {
            Matrix basis = Matrix.CreateFromAxisAngle(Vector3.UnitY, yawRadians);
            Vector3 forward = Flatten(basis.Forward);
            Vector3 right = Flatten(basis.Right);
            float f = (actuator.IsHolding("w") ? 1f : 0f) - (actuator.IsHolding("s") ? 1f : 0f);
            float r = (actuator.IsHolding("d") ? 1f : 0f) - (actuator.IsHolding("a") ? 1f : 0f);
            return forward * f + right * r;
        }

        private static bool ReconstructDirection(Vector3 intended, Vector3 walked, out float errorDegrees)
        {
            errorDegrees = 180f;
            Vector3 a = Flatten(intended);
            Vector3 b = walked;
            if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f)
                return false;
            a = Vector3.Normalize(a);
            b = Vector3.Normalize(b);
            float dot = Vector3.Dot(a, b);
            dot = dot < -1f ? -1f : (dot > 1f ? 1f : dot);
            errorDegrees = (float)Math.Acos(dot) * 180f / MathUtils.PI;
            return true;
        }

        /// <summary>什么都不会的传感器/执行器：`--selftest` 里没有游戏，桩必须能跑。</summary>
        private sealed class NullSensor : IAiSensor
        {
            public bool IsReady { get { return false; } }
            public bool IsInputAccepted { get { return false; } }
            public string PlayerName { get { return "selftest"; } }
            public Vector3 Position { get { return Vector3.Zero; } }
            public Vector3 EyePosition { get { return Vector3.Zero; } }
            public Vector3 Velocity { get { return Vector3.Zero; } }
            public float YawRadians { get { return 0f; } }
            public bool TryGetPitch(out float pitchRadians) { pitchRadians = 0f; return false; }
            public float Health { get { return 1f; } }
            public bool TryFindPlayer(string name, out AiActorView view)
            { view = default(AiActorView); return false; }
            public bool TryFindNearestPlayer(out AiActorView view)
            { view = default(AiActorView); return false; }
        }

        /// <summary>带扩展观察能力的桩：用来证明"服务确实会往黑板写"、以及寻路/交互任务的行为。</summary>
        private sealed class StubWorldSensor : IAiSensor, IAiWorldSensor
        {
            /// <summary>可改的朝向（`Task.FaceEntity` 的测试要摆两个位置）。</summary>
            public float Yaw = 0.5f;

            /// <summary>可改的位置（模拟"走过去了"）。</summary>
            public Vector3 BodyPosition = new Vector3(10f, 64f, 20f);

            /// <summary>`TryPeekBlock` 返回的方块 id（0 = 空气，用来模拟"挖掉了"）。</summary>
            public int BlockContents = 1;

            /// <summary>预置路径（**从近到远**，与 `CopyPath` 的输出顺序一致）。</summary>
            public Vector3[] Path;

            /// <summary>true = 请求后立刻可用；false = 一直"搜索中"（测等待分支）。</summary>
            public bool PathReady = true;

            /// <summary>true = 队列满（游戏会给"空且已完成"的结果 → 任务应判 Failed 并直线走）。</summary>
            public bool PathQueueFull;

            public int PathRequestCount { get; private set; }

            /// <summary>最近一次寻路请求的目标点：坐标模式（source=cell）要断言"确实走到了那格"。</summary>
            public Vector3 LastPathDestination { get; private set; }
            public int PathClearCount { get; private set; }

            private AiPathStatus m_status = AiPathStatus.Idle;

            public bool IsReady { get { return true; } }
            public bool IsInputAccepted { get { return InputAccepted; } }

            /// <summary>可改的"窗口是否接受输入"（失焦时游戏会丢掉全部注入输入）。</summary>
            public bool InputAccepted = true;
            public string PlayerName { get { return "selftest"; } }
            public Vector3 Position { get { return BodyPosition; } }
            public Vector3 EyePosition { get { return BodyPosition + new Vector3(0f, 1.6f, 0f); } }
            public Vector3 Velocity { get { return Vector3.Zero; } }
            public float YawRadians { get { return Yaw; } }
            public bool TryGetPitch(out float pitchRadians) { pitchRadians = 0.1f; return true; }
            public float Health { get { return 0.9f; } }
            public bool TryFindPlayer(string name, out AiActorView view)
            {
                // 与真传感器同语义：**按名字**找（真实现遍历 SubsystemPlayers，见 PlayerSensor.cs:150-184）
                view = NearestPlayer;
                return PlayerFound && !string.IsNullOrEmpty(name)
                    && (string.IsNullOrEmpty(NearestPlayer.Name)
                        || string.Equals(NearestPlayer.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            public bool TryFindNearestPlayer(out AiActorView view)
            {
                view = NearestPlayer;
                return PlayerFound;
            }

            /// <summary>可改的"最近的其它玩家"（联机场景里就是那个会动的网络玩家）。</summary>
            public AiActorView NearestPlayer;
            public bool PlayerFound;

            /// <summary>可改的"到目标的视线是否通畅"（模拟被墙挡住）。</summary>
            public bool LineOfSight;
            public int LineOfSightQueries;

            /// <summary>
            /// 只有**落在 [LowestVisibleY, HighestVisibleY] 之间**的点才看得见 —— 用来模拟两种真实遮挡：
            ///   · "只露出头"：低位被 1 格高的墙挡住 → 只给下限（头通、胸口不通）；
            ///   · "头顶有屋檐"：高位被挡住、胸口反而看得见 → 只给上限（这一条才验证"胸口那条备用射线"）。
            /// </summary>
            public float LowestVisibleY = float.MinValue;
            public float HighestVisibleY = float.MaxValue;

            /// <summary>每条被问过的视线目标点（断言"到底瞄的是头还是脚"）。</summary>
            public readonly List<Vector3> LineOfSightPoints = new List<Vector3>();

            public bool TryGetSelfState(out AiSelfState state)
            {
                state = new AiSelfState
                {
                    Health = 0.9f, Food = 0.42f, Stamina = 0.8f, Sleep = 0.1f,
                    Temperature = 12f, Wetness = 0f, Air = 1f, AirCapacity = 1f,
                    Position = Position, Velocity = Vector3.Zero, YawRadians = 0.5f,
                    IsOnGround = true, StandingOnValue = 0
                };
                return true;
            }

            public bool TryFindNearestCreature(int categoryMask, float maxDistance, out AiActorView view)
            { view = default(AiActorView); return false; }
            public bool TryFindNearestPickable(float maxDistance, out AiActorView view)
            { view = default(AiActorView); return false; }
            public bool TryRaycastBlock(Vector3 direction, float maxDistance, out AiBlockHit hit)
            {
                return TryRaycastBlockFrom(EyePosition, direction, maxDistance, out hit);
            }

            /// <summary>
            /// 通用射线桩：`RayHitDistance &gt; 0` 时"离起点这么远有一堵墙"，否则一路通畅。
            /// `RayQueries`/`RayDirections` 记下每条被问过的射线（断言"从哪打到哪"）。
            /// </summary>
            public bool TryRaycastBlockFrom(Vector3 origin, Vector3 direction, float maxDistance,
                out AiBlockHit hit)
            {
                hit = default(AiBlockHit);
                RayQueries.Add(origin);
                RayDirections.Add(direction);
                if (RayHitDistance <= 0f || RayHitDistance > maxDistance)
                    return false;

                Vector3 point = origin + Vector3.Normalize(direction) * RayHitDistance;
                hit = new AiBlockHit
                {
                    Contents = 1, Value = 1, Name = "Stone", Distance = RayHitDistance,
                    X = (int)Math.Floor(point.X), Y = (int)Math.Floor(point.Y),
                    Z = (int)Math.Floor(point.Z), HitPoint = point
                };
                return true;
            }

            /// <summary>假装的墙离起点多远（米）；&lt;= 0 = 通畅。</summary>
            public float RayHitDistance;
            public readonly List<Vector3> RayQueries = new List<Vector3>();
            public readonly List<Vector3> RayDirections = new List<Vector3>();

            // ---- 模型节点桩：骨骼名 → 相对脚底的静止姿态偏移
            public readonly Dictionary<string, Vector3> Bones = new Dictionary<string, Vector3>();

            public bool TryGetBoneWorldPosition(AiActorView actor, string boneName, out Vector3 world)
            {
                world = Vector3.Zero;
                Vector3 offset;
                if (string.IsNullOrEmpty(boneName) || !Bones.TryGetValue(boneName, out offset))
                    return false;
                world = actor.Position + offset;
                return true;
            }

            public bool TryListBones(AiActorView actor, out List<string> names,
                out List<Vector3> worldPositions)
            {
                names = new List<string>();
                worldPositions = new List<Vector3>();
                foreach (KeyValuePair<string, Vector3> pair in Bones)
                {
                    names.Add(pair.Key);
                    worldPositions.Add(actor.Position + pair.Value);
                }
                return names.Count > 0;
            }

            public bool TryPeekBlock(int x, int y, int z, out int contents, out string name)
            {
                contents = BlockContents;
                name = contents == 0 ? null : "Stone";
                return true;
            }

            public bool HasLineOfSight(Vector3 target, out float distance)
            {
                LineOfSightQueries++;
                LineOfSightPoints.Add(target);
                distance = Vector3.Distance(EyePosition, target);
                // 高度门槛优先：给了门槛就按门槛判（模拟"墙以上才看得见"），
                // 没给（MinValue）就用开关 LineOfSight。
                if (LowestVisibleY > float.MinValue || HighestVisibleY < float.MaxValue)
                    return target.Y >= LowestVisibleY && target.Y <= HighestVisibleY;
                return LineOfSight;
            }

            // ---- 寻路桩：不碰真游戏，按"预置路径"回答
            public bool TryRequestPath(Vector3 destination, float arriveRadius, int maxPositionsToCheck,
                out string error)
            {
                error = null;
                PathRequestCount++;
                LastPathDestination = destination;
                m_status = PathQueueFull
                    ? AiPathStatus.Failed                       // 队列满：游戏给"空且已完成"
                    : (PathReady ? AiPathStatus.Ready : AiPathStatus.Searching);
                return true;
            }

            public AiPathStatus GetPathStatus()
            {
                if (m_status == AiPathStatus.Searching && PathReady)
                    m_status = AiPathStatus.Ready;
                return m_status;
            }

            public int CopyPath(Vector3[] buffer)
            {
                if (buffer == null || Path == null)
                    return 0;
                int count = Math.Min(buffer.Length, Path.Length);
                Array.Copy(Path, buffer, count);
                return count;
            }

            public void ClearPath()
            {
                PathClearCount++;
                m_status = AiPathStatus.Idle;
            }
        }

        private sealed class NullActuator : IAiActuator
        {
            public bool IsReady { get { return false; } }
            public void Look(float yawRadians, float pitchRadians) { }
            public void LookAt(Vector3 worldPoint) { }
            public void LookDelta(float yawRadians, float pitchRadians) { }
            public void HoldKey(string key, bool down) { }
            public void PulseKey(string key, int holdMilliseconds) { }
            public void MouseButton(string button, bool down) { }
            public void MouseClick(string button, int holdMilliseconds) { }
            public void Wheel(int delta) { }
            public bool UiClick(string selectorOrPoint) { return false; }
            public bool UiClick(string target, string mode) { return false; }
            public void ReleaseAll() { }
        }

        /// <summary>会记账的执行器桩：用来断言"这个任务真的去转了视角 / 真的按了键"。</summary>
        private sealed class RecordingActuator : IAiActuator
        {
            public int LookCount { get; private set; }
            public int LookAtCount { get; private set; }
            public int MouseButtonCount { get; private set; }
            public int MouseClickCount { get; private set; }
            public int PulseKeyCount { get; private set; }
            public int WheelCount { get; private set; }

            /// <summary>最后被按下的键与它是否按下（用来断言"一直在按 W"）。</summary>
            public string LastHeldKey { get; private set; }
            public bool LastHeldDown { get; private set; }

            /// <summary>
            /// **当前真正被按住**的键集合（按变化维护）。
            ///
            /// 为什么不能只看 `LastHeldKey`：`Task.FollowEntity` 现在按住的是 W/A/S/D 的**组合**
            /// （走路方向投影到身体基上，镜头不参与），最后一次调用是哪根键跟语义无关 ——
            /// 断言必须看"集合里有谁"。
            /// </summary>
            public readonly System.Collections.Generic.HashSet<string> HeldKeys =
                new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            /// <summary>有没有任何移动键被按住（跟随的"在走 / 站住"就断言这个）。</summary>
            public bool AnyKeyHeld { get { return HeldKeys.Count > 0; } }

            public bool IsHolding(string key)
            {
                return !string.IsNullOrEmpty(key) && HeldKeys.Contains(key);
            }

            /// <summary>最后点过的鼠标键名。</summary>
            public string LastMouseButton { get; private set; }

            /// <summary>最后一次鼠标键是按下还是松开（`Task.UseItem` 要分别断言"按住"与"松开"两段）。</summary>
            public bool LastMouseDown { get; private set; }

            /// <summary>最后脉冲过的键盘按键。</summary>
            public string LastPulseKey { get; private set; }

            /// <summary>最后一次 `LookAt` 的瞄准点（"眼睛有没有对准他"要断言这个点）。</summary>
            public Vector3 LastLookAtPoint { get; private set; }
            public bool HasLookAtPoint { get; private set; }

            public bool IsReady { get { return true; } }
            public void Look(float yawRadians, float pitchRadians) { LookCount++; }
            public void LookAt(Vector3 worldPoint)
            {
                LookAtCount++;
                LastLookAtPoint = worldPoint;
                HasLookAtPoint = true;
            }
            public void LookDelta(float yawRadians, float pitchRadians) { }
            public void HoldKey(string key, bool down)
            {
                LastHeldKey = key;
                LastHeldDown = down;
                if (string.IsNullOrEmpty(key))
                    return;
                if (down)
                    HeldKeys.Add(key);
                else
                    HeldKeys.Remove(key);
            }
            public void PulseKey(string key, int holdMilliseconds)
            {
                PulseKeyCount++;
                LastPulseKey = key;
            }
            public void MouseButton(string button, bool down)
            {
                MouseButtonCount++;
                LastMouseButton = button;
                LastMouseDown = down;
            }
            public void MouseClick(string button, int holdMilliseconds)
            {
                MouseClickCount++;
                LastMouseButton = button;
            }
            public void Wheel(int delta) { WheelCount++; }
            public bool UiClick(string selectorOrPoint) { return false; }
            public bool UiClick(string target, string mode) { return false; }
            public void ReleaseAll() { HeldKeys.Clear(); }
        }

        /// <summary>装饰器的属性规格（`schema.decorators` 里按类型找）。</summary>
        private static PackageValue FindDecorator(PackageValue schema, string type)
        {
            if (schema == null || !schema.Get("decorators").IsArray)
                return null;
            for (int i = 0; i < schema.Get("decorators").Count; i++)
            {
                PackageValue entry = schema.Get("decorators").Item(i);
                if (string.Equals(entry.Get("type").AsString(null), type, StringComparison.Ordinal))
                    return entry;
            }
            return null;
        }

        private static void Packages(HttpClient client, string instanceRoot, string instanceDirectory)
        {
            string listJson = Get(client, "/api/packages");
            PackageValue list = Parse(listJson);
            Check("package list finds the factory trees",
                list != null && list.Get("packages").Count >= 3,
                "count=" + (list != null ? list.Get("packages").Count : -1));

            string demoPath = Path.Combine(instanceDirectory, PackageTemplates.DemoFile);
            Check("demo package exists on disk", File.Exists(demoPath), demoPath);

            // ---- 出厂示例 su.watch：**盯住最近的玩家**（反应式树的标准写法）
            // 只断言"能存能读"不够：这棵树的价值全在结构上（服务每帧刷 + 装饰器当门 + 空转分支），
            // 少任何一个，语义就变成"盯死一个旧坐标"或"看不见也一直写视角"。
            string watchPath = Path.Combine(instanceDirectory, PackageTemplates.WatchFile);
            Check("the watch demo (su.watch.scbtpak) is installed as factory content",
                File.Exists(watchPath), watchPath);
            PackageValue watch = Parse(Get(client, "/api/package?path="
                + Uri.EscapeDataString(watchPath)));
            string watchTree = watch != null && watch.Get("tree").IsObject
                ? watch.Get("tree").ToJson(false) : "";
            Check("the watch demo wires both sensors onto the looping sequence",
                watchTree.Contains("UpdateNearestPlayer") && watchTree.Contains("UpdateLineOfSight")
                && watchTree.Contains("\"interval\":0"),
                Short(watchTree));
            Check("the watch demo gates the look-at branch on canSee == true",
                watchTree.Contains("Blackboard") && watchTree.Contains("canSee")
                && watchTree.Contains("\"value\":true"),
                Short(watchTree));
            Check("the watch demo aims the visibility ray at the head (only-head = visible)",
                watchTree.Contains("targetEyeHeight") && watchTree.Contains("alsoCheckBody"),
                Short(watchTree));
            Check("the watch demo tracks the head bone and probes from the eye to it",
                watchTree.Contains("UpdateModelNode") && watchTree.Contains("Probe")
                && watchTree.Contains("\"mode\":\"clear\"") && watchTree.Contains("LookAtPoint")
                && watchTree.Contains("\"nodeName\":\"Head\""),
                Short(watchTree));
            Check("the watch demo keeps a fallback branch (hidden = do nothing)",
                watchTree.Contains("Task.LookAt") && watchTree.Contains("Task.Wait"),
                Short(watchTree));
            PackageValue watchOk = Parse(PostJson(client, "/api/validate",
                "{\"manifest\":" + watch.Get("manifest").ToJson(false)
                + ",\"tree\":" + watchTree + "}"));
            Check("the watch demo validates with no errors and no warnings",
                watchOk != null && watchOk.Get("ok").AsBool(false)
                && watchOk.Get("warnings").AsInt(-1) == 0,
                watchOk != null ? watchOk.Get("issues").Preview(200) : "<null>");

            PackageValue read = Parse(Get(client, "/api/package?path="
                + Uri.EscapeDataString(demoPath)));
            Check("reading a package returns manifest + tree",
                read != null && read.Get("ok").AsBool() && read.Get("manifest").IsObject
                && read.Get("tree").IsObject,
                read != null ? read.Get("issues").Preview(120) : "<null>");
            Check("the package in the (single) package folder is writable",
                read != null && read.Get("writable").AsBool(false),
                read != null ? "writable=" + read.Get("writable").AsBool(false)
                    + " root=" + read.Get("root").AsString(null) : "<null>");

            // ---- 校验：原样通过
            string manifestJson = read.Get("manifest").ToJson(false);
            string treeJson = read.Get("tree").ToJson(false);
            PackageValue valid = Parse(PostJson(client, "/api/validate",
                "{\"manifest\":" + manifestJson + ",\"tree\":" + treeJson + "}"));
            Check("validate accepts the unmodified tree",
                valid != null && valid.Get("ok").AsBool(false), valid != null ? valid.Preview(120) : "<null>");

            // ---- 补上的两个装饰器（用户点名"装饰器是否有缺失"）：能校验通过 + 属性真的落地 + 能往返
            string compareTree = "{"
                + "\"id\":\"root\",\"type\":\"Root\",\"children\":[{"
                + "\"id\":\"seq\",\"type\":\"Sequence\",\"decorators\":["
                + "{\"id\":\"d1\",\"type\":\"CompareBBEntries\",\"properties\":{\"keyA\":\"targetDistance\","
                + "\"keyB\":\"alertDistance\",\"operator\":\"<\"}},"
                + "{\"id\":\"d2\",\"type\":\"ForceFailure\"}],"
                + "\"children\":[{\"id\":\"w\",\"type\":\"Task.Wait\",\"properties\":{\"seconds\":1}}]}]}";
            string compareManifest = "{\"format\":\"scbt\",\"version\":1,\"id\":\"selftest.compare\","
                + "\"entry\":\"root\",\"blackboard\":["
                + "{\"name\":\"targetDistance\",\"type\":\"float\",\"readonly\":true},"
                + "{\"name\":\"alertDistance\",\"type\":\"float\"}]}";
            PackageValue compareOk = Parse(PostJson(client, "/api/validate",
                "{\"manifest\":" + compareManifest + ",\"tree\":" + compareTree + "}"));
            Check("validate accepts a tree using ForceFailure + CompareBBEntries",
                compareOk != null && compareOk.Get("ok").AsBool(false),
                compareOk != null ? compareOk.Get("issues").Preview(160) : "<null>");

            string comparePath = Path.Combine(instanceDirectory, "selftest_compare.scbtpak");
            PackageValue compareSaved = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(comparePath),
                "{\"manifest\":" + compareManifest + ",\"tree\":" + compareTree + "}"));
            Check("a tree with the new decorators saves and compiles",
                compareSaved != null && compareSaved.Get("ok").AsBool(false)
                && compareSaved.Get("reloaded").AsBool(false),
                compareSaved != null ? compareSaved.Preview(160) : "<null>");

            // ---- 新任务 + 传感器服务族：属性名 → 字段的映射必须真的落地（往返测试）
            //      只测"能编译"不够：漏写一个 case 的话，属性会被静默丢掉（编辑器里改了没效果）。
            string serviceTree = "{"
                + "\"id\":\"root\",\"type\":\"Root\",\"children\":[{"
                + "\"id\":\"seq\",\"type\":\"Sequence\",\"services\":["
                + "{\"id\":\"svc1\",\"type\":\"UpdateSelf\",\"interval\":0.2,\"properties\":{"
                + "\"prefix\":\"me.\",\"writePosition\":true}},"
                + "{\"id\":\"svc2\",\"type\":\"UpdateBlockAhead\",\"properties\":{"
                + "\"key\":\"wallAhead\",\"maxDistance\":2.5,\"pitchOffset\":-0.4}},"
                + "{\"id\":\"svc3\",\"type\":\"UpdateNearestCreature\",\"properties\":{"
                + "\"targetKey\":\"wolf\",\"categoryMask\":1,\"maxDistance\":24,\"clearWhenMissing\":true}},"
                + "{\"id\":\"svc4\",\"type\":\"UpdateLineOfSight\",\"properties\":{"
                + "\"targetKey\":\"wolf\",\"key\":\"canSee\",\"maxDistance\":40,"
                + "\"targetEyeHeight\":1.6,\"alsoCheckBody\":false,\"bodyHeight\":1.1}}],"
                + "\"children\":["
                + "{\"id\":\"face\",\"type\":\"Task.FaceEntity\",\"properties\":{"
                + "\"targetKey\":\"wolf\",\"toleranceDegrees\":15,\"timeout\":2,\"maxTurnPerSecond\":0}},"
                + "{\"id\":\"emit\",\"type\":\"Task.Emit\",\"properties\":{"
                + "\"category\":\"milestone\",\"message\":\"facing done\",\"key\":\"faced\",\"alsoEngineLog\":false}}]}]}";
            string serviceManifest = "{\"format\":\"scbt\",\"version\":1,\"id\":\"selftest.services\","
                + "\"entry\":\"root\",\"blackboard\":["
                + "{\"name\":\"me.Food\",\"type\":\"float\",\"readonly\":true},"
                + "{\"name\":\"wallAhead\",\"type\":\"bool\"},"
                + "{\"name\":\"faced\",\"type\":\"bool\"},"
                + "{\"name\":\"canSee\",\"type\":\"bool\"},"
                + "{\"name\":\"wolf\",\"type\":\"actor\"}]}";
            PackageValue serviceOk = Parse(PostJson(client, "/api/validate",
                "{\"manifest\":" + serviceManifest + ",\"tree\":" + serviceTree + "}"));
            Check("validate accepts a tree using the new services and tasks (FaceEntity + Emit)",
                serviceOk != null && serviceOk.Get("ok").AsBool(false),
                serviceOk != null ? serviceOk.Get("issues").Preview(200) : "<null>");

            string servicePath = Path.Combine(instanceDirectory, "selftest_services.scbtpak");
            PackageValue serviceSaved = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(servicePath),
                "{\"manifest\":" + serviceManifest + ",\"tree\":" + serviceTree + "}"));
            Check("the new services/tasks save and compile",
                serviceSaved != null && serviceSaved.Get("ok").AsBool(false)
                && serviceSaved.Get("reloaded").AsBool(false),
                serviceSaved != null ? serviceSaved.Preview(200) : "<null>");

            // 读回来：服务的属性与任务属性都还在（映射没丢）
            PackageValue reread = Parse(Get(client, "/api/package?path="
                + Uri.EscapeDataString(servicePath)));
            string rereadTree = reread != null ? reread.Get("tree").ToJson(false) : "";
            Check("service properties survive the round-trip (prefix / key / categoryMask)",
                rereadTree.Contains("\"me.\"") && rereadTree.Contains("wallAhead")
                && rereadTree.Contains("categoryMask"),
                Short(rereadTree));
            Check("task properties survive the round-trip (toleranceDegrees / message)",
                rereadTree.Contains("toleranceDegrees") && rereadTree.Contains("facing done"),
                Short(rereadTree));
            Check("line-of-sight properties survive the round-trip (targetEyeHeight / alsoCheckBody)",
                rereadTree.Contains("targetEyeHeight") && rereadTree.Contains("alsoCheckBody")
                && rereadTree.Contains("bodyHeight"),
                Short(rereadTree));

            // ---- P3：寻路任务 + 世界交互任务族的往返
            string p3Tree = "{"
                + "\"id\":\"root\",\"type\":\"Root\",\"children\":[{"
                + "\"id\":\"seq\",\"type\":\"Sequence\",\"children\":["
                + "{\"id\":\"nav\",\"type\":\"Task.NavigateTo\",\"properties\":{"
                + "\"targetKey\":\"wolf\",\"acceptableRadius\":3,\"timeout\":45,"
                + "\"repathSeconds\":1.5,\"maxPositionsToCheck\":800,\"jumpKey\":\"space\"}},"
                // 坐标模式（source=cell）：走到一组固定的 X/Y/Z 上，键名可改
                + "{\"id\":\"nav2\",\"type\":\"Task.NavigateTo\",\"properties\":{"
                + "\"source\":\"cell\",\"xKey\":\"goalX\",\"yKey\":\"goalY\",\"zKey\":\"goalZ\","
                + "\"acceptableRadius\":1.5,\"timeout\":30}},"
                + "{\"id\":\"useitem\",\"type\":\"Task.UseItem\",\"properties\":{"
                + "\"button\":\"right\",\"holdSeconds\":0.8,\"repeat\":2,\"interval\":0.4}},"
                + "{\"id\":\"jmp\",\"type\":\"Task.Jump\",\"properties\":{"
                + "\"times\":2,\"interval\":0.3,\"alsoForward\":true,\"forwardKey\":\"w\"}},"
                + "{\"id\":\"slot\",\"type\":\"Task.SelectSlot\",\"properties\":{\"slot\":4}},"
                + "{\"id\":\"mine\",\"type\":\"Task.Mine\",\"properties\":{"
                + "\"xKey\":\"mineX\",\"yKey\":\"mineY\",\"zKey\":\"mineZ\",\"timeout\":15}},"
                + "{\"id\":\"place\",\"type\":\"Task.PlaceBlock\",\"properties\":{"
                + "\"xKey\":\"placeX\",\"repeat\":2,\"interval\":0.25}},"
                + "{\"id\":\"hit\",\"type\":\"Task.Attack\",\"properties\":{"
                + "\"targetKey\":\"wolf\",\"range\":3,\"clickInterval\":0.5}},"
                + "{\"id\":\"use\",\"type\":\"Task.Interact\",\"properties\":{"
                + "\"source\":\"actor\",\"targetKey\":\"wolf\",\"repeat\":2}}]}]}";
            string p3Manifest = "{\"format\":\"scbt\",\"version\":1,\"id\":\"selftest.p3\","
                + "\"entry\":\"root\",\"blackboard\":["
                + "{\"name\":\"wolf\",\"type\":\"actor\"},{\"name\":\"mineX\",\"type\":\"int\"},"
                + "{\"name\":\"mineY\",\"type\":\"int\"},{\"name\":\"mineZ\",\"type\":\"int\"},"
                + "{\"name\":\"placeX\",\"type\":\"int\"},"
                + "{\"name\":\"goalX\",\"type\":\"int\"},{\"name\":\"goalY\",\"type\":\"int\"},"
                + "{\"name\":\"goalZ\",\"type\":\"int\"}]}";
            PackageValue p3Ok = Parse(PostJson(client, "/api/validate",
                "{\"manifest\":" + p3Manifest + ",\"tree\":" + p3Tree + "}"));
            Check("validate accepts the find-path and interaction task families",
                p3Ok != null && p3Ok.Get("ok").AsBool(false),
                p3Ok != null ? p3Ok.Get("issues").Preview(200) : "<null>");

            string p3Path = Path.Combine(instanceDirectory, "selftest_p3.scbtpak");
            PackageValue p3Saved = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(p3Path),
                "{\"manifest\":" + p3Manifest + ",\"tree\":" + p3Tree + "}"));
            Check("the find-path and interaction tasks save and compile",
                p3Saved != null && p3Saved.Get("ok").AsBool(false)
                && p3Saved.Get("reloaded").AsBool(false),
                p3Saved != null ? p3Saved.Preview(200) : "<null>");

            PackageValue p3Reread = Parse(Get(client, "/api/package?path="
                + Uri.EscapeDataString(p3Path)));
            string p3RereadTree = p3Reread != null ? p3Reread.Get("tree").ToJson(false) : "";
            Check("find-path properties survive the round-trip (maxPositionsToCheck / repathSeconds)",
                p3RereadTree.Contains("maxPositionsToCheck") && p3RereadTree.Contains("repathSeconds"),
                Short(p3RereadTree));
            // 坐标模式如果没进 registry/编译器/写回器，界面上能选、存盘却会静默退回 actor 模式
            Check("the coordinate navigation mode survives the round-trip (source=cell / goalX…Z)",
                p3RereadTree.Contains("\"cell\"") && p3RereadTree.Contains("goalX")
                && p3RereadTree.Contains("goalY") && p3RereadTree.Contains("goalZ"),
                Short(p3RereadTree));
            PackageValue navSchema = Parse(Get(client, "/api/schema"));
            Check("the palette exposes the coordinate navigation mode as an enum property",
                navSchema != null
                && navSchema.ToJson(false).Contains("goalX")
                && navSchema.ToJson(false).Contains("goalZ"),
                Short(navSchema != null ? navSchema.ToJson(false) : "<null>"));
            Check("interaction properties survive the round-trip (slot / interval / repeat / source)",
                p3RereadTree.Contains("slot") && p3RereadTree.Contains("interval")
                && p3RereadTree.Contains("repeat") && p3RereadTree.Contains("actor"),
                Short(p3RereadTree));
            Check("body-action properties survive the round-trip (holdSeconds / alsoForward)",
                p3RereadTree.Contains("holdSeconds") && p3RereadTree.Contains("alsoForward"),
                Short(p3RereadTree));

            // 黑板键打错（`targtDistance`）必须给出**警告**，否则运行期只表现为"条件永远不成立"
            string typoTree = compareTree.Replace("\"keyA\":\"targetDistance\"",
                "\"keyA\":\"targtDistance\"");
            PackageValue typo = Parse(PostJson(client, "/api/validate",
                "{\"manifest\":" + compareManifest + ",\"tree\":" + typoTree + "}"));
            Check("an undeclared blackboard key warns (and still validates)",
                typo != null && typo.Get("ok").AsBool(false) && typo.Get("warnings").AsInt() > 0
                && typo.Get("issues").ToJson(false).IndexOf("targtDistance", StringComparison.Ordinal) >= 0,
                typo != null ? typo.Get("issues").Preview(200) : "<null>");

            // ---- 校验：故意写坏（任务挂子节点）→ 必须被拦下
            PackageValue broken = Parse(treeJson);
            PackageValue taskNode = FindNodeType(broken, "Task.Wait");
            if (taskNode != null)
            {
                PackageValue child = PackageValue.Object();
                child.Set("id", PackageValue.Str("illegal_child"));
                child.Set("type", PackageValue.Str("Task.Wait"));
                taskNode.Set("children", PackageValue.Array(new[] { child }));
            }
            PackageValue invalid = Parse(PostJson(client, "/api/validate",
                "{\"manifest\":" + manifestJson + ",\"tree\":" + broken.ToJson(false) + "}"));
            Check("validate rejects an illegal tree (task with children)",
                invalid != null && !invalid.Get("ok").AsBool(true)
                && invalid.Get("errors").AsInt() > 0,
                invalid != null ? invalid.Get("issues").Preview(160) : "<null>");

            // ---- 直接保存回原包（包目录里那份游戏正在用的）→ 允许
            PackageValue intoSource = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(demoPath) + "&overwrite=true",
                "{\"manifest\":" + manifestJson + ",\"tree\":" + treeJson + "}"));
            Check("saving back over the package the game loads is allowed",
                intoSource != null && intoSource.Get("ok").AsBool(false)
                && intoSource.Get("root").AsString(null) == "instance",
                intoSource != null ? intoSource.Preview(160) : "<null>");

            // ---- 另存到同一个包目录 → 成功，且能重新装载
            string copyPath = Path.Combine(instanceDirectory, "editor_copy.scbtpak");
            PackageValue saved = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(copyPath),
                "{\"manifest\":" + manifestJson + ",\"tree\":" + treeJson + "}"));
            Check("saving a new package into the package folder succeeds",
                saved != null && saved.Get("ok").AsBool(false) && File.Exists(copyPath),
                saved != null ? saved.Preview(160) : "<null>");
            Check("the saved package reloads and compiles",
                saved != null && saved.Get("reloaded").AsBool(false) && saved.Get("nodes").AsInt() > 0,
                saved != null ? saved.Preview(160) : "<null>");

            // ---- 覆盖保护
            PackageValue again = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(copyPath),
                "{\"manifest\":" + manifestJson + ",\"tree\":" + treeJson + "}"));
            Check("saving over an existing file needs overwrite=true",
                again != null && !again.Get("ok").AsBool(true)
                && again.Get("code").AsString(null) == "exists",
                again != null ? again.Preview(140) : "<null>");

            // ---- 坏树不能写盘
            string badPath = Path.Combine(instanceDirectory, "must_not_exist.scbtpak");
            PackageValue badSave = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(badPath),
                "{\"manifest\":" + manifestJson + ",\"tree\":" + broken.ToJson(false) + "}"));
            Check("an invalid tree is never written to disk",
                badSave != null && !badSave.Get("ok").AsBool(true) && !File.Exists(badPath),
                badSave != null ? badSave.Preview(140) : "<null>");

            // ---- 游戏没在跑时，通知热重载要如实报错
            PackageValue notify = Parse(PostJson(client, "/api/notify?path="
                + Uri.EscapeDataString(copyPath), "{}"));
            Check("notify reports honestly when the game is not running",
                notify != null && !notify.Get("ok").AsBool(true)
                && notify.Get("code").AsString(null) == "game_unreachable",
                notify != null ? notify.Preview(160) : "<null>");

            // ---- meta 要报出目录与游戏状态
            PackageValue meta = Parse(Get(client, "/api/meta"));
            Check("meta reports folders and game state",
                meta != null && meta.Get("writableFolder").AsString(null) != null
                && meta.Has("gameRunning"),
                meta != null ? meta.Preview(160) : "<null>");
        }

        /// <summary>
        /// 动作包（P1）在编辑器里当**物料**用：列出来（能看出"能不能回放"）、校验、
        /// 以及在游戏没在跑时如实报错（不假装回放成功）。
        /// </summary>
        private static void Actions(HttpClient client, string instanceRoot)
        {
            string json = Get(client, "/api/actions");
            PackageValue list = Parse(json);
            Check("action list finds the factory sample package",
                list != null && list.Get("count").AsInt() >= 1,
                list != null ? "count=" + list.Get("count").AsInt() : Short(json));

            PackageValue sample = null;
            PackageValue actions = list != null ? list.Get("actions") : null;
            for (int i = 0; actions != null && i < actions.Count; i++)
            {
                if (string.Equals(actions.Item(i).Get("file").AsString(null),
                    PackageTemplates.SampleActionFile, StringComparison.Ordinal))
                {
                    sample = actions.Item(i);
                }
            }
            Check("the sample action package is listed with duration/frames/replayable",
                sample != null && sample.Get("replayable").AsBool(false)
                && Math.Abs(sample.Get("duration").AsNumber() - PlayerAiPackages.SampleDurationSeconds) < 0.01
                && sample.Get("frames").AsInt() > 0,
                sample != null ? sample.Preview(200) : "<missing>");
            Check("the factory sample comes from the package folder (and is writable)",
                sample != null && sample.Get("source").AsString(null) == "instance"
                && sample.Get("writable").AsBool(false),
                sample != null ? sample.Preview(160) : "<missing>");

            PackageValue valid = Parse(PostJson(client, "/api/action/validate?name="
                + Uri.EscapeDataString(PlayerAiPackages.SampleActionName), "{}"));
            Check("action validate accepts the sample and says it is replayable",
                valid != null && valid.Get("ok").AsBool(false) && valid.Get("replayable").AsBool(false)
                && valid.Get("frames").AsInt() > 0,
                valid != null ? valid.Preview(200) : "<null>");
            Check("action validate reports the single package folder as the source",
                valid != null && valid.Get("source").AsString(null) == "instance",
                valid != null ? valid.Preview(160) : "<null>");

            PackageValue missing = Parse(PostJson(client, "/api/action/validate?name=ghost_action",
                "{}"));
            Check("action validate reports a missing package honestly",
                missing != null && !missing.Get("ok").AsBool(true),
                missing != null ? missing.Preview(160) : "<null>");

            // 自造动作包（不依赖真机录制）→ 写进实例目录，然后两边都看得到
            PackageValue created = Parse(PostJson(client, "/api/action/create",
                "{\"name\":\"selftest_made\",\"overwrite\":true}"));
            Check("the editor can create its own action package in the instance folder",
                created != null && created.Get("ok").AsBool(false) && created.Get("created").AsBool(false)
                && created.Get("source").AsString(null) == "instance"
                && File.Exists(Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees",
                    "selftest_made.scatpak")),
                created != null ? created.Preview(200) : "<null>");
            Check("the created package replays (it carries a per-frame track)",
                created != null && created.Get("replayable").AsBool(false)
                && created.Get("frames").AsInt() > 0,
                created != null ? created.Preview(160) : "<null>");

            PackageValue again = Parse(PostJson(client, "/api/action/create",
                "{\"name\":\"selftest_made\"}"));
            Check("creating over an existing action package needs overwrite=true",
                again != null && !again.Get("ok").AsBool(true)
                && again.Get("code").AsString(null) == "exists",
                again != null ? again.Preview(140) : "<null>");

            // ---- 动作包编辑（右键菜单：编辑 / 重命名 / 删除）
            // 这三件事用户明确要求过，而且都会**写文件**，必须有自检钉住。
            PackageValue events = Parse(Get(client, "/api/action/events?file="
                + Uri.EscapeDataString("selftest_made.scatpak")));
            Check("the editor can read an action package's semantic events",
                events != null && events.Get("ok").AsBool(false)
                && events.Get("file").AsString(null) == "selftest_made.scatpak"
                && events.Get("writable").AsBool(false),
                events != null ? events.Preview(200) : "<null>");

            PackageValue saved = Parse(PostJson(client, "/api/action/events",
                "{\"file\":\"selftest_made.scatpak\",\"events\":["
                + "{\"t\":0.1,\"kind\":\"ui.click\",\"detail\":\"click:Play\"},"
                + "{\"t\":0.6,\"kind\":\"ui.click\",\"detail\":\"click:list:WorldsList@Rebritish\"}]}"));
            Check("saving events writes them back and re-validates",
                saved != null && saved.Get("ok").AsBool(false) && saved.Get("saved").AsBool(false)
                && saved.Get("eventCount").AsInt() == 2,
                saved != null ? saved.Preview(200) : "<null>");
            Check("the saved package still replays (the per-frame track was kept)",
                saved != null && saved.Get("replayable").AsBool(false),
                saved != null ? saved.Preview(160) : "<null>");

            PackageValue badEvents = Parse(PostJson(client, "/api/action/events",
                "{\"file\":\"selftest_made.scatpak\",\"events\":[{\"t\":0.1,\"kind\":\"ui.click\"}]}"));
            Check("an event without a detail is refused (no silent empty click)",
                badEvents != null && !badEvents.Get("ok").AsBool(true),
                badEvents != null ? badEvents.Preview(160) : "<null>");

            PackageValue renamed = Parse(PostJson(client, "/api/action/rename",
                "{\"file\":\"selftest_made.scatpak\",\"to\":\"selftest_renamed\"}"));
            Check("renaming an action package moves the file (manifest name follows)",
                renamed != null && renamed.Get("ok").AsBool(false) && renamed.Get("renamed").AsBool(false)
                && renamed.Get("file").AsString(null) == "selftest_renamed.scatpak"
                && File.Exists(Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees",
                    "selftest_renamed.scatpak")),
                renamed != null ? renamed.Preview(200) : "<null>");

            PackageValue sameName = Parse(PostJson(client, "/api/action/rename",
                "{\"file\":\"selftest_renamed.scatpak\",\"to\":\"selftest_renamed\"}"));
            Check("renaming onto the same name is a no-op (not an error)",
                sameName != null && sameName.Get("ok").AsBool(false)
                && sameName.Get("renamed").AsBool(true) == false,
                sameName != null ? sameName.Preview(140) : "<null>");

            PackageValue removed = Parse(PostJson(client, "/api/action/delete",
                "{\"file\":\"selftest_renamed.scatpak\"}"));
            Check("deleting an action package removes the file",
                removed != null && removed.Get("ok").AsBool(false) && removed.Get("deleted").AsBool(false)
                && !File.Exists(Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees",
                    "selftest_renamed.scatpak")),
                removed != null ? removed.Preview(160) : "<null>");

            PackageValue removedAgain = Parse(PostJson(client, "/api/action/delete",
                "{\"file\":\"selftest_renamed.scatpak\"}"));
            Check("deleting a missing package reports not_found honestly",
                removedAgain != null && !removedAgain.Get("ok").AsBool(true),
                removedAgain != null ? removedAgain.Preview(140) : "<null>");

            // 游戏没在跑：回放/停止都要如实报错（不能假装成功）
            PackageValue play = Parse(PostJson(client, "/api/action/play",
                "{\"path\":\"" + PlayerAiPackages.SampleActionName + "\",\"repeat\":2}"));
            Check("action play reports honestly when the game is not running",
                play != null && !play.Get("ok").AsBool(true)
                && play.Get("code").AsString(null) == "game_unreachable",
                play != null ? play.Preview(160) : "<null>");

            PackageValue stop = Parse(PostJson(client, "/api/action/stop", "{}"));
            Check("action stop reports honestly when the game is not running",
                stop != null && !stop.Get("ok").AsBool(true)
                && stop.Get("code").AsString(null) == "game_unreachable",
                stop != null ? stop.Preview(160) : "<null>");

            PackageValue ghost = Parse(PostJson(client, "/api/action/play",
                "{\"path\":\"ghost_action\"}"));
            Check("action play refuses an unknown package before touching the game",
                ghost != null && !ghost.Get("ok").AsBool(true)
                && ghost.Get("code").AsString(null) == "not_found",
                ghost != null ? ghost.Preview(160) : "<null>");
        }

        /// <summary>
        /// 「新建空白树」走的是**和手点按钮完全一样的那条路**：前端按最小骨架拼出
        /// manifest+tree，POST `/api/package`，由游戏内同一份校验器判定能不能存。
        /// 这里用同一份 payload 打一遍，确认"新建出来的树游戏真能装载"。
        /// </summary>
        private static void BlankTree(HttpClient client, string instanceDirectory)
        {
            const string manifest = "{\"format\":\"scbt\",\"version\":1,\"id\":\"selftest_new\","
                + "\"name\":\"selftest_new\",\"entry\":\"root\",\"blackboard\":[]}";
            const string tree = "{\"id\":\"root\",\"type\":\"Root\",\"children\":[{\"id\":\"seq\","
                + "\"type\":\"Sequence\",\"children\":[{\"id\":\"wait\",\"type\":\"Task.Wait\","
                + "\"properties\":{\"seconds\":1}}]}]}";

            string path = Path.Combine(instanceDirectory, "selftest_new.scbtpak");
            PackageValue saved = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(path),
                "{\"manifest\":" + manifest + ",\"tree\":" + tree + "}"));
            Check("a newly created blank tree saves and reloads",
                saved != null && saved.Get("ok").AsBool(false) && saved.Get("reloaded").AsBool(false)
                && saved.Get("nodes").AsInt() == 3,
                saved != null ? saved.Preview(200) : "<null>");
            Check("the blank tree passes the game validator with zero errors",
                saved != null && saved.Get("validation").Get("errors").AsInt() == 0,
                saved != null ? saved.Get("validation").Preview(160) : "<null>");

            PackageValue read = Parse(Get(client, "/api/package?path=" + Uri.EscapeDataString(path)));
            Check("the new tree is readable and writable (it lives in the instance folder)",
                read != null && read.Get("ok").AsBool(false) && read.Get("writable").AsBool(false)
                && read.Get("tree").Get("type").AsString(null) == "Root",
                read != null ? read.Preview(160) : "<null>");

            PackageValue list = Parse(Get(client, "/api/packages"));
            bool listed = false;
            PackageValue packages = list != null ? list.Get("packages") : null;
            for (int i = 0; packages != null && i < packages.Count; i++)
            {
                if (string.Equals(packages.Item(i).Get("file").AsString(null),
                    "selftest_new.scbtpak", StringComparison.Ordinal))
                {
                    listed = true;
                }
            }
            Check("the new tree shows up in the package list", listed, "<not listed>");
        }

        /// <summary>
        /// `GET /api/game/live` 与 `POST /api/game/pause|resume`。
        ///
        /// 自检时游戏**没在跑**：这时唯一正确的行为是**如实报错**（`game_unreachable`），
        /// 而不是假装成功或者抛 500 —— 编辑器面板就是靠这个把"游戏没开"显示给人看的。
        /// </summary>
        private static void LiveMonitor(HttpClient client)
        {
            PackageValue live = Parse(Get(client, "/api/game/live"));
            Check("the live monitor endpoint answers even when the game is not running",
                live != null && live.Get("ok").AsBool(true) == false
                && live.Get("code").AsString(null) == "game_unreachable",
                live != null ? live.Preview(160) : "<null>");

            PackageValue pause = Parse(PostJson(client, "/api/game/pause", "{}"));
            Check("pausing from the editor reports honestly when the game is not running",
                pause != null && pause.Get("ok").AsBool(true) == false
                && pause.Get("code").AsString(null) == "game_unreachable",
                pause != null ? pause.Preview(160) : "<null>");

            PackageValue resume = Parse(PostJson(client, "/api/game/resume", "{}"));
            Check("resuming from the editor reports honestly when the game is not running",
                resume != null && resume.Get("ok").AsBool(true) == false
                && resume.Get("code").AsString(null) == "game_unreachable",
                resume != null ? resume.Preview(160) : "<null>");
        }

        /// <summary>
        /// 启动 / 结束游戏，以及"三态"判断（没启动 / 启动了但通道还没开 / 已连上）。
        ///
        /// 自检用的实例根是临时目录、里面**没有 Survivalcraft.exe** —— 所以这里既证明了
        /// "找不到 exe 时如实拒绝、绝不乱起进程"，也顺手把"旧 runtime 文件"的谎话堵住：
        /// 文件在、进程不在时必须是 `runtimeStale`，而不是让人以为是"游戏在跑但通道坏了"。
        /// </summary>
        private static void GameProcess(HttpClient client, string instanceRoot)
        {
            string runtimePath = System.IO.Path.Combine(instanceRoot,
                GameBridgeClient.RuntimeFileName);

            PackageValue before = Parse(Get(client, "/api/game/process"));
            Check("game process status reports the executable path and existence",
                before != null && before.Get("exePath").AsString(null) != null
                && before.Get("exeExists").AsBool(true) == false
                && before.Get("running").AsBool(true) == false,
                before != null ? before.Preview(200) : "<null>");
            Check("with no running game the channel is honestly reported as not connected",
                before != null && before.Get("channelConnected").AsBool(true) == false
                && before.Get("channelError").AsString(null) != null,
                before != null ? before.Get("channelError").AsString("<none>") : "<null>");
            Check("no runtime file yet -> not flagged as stale (there is nothing stale)",
                before != null && before.Get("runtimeExists").AsBool(true) == false
                && before.Get("runtimeStale").AsBool(true) == false,
                before != null ? before.Preview(200) : "<null>");

            // 找不到 exe → 拒绝启动。自检环境里实例根是临时目录、本来就没有 Survivalcraft.exe，
            // 所以这条既证明"如实拒绝"，也保证**自检不会真的把游戏拉起来**。
            PackageValue launch = Parse(PostJson(client, "/api/game/launch", "{}"));
            Check("launching without a game executable fails honestly (and starts nothing)",
                launch != null && launch.Get("ok").AsBool(true) == false
                && launch.Get("code").AsString(null) == "game_exe_missing",
                launch != null ? launch.Preview(200) : "<null>");

            PackageValue quit = Parse(PostJson(client, "/api/game/quit", "{}"));
            Check("quitting when nothing is running says so instead of pretending",
                quit != null && quit.Get("ok").AsBool(true) && quit.Get("closed").AsInt(-1) == 0
                && quit.Get("killed").AsInt(-1) == 0,
                quit != null ? quit.Preview(200) : "<null>");

            // ---- 旧 runtime 文件（游戏上次退出时留下的）：真实现场就是这样，端口连不上
            int deadPort = FreePort();
            File.WriteAllText(runtimePath,
                "{\"port\":" + deadPort + ",\"token\":\"selftest-stale\"}", new UTF8Encoding(false));

            PackageValue stale = Parse(Get(client, "/api/game/process"));
            Check("a runtime file left over by a dead game is flagged as stale",
                stale != null && stale.Get("runtimeExists").AsBool(false)
                && stale.Get("runtimeStale").AsBool(false)
                && stale.Get("channelConnected").AsBool(true) == false,
                stale != null ? stale.Preview(220) : "<null>");

            PackageValue live = Parse(Get(client, "/api/game/live"));
            Check("a stale runtime file does not masquerade as a live channel",
                live != null && live.Get("ok").AsBool(true) == false
                && live.Get("reason").AsString(string.Empty).Contains("旧文件"),
                live != null ? live.Get("reason").AsString("<none>") : "<null>");
            Check("the stale runtime file is left alone (the editor never rewrites it)",
                File.Exists(runtimePath), runtimePath);

            // ---- 假游戏通道：把"游戏开在跑、但明确拒绝"的两种情况钉死
            // 用户实测的 bug：世界还没加载时点实时监视，报的是"多半是 Mod 没有这个命令"——
            // 把 `not_ready` 说成了 Mod 太旧。这里用一个只会说 not_ready 的假通道来守它。
            using (var fake = new FakeGameChannel(
                "{\"ok\":false,\"error\":{\"code\":\"not_ready\",\"message\":"
                + "\"ai.tree.snapshot needs a controllable local player (load a world and make sure "
                + "AI is enabled).\"}}"))
            {
                File.WriteAllText(runtimePath,
                    "{\"port\":" + fake.Port + ",\"token\":\"selftest\"}", new UTF8Encoding(false));

                PackageValue refused = Parse(Get(client, "/api/game/live"));
                Check("a not_ready refusal is reported as 还没准备好（不是「Mod 太旧」）",
                    refused != null && refused.Get("code").AsString(null) == "game_not_ready"
                    && refused.Get("reason").AsString(string.Empty).Contains("还没准备好")
                    && refused.Get("reason").AsString(string.Empty).Contains("ai enable"),
                    refused != null ? refused.Get("reason").AsString("<none>") : "<null>");
                Check("the game's own words are kept in the message",
                    refused != null && refused.Get("reason").AsString(string.Empty)
                        .Contains("controllable local player"),
                    refused != null ? refused.Get("reason").AsString("<none>") : "<null>");

                PackageValue unknown = Parse(Get(client, "/api/game/status"));
                Check("the same refusal from another endpoint is classified the same way",
                    unknown != null && unknown.Get("code").AsString(null) == "game_not_ready",
                    unknown != null ? unknown.Preview(160) : "<null>");

                // 切树也要走同一套分类（播放按钮点下去时最可能撞上它）
                string demoFile = Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees",
                    PackageTemplates.DemoFile);
                PackageValue switchRefused = Parse(PostJson(client,
                    "/api/game/tree/switch?path=" + Uri.EscapeDataString(demoFile), "{}"));
                Check("switching the tree reports 还没准备好 instead of a Mod-version story",
                    switchRefused != null && switchRefused.Get("ok").AsBool(true) == false
                    && switchRefused.Get("code").AsString(null) == "game_not_ready",
                    switchRefused != null ? switchRefused.Preview(200) : "<null>");
            }

            // ---- 假游戏通道：通道正常时，切树要如实把游戏的回应带回来
            using (var fake = new FakeGameChannel(
                "{\"ok\":true,\"result\":{\"switched\":true,\"usedPrepared\":false,\"recompiled\":true,"
                + "\"path\":\"demo.greet.scbtpak\",\"hash\":\"abc123\",\"nodes\":8,\"switchMs\":1.25,"
                + "\"totalMs\":3.5,\"compileMs\":2.25,\"mode\":\"tree\",\"issues\":[]}}"))
            {
                File.WriteAllText(runtimePath,
                    "{\"port\":" + fake.Port + ",\"token\":\"selftest\"}", new UTF8Encoding(false));
                string demoFile = Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees",
                    PackageTemplates.DemoFile);
                PackageValue switched = Parse(PostJson(client,
                    "/api/game/tree/switch?path=" + Uri.EscapeDataString(demoFile), "{}"));
                Check("a successful switch reports the game's own numbers",
                    switched != null && switched.Get("ok").AsBool(false)
                    && switched.Get("switched").AsBool(false)
                    && switched.Get("nodes").AsInt() == 8
                    && switched.Get("file").AsString(null) == PackageTemplates.DemoFile,
                    switched != null ? switched.Preview(200) : "<null>");
                Check("the editor sends ai.tree.switch with an absolute path",
                    fake.LastCommand == "ai.tree.switch"
                    && fake.LastRequest.Contains(demoFile.Replace("\\", "\\\\")),
                    fake.LastCommand + " / " + Short(fake.LastRequest));
            }

            // ---- UI 定位/点击服务（UI-1）：编辑器把语义目标转发给游戏的 `ui.locate`
            //      / `ui.clickelement`，"拾取界面元素"面板靠这两条活。
            using (var fake = new FakeGameChannel(
                "{\"ok\":true,\"result\":{\"target\":\"list:WorldsList@Rebritish\",\"kind\":\"ListRow\","
                + "\"name\":\"WorldsList\",\"path\":\"[SuPlayScreen#0]/…/WorldsList\",\"text\":\"Rebritish\","
                + "\"clickable\":true,\"hittable\":true,\"list\":{\"index\":0,\"text\":\"Rebritish\",\"count\":4},"
                + "\"clientPoint\":{\"x\":351.75,\"y\":36.6}}}"))
            {
                File.WriteAllText(runtimePath,
                    "{\"port\":" + fake.Port + ",\"token\":\"selftest\"}", new UTF8Encoding(false));
                string wanted = "list:WorldsList@Rebritish";
                PackageValue located = Parse(Get(client,
                    "/api/game/ui/locate?target=" + Uri.EscapeDataString(wanted)));
                Check("locating a UI target relays the game's live coordinates",
                    located != null && located.Get("ok").AsBool(false)
                    && located.Get("clientPoint").Get("x").AsNumber() > 0
                    && located.Get("list").Get("index").AsInt() == 0,
                    located != null ? located.Preview(200) : "<null>");
                Check("the editor sends ui.locate with the semantic target",
                    fake.LastCommand == "ui.locate" && fake.LastRequest.Contains("WorldsList"),
                    fake.LastCommand + " / " + Short(fake.LastRequest));
                // 落点标记（用户要求："点定位…渲染 2s 直接 5 像素的红色圆点，方便定位"）：
                // 编辑器必须把 mark 一起发出去，否则游戏里什么都不会亮。
                Check("the editor asks for the landing marker when locating",
                    fake.LastRequest.Contains("\"mark\":true"),
                    Short(fake.LastRequest));
            }

            using (var fake = new FakeGameChannel(
                "{\"ok\":true,\"result\":{\"target\":\"Play\",\"mode\":\"direct\","
                + "\"name\":\"Play\",\"clickPoint\":{\"x\":394.7,\"y\":459.7}}}"))
            {
                File.WriteAllText(runtimePath,
                    "{\"port\":" + fake.Port + ",\"token\":\"selftest\"}", new UTF8Encoding(false));
                PackageValue clicked = Parse(PostJson(client, "/api/game/ui/click",
                    "{\"target\":\"Play\",\"mode\":\"direct\"}"));
                Check("clicking a UI target relays the game's verdict and mode",
                    clicked != null && clicked.Get("ok").AsBool(false)
                    && clicked.Get("mode").AsString(null) == "direct"
                    && clicked.Get("clickPoint").Get("x").AsNumber() > 0,
                    clicked != null ? clicked.Preview(200) : "<null>");
                Check("the editor sends ui.clickelement with target and mode",
                    fake.LastCommand == "ui.clickelement"
                    && fake.LastRequest.Contains("\"target\":\"Play\"")
                    && fake.LastRequest.Contains("\"mode\":\"direct\""),
                    fake.LastCommand + " / " + Short(fake.LastRequest));
                Check("clicking also asks for the landing marker (so you can see where it clicked)",
                    fake.LastRequest.Contains("\"mark\":true"),
                    Short(fake.LastRequest));
            }

            // ---- UI 拾取面板：**列表行**那一块（用户实测在地图选择界面整个面板报错）
            //      关键在"嵌套数组不能被压成字符串"：游戏侧 `elements` 本身是**一段 JSON 文本**
            //      （`GameBridgeClient.Collect` 把数组 ToJson 成字符串），编辑器解析回数组之后，
            //      `list.items`（每一行）也必须**仍然是数组** —— 以前它在 PlainValue 里被
            //      `ToJson()` 成了字符串，前端 `list.items.forEach` 直接抛
            //      `is not a function`（字符串也有 .length，所以长度守卫拦不住）。
            using (var fake = new FakeGameChannel(BuildUiElementsAnswer()))
            {
                File.WriteAllText(runtimePath,
                    "{\"port\":" + fake.Port + ",\"token\":\"selftest\"}", new UTF8Encoding(false));
                PackageValue picked = Parse(Get(client, "/api/game/ui/elements"));
                PackageValue elements = picked != null ? picked.Get("elements") : null;
                Check("UI 拾取：elements 是真数组（游戏侧给的是一段 JSON 文本）",
                    elements != null && elements.IsArray && elements.Count == 2,
                    elements != null ? elements.Preview(160) : "<null>");
                PackageValue listElement = elements != null ? elements.Item(0) : null;
                PackageValue items = listElement != null ? listElement.Get("list").Get("items") : null;
                Check("UI 拾取：列表行 list.items 必须是**真数组**，不能是一段 JSON 文本",
                    items != null && items.IsArray && items.Count == 2
                    && items.Item(0).Get("text").AsString(null) == "Rebritish",
                    items != null ? items.Preview(160) : "<null>");
                Check("UI 拾取：行里带每一行的点击坐标（前端拿它显示/定位）",
                    items != null && items.IsArray
                    && items.Item(0).Get("clientPoint").Get("x").AsNumber() > 0,
                    items != null ? items.Preview(160) : "<null>");
                Check("UI 拾取：列表元素的 selectedIndex 会变成 rowTarget（list:列表#索引）",
                    listElement != null
                    && listElement.Get("rowTarget").AsString(null) == "list:WorldsList#1",
                    listElement != null ? listElement.Preview(160) : "<null>");
                Check("UI 拾取：每个元素都带上语义目标（路径优先）",
                    listElement != null
                    && listElement.Get("target").AsString(null) == "[SuPlayScreen#0]/WorldsList"
                    && listElement.Get("shortTarget").AsString(null) == "WorldsList",
                    listElement != null ? listElement.Preview(160) : "<null>");
            }

            // ---- 停止：编辑器把游戏的回应原样带回来（用户要的"重置"入口）
            using (var fake = new FakeGameChannel(
                "{\"ok\":true,\"result\":{\"stopped\":true,\"reason\":null,\"host\":\"menu\","
                + "\"mode\":\"idle\",\"hasTree\":false}}"))
            {
                File.WriteAllText(runtimePath,
                    "{\"port\":" + fake.Port + ",\"token\":\"selftest\"}", new UTF8Encoding(false));
                PackageValue stopped = Parse(PostJson(client, "/api/game/tree/stop", "{}"));
                Check("stopping the tree relays the game's answer (stopped/mode/hasTree)",
                    stopped != null && stopped.Get("ok").AsBool(false)
                    && stopped.Get("stopped").AsBool(false)
                    && stopped.Get("mode").AsString(null) == "idle"
                    && stopped.Get("hasTree").AsBool(true) == false,
                    stopped != null ? stopped.Preview(200) : "<null>");
                Check("the editor sends ai.tree.stop",
                    fake.LastCommand == "ai.tree.stop", fake.LastCommand);
            }

            try
            {
                File.Delete(runtimePath);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 一个"只说一句话"的假游戏通道：监听回环端口，收到请求就回一段固定 JSON。
        /// 用来测那些**只有真游戏才会给出的错误码**（`not_ready` / `unknown_command` …）——
        /// 这些分支恰恰是"报错文案把用户指错方向"的重灾区（实测踩过）。
        /// </summary>
        private sealed class FakeGameChannel : IDisposable
        {
            private readonly TcpListener m_listener;
            private readonly string m_response;
            private readonly Thread m_thread;
            private volatile bool m_stop;

            public FakeGameChannel(string responseJson)
            {
                m_response = responseJson;
                m_listener = new TcpListener(IPAddress.Loopback, 0);
                m_listener.Start();
                Port = ((IPEndPoint)m_listener.LocalEndpoint).Port;
                m_thread = new Thread(Loop) { IsBackground = true };
                m_thread.Start();
            }

            public int Port { get; }

            public string LastRequest { get; private set; } = string.Empty;

            public string LastCommand { get; private set; } = string.Empty;

            private void Loop()
            {
                while (!m_stop)
                {
                    try
                    {
                        using (TcpClient client = m_listener.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream())
                        {
                            var buffer = new List<byte>(256);
                            var one = new byte[1];
                            while (stream.Read(one, 0, 1) > 0 && one[0] != (byte)'\n')
                                buffer.Add(one[0]);
                            LastRequest = Encoding.UTF8.GetString(buffer.ToArray());
                            LastCommand = ExtractCommand(LastRequest);
                            byte[] payload = Encoding.UTF8.GetBytes(m_response + "\n");
                            stream.Write(payload, 0, payload.Length);
                            stream.Flush();
                        }
                    }
                    catch (Exception)
                    {
                        if (m_stop)
                            return;
                    }
                }
            }

            private static string ExtractCommand(string request)
            {
                int at = request.IndexOf("\"command\"", StringComparison.Ordinal);
                if (at < 0)
                    return string.Empty;
                int first = request.IndexOf('"', request.IndexOf(':', at) + 1);
                int second = first < 0 ? -1 : request.IndexOf('"', first + 1);
                return first < 0 || second < 0 ? string.Empty
                    : request.Substring(first + 1, second - first - 1);
            }

            public void Dispose()
            {
                m_stop = true;
                try
                {
                    m_listener.Stop();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>拿一个"刚刚还开着、现在已经关掉"的端口：连它必然被拒绝，又不会撞上别人。</summary>
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>
        /// `GET /api/subtree`：把 `Task.Subtree` 引用的包**按游戏内同一套规则**解析出来。
        /// 出厂示例 `demo.greet` 里有 `Task.Subtree(sub_look)` 引用 `common.scbtpak#greet.look`，
        /// 正好当靶子：解析出来的必须就是那个包的入口节点，而不是"随便读了个包"。
        /// </summary>
        private static void Subtree(HttpClient client, string instanceRoot)
        {
            string demoPath = Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees",
                PackageTemplates.DemoFile);

            PackageValue read = Parse(Get(client, "/api/package?path="
                + Uri.EscapeDataString(demoPath)));
            string subtreeId = FindSubtreeNodeId(read != null ? read.Get("tree") : null);
            Check("the factory demo tree contains a Task.Subtree node", subtreeId != null,
                subtreeId ?? ("demoPath=" + demoPath + " read="
                    + (read != null ? read.Preview(120) : "<null>")));

            if (subtreeId == null)
                return;

            PackageValue resolved = Parse(Get(client, "/api/subtree?path="
                + Uri.EscapeDataString(demoPath) + "&node=" + Uri.EscapeDataString(subtreeId)));
            Check("the referenced package resolves like the game resolves it",
                resolved != null && resolved.Get("ok").AsBool(false)
                && resolved.Get("resolvedId").AsString(null) == "common",
                resolved != null ? resolved.Preview(200) : "<null>");
            Check("the resolved entry node is the one the reference names",
                resolved != null && resolved.Get("entry").AsString(null) == "greet.look",
                resolved != null ? resolved.Get("entry").AsString(null) : "<null>");
            Check("the whole referenced tree comes back for inline folding",
                resolved != null && resolved.Get("tree").IsObject
                && resolved.Get("nodes").AsInt() > 0,
                resolved != null ? "nodes=" + resolved.Get("nodes").AsInt() : "<null>");
            Check("the reference list says which references resolved",
                resolved != null && resolved.Get("references").Count >= 1,
                resolved != null ? resolved.Get("references").Preview(120) : "<null>");

            PackageValue ghost = Parse(Get(client, "/api/subtree?path="
                + Uri.EscapeDataString(demoPath) + "&node=ghost_node"));
            Check("an unknown node reports not_found honestly",
                ghost != null && !ghost.Get("ok").AsBool(true)
                && ghost.Get("code").AsString(null) == "not_found",
                ghost != null ? ghost.Preview(140) : "<null>");
        }

        private static string FindSubtreeNodeId(PackageValue tree)
        {
            if (tree == null || !tree.IsObject)
                return null;
            if (string.Equals(tree.Get("type").AsString(null), "Task.Subtree", StringComparison.Ordinal))
                return tree.Get("id").AsString(null);
            PackageValue children = tree.Get("children");
            for (int i = 0; i < children.Count; i++)
            {
                string found = FindSubtreeNodeId(children.Item(i));
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>静态资源的响应头里必须有 no-store（否则浏览器可能一直用旧的 app.js）。</summary>
        private static bool CachedControl(HttpClient client)
        {
            try
            {
                HttpResponseMessage response = client.GetAsync("/app.js").Result;
                IEnumerable<string> values;
                if (!response.Headers.TryGetValues("Cache-Control", out values))
                    return false;
                foreach (string value in values)
                {
                    if (value != null && value.IndexOf("no-store", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 工具
        private static PackageValue FindNodeType(PackageValue node, string type)
        {
            if (node == null || !node.IsObject)
                return null;
            if (string.Equals(node.Get("type").AsString(null), type, StringComparison.Ordinal))
                return node;

            PackageValue children = node.Get("children");
            for (int i = 0; i < children.Count; i++)
            {
                PackageValue found = FindNodeType(children.Item(i), type);
                if (found != null)
                    return found;
            }
            // 也翻 decorators/services 之外的对象（schema 的 nodes 数组用下面那个入口）
            foreach (string name in node.MemberNames)
            {
                PackageValue member = node.Get(name);
                if (member.IsArray)
                {
                    for (int i = 0; i < member.Count; i++)
                    {
                        PackageValue found = FindNodeType(member.Item(i), type);
                        if (found != null)
                            return found;
                    }
                }
            }
            return null;
        }

        private static PackageValue FindProperty(PackageValue nodeType, string name)
        {
            PackageValue properties = nodeType.Get("properties");
            for (int i = 0; i < properties.Count; i++)
            {
                if (string.Equals(properties.Item(i).Get("name").AsString(null), name,
                    StringComparison.Ordinal))
                {
                    return properties.Item(i);
                }
            }
            return null;
        }

        private static string Get(HttpClient client, string path)
        {
            try
            {
                HttpResponseMessage response = client.GetAsync(path).Result;
                return response.Content.ReadAsStringAsync().Result;
            }
            catch (Exception exception)
            {
                return "<error: " + exception.Message + ">";
            }
        }

        private static string PostJson(HttpClient client, string path, string json)
        {
            try
            {
                var content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json");
                HttpResponseMessage response = client.PostAsync(path, content).Result;
                return response.Content.ReadAsStringAsync().Result;
            }
            catch (Exception exception)
            {
                return "<error: " + exception.Message + ">";
            }
        }

        /// <summary>
        /// 造一份"和游戏侧一模一样"的 `ui.elements` 回包：`elements` 是**一段 JSON 文本**
        /// （`GameBridgeClient.Collect` 对数组就是这个行为）。用来钉住
        /// "嵌套数组不能被压成字符串"这条 —— 列表行 `list.items` 一旦变文本，
        /// 前端就报 `items.forEach is not a function`，整个拾取面板用不了。
        /// </summary>
        private static string BuildUiElementsAnswer()
        {
            string elements =
                "[{\"name\":\"WorldsList\",\"type\":\"ListPanelWidget\","
                + "\"path\":\"[SuPlayScreen#0]/WorldsList\",\"text\":\"\",\"clickable\":true,"
                + "\"list\":{\"itemsCount\":2,\"itemSize\":52,\"selectedIndex\":1,"
                + "\"items\":[{\"index\":0,\"text\":\"Rebritish\","
                + "\"clientPoint\":{\"x\":1010.5,\"y\":64.8}},"
                + "{\"index\":1,\"text\":\"Other\",\"clientPoint\":{\"x\":1010.5,\"y\":116.8}}]}},"
                + "{\"name\":\"Play\",\"type\":\"BevelledButtonWidget\","
                + "\"path\":\"[SuPlayScreen#0]/Play\",\"text\":\"Play\",\"clickable\":true}]";
            return "{\"ok\":true,\"result\":{\"screen\":\"SuPlayScreen#0\",\"elements\":\""
                + elements.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}";
        }

        private static PackageValue Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            PackageValue value;
            var report = new PackageReport();
            return PackageJson.TryParse(json, "selftest", report, out value) ? value : null;
        }

        private static string Short(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "<empty>";
            return text.Length <= 100 ? text : text.Substring(0, 100) + "…";
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
                m_passed++;
            else
                m_failed++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name
                + (string.IsNullOrEmpty(detail) ? string.Empty : "  - " + detail));
        }
    }
}

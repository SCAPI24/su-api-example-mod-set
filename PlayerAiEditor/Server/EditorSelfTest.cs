using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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

            // 出厂内容：示例树 + 示例动作包（编辑器要能列出它们）
            string instanceDirectory = Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees");
            string modDirectory = Path.Combine(instanceRoot, "Mods", "PlayerAiMod", "PlayerAi",
                "BehaviorTrees");
            var roots = new PackageRoots(instanceDirectory, modDirectory);
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
            Check("index.html carries a real build stamp (not the placeholder)",
                html != null && html.Contains("buildBadge") && !html.Contains("<!--BUILDSTAMP-->")
                && html.Contains("构建 "),
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
        }

        private static void Packages(HttpClient client, string instanceRoot, string instanceDirectory)
        {
            string listJson = Get(client, "/api/packages");
            PackageValue list = Parse(listJson);
            Check("package list finds the factory trees",
                list != null && list.Get("packages").Count >= 3,
                "count=" + (list != null ? list.Get("packages").Count : -1));

            string demoPath = Path.Combine(instanceRoot, "Mods", "PlayerAiMod", "PlayerAi",
                "BehaviorTrees", PackageTemplates.DemoFile);
            Check("demo package exists on disk", File.Exists(demoPath), demoPath);

            PackageValue read = Parse(Get(client, "/api/package?path="
                + Uri.EscapeDataString(demoPath)));
            Check("reading a package returns manifest + tree",
                read != null && read.Get("ok").AsBool() && read.Get("manifest").IsObject
                && read.Get("tree").IsObject,
                read != null ? read.Get("issues").Preview(120) : "<null>");
            Check("a package in the mod folder is writable too (the editor may modify what the game loads)",
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

            // ---- 保存回 Mod 分发目录 → 允许（编辑器要能改游戏正在用的那份），并提醒会被 Mod 更新覆盖
            PackageValue intoMod = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(demoPath) + "&overwrite=true",
                "{\"manifest\":" + manifestJson + ",\"tree\":" + treeJson + "}"));
            Check("saving back into the mod folder is allowed (with an overwrite warning)",
                intoMod != null && intoMod.Get("ok").AsBool(false)
                && intoMod.Get("warnsModFolder").AsBool(false),
                intoMod != null ? intoMod.Preview(160) : "<null>");

            // ---- 另存到实例目录 → 成功，且能重新装载
            string copyPath = Path.Combine(instanceDirectory, "editor_copy.scbtpak");
            PackageValue saved = Parse(PostJson(client, "/api/package?path="
                + Uri.EscapeDataString(copyPath),
                "{\"manifest\":" + manifestJson + ",\"tree\":" + treeJson + "}"));
            Check("saving into the instance folder succeeds",
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
            Check("the factory sample reports the mod folder as its root (and is writable)",
                sample != null && sample.Get("source").AsString(null) == "mod"
                && sample.Get("writable").AsBool(false),
                sample != null ? sample.Preview(160) : "<missing>");

            PackageValue valid = Parse(PostJson(client, "/api/action/validate?name="
                + Uri.EscapeDataString(PlayerAiPackages.SampleActionName), "{}"));
            Check("action validate accepts the sample and says it is replayable",
                valid != null && valid.Get("ok").AsBool(false) && valid.Get("replayable").AsBool(false)
                && valid.Get("frames").AsInt() > 0,
                valid != null ? valid.Preview(200) : "<null>");
            Check("action validate resolves a name that only exists in the mod folder",
                valid != null && valid.Get("source").AsString(null) == "mod",
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
        /// `GET /api/subtree`：把 `Task.Subtree` 引用的包**按游戏内同一套规则**解析出来。
        /// 出厂示例 `demo.greet` 里有 `Task.Subtree(sub_look)` 引用 `common.scbtpak#greet.look`，
        /// 正好当靶子：解析出来的必须就是那个包的入口节点，而不是"随便读了个包"。
        /// </summary>
        private static void Subtree(HttpClient client, string instanceRoot)
        {
            string demoPath = Path.Combine(instanceRoot, "Mods", "PlayerAiMod", "PlayerAi",
                "BehaviorTrees", PackageTemplates.DemoFile);

            PackageValue read = Parse(Get(client, "/api/package?path="
                + Uri.EscapeDataString(demoPath)));
            string subtreeId = FindSubtreeNodeId(read != null ? read.Get("tree") : null);
            Check("the factory demo tree contains a Task.Subtree node", subtreeId != null,
                subtreeId ?? "<none>");

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

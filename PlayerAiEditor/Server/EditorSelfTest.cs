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

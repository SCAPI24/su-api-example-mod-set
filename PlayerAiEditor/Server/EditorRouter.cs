using System;
using System.Collections.Generic;
using System.Text;

namespace PlayerAiMod.Editor
{
    /// <summary>HTTP 路由：静态资源（内嵌）+ JSON API。</summary>
    internal sealed class EditorRouter
    {
        private readonly EditorApi m_api;

        public EditorRouter(EditorApi api)
        {
            m_api = api;
        }

        public HttpResponse Handle(HttpRequestInfo request)
        {
            string path = (request.Path ?? "/").TrimEnd('/');
            if (path.Length == 0)
                path = "/";

            try
            {
                switch (path)
                {
                    case "/":
                        return Asset("index.html", "text/html; charset=utf-8");
                    case "/app.js":
                        return Asset("app.js", "application/javascript; charset=utf-8");
                    // 资源库 / Laya 配置面板（P6）：与 app.js 分开，改这两块不碰主界面状态机。
                    case "/assets-panel.js":
                        return Asset("assets-panel.js", "application/javascript; charset=utf-8");
                    // 判定复盘面板（P4）：与 app.js 分开，改它不碰主界面状态机。
                    case "/review-panel.js":
                        return Asset("review-panel.js", "application/javascript; charset=utf-8");
                    case "/app.css":
                        return Asset("app.css", "text/css; charset=utf-8");
                    case "/engine-adapter.js":
                        return Asset("engine-adapter.js", "application/javascript; charset=utf-8");
                    // 显示层翻译（节点显示名 + UI 文案）：只影响显示，不改包里的类型标识。
                    case "/i18n.js":
                        return Asset("i18n.js", "application/javascript; charset=utf-8");
                    // 真浏览器自检页（用同源 iframe 加载真正的 "/"，用真实鼠标事件驱动）。
                    // 只给 Mod/Packages/check_editor_browser.py 用，正常使用编辑器不需要它。
                    case "/selftest.html":
                        return Asset("selftest.html", "text/html; charset=utf-8");

                    case "/api/meta":
                        return HttpResponse.Json(m_api.Meta());
                    case "/api/schema":
                        return HttpResponse.Json(m_api.Schema());
                    case "/api/packages":
                        return HttpResponse.Json(m_api.ListPackages());
                    // 问题库清单（P3 第二步）：`Task.LayaAsk` 的 questions / answerKeys 下拉靠它
                    case "/api/banks":
                        return HttpResponse.Json(m_api.ListQuestionBanks());
                    case "/api/package":
                    {
                        string target = request.GetQuery("path", request.GetQuery("name"));
                        if (string.Equals(request.Method, "POST", StringComparison.Ordinal))
                        {
                            return HttpResponse.Json(Save(request, target));
                        }
                        return HttpResponse.Json(m_api.ReadPackage(target));
                    }
                    case "/api/validate":
                    {
                        // 正文优先（前端 POST {manifest, tree}），其次查询串，最后退化成"按路径校验磁盘上的包"
                        var payload = new EditorPayload();
                        ReadPayload(request, payload);
                        return HttpResponse.Json(m_api.ValidatePackage(payload.Path, payload.Manifest,
                            payload.Tree));
                    }
                    case "/api/notify":
                        return HttpResponse.Json(m_api.NotifyGame(
                            request.GetQuery("path", request.GetQuery("name"))));

                    // ------------------------------------------------ 嵌套包（Task.Subtree）
                    case "/api/subtree":
                        return HttpResponse.Json(m_api.ReadSubtree(
                            request.GetQuery("path", request.GetQuery("name")),
                            request.GetQuery("node", request.GetQuery("id"))));

                    // ------------------------------------------------ 实时监视（P3）
                    case "/api/game/live":
                        return HttpResponse.Json(m_api.LiveStatus());
                    case "/api/game/pause":
                        return HttpResponse.Json(m_api.SetPaused(true));
                    case "/api/game/resume":
                        return HttpResponse.Json(m_api.SetPaused(false));

                    // ------------------------------------------------ UI 定位 / 点击服务（UI-1）
                    // 用户要求："把相应的方法做成 CmdBridgeMod 能提供的服务，在行为树编辑器中，
                    // 要能够使用来获取坐标或点击对象" —— 拾取面板就是这三条端点的前端。
                    case "/api/game/ui/elements":
                    {
                        bool includeAll = string.Equals(request.GetQuery("all", "false"), "true",
                            StringComparison.OrdinalIgnoreCase);
                        int max = 200;
                        int.TryParse(request.GetQuery("max", "200"), out max);
                        return HttpResponse.Json(m_api.UiElements(includeAll, max));
                    }
                    case "/api/game/ui/locate":
                        return HttpResponse.Json(m_api.LocateUi(
                            request.GetQuery("target", request.GetQuery("selector")),
                            !string.Equals(request.GetQuery("mark", "true"), "false",
                                StringComparison.OrdinalIgnoreCase)));
                    case "/api/game/ui/click":
                    {
                        var body = ReadBody(request);
                        string target = request.GetQuery("target", request.GetQuery("selector"));
                        string mode = request.GetQuery("mode", "direct");
                        bool mark = !string.Equals(request.GetQuery("mark", "true"), "false",
                            StringComparison.OrdinalIgnoreCase);
                        if (body != null && body.Has("target"))
                            target = body.Get("target").AsString(target);
                        if (body != null && body.Has("mode"))
                            mode = body.Get("mode").AsString(mode);
                        if (body != null && body.Has("mark"))
                            mark = body.Get("mark").AsBool(mark);
                        return HttpResponse.Json(m_api.ClickUi(target, mode, mark));
                    }

                    // ------------------------------------------------ 启动 / 结束游戏
                    // 只判断"进程在不在 + 通道通不通"，不碰游戏状态
                    case "/api/game/process":
                        return HttpResponse.Json(m_api.GameProcessStatus());
                    case "/api/game/launch":
                        return HttpResponse.Json(m_api.LaunchGame());
                    case "/api/game/quit":
                        return HttpResponse.Json(m_api.QuitGame());
                    case "/api/game/tree/stop":
                        return HttpResponse.Json(m_api.StopGameTree());
                    case "/api/game/tree/switch":
                    {
                        var body = ReadBody(request);
                        string target = request.GetQuery("path", request.GetQuery("name"));
                        string entry = request.GetQuery("entry", null);
                        bool start = !string.Equals(request.GetQuery("start", "true"), "false",
                            StringComparison.OrdinalIgnoreCase);
                        if (body != null && body.Has("path"))
                            target = body.Get("path").AsString(target);
                        else if (body != null && body.Has("name"))
                            target = body.Get("name").AsString(target);
                        if (body != null && body.Has("entry"))
                            entry = body.Get("entry").AsString(entry);
                        if (body != null && body.Has("start"))
                            start = body.Get("start").AsBool(start);
                        return HttpResponse.Json(m_api.SwitchGameTree(target, entry, start));
                    }

                    // ------------------------------------------------ 动作包（P1）：当物料用
                    case "/api/actions":
                        return HttpResponse.Json(m_api.ListActions());
                    case "/api/action/validate":
                        return HttpResponse.Json(m_api.ValidateAction(
                            request.GetQuery("path", request.GetQuery("name"))));
                    case "/api/action/play":
                    {
                        var body = ReadBody(request);
                        string target = request.GetQuery("path", request.GetQuery("name"));
                        if (body != null && body.Has("path"))
                            target = body.Get("path").AsString(target);
                        else if (body != null && body.Has("name"))
                            target = body.Get("name").AsString(target);

                        int repeat = 1;
                        int.TryParse(request.GetQuery("repeat", "1"), out repeat);
                        if (body != null && body.Has("repeat"))
                            repeat = body.Get("repeat").AsInt(repeat);
                        return HttpResponse.Json(m_api.PlayAction(target, repeat));
                    }
                    case "/api/action/stop":
                        return HttpResponse.Json(m_api.StopAction());

                    // ------------------------------------------ 动作包编辑（右键菜单：编辑/重命名/删除）
                    case "/api/action/events":
                    {
                        string target = request.GetQuery("file", request.GetQuery("path",
                            request.GetQuery("name")));
                        if (string.Equals(request.Method, "POST", StringComparison.Ordinal))
                        {
                            var body = ReadBody(request);
                            if (body == null)
                            {
                                return HttpResponse.Json(new Dictionary<string, object>(
                                    StringComparer.Ordinal)
                                {
                                    ["ok"] = false,
                                    ["code"] = "invalid_argument",
                                    ["reason"] = "改动作包要用 JSON 正文：{file, events:[…]}"
                                });
                            }
                            if (body.Has("file"))
                                target = body.Get("file").AsString(target);
                            else if (body.Has("path"))
                                target = body.Get("path").AsString(target);
                            return HttpResponse.Json(m_api.SaveActionEvents(target,
                                body.Get("events")));
                        }
                        return HttpResponse.Json(m_api.ReadActionEvents(target));
                    }
                    case "/api/action/rename":
                    {
                        var body = ReadBody(request);
                        string target = request.GetQuery("file", request.GetQuery("path",
                            request.GetQuery("name")));
                        string wanted = request.GetQuery("to", request.GetQuery("newName"));
                        if (body != null)
                        {
                            if (body.Has("file"))
                                target = body.Get("file").AsString(target);
                            if (body.Has("to"))
                                wanted = body.Get("to").AsString(wanted);
                            else if (body.Has("name"))
                                wanted = body.Get("name").AsString(wanted);
                        }
                        return HttpResponse.Json(m_api.RenameAction(target, wanted));
                    }
                    case "/api/action/delete":
                    {
                        var body = ReadBody(request);
                        string target = request.GetQuery("file", request.GetQuery("path",
                            request.GetQuery("name")));
                        if (body != null && body.Has("file"))
                            target = body.Get("file").AsString(target);
                        return HttpResponse.Json(m_api.DeleteAction(target));
                    }
                    case "/api/action/create":
                    {
                        var body = ReadBody(request);
                        string name = request.GetQuery("name", request.GetQuery("path"));
                        if (body != null && body.Has("name"))
                            name = body.Get("name").AsString(name);
                        bool overwrite = string.Equals(request.GetQuery("overwrite", "false"), "true",
                            StringComparison.OrdinalIgnoreCase);
                        if (body != null && body.Has("overwrite"))
                            overwrite = body.Get("overwrite").AsBool(overwrite);
                        return HttpResponse.Json(m_api.CreateSampleAction(name, overwrite));
                    }
                    case "/api/game/status":
                        return HttpResponse.Json(m_api.GameStatus());

                    // -------------------------------------------- 资源库 / Laya 配置（P6）
                    //
                    // 三态视图与所有写操作**都转发给游戏**：编辑器不自己算"哪一版更新"，
                    // 否则界面说的和游戏实际用的会是两套判据。
                    case "/api/assets":
                        return HttpResponse.Json(m_api.Assets());
                    case "/api/asset":
                    {
                        // /api/asset?action=save&name=… 或 POST {action:"save", name:"…"}
                        var arguments = ReadJsonObject(request);
                        if (arguments == null)
                            arguments = new Dictionary<string, object>(StringComparer.Ordinal);

                        string action = request.GetQuery("action", request.GetQuery("cmd"));
                        if (!string.IsNullOrEmpty(action))
                            arguments["action"] = action;

                        object rawAction;
                        arguments.TryGetValue("action", out rawAction);
                        string resolved = rawAction != null ? Convert.ToString(rawAction) : null;
                        arguments.Remove("action");
                        return HttpResponse.Json(m_api.AssetCommand(resolved, arguments));
                    }
                    case "/api/laya/config":
                    {
                        if (string.Equals(request.Method, "POST", StringComparison.Ordinal))
                            return HttpResponse.Json(m_api.SaveLayaConfig(ReadJsonObject(request)));
                        return HttpResponse.Json(m_api.ReadLayaConfig());
                    }
                    // 判定复盘（P4）：`ai.laya.review` 的转发。`count` 条明细，`format=md` 拿抽样表，
                    // `clear=true` 先清空再回答（界面上的"清空记录"按钮）。
                    case "/api/laya/review":
                    {
                        int count = 10;
                        int.TryParse(request.GetQuery("count", "10"), out count);
                        bool clear = string.Equals(request.GetQuery("clear", "false"), "true",
                            StringComparison.OrdinalIgnoreCase);
                        return HttpResponse.Json(m_api.LayaReview(count, request.GetQuery("format", "json"),
                            clear));
                    }
                    default:
                        return HttpResponse.Text(404, "not found: " + path);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("[editor] " + path + " failed: " + exception.GetType().Name + ": "
                    + exception.Message);
                return HttpResponse.Json(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = false,
                    ["reason"] = exception.GetType().Name + ": " + exception.Message
                }, 500);
            }
        }

        /// <summary>
        /// 把正文 JSON 读成一个**普通字典**（端点参数透传用）。空正文 / 非对象一律给 null。
        ///
        /// 为什么不用 `EditorPayload`：那个是给"包数据"（manifest/tree/path/overwrite）用的固定形状；
        /// 资源库那族命令的参数是**开放**的（`dir` / `mode` / `newName` / `maxAttempts` …），
        /// 逐个字段解析只会把自己锁死。
        /// </summary>
        private static Dictionary<string, object> ReadJsonObject(HttpRequestInfo request)
        {
            PackageValue value = ReadBody(request);
            if (value == null || !value.IsObject)
                return null;
            return ToDictionary(value);
        }

        private static Dictionary<string, object> ToDictionary(PackageValue value)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (value == null || !value.IsObject)
                return result;

            foreach (string name in value.MemberNames)
            {
                PackageValue item = value.Get(name);
                if (item == null)
                {
                    result[name] = null;
                }
                else if (item.IsObject)
                {
                    result[name] = ToDictionary(item);
                }
                else if (item.IsArray)
                {
                    var list = new List<object>();
                    for (int i = 0; i < item.Count; i++)
                    {
                        PackageValue element = item.Item(i);
                        list.Add(element != null && element.IsObject
                            ? (object)ToDictionary(element) : ValueOf(element));
                    }
                    result[name] = list;
                }
                else
                {
                    result[name] = ValueOf(item);
                }
            }
            return result;
        }

        private static object ValueOf(PackageValue value)
        {
            if (value == null || value.IsNull)
                return null;
            if (value.IsBool)
                return value.AsBool();
            if (value.IsNumber)
            {
                double number = value.AsNumber();
                return Math.Abs(number - Math.Round(number)) < 1e-9 ? (object)(int)Math.Round(number) : number;
            }
            return value.AsString(string.Empty);
        }

        /// <summary>
        /// 把正文按 JSON 解析（空正文 / 非 JSON 一律返回 null，调用方退回查询串参数）。
        /// 动作包那几个端点用它读 `{path, name, repeat, overwrite}`。
        /// </summary>
        private static PackageValue ReadBody(HttpRequestInfo request)
        {
            string body = request.BodyText;
            if (string.IsNullOrEmpty(body) || !body.TrimStart().StartsWith("{", StringComparison.Ordinal))
                return null;

            var report = new PackageReport();
            PackageValue value;
            return PackageJson.TryParse(body, "request", report, out value) ? value : null;
        }

        /// <summary>一次请求携带的包数据（正文或查询串都能给）。</summary>
        private sealed class EditorPayload
        {
            public string Path;
            public string Manifest;
            public string Tree;
            public bool Overwrite;

            /// <summary>解析失败时的原因（null = 没问题）。</summary>
            public string Error;
        }

        /// <summary>
        /// 统一解析包数据：正文优先 `{manifest, tree, path, overwrite}` JSON，
        /// 其次 `application/x-www-form-urlencoded`，最后退回查询串。
        /// `/api/package`（保存）与 `/api/validate`（校验）共用同一套解析，避免两边行为不一致。
        /// </summary>
        private static void ReadPayload(HttpRequestInfo request, EditorPayload payload)
        {
            payload.Path = request.GetQuery("path", request.GetQuery("name"));
            payload.Overwrite = string.Equals(request.GetQuery("overwrite", "false"), "true",
                StringComparison.OrdinalIgnoreCase);
            payload.Manifest = request.GetQuery("manifest");
            payload.Tree = request.GetQuery("tree");

            string body = request.BodyText;
            if (string.IsNullOrEmpty(body))
                return;

            if (body.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var report = new PackageReport();
                PackageValue value;
                if (!PackageJson.TryParse(body, "request", report, out value))
                {
                    payload.Error = "请求正文不是合法 JSON：" + report.Summary();
                    return;
                }

                if (value.Has("manifest"))
                    payload.Manifest = value.Get("manifest").ToJson(false);
                if (value.Has("tree"))
                    payload.Tree = value.Get("tree").ToJson(false);
                if (value.Has("path"))
                    payload.Path = value.Get("path").AsString(payload.Path);
                if (value.Has("overwrite"))
                    payload.Overwrite = value.Get("overwrite").AsBool(payload.Overwrite);
                return;
            }

            foreach (string pair in body.Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0)
                    continue;
                string name = Uri.UnescapeDataString(pair.Substring(0, equals));
                string value = Uri.UnescapeDataString(pair.Substring(equals + 1).Replace('+', ' '));
                if (string.Equals(name, "manifest", StringComparison.OrdinalIgnoreCase))
                    payload.Manifest = value;
                else if (string.Equals(name, "tree", StringComparison.OrdinalIgnoreCase))
                    payload.Tree = value;
                else if (string.Equals(name, "path", StringComparison.OrdinalIgnoreCase))
                    payload.Path = value;
                else if (string.Equals(name, "overwrite", StringComparison.OrdinalIgnoreCase))
                    payload.Overwrite = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        private Dictionary<string, object> Save(HttpRequestInfo request, string target)
        {
            var payload = new EditorPayload();
            ReadPayload(request, payload);
            if (!string.IsNullOrEmpty(payload.Error))
                return Failed(payload.Error);

            string path = string.IsNullOrEmpty(payload.Path) ? target : payload.Path;
            if (string.IsNullOrEmpty(payload.Manifest) || string.IsNullOrEmpty(payload.Tree))
                return Failed("缺少 manifest 或 tree");

            return m_api.SavePackage(path, payload.Manifest, payload.Tree, payload.Overwrite);
        }

        private static Dictionary<string, object> Failed(string reason)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = false,
                ["reason"] = reason
            };
        }

        private static HttpResponse Asset(string name, string contentType)
        {
            string content = Program.ReadEmbeddedAsset(name);
            if (content == null)
                return HttpResponse.Text(500, "embedded asset missing: " + name);

            // 页面里带一个**构建时间戳**：用户能一眼看出自己跑的是哪一版，
            // 我们也就不会再陷在"改了但你看的还是旧的"这种扯不清的循环里。
            if (content.IndexOf(BuildStampPlaceholder, StringComparison.Ordinal) >= 0)
                content = content.Replace(BuildStampPlaceholder, Program.BuildStamp);

            return new HttpResponse
            {
                Status = 200,
                ContentType = contentType,
                Body = Encoding.UTF8.GetBytes(content)
            };
        }

        private const string BuildStampPlaceholder = "<!--BUILDSTAMP-->";
    }
}

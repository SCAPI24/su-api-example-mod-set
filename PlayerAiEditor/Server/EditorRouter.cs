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
                    case "/app.css":
                        return Asset("app.css", "text/css; charset=utf-8");
                    case "/engine-adapter.js":
                        return Asset("engine-adapter.js", "application/javascript; charset=utf-8");
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
                    {
                        try
                        {
                            return HttpResponse.Json(new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["ok"] = true,
                                ["status"] = m_api.Game.QueryStatus()
                            });
                        }
                        catch (Exception exception)
                        {
                            return HttpResponse.Json(new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["ok"] = false,
                                ["reason"] = exception.Message
                            });
                        }
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

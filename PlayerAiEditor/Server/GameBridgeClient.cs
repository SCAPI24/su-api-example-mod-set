using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace PlayerAiMod.Editor
{
    /// <summary>
    /// 游戏**拒绝**了一条命令（`{"ok":false,"error":{code,message}}`）。
    ///
    /// 为什么要单独一个异常类型：拒绝的原因差别很大 —— `not_ready`（游戏在跑，但还没进世界 /
    /// AI 没接管）和"这条命令游戏里根本没有"（Mod 太旧）完全是两件事，混成一句
    /// "多半是 Mod 没有这个命令"会把用户指到错的方向（实测就踩到了：实时监视第一次点开时
    /// 世界还没加载，界面却让人去重新部署 Mod）。
    /// </summary>
    internal sealed class GameCommandException : Exception
    {
        public GameCommandException(string code, string message)
            : base("game refused the command: " + code + ": " + message)
        {
            Code = code ?? "unknown";
            GameMessage = message ?? string.Empty;
        }

        /// <summary>游戏给的稳定错误码（`not_ready` / `file_missing` / `unknown_command` …）。</summary>
        public string Code { get; }

        /// <summary>游戏给的原始说明（英文，通常已经写清缺什么）。</summary>
        public string GameMessage { get; }
    }

    /// <summary>
    /// 与**正在运行的游戏**通信（CmdBridge 控制通道）。
    ///
    /// 用途：编辑器保存包之后通知游戏热重载（`ai.tree.notify {path, hash}`）——
    /// 这就是"改文件 → 立刻生效"那条链的最后一跳：不重启、不重载世界。
    ///
    /// 端口与 token 从 `<实例根>/CmdBridge.runtime.json` 读（游戏启动时写的），
    /// 协议与 sccmd 完全一致：一行 JSON 请求 → 一行 JSON 响应。
    /// </summary>
    internal sealed class GameBridgeClient
    {
        public const string RuntimeFileName = "CmdBridge.runtime.json";

        private readonly string m_instanceRoot;
        private int m_counter;

        public GameBridgeClient(string instanceRoot)
        {
            m_instanceRoot = instanceRoot;
        }

        /// <summary>游戏是否在跑（能读到 runtime 文件且端口可连）。</summary>
        public bool TryDescribe(out Dictionary<string, object> info, out string error)
        {
            info = null;
            error = null;

            string path = System.IO.Path.Combine(m_instanceRoot ?? string.Empty, RuntimeFileName);
            if (!File.Exists(path))
            {
                error = "the game is not running (no " + RuntimeFileName + " yet)";
                return false;
            }

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                error = "cannot read " + RuntimeFileName + ": " + exception.Message;
                return false;
            }

            PackageValue value;
            var report = new PackageReport();
            if (!PackageJson.TryParse(text, RuntimeFileName, report, out value))
            {
                error = "invalid " + RuntimeFileName + ": " + report.Summary();
                return false;
            }

            int port = value.Get("port").AsInt(0);
            string token = value.Get("token").AsString(null);
            if (port <= 0 || string.IsNullOrEmpty(token))
            {
                error = RuntimeFileName + " has no usable port/token";
                return false;
            }

            try
            {
                var result = Send(value, "ping", null);
                info = result;
                return true;
            }
            catch (Exception exception)
            {
                error = "the game did not answer on port " + port + ": " + exception.Message;
                return false;
            }
        }

        /// <summary>通知游戏热重载某个包（`ai.tree.notify`）。返回游戏的控制面回包。</summary>
        public Dictionary<string, object> NotifyTree(string path, string hash)
        {
            PackageValue runtime = ReadRuntime();
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["hash"] = hash
            };
            return Send(runtime, "ai.tree.notify", arguments);
        }

        /// <summary>编辑器的"游戏状态"面板：直接问游戏 `ai.status`。</summary>
        public Dictionary<string, object> QueryStatus()
        {
            return Send(ReadRuntime(), "ai.status", null);
        }

        /// <summary>活动树的运行快照（`ai.tree.snapshot`）：活动节点路径、tick、上次结果。</summary>
        public Dictionary<string, object> QueryTreeSnapshot()
        {
            return Send(ReadRuntime(), "ai.tree.snapshot", null);
        }

        /// <summary>黑板全部键值（`ai.blackboard all=true`）——实时监视面板用。</summary>
        public Dictionary<string, object> QueryBlackboard()
        {
            return Send(ReadRuntime(), "ai.blackboard",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["all"] = true });
        }

        /// <summary>暂停/继续行为树（`ai.pause` / `ai.resume`）。</summary>
        public Dictionary<string, object> SetPaused(bool paused)
        {
            return Send(ReadRuntime(), paused ? "ai.pause" : "ai.resume", null);
        }

        /// <summary>切到某个包并开始跑（`ai.tree.switch`；参数含绝对路径与 start）。</summary>
        public Dictionary<string, object> SwitchTree(Dictionary<string, object> arguments)
        {
            return Send(ReadRuntime(), "ai.tree.switch", arguments);
        }

        /// <summary>停止并卸下当前树（`ai.tree.stop`）—— 之后重新 switch 就是从根开始。</summary>
        public Dictionary<string, object> StopTree()
        {
            return Send(ReadRuntime(), "ai.tree.stop", null);
        }

        /// <summary>让游戏校验一次包（与游戏内用的是同一份校验器）。</summary>
        public Dictionary<string, object> ValidateInGame(string nameOrPath)
        {
            return Send(ReadRuntime(), "ai.tree.validate",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["name"] = nameOrPath });
        }

        /// <summary>让游戏直接回放一个动作包（`ai.action.play`；传绝对路径最稳）。</summary>
        public Dictionary<string, object> PlayAction(string path, int repeat)
        {
            return Send(ReadRuntime(), "ai.action.play", new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["repeat"] = repeat
            });
        }

        /// <summary>让游戏停止回放并释放输入（`ai.action.stop`）。</summary>
        public Dictionary<string, object> StopAction()
        {
            return Send(ReadRuntime(), "ai.action.stop", null);
        }

        // ---------------------------------------------------------------- UI 定位 / 点击服务

        /// <summary>
        /// 当前屏幕上可交互的 UI 元素（`obs.ui`）：带**真实坐标**（客户区像素）与可点性判定。
        /// 编辑器的"UI 拾取"面板用它 —— 用户要的就是"别硬编码坐标"。
        /// </summary>
        public Dictionary<string, object> QueryUiElements(bool includeAll, int maxElements)
        {
            return Send(ReadRuntime(), "obs.ui", new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["includeAll"] = includeAll,
                ["maxElements"] = maxElements
            });
        }

        /// <summary>
        /// 解析一个**语义目标**（`Play` / 路径 / `list:WorldsList@世界名`）并给出当前坐标。
        /// 走 CmdBridge 的 UI 服务（`ui.locate`），与行为树/回放用的是同一份实现。
        /// `mark` 打开时游戏里会在那个像素上亮一个红点（默认 2 秒 / 5 像素）—— 人眼定位用。
        /// </summary>
        public Dictionary<string, object> LocateUi(string target, bool mark)
        {
            return Send(ReadRuntime(), "ui.locate", new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["target"] = target,
                ["mark"] = mark
            });
        }

        /// <summary>
        /// 真的点一下（`ui.clickelement`）：`mode` = `direct`（默认，单帧合成按下）/
        /// `input`（多帧软光标会话）/ `invoke`（直接触发控件事件）/ `auto`（能发事件就发事件）。
        /// `mark` 打开时在点的那个像素上亮红点。
        /// </summary>
        public Dictionary<string, object> ClickUi(string target, string mode, bool mark)
        {
            return Send(ReadRuntime(), "ui.clickelement", new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["target"] = target,
                ["mode"] = string.IsNullOrEmpty(mode) ? "direct" : mode,
                ["mark"] = mark
            });
        }

        private PackageValue ReadRuntime()
        {
            string path = System.IO.Path.Combine(m_instanceRoot ?? string.Empty, RuntimeFileName);
            if (!File.Exists(path))
                throw new InvalidOperationException("the game is not running (" + RuntimeFileName
                    + " is missing)");

            var report = new PackageReport();
            PackageValue value;
            if (!PackageJson.TryParse(File.ReadAllText(path, Encoding.UTF8), RuntimeFileName,
                report, out value))
            {
                throw new InvalidOperationException("invalid " + RuntimeFileName + ": "
                    + report.Summary());
            }
            return value;
        }

        private Dictionary<string, object> Send(PackageValue runtime, string command,
            Dictionary<string, object> arguments)
        {
            int port = runtime.Get("port").AsInt(0);
            string token = runtime.Get("token").AsString(null);
            if (port <= 0 || string.IsNullOrEmpty(token))
                throw new InvalidOperationException("runtime info has no port/token");

            string request = BuildRequest(command, token, arguments);

            using (var client = new TcpClient())
            {
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 20000;
                client.Connect("127.0.0.1", port);

                using (NetworkStream stream = client.GetStream())
                {
                    byte[] payload = Encoding.UTF8.GetBytes(request + "\n");
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();

                    string line = ReadLine(stream);
                    if (line == null)
                        throw new InvalidOperationException("the game closed the connection");

                    var report = new PackageReport();
                    PackageValue response;
                    if (!PackageJson.TryParse(line, "response", report, out response))
                        throw new InvalidOperationException("invalid response: " + report.Summary());

                    if (!response.Get("ok").AsBool(false))
                    {
                        PackageValue error = response.Get("error");
                        throw new GameCommandException(error.Get("code").AsString("unknown"),
                            error.Get("message").AsString(string.Empty));
                    }

                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    PackageValue payloadValue = response.Get("result");
                    Collect(payloadValue, result);
                    return result;
                }
            }
        }

        private static void Collect(PackageValue value, Dictionary<string, object> into)
        {
            if (value == null || !value.IsObject)
                return;

            foreach (string name in value.MemberNames)
            {
                PackageValue member = value.Get(name);
                if (member.IsObject)
                {
                    var nested = new Dictionary<string, object>(StringComparer.Ordinal);
                    Collect(member, nested);
                    into[name] = nested;
                }
                else if (member.IsArray)
                {
                    into[name] = member.ToJson(false);
                }
                else if (member.IsBool)
                {
                    into[name] = member.AsBool();
                }
                else if (member.IsNumber)
                {
                    into[name] = member.AsNumber();
                }
                else if (member.IsString)
                {
                    into[name] = member.AsString();
                }
                else
                {
                    into[name] = null;
                }
            }
        }

        private string BuildRequest(string command, string token,
            Dictionary<string, object> arguments)
        {
            m_counter++;
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = m_counter.ToString(),
                ["token"] = token,
                ["command"] = command
            };
            if (arguments != null && arguments.Count > 0)
                payload["args"] = arguments;
            return JsonWriter.Write(payload, false);
        }

        private static string ReadLine(NetworkStream stream)
        {
            var buffer = new List<byte>(256);
            var one = new byte[1];
            while (buffer.Count < 4 * 1024 * 1024)
            {
                int read = stream.Read(one, 0, 1);
                if (read <= 0)
                    return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
                if (one[0] == (byte)'\n')
                    return Encoding.UTF8.GetString(buffer.ToArray());
                buffer.Add(one[0]);
            }
            throw new InvalidDataException("response line too long");
        }
    }
}

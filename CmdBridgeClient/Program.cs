using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CmdBridgeClient
{
    /// <summary>
    /// sccmd —— 通过 CmdBridge 观察/操作 Survivalcraft 玩家控制器。
    ///
    /// 允许：无限读取游戏数据；注入玩家级输入（视角/键盘/鼠标/UI 点击）。
    /// 禁止：任何直接修改游戏状态的通道（本工具不提供，Mod 也不提供）。
    /// </summary>
    internal static class Program
    {
        private static bool s_json;
        private static string s_root;
        private static int? s_port;
        private static string s_token;
        private static int s_timeoutMs = 20000;

        /// <summary>
        /// `key` 子命令不传 holdMs 时下发的按下时长。
        /// 必须与服务端 `CmdBridgeMod/Server/CommandRouter.cs` 的 `DefaultKeyPulseHoldMs` 一致：
        /// 0ms 会让"按下"与"抬起"落在同一次 `Dispatcher.BeforeFrame` 批次里，帧体看不到按下
        /// （`Engine/Engine/Dispatcher.cs:79-105`）—— 这是"按键第一次无效、第二次才生效"的第二个根因。
        /// </summary>
        private const int DefaultKeyPulseHoldMs = 40;

        private static int Main(string[] args)
        {
            try
            {
                List<string> rest = ParseGlobalOptions(args);
                if (rest.Count == 0)
                    return RunRepl();

                return RunOnce(rest);
            }
            catch (BridgeException exception)
            {
                Console.Error.WriteLine("error[" + exception.Code + "]: " + exception.Message);
                return ExitCodeFor(exception.Code);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("error: " + exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        // ---------------------------------------------------------------- 参数

        private static List<string> ParseGlobalOptions(string[] args)
        {
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--json":
                        s_json = true;
                        break;
                    case "--root":
                        s_root = Next(args, ref i);
                        break;
                    case "--port":
                        s_port = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "--token":
                        s_token = Next(args, ref i);
                        break;
                    case "--timeout":
                        s_timeoutMs = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    default:
                        rest.Add(arg);
                        break;
                }
            }
            return rest;
        }

        private static string Next(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
                throw new BridgeException("invalid_argument", args[index] + " requires a value.");
            index++;
            return args[index];
        }

        // ---------------------------------------------------------------- 交互式

        private static int RunRepl()
        {
            Console.WriteLine("sccmd — CmdBridge console (type 'help' for commands, 'quit' to exit)");
            while (true)
            {
                Console.Write("sc> ");
                string line = Console.ReadLine();
                if (line == null)
                    break;
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                if (line == "quit" || line == "exit")
                    break;

                List<string> parts = SplitCommandLine(line);
                try
                {
                    RunOnce(parts);
                }
                catch (BridgeException exception)
                {
                    Console.Error.WriteLine("error[" + exception.Code + "]: " + exception.Message);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("error: " + exception.Message);
                }
            }
            return 0;
        }

        private static List<string> SplitCommandLine(string line)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char character = line[i];
                if (character == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (!inQuotes && char.IsWhiteSpace(character))
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
                current.Append(character);
            }
            if (current.Length > 0)
                parts.Add(current.ToString());
            return parts;
        }

        // ---------------------------------------------------------------- 单次执行

        private static int RunOnce(List<string> args)
        {
            string command = args[0].ToLowerInvariant();
            if (command == "help")
            {
                // help 不需要连上游戏。
                PrintHelp();
                return 0;
            }

            using (BridgeClient client = BridgeClient.Connect(s_root, s_port, s_token))
            {
                switch (command)
                {
                    case "status":
                        return Print(client, "status", null);
                    case "snapshot":
                        return Print(client, "obs.snapshot", Args(
                            ("maxElements", 256)));
                    case "ui":
                        return PrintUi(client, args);
                    case "maps":
                    case "worlds":
                        return MapsCommand(client, args);
                    case "reachability":
                        return Print(client, "ui.reachability", null);
                    case "player":
                        return Print(client, "obs.player", null);
                    // 每一个 PlayerData（主机分屏 + 联机远端 avatar），服务端命令 players.all。
                    case "players":
                    case "players.all":
                        return PlayersCommand(client, args);
                    case "input":
                        return Print(client, "obs.input", null);
                    case "aim":
                        return Print(client, "obs.aim", Args(("maxDistance", 8f)));
                    // 关掉挡着世界输入的界面（对话框 → 引擎自己的 Dismiss 路径；背包/创造背包等
                    // 模态面板 → ComponentGui.ModalPanelWidget = null；HUD MoreContents → 收起）。
                    // 什么都没挡时服务端报 `ui_not_blocking`，不会返回"成功但什么都没做"。
                    case "closeui":
                    case "act.closeui":
                        return CloseUiCommand(client, args);
                    case "events":
                        return Print(client, "obs.events", Args(
                            ("sinceSeq", args.Count >= 2 ? ParseInt(args[1]) : 0),
                            ("max", 200)));
                    case "world":
                        return WorldCommand(client, args);
                    case "waitfor":
                        return WaitForCommand(client, args);
                    case "messages":
                        return Print(client, "obs.messages", null);
                    case "dialogs":
                        return Print(client, "obs.dialogs", null);
                    case "focusrecover":
                        return Print(client, "focus.recover", null);
                    case "focusstatus":
                        return Print(client, "focus.status", null);
                    case "selftest":
                        return Print(client, "obs.selftest", null);
                    case "aiselftest":
                        // 三套自检（行为树内核 + 包/热重载 + 命令层）
                        return Print(client, "bt.selftest", null);
                    case "ai":
                        return AiCommand(client, args);
                    case "commands":
                        return Print(client, "cmd.list", null);

                    // 进世界：循环驱动服务端 `act.enter`（主菜单 → 世界列表 → 创建/加载 → 角色界面 → 游戏）
                    case "enter":
                    case "enterworld":
                        return EnterWorld(client, args);

                    case "look":
                        Require(args.Count >= 3, "look <yawDeg> <pitchDeg>");
                        return Print(client, "act.look", Args(
                            ("yaw", ParseFloat(args[1])), ("pitch", ParseFloat(args[2]))));
                    case "lookdelta":
                        Require(args.Count >= 3, "lookdelta <dYawDeg> <dPitchDeg>");
                        return Print(client, "act.lookdelta", Args(
                            ("dx", ParseFloat(args[1])), ("dy", ParseFloat(args[2]))));
                    case "lookat":
                        Require(args.Count >= 4, "lookat <x> <y> <z>");
                        return Print(client, "act.lookat", Args(
                            ("x", ParseFloat(args[1])),
                            ("y", ParseFloat(args[2])),
                            ("z", ParseFloat(args[3]))));
                    case "key":
                        Require(args.Count >= 2, "key <name> [holdMs]");
                        // 不传 holdMs 时用服务端同一个"一帧量级"默认值（40ms），而不是 0：
                        // 0 会让按下与抬起落在同一次 Dispatcher.BeforeFrame 批次里，帧体看不到按下。
                        return Print(client, "act.key", Args(
                            ("key", args[1]),
                            ("holdMs", args.Count >= 3 ? ParseInt(args[2]) : DefaultKeyPulseHoldMs)));
                    case "hold":
                        Require(args.Count >= 2, "hold <name>");
                        return Print(client, "act.hold", Args(("key", args[1]), ("down", true)));
                    case "release":
                        if (args.Count >= 2 && args[1] == "--all")
                            return Print(client, "act.releaseAll", null);
                        Require(args.Count >= 2, "release <name> | --all");
                        return Print(client, "act.hold", Args(("key", args[1]), ("down", false)));
                    case "chord":
                        Require(args.Count >= 3, "chord <modifier...> <key>");
                        return Print(client, "act.chord", Args(
                            ("modifiers", args.GetRange(1, args.Count - 2).ToArray()),
                            ("key", args[args.Count - 1])));
                    case "mouse":
                        Require(args.Count >= 2, "mouse <left|right|middle> [down|up|click|doubleclick] [holdMs]");
                        return Print(client, "act.mouse", Args(
                            ("button", args[1]),
                            ("action", args.Count >= 3 ? args[2] : "click"),
                            ("holdMs", args.Count >= 4 ? ParseInt(args[3]) : 0)));
                    case "wheel":
                        Require(args.Count >= 2, "wheel <notches>");
                        return Print(client, "act.wheel", Args(("delta", ParseInt(args[1]))));
                    // 玩家交互输入：引擎里"使用 / 交互 / 放置"是同一个右键边沿
                    // （`PlayerInput.Interact`，ComponentPlayer 按 Use → Interact → Place 消费），
                    // "瞄准"是同一个右键按住（`PlayerInput.Aim`）。命令名保留语义，回包里的
                    // `predictedBranch` 与 `cells.changed` 才是"引擎实际走了哪一步"的读据。
                    case "act.use":
                    case "use":
                        return InteractionCommand(client, args, "use");
                    case "act.interact":
                    case "interact":
                        return InteractionCommand(client, args, "interact");
                    case "act.place":
                    case "place":
                        return InteractionCommand(client, args, "place");
                    case "act.aim":
                        return AimCommand(client, args);
                    case "click":
                        return ClickCommand(client, args);
                    case "text":
                        Require(args.Count >= 2, "text <string>");
                        return Print(client, "act.text", Args(("text", args[1])));
                    case "settext":
                        Require(args.Count >= 3, "settext <selector> <text>");
                        return Print(client, "act.settext",
                            Args(("selector", args[1]), ("text", args[2])));
                    case "accept":
                        return AcceptCommand(client, args, "act.accept");
                    case "dialog":
                        return DialogCommand(client, args);
                    // SuMultiplayer 聊天注入：与手打走同一条 ChatModule 发送链。
                    // 文本按剩余参数拼接，所以 `chat.send hello world` 与引号写法等价；
                    // CJK 原样经 UTF-8 JSON 传输。
                    case "chat.open":
                        return Print(client, "chat.open", null);
                    case "chat.text":
                        Require(args.Count >= 2, "chat.text <text>");
                        return Print(client, "chat.text", Args(("text", JoinText(args, 1))));
                    case "chat.submit":
                        return Print(client, "chat.submit", null);
                    case "chat.send":
                        Require(args.Count >= 2, "chat.send <text>");
                        return Print(client, "chat.send", Args(("text", JoinText(args, 1))));
                    case "chat.cancel":
                        return Print(client, "chat.cancel", null);
                    case "raw":
                        Require(args.Count >= 2, "raw <command> [key=value ...]");
                        return RunRaw(client, args);
                    default:
                        Console.Error.WriteLine("Unknown command '" + command + "'. Try 'help'.");
                        return 1;
                }
            }
        }

        /// <summary>
        /// AI 控制面：`sccmd ai <子命令>`。全部落到 PlayerAiMod 注册的 `ai.*` 命令上
        /// （命令由 CmdBridgeMod 的扩展注册表分发，客户端不需要知道是谁实现的）。
        /// </summary>
        private static int AiCommand(BridgeClient client, List<string> args)
        {
            Require(args.Count >= 2, "ai status|pause|resume|enable|disable|trees|load|reload|notify|validate|bb|release");

            string sub = args[1].ToLowerInvariant();
            switch (sub)
            {
                case "status":
                    return Print(client, "ai.status", null);
                case "pause":
                    return Print(client, "ai.pause", null);
                case "resume":
                    return Print(client, "ai.resume", null);
                case "enable":
                    return Print(client, "ai.enable", null);
                case "disable":
                    return Print(client, "ai.disable", null);
                case "release":
                    return Print(client, "ai.input.release", null);
                case "trees":
                    return Print(client, "ai.tree.list", null);
                case "load":
                    Require(args.Count >= 3, "ai load <包名>");
                    return Print(client, "ai.tree.load", Args(
                        ("name", args[2]),
                        ("start", true)));
                case "reload":
                    return Print(client, "ai.tree.reload",
                        args.Count >= 3 ? Args(("name", args[2])) : null);
                case "validate":
                    return Print(client, "ai.tree.validate",
                        args.Count >= 3 ? Args(("name", args[2])) : null);
                case "prepare":
                    if (args.Count >= 3 && args[2] == "--all")
                        return Print(client, "ai.tree.prepare", Args(("all", true)));
                    Require(args.Count >= 3, "ai prepare <包名> | ai prepare --all");
                    return Print(client, "ai.tree.prepare", Args(("name", args[2])));
                case "switch":
                    Require(args.Count >= 3, "ai switch <包名>");
                    return Print(client, "ai.tree.switch", Args(("name", args[2])));
                case "notify":
                    Require(args.Count >= 3, "ai notify <路径> [hash]");
                    return Print(client, "ai.tree.notify", Args(
                        ("path", args[2]),
                        ("hash", args.Count >= 4 ? args[3] : null)));
                case "set":
                    Require(args.Count >= 5, "ai set <节点id> <参数名> <值> [类型]");
                    return Print(client, "ai.edit.set", Args(
                        ("id", args[2]),
                        ("name", args[3]),
                        ("value", args[4]),
                        ("type", args.Count >= 6 ? args[5] : "float")));
                case "insert":
                    Require(args.Count >= 4, "ai insert <父节点id> <节点定义JSON>");
                    return Print(client, "ai.edit.insert", Args(
                        ("parent", args[2]),
                        ("json", args[3])));
                case "remove":
                    Require(args.Count >= 3, "ai remove <节点id>");
                    return Print(client, "ai.edit.remove", Args(("id", args[2])));
                case "move":
                    Require(args.Count >= 4, "ai move <节点id> <新父节点id> [下标]");
                    return Print(client, "ai.edit.move", Args(
                        ("id", args[2]),
                        ("parent", args[3]),
                        ("index", args.Count >= 5 ? ParseInt(args[4]) : -1)));
                case "snapshot":
                    return Print(client, "ai.tree.snapshot", null);
                case "logs":
                    return Print(client, "ai.logs", Args(
                        ("count", args.Count >= 3 ? ParseInt(args[2]) : 20)));
                case "export":
                    Require(args.Count >= 3, "ai export <新包名>");
                    return Print(client, "ai.tree.export", Args(("name", args[2])));
                case "record":
                    return Print(client, RecordSubcommand(args), RecordArgs(args));
                case "bb":
                    if (args.Count == 2)
                        return Print(client, "ai.blackboard", Args(("all", true)));
                    if (args.Count == 3)
                        return Print(client, "ai.blackboard", Args(("key", args[2])));
                    return Print(client, "ai.blackboard", Args(
                        ("key", args[2]),
                        ("value", args[3]),
                        ("type", args.Count >= 5 ? args[4] : "string")));
                default:
                    Console.Error.WriteLine("Unknown ai subcommand '" + sub + "'.");
                    return 1;
            }
        }

        /// <summary>`ai record <start|pause|stop|save|discard> [名字]` → 对应的 ai.record.* 命令。</summary>
        private static string RecordSubcommand(List<string> args)
        {
            Require(args.Count >= 3,
                "ai record start|pause|stop|save|discard [名字]");
            switch (args[2].ToLowerInvariant())
            {
                case "start": return "ai.record.start";
                case "pause":
                case "resume": return "ai.record.pause";
                case "stop": return "ai.record.stop";
                case "save": return "ai.record.save";
                case "discard": return "ai.record.discard";
                default:
                    throw new InvalidOperationException(
                        "Unknown ai record subcommand '" + args[2] + "'.");
            }
        }

        private static Dictionary<string, object> RecordArgs(List<string> args)
        {
            string sub = args[2].ToLowerInvariant();
            string name = args.Count >= 4 ? args[3] : null;

            if (sub == "save" && string.IsNullOrEmpty(name))
                Require(false, "ai record save <名字> [--overwrite]");

            var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(name) && (sub == "start" || sub == "stop" || sub == "save"))
                arguments["name"] = name;
            if (sub == "save" && args.Contains("--overwrite"))
                arguments["overwrite"] = true;
            return arguments;
        }

        private static int RunRaw(BridgeClient client, List<string> args)
        {
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 2; i < args.Count; i++)
            {
                int equals = args[i].IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = args[i].Substring(0, equals);
                string value = args[i].Substring(equals + 1);
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                    arguments[key] = intValue;
                else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
                    arguments[key] = floatValue;
                else if (bool.TryParse(value, out bool boolValue))
                    arguments[key] = boolValue;
                else
                    arguments[key] = value;
            }
            return Print(client, args[1], arguments);
        }

        // ---------------------------------------------------------------- 输出

        private static int Print(BridgeClient client, string command, Dictionary<string, object> arguments)
        {
            JsonElement result = client.Send(command, arguments, s_timeoutMs);
            if (s_json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
                return 0;
            }
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            return 0;
        }

        /// <summary>
        /// `sccmd closeui [--cancel|--accept] [--button 1|2]` → 服务端 `act.closeui`。
        ///
        /// 关掉挡着世界输入的界面，并打印服务端回报的 `dismissedUi`（关掉了什么）。
        /// `--cancel` 只对 `MessageDialog` 有意义：走引擎的 Cancel 分支（第二个按钮 / 没有就第一个），
        /// 用于"Are you sure?"这类确认框的**拒绝**；默认 `--accept` 走引擎的 OK 分支。
        /// </summary>
        private static int CloseUiCommand(BridgeClient client, List<string> args)
        {
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == "--cancel" || args[i] == "--reject")
                    arguments["button"] = 2;
                else if (args[i] == "--accept" || args[i] == "--ok")
                    arguments["button"] = 1;
                else if (args[i] == "--button" && i + 1 < args.Count)
                    arguments["button"] = ParseInt(args[++i]);
                else
                    throw new BridgeException("invalid_argument",
                        "usage: closeui [--cancel|--accept] [--button 1|2]");
            }
            return Print(client, "act.closeui", arguments);
        }

        private static int ClickCommand(BridgeClient client, List<string> args)
        {
            Require(args.Count >= 2,
                "click <selector|id> [--at <x> <y>] [--gamewidget N] [holdMs]");
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["selector"] = args[1]
            };
            int holdMs = 0;
            for (int i = 2; i < args.Count; i++)
            {
                if (args[i] == "--at" && i + 2 < args.Count)
                {
                    arguments["x"] = ParseFloat(args[i + 1]);
                    arguments["y"] = ParseFloat(args[i + 2]);
                    i += 2;
                }
                else if (args[i] == "--gamewidget" && i + 1 < args.Count)
                {
                    arguments["gameWidget"] = ParseInt(args[++i]);
                }
                else if (int.TryParse(args[i], out int parsed))
                {
                    holdMs = parsed;
                }
            }
            arguments["holdMs"] = holdMs;
            return Print(client, "act.uiclick", arguments);
        }

        /// <summary>
        /// `sccmd accept [dialog] [--button &lt;selector&gt;]`：接受当前（或指定）对话框。
        ///
        /// 服务端走的是**引擎自己的点击路径**：在对话框的 OK 按钮上合成一次 Tap+Click，
        /// 于是对话框 `Update` 里的 `IsClicked` 提交分支（内存条/真值表在 `Dismiss` 之前
        /// 拷贝数据）真的跑起来 —— 不是只把窗口关掉。
        /// </summary>
        private static int AcceptCommand(BridgeClient client, List<string> args, string command)
        {
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == "--button" && i + 1 < args.Count)
                    arguments["button"] = args[++i];
                else if (args[i] == "--dialog" && i + 1 < args.Count)
                    arguments["dialog"] = args[++i];
                else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    arguments["dialog"] = args[i];
            }
            return Print(client, command, arguments);
        }

        /// <summary>
        /// `sccmd dialog ok|cancel [dialog] [--button &lt;selector&gt;]`：同 `accept` / 取消分支。
        /// </summary>
        private static int DialogCommand(BridgeClient client, List<string> args)
        {
            Require(args.Count >= 2, "dialog ok|cancel [dialog] [--button <selector>]");
            string action = args[1].ToLowerInvariant();
            if (action != "ok" && action != "accept" && action != "cancel")
            {
                throw new BridgeException(
                    "invalid_argument", "dialog action must be 'ok' (or 'accept') or 'cancel'.");
            }
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["action"] = action
            };
            for (int i = 2; i < args.Count; i++)
            {
                if (args[i] == "--button" && i + 1 < args.Count)
                    arguments["button"] = args[++i];
                else if (args[i] == "--dialog" && i + 1 < args.Count)
                    arguments["dialog"] = args[++i];
                else if (!args[i].StartsWith("--", StringComparison.Ordinal)
                    && !arguments.ContainsKey("dialog"))
                    arguments["dialog"] = args[i];
            }
            return Print(client, "act.dialog", arguments);
        }

        /// <summary>
        /// `sccmd act.use|act.interact|act.place [--hold ms]`（也接受短名 `use|interact|place`）。
        ///
        /// 三者注入的都是**同一次右键边沿**（`PlayerInput.Interact`）：引擎的
        /// `ComponentPlayer.Update` 按 `ComponentMiner.Use` → `Interact` → `Place` 的固定顺序消费
        /// （Survivalcraft/Game/ComponentPlayer.cs:151-181），没有任何输入能跳过前一步。
        /// 所以命令名只表达"你想做哪件事"，回包里的 `predictedBranch` / `predictedReason` /
        /// `cells.changed` / `heldItemAfter` 才是"引擎实际走了哪一步"的读据。
        /// `--hold ms` = 右键按住多久（边沿动作只看按下那一帧；按久一点同时会让
        /// `PlayerInput.Aim` 有值 —— 和真人按住右键完全一样，手持弓时会开始拉弓）。
        /// </summary>
        private static int InteractionCommand(BridgeClient client, List<string> args, string command)
        {
            int holdMs = 0;
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == "--hold" && i + 1 < args.Count)
                    holdMs = ParseInt(args[++i]);
                else if (int.TryParse(args[i], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int positional))
                    holdMs = positional;
                else
                    throw new BridgeException("invalid_argument",
                        "usage: sccmd " + command + " [--hold <ms>]");
            }
            if (holdMs < 0)
                throw new BridgeException("invalid_argument", "holdMs must be >= 0.");
            return Print(client, "act." + command, Args(("holdMs", holdMs)));
        }

        /// <summary>
        /// `sccmd act.aim start|release|cancel [--hold ms]`：
        ///   start   —— 按住右键 = `PlayerInput.Aim` 每帧续力（bow/crossbow/musket/spear）。
        ///              `--hold ms` = 按住这么久后自动松开（= 射出/扔出）；不给就保持按住。
        ///   release —— 松开右键 → 引擎在下一帧走 `AimState.Completed`（射出/扔出）。
        ///   cancel  —— 收弓且**不射出**（引擎没有"取消瞄准"输入，服务端做的是引擎自己取消时
        ///              的 `AimState.Cancelled` + 清 `ComponentPlayer.m_aim`，见 Mod 文档）。
        /// 回包里 `projectiles.created` 是"真的射出去了吗"的证据（>0 = 射了）。
        /// </summary>
        private static int AimCommand(BridgeClient client, List<string> args)
        {
            Require(args.Count >= 2, "act.aim start|release|cancel [--hold <ms>]");
            string action = args[1].ToLowerInvariant();
            if (action != "start" && action != "release" && action != "cancel")
            {
                throw new BridgeException("invalid_argument",
                    "act.aim action must be start, release or cancel (got '" + args[1] + "').");
            }

            int holdMs = 0;
            for (int i = 2; i < args.Count; i++)
            {
                if (args[i] == "--hold" && i + 1 < args.Count)
                    holdMs = ParseInt(args[++i]);
                else if (int.TryParse(args[i], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int positional))
                    holdMs = positional;
                else
                    throw new BridgeException("invalid_argument",
                        "usage: sccmd act.aim " + action + " [--hold <ms>]");
            }
            if (holdMs < 0)
                throw new BridgeException("invalid_argument", "holdMs must be >= 0.");
            return Print(client, "act.aim", Args(("action", action), ("holdMs", holdMs)));
        }

        /// <summary>
        /// 条件等待：服务端逐帧求值，避免客户端用固定 sleep 猜时序
        /// （转屏动画期间控件树会冻结，靠 sleep 读界面必然踩竞态）。
        /// </summary>
        private static int WaitForCommand(BridgeClient client, List<string> args)
        {
            Require(args.Count >= 2, "waitfor <condition> [--timeout ms]");
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["condition"] = args[1]
            };
            int timeoutMs = 10000;
            for (int i = 2; i < args.Count; i++)
            {
                if (args[i] == "--timeout" && i + 1 < args.Count)
                    timeoutMs = ParseInt(args[++i]);
            }
            arguments["timeoutMs"] = timeoutMs;

            // 服务端要一直等到条件成立或超时，客户端读超时必须比它更大。
            JsonElement result = client.Send(
                "obs.waitFor", arguments, timeoutMs + 15000);
            if (s_json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result));
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            return result.TryGetProperty("satisfied", out JsonElement satisfied) &&
                satisfied.GetBoolean() ? 0 : 7;
        }

        /// <summary>
        /// `sccmd enter`：把"进世界"这条链一次走完。
        ///
        /// 服务端 `act.enter` 每次只推进一步（主菜单 → 世界列表 → 创建/加载 → **角色界面** → 游戏），
        /// 这里循环调用并在两步之间留出切屏时间；`--settle` 调间隔、`--steps` 调上限、
        /// `--row N` / `--world 名字` 指定世界列表里要开的世界。
        /// </summary>
        private static int EnterWorld(BridgeClient client, List<string> args)
        {
            int maxSteps = 40;
            int settleMs = 800;
            int row = -1;
            string world = null;
            bool createNew = false;
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == "--steps" && i + 1 < args.Count)
                    maxSteps = ParseInt(args[++i]);
                else if (args[i] == "--settle" && i + 1 < args.Count)
                    settleMs = ParseInt(args[++i]);
                else if (args[i] == "--row" && i + 1 < args.Count)
                    row = ParseInt(args[++i]);
                else if (args[i] == "--world" && i + 1 < args.Count)
                    world = args[++i];
                else if (args[i] == "--new")
                    createNew = true;
            }

            bool listSelected = false;
            var trail = new List<string>();
            JsonElement last = default;
            for (int step = 0; step < maxSteps; step++)
            {
                var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
                if (createNew)
                {
                    arguments["new"] = true;
                }
                else if (!listSelected)
                {
                    if (!string.IsNullOrEmpty(world))
                        arguments["world"] = world;
                    else
                        arguments["row"] = row >= 0 ? row : 0;
                }

                last = client.Send("act.enter", arguments, s_timeoutMs);
                string screen = ReadString(last, "screen");
                string clicked = ReadString(last, "clicked");
                string reason = ReadString(last, "reason");
                bool done = ReadBool(last, "done");

                trail.Add(screen + (clicked != null
                    ? " -> " + clicked
                    : (reason != null ? " (" + reason + ")" : "")));

                if (done)
                {
                    PrintEnterResult(trail, last, ok: true);
                    return 0;
                }
                // 世界列表：只在第一次点列表行做选择，之后点 Play
                if (string.Equals(screen, "Play", StringComparison.Ordinal) &&
                    clicked != null && clicked.StartsWith("list:", StringComparison.Ordinal))
                {
                    listSelected = true;
                }
                else if (!string.Equals(screen, "Play", StringComparison.Ordinal))
                {
                    listSelected = false;
                }

                if (settleMs > 0)
                    System.Threading.Thread.Sleep(settleMs);
            }

            PrintEnterResult(trail, last, ok: false);
            return 7;
        }

        private static void PrintEnterResult(List<string> trail, JsonElement last, bool ok)
        {
            if (s_json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = ok,
                    ["steps"] = trail,
                    ["last"] = last.ValueKind == JsonValueKind.Undefined ? null : (object)last
                }));
                return;
            }
            Console.WriteLine(ok ? "已进入游戏。" : "未能进入游戏（步数用尽）。");
            for (int i = 0; i < trail.Count; i++)
            {
                Console.WriteLine("  " + (i + 1) + ". " + trail[i]);
            }
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(name, out JsonElement value))
            {
                return null;
            }
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static bool ReadBool(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.True;
        }

        private static int WorldCommand(BridgeClient client, List<string> args)
        {
            string topic = args.Count >= 2 ? args[1].ToLowerInvariant() : "time";
            switch (topic)
            {
                case "time":
                    return Print(client, "obs.world.time", null);
                case "entities":
                {
                    float er = 64f; int em = 64;
                    int? ex = null, ey = null, ez = null;
                    for (int i = 2; i < args.Count; i++)
                    {
                        switch (args[i])
                        {
                            case "--at":
                                if (i + 3 < args.Count)
                                { ex = ParseInt(args[i + 1]); ey = ParseInt(args[i + 2]); ez = ParseInt(args[i + 3]); i += 3; }
                                break;
                            case "--radius":
                                if (i + 1 < args.Count) er = ParseFloat(args[++i]);
                                break;
                            case "--max":
                                if (i + 1 < args.Count) em = ParseInt(args[++i]);
                                break;
                            default:
                                if (i == 2) er = ParseFloat(args[i]);
                                else if (i == 3) em = ParseInt(args[i]);
                                break;
                        }
                    }
                    var eargs = new System.Collections.Generic.List<(string, object)>
                    { ("radius", er), ("max", em) };
                    if (ex.HasValue && ey.HasValue && ez.HasValue)
                    {
                        eargs.Add(("x", ex.Value)); eargs.Add(("y", ey.Value)); eargs.Add(("z", ez.Value));
                    }
                    return Print(client, "obs.world.entities", Args(eargs.ToArray()));
                }
                case "blocks":
                {
                    // 2026-10-01：支持 **指定中心** 与命名参数 —— 读特定区域（例如火格）时人不用动。
                    // usage: world blocks [--at x y z] [--radius N] [--max N]（旧的 `world blocks <r> <max>` 仍可用）
                    int radius = 4, max = 256;
                    int? cx = null, cy = null, cz = null;
                    for (int i = 2; i < args.Count; i++)
                    {
                        switch (args[i])
                        {
                            case "--at":
                                if (i + 3 < args.Count)
                                {
                                    cx = ParseInt(args[i + 1]);
                                    cy = ParseInt(args[i + 2]);
                                    cz = ParseInt(args[i + 3]);
                                    i += 3;
                                }
                                break;
                            case "--radius":
                                if (i + 1 < args.Count) radius = ParseInt(args[++i]);
                                break;
                            case "--max":
                                if (i + 1 < args.Count) max = ParseInt(args[++i]);
                                break;
                            default:
                                // 兼容旧的 `world blocks <radius> <max>`
                                if (i == 2) radius = ParseInt(args[i]);
                                else if (i == 3) max = ParseInt(args[i]);
                                break;
                        }
                    }
                    var blockArgs = new System.Collections.Generic.List<(string, object)>
                    {
                        ("radius", radius),
                        ("max", max)
                    };
                    if (cx.HasValue && cy.HasValue && cz.HasValue)
                    {
                        blockArgs.Add(("x", cx.Value));
                        blockArgs.Add(("y", cy.Value));
                        blockArgs.Add(("z", cz.Value));
                    }
                    return Print(client, "obs.world.blocks", Args(blockArgs.ToArray()));
                }
                case "fire":
                    // 直接列主机火表（坐标 + 该格 contents/data）——火焰可见性看 data、能否点燃看 fireDuration
                    return Print(client, "obs.world.fire", null);
                default:
                    throw new BridgeException(
                        "invalid_argument", "usage: world <time|entities|blocks|fire> [--at x y z] [--radius N] [--max N]");
            }
        }

        /// <summary>
        /// `sccmd players [--index N] [--max N] [--slots N]` → 服务端 `players.all`。
        ///
        /// 返回的是**数组**：每个 PlayerData 一条（主机每个本地角色一条，联机远端 avatar 也各一条）。
        /// 用 `--json` 拿紧凑完整状态；默认缩进 JSON 便于直接读。
        /// </summary>
        private static int PlayersCommand(BridgeClient client, List<string> args)
        {
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == "--index" && i + 1 < args.Count)
                    arguments["index"] = ParseInt(args[++i]);
                else if (args[i] == "--max" && i + 1 < args.Count)
                    arguments["max"] = ParseInt(args[++i]);
                else if (args[i] == "--slots" && i + 1 < args.Count)
                    arguments["slots"] = ParseInt(args[++i]);
                else
                    throw new BridgeException("invalid_argument",
                        "usage: players [--index N] [--max N] [--slots N]");
            }

            return Print(client, "players.all", arguments);
        }

        /// <summary>
        /// 地图选择界面的语义扫描：把 obs.ui 的 WorldItem.Name / Details 配对，
        /// 用详情里的 Ping/Online 判断"服务器房间"还是"本地世界"，并原样输出完整详情。
        /// 这样不依赖 ui 表格的 24 字符截断，也不会再把本地地图当成联机房间。
        /// </summary>
        private static int MapsCommand(BridgeClient client, List<string> args)
        {
            JsonElement result = client.Send("obs.ui", Args(("all", true)), s_timeoutMs);
            if (!result.TryGetProperty("elements", out JsonElement elements))
            {
                Console.WriteLine("maps: obs.ui 没有返回 elements");
                return 1;
            }
            var rows = new Dictionary<string, MapRow>(StringComparer.Ordinal);
            var order = new List<string>();
            bool playEnabled = false;
            foreach (JsonElement element in elements.EnumerateArray())
            {
                string name = GetText(element, "name");
                string path = GetText(element, "path");
                int slash = path.LastIndexOf('/');
                string parent = slash >= 0 ? path.Substring(0, slash) : path;
                if (name == "WorldItem.Name" || name == "WorldItem.Details")
                {
                    if (!rows.TryGetValue(parent, out MapRow row))
                    {
                        row = new MapRow();
                        rows.Add(parent, row);
                        order.Add(parent);
                    }
                    if (name == "WorldItem.Name") row.Name = GetText(element, "text");
                    else row.Details = GetText(element, "text");
                }
                else if (name == "Play")
                {
                    playEnabled = element.TryGetProperty("enabled", out JsonElement enabled) &&
                        enabled.ValueKind == JsonValueKind.True;
                }
            }
            Console.WriteLine("maps: count=" + order.Count + " playEnabled=" + playEnabled);
            for (int i = 0; i < order.Count; i++)
            {
                MapRow row = rows[order[i]];
                string details = row.Details ?? string.Empty;
                bool server = details.IndexOf("Ping ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    details.IndexOf("LAN (", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    details.IndexOf("Online (", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    details.IndexOf("Service", StringComparison.OrdinalIgnoreCase) >= 0;
                string col = server ? "server-room" : "local-world-or-unknown";
                int ping = ExtractPing(details);
                Console.WriteLine("[" + (i + 1) + "] name=\"" + row.Name + "\" kind=" + col +
                    (ping >= 0 ? " ping=" + ping + "ms" : string.Empty));
                Console.WriteLine("    details=" + details);
            }
            if (order.Count == 0)
                Console.WriteLine("(no map rows; if on Play screen, wait for LAN discovery)");
            return 0;
        }

        private sealed class MapRow
        {
            public string Name = string.Empty;
            public string Details = string.Empty;
        }

        private static int ExtractPing(string details)
        {
            if (string.IsNullOrEmpty(details)) return -1;
            int ping = details.IndexOf("Ping", StringComparison.OrdinalIgnoreCase);
            if (ping < 0) return -1;
            int start = ping + 4;
            while (start < details.Length && !char.IsDigit(details[start])) start++;
            int end = start;
            while (end < details.Length && char.IsDigit(details[end])) end++;
            return end > start && int.TryParse(details.Substring(start, end - start), out int value)
                ? value : -1;
        }

        private static int PrintUi(BridgeClient client, List<string> args)
        {
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            // 默认输出完整 text：截断曾让操作者把服务器房间误判成本地世界。
            // --compact 只是显式要求旧的一行截断显示；--full 保留为兼容参数。
            bool compact = false;
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == "--all")
                    arguments["all"] = true;
                else if (args[i] == "--compact")
                    compact = true;
                else if (args[i] == "--full")
                    compact = false;
                else if (args[i] == "--filter" && i + 1 < args.Count)
                    arguments["filter"] = args[++i];
                else if (args[i] == "--max" && i + 1 < args.Count)
                    arguments["maxElements"] = ParseInt(args[++i]);
                else if (args[i] == "--gamewidget" && i + 1 < args.Count)
                    arguments["gameWidget"] = ParseInt(args[++i]);
            }

            JsonElement result = client.Send("obs.ui", arguments, s_timeoutMs);
            if (s_json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result));
                return 0;
            }

            if (result.TryGetProperty("elements", out JsonElement elements))
            {
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-4} {1,-3} {2,-24} {3,-22} {4,-24} {5,-20} {6}",
                    "id", "gw", "type", "name", "text", "clientPoint", "hittable"));
                foreach (JsonElement element in elements.EnumerateArray())
                {
                    bool laidOut = !element.TryGetProperty("laidOut", out JsonElement laidOutValue)
                        || laidOutValue.ValueKind != JsonValueKind.False;
                    bool hittable = element.TryGetProperty("hittable", out JsonElement hittableValue)
                        && hittableValue.ValueKind == JsonValueKind.True;
                    string state;
                    if (hittable)
                    {
                        state = "yes";
                    }
                    else if (!laidOut)
                    {
                        // 没布局过**不是**被遮挡：以前这里打印 "no (ViewWidget(View))"，把人带错方向。
                        string hidden = GetText(element, "hiddenBy");
                        state = "no (not laid out"
                            + (string.IsNullOrEmpty(hidden) ? "" : " - hidden by " + hidden) + ")";
                    }
                    else
                    {
                        state = "no (" + GetText(element, "blockedBy") + ")";
                    }
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0,-4} {1,-3} {2,-24} {3,-22} {4,-24} {5,-20} {6}",
                        GetText(element, "id"),
                        GetText(element, "gameWidget"),
                        GetText(element, "type"),
                        compact ? CompactName(GetText(element, "name"))
                            : GetText(element, "name"),
                        compact ? CompactText(GetText(element, "text"))
                            : GetText(element, "text"),
                        FormatPoint(element, "clientPoint"),
                        state));
                }
            }
            if (result.TryGetProperty("totalCount", out JsonElement total))
                Console.WriteLine("total=" + total.GetInt32());
            if (result.TryGetProperty("primaryGameWidget", out JsonElement primary)
                && primary.ValueKind != JsonValueKind.Null)
            {
                Console.WriteLine("primaryGameWidget=" + primary.GetInt32()
                    + " (click without --gamewidget targets this split view)");
            }
            if (result.TryGetProperty("views", out JsonElement views)
                && views.ValueKind == JsonValueKind.Array && views.GetArrayLength() > 0)
            {
                foreach (JsonElement view in views.EnumerateArray())
                {
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "view gw={0} player={1} rect={2} layout={3}{4} input={5} mouse={6}{7}",
                        GetText(view, "gameWidget"),
                        GetText(view, "player"),
                        FormatRect(view),
                        GetText(view, "layout"),
                        view.TryGetProperty("cellAgrees", out JsonElement agrees)
                            && agrees.ValueKind == JsonValueKind.False ? " CELL-MISMATCH" : "",
                        GetText(view, "inputDevices"),
                        view.TryGetProperty("acceptsMouse", out JsonElement mouse)
                            && mouse.ValueKind == JsonValueKind.True ? "yes" : "no",
                        view.TryGetProperty("primary", out JsonElement isPrimary)
                            && isPrimary.ValueKind == JsonValueKind.True ? " PRIMARY" : ""));
                }
            }
            return 0;
        }

        private static string FormatPoint(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement point) ||
                point.ValueKind != JsonValueKind.Object)
            {
                return "-";
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0},{1:0}",
                point.GetProperty("x").GetSingle(),
                point.GetProperty("y").GetSingle());
        }

        private static string FormatRect(JsonElement element)
        {
            if (!element.TryGetProperty("rect", out JsonElement rect) ||
                rect.ValueKind != JsonValueKind.Object)
            {
                return "-";
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0},{1:0} {2:0}x{3:0}",
                rect.GetProperty("x").GetSingle(),
                rect.GetProperty("y").GetSingle(),
                rect.GetProperty("w").GetSingle(),
                rect.GetProperty("h").GetSingle());
        }

        private static string GetText(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return string.Empty;
            if (value.ValueKind == JsonValueKind.Null)
                return string.Empty;
            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();
        }

        private static string CompactName(string value) =>
            string.IsNullOrEmpty(value) || value.Length <= 22
                ? value ?? string.Empty
                : value.Substring(0, 21) + "…[name truncated; use maps]";

        private static string CompactText(string value) =>
            string.IsNullOrEmpty(value) || value.Length <= 24
                ? value ?? string.Empty
                : value.Substring(0, 23) + "…[truncated; use maps or omit --compact]";

        private static void PrintHelp()
        {
            Console.WriteLine(@"sccmd — CmdBridge console

观察（只读，无限）
  status                      屏幕/窗口/动画/布局状态
  ui [--all] [--compact] [--filter T] [--max N] [--gamewidget N]
                              当前界面元素（RootWidget + 当前屏 + 活动对话框；含坐标、text、
                              hittable、mounted、scope、clicked、laidOut、gameWidget）
                              **默认输出完整 text**；--compact 才使用旧的 22/24 字符截断。
                              不能用截断文本判断房间类型；地图界面优先用 maps。
                              分屏：每个视图各有一份同名 HUD 按钮，元素上的 gw 就是槽位号；
                              views 段列出每个视图的矩形/布局/输入设备/主视图
  maps | worlds               地图选择界面语义扫描：配对 WorldItem.Name/Details，
                              用完整详情里的 LAN/Ping/Online 判定 server-room，
                              输出完整 details 与 ping；不依赖 ui 的截断文本
  reachability                可达性报告：哪些元素点不到、被谁挡住
  player                      玩家状态 + 当前按键意图 + 背包（只报第一个本地角色）
  players [--index N] [--max N] [--slots N]
                              每一个 PlayerData：主机分屏 H1/H2/H3 + 联机远端 avatar
                              （位置/朝向/生命/睡眠/着火/疾病/生命体征/背包/动作；数组）
  aim                         准星指向的方块/实体
  dialogs                     当前对话框
  snapshot                    一次性拿全
  selftest                    注入点与可达性自检
  focusstatus                 焦点策略状态（mode/realFocus/detached/真实焦点来源）
  focusrecover                回到跟随引擎并**只回报**焦点状态（不抢真实窗口/鼠标；有命令行桥时禁止抢占）
  waitfor <condition> [--timeout ms]  条件等待（服务端逐帧求值，超时退出码 7）
      screen.animating.false | screen.is:<name> | element.present:<sel>
      element.hittable:<sel> | element.clickable:<sel> | modal.none | modal.is:<Type>
      dialog.none | dialog.present | world.loaded | player.alive | player.sleeping
  messages                    游戏自身提示（小提示 + 屏幕叠加消息，如睡觉被拒原因）
  events [sinceSeq]           事件环增量（UI 点击/世界交互/面板与屏幕变化/受伤/提示消息）
  world time|entities|blocks  世界观察（时间季节天气 / 半径内实体 / 区域方块）

动作（玩家控制器输入层）
  look <yawDeg> <pitchDeg>    瞬时转视角（绝对角度）
  lookdelta <dYaw> <dPitch>   相对转视角
  lookat <x> <y> <z>          看向世界坐标（自动求解，瞬时）
  key <name> [holdMs]         按键（脉冲，默认 40ms≈一帧；显式 0 = 同帧按下并抬起）
  hold <name> / release <name>|--all   按住/释放
  chord ctrl v                组合键
  mouse left click|down|up    世界内挖掘/攻击（左键）；放/交互/瞄准是右键，见下面四条
  act.use [--hold ms]         右键一次 = 引擎「使用手持物」（水桶/船/口哨/火柴/锤/耙/鞍/肥料）
  act.interact [--hold ms]    右键一次 = 引擎「与方块交互」（箱子/工作台/门/开关/按钮…）
  act.place [--hold ms]       右键一次 = 引擎「放置手持方块」
  act.aim start [--hold ms]   按住右键 = 瞄准/拉弓；给了 --hold 就按住这么久后自动松开（= 射出）
  act.aim release             松开右键 → AimState.Completed（射出/扔出）
  act.aim cancel              收弓但不射出（引擎无取消输入；服务端走 AimState.Cancelled + 清瞄准态）
  closeui [--cancel]          关掉挡着世界输入的界面（对话框按接受/取消 = 引擎自己的 Dismiss 路径；
                              背包/创造背包等模态面板 → ModalPanelWidget = null；HUD MoreContents → 收起）
                              世界内输入命令（act.move/lookat/key/dig/use/interact/place/aim）注入前会自动做这一步
  wheel <n>                   滚轮
  click <selector|id|text:文字> [--at x y] [--gamewidget N]
                              引擎内 UI 点击（注入前二次校验，不可跳级；
                              text:OK = 按控件文字选（对话框按钮最稳）；--at 用于 ListPanelWidget 的虚拟列表项）
                              分屏多视图：不带 --gamewidget 时**确定性地点主视图**（接受鼠标的最小槽位，
                              通常 H1=0）；要指定视图就 --gamewidget N（= GameWidgetIndex，见 ui 的 gw 列）。
                              点不到会明确报错而不是静默：element_not_laid_out / element_no_mouse_device
                              / element_not_in_view / element_occluded
  text <string>               逐字符输入到**当前有焦点**的文本框（无焦点报 no_focused_textbox；
                              满行/放不下报 text_rejected，不会静默丢字符）
  settext <selector> <text>   用 TextBoxWidget.Text setter 替换整个值（触发 TextChanged，
                              对话框内部状态同步；内存条行/真值表 LinearText 用这个）
  accept [dialog] [--button <sel>]  接受当前（或指定）对话框 = 在它的 OK 按钮上合成一次
                              引擎自己的 Tap+Click，让 Update 的 IsClicked 提交分支真的跑
  dialog ok|cancel [dialog] [--button <sel>]  同上 / 取消（走对话框自己的 Cancel 按钮）
  chat.open                   打开 SuMultiplayer 聊天输入行（未进联机会话报 chat_not_in_session）
  chat.text <文本>            追加到已打开的聊天输入行（净化 + 200 字上限；未打开报 chat_not_open）
  chat.submit                 提交当前聊天输入行（等价按回车，不追加文本）
  chat.send <文本>            打开 + 追加 + 提交 = 一条真实聊天（与手打同一发送链，UTF-8/CJK 原样）
  chat.cancel                 关闭聊天输入并清空缓冲区（等价按 Esc）
  raw <command> k=v ...       直接下发任意命令
  commands                    列出内建命令与各 Mod 注册的扩展命令
  enter [--row N|--world 名字] [--settle ms] [--steps N]
                              自动进世界：主菜单 → 世界列表 → 创建/加载 →
                              **角色创建/选择界面** → 进入游戏（服务端 act.enter 的分步驱动）

AI 控制面（需要装 PlayerAiMod，命令经 CmdBridgeMod 通道分发）
  ai status                   模式/暂停/行为树来源与哈希/活动节点路径/黑板/重载统计
  ai pause | resume           暂停/继续行为树（保留运行态；暂停即释放 AI 输入）
  ai enable | disable         接管/放弃本端角色（disable 会释放全部 AI 输入）
  ai release                  只释放 AI 注入的输入，不动接管状态
  ai trees                    列出两个包目录里的行为树包（实例目录优先）
  ai load <包名>              装载/切换活动树（立即生效）
  ai reload [包名]            强制重载（排队，tick 边界生效）
  ai validate [包名]          只校验不装载（编辑器保存前预检）
  ai prepare <包名>|--all     预编译进常驻树库（把编译成本挪到切换之前）
  ai switch <包名>            毫秒级切换活动树（优先用常驻副本，报告 switchMs）
  ai notify <路径> [hash]     模拟编辑器推送重载通知
  ai bb [键] [值] [类型]      读/写黑板（类型 bool|int|float|string；无参列全部）
  ai record start [名字]      开始录制（= PgUp；录制中行为树停手、人类操作被记录）
  ai record pause             暂停/继续录制（= PgUp 切换）
  ai record stop [名字]       结束录制（= PgDn；带名字则直接落盘，不带则等命名）
  ai record save <名字> [--overwrite]   把录好的动作包写成 <名字>.scatpak
  ai record discard           丢弃待保存的录制

AI 内存态改写与导出（只改内存；原 .scbtpak 字节永远不变）
  ai set <节点id> <参数名> <值> [类型]   改活树里一个参数（类型 float|int|bool|string）
  ai insert <父节点id> <JSON>   按包格式插入节点（json 里写 type/id/properties）
  ai remove <节点id>            摘掉节点（先给子树收尾；删空组合要 force）
  ai move <节点id> <新父节点id> [下标]
  ai export <新包名>           把含改动的活树导出成新包（原包不动）
  ai snapshot                  活动节点快照（活动路径 + 每个节点状态；编辑器监视复用）
  ai logs [条数]               事件日志最近若干条（文件在 PlayerAi/Logs/PlayerAi.log）
  aiselftest                  跑行为树内核 + 包/热重载 + 命令层三套自检

全局选项：--json  --root <游戏目录>  --port <端口>  --token <令牌>  --timeout <ms>");
        }

        // ---------------------------------------------------------------- 工具

        private static Dictionary<string, object> Args(params (string Key, object Value)[] pairs)
        {
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i++)
                arguments[pairs[i].Key] = pairs[i].Value;
            return arguments;
        }

        /// <summary>把从 <paramref name="start"/> 起的参数用单个空格拼回一段文本（支持不加引号的多词）。</summary>
        private static string JoinText(List<string> args, int start)
        {
            if (args == null || start >= args.Count)
                return string.Empty;
            return string.Join(" ", args.GetRange(start, args.Count - start));
        }

        private static float ParseFloat(string text)
        {
            return float.Parse(text, CultureInfo.InvariantCulture);
        }

        private static int ParseInt(string text)
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }

        private static void Require(bool condition, string usage)
        {
            if (!condition)
                throw new BridgeException("invalid_argument", "usage: " + usage);
        }

        private static int ExitCodeFor(string code)
        {
            switch (code)
            {
                case "element_missing":
                    return 2;
                case "element_occluded":
                case "ambiguous_selector":
                // 世界内输入被界面吃掉（模态面板/对话框）——和"被挡住"同属"现在做不了这件事"。
                case "ui_modal_open":
                // 挡路的界面关不掉 / 当前不是游戏画面：同样属于"现在做不了这件事"。
                case "ui_blocking":
                    return 3;
                // 聊天输入没打开 / 不在联机会话 / 注入被拒绝：属于"当前状态下不能做"，
                // 与"连接失败"区分开。
                case "chat_not_open":
                case "chat_not_in_session":
                case "chat_inject_failed":
                case "chat_send_failed":
                    return 3;
                case "chat_unavailable":
                    return 5;
                case "connect_timeout":
                case "discovery_failed":
                case "runtime_file_missing":
                case "connection_closed":
                case "instance_ambiguous":
                    return 4;
                case "screen_busy":
                case "layout_invalid":
                case "not_ready":
                // 引擎认为窗口不活跃（输入会被整段丢弃）/ 本端输入面不含鼠标通道。
                case "window_not_active":
                case "world_not_active":
                case "input_channel_unavailable":
                case "timeout":
                    return 5;
                case "world_not_loaded":
                case "player_not_found":
                // 玩家未就绪 / 死亡 / 睡觉：ComponentInput 会丢掉整份 PlayerInput。
                case "player_not_ready":
                    return 6;
                default:
                    return 1;
            }
        }
    }
}

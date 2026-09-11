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
                    case "reachability":
                        return Print(client, "ui.reachability", null);
                    case "player":
                        return Print(client, "obs.player", null);
                    case "input":
                        return Print(client, "obs.input", null);
                    case "aim":
                        return Print(client, "obs.aim", Args(("maxDistance", 8f)));
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
                    case "selftest":
                        return Print(client, "obs.selftest", null);

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
                        return Print(client, "act.key", Args(
                            ("key", args[1]),
                            ("holdMs", args.Count >= 3 ? ParseInt(args[2]) : 0)));
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
                    case "click":
                        return ClickCommand(client, args);
                    case "text":
                        Require(args.Count >= 2, "text <string>");
                        return Print(client, "act.text", Args(("text", args[1])));
                    case "raw":
                        Require(args.Count >= 2, "raw <command> [key=value ...]");
                        return RunRaw(client, args);
                    default:
                        Console.Error.WriteLine("Unknown command '" + command + "'. Try 'help'.");
                        return 1;
                }
            }
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

        private static int ClickCommand(BridgeClient client, List<string> args)
        {
            Require(args.Count >= 2, "click <selector|id> [--at <x> <y>] [holdMs]");
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
                else if (int.TryParse(args[i], out int parsed))
                {
                    holdMs = parsed;
                }
            }
            arguments["holdMs"] = holdMs;
            return Print(client, "act.uiclick", arguments);
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

        private static int WorldCommand(BridgeClient client, List<string> args)
        {
            string topic = args.Count >= 2 ? args[1].ToLowerInvariant() : "time";
            switch (topic)
            {
                case "time":
                    return Print(client, "obs.world.time", null);
                case "entities":
                    return Print(client, "obs.world.entities", Args(
                        ("radius", args.Count >= 3 ? ParseFloat(args[2]) : 64f),
                        ("max", args.Count >= 4 ? ParseInt(args[3]) : 64)));
                case "blocks":
                    return Print(client, "obs.world.blocks", Args(
                        ("radius", args.Count >= 3 ? ParseInt(args[2]) : 4),
                        ("max", args.Count >= 4 ? ParseInt(args[3]) : 256)));
                default:
                    throw new BridgeException(
                        "invalid_argument", "usage: world <time|entities|blocks> [args]");
            }
        }

        private static int PrintUi(BridgeClient client, List<string> args)
        {
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == "--all")
                    arguments["all"] = true;
                else if (args[i] == "--filter" && i + 1 < args.Count)
                    arguments["filter"] = args[++i];
                else if (args[i] == "--max" && i + 1 < args.Count)
                    arguments["maxElements"] = ParseInt(args[++i]);
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
                    "{0,-4} {1,-24} {2,-22} {3,-24} {4,-22} {5}",
                    "id", "type", "name", "text", "clientPoint", "hittable"));
                foreach (JsonElement element in elements.EnumerateArray())
                {
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0,-4} {1,-24} {2,-22} {3,-24} {4,-22} {5}",
                        GetText(element, "id"),
                        GetText(element, "type"),
                        Truncate(GetText(element, "name"), 22),
                        Truncate(GetText(element, "text"), 24),
                        FormatPoint(element, "clientPoint"),
                        element.TryGetProperty("hittable", out JsonElement hittable) && hittable.GetBoolean()
                            ? "yes"
                            : "no (" + GetText(element, "blockedBy") + ")"));
                }
            }
            if (result.TryGetProperty("totalCount", out JsonElement total))
                Console.WriteLine("total=" + total.GetInt32());
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

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Length <= length ? value : value.Substring(0, length - 1) + "…";
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"sccmd — CmdBridge console

观察（只读，无限）
  status                      屏幕/窗口/动画/布局状态
  ui [--all] [--filter T] [--max N]   当前界面元素（含坐标与 hittable）
  reachability                可达性报告：哪些元素点不到、被谁挡住
  player                      玩家状态 + 当前按键意图 + 背包
  aim                         准星指向的方块/实体
  dialogs                     当前对话框
  snapshot                    一次性拿全
  selftest                    注入点与可达性自检
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
  key <name> [holdMs]         按键（脉冲）
  hold <name> / release <name>|--all   按住/释放
  chord ctrl v                组合键
  mouse left click|down|up    世界内挖/放/交互
  wheel <n>                   滚轮
  click <selector|id> [--at x y]  引擎内 UI 点击（注入前二次校验，不可跳级；
                              --at 用于 ListPanelWidget 的虚拟列表项）
  text <string>               逐字符输入
  raw <command> k=v ...       直接下发任意命令

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
                    return 3;
                case "connect_timeout":
                case "discovery_failed":
                case "runtime_file_missing":
                case "connection_closed":
                    return 4;
                case "screen_busy":
                case "layout_invalid":
                case "not_ready":
                case "timeout":
                    return 5;
                case "world_not_loaded":
                case "player_not_found":
                    return 6;
                default:
                    return 1;
            }
        }
    }
}

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
  focusstatus                 焦点策略状态（mode/realFocus/detached/真实焦点来源）
  focusrecover                焦点/鼠标卡住时自救：回到跟随引擎并恢复窗口状态
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
  commands                    列出内建命令与各 Mod 注册的扩展命令

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

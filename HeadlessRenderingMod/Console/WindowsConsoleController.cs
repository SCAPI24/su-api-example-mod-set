using Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace HeadlessRenderingMod
{
    internal sealed class WindowsConsoleController
    {
        private const uint AttachParentProcess = 0xFFFFFFFF;
        private const int MenuPageSize = 10;
        private static readonly JsonSerializerOptions s_jsonOptions =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly HeadlessControlServer m_server;
        private readonly HeadlessServerConfig m_config;
        private Thread m_thread;
        private volatile bool m_running;
        private bool m_ownsConsole;

        public bool IsRunning => m_running;

        public WindowsConsoleController(
            HeadlessControlServer server,
            HeadlessServerConfig config)
        {
            m_server = server ?? throw new ArgumentNullException(nameof(server));
            m_config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Main
        public bool Start()
        {
            if (m_running)
                return true;
            if (!InitializeConsole())
            {
                Log.Warning("[HeadlessRenderingMod] Unable to attach or allocate a console.");
                return false;
            }

            m_running = true;
            m_thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "HeadlessRenderingMod.Console"
            };
            m_thread.Start();
            return true;
        }

        public void Stop()
        {
            m_running = false;
            if (m_ownsConsole)
            {
                try
                {
                    FreeConsole();
                }
                catch
                {
                }
            }
            m_thread = null;
        }

        private bool InitializeConsole()
        {
            bool hasConsole = GetConsoleWindow() != IntPtr.Zero;
            if (!hasConsole)
            {
                hasConsole = AttachConsole(AttachParentProcess);
                if (!hasConsole)
                {
                    hasConsole = AllocConsole();
                    m_ownsConsole = hasConsole;
                }
            }
            if (!hasConsole)
                return false;

            SetConsoleCP(65001);
            SetConsoleOutputCP(65001);
            Encoding encoding = new UTF8Encoding(false, false);
            Console.InputEncoding = encoding;
            Console.OutputEncoding = encoding;
            Console.SetIn(new StreamReader(
                Console.OpenStandardInput(),
                encoding,
                false,
                1024,
                true));
            StreamWriter writer = new StreamWriter(
                Console.OpenStandardOutput(),
                encoding,
                1024,
                true)
            {
                AutoFlush = true
            };
            Console.SetOut(writer);
            Console.SetError(writer);
            try
            {
                Console.Title = "Survivalcraft Headless Server - " + m_config.InstanceId;
            }
            catch
            {
            }
            return true;
        }

        private void Run()
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine("Survivalcraft Headless Server");
                Console.WriteLine("Instance: " + m_config.InstanceId);
                Console.WriteLine("Waiting for game screens...");
                WaitForWorldCommands();
                RunMainMenu();
            }
            catch (Exception ex)
            {
                if (m_running)
                    Console.WriteLine("Console stopped: " + ex.Message);
            }
            finally
            {
                m_running = false;
            }
        }

        private void RunMainMenu()
        {
            string[] actions =
            {
                "Create World",
                "Join World",
                "List Worlds",
                "Export World",
                "Delete World",
                "Create Player",
                "Manage Players",
                "Server Status",
                "Command Line",
                "Shutdown"
            };

            int selected = 0;
            while (m_running)
            {
                try
                {
                    int? choice = SelectMenu("Server Control", actions, selected);
                    if (!choice.HasValue)
                        continue;
                    selected = choice.Value;
                    switch (selected)
                    {
                        case 0:
                            CreateWorld();
                            break;
                        case 1:
                            JoinWorld();
                            break;
                        case 2:
                            ShowWorlds();
                            break;
                        case 3:
                            ExportWorld();
                            break;
                        case 4:
                            DeleteWorld();
                            break;
                        case 5:
                            CreatePlayer();
                            break;
                        case 6:
                            ManagePlayers();
                            break;
                        case 7:
                            ShowResponse("status");
                            break;
                        case 8:
                            RunCommandLine();
                            break;
                        case 9:
                            if (PromptBoolean("Shut down Survivalcraft", false))
                            {
                                PrintResponse(m_server.SubmitLocal("shutdown"));
                                m_running = false;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("Error: " + ex.Message);
                    Pause();
                }
            }
        }

        private void RunCommandLine()
        {
            Console.Clear();
            Console.WriteLine("Command mode. Type Help for commands or Menu to return.");
            while (m_running)
            {
                Console.Write(GetCurrentScreen() + "> ");
                string line = Console.ReadLine();
                if (line == null || line.Trim().Equals("menu", StringComparison.OrdinalIgnoreCase))
                    return;
                try
                {
                    ExecuteLine(line.Trim());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                }
            }
        }

        private void ExecuteLine(string line)
        {
            if (line.Length == 0)
                return;
            List<string> tokens = SplitCommandLine(line);
            if (tokens.Count == 0)
                return;

            switch (tokens[0].ToLowerInvariant())
            {
                case "help":
                case "?":
                    PrintHelp();
                    break;
                case "createworld":
                    CreateWorld();
                    break;
                case "worldlist":
                    ShowWorlds();
                    break;
                case "joinworld":
                    JoinWorld();
                    break;
                case "exportworld":
                    ExportWorld();
                    break;
                case "deleteworld":
                    DeleteWorld();
                    break;
                case "createplayer":
                    CreatePlayer();
                    break;
                case "manageplayer":
                case "manageplayers":
                    ManagePlayers();
                    break;
                case "status":
                    PrintResponse(m_server.SubmitLocal("status"));
                    break;
                case "screenlist":
                case "screen.list":
                    PrintResponse(m_server.SubmitLocal("screen.list"));
                    break;
                case "switchscreen":
                case "screen.switch":
                    if (tokens.Count != 2)
                        throw new InvalidOperationException("Usage: SwitchScreen <screen-name>");
                    PrintResponse(m_server.SubmitLocal(
                        "screen.switch",
                        Args("screen", tokens[1])));
                    break;
                case "dialoglist":
                case "dialog.list":
                    PrintResponse(m_server.SubmitLocal("dialog.list"));
                    break;
                case "sequencelist":
                case "sequence.list":
                    PrintResponse(m_server.SubmitLocal("sequence.list"));
                    break;
                case "shutdown":
                case "exit":
                    PrintResponse(m_server.SubmitLocal("shutdown"));
                    m_running = false;
                    break;
                default:
                    Console.WriteLine("Unknown command. Type Help to list commands.");
                    break;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("CreateWorld, WorldList, JoinWorld, ExportWorld, DeleteWorld");
            Console.WriteLine("CreatePlayer, ManagePlayers, Status, ScreenList");
            Console.WriteLine("SwitchScreen <name>, DialogList, SequenceList, Shutdown, Menu");
            Console.WriteLine();
        }

        // Source: Survivalcraft/Game/NewWorldScreen.cs:NewWorldScreen.Update
        private void CreateWorld()
        {
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> Create World");
            string name;
            do
            {
                name = PromptText("World name", "ServerWorld");
                if (!IsValidWorldName(name))
                    Console.WriteLine("Use 1-14 ASCII letters, digits or spaces.");
            }
            while (!IsValidWorldName(name));

            string seed = PromptText("Seed", string.Empty, "random");
            string gameMode = PromptChoice(
                "Game mode",
                new[] { "Creative", "Harmless", "Survival", "Challenging", "Cruel" },
                "Survival");
            string startingPosition = PromptChoice(
                "Starting position",
                new[] { "Easy", "Medium", "Hard" },
                "Easy");
            string terrainGeneration = PromptChoice(
                "Terrain generation",
                gameMode == "Creative"
                    ? new[] { "Continent", "Island", "FlatContinent", "FlatIsland" }
                    : new[] { "Continent", "Island" },
                "Continent");

            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["seed"] = seed,
                ["gameMode"] = gameMode,
                ["startingPosition"] = startingPosition,
                ["terrainGeneration"] = terrainGeneration,
                ["supernaturalCreatures"] = PromptBoolean("Enable supernatural creatures", true),
                ["friendlyFire"] = PromptBoolean("Allow friendly fire", true),
                ["seasonsChanging"] = PromptBoolean("Enable changing seasons", true)
            };
            if (gameMode == "Creative")
            {
                values["environmentBehavior"] = PromptChoice(
                    "Environment behavior",
                    new[] { "Living", "Static" },
                    "Living");
                values["timeOfDay"] = PromptChoice(
                    "Time of day",
                    new[] { "Changing", "Day", "Night", "Sunrise", "Sunset" },
                    "Changing");
                values["weatherEffects"] = PromptBoolean("Enable weather effects", true);
            }

            Console.WriteLine(JsonSerializer.Serialize(values, s_jsonOptions));
            if (!PromptBoolean("Create and load this world", true))
                return;
            PrintResponse(RequireSuccess(m_server.SubmitLocal("world.create", values)));
            WaitForWorldReady();
            Console.WriteLine("World is ready. Current screen: " + GetCurrentScreen());
            if (PromptBoolean("Create the first player now", true))
                CreatePlayer();
            else
                Pause();
        }

        // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.Play
        private void JoinWorld()
        {
            Dictionary<string, object> world = SelectWorld("Join World");
            if (world == null)
                return;
            Dictionary<string, object> response = m_server.SubmitLocal(
                "world.join",
                Args("world", world["directoryName"]));
            PrintResponse(RequireSuccess(response));
            WaitForWorldReady();
            Console.WriteLine("World is ready. Current screen: " + GetCurrentScreen());
            Pause();
        }

        private void ShowWorlds()
        {
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> Worlds");
            PrintResponse(RequireSuccess(m_server.SubmitLocal("world.list")));
            Pause();
        }

        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ExportWorld
        private void ExportWorld()
        {
            Dictionary<string, object> world = SelectWorld("Export World");
            if (world == null)
                return;
            string defaultName = world["name"] + ".scworld";
            string fileName = PromptText("Export file", defaultName);
            Dictionary<string, object> response = m_server.SubmitLocal(
                "world.export",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["world"] = world["directoryName"],
                    ["fileName"] = fileName
                });
            PrintResponse(RequireSuccess(response));
            Pause();
        }

        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.DeleteWorld
        private void DeleteWorld()
        {
            Dictionary<string, object> world = SelectWorld("Delete World");
            if (world == null)
                return;
            if (!PromptBoolean("Permanently delete '" + world["name"] + "'", false))
                return;
            Dictionary<string, object> response = m_server.SubmitLocal(
                "world.delete",
                Args("world", world["directoryName"]));
            PrintResponse(RequireSuccess(response));
            Pause();
        }

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.Update
        private void CreatePlayer()
        {
            string playerClass = PromptChoice(
                "Player class",
                new[] { "Male", "Female" },
                "Male");
            Dictionary<string, object> skin = SelectSkin(playerClass, "Character Skin");
            if (skin == null)
                return;
            string defaultName = skin["displayName"].ToString();
            string name;
            do
            {
                name = PromptText("Player name", defaultName);
                if (!IsValidPlayerName(name))
                    Console.WriteLine("Use 2-14 letters, digits or spaces without edge spaces.");
            }
            while (!IsValidPlayerName(name));

            Dictionary<string, object> response = m_server.SubmitLocal(
                "player.create",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["playerClass"] = playerClass,
                    ["skin"] = skin["name"],
                    ["enterGame"] = true
                });
            PrintResponse(RequireSuccess(response));
            Pause();
        }

        private void ManagePlayers()
        {
            while (m_running)
            {
                Dictionary<string, object> player = SelectPlayer("Manage Players");
                if (player == null)
                    return;
                string[] actions = { "Rename", "Change Skin", "Delete Player", "Back" };
                int? action = SelectMenu(player["name"] + " (#" + player["playerIndex"] + ")", actions, 0);
                if (!action.HasValue || action.Value == 3)
                    continue;

                if (action.Value == 0)
                {
                    string name = PromptText("New player name", player["name"].ToString());
                    if (!IsValidPlayerName(name))
                        throw new InvalidOperationException("Invalid player name.");
                    PrintResponse(RequireSuccess(m_server.SubmitLocal(
                        "player.update",
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["playerIndex"] = player["playerIndex"],
                            ["name"] = name
                        })));
                    Pause();
                }
                else if (action.Value == 1)
                {
                    Dictionary<string, object> skin = SelectSkin(
                        player["playerClass"].ToString(),
                        "Change Skin");
                    if (skin != null)
                    {
                        PrintResponse(RequireSuccess(m_server.SubmitLocal(
                            "player.update",
                            new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["playerIndex"] = player["playerIndex"],
                                ["skin"] = skin["name"]
                            })));
                        Pause();
                    }
                }
                else if (PromptBoolean("Permanently delete '" + player["name"] + "'", false))
                {
                    PrintResponse(RequireSuccess(m_server.SubmitLocal(
                        "player.delete",
                        Args("playerIndex", player["playerIndex"]))));
                    Pause();
                }
            }
        }

        private Dictionary<string, object> SelectWorld(string title)
        {
            List<Dictionary<string, object>> worlds = GetResult<List<Dictionary<string, object>>>(
                m_server.SubmitLocal("world.list"));
            if (worlds.Count == 0)
                throw new InvalidOperationException("No worlds are available.");
            List<string> labels = new List<string>();
            foreach (Dictionary<string, object> world in worlds)
            {
                labels.Add(world["name"] + " | " + world["gameMode"] +
                    (world["loaded"] is bool loaded && loaded ? " | loaded" : string.Empty));
            }
            int? selected = SelectMenu(title, labels.ToArray(), 0);
            return selected.HasValue ? worlds[selected.Value] : null;
        }

        private Dictionary<string, object> SelectPlayer(string title)
        {
            List<Dictionary<string, object>> players = GetResult<List<Dictionary<string, object>>>(
                m_server.SubmitLocal("player.list"));
            if (players.Count == 0)
                throw new InvalidOperationException("No players are available in the loaded world.");
            List<string> labels = new List<string>();
            foreach (Dictionary<string, object> player in players)
            {
                labels.Add("#" + player["playerIndex"] + " " + player["name"] +
                    " | " + player["playerClass"] + " | " + player["skinDisplayName"]);
            }
            int? selected = SelectMenu(title, labels.ToArray(), 0);
            return selected.HasValue ? players[selected.Value] : null;
        }

        private Dictionary<string, object> SelectSkin(string playerClass, string title)
        {
            List<Dictionary<string, object>> skins = GetResult<List<Dictionary<string, object>>>(
                m_server.SubmitLocal(
                    "player.skin.list",
                    Args("playerClass", playerClass)));
            List<string> labels = new List<string>();
            foreach (Dictionary<string, object> skin in skins)
                labels.Add(skin["displayName"] + " | " + skin["name"]);
            int? selected = SelectMenu(title, labels.ToArray(), 0);
            return selected.HasValue ? skins[selected.Value] : null;
        }

        private int? SelectMenu(string title, string[] items, int selected)
        {
            if (items == null || items.Length == 0)
                return null;
            selected = Math.Clamp(selected, 0, items.Length - 1);
            while (m_running)
            {
                Console.Clear();
                int page = selected / MenuPageSize;
                int first = page * MenuPageSize;
                int last = Math.Min(first + MenuPageSize, items.Length);
                Console.WriteLine(GetCurrentScreen() + "> " + title);
                Console.WriteLine("Up/Down select  Left back  Right/Enter next  PageUp/PageDown page");
                Console.WriteLine();
                for (int i = first; i < last; i++)
                    Console.WriteLine((i == selected ? "> " : "  ") + items[i]);
                if (items.Length > MenuPageSize)
                {
                    int pageCount = (items.Length + MenuPageSize - 1) / MenuPageSize;
                    Console.WriteLine();
                    Console.WriteLine("Page " + (page + 1) + "/" + pageCount);
                }

                ConsoleKey key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.UpArrow)
                    selected = selected > 0 ? selected - 1 : items.Length - 1;
                else if (key == ConsoleKey.DownArrow)
                    selected = selected < items.Length - 1 ? selected + 1 : 0;
                else if (key == ConsoleKey.PageUp)
                    selected = Math.Max(0, selected - MenuPageSize);
                else if (key == ConsoleKey.PageDown)
                    selected = Math.Min(items.Length - 1, selected + MenuPageSize);
                else if (key == ConsoleKey.Home)
                    selected = 0;
                else if (key == ConsoleKey.End)
                    selected = items.Length - 1;
                else if (key == ConsoleKey.LeftArrow || key == ConsoleKey.Escape)
                    return null;
                else if (key == ConsoleKey.RightArrow || key == ConsoleKey.Enter)
                    return selected;
            }
            return null;
        }

        private string PromptChoice(string label, string[] choices, string defaultValue)
        {
            int defaultIndex = Array.IndexOf(choices, defaultValue);
            int? selected = SelectMenu(label, choices, Math.Max(defaultIndex, 0));
            return selected.HasValue ? choices[selected.Value] : defaultValue;
        }

        private bool PromptBoolean(string label, bool defaultValue)
        {
            string[] choices = { "Yes", "No" };
            int? selected = SelectMenu(label, choices, defaultValue ? 0 : 1);
            return selected.HasValue ? selected.Value == 0 : defaultValue;
        }

        private void WaitForWorldCommands()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (m_running && DateTime.UtcNow < deadline)
            {
                Dictionary<string, object> response = m_server.SubmitLocal("screen.list");
                if (TryGetResult(response, out object result) &&
                    result is List<string> screens &&
                    screens.Contains("GameLoading") &&
                    screens.Contains("Game"))
                {
                    return;
                }
                Thread.Sleep(250);
            }
            throw new TimeoutException("Game screens did not initialize within 60 seconds.");
        }

        private void WaitForWorldReady()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(180);
            while (m_running && DateTime.UtcNow < deadline)
            {
                Dictionary<string, object> status = GetResult<Dictionary<string, object>>(
                    m_server.SubmitLocal("status"));
                bool loaded = status.TryGetValue("worldLoaded", out object worldLoaded) &&
                    worldLoaded is bool loadedValue && loadedValue;
                string screen = status.TryGetValue("currentScreen", out object currentScreen)
                    ? currentScreen?.ToString()
                    : null;
                if (loaded && !string.Equals(screen, "GameLoading", StringComparison.Ordinal))
                    return;
                Thread.Sleep(500);
            }
            throw new TimeoutException("World did not finish loading within 180 seconds.");
        }

        private string GetCurrentScreen()
        {
            try
            {
                Dictionary<string, object> status = GetResult<Dictionary<string, object>>(
                    m_server.SubmitLocal("status"));
                return status.TryGetValue("currentScreen", out object screen) && screen != null
                    ? screen.ToString()
                    : "NoScreen";
            }
            catch
            {
                return "Unavailable";
            }
        }

        private void ShowResponse(string command)
        {
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> " + command);
            PrintResponse(RequireSuccess(m_server.SubmitLocal(command)));
            Pause();
        }

        private static string PromptText(string label, string defaultValue, string defaultDisplay = null)
        {
            Console.Write(label + " [" + (defaultDisplay ?? defaultValue) + "]: ");
            string value = Console.ReadLine();
            return string.IsNullOrEmpty(value) ? defaultValue : value.Trim();
        }

        private static Dictionary<string, object> Args(string name, object value)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { [name] = value };
        }

        private static Dictionary<string, object> RequireSuccess(
            Dictionary<string, object> response)
        {
            if (response.TryGetValue("ok", out object ok) && ok is bool success && success)
                return response;
            if (response.TryGetValue("error", out object error) &&
                error is Dictionary<string, object> errorData &&
                errorData.TryGetValue("message", out object message))
            {
                throw new InvalidOperationException(message?.ToString());
            }
            throw new InvalidOperationException("Control command failed.");
        }

        private static T GetResult<T>(Dictionary<string, object> response)
        {
            RequireSuccess(response);
            if (response.TryGetValue("result", out object result) && result is T typed)
                return typed;
            throw new InvalidOperationException("Control response has an unexpected result type.");
        }

        private static bool TryGetResult(Dictionary<string, object> response, out object result)
        {
            result = null;
            return response.TryGetValue("ok", out object ok) &&
                ok is bool success && success &&
                response.TryGetValue("result", out result);
        }

        private static void PrintResponse(Dictionary<string, object> response)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, s_jsonOptions));
        }

        private static void Pause()
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to continue.");
            Console.ReadKey(true);
        }

        private static bool IsValidWorldName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 14)
                return false;
            if (!char.IsLetterOrDigit(name[0]) || !char.IsLetterOrDigit(name[name.Length - 1]))
                return false;
            foreach (char c in name)
            {
                if (c > 127 || (!char.IsLetterOrDigit(c) && c != ' '))
                    return false;
            }
            return true;
        }

        private static bool IsValidPlayerName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 14 ||
                name[0] == ' ' || name[name.Length - 1] == ' ')
            {
                return false;
            }
            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != ' ')
                    return false;
            }
            return true;
        }

        private static List<string> SplitCommandLine(string line)
        {
            List<string> result = new List<string>();
            StringBuilder current = new StringBuilder();
            bool quoted = false;
            foreach (char c in line)
            {
                if (c == '"')
                    quoted = !quoted;
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                    current.Append(c);
            }
            if (quoted)
                throw new InvalidOperationException("Unterminated quote.");
            if (current.Length > 0)
                result.Add(current.ToString());
            return result;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCP(uint codePageId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleOutputCP(uint codePageId);
    }
}

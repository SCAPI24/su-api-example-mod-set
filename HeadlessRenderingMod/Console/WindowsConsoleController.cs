using Engine;
using Engine.Serialization;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private const int MaxPendingNotifications = 64;
        private static readonly JsonSerializerOptions s_jsonOptions =
            new JsonSerializerOptions { WriteIndented = true };
        private static readonly float[] IslandSizes =
        {
            30f, 40f, 50f, 60f, 80f, 100f, 120f, 150f, 200f, 250f,
            300f, 400f, 500f, 600f, 800f, 1000f, 1200f, 1500f, 2000f, 2500f
        };
        private static readonly float[] BiomeSizes =
            { 0.25f, 0.33f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f };
        private static readonly float[] YearDays =
            { 8f, 12f, 16f, 20f, 24f, 32f, 48f, 64f, 96f };
        private static readonly int[] FlatTerrainBlocks =
            { 8, 2, 7, 3, 67, 66, 4, 5, 26, 73, 21, 46, 47, 15, 62, 68, 126, 71, 1 };
        private static readonly string[] TimeOfYearNames =
        {
            "Early Summer", "Summer", "Late Summer",
            "Early Autumn", "Autumn", "Late Autumn",
            "Early Winter", "Winter", "Late Winter",
            "Early Spring", "Spring", "Late Spring"
        };
        private static readonly float[] TimeOfYearValues =
        {
            0.03125f, 0.125f, 0.21875f,
            0.28125f, 0.375f, 0.46875f,
            0.53125f, 0.625f, 0.71875f,
            0.78125f, 0.875f, 0.96875f
        };

        private readonly HeadlessControlServer m_server;
        private readonly HeadlessServerConfig m_config;
        private readonly Queue<string> m_pendingNotifications = new Queue<string>();
        private readonly object m_notificationLock = new object();
        private Thread m_thread;
        private volatile bool m_running;
        private volatile bool m_interactive;
        private bool m_ownsConsole;
        private string m_multiplayerTelemetry;
        private DataModificationFeed m_dataModificationFeed;

        public bool IsRunning => m_running;

        /// <summary>主机侧 DM 决策记录（请求 / 自动同意 / 手动裁决 / 回执），菜单里可查。</summary>
        public void SetDataModificationFeed(DataModificationFeed feed)
        {
            m_dataModificationFeed = feed;
        }

        // Source: HeadlessRenderingMod.cs:WriteConsoleLine
        // 游戏线程的非菜单输出（DM 审批 / 回执）：交互（菜单、输入提示、分页）开着时先排队，
        // 等交互结束再打印。远端服务器只有这一套控制台菜单，被外部输出冲掉就没法点审批了。
        public void Notify(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (!m_interactive)
            {
                Console.WriteLine(text);
                return;
            }
            lock (m_notificationLock)
            {
                m_pendingNotifications.Enqueue(text);
                while (m_pendingNotifications.Count > MaxPendingNotifications)
                    m_pendingNotifications.Dequeue();
            }
        }

        private void FlushNotifications()
        {
            List<string> pending = new List<string>();
            lock (m_notificationLock)
            {
                if (m_pendingNotifications.Count == 0)
                    return;
                pending.AddRange(m_pendingNotifications);
                m_pendingNotifications.Clear();
            }
            Console.WriteLine();
            Console.WriteLine("-- " + pending.Count +
                " notification(s) while the menu was open --");
            foreach (string text in pending)
                Console.WriteLine(text);
        }

        /// <summary>交互期间（菜单 / 输入提示 / 分页）挡住外部输出，结束后统一补印。</summary>
        private T Interactive<T>(Func<T> body)
        {
            bool previous = m_interactive;
            m_interactive = true;
            try
            {
                return body();
            }
            finally
            {
                m_interactive = previous;
                if (!m_interactive)
                    FlushNotifications();
            }
        }

        public void SetMultiplayerTelemetry(string value)
        {
            m_multiplayerTelemetry = value;
            if (!m_running) return;
            try
            {
                Console.Title = "Survivalcraft Headless Server - " + m_config.InstanceId +
                    (string.IsNullOrWhiteSpace(value) ? string.Empty : " | " + value);
            }
            catch
            {
            }
        }

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
            if (!string.IsNullOrWhiteSpace(m_multiplayerTelemetry))
                Console.WriteLine(m_multiplayerTelemetry);
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
            int selected = 0;
            while (m_running)
            {
                string[] actions =
                {
                    "Create World",
                    "Load World",
                    GetCurrentWorldMenuLabel(),
                    "List Worlds",
                    "Export World",
                    "Delete World",
                    "Create Player",
                    "Manage Players",
                    "Multiplayer Hosting",
                    "Multiplayer Bandwidth",
                    "Server Status",
                    "Command Line",
                    "Shutdown"
                };
                selected = Math.Clamp(selected, 0, actions.Length - 1);
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
                            ShowCurrentWorld();
                            break;
                        case 3:
                            ShowWorlds();
                            break;
                        case 4:
                            ExportWorld();
                            break;
                        case 5:
                            DeleteWorld();
                            break;
                        case 6:
                            CreatePlayer();
                            break;
                        case 7:
                            ManagePlayers();
                            break;
                        case 8:
                            ConfigureMultiplayerHosting();
                            break;
                        case 9:
                            ConfigureMultiplayerBandwidth();
                            break;
                        case 10:
                            ShowResponse("status");
                            break;
                        case 11:
                            RunCommandLine();
                            break;
                        case 12:
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
            Interactive(() =>
            {
                RunCommandLineCore();
                return true;
            });
        }

        private void RunCommandLineCore()
        {
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
                case "saveworld":
                    PrintResponse(m_server.SubmitLocal("world.save"));
                    break;
                case "exportworld":
                    ExportWorld();
                    break;
                case "deleteworld":
                    DeleteWorld();
                    break;
                case "worldmode":
                    ChangeCurrentWorldMode();
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
                case "multiplayer":
                case "multiplayer.settings":
                    ConfigureMultiplayerBandwidth();
                    break;
                case "multiplayer.hosting":
                    ConfigureMultiplayerHosting();
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
            Console.WriteLine("CreateWorld, WorldList, JoinWorld, SaveWorld, ExportWorld, DeleteWorld, WorldMode");
            Console.WriteLine("CreatePlayer, ManagePlayers, Multiplayer, Multiplayer.Hosting, Status, ScreenList");
            Console.WriteLine("SwitchScreen <name>, DialogList, SequenceList, Shutdown, Menu");
            Console.WriteLine();
        }

        // Source: Survivalcraft/Game/NewWorldScreen.cs:NewWorldScreen.Update
        private void CreateWorld()
        {
            WorldCreationDraft draft = new WorldCreationDraft();
            int page = 0;
            while (m_running)
            {
                WorldEditorResult result = EditWorldPage(draft, page);
                if (result == WorldEditorResult.Cancel)
                    return;
                if (result == WorldEditorResult.PreviousPage)
                {
                    page = Math.Max(0, page - 1);
                    continue;
                }
                if (result == WorldEditorResult.NextPage)
                {
                    page = Math.Min(1, page + 1);
                    continue;
                }
                if (result != WorldEditorResult.Submit)
                    continue;

                if (!IsValidWorldName(draft.Name))
                {
                    Console.WriteLine("Use 1-14 ASCII letters, digits or spaces for the world name.");
                    Pause();
                    page = 0;
                    continue;
                }
                Dictionary<string, object> values = BuildWorldCreateArguments(draft);
                Console.Clear();
                Console.WriteLine(GetCurrentScreen() + "> Create World > Review");
                Console.WriteLine(JsonSerializer.Serialize(values, s_jsonOptions));
                if (!PromptBoolean("Create and load this world", true))
                    continue;

                PrintResponse(RequireSuccess(m_server.SubmitLocal("world.create", values)));
                WaitForWorldReady();
                Console.WriteLine("World is ready. Current screen: " + GetCurrentScreen());
                if (PromptBoolean("Create the first player now", true))
                    CreatePlayer();
                else
                    Pause();
                return;
            }
        }

        // Source: Survivalcraft/Game/NewWorldScreen.cs:NewWorldScreen.Update
        private WorldEditorResult EditWorldPage(WorldCreationDraft draft, int page) =>
            Interactive(() => EditWorldPageCore(draft, page));

        private WorldEditorResult EditWorldPageCore(WorldCreationDraft draft, int page)
        {
            int selected = 0;
            while (m_running)
            {
                List<WorldEditorItem> items = page == 0
                    ? BuildNewWorldPage(draft)
                    : BuildWorldOptionsPage(draft);
                selected = Math.Clamp(selected, 0, items.Count - 1);
                Console.Clear();
                Console.WriteLine(GetCurrentScreen() + "> Create World [" + (page + 1) + "/2]");
                Console.WriteLine("Up/Down select  Right edit  Left previous  PageUp/PageDown page");
                Console.WriteLine("For text fields, type the value and press Enter to return.");
                Console.WriteLine();
                for (int i = 0; i < items.Count; i++)
                {
                    Console.WriteLine((i == selected ? "> " : "  ") + items[i].Label);
                }

                ConsoleKey key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.UpArrow)
                    selected = selected > 0 ? selected - 1 : items.Count - 1;
                else if (key == ConsoleKey.DownArrow)
                    selected = selected < items.Count - 1 ? selected + 1 : 0;
                else if (key == ConsoleKey.Home)
                    selected = 0;
                else if (key == ConsoleKey.End)
                    selected = items.Count - 1;
                else if (key == ConsoleKey.PageUp)
                    return page > 0 ? WorldEditorResult.PreviousPage : WorldEditorResult.Cancel;
                else if (key == ConsoleKey.PageDown)
                    return page < 1 ? WorldEditorResult.NextPage : WorldEditorResult.Submit;
                else if (key == ConsoleKey.LeftArrow || key == ConsoleKey.Escape)
                    return page > 0 ? WorldEditorResult.PreviousPage : WorldEditorResult.Cancel;
                else if (key == ConsoleKey.RightArrow || key == ConsoleKey.Enter)
                {
                    WorldEditorItem item = items[selected];
                    if (item.Result != WorldEditorResult.Stay)
                        return item.Result;
                    item.Edit?.Invoke();
                }
            }
            return WorldEditorResult.Cancel;
        }

        private List<WorldEditorItem> BuildNewWorldPage(WorldCreationDraft draft)
        {
            return new List<WorldEditorItem>
            {
                new WorldEditorItem("World name: " + draft.Name, delegate
                {
                    string value = PromptText("World name", draft.Name);
                    if (!IsValidWorldName(value))
                        throw new InvalidOperationException(
                            "Use 1-14 ASCII letters, digits or spaces.");
                    draft.Name = value;
                }),
                new WorldEditorItem(
                    "Seed: " + (draft.Seed.Length == 0 ? "<random>" : draft.Seed),
                    delegate { draft.Seed = PromptText("Seed", draft.Seed, "random"); }),
                new WorldEditorItem("Game mode: " + draft.GameMode, delegate
                {
                    draft.GameMode = PromptChoice(
                        "Game mode",
                        new[] { "Creative", "Harmless", "Survival", "Challenging", "Cruel" },
                        draft.GameMode);
                    if (draft.GameMode != "Creative" &&
                        (draft.TerrainGeneration == "FlatContinent" ||
                        draft.TerrainGeneration == "FlatIsland"))
                    {
                        draft.TerrainGeneration = draft.TerrainGeneration == "FlatIsland"
                            ? "Island"
                            : "Continent";
                    }
                }),
                new WorldEditorItem("Starting position: " + draft.StartingPosition, delegate
                {
                    draft.StartingPosition = PromptChoice(
                        "Starting position",
                        new[] { "Easy", "Medium", "Hard" },
                        draft.StartingPosition);
                }),
                new WorldEditorItem("World options...", WorldEditorResult.NextPage)
            };
        }

        // Source: Survivalcraft/Game/WorldOptionsScreen.cs:WorldOptionsScreen.Update
        private List<WorldEditorItem> BuildWorldOptionsPage(WorldCreationDraft draft)
        {
            List<WorldEditorItem> items = new List<WorldEditorItem>();
            items.Add(new WorldEditorItem("Terrain type: " + draft.TerrainGeneration, delegate
            {
                draft.TerrainGeneration = PromptChoice(
                    "Terrain type",
                    draft.GameMode == "Creative"
                        ? new[] { "Continent", "Island", "FlatContinent", "FlatIsland" }
                        : new[] { "Continent", "Island" },
                    draft.TerrainGeneration);
            }));

            bool island = draft.TerrainGeneration == "Island" ||
                draft.TerrainGeneration == "FlatIsland";
            bool continent = draft.TerrainGeneration == "Continent" ||
                draft.TerrainGeneration == "FlatContinent";
            bool flat = draft.TerrainGeneration == "FlatContinent" ||
                draft.TerrainGeneration == "FlatIsland";
            if (island)
            {
                items.Add(new WorldEditorItem("Island size east-west: " + draft.IslandSizeEW, delegate
                {
                    draft.IslandSizeEW = PromptFloatChoice(
                        "Island size east-west", IslandSizes, draft.IslandSizeEW);
                }));
                items.Add(new WorldEditorItem("Island size north-south: " + draft.IslandSizeNS, delegate
                {
                    draft.IslandSizeNS = PromptFloatChoice(
                        "Island size north-south", IslandSizes, draft.IslandSizeNS);
                }));
            }
            if (continent)
            {
                items.Add(new WorldEditorItem("Sea level: " + FormatOffset(draft.SeaLevelOffset), delegate
                {
                    draft.SeaLevelOffset = PromptIntegerChoice(
                        "Sea level", IntegerRange(-4, 4), draft.SeaLevelOffset);
                }));
                items.Add(new WorldEditorItem("Temperature: " + FormatOffset(draft.TemperatureOffset), delegate
                {
                    draft.TemperatureOffset = PromptFloatChoice(
                        "Temperature", FloatRange(-8, 8), draft.TemperatureOffset);
                }));
                items.Add(new WorldEditorItem("Humidity: " + FormatOffset(draft.HumidityOffset), delegate
                {
                    draft.HumidityOffset = PromptFloatChoice(
                        "Humidity", FloatRange(-8, 8), draft.HumidityOffset);
                }));
                items.Add(new WorldEditorItem("Biome size: " + FormatNumber(draft.BiomeSize) + "x", delegate
                {
                    draft.BiomeSize = PromptFloatChoice(
                        "Biome size", BiomeSizes, draft.BiomeSize, "x");
                }));
            }
            if (flat)
            {
                items.Add(new WorldEditorItem("Flat terrain level: " + draft.TerrainLevel, delegate
                {
                    draft.TerrainLevel = PromptInteger(
                        "Flat terrain level", draft.TerrainLevel, 2, 252);
                }));
                items.Add(new WorldEditorItem(
                    "Flat shore roughness: " + FormatNumber(draft.ShoreRoughness * 100f) + "%",
                    delegate
                    {
                        draft.ShoreRoughness = PromptFloatChoice(
                            "Flat shore roughness",
                            new[] { 0f, 0.25f, 0.5f, 0.75f, 1f },
                            draft.ShoreRoughness,
                            null,
                            100f,
                            "%");
                    }));
                items.Add(new WorldEditorItem(
                    "Flat terrain block: " + GetBlockLabel(draft.TerrainBlockIndex),
                    delegate { draft.TerrainBlockIndex = SelectTerrainBlock(draft.TerrainBlockIndex); }));
                items.Add(new WorldEditorItem(
                    "Magma ocean: " + FormatEnabled(draft.TerrainOceanBlockIndex == 92),
                    delegate { draft.TerrainOceanBlockIndex = ToggleBoolean("Magma ocean", draft.TerrainOceanBlockIndex == 92) ? 92 : 18; }));
            }

            items.Add(new WorldEditorItem(
                "Blocks texture: " + GetBlocksTextureLabel(draft.BlocksTextureName),
                delegate { draft.BlocksTextureName = SelectBlocksTexture(draft.BlocksTextureName); }));
            items.Add(new WorldEditorItem(
                "Customize paint colors...",
                delegate { EditPalette(draft); }));
            items.Add(new WorldEditorItem(
                "Changing seasons: " + FormatEnabled(draft.SeasonsChanging),
                delegate { draft.SeasonsChanging = ToggleBoolean("Changing seasons", draft.SeasonsChanging); }));
            if (draft.SeasonsChanging)
            {
                items.Add(new WorldEditorItem("Length of year: " + FormatNumber(draft.YearDays) + " days", delegate
                {
                    draft.YearDays = PromptFloatChoice(
                        "Length of year", YearDays, draft.YearDays, " days");
                }));
            }
            items.Add(new WorldEditorItem("Season: " + FormatTimeOfYear(draft.TimeOfYear), delegate
            {
                draft.TimeOfYear = PromptTimeOfYear(draft.TimeOfYear);
            }));
            items.Add(new WorldEditorItem(
                "Supernatural creatures: " + FormatEnabled(draft.SupernaturalCreatures),
                delegate { draft.SupernaturalCreatures = ToggleBoolean("Supernatural creatures", draft.SupernaturalCreatures); }));
            items.Add(new WorldEditorItem(
                "Player-on-player attacks: " + (draft.FriendlyFire ? "Allowed" : "Disallowed"),
                delegate { draft.FriendlyFire = ToggleBoolean("Player-on-player attacks", draft.FriendlyFire, "Allowed", "Disallowed"); }));

            if (draft.GameMode == "Creative")
            {
                items.Add(new WorldEditorItem("Environment behavior: " + draft.EnvironmentBehavior, delegate
                {
                    draft.EnvironmentBehavior = PromptChoice(
                        "Environment behavior",
                        new[] { "Living", "Static" },
                        draft.EnvironmentBehavior);
                }));
                items.Add(new WorldEditorItem("Time of day: " + draft.TimeOfDay, delegate
                {
                    draft.TimeOfDay = PromptChoice(
                        "Time of day",
                        new[] { "Changing", "Day", "Night", "Sunrise", "Sunset" },
                        draft.TimeOfDay);
                }));
                items.Add(new WorldEditorItem(
                    "Weather effects: " + FormatEnabled(draft.WeatherEffects),
                    delegate { draft.WeatherEffects = ToggleBoolean("Weather effects", draft.WeatherEffects); }));
                items.Add(new WorldEditorItem(
                    "Adventure respawn: " + (draft.AdventureRespawn ? "Allowed" : "Not allowed"),
                    delegate { draft.AdventureRespawn = ToggleBoolean("Adventure respawn", draft.AdventureRespawn, "Allowed", "Not allowed"); }));
                items.Add(new WorldEditorItem(
                    "Adventure survival mechanics: " + FormatEnabled(draft.AdventureSurvivalMechanics),
                    delegate { draft.AdventureSurvivalMechanics = ToggleBoolean("Adventure survival mechanics", draft.AdventureSurvivalMechanics); }));
            }

            items.Add(new WorldEditorItem("Create and load world", WorldEditorResult.Submit));
            return items;
        }

        private static Dictionary<string, object> BuildWorldCreateArguments(
            WorldCreationDraft draft)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = draft.Name,
                ["seed"] = draft.Seed,
                ["gameMode"] = draft.GameMode,
                ["startingPosition"] = draft.StartingPosition,
                ["terrainGeneration"] = draft.TerrainGeneration,
                ["environmentBehavior"] = draft.EnvironmentBehavior,
                ["timeOfDay"] = draft.TimeOfDay,
                ["weatherEffects"] = draft.WeatherEffects,
                ["supernaturalCreatures"] = draft.SupernaturalCreatures,
                ["friendlyFire"] = draft.FriendlyFire,
                ["seasonsChanging"] = draft.SeasonsChanging,
                ["seaLevelOffset"] = draft.SeaLevelOffset,
                ["temperatureOffset"] = draft.TemperatureOffset,
                ["humidityOffset"] = draft.HumidityOffset,
                ["biomeSize"] = draft.BiomeSize,
                ["yearDays"] = draft.YearDays,
                ["timeOfYear"] = draft.TimeOfYear,
                ["blocksTextureName"] = draft.BlocksTextureName,
                ["islandSizeEW"] = draft.IslandSizeEW,
                ["islandSizeNS"] = draft.IslandSizeNS,
                ["terrainLevel"] = draft.TerrainLevel,
                ["shoreRoughness"] = draft.ShoreRoughness,
                ["terrainBlockIndex"] = draft.TerrainBlockIndex,
                ["terrainOceanBlockIndex"] = draft.TerrainOceanBlockIndex,
                ["adventureRespawn"] = draft.AdventureRespawn,
                ["adventureSurvivalMechanics"] = draft.AdventureSurvivalMechanics,
                ["paletteColors"] = string.Join(";", draft.PaletteColors),
                ["paletteNames"] = string.Join(";", draft.PaletteNames)
            };
        }

        // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.Play
        private void JoinWorld()
        {
            Dictionary<string, object> world = SelectWorld("Load World");
            if (world == null)
                return;
            Dictionary<string, object> response = m_server.SubmitLocal(
                "world.join",
                Args("world", world["directoryName"]));
            PrintResponse(RequireSuccess(response));
            WaitForWorldReady(world["name"].ToString());
            Console.WriteLine("World loaded: " + world["name"] + ". Current screen: " +
                GetCurrentScreen());
            Pause();
        }

        private void ShowWorlds()
        {
            Dictionary<string, object> world = SelectWorld("Worlds");
            if (world != null)
                ShowWorldDetails(world);
        }

        private void ShowCurrentWorld()
        {
            Dictionary<string, object> current = GetLoadedWorld();

            if (current == null)
            {
                Console.Clear();
                Console.WriteLine(GetCurrentScreen() + "> Current World");
                Console.WriteLine(GetCurrentWorldMenuLabel());
                Pause();
                return;
            }

            ShowWorldDetails(current);
        }

        private string GetCurrentWorldMenuLabel()
        {
            try
            {
                // Source: HeadlessRenderingMod.cs:BuildStatus
                // The status snapshot is the authoritative loaded-world state and does not
                // rescan save directories while the game is loading.
                Dictionary<string, object> status = GetResult<Dictionary<string, object>>(
                    m_server.SubmitLocal("status"));
                bool loaded = status.TryGetValue("worldLoaded", out object loadedValue) &&
                    loadedValue is bool isLoaded && isLoaded;
                if (!loaded) return "Current World: <none>";
                if (status.TryGetValue("worldName", out object worldNameValue) &&
                    worldNameValue is string worldName && !string.IsNullOrWhiteSpace(worldName))
                    return "Current World: " + worldName;
                return "Current World: Loading...";
            }
            catch
            {
                return "Current World: Unavailable";
            }
        }

        private void ShowWorldDetails(Dictionary<string, object> world)
        {
            Console.Clear();
            bool loaded = world.TryGetValue("loaded", out object loadedValue) &&
                loadedValue is bool isLoaded && isLoaded;
            Console.WriteLine(GetCurrentScreen() + "> World: " + world["name"]);
            Console.WriteLine("Game mode: " + world["gameMode"]);
            Console.WriteLine("Terrain: " + world["terrainGeneration"]);
            Console.WriteLine("Saved players: " + world["players"]);
            Console.WriteLine("Directory: " + world["directoryName"]);
            Console.WriteLine("Status: " + (loaded ? "CURRENT / LOADED" : "Not loaded"));
            Console.WriteLine();

            if (!loaded)
            {
                Console.WriteLine("Online players are available only for the loaded world.");
                Pause();
                return;
            }

            Console.WriteLine("Players in current world:");
            List<Dictionary<string, object>> players = GetResult<List<Dictionary<string, object>>>(
                m_server.SubmitLocal("player.list"));
            if (players.Count == 0)
            {
                Console.WriteLine("  (none)");
            }
            else
            {
                foreach (Dictionary<string, object> player in players)
                {
                    bool spawned = player.TryGetValue("spawned", out object spawnedValue) &&
                        spawnedValue is bool isSpawned && isSpawned;
                    bool ready = player.TryGetValue("readyForPlaying", out object readyValue) &&
                        readyValue is bool isReady && isReady;
                    Console.WriteLine(
                        "  #" + player["playerIndex"] + " " + player["name"] +
                        " | " + player["playerClass"] +
                        " | " + player["skinDisplayName"] +
                        " | " + (spawned ? "Online" : "Saved") +
                        " | " + (ready ? "Ready" : "Loading"));
                }
            }
            int? action = SelectMenu("World Actions", new[] { "Edit game mode", "Back" }, 1);
            if (action == 0)
                ChangeWorldMode(world);
        }

        // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.Update
        private void ChangeCurrentWorldMode()
        {
            Dictionary<string, object> current = GetLoadedWorld();
            if (current == null)
            {
                Console.WriteLine("No world is currently loaded.");
                Pause();
                return;
            }
            ChangeWorldMode(current);
        }

        // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.Update
        private void ChangeWorldMode(Dictionary<string, object> world)
        {
            string gameMode = PromptChoice("Game mode", new[]
            {
                "Creative", "Harmless", "Challenging", "Survival", "Cruel", "Adventure"
            }, world["gameMode"].ToString());
            if (string.IsNullOrEmpty(gameMode)) return;
            Dictionary<string, object> response = RequireSuccess(m_server.SubmitLocal(
                "world.settings", new Dictionary<string, object>
                {
                    ["world"] = world["directoryName"],
                    ["gameMode"] = gameMode
                }));
            PrintResponse(response);
            string appliedWorldName = response.TryGetValue("worldName", out object worldNameValue)
                ? worldNameValue?.ToString()
                : world["name"].ToString();
            string appliedGameMode = response.TryGetValue("gameMode", out object gameModeValue)
                ? gameModeValue?.ToString()
                : gameMode;
            if (response.TryGetValue("reloading", out object reloading) && reloading is bool value && value)
                WaitForWorldReady(appliedWorldName);
            Console.WriteLine("Applied: " + appliedWorldName + " | Game mode: " +
                appliedGameMode);
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
                if (player.ContainsKey("networkKey"))
                {
                    ManageNetworkPlayer(player);
                    continue;
                }
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

        private void ManageNetworkPlayer(Dictionary<string, object> player)
        {
            string networkKey = player["networkKey"].ToString();
            string[] actions =
            {
                "Rename", "Change Skin", "Level", "Health", "Air", "Food",
                "Stamina", "Sleep", "Temperature", "Wetness", "Position",
                "Spawn Point", "Inventory Slot", "Handcraft Slot", "Clothing Slot",
                "Delete Player", "Back"
            };
            while (m_running)
            {
                int? action = SelectMenu(
                    player["name"] + " (network " + player["clientId"] + ")",
                    actions, actions.Length - 1);
                if (!action.HasValue || action.Value == actions.Length - 1)
                    return;
                Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["networkKey"] = networkKey
                };
                if (action.Value == 0)
                {
                    string name = PromptText("New player name", player["name"].ToString());
                    if (!IsValidPlayerName(name))
                        throw new InvalidOperationException("Invalid player name.");
                    values["name"] = name;
                }
                else if (action.Value == 1)
                {
                    Dictionary<string, object> skin = SelectSkin(
                        player["playerClass"].ToString(), "Change Network Skin");
                    if (skin == null) continue;
                    values["skin"] = skin["name"];
                }
                else if (action.Value >= 2 && action.Value <= 9)
                {
                    string field = new[]
                    {
                        "level", "health", "air", "food", "stamina", "sleep",
                        "temperature", "wetness"
                    }[action.Value - 2];
                    float current = Convert.ToSingle(player[field], CultureInfo.InvariantCulture);
                    float minimum = field == "level" ? 1f : 0f;
                    float maximum = field == "level" ? 1000000f :
                        field == "temperature" ? 24f : 1f;
                    values[field] = PromptFloat(field, current, minimum, maximum);
                }
                else if (action.Value == 10 || action.Value == 11)
                {
                    bool spawn = action.Value == 11;
                    string[] names = spawn
                        ? new[] { "spawnX", "spawnY", "spawnZ" }
                        : new[] { "x", "y", "z" };
                    string position = player[spawn ? "spawnPosition" : "position"].ToString();
                    string[] current = position.Split(',');
                    values[names[0]] = PromptFloat(names[0], ParsePosition(current, 0), -1000000f, 1000000f);
                    values[names[1]] = PromptFloat(names[1], ParsePosition(current, 1), 0f, 253f);
                    values[names[2]] = PromptFloat(names[2], ParsePosition(current, 2), -1000000f, 1000000f);
                }
                else if (action.Value == 12 || action.Value == 13)
                {
                    bool handcraft = action.Value == 13;
                    int slot = PromptInteger(handcraft ? "Handcraft slot" : "Inventory slot",
                        0, 0, 127);
                    values[handcraft ? "handcraftSlot" : "inventorySlot"] = slot;
                    values[handcraft ? "handcraftValue" : "inventoryValue"] =
                        PromptInteger("Item value", 0, 0, int.MaxValue);
                    values[handcraft ? "handcraftCount" : "inventoryCount"] =
                        PromptInteger("Item count", 0, 0, 9999);
                }
                else if (action.Value == 14)
                {
                    int slot = PromptInteger("Clothing slot", 0, 0, 3);
                    values["clothingSlot"] = slot;
                    values["clothingValues"] = PromptText(
                        "Clothing values", string.Empty, "comma-separated values");
                }
                else if (action.Value == 15)
                {
                    if (!PromptBoolean("Permanently delete '" + player["name"] + "'", false))
                        continue;
                    PrintResponse(RequireSuccess(m_server.SubmitLocal(
                        "player.delete", Args("networkKey", networkKey))));
                    Pause();
                    return;
                }
                PrintResponse(RequireSuccess(m_server.SubmitLocal("player.update", values)));
                Pause();
                player = FindNetworkPlayer(networkKey) ?? player;
            }
        }

        private Dictionary<string, object> FindNetworkPlayer(string networkKey)
        {
            List<Dictionary<string, object>> players = GetResult<List<Dictionary<string, object>>>(
                m_server.SubmitLocal("player.list"));
            return players.Find(item => item.TryGetValue("networkKey", out object value) &&
                string.Equals(value?.ToString(), networkKey, StringComparison.Ordinal));
        }

        private Dictionary<string, object> SelectWorld(string title)
        {
            List<Dictionary<string, object>> worlds = GetResult<List<Dictionary<string, object>>>(
                m_server.SubmitLocal("world.list"));
            if (worlds.Count == 0)
                throw new InvalidOperationException("No worlds are available.");
            // Source: HeadlessRenderingMod.cs:BuildStatus
            // Keep the active map first so a world-mode edit cannot be applied to a similarly
            // named inactive save by mistake.
            worlds.Sort((left, right) =>
            {
                bool leftLoaded = left["loaded"] is bool isLeftLoaded && isLeftLoaded;
                bool rightLoaded = right["loaded"] is bool isRightLoaded && isRightLoaded;
                if (leftLoaded != rightLoaded) return leftLoaded ? -1 : 1;
                return string.Compare(left["name"].ToString(), right["name"].ToString(),
                    StringComparison.OrdinalIgnoreCase);
            });
            List<string> labels = new List<string>();
            foreach (Dictionary<string, object> world in worlds)
            {
                bool loaded = world["loaded"] is bool isLoaded && isLoaded;
                labels.Add((loaded ? "CURRENT | " : string.Empty) + world["name"] + " | " +
                    world["gameMode"] +
                    " | players " + world["players"]);
            }
            int? selected = SelectMenu(title, labels.ToArray(), 0);
            return selected.HasValue ? worlds[selected.Value] : null;
        }

        // Source: HeadlessRenderingMod.cs:BuildStatus
        private Dictionary<string, object> GetLoadedWorld()
        {
            List<Dictionary<string, object>> worlds = GetResult<List<Dictionary<string, object>>>(
                m_server.SubmitLocal("world.list"));
            return worlds.Find(world =>
                world.TryGetValue("loaded", out object value) && value is bool loaded && loaded);
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

        private int? SelectMenu(string title, string[] items, int selected) =>
            Interactive(() => SelectMenuCore(title, items, selected));

        // SelectMenuCore 每次都清屏，所以"这一页的固定说明"必须由菜单自己印：notes 就是菜单
        // 标题下面那几行说明（GM 授权页用它写清"与能否加入房间无关"）。
        private int? SelectMenuWithNotes(string title, string[] notes, string[] items,
            int selected) =>
            Interactive(() => SelectMenuCore(title, items, selected, notes));

        private int? SelectMenuCore(string title, string[] items, int selected) =>
            SelectMenuCore(title, items, selected, null);

        private int? SelectMenuCore(string title, string[] items, int selected, string[] notes)
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
                if (notes != null)
                {
                    foreach (string note in notes)
                        Console.WriteLine(note);
                }
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

        private bool ToggleBoolean(
            string label,
            bool value,
            string trueLabel = "Enabled",
            string falseLabel = "Disabled")
        {
            string[] choices = { trueLabel, falseLabel };
            int? selected = SelectMenu(label, choices, value ? 0 : 1);
            return selected.HasValue ? selected.Value == 0 : value;
        }

        private float PromptFloatChoice(
            string label,
            float[] choices,
            float defaultValue,
            string suffix = null,
            float displayScale = 1f,
            string displaySuffix = null)
        {
            string[] labels = new string[choices.Length];
            int selectedIndex = 0;
            for (int i = 0; i < choices.Length; i++)
            {
                labels[i] = FormatNumber(choices[i] * displayScale) +
                    (displaySuffix ?? suffix ?? string.Empty);
                if (Math.Abs(choices[i] - defaultValue) <
                    Math.Abs(choices[selectedIndex] - defaultValue))
                {
                    selectedIndex = i;
                }
            }
            int? selected = SelectMenu(label, labels, selectedIndex);
            return selected.HasValue ? choices[selected.Value] : defaultValue;
        }

        private int PromptIntegerChoice(string label, int[] choices, int defaultValue)
        {
            string[] labels = new string[choices.Length];
            int selectedIndex = 0;
            for (int i = 0; i < choices.Length; i++)
            {
                labels[i] = choices[i] == 0 ? "Normal" :
                    (choices[i] > 0 ? "+" : string.Empty) + choices[i];
                if (Math.Abs(choices[i] - defaultValue) <
                    Math.Abs(choices[selectedIndex] - defaultValue))
                {
                    selectedIndex = i;
                }
            }
            int? selected = SelectMenu(label, labels, selectedIndex);
            return selected.HasValue ? choices[selected.Value] : defaultValue;
        }

        private int PromptInteger(
            string label,
            int defaultValue,
            int minimum,
            int maximum)
        {
            while (true)
            {
                string text = PromptText(label, defaultValue.ToString(CultureInfo.InvariantCulture));
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                    value >= minimum && value <= maximum)
                {
                    return value;
                }
                Console.WriteLine("Enter a whole number from " + minimum + " to " + maximum + ".");
            }
        }

        private static int[] IntegerRange(int minimum, int maximum)
        {
            int[] result = new int[maximum - minimum + 1];
            for (int i = 0; i < result.Length; i++)
                result[i] = minimum + i;
            return result;
        }

        private float PromptFloat(string label, float defaultValue,
            float minimum, float maximum)
        {
            while (true)
            {
                string text = PromptText(label,
                    defaultValue.ToString(CultureInfo.InvariantCulture));
                if (float.TryParse(text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float value) &&
                    float.IsFinite(value) && value >= minimum && value <= maximum)
                {
                    return value;
                }
                Console.WriteLine("Enter a number from " + minimum + " to " + maximum + ".");
            }
        }

        private static float ParsePosition(string[] values, int index)
        {
            return values != null && index >= 0 && index < values.Length &&
                float.TryParse(values[index], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float value)
                ? value : 0f;
        }

        private static float[] FloatRange(int minimum, int maximum)
        {
            float[] result = new float[maximum - minimum + 1];
            for (int i = 0; i < result.Length; i++)
                result[i] = minimum + i;
            return result;
        }

        private static string FormatNumber(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string FormatOffset(float value)
        {
            return Math.Abs(value) < 0.0001f
                ? "Normal"
                : (value > 0f ? "+" : string.Empty) + FormatNumber(value);
        }

        private static string FormatEnabled(bool enabled)
        {
            return enabled ? "Enabled" : "Disabled";
        }

        private static string FormatTimeOfYear(float value)
        {
            return SubsystemSeasons.GetTimeOfYearName(value) ?? FormatNumber(value);
        }

        private float PromptTimeOfYear(float defaultValue)
        {
            int selectedIndex = 0;
            for (int i = 1; i < TimeOfYearValues.Length; i++)
            {
                if (Math.Abs(TimeOfYearValues[i] - defaultValue) <
                    Math.Abs(TimeOfYearValues[selectedIndex] - defaultValue))
                {
                    selectedIndex = i;
                }
            }
            int? selected = SelectMenu("Season", TimeOfYearNames, selectedIndex);
            return selected.HasValue ? TimeOfYearValues[selected.Value] : defaultValue;
        }

        private static string GetBlockLabel(int blockIndex)
        {
            try
            {
                Block block = BlocksManager.Blocks[blockIndex];
                return block.GetDisplayName(null, Terrain.MakeBlockValue(blockIndex)) +
                    " (#" + blockIndex + ")";
            }
            catch
            {
                return "Block #" + blockIndex;
            }
        }

        private int SelectTerrainBlock(int defaultValue)
        {
            string[] labels = new string[FlatTerrainBlocks.Length];
            int selectedIndex = 0;
            for (int i = 0; i < FlatTerrainBlocks.Length; i++)
            {
                labels[i] = GetBlockLabel(FlatTerrainBlocks[i]);
                if (FlatTerrainBlocks[i] == defaultValue)
                    selectedIndex = i;
            }
            int? selected = SelectMenu("Flat terrain block", labels, selectedIndex);
            return selected.HasValue ? FlatTerrainBlocks[selected.Value] : defaultValue;
        }

        private static string GetBlocksTextureLabel(string textureName)
        {
            try
            {
                return BlocksTexturesManager.GetDisplayName(textureName) +
                    (string.IsNullOrEmpty(textureName) ? " 512x512" : string.Empty);
            }
            catch
            {
                return string.IsNullOrEmpty(textureName) ? "Survivalcraft" : textureName;
            }
        }

        private string SelectBlocksTexture(string defaultValue)
        {
            try
            {
                BlocksTexturesManager.UpdateBlocksTexturesList();
                List<string> names = new List<string>();
                List<string> labels = new List<string>();
                int selectedIndex = 0;
                foreach (string name in BlocksTexturesManager.BlockTexturesNames)
                {
                    if (string.Equals(name, defaultValue, StringComparison.OrdinalIgnoreCase))
                        selectedIndex = names.Count;
                    names.Add(name);
                    labels.Add(GetBlocksTextureLabel(name));
                }
                if (names.Count == 0)
                    return defaultValue;
                int? selected = SelectMenu("Blocks texture", labels.ToArray(), selectedIndex);
                return selected.HasValue ? names[selected.Value] : defaultValue;
            }
            catch
            {
                return PromptText("Blocks texture resource name", defaultValue, "Survivalcraft");
            }
        }

        private void EditPalette(WorldCreationDraft draft)
        {
            int selected = 0;
            while (m_running)
            {
                string[] labels = new string[WorldPalette.MaxColors];
                for (int i = 0; i < labels.Length; i++)
                {
                    string name = string.IsNullOrEmpty(draft.PaletteNames[i])
                        ? WorldPalette.DefaultNames[i]
                        : draft.PaletteNames[i];
                    string color = string.IsNullOrEmpty(draft.PaletteColors[i])
                        ? HumanReadableConverter.ConvertToString(WorldPalette.DefaultColors[i])
                        : draft.PaletteColors[i];
                    labels[i] = (i + 1) + ". " + name + " = " + color;
                }
                int? choice = SelectMenu("Customize Paint Colors", labels, selected);
                if (!choice.HasValue)
                    return;
                selected = choice.Value;
                string currentName = string.IsNullOrEmpty(draft.PaletteNames[selected])
                    ? WorldPalette.DefaultNames[selected]
                    : draft.PaletteNames[selected];
                string currentColor = string.IsNullOrEmpty(draft.PaletteColors[selected])
                    ? HumanReadableConverter.ConvertToString(WorldPalette.DefaultColors[selected])
                    : draft.PaletteColors[selected];
                string newName = PromptText("Color name", currentName);
                if (!WorldPalette.VerifyColorName(newName))
                    throw new InvalidOperationException(
                        "Color name must contain 1-16 letters, digits, spaces or hyphens.");
                string newColor = PromptText("Color (#RRGGBB or R,G,B)", currentColor);
                if (!HumanReadableConverter.TryConvertFromString(newColor, out Color parsedColor))
                    throw new InvalidOperationException("Invalid color value.");
                draft.PaletteNames[selected] = newName == WorldPalette.DefaultNames[selected]
                    ? string.Empty
                    : newName;
                draft.PaletteColors[selected] = parsedColor == WorldPalette.DefaultColors[selected]
                    ? string.Empty
                    : HumanReadableConverter.ConvertToString(parsedColor);
            }
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

        // Source: HeadlessRenderingMod.cs:BuildStatus
        private void WaitForWorldReady(string expectedWorldName = null)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(180);
            while (m_running && DateTime.UtcNow < deadline)
            {
                Dictionary<string, object> status = GetResult<Dictionary<string, object>>(
                    m_server.SubmitLocal("status"));
                bool loaded = status.TryGetValue("worldLoaded", out object worldLoaded) &&
                    worldLoaded is bool loadedValue && loadedValue;
                string actualWorldName = status.TryGetValue("worldName", out object worldName)
                    ? worldName as string
                    : null;
                bool hasWorldName = !string.IsNullOrWhiteSpace(actualWorldName);
                bool expectedWorldLoaded = string.IsNullOrWhiteSpace(expectedWorldName) ||
                    (hasWorldName && string.Equals(actualWorldName, expectedWorldName,
                        StringComparison.OrdinalIgnoreCase));
                string screen = status.TryGetValue("currentScreen", out object currentScreen)
                    ? currentScreen?.ToString()
                    : null;
                if (loaded && hasWorldName && expectedWorldLoaded &&
                    !string.Equals(screen, "GameLoading", StringComparison.Ordinal))
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

        // Source: ScMultiplayer/Func/Server/ScMultiplayerSettings.cs:ScMultiplayerSettings.Save
        private void ConfigureMultiplayerHosting()
        {
            int selected = 0;
            while (m_running)
            {
                Dictionary<string, object> settings = GetMultiplayerSettings();
                bool autoHost = ReadBoolean(settings, "autoCreateRoomFromCurrentWorld", false);
                bool autoApprove = ReadBoolean(settings, "autoApproveJoinRequests", false);
                bool diagnostics = ReadBoolean(settings, "serverDiagnosticsEnabled", false);
                string dataMode = ReadString(settings, "dataModificationMode", "default");
                int? choice = SelectMenu("Multiplayer Hosting",
                    new[]
                    {
                        "Auto host loaded world [" + (autoHost ? "On" : "Off") + "]",
                        "Auto approve joins [" + (autoApprove ? "On" : "Off") + "]",
                        "Server diagnostics [" + (diagnostics ? "On" : "Off") + "]",
                        "Data modification [" + FormatDataModificationMode(dataMode) + "]",
                        "Back"
                    }, selected);
                if (!choice.HasValue || choice.Value == 4)
                    return;
                selected = choice.Value;
                if (selected == 3)
                {
                    ConfigureDataModification();
                    selected = 0;
                    continue;
                }
                string name = selected == 0 ? "autoCreateRoomFromCurrentWorld" :
                    selected == 1 ? "autoApproveJoinRequests" :
                    "serverDiagnosticsEnabled";
                bool value = selected == 0 ? !autoHost :
                    selected == 1 ? !autoApprove : !diagnostics;
                RequireSuccess(m_server.SubmitLocal("multiplayer.settings",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [name] = value
                    }));
            }
        }

        private void ConfigureDataModification()
        {
            Dictionary<string, object> settings = GetMultiplayerSettings();
            int pendingApprovals = ReadInteger(settings,
                "pendingDataModificationApprovals");
            int decisionCount = m_dataModificationFeed?.Snapshot().Count ?? 0;
            DataModificationFeed.Entry latestDecision = m_dataModificationFeed?.Latest();
            int? setup = SelectMenu("Data Modification",
                new[]
                {
                    "View current configuration",
                    "Simple setup (recommended)",
                    "Professional settings",
                    "Pending approvals [" + pendingApprovals + "]",
                    // 父节点只显示计数；"last: …" 摘要挪进 Recent decisions 子页顶部。
                    "Recent decisions [" + decisionCount + "]",
                    "GM / data-modification authorisations [" + CountAuthorisedPlayers() + "]",
                    "Back"
                }, 0);
            if (!setup.HasValue || setup.Value == 6)
                return;
            if (setup.Value == 0)
            {
                Console.Clear();
                Console.WriteLine(GetCurrentScreen() + "> Data Modification");
                Console.WriteLine("Mode: " + FormatDataModificationMode(
                    ReadString(settings, "dataModificationMode", "default")));
                Console.WriteLine("Fast channels: " + ReadInteger(settings,
                    "dataModificationFastMaxConcurrent"));
                Console.WriteLine("Bulk channels: " + ReadInteger(settings,
                    "dataModificationBulkMaxConcurrent"));
                Console.WriteLine("Bulk chunks/frame: " + ReadInteger(settings,
                    "dataModificationBulkApplyChunksPerFrame"));
                Console.WriteLine("Bulk bytes/frame: " + ReadInteger(settings,
                    "dataModificationBulkApplyBytesPerFrame"));
                Console.WriteLine("Auto approve allowlist: " + DescribeAutoApproveAllowlist());
            }
            else if (setup.Value == 1)
            {
                string mode = PromptChoice("Data modification mode",
                    new[] { "Reject", "Default (ask host)", "Allow" },
                    FormatDataModificationMode(ReadString(settings,
                        "dataModificationMode", "default")));
                var values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["dataModificationMode"] = mode.StartsWith("Reject",
                        StringComparison.OrdinalIgnoreCase) ? "reject" :
                        mode.StartsWith("Allow", StringComparison.OrdinalIgnoreCase)
                            ? "allow" : "default",
                    ["dataModificationFastMaxConcurrent"] = PromptInteger(
                        "Fast DM channels", ReadInteger(settings,
                            "dataModificationFastMaxConcurrent"), 1, 128),
                    ["dataModificationBulkMaxConcurrent"] = PromptInteger(
                        "Bulk DM channels", ReadInteger(settings,
                            "dataModificationBulkMaxConcurrent"), 1, 32)
                };
                string budget = PromptChoice("Bulk DM frame budget",
                    new[] { "Low (4 chunks / 8 KiB)", "Balanced (8 chunks / 32 KiB)",
                        "High (32 chunks / 128 KiB)" }, "Balanced");
                values["dataModificationBulkApplyChunksPerFrame"] = budget.StartsWith("Low",
                    StringComparison.OrdinalIgnoreCase) ? 4 : budget.StartsWith("High",
                        StringComparison.OrdinalIgnoreCase) ? 32 : 8;
                values["dataModificationBulkApplyBytesPerFrame"] = budget.StartsWith("Low",
                    StringComparison.OrdinalIgnoreCase) ? 8 * 1024 : budget.StartsWith("High",
                        StringComparison.OrdinalIgnoreCase) ? 128 * 1024 : 32 * 1024;
                PrintResponse(RequireSuccess(m_server.SubmitLocal(
                    "multiplayer.settings", values)));
            }
            else if (setup.Value == 2)
            {
                ConfigureAdvancedDataModification(settings);
            }
            else if (setup.Value == 3)
            {
                ManageDataModificationApprovals();
                return;
            }
            else if (setup.Value == 4)
            {
                ShowDataModificationDecisions(latestDecision);
                return;
            }
            else
            {
                ShowDataModificationAuthorisation();
                return;
            }
            Pause();
        }

        /// <summary>
        /// 主机侧 DM 决策记录：审批请求、server.json 白名单自动同意（AutoApproved）、
        /// 控制台里手动允许/拒绝、以及收到的 Result 回执。远端只有控制台，这是唯一能直接
        /// 看到"请求是不是被自动同意了、后来怎么了"的地方。
        /// </summary>
        /// <param name="lastDecision">
        /// 父菜单里只显示计数的"最新一条"，摘要在这里作为子页顶部高亮印出来。
        /// </param>
        private void ShowDataModificationDecisions(DataModificationFeed.Entry lastDecision)
        {
            List<DataModificationFeed.Entry> decisions =
                m_dataModificationFeed?.Snapshot() ?? new List<DataModificationFeed.Entry>();
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> Data Modification Decisions");
            Console.WriteLine("Auto approve allowlist: " + DescribeAutoApproveAllowlist());
            // 最近一条的高亮行（父节点现在只显示计数）。
            Console.WriteLine("Last: " + (lastDecision == null
                ? "none yet"
                : lastDecision.Describe()));
            Console.WriteLine();
            if (decisions.Count == 0)
            {
                Console.WriteLine("Nothing recorded on this server yet.");
                Pause();
                return;
            }
            Console.WriteLine("Newest first (" + decisions.Count + " of the last " +
                DataModificationFeed.Capacity + "):");
            Console.WriteLine();
            foreach (DataModificationFeed.Entry decision in decisions)
                Console.WriteLine("  " + decision.Describe());
            Console.WriteLine();
            Console.WriteLine("AutoApproveQueued/AutoApproved = server.json allowlist,");
            Console.WriteLine("ManualAllowed/ManualRejected = decided in this menu,");
            Console.WriteLine("ManualTrusted = \"always allow\" in this menu (world trusted list),");
            Console.WriteLine("Result = receipt published by ScMultiplayer.");
            Console.WriteLine("Who holds GM / data-modification permission is on the");
            Console.WriteLine("\"GM / data-modification authorisations\" page (joining a room is separate).");
            Pause();
        }

        private string DescribeAutoApproveAllowlist()
        {
            string[] userIds = m_config?.AutoApproveDataModificationUserIds;
            if (userIds == null || userIds.Length == 0)
                return "not configured (server.json autoApproveDataModificationUserIds)";
            return userIds.Length + (userIds.Length == 1 ? " entry: " : " entries: ") +
                string.Join(", ", userIds);
        }

        /// <summary>已授权身份个数（server.json 白名单 + 世界受信任名单，按身份去重）。</summary>
        private int CountAuthorisedPlayers()
        {
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string identity in m_config?.AutoApproveDataModificationUserIds ??
                Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(identity))
                    identities.Add(identity);
            }
            foreach (string identity in ReadTrustedIdentities(TryGetDataModificationStatus()))
            {
                if (!string.IsNullOrWhiteSpace(identity))
                    identities.Add(identity);
            }
            return identities.Count;
        }

        /// <summary>
        /// "谁拿到 GM / 数据修改权限了"：server.json 白名单 + 世界受信任名单 + 在线客户端身份，
        /// 并且可以直接在这里**撤销 / 授予**（不再是纯打印）。
        /// 两者的区别：白名单的请求**仍然会到主机**，由无头服务器自动同意（Recent decisions 里看得到）；
        /// 世界受信任名单（"总是同意该玩家"）的请求**连审批请求都不产生**，主机直接落地。
        ///
        /// ⚠️ 这一页**只**管数据修改（GM）权限，与"能不能进房间"完全无关：
        /// 加入房间由上游 ScMultiplayerSettings.autoApproveJoinRequests
        /// （Multiplayer Hosting → "Auto approve joins"）控制。
        /// </summary>
        private void ShowDataModificationAuthorisation()
        {
            int selected = 0;
            while (m_running)
            {
                Dictionary<string, object> settings = GetMultiplayerSettings();
                Dictionary<string, object> status = TryGetDataModificationStatus();
                List<string> trustedIdentities = ReadTrustedIdentities(status);
                List<Dictionary<string, object>> clients = ReadClientIdentities(status);
                string[] allowlist = m_config?.AutoApproveDataModificationUserIds ??
                    Array.Empty<string>();
                string mode = ReadString(settings, "dataModificationMode", "default");

                // 菜单条目和"选中后干什么"分开存：labels 给菜单显示，kinds/keys 决定动作。
                var labels = new List<string>();
                var kinds = new List<string>();
                var keys = new List<string>();

                labels.Add("server.json allowlist (autoApproveDataModificationUserIds) [" +
                    allowlist.Length + "]");
                kinds.Add("header");
                keys.Add(string.Empty);
                if (allowlist.Length == 0)
                {
                    labels.Add("  (empty - nobody is auto approved)");
                    kinds.Add("header");
                    keys.Add(string.Empty);
                }
                foreach (string identity in allowlist)
                {
                    labels.Add("  - " + (identity == "*"
                        ? "*   [every client]"
                        : DescribeIdentity(identity, clients)) + "   (select to remove)");
                    kinds.Add("allowlist");
                    keys.Add(identity);
                }

                labels.Add("world trusted list (ScMultiplayerTrustedClients.xml) [" +
                    trustedIdentities.Count + "]");
                kinds.Add("header");
                keys.Add(string.Empty);
                if (trustedIdentities.Count == 0)
                {
                    labels.Add("  (nobody is trusted in this world)");
                    kinds.Add("header");
                    keys.Add(string.Empty);
                }
                foreach (string identity in trustedIdentities)
                {
                    labels.Add("  - " + DescribeIdentity(identity, clients) +
                        "   (select to revoke GM permission)");
                    kinds.Add("trusted");
                    keys.Add(identity);
                }

                labels.Add("online clients [" + clients.Count + "]");
                kinds.Add("header");
                keys.Add(string.Empty);
                if (clients.Count == 0)
                {
                    labels.Add("  (no remote client is connected)");
                    kinds.Add("header");
                    keys.Add(string.Empty);
                }
                foreach (Dictionary<string, object> client in clients)
                {
                    string clientKey = ReadString(client, "key", string.Empty);
                    bool trusted = client.TryGetValue("trusted", out object trustedValue) &&
                        trustedValue is bool trustedFlag && trustedFlag;
                    string verdict = trusted ? "GM granted (world trusted list)"
                        : IsAllowlisted(allowlist, clientKey)
                            ? "GM granted (server.json allowlist)"
                            : "not granted - asks for approval";
                    labels.Add("  client " + ReadInteger(client, "clientId") + "  " +
                        ReadString(client, "name", "Player") + "  " +
                        (string.IsNullOrEmpty(clientKey) ? "<no identity>" : clientKey) +
                        "  -> " + verdict + "   (select to grant GM)");
                    kinds.Add("client");
                    keys.Add(clientKey);
                }

                labels.Add("Back");
                kinds.Add("back");
                keys.Add(string.Empty);

                int? choice = SelectMenuWithNotes(
                    "Data Modification - GM / data-modification authorisations",
                    new[]
                    {
                        "Mode: " + FormatDataModificationMode(mode) +
                            DescribeDataModificationMode(mode),
                        "Pending approvals: " + ReadInteger(settings,
                            "pendingDataModificationApprovals"),
                        "This page only controls GM (data modification) permission.",
                        "It does NOT control who may join the room - joining is controlled",
                        "upstream by Multiplayer Hosting > \"Auto approve joins\".",
                        "allowlist: requests still reach the host, then are approved automatically.",
                        "world trusted: no approval request is created at all; applied directly."
                    },
                    labels.ToArray(), selected);
                if (!choice.HasValue)
                    return;
                selected = choice.Value;
                string kind = kinds[selected];
                string key = keys[selected];
                if (kind == "back")
                    return;
                if (kind == "header")
                {
                    Console.WriteLine();
                    Console.WriteLine("That line is a section heading, not an entry.");
                    Pause();
                    continue;
                }
                if (kind == "allowlist")
                {
                    RemoveAllowlistIdentity(key, allowlist);
                    continue;
                }
                if (kind == "trusted")
                {
                    RevokeWorldTrust(key);
                    continue;
                }
                GrantAllowlistIdentity(key);
            }
        }

        /// <summary>
        /// 从 server.json 白名单移除一个身份（TryRemoveAutoApproveUserId 会立即覆盖写盘）。
        /// </summary>
        private void RemoveAllowlistIdentity(string identity, string[] allowlist)
        {
            Console.WriteLine();
            string error;
            if (m_config.TryRemoveAutoApproveUserId(identity, out error))
            {
                Console.WriteLine("Removed from the GM allowlist: " + identity);
                RecordAuthorisationChange("ManualAllowlistRemoved", identity,
                    "removed from server.json autoApproveDataModificationUserIds");
                if (identity == "*")
                {
                    Console.WriteLine("The \"*\" entry itself is gone - every client falls back to");
                    Console.WriteLine("asking for approval (the individual entries above still apply).");
                }
                else if (Array.IndexOf(allowlist, "*") >= 0)
                {
                    // 注意别用 IsAllowlisted 判：传进来的 allowlist 是"删之前"的快照，
                    // 里面还带着刚删掉的那条，会永远命中。
                    Console.WriteLine("Note: the \"*\" entry is still in the list, so this identity");
                    Console.WriteLine("keeps being approved automatically.");
                }
            }
            else
            {
                Console.WriteLine("Could not remove \"" + identity + "\" from the GM allowlist: " +
                    error);
                RecordAuthorisationChange("ManualAllowlistRemoveFailed", identity, error);
            }
            Pause();
        }

        /// <summary>
        /// 在线客户端 → 授予 GM / 数据修改权限：写进 server.json 白名单并立即写盘。
        /// 它的请求之后仍会到主机，由无头服务器自动同意（Recent decisions 里看得到）。
        /// 只动数据修改权限，不碰任何"能否加入房间"的开关。
        /// </summary>
        private void GrantAllowlistIdentity(string identity)
        {
            Console.WriteLine();
            if (string.IsNullOrEmpty(identity))
            {
                Console.WriteLine("This client has no identity key yet, so it cannot go on the GM");
                Console.WriteLine("allowlist. Let it connect with an account identity first.");
                RecordAuthorisationChange("ManualAllowlistGrantFailed", identity,
                    "client has no identity key yet");
                Pause();
                return;
            }
            string error;
            if (m_config.TryAddAutoApproveUserId(identity, out error))
            {
                Console.WriteLine("Granted GM / data-modification permission to \"" + identity + "\".");
                Console.WriteLine("Written to server.json autoApproveDataModificationUserIds.");
                RecordAuthorisationChange("ManualAllowlistGranted", identity,
                    "added to server.json autoApproveDataModificationUserIds");
            }
            else
            {
                Console.WriteLine("Could not grant GM permission to \"" + identity + "\": " + error);
                RecordAuthorisationChange("ManualAllowlistGrantFailed", identity, error);
            }
            Pause();
        }

        /// <summary>
        /// 取消世界受信任名单里的 GM 授权：operation=untrust（按身份键）。
        /// Source: HeadlessRenderingMod.cs:ControlDataModificationApprovals
        /// </summary>
        private void RevokeWorldTrust(string identity)
        {
            Console.WriteLine();
            try
            {
                Dictionary<string, object> response = RequireSuccess(m_server.SubmitLocal(
                    "multiplayer.dm",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["operation"] = "untrust",
                        ["identity"] = identity
                    }));
                PrintResponse(response);
                Console.WriteLine("Revoked GM / data-modification permission for \"" + identity +
                    "\" (removed from the world trusted list).");
                RecordAuthorisationChange("ManualUntrusted", identity,
                    "removed from the world trusted list");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not revoke GM permission for \"" + identity + "\": " +
                    ex.Message);
                RecordAuthorisationChange("ManualUntrustFailed", identity, ex.Message);
            }
            Pause();
        }

        // Source: Mod/HeadlessRenderingMod/Server/DataModificationFeed.cs:DataModificationFeed.Entry
        // 授权页的"授予 GM / 移除白名单 / 取消世界受信"也要进 Recent decisions：与「Always allow」
        // 那条同口径。否则 Recent decisions 里只看得到待审批的处置记录，
        // 审计上会缺"谁被授权过、谁被撤销过"这一块。
        private void RecordAuthorisationChange(string code, string identity, string details)
        {
            m_dataModificationFeed?.Add(new DataModificationFeed.Entry
            {
                Kind = "manual",
                Code = code,
                ModId = "HeadlessRenderingMod",
                Operation = "authorisation",
                SourceKey = identity ?? string.Empty,
                Details = details ?? string.Empty
            });
        }

        private string DescribeIdentity(string identity,
            List<Dictionary<string, object>> clients)
        {
            foreach (Dictionary<string, object> client in clients)
            {
                if (!string.IsNullOrEmpty(identity) &&
                    string.Equals(ReadString(client, "key", string.Empty), identity,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return identity + "   [online: client " + ReadInteger(client, "clientId") +
                        " " + ReadString(client, "name", "Player") + "]";
                }
            }
            DataModificationFeed.Entry seen = FindLastSeenIdentity(identity);
            return identity + (seen == null
                ? "   [not seen online since this server started]"
                : "   [last request " + seen.Time + " from client " + seen.SourceClientId + "]");
        }

        private DataModificationFeed.Entry FindLastSeenIdentity(string identity)
        {
            if (m_dataModificationFeed == null || string.IsNullOrEmpty(identity))
                return null;
            foreach (DataModificationFeed.Entry entry in m_dataModificationFeed.Snapshot())
            {
                if (entry.SourceKey.Length > 0 &&
                    string.Equals(entry.SourceKey, identity, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }

        private static bool IsAllowlisted(string[] allowlist, string identity)
        {
            if (string.IsNullOrEmpty(identity))
                return false;
            foreach (string entry in allowlist)
            {
                if (entry == "*" ||
                    string.Equals(entry, identity, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string DescribeDataModificationMode(string mode)
        {
            if (string.Equals(mode, "allow", StringComparison.OrdinalIgnoreCase))
                return " - every request is approved automatically";
            if (string.Equals(mode, "reject", StringComparison.OrdinalIgnoreCase))
                return " - every request is refused";
            return " - GM-authorised identities are approved automatically; everyone else waits " +
                "in Pending approvals";
        }

        // Source: HeadlessRenderingMod.cs:ControlDataModificationApprovals
        // 主机侧 DM 状态（pending / trusted / clients）。ScMultiplayer 没装或没世界时返回空表，
        // 让这个界面仍然能显示 server.json 白名单。
        private Dictionary<string, object> TryGetDataModificationStatus()
        {
            try
            {
                Dictionary<string, object> response = RequireSuccess(
                    m_server.SubmitLocal("multiplayer.dm"));
                if (response.TryGetValue("result", out object result) &&
                    result is Dictionary<string, object> data)
                {
                    return data;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[HeadlessRenderingMod] Data modification status unavailable: " +
                    ex.Message);
            }
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        private static List<string> ReadTrustedIdentities(Dictionary<string, object> status)
        {
            if (status.TryGetValue("trusted", out object value) &&
                value is List<string> trusted)
            {
                return trusted;
            }
            return new List<string>();
        }

        private static List<Dictionary<string, object>> ReadClientIdentities(
            Dictionary<string, object> status)
        {
            if (status.TryGetValue("clients", out object value) &&
                value is List<Dictionary<string, object>> clients)
            {
                return clients;
            }
            return new List<Dictionary<string, object>>();
        }

        private void ManageDataModificationApprovals()
        {
            while (m_running)
            {
                List<Dictionary<string, object>> approvals =
                    GetDataModificationApprovals();
                if (approvals.Count == 0)
                {
                    Console.Clear();
                    Console.WriteLine(GetCurrentScreen() + "> Data Modification Approvals");
                    Console.WriteLine("No pending data modification requests.");
                    Console.WriteLine("Decisions already made are listed under Recent decisions.");
                    Console.WriteLine("GM / data-modification authorisations are on the");
                    Console.WriteLine("\"GM / data-modification authorisations\" page.");
                    Pause();
                    return;
                }
                string[] entries = approvals.Select(item =>
                    ReadString(item, "modId", "Mod") + " / " +
                    ReadString(item, "operation", "operation") + " / client " +
                    ReadInteger(item, "sourceClientId") + " / " +
                    ReadString(item, "channel", "fast")).Concat(new[] { "Back" }).ToArray();
                int? selected = SelectMenu("Data Modification Approvals", entries, 0);
                if (!selected.HasValue || selected.Value >= approvals.Count)
                    return;
                Dictionary<string, object> approval = approvals[selected.Value];
                string decision = PromptChoice("Decision",
                    new[]
                    {
                        "Allow",
                        "Reject",
                        // 第 4 项：把该玩家写进世界受信任名单（GM / 数据修改权限），之后不再产生审批请求。
                        "Always allow this player (grant GM / data-modification permission)",
                        "Back"
                    }, "Reject");
                if (decision.StartsWith("Back", StringComparison.OrdinalIgnoreCase))
                    continue;
                // 注意 "Always allow…" 并不是以 "Allow" 开头的，放行判定要把这一项显式算进去。
                bool alwaysAllow = decision.StartsWith("Always allow",
                    StringComparison.OrdinalIgnoreCase);
                if (alwaysAllow)
                {
                    // 先 trust 再 resolve：resolve 之后这条 pending 就消失了，trust 要按 pending
                    // 记录里的 sourceClientId / sourceKey 发。授权失败就不做本次 resolve，
                    // 让这条请求留在 Pending approvals 里，操作员能看出"权限没给成"并重试。
                    if (!GrantWorldTrust(approval))
                        continue;
                }
                var values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["operation"] = "resolve",
                    ["sourceClientId"] = ReadInteger(approval, "sourceClientId"),
                    ["requestId"] = ReadInteger(approval, "requestId"),
                    ["transferId"] = ReadInteger(approval, "transferId"),
                    ["allow"] = alwaysAllow || decision.StartsWith("Allow",
                        StringComparison.OrdinalIgnoreCase)
                };
                Dictionary<string, object> resolution = RequireSuccess(
                    m_server.SubmitLocal("multiplayer.dm", values));
                PrintResponse(resolution);
                bool allowed = Convert.ToBoolean(values["allow"], CultureInfo.InvariantCulture);
                bool resolved = resolution.TryGetValue("result", out object resolutionValue) &&
                    resolutionValue is Dictionary<string, object> resolutionData &&
                    resolutionData.TryGetValue("resolved", out object resolvedValue) &&
                    resolvedValue is bool resolvedFlag && resolvedFlag;
                m_dataModificationFeed?.Add(new DataModificationFeed.Entry
                {
                    Kind = "manual",
                    Code = !resolved ? "ManualFailed"
                        : allowed ? "ManualAllowed" : "ManualRejected",
                    ModId = ReadString(approval, "modId", "Mod"),
                    Operation = ReadString(approval, "operation", "operation"),
                    SourceClientId = ReadInteger(approval, "sourceClientId"),
                    RequestId = ReadInteger(approval, "requestId"),
                    SourceKey = ReadString(approval, "sourceKey", string.Empty),
                    Details = resolved ? "decided in the console menu"
                        : "the host did not resolve this approval"
                });
            }
        }

        /// <summary>
        /// "总是允许该玩家"：把发起这条 DM 请求的客户端写进世界受信任名单（GM / 数据修改权限），
        /// 之后它的请求**连审批请求都不产生**。必须先 trust 再 resolve —— resolve 之后这条 pending
        /// 就没了，两个参数都取自 pending 记录。
        ///
        /// ⚠️ 这里**只**动数据修改（GM）权限，不碰任何"能否加入房间"的开关。
        /// 返回 false 表示授权没成功（本次请求也不该 resolve），成功/失败都会写一条决策记录。
        /// </summary>
        private bool GrantWorldTrust(Dictionary<string, object> approval)
        {
            var trustValues = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["operation"] = "trust",
                ["sourceClientId"] = ReadInteger(approval, "sourceClientId"),
                ["sourceKey"] = ReadString(approval, "sourceKey", string.Empty)
            };
            string identity = ReadString(approval, "sourceKey", string.Empty);
            string who = string.IsNullOrEmpty(identity)
                ? "client " + ReadInteger(approval, "sourceClientId")
                : identity;
            Console.WriteLine();
            bool trusted = false;
            string details;
            try
            {
                Dictionary<string, object> trustResponse = RequireSuccess(
                    m_server.SubmitLocal("multiplayer.dm", trustValues));
                PrintResponse(trustResponse);
                trusted = true;
                details = "granted GM / data-modification permission (world trusted list)";
            }
            catch (Exception ex)
            {
                details = "could not grant GM / data-modification permission: " + ex.Message;
            }
            Console.WriteLine(trusted
                ? "Granted \"always allow\" (world trusted list) to " + who + "."
                : "Failed to grant \"always allow\" to " + who + ": " + details);
            m_dataModificationFeed?.Add(new DataModificationFeed.Entry
            {
                Kind = "manual",
                Code = trusted ? "ManualTrusted" : "ManualTrustFailed",
                ModId = ReadString(approval, "modId", "Mod"),
                Operation = ReadString(approval, "operation", "operation"),
                SourceClientId = ReadInteger(approval, "sourceClientId"),
                RequestId = ReadInteger(approval, "requestId"),
                SourceKey = ReadString(approval, "sourceKey", string.Empty),
                Details = details
            });
            return trusted;
        }

        private List<Dictionary<string, object>> GetDataModificationApprovals()
        {
            Dictionary<string, object> response = RequireSuccess(
                m_server.SubmitLocal("multiplayer.dm"));
            if (response.TryGetValue("result", out object result) &&
                result is Dictionary<string, object> data &&
                data.TryGetValue("pending", out object pending) &&
                pending is List<Dictionary<string, object>> approvals)
                return approvals;
            return new List<Dictionary<string, object>>();
        }

        private void ConfigureAdvancedDataModification(Dictionary<string, object> settings)
        {
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> Professional Data Modification");
            string mode = PromptChoice("Data modification mode",
                new[] { "Reject", "Default (ask host)", "Allow" },
                FormatDataModificationMode(ReadString(settings,
                    "dataModificationMode", "default")));
            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["dataModificationMode"] = mode.StartsWith("Reject",
                    StringComparison.OrdinalIgnoreCase) ? "reject" :
                    mode.StartsWith("Allow", StringComparison.OrdinalIgnoreCase)
                        ? "allow" : "default",
                ["dataModificationFastMaxConcurrent"] = PromptInteger(
                    "Fast DM channels", ReadInteger(settings,
                        "dataModificationFastMaxConcurrent"), 1, 128),
                ["dataModificationBulkMaxConcurrent"] = PromptInteger(
                    "Bulk DM channels", ReadInteger(settings,
                        "dataModificationBulkMaxConcurrent"), 1, 32),
                ["dataModificationBulkApplyChunksPerFrame"] = PromptInteger(
                    "Bulk chunks per frame", ReadInteger(settings,
                        "dataModificationBulkApplyChunksPerFrame"), 1, 128),
                ["dataModificationBulkApplyBytesPerFrame"] = PromptInteger(
                    "Bulk bytes per frame", ReadInteger(settings,
                        "dataModificationBulkApplyBytesPerFrame"), 1024, 1048576)
            };
            PrintResponse(RequireSuccess(m_server.SubmitLocal("multiplayer.settings", values)));
        }

        private Dictionary<string, object> GetMultiplayerSettings()
        {
            Dictionary<string, object> response = RequireSuccess(
                m_server.SubmitLocal("multiplayer.settings"));
            if (!(response.TryGetValue("result", out object result) &&
                result is Dictionary<string, object> settings))
            {
                throw new InvalidOperationException("Multiplayer settings response is invalid.");
            }
            return settings;
        }

        private void ConfigureMultiplayerBandwidth()
        {
            Dictionary<string, object> settings = GetMultiplayerSettings();

            string mode = settings.TryGetValue("bandwidthMode", out object modeValue) &&
                string.Equals(modeValue?.ToString(), "separate", StringComparison.OrdinalIgnoreCase)
                ? "separate" : "shared";
            bool configurationEnabled = ReadBoolean(settings,
                "bandwidthConfigurationEnabled", false);
            int? setup = SelectMenu("Multiplayer Bandwidth",
                new[]
                {
                    "View current configuration",
                    "Simple setup (recommended)",
                    "Advanced settings",
                    "Back"
                }, 0);
            if (!setup.HasValue || setup.Value == 3) return;
            if (setup.Value == 0)
                ShowMultiplayerBandwidthConfiguration(settings, mode, configurationEnabled);
            else if (setup.Value == 1)
                ConfigureSimpleBandwidth(settings, mode, configurationEnabled);
            else
                ConfigureAdvancedBandwidth(settings, mode);
            Pause();
        }

        // Source: ScMultiplayer/Func/Server/ScMultiplayerSettings.cs:HandleServerSettings
        private void ShowMultiplayerBandwidthConfiguration(
            Dictionary<string, object> settings, string mode, bool configurationEnabled)
        {
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> Multiplayer Bandwidth");
            Console.WriteLine();
            Console.WriteLine("Mode: " + (configurationEnabled ? "Configured" : "Automatic"));
            Console.WriteLine("Bandwidth scheme: " + (mode == "separate"
                ? "Separate upload / download" : "Shared total"));
            if (!configurationEnabled)
            {
                Console.WriteLine("Saved caps are inactive. Automatic adapts join transfer to available capacity.");
            }
            Console.WriteLine();
            Console.WriteLine("Shared total safe cap (Kbps): " + ReadInteger(settings,
                "sharedTotalSafeCapKbps"));
            Console.WriteLine("Upload safe cap (Kbps): " + ReadInteger(settings,
                "serverUploadLimitKbps"));
            Console.WriteLine("Download reference (Kbps): " + ReadInteger(settings,
                "serverDownloadLimitKbps"));
            Console.WriteLine("Gameplay reserve (Kbps): " + ReadInteger(settings,
                "joinTransferGameplayHeadroomKbps"));
            Console.WriteLine("Join fixed cap (Kbps): " + DisplayAutomatic(settings,
                "joinTransferMaxKbps"));
            Console.WriteLine("Per-join fixed cap (Kbps): " + DisplayAutomatic(settings,
                "joinTransferPerJoinMaxKbps"));
            Console.WriteLine("Join burst (KiB): " + ReadInteger(settings,
                "joinTransferBurstKiB"));
        }

        // Source: ScMultiplayer/Func/Server/ScMultiplayerSettings.cs:HandleServerSettings
        private static string DisplayAutomatic(Dictionary<string, object> settings, string name)
        {
            int value = ReadInteger(settings, name);
            return value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "Automatic [0]";
        }

        private void ConfigureSimpleBandwidth(Dictionary<string, object> settings, string mode,
            bool configurationEnabled)
        {
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> Simple Bandwidth");
            Console.WriteLine("Automatic ignores saved caps; Configured uses them without changing their values.");
            Console.WriteLine();
            configurationEnabled = PromptBoolean("Bandwidth configuration [" +
                (configurationEnabled ? "On" : "Automatic") + "]", configurationEnabled);
            if (!configurationEnabled)
            {
                PrintResponse(RequireSuccess(m_server.SubmitLocal("multiplayer.settings",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["bandwidthConfigurationEnabled"] = false
                    })));
                return;
            }
            Console.WriteLine("The value below is your already-safe application limit. It is not reduced automatically.");
            Console.WriteLine("Join transfer stays Automatic [0] and shares the safe default schedule among up to four clients.");
            Console.WriteLine();
            mode = PromptChoice("Bandwidth mode",
                new[] { "Shared total", "Separate upload / download" },
                mode == "shared" ? "Shared total" : "Separate upload / download");
            mode = mode.StartsWith("Shared", StringComparison.OrdinalIgnoreCase)
                ? "shared" : "separate";
            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["bandwidthMode"] = mode,
                ["joinTransferMaxKbps"] = 0,
                ["joinTransferPerJoinMaxKbps"] = 0,
                ["joinTransferBurstKiB"] = 32
            };
            if (mode == "shared")
            {
                int cap = PromptInteger("Shared total safe cap (Kbps)[" +
                    ReadInteger(settings, "sharedTotalSafeCapKbps") + "]",
                    ReadInteger(settings, "sharedTotalSafeCapKbps"), 0, 1048576);
                values["sharedTotalSafeCapKbps"] = cap;
                values["joinTransferGameplayHeadroomKbps"] = RecommendedGameplayReserve(cap);
                Console.WriteLine("Recommended gameplay reserve (Kbps)[" +
                    RecommendedGameplayReserve(cap) + "]");
            }
            else
            {
                int upload = PromptInteger("Upload safe cap (Kbps)[" +
                    ReadInteger(settings, "serverUploadLimitKbps") + "]",
                    ReadInteger(settings, "serverUploadLimitKbps"), 0, 1048576);
                int download = PromptInteger("Download reference (Kbps)[" +
                    ReadInteger(settings, "serverDownloadLimitKbps") + "]",
                    ReadInteger(settings, "serverDownloadLimitKbps"), 0, 1048576);
                values["serverUploadLimitKbps"] = upload;
                values["serverDownloadLimitKbps"] = download;
                values["joinTransferGameplayHeadroomKbps"] = RecommendedGameplayReserve(upload);
                Console.WriteLine("Recommended gameplay reserve (Kbps)[" +
                    RecommendedGameplayReserve(upload) + "]");
            }
            PrintResponse(RequireSuccess(m_server.SubmitLocal("multiplayer.settings", values)));
        }

        private void ConfigureAdvancedBandwidth(Dictionary<string, object> settings, string mode)
        {
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> Advanced Bandwidth");
            Console.WriteLine("Join fixed cap (Kbps)[0] means Automatic and is recommended.");
            Console.WriteLine();
            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["bandwidthMode"] = PromptChoice("Bandwidth mode",
                    new[] { "Shared total", "Separate upload / download" },
                    mode == "shared" ? "Shared total" : "Separate upload / download"),
                ["sharedTotalSafeCapKbps"] = PromptInteger(
                    "Shared total safe cap (Kbps)[" + ReadInteger(settings,
                        "sharedTotalSafeCapKbps") + "]",
                    ReadInteger(settings, "sharedTotalSafeCapKbps"), 0, 1048576),
                ["serverUploadLimitKbps"] = PromptInteger(
                    "Upload safe cap (Kbps)[" + ReadInteger(settings,
                        "serverUploadLimitKbps") + "]",
                    ReadInteger(settings, "serverUploadLimitKbps"), 0, 1048576),
                ["serverDownloadLimitKbps"] = PromptInteger(
                    "Download reference (Kbps)[" + ReadInteger(settings,
                        "serverDownloadLimitKbps") + "]",
                    ReadInteger(settings, "serverDownloadLimitKbps"), 0, 1048576),
                ["joinTransferMaxKbps"] = PromptInteger(
                    "Join fixed cap (Kbps)[" + ReadInteger(settings,
                        "joinTransferMaxKbps") + "] (0 Automatic)",
                    ReadInteger(settings, "joinTransferMaxKbps"), 0, 1048576),
                ["joinTransferGameplayHeadroomKbps"] = PromptInteger(
                    "Gameplay reserve (Kbps)[" + ReadInteger(settings,
                        "joinTransferGameplayHeadroomKbps") + "]",
                    ReadInteger(settings, "joinTransferGameplayHeadroomKbps"), 0, 1048576),
                ["joinTransferBurstKiB"] = PromptInteger(
                    "Join burst (KiB)[" + ReadInteger(settings,
                        "joinTransferBurstKiB") + "]",
                    ReadInteger(settings, "joinTransferBurstKiB"), 0, 1024),
                ["joinTransferPerJoinMaxKbps"] = PromptInteger(
                    "Per-join fixed cap (Kbps)[" + ReadInteger(settings,
                        "joinTransferPerJoinMaxKbps") + "] (0 Automatic)",
                    ReadInteger(settings, "joinTransferPerJoinMaxKbps"), 0, 1048576)
            };
            values["bandwidthMode"] = values["bandwidthMode"].ToString().StartsWith(
                "Shared", StringComparison.OrdinalIgnoreCase) ? "shared" : "separate";
            PrintResponse(RequireSuccess(m_server.SubmitLocal("multiplayer.settings", values)));
        }

        private static int RecommendedGameplayReserve(int safeCapKbps)
        {
            if (safeCapKbps >= 3000) return 512;
            if (safeCapKbps >= 1000) return 256;
            return 96;
        }

        private static int ReadInteger(Dictionary<string, object> values, string name)
        {
            return values.TryGetValue(name, out object value) ?
                Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        }

        private static bool ReadBoolean(Dictionary<string, object> values, string name,
            bool defaultValue)
        {
            return values.TryGetValue(name, out object value) && value != null
                ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                : defaultValue;
        }

        private static string ReadString(Dictionary<string, object> values, string name,
            string defaultValue)
        {
            return values.TryGetValue(name, out object value) && value != null
                ? value.ToString() : defaultValue;
        }

        private static string FormatDataModificationMode(string value)
        {
            if (string.Equals(value, "reject", StringComparison.OrdinalIgnoreCase))
                return "Reject";
            if (string.Equals(value, "allow", StringComparison.OrdinalIgnoreCase))
                return "Allow";
            return "Default";
        }

        private void ShowResponse(string command)
        {
            Console.Clear();
            Console.WriteLine(GetCurrentScreen() + "> " + command);
            PrintResponse(RequireSuccess(m_server.SubmitLocal(command)));
            Pause();
        }

        private string PromptText(string label, string defaultValue,
            string defaultDisplay = null) => Interactive(() =>
            {
                Console.Write(label + " [" + (defaultDisplay ?? defaultValue) + "]: ");
                string value = Console.ReadLine();
                return string.IsNullOrEmpty(value) ? defaultValue : value.Trim();
            });

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

        private void Pause() => Interactive(() =>
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to continue.");
            Console.ReadKey(true);
            return true;
        });

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

        private enum WorldEditorResult
        {
            Stay,
            Cancel,
            PreviousPage,
            NextPage,
            Submit
        }

        private sealed class WorldEditorItem
        {
            public WorldEditorItem(string label, Action edit)
            {
                Label = label;
                Edit = edit;
                Result = WorldEditorResult.Stay;
            }

            public WorldEditorItem(string label, WorldEditorResult result)
            {
                Label = label;
                Result = result;
            }

            public string Label { get; }

            public Action Edit { get; }

            public WorldEditorResult Result { get; }
        }

        private sealed class WorldCreationDraft
        {
            public string Name = "ServerWorld";
            public string Seed = string.Empty;
            public string GameMode = "Survival";
            public string StartingPosition = "Easy";
            public string TerrainGeneration = "Continent";
            public string EnvironmentBehavior = "Living";
            public string TimeOfDay = "Changing";
            public bool WeatherEffects = true;
            public bool AdventureRespawn = true;
            public bool AdventureSurvivalMechanics = true;
            public bool SupernaturalCreatures = true;
            public bool FriendlyFire = true;
            public bool SeasonsChanging = true;
            public int SeaLevelOffset;
            public float TemperatureOffset;
            public float HumidityOffset;
            public float BiomeSize = 1f;
            public float YearDays = 24f;
            public float TimeOfYear = 0.125f;
            public string BlocksTextureName = string.Empty;
            public float IslandSizeEW = 400f;
            public float IslandSizeNS = 400f;
            public int TerrainLevel = 64;
            public float ShoreRoughness = 0.5f;
            public int TerrainBlockIndex = 8;
            public int TerrainOceanBlockIndex = 18;
            public string[] PaletteColors = new string[WorldPalette.MaxColors];
            public string[] PaletteNames = new string[WorldPalette.MaxColors];
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

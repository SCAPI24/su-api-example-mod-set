using Engine;
using Engine.Serialization;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;
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
            int selected = 0;
            while (m_running)
            {
                string[] actions =
                {
                    "Create World",
                    "Join World",
                    GetCurrentWorldMenuLabel(),
                    "List Worlds",
                    "Export World",
                    "Delete World",
                    "Create Player",
                    "Manage Players",
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
                            ShowResponse("status");
                            break;
                        case 9:
                            RunCommandLine();
                            break;
                        case 10:
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
        private WorldEditorResult EditWorldPage(WorldCreationDraft draft, int page)
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
            Dictionary<string, object> world = SelectWorld("Worlds");
            if (world != null)
                ShowWorldDetails(world);
        }

        private void ShowCurrentWorld()
        {
            List<Dictionary<string, object>> worlds = GetResult<List<Dictionary<string, object>>>(
                m_server.SubmitLocal("world.list"));
            Dictionary<string, object> current = null;
            foreach (Dictionary<string, object> world in worlds)
            {
                if (world.TryGetValue("loaded", out object loaded) &&
                    loaded is bool isLoaded && isLoaded)
                {
                    current = world;
                    break;
                }
            }

            if (current == null)
            {
                Console.Clear();
                Console.WriteLine(GetCurrentScreen() + "> Current World");
                Console.WriteLine("No world is currently loaded.");
                Pause();
                return;
            }

            ShowWorldDetails(current);
        }

        private string GetCurrentWorldMenuLabel()
        {
            try
            {
                List<Dictionary<string, object>> worlds = GetResult<List<Dictionary<string, object>>>(
                    m_server.SubmitLocal("world.list"));
                foreach (Dictionary<string, object> world in worlds)
                {
                    if (world.TryGetValue("loaded", out object loaded) &&
                        loaded is bool isLoaded && isLoaded)
                    {
                        return "Current World: " + world["name"];
                    }
                }
            }
            catch
            {
            }
            return "Current World: <none>";
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
                bool loaded = world["loaded"] is bool isLoaded && isLoaded;
                labels.Add(world["name"] + " | " + world["gameMode"] +
                    (loaded ? " | CURRENT" : string.Empty) +
                    " | players " + world["players"]);
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

        private static int PromptInteger(
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

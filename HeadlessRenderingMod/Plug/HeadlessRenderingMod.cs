using Engine;
using Engine.Audio;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TemplatesDatabase;

namespace HeadlessRenderingMod
{
    public sealed class HeadlessRenderingMod : IMod
    {
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_frameToken;
        private HeadlessServerConfig m_config;
        private HeadlessControlServer m_server;
        private GameControlCommands m_gameCommands;
        private CommandSequenceManager m_sequences;
        private WindowsConsoleController m_consoleController;
        private FrameRateLimiter m_frameRateLimiter;
        private bool m_rootDrawStateCaptured;
        private bool m_originalRootDrawEnabled;
        private bool m_settingsStateCaptured;
        private bool m_originalFpsCounter;
        private bool m_originalFpsRibbon;
        private bool m_audioStateCaptured;
        private float m_originalMasterVolume;
        private bool m_windowHideAttempted;
        private object m_gameWindow;
        private PropertyInfo m_windowVisibleProperty;
        private bool? m_originalWindowVisible;
        private string m_lastFrameError;

        public string Name => "无画面服务器";

        public string Version => "1.3.0";

        public IEnumerable<string> Dependencies => Array.Empty<string>();

        public bool IsEnabled { get; set; } = true;

        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            if (eventBus == null)
                throw new ArgumentNullException(nameof(eventBus));

            // Source: Engine/Engine/Storage.cs:Storage.ProcessPath
            // Keep relative paths used by older game code aligned with the executable data root.
            string instanceRoot = Path.GetFullPath(AppContext.BaseDirectory);
            Environment.CurrentDirectory = instanceRoot;

            m_config = HeadlessServerConfig.LoadOrCreate(instanceRoot);
            if (!m_config.Enabled)
            {
                Log.Information("[HeadlessRenderingMod] Disabled by server.json.");
                return;
            }

            if (m_config.DisableAudio || m_config.DisableDrawing)
                HeadlessAudioFallback.Ensure(
                    instanceRoot, m_config.DisableAudio, m_config.DisableDrawing);
            HeadlessDisplayDeviceFallback.Ensure();

            m_server = new HeadlessControlServer(m_config);
            m_gameCommands = new GameControlCommands(instanceRoot);
            m_sequences = new CommandSequenceManager();
            try
            {
                m_server.Start();
                m_frameRateLimiter = new FrameRateLimiter(m_config.TargetFrameRate);
                m_eventBus = eventBus;

                // Source: Survivalcraft/Game/Program.cs:Program.Run
                m_frameToken = eventBus.SubscribeEvent(
                    "Frame.Update",
                    HandleFrameUpdate,
                    EventPriority.LOWEST);

                if (m_config.EnableConsole && OperatingSystem.IsWindows())
                {
                    m_consoleController = new WindowsConsoleController(
                        m_server,
                        m_config);
                    if (!m_consoleController.Start())
                    {
                        Log.Warning(
                            "[HeadlessRenderingMod] Console unavailable; " +
                            "the game window will remain visible.");
                    }
                }

                Log.Information(
                    $"[HeadlessRenderingMod] Instance '{m_config.InstanceId}' listening on " +
                    $"{m_config.BindAddress}:{m_config.Port}, target={m_config.TargetFrameRate} Hz.");
            }
            catch
            {
                m_server.Stop();
                m_server = null;
                throw;
            }
        }

        public void OnUnload()
        {
            if (m_eventBus != null && m_frameToken != null)
                m_eventBus.UnsubscribeEvent(m_frameToken);

            m_frameToken = null;
            m_eventBus = null;
            if (m_consoleController != null)
            {
                m_consoleController.Stop();
                m_consoleController = null;
            }
            RestoreGameState();

            if (m_server != null)
            {
                m_server.Stop();
                m_server = null;
            }
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        private object[] HandleFrameUpdate(object[] args)
        {
            try
            {
                ApplyHeadlessState();
                m_server.ProcessQueuedCommands(ExecuteCommand, m_config.MaxCommandsPerFrame);
                m_sequences.Update(ExecuteCommand, EvaluateSequenceCondition);
                m_frameRateLimiter.WaitForNextFrame();
                m_lastFrameError = null;
            }
            catch (Exception ex)
            {
                string message = ex.GetType().Name + ": " + ex.Message;
                if (!string.Equals(message, m_lastFrameError, StringComparison.Ordinal))
                {
                    m_lastFrameError = message;
                    Log.Error("[HeadlessRenderingMod] Frame handler failed: " + message);
                }
            }
            return null;
        }

        private void ApplyHeadlessState()
        {
            // Source: Survivalcraft/Game/Widget.cs:DrawContext.CollateDrawItems
            if (m_config.DisableDrawing && ScreensManager.RootWidget != null)
            {
                if (!m_rootDrawStateCaptured)
                {
                    m_originalRootDrawEnabled = ScreensManager.RootWidget.IsDrawEnabled;
                    m_rootDrawStateCaptured = true;
                }
                ScreensManager.RootWidget.IsDrawEnabled = false;
            }

            // Source: Survivalcraft/Game/PerformanceManager.cs:PerformanceManager.Draw
            if (!m_settingsStateCaptured)
            {
                m_originalFpsCounter = SettingsManager.DisplayFpsCounter;
                m_originalFpsRibbon = SettingsManager.DisplayFpsRibbon;
                m_settingsStateCaptured = true;
            }
            SettingsManager.DisplayFpsCounter = false;
            SettingsManager.DisplayFpsRibbon = false;

            // Source: Engine/Engine/Audio/Mixer.cs:Mixer.MasterVolume
            if (m_config.DisableAudio)
            {
                if (!m_audioStateCaptured)
                {
                    m_originalMasterVolume = Mixer.MasterVolume;
                    m_audioStateCaptured = true;
                }
                Mixer.MasterVolume = 0f;
            }

            bool consoleReady = !m_config.EnableConsole ||
                (m_consoleController != null && m_consoleController.IsRunning);
            if (m_config.HideWindow && consoleReady && !m_windowHideAttempted)
                HideWindowOnce();
        }

        // Source: Engine/Engine/Window.cs:Window.m_gameWindow
        private void HideWindowOnce()
        {
            m_windowHideAttempted = true;
            m_gameWindow = ModManager.Instance.ModParentField.GetStaticField(
                typeof(Engine.Window),
                "m_gameWindow");

            if (m_gameWindow == null)
                throw new InvalidOperationException("OpenTK GameWindow is not available.");

            m_windowVisibleProperty = m_gameWindow.GetType().GetProperty(
                "Visible",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (m_windowVisibleProperty == null ||
                m_windowVisibleProperty.PropertyType != typeof(bool) ||
                !m_windowVisibleProperty.CanRead ||
                !m_windowVisibleProperty.CanWrite)
            {
                throw new MissingMemberException(
                    m_gameWindow.GetType().FullName,
                    "Visible");
            }

            m_originalWindowVisible = (bool)m_windowVisibleProperty.GetValue(m_gameWindow);
            m_windowVisibleProperty.SetValue(m_gameWindow, false);
        }

        // Source: Survivalcraft/Game/ScreensManager.cs and GameManager.cs
        private object ExecuteCommand(ControlRequest request)
        {
            if (m_gameCommands.TryExecute(request, out object gameResult))
                return gameResult;

            switch (request.Command)
            {
                case "status":
                    return BuildStatus();
                case "screen.list":
                    return ListScreens();
                case "screen.switch":
                    return SwitchScreen(request);
                case "dialog.list":
                    return ListDialogs();
                case "createworld":
                case "world.create":
                    return CreateWorld(request);
                case "sequence.start":
                    return m_sequences.Start(request);
                case "sequence.status":
                    return m_sequences.GetStatus(request);
                case "sequence.list":
                    return m_sequences.List();
                case "sequence.cancel":
                    return m_sequences.Cancel(request);
                case "shutdown":
                    return Shutdown();
                default:
                    throw new ControlCommandException(
                        "unknown_command",
                        $"Unknown command '{request.Command}'.");
            }
        }

        // Source: Survivalcraft/Game/GameManager.cs and SubsystemPlayers.cs
        private Dictionary<string, object> BuildStatus()
        {
            Dictionary<string, Screen> screens = GetScreens();
            string currentScreen = FindScreenName(screens, ScreensManager.CurrentScreen);
            int playersCount = 0;
            if (GameManager.Project != null)
            {
                SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
                if (players != null)
                    playersCount = players.PlayersData.Count;
            }

            bool? windowVisible = GetWindowVisible();
            string worldName = GameManager.WorldInfo != null
                ? GameManager.WorldInfo.WorldSettings.Name
                : null;

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["instanceId"] = m_config.InstanceId,
                ["processId"] = Environment.ProcessId,
                ["currentScreen"] = currentScreen,
                ["screenAnimating"] = ScreensManager.IsAnimating,
                ["worldLoaded"] = GameManager.Project != null,
                ["worldName"] = worldName,
                ["playersCount"] = playersCount,
                ["targetFrameRate"] = m_config.TargetFrameRate,
                ["lastFrameTime"] = Program.LastFrameTime,
                ["frameIndex"] = Time.FrameIndex,
                ["rootWidgetDrawEnabled"] = ScreensManager.RootWidget != null
                    ? ScreensManager.RootWidget.IsDrawEnabled
                    : null,
                ["masterVolume"] = Mixer.MasterVolume,
                ["windowVisible"] = windowVisible,
                ["queuedCommands"] = m_server.QueuedCommandCount,
                ["serverError"] = m_server.LastError,
                ["frameError"] = m_lastFrameError
            };
        }

        // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.m_screens
        private List<string> ListScreens()
        {
            List<string> result = new List<string>(GetScreens().Keys);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.SwitchScreen
        private Dictionary<string, object> SwitchScreen(ControlRequest request)
        {
            if (ScreensManager.IsAnimating)
            {
                throw new ControlCommandException(
                    "screen_busy",
                    "A screen transition is already in progress.");
            }
            if (!request.TryGetString("screen", out string screenName) ||
                string.IsNullOrWhiteSpace(screenName))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "screen.switch requires a non-empty 'screen' argument.");
            }

            Dictionary<string, Screen> screens = GetScreens();
            if (!screens.ContainsKey(screenName))
            {
                throw new ControlCommandException(
                    "screen_not_found",
                    $"Screen '{screenName}' is not loaded.");
            }

            ScreensManager.SwitchScreen(screenName);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["screen"] = screenName
            };
        }

        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.Dialogs
        private List<Dictionary<string, object>> ListDialogs()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            for (int i = 0; i < DialogsManager.Dialogs.Count; i++)
            {
                Dialog dialog = DialogsManager.Dialogs[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["index"] = i,
                    ["type"] = dialog.GetType().FullName
                };
                if (dialog is MessageDialog)
                {
                    LabelWidget largeLabel = ModManager.Instance.ModParentField
                        .GetParentField<LabelWidget>(dialog, "m_largeLabelWidget", typeof(MessageDialog));
                    LabelWidget smallLabel = ModManager.Instance.ModParentField
                        .GetParentField<LabelWidget>(dialog, "m_smallLabelWidget", typeof(MessageDialog));
                    item["largeText"] = largeLabel?.Text;
                    item["smallText"] = smallLabel?.Text;
                }
                result.Add(item);
            }
            return result;
        }

        // Source: Survivalcraft/Game/NewWorldScreen.cs:NewWorldScreen.Update
        private Dictionary<string, object> CreateWorld(ControlRequest request)
        {
            if (ScreensManager.IsAnimating)
            {
                throw new ControlCommandException(
                    "screen_busy",
                    "A screen transition is in progress. Retry after screen.ready.");
            }
            if (GameManager.Project != null)
            {
                throw new ControlCommandException(
                    "world_already_loaded",
                    "A world is already loaded. Close it before creating another world.");
            }

            if (ScreensManager.FindScreen<GameLoadingScreen>("GameLoading") == null ||
                ScreensManager.FindScreen<GameScreen>("Game") == null)
            {
                throw new ControlCommandException(
                    "game_not_ready",
                    "World creation is not available until game loading has completed.");
            }

            if (!request.TryGetString("name", out string name) ||
                string.IsNullOrWhiteSpace(name))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "CreateWorld requires a non-empty 'name'.");
            }
            if (!WorldsManager.ValidateWorldName(name))
            {
                throw new ControlCommandException(
                    "invalid_world_name",
                    "World name must contain 1-14 ASCII letters, digits or spaces, " +
                    "and must start and end with a letter or digit.");
            }

            WorldsManager.UpdateWorldsList();
            if (WorldsManager.WorldInfos.Count >= WorldsManager.MaxAllowedWorlds)
            {
                throw new ControlCommandException(
                    "too_many_worlds",
                    $"A maximum of {WorldsManager.MaxAllowedWorlds} worlds is allowed.");
            }

            GameMode gameMode = ReadEnum(
                request,
                "gameMode",
                GameMode.Survival);
            if (gameMode == GameMode.Adventure)
            {
                throw new ControlCommandException(
                    "invalid_game_mode",
                    "Adventure mode cannot be selected for a new world.");
            }

            TerrainGenerationMode terrainGeneration = ReadEnum(
                request,
                "terrainGeneration",
                TerrainGenerationMode.Continent);
            if (gameMode != GameMode.Creative &&
                (terrainGeneration == TerrainGenerationMode.FlatContinent ||
                terrainGeneration == TerrainGenerationMode.FlatIsland))
            {
                throw new ControlCommandException(
                    "invalid_terrain",
                    "Flat terrain is available only in Creative mode.");
            }

            WorldSettings settings = new WorldSettings
            {
                Name = name,
                OriginalSerializationVersion = VersionsManager.SerializationVersion,
                GameMode = gameMode,
                StartingPositionMode = ReadEnum(
                    request,
                    "startingPosition",
                    StartingPositionMode.Easy),
                TerrainGenerationMode = terrainGeneration,
                EnvironmentBehaviorMode = ReadEnum(
                    request,
                    "environmentBehavior",
                    EnvironmentBehaviorMode.Living),
                TimeOfDayMode = ReadEnum(
                    request,
                    "timeOfDay",
                    TimeOfDayMode.Changing)
            };

            if (request.TryGetString("seed", out string seed))
                settings.Seed = seed ?? string.Empty;
            if (request.TryGetBoolean("weatherEffects", out bool weatherEffects))
                settings.AreWeatherEffectsEnabled = weatherEffects;
            if (request.TryGetBoolean("adventureRespawn", out bool adventureRespawn))
                settings.IsAdventureRespawnAllowed = adventureRespawn;
            if (request.TryGetBoolean(
                "adventureSurvivalMechanics",
                out bool adventureSurvivalMechanics))
            {
                settings.AreAdventureSurvivalMechanicsEnabled = adventureSurvivalMechanics;
            }
            if (request.TryGetBoolean(
                "supernaturalCreatures",
                out bool supernaturalCreatures))
            {
                settings.AreSupernaturalCreaturesEnabled = supernaturalCreatures;
            }
            if (request.TryGetBoolean("friendlyFire", out bool friendlyFire))
                settings.IsFriendlyFireEnabled = friendlyFire;
            if (request.TryGetBoolean("seasonsChanging", out bool seasonsChanging))
                settings.AreSeasonsChanging = seasonsChanging;

            // Source: Survivalcraft/Game/WorldOptionsScreen.cs:WorldOptionsScreen.Update
            settings.SeaLevelOffset = ReadIntegerOption(
                request, "seaLevelOffset", settings.SeaLevelOffset, -4, 4);
            settings.TemperatureOffset = ReadFloatOption(
                request, "temperatureOffset", settings.TemperatureOffset, -8f, 8f);
            settings.HumidityOffset = ReadFloatOption(
                request, "humidityOffset", settings.HumidityOffset, -8f, 8f);
            settings.BiomeSize = ReadChoiceFloatOption(
                request,
                "biomeSize",
                settings.BiomeSize,
                new[] { 0.25f, 0.33f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f });
            settings.YearDays = ReadChoiceFloatOption(
                request,
                "yearDays",
                settings.YearDays,
                new[] { 8f, 12f, 16f, 20f, 24f, 32f, 48f, 64f, 96f });
            settings.TimeOfYear = ReadFloatOption(
                request, "timeOfYear", settings.TimeOfYear, 0f, 0.999f);
            if (request.TryGetString("blocksTextureName", out string blocksTextureName))
                settings.BlocksTextureName = blocksTextureName ?? string.Empty;
            if (request.TryGetFloat("islandSizeEW", out float islandSizeEW))
                settings.IslandSize.X = ValidateRange("islandSizeEW", islandSizeEW, 30f, 2500f);
            if (request.TryGetFloat("islandSizeNS", out float islandSizeNS))
                settings.IslandSize.Y = ValidateRange("islandSizeNS", islandSizeNS, 30f, 2500f);
            if (request.TryGetInteger("terrainLevel", out int terrainLevel))
                settings.TerrainLevel = ValidateRange("terrainLevel", terrainLevel, 2, 252);
            if (request.TryGetFloat("shoreRoughness", out float shoreRoughness))
                settings.ShoreRoughness = ValidateRange("shoreRoughness", shoreRoughness, 0f, 1f);
            if (request.TryGetInteger("terrainBlockIndex", out int terrainBlockIndex))
                settings.TerrainBlockIndex = ValidateRange("terrainBlockIndex", terrainBlockIndex, 0, 1023);
            if (request.TryGetInteger("terrainOceanBlockIndex", out int terrainOceanBlockIndex))
                settings.TerrainOceanBlockIndex = ValidateRange("terrainOceanBlockIndex", terrainOceanBlockIndex, 0, 1023);
            bool hasPaletteColors = request.TryGetString(
                "paletteColors", out string paletteColors);
            bool hasPaletteNames = request.TryGetString(
                "paletteNames", out string paletteNames);
            if (hasPaletteColors || hasPaletteNames)
            {
                ValuesDictionary paletteValues = new ValuesDictionary();
                paletteValues.SetValue("Colors", paletteColors ?? new string(';', 15));
                paletteValues.SetValue("Names", paletteNames ?? new string(';', 15));
                settings.Palette = new WorldPalette(paletteValues);
            }

            if (settings.GameMode != GameMode.Creative)
                settings.ResetOptionsForNonCreativeMode(null);

            // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.CreateWorld
            WorldInfo worldInfo = WorldsManager.CreateWorld(settings);
            if (worldInfo == null)
            {
                throw new ControlCommandException(
                    "world_creation_failed",
                    "World files were created but could not be read back.");
            }

            // Source: Survivalcraft/Game/GameLoadingScreen.cs:GameLoadingScreen.Enter
            ScreensManager.SwitchScreen("GameLoading", worldInfo, null);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["worldName"] = worldInfo.WorldSettings.Name,
                ["directoryName"] = worldInfo.DirectoryName,
                ["gameMode"] = worldInfo.WorldSettings.GameMode.ToString(),
                ["startingPosition"] = worldInfo.WorldSettings.StartingPositionMode.ToString(),
                ["terrainGeneration"] = worldInfo.WorldSettings.TerrainGenerationMode.ToString(),
                ["screen"] = "GameLoading"
            };
        }

        // Source: Engine/Engine/Window.cs:Window.Close
        private Dictionary<string, object> Shutdown()
        {
            Engine.Window.Close();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["closing"] = true
            };
        }

        private Dictionary<string, Screen> GetScreens()
        {
            Dictionary<string, Screen> screens = ModManager.Instance.ModParentField
                .GetStaticField<Dictionary<string, Screen>>(
                    typeof(ScreensManager),
                    "m_screens");

            if (screens == null)
                throw new InvalidOperationException("ScreensManager screen registry is unavailable.");
            return screens;
        }

        private static string FindScreenName(
            Dictionary<string, Screen> screens,
            Screen currentScreen)
        {
            if (currentScreen == null)
                return null;

            foreach (KeyValuePair<string, Screen> pair in screens)
            {
                if (ReferenceEquals(pair.Value, currentScreen))
                    return pair.Key;
            }
            return currentScreen.GetType().Name;
        }

        private static T ReadEnum<T>(
            ControlRequest request,
            string argumentName,
            T defaultValue)
            where T : struct, Enum
        {
            if (!request.TryGetString(argumentName, out string value))
                return defaultValue;
            if (Enum.TryParse(value, true, out T result) && Enum.IsDefined(result))
                return result;

            throw new ControlCommandException(
                "invalid_argument",
                $"'{value}' is not a valid {argumentName} value.");
        }

        private static float ReadFloatOption(
            ControlRequest request,
            string argumentName,
            float defaultValue,
            float minimum,
            float maximum)
        {
            if (!request.TryGetFloat(argumentName, out float value))
                return defaultValue;
            return ValidateRange(argumentName, value, minimum, maximum);
        }

        private static int ReadIntegerOption(
            ControlRequest request,
            string argumentName,
            int defaultValue,
            int minimum,
            int maximum)
        {
            if (!request.TryGetInteger(argumentName, out int value))
                return defaultValue;
            return ValidateRange(argumentName, value, minimum, maximum);
        }

        private static float ReadChoiceFloatOption(
            ControlRequest request,
            string argumentName,
            float defaultValue,
            float[] choices)
        {
            if (!request.TryGetFloat(argumentName, out float value))
                return defaultValue;
            foreach (float choice in choices)
            {
                if (Math.Abs(choice - value) < 0.0001f)
                    return choice;
            }
            throw new ControlCommandException(
                "invalid_argument",
                $"'{argumentName}' must be one of: {string.Join(", ", choices)}.");
        }

        private static int ValidateRange(
            string argumentName,
            int value,
            int minimum,
            int maximum)
        {
            if (value < minimum || value > maximum)
                throw new ControlCommandException(
                    "invalid_argument",
                    $"'{argumentName}' must be between {minimum} and {maximum}.");
            return value;
        }

        private static float ValidateRange(
            string argumentName,
            float value,
            float minimum,
            float maximum)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < minimum || value > maximum)
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    $"'{argumentName}' must be between {minimum} and {maximum}.");
            }
            return value;
        }

        // Source: Survivalcraft/Game/ScreensManager.cs and GameManager.cs
        private bool EvaluateSequenceCondition(string condition)
        {
            if (string.Equals(condition, "screen.ready", StringComparison.OrdinalIgnoreCase))
                return !ScreensManager.IsAnimating;
            if (string.Equals(condition, "world.loaded", StringComparison.OrdinalIgnoreCase))
                return GameManager.Project != null;
            if (string.Equals(condition, "world.unloaded", StringComparison.OrdinalIgnoreCase))
                return GameManager.Project == null;
            if (string.Equals(condition, "world.ready", StringComparison.OrdinalIgnoreCase))
            {
                return GameManager.Project != null &&
                    ScreensManager.CurrentScreen is not GameLoadingScreen;
            }
            if (condition.StartsWith("screen:", StringComparison.OrdinalIgnoreCase))
            {
                string expected = condition.Substring("screen:".Length);
                string actual = FindScreenName(GetScreens(), ScreensManager.CurrentScreen);
                return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            }
            const string playersPrefix = "players.atleast:";
            if (condition.StartsWith(playersPrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(condition.Substring(playersPrefix.Length), out int count) &&
                GameManager.Project != null)
            {
                SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
                return players != null && players.PlayersData.Count >= count;
            }

            throw new ControlCommandException(
                "invalid_wait_condition",
                $"Unknown wait condition '{condition}'.");
        }

        private bool? GetWindowVisible()
        {
            if (m_gameWindow == null || m_windowVisibleProperty == null)
                return null;

            try
            {
                return (bool)m_windowVisibleProperty.GetValue(m_gameWindow);
            }
            catch
            {
                return null;
            }
        }

        private void RestoreGameState()
        {
            if (m_rootDrawStateCaptured && ScreensManager.RootWidget != null)
                ScreensManager.RootWidget.IsDrawEnabled = m_originalRootDrawEnabled;

            if (m_settingsStateCaptured)
            {
                SettingsManager.DisplayFpsCounter = m_originalFpsCounter;
                SettingsManager.DisplayFpsRibbon = m_originalFpsRibbon;
            }

            if (m_audioStateCaptured)
                Mixer.MasterVolume = m_originalMasterVolume;

            if (m_originalWindowVisible.HasValue &&
                m_gameWindow != null &&
                m_windowVisibleProperty != null)
            {
                try
                {
                    m_windowVisibleProperty.SetValue(
                        m_gameWindow,
                        m_originalWindowVisible.Value);
                }
                catch
                {
                }
            }
        }
    }
}

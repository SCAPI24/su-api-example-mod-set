using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.IO;

namespace HeadlessRenderingMod
{
    internal sealed class GameControlCommands
    {
        private readonly string m_instanceRoot;

        public GameControlCommands(string instanceRoot)
        {
            m_instanceRoot = instanceRoot ?? throw new ArgumentNullException(nameof(instanceRoot));
        }

        public bool TryExecute(ControlRequest request, out object result)
        {
            switch (request.Command)
            {
                case "world.list":
                    result = ListWorlds();
                    return true;
                case "world.join":
                    result = JoinWorld(request);
                    return true;
                case "world.close":
                    result = CloseWorld();
                    return true;
                case "world.export":
                    result = ExportWorld(request);
                    return true;
                case "world.delete":
                    result = DeleteWorld(request);
                    return true;
                case "player.list":
                    result = ListPlayers();
                    return true;
                case "player.skin.list":
                    result = ListCharacterSkins(request);
                    return true;
                case "player.create":
                    result = CreatePlayer(request);
                    return true;
                case "player.update":
                    result = UpdatePlayer(request);
                    return true;
                case "player.delete":
                    result = DeletePlayer(request);
                    return true;
                default:
                    result = null;
                    return false;
            }
        }

        // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.Enter
        private List<Dictionary<string, object>> ListWorlds()
        {
            WorldsManager.UpdateWorldsList();
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (WorldInfo worldInfo in WorldsManager.WorldInfos)
                result.Add(BuildWorldInfo(worldInfo));
            result.Sort((left, right) => string.Compare(
                left["name"].ToString(),
                right["name"].ToString(),
                StringComparison.OrdinalIgnoreCase));
            return result;
        }

        // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.Play
        private Dictionary<string, object> JoinWorld(ControlRequest request)
        {
            EnsureScreenStable();
            EnsureWorldScreensReady();
            WorldInfo worldInfo = FindWorld(request);
            if (GameManager.WorldInfo != null &&
                string.Equals(
                    GameManager.WorldInfo.DirectoryName,
                    worldInfo.DirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["alreadyLoaded"] = true,
                    ["world"] = BuildWorldInfo(worldInfo)
                };
            }

            CloseLoadedProject(switchToMainMenu: false);
            ScreensManager.SwitchScreen("GameLoading", worldInfo, null);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["screen"] = "GameLoading",
                ["world"] = BuildWorldInfo(worldInfo)
            };
        }

        // Source: Survivalcraft/Game/GameMenuDialog.cs:GameMenuDialog.Update
        private Dictionary<string, object> CloseWorld()
        {
            EnsureScreenStable();
            string worldName = GameManager.WorldInfo?.WorldSettings.Name;
            bool closed = GameManager.Project != null;
            CloseLoadedProject(switchToMainMenu: true);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["closed"] = closed,
                ["worldName"] = worldName,
                ["screen"] = "MainMenu"
            };
        }

        // Source: Survivalcraft/Game/ExternalContentManager.cs:ExternalContentManager.ShowUploadUi
        private Dictionary<string, object> ExportWorld(ControlRequest request)
        {
            EnsureScreenStable();
            WorldInfo worldInfo = FindWorld(request);
            bool wasLoaded = false;
            if (GameManager.WorldInfo != null &&
                string.Equals(
                    GameManager.WorldInfo.DirectoryName,
                    worldInfo.DirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                wasLoaded = true;
                CloseLoadedProject(switchToMainMenu: false);
            }

            string fileName = request.TryGetString("fileName", out string requestedName)
                ? requestedName
                : worldInfo.WorldSettings.Name + ".scworld";
            fileName = ValidateExportFileName(fileName);
            string exportDirectory = Path.Combine(m_instanceRoot, "Scworld");
            Directory.CreateDirectory(exportDirectory);
            string exportPath = Path.Combine(exportDirectory, fileName);
            using (FileStream stream = new FileStream(
                exportPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read))
            {
                WorldsManager.ExportWorld(worldInfo.DirectoryName, stream);
            }

            bool reload = wasLoaded &&
                (!request.TryGetBoolean("reload", out bool requestedReload) || requestedReload);
            if (reload)
                ScreensManager.SwitchScreen("GameLoading", worldInfo, null);
            else if (wasLoaded)
                ScreensManager.SwitchScreen("MainMenu");

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["worldName"] = worldInfo.WorldSettings.Name,
                ["fileName"] = fileName,
                ["path"] = exportPath,
                ["size"] = new FileInfo(exportPath).Length,
                ["worldWasClosed"] = wasLoaded,
                ["reloading"] = reload,
                ["screen"] = reload ? "GameLoading" : ScreensManager.CurrentScreen?.GetType().Name
            };
        }

        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.DeleteWorld
        private Dictionary<string, object> DeleteWorld(ControlRequest request)
        {
            WorldInfo worldInfo = FindWorld(request);
            if (GameManager.WorldInfo != null &&
                string.Equals(
                    GameManager.WorldInfo.DirectoryName,
                    worldInfo.DirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ControlCommandException(
                    "world_in_use",
                    "The loaded world cannot be deleted. Run world.close first.");
            }

            WorldsManager.DeleteWorld(worldInfo.DirectoryName);
            WorldsManager.UpdateWorldsList();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["deleted"] = true,
                ["worldName"] = worldInfo.WorldSettings.Name,
                ["directoryName"] = worldInfo.DirectoryName
            };
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.PlayersData
        private List<Dictionary<string, object>> ListPlayers()
        {
            SubsystemPlayers players = GetPlayers();
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (PlayerData playerData in players.PlayersData)
                result.Add(BuildPlayerInfo(playerData));
            return result;
        }

        // Source: Survivalcraft/Game/CharacterSkinsManager.cs:CharacterSkinsManager.UpdateCharacterSkinsList
        private List<Dictionary<string, object>> ListCharacterSkins(ControlRequest request)
        {
            PlayerClass? playerClass = null;
            if (request.TryGetString("playerClass", out string className))
                playerClass = ReadPlayerClass(className);

            CharacterSkinsManager.UpdateCharacterSkinsList();
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (string skinName in CharacterSkinsManager.CharacterSkinsNames)
            {
                PlayerClass? skinClass = CharacterSkinsManager.GetPlayerClass(skinName);
                if (playerClass.HasValue && skinClass.HasValue && skinClass != playerClass)
                    continue;
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = skinName,
                    ["displayName"] = CharacterSkinsManager.GetDisplayName(skinName),
                    ["playerClass"] = skinClass?.ToString(),
                    ["builtIn"] = CharacterSkinsManager.IsBuiltIn(skinName)
                });
            }
            return result;
        }

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.Update
        private Dictionary<string, object> CreatePlayer(ControlRequest request)
        {
            EnsureScreenStable();
            SubsystemPlayers players = GetPlayers();
            if (players.PlayersData.Count >= SubsystemPlayers.MaxPlayers)
            {
                throw new ControlCommandException(
                    "too_many_players",
                    $"A maximum of {SubsystemPlayers.MaxPlayers} players is allowed.");
            }

            SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(true);
            if (gameInfo.WorldSettings.GameMode == GameMode.Cruel ||
                gameInfo.WorldSettings.GameMode == GameMode.Adventure)
            {
                throw new ControlCommandException(
                    "player_creation_disabled",
                    $"Players cannot be added in {gameInfo.WorldSettings.GameMode} mode.");
            }

            PlayerData playerData = new PlayerData(GameManager.Project);
            if (request.TryGetString("playerClass", out string className))
                playerData.PlayerClass = ReadPlayerClass(className);
            if (request.TryGetString("skin", out string skinName))
                playerData.CharacterSkinName = ValidateSkin(skinName, playerData.PlayerClass);
            if (request.TryGetString("name", out string name))
            {
                ValidatePlayerName(name);
                playerData.Name = name;
            }
            playerData.InputDevice = WidgetInputDevice.None;
            players.AddPlayerData(playerData);
            SavePlayers();

            bool enterGame = !request.TryGetBoolean("enterGame", out bool requestedEnter) ||
                requestedEnter;
            if (enterGame &&
                (ScreensManager.CurrentScreen is PlayerScreen ||
                ScreensManager.CurrentScreen is PlayersScreen))
            {
                ScreensManager.SwitchScreen("Game");
            }
            return BuildPlayerInfo(playerData);
        }

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.Update
        private Dictionary<string, object> UpdatePlayer(ControlRequest request)
        {
            PlayerData playerData = FindPlayer(request);
            bool changed = false;
            if (request.TryGetString("name", out string name))
            {
                ValidatePlayerName(name);
                playerData.Name = name;
                changed = true;
            }
            if (request.TryGetString("skin", out string skinName))
            {
                playerData.CharacterSkinName = ValidateSkin(skinName, playerData.PlayerClass);
                changed = true;
            }
            if (!changed)
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "player.update requires 'name' or 'skin'.");
            }
            SavePlayers();
            return BuildPlayerInfo(playerData);
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.RemovePlayerData
        private Dictionary<string, object> DeletePlayer(ControlRequest request)
        {
            SubsystemPlayers players = GetPlayers();
            PlayerData playerData = FindPlayer(request);
            Dictionary<string, object> deleted = BuildPlayerInfo(playerData);
            players.RemovePlayerData(playerData);
            SavePlayers();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["deleted"] = true,
                ["player"] = deleted,
                ["remainingPlayers"] = players.PlayersData.Count
            };
        }

        private void EnsureWorldScreensReady()
        {
            if (ScreensManager.FindScreen<GameLoadingScreen>("GameLoading") == null ||
                ScreensManager.FindScreen<GameScreen>("Game") == null)
            {
                throw new ControlCommandException(
                    "game_not_ready",
                    "World commands are unavailable until game loading has completed.");
            }
        }

        private static void EnsureScreenStable()
        {
            if (ScreensManager.IsAnimating)
            {
                throw new ControlCommandException(
                    "screen_busy",
                    "A screen transition is in progress. Retry after screen.ready.");
            }
        }

        private static void CloseLoadedProject(bool switchToMainMenu)
        {
            if (GameManager.Project != null)
            {
                GameManager.SaveProject(waitForCompletion: true, showErrorDialog: false);
                GameManager.DisposeProject();
            }
            if (switchToMainMenu)
                ScreensManager.SwitchScreen("MainMenu");
        }

        private static WorldInfo FindWorld(ControlRequest request)
        {
            string selector = null;
            if (!request.TryGetString("world", out selector) &&
                !request.TryGetString("name", out selector) &&
                !request.TryGetString("directoryName", out selector))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "A world selector is required in 'world', 'name' or 'directoryName'.");
            }
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "The world selector cannot be empty.");
            }

            WorldsManager.UpdateWorldsList();
            List<WorldInfo> matches = new List<WorldInfo>();
            foreach (WorldInfo worldInfo in WorldsManager.WorldInfos)
            {
                string directoryLeaf = Path.GetFileName(
                    worldInfo.DirectoryName.Replace('/', Path.DirectorySeparatorChar));
                if (string.Equals(
                        worldInfo.WorldSettings.Name,
                        selector,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        worldInfo.DirectoryName,
                        selector,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(directoryLeaf, selector, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(worldInfo);
                }
            }

            if (matches.Count == 0)
            {
                throw new ControlCommandException(
                    "world_not_found",
                    $"World '{selector}' was not found.");
            }
            if (matches.Count > 1)
            {
                throw new ControlCommandException(
                    "ambiguous_world",
                    $"World selector '{selector}' matches more than one world; use directoryName.");
            }
            return matches[0];
        }

        private static Dictionary<string, object> BuildWorldInfo(WorldInfo worldInfo)
        {
            bool loaded = GameManager.WorldInfo != null &&
                string.Equals(
                    GameManager.WorldInfo.DirectoryName,
                    worldInfo.DirectoryName,
                    StringComparison.OrdinalIgnoreCase);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = worldInfo.WorldSettings.Name,
                ["directoryName"] = worldInfo.DirectoryName,
                ["gameMode"] = worldInfo.WorldSettings.GameMode.ToString(),
                ["terrainGeneration"] = worldInfo.WorldSettings.TerrainGenerationMode.ToString(),
                ["players"] = worldInfo.PlayerInfos.Count,
                ["size"] = worldInfo.Size,
                ["lastSaveUtc"] = worldInfo.LastSaveTime.ToUniversalTime().ToString("O"),
                ["loaded"] = loaded
            };
        }

        private static SubsystemPlayers GetPlayers()
        {
            if (GameManager.Project == null)
            {
                throw new ControlCommandException(
                    "world_not_loaded",
                    "A world must be loaded before managing players.");
            }
            return GameManager.Project.FindSubsystem<SubsystemPlayers>(true);
        }

        private static PlayerData FindPlayer(ControlRequest request)
        {
            if (!request.TryGetInteger("playerIndex", out int playerIndex))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "A numeric 'playerIndex' is required.");
            }
            foreach (PlayerData playerData in GetPlayers().PlayersData)
            {
                if (playerData.PlayerIndex == playerIndex)
                    return playerData;
            }
            throw new ControlCommandException(
                "player_not_found",
                $"Player index {playerIndex} was not found.");
        }

        private static Dictionary<string, object> BuildPlayerInfo(PlayerData playerData)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["playerIndex"] = playerData.PlayerIndex,
                ["name"] = playerData.Name,
                ["playerClass"] = playerData.PlayerClass.ToString(),
                ["skin"] = playerData.CharacterSkinName,
                ["skinDisplayName"] = CharacterSkinsManager.GetDisplayName(
                    playerData.CharacterSkinName),
                ["level"] = playerData.Level,
                ["spawned"] = playerData.ComponentPlayer != null,
                ["readyForPlaying"] = playerData.IsReadyForPlaying
            };
        }

        private static PlayerClass ReadPlayerClass(string value)
        {
            if (Enum.TryParse(value, true, out PlayerClass result) &&
                Enum.IsDefined(result))
            {
                return result;
            }
            throw new ControlCommandException(
                "invalid_argument",
                $"'{value}' is not a valid playerClass. Use Male or Female.");
        }

        private static void ValidatePlayerName(string name)
        {
            if (name == null || !PlayerData.VerifyName(name))
            {
                throw new ControlCommandException(
                    "invalid_player_name",
                    "Player name must contain 2-14 letters, digits or spaces, " +
                    "and cannot start or end with a space.");
            }
        }

        private static string ValidateSkin(string skinName, PlayerClass playerClass)
        {
            CharacterSkinsManager.UpdateCharacterSkinsList();
            bool found = false;
            foreach (string candidate in CharacterSkinsManager.CharacterSkinsNames)
            {
                if (string.Equals(candidate, skinName, StringComparison.OrdinalIgnoreCase))
                {
                    skinName = candidate;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                throw new ControlCommandException(
                    "skin_not_found",
                    $"Character skin '{skinName}' was not found.");
            }

            PlayerClass? skinClass = CharacterSkinsManager.GetPlayerClass(skinName);
            if (skinClass.HasValue && skinClass.Value != playerClass)
            {
                throw new ControlCommandException(
                    "skin_class_mismatch",
                    $"Skin '{skinName}' is for {skinClass.Value}, not {playerClass}.");
            }
            return skinName;
        }

        private static void SavePlayers()
        {
            GameManager.SaveProject(waitForCompletion: true, showErrorDialog: false);
        }

        private static string ValidateExportFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "fileName cannot be empty.");
            }
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ControlCommandException(
                    "invalid_export_name",
                    "fileName must be a plain file name without a directory path.");
            }
            if (!fileName.EndsWith(".scworld", StringComparison.OrdinalIgnoreCase))
                fileName += ".scworld";
            return fileName;
        }
    }
}

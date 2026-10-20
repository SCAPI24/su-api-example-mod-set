using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CmdBridgeMod
{
    /// <summary>
    /// `players.all` / `obs.players`：枚举本机项目里的**每一个玩家角色**。
    ///
    /// 为什么不能复用 `obs.player`：那条命令只看 `SubsystemPlayers.ComponentPlayers[0]`，
    /// 主机分屏的 H2/H3 与联机远端 avatar 都拿不到。
    ///
    /// **三个来源必须合并**（实测 SuMultiplayer 的表示方式，只看一个会漏人）：
    ///   · `SubsystemPlayers.PlayersData`：本地分屏角色；未生成身体时也在（ComponentPlayer 为 null）。
    ///   · `SubsystemPlayers.ComponentPlayers`：当前有 ComponentPlayer 的角色。当前 SuMultiplayer
    ///     在建好远端 avatar 后会把它从 `m_playersData` 里移除、只把 ComponentPlayer 追加进
    ///     `m_componentPlayers`（Source: Mod/SuMultiplayer/Engine/NetworkAvatarSpawner.cs:
    ///     CreateNetworkPlayer 的 "远端角色没有自己的界面" 段）。
    ///   · `SubsystemBodies.Bodies`：所有带 ComponentBody 的实体。远端 avatar 的实体被 SuMP
    ///     直接 `m_entities.Remove(entity)`（不触发 OnEntityRemoved），body 仍留在这里；
    ///     引擎的 `UpdateComponentPlayers` 又会在下一次实体增删时把远端 ComponentPlayer 从
    ///     ComponentPlayers 清掉。只查前两个集合仍可能漏掉客户端角色，所以再按 body 补一次。
    /// 合并后按 PlayerData 引用去重、按 playerIndex 排序；`inPlayersData` / `inComponentPlayers` /
    /// `inBodies` 标明每个角色出现在哪几个集合。本类只读引擎公开集合，不引用 ScMultiplayer /
    /// SuMultiplayer。
    /// 全程只读，且每个字段独立 try/catch —— 某个组件缺失或实体正在销毁时返回 null 并写进
    /// `errors`，绝不让一条坏数据拖垮整条命令。
    ///
    /// 输出上限（防单次回包过大）：
    ///   · 最多 <see cref="MaxPlayersReturned"/> 个 PlayerData（引擎自身 MaxPlayers = 4）；
    ///   · 每个背包最多返回 <see cref="MaxSlotLimit"/> 个非空格子（默认 16）；
    ///   · 饱食度明细最多 <see cref="SatiationEntryLimit"/> 条。
    /// 超出部分用 `slotsOmitted` / `entriesOmitted` 明确回报，不静默假装完整。
    /// </summary>
    internal static class AllPlayersObserver
    {
        private const int MaxPlayersReturned = 32;
        private const int MaxSlotLimit = 64;
        private const int SatiationEntryLimit = 8;
        private const float RadiansToDegrees = 180f / MathUtils.PI;

        /// <summary>
        /// 返回一个 JSON 数组（每个元素是一个 PlayerData）。参数：
        /// `index` = -1 表示全部，否则只返回该 playerIndex（不存在时明确报 `player_not_found`）；
        /// `max` = 0 表示全部（仍受 MaxPlayersReturned 硬上限），否则取前 N 个；
        /// `slots` = 每个背包最多返回多少个非空格子（0 = 只给计数，不返回格子明细）。
        /// </summary>
        public static object Describe(int index, int max, int slots)
        {
            if (GameManager.Project == null)
            {
                throw new BridgeCommandException(
                    "world_not_loaded", "No world is loaded; players.all needs an active project.");
            }

            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null)
            {
                throw new BridgeCommandException(
                    "world_not_loaded", "SubsystemPlayers is not present in the active project.");
            }

            if (index < -1)
                throw new BridgeCommandException("invalid_argument", "index must be -1 (all) or a playerIndex >= 0.");
            if (max < 0)
                throw new BridgeCommandException("invalid_argument", "max must be >= 0 (0 = all players).");
            if (slots < 0)
                throw new BridgeCommandException("invalid_argument", "slots must be >= 0 (0 returns no slot details).");

            int slotLimit = Math.Min(slots, MaxSlotLimit);

            // 合并两个集合：PlayersData 覆盖本地/未生成角色的 PlayerData；ComponentPlayers 覆盖
            // 当前 SuMultiplayer 只留下 ComponentPlayer 的远端 avatar。按 PlayerData 引用去重。
            var candidates = new List<PlayerData>(players.PlayersData);
            var playersDataSet = new HashSet<PlayerData>();
            var seen = new HashSet<PlayerData>();
            for (int i = 0; i < candidates.Count; i++)
            {
                playersDataSet.Add(candidates[i]);
                seen.Add(candidates[i]);
            }

            var componentPlayerData = new HashSet<PlayerData>();
            var componentPlayers = players.ComponentPlayers;
            for (int i = 0; i < componentPlayers.Count; i++)
            {
                ComponentPlayer componentPlayer = componentPlayers[i];
                if (componentPlayer == null || componentPlayer.PlayerData == null)
                    continue;
                componentPlayerData.Add(componentPlayer.PlayerData);
                if (seen.Add(componentPlayer.PlayerData))
                    candidates.Add(componentPlayer.PlayerData);
            }

            // 第三个来源：SubsystemBodies。远端 avatar 的实体被 SuMP 用
            // `project.m_entities.Remove(entity)` 直接摘掉（不触发 OnEntityRemoved），所以它的
            // ComponentBody 仍留在 SubsystemBodies 里；而引擎的 UpdateComponentPlayers 会在
            // 下一次任意实体增删时把远端 ComponentPlayer 从 ComponentPlayers 清掉。只查前两个
            // 集合仍可能漏掉客户端角色，这里按"有 ComponentPlayer 的 body"补一次。
            // Source: Survivalcraft/Game/SubsystemBodies.cs:SubsystemBodies.Bodies
            var bodyPlayerData = new HashSet<PlayerData>();
            SubsystemBodies bodies = GameManager.Project.FindSubsystem<SubsystemBodies>(false);
            if (bodies != null)
            {
                foreach (ComponentBody body in bodies.Bodies)
                {
                    if (body == null || body.Entity == null)
                        continue;
                    try
                    {
                        ComponentPlayer componentPlayer =
                            body.Entity.FindComponent<ComponentPlayer>(false);
                        if (componentPlayer == null || componentPlayer.PlayerData == null)
                            continue;
                        bodyPlayerData.Add(componentPlayer.PlayerData);
                        if (seen.Add(componentPlayer.PlayerData))
                            candidates.Add(componentPlayer.PlayerData);
                    }
                    catch (Exception)
                    {
                        // 单个 body 正在销毁/组件不完整时跳过它；不影响其它角色。
                    }
                }
            }

            candidates.Sort((left, right) => left.PlayerIndex.CompareTo(right.PlayerIndex));

            if (index >= 0)
            {
                PlayerData match = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].PlayerIndex == index)
                    {
                        match = candidates[i];
                        break;
                    }
                }
                if (match == null)
                {
                    throw new BridgeCommandException(
                        "player_not_found",
                        "No PlayerData with playerIndex " + index.ToString(CultureInfo.InvariantCulture)
                        + ". Valid playerIndex values: " + FormatIndices(candidates) + ".");
                }
                candidates.Clear();
                candidates.Add(match);
            }

            int limit = max == 0
                ? Math.Min(candidates.Count, MaxPlayersReturned)
                : Math.Min(max, MaxPlayersReturned);
            var result = new List<Dictionary<string, object>>(Math.Min(limit, candidates.Count));
            SubsystemGameWidgets gameWidgets =
                GameManager.Project.FindSubsystem<SubsystemGameWidgets>(false);

            for (int i = 0; i < candidates.Count && result.Count < limit; i++)
            {
                PlayerData playerData = candidates[i];
                result.Add(DescribePlayer(
                    playerData,
                    gameWidgets,
                    slotLimit,
                    playersDataSet.Contains(playerData),
                    componentPlayerData.Contains(playerData),
                    bodyPlayerData.Contains(playerData)));
            }
            return result;
        }

        private static Dictionary<string, object> DescribePlayer(
            PlayerData playerData, SubsystemGameWidgets gameWidgets, int slotLimit,
            bool inPlayersData, bool inComponentPlayers, bool inBodies)
        {
            var entry = new Dictionary<string, object>(StringComparer.Ordinal);
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);

            // 引擎里的三个集合分别覆盖什么，见类头。远端 avatar 常见形态是
            // inPlayersData:false / inComponentPlayers:false / inBodies:true。
            entry["inPlayersData"] = inPlayersData;
            entry["inComponentPlayers"] = inComponentPlayers;
            entry["inBodies"] = inBodies;

            // ---------------------------------------------------------- 身份（不依赖实体是否还活着）
            // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerIndex/Name/PlayerClass/Level
            // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPosition/SpawnsCount/FirstSpawnTime/LastSpawnTime
            Put(entry, errors, "playerIndex", () => playerData.PlayerIndex);
            Put(entry, errors, "name", () => playerData.Name);
            Put(entry, errors, "playerClass", () => PlayerClassToString(playerData.PlayerClass));
            Put(entry, errors, "level", () => playerData.Level);
            Put(entry, errors, "characterSkinName", () => playerData.CharacterSkinName);
            Put(entry, errors, "isDefaultName", () => playerData.IsDefaultName);
            Put(entry, errors, "inputDevice", () => playerData.InputDevice.ToString());
            Put(entry, errors, "readyForPlaying", () => playerData.IsReadyForPlaying);
            Put(entry, errors, "spawnPosition",
                () => PlayerObserver.Vector3ToDictionary(playerData.SpawnPosition));
            Put(entry, errors, "spawnsCount", () => playerData.SpawnsCount);
            Put(entry, errors, "firstSpawnTime", () => playerData.FirstSpawnTime);
            Put(entry, errors, "lastSpawnTime", () => playerData.LastSpawnTime);

            // PlayerData 的状态机是私有的；只读取 CurrentState（不触发任何 Transition）。
            // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.m_stateMachine（FirstUpdate/PrepareSpawn/
            //         WaitForTerrain/Playing/PlayerDead）
            Put(entry, errors, "playerState", () => ReadPlayerState(playerData));
            string playerState = entry.TryGetValue("playerState", out object stateValue)
                ? stateValue as string
                : null;

            // ---------------------------------------------------------- 本地角色 vs 远端 avatar
            // 关键：绝不能调用 PlayerData.GameWidget —— 远端 avatar 的 GameWidget 已被摘掉，
            // 该 getter 可能抛 InvalidOperationException，也可能返回内部缓存的旧 widget；
            // 两种都会误判。改为遍历 SubsystemGameWidgets.GameWidgets 反向匹配。
            // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.GameWidget
            // Source: Survivalcraft/Game/SubsystemGameWidgets.cs:SubsystemGameWidgets.GameWidgets
            if (gameWidgets == null)
            {
                entry["isLocal"] = null;
                entry["gameWidgetIndex"] = null;
                errors["isLocal"] = "SubsystemGameWidgets is not available; local/remote cannot be classified.";
            }
            else
            {
                try
                {
                    GameWidget gameWidget = FindGameWidget(playerData, gameWidgets);
                    entry["isLocal"] = gameWidget != null;
                    entry["gameWidgetIndex"] = gameWidget != null ? (object)gameWidget.GameWidgetIndex : null;
                }
                catch (Exception exception)
                {
                    entry["isLocal"] = null;
                    entry["gameWidgetIndex"] = null;
                    errors["isLocal"] = DescribeException(exception);
                }
            }

            // ---------------------------------------------------------- 实体是否还存在
            ComponentPlayer player = null;
            try
            {
                player = playerData.ComponentPlayer;
            }
            catch (Exception exception)
            {
                errors["hasComponentPlayer"] = DescribeException(exception);
            }

            entry["hasComponentPlayer"] = player != null;
            bool entityPresent = false;
            bool entityLive = false;
            if (player != null)
            {
                try
                {
                    // 实体已从 Project 摘掉（远端 avatar 离场/本地角色重生中间态）时不再碰任何组件。
                    // Source: EntitySystem/GameEntitySystem/Entity.cs:Entity.IsAddedToProject
                    entityPresent = player.Entity != null;
                    entityLive = entityPresent && player.Entity.IsAddedToProject;
                }
                catch (Exception exception)
                {
                    errors["hasEntity"] = DescribeException(exception);
                }
            }
            entry["hasEntity"] = entityLive;

            string stateReason = null;
            if (player == null)
            {
                stateReason = "component_player_missing";
            }
            else if (!entityPresent)
            {
                stateReason = "entity_missing";
            }
            else if (!entityLive)
            {
                stateReason = "entity_not_added_to_project";
            }
            entry["stateRead"] = stateReason == null;
            entry["stateReason"] = stateReason;

            if (stateReason != null)
            {
                // 只能从 PlayerData 状态机确认死亡；健康值读不到时不要把它伪装成 false。
                bool stateDead = string.Equals(playerState, "PlayerDead", StringComparison.Ordinal);
                entry["isDead"] = stateDead ? (object)true : null;
                entry["errors"] = errors;
                return entry;
            }

            // ---------------------------------------------------------- 位置 / 朝向
            ComponentBody body = null;
            try
            {
                body = player.ComponentBody;
            }
            catch (Exception exception)
            {
                errors["position"] = DescribeException(exception);
                errors["velocity"] = DescribeException(exception);
                errors["rotation"] = DescribeException(exception);
            }
            if (body != null)
            {
                entry["position"] = PlayerObserver.Vector3ToDictionary(body.Position);
                entry["velocity"] = PlayerObserver.Vector3ToDictionary(body.Velocity);
                entry["rotation"] = QuaternionToDictionary(body.Rotation);
            }
            else
            {
                entry["position"] = null;
                entry["velocity"] = null;
                entry["rotation"] = null;
            }

            Put(entry, errors, "look", () => DescribeLook(player, errors));

            // ---------------------------------------------------------- 生命 / 状态
            Put(entry, errors, "health", () => DescribeHealth(player, errors));
            Put(entry, errors, "isDead", () => ComputeIsDead(player.ComponentHealth, playerState));
            Put(entry, errors, "sleep", () => DescribeSleep(player, errors));
            Put(entry, errors, "fire", () => DescribeFire(player, errors));
            Put(entry, errors, "flu", () => DescribeFlu(player, errors));
            Put(entry, errors, "sickness", () => DescribeSickness(player, errors));

            // ---------------------------------------------------------- 生命体征 / 背包 / 动作
            Put(entry, errors, "vitals", () => DescribeVitals(player, errors));
            Put(entry, errors, "inventory", () => DescribeInventory(player, errors, slotLimit));
            Put(entry, errors, "action", () => DescribeAction(player, errors));

            entry["errors"] = errors;
            return entry;
        }

        /// <summary>
        /// 只读 `PlayerData.m_stateMachine.CurrentState`。字段名与声明类型是引擎事实；
        /// 反射失败时由调用方写进 `errors`，不猜测状态。
        /// </summary>
        private static string ReadPlayerState(PlayerData playerData)
        {
            StateMachine stateMachine = GetParentFields().GetParentField<StateMachine>(
                playerData, "m_stateMachine", typeof(PlayerData));
            if (stateMachine == null)
                throw new InvalidOperationException("PlayerData.m_stateMachine is null.");
            return stateMachine.CurrentState;
        }

        /// <summary>
        /// 本地/远端判定：GameWidgets 中存在 `GameWidget.PlayerData == playerData` 即本地。
        /// 不调用 PlayerData.GameWidget：远端 avatar 的 GameWidget 已从 SubsystemGameWidgets
        /// 摘掉，但 PlayerData 内部可能仍缓存着它 —— 用 getter 判断会把远端误判成本地；
        /// 而且没有缓存时该 getter 会直接抛 InvalidOperationException。
        /// Source: Survivalcraft/Game/GameWidget.cs:GameWidget.PlayerData
        /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.GameWidget
        /// </summary>
        private static GameWidget FindGameWidget(
            PlayerData playerData, SubsystemGameWidgets gameWidgets)
        {
            ReadOnlyList<GameWidget> widgets = gameWidgets.GameWidgets;
            for (int i = 0; i < widgets.Count; i++)
            {
                GameWidget widget = widgets[i];
                if (widget != null && ReferenceEquals(widget.PlayerData, playerData))
                    return widget;
            }
            return null;
        }

        private static Dictionary<string, object> DescribeLook(
            ComponentPlayer player, Dictionary<string, string> errors)
        {
            ComponentBody body = player.ComponentBody;
            if (body == null)
            {
                errors["look.body"] = "ComponentBody is null.";
                return null;
            }

            var look = new Dictionary<string, object>(StringComparer.Ordinal);

            // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
            //         （游戏自身的 yaw 提取公式）
            Quaternion rotation = body.Rotation;
            float yaw = MathUtils.Atan2(
                2f * rotation.Y * rotation.W - 2f * rotation.X * rotation.Z,
                1f - 2f * rotation.Y * rotation.Y - 2f * rotation.Z * rotation.Z);
            look["yawDeg"] = yaw * RadiansToDegrees;

            // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.LookAngles
            //         （X = 头部左右，Y = 俯仰；公开 getter，不需要反射）
            ComponentLocomotion locomotion = player.ComponentLocomotion;
            if (locomotion != null)
            {
                Vector2 angles = locomotion.LookAngles;
                look["pitchDeg"] = angles.Y * RadiansToDegrees;
                look["headYawDeg"] = angles.X * RadiansToDegrees;
            }
            else
            {
                look["pitchDeg"] = null;
                look["headYawDeg"] = null;
                errors["look.pitchDeg"] = "ComponentLocomotion is null.";
            }

            // Source: Survivalcraft/Game/ComponentCreatureModel.cs:ComponentCreatureModel.EyePosition/EyeRotation
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model != null)
            {
                look["eyePosition"] = PlayerObserver.Vector3ToDictionary(model.EyePosition);
                look["eyeRotation"] = QuaternionToDictionary(model.EyeRotation);
            }
            else
            {
                look["eyePosition"] = null;
                look["eyeRotation"] = null;
                errors["look.eye"] = "ComponentCreatureModel is null.";
            }

            return look;
        }

        private static Dictionary<string, object> DescribeHealth(
            ComponentPlayer player, Dictionary<string, string> errors)
        {
            // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Health/Air/AirCapacity/
            //         HealthChange/BreathingMode/IsInvulnerable/CauseOfDeath/DeathTime
            ComponentHealth health = player.ComponentHealth;
            if (health == null)
            {
                errors["health.component"] = "ComponentHealth is null.";
                return null;
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["health"] = health.Health,
                ["air"] = health.Air,
                ["airCapacity"] = health.AirCapacity,
                ["healthChange"] = health.HealthChange,
                ["breathingMode"] = health.BreathingMode.ToString(),
                ["isInvulnerable"] = health.IsInvulnerable,
                ["causeOfDeath"] = health.CauseOfDeath,
                ["deathTime"] = health.DeathTime.HasValue ? (object)health.DeathTime.Value : null,
                ["corpseDuration"] = health.CorpseDuration
            };
        }

        /// <summary>死亡 = 生命值 <= 0，或 PlayerData 状态机已进入 PlayerDead。</summary>
        private static object ComputeIsDead(ComponentHealth health, string playerState)
        {
            bool stateDead = string.Equals(playerState, "PlayerDead", StringComparison.Ordinal);
            if (health != null)
                return health.Health <= 0f || stateDead;
            if (playerState != null)
                return stateDead;
            return null;
        }

        private static Dictionary<string, object> DescribeSleep(
            ComponentPlayer player, Dictionary<string, string> errors)
        {
            // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.IsSleeping/SleepFactor
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null)
            {
                errors["sleep.component"] = "ComponentSleep is null.";
                return null;
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["isSleeping"] = sleep.IsSleeping,
                ["sleepFactor"] = sleep.SleepFactor
            };
            try
            {
                // 只读该字段：区分"主动入睡"与"困到昏睡"（与 obs.player 同一依据）。
                // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.m_allowManualWakeUp
                result["allowManualWakeUp"] = GetParentFields().GetParentField<bool>(
                    sleep, "m_allowManualWakeUp", typeof(ComponentSleep));
            }
            catch (Exception exception)
            {
                result["allowManualWakeUp"] = null;
                errors["sleep.allowManualWakeUp"] = DescribeException(exception);
            }
            return result;
        }

        private static Dictionary<string, object> DescribeFire(
            ComponentPlayer player, Dictionary<string, string> errors)
        {
            // Source: Survivalcraft/Game/ComponentOnFire.cs:ComponentOnFire.IsOnFire/TouchesFire
            ComponentOnFire fire = player.Entity.FindComponent<ComponentOnFire>(false);
            if (fire == null)
            {
                errors["fire.component"] = "ComponentOnFire is not present on this entity.";
                return null;
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["isOnFire"] = fire.IsOnFire,
                ["touchesFire"] = fire.TouchesFire
            };
        }

        private static Dictionary<string, object> DescribeFlu(
            ComponentPlayer player, Dictionary<string, string> errors)
        {
            // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.HasFlu/IsCoughing
            ComponentFlu flu = player.ComponentFlu;
            if (flu == null)
            {
                errors["flu.component"] = "ComponentFlu is null.";
                return null;
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["hasFlu"] = flu.HasFlu,
                ["isCoughing"] = flu.IsCoughing
            };
        }

        private static Dictionary<string, object> DescribeSickness(
            ComponentPlayer player, Dictionary<string, string> errors)
        {
            // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.IsSick/IsPuking
            ComponentSickness sickness = player.ComponentSickness;
            if (sickness == null)
            {
                errors["sickness.component"] = "ComponentSickness is null.";
                return null;
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["isSick"] = sickness.IsSick,
                ["isPuking"] = sickness.IsPuking
            };
        }

        private static Dictionary<string, object> DescribeVitals(
            ComponentPlayer player, Dictionary<string, string> errors)
        {
            // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Food/Stamina/
            //         Sleep/Temperature/Wetness/EnvironmentTemperature
            ComponentVitalStats vitals = player.ComponentVitalStats;
            if (vitals == null)
            {
                errors["vitals.component"] = "ComponentVitalStats is null.";
                return null;
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["food"] = vitals.Food,
                ["stamina"] = vitals.Stamina,
                ["sleep"] = vitals.Sleep,
                ["temperature"] = vitals.Temperature,
                ["wetness"] = vitals.Wetness,
                ["environmentTemperature"] = vitals.EnvironmentTemperature
            };
            result["satiation"] = DescribeSatiation(vitals, errors);
            return result;
        }

        /// <summary>
        /// `ComponentVitalStats` 的饱食度是私有的 `Dictionary&lt;int, float&gt; m_satiation`
        /// （key = 方块 contents）。只读枚举，按数值从大到小给前 N 条，并始终给出总和与条数。
        /// Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.m_satiation
        /// </summary>
        private static Dictionary<string, object> DescribeSatiation(
            ComponentVitalStats vitals, Dictionary<string, string> errors)
        {
            try
            {
                Dictionary<int, float> satiation = GetParentFields().GetParentField<Dictionary<int, float>>(
                    vitals, "m_satiation", typeof(ComponentVitalStats));
                if (satiation == null)
                {
                    errors["vitals.satiation"] = "ComponentVitalStats.m_satiation is null.";
                    return null;
                }

                var rows = new List<KeyValuePair<int, float>>(satiation);
                rows.Sort((left, right) => right.Value.CompareTo(left.Value));

                float total = 0f;
                for (int i = 0; i < rows.Count; i++)
                    total += rows[i].Value;

                var entries = new List<Dictionary<string, object>>(
                    Math.Min(rows.Count, SatiationEntryLimit));
                for (int i = 0; i < rows.Count && entries.Count < SatiationEntryLimit; i++)
                {
                    entries.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["contents"] = rows[i].Key,
                        ["value"] = rows[i].Value
                    });
                }

                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["total"] = total,
                    ["count"] = rows.Count,
                    ["entries"] = entries,
                    ["entriesOmitted"] = rows.Count - entries.Count
                };
            }
            catch (Exception exception)
            {
                errors["vitals.satiation"] = DescribeException(exception);
                return null;
            }
        }

        private static Dictionary<string, object> DescribeInventory(
            ComponentPlayer player, Dictionary<string, string> errors, int slotLimit)
        {
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Inventory
            // Source: Survivalcraft/Game/IInventory.cs:IInventory.SlotsCount/ActiveSlotIndex/GetSlotValue/GetSlotCount
            ComponentMiner miner = player.ComponentMiner;
            if (miner == null)
            {
                errors["inventory.miner"] = "ComponentMiner is null.";
                return null;
            }
            IInventory inventory = miner.Inventory;
            if (inventory == null)
            {
                errors["inventory.component"] = "ComponentMiner.Inventory is null.";
                return null;
            }

            int slotsCount = inventory.SlotsCount;
            int activeSlot = inventory.ActiveSlotIndex;

            object holding = null;
            if (activeSlot < 0 || activeSlot >= slotsCount)
            {
                errors["inventory.activeSlot"] =
                    "ActiveSlotIndex " + activeSlot.ToString(CultureInfo.InvariantCulture)
                    + " is outside 0.." + (slotsCount - 1).ToString(CultureInfo.InvariantCulture) + ".";
            }
            else
            {
                int activeValue = inventory.GetSlotValue(activeSlot);
                holding = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["value"] = activeValue,
                    ["contents"] = Terrain.ExtractContents(activeValue),
                    ["data"] = Terrain.ExtractData(activeValue),
                    ["count"] = inventory.GetSlotCount(activeSlot)
                };
            }

            var slots = new List<Dictionary<string, object>>(Math.Min(slotsCount, slotLimit));
            int nonEmpty = 0;
            for (int i = 0; i < slotsCount; i++)
            {
                int value = inventory.GetSlotValue(i);
                int count = inventory.GetSlotCount(i);
                if (value == 0 && count == 0)
                    continue;

                nonEmpty++;
                if (slots.Count >= slotLimit)
                    continue;

                slots.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["slot"] = i,
                    ["value"] = value,
                    ["contents"] = Terrain.ExtractContents(value),
                    ["data"] = Terrain.ExtractData(value),
                    ["count"] = count
                });
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["slotsCount"] = slotsCount,
                ["activeSlot"] = activeSlot,
                ["holding"] = holding,
                ["slots"] = slots,
                ["slotsNonEmpty"] = nonEmpty,
                ["slotsReturned"] = slots.Count,
                ["slotsOmitted"] = nonEmpty - slots.Count,
                ["slotLimit"] = slotLimit
            };
        }

        private static Dictionary<string, object> DescribeAction(
            ComponentPlayer player, Dictionary<string, string> errors)
        {
            var action = new Dictionary<string, object>(StringComparer.Ordinal);

            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.DigCellFace/DigTime/
            //         DigProgress/AttackPower/PokingPhase/ActiveBlockValue
            ComponentMiner miner = player.ComponentMiner;
            if (miner != null)
            {
                CellFace? digCellFace = miner.DigCellFace;
                action["digging"] = digCellFace.HasValue;
                action["digCell"] = digCellFace.HasValue
                    ? CellFaceToDictionary(digCellFace.Value)
                    : null;
                action["digProgress"] = miner.DigProgress;
                action["digTime"] = miner.DigTime;
                action["attackPower"] = miner.AttackPower;
                action["pokingPhase"] = miner.PokingPhase;
                action["activeBlockValue"] = miner.ActiveBlockValue;
            }
            else
            {
                action["digging"] = null;
                action["digCell"] = null;
                action["digProgress"] = null;
                action["digTime"] = null;
                action["attackPower"] = null;
                action["pokingPhase"] = null;
                action["activeBlockValue"] = null;
                errors["action.miner"] = "ComponentMiner is null.";
            }

            // Source: Survivalcraft/Game/ComponentCreatureModel.cs:ComponentCreatureModel.
            //         MovementAnimationPhase/DeathPhase/Bob/LookAtOrder
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model != null)
            {
                action["movementAnimationPhase"] = model.MovementAnimationPhase;
                action["deathPhase"] = model.DeathPhase;
                action["bob"] = model.Bob;
                action["lookAtOrder"] = model.LookAtOrder.HasValue
                    ? PlayerObserver.Vector3ToDictionary(model.LookAtOrder.Value)
                    : null;
            }
            else
            {
                action["movementAnimationPhase"] = null;
                action["deathPhase"] = null;
                action["bob"] = null;
                action["lookAtOrder"] = null;
                errors["action.model"] = "ComponentCreatureModel is null.";
            }

            // Source: Survivalcraft/Game/PlayerInput.cs:PlayerInput.Aim/Dig/Hit/Interact/Jump/Move/Look
            ComponentInput input = player.ComponentInput;
            if (input != null)
            {
                PlayerInput playerInput = input.PlayerInput;
                action["aiming"] = playerInput.Aim.HasValue;
                action["diggingInput"] = playerInput.Dig.HasValue;
                action["hittingInput"] = playerInput.Hit.HasValue;
                action["interactingInput"] = playerInput.Interact.HasValue;
                action["jumping"] = playerInput.Jump;
                action["move"] = PlayerObserver.Vector3ToDictionary(playerInput.Move);
                action["lookInput"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = playerInput.Look.X,
                    ["y"] = playerInput.Look.Y
                };
            }
            else
            {
                action["aiming"] = null;
                action["diggingInput"] = null;
                action["hittingInput"] = null;
                action["interactingInput"] = null;
                action["jumping"] = null;
                action["move"] = null;
                action["lookInput"] = null;
                errors["action.input"] = "ComponentInput is null.";
            }

            return action;
        }

        // ---------------------------------------------------------------- 小工具

        /// <summary>读一个字段：异常时写 null + 原因，绝不把异常抛给命令层。</summary>
        private static void Put(
            Dictionary<string, object> entry, Dictionary<string, string> errors,
            string key, Func<object> read)
        {
            try
            {
                entry[key] = read();
            }
            catch (Exception exception)
            {
                entry[key] = null;
                errors[key] = DescribeException(exception);
            }
        }

        private static IModParentField GetParentFields()
        {
            if (ModManager.Instance == null)
                throw new InvalidOperationException("SuAPI ModManager.Instance is not available.");
            IModParentField fields = ModManager.Instance.ModParentField;
            if (fields == null)
                throw new InvalidOperationException("SuAPI ModParentField is not available.");
            return fields;
        }

        private static string PlayerClassToString(PlayerClass playerClass)
        {
            switch (playerClass)
            {
                case PlayerClass.Male:
                    return "male";
                case PlayerClass.Female:
                    return "female";
                default:
                    throw new InvalidOperationException(
                        "Unknown PlayerClass value: " + ((int)playerClass).ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string FormatIndices(List<PlayerData> players)
        {
            if (players.Count == 0)
                return "<none>";
            var indices = new List<string>(players.Count);
            for (int i = 0; i < players.Count; i++)
                indices.Add(players[i].PlayerIndex.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", indices);
        }

        private static Dictionary<string, object> QuaternionToDictionary(Quaternion value)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = value.X,
                ["y"] = value.Y,
                ["z"] = value.Z,
                ["w"] = value.W
            };
        }

        private static Dictionary<string, object> CellFaceToDictionary(CellFace value)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = value.X,
                ["y"] = value.Y,
                ["z"] = value.Z,
                ["face"] = value.Face
            };
        }

        private static string DescribeException(Exception exception)
        {
            return exception.GetType().Name + ": " + exception.Message;
        }
    }
}

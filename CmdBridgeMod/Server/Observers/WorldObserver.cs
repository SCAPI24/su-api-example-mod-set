using Engine;
using Game;
using GameEntitySystem;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 世界观察（只读）：区域方块扫描、半径内实体、时间/季节/天气。
    ///
    /// 这是 AI 相对视觉的信息优势所在，因此允许无限读取；但必须限制单次返回体量，
    /// 否则一次请求就能拉爆响应并拖慢游戏线程。
    /// </summary>
    internal static class WorldObserver
    {
        private const int MaxRadius = 32;
        private const int MaxBlocks = 1024;
        private const int MaxEntities = 256;
        private const int MaxComponentNames = 12;

        // ---------------------------------------------------------------- 方块

        public static Dictionary<string, object> DescribeBlocks(
            int centerX, int centerY, int centerZ, int radius, int max)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            SubsystemTerrain subsystem = GetTerrain();
            if (subsystem == null || subsystem.Terrain == null)
                throw new BridgeCommandException("world_not_loaded", "No terrain is available.");

            int clampedRadius = MathUtils.Clamp(radius, 0, MaxRadius);
            int limit = MathUtils.Clamp(max, 1, MaxBlocks);
            Terrain terrain = subsystem.Terrain;

            var blocks = new List<Dictionary<string, object>>();
            int total = 0;
            int skippedY = 0;

            for (int x = centerX - clampedRadius; x <= centerX + clampedRadius; x++)
            {
                for (int z = centerZ - clampedRadius; z <= centerZ + clampedRadius; z++)
                {
                    for (int y = centerY - clampedRadius; y <= centerY + clampedRadius; y++)
                    {
                        if (y < 0 || y > 254)
                        {
                            skippedY++;
                            continue;
                        }
                        int contents = terrain.GetCellContentsFast(x, y, z);
                        if (contents == 0)
                            continue;
                        total++;
                        if (blocks.Count >= limit)
                            continue;
                        var block = BlocksManager.Blocks[contents];
                        blocks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["x"] = x,
                            ["y"] = y,
                            ["z"] = z,
                            ["contents"] = contents,
                            ["blockType"] = block != null ? block.GetType().Name : null
                        });
                    }
                }
            }

            result["center"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = centerX,
                ["y"] = centerY,
                ["z"] = centerZ
            };
            result["radius"] = clampedRadius;
            result["blocks"] = blocks;
            result["totalNonAir"] = total;
            result["truncated"] = total > blocks.Count;
            result["skippedOutOfRangeY"] = skippedY;
            return result;
        }

        // ---------------------------------------------------------------- 实体

        public static Dictionary<string, object> DescribeEntities(float radius, int max)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (GameManager.Project == null)
                throw new BridgeCommandException("world_not_loaded", "No world is loaded.");

            SubsystemBodies bodies = GameManager.Project.FindSubsystem<SubsystemBodies>(false);
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer self = players != null && players.ComponentPlayers.Count > 0
                ? players.ComponentPlayers[0]
                : null;

            float clampedRadius = MathUtils.Clamp(radius, 1f, 256f);
            int limit = MathUtils.Clamp(max, 1, MaxEntities);
            Vector3 origin = self != null ? self.ComponentBody.Position : Vector3.Zero;

            var entities = new List<Dictionary<string, object>>();
            int considered = 0;
            if (bodies != null)
            {
                foreach (ComponentBody body in bodies.Bodies)
                {
                    if (body == null || body.Entity == null)
                        continue;
                    float distance = Vector3.Distance(body.Position, origin);
                    if (distance > clampedRadius)
                        continue;
                    considered++;
                    if (entities.Count >= limit)
                        continue;

                    var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["handle"] = body.GetHashCode(),
                        ["position"] = PlayerObserver.Vector3ToDictionary(body.Position),
                        ["velocity"] = PlayerObserver.Vector3ToDictionary(body.Velocity),
                        ["distance"] = distance,
                        ["isSelf"] = ReferenceEquals(body, self != null ? self.ComponentBody : null),
                        ["isCreature"] = body.Entity.FindComponent<ComponentCreature>(false) != null,
                        ["isPlayer"] = body.Entity.FindComponent<ComponentPlayer>(false) != null
                    };

                    Vector3 boxSize = body.BoxSize;
                    entry["boxSize"] = PlayerObserver.Vector3ToDictionary(boxSize);

                    var componentNames = new List<string>();
                    ReadOnlyList<Component> components = body.Entity.Components;
                    for (int i = 0; i < components.Count && componentNames.Count < MaxComponentNames; i++)
                    {
                        Component component = components[i];
                        if (component != null)
                            componentNames.Add(component.GetType().Name);
                    }
                    entry["components"] = componentNames;
                    entities.Add(entry);
                }
            }

            entities.Sort((left, right) =>
                ((float)left["distance"]).CompareTo((float)right["distance"]));

            result["origin"] = PlayerObserver.Vector3ToDictionary(origin);
            result["radius"] = clampedRadius;
            result["entities"] = entities;
            result["consideredInRadius"] = considered;
            result["truncated"] = considered > entities.Count;
            return result;
        }

        // ---------------------------------------------------------------- 时间

        public static Dictionary<string, object> DescribeTime()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (GameManager.Project == null)
                throw new BridgeCommandException("world_not_loaded", "No world is loaded.");

            try
            {
                SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(false);
                if (gameInfo != null)
                {
                    result["gameMode"] = gameInfo.WorldSettings.GameMode.ToString();
                    result["timeOfDayMode"] = gameInfo.WorldSettings.TimeOfDayMode.ToString();
                    result["totalElapsedGameTime"] = gameInfo.TotalElapsedGameTime;
                    result["areSeasonsChanging"] = gameInfo.WorldSettings.AreSeasonsChanging;
                }
            }
            catch
            {
            }

            try
            {
                SubsystemTimeOfDay timeOfDay = GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(false);
                if (timeOfDay != null)
                {
                    float now = timeOfDay.TimeOfDay;
                    float nightStart = timeOfDay.NightStart;
                    float dawnStart = timeOfDay.DawnStart;
                    // TimeOfDay 是 [0,1) 的一天占比（DayDuration = 1200 秒）。
                    // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:16-42, 78-90
                    bool isNight = nightStart <= dawnStart
                        ? now >= nightStart && now < dawnStart
                        : now >= nightStart || now < dawnStart;
                    result["day"] = timeOfDay.Day;
                    result["timeOfDay"] = now;
                    result["hour"] = now * 24f;
                    result["isNight"] = isNight;
                    result["nightStart"] = nightStart;
                    result["dawnStart"] = dawnStart;
                    result["dayStart"] = timeOfDay.DayStart;
                    result["duskStart"] = timeOfDay.DuskStart;
                    result["dayDurationSeconds"] = SubsystemTimeOfDay.DayDuration;
                }
            }
            catch
            {
            }

            try
            {
                SubsystemSeasons seasons = GameManager.Project.FindSubsystem<SubsystemSeasons>(false);
                if (seasons != null)
                {
                    result["season"] = seasons.Season.ToString();
                    result["timeOfSeason"] = seasons.TimeOfSeason;
                }
            }
            catch
            {
            }

            try
            {
                SubsystemWeather weather = GameManager.Project.FindSubsystem<SubsystemWeather>(false);
                if (weather != null)
                {
                    result["precipitationIntensity"] = weather.PrecipitationIntensity;
                    result["isPrecipitationStarted"] = weather.IsPrecipitationStarted;
                }
            }
            catch
            {
            }

            return result;
        }

        private static SubsystemTerrain GetTerrain()
        {
            if (GameManager.Project == null)
                return null;
            return GameManager.Project.FindSubsystem<SubsystemTerrain>(false);
        }
    }
}

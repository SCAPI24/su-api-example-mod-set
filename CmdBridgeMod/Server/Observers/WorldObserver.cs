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
                        // 2026-10-01：补 `data`/`light`/`fireDuration`/`isFire`（用户要求：排障优先用桥）。
                        // 火焰可见性完全取决于火格(data=0 的火格画面上没有任何火面)，
                        // 而"能不能被点燃"取决于 `Blocks[contents].FireDuration != 0` ——
                        // 这两条以前必须加探针才能看到，现在一次 `world blocks` 就能读。
                        int cellValue = terrain.GetCellValue(x, y, z);
                        blocks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["x"] = x,
                            ["y"] = y,
                            ["z"] = z,
                            ["contents"] = contents,
                            ["blockType"] = block != null ? block.GetType().Name : null,
                            ["data"] = Terrain.ExtractData(cellValue),
                            ["light"] = Terrain.ExtractLight(cellValue),
                            ["fireDuration"] = block != null ? block.FireDuration : 0f,
                            ["isFire"] = contents == 104
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

        /// <summary>
        /// 主机火表（只读）：`SubsystemFireBlockBehavior.m_fireData` 的每个格 + 该格的 contents/data。
        /// 2026-10-01 用户要求（桥优先）：判断"有燃烧声音但看不到火焰"时，看火格 **data** ——
        /// 引擎在 `SetCellOnFire` 里把可见朝向写进 data（`num3 |= (1 << OppositeFace(i)) & 0xF`），
        /// data=0 的火格在画面上没有任何火面。
        /// </summary>
        public static Dictionary<string, object> DescribeFire()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (GameManager.Project == null)
                throw new BridgeCommandException("world_not_loaded", "No world is loaded.");
            SubsystemFireBlockBehavior fire =
                GameManager.Project.FindSubsystem<SubsystemFireBlockBehavior>(false);
            if (fire == null)
                throw new BridgeCommandException("no_fire_subsystem",
                    "SubsystemFireBlockBehavior is not present.");
            var field = typeof(SubsystemFireBlockBehavior).GetField("m_fireData",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var table = field != null ? field.GetValue(fire) as System.Collections.IDictionary : null;
            if (table == null)
                throw new BridgeCommandException("no_fire_data", "Fire table is not readable.");
            Terrain terrain = GetTerrain()?.Terrain;
            var entries = new List<Dictionary<string, object>>();
            foreach (System.Collections.DictionaryEntry entry in table)
            {
                if (!(entry.Key is Point3 point))
                    continue;
                int value = terrain != null ? terrain.GetCellValue(point.X, point.Y, point.Z) : 0;
                int contents = terrain != null
                    ? terrain.GetCellContents(point.X, point.Y, point.Z) : -1;
                entries.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = point.X,
                    ["y"] = point.Y,
                    ["z"] = point.Z,
                    ["contents"] = contents,
                    ["blockType"] = contents >= 0 && contents < BlocksManager.Blocks.Length &&
                        BlocksManager.Blocks[contents] != null
                        ? BlocksManager.Blocks[contents].GetType().Name : null,
                    ["data"] = terrain != null ? Terrain.ExtractData(value) : -1,
                    ["light"] = terrain != null ? Terrain.ExtractLight(value) : -1,
                    ["isFire"] = contents == 104,
                    ["remaining"] = entry.Value != null ? entry.Value.ToString() : null,
                    ["remainingType"] = entry.Value != null ? entry.Value.GetType().Name : null
                });
            }
            entries.Sort((left, right) =>
                ((int)left["x"]).CompareTo((int)right["x"]));
            result["count"] = entries.Count;
            result["entries"] = entries;
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

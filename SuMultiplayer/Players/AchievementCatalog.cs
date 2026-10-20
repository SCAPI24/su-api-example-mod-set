using System;
using System.Collections.Generic;
using System.Globalization;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 目录里的一条成就：稳定 id + 展示名 + 判据（某个权威统计 ≥ 阈值）。
    ///
    /// 为什么用"统计阈值"而不是引擎成就系统：本引擎版本没有成就子系统，
    /// 而统计是**主机权威**的既有事实（<see cref="PlayerStatSet"/>）。这样成就完全由主机判定、
    /// 可持久化 / 可重放 / 可自检，客户端只能展示。
    /// </summary>
    public sealed class AchievementDefinition
    {
        public AchievementDefinition(string id, string displayName, string description,
            string statName, double threshold)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? Id;
            Description = description ?? string.Empty;
            StatName = statName ?? string.Empty;
            Threshold = threshold;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Description { get; }

        /// <summary>判据统计字段名（与 <c>Game.PlayerStats</c> 的 [Stat] 字段同名）。</summary>
        public string StatName { get; }

        /// <summary>阈值：统计值 ≥ 它就解锁。</summary>
        public double Threshold { get; }
    }

    /// <summary>
    /// SuMultiplayer 成就目录（纯逻辑，无静态运行时状态；下表是**编译期常量数据**）。
    ///
    /// 判据全部落在引擎已有的统计字段上（Source: Survivalcraft/Game/PlayerStats.cs:PlayerStats），
    /// 因此主机只要拿到权威统计就能判定，不需要在引擎各处埋点。
    /// 新增 / 删除成就时**只允许追加 id**（旧记录里的 id 永远保留；未知 id 前向兼容）。
    /// </summary>
    public static class AchievementCatalog
    {
        /// <summary>本目录所有成就 id 的前缀（与第三方成就区分）。</summary>
        public const string IdPrefix = "sump.";

        private static readonly AchievementDefinition[] Definitions =
        {
            new AchievementDefinition("sump.walk.100", "First Steps",
                "Walk 100 m (DistanceWalked)", "DistanceWalked", 100.0),
            new AchievementDefinition("sump.travel.1000", "Wanderer",
                "Travel 1000 m in total (DistanceTravelled)", "DistanceTravelled", 1000.0),
            new AchievementDefinition("sump.dig.100", "Digger",
                "Dig 100 blocks (BlocksDug)", "BlocksDug", 100.0),
            new AchievementDefinition("sump.place.100", "Builder",
                "Place 100 blocks (BlocksPlaced)", "BlocksPlaced", 100.0),
            new AchievementDefinition("sump.craft.50", "Craftsman",
                "Craft 50 items (ItemsCrafted)", "ItemsCrafted", 50.0),
            new AchievementDefinition("sump.kills.10", "Hunter",
                "Kill 10 land creatures (LandCreatureKills)", "LandCreatureKills", 10.0),
            new AchievementDefinition("sump.level.5", "Seasoned",
                "Reach level 5 (HighestLevel)", "HighestLevel", 5.0),
            new AchievementDefinition("sump.dive.20", "Deep Diver",
                "Dive 20 m deep (DeepestDive)", "DeepestDive", 20.0),
            new AchievementDefinition("sump.altitude.150", "Mountaineer",
                "Reach altitude 150 (HighestAltitude)", "HighestAltitude", 150.0),
            new AchievementDefinition("sump.sleep.10", "Well Rested",
                "Sleep 10 times (TimesWentToSleep)", "TimesWentToSleep", 10.0),
            new AchievementDefinition("sump.eat.50", "Well Fed",
                "Eat 50 food items (FoodItemsEaten)", "FoodItemsEaten", 50.0)
        };

        private static readonly Dictionary<string, AchievementDefinition> ById = BuildIndex();

        private static Dictionary<string, AchievementDefinition> BuildIndex()
        {
            var index = new Dictionary<string, AchievementDefinition>(Definitions.Length,
                StringComparer.Ordinal);
            foreach (AchievementDefinition definition in Definitions)
                index[definition.Id] = definition;
            return index;
        }

        public static IReadOnlyList<AchievementDefinition> All => Definitions;

        public static bool IsKnown(string id) => id != null && ById.ContainsKey(id);

        public static bool TryGet(string id, out AchievementDefinition definition)
        {
            definition = null;
            return id != null && ById.TryGetValue(id, out definition);
        }

        /// <summary>展示名；未知 id 原样返回（并由调用方计数 achvUnknown）。</summary>
        public static string DisplayName(string id) =>
            TryGet(id, out AchievementDefinition definition) ? definition.DisplayName : id;

        public static string Describe(string id) =>
            TryGet(id, out AchievementDefinition definition)
                ? definition.DisplayName + " — " + definition.Description
                : id + " (unknown achievement id; kept for forward compatibility)";

        /// <summary>
        /// 判定**新解锁**的成就，追加到 <paramref name="target"/>（不改账本，由调用方决定是否合并）。
        /// 统计缺失或无法解析的判据计入 <paramref name="unparsable"/>（明确计数，不当成 0）。
        /// </summary>
        public static int Evaluate(PlayerStatSet stats, AchievementLedger already,
            List<string> target, out int unparsable)
        {
            unparsable = 0;
            if (target == null) throw new ArgumentNullException(nameof(target));
            int found = 0;
            if (stats == null) return 0;
            foreach (AchievementDefinition definition in Definitions)
            {
                if (already != null && already.Contains(definition.Id)) continue;
                if (!stats.TryGet(definition.StatName, out string text)) continue;
                if (!TryReadNumber(text, out double value))
                {
                    unparsable++;
                    continue;
                }
                if (double.IsNaN(value) || value < definition.Threshold) continue;
                target.Add(definition.Id);
                found++;
            }
            return found;
        }

        /// <summary>
        /// 读统计文本为数值。引擎的 double "R" 文本在 .NET 上可能写成 "Infinity" 或 "∞"
        /// （<c>PlayerStats.HighestAltitude</c> 的初始值是 ±∞），两种都接受；NaN 视为不可比较。
        /// 其他无法解析的文本 → false（调用方计入 unparsable）。
        /// </summary>
        public static bool TryReadNumber(string text, out double value)
        {
            value = 0.0;
            if (string.IsNullOrEmpty(text)) return false;
            if (string.Equals(text, "Infinity", StringComparison.Ordinal) ||
                string.Equals(text, "∞", StringComparison.Ordinal) ||
                string.Equals(text, "+Infinity", StringComparison.Ordinal))
            {
                value = double.PositiveInfinity;
                return true;
            }
            if (string.Equals(text, "-Infinity", StringComparison.Ordinal) ||
                string.Equals(text, "-∞", StringComparison.Ordinal))
            {
                value = double.NegativeInfinity;
                return true;
            }
            if (string.Equals(text, "NaN", StringComparison.Ordinal))
            {
                value = double.NaN;
                return true;
            }
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out value);
        }
    }
}

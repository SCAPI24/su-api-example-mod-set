using Game;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GmMod
{
    /// <summary>
    /// GM 工具提交的数据修改 operation。
    ///
    /// 注意：这**不是**联机 mod 的内置操作，而是"通用数据修改"里的**世界设置**类别：
    /// 联机 mod 只做"把 WorldSettings 的字段改成给定值"这件通用的事，**它不知道季节是什么**；
    /// 季节/日期语义完全由本 mod 通过字段名 `TimeOfYear` 表达。
    ///
    /// 落地与分发**都在主机**：主机审批后自己写 `WorldSettings`，再由主机随世界信息广播
    /// 把整份设置快照发给所有客户端 —— 本 mod 不做任何落地/分发/同步，主机端也不需要安装本 mod。
    /// </summary>
    internal static class GmOperations
    {
        /// <summary>联机 mod 的通用世界设置 operation（名字里没有季节）。</summary>
        public const string SetWorldSettings = "ScMP.Data.WorldSettings";

        /// <summary>联机 mod 的通用地图方块 operation（主机直接改权威地形并广播给全端）。</summary>
        public const string SetCells = "ScMP.Data.Cells";

        public const string ModId = "GmMod";

        /// <summary>季节/日期字段名（引擎 `WorldSettings.TimeOfYear`，0..1）。</summary>
        public const string TimeOfYearField = "TimeOfYear";
    }

    /// <summary>
    /// `ScMP.Data.WorldSettings` 的载荷：**纯文本**，每行 `字段名\t值`（UTF-8）。
    ///
    /// 不用 JSON：本 mod 的 DLL 会被 Obfuscar 改名，JSON 依赖属性名会静默解析成默认值
    /// （实测把季节写成了夏至）；纯文本是双方约定的字面格式，不受混淆影响。
    /// </summary>
    internal static class GmPayloadCodec
    {
        public static byte[] Encode(params KeyValuePair<string, string>[] entries)
        {
            var builder = new StringBuilder();
            if (entries != null)
            {
                foreach (KeyValuePair<string, string> entry in entries)
                {
                    builder.Append(Sanitize(entry.Key));
                    builder.Append('\t');
                    builder.Append(Sanitize(entry.Value));
                    builder.Append('\n');
                }
            }
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        public static byte[] EncodeTimeOfYear(float timeOfYear) =>
            Encode(new KeyValuePair<string, string>(GmOperations.TimeOfYearField,
                timeOfYear.ToString("R", CultureInfo.InvariantCulture)));

        /// <summary>`ScMP.Data.Cells` 的载荷：每行 `x,y,z,contents,data`（主机按原地形保留光照位）。</summary>
        public static byte[] EncodeCell(int x, int y, int z, int contents, int data) =>
            Encoding.UTF8.GetBytes(x.ToString(CultureInfo.InvariantCulture) + "," +
                y.ToString(CultureInfo.InvariantCulture) + "," +
                z.ToString(CultureInfo.InvariantCulture) + "," +
                contents.ToString(CultureInfo.InvariantCulture) + "," +
                data.ToString(CultureInfo.InvariantCulture) + "\n");

        private static string Sanitize(string text) =>
            (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>
    /// 季节/时段档位：四季 × 初/仲/晚 = 12 档。
    ///
    /// 数值口径直接取自引擎（`SubsystemSeasons`）：夏 0.00、秋 0.25、冬 0.50、春 0.75，
    /// 每季跨度 0.25；"初/仲/晚"取季内 0.1 / 0.5 / 0.9 处，
    /// 与引擎 `SubsystemSeasons.GetTimeOfYearName` 的 Early/Mid/Late 判定一致。
    /// 例：初冬 = 0.50 + 0.1×0.25 = 0.525。
    /// </summary>
    internal static class GmSeasons
    {
        // 每季跨度 0.25（引擎的季节起点是 static readonly，不能当 const 用）
        private static readonly float SeasonSpan =
            SubsystemSeasons.AutumnStart - SubsystemSeasons.SummerStart;

        internal sealed class Slot
        {
            public string Label;
            public Season Season;
            public string Stage; // 初/仲/晚
            public float TimeOfYear;
        }

        private static readonly Season[] Order =
        {
            Season.Spring, Season.Summer, Season.Autumn, Season.Winter
        };

        private static readonly string[] StageNames = { "初", "仲", "晚" };
        private static readonly float[] StageFractions = { 0.1f, 0.5f, 0.9f };

        public static string SeasonName(Season season) => season switch
        {
            Season.Spring => "春",
            Season.Summer => "夏",
            Season.Autumn => "秋",
            _ => "冬"
        };

        public static float StartOf(Season season) => season switch
        {
            Season.Spring => SubsystemSeasons.SpringStart,
            Season.Summer => SubsystemSeasons.SummerStart,
            Season.Autumn => SubsystemSeasons.AutumnStart,
            _ => SubsystemSeasons.WinterStart
        };

        public static List<Slot> BuildSlots()
        {
            var slots = new List<Slot>();
            foreach (Season season in Order)
            {
                for (int stage = 0; stage < StageNames.Length; stage++)
                {
                    float timeOfYear = StartOf(season) + SeasonSpan * StageFractions[stage];
                    slots.Add(new Slot
                    {
                        Label = StageNames[stage] + SeasonName(season) + "  (" +
                            SubsystemSeasons.GetTimeOfYearName(timeOfYear) + ")",
                        Season = season,
                        Stage = StageNames[stage],
                        TimeOfYear = timeOfYear
                    });
                }
            }
            return slots;
        }

        /// <summary>把 0..1 的季节值格式化成 "初冬 (Early Winter)" 这样的可读名。</summary>
        public static string Describe(float timeOfYear)
        {
            string name = SubsystemSeasons.GetTimeOfYearName(timeOfYear);
            string stage = name.StartsWith("Early ", StringComparison.Ordinal) ? "初"
                : name.StartsWith("Late ", StringComparison.Ordinal) ? "晚" : "仲";
            foreach (Season season in Order)
            {
                if (name.EndsWith(season.ToString(), StringComparison.Ordinal))
                    return stage + SeasonName(season) + "  (" + name + ")";
            }
            return name;
        }

        public static string FormatValue(float timeOfYear) =>
            timeOfYear.ToString("0.####", CultureInfo.InvariantCulture);
    }
}

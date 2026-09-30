using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayerAiMod
{
    /// <summary>
    /// 一个事实**这次取到的值**。数据文件里的条件只能看这几个属性
    /// （`hasValue` / `number` / `integer` / `text` / `kind` / `flag`），
    /// 于是"数据能说什么"是有界的、可校验的。
    /// </summary>
    public sealed class DigestFactValue
    {
        public bool HasValue;
        public float? Number;
        public int? Integer;
        public string Text;

        /// <summary>次级文本（准星的 `block`/`entity`、屏幕名…）——给条件用，不进摘要。</summary>
        public string Kind;

        public bool? Flag;

        /// <summary>次级数值（`day` 用它带"几点"）。</summary>
        public float? Aux;
    }

    /// <summary>
    /// **具名事实注册表** —— 摘要字段的取值全在这里（代码侧），数据文件只决定
    /// "用哪些、什么顺序、怎么格式化、进哪一份"。
    ///
    /// 为什么事实必须留在代码：它们是**对游戏状态的读数**（血量/准星/界面/时间），
    /// 每一条都要跟引擎字段对齐；让 JSON 去描述"怎么读 ComponentVitalStats"只会把
    /// 类型错误从编译期推到运行期。这与 verb 词表同一个边界：**新增能力要编译，重新编排不用**。
    /// </summary>
    public static class DigestFacts
    {
        public const string FormatText = "text";
        public const string FormatBand = "band";
        public const string FormatNumBand = "num(band)";
        public const string FormatNumber = "number";
        public const string FormatInteger = "integer";
        public const string FormatBool01 = "bool01";
        public const string FormatBool1 = "bool1";
        public const string FormatDayHour = "dayhour";

        private static readonly string[] s_formats =
        {
            FormatText, FormatBand, FormatNumBand, FormatNumber, FormatInteger,
            FormatBool01, FormatBool1, FormatDayHour
        };

        private static readonly string[] s_sources =
        {
            "health", "food", "sleep", "isNight", "aim", "holding", "stamina", "temperature",
            "wetness", "screen", "uiList", "phase", "uiState", "day", "season", "precipitation",
            "inventoryItems", "gameMode", "canSleep"
        };

        private static readonly string[] s_factAttributes =
        {
            "hasvalue", "number", "integer", "text", "kind", "flag"
        };

        private static readonly string[] s_observes =
        {
            "hasplayer", "worldloaded", "night", "sleeping", "dialogsopen", "modalpanel",
            "screen", "aimkind", "cansleep", "controlsvisible"
        };

        public static List<string> Sources()
        {
            return new List<string>(s_sources);
        }

        public static List<string> Formats()
        {
            return new List<string>(s_formats);
        }

        public static List<string> FactAttributes()
        {
            return new List<string>(s_factAttributes);
        }

        public static List<string> Observes()
        {
            return new List<string>(s_observes);
        }

        public static bool IsKnownSource(string source)
        {
            return IndexOf(s_sources, source) >= 0;
        }

        public static bool IsKnownFormat(string format)
        {
            return IndexOf(s_formats, format) >= 0;
        }

        public static bool IsKnownFactAttribute(string name)
        {
            return IndexOf(s_factAttributes, name) >= 0;
        }

        public static bool IsKnownObserve(string name)
        {
            return IndexOf(s_observes, name) >= 0;
        }

        private static int IndexOf(string[] values, string value)
        {
            if (string.IsNullOrEmpty(value))
                return -1;
            for (int i = 0; i < values.Length; i++)
                if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // ------------------------------------------------------------------ 事实读数

        /// <summary>
        /// 读一条事实。返回 false / <c>HasValue=false</c> 表示"这次没有这个事实"
        /// （该字段整条不发 —— 绝不用 0 或空串糊过去，"unknown" 会被模型当成事实）。
        /// </summary>
        public static bool TryRead(string source, StateInputs inputs, out DigestFactValue value)
        {
            value = null;
            if (inputs == null || string.IsNullOrEmpty(source))
                return false;

            switch (source.Trim().ToLowerInvariant())
            {
                case "health":
                    value = Number(inputs.Health);
                    break;
                case "food":
                    value = Number(inputs.Food);
                    break;
                case "stamina":
                    value = Number(inputs.Stamina);
                    break;
                case "temperature":
                    value = Number(inputs.Temperature);
                    break;
                case "wetness":
                    value = Number(inputs.Wetness);
                    break;
                case "sleep":
                {
                    // 睡着时报 `now`（比"困倦档位"更重要）；否则报档位。
                    if (inputs.Sleeping == true)
                        value = Text("now");
                    else
                        value = Number(inputs.Sleep);
                    break;
                }
                case "isnight":
                    value = inputs.IsNight.HasValue
                        ? Flag(inputs.IsNight.Value, true) : null;
                    break;
                case "aim":
                {
                    // 世界外没有"准星"这回事 → 返回 null 让它**根本不出现**；
                    // 世界里"没瞄任何东西"是字面量 `none`（引擎的取值，不是空串）。
                    if (string.IsNullOrEmpty(inputs.AimKind))
                    {
                        if (!inputs.HasPlayer)
                            return false;
                        value = Text("none");
                        break;
                    }
                    string kind = inputs.AimKind;
                    if (string.Equals(kind, "block", StringComparison.OrdinalIgnoreCase))
                    {
                        string block = StateDigestCompiler.Short(inputs.AimBlockType) ?? "block";
                        string text = inputs.AimDistance.HasValue
                            ? block + "@" + Num(inputs.AimDistance.Value) : block;
                        value = Text(text);
                    }
                    else
                    {
                        value = Text(StateDigestCompiler.Short(kind) ?? kind);
                    }
                    value.Kind = kind;
                    break;
                }
                case "holding":
                    value = Text(StateDigestCompiler.Short(inputs.HoldingBlockType));
                    break;
                case "screen":
                    value = Text(StateDigestCompiler.Short(inputs.Screen));
                    break;
                case "uilist":
                    value = Text(UiListDigest.Compile(inputs.UiList));
                    break;
                case "phase":
                    value = Text(PhaseNames.Of(inputs.WorldLoaded, inputs.HasPlayer));
                    break;
                case "uistate":
                {
                    string text = !inputs.WorldLoaded
                        ? "menu"
                        : !string.IsNullOrEmpty(inputs.ModalPanel)
                            ? StateDigestCompiler.Short(inputs.ModalPanel)
                            : inputs.DialogsOpen ? "dialog" : "hud";
                    value = Text(text);
                    break;
                }
                case "day":
                {
                    if (!inputs.Day.HasValue)
                        return false;
                    value = Integer(inputs.Day.Value);
                    value.Aux = inputs.Hour;
                    break;
                }
                case "season":
                    value = Text(StateDigestCompiler.Short(inputs.Season));
                    break;
                case "precipitation":
                    // 只在**有**降水时发（`> 0.01` 才算），没有就不占位置。
                    value = inputs.Precipitation.HasValue
                        ? Flag(inputs.Precipitation.Value > 0.01f, true) : null;
                    break;
                case "inventoryitems":
                    value = inputs.InventoryItems.HasValue
                        ? Integer(inputs.InventoryItems.Value) : null;
                    break;
                case "gamemode":
                    value = Text(StateDigestCompiler.Short(inputs.GameMode));
                    break;
                case "cansleep":
                    // 只在"能睡"时发（不能睡发出去只会诱发一次注定失败的动作）。
                    value = Flag(inputs.CanSleep == true, inputs.CanSleep == true);
                    break;
                default:
                    return false;
            }

            if (value == null)
                return false;
            value.HasValue = true;
            return true;
        }

        private static DigestFactValue Number(float? value)
        {
            if (!value.HasValue)
                return null;
            return new DigestFactValue { Number = value.Value, HasValue = true };
        }

        private static DigestFactValue Integer(int value)
        {
            return new DigestFactValue { Integer = value, HasValue = true };
        }

        private static DigestFactValue Text(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            return new DigestFactValue { Text = text, HasValue = true };
        }

        private static DigestFactValue Flag(bool value, bool present)
        {
            if (!present)
                return null;
            return new DigestFactValue { Flag = value, HasValue = true };
        }

        // ------------------------------------------------------------------ 原始观察（条件用）

        /// <summary>
        /// 读一条**原始观察**给条件用（与 <see cref="StateInputs"/> 同源：
        /// `family`/`scr`/`ui` 这类"世界外才发"的规则要判 `hasPlayer`，而那不是字段自己的属性）。
        /// </summary>
        public static bool TryReadObserve(string name, StateInputs inputs,
            out float number, out bool flag, out string text)
        {
            number = 0f;
            flag = false;
            text = null;
            if (inputs == null || string.IsNullOrEmpty(name))
                return false;

            switch (name.Trim().ToLowerInvariant())
            {
                case "hasplayer": flag = inputs.HasPlayer; return true;
                case "worldloaded": flag = inputs.WorldLoaded; return true;
                case "dialogsopen": flag = inputs.DialogsOpen; return true;
                case "night":
                    if (!inputs.IsNight.HasValue) return false;
                    flag = inputs.IsNight.Value; return true;
                case "sleeping":
                    if (!inputs.Sleeping.HasValue) return false;
                    flag = inputs.Sleeping.Value; return true;
                case "cansleep":
                    if (!inputs.CanSleep.HasValue) return false;
                    flag = inputs.CanSleep.Value; return true;
                case "controlsvisible":
                    if (!inputs.ControlsVisible.HasValue) return false;
                    flag = inputs.ControlsVisible.Value; return true;
                case "modalpanel": text = inputs.ModalPanel; return true;
                case "screen": text = inputs.Screen; return true;
                case "aimkind": text = inputs.AimKind; return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ 格式化（数据驱动）

        /// <summary>按字段的 `format` + 档位把事实渲染成摘要里的值；返回 null = 这条不发。</summary>
        public static string Apply(DigestFieldSpec field, DigestFactValue fact)
        {
            if (field == null || fact == null || !fact.HasValue)
                return null;

            switch ((field.Format ?? FormatText).ToLowerInvariant())
            {
                case FormatText:
                    return fact.Text;
                case FormatBand:
                    // 事实自己给了文本（例如睡着的 `now`）就用它，否则查档位。
                    if (fact.Text != null)
                        return fact.Text;
                    return fact.Number.HasValue ? DigestBands.Label(field.Bands, fact.Number.Value) : null;
                case FormatNumBand:
                {
                    if (fact.Text != null)
                        return fact.Text;
                    if (!fact.Number.HasValue)
                        return null;
                    string band = DigestBands.Label(field.Bands, fact.Number.Value);
                    string value = Num(fact.Number.Value);
                    return band == null ? value : value + "(" + band + ")";
                }
                case FormatNumber:
                    return fact.Number.HasValue ? Num(fact.Number.Value) : null;
                case FormatInteger:
                    return fact.Integer.HasValue
                        ? fact.Integer.Value.ToString(CultureInfo.InvariantCulture) : null;
                case FormatBool01:
                    return fact.Flag.HasValue ? (fact.Flag.Value ? "1" : "0") : null;
                case FormatBool1:
                    // 只在"真"时发（false 不占位置）。
                    return fact.Flag == true ? "1" : null;
                case FormatDayHour:
                {
                    if (!fact.Integer.HasValue)
                        return null;
                    string day = fact.Integer.Value.ToString(CultureInfo.InvariantCulture);
                    if (!fact.Aux.HasValue)
                        return day;
                    return day + "/" + fact.Aux.Value.ToString("0", CultureInfo.InvariantCulture) + "h";
                }
            }
            return null;
        }

        internal static string Num(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}

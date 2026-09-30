using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 喂给判定模型的**原始状态读数**（只读）。字段是"档位化之前"的量，
    /// 由 CmdBridge 的 `DescribeStateInputs()` 一次问完；缺的字段是 null，
    /// 由 <see cref="StateDigestCompiler"/> 决定"省略还是写成 unknown"——**绝不上层猜**。
    ///
    /// 为什么不直接用字典：编译规则要能被自检逐条钉住，强类型字段才拦得住
    /// "拼错 key 静默变 null"这类错误（摘要字段是长期要调参的东西，静默失效最贵）。
    /// </summary>
    public sealed class StateInputs
    {
        public bool WorldLoaded;
        public bool HasPlayer;

        /// <summary>屏幕名（`GameScreen` / `MainMenuScreen` …）或 null。</summary>
        public string Screen;

        /// <summary>模态面板类型名或 null。</summary>
        public string ModalPanel;

        public bool DialogsOpen;

        /// <summary>HUD 控件是否可见（世界内键鼠游玩时为 false，鼠标交给视角）。</summary>
        public bool? ControlsVisible;

        public bool? Sleeping;
        public bool? CanSleep;
        public string CanSleepReason;

        public float? Health;
        public float? Air;
        public float? Food;
        public float? Stamina;
        public float? Sleep;
        public float? Temperature;
        public float? Wetness;

        public int? Day;
        public float? Hour;
        public bool? IsNight;
        public string Season;
        public float? Precipitation;

        /// <summary>`block` / `entity` / null。</summary>
        public string AimKind;
        public string AimBlockType;
        public float? AimDistance;
        public int? AimCellX, AimCellY, AimCellZ;

        public string HoldingBlockType;
        public int? ActiveSlot;
        public int? InventoryItems;

        public string GameMode;

        /// <summary>
        /// 屏幕上"当前这个列表"的快照（G16）。null = 这个屏幕上没有列表。
        /// 世界外"选世界/翻页"这类决策的答案就是"第几行"，所以它必须进摘要。
        /// </summary>
        public UiListSnapshot UiList;

        /// <summary>把 CmdBridge 的观察字典读成强类型输入（缺字段 = null）。</summary>
        public static StateInputs FromObservation(Dictionary<string, object> raw)
        {
            var inputs = new StateInputs();
            if (raw == null)
                return inputs;

            inputs.WorldLoaded = Bool(raw, "worldLoaded");
            inputs.HasPlayer = Bool(raw, "hasPlayer");
            inputs.Screen = String(raw, "screen");
            inputs.ModalPanel = String(raw, "modalPanel");
            inputs.DialogsOpen = Bool(raw, "dialogsOpen");
            inputs.ControlsVisible = NullableBool(raw, "controlsVisible");
            inputs.Sleeping = NullableBool(raw, "sleeping");
            inputs.CanSleep = NullableBool(raw, "canSleep");
            inputs.CanSleepReason = String(raw, "canSleepReason");

            inputs.Health = Float(raw, "health");
            inputs.Air = Float(raw, "air");
            inputs.Food = Float(raw, "food");
            inputs.Stamina = Float(raw, "stamina");
            inputs.Sleep = Float(raw, "sleep");
            inputs.Temperature = Float(raw, "temperature");
            inputs.Wetness = Float(raw, "wetness");

            inputs.Day = Int(raw, "day");
            inputs.Hour = Float(raw, "hour");
            inputs.IsNight = NullableBool(raw, "isNight");
            inputs.Season = String(raw, "season");
            inputs.Precipitation = Float(raw, "precipitation");

            inputs.AimKind = String(raw, "aimKind");
            inputs.AimBlockType = String(raw, "aimBlockType");
            inputs.AimDistance = Float(raw, "aimDistance");
            Dictionary<string, object> cell = raw.ContainsKey("aimCell") ? raw["aimCell"] as Dictionary<string, object> : null;
            if (cell != null)
            {
                inputs.AimCellX = Int(cell, "x");
                inputs.AimCellY = Int(cell, "y");
                inputs.AimCellZ = Int(cell, "z");
            }

            inputs.HoldingBlockType = String(raw, "holdingBlockType");
            inputs.ActiveSlot = Int(raw, "activeSlot");
            inputs.InventoryItems = Int(raw, "inventoryItems");
            inputs.GameMode = String(raw, "gameMode");
            inputs.UiList = UiListSnapshot.FromObservation(raw);
            return inputs;
        }

        private static object Get(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value : null;
        }

        private static bool Bool(Dictionary<string, object> source, string key)
        {
            object raw = Get(source, key);
            return raw is bool && (bool)raw;
        }

        private static bool? NullableBool(Dictionary<string, object> source, string key)
        {
            object raw = Get(source, key);
            return raw is bool ? (bool?)raw : null;
        }

        private static string String(Dictionary<string, object> source, string key)
        {
            return Get(source, key) as string;
        }

        private static float? Float(Dictionary<string, object> source, string key)
        {
            object raw = Get(source, key);
            if (raw == null)
                return null;
            if (raw is float)
                return (float)raw;
            if (raw is double)
                return (float)(double)raw;
            if (raw is int)
                return (int)raw;
            if (raw is long)
                return (long)raw;
            string text = raw as string;
            float parsed;
            return text != null && float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsed) ? (float?)parsed : (float?)null;
        }

        private static int? Int(Dictionary<string, object> source, string key)
        {
            float? value = Float(source, key);
            return value.HasValue ? (int?)Math.Round(value.Value) : null;
        }
    }

    /// <summary>
    /// 摘要字段的**投影**：把当前生效规格里的一条字段包成"键 + 取值 + 条件"。
    ///
    /// 为什么还留这一层：黑板（`Service.ObserveState`）与自检读的是"字段表"这个形状，
    /// 而真实规则已经搬进 <see cref="DigestSpec"/>。投影保证两处**永远同一份数据**，
    /// 而不是"摘要按规格、黑板按老代码"这种必然漂移的双份实现。
    /// </summary>
    public sealed class DigestField
    {
        public DigestField(DigestFieldSpec spec, Func<StateInputs, string> format,
            Func<StateInputs, bool> include = null, string description = null,
            Func<StateInputs, bool> wireInclude = null)
        {
            Spec = spec;
            Key = spec != null ? spec.Key : null;
            Format = format;
            Include = include;
            Description = description ?? (spec != null ? spec.Description : null);
            WireInclude = wireInclude;
        }

        public DigestFieldSpec Spec { get; }

        public string Key { get; }

        public Func<StateInputs, string> Format { get; }

        /// <summary>为 null = 只要格式化结果非 null 就发。</summary>
        public Func<StateInputs, bool> Include { get; }

        public string Description { get; }

        /// <summary>
        /// **只有"发上线"那条**才额外要求的条件（为 null = 不额外要求）。
        ///
        /// 为什么需要它：实测（第 36 轮）良性值占着开头会把答案带偏，
        /// 所以上线的短状态里 `hp=1(full)`/`night=0`/`temp=normal` 这类**不值得注意的值不发**，
        /// 于是开头必然是"值得注意的那个事实"。
        ///
        /// **为什么不能并进 `Include`**：`Include` 是**共用**的 ——
        /// `Service.ObserveState` 的黑板键（`state.hp`…）也走这张表，
        /// 并进去会让"健康时 `state.hp` 这个键直接消失"，树里按 `state.hp` 分流的条件就静默失效。
        /// </summary>
        public Func<StateInputs, bool> WireInclude { get; }
    }

    /// <summary>
    /// 状态摘要（digest）：把原始读数压成**一行短键值**，喂给 Laya 判定（plan §4.2）。
    ///
    /// 三条实测纪律（§5.4）：
    ///   1. **短**：目标 ≤200 字符（延迟与判准双重约束），所以只发"驱动判定的原语"；
    ///   2. **离散**：数值一律带档位标签（`hp=12(crit)`），模型擅长判"文中明确写的"；
    ///   3. **不 dump**：加 1000 字符无关字段会让答案直接翻转，所以字段是**白名单**。
    ///
    /// **字段表本身现在是数据**（`.digest.json`，见 <see cref="DigestSpec"/>）：
    /// 顺序 / 进不进 wire / 档位阈值 / 预算都能在线改，本类只负责"按规格跑"。
    /// </summary>
    public static class StateDigestCompiler
    {
        private static DigestSpec s_active;

        /// <summary>
        /// 当前生效的规格（恒不为 null：没配 / 没文件 → 内置默认）。
        /// 由 <see cref="DigestCatalog"/> 在读到新文件时写入。
        /// </summary>
        public static DigestSpec Active
        {
            get { return s_active ?? DigestSpec.Default; }
        }

        internal static void SetActive(DigestSpec spec)
        {
            s_active = spec;
        }

        /// <summary>
        /// **摘要布局版本**（plan G18）：来自生效规格里的 `version`。
        ///
        /// 它进请求指纹：版本变了 / 规格内容变了（<see cref="SpecHash"/>），
        /// 去重缓存里的旧答案**不会**被当成本次判定复用。
        /// </summary>
        public static int Version
        {
            get { return Active.Version; }
        }

        /// <summary>生效规格的内容哈希（进请求指纹；改档位阈值也会换指纹）。</summary>
        public static string SpecHash
        {
            get { return Active.SourceHash; }
        }

        /// <summary>摘要字符预算（超出时按字段顺序从后往前丢）。</summary>
        public const int DefaultBudgetChars = 200;

        // ---------------------------------------------------------------- 档位标签
        //
        // 这些函数是"内置默认规格"的**视图**（自检与编辑器显示用），不是第二份阈值表 ——
        // 阈值只有一处：`DigestSpec.Default` 的 bands。

        public static string HealthBand(float value)
        {
            return DigestSpec.DefaultBandLabel("hp", value);
        }

        public static string FoodBand(float value)
        {
            return DigestSpec.DefaultBandLabel("food", value);
        }

        public static string StaminaBand(float value)
        {
            return DigestSpec.DefaultBandLabel("stam", value);
        }

        public static string SleepBand(float value)
        {
            return DigestSpec.DefaultBandLabel("sleep", value);
        }

        /// <summary>体温档位（游戏里 12 左右是舒适区；越低越冷）。</summary>
        public static string TemperatureBand(float value)
        {
            return DigestSpec.DefaultBandLabel("temp", value);
        }

        public static string WetnessBand(float value)
        {
            return DigestSpec.DefaultBandLabel("wet", value);
        }

        // ---------------------------------------------------------------- 字段表（投影）

        /// <summary>
        /// 当前生效规格的字段表（**按重要性排序**：越靠前越先被保留）。
        ///
        /// 黑板与自检读它；编译走同一份规格，所以两边不可能漂移。
        /// </summary>
        public static List<DigestField> Fields()
        {
            DigestSpec spec = Active;
            var fields = new List<DigestField>(spec.Fields.Count);
            for (int i = 0; i < spec.Fields.Count; i++)
            {
                DigestFieldSpec field = spec.Fields[i];
                fields.Add(new DigestField(field,
                    delegate(StateInputs inputs)
                    {
                        DigestFactValue fact;
                        if (!DigestFacts.TryRead(field.Source, inputs, out fact))
                            return null;
                        return DigestFacts.Apply(field, fact);
                    },
                    delegate(StateInputs inputs)
                    {
                        if (field.Rich == null)
                            return true;
                        DigestFactValue fact;
                        if (!DigestFacts.TryRead(field.Source, inputs, out fact))
                            return false;
                        return field.Rich.Evaluate(fact, inputs);
                    },
                    field.Description,
                    field.Wire == null ? (Func<StateInputs, bool>)null
                        : delegate(StateInputs inputs)
                        {
                            DigestFactValue fact;
                            if (!DigestFacts.TryRead(field.Source, inputs, out fact))
                                return false;
                            return field.Wire.Evaluate(fact, inputs);
                        }));
            }
            return fields;
        }

        /// <summary>
        /// 编译成一行摘要。`budgetChars` 之外的字段会被丢掉（从后往前），
        /// 且**人工那份的末尾会标 `+N`** 表示丢了几项。
        ///
        /// `budgetChars &lt;= 0` 时用规格自己的预算（wire → `WireBudget`，否则 `RichBudget`）。
        /// </summary>
        public static string Compile(StateInputs inputs, int budgetChars = 0, bool wire = false)
        {
            if (inputs == null)
                return string.Empty;

            DigestSpec spec = Active;
            int budget = budgetChars > 0
                ? budgetChars
                : (wire ? spec.WireBudget : spec.RichBudget);

            var kept = new List<string>();
            int dropped = 0;
            bool full = false;

            for (int i = 0; i < spec.Fields.Count; i++)
            {
                DigestFieldSpec field = spec.Fields[i];
                string value;
                // 取值与判定都在 try 里：字段表是数据，写坏一条不许把整条摘要带走。
                try
                {
                    value = FormatField(field, inputs, wire);
                }
                catch (Exception)
                {
                    value = null;
                }
                if (value == null)
                    continue;

                // **一旦装不下，后面的一律丢** —— 字段表是"顺序即优先级"，
                // 所以不能"跳过这一项、再试试后面更短的那项"：那等于把优先级反过来用
                // （实测形态：`hp` 因为长被丢掉，而后面的 `cansleep` 因为短被留下）。
                if (full)
                {
                    dropped++;
                    continue;
                }

                string pair = field.Key + "=" + value;
                // 分隔符**每个字段各一个**（不是总共一个）：旧式子只加 1，于是实际长度比算出来的
                // 多 `kept.Count - 1` —— 预算被悄悄突破，而这块预算现在是**实测出来的硬边界**。
                int projected = LengthOf(kept) + kept.Count + pair.Length;
                if (projected > budget)
                {
                    // 预算连"第一个字段"都装不下时**截断它**，而不是"至少留一项"了事。
                    // 为什么：上线状态的预算是**实测出来的硬边界**（>35 字符模型就给默认答案），
                    // 破一次等于预算白守；而已知会超长的只有 `aim=<类型名>` 这一类，截断它比整条超限划算。
                    if (kept.Count == 0)
                    {
                        kept.Add(budget > 4 ? pair.Substring(0, budget - 2) + ".." : pair);
                        full = true;
                        continue;
                    }
                    full = true;
                    dropped++;
                    continue;
                }
                kept.Add(pair);
            }

            var text = new StringBuilder();
            for (int i = 0; i < kept.Count; i++)
            {
                if (i > 0)
                    text.Append(' ');
                text.Append(kept[i]);
            }
            // **`+N` 只在给人看的那份里出现，绝不发上线**（A55）。
            //
            // 实测量到：同一个状态 `sleep=exhausted aim=none` → `sleep`，尾随 `+7`/`+1`/`+0`
            // → **`mine`** —— 任意一个尾随 token 都能把答案翻面，而这个标记的值恰好随状态变化，
            // 等于往提示词里注入一个与语义无关、却随状态抖动的变量，还挤占最宝贵的开头位置。
            if (!wire && dropped > 0)
                text.Append(" +").Append(dropped.ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }

        /// <summary>一条字段：取事实 → rich 条件 → wire 条件 → 格式化。任一不过就是 null（不发）。</summary>
        private static string FormatField(DigestFieldSpec field, StateInputs inputs, bool wire)
        {
            DigestFactValue fact;
            if (!DigestFacts.TryRead(field.Source, inputs, out fact) || fact == null || !fact.HasValue)
                return null;
            if (field.Rich != null && !field.Rich.Evaluate(fact, inputs))
                return null;
            if (wire && field.Wire != null && !field.Wire.Evaluate(fact, inputs))
                return null;
            return DigestFacts.Apply(field, fact);
        }

        /// <summary>
        /// 把摘要字段**逐项取出来**（键 → 值，**不做预算裁剪**）—— 供树内确定性分支用：
        /// `Service.ObserveState` 把它们写进黑板（`state.phase` / `state.ui` / `state.hp`…），
        /// 于是树里能直接按同一批字段分流，而**不必问模型**。
        ///
        /// 与 <see cref="Compile"/> **共用同一份规格**：摘要里有的字段，树里就查得到；反之亦然。
        /// </summary>
        public static List<KeyValuePair<string, string>> Evaluate(StateInputs inputs)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            if (inputs == null)
                return pairs;

            DigestSpec spec = Active;
            for (int i = 0; i < spec.Fields.Count; i++)
            {
                DigestFieldSpec field = spec.Fields[i];
                string value;
                try
                {
                    value = FormatField(field, inputs, false);
                }
                catch (Exception)
                {
                    value = null;
                }
                if (value == null)
                    continue;

                pairs.Add(new KeyValuePair<string, string>(field.Key, value));
            }
            return pairs;
        }

        private static int LengthOf(List<string> pairs)
        {
            int total = 0;
            for (int i = 0; i < pairs.Count; i++)
                total += pairs[i].Length;
            return total;
        }

        /// <summary>类型名瘦身：`FullInventoryWidget` → `FullInventory`（省 token，且更好判）。</summary>
        internal static string Short(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;
            string trimmed = typeName.Trim();
            if (trimmed.EndsWith("Widget", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 6);
            else if (trimmed.EndsWith("Screen", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 6);
            else if (trimmed.EndsWith("Block", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 5);
            return trimmed.Length == 0 ? typeName : trimmed;
        }
    }
}

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
                CultureInfo.InvariantCulture, out parsed) ? parsed : (float?)null;
        }

        private static int? Int(Dictionary<string, object> source, string key)
        {
            float? value = Float(source, key);
            return value.HasValue ? (int?)Math.Round(value.Value) : null;
        }
    }

    /// <summary>
    /// 摘要字段的规格：**顺序、键名、以及"这个字段要不要发"都在这里**。
    ///
    /// 为什么集中成表：摘要格式是长期要调参的东西（§5.4 实测"摘要长度会改变决策"），
    /// 所以要能一眼看出"发出去的是哪几项、按什么顺序"，而不是散在编译逻辑里。
    /// </summary>
    public sealed class DigestField
    {
        public DigestField(string key, Func<StateInputs, string> format,
            Func<StateInputs, bool> include = null, string description = null,
            Func<StateInputs, bool> wireInclude = null)
        {
            Key = key;
            Format = format;
            Include = include;
            Description = description;
            WireInclude = wireInclude;
        }

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
        /// **为什么不直接写进 `Include`**：`Include` 是**共用**的 ——
        /// `Service.ObserveState` 的黑板键（`state.hp`…）也走这张表，
        /// 用 `Include` 会让"健康时 `state.hp` 这个键直接消失"，
        /// 树里按 `state.hp` 分流的条件就会静默失效。所以这一条**只作用于 Compile 的上线模式**。
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
    /// </summary>
    public static class StateDigestCompiler
    {
        /// <summary>
        /// **摘要布局版本**（plan G18）：字段表/档位规则**改了就要 +1**。
        ///
        /// 为什么要有它：摘要格式是长期调参的东西，而"改了字段之后再回头看复盘表"时，
        /// 表里那些样本是**改之前**的判准产生的 —— 版本号让这件事看得出来
        /// （`ai.laya.review` 的明细里带版本，`ai.laya.status` 会提示"样本不是当前版本"）。
        /// 它同时进请求指纹：版本变了，去重缓存里的旧答案**不会**被当成同一题面复用。
        /// </summary>
        public static int Version = 1;

        /// <summary>摘要字符预算（超出时按 <see cref="Trim"/> 从低优先级字段开始丢）。</summary>
        public const int DefaultBudgetChars = 200;

        /// <summary>数值档位标签（同时给自检与编辑器显示用）。</summary>
        public static string HealthBand(float value)
        {
            if (value <= 0.001f) return "dead";
            if (value < 0.2f) return "crit";
            if (value < 0.5f) return "low";
            if (value < 0.8f) return "ok";
            return "full";
        }

        public static string FoodBand(float value)
        {
            if (value < 0.1f) return "starving";
            if (value < 0.3f) return "hungry";
            if (value < 0.6f) return "ok";
            return "full";
        }

        public static string StaminaBand(float value)
        {
            if (value < 0.2f) return "spent";
            if (value < 0.5f) return "tired";
            return "ok";
        }

        public static string SleepBand(float value)
        {
            if (value < 0.2f) return "rested";
            if (value < 0.6f) return "sleepy";
            return "exhausted";
        }

        /// <summary>体温档位（游戏里 12 左右是舒适区；越低越冷）。</summary>
        public static string TemperatureBand(float value)
        {
            if (value < 6f) return "freezing";
            if (value < 10f) return "cold";
            if (value <= 16f) return "normal";
            if (value <= 20f) return "hot";
            return "burning";
        }

        public static string WetnessBand(float value)
        {
            if (value < 0.2f) return "dry";
            if (value < 0.6f) return "damp";
            return "soaked";
        }

        private static string Num(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Int2(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 字段表：**按重要性排序**（越靠前越先被保留）。
        /// 顺序即优先级，所以"预算不够时先丢什么"是可预期的。
        /// </summary>
        public static List<DigestField> Fields()
        {
            var fields = new List<DigestField>();

            // ============================ 顺序 = 实测结论，不是审美 ============================
            //
            // 2026-09-26 实测（plan §9「P2 实测 #1」那两张表）：**这个模型只看摘要的"开头"**。
            // 同一个事实（角色在挨饿）：
            //     `food=0.0(starving)`               18 字符 → eat   ✅
            //     `food=0.0(starving) hp=1(full)`    29 字符 → eat   ✅
            //     `hp=1(full) food=0.0(starving)`    29 字符 → fight ❌（同内容、只换了先后）
            //     前面再塞 `ui=hud scr=Game`          45 字符 → mine  ❌
            //     出厂那条完整摘要                    97~120 字符 → mine ❌（正好是第一个选项）
            // ⇒ 于是字段表**按"决策相关性"排**：先说"眼前有什么/我缺什么"，再说"我在哪个界面"。
            //    世界外这些角色字段全是 null 自动跳过，`scr`/`list` 自然成为开头 —— 两边都对。
            // ==============================================================================

            // ============================ 顺序 = 实的紧迫程度 ============================
            // 第二层实测（第 36 轮收尾时看出来的）：短状态只放得下一个事实，
            // 而"放哪个"必须按**紧迫程度**，不能按字段类型 ——
            // 实测那次发上去的是 `aim=Grass@6.58`，而那个角色**已经死了、在挨饿、还在挨冻**。
            // 所以：① 良性值**不发**（`hp=1(full)`/`night=0`/`temp=normal` …，见 `WireInclude`），
            //       ② 顺序按"先救命、再干活"：hp → food → sleep/night → aim → …
            // 于是开头**必然**是值得注意的那个事实（健康时开口就是 `aim=`，饿了开口就是 `food=`）。
            // ==========================================================================

            fields.Add(new DigestField("hp", s => s.Health.HasValue
                ? Num(s.Health.Value) + "(" + HealthBand(s.Health.Value) + ")"
                : null, null, "生命 + 档位",
                // 满血不发：它不改变任何决策，却会占掉开头那个唯一的位置。
                s => s.Health.HasValue && s.Health.Value < 0.8f));

            fields.Add(new DigestField("food", s => s.Food.HasValue
                ? Num(s.Food.Value) + "(" + FoodBand(s.Food.Value) + ")"
                : null, null, "饥饿 + 档位",
                // 只在"饿/很饿"才发（`ok` 也算够吃，不值得占开头）。
                s => s.Food.HasValue && s.Food.Value < 0.6f));

            fields.Add(new DigestField("sleep", s => s.Sleeping == true
                ? "now"
                : (s.Sleep.HasValue ? SleepBand(s.Sleep.Value) : null), null, "困倦（睡着时=now）",
                // 只有"困到该睡"（exhausted）或已经在睡才发；`sleepy` 不占位置。
                s => s.Sleeping == true || (s.Sleep.HasValue && s.Sleep.Value >= 0.6f)));

            fields.Add(new DigestField("night", s => s.IsNight.HasValue
                ? (s.IsNight.Value ? "1" : "0") : null, null, "是否夜晚",
                s => s.IsNight == true));

            // 「眼前是什么」—— 挖/拾/打的直接依据。**排在救命的事后面**：
            // 死了/饿着/困死的时候，"前面有块石头"不该是模型看到的第一件事。
            fields.Add(new DigestField("aim", s =>
            {
                // 世界外没有"准星"这回事：返回 null 让它**根本不出现**。
                // 返回 "none" 会白占短预算的开头（实测 `aim=none` 排第一时，
                // 世界外的 `scr`/`list` 会被挤出去，而世界外恰恰只靠它们决策）。
                if (string.IsNullOrEmpty(s.AimKind))
                    return s.HasPlayer ? "none" : null;
                if (string.Equals(s.AimKind, "block", StringComparison.OrdinalIgnoreCase))
                {
                    string block = Short(s.AimBlockType) ?? "block";
                    return s.AimDistance.HasValue ? block + "@" + Num(s.AimDistance.Value) : block;
                }
                return Short(s.AimKind);
            }, null, "准星目标（方块类型@距离）",
                // **上线状态里"没瞄任何东西"不发**（A56）。
                // 实测（2026-09-26）：只给 `aim=none` 这种"什么都没有"的状态时，
                // 模型会答 `danger` / `mine` / `fight` —— 全是**无中生有的警报**；
                // 而"完全良性"的角色上线状态里剩下的正是它，于是最常见的"没事干"场景
                // 得到的是最危险的动作。人看的那份照旧保留 `aim=none`（它是有用的信息）。
                // 注意引擎在"什么都没瞄"时报的是**字面量 `"none"`**（不是空串）——
                // 实测踩到：只判 `IsNullOrEmpty` 时 `aim=none` 照样被发上去，
                // 于是"没事干"的状态仍然带着那个会引发假警报的 token。
                s => !string.IsNullOrEmpty(s.AimKind)
                    && !string.Equals(s.AimKind, "none", StringComparison.OrdinalIgnoreCase)));

            fields.Add(new DigestField("hold", s => Short(s.HoldingBlockType),
                null, "手持物品类型"));

            fields.Add(new DigestField("stam", s => s.Stamina.HasValue
                ? StaminaBand(s.Stamina.Value) : null, null, "耐力档位",
                s => s.Stamina.HasValue && s.Stamina.Value < 0.5f));

            fields.Add(new DigestField("temp", s => s.Temperature.HasValue
                ? TemperatureBand(s.Temperature.Value) : null, null, "体温档位",
                // `normal` 不发（舒适区不改变决策）。
                s => s.Temperature.HasValue && (s.Temperature.Value < 10f || s.Temperature.Value > 16f)));

            fields.Add(new DigestField("wet", s => s.Wetness.HasValue
                ? WetnessBand(s.Wetness.Value) : null, null, "潮湿档位",
                s => s.Wetness.HasValue && s.Wetness.Value >= 0.2f));

            // ---- 世界外那套（角色字段全 null，于是这两项成为开头）
            fields.Add(new DigestField("scr", s => Short(s.Screen),
                s => !string.IsNullOrEmpty(s.Screen), "屏幕名",
                // 世界里这是噪声（26 字符正好占满 28 的预算，而问题是"该做什么"不是"在哪个界面"）
                s => !s.HasPlayer));

            // G16：列表候选行紧跟在屏幕名之后 —— 世界外"选哪个世界"的答案就是"第几行"，
            // 而列表行是自绘的（条目不是控件），不在这里发出去模型就只能瞎猜。
            fields.Add(new DigestField("list", s => UiListDigest.Compile(s.UiList),
                null, "可见列表的候选行（第几行 / 选中行 / 还能不能滚）",
                // **不进上线状态**（2026-09-26 实测，plan §12.4.1）：候选行发上去之后
                // 模型**照样常答 `open_ui`**（加 `row0..row3` 选项也没用）——
                // 既然没人能用它，它在 28~111 字符的上线状态里就是纯噪声（40+ 字符）。
                // 世界外那条链保持确定性：树里写死 `list:WorldsList#0`，翻页/滚动由 C# 做。
                s => false));

            // ---- 以下都属于"低相关"：留给宽预算（人工/复盘用的 rich 摘要），
            //      上线那条短状态基本轮不到它们（这正是想要的）。
            // 口径与运行时/交接协议共用同一个函数（`PhaseNames.Of`）：三处各判一次必然会在边界那两帧对不上。
            fields.Add(new DigestField("phase", s => PhaseNames.Of(s.WorldLoaded, s.HasPlayer),
                null, "世界内/世界外/加载中",
                s => !s.HasPlayer));

            fields.Add(new DigestField("ui", s =>
            {
                if (!s.WorldLoaded) return "menu";
                if (!string.IsNullOrEmpty(s.ModalPanel)) return Short(s.ModalPanel);
                if (s.DialogsOpen) return "dialog";
                return "hud";
            }, null, "界面状态（世界外=menu，模态=面板名）",
                s => !s.HasPlayer));

            fields.Add(new DigestField("day", s => s.Day.HasValue
                ? Int2(s.Day.Value) + (s.Hour.HasValue ? "/" + s.Hour.Value.ToString("0", CultureInfo.InvariantCulture) + "h" : string.Empty)
                : null, null, "第几天 / 几点"));

            fields.Add(new DigestField("season", s => Short(s.Season), null, "季节"));

            fields.Add(new DigestField("rain", s => s.Precipitation.HasValue && s.Precipitation.Value > 0.01f
                ? "1" : null, null, "是否降水（只在有降水时发）"));

            fields.Add(new DigestField("bag", s => s.InventoryItems.HasValue
                ? s.InventoryItems.Value.ToString(CultureInfo.InvariantCulture) : null,
                s => s.InventoryItems.HasValue && s.InventoryItems.Value > 0, "背包非空格数"));

            fields.Add(new DigestField("mode", s => Short(s.GameMode), null, "游戏模式"));

            fields.Add(new DigestField("cansleep", s => s.CanSleep == true ? "1" : null,
                null, "现在能不能睡（只在能睡时发）"));

            return fields;
        }

        /// <summary>
        /// 编译成一行摘要。`budgetChars` 之外的字段会被丢掉（从后往前），
        /// 且**末尾会标 `+N`** 表示丢了几项 —— 这样"摘要被裁"这件事在数据里看得见，
        /// 而不是让模型自己去猜为什么少了几项。
        /// </summary>
        public static string Compile(StateInputs inputs, int budgetChars = DefaultBudgetChars,
            bool wire = false)
        {
            if (inputs == null)
                return string.Empty;

            List<DigestField> fields = Fields();
            var kept = new List<string>();
            int dropped = 0;
            bool full = false;

            for (int i = 0; i < fields.Count; i++)
            {
                DigestField field = fields[i];
                string value = null;
                try
                {
                    value = field.Format(inputs);
                }
                catch (Exception)
                {
                    value = null;
                }
                if (value == null)
                    continue;

                bool include = field.Include == null || SafeInclude(field, inputs);
                if (!include)
                    continue;
                // 上线模式额外要求 `WireInclude`：良性值不发，于是开头必然是值得注意的事实。
                // **只在 wire 模式生效** —— 黑板（`Evaluate`）与人工/复盘摘要都要完整字段，
                // 否则"健康时 `state.hp` 这个键消失"会让树里按它分流的条件静默失效。
                if (wire && field.WireInclude != null && !SafeWireInclude(field, inputs))
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
                // 多 `kept.Count - 1` —— 预算被悄悄突破，而这块预算现在是**实测出来的硬边界**
                // （见 `Fields()` 顶部的实测），不能有这种漏算。
                int projected = LengthOf(kept) + kept.Count + pair.Length;
                if (projected > budgetChars)
                {
                    // 预算连"第一个字段"都装不下时**截断它**，而不是"至少留一项"了事。
                    // 为什么：上线状态的预算是**实测出来的硬边界**（>35 字符模型就给默认答案，
                    // 见 `Fields()` 顶部），破一次等于预算白守；
                    // 而已知会超长的只有 `aim=<类型名>` 这一类，截断它比整条超限划算得多。
                    if (kept.Count == 0)
                    {
                        kept.Add(budgetChars > 4 ? pair.Substring(0, budgetChars - 2) + ".." : pair);
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
            // 它当初的理由是"让模型知道自己没看到全部"，但实测（2026-09-26）：
            //   同一个状态 `sleep=exhausted aim=none`            → `sleep`
            //   同一个状态 + `+7` / `+1` / `+0` / `x7`           → **`mine`**
            //   同一个状态 + `7` / `+12`                          → `sleep`
            // 也就是说**任意一个尾随 token 都能把答案翻面**，而这个标记的值恰好
            // **随状态变化**（丢了几项 = 状态的函数）—— 等于往提示词里注入了一个
            // 与语义无关、却随状态抖动的变量。更要命的是它挤占的是那 28 字符里
            // 最宝贵的位置（实测：去掉它以后同一状态答 `sleep`，也就是**对的**那个）。
            // 人要看"被裁了几项"照样有 —— rich 摘要里保留。
            if (!wire && dropped > 0)
                text.Append(" +").Append(dropped.ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }

        /// <summary>
        /// 把摘要字段**逐项取出来**（键 → 值，**不做预算裁剪**）—— 供树内确定性分支用：
        /// `Service.ObserveState` 把它们写进黑板（`state.phase` / `state.ui` / `state.hp`…），
        /// 于是树里能直接按同一批字段分流，而**不必问模型**。
        ///
        /// 与 <see cref="Compile"/> **共用同一张字段表**：摘要里有的字段，树里就查得到；反之亦然。
        /// 两处各维护一份必然漂移（模型按 `phase` 判、树按另一个口径判 = 边界上自相矛盾）。
        /// 顺序与 <see cref="Fields"/> 一致（重要的在前），树/编辑器要展示时可以直接用。
        /// </summary>
        public static List<KeyValuePair<string, string>> Evaluate(StateInputs inputs)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            if (inputs == null)
                return pairs;

            List<DigestField> fields = Fields();
            for (int i = 0; i < fields.Count; i++)
            {
                DigestField field = fields[i];
                string value;
                try
                {
                    value = field.Format(inputs);
                }
                catch (Exception)
                {
                    value = null;
                }
                if (value == null)
                    continue;
                if (field.Include != null && !SafeInclude(field, inputs))
                    continue;

                pairs.Add(new KeyValuePair<string, string>(field.Key, value));
            }
            return pairs;
        }

        private static bool SafeWireInclude(DigestField field, StateInputs inputs)
        {
            try
            {
                return field.WireInclude(inputs);
            }
            catch (Exception)
            {
                return true;   // 判定本身出错时宁可不发（短状态的位置很贵）
            }
        }

        private static bool SafeInclude(DigestField field, StateInputs inputs)        {
            try
            {
                return field.Include(inputs);
            }
            catch (Exception)
            {
                return true;
            }
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

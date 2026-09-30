using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 一个档位（band）：数值落到这个区间就用它的标签。
    ///
    /// **顺序即优先级，第一个命中的赢**；一个只有 <see cref="Label"/>、没有任何边界的档位是
    /// **兜底档**（放最后）。这与引擎/项目里其它"档位"的写法一致（`HealthBand` 那种 if 链），
    /// 只是把 if 链搬进了数据 —— 改阈值不用重编译。
    /// </summary>
    public sealed class DigestBand
    {
        public float? Lt;
        public float? Lte;
        public float? Gt;
        public float? Gte;
        public string Label;

        public bool Matches(float value)
        {
            if (Lt.HasValue && !(value < Lt.Value)) return false;
            if (Lte.HasValue && !(value <= Lte.Value)) return false;
            if (Gt.HasValue && !(value > Gt.Value)) return false;
            if (Gte.HasValue && !(value >= Gte.Value)) return false;
            return true;
        }

        /// <summary>是不是"兜底档"（没有任何边界）。</summary>
        public bool IsFallback
        {
            get { return !Lt.HasValue && !Lte.HasValue && !Gt.HasValue && !Gte.HasValue; }
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (Lt.HasValue) parts.Add("<" + Num(Lt.Value));
            if (Lte.HasValue) parts.Add("<=" + Num(Lte.Value));
            if (Gt.HasValue) parts.Add(">" + Num(Gt.Value));
            if (Gte.HasValue) parts.Add(">=" + Num(Gte.Value));
            return (parts.Count == 0 ? "(fallback)" : string.Join(" & ", parts.ToArray()))
                + " -> " + (Label ?? "?");
        }

        internal static string Num(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public DigestBand Clone()
        {
            return new DigestBand { Lt = Lt, Lte = Lte, Gt = Gt, Gte = Gte, Label = Label };
        }
    }

    /// <summary>
    /// 一条**声明式**条件：这个字段"要不要发"（rich 一份、wire 一份）。
    ///
    /// 为什么不用表达式语言：摘要字段的取值永远是代码（事实），能被数据决定的只有
    /// "**发不发**"。所以这里只做布尔判定，操作数只有两类 ——
    ///   · `fact`：这个字段自己算出来的属性（`number` / `integer` / `text` / `kind` / `flag` / `hasValue`）；
    ///   · `obs`：原始观察里的角色/界面事实（`hasPlayer` / `worldLoaded` / `night` / `sleeping` …）。
    /// 覆盖面刚好等于原来硬编码的那些 `WireInclude` lambda（含 `temp` 的 `any[...]` 这种或关系）。
    /// </summary>
    public sealed class DigestCondition
    {
        public const string ModeAlways = "always";
        public const string ModeNever = "never";
        public const string ModeLeaf = "leaf";
        public const string ModeAll = "all";
        public const string ModeAny = "any";

        public string Mode = ModeAlways;

        /// <summary>`fact` 或 `obs`（仅 <see cref="ModeLeaf"/> 有效）。</summary>
        public string Scope;

        /// <summary>属性名（`fact` 侧见 <see cref="DigestFactValue"/>，`obs` 侧见 <see cref="DigestFacts.Observe"/>）。</summary>
        public string Name;

        /// <summary>比较算子：`lt lte gt gte eq ne notEmpty is in`。</summary>
        public string Op;

        public string Text;
        public float Number;
        public bool Flag;
        public List<string> Values;
        public List<DigestCondition> Children;

        public static DigestCondition Always()
        {
            return new DigestCondition { Mode = ModeAlways };
        }

        public static DigestCondition Never()
        {
            return new DigestCondition { Mode = ModeNever };
        }

        public bool Evaluate(DigestFactValue fact, StateInputs obs)
        {
            switch (Mode)
            {
                case ModeAlways:
                    return true;
                case ModeNever:
                    return false;
                case ModeAll:
                    for (int i = 0; i < (Children != null ? Children.Count : 0); i++)
                        if (!Children[i].Evaluate(fact, obs)) return false;
                    return true;
                case ModeAny:
                    for (int i = 0; i < (Children != null ? Children.Count : 0); i++)
                        if (Children[i].Evaluate(fact, obs)) return true;
                    return false;
            }

            if (string.Equals(Scope, "obs", StringComparison.OrdinalIgnoreCase))
            {
                float number;
                bool flag;
                string text;
                if (!DigestFacts.TryReadObserve(Name, obs, out number, out flag, out text))
                    return false;
                return Compare(text, number, flag, true);
            }

            if (fact == null)
                return false;
            switch ((Name ?? string.Empty).ToLowerInvariant())
            {
                case "hasvalue": return Compare(null, 0f, fact.HasValue, true);
                case "number": return fact.Number.HasValue && Compare(null, fact.Number.Value, false, true);
                case "integer": return fact.Integer.HasValue && Compare(null, fact.Integer.Value, false, true);
                case "text": return Compare(fact.Text, 0f, false, fact.Text != null);
                case "kind": return Compare(fact.Kind, 0f, false, fact.Kind != null);
                case "flag": return fact.Flag.HasValue && Compare(null, 0f, fact.Flag.Value, true);
                default: return false;
            }
        }

        private bool Compare(string text, float number, bool flag, bool hasValue)
        {
            string op = (Op ?? string.Empty).ToLowerInvariant();
            if (op == "notempty")
                return !string.IsNullOrEmpty(text);
            if (!hasValue)
                return false;

            switch (op)
            {
                case "lt": return number < Number;
                case "lte": return number <= Number;
                case "gt": return number > Number;
                case "gte": return number >= Number;
                case "eq": return text != null ? string.Equals(text, Text, StringComparison.Ordinal)
                                               : Math.Abs(number - Number) < 0.0000001f;
                case "ne": return text != null ? !string.Equals(text, Text, StringComparison.Ordinal)
                                               : Math.Abs(number - Number) >= 0.0000001f;
                case "is": return text != null ? string.Equals(text, Text, StringComparison.OrdinalIgnoreCase)
                                               : flag == Flag;
                case "in":
                    for (int i = 0; i < (Values != null ? Values.Count : 0); i++)
                        if (string.Equals(text, Values[i], StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
            }
            return false;
        }

        /// <summary>自检/编辑器用的一行说明（数据文件里写错时，报错要能指出是哪一条）。</summary>
        public string Describe()
        {
            switch (Mode)
            {
                case ModeAlways: return "always";
                case ModeNever: return "never";
                case ModeAll: return "all[" + Describe(Children) + "]";
                case ModeAny: return "any[" + Describe(Children) + "]";
            }
            string value = Values != null ? "in(" + string.Join("|", Values.ToArray()) + ")"
                : Text != null ? "'" + Text + "'"
                : Math.Abs(Number) > 0.0000001f ? DigestBand.Num(Number)
                : Flag ? "true" : "false";
            return Scope + "." + Name + " " + Op + " " + value;
        }

        private static string Describe(List<DigestCondition> list)
        {
            if (list == null)
                return string.Empty;
            var parts = new List<string>();
            for (int i = 0; i < list.Count; i++)
                parts.Add(list[i].Describe());
            return string.Join(", ", parts.ToArray());
        }

        public DigestCondition Clone()
        {
            var copy = new DigestCondition
            {
                Mode = Mode, Scope = Scope, Name = Name, Op = Op,
                Text = Text, Number = Number, Flag = Flag
            };
            if (Values != null)
                copy.Values = new List<string>(Values);
            if (Children != null)
            {
                copy.Children = new List<DigestCondition>();
                for (int i = 0; i < Children.Count; i++)
                    copy.Children.Add(Children[i].Clone());
            }
            return copy;
        }
    }

    /// <summary>
    /// 一条字段规格：**键名、取值来源（事实名）、格式、档位、进不进 rich/wire**。
    ///
    /// 这里是"数据 / 代码"的接缝：`Source` 是代码里注册的事实名（新增事实要编译），
    /// 其余全在数据里（顺序、格式、档位阈值、进哪一份）。
    /// </summary>
    public sealed class DigestFieldSpec
    {
        public string Key;

        /// <summary>代码侧的事实名，见 <see cref="DigestFacts.IsKnownSource"/>。</summary>
        public string Source;

        /// <summary>`text` / `band` / `num(band)` / `number` / `integer` / `bool01` / `bool1` / `dayhour`。</summary>
        public string Format = "text";

        public string Description;

        public List<DigestBand> Bands = new List<DigestBand>();

        /// <summary>人工/复盘与黑板那份的条件（null = 只要事实取得到就发）。</summary>
        public DigestCondition Rich;

        /// <summary>上线那份的**附加**条件（null = 与 rich 同）。`never` = 只给人看，绝不发上线。</summary>
        public DigestCondition Wire;

        public DigestFieldSpec Clone()
        {
            var copy = new DigestFieldSpec
            {
                Key = Key, Source = Source, Format = Format, Description = Description
            };
            for (int i = 0; i < (Bands != null ? Bands.Count : 0); i++)
                copy.Bands.Add(Bands[i].Clone());
            copy.Rich = Rich != null ? Rich.Clone() : null;
            copy.Wire = Wire != null ? Wire.Clone() : null;
            return copy;
        }
    }

    /// <summary>
    /// 摘要规格（`.digest.json`）：**字段表 + 两份预算**。
    ///
    /// 为什么它必须是数据：摘要格式是长期调参的东西（§5.4/§12 那一串实测都只改了"发什么"），
    /// 而它此前硬编码在 <c>StateDigestCompiler.Fields()</c> 里 —— 于是"让模型换一个关注点"
    /// 这种纯策略改动也要重编译整个 Mod。搬进数据之后：
    ///   · 顺序 / 进不进 wire / 档位阈值 / 预算 —— 改文件即可（带文件戳热重载）；
    ///   · 事实本身（`health` / `aim` / `screen`…）仍留在代码 —— 那是"新增能力"，
    ///     与 verb 词表同一个边界。
    /// </summary>
    public sealed class DigestSpec
    {
        public const string FormatName = "digest";
        public const int FormatVersion = 1;

        /// <summary>出厂规格 id（也是默认文件名 `world.digest.json` 的名字）。</summary>
        public const string DefaultId = "world";

        public string Format = FormatName;
        public int Version = 1;
        public string Id = DefaultId;
        public string Name;
        public string Description;

        /// <summary>上线那份的字符预算（缺省用这个；调用方显式给预算时以调用方为准）。</summary>
        public int WireBudget = 28;

        /// <summary>人工/复盘那份的字符预算。</summary>
        public int RichBudget = 200;

        public List<DigestFieldSpec> Fields = new List<DigestFieldSpec>();

        /// <summary>从哪个文件来的（内置默认 = null）。</summary>
        public string SourcePath;

        /// <summary>内容哈希（进请求指纹：换了规格 ⇒ 旧答案缓存不复用）。</summary>
        public string SourceHash;

        public bool IsBuiltin
        {
            get { return string.IsNullOrEmpty(SourcePath); }
        }

        public DigestFieldSpec Field(string key)
        {
            for (int i = 0; i < Fields.Count; i++)
                if (string.Equals(Fields[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return Fields[i];
            return null;
        }

        public DigestSpec Clone()
        {
            var copy = new DigestSpec
            {
                Format = Format, Version = Version, Id = Id, Name = Name,
                Description = Description, WireBudget = WireBudget, RichBudget = RichBudget,
                SourcePath = SourcePath, SourceHash = SourceHash
            };
            for (int i = 0; i < Fields.Count; i++)
                copy.Fields.Add(Fields[i].Clone());
            return copy;
        }

        // ------------------------------------------------------------------ 内置默认规格

        private static DigestSpec s_default;

        /// <summary>
        /// **内置默认规格** —— 就是 2026-09-26 之前那份硬编码字段表，逐项搬过来。
        ///
        /// 它的两个用途：① 实例里没有 `.digest.json` 时兜底（空实例照样能跑）；
        /// ② 数据文件解析失败时的**上一份好表**（绝不半生效）。
        ///
        /// ⚠️ 改这里等于改"出厂默认判准"，动了要连 <c>DigestSelfTest</c> 的等价性用例一起看。
        /// </summary>
        public static DigestSpec Default
        {
            get
            {
                if (s_default == null)
                    s_default = BuildDefault();
                return s_default;
            }
        }

        private static DigestCondition Fact(string name, string op, float value)
        {
            return new DigestCondition
            {
                Mode = DigestCondition.ModeLeaf, Scope = "fact", Name = name, Op = op, Number = value
            };
        }

        private static DigestCondition FactFlag(string name, bool value)
        {
            return new DigestCondition
            {
                Mode = DigestCondition.ModeLeaf, Scope = "fact", Name = name, Op = "is", Flag = value
            };
        }

        private static DigestCondition FactText(string name, string op, string value = null)
        {
            return new DigestCondition
            {
                Mode = DigestCondition.ModeLeaf, Scope = "fact", Name = name, Op = op, Text = value
            };
        }

        private static DigestCondition ObsFlag(string name, bool value)
        {
            return new DigestCondition
            {
                // 名字一律**小写规范形**（解析器也按小写归一）：写回时大小写一变，
                // 内容哈希就变，"改个大小写 = 换一份规格"会把指纹缓存全打掉。
                Mode = DigestCondition.ModeLeaf, Scope = "obs", Name = name.ToLowerInvariant(),
                Op = "is", Flag = value
            };
        }

        private static DigestCondition Any(params DigestCondition[] children)
        {
            var node = new DigestCondition { Mode = DigestCondition.ModeAny, Children = new List<DigestCondition>() };
            node.Children.AddRange(children);
            return node;
        }

        private static DigestCondition All(params DigestCondition[] children)
        {
            var node = new DigestCondition { Mode = DigestCondition.ModeAll, Children = new List<DigestCondition>() };
            node.Children.AddRange(children);
            return node;
        }

        private static DigestBand Band(float? lt, float? lte, float? gt, float? gte, string label)
        {
            return new DigestBand { Lt = lt, Lte = lte, Gt = gt, Gte = gte, Label = label };
        }

        private static DigestBand Below(float bound, string label)
        {
            return Band(bound, null, null, null, label);
        }

        private static DigestBand AtMost(float bound, string label)
        {
            return Band(null, bound, null, null, label);
        }

        private static DigestBand Fallback(string label)
        {
            return Band(null, null, null, null, label);
        }

        private static DigestFieldSpec Field_(string key, string source, string format,
            string description, DigestCondition wire = null, DigestCondition rich = null,
            params DigestBand[] bands)
        {
            var field = new DigestFieldSpec
            {
                Key = key, Source = source, Format = format, Description = description,
                Wire = wire, Rich = rich
            };
            if (bands != null)
                field.Bands.AddRange(bands);
            return field;
        }

        private static DigestSpec BuildDefault()
        {
            var spec = new DigestSpec
            {
                Format = FormatName,
                Version = 1,
                Id = DefaultId,
                Name = "world",
                Description = "出厂默认：顺序按紧迫程度（hp → food → sleep/night → aim → …），"
                    + "良性值不进上线，上线预算 28 字符。",
                WireBudget = 28,
                RichBudget = 200
            };

            // ============================ 顺序 = 实测结论，不是审美 ============================
            //
            // 2026-09-26 实测（plan §9「P2 实测 #1」）：**这个模型只看摘要的"开头"**。
            // 同一个事实（角色在挨饿）：`food=0.0(starving)` 18 字符 → eat；换成
            // `hp=1(full) food=0.0(starving)` 又答 fight —— 同内容、只换了先后。
            // ⇒ 字段**按"先救命、再干活"排**，且良性值不发，于是开头必然是值得注意的那件事。
            // ==============================================================================

            // 满血不发（不改变决策，却会占掉开头唯一的位置）。
            spec.Fields.Add(Field_("hp", "health", "num(band)", "生命 + 档位",
                Fact("number", "lt", 0.8f), null,
                AtMost(0.001f, "dead"), Below(0.2f, "crit"), Below(0.5f, "low"),
                Below(0.8f, "ok"), Fallback("full")));

            spec.Fields.Add(Field_("food", "food", "num(band)", "饥饿 + 档位",
                Fact("number", "lt", 0.6f), null,
                Below(0.1f, "starving"), Below(0.3f, "hungry"), Below(0.6f, "ok"),
                Fallback("full")));

            // **上线永不发**（2026-09-26，用户指令）。角色不睡 ⇒ 恒为 `sleep=exhausted`，
            // 而它排在 `aim` 前、长 15 字符，正好独占 28 字符的预算 ⇒ 模型连答 10+ 轮 `sleep`
            // （p=0.986），角色的全部动作被这一个 token 决定。
            // 现在这条纪律写在数据里：要恢复只需把 `"wire": "never"` 删掉。
            spec.Fields.Add(Field_("sleep", "sleep", "band", "困倦（睡着时=now）",
                DigestCondition.Never(), null,
                Below(0.2f, "rested"), Below(0.6f, "sleepy"), Fallback("exhausted")));

            spec.Fields.Add(Field_("night", "isNight", "bool01", "是否夜晚",
                FactFlag("flag", true), null));

            // 「眼前是什么」—— 挖/拾/打的直接依据，排在救命的事后面。
            // 上线里"没瞄任何东西"不发（A56）：只给 `aim=none` 时模型会答 danger/fight 这类假警报。
            spec.Fields.Add(Field_("aim", "aim", "text", "准星目标（方块类型@距离）",
                All(FactText("text", "notempty"), FactText("text", "ne", "none")), null));

            spec.Fields.Add(Field_("hold", "holding", "text", "手持物品类型"));
            spec.Fields.Add(Field_("stam", "stamina", "band", "耐力档位",
                Fact("number", "lt", 0.5f), null,
                Below(0.2f, "spent"), Below(0.5f, "tired"), Fallback("ok")));
            spec.Fields.Add(Field_("temp", "temperature", "band", "体温档位",
                Any(Fact("number", "lt", 10f), Fact("number", "gt", 16f)), null,
                Below(6f, "freezing"), Below(10f, "cold"), AtMost(16f, "normal"),
                AtMost(20f, "hot"), Fallback("burning")));
            spec.Fields.Add(Field_("wet", "wetness", "band", "潮湿档位",
                Fact("number", "gte", 0.2f), null,
                Below(0.2f, "dry"), Below(0.6f, "damp"), Fallback("soaked")));

            // ---- 世界外那套（角色字段全 null，于是这两项成为开头）
            // 世界里 `scr` 是噪声（26 字符正好占满 28），所以只在世界外进上线。
            spec.Fields.Add(Field_("scr", "screen", "text", "屏幕名", ObsFlag("hasPlayer", false)));
            // 候选行**不进上线**（§12.4.1 实测：模型不会"从状态里的列表挑第 N 项"，
            // 40+ 字符纯噪声）；只留在人工/复盘那份。
            spec.Fields.Add(Field_("list", "uiList", "text", "可见列表的候选行",
                DigestCondition.Never()));
            spec.Fields.Add(Field_("phase", "phase", "text", "世界内/世界外/加载中",
                ObsFlag("hasPlayer", false)));
            spec.Fields.Add(Field_("ui", "uiState", "text", "界面状态（世界外=menu，模态=面板名）",
                ObsFlag("hasPlayer", false)));
            spec.Fields.Add(Field_("day", "day", "dayhour", "第几天 / 几点"));
            spec.Fields.Add(Field_("season", "season", "text", "季节"));
            spec.Fields.Add(Field_("rain", "precipitation", "bool1", "是否降水（只在有降水时发）"));
            spec.Fields.Add(Field_("bag", "inventoryItems", "integer", "背包非空格数",
                null, Fact("integer", "gt", 0f)));
            spec.Fields.Add(Field_("mode", "gameMode", "text", "游戏模式"));
            spec.Fields.Add(Field_("cansleep", "canSleep", "bool1", "现在能不能睡（只在能睡时发）"));

            spec.SourceHash = DigestSpecJson.ComputeHash(spec);
            return spec;
        }

        /// <summary>内置档位标签（自检与编辑器显示用）——**与默认规格同一份数据**，不另抄一套阈值。</summary>
        public static string DefaultBandLabel(string fieldKey, float value)
        {
            DigestFieldSpec field = Default.Field(fieldKey);
            if (field == null)
                return null;
            return DigestBands.Label(field.Bands, value);
        }
    }

    /// <summary>档位查表：**第一个命中的赢**；没有兜底档且都不命中 → null（该字段就整条不发）。</summary>
    public static class DigestBands
    {
        public static string Label(List<DigestBand> bands, float value)
        {
            if (bands == null)
                return null;
            for (int i = 0; i < bands.Count; i++)
            {
                if (bands[i].Matches(value))
                    return bands[i].Label;
            }
            return null;
        }
    }

    /// <summary>
    /// `.digest.json` 的解析与写回。
    ///
    /// 写回不是装饰：**出厂种子文件就是从内置默认规格写出来的** —— 于是
    /// "文件版 ≡ 硬编码版"这件事由构造保证，而不是靠手抄两份 JSON 再祈祷它们一致。
    /// </summary>
    public static class DigestSpecJson
    {
        public static DigestSpec Parse(string json, string sourcePath, out string error)
        {
            error = null;
            var report = new PackageReport();
            PackageValue root;
            if (!PackageJson.TryParse(json ?? string.Empty, sourcePath ?? "digest", report, out root))
            {
                PackageIssue first = report.FirstError;
                error = "invalid JSON: " + (first != null ? first.Describe() : "parse failed");
                return null;
            }
            return Parse(root, sourcePath, out error);
        }

        public static DigestSpec Parse(PackageValue root, string sourcePath, out string error)
        {
            error = null;
            if (root == null || !root.IsObject)
            {
                error = "digest spec must be a JSON object";
                return null;
            }

            string format = root.Get("format").AsString(null);
            if (!string.IsNullOrEmpty(format)
                && !string.Equals(format, DigestSpec.FormatName, StringComparison.OrdinalIgnoreCase))
            {
                error = "unsupported format '" + format + "' (expected '" + DigestSpec.FormatName + "')";
                return null;
            }

            int version = root.Get("version").AsInt(DigestSpec.FormatVersion);
            if (version != DigestSpec.FormatVersion)
            {
                error = "unsupported digest format version " + version
                    + " (this field is the FORMAT version and only " + DigestSpec.FormatVersion
                    + " is accepted; put content revisions in the file body)";
                return null;
            }

            var spec = new DigestSpec
            {
                Format = DigestSpec.FormatName,
                Version = version,
                Id = root.Get("id").AsString(null),
                Name = root.Get("name").AsString(null),
                Description = root.Get("description").AsString(null),
                SourcePath = sourcePath
            };
            if (string.IsNullOrEmpty(spec.Id))
            {
                error = "digest spec needs an 'id'";
                return null;
            }

            PackageValue budgets = root.Get("budgets");
            if (budgets.IsObject)
            {
                spec.WireBudget = budgets.Get("wire").AsInt(spec.WireBudget);
                spec.RichBudget = budgets.Get("rich").AsInt(spec.RichBudget);
            }
            if (spec.WireBudget < 8 || spec.RichBudget < 8)
            {
                error = "digest budgets must be at least 8 characters (wire=" + spec.WireBudget
                    + ", rich=" + spec.RichBudget + ")";
                return null;
            }

            PackageValue fields = root.Get("fields");
            if (!fields.IsArray || fields.Count == 0)
            {
                error = "digest spec needs a non-empty 'fields' array";
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fields.Count; i++)
            {
                PackageValue item = fields.Item(i);
                if (!item.IsObject)
                {
                    error = "fields[" + i + "] must be an object";
                    return null;
                }

                string key = item.Get("key").AsString(null);
                if (string.IsNullOrEmpty(key))
                {
                    error = "fields[" + i + "] needs a 'key'";
                    return null;
                }
                if (!seen.Add(key))
                {
                    error = "duplicate digest field key '" + key + "'";
                    return null;
                }

                string source = item.Get("source").AsString(null);
                if (string.IsNullOrEmpty(source))
                {
                    error = "digest field '" + key + "' needs a 'source' (the fact name)";
                    return null;
                }
                if (!DigestFacts.IsKnownSource(source))
                {
                    error = "digest field '" + key + "' uses unknown source '" + source
                        + "' (known: " + string.Join(", ", DigestFacts.Sources().ToArray()) + ")";
                    return null;
                }

                var field = new DigestFieldSpec
                {
                    Key = key,
                    Source = source,
                    Format = (item.Get("format").AsString("text") ?? "text").Trim().ToLowerInvariant(),
                    Description = item.Get("description").AsString(null)
                };
                if (!DigestFacts.IsKnownFormat(field.Format))
                {
                    error = "digest field '" + key + "' uses unknown format '" + field.Format
                        + "' (known: " + string.Join(", ", DigestFacts.Formats().ToArray()) + ")";
                    return null;
                }

                PackageValue bands = item.Get("bands");
                if (bands.IsArray)
                {
                    for (int b = 0; b < bands.Count; b++)
                    {
                        PackageValue bandValue = bands.Item(b);
                        if (!bandValue.IsObject)
                        {
                            error = "digest field '" + key + "' bands[" + b + "] must be an object";
                            return null;
                        }
                        string label = bandValue.Get("label").AsString(null);
                        if (string.IsNullOrEmpty(label))
                        {
                            error = "digest field '" + key + "' bands[" + b + "] needs a 'label'";
                            return null;
                        }
                        var band = new DigestBand { Label = label };
                        if (!bandValue.Get("lt").IsNull) band.Lt = bandValue.Get("lt").AsFloat(0f);
                        if (!bandValue.Get("lte").IsNull) band.Lte = bandValue.Get("lte").AsFloat(0f);
                        if (!bandValue.Get("gt").IsNull) band.Gt = bandValue.Get("gt").AsFloat(0f);
                        if (!bandValue.Get("gte").IsNull) band.Gte = bandValue.Get("gte").AsFloat(0f);
                        field.Bands.Add(band);
                    }
                }

                string conditionError;
                if (!TryParseCondition(item.Get("rich"), "fields[" + i + "].rich", out DigestCondition rich,
                        out conditionError))
                {
                    error = "digest field '" + key + "': " + conditionError;
                    return null;
                }
                field.Rich = rich;
                if (!TryParseCondition(item.Get("wire"), "fields[" + i + "].wire", out DigestCondition wire,
                        out conditionError))
                {
                    error = "digest field '" + key + "': " + conditionError;
                    return null;
                }
                field.Wire = wire;

                spec.Fields.Add(field);
            }

            spec.SourceHash = ComputeHash(spec);
            return spec;
        }

        private static bool TryParseCondition(PackageValue value, string where,
            out DigestCondition condition, out string error)
        {
            condition = null;
            error = null;
            if (value == null || value.IsNull)
                return true;   // 没写 = 不额外限制

            if (value.IsString)
            {
                string text = (value.AsString(string.Empty) ?? string.Empty).Trim().ToLowerInvariant();
                if (text == DigestCondition.ModeAlways)
                {
                    condition = DigestCondition.Always();
                    return true;
                }
                if (text == DigestCondition.ModeNever)
                {
                    condition = DigestCondition.Never();
                    return true;
                }
                error = where + " string form must be 'always' or 'never' (got '" + text + "')";
                return false;
            }

            if (value.IsBool)
            {
                condition = value.AsBool(true) ? DigestCondition.Always() : DigestCondition.Never();
                return true;
            }

            if (!value.IsObject)
            {
                error = where + " must be a string, a boolean or an object";
                return false;
            }

            // all / any 组合
            PackageValue all = value.Get("all");
            PackageValue any = value.Get("any");
            if (all.IsArray || any.IsArray)
            {
                bool isAll = all.IsArray;
                PackageValue list = isAll ? all : any;
                if (list.Count == 0)
                {
                    error = where + " '" + (isAll ? "all" : "any") + "' must not be empty";
                    return false;
                }
                var node = new DigestCondition
                {
                    Mode = isAll ? DigestCondition.ModeAll : DigestCondition.ModeAny,
                    Children = new List<DigestCondition>()
                };
                for (int i = 0; i < list.Count; i++)
                {
                    DigestCondition child;
                    string childError;
                    if (!TryParseCondition(list.Item(i), where + "." + (isAll ? "all" : "any") + "[" + i + "]",
                            out child, out childError))
                    {
                        error = childError;
                        return false;
                    }
                    if (child == null)
                    {
                        error = where + " children must be conditions";
                        return false;
                    }
                    node.Children.Add(child);
                }
                condition = node;
                return true;
            }

            string scope = null;
            string name = null;
            if (!value.Get("fact").IsNull)
            {
                scope = "fact";
                name = value.Get("fact").AsString(null);
            }
            else if (!value.Get("obs").IsNull)
            {
                scope = "obs";
                name = value.Get("obs").AsString(null);
            }
            if (scope == null || string.IsNullOrEmpty(name))
            {
                error = where + " needs either 'fact' or 'obs' (plus 'op' and 'value')";
                return false;
            }

            string op = (value.Get("op").AsString(null) ?? string.Empty).Trim().ToLowerInvariant();
            if (!IsKnownOperator(op))
            {
                error = where + " uses unknown op '" + op + "' (lt lte gt gte eq ne is in notempty)";
                return false;
            }

            var leaf = new DigestCondition
            {
                Mode = DigestCondition.ModeLeaf, Scope = scope, Name = name.ToLowerInvariant(), Op = op
            };
            PackageValue literal = value.Get("value");
            if (op == "in")
            {
                if (!literal.IsArray || literal.Count == 0)
                {
                    error = where + " op 'in' needs a non-empty 'value' array";
                    return false;
                }
                leaf.Values = new List<string>();
                for (int i = 0; i < literal.Count; i++)
                {
                    string item = literal.Item(i).AsString(null);
                    if (item != null)
                        leaf.Values.Add(item);
                }
            }
            else if (op != "notempty")
            {
                if (literal == null || literal.IsNull)
                {
                    error = where + " op '" + op + "' needs a 'value'";
                    return false;
                }
                if (literal.IsBool)
                    leaf.Flag = literal.AsBool(false);
                else if (literal.IsNumber)
                    leaf.Number = literal.AsFloat(0f);
                else
                    leaf.Text = literal.AsString(null);
                // 数值算子必须拿到数值：`{"op":"lt","value":"x"}` 是写错了，明确拒掉
                if ((op == "lt" || op == "lte" || op == "gt" || op == "gte") && !literal.IsNumber)
                {
                    error = where + " op '" + op + "' needs a numeric 'value'";
                    return false;
                }
            }

            if (scope == "obs" && !DigestFacts.IsKnownObserve(name))
            {
                error = where + " uses unknown observation '" + name
                    + "' (known: " + string.Join(", ", DigestFacts.Observes().ToArray()) + ")";
                return false;
            }
            if (scope == "fact" && !DigestFacts.IsKnownFactAttribute(name))
            {
                error = where + " uses unknown fact attribute '" + name
                    + "' (known: " + string.Join(", ", DigestFacts.FactAttributes().ToArray()) + ")";
                return false;
            }

            condition = leaf;
            return true;
        }

        private static bool IsKnownOperator(string op)
        {
            switch (op)
            {
                case "lt": case "lte": case "gt": case "gte":
                case "eq": case "ne": case "is": case "in": case "notempty":
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ 写回

        /// <summary>把规格写成 `.digest.json`（出厂种子 + 编辑器导出 + 自检往返用）。</summary>
        public static string Write(DigestSpec spec)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"format\": \"").Append(spec.Format).Append("\",\r\n");
            sb.Append("  \"version\": ").Append(spec.Version).Append(",\r\n");
            sb.Append("  \"id\": \"").Append(Escape(spec.Id)).Append("\",\r\n");
            if (!string.IsNullOrEmpty(spec.Name))
                sb.Append("  \"name\": \"").Append(Escape(spec.Name)).Append("\",\r\n");
            if (!string.IsNullOrEmpty(spec.Description))
                sb.Append("  \"description\": \"").Append(Escape(spec.Description)).Append("\",\r\n");
            sb.Append("  \"budgets\": { \"wire\": ").Append(spec.WireBudget)
                .Append(", \"rich\": ").Append(spec.RichBudget).Append(" },\r\n");
            sb.Append("  \"fields\": [\r\n");
            for (int i = 0; i < spec.Fields.Count; i++)
            {
                DigestFieldSpec field = spec.Fields[i];
                sb.Append("    { \"key\": \"").Append(Escape(field.Key))
                    .Append("\", \"source\": \"").Append(Escape(field.Source))
                    .Append("\", \"format\": \"").Append(Escape(field.Format)).Append('"');
                if (!string.IsNullOrEmpty(field.Description))
                    sb.Append(", \"description\": \"").Append(Escape(field.Description)).Append('"');
                if (field.Bands != null && field.Bands.Count > 0)
                {
                    sb.Append(", \"bands\": [");
                    for (int b = 0; b < field.Bands.Count; b++)
                    {
                        DigestBand band = field.Bands[b];
                        if (b > 0) sb.Append(", ");
                        sb.Append('{');
                        bool first = true;
                        first = AppendBound(sb, band.Lt, "lt", first);
                        first = AppendBound(sb, band.Lte, "lte", first);
                        first = AppendBound(sb, band.Gt, "gt", first);
                        first = AppendBound(sb, band.Gte, "gte", first);
                        if (!first) sb.Append(", ");
                        sb.Append("\"label\": \"").Append(Escape(band.Label)).Append("\" }");
                    }
                    sb.Append(']');
                }
                if (field.Rich != null)
                    sb.Append(", \"rich\": ").Append(WriteCondition(field.Rich, 4));
                if (field.Wire != null)
                    sb.Append(", \"wire\": ").Append(WriteCondition(field.Wire, 4));
                sb.Append(" }");
                if (i < spec.Fields.Count - 1)
                    sb.Append(',');
                sb.Append("\r\n");
            }
            sb.Append("  ]\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        private static bool AppendBound(StringBuilder sb, float? value, string name, bool first)
        {
            if (!value.HasValue)
                return first;
            if (!first) sb.Append(", ");
            sb.Append('"').Append(name).Append("\": ").Append(DigestBand.Num(value.Value));
            return false;
        }

        private static string WriteCondition(DigestCondition condition, int indent)
        {
            switch (condition.Mode)
            {
                case DigestCondition.ModeNever: return "\"never\"";
                case DigestCondition.ModeAlways: return "\"always\"";
                case DigestCondition.ModeAll:
                case DigestCondition.ModeAny:
                {
                    string key = condition.Mode == DigestCondition.ModeAll ? "all" : "any";
                    var parts = new List<string>();
                    for (int i = 0; i < condition.Children.Count; i++)
                        parts.Add(WriteCondition(condition.Children[i], indent));
                    return "{ \"" + key + "\": [" + string.Join(", ", parts.ToArray()) + "] }";
                }
            }

            string value;
            if (condition.Op == "in")
            {
                var items = new List<string>();
                for (int i = 0; i < (condition.Values != null ? condition.Values.Count : 0); i++)
                    items.Add("\"" + Escape(condition.Values[i]) + "\"");
                value = "[" + string.Join(", ", items.ToArray()) + "]";
            }
            else if (condition.Op == "notempty")
            {
                value = null;
            }
            else if (condition.Text != null)
                value = "\"" + Escape(condition.Text) + "\"";
            else if (condition.Flag)
                value = "true";
            else if (condition.Number != 0f || condition.Op == "lt" || condition.Op == "lte"
                || condition.Op == "gt" || condition.Op == "gte")
                value = DigestBand.Num(condition.Number);
            else
                value = "false";

            // 属性名/算子名一律按**小写规范形**写回：解析器已经归一成小写，
            // 写回时保留原大小写会让 `Write(Parse(x)) != Write(x)`（哈希随之漂移）。
            string body = "\"" + condition.Scope + "\": \""
                + Escape((condition.Name ?? string.Empty).ToLowerInvariant())
                + "\", \"op\": \"" + (condition.Op ?? string.Empty).ToLowerInvariant() + "\"";
            if (value != null)
                body += ", \"value\": " + value;
            return "{ " + body + " }";
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 内容哈希：**规格的规范文本**的 SHA-256（取前 16 位十六进制）。
        /// 它进请求指纹，所以"改了档位阈值"也必然换指纹 —— 旧答案不会被当成本次判定。
        /// </summary>
        public static string ComputeHash(DigestSpec spec)
        {
            string text = Normalize(Write(spec));
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>行尾归一：同一份规格在 CRLF/LF 两种检出下必须算出同一个哈希。</summary>
        private static string Normalize(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }
}

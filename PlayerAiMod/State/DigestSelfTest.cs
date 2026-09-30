using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 摘要规格自检（2026-09-26）：把"字段表从硬编码搬进数据"这件事钉住。
    ///
    /// 四组断言，按重要性排序：
    ///   ① **等价性**：内置默认规格编译出来的 wire/rich 摘要与搬家前**逐字符相同**
    ///      （下面那些 golden 串是从旧实现推导的，其中两条还被实机日志复核过 —— 见注释）；
    ///   ② **数据能改行为**：改档位/进出/预算，摘要跟着变（这是本功能存在的理由）；
    ///   ③ **拒坏文件**：未知 source/格式/算子、重复 key、坏版本都要**明确报错**，
    ///      且运行时**保留上一份好规格**（绝不半生效）；
    ///   ④ **不污染全局**：热重载用例动过目录/生效规格后必须还原（A47 的教训）。
    /// </summary>
    public static class DigestSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "DigestSelfTest" };
            try { CaseDefaultStructure(result); } catch (Exception e) { result.Check("case:default structure", false, e.Message); }
            try { CaseGoldenDigests(result); } catch (Exception e) { result.Check("case:golden digests", false, e.Message); }
            try { CaseBands(result); } catch (Exception e) { result.Check("case:bands", false, e.Message); }
            try { CaseConditions(result); } catch (Exception e) { result.Check("case:conditions", false, e.Message); }
            try { CaseParseRejects(result); } catch (Exception e) { result.Check("case:parse rejects", false, e.Message); }
            try { CaseRoundTrip(result); } catch (Exception e) { result.Check("case:round trip", false, e.Message); }
            try { CaseDataChangesBehaviour(result); } catch (Exception e) { result.Check("case:data changes behaviour", false, e.Message); }
            try { CaseHotReload(result); } catch (Exception e) { result.Check("case:hot reload", false, e.Message); }
            try { CaseInstallTemplate(result); } catch (Exception e) { result.Check("case:template install", false, e.Message); }
            return result;
        }

        // ------------------------------------------------------------------ 输入

        /// <summary>世界里、瞄着方块、手持木头 —— 与实机 `state.digest` 取样同形。</summary>
        private static StateInputs WorldAim()
        {
            return new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Health = 1f, Food = 0.9f, Stamina = 1f, Sleep = 0.9f,
                Temperature = 12f, Wetness = 0f, IsNight = false,
                AimKind = "block", AimBlockType = "GravelBlock", AimDistance = 2.51f,
                HoldingBlockType = "OakWoodBlock",
                Day = 27, Hour = 11f, Season = "Autumn", GameMode = "Creative",
                Precipitation = 0f, InventoryItems = 0, CanSleep = false,
                Screen = "GameScreen"
            };
        }

        private static StateInputs Benign()
        {
            return new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Health = 1f, Food = 1f, Stamina = 1f, Sleep = 0.1f,
                Temperature = 12f, Wetness = 0.05f, IsNight = false,
                AimKind = null, HoldingBlockType = null, Screen = "GameScreen"
            };
        }

        private static StateInputs Front()
        {
            return new StateInputs { WorldLoaded = false, HasPlayer = false, Screen = "MainMenuScreen" };
        }

        // ------------------------------------------------------------------ ① 结构 + 等价性

        private static void CaseDefaultStructure(BtSelfTest.TestResult result)
        {
            DigestSpec spec = DigestSpec.Default;
            var keys = new List<string>();
            for (int i = 0; i < spec.Fields.Count; i++)
                keys.Add(spec.Fields[i].Key);
            string order = string.Join(",", keys.ToArray());
            result.Check("*** the built-in default keeps the documented field order ***",
                order == "hp,food,sleep,night,aim,hold,stam,temp,wet,scr,list,phase,ui,day,season,rain,bag,mode,cansleep",
                order);
            result.Check("the default budgets are 28 (wire) / 200 (rich)",
                spec.WireBudget == 28 && spec.RichBudget == 200,
                spec.WireBudget + "/" + spec.RichBudget);
            result.Check("*** sleep is wire-never in the data (the model obsessed over it) ***",
                spec.Field("sleep").Wire != null
                && spec.Field("sleep").Wire.Mode == DigestCondition.ModeNever);
            result.Check("list (candidate rows) is wire-never in the data too",
                spec.Field("list").Wire != null
                && spec.Field("list").Wire.Mode == DigestCondition.ModeNever);
            result.Check("the built-in spec carries a content hash (it goes into the fingerprint)",
                !string.IsNullOrEmpty(spec.SourceHash) && spec.SourceHash.Length == 16,
                spec.SourceHash);
            result.Check("every default field names a known fact source",
                AllSourcesKnown(spec), DescribeUnknownSources(spec));
        }

        private static bool AllSourcesKnown(DigestSpec spec)
        {
            for (int i = 0; i < spec.Fields.Count; i++)
                if (!DigestFacts.IsKnownSource(spec.Fields[i].Source))
                    return false;
            return true;
        }

        private static string DescribeUnknownSources(DigestSpec spec)
        {
            var bad = new List<string>();
            for (int i = 0; i < spec.Fields.Count; i++)
                if (!DigestFacts.IsKnownSource(spec.Fields[i].Source))
                    bad.Add(spec.Fields[i].Key + "->" + spec.Fields[i].Source);
            return string.Join(",", bad.ToArray());
        }

        /// <summary>
        /// **等价性锚点**。下面两条字符串是"搬家前那套硬编码字段表"的输出：
        ///   · rich 那条 173 字符 —— 2026-09-26 实机 `state.digest` 取样逐字符一致
        ///     （实机那次瞄的是 `Snow@1.44`，比这里的 `Gravel@2.51` 短 2 字符，正好 171）；
        ///   · wire 那条 28 字符 —— 同一次实测里 Laya 判定记录里的 `digest` 字段逐字符一致
        ///     （`sleep` 从 wire 摘掉之后的那一版）。
        /// 换规格实现时它们必须原样通过；不过就是"搬家搬坏了"。
        /// </summary>
        private static void CaseGoldenDigests(BtSelfTest.TestResult result)
        {
            string rich = StateDigestCompiler.Compile(WorldAim(), 200);
            string expectedRich = "hp=1(full) food=0.9(full) sleep=exhausted night=0 aim=Gravel@2.51"
                + " hold=OakWood stam=ok temp=normal wet=dry scr=Game phase=world ui=hud"
                + " day=27/11h season=Autumn mode=Creative";
            result.Check("*** golden: the rich digest of the sampled world state is unchanged ***",
                rich == expectedRich, rich + "  (len=" + rich.Length + ")");

            string wire = StateDigestCompiler.Compile(WorldAim(), 28, wire: true);
            result.Check("*** golden: the wire digest is aim+hold (and never sleep) ***",
                wire == "aim=Gravel@2.51 hold=OakWood", wire + "  (len=" + wire.Length + ")");
            result.Check("...which is exactly the wire budget (28)",
                wire.Length == 28, "len=" + wire.Length);

            // 世界外：确定性那条链（`scr` / `phase` / `ui` 三个字段撑起开头）
            string front = StateDigestCompiler.Compile(Front(), 28, wire: true);
            result.Check("front (wire): screen + phase survive the 28-char budget",
                front == "scr=MainMenu phase=front", front);
            result.Check("front (rich): the human digest still names the interface",
                StateDigestCompiler.Compile(Front(), 200) == "scr=MainMenu phase=front ui=menu",
                StateDigestCompiler.Compile(Front(), 200));

            // 良性角色 → 上线状态为空（A56 的前提：空状态不发问）
            result.Check("*** a fully benign character still compiles to an EMPTY wire state ***",
                StateDigestCompiler.Compile(Benign(), 28, wire: true).Trim().Length == 0,
                "'" + StateDigestCompiler.Compile(Benign(), 28, wire: true) + "'");
            result.Check("...while the human digest still describes it",
                StateDigestCompiler.Compile(Benign(), 200).Length > 0,
                StateDigestCompiler.Compile(Benign(), 200));

            // 饿着的时候开头必须是 `food=`（"只看开头"那条实测纪律）
            var hungry = WorldAim();
            hungry.Food = 0.05f;
            hungry.AimBlockType = "GrassBlock";
            hungry.AimDistance = 6.58f;
            string hungryWire = StateDigestCompiler.Compile(hungry, 28, wire: true);
            result.Check("*** a starving character's wire state leads with food ***",
                hungryWire == "food=0.05(starving)", hungryWire);
            result.Check("...and the human digest still shows what is in front of it",
                StateDigestCompiler.Compile(hungry, 200).IndexOf("aim=Grass@6.58", StringComparison.Ordinal) >= 0,
                StateDigestCompiler.Compile(hungry, 200));

            // `+N` 只在人工那份出现（A55）
            var richState = WorldAim();
            string tightRich = StateDigestCompiler.Compile(richState, 60);
            string tightWire = StateDigestCompiler.Compile(richState, 60, wire: true);
            result.Check("the human digest marks dropped fields with +N",
                tightRich.IndexOf(" +", StringComparison.Ordinal) >= 0, tightRich);
            result.Check("*** the wire state never carries the +N marker ***",
                tightWire.IndexOf(" +", StringComparison.Ordinal) < 0, tightWire);

            // 装不下的第一个字段要**截断**而不是整条超限
            var longAim = new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                AimKind = "block", AimBlockType = "VeryLongBlockName", AimDistance = 12.5f
            };
            string cut = StateDigestCompiler.Compile(longAim, 10, wire: true);
            result.Check("an oversized first field is truncated to the budget",
                cut == "aim=Very.." && cut.Length == 10, "'" + cut + "'");

            // 黑板（Evaluate）不做预算裁剪，且**良性键不许消失**
            List<KeyValuePair<string, string>> pairs = StateDigestCompiler.Evaluate(Benign());
            var keys = new List<string>();
            for (int i = 0; i < pairs.Count; i++)
                keys.Add(pairs[i].Key);
            result.Check("*** the blackboard keeps the benign keys (state.hp / state.night) ***",
                keys.Contains("hp") && keys.Contains("night") && keys.Contains("temp"),
                string.Join(",", keys.ToArray()));
        }

        // ------------------------------------------------------------------ 档位 / 条件

        private static void CaseBands(BtSelfTest.TestResult result)
        {
            result.Check("health bands",
                StateDigestCompiler.HealthBand(0f) == "dead"
                && StateDigestCompiler.HealthBand(0.1f) == "crit"
                && StateDigestCompiler.HealthBand(0.3f) == "low"
                && StateDigestCompiler.HealthBand(0.6f) == "ok"
                && StateDigestCompiler.HealthBand(1f) == "full");
            result.Check("food bands",
                StateDigestCompiler.FoodBand(0.05f) == "starving"
                && StateDigestCompiler.FoodBand(0.2f) == "hungry"
                && StateDigestCompiler.FoodBand(0.4f) == "ok"
                && StateDigestCompiler.FoodBand(0.9f) == "full");
            result.Check("temperature bands (12 是舒适区)",
                StateDigestCompiler.TemperatureBand(2f) == "freezing"
                && StateDigestCompiler.TemperatureBand(8f) == "cold"
                && StateDigestCompiler.TemperatureBand(12f) == "normal"
                && StateDigestCompiler.TemperatureBand(18f) == "hot"
                && StateDigestCompiler.TemperatureBand(30f) == "burning");
            result.Check("sleep / stamina / wetness bands",
                StateDigestCompiler.SleepBand(0.1f) == "rested"
                && StateDigestCompiler.SleepBand(0.4f) == "sleepy"
                && StateDigestCompiler.SleepBand(0.9f) == "exhausted"
                && StateDigestCompiler.StaminaBand(0.1f) == "spent"
                && StateDigestCompiler.StaminaBand(0.3f) == "tired"
                && StateDigestCompiler.StaminaBand(0.9f) == "ok"
                && StateDigestCompiler.WetnessBand(0.05f) == "dry"
                && StateDigestCompiler.WetnessBand(0.3f) == "damp"
                && StateDigestCompiler.WetnessBand(0.9f) == "soaked");

            // 第一条命中的赢；没有兜底档且都不命中 → null（该字段整条不发）
            var bands = new List<DigestBand>
            {
                new DigestBand { Lt = 0.5f, Label = "low" },
                new DigestBand { Lt = 1f, Label = "mid" }
            };
            result.Check("bands: first match wins",
                DigestBands.Label(bands, 0.1f) == "low" && DigestBands.Label(bands, 0.7f) == "mid");
            result.Check("*** bands: no fallback + no match -> null (the field is omitted) ***",
                DigestBands.Label(bands, 2f) == null);

            // 边界用 lte（`dead` 是 `<=0.001`，`normal` 是 `<=16`）
            result.Check("bands: lte bounds are inclusive at the edge",
                DigestBands.Label(DigestSpec.Default.Field("hp").Bands, 0.001f) == "dead"
                && DigestBands.Label(DigestSpec.Default.Field("temp").Bands, 16f) == "normal");

            // 睡着时 sleep 报 `now`（文本优先于档位）
            var sleeping = new StateInputs { WorldLoaded = true, HasPlayer = true, Sleeping = true, Sleep = 0.9f };
            result.Check("sleeping is reported as sleep=now",
                StateDigestCompiler.Compile(sleeping).IndexOf("sleep=now", StringComparison.Ordinal) >= 0,
                StateDigestCompiler.Compile(sleeping));
        }

        private static void CaseConditions(BtSelfTest.TestResult result)
        {
            var fact = new DigestFactValue { HasValue = true, Number = 0.4f, Integer = 2, Text = "Gravel", Kind = "block", Flag = false };
            var obs = new StateInputs { WorldLoaded = true, HasPlayer = true, IsNight = true };

            Func<string, string, float, bool> num = delegate(string op, string name, float value)
            {
                return Leaf("fact", name, op, value, null, false).Evaluate(fact, obs);
            };
            result.Check("leaf: numeric comparisons",
                num("lt", "number", 0.5f) && !num("lt", "number", 0.4f)
                && num("lte", "number", 0.4f) && num("gt", "number", 0.3f) && num("gte", "number", 0.4f));

            result.Check("leaf: text comparisons",
                Leaf("fact", "text", "eq", 0f, "Gravel", false).Evaluate(fact, obs)
                && Leaf("fact", "text", "ne", 0f, "Stone", false).Evaluate(fact, obs)
                && Leaf("fact", "text", "notempty", 0f, null, false).Evaluate(fact, obs)
                && !Leaf("fact", "text", "eq", 0f, "Stone", false).Evaluate(fact, obs));

            result.Check("leaf: flag / in",
                Leaf("fact", "flag", "is", 0f, null, false).Evaluate(fact, obs)
                && Leaf("fact", "kind", "in", 0f, null, false, new[] { "block", "entity" }).Evaluate(fact, obs)
                && !Leaf("fact", "kind", "in", 0f, null, false, new[] { "entity" }).Evaluate(fact, obs));

            result.Check("obs conditions read the raw observation",
                Leaf("obs", "hasplayer", "is", 0f, null, true).Evaluate(fact, obs)
                && Leaf("obs", "worldloaded", "is", 0f, null, true).Evaluate(fact, obs)
                && Leaf("obs", "night", "is", 0f, null, true).Evaluate(fact, obs));

            var all = new DigestCondition { Mode = DigestCondition.ModeAll, Children = new List<DigestCondition> { DigestCondition.Always(), DigestCondition.Always() } };
            var allBad = new DigestCondition { Mode = DigestCondition.ModeAll, Children = new List<DigestCondition> { DigestCondition.Always(), DigestCondition.Never() } };
            var any = new DigestCondition { Mode = DigestCondition.ModeAny, Children = new List<DigestCondition> { DigestCondition.Never(), DigestCondition.Always() } };
            var anyBad = new DigestCondition { Mode = DigestCondition.ModeAny, Children = new List<DigestCondition> { DigestCondition.Never(), DigestCondition.Never() } };
            result.Check("combinators: all / any",
                all.Evaluate(fact, obs) && !allBad.Evaluate(fact, obs)
                && any.Evaluate(fact, obs) && !anyBad.Evaluate(fact, obs));

            // 缺事实 → 条件一律不成立（绝不用 0/空串糊过去）
            result.Check("a missing fact never satisfies a condition",
                !Leaf("fact", "number", "lt", 1f, null, false).Evaluate(null, obs)
                && !Leaf("fact", "hasvalue", "is", 0f, null, false).Evaluate(null, obs));
        }

        private static DigestCondition Leaf(string scope, string name, string op, float number,
            string text, bool flag, string[] values = null)
        {
            return new DigestCondition
            {
                Mode = DigestCondition.ModeLeaf, Scope = scope, Name = name, Op = op,
                Number = number, Text = text, Flag = flag,
                Values = values != null ? new List<string>(values) : null
            };
        }

        // ------------------------------------------------------------------ ③ 拒坏文件

        private static void CaseParseRejects(BtSelfTest.TestResult result)
        {
            Func<string, string> parse = delegate(string json)
            {
                string error;
                DigestSpec spec = DigestSpecJson.Parse(json, "test.digest.json", out error);
                return spec == null ? (error ?? "<no error>") : null;
            };

            string bad = parse("{ not json ");
            result.Check("invalid JSON is rejected", bad != null && bad.IndexOf("invalid JSON", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":[]}");
            result.Check("an empty field list is rejected", bad != null && bad.IndexOf("non-empty 'fields'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"qbank\",\"version\":1,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"health\"}]}");
            result.Check("a foreign format is rejected", bad != null && bad.IndexOf("unsupported format", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":9,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"health\"}]}");
            result.Check("a future format version is rejected (with the accepted value in the message)",
                bad != null && bad.IndexOf("unsupported digest format version 9", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"nope\"}]}");
            result.Check("*** an unknown fact source is rejected (with the known list) ***",
                bad != null && bad.IndexOf("unknown source 'nope'", StringComparison.Ordinal) >= 0
                && bad.IndexOf("health", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"health\",\"format\":\"sparkles\"}]}");
            result.Check("an unknown format is rejected",
                bad != null && bad.IndexOf("unknown format 'sparkles'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"health\"},{\"key\":\"a\",\"source\":\"food\"}]}");
            result.Check("a duplicate field key is rejected", bad != null && bad.IndexOf("duplicate digest field key", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"health\","
                + "\"wire\":{\"fact\":\"number\",\"op\":\"starts\",\"value\":1}}]}");
            result.Check("an unknown operator is rejected", bad != null && bad.IndexOf("unknown op 'starts'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"health\","
                + "\"wire\":{\"fact\":\"number\",\"op\":\"lt\",\"value\":\"soon\"}}]}");
            result.Check("a numeric operator with a text value is rejected",
                bad != null && bad.IndexOf("needs a numeric 'value'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"health\","
                + "\"wire\":{\"obs\":\"mood\",\"op\":\"is\",\"value\":true}}]}");
            result.Check("an unknown observation is rejected",
                bad != null && bad.IndexOf("unknown observation 'mood'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":[{\"key\":\"a\",\"source\":\"health\","
                + "\"wire\":{\"fact\":\"text\",\"op\":\"in\",\"value\":[]}}]}");
            result.Check("'in' with an empty array is rejected",
                bad != null && bad.IndexOf("non-empty 'value' array", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"budgets\":{\"wire\":2},\"fields\":[{\"key\":\"a\",\"source\":\"health\"}]}");
            result.Check("an absurdly small budget is rejected",
                bad != null && bad.IndexOf("at least 8", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"fields\":[{\"key\":\"a\",\"source\":\"health\"}]}");
            result.Check("a spec without an id is rejected",
                bad != null && bad.IndexOf("needs an 'id'", StringComparison.Ordinal) >= 0, bad);

            // 正例：条件写成字符串简写也要认
            string error;
            DigestSpec ok = DigestSpecJson.Parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"fields\":["
                + "{\"key\":\"a\",\"source\":\"health\",\"wire\":\"never\"},"
                + "{\"key\":\"b\",\"source\":\"food\",\"wire\":\"always\"}]}",
                "ok.digest.json", out error);
            result.Check("the string shorthand (never / always) parses",
                ok != null && ok.Field("a").Wire.Mode == DigestCondition.ModeNever
                && ok.Field("b").Wire.Mode == DigestCondition.ModeAlways, error);
        }

        // ------------------------------------------------------------------ 往返 / 数据改行为

        private static void CaseRoundTrip(BtSelfTest.TestResult result)
        {
            string text = DigestSpecJson.Write(DigestSpec.Default);
            string error;
            DigestSpec parsed = DigestSpecJson.Parse(text, "roundtrip.digest.json", out error);
            result.Check("the generated seed file parses back", parsed != null, error);
            if (parsed == null)
                return;
            result.Check("*** writing the built-in spec and reading it back yields the same content hash ***",
                parsed.SourceHash == DigestSpec.Default.SourceHash,
                parsed.SourceHash + " vs " + DigestSpec.Default.SourceHash);
            result.Check("...and the same field order",
                DigestSpecJson.Write(parsed) == text);
            result.Check("...including the wire-never rules on sleep and list",
                parsed.Field("sleep").Wire.Mode == DigestCondition.ModeNever
                && parsed.Field("list").Wire.Mode == DigestCondition.ModeNever);
            result.Check("...and the bands (thresholds survive the round trip)",
                DigestBands.Label(parsed.Field("temp").Bands, 16f) == "normal"
                && DigestBands.Label(parsed.Field("temp").Bands, 30f) == "burning"
                && DigestBands.Label(parsed.Field("hp").Bands, 0.001f) == "dead");
        }

        /// <summary>本功能存在的理由：**改数据就改行为**（不用重编译）。</summary>
        private static void CaseDataChangesBehaviour(BtSelfTest.TestResult result)
        {
            DigestSpec saved = StateDigestCompiler.Active;
            try
            {
                // ① 关掉 `hold` 的上线资格 → wire 里不再有 hold
                DigestSpec noHold = DigestSpec.Default.Clone();
                noHold.SourceHash = "test-nohold";
                noHold.Field("hold").Wire = DigestCondition.Never();
                StateDigestCompiler.SetActive(noHold);
                string wire = StateDigestCompiler.Compile(WorldAim(), 28, wire: true);
                result.Check("*** wire: dropping a field from the data removes it from the digest ***",
                    wire.IndexOf("hold=", StringComparison.Ordinal) < 0, wire);

                // ② 把 hp 的档位标签改掉 → rich 里立刻能看到新标签
                DigestSpec renamed = DigestSpec.Default.Clone();
                renamed.SourceHash = "test-bands";
                renamed.Field("hp").Bands[4].Label = "pristine";
                StateDigestCompiler.SetActive(renamed);
                string rich = StateDigestCompiler.Compile(WorldAim(), 200);
                result.Check("*** data: renaming a band label changes the digest ***",
                    rich.IndexOf("hp=1(pristine)", StringComparison.Ordinal) >= 0, rich);

                // ③ 放宽上线预算 → 原来被丢掉的字段回来了
                DigestSpec roomy = DigestSpec.Default.Clone();
                roomy.SourceHash = "test-budget";
                StateDigestCompiler.SetActive(roomy);
                string tight = StateDigestCompiler.Compile(WorldAim(), 20, wire: true);
                string wide = StateDigestCompiler.Compile(WorldAim(), 60, wire: true);
                result.Check("*** data: a bigger wire budget keeps more fields ***",
                    wide.Length > tight.Length && wide.IndexOf("hold=", StringComparison.Ordinal) >= 0,
                    tight + "  ->  " + wide);
            }
            finally
            {
                StateDigestCompiler.SetActive(saved);
            }
            result.Check("the active spec is restored after the behaviour case",
                ReferenceEquals(StateDigestCompiler.Active, saved));
        }

        // ------------------------------------------------------------------ ④ 热重载（含全局还原）

        private static void CaseHotReload(BtSelfTest.TestResult result)
        {
            List<string> savedDirectories = new List<string>(DigestCatalog.Directories);
            DigestSpec savedSpec = StateDigestCompiler.Active;
            string savedPath = DigestCatalog.CurrentPath;
            string root = Path.Combine(Path.GetTempPath(),
                "pai-digest-selftest-" + Guid.NewGuid().ToString("n"));
            try
            {
                string directory = Path.Combine(root, DigestCatalog.PlayerAiFolderName, DigestCatalog.FolderName);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, DigestSpecTemplates.DefaultFileName);

                // 先写一份"改过一点"的规格：sleep 重新允许上线
                DigestSpec draft = DigestSpec.Default.Clone();
                draft.Field("sleep").Wire = null;
                File.WriteAllText(path, DigestSpecJson.Write(draft));

                DigestCatalog.ResetForTests();
                DigestCatalog.Configure(new[] { directory });
                DigestCatalog.EnsureFresh();

                DigestSpec loaded = StateDigestCompiler.Active;
                result.Check("*** hot reload: the spec file on disk becomes the active spec ***",
                    !loaded.IsBuiltin && string.Equals(loaded.SourcePath, path, StringComparison.OrdinalIgnoreCase),
                    loaded.SourcePath);
                result.Check("...and the wire digest follows it",
                    StateDigestCompiler.Compile(WorldAim(), 28, wire: true)
                        .StartsWith("sleep=", StringComparison.Ordinal),
                    StateDigestCompiler.Compile(WorldAim(), 28, wire: true));
                result.Check("changedOnDisk is false right after loading",
                    !DigestCatalog.ChangedOnDisk);

                // 改档位 → 戳变 → 下一次 EnsureFresh 必须看到新内容
                string before = loaded.SourceHash;
                string text = DigestSpecJson.Write(draft).Replace("\"full\"", "\"topped\"");
                File.WriteAllText(path, text);
                result.Check("changedOnDisk lights up before the next read",
                    DigestCatalog.ChangedOnDisk);
                DigestCatalog.EnsureFresh();
                DigestSpec reloaded = StateDigestCompiler.Active;
                result.Check("*** hot reload: editing the file changes the active spec (no restart) ***",
                    !string.Equals(reloaded.SourceHash, before, StringComparison.Ordinal)
                    && StateDigestCompiler.Compile(WorldAim(), 200)
                        .IndexOf("hp=1(topped)", StringComparison.Ordinal) >= 0,
                    reloaded.SourceHash + " vs " + before);

                // 写坏 → 拒用，**保留上一份好规格**
                string good = reloaded.SourceHash;
                File.WriteAllText(path, "{ \"format\": \"digest\", \"version\": 1, \"fields\": [] }");
                DigestCatalog.EnsureFresh();
                result.Check("*** a broken spec file is rejected and the previous spec keeps running ***",
                    StateDigestCompiler.Active.SourceHash == good
                    && !string.IsNullOrEmpty(DigestCatalog.LastError),
                    StateDigestCompiler.Active.SourceHash + " err=" + DigestCatalog.LastError);

                // 显式重载失败时，**不许把"正在跑的那份从哪来"弄丢**：
                // 否则 `CurrentPath` 变 null、`changedOnDisk` 恒 true —— 状态面板开始撒谎，
                // 而这恰好发生在"文件坏了、正需要看清现场"的时候。
                DigestSpec afterFailedReload;
                string reloadError;
                bool reloadOk = DigestCatalog.Reload(out afterFailedReload, out reloadError);
                result.Check("*** a failed explicit reload keeps the previous spec AND its path ***",
                    !reloadOk
                    && StateDigestCompiler.Active.SourceHash == good
                    && string.Equals(DigestCatalog.CurrentPath, path, StringComparison.OrdinalIgnoreCase),
                    "ok=" + reloadOk + " active=" + StateDigestCompiler.Active.SourceHash
                    + " path=" + DigestCatalog.CurrentPath + " err=" + reloadError);

                // 删掉文件 → 退回内置默认（不报错）
                File.Delete(path);
                DigestCatalog.EnsureFresh();
                result.Check("deleting the file falls back to the built-in default",
                    StateDigestCompiler.Active.IsBuiltin
                    && StateDigestCompiler.Active.SourceHash == DigestSpec.Default.SourceHash);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
                DigestCatalog.ResetForTests();
                DigestCatalog.Configure(savedDirectories);
                StateDigestCompiler.SetActive(savedSpec == DigestSpec.Default ? null : savedSpec);
            }
            result.Check("the digest catalog is restored after the self-test (global seam discipline)",
                ReferenceEquals(StateDigestCompiler.Active, savedSpec == DigestSpec.Default ? DigestSpec.Default : savedSpec)
                || StateDigestCompiler.Active.SourceHash == savedSpec.SourceHash,
                DigestCatalog.CurrentPath + " vs " + savedPath);
        }

        private static void CaseInstallTemplate(BtSelfTest.TestResult result)
        {
            string root = Path.Combine(Path.GetTempPath(),
                "pai-digest-install-" + Guid.NewGuid().ToString("n"));
            try
            {
                List<string> installed;
                string error;
                int written = DigestSpecTemplates.Install(root, out installed, out error);
                result.Check("install writes the seed on a fresh instance",
                    written == 1 && installed.Count == 1 && string.IsNullOrEmpty(error),
                    written + " " + error);

                string path = Path.Combine(root, DigestCatalog.PlayerAiFolderName,
                    DigestCatalog.FolderName, DigestSpecTemplates.DefaultFileName);
                result.Check("the seed lands in PlayerAi/Digest/world.digest.json",
                    File.Exists(path), path);

                // 已存在 → 绝不再写（人/LLM 改过的那份就是权威）
                File.WriteAllText(path, DigestSpecJson.Write(DigestSpec.Default).Replace("\"full\"", "\"kept\""));
                int again = DigestSpecTemplates.Install(root, out installed, out error);
                result.Check("*** install never overwrites an existing spec ***",
                    again == 0 && File.ReadAllText(path).IndexOf("\"kept\"", StringComparison.Ordinal) >= 0);

                // 生成的种子必须能解析、且等于内置默认
                DigestSpec parsed = DigestCatalog.Load(path, out error);
                result.Check("the seed file parses (the kept edit above parses too)", parsed != null, error);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }
    }
}

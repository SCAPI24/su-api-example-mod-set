using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 物种别名表自检（2026-09-26）：把"口语词 → 能匹配的生物名"这条判定钉住。
    ///
    /// 为什么值得单独一套：引擎的 `CreatureCategory` 只有 5 个值，**分不出牛和鹿、狼和熊虎**，
    /// 于是"打牛"能不能落到牛身上，全靠这张表 + 这里的匹配规则。规则错的表现不是报错，
    /// 而是"AI 跑去打了一只别的动物"——静默错，必须由自检钉住。
    ///
    /// 六组断言：
    ///   ① 内置表覆盖常见猎物（牛/狼），且两个名字体系都在（中文显示名 + 英文名）；
    ///   ② **扩展**：`牛` → 一组名字；`牛狼` → 两组都算上（"最近的那只"说了算）；
    ///   ③ **匹配规则**：中文子串双向命中；纯 ASCII 短词不许"含"匹配（`ow` 不能命中 `cow`）；
    ///   ④ 表里没有的词**原样保留**（交给"和显示名直接比"的兜底路径，不是丢弃）；
    ///   ⑤ 拒坏文件：坏 JSON / 外来格式 / 坏版本 / 缺 id / 缺 species / 空 names / 重复 key / 名字跨物种重复；
    ///   ⑥ 热重载与"绝不半生效"：改文件立刻生效；写坏了保留上一份；缺文件用内置默认。
    /// </summary>
    public static class ChatAnimalAliasesSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "ChatAnimalAliasesSelfTest" };
            try { CaseBuiltin(result); } catch (Exception e) { result.Check("case:builtin", false, e.Message); }
            try { CaseExpand(result); } catch (Exception e) { result.Check("case:expand", false, e.Message); }
            try { CaseMatching(result); } catch (Exception e) { result.Check("case:matching", false, e.Message); }
            try { CaseParseRejects(result); } catch (Exception e) { result.Check("case:parse rejects", false, e.Message); }
            try { CaseRoundTrip(result); } catch (Exception e) { result.Check("case:round trip", false, e.Message); }
            try { CaseHotReload(result); } catch (Exception e) { result.Check("case:hot reload", false, e.Message); }
            return result;
        }

        private static void CaseBuiltin(BtSelfTest.TestResult result)
        {
            ChatAnimalAliases table = ChatAnimalAliases.Default;

            result.Check("the built-in table has an id and species",
                table.Id == ChatAnimalAliases.DefaultId && table.Species.Count > 5,
                table.Species.Count.ToString());

            ChatAnimalSpec cow = table.Find("cow");
            result.Check("*** the built-in table knows 牛 (both Chinese and English names) ***",
                cow != null && cow.Names.Contains("牛") && cow.Names.Contains("cow"),
                cow == null ? "<no cow>" : string.Join("/", cow.Names.ToArray()));

            ChatAnimalSpec wolf = table.Find("wolf");
            result.Check("*** the built-in table knows 狼 ***",
                wolf != null && wolf.Names.Contains("狼") && wolf.Names.Contains("wolf"),
                wolf == null ? "<no wolf>" : string.Join("/", wolf.Names.ToArray()));

            result.Check("every species has at least one name and a key",
                AllHaveNames(table), "");

            result.Check("the built-in table is not marked as coming from disk",
                table.IsBuiltin && string.IsNullOrEmpty(table.SourcePath));
        }

        private static bool AllHaveNames(ChatAnimalAliases table)
        {
            for (int i = 0; i < table.Species.Count; i++)
            {
                if (string.IsNullOrEmpty(table.Species[i].Key) || table.Species[i].Names.Count == 0)
                    return false;
            }
            return true;
        }

        private static void CaseExpand(BtSelfTest.TestResult result)
        {
            ChatAnimalAliases table = ChatAnimalAliases.Default;

            List<string> cow = table.Expand("牛");
            result.Check("*** 牛 expands to the whole cow group (口语词 -> 显示名) ***",
                cow.Contains("牛") && cow.Contains("公牛") && cow.Contains("cow"),
                string.Join("/", cow.ToArray()));

            List<string> wolf = table.Expand("狼");
            result.Check("狼 expands to the wolf group",
                wolf.Contains("狼") && wolf.Contains("wolf"), string.Join("/", wolf.ToArray()));

            List<string> mixed = table.Expand("牛,狼");
            result.Check("*** 牛,狼 expands to both groups ***",
                mixed.Contains("牛") && mixed.Contains("狼") && mixed.Contains("cow"),
                string.Join("/", mixed.ToArray()));

            // 中文没有词边界："牛狼" 里同时含两种动物的名字
            List<string> glued = table.Expand("牛狼");
            result.Check("*** 牛狼 (no separator) still reaches both species ***",
                glued.Contains("牛") && glued.Contains("狼"),
                string.Join("/", glued.ToArray()));

            List<string> unknown = table.Expand("三叶虫");
            result.Check("*** a word the table does not know is kept as-is (fallback path) ***",
                unknown.Count == 1 && unknown[0] == "三叶虫",
                string.Join("/", unknown.ToArray()));

            result.Check("an empty argument expands to nothing (the tree must not guess)",
                table.Expand("").Count == 0 && table.Expand(null).Count == 0);

            // 类别：名字是子串匹配，`牛` 会命中 "Bull Shark" —— 必须靠类别把它挡住
            result.Check("*** 牛 is land-only (a shark must never answer a cow call) ***",
                table.CategoryMaskOf("牛") == ChatAnimalAliases.MaskLand
                && (table.CategoryMaskOf("牛") & ChatAnimalAliases.MaskWater) == 0,
                table.CategoryMaskOf("牛").ToString());
            result.Check("*** 鱼 is water-only; 鸟 includes birds and never water ***",
                table.CategoryMaskOf("鱼") == ChatAnimalAliases.MaskWater
                && (table.CategoryMaskOf("鸟") & ChatAnimalAliases.MaskBird) != 0
                && (table.CategoryMaskOf("鸟") & ChatAnimalAliases.MaskWater) == 0,
                // 注意 鸟 还会带上陆地：鸵鸟是陆地动物，名字里就有个"鸟"字 —— 这是对的，
                // 断言只钉住"绝不会把水生动物算进鸟"
                table.CategoryMaskOf("鱼") + "/" + table.CategoryMaskOf("鸟"));
            result.Check("a word the table does not know imposes no category filter",
                table.CategoryMaskOf("三叶虫") == 0);
            result.Check("the mask name round-trips (file wording)",
                ChatAnimalAliases.MaskOf(ChatAnimalAliases.MaskName(ChatAnimalAliases.MaskLand))
                    == ChatAnimalAliases.MaskLand
                && ChatAnimalAliases.MaskOf("nonsense") == ChatAnimalAliases.MaskAny);
        }

        private static void CaseMatching(BtSelfTest.TestResult result)
        {
            result.Check("exact match hits",
                ChatAnimalAliases.MatchOne("cow", "cow") && ChatAnimalAliases.MatchOne("牛", "牛"));

            result.Check("*** Chinese matches by substring in both directions (牛 / 公牛 / 牛狼) ***",
                ChatAnimalAliases.MatchOne("公牛", "牛")
                && ChatAnimalAliases.MatchOne("牛", "公牛")
                && ChatAnimalAliases.MatchOne("牛狼", "牛"));

            result.Check("*** ASCII needs 3+ characters before substring matching (ow must not hit cow) ***",
                !ChatAnimalAliases.MatchOne("cow", "ow")
                && !ChatAnimalAliases.MatchOne("ox", "o")
                && ChatAnimalAliases.MatchOne("cow", "cow"));

            result.Check("ASCII substring still works for long enough words",
                ChatAnimalAliases.MatchOne("killer whale", "whale"));

            result.Check("different animals do not collide",
                !ChatAnimalAliases.MatchOne("wolf", "cow")
                && !ChatAnimalAliases.MatchOne("牛", "狼"));

            result.Check("empty names never match",
                !ChatAnimalAliases.MatchOne("", "牛") && !ChatAnimalAliases.MatchOne("牛", ""));

            var candidates = new List<string> { "牛", "cow", "bull" };
            result.Check("MatchWords works over a candidate list",
                ChatAnimalAliases.MatchWords("公牛", candidates)
                && ChatAnimalAliases.MatchWords("Cow", candidates)
                && !ChatAnimalAliases.MatchWords("狼", candidates));
        }

        private static void CaseParseRejects(BtSelfTest.TestResult result)
        {
            Func<string, string> parse = delegate(string json)
            {
                string error;
                ChatAnimalAliases parsed = ChatAnimalAliasesJson.Parse(json, "test.animals.json", out error);
                return parsed == null ? (error ?? "<no error>") : null;
            };

            string bad = parse("{ not json ");
            result.Check("invalid JSON is rejected",
                bad != null && bad.IndexOf("invalid JSON", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatanimals\",\"version\":1,\"id\":\"x\"}");
            result.Check("a table without a species array is rejected",
                bad != null && bad.IndexOf("'species' array", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatphrases\",\"version\":1,\"id\":\"x\",\"species\":["
                + "{\"key\":\"cow\",\"names\":[\"牛\"]}]}");
            result.Check("a foreign format is rejected",
                bad != null && bad.IndexOf("unsupported format", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatanimals\",\"version\":3,\"id\":\"x\",\"species\":["
                + "{\"key\":\"cow\",\"names\":[\"牛\"]}]}");
            result.Check("a future format version is rejected",
                bad != null && bad.IndexOf("unsupported chat animals format version 3", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatanimals\",\"version\":1,\"species\":["
                + "{\"key\":\"cow\",\"names\":[\"牛\"]}]}");
            result.Check("a table without an id is rejected",
                bad != null && bad.IndexOf("need an 'id'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatanimals\",\"version\":1,\"id\":\"x\",\"species\":[]}");
            result.Check("an empty species list is rejected",
                bad != null && bad.IndexOf("at least one species", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatanimals\",\"version\":1,\"id\":\"x\",\"species\":["
                + "{\"names\":[\"牛\"]}]}");
            result.Check("a species without a key is rejected",
                bad != null && bad.IndexOf("needs a 'key'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatanimals\",\"version\":1,\"id\":\"x\",\"species\":["
                + "{\"key\":\"cow\",\"names\":[]}]}");
            result.Check("a species with no names is rejected",
                bad != null && bad.IndexOf("non-empty 'names'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatanimals\",\"version\":1,\"id\":\"x\",\"species\":["
                + "{\"key\":\"cow\",\"names\":[\"牛\"]},{\"key\":\"cow\",\"names\":[\"奶牛\"]}]}");
            result.Check("a duplicate species key is rejected",
                bad != null && bad.IndexOf("duplicate animal key", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatanimals\",\"version\":1,\"id\":\"x\",\"species\":["
                + "{\"key\":\"cow\",\"names\":[\"牛\"]},{\"key\":\"bull\",\"names\":[\"牛\"]}]}");
            result.Check("*** the same name in two species is rejected (打牛 would be ambiguous) ***",
                bad != null && bad.IndexOf("already used by another species", StringComparison.Ordinal) >= 0, bad);

            string error;
            ChatAnimalAliases ok = ChatAnimalAliasesJson.Parse(
                "{\"format\":\"chatanimals\",\"version\":1,\"id\":\"x\",\"species\":["
                + "{\"key\":\"cow\",\"names\":[\"牛\",\"cow\"]}]}", "ok.animals.json", out error);
            result.Check("a minimal table parses and expands",
                ok != null && ok.Expand("牛").Contains("cow"), error);
        }

        private static void CaseRoundTrip(BtSelfTest.TestResult result)
        {
            string text = ChatAnimalAliasesJson.Write(ChatAnimalAliases.Default);
            string error;
            ChatAnimalAliases parsed = ChatAnimalAliasesJson.Parse(text, "roundtrip.animals.json", out error);
            result.Check("the generated seed parses back", parsed != null, error);
            if (parsed == null)
                return;

            result.Check("*** writing the built-in table and reading it back keeps the same content hash ***",
                parsed.SourceHash == ChatAnimalAliases.Default.SourceHash,
                parsed.SourceHash + " vs " + ChatAnimalAliases.Default.SourceHash);
            result.Check("...and the same text (no drift)",
                ChatAnimalAliasesJson.Write(parsed) == text);
            result.Check("...and the same expansion behaviour",
                parsed.Expand("牛").Contains("牛") && parsed.Expand("牛").Contains("cow")
                && parsed.Expand("狼").Contains("wolf"));
            result.Check("...and it is marked as coming from a file",
                !parsed.IsBuiltin && !string.IsNullOrEmpty(parsed.SourcePath));
        }

        private static void CaseHotReload(BtSelfTest.TestResult result)
        {
            List<string> savedDirectories = new List<string>(ChatAnimalCatalog.Directories);
            ChatAnimalAliases saved = ChatAnimalCatalog.Current;
            string root = Path.Combine(Path.GetTempPath(),
                "pai-animals-selftest-" + Guid.NewGuid().ToString("n"));
            try
            {
                string directory = Path.Combine(root, DigestCatalog.PlayerAiFolderName,
                    ChatPhraseCatalog.FolderName);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, ChatAnimalAliases.FileName);

                ChatAnimalAliases draft = ChatAnimalAliases.Default.Clone();
                draft.Species.Clear();
                draft.Species.Add(new ChatAnimalSpec
                {
                    Key = "snail",
                    Names = new List<string> { "蜗牛", "snail" }
                });
                File.WriteAllText(path, ChatAnimalAliasesJson.Write(draft));

                ChatAnimalCatalog.ResetForTests();
                ChatAnimalCatalog.Configure(new[] { directory });
                ChatAnimalAliases loaded = ChatAnimalCatalog.Current;
                result.Check("*** hot reload: the animals file becomes the active table ***",
                    !loaded.IsBuiltin
                    && string.Equals(loaded.SourcePath, path, StringComparison.OrdinalIgnoreCase),
                    loaded.SourcePath);
                result.Check("...and expansion follows the file",
                    loaded.Expand("蜗牛").Contains("snail")
                    && !loaded.Expand("蜗牛").Contains("cow"));

                string before = loaded.SourceHash;
                // ⚠️ 必须让时钟走一拍再写：文件戳的分辨率是系统时钟一拍（约 15 ms），
                // 同一拍内的两次写入时间戳相同，而这里改的是**等长**内容
                // （蜗牛 → 田螺，长度一样），连"长度"这个副判据也救不了。
                System.Threading.Thread.Sleep(30);
                File.WriteAllText(path, ChatAnimalAliasesJson.Write(draft)
                    .Replace("蜗牛", "田螺"));
                ChatAnimalAliases reloaded = ChatAnimalCatalog.Current;
                result.Check("*** hot reload: editing the file changes the table (no restart) ***",
                    !string.Equals(reloaded.SourceHash, before, StringComparison.Ordinal)
                    && reloaded.Expand("田螺").Contains("snail"),
                    reloaded.SourceHash + " vs " + before);

                string good = reloaded.SourceHash;
                System.Threading.Thread.Sleep(30);   // 同上：别让两次写入落在同一时钟拍里
                File.WriteAllText(path, "{\"format\":\"chatanimals\",\"version\":1,\"species\":[]}");
                ChatAnimalAliases after = ChatAnimalCatalog.Current;
                result.Check("*** a broken animals file is rejected and the previous table keeps running ***",
                    after.SourceHash == good && !string.IsNullOrEmpty(ChatAnimalCatalog.LastError),
                    after.SourceHash + " err=" + ChatAnimalCatalog.LastError);

                File.Delete(path);
                result.Check("deleting the file falls back to the built-in table",
                    ChatAnimalCatalog.Current.IsBuiltin
                    && ChatAnimalCatalog.Current.SourceHash == ChatAnimalAliases.Default.SourceHash);

                // 同目录里住着 phrases.json：别名表只认自己的文件名，不许把短语表读进来
                File.WriteAllText(Path.Combine(directory, ChatPhraseCatalog.DefaultFileName),
                    ChatPhrasesJson.Write(ChatPhrases.Default));
                result.Check("*** a phrases.json sitting next to it is ignored by the animal catalog ***",
                    ChatAnimalCatalog.Current.IsBuiltin && ChatAnimalCatalog.CurrentPath == null,
                    ChatAnimalCatalog.CurrentPath);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
                ChatAnimalCatalog.ResetForTests();
                ChatAnimalCatalog.Configure(savedDirectories);
                ChatAnimalCatalog.EnsureFresh();   // 让生效表回到"磁盘上真实的那一份"
            }
            result.Check("the animal catalog is restored after the self-test (global seam discipline)",
                ChatAnimalCatalog.Current.SourceHash == saved.SourceHash
                || ChatAnimalCatalog.Current.SourceHash == ChatAnimalAliases.Default.SourceHash,
                ChatAnimalCatalog.CurrentPath + " saved=" + saved.SourceHash);
        }
    }
}

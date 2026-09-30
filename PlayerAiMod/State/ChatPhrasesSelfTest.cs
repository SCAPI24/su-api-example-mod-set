using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 聊天短语表自检（2026-09-26）：把"聊天文本 → 意图"这条判定钉住。
    ///
    /// 四组断言：
    ///   ① **词边界**：`become` 不许命中 `come`（纯 ASCII 短语按词边界，中文按子串）；
    ///   ② **顺序即优先级**：`别过来` 必须判成 stop 而不是 come（否定句里含肯定词）；
    ///   ③ **拒坏文件**：未知意图 / 空短语 / 重复意图 / 坏版本 / 缺 id 都要明确报错；
    ///   ④ **热重载与"绝不半生效"**：改文件立刻生效；写坏了**保留上一份**；缺文件用内置默认。
    /// </summary>
    public static class ChatPhrasesSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "ChatPhrasesSelfTest" };
            try { CaseMatching(result); } catch (Exception e) { result.Check("case:matching", false, e.Message); }
            try { CaseArgument(result); } catch (Exception e) { result.Check("case:argument", false, e.Message); }
            try { CaseIntentSet(result); } catch (Exception e) { result.Check("case:intent set", false, e.Message); }
            try { CaseParseRejects(result); } catch (Exception e) { result.Check("case:parse rejects", false, e.Message); }
            try { CaseRoundTrip(result); } catch (Exception e) { result.Check("case:round trip", false, e.Message); }
            try { CaseHotReload(result); } catch (Exception e) { result.Check("case:hot reload", false, e.Message); }
            try { CaseInstall(result); } catch (Exception e) { result.Check("case:install", false, e.Message); }
            return result;
        }

        private static void CaseMatching(BtSelfTest.TestResult result)
        {
            ChatPhrases table = ChatPhrases.Default;

            result.Check("*** Chinese: 过来 -> come ***",
                ChatPhrases.Match(table, "过来") == ChatIntents.Come);
            result.Check("Chinese with punctuation: 过来。 -> come",
                ChatPhrases.Match(table, "过来。") == ChatIntents.Come);
            result.Check("Chinese sentence: 你能过来一下吗 -> come",
                ChatPhrases.Match(table, "你能过来一下吗") == ChatIntents.Come);
            result.Check("*** negation wins: 别过来 -> stop (not come) ***",
                ChatPhrases.Match(table, "别过来") == ChatIntents.Stop);
            result.Check("Chinese: 站住 -> stop", ChatPhrases.Match(table, "站住") == ChatIntents.Stop);
            result.Check("Chinese: 跟着我 -> follow", ChatPhrases.Match(table, "跟着我") == ChatIntents.Follow);
            result.Check("Chinese: 别理我 -> ignore", ChatPhrases.Match(table, "别理我") == ChatIntents.Ignore);

            result.Check("*** English word boundary: become must NOT match come ***",
                ChatPhrases.Match(table, "I will become a miner") == null);
            result.Check("English: come here -> come",
                ChatPhrases.Match(table, "come here") == ChatIntents.Come);
            result.Check("English is case-insensitive: COME HERE -> come",
                ChatPhrases.Match(table, "COME HERE") == ChatIntents.Come);
            result.Check("English: follow me -> follow",
                ChatPhrases.Match(table, "follow me") == ChatIntents.Follow);
            result.Check("English: stop -> stop", ChatPhrases.Match(table, "stop") == ChatIntents.Stop);
            result.Check("a random sentence matches nothing",
                ChatPhrases.Match(table, "nice weather today") == null);
            result.Check("empty text matches nothing",
                ChatPhrases.Match(table, "   ") == null && ChatPhrases.Match(table, null) == null);
        }

        private static void CaseArgument(BtSelfTest.TestResult result)
        {
            ChatPhrases table = ChatPhrases.Default;

            // ---- 触发词 → 意图 + 参数（打猎：说打谁才有意义）----
            ChatMatch match = ChatPhrases.MatchEx(table, "帮我打一下牛");
            result.Check("*** 帮我打一下牛 -> hunt + argument 牛 ***",
                match != null && match.Intent == ChatIntents.Hunt && match.Argument == "牛",
                match == null ? "<null>" : match.Intent + "/" + match.Argument);

            match = ChatPhrases.MatchEx(table, "帮我打一下牛。");
            result.Check("punctuation is trimmed from the argument",
                match != null && match.Argument == "牛", match == null ? "<null>" : match.Argument);

            match = ChatPhrases.MatchEx(table, "打死那只狼");
            result.Check("*** 打死那只狼 -> hunt + 狼 (leading 那只 stripped) ***",
                match != null && match.Intent == ChatIntents.Hunt && match.Argument == "狼",
                match == null ? "<null>" : match.Intent + "/" + match.Argument);

            match = ChatPhrases.MatchEx(table, "帮我打一下 牛 和 狼 吧");
            result.Check("*** several animals in one sentence -> 牛,狼 ***",
                match != null && match.Argument == "牛,狼",
                match == null ? "<null>" : match.Argument);

            match = ChatPhrases.MatchEx(table, "kill the cow");
            result.Check("English: kill the cow -> hunt + cow",
                match != null && match.Intent == ChatIntents.Hunt && match.Argument == "cow",
                match == null ? "<null>" : match.Intent + "/" + match.Argument);

            match = ChatPhrases.MatchEx(table, "帮我打死牛");
            result.Check("the LONGEST trigger word is stripped (帮我打死, not 打死)",
                match != null && match.Argument == "牛",
                match == null ? "<null>" : match.Argument);

            // 说了触发词却没说什么动物：参数是空的，树据"空"什么都不做（不猜）
            match = ChatPhrases.MatchEx(table, "帮我打一下");
            result.Check("*** a hunt with no animal yields an empty argument (nothing is guessed) ***",
                match != null && match.Intent == ChatIntents.Hunt && !match.HasArgument,
                match == null ? "<null>" : "'" + match.Argument + "'");

            // ---- 否定优先：这些必须判成 stop，否则"别打牛"会变成真的去打牛 ----
            result.Check("*** 别打牛 -> stop (negation beats hunt) ***",
                ChatPhrases.Match(table, "别打牛") == ChatIntents.Stop);
            result.Check("不要杀狼 -> stop", ChatPhrases.Match(table, "不要杀狼") == ChatIntents.Stop);

            // ---- 不带参数的意图不抠参数（Argument 为 null，不是空串）----
            match = ChatPhrases.MatchEx(table, "过来");
            result.Check("an intent without TakesArgument reports no argument at all",
                match != null && match.Intent == ChatIntents.Come && match.Argument == null);

            // ---- 参数不该被"语气词剥离"吃掉真名字 ----
            match = ChatPhrases.MatchEx(table, "去打牛");
            result.Check("short trigger still leaves the animal intact",
                match != null && match.Argument == "牛",
                match == null ? "<null>" : match.Argument);
        }

        private static void CaseIntentSet(BtSelfTest.TestResult result)
        {
            // 意图是**闭集**：数据只能给已知意图配说法（否则模型/用户答出一个树里没分支的枚举）
            result.Check("the intent set is closed and matches the tree",
                ChatIntents.IsKnown("come") && ChatIntents.IsKnown("FOLLOW")
                && ChatIntents.IsKnown("stop") && ChatIntents.IsKnown("hunt")
                && ChatIntents.IsKnown("ignore")
                && !ChatIntents.IsKnown("dance") && !ChatIntents.IsKnown(""));
            result.Check("every intent has phrases in the built-in table",
                ChatPhrases.Default.Intent(ChatIntents.Come).Phrases.Count > 0
                && ChatPhrases.Default.Intent(ChatIntents.Follow).Phrases.Count > 0
                && ChatPhrases.Default.Intent(ChatIntents.Stop).Phrases.Count > 0
                && ChatPhrases.Default.Intent(ChatIntents.Hunt).Phrases.Count > 0
                && ChatPhrases.Default.Intent(ChatIntents.Ignore).Phrases.Count > 0);
            result.Check("*** only hunt declares TakesArgument (it is the only one the tree parses) ***",
                ChatPhrases.Default.Intent(ChatIntents.Hunt).TakesArgument
                && !ChatPhrases.Default.Intent(ChatIntents.Come).TakesArgument
                && ChatIntents.AcceptsArgument(ChatIntents.Hunt)
                && !ChatIntents.AcceptsArgument(ChatIntents.Come));
            result.Check("*** stop is listed before hunt (别打 must not reach the hunter) ***",
                IndexOfIntent(ChatPhrases.Default, ChatIntents.Stop)
                    < IndexOfIntent(ChatPhrases.Default, ChatIntents.Hunt));
            result.Check("*** stop is listed before come (negations contain the positive word) ***",
                IndexOfIntent(ChatPhrases.Default, ChatIntents.Stop)
                    < IndexOfIntent(ChatPhrases.Default, ChatIntents.Come));
            result.Check("system senders are ignored by default",
                ChatPhrases.Default.IsIgnoredSender("ScMP")
                && !ChatPhrases.Default.IsIgnoredSender("Android User"));
        }

        private static int IndexOfIntent(ChatPhrases table, string key)
        {
            for (int i = 0; i < table.Intents.Count; i++)
                if (string.Equals(table.Intents[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static void CaseParseRejects(BtSelfTest.TestResult result)
        {
            Func<string, string> parse = delegate(string json)
            {
                string error;
                ChatPhrases parsed = ChatPhrasesJson.Parse(json, "test.phrases.json", out error);
                return parsed == null ? (error ?? "<no error>") : null;
            };

            string bad = parse("{ not json ");
            result.Check("invalid JSON is rejected",
                bad != null && bad.IndexOf("invalid JSON", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatphrases\",\"version\":1,\"id\":\"x\",\"intents\":[]}");
            result.Check("an empty intent list is rejected",
                bad != null && bad.IndexOf("non-empty 'intents'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"digest\",\"version\":1,\"id\":\"x\",\"intents\":[{\"key\":\"come\",\"phrases\":[\"过来\"]}]}");
            result.Check("a foreign format is rejected",
                bad != null && bad.IndexOf("unsupported format", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatphrases\",\"version\":7,\"id\":\"x\",\"intents\":[{\"key\":\"come\",\"phrases\":[\"过来\"]}]}");
            result.Check("a future format version is rejected",
                bad != null && bad.IndexOf("unsupported chat phrases format version 7", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatphrases\",\"version\":1,\"id\":\"x\",\"intents\":[{\"key\":\"dance\",\"phrases\":[\"跳舞\"]}]}");
            result.Check("*** an unknown intent key is rejected (with the known list) ***",
                bad != null && bad.IndexOf("unknown intent 'dance'", StringComparison.Ordinal) >= 0
                && bad.IndexOf("come", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatphrases\",\"version\":1,\"id\":\"x\",\"intents\":[{\"key\":\"come\",\"phrases\":[]}]}");
            result.Check("an intent with no phrases is rejected",
                bad != null && bad.IndexOf("non-empty 'phrases'", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatphrases\",\"version\":1,\"id\":\"x\",\"intents\":["
                + "{\"key\":\"come\",\"phrases\":[\"过来\"]},{\"key\":\"come\",\"phrases\":[\"来\"]}]}");
            result.Check("a duplicate intent is rejected",
                bad != null && bad.IndexOf("duplicate chat intent", StringComparison.Ordinal) >= 0, bad);

            bad = parse("{\"format\":\"chatphrases\",\"version\":1,\"intents\":[{\"key\":\"come\",\"phrases\":[\"过来\"]}]}");
            result.Check("a table without an id is rejected",
                bad != null && bad.IndexOf("need an 'id'", StringComparison.Ordinal) >= 0, bad);

            // 正例：只给一部分意图配说法也算合法（其余意图自然永不命中）
            string error;
            ChatPhrases partial = ChatPhrasesJson.Parse(
                "{\"format\":\"chatphrases\",\"version\":1,\"id\":\"x\",\"intents\":[{\"key\":\"come\",\"phrases\":[\"过来\"]}]}",
                "partial.phrases.json", out error);
            result.Check("a partial table (one intent only) parses",
                partial != null && partial.Intents.Count == 1
                && ChatPhrases.Match(partial, "过来") == ChatIntents.Come
                && ChatPhrases.Match(partial, "stop") == null, error);

            // ---- takesArgument 的合法性：只有 hunt 由树解析参数 ----
            bad = parse("{\"format\":\"chatphrases\",\"version\":1,\"id\":\"x\",\"intents\":["
                + "{\"key\":\"come\",\"takesArgument\":true,\"phrases\":[\"过来\"]}]}");
            result.Check("*** takesArgument on a non-hunt intent is rejected ***",
                bad != null && bad.IndexOf("cannot take an argument", StringComparison.Ordinal) >= 0, bad);

            ChatPhrases withArg = ChatPhrasesJson.Parse(
                "{\"format\":\"chatphrases\",\"version\":1,\"id\":\"x\",\"intents\":["
                + "{\"key\":\"hunt\",\"takesArgument\":true,\"phrases\":[\"打\"]}]}",
                "arg.phrases.json", out error);
            result.Check("hunt with takesArgument parses and extracts the argument",
                withArg != null && withArg.Intent(ChatIntents.Hunt).TakesArgument
                && ChatPhrases.MatchEx(withArg, "打牛").Argument == "牛", error);
        }

        private static void CaseRoundTrip(BtSelfTest.TestResult result)
        {
            string text = ChatPhrasesJson.Write(ChatPhrases.Default);
            string error;
            ChatPhrases parsed = ChatPhrasesJson.Parse(text, "roundtrip.phrases.json", out error);
            result.Check("the generated seed parses back", parsed != null, error);
            if (parsed == null)
                return;
            result.Check("*** writing the built-in table and reading it back keeps the same content hash ***",
                parsed.SourceHash == ChatPhrases.Default.SourceHash,
                parsed.SourceHash + " vs " + ChatPhrases.Default.SourceHash);
            result.Check("...and the same text (no drift)",
                ChatPhrasesJson.Write(parsed) == text);
            result.Check("...and the same matching behaviour",
                ChatPhrases.Match(parsed, "过来") == ChatIntents.Come
                && ChatPhrases.Match(parsed, "become") == null
                && ChatPhrases.Match(parsed, "别过来") == ChatIntents.Stop);
            result.Check("*** the round trip keeps hunt's takesArgument flag and its phrases ***",
                parsed.Intent(ChatIntents.Hunt) != null
                && parsed.Intent(ChatIntents.Hunt).TakesArgument
                && ChatPhrases.MatchEx(parsed, "帮我打一下牛").Argument == "牛");
        }

        private static void CaseHotReload(BtSelfTest.TestResult result)
        {
            List<string> savedDirectories = new List<string>(ChatPhraseCatalog.Directories);
            ChatPhrases saved = ChatPhraseCatalog.Current;
            string root = Path.Combine(Path.GetTempPath(),
                "pai-chat-selftest-" + Guid.NewGuid().ToString("n"));
            try
            {
                string directory = Path.Combine(root, DigestCatalog.PlayerAiFolderName,
                    ChatPhraseCatalog.FolderName);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, ChatPhraseCatalog.DefaultFileName);

                // 一份"只有 come、且说法是自定义"的表
                ChatPhrases draft = ChatPhrases.Default.Clone();
                draft.Intents.Clear();
                draft.Intents.Add(new ChatIntentSpec
                {
                    Key = ChatIntents.Come,
                    Phrases = new List<string> { "来我这儿" }
                });
                File.WriteAllText(path, ChatPhrasesJson.Write(draft));

                ChatPhraseCatalog.ResetForTests();
                ChatPhraseCatalog.Configure(new[] { directory });
                ChatPhrases loaded = ChatPhraseCatalog.Current;
                result.Check("*** hot reload: the phrases file becomes the active table ***",
                    !loaded.IsBuiltin
                    && string.Equals(loaded.SourcePath, path, StringComparison.OrdinalIgnoreCase),
                    loaded.SourcePath);
                result.Check("...and matching follows the file",
                    ChatPhrases.Match(loaded, "来我这儿") == ChatIntents.Come
                    && ChatPhrases.Match(loaded, "过来") == null);

                // 改文件 → 戳变 → 下一次读取生效
                string before = loaded.SourceHash;
                File.WriteAllText(path, ChatPhrasesJson.Write(draft)
                    .Replace("来我这儿", "过来呀"));
                ChatPhrases reloaded = ChatPhraseCatalog.Current;
                result.Check("*** hot reload: editing the file changes the table (no restart) ***",
                    !string.Equals(reloaded.SourceHash, before, StringComparison.Ordinal)
                    && ChatPhrases.Match(reloaded, "过来呀") == ChatIntents.Come,
                    reloaded.SourceHash + " vs " + before);

                // 写坏 → 拒用，保留上一份
                string good = reloaded.SourceHash;
                File.WriteAllText(path, "{\"format\":\"chatphrases\",\"version\":1,\"intents\":[]}");
                ChatPhrases after = ChatPhraseCatalog.Current;
                result.Check("*** a broken phrases file is rejected and the previous table keeps running ***",
                    after.SourceHash == good && !string.IsNullOrEmpty(ChatPhraseCatalog.LastError),
                    after.SourceHash + " err=" + ChatPhraseCatalog.LastError);

                // 删掉文件 → 退回内置默认
                File.Delete(path);
                result.Check("deleting the file falls back to the built-in table",
                    ChatPhraseCatalog.Current.IsBuiltin
                    && ChatPhraseCatalog.Current.SourceHash == ChatPhrases.Default.SourceHash);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
                ChatPhraseCatalog.ResetForTests();
                ChatPhraseCatalog.Configure(savedDirectories);
                ChatPhraseCatalog.EnsureFresh();   // 让生效表回到"磁盘上真实的那一份"
            }
            result.Check("the phrase catalog is restored after the self-test (global seam discipline)",
                ChatPhraseCatalog.Current.SourceHash == saved.SourceHash
                || ChatPhraseCatalog.Current.SourceHash == ChatPhrases.Default.SourceHash,
                ChatPhraseCatalog.CurrentPath + " saved=" + saved.SourceHash);
        }

        private static void CaseInstall(BtSelfTest.TestResult result)
        {
            string root = Path.Combine(Path.GetTempPath(),
                "pai-chat-install-" + Guid.NewGuid().ToString("n"));
            try
            {
                List<string> installed;
                string error;
                int written = ChatPhraseTemplates.Install(root, out installed, out error);
                result.Check("*** install writes both seeds on a fresh instance (phrases + animals) ***",
                    written == 2 && installed.Count == 2 && string.IsNullOrEmpty(error),
                    written + " " + error);

                string path = Path.Combine(root, DigestCatalog.PlayerAiFolderName,
                    ChatPhraseCatalog.FolderName, ChatPhraseCatalog.DefaultFileName);
                result.Check("the seed lands in PlayerAi/Chat/phrases.json",
                    File.Exists(path), path);

                string animals = Path.Combine(root, DigestCatalog.PlayerAiFolderName,
                    ChatPhraseCatalog.FolderName, ChatAnimalAliases.FileName);
                result.Check("the animal alias seed lands next to it",
                    File.Exists(animals), animals);
                string aliasError;
                ChatAnimalAliases aliases = ChatAnimalCatalog.Load(animals, out aliasError);
                result.Check("the animal seed parses and covers cow and wolf",
                    aliases != null
                    && aliases.Expand("牛").Contains("牛")
                    && aliases.Expand("狼").Contains("狼"), aliasError);

                File.WriteAllText(path, ChatPhrasesJson.Write(ChatPhrases.Default).Replace("过来", "过来吧"));
                int again = ChatPhraseTemplates.Install(root, out installed, out error);
                result.Check("*** install never overwrites an existing table ***",
                    again == 0
                    && File.ReadAllText(path).IndexOf("过来吧", StringComparison.Ordinal) >= 0);

                ChatPhrases parsed = ChatPhraseCatalog.Load(path, out error);
                result.Check("the (edited) seed still parses", parsed != null, error);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }
    }
}

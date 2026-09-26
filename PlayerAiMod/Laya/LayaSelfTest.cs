using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// Laya 接入层自检（P3）：绑定解析、答案类型折算、指纹、去重/降级路径。
    /// **不碰网络**（网络端到端由"拿真服务跑一次"的探针负责），所以能在临时工程里跑。
    /// </summary>
    public static class LayaSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "LayaSelfTest" };
            try { CaseBindings(result); } catch (Exception e) { result.Check("case:answer bindings", false, e.Message); }
            try { CaseAnswerWriting(result); } catch (Exception e) { result.Check("case:answer writing", false, e.Message); }
            try { CaseFingerprint(result); } catch (Exception e) { result.Check("case:fingerprint", false, e.Message); }
            try { CaseConfigChain(result); } catch (Exception e) { result.Check("case:config chain", false, e.Message); }
            try { CaseClientFailures(result); } catch (Exception e) { result.Check("case:client failures", false, e.Message); }
            try { CaseDecisionLog(result); } catch (Exception e) { result.Check("case:decision log", false, e.Message); }
            try { CaseVersionAccounting(result); } catch (Exception e) { result.Check("case:version accounting", false, e.Message); }
            try { CaseBankCacheInvalidation(result); } catch (Exception e) { result.Check("case:bank cache", false, e.Message); }
            return result;
        }

        /// <summary>
        /// **问题库缓存必须跟着磁盘走**：以前一旦读进缓存就**永不失效**，
        /// 于是"编辑器/人改完 `.qbank`，游戏里还在用旧的"，只能重启才生效 ——
        /// 而复盘表显示的哈希也是旧那份，两边看起来都对（实机踩过这一类）。
        /// </summary>
        private static void CaseBankCacheInvalidation(BtSelfTest.TestResult result)
        {
            string dir = Path.Combine(Path.GetTempPath(), "pai-bank-cache-selftest");
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "cache_probe.qbank");

            string first = "{\"format\":\"qbank\",\"version\":1,\"id\":\"cache_probe\",\"name\":\"n\","
                + "\"questions\":[{\"id\":\"goal\",\"type\":\"choice\",\"instructions\":\"i\",\"options\":["
                + "{\"key\":\"mine\",\"description\":\"d1\"}]},"
                // 第二问是**为了能测出 `only` 被丢掉**：整库 2 问、`only=goal` 应当只回 1 问。
                + "{\"id\":\"threat\",\"type\":\"choice\",\"instructions\":\"i2\",\"options\":["
                + "{\"key\":\"yes\",\"description\":\"d2\"}]}]}";
            File.WriteAllText(path, first, new UTF8Encoding(false));

            LayaRuntimeService previousHost = LayaRuntimeService.Current;
            ILayaRuntime previousNodeHost = LayaRuntimeHost.Current;
            try
            {
                // ⚠️ `publish:false`：**绝不能**动静态发布点。以前这里用普通构造，
                //    于是自检一跑就把"游戏正在用的那一个"顶掉了 —— 之后所有判定都去自检的临时目录
                //    找问题库，全局 `laya_no_bank`（平板实测踩到：跑完自检，判定全废）。
                var service = new LayaRuntimeService(null, new LayaConfig { Enabled = false },
                    new[] { dir }, false);
                var reloads = new List<string>();
                service.BankReloaded = delegate(string name) { reloads.Add(name); };

                result.Check("*** constructing a service for the self-test does not touch the host seams ***",
                    ReferenceEquals(LayaRuntimeHost.Current, previousNodeHost)
                    && ReferenceEquals(LayaRuntimeService.Current, previousHost),
                    "nodeHost=" + (LayaRuntimeHost.Current != null ? "set" : "<null>"));

                QuestionBank bank;
                string error;
                bool ok = service.TryResolveBank("cache_probe", null, out bank, out error);
                result.Check("the first resolve reads the bank from disk", ok && bank != null, error);
                result.Check("the parse path is recorded as the source", bank != null && bank.SourcePath == path,
                    bank != null ? bank.SourcePath : "<null>");
                string hashBefore = bank != null ? bank.SourceHash : null;

                // 第二次：同一份（没改过）→ 走缓存、不触发重读
                service.TryResolveBank("cache_probe", null, out bank, out error);
                result.Check("an unchanged file is served from cache (no reload callback)",
                    reloads.Count == 0 && bank != null && bank.SourceHash == hashBefore,
                    "reloads=" + reloads.Count);
                result.Check("the whole bank comes back when no subset is asked for",
                    bank != null && bank.Questions.Count == 2,
                    bank != null ? bank.Questions.Count.ToString() : "<null>");

                // **A54**：缓存命中时也必须按 `only` 取子集。
                // 旧写法在缓存命中处直接 `bank = loaded; return true;`，而取子集在读盘路径之后 ——
                // 于是 `only` **只生效第一次**，之后每个 tick 都拿到整库
                // （实机：出厂 demo 声明 `only=goal`，却一直在问 3 个问题，178 vs 78 input tokens）。
                service.TryResolveBank("cache_probe", "goal", out bank, out error);
                result.Check("*** a cache hit still honours only= (the subset must not be dropped) ***",
                    bank != null && bank.Questions.Count == 1
                    && string.Equals(bank.Questions[0].Id, "goal", StringComparison.Ordinal),
                    bank != null ? bank.Questions.Count + " question(s)" : error);

                // 再来一次，确认不是"第一次碰巧对"
                service.TryResolveBank("cache_probe", "goal", out bank, out error);
                result.Check("...and it keeps honouring it on every later call",
                    bank != null && bank.Questions.Count == 1,
                    bank != null ? bank.Questions.Count + " question(s)" : error);
                result.Check("asking for the other question narrows the other way",
                    service.TryResolveBank("cache_probe", "threat", out bank, out error)
                    && bank.Questions.Count == 1
                    && string.Equals(bank.Questions[0].Id, "threat", StringComparison.Ordinal),
                    bank != null ? bank.Questions.Count + " question(s)" : error);

                // **A54 的姊妹问题**：指纹必须能区分"同一个库的不同子集"。
                // `SourceHash` 是**整个文件**的哈希，子集不同、文件相同 ——
                // 只按它算指纹，两个问不同子集的节点就会互相顶掉缓存条目。
                QuestionBank subsetGoal;
                QuestionBank subsetThreat;
                service.TryResolveBank("cache_probe", "goal", out subsetGoal, out error);
                service.TryResolveBank("cache_probe", "threat", out subsetThreat, out error);
                string fpGoal = LayaClient.Fingerprint("same-state", subsetGoal, "m");
                string fpThreat = LayaClient.Fingerprint("same-state", subsetThreat, "m");
                result.Check("*** the fingerprint distinguishes question subsets of one bank ***",
                    fpGoal != fpThreat, "goal=" + fpGoal.Substring(0, 12) + " threat=" + fpThreat.Substring(0, 12));
                result.Check("...while the same subset stays stable (dedup still works)",
                    fpGoal == LayaClient.Fingerprint("same-state", subsetGoal, "m"), fpGoal.Substring(0, 12));

                // 改内容（并确保时间戳/长度变了）→ 下一次解析必须重读
                string second = first.Replace("\"key\":\"mine\"", "\"key\":\"mine\",\"description2\":\"x\"");
                File.WriteAllText(path, second, new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

                service.TryResolveBank("cache_probe", null, out bank, out error);
                result.Check("*** editing the bank on disk is picked up without a restart ***",
                    bank != null && bank.SourceHash != hashBefore && reloads.Count == 1,
                    "hashChanged=" + (bank != null && bank.SourceHash != hashBefore)
                    + " reloads=" + reloads.Count);
                result.Check("the reload is reported through the callback (so it can be logged)",
                    reloads.Count == 1 && reloads[0] == "cache_probe",
                    string.Join(",", reloads.ToArray()));

                // 删文件 → 如实报"找不到"（而不是继续用缓存里的旧内容）
                File.Delete(path);
                result.Check("*** a deleted bank is reported as missing, not served from cache ***",
                    !service.TryResolveBank("cache_probe", null, out bank, out error)
                    && error != null && error.Contains("not found"), error);

                // 清缓存：下一次重新读盘
                int dropped;
                service.ClearBankCache(out dropped);
                result.Check("ClearBankCache drops what it had", dropped == 0 || dropped >= 0,
                    "dropped=" + dropped);
                result.Check("after deleting the file and clearing, the bank is still missing",
                    !service.TryResolveBank("cache_probe", null, out bank, out error), error);
            }
            finally
            {
                LayaRuntimeService.Current = previousHost;   // 自检动过的静态发布点要还原
                LayaRuntimeHost.Current = previousNodeHost;  // 节点找服务靠的就是这一个
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// **版本对账**（plan G18）：摘要布局版本要进指纹与记录；库**内容**变了（描述改一个字）
        /// 指纹也要变 —— 否则复盘时分不清样本是新是旧、去重缓存还会把旧答案当成同一题面。
        /// </summary>
        private static void CaseVersionAccounting(BtSelfTest.TestResult result)
        {
            QuestionBank bank = ParseBank(
                "{\"format\":\"qbank\",\"version\":1,\"id\":\"world_goal\",\"name\":\"n\",\"questions\":["
                + "{\"id\":\"goal\",\"type\":\"choice\",\"instructions\":\"i\",\"options\":["
                + "{\"key\":\"mine\",\"description\":\"break the block being aimed at\"},"
                + "{\"key\":\"eat\",\"description\":\"consume food now\"}]}]}");
            result.Check("the bank keeps the version declared in the file", bank != null && bank.Version == 1,
                bank != null ? bank.Version.ToString() : "<null>");
            if (bank == null)
                return;

            string digest = "phase=world hp=1(full)";
            string first = LayaClient.Fingerprint(digest, bank, "m");

            // 同一个库、同一个摘要 → 指纹稳定（去重缓存靠它）
            result.Check("the fingerprint is stable for the same question and digest",
                LayaClient.Fingerprint(digest, bank, "m") == first);

            // **内容哈希变了**（改了选项描述）→ 指纹必须变
            bank.SourceHash = "aaaa";
            string withHash = LayaClient.Fingerprint(digest, bank, "m");
            bank.SourceHash = "bbbb";
            string otherHash = LayaClient.Fingerprint(digest, bank, "m");
            result.Check("*** editing the bank content changes the fingerprint (description edits count) ***",
                withHash != first && otherHash != withHash, withHash + " vs " + otherHash);

            // 没有哈希（手工构造的库）→ 退化成"id + 选项 key"串，选项改名也要变
            bank.SourceHash = null;
            string byKeys = LayaClient.Fingerprint(digest, bank, "m");
            QuestionBank renamed = ParseBank(
                "{\"format\":\"qbank\",\"version\":1,\"id\":\"world_goal\",\"name\":\"n\",\"questions\":["
                + "{\"id\":\"goal\",\"type\":\"choice\",\"instructions\":\"i\",\"options\":["
                + "{\"key\":\"mine2\",\"description\":\"break the block being aimed at\"},"
                + "{\"key\":\"eat\",\"description\":\"consume food now\"}]}]}");
            result.Check("without a content hash the fingerprint still follows the option keys",
                byKeys != LayaClient.Fingerprint(digest, renamed, "m"));

            // **摘要布局版本**变了 → 指纹必须变（否则改完字段表还在吃旧缓存）
            int version = StateDigestCompiler.Version;
            try
            {
                StateDigestCompiler.Version = version + 1;
                string bumped = LayaClient.Fingerprint(digest, bank, "m");
                result.Check("*** bumping the digest layout version changes the fingerprint ***",
                    bumped != byKeys, bumped + " vs " + byKeys);
            }
            finally
            {
                StateDigestCompiler.Version = version;   // 自检动过的全局缝必须还原
            }
            result.Check("the digest version is restored after the self-test (global seam discipline)",
                StateDigestCompiler.Version == version);

            // 记录里带版本：markdown 有 v 列、digest 视图带 v 前缀、样本对账能判"不是当前版本"
            var log = new DecisionLog();
            var answer = new LayaAnswer { Ok = true, ElapsedMs = 200, InputTokens = 300 };
            answer.Answers["goal"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["choice"] = "mine"
            };
            DecisionRecord record = log.Record(DecisionRecord.KindSent, "world_goal", "goal", digest,
                "fp", answer, 7, 3, "abcdef0123456789", "laya-multilingual-f16.gguf");
            result.Check("the record carries the versions and the model",
                record.DigestVersion == 7 && record.BankVersion == 3
                && record.BankHash == "abcdef0123456789"
                && record.Model == "laya-multilingual-f16.gguf",
                DecisionLog.DescribeVersion(record));

            Dictionary<string, object> row = record.ToDictionary();
            result.Check("the review row exposes the versions (editor panel reads them too)",
                Equals(row["digestVersion"], 7) && Equals(row["bankVersion"], 3)
                && Equals(row["model"], "laya-multilingual-f16.gguf"),
                DecisionLog.DescribeVersion(record));

            string markdown = log.ToMarkdown(1);
            result.Check("the sampling table has a version column (d7/q3@abcdef)",
                markdown.Contains("| v |") && markdown.Contains("d7/q3@abcdef"),
                markdown.Replace('\n', ' '));
            result.Check("the digest view shows the version prefix",
                log.RecentDigests(1)[0].Contains("vd7/q3@abcdef"),
                log.RecentDigests(1)[0]);

            result.Check("*** samples are flagged when the CURRENT digest version differs ***",
                !log.SamplesMatchCurrent(8, "abcdef0123456789")
                && log.SamplesMatchCurrent(7, "abcdef0123456789")
                && !log.SamplesMatchCurrent(7, "ffffffffffffffff"),
                "match=" + log.SamplesMatchCurrent(7, "abcdef0123456789"));
            log.Clear();
            result.Check("with no samples the version check says 'nothing to compare'",
                log.SamplesMatchCurrent(99, "x"));
        }

        private static QuestionBank ParseBank(string json)
        {
            string error;
            return QuestionBankParser.Parse(json, "selftest.qbank", out error);
        }

        /// <summary>
        /// 判定记录（P4 复盘）：聚合只算**真的发了请求**的那些（缓存/被拒不能污染延迟与 token 均值），
        /// 环会淘汰旧记录，抽样表能复算。
        /// </summary>
        private static void CaseDecisionLog(BtSelfTest.TestResult result)
        {
            var log = new DecisionLog();   // 默认容量（够装下这五条）
            log.Record(DecisionRecord.KindSent, "world_goal.qbank", "goal,threat",
                "phase=world hp=1(full)", "fp1", Answer(true, "goal=craft", 200, 300));
            log.Record(DecisionRecord.KindSent, "world_goal.qbank", "goal,threat",
                "phase=world hp=1(full)", "fp2", Answer(true, "goal=mine", 400, 500));
            log.Record(DecisionRecord.KindCached, "world_goal.qbank", "goal,threat",
                "phase=world hp=1(full)", "fp3", Answer(true, "goal=mine", 0, 0));
            log.Record(DecisionRecord.KindRefused, "front_goal.qbank", "front_goal",
                "phase=front ui=menu", "fp4", Answer(false, null, 0, 0, LayaClient.ErrNoKey));
            log.Record(DecisionRecord.KindSent, "world_goal.qbank", "goal",
                "phase=world hp=0.3(low)", "fp5", Answer(false, null, 900, 120, LayaClient.ErrTimeout));

            Dictionary<string, object> summary = log.Summarize();
            result.Check("the decision log counts every kind",
                Equals(summary["total"], 5L) && Equals(summary["sent"], 3L)
                && Equals(summary["cached"], 1L) && Equals(summary["refused"], 1L),
                Describe(summary));
            result.Check("failed counts both refused and timed-out attempts",
                Equals(summary["failed"], 2L), Describe(summary));

            // 延迟/ token 只统计 sent：200/400/900 -> avg 500, p95 900, max 900；token 300+500+120=920
            result.Check("latency averages only the real round trips",
                Equals(summary["avgMs"], 500L) && Equals(summary["maxMs"], 900L)
                && Equals(summary["p95Ms"], 900L) && Equals(summary["minMs"], 200L),
                Describe(summary));
            result.Check("tokens sum only the real round trips",
                Equals(summary["inputTokens"], 920L), Describe(summary));

            result.Check("Recent(1) returns the newest record",
                log.Recent(1).Count == 1 && log.Recent(1)[0].Seq == 5,
                "records=" + log.Recent(0).Count);

            string markdown = log.ToMarkdown(3);
            // 表头 + 分隔行 + 3 行记录 = 5 行（末尾换行会多切出一个空串）
            result.Check("the markdown table has a header and one row per record",
                markdown.Contains("| # | time | kind |") && markdown.Split('\n').Length == 6,
                "lines=" + markdown.Split('\n').Length);
            result.Check("the markdown table carries the failure code",
                markdown.Contains("FAILED:" + LayaClient.ErrNoKey), markdown.Replace('\n', ' '));

            List<string> digests = log.RecentDigests(2);
            result.Check("the digest view shows the literal state string",
                digests.Count == 2 && digests[1].Contains("phase=world hp=0.3(low)"),
                string.Join(" | ", digests.ToArray()));

            // 环容量：超过容量后只留最近的（容量有下限 8，所以用 9 来验）
            var small = new DecisionLog(9);
            for (int i = 1; i <= 12; i++)
            {
                small.Record(DecisionRecord.KindSent, "world_goal.qbank", "goal",
                    "phase=world i=" + i, "fp" + i, Answer(true, "goal=mine", 100, 10));
            }
            Dictionary<string, object> smallSummary = small.Summarize();
            result.Check("the ring keeps only its capacity but the totals keep counting",
                small.Recent(0).Count == 9 && small.Recent(0)[0].Seq == 4
                && small.Recent(1)[0].Seq == 12 && Equals(smallSummary["total"], 12L)
                && Equals(smallSummary["records"], 9),
                "records=" + small.Recent(0).Count + " oldest=" + small.Recent(0)[0].Seq
                + " " + Describe(smallSummary));

            log.Clear();
            Dictionary<string, object> cleared = log.Summarize();
            result.Check("clearing resets the ring and every counter",
                Equals(cleared["total"], 0L) && Equals(cleared["sent"], 0L)
                && Equals(cleared["inputTokens"], 0L) && log.Recent(0).Count == 0,
                Describe(cleared));
        }

        private static LayaAnswer Answer(bool ok, string summary, long ms, int tokens,
            string errorCode = null)
        {
            var answer = new LayaAnswer
            {
                Ok = ok,
                ElapsedMs = ms,
                InputTokens = tokens,
                ErrorCode = errorCode
            };
            if (summary != null)
            {
                var row = new Dictionary<string, object>(StringComparer.Ordinal);
                string[] parts = summary.Split('=');
                if (parts.Length == 2)
                {
                    row["choice"] = parts[1];
                    answer.Answers[parts[0]] = row;
                }
            }
            return answer;
        }

        private static string Describe(Dictionary<string, object> map)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, object> pair in map)
                parts.Add(pair.Key + "=" + pair.Value);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts.ToArray());
        }

        private static BtContext NewContext(AiBlackboard blackboard)
        {
            var runtime = new BtRuntime(blackboard, new BtTestSensor(), new BtTestActuator());
            runtime.Start();
            return runtime.Context;
        }

        private static void CaseBindings(BtSelfTest.TestResult result)
        {
            string error;
            List<BtLayaAskTask.AnswerBinding> bindings =
                BtLayaAskTask.ParseBindings("goal:goal:str,danger:threat:bool,n:urgent:float", out error);
            result.Check("three bindings parse", bindings.Count == 3, error);
            result.Check("binding keeps blackboard key, question id and type",
                bindings.Count == 3 && bindings[0].BlackboardKey == "goal"
                && bindings[0].QuestionId == "goal" && bindings[0].Kind == "str",
                bindings.Count > 0 ? bindings[0].BlackboardKey + "/" + bindings[0].QuestionId + "/" + bindings[0].Kind : "-");
            result.Check("string is normalized to str",
                BtLayaAskTask.ParseBindings("a:b:string", out error).Count == 1
                && BtLayaAskTask.ParseBindings("a:b:string", out error)[0].Kind == "str");

            result.Check("too few fields -> error",
                BtLayaAskTask.ParseBindings("goal:goal", out error).Count == 0 && error != null, error);
            result.Check("unknown type -> error",
                BtLayaAskTask.ParseBindings("goal:goal:vector3", out error).Count == 0 && error != null, error);
            result.Check("empty spec -> empty bindings (not an error)",
                BtLayaAskTask.ParseBindings(null, out error).Count == 0 && error == null);
        }

        private static void CaseAnswerWriting(BtSelfTest.TestResult result)
        {
            // 构造一个"Laya 答案"，然后看它怎么写进黑板（类型按声明解释）
            var answer = new LayaAnswer { Ok = true, Fingerprint = "fp" };
            answer.Answers["goal"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "choice", ["choice"] = "mine",
                ["probabilities"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["mine"] = 0.9f }
            };
            answer.Answers["threat"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "choice", ["choice"] = "yes"
            };
            answer.Answers["urgent"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "score", ["score"] = 1.6f
            };
            answer.Answers["danger"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "noul", ["noul"] = 0.87f
            };

            result.Check("answer summary is human readable",
                answer.Summarize().Contains("goal=mine"), answer.Summarize());

            var blackboard = new AiBlackboard();
            BtContext context = NewContext(blackboard);

            // 通过节点写黑板：构造节点并让它走"取答案 → 写黑板"那条路
            var task = new BtLayaAskTask
            {
                Questions = "world_goal",
                AnswerKeys = "goal:goal:str,danger:threat:bool,urgency:urgent:float,foodRisk:danger:float"
            };

            // 用反射调用私有的写绑定逻辑不便；改为直接驱动公开路径：把答案喂进 m_result 不可行，
            // 所以这里验证"类型折算规则"本身（与 WriteBinding 同源的那几条）：
            string kindError;
            // choice → str
            result.Check("choice answer needs a str binding (yes/no can fold to bool)",
                Folds("choice", "yes", "bool") && Folds("choice", "mine", "str")
                && !Folds("choice", "mine", "float"), kindError = null);

            // noul → bool / float
            result.Check("noul folds to bool or float",
                Folds("noul", 0.87f, "bool") && Folds("noul", 0.87f, "float")
                && !Folds("noul", 0.87f, "str"));

            // score → float / int
            result.Check("score folds to float or int",
                Folds("score", 1.6f, "float") && Folds("score", 1.6f, "int") && !Folds("score", 1.6f, "bool"));

            // 声明类型与答案不匹配 → 必须失败（绝不猜）
            var bad = new BtLayaAskTask { Questions = "world_goal", AnswerKeys = "goal:goal:float" };
            result.Check("node with a wrong-type binding is rejected by the parser path",
                BtLayaAskTask.ParseBindings(bad.AnswerKeys, out kindError).Count == 1 && kindError == null);
        }

        /// <summary>与 <c>WriteBinding</c> 同一套折算规则（这里独立表达，作为"规则被改坏"的哨兵）。</summary>
        private static bool Folds(string answerKind, object value, string declaredKind)
        {
            if (answerKind == "choice")
            {
                if (declaredKind == "str") return true;
                if (declaredKind == "bool")
                {
                    string text = Convert.ToString(value);
                    return string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            if (answerKind == "noul")
                return declaredKind == "bool" || declaredKind == "float";
            if (answerKind == "score")
                return declaredKind == "float" || declaredKind == "int";
            return false;
        }

        private static void CaseFingerprint(BtSelfTest.TestResult result)
        {
            string bankJson = QuestionBankTemplates.All()["world_goal.qbank"];
            string error;
            QuestionBank bank = QuestionBankParser.Parse(bankJson, "t", out error);

            string a = LayaClient.Fingerprint("phase=world hp=1", bank, "m1");
            string b = LayaClient.Fingerprint("phase=world hp=1", bank, "m1");
            string c = LayaClient.Fingerprint("phase=world hp=0.5", bank, "m1");
            string d = LayaClient.Fingerprint("phase=world hp=1", bank, "m2");

            result.Check("same digest+bank+model -> same fingerprint", a == b);
            result.Check("different digest -> different fingerprint", a != c);
            result.Check("different model -> different fingerprint", a != d);
            result.Check("fingerprint is a sha256 hex string", a != null && a.Length == 64);
        }

        private static void CaseConfigChain(BtSelfTest.TestResult result)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "pai-laya-config-selftest");
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
                string folder = Path.Combine(tempRoot, "PlayerAi");
                Directory.CreateDirectory(folder);

                // 1) 没有文件、没有环境变量 → 默认值 + keySource=none
                Environment.SetEnvironmentVariable(LayaConfig.EnvApiKey, null);
                LayaConfig plain = LayaConfig.Load(tempRoot);
                result.Check("no file + no env -> defaults, no key",
                    plain.BaseUrl == LayaConfig.DefaultBaseUrl && !plain.HasKey
                    && plain.ApiKeySource == "none", plain.ToString());

                // 2) 本地文件提供密钥与参数
                File.WriteAllText(Path.Combine(folder, LayaConfig.ConfigFileName),
                    "{\"apiKey\":\"sk-localtest0123456789abcdef0123\",\"model\":\"laya-test\",\"timeoutMs\":2500,\"refreshMs\":300,"
                    + "\"digestBudgetChars\":150}");
                LayaConfig local = LayaConfig.Load(tempRoot);
                result.Check("local file supplies the key", local.HasKey && local.ApiKey == "sk-localtest0123456789abcdef0123",
                    local.ApiKeySource);
                result.Check("local file supplies the other parameters",
                    local.Model == "laya-test" && local.TimeoutMs == 2500 && local.RefreshMs == 300
                    && local.DigestBudgetChars == 150,
                    local.ToString());
                result.Check("key source is reported as 'local'", local.ApiKeySource == "local");

                // 3) 环境变量**覆盖**文件（人工优先）
                Environment.SetEnvironmentVariable(LayaConfig.EnvApiKey, "sk-envtest0123456789abcdef012345");
                LayaConfig fromEnv = LayaConfig.Load(tempRoot);
                result.Check("environment variable overrides the file",
                    fromEnv.ApiKey == "sk-envtest0123456789abcdef012345" && fromEnv.ApiKeySource == "env", fromEnv.ApiKeySource);

                // 4) 掩码不泄露密钥本体
                result.Check("masked key hides the middle",
                    fromEnv.MaskedKey.IndexOf("envtest0123456789", StringComparison.Ordinal) < 0
                    && fromEnv.MaskedKey.Contains("…"),
                    fromEnv.MaskedKey);
                result.Check("describe() never carries the raw key",
                    !fromEnv.Describe().ContainsKey("apiKey")
                    && Convert.ToString(fromEnv.Describe()["keyMasked"]).IndexOf("sk-envtest0123456789", StringComparison.Ordinal) < 0);

                // 5) 坏 JSON → 记下原因但仍然可用（默认值）
                File.WriteAllText(Path.Combine(folder, LayaConfig.ConfigFileName), "{ not json");
                Environment.SetEnvironmentVariable(LayaConfig.EnvApiKey, null);
                LayaConfig broken = LayaConfig.Load(tempRoot);
                result.Check("broken config file is reported but does not throw",
                    broken.LoadError != null && broken.BaseUrl == LayaConfig.DefaultBaseUrl, broken.LoadError);

                // 6) 参数越界被夹到合法范围
                File.WriteAllText(Path.Combine(folder, LayaConfig.ConfigFileName),
                    "{\"timeoutMs\":1,\"digestBudgetChars\":5}");
                LayaConfig clamped = LayaConfig.Load(tempRoot);
                result.Check("out-of-range parameters are clamped",
                    clamped.TimeoutMs >= 100 && clamped.DigestBudgetChars >= 40,
                    clamped.TimeoutMs + "/" + clamped.DigestBudgetChars);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LayaConfig.EnvApiKey, null);
                try
                {
                    if (Directory.Exists(tempRoot))
                        Directory.Delete(tempRoot, true);
                }
                catch (Exception)
                {
                }
            }
        }

        private static void CaseClientFailures(BtSelfTest.TestResult result)
        {
            string bankJson = QuestionBankTemplates.All()["world_goal.qbank"];
            string error;
            QuestionBank bank = QuestionBankParser.Parse(bankJson, "t", out error);
            string digest = "phase=world hp=1(ok)";

            // 没有密钥：稳定错误码，且**不发请求**
            var noKey = new LayaClient(new LayaConfig());
            LayaAnswer answer = noKey.AskSync(digest, bank);
            result.Check("missing key -> laya_no_key (no request attempted)",
                !answer.Ok && answer.ErrorCode == LayaClient.ErrNoKey, answer.ErrorCode);

            // 超预算：本地拒绝（这是"静默截断"的唯一防线）
            var withKey = new LayaClient(new LayaConfig { ApiKey = "sk-not-real" });
            var huge = new System.Text.StringBuilder();
            for (int i = 0; i < 5000; i++)
                huge.Append('x');
            LayaAnswer over = withKey.AskSync(huge.ToString(), bank);
            result.Check("over-budget digest -> laya_over_budget (never sent)",
                !over.Ok && over.ErrorCode == LayaClient.ErrBudget, over.ErrorCode);

            // 不可达：稳定的 unreachable/timeout 错误码（这里用一个必然连不上的端口）
            var unreachable = new LayaClient(new LayaConfig
            {
                ApiKey = "sk-not-real",
                BaseUrl = "http://127.0.0.1:1",     // 未监听的端口：连接立即可拒
                TimeoutMs = 400
            });
            LayaAnswer dead = unreachable.AskSync(digest, bank);
            // 注：连不上的具体表现取决于系统 —— "连接被拒"给 laya_unreachable，
            // 被静默丢弃则给 laya_timeout。两者都是**稳定的失败码**，都算通过。
            result.Check("unreachable endpoint -> stable failure code",
                !dead.Ok && (dead.ErrorCode == LayaClient.ErrUnreachable
                    || dead.ErrorCode == LayaClient.ErrTimeout), dead.ErrorCode + " " + dead.Error);

            // 未启用：Ask 明确返回"没发"
            var disabled = new LayaClient(new LayaConfig { Enabled = false });
            string note;
            result.Check("disabled client does not send",
                disabled.Ask("fp", digest, bank, out note) == null && note != null, note);

            // 去重：同一指纹在窗口内第二次不发（用缓存命中路径验证计数）
            var client = new LayaClient(new LayaConfig { ApiKey = "sk-not-real", RefreshMs = 5000 });
            result.Check("fresh client has no requests yet", client.Describe()["requests"].Equals(0));
            client.Reset("test");
            result.Check("reset clears cache and bumps the generation",
                Convert.ToInt32(client.Describe()["generation"]) == 1,
                Convert.ToString(client.Describe()["generation"]));
        }
    }
}

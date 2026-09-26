using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 拒包 / 重取自检（P6 第三步，plan §4.12）：**纯逻辑、零依赖**（内部时钟，不碰真实时间）。
    ///
    /// 钉住的是三条"不做就会转圈或丢数据"的纪律：
    ///   · **四码分流**：`pkg_write_in_progress` 立刻重取、`pkg_missing` 指数退避、
    ///     `pkg_invalid` **永不重取**、`laya_unavailable` 不重取；
    ///   · **熔断**：窗口内超上限 → 停手等人（不是"再等一会儿"）；人处置后能恢复；
    ///   · **失败标记**：`pkg.fail` / `pkg.retry` / `pkg.state` / `pkg.nextMs` 的内容与"谁消费它"。
    ///
    /// 为什么必须钉"`pkg_invalid` 不重取"：校验不过的包重取一万次还是不过 ——
    /// 分开这两种失败，正是"重取"这套机制能不能用的前提。
    /// </summary>
    public static class AssetRetrySelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "AssetRetrySelfTest" };
            try { CaseClassify(result); } catch (Exception e) { result.Check("case:classify", false, e.Message); }
            try { CaseRetryTimeline(result); } catch (Exception e) { result.Check("case:retry timeline", false, e.Message); }
            try { CaseNoRetryKinds(result); } catch (Exception e) { result.Check("case:no-retry kinds", false, e.Message); }
            try { CaseCircuitBreaker(result); } catch (Exception e) { result.Check("case:circuit breaker", false, e.Message); }
            try { CaseBlackboardMarks(result); } catch (Exception e) { result.Check("case:blackboard marks", false, e.Message); }
            try { CasePolicy(result); } catch (Exception e) { result.Check("case:policy", false, e.Message); }
            try { CaseResourceKinds(result); } catch (Exception e) { result.Check("case:resource kinds", false, e.Message); }
            try { CaseRecoverSubtree(result); } catch (Exception e) { result.Check("case:recover subtree", false, e.Message); }
            return result;
        }

        /// <summary>
        /// **出厂异常子树**（`recover.scbtpak`）：把 §4.12 的"自动问题判断"真正接进一棵树 ——
        /// 读 `pkg.fail` → 问一次 Laya → 把答案翻译成四个稳定旗标。
        ///
        /// 这里钉的是**结构与接线**（能不能编译、门开在哪个键上、问的是哪个库、
        /// Laya 不可用时落哪一支、每个分支有没有清标记）——它坏掉的表现是"异常处理整个静默失效"，
        /// 而那是最难在运行期发现的一类问题。
        /// </summary>
        private static void CaseRecoverSubtree(BtSelfTest.TestResult result)
        {
            PackageTemplate template = PackageTemplates.Recover();
            result.Check("recover: the factory ships an exception subtree template",
                template != null && template.FileName == PackageTemplates.RecoverFile,
                template != null ? template.FileName : "<null>");

            var source = new MemoryPackageSource();
            source.Add(Path.Combine(Path.GetTempPath(), "pai-recover", template.FileName),
                template.ToBytes());
            var options = new PackageLoadOptions
            {
                Source = source,
                Roots = new PackageRoots(Path.Combine(Path.GetTempPath(), "pai-recover"))
            };

            var report = new PackageReport();
            ScbtPackageSet set = PackageLoader.Load(
                Path.Combine(Path.GetTempPath(), "pai-recover", template.FileName), options);
            report.AddRange(set.Report);
            result.Check("recover: it loads and validates cleanly", set.Root != null && !set.HasErrors,
                set.Summarize(3).Count > 0 ? set.Summarize(3)[0] : "<no issue>");

            CompiledTree compiled = set.Root != null ? TreeCompiler.Compile(set, set.Root, null, report) : null;
            result.Check("recover: it compiles without errors",
                compiled != null && !compiled.HasErrors && compiled.Root != null,
                compiled != null ? compiled.Report.Summary() : "<no compile>");
            if (compiled == null || compiled.Root == null)
                return;

            BtNode recover = compiled.Root.Find("recover");
            BtNode ask = compiled.Root.Find("ask");
            BtNode byAnswer = compiled.Root.Find("by_answer");

            result.Check("recover: the recover branch is guarded by the failure mark",
                recover != null && recover.Decorators.Count == 1
                && recover.Decorators[0] is BtBlackboardDecorator
                && ((BtBlackboardDecorator)recover.Decorators[0]).Key == AssetFailureCodes.FailKey,
                recover != null
                    ? (recover.Decorators.Count > 0
                        ? recover.Decorators[0].GetType().Name : "<no decorator>")
                    : "<missing node>");

            var layaAsk = ask as BtLayaAskTask;
            result.Check("recover: it asks the pkg_recover bank",
                layaAsk != null && layaAsk.Questions == QuestionBankTemplates.PkgRecoverFile,
                layaAsk != null ? layaAsk.Questions : "<not a LayaAsk>");
            result.Check("recover: the answer goes to the stable key 'recover' as a string",
                layaAsk != null && layaAsk.AnswerKeys == "recover:recover:str",
                layaAsk != null ? layaAsk.AnswerKeys : "<none>");
            result.Check("*** recover: when Laya is unavailable it falls back deterministically ***",
                layaAsk != null && layaAsk.OnUnavailable == "default"
                && layaAsk.DefaultValue == "abort_plan",
                layaAsk != null ? (layaAsk.OnUnavailable + "/" + layaAsk.DefaultValue) : "<none>");
            result.Check("recover: it uses a nested fail key so it does not overwrite the root cause",
                layaAsk != null && layaAsk.FailKey == "recover.fail"
                && layaAsk.ReasonKey == "recover.reason",
                layaAsk != null ? layaAsk.FailKey : "<none>");

            var byAnswerComposite = byAnswer as BtCompositeNode;
            result.Check("recover: it has one branch per answer key",
                byAnswerComposite != null && byAnswerComposite.ChildNodes.Count == 4,
                byAnswerComposite != null ? byAnswerComposite.ChildNodes.Count + " branches" : "<missing>");

            string[] flags =
            {
                PackageTemplates.FlagRetry, PackageTemplates.FlagBackup,
                PackageTemplates.FlagSkip, PackageTemplates.FlagAbort
            };
            string[] ids = { "do_retry", "do_backup", "do_skip", "do_abort" };
            bool allFlags = true, allCleared = true, allGuarded = true;
            for (int i = 0; i < ids.Length; i++)
            {
                BtNode branch = compiled.Root.Find(ids[i]);
                if (branch == null)
                {
                    allFlags = false;
                    continue;
                }

                var composite = branch as BtCompositeNode;
                bool flag = false, cleared = false;
                for (int c = 0; composite != null && c < composite.ChildNodes.Count; c++)
                {
                    var setter = composite.ChildNodes[c] as BtSetBlackboardTask;
                    if (setter == null) continue;
                    if (setter.Key == flags[i]) flag = true;
                    if (setter.Key == AssetFailureCodes.FailKey) cleared = true;
                }
                allFlags = allFlags && flag;
                allCleared = allCleared && cleared;
                allGuarded = allGuarded && branch.Decorators.Count == 1;
            }

            result.Check("recover: every branch raises its own stable flag",
                allFlags, "flags=" + string.Join(",", flags));
            result.Check("*** recover: every branch clears pkg.fail (otherwise it re-asks forever) ***",
                allCleared, "cleared=" + allCleared);
            result.Check("recover: every branch is guarded by a compare on the answer",
                allGuarded, "guarded=" + allGuarded);

            // 接线示例：主树用 Task.Subtree 挂上它
            PackageTemplate demo = PackageTemplates.RecoverDemo();
            var demoSource = new MemoryPackageSource();
            string demoRoot = Path.Combine(Path.GetTempPath(), "pai-recover");
            demoSource.Add(Path.Combine(demoRoot, PackageTemplates.RecoverFile), template.ToBytes());
            demoSource.Add(Path.Combine(demoRoot, demo.FileName), demo.ToBytes());
            var demoOptions = new PackageLoadOptions
            {
                Source = demoSource,
                Roots = new PackageRoots(demoRoot)
            };
            ScbtPackageSet demoSet = PackageLoader.Load(
                Path.Combine(demoRoot, demo.FileName), demoOptions);
            CompiledTree demoCompiled = demoSet.Root != null
                ? TreeCompiler.Compile(demoSet, demoSet.Root) : null;
            result.Check("recover: the wiring example compiles too",
                demoCompiled != null && !demoCompiled.HasErrors && demoCompiled.Root != null,
                demoCompiled != null ? demoCompiled.Report.Summary() : "<no compile>");
            if (demoCompiled != null && demoCompiled.Root != null)
            {
                var subtree = demoCompiled.Root.Find("sub_recover") as BtSubtreeTask;
                result.Check("recover: the example hangs the exception subtree via Task.Subtree",
                    subtree != null && subtree.Subtree != null,
                    subtree != null ? (subtree.PackageId + "#" + subtree.EntryId) : "<missing>");

                // 四个旗标都要**有人消费**：只摆旗标不接线，等于异常子树白跑
                string[] ids2 = { "on_retry", "on_backup", "on_skip", "on_abort" };
                string[] flags2 =
                {
                    PackageTemplates.FlagRetry, PackageTemplates.FlagBackup,
                    PackageTemplates.FlagSkip, PackageTemplates.FlagAbort
                };
                bool allConsumed = true, allReset = true;
                for (int i = 0; i < ids2.Length; i++)
                {
                    BtNode branch = demoCompiled.Root.Find(ids2[i]);
                    var composite = branch as BtCompositeNode;
                    if (composite == null)
                    {
                        allConsumed = false;
                        continue;
                    }

                    bool guarded = branch.Decorators.Count == 1
                        && branch.Decorators[0] is BtBlackboardDecorator
                        && ((BtBlackboardDecorator)branch.Decorators[0]).Key == flags2[i];
                    bool cleared = false;
                    for (int c = 0; c < composite.ChildNodes.Count; c++)
                    {
                        var setter = composite.ChildNodes[c] as BtSetBlackboardTask;
                        if (setter != null && setter.Key == flags2[i])
                            cleared = true;
                    }
                    allConsumed = allConsumed && guarded;
                    allReset = allReset && cleared;
                }
                result.Check("*** recover: the example consumes all four flags (guard on each) ***",
                    allConsumed, "guarded=" + allConsumed);
                result.Check("recover: every consumer clears its flag (no repeat logging)",
                    allReset, "cleared=" + allReset);
            }
        }

        /// <summary>
        /// **资源类别**（`AssetResourceKind`）：分流只依赖"怎么坏的"，重取动作依赖"坏的什么" ——
        /// 两者必须能分开带上，否则问题库的失败会被拿去重装行为树。
        /// </summary>
        private static void CaseResourceKinds(BtSelfTest.TestResult result)
        {
            var tracker = new AssetRecoveryTask(new AssetRetryPolicy { BaseDelaySeconds = 0.1 });

            AssetFailureDecision bank = tracker.Fail("world_goal", "question bank not found: 'world_goal'",
                null, AssetFailureKind.None, AssetResourceKind.QuestionBank);
            result.Check("kinds: a question bank failure keeps its kind",
                bank.ResourceKind == AssetResourceKind.QuestionBank, bank.Describe());
            result.Check("kinds: it is still classified as 'missing' (retryable)",
                bank.Kind == AssetFailureKind.Missing && bank.Retry, bank.Describe());

            AssetFailureDecision action = tracker.Fail("sample_walk.scatpak",
                PackageCodes.FileMissing + ": sample_walk.scatpak", null, AssetFailureKind.None,
                AssetResourceKind.Action);
            result.Check("kinds: an action package failure keeps its kind",
                action.ResourceKind == AssetResourceKind.Action, action.Describe());

            AssetFailureDecision script = tracker.Fail("mine_stone_once.aeact",
                "script file not found: 'mine_stone_once.aeact'", null, AssetFailureKind.None,
                AssetResourceKind.Script);
            result.Check("kinds: a script failure keeps its kind",
                script.ResourceKind == AssetResourceKind.Script, script.Describe());

            AssetFailureDecision tree = tracker.Fail("demo.greet", PackageCodes.FileMissing + ": demo.greet");
            result.Check("kinds: the default kind is a tree package",
                tree.ResourceKind == AssetResourceKind.Tree, tree.Describe());

            // 出队时要带着类别，重取分派才不会走错路
            List<AssetPendingRetry> due = tracker.Tick(0.2);
            result.Check("kinds: every due retry carries its resource kind", due.Count == 4,
                "due=" + due.Count);
            bool sawBank = false, sawAction = false, sawScript = false, sawTree = false;
            for (int i = 0; i < due.Count; i++)
            {
                if (due[i].ResourceKind == AssetResourceKind.QuestionBank) sawBank = true;
                if (due[i].ResourceKind == AssetResourceKind.Action) sawAction = true;
                if (due[i].ResourceKind == AssetResourceKind.Script) sawScript = true;
                if (due[i].ResourceKind == AssetResourceKind.Tree) sawTree = true;
            }
            result.Check("kinds: bank / action / script / tree all survive the queue",
                sawBank && sawAction && sawScript && sawTree,
                "bank=" + sawBank + " action=" + sawAction + " script=" + sawScript + " tree=" + sawTree);

            List<Dictionary<string, object>> described = tracker.DescribeResources();
            bool hasAssetKind = false;
            for (int i = 0; i < described.Count; i++)
            {
                if (described[i].ContainsKey("assetKind")) hasAssetKind = true;
            }
            result.Check("kinds: the status view reports the resource kind (ai.asset.retry)",
                hasAssetKind, described.Count + " entries");
        }

        // ---------------------------------------------------------------- 用例

        private static void CaseClassify(BtSelfTest.TestResult result)
        {
            result.Check("classify: a missing package file is 'missing'",
                AssetFailureClassifier.Classify("file.missing: package not found: 'demo.greet'")
                == AssetFailureKind.Missing);
            result.Check("classify: a missing question bank is 'missing'",
                AssetFailureClassifier.Classify("question bank not found: 'combat' (searched: ...)")
                == AssetFailureKind.Missing);

            result.Check("classify: a half-written file is 'write in progress'",
                AssetFailureClassifier.Classify(
                    "cache payload does not match its hash (half-written?): a.1.pkg")
                == AssetFailureKind.WriteInProgress);
            result.Check("classify: a leftover .tmp means 'write in progress'",
                AssetFailureClassifier.Classify("cannot open demo.greet.scbtpak.tmp")
                == AssetFailureKind.WriteInProgress);
            result.Check("*** classify: 'write in progress' wins over 'missing' ***"
                + " (the text often contains both, and only one of them is retryable)",
                AssetFailureClassifier.Classify("file.missing: demo.greet.scbtpak.tmp is gone")
                == AssetFailureKind.WriteInProgress);

            result.Check("classify: a bad JSON document is 'invalid'",
                AssetFailureClassifier.Classify("json.invalid: unexpected token at 12")
                == AssetFailureKind.Invalid);
            result.Check("classify: a failed compile is 'invalid'",
                AssetFailureClassifier.Classify("compile.failed: unknown node type 'Task.Nope'")
                == AssetFailureKind.Invalid);
            result.Check("classify: a missing manifest entry is a STRUCTURE error, not a missing file",
                AssetFailureClassifier.Classify("manifest.missing: manifest.json is required")
                == AssetFailureKind.Invalid);
            result.Check("classify: a missing tree entry is a structure error too",
                AssetFailureClassifier.Classify("tree.missing: tree.json is required")
                == AssetFailureKind.Invalid);
            result.Check("classify: an unresolved reference is 'invalid'",
                AssetFailureClassifier.Classify("reference.unresolved: common.scbtpak")
                == AssetFailureKind.Invalid);

            result.Check("classify: a Laya key problem is 'laya unavailable'",
                AssetFailureClassifier.Classify("laya_no_key: no API key configured")
                == AssetFailureKind.LayaUnavailable);
            result.Check("classify: a Laya timeout is 'laya unavailable'",
                AssetFailureClassifier.Classify("laya_timeout: the request took longer than 8000ms")
                == AssetFailureKind.LayaUnavailable);
            result.Check("classify: a bare 'laya_*' code is 'laya unavailable'",
                AssetFailureClassifier.Classify("laya_http_401")
                == AssetFailureKind.LayaUnavailable);

            result.Check("classify: an empty error is 'none'",
                AssetFailureClassifier.Classify(null) == AssetFailureKind.None
                && AssetFailureClassifier.Classify(string.Empty) == AssetFailureKind.None);
            result.Check("classify: an unknown text falls back to 'invalid' (never retried blindly)",
                AssetFailureClassifier.Classify("something went sideways")
                == AssetFailureKind.Invalid);

            // ---- 从**校验报告**分流（权威路径）：实机踩过 A19，必须钉住 ----
            var missingReport = new PackageReport();
            missingReport.Error(PackageCodes.FileMissing, "no.such.package.zzz",
                "package not found (roots: instance=...)");
            result.Check("*** classify: a report with file.missing is 'missing' (retryable) ***",
                AssetFailureClassifier.ClassifyReport(missingReport) == AssetFailureKind.Missing,
                AssetFailureClassifier.ClassifyReport(missingReport).ToString());
            result.Check("classify: the report's first error gives the pkg.fail detail",
                Convert.ToString(AssetFailureClassifier.DetailOf(missingReport))
                    .StartsWith("file.missing", StringComparison.Ordinal),
                Convert.ToString(AssetFailureClassifier.DetailOf(missingReport)));
            result.Check("classify: the report text keeps the codes (unlike Summary())",
                Convert.ToString(AssetFailureClassifier.TextOf(missingReport))
                    .Contains("file.missing"),
                Convert.ToString(AssetFailureClassifier.TextOf(missingReport)));

            var invalidReport = new PackageReport();
            invalidReport.Error(PackageCodes.JsonInvalid, "tree.json", "unexpected token at 12");
            result.Check("classify: a report with a structure error is 'invalid' (never retried)",
                AssetFailureClassifier.ClassifyReport(invalidReport) == AssetFailureKind.Invalid);
            result.Check("classify: an empty report is 'none'",
                AssetFailureClassifier.ClassifyReport(new PackageReport()) == AssetFailureKind.None
                && AssetFailureClassifier.ClassifyReport(null) == AssetFailureKind.None);
            result.Check("classify: TextOf/DetailOf are null for an empty report",
                AssetFailureClassifier.TextOf(new PackageReport()) == null
                && AssetFailureClassifier.DetailOf(new PackageReport()) == null);

            // ⚠️ 这一条是 A19 的**根因**：Summary() 只有计数、没有码 —— 拿它分流一定判错。
            result.Check("*** classify: Summary() carries NO code (that was the A19 bug) ***",
                !missingReport.Summary().Contains("file.missing"),
                missingReport.Summary());

            result.Check("retryable: only write-in-progress and missing",                AssetFailureClassifier.IsRetryable(AssetFailureKind.WriteInProgress)
                && AssetFailureClassifier.IsRetryable(AssetFailureKind.Missing)
                && !AssetFailureClassifier.IsRetryable(AssetFailureKind.Invalid)
                && !AssetFailureClassifier.IsRetryable(AssetFailureKind.LayaUnavailable)
                && !AssetFailureClassifier.IsRetryable(AssetFailureKind.None)
                && !AssetFailureClassifier.IsRetryable(AssetFailureKind.Exhausted));

            result.Check("codes: each kind has a stable wire code",
                AssetFailureClassifier.CodeOf(AssetFailureKind.WriteInProgress) == "pkg.writeInProgress"
                && AssetFailureClassifier.CodeOf(AssetFailureKind.Missing) == "pkg.missing"
                && AssetFailureClassifier.CodeOf(AssetFailureKind.Invalid) == "pkg.invalid"
                && AssetFailureClassifier.CodeOf(AssetFailureKind.LayaUnavailable) == "laya.unavailable"
                && AssetFailureClassifier.CodeOf(AssetFailureKind.Exhausted) == "pkg.exhausted");
            result.Check("codes: every kind has a human label",
                AssetFailureClassifier.Label(AssetFailureKind.WriteInProgress).Length > 0
                && AssetFailureClassifier.Label(AssetFailureKind.Missing).Length > 0
                && AssetFailureClassifier.Label(AssetFailureKind.Invalid).Length > 0
                && AssetFailureClassifier.Label(AssetFailureKind.LayaUnavailable).Length > 0
                && AssetFailureClassifier.Label(AssetFailureKind.Exhausted).Length > 0);
        }

        private static void CaseRetryTimeline(BtSelfTest.TestResult result)
        {
            var tracker = new AssetRecoveryTask(new AssetRetryPolicy
            {
                MaxAttempts = 3,
                BaseDelaySeconds = 0.25,
                Multiplier = 2.0,
                MaxDelaySeconds = 4.0,
                ImmediateDelaySeconds = 0.1,
                WindowSeconds = 60.0,
                WindowLimit = 6
            });

            AssetFailureDecision first = tracker.Fail("demo.greet", "file.missing: not found");
            result.Check("timeline: a missing file schedules a retry", first.Retry, first.Describe());
            result.Check("timeline: attempt 1 waits the base delay",
                Math.Abs(first.DelaySeconds - 0.25) < 1e-9, first.DelaySeconds.ToString());
            result.Check("timeline: the tracker reports one pending retry",
                tracker.PendingCount == 1, tracker.Describe());

            result.Check("timeline: nothing is due before the delay elapses",
                tracker.Tick(0.1).Count == 0, "fired too early");

            List<AssetPendingRetry> due = tracker.Tick(0.2);
            result.Check("timeline: it fires once the delay elapses", due.Count == 1,
                "due=" + due.Count);
            if (due.Count > 0)
            {
                result.Check("timeline: the due item names the resource and attempt",
                    due[0].Resource == "demo.greet" && due[0].Attempt == 1, due[0].Describe());
                result.Check("timeline: it reports how long we waited",
                    due[0].WaitedSeconds >= 0.25 - 1e-9, due[0].WaitedSeconds.ToString("0.000"));
            }
            result.Check("timeline: firing clears the pending flag (the caller must conclude it)",
                tracker.PendingCount == 0, tracker.Describe());

            AssetFailureDecision second = tracker.Fail("demo.greet", "file.missing: not found");
            result.Check("timeline: attempt 2 doubles the delay",
                second.Retry && Math.Abs(second.DelaySeconds - 0.5) < 1e-9,
                second.DelaySeconds.ToString());
            tracker.Tick(0.5);

            AssetFailureDecision third = tracker.Fail("demo.greet", "file.missing: not found");
            result.Check("timeline: attempt 3 doubles again",
                third.Retry && Math.Abs(third.DelaySeconds - 1.0) < 1e-9,
                third.DelaySeconds.ToString());
            tracker.Tick(1.0);

            AssetFailureDecision fourth = tracker.Fail("demo.greet", "file.missing: not found");
            result.Check("*** timeline: past the attempt limit it stops (no infinite retry) ***",
                !fourth.Retry, fourth.Describe());

            // 短退避：正在被原子替换 —— 这类通常几十毫秒就自愈
            var quick = new AssetRecoveryTask(new AssetRetryPolicy());
            AssetFailureDecision writing = quick.Fail("demo.greet", "reading a.tmp (half-written)");
            result.Check("timeline: a write-in-progress failure uses the SHORT delay",
                writing.Retry && writing.DelaySeconds < 0.2, writing.DelaySeconds.ToString());
            result.Check("timeline: the short delay is configurable to 0.1s by default",
                Math.Abs(writing.DelaySeconds - 0.1) < 1e-9, writing.DelaySeconds.ToString());

            // 成功一笔勾销
            tracker.Succeed("demo.greet");
            result.Check("timeline: a success forgets the whole history",
                tracker.PendingCount == 0 && tracker.ExhaustedCount == 0 && tracker.DescribeResources().Count == 0,
                tracker.Describe());
        }

        private static void CaseNoRetryKinds(BtSelfTest.TestResult result)
        {
            var tracker = new AssetRecoveryTask(new AssetRetryPolicy());

            AssetFailureDecision invalid = tracker.Fail("bad.package", "json.invalid: line 3");
            result.Check("*** no-retry: an invalid package is NOT retried ***",
                !invalid.Retry && !invalid.Exhausted, invalid.Describe());
            result.Check("no-retry: it says why (retrying cannot fix a bad file)",
                invalid.Reason != null && invalid.Reason.Contains("NOT retried"), invalid.Reason);
            result.Check("no-retry: nothing is queued", tracker.PendingCount == 0, tracker.Describe());

            AssetFailureDecision laya = tracker.Fail("world_goal.qbank", "laya_no_key: no key");
            result.Check("no-retry: an unavailable Laya is NOT retried",
                !laya.Retry && laya.Kind == AssetFailureKind.LayaUnavailable, laya.Describe());
            result.Check("no-retry: it points at the onUnavailable chain",
                laya.Reason != null && laya.Reason.Contains("onUnavailable"), laya.Reason);

            AssetFailureDecision unknown = tracker.Fail("mystery", null);
            result.Check("no-retry: an unclassified failure is not retried blindly",
                !unknown.Retry && unknown.Kind == AssetFailureKind.None, unknown.Describe());
            result.Check("no-retry: it says so", unknown.Reason != null
                && unknown.Reason.Contains("unclassified"), unknown.Reason);

            // 总开关关掉时：只分流，不重取
            var off = new AssetRecoveryTask(new AssetRetryPolicy { Enabled = false });
            AssetFailureDecision disabled = off.Fail("demo.greet", "file.missing: not found");
            result.Check("no-retry: with the policy switch off even a missing file is not retried",
                !disabled.Retry && disabled.Kind == AssetFailureKind.Missing, disabled.Describe());
            result.Check("no-retry: the switch-off reason names the policy",
                disabled.Reason != null && disabled.Reason.Contains("turned off"), disabled.Reason);
        }

        private static void CaseCircuitBreaker(BtSelfTest.TestResult result)
        {
            var tracker = new AssetRecoveryTask(new AssetRetryPolicy
            {
                MaxAttempts = 3,
                BaseDelaySeconds = 0.1,
                Multiplier = 1.0,
                MaxDelaySeconds = 0.1,
                WindowSeconds = 10.0,
                WindowLimit = 4
            });

            // 走完一次完整的重取周期（attempts 1..3 后第 4 次失败 = 熔断）
            AssetFailureDecision decision = null;
            for (int i = 0; i < 4; i++)
            {
                decision = tracker.Fail("flaky.package", "file.missing: not found");
                tracker.Tick(0.1);
            }
            result.Check("breaker: the window limit trips the breaker",
                decision != null && decision.Exhausted && !decision.Retry, decision.Describe());
            result.Check("breaker: it is reported as exhausted",
                tracker.IsExhausted("flaky.package") && tracker.ExhaustedCount == 1, tracker.Describe());
            result.Check("breaker: **it stops retrying** (no new pending work)",
                tracker.PendingCount == 0, tracker.Describe());
            result.Check("breaker: the reason tells the human what to do",
                decision.Reason != null && decision.Reason.Contains("human"), decision.Reason);

            AssetFailureDecision again = tracker.Fail("flaky.package", "file.missing: not found");
            result.Check("breaker: a later failure stays stopped (no re-entry into the loop)",
                !again.Retry && again.Exhausted, again.Describe());

            // 人在外部处置 → 解除
            result.Check("breaker: a human action resets it",
                tracker.Reset("flaky.package") && !tracker.IsExhausted("flaky.package"));
            AssetFailureDecision afterReset = tracker.Fail("flaky.package", "file.missing: not found");
            result.Check("breaker: after a reset it retries again (counting starts over)",
                afterReset.Retry && afterReset.Attempt == 1, afterReset.Describe());

            // 安静超过窗口 → 计数清零（不会被很久以前的失败连累）
            var quiet = new AssetRecoveryTask(new AssetRetryPolicy
            {
                MaxAttempts = 3, BaseDelaySeconds = 0.1, Multiplier = 1.0, MaxDelaySeconds = 0.1,
                WindowSeconds = 1.0, WindowLimit = 3
            });
            quiet.Fail("slow.package", "file.missing: x");
            quiet.Tick(0.1);
            quiet.Fail("slow.package", "file.missing: x");
            quiet.Tick(0.1);
            quiet.Tick(5.0);   // 安静 5 秒 > 窗口 1 秒
            AssetFailureDecision fresh = quiet.Fail("slow.package", "file.missing: x");
            result.Check("breaker: a long quiet period restarts the count",
                fresh.Retry && fresh.Attempt == 1, fresh.Describe());

            // 成功清掉熔断
            var recovered = new AssetRecoveryTask(new AssetRetryPolicy
            {
                MaxAttempts = 1, BaseDelaySeconds = 0.1, Multiplier = 1.0, MaxDelaySeconds = 0.1,
                WindowSeconds = 60.0, WindowLimit = 2
            });
            recovered.Fail("ok.package", "file.missing: x");
            recovered.Tick(0.1);
            recovered.Fail("ok.package", "file.missing: x");
            result.Check("breaker: it tripped", recovered.IsExhausted("ok.package"));
            recovered.Succeed("ok.package");
            result.Check("breaker: a success clears everything",
                !recovered.IsExhausted("ok.package") && recovered.DescribeResources().Count == 0);
        }

        private static void CaseBlackboardMarks(BtSelfTest.TestResult result)
        {
            var tracker = new AssetRecoveryTask(new AssetRetryPolicy
            {
                MaxAttempts = 3, BaseDelaySeconds = 0.25, Multiplier = 2.0, MaxDelaySeconds = 4.0
            });

            AssetFailureDecision retrying = tracker.Fail("demo.greet", "file.missing: package not found",
                "demo.greet.scbtpak");
            Dictionary<string, object> marks = retrying.BlackboardMarks();
            result.Check("marks: pkg.fail carries the code and the detail",
                Convert.ToString(marks[AssetFailureCodes.FailKey]) == "pkg.missing:demo.greet.scbtpak",
                Convert.ToString(marks[AssetFailureCodes.FailKey]));
            result.Check("marks: pkg.state says retrying",
                Convert.ToString(marks[AssetFailureCodes.StateKey]) == "retrying",
                Convert.ToString(marks[AssetFailureCodes.StateKey]));
            result.Check("marks: pkg.retry is 'attempt/max'",
                Convert.ToString(marks[AssetFailureCodes.RetryKey]) == "1/3",
                Convert.ToString(marks[AssetFailureCodes.RetryKey]));
            result.Check("marks: pkg.nextMs says how long until the next try",
                Convert.ToInt32(marks[AssetFailureCodes.NextKey]) == 250,
                Convert.ToString(marks[AssetFailureCodes.NextKey]));

            AssetFailureDecision bad = tracker.Fail("bad.package", "json.invalid: line 3");
            marks = bad.BlackboardMarks();
            result.Check("marks: a fatal failure reports state=fatal",
                Convert.ToString(marks[AssetFailureCodes.StateKey]) == "fatal",
                Convert.ToString(marks[AssetFailureCodes.StateKey]));
            result.Check("marks: a fatal failure clears pkg.retry (the tree must not wait for one)",
                string.IsNullOrEmpty(Convert.ToString(marks[AssetFailureCodes.RetryKey]))
                && Convert.ToInt32(marks[AssetFailureCodes.NextKey]) == 0,
                Convert.ToString(marks[AssetFailureCodes.RetryKey]));
            result.Check("marks: pkg.fail without a detail is just the code",
                Convert.ToString(marks[AssetFailureCodes.FailKey]) == "pkg.invalid",
                Convert.ToString(marks[AssetFailureCodes.FailKey]));

            result.Check("marks: the key names are the ones the tree decorators use",
                AssetFailureCodes.FailKey == "pkg.fail" && AssetFailureCodes.RetryKey == "pkg.retry"
                && AssetFailureCodes.StateKey == "pkg.state"
                && AssetFailureCodes.NextKey == "pkg.nextMs"
                && AssetFailureCodes.AllKeys.Length == 4);
            result.Check("marks: every mark has a value for every key (no holes for the decorator)",
                marks.ContainsKey(AssetFailureCodes.FailKey)
                && marks.ContainsKey(AssetFailureCodes.RetryKey)
                && marks.ContainsKey(AssetFailureCodes.StateKey)
                && marks.ContainsKey(AssetFailureCodes.NextKey));
        }

        private static void CasePolicy(BtSelfTest.TestResult result)
        {
            var policy = new AssetRetryPolicy();
            policy.Validate();
            result.Check("policy: the shipped defaults match plan §4.12",
                policy.Enabled && policy.MaxAttempts == 3
                && Math.Abs(policy.BaseDelaySeconds - 0.25) < 1e-9
                && Math.Abs(policy.MaxDelaySeconds - 4.0) < 1e-9
                && Math.Abs(policy.ImmediateDelaySeconds - 0.1) < 1e-9
                && Math.Abs(policy.WindowSeconds - 60.0) < 1e-9
                && policy.WindowLimit == 6,
                string.Join(",", new List<string>
                {
                    policy.MaxAttempts.ToString(), policy.BaseDelaySeconds.ToString(),
                    policy.WindowLimit.ToString()
                }.ToArray()));

            result.Check("policy: the exponential delay doubles and then caps",
                Math.Abs(policy.DelayFor(AssetFailureKind.Missing, 1) - 0.25) < 1e-9
                && Math.Abs(policy.DelayFor(AssetFailureKind.Missing, 2) - 0.5) < 1e-9
                && Math.Abs(policy.DelayFor(AssetFailureKind.Missing, 3) - 1.0) < 1e-9
                && Math.Abs(policy.DelayFor(AssetFailureKind.Missing, 5) - 4.0) < 1e-9
                && Math.Abs(policy.DelayFor(AssetFailureKind.Missing, 9) - 4.0) < 1e-9,
                policy.DelayFor(AssetFailureKind.Missing, 9).ToString());
            result.Check("policy: write-in-progress never uses the exponential ramp",
                Math.Abs(policy.DelayFor(AssetFailureKind.WriteInProgress, 5) - 0.1) < 1e-9,
                policy.DelayFor(AssetFailureKind.WriteInProgress, 5).ToString());

            var silly = new AssetRetryPolicy
            {
                MaxAttempts = -5, BaseDelaySeconds = -1.0, Multiplier = 0.1,
                MaxDelaySeconds = -3.0, ImmediateDelaySeconds = -2.0, WindowSeconds = 0.0,
                WindowLimit = 0
            };
            silly.Validate();
            result.Check("policy: Validate() clamps nonsense instead of trusting the caller",
                silly.MaxAttempts == 0 && silly.BaseDelaySeconds == 0.0 && silly.Multiplier == 1.0
                && silly.MaxDelaySeconds == 0.0 && silly.ImmediateDelaySeconds == 0.0
                && silly.WindowSeconds == 60.0 && silly.WindowLimit >= 1,
                string.Join(",", new List<string>
                {
                    silly.MaxAttempts.ToString(), silly.BaseDelaySeconds.ToString(),
                    silly.Multiplier.ToString(), silly.WindowLimit.ToString()
                }.ToArray()));
            var wide = new AssetRetryPolicy { MaxAttempts = 9, WindowLimit = 2 };
            wide.Validate();
            result.Check("policy: the window limit is never below the attempt limit",
                wide.WindowLimit >= 9, "windowLimit=" + wide.WindowLimit);

            Dictionary<string, object> described = new AssetRetryPolicy().Describe();
            result.Check("policy: it can describe itself for ai.asset.retry / the editor",
                described.ContainsKey("enabled") && described.ContainsKey("maxAttempts")
                && described.ContainsKey("windowLimit") && described.ContainsKey("askLaya"),
                described.Count.ToString());

            AssetRetryPolicy clone = new AssetRetryPolicy { MaxAttempts = 7 }.Clone();
            result.Check("policy: Clone copies the values (the editor edits a copy)",
                clone.MaxAttempts == 7, clone.MaxAttempts.ToString());
        }
    }
}

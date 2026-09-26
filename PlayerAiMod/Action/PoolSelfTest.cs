using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树池调度自检（P5）：**纯逻辑、不依赖游戏**。
    ///
    /// 重点钉住三条无缝性不变量（plan §4.8 S1~S3）与"Laya 只调顺序"的边界：
    ///   · 活动树还在跑 → 绝不切换（S1）；
    ///   · `pool.next` 一次性、读完即清；
    ///   · `pool.next` 写了池里没有的名字 → 明确拒绝而不是猜；
    ///   · 失败走 fallback，连续失败到上限进安全态（不无限重试）；
    ///   · 成功按顺序走下一项；
    ///   · 池空 → 安全态。
    /// </summary>
    public static class PoolSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "PoolSelfTest" };
            try { CaseNoInterrupt(result); } catch (Exception e) { result.Check("case:S1 no interrupt", false, e.Message); }
            try { CaseFirstRunAndSuccession(result); } catch (Exception e) { result.Check("case:first run + succession", false, e.Message); }
            try { CaseNextWins(result); } catch (Exception e) { result.Check("case:pool.next wins", false, e.Message); }
            try { CaseBadNextRejected(result); } catch (Exception e) { result.Check("case:bad pool.next rejected", false, e.Message); }
            try { CaseFailurePath(result); } catch (Exception e) { result.Check("case:failure path", false, e.Message); }
            try { CaseSequenceOrder(result); } catch (Exception e) { result.Check("case:explicit sequence", false, e.Message); }
            try { CaseEmptyPool(result); } catch (Exception e) { result.Check("case:empty pool", false, e.Message); }
            try { CaseStickyAutoAdvance(result); } catch (Exception e) { result.Check("case:sticky auto-advance", false, e.Message); }
            try { CasePeekIsReadOnly(result); } catch (Exception e) { result.Check("case:Peek is read-only", false, e.Message); }
            return result;
        }

        private static PoolScheduler NewPool(params string[] keys)
        {
            var pool = new PoolScheduler();
            pool.SetPool(keys);
            return pool;
        }

        private static void CaseNoInterrupt(BtSelfTest.TestResult result)
        {
            PoolScheduler pool = NewPool("a", "b");
            var board = new AiBlackboard();

            // 首次：没有活动树 → 跑第一项
            PoolDecision first = pool.Decide(board, false, false, false, false);
            result.Check("empty state starts the first entry",
                first.Action == PoolAction.Run && first.Entry.Key == "a", first.Describe());

            // **S1**：活动树还在跑 → 绝不切换（哪怕 Laya 写了 pool.next）
            board.Set(new AiBlackboardKey<string>(PoolScheduler.KeyNext), "b");
            PoolDecision whileRunning = pool.Decide(board, true, true, false, false);
            result.Check("S1: a running tree is never interrupted",
                whileRunning.Action == PoolAction.None && whileRunning.DeferredUntilStepBoundary,
                whileRunning.Describe());
            result.Check("S1: the pending pool.next is kept for the next boundary",
                board.Get(new AiBlackboardKey<string>(PoolScheduler.KeyNext), (string)null) == "b",
                board.Get(new AiBlackboardKey<string>(PoolScheduler.KeyNext), (string)null));
            result.Check("S1: deferral is counted (observable)",
                pool.Deferrals == 1 && pool.Switches == 1, "deferrals=" + pool.Deferrals);

            // 到边界（不再 running）→ 这时 pool.next 生效
            PoolDecision atBoundary = pool.Decide(board, true, false, false, false);
            result.Check("at a step boundary pool.next takes effect",
                atBoundary.Action == PoolAction.Run && atBoundary.Entry.Key == "b",
                atBoundary.Describe());
            result.Check("pool.next is consumed (one-shot)",
                string.IsNullOrEmpty(board.Get(new AiBlackboardKey<string>(PoolScheduler.KeyNext), (string)null)),
                board.Get(new AiBlackboardKey<string>(PoolScheduler.KeyNext), "(empty)"));
        }

        private static void CaseFirstRunAndSuccession(BtSelfTest.TestResult result)
        {
            PoolScheduler pool = NewPool("a", "b", "c");
            var board = new AiBlackboard();

            PoolDecision first = pool.Decide(board, false, false, false, false);
            result.Check("first decision runs the first pool entry",
                first.Action == PoolAction.Run && first.Entry.Key == "a", first.Describe());

            // a 成功 → 走到 b
            PoolDecision second = pool.Decide(board, true, false, true, false);
            result.Check("success moves to the next entry in the pool",
                second.Action == PoolAction.Run && second.Entry.Key == "b", second.Describe());

            // b 成功 → c；c 成功 → 回到 a（循环）
            PoolDecision third = pool.Decide(board, true, false, true, false);
            result.Check("success keeps advancing",
                third.Action == PoolAction.Run && third.Entry.Key == "c", third.Describe());
            PoolDecision fourth = pool.Decide(board, true, false, true, false);
            result.Check("the pool cycles (wraps to the first entry)",
                fourth.Action == PoolAction.Run && fourth.Entry.Key == "a", fourth.Describe());

            result.Check("switch count matches the decisions",
                pool.Switches == 4, "switches=" + pool.Switches);
        }

        /// <summary>
        /// **自动前进关掉时（相位绑定在管事）**：起得来、但不许自己往下轮。
        ///
        /// 为什么这条必须有：绑定"世界内跑 demo.laya"之后，池如果还在成功后按自然顺序换树，
        /// 就会把绑定刚切过去的那棵树换掉（**实机踩过**：切到 demo.laya 后下一个边界又切回 demo.front）。
        /// 两种来源仍然能动树：显式的 `sequence`（人明确要的轮换）与 `pool.next`（人或绑定写的）。
        /// </summary>
        private static void CaseStickyAutoAdvance(BtSelfTest.TestResult result)
        {
            PoolScheduler pool = NewPool("a", "b", "c");
            pool.AutoAdvance = false;
            var board = new AiBlackboard();

            PoolDecision first = pool.Decide(board, false, false, false, false);
            result.Check("auto-advance off still starts a tree when none is running",
                first.Action == PoolAction.Run && first.Entry.Key == "a", first.Describe());

            PoolDecision afterSuccess = pool.Decide(board, true, false, true, false);
            result.Check("auto-advance off: a success does NOT move to the next entry",
                afterSuccess.Action == PoolAction.None && afterSuccess.Entry != null
                && afterSuccess.Entry.Key == "a" && pool.Switches == 1,
                afterSuccess.Describe() + " switches=" + pool.Switches);
            result.Check("the decision explains why it stayed put",
                afterSuccess.Reason != null && afterSuccess.Reason.Contains("auto-advance is off"),
                afterSuccess.Reason);

            // pool.next（相位绑定走的就是它）照样能动树
            board.Set(new AiBlackboardKey<string>(PoolScheduler.KeyNext), "b");
            PoolDecision fromNext = pool.Decide(board, true, false, true, false);
            result.Check("auto-advance off: pool.next still switches (that is how a phase binding steers)",
                fromNext.Action == PoolAction.Run && fromNext.Entry.Key == "b", fromNext.Describe());

            // 显式 sequence 也照样轮换（那是人明确要的）
            PoolScheduler sequenced = NewPool("a", "b", "c");
            sequenced.SetSequence(new[] { "a", "b", "c" });
            sequenced.AutoAdvance = false;
            var board2 = new AiBlackboard();
            sequenced.Decide(board2, false, false, false, false);
            PoolDecision explicitSequence = sequenced.Decide(board2, true, false, true, false);
            result.Check("auto-advance off: an explicit sequence still rotates",
                explicitSequence.Action == PoolAction.Run && explicitSequence.Entry.Key == "b",
                explicitSequence.Describe());

            Dictionary<string, object> described = pool.Describe();
            result.Check("pool status exposes autoAdvance (so the switch is visible in ai.pool.status)",
                Equals(described["autoAdvance"], false), "autoAdvance=" + described["autoAdvance"]);

            // **失败也不许自己换树**（相位绑定在管事时）：实机踩过 —— 世界里的树一失败，
            // 池就按自然顺序切回了界面树。
            PoolScheduler failing = NewPool("a", "b", "c");
            failing.AutoAdvance = false;
            var board3 = new AiBlackboard();
            failing.Decide(board3, false, false, false, false);        // 起 a
            PoolDecision afterFailure = failing.Decide(board3, true, false, false, true);
            result.Check("*** auto-advance off: a failure does NOT rotate the pool ***",
                afterFailure.Action == PoolAction.None && afterFailure.Entry != null
                && afterFailure.Entry.Key == "a" && failing.Switches == 1 && !failing.SafeIdle,
                afterFailure.Describe() + " switches=" + failing.Switches + " safeIdle=" + failing.SafeIdle);

            // 但显式 pool.fallback 仍然有效（那是人明确要的）
            PoolScheduler withFallback = NewPool("a", "b", "c");
            withFallback.AutoAdvance = false;
            var board4 = new AiBlackboard();
            withFallback.Decide(board4, false, false, false, false);
            board4.Set(new AiBlackboardKey<string>(PoolScheduler.KeyFallback), "c");
            PoolDecision fell = withFallback.Decide(board4, true, false, false, true);
            result.Check("auto-advance off: an explicit pool.fallback still fires",
                fell.Action == PoolAction.Run && fell.Entry.Key == "c", fell.Describe());
        }

        /// <summary>
        /// **`Peek` 必须完全只读**：`ai.pool.status` 用它"演一遍"调度，绝不能改计数、
        /// 更不能**消费 `pool.next`** —— 以前状态查询直接调 `Decide`，
        /// 结果把别人（相位绑定）写的 `pool.next` 吃掉了，调度器以为切过了、树却还是旧的（实机踩过）。
        /// </summary>
        private static void CasePeekIsReadOnly(BtSelfTest.TestResult result)
        {
            PoolScheduler pool = NewPool("a", "b", "c");
            var board = new AiBlackboard();
            pool.Decide(board, false, false, false, false);   // 起 a
            board.Set(new AiBlackboardKey<string>(PoolScheduler.KeyNext), "b");

            long switches = pool.Switches;
            long decisions = pool.Decisions;
            long deferrals = pool.Deferrals;

            PoolDecision peeked = pool.Peek(board, true, false, true, false);
            result.Check("Peek reports what the scheduler would do",
                peeked.Action == PoolAction.Run && peeked.Entry.Key == "b", peeked.Describe());

            string still;
            result.Check("*** Peek does not consume pool.next ***",
                board.TryGet<string>(PoolScheduler.KeyNext, out still) && still == "b",
                "pool.next='" + (still ?? "<null>") + "'");
            result.Check("*** Peek does not count switches or move the active entry ***",
                pool.Switches == switches && pool.Decisions == decisions && pool.Deferrals == deferrals
                && pool.Active != null && pool.Active.Key == "a",
                "switches=" + pool.Switches + " active=" + (pool.Active != null ? pool.Active.Key : "-"));

            PoolDecision real = pool.Decide(board, true, false, true, false);
            result.Check("the real decision after Peek still switches (and consumes pool.next)",
                real.Action == PoolAction.Run && real.Entry.Key == "b"
                && pool.Active.Key == "b" && pool.Switches == switches + 1,
                real.Describe() + " switches=" + pool.Switches);

            // 停在被推迟时的 Peek：也不该把 deferrals 记上去
            long deferrals2 = pool.Deferrals;
            PoolDecision deferred = pool.Peek(board, true, true, false, false);
            result.Check("Peek while the tree is running reports the deferral without counting it",
                deferred.Action == PoolAction.None && deferred.DeferredUntilStepBoundary
                && pool.Deferrals == deferrals2, deferred.Describe());
        }

        private static void CaseNextWins(BtSelfTest.TestResult result)        {
            PoolScheduler pool = NewPool("a", "b", "c");
            var board = new AiBlackboard();
            pool.Decide(board, false, false, false, false);      // a

            // Laya 决定"下一个跑 c" → 优先于顺序里的 b
            board.Set(new AiBlackboardKey<string>(PoolScheduler.KeyNext), "c");
            PoolDecision jumped = pool.Decide(board, true, false, true, false);
            result.Check("pool.next overrides the natural order",
                jumped.Action == PoolAction.Run && jumped.Entry.Key == "c"
                && jumped.Reason != null && jumped.Reason.Contains("pool.next"), jumped.Describe());
        }

        private static void CaseBadNextRejected(BtSelfTest.TestResult result)
        {
            PoolScheduler pool = NewPool("a", "b");
            var board = new AiBlackboard();
            pool.Decide(board, false, false, false, false);

            board.Set(new AiBlackboardKey<string>(PoolScheduler.KeyNext), "not_in_pool");
            PoolDecision bad = pool.Decide(board, true, false, true, false);
            result.Check("unknown pool.next is refused with the known list",
                bad.Action == PoolAction.None && bad.Reason != null
                && bad.Reason.Contains("not in the pool") && bad.Reason.Contains("a") && bad.Reason.Contains("b"),
                bad.Describe());
        }

        private static void CaseFailurePath(BtSelfTest.TestResult result)
        {
            PoolScheduler pool = NewPool("a", "b", "c");
            pool.MaxConsecutiveFailures = 2;
            var board = new AiBlackboard();
            pool.Decide(board, false, false, false, false);       // a

            // 失败 → 顺序里的下一项（b）
            PoolDecision afterFail = pool.Decide(board, true, false, false, true);
            result.Check("failure moves to the next entry",
                afterFail.Action == PoolAction.Run && afterFail.Entry.Key == "b", afterFail.Describe());

            // 再失败 → 到上限 → 安全态（不无限重试）
            PoolDecision exhausted = pool.Decide(board, true, false, false, true);
            result.Check("consecutive failures reach a safe idle (no infinite retry)",
                exhausted.Action == PoolAction.SafeIdle && pool.SafeIdle, exhausted.Describe());

            // fallback 键优先于"顺序下一项"
            PoolScheduler pool2 = NewPool("a", "b", "safe");
            pool2.MaxConsecutiveFailures = 5;
            var board2 = new AiBlackboard();
            pool2.Decide(board2, false, false, false, false);     // a
            board2.Set(new AiBlackboardKey<string>(PoolScheduler.KeyFallback), "safe");
            PoolDecision fallback = pool2.Decide(board2, true, false, false, true);
            result.Check("pool.fallback is preferred after a failure",
                fallback.Action == PoolAction.Run && fallback.Entry.Key == "safe"
                && fallback.Reason != null && fallback.Reason.Contains("fallback"), fallback.Describe());

            // 成功会把"连续失败"清零
            PoolScheduler pool3 = NewPool("a", "b");
            pool3.MaxConsecutiveFailures = 3;
            var board3 = new AiBlackboard();
            pool3.Decide(board3, false, false, false, false);
            pool3.Decide(board3, true, false, false, true);       // 失败 1 次
            pool3.Decide(board3, true, false, true, false);       // 成功 → 清零
            result.Check("a success resets the consecutive failure counter",
                Convert.ToInt32(pool3.Describe()["consecutiveFailures"]) == 0,
                Convert.ToString(pool3.Describe()["consecutiveFailures"]));
        }

        private static void CaseSequenceOrder(BtSelfTest.TestResult result)
        {
            PoolScheduler pool = NewPool("a", "b", "c");
            pool.SetSequence(new[] { "c", "a", "b" });
            var board = new AiBlackboard();

            PoolDecision first = pool.Decide(board, false, false, false, false);
            result.Check("an explicit sequence changes the first entry",
                first.Action == PoolAction.Run && first.Entry.Key == "c", first.Describe());
            PoolDecision second = pool.Decide(board, true, false, true, false);
            result.Check("the sequence order is followed",
                second.Action == PoolAction.Run && second.Entry.Key == "a", second.Describe());

            // Laya 通过黑板键改顺序（不需要新节点）
            var board2 = new AiBlackboard();
            PoolScheduler pool2 = NewPool("a", "b", "c");
            board2.Set(new AiBlackboardKey<string>(PoolScheduler.KeySequence), "b,c,a");
            pool2.ApplyBlackboard(board2);
            PoolDecision fromBlackboard = pool2.Decide(board2, false, false, false, false);
            result.Check("pool.sequence on the blackboard reorders the pool",
                fromBlackboard.Action == PoolAction.Run && fromBlackboard.Entry.Key == "b",
                fromBlackboard.Describe());
        }

        private static void CaseEmptyPool(BtSelfTest.TestResult result)
        {
            var pool = new PoolScheduler();
            PoolDecision decision = pool.Decide(null, false, false, false, false);
            result.Check("an empty pool goes to a safe idle",
                decision.Action == PoolAction.SafeIdle && pool.SafeIdle, decision.Describe());
            result.Check("describe() reports the safe idle",
                Convert.ToBoolean(pool.Describe()["safeIdle"])
                && Convert.ToString(pool.Describe()["pool"]) == "<empty>",
                pool.ToString());
        }
    }
}

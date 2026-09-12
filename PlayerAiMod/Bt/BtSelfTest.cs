using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树内核自检：**纯逻辑、不依赖游戏**，用来验证 P0-1/P0-2 的语义
    /// （Selector/Sequence 推进、跨帧任务、装饰器条件与结果改写、观察者中断、
    /// 冷却/限时/循环、帧预算、异常隔离、树自动重跑、注册表与快照）。
    ///
    /// 用法：`BtSelfTest.Run()` 返回逐条结果；全部通过时 <c>AllPassed</c> 为真。
    /// 将来由控制面命令 `bt.selftest` 调用（P0-7），现在也能被测试工程直接调用。
    /// </summary>
    public static class BtSelfTest
    {
        public sealed class TestResult
        {
            public readonly List<string> Lines = new List<string>();
            public int Passed;
            public int Failed;

            /// <summary>摘要里的名字（多个自检套件共用这个结果类型）。</summary>
            public string Label = "SelfTest";

            public bool AllPassed
            {
                get { return Failed == 0; }
            }

            public void Check(string name, bool ok, string detail = null)
            {
                if (ok)
                {
                    Passed++;
                    Lines.Add("PASS  " + name);
                }
                else
                {
                    Failed++;
                    Lines.Add("FAIL  " + name + (string.IsNullOrEmpty(detail) ? string.Empty : "  - " + detail));
                }
            }

            public override string ToString()
            {
                return Label + " " + Passed + "/" + (Passed + Failed) + " passed"
                    + (AllPassed ? " (ALL PASS)" : " (" + Failed + " FAILED)");
            }
        }

        private const float Dt = 1f / 60f;

        private static BtRuntime NewRuntime(out BtTestActuator actuator, out BtTestSensor sensor, out AiBlackboard blackboard)
        {
            actuator = new BtTestActuator();
            sensor = new BtTestSensor();
            blackboard = new AiBlackboard();
            var runtime = new BtRuntime(blackboard, sensor, actuator);
            runtime.Start();
            return runtime;
        }

        private static void TickN(BtRuntime runtime, int frames)
        {
            for (int i = 0; i < frames; i++)
                runtime.Tick(Dt);
        }

        private static BtLambdaTask Instant(string id, Func<BtContext, BtResult> body)
        {
            return new BtLambdaTask { Id = id, ExecuteDelegate = body };
        }

        // ---------------------------------------------------------------- 用例

        public static TestResult Run()
        {
            var result = new TestResult();

            try { CaseSequenceOrder(result); } catch (Exception e) { result.Check("case:Sequence order", false, e.Message); }
            try { CaseSequenceFailure(result); } catch (Exception e) { result.Check("case:Sequence failure", false, e.Message); }
            try { CaseSelector(result); } catch (Exception e) { result.Check("case:Selector", false, e.Message); }
            try { CaseLatentTask(result); } catch (Exception e) { result.Check("case:Latent task", false, e.Message); }
            try { CaseBlackboardDecorator(result); } catch (Exception e) { result.Check("case:Blackboard decorator", false, e.Message); }
            try { CaseObserverSelfAbort(result); } catch (Exception e) { result.Check("case:Observer Self abort", false, e.Message); }
            try { CaseObserverLowerPriority(result); } catch (Exception e) { result.Check("case:Observer LowerPriority", false, e.Message); }
            try { CaseCooldown(result); } catch (Exception e) { result.Check("case:Cooldown", false, e.Message); }
            try { CaseTimeLimit(result); } catch (Exception e) { result.Check("case:TimeLimit", false, e.Message); }
            try { CaseLoop(result); } catch (Exception e) { result.Check("case:Loop", false, e.Message); }
            try { CaseResultRewriters(result); } catch (Exception e) { result.Check("case:ForceSuccess/Inverter", false, e.Message); }
            try { CaseFrameBudget(result); } catch (Exception e) { result.Check("case:Frame budget", false, e.Message); }
            try { CaseExceptionIsolation(result); } catch (Exception e) { result.Check("case:Exception isolation", false, e.Message); }
            try { CaseTreeRestart(result); } catch (Exception e) { result.Check("case:Tree restart", false, e.Message); }
            try { CaseRegistry(result); } catch (Exception e) { result.Check("case:Registry", false, e.Message); }
            try { CaseSnapshot(result); } catch (Exception e) { result.Check("case:Snapshot", false, e.Message); }
            try { CaseRootMigration(result); } catch (Exception e) { result.Check("case:Root migration", false, e.Message); }
            try { LoopOnce(result); } catch (Exception e) { result.Check("case:Root loop flag", false, e.Message); }

            return result;
        }

        private static void CaseSequenceOrder(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            var order = new List<string>();
            var sequence = new BtSequenceNode { Id = "seq" };
            sequence.AddChild(Instant("a", c => { order.Add("a"); return BtResult.Succeeded; }));
            sequence.AddChild(Instant("b", c => { order.Add("b"); return BtResult.Succeeded; }));
            runtime.SetRoot(sequence, "seq-order");

            runtime.Tick(Dt);

            result.Check("Sequence runs children in order", string.Join(",", order.ToArray()) == "a,b",
                "order=" + string.Join(",", order.ToArray()));
            result.Check("Sequence Succeeded when all children succeed", runtime.LastResult == BtResult.Succeeded,
                "last=" + runtime.LastResult);
        }

        private static void CaseSequenceFailure(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            bool secondRan = false;
            var sequence = new BtSequenceNode { Id = "seq" };
            sequence.AddChild(Instant("a", c => BtResult.Failed));
            sequence.AddChild(Instant("b", c => { secondRan = true; return BtResult.Succeeded; }));
            runtime.SetRoot(sequence);

            runtime.Tick(Dt);

            result.Check("Sequence stops at first failure", !secondRan);
            result.Check("Sequence Failed when a child fails", runtime.LastResult == BtResult.Failed,
                "last=" + runtime.LastResult);
        }

        private static void CaseSelector(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            var ran = new List<string>();
            var selector = new BtSelectorNode { Id = "sel" };
            selector.AddChild(Instant("a", c => { ran.Add("a"); return BtResult.Failed; }));
            selector.AddChild(Instant("b", c => { ran.Add("b"); return BtResult.Succeeded; }));
            selector.AddChild(Instant("c", c => { ran.Add("c"); return BtResult.Succeeded; }));
            runtime.SetRoot(selector);

            runtime.Tick(Dt);

            result.Check("Selector tries children until one succeeds",
                string.Join(",", ran.ToArray()) == "a,b", "ran=" + string.Join(",", ran.ToArray()));
            result.Check("Selector Succeeded", runtime.LastResult == BtResult.Succeeded);
        }

        private static void CaseLatentTask(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            int ticks = 0;
            bool exitCalled = false;
            var task = new BtLambdaTask
            {
                Id = "latent",
                Latent = true,
                ExecuteDelegate = c => BtResult.InProgress,
                TickDelegate = c => { ticks++; return ticks >= 3 ? BtResult.Succeeded : BtResult.InProgress; },
                ExitDelegate = (c, r) => { exitCalled = true; }
            };
            var root = new BtSequenceNode { Id = "root" };
            root.AddChild(task);
            runtime.SetRoot(root);

            // 恰好跑到"任务成功"那一帧（第 4 帧）；再多跑一帧整棵树就会重跑（UE 行为）
            TickN(runtime, 4);

            result.Check("Latent task keeps running across frames", ticks >= 3, "ticks=" + ticks);
            result.Check("Latent task exit ran", exitCalled);
            result.Check("Tree completed after latent task succeeded",
                runtime.LastResult == BtResult.Succeeded && runtime.CompletedLoops >= 1,
                "last=" + runtime.LastResult + " loops=" + runtime.CompletedLoops);
        }

        private static void CaseBlackboardDecorator(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            var ran = new List<string>();
            var selector = new BtSelectorNode { Id = "sel" };
            var gated = Instant("gated", c => { ran.Add("gated"); return BtResult.Succeeded; });
            gated.AddDecorator(new BtBlackboardDecorator { Key = "flag", Query = "IsSet" });
            selector.AddChild(gated);
            selector.AddChild(Instant("fallback", c => { ran.Add("fallback"); return BtResult.Succeeded; }));
            runtime.SetRoot(selector);

            runtime.Tick(Dt);
            result.Check("Blackboard IsSet gate blocks branch when key missing",
                string.Join(",", ran.ToArray()) == "fallback", "ran=" + string.Join(",", ran.ToArray()));

            blackboard.Set(new AiBlackboardKey<bool>("flag"), true);
            ran.Clear();
            runtime.Tick(Dt);
            result.Check("Blackboard IsSet gate opens when key set",
                string.Join(",", ran.ToArray()) == "gated", "ran=" + string.Join(",", ran.ToArray()));

            var compare = new BtBlackboardDecorator
            {
                Key = "distance",
                Query = "Compare",
                ValueKind = "float",
                Operator = "<=",
                FloatValue = 3f
            };
            blackboard.Set(new AiBlackboardKey<float>("distance"), 2.5f);
            result.Check("Blackboard compare true (2.5 <= 3)", compare.CheckCondition(runtime.Context));
            blackboard.Set(new AiBlackboardKey<float>("distance"), 4.5f);
            result.Check("Blackboard compare false (4.5 <= 3) is false", !compare.CheckCondition(runtime.Context));
        }

        private static void CaseObserverSelfAbort(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            bool gate = true;
            bool aborted = false;
            var task = new BtLambdaTask
            {
                Id = "long",
                Latent = true,
                ExecuteDelegate = c => BtResult.InProgress,
                TickDelegate = c => BtResult.InProgress,
                ExitDelegate = (c, r) =>
                {
                    if (r == BtResult.Aborted)
                    {
                        aborted = true;
                        c.Actuators.ReleaseAll();
                    }
                }
            };
            task.AddDecorator(new BtConditionDecorator
            {
                Id = "gate",
                Abort = BtAbortMode.Self,
                Condition = c => gate
            });

            var root = new BtSequenceNode { Id = "root" };
            root.AddChild(task);
            runtime.SetRoot(root);

            TickN(runtime, 3);
            result.Check("Observer Self: node runs while condition true", task.IsActive || aborted == false);

            gate = false;
            TickN(runtime, 2);

            result.Check("Observer Self: abort on condition loss", aborted,
                "aborted=" + aborted);
            result.Check("Observer Self: aborted node released input", actuator.ReleaseAllCalls > 0,
                "releaseAll=" + actuator.ReleaseAllCalls);
        }

        private static void CaseObserverLowerPriority(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            bool highReady = false;
            bool lowAborted = false;
            bool highRan = false;

            var selector = new BtSelectorNode { Id = "priority" };

            var high = Instant("high", c => { highRan = true; return BtResult.Succeeded; });
            high.AddDecorator(new BtConditionDecorator
            {
                Id = "highGate",
                Abort = BtAbortMode.LowerPriority,
                Condition = c => highReady
            });

            var low = new BtLambdaTask
            {
                Id = "low",
                Latent = true,
                ExecuteDelegate = c => BtResult.InProgress,
                TickDelegate = c => BtResult.InProgress,
                ExitDelegate = (c, r) => { if (r == BtResult.Aborted) lowAborted = true; }
            };

            selector.AddChild(high);
            selector.AddChild(low);
            runtime.SetRoot(selector);

            TickN(runtime, 3);
            result.Check("LowerPriority: low branch runs first when high condition false", !highRan);

            highReady = true;
            TickN(runtime, 2);

            result.Check("LowerPriority: high-priority condition preempts running low branch", lowAborted,
                "lowAborted=" + lowAborted);
            result.Check("LowerPriority: high branch then executes", highRan);
        }

        private static void CaseCooldown(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            int aRuns = 0;
            int bRuns = 0;
            var selector = new BtSelectorNode { Id = "sel" };
            var a = Instant("a", c => { aRuns++; return BtResult.Succeeded; });
            a.AddDecorator(new BtCooldownDecorator { CooldownSeconds = 1f });
            selector.AddChild(a);
            selector.AddChild(Instant("b", c => { bRuns++; return BtResult.Succeeded; }));
            runtime.SetRoot(selector);

            // 第一趟：a 成功并进入冷却（1 秒）
            runtime.Tick(Dt);
            // 冷却期内：a 不可用，落到 b；整棵树跑完会自动重跑，所以 b 会被反复选中
            TickN(runtime, 3);

            result.Check("Cooldown: first pass runs a exactly once", aRuns == 1, "aRuns=" + aRuns);
            result.Check("Cooldown: locked branch skipped, fallback runs", bRuns > 0, "bRuns=" + bRuns);
            result.Check("Cooldown survives tree restart (state kept)", aRuns == 1 && bRuns > 0,
                "aRuns=" + aRuns + " bRuns=" + bRuns);
        }

        private static void CaseTimeLimit(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            var task = new BtLambdaTask
            {
                Id = "slow",
                Latent = true,
                ExecuteDelegate = c => BtResult.InProgress,
                TickDelegate = c => BtResult.InProgress
            };
            task.AddDecorator(new BtTimeLimitDecorator { LimitSeconds = 0.05f });

            var root = new BtSequenceNode { Id = "root" };
            root.AddChild(task);
            runtime.SetRoot(root);

            TickN(runtime, 8); // 8 帧 ≈ 0.133s > 0.05s

            result.Check("TimeLimit fails a node that runs too long", !task.IsActive,
                "active=" + task.IsActive);
            result.Check("TimeLimit produced Failed", task.LastResult == BtResult.Failed,
                "last=" + task.LastResult);
        }

        private static void CaseLoop(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            int runs = 0;
            var task = Instant("looped", c => { runs++; return BtResult.Succeeded; });
            task.AddDecorator(new BtLoopDecorator { NumLoops = 3 });

            var root = new BtSequenceNode { Id = "root" };
            root.AddChild(task);
            runtime.SetRoot(root);

            // 3 帧刚好跑满 3 次循环（第 3 帧结束时循环完成、整棵树完成）
            TickN(runtime, 3);

            result.Check("Loop runs the node NumLoops times", runs == 3, "runs=" + runs);
            result.Check("Loop completed exactly one tree pass", runtime.CompletedLoops == 1,
                "loops=" + runtime.CompletedLoops);
        }

        private static void CaseResultRewriters(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            var forced = Instant("forced", c => BtResult.Failed);
            forced.AddDecorator(new BtForceSuccessDecorator());
            var root = new BtSequenceNode { Id = "root" };
            root.AddChild(forced);
            runtime.SetRoot(root);
            runtime.Tick(Dt);
            result.Check("ForceSuccess turns Failed into Succeeded", runtime.LastResult == BtResult.Succeeded,
                "last=" + runtime.LastResult);

            var inverted = Instant("inverted", c => BtResult.Succeeded);
            inverted.AddDecorator(new BtInverterDecorator());
            runtime.SetRoot(root = new BtSequenceNode { Id = "root2" });
            root.AddChild(inverted);
            runtime.Tick(Dt);
            result.Check("Inverter turns Succeeded into Failed", runtime.LastResult == BtResult.Failed,
                "last=" + runtime.LastResult);
        }

        private static void CaseFrameBudget(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);
            runtime.TickBudgetPerFrame = 2;

            int runs = 0;
            var root = new BtSequenceNode { Id = "root" };
            for (int i = 0; i < 5; i++)
            {
                string id = "t" + i;
                root.AddChild(Instant(id, c => { runs++; return BtResult.Succeeded; }));
            }
            runtime.SetRoot(root);

            runtime.Tick(Dt);
            int firstFrameRuns = runs;
            TickN(runtime, 6);

            result.Check("Frame budget postpones work (not all children in one frame)",
                firstFrameRuns < 5, "firstFrameRuns=" + firstFrameRuns);
            // 整棵树跑完会自动重跑，所以断言"至少跑满 5 个孩子"，而不是恰好 5
            result.Check("Budgeted tree still completes over frames", runs >= 5, "runs=" + runs);
            result.Check("Budget exhaustion reported", runtime.LastNodesVisited <= 8,
                "visited=" + runtime.LastNodesVisited);
        }

        private static void CaseExceptionIsolation(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            var root = new BtSequenceNode { Id = "root" };
            root.AddChild(Instant("boom", c => { throw new InvalidOperationException("boom"); }));
            runtime.SetRoot(root);

            bool threw = false;
            try
            {
                TickN(runtime, 3);
            }
            catch (Exception)
            {
                threw = true;
            }

            result.Check("Throwing node does not crash the tick loop", !threw);
            result.Check("Throwing node recorded LastError",
                !string.IsNullOrEmpty(runtime.LastError), "err=" + runtime.LastError);
            result.Check("Tree was reset after exception", !root.IsActive);
        }

        private static void CaseTreeRestart(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            int runs = 0;
            var root = new BtSequenceNode { Id = "root" };
            root.AddChild(Instant("once", c => { runs++; return BtResult.Succeeded; }));
            runtime.SetRoot(root);

            TickN(runtime, 3);

            result.Check("Finished tree is restarted (UE behaviour)", runs >= 2, "runs=" + runs);
            result.Check("CompletedLoops counted", runtime.CompletedLoops >= 2,
                "loops=" + runtime.CompletedLoops);
        }

        private static void CaseRegistry(TestResult result)
        {
            BtNode node;
            BtDecorator decorator;
            BtService service;

            result.Check("Registry: Selector", BtNodeRegistry.TryCreateNode("Selector", out node) && node is BtSelectorNode);
            result.Check("Registry: Sequence", BtNodeRegistry.TryCreateNode("Sequence", out node) && node is BtSequenceNode);
            result.Check("Registry: Task.Wait", BtNodeRegistry.TryCreateNode("Task.Wait", out node) && node is BtWaitTask);
            result.Check("Registry: Task.MoveTo", BtNodeRegistry.TryCreateNode("Task.MoveTo", out node) && node is BtMoveToTargetTask);
            result.Check("Registry: Task.Subtree", BtNodeRegistry.TryCreateNode("Task.Subtree", out node) && node is BtSubtreeTask);
            result.Check("Registry: decorator Blackboard",
                BtNodeRegistry.TryCreateDecorator("Blackboard", out decorator) && decorator is BtBlackboardDecorator);
            result.Check("Registry: decorator TimeLimit",
                BtNodeRegistry.TryCreateDecorator("TimeLimit", out decorator) && decorator is BtTimeLimitDecorator);
            result.Check("Registry: service UpdateNearestPlayer",
                BtNodeRegistry.TryCreateService("Service.UpdateNearestPlayer", out service)
                && service is BtUpdateNearestPlayerService);
            result.Check("Registry: unknown type rejected", !BtNodeRegistry.TryCreateNode("Task.DoesNotExist", out node));
            result.Check("Registry: node type lists non-empty",
                BtNodeRegistry.ListNodeTypes().Count >= 10 && BtNodeRegistry.ListDecoratorTypes().Count >= 6);
        }

        private static void CaseSnapshot(TestResult result)
        {
            BtTestActuator actuator;
            BtTestSensor sensor;
            AiBlackboard blackboard;
            BtRuntime runtime = NewRuntime(out actuator, out sensor, out blackboard);

            var root = new BtSequenceNode { Id = "root" };
            var task = new BtLambdaTask
            {
                Id = "running",
                Latent = true,
                ExecuteDelegate = c => BtResult.InProgress,
                TickDelegate = c => BtResult.InProgress
            };
            root.AddChild(task);
            runtime.SetRoot(root, "snapshot-test", "demo.scbtpak", "sha256:abc");

            TickN(runtime, 2);

            BtSnapshot snapshot = runtime.Snapshot();
            result.Check("Snapshot exposes tree metadata",
                snapshot.TreeId == "snapshot-test" && snapshot.SourcePackage == "demo.scbtpak"
                && snapshot.SourceHash == "sha256:abc");
            result.Check("Snapshot reports running state", snapshot.IsRunning);
            result.Check("Snapshot active path includes the running task",
                snapshot.DescribeActivePath().Contains("running"), snapshot.DescribeActivePath());
            result.Check("Snapshot does not hide dirtiness flag", snapshot.IsDirty == false);
        }

        // ---------------------------------------------------------------- 运行态迁移（热重载的地基）

        /// <summary>跨帧任务：按住 W 不放，用来观察"迁移后有没有被掐断/有没有漏放按键"。</summary>
        private static BtLambdaTask HoldForward(string id)
        {
            return new BtLambdaTask
            {
                Id = id,
                Latent = true,
                ExecuteDelegate = context =>
                {
                    context.Actuators.HoldKey("w", true);
                    return BtResult.InProgress;
                },
                TickDelegate = context =>
                {
                    context.Actuators.HoldKey("w", true);
                    return BtResult.InProgress;
                },
                ExitDelegate = (context, exitResult) => context.Actuators.HoldKey("w", false)
            };
        }

        private static BtNode SequenceTree(string seqId, BtNode first, BtNode second)
        {
            var root = new BtRootNode { Id = "root" };
            var sequence = new BtSequenceNode { Id = seqId };
            if (first != null)
                sequence.AddChild(first);
            if (second != null)
                sequence.AddChild(second);
            root.AddChild(sequence);
            return root;
        }

        private static void CaseRootMigration(TestResult result)
        {
            var blackboard = new AiBlackboard();
            var actuator = new BtTestActuator();
            var runtime = new BtRuntime(blackboard, new BtTestSensor(), actuator);

            // 旧树：Root[ Sequence(seq)[ hold, tail ] ]
            BtNode treeA = SequenceTree("seq", HoldForward("hold"),
                new BtWaitTask { Id = "tail", Seconds = 5f });
            runtime.SetRoot(treeA, "treeA", "a.scbtpak", "hashA");
            runtime.Start();
            TickN(runtime, 10);

            BtNode holdA = runtime.Root.Find("hold");
            bool activeBefore = holdA != null && holdA.IsActive;
            float before = holdA != null ? holdA.ActiveTime : -1f;
            result.Check("migration setup: latent task is running",
                activeBefore && actuator.HeldKeys.Contains("w"),
                "active=" + activeBefore + " keys=" + actuator.HeldKeys.Count);

            // 新树：结构相同、id 相同，只改一个参数（tail 5s -> 9s）
            BtNode treeB = SequenceTree("seq", HoldForward("hold"),
                new BtWaitTask { Id = "tail", Seconds = 9f });
            BtMigrationReport migrated = runtime.ReplaceRoot(treeB, true, "treeB", "b.scbtpak", "hashB");
            BtNode holdB = runtime.Root.Find("hold");

            result.Check("migration keeps the running task alive",
                migrated.Migrated && migrated.PreservedNodes >= 3 && holdB != null && holdB.IsActive,
                migrated.Describe());
            result.Check("migration aborts nothing when ids match",
                migrated.AbortedNodes == 0, migrated.Describe());
            result.Check("migration does not release input",
                actuator.ReleaseAllCalls == 0 && actuator.HeldKeys.Contains("w"),
                "releaseAll=" + actuator.ReleaseAllCalls + " keys=" + actuator.HeldKeys.Count);
            result.Check("migration keeps the changed parameter",
                ((BtWaitTask)runtime.Root.Find("tail")).Seconds > 8f,
                "tail=" + ((BtWaitTask)runtime.Root.Find("tail")).Seconds);

            TickN(runtime, 5);
            result.Check("preserved task continues its own timer", holdB.ActiveTime > before,
                "before=" + before.ToString("0.000") + " after=" + holdB.ActiveTime.ToString("0.000"));

            // 新树里没有 hold → 必须被正常中断收尾并放开 W
            BtNode treeC = SequenceTree("seq", null, new BtWaitTask { Id = "tail", Seconds = 9f });
            BtMigrationReport dropped = runtime.ReplaceRoot(treeC, true, "treeC", "c.scbtpak", "hashC");
            result.Check("dropped branch is aborted", dropped.AbortedNodes >= 1, dropped.Describe());
            result.Check("aborted task releases its input", !actuator.HeldKeys.Contains("w"),
                "keys=" + actuator.HeldKeys.Count);

            // 父链断了：hold 还在、类型也没变，但它换了父节点 → 不能迁移
            // （否则会出现"父节点没跑、子节点却 active"的幽灵任务，那种任务会永远按着键）
            runtime.ReplaceRoot(
                SequenceTree("seq", HoldForward("hold"), new BtWaitTask { Id = "tail", Seconds = 9f }),
                false, "treeD", "d.scbtpak", "hashD");
            TickN(runtime, 10);
            bool activeD = runtime.Root.Find("hold").IsActive;

            var rootE = new BtRootNode { Id = "root" };
            var selectorE = new BtSelectorNode { Id = "other" };
            selectorE.AddChild(HoldForward("hold"));
            rootE.AddChild(selectorE);

            BtMigrationReport ghost = runtime.ReplaceRoot(rootE, true, "treeE", "e.scbtpak", "hashE");
            BtNode holdE = runtime.Root.Find("hold");
            result.Check("migration requires the whole parent chain to match",
                activeD && ghost.AbortedNodes >= 1 && holdE != null && !holdE.IsActive,
                ghost.Describe() + " wasActive=" + activeD + " nowActive=" + (holdE != null && holdE.IsActive));

            TickN(runtime, 1);
            result.Check("ghost prevention: the re-selected task starts fresh",
                runtime.Root.Find("hold").ActiveTime <= 2f * Dt + 0.0001f,
                "activeTime=" + runtime.Root.Find("hold").ActiveTime.ToString("0.000"));

            // 不迁移 = 整树重启
            BtMigrationReport restarted = runtime.ReplaceRoot(
                SequenceTree("seq", HoldForward("hold"), new BtWaitTask { Id = "tail", Seconds = 1f }),
                false, "treeF", "f.scbtpak", "hashF");
            result.Check("ReplaceRoot(migrate:false) restarts the tree",
                !restarted.Migrated && restarted.Describe().Contains("restarted"),
                restarted.Describe());
            result.Check("restart released everything the old tree held",
                !actuator.HeldKeys.Contains("w"), "keys=" + actuator.HeldKeys.Count);

            result.Check("runtime counts migrations", runtime.MigrationCount >= 4
                && runtime.LastMigration != null, "count=" + runtime.MigrationCount);
        }

        /// <summary>
        /// `Root.loop=false` = 一次性树：跑完整棵树就把自己停掉，**不再从头再来**。
        ///
        /// 为什么必须有：UE 的语义是"完成 → 重开"，但"进游戏"这种菜单宏会因此每隔几秒
        /// 再点一次 Play（实测：树在菜单里循环点了十几轮）。
        /// </summary>
        private static void LoopOnce(BtSelfTest.TestResult result)
        {
            // ① 默认（loop=true）：完成之后继续跑下一轮
            {
                var actuator = new BtTestActuator();
                var runtime = new BtRuntime(new AiBlackboard(), new BtTestSensor(), actuator);
                var root = new BtRootNode { Id = "root" };
                root.AddChild(new BtLogTask { Id = "say", Message = "loop" });
                runtime.SetRoot(root, "loop.once.default");
                runtime.Start();

                for (int i = 0; i < 6; i++)
                    runtime.Tick(Dt);

                result.Check("a looping tree keeps running after it completes (UE semantics)",
                    runtime.IsRunning && runtime.CompletedLoops >= 1,
                    "running=" + runtime.IsRunning + " loops=" + runtime.CompletedLoops);
            }

            // ② loop=false：完成即停
            {
                var actuator = new BtTestActuator();
                var runtime = new BtRuntime(new AiBlackboard(), new BtTestSensor(), actuator);
                var root = new BtRootNode { Id = "root", Loop = false };
                root.AddChild(new BtLogTask { Id = "say", Message = "once" });
                runtime.SetRoot(root, "loop.once.disabled");
                runtime.Start();

                runtime.Tick(Dt);
                result.Check("a one-shot tree stops itself the moment it completes",
                    !runtime.IsRunning && runtime.CompletedLoops == 1
                    && runtime.LastResult == BtResult.Succeeded,
                    "running=" + runtime.IsRunning + " loops=" + runtime.CompletedLoops
                    + " last=" + runtime.LastResult);

                for (int i = 0; i < 4; i++)
                    runtime.Tick(Dt);
                result.Check("a stopped one-shot tree does not run another round",
                    runtime.CompletedLoops == 1 && runtime.TickCount == 1,
                    "loops=" + runtime.CompletedLoops + " ticks=" + runtime.TickCount);
            }
        }

    }
}

using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 模式层自检（P0-8）：**纯逻辑、不依赖游戏**。
    ///
    /// 因为 FSM 现在只认 <see cref="IAiTreeHost"/> 接口，这里可以把真正的
    /// <see cref="AiStateMachine"/> 绑到测试宿主上，验证：
    ///   · 模式图自检无问题（无孤儿状态、转移可达）；
    ///   · 初始 = 未接管；接管 → 待机；有树 → 运行树；树 tick 出错 → 未接管（安全态）；
    ///   · 关键一条：**树是"别人塞进来的"也要能推导出正确模式**（控制面直接替换运行时根的情形）；
    ///   · 模式映射表 <see cref="AiModeMap"/> 的每一项（含暂停/录制）；
    ///   · 离开运行树模式时"故障必停、正常保留运行态"。
    /// </summary>
    public static class AiModeSelfTest
    {
        private const float Dt = 1f / 60f;

        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "AiModeSelfTest" };
            try
            {
                GraphShape(result);
                ModeDerivation(result);
                FaultHandling(result);
                ExternalTreeLoad(result);
                RecordingMode(result);
                ModeMapTable(result);
            }
            catch (Exception exception)
            {
                result.Check("AiModeSelfTest no unexpected exception", false,
                    exception.GetType().Name + ": " + exception.Message);
            }
            return result;
        }

        // ---------------------------------------------------------------- 用例

        private static void GraphShape(BtSelfTest.TestResult result)
        {
            AiStateMachine machine = PlayerAiBehaviour.Create("selftest");
            IReadOnlyList<string> problems = machine.Validate();

            result.Check("mode graph validates without problems",
                problems.Count == 0, problems.Count > 0 ? problems[0] : "ok");
            result.Check("mode graph registers the three modes",
                machine.HasState(AiStateIds.Inactive) && machine.HasState(AiStateIds.Idle)
                && machine.HasState(AiStateIds.Tree),
                "states=" + string.Join(",", machine.StateIds));
            Tick(machine, 1);
            result.Check("initial mode is 'not taken over'",
                machine.CurrentStateId == AiStateIds.Inactive, machine.CurrentStateId);
            result.Check("mode layer has no decision states left",
                !machine.HasState("DemoLook") && !machine.HasState("DemoApproach"),
                "a demo decision state is still registered");
        }

        private static void ModeDerivation(BtSelfTest.TestResult result)
        {
            var host = new AiTestHost("mode-host");
            AiStateMachine machine = PlayerAiBehaviour.Create("mode-host").Bind(host);
            host.Machine = machine;

            // 第一帧：Start() 会把初始状态摆好（顺手吞掉启动前排队的触发，所以触发要在启动之后再 Raise）
            Tick(machine, 1);
            result.Check("host starts inactive", host.Mode == AiMode.Inactive,
                host.Mode.ToWireName());

            // 接管 → 待机
            machine.Raise(AiTriggers.Start);
            Tick(machine, 2);
            result.Check("taking over moves to idle mode",
                machine.CurrentStateId == AiStateIds.Idle && host.Mode == AiMode.Idle,
                machine.CurrentStateId + "/" + host.Mode.ToWireName());

            // 装树（走宿主的装载入口）→ 运行树
            CompiledTree compiled = BuildTree("m1", new BtWaitTask { Id = "w", Seconds = 5f });
            result.Check("tree loads into the host", host.LoadTree(compiled, true), "load failed");

            Tick(machine, 2);
            result.Check("a loaded tree moves the mode to 'running tree'",
                machine.CurrentStateId == AiStateIds.Tree && host.Mode == AiMode.Tree,
                machine.CurrentStateId + "/" + host.Mode.ToWireName());

            long before = host.Tree.TickCount;
            Tick(machine, 3);
            result.Check("tree mode actually ticks the tree", host.Tree.TickCount > before,
                "before=" + before + " after=" + host.Tree.TickCount);

            // 卸树 → 待机
            host.StopTree("selftest");
            Tick(machine, 2);
            result.Check("unloading the tree falls back to idle",
                machine.CurrentStateId == AiStateIds.Idle && host.Mode == AiMode.Idle,
                machine.CurrentStateId + "/" + host.Mode.ToWireName());

            // 暂停是"叠加"在模式上的（全局暂停在 PlayerAiRuntime）
            host.Paused = true;
            result.Check("pause is reported on top of the mode", host.Mode == AiMode.Paused,
                host.Mode.ToWireName());
            host.Paused = false;

            // 急停 → 未接管 + 释放输入（计数看执行器：状态是通过执行器放键的）
            int releases = host.TestActuator.ReleaseAllCalls;
            machine.Raise(AiTriggers.Stop);
            Tick(machine, 2);
            result.Check("stop returns to 'not taken over' and releases input",
                machine.CurrentStateId == AiStateIds.Inactive
                && host.TestActuator.ReleaseAllCalls > releases,
                machine.CurrentStateId + " releaseAll=" + (host.TestActuator.ReleaseAllCalls - releases));
        }

        private static void FaultHandling(BtSelfTest.TestResult result)
        {
            var host = new AiTestHost("fault-host");
            AiStateMachine machine = PlayerAiBehaviour.Create("fault-host").Bind(host);
            host.Machine = machine;

            Tick(machine, 1);
            machine.Raise(AiTriggers.Start);
            Tick(machine, 2);

            int releases = host.TestActuator.ReleaseAllCalls;
            CompiledTree compiled = BuildTree("boom", new ThrowingTask { Id = "boom" });
            host.LoadTree(compiled, true);
            Tick(machine, 4);

            result.Check("a failing tree is reported as a fault",
                host.Tree.LastError != null, host.Tree.LastError ?? "<none>");
            result.Check("a faulting tree forces the 'not taken over' mode",
                machine.CurrentStateId == AiStateIds.Inactive && host.Mode == AiMode.Fault,
                machine.CurrentStateId + "/" + host.Mode.ToWireName());
            result.Check("fault handling releases input",
                host.TestActuator.ReleaseAllCalls > releases,
                "releaseAll=" + (host.TestActuator.ReleaseAllCalls - releases));
            result.Check("faulting tree is stopped (no unticked tree keeps holding keys)",
                !host.Tree.IsRunning, "tree still running");
        }

        private static void ExternalTreeLoad(BtSelfTest.TestResult result)
        {
            // 控制面装载走的是 PackageReloader → 直接替换运行时根，不经过 host.LoadTree。
            // 模式层必须照样推导出"运行树"（否则命令装上了树，角色却不动）。
            var host = new AiTestHost("external-host");
            AiStateMachine machine = PlayerAiBehaviour.Create("external-host").Bind(host);
            host.Machine = machine;

            Tick(machine, 1);
            machine.Raise(AiTriggers.Start);
            Tick(machine, 2);

            host.Tree.ReplaceRoot(new BtWaitTask { Id = "w", Seconds = 5f }, false, "external",
                "x.scbtpak", "hash");
            host.Tree.Start();

            Tick(machine, 2);
            result.Check("a tree injected straight into the runtime still switches the mode",
                machine.CurrentStateId == AiStateIds.Tree, machine.CurrentStateId);

            host.Tree.ReplaceRoot(null, false);
            Tick(machine, 2);
            result.Check("clearing the runtime root falls back to idle",
                machine.CurrentStateId == AiStateIds.Idle, machine.CurrentStateId);
        }

        private static void RecordingMode(BtSelfTest.TestResult result)
        {
            var host = new AiTestHost("rec-host");
            AiStateMachine machine = PlayerAiBehaviour.Create("rec-host").Bind(host);
            host.Machine = machine;

            Tick(machine, 1);
            machine.Raise(AiTriggers.Start);
            Tick(machine, 2);

            CompiledTree compiled = BuildTree("rec", new BtWaitTask { Id = "w", Seconds = 10f });
            host.LoadTree(compiled, true);
            Tick(machine, 2);
            long ticksBefore = host.Tree.TickCount;

            // 开始录制：AI 停手、树不 tick、人类操作照常
            host.IsRecording = true;
            Tick(machine, 3);
            result.Check("recording takes over from the tree mode",
                machine.CurrentStateId == AiStateIds.Recording
                && host.Mode == AiMode.Recording,
                machine.CurrentStateId + "/" + host.Mode.ToWireName());
            result.Check("recording releases the AI's input on entry",
                host.TestActuator.ReleaseAllCalls > 0,
                "releaseAll=" + host.TestActuator.ReleaseAllCalls);
            result.Check("recording stops ticking the tree while running",
                host.Tree.TickCount == ticksBefore && host.Tree.IsRunning,
                "ticks=" + host.Tree.TickCount + " was=" + ticksBefore
                + " running=" + host.Tree.IsRunning);
            result.Check("recording keeps the tree runtime state (resume continues)",
                host.HasTree && host.Tree.IsRunning, "tree was reset or stopped");

            // 结束录制：回到运行树，且树继续跑
            host.IsRecording = false;
            Tick(machine, 3);
            result.Check("recording end returns to the tree mode",
                machine.CurrentStateId == AiStateIds.Tree && host.Tree.IsRunning,
                machine.CurrentStateId);
            Tick(machine, 2);
            result.Check("tree resumes ticking after recording", host.Tree.TickCount > ticksBefore,
                "ticks=" + host.Tree.TickCount);

            // 没有树时录制结束 → 待机
            host.StopTree("selftest");
            Tick(machine, 2);
            host.IsRecording = true;
            Tick(machine, 3);
            result.Check("recording works without a tree too",
                machine.CurrentStateId == AiStateIds.Recording, machine.CurrentStateId);
            host.IsRecording = false;
            Tick(machine, 3);
            result.Check("recording end without a tree falls back to idle",
                machine.CurrentStateId == AiStateIds.Idle, machine.CurrentStateId);
        }

        private static void ModeMapTable(BtSelfTest.TestResult result)
        {
            result.Check("mode map: inactive", AiModeMap.FromState(AiStateIds.Inactive, false, false)
                == AiMode.Inactive);
            result.Check("mode map: idle", AiModeMap.FromState(AiStateIds.Idle, false, false)
                == AiMode.Idle);
            result.Check("mode map: tree", AiModeMap.FromState(AiStateIds.Tree, false, false)
                == AiMode.Tree);
            result.Check("mode map: paused overrides tree",
                AiModeMap.FromState(AiStateIds.Tree, true, false) == AiMode.Paused);
            result.Check("mode map: recording", AiModeMap.FromState(AiStateIds.Recording, false, false)
                == AiMode.Recording);
            result.Check("mode map: fault wins over everything",
                AiModeMap.FromState(AiStateIds.Tree, true, true) == AiMode.Fault);
            // 未知状态（例如以后加的录制态拼错）当作"待机最安全"：只有 Inactive/Tree 是特殊语义。
            result.Check("mode map: unknown state falls back to idle",
                AiModeMap.FromState("nonsense", false, false) == AiMode.Idle);
        }

        // ---------------------------------------------------------------- 工具

        private static void Tick(AiStateMachine machine, int frames)
        {
            for (int i = 0; i < frames; i++)
                machine.Tick(Dt);
        }

        private static CompiledTree BuildTree(string id, BtNode root)
        {
            return new CompiledTree
            {
                Root = root,
                PackageId = id,
                EntryId = "root",
                SourcePath = id + ".scbtpak",
                SourceHash = "hash-" + id,
                NodeCount = 1
            };
        }

        /// <summary>必定抛异常的节点：用来验证"树故障 → 回安全模式"这条路径。</summary>
        private sealed class ThrowingTask : BtTaskNode
        {
            public override string NodeType
            {
                get { return "Task.Throw"; }
            }

            protected override BtResult OnExecute(BtContext context)
            {
                throw new InvalidOperationException("selftest: intentional tree failure");
            }
        }
    }
}

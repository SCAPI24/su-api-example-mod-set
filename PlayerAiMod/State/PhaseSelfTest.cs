using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 相位与交接协议自检（plan §4.13）：**纯逻辑、不依赖游戏**。
    ///
    /// 为什么这块必须钉死：交接动作全是"在边界上做一次"的，一条错了线上表现是
    /// "偶尔一次进世界后菜单自己乱走"或"世界外问到的答案落到世界上"，**极难复现**。
    /// 所以判定表逐格钉（谁变谁不变、谁释放、谁闸住），摘要口径与它对齐也钉一次。
    /// </summary>
    public static class PhaseSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "PhaseSelfTest" };

            try { CaseNames(result); } catch (Exception e) { result.Check("case:names", false, e.Message); }
            try { CasePlanTable(result); } catch (Exception e) { result.Check("case:plan table", false, e.Message); }
            try { CaseDebounce(result); } catch (Exception e) { result.Check("case:debounce", false, e.Message); }
            try { CaseDigestAgrees(result); } catch (Exception e) { result.Check("case:digest agrees", false, e.Message); }
            try { CaseEventText(result); } catch (Exception e) { result.Check("case:event text", false, e.Message); }
            try { CasePhaseBinding(result); } catch (Exception e) { result.Check("case:phase binding", false, e.Message); }

            return result;
        }

        /// <summary>
        /// **相位 → 树包绑定**（§4.13 的"两套树包"）：解析要严（相位名写错必须当场报错）、
        /// 查表要大小写无关、空串 = 清空。
        /// </summary>
        private static void CasePhaseBinding(BtSelfTest.TestResult result)
        {
            PhaseTreeBinding binding;
            string error;

            result.Check("a binding parses into a phase -> package table",
                PhaseTreeBinding.TryParse("front=demo.front,world=demo.laya", out binding, out error)
                && binding.Count == 2 && !string.IsNullOrEmpty(error) == false,
                error);

            string key;
            result.Check("lookup is case-insensitive on the phase name",
                binding.TryResolve(PhaseNames.World, out key) && key == "demo.laya"
                && binding.TryResolve("FRONT", out key) && key == "demo.front",
                key);

            result.Check("the transition phase can be bound too (even though the gate freezes the tree)",
                PhaseTreeBinding.TryParse("loading=demo.front", out binding, out error)
                && binding.TryResolve(PhaseNames.Loading, out key) && key == "demo.front",
                error);

            result.Check("*** an unknown phase name is rejected instead of silently never firing ***",
                !PhaseTreeBinding.TryParse("frontt=demo.front", out binding, out error)
                && error != null && error.Contains("frontt") && error.Contains("front / loading / world"),
                error);

            result.Check("a malformed entry is rejected with the offending text",
                !PhaseTreeBinding.TryParse("front", out binding, out error)
                && error != null && error.Contains("front"), error);
            result.Check("an entry without a package name is rejected",
                !PhaseTreeBinding.TryParse("world=", out binding, out error) && error != null, error);

            result.Check("*** one bad entry rejects the whole binding (no partial effect) ***",
                !PhaseTreeBinding.TryParse("front=demo.front,world=", out binding, out error)
                && error != null, error);

            result.Check("an empty spec is a valid 'clear'",
                PhaseTreeBinding.TryParse(string.Empty, out binding, out error)
                && binding.Count == 0 && error == null, error);
            result.Check("an unbound phase resolves to nothing (not to a random tree)",
                !binding.TryResolve(PhaseNames.World, out key) && key == null);
            result.Check("Describe() is readable when nothing is bound",
                binding.Describe().Contains("no phase binding"), binding.Describe());

            PhaseTreeBinding.TryParse("world=demo.laya,front=demo.front", out binding, out error);
            result.Check("Describe() is sorted (stable in logs and status output)",
                binding.Describe() == "front=demo.front,world=demo.laya", binding.Describe());
            result.Check("whitespace around entries is tolerated",
                PhaseTreeBinding.TryParse(" front = demo.front , world = demo.laya ", out binding, out error)
                && binding.Count == 2 && binding.Describe() == "front=demo.front,world=demo.laya",
                binding != null ? binding.Describe() : error);
        }

        /// <summary>相位口径：三档、可判、不认得的也算"认得清它不认得"。</summary>
        private static void CaseNames(BtSelfTest.TestResult result)
        {
            result.Check("no world = front",
                PhaseNames.Of(false, false) == PhaseNames.Front
                && PhaseNames.Of(false, true) == PhaseNames.Front,
                PhaseNames.Of(false, true));

            result.Check("world without a ready player = loading (transition)",
                PhaseNames.Of(true, false) == PhaseNames.Loading);

            result.Check("world with a ready player = world",
                PhaseNames.Of(true, true) == PhaseNames.World);

            result.Check("predicates are mutually exclusive",
                PhaseNames.IsFront(PhaseNames.Front)
                && !PhaseNames.IsWorld(PhaseNames.Front) && !PhaseNames.IsLoading(PhaseNames.Front)
                && PhaseNames.IsLoading(PhaseNames.Loading)
                && !PhaseNames.IsFront(PhaseNames.Loading) && !PhaseNames.IsWorld(PhaseNames.Loading)
                && PhaseNames.IsWorld(PhaseNames.World)
                && !PhaseNames.IsFront(PhaseNames.World) && !PhaseNames.IsLoading(PhaseNames.World));

            result.Check("unknown phases are reported as unknown (not silently treated as world)",
                !PhaseNames.IsKnown("lobby") && !PhaseNames.IsWorld("lobby")
                && !PhaseNames.IsKnown(null) && PhaseNames.IsKnown(PhaseNames.World));
        }

        /// <summary>交接判定表：**首次观测不动作**是所有规则的前提。</summary>
        private static void CasePlanTable(BtSelfTest.TestResult result)
        {
            PhaseStep first = PhaseHandover.Plan(null, PhaseNames.Front);
            result.Check("first observation never hands over",
                !first.Changed && !first.ReleaseInput && !first.ClearLaya && !first.GateTree,
                first.Describe());

            PhaseStep same = PhaseHandover.Plan(PhaseNames.World, PhaseNames.World);
            result.Check("same phase never hands over",
                !same.Changed && !same.ReleaseInput && !same.ClearLaya && !same.GateTree,
                same.Describe());

            PhaseStep inWorld = PhaseHandover.Plan(PhaseNames.Front, PhaseNames.World);
            result.Check("front -> world: release input + clear laya, tree runs",
                inWorld.Changed && inWorld.ReleaseInput && inWorld.ClearLaya && !inWorld.GateTree,
                inWorld.Describe());

            PhaseStep toFront = PhaseHandover.Plan(PhaseNames.World, PhaseNames.Front);
            result.Check("world -> front: release input + clear laya, tree runs (menu macro must run)",
                toFront.Changed && toFront.ReleaseInput && toFront.ClearLaya && !toFront.GateTree,
                toFront.Describe());

            PhaseStep toLoading = PhaseHandover.Plan(PhaseNames.Front, PhaseNames.Loading);
            result.Check("front -> loading: the transition gates the tree",
                toLoading.Changed && toLoading.ReleaseInput && toLoading.ClearLaya && toLoading.GateTree,
                toLoading.Describe());

            PhaseStep loaded = PhaseHandover.Plan(PhaseNames.Loading, PhaseNames.World);
            result.Check("loading -> world: gate opens (tree resumes at the same node)",
                loaded.Changed && loaded.ReleaseInput && loaded.ClearLaya && !loaded.GateTree,
                loaded.Describe());

            PhaseStep unknown = PhaseHandover.Plan(PhaseNames.World, "lobby");
            result.Check("unknown phase: conservative handover (release + clear, no gate)",
                unknown.Changed && unknown.ReleaseInput && unknown.ClearLaya && !unknown.GateTree,
                unknown.Describe());

            PhaseStep empty = PhaseHandover.Plan(null, null);
            result.Check("empty target defaults to front without handing over",
                !empty.Changed && empty.To == PhaseNames.Front, empty.Describe());
        }

        /// <summary>
        /// 去抖（G15）：单帧抖动**不许**触发交接；连续 N 帧才算。
        /// 这条不钉住的话，世界加载途中的一次 `Project == null` 抖动就会释放输入 + 清缓存。
        /// </summary>
        private static void CaseDebounce(BtSelfTest.TestResult result)
        {
            // 首次观测只记相位，不算切换
            var tracker = new PhaseTracker(3);
            PhaseStep init = tracker.Observe(PhaseNames.Front);
            result.Check("tracker: first observation records the phase without a handover",
                init.From == null && init.To == PhaseNames.Front && !init.Changed
                && tracker.Known && tracker.Changes == 0 && tracker.Phase == PhaseNames.Front,
                init.Describe());

            // 单帧抖动：1 帧新相位 → 不切；随后回到当前相位 → 候选作废、从头再数
            PhaseStep jitter = tracker.Observe(PhaseNames.Loading);
            result.Check("tracker: one jitter frame does not switch",
                !jitter.Changed && tracker.Phase == PhaseNames.Front && tracker.Changes == 0
                && tracker.Candidate == PhaseNames.Loading && tracker.CandidateFrames == 1,
                jitter.Describe());

            PhaseStep recovered = tracker.Observe(PhaseNames.Front);
            result.Check("tracker: returning to the current phase clears the candidate",
                !recovered.Changed && tracker.Candidate == null && tracker.CandidateFrames == 0
                && tracker.Changes == 0,
                recovered.Describe());

            // 两帧还不够（confirmFrames=3）
            tracker.Observe(PhaseNames.World);
            PhaseStep second = tracker.Observe(PhaseNames.World);
            result.Check("tracker: still waiting before the confirm frame count",
                !second.Changed && tracker.Candidate == PhaseNames.World && tracker.CandidateFrames == 2
                && tracker.Changes == 0,
                second.Describe());

            // 第三帧：承认切换，且交接内容与 Plan 完全一致
            PhaseStep committed = tracker.Observe(PhaseNames.World);
            PhaseStep expected = PhaseHandover.Plan(PhaseNames.Front, PhaseNames.World);
            result.Check("tracker: the Nth consecutive frame commits the switch",
                committed.Changed && tracker.Phase == PhaseNames.World && tracker.Changes == 1
                && committed.GateTree == expected.GateTree && committed.ReleaseInput && committed.ClearLaya
                && committed.Event == expected.Event,
                committed.Describe());

            // 认了的相位不再重复切换
            result.Check("tracker: the same phase after committing is a no-op",
                !tracker.Observe(PhaseNames.World).Changed && tracker.Changes == 1);

            // confirmFrames = 1：立刻切（排查用）
            var instant = new PhaseTracker(1);
            instant.Observe(PhaseNames.Front);
            PhaseStep immediate = instant.Observe(PhaseNames.Loading);
            result.Check("tracker: confirmFrames=1 switches immediately and gates the tree",
                immediate.Changed && immediate.GateTree && instant.Phase == PhaseNames.Loading
                && instant.Changes == 1,
                immediate.Describe());

            // 非法的确认帧数被夹到 1（0 或负数会让"等确认"永远等不到）
            var clamped = new PhaseTracker(0);
            clamped.Observe(PhaseNames.Front);
            result.Check("tracker: confirmFrames below 1 is clamped to 1 (never waits forever)",
                clamped.Observe(PhaseNames.World).Changed, "changes=" + clamped.Changes);

            result.Check("the runtime default debounce is sane (2..30 frames)",
                PlayerAiConfig.PhaseConfirmFrames >= 2 && PlayerAiConfig.PhaseConfirmFrames <= 30,
                "PhaseConfirmFrames=" + PlayerAiConfig.PhaseConfirmFrames);
        }

        /// <summary>
        /// **摘要与交接必须同口径**：摘要是喂给模型的，交接是驱动运行时的 ——
        /// 两处各判一次的话，边界那两帧就会出现"摘要说世界外、运行时按世界内交接"。
        /// </summary>
        private static void CaseDigestAgrees(BtSelfTest.TestResult result)
        {
            string front = StateDigestCompiler.Compile(new StateInputs { WorldLoaded = false });
            string loading = StateDigestCompiler.Compile(new StateInputs { WorldLoaded = true, HasPlayer = false });
            string world = StateDigestCompiler.Compile(new StateInputs { WorldLoaded = true, HasPlayer = true });

            result.Check("digest phase=front matches PhaseNames.Front",
                front.Contains("phase=" + PhaseNames.Front), front);
            result.Check("digest phase=loading matches PhaseNames.Loading",
                loading.Contains("phase=" + PhaseNames.Loading), loading);
            result.Check("digest phase=world matches PhaseNames.World",
                world.Contains("phase=" + PhaseNames.World), world);
        }

        /// <summary>事件行是复盘唯一的线索：必须写清"从哪到哪"，闸门还要写出来。</summary>
        private static void CaseEventText(BtSelfTest.TestResult result)
        {
            PhaseStep step = PhaseHandover.Plan(PhaseNames.Front, PhaseNames.Loading);
            result.Check("event names both phases",
                step.Event != null && step.Event.Contains("front") && step.Event.Contains("loading"),
                step.Event);
            result.Check("event mentions the gate when the tree is gated",
                step.Event.Contains("gated"), step.Event);
            result.Check("Describe() returns the event for a real change",
                step.Describe() == step.Event, step.Describe());
            result.Check("Describe() is readable when nothing changed",
                PhaseHandover.Plan(PhaseNames.World, PhaseNames.World).Describe().Contains("no change"),
                PhaseHandover.Plan(PhaseNames.World, PhaseNames.World).Describe());
        }
    }
}

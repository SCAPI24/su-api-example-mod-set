using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 动作层自检（P1）：**纯逻辑、不依赖游戏**——动作层不认识游戏类型，
    /// 所以用"记录型假执行器 + 假目标解析器 + 假守卫"就能把整条链路逐条钉住。
    ///
    /// 覆盖：verb 表与参数校验（错误码）、编译出的帧序列、确定性（同输入同输出）、
    /// 脚本解析（含守卫/onFail/repeat/未知参数）、执行器的按住-差异-松开语义、
    /// 看门狗超时、总超时熔丝、中断释放输入、语义目标必须现解析、轨迹留痕。
    /// </summary>
    public static class ActionSelfTest
    {
        private const float Dt = 1f / 60f;

        // ---------------------------------------------------------------- 测试替身

        /// <summary>记录每一次执行器调用的假执行器。</summary>
        private sealed class RecordingExecutor : IActionExecutor
        {
            public readonly List<string> Calls = new List<string>();
            public bool Ready = true;
            public bool RefuseEverything;

            public bool IsReady
            {
                get { return Ready; }
            }

            public bool HoldKeys(IReadOnlyList<string> keys, bool down)
            {
                if (RefuseEverything)
                    return false;
                var names = new List<string>();
                for (int i = 0; i < keys.Count; i++)
                    names.Add(keys[i]);
                names.Sort(StringComparer.Ordinal);
                Calls.Add("hold[" + string.Join(",", names) + "]=" + (down ? "1" : "0"));
                return true;
            }

            public bool PulseKey(string key)
            {
                if (RefuseEverything)
                    return false;
                Calls.Add("pulse[" + key + "]");
                return true;
            }

            public bool MouseAction(string button, string action)
            {
                if (RefuseEverything)
                    return false;
                Calls.Add("mouse[" + button + ":" + action + "]");
                return true;
            }

            public bool Wheel(int notches)
            {
                Calls.Add("wheel[" + notches.ToString(CultureInfo.InvariantCulture) + "]");
                return true;
            }

            public bool SelectSlot(int slot)
            {
                Calls.Add("slot[" + slot.ToString(CultureInfo.InvariantCulture) + "]");
                return true;
            }

            public bool Look(float yawDegrees, float pitchDegrees)
            {
                Calls.Add("look[" + yawDegrees.ToString("0.#", CultureInfo.InvariantCulture) + ","
                    + pitchDegrees.ToString("0.#", CultureInfo.InvariantCulture) + "]");
                return true;
            }

            public bool LookDelta(float yawDeltaDegrees, float pitchDeltaDegrees)
            {
                Calls.Add("lookdelta[" + yawDeltaDegrees.ToString("0.##", CultureInfo.InvariantCulture) + ","
                    + pitchDeltaDegrees.ToString("0.##", CultureInfo.InvariantCulture) + "]");
                return true;
            }

            public bool LookAt(float x, float y, float z)
            {
                Calls.Add("lookat[" + x.ToString("0.##", CultureInfo.InvariantCulture) + ","
                    + y.ToString("0.##", CultureInfo.InvariantCulture) + ","
                    + z.ToString("0.##", CultureInfo.InvariantCulture) + "]");
                return true;
            }

            public bool UiClick(string selector)
            {
                Calls.Add("uiclick[" + selector + "]");
                return true;
            }

            public bool TypeText(string text)
            {
                Calls.Add("text[" + text + "]");
                return true;
            }

            public void ReleaseAll()
            {
                Calls.Add("releaseAll");
            }

            public int Count(string prefix)
            {
                int total = 0;
                for (int i = 0; i < Calls.Count; i++)
                {
                    if (Calls[i].StartsWith(prefix, StringComparison.Ordinal))
                        total++;
                }
                return total;
            }

            public bool Has(string contains)
            {
                for (int i = 0; i < Calls.Count; i++)
                {
                    if (Calls[i].IndexOf(contains, StringComparison.Ordinal) >= 0)
                        return true;
                }
                return false;
            }

            public string Describe()
            {
                return string.Join(" ", Calls);
            }
        }

        /// <summary>只会解析 "aim" 的假解析器（把"必须现解析"这件事测出来）。</summary>
        private sealed class FakeResolver : IActionTargetResolver
        {
            public int Calls;
            public string LastTarget;

            public ActionTarget Resolve(string target, out string error)
            {
                Calls++;
                LastTarget = target;
                error = null;
                if (string.Equals(target, "aim", StringComparison.OrdinalIgnoreCase))
                    return ActionTarget.Cell(10, 64, -3);
                if (string.Equals(target, "entity", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target, "self", StringComparison.OrdinalIgnoreCase))
                    return ActionTarget.Cell(12, 64, -3);
                if (string.Equals(target, "first_food", StringComparison.OrdinalIgnoreCase))
                {
                    // 槽位用 X 回传（与 ScriptCompiler.CompileEat 的约定一致）
                    var food = new ActionTarget("cell") { X = 3, Y = 0, Z = 0 };
                    return food;
                }
                error = ActionErrorCodes.StepFailed + ": fake resolver cannot resolve '" + target + "'";
                return null;
            }
        }

        private sealed class FakeGuards : IActionGuardEvaluator
        {
            public bool Ready = true;
            public readonly HashSet<string> Failing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool IsReady
            {
                get { return Ready; }
            }

            public bool Evaluate(string guard, out string error)
            {
                error = null;
                if (!Ready)
                {
                    error = "evaluator not ready";
                    return false;
                }
                if (Failing.Contains(guard))
                {
                    error = "fake: guard marked as failing";
                    return false;
                }
                return true;
            }
        }

        private static ActionArgs Args(params object[] namesAndValues)
        {
            var args = new ActionArgs();
            for (int i = 0; i + 1 < namesAndValues.Length; i += 2)
                args.Set((string)namesAndValues[i], namesAndValues[i + 1]);
            return args;
        }

        private static string CompileError(string verb, ActionArgs args, IActionTargetResolver resolver = null)
        {
            string error;
            ScriptCompiler.CompileStep(verb, args, resolver, out error);
            return error;
        }

        private static string CodeOf(string error)
        {
            if (string.IsNullOrEmpty(error))
                return null;
            int colon = error.IndexOf(':');
            return colon > 0 ? error.Substring(0, colon) : error;
        }

        private static void RunToEnd(ActionQueuePlayer player, int maxFrames = 4000)
        {
            int frames = 0;
            while (!player.IsFinished && frames++ < maxFrames)
                player.Tick(Dt);
        }

        // ---------------------------------------------------------------- 用例

        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "ActionSelfTest" };

            try { CaseVerbTable(result); } catch (Exception e) { result.Check("case:verb table", false, e.Message); }
            try { CaseParameterValidation(result); } catch (Exception e) { result.Check("case:parameter validation", false, e.Message); }
            try { CaseClips(result); } catch (Exception e) { result.Check("case:clips", false, e.Message); }
            try { CaseDeterminism(result); } catch (Exception e) { result.Check("case:determinism", false, e.Message); }
            try { CaseTargetResolution(result); } catch (Exception e) { result.Check("case:target resolution", false, e.Message); }
            try { CaseScriptParsing(result); } catch (Exception e) { result.Check("case:script parsing", false, e.Message); }
            try { CaseExecution(result); } catch (Exception e) { result.Check("case:execution", false, e.Message); }
            try { CaseHoldDiffSemantics(result); } catch (Exception e) { result.Check("case:hold diff semantics", false, e.Message); }
            try { CaseWatchdog(result); } catch (Exception e) { result.Check("case:watchdog", false, e.Message); }
            try { CaseTotalTimeout(result); } catch (Exception e) { result.Check("case:total timeout", false, e.Message); }
            try { CaseGuards(result); } catch (Exception e) { result.Check("case:guards", false, e.Message); }
            try { CaseGuardRules(result); } catch (Exception e) { result.Check("case:guard rules", false, e.Message); }
            try { CaseAbortReleases(result); } catch (Exception e) { result.Check("case:abort releases", false, e.Message); }
            try { CaseRepeat(result); } catch (Exception e) { result.Check("case:repeat", false, e.Message); }
            try { CaseRefusedInjection(result); } catch (Exception e) { result.Check("case:refused injection", false, e.Message); }
            try { CaseRunActionScriptRegistry(result); } catch (Exception e) { result.Check("case:RunActionScript registry", false, e.Message); }
            try { CaseRunActionScriptNode(result); } catch (Exception e) { result.Check("case:RunActionScript node", false, e.Message); }
            try { CaseRunActionScriptFromBlackboard(result); } catch (Exception e) { result.Check("case:RunActionScript from blackboard", false, e.Message); }
            try { CaseRunActionScriptCompiles(result); } catch (Exception e) { result.Check("case:RunActionScript compiles from a package", false, e.Message); }
            try { CaseScriptLibrary(result); } catch (Exception e) { result.Check("case:script library", false, e.Message); }
            try { CaseShippedTemplates(result); } catch (Exception e) { result.Check("case:shipped script templates", false, e.Message); }
            try { CaseLongStepNotKilled(result); } catch (Exception e) { result.Check("case:long step budget", false, e.Message); }
            try { CaseNoStraySlotChange(result); } catch (Exception e) { result.Check("case:no stray slot change", false, e.Message); }
            try { CaseAimObservationParsing(result); } catch (Exception e) { result.Check("case:aim observation parsing", false, e.Message); }

            return result;
        }

        private static void CaseVerbTable(BtSelfTest.TestResult result)
        {
            ScriptCompiler.EnsureInitialized();
            IReadOnlyList<string> names = ScriptCompiler.VerbNames;
            result.Check("verb table is non-empty", names.Count >= 15, "count=" + names.Count);

            foreach (string expected in new[]
            {
                "wait", "move", "turn", "lookdir", "lookAt", "hotbar", "jump", "sneak",
                "dig", "hit", "interact", "attack", "aim", "drop", "ui", "sleep", "eat", "mount", "fly"
            })
            {
                ActionVerbSpec spec;
                result.Check("verb registered: " + expected, ScriptCompiler.TryGetVerb(expected, out spec));
            }

            // 前缀归一化：action.move / Move / MOVE 都是 move
            ActionVerbSpec spec2;
            result.Check("verb prefix is normalized (action.move)",
                ScriptCompiler.TryGetVerb("action.move", out spec2) && spec2.Name == "move");
            result.Check("verb name is case-insensitive (MOVE)",
                ScriptCompiler.TryGetVerb("MOVE", out spec2) && spec2.Name == "move");

            // 每个 verb 都要有分类与说明（编辑器物料区要用）
            foreach (string name in names)
            {
                ActionVerbSpec spec;
                ScriptCompiler.TryGetVerb(name, out spec);
                if (spec == null || string.IsNullOrEmpty(spec.Category) || string.IsNullOrEmpty(spec.Description))
                {
                    result.Check("verb has category+description: " + name, false);
                    return;
                }
            }
            result.Check("every verb has category+description", true);

            // 槽位键名与游戏一致（1..10，10 = Number0）
            result.Check("slot key 1 = Number1", ScriptCompiler.SlotKey(1) == "Number1");
            result.Check("slot key 10 = Number0", ScriptCompiler.SlotKey(10) == "Number0");
            result.Check("slot key out of range = null", ScriptCompiler.SlotKey(0) == null
                && ScriptCompiler.SlotKey(11) == null);
        }

        private static void CaseParameterValidation(BtSelfTest.TestResult result)
        {
            result.Check("unknown verb -> verb_unknown",
                CodeOf(CompileError("nosuchverb", null)) == ActionErrorCodes.VerbUnknown);

            result.Check("unknown parameter -> invalid_argument",
                CodeOf(CompileError("wait", Args("ms", 100, "bogus", 1))) == ActionErrorCodes.InvalidArgument);

            result.Check("missing required parameter -> invalid_argument",
                CodeOf(CompileError("move", Args("ms", 100))) == ActionErrorCodes.InvalidArgument);

            result.Check("negative duration -> invalid_argument",
                CodeOf(CompileError("wait", Args("ms", -5))) == ActionErrorCodes.InvalidArgument);

            result.Check("duration over per-step limit -> invalid_argument",
                CodeOf(CompileError("wait", Args("ms", ActionCompileDefaults.MaxTotalMs + 1)))
                == ActionErrorCodes.InvalidArgument);

            result.Check("bad direction -> invalid_argument",
                CodeOf(CompileError("move", Args("dir", "sideways", "ms", 100)))
                == ActionErrorCodes.InvalidArgument);

            result.Check("hotbar slot out of range -> invalid_argument",
                CodeOf(CompileError("hotbar", Args("slot", 11))) == ActionErrorCodes.InvalidArgument);

            result.Check("ui without any action -> invalid_argument",
                CodeOf(CompileError("ui", Args("wheel", 0))) == ActionErrorCodes.InvalidArgument);

            result.Check("turn without yaw or delta -> invalid_argument",
                CodeOf(CompileError("turn", Args("ms", 100))) == ActionErrorCodes.InvalidArgument);

            // 正例：默认值生效
            string error;
            ActionClip moveDefault = ScriptCompiler.CompileStep("move", Args("dir", "f", "ms", 200), null, out error);
            result.Check("move with defaults compiles", moveDefault != null && error == null, error);
            result.Check("move default duration honoured",
                moveDefault != null && Math.Abs(moveDefault.DurationSeconds - 0.2) < 0.06,
                moveDefault != null ? moveDefault.DurationSeconds.ToString("0.000") : "null");
        }

        private static void CaseClips(BtSelfTest.TestResult result)
        {
            string error;

            ActionClip wait = ScriptCompiler.CompileStep("wait", Args("ms", 300), null, out error);
            result.Check("wait: ~300ms of frames", wait != null
                && Math.Abs(wait.DurationSeconds - 0.3) < 0.06
                && wait.FrameCount >= 5, wait != null ? wait.Describe() : error);

            ActionClip hotbar = ScriptCompiler.CompileStep("hotbar", Args("slot", 4), null, out error);
            result.Check("hotbar: preselects slot 4", hotbar != null && hotbar.FrameCount == 1
                && hotbar.Frames[0].SelectSlot == 4
                && hotbar.Frames[0].KeysPressed != null && hotbar.Frames[0].KeysPressed[0] == "Number4",
                hotbar != null ? "slot=" + hotbar.Frames[0].SelectSlot : error);

            ActionClip forward = ScriptCompiler.CompileStep("move", Args("dir", "f", "ms", 200), null, out error);
            bool holdsW = false;
            if (forward != null)
            {
                for (int i = 0; i < forward.FrameCount; i++)
                {
                    string[] held = forward.Frames[i].KeysHeld;
                    if (held != null)
                    {
                        for (int k = 0; k < held.Length; k++)
                        {
                            if (held[k] == "W")
                                holdsW = true;
                        }
                    }
                }
            }
            result.Check("move f holds W", holdsW);

            ActionClip diagonal = ScriptCompiler.CompileStep("move", Args("dir", "fl", "ms", 100), null, out error);
            bool holdsWAndA = false;
            if (diagonal != null && diagonal.FrameCount > 0 && diagonal.Frames[0].KeysHeld != null)
            {
                var keys = new List<string>(diagonal.Frames[0].KeysHeld);
                holdsWAndA = keys.Contains("W") && keys.Contains("A");
            }
            result.Check("move fl holds W+A", holdsWAndA);

            ActionClip lookTurn = ScriptCompiler.CompileStep("turn", Args("yaw", 90, "pitch", 10), null, out error);
            result.Check("turn sets absolute look",
                lookTurn != null && lookTurn.FrameCount == 1 && lookTurn.Frames[0].HasAbsoluteLook
                && Math.Abs(lookTurn.Frames[0].YawDegrees - 90f) < 0.01f, error);

            ActionClip lookDelta = ScriptCompiler.CompileStep("turn", Args("dyaw", 180), null, out error);
            result.Check("turn dyaw becomes a look delta in radians",
                lookDelta != null && Math.Abs(lookDelta.Frames[0].LookDeltaYaw - (float)Math.PI) < 0.01f, error);

            ActionClip dig = ScriptCompiler.CompileStep("dig", Args("ms", 500), null, out error);
            bool digsWithLeft = false;
            if (dig != null)
            {
                for (int i = 0; i < dig.FrameCount; i++)
                {
                    if (dig.Frames[i].MouseAction == "down" && dig.Frames[i].MouseButtonName == "left")
                        digsWithLeft = true;
                }
            }
            result.Check("dig holds left mouse then releases", digsWithLeft
                && dig.Frames[dig.FrameCount - 1].MouseAction == "up");

            ActionClip ui = ScriptCompiler.CompileStep("ui",
                Args("click", "Play", "text", "world1", "wheel", -3), null, out error);
            bool hasClick = false, hasText = false, hasWheel = false;
            if (ui != null)
            {
                for (int i = 0; i < ui.FrameCount; i++)
                {
                    if (!string.IsNullOrEmpty(ui.Frames[i].UiClick)) hasClick = true;
                    if (!string.IsNullOrEmpty(ui.Frames[i].TypeText)) hasText = true;
                    if (ui.Frames[i].Wheel != 0) hasWheel = true;
                }
            }
            result.Check("ui compiles click+text+wheel", hasClick && hasText && hasWheel);

            ActionClip eat = ScriptCompiler.CompileStep("eat", null, new FakeResolver(), out error);
            result.Check("eat resolves first_food into a slot", eat != null && eat.FrameCount > 0
                && eat.Frames[0].SelectSlot == 3, error);
        }

        private static void CaseDeterminism(BtSelfTest.TestResult result)
        {
            string e1, e2;
            ActionClip first = ScriptCompiler.CompileStep("dig", Args("ms", 400, "target", "aim"),
                new FakeResolver(), out e1);
            ActionClip second = ScriptCompiler.CompileStep("dig", Args("ms", 400, "target", "aim"),
                new FakeResolver(), out e2);

            bool same = first != null && second != null && first.FrameCount == second.FrameCount
                && Math.Abs(first.DurationSeconds - second.DurationSeconds) < 0.0001;
            if (same)
            {
                for (int i = 0; i < first.FrameCount && same; i++)
                {
                    ActionFrame a = first.Frames[i];
                    ActionFrame b = second.Frames[i];
                    same = a.DeltaMs == b.DeltaMs && a.HasLookAt == b.HasLookAt
                        && Math.Abs(a.LookAtX - b.LookAtX) < 0.0001f
                        && Math.Abs(a.LookAtY - b.LookAtY) < 0.0001f
                        && Math.Abs(a.LookAtZ - b.LookAtZ) < 0.0001f
                        && string.Equals(a.MouseAction, b.MouseAction, StringComparison.Ordinal);
                }
            }
            result.Check("same verb+args compile to the identical clip (reproducible)", same);
        }

        private static void CaseTargetResolution(BtSelfTest.TestResult result)
        {
            // 没有解析器时，语义目标必须失败（绝不猜）
            string error = CompileError("dig", Args("ms", 100, "target", "aim"), null);
            result.Check("semantic target without resolver -> step_failed",
                CodeOf(error) == ActionErrorCodes.StepFailed, error);

            // 显式坐标不需要解析器
            error = null;
            ActionClip explicitTarget = ScriptCompiler.CompileStep("dig",
                Args("ms", 100, "target", "1,2,3"), null, out error);
            bool looked = false;
            if (explicitTarget != null)
            {
                for (int i = 0; i < explicitTarget.FrameCount; i++)
                {
                    if (explicitTarget.Frames[i].HasLookAt
                        && Math.Abs(explicitTarget.Frames[i].LookAtX - 1f) < 0.001f)
                    {
                        looked = true;
                    }
                }
            }
            result.Check("explicit x,y,z target compiles without a resolver", looked, error);

            // 解析器拒绝时，编译失败（不产出半截 clip）
            var resolver = new FakeResolver();
            string rejected = CompileError("dig", Args("ms", 100, "target", "unknown_thing"), resolver);
            result.Check("resolver rejection -> step_failed",
                CodeOf(rejected) == ActionErrorCodes.StepFailed, rejected);

            // 执行时才解析：Start 之前不该调用解析器，进入该步时才调
            var recording = new RecordingExecutor();
            var lateResolver = new FakeResolver();
            var player = new ActionQueuePlayer(recording, lateResolver, new FakeGuards());
            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"id\":\"t\",\"steps\":["
                + "{\"verb\":\"wait\",\"ms\":50},{\"verb\":\"dig\",\"ms\":200,\"target\":\"aim\"}]}");
            result.Check("script for target test parses", parsed.Ok, parsed.Error);

            player.Start(parsed.Script);
            result.Check("resolver not called before the aiming step runs", lateResolver.Calls == 0,
                "calls=" + lateResolver.Calls);
            RunToEnd(player);
            result.Check("resolver called when the aiming step executed", lateResolver.Calls >= 1,
                "calls=" + lateResolver.Calls);
            result.Check("resolver saw the semantic target 'aim'", lateResolver.LastTarget == "aim",
                lateResolver.LastTarget);
        }

        private static void CaseScriptParsing(BtSelfTest.TestResult result)
        {
            const string json = @"{
              ""format"": ""aea"", ""version"": 1, ""id"": ""mine_stone_once"",
              ""name"": ""挖一块石头"",
              ""guards"": [""player.alive"", ""world.loaded"", ""modal.none""],
              ""onFail"": ""retry:2"",
              ""steps"": [
                { ""verb"": ""hotbar"", ""slot"": 1 },
                { ""verb"": ""lookAt"", ""target"": ""aim"" },
                { ""verb"": ""dig"", ""ms"": 900, ""repeat"": 2, ""timeoutMs"": 3000 },
                { ""verb"": ""wait"", ""ms"": 120, ""continueOnFail"": true }
              ] }";

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(json, "test");
            result.Check("valid script parses", parsed.Ok, parsed.Error);
            if (parsed.Ok)
            {
                ActionScript script = parsed.Script;
                result.Check("script id/name read", script.Id == "mine_stone_once" && script.Name == "挖一块石头");
                result.Check("guards parsed", script.Guards.Count == 3);
                result.Check("onFail parsed (retry:2)", script.OnFail == "retry:2");
                result.Check("steps parsed", script.Steps.Count == 4);
                result.Check("repeat parsed", script.Steps[2].Repeat == 2);
                result.Check("timeoutMs parsed", script.Steps[2].TimeoutMs == 3000);
                result.Check("continueOnFail parsed", script.Steps[3].ContinueOnFail);
                result.Check("control fields not treated as verb params",
                    !script.Steps[2].Args.Has("repeat") && script.Steps[2].Args.Has("ms"));
                result.Check("verb name canonicalized", script.Steps[0].Verb == "hotbar");
            }

            result.Check("missing steps -> error",
                !ActionScriptParser.ParseJson("{\"format\":\"aea\",\"version\":1}").Ok);

            result.Check("unsupported version -> error",
                !ActionScriptParser.ParseJson(
                    "{\"format\":\"aea\",\"version\":99,\"steps\":[{\"verb\":\"wait\",\"ms\":1}]}").Ok);

            result.Check("unknown guard -> error",
                !ActionScriptParser.ParseJson(
                    "{\"format\":\"aea\",\"version\":1,\"guards\":[\"nonsense\"],"
                    + "\"steps\":[{\"verb\":\"wait\",\"ms\":1}]}").Ok);

            result.Check("unknown onFail -> error",
                !ActionScriptParser.ParseJson(
                    "{\"format\":\"aea\",\"version\":1,\"onFail\":\"explode\","
                    + "\"steps\":[{\"verb\":\"wait\",\"ms\":1}]}").Ok);

            result.Check("unknown verb in step -> error",
                !ActionScriptParser.ParseJson(
                    "{\"format\":\"aea\",\"version\":1,\"steps\":[{\"verb\":\"teleport\"}]}").Ok);

            result.Check("unknown param in step -> error",
                !ActionScriptParser.ParseJson(
                    "{\"format\":\"aea\",\"version\":1,\"steps\":[{\"verb\":\"wait\",\"ms\":1,\"warp\":9}]}").Ok);

            result.Check("too many steps -> error",
                !ActionScriptParser.ParseJson(BuildTooManySteps()).Ok);

            result.Check("malformed json -> error", !ActionScriptParser.ParseJson("{oops").Ok);
        }

        private static string BuildTooManySteps()
        {
            var text = new System.Text.StringBuilder();
            text.Append("{\"format\":\"aea\",\"version\":1,\"steps\":[");
            for (int i = 0; i <= ActionCompileDefaults.MaxSteps; i++)
            {
                if (i > 0)
                    text.Append(',');
                text.Append("{\"verb\":\"wait\",\"ms\":1}");
            }
            text.Append("]}");
            return text.ToString();
        }

        private static void CaseExecution(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var guards = new FakeGuards();
            var player = new ActionQueuePlayer(executor, new FakeResolver(), guards);

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"id\":\"exec\",\"steps\":["
                + "{\"verb\":\"hotbar\",\"slot\":2},"
                + "{\"verb\":\"move\",\"dir\":\"f\",\"ms\":200},"
                + "{\"verb\":\"wait\",\"ms\":100}]}");

            result.Check("execution script parses", parsed.Ok, parsed.Error);
            bool started = player.Start(parsed.Script);
            result.Check("script starts", started, player.LastError);

            RunToEnd(player);
            result.Check("script succeeds", player.State == ActionPlayState.Succeeded,
                player.State + " " + player.LastError);
            result.Check("slot was selected", executor.Has("slot[2]"), executor.Describe());
            result.Check("forward key was pressed (down)", executor.Has("hold[W]=1"), executor.Describe());
            result.Check("forward key was released (up)", executor.Has("hold[W]=0"), executor.Describe());
            result.Check("input released at the end", executor.Has("releaseAll"), executor.Describe());
            result.Check("trace mentions the steps", player.Trace.Contains("step0:hotbar")
                && player.Trace.Contains("step1:move"));
            result.Check("total elapsed is plausible",
                player.ElapsedMs > 200 && player.ElapsedMs < 1200,
                player.ElapsedMs.ToString("0") + "ms");

            // 执行器不可用：开局即失败，且错误码是 inject_refused
            var dead = new RecordingExecutor { Ready = false };
            var player2 = new ActionQueuePlayer(dead, null, new FakeGuards());
            bool started2 = player2.Start(parsed.Script);
            result.Check("not-ready executor fails at start", !started2
                && player2.ErrorCode == ActionErrorCodes.InjectRefused, player2.ErrorCode);

            // 空脚本
            var player3 = new ActionQueuePlayer(new RecordingExecutor(), null, null);
            result.Check("null script fails", !player3.Start(null));
            result.Check("null script error code", player3.ErrorCode == ActionErrorCodes.InvalidArgument,
                player3.ErrorCode);
        }

        private static void CaseHoldDiffSemantics(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var player = new ActionQueuePlayer(executor, null, new FakeGuards());

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"steps\":["
                + "{\"verb\":\"move\",\"dir\":\"f\",\"ms\":150},"
                + "{\"verb\":\"move\",\"dir\":\"fl\",\"ms\":150}]}");
            player.Start(parsed.Script);
            RunToEnd(player);

            // 断言口径（自检用仪器实测校准过）：
            //   · Start() 会先做一次"清掉上次残留"的 releaseAll，所以 W 的按下发生在之后；
            //   · 进入第二步时**不允许**出现 "hold[...]=0"（那会把还该按着的键松开 → 走一下停一下）；
            //   · 第二步要额外按下 A，且 W 的按下次数仍为 1（没有"松开再按"）。
            int releaseBatches = 0;
            for (int i = 0; i < executor.Calls.Count; i++)
            {
                string call = executor.Calls[i];
                if (call.StartsWith("hold[", StringComparison.Ordinal) && call.EndsWith("]=0", StringComparison.Ordinal))
                    releaseBatches++;
            }
            result.Check("no key release at the step boundary (would stutter)",
                releaseBatches == 1, "releaseCalls=" + releaseBatches + " :: " + executor.Describe());
            result.Check("second step presses the extra key", executor.Has("hold[A]=1"), executor.Describe());

            int wDown = 0;
            for (int i = 0; i < executor.Calls.Count; i++)
            {
                if (executor.Calls[i] == "hold[W]=1")
                    wDown++;
            }
            result.Check("W pressed exactly once across both steps (stays held)", wDown == 1,
                "down=" + wDown + " :: " + executor.Describe());
        }

        private static void CaseWatchdog(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var worker = new ActionQueuePlayer(executor, null, new FakeGuards());
            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"steps\":["
                + "{\"verb\":\"move\",\"dir\":\"f\",\"ms\":600,\"timeoutMs\":100}]}");
            worker.Start(parsed.Script);
            // timeoutMs=100 远小于编译出的 600ms 时长 → 必须被超时掐掉。
            // 注意：脚本级总超时也按 100ms 算（显式 timeoutMs 同时是这一步的预算），
            // 所以断言只要求"超时失败 + 指明是这一步"，不区分是步骤看门狗还是总熔丝。
            for (int i = 0; i < 30; i++)
                worker.Tick(Dt);

            result.Check("step watchdog fails the script", worker.State == ActionPlayState.Failed);
            result.Check("watchdog error code is timeout", worker.ErrorCode == ActionErrorCodes.Timeout,
                worker.ErrorCode);
            result.Check("watchdog reports the step index", worker.FailedStepIndex == 0,
                "step=" + worker.FailedStepIndex);
            result.Check("watchdog released the held key", executor.Has("hold[W]=0"), executor.Describe());
            result.Check("watchdog trace names the step and verb",
                worker.Trace.Contains("step0:move") && worker.Trace.Contains("fail@0:timeout"),
                worker.Trace);
            result.Check("watchdog message names the step and verb",
                worker.LastError != null && worker.LastError.Contains("step 0")
                && worker.LastError.Contains("move"), worker.LastError);
            result.Check("watchdog trace reports the real duration (not 0ms)",
                worker.Trace.Contains("dur=600ms"), worker.Trace);
        }

        private static void CaseTotalTimeout(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var player = new ActionQueuePlayer(executor, null, new FakeGuards());

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"steps\":["
                + "{\"verb\":\"wait\",\"ms\":200,\"timeoutMs\":5000},"
                + "{\"verb\":\"wait\",\"ms\":200,\"timeoutMs\":5000}]}");
            player.Start(parsed.Script);
            // 预算现在由"每步编译后的看门狗预算"累加（TotalTimeoutMs 只是它的起始值）。
            // 用显式的调试入口把它压小，验证脚本级熔丝仍然有效。
            player.DebugSetBudgetMs(50);
            RunToEnd(player);

            result.Check("total timeout fuse trips", player.State == ActionPlayState.Failed
                && player.ErrorCode == ActionErrorCodes.Timeout, player.ErrorCode);
            result.Check("total timeout released input", executor.Has("releaseAll"), executor.Describe());
        }

        /// <summary>
        /// 回归（**实机跑出来的真 bug**）：没写 `timeoutMs` 的长动作**不能被脚本级熔丝误杀**。
        ///
        /// 早期实现把"每步 1000ms"当默认预算，于是 `move ms=3000` 会在 1 秒时被
        /// "script exceeded total timeout (1000 ms)" 掐掉 —— 脚本看起来"莫名失败"。
        /// 现在预算在每步编译后按"时长 × 系数"累加，长动作自然有足够的预算。
        /// </summary>
        private static void CaseLongStepNotKilled(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var player = new ActionQueuePlayer(executor, null, new FakeGuards());

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"steps\":[{\"verb\":\"move\",\"dir\":\"f\",\"ms\":3000}]}");
            player.Start(parsed.Script);
            double budgetAtStart = player.BudgetMs;
            RunToEnd(player);

            result.Check("a 3s move without an explicit timeout is not killed by the total fuse",
                player.State == ActionPlayState.Succeeded, player.State + " " + player.ErrorCode
                + " " + player.LastError);
            result.Check("the script budget covers the compiled step duration",
                player.BudgetMs >= 3000, "budget=" + player.BudgetMs);
            result.Check("budget grew beyond its starting value as steps were compiled",
                player.BudgetMs >= budgetAtStart, "start=" + budgetAtStart + " now=" + player.BudgetMs);
            result.Check("the long step actually ran to completion (key released)",
                executor.Has("hold[W]=1") && executor.Has("hold[W]=0"), executor.Describe());
        }

        private static void CaseGuards(BtSelfTest.TestResult result)
        {
            var guards = new FakeGuards();
            var executor = new RecordingExecutor();
            var player = new ActionQueuePlayer(executor, null, guards);

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"guards\":[\"player.alive\"],\"steps\":["
                + "{\"verb\":\"wait\",\"ms\":200}]}");

            guards.Failing.Add("player.alive");
            bool started = player.Start(parsed.Script);
            result.Check("guard failing at start -> no execution", !started
                && player.ErrorCode == ActionErrorCodes.GuardFailed, player.ErrorCode);

            guards.Failing.Clear();
            player.Start(parsed.Script);
            player.Tick(Dt);
            guards.Failing.Add("player.alive");
            player.Tick(Dt);
            result.Check("guard failing mid-run stops the script",
                player.State == ActionPlayState.Failed && player.ErrorCode == ActionErrorCodes.GuardFailed,
                player.State + " " + player.ErrorCode);

            // 守卫需要判定器但没有 → 明确失败，不当成"没有守卫"
            var noGuards = new ActionQueuePlayer(new RecordingExecutor(), null, null);
            bool started2 = noGuards.Start(parsed.Script);
            result.Check("guards required but evaluator missing -> fail", !started2
                && noGuards.ErrorCode == ActionErrorCodes.GuardFailed, noGuards.ErrorCode);
        }

        /// <summary>
        /// **守卫判定规则**（`ActionGuardRules`）：`screen.is:` / `element.*` 这两类以前一律回
        /// `target_unavailable`，现在真判了 —— 判定规则是纯逻辑，所以这里逐条钉住，
        /// 包括"判不了绝不等于通过"这条铁律（UI 查询失败时必须判不通过）。
        /// </summary>
        private static void CaseGuardRules(BtSelfTest.TestResult result)
        {
            var context = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["worldLoaded"] = true,
                ["hasPlayer"] = true,
                ["playerAlive"] = true,
                ["sleeping"] = false,
                ["modalOpen"] = false,
                ["modalPanel"] = null,
                ["dialogsOpen"] = false,
                ["screen"] = "MainMenu"
            };

            var clicked = new List<string>();
            Func<string, UiElementFacts> probe = delegate(string selector)
            {
                clicked.Add(selector);
                if (selector == "Play") return UiElementFacts.Found(true, true, null);
                if (selector == "Buy") return UiElementFacts.Found(false, false, "off-screen");
                if (selector == "Ghost") return UiElementFacts.Absent("No element matches 'Ghost'.");
                return UiElementFacts.Unknown("UI is not ready yet");
            };

            bool value;
            string error;

            result.Check("screen.is: matches the current screen (case-insensitive)",
                Evaluated("screen.is:MainMenu", context, probe, out value, out error) && value,
                error);
            result.Check("*** screen.is: fails with the ACTUAL screen name in the reason ***",
                Evaluated("screen.is:Game", context, probe, out value, out error) && !value
                && error != null && error.Contains("MainMenu") && error.Contains("Game"),
                error);
            result.Check("screen.is: needs an argument (empty stays unrecognised, never 'passes')",
                !ActionGuardRules.TryEvaluate("screen.is:", context, probe, out value, out error),
                error);

            result.Check("element.present: passes for an element that is there",
                Evaluated("element.present:Play", context, probe, out value, out error) && value, error);
            result.Check("element.present: passes even when it is not clickable (present is present)",
                Evaluated("element.present:Buy", context, probe, out value, out error) && value, error);
            result.Check("element.clickable: fails for an element that is there but not clickable",
                Evaluated("element.clickable:Buy", context, probe, out value, out error) && !value
                && error != null && error.Contains("not clickable"), error);
            result.Check("element.hittable: fails for an element nothing hits at",
                Evaluated("element.hittable:Buy", context, probe, out value, out error) && !value
                && error != null && error.Contains("nothing hits"), error);
            result.Check("element.present: fails for a missing element and says so",
                Evaluated("element.present:Ghost", context, probe, out value, out error) && !value
                && error != null && error.Contains("No element matches"), error);
            result.Check("*** element.* on a query that could not run: never passes ***",
                Evaluated("element.clickable:Whatever", context, probe, out value, out error) && !value
                && error != null && error.Contains("cannot tell"), error);
            result.Check("a missing probe (game side unavailable) is also 'cannot tell'",
                Evaluated("element.clickable:Play", context, null, out value, out error) && !value
                && error != null && error.Contains("cannot tell"), error);
            result.Check("the probe is only asked when an element guard is evaluated",
                clicked.Count == 6, "probes=" + clicked.Count);

            // 与 waitFor 同源的固定词汇仍然照旧
            result.Check("the fixed vocabulary still works (world/player/modal/dialog/sleep)",
                Evaluated("world.loaded", context, probe, out value, out error) && value
                && Evaluated("modal.none", context, probe, out value, out error) && value
                && Evaluated("dialog.none", context, probe, out value, out error) && value
                && Evaluated("player.awake", context, probe, out value, out error) && value
                && Evaluated("player.dead", context, probe, out value, out error) && !value,
                error);

            // ---- `aim.*`：准星闸门（第 41 轮的结论：可行性由 C# 把住，Laya 只决定"做哪一件"）
            result.Check("*** aim.block passes only when a block is really under the crosshair ***",
                Evaluated("aim.block", WithAim(context, "block"), probe, out value, out error) && value
                && Evaluated("aim.entity", WithAim(context, "block"), probe, out value, out error) && !value
                && error != null && error.Contains("block"), error);
            result.Check("aim.none passes when nothing is aimed at",
                Evaluated("aim.none", WithAim(context, "none"), probe, out value, out error) && value,
                error);
            result.Check("*** no aim fact at all -> 'cannot tell', never a pass ***",
                Evaluated("aim.block", context, probe, out value, out error) && !value
                && error != null && error.Contains("cannot tell"), error);
            result.Check("the aim guards are advertised in the vocabulary",
                Array.IndexOf(ActionGuards.Names, ActionGuards.AimBlock) >= 0
                && ActionGuardRules.DescribeWired().Contains("aim.block"),
                ActionGuardRules.DescribeWired());

            result.Check("modal.is: still matches by substring (面板名带 Widget 后缀也能判)",
                Evaluated("modal.is:Inventory", WithModal(context, "FullInventoryWidget"), probe,
                    out value, out error) && value, error);
            result.Check("no modal open -> modal.none passes and modal.is: fails",
                Evaluated("modal.is:Anything", context, probe, out value, out error) && !value
                && error != null && error.Contains("<none>"), error);

            result.Check("an unknown guard is reported as unrecognised (caller says target_unavailable)",
                !ActionGuardRules.TryEvaluate("teleport.to:12", context, probe, out value, out error),
                error);

            // ---- `events.since:`（与 `obs.waitFor` 同词汇同判定；本轮补上的分歧）
            var withEvents = new Dictionary<string, object>(context, StringComparer.Ordinal)
            {
                ["eventSeq"] = 40L
            };
            result.Check("*** events.since: is wired (same predicate as obs.waitFor) ***",
                Evaluated("events.since:39", withEvents, probe, out value, out error) && value
                && Evaluated("events.since:40", withEvents, probe, out value, out error) && !value,
                error);
            result.Check("...and says how far the ring has got when it fails",
                Evaluated("events.since:40", withEvents, probe, out value, out error)
                && error != null && error.Contains("#40"), error);
            result.Check("a malformed events.since: is refused with the expected syntax",
                Evaluated("events.since:abc", withEvents, probe, out value, out error) && !value
                && error != null && error.Contains("numeric"), error);
            result.Check("*** no event sequence available -> 'cannot tell', never a pass ***",
                Evaluated("events.since:0", context, probe, out value, out error) && !value
                && error != null && error.Contains("cannot tell"), error);

            result.Check("the wired list is advertised (用于 target_unavailable 的提示文案)",
                ActionGuardRules.DescribeWired().Contains("screen.is:") 
                && ActionGuardRules.DescribeWired().Contains("element.clickable:")
                && ActionGuardRules.DescribeWired().Contains("events.since:"),
                ActionGuardRules.DescribeWired());
            result.Check("the guard vocabulary advertises exactly the wired prefixes",
                Array.IndexOf(ActionGuards.Prefixes, ActionGuardRules.ElementHittablePrefix) >= 0
                && Array.IndexOf(ActionGuards.Prefixes, ActionGuardRules.EventsSincePrefix) >= 0,
                string.Join(",", ActionGuards.Prefixes));
        }

        private static Dictionary<string, object> WithAim(Dictionary<string, object> context, string kind)
        {
            return new Dictionary<string, object>(context, StringComparer.Ordinal) { ["aimKind"] = kind };
        }
        private static Dictionary<string, object> WithModal(Dictionary<string, object> context, string panel)
        {
            var copy = new Dictionary<string, object>(context, StringComparer.Ordinal);
            copy["modalOpen"] = true;
            copy["modalPanel"] = panel;
            return copy;
        }

        private static bool Evaluated(string guard, Dictionary<string, object> context,
            Func<string, UiElementFacts> probe, out bool value, out string error)
        {
            return ActionGuardRules.TryEvaluate(guard, context, probe, out value, out error);
        }

        private static void CaseAbortReleases(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var player = new ActionQueuePlayer(executor, null, new FakeGuards());

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"steps\":[{\"verb\":\"move\",\"dir\":\"f\",\"ms\":2000}]}");
            player.Start(parsed.Script);
            for (int i = 0; i < 10; i++)
                player.Tick(Dt);

            result.Check("key is held while running", executor.Has("hold[W]=1"), executor.Describe());
            player.Abort("human took over");
            result.Check("abort marks the state", player.State == ActionPlayState.Aborted);
            result.Check("abort error code", player.ErrorCode == ActionErrorCodes.Aborted, player.ErrorCode);
            result.Check("abort released the key", executor.Has("hold[W]=0"), executor.Describe());
            result.Check("abort called ReleaseAll", executor.Has("releaseAll"), executor.Describe());
            result.Check("tick after abort returns Failed", player.Tick(Dt) == BtResult.Failed);
            result.Check("trace recorded the abort", player.Trace.Contains("abort@0"), player.Trace);
        }

        private static void CaseRepeat(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var player = new ActionQueuePlayer(executor, null, new FakeGuards());

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"steps\":["
                + "{\"verb\":\"jump\",\"repeat\":3}]}");
            player.Start(parsed.Script);
            RunToEnd(player);

            result.Check("repeat=3 runs the step three times", executor.Count("pulse[Space]") == 3,
                executor.Describe());
            result.Check("repeat script succeeds", player.State == ActionPlayState.Succeeded);
        }

        private static void CaseRefusedInjection(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor { RefuseEverything = true };
            var player = new ActionQueuePlayer(executor, null, new FakeGuards());

            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                "{\"format\":\"aea\",\"version\":1,\"steps\":[{\"verb\":\"jump\"}]}");
            player.Start(parsed.Script);
            RunToEnd(player);

            result.Check("refused injection is recorded in the trace", player.Trace.Contains("refused:"),
                player.Trace);
            result.Check("refused injection alone does not fail the script",
                player.State == ActionPlayState.Succeeded, player.State.ToString());
        }

        // ---------------------------------------------------------------- Task.RunActionScript

        /// <summary>假的技能服务提供者：脚本按名字查一张表。</summary>
        private sealed class FakeSkillProvider : IActionSkillProvider
        {
            public readonly Dictionary<string, ActionScript> Scripts =
                new Dictionary<string, ActionScript>(StringComparer.OrdinalIgnoreCase);

            public readonly List<string> Requested = new List<string>();
            public string ResolveError = "not found";

            public FakeSkillProvider(RecordingExecutor executor)
            {
                Executor = executor;
                Targets = new FakeResolver();
                Guards = new FakeGuards();
            }

            public IActionExecutor Executor { get; }

            public IActionTargetResolver Targets { get; }

            public IActionGuardEvaluator Guards { get; }

            public IReadOnlyList<string> ScriptDirectories
            {
                get { return new List<string> { "/fake/scripts" }; }
            }

            public ActionScript ResolveScript(string nameOrPath, out string error)
            {
                Requested.Add(nameOrPath ?? "<null>");
                ActionScript script;
                if (nameOrPath != null && Scripts.TryGetValue(nameOrPath, out script))
                {
                    error = null;
                    return script;
                }
                error = ResolveError;
                return null;
            }
        }

        private static ActionScript ScriptOrThrow(string json)
        {
            ActionScriptParseResult parsed = ActionScriptParser.ParseJson(json);
            if (!parsed.Ok)
                throw new InvalidOperationException("test script does not parse: " + parsed.Error);
            return parsed.Script;
        }

        private static BtContext NewContext(AiBlackboard blackboard)
        {
            var runtime = new BtRuntime(blackboard, new BtTestSensor(), new BtTestActuator());
            runtime.Start();
            return runtime.Context;
        }

        /// <summary>
        /// 推一帧，返回**本帧整棵树的结果**。
        ///
        /// 两个坑（都是自检实测踩出来的，写在这里免得再踩）：
        ///   1. 必须走 `runtime.Tick`：`BtContext.DeltaTime` 由 runtime 每帧写入，
        ///      直接反复调 `node.Tick(context)` 会让 DeltaTime 恒为 0，跨帧任务永远跑不完；
        ///   2. 读 `runtime.LastResult`，**不要读 `Root.LastResult`**：runtime 在整棵树跑完后会
        ///      `ResetSubtreeState()`（`loop=false` 的一次性树也走这条路），节点状态被重置回默认值
        ///      （`BtResult.Failed`），于是"明明成功了却读到 Failed"。
        /// </summary>
        private static BtResult TickFrame(BtRuntime runtime)
        {
            runtime.Tick(Dt);
            return runtime.LastResult;
        }

        /// <summary>
        /// 起一棵"只跑一次"的树：Root 的 `loop` 默认是 true（跑完立刻从头再来），
        /// 动作脚本这类**一次性**节点要放在 `loop=false` 的根下。
        /// </summary>
        private static BtRuntime StartOnce(BtNode node, AiBlackboard blackboard, string treeId)
        {
            var root = new BtRootNode { Loop = false };
            root.AddChild(node);
            var runtime = new BtRuntime(blackboard, new BtTestSensor(), new BtTestActuator());
            runtime.SetRoot(root, treeId);
            runtime.Start();
            return runtime;
        }

        private static void CaseRunActionScriptRegistry(BtSelfTest.TestResult result)
        {
            BtNodeInfo info;
            bool found = BtNodeRegistry.TryGetNodeInfo("Task.RunActionScript", out info);
            result.Check("Task.RunActionScript is registered", found);
            if (!found)
                return;

            result.Check("Task.RunActionScript is a Task (no children/services)",
                info.Shape == BtNodeShape.Task && !info.AllowsChildren && !info.AllowsServices);
            result.Check("Task.RunActionScript is package-serializable", info.PackageSerializable);

            BtNode node;
            result.Check("registry factory creates the node",
                BtNodeRegistry.TryCreateNode("Task.RunActionScript", out node)
                && node is BtRunActionScriptTask);
            result.Check("node reports the right type id", node != null
                && node.NodeType == "Task.RunActionScript");
            result.Check("node is latent (spans frames)",
                node is BtRunActionScriptTask && ((BtRunActionScriptTask)node).IsLatent);

            // 属性表里有 script / scriptKey / repeat / failKey（编辑器据此生成控件）
            var names = new List<string>();
            for (int i = 0; i < info.Properties.Count; i++)
                names.Add(info.Properties[i].Name);
            result.Check("property table exposes script+scriptKey",
                names.Contains("script") && names.Contains("scriptKey"), string.Join(",", names.ToArray()));
            result.Check("property table exposes repeat+totalTimeoutMs",
                names.Contains("repeat") && names.Contains("totalTimeoutMs"), string.Join(",", names.ToArray()));
            result.Check("property table exposes the fail-marker keys",
                names.Contains("failKey") && names.Contains("writeFailKey"),
                string.Join(",", names.ToArray()));
        }

        private static void CaseRunActionScriptNode(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var provider = new FakeSkillProvider(executor);
            provider.Scripts["mine"] = ScriptOrThrow(
                "{\"format\":\"aea\",\"version\":1,\"id\":\"mine\",\"steps\":["
                + "{\"verb\":\"hotbar\",\"slot\":2},{\"verb\":\"move\",\"dir\":\"f\",\"ms\":150},"
                + "{\"verb\":\"dig\",\"ms\":150}]}");

            IActionSkillProvider previous = ActionPlayServices.Current;
            ActionPlayServices.Current = provider;
            try
            {
                var blackboard = new AiBlackboard();
                var node = new BtRunActionScriptTask { Script = "mine" };
                BtRuntime runtime = StartOnce(node, blackboard, "act.node");

                BtResult first = TickFrame(runtime);
                result.Check("RunActionScript returns InProgress on the first tick",
                    first == BtResult.InProgress, first.ToString());

                BtResult last = first;
                int frames = 0;
                while (last == BtResult.InProgress && frames++ < 400)
                    last = TickFrame(runtime);

                result.Check("RunActionScript succeeds", last == BtResult.Succeeded,
                    last + " " + node.LastErrorCode + " " + node.LastError);
                result.Check("RunActionScript recorded the resolved script name",
                    node.ResolvedScript == "mine", node.ResolvedScript);
                result.Check("RunActionScript drove the executor", executor.Has("slot[2]")
                    && executor.Has("hold[W]=1"), executor.Describe());
                result.Check("RunActionScript released input at the end",
                    executor.Has("releaseAll"), executor.Describe());
                result.Check("no failure marker written on success",
                    !blackboard.Has("action.fail"), blackboard.ToString());

                // 未知脚本 → Failed + 稳定错误码 + 失败标记写黑板（供异常分支）
                var node2 = new BtRunActionScriptTask { Script = "nope" };
                BtResult failed = node2.Tick(NewContext(blackboard));
                result.Check("unknown script fails the node", failed == BtResult.Failed);
                result.Check("unknown script error code is step_failed",
                    node2.LastErrorCode == ActionErrorCodes.StepFailed, node2.LastErrorCode);
                string code;
                result.Check("failure code was written to the blackboard",
                    blackboard.TryGet<string>("action.fail", out code) && code == ActionErrorCodes.StepFailed,
                    code);
                string reason;
                result.Check("failure reason was written to the blackboard",
                    blackboard.TryGet<string>("action.reason", out reason)
                    && !string.IsNullOrEmpty(reason), reason);
            }
            finally
            {
                ActionPlayServices.Current = previous;
            }

            // 服务未装配 → 明确失败（不是静默什么都不做）。
            // ⚠️ **必须在 finally 里恢复**：`ActionPlayServices.Current` 是全局静态缝，
            //    自检跑完把它留成 null 的话，**整局树里的动作节点全部失效**
            //    （`action.fail=inject_refused` / `action.reason='action services are not initialized'`），
            //    而现象只是"树里的动作没反应"——平板实测踩到（A34）。
            IActionSkillProvider restore = ActionPlayServices.Current;
            Func<IActionSkillProvider> restoreFactory = ActionPlayServices.Provider;
            try
            {
                ActionPlayServices.Current = null;
                ActionPlayServices.Provider = null;
                var orphan = new BtRunActionScriptTask { Script = "mine" };
                BtResult orphanResult = orphan.Tick(NewContext(new AiBlackboard()));
                result.Check("without action services the node fails loudly",
                    orphanResult == BtResult.Failed
                    && orphan.LastErrorCode == ActionErrorCodes.InjectRefused, orphan.LastErrorCode);

                // 只有"按需取"的回调（`Current` 为空）时，节点也应能拿到服务（A34 的修法）
                ActionPlayServices.Provider = () => provider;
                var onDemand = new BtRunActionScriptTask { Script = "nope" };
                BtResult onDemandResult = onDemand.Tick(NewContext(new AiBlackboard()));
                result.Check("the node can resolve the provider on demand (Current == null)",
                    onDemandResult == BtResult.Failed
                    && onDemand.LastErrorCode == ActionErrorCodes.StepFailed,
                    onDemandResult + " " + onDemand.LastErrorCode);
            }
            finally
            {
                ActionPlayServices.Current = restore;
                ActionPlayServices.Provider = restoreFactory;
            }

            result.Check("the self-test restores the action-service seams it found",
                ReferenceEquals(ActionPlayServices.Current, restore)
                && ReferenceEquals(ActionPlayServices.Provider, restoreFactory));
        }

        private static void CaseRunActionScriptFromBlackboard(BtSelfTest.TestResult result)
        {
            var executor = new RecordingExecutor();
            var provider = new FakeSkillProvider(executor);
            provider.Scripts["laya_says_mine"] = ScriptOrThrow(
                "{\"format\":\"aea\",\"version\":1,\"id\":\"laya_says_mine\",\"steps\":["
                + "{\"verb\":\"jump\"}]}");

            IActionSkillProvider previous = ActionPlayServices.Current;
            ActionPlayServices.Current = provider;
            try
            {
                var blackboard = new AiBlackboard();
                BtContext context = NewContext(blackboard);
                var node = new BtRunActionScriptTask { ScriptKey = "pool.next" };

                // 黑板还没值 → 明确失败（计划里的脚本还没定，不许猜）
                BtResult noValue = node.Tick(context);
                result.Check("scriptKey without a value fails clearly",
                    noValue == BtResult.Failed && node.LastErrorCode == ActionErrorCodes.InvalidArgument,
                    node.LastErrorCode);

                // 写入"Laya 决定的脚本名" → 节点按它执行
                blackboard.Set(new AiBlackboardKey<string>("pool.next"), "laya_says_mine");
                var node2 = new BtRunActionScriptTask { ScriptKey = "pool.next" };
                BtRuntime runtime = StartOnce(node2, blackboard, "act.scriptkey");

                BtResult last = TickFrame(runtime);
                int frames = 0;
                while (last == BtResult.InProgress && frames++ < 200)
                    last = TickFrame(runtime);

                result.Check("scriptKey resolves the script chosen at runtime",
                    last == BtResult.Succeeded && node2.ResolvedScript == "laya_says_mine",
                    last + " " + node2.ResolvedScript + " " + node2.LastError);
                result.Check("the runtime-chosen script actually ran",
                    executor.Has("pulse[Space]"), executor.Describe());
            }
            finally
            {
                ActionPlayServices.Current = previous;
            }
        }

        private static void CaseRunActionScriptCompiles(BtSelfTest.TestResult result)
        {
            // 用手写包验证：属性名 → 字段的唯一映射点（TreeCompiler）认得新节点，
            // 且必填校验在**装载期**就报错（不是运行期才炸）
            PackageValue manifest = PackageValue.Object();
            manifest.Set("format", PackageValue.Str("scbt"));
            manifest.Set("version", PackageValue.Number(1));
            manifest.Set("id", PackageValue.Str("act.test"));
            manifest.Set("entry", PackageValue.Str("root"));

            PackageValue blackboard = PackageValue.Array();
            PackageValue key = PackageValue.Object();
            key.Set("name", PackageValue.Str("pool.next"));
            key.Set("type", PackageValue.Str("string"));
            blackboard.Add(key);
            manifest.Set("blackboard", blackboard);

            PackageValue node = PackageValue.Object();
            node.Set("id", PackageValue.Str("run"));
            node.Set("type", PackageValue.Str("Task.RunActionScript"));
            PackageValue properties = PackageValue.Object();
            properties.Set("script", PackageValue.Str("mine_stone_once"));
            properties.Set("repeat", PackageValue.Number(2));
            properties.Set("totalTimeoutMs", PackageValue.Number(5000));
            properties.Set("writeFailKey", PackageValue.Bool(false));
            properties.Set("failKey", PackageValue.Str("custom.fail"));
            node.Set("properties", properties);

            PackageValue root = PackageValue.Object();
            root.Set("id", PackageValue.Str("root"));
            root.Set("type", PackageValue.Str("Root"));
            PackageValue children = PackageValue.Array();
            children.Add(node);
            root.Set("children", children);

            byte[] bytes = PackageWriter.ToBytes(manifest, root);
            ScbtPackageSet set = PackageLoader.LoadFromBytes(bytes, "C:/pai-action-selftest/act.test.scbtpak");
            result.Check("hand-written package with the new node loads", set.Root != null && !set.HasErrors,
                set.Describe());

            CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
            result.Check("new node compiles", !compiled.HasErrors && compiled.Root != null,
                compiled.Report.Summary());

            var task = compiled.Root != null ? compiled.Root.Find("run") as BtRunActionScriptTask : null;
            result.Check("compiler mapped the properties onto the node", task != null
                && task.Script == "mine_stone_once" && task.Repeat == 2
                && task.TotalTimeoutMs == 5000 && !task.WriteFailKey && task.FailKey == "custom.fail",
                task == null ? "<node missing>" : task.ToString());

            // 既不写 script 也不写 scriptKey → 编译期报错（语义检查在编译点，不在装载器）
            properties.Remove("script");
            byte[] bad = PackageWriter.ToBytes(manifest, root);
            ScbtPackageSet badSet = PackageLoader.LoadFromBytes(bad, "C:/pai-action-selftest/act.bad.scbtpak");
            CompiledTree badCompiled = TreeCompiler.Compile(badSet, badSet.Root);
            result.Check("missing script AND scriptKey is rejected at compile time",
                badCompiled.HasErrors, badCompiled.Report.Summary());
        }

        private static void CaseScriptLibrary(BtSelfTest.TestResult result)
        {
            // 真磁盘（临时目录）：验证目录解析、扩展名补齐、同名遮蔽、哈希失效、错误上报
            string root = Path.Combine(Path.GetTempPath(), "pai-action-selftest");
            string scripts = Path.Combine(root, "PlayerAi", "Scripts");
            string trees = Path.Combine(root, "PlayerAi", "BehaviorTrees");
            SafeDelete(root);
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(trees);

            const string mineJson =
                "{\"format\":\"aea\",\"version\":1,\"id\":\"mine\",\"steps\":[{\"verb\":\"dig\",\"ms\":200}]}";
            const string jumpJson =
                "{\"format\":\"aea\",\"version\":1,\"id\":\"jump\",\"steps\":[{\"verb\":\"jump\"}]}";

            // 1) 主目录里的脚本 + 树包目录里的脚本（兼容：脚本与树包放一起）
            File.WriteAllText(Path.Combine(scripts, "mine.aeact"), mineJson);
            File.WriteAllText(Path.Combine(trees, "jump.aeact"), jumpJson);

            var library = new ActionScriptLibrary(new[] { scripts, trees });

            string error;
            result.Check("resolves a script by bare name",
                library.Resolve("mine", out error) != null, error);
            result.Check("resolves a script from the second directory too",
                library.Resolve("jump", out error) != null, error);
            result.Check("resolves by file name with extension",
                library.Resolve("mine.aeact", out error) != null, error);
            result.Check("unknown script gives a clear error",
                library.Resolve("nope", out error) == null && error != null && error.Contains("not found"),
                error);

            // 2) 列表：同名遮蔽（前面的目录优先）
            File.WriteAllText(Path.Combine(scripts, "shared.aeact"), jumpJson);
            File.WriteAllText(Path.Combine(trees, "shared.aeact"), mineJson);
            var entries = library.List();
            int sharedCount = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Name == "shared")
                    sharedCount++;
            }
            result.Check("list() de-duplicates same-named scripts across directories",
                sharedCount == 1, "count=" + sharedCount);
            result.Check("list() finds all scripts",
                entries.Count == 3, "count=" + entries.Count);

            // 3) 缓存：同内容命中缓存；内容变了立刻失效（热改脚本不用重启）
            int hitsBefore = library.CacheHits;
            library.Resolve("mine", out error);
            result.Check("second resolve hits the cache", library.CacheHits > hitsBefore,
                "hits=" + library.CacheHits);

            string changed = mineJson.Replace("\"ms\":200", "\"ms\":900");
            File.WriteAllText(Path.Combine(scripts, "mine.aeact"), changed);
            System.Threading.Thread.Sleep(20);
            File.SetLastWriteTimeUtc(Path.Combine(scripts, "mine.aeact"), DateTime.UtcNow.AddSeconds(2));

            ActionScript reloaded = library.Resolve("mine", out error);
            result.Check("edited script is re-read (no restart needed)",
                reloaded != null && Math.Abs(reloaded.Steps[0].Args.GetFloat("ms", 0f) - 900f) < 0.01f,
                reloaded != null ? reloaded.Steps[0].Args.ToString() : error);

            // 4) 坏脚本：出错但不炸，且能被 validate 报出来
            File.WriteAllText(Path.Combine(scripts, "broken.aeact"), "{\"format\":\"aea\",\"version\":1,");
            ActionScript broken = library.Resolve("broken", out error);
            result.Check("broken script resolves to null with an error",
                broken == null && !string.IsNullOrEmpty(error), error);
            result.Check("parse failures are counted", library.ParseFailures >= 1,
                "failures=" + library.ParseFailures);

            Dictionary<string, object> validation = library.Validate("mine");
            result.Check("validate() reports ok for a good script", Equals(validation["ok"], true),
                validation["error"] != null ? validation["error"].ToString() : "<null>");
            result.Check("validate() lists the verbs",
                validation["verbs"] is List<string> && ((List<string>)validation["verbs"]).Count == 1,
                "verbs missing");

            Dictionary<string, object> badValidation = library.Validate("broken");
            result.Check("validate() reports the failure reason",
                Equals(badValidation["ok"], false) && badValidation["error"] != null,
                badValidation["error"] != null ? badValidation["error"].ToString() : "<null>");

            SafeDelete(root);
        }

        /// <summary>
        /// 出厂示例脚本必须自身合法：它们是"装完就能跑"的门面，
        /// 一旦写错（verb 拼错、守卫不认、JSON 手滑）就是用户第一次接触就撞坑。
        /// </summary>
        private static void CaseShippedTemplates(BtSelfTest.TestResult result)
        {
            Dictionary<string, string> templates = ActionScriptTemplates.All();
            result.Check("there are shipped example scripts", templates.Count >= 3,
                "count=" + templates.Count);

            foreach (KeyValuePair<string, string> pair in templates)
            {
                ActionScriptParseResult parsed = ActionScriptParser.ParseJson(pair.Value, pair.Key);
                result.Check("shipped script parses: " + pair.Key, parsed.Ok, parsed.Error);
                if (!parsed.Ok)
                    continue;

                ActionScript script = parsed.Script;
                result.Check("shipped script has an id: " + pair.Key,
                    !string.IsNullOrEmpty(script.Id), script.Id);
                result.Check("shipped script has at least one step: " + pair.Key,
                    script.Steps.Count > 0, "steps=" + script.Steps.Count);

                // 每个 verb 必须真的存在于词表（防止示例用了没实现的动作）
                for (int i = 0; i < script.Steps.Count; i++)
                {
                    ActionVerbSpec spec;
                    result.Check("shipped script verb is known: " + pair.Key + "/" + script.Steps[i].Verb,
                        ScriptCompiler.TryGetVerb(script.Steps[i].Verb, out spec));
                }

                // 光"verb 认识"不够：**每一步都要真的编得出帧**（枚举写错、时长负数、
                // 目标解析不了都只有编译这一步才发现）。出厂脚本是"装完就能跑"的门面，
                // 在第一屏就撞 `invalid_argument` 是用户对整套东西的第一印象。
                for (int i = 0; i < script.Steps.Count; i++)
                {
                    ActionStep step = script.Steps[i];
                    ActionClip clip = ScriptCompiler.CompileStep(step.Verb, step.Args, new FakeResolver(),
                        out string compileError);
                    result.Check("shipped script step compiles: " + pair.Key + "/" + step.Verb,
                        clip != null && compileError == null, compileError);
                    if (clip != null)
                    {
                        result.Check("shipped script step produces frames: " + pair.Key + "/" + step.Verb,
                            clip.Frames.Count > 0, "frames=" + clip.Frames.Count);
                    }
                }
            }

            // 目标动作包必须齐：`demo.laya` 的映射表引用它们（少一条 = 那个目标永远跑不出动作）
            string[] goals =
            {
                ActionScriptNames.MineOnce, ActionScriptNames.GatherOnce, ActionScriptNames.CraftOnce,
                ActionScriptNames.EatOnce, ActionScriptNames.SleepOnce, ActionScriptNames.AttackOnce,
                ActionScriptNames.FleeOnce, ActionScriptNames.ExploreOnce
            };
            for (int i = 0; i < goals.Length; i++)
            {
                result.Check("goal script is shipped: " + goals[i], templates.ContainsKey(goals[i]),
                    "count=" + templates.Count);
            }

            // 安装：缺什么补什么，已存在的不覆盖
            string root = Path.Combine(Path.GetTempPath(), "pai-action-template-selftest");
            SafeDelete(root);
            Directory.CreateDirectory(root);

            List<string> installed;
            string error;
            int first = ActionScriptTemplates.Install(root, out installed, out error);
            result.Check("install writes the example scripts", first >= 3 && error == null,
                "written=" + first + " error=" + error);

            string marker = "{\"format\":\"aea\",\"version\":1,\"id\":\"user_edit\",\"steps\":[{\"verb\":\"jump\"}]}";
            string minePath = Path.Combine(ActionScriptTemplates.DirectoryFor(root),
                ActionScriptTemplates.MineOnceFile);
            File.WriteAllText(minePath, marker);

            int second = ActionScriptTemplates.Install(root, out installed, out error);
            result.Check("re-install never overwrites an existing script", second == 0,
                "written=" + second);
            result.Check("the user's edit is intact",
                File.ReadAllText(minePath) == marker, File.ReadAllText(minePath));

            SafeDelete(root);
        }

        /// <summary>
        /// 守住一条回归：**除了 hotbar/eat 这类真的要换槽位的 verb，其余 verb 一个槽位都不许动**。
        ///
        /// 背景（实测踩到的真 bug）：`ActionFrame` 是 struct，字段默认 0，而 0 会被当成"槽位 0"——
        /// 于是每一个用对象初始化器直接构造的帧（jump/aim/dig/ui…）都会顺手按下数字键 0。
        /// 游戏里表现为轨迹出现 `refused:slot:0`、真机上莫名切换快捷栏。
        /// </summary>
        private static void CaseNoStraySlotChange(BtSelfTest.TestResult result)
        {
            // verb → 一套合法的最小参数
            var samples = new List<KeyValuePair<string, ActionArgs>>
            {
                new KeyValuePair<string, ActionArgs>("wait", Args("ms", 60)),
                new KeyValuePair<string, ActionArgs>("move", Args("dir", "f", "ms", 60)),
                new KeyValuePair<string, ActionArgs>("turn", Args("yaw", 30)),
                new KeyValuePair<string, ActionArgs>("lookdir", Args("dir", "r")),
                new KeyValuePair<string, ActionArgs>("lookAt", Args("target", "1,2,3")),
                new KeyValuePair<string, ActionArgs>("jump", null),
                new KeyValuePair<string, ActionArgs>("sneak", null),
                new KeyValuePair<string, ActionArgs>("mount", null),
                new KeyValuePair<string, ActionArgs>("fly", null),
                new KeyValuePair<string, ActionArgs>("dig", Args("ms", 60)),
                new KeyValuePair<string, ActionArgs>("hit", null),
                new KeyValuePair<string, ActionArgs>("interact", null),
                new KeyValuePair<string, ActionArgs>("attack", null),
                new KeyValuePair<string, ActionArgs>("aim", Args("on", true)),
                new KeyValuePair<string, ActionArgs>("drop", null),
                new KeyValuePair<string, ActionArgs>("ui", Args("key", "E")),
                new KeyValuePair<string, ActionArgs>("sleep", null),
                new KeyValuePair<string, ActionArgs>("openInventory", null)
            };

            int stray = 0;
            string strayDetail = null;
            for (int i = 0; i < samples.Count; i++)
            {
                string verb = samples[i].Key;
                string error;
                ActionClip clip = ScriptCompiler.CompileStep(verb, samples[i].Value, new FakeResolver(), out error);
                if (clip == null)
                {
                    result.Check("sample verb compiles: " + verb, false, error);
                    continue;
                }
                for (int f = 0; f < clip.FrameCount; f++)
                {
                    int slot = clip.Frames[f].SelectSlot;
                    if (slot != -1)
                    {
                        stray++;
                        if (strayDetail == null)
                            strayDetail = verb + " frame " + f + " slot=" + slot;
                    }
                }
            }
            result.Check("no verb touches the hotbar unless it is meant to", stray == 0, strayDetail);

            // 反证：hotbar / eat 必须真的设槽位（否则这条回归测试会"永远绿"）
            string e1, e2;
            ActionClip hotbar = ScriptCompiler.CompileStep("hotbar", Args("slot", 5), null, out e1);
            bool hotbarSets = hotbar != null && hotbar.Frames[0].SelectSlot == 5;
            ActionClip eat = ScriptCompiler.CompileStep("eat", null, new FakeResolver(), out e2);
            bool eatSets = false;
            if (eat != null)
            {
                for (int f = 0; f < eat.FrameCount; f++)
                {
                    if (eat.Frames[f].SelectSlot == 3)
                        eatSets = true;
                }
            }
            result.Check("hotbar does set its slot (control sample)", hotbarSets, e1);
            result.Check("eat does set the resolved slot (control sample)", eatSets, e2);
        }

        /// <summary>
        /// `aim` / `first_food` 的"观察结果 → 动作层目标"解析（P2）。
        ///
        /// 这两个是**纯函数**，所以能在没有游戏的临时工程里逐条钉住：
        /// 正常命中、没世界、没瞄准、瞄到实体、背包为空、没有可食物 —— 每种都给明确原因，
        /// 而且**绝不猜一个格子**（猜了就会挖错方块 / 吃错东西）。
        /// </summary>
        private static void CaseAimObservationParsing(BtSelfTest.TestResult result)
        {
            // ---- aim ----
            string error;
            result.Check("aim: null observation -> clear error",
                GameActionServices.TargetFromAimObservation(null, out error) == null
                && error != null && error.StartsWith(GameActionServices.TargetUnavailable, StringComparison.Ordinal),
                error);

            var noWorld = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["loaded"] = false, ["active"] = false
            };
            result.Check("aim: no world -> clear error",
                GameActionServices.TargetFromAimObservation(noWorld, out error) == null, error);

            var noTarget = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["loaded"] = true, ["active"] = true
            };
            result.Check("aim: nothing under the crosshair -> clear error",
                GameActionServices.TargetFromAimObservation(noTarget, out error) == null, error);

            var entityTarget = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["loaded"] = true, ["active"] = true,
                ["target"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["kind"] = "entity" }
            };
            result.Check("aim: entity under the crosshair is rejected (not a block)",
                GameActionServices.TargetFromAimObservation(entityTarget, out error) == null
                && error != null && error.Contains("not a block"), error);

            var blockTarget = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["loaded"] = true, ["active"] = true,
                ["target"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["kind"] = "block",
                    ["cell"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = 12, ["y"] = 64, ["z"] = -7
                    }
                }
            };
            ActionTarget cell = GameActionServices.TargetFromAimObservation(blockTarget, out error);
            result.Check("aim: a block becomes a concrete cell", cell != null
                && cell.Kind == "cell" && cell.X == 12 && cell.Y == 64 && cell.Z == -7, error);
            result.Check("aim: the cell carries a look point (block centre)",
                cell != null && cell.HasLookPoint
                && Math.Abs(cell.LookX - 12.5f) < 0.001f
                && Math.Abs(cell.LookY - 64.5f) < 0.001f
                && Math.Abs(cell.LookZ + 6.5f) < 0.001f);

            // 数值以字符串形式回来时也要认（JSON 化的观察输出可能这样）
            var stringyCell = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["loaded"] = true, ["active"] = true,
                ["target"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["kind"] = "block",
                    ["cell"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = "3", ["y"] = "70", ["z"] = "5"
                    }
                }
            };
            ActionTarget parsed = GameActionServices.TargetFromAimObservation(stringyCell, out error);
            result.Check("aim: string-encoded coordinates are parsed", parsed != null
                && parsed.X == 3 && parsed.Y == 70 && parsed.Z == 5, error);

            // ---- first_food ----
            result.Check("first_food: null player -> clear error",
                GameActionServices.TargetFirstFoodFromPlayerObservation(null, out error) == null, error);

            var emptyInventory = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["loaded"] = true,
                ["inventory"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["slots"] = new List<object>()
                }
            };
            result.Check("first_food: empty inventory -> clear error",
                GameActionServices.TargetFirstFoodFromPlayerObservation(emptyInventory, out error) == null
                && error != null && error.Contains("empty"), error);

            // 真值用游戏里的方块表来取，避免自检硬编码"哪个是食物"。
            // 拿不到方块表时（无游戏上下文的临时工程）退化成"两种 contents 都必须判为不可食用"。
            int foodContents = FindEdibleContents();
            int stoneContents = FindInedibleContents();
            bool haveBlockTable = foodContents > 0 && stoneContents > 0;

            if (haveBlockTable)
            {
                var mixed = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["loaded"] = true,
                    ["inventory"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["slots"] = new List<object>
                        {
                            new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["slot"] = 0, ["value"] = stoneContents, ["contents"] = stoneContents, ["count"] = 5
                            },
                            new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["slot"] = 1, ["value"] = foodContents, ["contents"] = foodContents, ["count"] = 2
                            }
                        }
                    }
                };
                ActionTarget food = GameActionServices.TargetFirstFoodFromPlayerObservation(mixed, out error);
                result.Check("first_food: picks the first edible slot, skipping non-food",
                    food != null && food.X == 2,    // slot 1 → 槽位号 2
                    error ?? (food != null ? "X=" + food.X : "<null>"));

                var noFood = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["loaded"] = true,
                    ["inventory"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["slots"] = new List<object>
                        {
                            new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["slot"] = 0, ["value"] = stoneContents, ["contents"] = stoneContents, ["count"] = 5
                            }
                        }
                    }
                };
                result.Check("first_food: nothing edible -> clear error",
                    GameActionServices.TargetFirstFoodFromPlayerObservation(noFood, out error) == null
                    && error != null && error.Contains("no edible"), error);

                // 确定性：同一输入必定给同一结果（可复现是 §4.9 的硬要求）
                ActionTarget again = GameActionServices.TargetFirstFoodFromPlayerObservation(mixed, out error);
                result.Check("first_food: same inventory resolves to the same slot (deterministic)",
                    again != null && food != null && again.X == food.X);
            }
            else
            {
                // 无方块表：任何未知 contents 都必须判成"不能吃"（宁可报没有食物，也不能乱吃）
                var unknown = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["loaded"] = true,
                    ["inventory"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["slots"] = new List<object>
                        {
                            new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["slot"] = 0, ["value"] = 1, ["contents"] = 1, ["count"] = 5
                            }
                        }
                    }
                };
                result.Check("first_food: without a block table nothing is treated as food",
                    GameActionServices.TargetFirstFoodFromPlayerObservation(unknown, out error) == null,
                    error);
            }
        }

        /// <summary>
        /// 在游戏方块表里找一个可食用方块（营养值 &gt; 0）的 contents 值。
        /// 返回 -1 表示**这个环境下拿不到方块表**（自检在无游戏上下文里跑时 `BlocksManager.Blocks`
        /// 可能是 null，或某个方块的营养值计算会抛）—— 调用方据此改走断言"绝不吃非食物"。
        /// </summary>
        private static int FindEdibleContents()
        {
            Game.Block[] blocks = Game.BlocksManager.Blocks;
            if (blocks == null)
                return -1;
            for (int i = 1; i < blocks.Length; i++)
            {
                Game.Block block = blocks[i];
                if (block == null)
                    continue;
                try
                {
                    int value = Game.Terrain.MakeBlockValue(i, 0, 0);
                    if (block.GetNutritionalValue(value) > 0f)
                        return i;
                }
                catch (Exception)
                {
                }
            }
            return -1;
        }

        /// <summary>在游戏方块表里找一个**不可食用**方块（营养值 == 0）的 contents 值；-1 = 拿不到表。</summary>
        private static int FindInedibleContents()
        {
            Game.Block[] blocks = Game.BlocksManager.Blocks;
            if (blocks == null)
                return -1;
            for (int i = 1; i < blocks.Length; i++)
            {
                Game.Block block = blocks[i];
                if (block == null)
                    continue;
                try
                {
                    int value = Game.Terrain.MakeBlockValue(i, 0, 0);
                    if (block.GetNutritionalValue(value) <= 0f)
                        return i;
                }
                catch (Exception)
                {
                }
            }
            return -1;
        }

        private static void SafeDelete(string directory)        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // 自检收尾不因为清理失败而失败
            }
        }
    }
}

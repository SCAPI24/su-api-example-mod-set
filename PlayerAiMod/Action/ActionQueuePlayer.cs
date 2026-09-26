using System;
using System.Collections.Generic;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 动作层的执行出口：把**一帧动作输入**交给执行器。
    ///
    /// 为什么单独抽一个接口（而不是直接用 <see cref="IAiActuator"/>）：
    ///   · 动作层要能在**没有游戏的临时工程**里自检（塞个记录型假实现即可）；
    ///   · 游戏侧实现只有一处（<c>GameActionExecutor</c> 预热 <c>Look/LookAt</c> 并转交 IAiActuator）；
    ///   · 视角的"绝对朝向"必须由游戏侧在**编译那一帧**算成 yaw/pitch（那需要知道当前位置）。
    ///
    /// 实现方纪律（与 <see cref="IAiActuator"/> 同源）：所有写入排在**帧首**；
    /// 返回 false 表示这一帧这次动作没执行（未启用/被拒绝），调用方如实记账而不是当成功。
    /// </summary>
    public interface IActionExecutor
    {
        /// <summary>执行器是否可用（输入注入已接入）。</summary>
        bool IsReady { get; }

        /// <summary>按住/松开一组键。</summary>
        bool HoldKeys(IReadOnlyList<string> keys, bool down);

        /// <summary>按一下某个键。</summary>
        bool PulseKey(string key);

        /// <summary>鼠标动作：button = "left"/"right"/"middle"，action = "down"/"up"/"click"。</summary>
        bool MouseAction(string button, string action);

        /// <summary>滚轮格数（正 = 向上）。</summary>
        bool Wheel(int notches);

        /// <summary>选择快捷栏槽位（1..10）。</summary>
        bool SelectSlot(int slot);

        /// <summary>瞬时转向到绝对角度（度）。</summary>
        bool Look(float yawDegrees, float pitchDegrees);

        /// <summary>视角增量（度）。</summary>
        bool LookDelta(float yawDeltaDegrees, float pitchDeltaDegrees);

        /// <summary>看向世界坐标点。</summary>
        bool LookAt(float x, float y, float z);

        /// <summary>引擎内 UI 点击（语义目标）。</summary>
        bool UiClick(string selector);

        /// <summary>输入文本（需要焦点已在输入框）。</summary>
        bool TypeText(string text);

        /// <summary>释放全部按键与鼠标（任何结束路径都要走它）。</summary>
        void ReleaseAll();
    }

    /// <summary>守卫判定（游戏侧实现；临时工程里可塞常量实现）。</summary>
    public interface IActionGuardEvaluator
    {
        bool IsReady { get; }

        /// <summary>判定一个守卫（词汇见 <see cref="ActionGuards"/>）。无法判定时返回 false 并给原因。</summary>
        bool Evaluate(string guard, out string error);
    }

    /// <summary>动作播放的结束原因。</summary>
    public enum ActionPlayState
    {
        Idle,
        Playing,
        Succeeded,

        /// <summary>失败（含超时）。<see cref="ActionQueuePlayer.ErrorCode"/> 给稳定错误码。</summary>
        Failed,

        /// <summary>被外部中断（人夺回 / 更高优先级 / 树被替换）。</summary>
        Aborted
    }

    /// <summary>
    /// 脚本执行器：把"verb 序列"按帧喂给执行器。
    ///
    /// 三条铁律（对齐 plan §3.3）：
    ///   1. **每步现编译**：需要语义目标的 verb（dig/attack/lookAt）在**执行那一刻**才解析目标，
    ///      于是"Laya 给枚举、C# 给具体值"（§4.9）成立，且不会拿着过期的坐标发动作；
    ///   2. **看门狗**：每步有硬超时（默认 = 编译时长 × 系数，且有下限），超时即**释放输入**并判失败，
    ///      绝不允许"卡在某一步还按着键"；
    ///   3. **守卫在入口与每帧**：守卫不满足立刻收尾（人死了/世界卸载/模态弹出），不空转。
    /// </summary>
    public sealed class ActionQueuePlayer
    {
        private readonly List<string> m_heldKeys = new List<string>();
        private readonly StringBuilder m_trace = new StringBuilder();

        private ActionScript m_script;
        private ActionClip m_clip;
        private int m_stepIndex;
        private int m_frameIndex;
        private double m_frameTimeMs;
        private double m_elapsedMs;
        private double m_stepElapsedMs;
        private int m_stepTimeoutMs;
        private int m_budgetMs;
        private int m_stepRepeatLeft;
        private double m_totalElapsedMs;

        public ActionQueuePlayer(IActionExecutor executor, IActionTargetResolver targets = null,
            IActionGuardEvaluator guards = null)
        {
            Executor = executor;
            Targets = targets;
            Guards = guards;
        }

        public IActionExecutor Executor { get; }

        /// <summary>语义目标解析器（§4.9）。为 null 时"语义目标"会失败而不是猜。</summary>
        public IActionTargetResolver Targets { get; set; }

        /// <summary>守卫判定器（词汇与 `obs.waitFor` 一致）。为 null 时视为"无守卫"。</summary>
        public IActionGuardEvaluator Guards { get; set; }

        public ActionPlayState State { get; private set; } = ActionPlayState.Idle;

        /// <summary>稳定错误码（失败时）。</summary>
        public string ErrorCode { get; private set; }

        /// <summary>人类可读的失败原因（日志/事件环）。</summary>
        public string LastError { get; private set; }

        /// <summary>失败/中断发生在第几步（0 基；-1 = 不在步骤里）。</summary>
        public int FailedStepIndex { get; private set; } = -1;

        /// <summary>脚本（播放中）。</summary>
        public ActionScript Script
        {
            get { return m_script; }
        }

        public int StepIndex
        {
            get { return m_stepIndex; }
        }

        public int StepCount
        {
            get { return m_script != null ? m_script.Steps.Count : 0; }
        }

        public double ElapsedMs
        {
            get { return m_totalElapsedMs; }
        }

        /// <summary>本步剩余的重复次数（≥1 时还在跑）。</summary>
        public int StepRepeatRemaining
        {
            get { return m_stepRepeatLeft; }
        }

        public bool IsFinished
        {
            get
            {
                return State == ActionPlayState.Succeeded || State == ActionPlayState.Failed
                    || State == ActionPlayState.Aborted;
            }
        }

        /// <summary>最近一次执行的逐步轨迹（复盘用：走了哪几步、每步实际多久）。</summary>
        public string Trace
        {
            get { return m_trace.ToString(); }
        }

        /// <summary>总超时（毫秒）：脚本声明时长之和 × 系数，加上各步下限。0 = 不限制。</summary>
        public int TotalTimeoutMs { get; set; }

        /// <summary>
        /// 实际生效的脚本级预算（毫秒）：起始值 = 各步**显式声明**的 timeoutMs 之和，
        /// 之后每进入一步就把该步的看门狗预算加进来（因为"编译后时长 × 系数"只有编译了才知道）。
        /// 于是**没写 timeoutMs 的长动作不会被误杀**，而"每步都写了预算却被人为拖长"仍会被总熔丝拦住。
        /// </summary>
        public int BudgetMs
        {
            get { return m_budgetMs; }
        }

        /// <summary>自检用：把脚本级预算压到指定值，验证熔丝仍然有效（正常运行路径不调用）。</summary>
        public void DebugSetBudgetMs(int budgetMs)
        {
            m_budgetMs = budgetMs;
        }

        // ---------------------------------------------------------------- 生命周期

        /// <summary>开始执行一个脚本。返回 false 表示**开局就失败**（守卫/执行器/脚本问题）。</summary>
        public bool Start(ActionScript script)
        {
            Reset();
            m_script = script;
            State = ActionPlayState.Playing;

            if (script == null || script.Steps.Count == 0)
            {
                Fail(ActionErrorCodes.InvalidArgument, "script is empty");
                return false;
            }
            if (Executor == null || !Executor.IsReady)
            {
                Fail(ActionErrorCodes.InjectRefused, "action executor is not ready (input injection unavailable)");
                return false;
            }

            string guardError;
            if (!CheckGuards(out guardError))
            {
                Fail(ActionErrorCodes.GuardFailed, guardError);
                return false;
            }

            // 起始预算 = 显式声明的 timeoutMs 之和；其余在 BeginStepRun 里逐步累加
            TotalTimeoutMs = ComputeTotalTimeoutMs(script);
            m_budgetMs = TotalTimeoutMs;

            m_stepIndex = -1;
            if (!EnterNextStep())
                return false;

            return State == ActionPlayState.Playing;
        }

        /// <summary>停止并释放输入（任何结束路径都走它；reasonCode 为 null 时是正常停止）。</summary>
        public void Stop(string reasonCode = null, string reason = null)
        {
            ReleaseInput();
            if (State == ActionPlayState.Playing)
            {
                if (string.IsNullOrEmpty(reasonCode))
                {
                    State = ActionPlayState.Aborted;
                    ErrorCode = ActionErrorCodes.Aborted;
                }
                else
                {
                    State = ActionPlayState.Failed;
                    ErrorCode = reasonCode;
                }
                LastError = reason ?? reasonCode;
            }
        }

        /// <summary>
        /// 释放本播放器占住的输入（不改变状态）。宿主卸载/世界卸载时的兜底入口 ——
        /// 与 <see cref="Stop"/> 的区别：那个会顺带把状态改成失败/中断，这个只清输入。
        /// </summary>
        public void ReleaseInput()
        {
            try
            {
                ReleaseInputCore();
            }
            catch (Exception)
            {
                // 收尾路径不抛异常（铁律：任何结束路径都释放输入）
            }
        }

        /// <summary>外部中断（人夺回、优先级更高、树被替换）。</summary>
        public void Abort(string reason)        {
            if (State != ActionPlayState.Playing)
                return;
            ReleaseInput();
            State = ActionPlayState.Aborted;
            ErrorCode = ActionErrorCodes.Aborted;
            LastError = reason ?? "aborted";
            FailedStepIndex = m_stepIndex;
            AppendTrace("abort@" + m_stepIndex + ":" + LastError);
        }

        /// <summary>
        /// 推进一帧。返回：
        ///   InProgress = 还在跑；Succeeded = 全部步骤完成；Failed = 失败（看 <see cref="ErrorCode"/>）。
        /// </summary>
        public BtResult Tick(float deltaTime)
        {
            if (State == ActionPlayState.Succeeded)
                return BtResult.Succeeded;
            if (State == ActionPlayState.Failed)
                return BtResult.Failed;
            if (State == ActionPlayState.Aborted)
                return BtResult.Failed;
            if (State != ActionPlayState.Playing)
            {
                Fail(ActionErrorCodes.VerbFailed, "player is not playing (call Start first)");
                return BtResult.Failed;
            }

            if (deltaTime < 0f)
                deltaTime = 0f;
            double deltaMs = deltaTime * 1000.0;
            m_elapsedMs += deltaMs;
            m_stepElapsedMs += deltaMs;
            m_totalElapsedMs += deltaMs;

            // 总超时（脚本级保险丝）
            if (m_budgetMs > 0 && m_totalElapsedMs > m_budgetMs)
            {
                Fail(ActionErrorCodes.Timeout, "script exceeded total budget (" + m_budgetMs + " ms)");
                return BtResult.Failed;
            }

            // 守卫每帧复查：守卫失效立刻收尾，不空转
            string guardError;
            if (!CheckGuards(out guardError))
            {
                Fail(ActionErrorCodes.GuardFailed, guardError);
                return BtResult.Failed;
            }

            // 每步看门狗
            if (m_stepTimeoutMs > 0 && m_stepElapsedMs > m_stepTimeoutMs)
            {
                Fail(ActionErrorCodes.Timeout, "step " + m_stepIndex + " (" + CurrentVerb()
                    + ") exceeded " + m_stepTimeoutMs + " ms");
                return BtResult.Failed;
            }

            if (m_clip == null || m_clip.FrameCount == 0)
            {
                if (!FinishStepAndEnterNext())
                    return TerminalResult();
                return BtResult.InProgress;
            }

            // 按"这一帧自己的 dt"推进：与录制回放同一套节奏模型（帧率解耦）
            m_frameTimeMs -= deltaMs;
            int guard = 0;
            while (m_frameTimeMs <= 0.0 && guard++ < 8)
            {
                int next = m_frameIndex;
                if (next >= m_clip.FrameCount)
                {
                    if (!FinishStepAndEnterNext())
                        return TerminalResult();
                    return BtResult.InProgress;
                }

                ApplyFrame(m_clip.Frames[next]);
                m_frameIndex = next + 1;
                m_frameTimeMs += Math.Max(1.0, m_clip.Frames[next].DeltaMs);
            }

            if (State == ActionPlayState.Playing)
                return BtResult.InProgress;
            return TerminalResult();
        }

        /// <summary>当前正在执行的 verb 名（日志/黑板用）。</summary>
        public string CurrentVerb()
        {
            if (m_script == null || m_stepIndex < 0 || m_stepIndex >= m_script.Steps.Count)
                return null;
            return m_script.Steps[m_stepIndex].Verb;
        }

        public string Describe()
        {
            if (m_script == null)
                return "player(idle)";
            return "player(" + m_script.Id + " step=" + (m_stepIndex + 1) + "/" + m_script.Steps.Count
                + " verb=" + (CurrentVerb() ?? "-")
                + " t=" + (m_totalElapsedMs / 1000.0).ToString("0.00") + "s"
                + " state=" + State + ")";
        }

        public override string ToString()
        {
            return Describe();
        }

        // ---------------------------------------------------------------- 内部：步骤推进

        private void Reset()
        {
            // 开新脚本时先把"上一次残留"清干净：只释放**本对象记账过的**键 + 一次 ReleaseAll，
            // 之后不再中途 ReleaseAll（否则会把当前脚本刚按下的键清掉 —— 自检实测过的抖动原因）。
            if (m_heldKeys.Count > 0 && Executor != null)
            {
                var stale = new List<string>(m_heldKeys);
                try { Executor.HoldKeys(stale, false); } catch (Exception) { }
            }
            m_heldKeys.Clear();
            if (Executor != null)
            {
                try { Executor.ReleaseAll(); } catch (Exception) { }
            }

            m_script = null;
            m_clip = null;
            m_stepIndex = -1;
            m_frameIndex = 0;
            m_frameTimeMs = 0.0;
            m_elapsedMs = 0.0;
            m_stepElapsedMs = 0.0;
            m_stepTimeoutMs = 0;
            m_budgetMs = 0;
            m_stepRepeatLeft = 0;
            m_totalElapsedMs = 0.0;
            TotalTimeoutMs = 0;
            State = ActionPlayState.Idle;
            ErrorCode = null;
            LastError = null;
            FailedStepIndex = -1;
            m_trace.Length = 0;
        }

        private bool EnterNextStep()
        {
            m_stepIndex++;
            if (m_script == null || m_stepIndex >= m_script.Steps.Count)
            {
                State = ActionPlayState.Succeeded;
                ReleaseInput();
                return false;
            }

            ActionStep step = m_script.Steps[m_stepIndex];
            m_stepRepeatLeft = Math.Max(1, step.Repeat);
            return BeginStepRun(step);
        }

        private bool BeginStepRun(ActionStep step)
        {
            string error;
            m_clip = ScriptCompiler.CompileStep(step.Verb, step.Args, Targets, out error);
            if (m_clip == null)
            {
                Fail(ActionErrorCodes.VerbFailed, "step " + m_stepIndex + " (" + step.Verb + "): " + error);
                return false;
            }

            m_frameIndex = 0;
            m_frameTimeMs = 0.0;
            m_stepElapsedMs = 0.0;
            m_stepTimeoutMs = ComputeStepTimeoutMs(step, m_clip);
            // 这一步的预算也进脚本级熔丝（编译后才知道真实时长，所以在这里累加）
            m_budgetMs += m_stepTimeoutMs;
            m_stepRepeatLeft--;
            AppendTrace("step" + m_stepIndex + ":" + step.Verb
                + " frames=" + m_clip.FrameCount
                + " dur=" + (int)Math.Round(m_clip.DurationSeconds * 1000.0) + "ms"
                + " timeout=" + m_stepTimeoutMs + "ms");
            return true;
        }

        /// <summary>一步放完：还有重复就再来一次，否则进下一步。</summary>
        private bool FinishStepAndEnterNext()
        {
            if (m_stepRepeatLeft > 0)
                return BeginStepRun(m_script.Steps[m_stepIndex]);
            return EnterNextStep();
        }

        /// <summary>终止态 → 行为树结果。</summary>
        private BtResult TerminalResult()
        {
            if (State == ActionPlayState.Succeeded)
                return BtResult.Succeeded;
            if (State == ActionPlayState.Playing)
                return BtResult.InProgress;
            return BtResult.Failed;
        }

        private int ComputeStepTimeoutMs(ActionStep step, ActionClip clip)
        {
            if (step.TimeoutMs > 0)
                return step.TimeoutMs;
            int durationMs = (int)Math.Round(clip.DurationSeconds * 1000.0);
            int computed = (int)Math.Round(durationMs * ActionCompileDefaults.StepTimeoutFactor);
            return Math.Max(ActionCompileDefaults.MinimumStepTimeoutMs, computed);
        }

        private static int ComputeTotalTimeoutMs(ActionScript script)
        {
            // 准确的预算只有**每一步编译出来之后**才知道（看门狗预算 = 编译时长 × 系数），
            // 所以这里只累加"显式声明过的"预算，其余交给 BeginStepRun 逐步累加（见 m_budgetMs）。
            //
            // 早期版本在这里用"每步 1000ms"当默认值 —— 于是 `move ms=3000`（编译后 3 秒）
            // 会在脚本级熔丝上 1 秒就被掐掉；实机表现是"脚本莫名 timeout"
            // （自检已用 "long step without explicit timeout is not killed by the total fuse" 钉住）。
            int total = 0;
            for (int i = 0; i < script.Steps.Count; i++)
            {
                ActionStep step = script.Steps[i];
                if (step.TimeoutMs > 0)
                    total += step.TimeoutMs * Math.Max(1, step.Repeat);
            }
            return total;
        }

        // ---------------------------------------------------------------- 内部：帧落地

        /// <summary>把一帧动作写进执行器（按住类只改差异，与录制回放同语义）。</summary>
        private void ApplyFrame(ActionFrame frame)
        {
            if (Executor == null || !Executor.IsReady)
            {
                Fail(ActionErrorCodes.InjectRefused, "action executor became unavailable mid-script");
                return;
            }

            // 1) 按住类：先按住新的，再松开不在新集合里的（与 ScatPlayer 同一顺序，避免一瞬间全松）
            List<string> held = frame.KeysHeld != null
                ? new List<string>(frame.KeysHeld)
                : new List<string>();
            for (int i = 0; i < held.Count; i++)
            {
                if (!m_heldKeys.Contains(held[i]))
                    TryInject("hold:" + held[i], () => Executor.HoldKeys(new[] { held[i] }, true));
            }
            for (int i = m_heldKeys.Count - 1; i >= 0; i--)
            {
                string previous = m_heldKeys[i];
                if (held.Contains(previous))
                    continue;
                TryInject("release:" + previous, () => Executor.HoldKeys(new[] { previous }, false));
                m_heldKeys.RemoveAt(i);
            }
            for (int i = 0; i < held.Count; i++)
            {
                if (!m_heldKeys.Contains(held[i]))
                    m_heldKeys.Add(held[i]);
            }

            // 2) 按下类（downOnce）：只在进入这一帧时按一次
            if (frame.KeysPressed != null)
            {
                for (int i = 0; i < frame.KeysPressed.Length; i++)
                {
                    string key = frame.KeysPressed[i];
                    TryInject("press:" + key, () => Executor.PulseKey(key));
                }
            }

            // 3) 视角：绝对朝向优先（瞬时转向），否则增量
            if (frame.HasAbsoluteLook)
            {
                float yaw = frame.YawDegrees;
                float pitch = frame.PitchDegrees;
                TryInject("look", () => Executor.Look(yaw, pitch));
            }
            else if (frame.LookDeltaYaw != 0f || frame.LookDeltaPitch != 0f)
            {
                float dyaw = frame.LookDeltaYaw * 180f / (float)Math.PI;
                float dpitch = frame.LookDeltaPitch * 180f / (float)Math.PI;
                TryInject("lookdelta", () => Executor.LookDelta(dyaw, dpitch));
            }

            if (frame.HasLookAt)
            {
                float x = frame.LookAtX, y = frame.LookAtY, z = frame.LookAtZ;
                TryInject("lookat", () => Executor.LookAt(x, y, z));
            }

            // 4) 鼠标
            if (!string.IsNullOrEmpty(frame.MouseAction))
            {
                string button = string.IsNullOrEmpty(frame.MouseButtonName) ? "left" : frame.MouseButtonName;
                string action = frame.MouseAction;
                TryInject("mouse:" + button + ":" + action, () => Executor.MouseAction(button, action));
            }

            if (frame.Wheel != 0)
            {
                int notches = frame.Wheel;
                TryInject("wheel", () => Executor.Wheel(notches));
            }

            if (frame.SelectSlot >= 0)
            {
                int slot = frame.SelectSlot;
                TryInject("slot:" + slot, () => Executor.SelectSlot(slot));
            }

            if (!string.IsNullOrEmpty(frame.UiClick))
            {
                string selector = frame.UiClick;
                TryInject("uiclick:" + selector, () => Executor.UiClick(selector));
            }

            if (!string.IsNullOrEmpty(frame.TypeText))
            {
                string text = frame.TypeText;
                TryInject("text", () => Executor.TypeText(text));
            }
        }

        /// <summary>
        /// 调一次执行器：**返回 false 记为拒绝但不立刻判失败**（键名在这台机器上不存在这类问题
        /// 不该让整条脚本断掉），但会写进轨迹，事后能查。真正的"要不要失败"由看门狗与守卫决定。
        /// </summary>
        private void TryInject(string what, Func<bool> action)
        {
            try
            {
                if (!action())
                    AppendTrace("refused:" + what);
            }
            catch (Exception exception)
            {
                AppendTrace("throw:" + what + ":" + exception.GetType().Name);
            }
        }

        private void ReleaseInputCore()
        {
            if (m_heldKeys.Count > 0 && Executor != null)
            {
                var keys = new List<string>(m_heldKeys);
                try
                {
                    Executor.HoldKeys(keys, false);
                }
                catch (Exception)
                {
                    // 释放失败也要把本地账清掉，否则会一直以为还按着
                }
                m_heldKeys.Clear();
            }
            if (Executor != null)
            {
                try
                {
                    Executor.ReleaseAll();
                }
                catch (Exception)
                {
                    // 收尾路径不抛异常（铁律：任何结束路径都释放）
                }
            }
        }

        private bool CheckGuards(out string error)
        {
            error = null;
            if (m_script == null || m_script.Guards.Count == 0)
                return true;
            if (Guards == null || !Guards.IsReady)
            {
                error = "guards are required by this script but no evaluator is available";
                return false;
            }
            for (int i = 0; i < m_script.Guards.Count; i++)
            {
                string guard = m_script.Guards[i];
                string reason;
                if (!Guards.Evaluate(guard, out reason))
                {
                    error = "guard not satisfied: " + guard
                        + (string.IsNullOrEmpty(reason) ? string.Empty : " (" + reason + ")");
                    return false;
                }
            }
            return true;
        }

        private void Fail(string code, string message)
        {
            ReleaseInput();
            State = ActionPlayState.Failed;
            ErrorCode = string.IsNullOrEmpty(code) ? ActionErrorCodes.VerbFailed : code;
            LastError = message;
            FailedStepIndex = m_stepIndex;
            AppendTrace("fail@" + m_stepIndex + ":" + ErrorCode + ":" + message);
        }

        private void AppendTrace(string line)
        {
            const int MaxTraceChars = 2048;
            if (m_trace.Length > MaxTraceChars)
                return;
            if (m_trace.Length > 0)
                m_trace.Append(" | ");
            m_trace.Append(line);
        }
    }
}

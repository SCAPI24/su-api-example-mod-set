using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树任务：**跑一条动作脚本**（P1 的核心节点，与 `Task.PlayActionPackage` 并列）。
    ///
    /// 定位（plan §3）：`PlayActionPackage` 放的是"人录下来的一条"；本节点放的是
    /// **可参数化、可组合的 verb 序列**（基础动作包混搭）。两者都只经玩家控制器注入，
    /// 走同一个 <see cref="IActionExecutor"/>。
    ///
    /// 语义：
    ///   · 跨帧（Latent），每帧推进脚本；跑完 Succeeded；
    ///   · 失败（看门狗超时 / 执行器不可用 / 守卫失效 / verb 参数错）→ **Failed + 稳定错误码**，
    ///     交回行为树决定重试或换分支（与 §4.12 的异常处理对接）；
    ///   · 被中断 / OnExit 一律释放输入（既有铁律）；
    ///   · 失败原因可选写进黑板键（`failKey`），供异常分支**提前规划**——
    ///     注意这是**给树看的，不是给 Laya 看的**（plan G22）。
    ///
    /// 参数化的两种用法：
    ///   · `script` 直接给脚本名；
    ///   · `scriptKey` 给一个**黑板 string 键**，运行时读它的值当脚本名 ——
    ///     于是 Laya 的判定结果（或其它节点算出的结论）能决定"跑哪条脚本"（plan §4.10 档②）。
    /// </summary>
    public sealed class BtRunActionScriptTask : BtTaskNode
    {
        /// <summary>脚本名或相对路径（`mine_stone_once` / `sub/mine.aeact`）。与 ScriptKey 二选一。</summary>
        public string Script { get; set; }

        /// <summary>黑板 string 键：运行时从它取脚本名（优先于 <see cref="Script"/>）。</summary>
        public string ScriptKey { get; set; }

        /// <summary>整条脚本重复几次（≥1）。</summary>
        public int Repeat { get; set; } = 1;

        /// <summary>整条脚本的总超时（毫秒）；0 = 用每步预算之和。</summary>
        public int TotalTimeoutMs { get; set; }

        /// <summary>失败时是否写黑板（把错误码交给后续异常分支）。</summary>
        public bool WriteFailKey { get; set; } = true;

        /// <summary>失败错误码写进哪个黑板 string 键（默认 `action.fail`）。</summary>
        public string FailKey { get; set; } = "action.fail";

        /// <summary>失败原因（人类可读）写进哪个黑板 string 键（默认 `action.reason`）。留空则不写。</summary>
        public string ReasonKey { get; set; } = "action.reason";

        /// <summary>当前脚本实际用的名字（观测/日志）。</summary>
        public string ResolvedScript { get; private set; }

        /// <summary>最近一次失败的错误码（`ai.status` / 黑板）。</summary>
        public string LastErrorCode { get; private set; }

        /// <summary>最近一次失败原因。</summary>
        public string LastError { get; private set; }

        private ActionQueuePlayer m_player;
        private int m_loop;

        public override string NodeType
        {
            get { return "Task.RunActionScript"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            StopPlayer("restart");
            LastErrorCode = null;
            LastError = null;

            string name = ResolveScriptName(context, out string resolveError);
            if (name == null)
            {
                return Fail(context, ActionErrorCodes.InvalidArgument, resolveError);
            }

            // `Resolve()` 而不是 `Current`：服务可能只是"还没人访问过运行时"（A34 —— 平板上开机后
            // 第一棵跑动作的树全都会失败，而 PC 上因为先跑过 ai.action.script.* 命令而不暴露）。
            IActionSkillProvider provider = ActionPlayServices.Resolve();
            if (provider == null)
            {
                return Fail(context, ActionErrorCodes.InjectRefused,
                    "action services are not initialized");
            }

            ActionScript script = provider.ResolveScript(name, out string scriptError);
            if (script == null)
            {
                return Fail(context, ActionErrorCodes.StepFailed,
                    "script '" + name + "' could not be resolved: " + (scriptError ?? "not found"));
            }

            ResolvedScript = name;
            m_loop = 0;

            string createError;
            m_player = ActionPlayServices.CreatePlayer(out createError);
            if (m_player == null)
            {
                return Fail(context, ActionErrorCodes.InjectRefused, createError);
            }

            if (TotalTimeoutMs > 0)
                m_player.TotalTimeoutMs = TotalTimeoutMs;

            context.Log("RunActionScript: start '" + name + "' steps=" + script.Steps.Count
                + " repeat=" + Math.Max(1, Repeat));

            if (!m_player.Start(script))
            {
                return Fail(context, m_player.ErrorCode, m_player.LastError);
            }

            return BtResult.InProgress;
        }

        protected override BtResult OnTick(BtContext context)
        {
            if (m_player == null)
                return BtResult.Failed;

            BtResult step = m_player.Tick(context.DeltaTime);
            if (step == BtResult.InProgress)
                return BtResult.InProgress;

            if (step == BtResult.Succeeded)
            {
                m_loop++;
                if (m_loop < Math.Max(1, Repeat))
                {
                    // 再跑一轮：用同一个脚本重新 Start（会先清残留输入）
                    ActionScript script = m_player.Script;
                    if (script == null || !m_player.Start(script))
                        return Fail(context, m_player.ErrorCode, m_player.LastError);
                    return BtResult.InProgress;
                }

                context.Log("RunActionScript: done '" + (ResolvedScript ?? "?") + "' loops=" + m_loop
                    + " t=" + (m_player.ElapsedMs / 1000.0).ToString("0.00") + "s");
                return BtResult.Succeeded;
            }

            // Failed / Aborted
            return Fail(context, m_player.ErrorCode ?? ActionErrorCodes.VerbFailed,
                m_player.LastError ?? "action script failed");
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            // 铁律：任何结束路径都释放输入（含被中断）
            StopPlayer(result.ToString());
        }

        public override void ResetState()
        {
            base.ResetState();
            StopPlayer("reset");
            LastErrorCode = null;
            LastError = null;
            m_loop = 0;
        }

        public override string ToString()
        {
            string name = ResolvedScript ?? Script ?? ("{" + ScriptKey + "}");
            return base.ToString() + " script=" + name + " repeat=" + Math.Max(1, Repeat);
        }

        // ---------------------------------------------------------------- 内部

        private string ResolveScriptName(BtContext context, out string error)
        {
            error = null;

            // 1) 黑板键优先（运行时决定跑哪条脚本 —— Laya/其它节点写的值）
            if (!string.IsNullOrEmpty(ScriptKey) && context.Blackboard != null)
            {
                string fromBoard;
                if (context.Blackboard.TryGet<string>(ScriptKey, out fromBoard)
                    && !string.IsNullOrEmpty(fromBoard))
                {
                    return fromBoard;
                }

                // 黑板键有配置但当前没值：这是"计划里的脚本还没定" —— 明确失败，别猜
                if (string.IsNullOrEmpty(Script))
                {
                    error = "blackboard key '" + ScriptKey + "' has no script name yet";
                    return null;
                }
            }

            if (!string.IsNullOrEmpty(Script))
                return Script;

            error = "node has neither 'script' nor 'scriptKey' configured";
            return null;
        }

        private BtResult Fail(BtContext context, string code, string reason)
        {
            LastErrorCode = string.IsNullOrEmpty(code) ? ActionErrorCodes.VerbFailed : code;
            LastError = reason ?? LastErrorCode;
            StopPlayer("fail");

            if (context != null)
                context.Warn("RunActionScript: " + LastErrorCode + " - " + LastError + " -> Failed");

            // 失败原因写给**树**（异常分支据此提前规划）；这不是给 Laya 的输入（plan G22）
            WriteBlackboard(context, WriteFailKey, FailKey, LastErrorCode);
            WriteBlackboard(context, WriteFailKey, ReasonKey, LastError);
            return BtResult.Failed;
        }

        private static void WriteBlackboard(BtContext context, bool enabled, string key, string value)
        {
            if (!enabled || context == null || context.Blackboard == null
                || string.IsNullOrEmpty(key))
            {
                return;
            }
            context.Blackboard.Set(new AiBlackboardKey<string>(key), value ?? string.Empty);
        }

        private void StopPlayer(string reason)
        {
            if (m_player == null)
                return;
            try
            {
                m_player.Stop(null, reason);
            }
            catch (Exception)
            {
                // 收尾路径不抛异常
            }
            m_player = null;
        }
    }
}

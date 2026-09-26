using CmdBridgeMod;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 脚本直放状态（`ai.action.script.status` / `ai.status.script`）。
    /// </summary>
    public sealed class ActionScriptPlayback
    {
        public ActionScript Script;
        public ActionQueuePlayer Player;
        public string Name;
        public long StartedTick;

        public bool Playing
        {
            get { return Player != null && Player.State == ActionPlayState.Playing; }
        }
    }

    /// <summary>
    /// 动作脚本的运行入口（P1）：**每帧帧首推进**，与录制回放共用时间轴。
    ///
    /// 为什么和 <c>ScatPlayer</c> 分开：录制回放要漂移检查（关键帧 vs 实际位置），
    /// 脚本回放不需要（它是"发意图"，不是"复现轨迹"）；但两者都遵守同一条铁律 ——
    /// 任何结束路径都释放输入。
    ///
    /// 世界外也能跑界面脚本：宿主拿不到角色执行器时退回 <see cref="UiOnlyActuator"/>
    /// （与 `PlayerAiRuntime.PlayAction` 的既有取舍完全一致：UI 点击不需要角色）。
    /// </summary>
    public sealed class ActionScriptRuntime
    {
        private readonly PlayerAiRuntime m_runtime;
        private ActionScriptPlayback m_current;

        /// <summary>
        /// 最近一次跑完的结果（**完成后仍然保留**）。
        /// 为什么必须有：脚本一跑完就"什么都没了"，人/LLM 想问"刚才那条成没成、为什么失败"只能翻日志。
        /// 这里留一份摘要（状态 / 错误码 / 原因 / 耗时 / 轨迹 / 时间戳）。
        /// </summary>
        private Dictionary<string, object> m_lastResult;

        public ActionScriptRuntime(PlayerAiRuntime runtime)
        {
            m_runtime = runtime;
        }

        public ActionScriptPlayback Current
        {
            get { return m_current; }
        }

        /// <summary>当前宿主能用的动作执行器（拿不到角色时退回只做 UI 的兜底执行器）。</summary>
        public IActionExecutor ResolveExecutor(out string error)
        {
            error = null;
            IAiTreeHost host = m_runtime != null ? m_runtime.ResolveTreeHost() : null;
            IAiActuator actuator = host != null ? host.Actuators : null;

            if (actuator == null || !actuator.IsReady)
            {
                if (CmdBridgeActuator.IsAvailable)
                    actuator = new UiOnlyActuator();
            }
            if (actuator == null || !actuator.IsReady)
            {
                error = "input injection is unavailable (CmdBridgeMod missing or disabled)";
                return null;
            }
            return new GameActionExecutor(actuator);
        }

        /// <summary>按名字直接跑一条脚本（`ai.action.script.run`）。返回人类可读结果。</summary>
        public string Play(string nameOrPath, int repeat, int totalTimeoutMs)
        {
            // **问运行时拿服务，不读全局静态**（plan A10/A14）：实机出现过
            // "命令层读到的静态为 null、而运行时手里明明有服务"，导致同一条命令时好时坏。
            // 运行时是唯一权威；它自己会懒创建并 Publish 一份给静态（兼容别的读取点）。
            IActionSkillProvider provider = m_runtime != null
                ? (IActionSkillProvider)m_runtime.ActionServices
                : ActionPlayServices.Current;
            if (provider == null)
                throw new AiCommandException("not_ready", "action services are not initialized");

            ActionScript script = provider.ResolveScript(nameOrPath, out string resolveError);
            if (script == null)
            {
                // §4.12：脚本取不到也走同一条分流链。重取只做"读取 + 编译"（`RetryActionAsset`
                // 里的 `TryProbeScript`），**不会自己重放** —— 重放的副作用是角色真的动起来。
                // 于是"编辑器正在原子替换 .aeact"这类瞬时失败会被退避重取消化掉。
                if (m_runtime != null)
                {
                    m_runtime.NoteAssetFailure(nameOrPath, resolveError, null,
                        AssetFailureKind.None, AssetResourceKind.Script);
                }
                throw new AiCommandException("file_missing",
                    "script not found or invalid: '" + (nameOrPath ?? "<none>") + "' - " + resolveError);
            }

            // 读到了 → 清掉这个脚本的重试历史与失败标记
            if (m_runtime != null)
                m_runtime.NoteAssetSuccess(nameOrPath);

            IActionExecutor executor = ResolveExecutor(out string executorError);
            if (executor == null)
                throw new AiCommandException("not_ready", executorError);

            Stop();

            var player = new ActionQueuePlayer(executor, provider.Targets, provider.Guards);
            if (totalTimeoutMs > 0)
                player.TotalTimeoutMs = totalTimeoutMs;

            m_current = new ActionScriptPlayback
            {
                Script = script,
                Player = player,
                Name = nameOrPath,
                StartedTick = m_runtime != null ? m_runtime.TickCount : 0
            };

            bool started = player.Start(script);
            if (!started && player.State != ActionPlayState.Playing)
            {
                string code = player.ErrorCode;
                string reason = player.LastError;
                m_current = null;
                throw new AiCommandException(
                    string.IsNullOrEmpty(code) ? "failed" : code, reason ?? "script could not start");
            }

            if (m_runtime != null)
                m_runtime.EventLog.Write("action-script", script.Describe() + " repeat=" + Math.Max(1, repeat));
            m_runtime?.ShowMessage("AI: 脚本 " + (script.Id ?? nameOrPath));
            return "playing " + script.Describe();
        }

        /// <summary>停止并释放输入。没有在跑时返回 false。</summary>
        public bool Stop()
        {
            if (m_current == null)
                return false;
            try
            {
                m_current.Player.Stop("stopped by user");
            }
            catch (Exception)
            {
                // 收尾不抛
            }
            m_current = null;
            return true;
        }

        /// <summary>帧首推进（由 <c>PlayerAiRuntime.TickFrameStart</c> 调用）。</summary>
        public void Tick(float deltaTime)
        {
            ActionScriptPlayback current = m_current;
            if (current == null)
                return;

            BtResult result;
            try
            {
                result = current.Player.Tick(deltaTime);
            }
            catch (Exception exception)
            {
                if (m_runtime != null)
                    m_runtime.EventLog.Write("action-script-error",
                        exception.GetType().Name + ": " + exception.Message);
                try { current.Player.ReleaseInput(); } catch (Exception) { }
                m_current = null;
                return;
            }

            if (result == BtResult.InProgress)
                return;

            string summary = current.Name + " -> " + result
                + (current.Player.ErrorCode != null ? " (" + current.Player.ErrorCode + ")" : string.Empty)
                + " t=" + (current.Player.ElapsedMs / 1000.0).ToString("0.00") + "s";
            m_lastResult = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = current.Name,
                ["scriptId"] = current.Script != null ? current.Script.Id : null,
                ["result"] = result.ToString().ToLowerInvariant(),
                ["steps"] = current.Player.StepCount,
                ["failedStep"] = current.Player.FailedStepIndex,
                ["elapsedMs"] = Math.Round(current.Player.ElapsedMs),
                ["errorCode"] = current.Player.ErrorCode,
                ["lastError"] = current.Player.LastError,
                ["trace"] = current.Player.Trace,
                ["finishedAtUtc"] = DateTime.UtcNow.ToString("o")
            };
            if (m_runtime != null)
            {
                m_runtime.EventLog.Write(result == BtResult.Succeeded ? "action-script-done" : "action-script-failed",
                    summary + (current.Player.LastError != null ? " :: " + current.Player.LastError : string.Empty));
                if (result != BtResult.Succeeded)
                    m_runtime.ShowMessage("AI: 脚本失败 " + (current.Player.ErrorCode ?? "failed"));
            }

            try { current.Player.ReleaseInput(); } catch (Exception) { }
            m_current = null;
        }

        /// <summary>状态摘要。</summary>
        public Dictionary<string, object> Status()
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            ActionScriptPlayback current = m_current;
            info["playing"] = current != null;
            if (m_lastResult != null)
                info["last"] = m_lastResult;
            if (current == null)
                return info;

            ActionQueuePlayer player = current.Player;
            info["name"] = current.Name;
            info["scriptId"] = current.Script != null ? current.Script.Id : null;
            info["state"] = player.State.ToString().ToLowerInvariant();
            info["step"] = player.StepIndex + 1;
            info["steps"] = player.StepCount;
            info["verb"] = player.CurrentVerb();
            info["elapsedMs"] = Math.Round(player.ElapsedMs);
            info["totalTimeoutMs"] = player.TotalTimeoutMs;
            info["errorCode"] = player.ErrorCode;
            info["lastError"] = player.LastError;
            info["trace"] = player.Trace;
            return info;
        }

        /// <summary>卸载/世界卸载时的兜底（不抛异常）。</summary>
        public void ReleaseAll()
        {
            if (m_current == null)
                return;
            try { m_current.Player.ReleaseInput(); } catch (Exception) { }
            m_current = null;
        }
    }
}

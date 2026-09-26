using CmdBridgeMod;
using Engine;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// `ai.*` 命令到 **CmdBridgeMod 控制通道**的绑定（P0-7）。
    ///
    /// 这一层只干三件事：把 CmdBridgeMod 的请求转成 <see cref="AiCommandRequest"/>、
    /// 把 <see cref="AiCommandException"/> 映射成带错误码的 `CmdBridgeCommandException`、
    /// 把 <see cref="PlayerAiRuntime"/> 包成 <see cref="IAiCommandContext"/>。
    /// **命令语义一行都不在这里**（在 <see cref="AiCommandSet"/>），所以命令层能脱离游戏自检。
    /// </summary>
    public static class AiCommandBridge
    {
        /// <summary>注册者标识：卸载时按它整批摘掉，绝不误删别的 Mod 的命令。</summary>
        public const string Owner = "PlayerAiMod";

        /// <summary>把全部 `ai.*` 命令挂到当前 CmdBridgeMod 实例上，返回成功注册的条数。</summary>
        public static int RegisterAll(PlayerAiRuntime runtime)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null || runtime == null)
                return 0;

            var context = new RuntimeCommandContext(runtime);
            Dictionary<string, Func<AiCommandRequest, IAiCommandContext, object>> handlers =
                AiCommandSet.BuildHandlers();

            int registered = 0;
            foreach (KeyValuePair<string, Func<AiCommandRequest, IAiCommandContext, object>> pair in handlers)
            {
                string name = pair.Key;
                Func<AiCommandRequest, IAiCommandContext, object> handler = pair.Value;

                string description;
                AiCommandSet.CommandDescriptions.TryGetValue(name, out description);

                bool ok = facade.RegisterCommand(name, delegate (CmdBridgeCommandRequest request)
                {
                    var view = new AiCommandRequest(request.Command,
                        new Dictionary<string, object>(request.Arguments,
                            StringComparer.OrdinalIgnoreCase));
                    try
                    {
                        return handler(view, context);
                    }
                    catch (AiCommandException exception)
                    {
                        // 错误码原样带到客户端：脚本按码分支，不看文案
                        throw new CmdBridgeCommandException(exception.Code, exception.Message);
                    }
                }, Owner, true, description);

                if (ok)
                    registered++;
            }

            Engine.Log.Information("[PlayerAi][cmd] registered " + registered + "/"
                + handlers.Count + " command(s) on the CmdBridge channel: "
                + string.Join(", ", AiCommandSet.CommandNames));
            return registered;
        }

        /// <summary>摘掉本 Mod 全部命令（Mod 卸载时调用）。返回摘掉的条数。</summary>
        public static int UnregisterAll()
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return 0;

            int removed = facade.UnregisterAllCommands(Owner);
            if (removed > 0)
                Engine.Log.Information("[PlayerAi][cmd] unregistered " + removed + " command(s).");
            return removed;
        }

        private static CmdBridgeInput Facade
        {
            get
            {
                try
                {
                    CmdBridgeMod.CmdBridgeMod mod = CmdBridgeMod.CmdBridgeMod.Instance;
                    return mod != null ? mod.Input : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>把运行时的动作包播放能力包成命令层接口。</summary>
        private sealed class RuntimeActionPlayer : IAiActionPlayer
        {
            private readonly PlayerAiRuntime m_runtime;

            public RuntimeActionPlayer(PlayerAiRuntime runtime)
            {
                m_runtime = runtime;
            }

            public string ActionDirectory
            {
                get { return m_runtime.ResolveActionDirectory(); }
            }

            public List<string> ActionSearchDirectories
            {
                get { return m_runtime.ResolveActionDirectories(); }
            }

            public string Play(string nameOrPath, int repeat)
            {
                return m_runtime.PlayAction(nameOrPath, repeat);
            }

            public string Stop()
            {
                return m_runtime.StopAction();
            }

            public Dictionary<string, object> Status()
            {
                return m_runtime.ActionStatus();
            }
        }

        /// <summary>把运行时包成命令上下文（命令层只看到接口）。</summary>
        private sealed class RuntimeCommandContext : IAiCommandContext
        {
            private readonly PlayerAiRuntime m_runtime;

            public RuntimeCommandContext(PlayerAiRuntime runtime)
            {
                m_runtime = runtime;
            }

            public IAiTreeHost Host
            {
                get { return m_runtime.ResolveTreeHost(); }
            }

            public PackageReloader Reloader
            {
                get { return m_runtime.Reloader; }
            }

            public TreeLibrary Library
            {
                get { return m_runtime.Library; }
            }

            public AiEventLog EventLog
            {
                get { return m_runtime.EventLog; }
            }

            public bool Paused
            {
                get { return m_runtime.Paused; }
            }

            public string PauseReason
            {
                get { return m_runtime.PauseReason; }
            }

            public bool InputAvailable
            {
                get { return CmdBridgeActuator.IsAvailable; }
            }

            public string Phase
            {
                get { return m_runtime.Phase; }
            }

            public long PhaseChanges
            {
                get { return m_runtime.PhaseChanges; }
            }

            public long PhaseGatedFrames
            {
                get { return m_runtime.PhaseGatedFrames; }
            }

            public bool PhaseGateActive
            {
                get { return m_runtime.PhaseGateActive; }
            }

            public DecisionLog Decisions
            {
                get
                {
                    // 惰性建 Laya 服务：`ai.laya.review` 应该在"还没人问过"时也能回答
                    // （空表 + 全零聚合本身就是有效信息）。
                    LayaRuntimeService laya = m_runtime.Laya;
                    return laya != null ? laya.Decisions : null;
                }
            }

            public IAiRecordingControl Recording
            {
                get { return m_runtime.Recording; }
            }

            public IAiActionPlayer ActionPlayer
            {
                get { return new RuntimeActionPlayer(m_runtime); }
            }

            public void Pause(string reason)
            {
                m_runtime.Pause(reason);
            }

            public void Resume(string reason)
            {
                m_runtime.Resume(reason);
            }
        }
    }
}

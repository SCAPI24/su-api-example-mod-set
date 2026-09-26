using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 动作技能的服务提供者：把"执行器 / 语义目标解析 / 守卫判定 / 脚本来源"这四样东西
    /// 交给动作节点，**动作节点本身不依赖游戏类型**（于是能在临时工程里自检）。
    ///
    /// 游戏侧的实现是 <c>GameActionServices</c>（在 <see cref="PlayerAiRuntime"/> 启动时装配）；
    /// 没装配时节点判 Failed 并给明确原因，而不是"静默什么都不做"。
    /// </summary>
    public interface IActionSkillProvider
    {
        /// <summary>动作执行出口（把一帧动作交给玩家控制器）。</summary>
        IActionExecutor Executor { get; }

        /// <summary>语义目标解析（§4.9：Laya 给枚举，这里给具体格子/实体）。</summary>
        IActionTargetResolver Targets { get; }

        /// <summary>守卫判定（词汇与 `obs.waitFor` 一致）。</summary>
        IActionGuardEvaluator Guards { get; }

        /// <summary>按名字/路径解析脚本；找不到时返回 null 并给原因。</summary>
        ActionScript ResolveScript(string nameOrPath, out string error);

        /// <summary>脚本搜索目录（`ai.action.script.list` 与编辑器用；可为空列表）。</summary>
        IReadOnlyList<string> ScriptDirectories { get; }
    }

    /// <summary>
    /// 动作技能的全局装配点（与 <c>PlayerAiRuntime.Instance</c> 同样的"一个 Mod 一份"模式）。
    /// 用静态而不是构造函数注入的原因：节点由包编译器按注册表的工厂创建，
    /// 而注册表是静态的、包格式里只写类型与属性 —— 与既有 <c>CmdBridgeActuator</c> 拿门面的方式一致。
    /// </summary>
    public static class ActionPlayServices
    {
        /// <summary>当前提供者（Mod 启动时装配；卸载时置空）。</summary>
        public static IActionSkillProvider Current { get; set; }

        /// <summary>
        /// **按需取**提供者的回调（`null` = 没有）。由 <c>PlayerAiRuntime</c> 挂上。
        ///
        /// 为什么必须有它（与 `LayaRuntimeHost.Provider` / `PoolRuntimeHost.LibraryProvider` 同一个理由）：
        /// `Current` 只在"有人访问过 `PlayerAiRuntime.ActionServices`"之后才有值，而**行为树节点**
        /// 属于纯逻辑层、不认识运行时。于是"开机后第一棵跑 `Task.RunActionScript` 的树"会失败并写
        /// `action.fail=inject_refused / action.reason='action services are not initialized'`
        /// —— 服务其实完全可用，只是没人先碰过它。
        ///
        /// **实机症状**（A34）：同一棵树在 PC 上跑得好好的（因为我先执行过 `ai.action.script.*` 命令，
        /// 服务被创建了），在平板上却每个动作都立刻失败 —— "先跑过一条命令"这种隐性前提最难查。
        /// </summary>
        public static Func<IActionSkillProvider> Provider { get; set; }

        /// <summary>当前提供者：先看**已装配**的，再问**按需取**的回调。</summary>
        public static IActionSkillProvider Resolve()
        {
            IActionSkillProvider provider = Current;
            if (provider != null)
                return provider;

            Func<IActionSkillProvider> factory = Provider;
            return factory != null ? factory() : null;
        }

        /// <summary>
        /// 新建一个动作播放器（自动带上当前的执行器/解析器/守卫）。
        /// 返回 null 表示服务未装配（调用方据此给出明确失败，而不是空转）。
        /// </summary>
        public static ActionQueuePlayer CreatePlayer(out string error)
        {
            error = null;
            IActionSkillProvider provider = Resolve();
            if (provider == null)
            {
                error = "action services are not initialized (PlayerAiMod runtime did not register them)";
                return null;
            }

            var player = new ActionQueuePlayer(provider.Executor, provider.Targets, provider.Guards);
            return player;
        }

        /// <summary>是否已具备执行条件（节点开局守卫用）。</summary>
        public static bool IsReady
        {
            get
            {
                IActionSkillProvider provider = Resolve();
                return provider != null && provider.Executor != null && provider.Executor.IsReady;
            }
        }

        /// <summary>给人看的现状（`ai.status` / `ai.action.script.status`）。</summary>
        public static Dictionary<string, object> Describe()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            IActionSkillProvider provider = Resolve();
            result["registered"] = provider != null;
            result["executorReady"] = provider != null && provider.Executor != null && provider.Executor.IsReady;
            result["targetResolverReady"] = provider != null && provider.Targets != null;
            result["guardEvaluatorReady"] = provider != null && provider.Guards != null && provider.Guards.IsReady;
            if (provider != null)
            {
                var directories = new List<string>();
                if (provider.ScriptDirectories != null)
                {
                    for (int i = 0; i < provider.ScriptDirectories.Count; i++)
                        directories.Add(provider.ScriptDirectories[i]);
                }
                result["scriptDirectories"] = directories;
            }
            result["verbs"] = new List<string>(ScriptCompiler.VerbNames);
            return result;
        }
    }
}

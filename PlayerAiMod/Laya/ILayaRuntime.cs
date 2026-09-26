using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树节点看到的 Laya 运行时。**抽成接口是为了让 <see cref="BtLayaAskTask"/> 能被编辑器编译**
    /// （编辑器复用本 Mod 的纯逻辑层，但不带游戏侧胶水）。
    ///
    /// 真实实现是 <c>LayaRuntimeService</c>（游戏内）；编辑器侧给"永远答不上来"的桩，
    /// 于是校验器/编译器/物料区都能看到这个节点类型与属性表，而不会把引擎/网络依赖拖进编辑器。
    /// </summary>
    public interface ILayaRuntime
    {
        /// <summary>当前配置（节点要读 timeoutMs / digestBudgetChars）。</summary>
        LayaConfig Config { get; }

        /// <summary>HTTP 客户端（节点用它发问与取结果）。可为 null（桩实现）。</summary>
        LayaClient Client { get; }

        /// <summary>按名字解析问题库；`only` 是逗号分隔的问题 id 子集。</summary>
        bool TryResolveBank(string nameOrPath, string only, out QuestionBank bank, out string error);

        /// <summary>编译当前状态摘要（拿不到观察时返回 null + 原因）。</summary>
        string CompileDigest(BtContext context, out string error);

        /// <summary>
        /// 当前状态摘要的**结构化字段**（键 → 文本值）—— `Service.ObserveState` 用它把状态写进黑板，
        /// 于是树能按 `state.phase` / `state.ui` 这类**枚举**做确定性分流（不必问模型）。
        ///
        /// 与 <see cref="CompileDigest"/> **共用同一张字段表**（`StateDigestCompiler.Fields()`）：
        /// 摘要里有的字段，树里就查得到；反之亦然。两处各维护一份必然漂移。
        /// 拿不到观察时返回 false + 原因。
        /// </summary>
        bool TryObserveFields(out List<KeyValuePair<string, string>> fields, out string error);
    }

    /// <summary>
    /// Laya 运行时的装配点：由 <c>PlayerAiRuntime</c> 在首次访问时写入。
    /// 节点只认接口 + 这个装配点，**不认识具体实现类** —— 于是编辑器能在不带游戏胶水的情况下编译本节点。
    /// </summary>
    public static class LayaRuntimeHost
    {
        public static ILayaRuntime Current { get; set; }

        /// <summary>
        /// **按需取服务的回调**（`null` = 没有）。
        ///
        /// 为什么要有它：`Current` 是"已经建好"的静态发布点，而"谁负责建"只有游戏侧知道
        /// （`PlayerAiRuntime`）—— 但 `BtLayaAskTask` 属于**纯逻辑层**，编辑器也编它
        /// （`PlayerAiEditor.csproj` 只收 `Core/` 里的几个文件，不含 `PlayerAiRuntime`）。
        /// 直接引用运行时会**把编辑器编译打断**（实测踩过）。这里照 `PoolRuntimeHost` 的老办法：
        /// 运行时往这里挂一个委托，纯逻辑层只认委托，不认识运行时。
        /// </summary>
        public static Func<ILayaRuntime> Provider { get; set; }
    }

    /// <summary>Laya 运行时的**空实现**（编辑器/自检用：永远"还没接上"）。</summary>
    public sealed class NullLayaRuntime : ILayaRuntime
    {
        public NullLayaRuntime(LayaConfig config = null)
        {
            Config = config ?? new LayaConfig { Enabled = false };
        }

        public LayaConfig Config { get; }

        public LayaClient Client
        {
            get { return null; }
        }

        public bool TryResolveBank(string nameOrPath, string only, out QuestionBank bank, out string error)
        {
            bank = null;
            error = "the Laya runtime is not available in this host (editor/self-test)";
            return false;
        }

        public string CompileDigest(BtContext context, out string error)
        {
            error = "the Laya runtime is not available in this host (editor/self-test)";
            return null;
        }

        public bool TryObserveFields(out List<KeyValuePair<string, string>> fields, out string error)
        {
            fields = null;
            error = "the Laya runtime is not available in this host (editor/self-test)";
            return false;
        }
    }
}

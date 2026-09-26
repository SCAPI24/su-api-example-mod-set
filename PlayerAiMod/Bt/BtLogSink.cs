using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树内"记一行"（`Task.Log`）的落点。
    ///
    /// **为什么需要这个缝**：`BtContext` 属于纯逻辑层（编辑器与自检都要编它），它不认识
    /// `AiEventLog`；而"树里记的那一行"恰恰是用户复盘时最想看的（"Laya 说 craft → 跑了哪条脚本"、
    /// "异常分支为什么走了这条路"）。没有这个缝时，`Task.Log` 只在 `PlayerAiConfig.VerboseLogging`
    /// 打开时才写引擎日志 —— 于是**出厂异常示例里的"先记日志再清旗标"在默认配置下是一条也看不到的**，
    /// 用户看到的现象是"旗标被清了，但没有任何解释"。
    ///
    /// 做法与 `PoolRuntimeHost` / `LayaRuntimeHost` 同一套：运行时往这里挂一个委托，
    /// 纯逻辑层只认委托。运行时装的是事件日志（`ai.logs` 看得到）；编辑器/自检里为 null = 什么都不做。
    /// </summary>
    public static class BtLogSink
    {
        /// <summary>事件日志里的类别名（`ai.logs` 那一列看到的）。</summary>
        public const string TreeKind = "tree";

        /// <summary>写一行的回调（`kind` = 事件类别，`message` = 内容）。null = 不记。</summary>
        public static Action<string, string> Write { get; set; }

        /// <summary>记一行；**任何异常都吞掉** —— 记账失败绝不能把树 tick 打断。</summary>
        public static void Emit(string kind, string message)
        {
            Action<string, string> sink = Write;
            if (sink == null || string.IsNullOrEmpty(message))
                return;
            try
            {
                sink(kind, message);
            }
            catch (Exception)
            {
                // 记账是旁路：写不进去就算了（事件日志本身有 lastError 记录）
            }
        }
    }
}

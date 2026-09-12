namespace PlayerAiMod
{
    /// <summary>
    /// 行为树节点执行结果 —— 对齐虚幻引擎的 `EBTNodeResult::Type`
    /// （Succeeded / Failed / Aborted / InProgress）。
    ///
    /// 语义约定：
    ///   · 只有 <see cref="InProgress"/> 会让节点保持"获得焦点"，下一帧继续收到 Tick。
    ///   · <see cref="Aborted"/> 只由外部中断产生（装饰器条件变化、树被替换、强制停止），
    ///     节点内部不会主动返回它。
    /// </summary>
    public enum BtResult
    {
        Succeeded,
        Failed,
        Aborted,
        InProgress
    }
}

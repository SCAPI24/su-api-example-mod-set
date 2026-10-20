using System;
using System.Collections.Generic;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 客户端输入 / 对话框提交到模块的唯一入口（实现方 = Modules/EditableSyncModule.cs）。
    /// 返回 true = 请求已入队发送；false = 明确失败（<paramref name="reason"/> 必有值）。
    /// 调用方（Engine/EditableBlockSubsystems.cs 的对话框提交）**绝不**在本地写副本内容。
    /// </summary>
    public interface IEditableRequestSink
    {
        bool TrySubmitBlockEdit(in EditableEditRequestData request, out string reason);
    }

    /// <summary>主机落地一次可编辑内容提交后的结果：权威内容快照（revision 已前进）。</summary>
    public sealed class EditableHostOutcome
    {
        public EditableHostOutcome(EditableStateData state)
        {
            State = state;
        }

        /// <summary>落地后的权威内容（含主机新 revision）。</summary>
        public EditableStateData State { get; }

        public int Revision => State?.Revision ?? 0;
    }

    /// <summary>
    /// 可编辑方块内容同步的引擎适配端口（实现 = Engine/EditableBlockBridge.cs）。
    ///
    /// 模块只做协议 / 序号 / 去重 / 超时 / 计数 / 广播目标；
    /// 所有涉及 Game 类型的操作（读 / 写 SubsystemEditableItemBehavior 的方块数据、
    /// 对话框、距离与身份校验、电路重新排队）都在实现里。
    ///
    /// 权威模型与 BlockUse / Container 相同：
    ///   · 客户端提交 → 请求；本地副本**不写**；
    ///   · 主机校验并落地权威内容 → 广播权威 <see cref="EditableStateData"/>；
    ///   · 客户端只把权威内容写进副本（表现），拒绝时按主机内容回退。
    /// </summary>
    public interface IEditableBlockPort
    {
        /// <summary>每帧一次（模块 Tick 调用）：跟踪 Project / 角色变化，作废旧 revision。</summary>
        void Pump();

        /// <summary>模块在 Attach 时写入自己，Detach 时写 null。</summary>
        void SetRequestSink(IEditableRequestSink sink);

        /// <summary>
        /// 主机本机玩家的对话框提交已落到权威内容（不含客户端请求路径）。
        /// 模块订阅它，把权威内容广播给该格附近的客户端（单机 / 没有客户端时不发）。
        /// </summary>
        event Action<EditableStateData> HostStateChanged;

        // ------------------------------------------------------------------ 客户端
        /// <summary>该格已应用的权威 revision（没有 → 0；对话框提交时作为 base_revision）。</summary>
        int GetClientRevision(int kind, int x, int y, int z);

        /// <summary>
        /// 客户端对话框提交：校验内容形状 → 交给模块发请求。**不写本地副本**。
        /// 返回 false = 明确失败（端口会写计数 / 限流日志，调用方不猜结果）。
        /// </summary>
        bool SubmitClientEdit(int kind, int x, int y, int z, int contents, int baseRevision,
            byte[] baseData, byte[] data, out string reason);

        /// <summary>
        /// 客户端把主机权威内容写进副本（表现）。revision 更旧 → Stale；
        /// 副本子系统缺失 / 内容非法 → Failed（reason 必有值）。
        /// </summary>
        EditableStateApplyOutcome ApplyEditableState(EditableStateData state, out string reason);

        /// <summary>主机已判定该格不再是这种方块（block-changed）→ 丢弃副本内容。</summary>
        void DropReplicaContent(int kind, int x, int y, int z, string reason);

        /// <summary>客户端拒绝 / 超时的显式提示（小消息，不静默）。</summary>
        void NotifyEditRejected(int kind, int x, int y, int z, string reason);

        // ------------------------------------------------------------------ 主机
        /// <summary>
        /// 校验并落地一次客户端提交：请求者身份 / 距离 / 方块格 / 内容形状 /
        /// base 内容未变化（revision 只用于说明原因）。成功时 outcome.State 为权威内容。
        /// </summary>
        bool TryApplyEditableEdit(int clientId, in EditableEditRequestData request,
            out EditableHostOutcome outcome, out string reason);

        /// <summary>抓取一格当前权威内容（拒绝时发给请求者回退用）。</summary>
        bool TryCaptureEditableState(int x, int y, int z, int kind,
            out EditableStateData state, out string reason);

        /// <summary>
        /// 主机：收集"该格附近、需要收到权威内容"的其它客户端（请求者由模块单独发）。
        /// 返回因距离过远 / 取不到角色位置而跳过的客户端数（明确计数，不静默）。
        /// </summary>
        int CollectEditableStateRecipients(int x, int y, int z, int exceptClientId,
            List<int> recipients);

        /// <summary>
        /// 主机**自己**（本机玩家）在对话框里提交的编辑：直接落到权威内容并返回权威快照。
        /// 落地成功后端口会触发 <c>HostStateChanged</c>，由模块广播给附近客户端。
        /// </summary>
        bool TryApplyHostLocalEdit(int kind, int x, int y, int z, int cellValue, byte[] data,
            out EditableStateData state, out string reason);

        string Describe();
    }
}

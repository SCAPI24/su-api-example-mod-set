using System;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 主机侧"加入审批"决定器（由组合根注入 Terrain 层）：
    /// 旧联机 mod 的口径 —— 玩家端的 join 请求必须**主机同意**才继续握手；
    /// 设置"自动同意加入"打开时直接放行（此时 <see cref="AutoApprove"/> = true）。
    ///
    /// 审批发生在**任何角色记录 / 角色实体 / 世界传输之前**：被拒绝的连接不会在主机上留下
    /// 角色记录或联机化身，客户端收到 HelloAck.rejected + 原因。
    /// Terrain 层不认识 UI / 引擎对话框，所以决定异步回传（UI 线程点 Allow/Reject）。
    /// </summary>
    public interface IJoinApprovalDecider
    {
        /// <summary>true = 自动同意所有加入（设置 autoapprovejoins=true），不再弹审批界面。</summary>
        bool AutoApprove { get; }

        /// <summary>
        /// 请求主机决定。实现方必须在有限时间内回调 <paramref name="decided"/>（true=同意 / false=拒绝），
        /// 回调可来自任意线程（Terrain 层会用锁保护状态）；超时由 Terrain 层兜底拒绝并写清原因。
        /// </summary>
        void RequestDecision(int clientId, string name, string identity, Action<bool, string> decided);
    }
}

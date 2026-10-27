using Game;
using SuMultiplayer.Modules;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端"主机丢失 → 退出联机房间"的引擎侧实现（<see cref="IClientSessionExitPort"/>）。
    ///
    /// 产品口径（用户 2026-10-26）：主机掉线 / 重启后，客户端必须退出房间回到世界列表，
    /// 绝不带着旧状态自动连进主机新建的房间。
    ///
    /// 执行时机：模块 tick（Frame.Update 链上）只**登记**退出请求；真正的退世界在
    /// 下一帧头部由组合根调 <see cref="DrainPendingExit"/> 执行——避免在模块 tick 中途
    /// DisposeProject 把其余订阅者脚下的 Project 抽掉。
    ///
    /// 退世界路径对齐引擎自己的退出（Source: Survivalcraft/Game/GameMenuDialog.cs:294-298）：
    /// SaveProject(waitForCompletion) → DisposeProject → SwitchScreen。切到 Play 屏后
    /// <c>SuPlayScreen.Enter</c> 会自动调 <c>SessionModule.LeaveRoom</c>（停通道、
    /// 复位加入流程），通道一停 m_wantClient 也复位 —— 僵尸重连链路就此打断。
    /// </summary>
    public sealed class ClientSessionExit : IClientSessionExitPort
    {
        private string m_pendingReason;
        private bool m_pending;

        bool IClientSessionExitPort.TryExitWorldOnHostLost(string reason)
        {
            // 是否"已在联机世界里"由引擎侧判定：加入流程（Play/BusyDialog/传输中）不算，
            // 维持既有的按间隔重试语义（主机可能只是比客户端晚建房）。
            if (!(ScreensManager.CurrentScreen is GameScreen)) return false;
            m_pendingReason = reason ?? string.Empty;
            m_pending = true;
            return true;
        }

        /// <summary>
        /// 下一帧头部执行登记的退出（组合根在 Frame.Update 订阅最开始调用）。
        /// 返回 true = 本次真的执行了退世界。
        /// </summary>
        public bool DrainPendingExit()
        {
            if (!m_pending) return false;
            m_pending = false;
            string reason = m_pendingReason;
            m_pendingReason = null;
            if (!(ScreensManager.CurrentScreen is GameScreen))
            {
                // 登记与执行之间玩家自己退出了世界（LeaveRoom 已把通道停掉）→ 无事可做。
                global::Engine.Log.Information("[SuMP][session] 主机丢失退出：玩家已自行离开世界，不再重复退出");
                return false;
            }
            global::Engine.Log.Information("[SuMP][session] 主机丢失，退出联机房间（存盘 → 卸载世界 → 回世界列表）。原因：" +
                (string.IsNullOrEmpty(reason) ? "(通道失效)" : reason));
            GameManager.SaveProject(waitForCompletion: true, showErrorDialog: true);
            GameManager.DisposeProject();
            ScreensManager.SwitchScreen("Play");
            // 游戏界面字体没有中文字形：底层 reason 只写日志，UI 只显示英文固定文案。
            DialogsManager.ShowDialog(null, new MessageDialog("Multiplayer",
                "Connection to the host was lost. You have left the room.",
                "OK", null, null));
            return true;
        }
    }
}

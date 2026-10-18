using System;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 客户端**握手资料视图**：由地形模块（唯一收到 HelloAck 的模块）提供，
    /// 联机角色模块只通过这个端口读取"主机资料 / 主机分给我的资料 / 是否要求先建角色"，
    /// 不直接引用地形层内部状态。
    ///
    /// 语义（照旧实现）：
    ///   · <see cref="HostProfile"/>     = HelloAck.host_profile（客户端据此建主机化身）；
    ///   · <see cref="AssignedProfile"/> = HelloAck.assigned_profile（主机角色记录里的本机资料）；
    ///   · <see cref="ProfileRequired"/> = 主机没有本机角色记录且 Hello 没带完整资料
    ///     → 客户端必须打开角色创建界面（旧 SCMP_PROFILE_REQUIRED），然后重新握手。
    ///
    /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:254-278
    ///         （RefuseJoinGame("SCMP_PROFILE_REQUIRED") → 打开 SuNetworkPlayerScreen →
    ///          SubmitPendingJoin(hasPlayerProfile: true) 重新加入）
    /// </summary>
    public interface IPlayerProfileHandshakePort
    {
        PlayerProfileSnapshot HostProfile { get; }

        PlayerProfileSnapshot AssignedProfile { get; }

        bool ProfileRequired { get; }

        string ProfileRequiredReason { get; }

        /// <summary>
        /// 角色创建完成 / 资料变化后**重新握手**（旧实现同样是重新 JoinGame）。
        /// 返回 false = 当前没有可重连的客户端通道（已明确记录，不假装重连成功）。
        /// </summary>
        bool ReconnectClient(string reason);
    }
}

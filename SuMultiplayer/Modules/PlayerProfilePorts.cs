using System;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>主机 UI 用的在线玩家端条目（clientId / 名字 / 身份，身份是不可逆 UserId）。</summary>
    public sealed class HostPeerInfo
    {
        public int ClientId;
        public string Name = string.Empty;
        public string Identity = string.Empty;
    }

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
        /// HelloAck.assigned_skin_asset_present：主机角色记录里已经保存了本机资料对应的
        /// 自定义皮肤资源。true 时客户端不需要重新上传（主机会在加入时主动补发）；
        /// false / 旧主机 = 客户端在握手后显式上传。
        /// </summary>
        bool AssignedSkinAssetPresent { get; }

        /// <summary>
        /// HelloAck.has_assigned_placement：主机角色记录里有本机角色的重新进入位置。
        /// </summary>
        bool HasAssignedPlacement { get; }

        /// <summary>角色记录里的最后离线位置。</summary>
        SuMultiplayer.Players.PlayerPlacement AssignedPosition { get; }

        /// <summary>角色记录里的最后睡觉复活点。</summary>
        SuMultiplayer.Players.PlayerPlacement AssignedSpawnPosition { get; }

        /// <summary>HelloAck.has_assigned_state：角色退出前的飞行/蹲下状态可用。</summary>
        bool HasAssignedState { get; }

        /// <summary>角色退出前是否飞行。</summary>
        bool AssignedIsFlying { get; }

        /// <summary>角色退出前是否蹲着。</summary>
        bool AssignedIsCrouching { get; }

        /// <summary>
        /// 角色创建完成 / 资料变化后**重新握手**（旧实现同样是重新 JoinGame）。
        /// 返回 false = 当前没有可重连的客户端通道（已明确记录，不假装重连成功）。
        /// </summary>
        bool ReconnectClient(string reason);

        /// <summary>
        /// 角色创建完成：**不重连**，在当前仍然存活的客户端连接上重发一次 Hello，
        /// 携带刚确认的资料。主机保留首次 Allow，不会二次审批；随后主机建记录 / 发皮肤资源 / 传地图。
        /// 返回 false = 当前连接已不可用（主机踢出 / 通道已死），必须回 Play 重新加入。
        /// </summary>
        bool SendProfileHello();
    }
}

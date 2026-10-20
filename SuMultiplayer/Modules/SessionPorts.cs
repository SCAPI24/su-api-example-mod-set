using System.Collections.Generic;
using Engine;
using Game;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.Session;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Modules
{
    // 传输通道控制端口（ITransportControlPort）在 Modules/TransferPorts.cs：
    // 它与世界传输模块一样不依赖引擎，所以和模块一起被无设备自检工程直接编译。

    /// <summary>
    /// 房间浏览端口：给 Play 屏用。
    /// UI 只**读快照**、只**发请求**，不碰任何网络实现。
    /// </summary>
    public interface IRoomBrowsePort
    {
        /// <summary>当前发现的房间（主线程读；每次访问返回新快照）。</summary>
        IReadOnlyList<RoomEntry> Rooms { get; }

        /// <summary>本端是否正在主持房间（CR 已按下）。</summary>
        bool IsHostingRoom { get; }

        /// <summary>房间状态文本（给按钮信息弹窗）。</summary>
        string RoomSummary { get; }

        /// <summary>加入流程阶段文本（给 Play 屏的 BusyDialog）。</summary>
        string JoinStageText { get; }

        /// <summary>建房：主机必须在**正在游玩的世界里**调用（worldName 来自当前世界）。</summary>
        bool CreateRoom(string worldName, string gameMode, out string reason);

        /// <summary>关掉房间（停止应答 + 关闭通道）。</summary>
        void CloseRoom();

        /// <summary>加入指定房间：连上才返回 true。</summary>
        bool TryBeginJoin(RoomEntry entry, out string reason);

        /// <summary>
        /// 离开上一个房间/游戏。回到世界列表时调用 —— 旧实现
        /// <c>SuPlayScreen.Enter</c> 里的 <c>client.LeaveGame()</c>：
        /// 进世界列表就意味着"不玩了"，通道要断、加入状态要复位，否则再点一次房间行
        /// 会以为自己还连着，既不会重新下载世界，也不会重新进世界。
        /// </summary>
        void LeaveRoom();

        /// <summary>
        /// Play 屏 Enter 调用：true = 这次进入是"角色创建屏返回"的同一次加入，**不要** LeaveRoom。
        /// 只生效一次（由角色创建屏在返回前置位）。
        /// </summary>
        bool ConsumePreserveJoinOnNextPlayEnter();
    }

    // 下载世界端口（IDownloadedWorldPort / IWorldReadyPort）也在 Modules/TransferPorts.cs：
    // 它们是世界传输模块自己的对外契约，与房间浏览 / 联机角色这些引擎相关端口分开。

    /// <summary>
    /// 联机角色视图（由联机角色模块实现，交给地形通道）。旧实现里"地形兴趣"这一整块
    /// （<c>MaintainRemoteTerrainLocations</c> 及其 helper）住在模组主类里，所以两边共用同一份
    /// 网络化身表与同一个半径算法；本重写版按模块拆开后，这两件事必须由**拥有网络化身表的模块**
    /// 提供，地形模块只按旧规则消费它。
    /// </summary>
    public interface IAvatarTerrainLink
    {
        /// <summary>
        /// 该 PlayerData 是不是联机角色（网络化身）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:481-484
        ///         （旧实现用 <c>m_networkPlayerData.Values</c> 排除网络化身）
        /// </summary>
        bool IsNetworkAvatar(PlayerData playerData);

        /// <summary>
        /// 主机：某个玩家端的联机角色**在世界里的真实位置**（主机侧那个角色实体的
        /// <c>ComponentPlayer.ComponentBody.Position</c>）—— 实时地图上显示客户端坐标用它，
        /// **不按区块中心猜**。
        /// 角色实体还没建好 / 没有 ComponentPlayer/ComponentBody → 返回 false 并写清原因
        /// （调用方必须明说"该玩家端坐标未知"，不许退到区块中心或别的位置）。
        /// </summary>
        bool TryGetAvatarCell(int clientId, out Vector3 position, out string reason);

        /// <summary>
        /// 主机：实时地图的**逐角色槽位**清单（v10）—— 本机槽位 0..N-1（clientId=0）+ 每个联机化身的
        /// (clientId, slot)。唯一枚举口径：本机角色走 <see cref="NetworkAvatarSpawner.TryCollectLocalPlayers"/>
        /// （按 PlayerIndex 排序，下标就是槽位），联机化身走化身表的 <c>Keys</c> / <c>TryGet</c>。
        /// 条目直接填 <see cref="LiveMapPlayer"/> 的 ClientId / Slot / Key / Name / IsHost / 坐标；
        /// 某个角色还没有实体 / ComponentBody → **照样列出来**，但 HasCell=false + CellReason 写清原因
        /// （实时地图必须能说明"这个槽位存在但坐标未知"，绝不拿别的位置顶替）。
        /// <see cref="LiveMapPlayer.StableKey"/> 由调用方（地形通道，拥有身份会话表）填。
        /// 返回写进 <paramref name="target"/> 的条数；没有加载中的 Project 时返回 0 并写明原因。
        /// </summary>
        int CaptureLiveMapPlayers(List<LiveMapPlayer> target, out string reason);

        /// <summary>
        /// 可见距离 → 兴趣半径。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:521-525
        ///         （GetTerrainInterestRadius）：max(1, ceil(visibility / 16))，夹到 1..32。
        /// 客户端上报半径（旧 :487 <c>clientRadius</c>）与主机默认半径（旧 :499 <c>defaultRadius</c>）
        /// 是同一个算法（唯一实现：<see cref="TerrainSyncRange.RadiusForVisibility"/>），避免两处漂移。
        /// </summary>
        int GetTerrainInterestRadius(float visibility);

        /// <summary>
        /// 主机：某个玩家端上报了兴趣半径（InterestUpdate，载荷 (x, z, radius)）。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:441-449
        ///         （HandleTerrainChunkSyncMessage 的 Interest 分支 → m_hostTerrainReportedInterestRadii）
        /// </summary>
        void OnClientInterestRadiusReported(int clientId, int radius);

        /// <summary>
        /// 本机玩家当前的位置/姿态快照（客户端按旧实现的 32Hz 节奏发一份给主机）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:53-92
        ///         （旧实现只取"本机玩家" = <c>PlayersData</c> 里不是网络化身的那一条；
        ///           客户端分支发的是同一份位置/姿态字段）
        /// 拿不到（没进世界 / 没有本机玩家 / 没有 ComponentBody）→ 返回 false 并写清原因；
        /// **绝不猜一个位置**（猜出来的位置会把主机的地形更新位置带到别处）。
        /// </summary>
        bool TryCaptureLocalPlayerPosition(int serverTick, out PlayerPositionSnapshot snapshot,
            out string reason);

        /// <summary>
        /// 客户端：**所有本机角色**的位置快照（Phase 1 多本机角色）。槽位 0 的第一条与旧实现
        /// 相同（客户端编号 0、槽位 0），所以单角色房间的线上负载与旧版一致。
        /// 返回实际写进的条数；某个本机角色的 ComponentBody 还没好就跳过并写清原因。
        /// </summary>
        int CaptureLocalPlayerPositions(int serverTick, List<PlayerPositionEntry> target,
            out string reason);

        /// <summary>
        /// 主机：把"每个本机角色 + 每个联机角色"折成一批快照（旧 <c>positionBatch</c>）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:47-100
        ///         （本机玩家挂 <c>client.ClientID</c>；每个联机角色挂它自己的 clientId）
        /// 返回实际写进 <paramref name="target"/> 的条数；某个角色的 ComponentBody 还没好就
        /// **跳过它并写清原因**（限流），不是拿别的位置顶替。
        /// </summary>
        int CapturePlayerPositionBatch(int serverTick, List<PlayerPositionEntry> target, out string reason);

        /// <summary>
        /// 收到一份远端角色的位置快照，记进它的插值状态：
        ///   · 主机侧：键 = (来源 clientId, 槽位)，该玩家端的本机角色；
        ///   · 客户端侧：键 = 主机名册里的 (clientId, 槽位)。
        /// 找不到对应联机角色 / ComponentBody → **明确写原因并跳过**（限流一行），不伪造位置。
        /// </summary>
        void OnRemotePlayerPositionReceived(PlayerKey key, in PlayerPositionSnapshot snapshot);

        /// <summary>
        /// 客户端：收到**本机自己**的键（clientId = 本机通道编号）→ 计数 positionSkippedOwn 并跳过。
        /// </summary>
        void NotePositionSkippedOwn(PlayerKey key);

        /// <summary>
        /// 主机：这个玩家端是否已经上报过本机槽位（<c>LOCAL_PLAYER_SLOTS</c>）。
        /// false = 旧构建 / 尚未上报 → 位置批与名册只发槽位 0（单角色兼容，绝不让旧客户端
        /// 把主机第二个角色当成主机角色来回拉扯）。
        /// </summary>
        bool PeerAnnouncedLocalSlots(int clientId);

        /// <summary>
        /// 每帧推进远端角色的插值/平滑并写回引擎。
        /// Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:1299
        ///         （客户端分支每帧调用 UpdateRemotePlayerPresentations）
        /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:1054-1160
        ///         （外推 + 死区 + 追赶速度 + Lerp/Slerp，绝不把裸浮点噪声直接平移进引擎）
        /// </summary>
        void UpdateRemotePlayerPresentations(float deltaTime);
    }

    /// <summary>
    /// 挂在传输端口上的"联机角色 ↔ 地形通道"桥（由地形通道实现）。
    ///
    /// 为什么挂在 <c>ITransportPort</c> 上而不是单独注册一个端口：端口注册在组合根
    /// <c>Plug/SuMultiplayerMod.cs</c>（本次改动范围之外），而地形模块的传输端口**已经**被注册进
    /// 模块宿主，所以桥接接口由同一个端口对象实现，联机角色模块在 Attach 时从它取。
    /// 取不到会显式报错（不静默降级）。
    /// </summary>
    public interface IAvatarTerrainBridge
    {
        /// <summary>接上 / 断开联机角色视图（null = 断开：联机角色模块下线时调用）。</summary>
        void BindAvatarTerrainLink(IAvatarTerrainLink link);
    }
}

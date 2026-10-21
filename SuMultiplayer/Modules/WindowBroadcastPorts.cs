using System.Collections.Generic;
using Engine;
using SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 主机侧"玩家端兴趣窗口"视图：一个端 + 它此刻上报的中心区块 / 半径。
    /// 与地形下发 / 编辑校验 / 实体同步用的是**同一份**窗口，没有第二套分区口径。
    /// </summary>
    public readonly struct ClientWindowView
    {
        public readonly int ClientId;
        public readonly int CenterChunkX;
        public readonly int CenterChunkZ;
        public readonly int RadiusChunks;

        public ClientWindowView(int clientId, int centerChunkX, int centerChunkZ,
            int radiusChunks)
        {
            ClientId = clientId;
            CenterChunkX = centerChunkX;
            CenterChunkZ = centerChunkZ;
            RadiusChunks = radiusChunks;
        }

        /// <summary>该世界坐标是否落在这个端的窗口（外扩 chunkMargin 环，1 环 = 1 区块 = 16 m）。</summary>
        public bool Contains(Vector3 position, int chunkMargin)
        {
            int margin = chunkMargin < 0 ? 0 : chunkMargin;
            int chunkX = (int)System.Math.Floor(position.X / 16f);
            int chunkZ = (int)System.Math.Floor(position.Z / 16f);
            int limit = RadiusChunks + margin;
            return System.Math.Abs(chunkX - CenterChunkX) <= limit &&
                System.Math.Abs(chunkZ - CenterChunkZ) <= limit;
        }
    }

    /// <summary>
    /// 主机：把"纯表现事件"按**客户端兴趣窗口**投递。
    ///
    /// 口径只有一处真相：客户端 <c>INTEREST_UPDATE</c> 上报的中心区块 + 半径，切比雪夫方形，
    /// 与地形下发 / 编辑校验 / 实体同步 / 投射物命中完全相同 —— 世界是分区加载的，
    /// 主机加载全部地形，客户端只加载自己周围那片。
    ///
    /// 语义：声音 / 粒子 / 爆炸 / 烧尽这类表现只需要发给"本端加载了这片地形的端"；
    /// 主机自己的表现由引擎在本地照常执行，不经过本接口。
    ///
    /// **绝不回退**：范围内没有任何端（或端还没上报窗口）时返回 0，由调用方计数并写明确日志，
    /// 不允许改回"发给所有人"（那正是这次要消掉的带宽浪费）。
    /// </summary>
    public interface IWindowBroadcastPort
    {
        /// <summary>
        /// 发给兴趣窗口（外扩 <paramref name="chunkMargin"/> 环）包含 <paramref name="position"/>
        /// 的在线玩家端。<paramref name="chunkMargin"/> 用于表现半径换算：1 环 = 1 区块 = 16 m
        /// （例如爆炸要覆盖 96 m 的可见范围 ≈ 6 环）。
        /// </summary>
        /// <param name="candidates">
        /// 有可用兴趣窗口的端数（诊断用：0 表示还没人上报窗口，而不是"没人需要"）。
        /// </param>
        /// <returns>实际入队端数；0 = 没有端在范围内。</returns>
        int BroadcastToPlayersAt(MessageType type, byte[] payload, Vector3 position,
            int chunkMargin, out int candidates);

        /// <summary>
        /// 枚举"此刻有可用兴趣窗口的在线玩家端"（需要**逐端过滤消息内容**时用：
        /// 例如一批烧尽格子要按每个端自己的窗口裁剪，每个端收到的载荷不同）。
        /// 顺序 = 稳定用户槽位顺序，不随连接重排变化。
        /// </summary>
        int CaptureClientWindows(List<ClientWindowView> target);

        /// <summary>
        /// 给单个玩家端发一条已编码消息（<see cref="CaptureClientWindows"/> 枚举后使用）。
        /// false = 这条连接已经不在线 / 已不是玩家端 / 背压拒绝（调用方必须计数，不允许静默）。
        /// </summary>
        bool SendToClient(int clientId, MessageType type, byte[] payload);

        /// <summary>
        /// 主机：投给"兴趣窗口（外扩 <paramref name="chunkMargin"/> 环）包含该玩家端**化身真实位置**"
        /// 的端，并且**该化身自己的端永远包含**（它自己的状态必须到，即使它还没上报窗口）。
        ///
        /// 用于"别人的表现只在他加载的地形范围内才需要"的逐角色事件（如背包 / 手持物镜像）。
        /// <paramref name="avatarCellKnown"/> = false 表示主机还没有这个化身的实体（位置未知）：
        /// 此时只发给它自己的端，观察者一个都不发，调用方必须计数并限流说明 —— 绝不改发全体。
        /// </summary>
        /// <param name="candidates">被评估过窗口的观察者端数（不含化身自己的端）。</param>
        int BroadcastToPlayersAtAvatar(MessageType type, byte[] payload, int avatarClientId,
            int chunkMargin, out int candidates, out bool avatarCellKnown);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Engine;
using SuMultiplayer.Players;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 主机给**一个联机角色**挂的引擎地形更新位置（<c>TerrainUpdater.SetUpdateLocation</c> 的那一条）。
    ///
    /// 字段全部是"主机实际交给引擎的值"，不是猜测：
    ///   · <see cref="Chunk"/> = <see cref="Center"/> 所在区块（跟随角色的 ComponentBody.Position）；
    ///   · <see cref="Radius"/> = 该玩家端**上报**的兴趣半径（没有上报过 = 主机按自己的可见范围算的默认半径）；
    ///   · <see cref="ContentDistance"/> = radius * 16（与 <see cref="TerrainSyncRange.Distances"/> 同一口径）。
    ///
    /// 纯托管（只依赖 Engine 的 Vector2），因此自检可以直接对"locations / allocator 层"断言，
    /// 不需要跑真实游戏。
    /// </summary>
    public struct HostTerrainLocation
    {
        /// <summary>玩家键（clientId + 本机槽位）；多本机角色时每个角色一条。</summary>
        public PlayerKey Key;
        public int ClientId => Key.ClientId;
        public int Slot => Key.Slot;
        public int PlayerIndex;
        public Vector2 Center;
        public ChunkKey Chunk;
        public int Radius;
        public float ContentDistance;
        /// <summary>这条记录**跨区块**的次数（第一次挂不算；只增不减，离开时整条删掉）。</summary>
        public long MoveCount;
    }

    /// <summary>
    /// 主机侧"每个联机角色一条地形更新位置"的唯一账本。
    ///
    /// 为什么要有这一层（实机故障）：主机的引擎地形只围绕**地形更新位置**异步加载，
    /// 而联机角色的更新位置原先直接拿 <c>ComponentBody.Position</c> 调引擎，**没有任何可观察状态**：
    /// 客户端角色不动时，日志里只有一行"已挂位置"，看不出它挂在哪、跟着谁、移没移动。
    /// 于是"主机一直拿不到区块"只能靠猜。这里把"每个 clientId 的位置 / 区块 / 半径 / 内容距离"
    /// 落成显式账本，状态行直接打印，自检也能对同一份逻辑断言。
    ///
    /// **无静态状态**：一个组合根一个实例，随会话（<c>ForgetAll</c>）清空。
    /// 只做几何与账本，**不碰引擎**（引擎调用仍在 <c>NetworkAvatarSpawner.ApplyTerrainUpdateLocations</c>）。
    /// </summary>
    public sealed class HostTerrainLocationTable
    {
        private readonly Dictionary<PlayerKey, HostTerrainLocation> m_locations =
            new Dictionary<PlayerKey, HostTerrainLocation>();
        private long m_moves;

        /// <summary>当前挂着的更新位置条数（= 主机侧联机角色数；角色离开后立刻减少）。</summary>
        public int Count => m_locations.Count;

        /// <summary>累计"位置换到另一个区块"的次数（状态行的 updateLocationMoves）。</summary>
        public long Moves => m_moves;

        /// <summary>当前有更新位置的玩家键（旧调用方按 ClientIds 读 clientId，这里给键）。</summary>
        public IEnumerable<PlayerKey> Keys => m_locations.Keys;

        public bool TryGet(PlayerKey key, out HostTerrainLocation location) =>
            m_locations.TryGetValue(key, out location);

        /// <summary>旧版单角色入口：clientId 的槽位 0。</summary>
        public bool TryGet(int clientId, out HostTerrainLocation location) =>
            TryGet(new PlayerKey(clientId, 0), out location);

        /// <summary>
        /// 记下（并更新）某个联机角色的更新位置。
        /// 返回 true = 这一次调用**改变了**账本（第一次挂 / 换成别的区块）；
        /// 同一区块内的微小移动只刷新 Center（引擎自己按它的 8 格滞回决定要不要重排），不计 move。
        /// </summary>
        public bool Apply(int clientId, int playerIndex, Vector2 center, int radius,
            float contentDistance) =>
            Apply(new PlayerKey(clientId, 0), playerIndex, center, radius, contentDistance);

        public bool Apply(PlayerKey key, int playerIndex, Vector2 center, int radius,
            float contentDistance)
        {
            ChunkKey chunk = ChunkOf(center);
            if (m_locations.TryGetValue(key, out HostTerrainLocation location))
            {
                bool moved = !location.Chunk.Equals(chunk);
                location.PlayerIndex = playerIndex;
                location.Center = center;
                location.Chunk = chunk;
                location.Radius = radius;
                location.ContentDistance = contentDistance;
                if (moved)
                {
                    location.MoveCount++;
                    m_moves++;
                }
                m_locations[key] = location;
                return moved;
            }
            location = new HostTerrainLocation
            {
                Key = key,
                PlayerIndex = playerIndex,
                Center = center,
                Chunk = chunk,
                Radius = radius,
                ContentDistance = contentDistance,
                MoveCount = 0
            };
            m_locations[key] = location;
            return true;
        }

        /// <summary>角色离开：删掉它的账本（引擎那一侧的 RemoveUpdateLocation 由调用方负责）。</summary>
        public bool Remove(PlayerKey key) => m_locations.Remove(key);

        /// <summary>旧版单角色入口：clientId 的槽位 0。</summary>
        public bool Remove(int clientId) => Remove(new PlayerKey(clientId, 0));

        /// <summary>会话结束 / 换端：清空账本（累计移动次数一并归零，不跨会话继承）。</summary>
        public void Clear()
        {
            m_locations.Clear();
            m_moves = 0;
        }

        /// <summary>状态行 / 日志用的快照（避免遍历时被 Tick 改写）。</summary>
        public HostTerrainLocation[] Snapshot()
        {
            var result = new HostTerrainLocation[m_locations.Count];
            m_locations.Values.CopyTo(result, 0);
            return result;
        }

        /// <summary>
        /// 状态行：<c>hostUpdateLocations</c>（每个 clientId 的区块 + 半径 + 内容距离）。
        /// 空表写 <c>none</c>，不写空串（实机一眼看出"主机一个联机角色位置都没挂"）。
        /// </summary>
        public string Describe()
        {
            if (m_locations.Count == 0) return "none";
            var builder = new StringBuilder(64 * m_locations.Count);
            bool first = true;
            foreach (HostTerrainLocation location in Snapshot())
            {
                if (!first) builder.Append(' ');
                first = false;
                // 槽位 0 打印旧的 clientId（状态行/自检与旧版一致）；多本机角色才打印 cid:slot。
                builder.Append(location.Slot == 0 ? location.ClientId.ToString() : location.Key.ToString())
                    .Append(':')
                    .Append("chunk=").Append(location.Chunk)
                    .Append(",index=").Append(location.PlayerIndex)
                    .Append(",r=").Append(location.Radius)
                    .Append(",d=").Append(location.ContentDistance.ToString("0.#",
                        CultureInfo.InvariantCulture))
                    .Append(",moves=").Append(location.MoveCount);
            }
            return builder.ToString();
        }

        // ---------------------------------------------------------------- 几何（与引擎同一判据）

        /// <summary>
        /// 一个位置所在的区块。与引擎 <c>Terrain.ToChunk(int)</c> 的 <c>&gt;&gt; 4</c> 等价：
        /// 对负坐标必须向下取整，不能截断（-1 格属于区块 -1，不是 0）。
        /// </summary>
        public static ChunkKey ChunkOf(Vector2 position) => new ChunkKey(
            (int)Math.Floor(position.X / 16f), (int)Math.Floor(position.Y / 16f));

        /// <summary>两个区块相距多少个区块（切比雪夫距离：8 表示"横向或纵向隔了 8 块"）。</summary>
        public static int ChunkDistance(ChunkKey a, ChunkKey b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));

        /// <summary>
        /// 这些更新位置会让引擎**分配 / 保留**哪些区块。
        ///
        /// 判据与引擎 <c>TerrainUpdater.AllocateAndFreeChunks</c> 完全一致：
        /// 按 ContentDistance 圈出矩形范围，再逐块取**区块中心** <c>((x+0.5)*16, (z+0.5)*16)</c>，
        /// 距离 ≤ ContentDistance 才在范围内（所以是圆，不是方块；四角不在）。
        /// 这是自检用来断言"主机加载集合覆盖客户端窗口"的 allocator 层。
        /// </summary>
        public static void CollectCoveredChunks(IEnumerable<HostTerrainLocation> locations,
            ICollection<ChunkKey> target)
        {
            if (locations == null || target == null) return;
            foreach (HostTerrainLocation location in locations)
            {
                if (location.ContentDistance <= 0f) continue;
                float distance = location.ContentDistance;
                float distanceSquared = distance * distance;
                ChunkKey min = ChunkOf(location.Center - new Vector2(distance));
                ChunkKey max = ChunkOf(location.Center + new Vector2(distance));
                for (int x = min.X; x <= max.X; x++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        var center = new Vector2((x + 0.5f) * 16f, (z + 0.5f) * 16f);
                        if (Vector2.DistanceSquared(center, location.Center) > distanceSquared) continue;
                        target.Add(new ChunkKey(x, z));
                    }
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 逐用户兴趣槽位表（主机侧）。
    ///
    /// 位序规则（用户 2026-10 明确）：mask 的每一位必须是**稳定用户键**（userid 派生，见
    /// <c>SuMultiplayer.Terrain.PlayerSessionKeys</c>），**绝不能用 clientId** —— clientId 只是
    /// 这一次连接的传输句柄，重连即重分配：拿它当位序，上一轮的数据会投给下一个人；而且每个端
    /// 自己的角色在本地是从 0 开始编号的（见 <c>PlayerKey</c> / <c>ITransportPort.LocalClientId</c>），
    /// 按传输编号对齐位序必然错位。
    ///
    /// 槽位在一次主机会话内**不复用**：用户断开后它的 bit 仍代表它本人，重连回来仍是同一位
    /// （这样待发队列里没发完的位可以继续投给同一个人，而不是投给别人）。槽位用尽（64）时
    /// 明确失败并由调用方计数 —— 不做"轮流复用"这类会误投的兜底。
    /// </summary>
    public sealed class EntityInterestSlots
    {
        /// <summary>mask 是 64 位 → 一次会话最多 64 个不同用户（当前引擎上限 4 人，余量充足）。</summary>
        public const int MaxUsers = 64;

        private readonly Dictionary<string, int> m_slots =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> m_keys = new List<string>(MaxUsers);

        /// <summary>已有槽位数量（= 已分配过的用户数）。</summary>
        public int Count => m_keys.Count;

        /// <summary>该用户当前的槽位；没有返回 -1。</summary>
        public int IndexOf(string userKey) =>
            userKey != null && m_slots.TryGetValue(userKey, out int slot) ? slot : -1;

        /// <summary>取槽位（没有就分配）。返回 -1 = 已经用满 64 个用户，调用方必须计数并放弃投递。</summary>
        public int GetOrAlloc(string userKey)
        {
            if (string.IsNullOrEmpty(userKey)) return -1;
            if (m_slots.TryGetValue(userKey, out int slot)) return slot;
            if (m_keys.Count >= MaxUsers) return -1;
            slot = m_keys.Count;
            m_keys.Add(userKey);
            m_slots[userKey] = slot;
            return slot;
        }

        /// <summary>槽位对应的用户键（越界返回 null）。</summary>
        public string KeyAt(int slot) =>
            slot >= 0 && slot < m_keys.Count ? m_keys[slot] : null;

        /// <summary>位掩码。</summary>
        public static ulong Bit(int slot) => 1UL << slot;

        /// <summary>日志用：<c>1|3</c> 这样的槽位列表（0 = <c>none</c>）。</summary>
        public static string Describe(ulong mask)
        {
            if (mask == 0UL) return "none";
            var text = new System.Text.StringBuilder(24);
            for (int i = 0; i < MaxUsers; i++)
            {
                if ((mask & (1UL << i)) == 0UL) continue;
                if (text.Length > 0) text.Append('|');
                text.Append(i.ToString());
            }
            return text.ToString();
        }

        /// <summary>清空（主机侧退出联机 / 换角色时用）。</summary>
        public void Reset()
        {
            m_slots.Clear();
            m_keys.Clear();
        }
    }

    /// <summary>
    /// 窗口差集：客户端兴趣窗口（区块中心 + 半径，切比雪夫方形，与
    /// <c>ProjectileHitViewPolicy.IsCellInInterestWindow</c> / <c>TerrainSyncRange</c> 同一口径）
    /// 变化时，算出"新进入"与"已离开"的区块键。
    ///
    /// 只在窗口真的变化时调用（玩家跨区块 / 改可见范围），扫的是新旧窗口的并集包围盒；
    /// 中心瞬移（传送）导致包围盒过大时退化成"整旧窗口离开 + 整新窗口进入"两次矩形扫描，
    /// 仍然是 O(窗口面积) 而不是 O(包围盒)。
    /// </summary>
    public static class EntityInterestWindowDiff
    {
        /// <summary>区块键（与主机实体索引同一个编码）。</summary>
        public static long Key(int chunkX, int chunkZ) => ((long)chunkX << 32) ^ (uint)chunkZ;

        /// <summary>键 → X。</summary>
        public static int KeyX(long key) => (int)(key >> 32);

        /// <summary>键 → Z。</summary>
        public static int KeyZ(long key) => (int)(key & 0xFFFFFFFFL);

        /// <summary>坐标 → 区块下标（引擎口径 = 算术右移，负数向下取整）。</summary>
        public static int ChunkOf(float coordinate) => (int)Math.Floor(coordinate / 16f);

        /// <summary>这个区块在窗口内吗（切比雪夫方形）。</summary>
        public static bool InWindow(int chunkX, int chunkZ, int centerX, int centerZ, int radius) =>
            Math.Abs(chunkX - centerX) <= radius && Math.Abs(chunkZ - centerZ) <= radius;

        /// <summary>
        /// 算差集。<paramref name="hadOld"/> = false 表示第一次拿到窗口（整窗都算"新进入"）。
        /// 结果写入调用方的两个列表（先 Clear）。绝不返回 null。
        /// </summary>
        public static void Compute(bool hadOld, int oldCenterX, int oldCenterZ, int oldRadius,
            int newCenterX, int newCenterZ, int newRadius,
            List<long> entered, List<long> left)
        {
            if (entered == null) throw new ArgumentNullException(nameof(entered));
            if (left == null) throw new ArgumentNullException(nameof(left));
            entered.Clear();
            left.Clear();
            if (newRadius < 0) newRadius = 0;
            if (oldRadius < 0) oldRadius = 0;
            if (!hadOld)
            {
                AddRectangle(newCenterX - newRadius, newCenterZ - newRadius,
                    newCenterX + newRadius, newCenterZ + newRadius, entered);
                return;
            }
            int minX = Math.Min(oldCenterX - oldRadius, newCenterX - newRadius);
            int maxX = Math.Max(oldCenterX + oldRadius, newCenterX + newRadius);
            int minZ = Math.Min(oldCenterZ - oldRadius, newCenterZ - newRadius);
            int maxZ = Math.Max(oldCenterZ + oldRadius, newCenterZ + newRadius);
            long boxArea = (long)(maxX - minX + 1) * (maxZ - minZ + 1);
            long oldArea = (long)(2 * oldRadius + 1) * (2 * oldRadius + 1);
            long newArea = (long)(2 * newRadius + 1) * (2 * newRadius + 1);
            if (boxArea > 4L * (oldArea + newArea))
            {
                // 中心瞬移：整旧离开 + 整新进入（仍然只扫窗口面积）。
                AddRectangle(oldCenterX - oldRadius, oldCenterZ - oldRadius,
                    oldCenterX + oldRadius, oldCenterZ + oldRadius, left);
                AddRectangle(newCenterX - newRadius, newCenterZ - newRadius,
                    newCenterX + newRadius, newCenterZ + newRadius, entered);
                return;
            }
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    bool inOld = InWindow(x, z, oldCenterX, oldCenterZ, oldRadius);
                    bool inNew = InWindow(x, z, newCenterX, newCenterZ, newRadius);
                    if (inNew && !inOld) entered.Add(Key(x, z));
                    else if (inOld && !inNew) left.Add(Key(x, z));
                }
            }
        }

        private static void AddRectangle(int minX, int minZ, int maxX, int maxZ, List<long> target)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++) target.Add(Key(x, z));
            }
        }
    }
}

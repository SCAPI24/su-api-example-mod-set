using System;
using System.Collections.Generic;
using System.Globalization;

namespace SuMultiplayer.Furniture
{
    /// <summary>
    /// 源格世界视图（客户端锤子扫描 / 主机请求校验共用；纯逻辑，不引用 Game）。
    ///
    /// 生产实现：Engine/FurnitureReplicaEngine.cs:TerrainSourceWorld（SubsystemTerrain）；
    /// 自检实现：P:\tmp\tsverify:FtSourceWorld。
    ///
    /// 两侧共用同一份遍历 / 校验逻辑，规则完全按引擎
    /// Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:ScanDesign
    ///   · 只从 start 沿 **6 邻居**扩展；
    ///   · 只有 IsValueAllowed 的格子算源格（Air 等不在白名单 → 不进入源格集合）；
    ///   · 遇到 IsValueDisallowed（顶层流体）整次扫描失败；
    ///   · 任一轴跨度 >= 16 整次失败（"Furniture design is too large"）。
    /// </summary>
    public interface IFurnitureSourceWorld
    {
        /// <summary>坐标在引擎世界里可寻址（Source: Terrain.IsCellValid，当前只校验 y ∈ 0..255）。</summary>
        bool IsCellInRange(int x, int y, int z);

        /// <summary>
        /// 读一格；false = 区块未加载 / 不可读。扫描按引擎语义把不可读当空气
        /// （Terrain.GetCellValueFast 对未分配区块返回 0），主机校验则明确拒绝（source-missing）。
        /// </summary>
        bool TryReadCell(int x, int y, int z, out int value);

        /// <summary>contents 是家具方块（Source: FurnitureBlock.Index = 227）。</summary>
        bool IsFurnitureContents(int contents);

        /// <summary>引擎 ScanDesign 允许的 contents（Source: IsValueAllowed 白名单）。</summary>
        bool IsAllowedContents(int contents);

        /// <summary>引擎 ScanDesign 明确拒绝的格子（Source: IsValueDisallowed：顶层流体）。</summary>
        bool IsDisallowedCell(int contents, int data);

        /// <summary>家具格 data → 设计索引（Source: FurnitureBlock.GetDesignIndex）。</summary>
        int GetFurnitureDesignIndex(int data);
    }

    /// <summary>
    /// 引擎方块值规则（contents / data / 白名单）的纯逻辑形态：
    /// 客户端扫描、主机校验、自检都调这一份，避免三处各写一遍。
    /// </summary>
    public static class FurnitureDesignRules
    {
        public const int ContentsMask = 0x3FF;
        public const int DataShift = 14;

        // Source: Survivalcraft/Game/Terrain.cs:ExtractContents
        public static int ContentsOf(int value) => value & ContentsMask;

        // Source: Survivalcraft/Game/Terrain.cs:ExtractData
        public static int DataOf(int value) => (int)((uint)value >> DataShift);

        // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:IsValueAllowed
        public static bool IsAllowedContents(int contents)
        {
            switch (contents)
            {
                case 21:
                case 3:
                case 67:
                case 7:
                case 72:
                case 5:
                case 26:
                case 4:
                case 68:
                case 73:
                case 150:
                case 71:
                case 126:
                case 47:
                case 46:
                case 15:
                case 208:
                case 31:
                case 17:
                case 18:
                case 92:
                    return true;
                default:
                    return false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:IsValueDisallowed
        // Source: Survivalcraft/Game/FluidBlock.cs:GetLevel / GetIsTop
        public static bool IsDisallowedCell(int contents, int data) =>
            (contents == 18 || contents == 92) && (data & 0xF) != 0 && (data & 0x10) != 0;
    }

    /// <summary>源格坐标（纯值；避免依赖 Engine.Point3）。</summary>
    internal readonly struct FurnitureSourceCell : IEquatable<FurnitureSourceCell>
    {
        public FurnitureSourceCell(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public bool Equals(FurnitureSourceCell other) =>
            X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) =>
            obj is FurnitureSourceCell other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                return hash;
            }
        }

        public override string ToString() =>
            X.ToString(CultureInfo.InvariantCulture) + "," +
            Y.ToString(CultureInfo.InvariantCulture) + "," +
            Z.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 一次源格扫描的结果：世界坐标 + **原始**格子值（灯值未剥离，与引擎 ScanDesign 的
    /// valuesDictionary 同口径）。坐标不重复，顺序 = flood fill 的访问顺序。
    /// 调用方按协议对值做 <see cref="FurnitureDesignHash.StripLight"/>。
    /// </summary>
    public sealed class FurnitureSourceScan
    {
        private readonly List<int> m_x = new List<int>(8);
        private readonly List<int> m_y = new List<int>(8);
        private readonly List<int> m_z = new List<int>(8);
        private readonly List<int> m_values = new List<int>(8);

        public IReadOnlyList<int> X => m_x;
        public IReadOnlyList<int> Y => m_y;
        public IReadOnlyList<int> Z => m_z;
        public IReadOnlyList<int> Values => m_values;
        public int Count => m_x.Count;

        public int MinX { get; private set; }
        public int MinY { get; private set; }
        public int MinZ { get; private set; }
        public int MaxX { get; private set; }
        public int MaxY { get; private set; }
        public int MaxZ { get; private set; }

        /// <summary>start 是已放置的家具方块（编辑现有设计，源格只有这一格）。</summary>
        public bool StartIsFurniture { get; private set; }

        /// <summary>start 家具格的设计索引；-1 = 不是家具。</summary>
        public int BaseDesignIndex { get; private set; } = -1;

        public void Clear()
        {
            m_x.Clear();
            m_y.Clear();
            m_z.Clear();
            m_values.Clear();
            StartIsFurniture = false;
            BaseDesignIndex = -1;
            MinX = MaxX = 0;
            MinY = MaxY = 0;
            MinZ = MaxZ = 0;
        }

        internal void Add(int x, int y, int z, int value)
        {
            if (m_x.Count == 0)
            {
                MinX = MaxX = x;
                MinY = MaxY = y;
                MinZ = MaxZ = z;
            }
            else
            {
                if (x < MinX) MinX = x;
                else if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y;
                else if (y > MaxY) MaxY = y;
                if (z < MinZ) MinZ = z;
                else if (z > MaxZ) MaxZ = z;
            }
            m_x.Add(x);
            m_y.Add(y);
            m_z.Add(z);
            m_values.Add(value);
        }

        internal void MarkFurnitureStart(int designIndex)
        {
            StartIsFurniture = true;
            BaseDesignIndex = designIndex;
        }
    }

    /// <summary>
    /// 源格扫描（复刻引擎 ScanDesign 的 flood fill）。客户端锤子只把这里扫出来的
    /// **世界格**发给主机；主机校验器用同一套邻接规则，所以合法扫描结果必然被接受。
    /// </summary>
    public static class FurnitureSourceScanner
    {
        /// <summary>Source: ScanDesign：任一轴 |max-min| >= 16 报 "Furniture design is too large"。</summary>
        public const int MaxSpan = 16;

        /// <summary>上限 = 16³（引擎分辨率上限）；正常扫描受 MaxSpan 限制更严，到不了这里。</summary>
        public const int MaxCells = 4096;

        public static bool TryScan(IFurnitureSourceWorld world, int startX, int startY,
            int startZ, FurnitureSourceScan result, out string reason)
        {
            reason = null;
            if (world == null)
            {
                reason = "world-null";
                return false;
            }
            if (result == null)
            {
                reason = "result-null";
                return false;
            }
            result.Clear();
            if (!world.IsCellInRange(startX, startY, startZ))
            {
                reason = "start-out-of-range";
                return false;
            }
            if (!world.TryReadCell(startX, startY, startZ, out int startValue))
            {
                reason = "start-missing";
                return false;
            }
            int startContents = FurnitureDesignRules.ContentsOf(startValue);
            if (world.IsFurnitureContents(startContents))
            {
                result.MarkFurnitureStart(world.GetFurnitureDesignIndex(
                    FurnitureDesignRules.DataOf(startValue)));
                result.Add(startX, startY, startZ, startValue);
                return true;
            }

            var pending = new Stack<FurnitureSourceCell>();
            var visited = new HashSet<FurnitureSourceCell>();
            pending.Push(new FurnitureSourceCell(startX, startY, startZ));
            while (pending.Count > 0)
            {
                FurnitureSourceCell cell = pending.Pop();
                if (!visited.Add(cell)) continue;
                if (!world.TryReadCell(cell.X, cell.Y, cell.Z, out int value))
                {
                    // Source: Terrain.GetCellValueFast —— 未分配区块读作 0（空气），
                    // 既不在白名单也不被 disallow，等价于"跳过"，扫描不会跨过去。
                    continue;
                }
                int contents = FurnitureDesignRules.ContentsOf(value);
                int data = FurnitureDesignRules.DataOf(value);
                if (world.IsDisallowedCell(contents, data))
                {
                    reason = "disallowed";
                    return false;
                }
                if (!world.IsAllowedContents(contents)) continue;
                if (result.Count >= MaxCells)
                {
                    reason = "too-many";
                    return false;
                }
                result.Add(cell.X, cell.Y, cell.Z, value);
                if (result.MaxX - result.MinX >= MaxSpan ||
                    result.MaxY - result.MinY >= MaxSpan ||
                    result.MaxZ - result.MinZ >= MaxSpan)
                {
                    reason = "too-large";
                    return false;
                }
                pending.Push(new FurnitureSourceCell(cell.X - 1, cell.Y, cell.Z));
                pending.Push(new FurnitureSourceCell(cell.X + 1, cell.Y, cell.Z));
                pending.Push(new FurnitureSourceCell(cell.X, cell.Y - 1, cell.Z));
                pending.Push(new FurnitureSourceCell(cell.X, cell.Y + 1, cell.Z));
                pending.Push(new FurnitureSourceCell(cell.X, cell.Y, cell.Z - 1));
                pending.Push(new FurnitureSourceCell(cell.X, cell.Y, cell.Z + 1));
            }
            if (result.Count == 0)
            {
                reason = "no-source";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 主机侧源格集合校验（权威）。逐项给出**显式子原因**（日志 / 计数的 reason code）：
    ///   sources-empty · sources-length · sources-too-many · edit-source-count
    ///   source-out-of-range · source-too-far · source-duplicate · source-light-mismatch
    ///   source-missing · stale-source · source-block · source-disallowed
    ///   furniture-source-without-base · source-design-mismatch · edit-source-not-furniture
    ///   start-not-in-sources · source-diagonal · sources-not-connected
    ///
    /// 连通规则与引擎 ScanDesign 完全一致：只沿 6 邻居、且只走请求里声明的格子。
    /// 旧实现把"邻接但不在请求集合里的格子"也算进 reachable，于是**任何**请求
    /// （连单格编辑）都必然被判 sources-not-connected；这里的 flood fill 先判
    /// 成员再计入 reachable，因此 reachable == 集合大小 ⇔ 集合 6 连通。
    /// 对角接触（26 连通但非 6 连通）给独立的 source-diagonal，便于定位客户端形状问题。
    /// </summary>
    public static class FurnitureSourceSetValidator
    {
        public const int MaxSpan = FurnitureSourceScanner.MaxSpan;

        public static bool TryValidate(IReadOnlyList<int> sourceX, IReadOnlyList<int> sourceY,
            IReadOnlyList<int> sourceZ, IReadOnlyList<int> sourceValues, int startX,
            int startY, int startZ, int baseDesignIndex, IFurnitureSourceWorld world,
            out string reason)
        {
            reason = null;
            if (world == null)
            {
                reason = "world-null";
                return false;
            }
            if (sourceX == null || sourceY == null || sourceZ == null ||
                sourceValues == null || sourceX.Count != sourceY.Count ||
                sourceX.Count != sourceZ.Count || sourceX.Count != sourceValues.Count)
            {
                reason = "sources-length";
                return false;
            }
            int count = sourceX.Count;
            if (count <= 0)
            {
                reason = "sources-empty";
                return false;
            }
            if (count > FurnitureSourceScanner.MaxCells)
            {
                reason = "sources-too-many:" + count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (baseDesignIndex >= 0 && count != 1)
            {
                // 编辑现有设计时，引擎 ScanDesign 的源格只有那一个家具格；
                // 多余源格会让"编辑"变成任意销毁附近方块的后门。
                reason = "edit-source-count:" + count.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            var cells = new HashSet<FurnitureSourceCell>();
            bool startFound = false;
            bool furnitureSource = false;
            for (int i = 0; i < count; i++)
            {
                int x = sourceX[i];
                int y = sourceY[i];
                int z = sourceZ[i];
                if (x == startX && y == startY && z == startZ) startFound = true;
                if (!world.IsCellInRange(x, y, z))
                {
                    reason = "source-out-of-range:" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (Math.Abs(x - startX) >= MaxSpan || Math.Abs(y - startY) >= MaxSpan ||
                    Math.Abs(z - startZ) >= MaxSpan)
                {
                    reason = "source-too-far:" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (!cells.Add(new FurnitureSourceCell(x, y, z)))
                {
                    reason = "source-duplicate:" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                int declared = sourceValues[i];
                if (FurnitureDesignHash.StripLight(declared) != declared)
                {
                    // 协议规定源格值是**去灯值**（contents + data）；带灯值说明客户端
                    // 没有按同一口径规范化，明确拒绝而不是悄悄改写。
                    reason = "source-light-mismatch:" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (!world.TryReadCell(x, y, z, out int current))
                {
                    reason = "source-missing:" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (FurnitureDesignHash.StripLight(current) != declared)
                {
                    reason = "stale-source:" + i.ToString(CultureInfo.InvariantCulture) +
                        " expected=" + declared.ToString(CultureInfo.InvariantCulture) +
                        " actual=" + current.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                int contents = FurnitureDesignRules.ContentsOf(declared);
                int data = FurnitureDesignRules.DataOf(declared);
                bool furniture = world.IsFurnitureContents(contents);
                if (world.IsDisallowedCell(contents, data))
                {
                    reason = "source-disallowed:" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (!furniture && !world.IsAllowedContents(contents))
                {
                    reason = "source-block:" + contents.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (furniture)
                {
                    furnitureSource = true;
                    int designIndex = world.GetFurnitureDesignIndex(data);
                    if (baseDesignIndex < 0)
                    {
                        reason = "furniture-source-without-base";
                        return false;
                    }
                    if (designIndex != baseDesignIndex)
                    {
                        reason = "source-design-mismatch:" +
                            designIndex.ToString(CultureInfo.InvariantCulture) + "!=" +
                            baseDesignIndex.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }
            }
            if (!startFound)
            {
                reason = "start-not-in-sources";
                return false;
            }
            if (baseDesignIndex >= 0 && !furnitureSource)
            {
                // 编辑的源格必须是"正在编辑的那一件家具"（引擎 ScanDesign 的编辑分支）。
                reason = "edit-source-not-furniture";
                return false;
            }

            int reachable = CountReachable(cells, startX, startY, startZ, neighbours26: false);
            if (reachable != cells.Count)
            {
                int chebyshev = CountReachable(cells, startX, startY, startZ,
                    neighbours26: true);
                reason = (chebyshev == cells.Count ? "source-diagonal:" :
                    "sources-not-connected:") + reachable.ToString(CultureInfo.InvariantCulture) +
                    "!=" + cells.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }

        private static int CountReachable(HashSet<FurnitureSourceCell> cells, int startX,
            int startY, int startZ, bool neighbours26)
        {
            var reachable = new HashSet<FurnitureSourceCell>();
            var pending = new Stack<FurnitureSourceCell>();
            pending.Push(new FurnitureSourceCell(startX, startY, startZ));
            while (pending.Count > 0)
            {
                FurnitureSourceCell cell = pending.Pop();
                if (!cells.Contains(cell) || !reachable.Add(cell)) continue;
                if (neighbours26)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                if (dx == 0 && dy == 0 && dz == 0) continue;
                                pending.Push(new FurnitureSourceCell(cell.X + dx, cell.Y + dy,
                                    cell.Z + dz));
                            }
                        }
                    }
                    continue;
                }
                pending.Push(new FurnitureSourceCell(cell.X - 1, cell.Y, cell.Z));
                pending.Push(new FurnitureSourceCell(cell.X + 1, cell.Y, cell.Z));
                pending.Push(new FurnitureSourceCell(cell.X, cell.Y - 1, cell.Z));
                pending.Push(new FurnitureSourceCell(cell.X, cell.Y + 1, cell.Z));
                pending.Push(new FurnitureSourceCell(cell.X, cell.Y, cell.Z - 1));
                pending.Push(new FurnitureSourceCell(cell.X, cell.Y, cell.Z + 1));
            }
            return reachable.Count;
        }
    }

    /// <summary>
    /// 主机落地策略（与引擎 ScanDesign 的 GameMode 判断同口径；纯逻辑，可自检）。
    /// </summary>
    public static class FurnitureBuildPolicy
    {
        /// <summary>
        /// Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:ScanDesign ——
        /// GameMode != Creative 才销毁源方块；创造模式不销毁（设计 / 家具物品照常生成）。
        /// </summary>
        public static bool DestroysSourceCells(bool creativeMode) => !creativeMode;
    }
}

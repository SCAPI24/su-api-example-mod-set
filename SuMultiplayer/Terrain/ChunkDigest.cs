using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SuMultiplayer.Terrain
{
    /// <summary>按引擎 TerrainChunk.Cells 连续布局读取一个格子的原始值。</summary>
    public delegate int ChunkPackedValueSource(int index);

    /// <summary>chunk 坐标（16×16×256 的一块）。</summary>
    public readonly struct ChunkKey : IEquatable<ChunkKey>
    {
        public ChunkKey(int x, int z) { X = x; Z = z; }
        public int X { get; }
        public int Z { get; }
        public bool Equals(ChunkKey other) => X == other.X && Z == other.Z;
        public override bool Equals(object obj) => obj is ChunkKey other && Equals(other);
        public override int GetHashCode() => (X * 397) ^ Z;
        public override string ToString() => "(" + X + "," + Z + ")";
    }

    public static class ChunkDigestFootprint
    {
        public static bool Contains(ChunkKey center, ChunkKey key)
        {
            int dx = Math.Abs(key.X - center.X);
            int dz = Math.Abs(key.Z - center.Z);
            return (dx == 0 && dz == 0) || (dx == 1 && dz == 0) ||
                (dx == 0 && dz == 1);
        }
    }

    /// <summary>
    /// 读一格内容。**contents 与 data 分开传**：光照不在 API 里出现，从源头保证它进不了摘要。
    /// 引擎侧实现：contents = Terrain.ExtractContents(value)，data = Terrain.ExtractData(value)。
    /// </summary>
    public delegate void ChunkCellSource(int x, int y, int z, out int contents, out int data);

    /// <summary>
    /// 一个 chunk 上「主机侧哪些类别被真实改过」的标记（**按客户端**记录，见 TerrainSyncHost.m_pendingKinds）。
    ///
    /// 语义是「这块有该类改动，该客户端需要补」，**不是**「这块是这类方块」。
    /// 见 doc/leaves-layer-resync-plan.md 第 3 章。
    /// </summary>
    [Flags]
    public enum ChunkMutationKind : byte
    {
        None = 0,
        /// <summary>天气/季节的结冰、积雪（Contents 61/62）。行为不变：仍走顶层摘要修复。</summary>
        Season = 1 << 0,
        /// <summary>
        /// 季节驱动的树叶季相变化：落叶树树叶的 data 低 6 位（Season/TimeOfSeason/IsShaken）变化，
        /// 或 <c>CreateFallenLeaves</c> 新生成了落叶方块（Contents 261）。
        /// 只在**真实写入 chunk 之后**才打这个标记（离队即标记是不允许的：队列项可能不落地）。
        /// </summary>
        Leaves = 1 << 1,
        /// <summary>玩家或其他非季节地形内容发生变化；与 Season 同块出现时按 HasContentChange 处理。</summary>
        Content = 1 << 2
    }

    /// <summary>
    /// 树叶段补丁认的「树叶类」Contents：四种落叶树树叶 + 落叶方块。
    ///
    /// 为什么把 261（FallenLeaves）也算进来：<c>SubsystemDeciduousLeavesBlockBehavior.CreateFallenLeaves</c>
    /// 会在秋→冬时把落叶方块放进树冠下方，那是**同一季节事件的一部分**；不带上它，
    /// 客户端落地树叶补丁后整块仍不一致（永远收敛不了）。
    /// 常绿树（14 Spruce / 225 TallSpruce）不是 <c>DeciduousLeavesBlock</c>，不参与季节变化。
    /// </summary>
    public static class LeavesPatchContents
    {
        public const int Oak = 12;
        public const int Birch = 13;
        public const int Mimosa = 256;
        public const int Poplar = 263;
        public const int FallenLeaves = 261;

        /// <summary>contents → 是否属于树叶段补丁。上限按引擎 BlockIndex 上限（1024）裁。</summary>
        public static bool IsLeavesPatch(int contents)
        {
            switch (contents)
            {
                case Oak:
                case Birch:
                case Mimosa:
                case Poplar:
                case FallenLeaves:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 可选的**轻量写格**通道：只写引擎 <c>TerrainChunk.Cells</c> 数组 + 一次邻域降级，
    /// 不派发电路拓扑 / 方块行为 / 副本事件。
    ///
    /// 专供树叶段补丁：它只改方块 data（不影响电路拓扑），而 <see cref="ITerrainResyncDiagnosticWorld.ApplyResyncCells"/>
    /// 的每格 3 类事件派发正是客户端单帧 250~295ms 尖峰的来源。
    /// 不实现这个接口时调用方退回 <see cref="ITerrainWorld.ApplyCells"/>（功能不变，只是慢）。
    /// </summary>
    public interface IRawCellTerrainWriter
    {
        /// <summary>只写数组。返回真正发生变化的格数。</summary>
        int SetCellsRaw(ChunkKey key, IReadOnlyList<CellEdit> edits);
    }

    /// <summary>
    /// 一个 chunk 的内容摘要（32 bit）。
    ///
    /// **只在加载时计算一次**（用户定）：块卸载时只是把摘要丢掉，不做任何维护；
    /// 加载之后的改动不碰摘要 —— 那些差异走数据消息，由传输层保证送达。
    /// 主机侧需要自己的摘要时**按需现算**，不维护常驻表。
    ///
    /// 算法（HSH03，每格混入一次）：
    ///   sliceHash[s] = FNV-1a over 该切片 4096 格 of (cellValue &amp; 0xFFFFC3FF)
    ///   chunkHash    = XOR over 16 个 sliceHash
    /// 为什么要切片：不一致时只回传**不同的切片**（4096 格），而不是整块 65536 格。
    ///
    /// ⚠️ **算法变更历史（两端必须同版本，否则永久失配）**：
    ///   · HSH02：contents 与 data **分开**混入（每格 2 次乘），位置经 CellHash 参与
    ///   · HSH03：整个 cellValue 剥掉光照位后**一次**混入（每格 1 次乘），约快 2 倍
    /// 版本一致性由 <c>MultiplayerModCompatibility.BuildFingerprint</c>（程序集 MVID）保证：
    /// 不同构建之间 <c>IsCompatible</c> 直接拒绝连接，不会静默失配。
    /// </summary>
    public sealed class ChunkDigest
    {
        public const int SizeXZ = 16;
        public const int Height = 256;
        public const int SliceCount = 16;
        public const int SliceHeight = 16;
        public const int CellsPerSlice = SizeXZ * SizeXZ * SliceHeight;

        private readonly uint[] m_slices = new uint[SliceCount];
        private uint m_shaftHash;
        private bool m_computed;
        private uint m_topHeightHash;
        private uint m_topBlockHash;
        private bool m_topLayerComputed;

        /// <summary>未计算时为 false。界面/协议里 <see cref="Value"/> == 0 也代表「未知」。</summary>
        public bool HasValue => m_computed;

        /// <summary>顶层摘要是否已算（见 <see cref="ComputeTopLayer"/>）。</summary>
        public bool HasTopLayer => m_topLayerComputed;

        /// <summary>256 列 topHeight 的摘要。未算时为 0（= 未知）。</summary>
        public uint TopHeightHash => m_topLayerComputed ? m_topHeightHash : 0u;

        /// <summary>256 列顶格 (contents,data) 的摘要。未算时为 0（= 未知）。</summary>
        public uint TopBlockHash => m_topLayerComputed ? m_topBlockHash : 0u;

        /// <summary>
        /// 顶层是否一致（<b>必须两个 hash 都比</b>）。
        /// 0 = 未知（任一未算）；true/false = 比对结果。
        /// </summary>
        public bool TopLayerMatches(in ChunkDigest other) =>
            m_topLayerComputed && other != null && other.m_topLayerComputed &&
            m_topHeightHash == other.m_topHeightHash && m_topBlockHash == other.m_topBlockHash;

        /// <summary>是否把 shafts 计入摘要。**两端必须一致**，否则永远不匹配。默认关闭。</summary>
        public bool IncludeShafts { get; set; }

        public uint Value
        {
            get
            {
                if (!m_computed) return 0u;                       // 0 = 未计算 / 未知（保留值）
                uint hash = IncludeShafts ? m_shaftHash : 0u;
                for (int i = 0; i < SliceCount; i++) hash ^= m_slices[i];
                return hash;
            }
        }

        public uint SliceValue(int slice) => (uint)slice < SliceCount ? m_slices[slice] : 0u;

        public uint ShaftValue => IncludeShafts ? m_shaftHash : 0u;

        public static bool IsValidCell(int x, int y, int z) =>
            (uint)x < SizeXZ && (uint)y < Height && (uint)z < SizeXZ;

        public static int SliceOf(int y) => y >> 4;

        public static uint ShaftHash(int x, int z, int shaft)
        {
            ulong h = 0xC2B2AE3D27D4EB4FUL;
            h ^= (ulong)(uint)x * 0xBF58476D1CE4E5B9UL;
            h ^= (ulong)(uint)z * 0x94D049BB133111EBUL;
            h ^= (ulong)(uint)shaft * 0xA0761D6478BD642FUL;
            h ^= h >> 31; h *= 0x9E3779B97F4A7C15UL; h ^= h >> 29;
            uint mixed = (uint)(h ^ (h >> 32));
            return mixed == 0 ? 0x85EBCA6Bu : mixed;
        }

        /// <summary>
        /// **唯一的内容摘要入口**：按 <c>TerrainChunk.Cells</c> 的连续布局读取整块并计算。
        ///
        /// 数组布局（<c>TerrainChunk.CalculateCellIndex</c>）：<c>index = y + x * 256 + z * 4096</c>。
        /// 摘要只取 <c>contents</c>（bit 0~9）与 <c>data</c>（bit 14~31），**剥掉光照位（bit 10~13）** ——
        /// 引擎自己判定"格是否改变"时同样先 <c>ReplaceLight(value, 0)</c>
        /// （Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell）。
        ///
        /// 与旧的 <c>Compute(ChunkCellSource)</c> 相比（该方法已删除，避免两套算法并存导致两端
        /// 永久失配）：省掉每格一次委托调用，并把 contents/data 两次混入合并成一次，约快 2 倍。
        ///
        /// 切片：<c>slice = y >> 4</c>，每片 4096 格。16 片 × 4096 = 65536。
        /// </summary>
        public void ComputePacked(ChunkPackedValueSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            for (int i = 0; i < SliceCount; i++) m_slices[i] = 2166136261u;
            m_shaftHash = 0u;

            // 外层 = 256 个 (x,z) 列（每列在数组里连续 256 格），内层 = 16 个 y 切片 × 16 格。
            // 这样 hash 常驻寄存器，不需要每格读写 m_slices。
            for (int column = 0; column < SizeXZ * SizeXZ; column++)
            {
                int columnBase = column * Height;
                for (int slice = 0; slice < SliceCount; slice++)
                {
                    uint hash = m_slices[slice];
                    int start = columnBase + (slice << 4);
                    int end = start + SliceHeight;
                    for (int i = start; i < end; i++)
                    {
                        // 剥掉光照位：保留 contents(0~9) 与 data(14~31)
                        uint value = unchecked((uint)source(i)) & 0xFFFFC3FFu;
                        hash ^= value;
                        hash *= 16777619u;
                    }
                    m_slices[slice] = hash;
                }
            }
            m_computed = true;
        }

        public void ComputePacked(int[] cells)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (cells.Length < SizeXZ * SizeXZ * Height)
                throw new ArgumentException("Cell snapshot is smaller than one chunk.", nameof(cells));
            for (int i = 0; i < SliceCount; i++) m_slices[i] = 2166136261u;
            m_shaftHash = 0u;
            for (int column = 0; column < SizeXZ * SizeXZ; column++)
            {
                int columnBase = column * Height;
                for (int slice = 0; slice < SliceCount; slice++)
                {
                    uint hash = m_slices[slice];
                    int start = columnBase + (slice << 4);
                    int end = start + SliceHeight;
                    for (int i = start; i < end; i++)
                    {
                        hash ^= unchecked((uint)cells[i]) & 0xFFFFC3FFu;
                        hash *= 16777619u;
                    }
                    m_slices[slice] = hash;
                }
            }
            m_computed = true;
        }

        /// <summary>
        /// **顶层摘要**：16×16 = 256 个"顶格"的内容摘要，用于 chunk 加载时的快速校验。
        ///
        /// 读 <c>TerrainChunk.Shafts</c>（256 个 topHeight）+ <c>TerrainChunk.Cells</c>（256 个顶格）
        /// = <b>512 次</b>数组访问，比全量 <see cref="ComputePacked"/>（65536 格）快 128 倍。
        ///
        /// 为什么"只比顶层"是安全的（用户口径）：地形种子两端一致，人为/环境改动都会经
        /// <c>CellChange</c> 权威下发；两端唯一的自然差异是**天气/季节对最顶格的影响**
        /// （<c>SubsystemWeather.FreezeThawAndDepositSnow</c> 用 <c>GetTopHeightFast</c> 取顶层）。
        /// 因此加载时只要比顶层一致，就可以认为整块一致。
        ///
        /// 但顶层 hash 只能"快速否证"，不能"确认整块一致" —— 见计划文档第 8.1 节。
        ///
        /// 输出两部分**都必须比**（只比一个会漏判）：
        ///   · <c>TopHeightHash</c>：256 列的 topHeight。topHeight 不同 ⇒ 地形高度变了。
        ///   · <c>TopBlockHash</c> ：256 列顶格的 (contents,data)。同高度但顶格换成了别的方块。
        /// </summary>
        /// <param name="cells">TerrainChunk.Cells（布局 index = y + x*256 + z*4096）。</param>
        /// <param name="shafts">TerrainChunk.Shafts（布局 index = x + z*16，topHeight 在 bit 0~7）。</param>
        public void ComputeTopLayer(int[] cells, int[] shafts)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (shafts == null) throw new ArgumentNullException(nameof(shafts));
            if (cells.Length < SizeXZ * SizeXZ * Height)
                throw new ArgumentException("Cell array is smaller than one chunk.", nameof(cells));
            if (shafts.Length < SizeXZ * SizeXZ)
                throw new ArgumentException("Shaft array is smaller than one chunk.", nameof(shafts));

            uint topHeightHash = 2166136261u;
            uint topBlockHash = 2166136261u;

            for (int x = 0; x < SizeXZ; x++)
            {
                for (int z = 0; z < SizeXZ; z++)
                {
                    // topHeight 在 Shafts[x + z*16] 的 bit 0~7（Terrain.TopHeightMask）
                    int top = shafts[x + z * SizeXZ] & 0xFF;

                    topHeightHash ^= unchecked((uint)top);
                    topHeightHash *= 16777619u;

                    // 顶格在 Cells 里的下标（TerrainChunk.CalculateCellIndex）
                    int index = top + x * Height + z * Height * SizeXZ;
                    // 剥光照位：保留 contents(0~9) 与 data(14~31)。与 ComputePacked 同一掩码。
                    uint value = unchecked((uint)cells[index]) & 0xFFFFC3FFu;

                    topBlockHash ^= value;
                    topBlockHash *= 16777619u;
                }
            }

            m_topHeightHash = topHeightHash;
            m_topBlockHash = topBlockHash;
            m_topLayerComputed = true;
        }

        public void ComputeTopLayerSnapshot(int[] topHeights, int[] topValues)
        {
            if (topHeights == null) throw new ArgumentNullException(nameof(topHeights));
            if (topValues == null) throw new ArgumentNullException(nameof(topValues));
            if (topHeights.Length < SizeXZ * SizeXZ || topValues.Length < SizeXZ * SizeXZ)
                throw new ArgumentException("Top-layer snapshot is smaller than 256 columns.");
            uint heightHash = 2166136261u;
            uint blockHash = 2166136261u;
            for (int x = 0; x < SizeXZ; x++)
            {
                for (int z = 0; z < SizeXZ; z++)
                {
                    int column = x + z * SizeXZ;
                    heightHash ^= unchecked((uint)topHeights[column]);
                    heightHash *= 16777619u;
                    blockHash ^= unchecked((uint)topValues[column]) & 0xFFFFC3FFu;
                    blockHash *= 16777619u;
                }
            }
            m_topHeightHash = heightHash;
            m_topBlockHash = blockHash;
            m_topLayerComputed = true;
        }

        /// <summary>需要把 shafts 计入时，加载时一并算（<see cref="IncludeShafts"/> 为 true 才有效）。</summary>
        public void ComputeShafts(Func<int, int, int> shaftSource)
        {
            m_shaftHash = 0u;
            if (!IncludeShafts || shaftSource == null) return;
            for (int x = 0; x < SizeXZ; x++)
            {
                for (int z = 0; z < SizeXZ; z++) m_shaftHash ^= ShaftHash(x, z, shaftSource(x, z));
            }
        }

        /// <summary>卸载时丢掉摘要（只清状态，不计算任何东西）。</summary>
        public void Forget()
        {
            for (int i = 0; i < SliceCount; i++) m_slices[i] = 0u;
            m_shaftHash = 0u;
            m_computed = false;
            m_topHeightHash = 0u;
            m_topBlockHash = 0u;
            m_topLayerComputed = false;
        }

        /// <summary>哪些切片与对端不同 —— 决定回传哪几片（不一致才用得上）。</summary>
        public List<int> DifferingSlices(ChunkDigest other, int max = SliceCount)
        {
            var list = new List<int>();
            if (other == null) return list;
            for (int i = 0; i < SliceCount && list.Count < max; i++)
            {
                if (m_slices[i] != other.m_slices[i]) list.Add(i);
            }
            return list;
        }

        public bool Matches(ChunkDigest other) => other != null && HasValue && other.HasValue && Value == other.Value;
    }

    /// <summary>摘要裁决结果。</summary>
    public enum DigestVerdict : byte
    {
        /// <summary>主机手上没有可用内容（未加载 / 已卸载）→ 不能判，让客户端稍后重问。</summary>
        NotReady = 0,
        /// <summary>一致：两边都是最新的。</summary>
        Match = 1,
        /// <summary>不一致：客户端这份要覆盖，主机回传该块（或只回不同的切片）。</summary>
        Mismatch = 2
    }

    public enum DigestWorkKind : byte
    {
        TopLayerInitial = 0,
        TopLayerConfirmation = 1,
        FullFinal = 2,
        FullExplicit = 3,
        TopLayerRefresh = 4
    }

    public readonly struct DigestWorkResult
    {
        public DigestWorkResult(ChunkKey key, int generation, DigestWorkKind kind,
            uint value, uint topHeightHash, uint topBlockHash, string error, uint purpose = 0u)
        {
            Key = key;
            Generation = generation;
            Kind = kind;
            Value = value;
            TopHeightHash = topHeightHash;
            TopBlockHash = topBlockHash;
            Error = error ?? string.Empty;
            Purpose = purpose;
        }

        public ChunkKey Key { get; }
        public int Generation { get; }
        public DigestWorkKind Kind { get; }
        public uint Value { get; }
        public uint TopHeightHash { get; }
        public uint TopBlockHash { get; }
        public string Error { get; }
        public bool Succeeded => Error.Length == 0;
        public uint Purpose { get; }
    }

    public sealed class ChunkDigestPool : IDisposable
    {
        private readonly SemaphoreSlim m_topSlots;
        private readonly SemaphoreSlim m_fullSlots;
        private readonly ConcurrentQueue<DigestWorkResult> m_completed =
            new ConcurrentQueue<DigestWorkResult>();
        private bool m_disposed;

        public ChunkDigestPool(int topSlots = 20, int fullSlots = 4)
        {
            m_topSlots = new SemaphoreSlim(Math.Max(1, topSlots), Math.Max(1, topSlots));
            m_fullSlots = new SemaphoreSlim(Math.Max(1, fullSlots), Math.Max(1, fullSlots));
        }

        public bool TrySubmitTopLayer(ChunkKey key, int generation, int[] cells, int[] shafts,
            DigestWorkKind kind, uint purpose = 0u)
        {
            if (m_disposed) throw new ObjectDisposedException(nameof(ChunkDigestPool));
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (shafts == null) throw new ArgumentNullException(nameof(shafts));
            if (!m_topSlots.Wait(0)) return false;

            int[] heights = ArrayPool<int>.Shared.Rent(ChunkDigest.SizeXZ * ChunkDigest.SizeXZ);
            int[] values = ArrayPool<int>.Shared.Rent(ChunkDigest.SizeXZ * ChunkDigest.SizeXZ);
            try
            {
                for (int x = 0; x < ChunkDigest.SizeXZ; x++)
                {
                    for (int z = 0; z < ChunkDigest.SizeXZ; z++)
                    {
                        int column = x + z * ChunkDigest.SizeXZ;
                        int top = shafts[column] & 0xFF;
                        heights[column] = top;
                        values[column] = cells[top + x * ChunkDigest.Height +
                            z * ChunkDigest.Height * ChunkDigest.SizeXZ];
                    }
                }
                int[] capturedHeights = heights;
                int[] capturedValues = values;
                heights = null;
                values = null;
                Task.Run(() => ComputeTopLayer(key, generation, kind, capturedHeights, capturedValues, purpose));
                return true;
            }
            catch
            {
                if (heights != null) ArrayPool<int>.Shared.Return(heights);
                if (values != null) ArrayPool<int>.Shared.Return(values);
                m_topSlots.Release();
                throw;
            }
        }

        public bool TrySubmitFull(ChunkKey key, int generation, int[] cells, DigestWorkKind kind)
        {
            if (m_disposed) throw new ObjectDisposedException(nameof(ChunkDigestPool));
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (!m_fullSlots.Wait(0)) return false;
            int[] snapshot = ArrayPool<int>.Shared.Rent(ChunkDigest.SizeXZ * ChunkDigest.SizeXZ * ChunkDigest.Height);
            try
            {
                Array.Copy(cells, snapshot, ChunkDigest.SizeXZ * ChunkDigest.SizeXZ * ChunkDigest.Height);
                int[] captured = snapshot;
                snapshot = null;
                Task.Run(() => ComputeFull(key, generation, kind, captured));
                return true;
            }
            catch
            {
                if (snapshot != null) ArrayPool<int>.Shared.Return(snapshot);
                m_fullSlots.Release();
                throw;
            }
        }

        public bool TrySubmitFull(ChunkKey key, int generation, ChunkPackedValueSource source,
            DigestWorkKind kind)
        {
            if (m_disposed) throw new ObjectDisposedException(nameof(ChunkDigestPool));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!m_fullSlots.Wait(0)) return false;
            int[] snapshot = ArrayPool<int>.Shared.Rent(ChunkDigest.SizeXZ * ChunkDigest.SizeXZ * ChunkDigest.Height);
            try
            {
                for (int i = 0; i < ChunkDigest.SizeXZ * ChunkDigest.SizeXZ * ChunkDigest.Height; i++)
                    snapshot[i] = source(i);
                int[] captured = snapshot;
                snapshot = null;
                Task.Run(() => ComputeFull(key, generation, kind, captured));
                return true;
            }
            catch
            {
                if (snapshot != null) ArrayPool<int>.Shared.Return(snapshot);
                m_fullSlots.Release();
                throw;
            }
        }

        public int DrainCompleted(ICollection<DigestWorkResult> target, int maximum)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            int count = 0;
            while (count < Math.Max(0, maximum) && m_completed.TryDequeue(out DigestWorkResult result))
            {
                target.Add(result);
                count++;
            }
            return count;
        }

        private void ComputeTopLayer(ChunkKey key, int generation, DigestWorkKind kind,
            int[] heights, int[] values, uint purpose)
        {
            try
            {
                var digest = new ChunkDigest();
                digest.ComputeTopLayerSnapshot(heights, values);
                m_completed.Enqueue(new DigestWorkResult(key, generation, kind, 0u,
                    digest.TopHeightHash, digest.TopBlockHash, null, purpose));
            }
            catch (Exception e)
            {
                m_completed.Enqueue(new DigestWorkResult(key, generation, kind, 0u, 0u, 0u,
                    e.GetType().Name + ": " + e.Message, purpose));
            }
            finally
            {
                ArrayPool<int>.Shared.Return(heights);
                ArrayPool<int>.Shared.Return(values);
                m_topSlots.Release();
            }
        }

        private void ComputeFull(ChunkKey key, int generation, DigestWorkKind kind, int[] snapshot)
        {
            try
            {
                var digest = new ChunkDigest();
                digest.ComputePacked(snapshot);
                m_completed.Enqueue(new DigestWorkResult(key, generation, kind, digest.Value, 0u, 0u, null));
            }
            catch (Exception e)
            {
                m_completed.Enqueue(new DigestWorkResult(key, generation, kind, 0u, 0u, 0u,
                    e.GetType().Name + ": " + e.Message));
            }
            finally
            {
                ArrayPool<int>.Shared.Return(snapshot);
                m_fullSlots.Release();
            }
        }

        public void Dispose()
        {
            m_disposed = true;
        }
    }

    /// <summary>客户端一个 chunk 的新鲜度状态。</summary>
    public enum ChunkFreshness : byte
    {
        /// <summary>还没校验（刚加载或刚被要求重试）。</summary>
        Unverified = 0,
        /// <summary>已上报摘要，等主机裁决。</summary>
        Verifying = 1,
        /// <summary>主机确认一致，或后来通过数据消息补上。</summary>
        Fresh = 2,
        /// <summary>主机说不一致 → 等回传内容覆盖。</summary>
        Stale = 3
    }

    /// <summary>一条上报条目（客户端 → 主机）。</summary>
    public readonly struct DigestEntry
    {
        public DigestEntry(ChunkKey key, uint hash, uint purpose = 0u)
        { Key = key; Hash = hash; Purpose = purpose; }
        public ChunkKey Key { get; }
        public uint Hash { get; }
        public uint Purpose { get; }
    }

    /// <summary>
    /// 加载时校验状态机（纯逻辑）。
    ///
    /// 流程（用户定）：
    ///   · 客户端**加载完**一块 → 现算一次摘要 → 上报；
    ///   · 主机比对：一致 → 两边都是新的；不一致 → 回传正确内容；
    ///   · **加载之后的改动不再比对**：地形通道是 TCP，改动消息不会丢；
    ///   · **卸载**只做一件事：丢掉本块状态（重载时会重新算一次摘要再校验）。
    ///
    /// 演示页标记：HSH03。
    /// </summary>
    public sealed class ChunkLoadVerifier
    {
        private readonly Dictionary<ChunkKey, ChunkFreshness> m_states = new Dictionary<ChunkKey, ChunkFreshness>();
        private readonly Dictionary<ChunkKey, uint> m_confirmedHashes = new Dictionary<ChunkKey, uint>();
        private readonly Dictionary<ChunkKey, int> m_retries = new Dictionary<ChunkKey, int>();

        /// <summary>主机 NotReady 时的重问上限；超限就放弃自动重试（交上层，不静默死循环）。</summary>
        public int MaxRetries { get; set; } = 5;

        public int TrackedChunks => m_states.Count;

        public int PendingChunks
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<ChunkKey, ChunkFreshness> pair in m_states)
                {
                    if (pair.Value == ChunkFreshness.Verifying || pair.Value == ChunkFreshness.Unverified) n++;
                }
                return n;
            }
        }

        public int VerifiedCount { get; private set; }
        public int ResyncedCount { get; private set; }
        public int NotReadyCount { get; private set; }
        public int AbandonedCount { get; private set; }
        /// <summary>一次校验里"省下来的"常见情形：摘要相同，不用传数据。</summary>
        public int MatchWithoutPayloadCount { get; private set; }

        public ChunkFreshness StateOf(ChunkKey key) =>
            m_states.TryGetValue(key, out ChunkFreshness state) ? state : ChunkFreshness.Unverified;

        public bool IsFresh(ChunkKey key) => StateOf(key) == ChunkFreshness.Fresh;

        public uint ConfirmedHash(ChunkKey key) =>
            m_confirmedHashes.TryGetValue(key, out uint hash) ? hash : 0u;

        /// <summary>
        /// 这块现在还**会不会**开始校验（不改变任何状态）。
        /// 客户端"块加载完"只入队、真正算摘要要等几帧（每帧预算），所以入队前必须先用与
        /// <see cref="TryBeginVerify"/> 同一口径的前置条件筛一次 —— 否则会给"已 Fresh / 正在校验"
        /// 的块白算一次 65536 格摘要。
        /// </summary>
        public bool WouldBeginVerify(ChunkKey key)
        {
            ChunkFreshness state = StateOf(key);
            if (state == ChunkFreshness.Fresh || state == ChunkFreshness.Verifying) return false;
            if (m_retries.TryGetValue(key, out int retries) && retries > MaxRetries) return false;
            return true;
        }

        /// <summary>
        /// 客户端"加载完一块、算完摘要"后调用。返回 false 表示不需要上报
        /// （已 Fresh / 正在校验 / 重试次数用尽）。
        /// </summary>
        public bool TryBeginVerify(ChunkKey key, uint clientHash, out DigestEntry entry)
        {
            entry = default;
            if (!WouldBeginVerify(key))
            {
                // 与旧行为同一口径：只有"重报次数用尽"才计入放弃（Fresh / Verifying 是正常拒绝）。
                ChunkFreshness state = StateOf(key);
                if (state != ChunkFreshness.Fresh && state != ChunkFreshness.Verifying) AbandonedCount++;
                return false;
            }
            m_states[key] = ChunkFreshness.Verifying;
            entry = new DigestEntry(key, clientHash);
            return true;
        }

        /// <summary>
        /// **强制再核对**一块（"内存里但不在兴趣窗口内"的区块收到改动后必须证明与主机一致）。
        /// 与 <see cref="TryBeginVerify"/> 的区别：允许 Fresh / 重试用尽重新进入 Verifying
        /// （这正是"再证明一次"），但 Verifying（已有一条上报在途）与 Stale（不一致已确认、
        /// 等主机回传整块）不重复发起。返回 false = 这次不需要（也不会）上报。
        /// </summary>
        public bool TryBeginReverify(ChunkKey key, uint clientHash, out DigestEntry entry)
        {
            entry = default;
            ChunkFreshness state = StateOf(key);
            if (state == ChunkFreshness.Verifying || state == ChunkFreshness.Stale) return false;
            m_states[key] = ChunkFreshness.Verifying;
            entry = new DigestEntry(key, clientHash);
            return true;
        }

        /// <summary>
        /// **主机回传快照落地后的"再证明一次"**（实机故障的根因修复）。
        ///
        /// 与 <see cref="TryBeginVerify"/> 的唯一区别：**允许 Fresh**。
        /// 为什么必须允许：主机判 Mismatch 后回传整块；如果这期间客户端本地已经通过别的路径一致了
        /// （例如背压漏掉的 CellChange 后来按历史补发到位），本端状态机停在 Fresh，而旧代码在
        /// 快照落地后走 <see cref="TryBeginVerify"/>（Fresh → 拒绝）⇒ 客户端**不再上报**，
        /// 主机那条 Mismatch **永远没人重判**（实机：区块 (30,-4)/(30,-3) 一直红，客户端"不一致 0"/"队列 0"）。
        /// 快照刚覆盖过这块 ⇒ 它必须重新证明一次；Verifying（已有一条上报在途）与 NotReady 重报次数
        /// 用尽（<see cref="MaxRetries"/>）仍然不重复发起。
        /// </summary>
        public bool TryBeginResyncConfirm(ChunkKey key, uint clientHash, out DigestEntry entry)
        {
            entry = default;
            if (StateOf(key) == ChunkFreshness.Verifying) return false;
            if (m_retries.TryGetValue(key, out int retries) && retries > MaxRetries) return false;
            m_states[key] = ChunkFreshness.Verifying;
            entry = new DigestEntry(key, clientHash);
            return true;
        }

        /// <summary>
        /// 把一块**明确退回"未核对"**（不增加 NotReady 重试次数）。
        /// 用于"主机回传的整块快照因过期被丢弃"：这一块既没有被覆盖、也没有一条上报在途，
        /// 继续停在 Stale（"等主机回传"）就再也不会有人发起核对 —— 必须退回未核对并重新上报，
        /// 主机才会基于当下内容重发新快照。与 <see cref="OnNotReady"/> 的区别：那是主机说"还没好"。
        /// </summary>
        public void ForceUnverified(ChunkKey key)
        {
            m_states[key] = ChunkFreshness.Unverified;
        }

        /// <summary>主机侧裁决：内容可用才谈得上比对。</summary>
        public static DigestVerdict Compare(uint clientHash, uint hostHash, bool hostHasContents) =>
            !hostHasContents ? DigestVerdict.NotReady
            : clientHash == hostHash ? DigestVerdict.Match
            : DigestVerdict.Mismatch;

        /// <summary>客户端收到裁决（主机已确认有内容）。</summary>
        public void OnVerdict(ChunkKey key, in DigestEntry entry, uint hostHash)
        {
            DigestVerdict verdict = Compare(entry.Hash, hostHash, hostHasContents: true);
            switch (verdict)
            {
                case DigestVerdict.Match:
                    m_states[key] = ChunkFreshness.Fresh;
                    m_confirmedHashes[key] = hostHash;
                    m_retries.Remove(key);
                    VerifiedCount++;
                    MatchWithoutPayloadCount++;
                    break;
                case DigestVerdict.Mismatch:
                    m_states[key] = ChunkFreshness.Stale;
                    ResyncedCount++;
                    break;
                default:
                    Retry(key);
                    break;
            }
        }

        /// <summary>主机回答"我手上没有可用内容"。</summary>
        public void OnNotReady(ChunkKey key)
        {
            NotReadyCount++;
            Retry(key);
        }

        private void Retry(ChunkKey key)
        {
            m_retries.TryGetValue(key, out int retries);
            m_retries[key] = retries + 1;
            m_states[key] = ChunkFreshness.Unverified;
        }

        /// <summary>回传内容已覆盖到位。</summary>
        public void OnResyncApplied(ChunkKey key, uint hostHash)
        {
            m_states[key] = ChunkFreshness.Fresh;
            m_confirmedHashes[key] = hostHash;
            m_retries.Remove(key);
        }

        /// <summary>
        /// 收到并应用了一条带摘要的改动消息 —— **加载后的差异走这条**，不再比对。
        /// TCP 保证它一定到达，所以这里不需要"缺口检测/巡检"那套。
        /// </summary>
        public void OnDataApplied(ChunkKey key, uint hostHash)
        {
            m_states[key] = ChunkFreshness.Fresh;
            m_confirmedHashes[key] = hostHash;
            m_retries.Remove(key);
        }

        /// <summary>卸载：只丢状态。重载时会重新算摘要、重新校验一次。</summary>
        public void OnChunkUnloaded(ChunkKey key)
        {
            m_states.Remove(key);
            m_confirmedHashes.Remove(key);
            m_retries.Remove(key);
        }

        public void Clear()
        {
            m_states.Clear();
            m_confirmedHashes.Clear();
            m_retries.Clear();
        }

        /// <summary>把待校验的块按批取出来（一块一条消息太碎）。</summary>
        public List<DigestEntry> TakePendingBatch(int maxEntries)
        {
            var batch = new List<DigestEntry>(Math.Min(maxEntries, m_states.Count));
            foreach (KeyValuePair<ChunkKey, ChunkFreshness> pair in m_states)
            {
                if (batch.Count >= maxEntries) break;
                if (pair.Value != ChunkFreshness.Verifying) continue;
                batch.Add(new DigestEntry(pair.Key, 0u));   // 摘要在上层随块保存
            }
            return batch;
        }
    }

    /// <summary>
    /// 地形同步的传输契约（用户定：**TCP，不允许静默丢弃**）。
    /// 这是一份"决策记录 + 接口草案"，实现落在 transport 模块；放在这里是为了让策略可被断言。
    /// 演示页标记：HSH09。
    /// </summary>
    public readonly struct TerrainSyncPolicy
    {
        public TerrainSyncPolicy(bool orderedReliableChannel, bool allowSilentDrop, bool verifyOnLoadOnly,
            bool verifyOnUnload, int backpressureFrames)
        {
            OrderedReliableChannel = orderedReliableChannel;
            AllowSilentDrop = allowSilentDrop;
            VerifyOnLoadOnly = verifyOnLoadOnly;
            VerifyOnUnload = verifyOnUnload;
            BackpressureFrames = backpressureFrames;
        }

        /// <summary>地形通道是有序可靠流（TCP）。</summary>
        public bool OrderedReliableChannel { get; }
        /// <summary>是否允许"消息被静默丢掉而调用方不知道"。**必须为 false**。</summary>
        public bool AllowSilentDrop { get; }
        /// <summary>摘要只在加载时算/比。</summary>
        public bool VerifyOnLoadOnly { get; }
        /// <summary>卸载时是否要算摘要。false = 只丢状态。</summary>
        public bool VerifyOnUnload { get; }
        /// <summary>每客户端待发帧上限；超限必须是显式状态（Backpressured/Failed），不是丢包。</summary>
        public int BackpressureFrames { get; }

        public static TerrainSyncPolicy Default => new TerrainSyncPolicy(
            orderedReliableChannel: true, allowSilentDrop: false, verifyOnLoadOnly: true,
            verifyOnUnload: false, backpressureFrames: 256);
    }

    /// <summary>地形通道的健康状态。**没有"丢了一些但还能用"这种状态。**</summary>
    public enum TerrainChannelState : byte
    {
        Connected = 0,
        /// <summary>对端读得慢，发送队列积压到上限 —— 显式可见，可降级（暂停加载新块）。</summary>
        Backpressured = 1,
        /// <summary>连接失败/断开 —— 显式事件，不允许当成"静默丢包"继续跑。</summary>
        Failed = 2
    }

    /// <summary>地形通道接口草案：由 transport 模块用 TCP 实现。</summary>
    public interface ITerrainChannel
    {
        TerrainChannelState State { get; }
        /// <summary>入队一帧。返回 false 表示队列已满（调用方必须处理这个显式失败，不能当成丢包）。</summary>
        bool TrySend(int clientId, byte[] payload, int length);
        event Action<int, TerrainChannelState> StateChanged;
    }

    /// <summary>
    /// 回传一块的载荷估算。
    /// **陷阱**：一格 4 字节 → 整块 65536 格 = 256 KB，直接传是灾难；必须按列 RLE（地形在竖直方向极度连续）。
    /// 演示页标记：HSH07。
    /// </summary>
    public static class ChunkPayload
    {
        public const int RawBytesPerCell = 4;
        /// <summary>一个游程的字节数：长度 2 B + 值 4 B。</summary>
        public const int RunBytes = 6;

        public static int RawChunkBytes => ChunkDigest.SizeXZ * ChunkDigest.SizeXZ * ChunkDigest.Height * RawBytesPerCell;

        /// <summary>16×16 列竖直 RLE 的游程总数。</summary>
        public static int RunCount(ChunkCellSource source)
        {
            if (source == null) return 0;
            int runs = 0;
            for (int x = 0; x < ChunkDigest.SizeXZ; x++)
            {
                for (int z = 0; z < ChunkDigest.SizeXZ; z++)
                {
                    source(x, 0, z, out int pc, out int pd);
                    int prev = Pack(pc, pd);
                    runs++;
                    for (int y = 1; y < ChunkDigest.Height; y++)
                    {
                        source(x, y, z, out int c, out int d);
                        int cur = Pack(c, d);
                        if (cur != prev) { runs++; prev = cur; }
                    }
                }
            }
            return runs;
        }

        public static int EstimateColumnRleBytes(ChunkCellSource source) => RunCount(source) * RunBytes;

        /// <summary>
        /// 实际该发多少：<b>min(原始, RLE)</b>。
        /// RLE 对"下半实心、上半空气"的地形极省（每列 2–4 段），对噪声数据反而更贵 —— 所以要取小。
        /// </summary>
        public static int ChoosePayloadBytes(ChunkCellSource source, out bool useRle)
        {
            int raw = RawChunkBytes;
            int rle = EstimateColumnRleBytes(source);
            useRle = rle < raw;
            return useRle ? rle : raw;
        }

        private static int Pack(int contents, int data) => (contents << 16) | (data & 0xFFFF);
    }

    /// <summary>上报消息的批量与带宽账。条目 = 2×int16 坐标 + 4 B 摘要 = 6 B；批头 8 B。</summary>
    public static class DigestBatching
    {
        public const int BytesPerEntry = 6;
        public const int BatchHeaderBytes = 8;
        public const int DefaultEntriesPerMessage = 32;

        public static int MessageCount(int chunkCount, int entriesPerMessage = DefaultEntriesPerMessage)
        {
            if (chunkCount <= 0) return 0;
            if (entriesPerMessage <= 0) entriesPerMessage = DefaultEntriesPerMessage;
            return (chunkCount + entriesPerMessage - 1) / entriesPerMessage;
        }

        public static int BytesFor(int chunkCount, int entriesPerMessage = DefaultEntriesPerMessage)
        {
            int messages = MessageCount(chunkCount, entriesPerMessage);
            if (messages == 0) return 0;
            int last = chunkCount - (messages - 1) * entriesPerMessage;
            return (messages - 1) * (BatchHeaderBytes + entriesPerMessage * BytesPerEntry)
                + BatchHeaderBytes + last * BytesPerEntry;
        }

        public static int WindowChunks(int radius) => (2 * radius + 1) * (2 * radius + 1);

        /// <summary>截断摘要的碰撞率：n 块、每块 1/2^bits。说明 16 bit 不能当唯一判据。</summary>
        public static double FalseMatchProbability(int chunkCount, int hashBits)
        {
            if (hashBits <= 0) return 1.0;
            double perChunk = 1.0 / Math.Pow(2.0, hashBits);
            return 1.0 - Math.Pow(1.0 - perChunk, chunkCount);
        }
    }
}

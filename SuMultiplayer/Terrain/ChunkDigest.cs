using System;
using System.Collections.Generic;

namespace SuMultiplayer.Terrain
{
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

    /// <summary>
    /// 读一格内容。**contents 与 data 分开传**：光照不在 API 里出现，从源头保证它进不了摘要。
    /// 引擎侧实现：contents = Terrain.ExtractContents(value)，data = Terrain.ExtractData(value)。
    /// </summary>
    public delegate void ChunkCellSource(int x, int y, int z, out int contents, out int data);

    /// <summary>
    /// 一个 chunk 的内容摘要（32 bit）。
    ///
    /// **只在加载时计算一次**（用户定）：块卸载时只是把摘要丢掉，不做任何维护；
    /// 加载之后的改动不碰摘要 —— 那些差异走数据消息，由传输层保证送达。
    /// 主机侧需要自己的摘要时**按需现算**（一次 0.20 ms），不维护常驻表。
    ///
    /// 结构：
    ///   sliceHash[s] = XOR over 该切片 4096 格 of CellHash(x,y,z,contents,data)
    ///   chunkHash    = XOR over 16 个 sliceHash
    /// 为什么要切片：不一致时只回传**不同的切片**（4096 格），而不是整块 65536 格。
    ///
    /// 演示页标记：HSH02。
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

        /// <summary>未计算时为 false。界面/协议里 <see cref="Value"/> == 0 也代表「未知」。</summary>
        public bool HasValue => m_computed;

        /// <summary>
        /// data 的规范化钩子：(contents, data) → canonical data。
        /// 家具必须挂这个 —— 家具把**本机 m_furnitureDesigns[1024] 的槽位号**写进 data
        /// （FurnitureBlock.SetDesignIndex/GetDesignIndex），两边槽位号不同会导致永久误判。
        /// 默认恒等。
        /// </summary>
        public Func<int, int, int> DataCanonicalizer { get; set; }

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

        /// <summary>单格摘要：位置 + 内容 + 数据。同内容在任何机器上得到同一值。</summary>
        public static uint CellHash(int x, int y, int z, int contents, int data)
        {
            ulong h = 0x9E3779B97F4A7C15UL;
            h ^= (ulong)(uint)x * 0xBF58476D1CE4E5B9UL;
            h ^= (ulong)(uint)y * 0x94D049BB133111EBUL;
            h ^= (ulong)(uint)z * 0xD6E8FEB86659FD93UL;
            h ^= (ulong)(uint)contents * 0xA0761D6478BD642FUL;
            h ^= (ulong)(uint)data * 0xE7037ED1A0B428DBUL;
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27; h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            uint mixed = (uint)(h ^ (h >> 32));
            // 全空气格也要贡献非零值：位置必须参与，否则"哪里空"分不出来
            return mixed == 0 ? 0x9E3779B9u : mixed;
        }

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
        /// 加载时计算一次（65536 格 ≈ 0.20 ms，跑在加载线程）。之后不再改动摘要。
        /// </summary>
        public void Compute(ChunkCellSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            for (int i = 0; i < SliceCount; i++) m_slices[i] = 0u;
            m_shaftHash = 0u;
            for (int x = 0; x < SizeXZ; x++)
            {
                for (int z = 0; z < SizeXZ; z++)
                {
                    for (int y = 0; y < Height; y++)
                    {
                        source(x, y, z, out int contents, out int data);
                        int canonical = DataCanonicalizer == null ? data : DataCanonicalizer(contents, data);
                        m_slices[SliceOf(y)] ^= CellHash(x, y, z, contents, canonical);
                    }
                }
            }
            m_computed = true;
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
        public DigestEntry(ChunkKey key, uint hash) { Key = key; Hash = hash; }
        public ChunkKey Key { get; }
        public uint Hash { get; }
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

using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

using SuMultiplayer.Protocol;

using Google.Protobuf;
using PT = SuMultiplayer.Protocol.Terrain;
namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 追帧控制帧的阶段（MessageType.CatchUp 载荷第 1 字节）。
    /// 对应旧实现 <c>TerrainRecoveryStage</c>（Request / ReplayBatch / Barrier / Acknowledge / Ready /
    /// ResyncRequired，见 Mod/ScMultiplayer/Message/TerrainRecoveryMessage.cs）：
    /// 本工程把 ReplayBatch 换成复用 CellChange 帧（末尾带序号），控制帧只留四段 + 一个完成回报。
    /// </summary>
    public enum CatchUpStage : byte
    {
        /// <summary>客户端 → 主机：我上次应用到的改动序号（请求重放之后的改动）。</summary>
        Request = 0,
        /// <summary>主机 → 客户端：本轮追帧开始（请求序号 / 当前水位 / 最旧保留序号 / 将重放的改动条数）。</summary>
        Begin = 1,
        /// <summary>主机 → 客户端：重放数据已全部发出（客户端应已到 <see cref="Begin"/> 声明的水位）。</summary>
        Barrier = 2,
        /// <summary>主机 → 客户端：**明确拒绝**（历史已裁剪 / 不是被接受的重连）；客户端必须整图重下。</summary>
        Rejected = 3,
        /// <summary>客户端 → 主机：我已应用到某序号（确认追帧完成；未到水位时主机会再发一轮）。</summary>
        Applied = 4
    }

    /// <summary>一格改动（contents + data；<b>光照不在里面</b> —— 由引擎自己重算）。</summary>
    public readonly struct CellEdit
    {
        public CellEdit(int x, int y, int z, int contents, int data)
        {
            X = x; Y = y; Z = z; Contents = contents; Data = data;
        }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Contents { get; }
        public int Data { get; }
        public int Packed => ((Contents & 0xFFFF) << 16) | (Data & 0xFFFF);
        public static CellEdit FromPacked(int x, int y, int z, int packed) =>
            new CellEdit(x, y, z, (packed >> 16) & 0xFFFF, packed & 0xFFFF);
    }

    /// <summary>
    /// 引擎侧要实现的接口（本轮不接线；测试用合成世界实现）。
    /// 接线提示：<see cref="ApplyCells"/> 的实现走"不标脏"的路径
    /// （<c>SubsystemTerrain.ChangeCell(..., updateModificationCounter: false)</c> 的等价方式），
    /// 否则回传内容会把块重新标脏、又触发一轮同步。
    /// </summary>
    public interface ITerrainWorld
    {
        /// <summary>内容是否可用（引擎判据：<c>TerrainChunkState &gt; InvalidContents4</c>）。</summary>
        bool HasContents(ChunkKey key);
        /// <summary>该块的格子读取器（16×16×256；contents/data 分开）。</summary>
        ChunkCellSource GetCellSource(ChunkKey key);
        void ApplyCells(ChunkKey key, IReadOnlyList<CellEdit> edits);
        /// <summary>
        /// 主机：请求引擎**只加载这一块**（临时单块地形更新位置；见 <see cref="OnDemandChunkLoads"/>）。
        /// 异步；完成后内容自然出现在 <see cref="HasContents"/>。
        /// 返回 false = 引擎当前明确不接受（世界未加载 / 没有 TerrainUpdater / 临时位置已满），
        /// <paramref name="reason"/> 是可直接写进日志的原因 —— **绝不静默失败、也绝不假装已经请求**。
        /// 实现方（Engine/TerrainBridge.cs）不得借此改动任何角色的兴趣半径 / 内容距离。
        /// </summary>
        bool TryRequestSingleChunkLoad(ChunkKey key, out string reason);
        /// <summary>
        /// 主机：撤销 <see cref="TryRequestSingleChunkLoad"/> 建立的临时加载（块交回引擎按它自己的
        /// 更新位置决定去留，下一帧回收）。没有登记过就什么也不做。
        /// </summary>
        void ReleaseSingleChunkLoad(ChunkKey key);
        /// <summary>引擎**已分配**（占用内存）的区块；与内容是否定稿无关（海洋 / 水面块也在里面）。</summary>
        IEnumerable<ChunkKey> MemoryChunks { get; }
        /// <summary>内容可用（常驻）的区块 —— 摘要 / 裁决 / 回传唯一认这一份。</summary>
        IEnumerable<ChunkKey> LoadedChunks { get; }
    }

    /// <summary>列式 RLE 载荷：每一列（x,z）按 256 高编码成若干游程。</summary>
    public static class ChunkPayloadCodec
    {
        /// <summary>一条游程 = x(1)+z(1)+startY(1)+len-1(1)+packed(4) = 8 字节。</summary>
        public const int RecordBytes = 8;
        public const int RecordsPerFrame = 5000;      // ≈ 39 KiB，低于 60 KiB 帧上限

        public const int RunRaw = 0;
        public const int RunRle = 1;

        /// <summary>把一个 chunk 编码成若干帧载荷（超出单帧上限就分片）。</summary>
        public static List<List<byte[]>> Encode(ChunkKey key, ITerrainWorld world, out bool useRle,
            long watermark = -1L)
        {
            useRle = true;
            var records = BuildColumnRuns(key, world);
            var parts = new List<byte[]>();
            int total = records.Count / RecordBytes;
            int partCount = Math.Max(1, (total + RecordsPerFrame - 1) / RecordsPerFrame);
            for (int p = 0; p < partCount; p++)
            {
                int from = p * RecordsPerFrame;
                int take = Math.Min(RecordsPerFrame, total - from);
                var message = new PT.ChunkResync
                {
                    Key = new PT.ChunkKey { X = key.X, Z = key.Z },
                    Index = p,
                    Count = partCount,
                    Entries = take,
                    Records = ByteString.CopyFrom(records.GetRange(from * RecordBytes,
                        take * RecordBytes).ToArray())
                };
                // 只有"编码时确实有水位"才写这个 optional 字段；-1 = 未知（字段缺席）。
                if (watermark >= 0) message.Watermark = watermark;
                parts.Add(message.ToByteArray());
            }
            return new List<List<byte[]>> { parts };
        }

        private static List<byte> BuildColumnRuns(ChunkKey key, ITerrainWorld world)
        {
            var records = new List<byte>(2048);
            if (!world.HasContents(key)) return records;
            ChunkCellSource source = world.GetCellSource(key);
            var column = new int[ChunkDigest.Height];
            for (int x = 0; x < ChunkDigest.SizeXZ; x++)
            {
                for (int z = 0; z < ChunkDigest.SizeXZ; z++)
                {
                    for (int y = 0; y < ChunkDigest.Height; y++)
                    {
                        source(x, y, z, out int c, out int d);
                        column[y] = ((c & 0xFFFF) << 16) | (d & 0xFFFF);
                    }
                    int runStart = 0;
                    int prev = column[0];
                    for (int y = 1; y <= ChunkDigest.Height; y++)
                    {
                        int cur = y < ChunkDigest.Height ? column[y] : int.MinValue;
                        if (cur == prev) continue;
                        records.Add((byte)x);
                        records.Add((byte)z);
                        records.Add((byte)runStart);
                        records.Add((byte)(y - runStart - 1));
                        records.Add((byte)prev);
                        records.Add((byte)(prev >> 8));
                        records.Add((byte)(prev >> 16));
                        records.Add((byte)(prev >> 24));
                        runStart = y;
                        prev = cur;
                    }
                }
            }
            return records;
        }

        public readonly struct Part
        {
            public Part(ChunkKey key, int index, int count, List<CellEdit> edits)
            {
                Key = key; Index = index; Count = count; Edits = edits;
            }
            public ChunkKey Key { get; }
            public int Index { get; }
            public int Count { get; }
            public List<CellEdit> Edits { get; }
            public bool IsLast => Index == Count - 1;
        }

        /// <summary>
        /// 读回传载荷里的"编码水位"（主机**编码这份快照那一刻**的改动序号）。
        /// 没有这个字段 = 水位未知（返回 false；调用方只按到达顺序落地）。
        /// </summary>
        public static bool TryReadWatermark(byte[] payload, out long watermark)
        {
            watermark = -1L;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PT.ChunkResync message = PT.ChunkResync.Parser.ParseFrom(payload);
                if (!message.HasWatermark) return false;
                watermark = message.Watermark;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>只读回传载荷的头（区块坐标 + 水位）；解析失败返回 false（整帧丢弃）。</summary>
        public static bool TryReadHeader(byte[] payload, out ChunkKey key, out long watermark, out string error)
        {
            key = default;
            watermark = -1L;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            try
            {
                PT.ChunkResync message = PT.ChunkResync.Parser.ParseFrom(payload);
                if (message.Key == null)
                {
                    error = "载荷缺少 key";
                    return false;
                }
                key = new ChunkKey(message.Key.X, message.Key.Z);
                watermark = message.HasWatermark ? message.Watermark : -1L;
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 这一片是不是"零格快照"（主机编码时已无该块内容）：Entries == 0 且只有一片。
        /// 主机据此**不发**零格快照（改回 NotReady）；客户端据此不把它当成已覆盖。
        /// </summary>
        public static bool IsEmptySnapshotPart(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PT.ChunkResync message = PT.ChunkResync.Parser.ParseFrom(payload);
                return message.Entries == 0 && message.Count <= 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static Part Decode(byte[] payload)
        {
            PT.ChunkResync message = PT.ChunkResync.Parser.ParseFrom(payload);
            if (message.Key == null) throw new InvalidOperationException("回传载荷缺少 key");
            int index = message.Index, count = message.Count, entries = message.Entries;
            byte[] records = message.Records?.ToByteArray() ?? Array.Empty<byte>();
            if (entries < 0 || records.Length != entries * RecordBytes)
                throw new InvalidOperationException("回传载荷的 records 长度 " + records.Length +
                    " ≠ entries(" + entries + ") × " + RecordBytes);
            var key = new ChunkKey(message.Key.X, message.Key.Z);
            var edits = new List<CellEdit>(entries * 4);
            for (int i = 0; i < entries; i++)
            {
                int offset = i * RecordBytes;
                int x = records[offset], z = records[offset + 1];
                int startY = records[offset + 2], lenMinus1 = records[offset + 3];
                int packed = records[offset + 4] | (records[offset + 5] << 8) |
                    (records[offset + 6] << 16) | (records[offset + 7] << 24);
                int len = lenMinus1 + 1;
                for (int k = 0; k < len; k++)
                    edits.Add(CellEdit.FromPacked(x, startY + k, z, packed));
            }
            return new Part(key, index, count, edits);
        }
    }

    public sealed class TerrainSyncOptions
    {
        /// <summary>单条摘要上报最多几条（超过整帧拒绝；一个 chunk 正常一条）。</summary>
        public const int MaxDigestEntriesPerMessage = 4096;

        public int DefaultRadius { get; set; } = 2;
        public int MaxRadius { get; set; } = 32;
        public int DigestBatch { get; set; } = 32;
        public int ResyncPartsPerTick { get; set; } = 4;
        public int MaxRetries { get; set; } = 5;
        public int DebugIntervalMs { get; set; } = 500;

        /// <summary>
        /// 主机每 tick 轮转检查多少条"欠着裁决"的上报（有界；不是一次扫全表）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:665-666
        ///         （旧实现每 tick 最多处理 4 条待办请求）
        /// </summary>
        public int PendingReportsPerTick { get; set; } = 16;

        /// <summary>
        /// 主机每 tick 最多裁决几块（每块要现算一次 65536 格摘要 ≈ 0.20ms，必须限流，别顶出卡顿）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:666
        ///         （旧实现 `while (sent &lt; 4 &amp;&amp; ...)`，每个 tick 只放行 4 条）
        /// </summary>
        public int PendingResolvesPerTick { get; set; } = 4;

        /// <summary>
        /// 主机侧**按需单块加载**的参数（逐块临时加载位置 / 退避 / 上限 / 超时 / 冷却 / 保留窗口）。
        /// 为什么不再有"同一块多久重请一次"的全局间隔：重请节奏属于台账，见
        /// <see cref="Terrain.OnDemandLoadOptions.RetryBaseMs"/>（指数退避 + 次数 / 时间上限 + 冷却）。
        /// 用户口径（2026-10-18）：客户端上报的边缘也要加载，但主机**不**放大任何角色的范围。
        /// </summary>
        public OnDemandLoadOptions OnDemandLoad { get; set; } = new OnDemandLoadOptions();

        /// <summary>
        /// 广播改动时"某个订阅端没发出去（显式背压 / 连接不可达）"的原因行最小间隔（毫秒）。
        /// 背压是事件不是丢包：限流成 10 s 一行，既能在日志里查到，又不会被一次批量改动刷屏。
        /// </summary>
        public int CellChangeFailureLogIntervalMs { get; set; } = 10000;

        /// <summary>
        /// 客户端收到 NotReady 后重报的首次退避（毫秒），每次翻倍。
        /// 上限见 <see cref="NotReadyRetryMaxMs"/>。
        /// </summary>
        public int NotReadyRetryBaseMs { get; set; } = 250;

        /// <summary>
        /// 客户端 NotReady 重报的退避上限（毫秒）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:630-631
        ///         （旧实现的待办重问节奏是 5s，这里取同一个上限）
        /// </summary>
        public int NotReadyRetryMaxMs { get; set; } = 5000;

        // ===================================================================================
        // 客户端每渲染帧预算（飞行时"一帧几十块"不再顶穿帧时间）
        //
        // 问题（实机）：平板飞行时引擎一帧内会把几十块推进到内容可用，旧实现在 ChunkLoaded 事件里
        // **当场**算摘要（ChunkDigest.Compute：16×16×256 = 65536 格 ≈ 0.20 ms/块，见该文件第 113 行），
        // 一帧 40 块就是 8 ms+；再叠加回传落地（整块最多 65536 格写入 + 一次同样的摘要重算）
        // 和"每收到一格改动就重算整块摘要"，单帧工作直接顶穿 16.7 ms（60fps）预算 → 帧数掉到个位数。
        //
        // 现在：事件只**入队**，<see cref="TerrainSyncClient.Tick"/>（每渲染帧一次）按下面的预算消化，
        // 剩余排队到后续帧 —— FIFO、不丢、不假成功（只有"块已卸载"才作废，并计入明确计数器）。
        // ===================================================================================

        /// <summary>客户端每帧最多算几块摘要（65536 格/块 ≈ 0.20 ms）。</summary>
        public int ClientDigestComputationsPerTick { get; set; } = DefaultClientDigestComputationsPerTick;

        /// <summary>
        /// 默认 4 块/帧。理由：
        ///   · 成本：4 × 0.20 ms ≈ 0.8 ms/帧，只占 60fps 帧预算（16.7 ms）的约 5%，
        ///     远小于引擎自己在飞行时生成地形的工作量 —— 不会和引擎抢帧；
        ///   · 吞吐：4 块 × 60 fps = 240 块/秒。实测平板飞行时常驻 151 块、主机侧摘要上报 412 条，
        ///     按 16 格/块与 10~20 m/s 的移动速度，新块大约每秒几十块（<100），240 块/秒有 2~4 倍余量；
        ///   · 积压可见：真出现持续饱和，状态行有"队列 K"，并且 ≥3 s 会写一行明确原因（见下两个参数）。
        /// 旧实现没有预算（等价于把该值设成 int.MaxValue）：块加载事件里当场算，飞行时一帧几十块 → 8 ms+ 尖峰。
        /// </summary>
        public const int DefaultClientDigestComputationsPerTick = 4;

        /// <summary>客户端每帧最多落地几个回传分片（一片 = 最多 5000 条游程 → 最多 65536 格写入 + 一次摘要重算）。</summary>
        public int ClientResyncPartsPerTick { get; set; } = DefaultClientResyncPartsPerTick;

        /// <summary>
        /// 默认 1 片/帧。理由：一片回传的落地成本（解码 + 最多 65536 次 SetCellValueFast +
        /// DowngradeChunkNeighborhoodState + 末片还要重算一次摘要）在 1 ms 量级，1 片/帧 ≈ ≤1~2 ms/帧；
        /// 上限也刻意设成 1（而不是 2~4）：回传落地会**改地形**，和引擎的几何/光照重算抢同一帧，
        /// 叠两片就可能出现肉眼可见的顿挫。积压只是延迟，不会丢（FIFO 队列 + 状态行"回传 K"）。
        /// 旧实现：收到即落地，一帧内最多会把 TerrainBridge 泵出的 256 帧里的回传分片全部落地。
        /// </summary>
        public const int DefaultClientResyncPartsPerTick = 1;

        /// <summary>
        /// 客户端每帧最多重算几块"加载后被改动"的摘要（CellChange 之后）。
        /// 独立且更小的预算：它只影响"裁决如何解释本端内容"，**不影响任何上报**（上报是加载时那一次），
        /// 所以绝不能抢占加载摘要的预算（后者是"每块都必须上报"的硬要求）。
        /// </summary>
        public int ClientHashRefreshesPerTick { get; set; } = DefaultClientHashRefreshesPerTick;

        /// <summary>
        /// 默认 2 块/帧（≈0.4 ms/帧）。为什么需要这个预算：主机把改动**一格一条消息**发下来
        /// （TerrainSyncHost.BroadcastCellChange），旧实现每收到一条就把整块 65536 格重算一遍 ——
        /// 实测主机侧一个会话广播了 10632 条改动，等价于 2 秒多的纯摘要计算，且集中在主机批量改动时爆发。
        /// 现在按块去重（同一块改了 100 格只重算一次）后仍有上界。
        /// </summary>
        public const int DefaultClientHashRefreshesPerTick = 2;

        /// <summary>客户端每帧最多发几条摘要上报（单条消息内的批量上限仍由 <see cref="DigestBatch"/> 管）。</summary>
        public int ClientReportEntriesPerTick { get; set; } = DefaultClientReportEntriesPerTick;

        /// <summary>
        /// 默认 4 条/帧 = 一条 32 字节左右的消息（每条 6 B，见 <see cref="DigestBatching.BytesPerEntry"/>）。
        /// 与 <see cref="ClientDigestComputationsPerTick"/> 取同一个数：稳态下每帧算几块就发几块，
        /// "本轮上报"因此永远不会超过"每帧摘要预算"，帧时间上界是可解释的。
        /// 发送本身只是入队（不阻塞），真正贵的是算摘要；这里的上限主要防止积压一次性灌给通道。
        /// </summary>
        public const int DefaultClientReportEntriesPerTick = 4;

        /// <summary>
        /// 客户端每帧最多发几条"内存区块集合（引擎已分配）"消息（整集分片 + 分配/释放增量各算一条）。
        /// 一条消息最多 <see cref="ClientChunkSetCodec.MaxEntriesPerMessage"/> 条条目（64×9 B + 头 ≈ 600 B），
        /// 所以 4 条/帧的最坏体积 ≈ 2.4 KiB/帧：整集（150 块 ≈ 3 片）一次重连就能在 1 帧内发完，
        /// 增量稳态通常 0~1 条/帧；发送本身只是入队（不阻塞），真正需要限的是别把通道灌满。
        /// 预算没发完的消息留在队列里（FIFO），下一帧继续 —— 不丢、不假成功。
        /// </summary>
        public int ClientChunkMessagesPerTick { get; set; } = DefaultClientChunkMessagesPerTick;

        /// <summary>
        /// 默认 4 条/帧。理由：整集按 64 条/片切，平板常驻 151 块 = 3 片，一帧发完；
        /// 飞行时每帧新加载几十块，按每片 64 条也只需 1~2 条/帧；4 条留了一倍余量。
        /// </summary>
        public const int DefaultClientChunkMessagesPerTick = 4;

        /// <summary>
        /// 主机侧"内存区块集合（引擎已分配）"日志的限流间隔（毫秒）：整集收齐一条一行；
        /// 增量按这个间隔汇总一行；非法/超限载荷在这个间隔内最多一行（计数照常累加）。
        /// </summary>
        public int ClientChunkLogIntervalMs { get; set; } = 10000;

        /// <summary>
        /// 客户端队列连续饱和多久才写一行明确原因（毫秒）。
        /// 3 s：飞行时的加载突发通常 <1 s 就能被预算消化；持续 3 s 以上说明预算真的在拖后腿，值得审计。
        /// </summary>
        public int ClientQueueSaturationLogMs { get; set; } = 3000;

        /// <summary>队列持续饱和时两行之间的最小间隔（毫秒）：10 s 一条，够审计又不刷屏。</summary>
        public int ClientQueueLogIntervalMs { get; set; } = 10000;

        // ===================================================================================
        // 断线重连追帧（同会话、主机没重启）：改动历史 + 请求/重放/屏障
        // 旧实现：Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:774-998
        //         （SendHostTerrainRecoveryRound / ReplayBatch / Barrier / ResyncRequired）
        // 旧常量：Core/ScMultiplayerRuntimeState.cs:894 TerrainRecoveryRetention = 60.0（秒）
        // ===================================================================================

        /// <summary>
        /// 改动历史保留时长（毫秒）。超过就被裁掉；重连请求的序号落在裁剪区间外 ⇒ 明确要求整图重下。
        /// Source: Mod/ScMultiplayer/Core/ScMultiplayerRuntimeState.cs:894（TerrainRecoveryRetention = 60.0）
        /// </summary>
        public int CatchUpRetentionMs { get; set; } = 60000;

        /// <summary>
        /// 改动历史硬上限（条）。时间之外再加一道**条数**上界：
        /// 高频改动（流体/火）下 60 秒可能有几十万条，不能只靠时间。
        /// 超出后裁掉最旧的并计数（有界，不会无限吃内存）。
        /// </summary>
        public int CatchUpMaxJournalEntries { get; set; } = 8192;

        /// <summary>一条追帧 CellChange 最多带多少条改动（旧实现一批最多 64 条载荷）。</summary>
        public int CatchUpBatchEntries { get; set; } = 64;

        /// <summary>
        /// 主机每 tick 最多发几批追帧（每批 ≤ <see cref="CatchUpBatchEntries"/> 条改动）。
        /// 有界，避免一次重连把整段历史在单帧里灌进通道。
        /// </summary>
        public int CatchUpBatchesPerTick { get; set; } = 4;

        /// <summary>客户端请求追帧后、没收到 Begin 时的重发间隔（毫秒）。</summary>
        public int CatchUpRequestRetryMs { get; set; } = 2000;

        /// <summary>客户端重发追帧请求的最大次数（用尽后明确写一行"主机没有回应"，不无限循环）。</summary>
        public int CatchUpRequestMaxRetries { get; set; } = 3;

        /// <summary>主机追帧屏障发出后等客户端 Applied 的上限（毫秒；超时写一行明确原因）。</summary>
        public int CatchUpAckTimeoutMs { get; set; } = 30000;

        /// <summary>改动历史裁剪 / 追帧拒绝的限流日志间隔（毫秒）。</summary>
        public int CatchUpLogIntervalMs { get; set; } = 10000;

        /// <summary>
        /// 主机每 tick 最多补发多少条"背压 / 追帧期间没发出去的改动"（有界，不顶穿帧）。
        /// 补发 = 按改动历史顺序重发（不是新改动抢先发）：客户端看到的序号因此永远单调，
        /// 旧值不会盖到新值上。
        /// </summary>
        public int CellChangeRepairPerTick { get; set; } = 256;

        /// <summary>
        /// 客户端每帧最多对几个"内存区块但不在兴趣窗口内"的块做**强制摘要核对**
        /// （65536 格/块 ≈ 0.20ms，默认 2 块/帧 ≈ 0.4ms）。队列 FIFO、不丢；
        /// 只有块还没有内容时才按 <see cref="ClientMemoryVerifyRetryMs"/> 等。
        /// </summary>
        public int ClientMemoryVerificationsPerTick { get; set; } = 2;

        /// <summary>强制核对时块还没有内容 → 下一次重试的最小间隔（毫秒；不丢、也不每帧空转）。</summary>
        public int ClientMemoryVerifyRetryMs { get; set; } = 1000;

        /// <summary>
        /// 追帧期间收到"比追帧水位还新"的实时改动 → 先放进**有界暂存队列**，等重放结束再按序号应用
        /// （否则旧的重放会把新改动盖回去）。超过上限 = 明确失败（计数 + 一行原因 + 强制核对）。
        /// </summary>
        public int ClientLiveHoldMaxEntries { get; set; } = 4096;

        /// <summary>客户端"投递序号缺口 / 序号越序 / 暂存溢出"原因行的限流间隔（毫秒）。</summary>
        public int ClientDeliveryProblemLogIntervalMs { get; set; } = 10000;

        /// <summary>回传快照"编码水位"落后于该块已应用改动 → 丢弃并强制核对的限流日志间隔（毫秒）。</summary>
        public int ClientResyncSupersededLogIntervalMs { get; set; } = 10000;

        /// <summary>
        /// 客户端"同一块反复不一致"原因行的限流间隔（毫秒）：同一块累计不一致达到
        /// <see cref="ClientMismatchRepeatLogThreshold"/> 次时写一行（带区块键与次数），
        /// 之后按这个间隔再写 —— 用于"回传风暴"在日志里可定位，而不是只看到一个总数。
        /// </summary>
        public int ClientMismatchHotspotLogIntervalMs { get; set; } = 10000;

        /// <summary>同一块累计不一致多少次才写"反复不一致"原因行（低于它就是正常的一次回传后重核）。</summary>
        public int ClientMismatchRepeatLogThreshold { get; set; } = 3;

        // ===================================================================================
        // 客户端"同一块反复不一致"的有界重试（回传风暴闸门）
        //
        // 场景（实机 B 端）：客户端报摘要 → 主机 Mismatch → 回传 → 客户端落地后再报 → 又 Mismatch，
        // 1:1 循环 45 次/秒。重试必须有上界：过期快照被丢弃后按指数退避重核；
        // 连续重试超过上限就**停止自动重报**并写一行限流 Warning，绝不无界循环。
        // ===================================================================================

        /// <summary>同一块"不一致 → 重新核对"的首次退避（毫秒）；之后每次翻倍（封顶 <see cref="ClientMismatchRetryMaxMs"/>）。</summary>
        public int ClientMismatchRetryBaseMs { get; set; } = 100;

        /// <summary>退避上限（毫秒）。</summary>
        public int ClientMismatchRetryMaxMs { get; set; } = 5000;

        /// <summary>
        /// 同一块**连续**自动重核的次数上限（一次 Match 会清零）。超过后停止自动重报并写限流 Warning
        /// （见 <see cref="TerrainSyncClient.MismatchRetryExhausted"/>）—— 这是"绝不无界 1:1 循环"的硬闸门。
        /// </summary>
        public int ClientMismatchRetryCap { get; set; } = 8;

        /// <summary>超过重试上限的 Warning 限流间隔（毫秒）。</summary>
        public int ClientMismatchRetryWarnIntervalMs { get; set; } = 10000;

        /// <summary>回传分片因"本端内存里有这块、内容还没定稿"而整体延后的重试间隔（毫秒；保持分片顺序、不丢）。</summary>
        public int ClientResyncNotReadyRetryMs { get; set; } = 500;

        /// <summary>
        /// (重)连后"重新排队全部常驻区块摘要核对"结果行的限流间隔（毫秒）。
        /// 正常情况下一次重连恰好一行；同一连接上世界重复激活时也不会刷屏。
        /// </summary>
        public int ResidentRequeueLogIntervalMs { get; set; } = 10000;

        // ===================================================================================
        // 陈旧裁决（红框）自愈：主机已经把内容补给这条身份 / 客户端本地早已一致，但裁决停在 Mismatch
        //
        // 实机症状（2026-10-18）：区块 (30,-4)/(30,-3) 一直是红。背压漏掉的那条 CellChange 后来由
        // RepairCellChangesLocked 按历史补发（客户端内容因此与主机一致），而客户端本地校验器是 Fresh、
        // 不会自发重报（它“已经重新核对过、什么都没变”）⇒ 主机那条 Mismatch 永远没人重判。
        // 这里给出**有界**的出口：内容补齐 / 刚判 Mismatch 时登记一次“需要重新裁决”，
        // 退避后主动要一份新鲜摘要（单块 VerifyChunks）→ 到了就重判（Match → ReVerified 蓝）；
        // 要过摘要、过了清除上限仍没有新鲜裁决 → 明确清掉这条陈旧的红（计数 + 一行日志，绝不静默）。
        // ===================================================================================

        /// <summary>同一块“陈旧红”最多主动要几次新鲜摘要（一条单块 VerifyChunks 算一次）。</summary>
        public int StaleVerdictRejudgeMaxRequests { get; set; } = 2;

        /// <summary>
        /// 两次主动要摘要之间的退避（毫秒）。也给“客户端本来就会自发重报”的正常收敛路径留宽限：
        /// 正常 Mismatch → 回传落地 → 客户端再报 → Match 通常 1~2s 内完成，不会真的发请求。
        /// </summary>
        public int StaleVerdictRejudgeRetryMs { get; set; } = 3000;

        /// <summary>
        /// 从**最后一次真的发出去的请求**到**清除**陈旧红的上限（毫秒）。必须至少发出过一次请求；
        /// 过了上限仍然没有新鲜裁决 → 清掉这条红（客户端可能已经不持有这块 / 不再上报）。
        /// 从最后一次请求算起（而不是首次登记）：背压很久之后才补发的请求同样要给客户端一个完整的应答窗口。
        /// </summary>
        public int StaleVerdictClearMs { get; set; } = 12000;

        /// <summary>每 tick 最多为几个区块要新鲜摘要（合并成一条 VerifyChunks，有界）。</summary>
        public int StaleVerdictRequestsPerTick { get; set; } = 2;

        /// <summary>陈旧裁决日志（同一块请求 / 重判）的限流间隔（毫秒）。</summary>
        public int StaleVerdictLogIntervalMs { get; set; } = 10000;
    }

    /// <summary>
    /// 主机侧「同步/加载范围」的唯一算法：由客户端声明的兴趣半径算出引擎地形更新位置的两个距离
    /// （可见范围 + 内容距离），也就是主机为这个客户端生成 / 保留地形的范围。
    ///
    /// 用户口径："同步范围根据客户端的可见范围设置"。客户端的半径本来就是
    /// <c>max(1, ceil(VisibilityRange / 16))</c>（见 <see cref="RadiusForVisibility"/> 与
    /// <see cref="TerrainSyncClient"/> 的上报），主机这边只做夹紧 + 换算成世界单位：
    ///   · 可见范围 = radius * 16f（旧实现的 remoteVisibility）；
    ///   · 内容距离 = **同一个值**（旧实现把内容距离硬编码成 64f，比 radius * 16 小：半径 8 时是 64 vs 128，
    ///     主机的"生成 / 保留内容"范围因此比客户端声明的可见范围小）。
    ///
    /// 放在这里（而不是引擎侧）是为了让"范围怎么算"只有一个真相：主机侧的
    /// <see cref="SuMultiplayer.Engine.NetworkAvatarSpawner.ApplyTerrainUpdateLocations"/> 与
    /// 不依赖引擎的离线自检跑的是同一份算法。
    ///
    /// Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.SetUpdateLocation
    ///         （签名 = (locationIndex, center, visibilityDistance, contentDistance)）
    /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:509-512
    ///         （旧实现 SetUpdateLocation(..., remoteVisibility, 64f)）
    /// </summary>
    public static class TerrainSyncRange
    {
        /// <summary>半径上限（区块）：与 <see cref="TerrainSyncOptions.MaxRadius"/> / 引擎侧上限同一口径。</summary>
        public const int MaxRadius = 32;

        /// <summary>半径夹紧：1..<see cref="MaxRadius"/>（0 / 负数 / 超大值都不会变出非法范围）。</summary>
        public static int ClampRadius(int radius) => Math.Max(1, Math.Min(MaxRadius, radius));

        /// <summary>
        /// 可见距离（米）→ 兴趣半径（区块）：**全工程唯一实现** = <c>max(1, ceil(visibility / 16))</c>，
        /// 再夹到 1..<see cref="MaxRadius"/>。
        ///
        /// 客户端上报（旧 :486-487 <c>clientRadius</c>）与主机默认半径（旧 :499 <c>defaultRadius</c>）
        /// 必须是同一个口径，否则"客户端看得见的环"会落在主机窗口之外、长期停在 s=1（黄）。
        /// 客户端的 <c>SubsystemSky.VisibilityRange</c> 是米（默认 128 = 8 区块），所以 128 → 8。
        /// 非有限值不做隐式猜测：NaN / 负数按 1 米处理（半径 1），+∞ 直接取上限。
        /// </summary>
        public static int RadiusForVisibility(float visibility)
        {
            if (float.IsNaN(visibility)) visibility = 1f;
            if (float.IsPositiveInfinity(visibility)) return MaxRadius;
            if (visibility < 1f) visibility = 1f;
            int radius = (int)Math.Ceiling(visibility / 16f);
            return Math.Max(1, Math.Min(MaxRadius, radius));
        }

        /// <summary>
        /// 由半径得出（可见范围, 内容距离）：两者相等 = 同步范围完全跟随客户端声明的可见范围。
        /// 引擎侧 <c>TerrainUpdater.SetUpdateLocation</c> 内部还会做 <c>contentDistance =
        /// max(contentDistance, visibilityDistance)</c>（Survivalcraft/Game/TerrainUpdater.cs:258），
        /// 这里显式传相等值，两边同一口径。
        /// </summary>
        public static void Distances(int radius, out float visibilityDistance, out float contentDistance)
        {
            visibilityDistance = ClampRadius(radius) * 16f;
            contentDistance = visibilityDistance;
        }
    }

    public sealed class ClientView
    {
        /// <summary>
        /// 该身份**当前**的传输句柄（clientId）。clientId 只是"这一次连接"的编号：
        /// 对端断线重连拿到的编号会变，身份不会。所以**不要**用它归档任何状态
        /// （状态一律挂在 <see cref="PlayerSessionRecord"/> 上，见 <see cref="StableKey"/>）。
        /// </summary>
        public int Id;

        /// <summary>
        /// 身份的**稳定键**（实时地图 / 日志用它，见 <see cref="PlayerSessionKeys.StableKeyOf"/>）。
        /// 重连后 clientId 变了，这个键不变；同一身份只会出现一条记录（`TerrainSyncHost.GetSnapshot` 去重）。
        /// 它不是 UserId 全值（身份是 GUID，全值属私有信息，禁止上线 / 进日志）。
        /// </summary>
        public string StableKey = string.Empty;

        /// <summary>该身份记录当前指向的传输句柄（= <see cref="Id"/>；-1 = 没有在线连接）。</summary>
        public int CurrentClientId = -1;

        public string Name = string.Empty;
        public ClientKind Kind = ClientKind.Player;
        public ChunkKey Center;
        public int Radius;
        public int WindowChunks;
        public int Tracked;
        public int PendingParts;
        public long Match;
        public long Mismatch;
        public long NotReady;
        public long ResyncPartsSent;
        /// <summary>同一块的 Mismatch 已有一份回传快照在队列里 → 合并（客户端重报 / 迟到裁决）的次数。</summary>
        public long ResyncCoalesced;
        /// <summary>裁决后、编码前主机丢了该块内容 → 不发零格快照、改回 NotReady 的次数。</summary>
        public long ResyncEmptySkipped;
        public long CellChangesSent;
        public ChannelState State;

        /// <summary>
        /// 主机手上"欠着"的上报条数（客户端报过、主机还没有内容可比的块）。
        /// 这些块迟早要收到一条带着**真实主机摘要**的 <c>ChunkVerdict</c>（见 TerrainSyncHost.Tick）。
        /// </summary>
        public int PendingReports;

        /// <summary>
        /// 该**身份**上报过摘要（<c>ChunkDigestReport</c>）的区块 —— 也就是"它已经加载完的区块"。
        /// 这是**会话累计**（只增不减，卸载不上报）：仅用于"该身份是否上报过这块"的裁决过滤
        /// （<see cref="TerrainSyncHost.VerifyStateOf(string, ChunkKey)"/>），**不再是实时地图客户端区域的来源**。
        /// **按身份归档**：断线重连（clientId 变了、身份没变）继续用同一份集合，不会从 0 重来。
        /// </summary>
        public readonly List<ChunkKey> ReportedLoaded = new List<ChunkKey>();

        /// <summary>
        /// 该身份**当前存在于客户端内存**（引擎已分配）的区块集合（v8 实时地图客户端线框的唯一来源）。
        /// 由客户端 <c>ClientChunks</c> 消息维护：整集收齐 = 整体替换；增量 = 逐条 add / remove。
        /// 与 <see cref="ReportedLoaded"/> 的区别：后者只增不减（会话累计），这一份会**真的删掉**
        /// 客户端释放的块（用户口径："已经脱离内存的格子就取消绘制"）。
        /// 内容还没定稿的海洋 / 水面块也在里面（客户端上报的是**分配**集合，不是常驻集合）。
        /// **按身份归档**：重连复用同一条记录，新连接发来的整集会整体替换它，不会留下旧条目。
        /// </summary>
        public readonly List<ChunkKey> MemoryChunks = new List<ChunkKey>();

        /// <summary>该身份收到的**整集**条数（收齐 = 一次整体替换；诊断 / 测试计数）。</summary>
        public long ClientChunkFullSets;

        /// <summary>该身份收到的增量消息条数（每条消息内可含多块；诊断 / 测试计数）。</summary>
        public long ClientChunkDeltaMessages;

        /// <summary>该身份发来的"当前内存区块集合"载荷中**被拒绝**的条数（格式 / 超上限 / 非当前句柄）。</summary>
        public long ClientChunkRejects;
    }


    /// <summary>
    /// 主机侧的**一条身份（UserId）会话记录** —— 校验 / 上报状态的唯一归属。
    ///
    /// 为什么要有它（实机故障）：平板链路断开后重连拿到**新的 clientId**，旧实现把
    /// "上报过的区块 / 裁决状态 / 待裁决"按 clientId 各存一份 ⇒ 主机手里有两份互不相干的状态，
    /// 实时地图的验证框停在旧连接的区域，`clients[].chunks` 也是旧路径。身份是同一个（UserId.dat），
    /// 所以状态必须按身份归档：clientId 只是**这一次连接的传输句柄**（<see cref="CurrentClientId"/>）。
    ///
    /// 生命周期（同一个房间 / 会话内）：
    ///   · 第一次收到该身份的 Hello 时懒创建；
    ///   · 同身份重连（新 clientId）**复用**本记录，只把 <see cref="CurrentClientId"/> 指向新句柄；
    ///   · 对端离开（Bye / 链路失效）**不清空**本记录（重连要继续），只把当前句柄置 -1，并去掉欠更新标记；
    ///   · 房间 / 会话结束（<see cref="TerrainSyncHost.ClearPendingMarks"/>）**整体清空**。
    /// </summary>
    internal sealed class PlayerSessionRecord
    {
        /// <summary>
        /// Hello 带上来的身份原值（**已 Trim**）。只当字典键与内部判断用；
        /// **绝不写日志、绝不上实时地图** —— 日志/页面一律用 <see cref="StableKey"/> 或
        /// <see cref="PlayerSessionKeys.ShortIdentity"/>。
        /// </summary>
        public string Identity = string.Empty;

        /// <summary>身份的稳定键（见 <see cref="PlayerSessionKeys"/>）：可写日志、可上实时地图。</summary>
        public string StableKey = string.Empty;

        /// <summary>该身份最近一次 Hello 声明的显示名（重连可能改名，以最新一次为准）。</summary>
        public string DisplayName = string.Empty;

        /// <summary>该身份**当前**的传输句柄（-1 = 现在没有在线连接；重连后指向新 clientId）。</summary>
        public int CurrentClientId = -1;

        /// <summary>该身份最近一次上报的兴趣半径（区块；日志里的"该玩家端兴趣半径"用它）。</summary>
        public int Radius;

        /// <summary>
        /// 该身份**上报过摘要**（<c>ChunkDigestReport</c>）的区块 = 它加载完的区块。
        /// 实时地图的"客户端已加载区域"与 <see cref="TerrainSyncHost.VerifyStateOf"/> 都只认这一份；
        /// 只增不减（卸载不上报），随身份跨重连保留。
        /// </summary>
        public readonly HashSet<ChunkKey> ReportedLoaded = new HashSet<ChunkKey>();

        /// <summary>
        /// 该身份**当前存在于客户端内存**（引擎已分配）的区块集合 —— v8 实时地图客户端线框的
        /// 唯一来源，同时也是**逐区块欠更新计数**的唯一判定依据：改动时不含这一块的身份才记上。
        /// 客户端上报的是**分配**集合（不是常驻集合）：海洋 / 水面等"内容还没定稿但已占内存"的块
        /// 也在里面，所以它们不会被误标成欠更新。
        /// 客户端 <c>ClientChunks</c> 消息维护：整集收齐 → 整体替换；增量 → 逐条 add / remove。
        /// 与 <see cref="ReportedLoaded"/>（会话累计、只增不减）不同，这一份会真的删掉释放的块。
        /// **按身份归档**：重连复用同一条记录（旧集合保留到新连接发来整集为止，随后被整体替换）。
        /// </summary>
        public readonly HashSet<ChunkKey> MemoryChunks = new HashSet<ChunkKey>();

        /// <summary>收齐的整集次数（诊断 / 测试计数）。</summary>
        public long ClientChunkFullSets;

        /// <summary>应用过的增量消息条数（诊断 / 测试计数）。</summary>
        public long ClientChunkDeltaMessages;

        /// <summary>被拒绝的"当前内存区块集合"载荷条数（格式 / 超上限 / 非当前句柄）。</summary>
        public long ClientChunkRejects;

        // ---- 整集分片的拼装状态（**只在内存里**；同一身份同时只拼一个整集）----
        /// <summary>正在拼的整集序号（-1 = 空闲）。</summary>
        public int ClientChunkFullSetId = -1;
        /// <summary>正在拼的整集声明的分片数。</summary>
        public int ClientChunkFullExpectedParts;
        /// <summary>已经收到的分片下标。</summary>
        public readonly HashSet<int> ClientChunkFullParts = new HashSet<int>();
        /// <summary>已经收到的整集条目（收齐全部分片后才整体替换 <see cref="MemoryChunks"/>）。</summary>
        public readonly HashSet<ChunkKey> ClientChunkFullBuffer = new HashSet<ChunkKey>();

        /// <summary>
        /// 逐 chunk 的校验状态（实时地图着色用）：**键 = 该身份上报过摘要的块**。
        /// <see cref="ChunkVerifyState.Requested"/>（黄）只表示"这个身份确实上报过、主机还没裁决"；
        /// 客户端没上报的块查不到状态 = <see cref="ChunkVerifyState.None"/> = 不画框。
        /// </summary>
        public readonly Dictionary<ChunkKey, byte> Verdicts = new Dictionary<ChunkKey, byte>();

        /// <summary>
        /// 主机**欠着裁决**的上报（客户端报过、主机当时没有内容可比）：key → 待办。
        /// 内容可用后必须用**真实主机摘要**作答（见 <see cref="TerrainSyncHost.Tick"/> 的轮转核对）。
        /// 与 <see cref="ReportedLoaded"/> 的区别：后者是"上报过的块"（只增不减，实时地图用），
        /// 这里是"还没真正回答的块"（回答后出队）。**跨重连保留**（重连后用真实摘要继续作答）。
        /// </summary>
        public readonly Dictionary<ChunkKey, PendingReport> Pending = new Dictionary<ChunkKey, PendingReport>();

        /// <summary>
        /// 待裁决块的轮转顺序：每 tick 从队首取若干条核对，没核完的放回队尾 ——
        /// 于是不会出现"总从同一头扫、后面的块永远排不上"。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:630-642
        ///         （旧实现把超时的待办重新入队，循环推进）
        /// </summary>
        public readonly Queue<ChunkKey> PendingScan = new Queue<ChunkKey>();

        /// <summary>
        /// **陈旧裁决自愈台账**：主机刚刚把内容补给了这条身份（补发 CellChange / 回传快照 / 追帧重放 /
        /// 整块快照），或刚对它判了 Mismatch，而这块当前仍是红（<see cref="ChunkVerifyState.Failed"/>）
        /// → 这条结论可能已经过期：客户端本地校验器可能是 Fresh（内容早已一致），它**不会自发重报**，
        /// 红框就永远留着（实机 (30,-4)/(30,-3)）。这里按区块登记，由
        /// <see cref="TerrainSyncHost.TickStaleVerdicts"/> 有界地主动要一份新鲜摘要。
        /// 与 <see cref="Verdicts"/> 同生命周期（会话结束整体清空）；跨重连保留（同一身份）。
        /// </summary>
        public readonly Dictionary<ChunkKey, StaleVerdictWatch> StaleVerdicts =
            new Dictionary<ChunkKey, StaleVerdictWatch>();

        /// <summary>等待重新裁决区块的轮转顺序（每 tick 从队首取若干条，没到点的放回队尾）。</summary>
        public readonly Queue<ChunkKey> StaleVerdictScan = new Queue<ChunkKey>();

        /// <summary>在册等待重新裁决的区块数。</summary>
        public int PendingStaleVerdicts => StaleVerdicts.Count;

        /// <summary>
        /// 主机欠着裁决的上报**条数**（= <see cref="Pending"/> 的条数）：状态行 / 实时地图的"待裁决"、
        /// 重连复用日志里的"沿用 N 条待裁决"都读它（与 <see cref="TerrainHostSnapshot.PendingReports"/>
        /// 同一口径，但这里按**身份**归档）。
        /// </summary>
        public int PendingReports => Pending.Count;

        /// <summary>是不是"退化路径"记录（Hello 没带身份 → 只能按 clientId 单独归档）。</summary>
        public bool Degraded => Identity.Length == 0;
    }

    /// <summary>
    /// 主机手上"欠着"的一条上报。
    /// **客户端上报的摘要必须留在这里**：块还没加载出来时没法比对，等它加载出来再比 ——
    /// 旧代码在这一点上把摘要直接丢了，于是那些块永远不会有裁决（203 黄框的根因）。
    /// </summary>
    internal sealed class PendingReport
    {
        public ChunkKey Key;
        /// <summary>客户端最近一次上报的摘要（客户端可重报，以最新一次为准）。</summary>
        public uint ClientHash;
        /// <summary>第一次登记的时刻（TickCount64 口径），用于"欠了多久"。</summary>
        public long FirstSeenMs;
        /// <summary>
        /// "主机一直没拿到这块内容"的明确原因只写一行（同一块不重复刷屏）。
        /// 加载重请的节奏 / 次数 / 超时**不在这里**：那是 <see cref="OnDemandChunkLoads"/> 的台账
        /// （同一块一条记录，指数退避 + 次数上限 + 总超时 + 冷却），这里只留"欠了多久"。
        /// </summary>
        public bool StalledLogged;
    }

    /// <summary>
    /// 一条“这块的红（Mismatch）可能已经过期”的台账（见 <see cref="PlayerSessionRecord.StaleVerdicts"/>）。
    /// 生命周期：登记（内容补给 / 刚判 Mismatch）→ 退避后主动要新鲜摘要（≤ 上限次）→
    /// 收到 Match 改判（结束，计数 refreshed）或过了清除上限（明确清掉红，结束，计数 cleared）。
    /// **绝不伪造 Match**：改判只来自客户端真实上报后主机的比对结果。
    /// </summary>
    internal sealed class StaleVerdictWatch
    {
        public ChunkKey Key;
        /// <summary>首次登记时刻（TickCount64）：只用于日志里的“首次登记到现在”。</summary>
        public long FirstSeenMs;
        /// <summary>下一次允许主动要摘要的时刻（退避；给正常收敛路径留宽限）。</summary>
        public long NextRequestMs;
        /// <summary>已经**真的发出去**的“要新鲜摘要”次数（背压失败会退回，不虚增）。</summary>
        public int Requests;
        /// <summary>上一次请求**真的发出去**的时刻：清除上限从这里算起 —— 背压很久之后补发的请求
        /// 必须同样给客户端一个完整的应答窗口，不能“刚要到就清掉”。</summary>
        public long LastRequestMs;
        /// <summary>触发登记的原因（最近一次；写进日志）。</summary>
        public string LastReason = string.Empty;
        /// <summary>上一次为这块写日志的时刻（限流，逐块）。</summary>
        public long LastLogMs;
    }

    public sealed class TerrainHostSnapshot
    {
        public List<ChunkKey> Interest = new List<ChunkKey>();
        public List<ChunkKey> Loaded = new List<ChunkKey>();
        public List<ClientView> Clients = new List<ClientView>();
        public long DigestReports;
        public long ResyncBytes;
        /// <summary>同一块已有回传快照时合并（客户端重报 / 迟到裁决）的次数。</summary>
        public long ResyncCoalesced;
        /// <summary>裁决后主机丢了该块内容、改回 NotReady 的次数。</summary>
        public long ResyncEmptySkipped;
        public long CellChangesBroadcast;
        /// <summary>
        /// 广播改动时因**显式背压 / 连接不可达**而没发出去的投递次数（可审计计数器）。
        /// 它**只记录投递本身**，不参与欠更新计数：那个数字只看各身份当前内存集合里有没有该区块。
        /// </summary>
        public long CellChangesDeliveryFailed;
        /// <summary>所有玩家端"欠着裁决"的上报条数之和（>0 = 还有报告没被真正回答）。</summary>
        public int PendingReports;
        /// <summary>
        /// 实时地图用：有 ≥1 个在线身份**当前内存集合里没有该区块**的区块数（= 网页要标注数字的标块数）。
        /// 只统计内存里的欠更新集合，与主机自己加载/卸载了哪些区块**无关**。
        /// </summary>
        public int PendingMarks;
        /// <summary>
        /// 实时地图用：所有区块"欠更新身份数"之和（每块有几个在线身份当前内存里没有这一块就加几）。
        /// 例：3 个区块各欠 1 个身份 → 本值为 3；1 个区块欠 2 个 → 也是 2。
        /// </summary>
        public int PendingClients;

        // ---- 按需单块加载（主机：客户端上报的边缘块，逐块临时加载；口径见 Terrain/OnDemandChunkLoads.cs）----
        /// <summary>真正交给引擎的单块加载请求**次数**（含重试）= onDemandLoadRequests。</summary>
        public long OnDemandLoadRequests;
        /// <summary>进入按需加载的区块数（重试不重复计）。</summary>
        public long OnDemandLoadChunks;
        /// <summary>内容到位、已经可以裁决的区块数 = onDemandLoadServed。</summary>
        public long OnDemandLoadServed;
        /// <summary>超时 / 引擎拒绝后**明确放弃**的区块数 = onDemandLoadTimeouts。</summary>
        public long OnDemandLoadTimeouts;
        /// <summary>被拒绝的请求次数 = onDemandLoadRejected（= 下面三项之和）。</summary>
        public long OnDemandLoadRejected;
        /// <summary>拒绝原因：排队台账在册上限已满（真正的容量拒绝；不是并发满）。</summary>
        public long OnDemandLoadRejectedCap;
        /// <summary>拒绝原因：上一轮明确放弃后的冷却未过。</summary>
        public long OnDemandLoadRejectedCooldown;
        /// <summary>拒绝原因：引擎明确不接受（世界未加载 / 没有 TerrainUpdater / 临时位置满）。</summary>
        public long OnDemandLoadRejectedEngine;
        /// <summary>内容到位后按需撤掉的临时加载次数（服务完成 / 不再需要）。</summary>
        public long OnDemandLoadReleased;
        /// <summary>当前在册的按需加载块数 = pendingOnDemand。</summary>
        public int PendingOnDemand;
        /// <summary>当前在台账里排队（还没拿到引擎临时位置）的块数 = onDemandQueued。</summary>
        public int OnDemandQueued;
        /// <summary>当前活动的引擎单块加载请求数（已拿到临时位置、内容还没到）= onDemandActive。</summary>
        public int OnDemandActive;
        /// <summary>当前已服务、还在保留窗口内的临时位置数 = onDemandHeld。</summary>
        public int OnDemandHeld;
        /// <summary>为排队有压力而提前让出的"仍被客户端需要"的保留位置次数 = onDemandHeldReleasedUnderPressure。</summary>
        public long OnDemandHeldReleasedUnderPressure;
        /// <summary>台账接受但当时没有引擎槽位、进入排队等待的累计次数 = onDemandDeferred。</summary>
        public long OnDemandDeferred;
        /// <summary>
        /// 因为"主机明确不为这块发起加载"（冷却 / 上限 / 引擎拒绝）而**没有**登记成待裁决的上报条数。
        /// 这些上报已经用 NotReady 回答过（客户端会按退避重问），不会变成永远挂着、永远裁决不了的黄框。
        /// </summary>
        public long OnDemandReportsDropped;
        /// <summary>因为按需加载明确放弃（超时 / 引擎拒绝）而从待裁决里摘掉的上报条数。</summary>
        public long OnDemandReportsTimedOut;

        // ---- 客户端"内存区块集合"（v8 实时地图；= 引擎已分配的区块）的显式计数 ----
        /// <summary>在线身份内存（引擎已分配）区块条目总数（= 各身份 <see cref="PlayerSessionRecord.MemoryChunks"/> 之和）。</summary>
        public int MemoryChunkCount;
        /// <summary>收齐的整集次数（所有身份累计）。</summary>
        public long ClientChunkFullSets;
        /// <summary>应用过的增量消息条数（所有身份累计）。</summary>
        public long ClientChunkDeltaMessages;
        /// <summary>被拒绝的载荷条数（超上限 / 格式错误 / 非当前句柄；超限详情见限流日志）。</summary>
        public long ClientChunkRejects;

        // ---- 断线重连追帧（同会话）----
        /// <summary>主机改动序号（= 已广播的权威改动数；世界清单的导出基线 / 追帧范围都用它）。</summary>
        public long ChangeSequence;
        /// <summary>当前仍在内存里的改动历史条数（有界）。</summary>
        public int ChangeJournalCount;
        /// <summary>历史里最旧一条的序号（0 = 没有历史）。</summary>
        public long ChangeJournalOldestSequence;
        /// <summary>因为超过时间 / 条数上限被裁掉的改动条数（可审计）。</summary>
        public long ChangeJournalEvictions;
        /// <summary>接受的重连次数（会话一致 + 序号仍在历史内）。</summary>
        public long RejoinAccepted;
        /// <summary>拒绝的重连次数（缺会话 / 会话不一致 / 序号越界）。</summary>
        public long RejoinRejected;
        /// <summary>因历史已裁剪而拒绝追帧、要求整图重下的次数。</summary>
        public long CatchUpRejected;
        /// <summary>已开始的追帧轮数。</summary>
        public long CatchUpRounds;
        /// <summary>已发出的追帧改动条数。</summary>
        public long CatchUpChangesSent;
        /// <summary>正在发送 / 等待 Applied 的追帧任务数。</summary>
        public int CatchUpActive;
        /// <summary>追帧数据因显式背压没发出去、下一 tick 重试的次数（不丢、不假装发完）。</summary>
        public long CatchUpSendDeferrals;
        /// <summary>追帧已发 Barrier 但等不到客户端 Applied 的次数（明确放弃这一轮）。</summary>
        public long CatchUpAckTimeouts;

        // ---- 改动投递（内存集合 ∪ 兴趣窗口；用户口径："内存里有这块就必须收到改动"）----
        /// <summary>成功投递给"当前内存集合包含该块"的身份的次数（新规则的核心计数）。</summary>
        public long CellChangesMemoryRecipients;
        /// <summary>其中**只**因为内存集合才投递（不在该连接兴趣窗口内）的次数 —— 本轮修复的那类窗口。</summary>
        public long CellChangesMemoryOnly;
        /// <summary>成功投递时该块**不在**接收方兴趣窗口内的次数（窗口外内存块的投递证据）。</summary>
        public long CellChangesOutsideWindow;
        /// <summary>因为追帧在途 / 有缺失未补而**推迟**投递的改动次数（不是丢：随后按历史顺序补发）。</summary>
        public long CellChangeDeferrals;
        /// <summary>按历史顺序补发成功的改动条数（背压 / 追帧期间缺失的那些）。</summary>
        public long CellChangeRepairs;
        /// <summary>补发时又遇到背压、留到下一 tick 的次数（不丢、顺序不变）。</summary>
        public long CellChangeRepairDeferrals;
        /// <summary>缺口已被有界历史裁掉、无法序列化补齐的连接次数（明确警告，不假装同步）。</summary>
        public long CellChangeRepairUnavailable;
        /// <summary>身份刚开始持有某块（欠账被清除）时排队的整块快照数。</summary>
        public long OwedSnapshotsQueued;
        /// <summary>同上但主机没有该块内容 → 无法补发（由客户端加载时摘要核对兜住）。</summary>
        public long OwedSnapshotsSkippedNoContent;
        /// <summary>主机要求客户端"重新核对内存区块"的请求数（无法按序号补齐缺口时）。</summary>
        public long VerifyRequestsSent;
        /// <summary>上述请求没发出去的次数（显式背压 / 不可达）。</summary>
        public long VerifyRequestSendFailures;

        // ---- 陈旧裁决自愈（红框在内容补齐后必须被重新裁决；口径见 TerrainSyncOptions.StaleVerdict*）----
        /// <summary>已发出的“这块需要重新裁决、请给一份新鲜摘要”的区块请求数（按区块计）。</summary>
        public long StaleVerdictRefreshRequested;
        /// <summary>请求之后真的拿到新鲜裁决、并改判成 Match / ReVerified 的区块数。</summary>
        public long StaleVerdictRefreshed;
        /// <summary>要过摘要、过了清除上限仍没有新鲜裁决 → 明确清掉的陈旧红区块数。</summary>
        public long StaleVerdictCleared;
        /// <summary>请求没发出去（显式背压 / 连接不可达）的次数 —— 请求次数会退回，绝不假装要过。</summary>
        public long StaleVerdictRefreshDeferred;
        /// <summary>当前在册等待重新裁决的 (身份, 区块) 条数（实时地图 / 状态行用）。</summary>
        public int PendingStaleVerdicts;
    }

    public sealed class TerrainClientSnapshot
    {
        public ChunkKey Center;
        public int Radius;
        public List<ChunkKey> Loaded = new List<ChunkKey>();
        public List<ChunkKey> Fresh = new List<ChunkKey>();
        public List<ChunkKey> Stale = new List<ChunkKey>();
        public List<ChunkKey> Unverified = new List<ChunkKey>();
        public long ReportsSent;
        public long Match;
        public long Mismatch;
        public long ResyncApplied;
        public long ResyncVerified;
        public long CellChangesApplied;
        public long Retries;
        /// <summary>等待主机加载完成期间的重报次数（退避后重报，见 TerrainSyncClient.Tick）。</summary>
        public long ReReports;
        /// <summary>重报次数用尽仍没等到裁决的块数（ChunkLoadVerifier 明确计数，不静默硬撑）。</summary>
        public long Abandoned;
        public long LastVerifyMs;
        public long MaxVerifyMs;
        public ChannelState State;
        public long BytesReceived;

        // ---- 每帧预算 / 队列（本轮改动：状态行里的"队列 K · 本轮上报 N"就是这几个数）----

        /// <summary>还没算摘要的块数（已加载、已入队）。>0 表示预算正在延迟它们，但一块都不会丢。</summary>
        public int QueuedDigests;
        /// <summary>还没落地的回传分片数（已入队；落地受每帧预算限制）。</summary>
        public int QueuedResyncParts;
        /// <summary>加载后被改动、等重算摘要的块数（只影响裁决解释，不影响上报）。</summary>
        public int QueuedHashRefreshes;
        /// <summary>上一帧**实际算出**的摘要块数（加载摘要 + 改动刷新 + 回传末片重算）。</summary>
        public int DigestsThisTick;
        /// <summary>上一帧**实际发出去**的上报条数。</summary>
        public int ReportsThisTick;
        /// <summary>上一帧实际落地的回传分片数。</summary>
        public int ResyncAppliedThisTick;
        public int DigestBudgetPerTick;
        public int ResyncBudgetPerTick;
        public int HashRefreshBudgetPerTick;
        public int ReportBudgetPerTick;
        /// <summary>累计算出的摘要块数（含改动刷新与回传末片重算）。</summary>
        public long DigestsComputed;
        /// <summary>累计发出去的上报**条数**（不是消息数）。</summary>
        public long ReportEntriesSent;
        /// <summary>摘要还没算块就卸载了 → 作废的块数（明确原因：那块内容已经不在了）。</summary>
        public long DigestsDroppedUnloaded;
        /// <summary>摘要算完、还没发出去块就卸载了 → 作废的上报条数（同上原因）。</summary>
        public long ReportsDroppedUnloaded;
        /// <summary>改动后的摘要刷新因卸载作废的块数。</summary>
        public long HashRefreshesDroppedUnloaded;
        /// <summary>排队中的回传分片因"块在排队期间卸载过"作废的分片数（绝不落到另一份内容上）。</summary>
        public long ResyncDroppedUnloaded;
        /// <summary>回传载荷解码失败的分片数（协议异常，明确计数，不静默）。</summary>
        public long ResyncDecodeFailures;
        /// <summary>发送队列满导致上报延后的次数（条目留在队列里，不丢）。</summary>
        public long ReportSendDeferrals;
        /// <summary>上报到发送时内容还没定稿（内存里有这块）→ 退回未核对并排队强制核对的条数（不丢、不报旧状态）。</summary>
        public long ReportsDeferredNotReady;

        // ---- 客户端"内存区块集合"（v8 实时地图；= 引擎已分配的区块）----
        /// <summary>客户端**内存**（引擎已分配）的区块数（与本端 <see cref="TerrainSyncClient"/> 跟踪的集合同步）。</summary>
        public int MemoryChunkCount;
        /// <summary>已经发完的整集次数（每次 (重)连握手后一份）。</summary>
        public long ClientChunkFullSetsSent;
        /// <summary>已发出的"当前内存区块集合"消息条数（整集分片 + 增量）。</summary>
        public long ClientChunkMessagesSent;
        /// <summary>已发出的条目总数（整集 + 增量，去重后）。</summary>
        public long ClientChunkEntriesSent;
        /// <summary>其中增量消息条数。</summary>
        public long ClientChunkDeltaMessagesSent;
        /// <summary>还没发出的消息条数（整集剩余分片 + 至少 1 条增量批次）。</summary>
        public int QueuedClientChunkMessages;
        /// <summary>上一帧实际发出的消息条数。</summary>
        public int ClientChunkMessagesThisTick;
        /// <summary>每帧消息预算（Options.ClientChunkMessagesPerTick）。</summary>
        public int ClientChunkBudgetPerTick;
        /// <summary>发送失败（显式背压 / 连接不可用）导致延后的次数（消息留在队列里，不丢）。</summary>
        public long ClientChunkSendDeferrals;

        // ---- 断线重连追帧（客户端）----
        /// <summary>本端上次应用到的改动序号（重连时随 Hello 发回）。</summary>
        public long LastAppliedChangeSequence;
        /// <summary>本轮追帧目标水位（-1 = 没有开始）。</summary>
        public long CatchUpTargetSequence;
        /// <summary>本轮追帧实际落地的改动条数。</summary>
        public long CatchUpAppliedChanges;
        /// <summary>本轮追帧收到的重放消息条数。</summary>
        public long CatchUpMessages;
        /// <summary>追帧是否进行中 / 已完成。</summary>
        public bool CatchUpActive;
        public bool CatchUpCompleted;
        /// <summary>主机 CellChange 不带序号的批次数（旧构建；本端无法精确追踪水位）。</summary>
        public long CellChangeBatchesWithoutSequence;
        /// <summary>CellChange 序号尾字段非法的批次数（协议异常，明确计数）。</summary>
        public long CellChangeSequenceMalformed;

        // ---- 有序应用 / 缺口检测（本轮：TCP 也要能**证明**中间没缺）----
        /// <summary>收到的最后一个投递序号（主机每条连接从 0 开始，成功一帧 +1）。</summary>
        public long LastDeliveryIndex;
        /// <summary>投递序号缺口次数（prev ≠ 上一条 → 明确警告 + 内存块全部强制核对）。</summary>
        public long DeliveryGaps;
        /// <summary>实时改动序号越序（seq ≤ 已应用水位）而**没有落地**的次数（不把旧值盖回去）。</summary>
        public long OutOfOrderChanges;
        /// <summary>追帧期间暂存（等重放结束再应用）的实时改动条数（当前值）。</summary>
        public int HeldLiveChanges;
        /// <summary>追帧期间暂存队列溢出 / 追帧卡住而**明确丢弃**的暂存条数（随后强制核对）。</summary>
        public long HeldLiveChangesDropped;
        /// <summary>收到的改动因为"本端内存里有这块、但内容还没定稿"而没落地（排队强制核对）的条数。</summary>
        public long CellChangesDroppedNotReady;
        /// <summary>收到的改动因为"本端不持有这块"而跳过的条数（重放里常见的正常情形）。</summary>
        public long CellChangesSkippedNotHeld;

        // ---- 内存区块（窗口外）的强制核对 ----
        /// <summary>已经发出的强制核对条数（窗口外内存块收到改动 / 缺口 / 越序 / 快照过期）。</summary>
        public long MemoryVerificationsSent;
        /// <summary>强制核对时块还没有内容、继续排队的次数。</summary>
        public long MemoryVerificationsDeferred;
        /// <summary>强制核对排队期间块被释放 → 作废的块数。</summary>
        public long MemoryVerificationsDroppedUnloaded;
        /// <summary>已有一条上报在途 / 已确认不一致 → 本次不重复发起的块数。</summary>
        public long MemoryVerificationsSkipped;
        /// <summary>还没处理的强制核对块数（有界、FIFO）。</summary>
        public int QueuedMemoryVerifications;
        /// <summary>上一帧实际发出的强制核对条数。</summary>
        public int MemoryVerificationsThisTick;
        /// <summary>回传快照"编码水位"比该块已应用改动旧 → 丢弃（避免旧值盖新值）的分片数。</summary>
        public long ResyncDroppedSuperseded;
        /// <summary>同一计数（任务口径命名）：resyncDiscardedSuperseded。</summary>
        public long ResyncDiscardedSuperseded;
        /// <summary>丢弃过期快照后，按退避**成功重新上报**（= 向主机请求新快照）的块次数。</summary>
        public long ResyncRefreshedAfterDiscard;
        /// <summary>"本端内存里有这块、内容还没定稿"导致回传分片整体延后的次数（不丢、保持顺序）。</summary>
        public long ResyncDeferredNotReady;
        /// <summary>收到的零格回传快照数（主机编码时已无内容；不落地、不重报）。</summary>
        public long ResyncEmptySnapshots;
        /// <summary>连续不一致重试超过上限、进入"停止自动重报"状态的块次数。</summary>
        public long MismatchRetryExhausted;
        /// <summary>重试预算用尽后被**抑制**的自动重报次数（明确计数，不静默）。</summary>
        public long MismatchRetrySuppressedVerifications;
        /// <summary>当前仍在重试预算内的块数。</summary>
        public int MismatchRetryChunks;
        /// <summary>当前最大的单块连续重试次数（0 = 没有重试中的块）。</summary>
        public int MaxMismatchRetryCount;
        /// <summary>曾经不一致、后来核对一致的块数（收敛计数；与"反复不一致"对照）。</summary>
        public long ConvergedChunks;
        /// <summary>丢弃后重新核对因"内容还没定稿"延后的次数。</summary>
        public long DiscardRefreshDeferred;
        /// <summary>丢弃后重新核对被跳过（已 Fresh / 在途 / 已释放 / 已抑制）的次数。</summary>
        public long DiscardRefreshSkipped;
        /// <summary>发送时用"当下本地摘要"替换排队时旧摘要的上报条数（避免旧摘要换来无谓回传）。</summary>
        public long ReportHashesRefreshed;
        /// <summary>NotReady 退避到点但块内容还没定稿 → 继续延后的次数（不报 0 摘要）。</summary>
        public long NotReadyRetriesDeferred;
        /// <summary>收到主机"重新核对内存区块"请求的次数。</summary>
        public long VerifyRequestsReceived;
        /// <summary>这些请求里被排进强制核对的块数。</summary>
        public long VerifyRequestChunks;
        /// <summary>非法的"重新核对"载荷数（协议异常，明确计数）。</summary>
        public long VerifyRequestMalformed;

        // ---- (重)连后重新排队常驻区块的摘要核对 ----
        /// <summary>已经执行过几次"重新排队全部常驻区块"（每次 (重)连 / 世界激活一次；不会自循环）。</summary>
        public long ResidentRequeues;
        /// <summary>累计重新排队的区块数（每次 = 当时内容可用的常驻块里、确实需要核对的那部分）。</summary>
        public long ResidentRequeuedChunks;
        /// <summary>最近一次重新排队了多少块（正常应 == 上次报的常驻块数，去掉已 Fresh / 在途 / 内容不可用的）。</summary>
        public int LastResidentRequeueCount;
        /// <summary>最近一次重新排队跳过的块数（已 Fresh / 正在校验 / 重试用尽 / 内容不可用 / 已在队列）。</summary>
        public long ResidentRequeueSkipped;
        /// <summary>本次"重新排队"还在等追帧完成 / 被推迟的帧数（可审计：不是静默不发）。</summary>
        public long ResidentRequeueDeferred;
        /// <summary>最近一次重新排队的明确原因（"重连 + 追帧完成" / "世界已激活" …）。</summary>
        public string LastResidentRequeueReason = string.Empty;

        // ---- 现场计数（每秒）：回传风暴的可定位证据（不参与任何判定） ----
        /// <summary>最近 1 秒发出的摘要上报条数（≈ 每秒报告数）。</summary>
        public int ReportsPerSecond;
        /// <summary>观测到的每秒上报条数峰值（重连后是否出现爆发，一眼可见）。</summary>
        public int ReportsPerSecondPeak;
        /// <summary>最近 1 秒收到的 Match 裁决数。</summary>
        public int MatchPerSecond;
        /// <summary>最近 1 秒收到的 Mismatch 裁决数。</summary>
        public int MismatchPerSecond;
        /// <summary>最近 1 秒收到的回传分片数（主机"回传"的内容到达速率）。</summary>
        public int ResyncReceivedPerSecond;
        /// <summary>最近 1 秒落地的回传分片数（客户端实际应用速率，受每帧预算限制）。</summary>
        public int ResyncAppliedPerSecond;
        /// <summary>出现过的"不一致"区块数（去重；不含已卸载清掉的）。</summary>
        public int MismatchChunkCount;
        /// <summary>同一块累计不一致 ≥ 阈值的区块数（"反复不一致"——回传风暴的典型特征）。</summary>
        public int MismatchRepeatChunkCount;
        /// <summary>回传落地后重报被拒 / 重试用尽导致"没有发起核对"的次数（不静默，见 VerifyNotStarted 原因行）。</summary>
        public long VerifyNotStarted;
        /// <summary>世界整体被替换（整图重下 / Project 换新）时清掉的未完成核对工作条数（旧世界的验证状态对
        /// 新世界无效，必须清掉并重新排队；这里记账，不静默丢弃）。</summary>
        public long WorldReplaceClearedWork;
    }

    /// <summary>
    /// 主机侧地形同步：每客户端兴趣窗口（正表 + 反表）、摘要裁决、按订阅者广播改动。
    /// 所有发送都是显式的：队列满 = 背压事件，**不丢包**。
    /// 演示页标记：NET03。
    /// </summary>
    public sealed class TerrainSyncHost
    {
        /// <summary>加入审批中的客户端断开（Bye / 链路失效）：主机请求队列应清掉它的待决条目。</summary>
        public event Action<int> PendingApprovalAborted;

        /// <summary>Hello 里"我已有角色记录 hash"的条数上限（4 本机角色 × 多端，余量充足）。</summary>
        private const int MaxKnownRecordHashes = 64;

        /// <summary>
        /// **一条连接**（transport handle）的状态。clientId 只是传输编号：
        /// 这里**不放任何**"上报过的块 / 裁决状态 / 待裁决"——那些全部挂在
        /// <see cref="Session"/> 指向的身份记录上（见 <see cref="PlayerSessionRecord"/>）。
        /// 这里只放"这一次连接"自己的东西：窗口订阅、待发分片、记账计数、重连标记。
        /// </summary>
        private sealed class State
        {
            public int Id;
            public string Name = string.Empty;
            /// <summary>对端身份（Hello 末尾追加字段；旧构建 = 空串）。只用于与 <see cref="Session"/> 对照。</summary>
            public string Identity = string.Empty;
            public ClientKind Kind = ClientKind.Player;
            public ChunkKey Center;
            public int Radius;
            public bool WindowValid;
            public readonly HashSet<ChunkKey> Window = new HashSet<ChunkKey>();
            public readonly Dictionary<ChunkKey, int> PendingParts = new Dictionary<ChunkKey, int>();
            public readonly Queue<PendingResyncJob> PendingResync = new Queue<PendingResyncJob>();
            /// <summary>
            /// 这条连接归属的**身份记录**（校验 / 上报 / 待裁决的唯一归属，重连复用）。
            /// null = 这条连接没有校验状态：Debug 只读端（不发摘要），或还没 Hello 的连接。
            /// </summary>
            public PlayerSessionRecord Session;
            /// <summary>
            /// 这条连接"没有身份会话却发来摘要上报"的原因已经写过一行（不刷屏）。
            /// 这是明确拒绝，绝不静默建状态、也绝不并进别人的记录。
            /// </summary>
            public bool SessionlessReportLogged;
            /// <summary>
            /// 这条连接"没有身份会话却发来当前内存区块集合"的原因已经写过一行（不刷屏）。
            /// 同样是明确拒绝：不建状态、不并进别人的记录。
            /// </summary>
            public bool SessionlessClientChunksLogged;
            /// <summary>
            /// 链路失效后它们在这个客户端上的**欠更新标记已经清过了**（每 tick 只清一次，
            /// 不让一个已经断掉、还留在表里的客户端每帧去扫全表）。
            /// </summary>
            public bool PendingMarksCleared;
            /// <summary>
            /// 最近一次收到该连接**任何一帧**的主机时钟（Environment.TickCount64）。
            /// 判活不能只看 TCP 状态：闪退 / 半开连接可以长期 TCP Connected（实测 4.5 分钟
            /// 主机仍把死连接算在线、角色留在地图）。超过 PeerSilenceTimeoutMs 无帧 → 判失效。
            /// </summary>
            public long LastHeardMs;
            /// <summary>本连接累计收到的入站帧数：只有"已经正常跑起来的玩家端"才做静默判死，
            /// 避免把正在做世界传输 / 刚握手的连接误判（它们可能几十秒没有帧）。</summary>
            public long InboundFrames;
            /// <summary>判死原因（状态行 / 日志用；null = 正常）。</summary>
            public string LostReason;
            /// <summary>
            /// 这次握手是不是**同一端重连**（Hello 末尾追加字节 = 对端**此刻正在游玩**这个下载世界）。
            /// 本版起还要求**会话标识一致 + 上次应用序号仍在保留历史内**（见 HandleHello）；
            /// 只有它成立才不重发整图，缺字段 / 会话不一致 / 历史越界一律按首次加入处理。
            /// </summary>
            public bool Rejoin;
            /// <summary>Hello 追加字段：对端下载世界登记的主机会话标识（旧构建 = 空串）。</summary>
            public string ClientSessionId = string.Empty;
            /// <summary>
            /// Hello 追加字段：对端本地已有的**角色记录 hash**（来自它下载的 world pak 里的
            /// SuMultiplayerPlayers.xml）。主机据此只重发变化了的角色记录（皮肤资源等不常变部分）。
            /// 空 = 首次加入 / 旧客户端 → 全量补发（语义与改动前一致）。
            /// </summary>
            public readonly List<RecordHashRef> KnownRecords = new List<RecordHashRef>();
            /// <summary>Hello 追加字段：对端上次应用的改动序号（旧构建 = 0）。</summary>
            public long HelloLastApplied;
            /// <summary>这次握手被判为首次加入的**明确原因**（写进 HelloAck，客户端据此写日志 / 作废旧会话）。</summary>
            public string RejoinReason = string.Empty;
            /// <summary>加入审批：主机已同意（自动同意 或 主人点了 Allow）。</summary>
            public bool JoinApproved;
            /// <summary>加入审批：正在等主机决定（此期间**不建记录 / 不建角色 / 不下发世界 / 不回 HelloAck**）。</summary>
            public bool ApprovalPending;
            /// <summary>主机点了 Later（稍后）：审批暂停计时，条目挂在请求队列里由 IF 按钮再处理。
            /// 挂起期间不回 HelloAck、不建记录 / 角色 / 世界传输；恢复 = 重新弹出对话框起算超时。</summary>
            public bool ApprovalHeld;
            /// <summary>等待审批时暂存的 Hello（批准后按它继续走完整握手）。</summary>
            public Hello PendingHello;
            /// <summary>审批超时时刻（Environment.TickCount64 毫秒基准）。</summary>
            public long ApprovalDeadlineMs;
            /// <summary>
            /// 主机没有这个身份的角色记录、Hello 也没带完整资料 → 这次握手**不下发世界**，
            /// 客户端必须先建角色再重新握手（旧 SCMP_PROFILE_REQUIRED 流程）。
            /// </summary>
            public bool ProfileRequired;
            /// <summary>ProfileRequired 的明确原因（日志 / HelloAck.profile_required_reason）。</summary>
            public string ProfileRequiredReason = string.Empty;
            /// <summary>Accepted 重连后要追到的改动序号（-1 = 没有追帧任务）。</summary>
            public long CatchUpTarget = -1L;
            public long Match, Mismatch, NotReady, ResyncPartsSent, CellChangesSent;

            // ---- 改动投递的顺序（一条连接一条流；TCP + 投递序号证明"中间没有缺帧"）----
            /// <summary>
            /// 这条连接上 CellChange 帧的**投递序号**（每成功入队一帧 +1；TrySend 失败不推进）。
            /// 客户端按"prev == 上一条"验证连续性 —— 缺口 = 显式警告 + 强制核对，绝不静默继续。
            /// </summary>
            public long DeliveryIndex;
            /// <summary>这条连接上最后**成功发出**的改动全局序号（补发的起点；0 = 还没有）。</summary>
            public long LastDeliveredSequence;
            /// <summary>
            /// 这条连接有"没发出去的改动"：值 = 最早缺失的全局序号（-1 = 没有）。
            /// 一旦置位，本连接**不再发送任何新改动**，直到 Tick 把历史里缺失的那些按序号补完 ——
            /// 这样客户端看到的序号永远单调，旧值不会被后到的补发盖住。
            /// </summary>
            public long RepairFromSequence = -1L;
            /// <summary>这条连接累计补发成功的改动条数（诊断）。</summary>
            public long RepairedChanges;
            /// <summary>"补发不可用（缺口已被有界历史裁掉）"的明确原因只写一行。</summary>
            public bool RepairUnavailableLogged;
        }

        private readonly ITerrainWorld m_world;
        private readonly TcpTerrainServer m_server;
        private readonly MessageChannel m_messages;
        private readonly TerrainSyncOptions m_options;
        private readonly Dictionary<int, State> m_clients = new Dictionary<int, State>();

        // ---- 按需单块加载（客户端上报的边缘块；**不放大**任何角色的兴趣半径 / 内容距离）----
        /// <summary>
        /// 逐块临时加载的台账（唯一入口；参数见 <see cref="TerrainSyncOptions.OnDemandLoad"/>）。
        /// 引擎调用只通过 <see cref="ITerrainWorld.TryRequestSingleChunkLoad"/> /
        /// <see cref="ITerrainWorld.ReleaseSingleChunkLoad"/>；本类只管"什么时候请求、什么时候裁决、什么时候放弃"。
        /// </summary>
        private readonly OnDemandChunkLoads m_onDemand;
        /// <summary>本拍内容到位、要**立刻裁决**的块（复用缓冲，不在热路径分配）。</summary>
        private readonly List<ChunkKey> m_onDemandServed = new List<ChunkKey>(16);
        /// <summary>本拍明确放弃（超时 / 引擎拒绝）的块（宿主随后把它们从待裁决里摘掉）。</summary>
        private readonly List<ChunkKey> m_onDemandTimedOut = new List<ChunkKey>(16);
        /// <summary>按需加载台账产出的限流日志行（取走后交给 <see cref="OnDemandTrace"/>）。</summary>
        private readonly List<string> m_onDemandLines = new List<string>(4);
        /// <summary>"这块现在还该不该留在主机内存里"的判定（交给台账，避免每帧分配委托）。</summary>
        private readonly Func<ChunkKey, bool> m_onDemandStillNeeded;
        /// <summary>因为"主机明确不为这块发起加载"（冷却 / 上限 / 引擎拒绝）而没有登记成待裁决的上报条数。</summary>
        private long m_onDemandReportsDropped;
        /// <summary>因为按需加载明确放弃（超时 / 引擎拒绝）而从待裁决里摘掉的上报条数。</summary>
        private long m_onDemandReportsTimedOut;

        /// <summary>
        /// 一条权威改动（有界改动历史的最小单元）。序号由 <see cref="BroadcastCellChange"/> 递增分配，
        /// 客户端收到 CellChange 时把序号记为"上次应用序号"，重连时发回，主机从历史里重放它之后的改动。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:311-326
        ///         （RecordHostTerrainJournal：Sequence + CreatedTime + Payload 入队）
        /// </summary>
        private sealed class ChangeJournalEntry
        {
            public long Sequence;
            public ChunkKey Key;
            public CellEdit Edit;
            public long CreatedMs;
        }

        /// <summary>
        /// 一块待回传的整块快照。**发送时才编码**（见 Tick）：快照因此包含此前已应用的全部改动；
        /// 编码水位随载荷尾字段下发，客户端用它丢弃"比本块已应用改动更旧"的快照。
        /// </summary>
        private sealed class PendingResyncJob
        {
            public ChunkKey Key;
            public List<byte[]> Parts;
            public int Index;
            public long EncodeSequence = -1L;
        }

        /// <summary>
        /// 一次追帧发送任务：从历史里截出的 [lastApplied+1 .. head] 改动，按每帧预算分批发。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:774-911
        ///         （SendHostTerrainRecoveryRound：先把 journal 截成 replay，再按 64 条 / 4 MiB 分批发，
        ///          最后发 Barrier）
        /// </summary>
        private sealed class CatchUpJob
        {
            public int ClientId;
            public long RequestedSequence;
            public long HeadSequence;
            public long FirstRetainedSequence;
            public List<ChangeJournalEntry> Entries = new List<ChangeJournalEntry>();
            public int Index;
            public bool BarrierSent;
            public long StartedMs;
            public long BarrierSentMs;
            public int BatchCount;
        }
        /// <summary>
        /// 身份（**已 Trim 的 UserId**）→ 会话记录：主机侧校验状态的唯一归档。
        /// 只在同一个房间 / 会话内有效（主机实例一换就是新会话）；重连（新 clientId、同身份）**复用**同一条记录。
        /// </summary>
        private readonly Dictionary<string, PlayerSessionRecord> m_sessions =
            new Dictionary<string, PlayerSessionRecord>(StringComparer.Ordinal);
        /// <summary>
        /// 退化路径：Hello 没带身份（UserId.dat 为空 / 旧构建）的玩家端 → 按 **clientId** 单独一条记录。
        /// 与 <see cref="m_sessions"/> 分开存（键空间不重叠，绝不和真实身份合并 —— 这不是静默兜底：
        /// 建记录时由 <see cref="SessionProblem"/> 抛一行明确错误）。它的生命周期随那条连接。
        /// </summary>
        private readonly Dictionary<int, PlayerSessionRecord> m_degradedSessions =
            new Dictionary<int, PlayerSessionRecord>();
        private readonly Dictionary<ChunkKey, HashSet<int>> m_subscribers = new Dictionary<ChunkKey, HashSet<int>>();
        private readonly object m_gate = new object();

        /// <summary>
        /// 逐区块**欠更新身份集合**（实时地图格内数字的唯一来源；**只在内存里，绝不落盘**）。
        ///
        /// **键 = 身份的稳定键（<see cref="PlayerSessionRecord.StableKey"/>），不是 clientId**：
        /// 重连（新 clientId、同身份）不会丢标记、也不会把同一个身份算两遍。
        /// 集合成员 = 该身份**当前内存集合**（<see cref="PlayerSessionRecord.MemoryChunks"/>）里没有这一块的身份；
        /// 格内数字 = 集合大小（用户口径：数字 = 还有几个连着的人手里没有这一块）。
        ///
        /// 语义（用户口径，逐条对应）：
        ///   · 值 = "现在仍连着的身份里，有几个当前内存集合不含该区块"；0 = 全部都有 → 不标注；
        ///   · 主机自己加载/卸载哪一块**不影响**这个计数（<see cref="ITerrainWorld"/> 的加载集合与它无关）；
        ///   · 身份加入时**不**往已有区块里加它（加入时的整图下载就是最新的基线，它谁都不欠）；
        ///   · 区块有权威改动（<see cref="BroadcastCellChange"/> → <see cref="MarkChunkChanged"/>）→
        ///     **逐个在线玩家身份**按它自己的内存集合结算：该集合**不含**这一块 → 记上（同一身份只记一次）；
        ///     含这一块 → 不加，并移除它可能残留的标记；
        ///   · 某身份的内存集合**新增**这一块（<c>ClientChunks</c> 增量 add / 整集替换包含它）→
        ///     从该块集合里移除它的标记（<see cref="ClearPendingOnChunkLoaded"/>）；
        ///   · 身份离开联机（Bye / 链路失效）→ 把它从**所有**区块的集合里去掉（<see cref="RemovePendingClientEverywhere"/>）；
        ///   · 房间/会话结束 → 整体清空（<see cref="ClearPendingMarks"/>）；因为不落盘，下次加入一定是"全图最新"。
        /// **不按投递结果结算、也不按裁决结果清除**：订阅表、CellChange 是否真的发出去、verdict 是不是 Match
        /// 都不参与这个计数（它们各自有别的用途）；集合只由"改动时谁当前内存里没有这块"和"谁后来加载了这块"决定。
        /// 只增/删条目本身，绝不写入任何文件、Project.xml 或世界目录。
        /// </summary>
        private readonly Dictionary<ChunkKey, Dictionary<string, long>> m_pendingClients =
            new Dictionary<ChunkKey, Dictionary<string, long>>();

        // ---- 断线重连追帧（同会话）：改动历史 / 已接受的重连目标 / 计数与限流 ----
        /// <summary>
        /// 已广播的权威改动序号（从 1 递增；世界清单的导出基线与追帧范围都以它为准）。
        /// </summary>
        private long m_changeSequence;
        /// <summary>有界改动历史：序号递增，超出时间 / 条数上限就从队首裁掉（见 TrimChangeJournalLocked）。</summary>
        private readonly Queue<ChangeJournalEntry> m_changeJournal = new Queue<ChangeJournalEntry>();
        /// <summary>客户端 → 追帧发送任务（同一 clientId 只保留最新一次请求）。</summary>
        private readonly Dictionary<int, CatchUpJob> m_catchUps = new Dictionary<int, CatchUpJob>();
        private long m_changeJournalEvictions;
        private long m_lastChangeJournalEvictionLogMs;
        private long m_rejoinAccepted;
        private long m_rejoinRejected;
        private long m_catchUpRejected;
        private long m_catchUpRounds;
        private long m_catchUpChangesSent;

        private long m_digestReports, m_resyncBytes, m_cellChanges;
        /// <summary>同一块的 Mismatch 已有一份回传快照在队列里 → 合并（不再 1:1 放大回传队列/带宽）。</summary>
        private long m_resyncCoalesced;
        /// <summary>裁决后、编码前主机丢了该块内容 → 不发零格快照，改回 NotReady 的次数。</summary>
        private long m_resyncEmptySkipped;
        private long m_lastResyncEmptyLogMs;
        /// <summary>广播改动时没发出去的投递次数（显式背压 / 不可达）——仅记账，不参与欠更新计数。</summary>
        private long m_cellChangeSendFailures;
        // ---- 改动投递（内存集合 ∪ 兴趣窗口）：成功投递的组成 / 推迟 / 补发 ----
        /// <summary>成功投递给"当前内存集合包含该块"的身份的次数。</summary>
        private long m_cellChangeMemoryRecipients;
        /// <summary>其中**只**因为内存集合才投递（不在该连接兴趣窗口内）的次数。</summary>
        private long m_cellChangeMemoryOnly;
        /// <summary>成功投递时该块不在接收方兴趣窗口内的次数。</summary>
        private long m_cellChangeOutsideWindow;
        /// <summary>因为追帧在途 / 有缺失未补而推迟投递的改动次数。</summary>
        private long m_cellChangeDeferrals;
        /// <summary>按历史顺序补发成功的改动条数。</summary>
        private long m_cellChangeRepairs;
        /// <summary>补发时又遇到背压、留到下一 tick 的次数。</summary>
        private long m_cellChangeRepairDeferrals;
        /// <summary>缺口已被有界历史裁掉、无法序列化补齐的连接次数。</summary>
        private long m_cellChangeRepairUnavailable;
        // ---- "身份刚开始持有某块"时的整块快照补发（把 ClientChunks 在途窗口也补上）----
        /// <summary>因为"欠账标记被清除（身份刚开始持有该块）"而排队的整块快照数。</summary>
        private long m_owedSnapshotsQueued;
        /// <summary>同上但主机自己没有该块内容、无法补发（明确计数，由客户端加载时摘要核对兜住）。</summary>
        private long m_owedSnapshotsSkippedNoContent;
        private long m_lastOwedSnapshotLogMs;
        // ---- 主机 → 客户端"重新核对内存区块"（主机侧无法按序号补齐缺口时的触达通道）----
        /// <summary>已发出的"重新核对"请求数。</summary>
        private long m_verifyRequestsSent;
        /// <summary>请求没发出去（显式背压 / 不可达）的次数。</summary>
        private long m_verifyRequestSendFailures;
        /// <summary>"订阅端没发出去"的原因行上一次写出的时刻（限流，见 CellChangeFailureLogIntervalMs）。</summary>
        private long m_lastCellChangeSendFailureLogMs;

        // ---- 陈旧裁决自愈（红框在内容补齐后必须重新裁决；见 TerrainSyncOptions.StaleVerdict*）----
        /// <summary>已发出的“要一份新鲜摘要”的区块请求数（按区块计）。</summary>
        private long m_staleVerdictRefreshRequested;
        /// <summary>请求后拿到新鲜裁决并改判成 Match / ReVerified 的区块数。</summary>
        private long m_staleVerdictRefreshed;
        /// <summary>过了清除上限仍没有新鲜裁决 → 明确清掉的陈旧红区块数。</summary>
        private long m_staleVerdictCleared;
        /// <summary>请求因显式背压 / 不可达没发出去的次数（请求次数会退回）。</summary>
        private long m_staleVerdictRefreshDeferred;
        /// <summary>“请求没发出去”原因行上一次写出的时刻（全局限流，避免背压时刷屏）。</summary>
        private long m_lastStaleVerdictProblemLogMs;
        /// <summary>本 tick 待合并成一条 VerifyChunks 的区块（复用，避免热路径分配）。</summary>
        private readonly List<ChunkKey> m_staleVerdictBatch = new List<ChunkKey>();

        // ---- 客户端"当前内存区块集合"（v8 实时地图）的计数与限流 ----
        /// <summary>收齐的整集次数（所有身份累计）。</summary>
        private long m_clientChunkFullSets;
        /// <summary>应用过的增量消息条数（所有身份累计；消息内可含多块）。</summary>
        private long m_clientChunkDeltaMessages;
        /// <summary>被拒绝的载荷条数（格式 / 超上限 / 非当前句柄；超限细节由限流日志点名）。</summary>
        private long m_clientChunkRejects;
        /// <summary>上一次"拒绝"原因行写出的时刻；0 = 还没写过（不能用 TickCount64 直接相减）。
        /// 限流间隔 <see cref="TerrainSyncOptions.ClientChunkLogIntervalMs"/>。</summary>
        private long m_lastClientChunkRejectLogMs;
        /// <summary>自上次拒绝日志以来又被拒绝的条数（写进下一行，便于判断是否持续发生）。</summary>
        private long m_clientChunkRejectsSinceLog;
        /// <summary>上一次"增量汇总"原因行写出的时刻；0 = 还没写过。</summary>
        private long m_lastClientChunkDeltaLogMs;
        /// <summary>自上次增量汇总以来的消息条数 / 新增块数 / 移除块数 / 因新增块而清掉的欠更新标记数。</summary>
        private long m_clientChunkDeltaMessagesSinceLog;
        private long m_clientChunkDeltaAddsSinceLog;
        private long m_clientChunkDeltaRemovesSinceLog;
        private long m_clientChunkDeltaMarkClearsSinceLog;
        /// <summary>自上次增量汇总以来因"欠账清除"补发的整块快照数。</summary>
        private long m_clientChunkDeltaSnapshotsSinceLog;

        /// <param name="attachToChannel">
        /// false = 不自挂网络回调，由主线程泵调用 <see cref="ProcessFrame"/>。
        /// 引擎接入必须用 false：算摘要要读引擎地形，只能发生在主线程。
        /// </param>
        public TerrainSyncHost(ITerrainWorld world, TcpTerrainServer server,
            TerrainSyncOptions options = null, bool attachToChannel = true,
            MessageChannel messages = null)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_server = server ?? throw new ArgumentNullException(nameof(server));
            m_options = options ?? new TerrainSyncOptions();
            m_messages = messages ?? new MessageChannel(new MessageRegistry(),
                (clientId, type, payload) => clientId > 0 && m_server.TrySend(clientId, type, payload),
                "terrain-sync-host");
            m_onDemand = new OnDemandChunkLoads(m_world, m_options.OnDemandLoad);
            m_onDemandStillNeeded = HostStillNeedsOnDemandChunk;
            RegisterMessages();
            if (attachToChannel) m_server.FrameReceived += OnFrame;
        }

        /// <summary>
        /// 按需单块加载的**限流**日志行（超时放弃 / 被拒绝 / 汇总）。台账已经限流（同一块一行、
        /// 拒绝与汇总按 10s），这里只负责抛给组合根写日志 —— 不在热路径逐条打印。
        /// </summary>
        public event Action<string> OnDemandTrace;

        /// <summary>主线程泵入口：处理一帧入站消息。</summary>
        public void ProcessFrame(in ReceivedFrame frame) => OnFrame(frame);

        /// <summary>消息层（注册表 + 发送器）。组合根把它交给第三方模块。</summary>
        public MessageChannel Messages => m_messages;

        private const string MessageOwner = "terrain-sync-host";

        private State GetOrCreateState(int clientId)
        {
            if (!m_clients.TryGetValue(clientId, out State state))
            {
                state = new State { Id = clientId, Radius = m_options.DefaultRadius };
                m_clients[clientId] = state;
            }
            return state;
        }

        /// <summary>
        /// 主机侧全部入站消息都在这里按类型注册（强类型解析 + 处理器）。
        /// 未注册的类型由 <see cref="MessageRegistry"/> 明确计数并写一行原因，不静默丢。
        /// </summary>
        private void RegisterMessages()
        {
            MessageRegistry registry = m_messages.Registry;
            // 同一通道复用（重连 / 重新建房）：先移除上一实例的处理器，避免重复注册。
            registry.UnregisterOwner(MessageOwner);
            registry.Register((int)MessageType.Hello, Hello.Parser,
                new DelegateMessageHandler<Hello>(OnHello), MessageOwner);
            registry.Register((int)MessageType.InterestUpdate, InterestUpdate.Parser,
                new DelegateMessageHandler<InterestUpdate>(OnInterest), MessageOwner);
            registry.Register((int)MessageType.ChunkDigestReport, PT.ChunkDigestReport.Parser,
                new DelegateMessageHandler<PT.ChunkDigestReport>(OnDigestReport), MessageOwner);
            registry.Register((int)MessageType.ClientChunks, PT.ClientChunks.Parser,
                new DelegateMessageHandler<PT.ClientChunks>(OnClientChunks), MessageOwner);
            registry.Register((int)MessageType.CatchUp, PT.CatchUp.Parser,
                new DelegateMessageHandler<PT.CatchUp>(OnCatchUp), MessageOwner);
            registry.Register((int)MessageType.VerifyChunks, PT.VerifyChunks.Parser,
                new DelegateMessageHandler<PT.VerifyChunks>(OnVerifyChunksFromClient), MessageOwner);
            registry.Register((int)MessageType.Bye, Bye.Parser,
                new DelegateMessageHandler<Bye>(OnBye), MessageOwner);
            registry.Register((int)MessageType.Ping, Ping.Parser,
                new DelegateMessageHandler<Ping>(OnPing), MessageOwner);
        }

        private void OnHello(MessageContext context, Hello message)
        {
            lock (m_gate) HandleHello(GetOrCreateState(context.ClientId), message);
        }

        /// <summary>
        /// 加入等待期间的实时测速：客户端每 500ms 发 Ping，主机原样带回 nonce。
        /// 审批尚未通过、还没有 State/Session 时也必须能回 Pong，所以这里不走锁、
        /// 不碰角色/世界状态，只读 context.ClientId 发一帧。
        /// </summary>
        private void OnPing(MessageContext context, Ping message)
        {
            if (message == null) return;
            var pong = new Pong { Nonce = message.Nonce };
            m_server.TrySend(context.ClientId, MessageType.Pong, pong.ToByteArray());
        }

        private void OnInterest(MessageContext context, InterestUpdate message)
        {
            lock (m_gate) HandleInterest(GetOrCreateState(context.ClientId), message);
        }

        private void OnDigestReport(MessageContext context, PT.ChunkDigestReport message)
        {
            lock (m_gate) HandleDigestReport(GetOrCreateState(context.ClientId), message);
        }

        private void OnClientChunks(MessageContext context, PT.ClientChunks message)
        {
            lock (m_gate) HandleClientChunks(GetOrCreateState(context.ClientId), message);
        }

        private void OnCatchUp(MessageContext context, PT.CatchUp message)
        {
            lock (m_gate) HandleCatchUpMessage(GetOrCreateState(context.ClientId), message);
        }

        private void OnVerifyChunksFromClient(MessageContext context, PT.VerifyChunks message)
        {
            lock (m_gate)
            {
                State state = GetOrCreateState(context.ClientId);
                CatchUpProblem?.Invoke("客户端 clientId=" + state.Id +
                    "（" + DisplayName(state) + "）发来不该由客户端发送的 VerifyChunks → 丢弃" +
                    "（协议方向错误，计入日志）");
            }
        }

        private void OnBye(MessageContext context, Bye message)
        {
            string byeReason = "主动断开（Bye" +
                (string.IsNullOrEmpty(message.Reason) ? "" : "：" + message.Reason) + "）";
            lock (m_gate)
            {
                State state = GetOrCreateState(context.ClientId);
                // 客户端主动走了：欠着的上报**明确写一行**（不是静默丢弃），并保留在该身份的
                // 会话记录里（同一身份再回来时继续用真实主机摘要作答；房间结束才整体清）；
                // 它在所有区块上的欠更新标记也一并去掉（"退出联机 → 它的标记全没了"）。
                bool wasPending = state.ApprovalPending;
                NoteSessionDeparture(state, byeReason);
                ReleaseWindow(state);
                m_clients.Remove(context.ClientId);
                if (wasPending) PendingApprovalAborted?.Invoke(context.ClientId);
            }
            // 2026-10-26 显式退房立即摘角色：Bye 是"我要走了"，不是普通断线，零宽限。
            // 必须在锁外抛（处理方要拆引擎实体 / 广播，绝不能在 m_gate 里做）。
            ClientLost?.Invoke(context.ClientId, byeReason);
        }

        /// <summary>
        /// 这个玩家端这次握手是不是**重连**（Hello 里带了"此刻正在游玩这个下载世界"）。
        /// 只在 <see cref="ClientJoined"/> 回调里查才有意义（握手刚写完该 state）。
        /// 上层据此**不再重发整图**（重发会让已经进了世界的客户端重新下载并导入一遍）。
        /// </summary>
        public bool WasRejoin(int clientId)
        {
            lock (m_gate)
            {
                return m_clients.TryGetValue(clientId, out State state) && state.Rejoin;
            }
        }

        /// <summary>
        /// 该连接在 Hello 里申报的"我本地已有这些角色记录 hash"清单（重连 diff 用）。
        /// 读的是**这次连接**的握手内容；首次加入 / 旧客户端 = 0 条（= 全量补发）。
        /// </summary>
        public int CaptureKnownRecordHashes(int clientId, List<RecordHashRef> target)
        {
            if (target == null) return 0;
            lock (m_gate)
            {
                target.Clear();
                if (!m_clients.TryGetValue(clientId, out State state)) return 0;
                target.AddRange(state.KnownRecords);
                return target.Count;
            }
        }

        /// <summary>
        /// 这个连接是否已经持有某条角色记录的**当前** hash：
        /// true = 对端本地内容与我们一致 → 这条记录（皮肤资源这类不常变大块数据）不必重发。
        /// false = 没申报 / 没这条 / hash 不同 → 调用方按"变了"处理（绝不静默跳过）。
        /// </summary>
        public bool TryGetKnownRecordHash(int clientId, string recordKey, out ulong hash)
        {
            hash = 0UL;
            if (string.IsNullOrEmpty(recordKey)) return false;
            lock (m_gate)
            {
                if (!m_clients.TryGetValue(clientId, out State state)) return false;
                for (int i = 0; i < state.KnownRecords.Count; i++)
                {
                    if (!string.Equals(state.KnownRecords[i].RecordKey, recordKey,
                            StringComparison.Ordinal)) continue;
                    hash = state.KnownRecords[i].Hash;
                    return hash != 0UL;
                }
                return false;
            }
        }

        /// <summary>
        /// 该客户端当前兴趣窗口（区块中心 + 半径）。客户端编辑请求的**距离校验**唯一依据：
        /// 没有窗口（还没上报兴趣中心）就返回 false —— 没有距离依据时编辑一律拒绝，绝不猜中心。
        /// </summary>
        public bool TryGetClientRange(int clientId, out ChunkKey center, out int radius)
        {
            lock (m_gate)
            {
                center = default;
                radius = 0;
                if (!m_clients.TryGetValue(clientId, out State state) || !state.WindowValid) return false;
                center = state.Center;
                radius = state.Radius;
                return true;
            }
        }

        /// <summary>
        /// 这条连接的**稳定用户键**（userid 派生，见 <see cref="PlayerSessionKeys"/>）。
        ///
        /// 为什么不能用 clientId 当逐用户投递的位序：clientId 只是**这一次连接**的传输句柄，
        /// 重连就会重分配（同一个人换号、新的人拿到旧号）。按它归档 / 投递会让上一轮的数据
        /// 投给下一个人。所以实体复制、位置、体征这类"逐端裁剪"一律用这个键定位用户，
        /// 再用 State 上的当前连接句柄把数据发出去。
        ///
        /// 没有身份会话（Hello 还没到）时返回**按连接派生的退化键** —— 明确退化（前缀不同，
        /// 与身份键永不合并），不静默当成某个已知用户。
        /// </summary>
        public bool TryGetClientUserKey(int clientId, out string userKey)
        {
            lock (m_gate)
            {
                userKey = string.Empty;
                if (!m_clients.TryGetValue(clientId, out State state)) return false;
                if (state.Session != null && !string.IsNullOrEmpty(state.Session.StableKey))
                {
                    userKey = state.Session.StableKey;
                    return true;
                }
                userKey = PlayerSessionKeys.DegradedKeyOf(clientId);
                return true;
            }
        }

        /// <summary>有客户端完成 Hello 握手（加入房间）。参数：clientId, 名字, 身份。</summary>
        public event Action<int, string, string> ClientJoined;

        /// <summary>
        /// 主机欠着一条上报的裁决（内容一直没加载出来 / 客户端断开时还有欠账）—— 调用方写一行日志。
        /// **事件本身已经限流**：同一块最多一条（"内容一直没加载出来"），客户端断开时最多一条（欠账汇总），
        /// 所以调用方照原样写日志即可，不会刷屏。Terrain 层不认识引擎日志，所以走事件。
        /// </summary>
        public event Action<string> ReportUnresolved;

        /// <summary>
        /// **欠更新计数器的生命周期决策**：加标记（区块有改动、而该身份当前内存里没有这块）/
        /// 移除标记（它后来把这块加载进内存 / 它离开联机 / 会话结束）—— 每次**状态真的变了**才抛一条
        /// （0→≥1 的加入、真的移除了标记），按身份加载清除的日志由增量汇总行限流输出，
        /// 因此不是逐格改动的热路径日志；Terrain 层不认识引擎日志，由上层写一行。
        /// 这个计数器**不看投递结果、也不看裁决结果**（见 <see cref="m_pendingClients"/>）。
        /// </summary>
        public event Action<string> PendingTrace;

        /// <summary>
        /// 一个玩家端的连接**已经不可达**（对端关闭 / 链路失效 / 协议违规）：参数 (clientId, 原因)。
        /// 每个 state 只抛**一次**（与 <see cref="State.PendingMarksCleared"/> 同一处状态迁移），
        /// 上层据此写一行明确日志**并清掉这个客户端的联机角色**（角色实体会停在最后一个已应用的位置上，
        /// 不清理就会留下一个永远不动的"幽灵角色"，重连后还会出现第二个）。
        /// 本事件与 <see cref="ClientJoined"/> 成对：加入建角色、离开清角色。
        /// </summary>
        public event Action<int, string> ClientLost;

        /// <summary>
        /// 主机判活超时：连续这么久**一帧都没收到**就按失效处理（半开连接 / 闪退）。
        /// 客户端在世界里位置 32Hz、动作有变化驱动，正常绝不会静默这么久；
        /// 显式退房走 BYE 立即摘角色，不走这个超时（2026-10-26）。
        /// </summary>
        private const long PeerSilenceTimeoutMs = 30000;

        /// <summary>
        /// **身份会话记录的建立 / 重连复用**（一行一条，不是热路径）：参数是给上层写日志的整句。
        /// Terrain 层不认识引擎日志，所以走事件；句子里**只出现身份前 8 位与稳定键，绝不出现 UserId 全值**。
        /// </summary>
        public event Action<string> SessionTrace;

        /// <summary>
        /// **身份缺失 / 身份异常**（必须当错误写一行）：玩家端 Hello 没带身份（UserId.dat 为空 / 旧构建）
        /// 时走"按 clientId 单独一条记录"的**退化路径**；同一身份被另一条在线连接占用时也会抛。
        /// 这不是静默兜底 —— 调用方必须把它写成明确的一行错误。
        /// </summary>
        public event Action<string> SessionProblem;

        /// <summary>
        /// 客户端"当前内存区块集合"（v8 实时地图）的**明确日志**，参数是给上层写日志的整句：
        /// 整集收齐（每次重连一行）、增量汇总（按 <see cref="TerrainSyncOptions.ClientChunkLogIntervalMs"/>
        /// 限流，一行说明这段时间应用了多少条 / 增删多少块 / 当前集合多大）。
        /// 不是热路径日志：整集 = 每次 (重)连一次；增量 = 10 s 一行。
        /// </summary>
        public event Action<string> ClientChunkSetTrace;

        /// <summary>
        /// 被拒绝的"当前内存区块集合"载荷（超上限 / 格式错误 / 分片不一致 / 非当前句柄）：
        /// **必须让用户看见**，写成 Warning。事件本身已限流（同一间隔最多一行，计数照常累加，
        /// 行内带最近拒绝条数与累计值）—— 绝不静默丢弃，也绝不部分应用。
        /// </summary>
        public event Action<string> ClientChunkSetProblem;

        /// <summary>
        /// 本机（主机）身份与名字 —— 由上层注入（Terrain 层不认识 UserId.dat / Game.UserManager），
        /// 用在 HelloAck 的末尾追加字段里回给客户端。
        /// </summary>
        public string Identity { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 资料闸门（由联机角色模块注入）：主机在 Hello 时决定"这个身份有没有角色记录 /
        /// 要不要先建角色"。null = 没有接上资料模块（旧行为：不要求建角色）。
        /// 见 <see cref="IHelloProfileGate"/>。
        /// </summary>
        public IHelloProfileGate ProfileGate { get; set; }

        /// <summary>
        /// 加入审批决定器（由组合根注入；null = 不做审批）。
        /// 有它时，玩家端 Hello 先走审批：手动审批 = 主机点 Allow/Reject；自动同意 = 直接放行。
        /// 审批发生在资料闸门 / 角色记录 / 世界传输之前，拒绝的连接不会留下任何角色。
        /// </summary>
        public IJoinApprovalDecider JoinApproval { get; set; }

        /// <summary>等待主机审批的上限（毫秒）：超时 = 明确拒绝（绝不"等不到就当同意"）。</summary>
        public const long JoinApprovalTimeoutMs = 120000;

        /// <summary>主机同意 clientId 的加入（审批界面的 Allow）。不是等待审批状态 = 忽略。</summary>
        public void ApproveJoin(int clientId) => ResolveJoinApproval(clientId, true, string.Empty);

        /// <summary>主机拒绝 clientId 的加入（审批界面的 Reject / 审批超时）。客户端收到 HelloAck.rejected。</summary>
        public void RejectJoin(int clientId, string reason) =>
            ResolveJoinApproval(clientId, false, reason ?? string.Empty);

        /// <summary>
        /// 本次房间 / 会话标识（32 位十六进制，由上层在建房时生成；**绝不落盘**）。
        /// HelloAck 与 WorldBegin 世界清单都带它；客户端把它与下载世界登记在一起，
        /// 重连时发回 Hello —— 只有与会话一致才允许"不重发整图、只追帧"。
        /// </summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>主机当前改动序号（世界清单的导出基线；没有主机 = 0）。</summary>
        public long CurrentChangeSequence
        {
            get { lock (m_gate) return m_changeSequence; }
        }

        /// <summary>当前历史里最旧一条的序号；没有历史 = <see cref="CurrentChangeSequence"/>/+1（可直接接受当前序号）。</summary>
        public long FirstRetainedSequence
        {
            get
            {
                lock (m_gate)
                {
                    return m_changeJournal.Count > 0
                        ? m_changeJournal.Peek().Sequence
                        : m_changeSequence + 1;
                }
            }
        }

        /// <summary>
        /// 重连判定的一行明确日志（接受 / 拒绝 + 原因 + 会话短标识 + 序号范围）。
        /// 握手不是热路径，这里每次握手一条；上层照原样写日志。
        /// </summary>
        public event Action<string> RejoinTrace;

        /// <summary>
        /// 追帧过程的明确日志（开始一轮 / 完成 / 拒绝 / 裁剪历史 / 超时）。事件本身已限流，
        /// 上层照原样写一行即可。
        /// </summary>
        public event Action<string> CatchUpTrace;

        /// <summary>追帧的**异常 / 拒绝 / 裁剪 / 超时**（必须写成 Warning，让用户看见）。</summary>
        public event Action<string> CatchUpProblem;

        public TerrainHostSnapshot GetSnapshot()
        {
            var snapshot = new TerrainHostSnapshot
            {
                DigestReports = m_digestReports, ResyncBytes = m_resyncBytes,
                ResyncCoalesced = m_resyncCoalesced, ResyncEmptySkipped = m_resyncEmptySkipped,
                CellChangesBroadcast = m_cellChanges, CellChangesDeliveryFailed = m_cellChangeSendFailures,
                CellChangesMemoryRecipients = m_cellChangeMemoryRecipients,
                CellChangesMemoryOnly = m_cellChangeMemoryOnly,
                CellChangesOutsideWindow = m_cellChangeOutsideWindow,
                CellChangeDeferrals = m_cellChangeDeferrals,
                CellChangeRepairs = m_cellChangeRepairs,
                CellChangeRepairDeferrals = m_cellChangeRepairDeferrals,
                CellChangeRepairUnavailable = m_cellChangeRepairUnavailable,
                OwedSnapshotsQueued = m_owedSnapshotsQueued,
                OwedSnapshotsSkippedNoContent = m_owedSnapshotsSkippedNoContent,
                VerifyRequestsSent = m_verifyRequestsSent,
                VerifyRequestSendFailures = m_verifyRequestSendFailures,
                StaleVerdictRefreshRequested = m_staleVerdictRefreshRequested,
                StaleVerdictRefreshed = m_staleVerdictRefreshed,
                StaleVerdictCleared = m_staleVerdictCleared,
                StaleVerdictRefreshDeferred = m_staleVerdictRefreshDeferred
            };
            lock (m_gate)
            {
                var union = new HashSet<ChunkKey>();
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    foreach (ChunkKey key in pair.Value.Window) union.Add(key);
                }
                // **按身份归档**：同一条身份记录只输出一行（代表连接 = 记录当前指向的那个 clientId）。
                // 旧连接还没被判失效时，表里会有两条连接指向同一条记录 —— 实时地图据此不会多出第二个条目，
                // 页面选角（稳定键）也不会因此断掉。
                var represented = new Dictionary<PlayerSessionRecord, State>();
                var order = new List<State>();
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    State state = pair.Value;
                    PlayerSessionRecord session = state.Session;
                    if (session == null)
                    {
                        // 没有身份会话的连接（Debug 只读端 / 还没 Hello）：照实各占一行，但没有任何校验状态。
                        order.Add(state);
                        continue;
                    }
                    if (!represented.TryGetValue(session, out State current))
                    {
                        represented[session] = state;
                        order.Add(state);
                        continue;
                    }
                    if (current.Id != session.CurrentClientId && state.Id == session.CurrentClientId)
                        represented[session] = state;      // 换成记录当前指向的那条连接
                }
                var views = new List<ClientView>(order.Count);
                var countedSessions = new HashSet<PlayerSessionRecord>();
                for (int i = 0; i < order.Count; i++)
                {
                    State state = order[i];
                    PlayerSessionRecord session = state.Session;
                    if (session != null && represented.TryGetValue(session, out State chosen) && chosen.Id != state.Id)
                        state = chosen;                         // 用"当前句柄"那条连接代表该身份
                    var view = new ClientView
                    {
                        Id = state.Id, Name = state.Name, Kind = state.Kind, Center = state.Center,
                        Radius = state.Radius, WindowChunks = state.Window.Count,
                        Tracked = state.PendingParts.Count, PendingParts = state.PendingResync.Count,
                        Match = state.Match, Mismatch = state.Mismatch, NotReady = state.NotReady,
                        ResyncPartsSent = state.ResyncPartsSent, CellChangesSent = state.CellChangesSent,
                        State = m_server.StateOf(state.Id)
                    };
                    if (session != null)
                    {
                        view.StableKey = session.StableKey;
                        view.CurrentClientId = session.CurrentClientId;
                        view.PendingReports = session.PendingReports;
                        // 待裁决条数按**身份**求和（同一身份的多条连接不重复计）
                        if (countedSessions.Add(session))
                        {
                            snapshot.PendingReports += session.PendingReports;
                            snapshot.MemoryChunkCount += session.MemoryChunks.Count;
                            snapshot.ClientChunkFullSets += session.ClientChunkFullSets;
                            snapshot.ClientChunkDeltaMessages += session.ClientChunkDeltaMessages;
                            snapshot.ClientChunkRejects += session.ClientChunkRejects;
                            snapshot.PendingStaleVerdicts += session.PendingStaleVerdicts;
                        }
                        view.ClientChunkFullSets = session.ClientChunkFullSets;
                        view.ClientChunkDeltaMessages = session.ClientChunkDeltaMessages;
                        view.ClientChunkRejects = session.ClientChunkRejects;
                        if (session.ReportedLoaded.Count > 0)
                        {
                            view.ReportedLoaded.AddRange(session.ReportedLoaded);
                            // 顺序稳定（实时地图按 x,z 画；同一份数据每次输出同一个顺序，便于比对）
                            view.ReportedLoaded.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
                        }
                        // v8：该身份**当前内存**的区块集合（实时地图客户端线框只画这一份）
                        if (session.MemoryChunks.Count > 0)
                        {
                            view.MemoryChunks.AddRange(session.MemoryChunks);
                            view.MemoryChunks.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
                        }
                    }
                    views.Add(view);
                }
                // 顺序稳定：按当前传输句柄（clientId）排；同一身份只会有一条
                views.Sort((a, b) => a.Id.CompareTo(b.Id));
                snapshot.Clients.AddRange(views);
                snapshot.Interest.AddRange(union);
                // 欠更新计数（与主机加载集合无关，所以从这里一并给出，实时地图的状态行也用它）
                snapshot.PendingMarks = m_pendingClients.Count;
                int pendingClients = 0;
                foreach (KeyValuePair<ChunkKey, Dictionary<string, long>> pair in m_pendingClients)
                    pendingClients += pair.Value.Count;
                snapshot.PendingClients = pendingClients;
                // 断线重连追帧：改动水位 / 有界历史 / 拒绝与重放计数
                snapshot.ChangeSequence = m_changeSequence;
                snapshot.ChangeJournalCount = m_changeJournal.Count;
                snapshot.ChangeJournalOldestSequence = m_changeJournal.Count > 0
                    ? m_changeJournal.Peek().Sequence
                    : 0L;
                snapshot.ChangeJournalEvictions = m_changeJournalEvictions;
                snapshot.RejoinAccepted = m_rejoinAccepted;
                snapshot.RejoinRejected = m_rejoinRejected;
                snapshot.CatchUpRejected = m_catchUpRejected;
                snapshot.CatchUpRounds = m_catchUpRounds;
                snapshot.CatchUpChangesSent = m_catchUpChangesSent;
                snapshot.CatchUpActive = m_catchUps.Count;
                snapshot.CatchUpSendDeferrals = m_catchUpSendDeferrals;
                snapshot.CatchUpAckTimeouts = m_catchUpAckTimeouts;
                // 按需单块加载（客户端上报的边缘块）：口径与计数都来自同一份台账，不另立一份。
                snapshot.OnDemandLoadRequests = m_onDemand.Requests;
                snapshot.OnDemandLoadChunks = m_onDemand.Chunks;
                snapshot.OnDemandLoadServed = m_onDemand.Served;
                snapshot.OnDemandLoadTimeouts = m_onDemand.Timeouts;
                snapshot.OnDemandLoadRejected = m_onDemand.Rejected;
                snapshot.OnDemandLoadRejectedCap = m_onDemand.RejectedCap;
                snapshot.OnDemandLoadRejectedCooldown = m_onDemand.RejectedCooldown;
                snapshot.OnDemandLoadRejectedEngine = m_onDemand.RejectedEngine;
                snapshot.OnDemandLoadReleased = m_onDemand.ReleasedServed;
                snapshot.PendingOnDemand = m_onDemand.Pending;
                snapshot.OnDemandQueued = m_onDemand.Queued;
                snapshot.OnDemandActive = m_onDemand.Active;
                snapshot.OnDemandHeld = m_onDemand.Held;
                snapshot.OnDemandHeldReleasedUnderPressure = m_onDemand.ReleasedUnderPressure;
                snapshot.OnDemandDeferred = m_onDemand.Deferred;
                snapshot.OnDemandReportsDropped = m_onDemandReportsDropped;
                snapshot.OnDemandReportsTimedOut = m_onDemandReportsTimedOut;
            }
            snapshot.Loaded.AddRange(m_world.LoadedChunks);
            return snapshot;
        }

        /// <summary>
        /// 每 tick：重算窗口 + **补齐欠着的上报裁决** + 发送积压的重传分片（受背压限制，不丢）。
        ///
        /// 由主线程泵调用（<c>TerrainSyncModule.Tick</c> 每渲染帧一次）：这是"块后来才内容可用"
        /// 的唯一驱动点 —— 旧实现同样是每帧调 <c>MaintainRemoteTerrainLocations</c>，里面用
        /// <c>RefreshClientTerrainInterestChunks</c> 重查 <c>chunk.State &gt;= Valid</c> 再入队。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:460-516
        ///         （MaintainRemoteTerrainLocations 每帧调用；旧 :1303）
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:545-586
        ///         （RefreshClientTerrainInterestChunks：每帧重查块状态，Valid 才入队同步）
        /// </summary>
        public int Tick()
        {
            int work = 0;
            long nowMs = Environment.TickCount64;
            // 这一拍判定为"已经不可达"的玩家端（连接 Failed / 已关闭）：循环里只登记，
            // 出锁之后再抛 ClientLost —— 事件处理方要拆引擎实体（联机角色），不能在 m_gate 里做。
            List<KeyValuePair<int, string>> lost = null;
            // 加入审批超时的连接（收尾在锁内做，日志出锁写）。
            List<int> expiredApprovals = null;
            List<string> expiredApprovalTraces = null;
            lock (m_gate)
            {
                // ①' 按需单块加载（客户端上报的**边缘**块）：推进台账 —— 内容到位的**立刻裁决**（不用玩家移动），
                // 明确放弃的**从待裁决里摘掉**（这就是"待裁决永远挂着"的出口）。**不放大**任何角色的兴趣半径。
                // 放在客户端循环之前：本拍服务到的块在同一次 Tick 里就把裁决发出去。
                TickOnDemandLoads(nowMs, ref work);
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    State state = pair.Value;
                    if (!state.WindowValid) RefreshWindow(state);
                    // 加入审批超时：不让客户端无限等（主机没人点 Allow = 明确拒绝，绝不默认同意）。
                    // Later 挂起的连接暂停计时（ApprovalHeld）：用户口径 = 挂起是明确决策，
                    // 超时只对"弹着对话框没人管"的请求生效。
                    if (state.ApprovalPending && !state.ApprovalHeld && nowMs >= state.ApprovalDeadlineMs)
                    {
                        if (expiredApprovals == null) expiredApprovals = new List<int>();
                        expiredApprovals.Add(state.Id);
                    }
                    // ① 欠着裁决的上报：内容可用 → 真实主机摘要；还没好 → 限流重请 + 一行原因。
                    // 对端已经断了（没发 Bye 就断也一样）：这条线再也答不出去 —— 明确写一行，
                    // 并**保留在该身份的会话记录里**（重连后继续作答），免得对着一根断线每 5s 重请加载
                    // （客户端主动 Bye 的那条路在 OnFrame 里）。
                    // 同一时刻把它在**所有区块**上的欠更新标记一并去掉（明确写一行；这正是
                    // "退出联机后它的标记全部消失、再进来又是全 0" 的链路失效分支）。
                    bool silent = state.InboundFrames >= 64 && state.LastHeardMs > 0 &&
                        nowMs - state.LastHeardMs > PeerSilenceTimeoutMs;
                    if (!CanStillReachPeer(state) || silent)
                    {
                        // 每个 state 只清一次（清完置位）：一个断掉但还留在表里的客户端不会每帧扫全表。
                        if (!state.PendingMarksCleared)
                        {
                            state.PendingMarksCleared = true;
                            string gone = silent
                                ? "超过 " + (PeerSilenceTimeoutMs / 1000) +
                                    " 秒未收到任何帧（半开连接 / 闪退）"
                                : "连接已不可用（" + m_server.StateOf(state.Id) + "）";
                            state.LostReason = gone;
                            NoteSessionDeparture(state, gone);
                            // 这一端**再也不会回来了**（同一 clientId 不会重连）：把它从客户端表里摘掉，
                            // 否则状态行一直把死连接算成"客户端"（实机：clients=2 里有一个 4 小时前就断了），
                            // 并由上层按 ClientLost 清掉它的联机角色（角色实体会停在最后一个已应用的位置）。
                            if (lost == null) lost = new List<KeyValuePair<int, string>>();
                            lost.Add(new KeyValuePair<int, string>(state.Id, gone));
                        }
                    }
                    // 欠着裁决的上报**只由该身份当前的那条连接来驱动**：同一身份若还留着一条旧连接
                    // （新连接已经接过记录），旧连接不再扫同一份待办（否则同一 tick 会把队列扫两遍）。
                    if (state.Session == null || state.Session.CurrentClientId == state.Id)
                        work += ResolvePendingReports(state, nowMs);
                    // ② 发送积压的重传分片
                    for (int i = 0; i < m_options.ResyncPartsPerTick && state.PendingResync.Count > 0; i++)
                    {
                        PendingResyncJob job = state.PendingResync.Peek();
                        if (job.Parts == null)
                        {
                            // 在**发送这一刻**编码：快照包含此前已应用的全部改动（含刚发出的 CellChange）。
                            // 旧实现在裁决时就编码，之后的新改动会被旧快照盖回去（本轮修掉的窗口之一）。
                            job.EncodeSequence = m_changeSequence;
                            job.Parts = ChunkPayloadCodec.Encode(job.Key, m_world, out _,
                                job.EncodeSequence)[0];
                        }
                        if (job.Parts.Count == 0)
                        {
                            state.PendingResync.Dequeue();
                            m_resyncEmptySkipped++;
                            continue;
                        }
                        if (ChunkPayloadCodec.IsEmptySnapshotPart(job.Parts[job.Index]))
                        {
                            // 裁决后、编码前主机丢了这块内容（引擎卸载 / 回退）→ **绝不发零格快照**：
                            // 客户端"落地了却什么都没变"会再报 → 又 Mismatch → 1:1 空回传循环。
                            // 明确回 NotReady（客户端按退避重问），并计数 + 限流写一行原因。
                            state.PendingResync.Dequeue();
                            m_resyncEmptySkipped++;
                            SendVerdict(state, job.Key, DigestVerdict.NotReady, 0u);
                            NoteResyncEmptySkipped(state, job.Key);
                            work++;
                            continue;
                        }
                        if (!m_server.TrySend(state.Id, MessageType.ChunkResync, job.Parts[job.Index])) break;
                        state.ResyncPartsSent++;
                        m_resyncBytes += job.Parts[job.Index].Length;
                        job.Index++;
                        if (job.Index >= job.Parts.Count)
                        {
                            state.PendingResync.Dequeue();
                            // 整块快照已经全部发给它：这块如果还停在红（Mismatch），那条结论可能已经过期
                            // （客户端内容刚被主机内容覆盖），必须重新裁决（见 TickStaleVerdicts）。
                            NoteVerdictNeedsRejudge(state, job.Key,
                                "整块回传快照已发出（编码水位 " + job.EncodeSequence + "）");
                        }
                        work++;
                    }
                    // ④ 背压 / 追帧期间没发出去的改动：按改动历史顺序补发（每 tick 有界，顺序不变）。
                    work += RepairCellChangesLocked(state, nowMs);
                    // ④' 陈旧裁决自愈：内容已经补给它的那些块如果还停在红（Mismatch）→ 有界地要一份
                    // 新鲜摘要（重判成绿/蓝），到上限仍没有新裁决就明确清红。放在补发之后：本拍刚补上的
                    // 内容在下一拍进入自愈流程（退避给"客户端本来就会自发重报"的正常路径留宽限）。
                    // 只由该身份**当前**的那条连接驱动（旧连接的迟到帧不扫同一份台账）。
                    if (state.Session == null || state.Session.CurrentClientId == state.Id)
                        work += TickStaleVerdicts(state, nowMs);
                }
                if (lost != null)
                {
                    List<int> abortedApprovals = null;
                    for (int i = 0; i < lost.Count; i++)
                    {
                        m_clients.TryGetValue(lost[i].Key, out State state);
                        if (state != null)
                        {
                            ReleaseWindow(state);
                            if (state.ApprovalPending)
                                (abortedApprovals ??= new List<int>()).Add(lost[i].Key);
                        }
                        m_clients.Remove(lost[i].Key);
                    }
                    if (abortedApprovals != null)
                    {
                        for (int i = 0; i < abortedApprovals.Count; i++)
                            PendingApprovalAborted?.Invoke(abortedApprovals[i]);
                    }
                }
                // ②b 加入审批超时的连接：发 HelloAck.rejected + 移除状态（锁内收尾）。
                if (expiredApprovals != null)
                {
                    for (int i = 0; i < expiredApprovals.Count; i++)
                    {
                        if (!m_clients.TryGetValue(expiredApprovals[i], out State pending) ||
                            !pending.ApprovalPending) continue;
                        pending.ApprovalPending = false;
                        pending.PendingHello = null;
                        string reason = "The host did not approve your join request in time.";
                        if (expiredApprovalTraces == null) expiredApprovalTraces = new List<string>();
                        expiredApprovalTraces.Add("加入审批：clientId=" + pending.Id + "（" +
                            DisplayName(pending) + "）→ 超时拒绝：" + reason);
                        SendJoinRejectedLocked(pending, reason);
                        ReleaseWindow(pending);
                        m_clients.Remove(pending.Id);
                    }
                }
                // ③ 断线重连追帧：按每帧预算把重放数据发出去，发完发 Barrier（有界，不顶穿帧）。
                work += SendCatchUpBatchesLocked(nowMs);
            }
            // 出锁再抛：处理方会拆引擎实体（联机角色），不在 Terrain 层的锁里做。
            if (lost != null)
            {
                for (int i = 0; i < lost.Count; i++) ClientLost?.Invoke(lost[i].Key, lost[i].Value);
            }
            if (expiredApprovalTraces != null)
            {
                for (int i = 0; i < expiredApprovalTraces.Count; i++)
                    SessionTrace?.Invoke(expiredApprovalTraces[i]);
            }
            return work;
        }

        // ------------------------------------------------------------ 按需单块加载（主机；口径见 Terrain/OnDemandChunkLoads.cs）

        /// <summary>
        /// 推进按需单块加载台账，并把它这一拍的两个结果落到裁决上：
        ///   · 内容到位（served）→ 用**真实主机摘要**立刻裁决（Match / Mismatch + 回传排队），
        ///     不需要玩家移动；同一块被多个身份上报过就各答一条；
        ///   · 明确放弃（超时 / 引擎拒绝）→ 把这块的待裁决**摘掉**并如实计数（一行点名原因由台账写）。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void TickOnDemandLoads(long nowMs, ref int work)
        {
            m_onDemand.Tick(nowMs, m_onDemandStillNeeded, m_onDemandServed, m_onDemandTimedOut);
            for (int i = 0; i < m_onDemandServed.Count; i++)
                work += ResolveOnDemandServed(m_onDemandServed[i]);
            for (int i = 0; i < m_onDemandTimedOut.Count; i++)
                DropOnDemandReports(m_onDemandTimedOut[i]);
            m_onDemandLines.Clear();
            m_onDemand.DrainLines(m_onDemandLines);
            for (int i = 0; i < m_onDemandLines.Count; i++) OnDemandTrace?.Invoke(m_onDemandLines[i]);
        }

        /// <summary>
        /// 为一个"主机没有内容"的上报登记按需加载（**唯一入口**：同一块已有登记 → 只追加归属，
        /// 客户端重报不会产生第二个加载请求）。
        /// 返回 false = 主机**明确不为这块发起加载**（冷却 / 并发上限 / 引擎拒绝）：调用方不得把这条上报
        /// 登记成待裁决（那块没有任何人在加载，登记上就是永远裁决不了的黄框），只回 NotReady。
        /// 拒绝的计数与点名原因行由台账负责（已限流）。
        /// </summary>
        private bool RequestOnDemandLoad(PlayerSessionRecord session, ChunkKey key, long nowMs)
        {
            if (m_onDemand.Request(key, session.StableKey, nowMs, out _)) return true;
            m_onDemandReportsDropped++;
            return false;
        }

        /// <summary>
        /// 内容到位 → 用真实主机摘要立刻裁决（每个还欠着这块的身份各答一条）。
        /// 返回真正发出去的裁决条数（背压没发出去的不算，留在待裁决里下一拍补）。
        /// </summary>
        private int ResolveOnDemandServed(ChunkKey key)
        {
            if (!m_world.HasContents(key)) return 0;      // 台账判定后又被引擎回收：不裁决（下一拍再判）
            uint hostHash = HostDigest(key);
            int sent = ResolveServedForSessions(m_sessions.Values, key, hostHash);
            sent += ResolveServedForSessions(m_degradedSessions.Values, key, hostHash);
            return sent;
        }

        private int ResolveServedForSessions(IEnumerable<PlayerSessionRecord> sessions, ChunkKey key,
            uint hostHash)
        {
            int sent = 0;
            foreach (PlayerSessionRecord session in sessions)
            {
                if (!session.Pending.TryGetValue(key, out PendingReport report)) continue;
                State state = CurrentStateOf(session);
                // 这条身份已经没有可达连接：**保留**待裁决（重连后按常规扫描继续用真实摘要作答），
                // 这里不发（发也发不出去）。
                if (state == null || !CanStillReachPeer(state)) continue;
                DigestVerdict verdict = CompareReport(report.ClientHash, hostHash);
                if (!SendVerdict(state, key, verdict, hostHash))
                {
                    // 显式背压：没发出去就还欠着（条目留在待裁决里，下一拍按常规扫描补发真实摘要）。
                    continue;
                }
                ApplyVerdict(state, key, verdict);
                sent++;
            }
            return sent;
        }

        /// <summary>
        /// 按需加载明确放弃（超时 / 引擎拒绝）→ 把这块的待裁决**摘掉**：
        ///   · 绝不发 Match / Mismatch（主机确实没有内容，不做假裁决）；
        ///   · 状态从 Requested（黄）退回 None（不画框），否则那块黄框会永远挂着（这正是实机症状）；
        ///   · 客户端早已收到 NotReady（会按退避重问），重问若落在冷却外会重新尝试加载。
        /// </summary>
        private void DropOnDemandReports(ChunkKey key)
        {
            m_onDemandReportsTimedOut += DropPendingForSessions(m_sessions.Values, key);
            m_onDemandReportsTimedOut += DropPendingForSessions(m_degradedSessions.Values, key);
        }

        private int DropPendingForSessions(IEnumerable<PlayerSessionRecord> sessions, ChunkKey key)
        {
            int dropped = 0;
            foreach (PlayerSessionRecord session in sessions)
            {
                if (!session.Pending.Remove(key)) continue;
                ClearRequestedVerdict(session, key);
                dropped++;
            }
            return dropped;
        }

        /// <summary>
        /// 把这块的"黄（<see cref="ChunkVerifyState.Requested"/>）"退掉 —— 主机已明确不为它发起加载，
        /// 再挂着就是永远裁决不了的黄框。**不动**已有结论（绿 / 红 / 蓝）：那些是过去真的裁决过的结果，
        /// 不能被"这一次加载放弃"洗掉（实时地图只画最后一次结论）。
        /// </summary>
        private static void ClearRequestedVerdict(PlayerSessionRecord session, ChunkKey key)
        {
            if (session.Verdicts.TryGetValue(key, out byte value) &&
                value == (byte)ChunkVerifyState.Requested)
            {
                session.Verdicts.Remove(key);
            }
        }

        /// <summary>这条身份记录当前指向的连接状态（没有在线连接 / 句柄不匹配 → null）。</summary>
        private State CurrentStateOf(PlayerSessionRecord session)
        {
            if (session == null || session.CurrentClientId < 0) return null;
            return m_clients.TryGetValue(session.CurrentClientId, out State state) &&
                   ReferenceEquals(state.Session, session) ? state : null;
        }

        /// <summary>
        /// 这块现在还该不该留在主机内存里（按需加载的**回收判据**，交台账每 tick 判定）：
        ///   · 还有在线身份的**当前内存集合**里有它（客户端还拿着这块 → 主机要能随时取到权威内容）；
        ///   · 或者这条身份还欠着它的裁决；
        ///   · 或者某条连接上还排着它的回传快照（没发完就回收会退化成 NotReady → 重报 → 又加载）。
        /// 都不成立 → 台账过保留窗口后撤掉临时加载位置（主机不长期维持一圈更大的范围）。
        /// </summary>
        private bool HostStillNeedsOnDemandChunk(ChunkKey key)
        {
            foreach (KeyValuePair<int, State> pair in m_clients)
            {
                State state = pair.Value;
                if (state.Session != null && state.Session.MemoryChunks.Contains(key)) return true;
                if (state.PendingResync.Count == 0) continue;
                foreach (PendingResyncJob job in state.PendingResync)
                    if (job.Key.Equals(key)) return true;
            }
            foreach (KeyValuePair<string, PlayerSessionRecord> pair in m_sessions)
                if (pair.Value.Pending.ContainsKey(key)) return true;
            foreach (KeyValuePair<int, PlayerSessionRecord> pair in m_degradedSessions)
                if (pair.Value.Pending.ContainsKey(key)) return true;
            return false;
        }

        private void OnFrame(ReceivedFrame frame)
        {
            // 任何一帧都算"对端还活着"：主机判活超时（PeerSilenceTimeoutMs）的基础。
            // 闪退 / 半开连接可以长期保持 TCP Connected，旧版只查 server.StateOf 会把死连接
            // 一直算在线（实测退房后 4.5 分钟角色仍在地图）。
            lock (m_gate)
            {
                State heard = GetOrCreateState(frame.ClientId);
                if (heard != null)
                {
                    heard.LastHeardMs = Environment.TickCount64;
                    heard.InboundFrames++;
                }
            }
            m_messages.Dispatch(in frame);
        }

        /// <summary>
        /// 入站 Hello 的第一层：**先审批，再握手**（旧联机 mod 的 join request 流程）。
        /// 审批未通过时只记下这条连接，不产生任何角色/记录/世界传输副作用。
        /// </summary>
        private void HandleHello(State state, Hello hello)
        {
            if (hello == null) return;
            state.Kind = (ClientKind)(int)hello.Kind;
            state.Name = hello.Name ?? string.Empty;
            state.Identity = (hello.Identity ?? string.Empty).Trim();
            // 同一次房间内的断线重连不重复弹审批：Hello 声明的下载世界会话与主机会话一致
            // = 这个玩家本来就在这个房间里（旧联机 mod 的 rejoin grace 口径；重新"加入"才需要同意）。
            bool claimedSameRoomRejoin = hello.AlreadyHasWorld &&
                !string.IsNullOrEmpty(SessionId) &&
                string.Equals((hello.ClientSessionId ?? string.Empty).Trim(), SessionId,
                    StringComparison.Ordinal);
            if (claimedSameRoomRejoin && state.Kind == ClientKind.Player)
                state.JoinApproved = true;
            if (state.Kind == ClientKind.Player && JoinApproval != null)
            {
                if (state.ApprovalPending)
                {
                    // 客户端在等待期间又发了 Hello（重试）：刷新待审批内容，不重复弹窗。
                    state.PendingHello = hello;
                    return;
                }
                if (!state.JoinApproved)
                {
                    if (JoinApproval.AutoApprove)
                    {
                        state.JoinApproved = true;
                        SessionTrace?.Invoke("加入审批：clientId=" + state.Id + "（" + DisplayName(state) +
                            "）→ 设置 autoapprovejoins=true，自动同意");
                    }
                    else
                    {
                        state.ApprovalPending = true;
                        state.PendingHello = hello;
                        state.ApprovalDeadlineMs = Environment.TickCount64 + JoinApprovalTimeoutMs;
                        int clientId = state.Id;
                        string name = state.Name;
                        string identity = state.Identity;
                        SessionTrace?.Invoke("加入审批：clientId=" + clientId + "（" + DisplayName(state) +
                            "）等待主机决定（" + (JoinApprovalTimeoutMs / 1000) + " 秒超时）");
                        JoinApproval.RequestDecision(clientId, name, identity,
                            delegate (bool approved, string reason)
                            {
                                ResolveJoinApproval(clientId, approved, reason);
                            });
                        return;
                    }
                }
            }
            ContinueHello(state, hello);
        }

        /// <summary>
        /// 加入审批的出口（UI 线程 / 超时调用）：同意 → 继续完整握手；拒绝 → HelloAck.rejected + 移除连接状态。
        /// 每条连接只解决一次（重复调用 / 客户端已离开 = 忽略）。
        /// </summary>
        /// <summary>
        /// 加入审批的出口（UI 线程 / 超时调用）：approved=true 同意继续握手；approved=false 拒绝；
        /// <paramref name="approved"/>=true 且 reason=="hold" = Later（稍后）—— 挂起计时、条目
        /// 留在请求队列。每条连接只解决一次（重复调用 / 客户端已离开 = 忽略）。
        /// </summary>
        private void ResolveJoinApproval(int clientId, bool approved, string reason)
        {
            string trace;
            bool held = approved && reason == "hold";
            lock (m_gate)
            {
                if (!m_clients.TryGetValue(clientId, out State state) || !state.ApprovalPending) return;
                if (held)
                {
                    // Later：保持 ApprovalPending（连接继续等待），暂停超时（ApprovalHeld）。
                    state.ApprovalHeld = true;
                    trace = "加入审批：clientId=" + clientId + " → 主机稍后（Later），审批暂停计时，" +
                        "条目留在请求队列（IF 按钮可再处理）";
                }
                else
                {
                    state.ApprovalPending = false;
                    state.ApprovalHeld = false;
                    if (approved)
                    {
                        state.JoinApproved = true;
                        Hello hello = state.PendingHello;
                        state.PendingHello = null;
                        trace = "加入审批：clientId=" + clientId + "（" + DisplayName(state) + "）→ 主机同意，继续握手";
                        if (hello != null) ContinueHello(state, hello);
                    }
                    else
                    {
                        state.PendingHello = null;
                        trace = "加入审批：clientId=" + clientId + "（" + DisplayName(state) + "）→ 主机拒绝：" +
                            (string.IsNullOrEmpty(reason) ? "(未说明原因)" : reason);
                        SendJoinRejectedLocked(state,
                            string.IsNullOrEmpty(reason) ? "主机拒绝了加入请求" : reason);
                        ReleaseWindow(state);
                        m_clients.Remove(clientId);
                    }
                }
            }
            // 出锁再写日志（事件处理方可能碰引擎对象）。
            SessionTrace?.Invoke(trace);
        }

        /// <summary>明确拒绝：给客户端 HelloAck.rejected + 原因（它据此中止加入并显示原因，而不是静默重试）。</summary>
        private void SendJoinRejectedLocked(State state, string reason)
        {
            var ack = new HelloAck
            {
                Version = 1,
                ClientId = state.Id,
                Room = "SuMP-World",
                Flags = 0,
                MaxRadius = 20,
                HostIdentity = Identity ?? string.Empty,
                HostName = Name ?? string.Empty,
                Rejected = true,
                RejectReason = reason ?? string.Empty
            };
            m_server.TrySend(state.Id, MessageType.HelloAck, ack.ToByteArray());
        }

        private void ContinueHello(State state, Hello hello)
        {
            state.Kind = (ClientKind)(int)hello.Kind;
            state.Name = hello.Name ?? string.Empty;
            state.Radius = Clamp(hello.Radius);
            // 对端身份（UserId.dat）：空值 = 身份不可用，走按 clientId 的退化路径并明确报一行。
            state.Identity = (hello.Identity ?? string.Empty).Trim();
            // 对端是否**此刻正在游玩**这个下载世界（断线重连的声明）。
            bool claimedRejoin = hello.AlreadyHasWorld;
            // 下载世界登记的主机会话标识 + 上次应用的改动序号（空会话 ⇒ 不接受重连）。
            state.ClientSessionId = (hello.ClientSessionId ?? string.Empty).Trim();
            state.HelloLastApplied = hello.LastAppliedSequence;
            // 对端本地已有的角色记录 hash（重连时用来做"哪些角色记录变了"的 diff）。
            state.KnownRecords.Clear();
            int knownRecordRejects = 0;
            for (int i = 0; i < hello.KnownRecords.Count; i++)
            {
                KnownRecordHash item = hello.KnownRecords[i];
                if (state.KnownRecords.Count >= MaxKnownRecordHashes)
                {
                    knownRecordRejects++;
                    continue;
                }
                string recordKey = (item?.RecordKey ?? string.Empty).Trim();
                ulong hash = item?.Hash ?? 0UL;
                if (recordKey.Length == 0 || hash == 0UL)
                {
                    knownRecordRejects++;   // 空键 / 0 = 未知，绝不冒充"已知内容"
                    continue;
                }
                state.KnownRecords.Add(new RecordHashRef(recordKey, hash));
            }
            if (knownRecordRejects > 0)
            {
                RejoinTrace?.Invoke("握手（clientId=" + state.Id + "）：已知角色记录 hash 有 " +
                    knownRecordRejects + " 条被拒绝（空键 / hash=0 / 超过上限 " +
                    MaxKnownRecordHashes + "）→ 只按剩下的 " + state.KnownRecords.Count +
                    " 条做 diff（其余按变化处理，绝不跳过重发）");
            }

            // 重连的唯一判定（会话一致 + 序号仍在保留历史内）。不成立 ⇒ 首次加入 ⇒ 世界传输会重发整图。
            string rejectReason = null;
            bool accepted = false;
            long head;
            long oldest;
            lock (m_gate)
            {
                TrimChangeJournalLocked(Environment.TickCount64);
                head = m_changeSequence;
                oldest = FirstRetainedSequenceLocked();
                accepted = TryAcceptRejoinLocked(claimedRejoin, state.ClientSessionId,
                    state.HelloLastApplied, head, oldest, out rejectReason);
                state.Rejoin = accepted;
                state.RejoinReason = accepted ? "会话一致且序号在保留历史内" : rejectReason;
                if (accepted) m_rejoinAccepted++;
                else if (claimedRejoin) m_rejoinRejected++;
            }
            state.WindowValid = false;
            // 新一次握手 = 这一端又回来了：允许"链路失效清理"在它下次断掉时再跑一次（标记本身不补，
            // 因为加入时的整图就是最新基线 —— 用户口径：再进来是全 0）。
            state.PendingMarksCleared = false;
            state.SessionlessReportLogged = false;
            state.SessionlessClientChunksLogged = false;
            state.CatchUpTarget = accepted ? state.HelloLastApplied : -1L;
            // 这条连接的"已送达序号"从客户端声明的上次应用序号起算（重连）或从历史基线起算（首次加入）。
            // 补发 / 追帧都以它为起点；只有**真的发出去**才推进（见 BroadcastCellChange / RepairCellChangesLocked）。
            state.LastDeliveredSequence = accepted
                ? Math.Max(0L, state.HelloLastApplied)
                : Math.Max(0L, Math.Min(state.HelloLastApplied, head));
            state.DeliveryIndex = 0L;
            state.RepairFromSequence = -1L;
            state.RepairUnavailableLogged = false;
            // ---- 资料闸门（旧实现的 SCMP_PROFILE_REQUIRED；Source: ScMultiplayerPlayerHealthAndIngress.cs:941-961）----
            // 主机有记录 → 采用记录里的名字/性别/皮肤/衣服；没有记录且 Hello 没带完整资料 →
            // ProfileRequired=true，本次**不下发世界**（客户端先建角色，再带 Hello.profile 重新握手）。
            HelloProfileDecision profileDecision = null;
            PlayerProfileSnapshot claimedProfile = null;
            if (state.Kind == ClientKind.Player && ProfileGate != null)
            {
                int claimedClientId = -1;
                string claimedIdentity = string.Empty;
                bool claimedIsHost = false;
                bool claimedHasCharacter = false;
                claimedProfile = PlayerProfileCodec.FromMessage(hello.Profile, out claimedClientId,
                    out claimedIdentity, out claimedIsHost, out claimedHasCharacter);
                try
                {
                    profileDecision = ProfileGate.AcceptHello(state.Id, state.Identity, state.Name,
                        claimedProfile, claimedRejoin);
                }
                catch (Exception e)
                {
                    // 资料裁决失败 = 明确失败：本连接不下发世界，也不伪造一个角色。
                    profileDecision = new HelloProfileDecision
                    {
                        ProfileRequired = true,
                        Reason = "资料裁决异常：" + e.GetType().Name + " " + e.Message
                    };
                }
                if (profileDecision != null)
                {
                    state.ProfileRequired = profileDecision.ProfileRequired;
                    state.ProfileRequiredReason = profileDecision.Reason ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(profileDecision.Name)) state.Name = profileDecision.Name;
                    if (state.ProfileRequired) state.Rejoin = false;
                }
            }
            AttachSession(state);
            m_server.SetPeerIdentity(state.Id, state.Kind, state.Name);
            // 每握手一行：声明了重连没有 / 接受没有 / 依据（会话短标识 + 序号范围）。
            // 这不是热路径（握手一次一行），上层照原样写。
            RejoinTrace?.Invoke("重连判定（clientId=" + state.Id + " · " +
                PlayerSessionKeys.ShortIdentity(state.Identity) + "）：声明=" + (claimedRejoin ? "是" : "否") +
                " · 接受=" + (accepted ? "是" : "否") +
                " · 客户端会话=" + SessionIdentity.Short(state.ClientSessionId) +
                " · 主机会话=" + SessionIdentity.Short(SessionId) +
                " · 上次应用序号=" + state.HelloLastApplied +
                " · 保留历史=[" + (oldest > head ? "empty" : oldest + ".." + head) + "]" +
                " · 依据=" + state.RejoinReason +
                (state.ProfileRequired ? " · 角色资料=要求先创建（" + state.ProfileRequiredReason + "）" : string.Empty));
            if (state.ProfileRequired)
            {
                // 没有角色就不发世界：世界传输/生物等模块不能把"连上了"当成"可以进世界"。
                RejoinTrace?.Invoke("握手（clientId=" + state.Id + "）：主机没有该身份的角色记录，" +
                    "Hello 也没有完整资料 → helloAck.profile_required=true，本次不下发世界；" +
                    "客户端应打开角色创建界面后重新握手（旧 SCMP_PROFILE_REQUIRED 流程）");
            }
            else
            {
                ClientJoined?.Invoke(state.Id,
                    string.IsNullOrEmpty(state.Name) ? ("client#" + state.Id) : state.Name,
                    state.Identity ?? string.Empty);
            }
            PlayerProfileSnapshot hostProfile = null;
            string hostProfileReason = null;
            if (ProfileGate != null && !ProfileGate.TryGetHostProfile(out hostProfile, out hostProfileReason))
            {
                hostProfile = null;
                RejoinTrace?.Invoke("主机资料不可用（" + (hostProfileReason ?? "未知原因") +
                    "）→ HelloAck.host_profile 为空；客户端会明确记录\"主机外观未知\"（不伪造性别/皮肤）");
            }
            var ack = new HelloAck
            {
                Version = 1,
                ClientId = state.Id,
                Room = "SuMP-World",
                Flags = 0,
                MaxRadius = 20,
                // 主机身份 + 主机名字（客户端建"主机角色"必须按身份绑定，不能用 index 兜底）。
                HostIdentity = Identity ?? string.Empty,
                HostName = Name ?? string.Empty,
                // 主机自己的资料：客户端建主机化身时直接用真实名字/性别/皮肤/衣服。
                HostProfile = hostProfile != null
                    ? PlayerProfileCodec.ToMessage(0, Identity ?? string.Empty, hostProfile, true, true)
                    : null,
                // 主机给本客户端分配的角色记录资料（没有记录 / 要求先创建时为空）。
                AssignedProfile = profileDecision?.Profile != null
                    ? PlayerProfileCodec.ToMessage(state.Id, state.Identity ?? string.Empty,
                        profileDecision.Profile, false, profileDecision.HasCharacter)
                    : null,
                ProfileRequired = state.ProfileRequired,
                ProfileRequiredReason = state.ProfileRequiredReason ?? string.Empty,
                // 主机角色记录里已有这个身份的自定义皮肤资源 → 客户端不用重新上传。
                AssignedSkinAssetPresent = profileDecision?.AssignedSkinAssetPresent == true,
                // 2026-10-27：把客户端角色记录里的"最后离线位置 + 最后睡觉复活点"随 HelloAck 下发。
                // 客户端本地角色是下载世界里的新实体（Players 段是主机存档角色），不带上这份
                // 记录位置就会出生在主机角色旁边（实机 bug）。
                HasAssignedPlacement = profileDecision != null && profileDecision.HasPlacement,
                AssignedPosition = profileDecision != null && profileDecision.HasPlacement
                    ? new SuMultiplayer.Protocol.PlayerPlacement
                    {
                        X = profileDecision.Position.X,
                        Y = profileDecision.Position.Y,
                        Z = profileDecision.Position.Z
                    }
                    : null,
                AssignedSpawnPosition = profileDecision != null && profileDecision.HasPlacement
                    ? new SuMultiplayer.Protocol.PlayerPlacement
                    {
                        X = profileDecision.SpawnPosition.X,
                        Y = profileDecision.SpawnPosition.Y,
                        Z = profileDecision.SpawnPosition.Z
                    }
                    : null,
                HasAssignedState = profileDecision != null && profileDecision.HasPlacement,
                AssignedIsFlying = profileDecision?.IsFlying == true,
                AssignedIsCrouching = profileDecision?.IsCrouching == true,
                // 主机会话标识 + 是否接受重连 + 当前改动水位 + 依据。
                // 客户端据此：① 记住"主机当前会话"（下一次重连的判定）；② 收到拒绝时明确作废旧会话并重下整图。
                SessionId = SessionId ?? string.Empty,
                RejoinAccepted = accepted,
                HeadSequence = head,
                RejoinReason = state.RejoinReason ?? string.Empty
            };
            m_server.TrySend(state.Id, MessageType.HelloAck, ack.ToByteArray());
        }

        /// <summary>
        /// 重连接受的唯一判定：会话标识一致 + 序号仍在保留历史内（含"已经到最新"）。
        /// 返回 false 时 <paramref name="reason"/> 是写给客户端 / 日志的明确原因。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:784-797
        ///         （lastApplied &lt; 0 / &gt; head / &lt; unavailableEnd 且未被 bufferedRanges 覆盖 → ResyncRequired）
        /// </summary>
        private bool TryAcceptRejoinLocked(bool claimedRejoin, string clientSessionId, long lastApplied,
            long head, long oldest, out string reason)
        {
            if (!claimedRejoin)
            {
                reason = "未声明重连（首次加入 / Play 屏进入）";
                return false;
            }
            if (string.IsNullOrEmpty(SessionId))
            {
                reason = "主机没有会话标识（不是有效的房间会话）→ 不接受重连";
                return false;
            }
            if (string.IsNullOrEmpty(clientSessionId))
            {
                reason = "客户端没有下载世界的会话标识（旧世界 / 旧构建）→ 首次加入（整图重下）";
                return false;
            }
            if (!string.Equals(clientSessionId, SessionId, StringComparison.Ordinal))
            {
                reason = "会话不一致（客户端 " + SessionIdentity.Short(clientSessionId) +
                    " / 主机 " + SessionIdentity.Short(SessionId) +
                    "：主机重启或换了房间）→ 首次加入（整图重下）";
                return false;
            }
            if (lastApplied < 0 || lastApplied > head)
            {
                reason = "上次应用序号未知（" + lastApplied + "，当前水位 " + head +
                    "）→ 首次加入（整图重下）";
                return false;
            }
            if (oldest <= head && lastApplied < oldest - 1)
            {
                reason = "上次应用序号 " + lastApplied + " 已不在保留历史内（最旧 " + oldest +
                    "，当前水位 " + head + "）→ 整图重下";
                return false;
            }
            reason = "会话一致 · 上次应用序号 " + lastApplied + " 在保留历史内（最旧 " +
                (oldest > head ? "empty" : oldest.ToString()) + "，当前水位 " + head + "）";
            return true;
        }

        private long FirstRetainedSequenceLocked() =>
            m_changeJournal.Count > 0 ? m_changeJournal.Peek().Sequence : m_changeSequence + 1;

        // ------------------------------------------------------------ 追帧（主机侧）

        /// <summary>
        /// 主机收到追帧控制帧。客户端在 HelloAck 确认"重连被接受"之后发 Request（携带上次应用序号）；
        /// 主机从有界历史里重放它之后的改动（复用 CellChange 帧，末尾带每个条目的序号），
        /// 最后发 Barrier；客户端应用到位后回报 Applied。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:360-426
        ///         （HandleTerrainRecoveryMessage：Request → SendHostTerrainRecoveryRound；
        ///          Acknowledge → 未到水位再发一轮，到了就 Ready）
        /// </summary>
        private void HandleCatchUpMessage(State state, PT.CatchUp message)
        {
            var stage = (CatchUpStage)(int)message.Phase;
            switch (stage)
            {
                case CatchUpStage.Request:
                    StartCatchUpLocked(state, message.LastApplied);
                    break;
                case CatchUpStage.Applied:
                    HandleCatchUpAppliedLocked(state, message.LastApplied, message.Head);
                    break;
                default:
                    // 客户端不该发 Begin/Barrier/Rejected；明确记一行，不静默忽略。
                    CatchUpProblem?.Invoke("客户端 clientId=" + state.Id + " 发来不该由客户端发送的追帧阶段 " +
                        stage + " → 丢弃（协议方向错误，计入日志）");
                    break;
            }
        }

        /// <summary>
        /// 开始一轮追帧。调用方已持有 m_gate（OnFrame / Tick 都在锁内）。
        /// 范围判定与旧实现一致：lastApplied &lt; 0 / &gt; head / 早于最旧保留序号 ⇒ **明确拒绝**（整图重下）。
        /// </summary>
        private void StartCatchUpLocked(State state, long lastApplied)
        {
            long nowMs = Environment.TickCount64;
            TrimChangeJournalLocked(nowMs);
            long head = m_changeSequence;
            long oldest = FirstRetainedSequenceLocked();
            if (!state.Rejoin)
            {
                RejectCatchUpLocked(state, lastApplied, head, oldest,
                    "这条连接在 Hello 时没有被接受为重连（会话不一致 / 未声明）→ 必须整图重下", true);
                return;
            }
            if (lastApplied < 0 || lastApplied > head)
            {
                RejectCatchUpLocked(state, lastApplied, head, oldest,
                    "上次应用序号 " + lastApplied + " 未知（当前水位 " + head + "）→ 必须整图重下", true);
                return;
            }
            if (oldest <= head && lastApplied < oldest - 1)
            {
                RejectCatchUpLocked(state, lastApplied, head, oldest,
                    "上次应用序号 " + lastApplied + " 已不在保留历史内（最旧 " + oldest +
                    "，当前水位 " + head + "）→ 必须整图重下", true);
                return;
            }
            var job = new CatchUpJob
            {
                ClientId = state.Id,
                RequestedSequence = lastApplied,
                HeadSequence = head,
                FirstRetainedSequence = oldest,
                StartedMs = nowMs
            };
            foreach (ChangeJournalEntry entry in m_changeJournal)
            {
                if (entry.Sequence > lastApplied) job.Entries.Add(entry);
            }
            m_catchUps[state.Id] = job;
            state.CatchUpTarget = head;
            m_catchUpRounds++;
            // Begin 先把"这一轮的范围"告诉客户端（可审计：请求序号 / 水位 / 最旧 / 条数）。
            // 发送失败 = 显式背压：这一轮不成立，客户端会按重试节奏再请求（不假装已开始）。
            if (!SendCatchUpControl(state.Id, CatchUpStage.Begin, lastApplied, head, oldest,
                job.Entries.Count, "ok"))
            {
                m_catchUps.Remove(state.Id);
                state.CatchUpTarget = -1L;
                CatchUpProblem?.Invoke("追帧 Begin 发送失败（显式背压 / 连接不可用，clientId=" + state.Id +
                    "）→ 这一轮不成立；客户端会按重试节奏再次请求（不假装已开始）");
                return;
            }
            CatchUpTrace?.Invoke("追帧开始：clientId=" + state.Id +
                " · 请求序号=" + lastApplied + " · 水位=" + head +
                " · 保留历史=[" + (oldest > head ? "empty" : oldest + ".." + head) + "]" +
                " · 待重放改动=" + job.Entries.Count + " 条（≤" + m_options.CatchUpBatchEntries +
                " 条/批 · 每帧 ≤" + m_options.CatchUpBatchesPerTick + " 批）");
        }

        /// <summary>
        /// 客户端回报"已应用到位"。到水位 ⇒ 本轮完成并写一行；未到 ⇒ 从它报的序号再发一轮
        /// （旧实现 Acknowledge 分支：message.LastAppliedSequence &lt; head 就再发一轮）。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:374-400
        /// </summary>
        private void HandleCatchUpAppliedLocked(State state, long applied, long reportedHead)
        {
            if (!m_catchUps.TryGetValue(state.Id, out CatchUpJob job))
            {
                CatchUpTrace?.Invoke("收到 clientId=" + state.Id + " 的追帧完成回报（applied=" + applied +
                    " · head=" + reportedHead + "），但本端没有在途追帧任务 → 忽略（可能是上一轮的迟到回报）");
                return;
            }
            if (applied >= job.HeadSequence)
            {
                m_catchUps.Remove(state.Id);
                state.CatchUpTarget = -1L;
                CatchUpTrace?.Invoke("追帧完成：clientId=" + state.Id + " · 请求序号=" +
                    job.RequestedSequence + " · 水位=" + job.HeadSequence + " · 客户端已应用到=" + applied +
                    " · 重放改动=" + job.Entries.Count + " 条 / " + job.BatchCount + " 批");
                return;
            }
            CatchUpTrace?.Invoke("追帧回报未到水位：clientId=" + state.Id + " · applied=" + applied +
                " &lt; head=" + job.HeadSequence + " → 从 applied 再发一轮");
            StartCatchUpLocked(state, applied);
        }

        private void RejectCatchUpLocked(State state, long requested, long head, long oldest, string reason,
            bool count)
        {
            m_catchUps.Remove(state.Id);
            state.CatchUpTarget = -1L;
            if (count) m_catchUpRejected++;
            if (!SendCatchUpControl(state.Id, CatchUpStage.Rejected, requested, head, oldest, 0, reason))
            {
                // 拒绝帧没能发出去：客户端会重试请求 / 最终按"主机没有回应"写一行；这里也明确记一行。
                CatchUpProblem?.Invoke("追帧拒绝帧发送失败（显式背压 / 连接不可用，clientId=" + state.Id +
                    "）→ 客户端可能收不到拒绝；客户端会重试请求（不静默）");
            }
            // 拒绝就是拒绝：写 Warning 级别的明确原因（上层照原样写成 Warning）。
            CatchUpProblem?.Invoke("追帧拒绝（clientId=" + state.Id + "）：请求序号=" + requested +
                " · 水位=" + head + " · 保留历史=[" + (oldest > head ? "empty" : oldest + ".." + head) +
                "] · 原因=" + reason);
        }

        private bool SendCatchUpControl(int clientId, CatchUpStage stage, long lastApplied, long head,
            long firstRetained, int changeCount, string reason)
        {
            var message = new PT.CatchUp
            {
                Phase = (PT.CatchUpPhase)(int)stage,
                LastApplied = lastApplied,
                Head = head,
                FirstRetained = firstRetained,
                ChangeCount = changeCount,
                Reason = reason ?? string.Empty
            };
            return m_server.TrySend(clientId, MessageType.CatchUp, message.ToByteArray());
        }

        /// <summary>
        /// 每 tick 按预算发送追帧数据（调用方已持有 m_gate）：每批 ≤ CatchUpBatchEntries 条，
        /// 每 tick ≤ CatchUpBatchesPerTick 批；发完发 Barrier（带本轮水位）。
        /// 发送失败 = 显式背压：任务保留，下一 tick 继续（不丢、不假装发完）。
        /// </summary>
        private int SendCatchUpBatchesLocked(long nowMs)
        {
            if (m_catchUps.Count == 0) return 0;
            int work = 0;
            List<int> finished = null;
            foreach (KeyValuePair<int, CatchUpJob> pair in m_catchUps)
            {
                CatchUpJob job = pair.Value;
                int batches = 0;
                while (batches < m_options.CatchUpBatchesPerTick && job.Index < job.Entries.Count)
                {
                    int count = Math.Min(m_options.CatchUpBatchEntries, job.Entries.Count - job.Index);
                    m_clients.TryGetValue(job.ClientId, out State state);
                    long prevIndex = state != null ? state.DeliveryIndex : -1L;
                    long index = prevIndex >= 0 ? prevIndex + 1 : -1L;
                    byte[] payload = EncodeCatchUpBatch(job.Entries, job.Index, count, prevIndex, index);
                    if (!m_server.TrySend(job.ClientId, MessageType.CellChange, payload))
                    {
                        // 显式背压：这一批没发出去，任务保留，下一 tick 从同一位置继续。
                        m_catchUpSendDeferrals++;
                        break;
                    }
                    long lastSequence = 0L;
                    for (int i = 0; i < count; i++)
                    {
                        ChangeJournalEntry sentEntry = job.Entries[job.Index + i];
                        if (sentEntry.Sequence > lastSequence) lastSequence = sentEntry.Sequence;
                    }
                    if (state != null)
                    {
                        if (index >= 0) state.DeliveryIndex = index;
                        if (lastSequence > state.LastDeliveredSequence)
                            state.LastDeliveredSequence = lastSequence;
                        state.CellChangesSent += count;
                        if (state.Session != null)
                        {
                            // 追帧重放同样会把"断线期间漏掉的改动"补进客户端：这些块若还是红，必须重新裁决。
                            for (int i = 0; i < count; i++)
                                NoteVerdictNeedsRejudge(state, job.Entries[job.Index + i].Key,
                                    "追帧重放改动（序号 " + job.Entries[job.Index + i].Sequence + "）");
                        }
                    }
                    job.Index += count;
                    job.BatchCount++;
                    batches++;
                    work++;
                    m_catchUpChangesSent += count;
                }
                if (!job.BarrierSent && job.Index >= job.Entries.Count)
                {
                    if (!SendCatchUpControl(job.ClientId, CatchUpStage.Barrier, job.RequestedSequence,
                        job.HeadSequence, job.FirstRetainedSequence, job.Entries.Count, "ok"))
                    {
                        // 屏障没发出去：任务保留，下一 tick 重试（不假装重放已完成）。
                        m_catchUpSendDeferrals++;
                        if (m_lastCatchUpBarrierFailureLogMs == 0 ||
                            nowMs - m_lastCatchUpBarrierFailureLogMs >= m_options.CatchUpLogIntervalMs)
                        {
                            m_lastCatchUpBarrierFailureLogMs = nowMs;
                            CatchUpProblem?.Invoke("追帧 Barrier 发送失败（显式背压 / 连接不可用，clientId=" +
                                job.ClientId + "）→ 任务保留，下一 tick 重试（不假装重放已完成）");
                        }
                    }
                    else
                    {
                        job.BarrierSent = true;
                        job.BarrierSentMs = nowMs;
                        CatchUpTrace?.Invoke("追帧重放已发完：clientId=" + job.ClientId +
                        " · 请求序号=" + job.RequestedSequence + " · 水位=" + job.HeadSequence +
                        " · 已发 " + job.Entries.Count + " 条 / " + job.BatchCount + " 批 → Barrier 已发（等客户端 Applied）");
                    }
                }
                if (job.BarrierSent && nowMs - job.BarrierSentMs > m_options.CatchUpAckTimeoutMs)
                {
                    m_catchUpAckTimeouts++;
                    CatchUpProblem?.Invoke("追帧等不到客户端 Applied：clientId=" + job.ClientId +
                        " · 水位=" + job.HeadSequence + " · 已等 " +
                        (nowMs - job.BarrierSentMs) + "ms（上限 " + m_options.CatchUpAckTimeoutMs +
                        "ms）→ 放弃这一轮（客户端仍按保留历史在下次重连时再请求；绝不假装完成）");
                    (finished ?? (finished = new List<int>())).Add(job.ClientId);
                }
            }
            if (finished != null)
            {
                for (int i = 0; i < finished.Count; i++) m_catchUps.Remove(finished[i]);
            }
            return work;
        }

        /// <summary>
        /// 编码一条实时 CellChange：旧字段（count + 条目）+ 本版末尾追加的序号尾字段。
        /// 尾字段：<c>Int32 seqCount(=count) + Int64[seqCount]</c>。旧客户端读完条目就停（忽略尾字段）。
        /// </summary>
        private static byte[] EncodeCellChange(ChunkKey key, CellEdit edit, long sequence,
            long prevDeliveryIndex, long deliveryIndex, bool replay)
        {
            var message = new PT.CellChange
            {
                Flags = replay ? 1u : 0u,
                PrevDeliveryIndex = prevDeliveryIndex,
                DeliveryIndex = deliveryIndex
            };
            message.Entries.Add(new PT.CellChange.Types.Entry
            {
                Key = new PT.ChunkKey { X = key.X, Z = key.Z },
                Edit = ToProtoCellEdit(edit)
            });
            message.Sequences.Add(sequence);
            return message.ToByteArray();
        }

        private static PT.CellEdit ToProtoCellEdit(CellEdit edit) =>
            new PT.CellEdit
            {
                X = edit.X,
                Y = edit.Y,
                Z = edit.Z,
                Contents = edit.Contents,
                Data = edit.Data
            };

        /// <summary>
        /// 编码一批追帧改动：复用 CellChange 载荷，末尾追加 <c>Int32 seqCount + Int64[seqCount]</c>。
        /// 旧读取器读完 count 条就停（尾字段被忽略）；新读取器靠尾字段推进"上次应用序号"。
        /// </summary>
        private static byte[] EncodeCatchUpBatch(List<ChangeJournalEntry> entries, int from, int count,
            long prevDeliveryIndex, long deliveryIndex)
        {
            var message = new PT.CellChange
            {
                // 追帧重放同样走"一条连接一条流"的投递序号（flags bit0 = 1）。
                Flags = 1u,
                PrevDeliveryIndex = prevDeliveryIndex,
                DeliveryIndex = deliveryIndex
            };
            for (int i = 0; i < count; i++)
            {
                ChangeJournalEntry entry = entries[from + i];
                message.Entries.Add(new PT.CellChange.Types.Entry
                {
                    Key = new PT.ChunkKey { X = entry.Key.X, Z = entry.Key.Z },
                    Edit = ToProtoCellEdit(entry.Edit)
                });
                message.Sequences.Add(entry.Sequence);
            }
            return message.ToByteArray();
        }

        /// <summary>记一条权威改动进有界历史，并按时间 / 条数上限裁剪（裁掉数计数 + 限流一行原因）。</summary>
        private void RecordChangeLocked(ChunkKey key, CellEdit edit, long nowMs)
        {
            m_changeJournal.Enqueue(new ChangeJournalEntry
            {
                Sequence = ++m_changeSequence,
                Key = key,
                Edit = edit,
                CreatedMs = nowMs
            });
            TrimChangeJournalLocked(nowMs);
        }

        /// <summary>
        /// 有界改动历史裁剪：超过 <see cref="TerrainSyncOptions.CatchUpRetentionMs"/> 或
        /// <see cref="TerrainSyncOptions.CatchUpMaxJournalEntries"/> 就从最旧开始丢。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:328-358
        ///         （TrimHostTerrainJournalLocked：60 秒保留 + 在途追帧目标保护）
        /// 这里不再需要"保护在途目标"：一轮追帧开始时已经把要重放的条目**快照**进任务，
        /// 任务发送期间历史被裁掉不会影响这一轮的数据完整性（条数上界仍然成立）。
        /// </summary>
        private void TrimChangeJournalLocked(long nowMs)
        {
            long evicted = 0;
            while (m_changeJournal.Count > 0)
            {
                ChangeJournalEntry oldest = m_changeJournal.Peek();
                bool tooOld = nowMs - oldest.CreatedMs > m_options.CatchUpRetentionMs;
                bool tooMany = m_changeJournal.Count > Math.Max(1, m_options.CatchUpMaxJournalEntries);
                if (!tooOld && !tooMany) break;
                m_changeJournal.Dequeue();
                evicted++;
            }
            if (evicted == 0) return;
            m_changeJournalEvictions += evicted;
            if (m_lastChangeJournalEvictionLogMs == 0 ||
                nowMs - m_lastChangeJournalEvictionLogMs >= m_options.CatchUpLogIntervalMs)
            {
                m_lastChangeJournalEvictionLogMs = nowMs;
                CatchUpProblem?.Invoke("改动历史裁剪：本次裁掉 " + evicted + " 条（累计 " +
                    m_changeJournalEvictions + " 条 · 当前保留 " + m_changeJournal.Count +
                    " 条 · 上限 " + m_options.CatchUpMaxJournalEntries + " 条 / " +
                    m_options.CatchUpRetentionMs + "ms）→ 早于最旧保留序号的重连会明确要求整图重下");
            }
        }

        private long m_catchUpSendDeferrals;
        private long m_catchUpAckTimeouts;
        private long m_lastCatchUpBarrierFailureLogMs;

        /// <summary>
        /// 把这条连接挂到它的**身份记录**上（懒创建 / 重连复用 / 退化路径 / Debug 无状态），
        /// 一行一条明确日志。规则（唯一入口，别处不许再建校验状态）：
        ///   · <see cref="ClientKind.Debug"/>：只读观察者，**没有校验状态**（不发摘要）；
        ///   · Player + 身份为空：**退化路径** —— 按 clientId 单独一条记录 + <see cref="SessionProblem"/>
        ///     明确报错（它重连会拿到新记录，上报/裁决不延续；这是身份缺失的后果，不是静默兜底）；
        ///   · Player + 有身份：按 Trim 后的身份在 <see cref="m_sessions"/> 里懒创建，
        ///     同身份重连（新 clientId）**复用**同一条记录（只把 CurrentClientId 指向新句柄）。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void AttachSession(State state)
        {
            PlayerSessionRecord previous = state.Session;
            if (state.Kind == ClientKind.Debug)
            {
                if (previous != null)
                {
                    DetachSession(state, "对端改以 Debug 只读方式握手");
                }
                // Debug 端不建任何校验状态：它只观察，不发摘要（发了也会被 HandleDigestReport 明确忽略）。
                return;
            }
            if (state.Identity.Length == 0)
            {
                if (previous != null && !previous.Degraded) DetachSession(state, "Hello 的身份变成空串");
                if (!m_degradedSessions.TryGetValue(state.Id, out PlayerSessionRecord record))
                {
                    record = new PlayerSessionRecord
                    {
                        Identity = string.Empty,
                        StableKey = PlayerSessionKeys.DegradedKeyOf(state.Id),
                        CurrentClientId = state.Id,
                        Radius = state.Radius,
                        DisplayName = DisplayName(state)
                    };
                    m_degradedSessions[state.Id] = record;
                    state.Session = record;
                    SessionProblem?.Invoke("玩家端 clientId=" + state.Id + "（" + DisplayName(state) +
                        "）的 Hello 没有带身份（UserId.dat 为空 / 对端是旧构建）→ 这是**退化路径**：" +
                        "本次按 clientId 单独建一条校验记录（键 " + record.StableKey + "），不与任何其他端合并；" +
                        "它断线重连会拿到新记录，上报与裁决**不会延续**（身份不可用的直接后果）");
                    return;
                }
                record.CurrentClientId = state.Id;
                record.DisplayName = DisplayName(state);
                record.Radius = state.Radius;
                ResetClientChunkFullSet(record);
                state.Session = record;
                return;
            }
            if (previous != null && !string.Equals(previous.Identity, state.Identity, StringComparison.Ordinal))
            {
                DetachSession(state, "同一连接重新握手时身份变了（" +
                    PlayerSessionKeys.ShortIdentity(previous.Identity) + " → " +
                    PlayerSessionKeys.ShortIdentity(state.Identity) + "）");
            }
            if (!m_sessions.TryGetValue(state.Identity, out PlayerSessionRecord session))
            {
                session = new PlayerSessionRecord
                {
                    Identity = state.Identity,
                    StableKey = PlayerSessionKeys.StableKeyOf(state.Identity),
                    CurrentClientId = state.Id,
                    Radius = state.Radius,
                    DisplayName = DisplayName(state)
                };
                m_sessions[state.Identity] = session;
                state.Session = session;
                SessionTrace?.Invoke("新身份会话记录：身份 " + PlayerSessionKeys.ShortIdentity(state.Identity) +
                    " · 稳定键 " + session.StableKey + " · clientId=" + state.Id +
                    "（之后重连会用新 clientId 复用这一条）");
                return;
            }
            // 同身份重连（新 clientId）：复用记录，**不重置**上报 / 裁决 / 待裁决。
            int previousHandle = session.CurrentClientId;
            session.CurrentClientId = state.Id;
            session.DisplayName = DisplayName(state);
            session.Radius = state.Radius;
            // 新连接重新握手：上一份没拼完的整集作废（旧连接可能中途断掉；新连接会重新发整集）。
            // 已收齐的内存集合 MemoryChunks **保留**，直到新连接的整集收齐后整体替换。
            ResetClientChunkFullSet(session);
            state.Session = session;
            SessionTrace?.Invoke("身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                "（稳定键 " + session.StableKey + "）重连：新 clientId=" + state.Id + " 接上已有会话记录" +
                (previousHandle >= 0 ? "（原 clientId=" + previousHandle + "）" : "（原连接已离开）") +
                " → 沿用 " + session.ReportedLoaded.Count + " 块已上报区块 / " +
                session.Verdicts.Count + " 条裁决状态 / " + session.PendingReports + " 条待裁决（不重置）");
            if (previousHandle >= 0 && previousHandle != state.Id &&
                m_clients.TryGetValue(previousHandle, out State oldState) && CanStillReachPeer(oldState))
            {
                SessionProblem?.Invoke("身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                    "（稳定键 " + session.StableKey + "）同时有两条在线连接：clientId=" + previousHandle +
                    " 与 clientId=" + state.Id + " → 校验状态按**身份**只保留一份（当前句柄 " + state.Id +
                    "）；旧连接会被可达性检查摘掉（这不是静默合并：两条连接本来就是同一个 UserId）");
            }
        }

        /// <summary>
        /// 解绑这条连接与它的身份记录（离开 / 改握手）。**是否清空记录**由调用方决定：
        /// 身份记录本身在"对端离开"时保留（重连要继续），只有房间/会话结束才整体清空。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void DetachSession(State state, string why)
        {
            PlayerSessionRecord session = state.Session;
            state.Session = null;
            if (session == null) return;
            if (session.CurrentClientId == state.Id) session.CurrentClientId = -1;
            // 这条身份（退化记录 = 这条连接）不再拥有任何按需单块加载：撤掉它的归属，
            // 没有归属的记录立刻撤临时加载位置（块交回引擎按常规管理）。
            int onDemand = m_onDemand.ReleaseOwner(session.StableKey);
            if (onDemand > 0)
            {
                OnDemandTrace?.Invoke("身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                    "（稳定键 " + session.StableKey + "）解绑（" + why + "）：撤掉 " + onDemand +
                    " 条按需单块加载的临时位置（其余块仍按它自己的地形更新位置加载；没有任何范围被放大）");
            }
            if (!session.Degraded) return;
            // 退化记录按连接生命周期：这条连接没了就没有归属，直接丢弃（明确写一行）。
            if (m_degradedSessions.TryGetValue(state.Id, out PlayerSessionRecord record) && record == session)
            {
                m_degradedSessions.Remove(state.Id);
                SessionTrace?.Invoke("clientId=" + state.Id + " 的退化校验记录（键 " + record.StableKey +
                    "，Hello 没带身份）随该连接一起丢弃：" + why);
            }
        }

        /// <summary>
        /// 一个身份（或一条退化连接）**离开联机**（Bye / 链路失效 / 不可达）时的唯一出口：
        ///   · 同一身份还有另一条**可达**连接 → 会话记录与欠更新标记都保留（明确写一行，不重复不丢失）；
        ///   · 否则：明确写一行"还有 N 块上报没裁决"（保留在身份记录里，重连继续；房间结束才清），
        ///     去掉它在**所有**区块上的欠更新标记（"退出联机后它的标记全部消失，再进来又是全 0"），
        ///     并把当前句柄置 -1（记录本身留在 <see cref="m_sessions"/> 里等重连）。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void NoteSessionDeparture(State state, string why)
        {
            PlayerSessionRecord session = state.Session;
            if (session == null)
            {
                state.PendingMarksCleared = true;
                return;
            }
            if (AnotherReachablePeerFor(session, state.Id))
            {
                SessionTrace?.Invoke("clientId=" + state.Id + "（身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                    " · 稳定键 " + session.StableKey + "）" + why + "，但同一身份还有另一条可达连接 → " +
                    "会话记录与欠更新标记都保留（不重复、不丢失）");
                return;
            }
            int pending = session.PendingReports;
            if (pending > 0)
            {
                ReportUnresolved?.Invoke("玩家端 clientId=" + state.Id + "（" + DisplayName(state) + " · 身份 " +
                    PlayerSessionKeys.ShortIdentity(session.Identity) + " · 稳定键 " + session.StableKey + "）" + why +
                    "，还有 " + pending + " 块上报没裁决（主机一直没拿到这些块的内容）→ 这些上报**保留在该身份的" +
                    "会话记录里**：它重连后继续用真实主机摘要作答，房间/会话结束才整体清掉（不作废、也不对着断线重请）");
            }
            RemovePendingClientEverywhere(session, why);
            // 这条身份已经没有可达连接：正在拼的整集分片作废（CurrentLoaded 保留到下次整集整体替换）。
            ResetClientChunkFullSet(session);
            DetachSession(state, why);
            state.PendingMarksCleared = true;
        }

        /// <summary>这条身份记录现在还有没有**别的**可达连接（同一身份的新旧连接短暂共存时用）。</summary>
        private bool AnotherReachablePeerFor(PlayerSessionRecord session, int exceptClientId)
        {
            foreach (KeyValuePair<int, State> pair in m_clients)
            {
                State other = pair.Value;
                if (other.Id == exceptClientId) continue;
                if (other.Kind != ClientKind.Player) continue;
                if (!ReferenceEquals(other.Session, session)) continue;
                if (CanStillReachPeer(other)) return true;
            }
            return false;
        }

        private void HandleInterest(State state, InterestUpdate interest)
        {
            var center = new ChunkKey(interest.CenterX, interest.CenterZ);
            int radius = Clamp(interest.Radius);
            if (state.WindowValid && state.Center.Equals(center) && state.Radius == radius) return;
            state.Center = center;
            state.Radius = radius;
            // 兴趣半径属于**身份**（日志里"该玩家端兴趣半径"用身份记录里的值，重连后仍然有效）。
            if (state.Session != null) state.Session.Radius = radius;
            RefreshWindow(state);
            // 不剪 Verdicts：状态只跟"这个身份上报过哪些块"走（见 PlayerSessionRecord.Verdicts），
            // 窗口移动只影响订阅关系，不影响已经上报过的块的裁决结论。
        }

        private int Clamp(int radius) => Math.Max(1, Math.Min(m_options.MaxRadius, radius));

        /// <summary>退订旧窗口 → 订阅新窗口（反表逐格维护）。</summary>
        private void RefreshWindow(State state)
        {
            ReleaseWindow(state);
            for (int x = state.Center.X - state.Radius; x <= state.Center.X + state.Radius; x++)
            {
                for (int z = state.Center.Z - state.Radius; z <= state.Center.Z + state.Radius; z++)
                {
                    var key = new ChunkKey(x, z);
                    state.Window.Add(key);
                    // **这里不给任何块刷黄框**。窗口是主机按客户端的兴趣中心圈出的**方形**范围，里面的
                    // 角落 / 边缘客户端根本没加载（客户端按**圆形**可见范围加载，那些块也不会出现在上报里）。
                    // 之前在这里给窗口内每块写 Requested，于是方块角落永远黄着（实机量到的 157 个黄框里
                    // 只有 1 个是真的待裁决）。用户口径："Requested 只表示客户端确实上报过、还没裁决"
                    // → 只有 HandleDigestReport 的延迟（NotReady）路径会写它。
                    if (!m_subscribers.TryGetValue(key, out HashSet<int> subs))
                    {
                        subs = new HashSet<int>();
                        m_subscribers[key] = subs;
                    }
                    subs.Add(state.Id);
                }
            }
            state.WindowValid = true;
        }

        private void ReleaseWindow(State state)
        {
            foreach (ChunkKey key in state.Window)
            {
                if (!m_subscribers.TryGetValue(key, out HashSet<int> subs)) continue;
                subs.Remove(state.Id);
                if (subs.Count == 0) m_subscribers.Remove(key);
            }
            state.Window.Clear();
            // 这条连接的追帧任务随连接一起作废（重连会按新序号重新请求；绝不给死连接继续排队）。
            m_catchUps.Remove(state.Id);
            state.CatchUpTarget = -1L;
        }

        /// <summary>
        /// 收到客户端的上报（<c>ChunkDigestReport</c>，每块一条 = 坐标 + 客户端摘要）。
        ///
        /// **状态全部写进这条连接归属的身份记录**（<see cref="State.Session"/>）：同身份重连后继续累计，
        /// 不会在 clientId 换成新编号时从 0 重来。
        ///
        /// 两条路径：
        ///   · 主机**已经有内容** → 立刻用真实主机摘要裁决（Match / Mismatch）；
        ///   · 主机**还没有内容** → 立刻回一条 NotReady（明确的"还没好"，绝不静默丢掉这次上报），
        ///     同时把**客户端上报的摘要登记成待办**，等块内容可用后由 <see cref="Tick"/> 用真实主机摘要补齐裁决。
        ///     旧代码在这里 `RequestLoad + continue`，摘要被丢掉 ⇒ 那 203 块黄框永远没有裁决。
        /// 没有身份记录（Debug 只读端 / 还没 Hello）→ **明确写一行后忽略**，绝不猜测、绝不并进别人的记录。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:499-518
        ///         （旧实现：目标块 State &lt; Valid 时**不 finalize**，把待校验项留着，等块再次可用再重问）
        /// </summary>
        private void HandleDigestReport(State state, PT.ChunkDigestReport report)
        {
            PlayerSessionRecord session = state.Session;
            if (session == null)
            {
                if (!state.SessionlessReportLogged)
                {
                    state.SessionlessReportLogged = true;
                    ReportUnresolved?.Invoke("clientId=" + state.Id + "（" + DisplayName(state) + " · kind=" +
                        state.Kind + "）发来了摘要上报，但它没有身份会话记录" +
                        (state.Kind == ClientKind.Debug
                            ? "（Debug 只读端不参与校验）"
                            : "（还没完成带身份的 Hello）") +
                        " → 本次上报被明确忽略（不建状态、不猜、也不并进任何其他端的记录）");
                }
                return;
            }
            long nowMs = Environment.TickCount64;
            int count = report.Entries.Count;
            if (count > TerrainSyncOptions.MaxDigestEntriesPerMessage)
            {
                ReportUnresolved?.Invoke("clientId=" + state.Id + "（" + DisplayName(state) +
                    "）的摘要上报有 " + count + " 条，超过上限 " + TerrainSyncOptions.MaxDigestEntriesPerMessage +
                    " → 整帧丢弃（不部分应用）");
                return;
            }
            for (int i = 0; i < count; i++)
            {
                PT.ChunkDigestReport.Types.Entry entry = report.Entries[i];
                if (entry?.Key == null)
                {
                    ReportUnresolved?.Invoke("clientId=" + state.Id + " 的摘要上报第 " + (i + 1) +
                        " 条缺少 key → 整帧丢弃（不部分应用）");
                    return;
                }
                var key = new ChunkKey(entry.Key.X, entry.Key.Z);
                uint clientHash = entry.Hash;
                m_digestReports++;
                // "这个身份加载完的区块" = 它上报过摘要的区块（实时地图画客户端区域只认这一份）。
                session.ReportedLoaded.Add(key);
                if (!m_world.HasContents(key))
                {
                    state.NotReady++;
                    // 顺序很重要：**先**为这一个块登记按需加载（幂等；客户端重报不会产生第二个请求），
                    // **再**决定这条上报要不要留在待裁决里：
                    //   · 登记成功 → 主机确实欠着这条裁决（黄），内容一到就会用真实主机摘要回答；
                    //   · 被明确拒绝（冷却 / 并发上限 / 引擎不接受）→ **不挂黄框**：没有人会去加载它，
                    //     挂上就是永远裁决不了、玩家不动就永远不消失的那种框。只回 NotReady（客户端按退避重问）。
                    if (!RequestOnDemandLoad(session, key, nowMs))
                    {
                        ClearRequestedVerdict(session, key);
                        SendVerdict(state, key, DigestVerdict.NotReady, 0u);
                        continue;
                    }
                    // **Requested（黄）的唯一来源**：客户端确实上报了这一块、主机还没有内容可裁决。
                    session.Verdicts[key] = (byte)ChunkVerifyState.Requested;   // 等主机加载后才能核对
                    RememberReport(session, key, clientHash, nowMs);           // 摘要留着，别丢
                    SendVerdict(state, key, DigestVerdict.NotReady, 0u);       // 明确的"还没好"
                    continue;
                }
                ResolveReport(state, key, clientHash, nowMs);
            }
        }

        // ------------------------------------------------------------ 客户端当前内存区块集合（v8 实时地图）

        /// <summary>
        /// 收到客户端"内存区块集合（引擎已分配）"（<see cref="MessageType.ClientChunks"/>）。
        ///
        /// 用户口径："客户端的方框只显示当前存在于客户端内存的，已经脱离内存的格子就取消绘制"。
        /// 因此这份集合是实时地图客户端线框的**唯一来源**（<see cref="PlayerSessionRecord.ReportedLoaded"/>
        /// 保留会话累计语义，只用于裁决过滤）。
        ///
        /// 规则（全部显式，绝不猜、绝不部分应用）：
        ///   · 没有身份会话记录（Debug 只读端 / 还没带身份 Hello）→ 一行明确原因后忽略；
        ///   · 不是该身份**当前句柄**的连接发来的 → 拒绝（旧连接的迟到帧不能改新连接的状态）；
        ///   · 载荷格式 / 超上限 / 分片不一致 → 整条拒绝，计入 reject 并限流写一行原因；
        ///   · 增量 → 逐条 add / remove；
        ///   · 整集 → 收齐同一 fullSetId 的全部分片后**整体替换**（分片期间保留旧集合）。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void HandleClientChunks(State state, PT.ClientChunks wire)
        {
            PlayerSessionRecord session = state.Session;
            if (session == null)
            {
                if (!state.SessionlessClientChunksLogged)
                {
                    state.SessionlessClientChunksLogged = true;
                    ReportUnresolved?.Invoke("clientId=" + state.Id + "（" + DisplayName(state) + " · kind=" +
                        state.Kind + "）发来了内存区块集合（引擎已分配），但它没有身份会话记录" +
                        (state.Kind == ClientKind.Debug
                            ? "（Debug 只读端只观察，不上报自己的集合）"
                            : "（还没完成带身份的 Hello）") +
                        " → 本次载荷被明确忽略（不建状态、不猜、也不并进任何其他端的记录）");
                }
                return;
            }
            if (session.CurrentClientId != state.Id)
            {
                RejectClientChunks(state, session, "这条连接不是该身份当前句柄（当前 clientId=" +
                    session.CurrentClientId + "）—— 旧连接的迟到帧不能改动新连接的内存集合");
                return;
            }
            if (!ClientChunkSetCodec.TryConvert(wire, out ClientChunkSetMessage message, out string error))
            {
                RejectClientChunks(state, session, error);
                return;
            }
            if (message.Mode == ClientChunkSetMode.Delta)
            {
                ApplyClientChunkDelta(state, session, message);
                return;
            }
            ApplyClientChunkFullSetPart(state, session, message);
        }

        /// <summary>
        /// 增量：loaded=1 → add（并清掉该身份在这块上可能欠的标记），loaded=0 → remove
        /// （同一块在一条消息里出现多次以最后一次为准，因为逐条按顺序应用）。
        /// 只有**真的**新增 / 移除才计入日志里的增删数（重复的 no-op 不算）。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void ApplyClientChunkDelta(State state, PlayerSessionRecord session,
            ClientChunkSetMessage message)
        {
            int added = 0;
            int removed = 0;
            int marksCleared = 0;
            int snapshotsQueued = 0;
            for (int i = 0; i < message.Entries.Count; i++)
            {
                ClientChunkEntry entry = message.Entries[i];
                var key = new ChunkKey(entry.X, entry.Z);
                if (entry.Loaded)
                {
                    if (session.MemoryChunks.Add(key))
                    {
                        added++;
                        // 它的当前内存集合**新增**了这一块 → 它不再欠这块的更新（用户口径：走进该区域即清零）。
                        if (ClearPendingOnChunkLoaded(session, key, out long owedSequence))
                        {
                            marksCleared++;
                            // 关键补充：它"刚开始持有"这块，而主机在它没有这块时记过一笔改动 ——
                            // 那次投递可能刚好错过（ClientChunks 还在路上）。直接补一份**当前快照**，
                            // 比等"加载摘要 → 不一致 → 回传"少一个往返，也避免窗口里的静默旧内容。
                            if (EnqueueChunkSnapshotLocked(state, key, owedSequence)) snapshotsQueued++;
                        }
                    }
                }
                else if (session.MemoryChunks.Remove(key))
                {
                    removed++;
                }
            }
            session.ClientChunkDeltaMessages++;
            m_clientChunkDeltaMessages++;
            m_clientChunkDeltaMessagesSinceLog++;
            m_clientChunkDeltaAddsSinceLog += added;
            m_clientChunkDeltaRemovesSinceLog += removed;
            m_clientChunkDeltaMarkClearsSinceLog += marksCleared;
            m_clientChunkDeltaSnapshotsSinceLog += snapshotsQueued;
            long nowMs = Environment.TickCount64;
            if (m_lastClientChunkDeltaLogMs != 0 &&
                nowMs - m_lastClientChunkDeltaLogMs < m_options.ClientChunkLogIntervalMs)
            {
                return;
            }
            m_lastClientChunkDeltaLogMs = nowMs;
            long messages = m_clientChunkDeltaMessagesSinceLog;
            long logAdded = m_clientChunkDeltaAddsSinceLog;
            long logRemoved = m_clientChunkDeltaRemovesSinceLog;
            long logMarkClears = m_clientChunkDeltaMarkClearsSinceLog;
            long logSnapshots = m_clientChunkDeltaSnapshotsSinceLog;
            m_clientChunkDeltaMessagesSinceLog = 0;
            m_clientChunkDeltaAddsSinceLog = 0;
            m_clientChunkDeltaRemovesSinceLog = 0;
            m_clientChunkDeltaMarkClearsSinceLog = 0;
            m_clientChunkDeltaSnapshotsSinceLog = 0;
            ClientChunkSetTrace?.Invoke("身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                "（稳定键 " + session.StableKey + " · clientId=" + state.Id + "）内存区块增量：" +
                "最近 " + m_options.ClientChunkLogIntervalMs + "ms 内应用 " + messages +
                " 条消息 → 新增 " + logAdded + " 块 / 移除 " + logRemoved + " 块" +
                (logMarkClears > 0 ? " / 清除欠更新标记 " + logMarkClears + " 个" : string.Empty) +
                (logSnapshots > 0 ? " / 补发整块快照 " + logSnapshots + " 份" : string.Empty) +
                "；当前内存区块（引擎已分配）" + session.MemoryChunks.Count + " 块（累计增量消息 " +
                session.ClientChunkDeltaMessages + " 条）");
        }

        /// <summary>
        /// 整集分片：同一 fullSetId 的分片先攒着（分片期间**保留**旧集合，不让地图闪成半张）；
        /// 收齐全部分片后一次性 <see cref="PlayerSessionRecord.MemoryChunks"/> 整体替换。
        /// 分片序号重复 / 同一 id 的分片数不一致 → 整条拒绝（明确计数 + 限流原因行）。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void ApplyClientChunkFullSetPart(State state, PlayerSessionRecord session,
            ClientChunkSetMessage message)
        {
            if (session.ClientChunkFullSetId != message.FullSetId)
            {
                // 新的整集：丢掉上一份没拼完的分片（旧连接中途断掉时留下的），从这一片重新拼。
                session.ClientChunkFullSetId = message.FullSetId;
                session.ClientChunkFullExpectedParts = message.PartCount;
                session.ClientChunkFullParts.Clear();
                session.ClientChunkFullBuffer.Clear();
            }
            else if (session.ClientChunkFullExpectedParts != message.PartCount)
            {
                RejectClientChunks(state, session, "同一整集 id=" + message.FullSetId + " 的分片数从 " +
                    session.ClientChunkFullExpectedParts + " 变成 " + message.PartCount +
                    "（协议不一致，整条拒绝）");
                return;
            }
            if (!session.ClientChunkFullParts.Add(message.PartIndex))
            {
                RejectClientChunks(state, session, "整集 id=" + message.FullSetId + " 的分片 " +
                    message.PartIndex + " 重复到达（整条拒绝，不重复并入）");
                return;
            }
            for (int i = 0; i < message.Entries.Count; i++)
            {
                ClientChunkEntry entry = message.Entries[i];
                session.ClientChunkFullBuffer.Add(new ChunkKey(entry.X, entry.Z));
            }
            if (session.ClientChunkFullParts.Count < session.ClientChunkFullExpectedParts) return;
            // 收齐：整体替换（旧集合里不在新集合中的块 = 客户端已经卸载 / 本次会话不再持有的块）。
            int previous = session.MemoryChunks.Count;
            session.MemoryChunks.Clear();
            int marksCleared = 0;
            foreach (ChunkKey key in session.ClientChunkFullBuffer)
            {
                session.MemoryChunks.Add(key);
                // 新集合包含这一块 → 该身份不再欠这块的更新（整集替换也走同一条清除口径）。
                // **不**在这里补整块快照：整集是握手/重连的基线，缺口由追帧或整图覆盖（否则一次重连会灌几百份快照）。
                if (ClearPendingOnChunkLoaded(session, key, out _)) marksCleared++;
            }
            session.ClientChunkFullSets++;
            m_clientChunkFullSets++;
            int count = session.MemoryChunks.Count;
            ResetClientChunkFullSet(session);
            ClientChunkSetTrace?.Invoke("身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                "（稳定键 " + session.StableKey + " · clientId=" + state.Id + "）整集已收齐：id=" +
                message.FullSetId + " · " + message.PartCount + " 片 / " + count +
                " 块 → 内存区块集合（引擎已分配）整体替换（原 " + previous + " 块；v8 实时地图客户端线框只画这一份）" +
                (marksCleared > 0 ? "；新集合包含的块清掉 " + marksCleared + " 个欠更新标记" : string.Empty));
        }

        /// <summary>丢弃该身份正在拼的整集（新连接握手 / 离开时调用；已收齐的不受影响）。</summary>
        private static void ResetClientChunkFullSet(PlayerSessionRecord session)
        {
            session.ClientChunkFullSetId = -1;
            session.ClientChunkFullExpectedParts = 0;
            session.ClientChunkFullParts.Clear();
            session.ClientChunkFullBuffer.Clear();
        }

        /// <summary>
        /// 拒绝一条"当前内存区块集合"载荷：计数（全局 + 该身份）+ 限流一行明确原因。
        /// 限流是**只限日志**，计数每次都涨（否则"偶发拒绝"会看不见）。
        /// </summary>
        private void RejectClientChunks(State state, PlayerSessionRecord session, string reason)
        {
            m_clientChunkRejects++;
            if (session != null) session.ClientChunkRejects++;
            m_clientChunkRejectsSinceLog++;
            long nowMs = Environment.TickCount64;
            if (m_lastClientChunkRejectLogMs != 0 &&
                nowMs - m_lastClientChunkRejectLogMs < m_options.ClientChunkLogIntervalMs)
            {
                return;
            }
            m_lastClientChunkRejectLogMs = nowMs;
            long recent = m_clientChunkRejectsSinceLog;
            m_clientChunkRejectsSinceLog = 0;
            ClientChunkSetProblem?.Invoke("clientId=" + state.Id + "（" + DisplayName(state) +
                (session != null ? " · 身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                    " · 稳定键 " + session.StableKey : string.Empty) +
                "）的当前内存区块集合载荷被拒绝：" + reason + " → 这条载荷**整条不应用**" +
                "（不做部分应用、不猜内容）；最近 " + m_options.ClientChunkLogIntervalMs + "ms 内拒绝 " +
                recent + " 条，累计 " + m_clientChunkRejects + " 条");
        }

        /// <summary>
        /// 把一条上报登记成"待裁决"（块内容还没有时）。客户端重报同一块 → 只更新摘要，不重复入队。
        /// **登记在身份记录上**（跨重连保留）。
        /// 调用方必须先确认"主机真的会去加载这块"（<see cref="RequestOnDemandLoad"/> 返回 true）——
        /// 否则登记出来的就是一条永远裁决不了的黄框。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:572-575
        ///         （旧实现按坐标去重：pending/queued 里已有的块不再排队）
        /// </summary>
        private void RememberReport(PlayerSessionRecord session, ChunkKey key, uint clientHash, long nowMs)
        {
            if (session.Pending.TryGetValue(key, out PendingReport existing))
            {
                // 客户端重报（NotReady 后的重试）：以**最新一次**上报的摘要为裁决依据
                existing.ClientHash = clientHash;
                return;
            }
            session.Pending[key] = new PendingReport
            {
                Key = key, ClientHash = clientHash, FirstSeenMs = nowMs
            };
            session.PendingScan.Enqueue(key);
        }

        /// <summary>
        /// 用**真实主机摘要**裁决一条上报并记账（记账落在该身份的记录上）。
        /// 只有 <see cref="SendVerdict"/> 真的发出去了才调用（队列满 = 显式背压 → 留在待办里下一拍再发）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:691-709
        ///         （旧实现下发前一律改读当下的主机地形值，绝不发旧值/猜值）
        /// </summary>
        private void ApplyVerdict(State state, ChunkKey key, DigestVerdict verdict)
        {
            PlayerSessionRecord session = state.Session;
            if (session == null) return;
            session.Pending.Remove(key);
            if (verdict == DigestVerdict.Match)
            {
                state.Match++;
                // 之前失败过、回传后这次通过 = **二次验证成功**（蓝框），否则就是一次通过（绿框）
                bool wasFailed = session.Verdicts.TryGetValue(key, out byte previous) &&
                                 previous == (byte)ChunkVerifyState.Failed;
                session.Verdicts[key] = wasFailed
                    ? (byte)ChunkVerifyState.ReVerified
                    : (byte)ChunkVerifyState.Verified;
                // 陈旧裁决自愈：这块之前是红、主机为它主动要过（或已登记要）新鲜摘要 → 这次 Match 就是
                // "重判成功"。只有登记过（= 主机确实怀疑这条红已过期）才计数；正常一次通过的绿不算。
                if (session.StaleVerdicts.TryGetValue(key, out StaleVerdictWatch refreshed))
                {
                    session.StaleVerdicts.Remove(key);
                    if (wasFailed)
                    {
                        m_staleVerdictRefreshed++;
                        long nowMs = Environment.TickCount64;
                        refreshed.LastLogMs = nowMs;
                        NoteStaleVerdictTransition(state, session, key,
                            "重新裁决通过（红 → 蓝 ReVerified）：客户端这次报的摘要与主机一致；" +
                            "为它要过 " + refreshed.Requests + " 次新鲜摘要（原因：" +
                            refreshed.LastReason + "）—— 累计重判 " + m_staleVerdictRefreshed + " 块",
                            warning: false);
                    }
                }
                // 裁决只决定"绿/蓝/红"与回传；**不**参与欠更新计数 ——
                // 那面数字只跟"该身份当前内存集合里有没有这一块"走（见 m_pendingClients）。
                return;
            }
            state.Mismatch++;
            session.Verdicts[key] = (byte)ChunkVerifyState.Failed;
            // 刚判成红：如果这块已经在自愈台账里，只把下一次请求退避到正常收敛之后
            // （**不**重置首次登记时刻 → 同一块的自愈次数与清除上限仍然有界）；否则登记一条。
            NoteVerdictNeedsRejudge(session, key, "主机刚判 Mismatch（回传落地后需要一次新鲜摘要）");
            // 同一块已经有一份回传快照在队列里（客户端重报 / 迟到的裁决）→ **合并**，
            // 不再 1:1 放大回传队列与带宽。这一份快照在发送时刻按**当时最新内容**编码（见 PendingResyncJob），
            // 覆盖它本来就要覆盖的全部内容；客户端落地后仍不一致会再报一次，由下一份快照处理。
            foreach (PendingResyncJob queued in state.PendingResync)
            {
                if (!queued.Key.Equals(key)) continue;
                m_resyncCoalesced++;
                return;
            }
            // 只登记"要回传这一块"：真正编码在 Tick 的**发送时刻**做（见 PendingResyncJob），
            // 这样快照一定包含此前已应用的全部改动，不会用旧内容盖掉后到的改动。
            state.PendingResync.Enqueue(new PendingResyncJob { Key = key });
        }

        /// <summary>
        /// 主机已有内容 → 现算主机摘要、比对、发送裁决、记账。发送失败（显式背压）时登记成待裁决，
        /// 由下一拍补发 —— 绝不把"没发出去"当成"已裁决"。
        /// </summary>
        private void ResolveReport(State state, ChunkKey key, uint clientHash, long nowMs)
        {
            PlayerSessionRecord session = state.Session;
            if (session == null) return;
            uint hostHash = HostDigest(key);
            DigestVerdict verdict = CompareReport(clientHash, hostHash);
            if (!SendVerdict(state, key, verdict, hostHash))
            {
                // 发送队列满：这条上报还没被回答，留在待办里，下一拍补真实主机摘要。
                // 它同样是"客户端确实上报过、还没裁决"→ 没有历史结论时记 Requested（黄），
                // 这样"待裁决条数 = 黄框块数"在背压时也成立；已有结论（绿/红/蓝）不覆盖，
                // 免得把二次验证（蓝）洗成黄。
                if (!session.Verdicts.ContainsKey(key)) session.Verdicts[key] = (byte)ChunkVerifyState.Requested;
                RememberReport(session, key, clientHash, nowMs);
                return;
            }
            ApplyVerdict(state, key, verdict);
        }

        /// <summary>
        /// 主机侧比对：客户端摘要 0 是 <see cref="ChunkDigest.Value"/> 的"未计算/未知"保留值，
        /// **不能**当成"和主机一样"（那就是伪造匹配）—— 一律按不一致处理，把主机这份内容回传过去。
        /// </summary>
        private static DigestVerdict CompareReport(uint clientHash, uint hostHash) =>
            clientHash == 0u ? DigestVerdict.Mismatch : ChunkLoadVerifier.Compare(clientHash, hostHash, true);

        /// <summary>
        /// 每 tick 轮转核对"欠着裁决"的上报（有界；待办挂在**身份记录**上，跨重连保留）：
        ///   · 内容已可用 → 现算真实主机摘要 → 发裁决 → 记账（Match→绿/蓝，Mismatch→红 + 排队回传）；
        ///   · 内容还没有 → 交给按需加载台账（同一块一条记录：指数退避重试 + 次数 / 总超时上限 + 冷却），
        ///     并写**一次**明确原因（同一块不重复刷屏）；台账超时后由
        ///     <see cref="TickOnDemandLoads"/> 把这条上报**摘掉**（不无限挂着）。
        /// 绝不伪造裁决。调用方保证只有该身份**当前**的连接会进来（见 Tick）。
        ///
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:630-642
        ///         （待办超过 5s → 重新入队请求；旧实现没有次数上限，这里换成有界重试 + 明确超时）
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:652-660
        ///         （等块的 DueTime 到点后重查 chunk.State &gt;= Valid，可用才 QueueClientTerrainChunkSync）
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:670-671
        ///         （只有 chunk.State &gt;= Valid 才真正发请求/下发，块不可用就留在待办里等下一轮）
        /// </summary>
        private int ResolvePendingReports(State state, long nowMs)
        {
            PlayerSessionRecord session = state.Session;
            if (session == null || session.Pending.Count == 0) return 0;
            int scanned = 0;
            int resolved = 0;
            while (scanned < m_options.PendingReportsPerTick &&
                   resolved < m_options.PendingResolvesPerTick &&
                   session.PendingScan.Count > 0)
            {
                ChunkKey key = session.PendingScan.Dequeue();
                if (!session.Pending.TryGetValue(key, out PendingReport report))
                {
                    // 已经被别的路径（客户端重报且主机已有内容）裁决过：跳过，不再计一次扫描
                    continue;
                }
                scanned++;
                if (!m_world.HasContents(key))
                {
                    // 加载节奏全部归按需加载台账（这里**不再**自己重请引擎，否则同一块会有两条请求路径）。
                    if (!RequestOnDemandLoad(session, key, nowMs))
                    {
                        // 主机明确不为它发起加载（冷却 / 上限 / 引擎拒绝）→ 立刻摘掉这条待裁决，
                        // 不放回队列：否则黄框会永远挂着，而底下根本没有人在加载。
                        session.Pending.Remove(key);
                        ClearRequestedVerdict(session, key);
                        continue;
                    }
                    ReportStalled(state, session, report, nowMs);
                    session.PendingScan.Enqueue(key);      // 下一轮继续核（有界：台账负责重试与超时）
                    continue;
                }
                uint hostHash = HostDigest(key);
                DigestVerdict verdict = CompareReport(report.ClientHash, hostHash);
                if (!SendVerdict(state, key, verdict, hostHash))
                {
                    // 显式背压（TrySend=false）：没发出去就还欠着，下一轮补发真实主机摘要
                    session.PendingScan.Enqueue(key);
                    continue;
                }
                ApplyVerdict(state, key, verdict);
                resolved++;
            }
            return resolved;
        }

        /// <summary>
        /// 主机裁决后、回传编码前这块内容没了 → 已改回 NotReady（不发零格快照）：限流写一行原因。
        /// 同一块不重复刷屏（全局 10s 一行），计数照常累加。
        /// </summary>
        private void NoteResyncEmptySkipped(State state, ChunkKey key)
        {
            long nowMs = Environment.TickCount64;
            if (m_lastResyncEmptyLogMs != 0 &&
                nowMs - m_lastResyncEmptyLogMs < m_options.ClientChunkLogIntervalMs) return;
            m_lastResyncEmptyLogMs = nowMs;
            ReportUnresolved?.Invoke("区块 " + key + " 的 Mismatch 回传在编码时主机已没有内容（裁决后卸载 / 回退）" +
                " → **不发零格快照**，已改回 NotReady（客户端按退避重问；不形成空回传循环）；" +
                "clientId=" + state.Id + "（" + DisplayName(state) + "）· 累计 " + m_resyncEmptySkipped + " 次");
        }

        /// <summary>
        /// 同一块"内容一直没加载出来"只写一行明确原因（含欠了多久、按需加载台账的状态、该身份的兴趣半径）。
        /// 这是"客户端上报的边缘块"的现场说明：主机**已经为这一块单独发起按需加载**
        /// （临时单块地形更新位置，见 <see cref="OnDemandChunkLoads"/>），而不是把某个角色的范围放大一圈；
        /// 台账按退避重试，超时后**明确放弃**并把这条上报摘掉（绝不假装匹配、也不无限挂着）。
        /// </summary>
        private void ReportStalled(State state, PlayerSessionRecord session, PendingReport report, long nowMs)
        {
            if (report.StalledLogged) return;            // 限流：同一块最多一行
            report.StalledLogged = true;
            string onDemand = m_onDemand.TryDescribe(report.Key, nowMs, out string described)
                ? described
                : "没有在册的按需加载";
            ReportUnresolved?.Invoke("主机一直拿不到区块 " + report.Key + " 的内容，身份 " +
                PlayerSessionKeys.ShortIdentity(session.Identity) + "（稳定键 " + session.StableKey +
                " · clientId=" + state.Id + "）的这条上报还没裁决（已等 " + ((nowMs - report.FirstSeenMs) / 1000) +
                "s · " + onDemand + " · 该玩家端兴趣半径 " + session.Radius +
                " 区块，**没有**放大任何角色的范围）—— 加载只由引擎按地形更新位置异步推进，" +
                "本端已为这块单独请求加载并继续按退避重试，超时就明确放弃并摘掉这条待裁决，绝不假装匹配");
        }

        /// <summary>
        /// 对端还能不能收到消息：Connected / Backpressured（队列满但连接还在）都还能收；
        /// Failed / Disconnected 不能 —— 这时"欠着的上报"只能在**保留**的前提下写一行原因
        /// （记录按身份保留，重连继续作答；但绝不再对着一根断线重请加载）。
        /// </summary>
        private bool CanStillReachPeer(State state)
        {
            ChannelState peerState = m_server.StateOf(state.Id);
            return peerState == ChannelState.Connected || peerState == ChannelState.Backpressured;
        }

        /// <summary>
        /// 发送一条裁决。返回 false = 发送队列满（**显式背压**，调用方必须保留这条上报，不能当已裁决）。
        /// </summary>
        private bool SendVerdict(State state, ChunkKey key, DigestVerdict verdict, uint hostHash)
        {
            var message = new PT.ChunkVerdict();
            message.Entries.Add(new PT.ChunkVerdict.Types.Entry
            {
                Key = new PT.ChunkKey { X = key.X, Z = key.Z },
                Verdict = (PT.ChunkDigestVerdict)(int)verdict,
                HostHash = hostHash
            });
            return m_server.TrySend(state.Id, MessageType.ChunkVerdict, message.ToByteArray());
        }

        /// <summary>
        /// 主机侧摘要：**每次按当下内容现算，不做跨调用缓存**。
        ///
        /// 为什么不能缓存（实机 B 端"同一区块反复不一致"回传风暴的根因）：
        /// 引擎会绕过 <c>ChangeCell</c> 直接写格 —— 流体 <c>UpdateIsTop</c> 只改 data、
        /// 流体/岩浆在 <c>base.Update</c> 内被 <c>ProcessModifiedCells</c> 清表、
        /// 以及候选格快照上限（<c>ReplicaGates.SnapshotCandidates(64)</c>）之外的变化。
        /// 这些写入不会触发 <see cref="BroadcastCellChange"/>，任何"改动时失效"的缓存都会停在旧值：
        /// 客户端按主机**当下内容**编码的快照覆盖后，本地摘要 = 当下内容，主机却拿缓存旧值判 Mismatch
        /// → 再回传同一份内容 → 客户端再报 → 永远 1:1 循环（B 端实测 45 次/秒、累计 6978 片/6978 次）。
        /// 现算一次 65536 格 ≈ 0.20 ms，且每 tick 的裁决数由
        /// <see cref="TerrainSyncOptions.PendingResolvesPerTick"/> 限流，成本上界可解释。
        /// </summary>
        public uint HostDigest(ChunkKey key)
        {
            if (!m_world.HasContents(key)) return 0u;
            var digest = new ChunkDigest();
            digest.Compute(m_world.GetCellSource(key));
            return digest.HasValue ? digest.Value : 0u;
        }

        /// <summary>消息层明确丢弃的"载荷解析失败"帧数（按协议层计数；领域校验失败另有各自的 Malformed 计数）。</summary>
        public long MalformedMessages => m_messages.Registry.MalformedMessages;

        /// <summary>
        /// 主机权威改动：**投递目标 = 订阅了这一块的连接 ∪ 每个当前内存集合包含这一块的在线 Player 身份**
        /// （规则唯一入口：<see cref="PlanCellChangeLocked"/>）。用户口径："客户端内存里有这一块，
        /// 改动就必须到它手里" —— 旧实现只发兴趣窗口订阅者，客户端**内存里有、窗口外**的块会静默停在旧内容。
        /// 欠更新计数**不按投递结果结算**（数字 = 当前内存集合里没有该区块的在线身份数）：
        ///   · 每个**在线玩家身份**各自看 <see cref="PlayerSessionRecord.MemoryChunks"/>：
        ///     不含这一块 → 记上；含这一块 → 不加，并清掉可能残留的标记（<see cref="MarkChunkChanged"/>）；
        ///   · 投递成功 / 失败 / 推迟只进各自的可审计计数器，绝不参与加/减欠更新标记。
        /// **顺序**：每条连接一个投递序号（<see cref="State.DeliveryIndex"/>，成功入队一帧才 +1）；
        /// 失败 / 追帧在途 → 记 <see cref="State.RepairFromSequence"/>，之后**不再发新改动**，
        /// 由 <see cref="RepairCellChangesLocked"/> 按改动历史顺序补齐 —— 客户端看到的序号永远单调。
        /// </summary>
        public int BroadcastCellChange(ChunkKey key, CellEdit edit)
        {
            long nowMs = Environment.TickCount64;
            long sequence;
            List<CellDelivery> plan;
            lock (m_gate)
            {
                // 序号与历史**先记**（即使没有任何投递目标）：重连追帧只认这条历史，
                // 不能因为"当时没人订阅 / 没人持有"就在世界里留下一个无法重放的洞。
                sequence = m_changeSequence + 1;
                RecordChangeLocked(key, edit, nowMs);
                plan = PlanCellChangeLocked(key, sequence);
            }
            int sent = 0;
            List<int> failed = null;
            List<CellDelivery> delivered = null;
            for (int i = 0; i < plan.Count; i++)
            {
                CellDelivery target = plan[i];
                byte[] payload = EncodeCellChange(key, edit, sequence, target.PrevIndex, target.Index,
                    replay: false);
                if (!m_server.TrySend(target.ClientId, MessageType.CellChange, payload))
                {
                    // 显式背压 / 对端不可达：这一份**没有**发出去（不推进投递序号，随后按历史顺序补发）。
                    (failed ?? (failed = new List<int>())).Add(target.ClientId);
                    continue;
                }
                sent++;
                (delivered ?? (delivered = new List<CellDelivery>())).Add(target);
            }
            lock (m_gate)
            {
                if (delivered != null)
                {
                    for (int i = 0; i < delivered.Count; i++)
                    {
                        CellDelivery target = delivered[i];
                        if (m_clients.TryGetValue(target.ClientId, out State state))
                        {
                            state.DeliveryIndex = target.Index;
                            state.LastDeliveredSequence = sequence;
                            state.CellChangesSent++;
                            // 这条改动已经到它手里：如果这块的裁决停在红（Mismatch），那条结论可能已经
                            // 过期（客户端内容因此补齐）→ 交给陈旧裁决自愈重新要一份新鲜摘要。
                            NoteVerdictNeedsRejudge(state, key, "实时改动已送达（序号 " + sequence + "）");
                        }
                        m_cellChanges++;
                        if (target.MemoryRecipient) m_cellChangeMemoryRecipients++;
                        if (target.MemoryOnly) m_cellChangeMemoryOnly++;
                        if (target.OutsideWindow) m_cellChangeOutsideWindow++;
                    }
                }
                if (failed != null)
                {
                    for (int i = 0; i < failed.Count; i++)
                    {
                        m_cellChangeSendFailures++;
                        if (!m_clients.TryGetValue(failed[i], out State state)) continue;
                        // 这条连接从此不许再发新改动：先按历史顺序补齐（客户端看到的序号保持单调）。
                        if (state.RepairFromSequence < 0) state.RepairFromSequence = sequence;
                    }
                }
                // 不管有没有投递目标、投递成功与否：这一块的内容已经变了 →
                // 逐个在线玩家身份按它自己的当前内存集合结算欠更新（与订阅表 / 投递结果无关）。
                MarkChunkChanged(key, edit, sequence);
                if (failed != null)
                {
                    // 限流：同一波背压只写一行原因（计数仍会涨，但日志不刷屏）。
                    // 0 = 还没写过（不能用 TickCount64 直接相减：开机不到限流间隔时会把第一次失败吞掉）。
                    if (m_lastCellChangeSendFailureLogMs == 0 ||
                        nowMs - m_lastCellChangeSendFailureLogMs >= m_options.CellChangeFailureLogIntervalMs)
                    {
                        m_lastCellChangeSendFailureLogMs = nowMs;
                        PendingTrace?.Invoke("区块 " + key + " 有改动 (" + edit.X + "," + edit.Y + "," + edit.Z +
                            ") → " + failed.Count + " 个投递目标 CellChange **没发出去**（发送队列满 = " +
                            "显式背压 / 连接不可达）→ 这些连接先补齐历史里的改动再继续（新改动不抢先发，" +
                            "序号保持单调）；累计投递失败 " + m_cellChangeSendFailures + " 次（本行限流 " +
                            m_options.CellChangeFailureLogIntervalMs + "ms）");
                    }
                }
            }
            return sent;
        }

        /// <summary>一条改动的投递目标：计划阶段定好投递序号，**发送成功后才提交**到 State。</summary>
        private readonly struct CellDelivery
        {
            public CellDelivery(int clientId, long prevIndex, long index, bool memoryRecipient,
                bool memoryOnly, bool outsideWindow)
            {
                ClientId = clientId;
                PrevIndex = prevIndex;
                Index = index;
                MemoryRecipient = memoryRecipient;
                MemoryOnly = memoryOnly;
                OutsideWindow = outsideWindow;
            }

            public int ClientId { get; }
            public long PrevIndex { get; }
            public long Index { get; }
            /// <summary>这个目标是因为"当前内存集合包含该块"才投递的。</summary>
            public bool MemoryRecipient { get; }
            /// <summary>只在内存集合里（不在兴趣窗口订阅表里）—— 本轮修复的那类目标。</summary>
            public bool MemoryOnly { get; }
            /// <summary>投递时该块不在这个连接的兴趣窗口内。</summary>
            public bool OutsideWindow { get; }

            public CellDelivery AsMemoryRecipient(bool outsideWindow) =>
                new CellDelivery(ClientId, PrevIndex, Index, true, false, outsideWindow);
        }

        /// <summary>
        /// 改动投递目标（**唯一规则**）：
        ///   ① 订阅了这一块的连接（保持旧行为：还没上报 ClientChunks 的旧构建 / Debug 只读端仍按兴趣窗口收）；
        ///   ② 每个**在线 Player 身份**，只要它自己的当前内存集合（<see cref="PlayerSessionRecord.MemoryChunks"/>）
        ///      包含这一块 —— 与欠更新计数用的是**同一个集合**（含这块 → 不欠 → 必须收到改动）。
        /// 同一客户端只出现一次；身份只由当前句柄代表；不可达的连接不投递（由离开清理负责）。
        /// 已有追帧任务 / 有缺失未补的连接**不投递新改动**，只把缺失起点记进
        /// <see cref="State.RepairFromSequence"/>（随后由 <see cref="RepairCellChangesLocked"/> 按序补齐）。
        /// 调用方已持有 m_gate。
        /// </summary>
        private List<CellDelivery> PlanCellChangeLocked(ChunkKey key, long sequence)
        {
            var plan = new List<CellDelivery>();
            var seen = new HashSet<int>();
            var deferred = new HashSet<int>();
            if (m_subscribers.TryGetValue(key, out HashSet<int> subs))
            {
                foreach (int clientId in subs)
                {
                    if (!m_clients.TryGetValue(clientId, out State state)) continue;
                    if (!CanStillReachPeer(state)) continue;
                    if (!CanDeliverNowLocked(state, sequence)) { deferred.Add(state.Id); continue; }
                    bool outside = state.WindowValid && !state.Window.Contains(key);
                    plan.Add(new CellDelivery(clientId, state.DeliveryIndex, state.DeliveryIndex + 1,
                        memoryRecipient: false, memoryOnly: false, outsideWindow: outside));
                    seen.Add(clientId);
                }
            }
            foreach (KeyValuePair<int, State> pair in m_clients)
            {
                State state = pair.Value;
                if (state.Kind != ClientKind.Player) continue;   // 调试端只观察（它若订阅走上面那条）
                PlayerSessionRecord session = state.Session;
                if (session == null || session.CurrentClientId != state.Id) continue;
                if (!session.MemoryChunks.Contains(key)) continue;
                if (!CanStillReachPeer(state)) continue;
                if (!CanDeliverNowLocked(state, sequence)) { deferred.Add(state.Id); continue; }
                bool outside = !state.WindowValid || !state.Window.Contains(key);
                if (seen.Contains(state.Id))
                {
                    // 已经由订阅路径投递过：只把"内存持有"的计数补上（不重复发一帧）。
                    for (int i = 0; i < plan.Count; i++)
                    {
                        if (plan[i].ClientId != state.Id) continue;
                        plan[i] = plan[i].AsMemoryRecipient(outside);
                        break;
                    }
                    continue;
                }
                plan.Add(new CellDelivery(state.Id, state.DeliveryIndex, state.DeliveryIndex + 1,
                    memoryRecipient: true, memoryOnly: true, outsideWindow: outside));
                seen.Add(state.Id);
            }
            m_cellChangeDeferrals += deferred.Count;
            return plan;
        }

        /// <summary>
        /// 这条连接现在能不能发一条**新**改动：追帧在途（重放会覆盖到水位）或已有缺失未补 → 不行。
        /// 不行时把缺失起点记进 <see cref="State.RepairFromSequence"/>（只记最早那条）。
        /// </summary>
        private bool CanDeliverNowLocked(State state, long sequence)
        {
            if (!m_catchUps.ContainsKey(state.Id) && state.RepairFromSequence < 0) return true;
            if (state.RepairFromSequence < 0) state.RepairFromSequence = sequence;
            return false;
        }

        /// <summary>
        /// 把这条连接"没发出去的改动"按**改动历史顺序**补完（每 tick 有界）：
        /// 从 <see cref="State.LastDeliveredSequence"/> 之后逐条重发（flags=0，与实时改动同一种帧），
        /// 成功一条推进一步；任何一条遇到背压 → 本 tick 停止，下一 tick 从同一位置继续（顺序不变）。
        /// 缺口已被有界历史裁掉 → 明确写一行 + 计数，本连接不再假装能补齐（等强制核对 / 下次重连）。
        /// 追帧在途时不补（追帧本身会覆盖到水位；两路同时发会打乱顺序）。
        /// </summary>
        private int RepairCellChangesLocked(State state, long nowMs)
        {
            if (state.RepairFromSequence < 0) return 0;
            if (m_catchUps.ContainsKey(state.Id)) return 0;
            if (!CanStillReachPeer(state)) return 0;
            long oldest = FirstRetainedSequenceLocked();
            if (state.LastDeliveredSequence < m_changeSequence &&
                (m_changeJournal.Count == 0 || oldest > state.LastDeliveredSequence + 1))
            {
                // 缺失的改动已经被有界历史裁掉：不能靠"按序号补发"补齐。
                state.RepairFromSequence = -1L;
                m_cellChangeRepairUnavailable++;
                if (!state.RepairUnavailableLogged)
                {
                    state.RepairUnavailableLogged = true;
                    CatchUpProblem?.Invoke("改动补发不可用（clientId=" + state.Id + "）：这条连接最后送达序号=" +
                        state.LastDeliveredSequence + "，历史最旧=" +
                        (m_changeJournal.Count == 0 ? "empty" : oldest.ToString()) +
                        "，主机水位=" + m_changeSequence +
                        " → 缺口已被有界历史裁掉，本连接无法按序号补齐；这些块由强制核对" +
                        "（摘要不一致 → 主机回传整块）或下次重连整图/追帧纠正（绝不假装已同步）");
                    // 主机侧缺口：立刻要求客户端重新核对全部内存块（客户端有强制核对队列，有界）。
                    SendVerifyAllRequestLocked(state);
                }
                return 0;
            }
            int budget = Math.Max(1, m_options.CellChangeRepairPerTick);
            int sent = 0;
            foreach (ChangeJournalEntry entry in m_changeJournal)
            {
                if (entry.Sequence <= state.LastDeliveredSequence) continue;
                if (budget <= 0) break;
                long prevIndex = state.DeliveryIndex;
                byte[] payload = EncodeCellChange(entry.Key, entry.Edit, entry.Sequence, prevIndex,
                    prevIndex + 1, replay: false);
                if (!m_server.TrySend(state.Id, MessageType.CellChange, payload))
                {
                    // 显式背压：这一条没发出去，下一 tick 从同一位置继续（顺序不变，绝不跳过）。
                    m_cellChangeRepairDeferrals++;
                    break;
                }
                budget--;
                sent++;
                state.DeliveryIndex = prevIndex + 1;
                state.LastDeliveredSequence = entry.Sequence;
                state.RepairedChanges++;
                state.CellChangesSent++;
                m_cellChanges++;
                m_cellChangeRepairs++;
                // **实机根因路径**：背压漏掉的那条改动在这里按历史补齐 —— 客户端内容因此与主机一致，
                // 但它本地校验器是 Fresh、不会自发重报（"重新核对过、什么都没变"）。这块若还是红，
                // 必须重新裁决，否则红框永远留着（实机 (30,-4)/(30,-3)）。
                NoteVerdictNeedsRejudge(state, entry.Key,
                    "按历史补发缺失改动（序号 " + entry.Sequence + "）");
            }
            if (state.LastDeliveredSequence >= m_changeSequence)
            {
                state.RepairFromSequence = -1L;
                CatchUpTrace?.Invoke("改动补发完成（clientId=" + state.Id + "）：累计补发 " +
                    state.RepairedChanges + " 条，最后送达序号=" + state.LastDeliveredSequence +
                    "（与主机水位 " + m_changeSequence + " 一致；本连接从此刻起恢复实时投递）");
            }
            return sent;
        }

        /// <summary>
        /// 主机检测到自己**无法按序号补齐缺口**（背压期间历史被裁掉）→ 要求客户端把内存块重新上报摘要。
        /// 这是"主机侧缺口"唯一能触达客户端的通道：客户端上报 → 主机比对 → 不一致回传整块（有界、逐块）。
        /// 发送失败 = 显式背压：计数 + 一行原因（不假装已经要求过）。调用方已持有 m_gate。
        /// </summary>
        private void SendVerifyAllRequestLocked(State state)
        {
            // 空列表 = 全部内存块。
            var request = new PT.VerifyChunks();
            if (!m_server.TrySend(state.Id, MessageType.VerifyChunks, request.ToByteArray()))
            {
                m_verifyRequestSendFailures++;
                CatchUpProblem?.Invoke("无法要求客户端重新核对内存区块（clientId=" + state.Id +
                    "）：请求没发出去（显式背压 / 连接不可达）→ 本连接仍处于\"缺口无法按序号补齐\"状态，" +
                    "下次重连由整图 / 追帧纠正（累计发送失败 " + m_verifyRequestSendFailures + " 次）");
                return;
            }
            m_verifyRequestsSent++;
            CatchUpTrace?.Invoke("已要求客户端重新核对内存区块（clientId=" + state.Id +
                "）：全部内存块；客户端上报后不一致的由主机回传整块（累计请求 " + m_verifyRequestsSent + " 次）");
        }

        // ------------------------------------------------------------ 陈旧裁决自愈（红框重判）

        /// <summary>
        /// 主机刚刚**把内容补给了这条身份**（补发缺失改动 / 整块回传快照 / 追帧重放 / 实时改动送达），
        /// 或刚对它判了 Mismatch：那块如果当前是红（<see cref="ChunkVerifyState.Failed"/>），这条结论
        /// 可能已经过期 —— 客户端可能早已与主机一致，而它的本地校验器停在 Fresh，**不会自发重报**
        /// （实机：背压那条 CellChange 由历史补发后，(30,-4)/(30,-3) 一直是红）。
        /// 这里只**登记**（同一块只一条、不重置清除上限），真正要摘要由
        /// <see cref="TickStaleVerdicts"/> 按预算 / 退避 / 次数上限执行 —— 不在热路径逐块发消息。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void NoteVerdictNeedsRejudge(State state, ChunkKey key, string reason)
        {
            if (state == null) return;
            NoteVerdictNeedsRejudge(state.Session, key, reason);
        }

        private void NoteVerdictNeedsRejudge(PlayerSessionRecord session, ChunkKey key, string reason)
        {
            if (session == null) return;
            if (!session.Verdicts.TryGetValue(key, out byte value) ||
                value != (byte)ChunkVerifyState.Failed) return;
            long nowMs = Environment.TickCount64;
            long due = nowMs + Math.Max(1, m_options.StaleVerdictRejudgeRetryMs);
            if (session.StaleVerdicts.TryGetValue(key, out StaleVerdictWatch existing))
            {
                // 已在册：只把下一次请求安排到退避之后；**不**重置首次登记时刻（清除上限仍然有界）。
                if (existing.NextRequestMs > due) existing.NextRequestMs = due;
                existing.LastReason = reason ?? string.Empty;
                return;
            }
            session.StaleVerdicts[key] = new StaleVerdictWatch
            {
                Key = key,
                FirstSeenMs = nowMs,
                NextRequestMs = due,
                LastReason = reason ?? string.Empty
            };
            session.StaleVerdictScan.Enqueue(key);
        }

        /// <summary>
        /// 每 tick 推进"陈旧红 → 重新裁决"台账（有界；台账挂在**身份记录**上，跨重连保留）：
        ///   · 结论已经不是红（新鲜上报改判成绿 / 蓝，或这块被清了）→ 结束这一轮；
        ///   · 连接暂不可达 → 留在册里（重连后继续），不发送、也不清；
        ///   · 主机还欠这块一份整块回传快照（内容在路上）→ 先不重复要摘要，也不清红；
        ///   · 到点 → 合并成**一条**单块 VerifyChunks：客户端把它排进强制核对（允许 Fresh），
        ///     于是"本地早已一致、不会自发重报"的块也会给主机一份新鲜摘要；
        ///   · 要过摘要、过了清除上限仍然没有新裁决 → 明确清掉这条陈旧的红（计数 + 一行日志）。
        /// 绝不伪造 Match、绝不静默清状态。调用方已持有 m_gate。
        /// </summary>
        private int TickStaleVerdicts(State state, long nowMs)
        {
            PlayerSessionRecord session = state.Session;
            if (session == null || session.StaleVerdictScan.Count == 0) return 0;
            int initial = session.StaleVerdictScan.Count;
            int work = 0;
            int budget = Math.Max(1, m_options.StaleVerdictRequestsPerTick);
            m_staleVerdictBatch.Clear();
            for (int i = 0; i < initial && session.StaleVerdictScan.Count > 0; i++)
            {
                ChunkKey key = session.StaleVerdictScan.Dequeue();
                if (!session.StaleVerdicts.TryGetValue(key, out StaleVerdictWatch watch)) continue;
                if (!session.Verdicts.TryGetValue(key, out byte value) ||
                    value != (byte)ChunkVerifyState.Failed)
                {
                    // 结论已经不是红（新鲜上报把它改判成绿 / 蓝，或这块被清了）：这一轮自愈结束。
                    session.StaleVerdicts.Remove(key);
                    continue;
                }
                if (!CanStillReachPeer(state))
                {
                    // 连接暂不可达：留在册里（同一身份重连后继续），这一拍不发、也不清。
                    session.StaleVerdictScan.Enqueue(key);
                    continue;
                }
                if (HasQueuedResyncLocked(state, key))
                {
                    // 主机还欠这块一份整块快照：内容会到，客户端落地后会按新规则再证明一次。
                    // 这时既不重复要摘要（同一块两条请求），也不清红（内容还在路上）。
                    session.StaleVerdictScan.Enqueue(key);
                    continue;
                }
                if (watch.Requests >= 1 && watch.LastRequestMs != 0 &&
                    nowMs - watch.LastRequestMs >= Math.Max(1, m_options.StaleVerdictClearMs))
                {
                    ClearStaleVerdictLocked(state, session, watch, nowMs);
                    work++;
                    continue;
                }
                if (watch.Requests >= Math.Max(1, m_options.StaleVerdictRejudgeMaxRequests))
                {
                    // 请求次数用尽、还没到清除上限：等新鲜裁决或上限到点。
                    session.StaleVerdictScan.Enqueue(key);
                    continue;
                }
                if (nowMs < watch.NextRequestMs || budget <= 0)
                {
                    session.StaleVerdictScan.Enqueue(key);
                    continue;
                }
                m_staleVerdictBatch.Add(key);
                budget--;
                session.StaleVerdictScan.Enqueue(key);
            }
            if (m_staleVerdictBatch.Count > 0) work += SendStaleVerdictRequests(state, session, nowMs);
            return work;
        }

        /// <summary>这条连接上是否还排着某块的整块回传快照（内容在路上）。调用方已持有 m_gate。</summary>
        private static bool HasQueuedResyncLocked(State state, ChunkKey key)
        {
            foreach (PendingResyncJob job in state.PendingResync)
                if (job.Key.Equals(key)) return true;
            return false;
        }

        /// <summary>
        /// 把本 tick 到点的区块合并成**一条** VerifyChunks（只列这些块）发给客户端：
        /// 客户端收到后把它们排进强制核对（全部校验口径允许 Fresh），从而给主机一份新鲜摘要。
        /// 发送失败 = 显式背压：请求次数**退回**、下一拍再要（绝不假装已经要过）。调用方已持有 m_gate。
        /// </summary>
        private int SendStaleVerdictRequests(State state, PlayerSessionRecord session, long nowMs)
        {
            var message = new PT.VerifyChunks();
            for (int i = 0; i < m_staleVerdictBatch.Count; i++)
            {
                ChunkKey key = m_staleVerdictBatch[i];
                message.Chunks.Add(new PT.ChunkKey { X = key.X, Z = key.Z });
            }
            if (!m_server.TrySend(state.Id, MessageType.VerifyChunks, message.ToByteArray()))
            {
                m_staleVerdictRefreshDeferred++;
                long due = nowMs + Math.Max(1, m_options.StaleVerdictRejudgeRetryMs);
                for (int i = 0; i < m_staleVerdictBatch.Count; i++)
                {
                    if (!session.StaleVerdicts.TryGetValue(m_staleVerdictBatch[i],
                            out StaleVerdictWatch watch)) continue;
                    if (watch.Requests > 0) watch.Requests--;       // 没发出去就不算要过
                    if (watch.NextRequestMs < due) watch.NextRequestMs = due;
                }
                NoteStaleVerdictProblem(state, session, m_staleVerdictBatch[0],
                    "请客户端重新核对这块的请求没发出去（显式背压 / 连接不可达）→ 下一拍再要，" +
                    "这块的红**暂不改判**（累计没发出去 " + m_staleVerdictRefreshDeferred + " 次）");
                return 0;
            }
            int sent = m_staleVerdictBatch.Count;
            m_staleVerdictRefreshRequested += sent;
            for (int i = 0; i < m_staleVerdictBatch.Count; i++)
            {
                ChunkKey key = m_staleVerdictBatch[i];
                if (!session.StaleVerdicts.TryGetValue(key, out StaleVerdictWatch watch)) continue;
                watch.Requests++;
                watch.LastRequestMs = nowMs;
                watch.NextRequestMs = nowMs + Math.Max(1, m_options.StaleVerdictRejudgeRetryMs);
                if (watch.LastLogMs == 0 ||
                    nowMs - watch.LastLogMs >= Math.Max(1, m_options.StaleVerdictLogIntervalMs))
                {
                    watch.LastLogMs = nowMs;
                    NoteStaleVerdictTransition(state, session, key,
                        "这块仍停在 Mismatch（红），但主机已经把内容补给它了（原因：" + watch.LastReason +
                        "）→ 主动请客户端重新核对这块（第 " + watch.Requests + " 次；上限 " +
                        Math.Max(1, m_options.StaleVerdictRejudgeMaxRequests) + " 次）",
                        warning: false);
                }
            }
            return sent;
        }

        /// <summary>
        /// 到达上限仍没有新鲜裁决 → **明确清掉**这条陈旧的红（实时地图 s=3 → s=0，不画红框）：
        /// 客户端可能已经不持有这块 / 不再上报（它自己的校验器是 Fresh 时不会自发重报）。
        /// 计数 + 一行点名区块的日志；绝不静默。调用方已持有 m_gate。
        /// </summary>
        private void ClearStaleVerdictLocked(State state, PlayerSessionRecord session,
            StaleVerdictWatch watch, long nowMs)
        {
            session.StaleVerdicts.Remove(watch.Key);
            if (session.Verdicts.TryGetValue(watch.Key, out byte value) &&
                value == (byte)ChunkVerifyState.Failed)
            {
                session.Verdicts.Remove(watch.Key);
            }
            m_staleVerdictCleared++;
            NoteStaleVerdictTransition(state, session, watch.Key,
                "陈旧的红已清除（s=3 → 0，不再画红框）：主机要过 " + watch.Requests +
                " 次新鲜摘要、最后一次之后等了 " + ((nowMs - watch.LastRequestMs) / 1000) +
                "s（首次登记到现在 " + ((nowMs - watch.FirstSeenMs) / 1000) + "s）仍没有新裁决" +
                "（原因：" + watch.LastReason + "；客户端可能已不持有这块 / 不再上报）——" +
                "累计清除 " + m_staleVerdictCleared + " 块", warning: true);
        }

        /// <summary>陈旧裁决自愈的一条明确日志（请求 / 重判 = Info；清除 = Warning）。</summary>
        private void NoteStaleVerdictTransition(State state, PlayerSessionRecord session, ChunkKey key,
            string reason, bool warning)
        {
            string who = session == null
                ? "clientId=" + (state != null ? state.Id : -1)
                : "身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                  "（稳定键 " + session.StableKey + " · clientId=" + (state != null ? state.Id : -1) + "）";
            string line = "区块 " + key + " 的裁决自愈：" + reason + "；" + who;
            if (warning) ReportUnresolved?.Invoke(line);
            else PendingTrace?.Invoke(line);
        }

        /// <summary>请求没发出去（显式背压 / 不可达）→ Warning，全局限流（计数每次都涨）。</summary>
        private void NoteStaleVerdictProblem(State state, PlayerSessionRecord session, ChunkKey key,
            string reason)
        {
            long nowMs = Environment.TickCount64;
            if (m_lastStaleVerdictProblemLogMs != 0 &&
                nowMs - m_lastStaleVerdictProblemLogMs < m_options.ClientChunkLogIntervalMs) return;
            m_lastStaleVerdictProblemLogMs = nowMs;
            NoteStaleVerdictTransition(state, session, key, reason, warning: true);
        }

        public int SubscriberCount(ChunkKey key)
        {
            lock (m_gate) return m_subscribers.TryGetValue(key, out HashSet<int> subs) ? subs.Count : 0;
        }

        public int ClientCount { get { lock (m_gate) return m_clients.Count; } }

        /// <summary>被观察的玩家端（实时地图着色用它）：id 最小的 Player 客户端；没有就 null。</summary>
        private State FindWatchedPlayer()
        {
            State best = null;
            foreach (KeyValuePair<int, State> pair in m_clients)
            {
                if (pair.Value.Kind != ClientKind.Player) continue;
                if (best == null || pair.Key < best.Id) best = pair.Value;
            }
            return best;
        }

        /// <summary>
        /// 实时地图用：某块相对被观察身份（<see cref="FindWatchedPlayer"/>）的校验状态
        /// （没上报过这块 = <see cref="ChunkVerifyState.None"/>）。
        ///
        /// **只有"纳入了上报"的块才给状态**：用户口径"只有纳入上报的区域才显示方块，客户端不上报的就不画框"。
        /// 所以这里以 <see cref="PlayerSessionRecord.ReportedLoaded"/>（= 收到过 <c>ChunkDigestReport</c> 的块）为准：
        /// 客户端没上报的块，即使落在主机按兴趣中心圈出的方形窗口里、主机手上也有内容，
        /// 也一律返回 None → 实时地图 s=0 → 网页不画框。
        /// **状态按身份读**：平板重连（新 clientId）后仍是同一条记录 ⇒ 上一轮的绿/黄/红结论继续有效。
        /// </summary>
        public ChunkVerifyState VerifyStateOf(ChunkKey key)
        {
            lock (m_gate)
            {
                State state = FindWatchedPlayer();
                PlayerSessionRecord session = state?.Session;
                if (session == null) return ChunkVerifyState.None;
                if (!session.ReportedLoaded.Contains(key)) return ChunkVerifyState.None;
                return session.Verdicts.TryGetValue(key, out byte value)
                    ? (ChunkVerifyState)value
                    : ChunkVerifyState.None;
            }
        }

        /// <summary>
        /// 实时地图（v7）用：某块相对**指定身份**的校验状态 —— 每个玩家端的线框按它自己的裁决着色，
        /// 而不是所有客户端都套"被观察客户端"（<see cref="FindWatchedPlayer"/>）的状态。
        /// 身份用**稳定键**匹配（重连后 clientId 变了、键不变）；同一身份的多条连接共享同一条会话记录。
        /// 该身份没上报过这块 = <see cref="ChunkVerifyState.None"/>（网页不画线框，绝不按别的身份猜）。
        /// </summary>
        public ChunkVerifyState VerifyStateOf(string stableKey, ChunkKey key)
        {
            if (string.IsNullOrEmpty(stableKey)) return ChunkVerifyState.None;
            lock (m_gate)
            {
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    PlayerSessionRecord session = pair.Value.Session;
                    if (session == null) continue;
                    if (!string.Equals(session.StableKey, stableKey, StringComparison.Ordinal)) continue;
                    // 同键只有一条身份记录；万一出现重复也继续找（不因为第一条没上报就提前判 None）
                    if (!session.ReportedLoaded.Contains(key)) continue;
                    return session.Verdicts.TryGetValue(key, out byte value)
                        ? (ChunkVerifyState)value
                        : ChunkVerifyState.None;
                }
                return ChunkVerifyState.None;
            }
        }

        /// <summary>实时地图用：被观察身份的**稳定键**（身份；空 = 还没有玩家端）。</summary>
        public string WatchedStableKey
        {
            get
            {
                lock (m_gate)
                {
                    PlayerSessionRecord session = FindWatchedPlayer()?.Session;
                    return session?.StableKey ?? string.Empty;
                }
            }
        }

        /// <summary>实时地图用：被观察身份当前传输句柄的 id（-1 = 还没有玩家端）。</summary>
        public int WatchedClientId
        {
            get { lock (m_gate) { State state = FindWatchedPlayer(); return state?.Id ?? -1; } }
        }

        /// <summary>实时地图用：被观察身份的当前名字（重连后以最新一次 Hello 为准）。</summary>
        public string WatchedClientName
        {
            get
            {
                lock (m_gate)
                {
                    State state = FindWatchedPlayer();
                    if (state == null) return string.Empty;
                    PlayerSessionRecord session = state.Session;
                    return session != null && !string.IsNullOrEmpty(session.DisplayName)
                        ? session.DisplayName
                        : state.Name;
                }
            }
        }

        // ------------------------------------------------------------ 欠更新计数（实时地图格内数字）

        /// <summary>
        /// 实时地图用：该区块"有几个在线身份当前内存集合里没有它"（0 = 不标注；按身份计数，重连不会算成两个）。
        /// 集合成员只由"改动时谁没有这一块"与"谁后来把它加载进内存"决定；与主机自己是否加载了这一块、
        /// CellChange 是否送达、裁决结果都无关（用户口径：主机卸载后标记照旧留着）。
        /// </summary>
        public int PendingClientCount(ChunkKey key)
        {
            lock (m_gate) return m_pendingClients.TryGetValue(key, out Dictionary<string, long> set) ? set.Count : 0;
        }

        /// <summary>
        /// 会话 / 房间结束：整体清空欠更新计数**与全部身份会话记录**（**只在内存里**，
        /// 不写文件 / Project.xml / 世界目录），并写一行明确日志。返回清掉了几个标块。
        /// 调用方：模块的 StopChannel（会话结束的唯一入口）。
        /// </summary>
        public int ClearPendingMarks()
        {
            lock (m_gate)
            {
                int marks = m_pendingClients.Count;
                m_pendingClients.Clear();
                int sessions = m_sessions.Count;
                int degraded = m_degradedSessions.Count;
                int reported = 0;
                // 会话结束：按需单块加载的临时位置**全部撤掉**（不把上一个房间的块留在新世界里）。
                int onDemand = m_onDemand.Clear();
                m_sessions.Clear();
                m_degradedSessions.Clear();
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    if (pair.Value.Session == null) continue;
                    reported += pair.Value.Session.ReportedLoaded.Count;
                    pair.Value.Session = null;
                }
                // 会话结束是一次性事件（不是热路径）：**无论有没有标记都写一行**，让"清空了"这件事有据可查。
                PendingTrace?.Invoke("会话结束：欠更新计数整体清空（清掉 " + marks +
                    " 个区块的标记；该计数只在内存里，不落盘、不进 Project.xml / 世界目录）");
                SessionTrace?.Invoke("会话结束：身份会话记录整体清空（清掉 " + sessions + " 条身份记录 / " +
                    degraded + " 条无身份退化记录；被引用的已上报区块共 " + reported +
                    " 条 —— 这些状态只在内存里，下次加入从头开始）");
                if (onDemand > 0)
                {
                    OnDemandTrace?.Invoke("会话结束：撤掉 " + onDemand +
                        " 条按需单块加载的临时位置（客户端上报的边缘块；随会话一起回收，不跨房间保留）");
                }
                // 会话结束：改动历史 / 追帧任务 / 改动水位一起清（只清本主机实例；新房间会生成新会话标识）。
                int journal = m_changeJournal.Count;
                int catchUps = m_catchUps.Count;
                m_changeJournal.Clear();
                m_catchUps.Clear();
                m_changeSequence = 0L;
                m_changeJournalEvictions = 0L;
                m_lastChangeJournalEvictionLogMs = 0L;
                m_rejoinAccepted = 0L;
                m_rejoinRejected = 0L;
                m_catchUpRejected = 0L;
                m_catchUpRounds = 0L;
                m_cellChangeMemoryRecipients = 0L;
                m_cellChangeMemoryOnly = 0L;
                m_cellChangeOutsideWindow = 0L;
                m_cellChangeDeferrals = 0L;
                m_cellChangeRepairs = 0L;
                m_cellChangeRepairDeferrals = 0L;
                m_cellChangeRepairUnavailable = 0L;
                m_owedSnapshotsQueued = 0L;
                m_owedSnapshotsSkippedNoContent = 0L;
                m_verifyRequestsSent = 0L;
                m_verifyRequestSendFailures = 0L;
                m_staleVerdictRefreshRequested = 0L;
                m_staleVerdictRefreshed = 0L;
                m_staleVerdictCleared = 0L;
                m_staleVerdictRefreshDeferred = 0L;
                m_lastStaleVerdictProblemLogMs = 0L;
                m_staleVerdictBatch.Clear();
                m_lastOwedSnapshotLogMs = 0L;
                m_clientChunkDeltaSnapshotsSinceLog = 0L;
                m_cellChangeSendFailures = 0L;
                m_lastCellChangeSendFailureLogMs = 0L;
                m_catchUpChangesSent = 0L;
                m_catchUpSendDeferrals = 0L;
                m_catchUpAckTimeouts = 0L;
                m_lastCatchUpBarrierFailureLogMs = 0L;
                CatchUpTrace?.Invoke("会话结束：改动历史清空（清掉 " + journal + " 条改动 / " + catchUps +
                    " 个追帧任务；改动序号归零）—— 这些状态只在内存里，不落盘、不进世界目录");
                return marks;
            }
        }

        /// <summary>
        /// 区块有权威改动 → 逐个**在线玩家身份**按它自己的当前内存集合结算：
        ///   · <see cref="PlayerSessionRecord.MemoryChunks"/> **不含**这一块 → 加进欠更新集合
        ///     （同一身份只算一次，重连前后同一个稳定键）；
        ///   · 含这一块 → **不加**，并移除它可能残留的标记（旧规则留下的 / 加载与改动交错留下的）。
        /// **不看订阅表、不看 CellChange 是否送达、不看裁决**：数字只回答"谁当前内存里没有这一块"。
        /// 集合里存的是**身份稳定键 → 最早欠账的改动序号**：同一身份的新旧连接、重连前后都只算一个
        /// （不重复、不丢失）；序号用于"身份后来开始持有这块"时补一份最新快照（见
        /// <see cref="EnqueueChunkSnapshotLocked"/>），把 ClientChunks 在途窗口里的那次改动也补上。
        /// 只在 0 → ≥1 时写一行"记为"（同一块的连续改动不重复刷屏）；真的移除了残留标记才写一行"移除"。
        /// 没有任何在线身份时不建空条目（0 不标注）。调用方已持有 m_gate。
        /// </summary>
        private void MarkChunkChanged(ChunkKey key, CellEdit edit, long sequence)
        {
            m_pendingClients.TryGetValue(key, out Dictionary<string, long> set);
            bool wasEmpty = set == null || set.Count == 0;
            int added = 0;
            int removed = 0;
            foreach (KeyValuePair<int, State> pair in m_clients)
            {
                State state = pair.Value;
                if (state.Kind != ClientKind.Player) continue;   // 调试端（实时地图工具）只观察，不算客户端
                PlayerSessionRecord session = state.Session;
                if (session == null) continue;                   // 没有身份记录（还没 Hello）：不往计数里加
                // 同一身份只由**当前句柄**那条连接代表（旧连接还在表里也不会算成两个）
                if (session.CurrentClientId != state.Id) continue;
                if (!CanStillReachPeer(state)) continue;         // 已经不可达：由离开清理负责，不往计数里加
                if (session.MemoryChunks.Contains(key))
                {
                    // 它内存里已经有这一块 → 这份改动不欠它；顺手清掉可能残留的标记。
                    if (set != null && set.Remove(session.StableKey)) removed++;
                    continue;
                }
                if (set == null)
                {
                    set = new Dictionary<string, long>(StringComparer.Ordinal);
                    m_pendingClients[key] = set;
                }
                // 记住**最早**那笔欠账的改动序号：身份后来开始持有这块时用它判断"要不要补一份快照"。
                if (!set.ContainsKey(session.StableKey))
                {
                    set[session.StableKey] = sequence;
                    added++;
                }
            }
            if (set != null && set.Count == 0)
            {
                m_pendingClients.Remove(key);
                set = null;
            }
            if (removed > 0)
            {
                PendingTrace?.Invoke("区块 " + key + " 有改动 (" + edit.X + "," + edit.Y + "," + edit.Z +
                    ") → 该块已在 " + removed + " 个在线身份的内存集合里 → 移除它们残留的欠更新标记" +
                    "（该区块还剩 " + (set == null ? 0 : set.Count) + " 个身份当前内存里没有这一块）");
            }
            if (set == null) return;
            if (!wasEmpty) return;
            PendingTrace?.Invoke("区块 " + key + " 有改动 (" + edit.X + "," + edit.Y + "," + edit.Z +
                ") → 记为 " + set.Count + " 个在线身份当前内存里没有这一块（本次新加 " + added + " 个）");
        }

        /// <summary>
        /// 身份刚"开始持有"这块、而主机在它没有这块时记过一笔改动 → 直接补一份**当前整块快照**。
        /// 为什么需要：ClientChunks 的增量可能还在路上，主机当时按"它没有这块"处理了那次改动
        /// （不投递 + 记欠账）；增量到达时欠账被清掉，但改动本身不会自动补 —— 这一份快照就是补齐。
        /// 快照在 Tick 里**发送时才编码**（编码水位 = 当时的改动水位），客户端若已应用更新的改动会丢弃它并强制核对。
        /// 主机自己没有该块内容 → 不假装补过：明确计数，由客户端加载时的摘要核对兜住。
        /// 队列里已有同一块的快照则不重复排队。调用方已持有 m_gate。
        /// </summary>
        private bool EnqueueChunkSnapshotLocked(State state, ChunkKey key, long owedSequence)
        {
            if (state == null) return false;
            if (!m_world.HasContents(key))
            {
                m_owedSnapshotsSkippedNoContent++;
                return false;
            }
            foreach (PendingResyncJob queued in state.PendingResync)
                if (queued.Key.Equals(key)) return false;
            state.PendingResync.Enqueue(new PendingResyncJob { Key = key });
            m_owedSnapshotsQueued++;
            // 这份快照把"它开始持有这块时可能错过的那次改动"补上了：这块若还是红，必须重新裁决。
            NoteVerdictNeedsRejudge(state, key, "补发整块快照（清除欠账）");
            long nowMs = Environment.TickCount64;
            if (m_lastOwedSnapshotLogMs == 0 ||
                nowMs - m_lastOwedSnapshotLogMs >= m_options.ClientChunkLogIntervalMs)
            {
                m_lastOwedSnapshotLogMs = nowMs;
                ClientChunkSetTrace?.Invoke("身份开始持有区块 " + key +
                    "（清除欠账；最早欠账改动序号 " + owedSequence +
                    "）→ 已排队一份当前整块快照（发送时取最新水位；累计 " + m_owedSnapshotsQueued +
                    " 份 · 主机无内容跳过 " + m_owedSnapshotsSkippedNoContent + " 次）");
            }
            return true;
        }

        /// <summary>
        /// 某身份的当前内存集合**新增**了区块 X（<c>ClientChunks</c> 增量 add；整集替换时对新集合逐块调用）
        /// → 从 X 的欠更新集合里去掉它（用户口径："走进 A 的区域、当前加载集合包含该块 → 它的欠账清零"）。
        /// 集合里存身份稳定键，所以重连前后是同一个标记；移掉最后一个后计数为 0（网页不画数字）。
        /// 只有真的移除了才返回 true；集合空条目一并删掉。逐块**不写日志**（可能一次几百块），
        /// 调用方把清除数量汇总进限流的增量 / 整集日志。调用方已持有 m_gate。
        /// </summary>
        private bool ClearPendingOnChunkLoaded(PlayerSessionRecord session, ChunkKey key,
            out long owedSequence)
        {
            owedSequence = 0L;
            if (session == null) return false;
            if (!m_pendingClients.TryGetValue(key, out Dictionary<string, long> set)) return false;
            if (!set.TryGetValue(session.StableKey, out owedSequence)) return false;
            set.Remove(session.StableKey);
            if (set.Count == 0) m_pendingClients.Remove(key);
            return true;
        }

        /// <summary>
        /// 一个**身份**离开联机（主动 Bye / 链路失效）→ 把它从**所有**区块的欠更新集合里去掉。
        /// 这就是"退出联机后原本属于它的标记全部消失，再进来时它又是全 0"的实现（加入时不补标记）。
        /// 键是身份稳定键 ⇒ 同一身份重连前后是同一个键，既不会丢、也不会算两遍。
        /// 只有真的移除了标记才写一行（返回移掉的标块数）。调用方已持有 m_gate。
        /// </summary>
        private int RemovePendingClientEverywhere(PlayerSessionRecord session, string why)
        {
            if (session == null || m_pendingClients.Count == 0) return 0;
            int marks = 0;
            List<ChunkKey> emptied = null;
            foreach (KeyValuePair<ChunkKey, Dictionary<string, long>> pair in m_pendingClients)
            {
                if (!pair.Value.Remove(session.StableKey)) continue;
                marks++;
                if (pair.Value.Count == 0) (emptied ?? (emptied = new List<ChunkKey>())).Add(pair.Key);
            }
            if (emptied != null)
            {
                for (int i = 0; i < emptied.Count; i++) m_pendingClients.Remove(emptied[i]);
            }
            if (marks > 0)
            {
                PendingTrace?.Invoke("身份 " + PlayerSessionKeys.ShortIdentity(session.Identity) +
                    "（稳定键 " + session.StableKey + "）" + why + " → 从 " + marks +
                    " 个区块的欠更新计数里移除它的标记（这些区块不再把它算作\"当前内存里没有这一块\"）");
            }
            return marks;
        }

        /// <summary>日志用的角色名（没有名字就写 clientId，绝不空着）。</summary>
        private static string DisplayName(State state) =>
            string.IsNullOrEmpty(state.Name) ? ("client#" + state.Id) : state.Name;

        /// <summary>
        /// 主机 → 客户端：把"全部权威角色的位置快照批"发给每个**玩家端**（调试端只观察，不下发）。
        ///
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:38-101
        ///         （SendGamePlayerPositionMessage 把本机玩家 + 每个联机角色折成 positionBatch，
        ///           最后 NetworkMessageSender.SendPlayerPositionBatch）
        /// Source: Mod/ScMultiplayer/Networking/NetworkMessageSender.cs:220-232
        ///         （SendPlayerPositionBatch → SendScheduledMessage(-1, msg, latest: true)：-1 = 广播给所有就绪客户端）
        /// 发送失败（队列满 = 显式背压）**不静默**：计入返回值的失败数，由调用方写日志。
        /// </summary>
        public int BroadcastPlayerPositions(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return 0;
            var targets = new List<int>();
            lock (m_gate)
            {
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    if (pair.Value.Kind != ClientKind.Player) continue;
                    targets.Add(pair.Key);
                }
            }
            int sent = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                if (!m_server.TrySend(targets[i], MessageType.PlayerPositions, payload)) continue;
                sent++;
            }
            return sent;
        }

        /// <summary>
        /// 主机：当前**玩家端**的 clientId（多本机角色时位置批要按端过滤：旧的单角色端只收槽位 0）。
        /// 顺序按 clientId 升序，调用方可以逐端构建自己的负载。
        /// </summary>
        public int[] GetPlayerClientIds()
        {
            var targets = new List<int>();
            lock (m_gate)
            {
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    if (pair.Value.Kind != ClientKind.Player) continue;
                    targets.Add(pair.Key);
                }
            }
            targets.Sort();
            return targets.ToArray();
        }

        /// <summary>
        /// 主机 → 指定玩家端：一份位置快照批。失败（背压 / 不可达）返回 false，调用方明确计数，
        /// **绝不改投别的客户端**。
        /// </summary>
        public bool SendPlayerPositionsTo(int clientId, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return false;
            lock (m_gate)
            {
                if (!m_clients.TryGetValue(clientId, out State state) ||
                    state.Kind != ClientKind.Player)
                    return false;
            }
            return m_server.TrySend(clientId, MessageType.PlayerPositions, payload);
        }
    }

    /// <summary>
    /// 客户端侧地形同步：**块加载完算一次摘要 → 上报**；裁决后标记；
    /// 收到重传就落地并**再报一次确认**；加载后的改动走 CellChange。
    ///
    /// **每渲染帧预算**（本轮改动，解决"平板飞行时帧数掉到个位数"）：
    ///   · 块加载事件只**入队**（<see cref="OnChunkLoaded"/>），摘要与上报由 <see cref="Tick"/> 按
    ///     <see cref="TerrainSyncOptions.ClientDigestComputationsPerTick"/> 逐帧消化；
    ///   · 回传分片只**入队**（<see cref="HandleResync"/>），落地 + 末片重算受
    ///     <see cref="TerrainSyncOptions.ClientResyncPartsPerTick"/> 限制；
    ///   · 加载后的改动只把块**标脏**（<see cref="HandleCellChange"/>），整块摘要重算受
    ///     <see cref="TerrainSyncOptions.ClientHashRefreshesPerTick"/> 限制
    ///     （旧实现是"每收到一格改动就重算 65536 格"，主机一个会话广播 10632 条改动 = 2 秒多的纯计算）。
    /// 语义不变：每块**最终一定**上报/核对（FIFO，不丢、不假成功）；只有"块已卸载"才作废，并计入
    /// 明确计数器（<see cref="TerrainClientSnapshot.DigestsDroppedUnloaded"/> 等）。
    /// 另外维护**内存区块集合**（= 引擎已分配的块，含内容还没定稿的海洋 / 水面），供 v8 实时地图的
    /// 客户端线框使用；它只认 <see cref="OnMemoryChunkAllocated"/> / <see cref="OnMemoryChunkFreed"/>，
    /// 与摘要上报（只认内容可用的 <see cref="OnChunkLoaded"/>）是两条独立事实。
    /// 裁决状态机、NotReady 重报退避、欠更新计数、按身份归档的记录都不在这个类里，本轮未改动。
    /// 演示页标记：NET04。
    /// </summary>
    public sealed class TerrainSyncClient
    {
        private readonly ITerrainWorld m_world;
        private readonly TcpTerrainClient m_channel;
        private readonly TerrainSyncOptions m_options;
        private readonly MessageChannel m_messages;
        private readonly ChunkLoadVerifier m_verifier = new ChunkLoadVerifier();
        private readonly Dictionary<ChunkKey, uint> m_localHashes = new Dictionary<ChunkKey, uint>();
        private readonly Dictionary<ChunkKey, long> m_reportSentMs = new Dictionary<ChunkKey, long>();
        private readonly Dictionary<ChunkKey, ChunkKey> m_unused = new Dictionary<ChunkKey, ChunkKey>();
        private readonly Dictionary<ChunkKey, int> m_pendingParts = new Dictionary<ChunkKey, int>();
        private readonly Queue<DigestEntry> m_reportQueue = new Queue<DigestEntry>();
        /// <summary>收到 NotReady 的块 → 下一次重报的时刻（退避）。本端的保险：不等主机那边的时序。
        /// 重报次数上限由 <see cref="ChunkLoadVerifier.MaxRetries"/> 明确兜住（超限计数，不静默硬撑）。</summary>
        private readonly Dictionary<ChunkKey, NotReadyRetry> m_notReadyRetries = new Dictionary<ChunkKey, NotReadyRetry>();
        private long m_reportsSent, m_match, m_mismatch, m_resyncApplied, m_resyncVerified, m_cellChanges, m_retries;
        private long m_reReports, m_abandoned;
        private long m_lastVerifyMs, m_maxVerifyMs;

        // ---- 每帧预算队列（见类注释）----
        // **没有"超时作废"路径**：入队的块一定会在后续帧被算到（FIFO），因此不存在"报告计时器已过期
        // 但摘要还没算"的状态 —— 只有"块已卸载"才作废，并且每一种作废都有明确计数器。
        /// <summary>待算摘要的块（块加载事件入队；每帧按预算消化）。</summary>
        private readonly Queue<ChunkKey> m_digestQueue = new Queue<ChunkKey>();
        /// <summary>队列成员表：O(1) 去重 + 卸载时 O(1) 作废（<see cref="Queue{T}"/> 本身不能删中间项）。</summary>
        private readonly HashSet<ChunkKey> m_digestQueued = new HashSet<ChunkKey>();
        /// <summary>加载后被改动、等重算摘要的块（按块去重：同一块改 100 格只重算一次）。</summary>
        private readonly Queue<ChunkKey> m_dirtyQueue = new Queue<ChunkKey>();
        private readonly HashSet<ChunkKey> m_dirtyQueued = new HashSet<ChunkKey>();
        /// <summary>待落地的回传分片（原始载荷 + 入队时该块的"代"）。</summary>
        private readonly Queue<ResyncJob> m_resyncQueue = new Queue<ResyncJob>();
        /// <summary>每块当前的"代"：卸载时 +1，让排队中的旧分片在落地时被明确丢弃（不落到另一份内容上）。</summary>
        private readonly Dictionary<ChunkKey, int> m_chunkGeneration = new Dictionary<ChunkKey, int>();
        /// <summary>每块还有几个排队中的回传分片（用来决定"代"表什么时候可以清掉）。</summary>
        private readonly Dictionary<ChunkKey, int> m_queuedParts = new Dictionary<ChunkKey, int>();
        /// <summary>发送阶段复用的一批条目（避免每帧分配）。</summary>
        private readonly List<DigestEntry> m_reportBatch = new List<DigestEntry>();
        private long m_digestsComputed, m_digestsDroppedUnloaded, m_hashRefreshes;
        private long m_hashRefreshesDroppedUnloaded, m_resyncDroppedUnloaded, m_resyncDecodeFailures;
        private long m_reportEntriesSent, m_reportsDroppedUnloaded, m_reportSendDeferrals;
        /// <summary>上报条目到发送时内容还没定稿（内存里有这块）→ 退回未核对并排队强制核对（不丢、也不报旧状态）。</summary>
        private long m_reportsDeferredNotReady;
        private int m_digestsThisTick, m_reportsThisTick, m_resyncAppliedThisTick;
        private long m_saturatedSinceMs, m_lastSaturationLogMs, m_lastDecodeFailureLogMs;
        private long m_decodeFailuresSinceLog;

        // ---- 客户端"内存区块集合"（v8 实时地图客户端线框；= 引擎**已分配**的区块）----
        // 数据来源**只有** Engine/TerrainBridge 的 ChunkAllocated / ChunkFreed（模块转给
        // OnMemoryChunkAllocated / OnMemoryChunkFreed），这里不再扫一遍引擎 —— 同一份信号，避免两条
        // 扫描路径把集合算岔。**按"分配"记账，不按"内容可用"记账**：分配了但内容还没定稿的海洋 /
        // 水面块也要出现在集合里（用户口径："方框应该是整个客户端的内存中所有的 chunk"）。
        /// <summary>本端**内存**（引擎已分配）的区块集合（分配事件 add、释放事件 remove）。</summary>
        private readonly HashSet<ChunkKey> m_memoryChunks = new HashSet<ChunkKey>();
        /// <summary>
        /// 待发的**增量**（按键合并：同一块在发送前被反复加载/卸载只保留最后一次结果，
        /// 因此队列大小有上界 = 当前内存块数，不会被一次飞行灌爆；发送按消息分片，≤64 条/条消息）。
        /// </summary>
        private readonly Dictionary<ChunkKey, bool> m_clientChunkDeltaPending = new Dictionary<ChunkKey, bool>();
        /// <summary>待发的**整集分片**（每次 (重)连握手后按当前 MemoryChunks 重新生成）。</summary>
        private readonly List<byte[]> m_clientChunkFullParts = new List<byte[]>();
        /// <summary>与 <see cref="m_clientChunkFullParts"/> 一一对应的条目数（发送后累计 entries，不必回头解析载荷）。</summary>
        private readonly List<int> m_clientChunkFullPartEntries = new List<int>();
        private int m_clientChunkFullPartIndex;
        /// <summary>本连接内整集序号（从 1 递增；主机按它拼分片）。</summary>
        private int m_clientChunkFullSetId;
        /// <summary>每条消息复用的一批条目（避免每帧分配）。</summary>
        private readonly List<ClientChunkEntry> m_clientChunkEntryBuffer =
            new List<ClientChunkEntry>(ClientChunkSetCodec.MaxEntriesPerMessage);
        private long m_clientChunkFullSetsSent, m_clientChunkMessagesSent, m_clientChunkEntriesSent;
        private long m_clientChunkDeltaMessagesSent, m_clientChunkSendDeferrals;
        private int m_clientChunkMessagesThisTick;

        // ---- 有序应用 / 缺口检测（TCP 是可靠有序流，但"没有缺"必须能被证明）----
        /// <summary>收到的最后一个投递序号（主机每条连接从 0 开始，成功一帧 +1）。</summary>
        private long m_lastDeliveryIndex;
        /// <summary>是否已经收到过带投递序号的 CellChange（第一条要求 prev == 0）。</summary>
        private bool m_deliveryIndexSeen;
        private long m_deliveryGaps;
        private long m_lastDeliveryGapLogMs;
        private long m_outOfOrderChanges;
        private long m_lastOutOfOrderLogMs;
        private long m_cellChangesDroppedNotReady;
        private long m_cellChangesSkippedNotHeld;
        private long m_lastNotReadyChangeLogMs;
        private bool m_oldHostDeliveryIndexLogged;
        /// <summary>追帧重放进行中：此时"比水位还新"的实时改动先暂存（否则旧重放会把新改动盖回去）。</summary>
        private bool m_replayActive;
        private long m_replayTargetSequence = -1L;
        private long m_replayStartedMs;
        private bool m_replayStuckLogged;
        /// <summary>追帧期间暂存的实时改动（按到达顺序，flush 时按序号升序应用）。</summary>
        private readonly List<HeldChange> m_heldChanges = new List<HeldChange>();
        private long m_heldLiveAdded;
        private long m_heldLiveDropped;
        private long m_lastHoldOverflowLogMs;
        /// <summary>一次收到的实时改动（追帧期间暂存的单元）。</summary>
        private sealed class HeldChange
        {
            public long Sequence;
            public ChunkKey Key;
            public CellEdit Edit;
        }

        // ---- 内存区块（不在兴趣窗口内）的强制核对：窗口外也要有"能被发现不一致"的闭环 ----
        /// <summary>排队等强制核对的块（FIFO；<see cref="m_memoryVerifyQueued"/> 是去重表）。</summary>
        private readonly Queue<ChunkKey> m_memoryVerifyQueue = new Queue<ChunkKey>();
        private readonly HashSet<ChunkKey> m_memoryVerifyQueued = new HashSet<ChunkKey>();
        /// <summary>块还没有内容时的重试时刻（TickCount64；到点前不空转）。</summary>
        private readonly Dictionary<ChunkKey, long> m_memoryVerifyDueMs = new Dictionary<ChunkKey, long>();
        private long m_memoryVerifySent;
        private long m_memoryVerifyDeferred;
        private long m_memoryVerifyDroppedUnloaded;
        private long m_memoryVerifySkipped;
        private int m_memoryVerifyThisTick;
        private bool m_interestKnown;

        // ---- 回传快照的"编码水位"：比本块已应用改动更旧的快照会被丢弃（不能把新值盖回旧值）----
        /// <summary>每块**已应用**的最高改动序号（卸载时清掉；只增不减）。</summary>
        private readonly Dictionary<ChunkKey, long> m_chunkChangeSequence = new Dictionary<ChunkKey, long>();
        private long m_resyncDroppedSuperseded;
        private long m_lastResyncSupersededLogMs;
        // ---- 主机要求"重新核对内存区块"（VerifyChunks）----
        private long m_verifyRequestsReceived;
        private long m_verifyRequestChunks;
        private long m_verifyRequestMalformed;

        // ---- 断线重连追帧（客户端侧）----
        /// <summary>上次应用到的改动序号（CellChange 尾字段推进；世界导入时 = 清单 baseSequence）。</summary>
        private long m_lastAppliedChangeSequence;
        /// <summary>本次 Hello 是否声明了 alreadyHasWorld（用来区分"主机拒绝重连"与"首次加入"）。</summary>
        private bool m_helloClaimedRejoin;
        /// <summary>追帧请求状态（一次握手最多重发 <see cref="TerrainSyncOptions.CatchUpRequestMaxRetries"/> 次）。</summary>
        private long m_catchUpRequestSentMs;
        private int m_catchUpRequestAttempts;
        private bool m_catchUpBeginReceived;
        private bool m_catchUpBarrierReceived;
        private bool m_catchUpCompleted;
        private bool m_catchUpRejectedOnce;
        private long m_catchUpTargetSequence = -1L;
        private long m_catchUpAppliedChanges;
        private long m_catchUpMessages;
        private long m_catchUpBarrierMs;
        private long m_cellChangeBatchesWithoutSequence;
        private long m_cellChangeSequenceMalformed;
        private bool m_oldHostSequenceLogged;
        private long m_catchUpWaitTimeoutLogged;
        private bool m_catchUpNoResponseLogged;
        private bool m_catchUpAppliedSent;
        private bool m_catchUpSendFailureLogged;
        private bool m_catchUpAppliedSendFailureLogged;

        // ---- (重)连后重新排队常驻区块的摘要核对 ----
        // 语义：同一会话重连（追帧完成后）或世界整体换新（整图重下 / Project 换新）时，本端的
        // ChunkLoadVerifier 是空的（新 TerrainSyncClient）或与旧世界不符（同一连接换了 Project），
        // 而"块加载完"事件不会为已经常驻的块再触发一次 —— 不主动重新排队，这些块就永远不会被核对
        // （实机：重连后 4 分钟 常驻 135 / Fresh 0 / 未核对 135 / 累计上报 0）。
        // 排队只把块键放进**现有的**每帧摘要队列（FIFO + 去重），摘要计算与上报仍受
        // ClientDigestComputationsPerTick / ClientReportEntriesPerTick 预算限制（不是一次爆发）。
        /// <summary>待执行的"(重)连后重新排队"（置位 = 还没排；真正的扫描在 Tick 末尾做）。</summary>
        private bool m_requeueResidentsArmed;
        /// <summary>置位时的明确原因（写进结果行）。</summary>
        private string m_requeueReason = string.Empty;
        private long m_residentRequeues;
        private long m_residentRequeuedChunks;
        private int m_lastResidentRequeueCount;
        private long m_residentRequeueSkipped;
        private long m_residentRequeueDeferred;
        private string m_lastResidentRequeueReason = string.Empty;
        private long m_lastResidentRequeueLogMs;
        /// <summary>世界整体替换时清掉的未完成核对工作条数（旧世界的验证状态对新世界无效）。</summary>
        private long m_worldReplaceClearedWork;

        // ---- 现场计数（每秒，只用于诊断/日志，不参与判定）----
        private long m_secondBucketStartedMs;
        private int m_reportsInSecond, m_matchInSecond, m_mismatchInSecond;
        private int m_resyncReceivedInSecond, m_resyncAppliedInSecond;
        private int m_reportsPerSecond, m_reportsPerSecondPeak;
        private int m_matchPerSecond, m_mismatchPerSecond;
        private int m_resyncReceivedPerSecond, m_resyncAppliedPerSecond;
        /// <summary>每块累计不一致次数（去重键 = 区块；卸载时移除；用于定位"同一块反复回传"）。</summary>
        private readonly Dictionary<ChunkKey, int> m_mismatchCounts = new Dictionary<ChunkKey, int>();
        private long m_lastMismatchHotspotLogMs;
        private long m_verifyNotStarted;
        private long m_lastVerifyNotStartedLogMs;
        private long m_verifyNotStartedSinceLog;

        // ---- "同一块反复不一致"的有界重试（回传风暴闸门；见 TerrainSyncOptions.ClientMismatchRetry*）----
        /// <summary>每块**连续**不一致重试预算（一次 Match 清零）。超过上限 → 停止自动重报。</summary>
        private readonly Dictionary<ChunkKey, MismatchRetryState> m_mismatchRetries =
            new Dictionary<ChunkKey, MismatchRetryState>();
        /// <summary>"过期快照被丢弃后，本端该在何时重新上报一次"（指数退避；不再每片当场重报）。</summary>
        private readonly Dictionary<ChunkKey, long> m_discardRefreshDueMs = new Dictionary<ChunkKey, long>();
        /// <summary>重试预算用尽的块：不再自动重报（内容仍按主机回传落地，但不形成第 N+1 次 1:1 回传）。</summary>
        private readonly HashSet<ChunkKey> m_mismatchSuppressed = new HashSet<ChunkKey>();
        private long m_convergedChunks, m_resyncRefreshedAfterDiscard, m_resyncDeferredNotReady;
        private long m_resyncEmptySnapshots, m_mismatchRetrySuppressedVerifications;
        private long m_discardRefreshDeferred, m_discardRefreshSkipped, m_reportHashesRefreshed;
        private long m_mismatchRetryExhaustedCount, m_notReadyRetriesDeferred;
        /// <summary>回传队列头"内容还没定稿"时整体延后到该时刻（TickCount64；保持同块分片顺序）。</summary>
        private long m_resyncDeferredUntilMs;
        private long m_lastResyncEmptyLogMs;

        /// <summary>一块的"不一致重试"状态（纯诊断 + 上限闸门，不参与匹配判定）。</summary>
        private sealed class MismatchRetryState
        {
            public int Attempt;
            public long LastWarnMs;
        }

        /// <summary>
        /// 客户端"当前内存区块集合"的**明确失败**（整集超过协议上限 / 编码失败）：
        /// 不静默、不降级，由模块写成一行 Warning。正常路径不会触发（常驻块数远低于上限）。
        /// </summary>
        public event Action<string> ClientChunkSetFailed;

        /// <summary>
        /// 客户端同步的**明确原因行**（Terrain 层不写引擎日志，由模块写成一行）。
        /// 事件本身已限流：队列连续饱和 ≥ <see cref="TerrainSyncOptions.ClientQueueSaturationLogMs"/> 才抛一次，
        /// 之后每 <see cref="TerrainSyncOptions.ClientQueueLogIntervalMs"/> 至多一次 —— 调用方照原样写一行即可。
        /// </summary>
        public event Action<string> Saturated;

        /// <summary>
        /// 回传载荷解码失败（协议异常）：<b>明确计数 + 限流一行原因</b>，绝不当成"已落地"。
        /// 同一次失败不会让整帧抛异常（旧实现会从 Tick 里抛出去，把整帧的同步工作打断）。
        /// </summary>
        public event Action<string> DecodeFailed;

        /// <summary>
        /// 追帧过程的明确日志（请求 / Begin / Barrier / 完成 / 拒绝 / 异常帧）。事件本身已限流，
        /// 由模块照原样写一行。
        /// </summary>
        public event Action<string> CatchUpTrace;

        /// <summary>
        /// 客户端追帧的**协议异常 / 主机不回应 / 尾字段非法 / Barrier 后仍未到水位**：
        /// 必须写成 Warning 让用户看见（不静默当成成功）。
        /// </summary>
        public event Action<string> CatchUpProblem;

        /// <summary>
        /// 主机拒绝重连（HelloAck 里 RejoinAccepted=false）。参数：原因 + 主机是否会在**这条连接上**
        /// 直接下发整图（本版主机 = true；旧主机不带会话字段 = false，调用方必须断开重连成首次加入）。
        /// </summary>
        public event Action<string, bool> RejoinRejected;

        /// <summary>
        /// 主机拒绝加入（HelloAck.rejected + 原因；旧主机不会发这个字段 = 不会触发）。
        /// 参数 (clientId, 原因)。上层必须**中止本次加入并显示原因**，不要再静默重连（重连只会再被拒一次）。
        /// </summary>
        public event Action<int, string> JoinRejected;

        /// <summary>
        /// 追帧被主机明确拒绝（同会话握手通过后历史被裁剪）：客户端必须作废会话、断开重连成首次加入。
        /// </summary>
        public event Action<string> CatchUpRejected;

        /// <summary>
        /// (重)连后"重新排队全部常驻区块的摘要核对"结果行（内部已限流；一次重连一行）。
        /// 由模块照原样写一行 Info —— 这是"重连后为什么又开始上报 135 块"的唯一证据行。
        /// </summary>
        public event Action<string> ResidentRequeue;

        /// <summary>
        /// 同一块**反复不一致**（累计 ≥ <see cref="TerrainSyncOptions.ClientMismatchRepeatLogThreshold"/> 次）：
        /// 参数带区块键、次数、每秒上报/裁决/回传速率 —— 回传风暴必须能在日志里定位到具体区块。
        /// 事件本身已限流（<see cref="TerrainSyncOptions.ClientMismatchHotspotLogIntervalMs"/>）。
        /// </summary>
        public event Action<string> RepeatedMismatch;

        /// <summary>回传落地后本该重报、但 verifier 明确拒绝（已 Fresh / 正在校验 / 重试用尽）时的一行原因。</summary>
        public event Action<string> VerifyNotStarted;

        /// <summary>
        /// 同一块的连续不一致重试超过 <see cref="TerrainSyncOptions.ClientMismatchRetryCap"/>：停止自动重报，
        /// 写一行**限流** Warning（区块键 + 原因 + 次数）。这是"不再 1:1 回传循环"的显式证据。
        /// </summary>
        public event Action<string> MismatchRetryExhausted;

        /// <summary>一份待落地的回传分片；<see cref="Generation"/> 与块当前"代"不一致 = 排队期间卸载过。</summary>
        private readonly struct ResyncJob
        {
            public ResyncJob(ChunkKey key, byte[] payload, int generation, long watermark)
            {
                Key = key; Payload = payload; Generation = generation; Watermark = watermark;
            }
            public ChunkKey Key { get; }
            public byte[] Payload { get; }
            public int Generation { get; }
            /// <summary>主机编码这一片时的改动水位（-1 = 旧主机没有该尾字段，按到达顺序落地）。</summary>
            public long Watermark { get; }
        }

        /// <summary>一块 NotReady 之后的重报安排（退避到点就重报一次）。</summary>
        private sealed class NotReadyRetry
        {
            /// <summary>下一次允许重报的时刻（TickCount64 口径）。</summary>
            public long DueMs;
            /// <summary>这一块已经重报过几次（退避按它翻倍）。</summary>
            public int Attempt;
        }

        /// <param name="attachToChannel">false = 由主线程泵调用 <see cref="ProcessFrame"/>（引擎接入用）。</param>
        public TerrainSyncClient(ITerrainWorld world, TcpTerrainClient channel,
            TerrainSyncOptions options = null, bool attachToChannel = true,
            MessageChannel messages = null)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_channel = channel ?? throw new ArgumentNullException(nameof(channel));
            m_options = options ?? new TerrainSyncOptions();
            m_verifier.MaxRetries = m_options.MaxRetries;
            m_messages = messages ?? new MessageChannel(new MessageRegistry(),
                (clientId, type, payload) => clientId == 0 && m_channel.TrySend(type, payload),
                "terrain-sync-client");
            RegisterMessages();
            if (attachToChannel) m_channel.FrameReceived += OnFrame;
        }

        /// <summary>主线程泵入口：处理一帧入站消息。</summary>
        public void ProcessFrame(in ReceivedFrame frame) => OnFrame(frame);

        /// <summary>消息层（注册表 + 发送器）。组合根把它交给第三方模块。</summary>
        public MessageChannel Messages => m_messages;

        private const string MessageOwner = "terrain-sync-client";

        private void RegisterMessages()
        {
            MessageRegistry registry = m_messages.Registry;
            registry.UnregisterOwner(MessageOwner);
            registry.Register((int)MessageType.HelloAck, HelloAck.Parser,
                new DelegateMessageHandler<HelloAck>(OnHelloAck), MessageOwner);
            registry.Register((int)MessageType.ChunkVerdict, PT.ChunkVerdict.Parser,
                new DelegateMessageHandler<PT.ChunkVerdict>(OnVerdict), MessageOwner);
            registry.Register((int)MessageType.ChunkResync, PT.ChunkResync.Parser,
                new DelegateMessageHandler<PT.ChunkResync>(OnResync), MessageOwner);
            registry.Register((int)MessageType.CellChange, PT.CellChange.Parser,
                new DelegateMessageHandler<PT.CellChange>(OnCellChange), MessageOwner);
            registry.Register((int)MessageType.CatchUp, PT.CatchUp.Parser,
                new DelegateMessageHandler<PT.CatchUp>(OnCatchUp), MessageOwner);
            registry.Register((int)MessageType.VerifyChunks, PT.VerifyChunks.Parser,
                new DelegateMessageHandler<PT.VerifyChunks>(OnVerifyRequest), MessageOwner);
            registry.Register((int)MessageType.Pong, Pong.Parser,
                new DelegateMessageHandler<Pong>(OnPong), MessageOwner);
        }

        private void OnHelloAck(MessageContext context, HelloAck message) => HandleHelloAck(message);

        private void OnVerdict(MessageContext context, PT.ChunkVerdict message) => HandleVerdict(message);

        private void OnResync(MessageContext context, PT.ChunkResync message) =>
            HandleResync(context, message);

        private void OnCellChange(MessageContext context, PT.CellChange message) =>
            HandleCellChange(message);

        private void OnCatchUp(MessageContext context, PT.CatchUp message) => HandleCatchUp(message);

        private void OnVerifyRequest(MessageContext context, PT.VerifyChunks message) =>
            HandleVerifyRequest(message);

        private void OnPong(MessageContext context, Pong message)
        {
            if (message == null || message.Nonce != m_pingNonce || m_lastPingSentMs <= 0) return;
            long rtt = Environment.TickCount64 - m_lastPingSentMs;
            if (rtt < 0 || rtt > 5000) return;
            RoundTripMs = (int)rtt;
        }

        public int ViewRadius { get; private set; } = 2;
        public ChunkKey Center { get; private set; }
        public bool Handshaken { get; private set; }

        /// <summary>HelloAck 里收到的主机身份（UserId.dat）。未握手 / 旧构建 = 空串。</summary>
        public string HostIdentity { get; private set; } = string.Empty;

        /// <summary>HelloAck 里收到的主机名字。未握手 / 旧构建 = 空串。</summary>
        public string HostName { get; private set; } = string.Empty;

        /// <summary>
        /// HelloAck 里收到的主机资料（名字/性别/皮肤/衣服）。未握手 / 旧构建 = null：
        /// 客户端此时**明确记录"主机外观未知"**，不伪造性别或皮肤。
        /// </summary>
        public PlayerProfileSnapshot HostProfile { get; private set; }

        /// <summary>
        /// HelloAck 里主机给本客户端分配的角色记录资料（name/sex/skin/clothes）。
        /// 主机没有该身份记录（profile_required）时为空。
        /// </summary>
        public PlayerProfileSnapshot AssignedProfile { get; private set; }

        /// <summary>
        /// HelloAck.assigned_skin_asset_present：主机记录里是否已有本机资料对应的自定义皮肤资源。
        /// false/旧主机 = 客户端需要在握手后上传自己的资源。
        /// </summary>
        public bool AssignedSkinAssetPresent { get; private set; }

        /// <summary>
        /// HelloAck 里的本机角色重新进入位置（主机角色记录的最后离线位置 + 最后睡觉复活点）。
        /// false = 旧主机 / 首次创建角色 / 记录里没有有效位置 → 保持引擎出生行为。
        /// </summary>
        public bool HasAssignedPlacement { get; private set; }

        /// <summary>主机角色记录里的最后离线位置（客户端把本地角色放到这里）。</summary>
        public SuMultiplayer.Players.PlayerPlacement AssignedPosition { get; private set; }

        /// <summary>主机角色记录里的最后睡觉复活点（客户端 PlayerData.SpawnPosition）。</summary>
        public SuMultiplayer.Players.PlayerPlacement AssignedSpawnPosition { get; private set; }

        /// <summary>HelloAck.has_assigned_state：角色退出前的飞行/蹲下状态可用。</summary>
        public bool HasAssignedState { get; private set; }

        public bool AssignedIsFlying { get; private set; }

        public bool AssignedIsCrouching { get; private set; }

        /// <summary>
        /// 主机要求先创建角色（旧 SCMP_PROFILE_REQUIRED）：本连接**不会**收到世界，
        /// 调用方必须打开角色创建界面，完成后带 Hello.profile 重新握手。
        /// </summary>
        public bool ProfileRequired { get; private set; }

        /// <summary>ProfileRequired 的明确原因（主机给的 / 旧主机没有字段时为空）。</summary>
        public string ProfileRequiredReason { get; private set; } = string.Empty;

        /// <summary>
        /// 主机在 HelloAck 里给本端的编号（旧 <c>client.ClientID</c>，早就在这条线上，
        /// 只是此前被丢掉）。位置快照批里"本机玩家自己那一条"就是用它认出来的
        /// （旧 HandleGamePlayerPositionMessage 的 <c>remoteClientId == client.ClientID</c> 分支）。
        /// 未握手 = -1；它**只是传输层编号**，不是身份。
        /// </summary>
        public int LocalClientId { get; private set; } = -1;

        /// <summary>本端**上次应用到的权威改动序号**（重连时随 Hello 发回；主机据此从历史里追帧）。</summary>
        public long LastAppliedChangeSequence => m_lastAppliedChangeSequence;

        /// <summary>最近一次 HelloAck 收到的**主机会话标识**（旧主机 = 空串）。</summary>
        public string HostSessionId { get; private set; } = string.Empty;

        /// <summary>收到的 HelloAck 次数（同一连接上重发 Hello 时，测试/上层用它等到**新**确认）。</summary>
        public int Handshakes { get; private set; }

        /// <summary>最近一次 HelloAck 是否**接受**了本端的重连声明（会话一致 + 序号在历史内）。</summary>
        public bool RejoinAccepted { get; private set; }

        /// <summary>HelloAck 带回的接受 / 拒绝依据（写日志用）。</summary>
        public string RejoinReason { get; private set; } = string.Empty;

        /// <summary>HelloAck 带回的主机当前改动水位。</summary>
        public long RejoinHeadSequence { get; private set; }

        /// <summary>追帧是否已开始（收到 Begin）且尚未完成 / 拒绝。</summary>
        public bool CatchUpActive => RejoinAccepted && !m_catchUpCompleted && !m_catchUpRejectedOnce;

        /// <summary>追帧要追到的水位（Begin 带回；-1 = 没有开始）。</summary>
        public long CatchUpTargetSequence => m_catchUpTargetSequence;

        /// <summary>本次追帧重放里实际落地的改动条数。</summary>
        public long CatchUpAppliedChanges => m_catchUpAppliedChanges;

        /// <summary>本次追帧收到的重放消息条数（可审计：一批最多 CatchUpBatchEntries 条）。</summary>
        public long CatchUpMessages => m_catchUpMessages;

        /// <summary>追帧是否已完成（已到水位 + 已回报 Applied）。</summary>
        public bool CatchUpCompleted => m_catchUpCompleted;

        /// <summary>追帧被拒绝的原因（空 = 没被拒绝）。</summary>
        public string CatchUpRejectReason { get; private set; } = string.Empty;

        /// <summary>
        /// 把"上次应用序号"设为指定值。两处调用：
        ///   · 世界导入完成（= 世界清单的 baseSequence，重置）；
        ///   · 同一世界重连（= 模块保存的上次应用序号，恢复）。
        /// 只由持有真实事实的一方调用；这里不猜、不取最大值（新会话的序号可能比旧会话小）。
        /// </summary>
        public void SetAppliedChangeSequence(long sequence)
        {
            m_lastAppliedChangeSequence = Math.Max(0L, sequence);
            m_catchUpBeginReceived = false;
            m_catchUpBarrierReceived = false;
            m_catchUpCompleted = false;
            m_catchUpRejectedOnce = false;
            m_catchUpTargetSequence = -1L;
            m_catchUpAppliedChanges = 0L;
            m_catchUpMessages = 0L;
            m_catchUpBarrierMs = 0L;
            m_catchUpNoResponseLogged = false;
            m_catchUpAppliedSent = false;
            m_catchUpSendFailureLogged = false;
            m_catchUpAppliedSendFailureLogged = false;
            CatchUpRejectReason = string.Empty;
            // 注意：**不**在这里复位投递序号 / 重放状态 —— 世界导入可能发生在同一条连接上，
            // 复位会让随后到达的帧误报"投递缺口"；真正的新连接在 SendHello / 新实例里复位。
        }

        /// <summary>Hello 里上报"我已有角色记录 hash"的条数上限（与主机侧 64 一致）。</summary>
        private const int MaxKnownRecordHashesPerHello = 64;

        /// <summary>Hello：旧字段（kind, name, 1, 1, radius）+ 末尾追加的身份。保留单参重载给既有调用方。</summary>
        public void SendHello(string name) => SendHello(name, string.Empty, false, string.Empty, 0L);

        public void SendHello(string name, string identity) =>
            SendHello(name, identity, false, string.Empty, 0L);

        public void SendHello(string name, string identity, bool alreadyHasWorld) =>
            SendHello(name, identity, alreadyHasWorld, string.Empty, 0L);

        /// <summary>
        /// Hello。参数 <paramref name="alreadyHasWorld"/> = 本端**此刻正在游玩**这个房间的
        /// 已下载联机世界（在游戏里 + Project 已加载 + 当前世界就是下载登记的那个）。
        /// 本版起还要求带上：
        ///   · <paramref name="clientSessionId"/> = 下载世界登记里的主机会话标识（WorldBegin 下发）；
        ///   · <paramref name="lastAppliedChangeSequence"/> = 本端上次应用到的改动序号。
        /// 主机只接受"会话一致 + 序号仍在保留历史内"的重连；否则明确按首次加入处理（整图重下）。
        /// 在 Play 屏加入（世界正在下载 / 刚下载完还没进世界）必须 alreadyHasWorld=false。
        /// 缺字段（旧构建）= false / 空会话 / 0，主机照常下发整图，语义不变。
        /// </summary>
        public void SendHello(string name, string identity, bool alreadyHasWorld,
            string clientSessionId, long lastAppliedChangeSequence) =>
            SendHello(name, identity, alreadyHasWorld, clientSessionId, lastAppliedChangeSequence, null);

        /// <summary>
        /// Hello（带本机资料）。<paramref name="profile"/> = 客户端自己的显示名/性别/皮肤/衣服：
        /// 主机据此建角色记录（首次加入）或用它核对老角色（重连）。旧构建不认这个字段，语义不变。
        /// Source: Mod/ScMultiplayer/Message/GameWorldInfoMessage.cs:PlayerName/HasPlayerProfile/
        ///         PlayerClass/CharacterSkinName
        /// </summary>
        public void SendHello(string name, string identity, bool alreadyHasWorld,
            string clientSessionId, long lastAppliedChangeSequence, PlayerProfileSnapshot profile) =>
            SendHello(name, identity, alreadyHasWorld, clientSessionId, lastAppliedChangeSequence,
                profile, null);

        /// <summary>
        /// Hello（带本机资料 + "我本地已有的角色记录 hash"）。
        /// <paramref name="knownRecords"/> = 客户端从它下载的 world pak 里读到的角色记录 hash 清单
        /// （<see cref="SuMultiplayerPlayersXml.TryReadRecordHashes"/>）：主机据此只重发变化了的角色记录；
        /// 空 / null = 首次加入或旧客户端 → 全量补发，语义与改动前完全一致。
        /// </summary>
        public void SendHello(string name, string identity, bool alreadyHasWorld,
            string clientSessionId, long lastAppliedChangeSequence, PlayerProfileSnapshot profile,
            IReadOnlyList<RecordHashRef> knownRecords)
        {
            var hello = new Hello
            {
                Kind = (uint)ClientKind.Player,
                Name = name ?? string.Empty,
                ProtocolVersion = 1,
                ProtocolRevision = 1,
                Radius = ViewRadius,
                // 本机身份（UserId.dat）：空值也照样写（明确表示"身份不可用"，不是字段缺失）。
                Identity = identity ?? string.Empty,
                AlreadyHasWorld = alreadyHasWorld,
                // 下载世界的会话标识 + 上次应用的改动序号。
                ClientSessionId = clientSessionId ?? string.Empty,
                LastAppliedSequence = Math.Max(0L, lastAppliedChangeSequence),
                // 本机资料（首次加入没有 → 主机回 profile_required；有 → 主机据此建记录）。
                Profile = profile != null
                    ? PlayerProfileCodec.ToMessage(0, identity ?? string.Empty, profile, false,
                        profile.HasName)
                    : null
            };
            // 客户端本地已有的角色记录 hash（来自下载世界里的 SuMultiplayerPlayers.xml）：
            // 主机据此只重发变化了的角色记录。上限与主机侧一致；0 / 空键不冒充"已知"。
            if (knownRecords != null)
            {
                int limit = Math.Min(knownRecords.Count, MaxKnownRecordHashesPerHello);
                for (int i = 0; i < limit; i++)
                {
                    RecordHashRef item = knownRecords[i];
                    if (string.IsNullOrEmpty(item.RecordKey) || item.Hash == 0UL) continue;
                    hello.KnownRecords.Add(new KnownRecordHash
                    {
                        RecordKey = item.RecordKey,
                        Hash = item.Hash
                    });
                }
            }
            // 新一次握手：上一轮的"要求建角色 / 主机资料 / 分配资料"全部作废，
            // 只认这次 HelloAck 带回的事实（旧值留着会让客户端以为还缺角色）。
            ProfileRequired = false;
            ProfileRequiredReason = string.Empty;
            HostProfile = null;
            AssignedProfile = null;
            HasAssignedPlacement = false;
            AssignedPosition = default(SuMultiplayer.Players.PlayerPlacement);
            AssignedSpawnPosition = default(SuMultiplayer.Players.PlayerPlacement);
            SuMultiplayer.Engine.ClientSpawnPlacement.Clear();
            m_helloClaimedRejoin = alreadyHasWorld;
            m_catchUpRequestAttempts = 0;
            m_catchUpRequestSentMs = 0L;
            m_catchUpBeginReceived = false;
            m_catchUpBarrierReceived = false;
            m_catchUpCompleted = false;
            m_catchUpRejectedOnce = false;
            m_catchUpTargetSequence = -1L;
            m_catchUpAppliedChanges = 0L;
            m_catchUpMessages = 0L;
            m_catchUpBarrierMs = 0L;
            m_catchUpNoResponseLogged = false;
            m_catchUpAppliedSent = false;
            m_catchUpSendFailureLogged = false;
            m_catchUpAppliedSendFailureLogged = false;
            CatchUpRejectReason = string.Empty;
            // 每次 Hello = 新的一次投递流（主机按连接重新编号）。
            m_lastDeliveryIndex = 0L;
            m_deliveryIndexSeen = false;
            m_replayActive = false;
            m_replayTargetSequence = -1L;
            m_replayStartedMs = 0L;
            m_replayStuckLogged = false;
            m_heldChanges.Clear();
            m_channel.TrySend(MessageType.Hello, hello.ToByteArray());
        }

        public void SendInterest(ChunkKey center, int radius)
        {
            Center = center;
            ViewRadius = Math.Max(1, Math.Min(m_options.MaxRadius, radius));
            m_interestKnown = true;
            var interest = new InterestUpdate
            {
                CenterX = center.X,
                CenterZ = center.Z,
                Radius = ViewRadius
            };
            m_channel.TrySend(MessageType.InterestUpdate, interest.ToByteArray());
        }

        /// <summary>
        /// 块加载完 —— **唯一的"该算摘要"信号**。
        /// 本轮起**不在这里算**（见类注释的每帧预算）：只登记进"待算摘要"队列，摘要与上报由
        /// <see cref="Tick"/> 按 <see cref="TerrainSyncOptions.ClientDigestComputationsPerTick"/> 逐帧消化。
        /// 返回 false = 这块不需要上报（已 Fresh / 正在校验 / 重报次数用尽 —— 与旧实现 TryBeginVerify 的
        /// 拒绝条件同一口径，且不会白算一次 65536 格摘要）；
        /// 返回 true = 已入队（摘要算完后进入上报队列，**最终一定会发出去**；只有块在算之前卸载才作废并计数）。
        /// </summary>
        public bool OnChunkLoaded(ChunkKey key)
        {
            // 内存集合**不在这里**记账：它只认引擎的"分配"事件（见 OnMemoryChunkAllocated）。
            // 内容可用 ⇒ 必然早已分配（同一次扫描里先触发分配事件），所以这里只负责摘要 / 上报。
            if (!m_verifier.WouldBeginVerify(key)) return false;
            if (!m_digestQueued.Add(key)) return true;      // 同一块重复触发：只保留一条（幂等）
            m_digestQueue.Enqueue(key);
            return true;
        }

        /// <summary>
        /// 卸载：丢掉摘要（重载时会重新算一次），并让**排队中**的工作明确作废 + 计数：
        ///   · 还没算的摘要（那块内容已经不在了，没有可核对的东西 —— 不是静默丢弃，有计数器）；
        ///   · 还没落地的回传分片（给这块换一"代"，落地时丢弃，绝不把旧内容写到重载后的新块上）；
        ///   · 还没重算的改动标记（同上）。
        /// </summary>
        public void OnChunkUnloaded(ChunkKey key)
        {
            // 内存集合**不在这里**记账：只有引擎真的释放了块（OnMemoryChunkFreed）才移除 ——
            // 内容掉回未定稿（但还占着内存）时，实时地图的方框必须继续保留。
            // 摘要还没算块就没了：那条工作作废（重载会重新入队一条新的）
            if (m_digestQueued.Remove(key)) m_digestsDroppedUnloaded++;
            if (m_dirtyQueued.Remove(key)) m_hashRefreshesDroppedUnloaded++;
            if (m_queuedParts.ContainsKey(key))
            {
                m_chunkGeneration.TryGetValue(key, out int generation);
                m_chunkGeneration[key] = generation + 1;    // 旧分片落地时按"代"丢弃并计数
            }
            m_verifier.OnChunkUnloaded(key);
            m_localHashes.Remove(key);
            m_reportSentMs.Remove(key);
            m_pendingParts.Remove(key);
            m_chunkChangeSequence.Remove(key);
            if (m_memoryVerifyQueued.Remove(key)) m_memoryVerifyDueMs.Remove(key);
            m_notReadyRetries.Remove(key);      // 块没了：重报安排一并作废（重载时会重新上报一次）
            m_mismatchCounts.Remove(key);       // 不一致次数按块生命周期清掉（重载是另一份内容）
            ResetMismatchRetry(key);            // 重载是另一份内容：重试预算 / 抑制 / 丢弃后重核安排一并作废
        }

        // ------------------------------------------------------------ (重)连后重新排队常驻区块

        /// <summary>
        /// 标记"下一次 Tick 末尾把全部常驻（内容可用）区块重新排进摘要核对队列"。
        /// **只在明确的 (重)连 / 世界换新时刻调用**（HelloAck 接受重连、Project 换新），
        /// 每次调用只产生**一次**排队（<see cref="TryRequeueResidentChunks"/> 执行后清标记），
        /// 因此不会自循环、不会每帧重排。
        ///
        /// 为什么必须做：模块每建一次连接都会 new 一个 <see cref="TerrainSyncClient"/>，摘要状态机
        /// （<see cref="ChunkLoadVerifier"/>）与本地摘要表随之清空；而"块加载完"事件不会为**已经常驻**
        /// 的块再触发一次。不主动重新排队，重连后这些块就永远停在"未核对"，直到玩家走到新块才恢复核对
        /// （实机：重连 + 追帧完成后 4 分钟 常驻 135 / Fresh 0 / 未核对 135 / 累计上报 0）。
        ///
        /// 这里只置标记，**不**扫描、**不**算摘要：真正的入队发生在 Tick 末尾，摘要与上报仍由每帧预算
        /// （<see cref="TerrainSyncOptions.ClientDigestComputationsPerTick"/> /
        /// <see cref="TerrainSyncOptions.ClientReportEntriesPerTick"/>）逐帧消化 —— 135 块不会在一帧里爆发。
        /// </summary>
        public void ArmResidentRequeue(string reason)
        {
            m_requeueResidentsArmed = true;
            m_requeueReason = reason ?? string.Empty;
        }

        /// <summary>
        /// 世界整体被替换（整图重下完成后的 Project 换新 / 同一连接上重新加载世界）：
        /// 旧世界的摘要状态、已应用改动序号、排队中的核对工作**对新世界全部无效**，必须显式清掉
        /// （计数，不静默丢弃），然后重新排队全部常驻区块。
        ///
        /// 为什么不能只靠 OnChunkUnloaded：引擎换 Project 后，同一个区块键可能立刻又"已加载"，
        /// 而 <see cref="ChunkLoadVerifier"/> 里旧世界的 Fresh 状态会让重新核对直接跳过 —— 那等于
        /// 拿旧世界的结论给新世界背书。这里先清状态，再按新世界的内容核对。
        /// </summary>
        public void OnClientWorldReplaced(string reason)
        {
            long cleared = m_digestQueue.Count + m_dirtyQueue.Count + m_reportQueue.Count +
                m_resyncQueue.Count + m_notReadyRetries.Count + m_memoryVerifyQueue.Count;
            m_digestQueue.Clear();
            m_digestQueued.Clear();
            m_dirtyQueue.Clear();
            m_dirtyQueued.Clear();
            m_reportQueue.Clear();
            m_reportSentMs.Clear();
            m_resyncQueue.Clear();
            m_queuedParts.Clear();
            m_chunkGeneration.Clear();
            m_pendingParts.Clear();
            m_notReadyRetries.Clear();
            m_memoryVerifyQueue.Clear();
            m_memoryVerifyQueued.Clear();
            m_memoryVerifyDueMs.Clear();
            m_localHashes.Clear();
            m_chunkChangeSequence.Clear();
            m_mismatchCounts.Clear();
            m_mismatchRetries.Clear();
            m_discardRefreshDueMs.Clear();
            m_mismatchSuppressed.Clear();
            m_resyncDeferredUntilMs = 0L;
            m_verifier.Clear();
            m_worldReplaceClearedWork += cleared;
            // 旧世界的结论全部作废；下一次 Tick 末尾按新世界的内容重新排队。
            ArmResidentRequeue(reason ?? "世界整体替换");
        }

        /// <summary>
        /// Tick 末尾：(重)连后把**全部常驻（内容可用）区块**排进现有的每帧摘要队列（FIFO + 去重）。
        ///   · 同一会话重连：等追帧完成（或明确被拒绝 / 请求重试用尽）之后再排 —— 否则核对的是断线前的
        ///     旧内容，会白报一轮并触发无谓回传；
        ///   · 首次加入 / 整图重下：等世界激活（Project 换新）后由 <see cref="OnClientWorldReplaced"/> 置位，
        ///     此时常驻集合已经是新世界的内容；
        ///   · 只排**需要核对**的块（<see cref="ChunkLoadVerifier.WouldBeginVerify"/>：已 Fresh / 正在校验 /
        ///     重试用尽的块不重复排）；内容不可用的块跳过并计数；
        ///   · 一次置位只执行一次排队（执行后清标记），所以不会自循环。
        /// 结果写一行（内部限流）并累计计数：这就是"重连后重新排队 N 块"的证据。
        /// </summary>
        private void TryRequeueResidentChunks()
        {
            if (!m_requeueResidentsArmed) return;
            // 同一会话重连：追帧还没完成 → 推迟（计数，下一帧再看）。明确被拒绝 / 请求重试用尽时不再等：
            // 那些情况会有自己的明确原因行，而常驻块的核对仍然必须做（否则又回到"永远未核对"）。
            long settleNowMs = Environment.TickCount64;
            // Barrier 已到但 30s 仍未到水位：UpdateCatchUpOnTick 已经写过明确的"不假装完成"原因行，
            // 这里也不再无限等 —— 常驻块的核对必须做（内容不一致由主机回传纠正），否则又回到"永远未核对"。
            bool barrierTimedOut = m_catchUpBarrierReceived && m_catchUpBarrierMs != 0 &&
                settleNowMs - m_catchUpBarrierMs > m_options.CatchUpAckTimeoutMs;
            bool catchUpSettled = m_catchUpCompleted || m_catchUpRejectedOnce || barrierTimedOut ||
                (!m_catchUpBeginReceived && m_catchUpRequestAttempts >= m_options.CatchUpRequestMaxRetries);
            if (RejoinAccepted && !catchUpSettled)
            {
                m_residentRequeueDeferred++;
                return;
            }
            m_requeueResidentsArmed = false;
            int queued = 0;
            int skipped = 0;
            foreach (ChunkKey key in m_world.LoadedChunks)
            {
                if (!m_world.HasContents(key))
                {
                    skipped++;              // 内容还没定稿：不能算摘要（算了就是伪造），跳过并计数
                    continue;
                }
                if (!m_verifier.WouldBeginVerify(key))
                {
                    skipped++;              // 已 Fresh / 正在校验 / 重报次数用尽
                    continue;
                }
                if (!m_digestQueued.Add(key))
                {
                    skipped++;              // 已经在队列里（例如同一帧的块加载事件）
                    continue;
                }
                m_digestQueue.Enqueue(key);
                queued++;
            }
            m_residentRequeues++;
            m_residentRequeuedChunks += queued;
            m_lastResidentRequeueCount = queued;
            m_residentRequeueSkipped = skipped;
            m_lastResidentRequeueReason = m_requeueReason;
            long nowMs = Environment.TickCount64;
            if (m_lastResidentRequeueLogMs != 0 &&
                nowMs - m_lastResidentRequeueLogMs < m_options.ResidentRequeueLogIntervalMs) return;
            m_lastResidentRequeueLogMs = nowMs;
            int perTick = Math.Max(1, m_options.ClientDigestComputationsPerTick);
            ResidentRequeue?.Invoke("(重)连后重新排队常驻区块的摘要核对：" + queued +
                " 块进入每帧预算队列（每帧 " + perTick + " 块 → 约 " +
                ((queued + perTick - 1) / perTick) + " 帧排完，不在一帧里爆发；FIFO、不丢）" +
                " · 跳过 " + skipped + " 块（已核对/在途/重试用尽/内容不可用/已在队列）" +
                " · 累计 " + m_residentRequeues + " 次 / " + m_residentRequeuedChunks + " 块" +
                " · 原因：" + (string.IsNullOrEmpty(m_requeueReason) ? "(未给)" : m_requeueReason));
        }

        // ------------------------------------------------------------ 现场计数（每秒）

        /// <summary>
        /// 每秒滚动一次现场计数（上报 / Match / Mismatch / 回传收到 / 回传落地）。
        /// 只用于诊断与限流日志：不参与任何判定，也不影响预算。
        /// </summary>
        private void UpdatePerSecondCounters(long nowMs)
        {
            if (m_secondBucketStartedMs == 0L)
            {
                m_secondBucketStartedMs = nowMs;
                return;
            }
            long elapsed = nowMs - m_secondBucketStartedMs;
            if (elapsed < 1000L) return;
            double scale = 1000.0 / elapsed;
            m_reportsPerSecond = (int)Math.Round(m_reportsInSecond * scale);
            m_matchPerSecond = (int)Math.Round(m_matchInSecond * scale);
            m_mismatchPerSecond = (int)Math.Round(m_mismatchInSecond * scale);
            m_resyncReceivedPerSecond = (int)Math.Round(m_resyncReceivedInSecond * scale);
            m_resyncAppliedPerSecond = (int)Math.Round(m_resyncAppliedInSecond * scale);
            if (m_reportsPerSecond > m_reportsPerSecondPeak) m_reportsPerSecondPeak = m_reportsPerSecond;
            m_reportsInSecond = 0;
            m_matchInSecond = 0;
            m_mismatchInSecond = 0;
            m_resyncReceivedInSecond = 0;
            m_resyncAppliedInSecond = 0;
            m_secondBucketStartedMs = nowMs;
        }

        /// <summary>
        /// 记一次"不一致"并检查是否同一块反复不一致（回传风暴的特征）：达到阈值后限流写一行，
        /// 带上区块键、次数、以及每秒上报/裁决/回传速率 —— 让"回传为什么爆炸"在日志里可定位。
        /// </summary>
        private void RecordMismatch(ChunkKey key)
        {
            m_mismatchCounts.TryGetValue(key, out int count);
            count++;
            m_mismatchCounts[key] = count;
            // 连续重试预算：超过上限后进入"抑制自动重报"（内容仍按主机回传落地，但不再 1:1 请求新快照）。
            // 一次 Match 会清零（见 HandleVerdict），所以这里数的是**连续**失败，不是生命周期总数。
            NoteMismatchAttempt(key, "主机裁决不一致");
            if (count < m_options.ClientMismatchRepeatLogThreshold) return;
            long nowMs = Environment.TickCount64;
            if (m_lastMismatchHotspotLogMs != 0 &&
                nowMs - m_lastMismatchHotspotLogMs < m_options.ClientMismatchHotspotLogIntervalMs) return;
            m_lastMismatchHotspotLogMs = nowMs;
            RepeatedMismatch?.Invoke("同一区块反复不一致（回传风暴特征）：" + key + " 已 " + count +
                " 次 · 当前不一致区块 " + m_mismatchCounts.Count +
                " 个 · 每秒上报 " + m_reportsPerSecond + " 条 / Match " + m_matchPerSecond +
                " / Mismatch " + m_mismatchPerSecond +
                " · 每秒回传收到 " + m_resyncReceivedPerSecond + " 片 / 落地 " + m_resyncAppliedPerSecond +
                " 片（累计上报 " + m_reportEntriesSent + " 条 · 累计回传 " + m_resyncApplied +
                " 片 · 累计不一致 " + m_mismatch + " 次）→ 若持续增长，说明回传内容仍在被更新的改动覆盖，" +
                "用日志里的\"快照编码水位 / 投递序号\"定位（不静默、不假装一致）");
        }

        /// <summary>
        /// 回传落地后本该重报、但 verifier 明确拒绝（已 Fresh / 正在校验 / 重试用尽）：
        /// 计数 + 限流一行原因（绝不静默跳过 —— 否则"这块为什么不核对了"没有任何证据）。
        /// </summary>
        private void NoteVerifyNotStarted(ChunkKey key, string where)
        {
            m_verifyNotStarted++;
            m_verifyNotStartedSinceLog++;
            long nowMs = Environment.TickCount64;
            if (m_lastVerifyNotStartedLogMs != 0 &&
                nowMs - m_lastVerifyNotStartedLogMs < m_options.ClientQueueLogIntervalMs) return;
            long since = m_verifyNotStartedSinceLog;
            m_verifyNotStartedSinceLog = 0;
            m_lastVerifyNotStartedLogMs = nowMs;
            VerifyNotStarted?.Invoke("没有发起摘要核对（" + where + "，区块 " + key + "）：状态=" +
                m_verifier.StateOf(key) + "（Fresh / Verifying / 重报次数用尽 → 按既有规则不重复上报）" +
                " · 最近这段共 " + since + " 次，累计 " + m_verifyNotStarted + " 次");
        }

        // ------------------------------------------------------------ 内存区块集合（v8 实时地图）

        /// <summary>
        /// 块被引擎**分配**（进入内存）—— 内存区块集合的**唯一** add 入口。
        /// 与"内容可用"无关：已分配但内容还没定稿（典型：外圈海洋 / 水面）也要进集合，
        /// 实时地图才会给出对应的方框。握手前只记账不发：握手后的整集会覆盖它。
        /// </summary>
        public void OnMemoryChunkAllocated(ChunkKey key)
        {
            if (m_memoryChunks.Add(key) && Handshaken) QueueClientChunkDelta(key, loaded: true);
        }

        /// <summary>
        /// 块被引擎**释放**（离开内存）—— 内存区块集合的**唯一** remove 入口。
        /// 只有真的释放才发 loaded=0 的增量（内容掉回未定稿但还占内存时，方框要保留）。
        /// </summary>
        public void OnMemoryChunkFreed(ChunkKey key)
        {
            if (m_memoryChunks.Remove(key) && Handshaken) QueueClientChunkDelta(key, loaded: false);
            // 已经不在内存里：强制核对 / 已应用序号都不再需要（重载会重新上报、重新核对）。
            if (m_memoryVerifyQueued.Remove(key)) m_memoryVerifyDueMs.Remove(key);
            m_chunkChangeSequence.Remove(key);
        }

        /// <summary>
        /// 记一条待发增量（**按键合并**：同一块重复分配/释放只留最后一次结果）。
        /// 只在握手后调用：握手前分配的块会被 <see cref="QueueClientChunkFullSet"/> 的整集覆盖。
        /// </summary>
        private void QueueClientChunkDelta(ChunkKey key, bool loaded)
        {
            m_clientChunkDeltaPending[key] = loaded;
        }

        /// <summary>
        /// 握手完成（含**重连**）→ 把**当前真正占着引擎内存**的区块集合整理成整集分片发给主机。
        /// 来源 = <see cref="ITerrainWorld.MemoryChunks"/>（桥的**已分配**集合，与分配/释放事件同一份
        /// 事实，不另扫引擎）。**不是**常驻集合：内容还没定稿的海洋 / 水面块也必须进来
        /// （用户口径："方框应该是整个客户端的内存中所有的 chunk"）。
        /// 空集合也会发一条 count=0 的整集：主机据此把旧集合**明确清空**，
        /// 而不是继续画上一轮的方块（重连时世界还没加载完就是这种情形）。
        /// 整集分片先攒着；这期间发生的分配/释放进增量队列，等整集发完再按序补发（见
        /// <see cref="SendQueuedClientChunks"/>），所以不会出现"整集覆盖掉后到的增量"或反之。
        /// </summary>
        private void QueueClientChunkFullSet()
        {
            m_clientChunkFullParts.Clear();
            m_clientChunkFullPartEntries.Clear();
            m_clientChunkFullPartIndex = 0;
            m_clientChunkDeltaPending.Clear();
            m_memoryChunks.Clear();
            var keys = new List<ChunkKey>();
            foreach (ChunkKey key in m_world.MemoryChunks)
            {
                if (!m_memoryChunks.Add(key)) continue;
                keys.Add(key);
            }
            keys.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
            int partCount = Math.Max(1,
                (keys.Count + ClientChunkSetCodec.MaxEntriesPerMessage - 1) /
                ClientChunkSetCodec.MaxEntriesPerMessage);
            if (partCount > ClientChunkSetCodec.MaxFullSetParts)
            {
                // 明确失败：绝不截断后假装"整集完整"（否则主机会把一半的块当成已卸载删掉）。
                ClientChunkSetFailed?.Invoke("本端内存区块（引擎已分配）" + keys.Count + " 块 = " + partCount +
                    " 个整集分片，超过协议上限 " + ClientChunkSetCodec.MaxFullSetParts +
                    " 片（" + ClientChunkSetCodec.MaxFullSetParts * ClientChunkSetCodec.MaxEntriesPerMessage +
                    " 块）→ **本次不发整集**（主机保留上一份内存区块集合），请缩小可见范围后重连");
                return;
            }
            int fullSetId = ++m_clientChunkFullSetId;
            try
            {
                for (int part = 0; part < partCount; part++)
                {
                    int from = part * ClientChunkSetCodec.MaxEntriesPerMessage;
                    int take = Math.Min(ClientChunkSetCodec.MaxEntriesPerMessage, keys.Count - from);
                    m_clientChunkEntryBuffer.Clear();
                    for (int i = 0; i < take; i++)
                    {
                        ChunkKey key = keys[from + i];
                        m_clientChunkEntryBuffer.Add(new ClientChunkEntry(key.X, key.Z, true));
                    }
                    m_clientChunkFullParts.Add(ClientChunkSetCodec.EncodeFullSetPart(fullSetId, part,
                        partCount, m_clientChunkEntryBuffer));
                    m_clientChunkFullPartEntries.Add(take);
                }
            }
            catch (Exception e)
            {
                m_clientChunkFullParts.Clear();
                m_clientChunkFullPartEntries.Clear();
                m_clientChunkFullPartIndex = 0;
                ClientChunkSetFailed?.Invoke("本端内存区块集合（引擎已分配）编码失败（本次不发整集，主机保留上一份；" +
                    "绝不发截断数据）：" + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 发送"内存区块集合（引擎已分配）"（≤ <see cref="TerrainSyncOptions.ClientChunkMessagesPerTick"/> 条/帧）：
        ///   ① 整集分片先发完（一次重连通常 1~3 条，一帧内发完）；
        ///   ② 整集发完后才发增量 —— 顺序不能反：整集是"那一刻的快照"，之后发生的加载/卸载必须落在它后面；
        ///   ③ 发送失败（显式背压 / 连接不可用）→ 条目**留在队列里**，下一帧再发（计数，不静默丢）。
        /// 返回本帧真正发出去的消息条数。
        /// </summary>
        private int SendQueuedClientChunks()
        {
            int budget = Math.Max(1, m_options.ClientChunkMessagesPerTick);
            int sentThisTick = 0;
            while (budget > 0 && m_clientChunkFullPartIndex < m_clientChunkFullParts.Count)
            {
                byte[] payload = m_clientChunkFullParts[m_clientChunkFullPartIndex];
                if (!m_channel.TrySend(MessageType.ClientChunks, payload))
                {
                    m_clientChunkSendDeferrals++;
                    return sentThisTick;
                }
                m_clientChunkFullPartIndex++;
                budget--;
                sentThisTick++;
                m_clientChunkMessagesSent++;
                // 该分片里的条目数在生成分片时就算好了（不再回头解析载荷布局）。
                m_clientChunkEntriesSent += m_clientChunkFullPartEntries[m_clientChunkFullPartIndex - 1];
            }
            if (m_clientChunkFullPartIndex >= m_clientChunkFullParts.Count && m_clientChunkFullParts.Count > 0)
            {
                // 整集全部分片都发出去了（主机收齐后才整体替换；TCP 有序，不担心乱序）。
                m_clientChunkFullSetsSent++;
                m_clientChunkFullParts.Clear();
                m_clientChunkFullPartEntries.Clear();
                m_clientChunkFullPartIndex = 0;
            }
            while (budget > 0 && m_clientChunkDeltaPending.Count > 0)
            {
                m_clientChunkEntryBuffer.Clear();
                foreach (KeyValuePair<ChunkKey, bool> pair in m_clientChunkDeltaPending)
                {
                    m_clientChunkEntryBuffer.Add(new ClientChunkEntry(pair.Key.X, pair.Key.Z, pair.Value));
                    if (m_clientChunkEntryBuffer.Count >= ClientChunkSetCodec.MaxEntriesPerMessage) break;
                }
                byte[] payload = ClientChunkSetCodec.EncodeDelta(m_clientChunkEntryBuffer);
                if (!m_channel.TrySend(MessageType.ClientChunks, payload))
                {
                    m_clientChunkSendDeferrals++;
                    return sentThisTick;
                }
                for (int i = 0; i < m_clientChunkEntryBuffer.Count; i++)
                {
                    ClientChunkEntry entry = m_clientChunkEntryBuffer[i];
                    m_clientChunkDeltaPending.Remove(new ChunkKey(entry.X, entry.Z));
                }
                budget--;
                sentThisTick++;
                m_clientChunkMessagesSent++;
                m_clientChunkDeltaMessagesSent++;
                m_clientChunkEntriesSent += m_clientChunkEntryBuffer.Count;
            }
            return sentThisTick;
        }

        private uint ComputeHash(ChunkKey key)
        {
            if (!m_world.HasContents(key)) return 0u;
            var digest = new ChunkDigest();
            digest.Compute(m_world.GetCellSource(key));
            return digest.HasValue ? digest.Value : 0u;
        }

        /// <summary>
        /// 每渲染帧一次（<c>TerrainSyncModule.Tick</c> → 每帧）。顺序与理由：
        ///   ① 回传分片落地（≤ ClientResyncPartsPerTick）—— 先落地，随后算的摘要才包含落地后的内容；
        ///   ② 待算摘要（≤ ClientDigestComputationsPerTick）+ 改动后的摘要刷新（≤ ClientHashRefreshesPerTick）；
        ///   ③ NotReady 重报到点入队（退避节奏与旧实现完全一致，只是"入队"发生在这一步）；
        ///   ④ 发送 ≤ ClientReportEntriesPerTick 条（消息批上限仍是 DigestBatch；发送失败**不丢条目**）；
        ///   ⑤ 发送 ≤ ClientChunkMessagesPerTick 条"内存区块集合"（= 引擎已分配；整集分片优先，随后增量；
        ///      发送失败不丢，下一帧继续）；
        ///   ⑥ 断线重连追帧：接受重连后请求 / Barrier 到水位后回报 Applied（重发有上限，不无限循环）；
        ///   ⑦ 队列持续饱和的限流原因行。
        /// 返回本帧**真正发出去**的上报条数（旧实现返回的也是条数，调用方语义不变）。
        /// </summary>
        private const int PingIntervalMs = 500;
        private long m_lastPingSentMs;
        private ulong m_pingNonce;
        /// <summary>最近一次 Ping/Pong 的 RTT；-1 = 还没有有效样本（旧主机不会回 Pong）。</summary>
        public int RoundTripMs { get; private set; } = -1;

        private void UpdatePing(long nowMs)
        {
            if (m_channel == null || !m_channel.IsAlive) return;
            if (m_lastPingSentMs > 0 && nowMs - m_lastPingSentMs < PingIntervalMs) return;
            m_pingNonce++;
            var ping = new Ping { Nonce = m_pingNonce };
            if (!m_channel.TrySend(MessageType.Ping, ping.ToByteArray())) return;
            m_lastPingSentMs = nowMs;
        }

        public int Tick()
        {
            UpdatePing(Environment.TickCount64);
            m_digestsThisTick = 0;
            m_reportsThisTick = 0;
            m_resyncAppliedThisTick = 0;
            m_clientChunkMessagesThisTick = 0;
            ApplyQueuedResyncParts();
            ComputeQueuedDigests();
            RefreshDirtyHashes();
            // 过期快照被丢弃后的重新核对：**退避到点**才发，不在丢弃的同一拍当场重报（那正是 1:1 循环）。
            FlushDiscardRefreshes();
            ProcessMemoryVerifications();
            FlushDueNotReadyRetries();
            UpdateCatchUpOnTick(Environment.TickCount64);
            SendQueuedReports();
            m_clientChunkMessagesThisTick = SendQueuedClientChunks();
            // (重)连后重新排队常驻区块：放在本帧的发送之后，队列从下一帧开始按预算消化
            // （本帧已经发出去的上报不受影响；135 块也不会挤进同一帧）。
            TryRequeueResidentChunks();
            UpdatePerSecondCounters(Environment.TickCount64);
            CheckQueueSaturation();
            return m_reportsThisTick;
        }

        /// <summary>
        /// 落地排队中的回传分片（≤ <see cref="TerrainSyncOptions.ClientResyncPartsPerTick"/>）。
        /// 丢弃 / 延后路径全部有明确计数：
        ///   · 排队期间这块卸载过（代不一致）→ <c>ResyncDroppedUnloaded</c>（旧内容绝不落到新块上）；
        ///   · 块已不在内存 → 同上计数；**在内存但内容还没定稿** → 整体延后（保持同块分片顺序，不丢、不算 mismatch）；
        ///   · 快照编码水位比本块已应用改动旧 → <c>ResyncDroppedSuperseded</c> + **有界退避**后重新核对；
        ///   · 零格快照（主机裁决后编码前丢了内容）→ <c>ResyncEmptySnapshots</c>，不落地、不重报；
        ///   · 载荷解码失败 → <c>ResyncDecodeFailures</c> + 限流一行原因。
        /// 末片落地后重算本地摘要并重报；若该块连续重试已超上限则**不再自动重报**（见 MismatchRetryCap）。
        /// </summary>
        private void ApplyQueuedResyncParts()
        {
            long nowMs = Environment.TickCount64;
            if (nowMs < m_resyncDeferredUntilMs) return;
            int budget = Math.Max(1, m_options.ClientResyncPartsPerTick);
            while (budget > 0 && m_resyncQueue.Count > 0)
            {
                ResyncJob job = m_resyncQueue.Peek();
                if (m_chunkGeneration.TryGetValue(job.Key, out int generation) && generation != job.Generation)
                {
                    m_resyncQueue.Dequeue();
                    ReleaseQueuedPart(job.Key);
                    budget--;
                    m_resyncDroppedUnloaded++;
                    continue;
                }
                if (!m_world.HasContents(job.Key))
                {
                    if (m_memoryChunks.Contains(job.Key))
                    {
                        // 本端内存里有这块、内容还没定稿：**不落地、不作废、不算 mismatch**，
                        // 整条队列延后（保持同块分片的先后顺序）；内容定稿后从同一片继续。
                        m_resyncDeferredNotReady++;
                        m_resyncDeferredUntilMs = nowMs + Math.Max(1, m_options.ClientResyncNotReadyRetryMs);
                        break;
                    }
                    m_resyncQueue.Dequeue();
                    ReleaseQueuedPart(job.Key);
                    budget--;
                    m_resyncDroppedUnloaded++;
                    continue;
                }
                m_resyncQueue.Dequeue();
                ReleaseQueuedPart(job.Key);
                budget--;
                if (job.Watermark >= 0 &&
                    m_chunkChangeSequence.TryGetValue(job.Key, out long appliedSequence) &&
                    appliedSequence > job.Watermark)
                {
                    // 这一片是按**更早**的内容编码的，而本块之后已经应用了更新的改动 →
                    // 落地会把新值盖回旧值。丢弃 + 计数；**按退避**重新核对（不是每片当场重报）。
                    m_resyncDroppedSuperseded++;
                    NoteResyncSuperseded(job.Key, appliedSequence, job.Watermark);
                    m_verifier.ForceUnverified(job.Key);
                    ScheduleDiscardRefresh(job.Key, "回传快照过期（编码水位 " + job.Watermark +
                        " < 本端已应用 " + appliedSequence + "）");
                    continue;
                }
                ChunkPayloadCodec.Part part;
                try
                {
                    part = ChunkPayloadCodec.Decode(job.Payload);
                }
                catch (Exception e)
                {
                    m_resyncDecodeFailures++;
                    ReportDecodeFailure(job.Key, e);
                    continue;
                }
                if (part.Count == 1 && part.Edits.Count == 0)
                {
                    // 主机裁决时还有内容、编码时已经没了 → 零格快照。绝不当成"已覆盖"：
                    // 不落地、不计数 ResyncApplied、不重报；按退避重新核对，让主机内容可用后重发。
                    m_resyncEmptySnapshots++;
                    m_verifier.ForceUnverified(job.Key);
                    ScheduleDiscardRefresh(job.Key, "主机回传零格快照（编码时主机已无该块内容）");
                    NoteResyncEmptySnapshot(job.Key);
                    continue;
                }
                if (part.Edits.Count > 0) m_world.ApplyCells(part.Key, part.Edits);
                m_resyncApplied++;
                m_resyncAppliedThisTick++;
                m_resyncAppliedInSecond++;
                int remaining = part.Count - part.Index - 1;
                if (remaining > 0)
                {
                    m_pendingParts[part.Key] = remaining;   // 等其余分片（下一帧继续）
                    continue;
                }
                m_pendingParts.Remove(part.Key);
                m_notReadyRetries.Remove(part.Key);         // 内容已覆盖：作废重报安排
                // 快照内容 = 编码水位那一刻的权威内容：本块"已应用序号"至少推进到它，
                // 之后到达的更新改动再往上推（更旧的快照会被上面那条规则丢弃）。
                if (job.Watermark > 0)
                {
                    long applied = m_chunkChangeSequence.TryGetValue(part.Key, out long current) ? current : 0L;
                    if (job.Watermark > applied) m_chunkChangeSequence[part.Key] = job.Watermark;
                }
                if (m_mismatchSuppressed.Contains(part.Key))
                {
                    // 连续重试已超上限：内容照常落地（主机权威），但**不再自动上报** ——
                    // 否则就是第 N+1 次 1:1 回传。计数 + 由 MismatchRetryExhausted 的那行 Warning 说明原因。
                    m_mismatchRetrySuppressedVerifications++;
                    continue;
                }
                // 覆盖后重算并**再报一次**：确认内容真的对齐（不是先认后收）。这一次重算算在回传预算里。
                uint hash = ComputeHash(part.Key);
                m_localHashes[part.Key] = hash;
                m_digestsThisTick++;
                m_digestsComputed++;
                // 回传落地后的**再证明**：必须允许 Fresh —— 本端此前可能已经核对通过（Fresh），
                // 而这一块刚被主机内容覆盖；旧代码用 TryBeginVerify（Fresh → 拒绝）⇒ 客户端不再上报，
                // 主机那条 Mismatch 永远没人重判（实机 (30,-4)/(30,-3) 一直红、"重新核对过什么都没变"）。
                if (!m_verifier.TryBeginResyncConfirm(part.Key, hash, out DigestEntry entry))
                {
                    // 明确不静默：为什么这块没有发起确认（已有一条上报在途 / NotReady 重报次数用尽）——限流一行 + 计数。
                    NoteVerifyNotStarted(part.Key, "回传落地后确认");
                    continue;
                }
                m_reportQueue.Enqueue(entry);
                m_reportSentMs[part.Key] = Environment.TickCount64;
                m_resyncVerified++;
            }
        }

        /// <summary>出队记账（"代"表只在还有排队分片时保留，不随会话无限增长）。</summary>
        private void ReleaseQueuedPart(ChunkKey key)
        {
            if (!m_queuedParts.TryGetValue(key, out int count)) return;
            if (count > 1)
            {
                m_queuedParts[key] = count - 1;
                return;
            }
            m_queuedParts.Remove(key);
            m_chunkGeneration.Remove(key);
        }

        /// <summary>
        /// 算排队中的摘要（≤ <see cref="TerrainSyncOptions.ClientDigestComputationsPerTick"/>）并放进上报队列。
        /// 块在算之前卸载了 → <c>DigestsDroppedUnloaded</c>（OnChunkUnloaded 已经把它从
        /// <see cref="m_digestQueued"/> 里摘掉，所以这里只是跳过，不重复计数）。
        /// 不丢：队列是 FIFO，只要块还在，它一定会在后续某帧被算到、发出去。
        /// </summary>
        private void ComputeQueuedDigests()
        {
            int budget = Math.Max(1, m_options.ClientDigestComputationsPerTick);
            while (budget > 0 && m_digestQueue.Count > 0)
            {
                ChunkKey key = m_digestQueue.Dequeue();
                if (!m_digestQueued.Remove(key)) continue;       // 已卸载：那边已经作废并计数
                budget--;
                if (!m_world.HasContents(key))
                {
                    // 事件之后、预算之前内容没了（卸载事件可能晚一帧）：明确计数后作废，绝不报一个"空摘要"。
                    m_digestsDroppedUnloaded++;
                    continue;
                }
                m_digestsThisTick++;        // 真的算了一次 65536 格
                m_dirtyQueued.Remove(key);      // 现算的摘要已包含最新内容 → 脏标记作废（队列项留到刷新阶段跳过）
                uint hash = ComputeHash(key);
                m_localHashes[key] = hash;
                m_digestsComputed++;
                if (!m_verifier.TryBeginVerify(key, hash, out DigestEntry entry))
                {
                    // 这几帧里状态变了（已 Fresh / 重报次数用尽）：不重复上报。放弃计数由 verifier 记；
                    // 这里再补一条**明确原因**（限流 + 计数），否则"这块为什么没上报"没有证据。
                    NoteVerifyNotStarted(key, "摘要算完后上报");
                    continue;
                }
                m_reportQueue.Enqueue(entry);
                m_reportSentMs[key] = Environment.TickCount64;
            }
        }

        /// <summary>
        /// 加载后的改动（CellChange，主机一格一条消息）→ 只标脏，整块摘要按更小的独立预算重算
        /// （≤ <see cref="TerrainSyncOptions.ClientHashRefreshesPerTick"/>）。
        /// 为什么独立：这项计算**不产生上报**（上报只在加载/回传落地时发生），只是让"裁决如何解释本端内容"
        /// 用上最新摘要，所以绝不能抢占加载摘要的预算（后者是"每块都必须上报"的硬要求）。
        /// </summary>
        private void RefreshDirtyHashes()
        {
            int budget = Math.Max(0, m_options.ClientHashRefreshesPerTick);
            while (budget > 0 && m_dirtyQueue.Count > 0)
            {
                ChunkKey key = m_dirtyQueue.Dequeue();
                if (!m_dirtyQueued.Remove(key)) continue;        // 已卸载 / 已由加载摘要覆盖
                if (!m_world.HasContents(key)) continue;          // 卸载：状态已清，无需记账（OnChunkUnloaded 已作废）
                budget--;
                m_localHashes[key] = ComputeHash(key);
                m_hashRefreshes++;
                m_digestsThisTick++;
                m_digestsComputed++;
            }
        }

        /// <summary>
        /// 发送排队中的上报（≤ <see cref="TerrainSyncOptions.ClientReportEntriesPerTick"/> 条）。
        /// **发送失败（队列满 = 显式背压）时条目留在队列里**，下一帧再发 —— 旧实现是 dequeue 之后才
        /// TrySend，失败就会静默丢掉这些上报（块永远停在 Verifying：既不上报、也不会重试）。
        /// 另外：摘要算完但还没发出去时块卸载了 → 明确计数后作废（那块内容已不在，发了也只会换来一次
        /// 无意义的回传）。
        /// </summary>
        private void SendQueuedReports()
        {
            int budget = Math.Min(Math.Max(1, m_options.ClientReportEntriesPerTick),
                Math.Max(1, m_options.DigestBatch));
            while (budget > 0 && m_reportQueue.Count > 0)
            {
                int count = Math.Min(budget, m_reportQueue.Count);
                m_reportBatch.Clear();
                int dropped = 0;
                int scanned = 0;
                foreach (DigestEntry entry in m_reportQueue)     // Queue<T> 的枚举就是 FIFO，且这里不改队列
                {
                    if (scanned++ >= count) break;
                    if (!m_world.HasContents(entry.Key))
                    {
                        if (m_memoryChunks.Contains(entry.Key))
                        {
                            // 块还在内存里、内容只是**还没定稿**：这条上报不能发（0/旧摘要会换来无谓回传），
                            // 但也不能就这么丢掉（verifier 会永远停在 Verifying）——
                            // 退回未核对并交给强制核对队列（内容定稿后按退避重试）。
                            m_reportsDeferredNotReady++;
                            m_verifier.ForceUnverified(entry.Key);
                            QueueMemoryVerification(entry.Key);
                        }
                        else
                        {
                            dropped++;
                        }
                        continue;
                    }
                    m_reportBatch.Add(entry);
                }
                if (m_reportBatch.Count == 0)
                {
                    // 这一批全是"已卸载"的陈旧上报：只出队 + 计数，不占发送预算，继续看后面的条目。
                    for (int i = 0; i < count; i++) m_reportQueue.Dequeue();
                    m_reportsDroppedUnloaded += dropped;
                    budget -= count;
                    continue;
                }
                var report = new PT.ChunkDigestReport();
                for (int i = 0; i < m_reportBatch.Count; i++)
                {
                    DigestEntry entry = m_reportBatch[i];
                    // 条目在队列里可能等了几帧：发送时用**当下**本地摘要（回传落地 / CellChange 刷新过它）。
                    // 否则会把"落地前的旧摘要"发给主机，换来一次没有意义的整块回传（1:1 循环的放大器）。
                    uint hash = entry.Hash;
                    if (m_dirtyQueued.Contains(entry.Key))
                    {
                        // 这块在排队期间收到过 CellChange（脏标记还没轮到预算）：发送前**现算**，
                        // 保证"下一次核对"带的是落地后的内容，而不是排队时的旧摘要。
                        m_dirtyQueued.Remove(entry.Key);
                        hash = ComputeHash(entry.Key);
                        m_localHashes[entry.Key] = hash;
                        m_hashRefreshes++;
                        m_digestsComputed++;
                        m_digestsThisTick++;
                        m_reportHashesRefreshed++;
                    }
                    else if (m_localHashes.TryGetValue(entry.Key, out uint latest) && latest != hash)
                    {
                        hash = latest;
                        m_reportHashesRefreshed++;
                    }
                    report.Entries.Add(new PT.ChunkDigestReport.Types.Entry
                    {
                        Key = new PT.ChunkKey { X = entry.Key.X, Z = entry.Key.Z },
                        Hash = hash
                    });
                }
                if (!m_channel.TrySend(MessageType.ChunkDigestReport, report.ToByteArray()))
                {
                    // 显式背压：条目一条都不动，下一帧再发（计数，不静默）。
                    m_reportSendDeferrals++;
                    break;
                }
                for (int i = 0; i < count; i++) m_reportQueue.Dequeue();
                m_reportsDroppedUnloaded += dropped;
                m_reportsSent++;                                  // 消息数（旧口径不变）
                m_reportEntriesSent += m_reportBatch.Count;       // 条目数（状态行的"上报"用它）
                m_reportsThisTick += m_reportBatch.Count;
                m_reportsInSecond += m_reportBatch.Count;
                budget -= count;
            }
        }

        /// <summary>
        /// 队列持续饱和 → 限流一行明确原因（<c>TerrainSyncOptions.ClientQueueSaturationLogMs</c> 起，
        /// 之后每 <c>ClientQueueLogIntervalMs</c> 至多一条）。饱和本身不是错误：它是"块加载快于预算"，
        /// 但必须可审计 —— 用户看到的"队列 K"与这一行一起说明"还在排队、没丢"。
        /// </summary>
        private void CheckQueueSaturation()
        {
            // 用"仍在册"的计数：卸载时条目是从 m_digestQueued / m_dirtyQueued 里摘掉的，Queue 里的残项
            // 会在下一次出队时直接跳过（O(1)），所以状态行的"队列 K"必须是**真正待办**的条数。
            int queued = m_digestQueued.Count + m_resyncQueue.Count + m_dirtyQueued.Count +
                (m_clientChunkFullParts.Count - m_clientChunkFullPartIndex) +
                (m_clientChunkDeltaPending.Count == 0 ? 0 : 1);
            long nowMs = Environment.TickCount64;
            if (queued == 0)
            {
                m_saturatedSinceMs = 0;
                m_lastSaturationLogMs = 0;
                return;
            }
            if (m_saturatedSinceMs == 0)
            {
                m_saturatedSinceMs = nowMs;
                return;
            }
            long saturatedMs = nowMs - m_saturatedSinceMs;
            if (saturatedMs < m_options.ClientQueueSaturationLogMs) return;
            if (m_lastSaturationLogMs != 0 && nowMs - m_lastSaturationLogMs < m_options.ClientQueueLogIntervalMs)
                return;
            m_lastSaturationLogMs = nowMs;
            Saturated?.Invoke("客户端同步队列持续饱和 " + (saturatedMs / 1000.0).ToString("0.0") + " s：" +
                "待算摘要 " + m_digestQueued.Count + "（每帧预算 " + m_options.ClientDigestComputationsPerTick + "）· " +
                "待落地回传 " + m_resyncQueue.Count + "（每帧预算 " + m_options.ClientResyncPartsPerTick + "）· " +
                "待刷新摘要 " + m_dirtyQueued.Count + "（每帧预算 " + m_options.ClientHashRefreshesPerTick + "）· " +
                "待发内存区块消息 " + (m_clientChunkFullParts.Count - m_clientChunkFullPartIndex) +
                " 片 + 增量 " + m_clientChunkDeltaPending.Count + " 块（每帧预算 " +
                m_options.ClientChunkMessagesPerTick + " 条）" +
                " → 块加载快于预算消化（飞行/快速移动时的预期背压，不是丢包）：全部块仍按 FIFO 逐帧上报，" +
                "不丢弃、不假成功；累计已算摘要 " + m_digestsComputed + " 块 · 累计上报 " + m_reportEntriesSent +
                " 条 · 本帧上报 " + m_reportsThisTick + " 条");
        }

        /// <summary>回传解码失败：计数 + 限流一行（同一原因 10s 至多一条，带上这段时间的失败次数）。</summary>
        private void ReportDecodeFailure(ChunkKey key, Exception error)
        {
            m_decodeFailuresSinceLog++;
            long nowMs = Environment.TickCount64;
            if (m_lastDecodeFailureLogMs != 0 &&
                nowMs - m_lastDecodeFailureLogMs < m_options.ClientQueueLogIntervalMs) return;
            long failures = m_decodeFailuresSinceLog;
            m_decodeFailuresSinceLog = 0;
            m_lastDecodeFailureLogMs = nowMs;
            DecodeFailed?.Invoke("回传载荷解码失败（区块 " + key + "）：" + error.GetType().Name + " " +
                error.Message + " → 这一片**不落地**（不当成已覆盖），计入 ResyncDecodeFailures；" +
                "最近这段共失败 " + failures + " 片，累计 " + m_resyncDecodeFailures + " 片");
        }

        /// <summary>
        /// 收到 NotReady 的块到点后重报一次（退避：base 起每次翻倍，封顶 NotReadyRetryMaxMs）。
        /// 这是本端的保险，让两端**不互相依赖对方的时序**：主机那边由它自己的 Tick 补齐裁决，
        /// 万一那条路卡住（内容一直加载不出来），客户端也会按节奏再问一次。
        /// 上限由 <see cref="ChunkLoadVerifier.MaxRetries"/> 兜住：超限时 <c>TryBeginVerify</c> 返回 false，
        /// 这里计入 <c>Abandoned</c>（明确可见的计数，不静默死循环）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:630-642
        ///         （旧实现待办超过 5s 重新入队 QueueClientTerrainChunkSync，循环重问）
        /// </summary>
        private void FlushDueNotReadyRetries()
        {
            if (m_notReadyRetries.Count == 0) return;
            long nowMs = Environment.TickCount64;
            List<ChunkKey> due = null;
            foreach (KeyValuePair<ChunkKey, NotReadyRetry> pair in m_notReadyRetries)
            {
                if (nowMs < pair.Value.DueMs) continue;
                if (!m_world.HasContents(pair.Key))
                {
                    // 还没定稿：**绝不报 0 摘要**（0 会被主机当成"不一致"，换来一次无谓回传）。
                    // 继续按既有退避等；重载完成后的加载摘要会兜住这块。
                    pair.Value.DueMs = nowMs + Math.Max(1, m_options.ClientMemoryVerifyRetryMs);
                    m_notReadyRetriesDeferred++;
                    continue;
                }
                (due ?? (due = new List<ChunkKey>())).Add(pair.Key);
            }
            if (due == null) return;
            for (int i = 0; i < due.Count; i++)
            {
                ChunkKey key = due[i];
                m_notReadyRetries.Remove(key);
                if (m_mismatchSuppressed.Contains(key))
                {
                    m_mismatchRetrySuppressedVerifications++;
                    continue;
                }
                // 退避期间可能来了 CellChange：用**当下内容**重算，绝不发排队时的旧摘要。
                uint hash = ComputeHash(key);
                m_localHashes[key] = hash;
                m_digestsThisTick++;
                m_digestsComputed++;
                if (!m_verifier.TryBeginVerify(key, hash, out DigestEntry entry))
                {
                    // 到点时已经不需要再报：已 Fresh（主机裁决已落地）/ 已有报告在飞 —— 只有
                    // "还是 Unverified" 才说明 TryBeginVerify 因重报次数用尽拒了，明确计数不静默
                    if (m_verifier.StateOf(key) == ChunkFreshness.Unverified) m_abandoned++;
                    continue;
                }
                m_reportQueue.Enqueue(entry);
                m_reportSentMs[key] = nowMs;
                m_reReports++;
            }
        }

        /// <summary>
        /// 主机回答"我手上还没有可用内容" → 安排一次**退避后**的重报（不是当场连发）。
        /// </summary>
        private void ScheduleNotReadyRetry(ChunkKey key)
        {
            m_notReadyRetries.TryGetValue(key, out NotReadyRetry retry);
            int attempt = retry == null ? 0 : retry.Attempt + 1;
            long delay = (long)m_options.NotReadyRetryBaseMs << Math.Min(attempt, 8);
            if (delay > m_options.NotReadyRetryMaxMs) delay = m_options.NotReadyRetryMaxMs;
            if (delay < 1L) delay = 1L;
            m_notReadyRetries[key] = new NotReadyRetry
            {
                Attempt = attempt, DueMs = Environment.TickCount64 + delay
            };
        }

        /// <summary>
        /// 客户端 → 主机：本机玩家的一份位置/姿态快照（旧 <c>GamePlayerPositionMessage</c> /
        /// 客户端分支里等价的输入快照位置段）。返回 false = 通道没连上或队列满（调用方写日志）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:38-45（!IsHost → 发本机位置/输入）
        /// </summary>
        public bool SendPlayerPosition(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return false;
            return m_channel.TrySend(MessageType.PlayerPosition, payload);
        }

        private void OnFrame(ReceivedFrame frame) => m_messages.Dispatch(in frame);

        /// <summary>
        /// HelloAck：旧字段（1, clientId, 房间名, 0, 20）+ 末尾追加的主机身份与名字；
        /// 本版再追加（append-only）：主机会话标识 + 是否接受重连 + 当前改动水位 + 依据。
        /// 旧构建不带追加字段 → 会话留空、接受=false；调用方据此明确作废旧会话（不拿默认值猜）。
        /// </summary>
        private void HandleHelloAck(HelloAck ack)
        {
            // 主机拒绝加入（手动审批 Reject / 审批超时）：明确中止，绝不把这条 Ack 当成握手成功。
            if (ack.Rejected)
            {
                JoinRejected?.Invoke(ack.ClientId, ack.RejectReason ?? string.Empty);
                return;
            }
            // 传输层 clientId：位置快照批用它认"本机玩家自己那一条"。
            LocalClientId = ack.ClientId;
            HostIdentity = ack.HostIdentity ?? string.Empty;
            HostName = ack.HostName ?? string.Empty;
            int hostClientId = -1;
            string hostProfileIdentity = string.Empty;
            bool hostProfileIsHost = false;
            bool hostProfileHasCharacter = false;
            HostProfile = PlayerProfileCodec.FromMessage(ack.HostProfile, out hostClientId,
                out hostProfileIdentity, out hostProfileIsHost, out hostProfileHasCharacter);
            int assignedClientId = -1;
            string assignedIdentity = string.Empty;
            bool assignedIsHost = false;
            bool assignedHasCharacter = false;
            AssignedProfile = PlayerProfileCodec.FromMessage(ack.AssignedProfile,
                out assignedClientId, out assignedIdentity, out assignedIsHost,
                out assignedHasCharacter);
            AssignedSkinAssetPresent = ack.AssignedSkinAssetPresent;
            HasAssignedPlacement = ack.HasAssignedPlacement;
            HasAssignedState = ack.HasAssignedState;
            AssignedIsFlying = ack.AssignedIsFlying;
            AssignedIsCrouching = ack.AssignedIsCrouching;
            AssignedPosition = ack.AssignedPosition != null
                ? new SuMultiplayer.Players.PlayerPlacement(ack.AssignedPosition.X,
                    ack.AssignedPosition.Y, ack.AssignedPosition.Z)
                : default(SuMultiplayer.Players.PlayerPlacement);
            AssignedSpawnPosition = ack.AssignedSpawnPosition != null
                ? new SuMultiplayer.Players.PlayerPlacement(ack.AssignedSpawnPosition.X,
                    ack.AssignedSpawnPosition.Y, ack.AssignedSpawnPosition.Z)
                : default(SuMultiplayer.Players.PlayerPlacement);
            // 下载世界导入后由 GameLoading 直接加载 Project.xml 的 Players 段；引擎第一次出生
            // 前把这个位置交给 SuSubsystemPlayers.Load 预置，避免先出生在主机位置再瞬移。
            if (ack.HasAssignedPlacement && ack.AssignedPosition != null)
            {
                Vector3 earlyPosition = new Vector3(ack.AssignedPosition.X,
                    ack.AssignedPosition.Y, ack.AssignedPosition.Z);
                Vector3 earlySpawn = ack.AssignedSpawnPosition != null
                    ? new Vector3(ack.AssignedSpawnPosition.X, ack.AssignedSpawnPosition.Y,
                        ack.AssignedSpawnPosition.Z)
                    : earlyPosition;
                SuMultiplayer.Engine.ClientSpawnPlacement.Set(earlyPosition, earlySpawn);
            }
            else
            {
                SuMultiplayer.Engine.ClientSpawnPlacement.Clear();
            }
            // 主机要求先创建角色（旧 SCMP_PROFILE_REQUIRED）：本连接不会有世界。
            ProfileRequired = ack.ProfileRequired;
            ProfileRequiredReason = ack.ProfileRequiredReason ?? string.Empty;
            HostSessionId = ack.SessionId ?? string.Empty;
            RejoinAccepted = ack.RejoinAccepted;
            RejoinHeadSequence = ack.HeadSequence;
            RejoinReason = ack.RejoinReason ?? string.Empty;
            Handshaken = true;
            Handshakes++;
            if (m_helloClaimedRejoin && !RejoinAccepted)
            {
                // 我们声明了重连，主机不接受：会话不一致 / 历史裁剪 / 旧主机。
                // 本版主机会在 reason 里写清依据并**在这条连接上直接下发整图**（hostWillSendWorld=true）；
                // 旧主机没有这些字段（reason 为空）→ 调用方必须断开重连成首次加入，否则永远等不到世界。
                bool hostWillSendWorld = !string.IsNullOrEmpty(RejoinReason);
                m_catchUpRejectedOnce = true;
                CatchUpRejectReason = string.IsNullOrEmpty(RejoinReason)
                    ? "主机没有会话字段（旧构建）→ 无法确认同一会话"
                    : RejoinReason;
                CatchUpTrace?.Invoke("主机拒绝重连（alreadyHasWorld=true → RejoinAccepted=false）：" +
                    CatchUpRejectReason + " · 主机会话=" + SessionIdentity.Short(HostSessionId) +
                    " · 上次应用序号=" + m_lastAppliedChangeSequence +
                    (hostWillSendWorld
                        ? " → 主机将在本连接下发整图（客户端作废旧会话后等导入）"
                        : " → 旧主机不会下发整图，必须断开重连成首次加入"));
                RejoinRejected?.Invoke(CatchUpRejectReason, hostWillSendWorld);
            }
            else if (RejoinAccepted)
            {
                m_catchUpRequestAttempts = 0;
                m_catchUpRequestSentMs = 0L;
                m_catchUpBeginReceived = false;
                m_catchUpBarrierReceived = false;
                m_catchUpCompleted = false;
                m_catchUpRejectedOnce = false;
                m_catchUpTargetSequence = -1L;
                m_catchUpBarrierMs = 0L;
                m_catchUpAppliedChanges = 0L;
                m_catchUpMessages = 0L;
                m_catchUpNoResponseLogged = false;
                m_catchUpAppliedSent = false;
                m_catchUpSendFailureLogged = false;
                m_catchUpAppliedSendFailureLogged = false;
                CatchUpTrace?.Invoke("主机接受重连：会话=" + SessionIdentity.Short(HostSessionId) +
                    " · 上次应用序号=" + m_lastAppliedChangeSequence +
                    " · 主机水位=" + RejoinHeadSequence +
                    " → 将请求追帧（不下整图）");
                // 同一会话重连：本端是新的 TerrainSyncClient（摘要状态为空），而已经常驻的块不会
                // 再触发"加载完"事件 → 必须在追帧完成后主动把它们全部重新排进核对队列
                // （TryRequeueResidentChunks 会等 CatchUpCompleted；这里只置标记）。
                ArmResidentRequeue("同一会话重连被接受，追帧完成后核对全部常驻区块");
            }
            // (重)连握手完成 → 先发一份**内存区块集合（引擎已分配）**整集，让主机不残留上一轮的旧路径
            // （"客户端的方框只显示当前存在于客户端内存的"）。之后只发加载/卸载增量。
            if (!ProfileRequired)
            {
                QueueClientChunkFullSet();
            }
            else
            {
                // 主机要求先创建角色：本连接不会有世界，也没有"客户端内存区块集合"可报。
                // 不发这一帧 = 不在主机侧留下一个没有世界的客户端状态。
                CatchUpTrace?.Invoke("HelloAck 要求先创建角色（" +
                    (string.IsNullOrEmpty(ProfileRequiredReason) ? "主机未给原因" : ProfileRequiredReason) +
                    "）→ 本次不上报客户端内存区块集合，等角色创建后重新握手");
            }
        }

        /// <summary>
        /// 追帧控制帧（主机 → 客户端）：Begin（范围）/ Barrier（重放完）/ Rejected（历史裁剪，必须整图重下）。
        /// Request / Applied 是反方向，客户端收到就记一行协议方向错误（不静默）。
        /// </summary>
        private void HandleCatchUp(PT.CatchUp message)
        {
            try
            {
                HandleCatchUpCore(message);
            }
            catch (Exception e)
            {
                // 协议异常：明确计数 + 一行原因；绝不把半个控制帧当成"追帧成功"。
                m_catchUpRejectedOnce = true;
                CatchUpRejectReason = "追帧控制帧解析失败：" + e.GetType().Name + " " + e.Message;
                CatchUpProblem?.Invoke(CatchUpRejectReason + " → 丢弃该帧（不猜阶段/范围）；请主机检查协议版本");
                // 解析失败 = 无法确认追帧完成：走"整图重下"的显式路径（绝不带着可能过期的世界继续玩）。
                CatchUpRejected?.Invoke(CatchUpRejectReason);
            }
        }

        private void HandleCatchUpCore(PT.CatchUp message)
        {
            var stage = (CatchUpStage)(int)message.Phase;
            long lastApplied = message.LastApplied;
            long head = message.Head;
            long firstRetained = message.FirstRetained;
            int changeCount = message.ChangeCount;
            string reason = message.Reason ?? string.Empty;
            switch (stage)
            {
                case CatchUpStage.Begin:
                    m_catchUpBeginReceived = true;
                    m_catchUpBarrierReceived = false;
                    m_catchUpTargetSequence = head;
                    m_catchUpBarrierMs = 0L;
                    m_catchUpAppliedChanges = 0L;
                    m_catchUpMessages = 0L;
                    // 重放期间：比水位还新的实时改动先暂存（否则旧重放会把它们盖回去）。
                    m_replayActive = true;
                    m_replayTargetSequence = head;
                    m_replayStartedMs = Environment.TickCount64;
                    m_replayStuckLogged = false;
                    CatchUpTrace?.Invoke("追帧开始（主机）：请求序号=" + lastApplied +
                        " · 主机水位=" + head +
                        " · 保留历史=[" + (firstRetained > head ? "empty" : firstRetained + ".." + head) + "]" +
                        " · 待重放改动=" + changeCount + " 条 → 等 CellChange 重放与 Barrier");
                    break;
                case CatchUpStage.Barrier:
                    if (!m_catchUpBeginReceived)
                    {
                        // 没有 Begin 就收到 Barrier：协议异常，明确记一行（仍然按水位尝试完成）。
                        CatchUpProblem?.Invoke("收到追帧 Barrier 但之前没有 Begin（head=" + head +
                            "）→ 仍按水位判定，记录协议异常");
                    }
                    m_catchUpBarrierReceived = true;
                    m_catchUpBarrierMs = Environment.TickCount64;
                    m_catchUpTargetSequence = head;
                    if (m_lastAppliedChangeSequence >= head)
                    {
                        // 重放数据帧先于 Barrier 到达（同一条 TCP 流）→ 到水位 = 重放已全部落地。
                        m_replayActive = false;
                        FlushHeldChanges("Barrier 且已到水位");
                    }
                    // 未到水位：保持 m_replayActive，暂存的实时改动继续等（绝不把旧重放盖到新改动上）。
                    CatchUpTrace?.Invoke("追帧重放已发完（Barrier）：主机水位=" + head +
                        " · 本端上次应用=" + m_lastAppliedChangeSequence +
                        (m_lastAppliedChangeSequence >= head ? " → 已到水位，回报 Applied" : " → 等剩余重放数据"));
                    break;
                case CatchUpStage.Rejected:
                    m_replayActive = false;
                    if (m_heldChanges.Count > 0)
                    {
                        // 主机要求整图重下：暂存的实时改动由整图 / 后续核对覆盖，明确计数（不静默丢）。
                        m_heldLiveDropped += m_heldChanges.Count;
                        m_heldChanges.Clear();
                    }
                    m_catchUpRejectedOnce = true;
                    CatchUpRejectReason = string.IsNullOrEmpty(reason)
                        ? "主机拒绝追帧（未给原因）"
                        : reason;
                    CatchUpTrace?.Invoke("追帧被拒绝（主机）：请求序号=" + lastApplied +
                        " · 主机水位=" + head +
                        " · 保留历史=[" + (firstRetained > head ? "empty" : firstRetained + ".." + head) + "]" +
                        " · 原因=" + CatchUpRejectReason + " → 必须作废旧会话、整图重下");
                    CatchUpRejected?.Invoke(CatchUpRejectReason);
                    break;
                case CatchUpStage.Request:
                case CatchUpStage.Applied:
                    CatchUpProblem?.Invoke("客户端收到不该由主机发送的追帧阶段 " + stage +
                        " → 丢弃（协议方向错误，计入日志）");
                    break;
                default:
                    CatchUpProblem?.Invoke("客户端收到未知追帧阶段 " + (byte)stage + " → 丢弃（不猜语义）");
                    break;
            }
        }

        /// <summary>
        /// 每帧推进追帧请求 / 完成回报：
        ///   · 接受重连后请求一次（Begin 没回来就按 <see cref="TerrainSyncOptions.CatchUpRequestRetryMs"/> 重发，
        ///     上限 <see cref="TerrainSyncOptions.CatchUpRequestMaxRetries"/>，用尽写一行"主机没回应"）；
        ///   · Barrier 到且本端已应用 >= 水位 ⇒ 回报 Applied（本轮完成）。
        /// 由 <see cref="Tick"/> 调用（主线程）。
        /// </summary>
        private void UpdateCatchUpOnTick(long nowMs)
        {
            if (!Handshaken || !RejoinAccepted || m_catchUpCompleted || m_catchUpRejectedOnce) return;
            if (m_replayActive && m_replayStartedMs != 0 &&
                nowMs - m_replayStartedMs > m_options.CatchUpAckTimeoutMs)
            {
                // 追帧迟迟不结束：暂存的实时改动不能无限等（否则它们永远不落地）。
                // 明确写一行 + 把这些块排进强制核对（主机对比 → 不一致回传整块），然后丢弃暂存。
                if (m_heldChanges.Count > 0)
                {
                    if (!m_replayStuckLogged)
                    {
                        m_replayStuckLogged = true;
                        CatchUpProblem?.Invoke("追帧超过 " + m_options.CatchUpAckTimeoutMs +
                            "ms 仍未结束（已应用=" + m_lastAppliedChangeSequence + " / 目标=" +
                            m_replayTargetSequence + "）→ 暂存的 " + m_heldChanges.Count +
                            " 条实时改动不再无限等：全部排进强制核对后丢弃暂存（不一致由主机回传整块）");
                    }
                    for (int i = 0; i < m_heldChanges.Count; i++)
                        QueueMemoryVerification(m_heldChanges[i].Key);
                    m_heldLiveDropped += m_heldChanges.Count;
                    m_heldChanges.Clear();
                }
                m_replayActive = false;
                m_replayTargetSequence = -1L;
                m_replayStartedMs = 0L;
            }
            if (!m_catchUpBeginReceived)
            {
                bool due = m_catchUpRequestSentMs == 0 ||
                    nowMs - m_catchUpRequestSentMs >= m_options.CatchUpRequestRetryMs;
                if (due && m_catchUpRequestAttempts < m_options.CatchUpRequestMaxRetries)
                {
                    var request = new PT.CatchUp
                    {
                        Phase = PT.CatchUpPhase.Request,
                        LastApplied = m_lastAppliedChangeSequence
                    };
                    if (!m_channel.TrySend(MessageType.CatchUp, request.ToByteArray()))
                    {
                        // 显式背压：这次没发出去，不算一次尝试、也不假装请求成功；下一帧再试。
                        m_catchUpRequestSentMs = nowMs;
                        if (!m_catchUpSendFailureLogged)
                        {
                            m_catchUpSendFailureLogged = true;
                            CatchUpProblem?.Invoke("追帧请求发送失败（显式背压 / 连接不可用）→ 不假装已请求，" +
                                "下一帧重试；若一直发不出去，tick 的看门狗会拆链重连");
                        }
                        return;
                    }
                    m_catchUpRequestAttempts++;
                    m_catchUpRequestSentMs = nowMs;
                    // 从发出 Request 起就进入"重放中"：Request 与 Begin 之间到达的实时改动同样要暂存
                    // （主机在收到 Request 之前也不知道这条连接要追帧，不会有"追帧在途推迟"）。
                    m_replayActive = true;
                    m_replayTargetSequence = Math.Max(m_replayTargetSequence, RejoinHeadSequence);
                    m_replayStartedMs = nowMs;      // 每一轮请求都重新起算（上一轮的卡住超时不能误伤这一轮）
                    m_replayStuckLogged = false;
                    CatchUpTrace?.Invoke("请求追帧（第 " + m_catchUpRequestAttempts + "/" +
                        m_options.CatchUpRequestMaxRetries + " 次）：上次应用序号=" +
                        m_lastAppliedChangeSequence + " · 主机会话=" +
                        SessionIdentity.Short(HostSessionId));
                }
                else if (m_catchUpRequestAttempts >= m_options.CatchUpRequestMaxRetries &&
                    !m_catchUpNoResponseLogged)
                {
                    m_catchUpNoResponseLogged = true;
                    CatchUpProblem?.Invoke("追帧请求已重发 " + m_catchUpRequestAttempts +
                        " 次仍没有收到主机 Begin（上次应用序号=" + m_lastAppliedChangeSequence +
                        "）→ 不再无限重发；本端保留上次应用序号，下次重连时再请求（不假装追帧成功）");
                    if (m_replayActive)
                    {
                        // 追帧确认不会发生：暂存的实时改动不能一直压着（否则永远不落地）。
                        // 全部排进强制核对后丢弃暂存，并退出"重放中"（后续实时改动按序号正常应用）。
                        for (int i = 0; i < m_heldChanges.Count; i++)
                            QueueMemoryVerification(m_heldChanges[i].Key);
                        m_heldLiveDropped += m_heldChanges.Count;
                        m_heldChanges.Clear();
                        m_replayActive = false;
                        m_replayTargetSequence = -1L;
                        m_replayStartedMs = 0L;
                    }
                }
                return;
            }
            if (m_catchUpBarrierReceived)
            {
                if (m_lastAppliedChangeSequence >= m_catchUpTargetSequence)
                {
                    if (m_catchUpAppliedSent) return;
                    var appliedMessage = new PT.CatchUp
                    {
                        Phase = PT.CatchUpPhase.Applied,
                        LastApplied = m_lastAppliedChangeSequence,
                        Head = m_catchUpTargetSequence
                    };
                    if (!m_channel.TrySend(MessageType.CatchUp, appliedMessage.ToByteArray()))
                    {
                        // 完成回报没发出去：不标完成（下一帧重试），也绝不假装追帧已确认。
                        if (!m_catchUpAppliedSendFailureLogged)
                        {
                            m_catchUpAppliedSendFailureLogged = true;
                            CatchUpProblem?.Invoke("追帧完成回报（Applied）发送失败（显式背压 / 连接不可用）→ " +
                                "不标完成，下一帧重试（不假装追帧已确认）");
                        }
                        return;
                    }
                    m_catchUpAppliedSent = true;
                    m_catchUpCompleted = true;
                    CatchUpTrace?.Invoke("追帧完成：本端已应用到=" + m_lastAppliedChangeSequence +
                        " · 主机水位=" + m_catchUpTargetSequence +
                        " · 重放落地改动=" + m_catchUpAppliedChanges + " 条 / " +
                        m_catchUpMessages + " 批 → 已回报 Applied");
                }
                else if (m_catchUpBarrierMs != 0 &&
                    nowMs - m_catchUpBarrierMs > m_options.CatchUpAckTimeoutMs &&
                    nowMs - m_catchUpWaitTimeoutLogged > m_options.CatchUpLogIntervalMs)
                {
                    m_catchUpWaitTimeoutLogged = nowMs;
                    CatchUpProblem?.Invoke("追帧 Barrier 已到但本端仍未到水位（已应用=" +
                        m_lastAppliedChangeSequence + " / 目标=" + m_catchUpTargetSequence +
                        "，已等 " + (nowMs - m_catchUpBarrierMs) + "ms）→ 保持请求状态，不假装完成；" +
                        "缺失的改动会在下一次重连追帧里重放");
                }
            }
        }

        private void HandleVerdict(PT.ChunkVerdict message)
        {
            for (int i = 0; i < message.Entries.Count; i++)
            {
                PT.ChunkVerdict.Types.Entry entry = message.Entries[i];
                if (entry?.Key == null)
                {
                    CatchUpProblem?.Invoke("ChunkVerdict 第 " + (i + 1) + " 条缺少 key → 整帧丢弃");
                    return;
                }
                var key = new ChunkKey(entry.Key.X, entry.Key.Z);
                DigestVerdict verdict;
                switch (entry.Verdict)
                {
                    case PT.ChunkDigestVerdict.NotReady: verdict = DigestVerdict.NotReady; break;
                    case PT.ChunkDigestVerdict.Match: verdict = DigestVerdict.Match; break;
                    case PT.ChunkDigestVerdict.Mismatch: verdict = DigestVerdict.Mismatch; break;
                    default:
                        CatchUpProblem?.Invoke("ChunkVerdict 第 " + (i + 1) + " 条的裁决值未知（" +
                            (int)entry.Verdict + "）→ 整帧丢弃（不猜）");
                        return;
                }
                uint hostHash = entry.HostHash;
                uint localHash = m_localHashes.TryGetValue(key, out uint h) ? h : 0u;
                switch (verdict)
                {
                    case DigestVerdict.NotReady:
                        m_verifier.OnNotReady(key);
                        m_retries++;
                        // 退避后重报（不在这一拍直接连发）：两端不互相依赖对方的时序
                        m_reportSentMs.Remove(key);
                        ScheduleNotReadyRetry(key);
                        break;
                    case DigestVerdict.Match:
                        m_notReadyRetries.Remove(key);
                        bool wasRetrying = m_mismatchRetries.ContainsKey(key) || m_mismatchCounts.ContainsKey(key);
                        m_verifier.OnVerdict(key, new DigestEntry(key, localHash), hostHash);
                        if (wasRetrying) m_convergedChunks++;
                        ResetMismatchRetry(key);        // 已收敛：连续重试预算清零
                        m_match++;
                        m_matchInSecond++;
                        m_mismatchCounts.Remove(key);
                        if (m_reportSentMs.TryGetValue(key, out long sentAt))
                        {
                            m_lastVerifyMs = Environment.TickCount64 - sentAt;
                            if (m_lastVerifyMs > m_maxVerifyMs) m_maxVerifyMs = m_lastVerifyMs;
                            m_reportSentMs.Remove(key);
                        }
                        break;
                    case DigestVerdict.Mismatch:
                        m_notReadyRetries.Remove(key);
                        m_verifier.OnVerdict(key, new DigestEntry(key, localHash), hostHash);
                        m_mismatch++;
                        m_mismatchInSecond++;
                        // 裁决是对**上报时**的摘要下的；若本端摘要此后已被回传/改动刷新到与主机一致，
                        // 这条 Mismatch 只是"迟到的裁决"，不能再计入连续重试预算（否则正常路径也会被误封顶）。
                        if (localHash != hostHash) RecordMismatch(key);
                        else m_convergedChunks++;
                        break;
                }
            }
        }

        /// <summary>
        /// 收到回传分片 —— **只入队，不在这里落地**（见类注释的每帧预算）：
        /// 解码 + 最多 65536 次格子写入 + 末片摘要重算全部搬到 <see cref="ApplyQueuedResyncParts"/>，
        /// 由 <see cref="Tick"/> 按 <see cref="TerrainSyncOptions.ClientResyncPartsPerTick"/> 逐帧消化。
        /// 这里只读前 8 字节拿到区块坐标（为了给它记"代"），载荷本身原样入队。
        /// </summary>
        private void HandleResync(MessageContext context, PT.ChunkResync message)
        {
            if (message.Key == null)
            {
                // 协议异常：明确计数（由限流原因行点名），不当成已覆盖。
                m_resyncDecodeFailures++;
                ReportDecodeFailure(new ChunkKey(0, 0),
                    new InvalidOperationException("回传载荷缺少 key"));
                return;
            }
            var key = new ChunkKey(message.Key.X, message.Key.Z);
            long watermark = message.HasWatermark ? message.Watermark : -1L;
            byte[] payload = context.Payload;
            m_chunkGeneration.TryGetValue(key, out int generation);
            m_queuedParts.TryGetValue(key, out int queued);
            m_queuedParts[key] = queued + 1;
            m_resyncQueue.Enqueue(new ResyncJob(key, payload, generation, watermark));
            m_resyncReceivedInSecond++;
        }

        /// <summary>
        /// 主机要求"重新核对内存区块"（<see cref="MessageType.VerifyChunks"/>）：
        /// 主机检测到自己无法按序号补齐缺口（背压期间历史被裁掉）时发来。
        /// 载荷：<c>Int32 count</c>（0 = 全部内存块）+ count × (chunkX, chunkZ)。
        /// 本端把对应的内存块排进**强制核对**队列（有界、FIFO）：摘要上报后主机比对，
        /// 不一致由主机回传整块 —— 这是"主机侧缺口"能收敛的唯一通道，绝不静默忽略。
        /// 非法载荷 = 明确计数 + 一行原因，不猜要核对哪些块。
        /// </summary>
        private void HandleVerifyRequest(PT.VerifyChunks message)
        {
            int count = message.Chunks.Count;
            if (count > 65536)
            {
                m_verifyRequestMalformed++;
                if (m_verifyRequestMalformed == 1)
                    CatchUpProblem?.Invoke("VerifyChunks 载荷非法（count=" + count +
                        " > 65536）→ 丢弃（不猜要核对哪些块）");
                return;
            }
            m_verifyRequestsReceived++;
            if (count == 0)
            {
                int chunks = m_memoryChunks.Count;
                m_verifyRequestChunks += chunks;
                QueueAllMemoryVerifications();
                CatchUpTrace?.Invoke("主机要求重新核对内存区块（全部 " + chunks +
                    " 块；累计请求 " + m_verifyRequestsReceived + " 次）→ 已排队强制核对，" +
                    "不一致由主机回传整块");
                return;
            }
            int queued = 0;
            int listed = 0;
            for (int i = 0; i < count; i++)
            {
                PT.ChunkKey item = message.Chunks[i];
                if (item == null)
                {
                    m_verifyRequestMalformed++;
                    CatchUpProblem?.Invoke("VerifyChunks 第 " + (i + 1) + " 块缺少坐标 → 整帧丢弃");
                    return;
                }
                var key = new ChunkKey(item.X, item.Z);
                listed++;
                if (!m_memoryChunks.Contains(key)) continue;
                QueueMemoryVerification(key);
                queued++;
            }
            m_verifyRequestChunks += queued;
            _ = listed;
            CatchUpTrace?.Invoke("主机要求重新核对 " + count + " 个指定区块 → 本端内存里有 " + queued +
                " 个已排队强制核对（累计请求 " + m_verifyRequestsReceived + " 次）");
        }

        /// <summary>
        /// 主机权威改动（一格一条消息）。本版的**顺序规则**（这是"不产生不同步"的核心）：
        ///   · 落地这一格仍是每格 O(1)；整块摘要只**标脏**，由 <see cref="RefreshDirtyHashes"/> 按预算重算；
        ///   · 每条改动带全局序号（v9 尾字段）+ 投递序号（本版尾字段：flags/prev/index）：
        ///       - 投递序号必须与上一条连续（TCP 上本不该断）→ 断了 = 显式警告 + 内存块全部强制核对；
        ///       - **实时**改动若序号 ≤ 已应用水位 = 越序 → **不落地**（绝不把旧值盖回新值）+ 强制核对；
        ///       - **追帧重放**（flags bit0）一律按升序落地（重叠重放是幂等的，最终值 = 水位那一刻的权威值）；
        ///       - 追帧期间到达的"比水位还新"的实时改动 → 有界暂存，重放结束后按序号升序应用
        ///         （否则旧重放会把新改动盖回去 —— 实机重连时确实会发生）。
        ///   · 本端内存里有这一块、但内容还没定稿 → 改动无处落地：明确计数 + 排队强制核对
        ///     （内容可用后由摘要上报让主机裁决，不一致就回传整块）。
        /// </summary>
        private void HandleCellChange(PT.CellChange message)
        {
            int count = message.Entries.Count;
            if (count < 0 || count > 4096)
            {
                m_cellChangeSequenceMalformed++;
                if (m_cellChangeSequenceMalformed == 1)
                    CatchUpProblem?.Invoke("CellChange 条目数非法（" + count + "）→ 整帧丢弃（不部分应用）");
                return;
            }
            var entries = new List<CellEdit>(count);
            var keys = new List<ChunkKey>(count);
            for (int i = 0; i < count; i++)
            {
                PT.CellChange.Types.Entry entry = message.Entries[i];
                if (entry?.Key == null || entry.Edit == null)
                {
                    m_cellChangeSequenceMalformed++;
                    if (m_cellChangeSequenceMalformed == 1)
                        CatchUpProblem?.Invoke("CellChange 第 " + (i + 1) +
                            " 条缺少 key/edit → 整帧丢弃（不部分应用）");
                    return;
                }
                keys.Add(new ChunkKey(entry.Key.X, entry.Key.Z));
                entries.Add(new CellEdit(entry.Edit.X, entry.Edit.Y, entry.Edit.Z,
                    entry.Edit.Contents, entry.Edit.Data));
            }
            long[] sequences = null;
            if (message.Sequences.Count == count)
            {
                sequences = new long[count];
                for (int i = 0; i < count; i++) sequences[i] = message.Sequences[i];
            }
            else if (message.Sequences.Count > 0)
            {
                // 明确的协议异常：序号数组与条目数不一致（不猜序号、不假装已应用）。
                m_cellChangeSequenceMalformed++;
                if (m_cellChangeSequenceMalformed == 1)
                    CatchUpProblem?.Invoke("CellChange 序号数组长度非法（seqCount=" +
                        message.Sequences.Count + " ≠ count=" + count +
                        "）→ 本帧照常落地格子，但**不推进**上次应用序号（重连时会重放，幂等）");
            }
            else
            {
                m_cellChangeBatchesWithoutSequence++;
                if (!m_oldHostSequenceLogged)
                {
                    m_oldHostSequenceLogged = true;
                    CatchUpTrace?.Invoke("CellChange 不带改动序号 → 本端无法精确追踪上次应用序号；" +
                        "重连时会退回世界清单的基线序号（不猜），历史不覆盖时由主机要求整图重下");
                }
            }
            byte flags = (byte)message.Flags;
            long prevDeliveryIndex = message.PrevDeliveryIndex;
            long deliveryIndex = message.DeliveryIndex;
            if (deliveryIndex < 0 && !m_oldHostDeliveryIndexLogged)
            {
                m_oldHostDeliveryIndexLogged = true;
                CatchUpTrace?.Invoke("CellChange 没有投递序号：本端无法证明\"中间没有缺帧\"；" +
                    "仍按序号单调应用，缺口只能靠加载 / 强制核对发现（明确记一行，不假装有缺口检测）");
            }
            bool replay = (flags & 1) != 0;
            if (deliveryIndex >= 0) CheckDeliveryIndex(prevDeliveryIndex, deliveryIndex, replay);
            int applied = 0;
            for (int i = 0; i < count; i++)
            {
                long sequence = sequences != null ? sequences[i] : -1L;
                if (sequence < 0)
                {
                    // 没有序号：照旧落地（无法参与有序应用 / 缺口检测，已在上面明确记一行）。
                    ApplyOneChange(keys[i], entries[i], 0L);
                    applied++;
                    continue;
                }
                if (replay)
                {
                    // 追帧重放：**一律按升序落地**（重叠重放是幂等的；它保证 [起点..水位] 的最终值正确）。
                    ApplyOneChange(keys[i], entries[i], sequence);
                    applied++;
                    continue;
                }
                if (sequence <= m_lastAppliedChangeSequence)
                {
                    // 越序 / 过期：**不落地**（否则旧值会把新值盖回去），只计数 + 强制核对该块。
                    NoteOutOfOrderChange(keys[i], sequence);
                    continue;
                }
                if (m_replayActive && m_replayTargetSequence >= 0 && sequence > m_replayTargetSequence)
                {
                    // 追帧期间到达的"比水位还新"的实时改动：暂存，等重放结束再按序号升序应用。
                    HoldLiveChange(keys[i], entries[i], sequence);
                    continue;
                }
                ApplyOneChange(keys[i], entries[i], sequence);
                applied++;
            }
            if (CatchUpActive && m_catchUpBeginReceived)
            {
                m_catchUpAppliedChanges += applied;
                m_catchUpMessages++;
            }
        }

        /// <summary>本块是否在客户端声明的兴趣窗口（方形）内；窗口还没声明过 → 一律按"窗口外"处理（更保守）。</summary>
        private bool WithinInterestWindow(ChunkKey key) =>
            m_interestKnown &&
            Math.Abs(key.X - Center.X) <= ViewRadius && Math.Abs(key.Z - Center.Z) <= ViewRadius;

        /// <summary>
        /// 落地一条改动（顺序规则由 <see cref="HandleCellChange"/> 保证，这里只负责执行 + 记账）：
        ///   · 本端内存有这块、但内容没定稿 → 改动无处落地：明确计数 + 排队强制核对；
        ///   · 本端不持有这块（重放里常见）→ 跳过并计数，但**全局水位照常推进**；
        ///   · 落地成功 → 标脏（摘要按预算重算）；窗口外的内存块额外排一次强制核对。
        /// </summary>
        private void ApplyOneChange(ChunkKey key, CellEdit edit, long sequence)
        {
            if (!m_world.HasContents(key))
            {
                if (m_memoryChunks.Contains(key))
                {
                    m_cellChangesDroppedNotReady++;
                    NoteChangeDroppedNotReady(key, sequence);
                    QueueMemoryVerification(key);   // 内容可用后强制核对（不一致 → 主机回传整块）
                }
                else
                {
                    m_cellChangesSkippedNotHeld++;
                }
                if (sequence > m_lastAppliedChangeSequence) m_lastAppliedChangeSequence = sequence;
                return;
            }
            m_world.ApplyCells(key, new[] { edit });
            // 新的权威改动 = 新的证据：这块的连续重试预算与"停止自动重报"抑制一并清零
            // （每块每次改动仍只发起一次核对，由既有预算约束）。
            ResetMismatchRetry(key);
            m_cellChanges++;
            if (sequence > m_lastAppliedChangeSequence) m_lastAppliedChangeSequence = sequence;
            if (sequence > 0)
            {
                long applied = m_chunkChangeSequence.TryGetValue(key, out long current) ? current : 0L;
                if (sequence > applied) m_chunkChangeSequence[key] = sequence;
            }
            if (m_dirtyQueued.Add(key)) m_dirtyQueue.Enqueue(key);   // 同一块改了 N 格只重算一次
            // 窗口外的内存块：追加一次**强制摘要核对**（有界、FIFO）——
            // "内存里有、但不在兴趣窗口"的块因此也有验证闭环（不一致会被主机回传纠正）。
            if (!WithinInterestWindow(key)) QueueMemoryVerification(key);
        }

        /// <summary>
        /// 投递序号缺口（prev ≠ 上一条）：TCP 上本不该发生。明确计数 + 限流一行，并把**全部内存块**
        /// 排进强制核对（本端无法知道缺的是哪一块，只能整体证明一遍；有界、FIFO，不一致由主机回传）。
        /// </summary>
        private void CheckDeliveryIndex(long prev, long index, bool replay)
        {
            if (!m_deliveryIndexSeen)
            {
                m_deliveryIndexSeen = true;
                if (prev != 0)
                    NoteDeliveryGap(prev, index, replay, "本连接第一条改动的 prev 不是 0");
            }
            else if (prev != m_lastDeliveryIndex)
            {
                NoteDeliveryGap(prev, index, replay, "上一条投递序号是 " + m_lastDeliveryIndex);
            }
            m_lastDeliveryIndex = index;
        }

        private void NoteDeliveryGap(long prev, long index, bool replay, string detail)
        {
            m_deliveryGaps++;
            long now = Environment.TickCount64;
            if (m_lastDeliveryGapLogMs == 0 ||
                now - m_lastDeliveryGapLogMs >= m_options.ClientDeliveryProblemLogIntervalMs)
            {
                m_lastDeliveryGapLogMs = now;
                CatchUpProblem?.Invoke("CellChange 投递序号缺口（" + (replay ? "追帧重放" : "实时") +
                    "）：收到 prev=" + prev + " / index=" + index + "，但" + detail +
                    " → TCP 上本不该发生；本帧按序号规则处理，并把本端内存区块全部排进强制核对" +
                    "（不一致由主机回传整块纠正）；累计缺口 " + m_deliveryGaps + " 次");
            }
            QueueAllMemoryVerifications();
        }

        /// <summary>实时改动序号越序（≤ 已应用水位）：**不落地**，只计数 + 强制核对（绝不把旧值盖回新值）。</summary>
        private void NoteOutOfOrderChange(ChunkKey key, long sequence)
        {
            m_outOfOrderChanges++;
            long now = Environment.TickCount64;
            if (m_lastOutOfOrderLogMs == 0 ||
                now - m_lastOutOfOrderLogMs >= m_options.ClientDeliveryProblemLogIntervalMs)
            {
                m_lastOutOfOrderLogMs = now;
                CatchUpProblem?.Invoke("CellChange 越序：序号 " + sequence + " ≤ 本端已应用水位 " +
                    m_lastAppliedChangeSequence + "（区块 " + key + "）→ **不落地**（绝不把旧值盖回新值），" +
                    "该块排进强制核对；累计越序 " + m_outOfOrderChanges + " 条");
            }
            QueueMemoryVerification(key);
        }

        /// <summary>收到改动但本块"内存里有、内容还没定稿"（引擎侧同样无内容可改）：明确计数 + 限流一行。</summary>
        private void NoteChangeDroppedNotReady(ChunkKey key, long sequence)
        {
            long now = Environment.TickCount64;
            if (m_lastNotReadyChangeLogMs == 0 ||
                now - m_lastNotReadyChangeLogMs >= m_options.ClientDeliveryProblemLogIntervalMs)
            {
                m_lastNotReadyChangeLogMs = now;
                CatchUpProblem?.Invoke("区块 " + key + " 的改动（序号 " + sequence +
                    "）到达时本端**内存里有这块、但内容还没定稿** → 这一格无处落地（引擎同样会丢）；" +
                    "已排队强制核对：内容可用后用摘要让主机裁决，不一致就回传整块（不静默继续）；" +
                    "累计 " + m_cellChangesDroppedNotReady + " 条");
            }
        }

        /// <summary>追帧期间暂存一条"比水位还新"的实时改动（有界；溢出 = 明确计数 + 强制核对）。</summary>
        private void HoldLiveChange(ChunkKey key, CellEdit edit, long sequence)
        {
            if (m_heldChanges.Count >= Math.Max(1, m_options.ClientLiveHoldMaxEntries))
            {
                m_heldLiveDropped++;
                QueueMemoryVerification(key);
                long now = Environment.TickCount64;
                if (m_lastHoldOverflowLogMs == 0 ||
                    now - m_lastHoldOverflowLogMs >= m_options.ClientDeliveryProblemLogIntervalMs)
                {
                    m_lastHoldOverflowLogMs = now;
                    CatchUpProblem?.Invoke("追帧期间暂存的实时改动超过上限 " +
                        m_options.ClientLiveHoldMaxEntries + " 条 → 这条（序号 " + sequence + "，区块 " + key +
                        "）**暂存不下**：不落地、排进强制核对（不一致由主机回传整块）；累计丢弃暂存 " +
                        m_heldLiveDropped + " 条（绝不静默）");
                }
                return;
            }
            m_heldChanges.Add(new HeldChange { Sequence = sequence, Key = key, Edit = edit });
            m_heldLiveAdded++;
        }

        /// <summary>
        /// 追帧重放结束（Barrier 且已到水位 / 被拒绝 / 卡住超时）→ 把暂存的实时改动按序号升序应用。
        /// 比水位更旧的暂存条目已被重放覆盖（跳过，不重复应用）。
        /// </summary>
        private void FlushHeldChanges(string why)
        {
            if (m_heldChanges.Count == 0) return;
            m_heldChanges.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            int applied = 0;
            for (int i = 0; i < m_heldChanges.Count; i++)
            {
                HeldChange held = m_heldChanges[i];
                if (held.Sequence <= m_lastAppliedChangeSequence) continue;   // 已被重放覆盖
                ApplyOneChange(held.Key, held.Edit, held.Sequence);
                applied++;
            }
            CatchUpTrace?.Invoke("追帧结束（" + why + "）→ 按序号升序应用追帧期间暂存的 " + applied +
                " 条实时改动（暂存共 " + m_heldChanges.Count + " 条；更旧的已被重放覆盖，不重复应用）");
            m_heldChanges.Clear();
            m_heldLiveAdded = 0L;
        }

        private void QueueAllMemoryVerifications()
        {
            foreach (ChunkKey key in m_memoryChunks) QueueMemoryVerification(key);
        }

        /// <summary>把一块排进"强制核对"队列（去重；块不在内存 / 没有内容时按规则延后或作废）。</summary>
        private void QueueMemoryVerification(ChunkKey key)
        {
            if (!m_memoryVerifyQueued.Add(key)) return;
            m_memoryVerifyQueue.Enqueue(key);
        }

        /// <summary>
        /// 客户端编辑请求超时后的**明确回滚通道**：把该块排进强制核对 —— 本端按当前（预测）内容
        /// 重算摘要上报，主机比对不一致就回传整块，于是这一格回到主机最后的值。
        /// 返回 false = 这块不在本端内存集合（没有可回滚的内容），调用方写原因。
        /// 已经排队 / 已在核对中的同一块不重复入队（计入 <see cref="TerrainClientSnapshot.MemoryVerificationsSkipped"/>）。
        /// </summary>
        public bool RequestChunkReverification(ChunkKey key)
        {
            if (!m_memoryChunks.Contains(key)) return false;
            // 显式触发（编辑回滚 / 主机要求 / 重连核对）是新的证据：重置重试预算与抑制。
            ResetMismatchRetry(key);
            m_memoryVerifyDueMs.Remove(key);
            if (m_memoryVerifyQueued.Contains(key)) return true;
            QueueMemoryVerification(key);
            return true;
        }

        /// <summary>
        /// 处理强制核对队列（每帧 ≤ <see cref="TerrainSyncOptions.ClientMemoryVerificationsPerTick"/> 块）：
        /// 算摘要 → 走**常规上报通道**（主机比对；不一致回传整块）→ 客户端按回传再报一次确认。
        /// 块没有内容时按 <see cref="TerrainSyncOptions.ClientMemoryVerifyRetryMs"/> 退避重试（不丢、不空转）。
        /// </summary>
        private void ProcessMemoryVerifications()
        {
            m_memoryVerifyThisTick = 0;
            int budget = Math.Max(1, m_options.ClientMemoryVerificationsPerTick);
            long now = Environment.TickCount64;
            int guard = 0;
            int maxGuard = m_memoryVerifyQueued.Count + 1;
            while (budget > 0 && m_memoryVerifyQueue.Count > 0 && guard++ < maxGuard)
            {
                ChunkKey key = m_memoryVerifyQueue.Peek();
                if (m_memoryVerifyDueMs.TryGetValue(key, out long due) && due > now) break;   // FIFO 头还没到重试时间
                m_memoryVerifyQueue.Dequeue();
                if (!m_memoryVerifyQueued.Contains(key)) { m_memoryVerifyDueMs.Remove(key); continue; }
                if (!m_memoryChunks.Contains(key))
                {
                    // 排队期间这块已经释放：作废（重载会重新上报、重新核对）。
                    m_memoryVerifyQueued.Remove(key);
                    m_memoryVerifyDueMs.Remove(key);
                    m_memoryVerifyDroppedUnloaded++;
                    continue;
                }
                if (m_mismatchSuppressed.Contains(key))
                {
                    // 连续重试已超上限：不再自动核对（显式计数；下一次显式触发会 ResetMismatchRetry）。
                    m_memoryVerifyQueued.Remove(key);
                    m_memoryVerifyDueMs.Remove(key);
                    m_memoryVerifySkipped++;
                    m_mismatchRetrySuppressedVerifications++;
                    continue;
                }
                if (!m_world.HasContents(key))
                {
                    // 已分配但内容还没定稿：保持排队，退避后重试（不丢、也不每帧空转）。
                    m_memoryVerifyDeferred++;
                    m_memoryVerifyDueMs[key] = now + Math.Max(1, m_options.ClientMemoryVerifyRetryMs);
                    m_memoryVerifyQueue.Enqueue(key);
                    break;
                }
                m_memoryVerifyQueued.Remove(key);
                m_memoryVerifyDueMs.Remove(key);
                budget--;
                uint hash = ComputeHash(key);
                m_localHashes[key] = hash;
                m_digestsThisTick++;
                m_digestsComputed++;
                if (!m_verifier.TryBeginReverify(key, hash, out DigestEntry entry))
                {
                    // 已有一条上报在途 / 已确认不一致等主机回传：不重复发起（状态机会继续走）。
                    m_memoryVerifySkipped++;
                    continue;
                }
                m_reportQueue.Enqueue(entry);
                m_reportSentMs[key] = now;
                m_memoryVerifySent++;
                m_memoryVerifyThisTick++;
            }
        }

        /// <summary>指数退避（第一次 = baseMs，之后翻倍，封顶 maxMs；永远 ≥1ms）。</summary>
        private static long RetryBackoffMs(int attempt, int baseMs, int maxMs)
        {
            int shift = Math.Min(Math.Max(attempt - 1, 0), 16);
            long delay = (long)Math.Max(1, baseMs) << shift;
            return Math.Max(1L, Math.Min(delay, Math.Max(1, maxMs)));
        }

        /// <summary>清掉一块的重试预算 / 抑制 / "丢弃后重核"安排（Match / 新改动 / 重载 / 显式请求时调用）。</summary>
        private void ResetMismatchRetry(ChunkKey key)
        {
            m_mismatchRetries.Remove(key);
            m_discardRefreshDueMs.Remove(key);
            m_mismatchSuppressed.Remove(key);
        }

        /// <summary>
        /// 记一次"不一致"的连续重试。返回 true = 仍在预算内；false = 已超上限（调用方不得再自动重报）。
        /// 超限时写**限流** Warning（区块键 + 原因 + 次数）：这是"绝不无界 1:1 回传循环"的硬闸门。
        /// 一次 Match / 新 CellChange / 重载会把预算清零（见 <see cref="ResetMismatchRetry"/>）。
        /// </summary>
        private bool NoteMismatchAttempt(ChunkKey key, string reason)
        {
            if (!m_mismatchRetries.TryGetValue(key, out MismatchRetryState state))
            {
                state = new MismatchRetryState();
                m_mismatchRetries[key] = state;
            }
            state.Attempt++;
            int cap = Math.Max(1, m_options.ClientMismatchRetryCap);
            if (state.Attempt <= cap) return true;
            if (!m_mismatchSuppressed.Add(key)) return false;
            m_mismatchRetryExhaustedCount++;
            long nowMs = Environment.TickCount64;
            if (state.LastWarnMs != 0 &&
                nowMs - state.LastWarnMs < m_options.ClientMismatchRetryWarnIntervalMs) return false;
            state.LastWarnMs = nowMs;
            m_mismatchCounts.TryGetValue(key, out int total);
            MismatchRetryExhausted?.Invoke("区块 " + key + " 的连续不一致重试超过上限 " + cap +
                " 次（原因：" + reason + "）→ **停止自动重报**，不再形成 1:1 回传循环；" +
                "本端内容仍与主机不一致，等待下一次显式触发（区块重新加载 / 主机下发改动 / 重连核对 / 强制核对请求）。" +
                "累计不一致 " + total + " 次 · 该块连续重试 " + state.Attempt +
                " 次 · 累计进入上限 " + m_mismatchRetryExhaustedCount + " 块");
            return false;
        }

        /// <summary>
        /// 回传片因"水位过期 / 零格快照"被丢弃 → 安排一次**退避后**的重新核对（不是每片当场重报）。
        /// 丢弃本身不增加重试次数；重试次数由主机下一轮 Mismatch 裁决推进（<see cref="NoteMismatchAttempt"/>）。
        /// </summary>
        private void ScheduleDiscardRefresh(ChunkKey key, string reason)
        {
            if (m_mismatchSuppressed.Contains(key))
            {
                // 预算已用尽：不再自动请求新快照（内容仍可能被后续回传落地）。
                m_discardRefreshSkipped++;
                return;
            }
            int attempt = m_mismatchRetries.TryGetValue(key, out MismatchRetryState state)
                ? Math.Max(1, state.Attempt)
                : 1;
            long due = Environment.TickCount64 +
                RetryBackoffMs(attempt, m_options.ClientMismatchRetryBaseMs, m_options.ClientMismatchRetryMaxMs);
            if (m_discardRefreshDueMs.TryGetValue(key, out long existing) && existing <= due) return;
            m_discardRefreshDueMs[key] = due;
        }

        /// <summary>
        /// 到点的"丢弃后重新核对"：**现算**摘要 → 常规上报（= 让主机基于当下内容重发新快照）。
        /// 每块每轮最多一条；块还没内容时按 ClientMemoryVerifyRetryMs 延后（不报 0 摘要）。
        /// </summary>
        private void FlushDiscardRefreshes()
        {
            if (m_discardRefreshDueMs.Count == 0) return;
            long nowMs = Environment.TickCount64;
            List<ChunkKey> due = null;
            foreach (KeyValuePair<ChunkKey, long> pair in m_discardRefreshDueMs)
            {
                if (pair.Value > nowMs) continue;
                (due ?? (due = new List<ChunkKey>())).Add(pair.Key);
            }
            if (due == null) return;
            for (int i = 0; i < due.Count; i++)
            {
                ChunkKey key = due[i];
                m_discardRefreshDueMs.Remove(key);
                if (m_mismatchSuppressed.Contains(key))
                {
                    m_discardRefreshSkipped++;
                    continue;
                }
                if (!m_memoryChunks.Contains(key))
                {
                    m_discardRefreshSkipped++;
                    continue;
                }
                if (!m_world.HasContents(key))
                {
                    m_discardRefreshDeferred++;
                    m_discardRefreshDueMs[key] = nowMs + Math.Max(1, m_options.ClientMemoryVerifyRetryMs);
                    continue;
                }
                if (m_verifier.StateOf(key) != ChunkFreshness.Unverified)
                {
                    // 期间已经有新的上报在途 / 已 Fresh / 已 Stale（等新回传）：不重复发起。
                    m_discardRefreshSkipped++;
                    continue;
                }
                uint hash = ComputeHash(key);
                m_localHashes[key] = hash;
                m_digestsThisTick++;
                m_digestsComputed++;
                if (!m_verifier.TryBeginVerify(key, hash, out DigestEntry entry))
                {
                    m_discardRefreshSkipped++;
                    continue;
                }
                m_reportQueue.Enqueue(entry);
                m_reportSentMs[key] = nowMs;
                m_resyncRefreshedAfterDiscard++;
                // 与强制核对同一口径（同一份"必须证明一次"的工作；HH/RQ5 用这个计数断言"丢弃后真的核对了"）。
                m_memoryVerifySent++;
                m_memoryVerifyThisTick++;
            }
        }

        /// <summary>零格快照：明确计数 + 限流一行（不把它当成"已覆盖"，也不当场重报）。</summary>
        private void NoteResyncEmptySnapshot(ChunkKey key)
        {
            long nowMs = Environment.TickCount64;
            if (m_lastResyncEmptyLogMs != 0 &&
                nowMs - m_lastResyncEmptyLogMs < m_options.ClientQueueLogIntervalMs) return;
            m_lastResyncEmptyLogMs = nowMs;
            CatchUpProblem?.Invoke("收到零格回传快照（区块 " + key + "）：主机裁决时还有内容、编码时已没有" +
                " → **不当成已覆盖**（不落地、不重报）；已按退避重新核对，让主机内容可用后重发新快照。" +
                "累计零格快照 " + m_resyncEmptySnapshots + " 片");
        }

        /// <summary>单块当前连续重试次数（0 = 没有）。</summary>
        public int MismatchRetryCount(ChunkKey key) =>
            m_mismatchRetries.TryGetValue(key, out MismatchRetryState state) ? state.Attempt : 0;

        /// <summary>当前最大的单块连续重试次数（0 = 没有重试中的块）。</summary>
        private int MaxMismatchRetry()
        {
            int max = 0;
            foreach (KeyValuePair<ChunkKey, MismatchRetryState> pair in m_mismatchRetries)
            {
                if (pair.Value.Attempt > max) max = pair.Value.Attempt;
            }
            return max;
        }

        /// <summary>回传快照比本块已应用改动更旧 → 丢弃 + 限流一行（按退避重新核对，让主机重发新快照）。</summary>
        private void NoteResyncSuperseded(ChunkKey key, long appliedSequence, long watermark)
        {
            long now = Environment.TickCount64;
            if (m_lastResyncSupersededLogMs == 0 ||
                now - m_lastResyncSupersededLogMs >= m_options.ClientResyncSupersededLogIntervalMs)
            {
                m_lastResyncSupersededLogMs = now;
                CatchUpProblem?.Invoke("回传快照过期（区块 " + key + "）：快照编码水位=" + watermark +
                    "，但本端已应用改动序号=" + appliedSequence +
                    " → **丢弃这一片**（落地会把新值盖回旧值），已按指数退避安排重新核对" +
                    "（不是每片当场重报，避免 1:1 回传循环）让主机重发新快照；" +
                    "累计丢弃 " + m_resyncDroppedSuperseded + " 片");
            }
        }

        /// <summary>出现过的"不一致"区块数（去重；按块生命周期清掉）。</summary>
        private int MismatchChunkCount() => m_mismatchCounts.Count;

        /// <summary>同一块累计不一致 ≥ 阈值的区块数（"反复不一致" = 回传风暴特征）。</summary>
        private int MismatchRepeatChunkCount()
        {
            int n = 0;
            foreach (KeyValuePair<ChunkKey, int> pair in m_mismatchCounts)
            {
                if (pair.Value >= m_options.ClientMismatchRepeatLogThreshold) n++;
            }
            return n;
        }

        /// <summary>
        /// 当前"不一致区块 → 累计次数"的快照（按次数降序；测试与日志用它点名具体区块）。
        /// 返回的是**新列表**，调用方随便改；不暴露内部字典。
        /// </summary>
        public List<KeyValuePair<ChunkKey, int>> MismatchHotspots()
        {
            var list = new List<KeyValuePair<ChunkKey, int>>(m_mismatchCounts);
            list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : 0);
            return list;
        }

        public TerrainClientSnapshot GetSnapshot()
        {
            var snapshot = new TerrainClientSnapshot
            {
                Center = Center, Radius = ViewRadius, ReportsSent = m_reportsSent, Match = m_match,
                Mismatch = m_mismatch, ResyncApplied = m_resyncApplied, ResyncVerified = m_resyncVerified,
                CellChangesApplied = m_cellChanges, Retries = m_retries, LastVerifyMs = m_lastVerifyMs,
                MaxVerifyMs = m_maxVerifyMs, State = m_channel.State, BytesReceived = m_channel.Stats.BytesReceived,
                ReReports = m_reReports, Abandoned = m_abandoned,
                QueuedDigests = m_digestQueued.Count, QueuedResyncParts = m_resyncQueue.Count,
                QueuedHashRefreshes = m_dirtyQueued.Count,
                DigestsThisTick = m_digestsThisTick, ReportsThisTick = m_reportsThisTick,
                ResyncAppliedThisTick = m_resyncAppliedThisTick,
                DigestBudgetPerTick = m_options.ClientDigestComputationsPerTick,
                ResyncBudgetPerTick = m_options.ClientResyncPartsPerTick,
                HashRefreshBudgetPerTick = m_options.ClientHashRefreshesPerTick,
                ReportBudgetPerTick = m_options.ClientReportEntriesPerTick,
                DigestsComputed = m_digestsComputed, ReportEntriesSent = m_reportEntriesSent,
                DigestsDroppedUnloaded = m_digestsDroppedUnloaded,
                ReportsDroppedUnloaded = m_reportsDroppedUnloaded,
                HashRefreshesDroppedUnloaded = m_hashRefreshesDroppedUnloaded,
                ResyncDroppedUnloaded = m_resyncDroppedUnloaded,
                ResyncDecodeFailures = m_resyncDecodeFailures,
                ReportSendDeferrals = m_reportSendDeferrals,
                ReportsDeferredNotReady = m_reportsDeferredNotReady,
                // 内存区块集合（v8 实时地图；= 引擎已分配的区块）：队列 = 整集剩余分片 + 增量批次（≤64 条/批）
                MemoryChunkCount = m_memoryChunks.Count,
                ClientChunkFullSetsSent = m_clientChunkFullSetsSent,
                ClientChunkMessagesSent = m_clientChunkMessagesSent,
                ClientChunkEntriesSent = m_clientChunkEntriesSent,
                ClientChunkDeltaMessagesSent = m_clientChunkDeltaMessagesSent,
                QueuedClientChunkMessages = (m_clientChunkFullParts.Count - m_clientChunkFullPartIndex) +
                    (m_clientChunkDeltaPending.Count == 0
                        ? 0
                        : (m_clientChunkDeltaPending.Count + ClientChunkSetCodec.MaxEntriesPerMessage - 1) /
                          ClientChunkSetCodec.MaxEntriesPerMessage),
                ClientChunkMessagesThisTick = m_clientChunkMessagesThisTick,
                ClientChunkBudgetPerTick = m_options.ClientChunkMessagesPerTick,
                ClientChunkSendDeferrals = m_clientChunkSendDeferrals,
                // 断线重连追帧
                LastAppliedChangeSequence = m_lastAppliedChangeSequence,
                CatchUpTargetSequence = m_catchUpTargetSequence,
                CatchUpAppliedChanges = m_catchUpAppliedChanges,
                CatchUpMessages = m_catchUpMessages,
                CatchUpActive = CatchUpActive,
                CatchUpCompleted = m_catchUpCompleted,
                CellChangeBatchesWithoutSequence = m_cellChangeBatchesWithoutSequence,
                CellChangeSequenceMalformed = m_cellChangeSequenceMalformed,
                // 有序应用 / 缺口检测
                LastDeliveryIndex = m_lastDeliveryIndex,
                DeliveryGaps = m_deliveryGaps,
                OutOfOrderChanges = m_outOfOrderChanges,
                HeldLiveChanges = m_heldChanges.Count,
                HeldLiveChangesDropped = m_heldLiveDropped,
                CellChangesDroppedNotReady = m_cellChangesDroppedNotReady,
                CellChangesSkippedNotHeld = m_cellChangesSkippedNotHeld,
                // 内存区块（窗口外）的强制核对
                MemoryVerificationsSent = m_memoryVerifySent,
                MemoryVerificationsDeferred = m_memoryVerifyDeferred,
                MemoryVerificationsDroppedUnloaded = m_memoryVerifyDroppedUnloaded,
                MemoryVerificationsSkipped = m_memoryVerifySkipped,
                QueuedMemoryVerifications = m_memoryVerifyQueued.Count,
                MemoryVerificationsThisTick = m_memoryVerifyThisTick,
                ResyncDroppedSuperseded = m_resyncDroppedSuperseded,
                ResyncDiscardedSuperseded = m_resyncDroppedSuperseded,
                ResyncRefreshedAfterDiscard = m_resyncRefreshedAfterDiscard,
                ResyncDeferredNotReady = m_resyncDeferredNotReady,
                ResyncEmptySnapshots = m_resyncEmptySnapshots,
                MismatchRetryExhausted = m_mismatchRetryExhaustedCount,
                MismatchRetrySuppressedVerifications = m_mismatchRetrySuppressedVerifications,
                MismatchRetryChunks = m_mismatchRetries.Count,
                MaxMismatchRetryCount = MaxMismatchRetry(),
                ConvergedChunks = m_convergedChunks,
                DiscardRefreshDeferred = m_discardRefreshDeferred,
                DiscardRefreshSkipped = m_discardRefreshSkipped,
                ReportHashesRefreshed = m_reportHashesRefreshed,
                NotReadyRetriesDeferred = m_notReadyRetriesDeferred,
                VerifyRequestsReceived = m_verifyRequestsReceived,
                VerifyRequestChunks = m_verifyRequestChunks,
                VerifyRequestMalformed = m_verifyRequestMalformed,
                // (重)连后重新排队常驻区块
                ResidentRequeues = m_residentRequeues,
                ResidentRequeuedChunks = m_residentRequeuedChunks,
                LastResidentRequeueCount = m_lastResidentRequeueCount,
                ResidentRequeueSkipped = m_residentRequeueSkipped,
                ResidentRequeueDeferred = m_residentRequeueDeferred,
                LastResidentRequeueReason = m_lastResidentRequeueReason,
                // 现场计数（每秒）
                ReportsPerSecond = m_reportsPerSecond,
                ReportsPerSecondPeak = m_reportsPerSecondPeak,
                MatchPerSecond = m_matchPerSecond,
                MismatchPerSecond = m_mismatchPerSecond,
                ResyncReceivedPerSecond = m_resyncReceivedPerSecond,
                ResyncAppliedPerSecond = m_resyncAppliedPerSecond,
                MismatchChunkCount = MismatchChunkCount(),
                MismatchRepeatChunkCount = MismatchRepeatChunkCount(),
                VerifyNotStarted = m_verifyNotStarted,
                WorldReplaceClearedWork = m_worldReplaceClearedWork
            };
            foreach (ChunkKey key in m_world.LoadedChunks)
            {
                snapshot.Loaded.Add(key);
                switch (m_verifier.StateOf(key))
                {
                    case ChunkFreshness.Fresh: snapshot.Fresh.Add(key); break;
                    case ChunkFreshness.Stale: snapshot.Stale.Add(key); break;
                    default: snapshot.Unverified.Add(key); break;
                }
            }
            return snapshot;
        }

        /// <summary>消息层明确丢弃的"载荷解析失败"帧数（按协议层计数；领域校验失败另有各自的 Malformed 计数）。</summary>
        public long MalformedMessages => m_messages.Registry.MalformedMessages;

        public ChunkFreshness StateOf(ChunkKey key) => m_verifier.StateOf(key);
        public uint LocalHash(ChunkKey key) => m_localHashes.TryGetValue(key, out uint h) ? h : 0u;
    }
}

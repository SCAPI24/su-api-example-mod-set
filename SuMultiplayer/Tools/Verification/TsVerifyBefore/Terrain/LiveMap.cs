using System;
using System.Collections.Generic;
using SuMultiplayer.Net;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 一个 chunk 的**俯视地表数据**（实时地图用）：256 列（索引 = x + 16 * z）里，
    /// 每列只取"最上面的非空气方块"和它的绝对高度。
    ///
    /// 为什么是"地表"而不是整块体素：网页要的是一张 2D 地图（角色在正中间），
    /// 整块 16×16×256 传不动也没必要；地表 + 高度已经能画出地形形状。
    ///
    /// 演示页标记：MAP01。
    /// </summary>
    public sealed class LiveMapChunk
    {
        public const int Columns = 256;

        /// <summary>chunk 坐标。</summary>
        public int X;
        public int Z;

        /// <summary>每列最上面的非空气方块 contents（0 = 这一列没有数据/整列空气）。</summary>
        public readonly ushort[] Contents = new ushort[Columns];

        /// <summary>每列那个方块的绝对 Y（contents == 0 时无意义）。</summary>
        public readonly byte[] Top = new byte[Columns];

        /// <summary>
        /// 该块相对被观察玩家端的校验状态（见 <see cref="ChunkVerifyState"/>）。
        /// 0 = 该块不在被观察玩家端的**上报集合**里（客户端没上报过）→ 网页不画框。
        /// </summary>
        public byte State;

        /// <summary>
        /// 该块还有几个**在线玩家端没拿到最新内容**（主机侧内存计数，v5 追加字段）：
        /// 0 = 全部在线玩家端都已核对到最新（网页**不画**数字）；主机卸载过这一块也不清（值跟着区块走）。
        /// 只存在内存里、绝不落盘；会话结束整体清空。
        /// </summary>
        public int PendingClients;

        /// <summary>整块一列方块都没有（这种块不发给网页：画出来也是空的）。</summary>
        public bool IsEmpty()
        {
            for (int i = 0; i < Columns; i++)
            {
                if (Contents[i] != 0) return false;
            }
            return true;
        }
    }

    /// <summary>可以当作地图中心的一个"角色"（主机自己 + 每个已连玩家端的兴趣中心）。</summary>
    public sealed class LiveMapCenter
    {
        /// <summary>主机自己的角色用 -1（它不是网络客户端）。</summary>
        public int ClientId = -1;
        public string Name = string.Empty;
        public bool IsHost;
        public int ChunkX;
        public int ChunkZ;

        /// <summary>
        /// 该角色在世界里的**真实绝对坐标**（主机 = 本机玩家；玩家端 = 主机侧那个联机角色实体的
        /// <c>ComponentBody.Position</c>）。<c>false</c> = 这一格读不到（角色实体还没建好等），
        /// 此时 <see cref="CellReason"/> 写明原因，消费方必须**明说坐标未知**，绝不按区块中心猜。
        /// </summary>
        public bool HasCell;
        public int CellX;
        public int CellY;
        public int CellZ;
        public string CellReason = string.Empty;
    }

    /// <summary>
    /// 一个玩家端**上报过摘要**（<c>ChunkDigestReport</c>）的区块集合的头部信息。
    /// 集合本身走 <see cref="LiveMapClientChunk"/> 的扁平条目（随批分发，单帧装不下时不会被截断）。
    /// </summary>
    public sealed class LiveMapClient
    {
        /// <summary>玩家端在本机通道里的编号（**只用于本机索引**；主机自己的角色不在这个表里）。</summary>
        public int ClientId;
        public string Name = string.Empty;

        /// <summary>主机侧记下的该玩家端上报过摘要的区块数（= 实际发出的条目数）。</summary>
        public int ReportedChunkCount;
    }

    /// <summary>一条"某个玩家端已加载（上报过摘要）的区块"（只带坐标，不带地形数据）。</summary>
    public sealed class LiveMapClientChunk
    {
        public int ClientId;
        public int X;
        public int Z;
    }

    /// <summary>
    /// 逐 chunk 的**客户端校验状态**（网页着色框用）。
    /// 由主机按"被观察的那个玩家端"的记账得出：**只有该玩家端上报过摘要（<c>ChunkDigestReport</c>）的块**
    /// 才有状态（见 <see cref="TerrainSyncHost.VerifyStateOf"/>），其余块一律 <see cref="None"/>（s=0，不画框）。
    /// 页面只对 1..4 画框，所以"客户端没上报的块"不会在图上留下任何框。
    /// </summary>
    public enum ChunkVerifyState : byte
    {
        /// <summary>客户端**没有上报过**这块（或还没有被观察的玩家端）：不画框（s=0）。</summary>
        None = 0,

        /// <summary>客户端**上报过摘要**、主机还没裁决（等主机把内容加载出来 / 等下一拍补发）—— 黄框。</summary>
        Requested = 1,

        /// <summary>摘要核对通过 —— 绿框。</summary>
        Verified = 2,

        /// <summary>摘要核对不一致（主机已回传）—— 红框。</summary>
        Failed = 3,

        /// <summary>回传后二次核对通过 —— 蓝框。</summary>
        ReVerified = 4
    }

    /// <summary>只画角色周围窗口，还是把主机**整片已加载区域**都发过来。</summary>
    public enum LiveMapView : byte
    {
        Window = 0,
        All = 1
    }

    /// <summary>一次实时地图快照：中心 + 真正已加载的 chunk（未加载的**不放进来**）。</summary>
    public sealed class LiveMapSnapshot
    {
        /// <summary>是否知道主机角色的绝对坐标（不知道就只能按 chunk 中心居中）。</summary>
        public bool HasViewerCell;
        public int CellX;
        public int CellY;
        public int CellZ;

        /// <summary>窗口模式的半径（chunk 数）；整片模式为 0。</summary>
        public int ChunkRadius;

        public LiveMapView View = LiveMapView.Window;

        /// <summary>被观察的玩家端（逐 chunk 校验状态来自它）；没有玩家端时为 -1。</summary>
        public int WatchedClientId = -1;
        public string WatchedClientName = string.Empty;

        public readonly List<LiveMapCenter> Centers = new List<LiveMapCenter>();
        public readonly List<LiveMapChunk> Chunks = new List<LiveMapChunk>();

        /// <summary>每个玩家端**已加载**（上报过摘要）的区块集合的头部（名字 + 报告条数）。</summary>
        public readonly List<LiveMapClient> Clients = new List<LiveMapClient>();

        /// <summary>玩家端已加载区块的扁平条目（clientId + 区块坐标，跨批合并后条数必须等于头部计数）。</summary>
        public readonly List<LiveMapClientChunk> ClientChunks = new List<LiveMapClientChunk>();
    }

    /// <summary>一批（一个 MapState 帧）解码结果：头部字段 + 这一批的 chunk。</summary>
    public sealed class LiveMapBatch
    {
        public int SnapshotId;
        public int BatchIndex;
        public int BatchCount;
        public LiveMapSnapshot Header;
        public readonly List<LiveMapChunk> Chunks = new List<LiveMapChunk>();

        /// <summary>这一批携带的玩家端已加载区块条目（按批分发，合并后校验头部计数）。</summary>
        public readonly List<LiveMapClientChunk> ClientChunks = new List<LiveMapClientChunk>();
    }

    /// <summary>
    /// MapRequest / MapState 的编解码。
    ///
    /// 整片已加载区域可能有 200+ 块，一块 776 B ⇒ 一帧塞不下（帧上限 60 KiB），
    /// 所以 MapState **按批发送**：同一个 <c>snapshotId</c> 的多帧才算一次完整快照，
    /// 收齐了才发布（工具端负责合并）。
    ///
    /// 载荷版本（<see cref="Version"/>）改动记录 —— **只追加字段，绝不重排已有字段**：
    ///   · v1..v3：中心清单（id / 名字 / 是否主机 / 区块）+ 主机地形块。
    ///   · v4：中心追加"真实绝对坐标"（hasCell + cellX/Y/Z）；新增玩家端**已加载区块**
    ///     头部清单（id / 名字 / 上报条数）与扁平条目（随批分发，见 <see cref="MaxClientChunksPerBatch"/>）。
    ///     旧工具读到 v4 会在版本字节上直接失败（不会把新字段当旧字段解析）。
    ///   · v5（当前）：每个 chunk 记录**末尾**追加"该块有几个在线玩家端还没拿到最新内容"
    ///     （<see cref="LiveMapChunk.PendingClients"/>，int32；0 = 网页不画数字）。旧的 v1..v4 消费者读到
    ///     v5 会在**版本字节**上直接失败（载荷首字节就是版本，不做向后兼容解析）。
    /// </summary>
    public static class LiveMapCodec
    {
        /// <summary>窗口模式半径上限：49 块 × 776 B ≈ 38 KiB，仍低于 60 KiB 帧上限。</summary>
        public const int MaxRadius = 3;

        /// <summary>每批最多几块：16 × 776 B ≈ 12.4 KiB，留足余量。</summary>
        public const int MaxChunksPerBatch = 16;

        /// <summary>每批最多几条玩家端已加载区块条目：256 × 12 B = 3 KiB。</summary>
        public const int MaxClientChunksPerBatch = 256;

        /// <summary>玩家端已加载区块条目一次快照最多带多少条（超过 = 明确失败，绝不截断后假装完整）。</summary>
        public const int MaxClientChunksTotal = 65536;

        /// <summary>中心 / 玩家端清单的条数上限（线上是 1 字节计数）。</summary>
        public const int MaxListCount = 255;

        /// <summary>当前载荷版本。消费方必须**精确匹配**，不匹配就失败（不做向后兼容解析）。</summary>
        public const int Version = 5;

        public static byte[] EncodeRequest(int radius, bool all, bool hasCenter, int centerChunkX,
            int centerChunkZ)
        {
            var writer = new PayloadWriter();
            writer.Byte((byte)ClampRadius(radius));
            writer.Byte((byte)(all ? 1 : 0));
            writer.Byte((byte)(hasCenter ? 1 : 0));
            writer.Int32(centerChunkX);
            writer.Int32(centerChunkZ);
            return writer.ToArray();
        }

        public static void DecodeRequest(byte[] payload, out int radius, out bool all, out bool hasCenter,
            out int centerChunkX, out int centerChunkZ)
        {
            radius = 2;
            all = false;
            hasCenter = false;
            centerChunkX = 0;
            centerChunkZ = 0;
            if (payload == null || payload.Length == 0) return;
            var reader = new PayloadReader(payload);
            radius = ClampRadius(reader.Byte());
            if (reader.Remaining >= 1) all = reader.Byte() != 0;
            if (reader.Remaining >= 9)
            {
                hasCenter = reader.Byte() != 0;
                centerChunkX = reader.Int32();
                centerChunkZ = reader.Int32();
            }
        }

        public static int ClampRadius(int radius) => Math.Max(0, Math.Min(MaxRadius, radius));

        /// <summary>把整份快照切成若干帧载荷（同一个 snapshotId）。</summary>
        public static List<byte[]> EncodeBatches(LiveMapSnapshot snapshot, int snapshotId)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            List<LiveMapChunk> chunks = snapshot.Chunks;
            List<LiveMapClientChunk> clientChunks = snapshot.ClientChunks;
            if (clientChunks.Count > MaxClientChunksTotal)
            {
                // 明确失败：绝不截断后假装"客户端已加载区域"是完整的。
                throw new ArgumentException("玩家端已加载区块条目 " + clientChunks.Count +
                    " 条超过上限 " + MaxClientChunksTotal + "（不截断：请减少在线玩家端或缩小窗口）");
            }
            int chunkBatches = Math.Max(1, (chunks.Count + MaxChunksPerBatch - 1) / MaxChunksPerBatch);
            int clientBatches = (clientChunks.Count + MaxClientChunksPerBatch - 1) / MaxClientChunksPerBatch;
            int batchCount = Math.Max(chunkBatches, Math.Max(1, clientBatches));
            var result = new List<byte[]>(batchCount);
            for (int b = 0; b < batchCount; b++)
            {
                int from = b * MaxChunksPerBatch;
                int count = Math.Min(MaxChunksPerBatch, chunks.Count - from);
                if (count < 0) count = 0;
                int clientFrom = b * MaxClientChunksPerBatch;
                int clientCount = Math.Min(MaxClientChunksPerBatch, clientChunks.Count - clientFrom);
                if (clientCount < 0) clientCount = 0;
                result.Add(EncodeBatch(snapshot, snapshotId, b, batchCount, chunks, from, count,
                    clientChunks, clientFrom, clientCount));
            }
            return result;
        }

        private static byte[] EncodeBatch(LiveMapSnapshot snapshot, int snapshotId, int batchIndex,
            int batchCount, List<LiveMapChunk> chunks, int from, int count,
            List<LiveMapClientChunk> clientChunks, int clientFrom, int clientCount)
        {
            var writer = new PayloadWriter();
            writer.Byte(Version);
            writer.Int32(snapshotId);
            writer.Int32(batchIndex);
            writer.Int32(batchCount);
            writer.Byte((byte)(snapshot.HasViewerCell ? 1 : 0));
            writer.Int32(snapshot.CellX);
            writer.Int32(snapshot.CellY);
            writer.Int32(snapshot.CellZ);
            writer.Byte((byte)(snapshot.View == LiveMapView.All ? 0 : ClampRadius(snapshot.ChunkRadius)));
            writer.Byte((byte)snapshot.View);
            writer.Int32(snapshot.WatchedClientId);
            writer.Text(snapshot.WatchedClientName ?? string.Empty);
            writer.Byte((byte)Math.Min(snapshot.Centers.Count, MaxListCount));
            for (int i = 0; i < snapshot.Centers.Count && i < MaxListCount; i++)
            {
                LiveMapCenter center = snapshot.Centers[i];
                writer.Int32(center.ClientId);
                writer.Byte((byte)(center.IsHost ? 1 : 0));
                writer.Int32(center.ChunkX);
                writer.Int32(center.ChunkZ);
                writer.Text(center.Name);
                // v4 追加：真实绝对坐标（读不到时 hasCell=0 + 原因文本，消费方据此明说"坐标未知"）
                writer.Byte((byte)(center.HasCell ? 1 : 0));
                writer.Int32(center.CellX);
                writer.Int32(center.CellY);
                writer.Int32(center.CellZ);
                writer.Text(center.CellReason ?? string.Empty);
            }
            // v4 追加：玩家端已加载区块的头部清单（每批都写：头部很小，任何一批都能自描述）
            writer.Byte((byte)Math.Min(snapshot.Clients.Count, MaxListCount));
            for (int i = 0; i < snapshot.Clients.Count && i < MaxListCount; i++)
            {
                LiveMapClient client = snapshot.Clients[i];
                writer.Int32(client.ClientId);
                writer.Text(client.Name ?? string.Empty);
                writer.Int32(client.ReportedChunkCount);
            }
            writer.Int32(count);
            for (int i = 0; i < count; i++)
            {
                LiveMapChunk chunk = chunks[from + i];
                writer.Int32(chunk.X);
                writer.Int32(chunk.Z);
                writer.Byte(chunk.State);
                for (int c = 0; c < LiveMapChunk.Columns; c++) writer.UInt16(chunk.Contents[c]);
                for (int c = 0; c < LiveMapChunk.Columns; c++) writer.Byte(chunk.Top[c]);
                // v5 追加（chunk 记录末尾，v4 前缀逐字节不变）：该块欠更新的在线玩家端数（0 = 不画数字）
                writer.Int32(chunk.PendingClients);
            }
            // v4 追加：这一批携带的玩家端已加载区块条目
            writer.Int32(clientCount);
            for (int i = 0; i < clientCount; i++)
            {
                LiveMapClientChunk entry = clientChunks[clientFrom + i];
                writer.Int32(entry.ClientId);
                writer.Int32(entry.X);
                writer.Int32(entry.Z);
            }
            return writer.ToArray();
        }

        public static bool TryDecodeBatch(byte[] payload, out LiveMapBatch batch)
        {
            batch = null;
            if (payload == null || payload.Length < 24) return false;
            try
            {
                var reader = new PayloadReader(payload);
                if (reader.Byte() != Version) return false;
                var result = new LiveMapBatch
                {
                    SnapshotId = reader.Int32(),
                    BatchIndex = reader.Int32(),
                    BatchCount = reader.Int32()
                };
                var header = new LiveMapSnapshot
                {
                    HasViewerCell = reader.Byte() != 0,
                    CellX = reader.Int32(),
                    CellY = reader.Int32(),
                    CellZ = reader.Int32(),
                    ChunkRadius = reader.Byte(),
                    View = (LiveMapView)reader.Byte(),
                    WatchedClientId = reader.Int32(),
                    WatchedClientName = reader.Text()
                };
                int centerCount = reader.Byte();
                for (int i = 0; i < centerCount; i++)
                {
                    header.Centers.Add(new LiveMapCenter
                    {
                        ClientId = reader.Int32(),
                        IsHost = reader.Byte() != 0,
                        ChunkX = reader.Int32(),
                        ChunkZ = reader.Int32(),
                        Name = reader.Text(),
                        HasCell = reader.Byte() != 0,
                        CellX = reader.Int32(),
                        CellY = reader.Int32(),
                        CellZ = reader.Int32(),
                        CellReason = reader.Text()
                    });
                }
                int clientCount = reader.Byte();
                for (int i = 0; i < clientCount; i++)
                {
                    header.Clients.Add(new LiveMapClient
                    {
                        ClientId = reader.Int32(),
                        Name = reader.Text(),
                        ReportedChunkCount = reader.Int32()
                    });
                }
                int chunkCount = reader.Int32();
                if (chunkCount < 0 || chunkCount > 4096) return false;
                for (int i = 0; i < chunkCount; i++)
                {
                    var chunk = new LiveMapChunk { X = reader.Int32(), Z = reader.Int32() };
                    chunk.State = reader.Byte();
                    for (int c = 0; c < LiveMapChunk.Columns; c++) chunk.Contents[c] = reader.UInt16();
                    for (int c = 0; c < LiveMapChunk.Columns; c++) chunk.Top[c] = reader.Byte();
                    chunk.PendingClients = reader.Int32();       // v5 追加（末尾）
                    result.Chunks.Add(chunk);
                }
                int clientChunkCount = reader.Int32();
                if (clientChunkCount < 0 || clientChunkCount > MaxClientChunksPerBatch) return false;
                for (int i = 0; i < clientChunkCount; i++)
                {
                    result.ClientChunks.Add(new LiveMapClientChunk
                    {
                        ClientId = reader.Int32(),
                        X = reader.Int32(),
                        Z = reader.Int32()
                    });
                }
                if (!reader.AtEnd) return false;        // 尾部多出字节 = 线格式不匹配：明确失败
                result.Header = header;
                batch = result;
                return true;
            }
            catch (Exception)
            {
                batch = null;
                return false;
            }
        }
    }
}

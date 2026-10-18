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

    /// <summary>
    /// 实时地图上的一只**主机端**动物（v9）：只有位置，**没有名字 / 网络 id / 生命值**。
    ///
    /// <see cref="Key"/> 只用于主机侧**稳定排序**（同一份世界状态下每次快照的顺序一致），
    /// **绝不上线**：wire 只写 x / y / z 三个 float。客户端不贡献任何条目。
    /// </summary>
    public sealed class LiveMapAnimal
    {
        /// <summary>排序键（主机网络键；引擎只读扫描用实体的稳定身份哈希）。仅排序用，不上线。</summary>
        public int Key;

        public float X;
        public float Y;
        public float Z;
    }

    /// <summary>可以当作地图中心的一个"角色"（主机自己 + 每个已连玩家端的兴趣中心）。</summary>
    public sealed class LiveMapCenter
    {
        /// <summary>主机自己的角色用 -1（它不是网络客户端）。</summary>
        public int ClientId = -1;

        /// <summary>
        /// 角色的**稳定键**（占位/选角用它，重连不变）：主机 = <see cref="LiveMapCodec.HostKey"/>，
        /// 玩家端 = 该身份的稳定键（<see cref="PlayerSessionKeys.StableKeyOf"/> 派生，**不是 UserId 全值**）。
        /// 页面「居中角色」按它选，所以同一身份重连（clientId 变了）不会多出一个条目、也不会把选择断掉。
        /// </summary>
        public string StableKey = string.Empty;

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
    /// 一个玩家端**当前存在于内存**的区块集合的头部信息（v8 起实时地图客户端线框的唯一来源）。
    /// 集合本身走 <see cref="LiveMapClientChunk"/> 的扁平条目（随批分发，单帧装不下时不会被截断）。
    /// **按身份归档**：同一身份（重连前后）只有一个条目。
    /// </summary>
    public sealed class LiveMapClient
    {
        /// <summary>玩家端**当前**的传输句柄（clientId；重连会变 —— 归档/选角一律用 <see cref="StableKey"/>）。</summary>
        public int ClientId;

        /// <summary>
        /// 该玩家端身份的稳定键（重连不变；**不是 UserId 全值**）。页面「居中角色」与
        /// 客户端区块集合都按它匹配，所以重连不会出现第二个条目。
        /// </summary>
        public string StableKey = string.Empty;

        public string Name = string.Empty;

        /// <summary>
        /// 该身份**上报过摘要**的区块数（会话累计、只增不减；只用于说明"历史上报过多少"，
        /// **不是**实时地图客户端线框的条目数）。
        /// </summary>
        public int ReportedChunkCount;

        /// <summary>
        /// 该身份**当前存在于客户端内存**的区块数（v8 追加；= 本快照 <see cref="LiveMapClientChunk"/>
        /// 里该身份的条目数，收齐时两者必须相等）。客户端卸载区块后这个数会**变小**
        /// —— 实时地图因此只画当前还在内存里的方块。
        /// </summary>
        public int CurrentLoadedChunkCount;
    }

    /// <summary>
    /// 一条"某个玩家端**当前内存里**的区块"（只带坐标与验证状态，不带地形数据）。
    /// v8 起这份条目来自客户端的当前常驻集合（整集 + 加载/卸载增量），不是会话累计的上报集合。
    /// </summary>
    public sealed class LiveMapClientChunk
    {
        /// <summary>当前传输句柄（clientId；仅作参考/兼容，合并与选角按 <see cref="StableKey"/>）。</summary>
        public int ClientId;
        /// <summary>身份稳定键（与 <see cref="LiveMapClient.StableKey"/>、<see cref="LiveMapCenter.StableKey"/> 同一个键）。</summary>
        public string StableKey = string.Empty;
        public int X;
        public int Z;

        /// <summary>
        /// 该块相对**这个身份自己**的校验状态（v7 追加；<see cref="ChunkVerifyState"/>）。
        /// 它来自该身份会话记录的 <c>Verdicts</c>（见 <see cref="TerrainSyncHost.VerifyStateOf(string, ChunkKey)"/>），
        /// **不是**"被观察客户端"的状态：每个玩家端的线框必须按它自己的裁决着色。
        /// 0 = 这块虽然**当前在内存**、但该身份还没有上报摘要 / 还没有裁决（刚加载的头几帧），
        /// 网页对 0 不画线框（不做状态猜测）。
        /// </summary>
        public byte State;
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

        /// <summary>被观察身份的**当前传输句柄**（逐 chunk 校验状态来自它）；没有玩家端时为 -1。</summary>
        public int WatchedClientId = -1;
        public string WatchedClientName = string.Empty;
        /// <summary>被观察身份的**稳定键**（重连不变；v6 追加；空 = 没有玩家端 / 旧主机）。</summary>
        public string WatchedStableKey = string.Empty;

        public readonly List<LiveMapCenter> Centers = new List<LiveMapCenter>();
        public readonly List<LiveMapChunk> Chunks = new List<LiveMapChunk>();

        /// <summary>每个玩家端**当前内存**的区块集合的头部（名字 + 历史上报条数 + 当前条数）。</summary>
        public readonly List<LiveMapClient> Clients = new List<LiveMapClient>();

        /// <summary>
        /// 玩家端**当前内存**区块的扁平条目（clientId + 区块坐标 + 该身份的校验状态）。
        /// 跨批合并后条数必须等于头部 <see cref="LiveMapClient.CurrentLoadedChunkCount"/>。
        /// </summary>
        public readonly List<LiveMapClientChunk> ClientChunks = new List<LiveMapClientChunk>();

        /// <summary>
        /// 主机端动物的**位置**清单（v9；客户端不贡献）。按 <see cref="LiveMapAnimal.Key"/> 升序，
        /// 同一世界状态下每次快照顺序稳定；条数上限 <see cref="LiveMapCodec.MaxAnimals"/>，
        /// 超出时由 <see cref="LiveMapAnimalFeed.Fill"/> 截断并上报 dropped（调用方必须计数/记日志）。
        /// </summary>
        public readonly List<LiveMapAnimal> Animals = new List<LiveMapAnimal>();
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
    /// v9 动物清单的**唯一装配算法**（纯函数，无引擎 / 无网络，可直接自检）：
    ///   · 按 <see cref="LiveMapAnimal.Key"/> 升序（键相同再按坐标）排序 —— 同一世界状态下顺序稳定；
    ///   · 只取前 <paramref name="max"/> 条，其余计入 <paramref name="dropped"/>
    ///     （调用方必须把 dropped 计数并写日志，绝不静默截断）。
    /// 客户端不经过这里（只有主机的 BuildLiveMap 会调用）。
    /// </summary>
    public static class LiveMapAnimalFeed
    {
        public static int Fill(List<LiveMapAnimal> target, IReadOnlyList<LiveMapAnimal> source, int max,
            out int dropped)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Clear();
            dropped = 0;
            if (source == null || source.Count == 0) return 0;
            var ordered = new List<LiveMapAnimal>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null) ordered.Add(source[i]);
            }
            ordered.Sort((a, b) =>
            {
                if (a.Key != b.Key) return a.Key.CompareTo(b.Key);
                if (a.X != b.X) return a.X.CompareTo(b.X);
                if (a.Z != b.Z) return a.Z.CompareTo(b.Z);
                return a.Y.CompareTo(b.Y);
            });
            int limit = max > 0 ? max : 0;
            int count = Math.Min(ordered.Count, limit);
            for (int i = 0; i < count; i++) target.Add(ordered[i]);
            dropped = ordered.Count - count;
            return count;
        }
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
    ///   · v5：每个 chunk 记录**末尾**追加"该块有几个在线玩家端还没拿到最新内容"
    ///     （<see cref="LiveMapChunk.PendingClients"/>，int32；0 = 网页不画数字）。
    ///   · v6：**每个客户端条目追加身份稳定键**（重连不变，页面「居中角色」按它选）：
    ///       头部 watched 记录在 WatchedClientName 之后追加 WatchedStableKey；
    ///       每条 center 记录在 CellReason 之后追加 StableKey（主机 = "host"）；
    ///       每条玩家端头部记录在 ReportedChunkCount 之后追加 StableKey；
    ///       每条玩家端已加载区块条目在 Z 之后追加 StableKey。
    ///   · v7：**每条玩家端已加载区块条目末尾追加该块相对该身份的校验状态**
    ///       （<see cref="LiveMapClientChunk.State"/>，1 字节，见 <see cref="ChunkVerifyState"/>），
    ///       于是网页能给**每个客户端**的已加载区块画按状态着色的线框（绿/蓝/黄/红）。
    ///       记录顺序：clientId(int32) · X(int32) · Z(int32) · StableKey(text) · State(byte)；
    ///       v7 之前的所有字段逐字节不变，新字段只追加在条目末尾。
    ///   · v8：**客户端线框的数据源从"会话累计的上报集合"改为"客户端当前内存集合"**
    ///       （用户口径："客户端的方框只显示当前存在于客户端内存的，已经脱离内存的格子就取消绘制"）。
    ///       wire 只**追加一个字段**：每条玩家端头部记录在 StableKey 之后追加
    ///       <see cref="LiveMapClient.CurrentLoadedChunkCount"/>（int32）—— 该身份当前内存区块数；
    ///       条目 <see cref="LiveMapSnapshot.ClientChunks"/> 现在逐条对应这份当前集合
    ///       （`[cx,cz,s]` 形状不变，s 仍是该身份自己的校验状态，0 = 还没上报/裁决，网页不画）。
    ///       `ReportedChunkCount`（JSON 的 reported）语义不变（会话累计），两者分开表达。
    ///   · v9（当前）：**每批载荷末尾追加主机端动物的位置清单**
    ///       （<see cref="LiveMapSnapshot.Animals"/>，**仅位置、没有名字 / id / 生命值**；客户端不贡献）。
    ///       wire 只在 `clientChunks` 之后追加：`animalCount`(int32) + 每条 `x` / `y` / `z`（各 1 个
    ///       float32，小端）。清单按 <see cref="LiveMapAnimal.Key"/> 升序稳定排序、条数上限
    ///       <see cref="MaxAnimals"/>；每批都带**完整**清单（与 centers / clients 头部同一做法：
    ///       任何一批都能自描述，工具取任一批的头部即可，不会因为批序不同拼出不同结果）。
    ///       v8 及以前的所有字段逐字节不变；旧消费者读到 v9 会在**版本字节**上直接失败
    ///       （载荷首字节就是版本，不做向后兼容解析）。
    ///     旧字段顺序逐字节不变，新字段各自追加在所属记录的末尾；旧的 v1..v8 消费者读到
    ///     v9 会在**版本字节**上直接失败（载荷首字节就是版本，不做向后兼容解析）。
    /// </summary>
    public static class LiveMapCodec
    {
        /// <summary>主机自己的稳定键（玩家端用身份稳定键，见 <see cref="PlayerSessionKeys"/>）。</summary>
        public const string HostKey = PlayerSessionKeys.HostKey;

        /// <summary>窗口模式半径上限：49 块 × 776 B ≈ 38 KiB，仍低于 60 KiB 帧上限。</summary>
        public const int MaxRadius = 3;

        /// <summary>每批最多几块：16 × 776 B ≈ 12.4 KiB，留足余量。</summary>
        public const int MaxChunksPerBatch = 16;

        /// <summary>每批最多几条玩家端已加载区块条目：256 × 13 B ≈ 3.3 KiB（v7 起每条多 1 字节状态）。</summary>
        public const int MaxClientChunksPerBatch = 256;

        /// <summary>玩家端已加载区块条目一次快照最多带多少条（超过 = 明确失败，绝不截断后假装完整）。</summary>
        public const int MaxClientChunksTotal = 65536;

        /// <summary>
        /// 实时地图一次快照最多带多少只**主机端动物**（v9）。512 × 12 B ≈ 6 KiB，
        /// 每批一份也远低于 60 KiB 帧上限。超出部分由 <see cref="LiveMapAnimalFeed.Fill"/>
        /// 截断，调用方必须把 dropped 计入 animalsTruncated 并限流写日志（绝不静默丢）。
        /// </summary>
        public const int MaxAnimals = 512;

        /// <summary>中心 / 玩家端清单的条数上限（线上是 1 字节计数）。</summary>
        public const int MaxListCount = 255;

        /// <summary>当前载荷版本。消费方必须**精确匹配**，不匹配就失败（不做向后兼容解析）。</summary>
        public const int Version = 9;

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
            if (snapshot.Animals.Count > MaxAnimals)
            {
                // 明确失败：动物清单的截断必须发生在装配处（带 dropped 计数与日志），
                // codec 不替调用方静默裁剪。
                throw new ArgumentException("实时地图动物条目 " + snapshot.Animals.Count +
                    " 条超过上限 " + MaxAnimals + "（应先经 LiveMapAnimalFeed.Fill 截断并计数，不在这里静默裁剪）");
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
            // v6 追加：被观察身份的稳定键（重连不变；页面据此说明"看的是哪个身份"）
            writer.Text(snapshot.WatchedStableKey ?? string.Empty);
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
                // v6 追加（center 记录末尾）：角色稳定键（主机 = "host"，玩家端 = 身份稳定键）
                writer.Text(center.StableKey ?? string.Empty);
            }
            // v4 追加：玩家端已加载区块的头部清单（每批都写：头部很小，任何一批都能自描述）
            writer.Byte((byte)Math.Min(snapshot.Clients.Count, MaxListCount));
            for (int i = 0; i < snapshot.Clients.Count && i < MaxListCount; i++)
            {
                LiveMapClient client = snapshot.Clients[i];
                writer.Int32(client.ClientId);
                writer.Text(client.Name ?? string.Empty);
                writer.Int32(client.ReportedChunkCount);
                // v6 追加（玩家端头部记录末尾）：身份稳定键（重连不变）
                writer.Text(client.StableKey ?? string.Empty);
                // v8 追加（玩家端头部记录末尾）：该身份**当前内存**区块数（= 这一批 ClientChunks 里
                // 该身份的条目数；客户端卸载后会变小 —— 实时地图据此只画当前还在内存里的块）
                writer.Int32(client.CurrentLoadedChunkCount);
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
                // v6 追加（条目末尾）：身份稳定键（页面按它合并 / 选角，重连不变）
                writer.Text(entry.StableKey ?? string.Empty);
                // v7 追加（条目末尾）：该块相对这个身份自己的校验状态（网页线框按它着色；0 = 无状态）
                writer.Byte(entry.State);
            }
            // v9 追加：主机端动物位置清单（仅 x/y/z；每一批都带完整清单，任何一批都能自描述）。
            writer.Int32(snapshot.Animals.Count);
            for (int i = 0; i < snapshot.Animals.Count; i++)
            {
                LiveMapAnimal animal = snapshot.Animals[i];
                writer.Single(animal.X);
                writer.Single(animal.Y);
                writer.Single(animal.Z);
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
                    WatchedClientName = reader.Text(),
                    WatchedStableKey = reader.Text()            // v6 追加
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
                        CellReason = reader.Text(),
                        StableKey = reader.Text()               // v6 追加（记录末尾）
                    });
                }
                int clientCount = reader.Byte();
                for (int i = 0; i < clientCount; i++)
                {
                    header.Clients.Add(new LiveMapClient
                    {
                        ClientId = reader.Int32(),
                        Name = reader.Text(),
                        ReportedChunkCount = reader.Int32(),
                        StableKey = reader.Text(),               // v6 追加（记录末尾）
                        CurrentLoadedChunkCount = reader.Int32() // v8 追加（记录末尾）
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
                        Z = reader.Int32(),
                        StableKey = reader.Text(),              // v6 追加（记录末尾）
                        State = reader.Byte()                   // v7 追加（记录末尾）
                    });
                }
                // v9 追加：主机端动物位置清单（仅 x/y/z；每批都带完整清单）
                int animalCount = reader.Int32();
                if (animalCount < 0 || animalCount > MaxAnimals) return false;
                for (int i = 0; i < animalCount; i++)
                {
                    header.Animals.Add(new LiveMapAnimal
                    {
                        X = reader.Single(),
                        Y = reader.Single(),
                        Z = reader.Single()
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

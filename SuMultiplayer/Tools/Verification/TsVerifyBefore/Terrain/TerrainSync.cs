using System;
using System.Collections.Generic;
using SuMultiplayer.Net;

namespace SuMultiplayer.Terrain
{
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
        /// <summary>让引擎去加载这一块（异步；完成后实现方自行调用 OnChunkLoaded）。</summary>
        void RequestLoad(ChunkKey key);
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
        public static List<List<byte[]>> Encode(ChunkKey key, ITerrainWorld world, out bool useRle)
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
                var writer = new PayloadWriter();
                writer.Int32(key.X).Int32(key.Z).Int32(p).Int32(partCount).Int32(take);
                for (int i = 0; i < take * RecordBytes; i++) writer.Byte((byte)records[from * RecordBytes + i]);
                parts.Add(writer.ToArray());
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

        public static Part Decode(byte[] payload)
        {
            var reader = new PayloadReader(payload);
            var key = new ChunkKey(reader.Int32(), reader.Int32());
            int index = reader.Int32(), count = reader.Int32(), entries = reader.Int32();
            var edits = new List<CellEdit>(entries * 4);
            for (int i = 0; i < entries; i++)
            {
                int x = reader.Byte(), z = reader.Byte(), startY = reader.Byte(), lenMinus1 = reader.Byte();
                int packed = reader.Byte() | (reader.Byte() << 8) | (reader.Byte() << 16) | (reader.Byte() << 24);
                int len = lenMinus1 + 1;
                for (int k = 0; k < len; k++)
                    edits.Add(CellEdit.FromPacked(x, startY + k, z, packed));
            }
            return new Part(key, index, count, edits);
        }
    }

    public sealed class TerrainSyncOptions
    {
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
        /// 同一块还没加载出来时，重发引擎加载请求的最小间隔（毫秒）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:630-642
        ///         （旧实现待办超过 5s 就重新入队 QueueClientTerrainChunkSync）
        /// </summary>
        public int PendingReloadIntervalMs { get; set; } = 5000;

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
    }

    public sealed class ClientView
    {
        public int Id;
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
        public long CellChangesSent;
        public ChannelState State;

        /// <summary>
        /// 主机手上"欠着"的上报条数（客户端报过、主机还没有内容可比的块）。
        /// 这些块迟早要收到一条带着**真实主机摘要**的 <c>ChunkVerdict</c>（见 TerrainSyncHost.Tick）。
        /// </summary>
        public int PendingReports;

        /// <summary>
        /// 该玩家端**上报过摘要**（<c>ChunkDigestReport</c>）的区块 —— 也就是"它已经加载完的区块"。
        /// 这是实时地图画"客户端已加载区域"的唯一来源：主机没有别的渠道知道客户端加载了什么
        /// （卸载不上报，所以这是**本次会话累计**的上报集合）。
        /// </summary>
        public readonly List<ChunkKey> ReportedLoaded = new List<ChunkKey>();
    }

    public sealed class TerrainHostSnapshot
    {
        public List<ChunkKey> Interest = new List<ChunkKey>();
        public List<ChunkKey> Loaded = new List<ChunkKey>();
        public List<ClientView> Clients = new List<ClientView>();
        public long DigestReports;
        public long ResyncBytes;
        public long CellChangesBroadcast;
        /// <summary>所有玩家端"欠着裁决"的上报条数之和（>0 = 还有报告没被真正回答）。</summary>
        public int PendingReports;
        /// <summary>
        /// 实时地图用：有 ≥1 个在线玩家端"还没拿到最新内容"的区块数（= 网页要标注数字的标块数）。
        /// 只统计内存里的欠更新计数，与主机自己加载/卸载了哪些区块**无关**。
        /// </summary>
        public int PendingMarks;
        /// <summary>
        /// 实时地图用：所有区块"欠更新客户端数"之和（每块有几个在线玩家端欠着就加几）。
        /// 例：3 个区块各欠 1 个客户端 → 本值为 3；1 个区块欠 2 个 → 也是 2。
        /// </summary>
        public int PendingClients;
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
    }

    /// <summary>
    /// 主机侧地形同步：每客户端兴趣窗口（正表 + 反表）、摘要裁决、按订阅者广播改动。
    /// 所有发送都是显式的：队列满 = 背压事件，**不丢包**。
    /// 演示页标记：NET03。
    /// </summary>
    public sealed class TerrainSyncHost
    {
        private sealed class State
        {
            public int Id;
            public string Name = string.Empty;
            /// <summary>对端身份（Hello 末尾追加字段；旧构建 / Debug 只读端没有该字段 = 空串）。</summary>
            public string Identity = string.Empty;
            public ClientKind Kind = ClientKind.Player;
            public ChunkKey Center;
            public int Radius;
            public bool WindowValid;
            public readonly HashSet<ChunkKey> Window = new HashSet<ChunkKey>();
            public readonly Dictionary<ChunkKey, int> PendingParts = new Dictionary<ChunkKey, int>();
            public readonly Queue<List<byte[]>> PendingResync = new Queue<List<byte[]>>();
            /// <summary>逐 chunk 的校验状态（实时地图着色用）：见 ChunkVerifyState。</summary>
            public readonly Dictionary<ChunkKey, byte> Verdicts = new Dictionary<ChunkKey, byte>();
            /// <summary>
            /// 主机**欠着裁决**的上报：客户端报过、主机当时没有内容可比 → key → 待办。
            /// 内容可用后必须用**真实主机摘要**作答（<see cref="TerrainSyncHost.Tick"/> 的轮转核对）。
            /// 与 <see cref="ReportedLoaded"/> 的区别：后者是"上报过的块"（只增不减，实时地图用），
            /// 这里是"还没真正回答的块"（回答后出队）。
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
            /// 该玩家端上报过摘要的区块（= 它加载完的区块），实时地图的"客户端已加载区域"用这一份。
            /// 与 <see cref="Verdicts"/> 的区别：Verdicts 还含窗口里"已请求但还没上报"的块，
            /// 而且会随窗口移动被剪掉；这里是**只在收到 ChunkDigestReport 时增长**的上报集合。
            /// </summary>
            public readonly HashSet<ChunkKey> ReportedLoaded = new HashSet<ChunkKey>();
            /// <summary>
            /// 链路失效后它们在这个客户端上的**欠更新标记已经清过了**（每 tick 只清一次，
            /// 不让一个已经断掉、还留在表里的客户端每帧去扫全表）。
            /// </summary>
            public bool PendingMarksCleared;
            /// <summary>
            /// 这次握手是不是**同一端重连**（Hello 末尾追加字节 = 对端已经有过这个世界）。
            /// 只有它的意义是"别再发整图"；缺字段（旧构建）= false = 照常发。
            /// </summary>
            public bool Rejoin;
            public long Match, Mismatch, NotReady, ResyncPartsSent, CellChangesSent;
        }

        /// <summary>
        /// 主机手上"欠着"的一条上报。
        /// **客户端上报的摘要必须留在这里**：块还没加载出来时没法比对，等它加载出来再比 ——
        /// 旧代码在这一点上把摘要直接丢了，于是那些块永远不会有裁决（203 黄框的根因）。
        /// </summary>
        private sealed class PendingReport
        {
            public ChunkKey Key;
            /// <summary>客户端最近一次上报的摘要（客户端可重报，以最新一次为准）。</summary>
            public uint ClientHash;
            /// <summary>第一次登记的时刻（TickCount64 口径），用于"欠了多久"。</summary>
            public long FirstSeenMs;
            /// <summary>上一次向引擎发加载请求的时刻（限流：不高于 PendingReloadIntervalMs 一次）。</summary>
            public long LastLoadRequestMs;
            /// <summary>已经向引擎发过几次加载请求（写进原因行）。</summary>
            public int LoadRequests;
            /// <summary>"主机一直没拿到这块内容"的明确原因只写一行（同一块不重复刷屏）。</summary>
            public bool StalledLogged;
        }

        private readonly ITerrainWorld m_world;
        private readonly TcpTerrainServer m_server;
        private readonly TerrainSyncOptions m_options;
        private readonly Dictionary<int, State> m_clients = new Dictionary<int, State>();
        private readonly Dictionary<ChunkKey, uint> m_digestCache = new Dictionary<ChunkKey, uint>();
        private readonly Dictionary<ChunkKey, HashSet<int>> m_subscribers = new Dictionary<ChunkKey, HashSet<int>>();
        private readonly object m_gate = new object();

        /// <summary>
        /// 逐区块**欠更新客户端计数**（实时地图格内数字的唯一来源；**只在内存里，绝不落盘**）。
        ///
        /// 语义（用户口径，逐条对应）：
        ///   · 值 = "现在仍连着的玩家端里，有几个还没拿到该区块的最新内容"；0 = 全部都已核对到最新 → 不标注；
        ///   · 主机自己加载/卸载哪一块**不影响**这个计数（<see cref="ITerrainWorld"/> 的加载集合与它无关）；
        ///   · 客户端加入时**不**往已有区块里加它（加入时的整图下载就是最新的基线，它谁都不欠）；
        ///   · 区块有权威改动（<see cref="BroadcastCellChange"/>）→ 把**每个**在线玩家端都记上（改动由主机
        ///     权威下发，主机侧分辨不出"是谁改的"，所以不特殊对待任何一端）；
        ///   · 某玩家端对这块核对通过（Match / 再次核对 ReVerified，见 <see cref="ApplyVerdict"/>）→ 从它的
        ///     计数里移除；
        ///   · 玩家端离开联机（Bye / 链路失效）→ 把它从**所有**区块的集合里去掉（<see cref="RemovePendingClientEverywhere"/>）；
        ///   · 房间/会话结束 → 整体清空（<see cref="ClearPendingMarks"/>）；因为不落盘，下次加入一定是"全图最新"。
        /// 只增/删条目本身，绝不写入任何文件、Project.xml 或世界目录。
        /// </summary>
        private readonly Dictionary<ChunkKey, HashSet<int>> m_pendingClients = new Dictionary<ChunkKey, HashSet<int>>();

        private long m_digestReports, m_resyncBytes, m_cellChanges;

        /// <param name="attachToChannel">
        /// false = 不自挂网络回调，由主线程泵调用 <see cref="ProcessFrame"/>。
        /// 引擎接入必须用 false：算摘要要读引擎地形，只能发生在主线程。
        /// </param>
        public TerrainSyncHost(ITerrainWorld world, TcpTerrainServer server,
            TerrainSyncOptions options = null, bool attachToChannel = true)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_server = server ?? throw new ArgumentNullException(nameof(server));
            m_options = options ?? new TerrainSyncOptions();
            if (attachToChannel) m_server.FrameReceived += OnFrame;
        }

        /// <summary>主线程泵入口：处理一帧入站消息。</summary>
        public void ProcessFrame(in ReceivedFrame frame) => OnFrame(frame);

        /// <summary>
        /// 这个玩家端这次握手是不是**重连**（Hello 里带了"已经有过这个世界"）。
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

        /// <summary>有客户端完成 Hello 握手（加入房间）。参数：clientId, 名字, 身份。</summary>
        public event Action<int, string, string> ClientJoined;

        /// <summary>
        /// 主机欠着一条上报的裁决（内容一直没加载出来 / 客户端断开时还有欠账）—— 调用方写一行日志。
        /// **事件本身已经限流**：同一块最多一条（"内容一直没加载出来"），客户端断开时最多一条（欠账汇总），
        /// 所以调用方照原样写日志即可，不会刷屏。Terrain 层不认识引擎日志，所以走事件。
        /// </summary>
        public event Action<string> ReportUnresolved;

        /// <summary>
        /// **欠更新计数器的生命周期决策**：加标记（区块有改动）/ 移除标记（客户端核对通过）/ 客户端离开时
        /// 清掉它的全部标记 / 会话结束时整体清空 —— 每次**状态真的变了**才抛一条（0→≥1 的加入、真的移除了
        /// 一个标记、真的清了 N 块），因此不是逐格改动的热路径日志；Terrain 层不认识引擎日志，由上层写一行。
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
        /// 本机（主机）身份与名字 —— 由上层注入（Terrain 层不认识 UserId.dat / Game.UserManager），
        /// 用在 HelloAck 的末尾追加字段里回给客户端。
        /// </summary>
        public string Identity { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public TerrainHostSnapshot GetSnapshot()
        {
            var snapshot = new TerrainHostSnapshot
            {
                DigestReports = m_digestReports, ResyncBytes = m_resyncBytes,
                CellChangesBroadcast = m_cellChanges
            };
            lock (m_gate)
            {
                var union = new HashSet<ChunkKey>();
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    State state = pair.Value;
                    foreach (ChunkKey key in state.Window) union.Add(key);
                    var view = new ClientView
                    {
                        Id = state.Id, Name = state.Name, Kind = state.Kind, Center = state.Center,
                        Radius = state.Radius, WindowChunks = state.Window.Count,
                        Tracked = state.PendingParts.Count, PendingParts = state.PendingResync.Count,
                        Match = state.Match, Mismatch = state.Mismatch, NotReady = state.NotReady,
                        ResyncPartsSent = state.ResyncPartsSent, CellChangesSent = state.CellChangesSent,
                        State = m_server.StateOf(state.Id), PendingReports = state.Pending.Count
                    };
                    snapshot.PendingReports += state.Pending.Count;
                    if (state.ReportedLoaded.Count > 0)
                    {
                        view.ReportedLoaded.AddRange(state.ReportedLoaded);
                        // 顺序稳定（实时地图按 x,z 画；同一份数据每次输出同一个顺序，便于比对）
                        view.ReportedLoaded.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
                    }
                    snapshot.Clients.Add(view);
                }
                snapshot.Interest.AddRange(union);
                // 欠更新计数（与主机加载集合无关，所以从这里一并给出，实时地图的状态行也用它）
                snapshot.PendingMarks = m_pendingClients.Count;
                int pendingClients = 0;
                foreach (KeyValuePair<ChunkKey, HashSet<int>> pair in m_pendingClients)
                    pendingClients += pair.Value.Count;
                snapshot.PendingClients = pendingClients;
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
            lock (m_gate)
            {
                foreach (KeyValuePair<int, State> pair in m_clients)
                {
                    State state = pair.Value;
                    if (!state.WindowValid) RefreshWindow(state);
                    // ① 欠着裁决的上报：内容可用 → 真实主机摘要；还没好 → 限流重请 + 一行原因。
                    // 对端已经断了（没发 Bye 就断也一样）：这条线再也答不出去 —— 明确写一行后作废，
                    // 免得对着一根断线每 5s 重请加载（客户端主动 Bye 的那条路在 OnFrame 里）。
                    // 同一时刻把它在**所有区块**上的欠更新标记一并去掉（明确写一行；这正是
                    // "退出联机后它的标记全部消失、再进来又是全 0" 的链路失效分支）。
                    if (!CanStillReachPeer(state))
                    {
                        // 每个 state 只清一次（清完置位）：一个断掉但还留在表里的客户端不会每帧扫全表。
                        if (!state.PendingMarksCleared)
                        {
                            state.PendingMarksCleared = true;
                            string gone = "连接已不可用（" + m_server.StateOf(state.Id) + "）";
                            if (state.Pending.Count > 0) ReportPendingOnDeparture(state, gone);
                            RemovePendingClientEverywhere(state.Id, gone);
                            // 这一端**再也不会回来了**（同一 clientId 不会重连）：把它从客户端表里摘掉，
                            // 否则状态行一直把死连接算成"客户端"（实机：clients=2 里有一个 4 小时前就断了），
                            // 并由上层按 ClientLost 清掉它的联机角色（角色实体会停在最后一个已应用的位置）。
                            if (lost == null) lost = new List<KeyValuePair<int, string>>();
                            lost.Add(new KeyValuePair<int, string>(state.Id, gone));
                        }
                    }
                    work += ResolvePendingReports(state, nowMs);
                    // ② 发送积压的重传分片
                    for (int i = 0; i < m_options.ResyncPartsPerTick && state.PendingResync.Count > 0; i++)
                    {
                        List<byte[]> parts = state.PendingResync.Peek();
                        if (parts.Count == 0) { state.PendingResync.Dequeue(); continue; }
                        if (!m_server.TrySend(state.Id, TerrainMessageType.ChunkResync, parts[0])) break;
                        state.ResyncPartsSent++;
                        m_resyncBytes += parts[0].Length;
                        parts.RemoveAt(0);
                        if (parts.Count == 0) state.PendingResync.Dequeue();
                        work++;
                    }
                }
                if (lost != null)
                {
                    for (int i = 0; i < lost.Count; i++)
                    {
                        m_clients.TryGetValue(lost[i].Key, out State state);
                        if (state != null) ReleaseWindow(state);
                        m_clients.Remove(lost[i].Key);
                    }
                }
            }
            // 出锁再抛：处理方会拆引擎实体（联机角色），不在 Terrain 层的锁里做。
            if (lost != null)
            {
                for (int i = 0; i < lost.Count; i++) ClientLost?.Invoke(lost[i].Key, lost[i].Value);
            }
            return work;
        }

        private void OnFrame(ReceivedFrame frame)
        {
            lock (m_gate)
            {
                if (!m_clients.TryGetValue(frame.ClientId, out State state))
                {
                    state = new State { Id = frame.ClientId, Radius = m_options.DefaultRadius };
                    m_clients[frame.ClientId] = state;
                }
                switch (frame.Type)
                {
                    case TerrainMessageType.Hello: HandleHello(state, frame.Payload); break;
                    case TerrainMessageType.InterestUpdate: HandleInterest(state, frame.Payload); break;
                    case TerrainMessageType.ChunkDigestReport: HandleDigestReport(state, frame.Payload); break;
                    case TerrainMessageType.Bye:
                        // 客户端主动走了：欠着的上报随它一起作废，但必须**明确写一行**（不是静默丢弃）；
                        // 它在所有区块上的欠更新标记也一并去掉（"退出联机 → 它的标记全没了"）。
                        ReportPendingOnDeparture(state, "主动断开（Bye）");
                        RemovePendingClientEverywhere(state.Id, "主动断开（Bye）");
                        ReleaseWindow(state);
                        m_clients.Remove(frame.ClientId);
                        break;
                }
            }
        }

        private void HandleHello(State state, byte[] payload)
        {
            var reader = new PayloadReader(payload);
            state.Kind = (ClientKind)reader.Byte();
            state.Name = reader.Text();
            _ = reader.Byte();
            _ = reader.Byte();
            state.Radius = Clamp(reader.Int32());
            // 追加字段（旧字段顺序不变，只在末尾追加）：对端身份（UserId.dat）。
            // 旧构建 / Debug 只读端不带这一项 → 身份为空串，由上层明确拒绝建角色
            // （绝不用 clientId / PlayerIndex 兜底）。
            state.Identity = reader.Remaining > 0 ? reader.Text() : string.Empty;
            // 再追加一个字节（三次追加，旧字段顺序一个没动）：本端是否**已经有过这个世界**（断线重连）。
            // 缺字段（旧构建）= false = 照常下发整图（语义与之前完全一致）。
            state.Rejoin = reader.Remaining > 0 && reader.Byte() != 0;
            state.WindowValid = false;
            // 新一次握手 = 这一端又回来了：允许"链路失效清理"在它下次断掉时再跑一次（标记本身不补，
            // 因为加入时的整图就是最新基线 —— 用户口径：再进来是全 0）。
            state.PendingMarksCleared = false;
            m_server.SetPeerIdentity(state.Id, state.Kind, state.Name);
            ClientJoined?.Invoke(state.Id,
                string.IsNullOrEmpty(state.Name) ? ("client#" + state.Id) : state.Name,
                state.Identity ?? string.Empty);
            var writer = new PayloadWriter();
            writer.Byte(1).Int32(state.Id).Text("SuMP-World").Byte(0).Int32(20);
            // 追加字段：主机身份 + 主机名字（客户端建"主机角色"必须按身份绑定，不能用 index 兜底）。
            writer.Text(Identity ?? string.Empty).Text(Name ?? string.Empty);
            m_server.TrySend(state.Id, TerrainMessageType.HelloAck, writer.ToArray());
        }

        private void HandleInterest(State state, byte[] payload)
        {
            var reader = new PayloadReader(payload);
            var center = new ChunkKey(reader.Int32(), reader.Int32());
            int radius = Clamp(reader.Int32());
            if (state.WindowValid && state.Center.Equals(center) && state.Radius == radius) return;
            state.Center = center;
            state.Radius = radius;
            RefreshWindow(state);
            PruneVerdicts(state);
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
                    // 客户端请求了这块（还没结果时记黄框；已有结果就不覆盖）
                    if (!state.Verdicts.ContainsKey(key)) state.Verdicts[key] = (byte)ChunkVerifyState.Requested;
                    if (!m_subscribers.TryGetValue(key, out HashSet<int> subs))
                    {
                        subs = new HashSet<int>();
                        m_subscribers[key] = subs;
                    }
                    subs.Add(state.Id);
                }
            }
            state.WindowValid = true;
            PruneVerdicts(state);
        }

        /// <summary>窗口外的块不再保留校验状态（否则地图上会留下过期颜色）。</summary>
        private static void PruneVerdicts(State state)
        {
            if (state.Verdicts.Count == 0) return;
            var stale = new List<ChunkKey>();
            foreach (KeyValuePair<ChunkKey, byte> pair in state.Verdicts)
            {
                if (!state.Window.Contains(pair.Key)) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++) state.Verdicts.Remove(stale[i]);
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
        }

        /// <summary>
        /// 收到客户端的上报（<c>ChunkDigestReport</c>，每块一条 = 坐标 + 客户端摘要）。
        ///
        /// 两条路径：
        ///   · 主机**已经有内容** → 立刻用真实主机摘要裁决（Match / Mismatch）；
        ///   · 主机**还没有内容** → 立刻回一条 NotReady（明确的"还没好"，绝不静默丢掉这次上报），
        ///     同时把**客户端上报的摘要登记成待办**，等块内容可用后由 <see cref="Tick"/> 用真实主机摘要补齐裁决。
        ///     旧代码在这里 `RequestLoad + continue`，摘要被丢掉 ⇒ 那 203 块黄框永远没有裁决。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:499-518
        ///         （旧实现：目标块 State &lt; Valid 时**不 finalize**，把待校验项留着，等块再次可用再重问）
        /// </summary>
        private void HandleDigestReport(State state, byte[] payload)
        {
            long nowMs = Environment.TickCount64;
            var reader = new PayloadReader(payload);
            int count = reader.Int32();
            for (int i = 0; i < count && reader.Remaining >= 12; i++)
            {
                var key = new ChunkKey(reader.Int32(), reader.Int32());
                uint clientHash = reader.UInt32();
                m_digestReports++;
                // "客户端加载完的区块" = 它上报过摘要的区块（实时地图画客户端区域只认这一份）。
                state.ReportedLoaded.Add(key);
                if (!m_world.HasContents(key))
                {
                    state.NotReady++;
                    state.Verdicts[key] = (byte)ChunkVerifyState.Requested;   // 等主机加载后才能核对
                    RememberReport(state, key, clientHash, nowMs, loadRequested: true);   // 摘要留着，别丢
                    m_world.RequestLoad(key);                                 // 按需从存档重载
                    SendVerdict(state, key, DigestVerdict.NotReady, 0u);      // 明确的"还没好"
                    continue;
                }
                ResolveReport(state, key, clientHash, nowMs);
            }
        }

        /// <summary>
        /// 把一条上报登记成"待裁决"（块内容还没有时）。客户端重报同一块 → 只更新摘要，不重复入队。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:572-575
        ///         （旧实现按坐标去重：pending/queued 里已有的块不再排队）
        /// </summary>
        /// <param name="loadRequested">调用方是否已经为这块发过引擎加载请求（决定重请节奏从哪一刻起算）。</param>
        private void RememberReport(State state, ChunkKey key, uint clientHash, long nowMs, bool loadRequested)
        {
            if (state.Pending.TryGetValue(key, out PendingReport existing))
            {
                // 客户端重报（NotReady 后的重试）：以**最新一次**上报的摘要为裁决依据
                existing.ClientHash = clientHash;
                return;
            }
            state.Pending[key] = new PendingReport
            {
                Key = key, ClientHash = clientHash, FirstSeenMs = nowMs,
                LastLoadRequestMs = loadRequested ? nowMs : 0L,
                LoadRequests = loadRequested ? 1 : 0
            };
            state.PendingScan.Enqueue(key);
        }

        /// <summary>
        /// 用**真实主机摘要**裁决一条上报并记账。
        /// 只有 <see cref="SendVerdict"/> 真的发出去了才调用（队列满 = 显式背压 → 留在待办里下一拍再发）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:691-709
        ///         （旧实现下发前一律改读当下的主机地形值，绝不发旧值/猜值）
        /// </summary>
        private void ApplyVerdict(State state, ChunkKey key, DigestVerdict verdict)
        {
            state.Pending.Remove(key);
            if (verdict == DigestVerdict.Match)
            {
                state.Match++;
                // 之前失败过、回传后这次通过 = **二次验证成功**（蓝框），否则就是一次通过（绿框）
                state.Verdicts[key] = state.Verdicts.TryGetValue(key, out byte previous) &&
                                      previous == (byte)ChunkVerifyState.Failed
                    ? (byte)ChunkVerifyState.ReVerified
                    : (byte)ChunkVerifyState.Verified;
                // 它**已经**拿到这一块的最新内容（Match / 再次核对通过）→ 从欠更新计数里移除；
                // 移掉最后一个标记后该块的数字就是 0 → 网页不标注（0 不画）。
                ClearPendingOnVerified(state, key, verdict);
                return;
            }
            state.Mismatch++;
            state.Verdicts[key] = (byte)ChunkVerifyState.Failed;
            List<List<byte[]>> encoded = ChunkPayloadCodec.Encode(key, m_world, out _);
            state.PendingResync.Enqueue(encoded[0]);
        }

        /// <summary>
        /// 主机已有内容 → 现算主机摘要、比对、发送裁决、记账。发送失败（显式背压）时登记成待裁决，
        /// 由下一拍补发 —— 绝不把"没发出去"当成"已裁决"。
        /// </summary>
        private void ResolveReport(State state, ChunkKey key, uint clientHash, long nowMs)
        {
            uint hostHash = HostDigest(key);
            DigestVerdict verdict = CompareReport(clientHash, hostHash);
            if (!SendVerdict(state, key, verdict, hostHash))
            {
                // 发送队列满：这条上报还没被回答，留在待办里，下一拍补真实主机摘要
                RememberReport(state, key, clientHash, nowMs, loadRequested: false);
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
        /// 每 tick 轮转核对"欠着裁决"的上报（有界）：
        ///   · 内容已可用 → 现算真实主机摘要 → 发裁决 → 记账（Match→绿/蓝，Mismatch→红 + 排队回传）；
        ///   · 内容还没有 → 按 <see cref="TerrainSyncOptions.PendingReloadIntervalMs"/> 的节奏重发加载请求，
        ///     并写**一次**明确原因（同一块不重复刷屏），条目留在待办里继续核。
        /// 绝不放弃、绝不假装匹配。
        ///
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:630-642
        ///         （待办超过 5s → 重新入队请求；不因为一次没成就丢掉）
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:652-660
        ///         （等块的 DueTime 到点后重查 chunk.State &gt;= Valid，可用才 QueueClientTerrainChunkSync）
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:670-671
        ///         （只有 chunk.State &gt;= Valid 才真正发请求/下发，块不可用就留在待办里等下一轮）
        /// </summary>
        private int ResolvePendingReports(State state, long nowMs)
        {
            if (state.Pending.Count == 0) return 0;
            int scanned = 0;
            int resolved = 0;
            while (scanned < m_options.PendingReportsPerTick &&
                   resolved < m_options.PendingResolvesPerTick &&
                   state.PendingScan.Count > 0)
            {
                ChunkKey key = state.PendingScan.Dequeue();
                if (!state.Pending.TryGetValue(key, out PendingReport report))
                {
                    // 已经被别的路径（客户端重报且主机已有内容）裁决过：跳过，不再计一次扫描
                    continue;
                }
                scanned++;
                if (!m_world.HasContents(key))
                {
                    if (nowMs - report.LastLoadRequestMs >= m_options.PendingReloadIntervalMs)
                    {
                        report.LastLoadRequestMs = nowMs;
                        report.LoadRequests++;
                        m_world.RequestLoad(key);
                    }
                    ReportStalled(state, report, nowMs);
                    state.PendingScan.Enqueue(key);      // 下一轮继续核（不丢、不放弃）
                    continue;
                }
                uint hostHash = HostDigest(key);
                DigestVerdict verdict = CompareReport(report.ClientHash, hostHash);
                if (!SendVerdict(state, key, verdict, hostHash))
                {
                    // 显式背压（TrySend=false）：没发出去就还欠着，下一轮补发真实主机摘要
                    state.PendingScan.Enqueue(key);
                    continue;
                }
                ApplyVerdict(state, key, verdict);
                resolved++;
            }
            return resolved;
        }

        /// <summary>
        /// 同一块"内容一直没加载出来"只写一行明确原因（含欠了多久、请求过几次、该端的兴趣半径）。
        /// 引擎没有"按需加载任意区块"的公开入口，加载只由地形更新位置驱动
        /// （旧实现 MaintainRemoteTerrainLocations 给每个联机角色挂 SetUpdateLocation；
        ///  本重写版在 Engine/NetworkAvatarSpawner.cs:421-502 搬运）—— 块不在更新范围内就加载不出来，
        /// 这时**如实说明**并继续留在待办里，绝不放弃也绝不假装匹配。
        /// </summary>
        private void ReportStalled(State state, PendingReport report, long nowMs)
        {
            if (report.StalledLogged) return;            // 限流：同一块最多一行
            report.StalledLogged = true;
            ReportUnresolved?.Invoke("主机一直拿不到区块 " + report.Key + " 的内容，客户端 clientId=" +
                state.Id + " 的这条上报还没裁决（已等 " + ((nowMs - report.FirstSeenMs) / 1000) +
                "s · 已请求加载 " + report.LoadRequests + " 次 · 该玩家端兴趣半径 " + state.Radius +
                " 区块）—— 引擎只按地形更新位置异步加载（旧实现 MaintainRemoteTerrainLocations），" +
                "主机侧没有强制加载单块的入口；本端继续按节奏重试，绝不假装匹配");
        }

        /// <summary>
        /// 对端还能不能收到消息：Connected / Backpressured（队列满但连接还在）都还能收；
        /// Failed / Disconnected 不能 —— 这时"欠着的上报"只能作废并写一行原因。
        /// </summary>
        private bool CanStillReachPeer(State state)
        {
            ChannelState peerState = m_server.StateOf(state.Id);
            return peerState == ChannelState.Connected || peerState == ChannelState.Backpressured;
        }

        /// <summary>
        /// 对端已经不在了（Bye / 连接断开）时把"还欠着的上报"明确写一行并作废：
        /// 这些上报随该客户端一起结束（客户端已经不在了，没有对象可答；这是显式作废，不是静默丢弃）。
        /// </summary>
        private void ReportPendingOnDeparture(State state, string why)
        {
            int pending = state.Pending.Count;
            if (pending <= 0) return;
            state.Pending.Clear();
            state.PendingScan.Clear();
            ReportUnresolved?.Invoke("客户端 clientId=" + state.Id + "（" +
                (string.IsNullOrEmpty(state.Name) ? "未命名" : state.Name) + "）" + why + "，还有 " + pending +
                " 块上报没裁决（主机一直没拿到这些块的内容）→ 这些上报随该客户端一起作废");
        }

        /// <summary>
        /// 发送一条裁决。返回 false = 发送队列满（**显式背压**，调用方必须保留这条上报，不能当已裁决）。
        /// </summary>
        private bool SendVerdict(State state, ChunkKey key, DigestVerdict verdict, uint hostHash)
        {
            var writer = new PayloadWriter();
            writer.Int32(1).Int32(key.X).Int32(key.Z).Byte((byte)verdict).UInt32(hostHash);
            return m_server.TrySend(state.Id, TerrainMessageType.ChunkVerdict, writer.ToArray());
        }

        /// <summary>主机侧摘要：按需现算 + 缓存（有改动时失效）。</summary>
        public uint HostDigest(ChunkKey key)
        {
            if (m_digestCache.TryGetValue(key, out uint cached)) return cached;
            uint value = 0u;
            if (m_world.HasContents(key))
            {
                var digest = new ChunkDigest();
                digest.Compute(m_world.GetCellSource(key));
                value = digest.HasValue ? digest.Value : 0u;
            }
            m_digestCache[key] = value;
            return value;
        }

        /// <summary>主机权威改动：只发给窗口内的客户端，并让摘要缓存失效。</summary>
        public int BroadcastCellChange(ChunkKey key, CellEdit edit)
        {
            m_digestCache.Remove(key);
            HashSet<int> subs;
            lock (m_gate)
            {
                // 不管有几个订阅者：这一块的内容已经变了 → 记下"还有哪些在线玩家端没拿到最新"。
                MarkChunkChanged(key, edit);
                if (!m_subscribers.TryGetValue(key, out subs)) return 0;
                subs = new HashSet<int>(subs);
            }
            var writer = new PayloadWriter();
            writer.Int32(1).Int32(key.X).Int32(key.Z).Int32(edit.X).Int32(edit.Y).Int32(edit.Z).Int32(edit.Packed);
            int sent = 0;
            foreach (int clientId in subs)
            {
                if (!m_server.TrySend(clientId, TerrainMessageType.CellChange, writer.ToArray())) continue;
                sent++;
                m_cellChanges++;
                lock (m_gate)
                {
                    if (m_clients.TryGetValue(clientId, out State state)) state.CellChangesSent++;
                }
            }
            return sent;
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

        /// <summary>实时地图用：某块相对被观察玩家端的校验状态（未请求过 = None）。</summary>
        public ChunkVerifyState VerifyStateOf(ChunkKey key)
        {
            lock (m_gate)
            {
                State state = FindWatchedPlayer();
                if (state == null) return ChunkVerifyState.None;
                return state.Verdicts.TryGetValue(key, out byte value)
                    ? (ChunkVerifyState)value
                    : ChunkVerifyState.None;
            }
        }

        /// <summary>实时地图用：被观察玩家端的 id（-1 = 还没有玩家端）。</summary>
        public int WatchedClientId
        {
            get { lock (m_gate) { State state = FindWatchedPlayer(); return state?.Id ?? -1; } }
        }

        /// <summary>实时地图用：被观察玩家端的名字。</summary>
        public string WatchedClientName
        {
            get { lock (m_gate) { State state = FindWatchedPlayer(); return state?.Name ?? string.Empty; } }
        }

        public void InvalidateDigest(ChunkKey key) => m_digestCache.Remove(key);

        // ------------------------------------------------------------ 欠更新计数（实时地图格内数字）

        /// <summary>
        /// 实时地图用：该区块"还有几个在线玩家端没拿到最新内容"（0 = 不标注）。与主机自己是否加载了
        /// 这一块无关：主机离开过这一块，标记照旧留着（用户口径：客户端进来的时候仍要核对是否最新）。
        /// </summary>
        public int PendingClientCount(ChunkKey key)
        {
            lock (m_gate) return m_pendingClients.TryGetValue(key, out HashSet<int> set) ? set.Count : 0;
        }

        /// <summary>
        /// 会话 / 房间结束：整体清空欠更新计数（**只在内存里**，不写文件 / Project.xml / 世界目录），
        /// 并写一行明确日志。返回清掉了几个标块。调用方：模块的 StopChannel（会话结束的唯一入口）。
        /// </summary>
        public int ClearPendingMarks()
        {
            lock (m_gate)
            {
                int marks = m_pendingClients.Count;
                m_pendingClients.Clear();
                // 会话结束是一次性事件（不是热路径）：**无论有没有标记都写一行**，让"清空了"这件事有据可查。
                PendingTrace?.Invoke("会话结束：欠更新计数整体清空（清掉 " + marks +
                    " 个区块的标记；该计数只在内存里，不落盘、不进 Project.xml / 世界目录）");
                return marks;
            }
        }

        /// <summary>
        /// 区块有权威改动 → 把**每个仍连着的玩家端**记成"欠这一块的最新内容"。
        ///
        /// 改动是主机权威下发的（本地地形事件，或客户端交互先落到主机再广播），主机侧**分辨不出"是谁改的"**
        /// —— 所以本协议不特殊对待任何一端：所有在线玩家端都记上（注释即用户要求的口径说明）。
        /// 只在 0 → ≥1 时写一行（同一块的连续改动不重复刷屏）；没有任何在线玩家端时不建空条目（0 不标注）。
        /// 调用方已持有 m_gate。
        /// </summary>
        private void MarkChunkChanged(ChunkKey key, CellEdit edit)
        {
            m_pendingClients.TryGetValue(key, out HashSet<int> set);
            bool wasEmpty = set == null || set.Count == 0;
            int added = 0;
            foreach (KeyValuePair<int, State> pair in m_clients)
            {
                State state = pair.Value;
                if (state.Kind != ClientKind.Player) continue;   // 调试端（实时地图工具）只观察，不算客户端
                if (!CanStillReachPeer(state)) continue;         // 已经不可达：由离开清理负责，不往计数里加
                if (set == null)
                {
                    set = new HashSet<int>();
                    m_pendingClients[key] = set;
                }
                if (set.Add(state.Id)) added++;
            }
            if (set == null || set.Count == 0) return;
            if (!wasEmpty) return;
            PendingTrace?.Invoke("区块 " + key + " 有改动 (" + edit.X + "," + edit.Y + "," + edit.Z +
                ") → 记为 " + set.Count + " 个在线客户端还没拿到最新内容（本次新加 " + added + " 端）");
        }

        /// <summary>
        /// 某玩家端对这块**核对通过**（Match / 再次核对 ReVerified）→ 把它从这块的欠更新计数里移除；
        /// 移掉最后一个标记后计数为 0（网页不画数字）。只有真的移除了才写一行。调用方已持有 m_gate。
        /// </summary>
        private bool ClearPendingOnVerified(State state, ChunkKey key, DigestVerdict verdict)
        {
            if (!m_pendingClients.TryGetValue(key, out HashSet<int> set)) return false;
            if (!set.Remove(state.Id)) return false;
            int left = set.Count;
            if (left == 0) m_pendingClients.Remove(key);
            PendingTrace?.Invoke("客户端 clientId=" + state.Id + "（" + DisplayName(state) + "）核对通过（" +
                verdict + "）区块 " + key + " → 从欠更新计数移除，该区块还剩 " + left + " 个客户端没拿到最新");
            return true;
        }

        /// <summary>
        /// 玩家端离开联机（主动 Bye / 链路失效）→ 把它从**所有**区块的欠更新集合里去掉。
        /// 这就是"退出联机后原本属于它的标记全部消失，再进来时它又是全 0"的实现（加入时不补标记）。
        /// 只有真的移除了标记才写一行（返回移掉的标块数）。调用方已持有 m_gate。
        /// </summary>
        private int RemovePendingClientEverywhere(int clientId, string why)
        {
            if (m_pendingClients.Count == 0) return 0;
            int marks = 0;
            List<ChunkKey> emptied = null;
            foreach (KeyValuePair<ChunkKey, HashSet<int>> pair in m_pendingClients)
            {
                if (!pair.Value.Remove(clientId)) continue;
                marks++;
                if (pair.Value.Count == 0) (emptied ?? (emptied = new List<ChunkKey>())).Add(pair.Key);
            }
            if (emptied != null)
            {
                for (int i = 0; i < emptied.Count; i++) m_pendingClients.Remove(emptied[i]);
            }
            if (marks > 0)
            {
                PendingTrace?.Invoke("客户端 clientId=" + clientId + " " + why + " → 从 " + marks +
                    " 个区块的欠更新计数里移除它的标记（这些区块不再把它算作\"没拿到最新\"）");
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
                if (!m_server.TrySend(targets[i], TerrainMessageType.PlayerPositions, payload)) continue;
                sent++;
            }
            return sent;
        }
    }

    /// <summary>
    /// 客户端侧地形同步：**块加载完算一次摘要 → 上报**；裁决后标记；
    /// 收到重传就落地并**再报一次确认**；加载后的改动走 CellChange。
    /// 演示页标记：NET04。
    /// </summary>
    public sealed class TerrainSyncClient
    {
        private readonly ITerrainWorld m_world;
        private readonly TcpTerrainClient m_channel;
        private readonly TerrainSyncOptions m_options;
        private readonly ChunkLoadVerifier m_verifier = new ChunkLoadVerifier();
        private readonly Dictionary<ChunkKey, uint> m_localHashes = new Dictionary<ChunkKey, uint>();
        private readonly Dictionary<ChunkKey, long> m_reportSentMs = new Dictionary<ChunkKey, long>();
        private readonly Dictionary<ChunkKey, ChunkKey> m_unused = new Dictionary<ChunkKey, ChunkKey>();
        private readonly Dictionary<ChunkKey, int> m_pendingParts = new Dictionary<ChunkKey, int>();
        private readonly Queue<DigestEntry> m_reportQueue = new Queue<DigestEntry>();
        /// <summary>
        /// 收到 NotReady 的块 → 下一次重报的时刻（退避）。本端的保险：不等主机那边的时序。
        /// 重报次数上限由 <see cref="ChunkLoadVerifier.MaxRetries"/> 明确兜住（超限计数，不静默硬撑）。
        /// </summary>
        private readonly Dictionary<ChunkKey, NotReadyRetry> m_notReadyRetries = new Dictionary<ChunkKey, NotReadyRetry>();
        private long m_reportsSent, m_match, m_mismatch, m_resyncApplied, m_resyncVerified, m_cellChanges, m_retries;
        private long m_reReports, m_abandoned;
        private long m_lastVerifyMs, m_maxVerifyMs;

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
            TerrainSyncOptions options = null, bool attachToChannel = true)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_channel = channel ?? throw new ArgumentNullException(nameof(channel));
            m_options = options ?? new TerrainSyncOptions();
            m_verifier.MaxRetries = m_options.MaxRetries;
            if (attachToChannel) m_channel.FrameReceived += OnFrame;
        }

        /// <summary>主线程泵入口：处理一帧入站消息。</summary>
        public void ProcessFrame(in ReceivedFrame frame) => OnFrame(frame);

        public int ViewRadius { get; private set; } = 2;
        public ChunkKey Center { get; private set; }
        public bool Handshaken { get; private set; }

        /// <summary>HelloAck 里收到的主机身份（UserId.dat）。未握手 / 旧构建 = 空串。</summary>
        public string HostIdentity { get; private set; } = string.Empty;

        /// <summary>HelloAck 里收到的主机名字。未握手 / 旧构建 = 空串。</summary>
        public string HostName { get; private set; } = string.Empty;

        /// <summary>
        /// 主机在 HelloAck 里给本端的编号（旧 <c>client.ClientID</c>，早就在这条线上，
        /// 只是此前被丢掉）。位置快照批里"本机玩家自己那一条"就是用它认出来的
        /// （旧 HandleGamePlayerPositionMessage 的 <c>remoteClientId == client.ClientID</c> 分支）。
        /// 未握手 = -1；它**只是传输层编号**，不是身份。
        /// </summary>
        public int LocalClientId { get; private set; } = -1;

        /// <summary>Hello：旧字段（kind, name, 1, 1, radius）+ 末尾追加的身份。保留单参重载给既有调用方。</summary>
        public void SendHello(string name) => SendHello(name, string.Empty, false);

        public void SendHello(string name, string identity) => SendHello(name, identity, false);

        /// <summary>
        /// Hello。参数 <paramref name="alreadyHasWorld"/> = 本端**已经收下并进入过**这个房间的世界
        /// （断线重连时才是 true）：主机据此**不再重发整图**，只恢复同步。
        /// 主机分不清"同一个人的新连接"和"第一次加入"，所以这个事实必须由客户端明说；
        /// 缺字段（旧构建）= false = 照常发整图，语义不变。
        /// </summary>
        public void SendHello(string name, string identity, bool alreadyHasWorld)
        {
            var writer = new PayloadWriter();
            writer.Byte((byte)ClientKind.Player).Text(name).Byte(1).Byte(1).Int32(ViewRadius);
            // 追加字段（旧字段顺序不变）：本机身份（UserId.dat）。空值也照样写，
            // 让主机能区分"字段缺失（旧构建）"与"身份为空（UserId.dat 不可用）"。
            writer.Text(identity ?? string.Empty);
            // 再追加一个字节：本端是否已经有过这个世界（重连）。
            writer.Byte(alreadyHasWorld ? (byte)1 : (byte)0);
            m_channel.TrySend(TerrainMessageType.Hello, writer.ToArray());
        }

        public void SendInterest(ChunkKey center, int radius)
        {
            Center = center;
            ViewRadius = Math.Max(1, Math.Min(m_options.MaxRadius, radius));
            var writer = new PayloadWriter();
            writer.Int32(center.X).Int32(center.Z).Int32(ViewRadius);
            m_channel.TrySend(TerrainMessageType.InterestUpdate, writer.ToArray());
        }

        /// <summary>块加载完 —— 这是唯一算摘要的时刻。</summary>
        public bool OnChunkLoaded(ChunkKey key)
        {
            uint hash = ComputeHash(key);
            m_localHashes[key] = hash;
            if (!m_verifier.TryBeginVerify(key, hash, out DigestEntry entry)) return false;
            m_reportQueue.Enqueue(entry);
            m_reportSentMs[key] = Environment.TickCount64;
            return true;
        }

        /// <summary>卸载：丢掉摘要（重载时会重新算一次）。</summary>
        public void OnChunkUnloaded(ChunkKey key)
        {
            m_verifier.OnChunkUnloaded(key);
            m_localHashes.Remove(key);
            m_reportSentMs.Remove(key);
            m_pendingParts.Remove(key);
            m_notReadyRetries.Remove(key);      // 块没了：重报安排一并作废（重载时会重新上报一次）
        }

        private uint ComputeHash(ChunkKey key)
        {
            if (!m_world.HasContents(key)) return 0u;
            var digest = new ChunkDigest();
            digest.Compute(m_world.GetCellSource(key));
            return digest.HasValue ? digest.Value : 0u;
        }

        public int Tick()
        {
            FlushDueNotReadyRetries();
            int sent = 0;
            while (m_reportQueue.Count > 0 && sent < m_options.DigestBatch)
            {
                int count = Math.Min(m_options.DigestBatch - sent, m_reportQueue.Count);
                var writer = new PayloadWriter();
                writer.Int32(count);
                for (int i = 0; i < count; i++)
                {
                    DigestEntry entry = m_reportQueue.Dequeue();
                    writer.Int32(entry.Key.X).Int32(entry.Key.Z).UInt32(entry.Hash);
                }
                if (!m_channel.TrySend(TerrainMessageType.ChunkDigestReport, writer.ToArray())) break;
                sent += count;
                m_reportsSent++;
            }
            return sent;
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
                (due ?? (due = new List<ChunkKey>())).Add(pair.Key);
            }
            if (due == null) return;
            for (int i = 0; i < due.Count; i++)
            {
                ChunkKey key = due[i];
                m_notReadyRetries.Remove(key);
                uint hash = m_localHashes.TryGetValue(key, out uint h) ? h : 0u;
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
            return m_channel.TrySend(TerrainMessageType.PlayerPosition, payload);
        }

        private void OnFrame(ReceivedFrame frame)
        {
            switch (frame.Type)
            {
                case TerrainMessageType.HelloAck: HandleHelloAck(frame.Payload); break;
                case TerrainMessageType.ChunkVerdict: HandleVerdict(frame.Payload); break;
                case TerrainMessageType.ChunkResync: HandleResync(frame.Payload); break;
                case TerrainMessageType.CellChange: HandleCellChange(frame.Payload); break;
            }
        }

        /// <summary>
        /// HelloAck：旧字段（1, clientId, 房间名, 0, 20）+ 末尾追加的主机身份与名字。
        /// 旧构建不带追加字段 → 两者留空，由上层明确拒绝建主机角色（不拿 index 兜底）。
        /// </summary>
        private void HandleHelloAck(byte[] payload)
        {
            var reader = new PayloadReader(payload);
            _ = reader.Byte();
            // 旧 client.ClientID：位置快照批用它认"本机玩家自己那一条"（旧实现同名字段）。
            LocalClientId = reader.Int32();
            _ = reader.Text();
            _ = reader.Byte();
            _ = reader.Int32();
            HostIdentity = reader.Remaining > 0 ? reader.Text() : string.Empty;
            HostName = reader.Remaining > 0 ? reader.Text() : string.Empty;
            Handshaken = true;
        }

        private void HandleVerdict(byte[] payload)
        {
            var reader = new PayloadReader(payload);
            int count = reader.Int32();
            for (int i = 0; i < count; i++)
            {
                var key = new ChunkKey(reader.Int32(), reader.Int32());
                var verdict = (DigestVerdict)reader.Byte();
                uint hostHash = reader.UInt32();
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
                        m_verifier.OnVerdict(key, new DigestEntry(key, localHash), hostHash);
                        m_match++;
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
                        break;
                }
            }
        }

        private void HandleResync(byte[] payload)
        {
            ChunkPayloadCodec.Part part = ChunkPayloadCodec.Decode(payload);
            if (part.Edits.Count > 0) m_world.ApplyCells(part.Key, part.Edits);
            m_resyncApplied++;
            m_pendingParts.TryGetValue(part.Key, out int remaining);
            remaining = part.Count - part.Index - 1;
            m_pendingParts[part.Key] = remaining;
            if (remaining > 0) return;                       // 等其余分片
            m_pendingParts.Remove(part.Key);
            m_notReadyRetries.Remove(part.Key);             // 内容已覆盖：作废重报安排
            // 覆盖后重算并**再报一次**：确认内容真的对齐（不是先认后收）
            uint hash = ComputeHash(part.Key);
            m_localHashes[part.Key] = hash;
            if (m_verifier.TryBeginVerify(part.Key, hash, out DigestEntry entry))
            {
                m_reportQueue.Enqueue(entry);
                m_reportSentMs[part.Key] = Environment.TickCount64;
                m_resyncVerified++;
            }
        }

        private void HandleCellChange(byte[] payload)
        {
            var reader = new PayloadReader(payload);
            int count = reader.Int32();
            for (int i = 0; i < count; i++)
            {
                var key = new ChunkKey(reader.Int32(), reader.Int32());
                int x = reader.Int32(), y = reader.Int32(), z = reader.Int32();
                int packed = reader.Int32();
                m_world.ApplyCells(key, new[] { CellEdit.FromPacked(x, y, z, packed) });
                m_cellChanges++;
                if (m_world.HasContents(key)) m_localHashes[key] = ComputeHash(key);
            }
        }

        public TerrainClientSnapshot GetSnapshot()
        {
            var snapshot = new TerrainClientSnapshot
            {
                Center = Center, Radius = ViewRadius, ReportsSent = m_reportsSent, Match = m_match,
                Mismatch = m_mismatch, ResyncApplied = m_resyncApplied, ResyncVerified = m_resyncVerified,
                CellChangesApplied = m_cellChanges, Retries = m_retries, LastVerifyMs = m_lastVerifyMs,
                MaxVerifyMs = m_maxVerifyMs, State = m_channel.State, BytesReceived = m_channel.Stats.BytesReceived,
                ReReports = m_reReports, Abandoned = m_abandoned
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

        public ChunkFreshness StateOf(ChunkKey key) => m_verifier.StateOf(key);
        public uint LocalHash(ChunkKey key) => m_localHashes.TryGetValue(key, out uint h) ? h : 0u;
    }
}

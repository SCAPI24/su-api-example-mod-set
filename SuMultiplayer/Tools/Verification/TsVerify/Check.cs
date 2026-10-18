using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Engine;
using SuMultiplayer.Engine;
using SuMultiplayer.Join;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;
using SuMultiplayer.Transfer;

namespace TsVerify
{
    /// <summary>
    /// 地形摘要/裁决链的无设备自检（真实 TCP + 真实 Terrain/TerrainSync.cs 源码）：
    ///   A. 主机缺内容时上报 → 立刻 NotReady(hostHash=0) + 登记待裁决（摘要留着，不丢）
    ///   B. 块在主机侧变为内容可用 → host.Tick() → 收到 Match + **真实主机摘要** → 状态 Verified
    ///   C. 客户端摘要不一致 → Mismatch + 真实主机摘要 + 回传排队 → 状态 Failed
    ///   D. 回传落地后客户端重报 → ReVerified（蓝）
    ///   E. 主机一直没有内容 → 绝不伪造匹配、待裁决不降、明确原因只写一行（限流）、按节奏重请加载
    ///   F. ReportedLoaded 只随上报增长（实时地图的来源不被这次改动影响）
    ///   G. 客户端收到 NotReady 后**退避**重报（不是当场连发），最终仍能拿到真实裁决
    ///   K. **窗口不再刷黄框**：没上报过的窗口块 s=0（不画框）；黄框块数 == 待裁决条数；
    ///      上报过的块 黄→绿，且窗口移动/收缩后结论不丢（实时地图只对上报集合给状态）。
    ///   L. **同步范围跟随客户端声明的可见范围**：内容距离 = radius*16（不再是硬编码 64），
    ///      夹紧口径与主机侧一致；顺带做源码守卫（主机分支的 SetUpdateLocation 不许回退到 64f）。
    ///   · J. **玩家位置快照（客户端 → 主机）**：真实 TCP 上传 20 份 → 每份都被解码+应用（位置/旋转/视线角），
    ///      主机侧身体每拍跟到最新那份；链路失效后死连接上的发送必须**如实失败**（修复前返回 true = 假装发成功）；
    ///      客户端断链后新连接重新握手（alreadyHasWorld 标记）→ 位置快照继续被应用，主机把死连接从表里摘掉
    ///      并抛出 ClientLost（以前不抛，角色实体会永远停在最后一个已应用的位置上）。
    /// </summary>
    internal static class Check
    {
        private static int s_pass, s_fail;
        private static readonly List<string> s_failures = new List<string>();

        private static void Expect(bool ok, string message)
        {
            if (ok) { s_pass++; Console.WriteLine("  \u2713 " + message); return; }
            s_fail++; s_failures.Add(message); Console.WriteLine("  \u2717 " + message);
        }

        private static void Section(string name) => Console.WriteLine("\n== " + name + " ==");

        // ------------------------------------------------------------------ 路径
        //
        // 本自检要读仓库内生产源码做"源码守卫"（页面 / 工具 / codec / 模块 / 引擎调用点）。
        // 路径一律相对 mod 根目录解析：从程序输出目录向上寻找同时包含 ModInfo.xml 与 Terrain/ 的目录。
        // 找不到就明确报错退出（说明运行方式不对），不猜绝对路径、也不静默跳过这些守卫。
        private static string s_modRoot;

        private static string ModPath(params string[] parts)
        {
            if (s_modRoot == null)
                throw new InvalidOperationException("mod 根目录尚未定位：Main 必须先执行 FindModRoot");
            string path = s_modRoot;
            for (int i = 0; i < parts.Length; i++) path = Path.Combine(path, parts[i]);
            return path;
        }

        private static string FindModRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ModInfo.xml")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "Terrain")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        // ------------------------------------------------------------------ 合成世界

        /// <summary>
        /// 方块世界。主机侧 AutoLoad=false：RequestLoad 只**登记**，内容由测试显式 MakeValid
        /// （模拟"引擎稍后才把块加载出来"）。客户端侧 AutoLoad=true（本地加载即内容可用）。
        /// </summary>
        private sealed class World : ITerrainWorld
        {
            private readonly Dictionary<ChunkKey, int[]> m_chunks = new Dictionary<ChunkKey, int[]>();
            /// <summary>引擎**已分配**（占内存）的块：与内容是否定稿无关（海洋 / 水面块只有这一份）。</summary>
            private readonly HashSet<ChunkKey> m_allocated = new HashSet<ChunkKey>();
            private readonly HashSet<ChunkKey> m_divergent = new HashSet<ChunkKey>();
            private int m_requestLoads;

            public bool AutoLoad;

            public int RequestLoads => Volatile.Read(ref m_requestLoads);

            public static int Index(int x, int y, int z) => x + z * 16 + y * 256;

            public void MakeValid(ChunkKey key)
            {
                // 内容可用 ⇒ 引擎必然已分配（真实引擎的同一事实），测试也照此记账。
                m_allocated.Add(key);
                if (m_chunks.ContainsKey(key)) return;
                m_chunks[key] = Build(key, m_divergent.Contains(key));
            }

            /// <summary>只分配、内容还没定稿（海洋 / 水面块）：占内存、但 HasContents=false。</summary>
            public void Allocate(ChunkKey key) => m_allocated.Add(key);

            public void MakeDivergent(ChunkKey key) { m_allocated.Add(key); m_divergent.Add(key); if (m_chunks.ContainsKey(key)) m_chunks[key] = Build(key, true); }
            /// <summary>内容不可用（卸载 / 掉回未定稿）—— 引擎同时回收了块（测试按真实引擎的释放路径走）。</summary>
            public void Unload(ChunkKey key) { m_chunks.Remove(key); m_allocated.Remove(key); }
            /// <summary>引擎释放：离开内存，内容一起没了。</summary>
            public void Free(ChunkKey key) { m_chunks.Remove(key); m_allocated.Remove(key); }
            public bool HasContents(ChunkKey key) => m_chunks.ContainsKey(key);
            public IEnumerable<ChunkKey> LoadedChunks => m_chunks.Keys;
            public IEnumerable<ChunkKey> MemoryChunks => m_allocated;

            public uint DigestOf(ChunkKey key)
            {
                if (!m_chunks.ContainsKey(key)) return 0u;
                var digest = new ChunkDigest();
                digest.Compute(GetCellSource(key));
                return digest.HasValue ? digest.Value : 0u;
            }

            private static int[] Build(ChunkKey key, bool divergent)
            {
                var cells = new int[16 * 16 * 256];
                int seed = (key.X * 73856093) ^ (key.Z * 19349663);
                for (int x = 0; x < 16; x++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        int surface = 58 + ((seed + x * 7 + z * 13) & 7);
                        for (int y = 0; y < 256; y++)
                        {
                            int contents = y < surface - 4 ? 3 : (y < surface ? 2 : 0);
                            if (y == 0) contents = 1;
                            cells[Index(x, y, z)] = (contents & 0xFFFF) << 16;
                        }
                    }
                }
                if (divergent) cells[Index(5, 70, 5)] = 99 << 16;
                return cells;
            }

            public ChunkCellSource GetCellSource(ChunkKey key)
            {
                if (!m_chunks.TryGetValue(key, out int[] cells))
                    return (int x, int y, int z, out int c, out int d) => { c = 0; d = 0; };
                return (int x, int y, int z, out int c, out int d) =>
                {
                    int packed = cells[Index(x, y, z)];
                    c = (packed >> 16) & 0xFFFF;
                    d = packed & 0xFFFF;
                };
            }

            public void ApplyCells(ChunkKey key, IReadOnlyList<CellEdit> edits)
            {
                if (!m_chunks.TryGetValue(key, out int[] cells)) return;
                for (int i = 0; i < edits.Count; i++)
                {
                    CellEdit e = edits[i];
                    cells[Index(e.X, e.Y, e.Z)] = e.Packed;
                }
            }

            public void RequestLoad(ChunkKey key)
            {
                Interlocked.Increment(ref m_requestLoads);
                if (AutoLoad) MakeValid(key);
            }
        }

        // ------------------------------------------------------------------ 一个"房间"（真实 TCP）

        private readonly struct Verdict
        {
            public Verdict(ChunkKey key, DigestVerdict kind, uint hostHash)
            {
                Key = key; Kind = kind; HostHash = hostHash;
            }
            public ChunkKey Key { get; }
            public DigestVerdict Kind { get; }
            public uint HostHash { get; }
        }

        private sealed class Room : IDisposable
        {
            /// <summary>自检固定的主机会话标识（重连 Hello 必须带它，主机才接受）。</summary>
            public const string SessionId = "fedcba9876543210fedcba9876543210";
            public readonly World HostWorld = new World();
            public readonly World ClientWorld = new World { AutoLoad = true };
            public readonly TcpTerrainServer Server;
            public readonly TerrainSyncHost Host;
            public readonly TcpTerrainClient Client;
            public readonly ConcurrentQueue<ReceivedFrame> HostInbox = new ConcurrentQueue<ReceivedFrame>();
            /// <summary>客户端收到的帧：只入队（网络线程），由测试主线程 PumpClient 派发（同模块的做法）。</summary>
            public readonly ConcurrentQueue<ReceivedFrame> ClientInbox = new ConcurrentQueue<ReceivedFrame>();
            public readonly List<string> UnresolvedMessages = new List<string>();
            public readonly TerrainSyncClient Sync;      // 可空：本房间若只发原始报文就不挂
            private readonly List<ReceivedFrame> m_clientFrames = new List<ReceivedFrame>();

            public Room(TerrainSyncOptions options, bool withSyncClient)
            {
                Server = new TcpTerrainServer(0, 256, IPAddress.Loopback);
                Server.Start();
                Host = new TerrainSyncHost(HostWorld, Server, options ?? new TerrainSyncOptions(),
                    attachToChannel: false);
                Host.SessionId = SessionId;
                Host.ReportUnresolved += message => UnresolvedMessages.Add(message);
                Server.FrameReceived += frame => HostInbox.Enqueue(frame);
                Client = new TcpTerrainClient("127.0.0.1", Server.Port);
                if (!Client.Connect(3000)) throw new InvalidOperationException("连不上本机自检服务器");
                if (withSyncClient) Sync = new TerrainSyncClient(ClientWorld, Client, options,
                    attachToChannel: false);
                Client.FrameReceived += frame =>
                {
                    lock (m_clientFrames) m_clientFrames.Add(frame);
                    ClientInbox.Enqueue(frame);
                };
                WaitFor(() => Server.ListPeers().Length > 0, 2000);
            }

            public int UnresolvedLines => UnresolvedMessages.Count;

            public ReceivedFrame[] ClientFrames()
            {
                lock (m_clientFrames) return m_clientFrames.ToArray();
            }

            /// <summary>把真实收到的帧在主线程里派发给主机（模块里也是这么泵的）。</summary>
            public int PumpInbox()
            {
                int n = 0;
                while (HostInbox.TryDequeue(out ReceivedFrame frame)) { Host.ProcessFrame(frame); n++; }
                return n;
            }

            /// <summary>把客户端收到的帧在主线程里派发给 TerrainSyncClient（与模块一致：不在网络线程碰逻辑）。</summary>
            public int PumpClient()
            {
                if (Sync == null) return 0;
                int n = 0;
                while (ClientInbox.TryDequeue(out ReceivedFrame frame)) { Sync.ProcessFrame(frame); n++; }
                return n;
            }

            public void Pump() { PumpInbox(); PumpClient(); }

            /// <summary>等到主机回了至少 expected 条裁决（顺带把两端的入站帧都派发掉）。</summary>
            public bool WaitVerdicts(int expected, int timeoutMs) =>
                WaitFor(() => { Pump(); return Verdicts().Count >= expected; }, timeoutMs);

            public List<Verdict> Verdicts()
            {
                var list = new List<Verdict>();
                ReceivedFrame[] frames = ClientFrames();
                for (int i = 0; i < frames.Length; i++)
                {
                    if (frames[i].Type != TerrainMessageType.ChunkVerdict) continue;
                    var reader = new PayloadReader(frames[i].Payload);
                    int count = reader.Int32();
                    for (int k = 0; k < count; k++)
                        list.Add(new Verdict(new ChunkKey(reader.Int32(), reader.Int32()),
                            (DigestVerdict)reader.Byte(), reader.UInt32()));
                }
                return list;
            }

            public int ResyncFrames()
            {
                int n = 0;
                ReceivedFrame[] frames = ClientFrames();
                for (int i = 0; i < frames.Length; i++)
                    if (frames[i].Type == TerrainMessageType.ChunkResync) n++;
                return n;
            }

            public void SendHello(string name)
            {
                var writer = new PayloadWriter();
                writer.Byte((byte)ClientKind.Player).Text(name).Byte(1).Byte(1).Int32(2).Text("self-check");
                Client.TrySend(TerrainMessageType.Hello, writer.ToArray());
            }

            public void SendInterest(ChunkKey center, int radius)
            {
                var writer = new PayloadWriter();
                writer.Int32(center.X).Int32(center.Z).Int32(radius);
                Client.TrySend(TerrainMessageType.InterestUpdate, writer.ToArray());
            }

            public void SendReport(ChunkKey key, uint hash)
            {
                var writer = new PayloadWriter();
                writer.Int32(1).Int32(key.X).Int32(key.Z).UInt32(hash);
                Client.TrySend(TerrainMessageType.ChunkDigestReport, writer.ToArray());
            }

            public void Dispose()
            {
                try { Client.Dispose(); } catch (Exception) { }
                try { Server.Dispose(); } catch (Exception) { }
            }
        }

        // ------------------------------------------------- 欠更新计数（实时地图 chunks[].n）用房间

        /// <summary>一个玩家端连接（只发原始报文：Hello / Bye / ChunkDigestReport）。</summary>
        private sealed class PendingPeer
        {
            public TcpTerrainClient Channel;
            public int Id = -1;
            public string Name = string.Empty;
            /// <summary>Hello 里带上来的身份（UserId）；测试里用可控字符串（**不是**真 GUID）。</summary>
            public string Identity = string.Empty;

            /// <summary>客户端上报兴趣窗口（生产路径 InterestUpdate）：主机据此把这一块订阅给它。</summary>
            public void SendInterest(ChunkKey center, int radius)
            {
                var writer = new PayloadWriter();
                writer.Int32(center.X).Int32(center.Z).Int32(radius);
                Channel.TrySend(TerrainMessageType.InterestUpdate, writer.ToArray());
            }
        }

        /// <summary>
        /// 一个"主机 + 多个玩家端"的房间（真实 TCP + 真实 <see cref="TerrainSyncHost"/> 源码），
        /// 专门验证逐区块欠更新计数与**按身份归档**：改动标记 / 加入不补 / 核对清零 / 离开清掉 /
        /// 卸载不清 / 重进全 0 / 同身份重连复用记录 / 不同身份互不合并。
        /// </summary>
        private sealed class PendingRoom : IDisposable
        {
            public readonly World HostWorld = new World();
            public readonly TcpTerrainServer Server;
            public readonly TerrainSyncHost Host;
            public readonly ConcurrentQueue<ReceivedFrame> Inbox = new ConcurrentQueue<ReceivedFrame>();
            /// <summary>TerrainSyncHost 抛出的计数器生命周期日志（模块里就是把它写进游戏日志的）。</summary>
            public readonly List<string> Traces = new List<string>();
            /// <summary>SessionTrace（身份记录建立 / 重连复用 / 房间结束清空）——模块里写成 Info。</summary>
            public readonly List<string> SessionTraces = new List<string>();
            /// <summary>SessionProblem（身份缺失 / 身份异常）——模块里写成 Warning。</summary>
            public readonly List<string> SessionProblems = new List<string>();
            /// <summary>ReportUnresolved（欠着裁决 / 无身份连接发来上报等明确原因）。</summary>
            public readonly List<string> Unresolved = new List<string>();
            /// <summary>ClientChunkSetTrace（整集收齐 / 增量汇总；Terrain 层已限流）。</summary>
            public readonly List<string> ClientChunkTraces = new List<string>();
            /// <summary>ClientChunkSetProblem（当前内存区块集合载荷被拒绝；已限流）。</summary>
            public readonly List<string> ClientChunkProblems = new List<string>();
            private readonly List<PendingPeer> m_peers = new List<PendingPeer>();
            /// <summary>Raw peers (never read the socket; used to create real backpressure).</summary>
            private readonly List<RawPeer> m_rawPeers = new List<RawPeer>();

        /// <summary>
        /// 一个**从不读 socket** 的裸客户端（只发 Hello + Interest，之后不回读）：
        /// 用它把主机侧该连接的发送队列塞到上限，制造**真实的显式背压**（TrySend=false），
        /// 验证"已订阅但发送失败"仍记为欠最新 —— 而不是当成已送达。
        /// </summary>
        private sealed class RawPeer
        {
            public int Id = -1;
            public TcpClient Tcp;
            public NetworkStream Stream;
        }

            public PendingRoom()
            {
                Server = new TcpTerrainServer(0, 256, IPAddress.Loopback);
                Server.Start();
                Host = new TerrainSyncHost(HostWorld, Server, new TerrainSyncOptions(), attachToChannel: false);
                Host.PendingTrace += message => Traces.Add(message);
                Host.SessionTrace += message => SessionTraces.Add(message);
                Host.SessionProblem += message => SessionProblems.Add(message);
                Host.ReportUnresolved += message => Unresolved.Add(message);
                Host.ClientChunkSetTrace += message => ClientChunkTraces.Add(message);
                Host.ClientChunkSetProblem += message => ClientChunkProblems.Add(message);
                Server.FrameReceived += frame => Inbox.Enqueue(frame);
            }

            public void Pump()
            {
                while (Inbox.TryDequeue(out ReceivedFrame frame)) Host.ProcessFrame(frame);
            }

            /// <summary>
            /// 接一个玩家端上来并完成 Hello：返回它（含主机侧 clientId）。
            /// <paramref name="identity"/> 为空 = 显式走"Hello 没带身份"的退化路径（测试 (f) 用）；
            /// <paramref name="kind"/> = Debug 时 Hello 与实时地图工具完全一致（无身份字段、无重连字节），
            /// 主机侧**不建任何校验状态**（测试 (g) 用）。
            /// </summary>
            public PendingPeer AddClient(string name, string identity = null,
                ClientKind kind = ClientKind.Player)
            {
                var peer = new PendingPeer { Name = name, Identity = identity ?? ("identity-" + name) };
                peer.Channel = new TcpTerrainClient("127.0.0.1", Server.Port);
                if (!peer.Channel.Connect(3000)) throw new InvalidOperationException("连不上本机自检服务器");
                m_peers.Add(peer);
                var writer = new PayloadWriter();
                writer.Byte((byte)kind).Text(name).Byte(1).Byte(1).Int32(2);
                // 与生产 Hello 一致：Player 端追加身份 + 重连字节；Debug 只读端两个都不写。
                if (kind != ClientKind.Debug) writer.Text(peer.Identity);
                peer.Channel.TrySend(TerrainMessageType.Hello, writer.ToArray());
                // Hello 帧带的是主机侧编号：泵进来时顺手记下（每个连接只发一次 Hello）
                WaitFor(() =>
                {
                    while (Inbox.TryDequeue(out ReceivedFrame frame))
                    {
                        Host.ProcessFrame(frame);
                        if (frame.Type == TerrainMessageType.Hello) peer.Id = frame.ClientId;
                    }
                    return peer.Id > 0;
                }, 3000);
                return peer;
            }

            /// <summary>接一个裸端（Player 身份 + 兴趣窗口），返回主机侧 clientId；它之后**不读**任何回包。</summary>
            public int AddRawPeer(string name, string identity, ChunkKey center, int radius)
            {
                var tcp = new TcpClient();
                tcp.NoDelay = true;
                tcp.ReceiveBufferSize = 2048;       // 小接收缓冲：发送线程很快被写阻塞（队列才可能到上限）
                tcp.Connect(IPAddress.Loopback, Server.Port);
                var peer = new RawPeer { Tcp = tcp, Stream = tcp.GetStream() };
                m_rawPeers.Add(peer);
                var hello = new PayloadWriter();
                hello.Byte((byte)ClientKind.Player).Text(name).Byte(1).Byte(1).Int32(2)
                    .Text(identity).Byte(0);        // 0 = 不是重连
                WriteRawFrame(peer.Stream, 0, TerrainMessageType.Hello, hello.ToArray());
                var interest = new PayloadWriter();
                interest.Int32(center.X).Int32(center.Z).Int32(radius);
                WriteRawFrame(peer.Stream, 1, TerrainMessageType.InterestUpdate, interest.ToArray());
                WaitFor(() =>
                {
                    while (Inbox.TryDequeue(out ReceivedFrame frame))
                    {
                        Host.ProcessFrame(frame);
                        if (frame.Type == TerrainMessageType.Hello) peer.Id = frame.ClientId;
                    }
                    return peer.Id > 0;
                }, 3000);
                if (peer.Id <= 0) throw new InvalidOperationException("裸端握手没有到达主机");
                return peer.Id;
            }

            private static void WriteRawFrame(NetworkStream stream, long sequence, TerrainMessageType type, byte[] payload)
            {
                byte[] frame = TerrainFrame.Encode(sequence, type, payload);
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }

            /// <summary>
            /// 同一身份的**新连接**（模块看门狗重连时做的事）：旧连接直接断掉（不发 Bye），
            /// 立刻用**同一个身份** + alreadyHasWorld 字节重新握手 —— 这样主机侧可能短暂同时看到两条连接，
            /// 正是实机"旧链路还没被判失效、平板已经用新 clientId 回来"的场景。
            /// </summary>
            public PendingPeer Reconnect(PendingPeer peer)
            {
                try { peer.Channel.Dispose(); } catch (Exception) { }
                var next = new PendingPeer { Name = peer.Name, Identity = peer.Identity };
                next.Channel = new TcpTerrainClient("127.0.0.1", Server.Port);
                if (!next.Channel.Connect(3000)) throw new InvalidOperationException("重连连不上本机自检服务器");
                m_peers.Add(next);
                var writer = new PayloadWriter();
                writer.Byte((byte)ClientKind.Player).Text(next.Name).Byte(1).Byte(1).Int32(2)
                    .Text(next.Identity).Byte(1);        // 1 = 本端已经有过这个世界（重连）
                next.Channel.TrySend(TerrainMessageType.Hello, writer.ToArray());
                WaitFor(() =>
                {
                    while (Inbox.TryDequeue(out ReceivedFrame frame))
                    {
                        Host.ProcessFrame(frame);
                        if (frame.Type == TerrainMessageType.Hello) next.Id = frame.ClientId;
                    }
                    return next.Id > 0;
                }, 3000);
                return next;
            }

            public void SendReport(PendingPeer peer, ChunkKey key, uint hash)
            {
                var writer = new PayloadWriter();
                writer.Int32(1).Int32(key.X).Int32(key.Z).UInt32(hash);
                peer.Channel.TrySend(TerrainMessageType.ChunkDigestReport, writer.ToArray());
            }


            /// <summary>发一条"当前内存区块集合"载荷（测试里直接构造生产 codec 的载荷）。</summary>
            public void SendClientChunks(PendingPeer peer, byte[] payload) =>
                peer.Channel.TrySend(TerrainMessageType.ClientChunks, payload);

            /// <summary>当前内存集合里有没有这块（按身份记录读；测试断言用）。</summary>
            public bool CurrentLoadedContains(ChunkKey key)
            {
                TerrainHostSnapshot snapshot = Host.GetSnapshot();
                return snapshot.Clients.Count == 1 && snapshot.Clients[0].MemoryChunks.Contains(key);
            }

            public void SendBye(PendingPeer peer) =>
                peer.Channel.TrySend(TerrainMessageType.Bye, Array.Empty<byte>());

            /// <summary>不发 Bye 直接断链（模拟链路失效），并等到主机侧看到通道不可用。</summary>
            public void DropLink(PendingPeer peer)
            {
                try { peer.Channel.Dispose(); } catch (Exception) { }
                WaitFor(() =>
                {
                    ChannelState state = Server.StateOf(peer.Id);
                    return state != ChannelState.Connected && state != ChannelState.Backpressured;
                }, 3000);
            }

            public void Dispose()
            {
                for (int i = 0; i < m_peers.Count; i++)
                {
                    try { m_peers[i].Channel.Dispose(); } catch (Exception) { }
                }
                for (int i = 0; i < m_rawPeers.Count; i++)
                {
                    try { m_rawPeers[i].Tcp.Dispose(); } catch (Exception) { }
                }
                try { Server.Dispose(); } catch (Exception) { }
            }
        }

        private static string LastLine(List<string> list) =>
            list.Count == 0 ? "(无)" : list[list.Count - 1];

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(5);
                waited += 5;
            }
            return condition();
        }

        private static string KeyText(ChunkKey key) => "(" + key.X + "," + key.Z + ")";

        /// <summary>数一个子串在文本里出现几次（校验 feed 里某个条目只出现一次）。</summary>
        private static int CountOccurrences(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        /// <summary>四元数逐分量比较（位置/旋转/视线角都要跟着快照走）。</summary>
        private static bool SameRotation(Quaternion a, Quaternion b) =>
            Math.Abs(a.X - b.X) < 1e-4f && Math.Abs(a.Y - b.Y) < 1e-4f &&
            Math.Abs(a.Z - b.Z) < 1e-4f && Math.Abs(a.W - b.W) < 1e-4f;

        private static bool SameLook(Vector2 a, Vector2 b) =>
            Math.Abs(a.X - b.X) < 1e-4f && Math.Abs(a.Y - b.Y) < 1e-4f;

        private static string Describe(List<Verdict> verdicts)
        {
            var parts = new List<string>();
            for (int i = 0; i < verdicts.Count; i++)
                parts.Add(KeyText(verdicts[i].Key) + ":" + verdicts[i].Kind + "/" + verdicts[i].HostHash);
            return parts.Count == 0 ? "(无)" : string.Join(" ", parts);
        }

        // ------------------------------------------------------------------ 世界传输重连闸门（真实 TCP）
        //
        // 生产代码（同一份源码，不复制逻辑）：
        //   · Terrain/TerrainSync.cs   —— Hello 的 alreadyHasWorld → 主机 state.Rejoin → WasRejoin；
        //   · Modules/WorldTransferModule.cs —— 主机侧发不发整图（不变量①重连绝不发 / ②首次恰好一次）；
        //   · Terrain/RejoinFlagPolicy.cs    —— 客户端 Hello 标记的唯一判定。
        // 本文件只补一个端口适配（生产里由 TerrainSyncModule.Port 提供同样的成员）。

        private sealed class RecordingDiagnostics : IModuleDiagnostics
        {
            public readonly List<string> Lines = new List<string>();
            public void Info(string moduleId, string message) { lock (Lines) Lines.Add(message); }
            public void Warn(string moduleId, string message) { lock (Lines) Lines.Add("WARN " + message); }
            public void Error(string moduleId, string message, Exception exception) { lock (Lines) Lines.Add("ERROR " + message); }
            public bool Contains(string fragment)
            {
                lock (Lines)
                {
                    for (int i = 0; i < Lines.Count; i++)
                        if (Lines[i].Contains(fragment)) return true;
                }
                return false;
            }
            public string Find(string fragment)
            {
                lock (Lines)
                {
                    for (int i = 0; i < Lines.Count; i++)
                        if (Lines[i].Contains(fragment)) return Lines[i];
                }
                return null;
            }
        }

        private sealed class TestCapabilities : ICapabilityRegistry
        {
            public bool Has(string capability) => false;
            public IReadOnlyCollection<string> Active => Array.Empty<string>();
            public event Action<string, bool> CapabilityChanged { add { } remove { } }
        }

        private sealed class TransferTestContext : IModuleContext
        {
            private readonly Dictionary<Type, object> m_ports = new Dictionary<Type, object>();
            public TransferTestContext(IModuleDiagnostics diagnostics) { Diagnostics = diagnostics; }
            public string ModuleId => "world-transfer";
            public ICapabilityRegistry Capabilities { get; } = new TestCapabilities();
            public IModuleDiagnostics Diagnostics { get; }
            public void Add<T>(T port) where T : class { m_ports[typeof(T)] = port; }
            public bool TryGetPort<T>(out T port) where T : class
            {
                if (m_ports.TryGetValue(typeof(T), out object value)) { port = (T)value; return true; }
                port = null;
                return false;
            }
        }

        private sealed class TestTransportControl : ITransportControlPort
        {
            public bool Hosting = true;
            public bool Running = true;
            public bool StartHostServer(int port, out string reason) { reason = null; return Hosting; }
            public bool ConnectTo(string host, int port, out string reason)
            {
                reason = "自检只验证主机侧";
                return false;
            }
            public void StopChannel() { Running = false; }
            public bool IsChannelRunning => Running;
            public bool IsHosting => Hosting;
            public int ConnectedPlayerCount => 1;
            public string ChannelStateText => "test-host";
        }

        private sealed class TestExporter : IWorldExportSource
        {
            public byte[] Pak;
            public int Calls;
            public string WorldName = "TestWorld";
            public string Directory = "data:/Worlds/World1";
            public bool TryExport(out byte[] pak, out string worldName, out string directory, out string reason)
            {
                Calls++;
                pak = Pak;
                worldName = WorldName;
                directory = Directory;
                reason = Pak == null ? "尚未加载世界" : null;
                return Pak != null;
            }
        }

        private sealed class TestImporter : IWorldImporter
        {
            public bool TryImport(byte[] pak, out string directory, out string reason)
            {
                directory = null;
                reason = "自检只验证主机侧，不导入";
                return false;
            }
        }

        private sealed class TestDownloadedStore : IDownloadedWorldStore
        {
            private readonly List<string> m_registered = new List<string>();
            private readonly Dictionary<string, string> m_sessions = new Dictionary<string, string>();
            private readonly Dictionary<string, long> m_baseSequences = new Dictionary<string, long>();
            public void Register(string directory) { if (!m_registered.Contains(directory)) m_registered.Add(directory); }
            public IReadOnlyList<string> Registered => m_registered;
            public void SaveRegistry(IReadOnlyList<string> directories)
            {
                m_registered.Clear();
                if (directories != null) m_registered.AddRange(directories);
            }
            public bool Delete(string directory, out string reason)
            {
                reason = null;
                m_registered.Remove(directory);
                m_sessions.Remove(directory);
                m_baseSequences.Remove(directory);
                return true;
            }
            public void RegisterSession(string directory, string sessionId, long baseSequence)
            {
                Register(directory);
                m_sessions[directory] = sessionId ?? string.Empty;
                m_baseSequences[directory] = baseSequence;
            }
            public bool TryGetSession(string directory, out string sessionId, out long baseSequence)
            {
                sessionId = string.Empty;
                baseSequence = 0L;
                if (!m_sessions.TryGetValue(directory, out sessionId)) return false;
                m_baseSequences.TryGetValue(directory, out baseSequence);
                return !string.IsNullOrEmpty(sessionId);
            }
            public void ForgetSession(string directory)
            {
                m_sessions[directory] = string.Empty;
                m_baseSequences[directory] = 0L;
            }
        }

        /// <summary>
        /// 生产里 <c>TerrainSyncModule.Port</c> 的同形适配：ClientJoined 的 rejoin 就是
        /// <c>m_host.WasRejoin(clientId)</c>（握手刚写完 state），TrySend 发给最后加入的玩家端。
        /// </summary>
        private sealed class WorldHostPort : ITransportPort, IWorldSessionPort
        {
            private readonly TcpTerrainServer m_server;
            private readonly TerrainSyncHost m_host;
            private int m_targetId;
            public int JoinedCount;
            public int LastRejoin;
            public int LastClientId;
            /// <summary>客户端导入完成回调（测试里由 Install 触发；本工程的主机侧不会触发）。</summary>
            public int InstalledCount;
            public string InstalledSessionId = string.Empty;
            public long InstalledBaseSequence;

            public WorldHostPort(TcpTerrainServer server, TerrainSyncHost host)
            {
                m_server = server;
                m_host = host;
                m_host.ClientJoined += HandleClientJoined;
            }

            public void Unbind() { m_host.ClientJoined -= HandleClientJoined; }

            private void HandleClientJoined(int clientId, string name, string identity)
            {
                // 生产 TerrainSyncModule.HandleHostClientJoined 的同一组事实：
                // 握手刚写完 state → 查实际重连标记 → 只把玩家端转给世界传输模块。
                bool rejoin = m_host.WasRejoin(clientId);
                m_targetId = clientId;
                LastClientId = clientId;
                LastRejoin = rejoin ? 1 : 0;
                JoinedCount++;
                ClientJoined?.Invoke(clientId, name, identity ?? string.Empty, rejoin);
            }

            public bool TrySend(TerrainMessageType type, byte[] payload) =>
                m_targetId > 0 && m_server.TrySend(m_targetId, type, payload);

            public bool IsConnected => m_targetId > 0 && m_server.StateOf(m_targetId) == ChannelState.Connected;
            public string StateText => "test-host-port";
            public event Action<ReceivedFrame> FrameArrived { add { } remove { } }
            public event Action<int, string, string, bool> ClientJoined;
            public event Action<int, string> ClientLeft { add { } remove { } }
            public string HostIdentity => string.Empty;
            public string HostName => string.Empty;
            public int LocalClientId => -1;

            // IWorldSessionPort：生产里由 TerrainSyncModule.Port 提供同样的值。
            public string SessionId => m_host.SessionId;
            public long CurrentChangeSequence => m_host.CurrentChangeSequence;
            public void OnClientWorldInstalled(string worldDirectory, string sessionId, long baseSequence)
            {
                InstalledCount++;
                InstalledSessionId = sessionId ?? string.Empty;
                InstalledBaseSequence = baseSequence;
            }
        }

        /// <summary>真实 TCP + 真实 TerrainSyncHost + 真实 WorldTransferModule 的主机房间。</summary>
        private sealed class TransferRoom : IDisposable
        {
            /// <summary>自检固定的主机会话标识（生产里由 SessionIdentity.NewSessionId 生成）。</summary>
            public const string HostSessionId = "0123456789abcdef0123456789abcdef";
            public readonly World HostWorld = new World();
            public readonly World ClientWorld = new World { AutoLoad = true };
            public readonly TcpTerrainServer Server;
            public readonly TerrainSyncHost Host;
            public readonly TerrainSyncOptions Options;
            public readonly WorldHostPort Port;
            public readonly TestExporter Exporter = new TestExporter();
            public readonly RecordingDiagnostics Log = new RecordingDiagnostics();
            public readonly ConcurrentQueue<ReceivedFrame> HostInbox = new ConcurrentQueue<ReceivedFrame>();
            public readonly ConcurrentQueue<ReceivedFrame> ClientInbox = new ConcurrentQueue<ReceivedFrame>();
            public readonly List<string> SessionTraces = new List<string>();
            public readonly TestTransportControl Control = new TestTransportControl();
            public readonly TestDownloadedStore Store = new TestDownloadedStore();
            public readonly WorldTransferModule Module;
            public TcpTerrainClient Client;
            /// <summary>客户端地形同步（真实 TerrainSyncClient；由测试主线程泵帧，与模块一致）。</summary>
            public TerrainSyncClient Sync;
            public double NowSeconds = 10.0;
            private readonly List<ReceivedFrame> m_clientFrames = new List<ReceivedFrame>();
            private long m_frame;

            public TransferRoom(TerrainSyncOptions options = null)
            {
                Options = options ?? new TerrainSyncOptions();
                Server = new TcpTerrainServer(0, 256, IPAddress.Loopback);
                Server.Start();
                Host = new TerrainSyncHost(HostWorld, Server, Options, attachToChannel: false);
                Host.SessionId = HostSessionId;
                Host.SessionTrace += message => { lock (SessionTraces) SessionTraces.Add(message); };
                Server.FrameReceived += frame => HostInbox.Enqueue(frame);
                Port = new WorldHostPort(Server, Host);
                var context = new TransferTestContext(Log);
                context.Add<ITransportPort>(Port);
                context.Add<IWorldSessionPort>(Port);
                context.Add<ITransportControlPort>(Control);
                Module = new WorldTransferModule(new SyncConfig(), Exporter, new TestImporter(), Store);
                Module.Attach(context);
                Client = Connect();
            }

            public TcpTerrainClient Connect()
            {
                var client = new TcpTerrainClient("127.0.0.1", Server.Port);
                if (!client.Connect(3000)) throw new InvalidOperationException("连不上世界传输自检服务器");
                client.FrameReceived += frame =>
                {
                    lock (m_clientFrames) m_clientFrames.Add(frame);
                    ClientInbox.Enqueue(frame);
                };
                WaitFor(() => Server.ListPeers().Length > 0, 2000);
                while (ClientInbox.TryDequeue(out _)) { }
                Sync = new TerrainSyncClient(ClientWorld, client, Options, attachToChannel: false);
                return client;
            }

            public void SendHello(TcpTerrainClient client, bool alreadyHasWorld,
                string sessionId = null, long lastApplied = 0)
            {
                var writer = new PayloadWriter();
                writer.Byte((byte)ClientKind.Player).Text("client-WT").Byte(1).Byte(1).Int32(2)
                    .Text("identity-WT").Byte((byte)(alreadyHasWorld ? 1 : 0))
                    .Text(sessionId ?? string.Empty).Int64(lastApplied);
                if (!client.TrySend(TerrainMessageType.Hello, writer.ToArray()))
                    throw new InvalidOperationException("Hello 发送失败");
            }

            /// <summary>发 Hello 并等到主机完成握手（真实 TCP 是异步的，不能假设下一 tick 就到）。</summary>
            public void Join(TcpTerrainClient client, bool alreadyHasWorld,
                string sessionId = null, long lastApplied = 0)
            {
                SendHello(client, alreadyHasWorld, sessionId, lastApplied);
                bool joined = WaitFor(() =>
                {
                    while (HostInbox.TryDequeue(out ReceivedFrame frame)) Host.ProcessFrame(frame);
                    return Host.ClientCount > 0 && Port.LastClientId > 0;
                }, 5000);
                if (!joined) throw new InvalidOperationException("Hello 没有在 5s 内完成握手");
                Tick(2);
            }

            /// <summary>
            /// 用**真实 TerrainSyncClient** 握手（生产路径：Hello 带身份 + 会话 + 上次应用序号），
            /// 同时泵两端的帧（客户端在主线程处理，与模块一致）。
            /// </summary>
            public void JoinSync(bool alreadyHasWorld, string sessionId, long lastApplied,
                string name = "client-WT")
            {
                Sync.SetAppliedChangeSequence(lastApplied);
                int before = Sync.Handshakes;
                Sync.SendHello(name, "identity-WT", alreadyHasWorld, sessionId, lastApplied);
                bool joined = WaitFor(() =>
                {
                    PumpBoth();
                    return Sync.Handshakes > before;
                }, 5000);
                if (!joined) throw new InvalidOperationException("TerrainSyncClient Hello 没有在 5s 内完成握手");
                Tick(2);
                PumpBoth();
            }

            public int PumpClient()
            {
                if (Sync == null) return 0;
                int n = 0;
                while (ClientInbox.TryDequeue(out ReceivedFrame frame)) { Sync.ProcessFrame(frame); n++; }
                return n;
            }

            public void PumpBoth() { PumpClient(); while (HostInbox.TryDequeue(out ReceivedFrame frame)) Host.ProcessFrame(frame); }

            /// <summary>模拟"引擎已经把格子改好，主机再广播"：先改主机世界，再走真实 BroadcastCellChange。</summary>
            public void Change(ChunkKey key, CellEdit edit)
            {
                HostWorld.ApplyCells(key, new[] { edit });
                Host.BroadcastCellChange(key, edit);
            }

            public ReceivedFrame[] Frames()
            {
                lock (m_clientFrames) return m_clientFrames.ToArray();
            }

            /// <summary>推进主机（TerrainSyncHost.Tick）+ 世界传输模块（每 tick 8 片）。</summary>
            public void Tick(int ticks = 1)
            {
                for (int i = 0; i < ticks; i++)
                {
                    while (HostInbox.TryDequeue(out ReceivedFrame frame)) Host.ProcessFrame(frame);
                    Host.Tick();
                    Module.Tick(new ModuleTickContext(++m_frame, NowSeconds, 1f / 60f, true, true));
                    NowSeconds += 1.0 / 60.0;
                }
            }

            public int Count(TerrainMessageType type)
            {
                int n = 0;
                ReceivedFrame[] frames = Frames();
                for (int i = 0; i < frames.Length; i++) if (frames[i].Type == type) n++;
                return n;
            }

            /// <summary>推进到客户端真的收到 WorldEnd（TCP 投递是异步的，不能只数 tick）。</summary>
            public bool TickUntilWorldEnd(int maxTicks = 1500)
            {
                for (int i = 0; i < maxTicks; i++)
                {
                    Tick(4);
                    if (Count(TerrainMessageType.WorldEnd) > 0) return true;
                    Thread.Sleep(1);
                }
                return false;
            }

            /// <summary>推进直到条件成立（同时泵主机 + 客户端 + 两端 Tick）。</summary>
            public bool TickUntil(Func<bool> condition, int maxTicks = 1500)
            {
                for (int i = 0; i < maxTicks; i++)
                {
                    Tick(1);
                    Sync?.Tick();
                    PumpBoth();
                    if (condition()) return true;
                    Thread.Sleep(1);
                }
                return condition();
            }

            /// <summary>给网络线程一点时间把已排队的帧投递到客户端，再统计。</summary>
            public void Settle(int ms = 80) => Thread.Sleep(ms);

            public void Dispose()
            {
                try { Port.Unbind(); } catch (Exception) { }
                try { Module.Detach(ModuleDetachReason.HostShutdown); } catch (Exception) { }
                try { Client?.Dispose(); } catch (Exception) { }
                try { Server.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>确定性的世界 pak 字节（内容不是真 zip：这里只验证分片/重组/字节一致）。</summary>
        private static byte[] MakeWorldPak(int length)
        {
            var pak = new byte[length];
            unchecked
            {
                for (int i = 0; i < length; i++) pak[i] = (byte)(i * 31 + 7);
            }
            return pak;
        }

        // ------------------------------------------------------------------ 主流程

        private static int Main()
        {
            s_modRoot = FindModRoot();
            if (s_modRoot == null)
            {
                Console.WriteLine("无法定位 mod 根目录：从 " + AppContext.BaseDirectory +
                    " 向上未找到同时包含 ModInfo.xml 与 Terrain/ 的目录。" +
                    "请从仓库内用 --project 指向本套件 csproj 运行。");
                return 2;
            }
            Console.WriteLine("mod 根目录：" + s_modRoot);

            Section("A/B. 上报时主机缺内容 → 块变为内容可用 → Tick 补齐真实主机摘要 → Verified");
            {
                var options = new TerrainSyncOptions();
                using var room = new Room(options, withSyncClient: true);
                room.Sync.SendHello("client-A");
                room.Sync.SendInterest(new ChunkKey(0, 0), 2);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                var key = new ChunkKey(0, 0);
                // 主机侧先有一次真实加载（拿到真值），然后卸载 —— 模拟"客户端报的时候主机手上没有"
                room.HostWorld.MakeValid(key);
                uint expected = room.HostWorld.DigestOf(key);
                room.HostWorld.Unload(key);
                Expect(!room.HostWorld.HasContents(key), "主机此刻确实没有 " + KeyText(key) + " 的内容");
                Expect(expected != 0u, "主机侧真值摘要非 0：" + expected.ToString("x8"));

                // 客户端世界同一种地形 → 摘要相同；加载完上报
                room.ClientWorld.MakeValid(key);
                Expect(room.ClientWorld.DigestOf(key) == expected, "客户端摘要与主机真值一致（同一种地形）");
                Expect(room.Sync.OnChunkLoaded(key), "客户端块加载完 → 产生一次上报");
                Expect(room.Sync.Tick() == 1, "客户端 Tick 把这次上报发了出去");

                Expect(room.WaitVerdicts(1, 3000), "主机回了第一条裁决");
                List<Verdict> first = room.Verdicts();
                Expect(first.Count >= 1 && first[0].Key.Equals(key) && first[0].Kind == DigestVerdict.NotReady &&
                      first[0].HostHash == 0u,
                    "第一条是 NotReady + hostHash=0（明确的\"还没好\"，没丢这次上报）：" + Describe(first));
                TerrainHostSnapshot pending = room.Host.GetSnapshot();
                Expect(pending.PendingReports == 1 && pending.Clients[0].PendingReports == 1,
                    "主机登记了 1 条待裁决（客户端摘要留着）：" + pending.PendingReports);
                Expect(room.Host.VerifyStateOf(key) == ChunkVerifyState.Requested, "实时地图状态 = Requested（黄）");
                Expect(room.HostWorld.RequestLoads >= 1, "主机已经向引擎发过加载请求：" + room.HostWorld.RequestLoads);

                // 引擎把块加载出来了 → 主机 Tick 必须补齐真实裁决（这就是"块后来才可用"的驱动点）
                room.HostWorld.MakeValid(key);
                room.Host.Tick();
                Expect(room.WaitVerdicts(2, 3000), "主机 Tick 补了第二条裁决");
                List<Verdict> second = room.Verdicts();
                Verdict match = second[second.Count - 1];
                Expect(match.Key.Equals(key) && match.Kind == DigestVerdict.Match, "第二条是 Match：" + Describe(second));
                Expect(match.HostHash == expected && match.HostHash != 0u,
                    "Match 带的是**真实主机摘要** " + match.HostHash.ToString("x8") + "（期望 " + expected.ToString("x8") + "）");
                Expect(room.Host.VerifyStateOf(key) == ChunkVerifyState.Verified, "实时地图状态 = Verified（绿）");
                Expect(room.Host.GetSnapshot().PendingReports == 0, "待裁决清了（这一条真的被回答了）");
                Expect(room.Sync.StateOf(key) == ChunkFreshness.Fresh && room.Sync.LocalHash(key) == expected,
                    "客户端侧这块 = Fresh，本地摘要与主机一致");

                Section("F. ReportedLoaded 只随上报增长（实时地图的来源未受影响）");
                TerrainHostSnapshot snapA = room.Host.GetSnapshot();
                Expect(snapA.Clients[0].ReportedLoaded.Count == 1,
                    "上报过 1 块 → ReportedLoaded=1：" + snapA.Clients[0].ReportedLoaded.Count);
                for (int i = 0; i < 20; i++) room.Host.Tick();
                Expect(room.Host.GetSnapshot().Clients[0].ReportedLoaded.Count == 1,
                    "连续 Tick 不会往 ReportedLoaded 里加东西（只由上报增长）");
            }

            Section("C/D. 客户端摘要不一致 → Mismatch(真实主机摘要) + 回传 → 重报 → ReVerified（蓝）");
            {
                var options = new TerrainSyncOptions();
                using var room = new Room(options, withSyncClient: true);
                room.Sync.SendHello("client-CD");
                room.Sync.SendInterest(new ChunkKey(0, 0), 2);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                var key = new ChunkKey(1, 0);
                room.HostWorld.MakeValid(key);                     // 主机有内容 → 立即裁决
                uint hostHash = room.HostWorld.DigestOf(key);
                room.ClientWorld.MakeValid(key);
                room.ClientWorld.MakeDivergent(key);
                uint clientHash = room.ClientWorld.DigestOf(key);
                Expect(clientHash != hostHash, "客户端这份内容确实不一致（" + clientHash.ToString("x8") +
                    " vs " + hostHash.ToString("x8") + "）");

                Expect(room.Sync.OnChunkLoaded(key) && room.Sync.Tick() == 1, "客户端上报分歧摘要");
                Expect(room.WaitVerdicts(1, 3000), "主机回了裁决");
                List<Verdict> first = room.Verdicts();
                Expect(first[0].Kind == DigestVerdict.Mismatch, "裁决 = Mismatch（红）：" + Describe(first));
                Expect(first[0].HostHash == hostHash, "Mismatch 也带真实主机摘要 " + first[0].HostHash.ToString("x8"));
                Expect(room.Host.VerifyStateOf(key) == ChunkVerifyState.Failed, "实时地图状态 = Failed（红）");
                Expect(room.Sync.StateOf(key) == ChunkFreshness.Stale, "客户端侧 = Stale（等回传覆盖）");

                room.Host.Tick();                                   // 排队中的回传发出去
                Expect(WaitFor(() => { room.PumpClient(); return room.ResyncFrames() >= 1; }, 3000),
                    "客户端收到了回传分片");
                // 回传落地现在受**每帧预算**限制：帧只入队，落地发生在下一次 Tick 里
                // （旧实现收到即在帧里落地）。所以这里必须按"泵帧 + 跑 Tick"来推进。
                long reportsBeforeResync = room.Host.GetSnapshot().DigestReports;
                Expect(WaitFor(() =>
                {
                    room.PumpClient();
                    room.Sync.Tick();
                    room.PumpInbox();
                    return room.Sync.StateOf(key) != ChunkFreshness.Stale &&
                           room.Host.GetSnapshot().DigestReports > reportsBeforeResync;
                }, 3000),
                    "回传在**下一次 Tick 的预算内**落地，落地后客户端重新上报（状态离开 Stale）且主机收到了覆盖后的摘要");
                Expect(room.Sync.GetSnapshot().ResyncAppliedThisTick <= 1,
                    "回传落地受每帧预算限制（上一帧落地分片数 " +
                    room.Sync.GetSnapshot().ResyncAppliedThisTick + " ≤ 1）");
                Expect(room.WaitVerdicts(2, 3000), "主机回了第二条裁决");
                List<Verdict> second = room.Verdicts();
                Verdict re = second[second.Count - 1];
                Expect(re.Key.Equals(key) && re.Kind == DigestVerdict.Match && re.HostHash == hostHash,
                    "第二条 = Match + 真实主机摘要：" + Describe(second));
                Expect(room.Host.VerifyStateOf(key) == ChunkVerifyState.ReVerified,
                    "实时地图状态 = ReVerified（蓝：曾失败、回传后这次通过）");
                Expect(room.Sync.StateOf(key) == ChunkFreshness.Fresh &&
                      room.Sync.LocalHash(key) == hostHash, "客户端侧 = Fresh 且本地摘要 = 主机摘要");
            }

            Section("E. 主机一直拿不到内容 → 绝不伪造匹配；明确原因只写一行；继续按节奏重请");
            {
                var options = new TerrainSyncOptions { PendingReloadIntervalMs = 30 };
                using var room = new Room(options, withSyncClient: false);
                room.SendHello("client-E");
                room.SendInterest(new ChunkKey(0, 0), 2);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                var key = new ChunkKey(2, 0);
                Expect(!room.HostWorld.HasContents(key), "主机始终没有 " + KeyText(key) + " 的内容（引擎没加载出来）");
                room.SendReport(key, 0x12345678u);
                Expect(room.WaitVerdicts(1, 3000), "主机回了 NotReady（每次上报都被回答）");
                Expect(room.Host.GetSnapshot().PendingReports == 1, "待裁决 = 1（没有放弃）");

                for (int i = 0; i < 60; i++) { room.Host.Tick(); Thread.Sleep(3); }
                List<Verdict> verdicts = room.Verdicts();
                bool anyReal = false;
                for (int i = 0; i < verdicts.Count; i++)
                    if (verdicts[i].Key.Equals(key) && verdicts[i].Kind != DigestVerdict.NotReady) anyReal = true;
                Expect(!anyReal, "主机**没有**伪造 Match/Mismatch：" + Describe(verdicts));
                Expect(room.Host.VerifyStateOf(key) == ChunkVerifyState.Requested, "状态仍是 Requested（黄，等主机有内容）");
                Expect(room.Host.GetSnapshot().PendingReports == 1, "待裁决仍是 1（条目不会被丢掉）");
                Expect(room.HostWorld.RequestLoads >= 2,
                    "按节奏重复请求引擎加载（>=2 次）：" + room.HostWorld.RequestLoads);
                Expect(room.UnresolvedLines == 1, "明确原因只写一行（同一块不刷屏）：" + room.UnresolvedLines);
                Expect(room.UnresolvedLines == 1 && room.UnresolvedMessages[0].Contains(KeyText(key)) &&
                      room.UnresolvedMessages[0].Contains("绝不假装匹配"),
                    "原因行点名了这块并写明不伪造：" + (room.UnresolvedMessages.Count > 0 ? room.UnresolvedMessages[0] : "(空)"));
                for (int i = 0; i < 30; i++) room.Host.Tick();
                Expect(room.UnresolvedLines == 1, "再 Tick 30 次仍然只有那一行（限流生效）");
            }

            Section("G. 客户端收到 NotReady 后**退避**重报（不是当场连发），最终仍拿到真实裁决");
            {
                var options = new TerrainSyncOptions();
                using var room = new Room(options, withSyncClient: true);
                room.Sync.SendHello("client-G");
                room.Sync.SendInterest(new ChunkKey(0, 0), 2);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                var key = new ChunkKey(3, 0);
                room.HostWorld.MakeValid(key);
                uint expected = room.HostWorld.DigestOf(key);
                room.HostWorld.Unload(key);                         // 主机此刻没有 → 必然 NotReady
                room.ClientWorld.MakeValid(key);

                Expect(room.Sync.OnChunkLoaded(key) && room.Sync.Tick() == 1, "客户端上报第 1 次");
                Expect(room.WaitVerdicts(1, 3000) && room.Verdicts()[0].Kind == DigestVerdict.NotReady,
                    "主机回 NotReady");
                Expect(room.Sync.GetSnapshot().Retries == 1, "客户端记下这次 NotReady");
                long reportsBefore = room.Host.GetSnapshot().DigestReports;
                Expect(room.Sync.Tick() == 0, "紧接着的 Tick **不重报**（退避没到点，不是当场连发）");
                Thread.Sleep(400);                                  // 首次退避 250ms
                Expect(room.Sync.Tick() >= 1, "退避到点后重报了一次");
                WaitFor(() => { room.PumpInbox(); return room.Host.GetSnapshot().DigestReports > reportsBefore; }, 3000);
                Expect(room.Host.GetSnapshot().DigestReports == reportsBefore + 1,
                    "主机确实收到了这次重报（摘要上报 " + room.Host.GetSnapshot().DigestReports + "）");
                Expect(room.WaitVerdicts(2, 3000), "主机对重报也回了一条");
                TerrainClientSnapshot clientSnap = room.Sync.GetSnapshot();
                Expect(clientSnap.ReReports == 1 && clientSnap.Abandoned == 0,
                    "客户端重报计数 " + clientSnap.ReReports + " · 放弃 " + clientSnap.Abandoned);

                room.HostWorld.MakeValid(key);                      // 主机这时加载好了
                room.Host.Tick();
                Expect(room.WaitVerdicts(3, 3000), "主机 Tick 补齐真实裁决");
                List<Verdict> verdicts = room.Verdicts();
                Verdict last = verdicts[verdicts.Count - 1];
                Expect(last.Key.Equals(key) && last.Kind == DigestVerdict.Match && last.HostHash == expected,
                    "最后一条 = Match + 真实主机摘要：" + Describe(verdicts));
                Expect(room.Sync.StateOf(key) == ChunkFreshness.Fresh, "客户端侧最终 = Fresh");
                Expect(room.Host.GetSnapshot().PendingReports == 0, "主机待裁决清零");
            }

            Section("H. 对端断开：欠着的上报明确写一行并**保留在身份记录里**（不对着断线重请），同身份回来继续");
            {
                var options = new TerrainSyncOptions();
                using var room = new Room(options, withSyncClient: false);
                room.SendHello("client-H");
                room.SendInterest(new ChunkKey(0, 0), 2);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                var key = new ChunkKey(4, 0);
                // 上报的是**真实客户端摘要**（先让主机算出真值，再卸载 → 上报时主机手上没有内容）
                room.HostWorld.MakeValid(key);
                uint expectedH = room.HostWorld.DigestOf(key);
                room.HostWorld.Unload(key);
                room.SendReport(key, expectedH);
                Expect(room.WaitVerdicts(1, 3000), "主机回了 NotReady");
                Expect(room.Host.GetSnapshot().PendingReports == 1, "待裁决 = 1");
                int loadsBefore = room.HostWorld.RequestLoads;
                room.Client.Dispose();                              // 直接断链（不发 Bye）
                Expect(WaitFor(() => { room.Host.Tick(); return room.Host.GetSnapshot().PendingReports == 0; }, 3000),
                    "断线后快照待裁决清零（不再把这根死线算作欠账）");
                Expect(room.UnresolvedLines >= 1 &&
                      room.UnresolvedMessages[room.UnresolvedLines - 1].Contains("连接已不可用") &&
                      room.UnresolvedMessages[room.UnresolvedLines - 1].Contains("保留在该身份的会话记录里"),
                    "写了一条明确原因（" + room.UnresolvedLines + " 行）：" +
                    (room.UnresolvedLines > 0 ? room.UnresolvedMessages[room.UnresolvedLines - 1] : "(空)"));
                int loadsAfterDrop = room.HostWorld.RequestLoads;
                for (int i = 0; i < 20; i++) room.Host.Tick();
                Expect(room.HostWorld.RequestLoads == loadsAfterDrop,
                    "不再向引擎重请这块（" + loadsBefore + "→" + loadsAfterDrop + "→" + room.HostWorld.RequestLoads + "）");

                // 同一身份（Room.SendHello 固定写 "self-check"）回来：欠着的上报继续（不清空、不重置）
                var second = new TcpTerrainClient("127.0.0.1", room.Server.Port);
                try
                {
                    Expect(second.Connect(3000), "同身份的新连接建立");
                    second.FrameReceived += frame => room.HostInbox.Enqueue(frame);
                    var writer = new PayloadWriter();
                    writer.Byte((byte)ClientKind.Player).Text("client-H").Byte(1).Byte(1).Int32(2)
                        .Text("self-check").Byte(1);
                    second.TrySend(TerrainMessageType.Hello, writer.ToArray());
                    Expect(WaitFor(() => { room.PumpInbox(); return room.Host.GetSnapshot().Clients.Count == 1; }, 3000),
                        "重连后主机侧仍是 1 条客户端记录（身份相同）");
                    TerrainHostSnapshot resumed = room.Host.GetSnapshot();
                    Expect(resumed.PendingReports == 1 && resumed.Clients[0].PendingReports == 1,
                        "待裁决**继续**（1）：断线只是暂停，不是作废（实际 " + resumed.PendingReports + "）");
                    room.HostWorld.MakeValid(key);
                    room.Host.Tick();
                    Expect(WaitFor(() => { room.Host.Tick(); return room.Host.GetSnapshot().PendingReports == 0; }, 3000),
                        "主机有内容后用真实主机摘要作答 → 待裁决归 0");
                    Expect(room.Host.VerifyStateOf(key) == ChunkVerifyState.Verified,
                        "该身份的上报最终 Verified（跨重连同一条记录）");
                }
                finally
                {
                    try { second.Dispose(); } catch (Exception) { }
                }
            }

            Section("I. 欠更新计数（实时地图 chunks[].n）：改动标记 / 加入不补 / 裁决不清 / 加载清除 / 离开清掉 / 卸载不清 / 重进全 0");
            {
                using var room = new PendingRoom();
                var edit = new CellEdit(1, 2, 3, 2, 0);
                var keyA = new ChunkKey(0, 0);
                room.HostWorld.MakeValid(keyA);

                // (a) 一个在线身份 + 区块有改动 → 计数 1
                PendingPeer a = room.AddClient("client-A");
                Expect(room.Host.PendingClientCount(keyA) == 0, "改动前该区块计数 = 0（0 不标注）");
                Expect(room.Host.BroadcastCellChange(keyA, edit) == 0,
                    "该块没有订阅者 → 广播发出 0 份（计数与\"谁订了这块\"无关）");
                Expect(room.Host.PendingClientCount(keyA) == 1, "区块有改动 → 计数 = 1（在线身份 A 欠最新）");
                Expect(room.Traces.Count > 0 && LastLine(room.Traces).Contains("有改动") &&
                       LastLine(room.Traces).Contains("记为 1 个在线身份"),
                    "加标记写了明确日志：" + LastLine(room.Traces));

                // (f) 主机卸载这一块 → 计数仍在（与主机的加载集合无关）
                room.HostWorld.Unload(keyA);
                Expect(!room.HostWorld.HasContents(keyA), "主机已卸载这一块（内容不在手上）");
                Expect(room.Host.PendingClientCount(keyA) == 1, "主机离开这一块后计数仍是 1（卸载 / 离开不清零）");
                room.HostWorld.MakeValid(keyA);

                // (b) 第二个身份加入 → 计数仍是 1（新身份整图就是最新基线，不补标记）
                PendingPeer b = room.AddClient("client-B");
                Expect(room.Host.PendingClientCount(keyA) == 1, "第二个身份加入 → 计数仍是 1（加入时不补标记）");

                // (c) A 上报真实摘要 → Match（验证语义不变），但**裁决不参与欠更新计数**：
                //     A 的当前内存集合里仍没有这一块 → 数字仍是 1。
                room.SendReport(a, keyA, room.HostWorld.DigestOf(keyA));
                Expect(WaitFor(() => { room.Pump(); return room.Host.VerifyStateOf(keyA) == ChunkVerifyState.Verified; }, 3000),
                    "A 上报真实摘要 → 主机裁决 Match → Verified（验证状态语义未变）");
                Expect(room.Host.PendingClientCount(keyA) == 1,
                    "裁决 Match **不清**欠更新：A 的当前内存集合里仍没有这一块 → 计数仍是 1");
                bool verdictClearedMark = false;
                for (int i = 0; i < room.Traces.Count; i++)
                    if (room.Traces[i].Contains("核对通过")) verdictClearedMark = true;
                Expect(!verdictClearedMark,
                    "没有\"核对通过 → 移除标记\"的旧路径（裁决不参与欠更新计数）");

                // (c2) A 的当前内存集合**新增**这一块（ClientChunks 增量 add）→ 欠账清零
                room.SendClientChunks(a, ClientChunkSetCodec.EncodeDelta(
                    new[] { new ClientChunkEntry(keyA.X, keyA.Z, true) }));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyA) == 0; }, 3000),
                    "A 的内存集合新增这一块 → 计数 = 0（网页不标注）");
                Expect(LastLine(room.ClientChunkTraces).Contains("清除欠更新标记 1 个"),
                    "增量汇总日志写明清除了几个欠更新标记：" + LastLine(room.ClientChunkTraces));

                // (d) 两个区块同时标上 A+B（各 2）→ B 退出联机 → 它在这两块上的标记全部去掉
                var keyB = new ChunkKey(1, 1);
                var keyC = new ChunkKey(2, 2);
                room.HostWorld.MakeValid(keyB);
                room.HostWorld.MakeValid(keyC);
                room.Host.BroadcastCellChange(keyB, edit);
                room.Host.BroadcastCellChange(keyC, edit);
                Expect(room.Host.PendingClientCount(keyB) == 2 && room.Host.PendingClientCount(keyC) == 2,
                    "改动时两个在线身份都记上（各 2）");
                TerrainHostSnapshot snap = room.Host.GetSnapshot();
                Expect(snap.PendingMarks == 2 && snap.PendingClients == 4,
                    "快照合计 = 标块 2 · 身份 4（实际 " + snap.PendingMarks + " / " + snap.PendingClients + "）");
                room.SendBye(b);
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyB) == 1 &&
                        room.Host.PendingClientCount(keyC) == 1; }, 3000),
                    "B 退出联机（Bye）→ 它在这两块上的标记全部去掉（各剩 A = 1）");
                Expect(LastLine(room.Traces).Contains("个区块的欠更新计数里移除它的标记"),
                    "移除它的全部标记写了一行日志：" + LastLine(room.Traces));

                // (e) **同一身份**重新加入 → 它什么都不欠（计数不变）；A 把这两块加载进内存后整体归零。
                // 用同一个身份（b.Identity）才叫"重进"：若换成新身份，那就等于换了一个人。
                PendingPeer c = room.AddClient("client-B2", b.Identity);
                Expect(room.Host.PendingClientCount(keyB) == 1 && room.Host.PendingClientCount(keyC) == 1,
                    "重新加入的客户端不补任何标记（仍是 " + room.Host.PendingClientCount(keyB) + " / " +
                    room.Host.PendingClientCount(keyC) + "）→ 它自己全为 0");
                Expect(room.Host.GetSnapshot().PendingClients == 2, "重新加入后合计仍是 2（只有 A 欠）");
                room.SendReport(a, keyB, room.HostWorld.DigestOf(keyB));
                room.SendReport(a, keyC, room.HostWorld.DigestOf(keyC));
                Expect(WaitFor(() => { room.Pump(); return room.Host.VerifyStateOf(keyB) == ChunkVerifyState.Verified &&
                        room.Host.VerifyStateOf(keyC) == ChunkVerifyState.Verified; }, 3000),
                    "A 上报这两块 → 主机裁决 Match（验证语义不变）");
                Expect(room.Host.PendingClientCount(keyB) == 1 && room.Host.PendingClientCount(keyC) == 1,
                    "裁决通过仍不清欠更新（两块都还是 1）");
                room.SendClientChunks(a, ClientChunkSetCodec.EncodeDelta(new[]
                {
                    new ClientChunkEntry(keyB.X, keyB.Z, true),
                    new ClientChunkEntry(keyC.X, keyC.Z, true)
                }));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyB) == 0 &&
                        room.Host.PendingClientCount(keyC) == 0; }, 3000),
                    "A 的内存集合新增这两块 → 两块都归 0（都不标注）");
                Expect(room.Host.GetSnapshot().PendingMarks == 0,
                    "0 → 没有任何标块（网页只在 n>0 时画数字；n>0 才画由页面自检证明）");

                // (d2) 链路失效（不发 Bye）：Tick 必须把它的全部标记清掉
                var keyD = new ChunkKey(3, 3);
                room.HostWorld.MakeValid(keyD);
                room.Host.BroadcastCellChange(keyD, edit);            // A 与 B2 都连着的 → 2
                Expect(room.Host.PendingClientCount(keyD) == 2, "改动时两个在线客户端都记上（2）");
                room.DropLink(c);
                room.Host.Tick();
                Expect(room.Host.PendingClientCount(keyD) == 1,
                    "链路失效（没发 Bye）→ Tick 清掉它的标记，只剩 A（1）");

                // (g) 会话 / 房间结束：整体清空（只在内存里；不落盘）
                Expect(room.Host.ClearPendingMarks() == 1, "会话结束清掉 1 个标块");
                Expect(room.Host.PendingClientCount(keyD) == 0 && room.Host.GetSnapshot().PendingClients == 0,
                    "整体清空后没有任何标记（快照合计 0）");
                Expect(LastLine(room.Traces).Contains("会话结束") && LastLine(room.Traces).Contains("不落盘"),
                    "会话结束清空写了明确日志：" + LastLine(room.Traces));
                Expect(room.Traces.Count >= 5, "计数器生命周期共写了 " + room.Traces.Count + " 行明确日志");
            }

            Section("W. 欠更新计数按**当前内存集合**结算：有这块→0 / 没有→1 / 后来加载→0 / 两身份不叠加 / 离开清掉");
            {
                using var room = new PendingRoom();
                var edit = new CellEdit(2, 3, 4, 2, 0);
                var key = new ChunkKey(10, 10);
                room.HostWorld.MakeValid(key);

                // (a0) 连接了、但当前内存集合没有这块：改动 → 1
                PendingPeer a = room.AddClient("client-A");
                Expect(room.Host.BroadcastCellChange(key, edit) == 0, "(a0) 该块没有订阅者 → 广播发出 0 份");
                Expect(room.Host.PendingClientCount(key) == 1, "(a0) A 的当前内存集合没有这块 → 计数 1");

                // (a) 订阅 / 投递成功都**不**清欠账：数字只看身份当前内存集合里有没有这块
                a.SendInterest(key, 1);
                Expect(WaitFor(() => { room.Pump(); return room.Host.SubscriberCount(key) == 1; }, 3000),
                    "(a) A 的兴趣窗口包含该块 → 主机侧订阅者 = 1");
                Expect(room.Host.BroadcastCellChange(key, edit) == 1, "(a) 改动发给了已订阅的 A（1 份）");
                Expect(room.Host.PendingClientCount(key) == 1,
                    "(a) 已订阅且投递成功 → 计数仍是 1（投递结果不参与；A 内存里还没有这块）");

                // A 用 ClientChunks 增量声明已加载 → 清零（这是本计数器唯一的"有这块"清除路径）
                room.SendClientChunks(a, ClientChunkSetCodec.EncodeDelta(
                    new[] { new ClientChunkEntry(key.X, key.Z, true) }));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(key) == 0; }, 3000),
                    "(a) A 声明当前内存已加载该块 → 计数 0（清除标记）");
                Expect(LastLine(room.ClientChunkTraces).Contains("清除欠更新标记 1 个"),
                    "(a) 增量汇总日志写明清除了几个欠更新标记：" + LastLine(room.ClientChunkTraces));

                // (b) A 当前内存集合没有的块 → 1
                var keyFar = new ChunkKey(40, 40);
                room.HostWorld.MakeValid(keyFar);
                room.Host.BroadcastCellChange(keyFar, edit);
                Expect(room.Host.PendingClientCount(keyFar) == 1, "(b) A 当前内存里没有这块 → 计数 1");

                // (c) 它后来把这块加载进内存 → 0（不是靠上报 / 裁决）
                room.SendClientChunks(a, ClientChunkSetCodec.EncodeDelta(
                    new[] { new ClientChunkEntry(keyFar.X, keyFar.Z, true) }));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyFar) == 0; }, 3000),
                    "(c) A 的内存集合新增该块 → 计数 0");

                // (d) 两个身份：A 已有这一块、B 没有 → 数字 1，不是 2（用户三区域模型的核心）
                PendingPeer b = room.AddClient("client-B");
                var keyD = new ChunkKey(20, 20);
                room.HostWorld.MakeValid(keyD);
                a.SendInterest(keyD, 1);
                Expect(WaitFor(() => { room.Pump(); return room.Host.SubscriberCount(keyD) == 1; }, 3000),
                    "(d) A 订阅该块（订阅者 1）；B 没有订阅");
                // A 的当前内存集合先声明含这块（"A 的区域"）；B 尚未加载。
                room.SendClientChunks(a, ClientChunkSetCodec.EncodeDelta(
                    new[] { new ClientChunkEntry(keyD.X, keyD.Z, true) }));
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    TerrainHostSnapshot s = room.Host.GetSnapshot();
                    for (int i = 0; i < s.Clients.Count; i++)
                        if (s.Clients[i].Id == a.Id) return s.Clients[i].MemoryChunks.Contains(keyD);
                    return false;
                }, 3000), "(d) A 的当前内存集合已包含该块");
                Expect(room.Host.BroadcastCellChange(keyD, edit) == 1, "(d) 改动只发给订阅了的 A（1 份）");
                Expect(room.Host.PendingClientCount(keyD) == 1,
                    "(d) A 已有这块、B 没有 → 数字 1（不是 2）：" + room.Host.PendingClientCount(keyD));
                room.SendClientChunks(b, ClientChunkSetCodec.EncodeDelta(
                    new[] { new ClientChunkEntry(keyD.X, keyD.Z, true) }));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyD) == 0; }, 3000),
                    "(d) B 也把这块加载进内存 → 数字 0");

                // (f) 离开（Bye）→ 它自己的标记全部移除，别人的标记不动
                var keyE = new ChunkKey(60, 60);
                room.HostWorld.MakeValid(keyE);
                room.Host.BroadcastCellChange(keyE, edit);      // A 与 B 当前内存里都没有 keyE → 各欠 1
                Expect(room.Host.PendingClientCount(keyE) == 2,
                    "(f) 两个身份的内存集合都没有这块 → 都记上（2）");
                room.SendBye(b);
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    return room.Host.PendingClientCount(keyD) == 0 && room.Host.PendingClientCount(keyE) == 1;
                }, 3000),
                    "(f) B 离开 → 它在所有区块上的标记都被移除（keyD 0 / keyE 只剩 A = 1）");
                Expect(LastLine(room.Traces).Contains("1 个区块的欠更新计数里移除它的标记"),
                    "(f) 离开清理写了明确日志：" + LastLine(room.Traces));
            }

            Section("W2. 已订阅但 CellChange 发送失败（显式背压）→ 投递失败单独计数；欠更新仍按当前内存集合 = 1");
            {
                using var room = new PendingRoom();
                var edit = new CellEdit(5, 6, 7, 3, 0);
                var key = new ChunkKey(30, 30);
                room.HostWorld.MakeValid(key);

                int stubborn = room.AddRawPeer("stubborn", "identity-stubborn", key, 1);
                Expect(WaitFor(() => { room.Pump(); return room.Host.SubscriberCount(key) == 1; }, 3000),
                    "(e) 这个端已订阅该块（订阅者 1）");

                // 它从不读 socket → 发送线程被写阻塞、队列到上限 → TrySend 显式失败（真实背压路径）
                byte[] filler = new byte[8192];
                int fills = 0;
                while (fills < 4096 && room.Server.TrySend(stubborn, TerrainMessageType.ChunkResync, filler)) fills++;
                Expect(fills < 4096, "(e) 已把该端的发送队列塞到上限（入队 " + fills + " 帧后 TrySend=false）");
                Expect(room.Server.StateOf(stubborn) == ChannelState.Backpressured,
                    "(e) 该端通道 = Backpressured（连接还在 → 不能按\"已离开\"处理）");

                long failedBefore = room.Host.GetSnapshot().CellChangesDeliveryFailed;
                Expect(room.Host.BroadcastCellChange(key, edit) == 0, "(e) 已订阅但发送失败 → 广播发出 0 份");
                Expect(room.Host.PendingClientCount(key) == 1,
                    "(e) 这个端没有声明过这块已加载 → 欠更新 = 1（发送失败只进投递失败计数，不改变欠更新）");
                Expect(room.Host.GetSnapshot().CellChangesDeliveryFailed == failedBefore + 1,
                    "(e) 投递失败计数 +1（可审计）：" + room.Host.GetSnapshot().CellChangesDeliveryFailed);
                Expect(LastLine(room.Traces).Contains("没发出去") && LastLine(room.Traces).Contains("累计投递失败"),
                    "(e) 限流写了一行明确原因：" + LastLine(room.Traces));
            }
            Section("N. 用户三区域模型（A/B）：欠更新数字 = 当前内存集合里没有该块的在线身份数");
            {
                using var room = new PendingRoom();
                var edit = new CellEdit(8, 9, 10, 2, 0);
                var keyA = new ChunkKey(100, 100);      // A 的区域
                var keyB = new ChunkKey(200, 200);      // B 的区域
                var keyA2 = new ChunkKey(101, 100);     // A 区域的第二块
                var keyNone = new ChunkKey(300, 300);   // 两个人都没有
                room.HostWorld.MakeValid(keyA);
                room.HostWorld.MakeValid(keyB);
                room.HostWorld.MakeValid(keyA2);
                room.HostWorld.MakeValid(keyNone);

                PendingPeer a = room.AddClient("client-A");
                room.SendClientChunks(a, ClientChunkSetCodec.EncodeFullSetPart(1, 0, 1,
                    new[] { new ClientChunkEntry(keyA.X, keyA.Z, true) }));
                PendingPeer b = room.AddClient("client-B");
                room.SendClientChunks(b, ClientChunkSetCodec.EncodeFullSetPart(2, 0, 1,
                    new[] { new ClientChunkEntry(keyB.X, keyB.Z, true) }));
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    TerrainHostSnapshot s = room.Host.GetSnapshot();
                    return s.Clients.Count == 2 && s.MemoryChunkCount == 2;
                }, 3000), "A 当前内存 {A 区}、B 当前内存 {B 区}（v8 整集已送达主机）");

                // (a) A 有、B 没有的块发生改动 → 数字 1（只有 B 欠）。
                // (f) 同一件事也证明"已经加载这块的 A 不会被加标记"（否则数字会是 2）。
                a.SendInterest(keyA, 1);
                Expect(WaitFor(() => { room.Pump(); return room.Host.SubscriberCount(keyA) == 1; }, 3000),
                    "(a) A 订阅了 A 区这块（订阅者 1）；B 没有");
                Expect(room.Host.BroadcastCellChange(keyA, edit) == 1, "(a) 改动只发给订阅的 A（1 份）");
                Expect(room.Host.PendingClientCount(keyA) == 1,
                    "(a) A 有这块、B 没有 → 数字 1（只有 B 欠）");
                Expect(room.Host.GetSnapshot().PendingClients == 1,
                    "(f) 已经加载这块的 A 没有被加标记（合计仍 = 1）");

                // (a2) 反方向：B 有、A 没有 → 数字 1（只有 A 欠）
                b.SendInterest(keyB, 1);
                Expect(WaitFor(() => { room.Pump(); return room.Host.SubscriberCount(keyB) == 1; }, 3000),
                    "(a2) B 订阅了 B 区这块（订阅者 1）；A 没有");
                Expect(room.Host.BroadcastCellChange(keyB, edit) == 1, "(a2) 改动只发给订阅的 B（1 份）");
                Expect(room.Host.PendingClientCount(keyB) == 1,
                    "(a2) B 有这块、A 没有 → 数字 1（只有 A 欠）");

                // (c1) B 走进 A 的区域：它的当前加载集合新增这块（增量 add）→ B 的欠账清零
                room.SendClientChunks(b, ClientChunkSetCodec.EncodeDelta(
                    new[] { new ClientChunkEntry(keyA.X, keyA.Z, true) }));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyA) == 0; }, 3000),
                    "(c) B 的当前内存集合新增 A 区这块（增量）→ 数字 0（所有人都已加载）");

                // (c2) 整集替换也走同一条清除路径：B 的新整集包含 keyA2 → 该块数字 0
                room.SendClientChunks(a, ClientChunkSetCodec.EncodeDelta(
                    new[] { new ClientChunkEntry(keyA2.X, keyA2.Z, true) }));
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    TerrainHostSnapshot s = room.Host.GetSnapshot();
                    for (int i = 0; i < s.Clients.Count; i++)
                        if (s.Clients[i].Id == a.Id) return s.Clients[i].MemoryChunks.Contains(keyA2);
                    return false;
                }, 3000), "(c2) A 的内存集合新增 A 区第二块");
                room.Host.BroadcastCellChange(keyA2, edit);
                Expect(room.Host.PendingClientCount(keyA2) == 1,
                    "(c2) A 有、B 没有 A 区第二块 → 数字 1");
                room.SendClientChunks(b, ClientChunkSetCodec.EncodeFullSetPart(3, 0, 1, new[]
                {
                    new ClientChunkEntry(keyB.X, keyB.Z, true),
                    new ClientChunkEntry(keyA.X, keyA.Z, true),
                    new ClientChunkEntry(keyA2.X, keyA2.Z, true)
                }));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyA2) == 0; }, 3000),
                    "(c2) B 的整集替换包含该块 → 数字 0");
                Expect(LastLine(room.ClientChunkTraces).Contains("清掉 1 个欠更新标记"),
                    "(c2) 整集日志写明清掉了几个欠更新标记：" + LastLine(room.ClientChunkTraces));

                // (b) 两个人都没有的块发生改动 → 数字 2
                room.Host.BroadcastCellChange(keyNone, edit);
                Expect(room.Host.PendingClientCount(keyNone) == 2,
                    "(b) 两个身份的当前内存集合都没有这块 → 数字 2（A + B）");
                Expect(room.Host.GetSnapshot().PendingMarks == 2 &&
                       room.Host.GetSnapshot().PendingClients == 3,
                    "(b) 快照合计 = 标块 2 · 身份 3（keyB 还欠 A = 1，keyNone = A+B = 2；实际 " +
                    room.Host.GetSnapshot().PendingMarks + " / " + room.Host.GetSnapshot().PendingClients + "）");

                // (e) 主机卸载这一块 → 标记与数字都不动（计数器与主机加载集合无关）
                room.HostWorld.Unload(keyNone);
                Expect(!room.HostWorld.HasContents(keyNone), "(e) 主机已卸载这块");
                Expect(room.Host.PendingClientCount(keyNone) == 2,
                    "(e) 主机卸载后欠更新数字仍是 2（标记只在内存里，不随主机区块卸载消失）");
                room.HostWorld.MakeValid(keyNone);

                // (d) A 离开联机 → 它欠的条目全部移除：keyNone 2→1、keyB 1→0
                room.SendBye(a);
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    return room.Host.PendingClientCount(keyNone) == 1 &&
                           room.Host.PendingClientCount(keyB) == 0;
                }, 3000), "(d) A 离开 → 它欠的条目移除（keyNone 2→1，keyB 1→0）");
                Expect(LastLine(room.Traces).Contains("2 个区块的欠更新计数里移除它的标记"),
                    "(d) 离开清理写明清掉了几个区块的标记：" + LastLine(room.Traces));
            }


            Section("M. 按身份（UserId）归档：重连复用同一条记录 / 不同身份不合并 / 标记按身份 / 实时地图稳定键");
            {
                using var room = new PendingRoom();
                var edit = new CellEdit(4, 5, 6, 2, 0);
                // 身份是**同一台平板**（UserId）的字符串替身；稳定键只允许是派生哈希，绝不允许出现这一整串。
                const string identityX = "verify-identity-aaaaaaaa-1111-2222-3333-444444444444";
                const string identityY = "verify-identity-bbbbbbbb-9999-8888-7777-666666666666";
                var key1 = new ChunkKey(-5, -10);     // = 实机"当前区域一个框都没有"的那块
                var key2 = new ChunkKey(-5, -9);
                room.HostWorld.MakeValid(key1);
                room.HostWorld.MakeValid(key2);

                // (a) 身份 X 连接 → 上报两块 → 真实摘要裁决
                PendingPeer a = room.AddClient("tablet", identityX);
                room.SendReport(a, key1, room.HostWorld.DigestOf(key1));
                room.SendReport(a, key2, room.HostWorld.DigestOf(key2));
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    return room.Host.VerifyStateOf(key1) == ChunkVerifyState.Verified &&
                           room.Host.VerifyStateOf(key2) == ChunkVerifyState.Verified;
                }, 3000), "(a) 身份 X 上报 2 块 → 主机用真实摘要裁决 → Verified（绿）");
                TerrainHostSnapshot snapA = room.Host.GetSnapshot();
                Expect(snapA.Clients.Count == 1 && snapA.Clients[0].Id == a.Id &&
                       snapA.Clients[0].CurrentClientId == a.Id,
                    "(a) 快照 1 条客户端、clientId 就是这条连接的句柄（" + a.Id + "）");
                string stable = snapA.Clients[0].StableKey;
                Expect(stable.Length == 17 && stable[0] == 'u',
                    "(a) 稳定键 = u + 16 位 FNV-1a 哈希（定长、可上页面）：" + stable);
                Expect(!stable.Contains(identityX) && !identityX.Contains(stable),
                    "(a) 稳定键**不是** UserId 全值（GUID 全值不上线、不进日志）");
                Expect(snapA.Clients[0].ReportedLoaded.Count == 2, "(a) 该身份上报过 2 块（实时地图的客户端区域来源）");
                bool loggedSession = false, loggedFull = false;
                for (int i = 0; i < room.SessionTraces.Count; i++)
                {
                    if (room.SessionTraces[i].Contains(stable)) loggedSession = true;
                    if (room.SessionTraces[i].Contains(identityX)) loggedFull = true;
                }
                Expect(loggedSession && !loggedFull,
                    "(a) 身份记录建立写进 SessionTrace，且**只出现稳定键 / 前 8 位**（无 GUID 全值）");

                // (b) 链路失效 + **同一身份、新 clientId** 重连（旧连接可能还没被判失效 —— 实机场景）
                PendingPeer b = room.Reconnect(a);
                room.Pump();
                Expect(b.Id != a.Id, "(b) 重连拿到新的 clientId（" + a.Id + " → " + b.Id + "）");
                TerrainHostSnapshot snapB = room.Host.GetSnapshot();
                Expect(snapB.Clients.Count == 1,
                    "(b) 同一身份只有**一个**客户端条目（不是两份互不相干的状态）：" + snapB.Clients.Count);
                Expect(snapB.Clients[0].StableKey == stable && snapB.Clients[0].Id == b.Id,
                    "(b) 稳定键不变、条目换成当前句柄（key=" + snapB.Clients[0].StableKey +
                    " · id=" + snapB.Clients[0].Id + "）");
                Expect(snapB.Clients[0].ReportedLoaded.Count == 2,
                    "(b) ReportedLoaded **沿用**（2 块，不清零）：" + snapB.Clients[0].ReportedLoaded.Count);
                Expect(room.Host.VerifyStateOf(key1) == ChunkVerifyState.Verified &&
                       room.Host.VerifyStateOf(key2) == ChunkVerifyState.Verified,
                    "(b) VerifyStateOf 仍返回重连前的裁决（绿框跟着平板走，不停在旧区域）");
                bool loggedReuse = false;
                for (int i = 0; i < room.SessionTraces.Count; i++)
                {
                    if (room.SessionTraces[i].Contains("重连") && room.SessionTraces[i].Contains(stable) &&
                        room.SessionTraces[i].Contains("不重置"))
                        loggedReuse = true;
                }
                Expect(loggedReuse, "(b) 重连复用记录写了明确一行（含稳定键与\"不重置\"）");

                // (b2) 待裁决跨重连继续（断线只是暂停，不是作废）
                var key3 = new ChunkKey(-6, -10);
                room.HostWorld.MakeValid(key3);
                uint hash3 = room.HostWorld.DigestOf(key3);
                room.HostWorld.Unload(key3);                        // 上报时主机手上没有内容 → NotReady + 待裁决
                room.SendReport(b, key3, hash3);
                Expect(WaitFor(() => { room.Pump(); return room.Host.GetSnapshot().PendingReports == 1; }, 3000),
                    "(b2) 新连接的上报 → 待裁决 = 1");
                int loadsBefore = room.HostWorld.RequestLoads;
                room.DropLink(b);
                Expect(WaitFor(() => { room.Host.Tick(); return room.Host.GetSnapshot().PendingReports == 0; }, 3000),
                    "(b2) 断线后快照待裁决 = 0（不再对着一根断线算欠账）");
                for (int i = 0; i < 20; i++) room.Host.Tick();
                Expect(room.HostWorld.RequestLoads == loadsBefore,
                    "(b2) 断线后不再向引擎重请这块（" + loadsBefore + " → " + room.HostWorld.RequestLoads + "）");
                PendingPeer c = room.Reconnect(b);
                room.Pump();
                Expect(WaitFor(() => { room.Pump(); return room.Host.GetSnapshot().PendingReports == 1; }, 3000),
                    "(b2) 同身份再回来 → 之前欠着的裁决**继续**（1，不是新的空记录）");
                Expect(room.Host.GetSnapshot().Clients[0].ReportedLoaded.Contains(key3),
                    "(b2) 新连接的上报也记在同一份身份记录里");
                room.HostWorld.MakeValid(key3);
                room.Host.Tick();
                Expect(WaitFor(() => { room.Host.Tick(); return room.Host.GetSnapshot().PendingReports == 0; }, 3000),
                    "(b2) 主机有内容后用真实主机摘要作答 → 待裁决归 0");
                Expect(room.Host.VerifyStateOf(key3) == ChunkVerifyState.Verified,
                    "(b2) key3 → Verified（跨重连仍然答的是真实摘要）");

                // (c) 离开移除欠更新标记 / 同身份重进不补
                var keyMarks = new ChunkKey(-7, -10);
                room.HostWorld.MakeValid(keyMarks);
                room.Host.BroadcastCellChange(keyMarks, edit);
                Expect(room.Host.PendingClientCount(keyMarks) == 1, "(c) 改动 → 这个身份欠 1 块");
                room.SendBye(c);
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyMarks) == 0; }, 3000),
                    "(c) 离开（Bye）→ 它在**所有**区块上的欠更新标记全部移除");
                PendingPeer c2 = room.AddClient("tablet", identityX);
                room.Pump();
                Expect(room.Host.PendingClientCount(keyMarks) == 0,
                    "(c) 同身份重新加入 → 不补任何标记（全 0，加入时的整图就是最新基线）");
                Expect(room.Host.GetSnapshot().Clients.Count == 1 && room.Host.VerifyStateOf(key1) == ChunkVerifyState.Verified,
                    "(c) 重新加入复用的还是同一条身份记录（上报/裁决仍在）");
                room.Host.BroadcastCellChange(keyMarks, edit);
                Expect(room.Host.PendingClientCount(keyMarks) == 1,
                    "(c) 再改动 → 又是 1（同一身份只算一次，重连不会重复计）");

                // (d) 不同身份 = 独立记录（绝不合并）
                PendingPeer d = room.AddClient("phone", identityY);
                var keyY = new ChunkKey(-9, -10);
                room.HostWorld.MakeValid(keyY);
                room.SendReport(d, keyY, room.HostWorld.DigestOf(keyY));
                // 等**上报也处理完**再取快照（只等条目数会在网络/泵时序上抖动：下面要断言两个身份各自的上报集合）
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    TerrainHostSnapshot waiting = room.Host.GetSnapshot();
                    if (waiting.Clients.Count != 2) return false;
                    for (int i = 0; i < waiting.Clients.Count; i++)
                    {
                        if (waiting.Clients[i].Id == d.Id)
                            return waiting.Clients[i].ReportedLoaded.Count == 1;
                    }
                    return false;
                }, 3000),
                    "(d) 第二个身份加入 → 客户端条目 2 个（两条独立记录）");
                TerrainHostSnapshot snapD = room.Host.GetSnapshot();
                ClientView tablet = null, phone = null;
                for (int i = 0; i < snapD.Clients.Count; i++)
                {
                    if (snapD.Clients[i].Id == c2.Id) tablet = snapD.Clients[i];
                    if (snapD.Clients[i].Id == d.Id) phone = snapD.Clients[i];
                }
                Expect(tablet != null && phone != null && tablet.StableKey == stable &&
                       phone.StableKey != tablet.StableKey,
                    "(d) 两个身份的稳定键不同（tablet=" + (tablet != null ? tablet.StableKey : "?") +
                    " · phone=" + (phone != null ? phone.StableKey : "?") + "）");
                Expect(tablet != null && phone != null && tablet.ReportedLoaded.Count == 3 &&
                       phone.ReportedLoaded.Count == 1 && !phone.ReportedLoaded.Contains(key1),
                    "(d) 上报集合互不合并（tablet 3 块 / phone 1 块）");
                Expect(room.Host.WatchedStableKey == tablet.StableKey &&
                       room.Host.VerifyStateOf(keyY) == ChunkVerifyState.None,
                    "(d) 被观察身份 = tablet（id 最小）；phone 上报的块不给 tablet 画框（不静默合并）");
                // v7：每个身份查**自己**的裁决（不套被观察客户端的结论）—— 页面客户端线框按它着色
                Expect(room.Host.VerifyStateOf(tablet.StableKey, key1) == ChunkVerifyState.Verified &&
                       room.Host.VerifyStateOf(tablet.StableKey, keyY) == ChunkVerifyState.None &&
                       room.Host.VerifyStateOf(phone.StableKey, keyY) == ChunkVerifyState.Verified &&
                       room.Host.VerifyStateOf(phone.StableKey, key1) == ChunkVerifyState.None,
                    "(d) VerifyStateOf(稳定键, 块)：tablet 的绿只给 tablet、phone 只给自己的块（绝不套别人的裁决）");
                Expect(room.Host.VerifyStateOf("", key1) == ChunkVerifyState.None &&
                       room.Host.VerifyStateOf("u0000000000000000", key1) == ChunkVerifyState.None,
                    "(d) 未知 / 空稳定键 → None（页面不画线框，绝不猜状态）");
                Expect(WaitFor(() => { room.Pump(); return room.Host.VerifyStateOf(keyY) == ChunkVerifyState.None; },
                        1000) && room.Host.PendingClientCount(keyMarks) == 1,
                    "(d) 手机端加入不补标记（仍只有 tablet 欠那 1 块）");

                // (e) 稳定键跨重连不变 + 实时地图 v7 载荷带 key + 每条客户端区块的状态 + 旧版本字节明确失败
                PendingPeer c3 = room.Reconnect(c2);
                room.Pump();
                TerrainHostSnapshot snapE = room.Host.GetSnapshot();
                ClientView tabletAfter = null;
                for (int i = 0; i < snapE.Clients.Count; i++)
                {
                    if (snapE.Clients[i].Id == c3.Id) tabletAfter = snapE.Clients[i];
                }
                Expect(snapE.Clients.Count == 2 && tabletAfter != null &&
                       tabletAfter.StableKey == stable && tabletAfter.ReportedLoaded.Count == 3,
                    "(e) 重连（新 clientId）后稳定键不变、条目仍只有一个（页面选角不会多一个/断掉）");

                var live = new LiveMapSnapshot
                {
                    WatchedClientId = c3.Id,
                    WatchedClientName = "tablet",
                    WatchedStableKey = stable,
                    ChunkRadius = 0,
                    View = LiveMapView.All
                };
                live.Centers.Add(new LiveMapCenter
                {
                    ClientId = -1, StableKey = LiveMapCodec.HostKey, Name = "Host", IsHost = true
                });
                live.Centers.Add(new LiveMapCenter { ClientId = c3.Id, StableKey = stable, Name = "tablet" });
                live.Clients.Add(new LiveMapClient
                {
                    ClientId = c3.Id, StableKey = stable, Name = "tablet", ReportedChunkCount = 2,
                    CurrentLoadedChunkCount = 2
                });
                live.ClientChunks.Add(new LiveMapClientChunk
                {
                    ClientId = c3.Id, StableKey = stable, X = key1.X, Z = key1.Z,
                    State = (byte)ChunkVerifyState.Verified
                });
                live.ClientChunks.Add(new LiveMapClientChunk
                {
                    ClientId = c3.Id, StableKey = stable, X = key2.X, Z = key2.Z,
                    State = (byte)ChunkVerifyState.ReVerified
                });
                live.Chunks.Add(new LiveMapChunk
                {
                    X = key1.X, Z = key1.Z, State = (byte)ChunkVerifyState.Verified
                });
                List<byte[]> batches = LiveMapCodec.EncodeBatches(live, 4321);
                Expect(batches.Count >= 1 && batches[0][0] == 8 && LiveMapCodec.Version == 8,
                    "(e) 载荷版本 = v8（首字节就是版本，旧消费者在版本字节上明确失败）");
                Expect(LiveMapCodec.TryDecodeBatch(batches[0], out LiveMapBatch decoded),
                    "(e) v8 载荷可以被同一份 codec 解回来");
                bool centersKeyed = false;
                for (int i = 0; i < decoded.Header.Centers.Count; i++)
                {
                    if (decoded.Header.Centers[i].StableKey == LiveMapCodec.HostKey) centersKeyed = true;
                }
                Expect(decoded.Header.WatchedStableKey == stable &&
                       decoded.Header.Clients.Count == 1 && decoded.Header.Clients[0].StableKey == stable &&
                       decoded.ClientChunks.Count == 2 && decoded.ClientChunks[0].StableKey == stable &&
                       decoded.ClientChunks[1].StableKey == stable && centersKeyed,
                    "(e) 稳定键在 watched / players(centers) / clients / 区块条目里原样往返");
                Expect(decoded.ClientChunks[0].State == (byte)ChunkVerifyState.Verified &&
                       decoded.ClientChunks[1].State == (byte)ChunkVerifyState.ReVerified &&
                       decoded.ClientChunks[0].X == key1.X && decoded.ClientChunks[1].Z == key2.Z,
                    "(e) v8：每条客户端区块条目仍携带该身份自己的校验状态（绿 2 / 蓝 4），坐标不错位");
                Expect(decoded.ClientChunks.Count == 2 &&
                       decoded.Header.Clients[0].CurrentLoadedChunkCount == decoded.ClientChunks.Count &&
                       decoded.Header.Clients[0].ReportedChunkCount == 2,
                    "(e) 同一身份的当前内存条目合并到同一个键下（2 条，不因重连分裂），头部 currentLoaded = 2");
                string payloadText = System.Text.Encoding.ASCII.GetString(batches[0]);
                Expect(!payloadText.Contains(identityX) && !payloadText.Contains(identityY) &&
                       !payloadText.Contains("aaaaaaaa-1111"),
                    "(e) 载荷里没有 UserId 全值（只有派生稳定键）");
                byte[] wrongVersion = (byte[])batches[0].Clone();
                wrongVersion[0] = 6;
                Expect(!LiveMapCodec.TryDecodeBatch(wrongVersion, out _),
                    "(e) 版本字节 v6 → 解码明确失败（旧消费者不会把新增的状态字节当旧字段猜）");
                wrongVersion[0] = 5;
                Expect(!LiveMapCodec.TryDecodeBatch(wrongVersion, out _),
                    "(e) 版本字节 v5 → 解码同样明确失败");
                wrongVersion[0] = 7;
                Expect(!LiveMapCodec.TryDecodeBatch(wrongVersion, out _),
                    "(e) 版本字节 v7（上一版载荷）→ 解码明确失败：头部少了 currentLoaded 字段，绝不按旧布局猜");

                // (e3) v8：四种验证状态逐字节往返（同一份快照里 1/2/3/4 各一条，顺序与坐标不错位）
                var liveStates = new LiveMapSnapshot { View = LiveMapView.All };
                liveStates.Clients.Add(new LiveMapClient
                {
                    ClientId = 9, StableKey = stable, Name = "tablet", ReportedChunkCount = 4,
                    CurrentLoadedChunkCount = 4
                });
                for (int i = 0; i < 4; i++)
                {
                    liveStates.ClientChunks.Add(new LiveMapClientChunk
                    {
                        ClientId = 9, StableKey = stable, X = 40 + i, Z = -40 - i, State = (byte)(i + 1)
                    });
                }
                List<byte[]> stateBatches = LiveMapCodec.EncodeBatches(liveStates, 777);
                LiveMapBatch decodedStates = null;
                bool statesRoundTrip = stateBatches.Count == 1 && stateBatches[0][0] == 8 &&
                    LiveMapCodec.TryDecodeBatch(stateBatches[0], out decodedStates) &&
                    decodedStates.ClientChunks.Count == 4;
                for (int i = 0; statesRoundTrip && i < 4; i++)
                {
                    statesRoundTrip = decodedStates.ClientChunks[i].State == (byte)(i + 1) &&
                        decodedStates.ClientChunks[i].X == 40 + i && decodedStates.ClientChunks[i].Z == -40 - i;
                }
                Expect(statesRoundTrip,
                    "(e3) 客户端区块状态 1/2/3/4 在 v8 载荷里逐条原样往返（黄/绿/红/蓝，不串行）");

                // (e2) 直接跑**工具真实的 JSON 生产者**（SuMPLiveMap.Program.BuildStateJson）：
                //      验证 players[].key / clients[].key / 「居中角色」按稳定键选、重连不分裂条目。
                Type programType = Type.GetType("SuMPLiveMap.Program", throwOnError: false);
                Expect(programType != null, "(e2) 找到工具的真实 JSON 生产者 SuMPLiveMap.Program");
                if (programType != null)
                {
                    FieldInfo snapField = programType.GetField("s_snapshot",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    FieldInfo selectField = programType.GetField("s_selectedCenterKey",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo buildJson = programType.GetMethod("BuildStateJson",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    Expect(snapField != null && selectField != null && buildJson != null,
                        "(e2) 定位到 s_snapshot / s_selectedCenterKey / BuildStateJson");
                    if (snapField != null && selectField != null && buildJson != null)
                    {
                        object previousSnap = snapField.GetValue(null);
                        object previousSelection = selectField.GetValue(null);
                        try
                        {
                            // 与工具 OnFrame 的合批一致：把各批的 chunks / clientChunks 合并进头部快照
                            decoded.Header.Chunks.AddRange(decoded.Chunks);
                            decoded.Header.ClientChunks.AddRange(decoded.ClientChunks);
                            snapField.SetValue(null, decoded.Header);
                            selectField.SetValue(null, stable);
                            string feed = (string)buildJson.Invoke(null, null);
                            string feedPath = Path.Combine(Path.GetTempPath(),
                                "su-multiplayer-tsverify-feed-e2.json");
                            File.WriteAllText(feedPath, feed);
                            Console.WriteLine("  （e2 的 feed 已写到系统临时目录：" + feedPath + "）");
                            Expect(feed.Contains("\"payloadVersion\":8"), "(e2) feed 的 payloadVersion = 8");
                            Expect(feed.Contains("{\"id\":" + c3.Id + ",\"key\":\"" + stable + "\"") &&
                                   feed.Contains("{\"key\":\"" + stable + "\",\"id\":" + c3.Id),
                                "(e2) players[] / clients[] 同时带当前 clientId 与稳定键");
                            Expect(feed.Contains("\"center\":{\"key\":\"" + stable + "\""),
                                "(e2) 「居中角色」按稳定键输出（center.key）");
                            Expect(feed.Contains("\"reported\":2") && feed.Contains("\"currentLoaded\":2") &&
                                   feed.Contains("\"merged\":2") && feed.Contains("\"complete\":true"),
                                "(e2) clients[] 带 reported/currentLoaded/merged/complete（reported=2 会话累计、currentLoaded=2 当前内存、2/2 完整），chunk 集合按 key 合并");
                            Expect(feed.Contains("[[-5,-10,2],[-5,-9,4]]"),
                                "(e2) clients[].chunks 条目 = [cx,cz,s]（v8：当前内存集合，每条自带该身份的验证状态）");
                            Expect(CountOccurrences(feed, "\"reported\":") == 1 &&
                                   CountOccurrences(feed, "\"currentLoaded\":") == 1,
                                "(e2) 该身份在 clients[] 里只有**一个**条目（重连前基线）");

                            // 模拟"同一身份重连拿到新 clientId"：只改 id、key 不变 → 选择/合并都不能变
                            int reconnectedId = c3.Id + 100;
                            decoded.Header.WatchedClientId = reconnectedId;
                            decoded.Header.Clients[0].ClientId = reconnectedId;
                            for (int i = 0; i < decoded.Header.ClientChunks.Count; i++)
                                decoded.Header.ClientChunks[i].ClientId = reconnectedId;
                            for (int i = 0; i < decoded.Header.Centers.Count; i++)
                            {
                                if (decoded.Header.Centers[i].StableKey == stable)
                                    decoded.Header.Centers[i].ClientId = reconnectedId;
                            }
                            string feedAfter = (string)buildJson.Invoke(null, null);
                            Expect(feedAfter.Contains("\"center\":{\"key\":\"" + stable + "\""),
                                "(e2) 重连（clientId 变了）后「居中角色」仍按稳定键命中（选择不会断）");
                            Expect(feedAfter.Contains("{\"id\":" + reconnectedId + ",\"key\":\"" + stable + "\"") &&
                                   feedAfter.Contains("{\"key\":\"" + stable + "\",\"id\":" + reconnectedId),
                                "(e2) 条目里的 id = **当前** clientId、key = 稳定键（id 只是传输句柄）");
                            Expect(feedAfter.Contains("\"reported\":2") && feedAfter.Contains("\"currentLoaded\":2") &&
                                   feedAfter.Contains("\"merged\":2") && feedAfter.Contains("\"complete\":true") &&
                                   CountOccurrences(feedAfter, "\"reported\":") == 1,
                                "(e2) 重连后仍是 1 个完整条目、2 块（不按 clientId 分裂成两条）");
                        }
                        finally
                        {
                            snapField.SetValue(null, previousSnap);
                            selectField.SetValue(null, previousSelection);
                        }
                    }
                }

                // 源码守卫：页面选角 / 工具输出 / codec 版本都必须按稳定键（不是 clientId）
                string htmlPath = ModPath("doc", "live-map.html");
                string programPath = ModPath("Tools", "LiveMap", "Program.cs");
                string liveMapPath = ModPath("Terrain", "LiveMap.cs");
                Expect(File.Exists(htmlPath) && File.Exists(programPath) && File.Exists(liveMapPath),
                    "(e) 找到页面 / 工具 / codec 源码");
                string htmlText = File.ReadAllText(htmlPath);
                Expect(htmlText.Contains("String(regs[j].key) === String(sel.key)") &&
                       htmlText.Contains("payloadVersion !== 8") &&
                       htmlText.Contains("selectedKey = sel.key"),
                    "(e) 页面：视图作用域/选角按稳定键；页面版本守卫 v8（map-dev 已升 v8；本项只记录页面现状，页面不归本任务改）");
                Expect(htmlText.Contains("function drawClientWireframes") &&
                       htmlText.Contains("WIRE_INSET_STEP") &&
                       htmlText.Contains("客户端区块<b>只画线框、不填地形</b>") &&
                       !htmlText.Contains("CLIENT_RGB"),
                    "(e) 页面：客户端区块只画线框（四色按 s）、地形只填主机 chunks[]，品红区域底色已删除");
                Expect(htmlText.Contains("chunks:[[cx,cz,s]") || htmlText.Contains("chunks:[[cx,cz,s]…]"),
                    "(e) 页面契约写明 clients[].chunks 条目 = [cx,cz,s]");
                string programText = File.ReadAllText(programPath);
                Expect(programText.Contains("s_selectedCenterKey") && programText.Contains("key="),
                    "(e) 工具：「居中角色」按稳定键（s_selectedCenterKey，/center?key=）选");
                Expect(programText.Contains(".Append(',').Append(list[c].State)"),
                    "(e) 工具：/state 原样透传每条客户端区块的状态（[cx,cz,s]）");
                string modulePath = ModPath("Modules", "TerrainSyncModule.cs");
                Expect(File.Exists(modulePath), "(e) 找到实时地图构建模块 TerrainSyncModule.cs");
                if (File.Exists(modulePath))
                {
                    string moduleText = File.ReadAllText(modulePath);
                    Expect(moduleText.Contains("m_host.VerifyStateOf(view.StableKey, key)") &&
                           moduleText.Contains("State = (byte)"),
                        "(e) 模块：每条客户端区块用**该身份稳定键**查自己的裁决，并写进 v8 的 State（不套被观察客户端）");
                }
                string codecText = File.ReadAllText(liveMapPath);
                Expect(codecText.Contains("public const int Version = 8"),
                    "(e) LiveMapCodec.Version = 8（wire 形状变了就换版本）");
                Expect(programText.Contains("client.CurrentLoadedChunkCount") &&
                       programText.Contains("currentLoaded"),
                    "(e) 工具：/state 输出 clients[].currentLoaded（当前内存区块数，v8 新增）");

                // (f) Hello 没带身份（旧构建 / UserId 不可用）：明确报错 + 按 clientId 单独一条（退化，不合并）
                int problemsBefore = room.SessionProblems.Count;
                PendingPeer legacy = room.AddClient("legacy", string.Empty);
                Expect(room.SessionProblems.Count == problemsBefore + 1 &&
                       room.SessionProblems[room.SessionProblems.Count - 1].Contains("退化路径") &&
                       room.SessionProblems[room.SessionProblems.Count - 1].Contains("不与任何其他端合并"),
                    "(f) 身份缺失 → 一行明确错误（退化路径，说明重连不延续）：" +
                    (room.SessionProblems.Count > 0 ? room.SessionProblems[room.SessionProblems.Count - 1] : "(空)"));
                var keyL = new ChunkKey(-11, -10);
                room.HostWorld.MakeValid(keyL);
                room.SendReport(legacy, keyL, room.HostWorld.DigestOf(keyL));
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    TerrainHostSnapshot probe = room.Host.GetSnapshot();
                    for (int i = 0; i < probe.Clients.Count; i++)
                        if (probe.Clients[i].Id == legacy.Id) return probe.Clients[i].ReportedLoaded.Count == 1;
                    return false;
                }, 3000), "(f) 退化记录照常记账（该连接的 1 块上报进它自己的条目）");
                TerrainHostSnapshot snapF = room.Host.GetSnapshot();
                ClientView legacyView = null;
                for (int i = 0; i < snapF.Clients.Count; i++)
                {
                    if (snapF.Clients[i].Id == legacy.Id) legacyView = snapF.Clients[i];
                }
                Expect(legacyView != null && legacyView.StableKey == ("c" + legacy.Id),
                    "(f) 退化记录的键 = c+clientId（显式按连接，不是身份）：" +
                    (legacyView != null ? legacyView.StableKey : "(缺)"));
                bool degradedMerged = false;
                for (int i = 0; i < snapF.Clients.Count; i++)
                {
                    if (snapF.Clients[i].Id == legacy.Id) continue;
                    if (snapF.Clients[i].StableKey == legacyView.StableKey ||
                        snapF.Clients[i].ReportedLoaded.Contains(keyL))
                        degradedMerged = true;
                }
                Expect(!degradedMerged, "(f) 退化记录不并进任何真实身份记录（也没有别人的上报混进来）");
                PendingPeer legacy2 = room.Reconnect(legacy);
                room.Pump();
                TerrainHostSnapshot snapF2 = room.Host.GetSnapshot();
                ClientView legacyAfter = null;
                for (int i = 0; i < snapF2.Clients.Count; i++)
                {
                    if (snapF2.Clients[i].Id == legacy2.Id) legacyAfter = snapF2.Clients[i];
                }
                Expect(legacyAfter != null && legacyAfter.StableKey == ("c" + legacy2.Id) &&
                       legacyAfter.ReportedLoaded.Count == 0,
                    "(f) 退化记录按 clientId 生命周期：重连得到新记录（上报不延续，这正是身份缺失的后果）");
                // 退化重连本身也必须再报一行（新 clientId = 新记录）；Debug 端则**不该**再报
                Expect(room.SessionProblems.Count == problemsBefore + 2,
                    "(f) 退化重连又写了一行明确错误（新连接 = 新退化记录）：" + room.SessionProblems.Count);
                int problemsAfterDegraded = room.SessionProblems.Count;

                // (g) Debug 只读端：没有校验状态，发来上报也**明确忽略**（不是静默建状态）
                PendingPeer debug = room.AddClient("livemap-tool", null, ClientKind.Debug);
                TerrainHostSnapshot snapG = room.Host.GetSnapshot();
                ClientView debugView = null;
                for (int i = 0; i < snapG.Clients.Count; i++)
                {
                    if (snapG.Clients[i].Id == debug.Id) debugView = snapG.Clients[i];
                }
                Expect(debugView != null && debugView.Kind == ClientKind.Debug &&
                       debugView.StableKey.Length == 0 && debugView.ReportedLoaded.Count == 0,
                    "(g) Debug 端不建身份记录、也没有任何校验状态");
                int unresolvedBefore = room.Unresolved.Count;
                room.SendReport(debug, keyL, 1u);
                Expect(WaitFor(() => { room.Pump(); return room.Unresolved.Count > unresolvedBefore; }, 3000) &&
                       room.Unresolved[room.Unresolved.Count - 1].Contains("Debug 只读端不参与校验"),
                    "(g) Debug 端发来摘要上报 → 一行明确忽略原因（不猜、不建状态）");
                Expect(room.SessionProblems.Count == problemsAfterDegraded,
                    "(g) Debug 端不进\"身份缺失\"的退化路径（它本来就不参与校验）");

                // 房间 / 会话结束：身份记录整体清空（只在内存里）
                Expect(room.Host.ClearPendingMarks() == 1, "会话结束清掉 1 个欠更新标块（tablet 那一块）");
                Expect(room.SessionTraces[room.SessionTraces.Count - 1].Contains("身份会话记录整体清空"),
                    "会话结束清空身份记录写了明确日志：" + LastLine(room.SessionTraces));
                TerrainHostSnapshot cleared = room.Host.GetSnapshot();
                int stillReported = 0;
                for (int i = 0; i < cleared.Clients.Count; i++) stillReported += cleared.Clients[i].ReportedLoaded.Count;
                Expect(stillReported == 0 && room.Host.VerifyStateOf(key1) == ChunkVerifyState.None,
                    "清空后不再有任何按身份归档的上报/裁决状态（下次加入从头开始）");
            }

            // ------------------------------------------------------------------ J. 玩家位置快照（客户端 → 主机）
            //
            // 这一节锁的是**实机回归**：主机不再应用收到的位置快照（角色卡在最后一个已应用的位置上）。
            // 真机证据（两端 Game.log）显示：对端链路在 07:33:21 后黑掉 → 客户端 posSent 冻在 204125、
            // 主机 posApplied 冻在 203861、两端 4 小时里一个字节都没再交换；而死连接上的 TrySend
            // 仍然返回 true，所以上层以为"一直在发"，也永远不会去重连。
            Section("J. 位置快照（客户端→主机）：真实 TCP 上每份都被应用（位置/旋转/视线角）");
            {
                using var room = new Room(new TerrainSyncOptions(), withSyncClient: true);
                room.Sync.SendHello("client-H", "identity-H", false);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                Expect(room.Host.ClientCount == 1, "客户端握手完成（主机侧客户端 1 个）");
                Expect(room.Host.GetSnapshot().Clients.Count == 1 &&
                       !room.Host.WasRejoin(room.Host.GetSnapshot().Clients[0].Id),
                    "首次加入的 Hello 没有重连标记 → WasRejoin=false（主机会照常下发整图）");

                // 主机侧"联机角色"的替身：解码与应用走的是**同一份生产代码**
                // （PlayerPositionCodec 真编解码 + RemotePlayerPositionState.Apply + 表现层把权威位置写进身体）。
                var body = new Vector3(-79.6f, 78.4f, 45.8f);   // = 实机里卡住的那个位置
                var state = new RemotePlayerPositionState { ClientId = 0 };
                int applied = 0;
                double clock = 0.0;
                string decodeError = null;
                var appliedPosition = new List<Vector3>();
                var appliedRotation = new List<Quaternion>();
                var appliedLook = new List<Vector2>();

                int PumpPositions()
                {
                    int n = 0;
                    while (room.HostInbox.TryDequeue(out ReceivedFrame frame))
                    {
                        room.Host.ProcessFrame(frame);
                        if (frame.Type != TerrainMessageType.PlayerPosition) continue;
                        if (!PlayerPositionCodec.TryDecodeSingle(frame.Payload, out int senderId,
                            out PlayerPositionSnapshot snapshot, out string error))
                        {
                            decodeError = error;
                            continue;
                        }
                        clock += 1.0 / 32.0;
                        state.Apply(in snapshot, 1f / 32f, clock);
                        body = state.Position;      // UpdateRemotePresentations 的"首帧/大误差直接对齐"分支
                        appliedPosition.Add(state.Position);
                        appliedRotation.Add(state.Rotation);
                        appliedLook.Add(state.LookAngles);
                        applied++;
                        n++;
                    }
                    return n;
                }

                var sentPosition = new List<Vector3>();
                var sentRotation = new List<Quaternion>();
                var sentLook = new List<Vector2>();
                int sendFailures = 0;
                byte[] lastPayload = null;
                for (int i = 1; i <= 20; i++)
                {
                    var position = new Vector3(-154.1f - i * 0.25f, 76f + i * 0.01f, -20f + i * 0.5f);
                    var rotation = new Quaternion(0f, i * 0.01f, 0f, 1f);
                    var look = new Vector2(i * 0.02f, -i * 0.01f);
                    sentPosition.Add(position);
                    sentRotation.Add(rotation);
                    sentLook.Add(look);
                    var snapshot = new PlayerPositionSnapshot(i, position, rotation, Vector3.Zero,
                        look, isCrouching: false, isFlying: false, isGrounded: true);
                    lastPayload = PlayerPositionCodec.EncodeSingle(0, in snapshot);
                    if (!room.Client.TrySend(TerrainMessageType.PlayerPosition, lastPayload)) sendFailures++;
                }
                // 帧是异步到达的（网络线程 → 主线程泵），所以按"等到全部到齐"来断言，而不是发一份立刻查一份。
                Expect(WaitFor(() => { PumpPositions(); return applied >= 20; }, 5000),
                    "20 份位置快照全部到达并被应用（posApplied=" + applied + "；设备上是冻结的 203861）");
                Expect(sendFailures == 0, "20 次发送都成功入队（0 次发送失败）");
                Expect(decodeError == null,
                    "全部被真解码器解开" + (decodeError == null ? "" : "（失败：" + decodeError + "）"));
                int offPosition = 0, offRotation = 0, offLook = 0;
                for (int i = 0; i < 20 && i < appliedPosition.Count; i++)
                {
                    if (Vector3.DistanceSquared(appliedPosition[i], sentPosition[i]) > 1e-6f) offPosition++;
                    if (!SameRotation(appliedRotation[i], sentRotation[i])) offRotation++;
                    if (!SameLook(appliedLook[i], sentLook[i])) offLook++;
                }
                Expect(offPosition == 0, "主机按顺序应用了每一份的**位置**（20 份 0 次偏差）");
                Expect(offRotation == 0, "每一份的**旋转**（机身 yaw）都应用了");
                Expect(offLook == 0, "每一份的**视线角**（look）都应用了");
                Expect(state.ServerTick == 20 && Vector3.DistanceSquared(body, sentPosition[19]) < 1e-6f,
                    "主机记录的最新快照序号/位置 = 第 20 份（角色身体跟到客户端真实位置，陈旧判定基准）");

                Section("J2. 链路失效：死连接上的发送必须如实失败（posSent 不能凭空涨、也不能永不重连）");
                room.Server.Dispose();     // 真实服务端关闭所有 peer = 实机里的"链路失效/对端掉线"
                bool noticed = WaitFor(() => room.Client.State == ChannelState.Failed ||
                    room.Client.State == ChannelState.Disconnected, 3000);
                Expect(noticed, "客户端通道自己发现了断链（State=" + room.Client.State + "）");
                bool sentOnDeadLink = room.Client.TrySend(TerrainMessageType.PlayerPosition, lastPayload);
                Expect(!sentOnDeadLink,
                    "死连接上 TrySend 返回 false（修复前返回 true：posSent 继续涨、主机一个字节都收不到，" +
                    "tick 里的看门狗也因此永远不拆链重连 —— 实机位置冻结 4 小时的直接原因）");
            }

            Section("J3. 客户端断链后重连：新连接重新握手（alreadyHasWorld）→ 位置快照继续被应用 + 主机摘掉死连接");
            {
                using var room = new Room(new TerrainSyncOptions(), withSyncClient: false);
                var lost = new List<string>();
                room.Host.ClientLost += (id, reason) => { lock (lost) lost.Add(id + ":" + reason); };

                var state = new RemotePlayerPositionState { ClientId = 0 };
                var body = Vector3.Zero;
                int applied = 0;
                double clock = 0.0;

                int PumpPositions()
                {
                    int n = 0;
                    while (room.HostInbox.TryDequeue(out ReceivedFrame frame))
                    {
                        room.Host.ProcessFrame(frame);
                        if (frame.Type != TerrainMessageType.PlayerPosition) continue;
                        if (!PlayerPositionCodec.TryDecodeSingle(frame.Payload, out int senderId,
                            out PlayerPositionSnapshot snapshot, out string error)) continue;
                        clock += 1.0 / 32.0;
                        state.Apply(in snapshot, 1f / 32f, clock);
                        body = state.Position;
                        applied++;
                        n++;
                    }
                    return n;
                }

                bool SendPosition(TcpTerrainClient client, int tick, float offset)
                {
                    var snapshot = new PlayerPositionSnapshot(tick,
                        new Vector3(-154.1f + offset, 76f, offset * 2f), new Quaternion(0f, 0f, 0f, 1f),
                        Vector3.Zero, new Vector2(0.1f, -0.2f), false, false, true);
                    byte[] payload = PlayerPositionCodec.EncodeSingle(0, in snapshot);
                    if (!client.TrySend(TerrainMessageType.PlayerPosition, payload)) return false;
                    return WaitFor(() => { PumpPositions(); return applied >= tick; }, 2000);
                }

                // 第一条连接：Hello 用**旧格式**（不带重连标记）—— 旧构建 / 首次加入就是这条路
                room.SendHello("client-H3");
                Expect(WaitFor(() => { PumpPositions(); return room.Host.ClientCount > 0; }, 2000),
                    "第一条连接完成握手");
                int firstId = room.Host.GetSnapshot().Clients[0].Id;
                Expect(!room.Host.WasRejoin(firstId), "旧格式 Hello（字段缺失）→ WasRejoin=false（语义不变）");
                Expect(SendPosition(room.Client, 1, 1f) && SendPosition(room.Client, 2, 2f) &&
                       SendPosition(room.Client, 3, 3f),
                    "第一条连接发的 3 份快照都被主机应用（posApplied=" + applied + "）");
                Expect(Vector3.DistanceSquared(body, new Vector3(-154.1f + 3f, 76f, 6f)) < 1e-6f,
                    "角色身体跟到第 3 份的位置");

                // 对端掉线（模块里由 tick 的看门狗发现并拆链）
                room.Client.Dispose();
                Expect(WaitFor(() => room.Server.StateOf(firstId) == ChannelState.Failed ||
                    room.Server.StateOf(firstId) == ChannelState.Disconnected, 3000),
                    "主机发现这条连接已经不可达（State=" + room.Server.StateOf(firstId) + "）");
                room.Host.Tick();
                Expect(lost.Count == 1 && lost[0].StartsWith(firstId + ":"),
                    "主机 Tick 抛出 ClientLost（一次，带原因 → 上层据此摘掉联机角色）" +
                    (lost.Count > 0 ? "：" + lost[0] : "：一次都没有"));
                Expect(room.Host.GetSnapshot().Clients.Count == 0,
                    "死连接从客户端表里摘掉（clients=0；修复前它会一直算作在线客户端）");

                // 第二条连接 = 模块看门狗重连时做的事：新 TcpTerrainClient + 新 Hello
                var second = new TcpTerrainClient("127.0.0.1", room.Server.Port);
                try
                {
                    Expect(second.Connect(3000), "重连：新连接建立");
                    second.FrameReceived += frame => room.HostInbox.Enqueue(frame);
                    // Hello 末尾追加"本端已经有过这个世界"（= 重连；主机据此不再重发整图）
                    var writer = new PayloadWriter();
                    writer.Byte((byte)ClientKind.Player).Text("client-H3").Byte(1).Byte(1).Int32(2)
                        .Text("identity-H3").Byte(1).Text(Room.SessionId).Int64(0);
                    second.TrySend(TerrainMessageType.Hello, writer.ToArray());
                    Expect(WaitFor(() => { PumpPositions(); return room.Host.ClientCount > 0; }, 3000),
                        "重连握手到达主机（主机侧重新登记这个客户端）");
                    TerrainHostSnapshot snap = room.Host.GetSnapshot();
                    Expect(snap.Clients.Count == 1 && snap.Clients[0].Id != firstId,
                        "新连接是新 clientId（旧角色已被 ClientLost 清掉，不会出现两个角色）");
                    Expect(room.Host.WasRejoin(snap.Clients[0].Id),
                        "重连的 Hello 带了 alreadyHasWorld → WasRejoin=true（世界传输不再重发整图）");
                    Expect(SendPosition(second, 4, 4f) && SendPosition(second, 5, 5f) && SendPosition(second, 6, 6f),
                        "重连之后位置快照继续被应用（posApplied=" + applied + "，主机恢复跟随客户端）");
                    Expect(Vector3.DistanceSquared(body, new Vector3(-154.1f + 6f, 76f, 12f)) < 1e-6f,
                        "角色身体跟到重连后第 6 份的位置");
                }
                finally
                {
                    try { second.Dispose(); } catch (Exception) { }
                }
            }

            Section("K. 窗口不再刷黄框：没上报的块 s=0（不画框）；黄框块数 == 待裁决；上报的块 黄→绿");
            {
                var options = new TerrainSyncOptions();
                using var room = new Room(options, withSyncClient: false);
                room.SendHello("client-K");
                room.SendInterest(new ChunkKey(0, 0), 3);        // 方形兴趣窗口 7×7 = 49 块
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                // 主机把整个方形窗口的内容都准备好（= 主机按客户端可见范围生成地形后手上都有内容）
                var window = new List<ChunkKey>();
                for (int x = -3; x <= 3; x++)
                {
                    for (int z = -3; z <= 3; z++)
                    {
                        var key = new ChunkKey(x, z);
                        window.Add(key);
                        room.HostWorld.MakeValid(key);
                    }
                }

                // 客户端只上报其中 1 块，且上报时主机手上没有内容 → 延迟裁决（这才是真正的 Requested）
                var reported = new ChunkKey(0, 0);
                uint expected = room.HostWorld.DigestOf(reported);
                room.HostWorld.Unload(reported);
                room.SendReport(reported, expected);
                Expect(room.WaitVerdicts(1, 3000) && room.Verdicts()[0].Kind == DigestVerdict.NotReady,
                    "唯一一条上报 → NotReady（主机当时没有内容，裁决延迟）");
                Expect(room.Host.GetSnapshot().PendingReports == 1, "待裁决 = 1");

                room.HostWorld.MakeValid(reported);              // 内容到了，但还没 Tick → 仍"上报过、未裁决"

                int none = 0, requested = 0;
                for (int i = 0; i < window.Count; i++)
                {
                    ChunkVerifyState s = room.Host.VerifyStateOf(window[i]);
                    if (s == ChunkVerifyState.None) none++;
                    else if (s == ChunkVerifyState.Requested) requested++;
                }
                Expect(none == 48 && requested == 1,
                    "窗口 49 块里只有上报过的那 1 块有状态（黄），其余 48 块 = None（s=0 不画框）：none=" +
                    none + " requested=" + requested);
                Expect(requested == room.Host.GetSnapshot().PendingReports,
                    "黄框块数 == 待裁决条数（" + requested + " == " +
                    room.Host.GetSnapshot().PendingReports + "）→ 没有窗口污染");
                ChunkKey[] virgin = { new ChunkKey(-3, -3), new ChunkKey(3, 3), new ChunkKey(-3, 3), new ChunkKey(3, -3) };
                int virginNone = 0;
                for (int i = 0; i < virgin.Length; i++)
                    if (room.Host.VerifyStateOf(virgin[i]) == ChunkVerifyState.None) virginNone++;
                Expect(virginNone == virgin.Length,
                    "方形窗口的四个角 = 客户端圆形可见范围之外、它永远不上报 → 一律 None" +
                    "（修复前这些块会一直黄着）");

                room.Host.Tick();
                Expect(room.WaitVerdicts(2, 3000), "主机 Tick 补了真实裁决");
                List<Verdict> verdicts = room.Verdicts();
                Expect(verdicts[verdicts.Count - 1].Kind == DigestVerdict.Match &&
                       verdicts[verdicts.Count - 1].HostHash == expected, "最后一条 = Match + 真实主机摘要");
                Expect(room.Host.VerifyStateOf(reported) == ChunkVerifyState.Verified,
                    "上报过的块 黄 → 绿（Verified）");
                Expect(room.Host.VerifyStateOf(virgin[0]) == ChunkVerifyState.None,
                    "裁决完成后角落仍是 None（不会因为「有内容了」就冒出一片框）");
                Expect(room.Host.GetSnapshot().PendingReports == 0, "待裁决清零 = 黄框清零（0 == 0）");

                // 上报过的块即使离开兴趣窗口也保留结论（否则"上报过"的区域会拿不到框）
                var outside = new ChunkKey(6, 0);
                room.HostWorld.MakeValid(outside);
                room.SendReport(outside, room.HostWorld.DigestOf(outside));
                Expect(WaitFor(() => { room.Pump(); return room.Host.VerifyStateOf(outside) == ChunkVerifyState.Verified; }, 3000),
                    "窗口外的块上报 → 同样被裁决（Verified）");
                room.SendInterest(new ChunkKey(0, 0), 1);        // 窗口收缩到 3×3：(6,0) 出窗
                Expect(WaitFor(() =>
                {
                    room.PumpInbox();
                    TerrainHostSnapshot shrunk = room.Host.GetSnapshot();
                    return shrunk.Clients.Count == 1 && shrunk.Clients[0].WindowChunks == 9;
                }, 3000), "兴趣窗口收缩到 3×3（主机侧 WindowChunks=9）");
                Expect(room.Host.VerifyStateOf(outside) == ChunkVerifyState.Verified &&
                       room.Host.VerifyStateOf(reported) == ChunkVerifyState.Verified,
                    "窗口移动/收缩后，上报过的块的结论不被丢弃（仍 Verified）");
                Expect(room.Host.VerifyStateOf(new ChunkKey(-3, -3)) == ChunkVerifyState.None,
                    "没上报过的块始终 None（窗口怎么动都不给框）");
            }

            Section("L. 同步范围跟随客户端声明的可见范围：内容距离 = radius*16（不再是硬编码 64）");
            {
                int bad = 0;
                for (int radius = 1; radius <= TerrainSyncRange.MaxRadius; radius++)
                {
                    TerrainSyncRange.Distances(radius, out float visibility, out float contentDistance);
                    if (Math.Abs(visibility - radius * 16f) > 1e-3f) bad++;
                    if (Math.Abs(contentDistance - visibility) > 1e-3f) bad++;
                }
                Expect(bad == 0, "radius 1..32 → 可见范围 = 内容距离 = radius*16（0 处偏差）");
                TerrainSyncRange.Distances(8, out float vis8, out float content8);
                Expect(Math.Abs(vis8 - 128f) < 1e-3f && Math.Abs(content8 - 128f) < 1e-3f,
                    "实机口径：radius 8 → 可见范围 128 · 内容距离 128（修复前内容距离是 64）");
                TerrainSyncRange.Distances(0, out float visMin, out float contentMin);
                Expect(Math.Abs(visMin - 16f) < 1e-3f && Math.Abs(contentMin - 16f) < 1e-3f,
                    "radius 0 → 夹紧到 1 区块 = 16/16（不发非法范围）");
                TerrainSyncRange.Distances(999, out float visMax, out float contentMax);
                Expect(Math.Abs(visMax - 512f) < 1e-3f && Math.Abs(contentMax - 512f) < 1e-3f,
                    "radius 999 → 夹紧到 32 区块 = 512/512");

                // 源码守卫：主机分支必须把"跟随可见范围"的值交给 SetUpdateLocation（不许回退到字面量 64f）
                string spawnerPath = ModPath("Engine", "NetworkAvatarSpawner.cs");
                Expect(File.Exists(spawnerPath), "找到引擎侧调用点源码：" + spawnerPath);
                string spawner = File.ReadAllText(spawnerPath);
                int call = spawner.IndexOf("SetUpdateLocation(playerData.PlayerIndex", StringComparison.Ordinal);
                Expect(call >= 0, "定位到主机分支的 SetUpdateLocation 调用");
                string callText = call < 0
                    ? string.Empty
                    : spawner.Substring(call, Math.Min(240, spawner.Length - call));
                Expect(callText.Contains("remoteVisibility, contentDistance"),
                    "传参 = (可见范围, 内容距离)，两者都来自 radius*16");
                Expect(!callText.Contains("64f"), "同一处不再出现硬编码 64f");
                Expect(spawner.Contains("\u00b7 可见范围 ") && spawner.Contains("\u00b7 内容距离 "),
                    "挂地形更新位置那一行日志把实际生效的可见范围与内容距离都写出来");
            }

            // ------------------------------------------------------------------ M. 每帧预算（本轮改动）
            //
            // 实机：平板飞行时一帧内几十块同时变为内容可用。旧实现（= 预算无穷大）在加载事件里
            // 当场算 65536 格/块（≈0.20 ms/块）→ 单帧 8 ms+，叠加回传落地把帧数打到个位数。
            // 本轮把"摘要计算 / 上报发送 / 回传落地 / 改动后摘要刷新"改成入队 + 每帧有界消化。
            // 这一节用**同一份生产源码**跑两遍：旧口径（预算=无穷大）与新口径（默认 4），给出前后对照。
            Section("R. 每帧预算：N 块同一帧加载 → 每帧只算/发预算内的条数，其余排队，最终全部上报（不丢）");
            {
                const int n = 20;
                var keys = new List<ChunkKey>();
                for (int i = 0; i < n; i++) keys.Add(new ChunkKey(40 + i, 0));

                int legacyWorstDigests = 0;
                double legacyWorstMs = 0;
                {
                    // 旧口径 = 本轮之前的实现（事件里当场算、Tick 里一次发完）：把预算放到无穷大即可复现
                    var legacy = new TerrainSyncOptions
                    {
                        ClientDigestComputationsPerTick = int.MaxValue,
                        ClientReportEntriesPerTick = int.MaxValue,
                        ClientHashRefreshesPerTick = int.MaxValue,
                        ClientResyncPartsPerTick = int.MaxValue
                    };
                    using var room = new Room(legacy, withSyncClient: true);
                    room.Sync.SendHello("client-M-legacy");
                    room.Sync.SendInterest(new ChunkKey(40, 0), 2);
                    WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                    room.PumpInbox();
                    for (int i = 0; i < n; i++) { room.HostWorld.MakeValid(keys[i]); room.ClientWorld.MakeValid(keys[i]); }
                    for (int i = 0; i < n; i++) room.Sync.OnChunkLoaded(keys[i]);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    room.Sync.Tick();
                    watch.Stop();
                    TerrainClientSnapshot snap = room.Sync.GetSnapshot();
                    legacyWorstDigests = snap.DigestsThisTick;
                    legacyWorstMs = watch.Elapsed.TotalMilliseconds;
                    Expect(snap.DigestsThisTick == n,
                        "旧口径（无预算）：一帧内算完全部 " + n + " 块摘要（" + snap.DigestsThisTick +
                        " 块 / " + legacyWorstMs.ToString("0.00") + " ms）—— 这就是尖峰来源");
                    Expect(snap.QueuedDigests == 0, "旧口径不排队（一帧清空，帧时间不可控）");
                }

                var options = new TerrainSyncOptions();      // 默认预算：摘要 4/帧、上报 4/帧
                using (var room = new Room(options, withSyncClient: true))
                {
                    room.Sync.SendHello("client-M");
                    room.Sync.SendInterest(new ChunkKey(40, 0), 2);
                    WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                    room.PumpInbox();
                    for (int i = 0; i < n; i++) { room.HostWorld.MakeValid(keys[i]); room.ClientWorld.MakeValid(keys[i]); }
                    for (int i = 0; i < n; i++) room.Sync.OnChunkLoaded(keys[i]);

                    TerrainClientSnapshot snap = room.Sync.GetSnapshot();
                    Expect(snap.QueuedDigests == n,
                        n + " 块全部入队（加载事件本身不算摘要）：队列 " + snap.QueuedDigests);
                    Expect(snap.DigestsThisTick == 0 && snap.DigestsComputed == 0,
                        "加载事件里一次摘要都没算（旧实现这里已经算了 " + n + " 次）");

                    long entriesBefore = snap.ReportEntriesSent;
                    int maxDigestsPerTick = 0, maxSentPerTick = 0;
                    double maxMs = 0;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    for (int tick = 0; tick < 12; tick++)
                    {
                        watch.Restart();
                        room.Sync.Tick();
                        watch.Stop();
                        room.Pump();
                        snap = room.Sync.GetSnapshot();
                        if (snap.DigestsThisTick > maxDigestsPerTick) maxDigestsPerTick = snap.DigestsThisTick;
                        if (snap.ReportsThisTick > maxSentPerTick) maxSentPerTick = snap.ReportsThisTick;
                        if (watch.Elapsed.TotalMilliseconds > maxMs) maxMs = watch.Elapsed.TotalMilliseconds;
                        if (snap.QueuedDigests == 0 && snap.ReportsThisTick == 0) break;
                    }
                    Expect(maxDigestsPerTick <= options.ClientDigestComputationsPerTick,
                        "每帧算摘要 ≤ " + options.ClientDigestComputationsPerTick + "（实测最坏 " + maxDigestsPerTick + " 块）");
                    Expect(maxSentPerTick <= options.ClientReportEntriesPerTick,
                        "每帧上报 ≤ " + options.ClientReportEntriesPerTick + " 条（实测最坏 " + maxSentPerTick + " 条）");
                    Expect(snap.QueuedDigests == 0, "队列最终清空（0）");
                    Expect(snap.DigestsComputed == n,
                        "累计刚好算过 " + n + " 块摘要（多算=浪费，少算=漏报）：" + snap.DigestsComputed);
                    Expect(snap.ReportEntriesSent - entriesBefore == n,
                        "累计上报条数 == " + n + "（一条不丢）：" + (snap.ReportEntriesSent - entriesBefore));
                    Expect(snap.DigestsDroppedUnloaded == 0 && snap.ReportsDroppedUnloaded == 0,
                        "没有任何因卸载作废的工作（块都还在）");

                    room.Pump();
                    Expect(WaitFor(() => { room.Pump(); return room.Host.GetSnapshot().DigestReports >= n; }, 3000),
                        "主机收齐 " + n + " 条摘要上报（TCP 异步到达，按到达后再断言）");
                    room.Pump();
                    TerrainHostSnapshot host = room.Host.GetSnapshot();
                    Expect(host.Clients.Count == 1 && host.Clients[0].ReportedLoaded.Count == n,
                        "主机侧 ReportedLoaded = " + n + "（" + (host.Clients.Count == 1 ?
                            host.Clients[0].ReportedLoaded.Count : -1) + "）→ 只随真实上报增长");
                    Expect(host.DigestReports == n, "主机收到 " + n + " 条摘要上报（" + host.DigestReports + "）");
                    int fresh = 0;
                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        fresh = 0;
                        for (int i = 0; i < n; i++)
                            if (room.Sync.StateOf(keys[i]) == ChunkFreshness.Fresh) fresh++;
                        return fresh == n;
                    }, 3000), "全部 " + n + " 块最终都被核对（Fresh=" + fresh + "）");
                    Console.WriteLine("    对照：旧口径单帧 " + legacyWorstDigests + " 块摘要 / " + legacyWorstMs +
                        " ms → 新口径单帧最多 " + maxDigestsPerTick + " 块 / " + maxMs.ToString("0.00") + " ms（" + n + " 块全部上报）");
                }
            }

            Section("S. 排队中的块在算摘要之前卸载 → 明确计数作废（不再算/不再报）；重载后重新入队并正常上报");
            {
                var options = new TerrainSyncOptions { ClientDigestComputationsPerTick = 2 };
                using (var room = new Room(options, withSyncClient: true))
                {
                    room.Sync.SendHello("client-N");
                    room.Sync.SendInterest(new ChunkKey(60, 0), 2);
                    WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                    room.PumpInbox();

                    var k0 = new ChunkKey(60, 0);
                    var k1 = new ChunkKey(61, 0);
                    var k2 = new ChunkKey(62, 0);
                    ChunkKey[] three = { k0, k1, k2 };
                    for (int i = 0; i < three.Length; i++)
                    {
                        room.HostWorld.MakeValid(three[i]);
                        room.ClientWorld.MakeValid(three[i]);
                    }
                    Expect(room.Sync.OnChunkLoaded(k0) && room.Sync.OnChunkLoaded(k1) && room.Sync.OnChunkLoaded(k2),
                        "3 块同一帧入队");
                    room.Sync.Tick();                            // 预算 2 → 只算前两块
                    TerrainClientSnapshot snap = room.Sync.GetSnapshot();
                    Expect(snap.DigestsComputed == 2 && snap.QueuedDigests == 1,
                        "预算 2：本帧只算 2 块，第 3 块仍在队列（已算 " + snap.DigestsComputed +
                        " · 队列 " + snap.QueuedDigests + "）");

                    // 第 3 块在算之前卸载（引擎回收了块）：那块内容已经不在 → 明确计数作废
                    room.ClientWorld.Unload(k2);
                    room.Sync.OnChunkUnloaded(k2);
                    snap = room.Sync.GetSnapshot();
                    Expect(snap.QueuedDigests == 0, "队列里那条作废（不再排队）");
                    Expect(snap.DigestsDroppedUnloaded == 1,
                        "计入 DigestsDroppedUnloaded=1（原因：块已卸载，没有可核对的内容）：" +
                        snap.DigestsDroppedUnloaded);
                    long computedAfterUnload = snap.DigestsComputed;
                    room.Sync.Tick();
                    room.Pump();
                    Expect(room.Sync.GetSnapshot().DigestsComputed == computedAfterUnload,
                        "之后不再为这块算摘要（没有白算）");
                    Expect(room.Host.GetSnapshot().Clients[0].ReportedLoaded.Count == 2,
                        "主机只收到 2 块的上报（卸载的那块没有被上报）：" +
                        room.Host.GetSnapshot().Clients[0].ReportedLoaded.Count);

                    // 重载 → 重新入队 → 正常上报（不留下永久缺口）
                    room.ClientWorld.MakeValid(k2);
                    Expect(room.Sync.OnChunkLoaded(k2), "重载后重新入队");
                    room.Sync.Tick();
                    room.Pump();
                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        return room.Host.GetSnapshot().Clients[0].ReportedLoaded.Count == 3;
                    }, 3000), "重载后正常上报，主机侧 ReportedLoaded = 3（最终一块不少）");
                    Expect(WaitFor(() => { room.Pump(); return room.Sync.StateOf(k2) == ChunkFreshness.Fresh; }, 3000),
                        "重载的那块也拿到裁决（Fresh）");
                }

                // 第二种作废路径：摘要**已经算完、还没发出去**时块卸载了（发送阶段明确丢弃并计数）
                using (var room2 = new Room(new TerrainSyncOptions { ClientReportEntriesPerTick = 1 },
                    withSyncClient: true))
                {
                    room2.Sync.SendHello("client-N2");
                    room2.Sync.SendInterest(new ChunkKey(70, 0), 2);
                    WaitFor(() => { room2.PumpInbox(); return room2.Host.ClientCount > 0; }, 2000);
                    room2.PumpInbox();
                    var a = new ChunkKey(70, 0);
                    var b = new ChunkKey(71, 0);
                    room2.HostWorld.MakeValid(a);
                    room2.HostWorld.MakeValid(b);
                    room2.ClientWorld.MakeValid(a);
                    room2.ClientWorld.MakeValid(b);
                    room2.Sync.OnChunkLoaded(a);
                    room2.Sync.OnChunkLoaded(b);
                    room2.Sync.Tick();                            // 算 2 块，但每帧只发 1 条
                    TerrainClientSnapshot s2 = room2.Sync.GetSnapshot();
                    Expect(s2.DigestsComputed == 2 && s2.ReportEntriesSent == 1,
                        "上报预算 1：算 2 块、只发 1 条（剩 1 条在发送队列）");
                    room2.ClientWorld.Unload(b);
                    room2.Sync.OnChunkUnloaded(b);
                    room2.Sync.Tick();
                    room2.Pump();
                    s2 = room2.Sync.GetSnapshot();
                    Expect(s2.ReportsDroppedUnloaded == 1,
                        "已算完但还没发出去的上报因卸载作废并计数（ReportsDroppedUnloaded=1）：" +
                        s2.ReportsDroppedUnloaded);
                    // 上报走真实 TCP：先等到那一条到达（异步），再断言**只有**这一条。
                    Expect(WaitFor(() =>
                    {
                        room2.Pump();
                        return room2.Host.GetSnapshot().Clients[0].ReportedLoaded.Count >= 1;
                    }, 3000) && room2.Host.GetSnapshot().Clients[0].ReportedLoaded.Count == 1,
                        "主机只收到还在的那一块（卸载块没有假装上报）：" +
                        room2.Host.GetSnapshot().Clients[0].ReportedLoaded.Count);
                }
            }

            Section("T. 裁决后回传落地受每帧预算：每帧 ≤1 片，最终全部落地 + 重报 + ReVerified（不丢）");
            {
                var options = new TerrainSyncOptions
                {
                    ClientResyncPartsPerTick = 1, ClientDigestComputationsPerTick = 4
                };
                using var room = new Room(options, withSyncClient: true);
                room.Sync.SendHello("client-O");
                room.Sync.SendInterest(new ChunkKey(80, 0), 3);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                const int n = 4;
                var keys = new List<ChunkKey>();
                for (int i = 0; i < n; i++)
                {
                    var key = new ChunkKey(80 + i, 0);
                    keys.Add(key);
                    room.HostWorld.MakeValid(key);
                    room.ClientWorld.MakeValid(key);
                    room.ClientWorld.MakeDivergent(key);         // 客户端这份不一致 → Mismatch → 回传
                }
                for (int i = 0; i < n; i++) room.Sync.OnChunkLoaded(keys[i]);
                Expect(WaitFor(() =>
                {
                    room.Sync.Tick();
                    room.Pump();
                    return room.Verdicts().Count >= n;
                }, 5000), "客户端上报 → 主机对 " + n + " 块都回了裁决（全部 Mismatch）");
                for (int i = 0; i < n; i++) room.Host.Tick();      // 把排队的回传分片发给客户端
                Expect(WaitFor(() =>
                {
                    room.PumpClient();
                    return room.Sync.GetSnapshot().QueuedResyncParts >= n;
                }, 3000), "回传分片到达客户端后**只入队**（排队中 " +
                    room.Sync.GetSnapshot().QueuedResyncParts + " 片）");
                Expect(room.Sync.GetSnapshot().ResyncApplied == 0,
                    "帧里不落地任何一片（旧实现收到即落地，一帧可以落好几片）");

                int maxAppliedPerTick = 0, ticks = 0;
                double maxMs = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (ticks++ < 20)
                {
                    watch.Restart();
                    room.Sync.Tick();
                    watch.Stop();
                    if (watch.Elapsed.TotalMilliseconds > maxMs) maxMs = watch.Elapsed.TotalMilliseconds;
                    TerrainClientSnapshot now = room.Sync.GetSnapshot();
                    if (now.ResyncAppliedThisTick > maxAppliedPerTick) maxAppliedPerTick = now.ResyncAppliedThisTick;
                    room.Pump();
                    now = room.Sync.GetSnapshot();
                    if (now.QueuedResyncParts == 0 && now.ReportEntriesSent >= n) break;
                }
                TerrainClientSnapshot snap = room.Sync.GetSnapshot();
                Expect(maxAppliedPerTick <= options.ClientResyncPartsPerTick,
                    "每帧落地 ≤ " + options.ClientResyncPartsPerTick + " 片（实测最坏 " + maxAppliedPerTick + " 片）");
                Expect(snap.ResyncApplied == n, "全部 " + n + " 片都落地了（" + snap.ResyncApplied + "）");
                Expect(snap.ResyncVerified == n, "每块落地后都重算摘要并重报（" + snap.ResyncVerified + "）");
                Expect(snap.QueuedResyncParts == 0, "回传队列清空（0）");
                Expect(WaitFor(() => { room.Pump(); return room.Host.GetSnapshot().DigestReports >= 2 * n; }, 3000),
                    "主机共收到 2×" + n + " 条上报（首次 + 覆盖后确认；TCP 异步到达）");
                int fresh = 0;
                Expect(WaitFor(() =>
                {
                    room.Pump();
                    fresh = 0;
                    for (int i = 0; i < n; i++)
                        if (room.Sync.StateOf(keys[i]) == ChunkFreshness.Fresh) fresh++;
                    return fresh == n;
                }, 3000), "最终全部 Fresh（" + fresh + "/" + n + "）");
                Console.WriteLine("    回传：每帧最多落地 " + maxAppliedPerTick + " 片 / " + maxMs.ToString("0.00") + " ms，" +
                    n + " 片跨 " + ticks + " 帧全部完成（旧实现一帧全落）");
            }

            Section("U. 加载后的改动（CellChange，主机一格一条）不再逐条重算整块摘要：只标脏 + 每帧预算重算");
            {
                var options = new TerrainSyncOptions { ClientHashRefreshesPerTick = 1 };
                using var room = new Room(options, withSyncClient: true);
                room.Sync.SendHello("client-P");
                room.Sync.SendInterest(new ChunkKey(90, 0), 1);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                var key = new ChunkKey(90, 0);
                room.HostWorld.MakeValid(key);
                room.ClientWorld.MakeValid(key);
                room.Sync.OnChunkLoaded(key);
                room.Sync.Tick();
                room.Pump();
                Expect(WaitFor(() => { room.Pump(); return room.Sync.StateOf(key) == ChunkFreshness.Fresh; }, 3000),
                    "这块先完成一次正常核对（Fresh）");
                uint hashBefore = room.Sync.LocalHash(key);
                long computedBefore = room.Sync.GetSnapshot().DigestsComputed;

                // 主机按生产路径广播改动：一格一条消息（TerrainSyncHost.BroadcastCellChange）
                for (int i = 0; i < 3; i++)
                    room.Host.BroadcastCellChange(key, new CellEdit(1, 100 + i, 1, 5, 0));
                Expect(WaitFor(() => { room.PumpClient(); return room.Sync.GetSnapshot().CellChangesApplied >= 3; }, 3000),
                    "3 条改动都应用到了（每格 O(1)）");
                TerrainClientSnapshot snap = room.Sync.GetSnapshot();
                Expect(snap.DigestsComputed == computedBefore,
                    "收到改动时**没有**重算整块摘要（旧实现每条改动都重算 65536 格）：已算摘要仍是 " +
                    snap.DigestsComputed);
                Expect(snap.QueuedHashRefreshes == 1,
                    "同一块的 3 条改动只标脏一次（按块去重）：待刷新 " + snap.QueuedHashRefreshes);

                room.Sync.Tick();
                snap = room.Sync.GetSnapshot();
                Expect(snap.DigestsComputed == computedBefore + 1 && snap.QueuedHashRefreshes == 0,
                    "下一次 Tick 才重算一次摘要（预算 1 块/帧），队列清空");
                Expect(room.Sync.LocalHash(key) == room.ClientWorld.DigestOf(key) &&
                       room.Sync.LocalHash(key) != hashBefore,
                    "重算后的本地摘要 = 新内容的真值（不是旧值、也不是 0）");

                // 多块同时被改动：每帧只刷新预算内的块数，其余排队（仍然全部会刷到）
                var other = new ChunkKey(91, 0);
                var other2 = new ChunkKey(92, 0);
                room.HostWorld.MakeValid(other);
                room.HostWorld.MakeValid(other2);
                room.ClientWorld.MakeValid(other);
                room.ClientWorld.MakeValid(other2);
                room.Sync.SendInterest(new ChunkKey(90, 0), 2);
                room.PumpInbox();
                room.Sync.OnChunkLoaded(other);
                room.Sync.OnChunkLoaded(other2);
                for (int i = 0; i < 4; i++) { room.Sync.Tick(); room.Pump(); }
                room.Host.BroadcastCellChange(other, new CellEdit(2, 100, 2, 7, 0));
                room.Host.BroadcastCellChange(other2, new CellEdit(3, 100, 3, 8, 0));
                Expect(WaitFor(() => { room.PumpClient(); return room.Sync.GetSnapshot().QueuedHashRefreshes == 2; }, 3000),
                    "两块同时被改动 → 排队 2 个待刷新");
                long beforeTwo = room.Sync.GetSnapshot().DigestsComputed;
                room.Sync.Tick();
                Expect(room.Sync.GetSnapshot().DigestsComputed - beforeTwo == 1,
                    "预算 1：这一帧只刷新 1 块（实测 +" + (room.Sync.GetSnapshot().DigestsComputed - beforeTwo) + "）");
                Expect(WaitFor(() =>
                {
                    room.Sync.Tick();
                    return room.Sync.GetSnapshot().QueuedHashRefreshes == 0;
                }, 3000), "剩下的块在后续帧都刷新到了（不丢）");
                Expect(room.Sync.LocalHash(other) == room.ClientWorld.DigestOf(other) &&
                       room.Sync.LocalHash(other2) == room.ClientWorld.DigestOf(other2),
                    "两块的本地摘要最终都等于各自新内容的真值");
            }

            Section("V. 队列持续饱和 → 限流一行明确原因（可审计），且不是丢包");
            {
                var options = new TerrainSyncOptions
                {
                    ClientDigestComputationsPerTick = 1,
                    ClientQueueSaturationLogMs = 0,
                    ClientQueueLogIntervalMs = 60000
                };
                using var room = new Room(options, withSyncClient: true);
                var lines = new List<string>();
                room.Sync.Saturated += message => { lock (lines) lines.Add(message); };
                room.Sync.SendHello("client-Q");
                room.Sync.SendInterest(new ChunkKey(100, 0), 2);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                var keys = new List<ChunkKey>();
                for (int i = 0; i < 8; i++)
                {
                    var key = new ChunkKey(100 + i, 0);
                    keys.Add(key);
                    room.HostWorld.MakeValid(key);
                    room.ClientWorld.MakeValid(key);
                    room.Sync.OnChunkLoaded(key);
                }
                for (int tick = 0; tick < 9; tick++) { room.Sync.Tick(); room.Pump(); }
                int logged;
                lock (lines) logged = lines.Count;
                Expect(logged == 1,
                    "持续饱和只写一行（限流 60s 内不再写）：实际 " + logged + " 行");
                lock (lines)
                {
                    Expect(lines.Count == 1 && lines[0].Contains("队列持续饱和") &&
                           lines[0].Contains("待算摘要") && lines[0].Contains("不丢弃、不假成功"),
                        "原因行写清了「队列多满 / 预算多少 / 不会丢」：" +
                        (lines.Count > 0 ? lines[0] : "(空)"));
                }
                Expect(WaitFor(() =>
                {
                    room.Sync.Tick();
                    room.Pump();
                    return room.Sync.GetSnapshot().QueuedDigests == 0;
                }, 3000), "队列最终清空（饱和只是排队，没有丢块）");
                Expect(room.Sync.GetSnapshot().DigestsComputed == 8,
                    "8 块全部算过摘要（" + room.Sync.GetSnapshot().DigestsComputed + "）");
                Expect(WaitFor(() => { room.Pump(); return room.Host.GetSnapshot().DigestReports >= 8; }, 3000),
                    "8 块全部上报到主机（" + room.Host.GetSnapshot().DigestReports + " 条）");
            }

            Section("X. 客户端当前内存区块集合（v8）：整集 / 增量 / 重连替换 / 每帧预算 / 非法载荷拒绝");
            {
                // (a) 客户端同步路径：握手完成时把**当前内存（引擎已分配）**（World.MemoryChunks）作为整集发上去。
                var chunkOptions = new TerrainSyncOptions { ClientChunkMessagesPerTick = 1 };
                ChunkKey[] fullKeys = { new ChunkKey(70, 0), new ChunkKey(71, 0), new ChunkKey(72, 0) };
                using (var room = new Room(chunkOptions, withSyncClient: true))
                {
                    for (int i = 0; i < fullKeys.Length; i++) room.ClientWorld.MakeValid(fullKeys[i]);
                    room.Sync.SendHello("client-X-full");
                    Expect(WaitFor(() => { room.Pump(); return room.Sync.Handshaken; }, 3000),
                        "(a) 客户端完成握手（HelloAck）");
                    Expect(room.Sync.GetSnapshot().QueuedClientChunkMessages >= 1,
                        "(a) 握手后立刻排了一份整集（当前内存 " + fullKeys.Length + " 块）");
                    Expect(WaitFor(() =>
                    {
                        room.Sync.Tick();
                        room.PumpInbox();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        return s.Clients.Count == 1 && s.Clients[0].MemoryChunks.Count == fullKeys.Length;
                    }, 3000), "(a) 主机 MemoryChunks 收齐 = 客户端当前内存 3 块");
                    TerrainHostSnapshot fullSnap = room.Host.GetSnapshot();
                    bool allPresent = fullSnap.Clients.Count == 1;
                    for (int i = 0; allPresent && i < fullKeys.Length; i++)
                        allPresent = fullSnap.Clients[0].MemoryChunks.Contains(fullKeys[i]);
                    Expect(allPresent && fullSnap.MemoryChunkCount == fullKeys.Length &&
                           fullSnap.ClientChunkFullSets == 1,
                        "(a) MemoryChunks 逐块一致，整集计数 = 1（" + fullSnap.MemoryChunkCount +
                        " 块 / fullSets=" + fullSnap.ClientChunkFullSets + "）");
                    Expect(fullSnap.Clients[0].ReportedLoaded.Count == 0,
                        "(a) ReportedLoaded（会话累计上报）仍是 0：当前内存集合与摘要上报是两条独立事实");
                    Expect(room.Sync.GetSnapshot().ClientChunkMessagesThisTick <= 1,
                        "(a) 每帧消息预算 = 1（本帧 " + room.Sync.GetSnapshot().ClientChunkMessagesThisTick + " 条）");

                    // (b) 增量：加载 add、卸载 remove（主机集合立刻跟着变，不需要重发整集）
                    var add1 = new ChunkKey(73, 0);
                    var add2 = new ChunkKey(74, 0);
                    room.ClientWorld.MakeValid(add1);
                    room.ClientWorld.MakeValid(add2);
                    room.Sync.OnMemoryChunkAllocated(add1);
                    room.Sync.OnMemoryChunkAllocated(add2);
                    room.Sync.OnChunkLoaded(add1);
                    room.Sync.OnChunkLoaded(add2);
                    Expect(WaitFor(() =>
                    {
                        room.Sync.Tick();
                        room.PumpInbox();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        return s.Clients.Count == 1 && s.Clients[0].MemoryChunks.Count == 5;
                    }, 3000), "(b) 加载 2 块 → 增量 add → 主机 MemoryChunks = 5");
                    room.ClientWorld.Unload(add1);
                    room.Sync.OnMemoryChunkFreed(add1);
                    room.Sync.OnChunkUnloaded(add1);
                    Expect(WaitFor(() =>
                    {
                        room.Sync.Tick();
                        room.PumpInbox();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        return s.Clients.Count == 1 && s.Clients[0].MemoryChunks.Count == 4 &&
                               !s.Clients[0].MemoryChunks.Contains(add1);
                    }, 3000), "(b) 卸载 1 块 → 增量 remove → 主机 MemoryChunks = 4，且不再包含卸载的那块");
                    Expect(room.Host.GetSnapshot().ClientChunkDeltaMessages >= 2,
                        "(b) 增量消息计数 ≥ 2（加载 / 卸载都发了）");
                }

                // (c) 重连（新 clientId、同身份）：整集**整体替换**，旧路径不残留；分片期间保留旧集合
                using (var room = new PendingRoom())
                {
                    PendingPeer a = room.AddClient("tablet", "identity-X-reconnect");
                    var setA = new List<ClientChunkEntry>
                    {
                        new ClientChunkEntry(100, 0, true), new ClientChunkEntry(101, 0, true),
                        new ClientChunkEntry(102, 0, true)
                    };
                    room.SendClientChunks(a, ClientChunkSetCodec.EncodeFullSetPart(1, 0, 1, setA));
                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        return s.Clients.Count == 1 && s.Clients[0].MemoryChunks.Count == 3;
                    }, 3000), "(c) 第一条连接发整集 3 块 → 主机 MemoryChunks = 3");
                    PendingPeer b = room.Reconnect(a);
                    room.Pump();
                    Expect(room.Host.GetSnapshot().MemoryChunkCount == 3,
                        "(c) 重连后、新整集到达前**保留**旧集合（地图不闪空，也不是第二份数据）");
                    var setB = new List<ClientChunkEntry>
                    {
                        new ClientChunkEntry(102, 0, true), new ClientChunkEntry(103, 0, true)
                    };
                    room.SendClientChunks(b, ClientChunkSetCodec.EncodeFullSetPart(2, 0, 1, setB));
                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        if (s.Clients.Count != 1 || s.Clients[0].MemoryChunks.Count != 2) return false;
                        return s.Clients[0].MemoryChunks.Contains(new ChunkKey(102, 0)) &&
                               s.Clients[0].MemoryChunks.Contains(new ChunkKey(103, 0)) &&
                               !s.Clients[0].MemoryChunks.Contains(new ChunkKey(100, 0)) &&
                               !s.Clients[0].MemoryChunks.Contains(new ChunkKey(101, 0));
                    }, 3000),
                        "(c) 新连接整集 = {102,103} → **整体替换**：100/101 两条旧路径不残留");
                    // 多分片整集：第一片到达时保留旧集合，收齐第二片才替换（不出现半张地图）
                    room.SendClientChunks(b, ClientChunkSetCodec.EncodeFullSetPart(3, 0, 2,
                        new[] { new ClientChunkEntry(104, 0, true) }));
                    room.Pump();
                    TerrainHostSnapshot mid = room.Host.GetSnapshot();
                    Expect(mid.Clients.Count == 1 && mid.Clients[0].MemoryChunks.Count == 2 &&
                           mid.Clients[0].MemoryChunks.Contains(new ChunkKey(103, 0)),
                        "(c) 整集第 1/2 片到达 → 仍是旧集合（分片期间绝不显示半张）");
                    room.SendClientChunks(b, ClientChunkSetCodec.EncodeFullSetPart(3, 1, 2,
                        new[] { new ClientChunkEntry(105, 0, true) }));
                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        if (s.Clients.Count != 1 || s.Clients[0].MemoryChunks.Count != 2) return false;
                        return s.Clients[0].MemoryChunks.Contains(new ChunkKey(104, 0)) &&
                               s.Clients[0].MemoryChunks.Contains(new ChunkKey(105, 0)) &&
                               !s.Clients[0].MemoryChunks.Contains(new ChunkKey(103, 0));
                    }, 3000), "(c) 两片收齐后才整体替换为 {104,105}");

                    // 增量在重连后的身份记录上继续生效：卸载 104、加载 106
                    var delta = new List<ClientChunkEntry>
                    {
                        new ClientChunkEntry(104, 0, false), new ClientChunkEntry(106, 0, true)
                    };
                    room.SendClientChunks(b, ClientChunkSetCodec.EncodeDelta(delta));
                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        if (s.Clients.Count != 1 || s.Clients[0].MemoryChunks.Count != 2) return false;
                        return !s.Clients[0].MemoryChunks.Contains(new ChunkKey(104, 0)) &&
                               s.Clients[0].MemoryChunks.Contains(new ChunkKey(105, 0)) &&
                               s.Clients[0].MemoryChunks.Contains(new ChunkKey(106, 0));
                    }, 3000), "(c) 重连后增量照常生效（104 移除 / 106 加入，集合 = 2 块）");
                }

                // (d) 每帧预算：150 块同一帧入队 → 每条消息 ≤64 条、每帧 ≤1 条，最终全部送达（不丢）
                {
                    const int n = 150;
                    var keys = new List<ChunkKey>();
                    for (int i = 0; i < n; i++) keys.Add(new ChunkKey(200 + i, 0));
                    using (var room = new Room(new TerrainSyncOptions { ClientChunkMessagesPerTick = 1 },
                               withSyncClient: true))
                    {
                        room.Sync.SendHello("client-X-budget");
                        Expect(WaitFor(() => { room.Pump(); return room.Sync.Handshaken; }, 3000),
                            "(d) 预算测试客户端握手");
                        for (int i = 0; i < 3; i++) { room.Sync.Tick(); room.PumpInbox(); }
                        long entriesBefore = room.Sync.GetSnapshot().ClientChunkEntriesSent;
                        long messagesBefore = room.Sync.GetSnapshot().ClientChunkMessagesSent;
                        for (int i = 0; i < n; i++)
                        {
                            room.ClientWorld.MakeValid(keys[i]);
                            room.Sync.OnMemoryChunkAllocated(keys[i]);
                            room.Sync.OnChunkLoaded(keys[i]);
                        }
                        int maxMessagesPerTick = 0;
                        long maxEntriesPerTick = 0;
                        long previousEntries = room.Sync.GetSnapshot().ClientChunkEntriesSent;
                        for (int tick = 0; tick < 12; tick++)
                        {
                            room.Sync.Tick();
                            room.PumpInbox();
                            TerrainClientSnapshot s = room.Sync.GetSnapshot();
                            if (s.ClientChunkMessagesThisTick > maxMessagesPerTick)
                                maxMessagesPerTick = s.ClientChunkMessagesThisTick;
                            long deltaEntries = s.ClientChunkEntriesSent - previousEntries;
                            if (deltaEntries > maxEntriesPerTick) maxEntriesPerTick = deltaEntries;
                            previousEntries = s.ClientChunkEntriesSent;
                            if (s.QueuedClientChunkMessages == 0 &&
                                room.Host.GetSnapshot().MemoryChunkCount == n) break;
                        }
                        TerrainClientSnapshot queued = room.Sync.GetSnapshot();
                        Expect(maxMessagesPerTick <= 1 && queued.ClientChunkBudgetPerTick == 1,
                            "(d) 每帧最多 1 条消息（实测最坏 " + maxMessagesPerTick + " 条 / 预算 " +
                            queued.ClientChunkBudgetPerTick + "）");
                        Expect(maxEntriesPerTick <= ClientChunkSetCodec.MaxEntriesPerMessage,
                            "(d) 每条消息最多 " + ClientChunkSetCodec.MaxEntriesPerMessage + " 条区块条目（实测最坏 " +
                            maxEntriesPerTick + "）");
                        Expect(WaitFor(() =>
                        {
                            room.Sync.Tick(); room.PumpInbox();
                            return room.Host.GetSnapshot().MemoryChunkCount == n;
                        }, 5000), "(d) 分多帧发完，主机最终收齐 " + n + " 块（一条不丢）");
                        Expect(room.Sync.GetSnapshot().ClientChunkEntriesSent - entriesBefore >= n &&
                               room.Sync.GetSnapshot().ClientChunkMessagesSent - messagesBefore >= 3,
                            "(d) 累计条目 ≥ " + n + "、消息 ≥ 3（150 块按 64 条/消息分 3 条）");
                    }
                }

                // (e) 非法载荷：超上限 / 截断 / 未知版本 / 整集 loaded=0 → 计数 + 限流原因，集合不变
                using (var room = new PendingRoom())
                {
                    PendingPeer p = room.AddClient("tablet", "identity-X-bad");
                    var good = new List<ClientChunkEntry>
                    {
                        new ClientChunkEntry(300, 0, true), new ClientChunkEntry(301, 0, true)
                    };
                    room.SendClientChunks(p, ClientChunkSetCodec.EncodeFullSetPart(1, 0, 1, good));
                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        return room.Host.GetSnapshot().MemoryChunkCount == 2;
                    }, 3000), "(e) 先建立基线：MemoryChunks = 2 块");
                    long rejectsBefore = room.Host.GetSnapshot().ClientChunkRejects;
                    int problemsBefore = room.ClientChunkProblems.Count;

                    var overCap = new PayloadWriter();          // count=65 > 64（超上限）
                    overCap.Byte(1).Byte(0).Int32(65);
                    room.SendClientChunks(p, overCap.ToArray());

                    var truncated = new PayloadWriter();        // count=2 但只给 1 条条目（截断）
                    truncated.Byte(1).Byte(0).Int32(2).Int32(0).Int32(0).Byte(1);
                    room.SendClientChunks(p, truncated.ToArray());

                    var badVersion = new PayloadWriter();       // 版本 2（未知）
                    badVersion.Byte(2).Byte(0).Int32(0);
                    room.SendClientChunks(p, badVersion.ToArray());

                    var badFull = new PayloadWriter();          // 整集分片里 loaded=0（非法）
                    badFull.Byte(1).Byte(1).Int32(3).Int32(0).Int32(1).Int32(1).Int32(0).Int32(0).Byte(0);
                    room.SendClientChunks(p, badFull.ToArray());

                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        return room.Host.GetSnapshot().ClientChunkRejects >= rejectsBefore + 4;
                    }, 3000), "(e) 4 条非法载荷全部被拒绝并计数（超上限 / 截断 / 未知版本 / 整集 loaded=0）：" +
                        (room.Host.GetSnapshot().ClientChunkRejects - rejectsBefore) + " 条");
                    Expect(room.Host.GetSnapshot().MemoryChunkCount == 2 &&
                           room.Host.GetSnapshot().Clients[0].MemoryChunks.Count == 2,
                        "(e) 非法载荷**整条不应用**：MemoryChunks 仍是 2 块（不做部分应用）");
                    Expect(room.ClientChunkProblems.Count > problemsBefore,
                        "(e) 拒绝写了明确原因行（Terrain 层已限流）：" +
                        (room.ClientChunkProblems.Count > 0
                            ? room.ClientChunkProblems[room.ClientChunkProblems.Count - 1] : "(空)"));
                    string problem = room.ClientChunkProblems.Count > 0
                        ? room.ClientChunkProblems[room.ClientChunkProblems.Count - 1] : string.Empty;
                    Expect(problem.Contains("拒绝") && problem.Contains("累计") &&
                           problem.Contains("不应用"),
                        "(e) 原因行同时给出\"拒绝原因 / 整条不应用 / 累计条数\"");
                }

                // (f) v8 载荷往返（含每块状态）+ v7 字节明确拒绝；模块线框来源 = MemoryChunks
                {
                    var live = new LiveMapSnapshot { View = LiveMapView.All };
                    live.Clients.Add(new LiveMapClient
                    {
                        ClientId = 7, StableKey = "u0000000000000007", Name = "tablet",
                        ReportedChunkCount = 5, CurrentLoadedChunkCount = 4
                    });
                    for (int i = 0; i < 4; i++)
                    {
                        live.ClientChunks.Add(new LiveMapClientChunk
                        {
                            ClientId = 7, StableKey = "u0000000000000007",
                            X = 400 + i, Z = -400 - i, State = (byte)(i + 1)
                        });
                    }
                    List<byte[]> batches = LiveMapCodec.EncodeBatches(live, 9001);
                    LiveMapBatch decoded = null;
                    Expect(batches.Count == 1 && batches[0][0] == 8 &&
                           LiveMapCodec.TryDecodeBatch(batches[0], out decoded) &&
                           decoded.Header.Clients[0].CurrentLoadedChunkCount == 4 &&
                           decoded.ClientChunks.Count == 4,
                        "(f) v8 载荷往返：头部 currentLoaded = 4、当前内存条目 4 条");
                    bool statesOk = decoded != null && decoded.ClientChunks.Count == 4;
                    for (int i = 0; statesOk && i < 4; i++)
                    {
                        statesOk = decoded.ClientChunks[i].State == (byte)(i + 1) &&
                                   decoded.ClientChunks[i].X == 400 + i &&
                                   decoded.ClientChunks[i].Z == -400 - i;
                    }
                    Expect(statesOk, "(f) 每块状态 1/2/3/4 与坐标逐条原样往返");
                    byte[] v7bytes = (byte[])batches[0].Clone();
                    v7bytes[0] = 7;
                    Expect(!LiveMapCodec.TryDecodeBatch(v7bytes, out _),
                        "(f) v7 字节（首字节 7）被明确拒绝：新头部字段绝不按旧布局猜");

                    string modulePathX = ModPath("Modules", "TerrainSyncModule.cs");
                    string moduleTextX = File.ReadAllText(modulePathX);
                    Expect(moduleTextX.Contains("view.MemoryChunks") &&
                           !moduleTextX.Contains("for (int c = 0; c < view.ReportedLoaded.Count"),
                        "(f) 模块 clients[].chunks 来源 = view.MemoryChunks（不再用会话累计的 ReportedLoaded）");
                }
            }
            Section("X2. 内存区块集合 = 引擎已分配：未定稿的海洋块也进整集/增量；定稿不重复、释放才移除、重连反映内存、欠更新/摘要口径不变");
            {
                var opts = new TerrainSyncOptions { ClientChunkMessagesPerTick = 4 };
                var landA = new ChunkKey(120, 0);
                var landB = new ChunkKey(121, 0);
                var ocean = new ChunkKey(130, 5);
                using (var room = new Room(opts, withSyncClient: true))
                {
                    room.ClientWorld.MakeValid(landA);
                    room.ClientWorld.MakeValid(landB);
                    room.ClientWorld.Allocate(ocean);          // 只有内存、内容未定稿
                    Expect(!room.ClientWorld.HasContents(ocean),
                        "(a) 海洋块 (130,5) 此刻**只有分配、没有内容**（模拟内容未定稿）");
                    room.Sync.SendHello("client-X2", "identity-X2");
                    Expect(WaitFor(() => { room.Pump(); return room.Sync.Handshaken; }, 3000),
                        "(a) 客户端握手完成");
                    Expect(WaitFor(() =>
                    {
                        room.Sync.Tick();
                        room.PumpInbox();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        return s.Clients.Count == 1 && s.Clients[0].MemoryChunks.Count == 3;
                    }, 3000), "(a) 握手整集 = 引擎已分配的 3 块（含未定稿的海洋块）");
                    TerrainHostSnapshot s0 = room.Host.GetSnapshot();
                    Expect(s0.Clients[0].MemoryChunks.Contains(ocean) && s0.MemoryChunkCount == 3,
                        "(a) 主机 CurrentLoaded 含海洋块，计数 3");
                    Expect(room.Sync.GetSnapshot().ReportEntriesSent == 0 && s0.DigestReports == 0,
                        "(f) 只分配（内容未定稿）→ **不**触发摘要上报（0 条）");

                    // (a2) 握手后新分配 → 增量 add（不需要内容可用）
                    var ocean2 = new ChunkKey(131, 5);
                    room.ClientWorld.Allocate(ocean2);
                    room.Sync.OnMemoryChunkAllocated(ocean2);
                    Expect(WaitFor(() =>
                    {
                        room.Sync.Tick();
                        room.PumpInbox();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        return s.Clients.Count == 1 && s.Clients[0].MemoryChunks.Contains(ocean2) &&
                               s.Clients[0].MemoryChunks.Count == 4;
                    }, 3000), "(a) 新分配（仍未定稿）→ 增量 add → 主机集合 4 块");
                    long deltasBeforeValid = room.Host.GetSnapshot().ClientChunkDeltaMessages;

                    // (e) 欠更新计数跟着内存集合：内存里有（哪怕内容未定稿）⇒ 不标记
                    room.HostWorld.MakeValid(ocean2);
                    room.Host.BroadcastCellChange(ocean2, new CellEdit(1, 80, 1, 5, 0));
                    Expect(room.Host.PendingClientCount(ocean2) == 0,
                        "(e) 该块在客户端内存里（内容未定稿）→ 欠更新计数 = 0（不标记）");

                    // (b) 内容定稿：集合不变、不重复；摘要/裁决从这一刻才开始
                    room.ClientWorld.MakeValid(ocean);
                    room.Sync.OnChunkLoaded(ocean);
                    room.Sync.Tick();
                    room.Pump();
                    Expect(WaitFor(() =>
                    {
                        room.Pump();
                        return room.Host.GetSnapshot().Clients[0].ReportedLoaded.Contains(ocean);
                    }, 3000), "(f) 内容定稿后的 OnChunkLoaded → 才有摘要上报（主机 ReportedLoaded 含 ocean）");
                    TerrainHostSnapshot s1 = room.Host.GetSnapshot();
                    Expect(s1.Clients[0].MemoryChunks.Count == 4 && s1.Clients[0].MemoryChunks.Contains(ocean) &&
                           s1.ClientChunkDeltaMessages == deltasBeforeValid,
                        "(b) 内容定稿**不改变**内存区块集合（仍 4 块、集合只有一份、没有多余增量）");

                    // (c) 引擎释放 → 增量 remove（内容与内存一起消失）
                    room.ClientWorld.Free(ocean2);
                    room.Sync.OnMemoryChunkFreed(ocean2);
                    Expect(WaitFor(() =>
                    {
                        room.Sync.Tick();
                        room.PumpInbox();
                        TerrainHostSnapshot s = room.Host.GetSnapshot();
                        return s.Clients.Count == 1 && !s.Clients[0].MemoryChunks.Contains(ocean2) &&
                               s.Clients[0].MemoryChunks.Count == 3;
                    }, 3000), "(c) 引擎释放 → 增量 remove → 主机集合回到 3 块");
                    room.Host.BroadcastCellChange(ocean2, new CellEdit(2, 80, 2, 6, 0));
                    Expect(room.Host.PendingClientCount(ocean2) == 1,
                        "(e) 释放后再改动 → 欠更新计数 = 1（计数器跟着内存集合走）");

                    // (d) 重连（新连接、同身份）：整集仍来自**内存集合**（含海洋块，且不含已释放的块）
                    int oldId = room.Host.GetSnapshot().Clients[0].Id;
                    TcpTerrainClient reconnect = null;
                    TerrainSyncClient sync2 = null;
                    try
                    {
                        room.Client.Dispose();
                        Expect(WaitFor(() => room.Server.StateOf(oldId) == ChannelState.Failed ||
                                             room.Server.StateOf(oldId) == ChannelState.Disconnected, 3000),
                            "(d) 旧连接已不可达");
                        for (int i = 0; i < 3; i++) { room.Host.Tick(); room.PumpInbox(); }
                        var inbox2 = new ConcurrentQueue<ReceivedFrame>();
                        reconnect = new TcpTerrainClient("127.0.0.1", room.Server.Port);
                        Expect(reconnect.Connect(3000), "(d) 重连 TCP 成功");
                        sync2 = new TerrainSyncClient(room.ClientWorld, reconnect, opts, attachToChannel: false);
                        reconnect.FrameReceived += frame => inbox2.Enqueue(frame);
                        sync2.SendHello("client-X2", "identity-X2");
                        Expect(WaitFor(() =>
                        {
                            while (inbox2.TryDequeue(out ReceivedFrame frame)) sync2.ProcessFrame(frame);
                            sync2.Tick();
                            room.PumpInbox();
                            return sync2.Handshaken && room.Host.GetSnapshot().Clients.Count == 1 &&
                                   room.Host.GetSnapshot().Clients[0].MemoryChunks.Count == 3;
                        }, 5000), "(d) 重连整集 = 内存集合 3 块（含海洋块，且不含已释放的块）");
                        TerrainHostSnapshot s2 = room.Host.GetSnapshot();
                        Expect(s2.Clients[0].MemoryChunks.Contains(ocean) &&
                               s2.Clients[0].MemoryChunks.Contains(landA) &&
                               s2.Clients[0].MemoryChunks.Contains(landB) &&
                               !s2.Clients[0].MemoryChunks.Contains(ocean2),
                            "(d) 重连后主机 CurrentLoaded = {landA,landB,ocean}：反映内存集合，旧路径不残留");
                        Expect(WaitFor(() =>
                        {
                            while (inbox2.TryDequeue(out ReceivedFrame frame)) sync2.ProcessFrame(frame);
                            sync2.Tick();
                            return sync2.GetSnapshot().MemoryChunkCount == 3;
                        }, 3000), "(d) 客户端内存集合 = 3 块（与桥的已分配集合一致）");
                    }
                    finally
                    {
                        reconnect?.Dispose();
                    }
                }
            }
            Section("Y. 世界传输闸门（真实 TCP）：alreadyHasWorld 语义 → 首次恰好发一次 / 重连绝不发");
            {
                // (c) 客户端 Hello 标记的判定（同一份生产源码 Terrain/RejoinFlagPolicy.cs）：
                //     只有"此刻正在游玩下载来的联机世界"才是 true；Play 屏（世界刚下载/正在下载）必须 false。
                Expect(!RejoinFlagPolicy.ShouldSendAlreadyHasWorld(false, true, true, true, "sess-a", "sess-a", out string rejoinR1) &&
                       rejoinR1.Contains("不在游戏内"),
                    "(c) Play 屏加入（不在游戏内）即使下载世界已在登记表 → alreadyHasWorld=false，原因写明不在游戏内");
                Expect(!RejoinFlagPolicy.ShouldSendAlreadyHasWorld(true, false, true, true, "sess-a", "sess-a", out string rejoinR2) &&
                       rejoinR2.Contains("Project"),
                    "(c) GameLoading（Project 还没加载完）→ false（刚下载完但不在游戏内仍然要主机下发整图）");
                Expect(!RejoinFlagPolicy.ShouldSendAlreadyHasWorld(true, true, false, false, "sess-a", "sess-a", out string rejoinR3) &&
                       rejoinR3.Contains("世界目录"),
                    "(c) 在游戏内但读不到当前世界目录 → false（不猜）");
                Expect(!RejoinFlagPolicy.ShouldSendAlreadyHasWorld(true, true, true, false, "sess-a", "sess-a", out string rejoinR4) &&
                       rejoinR4.Contains("不是下载"),
                    "(c) 在游戏内玩的是本机世界（下载登记不匹配）→ false");
                Expect(RejoinFlagPolicy.ShouldSendAlreadyHasWorld(true, true, true, true, "sess-a", "sess-a", out string rejoinR5) &&
                       rejoinR5.Contains("正在游玩"),
                    "(c) 在游戏内 + Project + 当前世界就是下载世界 → true（这才是\"重连，不重下整图\"）");

                // (a) 首次加入（alreadyHasWorld=false）：主机恰好导出/发送一次整图。
                {
                    using var room = new TransferRoom();
                    byte[] expected = MakeWorldPak(200000);
                    room.Exporter.Pak = expected;
                    room.Join(room.Client, alreadyHasWorld: false);
                    Expect(room.TickUntilWorldEnd(), "(a) 世界发送在预算内完成（客户端收到 WorldEnd）");

                    ReceivedFrame[] frames = room.Frames();
                    int begins = 0, ends = 0, chunkFrames = 0;
                    int chunkCount = 0;
                    bool beginOk = false, endOk = false;
                    string beginSession = null;
                    long beginBaseSequence = -1L;
                    var byIndex = new Dictionary<int, byte[]>();
                    for (int i = 0; i < frames.Length; i++)
                    {
                        switch (frames[i].Type)
                        {
                            case TerrainMessageType.WorldBegin:
                                begins++;
                                beginOk = WorldTransferCodec.TryDecodeBegin(frames[i].Payload,
                                    out _, out _, out _, out _, out chunkCount, out _,
                                    out beginSession, out beginBaseSequence);
                                break;
                            case TerrainMessageType.WorldChunk:
                                chunkFrames++;
                                if (WorldTransferCodec.TryDecodeChunk(frames[i].Payload,
                                    out _, out int index, out int total, out _, out byte[] data))
                                {
                                    byIndex[index] = data;
                                    if (chunkCount == 0) chunkCount = total;
                                }
                                break;
                            case TerrainMessageType.WorldEnd:
                                ends++;
                                endOk = WorldTransferCodec.TryDecodeEnd(frames[i].Payload,
                                    out _, out bool ok, out _) && ok;
                                break;
                        }
                    }
                    bool bytesMatch = chunkCount > 0 && byIndex.Count == chunkCount;
                    if (bytesMatch)
                    {
                        var assembled = new byte[expected.Length];
                        int at = 0;
                        for (int i = 0; i < chunkCount && bytesMatch; i++)
                        {
                            if (!byIndex.TryGetValue(i, out byte[] data) ||
                                at + data.Length > assembled.Length)
                            {
                                bytesMatch = false;
                                break;
                            }
                            Buffer.BlockCopy(data, 0, assembled, at, data.Length);
                            at += data.Length;
                        }
                        bytesMatch = bytesMatch && at == expected.Length;
                        for (int i = 0; bytesMatch && i < expected.Length; i++)
                            if (assembled[i] != expected[i]) bytesMatch = false;
                    }
                    Expect(room.Port.LastRejoin == 0 &&
                           !room.Host.WasRejoin(room.Port.LastClientId),
                        "(a) 首次加入 Hello（alreadyHasWorld=false）→ 主机 WasRejoin=false");
                    Expect(room.Exporter.Calls == 1,
                        "(a) 主机恰好导出一次世界（实际 " + room.Exporter.Calls + " 次）");
                    Expect(begins == 1 && beginOk, "(a) 恰好一个 WorldBegin（实际 " + begins + " 个）");
                    Expect(beginSession == TransferRoom.HostSessionId,
                        "(a) WorldBegin 清单带主机会话标识（append-only）：" + beginSession);
                    Expect(beginBaseSequence == 0L,
                        "(a) WorldBegin 清单带导出基线序号（导出前没有改动 → 0）：" + beginBaseSequence);
                    Expect(chunkFrames == chunkCount && bytesMatch,
                        "(a) " + chunkCount + " 片全部收到，按序号重组成与导出字节完全一致（实际 " +
                        chunkFrames + " 片）");
                    Expect(ends == 1 && endOk, "(a) 恰好一个 WorldEnd(ok)（实际 " + ends + " 个）");
                    int chunksAfterFirst = room.Count(TerrainMessageType.WorldChunk);
                    room.Tick(300);
                    Expect(room.Exporter.Calls == 1 &&
                           room.Count(TerrainMessageType.WorldBegin) == 1 &&
                           room.Count(TerrainMessageType.WorldEnd) == 1 &&
                           room.Count(TerrainMessageType.WorldChunk) == chunksAfterFirst,
                        "(a) 再推进 300 tick 也不重发：导出 1 次 / Begin 1 / End 1 / 分片数不变" +
                        "（非重连 ⇒ 恰好一次）");
                    Expect(room.Log.Contains("role=host") && room.Log.Contains("首次加入") &&
                           room.Log.Contains("恰好发送一次"),
                        "(a) 握手日志一行：role=host / 首次加入 / 世界传输恰好发送一次");
                }

                // (b) 实机回归（14:59）：重连 Hello + **全新的传输状态** —— 主机一个世界包都不许发。
                {
                    using var room = new TransferRoom();
                    room.Exporter.Pak = MakeWorldPak(200000);   // 主机这边完全有条件发（旧实现就会发）
                    room.Join(room.Client, alreadyHasWorld: true, TransferRoom.HostSessionId);
                    room.Tick(300);
                    room.Settle();
                    Expect(room.Port.LastRejoin == 1 && room.Host.WasRejoin(room.Port.LastClientId),
                        "(b) 重连 Hello（alreadyHasWorld=true）→ 主机 WasRejoin=true");
                    Expect(room.Exporter.Calls == 0,
                        "(b) 传输状态是全新的，但重连 ⇒ 主机一次都不导出（实际 " +
                        room.Exporter.Calls + " 次；实机 14:59 的重发回归）");
                    Expect(room.Count(TerrainMessageType.WorldBegin) == 0 &&
                           room.Count(TerrainMessageType.WorldChunk) == 0 &&
                           room.Count(TerrainMessageType.WorldEnd) == 0,
                        "(b) 主机一个世界包都不发：Begin/Chunk/End = " +
                        room.Count(TerrainMessageType.WorldBegin) + "/" +
                        room.Count(TerrainMessageType.WorldChunk) + "/" +
                        room.Count(TerrainMessageType.WorldEnd));
                    Expect(room.Log.Contains("role=host") && room.Log.Contains("重连") &&
                           room.Log.Contains("不导出、不发送整图"),
                        "(b) 握手日志一行：role=host / 重连 / 世界传输不导出不发送");
                }

                // (d) 重连（断链后同身份新连接）：不重发整图，身份会话记录复用。
                {
                    using var room = new TransferRoom();
                    room.Exporter.Pak = MakeWorldPak(120000);
                    room.Join(room.Client, alreadyHasWorld: false);
                    Expect(room.TickUntilWorldEnd(), "(d) 首次加入在预算内完成（客户端收到 WorldEnd）");
                    Expect(room.Exporter.Calls == 1 &&
                           room.Count(TerrainMessageType.WorldBegin) == 1 &&
                           room.Count(TerrainMessageType.WorldEnd) == 1,
                        "(d) 第一条连接首次加入 → 完整下发一次整图（导出 " + room.Exporter.Calls +
                        " / Begin " + room.Count(TerrainMessageType.WorldBegin) +
                        " / Chunk " + room.Count(TerrainMessageType.WorldChunk) +
                        " / End " + room.Count(TerrainMessageType.WorldEnd) + "）");
                    int firstId = room.Port.LastClientId;
                    string firstKey = room.Host.GetSnapshot().Clients[0].StableKey;

                    room.Client.Dispose();
                    Expect(WaitFor(() => room.Server.StateOf(firstId) == ChannelState.Failed ||
                        room.Server.StateOf(firstId) == ChannelState.Disconnected, 3000),
                        "(d) 主机发现第一条连接已不可达（State=" + room.Server.StateOf(firstId) + "）");
                    room.Tick(3);

                    room.Client = room.Connect();
                    room.Join(room.Client, alreadyHasWorld: true, TransferRoom.HostSessionId);
                    room.Tick(300);
                    room.Settle();
                    TerrainHostSnapshot snap = room.Host.GetSnapshot();
                    Expect(snap.Clients.Count == 1 && snap.Clients[0].Id != firstId,
                        "(d) 重连后是新 clientId（旧连接已清理，不会出现两条记录）");
                    Expect(room.Host.WasRejoin(snap.Clients[0].Id),
                        "(d) 重连 Hello → WasRejoin=true");
                    Expect(snap.Clients[0].StableKey == firstKey,
                        "(d) 身份会话复用：稳定键不变（" + firstKey + "）");
                    Expect(room.SessionTraces.Exists(m => m.Contains("接上已有会话记录")),
                        "(d) 主机按身份接上已有会话记录（复用，不重置）");
                    Expect(room.Exporter.Calls == 1,
                        "(d) 重连后**没有**第二次导出（实际 " + room.Exporter.Calls + " 次）");
                    Expect(room.Count(TerrainMessageType.WorldBegin) == 1 &&
                           room.Count(TerrainMessageType.WorldEnd) == 1,
                        "(d) 重连后世界包数量不变：Begin=" +
                        room.Count(TerrainMessageType.WorldBegin) + " / Chunk=" +
                        room.Count(TerrainMessageType.WorldChunk) + " / End=" +
                        room.Count(TerrainMessageType.WorldEnd) + "（不重发整图）");
                    Expect(room.Log.Contains("重连") && room.Log.Contains("不导出、不发送整图"),
                        "(d) 握手日志写明重连 → 世界传输不发送");
                }

                // 源码守卫：标记来源与主机闸门都必须留在唯一入口上（防止以后又退回"磁盘上有世界就算"）。
                string terrainModulePathY = ModPath("Modules", "TerrainSyncModule.cs");
                string terrainModuleY = File.ReadAllText(terrainModulePathY);
                Expect(terrainModuleY.Contains("RejoinFlagPolicy.ShouldSendAlreadyHasWorld") &&
                       terrainModuleY.Contains("ScreensManager.CurrentScreen is Game.GameScreen"),
                    "(守卫) 客户端 flag 唯一来源 = RejoinFlagPolicy + GameScreen（不是\"磁盘上有下载世界\"）");
                string worldTransferModulePathY = ModPath("Modules", "WorldTransferModule.cs");
                string worldTransferModuleY = File.ReadAllText(worldTransferModulePathY);
                Expect(worldTransferModuleY.Contains("if (m_joinedIsRejoin) return;") &&
                       worldTransferModuleY.Contains("if (m_sendStarted) return;") &&
                       worldTransferModuleY.Contains("if (!m_joinedIsRejoin)"),
                    "(守卫) 主机发送分支按实际 rejoin 标记拦截（TryStartSend + Tick），并带\"已开始\"标记");
            }

            Section("Z. 会话标识 + 断线重连追帧（真实 TCP）：同会话只追帧 / 换会话整图重下 / 历史越界明确拒绝");
            {
                // ---- (z0) 纯逻辑：会话标识格式 + Hello 标记的两个新条件 ----
                string zs1 = SessionIdentity.NewSessionId();
                string zs2 = SessionIdentity.NewSessionId();
                Expect(zs1.Length == SessionIdentity.HexLength && SessionIdentity.IsWellFormed(zs1),
                    "(z0) NewSessionId 生成 32 位小写十六进制：" + zs1);
                Expect(zs1 != zs2, "(z0) 两次生成不同（每次建房/会话开始都换新标识）");
                Expect(!SessionIdentity.IsWellFormed(null) && !SessionIdentity.IsWellFormed("XYZ") &&
                       !SessionIdentity.IsWellFormed(zs1.ToUpperInvariant()),
                    "(z0) 非法标识明确判 false（不悄悄转成默认值）");
                Expect(!RejoinFlagPolicy.ShouldSendAlreadyHasWorld(true, true, true, true, "", zs1, out string zr0) &&
                       zr0.Contains("没有会话标识"), "(z0) 下载世界没有会话标识 → false（首次加入）");
                Expect(!RejoinFlagPolicy.ShouldSendAlreadyHasWorld(true, true, true, true, zs1, "", out string zr1) &&
                       zr1.Contains("HelloAck"), "(z0) 还没收到过主机会话（HelloAck）→ false");
                Expect(!RejoinFlagPolicy.ShouldSendAlreadyHasWorld(true, true, true, true, zs1, zs2, out string zr2) &&
                       zr2.Contains("不一致"), "(z0) 会话不一致（主机重启）→ false（整图重下）");
                Expect(RejoinFlagPolicy.ShouldSendAlreadyHasWorld(true, true, true, true, zs1, zs1, out string zr3) &&
                       zr3.Contains("会话一致"), "(z0) 会话一致 + 在游戏内 + 下载世界 → true（重连，只追帧）");

                var key = new ChunkKey(2, 3);
                // ---- (z1) 同会话重连：不重发整图，追帧恰好补上断线期间缺失的改动 ----
                {
                    using var room = new TransferRoom();
                    room.HostWorld.MakeValid(key);
                    room.ClientWorld.MakeValid(key);
                    room.JoinSync(false, string.Empty, 0L);
                    room.Sync.SendInterest(key, 2);
                    Expect(room.TickUntil(() => room.Host.SubscriberCount(key) == 1, 300),
                        "(z1) 客户端已订阅该块（真实 InterestUpdate 生效）");

                    // 断线前的 3 条改动：在线，客户端立即应用（真实 CellChange，带序号）。
                    for (int i = 0; i < 3; i++)
                    {
                        room.Change(key, new CellEdit(1, 100 + i, 1, 10 + i, 0));
                        room.TickUntil(() => room.Sync.LastAppliedChangeSequence >= i + 1, 300);
                    }
                    Expect(room.Sync.LastAppliedChangeSequence == 3 &&
                           room.Sync.GetSnapshot().CellChangesApplied == 3,
                        "(z1) 在线改动 3 条已应用，上次应用序号 = " + room.Sync.LastAppliedChangeSequence);
                    long lastApplied = room.Sync.LastAppliedChangeSequence;

                    // 断线（模拟网络断了几秒；主机仍在同一会话）。
                    int firstId = room.Port.LastClientId;
                    room.Client.Dispose();
                    room.Sync = null;
                    Expect(WaitFor(() => room.Server.StateOf(firstId) == ChannelState.Failed ||
                        room.Server.StateOf(firstId) == ChannelState.Disconnected, 3000),
                        "(z1) 主机发现客户端连接不可达");
                    room.Tick(3);

                    // 断线期间主机继续改动 3 条（无人订阅也必须进历史，否则重连会留下无法重放的洞）。
                    for (int i = 3; i < 6; i++)
                        room.Change(key, new CellEdit(1, 100 + i, 1, 10 + i, 0));
                    long head = room.Host.CurrentChangeSequence;
                    Expect(head == 6, "(z1) 主机改动水位 = 6：" + head);
                    Expect(room.HostWorld.DigestOf(key) != room.ClientWorld.DigestOf(key),
                        "(z1) 断线期间客户端世界确实落后（摘要不同）");

                    // 重连：同一会话 + 上次应用序号；主机**即使有 pak 也不许发**（设了 pak 才能证明闸门有效）。
                    int exportsBefore = room.Exporter.Calls;
                    room.Exporter.Pak = MakeWorldPak(60000);
                    room.Client = room.Connect();
                    room.JoinSync(true, TransferRoom.HostSessionId, lastApplied);
                    Expect(room.Host.WasRejoin(room.Port.LastClientId) && room.Port.LastRejoin == 1,
                        "(z1) 同会话 + 序号在历史内 → 主机接受重连（WasRejoin=true）");
                    Expect(room.Sync.RejoinAccepted && room.Sync.HostSessionId == TransferRoom.HostSessionId,
                        "(z1) HelloAck 带回主机会话标识且接受重连");
                    room.Sync.SendInterest(key, 2);
                    room.TickUntil(() => room.Host.SubscriberCount(key) == 1, 300);
                    room.Sync.Tick();          // 发出 CatchUp Request（在 Interest 之后，订阅先就绪）
                    Expect(room.TickUntil(() => room.Sync.CatchUpCompleted, 500),
                        "(z1) 追帧在预算内完成（客户端已到主机水位）");
                    room.Settle();
                    Expect(room.Sync.CatchUpAppliedChanges == 3 && room.Sync.CatchUpMessages >= 1,
                        "(z1) 追帧恰好补了 3 条缺失改动（没有重放已应用过的 1..3）：" +
                        room.Sync.CatchUpAppliedChanges + " 条 / " + room.Sync.CatchUpMessages + " 批");
                    Expect(room.Sync.LastAppliedChangeSequence == head,
                        "(z1) 客户端上次应用序号追到主机水位 " + head +
                        "（实际 " + room.Sync.LastAppliedChangeSequence + "）");
                    Expect(room.HostWorld.DigestOf(key) == room.ClientWorld.DigestOf(key),
                        "(z1) 追帧后客户端该块内容与主机完全一致");
                    TerrainHostSnapshot z1s = room.Host.GetSnapshot();
                    Expect(z1s.CatchUpRounds == 1 && z1s.CatchUpChangesSent == 3,
                        "(z1) 主机统计：追帧 1 轮 / 重放 3 条（实际 " + z1s.CatchUpRounds +
                        " 轮 / " + z1s.CatchUpChangesSent + " 条）");
                    Expect(room.Exporter.Calls == exportsBefore &&
                           room.Count(TerrainMessageType.WorldBegin) == 0 &&
                           room.Count(TerrainMessageType.WorldChunk) == 0 &&
                           room.Count(TerrainMessageType.WorldEnd) == 0,
                        "(z1) 重连**没有**导出/发送整图（导出次数 " + exportsBefore + " → " +
                        room.Exporter.Calls + " / Begin=" + room.Count(TerrainMessageType.WorldBegin) +
                        " / Chunk=" + room.Count(TerrainMessageType.WorldChunk) +
                        " / End=" + room.Count(TerrainMessageType.WorldEnd) + "）");
                }

                // ---- (z2) 会话不一致（主机重启）→ 首次加入 → 整图重下 ----
                {
                    using var room = new TransferRoom();
                    room.HostWorld.MakeValid(key);
                    room.ClientWorld.MakeValid(key);
                    room.Exporter.Pak = MakeWorldPak(50000);
                    string rejected = null;
                    bool hostWillSend = false;
                    room.Sync.RejoinRejected += (reason, willSend) => { rejected = reason; hostWillSend = willSend; };
                    room.JoinSync(true, "ffffffffffffffffffffffffffffffff", 0L);
                    Expect(!room.Sync.RejoinAccepted && rejected != null && rejected.Contains("会话不一致"),
                        "(z2) 不同会话 → RejoinAccepted=false，依据写明会话不一致：" + rejected);
                    Expect(hostWillSend, "(z2) 本版主机会在本连接直接下发整图（客户端不必再断开一次）");
                    Expect(!room.Host.WasRejoin(room.Port.LastClientId) && room.Port.LastRejoin == 0,
                        "(z2) 主机 WasRejoin=false（这条连接按首次加入处理）");
                    Expect(room.TickUntilWorldEnd(), "(z2) 主机随后下发整图（客户端收到 WorldEnd）");
                    Expect(room.Exporter.Calls == 1 &&
                           room.Count(TerrainMessageType.WorldBegin) == 1 &&
                           room.Count(TerrainMessageType.WorldEnd) == 1,
                        "(z2) 恰好导出/发送一次整图（Exporter=" + room.Exporter.Calls +
                        " / Begin=" + room.Count(TerrainMessageType.WorldBegin) +
                        " / End=" + room.Count(TerrainMessageType.WorldEnd) + "）");
                }

                // ---- (z3) 请求序号不在保留历史内 → Hello 阶段明确拒绝 → 整图重下 ----
                {
                    var historyOptions = new TerrainSyncOptions { CatchUpMaxJournalEntries = 2 };
                    using var room = new TransferRoom(historyOptions);
                    room.HostWorld.MakeValid(key);
                    room.ClientWorld.MakeValid(key);
                    room.JoinSync(false, string.Empty, 0L);
                    room.Sync.SendInterest(key, 2);
                    room.TickUntil(() => room.Host.SubscriberCount(key) == 1, 300);
                    for (int i = 0; i < 5; i++)
                        room.Change(key, new CellEdit(2, 100 + i, 2, 20 + i, 0));
                    TerrainHostSnapshot before = room.Host.GetSnapshot();
                    Expect(before.ChangeJournalCount == 2 && before.ChangeJournalOldestSequence == 4 &&
                           before.ChangeJournalEvictions > 0,
                        "(z3) 有界历史：5 条改动后只保留最旧=4 的 2 条（裁剪计数 " +
                        before.ChangeJournalEvictions + "）");
                    int exportsBefore = room.Exporter.Calls;
                    room.Exporter.Pak = MakeWorldPak(40000);
                    // 同一个连接上再 Hello 一次（模拟客户端重连后的握手；声明上次只应用到 0）。
                    room.JoinSync(true, TransferRoom.HostSessionId, 0L);
                    Expect(!room.Sync.RejoinAccepted && room.Sync.RejoinReason.Contains("历史"),
                        "(z3) 序号 0 已不在保留历史内 → RejoinAccepted=false，依据写明历史：" +
                        room.Sync.RejoinReason);
                    Expect(!room.Host.WasRejoin(room.Port.LastClientId) && room.Port.LastRejoin == 0,
                        "(z3) 主机 WasRejoin=false → 世界传输按首次加入发送整图");
                    Expect(room.TickUntilWorldEnd(),
                        "(z3) 整图在本连接上完成（客户端收到 WorldEnd）");
                    Expect(room.Exporter.Calls == exportsBefore + 1 &&
                           room.Count(TerrainMessageType.WorldBegin) == 1 &&
                           room.Count(TerrainMessageType.WorldEnd) == 1,
                        "(z3) 恰好一次整图（Exporter " + exportsBefore + " → " + room.Exporter.Calls +
                        " / Begin=" + room.Count(TerrainMessageType.WorldBegin) +
                        " / End=" + room.Count(TerrainMessageType.WorldEnd) + "）");
                }

                // ---- (z4) 追帧请求在握手后才越界（历史被裁剪）→ 明确拒绝 → 客户端以首次加入重连 ----
                {
                    var historyOptions = new TerrainSyncOptions { CatchUpMaxJournalEntries = 2 };
                    using var room = new TransferRoom(historyOptions);
                    room.HostWorld.MakeValid(key);
                    room.ClientWorld.MakeValid(key);
                    room.JoinSync(false, string.Empty, 0L);
                    room.Sync.SendInterest(key, 2);
                    room.TickUntil(() => room.Host.SubscriberCount(key) == 1, 300);
                    for (int i = 0; i < 2; i++)
                        room.Change(key, new CellEdit(3, 100 + i, 3, 30 + i, 0));

                    int firstId = room.Port.LastClientId;
                    room.Client.Dispose();
                    room.Sync = null;
                    Expect(WaitFor(() => room.Server.StateOf(firstId) == ChannelState.Failed ||
                        room.Server.StateOf(firstId) == ChannelState.Disconnected, 3000),
                        "(z4) 主机发现连接不可达");
                    room.Tick(3);

                    // 重连握手时序号 2 仍在历史内（保留 1..2）→ 接受重连。
                    room.Client = room.Connect();
                    room.JoinSync(true, TransferRoom.HostSessionId, 2L);
                    Expect(room.Sync.RejoinAccepted, "(z4) 握手时序号 2 仍在历史内 → 接受重连");
                    // 客户端还没发追帧请求之前，历史被后续改动挤出（只保留 5..6）。
                    for (int i = 2; i < 6; i++)
                        room.Change(key, new CellEdit(3, 100 + i, 3, 30 + i, 0));
                    var rejectedReasons = new List<string>();
                    room.Sync.CatchUpRejected += reason => rejectedReasons.Add(reason);
                    room.Sync.SendInterest(key, 2);
                    room.TickUntil(() => room.Host.SubscriberCount(key) == 1, 300);
                    room.Sync.Tick();          // 发出追帧请求（序号 2）
                    room.TickUntil(() => rejectedReasons.Count > 0, 300);
                    Expect(rejectedReasons.Count == 1 && rejectedReasons[0].Contains("历史"),
                        "(z4) 追帧请求越界 → 主机明确拒绝，原因写历史：" +
                        (rejectedReasons.Count > 0 ? rejectedReasons[0] : "(没有收到)"));
                    Expect(!room.Sync.CatchUpCompleted &&
                           room.Sync.CatchUpRejectReason.Contains("历史"),
                        "(z4) 客户端不会假装追帧成功（CatchUpCompleted=false）");
                    Expect(room.Host.GetSnapshot().CatchUpRejected == 1,
                        "(z4) 主机统计 CatchUpRejected=1");

                    // 客户端按拒绝走"整图重下"：作废会话 → 断开 → 以首次加入重连 → 主机发整图。
                    room.Client.Dispose();
                    room.Sync = null;
                    Expect(WaitFor(() => room.Server.StateOf(room.Port.LastClientId) == ChannelState.Failed ||
                        room.Server.StateOf(room.Port.LastClientId) == ChannelState.Disconnected, 3000),
                        "(z4) 追帧被拒后客户端断开");
                    room.Tick(3);
                    int exportsBefore = room.Exporter.Calls;
                    room.Exporter.Pak = MakeWorldPak(40000);
                    room.Client = room.Connect();
                    room.JoinSync(false, string.Empty, 0L);
                    Expect(room.TickUntilWorldEnd(),
                        "(z4) 以首次加入身份重连后收到整图（WorldEnd）");
                    Expect(room.Exporter.Calls == exportsBefore + 1 &&
                           room.Count(TerrainMessageType.WorldBegin) == 1 &&
                           room.Count(TerrainMessageType.WorldEnd) == 1,
                        "(z4) 整图恰好重下一次（Exporter " + exportsBefore + " → " + room.Exporter.Calls +
                        " / Begin=" + room.Count(TerrainMessageType.WorldBegin) +
                        " / End=" + room.Count(TerrainMessageType.WorldEnd) + "）");
                }

                // ---- (z5) 有界历史（条数上限）不破坏追帧：只重放仍在历史里的那段，客户端最终与主机一致 ----
                {
                    var historyOptions = new TerrainSyncOptions { CatchUpMaxJournalEntries = 4 };
                    using var room = new TransferRoom(historyOptions);
                    room.HostWorld.MakeValid(key);
                    room.ClientWorld.MakeValid(key);
                    room.JoinSync(false, string.Empty, 0L);
                    room.Sync.SendInterest(key, 2);
                    Expect(room.TickUntil(() => room.Host.SubscriberCount(key) == 1, 300),
                        "(z5) 客户端已订阅该块");
                    for (int i = 0; i < 6; i++)
                    {
                        room.Change(key, new CellEdit(4, 100 + i, 4, 40 + i, 0));
                        room.TickUntil(() => room.Sync.LastAppliedChangeSequence >= i + 1, 300);
                    }
                    long applied = room.Sync.LastAppliedChangeSequence;
                    Expect(applied == 6, "(z5) 断线前客户端已应用到 6（真实在线应用）");

                    int firstId = room.Port.LastClientId;
                    room.Client.Dispose();
                    room.Sync = null;
                    Expect(WaitFor(() => room.Server.StateOf(firstId) == ChannelState.Failed ||
                        room.Server.StateOf(firstId) == ChannelState.Disconnected, 3000),
                        "(z5) 主机发现连接不可达");
                    room.Tick(3);
                    // 断线期间 4 条改动，条数上限 4 ⇒ 历史只剩 7..10。
                    for (int i = 6; i < 10; i++)
                        room.Change(key, new CellEdit(4, 100 + i, 4, 40 + i, 0));
                    TerrainHostSnapshot evicted = room.Host.GetSnapshot();
                    Expect(evicted.ChangeSequence == 10 && evicted.ChangeJournalCount == 4 &&
                           evicted.ChangeJournalOldestSequence == 7 && evicted.ChangeJournalEvictions == 6,
                        "(z5) 有界历史：水位 10 / 保留 [7..10] / 裁掉 6 条（实际 " +
                        evicted.ChangeSequence + " / " + evicted.ChangeJournalCount +
                        " / 最旧 " + evicted.ChangeJournalOldestSequence +
                        " / 裁掉 " + evicted.ChangeJournalEvictions + "）");
                    int exportsBefore = room.Exporter.Calls;
                    room.Exporter.Pak = MakeWorldPak(40000);
                    room.Client = room.Connect();
                    room.JoinSync(true, TransferRoom.HostSessionId, applied);   // 6 = 最旧(7)-1 → 仍可追帧
                    Expect(room.Sync.RejoinAccepted,
                        "(z5) 上次应用 6 = 最旧保留 7 的前一条 → 仍接受重连（不用整图重下）");
                    room.Sync.SendInterest(key, 2);
                    room.TickUntil(() => room.Host.SubscriberCount(key) == 1, 300);
                    room.Sync.Tick();
                    Expect(room.TickUntil(() => room.Sync.CatchUpCompleted, 500),
                        "(z5) 追帧完成");
                    Expect(room.Sync.CatchUpAppliedChanges == 4,
                        "(z5) 只重放仍在历史里的 4 条（7..10）：" + room.Sync.CatchUpAppliedChanges);
                    Expect(room.Sync.LastAppliedChangeSequence == 10 &&
                           room.HostWorld.DigestOf(key) == room.ClientWorld.DigestOf(key),
                        "(z5) 追帧后客户端到水位 10 且内容与主机一致（有界裁剪没有破坏重放）");
                    Expect(room.Exporter.Calls == exportsBefore &&
                           room.Count(TerrainMessageType.WorldBegin) == 0,
                        "(z5) 有界历史下重连仍不下整图（Exporter " + exportsBefore + " → " +
                        room.Exporter.Calls + " / Begin=" + room.Count(TerrainMessageType.WorldBegin) + "）");
                }
            }

            Console.WriteLine("\n---------------------------------------------");
            Console.WriteLine("结果：" + s_pass + "/" + (s_pass + s_fail) + " 通过" +
                (s_fail > 0 ? "，" + s_fail + " 失败" : ""));
            if (s_fail > 0)
            {
                Console.WriteLine("失败项：");
                foreach (string f in s_failures) Console.WriteLine("  - " + f);
                return 1;
            }
            Console.WriteLine("RESULT terrain digest/verdict chain cases passed");
            return 0;
        }
    }
}

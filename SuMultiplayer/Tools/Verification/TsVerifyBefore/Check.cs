using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;

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

        // ------------------------------------------------------------------ 合成世界

        /// <summary>
        /// 方块世界。主机侧 AutoLoad=false：RequestLoad 只**登记**，内容由测试显式 MakeValid
        /// （模拟"引擎稍后才把块加载出来"）。客户端侧 AutoLoad=true（本地加载即内容可用）。
        /// </summary>
        private sealed class World : ITerrainWorld
        {
            private readonly Dictionary<ChunkKey, int[]> m_chunks = new Dictionary<ChunkKey, int[]>();
            private readonly HashSet<ChunkKey> m_divergent = new HashSet<ChunkKey>();
            private int m_requestLoads;

            public bool AutoLoad;

            public int RequestLoads => Volatile.Read(ref m_requestLoads);

            public static int Index(int x, int y, int z) => x + z * 16 + y * 256;

            public void MakeValid(ChunkKey key)
            {
                if (m_chunks.ContainsKey(key)) return;
                m_chunks[key] = Build(key, m_divergent.Contains(key));
            }

            public void MakeDivergent(ChunkKey key) { m_divergent.Add(key); if (m_chunks.ContainsKey(key)) m_chunks[key] = Build(key, true); }
            public void Unload(ChunkKey key) { m_chunks.Remove(key); }
            public bool HasContents(ChunkKey key) => m_chunks.ContainsKey(key);
            public IEnumerable<ChunkKey> LoadedChunks => m_chunks.Keys;

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
        }

        /// <summary>
        /// 一个"主机 + 多个玩家端"的房间（真实 TCP + 真实 <see cref="TerrainSyncHost"/> 源码），
        /// 专门验证逐区块欠更新计数：改动标记 / 加入不补 / 核对清零 / 离开清掉 / 卸载不清 / 重进全 0。
        /// </summary>
        private sealed class PendingRoom : IDisposable
        {
            public readonly World HostWorld = new World();
            public readonly TcpTerrainServer Server;
            public readonly TerrainSyncHost Host;
            public readonly ConcurrentQueue<ReceivedFrame> Inbox = new ConcurrentQueue<ReceivedFrame>();
            /// <summary>TerrainSyncHost 抛出的计数器生命周期日志（模块里就是把它写进游戏日志的）。</summary>
            public readonly List<string> Traces = new List<string>();
            private readonly List<PendingPeer> m_peers = new List<PendingPeer>();

            public PendingRoom()
            {
                Server = new TcpTerrainServer(0, 256, IPAddress.Loopback);
                Server.Start();
                Host = new TerrainSyncHost(HostWorld, Server, new TerrainSyncOptions(), attachToChannel: false);
                Host.PendingTrace += message => Traces.Add(message);
                Server.FrameReceived += frame => Inbox.Enqueue(frame);
            }

            public void Pump()
            {
                while (Inbox.TryDequeue(out ReceivedFrame frame)) Host.ProcessFrame(frame);
            }

            /// <summary>接一个玩家端上来并完成 Hello：返回它（含主机侧 clientId）。</summary>
            public PendingPeer AddClient(string name)
            {
                var peer = new PendingPeer { Name = name };
                peer.Channel = new TcpTerrainClient("127.0.0.1", Server.Port);
                if (!peer.Channel.Connect(3000)) throw new InvalidOperationException("连不上本机自检服务器");
                m_peers.Add(peer);
                var writer = new PayloadWriter();
                writer.Byte((byte)ClientKind.Player).Text(name).Byte(1).Byte(1).Int32(2).Text("pending-self-check");
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

            public void SendReport(PendingPeer peer, ChunkKey key, uint hash)
            {
                var writer = new PayloadWriter();
                writer.Int32(1).Int32(key.X).Int32(key.Z).UInt32(hash);
                peer.Channel.TrySend(TerrainMessageType.ChunkDigestReport, writer.ToArray());
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

        // ------------------------------------------------------------------ 主流程

        private static int Main()
        {
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
                Expect(WaitFor(() => { room.PumpClient(); return room.Sync.StateOf(key) != ChunkFreshness.Stale; }, 3000),
                    "回传落地后客户端重新上报（状态离开 Stale）");
                Expect(room.Sync.Tick() >= 1, "客户端把覆盖后的摘要重报出去");
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

            Section("H. 对端断开时欠着的上报明确作废（不静默、也不对着断线一直重请）");
            {
                var options = new TerrainSyncOptions();
                using var room = new Room(options, withSyncClient: false);
                room.SendHello("client-H");
                room.SendInterest(new ChunkKey(0, 0), 2);
                WaitFor(() => { room.PumpInbox(); return room.Host.ClientCount > 0; }, 2000);
                room.PumpInbox();

                var key = new ChunkKey(4, 0);
                room.SendReport(key, 0xAABBCCDDu);
                Expect(room.WaitVerdicts(1, 3000), "主机回了 NotReady");
                Expect(room.Host.GetSnapshot().PendingReports == 1, "待裁决 = 1");
                int loadsBefore = room.HostWorld.RequestLoads;
                room.Client.Dispose();                              // 直接断链（不发 Bye）
                Expect(WaitFor(() => { room.Host.Tick(); return room.Host.GetSnapshot().PendingReports == 0; }, 3000),
                    "断线后主机把这条欠账作废（待裁决清零）");
                Expect(room.UnresolvedLines >= 1 &&
                      room.UnresolvedMessages[room.UnresolvedLines - 1].Contains("连接已不可用"),
                    "写了一条明确原因（" + room.UnresolvedLines + " 行）：" +
                    (room.UnresolvedLines > 0 ? room.UnresolvedMessages[room.UnresolvedLines - 1] : "(空)"));
                int loadsAfterDrop = room.HostWorld.RequestLoads;
                for (int i = 0; i < 20; i++) room.Host.Tick();
                Expect(room.HostWorld.RequestLoads == loadsAfterDrop,
                    "作废后不再向引擎重请这块（" + loadsBefore + "→" + loadsAfterDrop + "→" + room.HostWorld.RequestLoads + "）");
            }

            Section("I. 欠更新计数（实时地图 chunks[].n）：改动标记 / 加入不补 / 核对清零 / 离开清掉 / 卸载不清 / 重进全 0");
            {
                using var room = new PendingRoom();
                var edit = new CellEdit(1, 2, 3, 2, 0);
                var keyA = new ChunkKey(0, 0);
                room.HostWorld.MakeValid(keyA);

                // (a) 一个在线客户端 + 区块有改动 → 计数 1
                PendingPeer a = room.AddClient("client-A");
                Expect(room.Host.PendingClientCount(keyA) == 0, "改动前该区块计数 = 0（0 不标注）");
                Expect(room.Host.BroadcastCellChange(keyA, edit) == 0,
                    "该块没有订阅者 → 广播发出 0 份（计数与\"谁订了这块\"无关）");
                Expect(room.Host.PendingClientCount(keyA) == 1, "区块有改动 → 计数 = 1（在线玩家端 A 欠最新）");
                Expect(room.Traces.Count > 0 && LastLine(room.Traces).Contains("有改动") &&
                       LastLine(room.Traces).Contains("记为 1 个在线客户端"),
                    "加标记写了明确日志：" + LastLine(room.Traces));

                // (f) 主机卸载这一块 → 计数仍在（与主机的加载集合无关）
                room.HostWorld.Unload(keyA);
                Expect(!room.HostWorld.HasContents(keyA), "主机已卸载这一块（内容不在手上）");
                Expect(room.Host.PendingClientCount(keyA) == 1, "主机离开这一块后计数仍是 1（卸载 / 离开不清零）");
                room.HostWorld.MakeValid(keyA);

                // (b) 第二个客户端加入 → 计数仍是 1（新客户端整图就是最新基线，不补标记）
                PendingPeer b = room.AddClient("client-B");
                Expect(room.Host.PendingClientCount(keyA) == 1, "第二个客户端加入 → 计数仍是 1（加入时不补标记）");

                // (c) A 核对通过（Match）→ 计数 0（网页不画数字）；s 语义不变
                room.SendReport(a, keyA, room.HostWorld.DigestOf(keyA));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyA) == 0; }, 3000),
                    "A 上报真实摘要 → 主机裁决 Match → 计数 = 0（网页不标注）");
                Expect(room.Host.VerifyStateOf(keyA) == ChunkVerifyState.Verified,
                    "验证状态语义未变（Match → Verified 绿框）");
                Expect(LastLine(room.Traces).Contains("核对通过") && LastLine(room.Traces).Contains("还剩 0 个客户端"),
                    "移除标记写了明确日志：" + LastLine(room.Traces));

                // (d) 两个区块同时标上 A+B（各 2）→ B 退出联机 → 它在这两块上的标记全部去掉
                var keyB = new ChunkKey(1, 1);
                var keyC = new ChunkKey(2, 2);
                room.HostWorld.MakeValid(keyB);
                room.HostWorld.MakeValid(keyC);
                room.Host.BroadcastCellChange(keyB, edit);
                room.Host.BroadcastCellChange(keyC, edit);
                Expect(room.Host.PendingClientCount(keyB) == 2 && room.Host.PendingClientCount(keyC) == 2,
                    "改动时两个在线客户端都记上（各 2）");
                TerrainHostSnapshot snap = room.Host.GetSnapshot();
                Expect(snap.PendingMarks == 2 && snap.PendingClients == 4,
                    "快照合计 = 标块 2 · 客户端 4（实际 " + snap.PendingMarks + " / " + snap.PendingClients + "）");
                room.SendBye(b);
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyB) == 1 &&
                        room.Host.PendingClientCount(keyC) == 1; }, 3000),
                    "B 退出联机（Bye）→ 它在这两块上的标记全部去掉（各剩 A = 1）");
                Expect(LastLine(room.Traces).Contains("个区块的欠更新计数里移除它的标记"),
                    "移除它的全部标记写了一行日志：" + LastLine(room.Traces));

                // (e) 重新加入 → 它什么都不欠（计数不变）；A 全部核对完后整体归零
                PendingPeer c = room.AddClient("client-B2");
                Expect(room.Host.PendingClientCount(keyB) == 1 && room.Host.PendingClientCount(keyC) == 1,
                    "重新加入的客户端不补任何标记（仍是 " + room.Host.PendingClientCount(keyB) + " / " +
                    room.Host.PendingClientCount(keyC) + "）→ 它自己全为 0");
                Expect(room.Host.GetSnapshot().PendingClients == 2, "重新加入后合计仍是 2（只有 A 欠）");
                room.SendReport(a, keyB, room.HostWorld.DigestOf(keyB));
                room.SendReport(a, keyC, room.HostWorld.DigestOf(keyC));
                Expect(WaitFor(() => { room.Pump(); return room.Host.PendingClientCount(keyB) == 0 &&
                        room.Host.PendingClientCount(keyC) == 0; }, 3000),
                    "A 核对这两块 → 两块都归 0（都不标注）");
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
                        .Text("identity-H3").Byte(1);
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

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;

namespace LmVerify
{
    /// <summary>
    /// LiveMap **v7** 载荷 + 工具 /state JSON 的无设备自检：
    ///   · 编解码往返（每个角色真实坐标 / 每个玩家端已加载区块 / **每条客户端区块自己的校验状态 s**）；
    ///   · v7 新字段：clients[].chunks 条目 = [cx, cz, s] —— State 逐条往返（含 0 也照实带回来）；
    ///   · v7 每条客户端条目比 v6 多 1 字节（状态 byte）；v8 每个客户端头部多 4 字节（currentLoaded），满载整批仍必须 < 帧上限；
    ///   · 旧版本号（v6 / v5 / v4 / v3）一律**明确失败**（版本字节是唯一闸门，绝不误解新字段）；
    ///   · 工具 BuildStateJson 的字段（players[] / clients[] / 每条 s / bounds 并集 / counts / payloadVersion=8）；
    ///   · 两个身份各自的 s 不串（clients[] 按身份稳定键归档，各自条目带自己的状态）；
    ///   · 单帧上限与"超过上限就明确抛错、绝不截断"。
    /// </summary>
    internal static class Verify
    {
        private static int s_pass, s_fail;
        private static readonly List<string> s_failures = new List<string>();

        private static void Check(bool ok, string message)
        {
            if (ok) { s_pass++; Console.WriteLine("  \u2713 " + message); return; }
            s_fail++; s_failures.Add(message); Console.WriteLine("  \u2717 " + message);
        }

        private static void Section(string name) => Console.WriteLine("\n== " + name + " ==");

        private static LiveMapChunk Chunk(int x, int z, byte state)
        {
            var chunk = new LiveMapChunk { X = x, Z = z, State = state };
            for (int i = 0; i < LiveMapChunk.Columns; i++) { chunk.Contents[i] = 2; chunk.Top[i] = 64; }
            return chunk;
        }

        // v7：每个客户端条目的校验状态按确定规则生成（含 0），便于逐条核对"原样往返、不串位"。
        private static byte StateOf(int index)
        {
            int cycle = index % 5;
            return cycle == 4 ? (byte)0 : (byte)((index % 4) + 1);
        }

        // v8：reportedCount = 会话累计上报条数（只增不减）；clientChunkCount = 本次快照的条目数（= 当前内存区块数）。
        // currentLoadedCount 缺省 = clientChunkCount（正常样本）；只有「卸载后 reported > currentLoaded」这类用例才显式给。
        private static LiveMapSnapshot BuildSnapshot(int clientChunkCount, int reportedCount, int currentLoadedCount = -1)
        {
            if (currentLoadedCount < 0) currentLoadedCount = clientChunkCount;
            var snapshot = new LiveMapSnapshot
            {
                HasViewerCell = true, CellX = -75, CellY = 70, CellZ = 140,
                View = LiveMapView.All, ChunkRadius = 0,
                WatchedClientId = 1234, WatchedClientName = "Android User",
                WatchedStableKey = "uk-android"           // v6：被观察身份的稳定键
            };
            snapshot.Centers.Add(new LiveMapCenter
            {
                ClientId = -1, StableKey = "host", Name = "Host", IsHost = true, ChunkX = -5, ChunkZ = 8,
                HasCell = true, CellX = -75, CellY = 70, CellZ = 140
            });
            snapshot.Centers.Add(new LiveMapCenter
            {
                ClientId = 1234, StableKey = "uk-android", Name = "Android User", IsHost = false, ChunkX = -13, ChunkZ = 13,
                HasCell = true, CellX = -207, CellY = 70, CellZ = 217
            });
            snapshot.Clients.Add(new LiveMapClient
            {
                ClientId = 1234, StableKey = "uk-android", Name = "Android User",
                ReportedChunkCount = reportedCount,            // v8：会话累计（仅参考）
                CurrentLoadedChunkCount = currentLoadedCount    // v8：客户端当前内存区块数（= 本快照条目数）
            });
            // 客户端已加载区块：3×3 一片 + 若干额外块（用于撑批数与上限用例）
            var extra = new List<int[]>();
            for (int x = -14; x <= -12; x++)
            {
                for (int z = 12; z <= 14; z++) extra.Add(new[] { x, z });
            }
            for (int i = 0; i < clientChunkCount; i++)
            {
                int[] key = i < extra.Count ? extra[i] : new[] { 100 + (i / 64), 200 + (i % 64) };
                snapshot.ClientChunks.Add(new LiveMapClientChunk
                {
                    ClientId = 1234,
                    StableKey = "uk-android",
                    X = key[0],
                    Z = key[1],
                    State = StateOf(i)          // v7：该块相对这个身份自己的校验状态（含 0）
                });
            }
            for (int i = 0; i < 9; i++) snapshot.Chunks.Add(Chunk(-6 + (i % 3), 7 + (i / 3), (byte)(i == 0 ? 1 : 2)));
            snapshot.Chunks.Add(Chunk(-12, 13, 3));
            // v5：逐块"还有几个在线客户端没拿到最新内容"（0 = 不标注）。第 1 块 1 个、第 2 块 2 个。
            snapshot.Chunks[0].PendingClients = 1;
            snapshot.Chunks[1].PendingClients = 2;
            return snapshot;
        }

        /// <summary>两个身份各自上报同一块 / 不同块，且状态不同 —— 证明"每块按它自己的身份着色，状态不串"。</summary>
        private static LiveMapSnapshot BuildTwoIdentitySnapshot()
        {
            var snapshot = new LiveMapSnapshot
            {
                HasViewerCell = true, CellX = -75, CellY = 70, CellZ = 140,
                View = LiveMapView.All, ChunkRadius = 0,
                WatchedClientId = 1234, WatchedClientName = "A", WatchedStableKey = "uk-a"
            };
            snapshot.Centers.Add(new LiveMapCenter
            {
                ClientId = -1, StableKey = "host", Name = "Host", IsHost = true, ChunkX = -5, ChunkZ = 8,
                HasCell = true, CellX = -75, CellY = 70, CellZ = 140
            });
            snapshot.Centers.Add(new LiveMapCenter
            {
                ClientId = 1234, StableKey = "uk-a", Name = "A", IsHost = false, ChunkX = -13, ChunkZ = 13,
                HasCell = true, CellX = -207, CellY = 70, CellZ = 217
            });
            snapshot.Centers.Add(new LiveMapCenter
            {
                ClientId = 4321, StableKey = "uk-b", Name = "B", IsHost = false, ChunkX = -5, ChunkZ = 8,
                HasCell = true, CellX = -75, CellY = 70, CellZ = 140
            });
            snapshot.Clients.Add(new LiveMapClient { ClientId = 1234, StableKey = "uk-a", Name = "A", ReportedChunkCount = 2, CurrentLoadedChunkCount = 2 });
            snapshot.Clients.Add(new LiveMapClient { ClientId = 4321, StableKey = "uk-b", Name = "B", ReportedChunkCount = 2, CurrentLoadedChunkCount = 2 });
            // 同一块 (-13,13) 两个人都有，但状态不同：A=2 绿、B=1 黄（绝不能互相套用）
            snapshot.ClientChunks.Add(new LiveMapClientChunk { ClientId = 1234, StableKey = "uk-a", X = -13, Z = 13, State = 2 });
            snapshot.ClientChunks.Add(new LiveMapClientChunk { ClientId = 1234, StableKey = "uk-a", X = -12, Z = 13, State = 4 });
            snapshot.ClientChunks.Add(new LiveMapClientChunk { ClientId = 4321, StableKey = "uk-b", X = -13, Z = 13, State = 1 });
            snapshot.ClientChunks.Add(new LiveMapClientChunk { ClientId = 4321, StableKey = "uk-b", X = -5, Z = 8, State = 3 });
            snapshot.Chunks.Add(Chunk(-5, 8, 2));
            return snapshot;
        }

        private static void SetStaticField(string name, object value) =>
            typeof(SuMPLiveMap.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);

        private static string CallBuildStateJson() =>
            (string)typeof(SuMPLiveMap.Program).GetMethod("BuildStateJson", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);

        private static LiveMapSnapshot Merge(List<byte[]> batches, out int decodedBatches)
        {
            var merged = new LiveMapSnapshot();
            decodedBatches = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                if (!LiveMapCodec.TryDecodeBatch(batches[i], out LiveMapBatch batch)) continue;
                decodedBatches++;
                if (i == 0) merged = batch.Header;
                merged.Chunks.AddRange(batch.Chunks);
                merged.ClientChunks.AddRange(batch.ClientChunks);
            }
            return merged;
        }

        private static int Main()
        {
            Section("1. 载荷版本 v8 与编解码往返（地形 / 中心 / 客户端区块 / 每条状态 s / 头部 currentLoaded）");
            {
                LiveMapSnapshot snapshot = BuildSnapshot(9, 9);
                List<byte[]> batches = LiveMapCodec.EncodeBatches(snapshot, 42);
                Check(LiveMapCodec.Version == 8,
                    "LiveMapCodec.Version = " + LiveMapCodec.Version + "（v8：客户端头部记录末尾追加 currentLoaded int32）");
                Check(batches.Count == 1, "9 块地形 + 9 条客户端条目只占 1 批（实际 " + batches.Count + "）");
                Check(batches[0].Length <= TerrainFrame.MaxPayloadBytes,
                    "单批 " + batches[0].Length + " 字节 ≤ 帧上限 " + TerrainFrame.MaxPayloadBytes);

                int decodedBatches;
                LiveMapSnapshot merged = Merge(batches, out decodedBatches);
                Check(decodedBatches == batches.Count, "全部 " + batches.Count + " 批都解码成功");

                LiveMapCenter host = merged.Centers[0], client = merged.Centers[1];
                Check(host.HasCell && host.CellX == -75 && host.CellY == 70 && host.CellZ == 140,
                    "主机中心带真实坐标 (-75,70,140)");
                Check(client.HasCell && client.CellX == -207 && client.CellY == 70 && client.CellZ == 217,
                    "玩家端中心带真实坐标 (-207,70,217)（不是按区块猜的）");
                Check(client.CellReason == null || client.CellReason.Length == 0, "有坐标时 cellReason 为空（不写假原因）");
                Check(merged.WatchedStableKey == "uk-android", "v6 的被观察身份稳定键随批往返（uk-android）");
                Check(merged.Chunks.Count == 10, "地形块 10 块合并回来（实际 " + merged.Chunks.Count + "）");
                Check(merged.ClientChunks.Count == 9, "玩家端已加载区块 9 条合并回来（实际 " + merged.ClientChunks.Count + "）");
                Check(merged.Clients.Count == 1 && merged.Clients[0].ClientId == 1234 &&
                      merged.Clients[0].ReportedChunkCount == 9, "玩家端头部（id / 会话累计上报条数）随批带回来");
                Check(merged.Clients[0].CurrentLoadedChunkCount == 9,
                    "v8 头部 currentLoaded = 9（客户端当前内存区块数）随批往返");

                // v7：每条客户端区块的 State 逐条往返（含 0 也照实带回来，绝不丢字段 / 不串位）
                bool statesOk = true, keysOk = true;
                for (int i = 0; i < merged.ClientChunks.Count && i < 9; i++)
                {
                    LiveMapClientChunk entry = merged.ClientChunks[i];
                    if (entry.State != StateOf(i)) statesOk = false;
                    if (entry.StableKey != "uk-android" || entry.ClientId != 1234) keysOk = false;
                }
                Check(statesOk, "v7 每条客户端条目的 State 逐条往返（9 条：" + StatesOf(9) + "，含 0 也原样）");
                Check(keysOk, "每条条目仍带自己的身份稳定键 uk-android / clientId 1234（v6 字段不丢）");
                Check(merged.ClientChunks[0].State == 1 && merged.ClientChunks[1].State == 2 &&
                      merged.ClientChunks[3].State == 4 && merged.ClientChunks[4].State == 0,
                    "按块对齐：第 0 条 s=1、第 1 条 s=2、第 3 条 s=4、第 4 条 s=0（不串位）");

                // v5：逐块欠更新计数原样往返（0 的块也照实带回来）
                int sumPending = 0, nonzero = 0, zero = 0;
                for (int i = 0; i < merged.Chunks.Count; i++)
                {
                    sumPending += merged.Chunks[i].PendingClients;
                    if (merged.Chunks[i].PendingClients > 0) nonzero++; else zero++;
                }
                Check(sumPending == 3 && nonzero == 2 && zero == 8,
                    "chunks[].n 逐块往返：合计 " + sumPending + "（2 块 >0 / 8 块 =0）");
                Check(merged.Chunks[0].PendingClients == 1 && merged.Chunks[1].PendingClients == 2,
                    "第 1 块 n=1、第 2 块 n=2（按块对齐，不串位）");
            }

            Section("2. 坐标读不到时必须带原因（cellReason），绝不留空");
            {
                var snapshot = new LiveMapSnapshot { View = LiveMapView.All };
                snapshot.Centers.Add(new LiveMapCenter
                {
                    ClientId = 7, Name = "P7", IsHost = false, ChunkX = 1, ChunkZ = 2,
                    HasCell = false, CellReason = "clientId=7 的联机角色还没有 ComponentBody（位置读不到）"
                });
                Check(LiveMapCodec.TryDecodeBatch(LiveMapCodec.EncodeBatches(snapshot, 7)[0], out LiveMapBatch batch),
                    "无坐标的中心仍能编解码");
                Check(!batch.Header.Centers[0].HasCell &&
                      batch.Header.Centers[0].CellReason.Contains("ComponentBody"),
                    "解码后 hasCell=false 且原因原样带回来（网页据此明说\u201c坐标未知\u201d）：" + batch.Header.Centers[0].CellReason);
            }

            Section("3. 旧版本 / 坏载荷必须明确失败（绝不误解；v7 也不能再被当 v8 读）");
            {
                byte[] good = LiveMapCodec.EncodeBatches(BuildSnapshot(9, 9), 5)[0];
                foreach (int oldVersion in new[] { 7, 6, 5, 4, 3 })
                {
                    byte[] old = (byte[])good.Clone();
                    old[0] = (byte)oldVersion;
                    Check(!LiveMapCodec.TryDecodeBatch(old, out _),
                        "版本号 " + oldVersion + " 的载荷解码失败（旧消费者不会把新状态字节当别的字段读）");
                }
                byte[] truncated = new byte[good.Length - 3];
                Array.Copy(good, truncated, truncated.Length);
                Check(!LiveMapCodec.TryDecodeBatch(truncated, out _), "截断的载荷解码失败");
                byte[] padded = new byte[good.Length + 4];
                Array.Copy(good, padded, good.Length);
                Check(!LiveMapCodec.TryDecodeBatch(padded, out _), "尾部多出字节的载荷解码失败（线格式不匹配）");
                Check(LiveMapCodec.TryDecodeBatch(good, out _), "未改动的 v7 载荷解码成功（闸门只拦旧版本，不误伤自己）");
            }

            Section("4. 客户端区域随批分发：不截断、超上限明确抛错、v8 每条 27 B + 头部 4 B 仍 < 帧上限");
            {
                // v7 每条**比 v6 多 1 字节状态**；v8 每条不变（多是客户端头部 4 B）。用"条目数 1 vs 2"量出真实每条开销。
                // 条目字节 = clientId(4) + X(4) + Z(4) + 稳定键文本(4 长度 + 10 B "uk-android") + State(1) = 27 B（v6 = 26 B）。
                byte[] one = LiveMapCodec.EncodeBatches(BuildSnapshot(1, 1), 11)[0];
                byte[] two = LiveMapCodec.EncodeBatches(BuildSnapshot(2, 2), 11)[0];
                Check(two.Length - one.Length == 27,
                    "每条客户端条目实测 " + (two.Length - one.Length) +
                    " B = 4+4+4+(4+10 稳定键)+1(状态)（v7 比 v6 多且只多这 1 个状态字节）");

                LiveMapSnapshot worst = BuildSnapshot(256, 256);
                for (int i = 10; i < 16; i++) worst.Chunks.Add(Chunk(i, i, 2));
                List<byte[]> worstBatches = LiveMapCodec.EncodeBatches(worst, 12);
                Check(worstBatches.Count == 1, "满载单批（16 块地形 + 256 条客户端条目）仍是 1 批（实际 " + worstBatches.Count + "）");
                Check(worstBatches[0].Length <= TerrainFrame.MaxPayloadBytes,
                    "满载单批 " + worstBatches[0].Length + " B ≤ 帧上限 " + TerrainFrame.MaxPayloadBytes +
                    "（16×776 + 256×27 仍有余量）");

                LiveMapSnapshot big = BuildSnapshot(3000, 3000);
                List<byte[]> batches = LiveMapCodec.EncodeBatches(big, 9);
                int expected = (3000 + LiveMapCodec.MaxClientChunksPerBatch - 1) / LiveMapCodec.MaxClientChunksPerBatch;
                Check(batches.Count == Math.Max(1, expected), "3000 条客户端条目切成 " + batches.Count +
                    " 批（每批 ≤ " + LiveMapCodec.MaxClientChunksPerBatch + "）");
                bool allUnder = true;
                for (int i = 0; i < batches.Count; i++) if (batches[i].Length > TerrainFrame.MaxPayloadBytes) allUnder = false;
                Check(allUnder, "所有批都在单帧上限内（最大 " + MaxLen(batches) + " 字节）");

                int total = 0;
                bool orderKept = true;
                int index = 0;
                for (int i = 0; i < batches.Count; i++)
                {
                    if (!LiveMapCodec.TryDecodeBatch(batches[i], out LiveMapBatch batch)) { total = -1; break; }
                    total += batch.ClientChunks.Count;
                    for (int c = 0; c < batch.ClientChunks.Count; c++, index++)
                    {
                        if (batch.ClientChunks[c].State != StateOf(index)) orderKept = false;
                    }
                }
                Check(total == 3000, "合并后客户端条目一条不少：" + total + "/3000");
                Check(orderKept, "分批后每条状态仍与它自己的位置一一对应（3000 条逐条核对 State）");

                LiveMapSnapshot over = BuildSnapshot(LiveMapCodec.MaxClientChunksTotal + 1,
                    LiveMapCodec.MaxClientChunksTotal + 1);
                bool threw = false;
                try { LiveMapCodec.EncodeBatches(over, 10); }
                catch (ArgumentException e) { threw = e.Message.Contains("超过上限"); }
                Check(threw, "超过 " + LiveMapCodec.MaxClientChunksTotal + " 条时明确抛错（不截断后假装完整）");
            }

            Section("5. 工具 GET /state：players[] / clients[reporting+currentLoaded+[cx,cz,s]] / bounds / counts / payloadVersion=8");
            {
                LiveMapSnapshot snapshot = BuildSnapshot(9, 9);
                SetStaticField("s_snapshot", snapshot);
                SetStaticField("s_snapshotMs", Environment.TickCount64);
                string json = CallBuildStateJson();
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                Check(root.GetProperty("payloadVersion").GetInt32() == 8, "/state 报 payloadVersion=8");
                Check(root.GetProperty("online").GetBoolean(), "/state online=true（年龄新鲜）");

                JsonElement players = root.GetProperty("players");
                Check(players.GetArrayLength() == 2, "players 有 2 条（主机 + 玩家端）");
                Check(players[0].GetProperty("id").GetInt32() == -1 &&
                      players[0].GetProperty("isHost").GetBoolean(), "players[0] = 主机（id=-1）");
                JsonElement hostCell = players[0].GetProperty("cell");
                Check(hostCell[0].GetInt32() == -75 && hostCell[1].GetInt32() == 70 && hostCell[2].GetInt32() == 140,
                    "players[0].cell = [-75,70,140]（主机本机玩家位置）");
                JsonElement clientCell = players[1].GetProperty("cell");
                Check(players[1].GetProperty("id").GetInt32() == 1234 &&
                      clientCell[0].GetInt32() == -207 && clientCell[2].GetInt32() == 217,
                    "players[1] = 玩家端 1234，cell = [-207,70,217]（主机侧联机角色身体位置）");
                Check(players[1].GetProperty("chunk")[0].GetInt32() == -13 &&
                      players[1].GetProperty("chunk")[1].GetInt32() == 13, "players[1].chunk = [-13,13]");
                Check(players[1].GetProperty("key").GetString() == "uk-android",
                    "players[].key = 身份稳定键（v6；不是 clientId 全值）");

                JsonElement watched = root.GetProperty("watched");
                Check(watched.GetProperty("key").GetString() == "uk-android" && watched.GetProperty("id").GetInt32() == 1234,
                    "watched = { id, key, name }（v6）");

                JsonElement center = root.GetProperty("center");
                Check(center.GetProperty("cell")[0].GetInt32() == -75 &&
                      center.GetProperty("chunk")[0].GetInt32() == -5, "center（默认主机）= 真实坐标 + 区块");

                JsonElement clients = root.GetProperty("clients");
                Check(clients.GetArrayLength() == 1, "clients 有 1 条（只有一个玩家端）");
                Check(clients[0].GetProperty("complete").GetBoolean(), "clients[0].complete = true（条数全部合并）");
                Check(clients[0].GetProperty("reported").GetInt32() == 9,
                    "reported = 9（会话累计上报条数，仅参考）");
                Check(clients[0].GetProperty("currentLoaded").GetInt32() == 9,
                    "v8 currentLoaded = 9（客户端当前内存区块数）");
                Check(clients[0].GetProperty("merged").GetInt32() == 9,
                    "merged = 9（本次真正合并的条目数）");

                // v7：每条 chunks 条目必须是 [cx, cz, s]，第三项 = 编入时该块的状态（逐条核对）
                JsonElement clientChunks = clients[0].GetProperty("chunks");
                Check(clientChunks.GetArrayLength() == 9, "clients[0].chunks 带 9 个区块条目");
                int len3 = 0, stateOk = 0, state0 = 0;
                for (int i = 0; i < clientChunks.GetArrayLength(); i++)
                {
                    JsonElement e = clientChunks[i];
                    if (e.GetArrayLength() == 3) len3++;
                    int s = e[2].GetInt32();
                    if (s == StateOf(i)) stateOk++;
                    if (s == 0) state0++;
                }
                Check(len3 == 9, "每条 clients[].chunks 条目都是 3 元 [cx,cz,s]（旧 2 元形状不再输出）");
                Check(stateOk == 9, "/state 每条 s 与编入时的状态逐条一致（含 0 也照实输出，不猜）");
                Check(state0 == 1, "状态 0 的条目如实输出 s=0（本快照 1 条：索引 4）");

                JsonElement bounds = root.GetProperty("bounds");
                Check(bounds.GetProperty("chunkMin")[0].GetInt32() == -14 && bounds.GetProperty("chunkMin")[1].GetInt32() == 7 &&
                      bounds.GetProperty("chunkMax")[0].GetInt32() == -4 && bounds.GetProperty("chunkMax")[1].GetInt32() == 14,
                    "bounds = 主机区块 ∪ 客户端区块（x -14..-4, z 7..14）");
                Check(root.GetProperty("boundsSource").GetString() == "hostChunks+clientChunks", "boundsSource 写明来源");
                Check(root.GetProperty("counts").GetProperty("clientChunks").GetInt32() == 9 &&
                      root.GetProperty("counts").GetProperty("currentLoadedChunks").GetInt32() == 9 &&
                      root.GetProperty("counts").GetProperty("players").GetInt32() == 2 &&
                      root.GetProperty("counts").GetProperty("clientsComplete").GetBoolean(),
                    "counts 与 players / clients 一致（v8 多 currentLoadedChunks = 9）");
                Check(root.GetProperty("centers").GetArrayLength() == 2, "centers[]（旧形状）仍在，旧页面继续可用");

                JsonElement chunks = root.GetProperty("chunks");
                Check(chunks.GetArrayLength() == 10, "chunks[] 有 10 条");
                int sawN = 0, sumN = 0, marks = 0;
                for (int i = 0; i < chunks.GetArrayLength(); i++)
                {
                    if (!chunks[i].TryGetProperty("n", out JsonElement nEl)) continue;
                    sawN++;
                    int n = nEl.GetInt32();
                    sumN += n;
                    if (n > 0) marks++;
                }
                Check(sawN == 10, "每个 chunks[] 条目都带 n 字段（" + sawN + "/10）");
                Check(sumN == 3 && marks == 2, "逐块 n 合计 " + sumN + "、n>0 的块 " + marks +
                    "（0 的块也照实写出 n=0，网页不画数字）");
                Check(root.GetProperty("counts").GetProperty("pendingMarks").GetInt32() == 2 &&
                      root.GetProperty("counts").GetProperty("pendingClients").GetInt32() == 3,
                    "counts.pendingMarks=2 / counts.pendingClients=3（页面图例与统计面板直接用这两个数）");
            }

            Section("6. v8：currentLoaded = 客户端当前内存区块集合（reported 只是会话累计参考值）");
            {
                // 同一身份：会话累计上报过 9 条，但客户端现在内存里只剩 3 块（其余已卸载）
                SetStaticField("s_snapshot", BuildSnapshot(3, 9, 3));
                SetStaticField("s_snapshotMs", Environment.TickCount64);
                string json = CallBuildStateJson();
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                JsonElement c0 = root.GetProperty("clients")[0];
                Check(c0.GetProperty("reported").GetInt32() == 9 && c0.GetProperty("currentLoaded").GetInt32() == 3 &&
                      c0.GetProperty("merged").GetInt32() == 3,
                    "reported=9 / currentLoaded=3 / merged=3（当前内存集合与会话累计分开报）");
                Check(c0.GetProperty("complete").GetBoolean(),
                    "merged === currentLoaded → complete=true（不能因为 reported 更大就判不完整）");
                Check(c0.GetProperty("chunks").GetArrayLength() == 3,
                    "clients[].chunks 只含当前内存的 3 块（卸载的块不再出现）");
                Check(root.GetProperty("counts").GetProperty("clientChunks").GetInt32() == 3 &&
                      root.GetProperty("counts").GetProperty("currentLoadedChunks").GetInt32() == 3,
                    "counts.clientChunks = counts.currentLoadedChunks = 3（v8 两个口径一致）");

                // 载荷里同样的语义：currentLoaded 与条目数可以不同，reported 独立
                List<byte[]> batches = LiveMapCodec.EncodeBatches(BuildSnapshot(3, 9, 3), 31);
                int decodedBatches;
                LiveMapSnapshot merged = Merge(batches, out decodedBatches);
                Check(merged.ClientChunks.Count == 3 && merged.Clients[0].CurrentLoadedChunkCount == 3 &&
                      merged.Clients[0].ReportedChunkCount == 9,
                    "载荷往返：条目 3 条 / currentLoaded 3 / reported 9（三个数各自独立、不串）");
            }

            Section("7. 两个身份：clients[] 按稳定键归档，各自的 s 不串");
            {
                SetStaticField("s_snapshot", BuildTwoIdentitySnapshot());
                SetStaticField("s_snapshotMs", Environment.TickCount64);
                string json = CallBuildStateJson();
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                JsonElement clients = root.GetProperty("clients");
                Check(clients.GetArrayLength() == 2, "两个玩家端 → clients[] 两条（各自身份一条）");
                Check(clients[0].GetProperty("key").GetString() == "uk-a" &&
                      clients[1].GetProperty("key").GetString() == "uk-b", "键分别是 uk-a / uk-b（不合并、不互串）");
                JsonElement a = clients[0].GetProperty("chunks");
                JsonElement b = clients[1].GetProperty("chunks");
                Check(a.GetArrayLength() == 2 && b.GetArrayLength() == 2, "各自的块数 2 / 2");
                Check(a[0][0].GetInt32() == -13 && a[0][1].GetInt32() == 13 && a[0][2].GetInt32() == 2 &&
                      a[1][0].GetInt32() == -12 && a[1][2].GetInt32() == 4,
                    "uk-a 的条目状态 = [2, 4]（绿 / 蓝），与编入一致");
                Check(b[0][0].GetInt32() == -13 && b[0][1].GetInt32() == 13 && b[0][2].GetInt32() == 1 &&
                      b[1][0].GetInt32() == -5 && b[1][2].GetInt32() == 3,
                    "uk-b 的条目状态 = [1, 3]（黄 / 红）；同一块 (-13,13) 两人各带自己的状态（A=2 / B=1）");
                Check(a[0][2].GetInt32() != b[0][2].GetInt32(),
                    "同一块的两个身份没有被套用成同一个状态（不伪造状态）");
            }

            Section("8. 工具 /state：当前内存区块不完整时必须 complete=false + 原因");
            {
                SetStaticField("s_snapshot", BuildSnapshot(8, 9, 9));   // 当前内存 9 块，只合并到 8 条
                SetStaticField("s_snapshotMs", Environment.TickCount64);
                string json = CallBuildStateJson();
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                Check(!root.GetProperty("clients")[0].GetProperty("complete").GetBoolean(),
                    "clients[0].complete = false（merged 8 < currentLoaded 9）");
                Check(root.GetProperty("clients")[0].GetProperty("merged").GetInt32() == 8 &&
                      root.GetProperty("clients")[0].GetProperty("currentLoaded").GetInt32() == 9 &&
                      root.GetProperty("clients")[0].GetProperty("reported").GetInt32() == 9,
                    "merged 8 / currentLoaded 9 / reported 9 如实报出（完整性基准是 currentLoaded）");
                Check(!root.GetProperty("counts").GetProperty("clientsComplete").GetBoolean() &&
                      root.GetProperty("counts").GetProperty("clientsIncompleteReason").GetString().Contains("8/9"),
                    "counts.clientsComplete=false + 原因（网页据此不画该区域）");
            }

            Section("9. 工具 /state：没有快照时的字段形状");
            {
                SetStaticField("s_snapshot", null);
                string json = CallBuildStateJson();
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                Check(!root.GetProperty("online").GetBoolean() && root.GetProperty("players").GetArrayLength() == 0 &&
                      root.GetProperty("clients").GetArrayLength() == 0 && root.GetProperty("bounds").ValueKind == JsonValueKind.Null,
                    "无快照时 online=false，players/clients 为空数组，bounds=null（不编数据）");
                Check(root.GetProperty("chunks").GetArrayLength() == 0 &&
                      root.GetProperty("counts").GetProperty("pendingMarks").GetInt32() == 0 &&
                      root.GetProperty("counts").GetProperty("pendingClients").GetInt32() == 0,
                    "无快照时 chunks 空、pendingMarks / pendingClients = 0（不编数字）");
                Check(root.GetProperty("payloadVersion").GetInt32() == 8, "无快照时 payloadVersion 仍是 8");
            }

            Console.WriteLine("\n---------------------------------------------");
            Console.WriteLine("结果：" + s_pass + "/" + (s_pass + s_fail) + " 通过" + (s_fail > 0 ? "，" + s_fail + " 失败" : ""));
            if (s_fail > 0)
            {
                Console.WriteLine("失败项：");
                foreach (string f in s_failures) Console.WriteLine("  - " + f);
                return 1;
            }
            Console.WriteLine("RESULT livemap payload+tool cases passed");
            return 0;
        }

        private static string StatesOf(int count)
        {
            var parts = new List<string>();
            for (int i = 0; i < count; i++) parts.Add(StateOf(i).ToString());
            return string.Join(",", parts);
        }

        private static int MaxLen(List<byte[]> batches)
        {
            int max = 0;
            for (int i = 0; i < batches.Count; i++) if (batches[i].Length > max) max = batches[i].Length;
            return max;
        }
    }
}

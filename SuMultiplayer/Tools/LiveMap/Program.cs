using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;

using SuMultiplayer.Protocol;
namespace SuMPLiveMap
{
    /// <summary>
    /// 实时地图桥（只读）：以 <c>ClientKind.Debug</c> 连到**正在跑的房间**，
    /// 周期请求地形（默认"主机整片已加载区域"），再通过 HTTP 给网页。
    ///
    /// 与已移除的旧调试桥的区别：那个跑的是**合成世界**（协议演示用），
    /// 这个连的是真实游戏 —— 网页上看到的每一格都是引擎里的真实地形，
    /// 主机没加载的 chunk 根本不会出现在数据里，所以网页上那片区域不会被填色。
    ///
    /// 用法：SuMPLiveMap [--host 127.0.0.1] [--port 26760] [--http 8738] [--window] [--radius 2]
    /// 演示页标记：MAP02。
    /// </summary>
    internal static class Program
    {
        private sealed class Assembly
        {
            public int SnapshotId;
            public int BatchCount;
            public LiveMapSnapshot Header;
            public readonly Dictionary<int, LiveMapBatch> Batches = new Dictionary<int, LiveMapBatch>();
        }

        private static readonly object s_gate = new object();
        private static LiveMapSnapshot s_snapshot;
        private static Assembly s_assembly;
        private static long s_snapshotMs;
        private static long s_samples;
        private static long s_droppedPartial;
        /// <summary>解包失败（版本不匹配 / 载荷坏了）的计数与只报一次的原因。</summary>
        private static long s_decodeFailures;
        private static string s_decodeFailureReason = string.Empty;
        /// <summary>
        /// 「居中角色」的选择 = **身份稳定键**（不是 clientId）：主机 = <see cref="LiveMapCodec.HostKey"/>，
        /// 玩家端 = 该身份的稳定键。玩家端重连后 clientId 会变、稳定键不变，所以选择不会被断掉。
        /// </summary>
        private static string s_selectedCenterKey = LiveMapCodec.HostKey;
        private static string s_host = "127.0.0.1";
        private static int s_port = 26760;
        private static int s_httpPort = 8738;
        private static int s_radius = 2;
        private static bool s_all = true;                // 默认：主机整片已加载区域
        private static TcpTerrainClient s_client;

        private static void Log(string message)
        {
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " [livemap] " + message);
        }

        private static int Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--host":
                        if (i + 1 < args.Length) s_host = args[++i];
                        break;
                    case "--port":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out s_port);
                        break;
                    case "--http":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out s_httpPort);
                        break;
                    case "--radius":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out s_radius);
                        break;
                    case "--window":
                        s_all = false;
                        break;
                }
            }
            s_radius = LiveMapCodec.ClampRadius(s_radius <= 0 ? 2 : s_radius);

            Log("目标房间 " + s_host + ":" + s_port + " · " +
                (s_all ? "整片已加载区域" : "窗口半径 " + s_radius + " 块") + " · HTTP 端口 " + s_httpPort);

            var http = new HttpListener();
            http.Prefixes.Add("http://127.0.0.1:" + s_httpPort + "/");
            try
            {
                http.Start();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("HTTP 监听失败：" + e.GetType().Name + " " + e.Message);
                return 2;
            }
            var httpThread = new Thread(() => HttpLoop(http)) { IsBackground = true, Name = "livemap-http" };
            httpThread.Start();
            Log("网页数据口已开：http://127.0.0.1:" + s_httpPort + "/state");
            Log("打开 Mod/SuMultiplayer/doc/live-map.html 即可（默认就指向这个地址）");

            ConnectLoop();
            return 0;
        }

        // ---------------------------------------------------------------- 房间连接

        private static void ConnectLoop()
        {
            long nextSendMs = 0;
            while (true)
            {
                if (s_client == null || s_client.State != ChannelState.Connected)
                {
                    TryConnect();
                    if (s_client == null)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                }
                long now = Environment.TickCount64;
                if (now >= nextSendMs)
                {
                    nextSendMs = now + (s_all ? 1500 : 1000);
                    SendMapRequest();
                }
                Thread.Sleep(50);
            }
        }

        private static void TryConnect()
        {
            try { s_client?.Dispose(); } catch (Exception) { }
            s_client = null;
            var client = new TcpTerrainClient(s_host, s_port, 4096);
            if (!client.Connect(3000))
            {
                try { client.Dispose(); } catch (Exception) { }
                Log("连接房间失败（主机还没按 CR 建房？）");
                return;
            }
            client.FrameReceived += OnFrame;
            // Hello 字段顺序与 TerrainSyncClient.SendHello 一致，只有 kind 是 Debug（只读，不参与权威）
            var hello = new PayloadWriter();
            hello.Byte((byte)ClientKind.Debug).Text("LiveMap").Byte(1).Byte(1).Int32(s_radius);
            client.TrySend(MessageType.Hello, hello.ToArray());
            s_client = client;
            Log("已连上房间 " + s_host + ":" + s_port + "（kind=Debug，只读）");
        }

        private static void OnFrame(ReceivedFrame frame)
        {
            if (frame.Type != MessageType.MapState) return;
            if (!LiveMapCodec.TryDecodeBatch(frame.Payload, out LiveMapBatch batch))
            {
                // 明确失败：**不猜**载荷内容。最常见的原因是主机端载荷版本与工具不一致
                // （两边必须用同一份 LiveMapCodec 重新构建）。
                lock (s_gate)
                {
                    s_decodeFailures++;
                    if (s_decodeFailureReason.Length == 0)
                    {
                        s_decodeFailureReason = "MapState 载荷解包失败（" + frame.Payload.Length +
                            " 字节）：工具期望版本 v" + LiveMapCodec.Version +
                            "，可能是主机端 Mod 与工具版本不一致（两边都要重新构建），或载荷被截断";
                        Log(s_decodeFailureReason);
                    }
                }
                return;
            }
            lock (s_gate)
            {
                if (s_assembly == null || s_assembly.SnapshotId != batch.SnapshotId)
                {
                    // 上一次没收齐就被新快照顶掉：明确计数，不假装成功
                    if (s_assembly != null) s_droppedPartial++;
                    s_assembly = new Assembly
                    {
                        SnapshotId = batch.SnapshotId,
                        BatchCount = batch.BatchCount,
                        Header = batch.Header
                    };
                }
                if (s_assembly.Batches.ContainsKey(batch.BatchIndex)) return;   // 同一批重发：忽略
                s_assembly.Batches[batch.BatchIndex] = batch;
                if (s_assembly.Batches.Count < s_assembly.BatchCount) return;

                LiveMapSnapshot snapshot = s_assembly.Header;
                snapshot.Chunks.Clear();
                snapshot.ClientChunks.Clear();
                for (int i = 0; i < s_assembly.BatchCount; i++)
                {
                    if (!s_assembly.Batches.TryGetValue(i, out LiveMapBatch part))
                    {
                        // 前面的计数检查保证不该发生；真发生就**不收这份快照**（不发布残缺数据）。
                        s_droppedPartial++;
                        Log("实时地图快照 " + s_assembly.SnapshotId + " 缺少第 " + (i + 1) + "/" +
                            s_assembly.BatchCount + " 批 → 整份丢弃（不发布残缺快照）");
                        s_assembly = null;
                        return;
                    }
                    snapshot.Chunks.AddRange(part.Chunks);
                    snapshot.ClientChunks.AddRange(part.ClientChunks);
                }
                s_snapshot = snapshot;
                s_snapshotMs = Environment.TickCount64;
                s_samples++;
                s_assembly = null;
            }
        }

        private static void SendMapRequest()
        {
            TcpTerrainClient client = s_client;
            if (client == null) return;
            int centerChunkX = 0, centerChunkZ = 0;
            bool hasCenter = false;
            lock (s_gate)
            {
                // 选中角色按**稳定键**找（主机 = "host"）：玩家端重连（clientId 变了）后仍然找得到。
                if (s_selectedCenterKey != LiveMapCodec.HostKey && s_snapshot != null)
                {
                    for (int i = 0; i < s_snapshot.Centers.Count; i++)
                    {
                        LiveMapCenter center = s_snapshot.Centers[i];
                        if (!string.Equals(center.StableKey, s_selectedCenterKey, StringComparison.Ordinal)) continue;
                        hasCenter = true;
                        centerChunkX = center.ChunkX;
                        centerChunkZ = center.ChunkZ;
                        break;
                    }
                }
            }
            client.TrySend(MessageType.MapRequest,
                LiveMapCodec.EncodeRequest(s_radius, s_all, hasCenter, centerChunkX, centerChunkZ));
        }

        // ---------------------------------------------------------------- HTTP

        private static void HttpLoop(HttpListener http)
        {
            while (true)
            {
                HttpListenerContext context;
                try { context = http.GetContext(); }
                catch (Exception) { return; }

                string path = context.Request.Url.AbsolutePath;
                string body;
                string contentType = "application/json; charset=utf-8";
                int status = 200;
                if (path == "/state")
                {
                    body = BuildStateJson();
                }
                else if (path == "/center")
                {
                    // 「居中角色」按**身份稳定键**选（?key=<stableKey>）：重连后 clientId 变了、键不变。
                    // 旧写法 ?id=<clientId> 仍然接受（-1 = 主机），由当前快照换算成稳定键；
                    // 换算不出来就**明确失败**（400 + 原因），绝不静默退回主机。
                    string key = context.Request.QueryString["key"];
                    string id = context.Request.QueryString["id"];
                    if (!string.IsNullOrEmpty(key))
                    {
                        lock (s_gate) s_selectedCenterKey = key;
                        Log("地图中心切到稳定键 " + key);
                    }
                    else if (!string.IsNullOrEmpty(id))
                    {
                        if (!int.TryParse(id, out int parsed))
                        {
                            status = 400;
                            body = "{\"selected\":\"" + Escape(s_selectedCenterKey) +
                                "\",\"error\":\"?id=" + Escape(id) + " 不是整数\"}";
                            WriteResponse(context, status, contentType, body);
                            continue;
                        }
                        string resolved = ResolveStableKeyForClientId(parsed);
                        if (resolved == null)
                        {
                            status = 400;
                            body = "{\"selected\":\"" + Escape(s_selectedCenterKey) + "\",\"error\":\"clientId=" +
                                parsed + " 在当前快照里找不到（角色可能已离开）\"}";
                            WriteResponse(context, status, contentType, body);
                            continue;
                        }
                        lock (s_gate) s_selectedCenterKey = resolved;
                        Log("地图中心切到 clientId=" + parsed + " → 稳定键 " + resolved);
                    }
                    body = "{\"selected\":\"" + Escape(s_selectedCenterKey) + "\"}";
                }
                else if (path == "/")
                {
                    contentType = "text/html; charset=utf-8";
                    body = "<!DOCTYPE html><meta charset=\"utf-8\"><title>SuMP LiveMap</title>" +
                        "<body style=\"font-family:system-ui;padding:24px\">" +
                        "<h2>实时地图桥在运行</h2>" +
                        "<p>数据口：<code>/state</code>（JSON）</p>" +
                        "<p>请打开 <code>Mod/SuMultiplayer/doc/live-map.html</code>。</p></body>";
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                WriteResponse(context, status, contentType, body);
            }
        }

        /// <summary>统一的 HTTP 回包（CORS 放开：允许 file:// 页面读取）。</summary>
        private static void WriteResponse(HttpListenerContext context, int status, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";   // 允许 file:// 页面读取
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        /// <summary>
        /// 旧写法 <c>?id=&lt;clientId&gt;</c> → 稳定键：-1 = 主机；其余在当前快照的角色清单里按
        /// **当前传输句柄**找（找不到 = 明确失败，返回 null，绝不猜、也不静默退回主机）。
        /// </summary>
        private static string ResolveStableKeyForClientId(int clientId)
        {
            if (clientId == -1) return LiveMapCodec.HostKey;
            LiveMapSnapshot snapshot;
            lock (s_gate) snapshot = s_snapshot;
            if (snapshot == null) return null;
            for (int i = 0; i < snapshot.Centers.Count; i++)
            {
                LiveMapCenter center = snapshot.Centers[i];
                if (center.ClientId != clientId) continue;
                if (string.IsNullOrEmpty(center.StableKey)) return null;
                return center.StableKey;
            }
            return null;
        }

        private static string BuildStateJson()
        {
            LiveMapSnapshot snapshot;
            long ageMs;
            long samples;
            long dropped;
            long decodeFailures;
            string decodeReason;
            lock (s_gate)
            {
                snapshot = s_snapshot;
                ageMs = snapshot == null ? -1 : Environment.TickCount64 - s_snapshotMs;
                samples = s_samples;
                dropped = s_droppedPartial;
                decodeFailures = s_decodeFailures;
                decodeReason = s_decodeFailureReason;
            }

            var sb = new StringBuilder(512 * 1024);
            sb.Append("{\"t\":").Append(Environment.TickCount64);
            sb.Append(",\"online\":").Append(snapshot != null && ageMs >= 0 && ageMs < 5000 ? "true" : "false");
            sb.Append(",\"ageMs\":").Append(ageMs < 0 ? 0 : ageMs);
            sb.Append(",\"samples\":").Append(samples);
            sb.Append(",\"droppedPartial\":").Append(dropped);
            // 载荷版本（网页据此判断"这台工具是不是配套版本"）；解包失败也如实报出来。
            sb.Append(",\"payloadVersion\":").Append(LiveMapCodec.Version);
            sb.Append(",\"decodeFailures\":").Append(decodeFailures);
            sb.Append(",\"decodeFailureReason\":\"").Append(Escape(decodeReason)).Append('"');
            sb.Append(",\"view\":\"")
              .Append(snapshot != null && snapshot.View == LiveMapView.All ? "all" : "window").Append('"');
            sb.Append(",\"radius\":").Append(snapshot?.ChunkRadius ?? s_radius);
            sb.Append(",\"watched\":{\"id\":").Append(snapshot?.WatchedClientId ?? -1)
              .Append(",\"key\":\"").Append(Escape(snapshot?.WatchedStableKey ?? string.Empty))
              .Append("\",\"name\":\"").Append(Escape(snapshot?.WatchedClientName ?? string.Empty)).Append("\"}");

            // —— 旧形状的**身份**清单（centers = 主机 + 每个玩家端；带真实坐标）——
            // 只用于 `centers[]`（旧页面 / /center?id 换算）；v10 起新的 `players[]` 是**逐角色槽位**清单。
            // v4 的主机端 Mod 会把主机自己作为 ClientId=-1 的条目放进 Centers；这里只在
            // "快照头部带主机坐标、但清单里没有主机条目"时补一条（用的仍是主机端的真实坐标，
            // 不按区块中心猜）。
            var identityPlayers = new List<LiveMapCenter>();
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Centers.Count; i++) identityPlayers.Add(snapshot.Centers[i]);
                if (snapshot.HasViewerCell && !HasHostEntry(identityPlayers))
                {
                    identityPlayers.Insert(0, new LiveMapCenter
                    {
                        ClientId = -1,
                        StableKey = LiveMapCodec.HostKey,
                        Name = "Host",
                        IsHost = true,
                        ChunkX = snapshot.CellX >> 4,
                        ChunkZ = snapshot.CellZ >> 4,
                        HasCell = true,
                        CellX = snapshot.CellX,
                        CellY = snapshot.CellY,
                        CellZ = snapshot.CellZ
                    });
                }
            }

            // —— v10：**每个玩家角色槽位**一条（主机本机槽位 0..N-1 + 每个联机化身的 (clientId, slot)）——
            // key = "clientId:slot"（主机槽位 1 = "0:1"）；stableKey = 身份稳定键（同一身份的所有槽位共用，
            // 页面「居中角色」按它选，重连不变）；id = 当前 clientId（主机 = 0）。
            var players = snapshot != null ? snapshot.Players : new List<LiveMapPlayer>();

            // —— 中心（网页上选中的那个角色）—— 按**身份稳定键**选（重连后 clientId 变了也不丢选择）；
            // 同一个身份有多个槽位时选排在前面的那个（mod 侧顺序 = 槽位升序，即槽位 0）。
            LiveMapPlayer selected = null;
            for (int i = 0; i < players.Count; i++)
            {
                if (string.Equals(players[i].StableKey, s_selectedCenterKey, StringComparison.Ordinal))
                {
                    selected = players[i];
                    break;
                }
            }
            if (selected == null)
            {
                sb.Append(",\"center\":null");
            }
            else
            {
                sb.Append(",\"center\":{\"key\":\"").Append(Escape(selected.StableKey))
                  .Append("\",\"id\":").Append(selected.ClientId)
                  .Append(",\"slot\":").Append(selected.Slot)
                  .Append(",\"charKey\":\"").Append(Escape(selected.Key)).Append('"')
                  .Append(",\"cell\":").Append(CellJson(selected))
                  .Append(",\"cellReason\":\"").Append(Escape(selected.HasCell ? string.Empty : selected.CellReason))
                  .Append("\",\"chunk\":[").Append(selected.ChunkX).Append(',').Append(selected.ChunkZ)
                  .Append("]}");
            }

            // —— 角色清单（逐槽位）——
            // 每条：id / key（"0:1"）/ slot / name / isHost / host / stableKey / cell / cellReason / chunk。
            // 旧字段（id / name / isHost / cell / cellReason / chunk）形状不变，只是**不再每个身份只给一条**；
            // 新增 slot / key（角色键）/ stableKey / host（= isHost 的显式别名，需求口径 clientId==0）。
            sb.Append(",\"players\":[");
            for (int i = 0; i < players.Count; i++)
            {
                LiveMapPlayer player = players[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(player.ClientId)
                  .Append(",\"key\":\"").Append(Escape(player.Key)).Append('"')
                  .Append(",\"slot\":").Append(player.Slot)
                  .Append(",\"name\":\"").Append(Escape(player.Name)).Append('"')
                  .Append(",\"isHost\":").Append(player.IsHost ? "true" : "false")
                  .Append(",\"host\":").Append(player.IsHost ? "true" : "false")
                  .Append(",\"stableKey\":\"").Append(Escape(player.StableKey)).Append('"')
                  .Append(",\"cell\":").Append(CellJson(player))
                  .Append(",\"cellReason\":\"").Append(Escape(player.HasCell ? string.Empty : player.CellReason))
                  .Append("\",\"chunk\":[").Append(player.ChunkX).Append(',').Append(player.ChunkZ).Append("]}");
            }
            sb.Append(']');

            // 兼容旧页面的同一份数据（形状不变：id / name / isHost / chunk；v6 追加 key）——
            // 仍是**每个身份一条**（主机 id=-1），旧页面/旧脚本继续按它读。
            sb.Append(",\"centers\":[");
            for (int i = 0; i < identityPlayers.Count; i++)
            {
                LiveMapCenter player = identityPlayers[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(player.ClientId)
                  .Append(",\"key\":\"").Append(Escape(player.StableKey)).Append('"')
                  .Append(",\"name\":\"").Append(Escape(player.Name)).Append('"')
                  .Append(",\"isHost\":").Append(player.IsHost ? "true" : "false")
                  .Append(",\"chunk\":[").Append(player.ChunkX).Append(',').Append(player.ChunkZ).Append("]}");
            }
            sb.Append(']');

            // —— 每个**身份**当前存在于客户端内存的区块集合（v8）——
            // 数据源 = 客户端 ClientChunks 消息（整集 + 加载/卸载增量），不是会话累计的上报集合：
            // 客户端卸载的区块会从这里**消失**（"已经脱离内存的格子就取消绘制"）。
            // complete=false 表示工具没有把头部声明的当前条数全部合并起来 → 网页必须**不画**这块区域，
            // 并在图例里写明原因（绝不画一片错的区域）。
            // 合并键 = **身份稳定键**（不是 clientId）：同一身份重连前后是同一个键 ⇒ 不会分裂成两份。
            // 条目是 **[cx, cz, s]**（s = 该块相对**这个身份自己**的校验状态），
            // 网页据此给每个客户端区块画按状态着色的线框（绿 2 / 蓝 4 / 黄 1 / 红 3；
            // s=0 = 这块还在内存里但还没上报/裁决，网页不画框、也不猜状态）。
            var mergedByClient = new Dictionary<string, List<LiveMapClientChunk>>(StringComparer.Ordinal);
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.ClientChunks.Count; i++)
                {
                    LiveMapClientChunk entry = snapshot.ClientChunks[i];
                    string key = ClientKeyOf(entry.StableKey, entry.ClientId);
                    if (!mergedByClient.TryGetValue(key, out List<LiveMapClientChunk> list))
                    {
                        list = new List<LiveMapClientChunk>();
                        mergedByClient[key] = list;
                    }
                    list.Add(entry);
                }
            }
            bool clientsComplete = true;
            string clientsReason = string.Empty;
            int currentLoadedTotal = 0;
            sb.Append(",\"clients\":[");
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Clients.Count; i++)
                {
                    LiveMapClient client = snapshot.Clients[i];
                    if (i > 0) sb.Append(',');
                    string key = ClientKeyOf(client.StableKey, client.ClientId);
                    int merged = mergedByClient.TryGetValue(key, out List<LiveMapClientChunk> list)
                        ? list.Count : 0;
                    currentLoadedTotal += client.CurrentLoadedChunkCount;
                    bool ok = merged == client.CurrentLoadedChunkCount;
                    if (!ok)
                    {
                        clientsComplete = false;
                        if (clientsReason.Length == 0)
                        {
                            clientsReason = "身份 " + key + "（" + client.Name + " · 当前 clientId=" +
                                client.ClientId + "）的当前内存区块只合并到 " + merged + "/" +
                                client.CurrentLoadedChunkCount + " 条";
                        }
                    }
                    sb.Append("{\"key\":\"").Append(Escape(key)).Append('"')
                      .Append(",\"id\":").Append(client.ClientId)
                      .Append(",\"name\":\"").Append(Escape(client.Name)).Append('"')
                      // reported = 会话累计的上报条数（只增不减，仅作参考）
                      .Append(",\"reported\":").Append(client.ReportedChunkCount)
                      // v8：currentLoaded = 客户端**当前内存**的区块数（chunks 的形状与它一致）
                      .Append(",\"currentLoaded\":").Append(client.CurrentLoadedChunkCount)
                      .Append(",\"merged\":").Append(merged)
                      .Append(",\"complete\":").Append(ok ? "true" : "false")
                      .Append(",\"chunks\":[");
                    if (list != null)
                    {
                        for (int c = 0; c < list.Count; c++)
                        {
                            if (c > 0) sb.Append(',');
                            // v7：[cx, cz, s] —— s 原样透传（0 也照实输出，由网页明确报警，绝不猜一个状态）
                            sb.Append('[').Append(list[c].X).Append(',').Append(list[c].Z)
                              .Append(',').Append(list[c].State).Append(']');
                        }
                    }
                    sb.Append("]}");
                }
            }
            sb.Append(']');
            // 头部里没有、却在条目里出现的身份键（不该发生）：同样算不完整，如实报出来
            if (snapshot != null)
            {
                foreach (KeyValuePair<string, List<LiveMapClientChunk>> pair in mergedByClient)
                {
                    bool known = false;
                    for (int i = 0; i < snapshot.Clients.Count; i++)
                    {
                        if (!string.Equals(ClientKeyOf(snapshot.Clients[i].StableKey,
                                snapshot.Clients[i].ClientId), pair.Key, StringComparison.Ordinal)) continue;
                        known = true;
                        break;
                    }
                    if (known) continue;
                    clientsComplete = false;
                    if (clientsReason.Length == 0)
                    {
                        clientsReason = "条目里出现了头部清单没有的身份键 " + pair.Key + "（" +
                            pair.Value.Count + " 条）";
                    }
                }
            }

            // —— 真实已加载的 chunk（主机侧引擎已分配；未加载的**不在**这里）+ 覆盖范围 ——
            // 覆盖范围 = 主机区块 ∪ 各玩家端**当前内存**区块（v8）：两块区域离得远时画布要能同时装下它们。
            int minChunkX = int.MaxValue, maxChunkX = int.MinValue;
            int minChunkZ = int.MaxValue, maxChunkZ = int.MinValue;
            int columnCount = 0;
            // v5：chunks[].n = 该块还有几个在线客户端没拿到最新内容（主机侧内存计数）。0 = 网页不画数字。
            // counts.pendingClients = 本快照所有 chunks[].n 之和（页面图例与统计面板逐字引用）。
            int pendingMarks = 0, pendingClients = 0;
            sb.Append(",\"chunks\":[");
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Chunks.Count; i++)
                {
                    LiveMapChunk chunk = snapshot.Chunks[i];
                    if (i > 0) sb.Append(',');
                    columnCount += LiveMapChunk.Columns;
                    if (chunk.PendingClients > 0) { pendingMarks++; pendingClients += chunk.PendingClients; }
                    IncludeChunkBounds(chunk.X, chunk.Z, ref minChunkX, ref maxChunkX, ref minChunkZ, ref maxChunkZ);
                    sb.Append("{\"x\":").Append(chunk.X).Append(",\"z\":").Append(chunk.Z)
                      .Append(",\"s\":").Append(chunk.State)
                      .Append(",\"n\":").Append(chunk.PendingClients)
                      .Append(",\"d\":\"").Append(EncodeChunkData(chunk)).Append("\"}");
                }
            }
            sb.Append(']');
            int clientChunkEntries = 0;
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.ClientChunks.Count; i++)
                {
                    LiveMapClientChunk entry = snapshot.ClientChunks[i];
                    clientChunkEntries++;
                    IncludeChunkBounds(entry.X, entry.Z, ref minChunkX, ref maxChunkX, ref minChunkZ, ref maxChunkZ);
                }
            }

            // —— v9：主机端动物（仅位置，黑色圆点）——
            // 条目形状 = { x, y, z }：**没有名字 / id / 生命值**；只来自主机（客户端不贡献）。
            // 坐标不是有限数时写 null（页面会把它计为"坐标非法、不画"，绝不猜一个坐标）。
            sb.Append(",\"animals\":[");
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Animals.Count; i++)
                {
                    LiveMapAnimal animal = snapshot.Animals[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"x\":").Append(JsonNumber(animal.X))
                      .Append(",\"y\":").Append(JsonNumber(animal.Y))
                      .Append(",\"z\":").Append(JsonNumber(animal.Z)).Append('}');
                }
            }
            sb.Append(']');

            sb.Append(",\"boundsSource\":\"")
              .Append(minChunkX <= maxChunkX ? "hostChunks+clientChunks" : "none").Append('"');
            if (maxChunkX >= minChunkX)
            {
                sb.Append(",\"bounds\":{\"chunkMin\":[").Append(minChunkX).Append(',').Append(minChunkZ)
                  .Append("],\"chunkMax\":[").Append(maxChunkX).Append(',').Append(maxChunkZ)
                  .Append("],\"cellMin\":[").Append(minChunkX * 16).Append(',').Append(minChunkZ * 16)
                  .Append("],\"cellMax\":[").Append(maxChunkX * 16 + 15).Append(',').Append(maxChunkZ * 16 + 15)
                  .Append("]}");
            }
            else
            {
                sb.Append(",\"bounds\":null");
            }
            sb.Append(",\"counts\":{\"chunks\":").Append(snapshot?.Chunks.Count ?? 0)
              .Append(",\"columns\":").Append(columnCount)
              .Append(",\"players\":").Append(players.Count)
              // v10：players[] = **逐角色槽位**清单；hostPlayers / slotPlayers 是对照计数
              .Append(",\"hostPlayers\":").Append(HostPlayerCount(players))
              .Append(",\"clientChunks\":").Append(clientChunkEntries)
              // v9：主机端动物条数（页面只做对照，以 animals[] 条数为准）
              .Append(",\"animals\":").Append(snapshot?.Animals.Count ?? 0)
              // v8：各身份"当前内存区块数"之和（= 客户端线框条目应有的总数）
              .Append(",\"currentLoadedChunks\":").Append(currentLoadedTotal)
              .Append(",\"pendingMarks\":").Append(pendingMarks)
              .Append(",\"pendingClients\":").Append(pendingClients)
              .Append(",\"clientsComplete\":").Append(clientsComplete ? "true" : "false")
              .Append(",\"clientsIncompleteReason\":\"").Append(Escape(clientsReason)).Append('"')
              .Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 玩家端条目的合并 / 选角键：**身份稳定键**（v6）。空串（理论上不该出现）时退回
        /// <c>id:&lt;clientId&gt;</c> 并保持显式（不静默把两个身份并成一条）。
        /// </summary>
        private static string ClientKeyOf(string stableKey, int clientId) =>
            string.IsNullOrEmpty(stableKey) ? ("id:" + clientId) : stableKey;

        private static bool HasHostEntry(List<LiveMapCenter> players)
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].IsHost || players[i].ClientId == -1) return true;
            }
            return false;
        }

        /// <summary>v10：players[] 里主机自己的角色槽位数（clientId == 0；页面只做对照）。</summary>
        private static int HostPlayerCount(List<LiveMapPlayer> players)
        {
            int count = 0;
            for (int i = 0; i < players.Count; i++) if (players[i].IsHost) count++;
            return count;
        }

        /// <summary>角色真实坐标的 JSON；读不到就是 <c>null</c>（绝不按区块中心猜一个坐标）。</summary>
        private static string CellJson(LiveMapCenter player) =>
            player.HasCell
                ? "[" + player.CellX + "," + player.CellY + "," + player.CellZ + "]"
                : "null";

        /// <summary>v10 逐角色槽位条目的坐标 JSON（读不到 = null，不猜）。</summary>
        private static string CellJson(LiveMapPlayer player) =>
            player.HasCell
                ? "[" + player.CellX + "," + player.CellY + "," + player.CellZ + "]"
                : "null";

        /// <summary>
        /// 动物坐标的 JSON 数字：有限数按 invariant 输出；NaN / ±∞ 输出 <c>null</c>
        /// （页面把它计为"坐标非法、不画"，绝不猜坐标；也避免输出非法的 JSON 字面量 NaN）。
        /// </summary>
        private static string JsonNumber(float value) =>
            float.IsNaN(value) || float.IsInfinity(value)
                ? "null"
                : value.ToString("R", CultureInfo.InvariantCulture);

        private static void IncludeChunkBounds(int x, int z, ref int minChunkX, ref int maxChunkX,
            ref int minChunkZ, ref int maxChunkZ)
        {
            if (x < minChunkX) minChunkX = x;
            if (x > maxChunkX) maxChunkX = x;
            if (z < minChunkZ) minChunkZ = z;
            if (z > maxChunkZ) maxChunkZ = z;
        }

        /// <summary>768 字节：前 512 = 256 个 LE uint16 的 contents（索引 x + 16*z），后 256 = 每列地表 Y。</summary>
        private static string EncodeChunkData(LiveMapChunk chunk)
        {
            var bytes = new byte[LiveMapChunk.Columns * 3];
            for (int i = 0; i < LiveMapChunk.Columns; i++)
            {
                bytes[i * 2] = (byte)(chunk.Contents[i] & 0xFF);
                bytes[i * 2 + 1] = (byte)(chunk.Contents[i] >> 8);
            }
            int topOffset = LiveMapChunk.Columns * 2;
            for (int i = 0; i < LiveMapChunk.Columns; i++) bytes[topOffset + i] = chunk.Top[i];
            return Convert.ToBase64String(bytes);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }
    }
}

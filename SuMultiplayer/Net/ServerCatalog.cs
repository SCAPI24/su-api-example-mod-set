using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using SuMultiplayer.Engine;
using SuMultiplayer.Session;

namespace SuMultiplayer.Net
{
    public sealed class DirectoryEndpoint
    {
        public string Key;
        public string Host;
        /// <summary>该地址要探测的 UDP 端口列表（端口段 / 单端口 / 区间展开后）。</summary>
        public int[] ProbePorts = System.Array.Empty<int>();
        public string DisplayAddress;
        public string Name;
        public RoomSource Source;
        /// <summary>
        /// 地址里是否写明了端口。决定两件事：
        ///   ① 探测失败时是否仍在列表里保留离线行（个人服务器写明端口 → 保留，可 TCP 强连）；
        ///   ② TCP 兜底连接该用哪个端口（写明端口 → 用这个端口）。
        /// </summary>
        public bool HasExplicitPort;
        /// <summary>TCP 兜底端口：写明端口 = 该端口；否则 = 端口段起点。</summary>
        public int FallbackGamePort;
    }

    public sealed class DirectoryProbeResult
    {
        public DirectoryEndpoint Endpoint;
        public RoomBeaconInfo Info;
        public int PingMs = -1;
        public bool Online;
        public string TargetHost;
        /// <summary>本次探测的端口（同号方案下 = 该槽位的游戏端口）。</summary>
        public int Port;
    }

    /// <summary>
    /// ServerDns（三端 raw 合并 + 内嵌兜底）+ 个人服务器目录。
    /// 后台线程负责 HTTP 刷新和 UDP Beacon 单播探测；主线程通过 TryDequeue 取结果，
    /// 不把网络等待放到游戏帧里。
    /// </summary>
    public sealed class ServerCatalog : IDisposable
    {
        private const string ContentResourceName = "SuMultiplayer.ServerDns.txt";
        private const int MaxDirectoryBytes = 64 * 1024;
        private const int MaxHosts = 256;
        private const int ProbeIntervalMs = 5000;
        private const int RefreshSuccessMs = 300000;
        private const int RefreshFailureMs = 60000;

        private static readonly string[] RawUrls =
        {
            "https://gitee.com/SC-SPM/su-api-example-mod-set/raw/master/ScMultiplayer/ServerDns.txt",
            "https://cnb.cool/suceru.cb/SC-SPM/SuAPIMod/-/git/raw/master/ScMultiplayer/ServerDns.txt",
            "https://raw.githubusercontent.com/SCAPI24/su-api-example-mod-set/master/ScMultiplayer/ServerDns.txt"
        };

        private readonly int m_probePortStart;
        private readonly int m_probePortCount;
        private readonly object m_gate = new object();
        private readonly List<DirectoryEndpoint> m_endpoints = new List<DirectoryEndpoint>();
        private readonly ConcurrentQueue<DirectoryProbeResult> m_results =
            new ConcurrentQueue<DirectoryProbeResult>();
        private readonly Dictionary<string, long> m_nextProbe = new Dictionary<string, long>();
        private Thread m_thread;
        private volatile bool m_running;
        private int m_personalRevision = -1;
        private long m_nextRefreshMs;
        private int m_refreshFailures;

        /// <param name="probePortStart">地址没写端口时，默认从哪个端口开始扫。</param>
        /// <param name="probePortCount">地址没写端口时，默认扫多少个端口。</param>
        public ServerCatalog(int probePortStart, int probePortCount)
        {
            m_probePortStart = probePortStart;
            m_probePortCount = Math.Max(1, Math.Min(256, probePortCount));
        }

        /// <summary>地址没写明端口时使用的默认探测端口段（起点 + 数量）。</summary>
        private int[] DefaultProbePorts()
        {
            var ports = new int[m_probePortCount];
            for (int i = 0; i < ports.Length; i++) ports[i] = m_probePortStart + i;
            return ports;
        }

        public void Start()
        {
            if (m_running) return;
            m_running = true;
            m_thread = new Thread(Loop) { IsBackground = true, Name = "SuMP-Catalog" };
            m_thread.Start();
        }

        public bool TryDequeue(out DirectoryProbeResult result) =>
            m_results.TryDequeue(out result);

        public void Dispose()
        {
            m_running = false;
            m_thread = null;
        }

        private void Loop()
        {
            LoadEmbeddedAndPersonal();
            while (m_running)
            {
                try
                {
                    if (Environment.TickCount64 >= m_nextRefreshMs) RefreshRaw();
                    RefreshPersonal();
                    long now = Environment.TickCount64;
                    List<DirectoryEndpoint> snapshot;
                    lock (m_gate) snapshot = new List<DirectoryEndpoint>(m_endpoints);
                    foreach (DirectoryEndpoint endpoint in snapshot)
                    {
                        if (!m_running) break;
                        lock (m_gate)
                        {
                            if (m_nextProbe.TryGetValue(endpoint.Key, out long due) && due > now)
                                continue;
                            m_nextProbe[endpoint.Key] = now + ProbeIntervalMs;
                        }
                        Probe(endpoint);
                    }
                }
                catch (Exception error)
                {
                    ModLog.Warn("[SuMP][catalog] Refresh threw: " + error.Message);
                }
                for (int i = 0; i < 10 && m_running; i++) Thread.Sleep(100);
            }
        }

        private void LoadEmbeddedAndPersonal()
        {
            var hosts = new List<string>();
            try
            {
                Assembly assembly = typeof(ServerCatalog).Assembly;
                using Stream stream = assembly.GetManifestResourceStream(ContentResourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    hosts.AddRange(ParseHosts(reader.ReadToEnd()));
                }
            }
            catch (Exception error)
            {
                ModLog.Warn("[SuMP][catalog] Failed to read embedded ServerDns: " + error.Message);
            }
            ApplyDirectoryHosts(hosts, "embedded");
            RefreshPersonal();
        }

        private void RefreshRaw()
        {
            m_nextRefreshMs = Environment.TickCount64 +
                (m_refreshFailures > 0 ? RefreshFailureMs : RefreshSuccessMs);
            var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int succeeded = 0;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            foreach (string url in RawUrls)
            {
                try
                {
                    byte[] data = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                    if (data == null || data.Length == 0 || data.Length > MaxDirectoryBytes) continue;
                    string text = Encoding.UTF8.GetString(data);
                    foreach (string host in ParseHosts(text)) merged.Add(host);
                    succeeded++;
                }
                catch (Exception error)
                {
                    ModLog.Info("[catalog] ServerDns source unavailable: " + url +
                        " · " + error.Message);
                }
            }
            m_refreshFailures = succeeded == 0 ? m_refreshFailures + 1 : 0;
            if (merged.Count > 0)
                ApplyDirectoryHosts(merged.ToList(), "raw×" + succeeded);
        }

        private void RefreshPersonal()
        {
            PersonalServers.Load();
            int revision = PersonalServers.Revision;
            if (revision == m_personalRevision) return;
            m_personalRevision = revision;
            foreach (PersonalServerRecord record in PersonalServers.Records)
            {
                if (!TrySplitAddress(record.Address, out string host, out int port, out _))
                    continue;
                // From Link（个人服务器）只支持 host / host:P，**不支持** host:P-N。
                // 没写端口 → 按默认端口段扫描（UDP 在线才显示）；写了端口 → 只扫该端口。
                int[] probePorts = port > 0 ? new[] { port } : DefaultProbePorts();
                var endpoint = new DirectoryEndpoint
                {
                    Key = "personal:" + record.Address,
                    Host = host,
                    ProbePorts = probePorts,
                    DisplayAddress = record.Address,
                    Name = record.Name,
                    Source = RoomSource.Personal,
                    HasExplicitPort = port > 0,
                    // 写了端口 → TCP 兜底就连这个端口；没写 → 连端口段起点。
                    FallbackGamePort = port > 0 ? port : m_probePortStart
                };
                lock (m_gate) RemoveLocked(endpoint.Key);
                lock (m_gate) m_endpoints.Add(endpoint);
                // 先给主线程一条离线行：即使探测失败，个人服务器也能在 Play 屏看到/删除。
                m_results.Enqueue(new DirectoryProbeResult
                {
                    Endpoint = endpoint,
                    Online = false,
                    TargetHost = host
                });
            }
            // 删除后的旧 personal 条目从列表移除
            var stale = new List<DirectoryEndpoint>();
            lock (m_gate)
            {
                foreach (DirectoryEndpoint endpoint in m_endpoints)
                {
                    if (endpoint.Source == RoomSource.Personal &&
                        !PersonalServers.Records.Any(record => string.Equals(
                            "personal:" + record.Address, endpoint.Key, StringComparison.Ordinal)))
                        stale.Add(endpoint);
                }
                foreach (DirectoryEndpoint endpoint in stale) RemoveLocked(endpoint.Key);
            }
        }

        private void ApplyDirectoryHosts(List<string> hosts, string source)
        {
            var valid = new List<DirectoryEndpoint>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in hosts)
            {
                if (valid.Count >= MaxHosts) break;
                if (!TrySplitAddress(raw, out string host, out int port, out int count)) continue;
                if (!seen.Add("dir:" + host)) continue;
                // ServerDns 支持三种写法：host（默认端口段）/ host:P / host:P-N（P 起 N 个）。
                int[] probePorts = count > 0
                    ? BuildPortRange(port, count)
                    : port > 0 ? new[] { port } : DefaultProbePorts();
                valid.Add(new DirectoryEndpoint
                {
                    Key = "dir:" + host + ":" + (count > 0 ? port + "-" + count : port.ToString()),
                    Host = host,
                    ProbePorts = probePorts,
                    DisplayAddress = host,
                    Name = string.Empty,
                    Source = RoomSource.Directory,
                    HasExplicitPort = port > 0,
                    FallbackGamePort = port > 0 ? port : m_probePortStart
                });
            }
            lock (m_gate)
            {
                m_endpoints.RemoveAll(endpoint => endpoint.Source == RoomSource.Directory);
                m_endpoints.AddRange(valid);
            }
            ModLog.Info("[catalog] ServerDns merged (" + source +
                "）：" + valid.Count + " 个服务地址");
        }

        private void Probe(DirectoryEndpoint endpoint)
        {
            string targetHost = endpoint.Host;
            int[] probePorts = endpoint.ProbePorts ?? System.Array.Empty<int>();
            try
            {
                // 一个地址可能对应多个端口（端口段 / 区间）：**逐个探测，每个端口一条结果**。
                // 不因为某个端口失败就跳过其余端口 —— 同一台机器可能在不同槽位开多个房间。
                for (int i = 0; i < probePorts.Length; i++)
                {
                    int discoveryPort = probePorts[i];
                    long nonce = Environment.TickCount64;
                    byte[] probe = RoomBeaconCodec.EncodeProbe(nonce);
                    RoomBeaconInfo info = null;
                    int ping = -1;
                    bool proxy = ProxyRouter.TryChooseUdp(targetHost, discoveryPort,
                        out bool useProxy, out _) && useProxy;
                    if (proxy)
                    {
                        if (Socks5Proxy.TryUdpProbe(targetHost, discoveryPort, probe, 1500,
                                out byte[] reply, out string proxyError) &&
                            RoomBeaconCodec.TryDecodeReply(reply, reply.Length, out info, out _))
                            ping = (int)(Environment.TickCount64 - nonce);
                        else if (!string.IsNullOrEmpty(proxyError))
                            ModLog.Info("[catalog] Proxy UDP probe failed: " +
                                targetHost + ":" + discoveryPort + " · " + proxyError);
                    }
                    else if (!TryDirectProbe(targetHost, discoveryPort, probe, nonce, out info,
                            out ping, out string directError))
                    {
                        // 离线是常态（端口段里只有少数槽位有房间），只记非空错误。
                        if (!string.IsNullOrEmpty(directError))
                            ModLog.Info("[catalog] Probe failed: " + targetHost + ":" +
                                discoveryPort + " · " + directError);
                    }
                    m_results.Enqueue(new DirectoryProbeResult
                    {
                        Endpoint = endpoint,
                        Info = info,
                        PingMs = ping,
                        Online = info != null,
                        TargetHost = targetHost,
                        // 探测端口 = 该槽位的游戏端口（同号方案）→ 房间条目直接用它。
                        Port = discoveryPort
                    });
                }
            }
            catch (Exception error)
            {
                ModLog.Info("[catalog] Probe threw: " + targetHost +
                    " · " + error.Message);
                m_results.Enqueue(new DirectoryProbeResult
                {
                    Endpoint = endpoint,
                    Online = false,
                    TargetHost = targetHost
                });
            }
        }

        private static bool TryDirectProbe(string host, int port, byte[] probe, long nonce,
            out RoomBeaconInfo info, out int ping, out string error)
        {
            info = null;
            ping = -1;
            error = null;
            try
            {
                IPAddress[] addresses = DohResolver.ResolveHost(host);
                if (addresses == null || addresses.Length == 0) return false;
                using var udp = new UdpClient(addresses[0].AddressFamily);
                udp.Client.ReceiveTimeout = 1200;
                var target = new IPEndPoint(addresses[0], port);
                udp.Send(probe, probe.Length, target);
                var remote = new IPEndPoint(
                    addresses[0].AddressFamily == AddressFamily.InterNetwork
                        ? IPAddress.Any : IPAddress.IPv6Any, 0);
                byte[] reply = udp.Receive(ref remote);
                if (!RoomBeaconCodec.TryDecodeReply(reply, reply.Length, out info, out error))
                    return false;
                ping = (int)(Environment.TickCount64 - nonce);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void RemoveLocked(string key) =>
            m_endpoints.RemoveAll(endpoint => string.Equals(endpoint.Key, key,
                StringComparison.Ordinal));

        public static IEnumerable<string> ParseHosts(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (string sourceLine in text.Split(new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.None))
            {
                string line = sourceLine.Trim().TrimStart('\ufeff');
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") ||
                    line.StartsWith("//")) continue;
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                foreach (string raw in line.Split(new[] { ' ', '\t', ',', ';', '|' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    string token = raw.Trim('"', '\'', '(', ')', '<', '>').TrimEnd('.');
                    if (token.Length > 0 && token.Length <= 300) yield return token;
                }
            }
        }

        /// <summary>
        /// 解析地址写法（ServerDns 行 / 个人服务器记录）：
        ///   <c>host</c>           → port=0, count=0（用默认端口段）
        ///   <c>host:P</c>         → port=P, count=0（只该端口）
        ///   <c>host:P-N</c>       → port=P, count=N（P 起连续 N 个端口；N=数量，不是结束端口）
        /// IPv6 用 <c>[v6]</c> / <c>[v6]:P</c> / <c>[v6]:P-N</c>。
        /// </summary>
        private static bool TrySplitAddress(string text, out string host, out int port,
            out int count)
        {
            host = string.Empty;
            port = 0;
            count = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closing = value.IndexOf(']');
                if (closing <= 1) return false;
                host = value.Substring(1, closing - 1);
                if (closing + 1 < value.Length)
                {
                    if (value[closing + 1] != ':') return false;
                    if (!TryParsePortSpec(value.Substring(closing + 2), out port, out count))
                        return false;
                }
                return true;
            }
            int first = value.IndexOf(':');
            int last = value.LastIndexOf(':');
            if (first > 0 && first == last)
            {
                if (!TryParsePortSpec(value.Substring(last + 1), out port, out count))
                    return false;
                host = value.Substring(0, last);
                return host.Length > 0;
            }
            if (first >= 0) return false;   // 多个冒号且不是 [v6] 形式：非法
            host = value;
            return Uri.CheckHostName(host) != UriHostNameType.Unknown ||
                IPAddress.TryParse(host, out _);
        }

        /// <summary>解析 <c>P</c> 或 <c>P-N</c>（N = 数量，1..256）。</summary>
        private static bool TryParsePortSpec(string spec, out int port, out int count)
        {
            port = 0;
            count = 0;
            if (string.IsNullOrEmpty(spec)) return false;
            int dash = spec.IndexOf('-');
            if (dash < 0)
                return int.TryParse(spec, out port) && port > 0 && port <= 65535;
            if (!int.TryParse(spec.Substring(0, dash), out port) || port <= 0 || port > 65535)
                return false;
            if (!int.TryParse(spec.Substring(dash + 1), out count) || count <= 0) return false;
            count = Math.Min(count, 256);
            if (port + count - 1 > 65535) count = 65535 - port + 1;
            return count > 0;
        }

        /// <summary>把「起点 + 数量」展开成端口数组。</summary>
        private static int[] BuildPortRange(int start, int count)
        {
            var ports = new int[count];
            for (int i = 0; i < count; i++) ports[i] = start + i;
            return ports;
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        public bool Removed;
        public string TargetHost;
        /// <summary>本次探测的端口（同号方案下 = 该槽位的游戏端口）。</summary>
        public int Port;
    }

    /// <summary>
    /// R2 公开服务器名单与本地个人服务器目录。
    /// 后台线程负责 HTTP 刷新和 UDP Beacon 单播探测；主线程通过 TryDequeue 取结果，
    /// 不把网络等待放到游戏帧里。
    /// </summary>
    public sealed class ServerCatalog : IDisposable
    {
        private const string DirectoryUrl = "https://store.suceru.site/Host/ServerDns.txt";
        private const int MaxDirectoryBytes = 64 * 1024;
        private const int MaxHosts = 256;
        private const int ProbeIntervalMs = 5000;
        private const int MaxConcurrentProbes = 12;
        private const int FailuresBeforeOffline = 3;
        private const int RefreshSuccessMs = 300000;
        private const int RefreshFailureMs = 60000;

        private static readonly HttpClient DirectoryClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxDirectoryBytes
        };
        private static readonly Encoding DirectoryEncoding = new UTF8Encoding(false, true);

        private readonly int m_probePortStart;
        private readonly int m_probePortCount;
        private readonly Dictionary<(string Endpoint, int Port), PortProbe> m_probes =
            new Dictionary<(string Endpoint, int Port), PortProbe>();
        private readonly List<PortProbe> m_inFlight = new List<PortProbe>();
        private readonly ConcurrentQueue<DirectoryProbeResult> m_results =
            new ConcurrentQueue<DirectoryProbeResult>();
        private Task<string> m_directoryRefresh;

        private sealed class PortProbe
        {
            public DirectoryEndpoint Endpoint;
            public int Port;
            public long NextProbeMs;
            public int ConsecutiveFailures;
            public bool Online;
            public Task<DirectoryProbeResult> InFlight;
        }

        private Thread m_thread;
        private volatile bool m_running;
        private volatile bool m_disposed;
        private int m_personalRevision = -1;
        private long m_nextRefreshMs;

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
            if (m_disposed) throw new ObjectDisposedException(nameof(ServerCatalog));
            if (m_running) return;
            m_running = true;
            m_thread = new Thread(Loop) { IsBackground = true, Name = "SuMP-Catalog" };
            m_thread.Start();
        }

        public bool TryDequeue(out DirectoryProbeResult result)
        {
            result = null;
            return !m_disposed && m_results.TryDequeue(out result);
        }

        public void Dispose()
        {
            m_disposed = true;
            m_running = false;
            m_thread = null;
        }

        private void Loop()
        {
            while (m_running)
            {
                try
                {
                    RefreshPersonal();
                    PumpDirectoryRefresh(Environment.TickCount64);
                    PumpProbes(Environment.TickCount64);
                }
                catch (Exception error)
                {
                    ModLog.Warn("[SuMP][catalog] Refresh threw: " + error.Message);
                }
                Thread.Sleep(100);
            }
        }

        // Source: ServerCatalog.cs:RefreshRaw
        // Directory HTTP refresh must not hold up probes for entries already in the list.
        private void PumpDirectoryRefresh(long now)
        {
            if (!m_running) return;
            if (m_directoryRefresh != null)
            {
                if (!m_directoryRefresh.IsCompleted) return;
                try
                {
                    ApplyDirectoryHosts(ParseHosts(m_directoryRefresh.GetAwaiter().GetResult()));
                    m_nextRefreshMs = now + RefreshSuccessMs;
                }
                catch (Exception error)
                {
                    m_nextRefreshMs = now + RefreshFailureMs;
                    ModLog.Warn("[SuMP][catalog] Could not refresh R2 ServerDns: " + error.Message);
                }
                finally { m_directoryRefresh = null; }
            }
            if (m_running && now >= m_nextRefreshMs) m_directoryRefresh = DownloadDirectory();
        }

        private static async Task<string> DownloadDirectory()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DirectoryUrl);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using HttpResponseMessage response = await DirectoryClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return DirectoryEncoding.GetString(data);
        }

        private void RefreshPersonal()
        {
            PersonalServers.Load();
            int revision = PersonalServers.Revision;
            if (revision == m_personalRevision) return;
            var endpoints = new List<DirectoryEndpoint>();
            foreach (PersonalServerRecord record in PersonalServers.Records)
            {
                if (!TrySplitAddress(record.Address, out string host, out int port, out _))
                    continue;
                // From Link（个人服务器）只支持 host / host:P，**不支持** host:P-N。
                // 没写端口 → 按默认端口段扫描（UDP 在线才显示）；写了端口 → 只扫该端口。
                int[] probePorts = port > 0 ? new[] { port } : DefaultProbePorts();
                endpoints.Add(new DirectoryEndpoint
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
                });
            }
            ReplaceEndpoints(RoomSource.Personal, endpoints);
            m_personalRevision = revision;
        }

        private void ApplyDirectoryHosts(IEnumerable<string> hosts)
        {
            var valid = new List<DirectoryEndpoint>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in hosts)
            {
                if (!TrySplitAddress(raw, out string host, out int port, out int count) ||
                    Uri.CheckHostName(host) == UriHostNameType.Unknown)
                    throw new InvalidDataException("ServerDns contains an invalid server address.");
                string key = "dir:" + host.ToLowerInvariant();
                string addressSpec = key + ":" + port + ":" + count;
                if (!seen.Add(addressSpec)) continue;
                if (valid.Count >= MaxHosts)
                    throw new InvalidDataException("ServerDns exceeds " + MaxHosts + " hosts.");
                // ServerDns 支持三种写法：host（默认端口段）/ host:P / host:P-N（P 起 N 个）。
                int[] probePorts = count > 0
                    ? BuildPortRange(port, count)
                    : port > 0 ? new[] { port } : DefaultProbePorts();
                valid.Add(new DirectoryEndpoint
                {
                    Key = key,
                    Host = host,
                    ProbePorts = probePorts,
                    DisplayAddress = host,
                    Name = string.Empty,
                    Source = RoomSource.Directory,
                    HasExplicitPort = port > 0,
                    FallbackGamePort = port > 0 ? port : m_probePortStart
                });
            }
            ReplaceEndpoints(RoomSource.Directory, valid);
            ModLog.Info("[SuMP][catalog] Loaded " + valid.Count + " endpoints from R2 ServerDns.");
        }

        // Only the catalog loop owns the slot table. A refresh preserves live slots,
        // and an old request cannot bring back a slot removed while it was in flight.
        private void ReplaceEndpoints(RoomSource source, List<DirectoryEndpoint> endpoints)
        {
            var desired = new HashSet<(string Endpoint, int Port)>();
            foreach (DirectoryEndpoint endpoint in endpoints)
            {
                foreach (int port in endpoint.ProbePorts)
                {
                    var key = (endpoint.Key, port);
                    if (!desired.Add(key)) continue;
                    if (m_probes.TryGetValue(key, out PortProbe probe))
                    {
                        bool renamed = probe.Endpoint.Name != endpoint.Name;
                        probe.Endpoint = endpoint;
                        if (renamed && !probe.Online && KeepOffline(endpoint))
                            Publish(probe, removed: false);
                        continue;
                    }
                    probe = new PortProbe { Endpoint = endpoint, Port = port };
                    m_probes.Add(key, probe);
                    if (KeepOffline(endpoint)) Publish(probe, removed: false);
                }
            }
            var removed = m_probes.Where(pair => pair.Value.Endpoint.Source == source &&
                !desired.Contains(pair.Key)).Select(pair => pair.Key).ToArray();
            foreach (var key in removed)
            {
                PortProbe probe = m_probes[key];
                m_probes.Remove(key);
                if (probe.Online || KeepOffline(probe.Endpoint)) Publish(probe, removed: true);
            }
        }

        // Each port has its own deadline and retry count. A stalled port only occupies
        // one worker, and a room stays listed until its own consecutive probes fail.
        private void PumpProbes(long now)
        {
            for (int i = m_inFlight.Count - 1; i >= 0; i--)
            {
                PortProbe probe = m_inFlight[i];
                if (!probe.InFlight.IsCompleted) continue;
                DirectoryProbeResult result = probe.InFlight.GetAwaiter().GetResult();
                probe.InFlight = null;
                probe.NextProbeMs = now + ProbeIntervalMs;
                m_inFlight.RemoveAt(i);
                if (!m_probes.TryGetValue((probe.Endpoint.Key, probe.Port), out PortProbe current) ||
                    !ReferenceEquals(current, probe)) continue;

                if (result.Online)
                {
                    probe.ConsecutiveFailures = 0;
                    probe.Online = true;
                    result.Endpoint = probe.Endpoint;
                    m_results.Enqueue(result);
                }
                else if (probe.ConsecutiveFailures < FailuresBeforeOffline &&
                    ++probe.ConsecutiveFailures == FailuresBeforeOffline && probe.Online)
                {
                    probe.Online = false;
                    Publish(probe, removed: !KeepOffline(probe.Endpoint));
                }
            }

            int capacity = MaxConcurrentProbes - m_inFlight.Count;
            if (capacity == 0) return;
            foreach (PortProbe probe in m_probes.Values
                .Where(probe => probe.InFlight == null && probe.NextProbeMs <= now)
                .OrderBy(probe => probe.NextProbeMs).Take(capacity))
            {
                if (!m_running) break;
                DirectoryEndpoint endpoint = probe.Endpoint;
                int port = probe.Port;
                probe.InFlight = Task.Run(() => Probe(endpoint, port));
                m_inFlight.Add(probe);
            }
        }

        private static bool KeepOffline(DirectoryEndpoint endpoint) =>
            endpoint.Source == RoomSource.Personal && endpoint.HasExplicitPort;

        private void Publish(PortProbe probe, bool removed) => m_results.Enqueue(new DirectoryProbeResult
        {
            Endpoint = probe.Endpoint,
            TargetHost = probe.Endpoint.Host,
            Port = probe.Port,
            Removed = removed
        });

        private static DirectoryProbeResult Probe(DirectoryEndpoint endpoint, int port)
        {
            string targetHost = endpoint.Host;
            var result = new DirectoryProbeResult { Endpoint = endpoint, TargetHost = targetHost, Port = port };
            try
            {
                bool proxy = ProxyRouter.TryChooseUdp(targetHost, port, out bool useProxy, out _) && useProxy;
                long nonce = Environment.TickCount64;
                byte[] request = RoomBeaconCodec.EncodeProbe(nonce);
                if (proxy)
                {
                    if (Socks5Proxy.TryUdpProbe(targetHost, port, request, 1500,
                            out byte[] reply, out string proxyError) &&
                        RoomBeaconCodec.TryDecodeReply(reply, reply.Length, out result.Info, out _))
                        result.PingMs = (int)(Environment.TickCount64 - nonce);
                    else if (!string.IsNullOrEmpty(proxyError))
                        ModLog.Info("[catalog] Proxy UDP probe failed: " + targetHost + ":" + port + " · " + proxyError);
                }
                else if (!TryDirectProbe(targetHost, port, request, nonce, out result.Info,
                    out result.PingMs, out string directError) && !string.IsNullOrEmpty(directError))
                    ModLog.Info("[catalog] Probe failed: " + targetHost + ":" + port + " · " + directError);
            }
            catch (Exception error)
            {
                ModLog.Info("[catalog] Probe threw: " + targetHost + ":" + port + " · " + error.Message);
            }
            result.Online = result.Info != null;
            return result;
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
                if (addresses.Length == 0)
                {
                    error = "DNS did not return a usable address for " + host;
                    return false;
                }
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

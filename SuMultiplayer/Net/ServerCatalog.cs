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
        public int DiscoveryPort;
        public string DisplayAddress;
        public string Name;
        public RoomSource Source;
        public int FallbackGamePort;
    }

    public sealed class DirectoryProbeResult
    {
        public DirectoryEndpoint Endpoint;
        public RoomBeaconInfo Info;
        public int PingMs = -1;
        public bool Online;
        public string TargetHost;
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

        private readonly int m_defaultDiscoveryPort;
        private readonly int m_defaultGamePort;
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

        public ServerCatalog(int defaultDiscoveryPort, int defaultGamePort)
        {
            m_defaultDiscoveryPort = defaultDiscoveryPort;
            m_defaultGamePort = defaultGamePort;
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
                    global::Engine.Log.Warning("[SuMP][catalog] 刷新异常：" + error.Message);
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
                global::Engine.Log.Warning("[SuMP][catalog] 内嵌 ServerDns 读取失败：" + error.Message);
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
                    ModLog.Info("[catalog] ServerDns 源不可用：" + url +
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
                if (!TrySplitAddress(record.Address, out string host, out int port))
                    continue;
                var endpoint = new DirectoryEndpoint
                {
                    Key = "personal:" + record.Address,
                    Host = host,
                    DiscoveryPort = port > 0 ? port : m_defaultDiscoveryPort,
                    DisplayAddress = record.Address,
                    Name = record.Name,
                    Source = RoomSource.Personal,
                    FallbackGamePort = m_defaultGamePort
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
                if (!TrySplitAddress(raw, out string host, out int port)) continue;
                string key = "dir:" + raw;
                if (!seen.Add(key)) continue;
                valid.Add(new DirectoryEndpoint
                {
                    Key = key,
                    Host = host,
                    DiscoveryPort = port > 0 ? port : m_defaultDiscoveryPort,
                    DisplayAddress = host,
                    Name = string.Empty,
                    Source = RoomSource.Directory,
                    FallbackGamePort = m_defaultGamePort
                });
            }
            lock (m_gate)
            {
                m_endpoints.RemoveAll(endpoint => endpoint.Source == RoomSource.Directory);
                m_endpoints.AddRange(valid);
            }
            ModLog.Info("[catalog] ServerDns 合并完成（" + source +
                "）：" + valid.Count + " 个服务地址");
        }

        private void Probe(DirectoryEndpoint endpoint)
        {
            string targetHost = endpoint.Host;
            int discoveryPort = endpoint.DiscoveryPort;
            try
            {
                bool proxy = ProxyRouter.TryChooseUdp(targetHost, discoveryPort, out bool useProxy,
                    out _) && useProxy;
                long nonce = Environment.TickCount64;
                byte[] probe = RoomBeaconCodec.EncodeProbe(nonce);
                RoomBeaconInfo info = null;
                int ping = -1;
                if (proxy)
                {
                    if (Socks5Proxy.TryUdpProbe(targetHost, discoveryPort, probe, 1500,
                            out byte[] reply, out string proxyError) &&
                        RoomBeaconCodec.TryDecodeReply(reply, reply.Length, out info, out _))
                        ping = (int)(Environment.TickCount64 - nonce);
                    else if (!string.IsNullOrEmpty(proxyError))
                        ModLog.Info("[catalog] 代理 UDP 探测失败：" +
                            targetHost + " · " + proxyError);
                }
                else if (TryDirectProbe(targetHost, discoveryPort, probe, nonce, out info,
                        out ping, out string directError) == false &&
                    !string.IsNullOrEmpty(directError))
                {
                    ModLog.Info("[catalog] 探测失败：" + targetHost +
                        " · " + directError);
                }
                m_results.Enqueue(new DirectoryProbeResult
                {
                    Endpoint = endpoint,
                    Info = info,
                    PingMs = ping,
                    Online = info != null,
                    TargetHost = targetHost
                });
            }
            catch (Exception error)
            {
                ModLog.Info("[catalog] 探测异常：" + targetHost +
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

        private static bool TrySplitAddress(string text, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closing = value.IndexOf(']');
                if (closing <= 1) return false;
                host = value.Substring(1, closing - 1);
                if (closing + 1 < value.Length &&
                    !int.TryParse(value.Substring(closing + 2), out port))
                    return false;
                return true;
            }
            int first = value.IndexOf(':');
            int last = value.LastIndexOf(':');
            if (first > 0 && first == last && int.TryParse(value.Substring(last + 1), out port))
            {
                host = value.Substring(0, last);
                return host.Length > 0;
            }
            host = value;
            return Uri.CheckHostName(host) != UriHostNameType.Unknown ||
                IPAddress.TryParse(host, out _);
        }
    }
}

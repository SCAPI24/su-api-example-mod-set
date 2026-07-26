using Comms.Drt;
using Engine;
using Engine.Content;
using Game;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScMultiplayer
{
    internal sealed class RemoteServerDirectory
    {
        private const string DirectoryFileName = "ServerDns.txt";
        private const string ContentResourceName = "Mod/ServerDns";
        private const string EmbeddedResourceName = "ScMultiplayer.ServerDns.txt";
        private const double SuccessfulRefreshPeriod = 300.0;
        private const double FailedRefreshPeriod = 30.0;
        private const int RawRequestTimeoutMilliseconds = 8000;
        private const int MaximumDirectoryBytes = 64 * 1024;
        private const int MaximumHosts = 256;

        // Source: Game/WebManager.cs:WebManager.Get
        // Gitee is the primary directory. GitHub is read afterwards and merged as a mirror.
        internal static readonly string[] RawDirectoryUrls =
        {
            "https://gitee.com/SC-SPM/su-api-example-mod-set/raw/master/ScMultiplayer/ServerDns.txt",
            "https://raw.githubusercontent.com/SCAPI24/su-api-example-mod-set/master/ScMultiplayer/ServerDns.txt"
        };

        private readonly Explorer m_explorer;
        private readonly HashSet<string> m_localHosts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> m_remoteHosts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> m_personalHosts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object m_resolvedHostsLock = new object();
        private Dictionary<IPAddress, string> m_resolvedHosts =
            new Dictionary<IPAddress, string>();
        private IPEndPoint[] m_resolvedExplicitEndpoints = Array.Empty<IPEndPoint>();
        private ResolvedPersonalRoute[] m_resolvedPersonalRoutes =
            Array.Empty<ResolvedPersonalRoute>();
        private string[] m_activeHosts = Array.Empty<string>();
        private HashSet<string> m_pendingRemoteHosts;
        private bool m_contentRootChecked;
        private bool m_rawRefreshInProgress;
        private bool m_rawRefreshSucceeded;
        private int m_rawSourceIndex;
        private int m_rawRequestId;
        private int m_resolveGeneration;
        private int m_discoveryGeneration;
        private bool m_discoveryEnabled;
        private CancellableProgress m_rawRefreshProgress;
        private double m_nextRawRefreshTime = double.MinValue;
        private double m_nextExplicitDiscoveryTime = double.MinValue;

        private sealed class ResolvedPersonalRoute
        {
            public IPAddress Address;
            public int Port;
            public PersonalServerRecord Record;
        }

        public RemoteServerDirectory(Explorer explorer)
        {
            m_explorer = explorer ?? throw new ArgumentNullException(nameof(explorer));
        }

        // Source: EntitySystem/SuAPI/ModResource.cs:ModResource.LoadModResources
        public void Start()
        {
            PersonalServerDirectory.Load();
            PersonalServerDirectory.Changed += HandlePersonalServersChanged;
            ReloadPersonalHosts();
            AddHosts(m_localHosts, ReadEmbeddedDirectory());
            if (m_localHosts.Count == 0)
                m_localHosts.Add("suceru.site");
            ApplyHosts();
            SetDiscoveryEnabled(true);
        }

        // Source: EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs:SuAPICoreMod.OnUnload
        public void Stop()
        {
            PersonalServerDirectory.Changed -= HandlePersonalServersChanged;
            Interlocked.Increment(ref m_resolveGeneration);
            Interlocked.Increment(ref m_discoveryGeneration);
            Interlocked.Increment(ref m_rawRequestId);
            m_rawRefreshProgress?.Cancel();
        }

        // Source: Survivalcraft/Game/ContentManager.cs:ContentManager.List
        public void Update()
        {
            if (!m_discoveryEnabled) return;
            if (!m_contentRootChecked)
                LoadMatchingContentRootFile();
            if (!m_rawRefreshInProgress && Time.RealTime >= m_nextRawRefreshTime)
                BeginRawRefresh();
            UpdateExplicitEndpointDiscovery();
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.StartDiscovery
        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.StopDiscovery
        public void SetDiscoveryEnabled(bool enabled)
        {
            if (m_discoveryEnabled == enabled) return;
            m_discoveryEnabled = enabled;
            Interlocked.Increment(ref m_discoveryGeneration);
            if (enabled)
            {
                StartExplorerDiscovery();
                m_nextRawRefreshTime = double.MinValue;
                m_nextExplicitDiscoveryTime = double.MinValue;
                Log.Information("[ScMP] Explorer discovery resumed");
                return;
            }

            Interlocked.Increment(ref m_resolveGeneration);
            Interlocked.Increment(ref m_rawRequestId);
            m_rawRefreshProgress?.Cancel();
            m_rawRefreshProgress = null;
            m_rawRefreshInProgress = false;
            m_pendingRemoteHosts = null;
            m_explorer.StopDiscovery();
            Log.Information("[ScMP] Explorer discovery paused while a room is active");
        }

        public string GetHostName(IPEndPoint endpoint)
        {
            if (endpoint == null) return null;
            lock (m_resolvedHostsLock)
            {
                return m_resolvedHosts.TryGetValue(endpoint.Address, out string host)
                    ? host
                    : null;
            }
        }

        // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:RemoteServerDirectory.GetHostName
        public PersonalServerRecord GetPersonalServer(IPEndPoint endpoint)
        {
            if (endpoint == null) return null;
            lock (m_resolvedHostsLock)
            {
                return m_resolvedPersonalRoutes.FirstOrDefault(route =>
                    route.Address.Equals(endpoint.Address) &&
                    (route.Port == 0 || route.Port == endpoint.Port))?.Record;
            }
        }

        // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:RemoteServerDirectory.ApplyHosts
        private void HandlePersonalServersChanged()
        {
            ReloadPersonalHosts();
            ApplyHosts(forceResolve: true);
        }

        // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:RemoteServerDirectory.ApplyHosts
        private void ReloadPersonalHosts()
        {
            m_personalHosts.Clear();
            AddHosts(m_personalHosts,
                PersonalServerDirectory.Records.Select(record => record.Address));
        }

        // Source: EntitySystem/SuAPI/ModResource.cs:ModResource.LoadModResources
        // Only the ContentRoot direct child whose original name is ServerDns.txt is accepted.
        private void LoadMatchingContentRootFile()
        {
            try
            {
                ContentInfo resource = ContentManager.List("Mod").FirstOrDefault(info =>
                    string.Equals(info.Name, ContentResourceName,
                        StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(resource.Name))
                {
                    string text = ContentManager.Get<string>(ContentResourceName);
                    AddHosts(m_localHosts, ParseHosts(text));
                    ApplyHosts();
                }
                m_contentRootChecked = true;
            }
            catch (Exception error)
            {
                Log.Warning($"[ScMP] Unable to read {DirectoryFileName} from ContentRoot: " +
                    error.Message);
            }
        }

        private static IEnumerable<string> ReadEmbeddedDirectory()
        {
            try
            {
                using Stream stream = typeof(RemoteServerDirectory).Assembly
                    .GetManifestResourceStream(EmbeddedResourceName);
                if (stream == null) return Array.Empty<string>();
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                return ParseHosts(reader.ReadToEnd()).ToArray();
            }
            catch (Exception error)
            {
                Log.Warning($"[ScMP] Unable to read embedded {DirectoryFileName}: " +
                    error.Message);
                return Array.Empty<string>();
            }
        }

        // Source: Survivalcraft/Game/WebManager.cs:WebManager.Get
        private void BeginRawRefresh()
        {
            m_rawRefreshInProgress = true;
            m_rawRefreshSucceeded = false;
            m_rawSourceIndex = 0;
            m_pendingRemoteHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FetchNextRawSource();
        }

        private void FetchNextRawSource()
        {
            if (m_rawSourceIndex >= RawDirectoryUrls.Length)
            {
                FinishRawRefresh();
                return;
            }

            string url = RawDirectoryUrls[m_rawSourceIndex++];
            int requestId = ++m_rawRequestId;
            var progress = new CancellableProgress();
            m_rawRefreshProgress = progress;
            Task.Run(async delegate
            {
                await Task.Delay(RawRequestTimeoutMilliseconds);
                Dispatcher.Dispatch(delegate
                {
                    if (m_rawRefreshInProgress && requestId == m_rawRequestId)
                        progress.Cancel();
                });
            });

            WebManager.Get(url, null, null, progress,
                data => CompleteRawSource(requestId, url, data, null),
                error => CompleteRawSource(requestId, url, null, error));
        }

        private void CompleteRawSource(int requestId, string url, byte[] data, Exception error)
        {
            if (!m_rawRefreshInProgress || requestId != m_rawRequestId) return;
            m_rawRefreshProgress = null;
            if (error == null && data != null && data.Length <= MaximumDirectoryBytes)
            {
                string text = Encoding.UTF8.GetString(data, 0, data.Length);
                string[] hosts = ParseHosts(text).Take(MaximumHosts).ToArray();
                if (hosts.Length > 0)
                {
                    AddHosts(m_pendingRemoteHosts, hosts);
                    m_rawRefreshSucceeded = true;
                    Log.Information($"[ScMP] Loaded {hosts.Length} service DNS entries from {url}");
                }
            }
            else if (data != null && data.Length > MaximumDirectoryBytes)
            {
                Log.Warning($"[ScMP] Ignored oversized service DNS directory from {url}");
            }
            FetchNextRawSource();
        }

        private void FinishRawRefresh()
        {
            if (m_rawRefreshSucceeded && m_pendingRemoteHosts.Count > 0)
            {
                m_remoteHosts.Clear();
                AddHosts(m_remoteHosts, m_pendingRemoteHosts);
                ApplyHosts();
            }
            m_pendingRemoteHosts = null;
            m_rawRefreshInProgress = false;
            m_nextRawRefreshTime = Time.RealTime +
                (m_rawRefreshSucceeded ? SuccessfulRefreshPeriod : FailedRefreshPeriod);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.StartDiscovery
        private void ApplyHosts(bool forceResolve = false)
        {
            string[] hosts = m_personalHosts.OrderBy(host => host,
                    StringComparer.OrdinalIgnoreCase)
                .Concat(m_localHosts.Concat(m_remoteHosts).OrderBy(host => host,
                    StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumHosts)
                .ToArray();
            if (hosts.SequenceEqual(m_activeHosts, StringComparer.OrdinalIgnoreCase))
            {
                if (forceResolve && m_discoveryEnabled)
                    ResolveDirectoryEntries(m_activeHosts);
                return;
            }
            m_activeHosts = hosts;
            if (!m_discoveryEnabled) return;
            StartExplorerDiscovery();
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.StartDiscovery
        private void StartExplorerDiscovery()
        {
            string[] standardHosts = m_activeHosts.Select(entry =>
                    TryParseDirectoryEntry(entry, out string host, out int port) && port == 0
                        ? host
                        : null)
                .Where(host => !string.IsNullOrEmpty(host))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int explicitCount = m_activeHosts.Count(entry =>
                TryParseDirectoryEntry(entry, out _, out int port) && port > 0);
            m_explorer.StartDiscovery(localBroadcast: true, internetHosts: standardHosts);
            ResolveDirectoryEntries(m_activeHosts);
            Log.Information($"[ScMP] Service discovery enabled for {standardHosts.Length} DNS " +
                $"entries across ports {ScMultiplayerSettings.ServerPorts[0]}-" +
                $"{ScMultiplayerSettings.ServerPorts[ScMultiplayerSettings.ServerPorts.Length - 1]} " +
                $"and {explicitCount} explicit endpoints");
        }

        // Source: SCAPI24/RuthlessConquest:RuthlessConquest/Net/ServersManager.DnsQueryServerAddresses
        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.DiscoverServer
        private void ResolveDirectoryEntries(IEnumerable<string> entries)
        {
            int generation = ++m_resolveGeneration;
            string[] entryArray = entries.ToArray();
            var personalRecords = PersonalServerDirectory.Records.ToDictionary(
                record => record.Address, StringComparer.OrdinalIgnoreCase);
            Task.Run(delegate
            {
                var resolved = new Dictionary<IPAddress, string>();
                var explicitEndpoints = new HashSet<IPEndPoint>();
                var personalRoutes = new List<ResolvedPersonalRoute>();
                foreach (string entry in entryArray)
                {
                    if (generation != Volatile.Read(ref m_resolveGeneration)) break;
                    if (!TryParseDirectoryEntry(entry, out string host, out int port))
                        continue;
                    personalRecords.TryGetValue(FormatDirectoryEntry(host, port),
                        out PersonalServerRecord personalRecord);
                    try
                    {
                        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress literal)
                            ? new[] { literal }
                            : Dns.GetHostEntry(host).AddressList;
                        foreach (IPAddress address in addresses)
                        {
                            if ((address.AddressFamily == AddressFamily.InterNetwork ||
                                address.AddressFamily == AddressFamily.InterNetworkV6) &&
                                !resolved.ContainsKey(address))
                                resolved.Add(address, host);
                            if (port > 0)
                                explicitEndpoints.Add(new IPEndPoint(address, port));
                            if (personalRecord != null)
                            {
                                personalRoutes.Add(new ResolvedPersonalRoute
                                {
                                    Address = address,
                                    Port = port,
                                    Record = personalRecord
                                });
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        Log.Warning($"[ScMP] DNS lookup failed for {host}: {error.Message}");
                    }
                }
                lock (m_resolvedHostsLock)
                {
                    if (generation == m_resolveGeneration)
                    {
                        m_resolvedHosts = resolved;
                        m_resolvedExplicitEndpoints = explicitEndpoints.ToArray();
                        m_resolvedPersonalRoutes = personalRoutes.ToArray();
                        m_nextExplicitDiscoveryTime = double.MinValue;
                    }
                }
            });
        }

        private void UpdateExplicitEndpointDiscovery()
        {
            if (!m_discoveryEnabled) return;
            if (Time.RealTime < m_nextExplicitDiscoveryTime) return;
            m_nextExplicitDiscoveryTime = Time.RealTime +
                Math.Max(m_explorer.Settings.InternetDiscoveryPeriod, 1f);
            int generation = Volatile.Read(ref m_discoveryGeneration);
            IPEndPoint[] endpoints;
            lock (m_resolvedHostsLock)
                endpoints = m_resolvedExplicitEndpoints.ToArray();
            // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.DiscoverServer
            // Socket.SendTo is synchronous. Keep explicit service probes off the frame thread.
            Task.Run(delegate
            {
                foreach (IPEndPoint endpoint in endpoints)
                {
                    if (generation != Volatile.Read(ref m_discoveryGeneration)) break;
                    try
                    {
                        // A host:port directory entry is still an internet service endpoint even
                        // though it uses a direct unicast probe.
                        m_explorer.DiscoverServer(endpoint, isInternet: true);
                    }
                    catch (Exception error)
                    {
                        Log.Warning($"[ScMP] Explicit endpoint probe failed for {endpoint}: " +
                            error.Message);
                    }
                }
            });
        }

        private static void AddHosts(ISet<string> target, IEnumerable<string> hosts)
        {
            if (target == null || hosts == null) return;
            foreach (string host in hosts)
            {
                if (target.Count >= MaximumHosts) break;
                target.Add(host);
            }
        }

        // Source: Mod/ScMultiplayer/ServerDns.txt
        internal static IEnumerable<string> ParseHosts(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (string sourceLine in text.Split(new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.None))
            {
                string line = sourceLine.Trim().TrimStart('\ufeff');
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") ||
                    line.StartsWith("//"))
                    continue;
                int hashComment = line.IndexOf('#');
                if (hashComment >= 0) line = line.Substring(0, hashComment);
                int slashComment = line.IndexOf("//", StringComparison.Ordinal);
                if (slashComment >= 0) line = line.Substring(0, slashComment);

                foreach (string rawToken in line.Split(
                    new[] { ' ', '\t', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string token = rawToken.Trim('"', '\'', '(', ')', '<', '>').TrimEnd('.');
                    if (token.Length == 0 || token.Length > 253) continue;
                    if (!TryParseDirectoryEntry(token, out string host, out int port)) continue;
                    yield return FormatDirectoryEntry(host, port);
                }
            }
        }

        // Source: Mod/ScMultiplayer/ServerDns.txt
        // Accept a normal host (scanned over the configured server range), host:port, and
        // bracketed IPv6 [address]:port. Explicit ports are UDP discovery endpoints.
        internal static bool TryParseDirectoryEntry(string token, out string host,
            out int port)
        {
            host = string.Empty;
            port = 0;
            if (string.IsNullOrWhiteSpace(token)) return false;
            string value = token.Trim();
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closingBracket = value.IndexOf(']');
                if (closingBracket <= 1) return false;
                host = value.Substring(1, closingBracket - 1);
                if (closingBracket + 1 < value.Length)
                {
                    if (value[closingBracket + 1] != ':' ||
                        !int.TryParse(value.Substring(closingBracket + 2), out port))
                        return false;
                }
            }
            else
            {
                int firstColon = value.IndexOf(':');
                int lastColon = value.LastIndexOf(':');
                if (firstColon > 0 && firstColon == lastColon &&
                    int.TryParse(value.Substring(lastColon + 1), out int explicitPort))
                {
                    host = value.Substring(0, lastColon);
                    port = explicitPort;
                }
                else
                {
                    host = value;
                }
            }
            if (port < 0 || port > ushort.MaxValue || (value.Contains(":") && port == 0 &&
                !IPAddress.TryParse(host, out _)))
                return false;
            if (!IPAddress.TryParse(host, out _) &&
                Uri.CheckHostName(host) == UriHostNameType.Unknown)
                return false;
            return host.IndexOf('.') >= 0 || host.IndexOf(':') >= 0;
        }

        internal static string FormatDirectoryEntry(string host, int port)
        {
            if (port <= 0) return host;
            return host.IndexOf(':') >= 0
                ? $"[{host}]:{port}"
                : $"{host}:{port}";
        }
    }
}

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
        private readonly object m_resolvedHostsLock = new object();
        private Dictionary<IPAddress, string> m_resolvedHosts =
            new Dictionary<IPAddress, string>();
        private string[] m_activeHosts = Array.Empty<string>();
        private HashSet<string> m_pendingRemoteHosts;
        private bool m_contentRootChecked;
        private bool m_rawRefreshInProgress;
        private bool m_rawRefreshSucceeded;
        private int m_rawSourceIndex;
        private int m_rawRequestId;
        private int m_resolveGeneration;
        private double m_nextRawRefreshTime = double.MinValue;

        public RemoteServerDirectory(Explorer explorer)
        {
            m_explorer = explorer ?? throw new ArgumentNullException(nameof(explorer));
        }

        // Source: EntitySystem/SuAPI/ModResource.cs:ModResource.LoadModResources
        public void Start()
        {
            AddHosts(m_localHosts, ReadEmbeddedDirectory());
            if (m_localHosts.Count == 0)
                m_localHosts.Add("suceru.site");
            ApplyHosts();
        }

        // Source: Survivalcraft/Game/ContentManager.cs:ContentManager.List
        public void Update()
        {
            if (!m_contentRootChecked)
                LoadMatchingContentRootFile();
            if (!m_rawRefreshInProgress && Time.RealTime >= m_nextRawRefreshTime)
                BeginRawRefresh();
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
        private void ApplyHosts()
        {
            string[] hosts = m_localHosts.Concat(m_remoteHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumHosts)
                .ToArray();
            if (hosts.SequenceEqual(m_activeHosts, StringComparer.OrdinalIgnoreCase)) return;
            m_activeHosts = hosts;
            m_explorer.StartDiscovery(localBroadcast: true, internetHosts: m_activeHosts);
            ResolveHostNames(m_activeHosts);
            Log.Information($"[ScMP] Service discovery enabled for {m_activeHosts.Length} DNS " +
                $"entries across ports {ScMultiplayerSettings.ServerPorts[0]}-" +
                $"{ScMultiplayerSettings.ServerPorts[ScMultiplayerSettings.ServerPorts.Length - 1]}");
        }

        // Source: SCAPI24/RuthlessConquest:RuthlessConquest/Net/ServersManager.DnsQueryServerAddresses
        private void ResolveHostNames(IEnumerable<string> hosts)
        {
            int generation = ++m_resolveGeneration;
            string[] hostArray = hosts.ToArray();
            Task.Run(delegate
            {
                var resolved = new Dictionary<IPAddress, string>();
                foreach (string host in hostArray)
                {
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
                        m_resolvedHosts = resolved;
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
                    string token = rawToken.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '.');
                    if (token.Length == 0 || token.Length > 253) continue;
                    if (Uri.CheckHostName(token) == UriHostNameType.Unknown) continue;
                    if (token.IndexOf('.') < 0 && token.IndexOf(':') < 0) continue;
                    yield return token;
                }
            }
        }
    }
}

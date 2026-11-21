using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using SuMultiplayer.Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// fake-ip（198.18.0.0/15）时的 DoH 兜底解析：DoH 端点用 IP 字面量，避免自身被 DNS 污染。
    /// 只负责域名 → 真实地址；直连使用真实 IP，SOCKS5 仍优先用域名（让代理解析）。
    /// </summary>
    public static class DohResolver
    {
        private const int TimeoutMs = 3000;
        private const int MaxDohTtlSeconds = 30;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Lazy<CacheEntry>> s_dohLookups =
            new Dictionary<string, Lazy<CacheEntry>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient s_client = CreateClient();
        private static readonly string[] s_endpoints =
        {
            "https://223.5.5.5/resolve?name={0}&type={1}",
            "https://1.1.1.1/dns-query?name={0}&type={1}"
        };

        private sealed class CacheEntry
        {
            public CacheEntry(IPAddress[] addresses, int ttlSeconds)
            {
                Addresses = addresses;
                ExpiresAtMs = Environment.TickCount64 + ttlSeconds * 1000L;
            }

            public IPAddress[] Addresses { get; }
            public long ExpiresAtMs { get; }
        }

        public static bool IsFakeIp(IPAddress address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            byte[] bytes = address.GetAddressBytes();
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                (bytes[0] == 198 && bytes[1] >= 18 && bytes[1] <= 19);
        }

        public static IPAddress[] ResolveHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return Array.Empty<IPAddress>();
            if (IPAddress.TryParse(host, out IPAddress literal)) return new[] { literal };

            // Source: ServerCatalog.cs:ServerCatalog.TryDirectProbe
            // The OS already honors DNS expiry. Its usable answers take priority over any DoH cache.
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host).Where(address => !IsFakeIp(address))
                    .OrderBy(address => address.AddressFamily != AddressFamily.InterNetwork).ToArray();
                if (addresses.Length > 0) return addresses;
            }
            catch (SocketException error)
            {
                ModLog.Info("[SuMP][doh] System DNS could not resolve " + host + ": " + error.SocketErrorCode);
            }

            // Several ports for the same host may be probed concurrently. Share their DoH query.
            Lazy<CacheEntry> lookup;
            lock (Gate)
            {
                if (!s_dohLookups.TryGetValue(host, out lookup) ||
                    (lookup.IsValueCreated && lookup.Value.ExpiresAtMs <= Environment.TickCount64))
                {
                    lookup = new Lazy<CacheEntry>(() => ResolveDoh(host));
                    s_dohLookups[host] = lookup;
                }
            }
            return lookup.Value.Addresses;
        }

        private static CacheEntry ResolveDoh(string host)
        {
            var result = ResolveDohRecords(host, type: 1);
            if (result.Addresses.Length == 0) result = ResolveDohRecords(host, type: 28);
            return result;
        }

        private static CacheEntry ResolveDohRecords(string host, int type)
        {
            string encoded = Uri.EscapeDataString(host);
            foreach (string template in s_endpoints)
            {
                string url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    template, encoded, type);
                try
                {
                    string json = s_client.GetStringAsync(url).GetAwaiter().GetResult();
                    CacheEntry result = ReadDohAnswer(json, type);
                    if (result.Addresses.Length > 0) return result;
                }
                catch (Exception error)
                {
                    ModLog.Info("[SuMP][doh] " + url + " 失败：" + error.Message);
                }
            }
            return new CacheEntry(Array.Empty<IPAddress>(), ttlSeconds: 0);
        }

        private static CacheEntry ReadDohAnswer(string json, int type)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Status", out JsonElement status) ||
                status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int responseCode) ||
                responseCode != 0 || !document.RootElement.TryGetProperty("Answer", out JsonElement answers) ||
                answers.ValueKind != JsonValueKind.Array)
                return new CacheEntry(Array.Empty<IPAddress>(), ttlSeconds: 0);

            var addresses = new List<IPAddress>();
            int ttlSeconds = MaxDohTtlSeconds;
            AddressFamily family = type == 1 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
            foreach (JsonElement answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("type", out JsonElement answerType) ||
                    answerType.ValueKind != JsonValueKind.Number || !answerType.TryGetInt32(out int recordType))
                    continue;
                if (recordType == type && answer.TryGetProperty("data", out JsonElement data) &&
                    data.ValueKind == JsonValueKind.String &&
                    IPAddress.TryParse(data.GetString(), out IPAddress address) &&
                    address.AddressFamily == family && !IsFakeIp(address))
                {
                    if (!addresses.Contains(address)) addresses.Add(address);
                }
                else if (recordType != 5) continue; // An alias may expire before its address record.

                int recordTtl = 0;
                if (answer.TryGetProperty("TTL", out JsonElement ttl) && ttl.ValueKind == JsonValueKind.Number)
                    ttl.TryGetInt32(out recordTtl);
                ttlSeconds = Math.Min(ttlSeconds, Math.Max(0, recordTtl));
            }
            return new CacheEntry(addresses.ToArray(), addresses.Count == 0 ? 0 : ttlSeconds);
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
            try
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
                    "application/dns-json");
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "SuAPI-SuMP");
            }
            catch (Exception) { }
            return client;
        }
    }
}

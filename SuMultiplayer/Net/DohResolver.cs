using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// fake-ip（198.18.0.0/15）时的 DoH 兜底解析：DoH 端点用 IP 字面量，避免自身被 DNS 污染。
    /// 只负责域名 → 真实地址；直连使用真实 IP，SOCKS5 仍优先用域名（让代理解析）。
    /// </summary>
    public static class DohResolver
    {
        private const int TimeoutMs = 3000;
        private const long CacheTtlMs = 10 * 60 * 1000;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CacheEntry> s_cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient s_client = CreateClient();
        private static readonly string[] s_endpoints =
        {
            "https://223.5.5.5/resolve?name={0}&type={1}",
            "https://1.1.1.1/dns-query?name={0}&type={1}"
        };

        private sealed class CacheEntry
        {
            public IPAddress[] Addresses;
            public long ExpiresAtMs;
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
            lock (Gate)
            {
                if (s_cache.TryGetValue(host, out CacheEntry cached) &&
                    cached.ExpiresAtMs > Environment.TickCount64)
                    return cached.Addresses;
            }
            List<IPAddress> addresses = Resolve(host);
            if (addresses.Count == 0)
            {
                // DoH 不可用：回退系统 DNS（可能是 fake-ip；由调用方/代理决定是否直连）。
                try
                {
                    addresses.AddRange(Dns.GetHostAddresses(host));
                }
                catch (Exception)
                {
                }
            }
            IPAddress[] result = addresses.ToArray();
            if (result.Length > 0)
            {
                lock (Gate)
                {
                    s_cache[host] = new CacheEntry
                    {
                        Addresses = result,
                        ExpiresAtMs = Environment.TickCount64 + CacheTtlMs
                    };
                }
            }
            return result;
        }

        private static List<IPAddress> Resolve(string host)
        {
            var result = new List<IPAddress>();
            Collect(host, 1, result);
            if (result.Count == 0) Collect(host, 28, result);
            return result;
        }

        private static void Collect(string host, int type, List<IPAddress> result)
        {
            string encoded = Uri.EscapeDataString(host);
            foreach (string template in s_endpoints)
            {
                string url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    template, encoded, type);
                try
                {
                    string json = s_client.GetStringAsync(url).GetAwaiter().GetResult();
                    using JsonDocument document = JsonDocument.Parse(json);
                    if (!document.RootElement.TryGetProperty("Answer", out JsonElement answers) ||
                        answers.ValueKind != JsonValueKind.Array) continue;
                    foreach (JsonElement answer in answers.EnumerateArray())
                    {
                        if (!answer.TryGetProperty("type", out JsonElement answerType) ||
                            answerType.GetInt32() != type) continue;
                        if (!answer.TryGetProperty("data", out JsonElement data)) continue;
                        if (IPAddress.TryParse(data.GetString(), out IPAddress address) &&
                            !result.Contains(address)) result.Add(address);
                    }
                    if (result.Count > 0) return;
                }
                catch (Exception error)
                {
                    global::Engine.Log.Information("[SuMP][doh] " + url + " 失败：" +
                        error.Message);
                }
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
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

using Engine;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ScMultiplayer
{
    /// <summary>
    /// fake-ip（198.18.0.0/15）时的 DoH 兜底解析。
    ///
    /// 本机 DNS 被代理抢答时（域名 → 198.18.x），直连和 ZeroTier 通路都会拿到一个不可达的地址。
    /// DoH 端点写成 **IP 字面量**（`https://223.5.5.5/…`、`https://1.1.1.1/…`），解析它本身不需要 DNS，
    /// 所以抢答拦不住它，能拿到真实 A/AAAA 记录。
    ///
    /// 拿到真实地址后：直连/TUN/ZeroTier 走真实 IP；SOCKS 侧仍然优先用域名（ATYP=3），由代理解析。
    /// </summary>
    internal static class DohResolver
    {
        private const int TimeoutMilliseconds = 3000;

        private static readonly HttpClient s_client = CreateClient();

        // 结果缓存：发现流程会周期性重解析，没有缓存就会反复打 DoH 并刷日志。
        private const long CacheTtlMilliseconds = 10 * 60 * 1000;
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CacheEntry> Cache = new();

        private sealed class CacheEntry
        {
            public IPAddress[] Addresses;
            public long ExpiresAtTicks;
        }

        private static readonly string[] s_endpoints =
        {
            "https://223.5.5.5/resolve?name={0}&type={1}",
            "https://1.1.1.1/dns-query?name={0}&type={1}"
        };

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(TimeoutMilliseconds)
            };
            try
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
                    "application/dns-json");
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SuAPI-ScMP");
            }
            catch (Exception)
            {
            }
            return client;
        }

        /// <summary>地址里出现 fake-ip 时用 DoH 换成真实地址；DoH 失败则原样返回（行为不变）。</summary>
        public static IPAddress[] ReplaceFakeIpAddresses(string host, IPAddress[] resolved)
        {
            if (string.IsNullOrWhiteSpace(host) || resolved == null || resolved.Length == 0)
                return resolved;
            bool hijacked = false;
            string hijackedAddress = string.Empty;
            foreach (IPAddress address in resolved)
            {
                if (Comms.Socks5ProxyState.IsFakeIpAddress(address))
                {
                    hijacked = true;
                    hijackedAddress = address.ToString();
                    break;
                }
            }
            if (!hijacked)
                return resolved;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(host, out CacheEntry cached) && cached != null &&
                    Environment.TickCount64 < cached.ExpiresAtTicks)
                    return cached.Addresses;
            }
            List<IPAddress> real = Resolve(host);
            if (real.Count == 0)
            {
                Log.Warning("[ScMP] DNS for " + host + " looks hijacked (fake-ip), and DoH " +
                    "resolution failed; keeping the proxy-provided address");
                return resolved;
            }
            IPAddress[] result = real.ToArray();
            lock (CacheLock)
            {
                Cache[host] = new CacheEntry
                {
                    Addresses = result,
                    ExpiresAtTicks = Environment.TickCount64 + CacheTtlMilliseconds
                };
            }
            Log.Information("[ScMP] DNS for " + host + " was hijacked (" + hijackedAddress + 
                "); DoH resolved " +
                string.Join(", ", Array.ConvertAll(result, address => address.ToString())));
            return result;
        }

        /// <summary>
        /// 供加入流程使用：按域名拿（带缓存的）真实地址。命中缓存时立即返回，不阻塞帧。
        /// </summary>
        public static IPAddress[] ResolveHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return Array.Empty<IPAddress>();
            lock (CacheLock)
            {
                if (Cache.TryGetValue(host, out CacheEntry cached) && cached != null &&
                    Environment.TickCount64 < cached.ExpiresAtTicks)
                    return cached.Addresses;
            }
            List<IPAddress> real = Resolve(host);
            if (real.Count == 0)
                return Array.Empty<IPAddress>();
            IPAddress[] result = real.ToArray();
            lock (CacheLock)
            {
                Cache[host] = new CacheEntry
                {
                    Addresses = result,
                    ExpiresAtTicks = Environment.TickCount64 + CacheTtlMilliseconds
                };
            }
            Log.Information("[ScMP] DoH resolved " + host + " -> " +
                string.Join(", ", Array.ConvertAll(result, address => address.ToString())));
            return result;
        }

        private static List<IPAddress> Resolve(string host)
        {
            var result = new List<IPAddress>();
            Collect(host, 1, result);      // A
            if (result.Count == 0)
                Collect(host, 28, result); // AAAA
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
                        answers.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (JsonElement answer in answers.EnumerateArray())
                    {
                        if (!answer.TryGetProperty("type", out JsonElement answerType) ||
                            answerType.GetInt32() != type)
                        {
                            continue;
                        }
                        if (!answer.TryGetProperty("data", out JsonElement data))
                            continue;
                        if (IPAddress.TryParse(data.GetString(), out IPAddress address) &&
                            !result.Contains(address))
                        {
                            result.Add(address);
                        }
                    }
                    if (result.Count > 0)
                        return;
                }
                catch (Exception ex)
                {
                    Log.Information("[ScMP] DoH endpoint " + url + " failed: " + ex.Message);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SuMultiplayer.Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// SOCKS5 路由选择：
    ///   Default = 自动比较直连/代理延迟；差距小默认直连；
    ///   On      = 强制代理（局域网/私有地址仍然直连，否则 LAN 发现/本地连接没有意义）；
    ///   Off     = 强制直连。
    ///
    /// 只对公网目标做延迟测量；测量使用 TCP connect（直接 / 经 SOCKS5 CONNECT）各一次，
    /// 结果缓存 60 秒，避免每次加入房间都重复拨号。
    /// </summary>
    public static class ProxyRouter
    {
        private const int MeasureTimeoutMs = 700;
        private const int DecisionTtlMs = 60000;
        private sealed class Decision
        {
            public bool UseProxy;
            public string Reason;
            public long ExpiresMs;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Decision> s_cache =
            new Dictionary<string, Decision>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> s_localHosts =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static bool TryChooseTcp(string host, int port, out bool useProxy, out string reason)
        {
            useProxy = false;
            reason = null;
            if (string.IsNullOrWhiteSpace(host) || port <= 0)
            {
                reason = "目标地址非法";
                return false;
            }
            if (IsPrivateOrLanTarget(host))
            {
                reason = "局域网/私有地址直连";
                return true;
            }
            ProxyMode mode = ProxySettings.Mode;
            if (mode == ProxyMode.Off)
            {
                reason = "代理模式 Off：强制直连";
                return true;
            }
            if (mode == ProxyMode.On)
            {
                useProxy = true;
                reason = "代理模式 On：强制代理";
                return true;
            }
            string key = host + ":" + port;
            long now = Environment.TickCount64;
            lock (Gate)
            {
                if (s_cache.TryGetValue(key, out Decision cached) && cached.ExpiresMs > now)
                {
                    useProxy = cached.UseProxy;
                    reason = cached.Reason + "（缓存）";
                    return true;
                }
            }
            // Default：一次直连/代理 TCP 握手测量。两者都失败时保持直连，让真正连接阶段
            // 暴露明确错误；代理失败时绝不假装代理可用。
            int directMs = MeasureTcp(host, port, viaProxy: false);
            int proxyMs = MeasureTcp(host, port, viaProxy: true);
            bool chooseProxy;
            string why;
            if (directMs >= 0 && proxyMs >= 0)
            {
                chooseProxy = proxyMs + ProxySettings.AutoTieThresholdMs < directMs;
                why = "Default：直连 " + directMs + "ms / 代理 " + proxyMs + "ms（阈值 " +
                    ProxySettings.AutoTieThresholdMs + "ms）→ " +
                    (chooseProxy ? "代理" : "直连");
            }
            else if (directMs < 0 && proxyMs >= 0)
            {
                chooseProxy = true;
                why = "Default：直连失败 / 代理 " + proxyMs + "ms → 代理";
            }
            else if (directMs >= 0 && proxyMs < 0)
            {
                chooseProxy = false;
                why = "Default：直连 " + directMs + "ms / 代理失败 → 直连";
            }
            else
            {
                chooseProxy = false;
                why = "Default：直连与代理探测均失败 → 保持直连，连接阶段再明确报错";
            }
            lock (Gate)
            {
                s_cache[key] = new Decision
                {
                    UseProxy = chooseProxy,
                    Reason = why,
                    ExpiresMs = now + DecisionTtlMs
                };
            }
            useProxy = chooseProxy;
            reason = why;
            return true;
        }

        public static bool TryChooseUdp(string host, int port, out bool useProxy, out string reason)
        {
            // UDP 只用于公共 Beacon 探测；Default 下先按同一目标的 TCP 决策走，
            // 没有决策时直接走直连（失败由调用方明确记录）。
            if (IsPrivateOrLanTarget(host))
            {
                useProxy = false;
                reason = "局域网/私有地址直连";
                return true;
            }
            if (ProxySettings.Mode == ProxyMode.Off)
            {
                useProxy = false;
                reason = "代理模式 Off：UDP 强制直连";
                return true;
            }
            if (ProxySettings.Mode == ProxyMode.On)
            {
                useProxy = true;
                reason = "代理模式 On：UDP 强制代理";
                return true;
            }
            if (TryChooseTcp(host, port, out bool tcpProxy, out string tcpReason))
            {
                useProxy = tcpProxy;
                reason = "Default：沿用同目标 TCP 决策（" + tcpReason + "）";
                return true;
            }
            useProxy = false;
            reason = "Default：无 UDP 决策，直连";
            return true;
        }

        public static bool IsPrivateOrLanTarget(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return true;
            lock (Gate)
            {
                if (s_localHosts.TryGetValue(host, out bool cached)) return cached;
            }
            bool local = false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
            {
                local = true;
            }
            else if (IPAddress.TryParse(host, out IPAddress address))
            {
                local = IsPrivateAddress(address);
            }
            else
            {
                try
                {
                    IPAddress[] addresses = Dns.GetHostAddresses(host);
                    for (int i = 0; i < addresses.Length; i++)
                    {
                        if (IsPrivateAddress(addresses[i])) { local = true; break; }
                    }
                }
                catch (Exception)
                {
                    local = false;
                }
            }
            lock (Gate) s_localHosts[host] = local;
            return local;
        }

        private static bool IsPrivateAddress(IPAddress address)
        {
            if (address == null) return true;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any)) return true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                    address.IsIPv6UniqueLocal;
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || b[0] == 0 ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254);
        }

        private static int MeasureTcp(string host, int port, bool viaProxy)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                if (!viaProxy)
                {
                    using var client = new TcpClient { NoDelay = true };
                    if (!client.ConnectAsync(host, port).Wait(MeasureTimeoutMs)) return -1;
                    watch.Stop();
                    return (int)Math.Max(0, watch.ElapsedMilliseconds);
                }
                Socket socket = Socks5Proxy.Connect(host, port, MeasureTimeoutMs, out _);
                if (socket == null) return -1;
                socket.Close();
                watch.Stop();
                return (int)Math.Max(0, watch.ElapsedMilliseconds);
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}

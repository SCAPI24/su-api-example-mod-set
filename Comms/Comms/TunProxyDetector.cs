using System;
using System.Net.NetworkInformation;

namespace Comms;

// Source: Mod/ScMultiplayer/doc/PROXY-AND-RESTRICTED-NETWORKS.md
// TUN 代理网卡识别。用途只有一件事：决定**普通公网地址**要不要自己再套一层 SOCKS。
//
// 背景：网络上常见的用法是"混合代理 + 虚拟网卡"（Clash/mihomo 的 TUN）或只开代理不开网卡。
//   · 开了 TUN：普通公网地址交给 TUN 自己带（两边只过一次代理），我们只对 fake-ip 地址兜底；
//   · 没开 TUN：只有 SOCKS 这一条路，普通公网地址也走 SOCKS。
//
// ZeroTier / Tailscale 明确**排除**：它们是私有网络不是上网代理，地址也永远走直连。
public static class TunProxyDetector
{
    private static readonly string[] Markers =
    {
        "meta tunnel", "mihomo", "clash", "flclash", "wintun", "tun2socks", "sing-box",
        "singbox", "v2ray", "xray", "netch", "sstap", "wireguard", "openvpn", "utun",
        "tap-windows", "proxy"
    };

    private static readonly string[] Exclusions =
    {
        "zerotier", "tailscale", "hyper-v", "vmware", "virtualbox", "hamachi", "radmin",
        "loopback", "bluetooth", "wsl", "docker"
    };

    private static readonly object Lock = new object();
    private static bool s_detected;
    private static string s_name = string.Empty;
    private static long s_nextRefreshTicks;

    public static bool IsActive
    {
        get
        {
            Refresh();
            lock (Lock)
                return s_detected;
        }
    }

    public static string ActiveAdapterName
    {
        get
        {
            Refresh();
            lock (Lock)
                return s_name;
        }
    }

    private static void Refresh()
    {
        long now = Environment.TickCount64;
        lock (Lock)
        {
            if (now < s_nextRefreshTicks)
                return;
            s_nextRefreshTicks = now + 5000;
        }
        bool detected = false;
        string name = string.Empty;
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                string text = ((adapter.Name ?? string.Empty) + " " +
                    (adapter.Description ?? string.Empty)).ToLowerInvariant();
                if (Matches(text, Exclusions))
                    continue;
                if (!Matches(text, Markers))
                    continue;
                detected = true;
                name = adapter.Name + " (" + adapter.Description + ")";
                break;
            }
        }
        catch (Exception)
        {
            detected = false;
        }
        lock (Lock)
        {
            s_detected = detected;
            s_name = name;
        }
    }

    private static bool Matches(string text, string[] patterns)
    {
        foreach (string pattern in patterns)
        {
            if (text.Contains(pattern))
                return true;
        }
        return false;
    }
}

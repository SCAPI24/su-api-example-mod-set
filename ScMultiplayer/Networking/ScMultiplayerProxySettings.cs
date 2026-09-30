using Comms;
using Engine;
using System;
using System.IO;
using System.Text;

namespace ScMultiplayer
{
    /// <summary>
    /// 客户端侧 SOCKS5 代理配置（进程级）。
    ///
    /// 文件：`data:/ScMultiplayer.proxy.txt`（即游戏目录下的 `ScMultiplayer.proxy.txt`）。
    ///
    /// ⚠️ **默认直连，而且不存在时不再自动创建这个文件**（用户 2026-09-26 明确要求）：
    /// 旧的默认值 `enabled=auto` + `127.0.0.1:7890` 会在**代理没开**的时候仍然把数据报往
    /// 那个端口送，后果是"加入卡在连接中、主机侧一行 `Client joining` 都收不到"，
    /// 而且**远端服务器过一会儿也会连不上**（用户实测）。代理应当是"显式选择"，
    /// 不该是"默认猜"：想走代理的用户自己写 `enabled=on`（或 `auto`），其余一律直连。
    ///
    /// 只影响**客户端**：主机端不需要这个文件，也不需要任何代理设置（它只监听）。
    /// </summary>
    internal static class ScMultiplayerProxySettings
    {
        private const string ConfigPath = "data:/ScMultiplayer.proxy.txt";

        private static Socks5ProxySettings s_current;
        private static bool s_loaded;

        public static Socks5ProxySettings Current
        {
            get
            {
                EnsureLoaded();
                return s_current;
            }
        }

        public static void EnsureLoaded()
        {
            if (s_loaded)
                return;
            s_loaded = true;
            s_current = Load();
            Socks5ProxyState.Log = message => Log.Information(message);
            Socks5ProxyState.Configure(s_current);
            Log.Information("[ScMP] SOCKS5 proxy setting: " + Describe(s_current) + " (" +
                ConfigPath + ")");
        }

        /// <summary>把数据报通道包成"代理可用时走中继、否则直连"的双通道；代理关闭时原样返回。</summary>
        public static ITransmitter Wrap(UdpTransmitter datagram)
        {
            if (datagram == null)
                return null;
            Socks5ProxySettings settings = Current;
            return settings != null && settings.IsEnabled
                ? new ProxyDatagramTransmitter(datagram, settings)
                : (ITransmitter)datagram;
        }

        private static string Describe(Socks5ProxySettings settings)
        {
            if (settings == null || !settings.IsEnabled)
                return "off (direct only)";
            string mode = settings.Mode == Socks5ProxyMode.On ? "on" : "auto";
            return settings.Host + ":" + settings.Port + " (" + mode + ")";
        }

        // Source: Engine/Engine/Storage.cs:Storage.OpenFile
        private static Socks5ProxySettings Load()
        {
            try
            {
                if (!Storage.FileExists(ConfigPath))
                {
                    // 没有配置文件 = 直连，**不创建文件**（要代理的人自己写，见类注释）。
                    return Direct();
                }
                string text;
                using (Stream stream = Storage.OpenFile(ConfigPath, OpenFileMode.Read))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }
                return Socks5ProxySettings.Parse(text);
            }
            catch (Exception ex)
            {
                // 读坏了也退回直连：连不上主机是最难受的失败，代理只是可选项。
                Log.Warning("[ScMP] Failed to read " + ConfigPath + ": " + ex.Message +
                    ", falling back to direct connections");
                return Direct();
            }
        }

        /// <summary>直连（不启代理）。用解析器构造，避免依赖 Comms 侧是否提供工厂方法。</summary>
        private static Socks5ProxySettings Direct()
        {
            return Socks5ProxySettings.Parse("enabled=off");
        }
    }
}

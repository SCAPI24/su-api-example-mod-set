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
    /// 文件：`data:/ScMultiplayer.proxy.txt`（即游戏目录下的 `ScMultiplayer.proxy.txt`，不存在时按默认值创建）。
    /// 默认 `enabled=auto` + `127.0.0.1:7890`：本机 Clash / v2ray 一类代理开着就用、关掉就直连，
    /// 游戏途中来回切换都能连上（探测 + 数据报/流的双通道回退，见 Comms/Comms/Socks5Proxy.cs）。
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
                    WriteDefault();
                    return Socks5ProxySettings.Default();
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
                Log.Warning("[ScMP] Failed to read " + ConfigPath + ": " + ex.Message +
                    ", using the default SOCKS5 setting");
                return Socks5ProxySettings.Default();
            }
        }

        private static void WriteDefault()
        {
            try
            {
                using Stream stream = Storage.OpenFile(ConfigPath, OpenFileMode.Create);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(Socks5ProxySettings.Default().Format());
            }
            catch (Exception ex)
            {
                Log.Warning("[ScMP] Failed to create " + ConfigPath + ": " + ex.Message);
            }
        }
    }
}

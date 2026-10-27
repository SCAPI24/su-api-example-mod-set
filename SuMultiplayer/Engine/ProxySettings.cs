using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Engine;

namespace SuMultiplayer.Engine
{
    public enum ProxyMode
    {
        Default = 0,
        On = 1,
        Off = 2
    }

    /// <summary>
    /// 客户端 SOCKS5 代理设置（三态：Default/On/Off）。
    ///
    /// 存储位置：与引擎 Settings.xml 同目录的独立文件 <c>data:/SuMultiplayerProxy.xml</c>。
    /// 不写 Settings.xml：引擎 SettingsManager.SaveSettings() 会按自身静态属性重建整个文件，
    /// 自定义条目会在窗口失焦时被静默删除，不能作为持久化存储。
    ///
    /// 语义：
    ///   Default：自动比较直连/代理延迟；差距不大默认直连；
    ///   On：强制代理（局域网/私有地址除外）；
    ///   Off：强制直连。
    /// </summary>
    public static class ProxySettings
    {
        public const string FileName = "SuMultiplayerProxy.xml";
        private const string LegacyFilePath = "data:/ScMultiplayer.proxy.txt";
        private static readonly object Gate = new object();
        private static bool s_loaded;
        private static ProxyMode s_mode = ProxyMode.Default;
        private static string s_host = "127.0.0.1";
        private static int s_port = 7890;
        private static int s_autoTieThresholdMs = 25;
        private static string s_lastError = string.Empty;

        public static string SettingsPath => Storage.CombinePaths("data:/", FileName);

        public static ProxyMode Mode
        {
            get { lock (Gate) return s_mode; }
        }

        public static string Host
        {
            get { lock (Gate) return s_host; }
        }

        public static int Port
        {
            get { lock (Gate) return s_port; }
        }

        public static int AutoTieThresholdMs
        {
            get { lock (Gate) return s_autoTieThresholdMs; }
        }

        public static string LastError
        {
            get { lock (Gate) return s_lastError; }
        }

        public static string ModeName(ProxyMode mode) =>
            mode == ProxyMode.On ? "On" : mode == ProxyMode.Off ? "Off" : "Default";

        public static string Describe
        {
            get
            {
                lock (Gate)
                {
                    return ModeName(s_mode) + " · " + s_host + ":" +
                        s_port.ToString(CultureInfo.InvariantCulture) +
                        " · tie=" + s_autoTieThresholdMs + "ms";
                }
            }
        }

        /// <summary>读取 XML；不存在/损坏都回退默认，不抛异常。旧 .proxy.txt 只做一次性迁移读取。</summary>
        public static void Load(out string note)
        {
            lock (Gate)
            {
                if (s_loaded)
                {
                    note = "代理配置已读取：" + SettingsPath;
                    return;
                }
                s_loaded = true;
                s_mode = ProxyMode.Default;
                s_host = "127.0.0.1";
                s_port = 7890;
                s_autoTieThresholdMs = 25;
                s_lastError = string.Empty;
                try
                {
                    if (Storage.FileExists(SettingsPath))
                    {
                        using Stream stream = Storage.OpenFile(SettingsPath, OpenFileMode.Read);
                        XDocument document = XDocument.Load(stream);
                        foreach (XElement element in document.Root?.Elements() ?? Array.Empty<XElement>())
                        {
                            string name = (string)element.Attribute("Name") ?? string.Empty;
                            string value = (string)element.Attribute("Value") ?? string.Empty;
                            ApplyValueLocked(name, value);
                        }
                        note = "代理配置已读取：" + SettingsPath + "（" + DescribeLocked() + "）";
                        return;
                    }
                    if (Storage.FileExists(LegacyFilePath))
                    {
                        ApplyLegacyLocked();
                        SaveLocked();
                        note = "旧代理配置已迁移到 " + SettingsPath + "（" + DescribeLocked() + "）";
                        return;
                    }
                    note = "代理配置不存在，使用默认值（" + DescribeLocked() + "）：" + SettingsPath;
                }
                catch (Exception error)
                {
                    s_lastError = error.GetType().Name + " " + error.Message;
                    note = "代理配置读取失败，使用默认值（" + DescribeLocked() + "）：" + s_lastError;
                }
            }
        }

        public static bool TrySetMode(ProxyMode mode, out string error)
        {
            error = null;
            lock (Gate)
            {
                ProxyMode previous = s_mode;
                s_mode = mode;
                if (SaveLocked())
                    return true;
                s_mode = previous;
                error = s_lastError;
                return false;
            }
        }

        public static bool CycleMode(out ProxyMode mode, out string error)
        {
            lock (Gate)
            {
                ProxyMode next = s_mode == ProxyMode.Default ? ProxyMode.On :
                    s_mode == ProxyMode.On ? ProxyMode.Off : ProxyMode.Default;
                if (!TrySetModeLocked(next, out error))
                {
                    mode = s_mode;
                    return false;
                }
                mode = s_mode;
                return true;
            }
        }

        /// <summary>供 UI/运行时使用：不写文件，仅接受已经过校验的值（高级配置手改后刷新）。</summary>
        public static void ReloadFromDisk()
        {
            lock (Gate)
            {
                s_loaded = false;
                Load(out _);
            }
        }

        private static bool TrySetModeLocked(ProxyMode mode, out string error)
        {
            error = null;
            ProxyMode previous = s_mode;
            s_mode = mode;
            if (SaveLocked())
                return true;
            s_mode = previous;
            error = s_lastError;
            return false;
        }

        private static void ApplyLegacyLocked()
        {
            try
            {
                using Stream stream = Storage.OpenFile(LegacyFilePath, OpenFileMode.Read);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                foreach (string sourceLine in reader.ReadToEnd().Split(new[] { "\r\n", "\n", "\r" },
                    StringSplitOptions.None))
                {
                    string line = sourceLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") ||
                        line.StartsWith("//"))
                        continue;
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    ApplyValueLocked(line.Substring(0, separator).Trim(),
                        line.Substring(separator + 1).Trim().Trim('"'));
                }
            }
            catch (Exception error)
            {
                s_lastError = "旧代理配置迁移失败：" + error.GetType().Name + " " + error.Message;
            }
        }

        private static void ApplyValueLocked(string name, string value)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "mode":
                case "enabled":
                    string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
                    if (normalized == "on" || normalized == "true" || normalized == "always" ||
                        normalized == "1")
                        s_mode = ProxyMode.On;
                    else if (normalized == "off" || normalized == "false" ||
                        normalized == "direct" || normalized == "0")
                        s_mode = ProxyMode.Off;
                    else
                        s_mode = ProxyMode.Default;
                    break;
                case "host":
                case "server":
                    if (!string.IsNullOrWhiteSpace(value)) s_host = value.Trim();
                    break;
                case "port":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int port) && port > 0 && port <= 65535)
                        s_port = port;
                    break;
                case "autotiethresholdms":
                case "threshold":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int threshold) && threshold >= 0 && threshold <= 2000)
                        s_autoTieThresholdMs = threshold;
                    break;
            }
        }

        private static string DescribeLocked() =>
            ModeName(s_mode) + " · " + s_host + ":" +
            s_port.ToString(CultureInfo.InvariantCulture) +
            " · tie=" + s_autoTieThresholdMs + "ms";

        private static bool SaveLocked()
        {
            string temporary = SettingsPath + ".tmp";
            try
            {
                var root = new XElement("SuMultiplayerProxy",
                    new XElement("Setting", new XAttribute("Name", "Mode"),
                        new XAttribute("Value", ModeName(s_mode))),
                    new XElement("Setting", new XAttribute("Name", "Host"),
                        new XAttribute("Value", s_host)),
                    new XElement("Setting", new XAttribute("Name", "Port"),
                        new XAttribute("Value", s_port.ToString(CultureInfo.InvariantCulture))),
                    new XElement("Setting", new XAttribute("Name", "AutoTieThresholdMs"),
                        new XAttribute("Value", s_autoTieThresholdMs.ToString(CultureInfo.InvariantCulture))));
                using (Stream stream = Storage.OpenFile(temporary, OpenFileMode.Create))
                    new XDocument(root).Save(stream);
                Storage.MoveFile(temporary, SettingsPath);
                s_lastError = string.Empty;
                return true;
            }
            catch (Exception error)
            {
                s_lastError = error.GetType().Name + " " + error.Message;
                try
                {
                    if (Storage.FileExists(temporary)) Storage.DeleteFile(temporary);
                }
                catch (Exception)
                {
                }
                return false;
            }
        }
    }
}

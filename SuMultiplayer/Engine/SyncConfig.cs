using System;
using System.IO;
using System.Text;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 联机参数配置。两端各一份，放在游戏数据根目录：
    /// <c>data:/Mods/SuMultiplayer.json</c>（Windows: &lt;运行根&gt;\Mods\；
    /// Android: /sdcard/Download/Survivalcraft/Mods/）。
    ///
    /// **这里只有"参数"，没有"模式"**：旧实现把 host/client 写进配置，于是启动即联网，
    /// 那不是用户要的入口。新实现的入口只有两个，都由玩家操作触发 ——
    ///   · 游戏内 CR 按钮 → 房子建在**正在玩的那个世界**上；
    ///   · Play 屏点房间行 → 连到那个房间。
    /// 因此本文件不再有 mode/role 字段（写了也不认）。
    ///
    /// 刻意不用 System.Text.Json：mod 要同时跑在 Windows 与 Android，
    /// 一个扁平配置用 40 行解析足够，少一个依赖少一个坑。
    ///
    /// 演示页标记：CFG01。
    /// </summary>
    public sealed class SyncConfig
    {
        public int Port = 26760;
        public int Radius = 3;
        public bool VerboseLog = true;
        /// <summary>主机侧是否把收到的客户端位置当作兴趣中心（实机 MVP 里两端各跑一个世界，故允许）。</summary>
        public bool TrustClientCenter = true;

        // —— 房间（会话）相关 ——
        /// <summary>房间名（主机应答发现探测时广播的名字）。</summary>
        public string RoomName = "SuMP Room";
        /// <summary>LAN 发现端口（UDP 探测/应答）。</summary>
        public int DiscoveryPort = 26761;
        public int MaxPlayers = 8;

        public const string FileName = "SuMultiplayer.json";

        public static string SettingsPath => Storage.CombinePaths("data:/Mods", FileName);

        public string Describe() =>
            "port=" + Port + " radius=" + Radius + " room=\"" + RoomName + "\" discovery=" +
            DiscoveryPort + " maxplayers=" + MaxPlayers + " verbose=" + VerboseLog;

        /// <summary>
        /// 读配置；不存在就写一份模板。**读失败不静默兜底**：日志里给出可直接定位的原因，
        /// 然后按默认参数继续（默认参数不含任何"自动联网"开关，所以不会假装联机成功）。
        /// </summary>
        public static SyncConfig Load(out string note)
        {
            var config = new SyncConfig();
            string path = SettingsPath;
            try
            {
                if (!Storage.FileExists(path))
                {
                    config.WriteTemplate(path);
                    note = "配置不存在，已写入模板：" + path;
                    return config;
                }
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                {
                    string text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                    ApplyFlatJson(config, text);
                }
                note = "配置已读取：" + path;
            }
            catch (Exception e)
            {
                note = "配置读取失败（用默认参数继续）：" + e.GetType().Name + " " + e.Message + " @ " + path;
                Log.Error("[SuMP] " + note);
            }
            return config;
        }

        private void WriteTemplate(string path)
        {
            try
            {
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine("{");
                    writer.WriteLine("  // 这里只有参数：建房/加入都由游戏内操作触发（CR 按钮 / Play 屏房间行）");
                    writer.WriteLine("  \"port\": 26760,");
                    writer.WriteLine("  \"discoveryport\": 26761,");
                    writer.WriteLine("  \"room\": \"SuMP Room\",");
                    writer.WriteLine("  \"radius\": 3,");
                    writer.WriteLine("  \"maxplayers\": 8,");
                    writer.WriteLine("  \"verbose\": true");
                    writer.WriteLine("}");
                }
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 写配置模板失败：" + e.GetType().Name + " " + e.Message + " @ " + path);
            }
        }

        /// <summary>扁平 JSON 解析：只认 "key": value（字符串/数字/布尔），支持 // 注释。</summary>
        private static void ApplyFlatJson(SyncConfig config, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int index = 0;
            while (index < text.Length)
            {
                int keyStart = text.IndexOf('"', index);
                if (keyStart < 0) break;
                int keyEnd = text.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) break;
                string key = text.Substring(keyStart + 1, keyEnd - keyStart - 1);
                int colon = text.IndexOf(':', keyEnd + 1);
                if (colon < 0) break;
                int valueStart = colon + 1;
                while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart])) valueStart++;
                if (valueStart >= text.Length) break;
                int valueEnd = valueStart;
                string value;
                if (text[valueStart] == '"')
                {
                    valueEnd = text.IndexOf('"', valueStart + 1);
                    if (valueEnd < 0) break;
                    value = text.Substring(valueStart + 1, valueEnd - valueStart - 1);
                    valueEnd++;
                }
                else
                {
                    while (valueEnd < text.Length && text[valueEnd] != ',' && text[valueEnd] != '}' &&
                           text[valueEnd] != '\n' && text[valueEnd] != '\r')
                        valueEnd++;
                    value = text.Substring(valueStart, valueEnd - valueStart).Trim();
                }
                Apply(config, key, value);
                index = valueEnd;
            }
        }

        private static void Apply(SyncConfig config, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "mode":
                case "role":
                    // 旧配置里的模式字段：不再作为入口，明确警告一次，避免"改了配置却没变化"。
                    Log.Warning("[SuMP] 配置里的 \"" + key + "\" 已不再生效：建房走游戏内 CR 按钮，" +
                                "加入走 Play 屏房间行");
                    break;
                case "port":
                    if (int.TryParse(value, out int port) && port > 0 && port < 65536) config.Port = port;
                    break;
                case "radius":
                    if (int.TryParse(value, out int radius)) config.Radius = Math.Max(1, Math.Min(32, radius));
                    break;
                case "verbose":
                case "log":
                    config.VerboseLog = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";
                    break;
                case "room":
                case "roomname":
                    if (!string.IsNullOrWhiteSpace(value)) config.RoomName = value.Trim();
                    break;
                case "discoveryport":
                case "discoverport":
                    if (int.TryParse(value, out int discoveryPort) && discoveryPort > 0 && discoveryPort < 65536)
                        config.DiscoveryPort = discoveryPort;
                    break;
                case "maxplayers":
                    if (int.TryParse(value, out int maxPlayers)) config.MaxPlayers = Math.Max(1, maxPlayers);
                    break;
                case "trustclientcenter":
                    config.TrustClientCenter = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        /// <summary>把当前参数写回去（测试脚本用得到）。</summary>
        public void Save()
        {
            try
            {
                using (Stream stream = Storage.OpenFile(SettingsPath, OpenFileMode.Create))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine("{");
                    writer.WriteLine("  \"port\": " + Port + ",");
                    writer.WriteLine("  \"discoveryport\": " + DiscoveryPort + ",");
                    writer.WriteLine("  \"room\": \"" + RoomName + "\",");
                    writer.WriteLine("  \"radius\": " + Radius + ",");
                    writer.WriteLine("  \"maxplayers\": " + MaxPlayers + ",");
                    writer.WriteLine("  \"verbose\": " + (VerboseLog ? "true" : "false"));
                    writer.WriteLine("}");
                }
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 写配置失败：" + e.GetType().Name + " " + e.Message);
            }
        }
    }
}

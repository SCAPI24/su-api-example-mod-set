using System;
using System.Globalization;
using System.IO;
using System.Text;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// **世界作用域**的调试镜像配置（主机侧，放在世界自己的文件夹里）。
    ///
    /// 为什么不放全局 `data:/Mods/SuMultiplayer.json`（用户口径 2026-10-21）：
    ///   · 调试镜像（debug-mirror / mirror-character / mirror-host）只是"跟着一张地图"调试用，
    ///     换地图就不应该沿用上一张图的调试开关；
    ///   · 每张地图的所有联机数据都在自己的目录（角色记录 = SuMultiplayerPlayers.xml、
    ///     区域 = Regions/…），调试开关也放同一层，删世界 = 连调试开关一起删。
    ///
    /// 文件：<c>&lt;世界目录&gt;/SuMultiplayerDebug.json</c>（主机读；不存在 = 全部关闭）。
    /// 格式与主配置相同的"扁平 JSON"（<c>"key": value</c>，支持 // 注释）。
    /// 没有任何写盘路径 —— 这是调试开关，不自动生成模板（不往世界目录里添多余文件）。
    /// </summary>
    public sealed class DebugMirrorWorldSettings
    {
        /// <summary>世界目录里的文件名（与角色记录同层）。</summary>
        public const string FileName = "SuMultiplayerDebug.json";

        /// <summary>镜像宿主自动角色名前缀默认值。</summary>
        public const string DefaultNamePrefix = "Mirror";

        // —— 调试镜像（camera-only 第三分屏，不开真实角色）——
        /// <summary>调试镜像目标：空/false = 关闭；true = 第一个远端角色；&lt;clientId&gt;[#slot] = 指定。</summary>
        public string DebugMirrorClient = string.Empty;

        // —— 调试镜像角色（把已有主机本地角色 H3 变成客户端视角）——
        /// <summary>显式角色名（例 "H3"）；空 = 关闭。</summary>
        public string MirrorCharacter = string.Empty;
        public string MirrorCharacterClient = string.Empty;
        public string MirrorCharacterMode = string.Empty;

        // —— 镜像宿主（自动创建真实本地角色；默认关闭）——
        /// <summary>
        /// true = 客户端加入时主机自动创建真实本地角色（第 2/3/4 分屏）并绑到客户端视角。
        /// **默认关闭**（调试功能，世界文件里显式打开才生效）。
        /// </summary>
        public bool MirrorHostAuto;
        public string MirrorHostNamePrefix = DefaultNamePrefix;

        /// <summary>
        /// 调试：主机为每个加入的客户端自动开一个"客户端视角"分屏（camera-only，不同步角色实体）。
        /// **默认关闭** —— 它不是正常联机流程的一部分（用户口径 2026-10-21：新世界加入时主机不该
        /// 默认多出一块屏幕）。要调试时在这个世界的 SuMultiplayerDebug.json 里显式打开。
        /// </summary>
        public bool ClientView;

        /// <summary>自动客户端分屏只给哪些客户端开：all / auto / &lt;clientId&gt;（空 = all）。</summary>
        public string ClientViewClient = string.Empty;

        /// <summary>全部关闭（世界为空 / 没有世界调试文件时用）。</summary>
        public static DebugMirrorWorldSettings None => new DebugMirrorWorldSettings();

        /// <summary>是否有任何调试镜像开关打开（日志 / 状态行用）。</summary>
        public bool AnyEnabled =>
            !string.IsNullOrEmpty(DebugMirrorClient) ||
            !string.IsNullOrEmpty(MirrorCharacter) ||
            MirrorHostAuto;

        /// <summary>
        /// 从世界目录读调试配置。<paramref name="note"/> 永远给出可直接写日志的结论：
        /// 文件不存在 = "没有 SuMultiplayerDebug.json（调试镜像全关）"，不是错误。
        /// </summary>
        public static DebugMirrorWorldSettings Load(string worldDirectory, out string note)
        {
            var settings = new DebugMirrorWorldSettings();
            string path = string.IsNullOrEmpty(worldDirectory)
                ? string.Empty
                : Storage.CombinePaths(worldDirectory, FileName);
            if (path.Length == 0)
            {
                note = "没有世界目录 → 调试镜像全关";
                return settings;
            }
            try
            {
                if (!Storage.FileExists(path))
                {
                    note = "没有 " + FileName + "（调试镜像全关）：" + path;
                    return settings;
                }
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                {
                    string text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                    ApplyFlatJson(settings, text);
                }
                if (string.IsNullOrWhiteSpace(settings.MirrorHostNamePrefix))
                    settings.MirrorHostNamePrefix = DefaultNamePrefix;
                note = "世界调试配置已读取：" + path + " · " + settings.Describe();
                return settings;
            }
            catch (Exception e)
            {
                // 明确失败：保持全关并写清原因（绝不把坏配置当成"打开了"）。
                note = "世界调试配置读取失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 调试镜像全关：" + path;
                Log.Error("[SuMP] " + note);
                return new DebugMirrorWorldSettings();
            }
        }

        public string Describe() =>
            "debugMirror=" + (string.IsNullOrEmpty(DebugMirrorClient) ? "off" : DebugMirrorClient) +
            " mirrorCharacter=" + (string.IsNullOrEmpty(MirrorCharacter) ? "off" : MirrorCharacter) +
            " mirrorCharacterClient=" +
            (string.IsNullOrEmpty(MirrorCharacterClient) ? "auto" : MirrorCharacterClient) +
            " mirrorCharacterMode=" +
            (string.IsNullOrEmpty(MirrorCharacterMode) ? "replace" : MirrorCharacterMode) +
            " mirrorHostAuto=" + (MirrorHostAuto ? "on" : "off") +
            " mirrorHostNamePrefix=\"" + MirrorHostNamePrefix + "\"" +
            " clientView=" + (ClientView ? "on" : "off") +
            " clientViewClient=" + (string.IsNullOrEmpty(ClientViewClient) ? "all" : ClientViewClient);

        /// <summary>扁平 JSON 解析：只认 "key": value（字符串/数字/布尔），支持 // 注释。</summary>
        private static void ApplyFlatJson(DebugMirrorWorldSettings settings, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int index = 0;
            while (index < text.Length)
            {
                int keyStart = text.IndexOf('"', index);
                if (keyStart < 0) break;
                int keyEnd = text.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) break;
                string key = text.Substring(keyStart + 1, keyEnd - keyStart - 1).Trim().ToLowerInvariant();
                int colon = text.IndexOf(':', keyEnd + 1);
                if (colon < 0) break;
                int valueStart = colon + 1;
                while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart])) valueStart++;
                if (valueStart >= text.Length) break;
                string value;
                int next;
                if (text[valueStart] == '"')
                {
                    int valueEnd = text.IndexOf('"', valueStart + 1);
                    if (valueEnd < 0) break;
                    value = text.Substring(valueStart + 1, valueEnd - valueStart - 1);
                    next = valueEnd + 1;
                }
                else
                {
                    int valueEnd = valueStart;
                    while (valueEnd < text.Length && text[valueEnd] != ',' && text[valueEnd] != '\n' &&
                        text[valueEnd] != '\r') valueEnd++;
                    value = text.Substring(valueStart, valueEnd - valueStart).Trim();
                    next = valueEnd;
                }
                ApplyKey(settings, key, value);
                index = next;
            }
        }

        private static void ApplyKey(DebugMirrorWorldSettings settings, string key, string value)
        {
            switch (key)
            {
                case "debugmirrorclient":
                    settings.DebugMirrorClient = value;
                    break;
                case "mirrorcharacter":
                    settings.MirrorCharacter = value;
                    break;
                case "mirrorcharacterclient":
                    settings.MirrorCharacterClient = value;
                    break;
                case "mirrorcharactermode":
                    settings.MirrorCharacterMode = value;
                    break;
                case "mirrorcharacterauto":
                    settings.MirrorHostAuto = ParseBool(value, settings.MirrorHostAuto);
                    break;
                case "mirrorhostnameprefix":
                    settings.MirrorHostNamePrefix = string.IsNullOrWhiteSpace(value)
                        ? DefaultNamePrefix
                        : value.Trim();
                    break;
                case "clientview":
                    settings.ClientView = ParseBool(value, settings.ClientView);
                    break;
                case "clientviewclient":
                    settings.ClientViewClient = value == null ? string.Empty : value.Trim();
                    break;
                default:
                    // 未知键静默忽略（同一份文件可以带未来新增的调试键；首次读取会打一行 Describe）。
                    break;
            }
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            switch (value.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "on":
                case "yes":
                    return true;
                case "false":
                case "0":
                case "off":
                case "no":
                    return false;
                default:
                    if (bool.TryParse(value, out bool parsed)) return parsed;
                    Log.Warning("[SuMP] 世界调试配置的布尔值非法：" + value + "（按 " +
                        (fallback ? "开" : "关") + " 处理）");
                    return fallback;
            }
        }
    }
}

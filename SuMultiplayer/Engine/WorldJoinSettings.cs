using System;
using System.IO;
using System.Text;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 世界作用域的**联机行为设置**（用户口径 2026-10-21：所有跟"这一张地图"有关的配置都放世界文件夹）：
    /// <c>&lt;世界目录&gt;/SuMultiplayerWorld.json</c>。
    ///
    /// 本版只有一个键（其余保持默认）：
    ///   · <c>autoapprovejoins</c>（bool，**默认 false**）：是否为所有加入请求自动同意。
    ///     false = 每个玩家端加入都要主机点 Allow（旧联机 mod 的 join request 审批）；
    ///     true  = 直接放行（适合无头/挂机主机 —— 无头主机没界面可点 Allow，必须显式打开）。
    /// 不存在 / 读失败 = 手动审批（默认），并在日志里写清原因。
    /// </summary>
    public sealed class WorldJoinSettings
    {
        public const string FileName = "SuMultiplayerWorld.json";

        public bool AutoApproveJoins;

        public string Describe() => "autoApproveJoins=" + (AutoApproveJoins ? "on" : "off（手动审批）");

        /// <summary>读世界目录下的设置文件；不存在 = 全默认（手动审批），不自动创建文件。</summary>
        public static WorldJoinSettings Load(string worldDirectory, out string note)
        {
            var settings = new WorldJoinSettings();
            if (string.IsNullOrEmpty(worldDirectory))
            {
                note = "没有世界目录（尚未进世界）→ " + FileName + " 不生效，加入审批按默认：手动";
                return settings;
            }
            string path = Storage.CombinePaths(worldDirectory, FileName);
            try
            {
                if (!Storage.FileExists(path))
                {
                    note = "没有 " + FileName + "（默认：手动审批，每个加入都要主机 Allow）：" + path;
                    return settings;
                }
                string text;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                ApplyFlatJson(settings, text);
                note = "世界联机设置已读取：" + path + " · " + settings.Describe();
                return settings;
            }
            catch (Exception e)
            {
                // 明确失败：保持默认（手动审批）并写清原因；绝不因为读失败就自动同意加入。
                note = "世界联机设置读取失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 保持默认：手动审批：" + path;
                Log.Error("[SuMP] " + note);
                return new WorldJoinSettings();
            }
        }

        private static void ApplyFlatJson(WorldJoinSettings settings, string text)
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
                int valueEnd = valueStart;
                while (valueEnd < text.Length && text[valueEnd] != ',' && text[valueEnd] != '\n' &&
                    text[valueEnd] != '\r' && text[valueEnd] != '}') valueEnd++;
                string value = text.Substring(valueStart, valueEnd - valueStart).Trim().ToLowerInvariant();
                if (key == "autoapprovejoins")
                {
                    settings.AutoApproveJoins = value == "true" || value == "1" || value == "on" ||
                        value == "yes";
                }
                index = valueEnd;
            }
        }
    }
}

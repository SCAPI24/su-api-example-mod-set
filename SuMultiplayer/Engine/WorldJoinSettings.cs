using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 世界作用域的联机设置，放在**世界文件夹本身**：
    /// <c>&lt;Worlds/World&gt;/SuMultiplayerWorld.xml</c>（不是 Worlds 根目录）。
    /// worldDirectory 由 SubsystemGameInfo.DirectoryName 提供，已经是具体世界目录。
    ///
    /// 每个字段独立校验：缺项 / 类型错 / 越界只回退该项到默认值，并写清原因；
    /// **文件不存在时只使用默认值，绝不自动创建模板**（单机世界不需要给存档添文件；
    /// 只有玩家在 IF 里真的修改了联机设置、或 Ban 等操作时才显式 Save）。
    /// 旧版 SuMultiplayerWorld.json 只读取并迁移到内存，不自动落盘。
    /// </summary>
    public sealed class WorldJoinSettings
    {
        public const string FileName = "SuMultiplayerWorld.xml";
        public const string LegacyFileName = "SuMultiplayerWorld.json";
        public const int AutoPort = 0;
        public const int AutoPortBase = 51459;
        public const int AutoPortCount = 10;

        public bool AutoHostCurrentWorld;
        public bool AutoApproveJoins;
        /// <summary>0 = 自动分配（旧版 51459 起 10 个）；1..65535 = 指定端口。</summary>
        public int HostPort;
        public readonly List<string> BannedIdentities = new List<string>();

        public bool IsBanned(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) return false;
            string value = identity.Trim();
            for (int i = 0; i < BannedIdentities.Count; i++)
            {
                if (string.Equals(BannedIdentities[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public string Describe() =>
            "autoHost=" + (AutoHostCurrentWorld ? "on" : "off") +
            " autoApprove=" + (AutoApproveJoins ? "on" : "off") +
            " port=" + (HostPort == AutoPort
                ? "auto(" + AutoPortBase + ".." + (AutoPortBase + AutoPortCount - 1) + ")"
                : HostPort.ToString()) +
            " bans=" + BannedIdentities.Count;

        public static string PathFor(string worldDirectory) =>
            Storage.CombinePaths(worldDirectory, FileName);

        public static string LegacyPathFor(string worldDirectory) =>
            Storage.CombinePaths(worldDirectory, LegacyFileName);

        public static WorldJoinSettings Load(string worldDirectory, out string note)
        {
            var settings = new WorldJoinSettings();
            if (string.IsNullOrEmpty(worldDirectory))
            {
                note = "没有世界目录（尚未进世界）→ " + FileName + " 不生效，使用默认值";
                return settings;
            }
            string path = PathFor(worldDirectory);
            var problems = new List<string>();
            try
            {
                if (Storage.FileExists(path))
                {
                    string text;
                    using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                        text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                    ApplyXml(settings, text, problems);
                }
                else
                {
                    ApplyLegacyJsonIfPresent(settings, worldDirectory, problems);
                    problems.Add("文件不存在 → 使用默认值（不创建文件；单机不写存档）");
                }
            }
            catch (Exception e)
            {
                problems.Add("读取/创建失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 当前会话使用默认值");
            }
            note = "世界联机设置：" + path + " · " + settings.Describe() +
                (problems.Count == 0 ? string.Empty : " · " + string.Join("；", problems));
            return settings;
        }

        public bool Save(string worldDirectory, out string note)
        {
            note = string.Empty;
            if (string.IsNullOrEmpty(worldDirectory))
            {
                note = "没有世界目录，无法保存";
                return false;
            }
            string path = PathFor(worldDirectory);
            try
            {
                var root = new XElement("SuMultiplayerWorld",
                    new XElement("AutoHostCurrentWorld", AutoHostCurrentWorld),
                    new XElement("AutoApproveJoins", AutoApproveJoins),
                    new XElement("HostPort", HostPort));
                var bans = new XElement("Banned");
                for (int i = 0; i < BannedIdentities.Count; i++)
                    bans.Add(new XElement("User", new XAttribute("Id", BannedIdentities[i])));
                root.Add(bans);
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(new XDocument(root).ToString());
                note = "已写入 " + path + " · " + Describe();
                return true;
            }
            catch (Exception e)
            {
                note = "写入失败（" + e.GetType().Name + " " + e.Message + "）：" + path;
                return false;
            }
        }

        private static void ApplyXml(WorldJoinSettings settings, string text, List<string> problems)
        {
            XDocument document;
            try { document = XDocument.Parse(text); }
            catch (Exception e)
            {
                problems.Add("XML 解析失败（" + e.GetType().Name + " " + e.Message + "）→ 全部默认");
                return;
            }
            XElement root = document.Root;
            if (root == null || root.Name.LocalName != "SuMultiplayerWorld")
            {
                problems.Add("根节点不是 <SuMultiplayerWorld> → 全部默认");
                return;
            }
            ApplyBool(root, "AutoHostCurrentWorld", value => settings.AutoHostCurrentWorld = value, problems);
            ApplyBool(root, "AutoApproveJoins", value => settings.AutoApproveJoins = value, problems);
            XElement port = root.Element("HostPort");
            if (port == null)
            {
                problems.Add("缺少 HostPort → 使用自动分配");
            }
            else if (int.TryParse((port.Value ?? string.Empty).Trim(), out int portValue) &&
                     portValue >= 0 && portValue <= 65535)
            {
                settings.HostPort = portValue;
            }
            else
            {
                problems.Add("HostPort=\"" + port.Value + "\" 非法（0..65535）→ 使用自动分配");
            }
            settings.BannedIdentities.Clear();
            XElement bans = root.Element("Banned");
            if (bans != null)
            {
                foreach (XElement user in bans.Elements("User"))
                {
                    string id = ((string)user.Attribute("Id") ?? user.Value ?? string.Empty).Trim();
                    if (id.Length == 0)
                    {
                        problems.Add("Banned 中有空用户 ID → 已忽略");
                        continue;
                    }
                    if (!settings.BannedIdentities.Contains(id, StringComparer.OrdinalIgnoreCase))
                        settings.BannedIdentities.Add(id);
                }
            }
        }

        private static void ApplyBool(XElement root, string name, Action<bool> apply,
            List<string> problems)
        {
            XElement element = root.Element(name);
            if (element == null)
            {
                problems.Add("缺少 " + name + " → 使用默认值");
                return;
            }
            string value = (element.Value ?? string.Empty).Trim();
            if (bool.TryParse(value, out bool parsed)) apply(parsed);
            else problems.Add(name + "=\"" + value + "\" 不是 true/false → 使用默认值");
        }

        private static void ApplyLegacyJsonIfPresent(WorldJoinSettings settings,
            string worldDirectory, List<string> problems)
        {
            string path = LegacyPathFor(worldDirectory);
            try
            {
                if (!Storage.FileExists(path)) return;
                string text;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                int index = 0;
                while (index < text.Length)
                {
                    int keyStart = text.IndexOf('"', index);
                    if (keyStart < 0) break;
                    int keyEnd = text.IndexOf('"', keyStart + 1);
                    if (keyEnd < 0) break;
                    string key = text.Substring(keyStart + 1, keyEnd - keyStart - 1)
                        .Trim().ToLowerInvariant();
                    int colon = text.IndexOf(':', keyEnd + 1);
                    if (colon < 0) break;
                    int valueStart = colon + 1;
                    while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
                        valueStart++;
                    int valueEnd = valueStart;
                    while (valueEnd < text.Length && text[valueEnd] != ',' &&
                           text[valueEnd] != '\n' && text[valueEnd] != '\r' &&
                           text[valueEnd] != '}')
                        valueEnd++;
                    string value = text.Substring(valueStart, valueEnd - valueStart)
                        .Trim().ToLowerInvariant();
                    if (key == "autoapprovejoins")
                        settings.AutoApproveJoins = value == "true" || value == "1" ||
                            value == "on" || value == "yes";
                    index = valueEnd;
                }
                problems.Add("发现旧版 " + LegacyFileName + "（只迁移 autoApproveJoins）");
            }
            catch (Exception e)
            {
                problems.Add("旧版 " + LegacyFileName + " 读取失败（" + e.GetType().Name +
                    " " + e.Message + "）→ 忽略");
            }
        }
    }
}

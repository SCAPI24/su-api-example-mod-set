using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Engine;

namespace SuMultiplayer.Net
{
    public sealed class PersonalServerRecord
    {
        public string Address { get; }
        public string Name { get; }
        public PersonalServerRecord(string address, string name)
        {
            Address = address;
            Name = name;
        }
    }

    /// <summary>
    /// 个人服务器目录（链接添加，可删除）。文件与旧 Mod 分开：
    /// <c>data:/SuMultiplayerPersonalServers.json</c>；首次启动若存在
    /// <c>data:/ScMultiplayerPersonalServers.json</c> 则迁移。
    /// </summary>
    public static class PersonalServers
    {
        public const int MaximumRecords = 64;
        private const int MaximumNameLength = 50;
        private const string FilePath = "data:/SuMultiplayerPersonalServers.json";
        private const string LegacyPath = "data:/ScMultiplayerPersonalServers.json";
        private static readonly object Gate = new object();
        private static readonly List<PersonalServerRecord> s_records =
            new List<PersonalServerRecord>();
        private static bool s_loaded;
        private static int s_revision;

        public static event Action Changed;
        public static int Revision => s_revision;

        public static PersonalServerRecord[] Records
        {
            get
            {
                lock (Gate)
                {
                    EnsureLoadedLocked();
                    return s_records.ToArray();
                }
            }
        }

        public static void Load()
        {
            lock (Gate) EnsureLoadedLocked();
        }

        public static bool TryAdd(string address, string name, out string error)
        {
            error = null;
            if (!TryNormalizeAddress(address, out string normalized, out error)) return false;
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > MaximumNameLength ||
                name.Any(char.IsControl))
            {
                error = "显示名称必须为 1-" + MaximumNameLength + " 个可打印字符";
                return false;
            }
            lock (Gate)
            {
                EnsureLoadedLocked();
                s_records.RemoveAll(record => string.Equals(record.Address, normalized,
                    StringComparison.OrdinalIgnoreCase));
                if (s_records.Count >= MaximumRecords)
                {
                    error = "个人服务器最多 " + MaximumRecords + " 条";
                    return false;
                }
                s_records.Add(new PersonalServerRecord(normalized, name));
                if (!SaveLocked(out error)) return false;
                s_revision++;
            }
            Changed?.Invoke();
            return true;
        }

        public static bool Remove(string address, out string error)
        {
            error = null;
            if (!TryNormalizeAddress(address, out string normalized, out error)) return false;
            lock (Gate)
            {
                EnsureLoadedLocked();
                int removed = s_records.RemoveAll(record => string.Equals(record.Address,
                    normalized, StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                {
                    error = "没有找到个人服务器：" + normalized;
                    return false;
                }
                if (!SaveLocked(out error)) return false;
                s_revision++;
            }
            Changed?.Invoke();
            return true;
        }

        public static bool TryNormalizeAddress(string address, out string normalized,
            out string error)
        {
            normalized = string.Empty;
            error = null;
            string value = (address ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 1024)
            {
                error = "服务器地址为空或过长";
                return false;
            }
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
                value = uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
            // 校验 host[:port] / [v6]:port；不强制带端口，缺省用发现端口。
            string host;
            int port = 0;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closing = value.IndexOf(']');
                if (closing <= 1) { error = "IPv6 地址格式错误"; return false; }
                host = value.Substring(1, closing - 1);
                if (closing + 1 < value.Length)
                {
                    if (value[closing + 1] != ':' ||
                        !int.TryParse(value.Substring(closing + 2), out port))
                    { error = "端口格式错误"; return false; }
                }
            }
            else
            {
                int colon = value.LastIndexOf(':');
                int first = value.IndexOf(':');
                if (colon > 0 && colon == first && int.TryParse(value.Substring(colon + 1),
                        out int parsedPort))
                {
                    host = value.Substring(0, colon);
                    port = parsedPort;
                }
                else
                {
                    host = value;
                }
            }
            host = host.Trim();
            if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace) ||
                host.Any(char.IsControl))
            {
                error = "服务器主机名非法";
                return false;
            }
            if (!System.Net.IPAddress.TryParse(host, out _) &&
                Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                error = "服务器主机名非法：" + host;
                return false;
            }
            if (port < 0 || port > 65535) { error = "端口超出范围"; return false; }
            normalized = (host.Contains(":") ? "[" + host + "]" : host) +
                (port > 0 ? ":" + port : string.Empty);
            return true;
        }

        private static void EnsureLoadedLocked()
        {
            if (s_loaded) return;
            s_loaded = true;
            s_records.Clear();
            string path = Storage.FileExists(FilePath) ? FilePath :
                Storage.FileExists(LegacyPath) ? LegacyPath : null;
            if (path == null) return;
            try
            {
                using Stream stream = Storage.OpenFile(path, OpenFileMode.Read);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return;
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (s_records.Count >= MaximumRecords) break;
                    if (!item.TryGetProperty("address", out JsonElement addressElement) ||
                        !item.TryGetProperty("name", out JsonElement nameElement))
                        continue;
                    string address = addressElement.GetString();
                    string name = nameElement.GetString()?.Trim();
                    if (!TryNormalizeAddress(address, out string normalized, out _) ||
                        string.IsNullOrEmpty(name) || name.Length > MaximumNameLength)
                        continue;
                    if (s_records.Any(record => string.Equals(record.Address, normalized,
                            StringComparison.OrdinalIgnoreCase)))
                        continue;
                    s_records.Add(new PersonalServerRecord(normalized, name));
                }
                if (path == LegacyPath) SaveLocked(out _);
            }
            catch (Exception error)
            {
                global::Engine.Log.Warning("[SuMP][personal] 读取失败：" + error.Message);
                s_records.Clear();
            }
        }

        private static bool SaveLocked(out string error)
        {
            error = null;
            string temporary = FilePath + ".tmp";
            try
            {
                using (Stream stream = Storage.OpenFile(temporary, OpenFileMode.Create))
                using (var writer = new Utf8JsonWriter(stream,
                    new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartArray();
                    foreach (PersonalServerRecord record in s_records)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("address", record.Address);
                        writer.WriteString("name", record.Name);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.Flush();
                }
                Storage.MoveFile(temporary, FilePath);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + " " + exception.Message;
                try { if (Storage.FileExists(temporary)) Storage.DeleteFile(temporary); }
                catch (Exception) { }
                return false;
            }
        }
    }
}

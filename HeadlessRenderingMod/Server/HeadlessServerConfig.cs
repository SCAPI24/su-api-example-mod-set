using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeadlessRenderingMod
{
    internal sealed class HeadlessServerConfig
    {
        public bool Enabled { get; private set; } = true;

        public string InstanceId { get; private set; }

        public string BindAddress { get; private set; } = "127.0.0.1";

        public int Port { get; private set; }

        public string Token { get; private set; }

        public int TargetFrameRate { get; private set; } = 20;

        public bool HideWindow { get; private set; } = true;

        public bool DisableDrawing { get; private set; } = true;

        public bool EnableConsole { get; private set; } = true;

        public bool DisableAudio { get; private set; } = true;

        public int MaxQueuedCommands { get; private set; } = 256;

        public int MaxCommandsPerFrame { get; private set; } = 64;

        public int RequestTimeoutSeconds { get; private set; } = 10;

        public int MaxRequestBytes { get; private set; } = 65536;

        // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:
        // DataModificationEvents.ApprovalRequested ("sourceKey")
        // A headless server has nobody to click the host approval dialog. This list is mod agnostic:
        // a data modification request of ANY mod is approved automatically when the requesting
        // client's record key matches an entry. That key is the account **user id**
        // (UserManager.ActiveUser.UniqueId, the value stored in UserId.dat) and cannot be changed by
        // the player; for a client without an account identity ScMultiplayer uses "name:<player name>".
        // "*" matches every client. An empty list (default) disables auto approval entirely.
        public string[] AutoApproveDataModificationUserIds { get; private set; } =
            Array.Empty<string>();

        // server.json 的落点。控制台菜单改了白名单要**立即覆盖写回同一份文件**，
        // 所以配置对象必须记住自己是从哪个路径读出来的（由 CreateDefault 赋值，
        // LoadOrCreate 的两个分支都经由它构造）。
        private string m_configPath;

        // Source: Engine/Engine/Storage.cs:Storage.ProcessPath
        public static HeadlessServerConfig LoadOrCreate(string instanceRoot)
        {
            string configPath = Path.Combine(instanceRoot, "server.json");
            if (!File.Exists(configPath))
            {
                HeadlessServerConfig created = CreateDefault(instanceRoot);
                created.Save(configPath);
                return created;
            }

            string json = File.ReadAllText(configPath);
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("server.json root must be a JSON object.");

            HeadlessServerConfig config = CreateDefault(instanceRoot);
            JsonElement root = document.RootElement;
            config.Enabled = ReadBoolean(root, "enabled", config.Enabled);
            config.InstanceId = ReadString(root, "instanceId", config.InstanceId);
            config.BindAddress = ReadString(root, "bindAddress", config.BindAddress);
            config.Port = ReadInteger(root, "port", config.Port);
            config.Token = ReadString(root, "token", null);
            config.TargetFrameRate = ReadInteger(
                root,
                "targetFrameRate",
                config.TargetFrameRate);
            config.HideWindow = ReadBoolean(root, "hideWindow", config.HideWindow);
            config.DisableDrawing = ReadBoolean(
                root,
                "disableDrawing",
                config.DisableDrawing);
            config.EnableConsole = ReadBoolean(
                root,
                "enableConsole",
                config.EnableConsole);
            config.DisableAudio = ReadBoolean(
                root,
                "disableAudio",
                config.DisableAudio);
            config.MaxQueuedCommands = ReadInteger(
                root,
                "maxQueuedCommands",
                config.MaxQueuedCommands);
            config.MaxCommandsPerFrame = ReadInteger(
                root,
                "maxCommandsPerFrame",
                config.MaxCommandsPerFrame);
            config.RequestTimeoutSeconds = ReadInteger(
                root,
                "requestTimeoutSeconds",
                config.RequestTimeoutSeconds);
            config.MaxRequestBytes = ReadInteger(
                root,
                "maxRequestBytes",
                config.MaxRequestBytes);
            config.AutoApproveDataModificationUserIds = ReadStringArray(
                root,
                "autoApproveDataModificationUserIds",
                config.AutoApproveDataModificationUserIds);
            config.Validate();
            return config;
        }

        private static HeadlessServerConfig CreateDefault(string instanceRoot)
        {
            string trimmedRoot = Path.TrimEndingDirectorySeparator(instanceRoot);
            HeadlessServerConfig config = new HeadlessServerConfig
            {
                InstanceId = Path.GetFileName(trimmedRoot),
                Port = FindAvailablePort(26741, 100),
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            };
            // 记住落点：白名单的增删要覆盖写回这份 server.json。
            // LoadOrCreate 的两个分支（首次创建 / 已存在）都经由这里构造，
            // 路径与它自己拼的 configPath 完全一致。
            config.m_configPath = Path.Combine(instanceRoot, "server.json");
            config.Validate();
            return config;
        }

        private void Validate()
        {
            if (string.IsNullOrWhiteSpace(InstanceId) || InstanceId.Length > 128)
                throw new InvalidDataException("instanceId must contain 1-128 characters.");

            if (!IPAddress.TryParse(BindAddress, out IPAddress address) ||
                !IPAddress.IsLoopback(address))
            {
                throw new InvalidDataException(
                    "bindAddress must be a numeric loopback address.");
            }

            if (Port < 1 || Port > 65535)
                throw new InvalidDataException("port must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(Token) || Token.Length < 32 || Token.Length > 256)
                throw new InvalidDataException("token must contain 32-256 characters.");
            if (TargetFrameRate < 1 || TargetFrameRate > 240)
                throw new InvalidDataException("targetFrameRate must be between 1 and 240.");
            if (MaxQueuedCommands < 16 || MaxQueuedCommands > 4096)
                throw new InvalidDataException("maxQueuedCommands must be between 16 and 4096.");
            if (MaxCommandsPerFrame < 1 || MaxCommandsPerFrame > 256)
                throw new InvalidDataException("maxCommandsPerFrame must be between 1 and 256.");
            if (RequestTimeoutSeconds < 1 || RequestTimeoutSeconds > 120)
                throw new InvalidDataException("requestTimeoutSeconds must be between 1 and 120.");
            if (MaxRequestBytes < 1024 || MaxRequestBytes > 1048576)
                throw new InvalidDataException("maxRequestBytes must be between 1024 and 1048576.");
        }

        /// <summary>
        /// 覆盖式写回 server.json（文件已存在也能写）。失败**不抛异常**：调用方是控制台菜单，
        /// 写盘出错只该打印一行错误，不能把菜单打崩。
        /// </summary>
        public bool TrySave(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(m_configPath))
            {
                error = "the server.json path is unknown.";
                return false;
            }
            try
            {
                WriteTo(m_configPath, FileMode.Create);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 把一个身份加进 server.json 的 DM 自动同意白名单并立即写盘。
        /// 不变式与 <see cref="ReadStringArray"/> 一致：去空白、每条 1-128 字符、最多 64 条。
        /// 已经在名单里（大小写不敏感，与 IsAllowlisted 同口径）视为成功，且不重复写盘。
        /// </summary>
        public bool TryAddAutoApproveUserId(string identity, out string error)
        {
            error = null;
            string trimmed = identity?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                error = "the identity cannot be empty.";
                return false;
            }
            if (trimmed.Length > MaximumAutoApproveEntryLength)
            {
                error = "the identity must contain 1-" +
                    MaximumAutoApproveEntryLength + " characters.";
                return false;
            }

            string[] current = AutoApproveDataModificationUserIds ?? Array.Empty<string>();
            foreach (string entry in current)
            {
                if (string.Equals(entry, trimmed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (current.Length >= MaximumAutoApproveEntries)
            {
                error = "the allowlist already holds " + MaximumAutoApproveEntries + " entries.";
                return false;
            }

            var updated = new List<string>(current) { trimmed };
            string[] previous = AutoApproveDataModificationUserIds;
            AutoApproveDataModificationUserIds = updated.ToArray();
            if (!TrySave(out error))
            {
                // 写盘失败就回滚内存状态：控制台显示的和 server.json 必须一致。
                AutoApproveDataModificationUserIds = previous;
                return false;
            }
            return true;
        }

        /// <summary>从白名单移除一个身份（大小写不敏感）并立即写盘。</summary>
        public bool TryRemoveAutoApproveUserId(string identity)
        {
            return TryRemoveAutoApproveUserId(identity, out _);
        }

        /// <summary>同上，额外给出失败原因（不在名单里 / 写盘失败）。</summary>
        public bool TryRemoveAutoApproveUserId(string identity, out string error)
        {
            error = null;
            string trimmed = identity?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                error = "the identity cannot be empty.";
                return false;
            }

            string[] current = AutoApproveDataModificationUserIds ?? Array.Empty<string>();
            var updated = new List<string>(current.Length);
            bool found = false;
            foreach (string entry in current)
            {
                if (string.Equals(entry, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    continue;
                }
                updated.Add(entry);
            }
            if (!found)
            {
                error = "the identity is not in the allowlist.";
                return false;
            }

            string[] previous = AutoApproveDataModificationUserIds;
            AutoApproveDataModificationUserIds = updated.ToArray();
            if (!TrySave(out error))
            {
                AutoApproveDataModificationUserIds = previous;
                return false;
            }
            return true;
        }

        // 首次创建仍然只走这里：FileMode.CreateNew，文件已存在会抛 IOException，与原实现语义一致。
        private void Save(string configPath)
        {
            WriteTo(configPath, FileMode.CreateNew);
        }

        // 字段顺序与缩进跟原 Save 完全一致。用 Utf8JsonWriter 直写 FileStream，
        // **绝不会写出 UTF-8 BOM** —— 远端工具用 python json.load 读这份文件，BOM 会让它解析失败。
        private void WriteTo(string configPath, FileMode mode)
        {
            using FileStream stream = new FileStream(
                configPath,
                mode,
                FileAccess.Write,
                FileShare.Read);
            using Utf8JsonWriter writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WriteBoolean("enabled", Enabled);
            writer.WriteString("instanceId", InstanceId);
            writer.WriteString("bindAddress", BindAddress);
            writer.WriteNumber("port", Port);
            writer.WriteString("token", Token);
            writer.WriteNumber("targetFrameRate", TargetFrameRate);
            writer.WriteBoolean("hideWindow", HideWindow);
            writer.WriteBoolean("disableDrawing", DisableDrawing);
            writer.WriteBoolean("enableConsole", EnableConsole);
            writer.WriteBoolean("disableAudio", DisableAudio);
            writer.WriteNumber("maxQueuedCommands", MaxQueuedCommands);
            writer.WriteNumber("maxCommandsPerFrame", MaxCommandsPerFrame);
            writer.WriteNumber("requestTimeoutSeconds", RequestTimeoutSeconds);
            writer.WriteNumber("maxRequestBytes", MaxRequestBytes);
            WriteStringArray(writer, "autoApproveDataModificationUserIds",
                AutoApproveDataModificationUserIds);
            writer.WriteEndObject();
        }

        private const int MaximumAutoApproveEntries = 64;

        private const int MaximumAutoApproveEntryLength = 128;

        private static string[] ReadStringArray(JsonElement root, string name,
            string[] defaultValue)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
                return defaultValue;
            if (value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{name} must be an array of strings.");
            var entries = new List<string>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"{name} must contain only strings.");
                string text = item.GetString();
                if (string.IsNullOrWhiteSpace(text) ||
                    text.Length > MaximumAutoApproveEntryLength)
                {
                    throw new InvalidDataException(
                        $"{name} entries must contain 1-{MaximumAutoApproveEntryLength} characters.");
                }
                entries.Add(text.Trim());
                if (entries.Count > MaximumAutoApproveEntries)
                {
                    throw new InvalidDataException(
                        $"{name} must contain at most {MaximumAutoApproveEntries} entries.");
                }
            }
            return entries.ToArray();
        }

        private static void WriteStringArray(Utf8JsonWriter writer, string name, string[] values)
        {
            writer.WriteStartArray(name);
            if (values != null)
            {
                foreach (string value in values)
                    writer.WriteStringValue(value);
            }
            writer.WriteEndArray();
        }

        private static int FindAvailablePort(int firstPort, int attempts)
        {
            for (int port = firstPort; port < firstPort + attempts; port++)
            {
                TcpListener listener = null;
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    return port;
                }
                catch (SocketException)
                {
                }
                finally
                {
                    listener?.Stop();
                }
            }
            throw new IOException("No free headless control port was found.");
        }

        private static string ReadString(
            JsonElement root,
            string name,
            string defaultValue)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
                return defaultValue;
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"{name} must be a string.");
            return value.GetString();
        }

        private static int ReadInteger(JsonElement root, string name, int defaultValue)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
                return defaultValue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
                throw new InvalidDataException($"{name} must be an integer.");
            return result;
        }

        private static bool ReadBoolean(
            JsonElement root,
            string name,
            bool defaultValue)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
                return defaultValue;
            if (value.ValueKind != JsonValueKind.True &&
                value.ValueKind != JsonValueKind.False)
            {
                throw new InvalidDataException($"{name} must be a boolean.");
            }
            return value.GetBoolean();
        }
    }
}

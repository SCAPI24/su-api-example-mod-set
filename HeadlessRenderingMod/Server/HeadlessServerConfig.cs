using System;
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

        private void Save(string configPath)
        {
            using FileStream stream = new FileStream(
                configPath,
                FileMode.CreateNew,
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
            writer.WriteEndObject();
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

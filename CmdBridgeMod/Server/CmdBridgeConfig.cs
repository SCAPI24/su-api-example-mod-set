using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CmdBridgeMod
{
    /// <summary>
    /// CmdBridge.json 配置。首次运行自动生成随机 token 与可用端口。
    /// Source: Mod/HeadlessRenderingMod/Server/HeadlessServerConfig.cs（同形态，精简）
    /// </summary>
    internal sealed class CmdBridgeConfig
    {
        public bool Enabled { get; private set; } = true;

        public string InstanceId { get; private set; }

        public string BindAddress { get; private set; } = "127.0.0.1";

        public int Port { get; private set; }

        public string Token { get; private set; }

        public bool EnableInputInjection { get; private set; } = true;

        public int MaxCommandsPerFrame { get; private set; } = 8;

        public int MaxQueuedCommands { get; private set; } = 64;

        public int RequestTimeoutSeconds { get; private set; } = 15;

        public int MaxRequestBytes { get; private set; } = 262144;

        public int MaxElements { get; private set; } = 512;

        public int EventRingCapacity { get; private set; } = 256;

        private const string FileName = "CmdBridge.json";

        public static CmdBridgeConfig LoadOrCreate(string instanceRoot)
        {
            string path = Path.Combine(instanceRoot, FileName);
            CmdBridgeConfig config = CreateDefault(instanceRoot);
            if (!File.Exists(path))
            {
                config.Validate();
                config.Save(path);
                return config;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("CmdBridge.json root must be a JSON object.");

            JsonElement root = document.RootElement;
            config.Enabled = ReadBoolean(root, "enabled", config.Enabled);
            config.InstanceId = ReadString(root, "instanceId", config.InstanceId);
            config.BindAddress = ReadString(root, "bindAddress", config.BindAddress);
            config.Port = ReadInteger(root, "port", config.Port);
            config.Token = ReadString(root, "token", config.Token);
            config.EnableInputInjection = ReadBoolean(
                root, "enableInputInjection", config.EnableInputInjection);
            config.MaxCommandsPerFrame = ReadInteger(
                root, "maxCommandsPerFrame", config.MaxCommandsPerFrame);
            config.MaxQueuedCommands = ReadInteger(
                root, "maxQueuedCommands", config.MaxQueuedCommands);
            config.RequestTimeoutSeconds = ReadInteger(
                root, "requestTimeoutSeconds", config.RequestTimeoutSeconds);
            config.MaxRequestBytes = ReadInteger(
                root, "maxRequestBytes", config.MaxRequestBytes);
            config.MaxElements = ReadInteger(root, "maxElements", config.MaxElements);
            config.EventRingCapacity = ReadInteger(
                root, "eventRingCapacity", config.EventRingCapacity);
            config.Validate();
            return config;
        }

        private static CmdBridgeConfig CreateDefault(string instanceRoot)
        {
            string trimmed = Path.TrimEndingDirectorySeparator(instanceRoot);
            return new CmdBridgeConfig
            {
                InstanceId = Path.GetFileName(trimmed),
                Port = FindAvailablePort(26751, 64),
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24))
            };
        }

        private void Validate()
        {
            if (string.IsNullOrWhiteSpace(InstanceId) || InstanceId.Length > 128)
                throw new InvalidDataException("instanceId must contain 1-128 characters.");
            if (!IPAddress.TryParse(BindAddress, out IPAddress address) ||
                !IPAddress.IsLoopback(address))
            {
                throw new InvalidDataException("bindAddress must be a numeric loopback address.");
            }
            if (Port < 1 || Port > 65535)
                throw new InvalidDataException("port must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(Token) || Token.Length < 32 || Token.Length > 256)
                throw new InvalidDataException("token must contain 32-256 characters.");
            if (MaxCommandsPerFrame < 1 || MaxCommandsPerFrame > 256)
                throw new InvalidDataException("maxCommandsPerFrame must be between 1 and 256.");
            if (MaxQueuedCommands < 4 || MaxQueuedCommands > 4096)
                throw new InvalidDataException("maxQueuedCommands must be between 4 and 4096.");
            if (RequestTimeoutSeconds < 1 || RequestTimeoutSeconds > 120)
                throw new InvalidDataException("requestTimeoutSeconds must be between 1 and 120.");
            if (MaxRequestBytes < 1024 || MaxRequestBytes > 4194304)
                throw new InvalidDataException("maxRequestBytes must be between 1024 and 4194304.");
            if (MaxElements < 1 || MaxElements > 8192)
                throw new InvalidDataException("maxElements must be between 1 and 8192.");
            if (EventRingCapacity < 16 || EventRingCapacity > 8192)
                throw new InvalidDataException("eventRingCapacity must be between 16 and 8192.");
        }

        private void Save(string path)
        {
            var builder = new StringBuilder();
            builder.Append("{\n");
            builder.Append("  \"enabled\": ").Append(Enabled ? "true" : "false").Append(",\n");
            builder.Append("  \"instanceId\": ").Append(Quote(InstanceId)).Append(",\n");
            builder.Append("  \"bindAddress\": ").Append(Quote(BindAddress)).Append(",\n");
            builder.Append("  \"port\": ").Append(Port.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            builder.Append("  \"token\": ").Append(Quote(Token)).Append(",\n");
            builder.Append("  \"enableInputInjection\": ").Append(EnableInputInjection ? "true" : "false").Append(",\n");
            builder.Append("  \"maxCommandsPerFrame\": ").Append(MaxCommandsPerFrame).Append(",\n");
            builder.Append("  \"maxQueuedCommands\": ").Append(MaxQueuedCommands).Append(",\n");
            builder.Append("  \"requestTimeoutSeconds\": ").Append(RequestTimeoutSeconds).Append(",\n");
            builder.Append("  \"maxRequestBytes\": ").Append(MaxRequestBytes).Append(",\n");
            builder.Append("  \"maxElements\": ").Append(MaxElements).Append(",\n");
            builder.Append("  \"eventRingCapacity\": ").Append(EventRingCapacity).Append("\n");
            builder.Append("}\n");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
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
            throw new IOException("No free CmdBridge port was found.");
        }

        private static string ReadString(JsonElement root, string name, string fallback)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
                return fallback;
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(name + " must be a string.");
            return value.GetString();
        }

        private static int ReadInteger(JsonElement root, string name, int fallback)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
                return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
                throw new InvalidDataException(name + " must be an integer.");
            return result;
        }

        private static bool ReadBoolean(JsonElement root, string name, bool fallback)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
                return fallback;
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                throw new InvalidDataException(name + " must be a boolean.");
            return value.GetBoolean();
        }
    }

    /// <summary>
    /// 运行时发现文件：客户端靠它找到端口与 token，无需手工配置。
    /// </summary>
    internal static class CmdBridgeRuntime
    {
        private const string FileName = "CmdBridge.runtime.json";

        public static void Write(string instanceRoot, CmdBridgeConfig config, string screen)
        {
            string path = Path.Combine(instanceRoot, FileName);
            var builder = new StringBuilder();
            builder.Append("{\n");
            builder.Append("  \"modVersion\": \"1.0.0\",\n");
            builder.Append("  \"pid\": ").Append(Environment.ProcessId).Append(",\n");
            builder.Append("  \"port\": ").Append(config.Port).Append(",\n");
            builder.Append("  \"token\": \"").Append(config.Token).Append("\",\n");
            builder.Append("  \"bindAddress\": \"").Append(config.BindAddress).Append("\",\n");
            builder.Append("  \"inputInjection\": ").Append(config.EnableInputInjection ? "true" : "false").Append(",\n");
            builder.Append("  \"startedUtc\": \"").Append(DateTime.UtcNow.ToString("O")).Append("\",\n");
            builder.Append("  \"screen\": \"").Append(screen ?? string.Empty).Append("\"\n");
            builder.Append("}\n");

            string temp = path + ".tmp";
            File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        public static void Delete(string instanceRoot)
        {
            try
            {
                string path = Path.Combine(instanceRoot, FileName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}

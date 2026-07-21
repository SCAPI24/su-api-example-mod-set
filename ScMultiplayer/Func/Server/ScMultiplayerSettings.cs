using Engine;
using System;
using System.IO;
using System.Text.Json;

namespace ScMultiplayer
{
    internal static class ScMultiplayerSettings
    {
        private const string SettingsPath = "data:/ScMultiplayerSettings.json";
        private const int DefaultServerBasePort = 51459;
        private const int DefaultServerPortCount = 64;
        private const int MaximumServerPortCount = 256;

        public static bool AutoApproveJoinRequests { get; private set; }

        public static bool AutoCreateRoomFromCurrentWorld { get; private set; }

        public static int ServerBasePort { get; private set; }

        public static int ServerPortCount { get; private set; }

        public static int[] ServerPorts { get; private set; } = Array.Empty<int>();

        public static int[] ServerBindPorts { get; private set; } = Array.Empty<int>();

        public static int ServerPreferredPort { get; private set; }

        // Source: Survivalcraft/Game/SettingsManager.cs:SettingsManager.LoadSettings
        public static void Load()
        {
            AutoApproveJoinRequests = false;
            AutoCreateRoomFromCurrentWorld = false;
            ServerBasePort = DefaultServerBasePort;
            ServerPortCount = DefaultServerPortCount;
            ServerPreferredPort = DefaultServerBasePort;
            if (!Storage.FileExists(SettingsPath))
            {
                BuildServerPorts();
                return;
            }

            try
            {
                using Stream stream = Storage.OpenFile(SettingsPath, OpenFileMode.Read);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty(
                    "autoApproveJoinRequests",
                    out JsonElement value) &&
                    (value.ValueKind == JsonValueKind.True ||
                    value.ValueKind == JsonValueKind.False))
                {
                    AutoApproveJoinRequests = value.GetBoolean();
                }
                if (document.RootElement.TryGetProperty(
                    "autoCreateRoomFromCurrentWorld",
                    out JsonElement autoCreateValue) &&
                    (autoCreateValue.ValueKind == JsonValueKind.True ||
                    autoCreateValue.ValueKind == JsonValueKind.False))
                {
                    AutoCreateRoomFromCurrentWorld = autoCreateValue.GetBoolean();
                }
                if (document.RootElement.TryGetProperty(
                    "serverBasePort",
                    out JsonElement basePortValue) &&
                    basePortValue.TryGetInt32(out int basePort))
                {
                    ServerBasePort = basePort;
                }
                if (document.RootElement.TryGetProperty(
                    "serverPortCount",
                    out JsonElement portCountValue) &&
                    portCountValue.TryGetInt32(out int portCount))
                {
                    ServerPortCount = portCount;
                }
                if (document.RootElement.TryGetProperty(
                    "serverPreferredPort",
                    out JsonElement preferredPortValue) &&
                    preferredPortValue.TryGetInt32(out int preferredPort))
                {
                    ServerPreferredPort = preferredPort;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[ScMP] Could not load multiplayer settings: " + ex.Message);
            }
            ValidateServerPorts();
            BuildServerPorts();
        }

        // Source: Survivalcraft/Game/SettingsManager.cs:SettingsManager.SaveSettings
        public static void SetAutoApproveJoinRequests(bool value)
        {
            AutoApproveJoinRequests = value;
            Save();
        }

        public static void SetAutoCreateRoomFromCurrentWorld(bool value)
        {
            AutoCreateRoomFromCurrentWorld = value;
            Save();
        }

        private static void Save()
        {
            using Stream stream = Storage.OpenFile(SettingsPath, OpenFileMode.Create);
            using Utf8JsonWriter writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteBoolean("autoApproveJoinRequests", AutoApproveJoinRequests);
            writer.WriteBoolean(
                "autoCreateRoomFromCurrentWorld",
                AutoCreateRoomFromCurrentWorld);
            writer.WriteNumber("serverBasePort", ServerBasePort);
            writer.WriteNumber("serverPortCount", ServerPortCount);
            writer.WriteNumber("serverPreferredPort", ServerPreferredPort);
            writer.WriteEndObject();
        }

        // Source: Mod/Comms/Comms/UdpTransmitter.cs:UdpTransmitter.UdpTransmitter
        private static void ValidateServerPorts()
        {
            if (ServerBasePort < 1 || ServerBasePort > 65535)
            {
                Log.Warning($"[ScMP] Invalid serverBasePort {ServerBasePort}; using {DefaultServerBasePort}.");
                ServerBasePort = DefaultServerBasePort;
            }
            int maximumCount = Math.Min(MaximumServerPortCount, 65536 - ServerBasePort);
            if (ServerPortCount < 1 || ServerPortCount > maximumCount)
            {
                int fallback = Math.Min(DefaultServerPortCount, maximumCount);
                Log.Warning($"[ScMP] Invalid serverPortCount {ServerPortCount}; using {fallback}.");
                ServerPortCount = fallback;
            }
            if (ServerPreferredPort < ServerBasePort ||
                ServerPreferredPort >= ServerBasePort + ServerPortCount)
            {
                Log.Warning($"[ScMP] Invalid serverPreferredPort {ServerPreferredPort}; " +
                    $"using {ServerBasePort}.");
                ServerPreferredPort = ServerBasePort;
            }
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:BindFirstAvailableServerPort
        private static void BuildServerPorts()
        {
            ServerPorts = new int[ServerPortCount];
            for (int i = 0; i < ServerPorts.Length; i++)
                ServerPorts[i] = ServerBasePort + i;

            ServerBindPorts = new int[ServerPortCount];
            ServerBindPorts[0] = ServerPreferredPort;
            int bindIndex = 1;
            foreach (int port in ServerPorts)
            {
                if (port != ServerPreferredPort)
                    ServerBindPorts[bindIndex++] = port;
            }
        }
    }
}

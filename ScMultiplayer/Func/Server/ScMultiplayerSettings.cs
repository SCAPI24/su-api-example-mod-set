using Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScMultiplayer
{
    internal enum BandwidthLimitMode
    {
        SharedTotal,
        SeparateDirections
    }

    internal static class ScMultiplayerSettings
    {
        private const string SettingsPath = "data:/ScMultiplayerSettings.json";
        private const int DefaultServerBasePort = 51459;
        private const int DefaultServerPortCount = 64;
        private const int MaximumServerPortCount = 256;
        private const int DefaultMaxPlayers = 4;
        private const int MaximumMaxPlayers = 32;
        private const int MaximumBandwidthKbps = 1024 * 1024;
        private const int MaximumBurstKiB = 1024;

        public static bool AutoApproveJoinRequests { get; private set; }

        public static bool AutoCreateRoomFromCurrentWorld { get; private set; }

        public static int ServerBasePort { get; private set; }

        public static int ServerPortCount { get; private set; }

        public static int[] ServerPorts { get; private set; } = Array.Empty<int>();

        public static int[] ServerBindPorts { get; private set; } = Array.Empty<int>();

        public static int ServerPreferredPort { get; private set; }

        public static int MaxPlayers { get; private set; }

        // 0 keeps the legacy unrestricted join-transfer behavior. Hosts with a capped uplink
        // should set this below their measured physical upload rate.
        public static int ServerUploadLimitKbps { get; private set; }

        // Source: ScMultiplayerSettings.ServerUploadLimitKbps
        // A shared-cap provider charges this application for both directions together. This is
        // already the administrator's safe value and is never reduced automatically.
        public static BandwidthLimitMode BandwidthMode { get; private set; }

        // Automatic keeps the saved limits available for the next constrained network. It only
        // disables their use; it never deletes or rewrites them.
        public static bool BandwidthConfigurationEnabled { get; private set; }

        public static int SharedTotalSafeCapKbps { get; private set; }

        // The receive value is retained for administrators of asymmetric links. It is telemetry
        // only because a UDP server cannot safely throttle packets that have already arrived.
        public static int ServerDownloadLimitKbps { get; private set; }

        // 0 means the adaptive spare-budget calculation is the only join-transfer cap.
        public static int JoinTransferMaxKbps { get; private set; }

        public static int JoinTransferGameplayHeadroomKbps { get; private set; } = 96;

        public static int JoinTransferBurstKiB { get; private set; } = 16;

        // 0 means active joiners share the global join-transfer budget fairly.
        public static int JoinTransferPerJoinMaxKbps { get; private set; }

        public static int EffectiveJoinBandwidthCapKbps =>
            !BandwidthConfigurationEnabled ? 0 :
            BandwidthMode == BandwidthLimitMode.SharedTotal
                ? SharedTotalSafeCapKbps
                : ServerUploadLimitKbps;

        // Source: Survivalcraft/Game/SettingsManager.cs:SettingsManager.LoadSettings
        public static void Load()
        {
            AutoApproveJoinRequests = false;
            AutoCreateRoomFromCurrentWorld = false;
            ServerBasePort = DefaultServerBasePort;
            ServerPortCount = DefaultServerPortCount;
            ServerPreferredPort = DefaultServerBasePort;
            MaxPlayers = DefaultMaxPlayers;
            BandwidthMode = BandwidthLimitMode.SharedTotal;
            BandwidthConfigurationEnabled = false;
            SharedTotalSafeCapKbps = 0;
            ServerUploadLimitKbps = 0;
            ServerDownloadLimitKbps = 0;
            JoinTransferMaxKbps = 0;
            JoinTransferGameplayHeadroomKbps = 96;
            JoinTransferBurstKiB = 16;
            JoinTransferPerJoinMaxKbps = 0;
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
                if (document.RootElement.TryGetProperty(
                    "maxPlayers",
                    out JsonElement maxPlayersValue) &&
                    maxPlayersValue.TryGetInt32(out int maxPlayers))
                {
                    MaxPlayers = maxPlayers;
                }
                bool hasBandwidthMode = document.RootElement.TryGetProperty(
                    "bandwidthMode", out JsonElement modeValue) &&
                    modeValue.ValueKind == JsonValueKind.String;
                bool hasBandwidthConfigurationEnabled = document.RootElement.TryGetProperty(
                    "bandwidthConfigurationEnabled", out JsonElement enabledValue) &&
                    (enabledValue.ValueKind == JsonValueKind.True ||
                    enabledValue.ValueKind == JsonValueKind.False);
                if (hasBandwidthConfigurationEnabled)
                    BandwidthConfigurationEnabled = enabledValue.GetBoolean();
                if (hasBandwidthMode)
                {
                    BandwidthMode = string.Equals(modeValue.GetString(), "separate",
                        StringComparison.OrdinalIgnoreCase)
                        ? BandwidthLimitMode.SeparateDirections
                        : BandwidthLimitMode.SharedTotal;
                }
                SharedTotalSafeCapKbps = ReadNonNegativeInteger(document.RootElement,
                    "sharedTotalSafeCapKbps", SharedTotalSafeCapKbps);
                ServerUploadLimitKbps = ReadNonNegativeInteger(document.RootElement,
                    "serverUploadLimitKbps", ServerUploadLimitKbps);
                ServerDownloadLimitKbps = ReadNonNegativeInteger(document.RootElement,
                    "serverDownloadLimitKbps", ServerDownloadLimitKbps);
                // Existing installations only had an upload cap. Preserve that behavior until an
                // administrator explicitly selects one of the new simple setup modes.
                if (!hasBandwidthMode && ServerUploadLimitKbps > 0)
                    BandwidthMode = BandwidthLimitMode.SeparateDirections;
                JoinTransferMaxKbps = ReadNonNegativeInteger(document.RootElement,
                    "joinTransferMaxKbps", JoinTransferMaxKbps);
                JoinTransferGameplayHeadroomKbps = ReadNonNegativeInteger(document.RootElement,
                    "joinTransferGameplayHeadroomKbps", JoinTransferGameplayHeadroomKbps);
                JoinTransferBurstKiB = ReadNonNegativeInteger(document.RootElement,
                    "joinTransferBurstKiB", JoinTransferBurstKiB);
                JoinTransferPerJoinMaxKbps = ReadNonNegativeInteger(document.RootElement,
                    "joinTransferPerJoinMaxKbps", JoinTransferPerJoinMaxKbps);
                if (!hasBandwidthConfigurationEnabled)
                {
                    // Migrate only an installation that had an explicit limiting value. A
                    // previous file containing only defaults remains Automatic.
                    BandwidthConfigurationEnabled = SharedTotalSafeCapKbps > 0 ||
                        ServerUploadLimitKbps > 0 || JoinTransferMaxKbps > 0 ||
                        JoinTransferPerJoinMaxKbps > 0;
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

        public static void SetBandwidthConfigurationEnabled(bool value)
        {
            BandwidthConfigurationEnabled = value;
            Save();
        }

        public static Dictionary<string, object> GetJoinTransferSettings()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["autoApproveJoinRequests"] = AutoApproveJoinRequests,
                ["autoCreateRoomFromCurrentWorld"] = AutoCreateRoomFromCurrentWorld,
                ["bandwidthConfigurationEnabled"] = BandwidthConfigurationEnabled,
                ["bandwidthMode"] = BandwidthMode == BandwidthLimitMode.SharedTotal
                    ? "shared" : "separate",
                ["sharedTotalSafeCapKbps"] = SharedTotalSafeCapKbps,
                ["serverUploadLimitKbps"] = ServerUploadLimitKbps,
                ["serverDownloadLimitKbps"] = ServerDownloadLimitKbps,
                ["joinTransferMaxKbps"] = JoinTransferMaxKbps,
                ["joinTransferGameplayHeadroomKbps"] = JoinTransferGameplayHeadroomKbps,
                ["joinTransferBurstKiB"] = JoinTransferBurstKiB,
                ["joinTransferPerJoinMaxKbps"] = JoinTransferPerJoinMaxKbps
            };
        }

        public static void UpdateJoinTransferSettings(IDictionary<string, object> values)
        {
            if (values == null)
                return;

            AutoApproveJoinRequests = UpdateBoolean(values,
                "autoApproveJoinRequests", AutoApproveJoinRequests);
            AutoCreateRoomFromCurrentWorld = UpdateBoolean(values,
                "autoCreateRoomFromCurrentWorld", AutoCreateRoomFromCurrentWorld);
            if (values.TryGetValue("bandwidthConfigurationEnabled", out object enabled) &&
                enabled != null)
            {
                try
                {
                    BandwidthConfigurationEnabled = Convert.ToBoolean(
                        enabled, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    throw new ArgumentException(
                        "bandwidthConfigurationEnabled must be boolean.",
                        "bandwidthConfigurationEnabled");
                }
            }
            if (values.TryGetValue("bandwidthMode", out object mode) && mode is string text)
            {
                if (string.Equals(text, "shared", StringComparison.OrdinalIgnoreCase))
                    BandwidthMode = BandwidthLimitMode.SharedTotal;
                else if (string.Equals(text, "separate", StringComparison.OrdinalIgnoreCase))
                    BandwidthMode = BandwidthLimitMode.SeparateDirections;
                else
                    throw new ArgumentException("bandwidthMode must be shared or separate.",
                        "bandwidthMode");
            }
            SharedTotalSafeCapKbps = UpdateNonNegativeInteger(values,
                "sharedTotalSafeCapKbps", SharedTotalSafeCapKbps, MaximumBandwidthKbps);
            ServerUploadLimitKbps = UpdateNonNegativeInteger(values,
                "serverUploadLimitKbps", ServerUploadLimitKbps, MaximumBandwidthKbps);
            ServerDownloadLimitKbps = UpdateNonNegativeInteger(values,
                "serverDownloadLimitKbps", ServerDownloadLimitKbps, MaximumBandwidthKbps);
            JoinTransferMaxKbps = UpdateNonNegativeInteger(values,
                "joinTransferMaxKbps", JoinTransferMaxKbps, MaximumBandwidthKbps);
            JoinTransferGameplayHeadroomKbps = UpdateNonNegativeInteger(values,
                "joinTransferGameplayHeadroomKbps", JoinTransferGameplayHeadroomKbps,
                MaximumBandwidthKbps);
            JoinTransferBurstKiB = UpdateNonNegativeInteger(values,
                "joinTransferBurstKiB", JoinTransferBurstKiB, MaximumBurstKiB);
            JoinTransferPerJoinMaxKbps = UpdateNonNegativeInteger(values,
                "joinTransferPerJoinMaxKbps", JoinTransferPerJoinMaxKbps,
                MaximumBandwidthKbps);
            Save();
        }

        private static bool UpdateBoolean(IDictionary<string, object> values,
            string name, bool target)
        {
            if (!values.TryGetValue(name, out object item) || item == null)
                return target;
            try
            {
                return Convert.ToBoolean(item, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new ArgumentException(name + " must be boolean.", name);
            }
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
            writer.WriteNumber("maxPlayers", MaxPlayers);
            writer.WriteBoolean("bandwidthConfigurationEnabled", BandwidthConfigurationEnabled);
            writer.WriteString("bandwidthMode", BandwidthMode == BandwidthLimitMode.SharedTotal
                ? "shared" : "separate");
            writer.WriteNumber("sharedTotalSafeCapKbps", SharedTotalSafeCapKbps);
            writer.WriteNumber("serverUploadLimitKbps", ServerUploadLimitKbps);
            writer.WriteNumber("serverDownloadLimitKbps", ServerDownloadLimitKbps);
            writer.WriteNumber("joinTransferMaxKbps", JoinTransferMaxKbps);
            writer.WriteNumber("joinTransferGameplayHeadroomKbps",
                JoinTransferGameplayHeadroomKbps);
            writer.WriteNumber("joinTransferBurstKiB", JoinTransferBurstKiB);
            writer.WriteNumber("joinTransferPerJoinMaxKbps",
                JoinTransferPerJoinMaxKbps);
            writer.WriteEndObject();
        }

        private static int ReadNonNegativeInteger(JsonElement root, string name, int value)
        {
            if (root.TryGetProperty(name, out JsonElement item) && item.TryGetInt32(out int parsed))
                return parsed;
            return value;
        }

        private static int UpdateNonNegativeInteger(IDictionary<string, object> values,
            string name, int target, int maximum)
        {
            if (!values.TryGetValue(name, out object item) || item == null)
                return target;
            int parsed;
            try
            {
                parsed = Convert.ToInt32(item, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new ArgumentException(name + " must be an integer.", name);
            }
            if (parsed < 0 || parsed > maximum)
                throw new ArgumentOutOfRangeException(name,
                    name + " must be between 0 and " + maximum + ".");
            return parsed;
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
            if (MaxPlayers < 1 || MaxPlayers > MaximumMaxPlayers)
            {
                Log.Warning($"[ScMP] Invalid maxPlayers {MaxPlayers}; using " +
                    $"{DefaultMaxPlayers}.");
                MaxPlayers = DefaultMaxPlayers;
            }
            ServerUploadLimitKbps = MathUtils.Clamp(ServerUploadLimitKbps, 0,
                MaximumBandwidthKbps);
            SharedTotalSafeCapKbps = MathUtils.Clamp(SharedTotalSafeCapKbps, 0,
                MaximumBandwidthKbps);
            ServerDownloadLimitKbps = MathUtils.Clamp(ServerDownloadLimitKbps, 0,
                MaximumBandwidthKbps);
            JoinTransferMaxKbps = MathUtils.Clamp(JoinTransferMaxKbps, 0,
                MaximumBandwidthKbps);
            JoinTransferGameplayHeadroomKbps = MathUtils.Clamp(
                JoinTransferGameplayHeadroomKbps, 0, MaximumBandwidthKbps);
            JoinTransferBurstKiB = MathUtils.Clamp(JoinTransferBurstKiB, 0,
                MaximumBurstKiB);
            JoinTransferPerJoinMaxKbps = MathUtils.Clamp(
                JoinTransferPerJoinMaxKbps, 0, MaximumBandwidthKbps);
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

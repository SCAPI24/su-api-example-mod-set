using System;

namespace ScMultiplayer
{
    // Source: ScMultiplayerProfileHandlers.GetPlayerRecordKey
    // Source: ScMultiplayerProfileHandlers.GetNetworkRecordKey
    // Pure key formatting only; player records and network state remain owned by the caller.
    internal static class PlayerRecordKeyResolver
    {
        public static string GetPlayerRecordKey(string identity, string fallbackName)
        {
            return !string.IsNullOrWhiteSpace(identity)
                ? identity.Trim()
                : "name:" + (fallbackName ?? string.Empty).Trim();
        }

        public static string GetNetworkRecordKey(int clientId) => "network:" + clientId;
    }
}

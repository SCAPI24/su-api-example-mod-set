using System;
using System.Collections.Generic;

namespace ScMultiplayer.Core
{
    // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs
    // Owns session-scoped skin hashes and transfer buffers. Texture lifetime remains with the
    // world-session adapter and is released before Reset through DetachWorldSessionAssets.
    internal sealed class SessionAssetRegistry
    {
        public Dictionary<string, global::ScMultiplayer.SkinSessionAsset> SkinAssets { get; } =
            new Dictionary<string, global::ScMultiplayer.SkinSessionAsset>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, string> PlayerSkinHashes { get; } = new Dictionary<int, string>();
        public HashSet<string> RequestedSkinAssetKeys { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, global::ScMultiplayer.SkinAssetTransfer> IncomingSkinAssetTransfers { get; } =
            new Dictionary<string, global::ScMultiplayer.SkinAssetTransfer>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SentLocalSkinAssetHashes { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public global::ScMultiplayer.NetworkWorldSessionAssets WorldSessionAssets { get; set; }

        public global::ScMultiplayer.NetworkWorldSessionAssets DetachWorldSessionAssets()
        {
            global::ScMultiplayer.NetworkWorldSessionAssets assets = WorldSessionAssets;
            WorldSessionAssets = null;
            return assets;
        }

        public void Reset()
        {
            SkinAssets.Clear();
            PlayerSkinHashes.Clear();
            RequestedSkinAssetKeys.Clear();
            IncomingSkinAssetTransfers.Clear();
            SentLocalSkinAssetHashes.Clear();
            WorldSessionAssets = null;
        }
    }
}

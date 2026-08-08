using System;
using System.Linq;

namespace ScMultiplayer
{
    // Source: Mod/ScMultiplayer/Modules/Join/ScMultiplayerWorldTransferHandlers.cs:
    // NormalizeNetworkWorldZipPath, ImportNetworkWorldEmbeddedAsset
    // Archive path and embedded asset limits are pure policy decisions; extraction and
    // resource lifetime remain in the existing world-transfer adapter.
    internal static class WorldTransferPathPolicy
    {
        public static bool TryNormalizeZipPath(string filename, out string normalized)
        {
            string text = (filename ?? string.Empty).Replace('\\', '/').Trim();
            if (text.Length == 0 || text.EndsWith("/", StringComparison.Ordinal) ||
                text.StartsWith("/", StringComparison.Ordinal) ||
                text.Split('/').Any(part => part == ".."))
            {
                normalized = string.Empty;
                return false;
            }
            normalized = text;
            return true;
        }

        public static int GetEmbeddedAssetLimit(string extension)
        {
            if (string.Equals(extension, ".scskin", StringComparison.Ordinal))
                return 524288;
            if (string.Equals(extension, ".scbtex", StringComparison.Ordinal))
                return 4194304;
            return 0;
        }
    }
}

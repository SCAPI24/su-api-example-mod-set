using System;
using System.Linq;

namespace ScMultiplayer
{
    // Source: ScMultiplayerProfileHandlers skin hash helpers
    // Pure byte/hash representation only; file and texture validation remain in the caller.
    internal static class SkinHashCodec
    {
        public static byte[] CloneBytes(byte[] bytes) =>
            bytes == null || bytes.Length == 0 ? Array.Empty<byte>() : (byte[])bytes.Clone();

        public static bool IsValid(byte[] hash) =>
            hash != null && hash.Length == 32 && hash.Any(value => value != 0);

        public static string Format(byte[] hash) =>
            IsValid(hash) ? Convert.ToHexString(hash).ToLowerInvariant() : string.Empty;

        public static byte[] Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<byte>();
            string text = value.Trim();
            if (text.Length != 64) return Array.Empty<byte>();
            try
            {
                byte[] hash = Convert.FromHexString(text);
                return IsValid(hash) ? hash : Array.Empty<byte>();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
    }
}

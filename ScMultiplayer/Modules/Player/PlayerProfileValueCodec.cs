using Game;
using System;
using System.Globalization;
using System.Linq;

namespace ScMultiplayer
{
    // Source: ScMultiplayerProfileHandlers profile XML load/save helpers
    // Pure value encoding only; player state ownership remains in the profile handler.
    internal static class PlayerProfileValueCodec
    {
        public static int[][] CreateEmptyClothes() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };

        public static string FormatFloat(float value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        public static float ParseFloat(string value, float fallback = 0f) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                ? result : fallback;

        public static PlayerClass ParsePlayerClass(string value) =>
            Enum.TryParse(value, true, out PlayerClass result) ? result : PlayerClass.Male;

        public static bool ParseBool(string value, bool fallback) =>
            bool.TryParse(value, out bool result) ? result : fallback;

        public static string FormatIntArray(int[] values) =>
            values == null || values.Length == 0 ? string.Empty : string.Join(";", values);

        public static int[] ParseIntArray(string values)
        {
            if (string.IsNullOrWhiteSpace(values)) return Array.Empty<int>();
            return values.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int result) ? result : 0).ToArray();
        }
    }
}

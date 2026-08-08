using System;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Load
    // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Load
    // Stable player-record defaults are named in one place; entity restoration remains in
    // ScMultiplayerProfileHandlers and keeps its existing save/load order.
    internal static class PlayerRecordValuePolicy
    {
        public const float DefaultLevel = 1f;
        public const float DefaultHealth = 1f;
        public const float DefaultAir = 1f;
        public const float DefaultFood = 0.9f;
        public const float DefaultStamina = 1f;
        public const float DefaultSleep = 0.9f;
        public const float DefaultTemperature = 12f;

        public static float ParseFloat(string value, float fallback)
        {
            return PlayerProfileValueCodec.ParseFloat(value, fallback);
        }

        public static bool ShouldPersistItem(int value, int count)
        {
            return value != 0 && count > 0;
        }

        public static int ClampActiveSlot(int activeSlotIndex, int slotsCount)
        {
            if (slotsCount <= 0)
                return 0;
            return Math.Max(0, Math.Min(activeSlotIndex, slotsCount - 1));
        }
    }
}

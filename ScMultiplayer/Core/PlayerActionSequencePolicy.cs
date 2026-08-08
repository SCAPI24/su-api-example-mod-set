namespace ScMultiplayer.Core
{
    // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
    // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:
    // HandlePlayerActionMessage
    // Sequence arithmetic is pure and deliberately does not own queues or transport state.
    internal static class PlayerActionSequencePolicy
    {
        public static bool IsNewer(int sequence, int previousSequence)
        {
            return sequence > previousSequence;
        }

        public static int Next(int sequence)
        {
            return sequence == int.MaxValue ? 1 : sequence + 1;
        }

        public static bool ShouldTrimCache(int count, int maximumCount)
        {
            return maximumCount > 0 && count >= maximumCount;
        }
    }
}

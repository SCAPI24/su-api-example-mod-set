using System;

namespace ScMultiplayer.Transport
{
    // Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:
    // SampleJoinTransferBandwidth
    // Pure bandwidth arithmetic stays separate from the Comms sampling and transfer mutation path.
    internal static class JoinTransferBudgetPolicy
    {
        public static double CalculateAvailableBytesPerSecond(double configuredLimit,
            double configuredJoinLimit, double gameplayBytesPerSecond, double headroom)
        {
            double available = configuredLimit > 0.0
                ? Math.Max(0.0, configuredLimit - gameplayBytesPerSecond - headroom)
                : double.PositiveInfinity;
            return configuredJoinLimit > 0.0
                ? Math.Min(available, configuredJoinLimit)
                : available;
        }

        public static double RefillTokens(double currentTokens, double availableBytesPerSecond,
            double elapsed, double burstBytes)
        {
            if (double.IsPositiveInfinity(availableBytesPerSecond))
                return double.PositiveInfinity;
            return Math.Min(burstBytes, Math.Max(0.0, currentTokens) +
                availableBytesPerSecond * Math.Max(0.0, elapsed));
        }

        public static int EstimatePacketBytes(int payloadBytes) =>
            Math.Max(1, payloadBytes) + 96;

        public static bool HasTokens(double tokens, int requiredBytes) =>
            double.IsPositiveInfinity(tokens) || tokens >= requiredBytes;

        public static double RefundTokens(double tokens, double burstBytes, int refundedBytes) =>
            double.IsPositiveInfinity(tokens)
                ? double.PositiveInfinity
                : Math.Min(burstBytes, tokens + Math.Max(0, refundedBytes));
    }
}

using System;

namespace ScMultiplayer
{
    // Source: Mod/ScMultiplayer/Modules/Join/ScMultiplayerWorldTransferHandlers.cs:
    // HandleGamePakWorldReadyMessage
    // Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:
    // UpdateClientJoinBarrier
    // Stage matching and timer arithmetic are kept independent from transport and Apply work.
    internal static class JoinReadyPolicy
    {
        public static bool IsTransferMatch(int expectedTransferId, int actualTransferId)
        {
            return expectedTransferId > 0 && expectedTransferId == actualTransferId;
        }

        public static bool HasTimedOut(double now, double lastProgressTime, double timeout)
        {
            return lastProgressTime > 0.0 &&
                now - lastProgressTime >= Math.Max(0.0, timeout);
        }

        public static bool IsRetryDue(double now, double nextRetryTime)
        {
            return nextRetryTime > 0.0 && now >= nextRetryTime;
        }

        public static double ScheduleRetry(double now, double retryInterval)
        {
            return now + Math.Max(0.0, retryInterval);
        }

        public static int GetRemainingSeconds(double now, double lastProgressTime,
            double timeout)
        {
            if (lastProgressTime <= 0.0)
                return 0;
            return Math.Max(0, (int)Math.Ceiling(
                Math.Max(0.0, timeout) - (now - lastProgressTime)));
        }
    }
}

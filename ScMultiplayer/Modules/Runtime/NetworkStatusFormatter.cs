using System;
using System.Globalization;

namespace ScMultiplayer
{
    // Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:
    // UpdateNetworkStatsLabel, FormatChatMessage, CreateJoinedPlayerInformationRow
    // Display-only formatting is kept independent from sampling and UI widget ownership.
    internal static class NetworkStatusFormatter
    {
        public static string FormatBytesPerSecond(float bytesPerSecond)
        {
            if (bytesPerSecond >= 1024f * 1024f)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0}MB/s", bytesPerSecond / (1024f * 1024f));
            if (bytesPerSecond >= 1024f)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0}KB/s", bytesPerSecond / 1024f);
            return string.Format(CultureInfo.InvariantCulture,
                "{0:0}B/s", bytesPerSecond);
        }

        public static string FormatChatMessage(ChatMessage message)
        {
            string sender = string.IsNullOrWhiteSpace(message?.Sender)
                ? "Player"
                : message.Sender;
            return string.Format(CultureInfo.InvariantCulture,
                "[{0:HH:mm:ss}] {1}: {2}", message.Timestamp.ToLocalTime(),
                sender, message.Text);
        }

        public static string FormatJoinBandwidthLimit(bool configured, bool shared,
            int sharedCapKbps, int uploadCapKbps)
        {
            if (!configured)
                return "Automatic [On]";
            return shared
                ? "Shared (Kbps)[" + sharedCapKbps + "]"
                : "Separate (Kbps)[" + uploadCapKbps + "]";
        }

        public static string FormatPlayerRelative(bool isSelf, float distance,
            int clockDirection)
        {
            return isSelf
                ? "(self)"
                : string.Format(CultureInfo.InvariantCulture,
                    "({0:0}m, {1:00} o'clock)", distance, clockDirection);
        }

        public static int GetClockDirection(float forwardX, float forwardZ,
            float deltaX, float deltaZ, float distance)
        {
            if (distance < 0.01f)
                return 12;
            double dot = forwardX * deltaX + forwardZ * deltaZ;
            double clockwiseCross = forwardX * deltaZ - forwardZ * deltaX;
            int hour = (int)Math.Round(Math.Atan2(clockwiseCross, dot) * 6.0 / Math.PI,
                MidpointRounding.AwayFromZero);
            hour %= 12;
            if (hour < 0)
                hour += 12;
            return hour == 0 ? 12 : hour;
        }
    }
}

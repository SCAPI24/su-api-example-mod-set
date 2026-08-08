using Engine;
using ScMultiplayer.Core;

namespace ScMultiplayer
{
    // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:
    // HandlePlayerActionMessage
    // Request envelope validation is separated from action execution and queue ownership.
    internal static class ActionRequestValidationPolicy
    {
        public static bool IsSupportedHostRequest(PlayerActionMessage message,
            int sourceClientId, bool hasNetworkPlayer)
        {
            if (message == null || sourceClientId <= 0 || !hasNetworkPlayer)
                return false;
            return message.Action == PlayerActionType.HitRequest ||
                message.Action == PlayerActionType.InteractRequest ||
                message.Action == PlayerActionType.DropRequest ||
                message.Action == PlayerActionType.JumpRequest;
        }

        public static bool IsTerrainPredictionRequest(PlayerActionMessage message,
            int sourceClientId)
        {
            return message != null &&
                message.PlayerIndex == sourceClientId &&
                message.RequestId > 0 &&
                message.RequestId == message.Sequence;
        }

        public static bool IsDropRequest(PlayerActionMessage message, int sourceClientId,
            int lastSequence)
        {
            return message != null &&
                PlayerActionSequencePolicy.IsNewer(message.Sequence, lastSequence) &&
                message.PlayerIndex == sourceClientId &&
                message.ActiveSlotIndex >= 0 &&
                message.ItemValue != 0 &&
                message.ItemCount > 0 &&
                message.DropCount > 0 &&
                message.RemoveCount > 0 &&
                message.DropCount <= message.RemoveCount &&
                message.RemoveCount <= message.ItemCount &&
                IsFinite(message.Position) &&
                IsFinite(message.Velocity);
        }

        public static bool IsJumpRequest(PlayerActionMessage message, int sourceClientId,
            int lastSequence)
        {
            return message != null && message.PlayerIndex == sourceClientId &&
                PlayerActionSequencePolicy.IsNewer(message.Sequence, lastSequence);
        }

        public static bool IsNewInteractRequest(PlayerActionMessage message, int lastSequence)
        {
            return message != null &&
                PlayerActionSequencePolicy.IsNewer(message.Sequence, lastSequence);
        }

        public static bool IsNewHitRequest(PlayerActionMessage message, int lastSequence)
        {
            return message != null &&
                PlayerActionSequencePolicy.IsNewer(message.Sequence, lastSequence);
        }

        public static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
                !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
                !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
        }

        public static bool IsFinite(Ray3 value)
        {
            return IsFinite(value.Position) && IsFinite(value.Direction);
        }
    }
}

using Engine;
using Game;
using ScMultiplayer.Core;

namespace ScMultiplayer
{
    internal static class PlayerReadOnlyStateCapture
    {
        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
        // Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.Update
        public static bool TryCapture(ComponentPlayer player,
            out PlayerReadOnlyStateSnapshot snapshot)
        {
            snapshot = default;
            ComponentBody body = player?.ComponentBody;
            ComponentLocomotion locomotion = player?.ComponentLocomotion;
            if (body == null || locomotion == null) return false;

            bool isGrounded = body.StandingOnValue.HasValue &&
                MathUtils.Abs(body.Velocity.Y) < 0.1f;
            snapshot = new PlayerReadOnlyStateSnapshot(
                body.Position,
                body.Rotation,
                body.Velocity,
                locomotion.LookAngles,
                body.TargetCrouchFactor > 0f,
                locomotion.IsCreativeFlyEnabled,
                player.ComponentRider?.Mount != null,
                isGrounded);
            return snapshot.IsFinite;
        }
    }
}

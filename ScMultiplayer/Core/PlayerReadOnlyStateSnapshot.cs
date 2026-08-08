using Engine;

namespace ScMultiplayer.Core
{
    // Source: Mod/ScMultiplayer/Message/GamePlayerPositionMessage.cs:GamePlayerPositionMessage
    // Position/pose data is a replaceable read-only state boundary. It contains no inventory,
    // input, action or authority result, so it can be captured and applied without changing the
    // existing wire message or reliable-channel semantics.
    internal readonly struct PlayerReadOnlyStateSnapshot
    {
        public PlayerReadOnlyStateSnapshot(Vector3 position, Quaternion rotation, Vector3 velocity,
            Vector2 lookAngles, bool isCrouching, bool isFlying, bool isRiding, bool isGrounded)
        {
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            LookAngles = lookAngles;
            IsCrouching = isCrouching;
            IsFlying = isFlying;
            IsRiding = isRiding;
            IsGrounded = isGrounded;
        }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Velocity { get; }
        public Vector2 LookAngles { get; }
        public bool IsCrouching { get; }
        public bool IsFlying { get; }
        public bool IsRiding { get; }
        public bool IsGrounded { get; }

        public bool IsFinite =>
            IsFiniteVector(Position) && IsFiniteQuaternion(Rotation) &&
            IsFiniteVector(Velocity) && IsFiniteVector(LookAngles);

        // Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:
        // HandleGamePlayerPositionMessage
        public void ApplyTo(NetworkPlayerState state)
        {
            if (state == null || !IsFinite) return;
            state.Position = Position;
            state.Rotation = Rotation;
            state.Velocity = Velocity;
            state.LookAngles = LookAngles;
            state.IsCrouching = IsCrouching;
            state.IsFlying = IsFlying;
            state.IsRiding = IsRiding;
            state.IsGrounded = IsGrounded;
        }

        private static bool IsFiniteVector(Vector3 value) =>
            IsFiniteValue(value.X) && IsFiniteValue(value.Y) && IsFiniteValue(value.Z);

        private static bool IsFiniteVector(Vector2 value) =>
            IsFiniteValue(value.X) && IsFiniteValue(value.Y);

        private static bool IsFiniteQuaternion(Quaternion value) =>
            IsFiniteValue(value.X) && IsFiniteValue(value.Y) &&
            IsFiniteValue(value.Z) && IsFiniteValue(value.W);

        private static bool IsFiniteValue(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

using Engine;
using Game;
using ScMultiplayer.Core;
using System.Linq;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        private int m_lastPlayerAuthoritySequence;

        // Source: Mod/ScMultiplayer/Networking/NetworkMessageSender.cs:
        // NetworkMessageSender.SendPlayerAuthorityMessage
        // The owning client applies only the host's reliable result. No request payload can call
        // this path directly or mutate a local player before host approval.
        private void HandlePlayerAuthorityMessage(PlayerAuthorityMessage message,
            int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null || client == null ||
                message.TargetClientId != client.ClientID ||
                message.Sequence <= m_lastPlayerAuthoritySequence)
                return;

            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer player = players?.ComponentPlayers.FirstOrDefault(item =>
                item?.PlayerData != null && !m_networkPlayerData.Values.Contains(item.PlayerData));
            if (player?.ComponentBody == null || player.PlayerData == null)
                return;

            m_lastPlayerAuthoritySequence = message.Sequence;
            player.PlayerData.SpawnPosition = message.SpawnPosition;
            if (message.Action != PlayerAuthorityAction.Teleport)
                return;

            ComponentBody body = player.ComponentBody;
            body.Position = message.Position;
            body.Velocity = message.Velocity;
            m_localPlayerInput = default;
            m_localInputBodyPosition = body.Position;
            m_localInputBodyVelocity = body.Velocity;
            m_localInputBodyRotation = body.Rotation;
            m_localInputLookAngles = player.ComponentLocomotion?.LookAngles ?? Vector2.Zero;
            m_localInputSequence = PlayerActionSequencePolicy.Next(m_localInputSequence);
            m_lastSentInputSequence = -1;
            m_localInputResendsRemaining = 3;
            m_localKnockbackPositionCorrectionUntil = 0.0;
            m_localKnockbackCorrectionStartTick = -1;
        }

        private void ResetPlayerAuthorityState()
        {
            m_hostPlayerAuthoritySequence = 0;
            m_lastPlayerAuthoritySequence = 0;
        }
    }
}

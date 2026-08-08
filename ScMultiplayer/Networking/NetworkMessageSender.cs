using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TemplatesDatabase;
using ScMultiplayer.Ports;
using ScMultiplayer.Transport;

namespace ScMultiplayer
{
    public class NetworkMessageSender
    {
        private static readonly INetworkTransport s_transport = new CommsTransportAdapter();
        private const int MaximumSyncBatchBytes = 1100;
        private const int SkinAssetChunkBytes = 640;
        private static int s_nextSkinAssetTransferId;

        private sealed class PendingSyncBatch
        {
            public int TargetClientId;
            public bool Sequenced;
            public bool Latest;
            public int EstimatedBytes;
            public readonly List<byte[]> Payloads = new List<byte[]>();
        }

        private static readonly List<PendingSyncBatch> s_pendingSyncBatches =
            new List<PendingSyncBatch>();
        private static bool s_isSyncBatchActive;

        public static int PendingSyncBatchCount => s_pendingSyncBatches.Count;

        private static void ReserveHostRelay(int targetClientId, int payloadBytes,
            bool reliable, bool relayReservationAlreadyHeld)
        {
            if (!reliable || relayReservationAlreadyHeld || targetClientId <= 0 ||
                !ScMultiplayer.IsHost)
                return;
            ScMultiplayer.currentInstance?.ReserveReliableRelayPackets(targetClientId,
                ScMultiplayer.EstimateReliableRelayPackets(payloadBytes));
        }

        // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
        // Raw transport forwarding is kept here so business modules do not depend on Comms.
        // It intentionally adds no queue, retry, batching or delivery-mode changes.
        public static void SendRawPayload(int targetClientId, byte[] payload,
            bool sequenced = false, bool latest = false,
            bool relayReservationAlreadyHeld = false)
        {
            if (payload == null || payload.Length == 0) return;
            ReserveHostRelay(targetClientId, payload.Length, !latest,
                relayReservationAlreadyHeld);
            s_transport.SendDirectInput(targetClientId, payload, sequenced, latest);
        }

        public static void SendRawMessage(int targetClientId, Message message,
            bool sequenced = false, bool latest = false)
        {
            if (message == null) return;
            SendRawPayload(targetClientId,
                Message.WriteWithSender(message, s_transport.Address), sequenced, latest);
        }

        public static void BeginSyncBatch()
        {
            FlushSyncBatch();
            s_isSyncBatchActive = true;
        }

        public static void FlushSyncBatch()
        {
            bool wasActive = s_isSyncBatchActive;
            s_isSyncBatchActive = false;
            foreach (PendingSyncBatch batch in s_pendingSyncBatches.ToArray())
                SendPendingSyncBatch(batch);
            s_pendingSyncBatches.Clear();
            if (!wasActive) return;
        }

        public static void SendScheduledMessage(int targetClientId, Message message,
            bool sequenced = false, bool latest = false, bool batchable = true)
        {
            byte[] payload = Message.WriteWithSender(message, s_transport.Address);
            // Source: ScMultiplayer.SendTerrainCatchUp
            // Joining clients need only the coalesced authoritative terrain checkpoint. Do not
            // replay every intermediate terrain state before that checkpoint.
            bool recordJoinPayload = !(message is GameModifiedCellsMessage);
            bool hasJoiningRecipients = targetClientId < 0 &&
                ScMultiplayer.currentInstance?.RecordJoinCatchUpMessage(
                    payload, sequenced, latest, recordJoinPayload) == true;
            if (!s_isSyncBatchActive || !batchable || hasJoiningRecipients ||
                payload.Length + 24 > MaximumSyncBatchBytes)
            {
                if (s_isSyncBatchActive)
                {
                    FlushSyncBatch();
                    s_isSyncBatchActive = true;
                }
                if (hasJoiningRecipients)
                    ScMultiplayer.currentInstance.SendLiveBroadcastToReadyClients(
                        payload, sequenced, latest);
                else
                    s_transport.SendDirectInput(
                        targetClientId, payload, sequenced, latest);
                return;
            }

            PendingSyncBatch batch = s_pendingSyncBatches.LastOrDefault(item =>
                item.TargetClientId == targetClientId && item.Sequenced == sequenced &&
                item.Latest == latest);
            int entryBytes = payload.Length + 5;
            if (batch == null || batch.EstimatedBytes + entryBytes > MaximumSyncBatchBytes)
            {
                batch = new PendingSyncBatch
                {
                    TargetClientId = targetClientId,
                    Sequenced = sequenced,
                    Latest = latest,
                    EstimatedBytes = 24
                };
                s_pendingSyncBatches.Add(batch);
            }
            batch.Payloads.Add(payload);
            batch.EstimatedBytes += entryBytes;
        }

        private static void SendPendingSyncBatch(PendingSyncBatch batch)
        {
            if (batch.Payloads.Count == 0) return;
            byte[] payload = batch.Payloads.Count == 1
                ? batch.Payloads[0]
                : Message.WriteWithSender(new SyncBatchMessage
                {
                    Payloads = batch.Payloads
                }, s_transport.Address);
            bool hasJoiningRecipients = batch.TargetClientId < 0 &&
                ScMultiplayer.currentInstance?.RecordJoinCatchUpMessage(
                    payload, batch.Sequenced, batch.Latest) == true;
            if (hasJoiningRecipients)
                ScMultiplayer.currentInstance.SendLiveBroadcastToReadyClients(
                    payload, batch.Sequenced, batch.Latest);
            else
                s_transport.SendDirectInput(
                    batch.TargetClientId, payload, batch.Sequenced, batch.Latest);
        }

        // Source: ScMultiplayer.SendLiveBroadcastToReadyClients
        // Direct broadcast users bypass SendScheduledMessage. Keep their joining-peer treatment
        // identical so a circuit or world-object packet cannot refill the join reliable window.
        private static void SendDirectBroadcast(byte[] payload, bool sequenced = false,
            bool latest = false)
        {
            if (ScMultiplayer.currentInstance?.SendLiveBroadcastToReadyClients(
                    payload, sequenced, latest) == true)
                return;
            s_transport.SendDirectInput(-1, payload, sequenced, latest);
        }

        public static void SendPlayerPositionMessage(int playerIndex, int serverTick,
            Vector3 position, Quaternion rotation,
            Vector3 velocity, Vector2 lookAngles, Vector2? walkOrder, float jumpOrder,
            float pokingPhase, bool attackOrder, bool rowLeftOrder, bool rowRightOrder,
            bool isCrouching, bool isFlying, bool isRiding, bool isGrounded,
            int activeSlotIndex, int handItemValue, int handItemCount,
            Vector3 itemOffset, Vector3 itemRotation, float aimHandAngle,
            int[] slotValues, int[] slotCounts,
            List<GamePlayerPositionMessage> batch = null)
        {
            var msg = new GamePlayerPositionMessage(playerIndex, serverTick, position, rotation, velocity,
                lookAngles, walkOrder, jumpOrder, pokingPhase,
                attackOrder, rowLeftOrder, rowRightOrder,
                isCrouching, isFlying, isRiding, isGrounded,
                activeSlotIndex, handItemValue, handItemCount,
                itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts);
            if (batch != null)
            {
                batch.Add(msg);
                return;
            }
            SendScheduledMessage(-1, msg, latest: true);
        }

        public static void SendPlayerPositionBatch(List<GamePlayerPositionMessage> players)
        {
            if (players == null || players.Count == 0) return;
            var message = new GamePlayerPositionsMessage(players);
            // Source: ScMultiplayer.cs:HandleGamePlayerPositionMessage
            // Player snapshots carry ServerTick and reject stale state at the receiver. Keeping
            // them out of Comms' shared ReliableSequenced stream prevents a delayed fragmented
            // message from freezing every later player presentation update.
            SendScheduledMessage(-1, message, latest: true);
        }

        public static void SendPlayerInputMessage(int playerIndex, int sequence, int clientTick,
            Vector3 bodyPosition, Vector3 bodyVelocity, Quaternion bodyRotation,
            Vector2 lookAngles, PlayerInput playerInput, float pokingPhase,
            bool isControlledByTouch,
            bool isCrouching, bool isFlying, bool isGrounded, bool isRiding,
            ushort mountEntityId,
            int activeSlotIndex, int inventoryAuthorityTick,
            int[] slotValues, int[] slotCounts)
        {
            var msg = new GamePlayerInputMessage(
                playerIndex, sequence, clientTick, bodyPosition, bodyVelocity,
                bodyRotation, lookAngles, playerInput, pokingPhase, isControlledByTouch,
                isCrouching, isFlying, isGrounded, isRiding,
                mountEntityId, activeSlotIndex, inventoryAuthorityTick,
                slotValues, slotCounts);
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(msg, s_transport.Address), latest: true);
        }

        public static ChatMessage SendChatMessage(string sender, string senderIdentity, string text)
        {
            var msg = new ChatMessage(sender, senderIdentity, text);
            // Source: ScMultiplayer.cs:NetworkMessageHandler.HandleChatMessage
            SendScheduledMessage(-1, msg);
            return msg;
        }

        public static void SendWorldInfoMessage(double timeOfDayOffset, double totalElapsedGameTime,
            TimeOfDayMode currentTimeMode, SubsystemWeather weather, SubsystemSky sky,
            int worldTimeRevision, bool reliable = false)
        {
            // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.m_lightningStrikePosition
            // Nullable<T> boxes a present value as T. Use SuAPI's non-generic getter so its
            // generic result check does not reject the boxed Vector3 value.
            object lightningValue = ScMultiplayer.ModManager.ModParentField.GetParentField(
                sky, "m_lightningStrikePosition", typeof(SubsystemSky));
            Vector3? lightningPosition = lightningValue is Vector3 position
                ? position
                : (Vector3?)null;
            var msg = new GameWorldInfoMessage1(timeOfDayOffset, totalElapsedGameTime, currentTimeMode,
                weather.IsPrecipitationStarted, weather.PrecipitationIntensity,
                weather.IsFogStarted, weather.FogProgress, weather.FogIntensity, weather.FogSeed,
                lightningPosition.HasValue, lightningPosition ?? Vector3.Zero)
            {
                ServerTick = s_transport.Step,
                TerrainSequence = ScMultiplayer.currentInstance?.CircuitTerrainSequence ?? 0L,
                WorldTimeRevision = worldTimeRevision,
                IsTimeAccelerated = GameManager.Project?.FindSubsystem<SubsystemTime>(false)?
                    .FixedTimeStep.HasValue == true
            };
            SendScheduledMessage(-1, msg, latest: !reliable, batchable: !reliable);
        }

        public static void SendWorldControlRequest(int requestId, WorldControlAction actions)
        {
            var msg = new WorldControlRequestMessage(requestId, actions);
            // Source: Mod/ScMultiplayer/Modules/Player/
            // ScMultiplayerHealthWorldControlHandlers.cs:HandleWorldControlRequest
            // World controls are rare transactional inputs. The host deduplicates RequestId, so
            // reliable ordering prevents a lost request from stalling every later request id.
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(msg, s_transport.Address), sequenced: true);
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:NetworkMessageSender.SendWorldControlRequest
        public static void SendWorldControlResult(int targetClientId,
            WorldControlResultMessage message)
        {
            // Source: Mod/ScMultiplayer/Modules/Player/
            // ScMultiplayerHealthWorldControlHandlers.cs:HandleWorldControlResult
            // The directed result owns the requesting client's native feedback. Losing this small
            // packet after the host mutation would change time without showing its Dawn/Noon label.
            s_transport.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, s_transport.Address), sequenced: true);
        }

        // Source: ScMultiplayer.cs:HandleAnimalEntityMessage
        public static void SendEntityMessage(EntityMessage message) =>
            SendScheduledMessage(-1, message);

        // Source: ScMultiplayer.cs:HandleAnimalBodyUpdate
        public static void SendBodyUpdateMessage(BodyUpdateMessage message,
            bool reliable = false) =>
            SendScheduledMessage(-1, message, latest: !reliable,
                batchable: !reliable);

        // Source: Survivalcraft/Game/ComponentCreatureSounds.cs:ComponentCreatureSounds.PlayIdleSound
        public static void BroadcastAnimalSound(AnimalSoundMessage message)
        {
            SendDirectBroadcast(Message.WriteWithSender(message,
                s_transport.Address));
        }

        // Source: ScMultiplayer.cs:HandlePickableSyncMessage
        public static void SendPickableMessage(PickableSyncMessage message,
            int targetClientId = -1) =>
            SendScheduledMessage(targetClientId, message,
                sequenced: false,
                latest: message.Action == PickableSyncMessage.PickAction.UpdatePosition);

        public static void SendPakWorldManifest(int targetClientId, GamePakWorldMessage message)
        {
            byte[] payload = Message.WriteWithSender(message, s_transport.Address);
            ReserveHostRelay(targetClientId, payload.Length, reliable: true,
                relayReservationAlreadyHeld: false);
            s_transport.SendDirectInput(targetClientId, payload);
        }

        public static void SendPakWorldChunk(int targetClientId,
            GamePakWorldChunkMessage message,
            bool relayReservationAlreadyHeld = false)
        {
            byte[] payload = Message.WriteWithSender(message, s_transport.Address);
            ReserveHostRelay(targetClientId, payload.Length, reliable: true,
                relayReservationAlreadyHeld);
            s_transport.SendDirectInput(targetClientId, payload);
        }

        public static void SendPakWorldReady(GamePakWorldReadyMessage message)
        {
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address), sequenced: true);
        }

        public static void SendPakWorldReady(int targetClientId, GamePakWorldReadyMessage message)
        {
            byte[] payload = Message.WriteWithSender(message, s_transport.Address);
            ReserveHostRelay(targetClientId, payload.Length, reliable: true,
                relayReservationAlreadyHeld: false);
            s_transport.SendDirectInput(targetClientId, payload, sequenced: true);
        }

        public static void SendPakWorldRepairRequest(GamePakWorldRepairRequestMessage message)
        {
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address));
        }

        public static void SendPlayerAimMessage(PlayerAimMessage message)
        {
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address), sequenced: true);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public static void SendPlayerHitRequest(PlayerActionMessage message)
        {
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address), sequenced: true);
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        public static void SendMeleeHitResult(int targetClientId, MeleeHitResultMessage message)
        {
            s_transport.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, s_transport.Address));
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public static void SendPlayerInteractRequest(PlayerActionMessage message)
        {
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address), sequenced: true);
        }

        public static void SendPlayerInteractResult(int targetClientId,
            PlayerActionMessage message)
        {
            s_transport.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, s_transport.Address), sequenced: true);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public static void SendPlayerDropRequest(PlayerActionMessage message)
        {
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address), sequenced: true);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public static void SendPlayerJumpRequest(PlayerActionMessage message)
        {
            byte[] payload = Message.WriteWithSender(message, s_transport.Address);
            // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
            // Two tiny unreliable copies bypass reliable head-of-line blocking. The reliable copy
            // remains a fallback, and the host deduplicates all three by JumpRequest.Sequence.
            s_transport.SendDirectInput(0, payload, latest: true);
            s_transport.SendDirectInput(0, payload, latest: true);
            s_transport.SendDirectInput(0, payload, sequenced: true);
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        public static void SendProjectileHit(int targetClientId, ProjectileSyncMessage message)
        {
            s_transport.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, s_transport.Address));
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.LeaveGame
        public static void BroadcastPlayerLeave(PlayerActionMessage message)
        {
            SendScheduledMessage(-1, message);
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead
        public static void SendPlayerRespawnRequest(PlayerActionMessage message)
        {
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address));
        }

        public static void BroadcastPlayerRespawn(PlayerActionMessage message)
        {
            SendScheduledMessage(-1, message);
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Poke
        public static void BroadcastPlayerPoke(PlayerActionMessage message)
        {
            SendScheduledMessage(-1, message);
        }

        // Source: Survivalcraft/Game/SubsystemWhistleBlockBehavior.cs:SubsystemWhistleBlockBehavior.OnUse
        public static void BroadcastPlayerWhistle(PlayerActionMessage message)
        {
            SendScheduledMessage(-1, message);
        }

        public static void SendTerrainDigRequest(TerrainDigRequestMessage message)
        {
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address));
        }

        public static void SendTerrainDigResult(int targetClientId,
            TerrainDigResultMessage message)
        {
            s_transport.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, s_transport.Address));
        }

        // Source: Survivalcraft/Game/ComponentDiggingCracks.cs:ComponentDiggingCracks.Draw
        public static void SendDigPresentation(int targetClientId, DigPresentationMessage message,
            bool latest)
        {
            if (message == null || ScMultiplayer.client == null) return;
            SendScheduledMessage(targetClientId, message, sequenced: false, latest: latest,
                batchable: true);
        }

        public static void SendPlayerProfileMessage(int clientId, NetworkPlayerRecord record)
        {
            var msg = new PlayerProfileMessage(clientId, record);
            // Source: ScMultiplayer.cs:HandlePlayerProfileMessage
            SendScheduledMessage(-1, msg);
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.HandlePlayerSkinAssetMessage
        public static void SendPlayerSkinAssetMessage(int targetClientId,
            PlayerSkinAssetMessage message)
        {
            if (message == null || ScMultiplayer.client == null) return;
            if (message.Action != PlayerSkinAssetMessage.SkinAssetAction.Data ||
                message.Data == null || message.Data.Length <= SkinAssetChunkBytes)
            {
                message.TransferId = message.TransferId != 0
                    ? message.TransferId
                    : Interlocked.Increment(ref s_nextSkinAssetTransferId);
                message.ChunkIndex = 0;
                message.ChunkCount = 1;
                message.TotalLength = message.Data?.Length ?? 0;
                s_transport.SendDirectInput(targetClientId,
                    Message.WriteWithSender(message, s_transport.Address),
                    sequenced: true);
                return;
            }

            int transferId = Interlocked.Increment(ref s_nextSkinAssetTransferId);
            int totalLength = message.Data.Length;
            int chunkCount = (totalLength + SkinAssetChunkBytes - 1) / SkinAssetChunkBytes;
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                int offset = chunkIndex * SkinAssetChunkBytes;
                int count = Math.Min(SkinAssetChunkBytes, totalLength - offset);
                byte[] chunk = new byte[count];
                Buffer.BlockCopy(message.Data, offset, chunk, 0, count);
                var part = new PlayerSkinAssetMessage(
                    PlayerSkinAssetMessage.SkinAssetAction.Data,
                    message.ClientId, message.SkinName, message.PlayerClass,
                    message.Sha256, chunk)
                {
                    TransferId = transferId,
                    ChunkIndex = chunkIndex,
                    ChunkCount = chunkCount,
                    TotalLength = totalLength
                };
                s_transport.SendDirectInput(targetClientId,
                    Message.WriteWithSender(part, s_transport.Address),
                    sequenced: true);
            }
        }

        public static void SendPlayerEquipmentMessage(int targetClientId,
            PlayerEquipmentMessage message)
        {
            if (message == null || ScMultiplayer.client == null) return;
            SendScheduledMessage(targetClientId, message, sequenced: true, latest: false,
                batchable: false);
        }

        // Source: Survivalcraft/Game/SubsystemEditableItemBehavior.cs:SubsystemEditableItemBehavior<T>
        public static void SendEditableDataRequest(EditableDataRequestMessage message)
        {
            if (message == null || ScMultiplayer.client == null) return;
            s_transport.SendDirectInput(0,
                Message.WriteWithSender(message, s_transport.Address), sequenced: true);
        }

        public static void SendEditableDataState(int targetClientId,
            EditableDataStateMessage message)
        {
            if (message == null || ScMultiplayer.client == null) return;
            SendScheduledMessage(targetClientId, message, sequenced: true, latest: false);
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.PublishNetworkState
        public static void SendCircuitSync(int targetClientId, CircuitSyncMessage message,
            bool latest = false)
        {
            if (message == null || ScMultiplayer.client == null) return;
            byte[] payload = Message.WriteWithSender(message, s_transport.Address);
            // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:
            // ServerGame.SendDirectInput
            // Circuit events carry their own EventSequence/recovery ordering. Keep reliable
            // delivery without coupling unrelated circuit messages to ReliableSequenced HOL.
            if (targetClientId < 0)
                SendDirectBroadcast(payload, sequenced: false, latest: latest);
            else
            {
                ReserveHostRelay(targetClientId, payload.Length, !latest,
                    relayReservationAlreadyHeld: false);
                s_transport.SendDirectInput(targetClientId, payload,
                    sequenced: false, latest: latest);
            }
        }

        // Source: Mod/ScMultiplayer/Func/WorldObjectSynchronizer.cs:
        // WorldObjectSynchronizer.Update
        public static void SendWorldObjectSync(int targetClientId,
            WorldObjectSyncMessage message, bool latest = false)
        {
            if (message == null || ScMultiplayer.client == null) return;
            byte[] payload = Message.WriteWithSender(message, s_transport.Address);
            if (targetClientId < 0)
                SendDirectBroadcast(payload, sequenced: !latest, latest: latest);
            else
                s_transport.SendDirectInput(targetClientId, payload,
                    sequenced: !latest, latest: latest);
        }

        public static void SendPlayerHealthMessage(int playerIndex, ComponentPlayer player,
            float healthChange, string cause = null, bool hasKnockback = false,
            int knockbackSequence = 0, int knockbackServerTick = 0,
            float knockbackStunTime = 0f, bool? isSleepingOverride = null)
        {
            ComponentHealth health = player?.ComponentHealth;
            ComponentVitalStats vitalStats = player?.ComponentVitalStats;
            if (health == null || vitalStats == null) return;
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            ComponentSleep sleep = player.ComponentSleep;
            object sleepStartValue = sleep == null
                ? null
                : ScMultiplayer.ModManager.ModParentField.GetParentField(
                    sleep, "m_sleepStartTime", typeof(ComponentSleep));
            double sleepStartTime = sleepStartValue is double startTime
                ? startTime
                : 0.0;
            float sleepFactor = sleep == null
                ? 0f
                : ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                    sleep, "m_sleepFactor", typeof(ComponentSleep));
            var msg = new GamePlayerHealthMessage(
                playerIndex, health.Health, 1f, healthChange, health.Health <= 0f,
                health.Air, vitalStats.Food, vitalStats.Stamina, vitalStats.Sleep,
                vitalStats.Temperature,
                ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                    vitalStats, "m_targetTemperature", typeof(ComponentVitalStats)),
                vitalStats.Wetness, player.PlayerData.Level,
                player.ComponentBody?.Velocity ?? Vector3.Zero,
                hasKnockback,
                isSleepingOverride ?? player.ComponentSleep?.IsSleeping == true,
                onFire != null ? ScMultiplayer.ModManager.ModParentField.GetParentField<float>(onFire, "m_fireDuration", typeof(ComponentOnFire)) : 0f,
                flu != null ? ScMultiplayer.ModManager.ModParentField.GetParentField<float>(flu, "m_fluDuration", typeof(ComponentFlu)) : 0f,
                sickness != null ? ScMultiplayer.ModManager.ModParentField.GetParentField<float>(sickness, "m_sicknessDuration", typeof(ComponentSickness)) : 0f,
                (flu as SuComponentFlu)?.CoughSequence ?? 0,
                flu?.IsCoughing == true,
                cause, sleepStartTime, sleepFactor);
            msg.KnockbackSequence = knockbackSequence;
            msg.KnockbackServerTick = knockbackServerTick;
            msg.KnockbackStunTime = knockbackStunTime;
            msg.DamageSequence = ScMultiplayer.currentInstance?.GetDamageSequence(
                playerIndex, healthChange) ?? 0;
            // Source: ScMultiplayer.cs:SendAuthoritativePlayerHealth
            // Client-to-host health requests retain the default zero sequence. Only host
            // snapshots receive an authoritative ordering token.
            if (ScMultiplayer.IsHost)
                msg.AuthoritativeStateSequence = ScMultiplayer.currentInstance
                    .GetNextAuthoritativePlayerStateSequence(playerIndex);
            // Source: ScMultiplayer.cs:NetworkMessageHandler.HandlePlayerHealthMessage
            if (hasKnockback)
            {
                byte[] payload = Message.WriteWithSender(msg, s_transport.Address);
                // Source: ScMultiplayer.cs:NetworkMessageSender.SendPlayerJumpRequest
                // Knockback is an immediate gameplay edge. Two replaceable fast copies bypass a
                // congested reliable queue; the reliable copy remains a fallback. Receivers use
                // KnockbackSequence to ensure that all three copies apply the impulse only once.
                SendDirectBroadcast(payload, latest: true);
                SendDirectBroadcast(payload, latest: true);
                SendDirectBroadcast(payload);
                return;
            }
            SendScheduledMessage(-1, msg);
        }

        public static void SendKickPlayerMessage(int targetClientID, string reason = null)
        {
            var msg = new GameKickPlayerMessage(targetClientID, reason);
            // Source: ScMultiplayer.cs:HandleGameKickPlayerMessage
            SendScheduledMessage(-1, msg);
        }
    }
}

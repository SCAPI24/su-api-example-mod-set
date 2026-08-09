using System;
using System.Diagnostics;
using Engine;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.Client_GameStep
        // The router owns message-to-domain dispatch only. Every game mutation keeps the
        // existing priority, world-transfer or end-of-frame queue and therefore the same thread.
        private sealed class NetworkMessageRouter
        {
            private readonly ScMultiplayer m_owner;
            private double m_nextFailureReportTime;

            public NetworkMessageRouter(ScMultiplayer owner)
            {
                m_owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public void Route(int sourceClientId, Message message, int payloadBytes)
            {
                if (message is SyncBatchMessage syncBatch)
                {
                    RouteBatch(sourceClientId, syncBatch);
                    return;
                }
                long receivedTimestamp = Stopwatch.GetTimestamp();
                NetworkIngressCommand command = NetworkIngressCommand.Create(
                    sourceClientId, message, payloadBytes, receivedTimestamp);
                m_owner.m_controlUnit?.Context.IngressDiagnostics.RecordReceive(in command);
                switch (message)
                {
                    case ChatMessage chat:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            NetworkMessageHandler.HandleChatMessage(chat, sourceClientId));
                        break;
                    case GamePlayerPositionMessage position:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleGamePlayerPositionMessage(position, sourceClientId));
                        break;
                    case GamePlayerPositionsMessage positions:
                        m_owner.QueueEndOfFrameAction(command, () =>
                        {
                            if (positions.Players == null) return;
                            foreach (GamePlayerPositionMessage item in positions.Players)
                                m_owner.HandleGamePlayerPositionMessage(item, sourceClientId);
                        });
                        break;
                    case GamePlayerInputMessage playerInput:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleGamePlayerInputMessage(playerInput, sourceClientId));
                        break;
                    case PlayerAimMessage playerAim:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandlePlayerAimMessage(playerAim, sourceClientId));
                        break;
                    case PlayerActionMessage playerAction:
                        if (playerAction.Action == PlayerActionType.JumpRequest)
                            m_owner.QueuePriorityInputAction(command, () =>
                                m_owner.HandlePlayerActionMessage(playerAction, sourceClientId));
                        else
                            m_owner.QueueEndOfFrameAction(command, () =>
                                m_owner.HandlePlayerActionMessage(playerAction, sourceClientId));
                        break;
                    case TerrainDigRequestMessage terrainDigRequest:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleTerrainDigRequest(terrainDigRequest, sourceClientId));
                        break;
                    case TerrainDigResultMessage terrainDigResult:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleTerrainDigResult(terrainDigResult, sourceClientId));
                        break;
                    case DigPresentationMessage digPresentation:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleDigPresentationMessage(digPresentation, sourceClientId));
                        break;
                    case GameModifiedCellsMessage cells:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            NetworkMessageHandler.HandleModifiedCellsMessage(cells, sourceClientId));
                        break;
                    case TerrainRecoveryMessage terrainRecovery:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleTerrainRecoveryMessage(terrainRecovery, sourceClientId));
                        break;
                    case TerrainChunkSyncMessage terrainChunkSync:
                        m_owner.QueueTerrainChunkSyncAction(command, terrainChunkSync,
                            sourceClientId);
                        break;
                    case GameWorldInfoMessage1 worldInfo:
                        if (sourceClientId == 0)
                            m_owner.DispatchIngressAction(command, () =>
                                NetworkMessageHandler.HandleWorldInfoMessage(worldInfo,
                                    sourceClientId));
                        break;
                    case WorldControlRequestMessage worldControl:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleWorldControlRequest(worldControl, sourceClientId));
                        break;
                    case WorldControlResultMessage worldControlResult:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleWorldControlResult(worldControlResult, sourceClientId));
                        break;
                    case PlayerProfileMessage playerProfile:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandlePlayerProfileMessage(playerProfile, sourceClientId));
                        break;
                    case PlayerSkinAssetMessage playerSkinAsset:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandlePlayerSkinAssetMessage(playerSkinAsset, sourceClientId));
                        break;
                    case PlayerEquipmentMessage playerEquipment:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandlePlayerEquipmentMessage(playerEquipment, sourceClientId));
                        break;
                    case EditableDataRequestMessage editableDataRequest:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleEditableDataRequest(editableDataRequest, sourceClientId));
                        break;
                    case EditableDataStateMessage editableDataState:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleEditableDataState(editableDataState, sourceClientId));
                        break;
                    case CircuitSyncMessage circuitSync:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.m_circuitSynchronizer?.HandleMessage(circuitSync,
                                sourceClientId));
                        break;
                    case WorldObjectSyncMessage worldObjectSync:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.m_worldObjectSynchronizer?.HandleMessage(worldObjectSync,
                                sourceClientId));
                        break;
                    case GamePakWorldMessage pakWorld:
                        if (sourceClientId == 0)
                            m_owner.QueueWorldTransferAction(command, () =>
                                NetworkMessageHandler.HandlePakWorldMessage(pakWorld,
                                    sourceClientId));
                        break;
                    case GamePakWorldChunkMessage worldChunk:
                        if (sourceClientId == 0)
                            m_owner.QueueWorldTransferAction(command, () =>
                                m_owner.HandleGamePakWorldChunkMessage(worldChunk));
                        break;
                    case GamePakWorldReadyMessage worldReady:
                        m_owner.QueueWorldTransferAction(command, () =>
                            m_owner.HandleGamePakWorldReadyMessage(worldReady, sourceClientId));
                        break;
                    case GamePakWorldRepairRequestMessage repairRequest:
                        m_owner.QueueWorldTransferAction(command, () =>
                            m_owner.HandleGamePakWorldRepairRequestMessage(repairRequest,
                                sourceClientId));
                        break;
                    case GamePlayerHealthMessage health:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            NetworkMessageHandler.HandlePlayerHealthMessage(health,
                                sourceClientId));
                        break;
                    case GameKickPlayerMessage kick:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleGameKickPlayerMessage(kick, sourceClientId));
                        break;
                    case EntityMessage entity:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleAnimalEntityMessage(entity, sourceClientId));
                        break;
                    case BodyUpdateMessage bodyUpdate:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleAnimalBodyUpdate(bodyUpdate, sourceClientId));
                        break;
                    case AnimalInteractionMessage animalInteraction:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleAnimalInteractionMessage(animalInteraction,
                                sourceClientId));
                        break;
                    case AnimalSoundMessage animalSound:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleAnimalSoundMessage(animalSound, sourceClientId));
                        break;
                    case MeleeHitResultMessage meleeHitResult:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleMeleeHitResultMessage(meleeHitResult, sourceClientId));
                        break;
                    case PickableSyncMessage pickableSync:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandlePickableSyncMessage(pickableSync, sourceClientId));
                        break;
                    case ProjectileSyncMessage projectileSync:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleProjectileSyncMessage(projectileSync, sourceClientId));
                        break;
                    case ExplosionSyncMessage explosionSync:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleExplosionSyncMessage(explosionSync, sourceClientId));
                        break;
                    case ContainerSyncMessage containerSync:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleContainerSyncMessage(containerSync, sourceClientId));
                        break;
                    case FurnitureBuildRequestMessage furnitureBuild:
                        m_owner.QueueEndOfFrameAction(command, () =>
                            m_owner.HandleFurnitureBuildRequest(furnitureBuild,
                                sourceClientId));
                        break;
                    default:
                        ReportFailure(sourceClientId,
                            "Unknown message type: " + message.GetType().Name);
                        break;
                }
            }

            private void RouteBatch(int sourceClientId, SyncBatchMessage batch)
            {
                if (batch?.Payloads == null)
                    return;

                foreach (byte[] payload in batch.Payloads)
                {
                    try
                    {
                        if (!NetworkMessageIngress.TryDecode(payload, client.Address.Port,
                                out Message message, out string error))
                        {
                            if (!string.IsNullOrEmpty(error))
                                ReportFailure(sourceClientId,
                                    "Failed to unpack sync batch item: " + error);
                            continue;
                        }
                        if (message is SyncBatchMessage)
                            throw new InvalidOperationException(
                                "Nested sync batch is not allowed.");
                        Route(sourceClientId, message, payload.Length);
                    }
                    catch (Exception ex)
                    {
                        ReportFailure(sourceClientId,
                            "Failed to unpack sync batch item: " + ex.Message);
                    }
                }
            }

            private void ReportFailure(int sourceClientId, string details)
            {
                double now = Time.RealTime;
                if (now < m_nextFailureReportTime)
                    return;
                m_nextFailureReportTime = now + 1.0;
                m_owner.RecordRouterFailure(sourceClientId, details);
            }
        }
    }
}

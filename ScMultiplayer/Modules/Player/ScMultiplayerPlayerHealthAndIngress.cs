using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using ScMultiplayer.Core;
using ScMultiplayer.Control;
using ScMultiplayer.Modules.Join;
using ScMultiplayer.Diagnostics;
using ScMultiplayer.Transport;
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

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        // ====================================================================
        // 发送: 生命值 (周期性)
        // ====================================================================
        private void SendGamePlayerHealthMessage(bool force)
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null || !IsHost) return;
            var players = subsystemPlayers.ComponentPlayers;

            ComponentPlayer item = players.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (item != null)
                SendAuthoritativePlayerHealth(client.ClientID, item, force);
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0 && remote.Value?.ComponentPlayer != null)
                    SendAuthoritativePlayerHealth(remote.Key, remote.Value.ComponentPlayer, force);
            }
        }

        private void SendAuthoritativePlayerHealth(int networkClientId, ComponentPlayer player,
            bool force, int sleepRequestSequence = 0)
        {
            if (player?.ComponentHealth == null || player.ComponentVitalStats == null) return;
            AuthoritativePlayerStateSnapshot current =
                CaptureAuthoritativePlayerState(player);
            bool hasPrevious = m_lastSentAuthoritativePlayerStates.TryGetValue(
                networkClientId, out AuthoritativePlayerStateSnapshot previous);
            if (!force && hasPrevious && !current.HasMeaningfulChangeFrom(previous))
                return;

            bool sleepAcceleration = IsSleepAccelerationActive(GameManager.Project);
            bool healthDecreased = hasPrevious &&
                current.Health < previous.Health - 0.0001f;
            bool sleepEdge = hasPrevious && current.IsSleeping != previous.IsSleeping;
            if (!force && sleepAcceleration && hasPrevious && !healthDecreased && !sleepEdge &&
                m_nextSleepHealthSendTimes.TryGetValue(networkClientId, out double nextSend) &&
                Time.RealTime < nextSend)
                return;

            float healthChange = hasPrevious ? current.Health - previous.Health : 0f;
            NetworkMessageSender.SendPlayerHealthMessage(networkClientId, player, healthChange,
                sleepRequestSequence: sleepRequestSequence);
            m_lastSentAuthoritativePlayerStates[networkClientId] = current;
            if (sleepAcceleration)
                m_nextSleepHealthSendTimes[networkClientId] = Time.RealTime + 0.5;
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Update
        private AuthoritativePlayerStateSnapshot CaptureAuthoritativePlayerState(
            ComponentPlayer player)
        {
            ComponentVitalStats vital = player.ComponentVitalStats;
            float targetTemperature = ModManager.ModParentField.GetParentField<float>(
                vital, "m_targetTemperature", typeof(ComponentVitalStats));
            return new AuthoritativePlayerStateSnapshot(player.ComponentHealth.Health,
                player.ComponentHealth.Air, vital.Food, vital.Stamina, vital.Sleep,
                vital.Temperature, targetTemperature, vital.Wetness,
                MathUtils.Max(player.PlayerData.Level, 1f),
                player.ComponentSleep?.IsSleeping == true);
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Attacked
        // Vanilla only wakes in some fatigue/health ranges. A network sleep session must leave
        // the shared accelerated-time state on any animal attack, otherwise the host can keep
        // advancing the world while a damaged player remains asleep on another client.
        private void EnsureHostSleepWakeHandlers(Project project)
        {
            if (!IsHost || project == null) return;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;
            HashSet<ComponentHealth> active = new HashSet<ComponentHealth>();
            foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
            {
                ComponentHealth health = player?.ComponentHealth;
                if (health == null || player.ComponentSleep == null) continue;
                active.Add(health);
                if (m_hostSleepWakeHandlers.ContainsKey(health)) continue;
                ComponentPlayer capturedPlayer = player;
                Action<ComponentCreature> handler = attacker =>
                {
                    if (attacker?.Entity?.FindComponent<ComponentPlayer>() != null ||
                        capturedPlayer.ComponentSleep?.IsSleeping != true)
                        return;
                    capturedPlayer.ComponentSleep.WakeUp();
                    PublishHostSleepWakeState(capturedPlayer);
                };
                m_hostSleepWakeHandlers[health] = handler;
                health.Attacked += handler;
            }
            foreach (KeyValuePair<ComponentHealth, Action<ComponentCreature>> item in
                m_hostSleepWakeHandlers.ToArray())
            {
                if (active.Contains(item.Key)) continue;
                item.Key.Attacked -= item.Value;
                m_hostSleepWakeHandlers.Remove(item.Key);
            }
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
        // ComponentSleep has several wake paths that do not share an event. Compare the host's
        // authoritative component state once per world frame and publish both sleep and wake
        // edges without relying on the client's local simulation.
        private void PublishHostSleepStateTransitions(Project project)
        {
            if (!IsHost || project == null || client?.IsConnected != true) return;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;

            HashSet<int> activeClientIds = new HashSet<int>();
            foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
            {
                if (!TryGetHostNetworkClientId(player, out int clientId)) continue;
                activeClientIds.Add(clientId);
                bool isSleeping = player.ComponentSleep?.IsSleeping == true;
                if (m_hostObservedSleepStates.TryGetValue(clientId,
                        out bool previousSleeping) && previousSleeping != isSleeping)
                {
                    PublishHostSleepWakeState(player);
                }
                m_hostObservedSleepStates[clientId] = isSleeping;
            }

            foreach (int clientId in m_hostObservedSleepStates.Keys
                .Where(id => !activeClientIds.Contains(id)).ToArray())
                m_hostObservedSleepStates.Remove(clientId);
        }

        private bool TryGetHostNetworkClientId(ComponentPlayer player, out int clientId)
        {
            clientId = client?.ClientID ?? 0;
            if (player == null) return false;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (ReferenceEquals(item.Value?.ComponentPlayer, player))
                {
                    clientId = item.Key;
                    return true;
                }
            }
            return player.PlayerData != null && clientId == 0;
        }

        // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
        // Vanilla stores a separate sleep start for every player. Once a multiplayer host
        // enters the shared accelerated-time window, move all sleeping players to one host
        // boundary so they cannot wake on different frames and leave clients on different
        // circuit timelines.
        private void MaintainHostSleepAccelerationSession(Project project)
        {
            if (!IsHost || project == null) return;
            SubsystemTime subsystemTime = project.FindSubsystem<SubsystemTime>(false);
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemTime == null || players == null) return;

            bool accelerated = subsystemTime.FixedTimeStep.HasValue;
            if (!accelerated)
            {
                bool accelerationEnded = m_hostSleepAccelerationSessionActive;
                m_hostSleepAccelerationSessionActive = false;
                if (accelerationEnded && client?.IsConnected == true)
                {
                    // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
                    // Publish the authoritative falling edge before player wake snapshots. The
                    // ordinary world stream is replaceable and only 2Hz outside acceleration;
                    // waiting for it lets a client wake while its circuit still has the pre-sleep
                    // counter values.
                    SendGameWorldInfoMessage(reliable: true);
                    foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
                        PublishHostSleepWakeState(player);
                }
                return;
            }
            if (m_hostSleepAccelerationSessionActive) return;

            m_hostSleepAccelerationSessionActive = true;
            // Source: Mod/ScMultiplayer/Modules/Player/
            // ScMultiplayerHealthWorldControlHandlers.cs:HandleGameWorldInfoMessage
            // Publish the rising edge reliably. Clients freeze at their current one-step timeline
            // and wait for the falling-edge snapshot instead of following accelerated host time.
            if (client?.IsConnected == true)
                SendGameWorldInfoMessage(reliable: true);
            foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
            {
                if (player?.ComponentSleep?.IsSleeping != true) continue;
                // ComponentSleep owns the vanilla per-player start time. Do not replace it with
                // a shared acceleration timestamp; the original 180-second/daylight rule uses
                // the time at which each player actually entered sleep.
                PublishHostSleepWakeState(player);
            }
        }

        private void DetachHostSleepWakeHandlers()
        {
            foreach (KeyValuePair<ComponentHealth, Action<ComponentCreature>> item in
                m_hostSleepWakeHandlers.ToArray())
                item.Key.Attacked -= item.Value;
            m_hostSleepWakeHandlers.Clear();
        }

        private void PublishHostSleepWakeState(ComponentPlayer player)
        {
            if (!IsHost || player == null || client?.IsConnected != true) return;
            if (!TryGetHostNetworkClientId(player, out int networkClientId)) return;
            SendAuthoritativePlayerHealth(networkClientId, player, force: true);
            m_hostObservedSleepStates[networkClientId] = player.ComponentSleep?.IsSleeping == true;
        }

        // Source: Mod/ScMultiplayer/Message/GamePlayerHealthMessage.cs:Write
        internal int GetNextAuthoritativePlayerStateSequence(int playerIndex)
        {
            if (!m_authoritativePlayerStateSequences.TryGetValue(playerIndex,
                out int sequence) || sequence == int.MaxValue)
                sequence = 0;
            sequence++;
            m_authoritativePlayerStateSequences[playerIndex] = sequence;
            return sequence;
        }

        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Update
        internal void PublishAuthoritativeCough(ComponentPlayer player)
        {
            if (!IsHost || client?.IsConnected != true || player == null) return;
            int playerClientId = 0;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (ReferenceEquals(item.Value?.ComponentPlayer, player))
                {
                    playerClientId = item.Key;
                    break;
                }
            }
            NetworkMessageSender.SendPlayerHealthMessage(playerClientId, player, 0f);
        }

        // Source: Survivalcraft/Game/VitalStatsWidget.cs:VitalStatsWidget.Update
        // Client-side UI damage is a request. The host accepts only a lower health value and
        // remains authoritative for the resulting health, events and death state.
        private void SendClientDamageRequest()
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            ComponentHealth health = localPlayer?.ComponentHealth;
            ComponentVitalStats vital = localPlayer?.ComponentVitalStats;
            if (health == null || vital == null) return;
            if (!m_hasObservedClientHealth)
            {
                m_hasObservedClientHealth = true;
                m_observedClientHealth = health.Health;
                m_observedClientFood = vital.Food;
                m_observedClientSleeping = localPlayer.ComponentSleep?.IsSleeping == true;
                return;
            }
            float change = health.Health - m_observedClientHealth;
            bool foodIncreased = vital.Food > m_observedClientFood + 0.0001f;
            bool isSleeping = localPlayer.ComponentSleep?.IsSleeping == true;
            bool sleepChanged = isSleeping != m_observedClientSleeping;
            int sleepRequestSequence = 0;
            if (sleepChanged && isSleeping)
                sleepRequestSequence = BeginClientSleepRequest();
            if (change < -0.0001f || foodIncreased || sleepChanged)
                NetworkMessageSender.SendPlayerHealthMessage(
                    client.ClientID, localPlayer, change,
                    foodIncreased ? "Client food request" : "Client state request",
                    sleepRequestSequence: sleepRequestSequence);
            m_observedClientHealth = health.Health;
            m_observedClientFood = vital.Food;
            m_observedClientSleeping = isSleeping;
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
        // Manual wake is a host-authoritative request. Do not clear the local sleep state before
        // the host ends acceleration and the client has applied the final circuit snapshot.
        internal bool RequestClientWakeUp(ComponentPlayer localPlayer)
        {
            if (IsHost || client?.IsConnected != true || localPlayer?.ComponentSleep == null ||
                !localPlayer.ComponentSleep.IsSleeping ||
                m_networkPlayerData.Values.Contains(localPlayer.PlayerData))
                return false;
            int sleepRequestSequence = BeginClientSleepRequest();
            NetworkMessageSender.SendPlayerHealthMessage(client.ClientID, localPlayer, 0f,
                "Client wake request", isSleepingOverride: false,
                sleepRequestSequence: sleepRequestSequence);
            return true;
        }

        private int BeginClientSleepRequest()
        {
            if (m_nextClientSleepRequestSequence == int.MaxValue)
                m_nextClientSleepRequestSequence = 0;
            m_pendingClientSleepRequestSequence = ++m_nextClientSleepRequestSequence;
            return m_pendingClientSleepRequestSequence;
        }

        // ====================================================================
        // 渲染远程玩家
        // ====================================================================
        private void RenderRemotePlayers()
        {
            if (!client.IsConnected || RemotePlayers.Count == 0) return;

            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            var players = subsystemPlayers.ComponentPlayers;
            if (players.Count == 0) return;

            // 获取本地玩家相机
            var localPlayer = players[0];
            var camera = localPlayer.GameWidget?.ActiveCamera;
            if (camera == null) return;

            // 延迟初始化 PrimitivesRenderer3D
            if (m_primitivesRenderer3D == null)
                m_primitivesRenderer3D = new PrimitivesRenderer3D();

            float cubeSize = 0.4f;
            var color = Color.White;
            double now = Time.RealTime;

            foreach (var kvp in RemotePlayers)
            {
                var state = kvp.Value;
                // 超过 5 秒没有更新, 跳过
                if (now - state.LastUpdateTime > 5.0) continue;

                Vector3 pos = state.Position;
                Vector3 offset = new Vector3(-cubeSize, 0, -cubeSize);
                Vector3 p1 = pos + new Vector3(-cubeSize, 0, -cubeSize);
                Vector3 p2 = pos + new Vector3(cubeSize, 0, -cubeSize);
                Vector3 p3 = pos + new Vector3(cubeSize, 2 * cubeSize, cubeSize);
                Vector3 p4 = pos + new Vector3(-cubeSize, 2 * cubeSize, cubeSize);

                var flatBatch = m_primitivesRenderer3D.FlatBatch();
                flatBatch.QueueQuad(p1, p2, p3, p4, color);
            }

            m_primitivesRenderer3D.Flush(camera.ViewProjectionMatrix);
        }

        // ====================================================================
        // Client_GameStep: 处理每 Tick 的网络事件
        // ====================================================================
        private void Client_GameStep(GameStepData obj)
        {
            // 加入
            foreach (var item in obj.Joins)
            {
                Log.Information($"[ScMP] Client joining: {item.ClientID}");
                PublishServerAudit("connection.request", item.ClientID, null);
                m_departedRemoteClientIds.Remove(item.ClientID);
                // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Handle
                // A single existing peer accepts or refuses a join. Only the room owner is allowed
                // to decide, otherwise another client can accept before the host requests a profile.
                if (!IsHost) continue;
                int joiningClientId = item.ClientID;
                IPEndPoint joiningAddress = item.Address;
                byte[] joinRequestBytes = item.JoinRequestBytes;
                QueueEndOfFrameAction(() => HandleHostJoinRequest(
                    joiningClientId,
                    joiningAddress,
                    joinRequestBytes));
            }

            // 输入消息
            if (m_messageRouter != null)
            {
                foreach (var item in obj.Inputs)
                    Client_DirectInput(item.ClientID, item.InputBytes);
            }
            else
            {
                // Source: Mod/ScMultiplayer/Control/NetworkMessageRouter.cs:
                // NetworkMessageRouter.Route
                // Keep the compatibility switch only as an initialization fallback. Normal
                // traffic always enters the router created during OnLoad.
                foreach (var item in obj.Inputs)
                {
                    if (!NetworkMessageIngress.TryDecode(item.InputBytes, client.Address.Port,
                        out Message message, out string decodeError))
                    {
                        if (!string.IsNullOrEmpty(decodeError))
                            Log.Error($"[ScMP] Failed to parse message: ClientID={item.ClientID}, " +
                                decodeError);
                        continue;
                    }

                    switch (message)
                    {
                    case SyncBatchMessage syncBatch:
                        foreach (byte[] payload in syncBatch.Payloads)
                        {
                            try
                            {
                                if (Message.Read(payload) is SyncBatchMessage)
                                    throw new InvalidOperationException("Nested sync batch is not allowed.");
                                Client_DirectInput(item.ClientID, payload);
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"[ScMP] Failed to unpack sync batch item: {ex.Message}");
                            }
                        }
                        break;
                    case ChatMessage chat:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandleChatMessage(chat, item.ClientID));
                        break;
                    case GamePlayerPositionMessage pos:
                        QueueEndOfFrameAction(() => HandleGamePlayerPositionMessage(pos, item.ClientID));
                        break;
                    case GamePlayerPositionsMessage positions:
                        QueueEndOfFrameAction(() =>
                        {
                            if (positions.Players == null) return;
                            foreach (GamePlayerPositionMessage position in positions.Players)
                                HandleGamePlayerPositionMessage(position, item.ClientID);
                        });
                        break;
                    case GamePlayerInputMessage playerInput:
                        QueueEndOfFrameAction(() => HandleGamePlayerInputMessage(
                            playerInput, item.ClientID));
                        break;
                    case MountActionMessage mountAction:
                        QueuePriorityInputAction(() => HandleMountActionMessage(
                            mountAction, item.ClientID));
                        break;
                    case MountStateMessage mountState:
                        QueuePriorityInputAction(() => HandleMountStateMessage(
                            mountState, item.ClientID));
                        break;
                    case PlayerAimMessage playerAim:
                        QueueEndOfFrameAction(() => HandlePlayerAimMessage(playerAim, item.ClientID));
                        break;
                    case PlayerActionMessage playerAction:
                        if (playerAction.Action == PlayerActionType.JumpRequest)
                            QueuePriorityInputAction(() =>
                                HandlePlayerActionMessage(playerAction, item.ClientID));
                        else
                            QueueEndOfFrameAction(() =>
                                HandlePlayerActionMessage(playerAction, item.ClientID));
                        break;
                    case TerrainDigRequestMessage terrainDigRequest:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainDigRequest(terrainDigRequest, item.ClientID));
                        break;
                    case TerrainDigResultMessage terrainDigResult:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainDigResult(terrainDigResult, item.ClientID));
                        break;
                    case DigPresentationMessage digPresentation:
                        QueueEndOfFrameAction(() => HandleDigPresentationMessage(
                            digPresentation, item.ClientID));
                        break;
                    case GameModifiedCellsMessage cells:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandleModifiedCellsMessage(cells, item.ClientID));
                        break;
                    case TerrainRecoveryMessage terrainRecovery:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainRecoveryMessage(terrainRecovery, item.ClientID));
                        break;
                    case TerrainChunkSyncMessage terrainChunkSync:
                        m_terrainChunkSyncActions.Enqueue(new QueuedTerrainChunkSync
                        {
                            Message = terrainChunkSync,
                            SourceClientId = item.ClientID,
                            EnqueuedTimestamp = Stopwatch.GetTimestamp()
                        });
                        break;
                    case GameWorldInfoMessage1 worldInfo:
                        if (item.ClientID == 0)
                            Dispatcher.Dispatch(() =>
                                NetworkMessageHandler.HandleWorldInfoMessage(worldInfo, item.ClientID));
                        break;
                    case WorldControlRequestMessage worldControl:
                        QueueEndOfFrameAction(() => HandleWorldControlRequest(worldControl, item.ClientID));
                        break;
                    case WorldControlResultMessage worldControlResult:
                        QueueEndOfFrameAction(() => HandleWorldControlResult(
                            worldControlResult, item.ClientID));
                        break;
                    case PlayerProfileMessage playerProfile:
                        QueueEndOfFrameAction(() => HandlePlayerProfileMessage(playerProfile, item.ClientID));
                        break;
                    case PlayerSkinAssetMessage playerSkinAsset:
                        QueueEndOfFrameAction(() => HandlePlayerSkinAssetMessage(
                            playerSkinAsset, item.ClientID));
                        break;
                    case PlayerEquipmentMessage playerEquipment:
                        QueueEndOfFrameAction(() => HandlePlayerEquipmentMessage(
                            playerEquipment, item.ClientID));
                        break;
                    case EditableDataRequestMessage editableDataRequest:
                        QueueEndOfFrameAction(() => HandleEditableDataRequest(
                            editableDataRequest, item.ClientID));
                        break;
                    case EditableDataStateMessage editableDataState:
                        QueueEndOfFrameAction(() => HandleEditableDataState(
                            editableDataState, item.ClientID));
                        break;
                    case CircuitSyncMessage circuitSync:
                        QueueEndOfFrameAction(() => m_circuitSynchronizer?.HandleMessage(
                            circuitSync, item.ClientID));
                        break;
                    case WorldObjectSyncMessage worldObjectSync:
                        QueueEndOfFrameAction(() => m_worldObjectSynchronizer?.HandleMessage(
                            worldObjectSync, item.ClientID));
                        break;
                    case GamePakWorldMessage pakWorld:
                        if (item.ClientID == 0)
                            QueueWorldTransferAction(() =>
                                NetworkMessageHandler.HandlePakWorldMessage(pakWorld, item.ClientID));
                        break;
                    case GamePakWorldChunkMessage worldChunk:
                        if (item.ClientID == 0)
                            QueueWorldTransferAction(() => HandleGamePakWorldChunkMessage(worldChunk));
                        break;
                    case GamePakWorldReadyMessage worldReady:
                        QueueWorldTransferAction(() =>
                            HandleGamePakWorldReadyMessage(worldReady, item.ClientID));
                        break;
                    case GamePakWorldRepairRequestMessage repairRequest:
                        QueueWorldTransferAction(() =>
                            HandleGamePakWorldRepairRequestMessage(repairRequest, item.ClientID));
                        break;
                    case GamePlayerHealthMessage health:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandlePlayerHealthMessage(health, item.ClientID));
                        break;
                    case GameKickPlayerMessage kick:
                        QueueEndOfFrameAction(() => HandleGameKickPlayerMessage(kick, item.ClientID));
                        break;
                    case EntityMessage entityMessage:
                        QueueEndOfFrameAction(() => HandleAnimalEntityMessage(entityMessage, item.ClientID));
                        break;
                    case BodyUpdateMessage bodyUpdate:
                        QueueEndOfFrameAction(() => HandleAnimalBodyUpdate(bodyUpdate, item.ClientID));
                        break;
                    case AnimalInteractionMessage animalInteraction:
                        QueueEndOfFrameAction(() => HandleAnimalInteractionMessage(
                            animalInteraction, item.ClientID));
                        break;
                    case AnimalSoundMessage animalSound:
                        QueueEndOfFrameAction(() => HandleAnimalSoundMessage(
                            animalSound, item.ClientID));
                        break;
                    case MeleeHitResultMessage meleeHitResult:
                        QueueEndOfFrameAction(() => HandleMeleeHitResultMessage(
                            meleeHitResult, item.ClientID));
                        break;
                    case PickableSyncMessage pickableSync:
                        QueueEndOfFrameAction(() => HandlePickableSyncMessage(pickableSync, item.ClientID));
                        break;
                    case ProjectileSyncMessage projectileSync:
                        QueueEndOfFrameAction(() => HandleProjectileSyncMessage(projectileSync, item.ClientID));
                        break;
                    case ExplosionSyncMessage explosionSync:
                        QueueEndOfFrameAction(() => HandleExplosionSyncMessage(explosionSync, item.ClientID));
                        break;
                    case ContainerSyncMessage containerSync:
                        QueueEndOfFrameAction(() => HandleContainerSyncMessage(containerSync, item.ClientID));
                        break;
                        default:
                            Log.Error($"[ScMP] Unknown message type: {message.GetType().Name}");
                            break;
                    }
                }
            }

            // Source: Comms/Comms.Drt/Data/GameStepData.cs:GameStepData.Inputs
            // A transport leave can share a GameStep with the peer's final already-received
            // reliable messages. Queue removal only after those messages so host-authoritative
            // equipment and container changes are applied before the player record is captured.
            foreach (var item in obj.Leaves)
            {
                Log.Information($"[ScMP] Client left: {item.ClientID}");
                PublishServerAudit("connection.leave", item.ClientID, null);
                if (!IsHost && item.ClientID == 0)
                {
                    HandleHostDisconnected();
                    continue;
                }
                int departedClientId = item.ClientID;
                m_controlUnit?.Context.Connections.MarkDisconnected(
                    departedClientId, Time.RealTime);
                QueueEndOfFrameAction(() =>
                {
                    m_circuitSynchronizer?.NotifyClientDeparted(departedClientId);
                    if (!IsHost)
                        m_departedRemoteClientIds.Add(departedClientId);
                    RemoveNetworkPlayer(departedClientId);
                    playerMappingManager.ReleasePlayerIndex(departedClientId);
                });
            }
        }

        // Source: Comms.Drt/Func/Client/Client.cs:Client.DirectInput
        // Reuse the normal message dispatcher while keeping direct network callbacks away from
        // game objects. Individual handlers enqueue their work on Frame.Update.
        private void Client_DirectInput(int sourceClientId, byte[] inputBytes)
        {
            if (!NetworkMessageIngress.TryDecode(inputBytes, client.Address.Port,
                out Message message, out string decodeError))
            {
                if (!string.IsNullOrEmpty(decodeError))
                    Log.Error($"[ScMP] Failed to parse message: ClientID={sourceClientId}, " +
                        decodeError);
                return;
            }

            (m_messageRouter ??= new NetworkMessageRouter(this)).Route(
                sourceClientId, message, inputBytes?.Length ?? 0);
        }

        // Source: Mod/Comms/Comms.Drt/Data/GameStepData.cs:GameStepData.JoinData
        private void HandleHostJoinRequest(
            int joiningClientId,
            IPEndPoint joiningAddress,
            byte[] joinRequestBytes)
        {
            if (!IsHost || m_hostJoinRequests.ContainsKey(joiningClientId))
                return;

            // Source: Mod/ScMultiplayer/Message/GameWorldInfoMessage.cs:
            // GameWorldInfoMessage.Read
            // Reject an incompatible multiplayer Mod before it can reserve a player slot.
            GameWorldInfoMessage worldInfo;
            try
            {
                worldInfo = Message.Read(joinRequestBytes) as GameWorldInfoMessage;
            }
            catch (Exception ex)
            {
                client.RefuseJoinGame(joiningClientId,
                    "Invalid join request: " + ex.Message);
                Log.Error($"[ScMP] Failed to parse ClientID {joiningClientId} join: " +
                    ex.Message);
                return;
            }
            if (worldInfo == null)
            {
                client.RefuseJoinGame(joiningClientId, "Invalid join request type");
                return;
            }
            if (!Message.IsProtocolCompatible(worldInfo.MultiplayerModVersion,
                    worldInfo.MultiplayerProtocolVersion,
                    worldInfo.MultiplayerProtocolHash,
                    worldInfo.MultiplayerBuildFingerprint))
            {
                string remoteProtocol = Message.GetProtocolLabel(
                    worldInfo.MultiplayerModVersion,
                    worldInfo.MultiplayerProtocolVersion,
                    worldInfo.MultiplayerProtocolHash,
                    worldInfo.MultiplayerBuildFingerprint);
                string hostProtocol = Message.GetProtocolLabel(
                    Message.ModVersion, Message.ProtocolVersion,
                    Message.ProtocolHash, Message.BuildFingerprint);
                string reason = $"{ProtocolMismatchReasonPrefix}: " +
                    $"host={hostProtocol}, client={remoteProtocol}";
                Log.Warning($"[ScMP] Refused incompatible ClientID {joiningClientId}: " +
                    $"host={hostProtocol}, client={remoteProtocol}");
                client.RefuseJoinGame(joiningClientId, reason);
                return;
            }

            if (!TryReserveNetworkPlayerIndex(joiningClientId,
                out int assignedPlayerIndex))
            {
                Log.Information($"[ScMP] Game full, refusing ClientID {joiningClientId}");
                client.RefuseJoinGame(joiningClientId, "Game is full");
                return;
            }
            if (playerMappingManager.AssignPlayerIndex(joiningClientId) == -1)
            {
                m_reservedNetworkPlayerIndices.Remove(joiningClientId);
                Log.Information($"[ScMP] Game full, refusing ClientID {joiningClientId}");
                client.RefuseJoinGame(joiningClientId, "Game is full");
                return;
            }
            m_controlUnit?.Context.Connections.Register(
                joiningClientId,
                assignedPlayerIndex,
                joiningAddress?.ToString(),
                isHost: false,
                Core.PlayerConnectionPhase.Reserved,
                Time.RealTime);

            try
            {
                if (SuPlayScreen.WorldData == null ||
                    SuPlayScreen.WorldDataName != worldInfo.Name ||
                    SuPlayScreen.WorldDataLastSaveTime != worldInfo.LastSaveTime)
                {
                    m_controlUnit?.Context.Connections.MarkDisconnected(joiningClientId, Time.RealTime);
                    m_reservedNetworkPlayerIndices.Remove(joiningClientId);
                    playerMappingManager.ReleasePlayerIndex(joiningClientId);
                    client.RefuseJoinGame(joiningClientId, "Host world snapshot is unavailable");
                    return;
                }

                EnsurePlayerRecordsLoaded();
                string recordKey = PlayerRecordKeyResolver.GetPlayerRecordKey(
                    worldInfo.PlayerIdentity,
                    worldInfo.PlayerName);
                bool isNewApproval = !m_playerRecords.TryGetValue(
                    recordKey,
                    out NetworkPlayerRecord joiningRecord);
                if (isNewApproval)
                {
                    if (!IsValidRequestedProfile(worldInfo))
                    {
                        m_controlUnit?.Context.Connections.MarkDisconnected(joiningClientId, Time.RealTime);
                        m_reservedNetworkPlayerIndices.Remove(joiningClientId);
                        playerMappingManager.ReleasePlayerIndex(joiningClientId);
                        client.RefuseJoinGame(joiningClientId, PlayerProfileRequiredReason);
                        return;
                    }
                    joiningRecord = CreateInitialPlayerRecord(worldInfo);
                }

                var request = new HostJoinRequest
                {
                    ClientId = joiningClientId,
                    Address = joiningAddress,
                    RecordKey = recordKey,
                    PlayerRecord = joiningRecord,
                    IsNewApproval = isNewApproval,
                    ReceivedTime = Time.RealTime
                };
                Log.Information($"[ScMP] Reserved PlayerIndex {assignedPlayerIndex} for " +
                    $"ClientID {joiningClientId} ({joiningRecord.Name})");

                if (ScMultiplayerSettings.AutoApproveJoinRequests)
                {
                    ApproveHostJoinRequest(request);
                    return;
                }

                m_hostJoinRequests.Add(joiningClientId, request);
                TryShowNextHostJoinRequest();
            }
            catch (Exception ex)
            {
                m_controlUnit?.Context.Connections.MarkDisconnected(joiningClientId, Time.RealTime);
                m_reservedNetworkPlayerIndices.Remove(joiningClientId);
                playerMappingManager.ReleasePlayerIndex(joiningClientId);
                try
                {
                    client.RefuseJoinGame(joiningClientId, "Invalid join request: " + ex.Message);
                }
                catch
                {
                }
                Log.Error($"[ScMP] Failed to process ClientID {joiningClientId} join: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.MaxPlayers
        // MaxPlayers is a local split-screen count, not an index range. Network avatars are
        // detached from PlayersData, so admission and the actual PlayerIndex reservation must be
        // maintained together by the multiplayer host.
        private bool TryReserveNetworkPlayerIndex(int clientId, out int playerIndex)
        {
            if (m_reservedNetworkPlayerIndices.TryGetValue(clientId, out playerIndex))
                return true;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null)
            {
                playerIndex = -1;
                return false;
            }
            int playerCount = players.PlayersData.Count +
                m_networkPlayerData.Count(item => item.Key > 0) +
                m_reservedNetworkPlayerIndices.Count;
            if (playerCount >= ScMultiplayerSettings.MaxPlayers)
            {
                playerIndex = -1;
                return false;
            }
            playerIndex = FindAvailableNetworkPlayerIndex(players);
            m_reservedNetworkPlayerIndices[clientId] = playerIndex;
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.AddPlayerData
        private int FindAvailableNetworkPlayerIndex(SubsystemPlayers players)
        {
            var used = new HashSet<int>(players.PlayersData.Select(item => item.PlayerIndex));
            used.UnionWith(m_networkPlayerData.Values.Select(item => item.PlayerIndex));
            used.UnionWith(m_reservedNetworkPlayerIndices.Values);
            int playerIndex = 0;
            while (used.Contains(playerIndex)) playerIndex++;
            return playerIndex;
        }

        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.Dialogs
        private void UpdateHostJoinRequests()
        {
            DeferDismissedHostJoinDecision();

            if (!IsHost || m_hostJoinRequests.Count == 0)
                return;

            HostJoinRequest[] expired = m_hostJoinRequests.Values
                .Where(request => Time.RealTime - request.ReceivedTime >= 285.0)
                .ToArray();
            foreach (HostJoinRequest request in expired)
                RejectHostJoinRequest(request, "Host approval timed out.");

            if (ScMultiplayerSettings.AutoApproveJoinRequests)
            {
                foreach (HostJoinRequest request in m_hostJoinRequests.Values.ToArray())
                    ApproveHostJoinRequest(request);
            }
            else
            {
                TryShowNextHostJoinRequest();
            }
        }

        private void TryShowNextHostJoinRequest()
        {
            DeferDismissedHostJoinDecision();
            if (!IsHost || ScMultiplayerSettings.AutoApproveJoinRequests ||
                m_activeJoinDecisionDialog != null)
            {
                return;
            }
            HostJoinRequest request = m_hostJoinRequests.Values
                .Where(item => !item.Deferred)
                .OrderBy(item => item.ReceivedTime)
                .FirstOrDefault();
            if (request != null)
                ShowHostJoinDecision(request);
        }

        // Source: Survivalcraft/Game/ListSelectionDialog.cs:ListSelectionDialog.Update
        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.HideDialog
        // An outside click dismisses ListSelectionDialog without invoking its selection callback.
        // Finalize that path before reopening MP request UI so the same transport request remains
        // pending and can be approved later instead of retaining a stale active-dialog marker.
        private void DeferDismissedHostJoinDecision()
        {
            if (m_activeJoinDecisionDialog == null ||
                DialogsManager.Dialogs.Contains(m_activeJoinDecisionDialog))
                return;
            if (m_hostJoinRequests.TryGetValue(
                m_activeJoinDecisionClientId, out HostJoinRequest dismissed))
            {
                dismissed.Deferred = true;
            }
            m_activeJoinDecisionDialog = null;
            m_activeJoinDecisionClientId = -1;
        }

        // Source: Survivalcraft/Game/ListSelectionDialog.cs:ListSelectionDialog
        private void ShowHostJoinDecision(HostJoinRequest request)
        {
            if (request == null || !m_hostJoinRequests.ContainsKey(request.ClientId))
                return;

            string[] decisions = { "Allow", "Reject", "Decide" };
            var dialog = new ListSelectionDialog(
                "Join Request: " + GetHostJoinRequestLabel(request),
                decisions,
                60f,
                item => item.ToString(),
                item =>
                {
                    m_activeJoinDecisionDialog = null;
                    m_activeJoinDecisionClientId = -1;
                    string decision = item?.ToString();
                    if (decision == decisions[0])
                        ApproveHostJoinRequest(request);
                    else if (decision == decisions[1])
                        RejectHostJoinRequest(request, "Host declined the join request.");
                    else
                    {
                        request.Deferred = true;
                        TryShowNextHostJoinRequest();
                    }
                });
            m_activeJoinDecisionDialog = dialog;
            m_activeJoinDecisionClientId = request.ClientId;
            // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.HasDialogs
            // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.UpdateInputFromMouseAndKeyboard
            // Attach to the local host player's GUI so the stock input guard treats the join
            // approval list as a gameplay modal and prevents mouse/keyboard passthrough.
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                player?.PlayerData != null && !m_networkPlayerData.Values.Contains(player.PlayerData));
            DialogsManager.ShowDialog(localPlayer?.GuiWidget ?? ScreensManager.RootWidget, dialog);
        }

        private void ApproveHostJoinRequest(HostJoinRequest request)
        {
            if (request == null)
                return;
            m_hostJoinRequests.Remove(request.ClientId);
            CloseActiveJoinDecision(request.ClientId);
            AcceptNetworkPlayerJoin(
                request.ClientId,
                request.RecordKey,
                request.PlayerRecord,
                request.IsNewApproval);
            TryShowNextHostJoinRequest();
        }

        private void RejectHostJoinRequest(HostJoinRequest request, string reason)
        {
            if (request == null)
                return;
            m_hostJoinRequests.Remove(request.ClientId);
            CloseActiveJoinDecision(request.ClientId);
            m_reservedNetworkPlayerIndices.Remove(request.ClientId);
            m_controlUnit?.Context.Connections.MarkDisconnected(request.ClientId, Time.RealTime);
            playerMappingManager.ReleasePlayerIndex(request.ClientId);
            PublishServerAudit("join.rejected", request.ClientId, null);
            try
            {
                client.RefuseJoinGame(request.ClientId, reason);
            }
            catch (Exception ex)
            {
                Log.Warning($"[ScMP] Could not refuse ClientID {request.ClientId}: {ex.Message}");
            }
            TryShowNextHostJoinRequest();
        }

        private void CloseActiveJoinDecision(int clientId)
        {
            if (m_activeJoinDecisionClientId != clientId)
                return;
            Dialog dialog = m_activeJoinDecisionDialog;
            m_activeJoinDecisionDialog = null;
            m_activeJoinDecisionClientId = -1;
            if (dialog != null && DialogsManager.Dialogs.Contains(dialog))
                DialogsManager.HideDialog(dialog);
        }

        private static string GetHostJoinRequestLabel(HostJoinRequest request)
        {
            string name = string.IsNullOrWhiteSpace(request?.PlayerRecord?.Name)
                ? "Player"
                : request.PlayerRecord.Name;
            return request?.Address == null ? name : name + " | " + request.Address;
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.AcceptJoinGame
        private void AcceptNetworkPlayerJoin(int joiningClientId, string recordKey,
            NetworkPlayerRecord joiningRecord, bool isNewApproval = false)
        {
            try
            {
                m_controlUnit?.Context.Connections.TryTransition(
                    joiningClientId, Core.PlayerConnectionPhase.Joining, Time.RealTime);
                m_lastSentInventoryValues.Remove(joiningClientId);
                m_lastSentInventoryCounts.Remove(joiningClientId);
                m_playerRecords[recordKey] = joiningRecord;
                RegisterPlayerSkinHash(joiningClientId, joiningRecord);
                RequestSkinAssetIfMissing(joiningClientId, joiningRecord?.SkinName,
                    joiningRecord?.PlayerClass ?? PlayerClass.Male,
                    joiningRecord?.SkinSha256);
                m_pendingAcceptedJoinKeys[joiningClientId] = recordKey;
                m_forceContainerFullSync = true;
                m_playerRecordsDirty = true;
                SavePlayerRecords();
                var joinJournal = new JoinCatchUpJournal
                {
                    StartTick = client.Step
                };
                m_joinCatchUpRegistry.Journals[joiningClientId] = joinJournal;
                HostedWorldSnapshot snapshot = CaptureHostedWorldSnapshot();
                // Source: ScMultiplayer.CaptureHostedWorldSnapshot
                // Changes through this tick are already inside the exported archive.
                joinJournal.StartTick = snapshot.Tick;
                // Source: Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.SendDataMessageToAllClients
                // Keep large direct broadcasts off this endpoint until its world is loaded.
                // Ordered ticks continue so the joining client's expected step stays current.
                SetServerClientGameTrafficEnabled(joiningClientId, enabled: false);
                client.AcceptJoinGame(joiningClientId);
                BeginWorldTransfer(
                    snapshot.Name, snapshot.WorldData, snapshot.LastSaveTime, joiningClientId,
                    m_sessionRandomSeed, snapshot.TerrainSequence,
                    snapshot.RandomStates, joiningRecord);
                Log.Information($"[ScMP] Accepted ClientID {joiningClientId} and queued live world snapshot " +
                    $"(Tick={snapshot.Tick}, Bytes={snapshot.WorldData.Length})");
                PublishServerAudit("join.snapshot_queued", joiningClientId,
                    "bytes=" + snapshot.WorldData.Length.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                SetServerClientGameTrafficEnabled(joiningClientId, enabled: true);
                RemoveNetworkPlayer(joiningClientId);
                if (isNewApproval)
                {
                    m_playerRecords.Remove(recordKey);
                    m_playerRecordsDirty = true;
                    SavePlayerRecords();
                }
                playerMappingManager.ReleasePlayerIndex(joiningClientId);
                client.RefuseJoinGame(joiningClientId, "Failed to prepare player: " + ex.Message);
                Log.Error($"[ScMP] Failed to accept ClientID {joiningClientId}: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.SaveProject
        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ExportWorld
        private HostedWorldSnapshot CaptureHostedWorldSnapshot()
        {
            Project project = GameManager.Project ??
                throw new InvalidOperationException("The hosted project is not loaded.");
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            // Source: ScMultiplayer.FlushPendingTerrainBroadcasts
            // The exported host world is the joining client's complete terrain baseline. Seal
            // current revision metadata first; any later terrain sequence is join catch-up.
            EnsureHostTerrainSyncStateLoaded();
            FlushPendingTerrainBroadcasts();
            MergePendingTerrainChanges();
            SaveHostTerrainSyncState();
            GameManager.SaveProject(waitForCompletion: true, showErrorDialog: false);

            string snapshotDirectory = Storage.CombinePaths(
                Storage.GetDirectoryName(gameInfo.DirectoryName),
                ".ScMpJoinSnapshot-" + Guid.NewGuid().ToString("N"));
            string snapshotSystemPath = Storage.GetSystemPath(snapshotDirectory);
            byte[] exportedWorld;
            try
            {
                WorldSnapshotFileCopier.CopyDirectory(
                    Storage.GetSystemPath(gameInfo.DirectoryName), snapshotSystemPath);
                using var stream = new MemoryStream();
                WorldsManager.ExportWorld(snapshotDirectory, stream);
                exportedWorld = stream.ToArray();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(snapshotSystemPath))
                        Directory.Delete(snapshotSystemPath, recursive: true);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[ScMP] Failed to remove temporary join snapshot: {ex.Message}");
                }
            }

            var networkPlayerIndices = new HashSet<string>(m_networkPlayerData.Values
                .Where(player => player != null)
                .Select(player => player.PlayerIndex.ToString(CultureInfo.InvariantCulture)));
            byte[] sanitizedWorld = WorldArchiveSanitizer.RemoveNetworkPlayers(
                exportedWorld, networkPlayerIndices);
            WorldInfo worldInfo = WorldsManager.GetWorldInfo(gameInfo.DirectoryName);
            return new HostedWorldSnapshot
            {
                Name = worldInfo?.WorldSettings?.Name ?? SuPlayScreen.WorldDataName,
                WorldData = sanitizedWorld,
                LastSaveTime = worldInfo?.LastSaveTime ?? DateTime.Now,
                Tick = client.Step,
                TerrainSequence = m_hostTerrainSequence,
                RandomStates = CaptureSubsystemRandomStates()
            };
        }

        // Source: RuthlessConquest/Net/Client.cs:Client.Client
        // The world uses Comms reliable UDP with an application-level sliding window and repair
        // requests. A full SHA-256 keeps the independently delivered chunks end-to-end verifiable.
        private void BeginWorldTransfer(string name, byte[] worldData, DateTime lastSaveTime,
            int targetClientId, int randomSeed, long terrainSequence,
            Dictionary<string, long> randomStates, NetworkPlayerRecord playerRecord)
        {
            if (worldData == null || worldData.Length == 0 ||
                worldData.Length > MaximumWorldTransferSize)
                throw new InvalidOperationException("Cached world data has an invalid size.");
            m_nextWorldTransferId = m_nextWorldTransferId == int.MaxValue
                ? 1
                : m_nextWorldTransferId + 1;
            int transferId = m_nextWorldTransferId;
            int chunkCount = (worldData.Length + WorldTransferChunkSize - 1) /
                WorldTransferChunkSize;
            var manifest = new GamePakWorldMessage(name, Array.Empty<byte>(), lastSaveTime,
                targetClientId, randomSeed, randomStates, playerRecord)
            {
                TransferId = transferId,
                ChunkCount = chunkCount,
                TotalLength = worldData.Length,
                WorldSha256 = SHA256.HashData(worldData),
                TerrainSequenceBaseline = terrainSequence
            };
            m_worldTransferRegistry.OutgoingTransfers[targetClientId] = new OutgoingWorldTransfer
            {
                TransferId = transferId,
                TargetClientId = targetClientId,
                StartTime = Time.RealTime,
                WorldData = worldData,
                ChunkCount = chunkCount,
                Manifest = manifest,
                ChunkLastQueueTimes = new double[chunkCount]
            };
            m_joinCatchUpRegistry.TransfersAwaitingReady[targetClientId] = transferId;
            m_joinCatchUpRegistry.HostProjectReadyTransfers.Remove(targetClientId);
            m_joinCatchUpRegistry.CompletedReadyTransfers.Remove(targetClientId);
            // The joining peer requests the manifest after ConnectAccepted has been processed.
            // Sending it here can be ACKed by Comm and then discarded by Peer because the
            // application connection is not established yet.
        }

        // Source: NetworkMessageSender.SendScheduledMessage
        // Joining peers receive the captured journal/checkpoint only. Sending normal broadcasts to
        // them as well fills the same reliable window and can strand the join completion marker.
        internal bool RecordJoinCatchUpMessage(byte[] payload, bool sequenced, bool latest,
            bool recordPayload = true)
        {
            if (!IsHost || payload == null || payload.Length == 0 ||
                m_joinCatchUpRegistry.Journals.Count == 0)
                return false;
            // Source: NetworkMessageSender.SendScheduledMessage
            // Latest-state position/body samples are immediately refreshed after normal traffic
            // is enabled. Recording every sample here creates a large obsolete replay burst and
            // delays the reliable join completion marker without adding authoritative history.
            if (latest || !recordPayload) return true;
            foreach (JoinCatchUpJournal journal in m_joinCatchUpRegistry.Journals.Values)
            {
                if (journal.TotalBytes + payload.Length > MaximumJoinCatchUpBytes)
                {
                    journal.DroppedMessages++;
                    continue;
                }
                byte[] copy = new byte[payload.Length];
                Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
                var item = new JoinCatchUpMessage
                {
                    Payload = copy,
                    Sequenced = sequenced,
                    Latest = latest
                };
                // Source: ScMultiplayer.cs:ScMultiplayer.SealAndSendJoinCatchUp
                // Once the cutoff is sealed, new live messages wait in a second buffer and can
                // no longer extend the acknowledged catch-up batch indefinitely.
                if (journal.CutoffSealed)
                    journal.PostCutoffMessages.Add(item);
                else
                    journal.Messages.Add(item);
                journal.TotalBytes += copy.Length;
            }
            return true;
        }

        // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.SendDataMessageToAllClients
        // The normal broadcast route cannot exclude a single peer. During join, emit one copy to
        // each ready peer and leave the joining peer's authoritative state in its bounded journal.
        internal bool SendLiveBroadcastToReadyClients(byte[] payload, bool sequenced, bool latest)
        {
            if (!IsHost || client == null || payload == null || payload.Length == 0 ||
                m_joinCatchUpRegistry.Journals.Count == 0)
                return false;
            foreach (ServerClient remote in GetConnectedRemoteClients())
            {
                if (remote.ClientID <= 0 || m_joinCatchUpRegistry.Journals.ContainsKey(remote.ClientID))
                    continue;
                NetworkMessageSender.SendRawPayload(remote.ClientID, payload, sequenced, latest);
            }
            return true;
        }

        private void FlushJoinCatchUpJournal(int targetClientId)
        {
            if (!m_joinCatchUpRegistry.Journals.TryGetValue(targetClientId,
                out JoinCatchUpJournal journal))
                return;
            JoinCatchUpMessage[] batch = journal.Messages.ToArray();
            journal.Messages.Clear();
            journal.TotalBytes = 0;
            journal.ReplayRound++;
            PendingJoinCatchUp pending = GetOrCreatePendingJoinCatchUp(targetClientId);
            foreach (JoinCatchUpMessage item in batch)
            {
                if (item?.Payload == null) continue;
                pending.Messages.Enqueue(item);
            }
            Log.Information($"[ScMP] Join catch-up batch queued: ClientID={targetClientId}, " +
                $"Round={journal.ReplayRound}, StartTick={journal.StartTick}, " +
                $"Messages={batch.Length}, Bytes={batch.Sum(item => item?.Payload?.Length ?? 0)}, " +
                $"Dropped={journal.DroppedMessages}");
            if (journal.DroppedMessages > 0)
                Log.Warning($"[ScMP] Join catch-up limit reached for ClientID={targetClientId}; " +
                    $"{journal.DroppedMessages} transient messages were replaced by subsequent full-state sync.");
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.RecordJoinCatchUpMessage
        private void DrainPostCutoffJournal(int targetClientId, JoinCatchUpJournal journal)
        {
            JoinCatchUpMessage[] batch = journal.PostCutoffMessages.ToArray();
            journal.PostCutoffMessages.Clear();
            journal.TotalBytes = 0;
            PendingJoinCatchUp pending = GetOrCreatePendingJoinCatchUp(targetClientId);
            foreach (JoinCatchUpMessage item in batch)
            {
                if (item?.Payload == null) continue;
                pending.Messages.Enqueue(item);
            }
            Log.Information($"[ScMP] Join post-cutoff batch queued: ClientID={targetClientId}, " +
                $"Messages={batch.Length}, Bytes={batch.Sum(item => item?.Payload?.Length ?? 0)}, " +
                $"Dropped={journal.DroppedMessages}");
        }

        private PendingJoinCatchUp GetOrCreatePendingJoinCatchUp(int targetClientId)
        {
            if (!m_joinCatchUpRegistry.Pending.TryGetValue(targetClientId, out PendingJoinCatchUp pending))
            {
                pending = new PendingJoinCatchUp { TargetClientId = targetClientId };
                m_joinCatchUpRegistry.Pending.Add(targetClientId, pending);
            }
            return pending;
        }

        private void QueueJoinCatchUpPayload(int targetClientId, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            GetOrCreatePendingJoinCatchUp(targetClientId).Messages.Enqueue(
                new JoinCatchUpMessage { Payload = payload, Sequenced = true });
        }

        // Source: ScMultiplayer.cs:SendPendingWorldTransferChunks
        // Catch-up uses the same spare-bandwidth bucket as the initial world archive. Completion
        // remains ordered after the last catch-up payload on the reliable transport, but must not
        // wait for the application's bulk-window estimate to fall below its limit. That extra
        // gate can strand the completion marker while the client is already waiting in recovery.
        private void SendPendingJoinCatchUps()
        {
            if (m_joinCatchUpRegistry.Pending.Count == 0) return;
            int budget = GetJoinTransferSendBudget(
                m_networkPlayerData.Any(item => item.Key > 0));
            int[] targetClientIds = m_joinCatchUpRegistry.Pending.Keys.OrderBy(id => id).ToArray();
            int attempts = targetClientIds.Length * (budget + 1);
            int cursor = m_worldTransferCursor % Math.Max(1, targetClientIds.Length);
            while (budget > 0 && attempts-- > 0 && targetClientIds.Length > 0)
            {
                int targetClientId = targetClientIds[cursor];
                cursor = (cursor + 1) % targetClientIds.Length;
                if (!m_joinCatchUpRegistry.Pending.TryGetValue(targetClientId, out PendingJoinCatchUp pending))
                    continue;
                if (pending.Messages.Count == 0)
                {
                    m_joinCatchUpRegistry.Pending.Remove(targetClientId);
                    pending.CompletionAction?.Invoke();
                    continue;
                }

                JoinCatchUpMessage item = pending.Messages.Peek();
                if (item?.Payload == null)
                {
                    pending.Messages.Dequeue();
                    continue;
                }
                int estimatedPackets = EstimateReliableRelayPackets(item.Payload.Length);
                int unackedPackets = GetWorldTransferRelayUnackedPackets(targetClientId);
                int joinWindow = GetWorldTransferUnackedPacketLimit(targetClientId);
                // Source: Mod/Comms/Comms/Comm.cs:Comm.SendMessages
                // A reliable message can occupy several 1024-byte UDP packets. Reserve its full
                // estimated packet count before queueing it, otherwise one weather-driven terrain
                // checkpoint can jump past the join window and strand the completion marker.
                bool oversizedMessageCanStart = estimatedPackets > joinWindow &&
                    unackedPackets == 0;
                if (!oversizedMessageCanStart &&
                    unackedPackets + estimatedPackets > joinWindow)
                    continue;
                if (!TryReserveJoinTransferBytes(null, item.Payload.Length))
                    break;
                // Leave the final join-control packet room in the critical reserve.
                if (!TryReserveReliableRelayPackets(targetClientId, estimatedPackets,
                        joinCritical: false))
                {
                    RefundJoinTransferBytes(null, item.Payload.Length);
                    continue;
                }
                pending.Messages.Dequeue();
                NetworkMessageSender.SendRawPayload(targetClientId, item.Payload,
                    sequenced: true, latest: false, relayReservationAlreadyHeld: true);
                RecordJoinTransferBytesSent(item.Payload.Length);
                if (m_joinCatchUpRegistry.Journals.TryGetValue(targetClientId,
                    out JoinCatchUpJournal journal))
                {
                    journal.TotalMessagesSent++;
                    journal.TotalBytesSent += item.Payload.Length;
                }
                budget--;
            }
            m_worldTransferCursor = cursor;
        }

        private void SendPendingWorldTransferChunks()
        {
            if (m_worldTransferRegistry.OutgoingTransfers.Count == 0) return;
            // Source: ScMultiplayer.cs:RequestMissingWorldTransferChunks
            // Source: RuthlessConquest/Net/ServerGame.cs:ServerGame.Run
            // Keep a small reliable-UDP window so delayed ACKs on a lossy remote link do not turn
            // premature retransmissions into a self-sustaining burst.
            bool gameplayActive = m_networkPlayerData.Any(item => item.Key > 0);
            int budget = GetJoinTransferSendBudget(gameplayActive);
            int[] targetClientIds = m_worldTransferRegistry.OutgoingTransfers.Keys.OrderBy(id => id).ToArray();
            if (targetClientIds.Length == 0) return;
            m_worldTransferCursor %= targetClientIds.Length;
            int attemptsRemaining = targetClientIds.Length * (budget + 1);
            while (budget > 0 && attemptsRemaining-- > 0)
            {
                int targetClientId = targetClientIds[m_worldTransferCursor];
                m_worldTransferCursor = (m_worldTransferCursor + 1) % targetClientIds.Length;
                if (!m_worldTransferRegistry.OutgoingTransfers.TryGetValue(targetClientId,
                    out OutgoingWorldTransfer transfer) ||
                    !transfer.StartRequested ||
                    (transfer.InitialSendComplete && transfer.RepairChunkIndices.Count == 0))
                    continue;
                int chunkIndex;
                bool isRepair = transfer.RepairChunkIndices.Count > 0;
                if (isRepair)
                {
                    chunkIndex = transfer.RepairChunkIndices.Dequeue();
                    transfer.QueuedRepairChunkIndices.Remove(chunkIndex);
                }
                else
                {
                    int windowEnd = Math.Min(transfer.ChunkCount,
                        transfer.HighestContiguousChunkIndex + 1 +
                        GetWorldTransferChunkWindow(targetClientId));
                    if (transfer.NextChunkIndex >= windowEnd)
                        continue;
                    chunkIndex = transfer.NextChunkIndex++;
                }
                int payloadBytes = Math.Min(WorldTransferChunkSize,
                    transfer.WorldData.Length - chunkIndex * WorldTransferChunkSize);
                if (!TryReserveJoinTransferBytes(transfer, payloadBytes))
                {
                    if (isRepair)
                    {
                        if (transfer.QueuedRepairChunkIndices.Add(chunkIndex))
                            transfer.RepairChunkIndices.Enqueue(chunkIndex);
                    }
                    else
                    {
                        transfer.NextChunkIndex--;
                    }
                    if (!JoinTransferBudgetPolicy.HasTokens(m_joinTransferTokens,
                        JoinTransferBudgetPolicy.EstimatePacketBytes(payloadBytes)))
                        break;
                    continue;
                }
                int estimatedRelayPackets = WorldTransferRelayPackets;
                if (!TryReserveReliableRelayPackets(targetClientId,
                        estimatedRelayPackets))
                {
                    RefundJoinTransferBytes(transfer, payloadBytes);
                    if (isRepair)
                    {
                        if (transfer.QueuedRepairChunkIndices.Add(chunkIndex))
                            transfer.RepairChunkIndices.Enqueue(chunkIndex);
                    }
                    else
                    {
                        transfer.NextChunkIndex--;
                    }
                    continue;
                }
                if (!QueueWorldTransferChunk(transfer, chunkIndex,
                        estimatedRelayPackets))
                {
                    ReleaseReliableRelayPackets(targetClientId, estimatedRelayPackets);
                    RefundJoinTransferBytes(transfer, payloadBytes);
                    if (isRepair)
                    {
                        if (transfer.QueuedRepairChunkIndices.Add(chunkIndex))
                            transfer.RepairChunkIndices.Enqueue(chunkIndex);
                    }
                    else
                        transfer.NextChunkIndex--;
                    break;
                }
                transfer.ChunkLastQueueTimes[chunkIndex] = Time.RealTime;
                budget--;
                if (!transfer.InitialSendComplete &&
                    transfer.NextChunkIndex >= transfer.ChunkCount)
                {
                    transfer.InitialSendComplete = true;
                    Log.Information($"[ScMP] World transfer initially queued: ClientID={transfer.TargetClientId}, " +
                        $"Transfer={transfer.TransferId}, Chunks={transfer.ChunkCount}");
                }
            }
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
        private void StartWorldTransferSender()
        {
            if (m_worldTransferSendTask != null) return;
            m_worldTransferSendCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = m_worldTransferSendCancellation.Token;
            m_worldTransferSendTask = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await m_worldTransferSendSignal.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    int burstCount = 0;
                    while (m_worldTransferSendQueue.TryDequeue(
                        out WorldTransferChunkSendWork work))
                    {
                        Interlocked.Decrement(ref m_worldTransferQueuedWorkCount);
                        if (work == null)
                            continue;
                        if (work.Generation != Volatile.Read(ref m_worldTransferGeneration) ||
                            cancellationToken.IsCancellationRequested)
                        {
                            ReleaseReliableRelayPackets(work.TargetClientId,
                                work.ReservedPackets);
                            continue;
                        }
                        try
                        {
                            int offset = work.ChunkIndex * WorldTransferChunkSize;
                            int count = Math.Min(WorldTransferChunkSize,
                                work.WorldData.Length - offset);
                            var data = new byte[count];
                            Array.Copy(work.WorldData, offset, data, 0, count);
                            NetworkMessageSender.SendPakWorldChunk(work.TargetClientId,
                                new GamePakWorldChunkMessage
                                {
                                    TransferId = work.TransferId,
                                    TargetClientId = work.TargetClientId,
                                    ChunkIndex = work.ChunkIndex,
                                    ChunkCount = work.ChunkCount,
                                    TotalLength = work.WorldData.Length,
                                    Data = data
                                }, relayReservationAlreadyHeld: true);
                            RecordJoinTransferBytesSent(data.Length);
                            if (++burstCount % 4 == 0)
                                await Task.Delay(1, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            ReleaseReliableRelayPackets(work.TargetClientId,
                                work.ReservedPackets);
                            if (!cancellationToken.IsCancellationRequested)
                                Log.Error($"[ScMP] World transfer sender failed: {ex.Message}");
                        }
                    }
                }
            }, cancellationToken);
        }

        private bool QueueWorldTransferChunk(OutgoingWorldTransfer transfer, int chunkIndex,
            int reservedPackets)
        {
            if (transfer == null || chunkIndex < 0 || chunkIndex >= transfer.ChunkCount ||
                Interlocked.CompareExchange(ref m_worldTransferQueuedWorkCount, 0, 0) >=
                    MaximumQueuedWorldTransferChunks)
                return false;
            Interlocked.Increment(ref m_worldTransferQueuedWorkCount);
            m_worldTransferSendQueue.Enqueue(new WorldTransferChunkSendWork
            {
                Generation = Volatile.Read(ref m_worldTransferGeneration),
                TransferId = transfer.TransferId,
                TargetClientId = transfer.TargetClientId,
                ChunkIndex = chunkIndex,
                ChunkCount = transfer.ChunkCount,
                WorldData = transfer.WorldData,
                ReservedPackets = reservedPackets
            });
            m_worldTransferSendSignal.Release();
            return true;
        }

        // Source: ScMultiplayer.cs:HandleGamePakWorldChunkMessage
        private void RequestMissingWorldTransferChunks()
        {
            double now = Time.RealTime;
            foreach (IncomingWorldTransfer transfer in m_worldTransferRegistry.IncomingTransfers.Values.ToArray())
            {
                if (transfer == null || transfer.Chunks == null ||
                    transfer.ReceivedChunkCount >= transfer.Chunks.Length ||
                    now - transfer.LastStatusRequestTime < WorldTransferProgressStatusInterval)
                    continue;
                bool stalled = now - transfer.LastProgressTime >= WorldTransferRepairInterval;
                bool requestRepair = stalled &&
                    now - transfer.LastRepairRequestTime >= WorldTransferRepairRequestInterval;
                int missingEnd = stalled
                    ? Math.Min(transfer.HighestContiguousChunkIndex + 1 +
                        WorldTransferWindowChunks, transfer.Chunks.Length)
                    : Math.Min(transfer.HighestReceivedChunkIndex + 1,
                        transfer.Chunks.Length);
                int[] missing = requestRepair
                    ? Enumerable.Range(
                            transfer.HighestContiguousChunkIndex + 1,
                            Math.Max(0, missingEnd - transfer.HighestContiguousChunkIndex - 1))
                        .Where(index => transfer.Chunks[index] == null)
                        .Take(MaximumWorldTransferRepairChunks)
                        .ToArray()
                    : Array.Empty<int>();
                transfer.LastStatusRequestTime = now;
                if (missing.Length > 0)
                {
                    transfer.LastRepairRequestTime = now;
                    transfer.RepairRequestCount++;
                }
                NetworkMessageSender.SendPakWorldRepairRequest(
                    new GamePakWorldRepairRequestMessage
                    {
                        TransferId = transfer.TransferId,
                        RequestManifest = transfer.Manifest == null,
                        HighestContiguousChunkIndex = transfer.HighestContiguousChunkIndex,
                        HighestReceivedChunkIndex = transfer.HighestReceivedChunkIndex,
                        MissingChunkIndices = missing
                    });
            }
        }

        private void HandleGamePakWorldRepairRequestMessage(
            GamePakWorldRepairRequestMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0 ||
                !m_worldTransferRegistry.OutgoingTransfers.TryGetValue(sourceClientId,
                    out OutgoingWorldTransfer transfer) ||
                (message.TransferId > 0 && transfer.TransferId != message.TransferId))
                return;
            if (message.RequestManifest && transfer.Manifest != null)
            {
                NetworkMessageSender.SendPakWorldManifest(sourceClientId, transfer.Manifest);
            }
            transfer.StartRequested = true;
            transfer.HighestContiguousChunkIndex = Math.Max(
                transfer.HighestContiguousChunkIndex,
                Math.Min(message.HighestContiguousChunkIndex, transfer.ChunkCount - 1));
            foreach (int index in message.MissingChunkIndices ?? Array.Empty<int>())
            {
                if (index < 0 || index >= transfer.NextChunkIndex ||
                    index >= transfer.ChunkLastQueueTimes.Length ||
                    Time.RealTime - transfer.ChunkLastQueueTimes[index] <
                        WorldTransferRepairRequestInterval ||
                    !transfer.QueuedRepairChunkIndices.Add(index))
                    continue;
                transfer.RepairChunkIndices.Enqueue(index);
                transfer.RepairChunkQueueCount++;
            }
        }

        // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Clients
        private int GetWorldTransferRelayUnackedPackets(int targetClientId)
        {
            IPEndPoint address = GetServerClientAddress(targetClientId);
            if (address == null)
                return MaximumWorldTransferUnackedPackets;
            return GetReliableRelayUnackedPackets(targetClientId);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Clients
        private static IPEndPoint GetServerClientAddress(int targetClientId)
        {
            if (server == null || client == null)
                return null;
            ServerGame game = server.Games.FirstOrDefault(item => item.GameID == client.GameID);
            return game?.Clients.FirstOrDefault(item =>
                item.ClientID == targetClientId)?.Address;
        }

        // Source: Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.SetClientGameTrafficEnabled
        private void SetServerClientGameTrafficEnabled(int targetClientId, bool enabled)
        {
            if (server == null || client == null) return;
            ServerGame game = server.Games.FirstOrDefault(item => item.GameID == client.GameID);
            game?.SetClientGameTrafficEnabled(targetClientId, enabled);
        }

    }
}

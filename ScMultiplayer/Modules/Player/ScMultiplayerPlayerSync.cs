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
using ScMultiplayer.Diagnostics;
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
        // 发送: 玩家位置
        // ====================================================================
        private void SendGamePlayerPositionMessage(bool includeInventory, bool forceInventory)
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            if (!IsHost)
            {
                SendGamePlayerInputMessage(includeInventory);
                return;
            }
            var players = subsystemPlayers.ComponentPlayers;
            var positionBatch = new List<GamePlayerPositionMessage>();

            // Source: SubsystemPlayers.ComponentPlayers
            // Network IDs and persisted PlayerData indices are different domains. Send the one
            // locally controlled player, identified by exclusion from the remote avatar table.
            ComponentPlayer item = players.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (item != null)
            {
                // 发送方直接使用 ClientID 作为网络标识，避免 PlayerIndex 映射冲突
                int senderClientId = client.ClientID;
                if (PlayerReadOnlyStateCapture.TryCapture(item,
                    out PlayerReadOnlyStateSnapshot readOnlyState))
                {
                    IInventory inventory = item.ComponentMiner?.Inventory;
                    int activeSlot = inventory?.ActiveSlotIndex ?? -1;
                    int handVal = inventory != null && activeSlot >= 0 ? inventory.GetSlotValue(activeSlot) : 0;
                    int handCnt = inventory != null && activeSlot >= 0 ? inventory.GetSlotCount(activeSlot) : 0;
                    GetInventoryDelta(senderClientId, inventory, includeInventory, forceInventory,
                        out int[] slotValues, out int[] slotCounts);

                    Vector3 itemOffset = item.ComponentCreatureModel.InHandItemOffsetOrder;
                    Vector3 itemRotation = item.ComponentCreatureModel.InHandItemRotationOrder;
                    float aimHandAngle = item.ComponentCreatureModel.AimHandAngleOrder;
                    ApplyPersistentAimPresentation(senderClientId, item, handVal,
                        ref itemOffset, ref itemRotation, ref aimHandAngle);
                    Vector2? walkOrder = item.ComponentLocomotion.LastWalkOrder;
                    float jumpOrder = item.ComponentLocomotion.LastJumpOrder;
                    float pokingPhase = item.ComponentMiner?.PokingPhase ?? 0f;
                    bool attackOrder = item.ComponentCreatureModel.AttackOrder;
                    bool rowLeftOrder = item.ComponentCreatureModel.RowLeftOrder;
                    bool rowRightOrder = item.ComponentCreatureModel.RowRightOrder;

                    NetworkMessageSender.SendPlayerPositionMessage(
                        senderClientId, client.Step, readOnlyState.Position,
                        readOnlyState.Rotation, readOnlyState.Velocity, readOnlyState.LookAngles,
                        walkOrder, jumpOrder, pokingPhase, attackOrder, rowLeftOrder, rowRightOrder,
                        readOnlyState.IsCrouching, readOnlyState.IsFlying, readOnlyState.IsRiding,
                        GetClientMountEntityId(item),
                        readOnlyState.IsGrounded,
                        activeSlot, handVal, handCnt,
                        itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts,
                        positionBatch);
                    BroadcastPlayerPokeIfStarted(senderClientId, item.ComponentMiner);
                }
            }
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0 && remote.Value?.ComponentPlayer != null)
                        SendAuthoritativePlayerState(remote.Key, remote.Value.ComponentPlayer,
                        includeInventory, forceInventory, positionBatch);
            }
            NetworkMessageSender.SendPlayerPositionBatch(positionBatch);
        }

        private void SendAuthoritativePlayerState(int networkClientId, ComponentPlayer item,
            bool includeInventory, bool forceInventory, List<GamePlayerPositionMessage> positionBatch)
        {
            if (!PlayerReadOnlyStateCapture.TryCapture(item,
                out PlayerReadOnlyStateSnapshot readOnlyState))
                return;
            IInventory inventory = item.ComponentMiner?.Inventory;
            int activeSlot = inventory?.ActiveSlotIndex ?? -1;
            int handValue = inventory != null && activeSlot >= 0 ? inventory.GetSlotValue(activeSlot) : 0;
            int handCount = inventory != null && activeSlot >= 0 ? inventory.GetSlotCount(activeSlot) : 0;
            GetInventoryDelta(networkClientId, inventory, includeInventory, forceInventory,
                out int[] slotValues, out int[] slotCounts);
            ComponentLocomotion locomotion = item.ComponentLocomotion;
            ComponentCreatureModel model = item.ComponentCreatureModel;
            Vector3 itemOffset = model.InHandItemOffsetOrder;
            Vector3 itemRotation = model.InHandItemRotationOrder;
            float aimHandAngle = model.AimHandAngleOrder;
            ApplyPersistentAimPresentation(networkClientId, item, handValue,
                ref itemOffset, ref itemRotation, ref aimHandAngle);
            NetworkMessageSender.SendPlayerPositionMessage(
                networkClientId, client.Step, readOnlyState.Position, readOnlyState.Rotation,
                readOnlyState.Velocity, readOnlyState.LookAngles,
                locomotion.LastWalkOrder, locomotion.LastJumpOrder,
                item.ComponentMiner?.PokingPhase ?? 0f, model.AttackOrder,
                model.RowLeftOrder, model.RowRightOrder,
                 readOnlyState.IsCrouching, readOnlyState.IsFlying, readOnlyState.IsRiding,
                 GetClientMountEntityId(item),
                 readOnlyState.IsGrounded,
                activeSlot, handValue, handCount,
                itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts,
                positionBatch);
            BroadcastPlayerPokeIfStarted(networkClientId, item.ComponentMiner);
        }

        // Source: SubsystemBowBlockBehavior.cs:SubsystemBowBlockBehavior.OnAim
        // Source: SubsystemCrossbowBlockBehavior.cs:SubsystemCrossbowBlockBehavior.OnAim
        // Source: SubsystemMusketBlockBehavior.cs:SubsystemMusketBlockBehavior.OnAim
        // Source: SubsystemThrowableBlockBehavior.cs:SubsystemThrowableBlockBehavior.OnAim
        private void ApplyPersistentAimPresentation(int networkClientId, ComponentPlayer player,
            int handValue, ref Vector3 itemOffset, ref Vector3 itemRotation,
            ref float aimHandAngle)
        {
            bool isAiming;
            if (networkClientId == 0)
            {
                // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.m_aim
                // Nullable<T> boxes a present value as T and an empty value as null. SuAPI's
                // generic getter rejects the latter, so inspect the boxed value directly.
                object aimValue = ModManager.ModParentField.GetParentField(
                    player, "m_aim", typeof(ComponentPlayer));
                isAiming = aimValue is Ray3;
            }
            else
            {
                isAiming = m_networkPlayerInputs.TryGetValue(networkClientId,
                    out NetworkPlayerInputState state) && state.HeldAim.HasValue;
            }
            if (!isAiming || handValue == 0) return;

            Block block = BlocksManager.Blocks[Terrain.ExtractContents(handValue)];
            if (block is BowBlock)
            {
                itemOffset = Vector3.Zero;
                itemRotation = new Vector3(0f, -0.2f, 0f);
                aimHandAngle = 1.2f;
            }
            else if (block is CrossbowBlock)
            {
                itemOffset = new Vector3(-0.08f, -0.1f, 0.07f);
                itemRotation = new Vector3(-1.55f, 0f, 0f);
                aimHandAngle = 1.3f;
            }
            else if (block is MusketBlock)
            {
                itemOffset = new Vector3(-0.08f, -0.08f, 0.07f);
                itemRotation = new Vector3(-1.7f, 0f, 0f);
                aimHandAngle = 1.4f;
            }
            else if (block.IsAimable)
            {
                aimHandAngle = 3.2f;
                if (block is SpearBlock)
                {
                    itemOffset = new Vector3(0f, -0.25f, 0f);
                    itemRotation = new Vector3(3.14159f, 0f, 0f);
                }
            }
        }

        // Source: Survivalcraft/Game/ComponentHumanModel.cs:ComponentHumanModel.Update
        // Model orders are consumed and reset every frame. Reapply remote held aim after native
        // updates so the host sees a stable pose between network aim pulses.
        private void MaintainHostAimPresentation()
        {
            if (!IsHost || m_networkPlayerInputs.Count == 0) return;
            foreach (KeyValuePair<int, NetworkPlayerInputState> item in m_networkPlayerInputs)
            {
                if (!item.Value.HeldAim.HasValue ||
                    !m_networkPlayerData.TryGetValue(item.Key, out PlayerData playerData))
                    continue;
                ComponentPlayer player = playerData?.ComponentPlayer;
                ComponentCreatureModel model = player?.ComponentCreatureModel;
                IInventory inventory = player?.ComponentMiner?.Inventory;
                if (model == null || inventory == null) continue;
                int slot = inventory.ActiveSlotIndex;
                int handValue = slot >= 0 && slot < inventory.SlotsCount
                    ? inventory.GetSlotValue(slot)
                    : 0;
                Vector3 itemOffset = Vector3.Zero;
                Vector3 itemRotation = Vector3.Zero;
                float aimHandAngle = 0f;
                ApplyPersistentAimPresentation(item.Key, player, handValue,
                    ref itemOffset, ref itemRotation, ref aimHandAngle);
                model.AimHandAngleOrder = aimHandAngle;
                model.InHandItemOffsetOrder = itemOffset;
                model.InHandItemRotationOrder = itemRotation;
            }
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Update
        private void BroadcastPlayerPokeIfStarted(int playerIndex, ComponentMiner miner)
        {
            if (!IsHost || miner == null) return;
            float phase = miner.PokingPhase;
            m_hostPlayerPokingPhases.TryGetValue(playerIndex, out float previousPhase);
            bool started = phase > 0f &&
                (previousPhase <= 0f || phase + 0.05f < previousPhase);
            m_hostPlayerPokingPhases[playerIndex] = phase;
            if (!started) return;

            m_hostPlayerPokeSequences.TryGetValue(playerIndex, out int sequence);
            sequence = sequence == int.MaxValue ? 1 : sequence + 1;
            m_hostPlayerPokeSequences[playerIndex] = sequence;
            NetworkMessageSender.BroadcastPlayerPoke(new PlayerActionMessage(
                PlayerActionType.Poke, playerIndex, sequence, default));
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Update
        // A short poke can start between two network snapshots, so observe the authoritative
        // miners every rendered frame and retain the reliable edge message as the primary signal.
        private void BroadcastHostPlayerPokes()
        {
            if (!IsHost || GameManager.Project == null) return;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                player != null && !m_networkPlayerData.Values.Contains(player.PlayerData));
            BroadcastPlayerPokeIfStarted(0, localPlayer?.ComponentMiner);
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0)
                    BroadcastPlayerPokeIfStarted(remote.Key,
                        remote.Value?.ComponentPlayer?.ComponentMiner);
            }
        }

        // Source: Survivalcraft/Game/SubsystemWhistleBlockBehavior.cs:SubsystemWhistleBlockBehavior.OnUse
        internal void PublishAuthoritativeWhistle(ComponentMiner componentMiner,
            Vector3 position)
        {
            if (!IsHost || client?.IsConnected != true || componentMiner?.ComponentPlayer == null ||
                !IsFinite(position))
                return;
            int playerClientId = 0;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (ReferenceEquals(item.Value?.ComponentPlayer, componentMiner.ComponentPlayer))
                {
                    playerClientId = item.Key;
                    break;
                }
            }
            m_playerWhistleSequences.TryGetValue(playerClientId, out int sequence);
            sequence = sequence == int.MaxValue ? 1 : sequence + 1;
            m_playerWhistleSequences[playerClientId] = sequence;
            var message = new PlayerActionMessage(
                PlayerActionType.Whistle, playerClientId, sequence, default)
            {
                Position = position
            };
            NetworkMessageSender.BroadcastPlayerWhistle(message);
        }

        private void SendGamePlayerInputMessage(bool includeInventory)
        {
            if (m_localInputResendsRemaining <= 0 || m_localInputSequence <= 0) return;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer == null) return;
            IInventory inventory = localPlayer.ComponentMiner?.Inventory;
            int activeSlotIndex = inventory?.ActiveSlotIndex ?? -1;
            // Source: ScMultiplayer.SynchronizePlayerEquipment
            // Full inventory state is a reliable equipment transaction. Position/input snapshots
            // can arrive after that transaction and must never restore an older slot layout.
            int[] slotValues = Array.Empty<int>();
            int[] slotCounts = Array.Empty<int>();
            NetworkMessageSender.SendPlayerInputMessage(
                client.ClientID, m_localInputSequence, client.Step,
                m_localInputBodyPosition, m_localInputBodyVelocity, m_localInputBodyRotation,
                m_localInputLookAngles, m_localPlayerInput,
                localPlayer.ComponentMiner?.PokingPhase ?? 0f,
                localPlayer.ComponentInput.IsControlledByTouch,
                localPlayer.ComponentBody.TargetCrouchFactor > 0f,
                localPlayer.ComponentLocomotion.IsCreativeFlyEnabled,
                (localPlayer.ComponentBody.StandingOnValue.HasValue ||
                    localPlayer.ComponentBody.StandingOnBody != null) &&
                    MathUtils.Abs(localPlayer.ComponentBody.Velocity.Y) < 0.1f,
                localPlayer.ComponentRider?.Mount != null,
                GetClientMountEntityId(localPlayer), activeSlotIndex,
                m_lastAuthoritativeLocalInventoryTick, slotValues, slotCounts);
            m_lastSentInputSequence = m_localInputSequence;
            m_localInputResendsRemaining--;
        }

        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.SetClothes
        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.GetSlotValue
        // Equipment changes are sent as one reliable snapshot so a clothing move cannot be
        // observed as two independent inventory operations on the host.
        private void SynchronizePlayerEquipment()
        {
            if (client?.IsConnected != true || GameManager.Project == null) return;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;

            if (IsHost)
            {
                ComponentPlayer localPlayer = players.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
                SynchronizeHostEquipment(0, localPlayer);
                foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
                    SynchronizeHostEquipment(item.Key, item.Value?.ComponentPlayer);
                return;
            }

            ComponentPlayer local = players.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (local == null) return;
            NormalizeCrossbowSlot(local.ComponentMiner?.Inventory,
                local.ComponentMiner?.Inventory?.ActiveSlotIndex ?? -1);
            // Source: Survivalcraft/Game/InventorySlotWidget.cs:InventorySlotWidget.Update
            // Player-only rearrangements remain valid while a container panel is open. A real
            // container transfer creates a pending transaction earlier in this frame and still
            // owns both inventories atomically.
            if (m_pendingContainerTransactions.Count > 0) return;
            // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.HandlePlayerEquipmentMessage
            // A newly joined client must not publish its empty placeholder inventory before the
            // host's first authoritative equipment snapshot has been applied.
            if (!m_equipmentSynchronizedClients.Contains(client.ClientID)) return;
            EquipmentSnapshot snapshot = CaptureEquipmentSnapshot(local);
            if (m_lastEquipmentSnapshots.TryGetValue(client.ClientID, out EquipmentSnapshot previous) &&
                EquipmentSnapshotsEqual(previous, snapshot)) return;

            m_lastEquipmentSnapshots[client.ClientID] = snapshot;
            m_localEquipmentRevision = m_localEquipmentRevision == int.MaxValue
                ? 1 : m_localEquipmentRevision + 1;
            NetworkMessageSender.SendPlayerEquipmentMessage(0,
                CreatePlayerEquipmentMessage(client.ClientID, m_localEquipmentRevision,
                    previous, snapshot));
        }

        private void SynchronizeHostEquipment(int clientId, ComponentPlayer player)
        {
            if (player == null) return;
            NormalizeCrossbowSlot(player.ComponentMiner?.Inventory,
                player.ComponentMiner?.Inventory?.ActiveSlotIndex ?? -1);
            EquipmentSnapshot snapshot = CaptureEquipmentSnapshot(player);
            if (m_lastEquipmentSnapshots.TryGetValue(clientId, out EquipmentSnapshot previous) &&
                EquipmentSnapshotsEqual(previous, snapshot)) return;

            m_lastEquipmentSnapshots[clientId] = snapshot;
            int revision = m_equipmentAuthorityRevisions.TryGetValue(clientId, out int current)
                ? (current == int.MaxValue ? 1 : current + 1) : 1;
            m_equipmentAuthorityRevisions[clientId] = revision;
            m_lastReceivedEquipmentRevisions[clientId] = revision;
            m_equipmentSynchronizedClients.Add(clientId);
            BroadcastPlayerEquipment(clientId, revision, previous, snapshot);
        }

        private void BroadcastPlayerEquipment(int clientId, int revision,
            EquipmentSnapshot snapshot)
        {
            EquipmentSnapshot previous = m_lastEquipmentSnapshots.TryGetValue(clientId,
                out EquipmentSnapshot value) ? value : null;
            BroadcastPlayerEquipment(clientId, revision, previous, snapshot);
        }

        private void BroadcastPlayerEquipment(int clientId, int revision,
            EquipmentSnapshot previous, EquipmentSnapshot snapshot)
        {
            NetworkMessageSender.SendPlayerEquipmentMessage(-1,
                CreatePlayerEquipmentMessage(clientId, revision, previous, snapshot));
        }

        // Source: Mod/ScMultiplayer/Message/PlayerEquipmentMessage.cs:PlayerEquipmentMessage
        private static PlayerEquipmentMessage CreatePlayerEquipmentMessage(int clientId,
            int revision, EquipmentSnapshot previous, EquipmentSnapshot snapshot)
        {
            if (snapshot == null)
                return new PlayerEquipmentMessage(clientId, revision, -1,
                    Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int[]>());
            bool clothesChanged = previous == null ||
                !ClothesSnapshotsEqual(previous.Clothes, snapshot.Clothes);
            if (!clothesChanged && previous?.SlotValues != null &&
                previous.SlotCounts != null &&
                TryBuildInventoryDelta(previous.SlotValues, previous.SlotCounts,
                    snapshot.SlotValues, snapshot.SlotCounts, out int[] indices,
                    out _, out _, out int[] values, out int[] counts))
            {
                return new PlayerEquipmentMessage(clientId, revision,
                    snapshot.ActiveSlotIndex, indices, values, counts,
                    CreateEmptyClothesSnapshot());
            }
            if (!clothesChanged && previous != null)
            {
                return new PlayerEquipmentMessage(clientId, revision,
                    snapshot.ActiveSlotIndex, Array.Empty<int>(), Array.Empty<int>(),
                    Array.Empty<int>(), CreateEmptyClothesSnapshot());
            }
            return new PlayerEquipmentMessage(clientId, revision,
                snapshot.ActiveSlotIndex, snapshot.SlotValues, snapshot.SlotCounts,
                snapshot.Clothes);
        }

        private void HandlePlayerEquipmentMessage(PlayerEquipmentMessage message, int sourceClientId)
        {
            if (message == null) return;
            if (IsHost)
            {
                if (sourceClientId <= 0 || message.ClientId != sourceClientId ||
                    !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) ||
                    playerData?.ComponentPlayer == null) return;
                if (m_lastClientEquipmentRevisions.TryGetValue(sourceClientId, out int previousRevision) &&
                    message.Revision <= previousRevision) return;

                m_lastClientEquipmentRevisions[sourceClientId] = message.Revision;
                m_lastEquipmentSnapshots.TryGetValue(sourceClientId,
                    out EquipmentSnapshot previousSnapshot);
                ApplyEquipmentSnapshot(playerData.ComponentPlayer, message);
                // Source: Survivalcraft/Game/ComponentInventoryBase.cs:
                // ComponentInventoryBase.AddSlotItems
                // Loading a musket consumes the powder/wad/bullet locally and changes the
                // complete equipment snapshot. Mark that snapshot authoritative before older
                // input snapshots can restore the pre-load inventory.
                MarkHostInventoryAuthoritative(sourceClientId);
                EquipmentSnapshot snapshot = CaptureEquipmentSnapshot(playerData.ComponentPlayer);
                m_lastEquipmentSnapshots[sourceClientId] = snapshot;
                int currentAuthority = m_equipmentAuthorityRevisions.TryGetValue(sourceClientId,
                    out int authorityRevision) ? authorityRevision : 0;
                // Source: ScMultiplayer.SynchronizePlayerEquipment
                // A host acknowledgement must be newer than its client request. Otherwise a
                // periodic host snapshot and a local drag can share one revision and the old
                // snapshot can overwrite the client's newer slot layout.
                int nextAuthorityRevision = currentAuthority == int.MaxValue
                    ? 1 : currentAuthority + 1;
                int nextRequestedRevision = message.Revision == int.MaxValue
                    ? 1 : message.Revision + 1;
                int revision = Math.Max(nextAuthorityRevision, nextRequestedRevision);
                m_equipmentAuthorityRevisions[sourceClientId] = revision;
                m_lastReceivedEquipmentRevisions[sourceClientId] = revision;
                m_equipmentSynchronizedClients.Add(sourceClientId);
                BroadcastPlayerEquipment(sourceClientId, revision, previousSnapshot,
                    snapshot);
                if (m_clientRecordKeys.TryGetValue(sourceClientId, out string recordKey))
                {
                    m_playerRecords[recordKey] = CapturePlayerRecord(playerData);
                    m_playerRecordsDirty = true;
                }
                // Source: ScMultiplayer.cs:SynchronizeLocalProfileIfChanged
                // A profile change is a single authoritative event. Broadcast it once so runtime
                // skin changes remain supported without a periodic profile stream.
                NetworkMessageSender.SendPlayerProfileMessage(sourceClientId,
                    CapturePlayerRecord(playerData));
                return;
            }

            if (sourceClientId != 0 || m_departedRemoteClientIds.Contains(message.ClientId)) return;
            if (m_lastReceivedEquipmentRevisions.TryGetValue(message.ClientId,
                out int lastRevision) && message.Revision <= lastRevision) return;
            // Source: ScMultiplayer.SynchronizePlayerEquipment
            // Host confirmations are strictly newer than local requests. Equal revisions can only
            // be an older host snapshot, so accepting one would undo a drag or stack split.
            if (message.ClientId == client.ClientID &&
                message.Revision <= m_localEquipmentRevision) return;

            if (message.ClientId == client.ClientID && m_pendingLocalPlayerRecord != null &&
                (!m_localPlayerRecordApplied || m_localReplacementPlayerData?.ComponentPlayer == null))
            {
                CachePendingPlayerEquipment(message);
                return;
            }

            ComponentPlayer player = null;
            if (message.ClientId == client.ClientID)
            {
                SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
                player = players?.ComponentPlayers.FirstOrDefault(item =>
                    !m_networkPlayerData.Values.Contains(item.PlayerData));
                m_localEquipmentRevision = Math.Max(m_localEquipmentRevision, message.Revision);
            }
            else if (m_networkPlayerData.TryGetValue(message.ClientId, out PlayerData remotePlayer))
            {
                player = remotePlayer?.ComponentPlayer;
            }
            if (player == null)
            {
                CachePendingPlayerEquipment(message);
                return;
            }

            ApplyEquipmentSnapshot(player, message);
            m_lastReceivedEquipmentRevisions[message.ClientId] = message.Revision;
            m_lastEquipmentSnapshots[message.ClientId] = CaptureEquipmentSnapshot(player);
            m_equipmentSynchronizedClients.Add(message.ClientId);
            if (message.ClientId == client.ClientID)
            {
                IInventory inventory = player.ComponentMiner?.Inventory;
                if (inventory != null)
                {
                    m_authoritativeLocalSlotValues = CaptureInventoryValues(inventory);
                    m_authoritativeLocalSlotCounts = CaptureInventoryCounts(inventory);
                    m_lastLocalInventoryValues = (int[])m_authoritativeLocalSlotValues.Clone();
                    m_lastLocalInventoryCounts = (int[])m_authoritativeLocalSlotCounts.Clone();
                    m_hasAuthoritativeLocalInventory = true;
                }
            }
        }

        // Source: Mod/ScMultiplayer/Message/PlayerEquipmentMessage.cs:PlayerEquipmentMessage
        private void CachePendingPlayerEquipment(PlayerEquipmentMessage message)
        {
            if (message == null) return;
            if (!m_pendingPlayerEquipmentMessages.TryGetValue(message.ClientId,
                    out PlayerEquipmentMessage pending) || message.Revision > pending.Revision)
                m_pendingPlayerEquipmentMessages[message.ClientId] = message;
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.HandlePlayerEquipmentMessage
        private void TryApplyPendingPlayerEquipment(int playerClientId)
        {
            if (IsHost || !m_pendingPlayerEquipmentMessages.TryGetValue(playerClientId,
                    out PlayerEquipmentMessage message))
                return;
            m_pendingPlayerEquipmentMessages.Remove(playerClientId);
            HandlePlayerEquipmentMessage(message, 0);
        }

        private static EquipmentSnapshot CaptureEquipmentSnapshot(ComponentPlayer player)
        {
            IInventory inventory = player?.ComponentMiner?.Inventory;
            return new EquipmentSnapshot
            {
                ActiveSlotIndex = inventory?.ActiveSlotIndex ?? -1,
                SlotValues = inventory == null ? Array.Empty<int>() : CaptureInventoryValues(inventory),
                SlotCounts = inventory == null ? Array.Empty<int>() : CaptureInventoryCounts(inventory),
                Clothes = CaptureClothes(player)
            };
        }

        private static void ApplyEquipmentSnapshot(ComponentPlayer player, PlayerEquipmentMessage message)
        {
            if (player == null || message == null) return;
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory != null)
            {
                if (message.ActiveSlotIndex >= 0 && message.ActiveSlotIndex < inventory.SlotsCount)
                    inventory.ActiveSlotIndex = message.ActiveSlotIndex;
                if (message.HasDelta)
                    ApplyInventoryDelta(inventory, message.SlotIndices,
                        message.SlotValues, message.SlotCounts);
                else
                    ApplyInventory(inventory, message.SlotValues, message.SlotCounts);
            }
            if (!message.HasDelta || !ClothesSnapshotsEqual(message.Clothes,
                    CreateEmptyClothesSnapshot()))
                ApplyClothes(player, message.Clothes);
        }

        private static bool EquipmentSnapshotsEqual(EquipmentSnapshot left, EquipmentSnapshot right)
        {
            if (left == null || right == null || left.ActiveSlotIndex != right.ActiveSlotIndex ||
                !ArraysEqual(left.SlotValues, right.SlotValues) ||
                !ArraysEqual(left.SlotCounts, right.SlotCounts)) return false;
            int[][] leftClothes = left.Clothes ?? Array.Empty<int[]>();
            int[][] rightClothes = right.Clothes ?? Array.Empty<int[]>();
            if (leftClothes.Length != rightClothes.Length) return false;
            for (int i = 0; i < leftClothes.Length; i++)
                if (!ArraysEqual(leftClothes[i], rightClothes[i])) return false;
            return true;
        }

        private static int[][] CreateEmptyClothesSnapshot() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(),
                Array.Empty<int>() };

        private static bool ClothesSnapshotsEqual(int[][] left, int[][] right)
        {
            int[][] leftClothes = left ?? Array.Empty<int[]>();
            int[][] rightClothes = right ?? Array.Empty<int[]>();
            if (leftClothes.Length != rightClothes.Length) return false;
            for (int i = 0; i < leftClothes.Length; i++)
                if (!ArraysEqual(leftClothes[i], rightClothes[i])) return false;
            return true;
        }

        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.GetSlotValue
        // Full inventory arrays are sent only on change, with a five-second recovery keyframe.
        private void GetInventoryDelta(int ownerId, IInventory inventory, bool check,
            bool force, out int[] values, out int[] counts)
        {
            values = Array.Empty<int>();
            counts = Array.Empty<int>();
            if (!check || inventory == null) return;
            int[] currentValues = CaptureInventoryValues(inventory);
            int[] currentCounts = CaptureInventoryCounts(inventory);
            bool changed = force || !m_lastSentInventoryValues.TryGetValue(ownerId,
                out int[] previousValues) || !ArraysEqual(currentValues, previousValues) ||
                !m_lastSentInventoryCounts.TryGetValue(ownerId, out int[] previousCounts) ||
                !ArraysEqual(currentCounts, previousCounts);
            if (!changed) return;
            m_lastSentInventoryValues[ownerId] = currentValues;
            m_lastSentInventoryCounts[ownerId] = currentCounts;
            values = currentValues;
            counts = currentCounts;
        }

    }
}

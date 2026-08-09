using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
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
        // 发送: 世界信息 (仅Host)
        // ====================================================================
        private void SendGameWorldInfoMessage(bool reliable = false)
        {
            if (client.ClientID != 0) return;
            if (m_hostWorldTimeRevision <= 0)
                m_hostWorldTimeRevision = 1;
            else if (m_hostWorldTimeRevision < int.MaxValue)
                m_hostWorldTimeRevision++;
            var gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(true);
            var timeOfDay = GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(true);
            var weather = GameManager.Project.FindSubsystem<SubsystemWeather>(true);
            var sky = GameManager.Project.FindSubsystem<SubsystemSky>(true);
            NetworkMessageSender.SendWorldInfoMessage(
                timeOfDay.TimeOfDayOffset,
                gameInfo.TotalElapsedGameTime,
                gameInfo.WorldSettings.TimeOfDayMode,
                weather,
                sky, m_hostWorldTimeRevision, reliable);
        }

        internal int CurrentHostWorldTimeRevision
        {
            get
            {
                if (m_hostWorldTimeRevision <= 0) m_hostWorldTimeRevision = 1;
                return m_hostWorldTimeRevision;
            }
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.DrawLightning
        // Vanilla clears lightning while drawing. A headless host has no draw pass, so clear only
        // after the maximum vanilla visual duration. GPU hosts normally clear it first.
        private void MaintainHostLightningLifecycle()
        {
            Project project = GameManager.Project;
            SubsystemSky sky = project?.FindSubsystem<SubsystemSky>(false);
            if (sky == null) return;
            object position = ModManager.ModParentField.GetParentField(
                sky, "m_lightningStrikePosition", typeof(SubsystemSky));
            if (position is not Vector3) return;
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            if (time == null) return;
            double started = ModManager.ModParentField.GetParentField<double>(
                sky, "m_lastLightningStrikeTime", typeof(SubsystemSky));
            if (time.GameTime - started <= HostLightningStaleDuration) return;
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikePosition", (Vector3?)null, typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikeBrightness", 0f, typeof(SubsystemSky));
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike
        // Slow weather snapshots use latest delivery, while both lightning edges remain reliable.
        private void SendHostLightningEdge()
        {
            SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(false);
            if (sky == null) return;
            object value = ModManager.ModParentField.GetParentField(
                sky, "m_lightningStrikePosition", typeof(SubsystemSky));
            bool active = value is Vector3;
            if (active != m_hostLightningActive)
                SendGameWorldInfoMessage(reliable: true);
            m_hostLightningActive = active;
        }

        // Source: Survivalcraft/Game/SubsystemBodies.cs:SubsystemBodies.Bodies
        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
        private void SendWorldObjects(bool fullSync)
        {
            Project project = GameManager.Project;
            if (project == null) return;

            // Full pickable snapshots are directed to clients that just completed world transfer
            // or terrain recovery. Periodic position updates remain broadcast and do not recreate
            // every existing pickable for every client.
            int[] snapshotTargets = m_pendingHostPickableSnapshots.ToArray();
            m_pendingHostPickableSnapshots.Clear();

            SubsystemBodies subsystemBodies = project.FindSubsystem<SubsystemBodies>(true);
            Entity[] animals = subsystemBodies.Bodies
                .Select(body => body?.Entity)
                .Where(entity => entity?.FindComponent<ComponentCreature>() != null &&
                    entity.FindComponent<ComponentPlayer>() == null)
                .Distinct()
                .ToArray();
            var currentAnimals = new HashSet<Entity>(animals);
            foreach (Entity removed in m_hostAnimalIds.Keys.Where(entity =>
                entity == null || !currentAnimals.Contains(entity) || !entity.IsAddedToProject).ToArray())
            {
                ushort id = m_hostAnimalIds[removed];
                NetworkMessageSender.SendEntityMessage(new EntityMessage(id, EntityMessage.EntityAction.Remove));
                m_hostAnimalIds.Remove(removed);
                m_hostAnimalSync.Remove(removed);
            }
            m_hostAnimals.Clear();
            foreach (Entity entity in animals)
            {
                if (!m_hostAnimalIds.TryGetValue(entity, out ushort id))
                {
                    id = m_nextAnimalId++;
                    m_hostAnimalIds.Add(entity, id);
                }
                if (!m_hostAnimalSync.ContainsKey(entity))
                {
                    int simulationSeed = CalculateAnimalSimulationSeed(id);
                    m_hostAnimalSync.Add(entity, new AnimalSyncMetadata
                    {
                        SimulationSeed = simulationSeed
                    });
                    ApplyAnimalSimulationSeed(entity, simulationSeed);
                }
                m_hostAnimals.Add(entity);
            }

            SubsystemPickables subsystemPickables = project.FindSubsystem<SubsystemPickables>(false);
            if (subsystemPickables == null) return;
            Pickable[] pickables = subsystemPickables.Pickables.Where(pickable => pickable != null && !pickable.ToRemove).ToArray();
            var currentPickables = new HashSet<Pickable>(pickables);
            foreach (Pickable removed in m_hostPickableIds.Keys.Where(pickable =>
                pickable == null || !currentPickables.Contains(pickable) || pickable.ToRemove).ToArray())
            {
                ushort id = m_hostPickableIds[removed];
                if (!m_authoritativePickableAcquireIds.Remove(id))
                {
                    NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                        PickableSyncMessage.PickAction.Delete, id, 0, 0,
                        Vector3.Zero, Vector3.Zero));
                }
                m_hostPickableIds.Remove(removed);
            }

            var pickableUpdate = new PickableSyncMessage { Action = PickableSyncMessage.PickAction.UpdatePosition };
            foreach (Pickable pickable in pickables)
            {
                bool isNew = !m_hostPickableIds.TryGetValue(pickable, out ushort id);
                if (isNew)
                {
                    id = m_nextPickableId++;
                    m_hostPickableIds.Add(pickable, id);
                }
                if (isNew)
                {
                    NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                        PickableSyncMessage.PickAction.Create, id, pickable.Value, pickable.Count,
                        pickable.Position, pickable.Velocity, pickable.FlyToPosition,
                        stuckMatrix: pickable.StuckMatrix));
                }
                foreach (int targetClientId in snapshotTargets)
                {
                    if (targetClientId > 0)
                        NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                            PickableSyncMessage.PickAction.Create, id, pickable.Value, pickable.Count,
                            pickable.Position, pickable.Velocity, pickable.FlyToPosition,
                            stuckMatrix: pickable.StuckMatrix), targetClientId);
                }
                pickableUpdate.Positions.Add(new PickableSyncMessage.PickablePos
                {
                    Id = id,
                    Position = pickable.Position,
                    Velocity = pickable.Velocity,
                    FlyToPosition = pickable.FlyToPosition
                });
            }
            if (pickableUpdate.Positions.Count > 0)
                NetworkMessageSender.SendPickableMessage(pickableUpdate);
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.PickableAdded
        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.PickableRemoved
        // A short-lived drop can be collected before the next 8Hz snapshot. Publish lifecycle
        // edges immediately and keep periodic snapshots for movement and recovery only.
        private void AttachHostPickableEvents(Project project)
        {
            if (project == null) return;
            SubsystemPickables subsystem = project.FindSubsystem<SubsystemPickables>(false);
            if (subsystem == null || ReferenceEquals(m_hostPickablesSubsystem, subsystem)) return;
            DetachHostPickableEvents();
            m_hostPickablesSubsystem = subsystem;
            subsystem.PickableAdded += HandleHostPickableAdded;
            subsystem.PickableRemoved += HandleHostPickableRemoved;
        }

        private void DetachHostPickableEvents()
        {
            if (m_hostPickablesSubsystem == null) return;
            m_hostPickablesSubsystem.PickableAdded -= HandleHostPickableAdded;
            m_hostPickablesSubsystem.PickableRemoved -= HandleHostPickableRemoved;
            m_hostPickablesSubsystem = null;
        }

        private void HandleHostPickableAdded(Pickable pickable)
        {
            if (!IsHost)
            {
                HandleClientPredictedPickableAdded(pickable);
                return;
            }
            if (client?.IsConnected != true || pickable == null ||
                !m_networkPlayerData.Any(item => item.Key > 0))
                return;
            if (!m_hostPickableIds.TryGetValue(pickable, out ushort id))
            {
                id = m_nextPickableId++;
                m_hostPickableIds.Add(pickable, id);
            }
            NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                PickableSyncMessage.PickAction.Create, id, pickable.Value, pickable.Count,
                pickable.Position, pickable.Velocity, pickable.FlyToPosition,
                stuckMatrix: pickable.StuckMatrix));
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        internal void PublishPickableWaterSplash(Pickable pickable)
        {
            if (!IsHost || client?.IsConnected != true || pickable == null || pickable.ToRemove ||
                !m_networkPlayerData.Any(item => item.Key > 0))
                return;
            if (!m_hostPickableIds.TryGetValue(pickable, out ushort id))
            {
                id = m_nextPickableId++;
                m_hostPickableIds.Add(pickable, id);
                NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                    PickableSyncMessage.PickAction.Create, id, pickable.Value, pickable.Count,
                    pickable.Position, pickable.Velocity, pickable.FlyToPosition,
                    stuckMatrix: pickable.StuckMatrix));
            }
            NetworkMessageSender.SendPickableMessage(new PickableSyncMessage
            {
                Action = PickableSyncMessage.PickAction.WaterSplash,
                Id = id,
                Position = pickable.Position
            });
        }

        private void HandleClientPredictedPickableAdded(Pickable pickable)
        {
            if (m_applyingNetworkPickable || pickable == null || client?.IsConnected != true ||
                GameManager.Project == null)
                return;
            ComponentPlayer player = GameManager.Project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(item =>
                    !m_networkPlayerData.Values.Contains(item.PlayerData));
            IInventory inventory = player?.ComponentMiner?.Inventory;
            if (inventory == null || player.ComponentBody == null) return;

            // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
            // SubsystemFurnitureBlockBehavior.ScanDesign
            // The native dialog has already created the local preview design and pickable.
            // Convert that edge into one host-authoritative build request before the generic
            // non-camera pickable filter removes it.
            if (TrySubmitPendingFurnitureBuild(pickable, player))
            {
                pickable.ToRemove = true;
                return;
            }

            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            // Q/gamepad drop already sent a request before native prediction creates its pickable.
            if (Time.RealTime <= m_pendingLocalDropPredictionUntil &&
                pickable.Value == m_pendingLocalDropValue &&
                pickable.Count == m_pendingLocalDropCount &&
                Vector3.DistanceSquared(pickable.Position, m_pendingLocalDropPosition) <= 0.01f)
            {
                m_pendingLocalDropPredictionUntil = 0.0;
                pickable.ToRemove = true;
                return;
            }

            // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
            // A UI drag creates the pickable exactly at the active camera. This excludes mining,
            // creature and subsystem drops which must never be converted into player requests.
            if (player.GameWidget?.ActiveCamera == null ||
                Vector3.DistanceSquared(pickable.Position,
                    player.GameWidget.ActiveCamera.ViewPosition) > 0.0001f)
            {
                pickable.ToRemove = true;
                return;
            }

            int sourceSlot = -1;
            int sourceCount = 0;
            int itemValue = NormalizeCrossbowValue(pickable.Value);
            // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
            // Container and player drops create the same predicted pickable. Resolve the open
            // block container first so an identical item in the player inventory is not selected.
            if (TryQueueContainerDrop(pickable, inventory))
            {
                pickable.ToRemove = true;
                return;
            }

            // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
            // Use the latest local equipment snapshot first. The last host-confirmed arrays can
            // lag behind a split, rearrangement or preceding drop and fail to identify the slot.
            if (m_lastEquipmentSnapshots.TryGetValue(client.ClientID,
                    out EquipmentSnapshot equipmentSnapshot))
                TryFindUiDropSource(inventory, equipmentSnapshot.SlotValues,
                    equipmentSnapshot.SlotCounts, itemValue, pickable.Count,
                    out sourceSlot, out sourceCount);
            if (sourceSlot < 0)
            {
                foreach (EquipmentSnapshot snapshot in
                    m_recentLocalEquipmentSnapshots.Reverse())
                {
                    if (TryFindUiDropSource(inventory, snapshot.SlotValues,
                            snapshot.SlotCounts, itemValue, pickable.Count,
                            out sourceSlot, out sourceCount))
                        break;
                }
            }
            if (sourceSlot < 0)
                TryFindUiDropSource(inventory, m_lastLocalInventoryValues,
                    m_lastLocalInventoryCounts, itemValue, pickable.Count,
                    out sourceSlot, out sourceCount);
            if (sourceSlot < 0 || sourceCount <= 0)
                return;

            // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
            // Native UI code has already removed the item. Reconstruct the exact pre-drop player
            // inventory so a delayed pre-split equipment message cannot restore the old layout.
            int[] preDropValues = CaptureInventoryValues(inventory);
            int[] preDropCounts = CaptureInventoryCounts(inventory);
            preDropValues[sourceSlot] = itemValue;
            preDropCounts[sourceSlot] = sourceCount;
            SendUiDropRequest(player, sourceSlot, itemValue, sourceCount, pickable.Count,
                pickable.Count,
                preDropValues, preDropCounts, pickable.Position, pickable.Velocity);
            // The host recreates and broadcasts the authoritative pickable. Keeping this local
            // prediction would leave an extra client-only item after the host response arrives.
            pickable.ToRemove = true;
        }

        // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
        // Player inventory drops must capture the source before native UI removal. Container drops
        // intentionally return false and continue through the existing ContainerSync transaction.
        public bool TryHandlePlayerInventoryDragDrop(ViewWidget viewWidget, Widget dragWidget,
            object data)
        {
            if (IsHost || client?.IsConnected != true || GameManager.Project == null ||
                !(data is InventoryDragData dragData) || viewWidget?.GameWidget == null ||
                dragWidget == null)
                return false;
            ComponentPlayer player = viewWidget.GameWidget.PlayerData?.ComponentPlayer;
            IInventory inventory = player?.ComponentMiner?.Inventory;
            if (player == null || inventory == null ||
                m_networkPlayerData.Values.Contains(player.PlayerData) ||
                !ReferenceEquals(dragData.Inventory, inventory) ||
                dragData.SlotIndex < 0 || dragData.SlotIndex >= inventory.SlotsCount ||
                viewWidget.GameWidget.ActiveCamera == null)
                return false;

            int sourceValue = inventory.GetSlotValue(dragData.SlotIndex);
            int sourceCount = inventory.GetSlotCount(dragData.SlotIndex);
            if (sourceValue == 0 || sourceCount <= 0)
                return true;
            // Source: Survivalcraft/Game/InventorySlotWidget.cs:InventorySlotWidget.IsSplitMode
            // The explicit split source is the only single-item player drop. A normal drag from
            // the inventory or hotbar must discard the complete source stack, regardless of the
            // input device's drag button mode.
            bool isSplitSource = player.ComponentInput.SplitSourceInventory == inventory &&
                player.ComponentInput.SplitSourceSlotIndex == dragData.SlotIndex;
            int dropCount = isSplitSource
                ? 1
                : dragData.DragMode != DragMode.SingleItem
                    ? sourceCount
                    : MathUtils.Min(sourceCount, 1);
            if (dropCount <= 0)
                return true;

            int itemValue = NormalizeCrossbowValue(sourceValue);
            int[] preDropValues = CaptureInventoryValues(inventory);
            int[] preDropCounts = CaptureInventoryCounts(inventory);
            preDropValues[dragData.SlotIndex] = itemValue;
            preDropCounts[dragData.SlotIndex] = sourceCount;
            Vector2 screenPosition = dragWidget.WidgetToScreen(dragWidget.ActualSize / 2f);
            Vector3 velocity = Vector3.Normalize(viewWidget.GameWidget.ActiveCamera.ScreenToWorld(
                new Vector3(screenPosition.X, screenPosition.Y, 1f), Matrix.Identity) -
                viewWidget.GameWidget.ActiveCamera.ViewPosition) * 12f;
            int removed = inventory.RemoveSlotItems(dragData.SlotIndex, dropCount);
            if (removed <= 0)
                return true;

            SendUiDropRequest(player, dragData.SlotIndex, itemValue, sourceCount, removed,
                dropCount,
                preDropValues, preDropCounts, viewWidget.GameWidget.ActiveCamera.ViewPosition,
                velocity);
            return true;
        }

        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.DropSlotItems
        private void SendUiDropRequest(ComponentPlayer player, int sourceSlot, int itemValue,
            int sourceCount, int dropCount, int removeCount, int[] preDropValues,
            int[] preDropCounts,
            Vector3 position, Vector3 velocity)
        {
            m_localEquipmentRevision = m_localEquipmentRevision == int.MaxValue
                ? 1
                : m_localEquipmentRevision + 1;
            m_localDropSequence = m_localDropSequence == int.MaxValue
                ? 1
                : m_localDropSequence + 1;
            var message = new PlayerActionMessage(
                PlayerActionType.DropRequest, client.ClientID, m_localDropSequence, default,
                sourceSlot, itemValue, sourceCount)
            {
                DropCount = dropCount,
                RemoveCount = removeCount,
                RequestId = m_localEquipmentRevision,
                Position = position,
                Velocity = velocity
            };
            message.HasInventoryDelta = true;
            message.InventorySlotIndices = new[] { sourceSlot };
            message.InventoryBaseValues = new[] { preDropValues[sourceSlot] };
            message.InventoryBaseCounts = new[] { preDropCounts[sourceSlot] };
            message.InventorySlotValues = new[] { itemValue };
            message.InventorySlotCounts = new[] { Math.Max(0, sourceCount - removeCount) };
            NetworkMessageSender.SendPlayerDropRequest(message);
            m_lastEquipmentSnapshots[client.ClientID] = CaptureEquipmentSnapshot(player);
            IInventory inventory = player?.ComponentMiner?.Inventory;
            m_lastLocalInventoryValues = CaptureInventoryValues(inventory);
            m_lastLocalInventoryCounts = CaptureInventoryCounts(inventory);
        }

        // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
        private static bool TryFindUiDropSource(IInventory inventory, int[] previousValues,
            int[] previousCounts, int itemValue, int dropCount,
            out int sourceSlot, out int sourceCount)
        {
            sourceSlot = -1;
            sourceCount = 0;
            if (inventory == null || previousValues == null || previousCounts == null)
                return false;
            int slotsCount = Math.Min(inventory.SlotsCount,
                Math.Min(previousValues.Length, previousCounts.Length));
            for (int i = 0; i < slotsCount; i++)
            {
                if (NormalizeCrossbowValue(previousValues[i]) != itemValue) continue;
                int currentCount = NormalizeCrossbowValue(inventory.GetSlotValue(i)) == itemValue
                    ? inventory.GetSlotCount(i)
                    : 0;
                if (previousCounts[i] - currentCount != dropCount) continue;
                sourceSlot = i;
                sourceCount = currentCount + dropCount;
                return true;
            }

            // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
            // UI removal can race the equipment publisher. A newer snapshot can therefore retain
            // the same slot state after an earlier player-only rearrangement, while this one still
            // proves that the slot contained the items now being dropped. Rebuild from the current
            // layout below, so only this drop is restored for the host transaction.
            for (int i = 0; i < slotsCount; i++)
            {
                if (NormalizeCrossbowValue(previousValues[i]) != itemValue) continue;
                int currentValue = NormalizeCrossbowValue(inventory.GetSlotValue(i));
                int currentCount = currentValue == itemValue ? inventory.GetSlotCount(i) : 0;
                if (currentValue != 0 && currentValue != itemValue) continue;
                if (previousCounts[i] < currentCount + dropCount) continue;
                sourceSlot = i;
                sourceCount = currentCount + dropCount;
                return true;
            }
            return false;
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        private void RememberLocalEquipmentSnapshot(IInventory inventory)
        {
            if (inventory == null) return;
            var snapshot = new EquipmentSnapshot
            {
                ActiveSlotIndex = inventory.ActiveSlotIndex,
                SlotValues = CaptureInventoryValues(inventory),
                SlotCounts = CaptureInventoryCounts(inventory)
            };
            if (m_recentLocalEquipmentSnapshots.Count > 0 &&
                EquipmentSnapshotsEqual(m_recentLocalEquipmentSnapshots.Last(), snapshot))
                return;
            m_recentLocalEquipmentSnapshots.Enqueue(snapshot);
            while (m_recentLocalEquipmentSnapshots.Count > 8)
                m_recentLocalEquipmentSnapshots.Dequeue();
        }

        // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
        // UI drops remove items before creating their predicted pickable. Match that exact loss
        // against the authoritative state of the open block inventory and reuse its transaction.
        private bool TryQueueContainerDrop(Pickable pickable, IInventory playerInventory)
        {
            NetworkContainerReference container = GetOpenNetworkContainer();
            if (container == null || !m_hasAuthoritativeLocalInventory ||
                GameManager.Project == null || playerInventory == null)
                return false;
            int dropValue = NormalizeCrossbowValue(pickable.Value);
            int[] playerValues = CaptureInventoryValues(playerInventory);
            int[] playerCounts = CaptureInventoryCounts(playerInventory);
            string key = container.Key;
            if (m_pendingContainerTransactions.ContainsKey(key) ||
                !m_containerStates.TryGetValue(key, out ContainerNetworkState state))
                return false;
            int[] values = CaptureInventoryValues(container.Inventory);
            int[] counts = CaptureInventoryCounts(container.Inventory);
            bool ordinaryDrop = HasContainerDropDelta(container.Inventory,
                state.Values, state.Counts, values, counts, dropValue, pickable.Count);
            bool craftingResultDrop = container.Inventory is ComponentCraftingTable crafting &&
                LooksLikeCraftingResultDrop(crafting, state.Values, state.Counts,
                    values, counts, dropValue, pickable.Count);
            if (!ordinaryDrop && !craftingResultDrop)
                return false;

            m_nextContainerRequestId = m_nextContainerRequestId == int.MaxValue
                ? 1 : m_nextContainerRequestId + 1;
            m_localEquipmentRevision = m_localEquipmentRevision == int.MaxValue
                ? 1 : m_localEquipmentRevision + 1;
            int[] containerIndices = Array.Empty<int>();
            int[] containerBaseValues = Array.Empty<int>();
            int[] containerBaseCounts = Array.Empty<int>();
            int[] containerDesiredValues = Array.Empty<int>();
            int[] containerDesiredCounts = Array.Empty<int>();
            bool hasContainerDelta = !(container.Inventory is ComponentCraftingTable);
            if (hasContainerDelta)
                hasContainerDelta = TryBuildInventoryDelta(state.Values, state.Counts,
                    values, counts, out containerIndices, out containerBaseValues,
                    out containerBaseCounts, out containerDesiredValues,
                    out containerDesiredCounts);
            bool hasPlayerDelta = TryBuildInventoryDelta(m_authoritativeLocalSlotValues,
                m_authoritativeLocalSlotCounts, playerValues, playerCounts,
                out int[] playerIndices, out int[] playerBaseValues,
                out int[] playerBaseCounts, out int[] playerDesiredValues,
                out int[] playerDesiredCounts);
            var request = new ContainerSyncMessage
            {
                Coordinates = container.Coordinates,
                ComponentType = container.ComponentType,
                OwnerClientId = container.OwnerClientId,
                Revision = state.Revision,
                RequestId = m_nextContainerRequestId,
                RequesterClientId = client.ClientID,
                PlayerRevision = m_localEquipmentRevision,
                IsRequest = true,
                IsDrop = true,
                DropValue = dropValue,
                DropCount = pickable.Count,
                DropPosition = pickable.Position,
                DropVelocity = pickable.Velocity
            };
            if (hasContainerDelta)
            {
                request.HasSlotDelta = true;
                request.SlotIndices = containerIndices;
                request.BaseSlotValues = containerBaseValues;
                request.BaseSlotCounts = containerBaseCounts;
                request.SlotValues = containerDesiredValues;
                request.SlotCounts = containerDesiredCounts;
            }
            else
            {
                request.SlotValues = values;
                request.SlotCounts = counts;
                request.BaseSlotValues = (int[])state.Values.Clone();
                request.BaseSlotCounts = (int[])state.Counts.Clone();
            }
            if (hasPlayerDelta)
            {
                request.HasPlayerSlotDelta = true;
                request.PlayerSlotIndices = playerIndices;
                request.PlayerBaseSlotValues = playerBaseValues;
                request.PlayerBaseSlotCounts = playerBaseCounts;
                request.PlayerSlotValues = playerDesiredValues;
                request.PlayerSlotCounts = playerDesiredCounts;
            }
            else
            {
                request.PlayerBaseSlotValues = (int[])m_authoritativeLocalSlotValues.Clone();
                request.PlayerBaseSlotCounts = (int[])m_authoritativeLocalSlotCounts.Clone();
                request.PlayerSlotValues = playerValues;
                request.PlayerSlotCounts = playerCounts;
            }
            m_pendingContainerTransactions[key] = new PendingContainerTransaction
            {
                Request = request,
                LastSendTime = Time.RealTime
            };
            SendContainerRequest(request);
            return true;
        }

        private static bool LooksLikeCraftingResultDrop(ComponentCraftingTable craftingTable,
            int[] baseValues, int[] baseCounts, int[] desiredValues, int[] desiredCounts,
            int dropValue, int dropCount)
        {
            if (craftingTable == null || baseValues == null || baseCounts == null ||
                desiredValues == null || desiredCounts == null || dropCount <= 0 ||
                baseValues.Length != craftingTable.SlotsCount ||
                baseCounts.Length != craftingTable.SlotsCount ||
                desiredValues.Length != craftingTable.SlotsCount ||
                desiredCounts.Length != craftingTable.SlotsCount)
                return false;
            int resultSlot = craftingTable.ResultSlotIndex;
            return NormalizeCrossbowValue(baseValues[resultSlot]) == dropValue &&
                baseCounts[resultSlot] >= dropCount &&
                (!ArraysEqual(baseValues, desiredValues) ||
                    !ArraysEqual(baseCounts, desiredCounts));
        }

        private void HandleHostPickableRemoved(Pickable pickable)
        {
            if (!IsHost || pickable == null ||
                !m_hostPickableIds.TryGetValue(pickable, out ushort id))
                return;
            m_hostPickableIds.Remove(pickable);
            // Source: ScMultiplayer.HandlePickableAcquireRequest
            // The request transaction already broadcast the authoritative result. Do not emit a
            // second acquisition with RequestId zero when native removal runs on the next frame.
            if (m_authoritativePickableAcquireIds.Remove(id)) return;
            if (client?.IsConnected == true &&
                m_networkPlayerData.Any(item => item.Key > 0))
            {
                if (pickable.Count == 0 &&
                    TryGetHostPickableCollector(pickable,
                        out int collectorClientId, out IInventory inventory))
                {
                    var message = new PickableSyncMessage
                    {
                        Action = PickableSyncMessage.PickAction.Acquire,
                        Id = id,
                        CollectorClientId = collectorClientId,
                        ServerTick = client.Step,
                        Count = 0,
                        PlaySound = true,
                        SlotValues = CaptureInventoryValues(inventory),
                        SlotCounts = CaptureInventoryCounts(inventory)
                    };
                    if (collectorClientId > 0)
                        MarkHostInventoryAuthoritative(collectorClientId);
                    NetworkMessageSender.SendPickableMessage(message);
                }
                else
                {
                    NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                        PickableSyncMessage.PickAction.Delete, id, 0, 0,
                        Vector3.Zero, Vector3.Zero));
                }
            }
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        private bool TryGetHostPickableCollector(Pickable pickable,
            out int collectorClientId, out IInventory inventory)
        {
            collectorClientId = -1;
            inventory = null;
            if (pickable == null || Terrain.ExtractContents(pickable.Value) == 248)
                return false;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return false;
            ComponentPlayer collector = players.ComponentPlayers
                .Where(player => player?.ComponentBody != null &&
                    player.ComponentMiner?.Inventory != null &&
                    player.ComponentHealth?.Health > 0f)
                .OrderBy(player => Vector3.DistanceSquared(
                    player.ComponentBody.Position + new Vector3(0f, 0.75f, 0f),
                    pickable.Position))
                .FirstOrDefault(player => Vector3.DistanceSquared(
                    player.ComponentBody.Position + new Vector3(0f, 0.75f, 0f),
                    pickable.Position) <= 2.25f);
            if (collector == null) return false;
            KeyValuePair<int, PlayerData> remote = m_networkPlayerData.FirstOrDefault(pair =>
                ReferenceEquals(pair.Value?.ComponentPlayer, collector));
            collectorClientId = remote.Value != null ? remote.Key : 0;
            inventory = collector.ComponentMiner.Inventory;
            return true;
        }

        // Source: Survivalcraft/Game/ComponentBehavior.cs:ComponentBehavior.IsActive
        // Source: Survivalcraft/Game/ComponentHerdBehavior.cs:ComponentHerdBehavior.CallNearbyCreaturesHelp
    private void SendAdaptiveAnimalUpdates(bool forceFullSnapshot)
        {
            Project project = GameManager.Project;
            if (project == null || (!forceFullSnapshot && m_hostAnimals.Count == 0)) return;

            double now = Time.RealTime;
            ComponentPlayer[] players = project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers
                .Where(player => player?.ComponentBody != null)
                .ToArray() ?? Array.Empty<ComponentPlayer>();
            var candidates = new List<AnimalSyncCandidate>(m_hostAnimals.Count);
            foreach (Entity entity in m_hostAnimals.ToArray())
            {
                if (entity?.IsAddedToProject != true) continue;
                ComponentCreature creature = entity.FindComponent<ComponentCreature>();
                ComponentBody body = creature?.ComponentBody;
                if (creature == null || body == null) continue;

                ComponentBehavior activeBehavior = entity.FindComponents<ComponentBehavior>()
                    .Where(behavior => behavior != null && behavior.IsActive)
                    .OrderByDescending(behavior => behavior.ImportanceLevel)
                    .FirstOrDefault();
                ComponentChaseBehavior chase = entity.FindComponent<ComponentChaseBehavior>();
                ComponentCreature target = chase?.Target;
                ComponentHerdBehavior herd = entity.FindComponent<ComponentHerdBehavior>();
                ComponentCreatureModel model = creature.ComponentCreatureModel;
                ComponentLocomotion locomotion = creature.ComponentLocomotion;
                ComponentShapeshifter shapeshifter = entity.FindComponent<ComponentShapeshifter>();
                // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.ShapeshiftTo
                string shapeshiftTarget = shapeshifter == null
                    ? string.Empty
                    : ModManager.ModParentField.GetParentField(
                        shapeshifter, "m_spawnEntityTemplateName",
                        typeof(ComponentShapeshifter)) as string ?? string.Empty;
                AnimalSyncMetadata metadata = m_hostAnimalSync[entity];
                float health = creature.ComponentHealth?.Health ?? 0f;
                bool wasAttacked = metadata.HasSent && health < metadata.LastHealth - 0.001f;
                if (wasAttacked) metadata.HighPriorityUntil = now + 3.0;
                string behaviorState = GetActiveBehaviorState(activeBehavior);
                bool isAttacking = IsAnimalAttackActive(chase, model);
                bool isFeeding = IsAnimalFeedActive(model, behaviorState);
                PublishHostAnimalSoundEvents(entity, creature, metadata,
                    isAttacking, wasAttacked);
                bool targetsPlayer = target?.Entity.FindComponent<ComponentPlayer>() != null;
                ComponentPlayer nearestPlayer = players.OrderBy(player => Vector3.DistanceSquared(
                    player.ComponentBody.Position, body.Position)).FirstOrDefault();
                float nearestPlayerDistanceSquared = nearestPlayer != null
                    ? Vector3.DistanceSquared(nearestPlayer.ComponentBody.Position, body.Position)
                    : float.MaxValue;
                bool highPriorityInteraction = wasAttacked || now < metadata.HighPriorityUntil;
                // Source: Survivalcraft/Game/ComponentBirdModel.cs:ComponentBirdModel.Animate
                // LastFlyOrder is also the native wing-flight presentation edge. Give visible flying
                // birds a wider high-rate range without increasing updates for grounded animals.
                bool isFlyingBird = model is ComponentBirdModel &&
                    locomotion?.LastFlyOrder.HasValue == true;

                byte tier = 0;
                float nearPlayerThreshold = metadata.SyncTier >= 2 ? 12f : 10f;
                bool isNearPlayer = nearestPlayerDistanceSquared <=
                    nearPlayerThreshold * nearPlayerThreshold;
                if (targetsPlayer) tier = 1;
                if (isNearPlayer)
                    tier = Math.Max(tier, (byte)2);
                if (isFlyingBird && nearestPlayerDistanceSquared <= 64f * 64f)
                    tier = Math.Max(tier, (byte)2);
                if (isFlyingBird && nearestPlayerDistanceSquared <= 24f * 24f)
                    tier = Math.Max(tier, (byte)3);
                if (highPriorityInteraction) tier = 3;
                if (isAttacking && targetsPlayer) tier = 4;

                candidates.Add(new AnimalSyncCandidate
                {
                    Entity = entity,
                    Creature = creature,
                    Body = body,
                    BehaviorState = behaviorState,
                    TargetEntityId = GetCreatureTargetNetworkId(target),
                    HerdName = herd?.HerdName ?? string.Empty,
                    SyncTier = tier,
                    AttackOrder = isAttacking,
                    FeedOrder = isFeeding,
                    ShapeshiftTarget = shapeshiftTarget
                });
            }

            foreach (AnimalSyncCandidate source in candidates.Where(candidate =>
                candidate.SyncTier >= 3 && !string.IsNullOrEmpty(candidate.HerdName)).ToArray())
            {
                foreach (AnimalSyncCandidate member in candidates)
                {
                    if (member.HerdName == source.HerdName &&
                        Vector3.DistanceSquared(member.Body.Position, source.Body.Position) < 256f)
                        member.SyncTier = Math.Max(member.SyncTier, (byte)3);
                }
            }

            // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:SendDirectInput
            // Presentation keyframes are replaceable. Durable lifecycle and damage edges use a
            // separate small reliable batch so they do not turn nearby animals' transforms into
            // retransmitted reliable traffic.
            var presentationMessage = new BodyUpdateMessage
            {
                ServerTick = client.Step,
                IsFullSnapshot = forceFullSnapshot
            };
            var durableMessage = new BodyUpdateMessage { ServerTick = client.Step };
            foreach (AnimalSyncCandidate candidate in candidates)
            {
                AnimalSyncMetadata metadata = m_hostAnimalSync[candidate.Entity];
                bool isInitialState = !metadata.HasSent;

                candidate.StateChanged = !metadata.HasSent ||
                    metadata.BehaviorState != candidate.BehaviorState ||
                    metadata.TargetEntityId != candidate.TargetEntityId ||
                    metadata.HerdName != candidate.HerdName ||
                    metadata.SyncTier != candidate.SyncTier ||
                    metadata.AttackOrder != candidate.AttackOrder ||
                    metadata.FeedOrder != candidate.FeedOrder ||
                    metadata.ShapeshiftTarget != candidate.ShapeshiftTarget;
                bool shapeshiftStarted = !string.IsNullOrEmpty(candidate.ShapeshiftTarget) &&
                    metadata.ShapeshiftTarget != candidate.ShapeshiftTarget;
                if (!forceFullSnapshot && !candidate.StateChanged &&
                    now < metadata.NextSendTime)
                    continue;

                ComponentLocomotion locomotion = candidate.Creature.ComponentLocomotion;
                ComponentCreatureModel model = candidate.Creature.ComponentCreatureModel;
                bool grounded = candidate.Body.StandingOnValue.HasValue ||
                    candidate.Body.StandingOnBody != null;
                bool gravityEnabled = candidate.Body.IsGravityEnabled;
                bool immersed = candidate.Body.ImmersionFactor > 0f;
                bool flying = locomotion != null && locomotion.FlySpeed > 0f &&
                    locomotion.FlyOrder.HasValue;
                BodyUpdateMessage.ChangeFlag flags = BodyUpdateMessage.ChangeFlag.Position |
                    BodyUpdateMessage.ChangeFlag.Rotation |
                    BodyUpdateMessage.ChangeFlag.Velocity |
                    BodyUpdateMessage.ChangeFlag.LookAngles |
                    BodyUpdateMessage.ChangeFlag.Movement |
                    BodyUpdateMessage.ChangeFlag.Health;
                if (isInitialState || forceFullSnapshot)
                    flags |= BodyUpdateMessage.ChangeFlag.Template;
                if (candidate.StateChanged || forceFullSnapshot)
                    flags |= BodyUpdateMessage.ChangeFlag.BehaviorState;
                float currentHealth = candidate.Creature.ComponentHealth?.Health ?? 0f;
                bool healthDecreased = metadata.HasSent &&
                    currentHealth < metadata.LastHealth - 0.0001f;
                if (healthDecreased)
                    metadata.DamageSequence = metadata.DamageSequence == int.MaxValue
                        ? 1
                        : metadata.DamageSequence + 1;
                var bodyItem = new BodyUpdateMessage.BodyItem
                {
                    EntityId = m_hostAnimalIds[candidate.Entity],
                    Flags = flags,
                    Position = candidate.Body.Position,
                    Rotation = candidate.Body.Rotation,
                    Velocity = candidate.Body.Velocity,
                    LookAngles = locomotion?.LookAngles ?? Vector2.Zero,
                    WalkOrder = locomotion?.LastWalkOrder,
                    FlyOrder = locomotion?.LastFlyOrder,
                    SwimOrder = locomotion?.LastSwimOrder,
                    TurnOrder = locomotion?.LastTurnOrder ?? Vector2.Zero,
                    JumpOrder = locomotion?.LastJumpOrder ?? 0f,
                    AttackOrder = candidate.AttackOrder,
                    FeedOrder = candidate.FeedOrder,
                    TemplateName = candidate.Entity.ValuesDictionary?.DatabaseObject?.Name,
                    SyncTier = candidate.SyncTier,
                    ActiveBehaviorState = candidate.BehaviorState,
                    TargetEntityId = candidate.TargetEntityId,
                    HerdName = candidate.HerdName,
                    SimulationSeed = metadata.SimulationSeed,
                    ShapeshiftTarget = candidate.ShapeshiftTarget,
                    Health = currentHealth,
                    MotionFlags = (grounded ? BodyUpdateMessage.BodyItem.MotionFlag.Grounded : 0) |
                        (gravityEnabled ? BodyUpdateMessage.BodyItem.MotionFlag.GravityEnabled : 0) |
                        (immersed ? BodyUpdateMessage.BodyItem.MotionFlag.Immersed : 0) |
                        (flying ? BodyUpdateMessage.BodyItem.MotionFlag.Flying : 0),
                    DamageSequence = metadata.DamageSequence
                };
                if (forceFullSnapshot)
                {
                    presentationMessage.Bodies.Add(bodyItem);
                }
                else
                {
                    // The first reliable item contains its transform and template. Later frames
                    // can be dropped because the client already owns the correct replica.
                    if (!isInitialState)
                        presentationMessage.Bodies.Add(bodyItem);

                    // Source: Survivalcraft/Game/ComponentCreatureSounds.cs:PlayPainSound
                    // A health-loss edge remains reliable without retaining unrelated movement.
                    if (isInitialState || shapeshiftStarted || healthDecreased)
                    {
                        BodyUpdateMessage.BodyItem durableItem = bodyItem;
                        BodyUpdateMessage.ChangeFlag durableFlags =
                            BodyUpdateMessage.ChangeFlag.None;
                        if (shapeshiftStarted)
                            durableFlags |= BodyUpdateMessage.ChangeFlag.BehaviorState;
                        if (healthDecreased)
                            durableFlags |= BodyUpdateMessage.ChangeFlag.Health;
                        durableItem.Flags = isInitialState ? bodyItem.Flags : durableFlags;
                        durableMessage.Bodies.Add(durableItem);
                    }
                }

                metadata.HasSent = true;
                metadata.BehaviorState = candidate.BehaviorState;
                metadata.TargetEntityId = candidate.TargetEntityId;
                metadata.HerdName = candidate.HerdName;
                metadata.SyncTier = candidate.SyncTier;
                metadata.AttackOrder = candidate.AttackOrder;
                metadata.FeedOrder = candidate.FeedOrder;
                metadata.ShapeshiftTarget = candidate.ShapeshiftTarget;
                metadata.LastHealth = currentHealth;
                metadata.NextSendTime = now + GetAnimalSyncInterval(candidate.SyncTier);

                if (!forceFullSnapshot &&
                    presentationMessage.Bodies.Count >= AnimalSyncBatchSize)
                {
                    NetworkMessageSender.SendBodyUpdateMessage(
                        presentationMessage);
                    presentationMessage = new BodyUpdateMessage { ServerTick = client.Step };
                }
                if (!forceFullSnapshot && durableMessage.Bodies.Count >= AnimalSyncBatchSize)
                {
                    NetworkMessageSender.SendBodyUpdateMessage(durableMessage, true);
                    durableMessage = new BodyUpdateMessage { ServerTick = client.Step };
                }
            }
            if (forceFullSnapshot)
            {
                // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
                // A reliable complete set lets clients recover missed add/remove datagrams and an
                // empty snapshot clears every stale replica after the host population reaches zero.
                // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
                // This is a periodic recovery view, not a durable gameplay edge. A newer complete
                // view supersedes an older one, so it must never occupy the reliable retry queue.
                NetworkMessageSender.SendBodyUpdateMessage(presentationMessage);
            }
            else
            {
                if (presentationMessage.Bodies.Count > 0)
                    NetworkMessageSender.SendBodyUpdateMessage(presentationMessage);
                if (durableMessage.Bodies.Count > 0)
                    NetworkMessageSender.SendBodyUpdateMessage(durableMessage, true);
            }
        }

        // Source: Survivalcraft/Game/ComponentCreatureSounds.cs:ComponentCreatureSounds.PlayIdleSound
        // Source: Survivalcraft/Game/ComponentHowlBehavior.cs:ComponentHowlBehavior.Update
        private void PublishHostAnimalSoundEvents(Entity entity, ComponentCreature creature,
            AnimalSyncMetadata metadata, bool isAttacking, bool wasAttacked)
        {
            ComponentCreatureSounds sounds = creature?.ComponentCreatureSounds;
            ComponentHowlBehavior howl = entity?.FindComponent<ComponentHowlBehavior>();
            double soundTime = sounds != null
                ? ModManager.ModParentField.GetParentField<double>(
                    sounds, "m_lastSoundTime", typeof(ComponentCreatureSounds))
                : -1000.0;
            float howlTime = howl != null
                ? ModManager.ModParentField.GetParentField<float>(
                    howl, "m_howlTime", typeof(ComponentHowlBehavior))
                : 0f;
            if (!metadata.SoundStateInitialized)
            {
                metadata.SoundStateInitialized = true;
                metadata.LastCreatureSoundTime = soundTime;
                metadata.LastHowlTime = howlTime;
                return;
            }

            if (soundTime > metadata.LastCreatureSoundTime + 0.0001)
            {
                // Pain already has a reliable DamageSequence in BodyUpdateMessage.
                if (!wasAttacked)
                    BroadcastAnimalSound(entity, creature.ComponentBody.Position, metadata,
                        isAttacking ? AnimalSoundType.Attack : AnimalSoundType.Idle);
                metadata.LastCreatureSoundTime = soundTime;
            }
            if (howl != null && metadata.LastHowlTime <= 0.5f && howlTime > 0.5f)
                BroadcastAnimalSound(entity, creature.ComponentBody.Position, metadata,
                    AnimalSoundType.Howl);
            metadata.LastHowlTime = howlTime;
        }

        private void BroadcastAnimalSound(Entity entity, Vector3 position,
            AnimalSyncMetadata metadata, AnimalSoundType soundType)
        {
            if (!m_hostAnimalIds.TryGetValue(entity, out ushort id)) return;
            metadata.SoundSequence = metadata.SoundSequence == int.MaxValue
                ? 1
                : metadata.SoundSequence + 1;
            NetworkMessageSender.BroadcastAnimalSound(new AnimalSoundMessage(
                id, metadata.SoundSequence, client.Step, soundType, position));
        }

        private string GetActiveBehaviorState(ComponentBehavior behavior)
        {
            if (behavior == null) return string.Empty;
            for (Type type = behavior.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                FieldInfo field = type.GetField("m_stateMachine",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null || field.FieldType != typeof(StateMachine)) continue;
                StateMachine stateMachine = ModManager.ModParentField.GetParentField<StateMachine>(
                    behavior, field.Name, field.DeclaringType);
                return behavior.GetType().Name + ":" + (stateMachine?.CurrentState ?? string.Empty);
            }
            return behavior.GetType().Name;
        }

        private int GetCreatureTargetNetworkId(ComponentCreature target)
        {
            Entity targetEntity = target?.Entity;
            if (targetEntity == null || targetEntity.IsAddedToProject != true) return 0;
            if (m_hostAnimalIds.TryGetValue(targetEntity, out ushort animalId)) return animalId;
            ComponentPlayer targetPlayer = targetEntity.FindComponent<ComponentPlayer>();
            if (targetPlayer == null) return 0;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (item.Value?.ComponentPlayer == targetPlayer) return -(item.Key + 1);
            }
            return -(client.ClientID + 1);
        }

        private double GetAnimalSyncInterval(byte tier)
        {
            // Source: ScMultiplayer.cs:HandleAnimalInteractionMessage
            // Player targets use 4Hz, any animal within 10 blocks uses 8Hz, and direct
            // interaction, herd help, or an active player attack uses 16Hz.
            if (IsSleepAccelerationActive(GameManager.Project))
            {
                // During the shared sleep catch-up, ordinary animals are replaceable presentation
                // state. Keep targets and nearby animals responsive, while attacks and damage keep
                // the normal 16Hz cadence so wake-up events cannot be hidden behind throttling.
                return tier >= 3 ? 0.0625 : tier >= 1 ? 0.25 : 1.0;
            }
            return tier >= 3 ? 0.0625 : tier >= 2 ? 0.125 : tier >= 1 ? 0.25 : 0.5;
        }

        private int CalculateAnimalSimulationSeed(ushort id)
        {
            unchecked
            {
                uint value = (uint)m_sessionRandomSeed;
                value = (value ^ id) * 16777619u;
                value ^= value >> 16;
                return (int)value;
            }
        }

        // Source: Survivalcraft/Game/Random.cs:Random.Seed
        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.m_random
        private void ApplyAnimalSimulationSeed(Entity entity, int seed)
        {
            if (entity == null) return;
            foreach (Component component in entity.Components)
            {
                for (Type type = component.GetType();
                    type != null && typeof(Component).IsAssignableFrom(type);
                    type = type.BaseType)
                {
                    FieldInfo field = type.GetField("m_random",
                        BindingFlags.Instance | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                    if (field == null || field.FieldType != typeof(Game.Random)) continue;
                    int componentSeed = CombineSimulationSeed(seed, type.FullName);
                    ModManager.ModParentField.ModifyParentField(component, field.Name,
                        new Game.Random(componentSeed), field.DeclaringType);
                }
            }
        }

        private static int CombineSimulationSeed(int seed, string text)
        {
            unchecked
            {
                uint value = (uint)seed;
                foreach (char character in text ?? string.Empty)
                    value = (value ^ character) * 16777619u;
                return (int)value;
            }
        }

        // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Update
        private bool IsAnimalAttackActive(ComponentChaseBehavior chase,
            ComponentCreatureModel model)
        {
            if (model?.AttackOrder == true || model?.IsAttackHitMoment == true) return true;
            ComponentBody targetBody = chase?.Target?.ComponentBody;
            if (targetBody == null || chase.IsActive != true) return false;
            return ModManager.ModParentMethod.InvokeParentMethod<bool>(
                chase, "IsTargetInAttackRange", targetBody);
        }

        // Source: Survivalcraft/Game/ComponentEatPickableBehavior.cs:ComponentEatPickableBehavior.Update
        // Source: Survivalcraft/Game/ComponentRandomFeedBehavior.cs:ComponentRandomFeedBehavior.Update
        // Source: Survivalcraft/Game/ComponentRandomPeckBehavior.cs:ComponentRandomPeckBehavior.Update
        private static bool IsAnimalFeedActive(ComponentCreatureModel model,
            string behaviorState)
        {
            if (model?.FeedOrder == true) return true;
            int separator = behaviorState?.LastIndexOf(':') ?? -1;
            string stateName = separator >= 0 ? behaviorState.Substring(separator + 1) : string.Empty;
            return stateName == "Eat" || stateName == "Feed" || stateName == "Peck";
        }

    // Source: Survivalcraft/Game/ComponentMount.cs:ComponentMount.Load
    // Boats do not contain ComponentCreature, so they are synchronized through the same
    // authoritative body channel with a separate ID range. This keeps rider binding independent
    // from the animal-only simulation and lets clients display a moving boat without simulating it.
    private void SendMountUpdates()
    {
        Project project = GameManager.Project;
        SubsystemBodies subsystemBodies = project?.FindSubsystem<SubsystemBodies>(false);
        if (subsystemBodies == null) return;

        Entity[] mounts = subsystemBodies.Bodies
            .Select(body => body?.Entity)
            .Where(entity => entity?.FindComponent<ComponentMount>() != null &&
                entity.FindComponent<ComponentPlayer>() == null &&
                entity.FindComponent<ComponentCreature>() == null)
            .Distinct()
            .ToArray();
        var current = new HashSet<Entity>(mounts);
        foreach (Entity removed in m_hostMountIds.Keys.Where(entity =>
            entity == null || !current.Contains(entity) || !entity.IsAddedToProject).ToArray())
        {
            ushort id = m_hostMountIds[removed];
            NetworkMessageSender.SendEntityMessage(new EntityMessage(
                id, EntityMessage.EntityAction.Remove));
            m_hostMountIds.Remove(removed);
        }

        var updates = new List<BodyUpdateMessage.BodyItem>(mounts.Length);
        foreach (Entity entity in mounts)
        {
            bool isNew = !m_hostMountIds.TryGetValue(entity, out ushort id);
            if (isNew)
            {
                id = m_nextMountId++;
                if (id < MountEntityIdStart) id = m_nextMountId = MountEntityIdStart;
                m_hostMountIds[entity] = id;
                NetworkMessageSender.SendEntityMessage(new EntityMessage(
                    id, EntityMessage.EntityAction.Add,
                    entity.ValuesDictionary?.DatabaseObject?.Name ?? "Boat"));
            }
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) continue;
            updates.Add(new BodyUpdateMessage.BodyItem
            {
                EntityId = id,
                Flags = BodyUpdateMessage.ChangeFlag.Position |
                    BodyUpdateMessage.ChangeFlag.Rotation |
                    BodyUpdateMessage.ChangeFlag.Velocity |
                    (isNew ? BodyUpdateMessage.ChangeFlag.Template :
                        BodyUpdateMessage.ChangeFlag.None),
                Position = body.Position,
                Rotation = body.Rotation,
                Velocity = body.Velocity,
                TemplateName = entity.ValuesDictionary?.DatabaseObject?.Name ?? "Boat"
            });
        }
        if (updates.Count > 0)
        {
            NetworkMessageSender.SendBodyUpdateMessage(new BodyUpdateMessage
            {
                ServerTick = client.Step,
                IsFullSnapshot = false,
                Bodies = updates
            });
        }
    }
}
}

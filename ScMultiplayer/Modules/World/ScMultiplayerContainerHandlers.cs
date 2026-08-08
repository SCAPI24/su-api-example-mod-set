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
        private void SynchronizeContainers()
        {
            Project project = GameManager.Project;
            if (project == null) return;
            bool forceFullSync = IsHost && m_forceContainerFullSync;
            NetworkContainerReference openContainer = !IsHost ? GetOpenNetworkContainer() : null;
            NetworkContainerReference hostOpenContainer = IsHost ? GetOpenNetworkContainer() : null;
            NetworkContainerReference requestContainer = openContainer ??
                (!IsHost && m_wasNetworkContainerOpen ? m_openContainer : null);
            bool allowClientRequest = requestContainer != null;
            IInventory localPlayerInventory = !IsHost ? GetLocalPlayerInventory() : null;
            if (!IsHost) UpdateOpenContainerBaseline(openContainer);
            foreach (NetworkContainerReference container in EnumerateNetworkContainers(project))
            {
                ComponentInventoryBase inventory = container.Inventory;
                string key = container.Key;
                int[] values = CaptureInventoryValues(inventory);
                int[] counts = CaptureInventoryCounts(inventory);
                if (IsHost)
                {
                    if (m_containerStates.TryGetValue(key, out ContainerNetworkState state) &&
                        ArraysEqual(values, state.Values) && ArraysEqual(counts, state.Counts))
                    {
                        if (forceFullSync) SendContainerState(container, state);
                        continue;
                    }
                    state = AdvanceContainerState(state, values, counts);
                    m_containerStates[key] = state;
                    if (inventory is ComponentCraftingTable craftingTable &&
                        hostOpenContainer?.Key == key)
                        DisplayLocalCraftingFeedback(craftingTable);
                    SendContainerState(container, state);
                }
                else
                {
                    if (inventory is IUpdateable updateable &&
                        m_disabledClientContainerUpdates.Add(updateable))
                        QueueEndOfFrameAction(() =>
                            project.FindSubsystem<SubsystemUpdate>(true).RemoveUpdateable(updateable));
                    if (m_pendingContainerTransactions.TryGetValue(key,
                            out PendingContainerTransaction pending))
                    {
                        if (Time.RealTime - pending.LastSendTime >= 2.0)
                        {
                            pending.LastSendTime = Time.RealTime;
                            SendContainerRequest(pending.Request);
                        }
                        continue;
                    }
                    if (!m_containerStates.TryGetValue(key, out ContainerNetworkState state) ||
                        ArraysEqual(values, state.Values) && ArraysEqual(counts, state.Counts))
                        continue;
                    if (!allowClientRequest || requestContainer.Key != key ||
                        localPlayerInventory == null || !m_hasAuthoritativeLocalInventory)
                        continue;

                    m_nextContainerRequestId = m_nextContainerRequestId == int.MaxValue
                        ? 1
                        : m_nextContainerRequestId + 1;
                    m_localEquipmentRevision = m_localEquipmentRevision == int.MaxValue
                        ? 1
                        : m_localEquipmentRevision + 1;
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
                        SlotValues = values,
                        SlotCounts = counts,
                        BaseSlotValues = (int[])state.Values.Clone(),
                        BaseSlotCounts = (int[])state.Counts.Clone(),
                        PlayerBaseSlotValues = (int[])m_authoritativeLocalSlotValues.Clone(),
                        PlayerBaseSlotCounts = (int[])m_authoritativeLocalSlotCounts.Clone(),
                        PlayerSlotValues = CaptureInventoryValues(localPlayerInventory),
                        PlayerSlotCounts = CaptureInventoryCounts(localPlayerInventory)
                    };
                    m_pendingContainerTransactions[key] = new PendingContainerTransaction
                    {
                        Request = request,
                        LastSendTime = Time.RealTime
                    };
                    SendContainerRequest(request);
                }
            }
            if (!IsHost && openContainer == null)
                m_openContainer = null;
            if (IsHost) m_forceContainerFullSync = false;
        }

        // Source: Survivalcraft/Game/FullInventoryWidget.cs:FullInventoryWidget
        // Source: Survivalcraft/Game/InventorySlotWidget.cs:InventorySlotWidget.AssignInventorySlot
        private NetworkContainerReference GetOpenNetworkContainer()
        {
            ComponentPlayer player = GetLocalPlayer();
            ContainerWidget panel = player?.ComponentGui?.ModalPanelWidget as ContainerWidget;
            string panelName = panel?.GetType().Name;
            bool supported = panelName == "ChestWidget" || panelName == "DispenserWidget" ||
                panelName == "FurnaceWidget" || panelName == "CraftingTableWidget" ||
                panelName == "FullInventoryWidget";
            if (!supported)
            {
                m_openContainerPanel = null;
                return null;
            }
            if (ReferenceEquals(panel, m_openContainerPanel) &&
                m_openContainer?.Inventory?.IsAddedToProject == true)
                return m_openContainer;

            m_openContainerPanel = panel;
            m_openContainer = null;
            m_baselineRequestedContainerKey = null;
            IInventory playerInventory = player.ComponentMiner?.Inventory;
            foreach (InventorySlotWidget slotWidget in panel.AllChildren.OfType<InventorySlotWidget>())
            {
                IInventory inventory = ModManager.ModParentField.GetParentField(
                    slotWidget, "m_inventory", typeof(InventorySlotWidget)) as IInventory;
                if (!(inventory is ComponentInventoryBase componentInventory) ||
                    ReferenceEquals(inventory, playerInventory))
                    continue;
                ComponentBlockEntity blockEntity = componentInventory.Entity?
                    .FindComponent<ComponentBlockEntity>();
                int ownerClientId = ReferenceEquals(componentInventory.Entity, player.Entity)
                    ? client.ClientID : -1;
                if (ownerClientId < 0 && blockEntity == null) continue;
                m_openContainer = CreateContainerReference(componentInventory,
                    blockEntity?.Coordinates ?? default, ownerClientId);
                break;
            }
            return m_openContainer;
        }

        private void UpdateOpenContainerBaseline(NetworkContainerReference container)
        {
            if (container == null)
            {
                m_baselineRequestedContainerKey = null;
                return;
            }
            if (container.Key == m_baselineRequestedContainerKey) return;
            m_baselineRequestedContainerKey = container.Key;
            m_nextContainerRequestId = m_nextContainerRequestId == int.MaxValue
                ? 1 : m_nextContainerRequestId + 1;
            SendContainerRequest(new ContainerSyncMessage
            {
                Coordinates = container.Coordinates,
                ComponentType = container.ComponentType,
                OwnerClientId = container.OwnerClientId,
                RequestId = m_nextContainerRequestId,
                RequesterClientId = client.ClientID,
                IsRequest = true,
                IsBaselineRequest = true
            });
        }

        private IEnumerable<NetworkContainerReference> EnumerateNetworkContainers(Project project)
        {
            foreach (Entity entity in project.Entities)
            {
                ComponentBlockEntity blockEntity = entity?.FindComponent<ComponentBlockEntity>();
                if (blockEntity == null) continue;
                foreach (ComponentInventoryBase inventory in entity.FindComponents<ComponentInventoryBase>())
                    yield return CreateContainerReference(inventory, blockEntity.Coordinates, -1);
            }

            if (IsHost)
            {
                foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
                {
                    ComponentCraftingTable crafting = item.Value?.ComponentPlayer?.Entity?
                        .FindComponent<ComponentCraftingTable>();
                    if (crafting != null)
                        yield return CreateContainerReference(crafting, default, item.Key);
                }
            }
            else
            {
                ComponentCraftingTable crafting = GetLocalPlayer()?.Entity?
                    .FindComponent<ComponentCraftingTable>();
                if (crafting != null && client != null)
                    yield return CreateContainerReference(crafting, default, client.ClientID);
            }
        }

        private static NetworkContainerReference CreateContainerReference(
            ComponentInventoryBase inventory, Point3 coordinates, int ownerClientId)
        {
            string type = inventory.GetType().FullName;
            return new NetworkContainerReference
            {
                Inventory = inventory,
                Coordinates = coordinates,
                OwnerClientId = ownerClientId,
                ComponentType = type,
                Key = GetContainerKey(coordinates, type, ownerClientId)
            };
        }

        private void HandleContainerSyncMessage(ContainerSyncMessage message, int sourceClientId)
        {
            if (message == null || GameManager.Project == null) return;
            ComponentInventoryBase inventory = FindContainer(message.Coordinates,
                message.ComponentType, message.OwnerClientId);
            if (inventory == null) return;
            string key = GetContainerKey(message.Coordinates, message.ComponentType,
                message.OwnerClientId);
            if (IsHost)
            {
                if (!message.IsRequest || sourceClientId <= 0 ||
                    message.RequesterClientId != sourceClientId ||
                    !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData player) ||
                    player?.ComponentPlayer?.ComponentBody == null ||
                    (message.OwnerClientId >= 0
                        ? message.OwnerClientId != sourceClientId
                        : Vector3.DistanceSquared(player.ComponentPlayer.ComponentBody.Position,
                            new Vector3(message.Coordinates) + new Vector3(0.5f)) > 8f * 8f))
                    return;

                int[] currentValues = CaptureInventoryValues(inventory);
                int[] currentCounts = CaptureInventoryCounts(inventory);
                if (!m_containerStates.TryGetValue(key, out ContainerNetworkState state))
                {
                    state = AdvanceContainerState(null, currentValues, currentCounts);
                    m_containerStates[key] = state;
                }
                else if (!ArraysEqual(currentValues, state.Values) ||
                    !ArraysEqual(currentCounts, state.Counts))
                {
                    state = AdvanceContainerState(state, currentValues, currentCounts);
                    m_containerStates[key] = state;
                    SendContainerState(CreateContainerReference(inventory,
                        message.Coordinates, message.OwnerClientId), state);
                }

                if (message.IsBaselineRequest)
                {
                    ContainerSyncMessage baseline = CreateContainerResponse(message, state,
                        sourceClientId, 0, null);
                    SendContainerResponse(baseline, sourceClientId);
                    return;
                }
                if (message.RequestId <= 0) return;

                string transactionKey = sourceClientId + "|" + key;
                if (m_processedContainerTransactions.TryGetValue(transactionKey,
                        out ProcessedContainerTransaction processed) &&
                    message.RequestId <= processed.RequestId)
                {
                    if (message.RequestId == processed.RequestId && processed.Response != null)
                        SendContainerResponse(processed.Response, sourceClientId);
                    return;
                }

                IInventory playerInventory = player.ComponentPlayer.ComponentMiner?.Inventory;
                ComponentCraftingTable craftingTable = inventory as ComponentCraftingTable;
                int craftedResultCount = 0;
                int craftedResultValue = craftingTable != null
                    ? NormalizeCrossbowValue(craftingTable.GetSlotValue(
                        craftingTable.ResultSlotIndex)) : 0;
                bool isContainerDrop = IsContainerDropRequestValid(inventory, message);
                int craftedDropCount = 0;
                bool isCraftingResultDrop = craftingTable != null && message.IsDrop &&
                    TryGetCraftingResultDrop(craftingTable, message,
                        out craftedDropCount);
                int craftingTargetSlot = -1;
                bool isCraftingResultTransfer = !message.IsDrop && craftingTable != null &&
                    TryGetCraftingResultTransfer(craftingTable, message,
                        out craftedResultCount, out craftingTargetSlot);
                bool validRequest = message.Revision == state.Revision &&
                    ArraysEqual(message.BaseSlotValues, state.Values) &&
                    ArraysEqual(message.BaseSlotCounts, state.Counts) &&
                    IsInventorySnapshotValid(inventory,
                        message.SlotValues, message.SlotCounts) &&
                    IsInventorySnapshotValid(playerInventory,
                        message.PlayerBaseSlotValues, message.PlayerBaseSlotCounts) &&
                    IsInventorySnapshotValid(playerInventory,
                        message.PlayerSlotValues, message.PlayerSlotCounts) &&
                    (InventoryMatches(playerInventory, message.PlayerBaseSlotValues,
                        message.PlayerBaseSlotCounts) ||
                    InventoryMatches(playerInventory, message.PlayerSlotValues,
                        message.PlayerSlotCounts)) &&
                    (craftingTable == null || isCraftingResultTransfer ||
                    isCraftingResultDrop || IsCraftingRemainsRemoval(craftingTable, message)) &&
                    (isContainerDrop || isCraftingResultDrop || !message.IsDrop &&
                    (playerInventory is ComponentCreativeInventory ||
                    isCraftingResultTransfer || HaveSameCombinedItems(inventory,
                        message.BaseSlotValues, message.BaseSlotCounts,
                        message.PlayerBaseSlotValues, message.PlayerBaseSlotCounts,
                        message.SlotValues, message.SlotCounts,
                        message.PlayerSlotValues, message.PlayerSlotCounts)));

                if (validRequest)
                {
                    if (isCraftingResultTransfer || isCraftingResultDrop)
                    {
                        // Source: Survivalcraft/Game/ComponentCraftingTable.RemoveSlotItems
                        // The crafting result and remains slots are host-derived. Calling the
                        // original removal method consumes the matching grid items atomically.
                        validRequest = craftingTable.RemoveSlotItems(
                            craftingTable.ResultSlotIndex,
                            isCraftingResultDrop ? craftedDropCount : craftedResultCount) ==
                            (isCraftingResultDrop ? craftedDropCount : craftedResultCount);
                        if (validRequest && isCraftingResultDrop)
                        {
                            ComponentBody body = player.ComponentPlayer.ComponentBody;
                            Vector3 defaultPosition = body.Position +
                                new Vector3(0f, body.StanceBoxSize.Y * 0.66f, 0f) +
                                0.25f * body.Matrix.Forward;
                            Vector3 position = Vector3.DistanceSquared(body.Position,
                                message.DropPosition) <= 64f
                                ? message.DropPosition : defaultPosition;
                            Vector3 velocity = message.DropVelocity;
                            if (velocity.LengthSquared() > 20f * 20f)
                                velocity = Vector3.Normalize(velocity) * 20f;
                            GameManager.Project.FindSubsystem<SubsystemPickables>(true)
                                .AddPickable(craftedResultValue, craftedDropCount,
                                    position, velocity, null);
                        }
                        else if (validRequest)
                        {
                            if (craftingTargetSlot >= 0)
                                craftingTable.AddSlotItems(craftingTargetSlot,
                                    craftedResultValue,
                                    craftedResultCount);
                            else
                                ApplyInventory(playerInventory,
                                    message.PlayerSlotValues, message.PlayerSlotCounts);
                        }
                    }
                    else
                    {
                        ApplyInventory(inventory, message.SlotValues, message.SlotCounts);
                        ApplyInventory(playerInventory,
                            message.PlayerSlotValues, message.PlayerSlotCounts);
                        if (isContainerDrop)
                        {
                            ComponentBody body = player.ComponentPlayer.ComponentBody;
                            Vector3 defaultPosition = body.Position +
                                new Vector3(0f, body.StanceBoxSize.Y * 0.66f, 0f) +
                                0.25f * body.Matrix.Forward;
                            Vector3 position = Vector3.DistanceSquared(body.Position,
                                message.DropPosition) <= 64f
                                ? message.DropPosition
                                : defaultPosition;
                            Vector3 velocity = message.DropVelocity;
                            if (velocity.LengthSquared() > 20f * 20f)
                                velocity = Vector3.Normalize(velocity) * 20f;
                            GameManager.Project.FindSubsystem<SubsystemPickables>(true)
                                .AddPickable(NormalizeCrossbowValue(message.DropValue),
                                    message.DropCount,
                                    position, velocity, null);
                        }
                    }
                    if (validRequest)
                    {
                        state = AdvanceContainerState(state,
                            CaptureInventoryValues(inventory), CaptureInventoryCounts(inventory));
                        m_containerStates[key] = state;
                    }
                }
                else if (playerInventory != null &&
                    InventoryMatches(playerInventory, message.PlayerSlotValues,
                        message.PlayerSlotCounts) &&
                    IsInventorySnapshotValid(playerInventory,
                        message.PlayerBaseSlotValues, message.PlayerBaseSlotCounts))
                {
                    // Source: ScMultiplayer.HandlePlayerEquipmentMessage
                    // An unordered equipment snapshot can arrive just before a rejected container
                    // transaction. Restore its declared base so stale furnace output cannot remain
                    // in the player inventory after the container authority rejects the transfer.
                    ApplyInventory(playerInventory,
                        message.PlayerBaseSlotValues, message.PlayerBaseSlotCounts);
                }

                int playerRevision = PublishPlayerInventoryAuthority(
                    sourceClientId, player.ComponentPlayer, message.PlayerRevision);
                var response = CreateContainerResponse(message, state,
                    sourceClientId, playerRevision, playerInventory);
                m_processedContainerTransactions[transactionKey] =
                    new ProcessedContainerTransaction
                    {
                        RequestId = message.RequestId,
                        ProcessedTime = Time.RealTime,
                        Response = response
                    };
                foreach (string stale in m_processedContainerTransactions.Where(item =>
                    Time.RealTime - item.Value.ProcessedTime > 30.0)
                    .Select(item => item.Key).ToArray())
                    m_processedContainerTransactions.Remove(stale);
                PublishServerAudit("container.transaction", sourceClientId,
                    "request=" + message.RequestId.ToString(CultureInfo.InvariantCulture) +
                    " accepted=" + validRequest.ToString(CultureInfo.InvariantCulture) +
                    " scope=" + (message.OwnerClientId >= 0 ? "handcraft" : "block") +
                    " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture));
                SendContainerResponse(response,
                    message.OwnerClientId >= 0 ? sourceClientId : -1);
                return;
            }
            if (sourceClientId != 0 || message.IsRequest) return;
            bool matchingPending = message.RequesterClientId == client.ClientID &&
                m_pendingContainerTransactions.TryGetValue(key,
                    out PendingContainerTransaction pendingTransaction) &&
                pendingTransaction.Request.RequestId == message.RequestId;
            if (!matchingPending && m_containerStates.TryGetValue(key,
                    out ContainerNetworkState oldState) &&
                (message.Revision < oldState.Revision ||
                    !message.IsBaselineRequest && message.Revision == oldState.Revision))
                return;
            ApplyInventory(inventory, message.SlotValues, message.SlotCounts);
            m_containerStates[key] = new ContainerNetworkState
            {
                Revision = message.Revision,
                Values = (int[])message.SlotValues.Clone(),
                Counts = (int[])message.SlotCounts.Clone()
            };
            if (matchingPending)
            {
                m_pendingContainerTransactions.Remove(key);
                ApplyContainerPlayerAuthority(message);
            }
        }

        // Source: ScMultiplayer.SynchronizePlayerEquipment
        private int PublishPlayerInventoryAuthority(int clientId,
            ComponentPlayer player, int clientRevision)
        {
            if (player == null) return 0;
            m_lastClientEquipmentRevisions.TryGetValue(clientId, out int lastClientRevision);
            m_lastClientEquipmentRevisions[clientId] = Math.Max(lastClientRevision,
                clientRevision);
            int currentAuthority = m_equipmentAuthorityRevisions.TryGetValue(clientId,
                out int authorityRevision) ? authorityRevision : 0;
            int revision = Math.Max(currentAuthority == int.MaxValue ? 1 : currentAuthority + 1,
                clientRevision == int.MaxValue ? 1 : clientRevision + 1);
            EquipmentSnapshot snapshot = CaptureEquipmentSnapshot(player);
            m_equipmentAuthorityRevisions[clientId] = revision;
            m_lastReceivedEquipmentRevisions[clientId] = revision;
            m_lastEquipmentSnapshots[clientId] = snapshot;
            m_equipmentSynchronizedClients.Add(clientId);
            MarkHostInventoryAuthoritative(clientId);
            BroadcastPlayerEquipment(clientId, revision, snapshot);
            return revision;
        }

        private void ApplyContainerPlayerAuthority(ContainerSyncMessage message)
        {
            if (message.PlayerSlotValues == null || message.PlayerSlotCounts == null ||
                message.PlayerSlotValues.Length == 0 ||
                message.PlayerRevision < m_lastReceivedEquipmentRevisions.GetValueOrDefault(
                    client.ClientID))
                return;
            IInventory inventory = GetLocalPlayerInventory();
            if (inventory == null) return;
            ApplyInventory(inventory, message.PlayerSlotValues, message.PlayerSlotCounts);
            int slotsCount = Math.Min(inventory.SlotsCount,
                Math.Min(message.PlayerSlotValues.Length, message.PlayerSlotCounts.Length));
            m_authoritativeLocalSlotValues = message.PlayerSlotValues.Take(slotsCount).ToArray();
            m_authoritativeLocalSlotCounts = message.PlayerSlotCounts.Take(slotsCount).ToArray();
            m_lastAuthoritativeLocalInventoryTick = Math.Max(
                m_lastAuthoritativeLocalInventoryTick, client.Step);
            m_hasAuthoritativeLocalInventory = true;
            m_localEquipmentRevision = Math.Max(m_localEquipmentRevision,
                message.PlayerRevision);
            m_lastReceivedEquipmentRevisions[client.ClientID] = message.PlayerRevision;
            ComponentPlayer player = GetLocalPlayer();
            if (player != null)
                m_lastEquipmentSnapshots[client.ClientID] = CaptureEquipmentSnapshot(player);
            m_lastLocalInventoryValues = CaptureInventoryValues(inventory);
            m_lastLocalInventoryCounts = CaptureInventoryCounts(inventory);
        }

        private static string GetContainerKey(Point3 point, string type,
            int ownerClientId = -1) => ownerClientId >= 0
                ? "player:" + ownerClientId.ToString(CultureInfo.InvariantCulture) + ":" + type
                : point.X + "," + point.Y + "," + point.Z + ":" + type;

        private ComponentInventoryBase FindContainer(Point3 point, string type,
            int ownerClientId)
        {
            if (ownerClientId >= 0)
            {
                ComponentPlayer owner;
                if (IsHost)
                {
                    owner = m_networkPlayerData.TryGetValue(ownerClientId,
                        out PlayerData playerData) ? playerData?.ComponentPlayer : null;
                }
                else
                {
                    owner = client?.ClientID == ownerClientId ? GetLocalPlayer() : null;
                }
                return owner?.Entity?.FindComponents<ComponentInventoryBase>()
                    .FirstOrDefault(item => item.GetType().FullName == type);
            }
            foreach (Entity entity in GameManager.Project.Entities)
            {
                ComponentBlockEntity blockEntity = entity?.FindComponent<ComponentBlockEntity>();
                if (blockEntity == null || blockEntity.Coordinates != point) continue;
                return entity.FindComponents<ComponentInventoryBase>()
                    .FirstOrDefault(item => item.GetType().FullName == type);
            }
            return null;
        }

        private static int[] CaptureInventoryValues(IInventory inventory) =>
            Enumerable.Range(0, inventory.SlotsCount)
                .Select(index => NormalizeCrossbowValue(inventory.GetSlotValue(index)))
                .ToArray();

        private static int[] CaptureInventoryCounts(IInventory inventory) =>
            Enumerable.Range(0, inventory.SlotsCount).Select(inventory.GetSlotCount).ToArray();

        private static bool ArraysEqual(int[] a, int[] b) =>
            a != null && b != null && a.SequenceEqual(b);

        // Source: Survivalcraft/Game/CrossbowBlock.cs:CrossbowBlock.GetDraw
        // CrossbowBlockBehavior only accepts a bolt at draw==15. Normalize the one invalid value
        // that the native failed-FireProjectile path can leave behind; do not touch bow or musket
        // data and do not alter item counts.
        private static int NormalizeCrossbowValue(int value)
        {
            if (value == 0 || Terrain.ExtractContents(value) != 200)
                return value;
            int data = Terrain.ExtractData(value);
            if (!CrossbowBlock.GetArrowType(data).HasValue ||
                CrossbowBlock.GetDraw(data) == 15)
                return value;
            return Terrain.MakeBlockValue(200, 0, CrossbowBlock.SetDraw(data, 15));
        }

        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.AddSlotItems
        private static void NormalizeCrossbowSlot(IInventory inventory, int slotIndex)
        {
            if (inventory == null || slotIndex < 0 || slotIndex >= inventory.SlotsCount)
                return;
            int value = inventory.GetSlotValue(slotIndex);
            int normalized = NormalizeCrossbowValue(value);
            if (normalized == value) return;
            int count = inventory.GetSlotCount(slotIndex);
            inventory.RemoveSlotItems(slotIndex, int.MaxValue);
            if (count > 0) inventory.AddSlotItems(slotIndex, normalized, count);
        }

        private ComponentPlayer GetLocalPlayer() =>
            GameManager.Project?.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(player =>
                    player != null && !m_networkPlayerData.Values.Contains(player.PlayerData));

        private IInventory GetLocalPlayerInventory() =>
            GetLocalPlayer()?.ComponentMiner?.Inventory;

        // Source: Survivalcraft/Game/ComponentCraftingTable.cs:
        // ComponentCraftingTable.UpdateCraftingResult
        // The native component sends recipe feedback to the nearest player, which can be a
        // remote avatar on the host. Route the same final-grid feedback to the local operator.
        private void DisplayLocalCraftingFeedback(ComponentCraftingTable craftingTable)
        {
            ComponentPlayer localPlayer = GetLocalPlayer();
            if (craftingTable == null || localPlayer?.ComponentGui == null ||
                ReferenceEquals(craftingTable.FindInteractingPlayer(), localPlayer))
                return;
            int gridSize = (int)MathUtils.Sqrt(craftingTable.SlotsCount - 2);
            if (gridSize < 1 || gridSize > 3) return;
            var ingredients = new string[9];
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    int slotIndex = x + y * gridSize;
                    if (craftingTable.GetSlotCount(slotIndex) <= 0) continue;
                    int value = craftingTable.GetSlotValue(slotIndex);
                    Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
                    ingredients[x + y * 3] = block.CraftingId + ":" +
                        Terrain.ExtractData(value).ToString(CultureInfo.InvariantCulture);
                }
            }
            SubsystemTerrain terrain = GameManager.Project?
                .FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null) return;
            CraftingRecipe recipe = CraftingRecipesManager.FindMatchingRecipe(terrain,
                ingredients, 0f, localPlayer.PlayerData.Level);
            if (!string.IsNullOrEmpty(recipe?.Message))
                localPlayer.ComponentGui.DisplaySmallMessage(recipe.Message, Color.White,
                    blinking: true, playNotificationSound: true);
        }

        // Source: Survivalcraft/Game/SubsystemFurnaceBlockBehavior.cs:
        // SubsystemFurnaceBlockBehavior.OnInteract
        private bool IsNetworkContainerOpen()
        {
            return GetOpenNetworkContainer() != null;
        }

        private static bool IsInventorySnapshotValid(IInventory inventory,
            int[] values, int[] counts)
        {
            if (inventory == null || values == null || counts == null ||
                values.Length != inventory.SlotsCount || counts.Length != inventory.SlotsCount)
                return false;
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                if (IsCraftingTableResultSlot(inventory, i))
                    continue;
                int value = NormalizeCrossbowValue(values[i]);
                if (counts[i] < 0 || counts[i] > 0 && value == 0) return false;
                if (counts[i] == 0) continue;
                if (inventory is ComponentCraftingTable craftingTable &&
                    i == craftingTable.RemainsSlotIndex)
                    continue;
                try
                {
                    if (counts[i] > inventory.GetSlotCapacity(i, value)) return false;
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        // Source: Survivalcraft/Game/ComponentCraftingTable.ResultSlotIndex
        // Source: Survivalcraft/Game/ComponentCraftingTable.RemainsSlotIndex
        private static bool IsCraftingTableResultSlot(IInventory inventory, int slotIndex)
        {
            if (!(inventory is ComponentCraftingTable craftingTable))
                return false;
            return slotIndex == craftingTable.ResultSlotIndex;
        }

        // Source: Survivalcraft/Game/ComponentCraftingTable.RemoveSlotItems
        private static bool TryGetCraftingResultTransfer(ComponentCraftingTable craftingTable,
            ContainerSyncMessage message, out int craftedResultCount,
            out int craftingTargetSlot)
        {
            craftedResultCount = 0;
            craftingTargetSlot = -1;
            if (craftingTable == null || message?.BaseSlotValues == null ||
                message.BaseSlotCounts == null || message.SlotValues == null ||
                message.SlotCounts == null || message.PlayerBaseSlotValues == null ||
                message.PlayerBaseSlotCounts == null || message.PlayerSlotValues == null ||
                message.PlayerSlotCounts == null ||
                message.BaseSlotValues.Length != craftingTable.SlotsCount ||
                message.BaseSlotCounts.Length != craftingTable.SlotsCount ||
                message.SlotValues.Length != craftingTable.SlotsCount ||
                message.SlotCounts.Length != craftingTable.SlotsCount ||
                message.PlayerBaseSlotValues.Length != message.PlayerSlotValues.Length ||
                message.PlayerBaseSlotCounts.Length != message.PlayerSlotCounts.Length ||
                message.PlayerBaseSlotValues.Length != message.PlayerBaseSlotCounts.Length)
                return false;

            int resultSlotIndex = craftingTable.ResultSlotIndex;
            int remainsSlotIndex = craftingTable.RemainsSlotIndex;
            int resultValue = NormalizeCrossbowValue(craftingTable.GetSlotValue(resultSlotIndex));
            int availableResultCount = craftingTable.GetSlotCount(resultSlotIndex);
            if (resultValue == 0 || availableResultCount <= 0 ||
                NormalizeCrossbowValue(message.BaseSlotValues[resultSlotIndex]) != resultValue ||
                message.BaseSlotCounts[resultSlotIndex] != availableResultCount)
                return false;

            CraftingRecipe recipe;
            string[] matchedIngredients;
            try
            {
                recipe = ModManager.ModParentField.GetParentField<CraftingRecipe>(
                    craftingTable, "m_matchedRecipe", typeof(ComponentCraftingTable));
                matchedIngredients = ModManager.ModParentField.GetParentField<string[]>(
                    craftingTable, "m_matchedIngredients", typeof(ComponentCraftingTable));
            }
            catch
            {
                return false;
            }
            if (recipe == null || recipe.ResultCount <= 0 ||
                NormalizeCrossbowValue(recipe.ResultValue) != resultValue ||
                matchedIngredients == null || matchedIngredients.Length < 9)
                return false;

            int gridSize = (int)MathUtils.Sqrt(craftingTable.SlotsCount - 2);
            if (gridSize < 1 || gridSize > 3)
                return false;
            int maximumBatches = availableResultCount / recipe.ResultCount;
            for (int batches = 1; batches <= maximumBatches; batches++)
            {
                int expectedCraftedCount = batches * recipe.ResultCount;
                long addedResultCount = 0;
                int addedDestinations = 0;
                int candidateCraftingTarget = -1;
                bool valid = true;

                for (int i = 0; i < message.PlayerSlotValues.Length && valid; i++)
                {
                    int baseCount = message.PlayerBaseSlotCounts[i];
                    int desiredCount = message.PlayerSlotCounts[i];
                    int baseValue = baseCount > 0
                        ? NormalizeCrossbowValue(message.PlayerBaseSlotValues[i]) : 0;
                    int desiredValue = desiredCount > 0
                        ? NormalizeCrossbowValue(message.PlayerSlotValues[i]) : 0;
                    if (baseCount == desiredCount && baseValue == desiredValue) continue;
                    if (desiredCount <= baseCount || desiredValue != resultValue ||
                        (baseCount > 0 && baseValue != resultValue))
                    {
                        valid = false;
                        break;
                    }
                    addedResultCount += desiredCount - baseCount;
                    addedDestinations++;
                }

                for (int y = 0; y < gridSize && valid; y++)
                {
                    for (int x = 0; x < gridSize; x++)
                    {
                        int slotIndex = x + y * gridSize;
                        int ingredientIndex = x + y * 3;
                        int baseCount = message.BaseSlotCounts[slotIndex];
                        int expectedCount = !string.IsNullOrEmpty(
                            matchedIngredients[ingredientIndex]) ? baseCount - batches : baseCount;
                        if (expectedCount < 0)
                        {
                            valid = false;
                            break;
                        }
                        int expectedValue = expectedCount > 0
                            ? NormalizeCrossbowValue(message.BaseSlotValues[slotIndex]) : 0;
                        int desiredCount = message.SlotCounts[slotIndex];
                        int desiredValue = desiredCount > 0
                            ? NormalizeCrossbowValue(message.SlotValues[slotIndex]) : 0;
                        if (desiredCount == expectedCount && desiredValue == expectedValue)
                            continue;
                        if (desiredCount <= expectedCount || desiredValue != resultValue ||
                            (expectedCount > 0 && expectedValue != resultValue))
                        {
                            valid = false;
                            break;
                        }
                        addedResultCount += desiredCount - expectedCount;
                        addedDestinations++;
                        candidateCraftingTarget = slotIndex;
                    }
                }

                int baseRemainsCount = message.BaseSlotCounts[remainsSlotIndex];
                int baseRemainsValue = baseRemainsCount > 0
                    ? NormalizeCrossbowValue(message.BaseSlotValues[remainsSlotIndex]) : 0;
                int expectedRemainsValue = NormalizeCrossbowValue(recipe.RemainsValue);
                int expectedRemainsCount = baseRemainsCount;
                if (recipe.RemainsValue != 0 && recipe.RemainsCount > 0)
                {
                    if (baseRemainsCount > 0 && baseRemainsValue != expectedRemainsValue)
                        valid = false;
                    expectedRemainsCount += batches * recipe.RemainsCount;
                }
                else
                {
                    expectedRemainsValue = baseRemainsValue;
                }
                int desiredRemainsCount = message.SlotCounts[remainsSlotIndex];
                int desiredRemainsValue = desiredRemainsCount > 0
                    ? NormalizeCrossbowValue(message.SlotValues[remainsSlotIndex]) : 0;
                if (desiredRemainsCount != expectedRemainsCount ||
                    desiredRemainsValue != (expectedRemainsCount > 0 ? expectedRemainsValue : 0))
                    valid = false;

                if (valid && addedDestinations == 1 &&
                    addedResultCount == expectedCraftedCount)
                {
                    craftedResultCount = expectedCraftedCount;
                    craftingTargetSlot = candidateCraftingTarget;
                    return true;
                }
            }
            return false;
        }

        private static bool IsCraftingRemainsRemoval(ComponentCraftingTable craftingTable,
            ContainerSyncMessage message)
        {
            int slot = craftingTable.RemainsSlotIndex;
            if (message?.BaseSlotValues == null || message.BaseSlotCounts == null ||
                message.SlotValues == null || message.SlotCounts == null ||
                slot >= message.BaseSlotValues.Length || slot >= message.BaseSlotCounts.Length ||
                slot >= message.SlotValues.Length || slot >= message.SlotCounts.Length)
                return false;
            int baseCount = message.BaseSlotCounts[slot];
            int desiredCount = message.SlotCounts[slot];
            int baseValue = baseCount > 0
                ? NormalizeCrossbowValue(message.BaseSlotValues[slot]) : 0;
            int desiredValue = desiredCount > 0
                ? NormalizeCrossbowValue(message.SlotValues[slot]) : 0;
            return desiredCount >= 0 && desiredCount <= baseCount &&
                (desiredCount == 0 || desiredValue == baseValue);
        }

        // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
        // Source: Survivalcraft/Game/ComponentCraftingTable.cs:
        // ComponentCraftingTable.RemoveSlotItems
        private static bool TryGetCraftingResultDrop(ComponentCraftingTable craftingTable,
            ContainerSyncMessage message, out int craftedResultCount)
        {
            craftedResultCount = 0;
            if (craftingTable == null || message?.IsDrop != true ||
                message.DropCount <= 0 || !IsFinite(message.DropPosition) ||
                !IsFinite(message.DropVelocity) ||
                !ArraysEqual(message.PlayerBaseSlotValues, message.PlayerSlotValues) ||
                !ArraysEqual(message.PlayerBaseSlotCounts, message.PlayerSlotCounts) ||
                message.BaseSlotValues?.Length != craftingTable.SlotsCount ||
                message.BaseSlotCounts?.Length != craftingTable.SlotsCount ||
                message.SlotValues?.Length != craftingTable.SlotsCount ||
                message.SlotCounts?.Length != craftingTable.SlotsCount)
                return false;

            int resultSlot = craftingTable.ResultSlotIndex;
            int remainsSlot = craftingTable.RemainsSlotIndex;
            int resultValue = NormalizeCrossbowValue(craftingTable.GetSlotValue(resultSlot));
            int available = craftingTable.GetSlotCount(resultSlot);
            if (resultValue == 0 || NormalizeCrossbowValue(message.DropValue) != resultValue ||
                message.DropCount > available ||
                NormalizeCrossbowValue(message.BaseSlotValues[resultSlot]) != resultValue ||
                message.BaseSlotCounts[resultSlot] != available)
                return false;

            CraftingRecipe recipe;
            string[] matchedIngredients;
            try
            {
                recipe = ModManager.ModParentField.GetParentField<CraftingRecipe>(
                    craftingTable, "m_matchedRecipe", typeof(ComponentCraftingTable));
                matchedIngredients = ModManager.ModParentField.GetParentField<string[]>(
                    craftingTable, "m_matchedIngredients", typeof(ComponentCraftingTable));
            }
            catch
            {
                return false;
            }
            if (recipe == null || recipe.ResultCount <= 0 ||
                message.DropCount % recipe.ResultCount != 0 ||
                NormalizeCrossbowValue(recipe.ResultValue) != resultValue ||
                matchedIngredients == null || matchedIngredients.Length < 9)
                return false;

            int batches = message.DropCount / recipe.ResultCount;
            int gridSize = (int)MathUtils.Sqrt(craftingTable.SlotsCount - 2);
            if (gridSize < 1 || gridSize > 3) return false;
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    int slot = x + y * gridSize;
                    int ingredient = x + y * 3;
                    int expectedCount = message.BaseSlotCounts[slot] -
                        (!string.IsNullOrEmpty(matchedIngredients[ingredient]) ? batches : 0);
                    int expectedValue = expectedCount > 0
                        ? NormalizeCrossbowValue(message.BaseSlotValues[slot]) : 0;
                    int desiredValue = message.SlotCounts[slot] > 0
                        ? NormalizeCrossbowValue(message.SlotValues[slot]) : 0;
                    if (expectedCount < 0 || message.SlotCounts[slot] != expectedCount ||
                        desiredValue != expectedValue)
                        return false;
                }
            }

            int baseRemainsCount = message.BaseSlotCounts[remainsSlot];
            int baseRemainsValue = baseRemainsCount > 0
                ? NormalizeCrossbowValue(message.BaseSlotValues[remainsSlot]) : 0;
            int expectedRemainsValue = NormalizeCrossbowValue(recipe.RemainsValue);
            int expectedRemainsCount = baseRemainsCount;
            if (recipe.RemainsValue != 0 && recipe.RemainsCount > 0)
            {
                if (baseRemainsCount > 0 && baseRemainsValue != expectedRemainsValue)
                    return false;
                expectedRemainsCount += batches * recipe.RemainsCount;
            }
            else
            {
                expectedRemainsValue = baseRemainsValue;
            }
            int desiredRemainsValue = message.SlotCounts[remainsSlot] > 0
                ? NormalizeCrossbowValue(message.SlotValues[remainsSlot]) : 0;
            if (message.SlotCounts[remainsSlot] != expectedRemainsCount ||
                desiredRemainsValue != (expectedRemainsCount > 0 ? expectedRemainsValue : 0))
                return false;

            craftedResultCount = message.DropCount;
            return true;
        }

        private static bool HaveSameCombinedItems(IInventory container,
            int[] baseContainerValues, int[] baseContainerCounts,
            int[] basePlayerValues, int[] basePlayerCounts,
            int[] desiredContainerValues, int[] desiredContainerCounts,
            int[] desiredPlayerValues, int[] desiredPlayerCounts)
        {
            var balance = new Dictionary<int, long>();
            AddInventoryCounts(balance, baseContainerValues, baseContainerCounts, 1, container);
            AddInventoryCounts(balance, basePlayerValues, basePlayerCounts, 1);
            AddInventoryCounts(balance, desiredContainerValues, desiredContainerCounts, -1, container);
            AddInventoryCounts(balance, desiredPlayerValues, desiredPlayerCounts, -1);
            return balance.Values.All(value => value == 0L);
        }

        // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
        private static bool IsContainerDropRequestValid(IInventory container,
            ContainerSyncMessage message)
        {
            return message?.IsDrop == true && message.DropValue != 0 &&
                message.DropCount > 0 && IsFinite(message.DropPosition) &&
                IsFinite(message.DropVelocity) &&
                ArraysEqual(message.PlayerBaseSlotValues, message.PlayerSlotValues) &&
                ArraysEqual(message.PlayerBaseSlotCounts, message.PlayerSlotCounts) &&
                HasContainerDropDelta(container,
                    message.BaseSlotValues, message.BaseSlotCounts,
                    message.SlotValues, message.SlotCounts,
                    NormalizeCrossbowValue(message.DropValue), message.DropCount);
        }

        private static bool HasContainerDropDelta(IInventory container,
            int[] baseValues, int[] baseCounts, int[] desiredValues, int[] desiredCounts,
            int dropValue, int dropCount)
        {
            if (container == null || dropValue == 0 || dropCount <= 0 ||
                baseValues == null || baseCounts == null || desiredValues == null ||
                desiredCounts == null || baseValues.Length != container.SlotsCount ||
                baseCounts.Length != container.SlotsCount ||
                desiredValues.Length != container.SlotsCount ||
                desiredCounts.Length != container.SlotsCount)
                return false;
            var balance = new Dictionary<int, long>();
            AddInventoryCounts(balance, baseValues, baseCounts, 1, container);
            AddInventoryCounts(balance, desiredValues, desiredCounts, -1, container);
            int normalizedDropValue = NormalizeCrossbowValue(dropValue);
            return balance.TryGetValue(normalizedDropValue, out long removed) &&
                removed == dropCount && balance.All(item =>
                    item.Key == normalizedDropValue || item.Value == 0L);
        }

        private static void AddInventoryCounts(Dictionary<int, long> balance,
            int[] values, int[] counts, int direction, IInventory inventory = null)
        {
            int length = Math.Min(values?.Length ?? 0, counts?.Length ?? 0);
            for (int i = 0; i < length; i++)
            {
                if (IsCraftingTableResultSlot(inventory, i))
                    continue;
                if (values[i] == 0 || counts[i] <= 0) continue;
                balance.TryGetValue(values[i], out long current);
                balance[values[i]] = current + (long)direction * counts[i];
            }
        }

        private static ContainerNetworkState AdvanceContainerState(ContainerNetworkState state,
            int[] values, int[] counts)
        {
            int revision = state?.Revision ?? 0;
            return new ContainerNetworkState
            {
                Revision = revision == int.MaxValue ? 1 : revision + 1,
                Values = (int[])values.Clone(),
                Counts = (int[])counts.Clone()
            };
        }

        private static void SendContainerState(NetworkContainerReference container,
            ContainerNetworkState state)
        {
            var message = new ContainerSyncMessage
            {
                Coordinates = container.Coordinates,
                ComponentType = container.ComponentType,
                OwnerClientId = container.OwnerClientId,
                Revision = state.Revision,
                IsRequest = false,
                SlotValues = state.Values,
                SlotCounts = state.Counts
            };
            NetworkMessageSender.SendScheduledMessage(
                container.OwnerClientId >= 0 ? container.OwnerClientId : -1, message,
                sequenced: false, latest: false);
        }

        private static void SendContainerRequest(ContainerSyncMessage message)
        {
            NetworkMessageSender.SendScheduledMessage(0, message,
                sequenced: false, latest: false, batchable: false);
        }

        private static void SendContainerResponse(ContainerSyncMessage message,
            int targetClientId)
        {
            NetworkMessageSender.SendScheduledMessage(targetClientId, message,
                sequenced: false, latest: false, batchable: false);
        }

        private static ContainerSyncMessage CreateContainerResponse(
            ContainerSyncMessage request, ContainerNetworkState state,
            int requesterClientId, int playerRevision, IInventory playerInventory)
        {
            return new ContainerSyncMessage
            {
                Coordinates = request.Coordinates,
                ComponentType = request.ComponentType,
                OwnerClientId = request.OwnerClientId,
                Revision = state.Revision,
                RequestId = request.RequestId,
                RequesterClientId = requesterClientId,
                PlayerRevision = playerRevision,
                IsRequest = false,
                IsBaselineRequest = request.IsBaselineRequest,
                SlotValues = (int[])state.Values.Clone(),
                SlotCounts = (int[])state.Counts.Clone(),
                PlayerSlotValues = playerInventory == null
                    ? Array.Empty<int>() : CaptureInventoryValues(playerInventory),
                PlayerSlotCounts = playerInventory == null
                    ? Array.Empty<int>() : CaptureInventoryCounts(playerInventory)
            };
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
        // Source: Survivalcraft/Game/SubsystemEditableItemBehavior.cs:SubsystemEditableItemBehavior<T>
        internal bool CanSubmitEditableDataEdit(ComponentPlayer player)
        {
            if (client?.IsConnected != true || GameManager.Project == null || player == null)
                return false;
            return !m_networkPlayerData.Values.Any(item =>
                ReferenceEquals(item?.ComponentPlayer, player));
        }

        internal bool ShouldSuppressRemoteEditableDataEdit(ComponentPlayer player)
        {
            return IsHost && client?.IsConnected == true && player != null &&
                m_networkPlayerData.Values.Any(item =>
                    ReferenceEquals(item?.ComponentPlayer, player));
        }

        internal bool TrySubmitEditableItemData(EditableDataKind kind, IInventory inventory,
            int slotIndex, ComponentPlayer player, int expectedValue, string payload)
        {
            if (!CanSubmitEditableDataEdit(player) || inventory == null ||
                slotIndex < 0 || slotIndex >= inventory.SlotsCount)
                return false;
            var message = new EditableDataRequestMessage
            {
                RequestId = NextEditableDataRequestId(),
                Kind = kind,
                Scope = EditableDataScope.InventoryItem,
                SlotIndex = slotIndex,
                ExpectedValue = expectedValue,
                Payload = payload ?? string.Empty
            };
            if (IsHost)
                HandleEditableDataRequest(message, 0);
            else
                NetworkMessageSender.SendEditableDataRequest(message);
            return true;
        }

        internal bool TrySubmitEditableBlockData(EditableDataKind kind, Point3 point,
            ComponentPlayer player, int expectedValue, string payload)
        {
            if (!CanSubmitEditableDataEdit(player)) return false;
            var message = new EditableDataRequestMessage
            {
                RequestId = NextEditableDataRequestId(),
                Kind = kind,
                Scope = EditableDataScope.Block,
                SlotIndex = -1,
                ExpectedValue = expectedValue,
                Coordinates = point,
                Payload = payload ?? string.Empty
            };
            if (IsHost)
                HandleEditableDataRequest(message, 0);
            else
                NetworkMessageSender.SendEditableDataRequest(message);
            return true;
        }

        private int NextEditableDataRequestId()
        {
            m_localEditableDataRequestId = m_localEditableDataRequestId == int.MaxValue
                ? 1
                : m_localEditableDataRequestId + 1;
            return m_localEditableDataRequestId;
        }

        private void HandleEditableDataRequest(EditableDataRequestMessage message,
            int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId < 0 ||
                message.RequestId <= 0 || !IsValidEditableDataKind(message.Kind) ||
                !IsValidEditableDataScope(message.Scope) ||
                (message.Payload?.Length ?? 0) > 512)
                return;
            if (m_lastEditableDataRequestIds.TryGetValue(sourceClientId, out int lastRequestId) &&
                message.RequestId <= lastRequestId)
                return;
            m_lastEditableDataRequestIds[sourceClientId] = message.RequestId;

            ComponentPlayer player = GetEditableDataPlayer(sourceClientId);
            if (player == null) return;
            if (message.Scope == EditableDataScope.InventoryItem)
                ApplyEditableItemRequest(message, sourceClientId, player);
            else
                ApplyEditableBlockRequest(message, player);
        }

        private void ApplyEditableItemRequest(EditableDataRequestMessage message,
            int sourceClientId, ComponentPlayer player)
        {
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory == null || message.SlotIndex < 0 ||
                message.SlotIndex >= inventory.SlotsCount ||
                inventory.GetSlotCount(message.SlotIndex) <= 0)
                return;
            int currentValue = inventory.GetSlotValue(message.SlotIndex);
            if (currentValue != message.ExpectedValue ||
                Terrain.ExtractContents(currentValue) != GetEditableDataContents(message.Kind))
                return;

            int dataId;
            if (IsDirectBlockDataKind(message.Kind))
            {
                if (!TryGetAuthoritativeEditableData(message.Kind,
                    Terrain.ExtractData(currentValue), message.Payload, out dataId))
                    return;
            }
            else
            {
                dataId = StoreAuthoritativeEditableItemData(message.Kind, message.Payload);
            }
            if (dataId < 0) return;
            int authoritativeValue = Terrain.ReplaceData(currentValue, dataId);
            inventory.RemoveSlotItems(message.SlotIndex, int.MaxValue);
            inventory.AddSlotItems(message.SlotIndex, authoritativeValue, 1);
            PublishEditableDataState(message.Kind, EditableDataScope.InventoryItem,
                dataId, default, message.Payload);

            m_lastEquipmentSnapshots.Remove(sourceClientId);
            m_lastSentInventoryValues.Remove(sourceClientId);
            m_lastSentInventoryCounts.Remove(sourceClientId);
            m_forceHostInventorySync = true;
        }

        private void ApplyEditableBlockRequest(EditableDataRequestMessage message,
            ComponentPlayer player)
        {
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null || player.ComponentBody == null ||
                Vector3.DistanceSquared(player.ComponentBody.Position,
                    new Vector3(message.Coordinates) + new Vector3(0.5f)) > 64f)
                return;
            int currentValue = terrain.Terrain.GetCellValue(
                message.Coordinates.X, message.Coordinates.Y, message.Coordinates.Z);
            if (Terrain.ReplaceLight(currentValue, 0) !=
                    Terrain.ReplaceLight(message.ExpectedValue, 0) ||
                Terrain.ExtractContents(currentValue) != GetEditableDataContents(message.Kind))
                return;

            EditableDataKind kind = message.Kind;
            Point3 point = message.Coordinates;
            string payload = message.Payload ?? string.Empty;
            int executeHostCircuitStep = m_circuitSynchronizer?.ScheduleHostAction(point, () =>
            {
                ApplyEditableBlockData(kind, point, payload);
            }) ?? 0;
            if (executeHostCircuitStep <= 0 &&
                !ApplyEditableBlockData(kind, point, payload)) return;
            PublishEditableDataState(message.Kind, EditableDataScope.Block, 0,
                message.Coordinates, payload, executeHostCircuitStep);
        }

        private ComponentPlayer GetEditableDataPlayer(int sourceClientId)
        {
            if (sourceClientId > 0)
                return m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData remote)
                    ? remote?.ComponentPlayer
                    : null;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            return players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
        }

        private int StoreAuthoritativeEditableItemData(EditableDataKind kind, string payload)
        {
            Project project = GameManager.Project;
            if (project == null) return -1;
            if (kind == EditableDataKind.MemoryBank)
                return project.FindSubsystem<SuSubsystemMemoryBankBlockBehavior>(false)?
                    .StoreNetworkItemData(payload) ?? -1;
            if (kind == EditableDataKind.TruthTable)
                return project.FindSubsystem<SuSubsystemTruthTableCircuitBlockBehavior>(false)?
                    .StoreNetworkItemData(payload) ?? -1;
            return -1;
        }

        private bool ApplyEditableBlockData(EditableDataKind kind, Point3 point,
            string payload)
        {
            Project project = GameManager.Project;
            if (project == null) return false;
            if (IsDirectBlockDataKind(kind))
            {
                SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
                if (terrain == null) return false;
                int value = terrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
                if (Terrain.ExtractContents(value) != GetEditableDataContents(kind) ||
                    !TryGetAuthoritativeEditableData(kind, Terrain.ExtractData(value),
                        payload, out int data))
                    return false;
                int newValue = Terrain.ReplaceData(value, data);
                if (newValue != value)
                    terrain.ChangeCell(point.X, point.Y, point.Z, newValue);
                int face = kind == EditableDataKind.AdjustableDelay
                    ? ((AdjustableDelayGateBlock)BlocksManager.Blocks[
                        AdjustableDelayGateBlock.Index]).GetFace(newValue)
                    : 0;
                SubsystemElectricity electricity =
                    project.FindSubsystem<SubsystemElectricity>(false);
                ElectricElement element = electricity?.GetElectricElement(
                    point.X, point.Y, point.Z, face);
                if (element != null)
                    electricity.QueueElectricElementForSimulation(element,
                        electricity.CircuitStep + 1);
                return true;
            }
            if (kind == EditableDataKind.MemoryBank)
            {
                SuSubsystemMemoryBankBlockBehavior behavior =
                    project.FindSubsystem<SuSubsystemMemoryBankBlockBehavior>(false);
                if (behavior == null) return false;
                behavior.ApplyNetworkBlockData(point, payload);
                return true;
            }
            if (kind == EditableDataKind.TruthTable)
            {
                SuSubsystemTruthTableCircuitBlockBehavior behavior =
                    project.FindSubsystem<SuSubsystemTruthTableCircuitBlockBehavior>(false);
                if (behavior == null) return false;
                behavior.ApplyNetworkBlockData(point, payload);
                return true;
            }
            return false;
        }

        private void HandleEditableDataState(EditableDataStateMessage message,
            int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null || message.Revision <= 0 ||
                !IsValidEditableDataKind(message.Kind) ||
                !IsValidEditableDataScope(message.Scope) ||
                (message.Payload?.Length ?? 0) > 512)
                return;
            string key = GetEditableDataKey(message.Kind, message.Scope,
                message.DataId, message.Coordinates);
            if (m_lastEditableDataRevisions.TryGetValue(key, out int revision) &&
                message.Revision <= revision)
                return;

            if (message.Scope == EditableDataScope.Block &&
                message.ExecuteHostCircuitStep > 0)
            {
                EditableDataKind kind = message.Kind;
                Point3 point = message.Coordinates;
                string payload = message.Payload ?? string.Empty;
                if (m_circuitSynchronizer?.ScheduleRemoteAction(point,
                    message.ExecuteHostCircuitStep, () =>
                    {
                        ApplyEditableBlockData(kind, point, payload);
                    }) == true)
                {
                    m_lastEditableDataRevisions[key] = message.Revision;
                    m_lastEditableDataPayloads[key] = payload;
                    return;
                }
            }

            bool applied;
            if (message.Scope == EditableDataScope.InventoryItem)
                applied = ApplyEditableItemData(message.Kind, message.DataId, message.Payload);
            else
                applied = ApplyEditableBlockData(message.Kind, message.Coordinates,
                    message.Payload);
            if (!applied) return;
            m_lastEditableDataRevisions[key] = message.Revision;
            m_lastEditableDataPayloads[key] = message.Payload ?? string.Empty;
        }

        private bool ApplyEditableItemData(EditableDataKind kind, int dataId, string payload)
        {
            if (GameManager.Project == null) return false;
            if (IsDirectBlockDataKind(kind)) return dataId >= 0;
            if (dataId < 0 || dataId >= 1000) return false;
            if (kind == EditableDataKind.MemoryBank)
            {
                SuSubsystemMemoryBankBlockBehavior behavior =
                    GameManager.Project.FindSubsystem<SuSubsystemMemoryBankBlockBehavior>(false);
                if (behavior == null) return false;
                behavior.ApplyNetworkItemData(dataId, payload);
                return true;
            }
            if (kind == EditableDataKind.TruthTable)
            {
                SuSubsystemTruthTableCircuitBlockBehavior behavior =
                    GameManager.Project.FindSubsystem<SuSubsystemTruthTableCircuitBlockBehavior>(false);
                if (behavior == null) return false;
                behavior.ApplyNetworkItemData(dataId, payload);
                return true;
            }
            return false;
        }

        // Source: Survivalcraft/Game/MemoryBankElectricElement.cs:MemoryBankElectricElement.Simulate
        // Memory banks can change without an edit dialog, so publish registry deltas once per second.
        private void SynchronizeEditableData()
        {
            if (!IsHost || client?.IsConnected != true || GameManager.Project == null) return;
            SuSubsystemMemoryBankBlockBehavior memory =
                GameManager.Project.FindSubsystem<SuSubsystemMemoryBankBlockBehavior>(false);
            if (memory != null)
            {
                PublishEditableItemData(EditableDataKind.MemoryBank,
                    memory.CaptureNetworkItemData());
                PublishEditableBlockData(EditableDataKind.MemoryBank,
                    memory.CaptureNetworkBlockData());
            }
            SuSubsystemTruthTableCircuitBlockBehavior truth =
                GameManager.Project.FindSubsystem<SuSubsystemTruthTableCircuitBlockBehavior>(false);
            if (truth != null)
            {
                PublishEditableItemData(EditableDataKind.TruthTable,
                    truth.CaptureNetworkItemData());
                PublishEditableBlockData(EditableDataKind.TruthTable,
                    truth.CaptureNetworkBlockData());
            }
        }

        private void PublishEditableItemData(EditableDataKind kind,
            Dictionary<int, string> items)
        {
            foreach (KeyValuePair<int, string> item in items)
                PublishEditableDataState(kind, EditableDataScope.InventoryItem,
                    item.Key, default, item.Value);
        }

        private void PublishEditableBlockData(EditableDataKind kind,
            Dictionary<Point3, string> blocks)
        {
            foreach (KeyValuePair<Point3, string> item in blocks)
                PublishEditableDataState(kind, EditableDataScope.Block,
                    0, item.Key, item.Value);
        }

        private void PublishEditableDataState(EditableDataKind kind,
            EditableDataScope scope, int dataId, Point3 point, string payload,
            int executeHostCircuitStep = 0)
        {
            string key = GetEditableDataKey(kind, scope, dataId, point);
            string safePayload = payload ?? string.Empty;
            if (m_lastEditableDataPayloads.TryGetValue(key, out string previous) &&
                previous == safePayload)
                return;
            if (scope == EditableDataScope.Block && executeHostCircuitStep <= 0)
            {
                EditableDataKind scheduledKind = kind;
                Point3 scheduledPoint = point;
                string scheduledPayload = safePayload;
                executeHostCircuitStep = m_circuitSynchronizer?.ScheduleHostAction(
                    scheduledPoint, () =>
                    {
                        ApplyEditableBlockData(scheduledKind, scheduledPoint,
                            scheduledPayload);
                    }) ?? 0;
            }
            m_lastEditableDataPayloads[key] = safePayload;
            m_editableDataRevision = m_editableDataRevision == int.MaxValue
                ? 1
                : m_editableDataRevision + 1;
            NetworkMessageSender.SendEditableDataState(-1, new EditableDataStateMessage
            {
                Revision = m_editableDataRevision,
                ExecuteHostCircuitStep = executeHostCircuitStep,
                Kind = kind,
                Scope = scope,
                DataId = dataId,
                Coordinates = point,
                Payload = safePayload
            });
        }

        private static string GetEditableDataKey(EditableDataKind kind,
            EditableDataScope scope, int dataId, Point3 point)
        {
            return scope == EditableDataScope.InventoryItem
                ? ((int)kind).ToString(CultureInfo.InvariantCulture) + ":I:" +
                    dataId.ToString(CultureInfo.InvariantCulture)
                : ((int)kind).ToString(CultureInfo.InvariantCulture) + ":B:" +
                    point.X.ToString(CultureInfo.InvariantCulture) + "," +
                    point.Y.ToString(CultureInfo.InvariantCulture) + "," +
                    point.Z.ToString(CultureInfo.InvariantCulture);
        }

        private static int GetEditableDataContents(EditableDataKind kind)
        {
            return kind == EditableDataKind.MemoryBank ? 186 :
                kind == EditableDataKind.TruthTable ? 188 :
                kind == EditableDataKind.AdjustableDelay ? AdjustableDelayGateBlock.Index :
                kind == EditableDataKind.SwitchVoltage ? SwitchBlock.Index :
                kind == EditableDataKind.ButtonVoltage ? ButtonBlock.Index :
                kind == EditableDataKind.Piston ? PistonBlock.Index : 0;
        }

        private static bool IsValidEditableDataKind(EditableDataKind kind) =>
            kind == EditableDataKind.MemoryBank || kind == EditableDataKind.TruthTable ||
            IsDirectBlockDataKind(kind);

        private static bool IsDirectBlockDataKind(EditableDataKind kind) =>
            kind == EditableDataKind.AdjustableDelay ||
                kind == EditableDataKind.SwitchVoltage ||
                kind == EditableDataKind.ButtonVoltage ||
                kind == EditableDataKind.Piston ||
                kind == EditableDataKind.Dispenser;

        // Source: Survivalcraft/Game/SubsystemAdjustableDelayGateBlockBehavior.cs:
        // SubsystemAdjustableDelayGateBlockBehavior.OnEditBlock
        // Source: Survivalcraft/Game/SubsystemSwitchBlockBehavior.cs:
        // SubsystemSwitchBlockBehavior.OnEditBlock
        // Source: Survivalcraft/Game/SubsystemButtonBlockBehavior.cs:
        // SubsystemButtonBlockBehavior.OnEditBlock
        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:
        // SubsystemPistonBlockBehavior.OnEditBlock
        private static bool TryGetAuthoritativeEditableData(EditableDataKind kind,
            int currentData, string payload, out int data)
        {
            data = currentData;
            if (!int.TryParse(payload, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int requestedData))
                return false;
            if (kind == EditableDataKind.AdjustableDelay)
            {
                data = AdjustableDelayGateBlock.SetDelay(currentData,
                    AdjustableDelayGateBlock.GetDelay(requestedData));
                return true;
            }
            if (kind == EditableDataKind.SwitchVoltage)
            {
                data = SwitchBlock.SetVoltageLevel(currentData,
                    SwitchBlock.GetVoltageLevel(requestedData));
                return true;
            }
            if (kind == EditableDataKind.ButtonVoltage)
            {
                data = ButtonBlock.SetVoltageLevel(currentData,
                    ButtonBlock.GetVoltageLevel(requestedData));
                return true;
            }
            if (kind == EditableDataKind.Piston)
            {
                data = PistonBlock.SetMode(currentData, PistonBlock.GetMode(requestedData));
                data = PistonBlock.SetMaxExtension(data,
                    PistonBlock.GetMaxExtension(requestedData));
                data = PistonBlock.SetPullCount(data,
                    PistonBlock.GetPullCount(requestedData));
                data = PistonBlock.SetSpeed(data, PistonBlock.GetSpeed(requestedData));
                return true;
            }
            if (kind == EditableDataKind.Dispenser)
            {
                data = DispenserBlock.SetMode(currentData,
                    DispenserBlock.GetMode(requestedData));
                data = DispenserBlock.SetAcceptsDrops(data,
                    DispenserBlock.GetAcceptsDrops(requestedData));
                return true;
            }
            return false;
        }

        private static bool IsValidEditableDataScope(EditableDataScope scope) =>
            scope == EditableDataScope.InventoryItem || scope == EditableDataScope.Block;

    }
}

using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        private const PlayerCapabilityFlags SupportedPlayerCapabilities =
            PlayerCapabilityFlags.CreativeFly |
            PlayerCapabilityFlags.WorldControl |
            PlayerCapabilityFlags.CreativeInventory;

        private readonly Dictionary<int, PlayerCapabilityFlags> m_playerCapabilities =
            new Dictionary<int, PlayerCapabilityFlags>();
        private readonly Dictionary<int, int> m_playerCapabilitySequences =
            new Dictionary<int, int>();
        private readonly Dictionary<int, IInventory> m_capabilityNormalInventories =
            new Dictionary<int, IInventory>();

        internal bool HasPlayerCapability(int clientId, PlayerCapabilityFlags capability)
        {
            return capability != PlayerCapabilityFlags.None &&
                (GetPlayerCapabilities(clientId) & capability) == capability;
        }

        internal PlayerCapabilityFlags GetPlayerCapabilities(int clientId)
        {
            return m_playerCapabilities.TryGetValue(clientId,
                out PlayerCapabilityFlags capabilities) ? capabilities : PlayerCapabilityFlags.None;
        }

        internal bool HasLocalPlayerCapability(PlayerCapabilityFlags capability)
        {
            return HasPlayerCapability(IsHost ? 0 : client?.ClientID ?? -1, capability);
        }

        // Source: Mod/ScMultiplayer/DataModification/DataModificationCoordinator.cs:
        // DataModificationCoordinator.TryApply
        // Requests enter the same host-thread executor as the other built-in operations. The
        // operation never accepts a client-supplied target outside the existing authority checks.
        private DataModificationApplyResult ApplyPlayerCapabilityRequest(
            DataModificationApplyContext context, PlayerDataModificationRequest request)
        {
            if (context == null || request == null || context.SourceClientId < 0)
                return DataModificationApplyResult.Reject("The capability request is invalid.");
            PlayerCapabilityFlags requested = request.Capabilities & SupportedPlayerCapabilities;
            if (requested == PlayerCapabilityFlags.None ||
                (request.Capabilities & ~SupportedPlayerCapabilities) != PlayerCapabilityFlags.None)
                return DataModificationApplyResult.Reject(
                    "The requested player capabilities are not supported.");

            int targetClientId = request.TargetClientId >= 0
                ? request.TargetClientId : context.SourceClientId;
            if (targetClientId <= 0 && context.SourceClientId > 0)
                return DataModificationApplyResult.Reject(
                    "A remote client can change only its own capabilities.");
            if (context.SourceClientId > 0 && targetClientId != context.SourceClientId)
                return DataModificationApplyResult.Reject(
                    "A remote client can change only its own capabilities.");
            if (!TryResolveAuthorityPlayer(targetClientId, out _, out ComponentPlayer player) ||
                player == null)
                return DataModificationApplyResult.Reject(
                    "The capability target is not online on the authoritative host.");

            PlayerCapabilityFlags current = GetPlayerCapabilities(targetClientId);
            bool revoke = string.Equals(context.Operation,
                DataModificationOperationNames.RevokeCapabilities,
                StringComparison.Ordinal);
            PlayerCapabilityFlags updated = revoke
                ? current & ~requested
                : current | requested;
            SetPlayerCapabilities(targetClientId, updated);
            string action = revoke ? "revoked" : "granted";
            return DataModificationApplyResult.Success(
                "Host " + action + " capabilities: " + DescribePlayerCapabilities(requested));
        }

        private void SetPlayerCapabilities(int clientId, PlayerCapabilityFlags capabilities)
        {
            capabilities &= SupportedPlayerCapabilities;
            if (capabilities == PlayerCapabilityFlags.None)
                m_playerCapabilities.Remove(clientId);
            else
                m_playerCapabilities[clientId] = capabilities;

            int sequence = m_playerCapabilitySequences.TryGetValue(clientId,
                out int previous) ? NextPlayerCapabilitySequence(previous) : 1;
            m_playerCapabilitySequences[clientId] = sequence;
            ApplyPlayerCapabilitiesToRuntime(clientId, capabilities);
            if (IsHost && client?.IsConnected == true && clientId > 0)
            {
                NetworkMessageSender.SendPlayerCapabilityMessage(clientId,
                    new PlayerCapabilityMessage
                    {
                        TargetClientId = clientId,
                        Sequence = sequence,
                        Capabilities = capabilities
                    });
            }
        }

        private static int NextPlayerCapabilitySequence(int previous) =>
            previous == int.MaxValue ? 1 : previous + 1;

        // Source: Mod/ScMultiplayer/Message/PlayerCapabilityMessage.cs:
        // PlayerCapabilityMessage
        // Only the host can publish this directed state. A client accepts a newer sequence for
        // itself and then applies the same local presentation adapter used by the host avatar.
        internal void HandlePlayerCapabilityMessage(PlayerCapabilityMessage message,
            int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null || client == null ||
                message.TargetClientId != client.ClientID ||
                !IsSupportedPlayerCapabilities(message.Capabilities))
                return;
            if (m_playerCapabilitySequences.TryGetValue(message.TargetClientId,
                    out int previous) && !IsNewPlayerCapabilitySequence(message.Sequence, previous))
                return;
            m_playerCapabilitySequences[message.TargetClientId] = message.Sequence;
            if (message.Capabilities == PlayerCapabilityFlags.None)
                m_playerCapabilities.Remove(message.TargetClientId);
            else
                m_playerCapabilities[message.TargetClientId] = message.Capabilities;
            ApplyPlayerCapabilitiesToRuntime(message.TargetClientId, message.Capabilities);
        }

        private static bool IsNewPlayerCapabilitySequence(int sequence, int previous)
        {
            if (sequence == previous) return false;
            long distance = sequence - (long)previous;
            if (distance < 0) distance += int.MaxValue;
            return distance > 0 && distance <= int.MaxValue / 2;
        }

        private static bool IsSupportedPlayerCapabilities(PlayerCapabilityFlags capabilities) =>
            (capabilities & ~SupportedPlayerCapabilities) == PlayerCapabilityFlags.None;

        private void ApplyPlayerCapabilitiesToRuntime(int clientId,
            PlayerCapabilityFlags capabilities)
        {
            ComponentPlayer player = ResolveCapabilityPlayer(clientId);
            if (player == null) return;
            ApplyPlayerCreativeInventory(clientId, player,
                capabilities.HasFlag(PlayerCapabilityFlags.CreativeInventory));
            ApplyPlayerCreativeFly(player,
                capabilities.HasFlag(PlayerCapabilityFlags.CreativeFly));
        }

        private ComponentPlayer ResolveCapabilityPlayer(int clientId)
        {
            if (clientId > 0)
                return m_networkPlayerData.TryGetValue(clientId, out PlayerData data)
                    ? data?.ComponentPlayer : null;
            return GetLocalPlayer();
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Load
        // Source: Survivalcraft/Game/ComponentCreativeInventory.cs:ComponentCreativeInventory
        // The native player template contains both inventories. Selecting the creative one per
        // entity preserves the global GameMode and leaves all other players untouched.
        private void ApplyPlayerCreativeInventory(int clientId, ComponentPlayer player,
            bool enabled)
        {
            ComponentMiner miner = player?.ComponentMiner;
            if (miner == null) return;
            SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo?.WorldSettings.GameMode == GameMode.Creative)
                return;
            IInventory current = miner.Inventory;
            if (enabled)
            {
                if (current is ComponentCreativeInventory) return;
                ComponentCreativeInventory creative = player.Entity.FindComponent<
                    ComponentCreativeInventory>();
                if (creative == null) return;
                if (!m_capabilityNormalInventories.ContainsKey(clientId))
                    m_capabilityNormalInventories[clientId] = current;
                ModManager.ModParentField.ModifyParentField(miner,
                    "<Inventory>k__BackingField", creative, typeof(ComponentMiner));
                return;
            }
            if (current is ComponentCreativeInventory &&
                m_capabilityNormalInventories.TryGetValue(clientId,
                    out IInventory normal) && normal != null)
            {
                ModManager.ModParentField.ModifyParentField(miner,
                    "<Inventory>k__BackingField", normal, typeof(ComponentMiner));
                m_capabilityNormalInventories.Remove(clientId);
            }
        }

        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
        // The original update clears creative flight in non-creative worlds. Reapply only the
        // host-approved flag after the project update; no global GameMode mutation is performed.
        private static void ApplyPlayerCreativeFly(ComponentPlayer player, bool enabled)
        {
            ComponentLocomotion locomotion = player?.ComponentLocomotion;
            ComponentBody body = player?.ComponentBody;
            if (locomotion == null || body == null) return;
            bool active = enabled && player.ComponentHealth?.Health > 0f &&
                player.ComponentRider?.Mount == null;
            locomotion.IsCreativeFlyEnabled = active;
            body.IsGravityEnabled = !active;
            body.IsGroundDragEnabled = !active;
            if (!active) return;
            body.TargetCrouchFactor = 0f;
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        // Frame.Update runs after the native project update and is the stable point for restoring
        // the per-player flight flag and the original CreativeInventory binding.
        internal void MaintainPlayerCapabilities()
        {
            // Source: Mod/ScMultiplayer/Core/ScMultiplayerRuntimeState.cs:ScMultiplayer.IsInRoom
            // Offline local worlds must use the native creative-flight path. Capability maintenance
            // is authoritative only after a confirmed room/game session exists.
            if (!IsInRoom || GameManager.Project == null) return;
            if (IsHost)
            {
                foreach (KeyValuePair<int, PlayerCapabilityFlags> item in
                    m_playerCapabilities.ToArray())
                    ApplyPlayerCapabilitiesToRuntime(item.Key, item.Value);
                return;
            }
            int localClientId = client?.ClientID ?? -1;
            if (localClientId >= 0)
                ApplyPlayerCapabilitiesToRuntime(localClientId,
                    GetPlayerCapabilities(localClientId));
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
        // Native creative controls are hidden by the global GameMode check. Expose the same
        // controls only for an authorized local player; SuComponentInput routes their actions
        // through the host-authoritative request path.
        internal void ApplyPlayerCapabilityUi()
        {
            ComponentPlayer player = GetLocalPlayer();
            ComponentGui gui = player?.ComponentGui;
            if (gui == null) return;
            PlayerCapabilityFlags capabilities = GetPlayerCapabilities(IsHost ? 0 : client?.ClientID ?? -1);
            SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            bool globalCreative = gameInfo?.WorldSettings.GameMode == GameMode.Creative;
            bool creativeFly = globalCreative || capabilities.HasFlag(PlayerCapabilityFlags.CreativeFly);
            bool worldControl = globalCreative || capabilities.HasFlag(PlayerCapabilityFlags.WorldControl);
            SetCapabilityButtonVisible(gui, "m_creativeFlyButtonWidget", creativeFly);
            SetCapabilityButtonVisible(gui, "m_timeOfDayButtonWidget", worldControl &&
                gameInfo?.WorldSettings.TimeOfDayMode == TimeOfDayMode.Changing);
            SetCapabilityButtonVisible(gui, "m_lightningButtonWidget", worldControl);
            SetCapabilityButtonVisible(gui, "m_precipitationButtonWidget", worldControl &&
                gameInfo?.WorldSettings.AreWeatherEffectsEnabled == true);
            SetCapabilityButtonVisible(gui, "m_fogButtonWidget", worldControl &&
                gameInfo?.WorldSettings.AreWeatherEffectsEnabled == true);
        }

        private static void SetCapabilityButtonVisible(ComponentGui gui, string fieldName,
            bool visible)
        {
            ButtonWidget button = ModManager.ModParentField.GetParentField<ButtonWidget>(
                gui, fieldName, typeof(ComponentGui));
            if (button != null) button.IsVisible = visible;
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
        // Consume the creative-fly edge locally because the native branch is guarded by global
        // GameMode. The actual remote state is still accepted only from the host snapshot.
        internal void ApplyPlayerCapabilityInput(ComponentPlayer player, ref PlayerInput input)
        {
            PlayerCapabilityFlags capabilities = GetPlayerCapabilities(IsHost ? 0 : client?.ClientID ?? -1);
            bool creativeFly = capabilities.HasFlag(PlayerCapabilityFlags.CreativeFly);
            bool globalCreative = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false)?
                .WorldSettings.GameMode == GameMode.Creative;
            if (globalCreative) return;
            if (!creativeFly)
            {
                input.ToggleCreativeFly = false;
                ApplyPlayerCreativeFly(player, false);
                return;
            }
            ButtonWidget button = ModManager.ModParentField.GetParentField<ButtonWidget>(
                player?.ComponentGui, "m_creativeFlyButtonWidget", typeof(ComponentGui));
            bool buttonClicked = button?.IsClicked == true;
            if (buttonClicked)
                input.ToggleCreativeFly = true;
            if (input.ToggleCreativeFly && player?.ComponentRider?.Mount == null)
            {
                bool enabled = !player.ComponentLocomotion.IsCreativeFlyEnabled;
                ApplyPlayerCreativeFly(player, enabled);
                input.ToggleCreativeFly = false;
                if (buttonClicked)
                    ConsumeCapabilityButtonClick(button);
            }
        }

        private static void ConsumeCapabilityButtonClick(ButtonWidget button)
        {
            if (button == null) return;
            ClickableWidget clickable = button is BitmapButtonWidget
                ? ModManager.ModParentField.GetParentField<ClickableWidget>(
                    button, "m_clickableWidget", typeof(BitmapButtonWidget))
                : button is BevelledButtonWidget
                    ? ModManager.ModParentField.GetParentField<ClickableWidget>(
                        button, "m_clickableWidget", typeof(BevelledButtonWidget))
                    : null;
            if (clickable != null)
                ModManager.ModParentField.ModifyParentField(clickable,
                    "<IsClicked>k__BackingField", false, typeof(ClickableWidget));
        }

        internal void ResetPlayerCapabilityState()
        {
            foreach (int clientId in m_capabilityNormalInventories.Keys.ToArray())
            {
                ComponentPlayer player = ResolveCapabilityPlayer(clientId);
                ApplyPlayerCreativeInventory(clientId, player, false);
            }
            m_capabilityNormalInventories.Clear();
            m_playerCapabilities.Clear();
            m_playerCapabilitySequences.Clear();
        }

        internal void ForgetPlayerCapability(int clientId)
        {
            ComponentPlayer player = ResolveCapabilityPlayer(clientId);
            ApplyPlayerCreativeInventory(clientId, player, false);
            m_capabilityNormalInventories.Remove(clientId);
            m_playerCapabilities.Remove(clientId);
            m_playerCapabilitySequences.Remove(clientId);
        }

        private static string DescribePlayerCapabilities(PlayerCapabilityFlags capabilities)
        {
            List<string> names = new List<string>();
            if (capabilities.HasFlag(PlayerCapabilityFlags.CreativeFly)) names.Add("fly");
            if (capabilities.HasFlag(PlayerCapabilityFlags.WorldControl)) names.Add("world-control");
            if (capabilities.HasFlag(PlayerCapabilityFlags.CreativeInventory)) names.Add("creative-inventory");
            return string.Join(",", names);
        }
    }
}

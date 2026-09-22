using Engine;
using Game;
using ScMultiplayer.Core;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        private const int MaximumGrantedItemCount = 4096;
        private const float MaximumRestoredLevel = 1000000f;
        private const float MaximumRequestedCoordinate = 1000000f;
        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.UpdateTemperature
        // 引擎把体温夹在 0..24（12 = 舒适），所以 GM 也只能在这个区间里设值。
        private const float MaximumRequestedTemperature = 24f;
        private const float MaximumRequestedConditionDuration = 3600f;
        private int m_hostPlayerAuthoritySequence;

        // Source: Mod/ScMultiplayer/DataModification/DataModificationCoordinator.cs:
        // DataModificationCoordinator.TryApply
        // Built-in player operations reserve the ScMP.Player namespace. Clients submit only a
        // request; this host-thread executor owns every authoritative mutation.
        internal DataModificationApplyResult ApplyBuiltInDataModification(
            DataModificationApplyContext context)
        {
            if (context == null || !DataModificationOperationNames.IsBuiltIn(context.Operation))
                return DataModificationApplyResult.Reject(
                    "The built-in player operation is not supported.");

            try
            {
                return ApplyBuiltInPlayerModification(context);
            }
            catch (Exception ex)
            {
                Log.Error("[ScMP] Failed to apply built-in player DM operation " +
                    context.Operation + ": " + ex.Message);
                return DataModificationApplyResult.Reject(
                    "The authoritative player operation failed.");
            }
        }

        private DataModificationApplyResult ApplyBuiltInPlayerModification(
            DataModificationApplyContext context)
        {
            if (!IsHost || client?.IsConnected != true || GameManager.Project == null)
                return DataModificationApplyResult.Reject("An active authoritative host is required.");
            if (context.Channel != DataModificationChannel.Fast || context.ChunkCount != 1 ||
                context.ChunkIndex != 0 || !context.IsFinalChunk)
            {
                return DataModificationApplyResult.Reject(
                    "Built-in player operations require one fast request.");
            }
            if (!PlayerDataModificationCodec.TryDecode(context.Payload,
                out PlayerDataModificationRequest request, out string decodeError))
            {
                return DataModificationApplyResult.Reject(decodeError);
            }

            int targetClientId = request.TargetClientId >= 0
                ? request.TargetClientId
                : context.SourceClientId;
            if (context.SourceClientId < 0 || targetClientId < 0)
                return DataModificationApplyResult.Reject("The player target is invalid.");
            if (context.SourceClientId > 0 && targetClientId != context.SourceClientId)
            {
                // GM 工具（第三方 mod）需要能操作**所有玩家**的数据，因此跨玩家目标不再被无条件拒绝：
                // 授权由主机的 DM 策略决定（`default` 会弹窗并把目标与内容写进摘要，`allow`/受信身份自动同意），
                // 执行方始终是主机。这里只留一条审计日志。
                Log.Information("[ScMP] Cross-player DM request: operation=" + context.Operation +
                    " from client " + context.SourceClientId + " target client " + targetClientId);
            }
            if (string.Equals(context.Operation, DataModificationOperationNames.SealPlayer,
                StringComparison.Ordinal) && context.SourceClientId != 0)
            {
                return DataModificationApplyResult.Reject(
                    "Only a host-local request can seal a player.");
            }
            if (!TryResolveAuthorityPlayer(targetClientId, out PlayerData playerData,
                out ComponentPlayer player))
            {
                return DataModificationApplyResult.Reject(
                    "The target player is not online on the authoritative host.");
            }

            DataModificationApplyResult result = context.Operation switch
            {
                DataModificationOperationNames.SetRespawnAnchor =>
                    ApplyRespawnAnchorRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.ReturnToPlayer =>
                    ApplyReturnToPlayerRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.GrantInventory =>
                    ApplyGrantInventoryRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.RestoreLevel =>
                    ApplyRestoreLevelRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.HealPlayer =>
                    ApplyHealPlayerRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.SetVitals =>
                    ApplySetVitalsRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.SetCondition =>
                    ApplySetConditionRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.SafeRespawnRelocate =>
                    ApplySafeRespawnRelocateRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.SealPlayer =>
                    ApplySealPlayerRequest(targetClientId, playerData, player, request),
                DataModificationOperationNames.RequestCapabilities =>
                    ApplyPlayerCapabilityRequest(context, request),
                DataModificationOperationNames.RevokeCapabilities =>
                    ApplyPlayerCapabilityRequest(context, request),
                _ => DataModificationApplyResult.Reject("The player operation is not supported.")
            };
            if (result.Applied)
            {
                PublishServerAudit("dm.player", context.SourceClientId,
                    "operation=" + context.Operation + " target=" +
                    targetClientId.ToString(CultureInfo.InvariantCulture) + " mod=" +
                    NormalizePlayerAdministrationAuditValue(context.ModId));
            }
            return result;
        }

        // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:
        // ScMultiplayer.CreateNetworkPlayer
        private bool TryResolveAuthorityPlayer(int targetClientId, out PlayerData playerData,
            out ComponentPlayer player)
        {
            playerData = null;
            player = null;
            if (targetClientId > 0)
            {
                if (!m_networkPlayerData.TryGetValue(targetClientId, out playerData))
                    return false;
                player = playerData?.ComponentPlayer;
                return player?.ComponentBody != null;
            }

            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            player = players?.ComponentPlayers.FirstOrDefault(item =>
                item?.PlayerData != null && !m_networkPlayerData.Values.Contains(item.PlayerData));
            playerData = player?.PlayerData;
            return player?.ComponentBody != null && playerData != null;
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.FindNoIntroSpawnPosition
        private DataModificationApplyResult ApplyRespawnAnchorRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            Vector3 desired = GetRequestedPositionOrFallback(request, player.ComponentBody.Position);
            if (!TryFindAuthoritativeSafePosition(playerData, desired, respawn: true,
                out Vector3 anchor, out string error))
                return DataModificationApplyResult.Reject(error);

            playerData.SpawnPosition = anchor;
            PersistPlayerAdministrationTarget(targetClientId, playerData);
            SendPlayerAuthorityState(targetClientId, PlayerAuthorityAction.RespawnAnchor,
                playerData, player.ComponentBody);
            return DataModificationApplyResult.Success("Respawn anchor updated by the host.");
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.FindNoIntroSpawnPosition
        private DataModificationApplyResult ApplyReturnToPlayerRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            int destinationClientId = request.DestinationClientId;
            if (destinationClientId < 0 || destinationClientId == targetClientId ||
                !TryResolveAuthorityPlayer(destinationClientId, out _,
                    out ComponentPlayer destination))
            {
                return DataModificationApplyResult.Reject(
                    "The destination player is not a different online player.");
            }
            if (player.ComponentRider?.Mount != null || player.ComponentBody.ParentBody != null)
            {
                return DataModificationApplyResult.Reject(
                    "The target player must dismount before teleporting.");
            }

            Vector3 offset = NormalizeReturnOffset(request, targetClientId, destinationClientId);
            Vector3 desired = destination.ComponentBody.Position + offset;
            if (!TryFindAuthoritativeSafePosition(playerData, desired, respawn: false,
                out Vector3 position, out string error))
                return DataModificationApplyResult.Reject(error);

            ApplyHostAuthoritativeTeleport(targetClientId, playerData, player, position);
            return DataModificationApplyResult.Success(
                "Player returned near the destination by the host.");
        }

        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:
        // ComponentInventoryBase.AcquireItems
        private DataModificationApplyResult ApplyGrantInventoryRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            IInventory inventory = player.ComponentMiner?.Inventory;
            int contents = Terrain.ExtractContents(request.ItemValue);
            if (inventory == null || request.ItemValue == 0 || contents <= 0 ||
                contents >= BlocksManager.Blocks.Length || BlocksManager.Blocks[contents] == null ||
                request.ItemCount <= 0 || request.ItemCount > MaximumGrantedItemCount)
            {
                return DataModificationApplyResult.Reject(
                    "The requested inventory compensation is invalid.");
            }
            if (!TryPlanInventoryGrant(inventory, request.ItemValue, request.ItemCount,
                out List<InventoryGrantEntry> plan))
            {
                return DataModificationApplyResult.Reject(
                    "The authoritative inventory has insufficient capacity.");
            }
            foreach (InventoryGrantEntry entry in plan)
                inventory.AddSlotItems(entry.SlotIndex, request.ItemValue, entry.Count);

            MarkHostInventoryAuthoritative(targetClientId);
            SynchronizeHostEquipment(targetClientId, player);
            PersistPlayerAdministrationTarget(targetClientId, playerData);
            return DataModificationApplyResult.Success(
                "Inventory compensation granted by the host.");
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Level
        private DataModificationApplyResult ApplyRestoreLevelRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            float value = request.SetAbsoluteLevel
                ? request.Level
                : playerData.Level + request.Amount;
            if (!float.IsFinite(value) || value < 1f || value > MaximumRestoredLevel ||
                (!request.SetAbsoluteLevel &&
                    (!float.IsFinite(request.Amount) || request.Amount <= 0f)))
            {
                return DataModificationApplyResult.Reject(
                    "The requested level compensation is invalid.");
            }

            playerData.Level = value;
            SendAuthoritativePlayerHealth(targetClientId, player, force: true);
            PersistPlayerAdministrationTarget(targetClientId, playerData);
            return DataModificationApplyResult.Success("Level restored by the host.");
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Heal
        private DataModificationApplyResult ApplyHealPlayerRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            if (player.ComponentHealth == null || player.ComponentHealth.Health <= 0f ||
                !float.IsFinite(request.Amount) || request.Amount <= 0f || request.Amount > 1f)
            {
                return DataModificationApplyResult.Reject(
                    "Healing requires a living player and an amount in (0, 1].");
            }

            player.ComponentHealth.Heal(request.Amount);
            SendAuthoritativePlayerHealth(targetClientId, player, force: true);
            PersistPlayerAdministrationTarget(targetClientId, playerData);
            return DataModificationApplyResult.Success("Health restored by the host.");
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Update
        // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:
        // ScMultiplayer.ApplyAuthoritativePlayerStats
        // 「改生命体征 / 饱食度 / 体温」：`ComponentVitalStats.Food` 等属性是 `private set`，只能按名写
        // 底层字段（GodMode 同法）；写完必须走权威生命值下发，否则客户端表现不会刷新。
        private DataModificationApplyResult ApplySetVitalsRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            ComponentVitalStats vital = player.ComponentVitalStats;
            ComponentHealth health = player.ComponentHealth;
            if (vital == null || health == null || health.Health <= 0f)
                return DataModificationApplyResult.Reject("Vitals require a living target player.");
            VitalsField fields = request.Vitals &
                (VitalsField.Food | VitalsField.Stamina | VitalsField.Sleep |
                    VitalsField.Temperature | VitalsField.Wetness);
            if (fields == VitalsField.None)
                return DataModificationApplyResult.Reject("No vital field was requested.");
            if (!TryResolveRequestedVital(fields, VitalsField.Food, request.VitalsFood, vital.Food,
                    0f, 1f, "food", out float food, out string error) ||
                !TryResolveRequestedVital(fields, VitalsField.Stamina, request.VitalsStamina,
                    vital.Stamina, 0f, 1f, "stamina", out float stamina, out error) ||
                !TryResolveRequestedVital(fields, VitalsField.Sleep, request.VitalsSleep,
                    vital.Sleep, 0f, 1f, "sleep", out float sleep, out error) ||
                !TryResolveRequestedVital(fields, VitalsField.Temperature, request.VitalsTemperature,
                    vital.Temperature, 0f, MaximumRequestedTemperature, "temperature",
                    out float temperature, out error) ||
                !TryResolveRequestedVital(fields, VitalsField.Wetness, request.VitalsWetness,
                    vital.Wetness, 0f, 1f, "wetness", out float wetness, out error))
            {
                return DataModificationApplyResult.Reject(error);
            }

            ApplyAuthoritativePlayerStats(player, health.Health, health.Air, food, stamina, sleep,
                temperature, wetness, playerData.Level);
            SendAuthoritativePlayerHealth(targetClientId, player, force: true);
            PersistPlayerAdministrationTarget(targetClientId, playerData);
            var applied = new List<string>();
            if ((fields & VitalsField.Food) != 0)
                applied.Add("food=" + FormatVitalValue(food));
            if ((fields & VitalsField.Stamina) != 0)
                applied.Add("stamina=" + FormatVitalValue(stamina));
            if ((fields & VitalsField.Sleep) != 0)
                applied.Add("sleep=" + FormatVitalValue(sleep));
            if ((fields & VitalsField.Temperature) != 0)
                applied.Add("temperature=" + FormatVitalValue(temperature));
            if ((fields & VitalsField.Wetness) != 0)
                applied.Add("wetness=" + FormatVitalValue(wetness));
            return DataModificationApplyResult.Success(
                "vitals updated by the host: " + string.Join(", ", applied));
        }

        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.StartFlu
        // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.StartSickness
        // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:
        // ScMultiplayer.ResetNetworkPlayerVitals
        // 「添加 / 解除异常状态」：异常状态只能由主机施加（客户端的 SuComponentFlu / SuComponentSickness
        // 只做表现）。字段名与清零口径与 ResetNetworkPlayerVitals 一致，避免两处语义漂移。
        private DataModificationApplyResult ApplySetConditionRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            if (player.ComponentHealth == null || player.ComponentHealth.Health <= 0f)
                return DataModificationApplyResult.Reject(
                    "Status effects require a living target player.");
            float duration = request.ConditionDuration;
            if (!float.IsFinite(duration) || duration < 0f ||
                duration > MaximumRequestedConditionDuration)
            {
                return DataModificationApplyResult.Reject(
                    "The requested condition duration is out of range.");
            }
            ConditionKind kinds = request.Condition & ConditionKind.All;
            bool apply = request.ConditionMode == ConditionAction.Apply;
            if (kinds == ConditionKind.None)
            {
                // 不指定病种 = 全部解除（GM 菜单里的"解除全部异常状态"）。
                kinds = ConditionKind.All;
                apply = false;
            }

            var applied = new List<string>();
            if ((kinds & ConditionKind.Flu) != 0)
            {
                ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
                if (flu == null)
                    return DataModificationApplyResult.Reject("The target has no flu component.");
                if (apply)
                {
                    if (duration > 0f)
                        ModManager.ModParentField.ModifyParentField(flu, "m_fluDuration", duration,
                            typeof(ComponentFlu));
                    else
                        flu.StartFlu();
                }
                else
                {
                    ClearFluState(flu);
                }
                applied.Add("flu=" + (apply ? "on" : "cleared"));
            }
            if ((kinds & ConditionKind.Sickness) != 0)
            {
                ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
                if (sickness == null)
                    return DataModificationApplyResult.Reject(
                        "The target has no sickness component.");
                if (apply)
                {
                    if (duration > 0f)
                        ModManager.ModParentField.ModifyParentField(sickness, "m_sicknessDuration",
                            duration, typeof(ComponentSickness));
                    else
                        sickness.StartSickness();
                }
                else
                {
                    ClearSicknessState(sickness);
                }
                applied.Add("sickness=" + (apply ? "on" : "cleared"));
            }
            SendAuthoritativePlayerHealth(targetClientId, player, force: true);
            PersistPlayerAdministrationTarget(targetClientId, playerData);
            return DataModificationApplyResult.Success(
                "conditions updated by the host: " + string.Join(", ", applied));
        }

        private static void ClearFluState(ComponentFlu flu)
        {
            string[] fields =
            {
                "m_fluOnset", "m_fluDuration", "m_coughDuration", "m_sneezeDuration",
                "m_blackoutDuration", "m_blackoutFactor"
            };
            foreach (string field in fields)
                ModManager.ModParentField.ModifyParentField(flu, field, 0f, typeof(ComponentFlu));
        }

        private static void ClearSicknessState(ComponentSickness sickness)
        {
            // 注意：**不要**用泛型 `GetParentField<PukeParticleSystem>` —— 它内部只做 `value is T`，
            // 字段为 null（玩家当前没在呕吐，这是常态）时会抛 "Member ... is not of type ..."。
            // 用返回 object 的非泛型重载 + `is` 模式才是 null 安全的。
            if (ModManager.ModParentField.GetParentField(sickness,
                "m_pukeParticleSystem", typeof(ComponentSickness)) is PukeParticleSystem puke)
            {
                puke.IsStopped = true;
            }
            ModManager.ModParentField.ModifyParentField(sickness, "m_pukeParticleSystem", null,
                typeof(ComponentSickness));
            ModManager.ModParentField.ModifyParentField(sickness, "m_sicknessDuration", 0f,
                typeof(ComponentSickness));
            ModManager.ModParentField.ModifyParentField(sickness, "m_greenoutDuration", 0f,
                typeof(ComponentSickness));
            ModManager.ModParentField.ModifyParentField(sickness, "m_greenoutFactor", 0f,
                typeof(ComponentSickness));
        }

        private static bool TryResolveRequestedVital(VitalsField requested, VitalsField field,
            float requestedValue, float currentValue, float minimum, float maximum, string name,
            out float value, out string error)
        {
            value = currentValue;
            error = null;
            if ((requested & field) == 0)
                return true;
            if (!float.IsFinite(requestedValue) || requestedValue < minimum ||
                requestedValue > maximum)
            {
                error = "The requested " + name + " must be between " +
                    FormatVitalValue(minimum) + " and " + FormatVitalValue(maximum) + ".";
                return false;
            }
            value = requestedValue;
            return true;
        }

        private static string FormatVitalValue(float value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.FindNoIntroSpawnPosition
        private DataModificationApplyResult ApplySafeRespawnRelocateRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            if (player.ComponentRider?.Mount != null || player.ComponentBody.ParentBody != null)
            {
                return DataModificationApplyResult.Reject(
                    "The target player must dismount before relocating.");
            }
            Vector3 anchor = playerData.SpawnPosition != Vector3.Zero
                ? playerData.SpawnPosition
                : player.ComponentBody.Position;
            Vector3 offset = NormalizeRequestedOffset(request, 8f);
            if (!TryFindAuthoritativeSafePosition(playerData, anchor + offset, respawn: true,
                out Vector3 position, out string error))
                return DataModificationApplyResult.Reject(error);

            playerData.SpawnPosition = position;
            ApplyHostAuthoritativeTeleport(targetClientId, playerData, player, position);
            return DataModificationApplyResult.Success(
                "Respawn anchor and player position safely relocated by the host.");
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        private DataModificationApplyResult ApplySealPlayerRequest(int targetClientId,
            PlayerData playerData, ComponentPlayer player, PlayerDataModificationRequest request)
        {
            SubsystemTerrain terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(false);
            ComponentBody body = player.ComponentBody;
            int radius = MathUtils.Clamp(request.Radius, 1, 3);
            int height = MathUtils.Clamp(request.Height, 2, 6);
            int centerX = Terrain.ToCell(body.Position.X);
            int baseY = Terrain.ToCell(body.Position.Y);
            int centerZ = Terrain.ToCell(body.Position.Z);
            if (terrain == null || baseY < 2 || baseY + height < baseY ||
                baseY + height >= 255 ||
                !AreSealChunksReady(terrain, centerX, centerZ, radius))
            {
                return DataModificationApplyResult.Reject(
                    "The target terrain is not ready for an authoritative seal.");
            }

            int bedrockValue = Terrain.MakeBlockValue(BedrockBlock.Index);
            for (int x = centerX - radius; x <= centerX + radius; x++)
            {
                for (int z = centerZ - radius; z <= centerZ + radius; z++)
                {
                    for (int y = baseY - 1; y <= baseY + height; y++)
                    {
                        bool shell = x == centerX - radius || x == centerX + radius ||
                            z == centerZ - radius || z == centerZ + radius ||
                            y == baseY - 1 || y == baseY + height;
                        if (shell)
                            terrain.ChangeCell(x, y, z, bedrockValue);
                    }
                }
            }
            terrain.TerrainUpdater.RequestSynchronousUpdate();
            (terrain as SuSubsystemTerrain)?.FlushHostModifiedCellClosureForNetworkAction();
            return DataModificationApplyResult.Success("Player sealed by the host.");
        }

        private void ApplyHostAuthoritativeTeleport(int targetClientId, PlayerData playerData,
            ComponentPlayer player, Vector3 position)
        {
            ComponentBody body = player.ComponentBody;
            body.Position = position;
            body.Velocity = Vector3.Zero;
            if (targetClientId > 0)
            {
                if (!m_networkPlayerInputs.TryGetValue(targetClientId,
                    out NetworkPlayerInputState inputState))
                {
                    inputState = new NetworkPlayerInputState();
                    m_networkPlayerInputs[targetClientId] = inputState;
                }
                ResetInputStateForAuthoritativeTeleport(inputState, body);
            }
            PersistPlayerAdministrationTarget(targetClientId, playerData);
            SendPlayerAuthorityState(targetClientId, PlayerAuthorityAction.Teleport,
                playerData, body);
        }

        private static void ResetInputStateForAuthoritativeTeleport(
            NetworkPlayerInputState state, ComponentBody body)
        {
            state.Input = default;
            state.HeldInput = default;
            state.BodyPosition = body.Position;
            state.BodyVelocity = body.Velocity;
            state.BodyRotation = body.Rotation;
            state.ConsumedSequence = state.Sequence;
            state.LastReceivedTime = Time.RealTime;
            state.HeldAim = null;
            state.ActiveAimSequence = -1;
            state.ActiveAimSlotIndex = -1;
            state.ActiveAimItemValue = 0;
            state.ActiveAimItemCount = 0;
            state.NextHitExecutionTime = 0.0;
            state.AimEvents.Clear();
            state.QueuedAimCompletions.Clear();
            state.InteractEvents.Clear();
            state.HitEvents.Clear();
            state.DropEvents.Clear();
            state.JumpEvents.Clear();
        }

        private void SendPlayerAuthorityState(int targetClientId, PlayerAuthorityAction action,
            PlayerData playerData, ComponentBody body)
        {
            if (targetClientId <= 0 || body == null)
                return;
            m_hostPlayerAuthoritySequence = PlayerActionSequencePolicy.Next(
                m_hostPlayerAuthoritySequence);
            NetworkMessageSender.SendPlayerAuthorityMessage(targetClientId,
                new PlayerAuthorityMessage
                {
                    Action = action,
                    TargetClientId = targetClientId,
                    Sequence = m_hostPlayerAuthoritySequence,
                    Position = body.Position,
                    Velocity = body.Velocity,
                    SpawnPosition = playerData?.SpawnPosition ?? Vector3.Zero
                });
        }

        // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:
        // ScMultiplayer.CapturePlayerRecord
        private void PersistPlayerAdministrationTarget(int targetClientId, PlayerData playerData)
        {
            if (targetClientId > 0 && playerData != null &&
                m_clientRecordKeys.TryGetValue(targetClientId, out string recordKey))
            {
                m_playerRecords[recordKey] = CapturePlayerRecord(playerData);
                m_playerRecordsDirty = true;
                SavePlayerRecords();
                return;
            }
            if (targetClientId == 0 && GameManager.Project != null)
                GameManager.SaveProject(waitForCompletion: false, showErrorDialog: false);
        }

        private static bool TryPlanInventoryGrant(IInventory inventory, int value, int count,
            out List<InventoryGrantEntry> plan)
        {
            plan = new List<InventoryGrantEntry>();
            int remaining = count;
            for (int pass = 0; pass < 2 && remaining > 0; pass++)
            {
                for (int slot = 0; slot < inventory.SlotsCount && remaining > 0; slot++)
                {
                    int slotCount = inventory.GetSlotCount(slot);
                    bool matching = slotCount > 0 && inventory.GetSlotValue(slot) == value;
                    bool empty = slotCount == 0;
                    if ((pass == 0 && !matching) || (pass == 1 && !empty))
                        continue;
                    int capacity = inventory.GetSlotCapacity(slot, value) - slotCount;
                    int added = Math.Min(Math.Max(capacity, 0), remaining);
                    if (added <= 0)
                        continue;
                    plan.Add(new InventoryGrantEntry(slot, added));
                    remaining -= added;
                }
            }
            return remaining == 0;
        }

        private bool TryFindAuthoritativeSafePosition(PlayerData playerData, Vector3 desired,
            bool respawn, out Vector3 position, out string error)
        {
            position = Vector3.Zero;
            error = null;
            if (!IsFinitePlayerAdministrationVector(desired))
            {
                error = "The requested position is not finite.";
                return false;
            }
            desired = new Vector3(
                MathUtils.Clamp(desired.X, -MaximumRequestedCoordinate,
                    MaximumRequestedCoordinate),
                MathUtils.Clamp(desired.Y, 2f, 253f),
                MathUtils.Clamp(desired.Z, -MaximumRequestedCoordinate,
                    MaximumRequestedCoordinate));
            SubsystemTerrain terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null || !AreSafeSearchChunksReady(terrain, desired))
            {
                error = "The host terrain around the requested position is not loaded.";
                return false;
            }

            position = ModManager.ModParentMethod.InvokeParentMethod<Vector3>(
                playerData, "FindNoIntroSpawnPosition",
                new[] { typeof(Vector3), typeof(bool) }, desired, respawn);
            if (!IsSafeStandingPosition(terrain, position))
            {
                position = Vector3.Zero;
                error = "The host could not find a safe standing position.";
                return false;
            }
            return true;
        }

        private static bool AreSafeSearchChunksReady(SubsystemTerrain terrain, Vector3 desired)
        {
            int minX = Terrain.ToCell(desired.X) - 8;
            int maxX = Terrain.ToCell(desired.X) + 8;
            int minZ = Terrain.ToCell(desired.Z) - 8;
            int maxZ = Terrain.ToCell(desired.Z) + 8;
            for (int chunkX = minX >> 4; chunkX <= maxX >> 4; chunkX++)
            {
                for (int chunkZ = minZ >> 4; chunkZ <= maxZ >> 4; chunkZ++)
                {
                    if (!HostTerrainAuthority.IsReadyForAuthoritativeMutation(
                        terrain, chunkX << 4, chunkZ << 4))
                        return false;
                }
            }
            return true;
        }

        private static bool AreSealChunksReady(SubsystemTerrain terrain, int centerX,
            int centerZ, int radius)
        {
            for (int chunkX = (centerX - radius) >> 4;
                chunkX <= (centerX + radius) >> 4; chunkX++)
            {
                for (int chunkZ = (centerZ - radius) >> 4;
                    chunkZ <= (centerZ + radius) >> 4; chunkZ++)
                {
                    if (!HostTerrainAuthority.IsReadyForAuthoritativeMutation(
                        terrain, chunkX << 4, chunkZ << 4))
                        return false;
                }
            }
            return true;
        }

        private static bool IsSafeStandingPosition(SubsystemTerrain terrain, Vector3 position)
        {
            int x = Terrain.ToCell(position.X);
            int y = Terrain.ToCell(position.Y);
            int z = Terrain.ToCell(position.Z);
            if (y < 1 || y >= 254)
                return false;
            Block below = BlocksManager.Blocks[terrain.Terrain.GetCellContents(x, y - 1, z)];
            Block feet = BlocksManager.Blocks[terrain.Terrain.GetCellContents(x, y, z)];
            Block head = BlocksManager.Blocks[terrain.Terrain.GetCellContents(x, y + 1, z)];
            return below.IsCollidable && !below.IsTransparent &&
                !feet.IsCollidable && !head.IsCollidable;
        }

        private static Vector3 GetRequestedPositionOrFallback(
            PlayerDataModificationRequest request, Vector3 fallback)
        {
            Vector3 requested = new Vector3(request.X, request.Y, request.Z);
            return requested == Vector3.Zero ? fallback : requested;
        }

        private static Vector3 NormalizeRequestedOffset(PlayerDataModificationRequest request,
            float maximumLength)
        {
            Vector3 offset = new Vector3(request.OffsetX, request.OffsetY, request.OffsetZ);
            if (!IsFinitePlayerAdministrationVector(offset))
                return Vector3.Zero;
            float length = offset.Length();
            return length > maximumLength && length > 0.0001f
                ? offset * (maximumLength / length)
                : offset;
        }

        private static Vector3 NormalizeReturnOffset(PlayerDataModificationRequest request,
            int targetClientId, int destinationClientId)
        {
            Vector3 offset = NormalizeRequestedOffset(request, 8f);
            if (offset.LengthSquared() >= 1f)
                return offset;
            float angle = (targetClientId * 0.7548777f + destinationClientId * 0.5698403f) *
                2f * MathUtils.PI;
            return new Vector3(3f * MathUtils.Cos(angle), 0f,
                3f * MathUtils.Sin(angle));
        }

        private static bool IsFinitePlayerAdministrationVector(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private static string NormalizePlayerAdministrationAuditValue(string value)
        {
            string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Length <= 64 ? normalized : normalized.Substring(0, 64);
        }

        private readonly struct InventoryGrantEntry
        {
            public readonly int SlotIndex;
            public readonly int Count;

            public InventoryGrantEntry(int slotIndex, int count)
            {
                SlotIndex = slotIndex;
                Count = count;
            }
        }
    }
}

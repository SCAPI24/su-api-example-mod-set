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
        // 消息处理
        // ====================================================================
        public void HandleGamePlayerPositionMessage(GamePlayerPositionMessage msg, int clientID)
        {
            if (msg == null) return;
            PlayerReadOnlyStateSnapshot readOnlyState = new PlayerReadOnlyStateSnapshot(
                msg.Position, msg.Rotation, msg.Velocity, msg.LookAngles,
                msg.IsCrouching, msg.IsFlying, msg.IsRiding, msg.IsGrounded);
            if (!readOnlyState.IsFinite) return;
            // Source: msg.PlayerIndex = 发送方的 ClientID
            // 写入 RemotePlayers 而非本地 ComponentPlayers, 避免覆盖本地玩家
            // Source: Comms/GameStepData.Inputs
            // The transport ClientID is authoritative. The ID serialized in a packet can belong
            // to an earlier connection after a client leaves and rejoins.
            if (IsHost || clientID != 0) return;
            int remoteClientId = msg.PlayerIndex;
            if (remoteClientId == client.ClientID)
            {
                // Source: Mod/ScMultiplayer/Message/GamePlayerPositionMessage.cs:ServerTick
                // The local player now consumes host snapshots for post-hit velocity correction,
                // so reject an older latest-state packet before it can pull that correction back.
                if (msg.ServerTick <= m_lastAuthoritativeLocalPositionTick) return;
                m_lastAuthoritativeLocalPositionTick = msg.ServerTick;
                ApplyAuthoritativeLocalPlayerState(msg, readOnlyState);
                return;
            }
            if (RemotePlayers.TryGetValue(remoteClientId,
                out NetworkPlayerState previousState) &&
                msg.ServerTick < previousState.ServerTick)
                return;
            // A delayed position packet can arrive after Client_GameStep reports a leave. Do not
            // recreate a presentation-only avatar until the current connection registers it again.
            if (!m_networkPlayerData.ContainsKey(remoteClientId))
                return;

            NetworkPlayerState state;
            if (!RemotePlayers.TryGetValue(remoteClientId, out state))
            {
                state = new NetworkPlayerState { ClientID = remoteClientId };
                RemotePlayers[remoteClientId] = state;
            }

            float previousPokingPhase = state.PokingPhase;
            bool pokeStarted = msg.PokingPhase > 0f &&
                (previousPokingPhase <= 0f ||
                    msg.PokingPhase + 0.05f < previousPokingPhase);
            readOnlyState.ApplyTo(state);
            state.ServerTick = msg.ServerTick;
            state.WalkOrder = msg.WalkOrder;
            state.JumpOrder = msg.JumpOrder;
            state.PokingPhase = msg.PokingPhase;
            state.AttackOrder = msg.AttackOrder;
            state.RowLeftOrder = msg.RowLeftOrder;
            state.RowRightOrder = msg.RowRightOrder;
            state.ActiveSlotIndex = msg.ActiveSlotIndex;
            state.HandItemValue = msg.HandItemValue;
            state.HandItemCount = msg.HandItemCount;
            state.MountEntityId = msg.MountEntityId;
            state.ItemOffset = msg.ItemOffset;
            state.ItemRotation = msg.ItemRotation;
            state.AimHandAngle = msg.AimHandAngle;
            state.LastUpdateTime = Time.RealTime;

            if (m_networkPlayerData.TryGetValue(remoteClientId, out PlayerData playerData) &&
                playerData.ComponentPlayer != null)
            {
                // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Poke
                // Position snapshots are a fallback when the reliable poke edge is delayed.
                if (pokeStarted && Time.RealTime - state.LastPokeEventTime > 0.1)
                {
                    playerData.ComponentPlayer.ComponentMiner?.Poke(forceRestart: true);
                    state.LastPokeEventTime = Time.RealTime;
                }
                ComponentMiner remoteMiner = playerData.ComponentPlayer.ComponentMiner;
                if (remoteMiner != null)
                {
                    // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Update
                    // Human arm animation reads PokingPhase directly. Applying the authoritative
                    // phase also recovers a short poke whose reliable edge arrived late or was lost.
                    if (msg.PokingPhase > 0f ||
                        Time.RealTime - state.LastPokeEventTime > 0.3)
                    {
                        ModManager.ModParentField.ModifyParentField(
                            remoteMiner, "<PokingPhase>k__BackingField", msg.PokingPhase,
                            typeof(ComponentMiner));
                    }
                }
                ComponentBody body = playerData.ComponentPlayer.ComponentBody;
                body.TargetCrouchFactor = msg.IsCrouching ? 1f : 0f;
                // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.LookAngles
                // Body rotation carries yaw; pitch is stored separately in m_lookAngles.
                ComponentLocomotion locomotion = playerData.ComponentPlayer.ComponentLocomotion;
                if (locomotion != null)
                {
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "m_lookAngles", msg.LookAngles, typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "<LastWalkOrder>k__BackingField", msg.WalkOrder, typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "<LastJumpOrder>k__BackingField", msg.JumpOrder, typeof(ComponentLocomotion));
                    locomotion.IsCreativeFlyEnabled = msg.IsFlying;
                }
                ComponentCreatureModel remoteModel = playerData.ComponentPlayer.ComponentCreatureModel;
                if (remoteModel != null)
                {
                    remoteModel.AttackOrder = msg.AttackOrder;
                    remoteModel.RowLeftOrder = msg.RowLeftOrder;
                    remoteModel.RowRightOrder = msg.RowRightOrder;
                    remoteModel.InHandItemOffsetOrder = msg.ItemOffset;
                    remoteModel.InHandItemRotationOrder = msg.ItemRotation;
                    remoteModel.AimHandAngleOrder = msg.AimHandAngle;
                }
                IInventory inventory = playerData.ComponentPlayer.ComponentMiner?.Inventory;
                if (inventory != null && msg.SlotValues != null)
                {
                    if (msg.ActiveSlotIndex >= 0 && msg.ActiveSlotIndex < inventory.SlotsCount)
                        inventory.ActiveSlotIndex = msg.ActiveSlotIndex;
                    ApplyInventory(inventory, msg.SlotValues, msg.SlotCounts);
                }
                // Source: ScMultiplayer.HandleMountStateMessage
                // Position snapshots update presentation only. Mount parentage is changed solely
                // by the ordered host MountStateMessage.
                TryApplyReceivedMountState(msg.PlayerIndex);
            }
        }

        private void ApplyAuthoritativeLocalPlayerState(GamePlayerPositionMessage msg,
            PlayerReadOnlyStateSnapshot readOnlyState)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer == null) return;

            // Source: ScMultiplayer.HandleMountStateMessage
            // The ordered action result, not this latest position snapshot, owns local mount state.
            TryApplyReceivedMountState(client?.ClientID ?? 0);

            float delaySample = MathUtils.Clamp(
                (client.Step - msg.ServerTick) * ServerTickDuration, 0f, 0.5f);
            m_smoothedNetworkDelay = m_smoothedNetworkDelay <= 0f
                ? delaySample
                : MathUtils.Lerp(m_smoothedNetworkDelay, delaySample, 0.1f);
            // Client movement is predicted and never rewound. The host-side split-screen avatar
            // follows this trajectory with a bounded catch-up velocity instead.
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
            // After a host-authoritative hit, use the following host snapshots to close only the
            // horizontal trajectory error. Never write Position, Rotation or LookAngles here, so
            // the owning player's view cannot be snapped or rotated by network reconciliation.
            ComponentBody localBody = localPlayer.ComponentBody;
            double now = Time.RealTime;
            if (localBody != null && now < m_localKnockbackPositionCorrectionUntil &&
                msg.ServerTick >= m_localKnockbackCorrectionStartTick)
            {
                // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
                // Host position is authoritative for the knockback trajectory. Correct velocity
                // toward that sampled position without ever writing Position, Rotation or view.
                float predictionTime = MathUtils.Min(
                    MathUtils.Min(delaySample, m_smoothedNetworkDelay), 0.15f);
                Vector3 hostPosition = readOnlyState.Position +
                    readOnlyState.Velocity * predictionTime;
                Vector3 positionError = hostPosition - localBody.Position;
                positionError.Y = 0f;
                float errorLength = positionError.Length();
                if (errorLength > 0.02f)
                {
                    Vector3 correctionVelocity = positionError / 0.25f;
                    float correctionSpeed = correctionVelocity.Length();
                    if (correctionSpeed > 2.5f)
                        correctionVelocity *= 2.5f / correctionSpeed;
                    Vector3 localHorizontalVelocity = new Vector3(
                        localBody.Velocity.X, 0f, localBody.Velocity.Z);
                    // Small delayed errors may slow an overshooting client, but do not make it
                    // visibly reverse. A material divergence still converges to the host.
                    if (errorLength < 0.25f &&
                        Vector3.Dot(correctionVelocity, localHorizontalVelocity) < 0f)
                    {
                        correctionVelocity = Vector3.Zero;
                    }
                    Vector3 desiredVelocity = readOnlyState.Velocity + correctionVelocity;
                    localBody.Velocity = new Vector3(
                        MathUtils.Lerp(localBody.Velocity.X, desiredVelocity.X, 0.35f),
                        localBody.Velocity.Y,
                        MathUtils.Lerp(localBody.Velocity.Z, desiredVelocity.Z, 0.35f));
                    m_localInputBodyVelocity = localBody.Velocity;
                }
            }
            else if (now >= m_localKnockbackPositionCorrectionUntil)
            {
                m_localKnockbackPositionCorrectionUntil = 0.0;
            }

            IInventory inventory = localPlayer.ComponentMiner?.Inventory;
            if (inventory == null) return;
            // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
            // The owning client selects its hotbar slot. The host records that selection for its
            // replica and other peers, but never writes a delayed slot selection back to the owner.
            if (msg.SlotValues == null || msg.SlotValues.Length == 0) return;
            // Source: ScMultiplayer.cs:HandleAuthoritativePickableAcquire
            // A position snapshot captured earlier in the same host tick can arrive after Acquire.
            // Do not let that equal-tick stale inventory erase the newly collected item.
            if (m_hasAuthoritativeLocalInventory &&
                msg.ServerTick <= m_lastAuthoritativeLocalInventoryTick)
                return;
            int slotsCount = Math.Min(inventory.SlotsCount,
                Math.Min(msg.SlotValues.Length, msg.SlotCounts?.Length ?? 0));
            bool localInventoryChanged = m_hasAuthoritativeLocalInventory &&
                !InventoryMatches(inventory, m_authoritativeLocalSlotValues,
                    m_authoritativeLocalSlotCounts);
            bool hostAcknowledgedLocalInventory = localInventoryChanged &&
                InventoryMatches(inventory, msg.SlotValues, msg.SlotCounts, slotsCount);
            if (localInventoryChanged && !hostAcknowledgedLocalInventory)
                return;
            ApplyInventory(inventory, msg.SlotValues, msg.SlotCounts);
            m_authoritativeLocalSlotValues = msg.SlotValues.Take(slotsCount).ToArray();
            m_authoritativeLocalSlotCounts = msg.SlotCounts.Take(slotsCount).ToArray();
            m_lastAuthoritativeLocalInventoryTick = msg.ServerTick;
            m_hasAuthoritativeLocalInventory = true;
        }

        private static bool InventoryMatches(IInventory inventory, int[] values, int[] counts,
            int slotsCount = -1)
        {
            if (inventory == null || values == null || counts == null) return false;
            int count = slotsCount >= 0
                ? Math.Min(slotsCount, Math.Min(values.Length, counts.Length))
                : Math.Min(inventory.SlotsCount, Math.Min(values.Length, counts.Length));
            if (count != inventory.SlotsCount && slotsCount < 0) return false;
            for (int i = 0; i < count; i++)
            {
                if (NormalizeCrossbowValue(inventory.GetSlotValue(i)) !=
                        NormalizeCrossbowValue(values[i]) ||
                    inventory.GetSlotCount(i) != counts[i])
                    return false;
            }
            return true;
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public bool TrySendAnimalAttackRequest(ComponentPlayer player, Ray3 hitRay)
        {
            // Source: Survivalcraft/Game/SubsystemSaddleBlockBehavior.cs:OnUse
            // A saddle is an interaction item. Do not convert its body ray into melee damage.
            if (IsSaddleActive(player) || IsHost || client?.IsConnected != true || player?.ComponentMiner == null ||
                GameManager.Project == null)
                return false;
            BodyRaycastResult? result = player.ComponentMiner.Raycast<BodyRaycastResult>(
                hitRay, RaycastMode.Interaction);
            if (!result.HasValue) return false;

            Entity targetEntity = result.Value.ComponentBody?.Entity;
            if (targetEntity == null) return false;
            ushort targetId = 0;
            foreach (KeyValuePair<ushort, Entity> item in m_remoteAnimals)
            {
                if (ReferenceEquals(item.Value, targetEntity))
                {
                    targetId = item.Key;
                    break;
                }
            }
            if (targetId == 0) return false;

            Vector3 hitPoint = result.Value.HitPoint();
            if (Vector3.DistanceSquared(hitPoint, player.ComponentCreatureModel.EyePosition) > 2.25f * 2.25f)
                return false;
            // This is still an animal hit even while the native attack cooldown is active. Keep
            // suppressing the generic body ray request, but do not flood the host with duplicates.
            if (Time.RealTime < m_nextLocalHitRequestTime) return true;
            m_nextLocalHitRequestTime = Time.RealTime + PlayerHitRequestInterval;
            m_localHitSequence = m_localHitSequence == int.MaxValue ? 1 : m_localHitSequence + 1;
            TrackLocalMeleePrediction(player, m_localHitSequence);
            Vector3 direction = hitRay.Direction.LengthSquared() > 0.0001f
                ? Vector3.Normalize(hitRay.Direction)
                : Vector3.UnitZ;
            var message = new AnimalInteractionMessage(
                targetId, m_localHitSequence, hitPoint, direction);
            // Source: ScMultiplayer.cs:HandleAnimalInteractionMessage
            NetworkMessageSender.SendRawMessage(0, message);
            return true;
        }

        private void HandleAnimalInteractionMessage(AnimalInteractionMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0 || GameManager.Project == null)
                return;
            if (!m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData))
                return;
            ComponentPlayer attacker = playerData?.ComponentPlayer;
            ComponentMiner miner = attacker?.ComponentMiner;
            // Source: Survivalcraft/Game/SubsystemSaddleBlockBehavior.cs:OnUse
            // Keep the host-side validation aligned with the client-side saddle guard.
            if (IsSaddleActive(attacker) || attacker == null || miner == null || attacker.ComponentHealth?.Health <= 0f)
                return;

            Entity targetEntity = m_hostAnimalIds
                .FirstOrDefault(item => item.Value == message.TargetAnimalId).Key;
            ComponentCreature target = targetEntity?.FindComponent<ComponentCreature>();
            ComponentBody targetBody = target?.ComponentBody;
            if (targetEntity?.IsAddedToProject != true || targetBody == null ||
                target.ComponentHealth?.Health <= 0f)
            {
                SendRejectedMeleeHitResult(sourceClientId, message.ClientTick,
                    message.HitPoint, message.HitDirection,
                    attacker.ComponentBody?.Velocity ?? Vector3.Zero);
                return;
            }

            Vector3 eyePosition = attacker.ComponentCreatureModel.EyePosition;
            Vector3 targetPoint = targetBody.BoundingBox.Center();
            Vector3 toTarget = targetPoint - eyePosition;
            if (toTarget.LengthSquared() > 4f * 4f)
            {
                SendRejectedMeleeHitResult(sourceClientId, message.ClientTick, targetPoint,
                    message.HitDirection, attacker.ComponentBody?.Velocity ?? Vector3.Zero);
                return;
            }
            Vector3 direction = message.HitDirection.LengthSquared() > 0.0001f
                ? Vector3.Normalize(message.HitDirection)
                : Vector3.Normalize(toTarget);
            if (toTarget.LengthSquared() > 0.0001f &&
                Vector3.Dot(direction, Vector3.Normalize(toTarget)) < 0.2f)
            {
                SendRejectedMeleeHitResult(sourceClientId, message.ClientTick, targetPoint,
                    direction, attacker.ComponentBody?.Velocity ?? Vector3.Zero);
                return;
            }

            // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Attack
            // A network attack is already spatially validated above. Establish predator aggro
            // immediately instead of waiting for a second request when host-side hit RNG misses.
            CreatureCategory predatorMask = CreatureCategory.LandPredator | CreatureCategory.WaterPredator;
            if ((target.Category & predatorMask) != 0)
            {
                ComponentChaseBehavior chase = targetEntity.FindComponent<ComponentChaseBehavior>();
                if (chase != null && chase.Target == null)
                    chase.Attack(attacker, 30f, 60f, true);
                ComponentHerdBehavior herd = targetEntity.FindComponent<ComponentHerdBehavior>();
                herd?.CallNearbyCreaturesHelp(attacker, 20f, 30f, false);
            }
            else
            {
                // Source: Survivalcraft/Game/ComponentRunAwayBehavior.cs:
                // ComponentRunAwayBehavior.RunAwayFrom
                // Establish the same native attacker reference that ComponentHealth.Attacked
                // would create locally, even when a valid host-side tool hit later misses RNG.
                targetEntity.FindComponent<ComponentRunAwayBehavior>()?.RunAwayFrom(
                    attacker.ComponentBody);
            }

            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Hit
            // The host recomputes hit probability, tool power, damage and Attacked events.
            float previousHealth = target.ComponentHealth?.Health ?? 0f;
            bool randomStateChanged = TryApplyDeterministicMeleeRandomState(miner,
                sourceClientId, message.ClientTick, out ulong previousRandomState);
            try
            {
                miner.Hit(targetBody, targetPoint, direction);
            }
            finally
            {
                if (randomStateChanged)
                    RestoreDeterministicMeleeRandomState(miner, previousRandomState);
            }
            SendAuthoritativeMeleeHitResult(sourceClientId, message.ClientTick,
                target.ComponentHealth, previousHealth, targetPoint, direction,
                attacker.ComponentBody?.Velocity ?? Vector3.Zero);
            if (m_hostAnimalSync.TryGetValue(targetEntity, out AnimalSyncMetadata metadata))
            {
                metadata.NextSendTime = 0.0;
                metadata.HighPriorityUntil = Time.RealTime + 3.0;
            }
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Hit
        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        private void HandleMeleeHitResultMessage(MeleeHitResultMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null ||
                message.RequestSequence <= 0 ||
                !float.IsFinite(message.Damage) || !IsFinite(message.HitPoint) ||
                !IsFinite(message.HitDirection) || !IsFinite(message.AttackerVelocity))
                return;
            bool hasPrediction = m_localMeleePredictions.TryGetValue(message.RequestSequence,
                out LocalMeleePrediction prediction);
            if (hasPrediction)
                m_localMeleePredictions.Remove(message.RequestSequence);
            if (message.ResultKind == MeleeHitResultMessage.MeleeHitResultKind.Rejected)
                return;

            Project project = GameManager.Project;
            ComponentPlayer localPlayer = project?.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(player => player != null &&
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer?.ComponentMiner == null) return;
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Hit
            // The native local prediction already emitted swoosh, hit/miss particles and impact
            // audio when it advanced m_lastHitTime. The authoritative reply only confirms it.
            bool nativePredictionExecuted = hasPrediction &&
                ReferenceEquals(prediction.Miner, localPlayer.ComponentMiner) &&
                ModManager.ModParentField.GetParentField<double>(prediction.Miner,
                    "m_lastHitTime", typeof(ComponentMiner)) > prediction.PreviousHitTime;
            if (nativePredictionExecuted)
                return;
            Vector3 direction = message.HitDirection.LengthSquared() > 0.0001f
                ? Vector3.Normalize(message.HitDirection)
                : Vector3.UnitZ;
            localPlayer.ComponentMiner.Poke(forceRestart: true);
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            audio?.PlaySound("Audio/Swoosh", 1f,
                m_audioEventRandom.Float(-0.2f, 0.2f), message.HitPoint, 3f,
                autoDelay: false);
            if (message.ResultKind == MeleeHitResultMessage.MeleeHitResultKind.Miss)
            {
                project.FindSubsystem<SubsystemParticles>(false)?.AddParticleSystem(
                    new HitValueParticleSystem(message.HitPoint + 0.75f * direction,
                        direction + message.AttackerVelocity, Color.White, "miss"));
                return;
            }
            audio?.PlayRandomSound("Audio/Impacts/Body", 1f,
                m_audioEventRandom.Float(-0.3f, 0.3f), message.HitPoint, 4f,
                autoDelay: false);
            string text = (0f - message.Damage).ToString("0", CultureInfo.InvariantCulture);
            project.FindSubsystem<SubsystemParticles>(false)?.AddParticleSystem(
                new HitValueParticleSystem(message.HitPoint + 0.75f * direction,
                    direction + message.AttackerVelocity, Color.White, text));
        }

        private void HandleAnimalEntityMessage(EntityMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null) return;
            if (message.Action == EntityMessage.EntityAction.Add)
            {
                if (IsMountNetworkId(message.EntityId))
                {
                    if (!string.IsNullOrWhiteSpace(message.TemplateName))
                        m_remoteMountTemplates[message.EntityId] = message.TemplateName;
                }
                else if (!string.IsNullOrWhiteSpace(message.TemplateName))
                    m_remoteAnimalTemplates[message.EntityId] = message.TemplateName;
                return;
            }

            if (IsMountNetworkId(message.EntityId))
                RemoveRemoteMount(message.EntityId);
            else
                RemoveRemoteAnimal(message.EntityId);
        }

        private bool IsMountNetworkId(ushort id) => id >= MountEntityIdStart;

        private void RemoveRemoteMount(ushort id)
        {
            if (m_remoteMounts.TryGetValue(id, out Entity entity))
            {
                foreach (PlayerData playerData in m_networkPlayerData.Values.ToArray())
                {
                    ComponentRider rider = playerData?.ComponentPlayer?.ComponentRider;
                    if (rider?.Mount?.Entity == entity)
                        rider.StartDismounting();
                }
                if (entity?.IsAddedToProject == true && entity.Project == GameManager.Project)
                    entity.Project.RemoveEntity(entity, true);
                m_remoteMounts.Remove(id);
            }
            m_remoteMountTemplates.Remove(id);
            m_remoteMountSync.Remove(id);
        }

        private Entity EnsureRemoteMount(ushort id, Vector3 position,
            Quaternion rotation, Vector3 velocity)
        {
            if (m_remoteMounts.TryGetValue(id, out Entity existing) &&
                existing?.IsAddedToProject == true)
                return existing;
            if (!m_remoteMountTemplates.TryGetValue(id, out string templateName) ||
                string.IsNullOrWhiteSpace(templateName) || GameManager.Project == null)
                return null;
            try
            {
                Entity entity = DatabaseManager.CreateEntity(
                    GameManager.Project, templateName, new ValuesDictionary(), true);
                ComponentBody body = entity?.FindComponent<ComponentBody>();
                if (entity == null || body == null) return null;
                body.Position = position;
                body.Rotation = rotation;
                body.Velocity = velocity;
                GameManager.Project.AddEntity(entity);
                SubsystemUpdate subsystemUpdate = GameManager.Project.FindSubsystem<SubsystemUpdate>(true);
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    if (updateable is ComponentBoat || updateable is ComponentSpawn)
                        subsystemUpdate.RemoveUpdateable(updateable);
                }
                m_remoteMounts[id] = entity;
                m_remoteMountSync[id] = new RemoteMountSyncState
                {
                    LastServerTick = client.Step,
                    Position = position,
                    Rotation = rotation,
                    Velocity = velocity,
                    LastUpdateTime = Time.RealTime,
                    HasTransform = true
                };
                return entity;
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Failed to recreate mount {id}: {ex.Message}");
                m_remoteMounts.Remove(id);
                return null;
            }
        }

        private void HandleRemoteMountBodyUpdate(BodyUpdateMessage.BodyItem item, int serverTick)
        {
            ComponentPlayer localPlayer = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer?.ComponentBody != null)
            {
                float visibility = MathUtils.Min(
                    GameManager.Project.FindSubsystem<SubsystemSky>(false)?.VisibilityRange ?? 64f,
                    64f) + 8f;
                if (Vector3.DistanceSquared(localPlayer.ComponentBody.Position, item.Position) >
                    visibility * visibility)
                {
                    if (m_remoteMounts.ContainsKey(item.EntityId))
                        RemoveRemoteMount(item.EntityId);
                    return;
                }
            }
            Entity entity = EnsureRemoteMount(item.EntityId, item.Position,
                item.Rotation, item.Velocity);
            ComponentBody body = entity?.FindComponent<ComponentBody>();
            if (body == null) return;
            if (!m_remoteMountSync.TryGetValue(item.EntityId, out RemoteMountSyncState state))
            {
                state = new RemoteMountSyncState();
                m_remoteMountSync[item.EntityId] = state;
            }
            if (serverTick < state.LastServerTick) return;
            state.LastServerTick = serverTick;
            state.Position = item.Position;
            state.Rotation = item.Rotation;
            state.Velocity = item.Velocity;
            state.LastUpdateTime = Time.RealTime;
            state.HasTransform = true;
            foreach (int playerClientId in m_receivedMountStates.Keys.ToArray())
                TryApplyReceivedMountState(playerClientId);
        }

        // Source: Survivalcraft/Game/ComponentSpawn.cs:ComponentSpawn.Update
        private void RemoveRemoteAnimal(ushort id)
        {
            if (m_remoteAnimals.TryGetValue(id, out Entity entity))
            {
                StopRemoteAnimalShapeshiftEffect(entity);
                if (entity?.IsAddedToProject == true && entity.Project == GameManager.Project)
                    entity.Project.RemoveEntity(entity, true);
                m_remoteAnimals.Remove(id);
            }
            m_remoteAnimalTemplates.Remove(id);
            m_remoteAnimalSync.Remove(id);
            m_loggedRemoteAnimalFailures.Remove(id);
        }

        private Entity EnsureRemoteAnimal(ushort id, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            if (m_remoteAnimals.TryGetValue(id, out Entity existing) && existing?.IsAddedToProject == true)
                return existing;
            if (!m_remoteAnimalTemplates.TryGetValue(id, out string templateName) ||
                string.IsNullOrWhiteSpace(templateName) || GameManager.Project == null)
                return null;

            try
            {
                Entity entity = DatabaseManager.CreateEntity(
                    GameManager.Project, templateName, new ValuesDictionary(), true);
                ComponentBody body = entity?.FindComponent<ComponentBody>();
                if (entity == null || body == null) return null;
                body.Position = position;
                body.Rotation = rotation;
                body.Velocity = velocity;
                GameManager.Project.AddEntity(entity);
                // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.RemoveUpdateable
                // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.Update
                // Remote animals are presentation replicas. Their local AI and spawn state machine
                // must not compete with authoritative movement or despawn them at chunk edges.
                SubsystemUpdate subsystemUpdate = GameManager.Project.FindSubsystem<SubsystemUpdate>(true);
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    if (updateable is ComponentBehavior || updateable is ComponentLocomotion ||
                        updateable is ComponentHealth || updateable is ComponentShapeshifter ||
                        ReferenceEquals(updateable, entity.FindComponent<ComponentSpawn>()))
                        subsystemUpdate.RemoveUpdateable(updateable);
                }
                StopRemoteAnimalShapeshiftEffect(entity);
                m_remoteAnimals[id] = entity;
                m_loggedRemoteAnimalFailures.Remove(id);
                return entity;
            }
            catch (Exception ex)
            {
                if (m_loggedRemoteAnimalFailures.Add(id))
                    Log.Error($"[ScMP] Failed to recreate animal {id} ({templateName}): {ex.Message}");
                m_remoteAnimals.Remove(id);
                return null;
            }
        }

        private void SetRemoteAnimalTemplate(ushort id, string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName)) return;
            bool changed = m_remoteAnimalTemplates.TryGetValue(id, out string oldTemplate) &&
                !string.Equals(oldTemplate, templateName, StringComparison.Ordinal);
            if (changed && m_remoteAnimals.TryGetValue(id, out Entity oldEntity))
            {
                // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.ComponentSpawn_Despawned
                StopRemoteAnimalShapeshiftEffect(oldEntity);
                if (oldEntity?.IsAddedToProject == true && oldEntity.Project == GameManager.Project)
                    oldEntity.Project.RemoveEntity(oldEntity, true);
                m_remoteAnimals.Remove(id);
                m_remoteAnimalSync.Remove(id);
            }
            m_remoteAnimalTemplates[id] = templateName;
        }

        private void HandleAnimalBodyUpdate(BodyUpdateMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message?.Bodies == null || GameManager.Project == null) return;
            MaintainClientWorldObjects();
            HashSet<ushort> fullSnapshotIds = message.IsFullSnapshot
                ? new HashSet<ushort>()
                : null;
            // Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.SetUpdateLocation
            // The host remains authoritative for the whole animal population, but a client only
            // owns replicas in its local visibility window. Hysteresis prevents an overlapping
            // A/B boundary from repeatedly destroying and recreating the same animal.
            ComponentPlayer localPlayer = GameManager.Project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            bool hasVisibility = localPlayer?.ComponentBody != null;
            Vector3 localPosition = hasVisibility
                ? localPlayer.ComponentBody.Position
                : Vector3.Zero;
            float visibility = MathUtils.Min(
                GameManager.Project.FindSubsystem<SubsystemSky>(false)?.VisibilityRange ?? 64f,
                64f);
            foreach (BodyUpdateMessage.BodyItem item in message.Bodies)
            {
                // Source: ScMultiplayerWorldSync.cs:SendMountUpdates
                // Cache a mount template before visibility filtering. A boat outside the initial
                // camera radius can be recreated when the client later approaches it.
                if (IsMountNetworkId(item.EntityId) &&
                    item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Template) &&
                    !string.IsNullOrWhiteSpace(item.TemplateName))
                    m_remoteMountTemplates[item.EntityId] = item.TemplateName;
                if (hasVisibility)
                {
                    bool alreadyVisible = m_remoteAnimals.ContainsKey(item.EntityId);
                    float radius = visibility + (alreadyVisible ? 8f : 0f);
                    if (Vector3.DistanceSquared(localPosition, item.Position) > radius * radius)
                        continue;
                }
                fullSnapshotIds?.Add(item.EntityId);
                if (IsMountNetworkId(item.EntityId) ||
                    m_remoteMountTemplates.ContainsKey(item.EntityId))
                {
                    HandleRemoteMountBodyUpdate(item, message.ServerTick);
                    continue;
                }
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Template) &&
                    !string.IsNullOrWhiteSpace(item.TemplateName))
                    SetRemoteAnimalTemplate(item.EntityId, item.TemplateName);
                Entity entity = EnsureRemoteAnimal(item.EntityId, item.Position, item.Rotation, item.Velocity);
                ComponentCreature creature = entity?.FindComponent<ComponentCreature>();
                ComponentBody body = creature?.ComponentBody;
                if (creature == null || body == null) continue;

                if (!m_remoteAnimalSync.TryGetValue(item.EntityId,
                    out RemoteAnimalSyncState networkState))
                {
                    networkState = new RemoteAnimalSyncState();
                    m_remoteAnimalSync[item.EntityId] = networkState;
                }
                int previousServerTick = networkState.LastServerTick;
                Vector3 previousVelocity = networkState.Velocity;
                bool hadTransform = networkState.HasTransform;
                float snapshotInterval = networkState.EstimatedSnapshotInterval;
                if (message.ServerTick < previousServerTick) continue;
                if (previousServerTick > 0 && message.ServerTick > previousServerTick)
                {
                    snapshotInterval = MathUtils.Clamp(
                        (message.ServerTick - previousServerTick) * ServerTickDuration,
                        0.03f, RemoteAnimalPredictionLimit);
                    networkState.EstimatedSnapshotInterval = MathUtils.Lerp(
                        networkState.EstimatedSnapshotInterval, snapshotInterval, 0.2f);
                }
                networkState.LastServerTick = message.ServerTick;
                networkState.LastUpdateTime = Time.RealTime;
                float delaySample = MathUtils.Clamp(
                    (client.Step - message.ServerTick) * ServerTickDuration,
                    0f, RemoteAnimalPredictionLimit);
                networkState.EstimatedDelay = networkState.EstimatedDelay <= 0f
                    ? delaySample
                    : MathUtils.Lerp(networkState.EstimatedDelay, delaySample, 0.15f);

                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.BehaviorState))
                {
                    if (!networkState.SimulationSeedApplied ||
                        networkState.SimulationSeed != item.SimulationSeed)
                    {
                        networkState.SimulationSeed = item.SimulationSeed;
                        networkState.SimulationSeedApplied = true;
                        ApplyAnimalSimulationSeed(entity, item.SimulationSeed);
                    }
                    ApplyRemoteAnimalBehaviorState(item.EntityId, entity, item);
                }
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Health) &&
                    creature.ComponentHealth != null)
                {
                    // Source: ComponentHealth.cs:ComponentHealth.Update
                    if (!m_remoteAnimalSync.TryGetValue(item.EntityId,
                        out RemoteAnimalSyncState state))
                    {
                        state = new RemoteAnimalSyncState();
                        m_remoteAnimalSync[item.EntityId] = state;
                    }
                    float health = MathUtils.Saturate(item.Health);
                    bool wasInjured = state.HasHealth && health < state.LastHealth - 0.001f;
                    ModManager.ModParentField.ModifyParentField(
                        creature.ComponentHealth, "<Health>k__BackingField",
                        health, typeof(ComponentHealth));
                    ModManager.ModParentField.ModifyParentField(
                        creature.ComponentHealth, "m_lastHealth", health,
                        typeof(ComponentHealth));
                    ModManager.ModParentField.ModifyParentField(
                        creature.ComponentHealth, "<HealthChange>k__BackingField", 0f,
                        typeof(ComponentHealth));
                    if (wasInjured && creature.ComponentCreatureModel != null)
                    {
                        // Source: ComponentCreatureModel.cs:ComponentCreatureModel.Update
                        ModManager.ModParentField.ModifyParentField(
                            creature.ComponentCreatureModel, "m_injuryColorFactor", 1f,
                            typeof(ComponentCreatureModel));
                    }
                    if (item.DamageSequence > 0 &&
                        item.DamageSequence > state.LastDamageSequence)
                    {
                        state.LastDamageSequence = item.DamageSequence;
                        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                        creature.ComponentCreatureSounds.PlayPainSound();
                    }
                    state.LastHealth = health;
                    state.HasHealth = true;
                    if (health <= 0f && !state.DeathTime.HasValue)
                    {
                        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                        SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<
                            SubsystemGameInfo>(false);
                        if (gameInfo != null)
                        {
                            state.DeathTime = gameInfo.TotalElapsedGameTime;
                            ModManager.ModParentField.ModifyParentField(
                                creature.ComponentHealth, "<DeathTime>k__BackingField",
                                state.DeathTime, typeof(ComponentHealth));
                        }
                    }
                    else if (health > 0f && state.DeathTime.HasValue)
                    {
                        state.DeathTime = null;
                        state.LocalDespawnStarted = false;
                        ModManager.ModParentField.ModifyParentField(
                            creature.ComponentHealth, "<DeathTime>k__BackingField",
                            (double?)null, typeof(ComponentHealth));
                    }
                }

                // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
                // Store authoritative keyframes here. UpdateRemoteAnimalPresentations performs
                // continuous prediction and correction instead of snapping on packet arrival.
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Position))
                    networkState.Position = item.Position;
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Rotation))
                    networkState.Rotation = item.Rotation;
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Velocity))
                {
                    networkState.Velocity = item.Velocity;
                    if (hadTransform && snapshotInterval > 0.001f)
                    {
                        Vector3 acceleration = (item.Velocity - previousVelocity) / snapshotInterval;
                        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
                        // Vertical velocity contains discrete jump and collision impulses. Treating
                        // those keyframe deltas as continuous acceleration doubles takeoff impulses
                        // and reverses landing impulses into false upward movement.
                        acceleration.Y = 0f;
                        float accelerationLength = acceleration.Length();
                        const float maxAnimalAcceleration = 24f;
                        if (accelerationLength > maxAnimalAcceleration)
                            acceleration *= maxAnimalAcceleration / accelerationLength;
                        networkState.Acceleration = Vector3.Lerp(
                            networkState.Acceleration, acceleration, 0.35f);
                    }
                    else
                    {
                        networkState.Acceleration = Vector3.Zero;
                    }
                }
                if (!networkState.HasTransform &&
                    item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Position))
                {
                    body.Position = networkState.Position;
                    body.Rotation = networkState.Rotation;
                    body.Velocity = networkState.Velocity;
                    networkState.HasTransform = true;
                    networkState.PresentationInitialized = true;
                    networkState.PresentationPosition = networkState.Position;
                    networkState.HasPresentationPosition = true;
                    networkState.SmoothedVelocity = networkState.Velocity;
                    networkState.HasSmoothedVelocity = true;
                }
                ComponentLocomotion locomotion = creature.ComponentLocomotion;
                if (locomotion != null)
                {
                    if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.LookAngles))
                        networkState.LookAngles = item.LookAngles;
                    if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Movement))
                    {
                        networkState.WalkOrder = item.WalkOrder;
                        networkState.FlyOrder = item.FlyOrder;
                        networkState.SwimOrder = item.SwimOrder;
                        networkState.TurnOrder = item.TurnOrder;
                        networkState.JumpOrder = item.JumpOrder;
                        networkState.MotionFlags = item.MotionFlags;
                        locomotion.WalkOrder = item.WalkOrder;
                        locomotion.FlyOrder = item.FlyOrder;
                        locomotion.SwimOrder = item.SwimOrder;
                        locomotion.TurnOrder = item.TurnOrder;
                        locomotion.JumpOrder = item.JumpOrder;
                        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
                        // Publish Last* immediately; the restored local locomotion then advances
                        // these same orders between authoritative keyframes.
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastWalkOrder>k__BackingField", item.WalkOrder,
                            typeof(ComponentLocomotion));
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastFlyOrder>k__BackingField", item.FlyOrder,
                            typeof(ComponentLocomotion));
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastSwimOrder>k__BackingField", item.SwimOrder,
                            typeof(ComponentLocomotion));
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastTurnOrder>k__BackingField", item.TurnOrder,
                            typeof(ComponentLocomotion));
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastJumpOrder>k__BackingField", item.JumpOrder,
                            typeof(ComponentLocomotion));
                    }
                }
                networkState.AttackOrder = item.AttackOrder;
                networkState.FeedOrder = item.FeedOrder;
                ComponentCreatureModel model = creature.ComponentCreatureModel;
                if (model != null)
                {
                    model.AttackOrder = networkState.AttackOrder;
                    model.FeedOrder = networkState.FeedOrder;
                }
				// Source: ScMultiplayer.HandleMountStateMessage
				// A rider state may arrive before its animal replica. Retry ordered mount results
				// once the authoritative animal entity becomes available.
				foreach (int playerClientId in m_receivedMountStates.Keys.ToArray())
					TryApplyReceivedMountState(playerClientId);
			}
            if (fullSnapshotIds != null &&
                message.ServerTick >= m_lastFullAnimalSnapshotTick)
            {
                // Source: ScMultiplayer.cs:SendAdaptiveAnimalUpdates
                // Preserve a replica that already received a newer incremental add, otherwise the
                // complete snapshot is the authoritative membership set for this server tick.
                foreach (ushort id in m_remoteAnimals.Keys.Where(id =>
                    !fullSnapshotIds.Contains(id) &&
                    (!m_remoteAnimalSync.TryGetValue(id, out RemoteAnimalSyncState state) ||
                        state.LastServerTick <= message.ServerTick)).ToArray())
                    RemoveRemoteAnimal(id);
                m_lastFullAnimalSnapshotTick = message.ServerTick;
            }
        }

        // Source: Survivalcraft/Game/ComponentCreatureSounds.cs:ComponentCreatureSounds.PlayIdleSound
        // Source: Survivalcraft/Game/ComponentHowlBehavior.cs:ComponentHowlBehavior.Update
        private void HandleAnimalSoundMessage(AnimalSoundMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null ||
                message.Sequence <= 0 || !IsFinite(message.Position) || GameManager.Project == null)
                return;
            ComponentPlayer localPlayer = GameManager.Project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer?.ComponentBody != null)
            {
                float visibility = MathUtils.Min(
                    GameManager.Project.FindSubsystem<SubsystemSky>(false)?.VisibilityRange ?? 64f,
                    64f) + 8f;
                if (Vector3.DistanceSquared(localPlayer.ComponentBody.Position,
                    message.Position) > visibility * visibility)
                    return;
            }
            Entity entity = m_remoteAnimals.TryGetValue(message.EntityId, out Entity existing)
                ? existing
                : EnsureRemoteAnimal(message.EntityId, message.Position,
                    Quaternion.Identity, Vector3.Zero);
            ComponentCreatureSounds sounds = entity?.FindComponent<ComponentCreatureSounds>();
            if (entity == null || sounds == null) return;
            if (!m_remoteAnimalSync.TryGetValue(message.EntityId,
                out RemoteAnimalSyncState state))
            {
                state = new RemoteAnimalSyncState();
                m_remoteAnimalSync[message.EntityId] = state;
            }
            if (message.Sequence <= state.LastSoundSequence) return;
            state.LastSoundSequence = message.Sequence;

            string soundName = string.Empty;
            float minDistance = 0f;
            bool autoDelay = false;
            if (message.SoundType == AnimalSoundType.Idle)
            {
                soundName = ModManager.ModParentField.GetParentField(
                    sounds, "m_idleSound", typeof(ComponentCreatureSounds)) as string;
                minDistance = ModManager.ModParentField.GetParentField<float>(
                    sounds, "m_idleSoundMinDistance", typeof(ComponentCreatureSounds));
                if (string.IsNullOrEmpty(soundName))
                {
                    soundName = ModManager.ModParentField.GetParentField(
                        sounds, "m_rareIdleSound", typeof(ComponentCreatureSounds)) as string;
                    minDistance = ModManager.ModParentField.GetParentField<float>(
                        sounds, "m_rareIdleSoundMinDistance", typeof(ComponentCreatureSounds));
                }
            }
            else if (message.SoundType == AnimalSoundType.Attack)
            {
                soundName = ModManager.ModParentField.GetParentField(
                    sounds, "m_attackSound", typeof(ComponentCreatureSounds)) as string;
                minDistance = ModManager.ModParentField.GetParentField<float>(
                    sounds, "m_attackSoundMinDistance", typeof(ComponentCreatureSounds));
            }
            else if (message.SoundType == AnimalSoundType.Howl)
            {
                ComponentHowlBehavior howl = entity.FindComponent<ComponentHowlBehavior>();
                soundName = howl == null
                    ? string.Empty
                    : ModManager.ModParentField.GetParentField(
                        howl, "m_howlSoundName", typeof(ComponentHowlBehavior)) as string;
                minDistance = 10f;
                autoDelay = true;
            }
            if (!string.IsNullOrEmpty(soundName))
            {
                GameManager.Project.FindSubsystem<SubsystemAudio>(false)?.PlayRandomSound(
                    soundName, 1f, m_audioEventRandom.Float(-0.1f, 0.1f),
                    message.Position, minDistance, autoDelay);
            }
        }

        private void ApplyRemoteAnimalBehaviorState(ushort id, Entity entity, BodyUpdateMessage.BodyItem item)
        {
            if (!m_remoteAnimalSync.TryGetValue(id, out RemoteAnimalSyncState state))
            {
                state = new RemoteAnimalSyncState();
                m_remoteAnimalSync[id] = state;
            }
            if (state.SyncTier != item.SyncTier)
            {
                // Source: ScMultiplayer.cs:GetAnimalSyncInterval
                // A tier change changes host cadence, not the visible correction speed. Discard
                // the previous cadence estimate without resetting the independent presentation
                // position to a value that local body collision could have displaced.
                state.SyncTier = item.SyncTier;
                state.Acceleration = Vector3.Zero;
                state.EstimatedDelay = 0f;
                state.EstimatedSnapshotInterval = (float)GetAnimalSyncInterval(item.SyncTier);
            }
            state.BehaviorState = item.ActiveBehaviorState ?? string.Empty;
            state.TargetEntityId = item.TargetEntityId;
            state.HerdName = item.HerdName ?? string.Empty;
            string shapeshiftTarget = item.ShapeshiftTarget ?? string.Empty;
            if (!string.IsNullOrEmpty(shapeshiftTarget) &&
                state.ShapeshiftTarget != shapeshiftTarget)
                StartRemoteAnimalShapeshiftEffect(entity, shapeshiftTarget);
            state.ShapeshiftTarget = shapeshiftTarget;
            string activeBehaviorName = (item.ActiveBehaviorState ?? string.Empty).Split(':')[0];
            int separator = (item.ActiveBehaviorState ?? string.Empty).IndexOf(':');
            string stateName = separator >= 0
                ? item.ActiveBehaviorState.Substring(separator + 1)
                : string.Empty;
            foreach (ComponentBehavior behavior in entity.FindComponents<ComponentBehavior>())
            {
                if (behavior == null) continue;
                bool active = behavior.GetType().Name == activeBehaviorName;
                behavior.IsActive = active;
                if (!active || string.IsNullOrEmpty(stateName)) continue;
                StateMachine stateMachine = GetBehaviorStateMachine(behavior);
                if (stateMachine != null && stateMachine.CurrentState != stateName)
                {
                    try { stateMachine.TransitionTo(stateName); }
                    catch (Exception) { }
                }
            }
            ApplyRemoteAnimalAggroTarget(entity, state.TargetEntityId);
        }

        // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Attack
        private void ApplyRemoteAnimalAggroTarget(Entity entity, int targetEntityId)
        {
            ComponentChaseBehavior chase = entity?.FindComponent<ComponentChaseBehavior>();
            if (chase == null) return;
            ComponentCreature target = ResolveRemoteAnimalTarget(targetEntityId);
            ModManager.ModParentField.ModifyParentField(
                chase, "m_target", target, typeof(ComponentChaseBehavior));
        }

        // Source: ScMultiplayer.cs:GetCreatureTargetNetworkId
        private ComponentCreature ResolveRemoteAnimalTarget(int targetEntityId)
        {
            if (targetEntityId > 0)
            {
                return m_remoteAnimals.TryGetValue((ushort)targetEntityId, out Entity animal)
                    ? animal?.FindComponent<ComponentCreature>()
                    : null;
            }
            if (targetEntityId < 0)
            {
                int clientId = -targetEntityId - 1;
                return m_networkPlayerData.TryGetValue(clientId, out PlayerData playerData)
                    ? playerData?.ComponentPlayer
                    : null;
            }
            return null;
        }

        private StateMachine GetBehaviorStateMachine(ComponentBehavior behavior)
        {
            for (Type type = behavior?.GetType();
                type != null && type != typeof(object); type = type.BaseType)
            {
                FieldInfo field = type.GetField("m_stateMachine",
                    BindingFlags.Instance | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                if (field?.FieldType == typeof(StateMachine))
                    return ModManager.ModParentField.GetParentField<StateMachine>(
                        behavior, field.Name, field.DeclaringType);
            }
            return null;
        }

        // Source: ComponentShapeshifter.cs:ComponentShapeshifter.ComponentSpawn_Despawned
        private static void StopRemoteAnimalShapeshiftEffect(Entity entity)
        {
            ComponentShapeshifter shapeshifter = entity?.FindComponent<ComponentShapeshifter>();
            if (shapeshifter == null) return;
            // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.m_particleSystem
            // An inactive shapeshifter stores null; use the non-generic SuAPI getter so null is
            // accepted instead of aborting creation of the remote animal.
            ShapeshiftParticleSystem particleSystem =
                ModManager.ModParentField.GetParentField(
                    shapeshifter, "m_particleSystem", typeof(ComponentShapeshifter))
                    as ShapeshiftParticleSystem;
            if (particleSystem != null) particleSystem.Stopped = true;
            ModManager.ModParentField.ModifyParentField(
                shapeshifter, "m_particleSystem", null, typeof(ComponentShapeshifter));
            ModManager.ModParentField.ModifyParentField(
                shapeshifter, "m_spawnEntityTemplateName", string.Empty,
                typeof(ComponentShapeshifter));
        }

        // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.Update
        // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.ShapeshiftTo
        private static void StartRemoteAnimalShapeshiftEffect(Entity entity, string targetTemplate)
        {
            ComponentShapeshifter shapeshifter = entity?.FindComponent<ComponentShapeshifter>();
            ComponentBody body = entity?.FindComponent<ComponentBody>();
            Project project = entity?.Project;
            if (shapeshifter == null || body == null || project == null ||
                string.IsNullOrEmpty(targetTemplate))
                return;

            ShapeshiftParticleSystem particleSystem = new ShapeshiftParticleSystem
            {
                BoundingBox = body.BoundingBox
            };
            project.FindSubsystem<SubsystemParticles>(true).AddParticleSystem(particleSystem);
            ModManager.ModParentField.ModifyParentField(
                shapeshifter, "m_particleSystem", particleSystem,
                typeof(ComponentShapeshifter));
            ModManager.ModParentField.ModifyParentField(
                shapeshifter, "m_spawnEntityTemplateName", targetTemplate,
                typeof(ComponentShapeshifter));
            project.FindSubsystem<SubsystemAudio>(true).PlaySound(
                "Audio/Shapeshift", 1f, 0f, body.Position, 3f, autoDelay: true);
        }

        // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.Update
        private static void UpdateRemoteAnimalShapeshiftEffect(Entity entity)
        {
            ComponentShapeshifter shapeshifter = entity?.FindComponent<ComponentShapeshifter>();
            ComponentBody body = entity?.FindComponent<ComponentBody>();
            if (shapeshifter == null || body == null) return;
            ShapeshiftParticleSystem particleSystem =
                ModManager.ModParentField.GetParentField(
                    shapeshifter, "m_particleSystem", typeof(ComponentShapeshifter))
                    as ShapeshiftParticleSystem;
            if (particleSystem != null) particleSystem.BoundingBox = body.BoundingBox;
        }

        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
        // Source: Survivalcraft/Game/ComponentCreatureModel.cs:ComponentCreatureModel.Update
        private void UpdateRemoteAnimalPresentations(float dt)
        {
            if (IsHost || GameManager.Project == null) return;
            float step = MathUtils.Clamp(dt, 0f, 0.05f);
            double now = Time.RealTime;
            SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<
                SubsystemGameInfo>(false);
            var expiredAnimals = new List<ushort>();
            foreach (KeyValuePair<ushort, RemoteAnimalSyncState> item in
                m_remoteAnimalSync.ToArray())
            {
                if (!m_remoteAnimals.TryGetValue(item.Key, out Entity entity) ||
                    entity?.IsAddedToProject != true)
                    continue;
                ComponentCreature creature = entity.FindComponent<ComponentCreature>();
                ComponentBody body = creature?.ComponentBody;
                RemoteAnimalSyncState state = item.Value;
                UpdateRemoteAnimalShapeshiftEffect(entity);
                if (body == null || !state.HasTransform) continue;

                // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
                // Retain only the local collision floor. It may prevent a presentation replica from
                // entering terrain, but never becomes the next network interpolation baseline.
                bool hostGrounded =
                    state.MotionFlags.HasFlag(BodyUpdateMessage.BodyItem.MotionFlag.Grounded);
                bool hostImmersed =
                    state.MotionFlags.HasFlag(BodyUpdateMessage.BodyItem.MotionFlag.Immersed);
                bool hostFlying =
                    state.MotionFlags.HasFlag(BodyUpdateMessage.BodyItem.MotionFlag.Flying);
                bool locallyStanding = hostGrounded &&
                    (body.StandingOnValue.HasValue || body.StandingOnBody != null) &&
                    !hostImmersed && !hostFlying;
                float localCollisionFloor = MathUtils.Min(body.Position.Y, state.Position.Y);

                float arrivalAge = (float)MathUtils.Clamp(
                    now - state.LastUpdateTime, 0.0, RemoteAnimalPredictionLimit);
                float stepAge = MathUtils.Clamp(
                    (client.Step - state.LastServerTick) * ServerTickDuration,
                    0f, RemoteAnimalPredictionLimit);
                float predictionLimit = MathUtils.Clamp(
                    state.EstimatedSnapshotInterval * 1.25f, 0.1f,
                    RemoteAnimalPredictionLimit);
                float predictionTime = MathUtils.Min(
                    MathUtils.Max(MathUtils.Max(arrivalAge, stepAge), state.EstimatedDelay),
                    predictionLimit);
                float accelerationTime = MathUtils.Min(predictionTime, 0.35f);
                Vector3 predictedVelocity = state.Velocity +
                    state.Acceleration * accelerationTime;
                Vector3 predictedPosition = state.Position +
                    state.Velocity * predictionTime +
                    0.5f * state.Acceleration * accelerationTime * accelerationTime;
                if (!state.HasPresentationPosition)
                {
                    state.PresentationPosition = state.Position;
                    state.HasPresentationPosition = true;
                }
                Vector3 error = predictedPosition - state.PresentationPosition;
                float errorSquared = error.LengthSquared();
                if (!state.PresentationInitialized ||
                    errorSquared > MathUtils.Sqr(RemoteAnimalSnapDistance))
                {
                    state.PresentationPosition = state.Position;
                    body.Rotation = state.Rotation;
                    body.Velocity = state.Velocity;
                    state.SmoothedVelocity = state.Velocity;
                    state.HasSmoothedVelocity = true;
                    state.PresentationInitialized = true;
                }
                else
                {
                    Vector3 remainingError = predictedPosition - state.PresentationPosition;
                    const float correctionHorizon = 0.4f;
                    Vector3 correctionVelocity = remainingError / correctionHorizon;
                    // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
                    // Horizontal motion uses velocity prediction. Vertical motion follows the
                    // authoritative trajectory directly so correction velocity cannot add a second
                    // jump impulse or overshoot a landing.
                    correctionVelocity.Y = 0f;
                    const float maxCorrectionSpeed = 3f;
                    float correctionSpeed = correctionVelocity.Length();
                    if (correctionSpeed > maxCorrectionSpeed)
                        correctionVelocity *= maxCorrectionSpeed / correctionSpeed;
                    Vector3 desiredVelocity = predictedVelocity + correctionVelocity;
                    desiredVelocity.Y = state.Velocity.Y;
                    if (!state.HasSmoothedVelocity)
                    {
                        state.SmoothedVelocity = state.Velocity;
                        state.HasSmoothedVelocity = true;
                    }
                    float velocityBlend = 1f - (float)Math.Exp(
                        -8f * step);
                    state.SmoothedVelocity = Vector3.Lerp(
                        state.SmoothedVelocity, desiredVelocity, velocityBlend);
                    state.PresentationPosition.X += state.SmoothedVelocity.X * step;
                    state.PresentationPosition.Z += state.SmoothedVelocity.Z * step;
                    float verticalBlend = 1f - (float)Math.Exp(-12f * step);
                    state.PresentationPosition.Y = MathUtils.Lerp(
                        state.PresentationPosition.Y, predictedPosition.Y, verticalBlend);

                    float rotationBlend = 1f - (float)Math.Exp(
                        -10f * step);
                    body.Rotation = Quaternion.Slerp(
                        body.Rotation, state.Rotation, rotationBlend);
                }

                if (locallyStanding && state.PresentationPosition.Y < localCollisionFloor)
                {
                    state.PresentationPosition.Y = localCollisionFloor;
                    if (state.SmoothedVelocity.Y < 0f)
                        state.SmoothedVelocity.Y = 0f;
                }

                // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
                // Restore the host-driven presentation after this client's physics pass. This
                // prevents player/animal collision impulses from being fed into the next frame's
                // interpolation, while retaining the original replica and animation components.
                body.Position = state.PresentationPosition;
                body.Velocity = state.SmoothedVelocity;

                ComponentLocomotion locomotion = creature.ComponentLocomotion;
                if (locomotion != null)
                {
                    // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
                    // Physics follows the smoothed authoritative trajectory. Publish the state
                    // machine orders only as Last* values so model animation cannot apply a second,
                    // differently-oriented locomotion force and make animals slide diagonally.
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastWalkOrder>k__BackingField", state.WalkOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastFlyOrder>k__BackingField", state.FlyOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastSwimOrder>k__BackingField", state.SwimOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastTurnOrder>k__BackingField", state.TurnOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastJumpOrder>k__BackingField", state.JumpOrder,
                        typeof(ComponentLocomotion));
                    float lookBlend = 1f - (float)Math.Exp(-8f * step);
                    Vector2 lookAngles = Vector2.Lerp(
                        locomotion.LookAngles, state.LookAngles, lookBlend);
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "m_lookAngles", lookAngles,
                        typeof(ComponentLocomotion));
                }
                ApplyLocalMountedAnimalLook(entity, creature, step);
                ComponentCreature aggroTarget = ResolveRemoteAnimalTarget(state.TargetEntityId);
                ApplyRemoteAnimalAggroTarget(entity, state.TargetEntityId);
                ComponentCreatureModel model = creature.ComponentCreatureModel;
                if (model != null)
                {
                    if (aggroTarget?.ComponentCreatureModel != null)
                        model.LookAtOrder = aggroTarget.ComponentCreatureModel.EyePosition;
                    // Animal models consume and clear these orders every animation update.
                    model.AttackOrder = state.AttackOrder;
                    model.FeedOrder = state.FeedOrder;
                }

                // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                // Source: Survivalcraft/Game/ComponentSpawn.cs:ComponentSpawn.Update
                // Health and spawn updates are disabled on presentation replicas. Mirror only the
                // original corpse/despawn lifecycle as a fallback for a delayed/lost host removal.
                ComponentHealth health = creature.ComponentHealth;
                ComponentSpawn spawn = creature.ComponentSpawn;
                if (state.DeathTime.HasValue && gameInfo != null && health != null &&
                    spawn != null && health.CorpseDuration > 0f)
                {
                    double corpseAge = gameInfo.TotalElapsedGameTime - state.DeathTime.Value;
                    if (!state.LocalDespawnStarted && corpseAge >= health.CorpseDuration)
                    {
                        spawn.Despawn();
                        state.LocalDespawnStarted = true;
                    }
                    if (state.LocalDespawnStarted &&
                        corpseAge >= health.CorpseDuration + spawn.DespawnDuration)
                        expiredAnimals.Add(item.Key);
                }
            }
            foreach (ushort id in expiredAnimals) RemoveRemoteAnimal(id);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
        // Remote animal locomotion is intentionally disabled to preserve host authority. When the
        // local player is mounted, apply only the local head-look delta to the presentation model;
        // position, collision and riding ownership remain driven by host snapshots.
        private void ApplyLocalMountedAnimalLook(Entity entity, ComponentCreature creature, float dt)
        {
            if (IsHost || entity == null || creature?.ComponentLocomotion == null)
                return;
            ComponentPlayer localPlayer = GameManager.Project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer?.ComponentRider?.Mount?.Entity != entity)
                return;
            PlayerInput input = localPlayer.ComponentInput?.PlayerInput ?? default(PlayerInput);
            if (input.Look.LengthSquared() <= 0.000001f)
                return;
            ComponentLocomotion locomotion = creature.ComponentLocomotion;
            Vector2 lookAngles = locomotion.LookAngles;
            lookAngles.X += locomotion.LookSpeed * input.Look.X * dt;
            lookAngles.Y += locomotion.LookSpeed * input.Look.Y * dt;
            lookAngles.X = MathUtils.Clamp(lookAngles.X,
                0f - MathUtils.DegToRad(140f), MathUtils.DegToRad(140f));
            lookAngles.Y = MathUtils.Clamp(lookAngles.Y,
                0f - MathUtils.DegToRad(82f), MathUtils.DegToRad(82f));
            ModManager.ModParentField.ModifyParentField(
                locomotion, "m_lookAngles", lookAngles, typeof(ComponentLocomotion));
        }

        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
        // Remote mounts are presentation replicas. Their transform remains host-authoritative;
        // local boat physics is disabled in EnsureRemoteMount so it cannot drift between snapshots.
        private void UpdateRemoteMountPresentations(float dt)
        {
            if (IsHost || GameManager.Project == null) return;
            double now = Time.RealTime;
            foreach (KeyValuePair<ushort, RemoteMountSyncState> item in
                m_remoteMountSync.ToArray())
            {
                if (!m_remoteMounts.TryGetValue(item.Key, out Entity entity) ||
                    entity?.IsAddedToProject != true || !item.Value.HasTransform)
                    continue;
                ComponentBody body = entity.FindComponent<ComponentBody>();
                if (body == null) continue;
                float age = MathUtils.Clamp((float)(now - item.Value.LastUpdateTime), 0f, 0.25f);
                Vector3 target = item.Value.Position + item.Value.Velocity * age;
                float blend = MathUtils.Clamp(dt * 12f, 0.15f, 1f);
                body.Position = Vector3.Lerp(body.Position, target, blend);
                body.Rotation = Quaternion.Slerp(body.Rotation, item.Value.Rotation, blend);
                body.Velocity = item.Value.Velocity;
            }
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        // Client physics can independently add attraction and collision velocity between host
        // snapshots. Reapply the host trajectory after the local update so idle items do not
        // oscillate sideways and moving items remain smooth between 10Hz snapshots.
    }
}

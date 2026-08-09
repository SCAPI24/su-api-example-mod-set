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
        public void HandleGamePlayerHealthMessage(GamePlayerHealthMessage msg, int clientID)
        {
            if (msg == null) return;
            if (IsHost)
            {
                if (clientID <= 0 || msg.PlayerIndex != clientID ||
                    !m_networkPlayerData.TryGetValue(clientID, out PlayerData requestedPlayer) ||
                    requestedPlayer?.ComponentPlayer?.ComponentHealth == null)
                    return;
                ComponentHealth requestedHealth = requestedPlayer.ComponentPlayer.ComponentHealth;
                float requestedValue = MathUtils.Saturate(msg.Health);
                if (msg.HealthChange < -0.0001f && requestedValue < requestedHealth.Health)
                {
                    float requestedPreviousHealth = requestedHealth.Health;
                    requestedHealth.Injure(requestedHealth.Health - requestedValue, null,
                        ignoreInvulnerability: true, "Client damage request");
                    if (requestedHealth.Health < requestedPreviousHealth - 0.0001f)
                    {
                        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                        requestedPlayer.ComponentPlayer.ComponentCreatureSounds?.PlayPainSound();
                    }
                }
                // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.ProcessSlotItems
                // Eating is predicted by the local clothing inventory. Accept only an upward food
                // edge, then let the normal reliable equipment snapshot carry the consumed item.
                ComponentVitalStats requestedVital = requestedPlayer.ComponentPlayer.ComponentVitalStats;
                float requestedFood = MathUtils.Saturate(msg.Food);
                if (requestedVital != null &&
                    string.Equals(msg.CauseOrSource, "Client food request",
                        StringComparison.Ordinal) &&
                    requestedFood > requestedVital.Food + 0.0001f)
                {
                    ModManager.ModParentField.ModifyParentField(requestedVital, "m_food",
                        requestedFood, typeof(ComponentVitalStats));
                    ModManager.ModParentField.ModifyParentField(requestedVital, "m_lastFood",
                        requestedFood, typeof(ComponentVitalStats));
                }
                ComponentSleep requestedSleep = requestedPlayer.ComponentPlayer.ComponentSleep;
                if (requestedSleep != null && requestedSleep.IsSleeping != msg.IsSleeping)
                {
                    if (!msg.IsSleeping) requestedSleep.WakeUp();
                    else if (requestedSleep.CanSleep(out _)) requestedSleep.Sleep(true);
                }
                return;
            }
            if (clientID != 0) return;
            // msg.PlayerIndex = 发送方 ClientID, 写入 RemotePlayers
            int remoteClientId = msg.PlayerIndex;
            if (remoteClientId != client.ClientID &&
                !m_networkPlayerData.ContainsKey(remoteClientId))
                return;
            if (msg.AuthoritativeStateSequence <= 0)
                return;
            bool applyAuthoritativeState =
                !m_lastReceivedAuthoritativePlayerStateSequences.TryGetValue(
                    remoteClientId, out int lastAuthoritativeStateSequence) ||
                msg.AuthoritativeStateSequence > lastAuthoritativeStateSequence;
            if (applyAuthoritativeState)
                m_lastReceivedAuthoritativePlayerStateSequences[remoteClientId] =
                    msg.AuthoritativeStateSequence;
            // Source: ScMultiplayer.cs:NetworkMessageSender.SendPlayerHealthMessage
            // A duplicated delayed knockback packet can still carry the only immediate impulse.
            // Its own KnockbackSequence keeps that edge idempotent while the stale vital values
            // below remain rejected.
            if (!applyAuthoritativeState && !msg.HasKnockback)
                return;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer targetPlayer = remoteClientId == client.ClientID
                ? players?.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData))
                : (m_networkPlayerData.TryGetValue(remoteClientId, out PlayerData remoteData)
                    ? remoteData.ComponentPlayer
                    : null);
            float previousHealth = targetPlayer?.ComponentHealth?.Health ?? msg.Health;
            if (applyAuthoritativeState && remoteClientId == client.ClientID &&
                Time.RealTime < m_localRespawnPendingUntil && msg.Health <= 0f)
                return;
            if (applyAuthoritativeState && remoteClientId == client.ClientID && msg.Health > 0f)
                m_localRespawnPendingUntil = 0.0;
            int previousWholeLevel = targetPlayer?.PlayerData != null
                ? (int)MathUtils.Floor(MathUtils.Max(targetPlayer.PlayerData.Level, 1f))
                : -1;
            if (applyAuthoritativeState)
            {
                ApplyAuthoritativePlayerStats(targetPlayer, msg.Health, msg.Air, msg.Food,
                    msg.Stamina, msg.Sleep, msg.Temperature, msg.Wetness, msg.Level);
                if (remoteClientId == client.ClientID)
                    UpdateLocalLevelPresentation(targetPlayer, previousWholeLevel, msg.Level);
                (targetPlayer?.ComponentVitalStats as SuComponentVitalStats)?
                    .ApplyAuthoritativeTargetTemperature(msg.TargetTemperature);
                ApplyAuthoritativePlayerEffects(targetPlayer, msg);
                if (targetPlayer?.ComponentHealth != null && msg.HealthChange < -0.0001f &&
                    msg.Health < previousHealth - 0.0001f)
                {
                    ModManager.ModParentField.ModifyParentField(
                        targetPlayer.ComponentHealth, "m_lastHealth", previousHealth,
                        typeof(ComponentHealth));
                }
                if (msg.DamageSequence >= 0 && targetPlayer?.ComponentCreatureSounds != null)
                {
                    bool hasDamageBaseline = m_receivedDamageSequences.TryGetValue(
                        remoteClientId, out int lastDamageSequence);
                    if (!hasDamageBaseline || msg.DamageSequence > lastDamageSequence)
                    {
                        m_receivedDamageSequences[remoteClientId] = msg.DamageSequence;
                        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                        // The first snapshot carries historical sequence state. Only a later edge
                        // is a new injury that should make a sound on this client.
                        if (hasDamageBaseline)
                            targetPlayer.ComponentCreatureSounds.PlayPainSound();
                    }
                }
            }
            if (remoteClientId == client.ClientID)
            {
                if (applyAuthoritativeState)
                {
                    m_hasObservedClientHealth = true;
                    m_observedClientHealth = msg.Health;
                    m_observedClientFood = msg.Food;
                    m_observedClientSleeping = msg.IsSleeping;
                }
                if (msg.HasKnockback &&
                    msg.KnockbackSequence > m_lastLocalKnockbackSequence &&
                    targetPlayer?.ComponentBody != null)
                {
                    m_lastLocalKnockbackSequence = msg.KnockbackSequence;
                    targetPlayer.ComponentBody.Velocity = msg.BodyVelocity;
                    // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
                    // Apply the host's native stun once so local movement input cannot compete
                    // with the one-shot authoritative knockback impulse.
                    if (targetPlayer.ComponentLocomotion != null)
                    {
                        float elapsedSinceHit = MathUtils.Max(
                            (client.Step - msg.KnockbackServerTick) * ServerTickDuration, 0f);
                        float remainingStun = MathUtils.Max(
                            msg.KnockbackStunTime - elapsedSinceHit, 0f);
                        targetPlayer.ComponentLocomotion.StunTime = MathUtils.Max(
                            targetPlayer.ComponentLocomotion.StunTime,
                            remainingStun);
                    }
                    m_localKnockbackPositionCorrectionUntil = Time.RealTime +
                        LocalKnockbackPositionCorrectionDuration;
                    m_localKnockbackCorrectionStartTick = msg.KnockbackServerTick;
                    m_localInputBodyVelocity = msg.BodyVelocity;
                }
                return;
            }
            if (!applyAuthoritativeState)
                return;
            // Ignore delayed health snapshots from a client that has already left. Without this
            // guard the health handler can recreate the same stale RemotePlayers entry.
            NetworkPlayerState state;
            if (!RemotePlayers.TryGetValue(remoteClientId, out state))
            {
                state = new NetworkPlayerState { ClientID = remoteClientId };
                RemotePlayers[remoteClientId] = state;
            }

            state.Health = msg.Health;
            state.MaxHealth = msg.MaxHealth;
            state.IsDead = msg.IsDead;
            if (msg.HasKnockback &&
                msg.KnockbackSequence > state.LastKnockbackSequence)
            {
                state.LastKnockbackSequence = msg.KnockbackSequence;
                // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
                // All observers already receive host position snapshots. Temporarily tighten only
                // their presentation dead zone after a confirmed hit so the struck avatar settles
                // on the same host trajectory without adding another message type or steady traffic.
                state.KnockbackCorrectionUntil = Time.RealTime +
                    LocalKnockbackPositionCorrectionDuration;
                state.KnockbackCorrectionStartTick = msg.KnockbackServerTick;
            }
        }

        // Source: Survivalcraft/Game/ComponentLevel.cs:ComponentLevel.AddExperience
        private void UpdateLocalLevelPresentation(ComponentPlayer player, int previousWholeLevel,
            float authoritativeLevel)
        {
            int wholeLevel = (int)MathUtils.Floor(MathUtils.Max(authoritativeLevel, 1f));
            if (m_lastAuthoritativeLocalWholeLevel < 0)
            {
                m_lastAuthoritativeLocalWholeLevel = wholeLevel;
                return;
            }

            bool gainedLevel = player != null &&
                wholeLevel > m_lastAuthoritativeLocalWholeLevel &&
                wholeLevel > previousWholeLevel;
            m_lastAuthoritativeLocalWholeLevel = Math.Max(
                m_lastAuthoritativeLocalWholeLevel, wholeLevel);
            if (!gainedLevel)
                return;

            Project project = GameManager.Project;
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            if (project == null || audio == null || player.ComponentGui == null)
                return;

            double startTime = Time.FrameStartTime + 0.5;
            Time.QueueTimeDelayedExecution(startTime, () =>
            {
                if (ReferenceEquals(GameManager.Project, project))
                    player.ComponentGui.DisplaySmallMessage("You've gained a level!",
                        Color.White, blinking: true, playNotificationSound: false);
            });
            QueueLevelUpSound(project, audio, startTime, 0.0, -0.2f);
            QueueLevelUpSound(project, audio, startTime, 0.15, -0.03333333f);
            QueueLevelUpSound(project, audio, startTime, 0.3, 2f / 15f);
            QueueLevelUpSound(project, audio, startTime, 0.45, 23f / 60f);
            QueueLevelUpSound(project, audio, startTime, 0.75, -0.03333333f);
            QueueLevelUpSound(project, audio, startTime, 0.9, 23f / 60f);
        }

        // Source: Survivalcraft/Game/ComponentLevel.cs:ComponentLevel.AddExperience
        private static void QueueLevelUpSound(Project project, SubsystemAudio audio,
            double startTime, double delay, float pitch)
        {
            Time.QueueTimeDelayedExecution(startTime + delay, () =>
            {
                if (ReferenceEquals(GameManager.Project, project))
                    audio.PlaySound("Audio/ExperienceCollected", 1f, pitch, 0f, 0f);
            });
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        internal int GetDamageSequence(int playerIndex, float healthChange)
        {
            if (!m_damageSequences.TryGetValue(playerIndex, out int sequence)) sequence = 0;
            if (healthChange < -0.0001f)
                sequence = sequence == int.MaxValue ? 1 : sequence + 1;
            m_damageSequences[playerIndex] = sequence;
            return sequence;
        }

        // Source: SubsystemTime.cs:SubsystemTime.NextFrame
        // Split-screen keeps running while at least one player is outside GameMenuDialog. A
        // network room has players on other devices, so local dialogs must never pause its clock.
        private static void MaintainMultiplayerTimeFlow(Project project)
        {
            if (project == null || client?.IsConnected != true) return;
            SubsystemTime subsystemTime = project.FindSubsystem<SubsystemTime>(false);
            if (subsystemTime == null) return;
            ModManager.ModParentField.ModifyParentField(
                subsystemTime, "m_gameTimeFactor", 1f, typeof(SubsystemTime));
        }

        // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
        // A client owns only its local PlayerData, so vanilla would treat that one sleeping
        // player as "everyone asleep" and run the entire client world at 20 updates per frame.
        // The host already publishes authoritative elapsed world time; clients must keep normal
        // simulation cadence and follow those snapshots instead of duplicating sleep catch-up.
        internal void MaintainClientAuthoritativeTimeFlow(Project project)
        {
            if (IsHost || client?.IsConnected != true || project == null) return;
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (time == null || update == null) return;
            ModManager.ModParentField.ModifyParentField(time, "<FixedTimeStep>k__BackingField",
                (float?)null, typeof(SubsystemTime));
            update.UpdatesPerFrame = 1;
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
        // Source: Survivalcraft/Game/ComponentOnFire.cs:ComponentOnFire.Update
        private void ApplyAuthoritativePlayerEffects(
            ComponentPlayer player, GamePlayerHealthMessage message)
        {
            if (player == null || message == null) return;
            if (player.ComponentSleep != null)
            {
                if (message.IsSleeping)
                {
                    m_pendingClientSleepWakeups.Remove(player.ComponentSleep);
                    if (!player.ComponentSleep.IsSleeping)
                        player.ComponentSleep.Sleep(true);
                    if (message.SleepStartTime > 0.0 &&
                        !double.IsNaN(message.SleepStartTime) &&
                        !double.IsInfinity(message.SleepStartTime))
                    {
                        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
                        // Replace the local request-arrival time with the host's session boundary
                        // so the automatic 180-second/daylight wake test is shared by all peers.
                        ModManager.ModParentField.ModifyParentField(
                            player.ComponentSleep, "m_sleepStartTime",
                            (double?)message.SleepStartTime, typeof(ComponentSleep));
                    }
                    // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
                    // A replicated value of exactly 1 would make the client run the native 20x
                    // all-players-sleep loop before the next circuit update can clamp it.
                    float authoritativeSleepFactor = MathUtils.Min(
                        MathUtils.Clamp(message.SleepFactor, 0f, 1f), 0.999f);
                    ModManager.ModParentField.ModifyParentField(
                        player.ComponentSleep, "m_sleepFactor",
                        authoritativeSleepFactor,
                        typeof(ComponentSleep));
                }
                else if (player.ComponentSleep.IsSleeping)
                {
                    // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
                    // CircuitSynchronizer.BeginPostAccelerationRebase
                    // A reliable player snapshot can arrive before the host acceleration edge.
                    // Keep the sleep presentation until the authoritative circuit snapshot and
                    // its following fence have completed, so the player never wakes onto stale
                    // counter values.
                    if (ShouldDeferClientSleepWakeup())
                        m_pendingClientSleepWakeups.Add(player.ComponentSleep);
                    else
                        player.ComponentSleep.WakeUp();
                }
            }
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            if (onFire != null)
                ModManager.ModParentField.ModifyParentField(
                    onFire, "m_fireDuration", MathUtils.Max(message.FireDuration, 0f), typeof(ComponentOnFire));
            if (flu != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    flu, "m_fluDuration", MathUtils.Max(message.FluDuration, 0f), typeof(ComponentFlu));
                (flu as SuComponentFlu)?.ApplyAuthoritativeCough(
                    message.CoughSequence, message.IsCoughing);
            }
            if (sickness != null)
                ModManager.ModParentField.ModifyParentField(
                sickness, "m_sicknessDuration", MathUtils.Max(message.SicknessDuration, 0f), typeof(ComponentSickness));
        }

        private bool ShouldDeferClientSleepWakeup()
        {
            if (IsHost || client?.IsConnected != true)
                return false;
            // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
            // CircuitSynchronizer.UpdateHostTimeAccelerationFromFence
            // Once bound, the circuit timeline is the stronger acceleration authority. It can
            // recover from a missing world-info falling edge, so an older outer flag must not
            // keep a ready client asleep forever.
            if (m_circuitSynchronizer != null)
                return m_circuitSynchronizer.IsHostTimeAccelerationActive ||
                    !m_circuitSynchronizer.IsClientBootstrapReady;
            return m_remoteTimeAccelerated;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
        // CircuitSynchronizer.UpdateHostTimeAccelerationFromFence
        internal void ConfirmRemoteTimeAccelerationEndedFromCircuitFence()
        {
            if (!IsHost)
                m_remoteTimeAccelerated = false;
        }

        // Source: CircuitSynchronizer.NotifyRemoteTimeAccelerationChanged
        // The reliable world edge or the fence-rate fallback can end the host sleep session.
        // Preserve that fact independently from a single player-health snapshot so a client
        // cannot remain asleep after the authoritative circuit boundary is ready.
        internal void MarkClientSleepWakeBoundaryPending()
        {
            if (!IsHost)
            {
                if (!m_clientSleepWakeBoundaryPending)
                    m_clientSleepWakeBoundaryPendingTime = Time.RealTime;
                m_clientSleepWakeBoundaryPending = true;
            }
        }

        // Source: CircuitSynchronizer.IsClientBootstrapReady
        // The host snapshot rebases directly to the final accelerated circuit boundary. Do not
        // replay the night's missing steps; wake the presentation only after that rebase is ready.
        private void CompletePendingClientSleepWakeups()
        {
            bool shouldDeferClientSleepWakeup = ShouldDeferClientSleepWakeup();
            if (shouldDeferClientSleepWakeup &&
                !ShouldForceClientSleepWakeupAfterBoundaryTimeout())
                return;

            if (m_clientSleepWakeBoundaryPending)
            {
                SubsystemPlayers players = GameManager.Project?
                    .FindSubsystem<SubsystemPlayers>(false);
                if (players == null)
                    return;

                bool foundLocalPlayer = false;
                foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
                {
                    if (player?.PlayerData == null ||
                        m_networkPlayerData.Values.Contains(player.PlayerData))
                        continue;
                    foundLocalPlayer = true;
                    if (player.ComponentSleep?.IsSleeping == true)
                        m_pendingClientSleepWakeups.Add(player.ComponentSleep);
                }
                if (!foundLocalPlayer)
                    return;
                m_clientSleepWakeBoundaryPending = false;
                m_clientSleepWakeBoundaryPendingTime = 0.0;
            }

            if (m_pendingClientSleepWakeups.Count == 0)
                return;
            foreach (ComponentSleep sleep in m_pendingClientSleepWakeups.ToArray())
            {
                if (sleep?.IsSleeping == true)
                    sleep.WakeUp();
            }
            m_pendingClientSleepWakeups.Clear();
        }

        private bool ShouldForceClientSleepWakeupAfterBoundaryTimeout()
        {
            if (!m_clientSleepWakeBoundaryPending ||
                m_clientSleepWakeBoundaryPendingTime <= 0.0)
                return false;
            if (m_circuitSynchronizer != null)
            {
                if (m_circuitSynchronizer.IsHostTimeAccelerationActive)
                    return false;
            }
            else if (m_remoteTimeAccelerated)
            {
                return false;
            }
            return Time.RealTime - m_clientSleepWakeBoundaryPendingTime >=
                ClientSleepWakeBoundaryTimeout;
        }

        public void HandleGameKickPlayerMessage(GameKickPlayerMessage msg, int sourceClientID)
        {
            // 仅 Host 可以处理踢人
            if (client.ClientID != 0) return;

            int targetID = msg.TargetClientID;
            Log.Information($"[ScMP] Kick request: ClientID {targetID}, reason: {msg.Reason}");

            // 释放玩家映射
            playerMappingManager.ReleasePlayerIndex(targetID);

            // 通过 Drt 框架断开玩家
            // Comms.Drt 内部管理连接，我们通过 RefuseJoinGame 已经可以阻止加入
            // Peer 层的 DisconnectPeer 需要 PeerData 引用
            Log.Information($"[ScMP] Player {targetID} kicked");
        }

        public void HandleGameWorldInfoMessage(GameWorldInfoMessage1 msg)
        {
            Project project = GameManager.Project;
            if (project == null || IsHost) return;
            // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.TriggerNetworkTick
            // A delayed latest-state datagram must not rewind fog intensity or restart its ramp.
            if (msg.ServerTick < m_lastRemoteWorldInfoTick) return;
            m_lastRemoteWorldInfoTick = msg.ServerTick;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            var timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
            // Only the host executes the accelerated sleep timeline. During the rising/active
            // phase the client retains its one-step clock; the reliable falling edge applies the
            // final host clock once before circuit rebase and visual wakeup.
            bool applyAuthoritativeTime = !msg.IsTimeAccelerated;
            // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.TimeOfDay
            // TimeOfDay depends on both values. Synchronizing only the offset allows the imported
            // client clock to remain minutes away from the host clock.
            if (applyAuthoritativeTime &&
                Math.Abs(gameInfo.TotalElapsedGameTime - msg.TotalElapsedGameTime) > 0.25)
            {
                ModManager.ModParentField.ModifyParentField(
                    gameInfo, "<TotalElapsedGameTime>k__BackingField",
                    msg.TotalElapsedGameTime, typeof(SubsystemGameInfo));
                ModManager.ModParentField.ModifyParentField(
                    gameInfo, "m_lastTotalElapsedGameTime",
                    (double?)msg.TotalElapsedGameTime, typeof(SubsystemGameInfo));
            }
            if (applyAuthoritativeTime)
            {
                gameInfo.WorldSettings.TimeOfDayMode = msg.CurrentTimeMode;
                if (Math.Abs(timeOfDay.TimeOfDayOffset - msg.TimeOfDayOffset) > 0.0001)
                    timeOfDay.TimeOfDayOffset = msg.TimeOfDayOffset;
            }
            // The latest world state is the clock authority. Refresh the electricity anchor now
            // so an older fence cannot restore pre-sleep or pre-button time on the next circuit step.
            if (applyAuthoritativeTime)
                m_circuitSynchronizer?.UpdateAuthoritativeWorldTime(msg.TotalElapsedGameTime,
                    msg.TimeOfDayOffset, msg.ServerTick, msg.WorldTimeRevision);
            // Source: CircuitSynchronizer.NotifyRemoteTimeAccelerationChanged
            // Notify on every accepted authoritative world snapshot. A fence-rate inference can
            // mark acceleration locally while m_remoteTimeAccelerated is still false; guarding
            // this call by the remote flag would then miss the authoritative wake edge.
            m_remoteTimeAccelerated = msg.IsTimeAccelerated;
            m_circuitSynchronizer?.NotifyRemoteTimeAccelerationChanged(
                msg.IsTimeAccelerated);
            m_remoteWeatherState = msg;
            m_remoteTerrainHeadSequence = Math.Max(
                m_remoteTerrainHeadSequence, msg.TerrainSequence);
            if (m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
                SuppressClientJoinWeatherPresentation(project);
            else
                ApplyRemoteWeatherState();
        }

        public bool TrySendWorldControlRequest(ComponentPlayer componentPlayer, WorldControlAction actions)
        {
            if (actions == WorldControlAction.None || IsHost || client?.IsConnected != true ||
                componentPlayer == null || m_networkPlayerData.Values.Contains(componentPlayer.PlayerData))
                return false;

            // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
            // CircuitSynchronizer.ShouldSuppressClientInput
            // A blocking recovery owns the circuit timeline. Preserve every accepted physical
            // click locally and release it in order after the authority fence is healthy again.
            if (ShouldDeferWorldControlRequest() || m_queuedWorldControlRequests.Count > 0)
            {
                if (m_pendingWorldControlRequests.Count + m_queuedWorldControlRequests.Count >=
                    MaximumPendingWorldControlRequests)
                {
                    DisplayWorldControlFeedback(componentPlayer,
                        "World control queue is full. Please wait for synchronization.");
                    return true;
                }
                m_queuedWorldControlRequests.Enqueue(new QueuedWorldControlRequest
                {
                    Actions = actions,
                    ComponentPlayer = componentPlayer
                });
                if (!m_worldControlQueueNoticeShown)
                {
                    m_worldControlQueueNoticeShown = true;
                    DisplayWorldControlFeedback(componentPlayer,
                        "World control queued. Waiting for synchronization.");
                }
                return true;
            }

            SendWorldControlRequestNow(componentPlayer, actions);
            return true;
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.TrySendWorldControlRequest
        private bool ShouldDeferWorldControlRequest() =>
            !IsHost && client?.IsConnected == true &&
            (m_clientTerrainRecoveryActive ||
            m_circuitSynchronizer?.ShouldSuppressClientInput == true);

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.TrySendWorldControlRequest
        private void SendWorldControlRequestNow(ComponentPlayer componentPlayer,
            WorldControlAction actions)
        {
            do
            {
                m_nextWorldControlRequestId = m_nextWorldControlRequestId == int.MaxValue
                    ? 1
                    : m_nextWorldControlRequestId + 1;
            }
            while (m_pendingWorldControlRequests.ContainsKey(m_nextWorldControlRequestId));

            int requestId = m_nextWorldControlRequestId;
            // Register before sending. A low-latency host can return the authoritative result from
            // another network callback before this game frame completes.
            m_pendingWorldControlRequests[requestId] = new PendingWorldControlRequest
            {
                Actions = actions,
                ComponentPlayer = componentPlayer,
                ExpirationTime = Time.RealTime + WorldControlResultTimeout
            };
            try
            {
                NetworkMessageSender.SendWorldControlRequest(requestId, actions);
            }
            catch (Exception ex)
            {
                PendingWorldControlRequest failed =
                    m_pendingWorldControlRequests[requestId];
                failed.TimedOut = true;
                failed.FailureMessage = "World control request failed: " + ex.Message;
                DrainWorldControlResults();
                return;
            }
        }

        private void HandleWorldControlRequest(WorldControlRequestMessage message, int sourceClientId)
        {
            Project project = GameManager.Project;
            if (!IsHost || sourceClientId <= 0 || message == null || message.RequestId <= 0 ||
                project == null ||
                !m_networkPlayerData.ContainsKey(sourceClientId))
                return;
            if (!m_hostWorldControlRequestStates.TryGetValue(sourceClientId,
                    out HostWorldControlRequestState state))
            {
                state = new HostWorldControlRequestState();
                m_hostWorldControlRequestStates.Add(sourceClientId, state);
            }

            // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
            // Each physical click owns a new RequestId. Only a retransmission of an already
            // completed id is deduplicated, and it receives the cached authoritative result.
            if (state.Completed.TryGetValue(message.RequestId,
                    out WorldControlResultMessage completed))
            {
                NetworkMessageSender.SendWorldControlResult(sourceClientId, completed);
                return;
            }
            if (message.RequestId == state.NextExpectedRequestId)
            {
                ProcessOrderedWorldControlRequests(sourceClientId, state, message);
                return;
            }

            long forwardDistance = message.RequestId - (long)state.NextExpectedRequestId;
            if (forwardDistance < 0) forwardDistance += int.MaxValue;
            if (forwardDistance > 0 && forwardDistance <= int.MaxValue / 2 &&
                state.Pending.Count < MaximumPendingWorldControlRequests &&
                !state.Pending.ContainsKey(message.RequestId))
                state.Pending.Add(message.RequestId, message);
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.HandleWorldControlRequest
        private void ProcessOrderedWorldControlRequests(int sourceClientId,
            HostWorldControlRequestState state, WorldControlRequestMessage first)
        {
            WorldControlRequestMessage message = first;
            while (message != null)
            {
                WorldControlResultMessage result = ExecuteWorldControlRequest(
                    message, sourceClientId);
                state.Completed[message.RequestId] = result;
                state.CompletedOrder.Enqueue(message.RequestId);
                while (state.CompletedOrder.Count > MaximumCachedWorldControlResults)
                    state.Completed.Remove(state.CompletedOrder.Dequeue());
                NetworkMessageSender.SendWorldControlResult(sourceClientId, result);
                if (result.Actions != WorldControlAction.None)
                    PublishServerAudit("world.control", sourceClientId,
                        "actions=" + result.Actions + " time=" + result.TimeResult +
                        " rain=" + result.PrecipitationStarted + " fog=" + result.FogStarted +
                        " lightning=" + result.LightningTriggered);

                state.NextExpectedRequestId = message.RequestId == int.MaxValue
                    ? 1
                    : message.RequestId + 1;
                if (!state.Pending.TryGetValue(state.NextExpectedRequestId, out message))
                    break;
                state.Pending.Remove(state.NextExpectedRequestId);
            }
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
        private WorldControlResultMessage ExecuteWorldControlRequest(
            WorldControlRequestMessage message, int sourceClientId)
        {
            Project project = GameManager.Project;
            WorldControlAction validActions = message.Actions &
                (WorldControlAction.TimeOfDay | WorldControlAction.Precipitation |
                WorldControlAction.Fog | WorldControlAction.Lightning);
            var result = new WorldControlResultMessage(message.RequestId, validActions);
            if (project == null) return result;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            if (gameInfo.WorldSettings.GameMode != GameMode.Creative)
            {
                result.Actions = WorldControlAction.None;
                return result;
            }

            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(true);
            SubsystemTimeOfDay timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(true);
            ComponentGui hostGui = project.FindSubsystem<SubsystemPlayers>(true).ComponentPlayers
                .FirstOrDefault(player => !m_networkPlayerData.Values.Contains(player.PlayerData))?.ComponentGui;
            if (validActions.HasFlag(WorldControlAction.Precipitation))
            {
                if (weather.IsPrecipitationStarted)
                {
                    weather.ManualPrecipitationEnd();
                    hostGui?.DisplaySmallMessage("Precipitation Off", Color.White, false, false);
                }
                else
                {
                    weather.ManualPrecipitationStart();
                    hostGui?.DisplaySmallMessage("Precipitation On", Color.White, false, false);
                }
                result.PrecipitationStarted = weather.IsPrecipitationStarted;
            }
            if (validActions.HasFlag(WorldControlAction.Fog))
            {
                if (weather.IsFogStarted)
                {
                    weather.ManualFogEnd();
                    hostGui?.DisplaySmallMessage("Fog Off", Color.White, false, false);
                }
                else
                {
                    weather.ManualFogStart();
                    hostGui?.DisplaySmallMessage("Fog On", Color.White, false, false);
                }
                result.FogStarted = weather.IsFogStarted;
            }
            if (validActions.HasFlag(WorldControlAction.TimeOfDay))
            {
                // Source: Mod/ScMultiplayer/Modules/Player/
                // ScMultiplayerHealthWorldControlHandlers.cs:HandleWorldControlResult
                // The directed result displays the native time label on the requesting client.
                // The host applies authority here without showing another player's feedback.
                float dawn = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middawn);
                float noon = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midday);
                float dusk = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middusk);
                float midnight = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midnight);
                float nearest = MathUtils.Min(dawn, noon, dusk, midnight);
                if (dawn == nearest)
                {
                    timeOfDay.TimeOfDayOffset += dawn;
                    result.TimeResult = WorldControlTimeResult.Dawn;
                }
                else if (noon == nearest)
                {
                    timeOfDay.TimeOfDayOffset += noon;
                    result.TimeResult = WorldControlTimeResult.Noon;
                }
                else if (dusk == nearest)
                {
                    timeOfDay.TimeOfDayOffset += dusk;
                    result.TimeResult = WorldControlTimeResult.Dusk;
                }
                else
                {
                    timeOfDay.TimeOfDayOffset += midnight;
                    result.TimeResult = WorldControlTimeResult.Midnight;
                }
            }
            if (validActions.HasFlag(WorldControlAction.Lightning) &&
                m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData sourcePlayer) &&
                sourcePlayer?.ComponentPlayer != null)
            {
                double previousStrikeTime = ModManager.ModParentField.GetParentField<double>(
                    sky, "m_lastLightningStrikeTime", typeof(SubsystemSky));
                ComponentCreatureModel model = sourcePlayer.ComponentPlayer.ComponentCreatureModel;
                Matrix eyeMatrix = Matrix.CreateFromQuaternion(model.EyeRotation);
                weather.ManualLightingStrike(model.EyePosition, eyeMatrix.Forward);
                double currentStrikeTime = ModManager.ModParentField.GetParentField<double>(
                    sky, "m_lastLightningStrikeTime", typeof(SubsystemSky));
                result.LightningTriggered = currentStrikeTime > previousStrikeTime;
            }
            SendGameWorldInfoMessage();
            return result;
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        private void HandleWorldControlResult(WorldControlResultMessage message,
            int sourceClientId)
        {
            PendingWorldControlRequest pending = null;
            bool pendingFound = message != null &&
                m_pendingWorldControlRequests.TryGetValue(message.RequestId, out pending);
            if (IsHost || sourceClientId != 0 || message == null || !pendingFound)
                return;
            // A result is self-identifying. Waiting for an earlier button result can suppress the
            // feedback for a later accepted click even though its world mutation already happened.
            m_pendingWorldControlRequests.Remove(message.RequestId);
            DisplayWorldControlResult(message, pending);
        }

        // Source: Mod/ScMultiplayer/Message/WorldControlResultMessage.cs:
        // WorldControlResultMessage.RequestId
        private void DrainWorldControlResults()
        {
            foreach (KeyValuePair<int, PendingWorldControlRequest> item in
                m_pendingWorldControlRequests.Where(entry => entry.Value.TimedOut).ToArray())
            {
                m_pendingWorldControlRequests.Remove(item.Key);
                DisplayWorldControlFeedback(item.Value.ComponentPlayer,
                    item.Value.FailureMessage ??
                    "Host did not confirm the world control request.");
            }
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        private void DisplayWorldControlResult(WorldControlResultMessage message,
            PendingWorldControlRequest pending)
        {

            WorldControlAction confirmed = message.Actions & pending.Actions;
            var feedback = new List<string>();
            if (confirmed.HasFlag(WorldControlAction.Precipitation))
                feedback.Add(message.PrecipitationStarted
                    ? "Precipitation On"
                    : "Precipitation Off");
            if (confirmed.HasFlag(WorldControlAction.Fog))
                feedback.Add(message.FogStarted ? "Fog On" : "Fog Off");
            if (confirmed.HasFlag(WorldControlAction.TimeOfDay) &&
                message.TimeResult != WorldControlTimeResult.None)
            {
                // Source: Mod/ScMultiplayer/Message/WorldControlResultMessage.cs:
                // WorldControlTimeResult
                // Obfuscar can rename enum member identifiers. UI text must not depend on
                // Enum.ToString(), otherwise a Release build displays the renamed identifier.
                string timeText = message.TimeResult switch
                {
                    WorldControlTimeResult.Dawn => "Dawn",
                    WorldControlTimeResult.Noon => "Noon",
                    WorldControlTimeResult.Dusk => "Dusk",
                    WorldControlTimeResult.Midnight => "Midnight",
                    _ => null
                };
                if (timeText != null) feedback.Add(timeText);
            }
            if (confirmed.HasFlag(WorldControlAction.Lightning))
                feedback.Add(message.LightningTriggered ? "Lightning" : "Lightning unavailable");
            if (feedback.Count == 0) return;

            DisplayWorldControlFeedback(pending.ComponentPlayer,
                string.Join("\r\n", feedback));
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        private void DisplayWorldControlFeedback(ComponentPlayer preferredPlayer,
            string message)
        {
            ComponentPlayer localPlayer = preferredPlayer;
            if (localPlayer?.ComponentGui == null)
                localPlayer = m_localReplacementPlayerData?.ComponentPlayer;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (localPlayer?.ComponentGui == null)
                localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            localPlayer?.ComponentGui.DisplaySmallMessage(
                message, Color.White, false, false);
        }

        private void AdvanceWorldControlFeedbackRequestId()
        {
            m_nextWorldControlFeedbackRequestId =
                m_nextWorldControlFeedbackRequestId == int.MaxValue
                    ? 1
                    : m_nextWorldControlFeedbackRequestId + 1;
        }

        // Source: Engine/Time.cs:Time.RealTime
        private void UpdatePendingWorldControlRequests()
        {
            FlushQueuedWorldControlRequest();
            if (m_pendingWorldControlRequests.Count == 0) return;
            double now = Time.RealTime;
            foreach (PendingWorldControlRequest pending in m_pendingWorldControlRequests
                .Where(item => item.Value.ExpirationTime <= now)
                .Select(item => item.Value).ToArray())
                pending.TimedOut = true;
            DrainWorldControlResults();
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.TrySendWorldControlRequest
        private void FlushQueuedWorldControlRequest()
        {
            if (m_queuedWorldControlRequests.Count == 0)
            {
                m_worldControlQueueNoticeShown = false;
                return;
            }
            if (IsHost || client?.IsConnected != true)
            {
                m_queuedWorldControlRequests.Clear();
                m_worldControlQueueNoticeShown = false;
                return;
            }
            if (ShouldDeferWorldControlRequest()) return;

            QueuedWorldControlRequest queued = m_queuedWorldControlRequests.Dequeue();
            SendWorldControlRequestNow(queued.ComponentPlayer, queued.Actions);
            if (m_queuedWorldControlRequests.Count == 0)
                m_worldControlQueueNoticeShown = false;
        }

        public void ApplyRemoteWeatherState()
        {
            GameWorldInfoMessage1 msg = m_remoteWeatherState;
            Project project = GameManager.Project;
            if (msg == null || project == null || IsHost) return;
            if (m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
            {
                SuppressClientJoinWeatherPresentation(project);
                return;
            }
            // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdatePrecipitation
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(true);
            if (weather.IsPrecipitationStarted != msg.IsPrecipitationStarted)
            {
                if (msg.IsPrecipitationStarted) weather.ManualPrecipitationStart();
                else weather.ManualPrecipitationEnd();
            }
            if (weather.IsFogStarted != msg.IsFogStarted)
                ConfigureRemoteFogSchedule(weather, msg.IsFogStarted);
            ModManager.ModParentField.ModifyParentField(
                weather, "<PrecipitationIntensity>k__BackingField", msg.PrecipitationIntensity, typeof(SubsystemWeather));
            if (!m_remoteFogPresentationInitialized)
            {
                ModManager.ModParentField.ModifyParentField(
                    weather, "<FogProgress>k__BackingField", msg.FogProgress, typeof(SubsystemWeather));
                ModManager.ModParentField.ModifyParentField(
                    weather, "<FogIntensity>k__BackingField", msg.FogIntensity, typeof(SubsystemWeather));
                m_remoteFogPresentationInitialized = true;
            }
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogSeed>k__BackingField", msg.FogSeed, typeof(SubsystemWeather));
            SuppressClientRandomLightning(project);

            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(true);
            if (msg.HasLightningStrike && !m_remoteLightningActive)
            {
                m_remoteLightningActive = true;
                ApplyRemoteLightningVisual(sky, msg.LightningStrikePosition);
            }
            else if (!msg.HasLightningStrike)
            {
                ClearRemoteLightningVisual(sky);
                m_remoteLightningActive = false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.Update
        // Joining clients have not applied the host terrain/circuit boundary yet. Keep weather
        // presentation dormant so rain, snow, fog and lightning work does not compete with apply;
        // the authoritative state is restored as soon as ReadyToPlay clears the barrier.
        private void SuppressClientJoinWeatherPresentation(Project project)
        {
            if (IsHost || project == null ||
                m_worldTransferRegistry.PendingWorldReadyTransferId <= 0)
                return;
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather == null) return;
            ModManager.ModParentField.ModifyParentField(
                weather, "<PrecipitationIntensity>k__BackingField", 0f,
                typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogProgress>k__BackingField", 0f,
                typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogIntensity>k__BackingField", 0f,
                typeof(SubsystemWeather));
            SuppressClientRandomLightning(project);
            if (m_remoteLightningActive)
            {
                ClearRemoteLightningVisual(project.FindSubsystem<SubsystemSky>(false));
                m_remoteLightningActive = false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdateFog
        // Disable the client's independent random fog schedule while retaining the original
        // SubsystemWeather renderer and all weather effects.
        private static void ConfigureRemoteFogSchedule(SubsystemWeather weather, bool isStarted)
        {
            SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (weather == null || gameInfo == null) return;
            double startTime = isStarted
                ? gameInfo.TotalElapsedGameTime
                : double.MaxValue;
            ModManager.ModParentField.ModifyParentField(
                weather, "m_fogStartTime", startTime, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "m_fogEndTime", double.MaxValue, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "m_fogRampTime", float.MaxValue, typeof(SubsystemWeather));
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdateFog
        // World info arrives at 2Hz. Interpolate its authority every rendered frame instead of
        // alternating between the local weather ramp and a hard network correction.
        private void UpdateRemoteFogPresentation(float dt)
        {
            GameWorldInfoMessage1 msg = m_remoteWeatherState;
            Project project = GameManager.Project;
            if (msg == null || project == null || IsHost) return;
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather == null) return;
            if (weather.IsFogStarted != msg.IsFogStarted)
                ConfigureRemoteFogSchedule(weather, msg.IsFogStarted);
            float step = MathUtils.Clamp(dt, 0f, 0.05f);
            float blend = 1f - (float)Math.Exp(-8f * step);
            float fogProgress = MathUtils.Lerp(weather.FogProgress, msg.FogProgress, blend);
            float fogIntensity = MathUtils.Lerp(weather.FogIntensity, msg.FogIntensity, blend);
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogProgress>k__BackingField", fogProgress, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogIntensity>k__BackingField", fogIntensity, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogSeed>k__BackingField", msg.FogSeed, typeof(SubsystemWeather));
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdateLightning
        private void SuppressClientRandomLightning(Project project)
        {
            if (IsHost || project == null) return;
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    weather, "m_lightningIntensity", 0f, typeof(SubsystemWeather));
            }
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike
        // The original method also damages creatures, starts fires and creates a random explosion.
        // A client replica must only render the host event; terrain effects arrive separately.
        private void ApplyRemoteLightningVisual(SubsystemSky sky, Vector3 position)
        {
            if (sky == null) return;
            SubsystemTime subsystemTime = GameManager.Project?.FindSubsystem<SubsystemTime>(false);
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lastLightningStrikeTime", subsystemTime?.GameTime ?? 0.0,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikePosition", (Vector3?)position,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikeBrightness", 1f, typeof(SubsystemSky));
            // Source: ScMultiplayer.SendWorldControlRequestNow
            // World-control clients do not predict the native strike locally. Every client,
            // including the requester, therefore plays the authoritative rising-edge thunder.
            PlayRemoteThunder(position);
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike
        // Reproduce only the listener-distance audio branch. Damage, fire and explosions remain
        // host-authoritative and arrive through their existing synchronization paths.
        private void PlayRemoteThunder(Vector3 position)
        {
            SubsystemAudio audio = GameManager.Project?.FindSubsystem<SubsystemAudio>(false);
            if (audio == null) return;
            float distance = float.MaxValue;
            foreach (Vector3 listenerPosition in audio.ListenerPositions)
            {
                distance = MathUtils.Min(distance, Vector2.Distance(
                    new Vector2(listenerPosition.X, listenerPosition.Z),
                    new Vector2(position.X, position.Z)));
            }
            if (distance >= 200f) return;
            float pitch = m_audioEventRandom.Float(-0.2f, 0.2f);
            float delay = audio.CalculateDelay(distance);
            if (distance < 40f)
                audio.PlayRandomSound("Audio/ThunderNear", 1f, pitch, 0f, delay);
            else
                audio.PlayRandomSound("Audio/ThunderFar", 0.8f, pitch, 0f, delay);
        }

        private void ClearRemoteLightningVisual(SubsystemSky sky)
        {
            if (sky == null) return;
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikePosition", (Vector3?)null,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikeBrightness", 0f, typeof(SubsystemSky));
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
    }
}

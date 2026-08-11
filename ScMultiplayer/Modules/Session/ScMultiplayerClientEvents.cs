using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using ScMultiplayer.Core;
using ScMultiplayer.Modules.Join;
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

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
	private void Client_GameCreated(GameCreatedData obj)
	{
		m_pendingLocalCreateDescription = null;
		m_pendingLocalCreateAddress = null;
		m_localCreateAttempts = 0;
		Log.Information($"[ScMP] GameCreated, ClientID={client.ClientID}, Creator={obj.CreatorAddress}");
		IsHost = true;
		m_controlUnit?.Context.Connections.Reset();
		m_controlUnit?.Context.Session.EnterRoom(client.ClientID, client.GameID, isHost: true, client.Address?.ToString(), SuPlayScreen.WorldDataName);
		SetServerDiscoveryEnabled(enabled: false);
		m_localLeaveInProgress = false;
		m_shouldCreateHostAvatar = false;
		ResetTransientNetworkState();
		EnterNetworkHostSession(GameManager.Project);
		m_sessionRandomSeed = Guid.NewGuid().GetHashCode();
		if (m_sessionRandomSeed == 0)
		{
			m_sessionRandomSeed = 1;
		}
		playerMappingManager.AssignPlayerIndex(client.ClientID);
		m_controlUnit?.Context.Connections.Register(client.ClientID, playerMappingManager.GetPlayerIndex(client.ClientID), client.Address?.ToString(), isHost: true, PlayerConnectionPhase.Ready, Time.RealTime);
		connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Playing);
		Dispatcher.Dispatch(delegate
		{
			FinishCreateRoomFeedback(success: true, $"Room created (ID {client.GameID}).");
		});
	}

	private void FinishCreateRoomFeedback(bool success, string message)
	{
		bool wasPending = m_createRoomPending;
		m_createRoomPending = false;
		if (success)
		{
			return;
		}
		if (!success)
		{
			if (wasPending)
			{
				DialogsManager.ShowDialog(null, new MessageDialog("Create Room", message ?? "Room creation failed.", "OK", null, null));
			}
		}
		else if (GameManager.Project != null)
		{
			SubsystemPlayers subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(throwOnError: false);
			((subsystemPlayers != null) ? subsystemPlayers.ComponentPlayers.FirstOrDefault((ComponentPlayer player) => !m_networkPlayerData.Values.Contains(player.PlayerData)) : null)?.ComponentGui.DisplaySmallMessage(message, Color.Green, blinking: true, playNotificationSound: true);
		}
	}

	private void Client_GameJoined(GameJoinedData obj)
	{
		bool reconnectPending = m_reconnectPending;
		Log.Information($"[ScMP] GameJoined, Step={obj.Step}, ClientID={client.ClientID}");
		IsHost = false;
		m_controlUnit?.Context.Connections.Reset();
		m_controlUnit?.Context.Session.EnterRoom(client.ClientID, client.GameID, isHost: false, client.Peer?.ConnectedTo?.Address?.ToString(), m_activeJoinRequest?.WorldInfo?.Name);
		if (GameManager.Project != null)
		{
			m_clientWorldRefreshProject = GameManager.Project;
		}
		SetServerDiscoveryEnabled(enabled: false);
		m_hostDisconnectHandled = false;
		m_localLeaveInProgress = false;
		m_isLoadingDownloadedWorld = true;
		ResetTransientNetworkState();
		EnterNetworkClientSession(GameManager.Project);
		m_joinAwaitingWorldProgress = true;
		RecordClientJoinProgress();
		m_nextWorldTransferManifestRequestTime = Time.RealTime + 0.75;
		NetworkMessageSender.SendPakWorldRepairRequest(new GamePakWorldRepairRequestMessage
		{
			TransferId = 0,
			RequestManifest = true
		});
		m_reconnectRequested = false;
		m_reconnectPending = false;
		m_reconnectAttempts = 0;
		m_nextReconnectAttemptTime = 0.0;
		m_reconnectAttemptDeadline = 0.0;
		m_pendingJoinRequest = null;
		playerMappingManager.AssignPlayerIndex(client.ClientID);
		m_controlUnit?.Context.Connections.Register(client.ClientID, playerMappingManager.GetPlayerIndex(client.ClientID), client.Address?.ToString(), isHost: false, PlayerConnectionPhase.Joining, Time.RealTime);
		downloadSM.TransitionTo(WorldDownloadStateMachine.DownloadState.Requesting);
		Dispatcher.Dispatch(delegate
		{
			if (m_joinRoomBusyDialog != null)
			{
				m_joinRoomBusyDialog.SmallMessage = "Connected. Downloading the host world...";
			}
		});
		if (reconnectPending)
		{
			Log.Information("[ScMP] Host reconnect succeeded; refreshing authoritative world state");
		}
	}

	public static string GetLocalPlayerName()
	{
		string name = UserManager.ActiveUser?.DisplayName;
		if (!string.IsNullOrWhiteSpace(name))
		{
			return name;
		}
		Project project = GameManager.Project;
		object obj;
		if (project == null)
		{
			obj = null;
		}
		else
		{
			SubsystemPlayers subsystemPlayers = project.FindSubsystem<SubsystemPlayers>(throwOnError: false);
			obj = ((subsystemPlayers != null) ? subsystemPlayers.PlayersData.FirstOrDefault() : null);
		}
		PlayerData player = (PlayerData)obj;
		if (string.IsNullOrWhiteSpace(player?.Name))
		{
			return "Player";
		}
		return player.Name;
	}

	public static string GetLocalPlayerIdentity()
	{
		return UserManager.ActiveUser?.UniqueId ?? string.Empty;
	}

	public static string GetServiceDiscoveryHost(IPEndPoint endpoint)
	{
		return currentInstance?.m_remoteServerDirectory?.GetHostName(endpoint);
	}

	internal static PersonalServerRecord GetPersonalServer(IPEndPoint endpoint)
	{
		return currentInstance?.m_remoteServerDirectory?.GetPersonalServer(endpoint);
	}

	private void Client_GameDescriptionRequest(GameDescriptionRequestData obj)
	{
		if (LastGameDescription != null && LastGameDescription.Length != 0)
		{
			try
			{
				client.SendGameDescription(LastGameDescription);
			}
			catch (Exception ex)
			{
				Log.Error("[ScMP] SendGameDescription failed: " + ex.Message);
			}
		}
	}

	private void Client_ConnectRefused(ConnectRefusedData obj)
	{
		Log.Information("[ScMP] Connect refused: " + obj.Reason);
		m_clientWorldRefreshProject = null;
		string reason = obj.Reason;
		if (reason != null && reason.StartsWith("SCMP_PROTOCOL_MISMATCH", StringComparison.Ordinal))
		{
			m_reconnectPending = false;
			m_reconnectRequested = false;
			m_reconnectAttempts = 0;
			m_reconnectAttemptDeadline = 0.0;
			m_nextReconnectAttemptTime = 0.0;
			m_isLoadingDownloadedWorld = false;
			m_pendingJoinRequest = null;
			m_activeJoinRequest = null;
			connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Disconnected);
			Dispatcher.Dispatch(delegate
			{
				HideJoinRoomBusyDialog();
				DialogsManager.ShowDialog(null, new MessageDialog("Join Room", obj.Reason + "\nInstall the same ScMultiplayer package on all devices.", "OK", null, null));
			});
			return;
		}
		if (m_reconnectPending && obj.Reason != "SCMP_PROFILE_REQUIRED")
		{
			m_reconnectRequested = false;
			m_reconnectPending = false;
			m_reconnectAttempts = 0;
			m_reconnectAttemptDeadline = 0.0;
			m_nextReconnectAttemptTime = 0.0;
			Log.Information("[ScMP] Reconnect refusal is final; stopping retry loop");
		}
		if (obj.Reason == "SCMP_PROFILE_REQUIRED")
		{
			m_reconnectRequested = false;
			m_reconnectPending = false;
			m_reconnectAttempts = 0;
			m_reconnectAttemptDeadline = 0.0;
			m_nextReconnectAttemptTime = 0.0;
		}
		m_isLoadingDownloadedWorld = false;
		connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Disconnected);
		Dispatcher.Dispatch(delegate
		{
			FinishCreateRoomFeedback(success: false, obj.Reason);
		});
		if (obj.Reason == "SCMP_PROFILE_REQUIRED" && m_pendingJoinRequest != null)
		{
			Dispatcher.Dispatch(delegate
			{
				HideJoinRoomBusyDialog();
				ScreensManager.SwitchScreen("ScMultiplayerPlayer", (Action<string, PlayerClass, string>)delegate(string name, PlayerClass playerClass, string skinName)
				{
					SubmitPendingJoin(name, playerClass, skinName, hasPlayerProfile: true);
				});
			});
		}
		else
		{
			Dispatcher.Dispatch(delegate
			{
				HideJoinRoomBusyDialog();
				DialogsManager.ShowDialog(null, new MessageDialog("Join Room", obj.Reason ?? "The host refused the connection.", "OK", null, null));
			});
			m_pendingJoinRequest = null;
			m_activeJoinRequest = null;
		}
	}

	private void Client_ConnectTimedOut(ConnectTimedOutData obj)
	{
		string address = obj.Address?.ToString() ?? "host";
		if (m_reconnectPending)
		{
			m_reconnectAttemptDeadline = 0.0;
			m_nextReconnectAttemptTime = Math.Max(m_nextReconnectAttemptTime, Time.RealTime + 1.0);
			Log.Information("[ScMP] Reconnect attempt to " + address + " timed out; retry remains scheduled");
			return;
		}
		Log.Error("[ScMP] Join request to " + address + " timed out");
		m_clientWorldRefreshProject = null;
		m_isLoadingDownloadedWorld = false;
		m_pendingJoinRequest = null;
		m_activeJoinRequest = null;
		connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Disconnected);
		Dispatcher.Dispatch(delegate
		{
			HideJoinRoomBusyDialog();
			DialogsManager.ShowDialog(null, new MessageDialog("Join Room", "The host did not complete the join request in time.", "OK", null, null));
		});
	}

	private void Client_Disconnected(Client sourceClient, DisconnectedData obj)
	{
		if (sourceClient != client)
		{
			return;
		}
		m_controlUnit?.Context.Connections.MarkDisconnected(IsHost ? sourceClient.ClientID : 0, Time.RealTime);
		if (!IsHost && !m_localLeaveInProgress && !m_hostDisconnectHandled && !m_reconnectRequested && !m_reconnectPending && (m_pendingJoinRequest == null || m_activeJoinHasPlayerProfile) && (m_activeJoinRequest?.WorldInfo != null || !string.IsNullOrEmpty(m_downloadedWorldDirectory) || m_isLoadingDownloadedWorld || m_shouldCreateHostAvatar))
		{
			if (m_activeJoinRequest?.WorldInfo != null)
			{
				m_reconnectRequested = true;
				Log.Warning("[ScMP] Remote client disconnected; reconnect requested");
			}
			else
			{
				Log.Warning("[ScMP] Remote client disconnected; leaving downloaded world");
				HandleHostDisconnected();
			}
		}
	}

	private void DetectUnexpectedClientDisconnect()
	{
		if (IsHost || m_localLeaveInProgress || m_hostDisconnectHandled || m_reconnectRequested || m_reconnectPending)
		{
			return;
		}
		Client obj = client;
		if (obj != null && obj.IsConnected)
		{
			return;
		}
		Client obj2 = client;
		if ((obj2 == null || !obj2.IsConnecting) && (m_pendingJoinRequest == null || m_activeJoinHasPlayerProfile) && (m_activeJoinRequest?.WorldInfo != null || !string.IsNullOrEmpty(m_downloadedWorldDirectory) || m_isLoadingDownloadedWorld || m_shouldCreateHostAvatar))
		{
			if (m_activeJoinRequest?.WorldInfo != null)
			{
				m_reconnectRequested = true;
				Log.Warning("[ScMP] Remote client connection is inactive; reconnect requested");
			}
			else
			{
				Log.Warning("[ScMP] Remote client connection is no longer active; leaving downloaded world");
				HandleHostDisconnected();
			}
		}
	}

	private void Client_GameStateRequest(GameStateRequestData obj)
	{
		client.SendState(client.Step, Message.WriteWithSender(new ChatMessage("StateSync", string.Empty, "OK"), client.Address));
	}

	private static bool IsTransientNetworkSocketError(Exception error)
	{
		if (!(error is SocketException { SocketErrorCode: var socketErrorCode }))
		{
			return false;
		}
		if ((uint)(socketErrorCode - 10050) <= 4u || (uint)(socketErrorCode - 10064) <= 1u)
		{
			return true;
		}
		return false;
	}

	private void Client_Error(Client sourceClient, Exception obj)
	{
		if (sourceClient != client)
		{
			return;
		}
		Log.Error("[ScMP] Client error: " + obj.Message);
		bool activeClientSession = !IsHost && !m_localLeaveInProgress && (m_isLoadingDownloadedWorld || !string.IsNullOrEmpty(m_downloadedWorldDirectory) || m_shouldCreateHostAvatar);
		if (activeClientSession && IsTransientNetworkSocketError(obj))
		{
			Log.Warning("[ScMP] Transient client network error; waiting for transport recovery: " + obj.Message);
			return;
		}
		if (activeClientSession && obj is KeepAliveTimeoutException && m_activeJoinRequest?.WorldInfo != null)
		{
			m_reconnectRequested = true;
			return;
		}
		Dispatcher.Dispatch(delegate
		{
			HideJoinRoomBusyDialog();
			FinishCreateRoomFeedback(success: false, obj.Message);
		});
		if (activeClientSession)
		{
			HandleHostDisconnected();
		}
	}

	private void HandleHostDisconnected()
	{
		if (IsHost || m_hostDisconnectHandled)
		{
			return;
		}
		m_hostDisconnectHandled = true;
		m_controlUnit?.Context.Connections.MarkDisconnected(0, Time.RealTime);
		m_reconnectRequested = false;
		m_reconnectPending = false;
		m_reconnectAttemptDeadline = 0.0;
		m_joinAwaitingWorldProgress = false;
		m_pendingJoinRequest = null;
		m_activeJoinRequest = null;
		m_isLoadingDownloadedWorld = false;
		m_shouldCreateHostAvatar = false;
		m_clientWorldRefreshProject = null;
		connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Disconnected);
		HideJoinRoomBusyDialog();
		Client obj = client;
		if (obj != null && obj.IsConnected)
		{
			try
			{
				client.LeaveGame();
			}
			catch (Exception ex)
			{
				Log.Error("[ScMP] Failed to leave disconnected host: " + ex.Message);
			}
		}
		QueueEndOfFrameAction(delegate
		{
			string downloadedWorldDirectory = m_downloadedWorldDirectory;
			if (GameManager.Project != null)
			{
				GameManager.DisposeProject();
			}
			if (!string.IsNullOrEmpty(downloadedWorldDirectory))
			{
				WorldsManager.DeleteWorld(downloadedWorldDirectory);
				WorldsManager.UpdateWorldsList();
				m_downloadedWorldDirectory = null;
			}
			m_networkPlayerData.Clear();
			m_pendingNetworkPlayers.Clear();
			RemotePlayers.Clear();
			ScreensManager.SwitchScreen("Play");
			HideJoinRoomBusyDialog();
		});
	}

	public void NotifyPlayerComponentDisposing(PlayerData playerData)
	{
		if (playerData != null && !IsHost && !m_hostDisconnectHandled && !m_localLeaveInProgress && !m_replacingLocalPlayerData && !m_networkPlayerData.Values.Contains(playerData) && GameManager.Project == null)
		{
			BeginLocalGameLeave();
			ResetTransientNetworkState();
		}
	}

	private void BeginLocalGameLeave()
	{
		if (m_localLeaveInProgress)
		{
			return;
		}
		m_clientWorldRefreshProject = null;
		HideJoinRoomBusyDialog();
		Client obj = client;
		if (obj == null || !obj.IsConnected)
		{
			return;
		}
		m_localLeaveInProgress = true;
		m_reconnectRequested = false;
		m_reconnectPending = false;
		m_reconnectAttempts = 0;
		m_activeJoinRequest = null;
		m_shouldCreateHostAvatar = false;
		m_isLoadingDownloadedWorld = false;
		try
		{
			NetworkMessageSender.BroadcastPlayerLeave(new PlayerActionMessage(PlayerActionType.LeaveRequest, client.ClientID, 0, default(Ray3)));
		}
		catch (Exception ex2)
		{
			Log.Error("[ScMP] Failed to broadcast leave: " + ex2.Message);
		}
		try
		{
			client.LeaveGame();
		}
		catch (Exception ex)
		{
			Log.Error("[ScMP] Failed to leave game: " + ex.Message);
		}
	}

	private void ResetTransientNetworkState()
	{
		// Source: GameEntitySystem/Project.cs:Project.Dispose
		// Do this before clearing queues so injected subsystems cannot observe the old online role
		// while the next ordinary Project is being constructed.
		ExitNetworkSession();
		DetachHostSleepWakeHandlers();
		m_circuitSynchronizer?.Reset();
		m_worldObjectSynchronizer?.Reset();
		Interlocked.Increment(ref m_worldTransferGeneration);
		WorldTransferChunkSendWork result;
        while (m_worldTransferSendQueue.TryDequeue(out result))
        {
            Interlocked.Decrement(ref m_worldTransferQueuedWorkCount);
            if (result != null)
                ReleaseReliableRelayPackets(result.TargetClientId,
                    result.ReservedPackets);
        }
		QueuedFrameAction result2;
		while (m_worldTransferActions.TryDequeue(out result2))
		{
		}
		QueuedFrameAction result3;
		while (m_priorityInputActions.TryDequeue(out result3))
		{
		}
		QueuedTerrainChunkSync result4;
		while (m_terrainChunkSyncActions.TryDequeue(out result4))
		{
		}
		m_networkPlayerData.Clear();
		m_reservedNetworkPlayerIndices.Clear();
		lock (m_creatingNetworkPlayers)
		{
			m_creatingNetworkPlayers.Clear();
		}
		m_pendingNetworkPlayers.Clear();
		m_pendingNetworkPlayerIdentities.Clear();
		m_networkPlayerInputs.Clear();
		m_departedRemoteClientIds.Clear();
		m_clientRecordKeys.Clear();
		m_containerStates.Clear();
		m_pendingContainerTransactions.Clear();
		m_processedContainerTransactions.Clear();
		m_openContainerPanel = null;
		m_openContainer = null;
		m_baselineRequestedContainerKey = null;
		m_recentChatMessages.Clear();
		m_recentChatMessageIds.Clear();
		m_hostJoinRequests.Clear();
		if (m_activeJoinDecisionDialog != null && DialogsManager.Dialogs.Contains(m_activeJoinDecisionDialog))
		{
			DialogsManager.HideDialog(m_activeJoinDecisionDialog);
		}
		m_activeJoinDecisionDialog = null;
		m_activeJoinDecisionClientId = -1;
		m_lastSentAuthoritativePlayerStates.Clear();
		m_nextSleepHealthSendTimes.Clear();
		m_nextClientSleepRequestSequence = 0;
		m_pendingClientSleepRequestSequence = 0;
		m_authoritativePlayerStateSequences.Clear();
		m_lastReceivedAuthoritativePlayerStateSequences.Clear();
		m_lastAuthoritativeLocalWholeLevel = -1;
		m_lastSentInventoryValues.Clear();
		m_lastSentInventoryCounts.Clear();
		m_equipmentAuthorityRevisions.Clear();
		m_lastClientEquipmentRevisions.Clear();
		m_lastReceivedEquipmentRevisions.Clear();
		m_lastEquipmentSnapshots.Clear();
		m_equipmentSynchronizedClients.Clear();
		m_localEquipmentRevision = 0;
		m_lastEditableDataRequestIds.Clear();
		m_lastEditableDataRevisions.Clear();
		m_lastEditableDataPayloads.Clear();
		m_localEditableDataRequestId = 0;
		m_editableDataRevision = 0;
		m_pendingFurnitureBuild = null;
		m_nextFurnitureBuildRequestId = 0;
		m_lastFurnitureBuildRequestIds.Clear();
		m_hostKnockbackHealthCache.Clear();
		m_hostRemoteKnockbackUntil.Clear();
		m_hostKnockbackSequences.Clear();
		RemotePlayers.Clear();
        m_hostAnimalIds.Clear();
        m_hostMountIds.Clear();
        m_hostMountJoinSnapshotClients.Clear();
        m_nextMountId = MountEntityIdStart;
        m_hostAnimals.Clear();
		m_hostAnimalSync.Clear();
        m_remoteAnimals.Clear();
        m_remoteMounts.Clear();
        m_remoteMountTemplates.Clear();
        m_remoteMountSync.Clear();
		m_remoteAnimalTemplates.Clear();
		m_remoteAnimalSync.Clear();
		m_loggedRemoteAnimalFailures.Clear();
		m_lastFullAnimalSnapshotTick = 0;
		m_hostPickableIds.Clear();
		m_pendingHostPickableSnapshots.Clear();
		m_remotePickables.Clear();
		m_remotePickableRecords.Clear();
		m_remotePickableStates.Clear();
		m_pendingPickablePickups.Clear();
		m_pendingPickableAcquireRequests.Clear();
		m_processedPickableAcquireRequests.Clear();
		m_authoritativePickableAcquireIds.Clear();
		m_nextPickableAcquireRequestId = 0;
		m_nextPickableAcquireScanTime = 0.0;
		m_applyingNetworkPickable = false;
		m_lastAuthoritativeLocalInventoryTick = 0;
		m_lastLocalInventoryValues = Array.Empty<int>();
		m_lastLocalInventoryCounts = Array.Empty<int>();
		m_pendingLocalDropValue = 0;
		m_pendingLocalDropCount = 0;
		m_pendingLocalDropPosition = Vector3.Zero;
		m_pendingLocalDropPredictionUntil = 0.0;
		m_hostProjectileIds.Clear();
		m_hostProjectileReleaseCompensationSteps.Clear();
		m_remoteProjectiles.Clear();
		m_clientPredictedProjectiles.Clear();
		m_displayedProjectileHits.Clear();
		m_nextProjectileId = 1;
		lock (m_terrainJournalLock)
		{
			m_hostTerrainJournal.Clear();
			m_hostTerrainChunkRevisions.Clear();
			m_hostTerrainInterestChunks.Clear();
			m_hostTerrainChunkSubscribers.Clear();
			m_hostTerrainInterestCenters.Clear();
			m_hostTerrainInterestRadii.Clear();
			m_hostTerrainReportedInterestRadii.Clear();
			m_terrainCheckpoint.Clear();
			m_terrainCheckpointByChunk.Clear();
			m_pendingTerrainChanges.Clear();
			m_pendingFluidSettlements.Clear();
			m_hostTerrainSequence = 0L;
		}
		ResetHostTerrainSyncStateStorage();
		m_hostTerrainRecoveryTargets.Clear();
		m_pendingTerrainSequenceBaseline = 0L;
		DetachClientTerrainChunkSyncUpdater();
		ResetClientTerrainChunkSyncState();
		m_hostTerrainPlaceFallbacks.Clear();
		m_clientTerrainRecoveryActive = false;
		m_clientTerrainRecoveryPending = false;
		m_clientTerrainRecoveryRequestInFlight = false;
		m_clientSuspensionRequested = false;
		m_clientTerrainRecoveryTarget = -1L;
		m_clientTerrainRecoveryAcknowledged = -1L;
		m_clientTerrainRecoveryReady = -1L;
		m_lastObservedClientTerrainSequence = -1L;
		m_clientTerrainGapDetectedTime = 0.0;
		m_clientGameplayScreenObserved = false;
		m_wasClientGameScreenActive = false;
		m_clientWindowDeactivated = false;
		m_pendingTerrainPredictions.Clear();
		m_pendingTerrainPredictionCells.Clear();
		m_processedTerrainDigRequests.Clear();
		m_localTerrainDigIntents.Clear();
		m_localTerrainUsePredictions.Clear();
		m_pendingTerrainPlacePredictions.Clear();
		m_pendingTerrainPlacePredictionCells.Clear();
		m_localCollapsingPlacePredictions.Clear();
		m_recentLocalEquipmentSnapshots.Clear();
		m_hostTerrainPlaceExecutions.Clear();
		m_hostMeleeHitExecutions.Clear();
		m_processedTerrainPlaceRequests.Clear();
        m_worldTransferRegistry.Reset();
        m_joinCatchUpRegistry.Reset();
        ClearReliableRelayReservations();
		m_hostPlayerPokingPhases.Clear();
		m_hostPlayerPokeSequences.Clear();
		m_playerWhistleSequences.Clear();
		m_localDigTarget = null;
		m_localDigPresentationSequence = 0;
		m_localDigPresentationActive = false;
		m_localDigPresentationFace = null;
		m_remoteDigPresentations.Clear();
		m_nextTerrainDigRequestId = 0;
		m_localHitSequence = 0;
		m_nextLocalHitRequestTime = 0.0;
		m_localMeleePredictions.Clear();
		m_localInteractSequence = 0;
		m_localDropSequence = 0;
		m_localJumpSequence = 0;
		m_observedLocalPlayerEntity = null;
		m_observedLocalPlayerWasDead = false;
		m_localRespawnSequence = 0;
		m_localRespawnPendingUntil = 0.0;
		m_nextWorldTransferId = 0;
		m_worldTransferCursor = 0;
		m_joinTransferTrafficSamples.Clear();
		m_joinTransferTokens = 0.0;
		m_joinTransferLastTokenTime = 0.0;
		m_joinTransferAvailableBytesPerSecond = 0.0;
		m_joinTransferPausedByGameplay = false;
		m_automaticJoinTransferKbps = 1200.0;
		m_nextAutomaticJoinTransferAdjustmentTime = 0.0;
		m_automaticJoinTransferCooldownUntil = 0.0;
		m_automaticJoinRttBaseline = 0.0;
		m_lastJoinTransferSampleTime = 0.0;
		m_lastJoinTransferNetworkBytesSample = 0L;
		m_lastJoinTransferReceiveBytesSample = 0L;
		m_lastJoinTransferBytesSample = Interlocked.Read(ref m_joinTransferBytesSentSinceSample);
		m_nextServerTrafficSampleStartTime = 0.0;
		m_serverTrafficSampleStartTime = 0.0;
		m_serverTrafficSampleStartBytesSent = 0L;
		m_serverTrafficSampleStartBytesReceived = 0L;
		m_serverTrafficSampleStartPacketsSent = 0L;
		m_serverTrafficSampleStartPacketsReceived = 0L;
		m_lastServerTrafficSampleBytesSent = -1L;
		m_lastServerTrafficSampleBytesReceived = -1L;
		m_lastServerTrafficSamplePacketsSent = -1L;
		m_lastServerTrafficSamplePacketsReceived = -1L;
		m_serverTrafficSampleActive = false;
		m_nextWorldTransferManifestRequestTime = 0.0;
		m_nextWorldTransferUiUpdateTime = 0.0;
		m_projectReadySentProject = null;
		m_projectReadySentTransferId = 0;
		ClientJoinReadyStage = GamePakWorldReadyStage.ProjectReady;
		m_nextClientJoinReadyRetryTime = 0.0;
		m_lastClientJoinBarrierProgressTime = 0.0;
		m_terrainMergeTime = 0f;
		SuSubsystemTerrain.ResetNetworkState();
		m_sessionRandomSeed = 0;
		m_pendingRandomStates.Clear();
		m_randomStateAppliedProject = null;
		m_nextAnimalId = 1;
		m_nextPickableId = 1;
		m_fullWorldObjectsSyncTime = 0f;
		m_fullAnimalSyncTime = 0f;
		m_runawayCreatureCleanup.Clear();
		m_runawayCreatureCleanupProject = null;
		m_nextRunawayCreatureCheckTime = 0.0;
		m_nextRemoteCreatureSpawnTime = 0.0;
		m_remoteCreatureSpawnCursor = 0;
		m_syncPulseAccumulator = 0f;
		m_lastSyncUpdateTime = 0.0;
		m_syncPulseIndex = 0u;
		m_inventoryKeyframeTime = 0f;
		m_playerRecordSaveTime = 0f;
		m_localPlayerInput = default(PlayerInput);
		m_localInputBodyPosition = Vector3.Zero;
		m_localInputBodyVelocity = Vector3.Zero;
		m_localInputBodyRotation = Quaternion.Identity;
		m_localInputLookAngles = Vector2.Zero;
		m_localInputSequence = 0;
		m_lastSentInputSequence = -1;
		m_localInputResendsRemaining = 0;
		m_localMountActionSequence = 0;
		m_localMountActionExpectedRiding = false;
		m_lastLocalMountStateSequence = -1;
		m_lastLocalMountStateServerTick = -1;
		m_receivedMountStates.Clear();
		m_hostMountStateSequences.Clear();
		m_localAimActive = false;
		m_localAimSequence = 0;
		m_localAimSlot = -1;
		m_localAimItemValue = 0;
		m_localAimItemCount = 0;
		m_lastAimUpdateSentTime = 0.0;
		m_smoothedNetworkDelay = 0f;
		m_localKnockbackPositionCorrectionUntil = 0.0;
		m_localKnockbackCorrectionStartTick = -1;
		m_lastLocalKnockbackSequence = -1;
		m_lastAuthoritativeLocalPositionTick = -1;
		m_clientWorldObjectsProject = null;
		m_remoteWeatherState = null;
		m_lastRemoteWorldInfoTick = -1;
		m_lastRemoteWorldTimeRevision = 0;
		m_hostWorldTimeRevision = 0;
		m_remoteTimeAccelerated = false;
		m_hostSleepAccelerationSessionActive = false;
		m_hostObservedSleepStates.Clear();
		m_clientSleepWakeBoundaryPending = false;
		m_pendingClientSleepWakeups.Clear();
		m_remoteTerrainHeadSequence = 0L;
		m_remoteFogPresentationInitialized = false;
		m_remoteLightningActive = false;
		m_hostLightningActive = false;
		ClearSessionAssets();
		m_pendingWorldControlRequests.Clear();
		m_queuedWorldControlRequests.Clear();
		m_bufferedWorldControlResults.Clear();
		m_hostWorldControlRequestStates.Clear();
		m_nextWorldControlRequestId = 0;
		m_nextWorldControlFeedbackRequestId = 1;
		m_worldControlQueueNoticeShown = false;
		m_reliableRetryLimitBaseline = 0L;
		m_reliableStallReconnectIssued = false;
		m_reliableStallSince = 0.0;
		m_hostReliableRetryLimitBaselines.Clear();
		m_hostReliableStallSince.Clear();
		m_hostReliableStallDisconnectIssued.Clear();
		m_nextHostReliableHealthTime = 0.0;
		m_pendingLocalPlayerRecord = null;
		m_localReplacementPlayerData = null;
		m_localPlayerRecordQueued = false;
		m_localPlayerRecordApplied = false;
		m_pendingPlayerEquipmentMessages.Clear();
		if (!IsHost)
		{
			m_playerRecords.Clear();
			m_playerRecordsWorldDirectory = null;
			m_playerRecordsDirty = false;
		}
		playerMappingManager.Reset();
	}

	private Dictionary<string, long> CaptureSubsystemRandomStates()
	{
		Dictionary<string, long> states = new Dictionary<string, long>(StringComparer.Ordinal);
		Project project = GameManager.Project;
		if (project == null)
		{
			return states;
		}
		foreach (Subsystem subsystem in project.Subsystems)
		{
			foreach (FieldInfo field in GetSubsystemRandomFields(subsystem.GetType()))
			{
				Engine.Random random = ModManager.ModParentField.GetParentField<Engine.Random>(subsystem, field.Name, field.DeclaringType);
				if (random != null)
				{
					states[GetRandomFieldKey(field)] = (long)random.State;
				}
			}
		}
		return states;
	}

	private void ApplyHostRandomStates(Project project)
	{
		if (project == null || IsHost || m_sessionRandomSeed == 0 || m_randomStateAppliedProject == project)
		{
			return;
		}
		foreach (Subsystem subsystem in project.Subsystems)
		{
			foreach (FieldInfo field in GetSubsystemRandomFields(subsystem.GetType()))
			{
				Engine.Random random = new Engine.Random(DeriveRandomSeed(m_sessionRandomSeed, GetRandomFieldKey(field)));
				if (m_pendingRandomStates.TryGetValue(GetRandomFieldKey(field), out var state))
				{
					random.State = (ulong)state;
				}
				ModManager.ModParentField.ModifyParentField(subsystem, field.Name, random, field.DeclaringType);
			}
		}
		m_randomStateAppliedProject = project;
	}

	private static string GetRandomFieldKey(FieldInfo field)
	{
		return field.DeclaringType.FullName + "|" + field.Name;
	}

	private static int DeriveRandomSeed(int seed, string key)
	{
		int hash = seed;
		foreach (char c in key)
		{
			hash = hash * 31 + c;
		}
		return hash;
	}

	private void EnsureNetworkComponentPlayers()
	{
		SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(throwOnError: false);
		if (players == null || m_networkPlayerData.Count == 0)
		{
			return;
		}
		List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(players, "m_componentPlayers", typeof(SubsystemPlayers));
		foreach (PlayerData playerData in m_networkPlayerData.Values)
		{
			if (playerData.ComponentPlayer != null && !componentPlayers.Contains(playerData.ComponentPlayer))
			{
				componentPlayers.Add(playerData.ComponentPlayer);
			}
		}
	}

	private void HandleProjectDisposed(Project project)
	{
		if (m_hostPickablesSubsystem == project?.FindSubsystem<SubsystemPickables>(throwOnError: false))
		{
			DetachHostPickableEvents();
		}
		bool isClientWorldRefresh = !IsHost && project == m_clientWorldRefreshProject;
		if (isClientWorldRefresh)
		{
			m_clientWorldRefreshProject = null;
		}
		if (!m_hostDisconnectHandled && !isClientWorldRefresh)
		{
			BeginLocalGameLeave();
		}
		int[] array = (from pair in m_networkPlayerData
			where pair.Value.ComponentPlayer?.Entity.Project == project
			select pair.Key).ToArray();
		foreach (int clientId in array)
		{
			RemoveNetworkPlayer(clientId);
		}
		if (!isClientWorldRefresh)
		{
			ResetTransientNetworkState();
		}
	}

	private static HashSet<string> ReadDownloadedWorldRegistry()
	{
		if (!Storage.FileExists("data:/ScMultiplayerDownloadedWorlds.txt"))
		{
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
		return new HashSet<string>(Storage.ReadAllText("data:/ScMultiplayerDownloadedWorlds.txt").Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
	}

	private static void WriteDownloadedWorldRegistry(HashSet<string> directories)
	{
		if (directories.Count == 0)
		{
			if (Storage.FileExists("data:/ScMultiplayerDownloadedWorlds.txt"))
			{
				Storage.DeleteFile("data:/ScMultiplayerDownloadedWorlds.txt");
			}
		}
		else
		{
			Storage.WriteAllText("data:/ScMultiplayerDownloadedWorlds.txt", string.Join("\n", directories));
		}
	}

	private static void RegisterDownloadedWorld(string directoryName)
	{
		HashSet<string> hashSet = ReadDownloadedWorldRegistry();
		hashSet.Add(directoryName);
		WriteDownloadedWorldRegistry(hashSet);
	}

	private void CleanupDownloadedWorldsIfIdle()
	{
		if (GameManager.Project != null || m_isLoadingDownloadedWorld)
		{
			return;
		}
		HashSet<string> directories = ReadDownloadedWorldRegistry();
		if (directories.Count == 0)
		{
			return;
		}
		HashSet<string> failedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string directoryName in directories)
		{
			try
			{
				WorldsManager.DeleteWorld(directoryName);
				if (string.Equals(m_downloadedWorldDirectory, directoryName, StringComparison.OrdinalIgnoreCase))
				{
					m_downloadedWorldDirectory = null;
				}
			}
			catch (Exception ex)
			{
				Log.Error("[ScMP] Failed to delete downloaded world " + directoryName + ": " + ex.Message);
				failedDirectories.Add(directoryName);
			}
		}
		WriteDownloadedWorldRegistry(failedDirectories);
		WorldsManager.UpdateWorldsList();
	}

	internal void CleanupDownloadedWorldsBeforeWorldList()
	{
		CleanupDownloadedWorldsIfIdle();
	}

	private void Server_Information(string obj)
	{
		Log.Information("[Server] " + obj);
	}

	private void UpdateRemotePlayerPresentations(float dt)
	{
		KeyValuePair<int, NetworkPlayerState>[] array = RemotePlayers.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			KeyValuePair<int, NetworkPlayerState> item = array[i];
			NetworkPlayerState state = item.Value;
			if (state == null || Time.RealTime - state.LastUpdateTime > 2.0 || !m_networkPlayerData.TryGetValue(item.Key, out var playerData) || playerData.ComponentPlayer?.ComponentBody == null)
			{
				continue;
			}
			ComponentBody body = playerData.ComponentPlayer.ComponentBody;
			ComponentCreatureModel model = playerData.ComponentPlayer.ComponentCreatureModel;
			ComponentLocomotion locomotion = playerData.ComponentPlayer.ComponentLocomotion;
			body.IsGravityEnabled = !state.IsFlying;
			body.IsGroundDragEnabled = !state.IsFlying;
			if (locomotion != null)
			{
				locomotion.IsCreativeFlyEnabled = state.IsFlying;
				ModManager.ModParentField.ModifyParentField(locomotion, "m_lookAngles", state.LookAngles, typeof(ComponentLocomotion));
				ModManager.ModParentField.ModifyParentField(locomotion, "<LastWalkOrder>k__BackingField", state.WalkOrder, typeof(ComponentLocomotion));
				ModManager.ModParentField.ModifyParentField(locomotion, "<LastJumpOrder>k__BackingField", state.JumpOrder, typeof(ComponentLocomotion));
			}
			if (model != null)
			{
				model.AttackOrder = state.AttackOrder;
				model.RowLeftOrder = state.RowLeftOrder;
				model.RowRightOrder = state.RowRightOrder;
				model.InHandItemOffsetOrder = state.ItemOffset;
				model.InHandItemRotationOrder = state.ItemRotation;
				model.AimHandAngleOrder = state.AimHandAngle;
			}
			// Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.Update
			// A mounted body follows ParentBody. Direct network correction of the rider body
			// makes the native out-of-mount distance test dismount it after collisions.
			ComponentRider rider = playerData.ComponentPlayer.ComponentRider;
			if (state.IsRiding && rider?.Mount != null)
			{
				state.PresentationInitialized = true;
				continue;
			}
			float delaySample = MathUtils.Clamp((float)(client.Step - state.ServerTick) * 0.01f, 0f, 0.6f);
			state.EstimatedDelay = ((state.EstimatedDelay <= 0f) ? delaySample : MathUtils.Lerp(state.EstimatedDelay, delaySample, 0.12f));
            float extrapolationTime = MathUtils.Min(state.EstimatedDelay, RemoteExtrapolationLimit);
			Vector3 targetPosition = state.Position;
			if (!state.IsFlying)
			{
				targetPosition.X += state.Velocity.X * extrapolationTime;
				targetPosition.Z += state.Velocity.Z * extrapolationTime;
			}
			if (!state.IsGrounded && !state.IsFlying)
			{
				targetPosition.Y += state.Velocity.Y * extrapolationTime - 4.9f * extrapolationTime * extrapolationTime;
			}
			float errorSquared = Vector3.DistanceSquared(body.Position, targetPosition);
			if (!state.PresentationInitialized || errorSquared > 9f)
			{
				body.Position = state.Position;
				body.Rotation = state.Rotation;
				body.Velocity = state.Velocity;
				state.PresentationInitialized = true;
				continue;
			}
			if (state.IsFlying)
			{
				float positionBlend = 1f - MathUtils.Pow(0.001f, MathUtils.Min(dt, 0.1f));
				body.Position = Vector3.Lerp(body.Position, state.Position, positionBlend);
				body.Velocity = Vector3.Zero;
				body.Rotation = Quaternion.Slerp(body.Rotation, state.Rotation, positionBlend);
				continue;
			}
			float delayFactor = MathUtils.Saturate(state.EstimatedDelay / 0.2f);
			Vector3 error = targetPosition - body.Position;
			float deadZone = ((!(Time.RealTime < state.KnockbackCorrectionUntil) || state.ServerTick < state.KnockbackCorrectionStartTick) ? (state.IsGrounded ? 0.2f : 0.1f) : (state.IsGrounded ? 0.05f : 0.1f));
			Vector3 targetVelocity = state.Velocity;
			if (state.IsGrounded)
			{
				targetVelocity.Y = 0f;
			}
			Vector3 desiredVelocity;
			float blend;
			if (error.LengthSquared() <= deadZone * deadZone)
			{
				desiredVelocity = targetVelocity;
				blend = 0.45f;
			}
			else
			{
				float horizon = MathUtils.Lerp(0.35f, 0.2f, delayFactor);
				Vector3 catchUpVelocity = error / horizon;
				float maxExtraSpeed = MathUtils.Lerp(3f, 8f, delayFactor);
				float extraSpeed = catchUpVelocity.Length();
				if (extraSpeed > maxExtraSpeed)
				{
					catchUpVelocity *= maxExtraSpeed / extraSpeed;
				}
				desiredVelocity = targetVelocity + catchUpVelocity;
				blend = MathUtils.Lerp(0.2f, 0.35f, delayFactor);
			}
			if (!state.IsGrounded)
			{
				blend = MathUtils.Max(blend, 0.45f);
			}
			body.Velocity = Vector3.Lerp(body.Velocity, desiredVelocity, blend);
			body.Rotation = Quaternion.Slerp(body.Rotation, state.Rotation, 0.4f);
		}
	}

	private void CaptureHostRemoteKnockbacks()
	{
		double now = Time.RealTime;
		KeyValuePair<int, PlayerData>[] array = m_networkPlayerData.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			KeyValuePair<int, PlayerData> remote = array[i];
			if (remote.Key <= 0 || remote.Value?.ComponentPlayer == null)
			{
				continue;
			}
			ComponentPlayer player = remote.Value.ComponentPlayer;
			ComponentHealth health = player.ComponentHealth;
			ComponentBody body = player.ComponentBody;
			if (health == null || body == null)
			{
				continue;
			}
			if (!m_hostKnockbackHealthCache.TryGetValue(remote.Key, out var previousHealth))
			{
				m_hostKnockbackHealthCache[remote.Key] = health.Health;
				continue;
			}
			bool num = health.Health < previousHealth - 0.0001f;
			double heldUntil;
			bool alreadyHeld = m_hostRemoteKnockbackUntil.TryGetValue(remote.Key, out heldUntil) && heldUntil > now;
			float knockbackStunTime = MathUtils.Max(player.ComponentLocomotion?.StunTime ?? 0f, 0f);
			bool attackStun = knockbackStunTime > 0f;
			if ((num || (attackStun && !alreadyHeld)) && body.Velocity.LengthSquared() > 0.0001f)
			{
				AuthoritativePlayerStateSnapshot lastSentState;
				float lastSentHealth = (m_lastSentAuthoritativePlayerStates.TryGetValue(remote.Key, out lastSentState) ? lastSentState.Health : previousHealth);
				m_hostKnockbackSequences.TryGetValue(remote.Key, out var knockbackSequence);
				knockbackSequence = PlayerActionSequencePolicy.Next(knockbackSequence);
				m_hostKnockbackSequences[remote.Key] = knockbackSequence;
				m_hostRemoteKnockbackUntil[remote.Key] = now + 0.75;
				NetworkMessageSender.SendPlayerHealthMessage(remote.Key, player, health.Health - lastSentHealth, null, hasKnockback: true, knockbackSequence, client.Step, knockbackStunTime);
				if (player.ComponentVitalStats != null)
				{
					m_lastSentAuthoritativePlayerStates[remote.Key] = CaptureAuthoritativePlayerState(player);
				}
			}
			m_hostKnockbackHealthCache[remote.Key] = health.Health;
		}
		int[] array2 = m_hostKnockbackHealthCache.Keys.Where((int id) => !m_networkPlayerData.ContainsKey(id)).ToArray();
		foreach (int clientId in array2)
		{
			m_hostKnockbackHealthCache.Remove(clientId);
			m_hostRemoteKnockbackUntil.Remove(clientId);
			m_hostKnockbackSequences.Remove(clientId);
		}
	}

	private void SampleRemotePositionRtt(int clientId, NetworkPlayerInputState state)
	{
		if (clientId <= 0 || state == null || server?.Peer == null || client == null)
		{
			return;
		}
		double now = Time.RealTime;
		if (now < state.NextPositionRttSampleTime)
		{
			return;
		}
		state.NextPositionRttSampleTime = now + 0.25;
		float ping = 0f;
		try
		{
			lock (server.Peer.Lock)
			{
				ServerClient remoteClient = server.Games.FirstOrDefault((ServerGame item) => item.GameID == client.GameID)?.Clients.FirstOrDefault((ServerClient item) => item.ClientID == clientId);
				ping = ((remoteClient == null) ? null : server.Peer.FindPeer(remoteClient.Address))?.Ping ?? 0f;
			}
		}
		catch
		{
			return;
		}
		if (!(ping <= 0f) && !float.IsNaN(ping) && !float.IsInfinity(ping))
		{
			ping = (state.LatestPositionRtt = MathUtils.Clamp(ping, 0f, 1.2f));
			if (state.SmoothedPositionRtt <= 0f)
			{
				state.SmoothedPositionRtt = ping;
				state.PositionRttDeviation = 0f;
			}
			else
			{
				float deviation = MathUtils.Abs(ping - state.SmoothedPositionRtt);
				state.PositionRttDeviation = MathUtils.Lerp(state.PositionRttDeviation, deviation, 0.1f);
				state.SmoothedPositionRtt = MathUtils.Lerp(state.SmoothedPositionRtt, ping, 0.1f);
			}
		}
	}

	private static float GetRemotePositionPredictionTime(NetworkPlayerInputState state)
	{
		if (state == null || state.SmoothedPositionRtt <= 0f)
		{
			return 0f;
		}
		float stableThreshold = MathUtils.Max(0.005f, 0.25f * state.SmoothedPositionRtt);
		float effectiveRtt = ((state.PositionRttDeviation <= stableThreshold) ? state.SmoothedPositionRtt : MathUtils.Min(state.LatestPositionRtt, state.SmoothedPositionRtt));
		return 0.5f * effectiveRtt;
	}

	private void ApplyHostRemoteFollowVelocities()
	{
		KeyValuePair<int, PlayerData>[] array = m_networkPlayerData.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			KeyValuePair<int, PlayerData> remote = array[i];
			if (remote.Key <= 0 || remote.Value?.ComponentPlayer == null || !m_networkPlayerInputs.TryGetValue(remote.Key, out var state) || Time.RealTime - state.LastReceivedTime > 0.75)
			{
				continue;
			}
			if (m_hostRemoteKnockbackUntil.TryGetValue(remote.Key, out var knockbackUntil))
			{
				if (Time.RealTime < knockbackUntil)
				{
					continue;
				}
				m_hostRemoteKnockbackUntil.Remove(remote.Key);
			}
			ComponentPlayer player = remote.Value.ComponentPlayer;
			if (player.ComponentRider?.Mount != null)
			{
				continue;
			}
			ComponentBody body = player.ComponentBody;
			ComponentLocomotion locomotion = player.ComponentLocomotion;
			float delay = GetRemotePositionPredictionTime(state);
			Vector3 predictionOffset = state.BodyVelocity * delay;
			float maximumPredictionDistance = 0.5f * MathUtils.Max(body.StanceBoxSize.X, body.StanceBoxSize.Z);
			float predictionDistance = predictionOffset.Length();
			if (predictionDistance > maximumPredictionDistance && predictionDistance > 0.0001f)
			{
				predictionOffset *= maximumPredictionDistance / predictionDistance;
			}
			Vector3 error = state.BodyPosition + predictionOffset - body.Position;
			Vector3 travelVelocity = state.BodyVelocity;
			Vector3 localIntent = ((body.CrouchFactor > 0f) ? (0.66f * state.Input.CrouchMove) : state.Input.Move);
			Vector3 right = body.Matrix.Right;
			Vector3 forward = body.Matrix.Forward;
			Vector3 travelIntent;
			if (!locomotion.IsCreativeFlyEnabled)
			{
				error.Y = 0f;
				travelVelocity.Y = 0f;
				right.Y = 0f;
				forward.Y = 0f;
				if (right.LengthSquared() > 0.0001f)
				{
					right = Vector3.Normalize(right);
				}
				if (forward.LengthSquared() > 0.0001f)
				{
					forward = Vector3.Normalize(forward);
				}
				travelIntent = right * localIntent.X + forward * localIntent.Z;
			}
			else
			{
				travelIntent = right * localIntent.X + Vector3.UnitY * localIntent.Y + forward * localIntent.Z;
			}
			float intentLength = travelIntent.Length();
			if (intentLength < 0.05f && locomotion.IsCreativeFlyEnabled)
			{
				travelIntent = ((error.LengthSquared() > 0.0025f) ? error : travelVelocity);
				intentLength = travelIntent.Length();
			}
			if (state.IsGrounded && !locomotion.IsCreativeFlyEnabled && MathUtils.Abs(state.BodyVelocity.Y) < 0.5f && body.Velocity.Y < 0.1f)
			{
				if (MathUtils.Abs((state.BodyPosition - body.Position).Y) > 0.025f)
				{
					body.Position = new Vector3(body.Position.X, state.BodyPosition.Y, body.Position.Z);
				}
				body.Velocity = new Vector3(body.Velocity.X, state.BodyVelocity.Y, body.Velocity.Z);
				if (intentLength < 0.05f)
				{
					Vector3 groundedError = state.BodyPosition - body.Position;
					float correctionLength = groundedError.Length();
					Vector3 correction = ((correctionLength > 1f) ? (groundedError / correctionLength) : groundedError);
					body.Position += correction;
					body.Velocity = state.BodyVelocity;
					continue;
				}
			}
			if (intentLength < 0.05f)
			{
				continue;
			}
			Vector3 travelDirection = travelIntent / intentLength;
			float forwardError = Vector3.Dot(error, travelDirection);
			if (forwardError < 0f)
			{
				error -= travelDirection * forwardError;
			}
			float staleVelocity = Vector3.Dot(travelVelocity, travelDirection);
			if (staleVelocity < 0f)
			{
				travelVelocity -= travelDirection * staleVelocity;
			}
			float trackingRadius = (locomotion.IsCreativeFlyEnabled ? 32f : 16f);
			float errorLength = error.Length();
			if (errorLength > trackingRadius)
			{
				error *= trackingRadius / errorLength;
				errorLength = trackingRadius;
			}
			bool isInteracting = state.Input.Dig.HasValue || state.Input.Hit.HasValue || state.Input.Interact.HasValue || state.Input.Aim.HasValue;
			float deadZone = (locomotion.IsCreativeFlyEnabled ? 0.35f : 0.15f);
			if (!(errorLength <= deadZone))
			{
				float delayFactor = MathUtils.Saturate(delay / 0.2f);
				float horizon = MathUtils.Lerp(0.45f, 0.22f, delayFactor);
				Vector3 catchUpVelocity = error / horizon;
				float maxExtraSpeed = (locomotion.IsCreativeFlyEnabled ? MathUtils.Lerp(4f, 10f, delayFactor) : MathUtils.Lerp(2f, 6f, delayFactor));
				float extraSpeed = catchUpVelocity.Length();
				if (extraSpeed > maxExtraSpeed)
				{
					catchUpVelocity *= maxExtraSpeed / extraSpeed;
				}
				Vector3 desiredVelocity = travelVelocity + catchUpVelocity;
				if (!locomotion.IsCreativeFlyEnabled)
				{
					desiredVelocity.Y = body.Velocity.Y;
				}
				float blend = MathUtils.Lerp(0.14f, 0.28f, delayFactor);
				if (isInteracting)
				{
					blend = MathUtils.Max(blend, 0.35f);
				}
				body.Velocity = Vector3.Lerp(body.Velocity, desiredVelocity, blend);
			}
		}
	}

	private void UpdateRemoteDigPresentations()
	{
		if (m_remoteDigPresentations.Count == 0)
		{
			return;
		}
		SubsystemTime subsystemTime = GameManager.Project?.FindSubsystem<SubsystemTime>(throwOnError: false);
		if (subsystemTime == null)
		{
			return;
		}
		double now = Time.RealTime;
		KeyValuePair<int, RemoteDigPresentation>[] array = m_remoteDigPresentations.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			KeyValuePair<int, RemoteDigPresentation> entry = array[i];
			if (now - entry.Value.LastUpdateTime > 0.35 || !m_networkPlayerData.TryGetValue(entry.Key, out var playerData) || playerData?.ComponentPlayer?.ComponentMiner == null)
			{
				m_remoteDigPresentations.Remove(entry.Key);
				continue;
			}
			ComponentMiner miner = playerData.ComponentPlayer.ComponentMiner;
			RemoteDigPresentation state = entry.Value;
			state.DisplayProgress = MathUtils.Lerp(state.DisplayProgress, state.TargetProgress, 0.28f);
			ModManager.ModParentField.ModifyParentField(miner, "<DigCellFace>k__BackingField", state.CellFace, typeof(ComponentMiner));
			ModManager.ModParentField.ModifyParentField(miner, "m_digProgress", state.DisplayProgress, typeof(ComponentMiner));
			ModManager.ModParentField.ModifyParentField(miner, "m_digStartTime", subsystemTime.GameTime - 0.25, typeof(ComponentMiner));
			ModManager.ModParentField.ModifyParentField(miner, "m_lastDigFrameIndex", Time.FrameIndex, typeof(ComponentMiner));
		}
	}

	public void CaptureLocalPlayerInput(ComponentPlayer player, PlayerInput playerInput)
	{
		if (IsHost)
		{
			TrackHostTerrainPlaceIntent(player, playerInput.Interact);
			return;
		}
		Client obj = client;
		if (obj == null || !obj.IsConnected || player == null || m_networkPlayerData.Values.Contains(player.PlayerData))
		{
			return;
		}
		EnsureClientDropDragHost(player);
		// Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
		// The local GUI still predicts the native animation, but its one-shot action must travel
		// through MountActionMessage, not the latest-only input snapshot.
		if (playerInput.ToggleMount)
		{
			ComponentMount currentMount = player.ComponentRider?.Mount;
			m_localMountActionExpectedRiding = currentMount == null;
			MountActionKind action = currentMount == null
				? MountActionKind.Mount
				: MountActionKind.Dismount;
			ComponentMount targetMount = currentMount ?? player.ComponentRider?.FindNearestMount();
			m_localMountActionSequence = PlayerActionSequencePolicy.Next(m_localMountActionSequence);
			NetworkMessageSender.SendMountActionMessage(new MountActionMessage(
				obj.ClientID, m_localMountActionSequence, action,
				GetNetworkMountEntityId(targetMount?.Entity),
				player.ComponentBody.Position, player.ComponentBody.Rotation,
				player.ComponentLocomotion?.LookAngles ?? Vector2.Zero, obj.Step));
		}
		IInventory inventory = player.ComponentMiner?.Inventory;
		int activeSlot = inventory?.ActiveSlotIndex ?? (-1);
		NormalizeCrossbowSlot(inventory, activeSlot);
		RememberLocalEquipmentSnapshot(inventory);
		UpdateLocalAimLifecycle(player, playerInput, inventory, activeSlot);
		if (playerInput.Aim.HasValue && inventory != null && activeSlot >= 0 && activeSlot < inventory.SlotsCount)
		{
			int value = inventory.GetSlotValue(activeSlot);
			if (BlocksManager.Blocks[Terrain.ExtractContents(value)].IsAimable)
			{
				playerInput.Dig = null;
			}
		}
		UpdateLocalDigTarget(player, playerInput.Dig);
		UpdateLocalDigPresentation(player);
		UpdateLocalHitRequests(player, playerInput.Hit);
		UpdateLocalInteractRequests(player, playerInput.Interact);
		UpdateLocalDropRequests(player, playerInput.Drop);
		UpdateLocalJumpRequests(playerInput.Jump);
		// Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
		// The reliable MountActionMessage above owns this edge. Keep it out of the replaceable
		// GamePlayerInputMessage snapshot so the host cannot execute it a second time.
		playerInput.ToggleMount = false;
		playerInput.Aim = null;
		playerInput.Drop = false;
		playerInput.Jump = false;
		m_localPlayerInput = PlayerInputStatePolicy.Sanitize(playerInput);
		m_localPlayerInput.Dig = null;
		m_localPlayerInput.Hit = null;
		m_localPlayerInput.Interact = null;
		m_localInputBodyPosition = player.ComponentBody.Position;
		m_localInputBodyVelocity = player.ComponentBody.Velocity;
		m_localInputBodyRotation = player.ComponentBody.Rotation;
		m_localInputLookAngles = player.ComponentLocomotion.LookAngles;
		m_localInputSequence = PlayerActionSequencePolicy.Next(m_localInputSequence);
		m_localInputResendsRemaining = 3;
		if (inventory != null)
		{
			m_lastLocalInventoryValues = Enumerable.Range(0, inventory.SlotsCount).Select(inventory.GetSlotValue).ToArray();
			m_lastLocalInventoryCounts = Enumerable.Range(0, inventory.SlotsCount).Select(inventory.GetSlotCount).ToArray();
		}
	}

	private void EnsureClientDropDragHost(ComponentPlayer player)
	{
		GameWidget gameWidget = player?.PlayerData?.GameWidget;
		if (gameWidget == null || m_clientDropDragHostGameWidget == gameWidget)
		{
			return;
		}
		DragHostWidget dragHost = gameWidget.Children.Find<DragHostWidget>("DragHost", throwIfNotFound: false);
		if (dragHost == null || dragHost is SuNetworkDragHostWidget || dragHost.IsDragInProgress || dragHost.ParentWidget == null)
		{
			return;
		}
		ContainerWidget parentWidget = dragHost.ParentWidget;
		int childIndex = parentWidget.Children.IndexOf(dragHost);
		SuNetworkDragHostWidget replacement = new SuNetworkDragHostWidget
		{
			Name = dragHost.Name,
			Tag = dragHost.Tag,
			IsVisible = dragHost.IsVisible,
			IsEnabled = dragHost.IsEnabled,
			IsHitTestVisible = dragHost.IsHitTestVisible,
			ClampToBounds = dragHost.ClampToBounds,
			Margin = dragHost.Margin,
			HorizontalAlignment = dragHost.HorizontalAlignment,
			VerticalAlignment = dragHost.VerticalAlignment,
			LayoutTransform = dragHost.LayoutTransform,
			RenderTransform = dragHost.RenderTransform
		};
		parentWidget.Children.Remove(dragHost);
		parentWidget.Children.Insert(childIndex, replacement);
		foreach (InventorySlotWidget slotWidget in gameWidget.AllChildren.OfType<InventorySlotWidget>())
		{
			ModManager.ModParentField.ModifyParentField(slotWidget, "m_dragHostWidget", replacement, typeof(InventorySlotWidget));
		}
		m_clientDropDragHostGameWidget = gameWidget;
	}

	private void UpdateLocalDigTarget(ComponentPlayer player, Ray3? digRay)
	{
		if (!digRay.HasValue || player?.ComponentMiner == null)
		{
			m_localDigTarget = null;
			return;
		}
		TerrainRaycastResult? hit = player.ComponentMiner.Raycast<TerrainRaycastResult>(digRay.Value, RaycastMode.Digging, raycastTerrain: true, raycastBodies: false, raycastMovingBlocks: false);
		if (!hit.HasValue)
		{
			m_localDigTarget = null;
			return;
		}
		Point3 point = new Point3(hit.Value.CellFace.X, hit.Value.CellFace.Y, hit.Value.CellFace.Z);
		int expectedValue = Terrain.ReplaceLight(hit.Value.Value, 0);
		if (!m_localDigTarget.HasValue || m_localDigTarget.Value != point)
		{
			m_localDigTarget = point;
		}
		IInventory inventory = player.ComponentMiner.Inventory;
		int activeSlot = inventory?.ActiveSlotIndex ?? (-1);
		int toolValue = ((inventory != null && activeSlot >= 0 && activeSlot < inventory.SlotsCount) ? inventory.GetSlotValue(activeSlot) : 0);
		int toolCount = ((inventory != null && activeSlot >= 0 && activeSlot < inventory.SlotsCount) ? inventory.GetSlotCount(activeSlot) : 0);
		BlockPlacementData predictedDig = BlocksManager.Blocks[Terrain.ExtractContents(expectedValue)].GetDigValue(
			GameManager.Project.FindSubsystem<SubsystemTerrain>(true), player.ComponentMiner,
			hit.Value.Value, toolValue, hit.Value);
		int predictedValue = Terrain.ReplaceLight(predictedDig.Value, 0);
		if (!m_localTerrainDigIntents.TryGetValue(point, out var intent) || intent.ExpectedValue != expectedValue)
		{
			intent = new LocalTerrainDigIntent
			{
				ExpectedValue = expectedValue,
				PredictedValue = predictedValue,
				StartClientTick = client.Step
			};
			m_localTerrainDigIntents[point] = intent;
		}
		intent.PredictedValue = predictedValue;
		intent.DigRay = digRay.Value;
		intent.HitFace = hit.Value.CellFace.Face;
		intent.ActiveSlotIndex = activeSlot;
		intent.ToolValue = toolValue;
		intent.ToolCount = toolCount;
		intent.BodyPosition = player.ComponentBody.Position;
		intent.LastSeenTime = Time.RealTime;
	}

	private void UpdateLocalDigPresentation(ComponentPlayer player)
	{
		ComponentMiner miner = player?.ComponentMiner;
		CellFace? cellFace = ((!m_localDigTarget.HasValue) ? null : miner?.DigCellFace);
		bool active = cellFace.HasValue && miner.DigTime > 0f && miner.DigProgress < 1f;
		double now = Time.RealTime;
		bool changed = active != m_localDigPresentationActive || (active && (!m_localDigPresentationFace.HasValue || m_localDigPresentationFace.Value.X != cellFace.Value.X || m_localDigPresentationFace.Value.Y != cellFace.Value.Y || m_localDigPresentationFace.Value.Z != cellFace.Value.Z || m_localDigPresentationFace.Value.Face != cellFace.Value.Face));
		if (changed || (active && !(now < m_nextLocalDigPresentationTime)))
		{
			m_localDigPresentationSequence = PlayerActionSequencePolicy.Next(m_localDigPresentationSequence);
			int x = (active ? cellFace.Value.X : 0);
			int y = (active ? cellFace.Value.Y : 0);
			int z = (active ? cellFace.Value.Z : 0);
			int face = (active ? cellFace.Value.Face : 0);
			NetworkMessageSender.SendDigPresentation(0, new DigPresentationMessage(client.ClientID, m_localDigPresentationSequence, active, x, y, z, face, active ? MathUtils.Saturate(miner.DigProgress) : 0f), !changed);
			m_localDigPresentationActive = active;
			m_localDigPresentationFace = (active ? cellFace : null);
			m_nextLocalDigPresentationTime = now + 0.125;
		}
	}

	private void UpdateLocalAimLifecycle(ComponentPlayer player, PlayerInput playerInput, IInventory inventory, int activeSlot)
	{
		int itemValue = ((inventory != null && activeSlot >= 0 && activeSlot < inventory.SlotsCount) ? inventory.GetSlotValue(activeSlot) : 0);
		bool isAimable = itemValue != 0 && BlocksManager.Blocks[Terrain.ExtractContents(itemValue)].IsAimable;
		double now = Time.RealTime;
		if (playerInput.Aim.HasValue && isAimable)
		{
			Ray3 aim = playerInput.Aim.Value;
			if (!m_localAimActive || activeSlot != m_localAimSlot || Terrain.ExtractContents(itemValue) != Terrain.ExtractContents(m_localAimItemValue))
			{
				if (m_localAimActive)
				{
					SendLocalAimEvent(player, PlayerAimAction.Cancel, m_localAimRay);
				}
				m_localAimSequence = PlayerActionSequencePolicy.Next(m_localAimSequence);
				m_localAimActive = true;
				m_localAimSlot = activeSlot;
				m_localAimItemValue = itemValue;
				m_localAimItemCount = inventory.GetSlotCount(activeSlot);
				m_localAimRay = aim;
				m_lastAimUpdateSentTime = now;
				SendLocalAimEvent(player, PlayerAimAction.Start, aim);
			}
			else
			{
				m_localAimRay = aim;
				if (itemValue != 0 && activeSlot == m_localAimSlot && Terrain.ExtractContents(itemValue) == Terrain.ExtractContents(m_localAimItemValue))
				{
					m_localAimItemValue = itemValue;
					m_localAimItemCount = Math.Max(inventory.GetSlotCount(activeSlot), 1);
				}
				if (now - m_lastAimUpdateSentTime >= 1.0 / 32.0)
				{
					m_lastAimUpdateSentTime = now;
					SendLocalAimEvent(player, PlayerAimAction.Update, aim);
				}
			}
		}
		else if (m_localAimActive)
		{
			bool num = activeSlot == m_localAimSlot;
			bool sameItem = num && ((isAimable && Terrain.ExtractContents(itemValue) == Terrain.ExtractContents(m_localAimItemValue)) || itemValue == 0);
			if (num && itemValue != 0 && Terrain.ExtractContents(itemValue) == Terrain.ExtractContents(m_localAimItemValue))
			{
				m_localAimItemValue = itemValue;
				m_localAimItemCount = Math.Max(inventory.GetSlotCount(activeSlot), 1);
			}
			PlayerAimAction action = sameItem
				? PlayerAimAction.Release
				: PlayerAimAction.Cancel;
			Projectile predictedProjectile = action == PlayerAimAction.Release
				? CompleteLocalAimRelease(player, m_localAimRay)
				: null;
			SendLocalAimEvent(player, action, m_localAimRay, predictedProjectile);
			m_localAimActive = false;
			m_localAimSlot = -1;
			m_localAimItemValue = 0;
			m_localAimItemCount = 0;
		}
	}

	// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
	// Complete the native release while the local projectile can still be captured. The host uses
	// the validated launch vector, but remains authoritative for simulation, collision and damage.
	private Projectile CompleteLocalAimRelease(ComponentPlayer player, Ray3 aim)
	{
		SubsystemProjectiles projectiles = GameManager.Project?
			.FindSubsystem<SubsystemProjectiles>(false);
		if (player?.ComponentMiner == null || projectiles == null) return null;
		var existing = new HashSet<Projectile>(projectiles.Projectiles.Where(item => item != null));
		player.ComponentMiner.Aim(aim, AimState.Completed);
		ModManager.ModParentField.ModifyParentField(player, "m_aim", null,
			typeof(ComponentPlayer));
		SubsystemTime time = GameManager.Project?.FindSubsystem<SubsystemTime>(false);
		if (time != null)
			ModManager.ModParentField.ModifyParentField(player, "m_lastActionTime",
				time.GameTime, typeof(ComponentPlayer));
		return projectiles.Projectiles.FirstOrDefault(projectile => projectile != null &&
			!projectile.ToRemove && !existing.Contains(projectile) &&
			ReferenceEquals(projectile.Owner, player));
	}

	private void SendLocalAimEvent(ComponentPlayer player, PlayerAimAction action, Ray3 aim,
		Projectile predictedProjectile = null)
	{
		Client obj = client;
		if (obj != null && obj.IsConnected && player?.ComponentBody != null)
		{
			bool isRelease = action == PlayerAimAction.Release;
			var message = new PlayerAimMessage(m_localAimSequence, action, aim,
				m_localAimSlot, m_localAimItemValue, m_localAimItemCount,
				player.ComponentBody.Position, player.ComponentBody.Rotation,
				isRelease ? player.ComponentBody.Velocity : Vector3.Zero,
				isRelease ? client.Step : 0);
			if (isRelease && predictedProjectile != null)
			{
				message.HasProjectileLaunch = true;
				message.ProjectileValue = predictedProjectile.Value;
				message.ProjectilePosition = predictedProjectile.Position;
				message.ProjectileVelocity = predictedProjectile.Velocity;
				message.ProjectileAngularVelocity = predictedProjectile.AngularVelocity;
			}
			NetworkMessageSender.SendPlayerAimMessage(message);
		}
	}

	// Source: Survivalcraft/Game/SubsystemSaddleBlockBehavior.cs:HandledBlocks
	// Saddle use is resolved by the native interaction path, never by ComponentMiner.Hit.
	internal static bool IsSaddleActive(ComponentPlayer player)
	{
		IInventory inventory = player?.ComponentMiner?.Inventory;
		int activeSlot = inventory?.ActiveSlotIndex ?? -1;
		if (inventory == null || activeSlot < 0 || activeSlot >= inventory.SlotsCount)
			return false;
		return Terrain.ExtractContents(inventory.GetSlotValue(activeSlot)) == 158;
	}

	private void UpdateLocalHitRequests(ComponentPlayer player, Ray3? hitRay)
	{
		if (IsSaddleActive(player) || !hitRay.HasValue || player?.ComponentMiner == null ||
			player.ComponentCreatureModel == null)
			return;

		// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
		// Hit is a body-only action. Digging has its own request path and must never receive a
		// host melee Miss merely because the same input ray intersects terrain instead of a body.
		BodyRaycastResult? target = player.ComponentMiner.Raycast<BodyRaycastResult>(
			hitRay.Value, RaycastMode.Interaction);
		if (!target.HasValue || Vector3.Distance(target.Value.HitPoint(),
			player.ComponentCreatureModel.EyePosition) > 2f)
			return;

		if (!(Time.RealTime < m_nextLocalHitRequestTime))
		{
			Client obj = client;
			if (obj != null && obj.IsConnected)
			{
				m_nextLocalHitRequestTime = Time.RealTime + 0.36000001430511475;
				m_localHitSequence = PlayerActionSequencePolicy.Next(m_localHitSequence);
				ComponentMiner miner = player?.ComponentMiner;
				if (TryApplyDeterministicMeleeRandomState(miner, obj.ClientID,
					m_localHitSequence, out ulong previousRandomState))
				{
					QueueEndOfFrameAction(() =>
						RestoreDeterministicMeleeRandomState(miner, previousRandomState));
				}
				TrackLocalMeleePrediction(player, m_localHitSequence);
				NetworkMessageSender.SendPlayerHitRequest(new PlayerActionMessage(PlayerActionType.HitRequest, client.ClientID, m_localHitSequence, hitRay.Value));
			}
		}
	}

	private static ulong BuildDeterministicMeleeRandomState(int clientId, int sequence,
		int sessionSeed)
	{
		unchecked
		{
			ulong state = 1469598103934665603UL;
			state ^= (uint)sessionSeed;
			state *= 1099511628211UL;
			state ^= (uint)clientId;
			state *= 1099511628211UL;
			state ^= (uint)sequence;
			state *= 1099511628211UL;
			state ^= state >> 32;
			return state == 0UL ? 1UL : state;
		}
	}

	private bool TryApplyDeterministicMeleeRandomState(ComponentMiner miner, int clientId,
		int sequence, out ulong previousState)
	{
		previousState = 0UL;
		if (miner == null || sequence <= 0) return false;
		Game.Random random = ModManager.ModParentField.GetParentField<Game.Random>(
			miner, "m_random", typeof(ComponentMiner));
		if (random == null) return false;
		previousState = random.State;
		random.State = BuildDeterministicMeleeRandomState(clientId, sequence,
			m_sessionRandomSeed);
		return true;
	}

	private static void RestoreDeterministicMeleeRandomState(ComponentMiner miner,
		ulong previousState)
	{
		if (miner == null) return;
		Game.Random random = ModManager.ModParentField.GetParentField<Game.Random>(
			miner, "m_random", typeof(ComponentMiner));
		if (random != null)
			random.State = previousState;
	}

	private void TrackLocalMeleePrediction(ComponentPlayer player, int sequence)
	{
		ComponentMiner miner = player?.ComponentMiner;
		if (miner != null && sequence > 0)
		{
			int[] array = (from item in m_localMeleePredictions
				where Time.RealTime - item.Value.CreatedTime > 3.0
				select item.Key).ToArray();
			foreach (int expired in array)
			{
				m_localMeleePredictions.Remove(expired);
			}
			m_localMeleePredictions[sequence] = new LocalMeleePrediction
			{
				Miner = miner,
				PreviousHitTime = ModManager.ModParentField.GetParentField<double>(miner, "m_lastHitTime", typeof(ComponentMiner)),
				CreatedTime = Time.RealTime
			};
		}
	}

	private void UpdateLocalInteractRequests(ComponentPlayer player, Ray3? interactRay)
	{
		if (!interactRay.HasValue)
		{
			return;
		}
		Client obj = client;
		if (obj == null || !obj.IsConnected || player?.ComponentMiner?.Inventory == null)
		{
			return;
		}
		IInventory inventory = player.ComponentMiner.Inventory;
		int activeSlot = inventory.ActiveSlotIndex;
		if (activeSlot < 0 || activeSlot >= inventory.SlotsCount)
		{
			return;
		}
		int itemValue = inventory.GetSlotValue(activeSlot);
		int itemCount = inventory.GetSlotCount(activeSlot);
		if (!IsHost && itemValue != 0 && BlocksManager.Blocks[Terrain.ExtractContents(itemValue)] is FurnitureBlock)
		{
			m_worldObjectSynchronizer?.PublishLocalFurnitureChangesNow();
		}
		m_localInteractSequence = PlayerActionSequencePolicy.Next(m_localInteractSequence);
		if (TryGetTerrainUsePrediction(player, interactRay.Value, out var terrainUseCell, out var terrainUseExpectedValue))
		{
			m_localTerrainUsePredictions[terrainUseCell] = new LocalTerrainUsePrediction
			{
				ExpectedValue = terrainUseExpectedValue,
				LastSeenTime = Time.RealTime
			};
		}
		PlayerActionMessage request = new PlayerActionMessage(PlayerActionType.InteractRequest, client.ClientID, m_localInteractSequence, interactRay.Value, activeSlot, itemValue, itemCount);
		if (TryGetTerrainPlacePrediction(player, interactRay.Value, out var cell, out var expectedValue, out var predictedValue))
		{
			request.HasTerrainPrediction = true;
			request.RequestId = m_localInteractSequence;
			request.Cell = cell;
			request.ExpectedValue = expectedValue;
			request.PredictedValue = predictedValue;
			if (m_pendingTerrainPlacePredictionCells.TryGetValue(cell, out var previousRequestId))
			{
				RemovePendingTerrainPlacePrediction(previousRequestId);
			}
			m_pendingTerrainPlacePredictions[request.RequestId] = new PendingTerrainPlacePrediction
			{
				Request = request,
				LocalPredictedValue = predictedValue,
				IsCollapsingBlock = IsCollapsingBlockValue(predictedValue),
				HasLocalPrediction = false,
				LastSendTime = Time.RealTime,
				SendCount = 1
			};
			if (m_pendingTerrainPlacePredictions[request.RequestId].IsCollapsingBlock)
			{
				m_localCollapsingPlacePredictions[cell] = Time.RealTime + 8.0;
			}
			m_pendingTerrainPlacePredictionCells[cell] = request.RequestId;
		}
		NetworkMessageSender.SendPlayerInteractRequest(request);
	}

	private static bool TryGetTerrainUsePrediction(ComponentPlayer player, Ray3 ray, out Point3 cell, out int expectedValue)
	{
		cell = default(Point3);
		expectedValue = 0;
		ComponentMiner miner = player?.ComponentMiner;
		IInventory inventory = miner?.Inventory;
		if (miner == null)
		{
			return false;
		}
		TerrainRaycastResult? hit = miner.Raycast<TerrainRaycastResult>(ray, RaycastMode.Interaction);
		if (!hit.HasValue)
		{
			return false;
		}
		cell = new Point3(hit.Value.CellFace.X, hit.Value.CellFace.Y, hit.Value.CellFace.Z);
		expectedValue = Terrain.ReplaceLight(hit.Value.Value, 0);
		if (BlocksManager.Blocks[Terrain.ExtractContents(hit.Value.Value)] is TrapdoorBlock)
		{
			return true;
		}
		if (inventory == null || inventory.ActiveSlotIndex < 0 || inventory.ActiveSlotIndex >= inventory.SlotsCount || hit.Value.CellFace.Face != 4)
		{
			return false;
		}
		int toolValue = inventory.GetSlotValue(inventory.ActiveSlotIndex);
		if (toolValue == 0 || !(BlocksManager.Blocks[Terrain.ExtractContents(toolValue)] is RakeBlock))
		{
			return false;
		}
		int targetContents = Terrain.ExtractContents(hit.Value.Value);
		if (targetContents != 2 && targetContents != 8)
		{
			return false;
		}
		return true;
	}

	private static bool IsCollapsingBlockValue(int value)
	{
		int contents = Terrain.ExtractContents(value);
		if (contents != 6)
		{
			return contents == 7;
		}
		return true;
	}

	internal void ReconcileAuthoritativeCollapsingCell(Point3 point, int networkValue)
	{
		if (IsHost || !IsCollapsingBlockValue(networkValue))
		{
			return;
		}
		SubsystemMovingBlocks moving = GameManager.Project?.FindSubsystem<SubsystemMovingBlocks>(throwOnError: false);
		if (moving == null)
		{
			return;
		}
		int contents = Terrain.ExtractContents(networkValue);
		IMovingBlockSet[] array = moving.MovingBlockSets.ToArray();
		foreach (IMovingBlockSet set in array)
		{
			if (set != null && !(set.Id != "CollapsingBlock"))
			{
				Point3 origin = Terrain.ToCell(MathUtils.Round(set.Position.X), MathUtils.Round(set.Position.Y), MathUtils.Round(set.Position.Z));
				if (origin.X == point.X && origin.Z == point.Z && set.Blocks.Any((MovingBlock block) => Terrain.ExtractContents(block.Value) == contents))
				{
					moving.RemoveMovingBlockSet(set);
				}
			}
		}
	}

	private void RemoveLocalCollapsingSets(Point3 column, Dictionary<Point3, bool> repairCells)
	{
		SubsystemMovingBlocks moving = GameManager.Project?.FindSubsystem<SubsystemMovingBlocks>(throwOnError: false);
		if (moving == null)
		{
			return;
		}
		IMovingBlockSet[] array = moving.MovingBlockSets.ToArray();
		foreach (IMovingBlockSet set in array)
		{
			if (set == null || set.Id != "CollapsingBlock")
			{
				continue;
			}
			Point3 origin = Terrain.ToCell(MathUtils.Round(set.Position.X), MathUtils.Round(set.Position.Y), MathUtils.Round(set.Position.Z));
			if (origin.X != column.X || origin.Z != column.Z)
			{
				continue;
			}
			if (repairCells != null)
			{
				repairCells[column] = true;
				foreach (MovingBlock block in set.Blocks)
				{
					repairCells[origin + block.Offset] = true;
				}
			}
			moving.RemoveMovingBlockSet(set);
		}
	}

	private static bool TryGetTerrainPlacePrediction(ComponentPlayer player, Ray3 ray, out Point3 cell, out int expectedValue, out int predictedValue)
	{
		cell = default(Point3);
		expectedValue = 0;
		predictedValue = 0;
		ComponentMiner miner = player?.ComponentMiner;
		IInventory inventory = miner?.Inventory;
		SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(throwOnError: false);
		if (miner == null || inventory == null || terrain == null)
		{
			return false;
		}
		int activeSlot = inventory.ActiveSlotIndex;
		if (activeSlot < 0 || activeSlot >= inventory.SlotsCount)
		{
			return false;
		}
		int itemValue = inventory.GetSlotValue(activeSlot);
		if (itemValue == 0 || inventory.GetSlotCount(activeSlot) <= 0)
		{
			return false;
		}
		Block block = BlocksManager.Blocks[Terrain.ExtractContents(itemValue)];
		if (!block.IsPlaceable)
		{
			return false;
		}
		TerrainRaycastResult? hit = miner.Raycast<TerrainRaycastResult>(ray, RaycastMode.Interaction);
		if (!hit.HasValue)
		{
			return false;
		}
		BlockPlacementData placement = block.GetPlacementValue(terrain, miner, itemValue, hit.Value);
		if (placement.Value == 0)
		{
			return false;
		}
		Point3 offset = CellFace.FaceToPoint3(placement.CellFace.Face);
		cell = new Point3(placement.CellFace.X + offset.X, placement.CellFace.Y + offset.Y, placement.CellFace.Z + offset.Z);
		if (!terrain.Terrain.IsCellValid(cell.X, cell.Y, cell.Z))
		{
			return false;
		}
		expectedValue = Terrain.ReplaceLight(terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z), 0);
		predictedValue = Terrain.ReplaceLight(placement.Value, 0);
		return predictedValue != 0;
	}

	private void UpdateLocalDropRequests(ComponentPlayer player, bool drop)
	{
		if (!drop)
		{
			return;
		}
		Client obj = client;
		if (obj == null || !obj.IsConnected || player?.ComponentMiner?.Inventory == null || player.ComponentBody == null)
		{
			return;
		}
		IInventory inventory = player.ComponentMiner.Inventory;
		int activeSlot = inventory.ActiveSlotIndex;
		if (activeSlot >= 0 && activeSlot < inventory.SlotsCount)
		{
			int itemValue = inventory.GetSlotValue(activeSlot);
			int itemCount = inventory.GetSlotCount(activeSlot);
			if (itemValue != 0 && itemCount > 0)
			{
				int dropCount = ActionRequestValidationPolicy.NormalizeDropCountForWire(itemCount);
					m_localDropSequence = PlayerActionSequencePolicy.Next(m_localDropSequence);
				PlayerActionMessage message = new PlayerActionMessage(PlayerActionType.DropRequest, client.ClientID, m_localDropSequence, default(Ray3), activeSlot, itemValue, itemCount)
				{
					DropCount = dropCount,
					RemoveCount = itemCount,
					RequestId = m_localEquipmentRevision,
					Position = player.ComponentBody.Position + new Vector3(0f, player.ComponentBody.StanceBoxSize.Y * 0.66f, 0f) + 0.25f * player.ComponentBody.Matrix.Forward,
					Velocity = 8f * Matrix.CreateFromQuaternion(player.ComponentCreatureModel.EyeRotation).Forward
				};
				message.HasInventoryDelta = true;
				message.InventorySlotIndices = new[] { activeSlot };
				message.InventoryBaseValues = new[] { itemValue };
				message.InventoryBaseCounts = new[] { itemCount };
				message.InventorySlotValues = new[] { itemValue };
				message.InventorySlotCounts = new[] { 0 };
				NetworkMessageSender.SendPlayerDropRequest(message);
				m_pendingLocalDropValue = itemValue;
				m_pendingLocalDropCount = dropCount;
				m_pendingLocalDropPosition = message.Position;
				m_pendingLocalDropPredictionUntil = Time.RealTime + 0.5;
			}
		}
	}

	private void UpdateLocalJumpRequests(bool jump)
	{
		if (jump)
		{
			Client obj = client;
			if (obj != null && obj.IsConnected)
			{
					m_localJumpSequence = PlayerActionSequencePolicy.Next(m_localJumpSequence);
				NetworkMessageSender.SendPlayerJumpRequest(new PlayerActionMessage(PlayerActionType.JumpRequest, client.ClientID, m_localJumpSequence, default(Ray3))
				{
					ServerTick = client.Step
				});
			}
		}
	}

	public bool TryGetNetworkPlayerInput(ComponentPlayer player, out PlayerInput playerInput)
	{
		playerInput = default(PlayerInput);
		if (!IsHost || player == null)
		{
			return false;
		}
		int sourceClientId = m_networkPlayerData.FirstOrDefault((KeyValuePair<int, PlayerData> pair) => pair.Key > 0 && pair.Value == player.PlayerData).Key;
		if (sourceClientId <= 0)
		{
			return false;
		}
		if (!m_networkPlayerInputs.TryGetValue(sourceClientId, out var state) || Time.RealTime - state.LastReceivedTime > 0.75)
		{
			return true;
		}
		while (state.JumpEvents.Count > 0 && Time.RealTime - state.JumpEvents.Peek().ReceivedTime > 1.0)
		{
			state.JumpEvents.Dequeue();
		}
		if (state.JumpEvents.Count > 0)
		{
			state.JumpEvents.Dequeue();
			playerInput = ((state.ConsumedSequence != state.Sequence) ? state.Input : state.HeldInput);
			state.ConsumedSequence = state.Sequence;
			playerInput.Jump = true;
			state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
			return true;
		}
		if (state.DropEvents.Count > 0)
		{
			PlayerActionMessage drop = state.DropEvents.Dequeue();
			playerInput = ((state.ConsumedSequence != state.Sequence) ? state.Input : state.HeldInput);
			state.ConsumedSequence = state.Sequence;
			ApplyInteractionInventory(player.ComponentMiner?.Inventory, drop);
			playerInput.Drop = true;
			state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
			return true;
		}
		if (state.InteractEvents.Count > 0)
		{
			PlayerActionMessage interact = state.InteractEvents.Dequeue();
			ApplyHostActionPose(player, state);
			playerInput = ((state.ConsumedSequence != state.Sequence) ? state.Input : state.HeldInput);
			state.ConsumedSequence = state.Sequence;
			// Source: Survivalcraft/Game/SubsystemSaddleBlockBehavior.cs:OnUse
			// A saddle interaction replaces the target entity and must not leave a same-frame
			// melee request queued behind it.
			if (Terrain.ExtractContents(interact.ItemValue) == 158)
			{
				state.MeleeSuppressedUntil = Time.RealTime + 0.6;
				state.HitEvents.Clear();
			}
			ApplyInteractionInventory(player.ComponentMiner?.Inventory, interact);
			if (interact.HasTerrainPrediction)
			{
				if (!TryGetTerrainPlacePrediction(player, interact.HitRay, out var cell, out var expectedValue, out var _) || !(cell == interact.Cell) || expectedValue != interact.ExpectedValue)
				{
					SendHostTerrainPlaceResult(sourceClientId, interact, accepted: false);
					state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
					return true;
				}
				m_hostTerrainPlaceExecutions.Add(new HostTerrainPlaceExecution
				{
					ClientId = sourceClientId,
					Request = interact,
					PlayerStats = player.PlayerStats,
					PreviousBlocksPlaced = (player.PlayerStats?.BlocksPlaced ?? 0)
				});
			}
			playerInput.Interact = interact.HitRay;
			state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
			return true;
		}
		if (state.AimEvents.Count > 0)
		{
			PlayerAimMessage aimEvent = state.AimEvents.Dequeue();
			if (state.ConsumedSequence != state.Sequence)
			{
				playerInput = state.Input;
				state.ConsumedSequence = state.Sequence;
			}
			else
			{
				playerInput = state.HeldInput;
			}
			if (aimEvent.Action == PlayerAimAction.Start || aimEvent.Action == PlayerAimAction.Update)
			{
				state.HeldAim = aimEvent.Aim;
				playerInput.Aim = aimEvent.Aim;
			}
			else
			{
				state.HeldAim = null;
				playerInput.Aim = null;
				state.LastCompletedAimSequence = Math.Max(state.LastCompletedAimSequence, aimEvent.Sequence);
				state.QueuedAimCompletions.Remove(aimEvent.Sequence);
				if (aimEvent.Action == PlayerAimAction.Cancel)
				{
					player.ComponentMiner?.Aim(aimEvent.Aim, AimState.Cancelled);
					ModManager.ModParentField.ModifyParentField(player, "m_aim", null, typeof(ComponentPlayer));
				}
				state.ActiveAimSequence = -1;
				state.ActiveAimSlotIndex = -1;
				state.ActiveAimItemValue = 0;
				state.ActiveAimItemCount = 0;
			}
			state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
			return true;
		}
		if (state.HitEvents.Count > 0 && Time.RealTime < state.MeleeSuppressedUntil)
		{
			state.HitEvents.Clear();
			if (state.ConsumedSequence != state.Sequence)
			{
				playerInput = state.Input;
				state.ConsumedSequence = state.Sequence;
				state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
			}
			else
			{
				playerInput = state.HeldInput;
			}
			playerInput.Hit = null;
			return true;
		}
		if (state.HitEvents.Count > 0 && Time.RealTime >= state.NextHitExecutionTime)
		{
			PlayerActionMessage hit = state.HitEvents.Dequeue();
			ApplyHostActionPose(player, state);
			ComponentMiner miner = player?.ComponentMiner;
			if (TryApplyDeterministicMeleeRandomState(miner, sourceClientId,
				hit.Sequence, out ulong previousRandomState))
			{
				QueueEndOfFrameAction(() =>
					RestoreDeterministicMeleeRandomState(miner, previousRandomState));
			}
			playerInput = ((state.ConsumedSequence != state.Sequence) ? state.Input : state.HeldInput);
			state.ConsumedSequence = state.Sequence;
			BodyRaycastResult? target = miner?.Raycast<BodyRaycastResult>(hit.HitRay, RaycastMode.Interaction);
			if (target.HasValue && player.ComponentCreatureModel != null && Vector3.Distance(target.Value.HitPoint(), player.ComponentCreatureModel.EyePosition) <= 2f)
			{
				ComponentHealth health = target.Value.ComponentBody?.Entity?.FindComponent<ComponentHealth>();
				if (health != null)
				{
					m_hostMeleeHitExecutions.Add(new HostMeleeHitExecution
					{
						ClientId = sourceClientId,
						RequestSequence = hit.Sequence,
						TargetHealth = health,
						PreviousHealth = health.Health,
						HitPoint = target.Value.HitPoint(),
						HitDirection = hit.HitRay.Direction,
						AttackerVelocity = (player.ComponentBody?.Velocity ?? Vector3.Zero)
					});
					playerInput.Hit = hit.HitRay;
					state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
					state.NextHitExecutionTime = Time.RealTime + 0.36000001430511475;
					return true;
				}
			}
			m_hostMeleeHitExecutions.Add(new HostMeleeHitExecution
			{
				ClientId = sourceClientId,
				RequestSequence = hit.Sequence,
				TargetHealth = null,
				PreviousHealth = 0f,
				HitPoint = target.HasValue ? target.Value.HitPoint() :
					(player.ComponentCreatureModel != null
						? player.ComponentCreatureModel.EyePosition + 0.75f *
							player.ComponentBody.Matrix.Forward
						: player.ComponentBody.Position + 0.75f *
							player.ComponentBody.Matrix.Forward),
				HitDirection = hit.HitRay.Direction,
				AttackerVelocity = (player.ComponentBody?.Velocity ?? Vector3.Zero)
			});
			playerInput.Hit = hit.HitRay;
			state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
			state.NextHitExecutionTime = Time.RealTime + 0.36000001430511475;
			return true;
		}
		if (state.ConsumedSequence != state.Sequence)
		{
			player.ComponentBody.Rotation = state.BodyRotation;
			ModManager.ModParentField.ModifyParentField(player.ComponentLocomotion, "m_lookAngles", state.LookAngles, typeof(ComponentLocomotion));
			playerInput = state.Input;
			playerInput.Aim = null;
			state.ConsumedSequence = state.Sequence;
			state.HeldInput = PlayerInputStatePolicy.CreateHeld(playerInput);
		}
		else
		{
			playerInput = state.HeldInput;
		}
		playerInput.Aim = null;
		return true;
	}

	private static void ApplyHostActionPose(ComponentPlayer player, NetworkPlayerInputState state)
	{
		ComponentBody body = player?.ComponentBody;
		if (body != null && state != null && IsFinite(state.BodyPosition) && !(Time.RealTime - state.LastReceivedTime > 0.75))
		{
			Vector3 correction = state.BodyPosition - body.Position;
			float distance = correction.Length();
			if (distance > 2f)
			{
				correction *= 2f / distance;
			}
			body.Position += correction;
			body.Rotation = state.BodyRotation;
			if (player.ComponentLocomotion != null)
			{
				ModManager.ModParentField.ModifyParentField(player.ComponentLocomotion, "m_lookAngles", state.LookAngles, typeof(ComponentLocomotion));
			}
		}
	}

	private void CompleteHostTerrainPlaceExecutions()
	{
		if (!IsHost || m_hostTerrainPlaceExecutions.Count == 0)
		{
			return;
		}
		HostTerrainPlaceExecution[] array = m_hostTerrainPlaceExecutions.ToArray();
		foreach (HostTerrainPlaceExecution execution in array)
		{
			SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(throwOnError: false);
			bool accepted = ((terrain == null) ? execution.Request.ExpectedValue : Terrain.ReplaceLight(terrain.Terrain.GetCellValue(execution.Request.Cell.X, execution.Request.Cell.Y, execution.Request.Cell.Z), 0)) != execution.Request.ExpectedValue || execution.PlayerStats?.BlocksPlaced > execution.PreviousBlocksPlaced;
			SendHostTerrainPlaceResult(execution.ClientId, execution.Request, accepted);
			if (accepted)
			{
				// Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.Update
				// Finish the native placement/fluids closure before the next weather scan. This
				// preserves the original scanner cadence while preventing a transient water cell
				// from being frozen before a sand or soil placement settles on the host.
				terrain?.TerrainUpdater?.RequestSynchronousUpdate();
				(terrain as SuSubsystemTerrain)?.FlushHostModifiedCellClosureForNetworkAction();
				PublishServerAudit("terrain.place", execution.ClientId, "cell=" + execution.Request.Cell.X.ToString(CultureInfo.InvariantCulture) + "," + execution.Request.Cell.Y.ToString(CultureInfo.InvariantCulture) + "," + execution.Request.Cell.Z.ToString(CultureInfo.InvariantCulture));
			}
		}
		m_hostTerrainPlaceExecutions.Clear();
	}

	private void CompleteHostMeleeHitExecutions()
	{
		if (!IsHost || m_hostMeleeHitExecutions.Count == 0)
		{
			return;
		}
		foreach (HostMeleeHitExecution execution in m_hostMeleeHitExecutions)
		{
			SendAuthoritativeMeleeHitResult(execution.ClientId, execution.RequestSequence, execution.TargetHealth, execution.PreviousHealth, execution.HitPoint, execution.HitDirection, execution.AttackerVelocity);
		}
		m_hostMeleeHitExecutions.Clear();
	}

    private void SendAuthoritativeMeleeHitResult(int targetClientId, int requestSequence, ComponentHealth targetHealth, float previousHealth, Vector3 hitPoint, Vector3 hitDirection, Vector3 attackerVelocity)
    {
        if (IsHost && targetClientId > 0 && requestSequence > 0)
        {
            float damage = targetHealth != null
                ? (previousHealth - targetHealth.Health) * targetHealth.AttackResilience
                : 0f;
            Vector3 direction = ((hitDirection.LengthSquared() > 0.0001f) ? Vector3.Normalize(hitDirection) : Vector3.UnitZ);
            NetworkMessageSender.SendMeleeHitResult(targetClientId,
                new MeleeHitResultMessage(requestSequence, client.Step, hitPoint, direction,
                    attackerVelocity, damage)
                {
                    ResultKind = targetHealth == null
                        ? MeleeHitResultMessage.MeleeHitResultKind.Miss
                        : (damage > 0f
                            ? MeleeHitResultMessage.MeleeHitResultKind.Hit
                            : MeleeHitResultMessage.MeleeHitResultKind.Miss)
                });
        }
    }

    private void SendRejectedMeleeHitResult(int targetClientId, int requestSequence,
        Vector3 hitPoint, Vector3 hitDirection, Vector3 attackerVelocity)
    {
        if (IsHost && targetClientId > 0 && requestSequence > 0)
        {
            Vector3 direction = hitDirection.LengthSquared() > 0.0001f
                ? Vector3.Normalize(hitDirection)
                : Vector3.UnitZ;
            NetworkMessageSender.SendMeleeHitResult(targetClientId,
                new MeleeHitResultMessage(requestSequence, client.Step, hitPoint, direction,
                    attackerVelocity, 0f)
                {
                    ResultKind = MeleeHitResultMessage.MeleeHitResultKind.Rejected
                });
        }
    }

	private void SendHostTerrainPlaceResult(int targetClientId, PlayerActionMessage request, bool accepted)
	{
		SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(throwOnError: false);
		int authoritativeValue = ((terrain == null || !terrain.Terrain.IsCellValid(request.Cell.X, request.Cell.Y, request.Cell.Z)) ? request.ExpectedValue : Terrain.ReplaceLight(terrain.Terrain.GetCellValue(request.Cell.X, request.Cell.Y, request.Cell.Z), 0));
		PlayerActionMessage result = new PlayerActionMessage(PlayerActionType.InteractResult, targetClientId, request.Sequence, default(Ray3))
		{
			RequestId = request.RequestId,
			Cell = request.Cell,
			Accepted = accepted,
			AuthoritativeValue = authoritativeValue,
			ServerTick = client.Step
		};
		long requestKey = ((long)targetClientId << 32) | (uint)request.RequestId;
		if (PlayerActionSequencePolicy.ShouldTrimCache(
			m_processedTerrainPlaceRequests.Count, 2048))
		{
			m_processedTerrainPlaceRequests.Clear();
		}
		m_processedTerrainPlaceRequests[requestKey] = result;
		NetworkMessageSender.SendPlayerInteractResult(targetClientId, result);
		MarkHostInventoryAuthoritative(targetClientId);
		m_forceHostInventorySync = true;
	}

	private void HandleTerrainPlaceResult(PlayerActionMessage result, int sourceClientId)
	{
		if (!IsHost && sourceClientId == 0 && result != null && m_pendingTerrainPlacePredictions.TryGetValue(result.RequestId, out var prediction) && !(prediction.Request.Cell != result.Cell))
		{
			Dictionary<Point3, bool> cells = new Dictionary<Point3, bool> { [result.Cell] = true };
			List<int> values = new List<int> { result.AuthoritativeValue };
			bool num = result.Accepted && prediction.IsCollapsingBlock && m_localCollapsingPlacePredictions.ContainsKey(result.Cell);
			RemovePendingTerrainPlacePrediction(result.RequestId);
			if (!num)
			{
				m_localCollapsingPlacePredictions.Remove(result.Cell);
				SuSubsystemTerrain.EnqueuePriorityNetworkBatch(new GameModifiedCellsMessage(cells, values, result.ServerTick, isCatchUp: true, client.ClientID, 0L));
			}
		}
	}

	private void RemovePendingTerrainPlacePrediction(int requestId)
	{
		if (m_pendingTerrainPlacePredictions.TryGetValue(requestId, out var prediction))
		{
			m_pendingTerrainPlacePredictions.Remove(requestId);
			if (m_pendingTerrainPlacePredictionCells.TryGetValue(prediction.Request.Cell, out var mappedRequestId) && mappedRequestId == requestId)
			{
				m_pendingTerrainPlacePredictionCells.Remove(prediction.Request.Cell);
			}
		}
	}

	private void HandlePlayerAimMessage(PlayerAimMessage message, int sourceClientId)
	{
		if (!IsHost || message == null || sourceClientId <= 0 || !m_networkPlayerData.TryGetValue(sourceClientId, out var playerData) || playerData?.ComponentPlayer == null)
		{
			return;
		}
		ComponentPlayer player = playerData.ComponentPlayer;
		IInventory inventory = player.ComponentMiner?.Inventory;
		if (inventory == null || message.ActiveSlotIndex < 0 || message.ActiveSlotIndex >= inventory.SlotsCount || message.ItemCount <= 0 || !BlocksManager.Blocks[Terrain.ExtractContents(message.ItemValue)].IsAimable)
		{
			return;
		}
		if (!m_networkPlayerInputs.TryGetValue(sourceClientId, out var state))
		{
			state = new NetworkPlayerInputState();
			m_networkPlayerInputs[sourceClientId] = state;
		}
		state.LastReceivedTime = Time.RealTime;
		if (message.Sequence <= state.LastCompletedAimSequence)
		{
			return;
		}
		player.ComponentBody.Rotation = message.BodyRotation;
		if (message.Action == PlayerAimAction.Start || message.Action == PlayerAimAction.Update)
		{
			if (state.ActiveAimSequence != message.Sequence)
			{
				SubsystemTime subsystemTime = GameManager.Project?.FindSubsystem<SubsystemTime>(throwOnError: false);
				SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(throwOnError: false);
				if (subsystemTime == null || gameInfo == null)
				{
					return;
				}
				double lastActionTime = ModManager.ModParentField.GetParentField<double>(player, "m_lastActionTime", typeof(ComponentPlayer));
				float requiredDelay = ((gameInfo.WorldSettings.GameMode == GameMode.Creative) ? 0.1f : 1.4f);
				if (subsystemTime.GameTime - lastActionTime <= (double)requiredDelay)
				{
					return;
				}
				ApplyAimReservation(inventory, message);
				state.ActiveAimSequence = message.Sequence;
				state.ActiveAimSlotIndex = message.ActiveSlotIndex;
				state.ActiveAimItemValue = message.ItemValue;
				state.ActiveAimItemCount = message.ItemCount;
				inventory.ActiveSlotIndex = message.ActiveSlotIndex;
			}
			else
			{
				if (message.ActiveSlotIndex != state.ActiveAimSlotIndex || Terrain.ExtractContents(message.ItemValue) != Terrain.ExtractContents(state.ActiveAimItemValue))
				{
					return;
				}
				inventory.ActiveSlotIndex = state.ActiveAimSlotIndex;
			}
			state.HeldAim = message.Aim;
			if (player.ComponentMiner.Aim(message.Aim, AimState.InProgress))
			{
				player.ComponentMiner.Aim(message.Aim, AimState.Cancelled);
				CompleteHostAimLifecycle(player, state, message.Sequence, updateLastActionTime: false);
			}
			else if (state.ActiveAimSequence == message.Sequence && inventory.ActiveSlotIndex == state.ActiveAimSlotIndex && inventory.GetSlotCount(state.ActiveAimSlotIndex) > 0 && Terrain.ExtractContents(inventory.GetSlotValue(state.ActiveAimSlotIndex)) == Terrain.ExtractContents(state.ActiveAimItemValue))
			{
				state.ActiveAimItemValue = inventory.GetSlotValue(state.ActiveAimSlotIndex);
				state.ActiveAimItemCount = inventory.GetSlotCount(state.ActiveAimSlotIndex);
			}
			return;
		}
		if (state.ActiveAimSequence != message.Sequence || message.ActiveSlotIndex != state.ActiveAimSlotIndex || Terrain.ExtractContents(message.ItemValue) != Terrain.ExtractContents(state.ActiveAimItemValue))
		{
			if (message.Action != PlayerAimAction.Release)
			{
				return;
			}
			SubsystemTime releaseTime = GameManager.Project?.FindSubsystem<SubsystemTime>(throwOnError: false);
			SubsystemGameInfo releaseInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(throwOnError: false);
			if (releaseTime == null || releaseInfo == null)
			{
				return;
			}
			double lastActionTime2 = ModManager.ModParentField.GetParentField<double>(player, "m_lastActionTime", typeof(ComponentPlayer));
			float requiredDelay2 = ((releaseInfo.WorldSettings.GameMode == GameMode.Creative) ? 0.1f : 1.4f);
			if (releaseTime.GameTime - lastActionTime2 <= (double)requiredDelay2)
			{
				CompleteHostAimLifecycle(player, state, message.Sequence, updateLastActionTime: false);
				return;
			}
			state.ActiveAimSequence = message.Sequence;
			state.ActiveAimSlotIndex = message.ActiveSlotIndex;
			state.ActiveAimItemValue = message.ItemValue;
			state.ActiveAimItemCount = message.ItemCount;
			state.HeldAim = message.Aim;
		}
		int authoritativeItemValue = state.ActiveAimItemValue;
		int authoritativeItemCount = state.ActiveAimItemCount;
		if (message.Action == PlayerAimAction.Release && message.ActiveSlotIndex == state.ActiveAimSlotIndex && Terrain.ExtractContents(message.ItemValue) == Terrain.ExtractContents(authoritativeItemValue) && IsCockedMusketValue(message.ItemValue) && !IsCockedMusketValue(authoritativeItemValue))
		{
			authoritativeItemValue = message.ItemValue;
			authoritativeItemCount = Math.Max(message.ItemCount, 1);
			state.ActiveAimItemValue = authoritativeItemValue;
			state.ActiveAimItemCount = authoritativeItemCount;
		}
		PlayerAimMessage authoritativeMessage = new PlayerAimMessage(message.Sequence,
			message.Action, message.Aim, state.ActiveAimSlotIndex, authoritativeItemValue,
			authoritativeItemCount, message.BodyPosition, message.BodyRotation,
			message.BodyVelocity, message.ClientTick)
		{
			HasProjectileLaunch = message.HasProjectileLaunch,
			ProjectileValue = message.ProjectileValue,
			ProjectilePosition = message.ProjectilePosition,
			ProjectileVelocity = message.ProjectileVelocity,
			ProjectileAngularVelocity = message.ProjectileAngularVelocity
		};
		ApplyAimReservation(inventory, authoritativeMessage);
		if (message.Action == PlayerAimAction.Release)
		{
			ExecuteHostAimRelease(player, authoritativeMessage);
			NormalizeCrossbowSlot(inventory, state.ActiveAimSlotIndex);
			MarkHostInventoryAuthoritative(sourceClientId);
			m_forceHostInventorySync = true;
		}
		else
		{
			player.ComponentMiner.Aim(message.Aim, AimState.Cancelled);
		}
		CompleteHostAimLifecycle(player, state, message.Sequence, message.Action == PlayerAimAction.Release);
	}

	private void ExecuteHostAimRelease(ComponentPlayer player, PlayerAimMessage message)
	{
		SubsystemProjectiles projectiles = GameManager.Project?.FindSubsystem<SubsystemProjectiles>(throwOnError: false);
		HashSet<Projectile> existingProjectiles = ((projectiles != null) ? new HashSet<Projectile>(projectiles.Projectiles) : null);
		Vector3 musketMuzzlePosition;
		Vector3 musketDirection;
		bool broadcastMusketFire = TryGetMusketFireEffect(player, message, out musketMuzzlePosition, out musketDirection);
		ComponentBody body = player.ComponentBody;
		Vector3 previousVelocity = body.Velocity;
		bool useReleaseVelocity = IsFinite(message.BodyVelocity) && message.BodyVelocity.LengthSquared() <= 4096f;
		if (useReleaseVelocity)
		{
			body.Velocity = message.BodyVelocity;
		}
		try
		{
			player.ComponentMiner.Aim(message.Aim, AimState.Completed);
		}
		finally
		{
			if (useReleaseVelocity)
			{
				body.Velocity = previousVelocity;
			}
		}
		if (broadcastMusketFire)
		{
			NetworkMessageSender.SendScheduledMessage(-1, new PlayerActionMessage(PlayerActionType.MusketFire, GetProjectileOwnerClientIdForPlayer(player), message.Sequence, default(Ray3))
			{
				Position = musketMuzzlePosition,
				Velocity = musketDirection
			}, sequenced: true, latest: false, batchable: false);
		}
		if (projectiles == null || existingProjectiles == null)
		{
			return;
		}
		int delaySteps = ((message.ClientTick > 0) ? MathUtils.Clamp(client.Step - message.ClientTick, 0, 25) : 0);
		ComponentCreature owner = player.ComponentMiner.ComponentCreature;
		foreach (Projectile projectile in projectiles.Projectiles)
		{
			if (projectile != null && !existingProjectiles.Contains(projectile) && projectile.Owner == owner)
			{
				ApplyValidatedClientProjectileLaunch(player, message, projectile);
				if (delaySteps > 0)
				{
					m_hostProjectileReleaseCompensationSteps[projectile] = delaySteps;
				}
				BroadcastNewHostProjectile(projectile);
			}
		}
	}

	// Source: Survivalcraft/Game/SubsystemBowBlockBehavior.cs:SubsystemBowBlockBehavior.OnAim
	// Source: Survivalcraft/Game/SubsystemCrossbowBlockBehavior.cs:SubsystemCrossbowBlockBehavior.OnAim
	// Both sides run native sway and arrow spread independently. Preserve the client's actual
	// presentation launch only after validating it against the projectile created by the host.
	private static void ApplyValidatedClientProjectileLaunch(ComponentPlayer player,
		PlayerAimMessage message, Projectile projectile)
	{
		if (player?.ComponentCreatureModel == null || message?.HasProjectileLaunch != true ||
			projectile == null || projectile.Value != message.ProjectileValue ||
			!IsFinite(message.Aim.Position) || !IsFinite(message.Aim.Direction) ||
			!IsFinite(message.BodyVelocity) ||
			!IsFinite(message.ProjectilePosition) ||
			!IsFinite(message.ProjectileVelocity) ||
			!IsFinite(message.ProjectileAngularVelocity))
			return;
		int itemContents = Terrain.ExtractContents(message.ItemValue);
		if (itemContents != 191 && itemContents != 200) return;
		if (Terrain.ExtractContents(message.ProjectileValue) != 192 ||
			Vector3.DistanceSquared(message.ProjectilePosition,
				player.ComponentCreatureModel.EyePosition) > 5.0625f ||
			message.ProjectileVelocity.LengthSquared() > 16384f ||
			message.ProjectileAngularVelocity.LengthSquared() > 160000f)
			return;
		Vector3 launchVelocity = message.ProjectileVelocity - message.BodyVelocity;
		Vector3 hostLaunchVelocity = projectile.Velocity - message.BodyVelocity;
		float hostLaunchSpeed = hostLaunchVelocity.Length();
		if (Math.Abs(launchVelocity.Length() - hostLaunchSpeed) >
			MathUtils.Max(2f, 0.15f * hostLaunchSpeed))
			return;
		if (launchVelocity.LengthSquared() > 1f && message.Aim.Direction.LengthSquared() > 0.0001f &&
			Vector3.Dot(Vector3.Normalize(launchVelocity),
				Vector3.Normalize(message.Aim.Direction)) < 0.6f)
			return;
		projectile.Position = message.ProjectilePosition;
		projectile.Velocity = message.ProjectileVelocity;
		projectile.AngularVelocity = message.ProjectileAngularVelocity;
	}

	private static bool TryGetMusketFireEffect(ComponentPlayer player, PlayerAimMessage message, out Vector3 muzzlePosition, out Vector3 direction)
	{
		muzzlePosition = Vector3.Zero;
		direction = Vector3.Zero;
		if (player?.ComponentMiner == null || player.ComponentCreatureModel == null || player.ComponentBody == null || message == null || !(BlocksManager.Blocks[Terrain.ExtractContents(message.ItemValue)] is MusketBlock))
		{
			return false;
		}
		int data = Terrain.ExtractData(message.ItemValue);
		MusketBlock.LoadState loadState = MusketBlock.GetLoadState(data);
		if (!MusketBlock.GetHammerState(data) || loadState == MusketBlock.LoadState.Empty || player.ComponentBody.ImmersionFactor > 0.4f || message.Aim.Direction.LengthSquared() <= 0.0001f)
		{
			return false;
		}
		muzzlePosition = player.ComponentCreatureModel.EyePosition + player.ComponentBody.Matrix.Right * 0.3f - player.ComponentBody.Matrix.Up * 0.2f;
		direction = Vector3.Normalize(message.Aim.Direction);
		return true;
	}

	private int GetProjectileOwnerClientIdForPlayer(ComponentPlayer player)
	{
		foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
		{
			if (item.Value?.ComponentPlayer == player)
			{
				return item.Key;
			}
		}
		return 0;
	}

	private void CompleteHostAimLifecycle(ComponentPlayer player, NetworkPlayerInputState state, int sequence, bool updateLastActionTime)
	{
		state.LastCompletedAimSequence = Math.Max(state.LastCompletedAimSequence, sequence);
		state.HeldAim = null;
		state.HeldInput.Aim = null;
		state.ActiveAimSequence = -1;
		state.ActiveAimSlotIndex = -1;
		state.ActiveAimItemValue = 0;
		state.ActiveAimItemCount = 0;
		if (updateLastActionTime)
		{
			SubsystemTime subsystemTime = GameManager.Project?.FindSubsystem<SubsystemTime>(throwOnError: false);
			if (subsystemTime != null)
			{
				ModManager.ModParentField.ModifyParentField(player, "m_lastActionTime", subsystemTime.GameTime, typeof(ComponentPlayer));
			}
		}
	}

	private static void ApplyAimReservation(IInventory inventory, PlayerAimMessage message)
	{
		inventory.ActiveSlotIndex = message.ActiveSlotIndex;
		inventory.RemoveSlotItems(message.ActiveSlotIndex, int.MaxValue);
		inventory.AddSlotItems(message.ActiveSlotIndex, message.ItemValue, message.ItemCount);
		NormalizeCrossbowSlot(inventory, message.ActiveSlotIndex);
	}

	private static bool IsCockedMusketValue(int value)
	{
		if (value == 0)
		{
			return false;
		}
		if (BlocksManager.Blocks[Terrain.ExtractContents(value)] is MusketBlock)
		{
			return MusketBlock.GetHammerState(Terrain.ExtractData(value));
		}
		return false;
	}

	private void HandlePlayerActionMessage(PlayerActionMessage message, int sourceClientId)
	{
		if (message == null)
		{
			return;
		}
		PlayerData playerData;
		if (message.Action == PlayerActionType.InteractResult)
		{
			HandleTerrainPlaceResult(message, sourceClientId);
		}
		else if (message.Action == PlayerActionType.RespawnRequest)
		{
			if (IsHost)
			{
				if (sourceClientId > 0 && message.PlayerIndex == sourceClientId && ResetNetworkPlayerAfterRespawn(sourceClientId, message.Position, requireDead: true, message.Sequence))
				{
					NetworkMessageSender.BroadcastPlayerRespawn(message);
					SendGamePlayerHealthMessage(force: true);
					PublishServerAudit("player.respawn", sourceClientId, null);
				}
			}
			else if (sourceClientId == 0 && message.PlayerIndex != client.ClientID)
			{
				ResetNetworkPlayerAfterRespawn(message.PlayerIndex, message.Position, requireDead: false, message.Sequence);
			}
		}
		else if (message.Action == PlayerActionType.LeaveRequest)
		{
			if (sourceClientId < 0 || message.PlayerIndex != sourceClientId || sourceClientId == client.ClientID)
			{
				return;
			}
			if (!IsHost && sourceClientId == 0)
			{
				HandleHostDisconnected();
				return;
			}
			QueueEndOfFrameAction(delegate
			{
				m_circuitSynchronizer?.NotifyClientDeparted(sourceClientId);
				if (!IsHost)
				{
					m_departedRemoteClientIds.Add(sourceClientId);
				}
				RemoveNetworkPlayer(sourceClientId);
				playerMappingManager.ReleasePlayerIndex(sourceClientId);
			});
		}
		else if (!IsHost && message.Action == PlayerActionType.MusketFire)
		{
			if (sourceClientId == 0 && message.PlayerIndex != client.ClientID && IsFinite(message.Position) && IsFinite(message.Velocity) && !(message.Velocity.LengthSquared() <= 0.0001f))
			{
				SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(throwOnError: false);
				GameManager.Project?.FindSubsystem<SubsystemAudio>(throwOnError: false)?.PlaySound("Audio/MusketFire", 1f, m_audioEventRandom.Float(-0.1f, 0.1f), message.Position, 10f, autoDelay: true);
				GameManager.Project?.FindSubsystem<SubsystemParticles>(throwOnError: false)?.AddParticleSystem(new GunSmokeParticleSystem(terrain, message.Position + 0.3f * Vector3.Normalize(message.Velocity), Vector3.Normalize(message.Velocity)));
			}
		}
		else if (!IsHost && message.Action == PlayerActionType.Whistle)
		{
			if (sourceClientId == 0 && message.PlayerIndex != client.ClientID && IsFinite(message.Position) && (!m_playerWhistleSequences.TryGetValue(message.PlayerIndex, out var lastSequence) || message.Sequence > lastSequence))
			{
				m_playerWhistleSequences[message.PlayerIndex] = message.Sequence;
				GameManager.Project?.FindSubsystem<SubsystemAudio>(throwOnError: false)?.PlayRandomSound("Audio/Whistle", 1f, m_audioEventRandom.Float(-0.2f, 0f), message.Position, 4f, autoDelay: true);
			}
		}
		else if (IsHost)
		{
			if (!ActionRequestValidationPolicy.IsSupportedHostRequest(message,
				sourceClientId, m_networkPlayerData.ContainsKey(sourceClientId)))
			{
				return;
			}
			if (!m_networkPlayerInputs.TryGetValue(sourceClientId, out var state))
			{
				state = new NetworkPlayerInputState();
				m_networkPlayerInputs[sourceClientId] = state;
			}
			state.LastReceivedTime = Time.RealTime;
			if (message.Action == PlayerActionType.InteractRequest && message.HasTerrainPrediction)
			{
				if (!ActionRequestValidationPolicy.IsTerrainPredictionRequest(message,
					sourceClientId))
				{
					return;
				}
				long requestKey = ((long)sourceClientId << 32) | (uint)message.RequestId;
				if (m_processedTerrainPlaceRequests.TryGetValue(requestKey, out var previousResult))
				{
					NetworkMessageSender.SendPlayerInteractResult(sourceClientId, previousResult);
					return;
				}
				if (m_hostTerrainPlaceExecutions.Any((HostTerrainPlaceExecution item) => item.ClientId == sourceClientId && item.Request.RequestId == message.RequestId))
				{
					return;
				}
			}
			if (message.Action == PlayerActionType.DropRequest)
			{
				if (ActionRequestValidationPolicy.IsDropRequest(message, sourceClientId,
					state.LastDropSequence))
				{
					state.LastDropSequence = message.Sequence;
					bool num = ExecuteHostDropRequest(sourceClientId, message);
					PublishPlayerInventoryAuthority(sourceClientId, m_networkPlayerData.TryGetValue(sourceClientId, out var dropPlayer) ? dropPlayer.ComponentPlayer : null, message.RequestId);
					if (num)
					{
						PublishServerAudit("item.drop", sourceClientId, "count=" + message.DropCount.ToString(CultureInfo.InvariantCulture));
					}
				}
			}
			else if (message.Action == PlayerActionType.JumpRequest)
			{
				if (!ActionRequestValidationPolicy.IsJumpRequest(message, sourceClientId,
					state.LastJumpSequence))
				{
					return;
				}
				state.LastJumpSequence = message.Sequence;
				long inputLagSteps = (long)state.ClientTick - (long)message.ServerTick;
				if (state.ClientTick <= 0 || inputLagSteps <= 50)
				{
					while (state.JumpEvents.Count >= 4)
					{
						state.JumpEvents.Dequeue();
					}
					state.JumpEvents.Enqueue(new PendingNetworkJump
					{
						Message = message,
						ReceivedTime = Time.RealTime
					});
				}
			}
			else if (message.Action == PlayerActionType.InteractRequest)
			{
				if (ActionRequestValidationPolicy.IsNewInteractRequest(message,
					state.LastInteractSequence))
				{
					message.ItemValue = m_worldObjectSynchronizer?.RemapFurnitureValue(sourceClientId, message.ItemValue) ?? message.ItemValue;
					if (message.HasTerrainPrediction)
					{
						message.PredictedValue = m_worldObjectSynchronizer?.RemapFurnitureValue(sourceClientId, message.PredictedValue) ?? message.PredictedValue;
					}
					state.LastInteractSequence = message.Sequence;
					state.InteractEvents.Enqueue(message);
				}
			}
			else if (ActionRequestValidationPolicy.IsNewHitRequest(message,
				state.LastHitSequence))
			{
				state.LastHitSequence = message.Sequence;
				state.HitEvents.Enqueue(message);
			}
			else if (message.Action == PlayerActionType.HitRequest)
			{
				SendRejectedMeleeHitResult(sourceClientId, message.Sequence,
					message.HitRay.Position, message.HitRay.Direction, Vector3.Zero);
			}
		}
		else if (sourceClientId == 0 && message.Action == PlayerActionType.Poke && message.PlayerIndex != client.ClientID && m_networkPlayerData.TryGetValue(message.PlayerIndex, out playerData))
		{
			double now = Time.RealTime;
			NetworkPlayerState playerState;
			bool num2 = RemotePlayers.TryGetValue(message.PlayerIndex, out playerState);
			if (!num2 || now - playerState.LastPokeEventTime > 0.1)
			{
				playerData.ComponentPlayer?.ComponentMiner?.Poke(forceRestart: true);
			}
			if (num2)
			{
				playerState.PokingPhase = 0.0001f;
				playerState.LastPokeEventTime = now;
			}
		}
	}

	private bool ResetNetworkPlayerAfterRespawn(int playerClientId, Vector3 position, bool requireDead, int sequence)
	{
		if (!m_networkPlayerData.TryGetValue(playerClientId, out var playerData) || playerData?.ComponentPlayer == null || !IsFinite(position))
		{
			return false;
		}
		ComponentPlayer player = playerData.ComponentPlayer;
		ComponentHealth health = player.ComponentHealth;
		if (health == null || (requireDead && health.Health > 0f))
		{
			return false;
		}
		if (!m_networkPlayerInputs.TryGetValue(playerClientId, out var state))
		{
			state = new NetworkPlayerInputState();
			m_networkPlayerInputs[playerClientId] = state;
		}
		if (sequence <= state.LastRespawnSequence)
		{
			return false;
		}
		state.LastRespawnSequence = sequence;
		// Source: ScMultiplayerProfileHandlers.cs:UpdateNetworkPlayerRespawnAnchor
		// The client reports that its local respawn entity exists, but the host alone chooses the
		// location. Never let a request position replace the persisted network-player anchor.
		Vector3 spawnPosition = playerData.SpawnPosition;
		if (spawnPosition == Vector3.Zero && m_clientRecordKeys.TryGetValue(playerClientId,
			out string anchorRecordKey) && m_playerRecords.TryGetValue(anchorRecordKey,
			out NetworkPlayerRecord record) && record?.SpawnPosition != Vector3.Zero)
		{
			spawnPosition = record.SpawnPosition;
		}
		if (spawnPosition == Vector3.Zero)
		{
			spawnPosition = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false)?
				.GlobalSpawnPosition ?? Vector3.Zero;
		}
		player.ComponentBody.Position = spawnPosition;
		player.ComponentBody.Velocity = Vector3.Zero;
		player.ComponentBody.TargetCrouchFactor = 0f;
		playerData.SpawnPosition = spawnPosition;
		ModManager.ModParentField.ModifyParentField(health, "<Health>k__BackingField", 1f, typeof(ComponentHealth));
		ModManager.ModParentField.ModifyParentField(health, "<Air>k__BackingField", 1f, typeof(ComponentHealth));
		ModManager.ModParentField.ModifyParentField(health, "<HealthChange>k__BackingField", 0f, typeof(ComponentHealth));
		ModManager.ModParentField.ModifyParentField(health, "<DeathTime>k__BackingField", null, typeof(ComponentHealth));
		ModManager.ModParentField.ModifyParentField(health, "<CauseOfDeath>k__BackingField", string.Empty, typeof(ComponentHealth));
		ModManager.ModParentField.ModifyParentField(health, "m_lastHealth", 1f, typeof(ComponentHealth));
		ResetNetworkPlayerVitals(player);
		ComponentCreatureModel model = player.ComponentCreatureModel;
		if (model != null)
		{
			ModManager.ModParentField.ModifyParentField(model, "<DeathPhase>k__BackingField", 0f, typeof(ComponentCreatureModel));
			ModManager.ModParentField.ModifyParentField(model, "<DeathCauseOffset>k__BackingField", Vector3.Zero, typeof(ComponentCreatureModel));
		}
		ComponentSpawn spawn = player.Entity.FindComponent<ComponentSpawn>();
		if (spawn != null)
		{
			ModManager.ModParentField.ModifyParentField(spawn, "<DespawnTime>k__BackingField", null, typeof(ComponentSpawn));
		}
		player.Entity.FindComponent<ComponentOnFire>()?.Extinguish();
		state.Input = default(PlayerInput);
		state.HeldInput = default(PlayerInput);
		state.HeldAim = null;
		state.AimEvents.Clear();
		state.QueuedAimCompletions.Clear();
		state.InteractEvents.Clear();
		state.HitEvents.Clear();
		state.DropEvents.Clear();
		state.InitialPositionApplied = true;
		state.BodyPosition = spawnPosition;
		state.BodyVelocity = Vector3.Zero;
		state.LastReceivedTime = Time.RealTime;
		m_hostPlayerPokingPhases.Remove(playerClientId);
		m_hostPlayerPokeSequences.Remove(playerClientId);
		if (m_clientRecordKeys.TryGetValue(playerClientId, out var recordKey))
		{
			m_playerRecords[recordKey] = CapturePlayerRecord(playerData);
			if (IsHost)
			{
				m_playerRecordsDirty = true;
			}
		}
		return true;
	}

	private static void ResetNetworkPlayerVitals(ComponentPlayer player)
	{
		ComponentVitalStats vital = player?.ComponentVitalStats;
		if (vital != null)
		{
			ModManager.ModParentField.ModifyParentField(vital, "m_food", 1f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_stamina", 1f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_sleep", 1f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_temperature", 12f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_wetness", 0f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_lastFood", 1f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_lastStamina", 1f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_lastSleep", 1f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_lastTemperature", 12f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_lastWetness", 0f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_environmentTemperature", 8f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_targetTemperature", 12f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_targetTemperatureFlux", 0f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_sleepBlackoutFactor", 0f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_sleepBlackoutDuration", 0f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_temperatureBlackoutFactor", 0f, typeof(ComponentVitalStats));
			ModManager.ModParentField.ModifyParentField(vital, "m_temperatureBlackoutDuration", 0f, typeof(ComponentVitalStats));
			ModManager.ModParentField.GetParentField<Dictionary<int, float>>(vital, "m_satiation", typeof(ComponentVitalStats))?.Clear();
		}
		ComponentSleep sleep = player?.ComponentSleep;
		sleep?.WakeUp();
		if (sleep != null)
		{
			ModManager.ModParentField.ModifyParentField(sleep, "m_sleepFactor", 0f, typeof(ComponentSleep));
			ModManager.ModParentField.ModifyParentField(sleep, "m_messageFactor", 0f, typeof(ComponentSleep));
		}
		ComponentFlu flu = player?.Entity.FindComponent<ComponentFlu>();
		if (flu != null)
		{
			string[] array = new string[6] { "m_fluOnset", "m_fluDuration", "m_coughDuration", "m_sneezeDuration", "m_blackoutDuration", "m_blackoutFactor" };
			foreach (string field in array)
			{
				ModManager.ModParentField.ModifyParentField(flu, field, 0f, typeof(ComponentFlu));
			}
		}
		ComponentSickness sickness = player?.Entity.FindComponent<ComponentSickness>();
		if (sickness != null)
		{
			if (ModManager.ModParentField.GetParentField(sickness, "m_pukeParticleSystem", typeof(ComponentSickness)) is PukeParticleSystem puke)
			{
				puke.IsStopped = true;
			}
			ModManager.ModParentField.ModifyParentField(sickness, "m_pukeParticleSystem", null, typeof(ComponentSickness));
			ModManager.ModParentField.ModifyParentField(sickness, "m_sicknessDuration", 0f, typeof(ComponentSickness));
			ModManager.ModParentField.ModifyParentField(sickness, "m_greenoutDuration", 0f, typeof(ComponentSickness));
			ModManager.ModParentField.ModifyParentField(sickness, "m_greenoutFactor", 0f, typeof(ComponentSickness));
		}
	}

	private static bool IsFinite(Vector3 value)
	{
		if (!float.IsNaN(value.X) && !float.IsInfinity(value.X) && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) && !float.IsNaN(value.Z))
		{
			return !float.IsInfinity(value.Z);
		}
		return false;
	}

	private static bool IsFinite(Vector2 value)
	{
		return !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
			!float.IsNaN(value.Y) && !float.IsInfinity(value.Y);
	}

	private static bool IsFinite(Quaternion value)
	{
		return !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
			!float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
			!float.IsNaN(value.Z) && !float.IsInfinity(value.Z) &&
			!float.IsNaN(value.W) && !float.IsInfinity(value.W);
	}

	private static void ApplyInteractionInventory(IInventory inventory, PlayerActionMessage message)
	{
		if (inventory != null && message != null && message.ActiveSlotIndex >= 0 && message.ActiveSlotIndex < inventory.SlotsCount && message.ItemCount >= 0)
		{
			inventory.ActiveSlotIndex = message.ActiveSlotIndex;
			inventory.RemoveSlotItems(message.ActiveSlotIndex, int.MaxValue);
			if (message.ItemValue != 0 && message.ItemCount > 0)
			{
				inventory.AddSlotItems(message.ActiveSlotIndex, message.ItemValue, message.ItemCount);
			}
		}
	}

	private bool ExecuteHostDropRequest(int sourceClientId, PlayerActionMessage message)
	{
		if (!m_networkPlayerData.TryGetValue(sourceClientId, out var playerData))
		{
			return false;
		}
		ComponentPlayer obj = playerData?.ComponentPlayer;
		IInventory inventory = obj?.ComponentMiner?.Inventory;
		ComponentBody body = obj?.ComponentBody;
		if (inventory == null || body == null || message.ActiveSlotIndex < 0 || message.ActiveSlotIndex >= inventory.SlotsCount)
		{
			return false;
		}
		int[] inventorySlotValues = message.InventorySlotValues;
		int[] inventorySlotCounts = message.InventorySlotCounts;
		if (message.HasInventoryDelta)
		{
			if (!TryExpandInventoryTransaction(inventory, true, message.InventorySlotIndices,
				message.InventoryBaseValues, message.InventoryBaseCounts,
				message.InventorySlotValues, message.InventorySlotCounts,
				out int[] expandedBaseValues, out int[] expandedBaseCounts,
				out int[] expandedDesiredValues, out int[] expandedDesiredCounts))
				return false;
			if (!IsInventorySnapshotValid(inventory, expandedBaseValues, expandedBaseCounts) ||
				expandedBaseValues[message.ActiveSlotIndex] != message.ItemValue ||
				expandedBaseCounts[message.ActiveSlotIndex] != message.ItemCount ||
				expandedDesiredValues[message.ActiveSlotIndex] != message.ItemValue ||
				expandedDesiredCounts[message.ActiveSlotIndex] !=
					Math.Max(0, message.ItemCount - message.RemoveCount))
				return false;
			ApplyInventory(inventory, expandedBaseValues, expandedBaseCounts);
		}
		else
		{
			if (inventorySlotValues == null || inventorySlotCounts == null ||
				inventorySlotValues.Length == 0 || inventorySlotCounts.Length == 0)
				return false;
			if (!IsInventorySnapshotValid(inventory, inventorySlotValues, inventorySlotCounts) ||
				message.InventorySlotValues[message.ActiveSlotIndex] != message.ItemValue ||
				message.InventorySlotCounts[message.ActiveSlotIndex] != message.ItemCount ||
				!HaveSameInventoryItems(inventory, message.InventorySlotValues,
					message.InventorySlotCounts))
			{
				return false;
			}
			ApplyInventory(inventory, message.InventorySlotValues,
				message.InventorySlotCounts);
		}
		int slotValue = inventory.GetSlotValue(message.ActiveSlotIndex);
		int count = Math.Min(message.RemoveCount, inventory.GetSlotCount(message.ActiveSlotIndex));
		if (slotValue != message.ItemValue || count <= 0)
		{
			return false;
		}
		int removed = inventory.RemoveSlotItems(message.ActiveSlotIndex, count);
		if (removed <= 0)
		{
			return false;
		}
		Vector3 defaultPosition = body.Position + new Vector3(0f, body.StanceBoxSize.Y * 0.66f, 0f) + 0.25f * body.Matrix.Forward;
		Vector3 position = ((Vector3.DistanceSquared(body.Position, message.Position) <= 64f) ? message.Position : defaultPosition);
		Vector3 velocity = message.Velocity;
		if (velocity.LengthSquared() > 400f)
		{
			velocity = Vector3.Normalize(velocity) * 20f;
		}
			int dropCount = ActionRequestValidationPolicy.NormalizeDropCountForWire(message.DropCount);
			int distributedCount = Math.Min(removed, dropCount);
			if (distributedCount <= 0)
			{
				return false;
			}
			GameManager.Project.FindSubsystem<SubsystemPickables>(throwOnError: true).AddPickable(
				slotValue, distributedCount, position, velocity, null);
		MarkHostInventoryAuthoritative(sourceClientId);
		return true;
	}

	private static bool HaveSameInventoryItems(IInventory inventory, int[] desiredValues, int[] desiredCounts)
	{
		Dictionary<int, long> dictionary = new Dictionary<int, long>();
		AddInventoryCounts(dictionary, CaptureInventoryValues(inventory), CaptureInventoryCounts(inventory), 1);
		AddInventoryCounts(dictionary, desiredValues, desiredCounts, -1);
		return dictionary.Values.All((long value) => value == 0);
	}

	private void MarkHostInventoryAuthoritative(int sourceClientId)
	{
		if (sourceClientId > 0 && m_networkPlayerInputs.TryGetValue(sourceClientId, out var state))
		{
			state.LastAuthoritativeInventoryTick = Math.Max(state.LastAuthoritativeInventoryTick, client?.Step ?? 0);
			m_lastSentInventoryValues.Remove(sourceClientId);
			m_lastSentInventoryCounts.Remove(sourceClientId);
		}
	}

	private void HandleGamePlayerInputMessage(GamePlayerInputMessage msg, int sourceClientId)
	{
		if (!IsHost || msg == null || sourceClientId <= 0 || !m_networkPlayerData.ContainsKey(sourceClientId) || (msg.PlayerIndex != 0 && msg.PlayerIndex != sourceClientId))
		{
			return;
		}
		if (!m_networkPlayerInputs.TryGetValue(sourceClientId, out var state))
		{
			state = new NetworkPlayerInputState();
			m_networkPlayerInputs[sourceClientId] = state;
		}
		if (!state.InitialPositionApplied && m_networkPlayerData.TryGetValue(sourceClientId, out var playerData) && playerData.ComponentPlayer?.ComponentBody != null)
		{
			ComponentBody body = playerData.ComponentPlayer.ComponentBody;
			if (Vector3.DistanceSquared(body.Position, msg.BodyPosition) <= 64f)
			{
				body.Position = msg.BodyPosition;
			}
			state.InitialPositionApplied = true;
		}
		if (msg.Sequence <= state.Sequence)
		{
			return;
		}
		ComponentPlayer remotePlayer = m_networkPlayerData[sourceClientId].ComponentPlayer;
		if (remotePlayer != null)
		{
			ModManager.ModParentField.ModifyParentField(remotePlayer.ComponentInput, "<IsControlledByTouch>k__BackingField", msg.IsControlledByTouch, typeof(ComponentInput));
			if (remotePlayer.ComponentMiner != null)
			{
				ModManager.ModParentField.ModifyParentField(remotePlayer.ComponentMiner, "<PokingPhase>k__BackingField", msg.PokingPhase, typeof(ComponentMiner));
			}
			remotePlayer.ComponentBody.TargetCrouchFactor = (msg.IsCrouching ? 1f : 0f);
			remotePlayer.ComponentLocomotion.IsCreativeFlyEnabled = msg.IsFlying;
			IInventory inventory = remotePlayer.ComponentMiner?.Inventory;
			if (!state.HeldAim.HasValue && state.AimEvents.Count <= 0 && state.QueuedAimCompletions.Count <= 0 && inventory != null && msg.ActiveSlotIndex >= 0 && msg.ActiveSlotIndex < inventory.VisibleSlotsCount)
			{
				inventory.ActiveSlotIndex = msg.ActiveSlotIndex;
			}
			// Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
			// ToggleMount is the only host-side mount transition command. Do not infer a new mount
			// from a stale IsRiding snapshot after the native dismount has completed.
		}
		state.Input = PlayerInputStatePolicy.Sanitize(msg.PlayerInput);
		state.BodyPosition = msg.BodyPosition;
		state.BodyVelocity = msg.BodyVelocity;
		state.IsGrounded = msg.IsGrounded;
		state.ClientTick = msg.ClientTick;
		state.BodyRotation = msg.BodyRotation;
		state.LookAngles = msg.LookAngles;
		state.Sequence = msg.Sequence;
		state.LastReceivedTime = Time.RealTime;
		SampleRemotePositionRtt(sourceClientId, state);
	}

	private ushort GetClientMountEntityId(ComponentPlayer player)
	{
		return GetNetworkMountEntityId(player?.ComponentRider?.Mount?.Entity);
	}

	// Source: Survivalcraft/Game/ComponentMount.cs:ComponentMount.Load
	// Network identities are assigned by the host for both saddled creatures and boats.
	private ushort GetNetworkMountEntityId(Entity mountEntity)
	{
		if (mountEntity == null)
		{
			return 0;
		}
		foreach (KeyValuePair<ushort, Entity> item in m_remoteAnimals)
		{
			if (item.Value == mountEntity)
			{
				return item.Key;
			}
		}
		// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:SendWorldObjects
		// A host player rides the authoritative creature entity, which is tracked in
		// m_hostAnimalIds before the position snapshot is broadcast. Use that identity
		// instead of falling through to zero, otherwise peers cannot bind the rider.
		foreach (KeyValuePair<Entity, ushort> item in m_hostAnimalIds)
		{
			if (item.Key == mountEntity)
			{
				return item.Value;
			}
		}
		foreach (KeyValuePair<Entity, ushort> item in m_hostMountIds)
		{
			if (item.Key == mountEntity)
				return item.Value;
		}
		foreach (KeyValuePair<ushort, Entity> item in m_remoteMounts)
		{
			if (item.Value == mountEntity)
				return item.Key;
		}
		return IsHost ? EnsureHostMountNetworkId(mountEntity) : (ushort)0;
	}

	// Source: Survivalcraft/Game/ComponentRider.StartMounting
	// Allocate the authoritative identity at the action boundary. The next world-object pulse may
	// not have run yet, but a mount acknowledgement must never carry MountEntityId=0.
	private ushort EnsureHostMountNetworkId(Entity mountEntity)
	{
		if (!IsHost || mountEntity == null || mountEntity.FindComponent<ComponentMount>() == null)
			return 0;
		if (mountEntity.FindComponent<ComponentCreature>() != null)
		{
			if (m_hostAnimalIds.TryGetValue(mountEntity, out ushort animalId))
				return animalId;
			animalId = m_nextAnimalId++;
			m_hostAnimalIds[mountEntity] = animalId;
			if (!m_hostAnimalSync.ContainsKey(mountEntity))
			{
				int simulationSeed = CalculateAnimalSimulationSeed(animalId);
				m_hostAnimalSync[mountEntity] = new AnimalSyncMetadata
				{
					SimulationSeed = simulationSeed
				};
				ApplyAnimalSimulationSeed(mountEntity, simulationSeed);
			}
			return animalId;
		}
		if (m_hostMountIds.TryGetValue(mountEntity, out ushort mountId))
			return mountId;
		mountId = m_nextMountId++;
		if (mountId < MountEntityIdStart)
			mountId = m_nextMountId = MountEntityIdStart;
		m_hostMountIds[mountEntity] = mountId;
		NetworkMessageSender.SendEntityMessage(new EntityMessage(
			mountId, EntityMessage.EntityAction.Add,
			mountEntity.ValuesDictionary?.DatabaseObject?.Name ?? "Boat"));
		return mountId;
	}

	// Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
	// Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.StartMounting
	// A client action is acknowledged only after the host has decided its outcome. Position
	// snapshots never call this path, so stale IsRiding values cannot replay an old transition.
	private void HandleMountActionMessage(MountActionMessage message, int sourceClientId)
	{
		if (!IsHost || message == null || sourceClientId <= 0 ||
			message.PlayerIndex != sourceClientId ||
			!m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData))
			return;
		ComponentPlayer player = playerData.ComponentPlayer;
		ComponentRider rider = player?.ComponentRider;
		NetworkPlayerInputState inputState = m_networkPlayerInputs.TryGetValue(sourceClientId,
			out NetworkPlayerInputState existing) ? existing : new NetworkPlayerInputState();
		m_networkPlayerInputs[sourceClientId] = inputState;
		if (message.ActionSequence <= inputState.LastMountActionSequence)
		{
			BroadcastMountState(sourceClientId, inputState.LastMountActionSequence,
				inputState.LastMountState, inputState.LastMountEntityId, player,
				advanceStateSequence: false);
			return;
		}
		inputState.LastMountActionSequence = message.ActionSequence;
		// Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
		// Mount selection is position-sensitive. The action carries the same local body pose that
		// produced the button edge, so apply that pose before the host reruns FindNearestMount;
		// otherwise a delayed input snapshot can leave the authoritative avatar behind the horse.
		if (IsFinite(message.BodyPosition) && IsFinite(message.BodyRotation) &&
			IsFinite(message.LookAngles))
		{
			inputState.BodyPosition = message.BodyPosition;
			inputState.BodyRotation = message.BodyRotation;
			inputState.LookAngles = message.LookAngles;
			inputState.LastReceivedTime = Time.RealTime;
		}
		ApplyHostActionPose(player, inputState);
		ComponentMount currentMount = rider?.Mount;
		if (rider == null || player.ComponentHealth?.Health <= 0f)
		{
			BroadcastMountState(sourceClientId, message.ActionSequence, MountStateKind.Rejected,
				0, player);
			return;
		}
		if (message.Action == MountActionKind.Dismount)
		{
			// Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.Mount
			// The host's actual parent relationship is authoritative. The client target ID is
			// only a hint and may be stale when the mount snapshot races the input edge.
			if (currentMount == null)
			{
				BroadcastMountState(sourceClientId, message.ActionSequence, MountStateKind.Rejected,
					0, player);
				return;
			}
			rider.StartDismounting();
			BroadcastMountState(sourceClientId, message.ActionSequence,
				MountStateKind.Dismounting, GetNetworkMountEntityId(currentMount.Entity), player);
			return;
		}
		if (message.Action != MountActionKind.Mount || currentMount != null)
		{
			BroadcastMountState(sourceClientId, message.ActionSequence, MountStateKind.Rejected,
				GetNetworkMountEntityId(currentMount?.Entity), player);
			return;
		}
		// Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.FindNearestMount
		// Prefer the host entity represented by the client's stable network ID. This preserves
		// identity when two mounts are close together, while the same native visibility checks
		// below keep the client from selecting an invalid or occupied host mount.
		ComponentMount mount = ResolveHostMount(message.MountEntityId);
		if (mount == null || !IsHostMountTargetValid(rider, mount))
			mount = rider.FindNearestMount();
		// Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.FindNearestMount
		// Re-run the original host-side selection. A missing or stale client ID must not reject
		// a valid interaction; the host still validates occupancy through ComponentMount.Rider.
		if (mount == null || (mount.Rider != null && mount.Rider != rider))
		{
			BroadcastMountState(sourceClientId, message.ActionSequence, MountStateKind.Rejected,
				0, player);
			return;
		}
		rider.StartMounting(mount);
		BroadcastMountState(sourceClientId, message.ActionSequence, MountStateKind.Mounting,
			GetNetworkMountEntityId(mount.Entity), player);
	}

	// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:SendWorldObjects
	// Animal IDs are allocated by the host and reused by every client. Mount-only bodies use the
	// high ID range and are resolved from the separate host mount table.
	private ComponentMount ResolveHostMount(ushort mountEntityId)
	{
		if (mountEntityId == 0) return null;
		Entity animal = m_hostAnimalIds.FirstOrDefault(item => item.Value == mountEntityId).Key;
		ComponentMount mount = animal?.FindComponent<ComponentMount>();
		if (mount != null) return mount;
		Entity body = m_hostMountIds.FirstOrDefault(item => item.Value == mountEntityId).Key;
		return body?.FindComponent<ComponentMount>();
	}

	// Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.ScoreMount
	// Keep the original distance, facing, velocity and occupancy constraints when resolving a
	// network identity instead of accepting an arbitrary client-provided entity ID.
	private static bool IsHostMountTargetValid(ComponentRider rider, ComponentMount mount)
	{
		if (rider?.ComponentCreature?.ComponentCreatureModel == null || mount?.ComponentBody == null)
			return false;
		if (mount.Rider != null && mount.Rider != rider || mount.ComponentBody.Velocity.LengthSquared() >= 1f)
			return false;
		Vector3 offset = mount.ComponentBody.Position +
			Vector3.Transform(mount.MountOffset, mount.ComponentBody.Rotation) -
			rider.ComponentCreature.ComponentCreatureModel.EyePosition;
		if (offset.Length() >= 2.5f) return false;
		Vector3 direction = Vector3.Normalize(offset);
		Vector3 forward = Matrix.CreateFromQuaternion(
			rider.ComponentCreature.ComponentCreatureModel.EyeRotation).Forward;
		return Vector3.Dot(direction, forward) > 0.33f;
	}

	private void BroadcastMountState(int playerClientId, int actionSequence,
		MountStateKind state, ushort mountEntityId, ComponentPlayer player,
		bool advanceStateSequence = true)
	{
		// Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.Mount
		// Rejections and native dismount animation must report the actual host mount, not just
		// the requested target. A rejected dismount can therefore restore the mounted state.
		ushort authoritativeMountId = GetNetworkMountEntityId(
			player?.ComponentRider?.Mount?.Entity);
		if (authoritativeMountId != 0) mountEntityId = authoritativeMountId;
		int stateSequence;
		if (!m_hostMountStateSequences.TryGetValue(playerClientId, out stateSequence))
			stateSequence = -1;
		if (advanceStateSequence)
		{
			stateSequence = PlayerActionSequencePolicy.Next(stateSequence);
			m_hostMountStateSequences[playerClientId] = stateSequence;
		}
		if (m_networkPlayerInputs.TryGetValue(playerClientId, out NetworkPlayerInputState inputState))
		{
			inputState.LastMountStateSequence = stateSequence;
			inputState.LastMountState = state;
			inputState.LastMountEntityId = mountEntityId;
		}
		ComponentBody body = ResolveNetworkMount(mountEntityId)?.ComponentBody;
		NetworkMessageSender.BroadcastMountStateMessage(new MountStateMessage(playerClientId,
			actionSequence, stateSequence, state, mountEntityId, client?.Step ?? 0,
			body?.Position ?? player?.ComponentBody?.Position ?? Vector3.Zero,
			body?.Rotation ?? Quaternion.Identity));
	}

	private void HandleMountStateMessage(MountStateMessage message, int sourceClientId)
	{
		if (IsHost || sourceClientId != 0 || message == null || message.PlayerIndex < 0) return;
		if (message.PlayerIndex == client?.ClientID)
		{
			if (message.StateSequence < m_lastLocalMountStateSequence ||
				(message.StateSequence == m_lastLocalMountStateSequence &&
					message.ServerTick <= m_lastLocalMountStateServerTick)) return;
			m_lastLocalMountStateSequence = Math.Max(m_lastLocalMountStateSequence,
				message.StateSequence);
			m_lastLocalMountStateServerTick = Math.Max(m_lastLocalMountStateServerTick,
				message.ServerTick);
		}
		else if (RemotePlayers.TryGetValue(message.PlayerIndex, out NetworkPlayerState remoteState))
		{
			if (message.StateSequence < remoteState.MountStateSequence) return;
			remoteState.MountStateSequence = message.StateSequence;
			remoteState.MountActionSequence = Math.Max(remoteState.MountActionSequence,
				message.ActionSequence);
		}
		m_receivedMountStates[message.PlayerIndex] = message;
		TryApplyReceivedMountState(message.PlayerIndex);
	}

	private void TryApplyReceivedMountState(int playerClientId)
	{
		if (!m_receivedMountStates.TryGetValue(playerClientId, out MountStateMessage message)) return;
		ComponentPlayer player = playerClientId == client?.ClientID
			? GameManager.Project?.FindSubsystem<SubsystemPlayers>(false)?.ComponentPlayers
				.FirstOrDefault(item => !m_networkPlayerData.Values.Contains(item.PlayerData))
			: (m_networkPlayerData.TryGetValue(playerClientId, out PlayerData playerData)
				? playerData.ComponentPlayer : null);
		ComponentRider rider = player?.ComponentRider;
		if (rider == null) return;
		if (message.State == MountStateKind.Dismounting ||
			message.State == MountStateKind.Dismounted)
		{
			if (rider.Mount != null) rider.StartDismounting();
			return;
		}
		if (message.State == MountStateKind.Rejected && !message.IsRiding)
		{
			if (rider.Mount != null) rider.StartDismounting();
			return;
		}
		ComponentMount mount = ResolveNetworkMount(message.MountEntityId);
		if (mount == null) return;
		if (message.State == MountStateKind.Rejected || message.State == MountStateKind.Mounted ||
			message.State == MountStateKind.Mounting)
		{
			RestoreMountedState(rider, mount);
		}
	}

	// Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.Update
	// A rejected dismount must cancel the local prediction animation. The original game has no
	// public "cancel dismount" operation, so this narrow ModParentField bridge restores the same
	// parent relationship and animation fields that StartMounting would leave authoritative.
	private void RestoreMountedState(ComponentRider rider, ComponentMount mount)
	{
		if (rider?.ComponentCreature?.ComponentBody == null || mount?.ComponentBody == null) return;
		ComponentBody body = rider.ComponentCreature.ComponentBody;
		if (rider.Mount == null)
		{
			rider.StartMounting(mount);
			return;
		}
		if (rider.Mount != mount) return;
		Vector3 riderOffset = ModManager.ModParentField.GetParentField<Vector3>(
			rider, "m_riderOffset", typeof(ComponentRider));
		body.ParentBody = mount.ComponentBody;
		body.ParentBodyPositionOffset = mount.MountOffset + riderOffset;
		body.ParentBodyRotationOffset = Quaternion.Identity;
		ModManager.ModParentField.ModifyParentField(rider, "m_isAnimating", false,
			typeof(ComponentRider));
		ModManager.ModParentField.ModifyParentField(rider, "m_isDismounting", false,
			typeof(ComponentRider));
		ModManager.ModParentField.ModifyParentField(rider, "m_animationTime", 0f,
			typeof(ComponentRider));
		ModManager.ModParentField.ModifyParentField(rider, "m_outOfMountTime", 0f,
			typeof(ComponentRider));
	}

	private void PublishHostMountStateChanges()
	{
		if (!IsHost) return;
		var players = new List<KeyValuePair<int, ComponentPlayer>>();
		ComponentPlayer localPlayer = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false)?.ComponentPlayers
			.FirstOrDefault(item => !m_networkPlayerData.Values.Contains(item.PlayerData));
		if (localPlayer != null) players.Add(new KeyValuePair<int, ComponentPlayer>(0, localPlayer));
		foreach (KeyValuePair<int, PlayerData> entry in m_networkPlayerData.ToArray())
			if (entry.Key > 0 && entry.Value?.ComponentPlayer != null)
				players.Add(new KeyValuePair<int, ComponentPlayer>(entry.Key, entry.Value.ComponentPlayer));
		foreach (KeyValuePair<int, ComponentPlayer> entry in players)
		{
			ComponentPlayer player = entry.Value;
			ComponentMount mount = player.ComponentRider?.Mount;
			MountStateKind observedState = mount == null
				? MountStateKind.Dismounted : MountStateKind.Mounted;
			if (!m_networkPlayerInputs.TryGetValue(entry.Key, out NetworkPlayerInputState state))
			{
				state = new NetworkPlayerInputState();
				m_networkPlayerInputs[entry.Key] = state;
			}
			if (state.LastMountActionSequence < 0 && state.LastMountStateSequence < 0)
			{
				BroadcastMountState(entry.Key, -1, observedState,
					GetNetworkMountEntityId(mount?.Entity), player);
				continue;
			}
			if (state.LastMountState == MountStateKind.Mounting && mount != null)
				BroadcastMountState(entry.Key, state.LastMountActionSequence, MountStateKind.Mounted,
					GetNetworkMountEntityId(mount.Entity), player);
			else if (state.LastMountState == MountStateKind.Dismounting && mount == null)
				BroadcastMountState(entry.Key, state.LastMountActionSequence, MountStateKind.Dismounted,
					0, player);
			else if (state.LastMountState == MountStateKind.Mounted && mount == null)
				BroadcastMountState(entry.Key, state.LastMountActionSequence, MountStateKind.Dismounted,
					0, player);
			else if (state.LastMountState == MountStateKind.Dismounted && mount != null)
				BroadcastMountState(entry.Key, state.LastMountActionSequence, MountStateKind.Mounted,
					GetNetworkMountEntityId(mount.Entity), player);
		}
	}

	private ComponentMount ResolveNetworkMount(ushort mountEntityId)
	{
		if (mountEntityId == 0) return null;
		Entity hostEntity = m_hostAnimalIds.FirstOrDefault(
			item => item.Value == mountEntityId).Key;
		if (hostEntity != null)
			return hostEntity.FindComponent<ComponentMount>();
		if (m_remoteAnimals.TryGetValue(mountEntityId, out Entity remoteAnimal))
			return remoteAnimal?.FindComponent<ComponentMount>();
		if (m_remoteMounts.TryGetValue(mountEntityId, out Entity remoteMount))
			return remoteMount?.FindComponent<ComponentMount>();
		return null;
	}

        private static IEnumerable<FieldInfo> GetSubsystemRandomFields(Type type)
        {
            Type current = type;
            while (current != null && current != typeof(object))
            {
                FieldInfo[] fields = current.GetFields(
                    BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (FieldInfo field in fields)
                {
                    if (!field.IsInitOnly && field.FieldType == typeof(Engine.Random))
                        yield return field;
                }
                current = current.BaseType;
            }
        }
    }
}

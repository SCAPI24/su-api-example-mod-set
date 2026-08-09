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
	private void MergePendingTerrainChanges()
	{
		lock (m_terrainJournalLock)
		{
			MergePendingTerrainChangesLocked();
		}
	}

	private void MergePendingTerrainChangesLocked()
	{
		foreach (KeyValuePair<Point3, TerrainCellState> item in m_pendingTerrainChanges)
		{
			m_terrainCheckpoint[item.Key] = item.Value;
			Point2 coordinates = Terrain.ToChunk(item.Key.X, item.Key.Z);
			if (!m_terrainCheckpointByChunk.TryGetValue(coordinates, out var chunkCheckpoint))
			{
				chunkCheckpoint = new Dictionary<Point3, TerrainCellState>();
				m_terrainCheckpointByChunk.Add(coordinates, chunkCheckpoint);
			}
			chunkCheckpoint[item.Key] = item.Value;
		}
		m_pendingTerrainChanges.Clear();
	}

	private void SendTerrainCatchUp(int targetClientId, int snapshotStartTick)
	{
		// Source: ScMultiplayer.UpdateHostTerrainInterestTable
		// Refresh once before projecting the join snapshot so a client does not
		// receive deferred cells from chunks outside its initial terrain window.
		UpdateHostTerrainInterestTable(GameManager.Project);
		int targetTick = client.Step;
		long headSequence;
		List<KeyValuePair<Point3, TerrainCellState>> snapshot;
		lock (m_terrainJournalLock)
		{
			headSequence = m_hostTerrainSequence;
			MergePendingTerrainChangesLocked();
			snapshot = (from item in m_terrainCheckpoint
				where item.Value.Tick > snapshotStartTick &&
					(!m_hostTerrainInterestChunks.TryGetValue(targetClientId,
						out HashSet<Point2> interestedChunks) ||
						interestedChunks.Contains(Terrain.ToChunk(item.Key.X, item.Key.Z)))
				orderby item.Value.Tick, item.Key.X, item.Key.Y, item.Key.Z
				select item).ToList();
		}
		if (snapshot.Count == 0)
		{
			GameModifiedCellsMessage marker = new GameModifiedCellsMessage(new Dictionary<Point3, bool>(), new List<int>(), targetTick, isCatchUp: true, targetClientId, 0L);
			marker.HeadSequence = headSequence;
			QueueJoinCatchUpPayload(targetClientId, Message.WriteWithSender(marker, client.Address));
			return;
		}
		for (int offset = 0; offset < snapshot.Count; offset += 48)
		{
			Dictionary<Point3, bool> cells = new Dictionary<Point3, bool>();
			List<int> values = new List<int>();
			int count = Math.Min(48, snapshot.Count - offset);
			for (int i = 0; i < count; i++)
			{
				KeyValuePair<Point3, TerrainCellState> item2 = snapshot[offset + i];
				cells[item2.Key] = item2.Value.IsModified;
				values.Add(item2.Value.CellValue);
			}
			GameModifiedCellsMessage message = new GameModifiedCellsMessage(cells, values, targetTick, isCatchUp: true, targetClientId, 0L);
			message.HeadSequence = headSequence;
			QueueJoinCatchUpPayload(targetClientId, Message.WriteWithSender(message, client.Address));
		}
		Log.Information($"[ScMP] Sent terrain catch-up: ClientID={targetClientId}, Tick={targetTick}, StartTick={snapshotStartTick}, Cells={snapshot.Count}");
	}

	private IncomingWorldTransfer GetOrCreateIncomingWorldTransfer(int transferId, int targetClientId, int chunkCount, int totalLength)
	{
		int expectedChunks = ((totalLength > 0) ? ((totalLength + 940 - 1) / 940) : 0);
		if (transferId <= 0 || targetClientId != client.ClientID || totalLength <= 0 || totalLength > 67108864 || chunkCount <= 0 || chunkCount != expectedChunks)
		{
			return null;
		}
		if (m_worldTransferRegistry.IncomingTransfers.TryGetValue(transferId, out var existing))
		{
			if (existing.TargetClientId != targetClientId || existing.TotalLength != totalLength || existing.Chunks.Length != chunkCount)
			{
				return null;
			}
			return existing;
		}
		IncomingWorldTransfer transfer = new IncomingWorldTransfer
		{
			TransferId = transferId,
			TargetClientId = targetClientId,
			TotalLength = totalLength,
			Chunks = new byte[chunkCount][],
			StartTime = Time.RealTime,
			LastProgressTime = Time.RealTime
		};
		m_worldTransferRegistry.IncomingTransfers.Add(transferId, transfer);
		return transfer;
	}

	private void HandleGamePakWorldChunkMessage(GamePakWorldChunkMessage message)
	{
		if (IsHost || message == null)
		{
			return;
		}
		IncomingWorldTransfer transfer = GetOrCreateIncomingWorldTransfer(message.TransferId, message.TargetClientId, message.ChunkCount, message.TotalLength);
		if (transfer == null || message.ChunkIndex < 0 || message.ChunkIndex >= transfer.Chunks.Length || message.Data == null)
		{
			return;
		}
		int expectedLength = Math.Min(940, transfer.TotalLength - message.ChunkIndex * 940);
		if (message.Data.Length != expectedLength)
		{
			return;
		}
		if (transfer.Chunks[message.ChunkIndex] == null)
		{
			transfer.Chunks[message.ChunkIndex] = message.Data;
			transfer.ReceivedChunkCount++;
			transfer.ReceivedBytes += message.Data.Length;
			transfer.HighestReceivedChunkIndex = Math.Max(transfer.HighestReceivedChunkIndex, message.ChunkIndex);
			while (transfer.HighestContiguousChunkIndex + 1 < transfer.Chunks.Length && transfer.Chunks[transfer.HighestContiguousChunkIndex + 1] != null)
			{
				transfer.HighestContiguousChunkIndex++;
			}
			transfer.LastProgressTime = Time.RealTime;
			RecordClientJoinProgress();
		}
		TryCompleteIncomingWorldTransfer(transfer);
	}

	private void TryCompleteIncomingWorldTransfer(IncomingWorldTransfer transfer)
	{
		if (transfer?.Manifest == null || transfer.ReceivedChunkCount != transfer.Chunks.Length)
		{
			return;
		}
		byte[] worldData = new byte[transfer.TotalLength];
		int offset = 0;
		byte[][] chunks = transfer.Chunks;
		foreach (byte[] chunk in chunks)
		{
			if (chunk == null || offset + chunk.Length > worldData.Length)
			{
				return;
			}
			Array.Copy(chunk, 0, worldData, offset, chunk.Length);
			offset += chunk.Length;
		}
		if (offset != worldData.Length)
		{
			return;
		}
		GamePakWorldMessage manifest = transfer.Manifest;
		byte[] expectedHash = manifest.WorldSha256;
		byte[] actualHash = SHA256.HashData(worldData);
		if (expectedHash == null || expectedHash.Length != actualHash.Length || !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
		{
			ResetIncomingWorldTransferAfterChecksumFailure(transfer);
			return;
		}
		double elapsed = Math.Max(Time.RealTime - transfer.StartTime, 0.0);
		Log.Information($"[ScMP] World download complete: Transfer={transfer.TransferId}, Transport=UDP, Bytes={transfer.TotalLength}, Seconds={elapsed:0.00}, RepairRounds={transfer.RepairRequestCount}");
		if (m_joinRoomBusyDialog != null)
		{
			m_joinRoomBusyDialog.SmallMessage = "Connected.\r\nWorld download complete.\r\nImporting world...";
		}
		m_joinAwaitingWorldProgress = false;
		manifest.WorldData = worldData;
		manifest.ChunkCount = 0;
		m_worldTransferRegistry.IncomingTransfers.Remove(transfer.TransferId);
		HandleGamePakWorldMessage(manifest);
	}

	private static void ResetIncomingWorldTransferAfterChecksumFailure(IncomingWorldTransfer transfer)
	{
		Array.Clear(transfer.Chunks, 0, transfer.Chunks.Length);
		transfer.ReceivedChunkCount = 0;
		transfer.ReceivedBytes = 0;
		transfer.HighestContiguousChunkIndex = -1;
		transfer.HighestReceivedChunkIndex = -1;
		transfer.LastProgressTime = Time.RealTime;
		transfer.LastStatusRequestTime = 0.0;
		transfer.LastRepairRequestTime = 0.0;
		transfer.RepairRequestCount++;
		Log.Warning($"[ScMP] World transfer checksum failed; requesting all chunks again: Transfer={transfer.TransferId}, Attempt={transfer.RepairRequestCount}");
	}

	private void HandleGamePakWorldReadyMessage(GamePakWorldReadyMessage message, int sourceClientId)
	{
		if (message == null)
		{
			return;
		}
		if (!IsHost)
		{
			if (sourceClientId != 0 || !JoinReadyPolicy.IsTransferMatch(
				m_worldTransferRegistry.PendingWorldReadyTransferId, message.TransferId))
			{
				return;
			}
			if (message.Stage == GamePakWorldReadyStage.CatchUpBatchComplete)
			{
				RecordClientJoinProgress();
				if (ClientJoinReadyStage == GamePakWorldReadyStage.CatchUpBatchApplied)
				{
					SendClientJoinReadyStage(GamePakWorldReadyStage.CatchUpBatchApplied);
					return;
				}
				int clientTransferId = message.TransferId;
				QueueEndOfFrameAction(delegate
				{
					if (m_worldTransferRegistry.PendingWorldReadyTransferId == clientTransferId)
					{
						if (m_worldTransferRegistry.PendingCircuitReadyTransferId != clientTransferId)
						{
							m_circuitSynchronizer?.BeginJoinBootstrap();
							m_worldTransferRegistry.PendingCircuitReadyTransferId = clientTransferId;
						}
						TryAcknowledgeClientCatchUpApplied();
					}
				});
			}
			else if (message.Stage == GamePakWorldReadyStage.ReadyToPlay)
			{
				int readyTransferId2 = message.TransferId;
				QueueEndOfFrameAction(delegate
				{
					CompleteClientJoinAfterApply(readyTransferId2);
				});
			}
		}
		else
		{
			if (sourceClientId <= 0)
			{
				return;
			}
			if (!m_joinCatchUpRegistry.TransfersAwaitingReady.TryGetValue(sourceClientId, out var transferId) ||
				!JoinReadyPolicy.IsTransferMatch(transferId, message.TransferId))
			{
				if (m_joinCatchUpRegistry.CompletedReadyTransfers.TryGetValue(sourceClientId, out var completedTransferId) && completedTransferId == message.TransferId && (message.Stage == GamePakWorldReadyStage.ProjectReady || message.Stage == GamePakWorldReadyStage.CatchUpBatchApplied))
				{
					NetworkMessageSender.SendPakWorldReady(sourceClientId, new GamePakWorldReadyMessage(message.TransferId, GamePakWorldReadyStage.ReadyToPlay));
				}
				return;
			}
			switch (message.Stage)
			{
			case GamePakWorldReadyStage.LoadingProject:
			{
				if (!m_pendingAcceptedJoinKeys.TryGetValue(sourceClientId, out var recordKey) || !m_playerRecords.TryGetValue(recordKey, out var record))
				{
					AbortJoiningClient(sourceClientId, "The approved player profile is unavailable.");
					break;
				}
				CreateNetworkPlayer(sourceClientId, record.Name, recordKey);
				if (!m_networkPlayerData.ContainsKey(sourceClientId))
				{
					AbortJoiningClient(sourceClientId, "The host could not create the network player.");
					break;
				}
				m_pendingAcceptedJoinKeys.Remove(sourceClientId);
				SynchronizePlayerProfiles();
				Log.Information($"[ScMP] Client entered Loading Project: ClientID={sourceClientId}, Transfer={transferId}");
				break;
			}
			case GamePakWorldReadyStage.ProjectReady:
			{
				if (!m_networkPlayerData.ContainsKey(sourceClientId))
				{
					AbortJoiningClient(sourceClientId, "The network player was not initialized.");
					break;
				}
				if (m_joinCatchUpRegistry.HostProjectReadyTransfers.TryGetValue(sourceClientId, out var readyTransferId) && readyTransferId == transferId)
				{
					NetworkMessageSender.SendPakWorldReady(sourceClientId, new GamePakWorldReadyMessage(transferId, GamePakWorldReadyStage.CatchUpBatchComplete));
					break;
				}
                m_joinCatchUpRegistry.HostProjectReadyTransfers[sourceClientId] = transferId;
                m_worldTransferRegistry.OutgoingTransfers.Remove(sourceClientId);
                // Source: Mod/ScMultiplayer/Modules/Player/
                // ScMultiplayerPlayerHealthAndIngress.cs:SendPendingWorldTransferChunks
                // ProjectReady proves the client received the complete archive. Any remaining
                // local-relay reservations are only for the finished archive; actual Comms packets
                // remain visible through GetUnackedPacketsCount for the catch-up phase.
                RemoveReliableRelayReservations(sourceClientId);
                SealAndSendJoinCatchUp(sourceClientId, transferId);
				break;
			}
			case GamePakWorldReadyStage.CatchUpBatchApplied:
			{
				JoinCatchUpJournal journal;
				if (!m_networkPlayerData.ContainsKey(sourceClientId))
				{
					AbortJoiningClient(sourceClientId, "The network player was lost during join catch-up.");
				}
				else if (m_joinCatchUpRegistry.Journals.TryGetValue(sourceClientId, out journal))
				{
					CompleteJoiningClient(sourceClientId, transferId, journal);
				}
				break;
			}
			}
		}
	}

	private void CompleteClientJoinAfterApply(int transferId)
	{
		if (JoinReadyPolicy.IsTransferMatch(
			m_worldTransferRegistry.PendingWorldReadyTransferId, transferId))
		{
			Log.Information($"[ScMP] Client catch-up complete: Transfer={transferId}");
			m_worldTransferRegistry.ClientTerrainChunkBaselineRevision = Math.Max(m_worldTransferRegistry.ClientTerrainChunkBaselineRevision, m_worldTransferRegistry.ClientTerrainJoinBaselineRevision);
			m_worldTransferRegistry.ClientTerrainJoinBaselineRevision = 0L;
			m_worldTransferRegistry.PendingWorldReadyTransferId = 0;
			m_worldTransferRegistry.PendingCircuitReadyTransferId = 0;
			m_projectReadySentProject = null;
			m_projectReadySentTransferId = 0;
			m_nextClientJoinReadyRetryTime = 0.0;
			m_lastClientJoinBarrierProgressTime = 0.0;
			m_isLoadingDownloadedWorld = false;
			m_controlUnit?.Context.Session.MarkWorldReady();
			m_controlUnit?.Context.Connections.TryTransition(client.ClientID, PlayerConnectionPhase.Ready, Time.RealTime);
			m_clientTerrainRecoveryActive = false;
			m_clientTerrainRecoveryPending = false;
			m_clientTerrainRecoveryRequestInFlight = false;
			m_clientTerrainRecoveryTarget = -1L;
			m_clientTerrainRecoveryAcknowledged = -1L;
			m_clientTerrainRecoveryReady = -1L;
			m_clientTerrainGapDetectedTime = 0.0;
			m_clientWorldRefreshProject = null;
			m_remoteFogPresentationInitialized = false;
			ApplyRemoteWeatherState();
			HideJoinRoomBusyDialog();
		}
	}

	private void AbortJoiningClient(int sourceClientId, string reason)
	{
		SetServerClientGameTrafficEnabled(sourceClientId, enabled: true);
		m_pendingNetworkPlayers.Remove(sourceClientId);
		m_pendingNetworkPlayerIdentities.Remove(sourceClientId);
		RemoveNetworkPlayer(sourceClientId);
		playerMappingManager.ReleasePlayerIndex(sourceClientId);
		ServerClient remoteClient = (server?.Games.FirstOrDefault((ServerGame item) => item.GameID == client?.GameID))?.Clients.FirstOrDefault((ServerClient item) => item.ClientID == sourceClientId);
		if (remoteClient != null)
		{
			DisconnectNetworkClient(remoteClient);
		}
		Log.Error($"[ScMP] Aborted joining ClientID {sourceClientId}: {reason}");
	}

	private void SealAndSendJoinCatchUp(int targetClientId, int transferId)
	{
		if (m_joinCatchUpRegistry.Journals.TryGetValue(targetClientId, out var journal))
		{
			journal.CutoffSealed = true;
			FlushJoinCatchUpJournal(targetClientId);
			SendTerrainCatchUp(targetClientId, journal.StartTick);
			GetOrCreatePendingJoinCatchUp(targetClientId).CompletionAction = delegate
			{
				NetworkMessageSender.SendPakWorldReady(targetClientId, new GamePakWorldReadyMessage(transferId, GamePakWorldReadyStage.CatchUpBatchComplete));
			};
		}
	}

	private void CompleteJoiningClient(int sourceClientId, int transferId, JoinCatchUpJournal journal)
	{
		DrainPostCutoffJournal(sourceClientId, journal);
		GetOrCreatePendingJoinCatchUp(sourceClientId).CompletionAction = delegate
		{
			FinishJoiningClient(sourceClientId, transferId, journal);
		};
	}

	private void FinishJoiningClient(int sourceClientId, int transferId, JoinCatchUpJournal journal)
	{
		m_joinCatchUpRegistry.Journals.Remove(sourceClientId);
		m_joinCatchUpRegistry.TransfersAwaitingReady.Remove(sourceClientId);
		m_joinCatchUpRegistry.HostProjectReadyTransfers.Remove(sourceClientId);
		m_joinCatchUpRegistry.CompletedReadyTransfers[sourceClientId] = transferId;
		NetworkMessageSender.SendPakWorldReady(sourceClientId, new GamePakWorldReadyMessage(transferId, GamePakWorldReadyStage.ReadyToPlay));
		SetServerClientGameTrafficEnabled(sourceClientId, enabled: true);
		m_controlUnit?.Context.Connections.TryTransition(sourceClientId, PlayerConnectionPhase.Ready, Time.RealTime);
		m_pendingHostPickableSnapshots.Add(sourceClientId);
		m_fullWorldObjectsSyncTime = 5f;
		m_fullAnimalSyncTime = 5f;
		Log.Information($"[ScMP] World transfer ready: ClientID={sourceClientId}, Transfer={transferId}, CatchUpRounds={journal.ReplayRound}, CatchUpMessages={journal.TotalMessagesSent}, CatchUpBytes={journal.TotalBytesSent}, Dropped={journal.DroppedMessages}");
	}

	public void HandleGamePakWorldMessage(GamePakWorldMessage msg)
	{
		if (msg == null || (msg.TargetClientId >= 0 && msg.TargetClientId != client.ClientID))
		{
			return;
		}
		if (msg.TransferId > 0 && msg.ChunkCount > 0)
		{
			IncomingWorldTransfer transfer = GetOrCreateIncomingWorldTransfer(msg.TransferId, msg.TargetClientId, msg.ChunkCount, msg.TotalLength);
			if (transfer == null)
			{
				return;
			}
			if (msg.WorldSha256 == null || msg.WorldSha256.Length != 32)
			{
				Log.Error($"[ScMP] Invalid world checksum manifest: Transfer={msg.TransferId}");
				return;
			}
			bool num = transfer.Manifest == null;
			transfer.Manifest = msg;
			transfer.LastProgressTime = Time.RealTime;
			if (num)
			{
				RecordClientJoinProgress();
			}
			TryCompleteIncomingWorldTransfer(transfer);
		}
		else
		{
			if (msg.WorldData == null || msg.WorldData.Length == 0)
			{
				return;
			}
			m_joinAwaitingWorldProgress = false;
			m_sessionRandomSeed = msg.RandomSeed;
			m_pendingTerrainSequenceBaseline = msg.TerrainSequenceBaseline;
			m_pendingRandomStates = msg.RandomStates ?? new Dictionary<string, long>();
			m_randomStateAppliedProject = null;
			m_pendingLocalPlayerRecord = new NetworkPlayerRecord
			{
				Name = msg.PlayerName,
				PlayerClass = msg.PlayerClass,
				SkinName = msg.SkinName,
				SkinSha256 = SkinHashCodec.CloneBytes(msg.SkinSha256),
				Position = msg.PlayerPosition,
				SpawnPosition = msg.PlayerSpawnPosition,
				Level = msg.PlayerLevel,
				Health = msg.PlayerHealth,
				Air = msg.PlayerAir,
				Food = msg.PlayerFood,
				Stamina = msg.PlayerStamina,
				Sleep = msg.PlayerSleep,
				Temperature = msg.PlayerTemperature,
				TargetTemperature = msg.PlayerTargetTemperature,
				Wetness = msg.PlayerWetness,
				FluDuration = msg.PlayerFluDuration,
				FluOnset = msg.PlayerFluOnset,
				SicknessDuration = msg.PlayerSicknessDuration,
				BodyRotation = msg.PlayerBodyRotation,
				LookAngles = msg.PlayerLookAngles,
				FireDuration = msg.PlayerFireDuration,
				Satiation = (msg.PlayerSatiation ?? new Dictionary<int, float>()),
				IsCreativeFlying = msg.PlayerIsCreativeFlying,
				HasReceivedInitialItems = msg.HasReceivedInitialItems,
				InventoryWasCreative = msg.InventoryWasCreative,
				ActiveSlotIndex = msg.ActiveSlotIndex,
				CreativeCategoryIndex = msg.CreativeCategoryIndex,
				CreativePageIndex = msg.CreativePageIndex,
				SlotValues = msg.SlotValues,
				SlotCounts = msg.SlotCounts,
				HandcraftSlotValues = msg.HandcraftSlotValues,
				HandcraftSlotCounts = msg.HandcraftSlotCounts,
				Clothes = msg.Clothes
			};
			m_localReplacementPlayerData = null;
			m_localPlayerRecordQueued = false;
			m_localPlayerRecordApplied = false;
			try
			{
				Log.Information($"[ScMP] Importing world: {msg.Name} ({msg.WorldData.Length} bytes)");
				HashSet<string> existingDirectories = new HashSet<string>(WorldsManager.WorldInfos.Select((WorldInfo world) => world.DirectoryName));
				string importedDirectory = ImportNetworkWorld(new MemoryStream(msg.WorldData));
				m_downloadedWorldDirectory = importedDirectory;
				RegisterDownloadedWorld(importedDirectory);
				WorldsManager.UpdateWorldsList();
				WorldInfo importedWorld = WorldsManager.WorldInfos.FirstOrDefault((WorldInfo world) => world.DirectoryName == importedDirectory);
				if (importedWorld == null)
				{
					importedWorld = WorldsManager.WorldInfos.FirstOrDefault((WorldInfo world) => !existingDirectories.Contains(world.DirectoryName) && world.WorldSettings.Name == msg.Name);
				}
				if (importedWorld == null)
				{
					importedWorld = WorldsManager.WorldInfos.FirstOrDefault((WorldInfo world) => world.WorldSettings.Name == msg.Name && world.LastSaveTime == msg.LastSaveTime);
				}
				if (importedWorld != null)
				{
					if (m_joinRoomBusyDialog != null)
					{
						m_joinRoomBusyDialog.SmallMessage = "Connected.\r\nWorld imported.\r\nLoading project...";
					}
					SuPlayScreen.Play(importedWorld);
					connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Playing);
					m_shouldCreateHostAvatar = true;
					m_pendingNetworkPlayers[0] = "Host";
					m_pendingNetworkPlayerIdentities[0] = PlayerRecordKeyResolver.GetNetworkRecordKey(0);
					if (msg.TransferId > 0)
					{
						m_worldTransferRegistry.PendingWorldReadyTransferId = msg.TransferId;
						m_worldTransferRegistry.PendingCircuitReadyTransferId = 0;
						m_projectReadySentProject = null;
						m_projectReadySentTransferId = 0;
						m_nextClientJoinReadyRetryTime = 0.0;
						RecordClientJoinProgress();
						NetworkMessageSender.SendPakWorldReady(new GamePakWorldReadyMessage(msg.TransferId, GamePakWorldReadyStage.LoadingProject));
					}
					Log.Information("[ScMP] World imported, entering game: " + importedWorld.DirectoryName);
				}
				else
				{
					Log.Error("[ScMP] World imported but not found in world list: " + msg.Name);
					m_clientWorldRefreshProject = null;
					m_isLoadingDownloadedWorld = false;
					HideJoinRoomBusyDialog();
					DialogsManager.ShowDialog(null, new MessageDialog("Join Room", "The downloaded host world could not be opened.", "OK", null, null));
				}
			}
			catch (Exception ex)
			{
				m_clientWorldRefreshProject = null;
				m_isLoadingDownloadedWorld = false;
				HideJoinRoomBusyDialog();
				Log.Error("[ScMP] Failed to import world: " + ex.Message);
				DialogsManager.ShowDialog(null, new MessageDialog("Join Room", "Failed to load the host world: " + ex.Message, "OK", null, null));
			}
		}
	}

	private string ImportNetworkWorld(Stream sourceStream)
	{
		if (MarketplaceManager.IsTrialMode)
		{
			throw new InvalidOperationException("Cannot import worlds in trial mode.");
		}
		if (WorldsManager.WorldInfos.Count >= 30)
		{
			throw new InvalidOperationException("Too many worlds on device, maximum allowed is 30. Delete some to free up space.");
		}
		string directoryName = GetUnusedNetworkWorldDirectoryName();
		NetworkWorldSessionAssets sessionAssets = new NetworkWorldSessionAssets();
		Storage.CreateDirectory(directoryName);
		try
		{
			using Game.ZipArchive zipArchive = Game.ZipArchive.Open(sourceStream, keepStreamOpen: true);
			foreach (Game.ZipArchiveEntry entry in zipArchive.ReadCentralDir())
			{
				string filename = NormalizeNetworkWorldZipPath(entry.FilenameInZip);
				if (string.IsNullOrEmpty(filename))
				{
					continue;
				}
				string extension = Storage.GetExtension(filename).ToLowerInvariant();
				if (filename.StartsWith("EmbeddedContent/", StringComparison.OrdinalIgnoreCase))
				{
					ImportNetworkWorldEmbeddedAsset(zipArchive, entry, filename, extension, sessionAssets);
					continue;
				}
				string path = Storage.CombinePaths(directoryName, filename);
				Storage.CreateDirectory(Storage.GetDirectoryName(path));
				using Stream target = Storage.OpenFile(path, OpenFileMode.Create);
				zipArchive.ExtractFile(entry, target);
			}
			if (!TestNetworkWorldProjectXml(directoryName))
			{
				throw new InvalidOperationException("Cannot import world because it does not contain valid world data.");
			}
			m_sessionAssetRegistry.WorldSessionAssets = sessionAssets;
			return directoryName;
		}
		catch
		{
			try
			{
				WorldsManager.DeleteWorld(directoryName);
			}
			catch
			{
			}
			ClearNetworkWorldSessionAssets(sessionAssets);
			throw;
		}
	}

	private static string GetUnusedNetworkWorldDirectoryName()
	{
		string root = Storage.CombinePaths("data:/Worlds", "World");
		for (int i = 0; i < 1000; i++)
		{
			string directory = Storage.CombinePaths(Storage.GetDirectoryName(root), Storage.GetFileNameWithoutExtension(root) + ((i > 0) ? i.ToString(CultureInfo.InvariantCulture) : string.Empty) + Storage.GetExtension(root));
			if (!Storage.DirectoryExists(directory) && !Storage.FileExists(directory))
			{
				return directory;
			}
		}
		throw new InvalidOperationException("Out of filenames for network world import.");
	}

	private static string NormalizeNetworkWorldZipPath(string filename)
	{
		return WorldTransferPathPolicy.TryNormalizeZipPath(filename, out string normalized)
			? normalized
			: string.Empty;
	}

	private void ImportNetworkWorldEmbeddedAsset(Game.ZipArchive zipArchive, Game.ZipArchiveEntry entry, string filename, string extension, NetworkWorldSessionAssets sessionAssets)
	{
		try
		{
			if (extension == ".scskin" && entry.FileSize <=
				WorldTransferPathPolicy.GetEmbeddedAssetLimit(extension))
			{
				byte[] data2 = ExtractZipEntryBytes(zipArchive, entry,
					WorldTransferPathPolicy.GetEmbeddedAssetLimit(extension));
				byte[] hash = SHA256.HashData(data2);
				string skinName = Storage.GetFileName(filename);
				StoreSessionSkinAsset(0, skinName, CharacterSkinsManager.GetPlayerClass(skinName).GetValueOrDefault(), hash, data2);
			}
			else if (extension == ".scbtex" && entry.FileSize <=
				WorldTransferPathPolicy.GetEmbeddedAssetLimit(extension) &&
				sessionAssets.BlocksTextureData.Length == 0)
			{
				byte[] data = ExtractZipEntryBytes(zipArchive, entry,
					WorldTransferPathPolicy.GetEmbeddedAssetLimit(extension));
				ValidateBlocksTextureAssetData(data);
				sessionAssets.BlocksTextureName = Storage.GetFileName(filename);
				sessionAssets.BlocksTextureData = data;
			}
		}
		catch (Exception ex)
		{
			Log.Warning("[ScMP] Skipped embedded network asset \"" + filename + "\": " + ex.Message);
		}
	}

	private static byte[] ExtractZipEntryBytes(Game.ZipArchive zipArchive, Game.ZipArchiveEntry entry, int maximumBytes)
	{
		using MemoryStream memory = new MemoryStream();
		zipArchive.ExtractFile(entry, memory);
		if (memory.Length > maximumBytes)
		{
			throw new InvalidOperationException($"Asset is larger than {maximumBytes} bytes.");
		}
		return memory.ToArray();
	}

	private static Image ValidateBlocksTextureAssetData(byte[] data)
	{
		if (data == null || data.Length == 0 || data.Length > 4194304)
		{
			throw new InvalidOperationException("Invalid blocks texture size.");
		}
		Image image = Image.Load(new MemoryStream(data));
		if (image.Width > 1024 || image.Height > 1024)
		{
			throw new InvalidOperationException($"Blocks texture is larger than 1024x1024 pixels (size={image.Width}x{image.Height}).");
		}
		if (!MathUtils.IsPowerOf2(image.Width) || !MathUtils.IsPowerOf2(image.Height))
		{
			throw new InvalidOperationException($"Blocks texture does not have power-of-two size (size={image.Width}x{image.Height}).");
		}
		return image;
	}

	private static bool TestNetworkWorldProjectXml(string directoryName)
	{
		try
		{
			string fileName = Storage.CombinePaths(directoryName, "Project.xml");
			if (!Storage.FileExists(fileName))
			{
				return false;
			}
			using Stream stream = Storage.OpenFile(fileName, OpenFileMode.Read);
			return XElement.Load(stream).Name == "Project";
		}
		catch
		{
			return false;
		}
	}

	private void ApplyNetworkWorldTexture(Project project)
	{
		NetworkWorldSessionAssets assets = m_sessionAssetRegistry.WorldSessionAssets;
		if (IsHost || project == null || assets == null || assets.BlocksTextureData == null || assets.BlocksTextureData.Length == 0 || assets.BlocksTextureLoadFailed)
		{
			return;
		}
		string expectedName = project.FindSubsystem<SubsystemGameInfo>(throwOnError: false)?.WorldSettings.BlocksTextureName ?? string.Empty;
		if (BlocksTexturesManager.IsBuiltIn(expectedName) || (!string.IsNullOrEmpty(assets.BlocksTextureName) && !string.Equals(assets.BlocksTextureName, expectedName, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}
		if (assets.BlocksTexture == null)
		{
			try
			{
				Image image = ValidateBlocksTextureAssetData(assets.BlocksTextureData);
				assets.BlocksTexture = Texture2D.Load(image);
				assets.BlocksTexture.Tag = image;
			}
			catch (Exception ex)
			{
				assets.BlocksTextureLoadFailed = true;
				Log.Warning("[ScMP] Could not create session blocks texture \"" + expectedName + "\": " + ex.Message);
				return;
			}
		}
		SubsystemBlocksTexture subsystemBlocksTexture = project.FindSubsystem<SubsystemBlocksTexture>(throwOnError: false);
		if (subsystemBlocksTexture == null)
		{
			return;
		}
		Texture2D currentTexture = subsystemBlocksTexture.BlocksTexture;
		if (currentTexture == assets.BlocksTexture)
		{
			assets.AppliedProject = project;
			return;
		}
		if (currentTexture != null && !ContentManager.IsContent(currentTexture))
		{
			currentTexture.Dispose();
		}
		ModManager.ModParentField.ModifyParentField(subsystemBlocksTexture, "<BlocksTexture>k__BackingField", assets.BlocksTexture, typeof(SubsystemBlocksTexture));
		ResetAnimatedBlocksTexture(project);
		assets.AppliedProject = project;
	}

	private static void ResetAnimatedBlocksTexture(Project project)
	{
		SubsystemAnimatedTextures animatedTextures = project?.FindSubsystem<SubsystemAnimatedTextures>(throwOnError: false);
		if (animatedTextures != null)
		{
			ModManager.ModParentField.GetParentField<RenderTarget2D>(animatedTextures, "m_animatedBlocksTexture", typeof(SubsystemAnimatedTextures))?.Dispose();
			ModManager.ModParentField.ModifyParentField(animatedTextures, "m_animatedBlocksTexture", null, typeof(SubsystemAnimatedTextures));
		}
	}

	private void ClearSessionAssets()
	{
		ClearNetworkWorldSessionAssets(m_sessionAssetRegistry.DetachWorldSessionAssets());
		m_sessionAssetRegistry.Reset();
		m_lastLocalProfileSignature = null;
	}

	private static void ClearNetworkWorldSessionAssets(NetworkWorldSessionAssets assets)
	{
		if (assets?.BlocksTexture != null && assets.AppliedProject == null && !ContentManager.IsContent(assets.BlocksTexture))
		{
			assets.BlocksTexture.Dispose();
		}
	}

    }
}

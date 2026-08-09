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
	private sealed class GameWidgetListScope : IDisposable
	{
		private List<GameWidget> m_gameWidgets;

		private GameWidget[] m_originalViews;

		public GameWidgetListScope(List<GameWidget> gameWidgets, GameWidget[] originalViews)
		{
			m_gameWidgets = gameWidgets;
			m_originalViews = originalViews;
		}

		public void Dispose()
		{
			if (m_gameWidgets != null)
			{
				m_gameWidgets.Clear();
				m_gameWidgets.AddRange(m_originalViews);
				m_gameWidgets = null;
				m_originalViews = null;
			}
		}
	}

	private sealed class AutoDespawnScope : IDisposable
	{
		private List<ComponentSpawn> m_spawns;

		public AutoDespawnScope(List<ComponentSpawn> spawns)
		{
			m_spawns = spawns;
		}

		public void Dispose()
		{
			if (m_spawns == null)
			{
				return;
			}
			foreach (ComponentSpawn spawn in m_spawns)
			{
				ModManager.ModParentField.ModifyParentField(spawn, "<AutoDespawn>k__BackingField", true, typeof(ComponentSpawn));
			}
			m_spawns = null;
		}
	}

	private sealed class SubsystemBodiesScope : IDisposable
	{
		private SubsystemBodies m_subsystemBodies;

		private MethodInfo m_addBody;

		private ComponentBody[] m_bodies;

		public SubsystemBodiesScope(SubsystemBodies subsystemBodies, MethodInfo addBody, ComponentBody[] bodies)
		{
			m_subsystemBodies = subsystemBodies;
			m_addBody = addBody;
			m_bodies = bodies;
		}

		public void Dispose()
		{
			if (m_subsystemBodies != null)
			{
				ComponentBody[] bodies = m_bodies;
				foreach (ComponentBody body in bodies)
				{
					m_addBody.Invoke(m_subsystemBodies, new object[1] { body });
				}
				m_subsystemBodies = null;
				m_addBody = null;
				m_bodies = null;
			}
		}
	}

	private void MaintainClientWorldObjects()
	{
		Project project = GameManager.Project;
		if (project == null || IsHost)
		{
			return;
		}
		if (m_clientWorldObjectsProject != project)
		{
			m_clientWorldObjectsProject = project;
			m_remoteAnimals.Clear();
			m_remoteAnimalSync.Clear();
			m_lastFullAnimalSnapshotTick = 0;
			m_remotePickables.Clear();
			m_remotePickableStates.Clear();
			m_pendingPickablePickups.Clear();
			m_pendingPickableAcquireRequests.Clear();
		}
		HashSet<Entity> remoteAnimalSet = new HashSet<Entity>(m_remoteAnimals.Values.Where((Entity entity) => entity != null));
		Entity[] array = project.Entities.Where((Entity entity) => entity?.FindComponent<ComponentCreature>() != null && entity.FindComponent<ComponentPlayer>() == null && !remoteAnimalSet.Contains(entity)).ToArray();
		foreach (Entity entity2 in array)
		{
			if (entity2 != null && entity2.IsAddedToProject)
			{
				project.RemoveEntity(entity2, disposeEntity: true);
			}
		}
		SubsystemPickables subsystem = project.FindSubsystem<SubsystemPickables>(throwOnError: false);
		if (subsystem == null)
		{
			return;
		}
		HashSet<Pickable> remotePickableSet = new HashSet<Pickable>(m_remotePickables.Values.Where((Pickable pickable) => pickable != null));
		foreach (Pickable pickable2 in subsystem.Pickables)
		{
			if (pickable2 != null && !remotePickableSet.Contains(pickable2))
			{
				pickable2.ToRemove = true;
			}
		}
	}

	private void CreateNetworkPlayer(int clientId, string requestedName, string playerIdentity = null)
	{
		if (!IsHost && clientId != 0 && m_departedRemoteClientIds.Contains(clientId))
		{
			return;
		}
		if (GameManager.Project == null)
		{
			m_pendingNetworkPlayers[clientId] = requestedName;
			m_pendingNetworkPlayerIdentities[clientId] = playerIdentity ?? string.Empty;
			return;
		}
		lock (m_creatingNetworkPlayers)
		{
			if (m_networkPlayerData.ContainsKey(clientId) || !m_creatingNetworkPlayers.Add(clientId))
			{
				return;
			}
		}
		Project project = GameManager.Project;
		SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(throwOnError: true);
		PlayerData playerData = null;
		Entity entity = null;
		try
		{
			PlayerData hostPlayer = players.PlayersData.FirstOrDefault();
			string playerName = (string.IsNullOrWhiteSpace(requestedName) ? ("NetPlayer" + clientId) : requestedName.Trim());
			if (playerName.Length > 14)
			{
				playerName = playerName.Substring(0, 14);
			}
			string recordKey = (string.IsNullOrWhiteSpace(playerIdentity) ? playerName : playerIdentity);
			m_playerRecords.TryGetValue(recordKey, out var record);
			if (record != null)
			{
				RegisterPlayerSkinHash(clientId, record);
			}
			playerData = new PlayerData(project)
			{
				Name = (record?.Name ?? playerName),
				PlayerClass = (record?.PlayerClass ?? PlayerClass.Male),
				Level = (record?.Level ?? 1f),
				InputDevice = WidgetInputDevice.None,
				SpawnPosition = (record?.SpawnPosition != Vector3.Zero
					? record.SpawnPosition
					: (players.GlobalSpawnPosition != Vector3.Zero
						? players.GlobalSpawnPosition
						: (hostPlayer?.ComponentPlayer?.ComponentBody.Position ?? record?.Position ?? Vector3.Zero)))
			};
			if (!string.IsNullOrEmpty(record?.SkinName))
			{
				playerData.CharacterSkinName = record.SkinName;
			}
			int reservedPlayerIndex;
			int freePlayerIndex = (m_reservedNetworkPlayerIndices.TryGetValue(clientId, out reservedPlayerIndex) ? reservedPlayerIndex : FindAvailableNetworkPlayerIndex(players));
			if (players.PlayersData.Any((PlayerData player) => player.PlayerIndex == freePlayerIndex) || m_networkPlayerData.Values.Any((PlayerData player) => player.PlayerIndex == freePlayerIndex))
			{
				throw new InvalidOperationException("Reserved remote player index is occupied.");
			}
			ModManager.ModParentField.ModifyParentField(players, "m_nextPlayerIndex", freePlayerIndex, typeof(SubsystemPlayers));
			players.AddPlayerData(playerData);
			ValuesDictionary overrides = new ValuesDictionary
			{
				{
					"Player",
					new ValuesDictionary { { "PlayerIndex", playerData.PlayerIndex } }
				},
				{
					"Intro",
					new ValuesDictionary { { "PlayIntro", false } }
				}
			};
			bool initialSpawn = IsHost && record != null && !record.HasReceivedInitialItems;
			if (initialSpawn)
			{
				InvokeInitialPlayerSpawn(playerData, record.Position);
				entity = playerData.ComponentPlayer?.Entity ?? throw new InvalidOperationException("Initial network player spawn failed.");
				record.HasReceivedInitialItems = true;
			}
			else
			{
				entity = DatabaseManager.CreateEntity(project, playerData.GetEntityTemplateName(), overrides, throwIfNotFound: true);
				entity.FindComponent<ComponentBody>(throwOnError: true).Position = record?.Position ?? (playerData.SpawnPosition + new Vector3(1f, 0f, 0f));
				project.AddEntity(entity);
			}
			SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(throwOnError: true);
			foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
			{
				if (!IsHost && (updateable is ComponentPlayer || updateable is ComponentInput || updateable is ComponentLocomotion || updateable is ComponentMiner))
				{
					subsystemUpdate.RemoveUpdateable(updateable);
				}
			}
			ModManager.ModParentField.GetParentField<Dictionary<Entity, bool>>(project, "m_entities", typeof(Project)).Remove(entity);
			IInventory inventory = playerData.ComponentPlayer?.ComponentMiner?.Inventory;
			ConfigureNetworkPlayerInventory(inventory);
			RestorePlayerRecordInventory(inventory, record);
			RestorePlayerRecordCrafting(playerData.ComponentPlayer?.Entity?.FindComponent<ComponentCraftingTable>(), record);
			ApplyClothes(playerData.ComponentPlayer, record?.Clothes);
			if (record != null)
			{
				ApplyAuthoritativePlayerStats(playerData.ComponentPlayer, record.Health, record.Air, record.Food, record.Stamina, record.Sleep, record.Temperature, record.Wetness, record.Level);
				ApplyPlayerRecordState(playerData.ComponentPlayer, record);
			}
			if (IsHost && initialSpawn)
			{
				record = CapturePlayerRecord(playerData);
				record.HasReceivedInitialItems = true;
				m_playerRecords[recordKey] = record;
				m_playerRecordsDirty = true;
				SavePlayerRecords();
			}
			ModManager.ModParentField.GetParentField<StateMachine>(playerData, "m_stateMachine", typeof(PlayerData)).TransitionTo("Playing");
			SubsystemGameWidgets gameWidgets = project.FindSubsystem<SubsystemGameWidgets>(throwOnError: true);
			GameWidget networkGameWidget = playerData.GameWidget;
			ModManager.ModParentMethod.GetInstanceMethodInfo(typeof(SubsystemGameWidgets), "RemoveGameWidget", new Type[1] { typeof(GameWidget) }).Invoke(gameWidgets, new object[1] { networkGameWidget });
			ModManager.ModParentField.GetParentField<List<PlayerData>>(players, "m_playersData", typeof(SubsystemPlayers)).Remove(playerData);
			List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(players, "m_componentPlayers", typeof(SubsystemPlayers));
			if (playerData.ComponentPlayer != null && !componentPlayers.Contains(playerData.ComponentPlayer))
			{
				componentPlayers.Add(playerData.ComponentPlayer);
			}
			m_networkPlayerData.Add(clientId, playerData);
			m_reservedNetworkPlayerIndices.Remove(clientId);
			m_clientRecordKeys[clientId] = recordKey;
			m_pendingNetworkPlayers.Remove(clientId);
			m_pendingNetworkPlayerIdentities.Remove(clientId);
			if (clientId == 0)
			{
				m_shouldCreateHostAvatar = false;
			}
			if (IsHost)
			{
				m_lastEquipmentSnapshots.Remove(clientId);
				SynchronizeHostEquipment(clientId, playerData.ComponentPlayer);
			}
			else
			{
				TryApplyPendingPlayerEquipment(clientId);
			}
			ApplySessionSkinToPlayer(playerData.ComponentPlayer, clientId, record);
			Log.Information($"[ScMP] Created transient network player for ClientID {clientId}, PlayerIndex={playerData.PlayerIndex}");
		}
		catch (Exception ex)
		{
			List<PlayerData> playerList = ModManager.ModParentField.GetParentField<List<PlayerData>>(players, "m_playersData", typeof(SubsystemPlayers));
			if (playerData != null)
			{
				playerList.Remove(playerData);
			}
			if (entity != null && entity.IsAddedToProject)
			{
				project.RemoveEntity(entity, disposeEntity: true);
			}
			RemoveNetworkSimulationView(playerData);
			playerData?.Dispose();
			m_pendingNetworkPlayers[clientId] = requestedName;
			m_pendingNetworkPlayerIdentities[clientId] = playerIdentity ?? string.Empty;
			Log.Error($"[ScMP] Failed to create network player for ClientID {clientId}: {ex.Message}");
		}
		finally
		{
			lock (m_creatingNetworkPlayers)
			{
				m_creatingNetworkPlayers.Remove(clientId);
			}
		}
	}

    private void RemoveNetworkPlayer(int clientId)
    {
        RemotePlayers.Remove(clientId);
        RemoveReliableRelayReservations(clientId);
        m_worldTransferRegistry.RemoveClient(clientId);
		m_joinCatchUpRegistry.RemoveClient(clientId);
		m_hostTerrainRecoveryTargets.Remove(clientId);
		m_pendingAcceptedJoinKeys.Remove(clientId);
		m_hostPlayerPokingPhases.Remove(clientId);
		m_hostPlayerPokeSequences.Remove(clientId);
		m_hostKnockbackHealthCache.Remove(clientId);
		m_hostRemoteKnockbackUntil.Remove(clientId);
		m_lastSentAuthoritativePlayerStates.Remove(clientId);
		m_authoritativePlayerStateSequences.Remove(clientId);
		m_lastReceivedAuthoritativePlayerStateSequences.Remove(clientId);
		m_hostWorldControlRequestStates.Remove(clientId);
		m_pendingPlayerEquipmentMessages.Remove(clientId);
		m_equipmentAuthorityRevisions.Remove(clientId);
		m_lastClientEquipmentRevisions.Remove(clientId);
		m_lastReceivedEquipmentRevisions.Remove(clientId);
		m_lastEquipmentSnapshots.Remove(clientId);
		m_equipmentSynchronizedClients.Remove(clientId);
		m_reservedNetworkPlayerIndices.Remove(clientId);
		m_worldObjectSynchronizer?.ForgetClient(clientId);
		if (!m_networkPlayerData.TryGetValue(clientId, out var playerData))
		{
			return;
		}
		if (playerData != null && playerData.PlayerIndex >= 0)
		{
			GameManager.Project?.FindSubsystem<SubsystemTerrain>(throwOnError: false)?.TerrainUpdater.RemoveUpdateLocation(playerData.PlayerIndex);
		}
		string key2;
		string recordKey = (m_clientRecordKeys.TryGetValue(clientId, out key2) ? key2 : playerData.Name);
		NetworkPlayerRecord record = CapturePlayerRecord(playerData);
		m_playerRecords[recordKey] = record;
		if (IsHost)
		{
			m_playerRecordsDirty = true;
			SavePlayerRecords();
			int clientId2 = clientId;
			string[] obj = new string[10]
			{
				"position=",
				PlayerProfileValueCodec.FormatFloat(record.Position.X),
				",",
				PlayerProfileValueCodec.FormatFloat(record.Position.Y),
				",",
				PlayerProfileValueCodec.FormatFloat(record.Position.Z),
				" inventory_slots=",
				null,
				null,
				null
			};
			int[] slotValues = record.SlotValues;
			int val = ((slotValues != null) ? slotValues.Length : 0);
			int[] slotCounts = record.SlotCounts;
			obj[7] = Math.Min(val, (slotCounts != null) ? slotCounts.Length : 0).ToString(CultureInfo.InvariantCulture);
			obj[8] = " handcraft_slots=";
			int[] handcraftSlotValues = record.HandcraftSlotValues;
			int val2 = ((handcraftSlotValues != null) ? handcraftSlotValues.Length : 0);
			int[] handcraftSlotCounts = record.HandcraftSlotCounts;
			obj[9] = Math.Min(val2, (handcraftSlotCounts != null) ? handcraftSlotCounts.Length : 0).ToString(CultureInfo.InvariantCulture);
			PublishServerAudit("player.leave_saved", clientId2, string.Concat(obj));
		}
		SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(throwOnError: false);
		if (players != null)
		{
			RemoveNetworkSimulationView(playerData);
			ModManager.ModParentField.GetParentField<List<PlayerData>>(players, "m_playersData", typeof(SubsystemPlayers)).Remove(playerData);
			List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(players, "m_componentPlayers", typeof(SubsystemPlayers));
			if (playerData.ComponentPlayer != null)
			{
				componentPlayers.Remove(playerData.ComponentPlayer);
				GameManager.Project.RemoveEntity(playerData.ComponentPlayer.Entity, disposeEntity: true);
			}
			playerData.Dispose();
		}
		m_networkPlayerData.Remove(clientId);
		m_networkPlayerInputs.Remove(clientId);
		m_pendingNetworkPlayers.Remove(clientId);
		m_pendingNetworkPlayerIdentities.Remove(clientId);
		m_clientRecordKeys.Remove(clientId);
		string playerContainerPrefix = "player:" + clientId.ToString(CultureInfo.InvariantCulture) + ":";
		string[] array = m_containerStates.Keys.Where((string key) => key.StartsWith(playerContainerPrefix, StringComparison.Ordinal)).ToArray();
		foreach (string containerKey in array)
		{
			m_containerStates.Remove(containerKey);
		}
		array = m_processedContainerTransactions.Keys.Where((string key) => key.Contains("|" + playerContainerPrefix, StringComparison.Ordinal)).ToArray();
		foreach (string transactionKey in array)
		{
			m_processedContainerTransactions.Remove(transactionKey);
		}
		if (IsHost && GameManager.Project != null)
		{
			GameManager.SaveProject(waitForCompletion: false, showErrorDialog: false);
		}
	}

	private static void RemoveNetworkSimulationView(PlayerData playerData)
	{
		SubsystemGameWidgets gameWidgets = playerData?.SubsystemGameWidgets;
		GameWidget gameWidget = ((gameWidgets != null) ? gameWidgets.GameWidgets.FirstOrDefault((GameWidget item) => item.PlayerData == playerData) : null);
		if (gameWidget != null && gameWidgets != null && gameWidgets.GameWidgets.Contains(gameWidget))
		{
			ModManager.ModParentMethod.GetInstanceMethodInfo(typeof(SubsystemGameWidgets), "RemoveGameWidget", new Type[1] { typeof(GameWidget) }).Invoke(gameWidgets, new object[1] { gameWidget });
		}
	}

	private void MaintainRemoteTerrainLocations(Project project)
	{
		if (project == null)
		{
			return;
		}
		SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(throwOnError: false);
		if (terrain?.TerrainUpdater == null)
		{
			return;
		}
		if (!IsHost)
		{
			// Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.SetUpdateLocation
			// A client owns only its local terrain window. Remote avatars must not create extra
			// updater locations, otherwise their exploration becomes this client's Apply backlog.
			foreach (PlayerData remotePlayer in m_networkPlayerData.Values.ToArray())
			{
				if (remotePlayer?.PlayerIndex >= 0)
					terrain.TerrainUpdater.RemoveUpdateLocation(remotePlayer.PlayerIndex);
			}
			SubsystemPlayers localPlayers = project.FindSubsystem<SubsystemPlayers>(throwOnError: false);
			PlayerData localPlayer = localPlayers?.PlayersData.FirstOrDefault(item =>
				!m_networkPlayerData.Values.Contains(item));
			if (localPlayer?.ComponentPlayer?.ComponentBody != null && localPlayer.PlayerIndex >= 0)
			{
				float clientVisibility = project.FindSubsystem<SubsystemSky>(throwOnError: false)?.VisibilityRange ?? 64f;
				terrain.TerrainUpdater.SetUpdateLocation(localPlayer.PlayerIndex,
					localPlayer.ComponentPlayer.ComponentBody.Position.XZ,
					clientVisibility, clientVisibility);
				RefreshClientTerrainInterestChunks(project,
					localPlayer.ComponentPlayer.ComponentBody.Position.XZ,
					MathUtils.Min(clientVisibility, 64f));
			}
			return;
		}
		float hostVisibility = MathUtils.Min(project.FindSubsystem<SubsystemSky>(throwOnError: false)?.VisibilityRange ?? 64f, 64f);
		PlayerData[] array = m_networkPlayerData.Values.ToArray();
		foreach (PlayerData playerData in array)
		{
			if (playerData?.ComponentPlayer?.ComponentBody != null && playerData.PlayerIndex >= 0)
			{
				Vector3 position = playerData.ComponentPlayer.ComponentBody.Position;
				terrain.TerrainUpdater.SetUpdateLocation(playerData.PlayerIndex, position.XZ, hostVisibility, 64f);
			}
		}
		UpdateHostTerrainInterestTable(project);
	}

	// Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.SetUpdateLocation
	// A chunk may already be Valid when a player enters its range. Queue a compact authoritative
	// checkpoint for newly entered chunks so remote edits are visible without a manual interaction.
	private void RefreshClientTerrainInterestChunks(Project project, Vector2 center,
		float visibility)
	{
		if (IsHost || project == null) return;
		Point2 chunkCenter = Terrain.ToChunk(center);
		int radius = Math.Max(1, (int)Math.Ceiling(visibility / 16f));
		var desired = new HashSet<Point2>();
		for (int x = chunkCenter.X - radius; x <= chunkCenter.X + radius; x++)
		{
			for (int z = chunkCenter.Y - radius; z <= chunkCenter.Y + radius; z++)
			{
				var coordinates = new Point2(x, z);
				desired.Add(coordinates);
				bool enteredInterest = !m_clientTerrainInterestInitialized ||
					!m_clientTerrainInterestChunks.Contains(coordinates);
				TerrainChunk chunk = project.FindSubsystem<SubsystemTerrain>(false)?
					.Terrain.GetChunkAtCoords(x, z);
				// Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.ChunkInitialized
				// ChunkInitialized can be missed when the chunk becomes Valid before the
				// checkpoint updater is attached. Re-check every valid chunk in the local
				// interest window until it has a per-chunk confirmed revision.
				bool checkpointMissing = !m_clientTerrainChunkRevisions.ContainsKey(coordinates) &&
					!m_clientTerrainChunkSyncPending.ContainsKey(coordinates) &&
					!m_clientTerrainChunkSyncQueued.Contains(coordinates);
				if ((enteredInterest || checkpointMissing) &&
					chunk != null && chunk.State >= TerrainChunkState.Valid)
				{
					QueueClientTerrainChunkSync(coordinates);
				}
			}
		}
		m_clientTerrainInterestChunks.Clear();
		m_clientTerrainInterestChunks.UnionWith(desired);
		m_clientTerrainInterestInitialized = true;
	}

	private void UpdateClientTerrainChunkSync(Project project)
	{
		if (IsHost || project == null)
		{
			if (m_clientTerrainChunkSyncUpdater != null)
			{
				DetachClientTerrainChunkSyncUpdater();
			}
			if (m_clientTerrainChunkSyncQueue.Count > 0 || m_clientTerrainChunkSyncQueued.Count > 0 || m_clientTerrainChunkSyncPending.Count > 0 || m_clientTerrainChunkRevisions.Count > 0 || m_clientTerrainChunkVerifications.Count > 0 || m_worldTransferRegistry.ClientTerrainChunkBaselineRevision > 0 || m_worldTransferRegistry.ClientTerrainJoinBaselineRevision > 0)
			{
				ResetClientTerrainChunkSyncState();
			}
			return;
		}
		SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(throwOnError: false);
		TerrainUpdater updater = terrain?.TerrainUpdater;
		if (m_clientTerrainChunkSyncUpdater != updater)
		{
			DetachClientTerrainChunkSyncUpdater();
			ResetClientTerrainChunkSyncState();
			if (updater != null)
			{
				m_clientTerrainChunkSyncUpdater = updater;
				updater.ChunkInitialized += OnClientTerrainChunkInitialized;
			}
		}
		if (updater == null)
		{
			return;
		}
		Client obj = client;
		if (obj == null || !obj.IsConnected || m_isLoadingDownloadedWorld || m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
		{
			return;
		}
		double now = Time.RealTime;
		Point2[] array = (from item in m_clientTerrainChunkSyncPending
			where now - item.Value >= 5.0
			select item.Key).ToArray();
		foreach (Point2 coordinates2 in array)
		{
			if (m_clientTerrainChunkCheckpoints.TryGetValue(coordinates2, out var checkpoint) && checkpoint.Revision > 0 && (checkpoint.CompleteReceived || checkpoint.AppliedBatches < checkpoint.ReceivedBatches))
			{
				m_clientTerrainChunkSyncPending[coordinates2] = now;
				continue;
			}
			m_clientTerrainChunkSyncPending.Remove(coordinates2);
			QueueClientTerrainChunkSync(coordinates2);
		}
		KeyValuePair<Point2, PendingTerrainChunkVerification>[] array2 = m_clientTerrainChunkVerifications.ToArray();
		for (int i = 0; i < array2.Length; i++)
		{
			KeyValuePair<Point2, PendingTerrainChunkVerification> item2 = array2[i];
			if (GetClientTerrainChunkRevision(item2.Key) >= item2.Value.RequiredRevision)
			{
				m_clientTerrainChunkVerifications.Remove(item2.Key);
			}
			else if (!(now < item2.Value.DueTime))
			{
				TerrainChunk chunk2 = terrain.Terrain.GetChunkAtCoords(item2.Key.X, item2.Key.Y);
				if (chunk2 != null && chunk2.State >= TerrainChunkState.Valid)
				{
					QueueClientTerrainChunkSync(item2.Key);
				}
			}
		}
		if ((m_clientTerrainChunkSyncQueue.Count > 0 && (m_terrainChunkSyncActions.Count >= 32 || SuSubsystemTerrain.PendingChunkCheckpointCount >= 16)) || now < m_nextTerrainChunkSyncRequestTime)
		{
			return;
		}
		int sent = 0;
		while (sent < 4 && m_clientTerrainChunkSyncQueue.Count > 0)
		{
			Point2 coordinates = m_clientTerrainChunkSyncQueue.Dequeue();
			m_clientTerrainChunkSyncQueued.Remove(coordinates);
			TerrainChunk chunk = terrain.Terrain.GetChunkAtCoords(coordinates.X, coordinates.Y);
			if (chunk != null && chunk.State >= TerrainChunkState.Valid && !m_clientTerrainChunkSyncPending.ContainsKey(coordinates))
			{
				long knownRevision = GetClientTerrainChunkRevision(coordinates);
                NetworkMessageSender.SendRawMessage(0, new TerrainChunkSyncMessage
                {
                    Stage = TerrainChunkSyncStage.Request,
                    ChunkX = coordinates.X,
                    ChunkZ = coordinates.Y,
                    KnownRevision = knownRevision
                }, sequenced: true);
				m_clientTerrainChunkSyncPending[coordinates] = now;
				sent++;
			}
		}
		if (sent > 0)
		{
			m_nextTerrainChunkSyncRequestTime = now + 0.1;
		}
	}

	private void OnClientTerrainChunkInitialized(TerrainChunk chunk)
	{
		if (IsHost || chunk == null) return;
		if (m_clientTerrainInterestChunks.Contains(chunk.Coords))
		{
			QueueClientTerrainChunkSync(chunk.Coords);
			return;
		}
		if (m_clientTerrainChunkVerifications.TryGetValue(chunk.Coords,
			out var verification) &&
			GetClientTerrainChunkRevision(chunk.Coords) < verification.RequiredRevision)
			QueueClientTerrainChunkSync(chunk.Coords);
	}

	private long GetClientTerrainChunkRevision(Point2 coordinates)
	{
		// Source: ScMultiplayer.OnClientTerrainChunkCheckpointBatchApplied
		// The join baseline is a global sequence barrier, not proof that this
		// particular chunk was received. An unseen chunk must request its full
		// retained checkpoint from revision zero.
		return m_clientTerrainChunkRevisions.TryGetValue(coordinates, out var revision)
			? Math.Max(revision, 0L)
			: 0L;
	}

	private void QueueClientTerrainChunkSync(Point2 coordinates)
	{
		if (!m_clientTerrainChunkSyncPending.ContainsKey(coordinates) && m_clientTerrainChunkSyncQueued.Add(coordinates))
		{
			m_clientTerrainChunkSyncQueue.Enqueue(coordinates);
		}
	}

	private void DetachClientTerrainChunkSyncUpdater()
	{
		if (m_clientTerrainChunkSyncUpdater != null)
		{
			m_clientTerrainChunkSyncUpdater.ChunkInitialized -= OnClientTerrainChunkInitialized;
		}
		m_clientTerrainChunkSyncUpdater = null;
	}

	private void ResetClientTerrainChunkSyncState()
	{
		m_clientTerrainChunkSyncQueue.Clear();
		m_clientTerrainChunkSyncQueued.Clear();
		m_clientTerrainChunkSyncPending.Clear();
		m_clientTerrainChunkRevisions.Clear();
		m_clientTerrainInterestChunks.Clear();
		m_clientTerrainInterestInitialized = false;
		m_worldTransferRegistry.ResetClientTerrainBaselines();
		m_clientTerrainChunkVerifications.Clear();
		m_clientTerrainChunkCheckpoints.Clear();
		m_clientTerrainChunkFailedRevisions.Clear();
		m_nextTerrainChunkSyncRequestTime = 0.0;
	}

	internal IDisposable BeginRemoteSimulationViewScope(Project project)
	{
		if (!IsHost || project == null || m_networkPlayerData.Count == 0)
		{
			return null;
		}
		SubsystemGameWidgets subsystemViews = project.FindSubsystem<SubsystemGameWidgets>(throwOnError: false);
		if (subsystemViews == null)
		{
			return null;
		}
		List<GameWidget> gameWidgets = ModManager.ModParentField.GetParentField<List<GameWidget>>(subsystemViews, "m_gameWidgets", typeof(SubsystemGameWidgets));
		if (gameWidgets == null)
		{
			return null;
		}
		GameWidget[] originalViews = gameWidgets.ToArray();
		bool changed = false;
		PlayerData[] array = m_networkPlayerData.Values.ToArray();
		foreach (PlayerData playerData in array)
		{
			if (((playerData == null) ? null : ModManager.ModParentField.GetParentField(playerData, "m_gameWidget", typeof(PlayerData))) is GameWidget gameWidget && !gameWidgets.Contains(gameWidget))
			{
				gameWidget.ActiveCamera.Update(0f);
				gameWidgets.Add(gameWidget);
				changed = true;
			}
		}
		if (!changed)
		{
			return null;
		}
		return new GameWidgetListScope(gameWidgets, originalViews);
	}

	internal void MaintainRemoteCreatureSpawning(Project project, SubsystemSpawn subsystemSpawn)
	{
		if (!IsHost || project == null || subsystemSpawn == null || m_networkPlayerData.Count == 0 || Time.RealTime < m_nextRemoteCreatureSpawnTime)
		{
			return;
		}
		m_nextRemoteCreatureSpawnTime = Time.RealTime + 1.0;
		Vector3[] remotePositions = (from playerData in m_networkPlayerData.Values
			select playerData?.ComponentPlayer?.ComponentBody into body
			where body != null
			select body.Position into position
			where CountRemoteCreatures(project, position.XZ, 68f) < 26
			select position).ToArray();
		if (remotePositions.Length == 0)
		{
			return;
		}
		SubsystemGameWidgets subsystemViews = project.FindSubsystem<SubsystemGameWidgets>(throwOnError: false);
		SubsystemTerrain subsystemTerrain = project.FindSubsystem<SubsystemTerrain>(throwOnError: false);
		if (subsystemViews == null || subsystemTerrain == null)
		{
			return;
		}
		Vector2[] visiblePositions = subsystemViews.GameWidgets.Select((GameWidget gameWidget) => gameWidget.ActiveCamera.ViewPosition.XZ).ToArray();
		HashSet<Point2> candidatePoints = new HashSet<Point2>();
		Vector3[] array = remotePositions;
		foreach (Vector3 remotePosition in array)
		{
			Vector2 center = remotePosition.XZ;
			Point2 min = Terrain.ToChunk(center - new Vector2(48f));
			Point2 max = Terrain.ToChunk(center + new Vector2(48f));
			for (int x = min.X; x <= max.X; x++)
			{
				for (int z = min.Y; z <= max.Y; z++)
				{
					Vector2 chunkCenter = new Vector2(((float)x + 0.5f) * 16f, ((float)z + 0.5f) * 16f);
					if (!(Vector2.DistanceSquared(center, chunkCenter) >= 2304f) && !visiblePositions.Any((Vector2 position) => Vector2.DistanceSquared(position, chunkCenter) < 2304f))
					{
						TerrainChunk terrainChunk = subsystemTerrain.Terrain.GetChunkAtCell(Terrain.ToCell(chunkCenter.X), Terrain.ToCell(chunkCenter.Y));
						if (terrainChunk != null && terrainChunk.State > TerrainChunkState.InvalidPropagatedLight)
						{
							candidatePoints.Add(new Point2(x, z));
						}
					}
				}
			}
		}
		Point2[] candidates = (from point in candidatePoints
			orderby point.X, point.Y
			select point).ToArray();
		if (candidates.Length == 0)
		{
			return;
		}
		Point2 selectedPoint = candidates[m_remoteCreatureSpawnCursor % candidates.Length];
		m_remoteCreatureSpawnCursor = (m_remoteCreatureSpawnCursor + 1) % candidates.Length;
		Vector2 selectedChunkCenter = new Vector2(((float)selectedPoint.X + 0.5f) * 16f, ((float)selectedPoint.Y + 0.5f) * 16f);
		Vector2 selectedRemoteCenter = (from position in remotePositions
			select position.XZ into position
			orderby Vector2.DistanceSquared(position, selectedChunkCenter)
			select position).First();
		if (!(ModManager.ModParentMethod.GetInstanceMethodInfo(typeof(SubsystemSpawn), "GetOrCreateSpawnChunk", new Type[1] { typeof(Point2) })?.Invoke(subsystemSpawn, new object[1] { selectedPoint }) is SpawnChunk spawnChunk))
		{
			return;
		}
		int localCreatureCount = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
		int availableCreatureSlots = Math.Max(0, 26 - localCreatureCount);
		MethodInfo spawnEntity = ModManager.ModParentMethod.GetInstanceMethodInfo(typeof(SubsystemSpawn), "SpawnEntity", new Type[1] { typeof(SpawnEntityData) });
		int recordsToRestore = Math.Min(Math.Min(2, availableCreatureSlots), spawnChunk.SpawnsData.Count);
		for (int i = 0; i < recordsToRestore; i++)
		{
			SpawnEntityData record = spawnChunk.SpawnsData[0];
			spawnChunk.SpawnsData.RemoveAt(0);
			spawnEntity?.Invoke(subsystemSpawn, new object[1] { record });
		}
		localCreatureCount = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
		if (spawnChunk.SpawnsData.Count != 0 || localCreatureCount >= 26)
		{
			return;
		}
		SubsystemCreatureSpawn creatureSpawn = project.FindSubsystem<SubsystemCreatureSpawn>(throwOnError: false);
		MethodInfo spawnChunkCreatures = ModManager.ModParentMethod.GetInstanceMethodInfo(typeof(SubsystemCreatureSpawn), "SpawnChunkCreatures", new Type[3]
		{
			typeof(SpawnChunk),
			typeof(int),
			typeof(bool)
		});
		if (creatureSpawn != null && spawnChunkCreatures != null)
		{
			HashSet<Entity> existingCreatures = new HashSet<Entity>(FindRemoteCreatures(project, selectedRemoteCenter, 68f));
			using (BeginRemoteCreatureCountScope(project, selectedRemoteCenter, 68f))
			{
				int nonConstantAttempts = (spawnChunk.IsSpawned ? 1 : 10);
				spawnChunkCreatures.Invoke(creatureSpawn, new object[3] { spawnChunk, nonConstantAttempts, false });
				if (CountRemoteCreatures(project, selectedRemoteCenter, 68f) < 26)
				{
					spawnChunkCreatures.Invoke(creatureSpawn, new object[3] { spawnChunk, 2, true });
				}
				TrimRemoteCreatureOverflow(project, selectedRemoteCenter, 68f, 26, existingCreatures);
			}
		}
		spawnChunk.IsSpawned = true;
	}

	private static int CountRemoteCreatures(Project project, Vector2 center, float radius)
	{
		return FindRemoteCreatures(project, center, radius).Length;
	}

	private static Entity[] FindRemoteCreatures(Project project, Vector2 center, float radius)
	{
		SubsystemBodies subsystemBodies = project?.FindSubsystem<SubsystemBodies>(throwOnError: false);
		if (subsystemBodies == null)
		{
			return Array.Empty<Entity>();
		}
		float radiusSquared = radius * radius;
		return (from body in subsystemBodies.Bodies
			where body?.Entity.FindComponent<ComponentCreature>() != null && body.Entity.FindComponent<ComponentPlayer>() == null && Vector2.DistanceSquared(body.Position.XZ, center) <= radiusSquared
			select body.Entity).Distinct().ToArray();
	}

	private static void TrimRemoteCreatureOverflow(Project project, Vector2 center, float radius, int targetCount, HashSet<Entity> existingCreatures)
	{
		Entity[] creatures = FindRemoteCreatures(project, center, radius);
		int excess = creatures.Length - targetCount;
		if (excess <= 0)
		{
			return;
		}
		Entity[] array = creatures.Where((Entity entity) => !existingCreatures.Contains(entity)).Take(excess).ToArray();
		foreach (Entity entity2 in array)
		{
			if (entity2 != null && entity2.IsAddedToProject)
			{
				project.RemoveEntity(entity2, disposeEntity: true);
			}
		}
	}

	private IDisposable BeginRemoteCreatureCountScope(Project project, Vector2 center, float radius)
	{
		SubsystemBodies subsystemBodies = project?.FindSubsystem<SubsystemBodies>(throwOnError: false);
		if (subsystemBodies == null)
		{
			return null;
		}
		Dictionary<ComponentBody, Point2> areaByBody = ModManager.ModParentField.GetParentField<Dictionary<ComponentBody, Point2>>(subsystemBodies, "m_areaByComponentBody", typeof(SubsystemBodies));
		if (areaByBody == null)
		{
			return null;
		}
		float radiusSquared = radius * radius;
		ComponentBody[] outsideBodies = areaByBody.Keys.Where((ComponentBody body) => body?.Entity.FindComponent<ComponentCreature>() != null && Vector2.DistanceSquared(body.Position.XZ, center) > radiusSquared).ToArray();
		if (outsideBodies.Length == 0)
		{
			return null;
		}
		MethodInfo removeBody = ModManager.ModParentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies), "RemoveBody", new Type[1] { typeof(ComponentBody) });
		MethodInfo addBody = ModManager.ModParentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies), "AddBody", new Type[1] { typeof(ComponentBody) });
		if (removeBody == null || addBody == null)
		{
			return null;
		}
		ComponentBody[] array = outsideBodies;
		foreach (ComponentBody body2 in array)
		{
			removeBody.Invoke(subsystemBodies, new object[1] { body2 });
		}
		return new SubsystemBodiesScope(subsystemBodies, addBody, outsideBodies);
	}

	internal IDisposable BeginRemoteDespawnProtectionScope(Project project)
	{
		if (!IsHost || project == null || m_networkPlayerData.Count == 0)
		{
			return null;
		}
		Vector3[] remotePositions = (from playerData in m_networkPlayerData.Values
			select playerData?.ComponentPlayer?.ComponentBody into body
			where body != null
			select body.Position).ToArray();
		if (remotePositions.Length == 0)
		{
			return null;
		}
		SubsystemSpawn subsystemSpawn = project.FindSubsystem<SubsystemSpawn>(throwOnError: false);
		if (subsystemSpawn == null)
		{
			return null;
		}
		List<ComponentSpawn> protectedSpawns = new List<ComponentSpawn>();
		ComponentSpawn[] array = subsystemSpawn.Spawns.ToArray();
		foreach (ComponentSpawn spawn in array)
		{
			ComponentSpawn componentSpawn = spawn;
			if (componentSpawn != null && componentSpawn.AutoDespawn && spawn.ComponentFrame != null && remotePositions.Any((Vector3 position) => Vector3.DistanceSquared(position, spawn.ComponentFrame.Position) <= 3600f))
			{
				ComponentCreature componentCreature = spawn.ComponentCreature;
				bool isDead = componentCreature != null && componentCreature.ComponentHealth?.Health <= 0f;
				ComponentShapeshifter shapeshifter = spawn.Entity.FindComponent<ComponentShapeshifter>();
				bool isShapeshifting = !string.IsNullOrEmpty((shapeshifter == null) ? null : (ModManager.ModParentField.GetParentField(shapeshifter, "m_spawnEntityTemplateName", typeof(ComponentShapeshifter)) as string));
				if (isShapeshifting && !spawn.IsDespawning)
				{
					spawn.Despawn();
				}
				else if (spawn.IsDespawning && !isDead && !isShapeshifting)
				{
					ModManager.ModParentField.ModifyParentField(spawn, "<DespawnTime>k__BackingField", null, typeof(ComponentSpawn));
					RemovePersistedSpawnRecord(subsystemSpawn, spawn);
				}
				ModManager.ModParentField.ModifyParentField(spawn, "<AutoDespawn>k__BackingField", false, typeof(ComponentSpawn));
				protectedSpawns.Add(spawn);
			}
		}
		if (protectedSpawns.Count <= 0)
		{
			return null;
		}
		return new AutoDespawnScope(protectedSpawns);
	}

	private static void RemovePersistedSpawnRecord(SubsystemSpawn subsystemSpawn, ComponentSpawn spawn)
	{
		SpawnChunk chunk = subsystemSpawn.GetSpawnChunk(Terrain.ToChunk(spawn.ComponentFrame.Position.XZ));
		if (chunk != null && chunk.SpawnsData.Count != 0)
		{
			string templateName = spawn.Entity.ValuesDictionary.DatabaseObject.Name;
			Vector3 position = spawn.ComponentFrame.Position;
			bool constantSpawn = spawn.ComponentCreature?.ConstantSpawn ?? false;
			chunk.SpawnsData.RemoveAll((SpawnEntityData record) => record.TemplateName == templateName && record.ConstantSpawn == constantSpawn && Vector3.DistanceSquared(record.Position, position) < 0.01f);
		}
	}

	private void QueueRunawayCreatureCleanup(Project project)
	{
		m_runawayCreatureCleanup.Clear();
		m_runawayCreatureCleanupProject = project;
		if (project == null)
		{
			return;
		}
		ComponentCreature[] creatures = project.Entities.Select((Entity entity) => entity?.FindComponent<ComponentCreature>()).Where(delegate(ComponentCreature creature)
		{
			if (creature != null && creature.Entity.FindComponent<ComponentPlayer>() == null)
			{
				ComponentSpawn componentSpawn = creature.Entity.FindComponent<ComponentSpawn>();
				if (componentSpawn != null && componentSpawn.AutoDespawn)
				{
					return !creature.ConstantSpawn;
				}
			}
			return false;
		}).ToArray();
		SubsystemSpawn subsystemSpawn = project.FindSubsystem<SubsystemSpawn>(throwOnError: false);
		Dictionary<Point2, SpawnChunk> spawnChunks = ((subsystemSpawn == null) ? null : ModManager.ModParentField.GetParentField<Dictionary<Point2, SpawnChunk>>(subsystemSpawn, "m_chunks", typeof(SubsystemSpawn)));
		SpawnEntityData[] spawnRecords = (from record in spawnChunks?.Values.SelectMany((SpawnChunk chunk) => chunk.SpawnsData)
			where record != null && !record.ConstantSpawn
			select record).ToArray() ?? Array.Empty<SpawnEntityData>();
		if (creatures.Length + spawnRecords.Length <= 256)
		{
			return;
		}
		SubsystemPlayers subsystemPlayers = project.FindSubsystem<SubsystemPlayers>(throwOnError: false);
		Vector3[] playerPositions = ((subsystemPlayers != null) ? (from player in subsystemPlayers.ComponentPlayers
			where player?.ComponentBody != null
			select player.ComponentBody.Position).ToArray() : null) ?? Array.Empty<Vector3>();
		IEnumerable<ComponentCreature> source;
		if (playerPositions.Length == 0)
		{
			IEnumerable<ComponentCreature> enumerable = creatures;
			source = enumerable;
		}
		else
		{
			IEnumerable<ComponentCreature> enumerable = creatures.OrderBy((ComponentCreature creature) => playerPositions.Min((Vector3 position) => Vector3.DistanceSquared(position, creature.ComponentBody.Position)));
			source = enumerable;
		}
		int activeKeepCount = Math.Min(creatures.Length, 52);
		foreach (ComponentCreature creature2 in source.Skip(activeKeepCount))
		{
			m_runawayCreatureCleanup.Enqueue(creature2.Entity);
		}
		int recordKeepCount = Math.Max(0, 52 - activeKeepCount);
		IEnumerable<SpawnEntityData> enumerable3;
		if (playerPositions.Length == 0)
		{
			IEnumerable<SpawnEntityData> enumerable2 = spawnRecords;
			enumerable3 = enumerable2;
		}
		else
		{
			IEnumerable<SpawnEntityData> enumerable2 = spawnRecords.OrderBy((SpawnEntityData record) => playerPositions.Min((Vector3 position) => Vector3.DistanceSquared(position, record.Position)));
			enumerable3 = enumerable2;
		}
		IEnumerable<SpawnEntityData> orderedRecords = enumerable3;
		HashSet<SpawnEntityData> keptRecords = new HashSet<SpawnEntityData>(orderedRecords.Take(recordKeepCount));
		int removedRecords = 0;
		if (spawnChunks != null)
		{
			foreach (SpawnChunk chunk2 in spawnChunks.Values)
			{
				removedRecords += chunk2.SpawnsData.RemoveAll((SpawnEntityData record) => record != null && !record.ConstantSpawn && !keptRecords.Contains(record));
			}
		}
		Log.Warning($"[ScMP] Blocked runaway creature generation: Entities={m_runawayCreatureCleanup.Count}, SpawnRecords={removedRecords}.");
	}

	internal void SanitizeRunawayCreatureState(Project project)
	{
		if (project != null && m_runawayCreatureCleanup.Count <= 0 && !(Time.RealTime < m_nextRunawayCreatureCheckTime))
		{
			m_nextRunawayCreatureCheckTime = Time.RealTime + 2.0;
			QueueRunawayCreatureCleanup(project);
		}
	}

	private void ProcessRunawayCreatureCleanup(Project project)
	{
		if (project != m_runawayCreatureCleanupProject || m_runawayCreatureCleanup.Count == 0)
		{
			return;
		}
		int removed = 0;
		while (removed < 256 && m_runawayCreatureCleanup.Count > 0)
		{
			Entity entity = m_runawayCreatureCleanup.Dequeue();
			if (entity != null && entity.IsAddedToProject)
			{
				project.RemoveEntity(entity, disposeEntity: true);
				removed++;
			}
		}
		if (m_runawayCreatureCleanup.Count == 0)
		{
			Log.Information("[ScMP] Runaway creature cleanup completed.");
		}
	}

    }
}

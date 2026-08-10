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
        public void PublishTerrainChanges(Dictionary<Point3, bool> modifiedCells,
            bool immediate = false)
        {
            if (client?.IsConnected != true || modifiedCells == null || modifiedCells.Count == 0)
                return;
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            // Client terrain is prediction. The host executes the same remote input through the
            // original ComponentMiner and is the only source of authoritative terrain changes.
            if (!IsHost)
            {
                SubmitClientTerrainPredictions(modifiedCells);
                return;
            }
            // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
            // Environmental changes arrive from several simulation frames. Coalesce them in the
            // network tick window and keep the final value for each coordinate; direct player
            // actions pass immediate=true and bypass this small window.
            lock (m_terrainJournalLock)
            {
                foreach (KeyValuePair<Point3, bool> item in modifiedCells)
                    m_pendingHostTerrainBroadcastCells[item.Key] = item.Value;
            }
            if (!immediate)
                return;
            FlushPendingTerrainBroadcasts();
        }

        // Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.SetUpdateLocation
        // Maintain a chunk subscription table only when a player's center/radius changes. The
        // reverse map lets a terrain batch be projected once per interested client without
        // repeatedly scanning every player for every modified cell.
        private void UpdateHostTerrainInterestTable(Project project)
        {
            if (!IsHost || project == null) return;
            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(false);
            int defaultRadius = GetTerrainInterestRadius(sky?.VisibilityRange ?? 64f);
            var activeClients = new HashSet<int>();
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
            {
                int clientId = item.Key;
                PlayerData player = item.Value;
                if (clientId <= 0 || player?.ComponentPlayer?.ComponentBody == null)
                    continue;
                activeClients.Add(clientId);
                Point2 center = Terrain.ToChunk(player.ComponentPlayer.ComponentBody.Position.XZ);
                int radius = m_hostTerrainReportedInterestRadii.TryGetValue(clientId,
                    out int reportedRadius)
                    ? Math.Max(1, Math.Min(MaximumTerrainInterestRadius, reportedRadius))
                    : defaultRadius;
                if (m_hostTerrainInterestChunks.TryGetValue(clientId,
                        out HashSet<Point2> previous) &&
                    m_hostTerrainInterestCenters.TryGetValue(clientId, out Point2 oldCenter) &&
                    m_hostTerrainInterestRadii.TryGetValue(clientId, out int oldRadius) &&
                    oldCenter == center && oldRadius == radius)
                    continue;

                if (previous != null)
                {
                    foreach (Point2 chunk in previous)
                    {
                        if (m_hostTerrainChunkSubscribers.TryGetValue(chunk,
                                out HashSet<int> subscribers))
                        {
                            subscribers.Remove(clientId);
                            if (subscribers.Count == 0)
                                m_hostTerrainChunkSubscribers.Remove(chunk);
                        }
                    }
                }

                var chunks = new HashSet<Point2>();
                for (int x = center.X - radius; x <= center.X + radius; x++)
                {
                    for (int z = center.Y - radius; z <= center.Y + radius; z++)
                    {
                        var chunk = new Point2(x, z);
                        chunks.Add(chunk);
                        if (!m_hostTerrainChunkSubscribers.TryGetValue(chunk,
                                out HashSet<int> subscribers))
                        {
                            subscribers = new HashSet<int>();
                            m_hostTerrainChunkSubscribers.Add(chunk, subscribers);
                        }
                        subscribers.Add(clientId);
                    }
                }
                m_hostTerrainInterestChunks[clientId] = chunks;
                m_hostTerrainInterestCenters[clientId] = center;
                m_hostTerrainInterestRadii[clientId] = radius;
            }

            foreach (int clientId in m_hostTerrainInterestChunks.Keys.ToArray())
            {
                if (activeClients.Contains(clientId)) continue;
                foreach (Point2 chunk in m_hostTerrainInterestChunks[clientId])
                {
                    if (m_hostTerrainChunkSubscribers.TryGetValue(chunk,
                            out HashSet<int> subscribers))
                    {
                        subscribers.Remove(clientId);
                        if (subscribers.Count == 0)
                            m_hostTerrainChunkSubscribers.Remove(chunk);
                    }
                }
                m_hostTerrainInterestChunks.Remove(clientId);
                m_hostTerrainInterestCenters.Remove(clientId);
                m_hostTerrainInterestRadii.Remove(clientId);
                m_hostTerrainReportedInterestRadii.Remove(clientId);
            }
        }

        // Source: ScMultiplayerUpdateLoop.TriggerNetworkTick
        // Submit one bounded logical terrain batch per network window. The transport retains its
        // reliable sequence and performs normal UDP fragmentation when the serialized message is
        // larger than one datagram.
        internal void FlushPendingTerrainBroadcasts()
        {
            if (client?.IsConnected != true || !IsHost)
                return;
            EnsureHostTerrainSyncStateLoaded();
            UpdateHostTerrainInterestTable(GameManager.Project);
            Dictionary<Point3, bool> pending;
            lock (m_terrainJournalLock)
            {
                if (m_pendingHostTerrainBroadcastCells.Count == 0)
                    return;
                pending = new Dictionary<Point3, bool>(
                    m_pendingHostTerrainBroadcastCells);
                m_pendingHostTerrainBroadcastCells.Clear();
            }
            KeyValuePair<Point3, bool>[] items = pending.ToArray();
            for (int offset = 0; offset < items.Length; offset += TerrainReliableBatchSize)
            {
                var batch = new Dictionary<Point3, bool>();
                int count = Math.Min(TerrainReliableBatchSize, items.Length - offset);
                for (int i = 0; i < count; i++)
                    batch[items[offset + i].Key] = items[offset + i].Value;
                var message = new GameModifiedCellsMessage(batch, client.Step);
                lock (m_terrainJournalLock)
                    message.Sequence = ++m_hostTerrainSequence;
                RecordHostTerrainChanges(message, client.Step);
                RecordHostTerrainJournal(message);
                AcknowledgeHostTerrainPlaceFallbacks(message);

                // Source: ScMultiplayer.cs:NetworkMessageHandler.HandleModifiedCellsMessage
                // Project this sequence to each client. Empty projections are intentional: they
                // advance the client's global sequence barrier without sending out-of-interest
                // cells, so a later chunk checkpoint can fill the area when it becomes loaded.
                foreach (ServerClient remote in GetConnectedRemoteClients())
                {
                    if (remote == null || remote.ClientID <= 0) continue;
                    var projected = new Dictionary<Point3, bool>();
                    foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
                    {
                        Point2 chunk = Terrain.ToChunk(item.Key.X, item.Key.Z);
                        if (m_hostTerrainChunkSubscribers.TryGetValue(chunk,
                                out HashSet<int> subscribers) && subscribers.Contains(remote.ClientID))
                            projected[item.Key] = item.Value;
                    }
                    var projectedMessage = new GameModifiedCellsMessage(projected, client.Step)
                    {
                        Sequence = message.Sequence,
                        TargetClientId = remote.ClientID
                    };
                    NetworkMessageSender.SendScheduledMessage(remote.ClientID, projectedMessage,
                        sequenced: false, latest: false, batchable: false);
                }
            }
        }

        // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:
        // SubsystemFluidBlockBehavior.Update
        // Fluid processing can call ProcessModifiedCells before UpdateOrder.Terrain observes a
        // host placement. Remove candidates that entered the normal publication path first.
        private void AcknowledgeHostTerrainPlaceFallbacks(GameModifiedCellsMessage message)
        {
            if (message?.ModifiedCells == null || m_hostTerrainPlaceFallbacks.Count == 0)
                return;
            foreach (Point3 cell in message.ModifiedCells.Keys)
                m_hostTerrainPlaceFallbacks.Remove(cell);
        }

        // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:
        // SubsystemFluidBlockBehavior.Update
        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        // Fluid processing can consume m_modifiedCells between a native host placement and the
        // regular terrain publisher. If that happens, republish only the final changed cell through
        // the existing global sequence, journal and reliable terrain path.
        private void UpdateHostTerrainPlaceFallbacks(Project project)
        {
            if (!IsHost || project == null || m_hostTerrainPlaceFallbacks.Count == 0)
                return;
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null)
                return;

            double now = Time.RealTime;
            foreach (KeyValuePair<Point3, PendingHostTerrainPlaceFallback> item in
                m_hostTerrainPlaceFallbacks.ToArray())
            {
                PendingHostTerrainPlaceFallback pending = item.Value;
                if (now > pending.ExpiresAt)
                {
                    m_hostTerrainPlaceFallbacks.Remove(item.Key);
                    continue;
                }
                if (Time.FrameIndex < pending.CheckAfterFrameIndex)
                    continue;

                m_hostTerrainPlaceFallbacks.Remove(item.Key);
                if (!terrain.Terrain.IsCellValid(item.Key.X, item.Key.Y, item.Key.Z))
                    continue;
                int finalValue = Terrain.ReplaceLight(terrain.Terrain.GetCellValue(
                    item.Key.X, item.Key.Y, item.Key.Z), 0);
                if (finalValue == pending.ExpectedValue)
                    continue;

                PublishTerrainChanges(new Dictionary<Point3, bool>
                {
                    [item.Key] = true
                });
            }
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        private void RecordHostTerrainJournal(GameModifiedCellsMessage message)
        {
            if (!IsHost || message?.Sequence <= 0) return;
            byte[] payload = Message.WriteWithSender(message, client.Address);
            lock (m_terrainJournalLock)
            {
                m_hostTerrainJournal.Enqueue(new TerrainJournalEntry
                {
                    Sequence = message.Sequence,
                    ServerStep = message.Tick,
                    CreatedTime = Time.RealTime,
                    Payload = payload
                });
                TrimHostTerrainJournalLocked(Time.RealTime);
            }
        }

        private void TrimHostTerrainJournalLocked(double now)
        {
            while (m_hostTerrainJournal.Count > 0 &&
                now - m_hostTerrainJournal.Peek().CreatedTime > TerrainRecoveryRetention)
                m_hostTerrainJournal.Dequeue();
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.Client_GameStep
        private void HandleTerrainRecoveryMessage(TerrainRecoveryMessage message,
            int sourceClientId)
        {
            if (message == null) return;
            if (IsHost)
            {
                if (sourceClientId <= 0 || !m_networkPlayerData.ContainsKey(sourceClientId))
                    return;
                if (message.Stage == TerrainRecoveryStage.Request)
                {
                    SendHostTerrainRecoveryRound(sourceClientId,
                        message.LastAppliedSequence, message.BufferedRanges);
                }
                else if (message.Stage == TerrainRecoveryStage.Acknowledge &&
                    m_hostTerrainRecoveryTargets.TryGetValue(sourceClientId,
                        out long target) && message.LastAppliedSequence >= target)
                {
                    long head;
                    lock (m_terrainJournalLock) head = m_hostTerrainSequence;
                    if (message.LastAppliedSequence < head)
                    {
                        SendHostTerrainRecoveryRound(sourceClientId,
                            message.LastAppliedSequence, message.BufferedRanges);
                    }
					else
					{
						m_hostTerrainRecoveryTargets.Remove(sourceClientId);
						m_forceHostInventorySync = true;
						m_pendingHostPickableSnapshots.Add(sourceClientId);
						m_fullWorldObjectsSyncTime = WorldObjectFullSyncInterval;
                        m_fullAnimalSyncTime = 5f;
                        SendTerrainRecoveryMessage(sourceClientId,
                            new TerrainRecoveryMessage
                            {
                                Stage = TerrainRecoveryStage.Ready,
                                LastAppliedSequence = message.LastAppliedSequence,
                                HeadSequence = head,
                                ServerStep = client.Step
                            });
                    }
                }
                return;
            }

            if (sourceClientId != 0) return;
            switch (message.Stage)
            {
                case TerrainRecoveryStage.ReplayBatch:
                    EnqueueClientTerrainRecoveryReplay(message);
                    break;
                case TerrainRecoveryStage.Barrier:
                    m_clientTerrainRecoveryActive = true;
                    m_clientTerrainRecoveryRequestInFlight = false;
                    m_clientTerrainRecoveryTarget = message.HeadSequence;
                    m_clientTerrainRecoveryAcknowledged = -1;
                    break;
                case TerrainRecoveryStage.Ready:
                    m_clientTerrainRecoveryActive = true;
                    m_clientTerrainRecoveryRequestInFlight = false;
                    m_clientTerrainRecoveryReady = message.HeadSequence;
                    break;
                case TerrainRecoveryStage.ResyncRequired:
                    RestartClientWorldDownload();
                    break;
            }
        }

        // Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.ChunkInitialized
        // The client asks only after a Chunk exists. The host replies from the coalesced
        // post-snapshot checkpoint, never by rebroadcasting every terrain change.
        private void HandleTerrainChunkSyncMessage(TerrainChunkSyncMessage message,
            int sourceClientId)
        {
            if (message == null) return;
            Point2 coordinates = new Point2(message.ChunkX, message.ChunkZ);
            if (IsHost)
            {
                if (sourceClientId <= 0 || !m_networkPlayerData.ContainsKey(sourceClientId))
                    return;
                if (message.Stage == TerrainChunkSyncStage.Interest)
                {
                    if (message.InterestRadius < 1 ||
                        message.InterestRadius > MaximumTerrainInterestRadius)
                        return;
                    m_hostTerrainReportedInterestRadii[sourceClientId] =
                        message.InterestRadius;
                    UpdateHostTerrainInterestTable(GameManager.Project);
                    return;
                }
                if (message.Stage != TerrainChunkSyncStage.Request)
                    return;
                SendHostTerrainChunkSync(sourceClientId, coordinates,
                    Math.Max(message.KnownRevision, 0L));
                return;
            }

            if (sourceClientId != 0) return;
            if (message.Stage == TerrainChunkSyncStage.Data)
            {
                // Source: ScMultiplayer.UpdateClientTerrainChunkSync
                // Reliable checkpoint data is still making progress. Refresh the request timer so
                // a large response cannot be duplicated before its trailing Complete arrives.
                if (m_clientTerrainChunkSyncPending.ContainsKey(coordinates))
                    m_clientTerrainChunkSyncPending[coordinates] = Time.RealTime;
                if (message.Revision <= 0 || message.Cells == null ||
                    message.CellValues == null)
                    return;
                var cells = new Dictionary<Point3, bool>();
                var values = new List<int>();
                int count = Math.Min(message.Cells.Count, message.CellValues.Count);
                for (int i = 0; i < count; i++)
                {
                    Point3 cell = message.Cells[i];
                    if (Terrain.ToChunk(cell.X, cell.Z) != coordinates)
                        continue;
                    cells[cell] = true;
                    values.Add(message.CellValues[i]);
                }
                if (cells.Count > 0)
                {
                    RegisterClientTerrainChunkCheckpointBatch(coordinates, message.Revision);
                    // Source: ScMultiplayer.Func.Subsystem.SuSubsystemTerrain:
                    // SuSubsystemTerrain.EnqueuePriorityNetworkBatch
                    // Chunk checkpoint data carries its chunk revision for per-cell stale-write
                    // rejection, but must not advance the global terrain sequence.
                    var checkpointMessage = new GameModifiedCellsMessage(cells, values,
                        message.ServerTick, false, client.ClientID, message.Revision)
                    {
                        ChunkCheckpointCoordinates = coordinates,
                        ChunkCheckpointRevision = message.Revision
                    };
                    SuSubsystemTerrain.EnqueuePriorityNetworkBatch(checkpointMessage);
                }
                return;
            }

            if (message.Stage != TerrainChunkSyncStage.Complete) return;
            SubsystemTerrain terrain = GameManager.Project?
                .FindSubsystem<SubsystemTerrain>(false);
            TerrainChunk chunk = terrain?.Terrain.GetChunkAtCoords(
                coordinates.X, coordinates.Y);
            if (chunk == null || chunk.State < TerrainChunkState.Valid)
            {
                // Source: ScMultiplayer.OnClientTerrainChunkInitialized
                // A chunk can unload while its targeted reply is in flight. Keep the imported
                // world baseline and request the newer revision after this chunk is valid again.
                m_clientTerrainChunkSyncPending.Remove(coordinates);
                if (message.Revision > GetClientTerrainChunkRevision(coordinates))
                {
                    m_clientTerrainChunkVerifications[coordinates] =
                        new PendingTerrainChunkVerification
                        {
                            RequiredRevision = message.Revision,
                            DueTime = Time.RealTime
                        };
                }
                return;
            }
            if (m_clientTerrainChunkFailedRevisions.TryGetValue(coordinates,
                    out long failedRevision) && failedRevision >= message.Revision)
            {
                m_clientTerrainChunkSyncPending.Remove(coordinates);
                QueueClientTerrainChunkSync(coordinates);
                return;
            }
            if (!m_clientTerrainChunkCheckpoints.TryGetValue(coordinates,
                    out PendingTerrainChunkCheckpoint checkpoint) ||
                checkpoint.Revision != message.Revision)
            {
                checkpoint = new PendingTerrainChunkCheckpoint
                {
                    Revision = Math.Max(message.Revision, 0L)
                };
                m_clientTerrainChunkCheckpoints[coordinates] = checkpoint;
            }
            checkpoint.CompleteReceived = true;
            TryFinalizeClientTerrainChunkCheckpoint(coordinates, checkpoint);
        }

        // Source: ScMultiplayer.Func.Subsystem.SuSubsystemTerrain:
        // SuSubsystemTerrain.ApplyPriorityNetworkBatches
        private void RegisterClientTerrainChunkCheckpointBatch(Point2 coordinates,
            long revision)
        {
            if (revision <= 0) return;
            if (!m_clientTerrainChunkCheckpoints.TryGetValue(coordinates,
                    out PendingTerrainChunkCheckpoint checkpoint) || checkpoint.Revision != revision)
            {
                checkpoint = new PendingTerrainChunkCheckpoint { Revision = revision };
                m_clientTerrainChunkCheckpoints[coordinates] = checkpoint;
            }
            m_clientTerrainChunkFailedRevisions.Remove(coordinates);
            checkpoint.ReceivedBatches++;
        }

        internal void OnClientTerrainChunkCheckpointBatchApplied(Point2 coordinates,
            long revision, bool applied)
        {
            if (IsHost || revision <= 0) return;
            if (!m_clientTerrainChunkCheckpoints.TryGetValue(coordinates,
                    out PendingTerrainChunkCheckpoint checkpoint) || checkpoint.Revision != revision)
                return;
            if (!applied)
            {
                m_clientTerrainChunkCheckpoints.Remove(coordinates);
                m_clientTerrainChunkSyncPending.Remove(coordinates);
                m_clientTerrainChunkFailedRevisions[coordinates] = revision;
                if (!m_clientTerrainChunkVerifications.TryGetValue(coordinates,
                        out PendingTerrainChunkVerification verification))
                {
                    verification = new PendingTerrainChunkVerification();
                    m_clientTerrainChunkVerifications.Add(coordinates, verification);
                }
                verification.RequiredRevision = Math.Max(verification.RequiredRevision, revision);
                verification.DueTime = 0.0;
                QueueClientTerrainChunkSync(coordinates);
                return;
            }
            checkpoint.AppliedBatches++;
            TryFinalizeClientTerrainChunkCheckpoint(coordinates, checkpoint);
        }

        private void TryFinalizeClientTerrainChunkCheckpoint(Point2 coordinates,
            PendingTerrainChunkCheckpoint checkpoint)
        {
            if (checkpoint == null || !checkpoint.CompleteReceived ||
                checkpoint.AppliedBatches < checkpoint.ReceivedBatches)
                return;
            if (m_clientTerrainChunkVerifications.TryGetValue(coordinates,
                    out PendingTerrainChunkVerification verification) &&
                checkpoint.Revision < verification.RequiredRevision)
            {
                // Source: ScMultiplayer.OnClientTerrainCellApplicationFailed
                // A delayed reply can complete after newer live terrain was received. Do not
                // advance the per-chunk revision from that old reply, or the newer final cells
                // would be permanently omitted from subsequent authoritative checkpoints.
                m_clientTerrainChunkCheckpoints.Remove(coordinates);
                m_clientTerrainChunkSyncPending.Remove(coordinates);
                QueueClientTerrainChunkSync(coordinates);
                return;
            }
            long knownRevision = GetClientTerrainChunkRevision(coordinates);
            m_clientTerrainChunkRevisions[coordinates] = Math.Max(knownRevision, checkpoint.Revision);
            m_clientTerrainChunkCheckpoints.Remove(coordinates);
            m_clientTerrainChunkSyncPending.Remove(coordinates);
            if (m_clientTerrainChunkVerifications.TryGetValue(coordinates,
                    out verification) &&
                m_clientTerrainChunkRevisions[coordinates] >= verification.RequiredRevision)
                m_clientTerrainChunkVerifications.Remove(coordinates);
        }

        private void SendHostTerrainChunkSync(int targetClientId, Point2 coordinates,
            long knownRevision)
        {
            List<KeyValuePair<Point3, TerrainCellState>> snapshot =
                new List<KeyValuePair<Point3, TerrainCellState>>();
            long revision;
            int serverTick;
            lock (m_terrainJournalLock)
            {
                MergePendingTerrainChangesLocked();
                m_hostTerrainChunkRevisions.TryGetValue(coordinates, out revision);
                if (revision > knownRevision &&
                    m_terrainCheckpointByChunk.TryGetValue(coordinates,
                        out Dictionary<Point3, TerrainCellState> cells))
                {
                    snapshot = cells.Where(item => item.Value.Sequence > knownRevision)
                        .OrderBy(item => item.Key.X)
                        .ThenBy(item => item.Key.Y).ThenBy(item => item.Key.Z).ToList();
                }
                serverTick = client.Step;
            }

            for (int offset = 0; offset < snapshot.Count;
                offset += TerrainChunkSyncBatchSize)
            {
                int count = Math.Min(TerrainChunkSyncBatchSize, snapshot.Count - offset);
                var message = new TerrainChunkSyncMessage
                {
                    Stage = TerrainChunkSyncStage.Data,
                    ChunkX = coordinates.X,
                    ChunkZ = coordinates.Y,
                    Revision = revision,
                    ServerTick = serverTick
                };
                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<Point3, TerrainCellState> item = snapshot[offset + i];
                    message.Cells.Add(item.Key);
                    message.CellValues.Add(item.Value.CellValue);
                }
                NetworkMessageSender.SendRawMessage(targetClientId, message, sequenced: true);
            }

            NetworkMessageSender.SendRawMessage(targetClientId, new TerrainChunkSyncMessage
                {
                    Stage = TerrainChunkSyncStage.Complete,
                    ChunkX = coordinates.X,
                    ChunkZ = coordinates.Y,
                    KnownRevision = knownRevision,
                    Revision = revision,
                    ServerTick = serverTick
                }, sequenced: true);
        }

        private void SendHostTerrainRecoveryRound(int targetClientId, long lastApplied,
            List<TerrainSequenceRange> bufferedRanges)
        {
            List<TerrainJournalEntry> replay;
            long head;
            long oldest;
            lock (m_terrainJournalLock)
            {
                TrimHostTerrainJournalLocked(Time.RealTime);
                head = m_hostTerrainSequence;
                if (lastApplied < 0 || lastApplied > head)
                {
                    SendTerrainRecoveryResyncRequired(targetClientId, head);
                    return;
                }
                TerrainJournalEntry[] journal = m_hostTerrainJournal.ToArray();
                oldest = journal.Length > 0 ? journal[0].Sequence : head + 1;
                long unavailableEnd = Math.Min(head, oldest - 1);
                if (lastApplied < unavailableEnd && !RangesCoverInterval(
                    bufferedRanges, lastApplied + 1, unavailableEnd))
                {
                    SendTerrainRecoveryResyncRequired(targetClientId, head);
                    return;
                }
                replay = journal.Where(entry => entry.Sequence > lastApplied &&
                    entry.Sequence <= head &&
                    !SequenceIsBuffered(entry.Sequence, bufferedRanges)).ToList();
                m_hostTerrainRecoveryTargets[targetClientId] = head;
            }

            // Recovery only needs to restore the final terrain state. Preserve every sequence as
            // a barrier, but collapse repeated writes to the same cell before serializing them.
            // Source: Survivalcraft/Game/SubsystemTerrain.cs:ChangeCell
            var finalCells = new Dictionary<Point3, TerrainCellState>();
            foreach (TerrainJournalEntry entry in replay)
            {
                try
                {
                    if (!(Message.Read(entry.Payload) is GameModifiedCellsMessage terrain) ||
                        terrain.ModifiedCells == null || terrain.CellValues == null)
                        continue;
                    int count = Math.Min(terrain.ModifiedCells.Count, terrain.CellValues.Count);
                    int index = 0;
                    foreach (KeyValuePair<Point3, bool> item in terrain.ModifiedCells)
                    {
                        if (index >= count) break;
                        finalCells[item.Key] = new TerrainCellState
                        {
                            IsModified = item.Value,
                            CellValue = terrain.CellValues[index],
                            Tick = terrain.Tick,
                            Sequence = terrain.Sequence
                        };
                        index++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] Invalid host terrain recovery payload: {ex.Message}");
                }
            }
            var finalCellsBySequence = finalCells.GroupBy(item => item.Value.Sequence)
                .ToDictionary(group => group.Key, group => group.ToDictionary(
                    item => item.Key, item => item.Value));
            var decodedReplay = new Dictionary<long, GameModifiedCellsMessage>();
            foreach (TerrainJournalEntry entry in replay)
            {
                try
                {
                    if (Message.Read(entry.Payload) is GameModifiedCellsMessage terrain &&
                        terrain.Sequence > 0)
                        decodedReplay[terrain.Sequence] = terrain;
                }
                catch
                {
                    // The first pass already logged malformed payloads. Keep this pass silent.
                }
            }
            var payloads = new List<byte[]>();
            int payloadBytes = 0;
            // Keep every sequence as an ordered barrier. Superseded sequences carry zero cells,
            // so they advance reliability without replaying obsolete ChangeCell states.
            foreach (TerrainJournalEntry entry in replay)
            {
                if (!decodedReplay.TryGetValue(entry.Sequence, out GameModifiedCellsMessage source))
                {
                    if (payloads.Count > 0 && (payloads.Count >= 64 ||
                        payloadBytes + entry.Payload.Length > MaximumTerrainRecoveryBatchBytes))
                    {
                        SendTerrainRecoveryReplayBatch(targetClientId, head, payloads);
                        payloads = new List<byte[]>();
                        payloadBytes = 0;
                    }
                    payloads.Add(entry.Payload);
                    payloadBytes += entry.Payload.Length;
                    continue;
                }
                var cells = new Dictionary<Point3, bool>();
                var values = new List<int>();
                if (finalCellsBySequence.TryGetValue(source.Sequence,
                    out Dictionary<Point3, TerrainCellState> finalForSequence))
                {
                    foreach (KeyValuePair<Point3, TerrainCellState> item in finalForSequence
                        .OrderBy(item => item.Key.X).ThenBy(item => item.Key.Y)
                        .ThenBy(item => item.Key.Z))
                    {
                        cells[item.Key] = item.Value.IsModified;
                        values.Add(item.Value.CellValue);
                    }
                }
                byte[] payload = Message.WriteWithSender(new GameModifiedCellsMessage(cells,
                    values, source.Tick, source.IsCatchUp, targetClientId, source.Sequence)
                {
                    HeadSequence = source.HeadSequence
                }, client.Address);
                if (payloads.Count > 0 && (payloads.Count >= 64 ||
                    payloadBytes + payload.Length > MaximumTerrainRecoveryBatchBytes))
                {
                    SendTerrainRecoveryReplayBatch(targetClientId, head, payloads);
                    payloads = new List<byte[]>();
                    payloadBytes = 0;
                }
                payloads.Add(payload);
                payloadBytes += payload.Length;
            }
            if (payloads.Count > 0)
                SendTerrainRecoveryReplayBatch(targetClientId, head, payloads);
            SendTerrainRecoveryMessage(targetClientId, new TerrainRecoveryMessage
            {
                Stage = TerrainRecoveryStage.Barrier,
                LastAppliedSequence = lastApplied,
                HeadSequence = head,
                ServerStep = client.Step
            });
            Log.Information($"[ScMP] Terrain recovery round: ClientID={targetClientId}, " +
                $"Applied={lastApplied}, Oldest={oldest}, Head={head}, Replay={replay.Count}, " +
                $"Final={finalCells.Count}");
        }

        // Source: ScMultiplayer.SendHostTerrainRecoveryRound
        private static void EnqueueClientTerrainRecoveryReplay(TerrainRecoveryMessage message)
        {
            if (message?.Payloads == null) return;
            foreach (byte[] payload in message.Payloads)
            {
                try
                {
                    if (Message.Read(payload) is GameModifiedCellsMessage terrain &&
                        terrain.Sequence > 0 && terrain.Sequence <= message.HeadSequence)
                        SuSubsystemTerrain.EnqueueNetworkBatch(terrain);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] Invalid terrain replay payload: {ex.Message}");
                }
            }
        }

        private static void SendTerrainRecoveryReplayBatch(int targetClientId,
            long head, List<byte[]> payloads)
        {
            SendTerrainRecoveryMessage(targetClientId, new TerrainRecoveryMessage
            {
                Stage = TerrainRecoveryStage.ReplayBatch,
                HeadSequence = head,
                ServerStep = client.Step,
                Payloads = payloads
            });
        }

        private void SendTerrainRecoveryResyncRequired(int targetClientId, long head)
        {
            m_hostTerrainRecoveryTargets.Remove(targetClientId);
            SendTerrainRecoveryMessage(targetClientId, new TerrainRecoveryMessage
            {
                Stage = TerrainRecoveryStage.ResyncRequired,
                HeadSequence = head,
                ServerStep = client.Step
            });
            Log.Warning($"[ScMP] Terrain recovery history expired for ClientID={targetClientId}");
        }

        private static bool SequenceIsBuffered(long sequence,
            List<TerrainSequenceRange> ranges) =>
            ranges != null && ranges.Any(range => range != null &&
                sequence >= range.Start && sequence <= range.End);

        private static bool RangesCoverInterval(List<TerrainSequenceRange> ranges,
            long start, long end)
        {
            if (start > end) return true;
            long cursor = start;
            foreach (TerrainSequenceRange range in (ranges ?? new List<TerrainSequenceRange>())
                .Where(item => item != null).OrderBy(item => item.Start))
            {
                if (range.End < cursor) continue;
                if (range.Start > cursor) return false;
                cursor = Math.Max(cursor, range.End + 1);
                if (cursor > end) return true;
            }
            return false;
        }

        private void RestartClientWorldDownload()
        {
            Log.Warning("[ScMP] Terrain recovery requires a fresh host world snapshot");
            if (m_activeJoinRequest?.WorldInfo == null)
            {
                HandleHostDisconnected();
                return;
            }
            ShowJoinRoomBusyDialog();
            if (m_joinRoomBusyDialog != null)
                m_joinRoomBusyDialog.SmallMessage =
                    "Terrain history expired.\r\nDownloading the current host world...";
            // Source: Survivalcraft/Game/GameLoadingScreen.cs:GameLoadingScreen.Enter
            // Loading the refreshed snapshot disposes the currently running client Project. Keep
            // that known replacement separate from an intentional game-menu leave.
            m_clientWorldRefreshProject = GameManager.Project;
            PrepareClientForRemoteJoin();
            m_pendingJoinRequest = m_activeJoinRequest;
            m_isLoadingDownloadedWorld = true;
            SubmitPendingJoin(m_activeJoinPlayerName, m_activeJoinPlayerClass,
                m_activeJoinSkinName, m_activeJoinHasPlayerProfile);
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.m_modifiedCells
        private void SubmitClientTerrainPredictions(Dictionary<Point3, bool> modifiedCells)
        {
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null) return;
            var repairCells = new Dictionary<Point3, bool>();
            foreach (Point3 cell in modifiedCells.Keys)
            {
                if (m_pendingTerrainPlacePredictionCells.TryGetValue(cell,
                    out int placeRequestId) &&
                    m_pendingTerrainPlacePredictions.TryGetValue(placeRequestId,
                        out PendingTerrainPlacePrediction placePrediction))
                {
                    placePrediction.LocalPredictedValue = Terrain.ReplaceLight(
                        terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z), 0);
                    placePrediction.HasLocalPrediction = true;
                    // Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs:
                    // SubsystemCollapsingBlockBehavior.TryCollapseColumn
                    // A successful sand/gravel placement immediately restores its source cell to
                    // air while the moving-block animation owns the falling block.
                    if (placePrediction.IsCollapsingBlock &&
                        placePrediction.LocalPredictedValue ==
                            placePrediction.Request.ExpectedValue)
                        m_localCollapsingPlacePredictions[cell] = Time.RealTime +
                            LocalCollapsingPredictionLifetime;
                    continue;
                }
                bool pending = m_pendingTerrainPredictionCells.ContainsKey(cell);
                bool hasIntent = m_localTerrainDigIntents.TryGetValue(cell,
                    out LocalTerrainDigIntent intent);
                double intentAge = hasIntent ? Time.RealTime - intent.LastSeenTime : -1.0;
                if (pending)
                    continue;
                if (m_localTerrainUsePredictions.TryGetValue(cell,
                    out LocalTerrainUsePrediction terrainUsePrediction))
                {
                    int currentValue = Terrain.ReplaceLight(terrain.Terrain.GetCellValue(
                        cell.X, cell.Y, cell.Z), 0);
                    if (currentValue != terrainUsePrediction.ExpectedValue)
                    {
                        terrainUsePrediction.LastSeenTime = Time.RealTime;
                        continue;
                    }
                    m_localTerrainUsePredictions.Remove(cell);
                }
                int predictedValue = Terrain.ReplaceLight(
                    terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z), 0);
                // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:
                // SubsystemDeciduousLeavesBlockBehavior.UpdateTimeOfYear
                // Leaf season metadata is derived locally and host-authoritative. It is not a
                // player terrain prediction and must not trigger a repair round trip.
                if (!hasIntent && BlocksManager.Blocks[
                    Terrain.ExtractContents(predictedValue)] is DeciduousLeavesBlock)
                {
                    continue;
                }
				if (hasIntent && intentAge <= 2.0 && predictedValue != intent.ExpectedValue &&
					predictedValue == intent.PredictedValue)
					QueueTerrainDigRequest(cell, intent, predictedValue);
                else if (!hasIntent || intentAge > 2.0)
                    repairCells[cell] = modifiedCells[cell];
            }
            RequestAuthoritativeTerrainRepair(repairCells);
        }

        private void RequestAuthoritativeTerrainRepair(Dictionary<Point3, bool> cells)
        {
            if (cells == null || cells.Count == 0 || client?.IsConnected != true) return;
            KeyValuePair<Point3, bool>[] items = cells.ToArray();
            for (int offset = 0; offset < items.Length; offset += TerrainReliableBatchSize)
            {
                var batch = new Dictionary<Point3, bool>();
                int count = Math.Min(TerrainReliableBatchSize, items.Length - offset);
                for (int i = 0; i < count; i++)
                    batch[items[offset + i].Key] = items[offset + i].Value;
                var message = new GameModifiedCellsMessage(batch, client.Step);
                // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:
                // ServerGame.SendDirectInput
                // Repairs carry cell values and ticks, so they do not need ReliableSequenced HOL.
                NetworkMessageSender.SendRawMessage(0, message);
            }
        }

        private void QueueTerrainDigRequest(Point3 cell, LocalTerrainDigIntent intent,
            int predictedValue)
        {
            if (intent == null || m_pendingTerrainPredictionCells.ContainsKey(cell)) return;
            m_nextTerrainDigRequestId = m_nextTerrainDigRequestId == int.MaxValue
                ? 1
                : m_nextTerrainDigRequestId + 1;
            var request = new TerrainDigRequestMessage(m_nextTerrainDigRequestId, cell,
                intent.ExpectedValue, predictedValue, intent.DigRay,
                intent.HitFace, intent.StartClientTick, client.Step, intent.ActiveSlotIndex,
                intent.ToolValue)
            {
                ToolCount = intent.ToolCount,
                BodyPosition = intent.BodyPosition
            };
            if (Terrain.ExtractContents(intent.ExpectedValue) == 62)
                SuSubsystemTerrain.BeginIceTrace(cell);
            m_pendingTerrainPredictions[request.RequestId] = new PendingTerrainPrediction
            {
                Request = request,
                LastSendTime = Time.RealTime,
                SendCount = 1
            };
            m_pendingTerrainPredictionCells[cell] = request.RequestId;
            m_localTerrainDigIntents.Remove(cell);
            NetworkMessageSender.SendTerrainDigRequest(request);
        }

        private void UpdatePendingTerrainPredictions()
        {
            if (client?.IsConnected != true) return;
            double now = Time.RealTime;
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain != null)
            {
                var expiredTerrainUseRepairs = new Dictionary<Point3, bool>();
                foreach (KeyValuePair<Point3, LocalTerrainUsePrediction> item in
                    m_localTerrainUsePredictions.ToArray())
                {
                    if (now - item.Value.LastSeenTime <= 2.0) continue;
                    int currentValue = Terrain.ReplaceLight(terrain.Terrain.GetCellValue(
                        item.Key.X, item.Key.Y, item.Key.Z), 0);
                    m_localTerrainUsePredictions.Remove(item.Key);
                    if (currentValue != item.Value.ExpectedValue)
                        expiredTerrainUseRepairs[item.Key] = true;
                }
                if (expiredTerrainUseRepairs.Count > 0)
                    RequestAuthoritativeTerrainRepair(expiredTerrainUseRepairs);
            }
            var expiredCollapsingRepairs = new Dictionary<Point3, bool>();
            foreach (KeyValuePair<Point3, double> item in
                m_localCollapsingPlacePredictions.ToArray())
            {
                if (now <= item.Value) continue;
                m_localCollapsingPlacePredictions.Remove(item.Key);
                expiredCollapsingRepairs[item.Key] = true;
                RemoveLocalCollapsingSets(item.Key, expiredCollapsingRepairs);
            }
            if (expiredCollapsingRepairs.Count > 0)
                RequestAuthoritativeTerrainRepair(expiredCollapsingRepairs);
            foreach (PendingTerrainPlacePrediction prediction in
                m_pendingTerrainPlacePredictions.Values.ToArray())
            {
                if (now - prediction.LastSendTime < 0.5) continue;
                prediction.LastSendTime = now;
                prediction.SendCount++;
                NetworkMessageSender.SendPlayerInteractRequest(prediction.Request);
            }
            if (terrain != null)
            {
                foreach (KeyValuePair<Point3, LocalTerrainDigIntent> item in
                    m_localTerrainDigIntents.ToArray())
                {
                    if (m_pendingTerrainPredictionCells.ContainsKey(item.Key)) continue;
                    int currentValue = Terrain.ReplaceLight(terrain.Terrain.GetCellValue(
                        item.Key.X, item.Key.Y, item.Key.Z), 0);
                    if (currentValue != item.Value.ExpectedValue)
                        QueueTerrainDigRequest(item.Key, item.Value, currentValue);
                }
            }
            foreach (Point3 cell in m_localTerrainDigIntents.Where(
                item => now - item.Value.LastSeenTime > 2.0).Select(item => item.Key).ToArray())
                m_localTerrainDigIntents.Remove(cell);
            if (m_pendingTerrainPredictions.Count == 0) return;
            foreach (PendingTerrainPrediction prediction in
                m_pendingTerrainPredictions.Values.ToArray())
            {
                if (prediction.Result != null)
                {
                    if (now >= prediction.ReconcileTime)
                    {
                        ApplyTerrainDigResult(prediction.Result);
                        RemovePendingTerrainPrediction(prediction.Request.RequestId);
                    }
                    continue;
                }
                double retryPeriod = prediction.SendCount < 8 ? 0.25 : 1.0;
                if (now - prediction.LastSendTime < retryPeriod) continue;
                prediction.LastSendTime = now;
                prediction.SendCount++;
                NetworkMessageSender.SendTerrainDigRequest(prediction.Request);
            }
        }

        private void HandleTerrainDigResult(TerrainDigResultMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null ||
                !m_pendingTerrainPredictions.TryGetValue(message.RequestId,
                    out PendingTerrainPrediction prediction) ||
                prediction.Request.Cell != message.Cell)
                return;
            if (message.Accepted || message.AuthoritativeValue == prediction.Request.PredictedValue)
            {
                ApplyTerrainDigResult(message);
                RemovePendingTerrainPrediction(message.RequestId);
            }
            else
            {
                // A direct request can overtake the matching inventory/input tick. Retry several
                // times before restoring so a valid local dig does not visibly pop back.
                if (prediction.SendCount < 4)
                {
                    prediction.LastSendTime = Time.RealTime;
                }
                else
                {
                    prediction.Result = message;
                    prediction.ReconcileTime = Time.RealTime + 0.25;
                }
            }
        }

        // Source: Survivalcraft/Game/ComponentDiggingCracks.cs:ComponentDiggingCracks.Draw
        private void HandleDigPresentationMessage(DigPresentationMessage message, int sourceClientId)
        {
            if (message == null) return;
            if (IsHost)
            {
                if (sourceClientId <= 0 || message.PlayerIndex != sourceClientId ||
                    !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) ||
                    playerData?.ComponentPlayer?.ComponentCreatureModel == null)
                    return;
                if (message.IsActive)
                {
                    SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
                    if (terrain == null || !terrain.Terrain.IsCellValid(message.X, message.Y, message.Z) ||
                        message.Face < 0 || message.Face > 5 ||
                        Vector3.DistanceSquared(playerData.ComponentPlayer.ComponentCreatureModel.EyePosition,
                            new Vector3(message.X + 0.5f, message.Y + 0.5f, message.Z + 0.5f)) >
                            2.25f * 2.25f)
                        return;
                    message.Progress = MathUtils.Saturate(message.Progress);
                }
                NetworkMessageSender.SendDigPresentation(-1, message, latest: !message.IsActive);
                return;
            }
            if (sourceClientId != 0 || message.PlayerIndex == client?.ClientID ||
                !m_networkPlayerData.ContainsKey(message.PlayerIndex))
                return;
            if (!m_remoteDigPresentations.TryGetValue(message.PlayerIndex,
                out RemoteDigPresentation state))
            {
                state = new RemoteDigPresentation();
                m_remoteDigPresentations[message.PlayerIndex] = state;
            }
            if (message.Sequence <= state.Sequence) return;
            state.Sequence = message.Sequence;
            state.LastUpdateTime = Time.RealTime;
            if (!message.IsActive)
            {
                m_remoteDigPresentations.Remove(message.PlayerIndex);
                return;
            }
            state.CellFace = new CellFace(message.X, message.Y, message.Z, message.Face);
            state.TargetProgress = MathUtils.Saturate(message.Progress);
            state.DisplayProgress = MathUtils.Min(state.DisplayProgress, state.TargetProgress);
        }

        private void ApplyTerrainDigResult(TerrainDigResultMessage message)
        {
            var cells = new Dictionary<Point3, bool> { [message.Cell] = true };
            var values = new List<int> { message.AuthoritativeValue };
            SuSubsystemTerrain.EnqueuePriorityNetworkBatch(new GameModifiedCellsMessage(
                cells, values, message.ServerTick, false, client.ClientID));
        }

        private void RemovePendingTerrainPrediction(int requestId)
        {
            if (!m_pendingTerrainPredictions.TryGetValue(requestId,
                out PendingTerrainPrediction prediction))
                return;
            m_pendingTerrainPredictions.Remove(requestId);
            m_pendingTerrainPredictionCells.Remove(prediction.Request.Cell);
        }

        public void HandleGameModifiedCellsMessage(GameModifiedCellsMessage msg, int sourceClientId)
        {
            // Source: SuSubsystemTerrain.cs - 接收远程方块修改
            if (msg == null || (msg.TargetClientId >= 0 && msg.TargetClientId != client.ClientID))
                return;
            if (IsHost && sourceClientId != 0)
            {
                SendAuthoritativeTerrainRepair(sourceClientId, msg.ModifiedCells);
                return;
            }
            else if (!IsHost && sourceClientId == 0)
            {
                if (msg.IsCatchUp && msg.HeadSequence > 0)
                    m_worldTransferRegistry.ClientTerrainJoinBaselineRevision = Math.Max(
                        m_worldTransferRegistry.ClientTerrainJoinBaselineRevision, msg.HeadSequence);
                msg = FilterPendingTerrainPlacePredictions(msg);
                if (msg == null || (msg.ModifiedCells.Count == 0 && msg.Sequence <= 0)) return;
                msg = FilterStaleTerrainRepairs(msg);
                if (msg == null || (msg.ModifiedCells.Count == 0 && msg.Sequence <= 0)) return;
                ConfirmTerrainPredictions(msg);
                // Source: ScMultiplayer.SendTerrainCatchUp
                // Only an accepted authoritative catch-up batch advances the join countdown.
                // Ordinary position, keepalive and steady-state packets are not join progress.
                if (msg.IsCatchUp && m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
                    RecordClientJoinProgress();
            }
            SuSubsystemTerrain.EnqueueNetworkBatch(msg);
        }

        // Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.ChunkInitialized
        // A loaded chunk does not raise ChunkInitialized again after a live terrain change. Keep a
        // coalesced per-chunk authority checkpoint so a sequence that was received but not applied
        // to one cell is repaired while the client remains in the room.
        internal void OnClientTerrainCellApplicationFailed(Point3 cell, long sequence)
        {
            if (IsHost || sequence <= 0 || GameManager.Project == null)
                return;
            Point2 coordinates = Terrain.ToChunk(cell.X, cell.Z);
            if (!m_clientTerrainChunkVerifications.TryGetValue(coordinates,
                    out PendingTerrainChunkVerification pending))
            {
                pending = new PendingTerrainChunkVerification();
                m_clientTerrainChunkVerifications.Add(coordinates, pending);
            }
            pending.RequiredRevision = Math.Max(pending.RequiredRevision, sequence);
            pending.DueTime = Time.RealTime + TerrainChunkVerificationDelay;
        }

        private void TrackHostTerrainPlaceIntent(ComponentPlayer player, Ray3? interactRay)
        {
            if (player == null || !interactRay.HasValue || client?.IsConnected != true ||
                GetConnectedRemoteClients().Count == 0 ||
                !TryGetTerrainPlacePrediction(player, interactRay.Value, out Point3 cell,
                    out int expectedValue, out _))
                return;
            m_hostTerrainPlaceFallbacks[cell] = new PendingHostTerrainPlaceFallback
            {
                ExpectedValue = expectedValue,
                CheckAfterFrameIndex = Time.FrameIndex + 1,
                ExpiresAt = Time.RealTime + HostTerrainPlaceFallbackLifetime
            };
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        private GameModifiedCellsMessage FilterPendingTerrainPlacePredictions(
            GameModifiedCellsMessage message)
        {
            if (message?.ModifiedCells == null || message.CellValues == null ||
                (m_pendingTerrainPlacePredictionCells.Count == 0 &&
                m_localCollapsingPlacePredictions.Count == 0))
                return message;

            var cells = new Dictionary<Point3, bool>();
            var values = new List<int>();
            int index = 0;
            foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
            {
                int value = index < message.CellValues.Count
                    ? message.CellValues[index]
                    : 0;
                bool collapsingPrediction = m_localCollapsingPlacePredictions.TryGetValue(
                    item.Key, out double expiresAt) && Time.RealTime <= expiresAt;
                bool conflictsWithPrediction = m_pendingTerrainPlacePredictionCells.TryGetValue(
                    item.Key, out int requestId) &&
                    m_pendingTerrainPlacePredictions.TryGetValue(requestId,
                        out PendingTerrainPlacePrediction prediction) &&
                    prediction.HasLocalPrediction && value != prediction.LocalPredictedValue &&
                    value == prediction.Request.ExpectedValue;
                if (!collapsingPrediction && !conflictsWithPrediction)
                {
                    cells[item.Key] = item.Value;
                    values.Add(value);
                }
                index++;
            }
            if (cells.Count == message.ModifiedCells.Count) return message;
            return new GameModifiedCellsMessage(cells, values, message.Tick,
                message.IsCatchUp, message.TargetClientId, message.Sequence);
        }

        private void SendAuthoritativeTerrainRepair(int targetClientId,
            Dictionary<Point3, bool> requestedCells)
        {
            if (!IsHost || targetClientId <= 0 || requestedCells == null ||
                requestedCells.Count == 0 || !m_networkPlayerData.ContainsKey(targetClientId))
                return;
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null) return;
            var cells = new List<KeyValuePair<Point3, bool>>();
            foreach (KeyValuePair<Point3, bool> item in requestedCells)
            {
                if (!terrain.Terrain.IsCellValid(item.Key.X, item.Key.Y, item.Key.Z)) continue;
                cells.Add(item);
            }
            if (cells.Count == 0) return;
            for (int offset = 0; offset < cells.Count; offset += TerrainReliableBatchSize)
            {
                var batch = new Dictionary<Point3, bool>();
                var values = new List<int>();
                int count = Math.Min(TerrainReliableBatchSize, cells.Count - offset);
                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<Point3, bool> item = cells[offset + i];
                    batch[item.Key] = item.Value;
                    int value = terrain.Terrain.GetCellValue(
                        item.Key.X, item.Key.Y, item.Key.Z);
                    values.Add(Terrain.ReplaceLight(value, 0));
                }
                var response = new GameModifiedCellsMessage(batch, values, client.Step,
                    true, targetClientId);
                // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:
                // ServerGame.SendDirectInput
                // Per-cell tick rejection makes this reliable response order-independent.
                NetworkMessageSender.SendRawMessage(targetClientId, response);
            }
        }

        private GameModifiedCellsMessage FilterStaleTerrainRepairs(
            GameModifiedCellsMessage message)
        {
            if (message?.IsCatchUp != true || message.ModifiedCells == null ||
                message.CellValues == null || m_pendingTerrainPredictionCells.Count == 0)
                return message;

            var cells = new Dictionary<Point3, bool>();
            var values = new List<int>();
            int index = 0;
            foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
            {
                int value = index < message.CellValues.Count
                    ? message.CellValues[index]
                    : 0;
                bool staleRepair = m_pendingTerrainPredictionCells.TryGetValue(
                    item.Key, out int requestId) &&
                    m_pendingTerrainPredictions.TryGetValue(
                        requestId, out PendingTerrainPrediction prediction) &&
                    value != prediction.Request.PredictedValue;
                if (!staleRepair)
                {
                    cells[item.Key] = item.Value;
                    values.Add(value);
                }
                index++;
            }
            if (cells.Count == message.ModifiedCells.Count) return message;
            return cells.Count > 0
                ? new GameModifiedCellsMessage(cells, values, message.Tick,
                    message.IsCatchUp, message.TargetClientId, message.Sequence)
                : null;
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Dig
        private void HandleTerrainDigRequest(TerrainDigRequestMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0) return;
            long requestKey = ((long)sourceClientId << 32) | (uint)message.RequestId;
            if (m_processedTerrainDigRequests.TryGetValue(requestKey,
                out TerrainDigResultMessage previousResult))
            {
                NetworkMessageSender.SendTerrainDigResult(sourceClientId, previousResult);
                return;
            }

            Project project = GameManager.Project;
            bool accepted = false;
            int authoritativeValue = 0;
            if (project != null && m_networkPlayerData.TryGetValue(sourceClientId,
                out PlayerData playerData) && playerData?.ComponentPlayer != null)
            {
                ComponentPlayer player = playerData.ComponentPlayer;
                ComponentMiner miner = player.ComponentMiner;
                SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(true);
                int currentCellValue = terrain.Terrain.GetCellValue(
                    message.Cell.X, message.Cell.Y, message.Cell.Z);
                authoritativeValue = currentCellValue;
                int authoritativeContents = Terrain.ExtractContents(authoritativeValue);
                int expectedContents = Terrain.ExtractContents(message.ExpectedValue);
                Vector3 center = new Vector3(message.Cell.X + 0.5f,
                    message.Cell.Y + 0.5f, message.Cell.Z + 0.5f);
                Block targetBlock = BlocksManager.Blocks[authoritativeContents];
                bool contentsMatch = authoritativeContents == expectedContents;
                bool isLeafBlock = targetBlock is DeciduousLeavesBlock;
                bool leafContentsMatch = authoritativeContents == expectedContents;
                TerrainRaycastResult? targetRaycast = null;
                if (miner != null && (contentsMatch || isLeafBlock && leafContentsMatch))
                {
                    SubsystemGameInfo gameInfo =
                        project.FindSubsystem<SubsystemGameInfo>(true);
                    bool creative = gameInfo.WorldSettings.GameMode == GameMode.Creative;
                    float reach = creative ? SettingsManager.CreativeReach : 5f;
                    // Source: ComponentMiner.Dig
                    // The sampled client position is only a reach check. Never rewind the host
                    // replica body to the historical position captured with this request.
                    Vector3 authoritativePlayerPosition = player.ComponentBody.Position;
                    if (m_networkPlayerInputs.TryGetValue(sourceClientId,
                        out NetworkPlayerInputState inputState) &&
                        Time.RealTime - inputState.LastReceivedTime <= RemoteInputHoldDuration)
                    {
                        authoritativePlayerPosition = inputState.BodyPosition;
                    }
                    bool inReach = Vector3.DistanceSquared(
                        authoritativePlayerPosition, center) <=
                        MathUtils.Sqr(reach + 1.5f);
                    Vector3 rayDirection = message.DigRay.Direction;
                    if (inReach && message.HitFace >= 0 && message.HitFace <= 5)
                    {
                        // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:
                        // Leaf seasonal data can differ between peers. Dig requests only need the
                        // stable block contents index to match; the final host value is broadcast
                        // unchanged through the normal terrain change path.
                        if (contentsMatch || isLeafBlock && leafContentsMatch)
                        {
                            targetRaycast = new TerrainRaycastResult
                            {
                                Ray = message.DigRay,
                                Value = currentCellValue,
                                CellFace = new CellFace(message.Cell.X, message.Cell.Y,
                                    message.Cell.Z, message.HitFace),
                                CollisionBoxIndex = 0,
                                Distance = 0f
                            };
                        }

                        // Source: SubsystemTerrain.cs:SubsystemTerrain.Raycast
                        // Test the client's requested cell directly. A preceding predicted cell may
                        // still exist on the host while fast consecutive dig requests are in flight.
                        if (!targetRaycast.HasValue &&
                            rayDirection.LengthSquared() > 0.0001f)
                        {
                            rayDirection = Vector3.Normalize(rayDirection);
                            var ray = new Ray3(message.DigRay.Position, rayDirection);
                            var localRay = new Ray3(ray.Position - new Vector3(
                                message.Cell.X, message.Cell.Y, message.Cell.Z), ray.Direction);
                            float? distance = targetBlock.Raycast(localRay, terrain,
                                currentCellValue, true, out int collisionBoxIndex,
                                out BoundingBox collisionBox);
                            if (!targetBlock.IsDiggingTransparent && distance.HasValue &&
                                distance.Value >= 0f && distance.Value <= 15f)
                            {
                                targetRaycast = new TerrainRaycastResult
                                {
                                    Ray = ray,
                                    Value = currentCellValue,
                                    CellFace = new CellFace(message.Cell.X, message.Cell.Y,
                                        message.Cell.Z, message.HitFace),
                                    CollisionBoxIndex = collisionBoxIndex,
                                    Distance = distance.Value
                                };
                            }
                        }
                    }
                    if (targetRaycast.HasValue)
                    {
                        IInventory inventory = miner.Inventory;
                        bool validToolSlot = ApplyTerrainDigToolState(inventory, message);
                        int toolValue = validToolSlot ? miner.ActiveBlockValue : 0;
                        int toolContents = Terrain.ExtractContents(toolValue);
                        Block block = BlocksManager.Blocks[Terrain.ExtractContents(authoritativeValue)];
                        Block tool = BlocksManager.Blocks[toolContents];
                        bool levelSufficient = ModManager.ModParentMethod.InvokeParentMethod<bool>(
                            miner, "IsLevelSufficientForTool", toolValue);
                        float requiredTime = ModManager.ModParentMethod.InvokeParentMethod<float>(
                            miner, "CalculateDigTime", authoritativeValue, toolContents);
                        float elapsedTime = MathUtils.Max(0f,
                            (message.CompletedClientTick - message.StartClientTick) * ServerTickDuration);
						BlockPlacementData digValue = block.GetDigValue(
							terrain, miner, currentCellValue, toolValue, targetRaycast.Value);
						int predictedDigValue = Terrain.ReplaceLight(digValue.Value, 0);
			// Source: Survivalcraft/Game/Block.GetDigValue
			// Ice can be locally converted to water by fluid/weather simulation before
			// the request is evaluated. The host remains authoritative for the final
			// value, so only the stable ice target must match in this special case.
			bool dynamicIceDig = authoritativeContents == 62 && expectedContents == 62;
			bool predictedValueMatches = dynamicIceDig || predictedDigValue ==
				Terrain.ReplaceLight(message.PredictedValue, 0);
						Point3 digPoint = new Point3(digValue.CellFace.X,
                            digValue.CellFace.Y, digValue.CellFace.Z);
                        bool matchingDigProgress = miner.DigCellFace.HasValue &&
                            miner.DigCellFace.Value.X == message.Cell.X &&
                            miner.DigCellFace.Value.Y == message.Cell.Y &&
                            miner.DigCellFace.Value.Z == message.Cell.Z &&
                            miner.DigProgress >= 0.85f;
                        if (validToolSlot && levelSufficient && predictedValueMatches && digPoint == message.Cell &&
                            (creative || matchingDigProgress ||
                                elapsedTime + 0.4f >= requiredTime))
                        {
                            bool dugIce = authoritativeContents == 62;
                            if (dugIce)
                                SuSubsystemTerrain.BeginIceTrace(digPoint);
                            terrain.DestroyCell(tool.ToolLevel, digPoint.X, digPoint.Y,
                                digPoint.Z, digValue.Value, false, false);
                            terrain.TerrainUpdater.RequestSynchronousUpdate();
                            (terrain as SuSubsystemTerrain)?
                                .FlushHostModifiedCellClosureForNetworkAction();
                            miner.DamageActiveTool(1);
                            if (miner.ComponentCreature.PlayerStats != null)
                                miner.ComponentCreature.PlayerStats.BlocksDug++;
                            authoritativeValue = Terrain.ReplaceLight(
                                terrain.Terrain.GetCellValue(digPoint.X, digPoint.Y, digPoint.Z), 0);
                            accepted = true;
                        }
                    }
                }
            }

            var result = new TerrainDigResultMessage(message.RequestId, message.Cell,
                accepted, authoritativeValue, client.Step);
            if (accepted)
            {
                if (m_processedTerrainDigRequests.Count >= 2048)
                    m_processedTerrainDigRequests.Clear();
                m_processedTerrainDigRequests[requestKey] = result;
                PublishServerAudit("terrain.dig", sourceClientId,
                    "cell=" + message.Cell.X.ToString(CultureInfo.InvariantCulture) + "," +
                    message.Cell.Y.ToString(CultureInfo.InvariantCulture) + "," +
                    message.Cell.Z.ToString(CultureInfo.InvariantCulture));
            }
            NetworkMessageSender.SendTerrainDigResult(sourceClientId, result);
        }

        // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:
        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Dig
        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.AddSlotItems
        private static bool ApplyTerrainDigToolState(IInventory inventory,
            TerrainDigRequestMessage message)
        {
            if (inventory == null || message == null || message.ActiveSlotIndex < 0 ||
                message.ActiveSlotIndex >= inventory.VisibleSlotsCount ||
                message.ToolCount < 0 || (message.ToolValue != 0 && message.ToolCount <= 0))
                return false;
            int slot = message.ActiveSlotIndex;
            int count = message.ToolValue == 0 ? 0 : Math.Min(message.ToolCount,
                inventory.GetSlotCapacity(slot, message.ToolValue));
            if (message.ToolValue != 0 && count <= 0) return false;
            inventory.ActiveSlotIndex = slot;
            inventory.RemoveSlotItems(slot, int.MaxValue);
            if (count > 0) inventory.AddSlotItems(slot, message.ToolValue, count);
            return inventory.GetSlotValue(slot) == message.ToolValue &&
                inventory.GetSlotCount(slot) == count;
        }

        private void ConfirmTerrainPredictions(GameModifiedCellsMessage message)
        {
            if (message?.ModifiedCells == null || message.CellValues == null) return;
            int index = 0;
            foreach (Point3 cell in message.ModifiedCells.Keys)
            {
                if (index >= message.CellValues.Count) break;
                if (m_pendingTerrainPredictionCells.TryGetValue(cell, out int requestId) &&
                    m_pendingTerrainPredictions.TryGetValue(requestId,
                        out PendingTerrainPrediction prediction) &&
                    message.CellValues[index] == prediction.Request.PredictedValue)
                    RemovePendingTerrainPrediction(requestId);
                m_localTerrainUsePredictions.Remove(cell);
                index++;
            }
        }

        private void RecordHostTerrainChanges(GameModifiedCellsMessage message,
            int authoritativeTick, bool isFluidSettlementConfirmation = false)
        {
            if (message?.ModifiedCells == null) return;
            lock (m_terrainJournalLock)
            {
                int index = 0;
                foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
                {
                    if (message.CellValues != null && index < message.CellValues.Count)
                    {
                        m_pendingTerrainChanges[item.Key] = new TerrainCellState
                        {
                            IsModified = item.Value,
                            CellValue = message.CellValues[index],
                            Tick = authoritativeTick,
                            Sequence = message.Sequence
                        };
                        if (message.Sequence > 0)
                        {
                            Point2 coordinates = Terrain.ToChunk(item.Key.X, item.Key.Z);
                            m_hostTerrainChunkRevisions[coordinates] = message.Sequence;
                        }
                        int cellValue = message.CellValues[index];
                        int contents = Terrain.ExtractContents(cellValue);
                        if (!isFluidSettlementConfirmation &&
                            contents == 0 &&
                            TryGetFluidSettlementDelay(item.Key, out double delay))
                        {
                            double dueGameTime = GetCurrentGameTime() + delay;
                            if (m_pendingFluidSettlements.TryGetValue(item.Key,
                                out PendingFluidSettlement pending))
                            {
                                pending.TransientValue = cellValue;
                                pending.DueGameTime = Math.Min(
                                    pending.DueGameTime, dueGameTime);
                            }
                            else
                            {
                                m_pendingFluidSettlements[item.Key] =
                                    new PendingFluidSettlement
                                    {
                                        TransientValue = cellValue,
                                        DueGameTime = dueGameTime
                                    };
                            }
                        }
                        else
                        {
                            m_pendingFluidSettlements.Remove(item.Key);
                        }
                    }
                    index++;
                }
            }
            if (message.Sequence > 0)
                MarkHostTerrainSyncStateDirty();
        }

        // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:
        // SubsystemFluidBlockBehavior.SpreadFluid
        // Fluid changes call ProcessModifiedCells internally, before SuSubsystemTerrain can
        // journal them. Confirm only transient air cells beside fluid after the native spread
        // interval, then reuse one terrain read for comparison, broadcast and persistence.
        private void ConfirmPendingFluidSettlements()
        {
            if (!IsHost || m_pendingFluidSettlements.Count == 0) return;
            EnsureHostTerrainSyncStateLoaded();
            double gameTime = GetCurrentGameTime();
            Point3[] dueCells;
            lock (m_terrainJournalLock)
            {
                dueCells = m_pendingFluidSettlements.Where(item =>
                    gameTime >= item.Value.DueGameTime).Select(item => item.Key).ToArray();
            }
            if (dueCells.Length == 0) return;

            SubsystemTerrain terrain = GameManager.Project?
                .FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null) return;
            var settledCells = new List<KeyValuePair<Point3, int>>();
            foreach (Point3 cell in dueCells)
            {
                PendingFluidSettlement pending;
                lock (m_terrainJournalLock)
                {
                    if (!m_pendingFluidSettlements.TryGetValue(cell, out pending) ||
                        gameTime < pending.DueGameTime)
                        continue;
                    m_pendingFluidSettlements.Remove(cell);
                }

                int finalValue = Terrain.ReplaceLight(terrain.Terrain.GetCellValue(
                    cell.X, cell.Y, cell.Z), 0);
                settledCells.Add(new KeyValuePair<Point3, int>(cell, finalValue));
            }

            // Source: ScMultiplayer.PublishTerrainChanges
            // A fluid wave can settle many cells at once. Keep the existing 48-cell reliable
            // payload limit, but allocate one sequence per batch instead of one per cell.
            for (int offset = 0; offset < settledCells.Count;
                offset += TerrainReliableBatchSize)
            {
                int count = Math.Min(TerrainReliableBatchSize, settledCells.Count - offset);
                var cells = new Dictionary<Point3, bool>();
                var values = new List<int>(count);
                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<Point3, int> item = settledCells[offset + i];
                    cells[item.Key] = true;
                    values.Add(item.Value);
                }
                var confirmation = new GameModifiedCellsMessage(cells, values,
                    client.Step, false, -1);
                lock (m_terrainJournalLock)
                    confirmation.Sequence = ++m_hostTerrainSequence;
                RecordHostTerrainChanges(confirmation, client.Step,
                    isFluidSettlementConfirmation: true);
                RecordHostTerrainJournal(confirmation);
                NetworkMessageSender.SendScheduledMessage(-1, confirmation);
            }
        }

        // Source: Survivalcraft/Game/SubsystemWaterBlockBehavior.cs:
        // SubsystemWaterBlockBehavior.Update
        private bool TryGetFluidSettlementDelay(Point3 cell, out double delay)
        {
            delay = 0.0;
            SubsystemTerrain terrain = GameManager.Project?
                .FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null) return false;
            bool slowFluidFound = false;
            for (int face = 0; face < 6; face++)
            {
                Point3 offset = CellFace.FaceToPoint3(face);
                int value = terrain.Terrain.GetCellValue(
                    cell.X + offset.X, cell.Y + offset.Y, cell.Z + offset.Z);
                Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
                if (block is WaterBlock)
                {
                    delay = WaterSettlementDelay;
                    return true;
                }
                if (block is FluidBlock)
                    slowFluidFound = true;
            }
            if (slowFluidFound)
                delay = SlowFluidSettlementDelay;
            return slowFluidFound;
        }

        private static double GetCurrentGameTime()
        {
            return GameManager.Project?.FindSubsystem<SubsystemTime>(false)?.GameTime ?? 0.0;
        }

    }
}

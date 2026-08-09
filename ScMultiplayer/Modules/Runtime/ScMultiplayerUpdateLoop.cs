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
using ScMultiplayer.Transport;
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
    public partial class ScMultiplayer : global::ScMultiplayer.Ports.IMultiplayerRuntimeHost
    {
        // ====================================================================
        // Update
        // ====================================================================
        private void UpdateFrame(float dt)
        {
            m_controlUnit?.Tick(dt, Time.RealTime, IsHost, client?.IsConnected == true);
        }

        // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:RemoteServerDirectory.SetDiscoveryEnabled
        public void SetServerDiscoveryEnabled(bool enabled)
        {
            m_remoteServerDirectory?.SetDiscoveryEnabled(enabled);
        }

        public bool ShouldSuppressClientInput =>
            !IsHost && client?.IsConnected == true &&
            (m_clientTerrainRecoveryActive ||
            m_circuitSynchronizer?.ShouldSuppressClientInput == true);

        internal long CircuitTerrainSequence => IsHost
            ? m_hostTerrainSequence
            : SuSubsystemTerrain.LastAppliedTerrainSequence;

        // Source: ScMultiplayer.cs:ScMultiplayer.GetEditableDataPlayer
        internal ComponentPlayer GetCircuitPlayer(int sourceClientId) =>
            GetEditableDataPlayer(sourceClientId);

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Interact
        public bool TryScheduleLocalCircuitInteraction(ComponentPlayer player, Ray3? ray) =>
            client?.IsConnected == true &&
            m_circuitSynchronizer?.TryScheduleLocalInteraction(player, ray) == true;

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.UpdateProject
        public void NotifyProjectSimulationStep(Project project)
        {
            if (project != null && ReferenceEquals(project, GameManager.Project))
                m_lastProjectSimulationFrameIndex = Time.FrameIndex;
        }

        // Source: Engine/Window.cs:Window.Deactivated
        private void HandleWindowDeactivated()
        {
            m_circuitSynchronizer?.SetWindowActive(false);
            // Source: Survivalcraft/Game/Program.cs:Program.Run
            // Losing focus does not stop the Windows Project update loop. Keep receiving terrain,
            // circuit and world-time state instead of starting an unnecessary recovery barrier.
            if (OperatingSystem.IsWindows()) return;
            if (!IsHost && client?.IsConnected == true)
            {
                m_clientWindowDeactivated = true;
                m_clientSuspensionRequested = true;
                m_clientTerrainRecoveryActive = true;
            }
        }

        // Source: Engine/Window.cs:Window.Activated
        private void HandleWindowActivated()
        {
            // Source: Comms/UdpTransmitter.cs:UdpTransmitter.InvalidateIPv4NetworkSnapshot
            UdpTransmitter.InvalidateIPv4NetworkSnapshot();
            m_circuitSynchronizer?.SetWindowActive(true);
            if (!m_clientWindowDeactivated) return;
            m_clientWindowDeactivated = false;
            if (!IsHost && client?.IsConnected == true)
                m_clientTerrainRecoveryPending = true;
        }

        // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.CurrentScreen
        private void UpdateClientSuspensionState(Project project)
        {
            if (m_clientSuspensionRequested)
            {
                m_clientSuspensionRequested = false;
                BeginClientTerrainSuspension();
            }
            bool eligible = !IsHost && client?.IsConnected == true && project != null &&
                !m_isLoadingDownloadedWorld && m_worldTransferRegistry.PendingWorldReadyTransferId <= 0;
            bool gameScreenActive = eligible && ScreensManager.CurrentScreen is GameScreen;
            if (!eligible)
            {
                m_clientGameplayScreenObserved = false;
                m_wasClientGameScreenActive = false;
                return;
            }
            if (!m_clientGameplayScreenObserved)
            {
                if (gameScreenActive)
                {
                    m_clientGameplayScreenObserved = true;
                    m_wasClientGameScreenActive = true;
                }
                return;
            }
            if (m_wasClientGameScreenActive && !gameScreenActive)
                BeginClientTerrainSuspension();
            else if (!m_wasClientGameScreenActive && gameScreenActive &&
                m_clientTerrainRecoveryActive)
                m_clientTerrainRecoveryPending = true;
            m_wasClientGameScreenActive = gameScreenActive;
        }

    private void BeginClientTerrainSuspension()
    {
        if (IsHost || client?.IsConnected != true || !m_clientGameplayScreenObserved)
            return;
        if (m_clientTerrainRecoveryActive)
        {
            if (!m_clientTerrainRecoveryRequestInFlight &&
                m_clientTerrainRecoveryTarget < 0 && m_clientTerrainRecoveryReady < 0)
                m_clientTerrainRecoveryPending = true;
            return;
        }
        m_clientTerrainRecoveryActive = true;
        // Queue the request immediately. If the Android lifecycle does not emit an Activated
        // event after returning to the game, waiting for that callback leaves input suppressed
        // forever with no recovery request in flight.
        m_clientTerrainRecoveryPending = true;
            m_clientTerrainRecoveryRequestInFlight = false;
            m_clientTerrainRecoveryTarget = -1;
            m_clientTerrainRecoveryAcknowledged = -1;
            m_clientTerrainRecoveryReady = -1;
            m_localPlayerInput = default;
            m_localInputResendsRemaining = 0;
             m_localAimActive = false;
             m_localAimSlot = -1;
             m_localTerrainDigIntents.Clear();
             m_localTerrainUsePredictions.Clear();
             m_pendingTerrainPredictions.Clear();
            m_pendingTerrainPredictionCells.Clear();
            m_pendingTerrainPlacePredictions.Clear();
            m_pendingTerrainPlacePredictionCells.Clear();
            m_localCollapsingPlacePredictions.Clear();
            m_recentLocalEquipmentSnapshots.Clear();
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.ProcessEndOfFrameActions
        private void UpdateClientTerrainRecoveryAfterNetworkActions()
        {
            if (IsHost || client?.IsConnected != true || GameManager.Project == null ||
                m_isLoadingDownloadedWorld || m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
                return;

            bool gameScreenActive = ScreensManager.CurrentScreen is GameScreen;
            if (gameScreenActive && m_clientTerrainRecoveryActive &&
                !m_clientTerrainRecoveryPending && m_clientTerrainRecoveryTarget < 0 &&
                m_clientTerrainRecoveryReady < 0 && !m_clientTerrainRecoveryRequestInFlight)
                m_clientTerrainRecoveryPending = true;
            long applied = SuSubsystemTerrain.LastAppliedTerrainSequence;
            // Source: ScMultiplayer.cs:ScMultiplayer.HandleGameWorldInfoMessage
            // The host head is a low-frequency authoritative watermark. Reset the recovery timer
            // whenever the continuous client sequence advances; pending work that stops advancing
            // must not hide a missing final packet forever.
            if (m_lastObservedClientTerrainSequence != applied)
            {
                m_lastObservedClientTerrainSequence = applied;
                m_clientTerrainGapDetectedTime = 0.0;
            }
            bool sequenceGap = SuSubsystemTerrain.HasBufferedSequenceGap() ||
                m_remoteTerrainHeadSequence > applied;
            if (!m_clientTerrainRecoveryActive && sequenceGap)
            {
                if (m_clientTerrainGapDetectedTime <= 0.0)
                    m_clientTerrainGapDetectedTime = Time.RealTime;
                else if (Time.RealTime - m_clientTerrainGapDetectedTime >=
                    TerrainGapRecoveryDelay)
                {
                    m_clientTerrainRecoveryActive = true;
                    m_clientTerrainRecoveryPending = true;
                }
            }
            else if (!sequenceGap)
            {
                m_clientTerrainGapDetectedTime = 0.0;
            }

            if (m_clientTerrainRecoveryPending && gameScreenActive)
                SendClientTerrainRecoveryRequest();

            if (m_clientTerrainRecoveryTarget >= 0 &&
                applied >= m_clientTerrainRecoveryTarget &&
                m_clientTerrainRecoveryAcknowledged < m_clientTerrainRecoveryTarget)
            {
                m_clientTerrainRecoveryAcknowledged = m_clientTerrainRecoveryTarget;
                SendTerrainRecoveryMessage(0, new TerrainRecoveryMessage
                {
                    Stage = TerrainRecoveryStage.Acknowledge,
                    LastAppliedSequence = applied,
                    HeadSequence = m_clientTerrainRecoveryTarget,
                    ServerStep = client.Step,
                    // Source: ScMultiplayer.cs:ScMultiplayer.SendClientTerrainRecoveryRequest
                    // The host must know which higher sequences are already buffered locally;
                    // otherwise a follow-up round replays those packets and amplifies recovery.
                    BufferedRanges = SuSubsystemTerrain.GetBufferedSequenceRanges(
                        applied, MaximumTerrainRecoveryRanges)
                });
            }

            if (m_clientTerrainRecoveryReady >= 0 &&
                applied >= m_clientTerrainRecoveryReady)
            {
                Log.Information($"[ScMP] Terrain recovery complete: Sequence={applied}");
                m_clientTerrainRecoveryActive = false;
                m_clientTerrainRecoveryPending = false;
                m_clientTerrainRecoveryRequestInFlight = false;
                m_clientTerrainRecoveryTarget = -1;
                m_clientTerrainRecoveryAcknowledged = -1;
                m_clientTerrainRecoveryReady = -1;
                m_clientTerrainGapDetectedTime = 0.0;
            }
        }

        private void SendClientTerrainRecoveryRequest()
        {
            m_clientTerrainRecoveryPending = false;
            m_clientTerrainRecoveryRequestInFlight = true;
            m_clientTerrainRecoveryTarget = -1;
            m_clientTerrainRecoveryAcknowledged = -1;
            m_clientTerrainRecoveryReady = -1;
            long applied = SuSubsystemTerrain.LastAppliedTerrainSequence;
            var request = new TerrainRecoveryMessage
            {
                Stage = TerrainRecoveryStage.Request,
                LastAppliedSequence = applied,
                ServerStep = client.Step,
                BufferedRanges = SuSubsystemTerrain.GetBufferedSequenceRanges(
                    applied, MaximumTerrainRecoveryRanges)
            };
            SendTerrainRecoveryMessage(0, request);
            Log.Information($"[ScMP] Terrain recovery requested: Applied={applied}, " +
                $"BufferedRanges={request.BufferedRanges.Count}");
        }

        private static void SendTerrainRecoveryMessage(int targetClientId,
            TerrainRecoveryMessage message)
        {
            if (client?.IsConnected != true || message == null) return;
            NetworkMessageSender.SendRawMessage(targetClientId, message, sequenced: true);
        }

        // Source: Survivalcraft/Game/PerformanceManager.cs:PerformanceManager.Draw
        private void UpdateNetworkStatsOverlay()
        {
            EnsureNetworkStatsLabel();
            if (m_networkStatsLabel == null) return;
            float rootScale = MathUtils.Max(
                ScreensManager.RootWidget?.GlobalScale ?? 1f, 0.01f);
            float displayScale = MathUtils.Round(
                MathUtils.Clamp(rootScale, 1f, 4f));
            float widgetScaleCompensation = displayScale / rootScale;
            BitmapFont statsFont = BitmapFont.DebugFont;
            float lineHeight = (statsFont.GlyphHeight + statsFont.Spacing.Y) *
                statsFont.Scale;
            m_networkStatsLabel.FontScale = widgetScaleCompensation;
            m_networkStatsLabel.Margin = new Vector2(
                0f, lineHeight * widgetScaleCompensation);
            bool visible = SettingsManager.DisplayFpsCounter &&
                client?.IsConnected == true;
            m_networkStatsLabel.IsVisible = visible;
            if (!visible || Time.RealTime < m_nextNetworkStatsUpdateTime) return;
            m_nextNetworkStatsUpdateTime = Time.RealTime + 1.0;
            ReadNetworkStats(out float throughputBytesPerSecond, out float latencyMs,
                out float ackLatencyMs, out int syncQueue, out int applyQueue,
                out float applyOldestMs, out int reliableQueue,
                out float retransmitPercent, out long reliableRetryLimitCount,
                out long pendingBlocks, out long blockWindowReceived,
                out int blocksReceivedPerSecond, out int blocksConsumedPerSecond);
            string circuitState = m_circuitSynchronizer?.ClientStateText ??
                (IsHost ? "Host" : "Unbound");
            float fenceAge = m_circuitSynchronizer?.FenceAgeMilliseconds ?? -1f;
            string fenceText = fenceAge < 0f ? "--" :
                string.Format(CultureInfo.InvariantCulture, "{0:0}ms", fenceAge);
            m_networkStatsLabel.Text = string.Format(CultureInfo.InvariantCulture,
                "NET {0}, Ping {1:0}ms, Ack {2:0}ms, ReTx {7:0.0}%\r\n" +
                "Q Sync {3}, Apply {4} ({5:0}ms), Rel {6}\r\n" +
                "Ckt {8}, Fence {9}, Limit {10}\r\n" +
                "Block {11} (+{12}, +{13}/s, -{14}/s)",
                FormatNetworkThroughput(throughputBytesPerSecond), latencyMs, ackLatencyMs,
                syncQueue, applyQueue, applyOldestMs, reliableQueue,
                retransmitPercent, circuitState, fenceText, reliableRetryLimitCount,
                pendingBlocks, blockWindowReceived, blocksReceivedPerSecond,
                blocksConsumedPerSecond);
        }

        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticStats.BytesSent/BytesReceived
        private static string FormatNetworkThroughput(float bytesPerSecond)
        {
            return NetworkStatusFormatter.FormatBytesPerSecond(bytesPerSecond);
        }

        private void EnsureNetworkStatsLabel()
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null) return;
            if (m_networkStatsLabel == null)
            {
                BitmapFont font = BitmapFont.DebugFont;
                float lineHeight = (font.GlyphHeight + font.Spacing.Y) * font.Scale;
                m_networkStatsLabel = new LabelWidget
                {
                    Name = "ScMultiplayer.NetworkStats",
                    Font = font,
                    FontScale = 1f,
                    TextureLinearFilter = false,
                    Color = Color.White,
                    HorizontalAlignment = WidgetAlignment.Far,
                    VerticalAlignment = WidgetAlignment.Near,
                    TextAnchor = TextAnchor.Right,
                    Margin = new Vector2(0f, lineHeight),
                    IsHitTestVisible = false,
                    IsVisible = false
                };
            }
            if (m_networkStatsLabel.ParentWidget == null)
                root.Children.Add(m_networkStatsLabel);
        }

        private void ReadNetworkStats(out float throughputBytesPerSecond, out float latencyMs,
            out float ackLatencyMs, out int syncQueue, out int applyQueue,
            out float applyOldestMs, out int reliableQueue, out float retransmitPercent,
            out long reliableRetryLimitCount, out long pendingBlocks,
            out long blockWindowReceived, out int blocksReceivedPerSecond,
            out int blocksConsumedPerSecond)
        {
            throughputBytesPerSecond = 0f;
            latencyMs = 0f;
            ackLatencyMs = 0f;
            retransmitPercent = 0f;
            reliableRetryLimitCount = 0L;
            SuSubsystemTerrain.ReadBlockStats(out pendingBlocks, out blockWindowReceived,
                out blocksReceivedPerSecond, out blocksConsumedPerSecond);
            syncQueue = NetworkMessageSender.PendingSyncBatchCount;
            applyQueue = m_endOfFrameActions.Count + m_terrainChunkSyncActions.Count +
                SuSubsystemTerrain.PendingChunkCheckpointCount;
            applyOldestMs = 0f;
            long oldestTimestamp = 0L;
            if (m_endOfFrameActions.TryPeek(out QueuedFrameAction oldestAction))
            {
                oldestTimestamp = oldestAction.EnqueuedTimestamp;
            }
            if (m_terrainChunkSyncActions.TryPeek(out QueuedTerrainChunkSync oldestChunkSync) &&
                (oldestTimestamp == 0L || oldestChunkSync.EnqueuedTimestamp < oldestTimestamp))
            {
                oldestTimestamp = oldestChunkSync.EnqueuedTimestamp;
            }
            if (oldestTimestamp > 0L)
            {
                long elapsed = Stopwatch.GetTimestamp() - oldestTimestamp;
                if (elapsed > 0)
                    applyOldestMs = (float)(1000.0 * elapsed / Stopwatch.Frequency);
            }
            reliableQueue = 0;
            DiagnosticStats stats = IsHost ? m_serverNetworkStats : m_clientNetworkStats;
            if (stats != null)
            {
                throughputBytesPerSecond = m_networkMetricsCollector
                    .Sample(stats, Time.RealTime).BytesPerSecond;
            }
            if (IsHost && server?.Peer != null)
            {
                foreach (ServerClient remote in GetConnectedRemoteClients())
                {
                    PeerData peer = server.Peer.FindPeer(remote.Address);
                    if (peer == null) continue;
                    double smoothedRtt = server.Peer.Comm
                        .GetSmoothedRoundTripTime(peer.Address);
                    latencyMs = MathUtils.Max(latencyMs,
                        (float)(1000.0 * peer.Ping));
                    ackLatencyMs = MathUtils.Max(ackLatencyMs,
                        (float)(1000.0 * smoothedRtt));
                    reliableQueue = Math.Max(reliableQueue,
                        server.Peer.Comm.GetUnackedPacketsCount(peer.Address));
                    retransmitPercent = MathUtils.Max(retransmitPercent,
                        (float)(100.0 * server.Peer.Comm.GetPacketLossRate(
                            peer.Address)));
                    reliableRetryLimitCount += server.Peer.Comm
                        .GetReliableRetryLimitCount(peer.Address);
                }
                return;
            }
            PeerData connected = client?.Peer?.ConnectedTo;
            if (connected == null) return;
            double clientSmoothedRtt = client.Peer.Comm
                .GetSmoothedRoundTripTime(connected.Address);
            latencyMs = (float)(1000.0 * connected.Ping);
            ackLatencyMs = (float)(1000.0 * clientSmoothedRtt);
            reliableQueue = client.Peer.Comm.GetUnackedPacketsCount(connected.Address);
            retransmitPercent = (float)(100.0 *
                client.Peer.Comm.GetPacketLossRate(connected.Address));
            reliableRetryLimitCount = client.Peer.Comm
                .GetReliableRetryLimitCount(connected.Address);
        }

        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticTransmitter.SendPacket
        // Retransmissions cannot be tagged after Comms has queued them. Treating that excess as
        // gameplay pressure is deliberately conservative: a lossy join pauses before it harms
        // connected players.
        private void UpdateJoinTransferBandwidthBudget()
        {
            DiagnosticStats stats = m_serverNetworkStats;
            if (stats == null) return;

            double now = Time.RealTime;
            UpdateAutomaticJoinTransferRate(now);
            long networkBytes = Math.Max(0L, Volatile.Read(ref stats.BytesSent));
            long receiveBytes = Math.Max(0L, Volatile.Read(ref stats.BytesReceived));
            long joinBytes = Interlocked.Read(ref m_joinTransferBytesSentSinceSample);
            if (m_lastJoinTransferSampleTime <= 0.0)
            {
                m_lastJoinTransferSampleTime = now;
                m_lastJoinTransferNetworkBytesSample = networkBytes;
                m_lastJoinTransferReceiveBytesSample = receiveBytes;
                m_lastJoinTransferBytesSample = joinBytes;
                m_joinTransferLastTokenTime = now;
                return;
            }

            double elapsed = now - m_lastJoinTransferSampleTime;
            if (elapsed <= 0.0) return;
            long networkDelta = Math.Max(0L, networkBytes - m_lastJoinTransferNetworkBytesSample);
            long receiveDelta = Math.Max(0L, receiveBytes - m_lastJoinTransferReceiveBytesSample);
            long joinDelta = Math.Max(0L, joinBytes - m_lastJoinTransferBytesSample);
            long gameplayOutboundBytes = Math.Max(0L, networkDelta - joinDelta);
            // Source: Comms/Comms.Drt/Func/Client/Client.cs:SendDirectInput
            // World chunks pass through the host's local Client before Server relays them to the
            // joiner. DiagnosticStats therefore sees that loopback hop as received traffic even
            // though it never consumes WAN bandwidth. Charge only the unmatched external input
            // against a shared external-bandwidth cap.
            long externalReceiveBytes = Math.Max(0L, receiveDelta - joinDelta);
            long bandwidthUsageBytes = gameplayOutboundBytes +
                (!ScMultiplayerSettings.BandwidthConfigurationEnabled ||
                    ScMultiplayerSettings.BandwidthMode == BandwidthLimitMode.SharedTotal
                    ? externalReceiveBytes
                    : 0L);
            m_joinTransferTrafficSamples.Enqueue(new JoinTransferTrafficSample(now,
                bandwidthUsageBytes));
            // Source: ScMultiplayer.cs:UpdateJoinTransferBandwidthBudget
            // Retain only a short gameplay history so a completed traffic spike does not
            // throttle a later join for the full display window. The current sample remains
            // authoritative for immediate protection of already-connected players.
            while (m_joinTransferTrafficSamples.Count > 0 &&
                now - m_joinTransferTrafficSamples.Peek().Time > 2.0)
                m_joinTransferTrafficSamples.Dequeue();

            long recentGameplayBytes = 0L;
            foreach (JoinTransferTrafficSample sample in m_joinTransferTrafficSamples)
                recentGameplayBytes += sample.GameplayBytes;
            double recentWindow = m_joinTransferTrafficSamples.Count > 0
                ? Math.Max(0.25, now - m_joinTransferTrafficSamples.Peek().Time)
                : elapsed;
            double gameplayBytesPerSecond = Math.Max(bandwidthUsageBytes / elapsed,
                recentGameplayBytes / recentWindow);
            m_joinTransferGameplayBytesPerSecond = gameplayOutboundBytes / elapsed;
            m_joinTransferBytesPerSecond = joinDelta / elapsed;
            m_joinTransferReceiveBytesPerSecond = externalReceiveBytes / elapsed;
            double configuredLimit = KilobitsPerSecondToBytes(
                GetJoinTransferBandwidthLimitKbps());
            double configuredJoinLimit = KilobitsPerSecondToBytes(
                ScMultiplayerSettings.BandwidthConfigurationEnabled
                    ? ScMultiplayerSettings.JoinTransferMaxKbps
                    : 0);
            double headroom = KilobitsPerSecondToBytes(
                ScMultiplayerSettings.BandwidthConfigurationEnabled
                    ? ScMultiplayerSettings.JoinTransferGameplayHeadroomKbps
                    : AutomaticJoinTransferGameplayReserveKbps);
            double available = JoinTransferBudgetPolicy.CalculateAvailableBytesPerSecond(
                configuredLimit, configuredJoinLimit, gameplayBytesPerSecond, headroom);

            m_joinTransferAvailableBytesPerSecond = available;
            m_joinTransferPausedByGameplay = configuredLimit > 0.0 && available <= 0.0 &&
                (m_worldTransferRegistry.OutgoingTransfers.Count > 0 || m_joinCatchUpRegistry.Pending.Count > 0);
            double tokenElapsed = Math.Max(0.0, now - m_joinTransferLastTokenTime);
            m_joinTransferLastTokenTime = now;
            if (double.IsPositiveInfinity(available))
            {
                m_joinTransferTokens = double.PositiveInfinity;
            }
            else
            {
                double burstBytes = GetEffectiveJoinTransferBurstBytes();
                m_joinTransferTokens = JoinTransferBudgetPolicy.RefillTokens(
                    m_joinTransferTokens, available, tokenElapsed, burstBytes);
            }
            m_lastJoinTransferSampleTime = now;
            m_lastJoinTransferNetworkBytesSample = networkBytes;
            m_lastJoinTransferReceiveBytesSample = receiveBytes;
            m_lastJoinTransferBytesSample = joinBytes;
        }

        private bool TryReserveJoinTransferBytes(OutgoingWorldTransfer transfer, int payloadBytes)
        {
            int reservedBytes = JoinTransferBudgetPolicy.EstimatePacketBytes(payloadBytes);
            if (!JoinTransferBudgetPolicy.HasTokens(m_joinTransferTokens, reservedBytes))
                return false;

            double perJoinLimit = KilobitsPerSecondToBytes(
                ScMultiplayerSettings.BandwidthConfigurationEnabled
                    ? ScMultiplayerSettings.JoinTransferPerJoinMaxKbps
                    : 0);
            if (transfer != null && perJoinLimit > 0.0)
            {
                double now = Time.RealTime;
                double burstBytes = Math.Max(WorldTransferChunkSize + 96,
                    Math.Min(GetEffectiveJoinTransferBurstBytes(), perJoinLimit));
                if (transfer.LastBandwidthTokenTime <= 0.0)
                {
                    transfer.LastBandwidthTokenTime = now;
                    transfer.BandwidthTokens = burstBytes;
                }
                else
                {
                    transfer.BandwidthTokens = Math.Min(burstBytes, transfer.BandwidthTokens +
                        perJoinLimit * Math.Max(0.0, now - transfer.LastBandwidthTokenTime));
                    transfer.LastBandwidthTokenTime = now;
                }
                if (transfer.BandwidthTokens < reservedBytes) return false;
                transfer.BandwidthTokens -= reservedBytes;
            }
            if (!double.IsPositiveInfinity(m_joinTransferTokens))
                m_joinTransferTokens -= reservedBytes;
            return true;
        }

        private void RefundJoinTransferBytes(OutgoingWorldTransfer transfer, int payloadBytes)
        {
            int reservedBytes = JoinTransferBudgetPolicy.EstimatePacketBytes(payloadBytes);
            double perJoinLimit = KilobitsPerSecondToBytes(
                ScMultiplayerSettings.BandwidthConfigurationEnabled
                    ? ScMultiplayerSettings.JoinTransferPerJoinMaxKbps
                    : 0);
            if (transfer != null && perJoinLimit > 0.0)
            {
                double burstBytes = Math.Max(WorldTransferChunkSize + 96,
                    Math.Min(GetEffectiveJoinTransferBurstBytes(), perJoinLimit));
                transfer.BandwidthTokens = Math.Min(burstBytes,
                    transfer.BandwidthTokens + reservedBytes);
            }
            if (double.IsPositiveInfinity(m_joinTransferTokens)) return;
            double globalBurstBytes = GetEffectiveJoinTransferBurstBytes();
            m_joinTransferTokens = JoinTransferBudgetPolicy.RefundTokens(
                m_joinTransferTokens, globalBurstBytes, reservedBytes);
        }

        private bool HasManagedJoinTransferRate()
        {
            return !ScMultiplayerSettings.BandwidthConfigurationEnabled ||
                ScMultiplayerSettings.EffectiveJoinBandwidthCapKbps > 0 ||
                ScMultiplayerSettings.JoinTransferMaxKbps > 0;
        }

        private double GetJoinTransferBandwidthLimitKbps()
        {
            return ScMultiplayerSettings.BandwidthConfigurationEnabled
                ? ScMultiplayerSettings.EffectiveJoinBandwidthCapKbps
                : m_automaticJoinTransferKbps;
        }

        // Source: Comms/Comms/Comm.cs:GetUnackedPacketsCount
        // Ramp only after a full stable second. Packet loss, a nearly full reliable window, or a
        // material RTT increase immediately halves the join rate and holds it for three seconds.
        private void UpdateAutomaticJoinTransferRate(double now)
        {
            if (ScMultiplayerSettings.BandwidthConfigurationEnabled)
            {
                m_automaticJoinTransferKbps = AutomaticJoinTransferStartKbps;
                m_nextAutomaticJoinTransferAdjustmentTime = 0.0;
                m_automaticJoinTransferCooldownUntil = 0.0;
                m_automaticJoinRttBaseline = 0.0;
                return;
            }

            bool hasActiveJoin = m_worldTransferRegistry.OutgoingTransfers.Count > 0 ||
                m_joinCatchUpRegistry.Pending.Count > 0;
            if (!hasActiveJoin)
            {
                m_automaticJoinTransferKbps = AutomaticJoinTransferStartKbps;
                m_nextAutomaticJoinTransferAdjustmentTime = 0.0;
                m_automaticJoinTransferCooldownUntil = 0.0;
                m_automaticJoinRttBaseline = 0.0;
                return;
            }
            if (now < m_nextAutomaticJoinTransferAdjustmentTime) return;
            m_nextAutomaticJoinTransferAdjustmentTime = now +
                AutomaticJoinTransferAdjustmentInterval;

            bool pressure = false;
            double highestRtt = 0.0;
            if (server?.Peer != null)
            {
                foreach (ServerClient remote in GetConnectedRemoteClients())
                {
                    if (remote?.Address == null) continue;
                    double lossRate = server.Peer.Comm.GetPacketLossRate(remote.Address);
                    int unacked = server.Peer.Comm.GetUnackedPacketsCount(remote.Address);
                    double rtt = server.Peer.Comm.GetSmoothedRoundTripTime(remote.Address);
                    highestRtt = Math.Max(highestRtt, rtt);
                    if (lossRate >= AutomaticJoinTransferMaximumLossRate ||
                        unacked >= AutomaticJoinTransferPressureUnackedPackets)
                    {
                        pressure = true;
                    }
                }
            }
            if (highestRtt > 0.0)
            {
                if (m_automaticJoinRttBaseline > 0.0 && highestRtt > Math.Max(
                    m_automaticJoinRttBaseline * 1.5,
                    m_automaticJoinRttBaseline + 0.03))
                {
                    pressure = true;
                }
                if (!pressure)
                {
                    m_automaticJoinRttBaseline = m_automaticJoinRttBaseline <= 0.0
                        ? highestRtt
                        : Math.Min(highestRtt,
                            m_automaticJoinRttBaseline * 0.9 + highestRtt * 0.1);
                }
            }

            if (pressure)
            {
                m_automaticJoinTransferKbps = Math.Max(AutomaticJoinTransferStartKbps,
                    m_automaticJoinTransferKbps * AutomaticJoinTransferBackoffFactor);
                m_automaticJoinTransferCooldownUntil = now + AutomaticJoinTransferCooldown;
            }
            else if (now >= m_automaticJoinTransferCooldownUntil)
            {
                m_automaticJoinTransferKbps = Math.Min(AutomaticJoinTransferMaximumKbps,
                    m_automaticJoinTransferKbps * AutomaticJoinTransferGrowthFactor);
            }
        }

        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticStats.BytesReceived
        // Preserve four seconds with no display-stat reads, then capture one full second. The
        // resulting counters are raw bytes for that final second, not a five-second average.
        private void UpdateServerTrafficDisplaySample()
        {
            DiagnosticStats stats = m_serverNetworkStats;
            if (stats == null) return;

            double now = Time.RealTime;
            if (!m_serverTrafficSampleActive)
            {
                if (m_nextServerTrafficSampleStartTime <= 0.0)
                {
                    m_nextServerTrafficSampleStartTime = now + 4.0;
                    return;
                }
                if (now < m_nextServerTrafficSampleStartTime) return;

                m_serverTrafficSampleStartBytesSent = Math.Max(0L,
                    Volatile.Read(ref stats.BytesSent));
                m_serverTrafficSampleStartBytesReceived = Math.Max(0L,
                    Volatile.Read(ref stats.BytesReceived));
                m_serverTrafficSampleStartPacketsSent = Math.Max(0L,
                    Volatile.Read(ref stats.PacketsSent));
                m_serverTrafficSampleStartPacketsReceived = Math.Max(0L,
                    Volatile.Read(ref stats.PacketsReceived));
                m_serverTrafficSampleStartTime = now;
                m_serverTrafficSampleActive = true;
                return;
            }

            if (now - m_serverTrafficSampleStartTime < 1.0) return;
            long bytesSent = Math.Max(0L, Volatile.Read(ref stats.BytesSent));
            long bytesReceived = Math.Max(0L, Volatile.Read(ref stats.BytesReceived));
            long packetsSent = Math.Max(0L, Volatile.Read(ref stats.PacketsSent));
            long packetsReceived = Math.Max(0L, Volatile.Read(ref stats.PacketsReceived));
            m_lastServerTrafficSampleBytesSent = Math.Max(0L,
                bytesSent - m_serverTrafficSampleStartBytesSent);
            m_lastServerTrafficSampleBytesReceived = Math.Max(0L,
                bytesReceived - m_serverTrafficSampleStartBytesReceived);
            m_lastServerTrafficSamplePacketsSent = Math.Max(0L,
                packetsSent - m_serverTrafficSampleStartPacketsSent);
            m_lastServerTrafficSamplePacketsReceived = Math.Max(0L,
                packetsReceived - m_serverTrafficSampleStartPacketsReceived);
            m_serverTrafficSampleActive = false;
            m_nextServerTrafficSampleStartTime = now + 4.0;
        }

        private double GetEffectiveJoinTransferBurstBytes()
        {
            double configuredBurst = (ScMultiplayerSettings.BandwidthConfigurationEnabled
                ? ScMultiplayerSettings.JoinTransferBurstKiB
                : 32) * 1024.0;
            double tickCapacity = double.IsPositiveInfinity(
                m_joinTransferAvailableBytesPerSecond)
                ? 0.0
                // Source: ScMultiplayer.cs:TriggerNetworkTick
                // Keep two transport ticks of budget so normal frame/ack jitter cannot discard
                // already-approved join bandwidth. The token refill rate remains unchanged.
                : m_joinTransferAvailableBytesPerSecond * TransportTickDuration * 2.0;
            return Math.Max(WorldTransferChunkSize + 96,
                Math.Max(configuredBurst, tickCapacity));
        }

        private int GetJoinTransferSendBudget(bool gameplayActive)
        {
            int legacyBudget = gameplayActive
                ? MaximumWorldTransferChunksPerGameplayTick
                : MaximumWorldTransferChunksPerNetworkTick;
            if (!HasManagedJoinTransferRate())
                return legacyBudget;
            if (!double.IsFinite(m_joinTransferTokens) &&
                !double.IsPositiveInfinity(m_joinTransferTokens))
            {
                return 0;
            }
            if (m_joinTransferTokens <= 0.0)
                return 0;
            return Math.Min(MaximumDynamicWorldTransferChunksPerNetworkTick,
                Math.Max(0, (int)Math.Floor(m_joinTransferTokens /
                    JoinTransferBudgetPolicy.EstimatePacketBytes(WorldTransferChunkSize))));
        }

        private int GetWorldTransferUnackedPacketLimit(int targetClientId)
        {
            if (!HasManagedJoinTransferRate() ||
                m_joinTransferAvailableBytesPerSecond <= 0.0 ||
                double.IsPositiveInfinity(m_joinTransferAvailableBytesPerSecond))
            {
                return MaximumWorldTransferUnackedPackets;
            }

            IPEndPoint address = GetServerClientAddress(targetClientId);
            double rtt = address != null
                ? server.Peer.Comm.GetSmoothedRoundTripTime(address)
                : 0.1;
            double flightTime = Math.Max(0.075, Math.Min(0.5, rtt * 1.25));
            int desired = (int)Math.Ceiling(m_joinTransferAvailableBytesPerSecond *
                flightTime / JoinTransferBudgetPolicy.EstimatePacketBytes(WorldTransferChunkSize)) + 4;
            return Math.Min(MaximumDynamicWorldTransferUnackedPackets,
                Math.Max(MaximumWorldTransferUnackedPackets +
                    ConfiguredWorldTransferGameplayQueueAllowance,
                    desired + ConfiguredWorldTransferGameplayQueueAllowance));
        }

        private int GetWorldTransferChunkWindow(int targetClientId)
        {
            return Math.Max(WorldTransferWindowChunks,
                GetReliableBulkUnackedPacketLimit(targetClientId) - 8);
        }

        // Source: Comms/Comms/Comm.cs:Comm.GetUnackedPacketsCount
        // Bulk transfers stop before the transport window is full. The remaining packets are for
        // gameplay confirmations such as circuit edges, pickables, containers and terrain edits.
        private int GetReliableBulkUnackedPacketLimit(int targetClientId)
        {
            return Math.Max(8, GetWorldTransferUnackedPacketLimit(targetClientId) -
                ReliableCriticalReservePackets);
        }

        internal static int EstimateReliableRelayPackets(int payloadBytes)
        {
            // Source: Mod/Comms/Comms/Comm.cs:Comm.SendMessages
            // Leave room for DataMessage, DRT and Comm headers before the 1024-byte UDP limit.
            return Math.Max(1, (Math.Max(0, payloadBytes) + 899) / 900);
        }

        private int GetReliableRelayReservationCountLocked(int targetClientId)
        {
            if (!m_reliableRelayReservations.TryGetValue(targetClientId,
                    out Queue<ReliableRelayReservation> reservations))
                return 0;
            return reservations.Sum(item => Math.Max(0, item.Packets));
        }

        private double GetReliableRelayReservationLifetime(int targetClientId)
        {
            IPEndPoint address = GetServerClientAddress(targetClientId);
            double rtt = address != null && server?.Peer != null
                ? server.Peer.Comm.GetSmoothedRoundTripTime(address)
                : 0.0;
            double lifetime = rtt > 0.0 ? rtt * 1.5 + 0.1 : 0.25;
            return Math.Clamp(lifetime, ReliableRelayReservationMinimumLifetime,
                ReliableRelayReservationMaximumLifetime);
        }

        private void ReconcileReliableRelayReservationsLocked(int targetClientId,
            int actualUnackedPackets, double now, double reservationLifetime)
        {
            if (!m_reliableRelayObservedUnacked.TryGetValue(targetClientId,
                    out int previousUnackedPackets))
                previousUnackedPackets = actualUnackedPackets;

            int newlyObservedPackets = Math.Max(0,
                actualUnackedPackets - previousUnackedPackets);
            if (newlyObservedPackets > 0 &&
                m_reliableRelayReservations.TryGetValue(targetClientId,
                    out Queue<ReliableRelayReservation> reservations))
            {
                while (newlyObservedPackets > 0 && reservations.Count > 0)
                {
                    ReliableRelayReservation reservation = reservations.Peek();
                    int consumed = Math.Min(newlyObservedPackets,
                        Math.Max(0, reservation.Packets));
                    reservation.Packets -= consumed;
                    newlyObservedPackets -= consumed;
                    if (reservation.Packets <= 0)
                        reservations.Dequeue();
                }
                if (reservations.Count == 0)
                    m_reliableRelayReservations.Remove(targetClientId);
            }
            m_reliableRelayObservedUnacked[targetClientId] = actualUnackedPackets;

            if (!m_reliableRelayReservations.TryGetValue(targetClientId,
                    out Queue<ReliableRelayReservation> active))
                return;
            while (active.Count > 0 && now - active.Peek().CreatedTime >=
                reservationLifetime)
                active.Dequeue();
            if (active.Count == 0)
                m_reliableRelayReservations.Remove(targetClientId);
        }

        internal int GetReliableRelayUnackedPackets(int targetClientId)
        {
            if (!IsHost || server?.Peer == null || targetClientId <= 0)
                return 0;
            IPEndPoint address = GetServerClientAddress(targetClientId);
            if (address == null)
                return MaximumWorldTransferUnackedPackets;
            int actualUnackedPackets = server.Peer.Comm.GetUnackedPacketsCount(address);
            lock (m_reliableRelayReservationLock)
            {
                ReconcileReliableRelayReservationsLocked(targetClientId,
                    actualUnackedPackets, Time.RealTime,
                    GetReliableRelayReservationLifetime(targetClientId));
                return actualUnackedPackets +
                    GetReliableRelayReservationCountLocked(targetClientId);
            }
        }

        internal void ReserveReliableRelayPackets(int targetClientId, int packets)
        {
            if (!IsHost || server?.Peer == null || targetClientId <= 0 || packets <= 0)
                return;
            IPEndPoint address = GetServerClientAddress(targetClientId);
            if (address == null) return;
            int actualUnackedPackets = server.Peer.Comm.GetUnackedPacketsCount(address);
            lock (m_reliableRelayReservationLock)
            {
                ReconcileReliableRelayReservationsLocked(targetClientId,
                    actualUnackedPackets, Time.RealTime,
                    GetReliableRelayReservationLifetime(targetClientId));
                if (!m_reliableRelayReservations.TryGetValue(targetClientId,
                        out Queue<ReliableRelayReservation> reservations))
                {
                    reservations = new Queue<ReliableRelayReservation>();
                    m_reliableRelayReservations[targetClientId] = reservations;
                }
                reservations.Enqueue(new ReliableRelayReservation
                {
                    Packets = packets,
                    CreatedTime = Time.RealTime
                });
            }
        }

        internal void ReleaseReliableRelayPackets(int targetClientId, int packets)
        {
            if (targetClientId <= 0 || packets <= 0) return;
            lock (m_reliableRelayReservationLock)
            {
                if (!m_reliableRelayReservations.TryGetValue(targetClientId,
                        out Queue<ReliableRelayReservation> reservations))
                    return;
                int remaining = packets;
                while (remaining > 0 && reservations.Count > 0)
                {
                    ReliableRelayReservation reservation = reservations.Last();
                    int released = Math.Min(remaining, Math.Max(0, reservation.Packets));
                    reservation.Packets -= released;
                    remaining -= released;
                    if (reservation.Packets <= 0)
                        RemoveLastReservation(reservations);
                }
                if (reservations.Count == 0)
                    m_reliableRelayReservations.Remove(targetClientId);
            }
        }

        private static void RemoveLastReservation(
            Queue<ReliableRelayReservation> reservations)
        {
            int count = reservations.Count;
            if (count <= 1)
            {
                reservations.Clear();
                return;
            }
            ReliableRelayReservation[] values = reservations.ToArray();
            reservations.Clear();
            for (int i = 0; i < count - 1; i++)
                reservations.Enqueue(values[i]);
        }

        internal void RemoveReliableRelayReservations(int targetClientId)
        {
            if (targetClientId <= 0) return;
            lock (m_reliableRelayReservationLock)
            {
                m_reliableRelayReservations.Remove(targetClientId);
                m_reliableRelayObservedUnacked.Remove(targetClientId);
            }
        }

        internal void ClearReliableRelayReservations()
        {
            lock (m_reliableRelayReservationLock)
            {
                m_reliableRelayReservations.Clear();
                m_reliableRelayObservedUnacked.Clear();
            }
        }

        internal bool TryReserveReliableRelayPackets(int targetClientId,
            int estimatedPackets, bool joinCritical = false)
        {
            if (!CanSendReliableBulk(targetClientId, estimatedPackets, joinCritical))
                return false;
            ReserveReliableRelayPackets(targetClientId, estimatedPackets);
            return true;
        }

        internal bool CanSendReliableBulk(int targetClientId, int estimatedPackets,
            bool joinCritical = false)
        {
            if (!IsHost || server?.Peer == null || targetClientId <= 0) return false;
            IPEndPoint address = GetServerClientAddress(targetClientId);
            if (address == null) return false;
            int packets = Math.Max(estimatedPackets, 1);
            // Source: Mod/ScMultiplayer/Modules/Join/
            // ScMultiplayerWorldTransferHandlers.cs:HandleGamePakWorldReadyMessage
            // A joining client cannot acknowledge CatchUpBatchApplied until its circuit snapshot
            // is applied. Let that snapshot use the join window; ordinary repair bulk still leaves
            // the gameplay reserve untouched.
            bool useJoinWindow = joinCritical &&
                m_joinCatchUpRegistry.TransfersAwaitingReady.ContainsKey(targetClientId);
            int packetLimit = useJoinWindow
                ? GetWorldTransferUnackedPacketLimit(targetClientId)
                : GetReliableBulkUnackedPacketLimit(targetClientId);
            return GetReliableRelayUnackedPackets(targetClientId) + packets <=
                packetLimit;
        }

        private static double KilobitsPerSecondToBytes(int value)
        {
            return value > 0 ? value * 1000.0 / 8.0 : 0.0;
        }

        private static double KilobitsPerSecondToBytes(double value)
        {
            return value > 0.0 ? value * 1000.0 / 8.0 : 0.0;
        }

        private void RecordJoinTransferBytesSent(int payloadBytes)
        {
            Interlocked.Add(ref m_joinTransferBytesSentSinceSample,
                JoinTransferBudgetPolicy.EstimatePacketBytes(payloadBytes));
        }

        private object[] HandleServerSettingsEvent(object[] args)
        {
            IDictionary<string, object> request = args != null && args.Length > 0
                ? args[0] as IDictionary<string, object> : null;
            string operation = request != null && request.TryGetValue("operation", out object value)
                ? value as string : "get";
            if (string.Equals(operation, "set", StringComparison.OrdinalIgnoreCase))
            {
                bool autoApprove = ScMultiplayerSettings.AutoApproveJoinRequests;
                bool autoHost = ScMultiplayerSettings.AutoCreateRoomFromCurrentWorld;
                ScMultiplayerSettings.UpdateJoinTransferSettings(request);
                if (!autoApprove && ScMultiplayerSettings.AutoApproveJoinRequests && IsHost)
                {
                    Dialog activeDialog = m_activeJoinDecisionDialog;
                    m_activeJoinDecisionDialog = null;
                    m_activeJoinDecisionClientId = -1;
                    if (activeDialog != null && DialogsManager.Dialogs.Contains(activeDialog))
                        DialogsManager.HideDialog(activeDialog);
                    foreach (HostJoinRequest joinRequest in m_hostJoinRequests.Values.ToArray())
                        ApproveHostJoinRequest(joinRequest);
                }
                if (autoHost != ScMultiplayerSettings.AutoCreateRoomFromCurrentWorld)
                {
                    m_autoHostAttempted = false;
                    m_nextAutoHostAttemptTime = 0.0;
                }
            }

            Dictionary<string, object> result = ScMultiplayerSettings.GetJoinTransferSettings();
            result["activeJoins"] = m_worldTransferRegistry.OutgoingTransfers.Count + m_joinCatchUpRegistry.Pending.Count;
            result["connectedClients"] = GetConnectedRemoteClients().Count;
            result["gameplayTxKbps"] = m_joinTransferGameplayBytesPerSecond * 8.0 / 1000.0;
            result["joinTxKbps"] = m_joinTransferBytesPerSecond * 8.0 / 1000.0;
            result["rxKbps"] = m_joinTransferReceiveBytesPerSecond * 8.0 / 1000.0;
            result["lastUdpOutBytes"] = m_lastServerTrafficSampleBytesSent;
            result["lastUdpInBytes"] = m_lastServerTrafficSampleBytesReceived;
            result["lastUdpOutPackets"] = m_lastServerTrafficSamplePacketsSent;
            result["lastUdpInPackets"] = m_lastServerTrafficSamplePacketsReceived;
            result["joinState"] = result["activeJoins"] is int active && active > 0
                ? (m_joinTransferPausedByGameplay ? "Paused" : "Ready")
                : "idle";
            result["availableJoinKbps"] = double.IsPositiveInfinity(
                m_joinTransferAvailableBytesPerSecond) ? 0.0 :
                m_joinTransferAvailableBytesPerSecond * 8.0 / 1000.0;
            result["joinChunksPerTick"] = GetJoinTransferSendBudget(
                m_networkPlayerData.Any(item => item.Key > 0));
            result["joinWindowPackets"] = m_worldTransferRegistry.OutgoingTransfers.Count > 0
                ? GetReliableBulkUnackedPacketLimit(m_worldTransferRegistry.OutgoingTransfers.Keys.First())
                : MaximumWorldTransferUnackedPackets - ReliableCriticalReservePackets;
            result["pausedByGameplay"] = m_joinTransferPausedByGameplay;
            return new object[] { result };
        }

        // Source: Survivalcraft/Game/GameLoadingScreen.cs:GameLoadingScreen.Enter
        // Source: ScMultiplayer.CreateRoomFromCurrentWorld
        private void UpdateAutoHostCurrentWorld()
        {
            if (!ScMultiplayerSettings.AutoCreateRoomFromCurrentWorld)
            {
                m_autoHostProject = null;
                m_autoHostAttempted = false;
                return;
            }

            Project project = GameManager.Project;
            if (project == null)
                return;
            if (!ReferenceEquals(m_autoHostProject, project))
            {
                m_autoHostProject = project;
                m_autoHostAttempted = false;
                m_nextAutoHostAttemptTime = 0.0;
            }
            if (client?.IsConnected == true || client?.IsConnecting == true || m_createRoomPending ||
                ScreensManager.IsAnimating || ScreensManager.CurrentScreen is not GameScreen ||
                (m_autoHostAttempted && Time.RealTime < m_nextAutoHostAttemptTime))
            {
                return;
            }

            m_autoHostAttempted = true;
            m_nextAutoHostAttemptTime = Time.RealTime + 10.0;
            try
            {
                SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
                m_createRoomPending = true;
                CreateRoomFromCurrentWorld(gameInfo);
            }
            catch (Exception ex)
            {
                m_createRoomPending = false;
                Log.Error("[ScMP] Automatic room creation failed: " + ex.Message);
            }
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        // Called once after native world updates, independently of SubsystemUpdate.UpdatesPerFrame.
        public void UpdateWorldSubsystem(float dt, Project project)
        {
            if (project == null || !ReferenceEquals(GameManager.Project, project)) return;
            // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.UpdatesPerFrame
            // Multiple logical catch-up steps can run in one rendered frame. Real-time network
            // collection and presentation must execute only once for that frame.
            if (m_lastWorldUpdateFrameIndex == Time.FrameIndex) return;
            m_lastWorldUpdateFrameIndex = Time.FrameIndex;
            MaintainMultiplayerTimeFlow(project);
            ObserveLocalPlayerRespawn(project);
            SynchronizeLocalProfileIfChanged(project);
            if (!ReferenceEquals(m_frameProject, project))
            {
                DetachHostSleepWakeHandlers();
                DetachHostPickableEvents();
                m_frameProject = project;
                m_hasObservedClientHealth = false;
                m_lastAuthoritativeLocalWholeLevel = -1;
                m_lastSentAuthoritativePlayerStates.Clear();
                m_authoritativePlayerStateSequences.Clear();
                m_lastReceivedAuthoritativePlayerStateSequences.Clear();
                m_hasAuthoritativeLocalInventory = false;
                m_lastAuthoritativeLocalInventoryTick = 0;
                m_authoritativeLocalSlotValues = Array.Empty<int>();
                m_authoritativeLocalSlotCounts = Array.Empty<int>();
                m_containerStates.Clear();
                m_forceContainerFullSync = IsHost;
                m_pendingContainerTransactions.Clear();
                m_processedContainerTransactions.Clear();
                m_wasNetworkContainerOpen = false;
                m_openContainerPanel = null;
                m_openContainer = null;
                m_baselineRequestedContainerKey = null;
                m_remoteProjectiles.Clear();
                m_hostProjectileIds.Clear();
                m_hostProjectileReleaseCompensationSteps.Clear();
                m_clientPredictedProjectiles.Clear();
                m_displayedProjectileHits.Clear();
                m_receivedDamageSequences.Clear();
                m_localMeleePredictions.Clear();
                m_remoteDigPresentations.Clear();
                m_pendingTerrainPredictions.Clear();
                m_pendingTerrainPredictionCells.Clear();
                m_processedTerrainDigRequests.Clear();
                m_localTerrainDigIntents.Clear();
                m_localTerrainUsePredictions.Clear();
                m_pendingTerrainPlacePredictions.Clear();
                m_pendingTerrainPlacePredictionCells.Clear();
                m_localCollapsingPlacePredictions.Clear();
                m_hostTerrainPlaceExecutions.Clear();
                m_hostMeleeHitExecutions.Clear();
                m_hostTerrainPlaceFallbacks.Clear();
                m_processedTerrainPlaceRequests.Clear();
                m_hostPlayerPokingPhases.Clear();
                m_hostPlayerPokeSequences.Clear();
                m_playerWhistleSequences.Clear();
                m_equipmentAuthorityRevisions.Clear();
                m_lastClientEquipmentRevisions.Clear();
                m_lastReceivedEquipmentRevisions.Clear();
                m_lastEquipmentSnapshots.Clear();
                m_recentLocalEquipmentSnapshots.Clear();
                m_equipmentSynchronizedClients.Clear();
                m_localEquipmentRevision = 0;
                m_nextContainerRequestId = 0;
                m_disabledClientContainerUpdates.Clear();
                if (!IsHost) m_isLoadingDownloadedWorld = false;
                if (!IsHost)
                    SuSubsystemTerrain.ConfigureTerrainSequence(
                        m_pendingTerrainSequenceBaseline);
                ApplyHostRandomStates(project);
                ApplyNetworkWorldTexture(project);
                foreach (var pending in m_pendingNetworkPlayers.ToArray())
                {
                    m_pendingNetworkPlayerIdentities.TryGetValue(pending.Key, out string identity);
                    CreateNetworkPlayer(pending.Key, pending.Value, identity);
                }
                if (!IsHost && m_shouldCreateHostAvatar && !m_networkPlayerData.ContainsKey(0))
                    CreateNetworkPlayer(0, "Host", PlayerRecordKeyResolver.GetNetworkRecordKey(0));
                if (!IsHost && m_worldTransferRegistry.PendingWorldReadyTransferId > 0 &&
                    (!ReferenceEquals(m_projectReadySentProject, project) ||
                    m_projectReadySentTransferId != m_worldTransferRegistry.PendingWorldReadyTransferId))
                {
                    // Source: ScMultiplayer.ReplaceLocalPlayerData
                    // Rebinding the same Project after replacing PlayerData must not restart the
                    // join barrier or resend the entire host catch-up batch.
                    m_projectReadySentProject = project;
                    m_projectReadySentTransferId = m_worldTransferRegistry.PendingWorldReadyTransferId;
                    RecordClientJoinProgress();
                    SendClientJoinReadyStage(GamePakWorldReadyStage.ProjectReady);
                    Log.Information($"[ScMP] Client project ready: Transfer={m_worldTransferRegistry.PendingWorldReadyTransferId}");
                    if (m_joinRoomBusyDialog != null)
                        m_joinRoomBusyDialog.SmallMessage =
                            "Connected.\r\nWorld loaded.\r\nApplying host changes...";
                }
                AttachHostPickableEvents(project);
                QueueRunawayCreatureCleanup(project);
                m_nextRunawayCreatureCheckTime = Time.RealTime + 2.0;
                Log.Information("[ScMP] Multiplayer project runtime initialized");
            }
            if (IsHost)
                EnsureHostSleepWakeHandlers(project);
            if (IsHost)
                MaintainHostSleepAccelerationSession(project);
            ApplyNetworkWorldTexture(project);
            SanitizeRunawayCreatureState(project);
            ProcessRunawayCreatureCleanup(project);
            if (IsHost)
            {
                UpdateHostTerrainPlaceFallbacks(project);
                CompleteHostTerrainPlaceExecutions();
                CompleteHostMeleeHitExecutions();
                BroadcastHostPlayerPokes();
                CaptureHostRemoteKnockbacks();
                ApplyHostRemoteFollowVelocities();
            }
            else
            {
                if (m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
                    SuppressClientJoinWeatherPresentation(project);
                else
                {
                    SuppressClientRandomLightning(project);
                    UpdateRemoteFogPresentation(dt);
                }
                UpdateRemoteAnimalPresentations(dt);
                UpdateRemoteMountPresentations(dt);
                UpdateRemotePickablePresentations(dt);
                UpdateRemotePlayerPresentations(dt);
                UpdateRemoteDigPresentations();
                UpdatePendingTerrainPredictions();
            }
            MaintainRemoteTerrainLocations(project);
            UpdateClientTerrainChunkSync(project);

            // Source: Engine/Time.cs:Time.RealTime
            // Real time avoids duplicate network time when SubsystemUpdate runs multiple game
            // updates in one rendered frame. The accumulator preserves fractional 32Hz pulses.
            double now = Time.RealTime;
            if (m_lastSyncUpdateTime <= 0.0)
                m_lastSyncUpdateTime = now;
            float elapsed = (float)MathUtils.Clamp(
                now - m_lastSyncUpdateTime, 0.0, SyncPulseDuration * MaxSyncPulsesPerUpdate);
            m_lastSyncUpdateTime = now;
            m_syncPulseAccumulator += elapsed;

            int ticks = 0;
            while (m_syncPulseAccumulator >= SyncPulseDuration && ticks < MaxSyncPulsesPerUpdate)
            {
                m_syncPulseAccumulator -= SyncPulseDuration;
                TriggerNetworkTick(SyncPulseDuration);
                ticks++;
            }
            if (ticks == MaxSyncPulsesPerUpdate && m_syncPulseAccumulator >= SyncPulseDuration)
                m_syncPulseAccumulator = 0f;

        }

        // Source: ScMultiplayer.Update keyboard J flow
        // Source: ConsoleMod.ConsoleSubsystemGameWidgets.Update touch-button command pattern
        public void ShowCreateRoomDialog()
        {
            var gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo == null || server == null)
            {
                DialogsManager.ShowDialog(null, new MessageDialog("Network", "No local server or world is available.", "OK", null, null));
                return;
            }
            if (m_createRoomPending)
                return;
            if (client?.IsConnected == true)
                return;

            // Source: Survivalcraft/Game/GameManager.cs:GameManager.DisposeProject
            // Keep a confirmation against accidental CR taps. After confirmation, the visible
            // save/unload/reload transition replaces busy and success dialogs.
            DialogsManager.ShowDialog(null,
                new MessageDialog("Create Room", gameInfo.WorldSettings.Name,
                    "Create", "Cancel", delegate (MessageDialogButton button)
                    {
                        if (button != MessageDialogButton.Button1) return;
                        try
                        {
                            m_createRoomPending = true;
                            CreateRoomFromCurrentWorld(gameInfo);
                        }
                        catch (Exception ex)
                        {
                            FinishCreateRoomFeedback(false, ex.Message);
                        }
                    }));
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        // Source: Mod/WeatherTips/Subsystem/SuSubsystemWeather.cs:SuSubsystemWeather.Update
        public void ShowTalkDialog()
        {
            if (client == null || !client.IsConnected)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Talk", "Join or create a room before sending messages.", "OK", null, null));
                return;
            }

            // Source: Engine/Engine/Input/Keyboard.cs:Keyboard.KeyPressHandler
            // Windows IMEs commit a candidate as multiple characters in one frame.
            TextBoxDialog dialog = OperatingSystem.IsWindows()
                ? new WindowsTalkDialog("Talk", "", 125, SendTalkMessage)
                : new TextBoxDialog("Talk", "", 125, SendTalkMessage);

            void SendTalkMessage(string text)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ChatMessage message = NetworkMessageSender.SendChatMessage(
                        GetLocalPlayerName(), GetLocalPlayerIdentity(), text.Trim());
                    DisplayChatMessage(message, client.ClientID);
                }
            }
            BitmapFont chatFont = MultiplayerChineseFont.TextInputFont ??
                MultiplayerChineseFont.Font;
            Widget textBox = dialog.Children.Find("TextBoxDialog.TextBox", false);
            if (chatFont != null && textBox != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    textBox, "Font", chatFont, textBox.GetType());
                ModManager.ModParentField.ModifyParentField(
                    textBox, "TextureLinearFilter", true, textBox.GetType());
            }
            // Source: Survivalcraft/Game/SubsystemSignBlockBehavior.cs:
            // SubsystemSignBlockBehavior.OnInteract
            // Attach text input to the local player's GuiWidget like the stock sign editor.
            // ComponentInput then sees the modal dialog and suppresses gameplay keys while typing.
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                player != null && !m_networkPlayerData.Values.Contains(player.PlayerData));
            DialogsManager.ShowDialog(localPlayer?.GuiWidget ?? ScreensManager.RootWidget, dialog);
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.ComponentPlayers
        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Position
        public void ShowJoinedPlayerInformation()
        {
            if (!IsInRoom || GameManager.Project == null)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Joined Players", "Join or create a room first.", "OK", null, null));
                return;
            }

            var entries = new List<JoinedPlayerInformation>();
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                player?.PlayerData != null && !m_networkPlayerData.Values.Contains(player.PlayerData));
            Vector3 referencePosition = localPlayer?.ComponentBody?.Position ?? Vector3.Zero;
            Vector3 referenceViewDirection = GetJoinedPlayerViewDirection(localPlayer);
            if (players != null)
            {
                foreach (ComponentPlayer componentPlayer in players.ComponentPlayers.Where(player =>
                    player?.PlayerData != null &&
                    !m_networkPlayerData.Values.Contains(player.PlayerData)))
                {
                    bool isSelf = ReferenceEquals(componentPlayer, localPlayer);
                    entries.Add(CreateJoinedPlayerInformation(
                        componentPlayer.PlayerData.Name,
                        IsHost ? "Host" : "Client",
                        componentPlayer.ComponentBody.Position,
                        isSelf,
                        referencePosition,
                        referenceViewDirection));
                }
            }
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.OrderBy(pair => pair.Key))
            {
                PlayerData playerData = item.Value;
                Vector3 position = playerData?.ComponentPlayer?.ComponentBody?.Position ??
                    playerData?.SpawnPosition ?? Vector3.Zero;
                string role = item.Key == 0 ? "Host" : $"Client {item.Key}";
                entries.Add(CreateJoinedPlayerInformation(playerData?.Name, role, position,
                    isSelf: false, referencePosition, referenceViewDirection));
            }

            if (entries.Count == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Joined Players", "No joined player information is available.",
                    "OK", null, null));
                return;
            }
            var dialog = new ListSelectionDialog(
                "Joined Players",
                entries,
                44f,
                item => CreateJoinedPlayerInformationRow((JoinedPlayerInformation)item),
                item => { });
            float availableWidth = MathUtils.Max(
                ScreensManager.RootWidget.ActualSize.X - 40f, 0f);
            dialog.ContentSize = new Vector2(
                MathUtils.Min(800f, availableWidth), dialog.ContentSize.Y);
            // Source: Survivalcraft/Game/ListPanelWidget.cs:ListPanelWidget.ScrollPosition
            // ListSelectionDialog inherits residual touch momentum from its initial widget state.
            // A fresh IF dialog must always begin with the first player row visible.
            ListPanelWidget list = dialog.Children.Find<ListPanelWidget>(
                "ListSelectionDialog.List", true);
            if (list != null)
            {
                list.ScrollPosition = 0f;
                list.ScrollSpeed = 0f;
            }
            DialogsManager.ShowDialog(null, dialog);
        }

        // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.UpdateInputFromMouseAndKeyboard
        private static Vector3 GetJoinedPlayerViewDirection(ComponentPlayer localPlayer)
        {
            Vector3 direction = localPlayer?.GameWidget?.ActiveCamera?.ViewDirection ??
                localPlayer?.ComponentBody?.Matrix.Forward ?? Vector3.UnitZ;
            float length = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
            if (length < 0.0001f)
                return Vector3.UnitZ;
            return new Vector3(direction.X / length, 0f, direction.Z / length);
        }

        private static JoinedPlayerInformation CreateJoinedPlayerInformation(string name,
            string role, Vector3 position, bool isSelf, Vector3 referencePosition,
            Vector3 referenceViewDirection)
        {
            float deltaX = position.X - referencePosition.X;
            float deltaZ = position.Z - referencePosition.Z;
            float distance = MathF.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            return new JoinedPlayerInformation
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Player" : name,
                Role = role ?? "Client",
                Position = position,
                IsSelf = isSelf,
                Distance = distance,
                ClockDirection = isSelf ? 0 : GetClockDirection(referenceViewDirection,
                    deltaX, deltaZ, distance)
            };
        }

        // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.UpdateInputFromMouseAndKeyboard
        private static int GetClockDirection(Vector3 forward, float deltaX, float deltaZ,
            float distance)
        {
            return NetworkStatusFormatter.GetClockDirection(forward.X, forward.Z,
                deltaX, deltaZ, distance);
        }

        private static Widget CreateJoinedPlayerInformationRow(JoinedPlayerInformation entry)
        {
            float availableWidth = MathUtils.Clamp(
                (ScreensManager.RootWidget?.ActualSize.X ?? 760f) - 40f, 520f, 760f);
            float scale = availableWidth / 760f;
            var row = new CanvasWidget { Size = new Vector2(availableWidth, 44f) };
            AddJoinedPlayerColumn(row, entry.Name, 8f, 178f, scale);
            AddJoinedPlayerColumn(row, entry.Role, 186f, 92f, scale);
            AddJoinedPlayerColumn(row, string.Format(CultureInfo.InvariantCulture,
                "X {0:0.0} Y {1:0.0} Z {2:0.0}", entry.Position.X,
                entry.Position.Y, entry.Position.Z), 286f, 258f, scale);
            string relative = NetworkStatusFormatter.FormatPlayerRelative(
                entry.IsSelf, entry.Distance, entry.ClockDirection);
            AddJoinedPlayerColumn(row, relative, 552f, 200f, scale);
            return row;
        }

        private static void AddJoinedPlayerColumn(CanvasWidget row, string text, float x,
            float width, float scale)
        {
            LabelWidget label = CreateMultiplayerTextLabel(text, 0.82f * scale,
                WidgetAlignment.Near);
            label.Size = new Vector2(width * scale, 44f);
            label.TextAnchor = TextAnchor.Left;
            row.Children.Add(label);
            CanvasWidget.SetPosition(label, new Vector2(x * scale, 0f));
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.m_moreContentsWidget
        public void ShowMultiplayerManagementDialog()
        {
            if (GameManager.Project == null)
                return;

            List<ServerClient> connected = GetConnectedRemoteClients();
            var actions = new List<Tuple<string, Action>>();
            string roomState = IsHost && client?.IsConnected == true
                ? $"Hosting Room {client.GameID}"
                : client?.IsConnected == true
                    ? $"Connected to Room {client.GameID}"
                    : "No Active Room";
            actions.Add(Tuple.Create(roomState, (Action)ShowRoomStatus));
            if (client?.IsConnected != true)
                actions.Add(Tuple.Create("Create Room from Current World", (Action)ShowCreateRoomDialog));
            if (IsHost && client?.IsConnected == true)
            {
                string approval = ScMultiplayerSettings.AutoApproveJoinRequests ? "On" : "Off";
                actions.Add(Tuple.Create(
                    "Auto Approve Joins: " + approval,
                    (Action)ToggleAutoApproveJoinRequests));
                string autoHost = ScMultiplayerSettings.AutoCreateRoomFromCurrentWorld
                    ? "On"
                    : "Off";
                actions.Add(Tuple.Create(
                    "Auto Host Current World: " + autoHost,
                    (Action)ToggleAutoHostCurrentWorld));
                actions.Add(Tuple.Create(
                    "Join Bandwidth: " + FormatJoinBandwidthLimit(),
                    (Action)ShowJoinTransferSettingsDialog));
            }
            if (IsHost && client?.IsConnected == true)
            {
                actions.Add(Tuple.Create(
                    $"Connected Players ({connected.Count})",
                    (Action)ShowConnectedPlayersDialog));
                actions.Add(Tuple.Create(
                    $"Pending Join Requests ({m_hostJoinRequests.Count})",
                    (Action)ShowPendingJoinRequestsDialog));
            }
            if (client?.IsConnected == true)
                actions.Add(Tuple.Create(
                    "Circuit Synchronization",
                    (Action)SynchronizeCircuitsNow));
            if (client?.IsConnected == true)
                actions.Add(Tuple.Create(
                    $"Talk ({m_recentChatMessages.Count})",
                    (Action)ShowRecentMessagesDialog));

            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Multiplayer",
                actions,
                60f,
                item => ((Tuple<string, Action>)item).Item1,
                item => ((Tuple<string, Action>)item).Item2()));
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
        // CircuitSynchronizer.RequestManualSynchronization
        private void SynchronizeCircuitsNow()
        {
            if (client?.IsConnected != true || m_circuitSynchronizer == null)
                return;
            if (IsHost)
            {
                m_circuitSynchronizer.RequestManualSynchronization(
                    GetConnectedRemoteClients()
                        .Where(remote => remote.ClientID > 0 &&
                            !m_joinCatchUpRegistry.Journals.ContainsKey(remote.ClientID))
                        .Select(remote => remote.ClientID));
            }
            else
                m_circuitSynchronizer.RequestManualSynchronization();
        }

        private void ShowRecentMessagesDialog()
        {
            ChatMessage[] messages = m_recentChatMessages.Reverse().ToArray();
            if (messages.Length == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Talk", "No recent messages.", "OK", null, null));
                return;
            }
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Talk",
                messages,
                60f,
                item => CreateMultiplayerTextLabel(
                    FormatChatMessage((ChatMessage)item), 0.82f,
                    WidgetAlignment.Near),
                item =>
                {
                    ChatMessage message = (ChatMessage)item;
                    string sender = string.IsNullOrWhiteSpace(message.Sender)
                        ? "Player"
                        : message.Sender;
                    var messageDialog = new MessageDialog(
                        sender, message.Text, "OK", null, null);
                    ApplyChatDialogFont(messageDialog);
                    DialogsManager.ShowDialog(null, messageDialog);
                }));
        }

        private static LabelWidget CreateMultiplayerTextLabel(string text,
            float fontScale, WidgetAlignment horizontalAlignment)
        {
            return new LabelWidget
            {
                Text = text ?? string.Empty,
                Font = MultiplayerChineseFont.Font ??
                    ContentManager.Get<BitmapFont>("Fonts/Pericles18"),
                FontScale = fontScale,
                TextureLinearFilter = true,
                HorizontalAlignment = horizontalAlignment,
                VerticalAlignment = WidgetAlignment.Center,
                TextAnchor = horizontalAlignment == WidgetAlignment.Near
                    ? TextAnchor.Left
                    : TextAnchor.Center
            };
        }

        private static void ApplyChatDialogFont(MessageDialog dialog)
        {
            if (dialog == null || MultiplayerChineseFont.Font == null) return;
            LabelWidget title = dialog.Children.Find<LabelWidget>(
                "MessageDialog.LargeLabel", true);
            LabelWidget message = dialog.Children.Find<LabelWidget>(
                "MessageDialog.SmallLabel", true);
            if (title != null)
            {
                title.Font = MultiplayerChineseFont.Font;
                title.TextureLinearFilter = true;
            }
            if (message != null)
            {
                message.Font = MultiplayerChineseFont.Font;
                message.TextureLinearFilter = true;
            }
        }

        private static string FormatChatMessage(ChatMessage message)
        {
            return NetworkStatusFormatter.FormatChatMessage(message);
        }

        private void ShowRoomStatus()
        {
            string status;
            if (IsHost && client?.IsConnected == true)
            {
                status = $"Room ID: {client.GameID}\r\n" +
                    $"World: {SuPlayScreen.WorldDataName}\r\n" +
                    $"Connected players: {GetConnectedRemoteClients().Count}\r\n" +
                    $"Pending requests: {m_hostJoinRequests.Count}\r\n" +
                    "Auto approve: " +
                    (ScMultiplayerSettings.AutoApproveJoinRequests ? "On" : "Off");
            }
            else if (client?.IsConnected == true)
            {
                status = $"Connected to room {client.GameID}.";
            }
            else
            {
                status = "Create a room from the current world to begin hosting.";
            }
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Multiplayer", status, "OK", null, null));
        }

        private void ToggleAutoApproveJoinRequests()
        {
            bool value = !ScMultiplayerSettings.AutoApproveJoinRequests;
            ScMultiplayerSettings.SetAutoApproveJoinRequests(value);
            if (value)
            {
                Dialog active = m_activeJoinDecisionDialog;
                m_activeJoinDecisionDialog = null;
                m_activeJoinDecisionClientId = -1;
                if (active != null && DialogsManager.Dialogs.Contains(active))
                    DialogsManager.HideDialog(active);
                foreach (HostJoinRequest request in m_hostJoinRequests.Values.ToArray())
                    ApproveHostJoinRequest(request);
            }
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Auto Approve Joins",
                value
                    ? "New join requests will be accepted automatically."
                    : "The host must allow, reject or defer each join request.",
                "OK", null, null));
        }

        private void ToggleAutoHostCurrentWorld()
        {
            bool value = !ScMultiplayerSettings.AutoCreateRoomFromCurrentWorld;
            ScMultiplayerSettings.SetAutoCreateRoomFromCurrentWorld(value);
            m_autoHostAttempted = false;
            m_nextAutoHostAttemptTime = 0.0;
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Auto Host Current World",
                value
                    ? "A room will be created whenever a world finishes loading."
                    : "Loaded worlds will no longer be hosted automatically.",
                "OK", null, null));
        }

        // Source: ScMultiplayer.ShowMultiplayerManagementDialog
        // Keep normal hosting configuration small. Fixed join limits are available only to an
        // administrator who deliberately opens Advanced, because zero is normally optimal.
        private void ShowJoinTransferSettingsDialog()
        {
            if (!ScMultiplayerSettings.BandwidthConfigurationEnabled)
            {
                ShowAutomaticBandwidthSettingsDialog();
                return;
            }
            ShowSimpleBandwidthSettingsDialog();
        }

        private void ShowAutomaticBandwidthSettingsDialog()
        {
            var actions = new List<Tuple<string, Action>>
            {
                Tuple.Create("Bandwidth: Automatic [On]",
                    (Action)ToggleBandwidthConfiguration),
                Tuple.Create("Automatic mode information",
                    (Action)(() => DialogsManager.ShowDialog(null, new MessageDialog(
                        "Automatic bandwidth",
                        "Saved bandwidth values are not used. Join transfer increases while " +
                        "connected players stay stable and immediately slows on network " +
                        "pressure. Select the first item to configure a measured server limit.",
                        "OK", null, null))))
            };
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Bandwidth", actions, 60f,
                item => ((Tuple<string, Action>)item).Item1,
                item => ((Tuple<string, Action>)item).Item2()));
        }

        private void ToggleBandwidthConfiguration()
        {
            bool enabled = !ScMultiplayerSettings.BandwidthConfigurationEnabled;
            ScMultiplayerSettings.SetBandwidthConfigurationEnabled(enabled);
            if (enabled)
                ShowSimpleBandwidthSettingsDialog();
            else
                ShowAutomaticBandwidthSettingsDialog();
        }

        private void ShowSimpleBandwidthSettingsDialog()
        {
            var actions = new List<Tuple<string, Action>>
            {
                Tuple.Create("Bandwidth: Configured [On]",
                    (Action)ToggleBandwidthConfiguration),
                Tuple.Create("Mode: " + (ScMultiplayerSettings.BandwidthMode ==
                    BandwidthLimitMode.SharedTotal ? "Shared total" : "Separate upload / download"),
                    (Action)SelectSimpleBandwidthMode)
            };

            if (ScMultiplayerSettings.BandwidthMode == BandwidthLimitMode.SharedTotal)
            {
                actions.Add(Tuple.Create("Shared total safe cap (Kbps)[" +
                    ScMultiplayerSettings.SharedTotalSafeCapKbps + "]",
                    (Action)(() => PromptBandwidthInteger("Shared total safe cap (Kbps)",
                        "sharedTotalSafeCapKbps", ScMultiplayerSettings.SharedTotalSafeCapKbps,
                        true))));
            }
            else
            {
                actions.Add(Tuple.Create("Upload safe cap (Kbps)[" +
                    ScMultiplayerSettings.ServerUploadLimitKbps + "]",
                    (Action)(() => PromptBandwidthInteger("Upload safe cap (Kbps)",
                        "serverUploadLimitKbps", ScMultiplayerSettings.ServerUploadLimitKbps,
                        true))));
                actions.Add(Tuple.Create("Download reference (Kbps)[" +
                    ScMultiplayerSettings.ServerDownloadLimitKbps + "]",
                    (Action)(() => PromptBandwidthInteger("Download reference (Kbps)",
                        "serverDownloadLimitKbps", ScMultiplayerSettings.ServerDownloadLimitKbps,
                        true))));
            }
            actions.Add(Tuple.Create("Join capacity: Automatic [0] (up to four clients)",
                (Action)ShowAutomaticJoinCapacityInfo));
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Simple Bandwidth", actions, 60f,
                item => ((Tuple<string, Action>)item).Item1,
                item => ((Tuple<string, Action>)item).Item2()));
        }

        private void SelectSimpleBandwidthMode()
        {
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Bandwidth mode", new[] { "Shared total", "Separate upload / download" }, 60f,
                item => (string)item,
                item =>
                {
                    bool shared = string.Equals((string)item, "Shared total",
                        StringComparison.Ordinal);
                    ApplySimpleBandwidthSettings(null, 0, shared);
                    ShowSimpleBandwidthSettingsDialog();
                }));
        }

        private void PromptBandwidthInteger(string title, string setting, int value,
            bool simple)
        {
            DialogsManager.ShowDialog(null, new TextBoxDialog(title, value.ToString(
                CultureInfo.InvariantCulture), 7, text =>
            {
                if (text == null) return;
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int parsed) || parsed < 0 || parsed > 1048576)
                {
                    DialogsManager.ShowDialog(null, new MessageDialog(title,
                        "Enter a whole number from 0 to 1048576.", "OK", null, null));
                    return;
                }
                if (simple)
                    ApplySimpleBandwidthSettings(setting, parsed,
                        ScMultiplayerSettings.BandwidthMode == BandwidthLimitMode.SharedTotal);
                else
                    ScMultiplayerSettings.UpdateJoinTransferSettings(
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            [setting] = parsed
                        });
                if (simple) ShowSimpleBandwidthSettingsDialog();
                else ShowAdvancedBandwidthSettingsDialog();
            }));
        }

        private void ApplySimpleBandwidthSettings(string setting, int value, bool shared)
        {
            int cap = setting == "sharedTotalSafeCapKbps" ? value :
                shared ? ScMultiplayerSettings.SharedTotalSafeCapKbps :
                setting == "serverUploadLimitKbps" ? value :
                ScMultiplayerSettings.ServerUploadLimitKbps;
            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["bandwidthMode"] = shared ? "shared" : "separate",
                ["joinTransferMaxKbps"] = 0,
                ["joinTransferPerJoinMaxKbps"] = 0,
                ["joinTransferBurstKiB"] = 32,
                ["joinTransferGameplayHeadroomKbps"] = GetRecommendedGameplayReserve(cap)
            };
            if (!string.IsNullOrEmpty(setting)) values[setting] = value;
            ScMultiplayerSettings.UpdateJoinTransferSettings(values);
        }

        private static int GetRecommendedGameplayReserve(int safeCapKbps)
        {
            if (safeCapKbps >= 3000) return 512;
            if (safeCapKbps >= 1000) return 256;
            return 96;
        }

        private void ShowAutomaticJoinCapacityInfo()
        {
            DialogsManager.ShowDialog(null, new MessageDialog("Automatic join capacity",
                "With no fixed bandwidth value, the safe transfer scheduler shares world data " +
                "among up to four default clients and increases while the connection stays " +
                "stable. Set a shared cap in Simple setup to use a measured server limit.",
                "OK", null, null));
        }

        private void ShowAdvancedBandwidthSettingsDialog()
        {
            var actions = new List<Tuple<string, Action>>
            {
                Tuple.Create("Mode: " + (ScMultiplayerSettings.BandwidthMode ==
                    BandwidthLimitMode.SharedTotal ? "Shared total" : "Separate upload / download"),
                    (Action)SelectSimpleBandwidthMode),
                Tuple.Create("Shared total safe cap (Kbps)[" +
                    ScMultiplayerSettings.SharedTotalSafeCapKbps + "]",
                    (Action)(() => PromptBandwidthInteger("Shared total safe cap (Kbps)",
                        "sharedTotalSafeCapKbps", ScMultiplayerSettings.SharedTotalSafeCapKbps,
                        false))),
                Tuple.Create("Upload safe cap (Kbps)[" + ScMultiplayerSettings.ServerUploadLimitKbps + "]",
                    (Action)(() => PromptBandwidthInteger("Upload safe cap (Kbps)",
                        "serverUploadLimitKbps", ScMultiplayerSettings.ServerUploadLimitKbps,
                        false))),
                Tuple.Create("Download reference (Kbps)[" +
                    ScMultiplayerSettings.ServerDownloadLimitKbps + "]",
                    (Action)(() => PromptBandwidthInteger("Download reference (Kbps)",
                        "serverDownloadLimitKbps", ScMultiplayerSettings.ServerDownloadLimitKbps,
                        false))),
                Tuple.Create("Join fixed cap (Kbps)[" +
                    ScMultiplayerSettings.JoinTransferMaxKbps + "] (0 Automatic)",
                    (Action)(() => PromptBandwidthInteger("Join fixed cap (Kbps, 0 Automatic)",
                        "joinTransferMaxKbps", ScMultiplayerSettings.JoinTransferMaxKbps,
                        false))),
                Tuple.Create("Gameplay reserve (Kbps)[" +
                    ScMultiplayerSettings.JoinTransferGameplayHeadroomKbps + "]",
                    (Action)(() => PromptBandwidthInteger("Gameplay reserve (Kbps)",
                        "joinTransferGameplayHeadroomKbps",
                        ScMultiplayerSettings.JoinTransferGameplayHeadroomKbps, false))),
                Tuple.Create("Per-join fixed cap (Kbps)[" +
                    ScMultiplayerSettings.JoinTransferPerJoinMaxKbps + "] (0 Automatic)",
                    (Action)(() => PromptBandwidthInteger("Per-join fixed cap (Kbps, 0 Automatic)",
                        "joinTransferPerJoinMaxKbps",
                        ScMultiplayerSettings.JoinTransferPerJoinMaxKbps, false))),
                Tuple.Create("Join burst (KiB)[" + ScMultiplayerSettings.JoinTransferBurstKiB + "]",
                    (Action)(() => PromptBandwidthInteger("Join burst (KiB)", "joinTransferBurstKiB",
                        ScMultiplayerSettings.JoinTransferBurstKiB, false)))
            };
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Advanced Bandwidth", actions, 60f,
                item => ((Tuple<string, Action>)item).Item1,
                item => ((Tuple<string, Action>)item).Item2()));
        }

        private static string FormatJoinBandwidthLimit()
        {
            return NetworkStatusFormatter.FormatJoinBandwidthLimit(
                ScMultiplayerSettings.BandwidthConfigurationEnabled,
                ScMultiplayerSettings.BandwidthMode == BandwidthLimitMode.SharedTotal,
                ScMultiplayerSettings.SharedTotalSafeCapKbps,
                ScMultiplayerSettings.ServerUploadLimitKbps);
        }

        private void ShowPendingJoinRequestsDialog()
        {
            DeferDismissedHostJoinDecision();
            HostJoinRequest[] requests = m_hostJoinRequests.Values
                .OrderBy(item => item.ReceivedTime)
                .ToArray();
            if (requests.Length == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Pending Join Requests",
                    "No players are waiting for approval.",
                    "OK", null, null));
                return;
            }
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Pending Join Requests",
                requests,
                60f,
                item =>
                {
                    var request = (HostJoinRequest)item;
                    return GetHostJoinRequestLabel(request) +
                        (request.Deferred ? " | Later" : string.Empty);
                },
                item =>
                {
                    DeferDismissedHostJoinDecision();
                    var request = (HostJoinRequest)item;
                    request.Deferred = false;
                    ShowHostJoinDecision(request);
                }));
        }

        private void ShowConnectedPlayersDialog()
        {
            List<ServerClient> players = GetConnectedRemoteClients();
            if (players.Count == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Connected Players",
                    "No remote players are connected.",
                    "OK", null, null));
                return;
            }
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Connected Players",
                players,
                60f,
                item => GetConnectedPlayerLabel((ServerClient)item),
                item => ConfirmDisconnectPlayer((ServerClient)item)));
        }

        private void ConfirmDisconnectPlayer(ServerClient player)
        {
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Disconnect Player",
                GetConnectedPlayerLabel(player),
                "Disconnect",
                "Cancel",
                button =>
                {
                    if (button == MessageDialogButton.Button1)
                        DisconnectNetworkClient(player);
                }));
        }

        private List<ServerClient> GetConnectedRemoteClients()
        {
            if (server?.Peer == null || client == null || client.GameID < 0)
                return new List<ServerClient>();
            lock (server.Peer.Lock)
            {
                ServerGame game = server.Games.FirstOrDefault(
                    item => item.GameID == client.GameID);
                return game?.Clients
                    .Where(item => item.ClientID != client.ClientID)
                    .ToList() ?? new List<ServerClient>();
            }
        }

        private string GetConnectedPlayerLabel(ServerClient player)
        {
            string name = null;
            if (player != null &&
                m_clientRecordKeys.TryGetValue(player.ClientID, out string key) &&
                m_playerRecords.TryGetValue(key, out NetworkPlayerRecord record))
            {
                name = record.Name;
            }
            if (string.IsNullOrWhiteSpace(name))
                name = string.IsNullOrWhiteSpace(player?.ClientName) ? "Player" : player.ClientName;
            return $"{name} | Client {player?.ClientID} | {player?.Address}";
        }

        // Source: Mod/Comms/Comms/Peer.cs:Peer.DisconnectPeer
        private void DisconnectNetworkClient(ServerClient player)
        {
            if (!IsHost || player == null || server?.Peer == null)
                return;
            lock (server.Peer.Lock)
            {
                PeerData peer = server.Peer.FindPeer(player.Address);
                if (peer != null)
                    server.Peer.DisconnectPeer(peer);
            }
        }

        // Source: EntitySystem/SuAPI/IModEventBus.cs:IModEventBus.TriggerEvent
        // Only host-authoritative, low-frequency events are published for server audit storage.
        private void PublishServerAudit(string eventName, int clientId, string details)
        {
            if (!IsHost || clientId <= 0) return;
            string playerName = m_networkPlayerData.TryGetValue(clientId, out PlayerData data)
                ? data?.Name
                : null;
            m_controlUnit?.Diagnostics.TryRecord(
                Diagnostics.DiagnosticRecord.Audit(eventName, clientId, playerName, details));
        }

        // Source: Comms/Comms/Comm.cs:Comm.ProcessConnections
        private void HandleReliableRetransmit(ReliableRetransmitInfo info)
        {
            m_controlUnit?.Diagnostics.TryRecord(
                Diagnostics.DiagnosticRecord.Retransmission(info));
        }

        private static string NormalizeServerAuditValue(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            string normalized = value.Replace((char)13, ' ').Replace((char)10, ' ').Replace('"', '\'').Trim();
            return normalized.Length <= maximumLength
                ? normalized
                : normalized.Substring(0, maximumLength);
        }

        public void DisplayChatMessage(ChatMessage message, int clientId)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Text)) return;
            if (!RecordChatMessage(message)) return;
            if (IsHost && clientId > 0)
                PublishServerAudit("chat", clientId,
                    "length=" + message.Text.Length.ToString(CultureInfo.InvariantCulture));
            string identity = string.IsNullOrWhiteSpace(message.SenderIdentity)
                ? clientId.ToString()
                : message.SenderIdentity;
            int hash = 17;
            foreach (char c in identity)
                hash = unchecked(hash * 31 + c);
            Color color = ChatColors[(hash & int.MaxValue) % ChatColors.Length];
            string sender = string.IsNullOrWhiteSpace(message.Sender) ? "Player" : message.Sender;

            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;
            foreach (ComponentPlayer componentPlayer in players.ComponentPlayers)
            {
                if (m_networkPlayerData.Values.Contains(componentPlayer.PlayerData)) continue;
                componentPlayer.ComponentGui.DisplaySmallMessage(
                    sender + ": " + message.Text, color, blinking: true, playNotificationSound: true);
                ApplyLatestChatMessageFont(componentPlayer.ComponentGui);
            }
        }

        // Source: Survivalcraft/Game/MessageWidget.cs:MessageWidget.DisplayMessage
        private static void ApplyLatestChatMessageFont(ComponentGui gui)
        {
            if (gui == null || MultiplayerChineseFont.Font == null) return;
            MessageWidget messageWidget = ModManager.ModParentField
                .GetParentField<MessageWidget>(gui, "m_messageWidget", typeof(ComponentGui));
            LabelWidget label = messageWidget?.Children.LastOrDefault() as LabelWidget;
            if (label != null)
            {
                label.Font = MultiplayerChineseFont.Font;
                label.TextureLinearFilter = true;
            }
        }

        private bool RecordChatMessage(ChatMessage message)
        {
            if (message.MessageId == Guid.Empty)
                message.MessageId = Guid.NewGuid();
            if (!m_recentChatMessageIds.Add(message.MessageId))
                return false;
            m_recentChatMessages.Enqueue(message);
            while (m_recentChatMessages.Count > MaximumRecentChatMessages)
            {
                ChatMessage removed = m_recentChatMessages.Dequeue();
                m_recentChatMessageIds.Remove(removed.MessageId);
            }
            return true;
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.SaveProject
        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ExportWorld
        private void CreateRoomFromCurrentWorld(SubsystemGameInfo gameInfo)
        {
            PrepareClientForGameCreation();
            IPEndPoint localServerAddress = GetLocalServerConnectionAddress();
            if (localServerAddress == null)
                throw new InvalidOperationException("The local multiplayer server is not available.");
            string directoryName = gameInfo.DirectoryName;
            // Region files are opened exclusively while a project is running. Always save and
            // unload before export so the room snapshot represents the current running world,
            // rather than an older Play-screen cache.
            GameManager.SaveProject(waitForCompletion: true, showErrorDialog: true);
            GameManager.DisposeProject();
            WorldsManager.UpdateWorldsList();
            WorldInfo worldInfo = WorldsManager.WorldInfos.FirstOrDefault(
                world => world.DirectoryName == directoryName);
            if (worldInfo == null)
                throw new InvalidOperationException("Saved world was not found after unloading the project.");

            using (var stream = new MemoryStream())
            {
                WorldsManager.ExportWorld(worldInfo.DirectoryName, stream);
                SuPlayScreen.WorldData = stream.ToArray();
            }
            SuPlayScreen.WorldDataName = worldInfo.WorldSettings.Name;
            SuPlayScreen.WorldDataLastSaveTime = worldInfo.LastSaveTime;

            var worldMessage = new GameWorldInfoMessage(
                worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime,
                worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode,
                worldInfo.SerializationVersion, server.Address, GetLocalPlayerName(),
                GetLocalPlayerIdentity());
            IsHost = true;
            LastGameDescription = Message.WriteWithSender(worldMessage, client.Address);
            // Source: Mod/Comms/Comms.Drt/Func/Server/Server.cs:Server.Server
            // Every terminal owns a server bound to all interfaces. Connect the local client over
            // loopback; remote clients use the source endpoint returned by Explorer discovery.
            BeginLocalGameCreation(localServerAddress, LastGameDescription);

            if (GameManager.Project == null)
                ScreensManager.SwitchScreen("GameLoading", worldInfo, null);
        }

        // Source: Mod/Comms/Comms/UdpTransmitter.cs:UdpTransmitter.UdpTransmitter
        public static IPEndPoint GetLocalServerConnectionAddress()
        {
            return server == null ? null : new IPEndPoint(IPAddress.Loopback, server.Address.Port);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.Handle
        public static bool IsLocalServerEndpoint(IPEndPoint endpoint)
        {
            return endpoint != null && server != null && endpoint.Port == server.Address.Port &&
                UdpTransmitter.IsLocalIPv4Address(endpoint.Address);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.Dispose
        // A client that left through a failed VPN socket can retain an unusable Peer session.
        // Every manual remote join therefore starts with a new endpoint and handshake GUID.
        private void PrepareClientForRemoteJoin()
        {
            Client previousClient = client;
            // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.Dispose
            // Detach the endpoint before LeaveGame/Dispose can raise a synchronous final event.
            client = null;
            if (previousClient?.IsConnected == true)
            {
                try { previousClient.LeaveGame(); }
                catch (Exception ex) { Log.Warning($"[ScMP] Could not leave previous room: {ex.Message}"); }
            }
            try { previousClient?.Dispose(); }
            catch (Exception ex) { Log.Warning($"[ScMP] Could not dispose previous client: {ex.Message}"); }
            client = CreateStartedClient(RemoteConnectionLostPeriod);
            m_localLeaveInProgress = false;
            m_hostDisconnectHandled = false;
        }

        // Source: Comms/Drt/Client.cs:Client.CreateGame
        // A peer can own only one game membership. Close an existing hosted/joined session before
        // every create entry point so repeated CR clicks cannot reuse a joined Peer.
        public void PrepareClientForGameCreation()
        {
            m_pendingLocalCreateDescription = null;
            m_pendingLocalCreateAddress = null;
            m_localCreateAttempts = 0;
            m_activeJoinRequest = null;
            m_reconnectRequested = false;
            m_reconnectPending = false;
            Client previousClient = client;
            client = null;
            if (previousClient?.IsConnected == true)
            {
                foreach (int clientId in m_networkPlayerData.Keys.ToArray())
                    RemoveNetworkPlayer(clientId);
                previousClient.LeaveGame();
            }
            // Source: Mod/Comms/Comms/Comm.cs:Comm.ResetConnection
            // A local Host retry must use a fresh UDP endpoint. Reusing the previous endpoint lets
            // delayed reliable packets replace the new handshake GUID and prevents stable recovery.
            try { previousClient?.Dispose(); }
            catch (Exception ex) { Log.Error($"[ScMP] Failed to dispose previous client: {ex.Message}"); }
            client = CreateStartedClient(LocalHostConnectionLostPeriod);
            LastGameDescription = null;
            ResetTransientNetworkState();
        }

        public void BeginLocalGameCreation(IPEndPoint serverAddress, byte[] description)
        {
            if (serverAddress == null || description == null || description.Length == 0)
                throw new ArgumentException("Local game creation requires an address and description.");
            m_pendingLocalCreateAddress = serverAddress;
            m_pendingLocalCreateDescription = description.ToArray();
            m_localCreateAttempts = 1;
            m_nextLocalCreateAttemptTime = Time.RealTime + LocalCreateRetryInterval;
            client.CreateGame(serverAddress, description, client.Address.Port.ToString());
            Log.Information($"[ScMP] CreateGame attempt 1/{MaximumLocalCreateAttempts}, local={serverAddress}, advertised={server?.Address}");
        }

        private void UpdatePendingLocalGameCreation()
        {
            if (m_pendingLocalCreateDescription == null || client?.IsConnected == true ||
                Time.RealTime < m_nextLocalCreateAttemptTime)
                return;
            if (m_localCreateAttempts >= MaximumLocalCreateAttempts)
            {
                m_pendingLocalCreateDescription = null;
                m_pendingLocalCreateAddress = null;
                Dispatcher.Dispatch(() => FinishCreateRoomFeedback(false,
                    "The local multiplayer server did not respond."));
                return;
            }

            try
            {
                Client previousClient = client;
                client = null;
                try { previousClient?.Dispose(); }
                catch (Exception ex) { Log.Warning($"[ScMP] Failed to dispose create client: {ex.Message}"); }
                client = CreateStartedClient(LocalHostConnectionLostPeriod);
                Message description = Message.Read(m_pendingLocalCreateDescription);
                m_pendingLocalCreateDescription = Message.WriteWithSender(description, client.Address);
                LastGameDescription = m_pendingLocalCreateDescription;
                m_localCreateAttempts++;
                m_nextLocalCreateAttemptTime = Time.RealTime + LocalCreateRetryInterval;
                client.CreateGame(m_pendingLocalCreateAddress, m_pendingLocalCreateDescription,
                    client.Address.Port.ToString());
                Log.Information($"[ScMP] CreateGame retry {m_localCreateAttempts}/{MaximumLocalCreateAttempts}, local={m_pendingLocalCreateAddress}");
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Local CreateGame retry failed: {ex.Message}");
                m_nextLocalCreateAttemptTime = Time.RealTime + LocalCreateRetryInterval;
            }
        }

        // Source: ScMultiplayer.Update keyboard K flow
        // Source: Comms.Drt.Explorer.DiscoveredServers
        public void ShowJoinRoomDialog()
        {
            var games = explorer?.DiscoveredServers?
                .SelectMany(serverDescription => serverDescription.GameDescriptions)
                .ToList() ?? new List<GameDescription>();
            if (games.Count == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog("Network", "No rooms were found.", "OK", null, null));
                return;
            }

            DialogsManager.ShowDialog(null,
                new ListSelectionDialog("Join Room", games, 60f,
                    item =>
                    {
                        var game = (GameDescription)item;
                        var info = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage;
                        return info != null ? info.Name : game.ToString();
                    },
                    item =>
                    {
                        var game = (GameDescription)item;
                        var info = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage;
                        if (info == null) return;
                        BeginJoinGame(game.ServerDescription.Address, game.GameID, info);
                    }));
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.JoinGame
        public void BeginJoinGame(IPEndPoint serverAddress, int gameId, GameWorldInfoMessage worldInfo)
        {
            if (serverAddress == null || worldInfo == null) return;
            // Source: Mod/ScMultiplayer/Message/GameWorldInfoMessage.cs:
            // GameWorldInfoMessage.Read
            if (!Message.IsProtocolCompatible(worldInfo.MultiplayerModVersion,
                    worldInfo.MultiplayerProtocolVersion,
                    worldInfo.MultiplayerProtocolHash,
                    worldInfo.MultiplayerBuildFingerprint))
            {
                string hostProtocol = Message.GetProtocolLabel(
                    worldInfo.MultiplayerModVersion,
                    worldInfo.MultiplayerProtocolVersion,
                    worldInfo.MultiplayerProtocolHash,
                    worldInfo.MultiplayerBuildFingerprint);
                string localProtocol = Message.GetProtocolLabel(
                    Message.ModVersion,
                    Message.ProtocolVersion, Message.ProtocolHash,
                    Message.BuildFingerprint);
                Log.Warning($"[ScMP] Refused incompatible room before join: " +
                    $"host={hostProtocol}, local={localProtocol}");
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Join Room",
                    $"Multiplayer Mod protocol mismatch.\r\n" +
                    $"Host: {hostProtocol}\r\nLocal: {localProtocol}\r\n" +
                    "Install the same ScMultiplayer package on all devices.",
                    "OK", null, null));
                return;
            }
            PrepareClientForRemoteJoin();
            ShowJoinRoomBusyDialog();
            m_activeJoinRequest = new PendingJoinRequest
            {
                ServerAddress = serverAddress,
                GameId = gameId,
                WorldInfo = worldInfo
            };
            m_pendingJoinRequest = m_activeJoinRequest;
            m_activeJoinPlayerName = GetLocalPlayerName();
            m_activeJoinPlayerClass = PlayerClass.Male;
            m_activeJoinSkinName = null;
            m_activeJoinHasPlayerProfile = false;
            m_reconnectRequested = false;
            m_reconnectPending = false;
            m_reconnectAttempts = 0;
            m_reconnectAttemptDeadline = 0.0;
            SubmitPendingJoin(null, PlayerClass.Male, null, hasPlayerProfile: false);
        }

        public void CancelPendingJoin()
        {
            HideJoinRoomBusyDialog();
            m_pendingJoinRequest = null;
            m_activeJoinRequest = null;
            m_reconnectRequested = false;
            m_reconnectPending = false;
            m_reconnectAttemptDeadline = 0.0;
            m_joinAwaitingWorldProgress = false;
        }

        // Source: Survivalcraft/Game/BusyDialog.cs:BusyDialog
        private void ShowJoinRoomBusyDialog()
        {
            if (m_joinRoomBusyDialog != null) return;
            m_joinRoomBusyDialog = new BusyDialog(
                "Joining Room", "Status: Connecting to host");
            // Source: Survivalcraft/Game/BusyDialog.cs:BusyDialog.BusyDialog
            // Source: Survivalcraft/Game/LabelWidget.cs:LabelWidget.MeasureOverride
            // BusyDialog normally sizes its small label from the changing longest line. Keep a
            // stable area and use explicit semantic lines so changing counters cannot reflow or
            // move every line below them.
            LabelWidget statusLabel = m_joinRoomBusyDialog.Children.Find<LabelWidget>(
                "BusyDialog.SmallLabel", true);
            if (statusLabel != null)
            {
                statusLabel.Size = new Vector2(640f, 190f);
                statusLabel.WordWrap = false;
                statusLabel.MaxLines = 5;
            }
            DialogsManager.ShowDialog(ScreensManager.RootWidget, m_joinRoomBusyDialog);
        }

        private void HideJoinRoomBusyDialog()
        {
            if (m_joinRoomBusyDialog == null) return;
            DialogsManager.HideDialog(m_joinRoomBusyDialog);
            m_joinRoomBusyDialog = null;
        }

        private void UpdateWorldTransferBusyStatus()
        {
            if (m_joinRoomBusyDialog == null || IsHost ||
                (!m_isLoadingDownloadedWorld && m_worldTransferRegistry.PendingWorldReadyTransferId <= 0) ||
                Time.RealTime < m_nextWorldTransferUiUpdateTime)
                return;
            m_nextWorldTransferUiUpdateTime = Time.RealTime + 0.25;
            string countdown = GetClientJoinCountdownText();
            if (m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
            {
                bool projectReady = m_projectReadySentTransferId ==
                    m_worldTransferRegistry.PendingWorldReadyTransferId;
                SetJoinRoomBusyStatus(
                    "Connection: Connected",
                    projectReady ? "World: Loaded" : "World: Imported",
                    projectReady ? "Status: Applying host changes" : "Status: Loading project",
                    "Circuit: " + (m_circuitSynchronizer?.IsClientBootstrapReady == true
                        ? "Ready" : "Synchronizing"),
                    countdown);
                return;
            }
            IncomingWorldTransfer transfer = m_worldTransferRegistry.IncomingTransfers.Values
                .OrderByDescending(item => item.TransferId).FirstOrDefault();
            if (transfer == null)
            {
                SetJoinRoomBusyStatus(
                    "Connection: Connected",
                    "Status: Waiting for host",
                    "Data: World manifest",
                    countdown);
                return;
            }
            double percent = transfer.Chunks.Length > 0
                ? 100.0 * transfer.ReceivedChunkCount / transfer.Chunks.Length
                : 0.0;
            string stage = transfer.RepairRequestCount > 0 &&
                Time.RealTime - transfer.LastProgressTime >= WorldTransferRepairInterval
                    ? $"Status: Recovering chunks (request {transfer.RepairRequestCount})"
                    : "Status: Downloading world";
            SetJoinRoomBusyStatus(
                "Connection: Connected",
                stage,
                string.Format(CultureInfo.InvariantCulture,
                    "Chunks: {0} / {1} ({2:0.0}%)",
                    transfer.ReceivedChunkCount, transfer.Chunks.Length, percent),
                string.Format(CultureInfo.InvariantCulture,
                    "Data: {0:0.00} / {1:0.00} MB",
                    transfer.ReceivedBytes / 1048576.0,
                    transfer.TotalLength / 1048576.0),
                countdown);
        }

        private void SetJoinRoomBusyStatus(params string[] lines)
        {
            if (m_joinRoomBusyDialog == null) return;
            // Source: Survivalcraft/Game/LabelWidget.cs:LabelWidget.UpdateLines
            // LabelWidget splits explicit lines only on LF. CR remains ordinary text and makes
            // the entire status appear on one line when word wrapping is disabled.
            m_joinRoomBusyDialog.SmallMessage = string.Join("\r\n",
                (lines ?? Array.Empty<string>()).Where(line =>
                    !string.IsNullOrWhiteSpace(line)));
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.UpdateJoinWorldProgressTimeout
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.UpdateClientJoinBarrier
        private void RecordClientJoinProgress()
        {
            double now = Time.RealTime;
            m_lastJoinWorldProgressTime = now;
            m_lastClientJoinBarrierProgressTime = now;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
        // CircuitSynchronizer.HandleSnapshot
        internal void RecordCircuitJoinProgress()
        {
            if (!IsHost && m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
                RecordClientJoinProgress();
        }

        private string GetClientJoinCountdownText()
        {
            if (m_reconnectPending && m_reconnectAttemptDeadline > 0.0)
            {
                int reconnectSeconds = Math.Max(0, (int)Math.Ceiling(
                    m_reconnectAttemptDeadline - Time.RealTime));
                return $"Reconnect: {m_reconnectAttempts} / {MaxReconnectAttempts} | " +
                    $"Timeout: {reconnectSeconds}s";
            }
            double lastProgressTime = Math.Max(m_lastJoinWorldProgressTime,
                m_lastClientJoinBarrierProgressTime);
            if (lastProgressTime <= 0.0) return string.Empty;
            int seconds = JoinReadyPolicy.GetRemainingSeconds(Time.RealTime,
                lastProgressTime, JoinBarrierNoProgressTimeout);
            return $"No-progress timeout: {seconds}s";
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.HandleGamePakWorldMessage
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.HandleGamePakWorldChunkMessage
        private void UpdateJoinWorldProgressTimeout()
        {
            if (!m_joinAwaitingWorldProgress || IsHost || m_hostDisconnectHandled ||
                !m_isLoadingDownloadedWorld)
                return;
            double stalledTime = Time.RealTime - m_lastJoinWorldProgressTime;
            if (!JoinReadyPolicy.HasTimedOut(Time.RealTime,
                m_lastJoinWorldProgressTime, JoinWorldNoProgressTimeout)) return;

            m_joinAwaitingWorldProgress = false;
            Log.Error($"[ScMP] Join world transfer stopped making progress for " +
                $"{stalledTime:0.0} seconds");
            HandleHostDisconnected();
        }

        // Source: ScMultiplayer.HandleGamePakWorldReadyMessage
        private void UpdateClientJoinBarrier()
        {
            if (IsHost || m_worldTransferRegistry.PendingWorldReadyTransferId <= 0 ||
                m_reconnectRequested || m_reconnectPending || client?.IsConnected != true)
                return;

            double now = Time.RealTime;
            if (client?.IsConnected == true)
                TryAcknowledgeClientCatchUpApplied();
            if (JoinReadyPolicy.HasTimedOut(now,
                m_lastClientJoinBarrierProgressTime, JoinBarrierNoProgressTimeout))
            {
                Log.Error($"[ScMP] Join barrier timed out: " +
                    $"Transfer={m_worldTransferRegistry.PendingWorldReadyTransferId}, Stage={ClientJoinReadyStage}");
                m_nextClientJoinReadyRetryTime = 0.0;
                HandleHostDisconnected();
                return;
            }

            if (client?.IsConnected == true && JoinReadyPolicy.IsRetryDue(now,
                m_nextClientJoinReadyRetryTime))
                SendClientJoinReadyStage(ClientJoinReadyStage);
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
        // CircuitSynchronizer.IsClientBootstrapReady
        private void TryAcknowledgeClientCatchUpApplied()
        {
            if (m_worldTransferRegistry.PendingCircuitReadyTransferId <= 0 ||
                m_worldTransferRegistry.PendingCircuitReadyTransferId != m_worldTransferRegistry.PendingWorldReadyTransferId ||
                m_circuitSynchronizer?.IsClientBootstrapReady != true)
                return;
            int transferId = m_worldTransferRegistry.PendingCircuitReadyTransferId;
            m_worldTransferRegistry.PendingCircuitReadyTransferId = 0;
            RecordClientJoinProgress();
            SendClientJoinReadyStage(GamePakWorldReadyStage.CatchUpBatchApplied);
            Log.Information($"[ScMP] Client circuit bootstrap complete: Transfer={transferId}");
        }

        private void SendClientJoinReadyStage(GamePakWorldReadyStage stage)
        {
            if (m_worldTransferRegistry.PendingWorldReadyTransferId <= 0 || client?.IsConnected != true) return;
            ClientJoinReadyStage = stage;
            m_nextClientJoinReadyRetryTime = JoinReadyPolicy.ScheduleRetry(
                Time.RealTime, JoinBarrierRetryInterval);
            NetworkMessageSender.SendPakWorldReady(new GamePakWorldReadyMessage(
                m_worldTransferRegistry.PendingWorldReadyTransferId, stage));
        }

        private void SubmitPendingJoin(string playerName, PlayerClass playerClass,
            string skinName, bool hasPlayerProfile)
        {
            PendingJoinRequest pending = m_pendingJoinRequest;
            if (pending?.WorldInfo == null) return;
            ShowJoinRoomBusyDialog();
            if (hasPlayerProfile)
            {
                m_activeJoinPlayerName = string.IsNullOrWhiteSpace(playerName)
                    ? GetLocalPlayerName()
                    : playerName;
                m_activeJoinPlayerClass = playerClass;
                m_activeJoinSkinName = skinName;
                m_activeJoinHasPlayerProfile = true;
            }
            IsHost = false;
            if (client.IsConnected) client.LeaveGame();
            GameWorldInfoMessage info = pending.WorldInfo;
            byte[] skinSha256 = hasPlayerProfile
                ? GetLocalCharacterSkinSha256(skinName)
                : Array.Empty<byte>();
            var joinInfo = new GameWorldInfoMessage(
                info.Name, info.Size, info.LastSaveTime, info.GameMode,
                info.EnvironmentBehaviorMode, info.SerializationVersion, client.Address,
                hasPlayerProfile ? playerName : GetLocalPlayerName(), GetLocalPlayerIdentity(),
                hasPlayerProfile, playerClass, skinName);
            joinInfo.CharacterSkinSha256 = skinSha256;
            client.JoinGame(pending.ServerAddress, pending.GameId,
                Message.WriteWithSender(joinInfo, client.Address), client.Address.Port.ToString());
        }

        private void TryKickPlayer()
        {
            // 踢出最后一个加入的非房主玩家
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            var allPlayers = subsystemPlayers.ComponentPlayers;

            int hostPlayerIndex = playerMappingManager.GetPlayerIndex(0);
            ComponentPlayer target = null;
            foreach (var p in allPlayers)
            {
                if (p.PlayerData.PlayerIndex != hostPlayerIndex)
                {
                    target = p;
                    break;
                }
            }

            if (target == null) { Log.Information("[ScMP] No players to kick"); return; }

            int targetClientID = playerMappingManager.GetClientId(target.PlayerData.PlayerIndex);
            if (targetClientID <= 0) { Log.Information("[ScMP] Cannot kick player with invalid client ID"); return; }

            Log.Information($"[ScMP] Kicking player ClientID={targetClientID}");
            NetworkMessageSender.SendKickPlayerMessage(targetClientID, "Kicked by host");
        }

        // ====================================================================
        // 32Hz 定时事件
        // ====================================================================
        private void TriggerNetworkTick(float tickDuration)
        {
            // Source: ScMultiplayer.ScMultiplayer.Update
            // Real-time messages share one aligned 1/2/4/8/16/32Hz phase ladder.
            if (!client.IsConnected) return;
            uint pulse = m_syncPulseIndex++;
            bool pulse1Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz1);
            bool pulse2Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz2);
            bool pulse4Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz4);
            bool pulse8Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz8);
            bool pulse16Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz16);
            bool sleepAcceleration = IsSleepAccelerationActive(GameManager.Project);
            // Source: CircuitSynchronizer.SendFence
            // Refresh the 200ms execution window at 16Hz, leaving overlapping authorized time
            // without sending per-step circuit state.
            m_circuitSynchronizer?.PublishNetworkState(pulse1Hz, pulse16Hz);
            if (IsHost && pulse1Hz)
            {
                lock (m_terrainJournalLock)
                    TrimHostTerrainJournalLocked(Time.RealTime);
            }
            if (IsHost)
            {
                EnsureHostTerrainSyncStateLoaded();
                UpdateJoinTransferBandwidthBudget();
                UpdateServerTrafficDisplaySample();
                ConfirmPendingFluidSettlements();
                FlushPendingTerrainBroadcasts();
            }
            // Source: ScMultiplayer.cs:AcceptNetworkPlayerJoin
            // A joining client intentionally has no host-side avatar until it reports that the
            // Loading Project screen started. World chunks must therefore run before the
            // no-remote-avatar maintenance fast path.
            if (IsHost) SendPendingWorldTransferChunks();
            if (IsHost) SendPendingJoinCatchUps();
            if (IsHost) MaintainHostLightningLifecycle();
            if (IsHost && !m_networkPlayerData.Any(item => item.Key > 0))
            {
                // Source: ScMultiplayer.cs:ScMultiplayer.TriggerNetworkTick
                // With no remote recipients, world-object scans and serialization cannot produce
                // useful network state. Keep only persistent host maintenance until a join creates
                // its authoritative avatar, at which point normal synchronization resumes.
                m_hostLightningActive = false;
                m_playerRecordSaveTime += tickDuration;
                if (m_playerRecordSaveTime >= PlayerRecordSaveInterval)
                {
                    m_playerRecordSaveTime -= PlayerRecordSaveInterval;
                    RefreshHostPlayerRecords();
                    SavePlayerRecords();
                    SaveHostTerrainSyncState();
                }
                m_terrainMergeTime += tickDuration;
                if (m_terrainMergeTime >= TerrainMergeInterval)
                {
                    m_terrainMergeTime -= TerrainMergeInterval;
                    MergePendingTerrainChanges();
                }
                return;
            }
            NetworkMessageSender.BeginSyncBatch();
            try
            {
            if (IsHost) SendHostLightningEdge();

            m_inventoryKeyframeTime += tickDuration;
            bool inventoryKeyframe = pulse1Hz && m_inventoryKeyframeTime >= 5f;
            if (inventoryKeyframe) m_inventoryKeyframeTime -= 5f;
            bool forceHostInventorySync = IsHost && m_forceHostInventorySync;
            if (!sleepAcceleration || pulse1Hz || forceHostInventorySync)
                SendGamePlayerPositionMessage(
                    pulse1Hz || forceHostInventorySync,
                    inventoryKeyframe || forceHostInventorySync);
            if (forceHostInventorySync) m_forceHostInventorySync = false;
            bool networkContainerOpen = !IsHost && IsNetworkContainerOpen();
            bool synchronizedClientContainers = !IsHost &&
                (networkContainerOpen || m_wasNetworkContainerOpen ||
                m_pendingContainerTransactions.Count > 0);
            if (synchronizedClientContainers) SynchronizeContainers();
            m_wasNetworkContainerOpen = networkContainerOpen;
            SynchronizePlayerEquipment();
            if (IsHost)
                SendGamePlayerHealthMessage(false);
            else
                SendClientDamageRequest();

            // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
            // Sleeping clients stay at one logical update per rendered frame and do not follow
            // every accelerated host clock sample. Reliable acceleration edges delimit a single
            // final time/circuit rebase; the ordinary 2Hz state remains weather authority.
            if (pulse2Hz)
                SendGameWorldInfoMessage();

            if (pulse1Hz)
                SynchronizeEditableData();
            if (pulse1Hz)
                if (IsHost) SendGamePlayerHealthMessage(true);
            if (IsHost)
            {
                m_playerRecordSaveTime += tickDuration;
                if (m_playerRecordSaveTime >= PlayerRecordSaveInterval)
                {
                    m_playerRecordSaveTime -= PlayerRecordSaveInterval;
                    RefreshHostPlayerRecords();
                    SavePlayerRecords();
                    SaveHostTerrainSyncState();
                }
                m_terrainMergeTime += tickDuration;
                if (m_terrainMergeTime >= TerrainMergeInterval)
                {
                    m_terrainMergeTime -= TerrainMergeInterval;
                    MergePendingTerrainChanges();
                }
            }
            m_fullWorldObjectsSyncTime += tickDuration;
            if (pulse8Hz && (!sleepAcceleration || pulse2Hz))
            {
                bool fullSync = m_fullWorldObjectsSyncTime >= WorldObjectFullSyncInterval;
                if (fullSync) m_fullWorldObjectsSyncTime -= WorldObjectFullSyncInterval;
                if (IsHost) SendWorldObjects(fullSync);
                else QueueEndOfFrameAction(MaintainClientWorldObjects);
                if (IsHost) SendMountUpdates();
            }
            m_fullAnimalSyncTime += tickDuration;
            if (IsHost && pulse16Hz)
            {
                bool fullSnapshot = m_fullAnimalSyncTime >= 5f;
                if (fullSnapshot) m_fullAnimalSyncTime -= 5f;
                SendAdaptiveAnimalUpdates(fullSnapshot);
            }
            if (!sleepAcceleration || pulse4Hz)
                SynchronizeProjectiles();
            if (pulse4Hz && !synchronizedClientContainers) SynchronizeContainers();
            }
            finally
            {
                NetworkMessageSender.FlushSyncBatch();
            }
        }

        // Source: ScMultiplayer.cs:TriggerNetworkTick
        // Every lower tier divides the same 32Hz phase, so all tiers coincide once per second.
        private static bool IsSyncPulse(uint pulse, NetworkSyncRate rate)
        {
            int divider = SyncBaseRate / (int)rate;
            return pulse % (uint)divider == 0u;
        }

        // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
        // The host exposes the authoritative accelerated-time state; clients use that state only
        // to reduce replaceable presentation traffic and never to run a second 20x simulation.
        private bool IsSleepAccelerationActive(Project project)
        {
            if (project == null || client?.IsConnected != true)
                return false;
            if (IsHost)
                return project.FindSubsystem<SubsystemTime>(false)?.FixedTimeStep.HasValue == true;
            return m_remoteTimeAccelerated;
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        // Frame.Update runs after ScreensManager.Update, outside SubsystemUpdate.Update enumeration.
        private void QueueEndOfFrameAction(Action action)
        {
            QueueEndOfFrameAction(default, action);
        }

        private void QueueEndOfFrameAction(in NetworkIngressCommand command, Action action)
        {
            if (action == null) return;
            // Source: ScMultiplayer.cs:ScMultiplayer.ReadNetworkStats
            // Timestamp queued work so NET distinguishes current-frame traffic from backlog.
            long enqueuedTimestamp = Stopwatch.GetTimestamp();
            NetworkIngressCommand queuedCommand = command.IsValid
                ? command.WithQueue(NetworkIngressQueueKind.EndOfFrame, enqueuedTimestamp)
                : default;
            m_controlUnit?.Context.IngressDiagnostics.RecordEnqueue(in queuedCommand);
            m_endOfFrameActions.Enqueue(new QueuedFrameAction
            {
                Action = action,
                EnqueuedTimestamp = enqueuedTimestamp,
                Command = queuedCommand
            });
        }

        // Source: ScMultiplayer.cs:ProcessEndOfFrameActions
        private void QueuePriorityInputAction(Action action)
        {
            QueuePriorityInputAction(default, action);
        }

        private void QueuePriorityInputAction(in NetworkIngressCommand command, Action action)
        {
            if (action == null) return;
            long enqueuedTimestamp = Stopwatch.GetTimestamp();
            NetworkIngressCommand queuedCommand = command.IsValid
                ? command.WithQueue(NetworkIngressQueueKind.PriorityInput, enqueuedTimestamp)
                : default;
            m_controlUnit?.Context.IngressDiagnostics.RecordEnqueue(in queuedCommand);
            m_priorityInputActions.Enqueue(new QueuedFrameAction
            {
                Action = action,
                EnqueuedTimestamp = enqueuedTimestamp,
                Command = queuedCommand
            });
        }

        // Source: ScMultiplayer.cs:QueueEndOfFrameAction
        private void QueueWorldTransferAction(Action action)
        {
            QueueWorldTransferAction(default, action);
        }

        private void QueueWorldTransferAction(in NetworkIngressCommand command, Action action)
        {
            if (action == null) return;
            long enqueuedTimestamp = Stopwatch.GetTimestamp();
            NetworkIngressCommand queuedCommand = command.IsValid
                ? command.WithQueue(NetworkIngressQueueKind.WorldTransfer, enqueuedTimestamp)
                : default;
            m_controlUnit?.Context.IngressDiagnostics.RecordEnqueue(in queuedCommand);
            m_worldTransferActions.Enqueue(new QueuedFrameAction
            {
                Action = action,
                EnqueuedTimestamp = enqueuedTimestamp,
                Command = queuedCommand
            });
        }

        private void QueueTerrainChunkSyncAction(in NetworkIngressCommand command,
            TerrainChunkSyncMessage message, int sourceClientId)
        {
            if (message == null) return;
            long enqueuedTimestamp = Stopwatch.GetTimestamp();
            NetworkIngressCommand queuedCommand = command.IsValid
                ? command.WithQueue(NetworkIngressQueueKind.TerrainChunk, enqueuedTimestamp)
                : default;
            m_controlUnit?.Context.IngressDiagnostics.RecordEnqueue(in queuedCommand);
            m_terrainChunkSyncActions.Enqueue(new QueuedTerrainChunkSync
            {
                Message = message,
                SourceClientId = sourceClientId,
                EnqueuedTimestamp = enqueuedTimestamp,
                Command = queuedCommand
            });
        }

        private void DispatchIngressAction(in NetworkIngressCommand command, Action action)
        {
            if (action == null) return;
            long enqueuedTimestamp = Stopwatch.GetTimestamp();
            NetworkIngressCommand queuedCommand = command.IsValid
                ? command.WithQueue(NetworkIngressQueueKind.Dispatcher, enqueuedTimestamp)
                : default;
            m_controlUnit?.Context.IngressDiagnostics.RecordEnqueue(in queuedCommand);
            Dispatcher.Dispatch(() =>
            {
                long applyTimestamp = Stopwatch.GetTimestamp();
                m_controlUnit?.Context.IngressDiagnostics.RecordApply(
                    in queuedCommand, applyTimestamp);
                try
                {
                    action();
                    m_controlUnit?.Context.IngressDiagnostics.RecordResult(
                        in queuedCommand, applyTimestamp, Stopwatch.GetTimestamp(),
                        succeeded: true);
                }
                catch
                {
                    m_controlUnit?.Context.IngressDiagnostics.RecordResult(
                        in queuedCommand, applyTimestamp, Stopwatch.GetTimestamp(),
                        succeeded: false);
                    throw;
                }
            });
        }

        private void ExecuteQueuedIngressAction(QueuedFrameAction queuedAction,
            string failureMessage)
        {
            if (queuedAction?.Action == null) return;
            long applyTimestamp = Stopwatch.GetTimestamp();
            m_controlUnit?.Context.IngressDiagnostics.RecordApply(
                in queuedAction.Command, applyTimestamp);
            try
            {
                queuedAction.Action();
                m_controlUnit?.Context.IngressDiagnostics.RecordResult(
                    in queuedAction.Command, applyTimestamp, Stopwatch.GetTimestamp(),
                    succeeded: true);
            }
            catch (Exception ex)
            {
                m_controlUnit?.Context.IngressDiagnostics.RecordResult(
                    in queuedAction.Command, applyTimestamp, Stopwatch.GetTimestamp(),
                    succeeded: false);
                Log.Error("[ScMP] " + failureMessage + ": " + ex.Message);
            }
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.ProcessEndOfFrameActions
        // Keep the normal budget small, but let a burst of authoritative Chunk messages drain
        // faster than the 40-request/s producer. The time budget below remains the hard limit.
        private int GetTerrainChunkSyncMessagesPerFrame()
        {
            int queued = m_terrainChunkSyncActions.Count;
            if (queued <= 0) return TerrainChunkSyncBaseMessagesPerFrame;
            int extra = queued / TerrainChunkSyncQueueGrowthStep;
            return Math.Min(TerrainChunkSyncBurstMessagesPerFrame,
                TerrainChunkSyncBaseMessagesPerFrame + extra);
        }

        private void ProcessEndOfFrameActions()
        {
            // Source: Survivalcraft/Game/Program.cs:Program.Run
            // Background recovery can enqueue several seconds of network work. Preserve FIFO,
            // but leave excess work for later frames so returning to the game does not stall one
            // render frame while draining the entire queue.
            long start = Stopwatch.GetTimestamp();
            long budgetTicks = Math.Max(1L, Stopwatch.Frequency *
                EndOfFrameActionBudgetMilliseconds / 1000L);
            int count = 0;
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            // Drain one-frame player edges before bulk circuit recovery. The queue contains only
            // bounded, sequence-deduplicated actions and therefore cannot starve normal work.
            while (count < MaximumPriorityInputActionsPerFrame &&
                Stopwatch.GetTimestamp() - start < budgetTicks &&
                m_priorityInputActions.TryDequeue(out QueuedFrameAction priorityAction))
            {
                ExecuteQueuedIngressAction(priorityAction, "Priority input action failed");
                count++;
            }
            // Source: ScMultiplayer.cs:Client_GameStep
            // Drain join/download control first. LAN peers can otherwise produce gameplay actions
            // faster than the normal FIFO reaches a manifest request or response.
            while (count < MaximumEndOfFrameActionsPerFrame &&
                Stopwatch.GetTimestamp() - start < budgetTicks &&
                m_worldTransferActions.TryDequeue(out QueuedFrameAction transferAction))
            {
                ExecuteQueuedIngressAction(transferAction, "World-transfer action failed");
                count++;
            }
            // Source: ScMultiplayer.cs:ScMultiplayer.HandleTerrainChunkSyncMessage
            // Preserve Data -> Complete order while bounding checkpoint dispatch. The terrain
            // subsystem applies the cells separately across frames.
            int chunkSyncCount = 0;
            int chunkSyncBudget = GetTerrainChunkSyncMessagesPerFrame();
            while (count < MaximumEndOfFrameActionsPerFrame &&
                chunkSyncCount < chunkSyncBudget &&
                Stopwatch.GetTimestamp() - start < budgetTicks &&
                m_terrainChunkSyncActions.TryDequeue(
                    out QueuedTerrainChunkSync chunkSync))
            {
                long applyTimestamp = Stopwatch.GetTimestamp();
                m_controlUnit?.Context.IngressDiagnostics.RecordApply(
                    in chunkSync.Command, applyTimestamp);
                try
                {
                    HandleTerrainChunkSyncMessage(chunkSync.Message, chunkSync.SourceClientId);
                    m_controlUnit?.Context.IngressDiagnostics.RecordResult(
                        in chunkSync.Command, applyTimestamp, Stopwatch.GetTimestamp(),
                        succeeded: true);
                }
                catch (Exception ex)
                {
                    m_controlUnit?.Context.IngressDiagnostics.RecordResult(
                        in chunkSync.Command, applyTimestamp, Stopwatch.GetTimestamp(),
                        succeeded: false);
                    Log.Error($"[ScMP] Terrain chunk sync action failed: {ex.Message}");
                }
                chunkSyncCount++;
                count++;
            }
            while (count < MaximumEndOfFrameActionsPerFrame &&
                Stopwatch.GetTimestamp() - start < budgetTicks &&
                m_endOfFrameActions.TryDequeue(out QueuedFrameAction queuedAction))
            {
                ExecuteQueuedIngressAction(queuedAction,
                    "End-of-frame network action failed");
                count++;
            }
        }

    }
}

using Engine;
using Game;
using GameEntitySystem;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
    internal sealed class CircuitSynchronizer
    {
        private sealed class ButtonPulseState
        {
            public int PressSequence;
            public int ReleaseHostStep;
        }

        private sealed class PendingSwitchState
        {
            public bool State;
            public int LastSequence;
        }

        private const int DefaultCircuitLeadSteps = 5;
        private const int MinimumCircuitLeadSteps = 3;
        private const int MaximumCircuitLeadSteps = 30;
        private const int CircuitExecutionWindowSteps = 20;
        private const int PressureRefreshSteps = 10;
        private const int HashLeadSteps = 20;
        private const double JournalRetention = 45.0;
        private const double FenceStaleTime = 0.75;
        private const int CatchUpInputSuppressionSteps = 30;
        private const int MaximumEventsPerBatch = 40;
        private const int MaximumStatesPerSnapshot = 40;
        private const int TrackedCircuitRetentionSteps = 3000;
        private const int RepairBarrierMinimumLeadSteps = 20;

        private readonly ScMultiplayer m_owner;
        private readonly SortedDictionary<int, CircuitEventRecord> m_receivedEvents =
            new SortedDictionary<int, CircuitEventRecord>();
        private readonly SortedDictionary<int, List<CircuitEventRecord>> m_scheduledEvents =
            new SortedDictionary<int, List<CircuitEventRecord>>();
        private readonly SortedDictionary<int, List<Action>> m_scheduledActions =
            new SortedDictionary<int, List<Action>>();
        private readonly SortedDictionary<int, List<Action>> m_pendingRemoteActions =
            new SortedDictionary<int, List<Action>>();
        private readonly List<CircuitEventRecord> m_pendingBroadcast =
            new List<CircuitEventRecord>();
        private readonly Queue<CircuitEventRecord> m_hostJournal =
            new Queue<CircuitEventRecord>();
        private readonly Dictionary<int, int> m_lastRequestByClient =
            new Dictionary<int, int>();
        private readonly HashSet<int> m_departedClientIds = new HashSet<int>();
        private readonly Dictionary<CellFace, int> m_nextPressureScheduleSteps =
            new Dictionary<CellFace, int>();
        private readonly Dictionary<CellFace, int> m_trackedCircuitCells =
            new Dictionary<CellFace, int>();
        private readonly SortedDictionary<int, List<CircuitStateRecord>> m_snapshotParts =
            new SortedDictionary<int, List<CircuitStateRecord>>();
        private readonly Dictionary<CellFace, ButtonPulseState> m_buttonPulses =
            new Dictionary<CellFace, ButtonPulseState>();
        private readonly Dictionary<Point3, PendingSwitchState> m_pendingSwitchStates =
            new Dictionary<Point3, PendingSwitchState>();
        private readonly Dictionary<int, uint> m_hostCheckpointHashes =
            new Dictionary<int, uint>();
        private readonly Dictionary<int, List<KeyValuePair<int, uint?>>> m_pendingHashReports =
            new Dictionary<int, List<KeyValuePair<int, uint?>>>();
        private readonly Dictionary<int, int> m_hostRepairBarriers =
            new Dictionary<int, int>();

        private SuSubsystemElectricity m_subsystem;
        private Project m_project;
        private bool m_applyingEvent;
        private bool m_hasClock;
        private bool m_hasFence;
        private bool m_localSuspended;
        private bool m_hostPaused;
        private bool m_recoveryHold;
        private bool m_recoveryRequested;
        private bool m_snapshotRequested;
        private bool m_randomGeneratorsInitialized;
        private int m_epoch;
        private int m_epochServerStep;
        private int m_epochHostCircuitStep;
        private int m_localCircuitOffset;
        private int m_nextRequestId;
        private int m_nextHostSequence;
        private int m_expectedSequence = 1;
        private int m_lastAppliedSequence;
        private int m_knownHostSequence;
        private int m_nextHashStep;
        private int m_lastHashStep;
        private uint m_lastHash;
        private int m_snapshotPartCount;
        private int m_snapshotSequence;
        private int m_timelineGeneration;
        private int m_safeThroughHostCircuitStep;
        private int m_receivedFenceSerial;
        private int m_requiredFenceSerial;
        private long m_fenceTerrainSequence;
        private double m_lastFenceRealTime;
        private double m_smoothedRtt;
        private double m_smoothedRttDeviation;
        private int m_repairBarrierHostStep;
        private int m_repairCheckpointStep;
        private int m_lastReportedHashStep;
        private int m_snapshotHostCircuitStep;
        private int m_snapshotLastSequence;
        private int m_lastObservedHostCircuitStep;
        private double m_lastHostProgressRealTime;

        public CircuitSynchronizer(ScMultiplayer owner)
        {
            m_owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public bool IsSimulationPaused
        {
            get
            {
                if (!ScMultiplayer.IsHost) TryCompleteRecoveryHold();
                return m_localSuspended || (!ScMultiplayer.IsHost &&
                    (IsRepairBarrierReached() ||
                    (!m_hasFence || m_hostPaused || m_recoveryHold || IsFenceStale())));
            }
        }

        public bool ShouldSuppressClientInput
        {
            get
            {
                if (ScMultiplayer.IsHost || m_subsystem == null) return false;
                if (!m_hasFence || m_localSuspended || m_hostPaused || m_recoveryHold ||
                    IsFenceStale())
                    return true;
                int target = HostToLocal(m_epochHostCircuitStep + StepDelta(NetworkStep,
                    m_epochServerStep));
                target = Math.Min(target, HostToLocal(m_safeThroughHostCircuitStep));
                return target - m_subsystem.CircuitStep > CatchUpInputSuppressionSteps;
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Load
        public void EnsureBound(Project project)
        {
            bool shouldBind = project != null && ScMultiplayer.client?.IsConnected == true;
            SuSubsystemElectricity electricity = shouldBind
                ? project.FindSubsystem<SubsystemElectricity>(false) as
                    SuSubsystemElectricity
                : null;
            if (ReferenceEquals(m_project, project) && ReferenceEquals(m_subsystem, electricity))
            {
                return;
            }

            Detach();
            m_project = project;
            m_subsystem = electricity;
            if (m_subsystem == null) return;

            if (ScMultiplayer.IsHost)
            {
                m_epoch = CreateEpoch();
                m_timelineGeneration = 1;
                m_hasClock = true;
                m_epochServerStep = NetworkStep;
                m_epochHostCircuitStep = m_subsystem.CircuitStep;
                m_localCircuitOffset = 0;
            }
            // Source: Mod/HeadlessRenderingMod/Plug/HeadlessRenderingMod.cs:
            // HeadlessRenderingMod.HideWindowOnce
            m_localSuspended = ShouldSuspendForWindow(Window.IsActive);
            if (!ScMultiplayer.IsHost && m_localSuspended)
            {
                m_recoveryHold = true;
                m_requiredFenceSerial = m_receivedFenceSerial + 1;
            }
            m_subsystem.AttachSynchronizer(this);
            if (ScMultiplayer.IsHost)
            {
                EnableDeterministicRandom();
                SendFence(-1);
            }
        }

        // Source: Engine/Window.cs:Window.Deactivated
        // Source: Engine/Window.cs:Window.Activated
        public void SetWindowActive(bool active)
        {
            bool suspended = ShouldSuspendForWindow(active);
            if (m_localSuspended == suspended) return;
            m_localSuspended = suspended;
            if (ScMultiplayer.IsHost)
            {
                m_timelineGeneration = m_timelineGeneration == int.MaxValue
                    ? 1
                    : Math.Max(m_timelineGeneration + 1, 1);
                FlushPendingEvents(-1);
                SendFence(-1);
                return;
            }

            m_recoveryHold = true;
            m_requiredFenceSerial = m_receivedFenceSerial + 1;
            m_recoveryRequested = false;
            if (!suspended && m_epoch > 0)
            {
                NetworkMessageSender.SendCircuitSync(0, new CircuitSyncMessage
                {
                    Stage = CircuitSyncStage.CheckpointRequest,
                    Epoch = m_epoch
                });
            }
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Raycast
        public bool TryScheduleLocalInteraction(ComponentPlayer player, Ray3? ray)
        {
            if (m_subsystem == null || player?.ComponentMiner == null || !ray.HasValue ||
                !TryResolveCircuitInteraction(player.ComponentMiner, ray.Value,
                    out CellFace target, out CircuitOperationType operation))
                return false;

            if (ScMultiplayer.IsHost)
            {
                ScheduleHostEvent(target, operation, 0, NetworkStep);
            }
            else
            {
                m_nextRequestId = m_nextRequestId == int.MaxValue ? 1 : m_nextRequestId + 1;
                NetworkMessageSender.SendCircuitSync(0, new CircuitSyncMessage
                {
                    Stage = CircuitSyncStage.Request,
                    RequestId = m_nextRequestId,
                    ClientStep = NetworkStep,
                    Point = target.Point,
                    RequestRay = ray.Value,
                    MountingFace = (byte)target.Face,
                    Operation = operation
                });
            }
            return true;
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.Client_DirectInput
        public void HandleMessage(CircuitSyncMessage message, int sourceClientId)
        {
            if (message == null || m_subsystem == null) return;
            if (ScMultiplayer.IsHost)
            {
                if (sourceClientId <= 0) return;
                switch (message.Stage)
                {
                    case CircuitSyncStage.Request:
                        HandleHostRequest(message, sourceClientId);
                        break;
                    case CircuitSyncStage.RecoveryRequest:
                        HandleRecoveryRequest(message, sourceClientId);
                        break;
                    case CircuitSyncStage.SnapshotRequest:
                        SendSnapshot(sourceClientId);
                        break;
                    case CircuitSyncStage.HashReport:
                        HandleHashReport(message, sourceClientId);
                        break;
                    case CircuitSyncStage.CheckpointRequest:
                        if (message.Epoch == m_epoch)
                        {
                            ScheduleHostCheckpoint();
                            SendFence(sourceClientId);
                        }
                        break;
                }
                return;
            }
            if (sourceClientId != 0) return;
            switch (message.Stage)
            {
                case CircuitSyncStage.EventBatch:
                    HandleEventBatch(message);
                    break;
                case CircuitSyncStage.Clock:
                    HandleClock(message);
                    break;
                case CircuitSyncStage.Snapshot:
                    HandleSnapshot(message);
                    break;
                case CircuitSyncStage.Fence:
                    HandleFence(message);
                    break;
                case CircuitSyncStage.ButtonReleaseConfirm:
                    HandleButtonReleaseConfirm(message);
                    break;
                case CircuitSyncStage.RepairPlan:
                    HandleRepairPlan(message);
                    break;
            }
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.TriggerNetworkTick
        public void PublishNetworkState(bool checkpoint, bool publishFence)
        {
            if (!ScMultiplayer.IsHost || m_subsystem == null) return;
            TrimJournal();
            FlushPendingEvents(-1);
            if (publishFence) SendFence(-1);
            if (!checkpoint) return;

            ScheduleHostCheckpoint();
            int nextHashStep = m_nextHashStep;
            NetworkMessageSender.SendCircuitSync(-1, new CircuitSyncMessage
            {
                Stage = CircuitSyncStage.Clock,
                Epoch = m_epoch,
                ServerStep = NetworkStep,
                HostCircuitStep = m_subsystem.CircuitStep,
                LastSequence = m_nextHostSequence,
                RequiredTerrainSequence = m_owner.CircuitTerrainSequence,
                HashStep = m_lastHashStep,
                StateHash = m_lastHash,
                NextHashStep = nextHashStep
            }, latest: true);
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.Client_GameStep
        // A topology change invalidates pending per-client circuit repair bookkeeping. Publish a
        // reliable fence immediately so surviving clients do not remain behind a stale 16Hz
        // unreliable execution window after another peer disconnects.
        internal void NotifyClientDeparted(int clientId)
        {
            if (clientId <= 0 || !m_departedClientIds.Add(clientId)) return;
            if (ScMultiplayer.IsHost)
            {
                m_lastRequestByClient.Remove(clientId);
                m_hostRepairBarriers.Remove(clientId);
                foreach (int checkpoint in m_pendingHashReports.Keys.ToArray())
                {
                    List<KeyValuePair<int, uint?>> reports =
                        m_pendingHashReports[checkpoint];
                    reports.RemoveAll(item => item.Key == clientId);
                    if (reports.Count == 0)
                        m_pendingHashReports.Remove(checkpoint);
                }
                if (m_subsystem != null && m_epoch > 0)
                {
                    ScheduleHostCheckpoint();
                    SendFence(-1);
                }
                return;
            }

            if (m_subsystem == null || m_epoch <= 0 ||
                ScMultiplayer.client?.IsConnected != true)
                return;
            NetworkMessageSender.SendCircuitSync(0, new CircuitSyncMessage
            {
                Stage = CircuitSyncStage.CheckpointRequest,
                Epoch = m_epoch
            });
        }

        public void Reset()
        {
            Detach();
            m_project = null;
            m_receivedEvents.Clear();
            m_scheduledEvents.Clear();
            m_scheduledActions.Clear();
            m_pendingRemoteActions.Clear();
            m_pendingBroadcast.Clear();
            m_hostJournal.Clear();
            m_lastRequestByClient.Clear();
            m_departedClientIds.Clear();
            m_nextPressureScheduleSteps.Clear();
            m_trackedCircuitCells.Clear();
            m_snapshotParts.Clear();
            m_buttonPulses.Clear();
            m_pendingSwitchStates.Clear();
            m_hostCheckpointHashes.Clear();
            m_pendingHashReports.Clear();
            m_hostRepairBarriers.Clear();
            m_hasClock = false;
            m_hasFence = false;
            m_localSuspended = false;
            m_hostPaused = false;
            m_recoveryHold = false;
            m_recoveryRequested = false;
            m_snapshotRequested = false;
            m_randomGeneratorsInitialized = false;
            m_epoch = 0;
            m_nextRequestId = 0;
            m_nextHostSequence = 0;
            m_expectedSequence = 1;
            m_lastAppliedSequence = 0;
            m_knownHostSequence = 0;
            m_nextHashStep = 0;
            m_lastHashStep = 0;
            m_lastHash = 0;
            m_timelineGeneration = 0;
            m_safeThroughHostCircuitStep = 0;
            m_receivedFenceSerial = 0;
            m_requiredFenceSerial = 0;
            m_fenceTerrainSequence = 0;
            m_lastFenceRealTime = 0.0;
            m_smoothedRtt = 0.0;
            m_smoothedRttDeviation = 0.0;
            m_repairBarrierHostStep = 0;
            m_repairCheckpointStep = 0;
            m_lastReportedHashStep = 0;
            m_snapshotHostCircuitStep = 0;
            m_snapshotLastSequence = 0;
            m_lastObservedHostCircuitStep = 0;
            m_lastHostProgressRealTime = 0.0;
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        internal int? GetCircuitStepTarget()
        {
            if (ScMultiplayer.IsHost || !m_hasClock) return null;
            TryCompleteRecoveryHold();
            if (IsSimulationPaused) return m_subsystem.CircuitStep;
            int target = HostToLocal(m_epochHostCircuitStep + StepDelta(NetworkStep,
                m_epochServerStep));
            if (m_hasFence)
            {
                target = Math.Min(target, HostToLocal(m_safeThroughHostCircuitStep));
                if (SuSubsystemTerrain.LastAppliedTerrainSequence < m_fenceTerrainSequence)
                    return m_subsystem.CircuitStep;
            }
            if (m_scheduledEvents.Count > 0)
            {
                KeyValuePair<int, List<CircuitEventRecord>> first = m_scheduledEvents.First();
                long required = first.Value.Count > 0
                    ? first.Value.Max(item => item.RequiredTerrainSequence)
                    : 0;
                if (first.Key <= target && SuSubsystemTerrain.LastAppliedTerrainSequence < required)
                    target = Math.Min(target, first.Key - 1);
            }
            if (m_repairBarrierHostStep > 0)
                target = Math.Min(target, HostToLocal(m_repairBarrierHostStep) - 1);
            return target;
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        internal void PrepareCircuitStep(int nextStep)
        {
            if (m_scheduledActions.TryGetValue(nextStep, out List<Action> actions))
            {
                m_scheduledActions.Remove(nextStep);
                foreach (Action action in actions)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[SuAPI] Scheduled circuit write failed: {ex.Message}");
                    }
                }
            }

            if (!m_scheduledEvents.TryGetValue(nextStep, out List<CircuitEventRecord> events))
                return;
            m_scheduledEvents.Remove(nextStep);
            foreach (CircuitEventRecord item in events.OrderBy(value => value.Sequence))
            {
                ApplyEvent(item);
                m_lastAppliedSequence = Math.Max(m_lastAppliedSequence, item.Sequence);
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        // Hashes are sampled only after the named circuit step and all of its propagation work
        // have completed, so every endpoint observes the same simulation boundary.
        internal void CompleteCircuitStep(int completedStep)
        {
            int hostStep = LocalToHost(completedStep);
            if (m_nextHashStep > 0 && hostStep >= m_nextHashStep)
            {
                int checkpointStep = m_nextHashStep;
                bool exact = hostStep == checkpointStep;
                uint hash = exact ? ComputeStateHash() : 0u;
                if (ScMultiplayer.IsHost)
                {
                    if (exact)
                    {
                        m_lastHashStep = checkpointStep;
                        m_lastHash = hash;
                        m_hostCheckpointHashes[checkpointStep] = hash;
                        while (m_hostCheckpointHashes.Count > 4)
                            m_hostCheckpointHashes.Remove(m_hostCheckpointHashes.Keys.Min());
                        ProcessPendingHashReports(checkpointStep);
                    }
                }
                else if (checkpointStep > m_lastReportedHashStep &&
                    m_repairBarrierHostStep <= 0 && !m_snapshotRequested)
                {
                    m_lastReportedHashStep = checkpointStep;
                    NetworkMessageSender.SendCircuitSync(0, new CircuitSyncMessage
                    {
                        Stage = CircuitSyncStage.HashReport,
                        Epoch = m_epoch,
                        HashStep = checkpointStep,
                        StateHash = hash,
                        HashAvailable = exact
                    });
                }
                m_nextHashStep = 0;
            }
            TryFinalizeSnapshot();
        }

        // Source: Survivalcraft/Game/ButtonElectricElement.cs:ButtonElectricElement.Press
        // Source: Survivalcraft/Game/PressurePlateElectricElement.cs:PressurePlateElectricElement.Press
        internal bool TryScheduleExternalInput(ElectricElement element, float value)
        {
            if (ScMultiplayer.client?.IsConnected != true || m_subsystem == null ||
                m_applyingEvent || element?.CellFaces.Count < 1)
                return false;
            if (!ScMultiplayer.IsHost) return true;
            CellFace target = GetStableCellFace(element);
            if (element is ButtonElectricElement || element is ButtonFurnitureElectricElement)
            {
                ScheduleHostEvent(target, CircuitOperationType.Interact, 0, NetworkStep);
                return true;
            }
            if (element is PressurePlateElectricElement)
            {
                int currentStep = m_subsystem.CircuitStep;
                if (!m_nextPressureScheduleSteps.TryGetValue(target, out int nextStep) ||
                    currentStep >= nextStep)
                {
                    m_nextPressureScheduleSteps[target] = currentStep + PressureRefreshSteps;
                    ScheduleHostEvent(target, CircuitOperationType.Pressure,
                        QuantizePressure(value), NetworkStep);
                }
                return true;
            }
            return false;
        }

        // Source: Survivalcraft/Game/RandomGeneratorElectricElement.cs:RandomGeneratorElectricElement.Simulate
        internal bool TryGetDeterministicRandom(Point3 point, int localCircuitStep,
            int purpose, out uint value)
        {
            value = 0u;
            if (m_subsystem == null || m_epoch <= 0 ||
                (!ScMultiplayer.IsHost && !m_hasClock))
                return false;
            uint hash = 2166136261u;
            Hash(ref hash, m_epoch);
            Hash(ref hash, point.X);
            Hash(ref hash, point.Y);
            Hash(ref hash, point.Z);
            int hostStep = LocalToHost(localCircuitStep);
            Hash(ref hash, hostStep);
            Hash(ref hash, purpose);
            value = hash;
            return true;
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Interact
        private void HandleHostRequest(CircuitSyncMessage message, int sourceClientId)
        {
            if (message.RequestId <= 0 || message.MountingFace > 5 ||
                !IsPlayerCircuitOperation(message.Operation) ||
                !IsFinite(message.RequestRay)) return;
            if (m_lastRequestByClient.TryGetValue(sourceClientId, out int previous) &&
                message.RequestId <= previous) return;
            ComponentPlayer player = m_owner.GetCircuitPlayer(sourceClientId);
            if (player?.ComponentMiner == null ||
                !TryResolveCircuitInteraction(player.ComponentMiner, message.RequestRay,
                    out CellFace target, out CircuitOperationType operation) ||
                !target.Point.Equals(message.Point) ||
                target.Face != message.MountingFace || operation != message.Operation)
                return;
            m_lastRequestByClient[sourceClientId] = message.RequestId;
            ScheduleHostEvent(target, operation, 0, message.ClientStep);
        }

        private void ScheduleHostEvent(CellFace target, CircuitOperationType operation,
            byte value, int clientStep)
        {
            if (!ScMultiplayer.IsHost || m_subsystem == null) return;
            int sequence = ++m_nextHostSequence;
            if (operation == CircuitOperationType.Switch)
            {
                // Source: Survivalcraft/Game/SwitchBlock.cs:SwitchBlock.GetLeverState
                // Serialize the host-selected final state so clients never toggle from a stale
                // local base value. Pending state preserves the order of rapid repeated clicks.
                if (!m_pendingSwitchStates.TryGetValue(target.Point,
                    out PendingSwitchState pending))
                {
                    int cellValue = m_subsystem.SubsystemTerrain.Terrain.GetCellValue(
                        target.X, target.Y, target.Z);
                    pending = new PendingSwitchState
                    {
                        State = SwitchBlock.GetLeverState(cellValue)
                    };
                    m_pendingSwitchStates[target.Point] = pending;
                }
                pending.State = !pending.State;
                pending.LastSequence = sequence;
                value = pending.State ? (byte)1 : (byte)0;
            }
            var item = new CircuitEventRecord
            {
                Sequence = sequence,
                HostCircuitStep = GetAssignedHostCircuitStep(clientStep),
                Point = target.Point,
                MountingFace = (byte)target.Face,
                Operation = operation,
                Value = value,
                RequiredTerrainSequence = m_owner.CircuitTerrainSequence,
                CreatedTime = Time.RealTime
            };
            QueueScheduledEvent(item, item.HostCircuitStep);
            m_hostJournal.Enqueue(item);
            // Source: CircuitSynchronizer.SendEventRecords
            // Player circuit operations are latency-sensitive. Send their assigned execution
            // step immediately; the rolling fence and journal still provide recovery.
            SendEventRecords(-1, new List<CircuitEventRecord> { item });
        }

        // Source: CircuitSynchronizer.GetCircuitLeadSteps
        // Map the observed client input slot onto the host timeline, then leave enough future
        // slots for the authoritative operation to reach every client before execution.
        private int GetAssignedHostCircuitStep(int clientStep)
        {
            int currentHostStep = m_subsystem.CircuitStep;
            int leadSteps = GetCircuitLeadSteps();
            int assignedStep = unchecked(currentHostStep + leadSteps);
            if (clientStep > 0 && m_hasClock)
            {
                int inputHostStep = unchecked(m_epochHostCircuitStep +
                    StepDelta(clientStep, m_epochServerStep));
                int maximumFutureInputStep = unchecked(currentHostStep +
                    CircuitExecutionWindowSteps);
                inputHostStep = MathUtils.Clamp(inputHostStep,
                    currentHostStep - CircuitExecutionWindowSteps,
                    maximumFutureInputStep);
                assignedStep = Math.Max(assignedStep,
                    unchecked(inputHostStep + leadSteps));
            }
            return assignedStep;
        }

        // Source: Mod/ScMultiplayer/Message/CircuitSyncMessage.cs:CircuitSyncMessage.WriteEventBatch
        private void FlushPendingEvents(int targetClientId)
        {
            if (m_pendingBroadcast.Count == 0) return;
            List<CircuitEventRecord> outgoing = m_pendingBroadcast
                .OrderBy(item => item.Sequence).ToList();
            m_pendingBroadcast.Clear();
            SendEventRecords(targetClientId, outgoing);
        }

        private void SendEventRecords(int targetClientId, List<CircuitEventRecord> records)
        {
            for (int offset = 0; offset < records.Count;)
            {
                long terrainSequence = records[offset].RequiredTerrainSequence;
                var batch = new List<CircuitEventRecord>();
                while (offset < records.Count && batch.Count < MaximumEventsPerBatch &&
                    records[offset].RequiredTerrainSequence == terrainSequence)
                    batch.Add(records[offset++]);
                var message = new CircuitSyncMessage
                {
                    Stage = CircuitSyncStage.EventBatch,
                    Epoch = m_epoch,
                    ServerStep = NetworkStep,
                    HostCircuitStep = m_subsystem.CircuitStep,
                    BaseSequence = batch[0].Sequence,
                    BaseHostCircuitStep = batch.Min(item => item.HostCircuitStep),
                    RequiredTerrainSequence = terrainSequence
                };
                message.Events.AddRange(batch);
                NetworkMessageSender.SendCircuitSync(targetClientId, message);
            }
        }

        private void HandleEventBatch(CircuitSyncMessage message)
        {
            if (!AcceptEpoch(message.Epoch)) return;
            if (!m_hasClock)
            {
                int estimatedHostStep = message.HostCircuitStep +
                    StepDelta(NetworkStep, message.ServerStep);
                m_localCircuitOffset = m_subsystem.CircuitStep - estimatedHostStep;
                m_epochServerStep = message.ServerStep;
                m_epochHostCircuitStep = message.HostCircuitStep;
                m_hasClock = true;
                EnableDeterministicRandom();
                DrainPendingRemoteActions();
            }
            if (message.Events.Count > 0)
                m_knownHostSequence = Math.Max(m_knownHostSequence,
                    message.Events.Max(item => item.Sequence));
            foreach (CircuitEventRecord item in message.Events)
            {
                if (item.Sequence < m_expectedSequence ||
                    m_receivedEvents.ContainsKey(item.Sequence)) continue;
                item.RequiredTerrainSequence = message.RequiredTerrainSequence;
                m_receivedEvents[item.Sequence] = item;
            }
            DrainReceivedEvents();
        }

        private void DrainReceivedEvents()
        {
            while (m_receivedEvents.TryGetValue(m_expectedSequence,
                out CircuitEventRecord item))
            {
                m_receivedEvents.Remove(m_expectedSequence);
                int assignedLocalStep = HostToLocal(item.HostCircuitStep);
                int localStep = Math.Max(m_subsystem.CircuitStep + 1, assignedLocalStep);
                QueueScheduledEvent(item, localStep);
                if (assignedLocalStep <= m_subsystem.CircuitStep)
                {
                    // Source: CircuitSynchronizer.RequestSnapshot
                    // A late operation means this client already simulated past its assigned
                    // slot. Apply it at the next slot, hold the timeline and verify by snapshot.
                    m_recoveryHold = true;
                    m_requiredFenceSerial = m_receivedFenceSerial;
                    RequestSnapshot();
                }
                m_expectedSequence++;
                m_recoveryRequested = false;
            }
            if (m_receivedEvents.Count > 0 && m_receivedEvents.Keys.Min() > m_expectedSequence)
                RequestRecovery();
        }

        private void QueueScheduledEvent(CircuitEventRecord item, int localStep)
        {
            if (!m_scheduledEvents.TryGetValue(localStep, out List<CircuitEventRecord> list))
            {
                list = new List<CircuitEventRecord>();
                m_scheduledEvents.Add(localStep, list);
            }
            list.Add(item);
        }

        private void QueueScheduledAction(int localStep, Action action)
        {
            QueueAction(m_scheduledActions, localStep, action);
        }

        private static void QueueAction(SortedDictionary<int, List<Action>> target,
            int step, Action action)
        {
            if (!target.TryGetValue(step, out List<Action> actions))
            {
                actions = new List<Action>();
                target.Add(step, actions);
            }
            actions.Add(action);
        }

        private void DrainPendingRemoteActions()
        {
            foreach (KeyValuePair<int, List<Action>> item in m_pendingRemoteActions)
            {
                int localStep = Math.Max(m_subsystem.CircuitStep + 1,
                    HostToLocal(item.Key));
                foreach (Action action in item.Value)
                    QueueScheduledAction(localStep, action);
            }
            m_pendingRemoteActions.Clear();
        }

        // Source: Survivalcraft/Game/ElectricElement.cs:ElectricElement.OnInteract
        private void ApplyEvent(CircuitEventRecord item)
        {
            if (ScMultiplayer.IsHost && item.Operation == CircuitOperationType.Switch &&
                m_pendingSwitchStates.TryGetValue(item.Point,
                    out PendingSwitchState pending) &&
                pending.LastSequence <= item.Sequence)
            {
                m_pendingSwitchStates.Remove(item.Point);
            }
            ElectricElement element = m_subsystem.GetElectricElement(item.Point.X,
                item.Point.Y, item.Point.Z, item.MountingFace);
            if (element == null) return;
            TrackCircuit(element, item.HostCircuitStep);
            m_applyingEvent = true;
            try
            {
                if (item.Operation == CircuitOperationType.Interact)
                {
                    ApplyButtonPress(element, item);
                }
                else if (item.Operation == CircuitOperationType.Pressure &&
                    element is SuPressurePlateElectricElement networkPlate)
                {
                    networkPlate.ApplyNetworkPressure(ExpandPressure(item.Value));
                }
                else if (item.Operation == CircuitOperationType.Pressure &&
                    element is PressurePlateElectricElement plate)
                {
                    plate.Press(ExpandPressure(item.Value));
                }
                else if (item.Operation == CircuitOperationType.Switch &&
                    element is SwitchElectricElement switchElement)
                {
                    // Source: Survivalcraft/Game/SwitchElectricElement.cs:
                    // SwitchElectricElement.OnInteract
                    CellFace face = GetStableCellFace(switchElement);
                    int cellValue = m_subsystem.SubsystemTerrain.Terrain.GetCellValue(
                        face.X, face.Y, face.Z);
                    int value = SwitchBlock.SetLeverState(cellValue,
                        item.Value != 0);
                    m_subsystem.SubsystemTerrain.ChangeCell(
                        face.X, face.Y, face.Z, value);
                    // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemTerrain.cs:
                    // SuSubsystemTerrain.ForceNetworkCellGeometry
                    // The authoritative lever model and its electric output change on the same
                    // host-assigned circuit step, even if the chunk was already downgraded.
                    (m_subsystem.SubsystemTerrain as SuSubsystemTerrain)?
                        .ForceNetworkCellGeometry(face.Point);
                    PlayCircuitClick(face);
                }
                else if (item.Operation == CircuitOperationType.Rotate &&
                    element is RotateableElectricElement rotateable)
                {
                    // Source: Survivalcraft/Game/RotateableElectricElement.cs:
                    // RotateableElectricElement.OnInteract
                    rotateable.Rotation++;
                }
                else if (item.Operation == CircuitOperationType.FurnitureSwitch &&
                    element is SwitchFurnitureElectricElement furnitureSwitch)
                {
                    // Source: Survivalcraft/Game/SwitchFurnitureElectricElement.cs:
                    // SwitchFurnitureElectricElement.OnInteract
                    CellFace face = GetStableCellFace(furnitureSwitch);
                    m_subsystem.SubsystemTerrain.SubsystemFurnitureBlockBehavior
                        .SwitchToNextState(face.X, face.Y, face.Z, playSound: false);
                    PlayCircuitClick(face);
                }
            }
            finally
            {
                m_applyingEvent = false;
            }
        }

        // Source: Survivalcraft/Game/ButtonElectricElement.cs:ButtonElectricElement.Press
        // Source: Survivalcraft/Game/ButtonFurnitureElectricElement.cs:
        // ButtonFurnitureElectricElement.Press
        private void ApplyButtonPress(ElectricElement element, CircuitEventRecord item)
        {
            if (!TryGetButtonDeclaringType(element, out Type declaringType)) return;
            bool wasPressed = ScMultiplayer.ModManager.ModParentField.GetParentField<bool>(
                element, "m_wasPressed", declaringType);
            float voltage = ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                element, "m_voltage", declaringType);
            if (wasPressed || ElectricElement.IsSignalHigh(voltage)) return;

            if (element is SuButtonElectricElement networkButton)
                networkButton.ApplyNetworkPress();
            else if (element is ButtonElectricElement button)
                button.Press();
            else if (element is ButtonFurnitureElectricElement furnitureButton)
                furnitureButton.Press();
            else return;

            CellFace face = GetStableCellFace(element);
            int releaseHostStep = item.HostCircuitStep + 11;
            m_buttonPulses[face] = new ButtonPulseState
            {
                PressSequence = item.Sequence,
                ReleaseHostStep = releaseHostStep
            };
            if (ScMultiplayer.IsHost)
            {
                int confirmHostStep = releaseHostStep + 1;
                QueueScheduledAction(confirmHostStep, () =>
                    SendButtonReleaseConfirm(face, item.Sequence, confirmHostStep));
            }
        }

        // Source: CircuitSynchronizer.ApplyButtonPress
        private void SendButtonReleaseConfirm(CellFace face, int pressSequence,
            int confirmHostStep)
        {
            NetworkMessageSender.SendCircuitSync(-1, new CircuitSyncMessage
            {
                Stage = CircuitSyncStage.ButtonReleaseConfirm,
                Epoch = m_epoch,
                HostCircuitStep = confirmHostStep,
                ExpectedSequence = pressSequence,
                Point = face.Point,
                MountingFace = (byte)face.Face
            });
            if (m_buttonPulses.TryGetValue(face, out ButtonPulseState pulse) &&
                pulse.PressSequence == pressSequence)
                m_buttonPulses.Remove(face);
        }

        // Source: Mod/ScMultiplayer/Message/CircuitSyncMessage.cs:
        // CircuitSyncStage.ButtonReleaseConfirm
        private void HandleButtonReleaseConfirm(CircuitSyncMessage message)
        {
            if (!AcceptEpoch(message.Epoch) || message.ExpectedSequence <= 0) return;
            Action apply = () => ApplyButtonReleaseConfirm(message.Point,
                message.MountingFace, message.ExpectedSequence);
            if (!m_hasClock)
                QueueAction(m_pendingRemoteActions, message.HostCircuitStep, apply);
            else
                QueueScheduledAction(Math.Max(m_subsystem.CircuitStep + 1,
                    HostToLocal(message.HostCircuitStep)), apply);
        }

        private void ApplyButtonReleaseConfirm(Point3 point, byte mountingFace,
            int pressSequence)
        {
            var face = new CellFace(point.X, point.Y, point.Z, mountingFace);
            if (!m_buttonPulses.TryGetValue(face, out ButtonPulseState pulse) ||
                pulse.PressSequence != pressSequence)
                return;
            ElectricElement element = m_subsystem.GetElectricElement(
                point.X, point.Y, point.Z, mountingFace);
            if (element == null || !TryGetButtonDeclaringType(element,
                out Type declaringType))
            {
                m_buttonPulses.Remove(face);
                return;
            }
            ClearScheduledSimulation(element);
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                "m_wasPressed", false, declaringType);
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                "m_voltage", 0f, declaringType);
            m_subsystem.QueueElectricElementConnectionsForSimulation(element,
                m_subsystem.CircuitStep + 1);
            m_buttonPulses.Remove(face);
        }

        // Source: Mod/ScMultiplayer/Message/CircuitSyncMessage.cs:
        // CircuitSyncMessage.ReadFence
        private void HandleFence(CircuitSyncMessage message)
        {
            if (!AcceptEpoch(message.Epoch)) return;
            if (m_timelineGeneration > 0 &&
                message.TimelineGeneration < m_timelineGeneration)
                return;

            bool generationChanged = message.TimelineGeneration != m_timelineGeneration;
            bool wasStale = IsFenceStale();
            bool wasHostPaused = m_hostPaused;
            m_timelineGeneration = message.TimelineGeneration;
            m_receivedFenceSerial++;
            m_lastFenceRealTime = Time.RealTime;
            m_hasFence = true;
            m_safeThroughHostCircuitStep = message.SafeThroughHostCircuitStep;
            m_fenceTerrainSequence = Math.Max(message.RequiredTerrainSequence, 0L);
            m_hostPaused = message.IsPaused;
            AcceptRemoteCheckpoint(message.NextHashStep);
            if ((wasHostPaused && !m_hostPaused) || wasStale)
            {
                NetworkMessageSender.SendCircuitSync(0, new CircuitSyncMessage
                {
                    Stage = CircuitSyncStage.CheckpointRequest,
                    Epoch = m_epoch
                });
            }

            if (!m_hasClock)
            {
                int estimatedHostStep = message.HostCircuitStep +
                    StepDelta(NetworkStep, message.ServerStep);
                m_localCircuitOffset = m_subsystem.CircuitStep - estimatedHostStep;
                m_expectedSequence = message.LastSequence + 1;
                m_lastAppliedSequence = message.LastSequence;
                m_hasClock = true;
                EnableDeterministicRandom();
                DrainPendingRemoteActions();
                if (message.LastSequence > 0) RequestSnapshot();
            }
            if (StepDelta(message.ServerStep, m_epochServerStep) > 0 ||
                m_epochServerStep == 0)
            {
                m_epochServerStep = message.ServerStep;
                m_epochHostCircuitStep = message.HostCircuitStep;
            }

            m_knownHostSequence = Math.Max(m_knownHostSequence, message.LastSequence);
            if (generationChanged || wasStale)
            {
                m_recoveryHold = true;
                m_requiredFenceSerial = m_receivedFenceSerial;
                m_recoveryRequested = false;
            }
            if (message.LastSequence >= m_expectedSequence && m_receivedEvents.Count == 0)
                RequestRecovery();
            TryCompleteRecoveryHold();
        }

        private void HandleClock(CircuitSyncMessage message)
        {
            if (!AcceptEpoch(message.Epoch)) return;
            // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.SendDirectInput
            // Latest clock packets are intentionally unreliable and can be reordered in transit.
            if (m_hasClock && StepDelta(message.ServerStep, m_epochServerStep) <= 0)
                return;
            if (!m_hasClock)
            {
                int estimatedHostStep = message.HostCircuitStep +
                    StepDelta(NetworkStep, message.ServerStep);
                m_localCircuitOffset = m_subsystem.CircuitStep - estimatedHostStep;
                m_expectedSequence = message.LastSequence + 1;
                m_lastAppliedSequence = message.LastSequence;
                m_hasClock = true;
                EnableDeterministicRandom();
                DrainPendingRemoteActions();
                if (message.LastSequence > 0) RequestSnapshot();
            }
            m_epochServerStep = message.ServerStep;
            m_epochHostCircuitStep = message.HostCircuitStep;
            m_knownHostSequence = Math.Max(m_knownHostSequence, message.LastSequence);
            AcceptRemoteCheckpoint(message.NextHashStep);
            if (message.LastSequence >= m_expectedSequence && m_receivedEvents.Count == 0)
                RequestRecovery();
        }

        private void ScheduleHostCheckpoint()
        {
            if (!ScMultiplayer.IsHost || m_subsystem == null) return;
            int candidate = m_subsystem.CircuitStep + Math.Max(HashLeadSteps,
                GetCircuitLeadSteps() + 5);
            if (m_nextHashStep <= m_subsystem.CircuitStep || candidate < m_nextHashStep)
                m_nextHashStep = candidate;
        }

        private void AcceptRemoteCheckpoint(int hostStep)
        {
            if (ScMultiplayer.IsHost || hostStep <= 0 ||
                hostStep <= m_lastReportedHashStep || m_repairBarrierHostStep > 0)
                return;
            if (m_nextHashStep <= 0 || hostStep < m_nextHashStep)
                m_nextHashStep = hostStep;
        }

        private void HandleHashReport(CircuitSyncMessage message, int sourceClientId)
        {
            if (!ScMultiplayer.IsHost || message.Epoch != m_epoch ||
                message.HashStep <= 0 || sourceClientId <= 0) return;
            uint? reportedHash = message.HashAvailable ? message.StateHash : null;
            if (!m_hostCheckpointHashes.TryGetValue(message.HashStep,
                out uint authoritativeHash))
            {
                if (message.HashStep < m_lastHashStep)
                {
                    PlanClientRepair(sourceClientId, message.HashStep);
                    return;
                }
                if (!m_pendingHashReports.TryGetValue(message.HashStep,
                    out List<KeyValuePair<int, uint?>> reports))
                {
                    reports = new List<KeyValuePair<int, uint?>>();
                    m_pendingHashReports.Add(message.HashStep, reports);
                }
                reports.RemoveAll(item => item.Key == sourceClientId);
                reports.Add(new KeyValuePair<int, uint?>(sourceClientId, reportedHash));
                return;
            }
            if (!reportedHash.HasValue || reportedHash.Value != authoritativeHash)
                PlanClientRepair(sourceClientId, message.HashStep);
        }

        private void ProcessPendingHashReports(int checkpointStep)
        {
            if (!m_pendingHashReports.TryGetValue(checkpointStep,
                out List<KeyValuePair<int, uint?>> reports)) return;
            m_pendingHashReports.Remove(checkpointStep);
            uint authoritativeHash = m_hostCheckpointHashes[checkpointStep];
            foreach (KeyValuePair<int, uint?> report in reports)
            {
                if (!report.Value.HasValue || report.Value.Value != authoritativeHash)
                    PlanClientRepair(report.Key, checkpointStep);
            }
        }

        private void PlanClientRepair(int targetClientId, int checkpointStep)
        {
            if (m_hostRepairBarriers.ContainsKey(targetClientId)) return;
            int barrier = m_subsystem.CircuitStep + Math.Max(
                RepairBarrierMinimumLeadSteps, GetCircuitLeadSteps() + 5);
            m_hostRepairBarriers[targetClientId] = barrier;
            NetworkMessageSender.SendCircuitSync(targetClientId, new CircuitSyncMessage
            {
                Stage = CircuitSyncStage.RepairPlan,
                Epoch = m_epoch,
                HashStep = checkpointStep,
                HostCircuitStep = barrier
            });
            QueueScheduledAction(barrier, () =>
            {
                SendSnapshot(targetClientId);
                m_hostRepairBarriers.Remove(targetClientId);
            });
        }

        private void HandleRepairPlan(CircuitSyncMessage message)
        {
            if (!AcceptEpoch(message.Epoch) || message.HostCircuitStep <= 0) return;
            if (m_repairBarrierHostStep > 0 &&
                message.HostCircuitStep >= m_repairBarrierHostStep) return;
            m_repairCheckpointStep = message.HashStep;
            m_repairBarrierHostStep = message.HostCircuitStep;
            m_snapshotRequested = true;
        }

        private bool IsRepairBarrierReached()
        {
            return !ScMultiplayer.IsHost && m_subsystem != null &&
                m_repairBarrierHostStep > 0 &&
                m_subsystem.CircuitStep >= HostToLocal(m_repairBarrierHostStep) - 1;
        }

        private void TryCompleteRecoveryHold()
        {
            if (!m_recoveryHold || m_localSuspended || m_hostPaused || !m_hasFence)
                return;
            if (m_receivedFenceSerial < m_requiredFenceSerial)
                return;
            if (SuSubsystemTerrain.LastAppliedTerrainSequence < m_fenceTerrainSequence)
                return;
            if (m_snapshotRequested)
                return;
            if (m_expectedSequence <= m_knownHostSequence)
            {
                RequestRecovery();
                return;
            }
            m_recoveryHold = false;
        }

        private bool IsFenceStale()
        {
            return m_hasFence && m_lastFenceRealTime > 0.0 &&
                Time.RealTime - m_lastFenceRealTime > FenceStaleTime;
        }

        private bool AcceptEpoch(int epoch)
        {
            if (epoch <= 0) return false;
            if (m_epoch == 0)
            {
                m_epoch = epoch;
                return true;
            }
            if (m_epoch == epoch) return true;
            Reset();
            EnsureBound(GameManager.Project);
            m_epoch = epoch;
            return true;
        }

        private void RequestRecovery()
        {
            if (m_recoveryRequested || m_epoch <= 0) return;
            m_recoveryRequested = true;
            NetworkMessageSender.SendCircuitSync(0, new CircuitSyncMessage
            {
                Stage = CircuitSyncStage.RecoveryRequest,
                Epoch = m_epoch,
                ExpectedSequence = m_expectedSequence
            });
        }

        private void HandleRecoveryRequest(CircuitSyncMessage message, int sourceClientId)
        {
            if (message.Epoch != m_epoch || message.ExpectedSequence <= 0) return;
            List<CircuitEventRecord> records = m_hostJournal
                .Where(item => item.Sequence >= message.ExpectedSequence)
                .OrderBy(item => item.Sequence).ToList();
            if (records.Count == 0 || records[0].Sequence != message.ExpectedSequence)
            {
                SendSnapshot(sourceClientId);
                return;
            }
            SendEventRecords(sourceClientId, records);
        }

        private void RequestSnapshot()
        {
            if (m_snapshotRequested || m_epoch <= 0) return;
            m_snapshotRequested = true;
            NetworkMessageSender.SendCircuitSync(0, new CircuitSyncMessage
            {
                Stage = CircuitSyncStage.SnapshotRequest,
                Epoch = m_epoch,
                ExpectedSequence = m_expectedSequence
            });
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_electricElements
        private void SendSnapshot(int targetClientId)
        {
            List<CircuitStateRecord> states = CaptureStateRecords();
            int partCount = Math.Max((states.Count + MaximumStatesPerSnapshot - 1) /
                MaximumStatesPerSnapshot, 1);
            for (int part = 0; part < partCount; part++)
            {
                var message = new CircuitSyncMessage
                {
                    Stage = CircuitSyncStage.Snapshot,
                    Epoch = m_epoch,
                    HostCircuitStep = m_subsystem.CircuitStep,
                    LastSequence = m_lastAppliedSequence,
                    SnapshotPartIndex = part,
                    SnapshotPartCount = partCount
                };
                message.States.AddRange(states.Skip(part * MaximumStatesPerSnapshot)
                    .Take(MaximumStatesPerSnapshot));
                NetworkMessageSender.SendCircuitSync(targetClientId, message);
            }
        }

        private void HandleSnapshot(CircuitSyncMessage message)
        {
            if (!AcceptEpoch(message.Epoch) || message.SnapshotPartCount < 1 ||
                message.SnapshotPartIndex >= message.SnapshotPartCount) return;
            if (m_snapshotSequence != message.LastSequence ||
                m_snapshotPartCount != message.SnapshotPartCount ||
                m_snapshotHostCircuitStep != message.HostCircuitStep)
            {
                m_snapshotParts.Clear();
                m_snapshotSequence = message.LastSequence;
                m_snapshotPartCount = message.SnapshotPartCount;
                m_snapshotHostCircuitStep = message.HostCircuitStep;
                m_snapshotLastSequence = message.LastSequence;
            }
            m_snapshotParts[message.SnapshotPartIndex] = message.States.ToList();
            TryFinalizeSnapshot();
        }

        private void TryFinalizeSnapshot()
        {
            if (m_snapshotPartCount < 1 ||
                m_snapshotParts.Count != m_snapshotPartCount) return;
            if (m_repairBarrierHostStep > 0 && m_subsystem.CircuitStep <
                HostToLocal(m_repairBarrierHostStep) - 1) return;

            m_trackedCircuitCells.Clear();
            foreach (CircuitStateRecord state in m_snapshotParts.Values.SelectMany(x => x))
                ApplyStateRecord(state, m_snapshotHostCircuitStep);
            m_snapshotParts.Clear();
            m_receivedEvents.Clear();
            foreach (int step in m_scheduledEvents.Keys.ToArray())
            {
                m_scheduledEvents[step].RemoveAll(item =>
                    item.Sequence <= m_snapshotLastSequence);
                if (m_scheduledEvents[step].Count == 0)
                    m_scheduledEvents.Remove(step);
            }
            m_expectedSequence = m_snapshotLastSequence + 1;
            foreach (int sequence in m_scheduledEvents.Values.SelectMany(items => items)
                .Select(item => item.Sequence).Where(sequence =>
                    sequence >= m_expectedSequence).Distinct().OrderBy(sequence => sequence))
            {
                if (sequence != m_expectedSequence) break;
                m_expectedSequence++;
            }
            m_lastAppliedSequence = m_snapshotLastSequence;
            m_snapshotRequested = false;
            m_recoveryRequested = false;
            m_snapshotPartCount = 0;
            m_repairBarrierHostStep = 0;
            m_repairCheckpointStep = 0;
            if (m_expectedSequence <= m_knownHostSequence) RequestRecovery();
            TryCompleteRecoveryHold();
        }

        private List<CircuitStateRecord> CaptureStateRecords()
        {
            var result = new List<CircuitStateRecord>();
            int hostStep = LocalToHost(m_subsystem.CircuitStep);
            foreach (CellFace stale in m_trackedCircuitCells.Where(item =>
                hostStep - item.Value > TrackedCircuitRetentionSteps)
                .Select(item => item.Key).ToArray())
                m_trackedCircuitCells.Remove(stale);
            foreach (ElectricElement element in GetElectricElements())
            {
                CellFace face = GetStableCellFace(element);
                var record = new CircuitStateRecord
                {
                    Point = face.Point,
                    MountingFace = (byte)face.Face,
                    ElementTypeCode = GetStableTypeCode(element.GetType())
                };
                if (!TryCaptureState(element, record)) continue;
                CaptureButtonSnapshotTiming(element, face, record);
                int nextStep = FindNextSimulationStep(element);
                record.NextSimulationSteps = nextStep > m_subsystem.CircuitStep
                    ? MathUtils.Clamp(nextStep - m_subsystem.CircuitStep, 1, 10000)
                    : 0;
                result.Add(record);
            }
            return result.OrderBy(item => item.Point.X).ThenBy(item => item.Point.Y)
                .ThenBy(item => item.Point.Z).ThenBy(item => item.MountingFace).ToList();
        }

        private uint ComputeStateHash()
        {
            uint hash = 2166136261u;
            foreach (CircuitStateRecord item in CaptureStateRecords())
            {
                Hash(ref hash, item.Point.X);
                Hash(ref hash, item.Point.Y);
                Hash(ref hash, item.Point.Z);
                Hash(ref hash, item.MountingFace);
                Hash(ref hash, item.ElementTypeCode);
                Hash(ref hash, item.VoltageLevel);
                Hash(ref hash, item.ButtonPhase);
                Hash(ref hash, item.RemainingSteps);
                Hash(ref hash, item.RelatedSequence);
                Hash(ref hash, item.StateFlags);
                Hash(ref hash, item.NextSimulationSteps);
                Hash(ref hash, item.AuxiliaryVoltageLevel);
                Hash(ref hash, item.ScheduledVoltages.Count);
                foreach (CircuitScheduledVoltageRecord scheduled in
                    item.ScheduledVoltages.OrderBy(value => value.RemainingSteps))
                {
                    Hash(ref hash, scheduled.RemainingSteps);
                    Hash(ref hash, scheduled.VoltageLevel);
                }
            }
            return hash;
        }

        private IEnumerable<ElectricElement> GetElectricElements()
        {
            try
            {
                return ScMultiplayer.ModManager.ModParentField
                    .GetParentField<Dictionary<ElectricElement, bool>>(m_subsystem,
                        "m_electricElements", typeof(SubsystemElectricity)).Keys.ToArray();
            }
            catch
            {
                return Array.Empty<ElectricElement>();
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:
        // SubsystemElectricity.m_futureSimulateLists
        private void CaptureButtonSnapshotTiming(ElectricElement element, CellFace face,
            CircuitStateRecord record)
        {
            if (!TryGetButtonDeclaringType(element, out Type declaringType)) return;
            bool wasPressed = ScMultiplayer.ModManager.ModParentField.GetParentField<bool>(
                element, "m_wasPressed", declaringType);
            float voltage = ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                element, "m_voltage", declaringType);
            byte phase = wasPressed ? (byte)1 :
                ElectricElement.IsSignalHigh(voltage) ? (byte)2 : (byte)0;
            if (phase == 0) return;

            int nextStep = FindNextSimulationStep(element);
            int remaining = nextStep > m_subsystem.CircuitStep
                ? nextStep - m_subsystem.CircuitStep
                : 1;
            record.ButtonPhase = phase;
            record.RemainingSteps = (byte)MathUtils.Clamp(remaining, 1, 15);
            if (m_buttonPulses.TryGetValue(face, out ButtonPulseState pulse))
                record.RelatedSequence = pulse.PressSequence;
        }

        private int FindNextSimulationStep(ElectricElement element)
        {
            Dictionary<int, Dictionary<ElectricElement, bool>> future =
                ScMultiplayer.ModManager.ModParentField.GetParentField<Dictionary<int,
                    Dictionary<ElectricElement, bool>>>(m_subsystem,
                    "m_futureSimulateLists", typeof(SubsystemElectricity));
            return future.Where(item => item.Key > m_subsystem.CircuitStep &&
                item.Value.ContainsKey(element)).Select(item => item.Key)
                .DefaultIfEmpty(0).Min();
        }

        private void ClearScheduledSimulation(ElectricElement element)
        {
            Dictionary<int, Dictionary<ElectricElement, bool>> future =
                ScMultiplayer.ModManager.ModParentField.GetParentField<Dictionary<int,
                    Dictionary<ElectricElement, bool>>>(m_subsystem,
                    "m_futureSimulateLists", typeof(SubsystemElectricity));
            foreach (Dictionary<ElectricElement, bool> list in future.Values)
                list.Remove(element);
        }

        private static bool TryGetButtonDeclaringType(ElectricElement element,
            out Type declaringType)
        {
            if (element is ButtonElectricElement)
            {
                declaringType = typeof(ButtonElectricElement);
                return true;
            }
            if (element is ButtonFurnitureElectricElement)
            {
                declaringType = typeof(ButtonFurnitureElectricElement);
                return true;
            }
            declaringType = null;
            return false;
        }

        private bool TryCaptureState(ElectricElement element,
            CircuitStateRecord state)
        {
            try
            {
                if (element is CounterElectricElement)
                {
                    int counter = ScMultiplayer.ModManager.ModParentField.GetParentField<int>(
                        element, "m_counter", typeof(CounterElectricElement));
                    bool overflow = ScMultiplayer.ModManager.ModParentField.GetParentField<bool>(
                        element, "m_overflow", typeof(CounterElectricElement));
                    bool plusAllowed = ScMultiplayer.ModManager.ModParentField
                        .GetParentField<bool>(element, "m_plusAllowed",
                            typeof(CounterElectricElement));
                    bool minusAllowed = ScMultiplayer.ModManager.ModParentField
                        .GetParentField<bool>(element, "m_minusAllowed",
                            typeof(CounterElectricElement));
                    bool resetAllowed = ScMultiplayer.ModManager.ModParentField
                        .GetParentField<bool>(element, "m_resetAllowed",
                            typeof(CounterElectricElement));
                    state.VoltageLevel = (sbyte)(overflow ? -(counter + 1) : counter);
                    state.StateFlags = (byte)((plusAllowed ? 1 : 0) |
                        (minusAllowed ? 2 : 0) | (resetAllowed ? 4 : 0));
                    return true;
                }
                Type declaringType = TryGetButtonDeclaringType(element,
                    out Type buttonType) ? buttonType :
                    element is BaseDelayGateElectricElement
                        ? typeof(BaseDelayGateElectricElement)
                        : FindFieldDeclaringType(element.GetType(), "m_voltage");
                if (declaringType == null) return false;
                float voltage = ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                    element, "m_voltage", declaringType);
                state.VoltageLevel = (sbyte)MathUtils.Clamp(
                    (int)MathUtils.Round(voltage * 15f), -15, 15);
                if (element is BaseDelayGateElectricElement)
                {
                    float lastStored = ScMultiplayer.ModManager.ModParentField
                        .GetParentField<float>(element, "m_lastStoredVoltage",
                            typeof(BaseDelayGateElectricElement));
                    state.AuxiliaryVoltageLevel = (sbyte)MathUtils.Clamp(
                        (int)MathUtils.Round(lastStored * 15f), -15, 15);
                    Dictionary<int, float> history = ScMultiplayer.ModManager.ModParentField
                        .GetParentField<Dictionary<int, float>>(element,
                            "m_voltagesHistory", typeof(BaseDelayGateElectricElement));
                    foreach (KeyValuePair<int, float> item in history.Where(item =>
                        item.Key >= m_subsystem.CircuitStep).OrderBy(item => item.Key)
                        .Take(300))
                    {
                        state.ScheduledVoltages.Add(new CircuitScheduledVoltageRecord
                        {
                            RemainingSteps = MathUtils.Clamp(
                                item.Key - m_subsystem.CircuitStep, 0, 10000),
                            VoltageLevel = (sbyte)MathUtils.Clamp(
                                (int)MathUtils.Round(item.Value * 15f), -15, 15)
                        });
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyStateRecord(CircuitStateRecord state, int hostCircuitStep)
        {
            ElectricElement element = m_subsystem.GetElectricElement(state.Point.X,
                state.Point.Y, state.Point.Z, state.MountingFace);
            if (element == null || GetStableTypeCode(element.GetType()) !=
                state.ElementTypeCode) return;
            TrackCircuit(element, hostCircuitStep);
            try
            {
                bool isButton = element is ButtonElectricElement ||
                    element is ButtonFurnitureElectricElement;
                if (!isButton) ClearScheduledSimulation(element);
                if (element is ButtonElectricElement)
                {
                    ApplyButtonSnapshotState(element, state, hostCircuitStep,
                        typeof(ButtonElectricElement));
                }
                else if (element is ButtonFurnitureElectricElement)
                {
                    ApplyButtonSnapshotState(element, state, hostCircuitStep,
                        typeof(ButtonFurnitureElectricElement));
                }
                else if (element is CounterElectricElement)
                {
                    int counter = state.VoltageLevel < 0 ? -state.VoltageLevel - 1 :
                        state.VoltageLevel;
                    bool overflow = state.VoltageLevel < 0;
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                        "m_counter", counter, typeof(CounterElectricElement));
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                        "m_overflow", overflow, typeof(CounterElectricElement));
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                        "m_plusAllowed", (state.StateFlags & 1) != 0,
                        typeof(CounterElectricElement));
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                        "m_minusAllowed", (state.StateFlags & 2) != 0,
                        typeof(CounterElectricElement));
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                        "m_resetAllowed", (state.StateFlags & 4) != 0,
                        typeof(CounterElectricElement));
                    m_subsystem.WritePersistentVoltage(state.Point,
                        (float)counter / 15f * (overflow ? -1f : 1f));
                }
                else if (element is BaseDelayGateElectricElement)
                {
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                        "m_voltage", (float)state.VoltageLevel / 15f,
                        typeof(BaseDelayGateElectricElement));
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                        "m_lastStoredVoltage", (float)state.AuxiliaryVoltageLevel / 15f,
                        typeof(BaseDelayGateElectricElement));
                    Dictionary<int, float> history = ScMultiplayer.ModManager.ModParentField
                        .GetParentField<Dictionary<int, float>>(element,
                            "m_voltagesHistory", typeof(BaseDelayGateElectricElement));
                    history.Clear();
                    foreach (CircuitScheduledVoltageRecord scheduled in
                        state.ScheduledVoltages)
                    {
                        int step = m_subsystem.CircuitStep + scheduled.RemainingSteps;
                        history[step] = (float)scheduled.VoltageLevel / 15f;
                        if (scheduled.RemainingSteps > 0)
                            m_subsystem.QueueElectricElementForSimulation(element, step);
                    }
                }
                else
                {
                    float voltage = (float)state.VoltageLevel / 15f;
                    Type declaringType = FindFieldDeclaringType(
                        element.GetType(), "m_voltage");
                    if (declaringType == null) return;
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                        "m_voltage", voltage, declaringType);
                    if (element is SRLatchElectricElement ||
                        element is RandomGeneratorElectricElement)
                        m_subsystem.WritePersistentVoltage(state.Point, voltage);
                }
                m_subsystem.QueueElectricElementConnectionsForSimulation(element,
                    m_subsystem.CircuitStep + 1);
                if (!isButton && state.NextSimulationSteps > 0)
                    m_subsystem.QueueElectricElementForSimulation(element,
                        m_subsystem.CircuitStep + state.NextSimulationSteps);
            }
            catch
            {
            }
        }

        // Source: Survivalcraft/Game/ButtonElectricElement.cs:
        // ButtonElectricElement.Simulate
        // Source: Survivalcraft/Game/ButtonFurnitureElectricElement.cs:
        // ButtonFurnitureElectricElement.Simulate
        private void ApplyButtonSnapshotState(ElectricElement element,
            CircuitStateRecord state, int hostCircuitStep, Type declaringType)
        {
            ClearScheduledSimulation(element);
            float voltage = MathUtils.Max((float)state.VoltageLevel / 15f, 0f);
            bool pendingPress = state.ButtonPhase == 1;
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                "m_wasPressed", pendingPress, declaringType);
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(element,
                "m_voltage", voltage, declaringType);
            CellFace face = GetStableCellFace(element);
            if (state.ButtonPhase != 0 && state.RemainingSteps > 0)
            {
                m_buttonPulses[face] = new ButtonPulseState
                {
                    PressSequence = state.RelatedSequence,
                    ReleaseHostStep = state.ButtonPhase == 1
                        ? hostCircuitStep + state.RemainingSteps + 10
                        : hostCircuitStep + state.RemainingSteps
                };
                m_subsystem.QueueElectricElementForSimulation(element,
                    m_subsystem.CircuitStep + state.RemainingSteps);
            }
            else m_buttonPulses.Remove(face);
        }

        // Source: Survivalcraft/Game/ElectricElement.cs:ElectricElement.OnInteract
        private static bool TryResolveCircuitInteraction(ComponentMiner miner, Ray3 ray,
            out CellFace target, out CircuitOperationType operation)
        {
            target = default;
            operation = default;
            if (!(miner.Raycast(ray, RaycastMode.Interaction) is TerrainRaycastResult hit))
                return false;
            SubsystemElectricity electricity = GameManager.Project?
                .FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null) return false;
            for (int face = 0; face < 6; face++)
            {
                ElectricElement element = electricity.GetElectricElement(hit.CellFace.X,
                    hit.CellFace.Y, hit.CellFace.Z, face);
                if (TryGetPlayerCircuitOperation(element, out operation))
                {
                    target = new CellFace(hit.CellFace.X, hit.CellFace.Y,
                        hit.CellFace.Z, face);
                    return true;
                }
            }
            return false;
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Interact
        private static bool TryGetPlayerCircuitOperation(ElectricElement element,
            out CircuitOperationType operation)
        {
            if (element is ButtonElectricElement ||
                element is ButtonFurnitureElectricElement)
                operation = CircuitOperationType.Interact;
            else if (element is SwitchElectricElement)
                operation = CircuitOperationType.Switch;
            else if (element is RotateableElectricElement)
                operation = CircuitOperationType.Rotate;
            else if (element is SwitchFurnitureElectricElement)
                operation = CircuitOperationType.FurnitureSwitch;
            else
            {
                operation = default;
                return false;
            }
            return true;
        }

        private static bool IsPlayerCircuitOperation(CircuitOperationType operation)
        {
            return operation == CircuitOperationType.Interact ||
                operation == CircuitOperationType.Switch ||
                operation == CircuitOperationType.Rotate ||
                operation == CircuitOperationType.FurnitureSwitch;
        }

        private void PlayCircuitClick(CellFace face)
        {
            m_subsystem.SubsystemAudio.PlaySound("Audio/Click", 1f, 0f,
                new Vector3(face.X, face.Y, face.Z), 2f, autoDelay: true);
        }

        // Source: EntitySystem/SuAPI/ModParentField.cs:ModParentField
        private static Type FindFieldDeclaringType(Type type, string fieldName)
        {
            while (type != null && type != typeof(object))
            {
                if (type.GetField(fieldName, System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly) != null)
                    return type;
                type = type.BaseType;
            }
            return null;
        }

        private static int GetStableTypeCode(Type type)
        {
            uint hash = 2166136261u;
            string name = type?.FullName ?? string.Empty;
            foreach (char value in name)
            {
                hash = (hash ^ (byte)value) * 16777619u;
                hash = (hash ^ (byte)(value >> 8)) * 16777619u;
            }
            return (int)(hash & 0x7fffffffu);
        }

        private static bool IsFinite(Ray3 ray) =>
            IsFinite(ray.Position.X) && IsFinite(ray.Position.Y) && IsFinite(ray.Position.Z) &&
            IsFinite(ray.Direction.X) && IsFinite(ray.Direction.Y) &&
            IsFinite(ray.Direction.Z) && ray.Direction.LengthSquared() > 0.0001f;

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static CellFace GetStableCellFace(ElectricElement element)
        {
            return element.CellFaces.OrderBy(face => face.X).ThenBy(face => face.Y)
                .ThenBy(face => face.Z).ThenBy(face => face.Face).First();
        }

        // Source: Survivalcraft/Game/ElectricElement.cs:ElectricElement.Connections
        private void TrackCircuit(ElectricElement root, int hostCircuitStep)
        {
            var pending = new Queue<ElectricElement>();
            var visited = new HashSet<ElectricElement>();
            pending.Enqueue(root);
            while (pending.Count > 0 && visited.Count < 4096)
            {
                ElectricElement element = pending.Dequeue();
                if (!visited.Add(element)) continue;
                m_trackedCircuitCells[GetStableCellFace(element)] = hostCircuitStep;
                foreach (ElectricConnection connection in element.Connections)
                {
                    if (connection.NeighborElectricElement != null &&
                        !visited.Contains(connection.NeighborElectricElement))
                        pending.Enqueue(connection.NeighborElectricElement);
                }
            }
        }

        private void TrackCircuitPoint(Point3 point, int hostCircuitStep)
        {
            for (int face = 0; face < 6; face++)
            {
                ElectricElement element = m_subsystem.GetElectricElement(
                    point.X, point.Y, point.Z, face);
                if (element == null) continue;
                TrackCircuit(element, hostCircuitStep);
                return;
            }
        }

        // Source: Survivalcraft/Game/MemoryBankElectricElement.cs:MemoryBankElectricElement.Simulate
        public int ScheduleHostAction(Point3 point, Action action)
        {
            if (!ScMultiplayer.IsHost || m_subsystem == null || action == null) return 0;
            int hostStep = m_subsystem.CircuitStep + GetCircuitLeadSteps();
            QueueScheduledAction(hostStep, () =>
            {
                TrackCircuitPoint(point, hostStep);
                action();
            });
            return hostStep;
        }

        // Source: Survivalcraft/Game/TruthTableCircuitElectricElement.cs:TruthTableCircuitElectricElement.Simulate
        public bool ScheduleRemoteAction(Point3 point, int hostCircuitStep, Action action)
        {
            if (ScMultiplayer.IsHost || m_subsystem == null || hostCircuitStep <= 0 ||
                action == null) return false;
            Action scheduledAction = () =>
            {
                TrackCircuitPoint(point, hostCircuitStep);
                action();
            };
            if (!m_hasClock)
            {
                QueueAction(m_pendingRemoteActions, hostCircuitStep, scheduledAction);
                return true;
            }
            int localStep = Math.Max(m_subsystem.CircuitStep + 1,
                HostToLocal(hostCircuitStep));
            QueueScheduledAction(localStep, scheduledAction);
            return true;
        }

        private int HostToLocal(int hostStep) => hostStep + m_localCircuitOffset;

        private int LocalToHost(int localStep) => localStep - m_localCircuitOffset;

        private int NetworkStep => ScMultiplayer.client?.Step ?? 0;

        private static int StepDelta(int newer, int older) => unchecked(newer - older);

        // Source: Engine/Window.cs:Window.IsActive
        private static bool ShouldSuspendForWindow(bool windowActive)
        {
            // Source: Survivalcraft/Game/GameManager.cs:GameManager.UpdateProject
            // Host focus is presentation state. If its Project is still advancing, electricity
            // must advance as well. A genuinely stopped Project is detected from CircuitStep
            // progress when publishing fences. Clients still suspend their own local simulation
            // while inactive and validate at a future checkpoint after resuming.
            return !ScMultiplayer.IsHost && !windowActive;
        }

        private int CreateEpoch()
        {
            int value = unchecked((int)(DateTime.UtcNow.Ticks ^ Environment.TickCount));
            return value == int.MinValue ? int.MaxValue : Math.Max(Math.Abs(value), 1);
        }

        // Source: Mod/ScMultiplayer/Message/CircuitSyncMessage.cs:
        // CircuitSyncMessage.WriteFence
        private void SendFence(int targetClientId)
        {
            if (!ScMultiplayer.IsHost || m_subsystem == null || m_epoch <= 0)
                return;
            int hostStep = m_subsystem.CircuitStep;
            double now = Time.RealTime;
            if (hostStep != m_lastObservedHostCircuitStep || m_lastHostProgressRealTime <= 0.0)
            {
                m_lastObservedHostCircuitStep = hostStep;
                m_lastHostProgressRealTime = now;
            }
            bool simulationPaused = now - m_lastHostProgressRealTime > 0.15;
            int safeThrough = simulationPaused
                ? hostStep
                : unchecked(hostStep + Math.Max(CircuitExecutionWindowSteps,
                    GetCircuitLeadSteps() + 5));
            NetworkMessageSender.SendCircuitSync(targetClientId, new CircuitSyncMessage
            {
                Stage = CircuitSyncStage.Fence,
                Epoch = m_epoch,
                TimelineGeneration = Math.Max(m_timelineGeneration, 1),
                ServerStep = NetworkStep,
                HostCircuitStep = hostStep,
                SafeThroughHostCircuitStep = safeThrough,
                LastSequence = m_nextHostSequence,
                RequiredTerrainSequence = m_owner.CircuitTerrainSequence,
                NextHashStep = m_nextHashStep,
                IsPaused = simulationPaused
            });
        }

        // Source: Comms/Comms/Comm.cs:Comm.GetSmoothedRoundTripTime
        private int GetCircuitLeadSteps()
        {
            try
            {
                if (ScMultiplayer.server == null || ScMultiplayer.client == null)
                    return DefaultCircuitLeadSteps;
                var game = ScMultiplayer.server.Games.FirstOrDefault(item =>
                    item.GameID == ScMultiplayer.client.GameID);
                if (game == null || !game.Clients.Any(item => item.ClientID > 0))
                    return 1;
                double maximumRtt = 0.0;
                foreach (var remote in game.Clients.Where(item => item.ClientID > 0))
                {
                    maximumRtt = Math.Max(maximumRtt,
                        ScMultiplayer.server.Peer.Comm.GetSmoothedRoundTripTime(remote.Address));
                }
                if (maximumRtt <= 0.0) return DefaultCircuitLeadSteps;
                if (m_smoothedRtt <= 0.0)
                {
                    m_smoothedRtt = maximumRtt;
                    m_smoothedRttDeviation = 0.0;
                }
                else
                {
                    double deviation = Math.Abs(maximumRtt - m_smoothedRtt);
                    m_smoothedRttDeviation = 0.8 * m_smoothedRttDeviation +
                        0.2 * deviation;
                    m_smoothedRtt = 0.85 * m_smoothedRtt + 0.15 * maximumRtt;
                }
                double jitterMargin = Math.Max(0.01,
                    2.0 * m_smoothedRttDeviation + 0.005);
                double leadSeconds = m_smoothedRtt * 0.5 + jitterMargin +
                    SubsystemElectricity.CircuitStepDuration;
                return MathUtils.Clamp((int)Math.Ceiling(
                    leadSeconds / SubsystemElectricity.CircuitStepDuration),
                    MinimumCircuitLeadSteps, MaximumCircuitLeadSteps);
            }
            catch
            {
                return DefaultCircuitLeadSteps;
            }
        }

        private void Detach()
        {
            if (m_subsystem != null)
            {
                m_subsystem.DetachSynchronizer(this);
            }
            m_subsystem = null;
        }

        // Source: Survivalcraft/Game/RandomGeneratorElectricElement.cs:RandomGeneratorElectricElement.Simulate
        private void EnableDeterministicRandom()
        {
            if (m_subsystem == null) return;
            if (m_randomGeneratorsInitialized) return;
            m_randomGeneratorsInitialized = true;
            foreach (RandomGeneratorElectricElement element in GetElectricElements()
                .OfType<RandomGeneratorElectricElement>())
            {
                CellFace face = GetStableCellFace(element);
                m_subsystem.OnElectricElementBlockGenerated(face.X, face.Y, face.Z);
            }
        }

        private void TrimJournal()
        {
            double now = Time.RealTime;
            while (m_hostJournal.Count > 0 &&
                now - m_hostJournal.Peek().CreatedTime > JournalRetention)
                m_hostJournal.Dequeue();
        }

        private static byte QuantizePressure(float pressure)
        {
            if (pressure < 1f) return 8;
            if (pressure < 2f) return 9;
            if (pressure < 5f) return 10;
            if (pressure < 25f) return 11;
            if (pressure < 100f) return 12;
            if (pressure < 250f) return 13;
            if (pressure < 500f) return 14;
            return 15;
        }

        private static float ExpandPressure(byte level)
        {
            return level switch
            {
                8 => 0.5f,
                9 => 1.5f,
                10 => 3f,
                11 => 10f,
                12 => 50f,
                13 => 150f,
                14 => 300f,
                _ => 600f
            };
        }

        private static void Hash(ref uint hash, int value)
        {
            unchecked
            {
                hash = (hash ^ (byte)value) * 16777619u;
                hash = (hash ^ (byte)(value >> 8)) * 16777619u;
                hash = (hash ^ (byte)(value >> 16)) * 16777619u;
                hash = (hash ^ (byte)(value >> 24)) * 16777619u;
            }
        }
    }

}

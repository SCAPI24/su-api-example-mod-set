using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Protocol.Circuit;
using SuMultiplayer.Terrain;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// Connects circuit synchronization to the live game project. All calls are restricted to
    /// the thread that creates this adapter.
    /// </summary>
    public sealed class CircuitWorldEngine : ICircuitWorldPort
    {
        private readonly TerrainBridge m_terrainBridge;
        private readonly CircuitStateCodec m_codec;
        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly CircuitUseInputSink m_interactionPresentation;
        private CircuitProbeRecorder m_probe;
        private readonly int m_mainThreadId;
        private Project m_project;
        private bool m_topologyDirty = true;
        private bool m_clientElectricitySeedPending = true;
        private bool m_clientSleepScaleFailureLogged;
        private string m_lastPumpTrigger = "none";
        private int m_lastSeededChunks;
        private sealed class AuthoritativeState
        {
            public CircuitElementState State;
            public int HostStep;
        }
        private readonly Dictionary<string, AuthoritativeState> m_authoritativeStates =
            new Dictionary<string, AuthoritativeState>(StringComparer.Ordinal);

        // Source: Mod/SuMultiplayer/Engine/CircuitStateCodec.cs:CircuitStateCodec.CaptureChunk
        public CircuitWorldEngine(TerrainBridge terrainBridge, CircuitStateCodec codec,
            IModParentField parentField, IModParentMethod parentMethod,
            CircuitUseInputSink interactionPresentation)
        {
            m_terrainBridge = terrainBridge ?? throw new ArgumentNullException(nameof(terrainBridge));
            m_codec = codec ?? throw new ArgumentNullException(nameof(codec));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_interactionPresentation = interactionPresentation ??
                throw new ArgumentNullException(nameof(interactionPresentation));
            m_mainThreadId = Thread.CurrentThread.ManagedThreadId;
            m_terrainBridge.ReplicaCellApplied += HandleReplicaCellApplied;
            m_terrainBridge.ReplicaElectricCellChanged += HandleReplicaElectricCellChanged;
            m_terrainBridge.ReplicaElectricTopologyChanged += HandleReplicaElectricTopologyChanged;
        }

        public void SetCircuitProbe(CircuitProbeRecorder probe) => m_probe = probe;
        public CircuitProbeRecorder CurrentCircuitProbe => m_probe;

        public int LoadedCellScanCount => checked((int)m_terrainBridge.ResidentCount);

        public string LastPumpTrigger => m_lastPumpTrigger;

        public int LastSeededChunks => m_lastSeededChunks;

        public bool IsClientReplica
        {
            get
            {
                EnsureMainThread(nameof(IsClientReplica));
                return m_terrainBridge.ClientReplica;
            }
        }

        // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
        // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.Update
        public bool IsWorldAccelerated
        {
            get
            {
                EnsureMainThread(nameof(IsWorldAccelerated));
                Project project = GameManager.Project;
                SubsystemTime time = project?.FindSubsystem<SubsystemTime>(false);
                SubsystemUpdate update = project?.FindSubsystem<SubsystemUpdate>(false);
                return time != null && time.FixedTimeStep.HasValue &&
                    update != null && update.UpdatesPerFrame > 1;
            }
        }

        // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
        // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.Update
        public void NormalizeClientSleepTime()
        {
            EnsureMainThread(nameof(NormalizeClientSleepTime));
            if (!m_terrainBridge.ClientReplica) return;
            Project project = GameManager.Project;
            SubsystemTime time = project?.FindSubsystem<SubsystemTime>(false);
            if (time == null || !time.FixedTimeStep.HasValue ||
                time.GameTimeFactor == 0f) return;

            float fixedStep = time.FixedTimeStep.Value;
            float frameDuration = Time.FrameDuration;
            if (fixedStep <= 0f || frameDuration <= 0f ||
                float.IsNaN(frameDuration) || float.IsInfinity(frameDuration))
                return;
            try
            {
                m_parentField.ModifyParentField(time, "GameTimeFactor",
                    frameDuration / fixedStep, typeof(SubsystemTime));
            }
            catch (Exception error)
            {
                if (m_clientSleepScaleFailureLogged) return;
                m_clientSleepScaleFailureLogged = true;
                ModLog.Warn("[SuMP] Client sleep time scaling could not be normalized: " +
                    error.GetType().Name + ": " + error.Message);
            }
        }

        public bool IsReady
        {
            get
            {
                EnsureMainThread(nameof(IsReady));
                Project project = GameManager.Project;
                return project != null &&
                    project.FindSubsystem<SubsystemElectricity>(false) != null &&
                    project.FindSubsystem<SubsystemTerrain>(false)?.Terrain != null &&
                    project.FindSubsystem<SubsystemGameInfo>(false) != null &&
                    project.FindSubsystem<SubsystemTimeOfDay>(false) != null;
            }
        }

        public event Action<ChunkKey, CellEdit> HostCellChanged
        {
            add
            {
                EnsureMainThread(nameof(HostCellChanged));
                m_terrainBridge.HostCellChanged += value;
            }
            remove
            {
                EnsureMainThread(nameof(HostCellChanged));
                m_terrainBridge.HostCellChanged -= value;
            }
        }

        public event Action<ChunkKey, CellEdit> ReplicaCellApplied
        {
            add
            {
                EnsureMainThread(nameof(ReplicaCellApplied));
                m_replicaCellApplied += value;
            }
            remove
            {
                EnsureMainThread(nameof(ReplicaCellApplied));
                m_replicaCellApplied -= value;
            }
        }

        public event Action<ChunkKey, CellEdit> ReplicaElectricTopologyChanged
        {
            add
            {
                EnsureMainThread(nameof(ReplicaElectricTopologyChanged));
                m_terrainBridge.ReplicaElectricTopologyChanged += value;
            }
            remove
            {
                EnsureMainThread(nameof(ReplicaElectricTopologyChanged));
                m_terrainBridge.ReplicaElectricTopologyChanged -= value;
            }
        }

        public event Action<ChunkKey, CellEdit> ReplicaElectricCellChanged
        {
            add
            {
                EnsureMainThread(nameof(ReplicaElectricCellChanged));
                m_terrainBridge.ReplicaElectricCellChanged += value;
            }
            remove
            {
                EnsureMainThread(nameof(ReplicaElectricCellChanged));
                m_terrainBridge.ReplicaElectricCellChanged -= value;
            }
        }

        public event Action<ChunkKey, CellEdit> HostCellPublished
        {
            add
            {
                EnsureMainThread(nameof(HostCellPublished));
                m_terrainBridge.HostCellPublished += value;
            }
            remove
            {
                EnsureMainThread(nameof(HostCellPublished));
                m_terrainBridge.HostCellPublished -= value;
            }
        }

        private Action<ChunkKey, CellEdit> m_replicaCellApplied;

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.CircuitStep
        // Source: Survivalcraft/Game/SubsystemGameInfo.cs:SubsystemGameInfo.TotalElapsedGameTime
        // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.TimeOfDayOffset
        public bool TryGetClock(out int circuitStep, out double totalElapsedGameTime,
            out double timeOfDayOffset, out bool paused, out string error)
        {
            EnsureMainThread(nameof(TryGetClock));
            circuitStep = 0;
            totalElapsedGameTime = 0;
            timeOfDayOffset = 0;
            paused = false;
            error = null;

            Project project = GameManager.Project;
            if (project == null)
                return Fail("Current game project is unavailable.", out error);
            SubsystemElectricity electricity = project.FindSubsystem<SubsystemElectricity>(false);
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(false);
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            SubsystemTimeOfDay timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(false);
            if (electricity == null || gameInfo == null || time == null || timeOfDay == null)
                return Fail("Circuit or world clock subsystem is unavailable.", out error);

            circuitStep = electricity.CircuitStep;
            totalElapsedGameTime = gameInfo.TotalElapsedGameTime;
            timeOfDayOffset = timeOfDay.TimeOfDayOffset;
            paused = time.GameTimeFactor == 0f;
            return true;
        }

        // Source: Survivalcraft/Game/Terrain.cs:Terrain.AllocatedChunks
        // Source: Mod/SuMultiplayer/Engine/TerrainBridge.cs:TerrainBridge.HasContents
        public int CopyLoadedChunks(List<ChunkKey> target)
        {
            EnsureMainThread(nameof(CopyLoadedChunks));
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Clear();

            Project project = GameManager.Project;
            SubsystemTerrain subsystemTerrain = project?.FindSubsystem<SubsystemTerrain>(false);
            Game.Terrain terrain = subsystemTerrain?.Terrain;
            if (terrain == null)
                throw new InvalidOperationException("Terrain subsystem is unavailable.");

            TerrainChunk[] chunks = terrain.AllocatedChunks;
            for (int i = 0; i < chunks.Length; i++)
            {
                TerrainChunk chunk = chunks[i];
                if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                    continue;
                target.Add(new ChunkKey(chunk.Coords.X, chunk.Coords.Y));
            }
            return target.Count;
        }

        // Source: Survivalcraft/Game/TerrainChunk.cs:TerrainChunk.State
        public bool IsChunkReady(ChunkKey key)
        {
            EnsureMainThread(nameof(IsChunkReady));
            Project project = GameManager.Project;
            TerrainChunk chunk = project?.FindSubsystem<SubsystemTerrain>(false)?.Terrain
                ?.GetChunkAtCoords(key.X, key.Z);
            return chunk != null && ChunkContentStages.IsContentValid((int)chunk.State);
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.GetElectricElement
        public bool AreElementsReady(IList<CircuitElementState> states, out string failure)
        {
            EnsureMainThread(nameof(AreElementsReady));
            if (states == null) throw new ArgumentNullException(nameof(states));
            failure = null;
            Project project = GameManager.Project;
            SubsystemElectricity electricity = project?.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
            {
                failure = "subsystem-unavailable";
                return false;
            }
            foreach (CircuitElementState state in states)
            {
                if (state == null)
                {
                    failure = "state-null";
                    return false;
                }
                if (state.MountingFace > 5)
                {
                    failure = "invalid-face=" + state.MountingFace;
                    return false;
                }
                if (electricity.GetElectricElement(state.X, state.Y, state.Z,
                        (int)state.MountingFace) != null)
                    continue;

                failure = DescribeMissingElement(state);
                return false;
            }
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.GetElectricElement
        // Source: Survivalcraft/Game/Terrain.cs:Terrain.GetCellValueFast
        private static string DescribeMissingElement(CircuitElementState state)
        {
            Project project = GameManager.Project;
            Game.Terrain terrain = project?.FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            string blockType = "unavailable";
            int contents = -1;
            if (terrain != null && terrain.IsCellValid(state.X, state.Y, state.Z))
            {
                TerrainChunk chunk = terrain.GetChunkAtCell(state.X, state.Z);
                if (chunk != null && ChunkContentStages.IsContentValid((int)chunk.State))
                {
                    int value = terrain.GetCellValueFast(state.X, state.Y, state.Z);
                    contents = Game.Terrain.ExtractContents(value);
                    Block[] blocks = BlocksManager.Blocks;
                    if (blocks != null && contents >= 0 && contents < blocks.Length &&
                        blocks[contents] != null)
                        blockType = blocks[contents].GetType().Name;
                }
            }
            return "missing-element x=" + state.X + " y=" + state.Y + " z=" + state.Z +
                " face=" + state.MountingFace + " expectedType=0x" +
                state.ElementType.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) +
                " blockContents=" + contents + " blockType=" + blockType;
        }

        // Source: Mod/SuMultiplayer/Engine/CircuitStateCodec.cs:CircuitStateCodec.CaptureChunk
        public bool TryCaptureChunk(ChunkKey key, List<CircuitElementState> target,
            out string error)
        {
            EnsureMainThread(nameof(TryCaptureChunk));
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Clear();
            error = null;

            Project project = GameManager.Project;
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            TerrainChunk chunk = terrain?.Terrain?.GetChunkAtCoords(key.X, key.Z);
            if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                return Fail("Circuit chunk " + key + " is not loaded with valid contents.",
                    out error);
            SubsystemElectricity electricity = project.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                return Fail("SubsystemElectricity is unavailable.", out error);
            return m_codec.CaptureChunk(electricity, key.X, key.Z, target, out error);
        }

        public bool TryCaptureElements(IList<CircuitElementState> identities,
            List<CircuitElementState> target, out string error)
        {
            EnsureMainThread(nameof(TryCaptureElements));
            if (identities == null) throw new ArgumentNullException(nameof(identities));
            if (target == null) throw new ArgumentNullException(nameof(target));
            Project project = GameManager.Project;
            SubsystemElectricity electricity = project?.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                return Fail("SubsystemElectricity is unavailable.", out error);
            return m_codec.CaptureElements(electricity, identities, target, out error);
        }

        public bool TryCaptureChunks(IList<ChunkKey> keys,
            IDictionary<ChunkKey, List<CircuitElementState>> target, out string error)
        {
            EnsureMainThread(nameof(TryCaptureChunks));
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (target == null) throw new ArgumentNullException(nameof(target));
            Project project = GameManager.Project;
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
                return Fail("Terrain subsystem is unavailable.", out error);
            foreach (ChunkKey key in keys)
            {
                TerrainChunk chunk = terrain.Terrain.GetChunkAtCoords(key.X, key.Z);
                if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                    return Fail("Circuit chunk " + key + " is not loaded with valid contents.",
                        out error);
            }
            SubsystemElectricity electricity = project.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                return Fail("SubsystemElectricity is unavailable.", out error);
            return m_codec.CaptureChunks(electricity, keys, target, out error);
        }

        // Source: Mod/SuMultiplayer/Engine/CircuitStateCodec.cs:CircuitStateCodec.CaptureChunksBestEffort
        public bool TryCaptureChunksBestEffort(IList<ChunkKey> keys,
            IDictionary<ChunkKey, List<CircuitElementState>> target,
            IDictionary<ChunkKey, string> chunkErrors, out string error)
        {
            EnsureMainThread(nameof(TryCaptureChunksBestEffort));
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (chunkErrors == null) throw new ArgumentNullException(nameof(chunkErrors));
            target.Clear();
            chunkErrors.Clear();
            error = null;

            Project project = GameManager.Project;
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
                return Fail("Terrain subsystem is unavailable.", out error);
            var readyChunks = new List<ChunkKey>(keys.Count);
            foreach (ChunkKey key in keys)
            {
                TerrainChunk chunk = terrain.Terrain.GetChunkAtCoords(key.X, key.Z);
                if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                {
                    chunkErrors[key] = "Circuit chunk " + key +
                        " is not loaded with valid contents.";
                    continue;
                }
                readyChunks.Add(key);
            }

            if (readyChunks.Count == 0) return true;
            SubsystemElectricity electricity = project.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                return Fail("SubsystemElectricity is unavailable.", out error);
            long captureStarted = m_probe?.Start() ?? 0;
            if (!m_codec.CaptureChunksBestEffort(electricity, readyChunks, target,
                    chunkErrors, out error))
            {
                if (m_probe != null)
                    m_probe.Record("electric-table-capture", captureStarted,
                        "best-effort", readyChunks.Count, 0, 0, 0, 0);
                target.Clear();
                return false;
            }
            if (m_probe != null)
                m_probe.Record("electric-table-capture", captureStarted,
                    "best-effort", readyChunks.Count, 0, 0,
                    target.Values.Sum(states => states.Count), 0);
            return true;
        }

        // Source: Mod/SuMultiplayer/Engine/CircuitStateCodec.cs:CircuitStateCodec.CaptureChunkTopology
        public bool TryCaptureChunkTopology(ChunkKey key, List<CircuitElementState> target,
            out string error)
        {
            EnsureMainThread(nameof(TryCaptureChunkTopology));
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Clear();
            error = null;

            Project project = GameManager.Project;
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            TerrainChunk chunk = terrain?.Terrain?.GetChunkAtCoords(key.X, key.Z);
            if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                return Fail("Circuit chunk " + key + " is not loaded with valid contents.",
                    out error);
            SubsystemElectricity electricity = project.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                return Fail("SubsystemElectricity is unavailable.", out error);
            return m_codec.CaptureChunkTopology(electricity, key.X, key.Z, target, out error);
        }

        // Source: Mod/SuMultiplayer/Engine/CircuitStateCodec.cs:CircuitStateCodec.Apply
        public bool Apply(CircuitElementState state, int hostStep, out string error)
        {
            EnsureMainThread(nameof(Apply));
            error = null;
            if (!m_terrainBridge.ClientReplica)
                return Fail("Circuit runtime state may only be applied to a client replica.",
                    out error);
            if (state == null)
                return Fail("Circuit state is null.", out error);

            Project project = GameManager.Project;
            SubsystemElectricity electricity = project?.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                return Fail("SubsystemElectricity is unavailable.", out error);
            bool applied = m_codec.Apply(electricity, state, hostStep, out error);
            if (applied && !CircuitStateCodec.IsDisplayElementType(state.ElementType))
            {
                m_authoritativeStates[GetElementIdentity(state)] = new AuthoritativeState
                {
                    State = state.Clone(),
                    HostStep = hostStep
                };
            }
            return applied;
        }

        public bool ApplyOperationEvent(CircuitEvent circuitEvent, out string error)
        {
            EnsureMainThread(nameof(ApplyOperationEvent));
            if (circuitEvent == null) throw new ArgumentNullException(nameof(circuitEvent));
            SubsystemElectricity electricity = GameManager.Project?
                .FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                return Fail("SubsystemElectricity is unavailable.", out error);
            return m_codec.ApplyOperationEvent(electricity, circuitEvent, out error);
        }

        public bool AlignClientCircuitStep(int hostStep, bool restoreAuthoritativeStates,
            out string error)
        {
            EnsureMainThread(nameof(AlignClientCircuitStep));
            error = null;
            if (hostStep < 0)
                return Fail("Host circuit step must be non-negative.", out error);
            if (!m_terrainBridge.ClientReplica)
                return Fail("Only a client replica can align its circuit step.", out error);

            SubsystemElectricity electricity = GameManager.Project?
                .FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                return Fail("SubsystemElectricity is unavailable.", out error);
            try
            {
                if (!restoreAuthoritativeStates)
                    m_authoritativeStates.Clear();
                ClearClientSimulationSchedule(electricity);
                m_parentField.ModifyParentField(electricity, "CircuitStep", hostStep,
                    typeof(SubsystemElectricity));
                m_parentField.ModifyParentField(electricity, "FrameStartCircuitStep", hostStep,
                    typeof(SubsystemElectricity));
                m_parentField.ModifyParentField(electricity, "m_remainingSimulationTime", 0f,
                    typeof(SubsystemElectricity));
                if (restoreAuthoritativeStates)
                {
                    SubsystemTerrain terrainSubsystem = GameManager.Project?
                        .FindSubsystem<SubsystemTerrain>(false);
                    Game.Terrain terrain = terrainSubsystem?.Terrain;
                    foreach (KeyValuePair<string, AuthoritativeState> pair in
                        m_authoritativeStates.ToArray())
                    {
                        CircuitElementState state = pair.Value.State;
                        TerrainChunk chunk = terrain?.GetChunkAtCell(state.X, state.Z);
                        if (chunk == null ||
                            !ChunkContentStages.IsContentValid((int)chunk.State))
                        {
                            m_authoritativeStates.Remove(pair.Key);
                            continue;
                        }
                        if (electricity.GetElectricElement(state.X, state.Y,
                                state.Z, (int)state.MountingFace) == null)
                        {
                            m_authoritativeStates.Remove(pair.Key);
                            continue;
                        }
                        if (!m_codec.Apply(electricity, pair.Value.State, hostStep,
                                out string applyError))
                            return Fail("Client circuit step alignment could not restore " +
                                pair.Key + ": " + applyError, out error);
                        pair.Value.HostStep = hostStep;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                return Fail("Client circuit step alignment failed: " + e.GetType().Name +
                    ": " + e.Message, out error);
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.UpdateElectricElements
        public string ReconcileSnapshot(ChunkKey key, IList<CircuitElementState> expected,
            int hostStep)
        {
            EnsureMainThread(nameof(ReconcileSnapshot));
            m_lastPumpTrigger = "full-snapshot-reconcile:" + key;
            if (expected == null) return "Snapshot state list is null.";
            if (!m_terrainBridge.ClientReplica)
                return "Full snapshot reconciliation is only valid on a client replica.";

            long started = m_probe?.Start() ?? 0;
            Pump();
            m_probe?.Record("reconcile-pump", started,
                m_lastPumpTrigger, 1, 0, 0, 0, 0);
            var local = new List<CircuitElementState>();
            started = m_probe?.Start() ?? 0;
            bool topologyCaptured = TryCaptureChunkTopology(key, local, out string captureError);
            m_probe?.Record("reconcile-topology-capture", started,
                "full-snapshot", 1, 0, 0, local.Count, 0);
            if (!topologyCaptured)
                return "Client topology capture failed: " + captureError;

            started = m_probe?.Start() ?? 0;
            var localTypes = new Dictionary<string, uint>(local.Count, StringComparer.Ordinal);
            foreach (CircuitElementState state in local)
            {
                string identity = GetElementIdentity(state);
                if (!localTypes.TryAdd(identity, state.ElementType))
                {
                    m_probe?.Record("reconcile-topology-verify", started,
                        "full-snapshot", 1, 0, 0, local.Count, 0);
                    return "Client topology contains a duplicate circuit element at " + identity + ".";
                }
            }
            if (expected.Count != localTypes.Count)
            {
                m_probe?.Record("reconcile-topology-verify", started,
                    "full-snapshot", 1, 0, 0, expected.Count, 0);
                return "Client topology element count " + localTypes.Count +
                    " does not match host snapshot count " + expected.Count + ".";
            }

            foreach (CircuitElementState state in expected)
            {
                string identity = GetElementIdentity(state);
                if (!localTypes.TryGetValue(identity, out uint localType) ||
                    localType != state.ElementType)
                {
                    m_probe?.Record("reconcile-topology-verify", started,
                        "full-snapshot", 1, 0, 0, expected.Count, 0);
                    return "Client topology does not match host snapshot at " + identity + ".";
                }
            }
            m_probe?.Record("reconcile-topology-verify", started,
                "full-snapshot", 1, 0, 0, expected.Count, 0);
            started = m_probe?.Start() ?? 0;
            RemoveCachedStatesForChunk(key);
            foreach (CircuitElementState state in expected)
                if (!Apply(state, hostStep, out string applyError)) return applyError;
            m_probe?.Record("reconcile-state-apply", started,
                "full-snapshot", 1, 0, 0, expected.Count, 0);
            return null;
        }

        private static string GetElementIdentity(CircuitElementState state) =>
            state.X + ":" + state.Y + ":" + state.Z + ":" + state.MountingFace;

        // Source: Survivalcraft/Game/SubsystemGameInfo.cs:SubsystemGameInfo.TotalElapsedGameTime
        // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.TimeOfDayOffset
        // Source: EntitySystem/SuAPI/IModParentField.cs:IModParentField.ModifyParentField
        public bool ApplyClock(double totalElapsedGameTime, double timeOfDayOffset,
            out string error)
        {
            EnsureMainThread(nameof(ApplyClock));
            error = null;
            if (!m_terrainBridge.ClientReplica)
                return Fail("World clock may only be applied to a client replica.", out error);
            if (double.IsNaN(totalElapsedGameTime) || double.IsInfinity(totalElapsedGameTime) ||
                double.IsNaN(timeOfDayOffset) || double.IsInfinity(timeOfDayOffset))
                return Fail("World clock values must be finite.", out error);

            Project project = GameManager.Project;
            SubsystemGameInfo gameInfo = project?.FindSubsystem<SubsystemGameInfo>(false);
            SubsystemTimeOfDay timeOfDay = project?.FindSubsystem<SubsystemTimeOfDay>(false);
            if (gameInfo == null || timeOfDay == null)
                return Fail("World clock subsystem is unavailable.", out error);

            try
            {
                m_parentField.ModifyParentField(gameInfo, "<TotalElapsedGameTime>k__BackingField",
                    totalElapsedGameTime, typeof(SubsystemGameInfo));
                if (gameInfo.TotalElapsedGameTime != totalElapsedGameTime)
                    return Fail("SubsystemGameInfo.TotalElapsedGameTime did not accept the host value.",
                        out error);

                timeOfDay.TimeOfDayOffset = timeOfDayOffset;
                if (timeOfDay.TimeOfDayOffset != timeOfDayOffset)
                    return Fail("SubsystemTimeOfDay.TimeOfDayOffset did not accept the host value.",
                        out error);
                return true;
            }
            catch (Exception e)
            {
                return Fail("World clock apply failed: " + e.GetType().Name + ": " + e.Message,
                    out error);
            }
        }

        public uint ComputeHash(IEnumerable<CircuitElementState> states)
        {
            EnsureMainThread(nameof(ComputeHash));
            return m_codec.ComputeHash(states);
        }

        public uint ComputeCapturedHash(IList<CircuitElementState> states)
        {
            EnsureMainThread(nameof(ComputeCapturedHash));
            return m_codec.ComputeCapturedHash(states);
        }

        public void RefreshClientDisplayElements()
        {
            EnsureMainThread(nameof(RefreshClientDisplayElements));
            if (!m_terrainBridge.ClientReplica) return;
            Project project = GameManager.Project;
            SubsystemElectricity electricity = project?.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
                throw new InvalidOperationException(
                    "SubsystemElectricity is unavailable while refreshing client display elements.");
            m_codec.RefreshDisplayElements(electricity);
        }

        public void SetDeterministicRandomSeed(uint seed, bool hostAuthority)
        {
            EnsureMainThread(nameof(SetDeterministicRandomSeed));
            CircuitRandomSequence.Configure(seed, hostAuthority);
        }

        // Source: Survivalcraft/Game/Terrain.cs:Terrain.GetCellValue
        public bool TryGetCellValue(int x, int y, int z, out int value)
        {
            EnsureMainThread(nameof(TryGetCellValue));
            value = 0;
            Project project = GameManager.Project;
            Game.Terrain terrain = project?.FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            if (terrain == null || !terrain.IsCellValid(x, y, z)) return false;
            TerrainChunk chunk = terrain.GetChunkAtCell(x, z);
            if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                return false;
            value = terrain.GetCellValueFast(x, y, z);
            return true;
        }

        public string DescribeDiagnostics()
        {
            EnsureMainThread(nameof(DescribeDiagnostics));
            return m_codec.DescribeDiagnostics();
        }

        // Source: Mod/SuMultiplayer/Engine/TerrainBridge.cs:TerrainBridge.ApplyCells
        // Source: Mod/SuMultiplayer/Engine/CircuitUseInputSink.ApplyAuthoritativeCell
        private void HandleReplicaCellApplied(ChunkKey key, CellEdit edit)
        {
            EnsureMainThread(nameof(HandleReplicaCellApplied));
            int x = key.X * 16 + edit.X;
            int y = edit.Y;
            int z = key.Z * 16 + edit.Z;
            // A terrain stream can apply many non-circuit cells in the same chunk
            // while a rejoined client is rebuilding its replica. Invalidating the
            // whole chunk here discards authoritative LED/display states that are
            // still valid and leaves them dark until another full repair arrives.
            // Only the edited cell can have had its electric element replaced.
            RemoveCachedStatesForCell(x, y, z);
            if (!TryGetCellValue(x, y, z, out int value))
                throw new InvalidOperationException("Applied replica cell is unavailable at (" +
                    x + "," + y + "," + z + ").");
            TerrainResyncDiagnosticWindow diagnostics = m_terrainBridge.ResyncDiagnostics;
            bool measureRollback = diagnostics != null && diagnostics.IsActive &&
                !m_interactionPresentation.HasPendingOperationAt(x, y, z);
            long rollbackStarted = measureRollback ? Stopwatch.GetTimestamp() : 0;
            for (int face = 0; face < 6; face++)
                m_interactionPresentation.ApplyAuthoritativeCell(x, y, z, face, value);
            if (measureRollback)
                diagnostics.RecordNoPendingSixFaceRollback(Stopwatch.GetTimestamp() - rollbackStarted);
            m_replicaCellApplied?.Invoke(key, edit);
        }

        private void HandleReplicaElectricTopologyChanged(ChunkKey key, CellEdit edit)
        {
            EnsureMainThread(nameof(HandleReplicaElectricTopologyChanged));
            m_topologyDirty = true;
            m_lastPumpTrigger = "terrain-topology-change:" + key;
        }

        private void HandleReplicaElectricCellChanged(ChunkKey key, CellEdit edit)
        {
            EnsureMainThread(nameof(HandleReplicaElectricCellChanged));
            int x = key.X * 16 + edit.X;
            int z = key.Z * 16 + edit.Z;
            Project project = GameManager.Project;
            SubsystemElectricity electricity = project?.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null) return;
            bool elementReady = false;
            for (int face = 0; face < 6; face++)
            {
                if (electricity.GetElectricElement(x, edit.Y, z, face) != null)
                {
                    elementReady = true;
                    break;
                }
            }
            if (!elementReady)
            {
                m_topologyDirty = true;
                m_lastPumpTrigger = "electric-element-missing:" + key;
            }
        }


        public bool IsElectricTopologyCell(ChunkKey key, CellEdit edit)
        {
            EnsureMainThread(nameof(IsElectricTopologyCell));
            int x = key.X * 16 + edit.X;
            int z = key.Z * 16 + edit.Z;
            if (!TryGetCellValue(x, edit.Y, z, out int value))
                throw new InvalidOperationException("Cannot inspect circuit topology at unavailable cell (" +
                    x + "," + edit.Y + "," + z + ").");
            int contents = Game.Terrain.ExtractContents(value);
            Block[] blocks = BlocksManager.Blocks;
            if (blocks == null || contents < 0 || contents >= blocks.Length)
                throw new InvalidOperationException("Block database is unavailable while classifying a circuit topology edit.");
            return blocks[contents] is IElectricElementBlock;
        }

        public bool IsElectricTopologyChange(int oldValue, int newValue, int x, int y, int z)
        {
            EnsureMainThread(nameof(IsElectricTopologyChange));
            return m_terrainBridge.IsElectricTopologyChange(oldValue, newValue, x, y, z);
        }

        public int GetCellValue(int x, int y, int z)
        {
            EnsureMainThread(nameof(GetCellValue));
            if (!TryGetCellValue(x, y, z, out int value))
                throw new InvalidOperationException("Cell is unavailable at " + x + "," + y + "," + z + ".");
            return value;
        }

        public bool IsElectricTopologyElementAt(int x, int y, int z)
        {
            EnsureMainThread(nameof(IsElectricTopologyElementAt));
            int value = GetCellValue(x, y, z);
            int contents = Game.Terrain.ExtractContents(value);
            Block[] blocks = BlocksManager.Blocks;
            if (blocks == null || contents < 0 || contents >= blocks.Length)
                throw new InvalidOperationException("Block database is unavailable while reading circuit cell.");
            return blocks[contents] is IElectricElementBlock;
        }

        public bool TryCaptureChunkTopologyValues(ChunkKey key,
            IDictionary<string, int> target, out string error)
        {
            EnsureMainThread(nameof(TryCaptureChunkTopologyValues));
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Clear();
            error = null;
            Project project = GameManager.Project;
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            TerrainChunk chunk = terrain?.Terrain?.GetChunkAtCoords(key.X, key.Z);
            if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
            {
                error = "Circuit chunk " + key + " is not loaded with valid contents.";
                return false;
            }
            SubsystemElectricity electricity = project.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null)
            {
                error = "SubsystemElectricity is unavailable.";
                return false;
            }
            return m_codec.CaptureChunkTopologyValues(electricity, key.X, key.Z,
                target, out error);
        }

        public void ClearAuthoritativeStatesForChunk(ChunkKey key)
        {
            EnsureMainThread(nameof(ClearAuthoritativeStatesForChunk));
            foreach (KeyValuePair<string, AuthoritativeState> pair in
                m_authoritativeStates.Where(item =>
                    (item.Value.State.X >> 4) == key.X &&
                    (item.Value.State.Z >> 4) == key.Z).ToArray())
                m_authoritativeStates.Remove(pair.Key);
        }

        public void MarkClientCircuitTopologyDirty(ChunkKey key)
        {
            EnsureMainThread(nameof(MarkClientCircuitTopologyDirty));
            if (!m_terrainBridge.ClientReplica)
                throw new InvalidOperationException(
                    "Client circuit topology cannot be dirtied on the host.");
            m_topologyDirty = true;
            m_lastPumpTrigger = "circuit-operation-topology:" + key;
        }

        public void ResetClientCircuitState()
        {
            EnsureMainThread(nameof(ResetClientCircuitState));
            m_authoritativeStates.Clear();
            m_topologyDirty = true;
            m_clientElectricitySeedPending = true;
            m_lastPumpTrigger = "client-reset";
        }

        public void Pump()
        {
            EnsureMainThread(nameof(Pump));
            if (!m_terrainBridge.ClientReplica)
            {
                m_authoritativeStates.Clear();
                m_topologyDirty = true;
                m_project = null;
                return;
            }
            Project project = GameManager.Project;
            if (!ReferenceEquals(m_project, project))
            {
                m_project = project;
                m_authoritativeStates.Clear();
                m_topologyDirty = true;
                m_clientElectricitySeedPending = true;
                m_lastPumpTrigger = "project-change";
            }
            if (!m_topologyDirty)
                return;
            SubsystemElectricity electricity = project?.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null) return;
            try
            {
                bool seededElectricity = true;
                if (m_clientElectricitySeedPending)
                {
                    long seedStarted = m_probe?.Start() ?? 0;
                    seededElectricity = SeedLoadedElectricityElements(project, electricity,
                        out m_lastSeededChunks);
                    if (m_probe != null)
                        m_probe.Record("seed-loaded-electricity", seedStarted,
                            m_lastPumpTrigger, m_lastSeededChunks, 0, 0, 0, 0);
                }
                long rebuildStarted = m_probe?.Start() ?? 0;
                m_parentMethod.InvokeParentMethod(electricity, "UpdateElectricElements",
                    Type.EmptyTypes);
                if (m_probe != null)
                    m_probe.Record("topology-rebuild", rebuildStarted, m_lastPumpTrigger,
                        LoadedCellScanCount, 0, 0, 0, 0);
                // The engine rebuild schedules newly added elements and their neighbors.
                // Keep those kicks while replaying cached authoritative voltages.
                long replayStarted = m_probe?.Start() ?? 0;
                ReapplyAuthoritativeStates(electricity);
                if (m_probe != null)
                    m_probe.Record("authoritative-replay", replayStarted, m_lastPumpTrigger,
                        LoadedCellScanCount, 0, 0, m_authoritativeStates.Count, 0);
                m_codec.RefreshDisplayElements(electricity);
                m_topologyDirty = false;
                if (seededElectricity)
                    m_clientElectricitySeedPending = false;
                m_lastPumpTrigger = "none";
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Client electric topology refresh failed without running circuit simulation.",
                    error);
            }
        }

        private static bool SeedLoadedElectricityElements(Project project,
            SubsystemElectricity electricity, out int scannedChunks)
        {
            scannedChunks = 0;
            SubsystemTerrain terrainSubsystem = project?.FindSubsystem<SubsystemTerrain>(false);
            Game.Terrain terrain = terrainSubsystem?.Terrain;
            Block[] blocks = BlocksManager.Blocks;
            if (terrain == null || blocks == null)
                throw new InvalidOperationException(
                    "Client electricity cannot seed before terrain and blocks are ready.");
            bool foundValidChunk = false;
            foreach (TerrainChunk chunk in terrain.AllocatedChunks)
            {
                if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                    continue;
                foundValidChunk = true;
                scannedChunks++;
                for (int x = chunk.Origin.X; x < chunk.Origin.X + 16; x++)
                    for (int z = chunk.Origin.Y; z < chunk.Origin.Y + 16; z++)
                        for (int y = 0; y < 256; y++)
                        {
                            int contents = Game.Terrain.ExtractContents(
                                terrain.GetCellValueFast(x, y, z));
                            if (contents >= 0 && contents < blocks.Length &&
                                blocks[contents] is IElectricElementBlock)
                                electricity.OnElectricElementBlockGenerated(x, y, z);
                        }
            }
            return foundValidChunk;
        }

        private void ReapplyAuthoritativeStates(SubsystemElectricity electricity)
        {
            foreach (KeyValuePair<string, AuthoritativeState> pair in
                m_authoritativeStates.ToArray())
            {
                CircuitElementState state = pair.Value.State;
                ElectricElement element = state.MountingFace <= 5
                    ? electricity.GetElectricElement(state.X, state.Y, state.Z,
                        (int)state.MountingFace)
                    : null;
                if (element == null)
                {
                    m_authoritativeStates.Remove(pair.Key);
                    continue;
                }
                if (!m_codec.Apply(electricity, state, pair.Value.HostStep,
                    out string error))
                    throw new InvalidOperationException(
                        "Client authoritative circuit state replay failed for " +
                        pair.Key + ": " + error);
            }
        }

        private void RemoveCachedStatesForChunk(ChunkKey key)
        {
            foreach (KeyValuePair<string, AuthoritativeState> pair in
                m_authoritativeStates.Where(item =>
                    (item.Value.State.X >> 4) == key.X &&
                    (item.Value.State.Z >> 4) == key.Z).ToArray())
                m_authoritativeStates.Remove(pair.Key);
        }

        // Source: Mod/SuMultiplayer/Engine/CircuitWorldEngine.cs:HandleReplicaCellApplied
        private void RemoveCachedStatesForCell(int x, int y, int z)
        {
            foreach (KeyValuePair<string, AuthoritativeState> pair in
                m_authoritativeStates.Where(item =>
                    item.Value.State.X == x && item.Value.State.Y == y &&
                    item.Value.State.Z == z).ToArray())
                m_authoritativeStates.Remove(pair.Key);
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:m_futureSimulateLists
        // Source: Survivalcraft/Game/SubsystemElectricity.cs:m_nextStepSimulateList
        private void ClearClientSimulationSchedule(SubsystemElectricity electricity)
        {
            var future = m_parentField.GetParentField<object>(electricity,
                "m_futureSimulateLists", typeof(SubsystemElectricity));
            if (!(future is System.Collections.IDictionary futureDictionary))
                throw new InvalidOperationException(
                    "SubsystemElectricity.m_futureSimulateLists is unavailable on the client replica.");
            futureDictionary.Clear();
            m_parentField.ModifyParentField(electricity, "m_nextStepSimulateList", null,
                typeof(SubsystemElectricity));
        }

        private void EnsureMainThread(string operation)
        {
            if (Thread.CurrentThread.ManagedThreadId != m_mainThreadId)
                throw new InvalidOperationException(operation + " must run on the circuit world main thread.");
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}

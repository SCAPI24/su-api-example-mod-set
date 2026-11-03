using Engine;
using Game;
using SuAPI;
using SuMultiplayer.Protocol.Circuit;
using SuMultiplayer.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SuMultiplayer.Engine
{
    // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.CaptureStateRecords
    // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_electricElements
    public sealed class CircuitStateCodec
    {
        private const uint FnvOffsetBasis = 2166136261u;
        private const uint FnvPrime = 16777619u;
        private const int MaximumDelayHistoryEntries = 300;
        private const int MaximumScheduledSteps = 10000;

        private sealed class DelayHistoryComparer : IComparer<KeyValuePair<int, float>>
        {
            public int Compare(KeyValuePair<int, float> left,
                KeyValuePair<int, float> right) => left.Key.CompareTo(right.Key);
        }

        private sealed class ScheduledVoltageComparer : IComparer<CircuitScheduledVoltage>
        {
            public int Compare(CircuitScheduledVoltage left, CircuitScheduledVoltage right)
            {
                int result = left.RemainingSteps.CompareTo(right.RemainingSteps);
                return result != 0 ? result : left.VoltageLevel.CompareTo(right.VoltageLevel);
            }
        }

        private static readonly IComparer<KeyValuePair<int, float>> s_delayHistoryComparer =
            new DelayHistoryComparer();
        private static readonly IComparer<CircuitScheduledVoltage> s_scheduledVoltageComparer =
            new ScheduledVoltageComparer();

        private static readonly uint s_sevenSegmentTypeCode =
            GetStableTypeCode(typeof(SevenSegmentDisplayElectricElement));
        private static readonly uint s_fourLedTypeCode =
            GetStableTypeCode(typeof(FourLedElectricElement));
        private static readonly uint s_oneLedTypeCode =
            GetStableTypeCode(typeof(OneLedElectricElement));
        private static readonly uint s_ledTypeCode =
            GetStableTypeCode(typeof(LedElectricElement));
        private static readonly uint s_multicoloredLedTypeCode =
            GetStableTypeCode(typeof(MulticoloredLedElectricElement));

        private readonly IModParentField m_parentField;
        private readonly List<KeyValuePair<int, float>> m_delayHistoryScratch =
            new List<KeyValuePair<int, float>>(MaximumDelayHistoryEntries);
        private readonly List<CircuitScheduledVoltage> m_scheduledVoltageScratch =
            new List<CircuitScheduledVoltage>(MaximumDelayHistoryEntries);
        private long m_sevenSegmentCaptures;
        private long m_sevenSegmentApplies;
        private string m_lastSevenSegmentCapture = "none";
        private string m_lastSevenSegmentApply = "none";

        // Source: EntitySystem/SuAPI/IModParentField.cs:IModParentField
        public CircuitStateCodec(IModParentField parentField)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
        }

        // Source: Survivalcraft/Game/SevenSegmentDisplayElectricElement.cs:Simulate
        public string DescribeDiagnostics() => "sevenSegment(captured=" +
            m_sevenSegmentCaptures + " last=" + m_lastSevenSegmentCapture +
            " applied=" + m_sevenSegmentApplies + " last=" + m_lastSevenSegmentApply + ")";

        public static int CountSevenSegmentStates(IEnumerable<CircuitElementState> states)
        {
            if (states == null) throw new ArgumentNullException(nameof(states));
            return states.Count(state => state != null && state.ElementType == s_sevenSegmentTypeCode);
        }

        public static uint SevenSegmentTypeCode =>
            s_sevenSegmentTypeCode;

        public static bool IsDisplayElementType(uint typeCode) =>
            typeCode == s_sevenSegmentTypeCode ||
            typeCode == s_fourLedTypeCode ||
            typeCode == s_oneLedTypeCode ||
            typeCode == s_ledTypeCode ||
            typeCode == s_multicoloredLedTypeCode;

        // Source: Survivalcraft/Game/SevenSegmentDisplayElectricElement.cs:Simulate
        // Source: Survivalcraft/Game/FourLedElectricElement.cs:Simulate
        // Source: Survivalcraft/Game/OneLedElectricElement.cs:Simulate
        // Source: Survivalcraft/Game/LedElectricElement.cs:Simulate
        // Source: Survivalcraft/Game/MulticoloredLedElectricElement.cs:Simulate
        // Display elements only read their neighbor outputs and update their own glow cache.
        public void RefreshDisplayElements(SubsystemElectricity subsystem)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            foreach (ElectricElement element in GetAllElements(subsystem))
                if (IsDisplayElement(element))
                    element.Simulate();
        }

        // Source: Survivalcraft/Game/SevenSegmentDisplayElectricElement.cs:Simulate
        // The client replica does not advance the electricity solver. Reapply only the
        // presentation cache when another engine callback rebuilt the electric topology.
        public bool RefreshSevenSegmentPresentation(SubsystemElectricity subsystem,
            CircuitElementState state, out string error)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            if (state == null) throw new ArgumentNullException(nameof(state));
            error = null;
            try
            {
                ElectricElement element = subsystem.GetElectricElement(state.X, state.Y, state.Z,
                    checked((int)state.MountingFace));
                if (!(element is SevenSegmentDisplayElectricElement))
                    throw new InvalidOperationException("Authoritative state is not a seven-segment display.");
                if (GetStableTypeCode(element.GetType()) != state.ElementType)
                    throw new InvalidOperationException("Seven-segment element type code does not match the live element.");
                RefreshPresentation(element, state.VoltageLevel);
                return true;
            }
            catch (Exception ex)
            {
                error = "Seven-segment presentation refresh failed at (" + state.X + "," +
                    state.Y + "," + state.Z + ") face " + state.MountingFace + ": " + ex.Message;
                return false;
            }
        }

        public bool RefreshSevenSegmentPresentationFromLiveVoltage(
            SubsystemElectricity subsystem, CircuitElementState identity, out string error)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            error = null;
            try
            {
                ElectricElement element = subsystem.GetElectricElement(identity.X,
                    identity.Y, identity.Z, checked((int)identity.MountingFace));
                if (!(element is SevenSegmentDisplayElectricElement))
                    throw new InvalidOperationException("Authoritative state is not a seven-segment display.");
                if (GetStableTypeCode(element.GetType()) != identity.ElementType)
                    throw new InvalidOperationException("Seven-segment element type code does not match the live element.");
                Type declaringType = FindFieldDeclaringType(element.GetType(), "m_voltage");
                if (declaringType == null)
                    throw new InvalidOperationException("Seven-segment element has no m_voltage field.");
                int level = QuantizeVoltage(GetField<float>(element, "m_voltage", declaringType));
                RefreshPresentation(element, level);
                return true;
            }
            catch (Exception ex)
            {
                error = "Live seven-segment presentation refresh failed at (" + identity.X + "," +
                    identity.Y + "," + identity.Z + ") face " + identity.MountingFace +
                    ": " + ex.Message;
                return false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        // This API is called on the game thread because it reads and mutates live circuit state.
        public bool CaptureChunk(SubsystemElectricity subsystem, int chunkX, int chunkZ,
            List<CircuitElementState> states, out string error)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            if (states == null) throw new ArgumentNullException(nameof(states));
            states.Clear();
            error = null;

            try
            {
                var elements = GetElements(subsystem);
                foreach (ElectricElement element in elements)
                {
                    // Piston elements are replicated by the piston module, but they must
                    // still be ignored before any type validation so one piston cannot
                    // abort capture of the whole circuit scope.
                    if (element is PistonElectricElement)
                        continue;

                    if (element is WireDomainElectricElement)
                    {
                        foreach (CellFace wireFace in element.CellFaces)
                        {
                            if ((wireFace.X >> 4) == chunkX && (wireFace.Z >> 4) == chunkZ)
                                states.Add(CaptureElement(subsystem, element, wireFace));
                        }
                        continue;
                    }

                    CellFace face = GetStableCellFace(element);
                    if ((face.X >> 4) != chunkX || (face.Z >> 4) != chunkZ)
                        continue;

                    CircuitElementState state = CaptureElement(subsystem, element, face);
                    states.Add(state);
                }

                states.Sort(CompareStates);
                return true;
            }
            catch (Exception ex)
            {
                states.Clear();
                error = "Circuit chunk capture failed: " + ex.Message;
                return false;
            }
        }

        public bool CaptureChunkTopologyValues(SubsystemElectricity subsystem, int chunkX,
            int chunkZ, IDictionary<string, int> values, out string error)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            if (values == null) throw new ArgumentNullException(nameof(values));
            values.Clear();
            error = null;
            try
            {
                foreach (ElectricElement element in GetAllElements(subsystem))
                {
                    foreach (CellFace face in element.CellFaces)
                    {
                        if ((face.X >> 4) != chunkX || (face.Z >> 4) != chunkZ)
                            continue;
                        int value = subsystem.SubsystemTerrain.Terrain.GetCellValue(face.X,
                            face.Y, face.Z);
                        values[face.X + ":" + face.Y + ":" + face.Z] = value;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                values.Clear();
                error = "Circuit topology value capture failed: " + ex.Message;
                return false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_electricElements
        public bool CaptureChunks(SubsystemElectricity subsystem, IList<ChunkKey> chunks,
            IDictionary<ChunkKey, List<CircuitElementState>> statesByChunk, out string error)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            if (chunks == null) throw new ArgumentNullException(nameof(chunks));
            if (statesByChunk == null) throw new ArgumentNullException(nameof(statesByChunk));
            statesByChunk.Clear();
            error = null;

            try
            {
                foreach (ChunkKey chunk in chunks)
                    if (!statesByChunk.ContainsKey(chunk))
                        statesByChunk.Add(chunk, new List<CircuitElementState>());

                foreach (ElectricElement element in GetElements(subsystem))
                {
                    if (element is PistonElectricElement)
                        continue;

                    if (element is WireDomainElectricElement)
                    {
                        foreach (CellFace wireFace in element.CellFaces)
                        {
                            var wireKey = new ChunkKey(wireFace.X >> 4, wireFace.Z >> 4);
                            if (statesByChunk.TryGetValue(wireKey, out List<CircuitElementState> wireStates))
                                wireStates.Add(CaptureElement(subsystem, element, wireFace));
                        }
                        continue;
                    }

                    CellFace face = GetStableCellFace(element);
                    var key = new ChunkKey(face.X >> 4, face.Z >> 4);
                    if (!statesByChunk.TryGetValue(key, out List<CircuitElementState> states))
                        continue;
                    states.Add(CaptureElement(subsystem, element, face));
                }

                foreach (List<CircuitElementState> states in statesByChunk.Values)
                    states.Sort(CompareStates);
                return true;
            }
            catch (Exception ex)
            {
                statesByChunk.Clear();
                error = "Circuit multi-chunk capture failed: " + ex.Message;
                return false;
            }
        }

        // Source: Mod/SuMultiplayer/Modules/CircuitSyncModule.cs:CircuitSyncModule.SendClientHashes
        // Capture the electric element table once, while isolating element failures to their chunk.
        public bool CaptureChunksBestEffort(SubsystemElectricity subsystem, IList<ChunkKey> chunks,
            IDictionary<ChunkKey, List<CircuitElementState>> statesByChunk,
            IDictionary<ChunkKey, string> chunkErrors, out string error)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            if (chunks == null) throw new ArgumentNullException(nameof(chunks));
            if (statesByChunk == null) throw new ArgumentNullException(nameof(statesByChunk));
            if (chunkErrors == null) throw new ArgumentNullException(nameof(chunkErrors));
            statesByChunk.Clear();
            chunkErrors.Clear();
            error = null;

            try
            {
                foreach (ChunkKey chunk in chunks)
                    if (!statesByChunk.ContainsKey(chunk))
                        statesByChunk.Add(chunk, new List<CircuitElementState>());

                foreach (ElectricElement element in GetElements(subsystem))
                {
                    if (element is PistonElectricElement) continue;
                    if (element is WireDomainElectricElement)
                    {
                        foreach (CellFace wireFace in element.CellFaces)
                        {
                            ChunkKey key = new ChunkKey(wireFace.X >> 4, wireFace.Z >> 4);
                            if (!statesByChunk.TryGetValue(key, out List<CircuitElementState> states) ||
                                chunkErrors.ContainsKey(key)) continue;
                            try
                            {
                                states.Add(CaptureElement(subsystem, element, wireFace));
                            }
                            catch (Exception captureError)
                            {
                                MarkChunkCaptureFailure(key, states, chunkErrors, captureError);
                            }
                        }
                        continue;
                    }

                    CellFace face;
                    try
                    {
                        face = GetStableCellFace(element);
                    }
                    catch (Exception faceError)
                    {
                        error = "Circuit element location capture failed: " + faceError.Message;
                        statesByChunk.Clear();
                        return false;
                    }
                    ChunkKey elementChunk = new ChunkKey(face.X >> 4, face.Z >> 4);
                    if (!statesByChunk.TryGetValue(elementChunk,
                            out List<CircuitElementState> elementStates) ||
                        chunkErrors.ContainsKey(elementChunk)) continue;
                    try
                    {
                        elementStates.Add(CaptureElement(subsystem, element, face));
                    }
                    catch (Exception captureError)
                    {
                        MarkChunkCaptureFailure(elementChunk, elementStates, chunkErrors,
                            captureError);
                    }
                }

                foreach (KeyValuePair<ChunkKey, List<CircuitElementState>> pair in statesByChunk)
                    if (!chunkErrors.ContainsKey(pair.Key))
                        pair.Value.Sort(CompareStates);
                return true;
            }
            catch (Exception captureError)
            {
                statesByChunk.Clear();
                chunkErrors.Clear();
                error = "Circuit best-effort multi-chunk capture failed: " + captureError.Message;
                return false;
            }
        }

        private static void MarkChunkCaptureFailure(ChunkKey key,
            List<CircuitElementState> states, IDictionary<ChunkKey, string> errors,
            Exception captureError)
        {
            states.Clear();
            errors[key] = "Circuit chunk " + key + " capture failed: " + captureError.Message;
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_electricElements
        // Full snapshot topology checks must not read replica voltages before host state arrives.
        public bool CaptureChunkTopology(SubsystemElectricity subsystem, int chunkX, int chunkZ,
            List<CircuitElementState> states, out string error)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            if (states == null) throw new ArgumentNullException(nameof(states));
            states.Clear();
            error = null;

            try
            {
                foreach (ElectricElement element in GetElements(subsystem))
                {
                    if (element is PistonElectricElement)
                        continue;

                    if (element is WireDomainElectricElement)
                    {
                        foreach (CellFace wireFace in element.CellFaces)
                        {
                            if ((wireFace.X >> 4) == chunkX && (wireFace.Z >> 4) == chunkZ)
                                states.Add(CaptureElementTopology(element, wireFace));
                        }
                        continue;
                    }

                    CellFace face = GetStableCellFace(element);
                    if ((face.X >> 4) == chunkX && (face.Z >> 4) == chunkZ)
                        states.Add(CaptureElementTopology(element, face));
                }

                states.Sort(CompareStates);
                return true;
            }
            catch (Exception ex)
            {
                states.Clear();
                error = "Circuit chunk topology capture failed: " + ex.Message;
                return false;
            }
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.ApplyStateRecord
        public bool Apply(SubsystemElectricity subsystem, CircuitElementState state,
            int hostStep, out string error)
        {
            if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
            if (state == null) throw new ArgumentNullException(nameof(state));
            error = null;

            try
            {
                if (state.MountingFace > 5)
                    throw new InvalidOperationException("Mounting face is outside 0..5.");

                ElectricElement element = subsystem.GetElectricElement(
                    state.X, state.Y, state.Z, (int)state.MountingFace);
                if (element == null)
                    throw new InvalidOperationException("No electric element exists at the encoded cell face.");

                uint actualType = GetStableTypeCode(element.GetType());
                if (actualType != state.ElementType)
                    throw new InvalidOperationException("Element type code does not match the live element.");
                EnsureSupportedType(element);

                int currentStep = subsystem.CircuitStep;
                int stateBaseStep = Math.Max(currentStep, hostStep);
                ClearScheduledSimulation(subsystem, element);

                if (element is BatteryElectricElement)
                {
                    ValidateOutputVoltages(element, state);
                }
                else if (element is RealTimeClockElectricElement)
                {
                    if (state.RuntimeState < int.MinValue || state.RuntimeState > int.MaxValue)
                        throw new InvalidOperationException(
                            "Real-time clock runtime state is outside the Int32 range.");
                    SetField(element, "m_lastClockValue", (int)state.RuntimeState,
                        typeof(RealTimeClockElectricElement));
                }
                else if (IsButton(element))
                {
                    ApplyButtonState(subsystem, element, state, hostStep);
                }
                else if (element is CounterElectricElement)
                {
                    int counter = state.VoltageLevel < 0 ? -state.VoltageLevel - 1 : state.VoltageLevel;
                    bool overflow = state.VoltageLevel < 0;
                    SetField(element, "m_counter", counter, typeof(CounterElectricElement));
                    SetField(element, "m_overflow", overflow, typeof(CounterElectricElement));
                    SetField(element, "m_plusAllowed", (state.Flags & 1) != 0, typeof(CounterElectricElement));
                    SetField(element, "m_minusAllowed", (state.Flags & 2) != 0, typeof(CounterElectricElement));
                    SetField(element, "m_resetAllowed", (state.Flags & 4) != 0, typeof(CounterElectricElement));
                    subsystem.WritePersistentVoltage(new Point3(state.X, state.Y, state.Z),
                        counter / 15f * (overflow ? -1f : 1f));
                }
                else if (element is BaseDelayGateElectricElement)
                {
                    SetField(element, "m_voltage", state.VoltageLevel / 15f,
                        typeof(BaseDelayGateElectricElement));
                    SetField(element, "m_lastStoredVoltage", state.AuxiliaryVoltageLevel / 15f,
                        typeof(BaseDelayGateElectricElement));
                    Dictionary<int, float> history = GetField<Dictionary<int, float>>(element,
                        "m_voltagesHistory", typeof(BaseDelayGateElectricElement));
                    history.Clear();
                    foreach (CircuitScheduledVoltage scheduled in state.ScheduledVoltages)
                    {
                        int step = checked(stateBaseStep + (int)scheduled.RemainingSteps);
                        history[step] = scheduled.VoltageLevel / 15f;
                    }
                }
                else
                {
                    Type voltageDeclaringType = FindFieldDeclaringType(element.GetType(), "m_voltage");
                    if (voltageDeclaringType == null)
                        throw new InvalidOperationException("Supported element has no m_voltage field.");
                    SetField(element, "m_voltage", state.VoltageLevel / 15f, voltageDeclaringType);

                    if (element is SRLatchElectricElement)
                    {
                        SetField(element, "m_setAllowed", (state.Flags & 1) != 0, typeof(SRLatchElectricElement));
                        SetField(element, "m_resetAllowed", (state.Flags & 2) != 0, typeof(SRLatchElectricElement));
                        SetField(element, "m_clockAllowed", (state.Flags & 4) != 0, typeof(SRLatchElectricElement));
                    }
                    else if (element is MemoryBankElectricElement)
                    {
                        SetField(element, "m_writeAllowed", (state.Flags & 1) != 0, typeof(MemoryBankElectricElement));
                        SetField(element, "m_clockAllowed", (state.Flags & 2) != 0, typeof(MemoryBankElectricElement));
                    }
                    else if (element is RandomGeneratorElectricElement)
                    {
                        SetField(element, "m_clockAllowed", (state.Flags & 1) != 0, typeof(RandomGeneratorElectricElement));
                    }

                    if (element is SRLatchElectricElement || element is RandomGeneratorElectricElement)
                        subsystem.WritePersistentVoltage(new Point3(state.X, state.Y, state.Z),
                            state.VoltageLevel / 15f);
                }

                RefreshPresentation(element, state.VoltageLevel);
                if (element is SevenSegmentDisplayElectricElement)
                {
                    m_sevenSegmentApplies++;
                    m_lastSevenSegmentApply = DescribeSevenSegmentState(element, state);
                }

                uint remainingSteps = state.NextSimulationSteps;
                if (IsButton(element) && state.ButtonPhase != 0 &&
                    state.ButtonRemainingSteps > 0)
                    remainingSteps = state.ButtonRemainingSteps;
                if (remainingSteps > int.MaxValue)
                    throw new InvalidOperationException(
                        "Next circuit simulation step is outside the Int32 range.");
                if (remainingSteps > 0)
                    subsystem.QueueElectricElementForSimulation(element,
                        checked(stateBaseStep + (int)remainingSteps));

                return true;
            }
            catch (Exception ex)
            {
                error = "Circuit state apply failed at (" + state.X + "," + state.Y + "," +
                    state.Z + ") face " + state.MountingFace + ": " + ex.Message;
                return false;
            }
        }

        // Source: Survivalcraft/Game/SevenSegmentDisplayElectricElement.cs:Simulate
        // Source: Survivalcraft/Game/OneLedElectricElement.cs:Simulate
        // Source: Survivalcraft/Game/FourLedElectricElement.cs:Simulate
        // Source: Survivalcraft/Game/LedElectricElement.cs:Simulate
        // Source: Survivalcraft/Game/MulticoloredLedElectricElement.cs:Simulate
        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.QueueSnapshotPresentationRefresh
        // Snapshot application refreshes glow caches immediately; the client solver is
        // separately paused during host fast-forward and bounded by the host step.
        private void RefreshPresentation(ElectricElement element, int voltageLevel)
        {
            int level = MathUtils.Clamp(voltageLevel, 0, 15);
            float voltage = level / 15f;

            if (element is SevenSegmentDisplayElectricElement)
            {
                SetField(element, "m_voltage", voltage,
                    typeof(SevenSegmentDisplayElectricElement));
                GlowPoint[] glowPoints = GetField<GlowPoint[]>(element, "m_glowPoints",
                    typeof(SevenSegmentDisplayElectricElement));
                int[] patterns = GetField<int[]>(element, "m_patterns",
                    typeof(SevenSegmentDisplayElectricElement));
                if (glowPoints.Length != 7 || patterns.Length != 16)
                    throw new InvalidOperationException("Seven-segment display cache has an unexpected size.");

                // SevenSegmentDisplayElectricElement reads its color only in OnAdded.
                // Terrain replication can create the element before the authoritative
                // block data arrives, so refresh the color from the live cell here.
                CellFace face = element.CellFaces[0];
                Game.Terrain terrain = element.SubsystemElectricity?.SubsystemTerrain?.Terrain;
                if (terrain == null)
                    throw new InvalidOperationException("Seven-segment display terrain is unavailable.");
                int cellValue = terrain.GetCellValue(face.X, face.Y, face.Z);
                int colorIndex = SevenSegmentDisplayBlock.GetColor(
                    Game.Terrain.ExtractData(cellValue));
                Color color = LedBlock.LedColors[MathUtils.Clamp(colorIndex, 0,
                    LedBlock.LedColors.Length - 1)];
                SetField(element, "m_color", color,
                    typeof(SevenSegmentDisplayElectricElement));
                for (int i = 0; i < glowPoints.Length; i++)
                    SetGlowPointColor(glowPoints[i], (patterns[level] & (1 << i)) != 0
                        ? color
                        : Color.Transparent);
                return;
            }

            if (element is FourLedElectricElement)
            {
                SetField(element, "m_voltage", voltage, typeof(FourLedElectricElement));
                GlowPoint[] glowPoints = GetField<GlowPoint[]>(element, "m_glowPoints",
                    typeof(FourLedElectricElement));
                if (glowPoints.Length != 4)
                    throw new InvalidOperationException("Four-LED display cache has an unexpected size.");

                Color color = GetField<Color>(element, "m_color", typeof(FourLedElectricElement));
                for (int i = 0; i < glowPoints.Length; i++)
                    SetGlowPointColor(glowPoints[i], (level & (1 << i)) != 0
                        ? color
                        : Color.Transparent);
                return;
            }

            if (element is OneLedElectricElement)
            {
                SetField(element, "m_voltage", voltage, typeof(OneLedElectricElement));
                GlowPoint glowPoint = GetField<GlowPoint>(element, "m_glowPoint",
                    typeof(OneLedElectricElement));
                Color color = level >= 8
                    ? LedBlock.LedColors[MathUtils.Clamp(level - 8, 0, 7)]
                    : Color.Transparent;
                SetGlowPointColor(glowPoint, color);
                return;
            }

            if (element is MulticoloredLedElectricElement)
            {
                SetField(element, "m_voltage", voltage, typeof(MulticoloredLedElectricElement));
                GlowPoint glowPoint = GetField<GlowPoint>(element, "m_glowPoint",
                    typeof(MulticoloredLedElectricElement));
                Color color = level >= 8
                    ? LedBlock.LedColors[MathUtils.Clamp(level - 8, 0, 7)]
                    : Color.Transparent;
                SetGlowPointColor(glowPoint, color);
                return;
            }

            if (element is LedElectricElement)
            {
                SetField(element, "m_voltage", voltage, typeof(LedElectricElement));
                GlowPoint glowPoint = GetField<GlowPoint>(element, "m_glowPoint",
                    typeof(LedElectricElement));
                Color color = ElectricElement.IsSignalHigh(voltage)
                    ? GetField<Color>(element, "m_color", typeof(LedElectricElement))
                    : Color.Transparent;
                SetGlowPointColor(glowPoint, color);
            }
        }

        // Source: Survivalcraft/Game/GlowPoint.cs:GlowPoint.Color
        private static void SetGlowPointColor(GlowPoint glowPoint, Color color)
        {
            if (glowPoint == null)
                throw new InvalidOperationException("LED glow point cache contains null.");
            glowPoint.Color = color;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.ComputeStateHash
        public uint ComputeHash(IEnumerable<CircuitElementState> states)
        {
            if (states == null) throw new ArgumentNullException(nameof(states));
            uint hash = FnvOffsetBasis;
            foreach (CircuitElementState state in states.OrderBy(item => item.X)
                .ThenBy(item => item.Y).ThenBy(item => item.Z).ThenBy(item => item.MountingFace))
            {
                if (state == null) throw new ArgumentException("Circuit states cannot contain null.", nameof(states));
                if (IsDisplayElementType(state.ElementType)) continue;
                HashState(ref hash, state, scheduledVoltagesAreSorted: false);
            }
            return hash;
        }

        // Source: CircuitStateCodec.CaptureChunk and CircuitStateCodec.CaptureChunksBestEffort
        // Captures sort states and scheduled voltages by the exact keys used by ComputeHash.
        public uint ComputeCapturedHash(IList<CircuitElementState> states)
        {
            if (states == null) throw new ArgumentNullException(nameof(states));
            uint hash = FnvOffsetBasis;
            CircuitElementState previous = null;
            for (int i = 0; i < states.Count; i++)
            {
                CircuitElementState state = states[i];
                if (state == null)
                    throw new ArgumentException("Circuit states cannot contain null.", nameof(states));
                if (previous != null && CompareStates(previous, state) > 0)
                    throw new InvalidOperationException(
                        "Captured circuit states are not sorted by coordinate and mounting face.");
                if (IsDisplayElementType(state.ElementType))
                {
                    previous = state;
                    continue;
                }
                HashState(ref hash, state, scheduledVoltagesAreSorted: true);
                previous = state;
            }
            return hash;
        }

        private static void HashState(ref uint hash, CircuitElementState state,
            bool scheduledVoltagesAreSorted)
        {
            if (state == null)
                throw new ArgumentException("Circuit states cannot contain null.");
            Hash(ref hash, state.X);
            Hash(ref hash, state.Y);
            Hash(ref hash, state.Z);
            Hash(ref hash, unchecked((int)state.MountingFace));
            Hash(ref hash, unchecked((int)state.ElementType));
            Hash(ref hash, state.VoltageLevel);
            Hash(ref hash, unchecked((int)state.Flags));
            Hash(ref hash, state.AuxiliaryVoltageLevel);
            Hash(ref hash, unchecked((int)state.RuntimeState));
            Hash(ref hash, unchecked((int)(state.RuntimeState >> 32)));
            Hash(ref hash, unchecked((int)state.AuxiliaryState));
            Hash(ref hash, unchecked((int)(state.AuxiliaryState >> 32)));
            Hash(ref hash, unchecked((int)state.ButtonPhase));
            Hash(ref hash, state.ScheduledVoltages.Count);
            if (scheduledVoltagesAreSorted)
            {
                CircuitScheduledVoltage previous = null;
                foreach (CircuitScheduledVoltage scheduled in state.ScheduledVoltages)
                {
                    if (previous != null && s_scheduledVoltageComparer.Compare(
                            previous, scheduled) > 0)
                        throw new InvalidOperationException(
                            "Captured circuit scheduled voltages are not sorted by remaining steps and voltage.");
                    HashScheduledVoltage(ref hash, scheduled);
                    previous = scheduled;
                }
            }
            else
            {
                foreach (CircuitScheduledVoltage scheduled in state.ScheduledVoltages
                    .OrderBy(item => item.RemainingSteps).ThenBy(item => item.VoltageLevel))
                    HashScheduledVoltage(ref hash, scheduled);
            }
        }

        private void CaptureScheduledVoltages(Dictionary<int, float> history, int circuitStep,
            Google.Protobuf.Collections.RepeatedField<CircuitScheduledVoltage> target)
        {
            m_delayHistoryScratch.Clear();
            foreach (KeyValuePair<int, float> item in history)
                if (item.Key >= circuitStep)
                    m_delayHistoryScratch.Add(item);
            m_delayHistoryScratch.Sort(s_delayHistoryComparer);

            m_scheduledVoltageScratch.Clear();
            int count = Math.Min(MaximumDelayHistoryEntries, m_delayHistoryScratch.Count);
            for (int i = 0; i < count; i++)
            {
                KeyValuePair<int, float> item = m_delayHistoryScratch[i];
                m_scheduledVoltageScratch.Add(new CircuitScheduledVoltage
                {
                    RemainingSteps = (uint)MathUtils.Clamp(
                        item.Key - circuitStep, 0, MaximumScheduledSteps),
                    VoltageLevel = QuantizeVoltage(item.Value)
                });
            }
            m_scheduledVoltageScratch.Sort(s_scheduledVoltageComparer);
            target.Add(m_scheduledVoltageScratch);
        }

        private static void HashScheduledVoltage(ref uint hash,
            CircuitScheduledVoltage scheduled)
        {
            Hash(ref hash, unchecked((int)scheduled.RemainingSteps));
            Hash(ref hash, scheduled.VoltageLevel);
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_electricElements
        private ElectricElement[] GetElements(SubsystemElectricity subsystem)
        {
            var elements = (Dictionary<ElectricElement, bool>)GetStaticParentField(subsystem,
                "m_electricElements", typeof(SubsystemElectricity));
            if (elements == null)
                throw new InvalidOperationException("SubsystemElectricity.m_electricElements is null.");
            // PistonElectricElement is simulated by SubsystemPistonBlockBehavior and
            // intentionally has no circuit snapshot representation. Filter it before
            // any state capture path can reach EnsureSupportedType.
            return elements.Keys.Where(item => !(item is PistonElectricElement)).ToArray();
        }

        private ElectricElement[] GetAllElements(SubsystemElectricity subsystem)
        {
            var elements = (Dictionary<ElectricElement, bool>)GetStaticParentField(subsystem,
                "m_electricElements", typeof(SubsystemElectricity));
            if (elements == null)
                throw new InvalidOperationException("SubsystemElectricity.m_electricElements is null.");
            return elements.Keys.ToArray();
        }

        private static bool IsDisplayElement(ElectricElement element) =>
            element is SevenSegmentDisplayElectricElement ||
            element is FourLedElectricElement ||
            element is OneLedElectricElement ||
            element is LedElectricElement ||
            element is MulticoloredLedElectricElement;

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.TryCaptureState
        private CircuitElementState CaptureElement(SubsystemElectricity subsystem,
            ElectricElement element, CellFace face)
        {
            EnsureSupportedType(element);
            var state = new CircuitElementState
            {
                X = face.X,
                Y = face.Y,
                Z = face.Z,
                MountingFace = (uint)face.Face,
                ElementType = GetStableTypeCode(element.GetType())
            };

            if (element is BatteryElectricElement)
            {
                // Battery output is derived directly from terrain block data.
            }
            else if (element is RealTimeClockElectricElement)
            {
                state.RuntimeState = GetField<int>(element, "m_lastClockValue",
                    typeof(RealTimeClockElectricElement));
            }
            else if (element is CounterElectricElement)
            {
                int counter = GetField<int>(element, "m_counter", typeof(CounterElectricElement));
                bool overflow = GetField<bool>(element, "m_overflow", typeof(CounterElectricElement));
                state.VoltageLevel = overflow ? -(counter + 1) : counter;
                state.Flags = (uint)((GetField<bool>(element, "m_plusAllowed", typeof(CounterElectricElement)) ? 1 : 0) |
                    (GetField<bool>(element, "m_minusAllowed", typeof(CounterElectricElement)) ? 2 : 0) |
                    (GetField<bool>(element, "m_resetAllowed", typeof(CounterElectricElement)) ? 4 : 0));
            }
            else
            {
                Type voltageDeclaringType = element is BaseDelayGateElectricElement
                    ? typeof(BaseDelayGateElectricElement)
                    : IsButton(element) ? GetButtonType(element)
                    : FindFieldDeclaringType(element.GetType(), "m_voltage");
                if (voltageDeclaringType == null)
                    throw new InvalidOperationException("No supported m_voltage field was found.");
                float voltage = GetField<float>(element, "m_voltage", voltageDeclaringType);
                state.VoltageLevel = QuantizeVoltage(voltage);

                if (element is ButtonElectricElement || element is ButtonFurnitureElectricElement)
                    CaptureButtonTiming(subsystem, element, face, state, voltage, voltageDeclaringType);
                else if (element is SRLatchElectricElement)
                    state.Flags = (uint)((GetField<bool>(element, "m_setAllowed", typeof(SRLatchElectricElement)) ? 1 : 0) |
                        (GetField<bool>(element, "m_resetAllowed", typeof(SRLatchElectricElement)) ? 2 : 0) |
                        (GetField<bool>(element, "m_clockAllowed", typeof(SRLatchElectricElement)) ? 4 : 0));
                else if (element is MemoryBankElectricElement)
                    state.Flags = (uint)((GetField<bool>(element, "m_writeAllowed", typeof(MemoryBankElectricElement)) ? 1 : 0) |
                        (GetField<bool>(element, "m_clockAllowed", typeof(MemoryBankElectricElement)) ? 2 : 0));
                else if (element is RandomGeneratorElectricElement)
                    state.Flags = GetField<bool>(element, "m_clockAllowed", typeof(RandomGeneratorElectricElement)) ? 1u : 0u;

                if (element is BaseDelayGateElectricElement)
                {
                    state.AuxiliaryVoltageLevel = QuantizeVoltage(GetField<float>(element,
                        "m_lastStoredVoltage", typeof(BaseDelayGateElectricElement)));
                    Dictionary<int, float> history = GetField<Dictionary<int, float>>(element,
                        "m_voltagesHistory", typeof(BaseDelayGateElectricElement));
                    if (history == null)
                        throw new InvalidOperationException("BaseDelayGateElectricElement.m_voltagesHistory is null.");
                    CaptureScheduledVoltages(history, subsystem.CircuitStep,
                        state.ScheduledVoltages);
                }
            }

            for (int port = 0; port < 6; port++)
                state.OutputVoltageLevels.Add(QuantizeVoltage(element.GetOutputVoltage(port)));

            if (element is SevenSegmentDisplayElectricElement)
            {
                m_sevenSegmentCaptures++;
                m_lastSevenSegmentCapture = DescribeSevenSegmentState(element, state);
            }

            int nextStep = FindNextSimulationStep(subsystem, element);
            state.NextSimulationSteps = nextStep > subsystem.CircuitStep
                ? (uint)MathUtils.Clamp(nextStep - subsystem.CircuitStep, 1, MaximumScheduledSteps)
                : 0;
            return state;
        }

        // Source: Survivalcraft/Game/ElectricElement.cs:ElectricElement.CellFaces
        private static CircuitElementState CaptureElementTopology(ElectricElement element,
            CellFace face)
        {
            EnsureSupportedType(element);
            return new CircuitElementState
            {
                X = face.X,
                Y = face.Y,
                Z = face.Z,
                MountingFace = (uint)face.Face,
                ElementType = GetStableTypeCode(element.GetType())
            };
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.CaptureButtonSnapshotTiming
        private void CaptureButtonTiming(SubsystemElectricity subsystem, ElectricElement element,
            CellFace face, CircuitElementState state, float voltage, Type declaringType)
        {
            bool wasPressed = GetField<bool>(element, "m_wasPressed", declaringType);
            uint phase = wasPressed ? 1u : ElectricElement.IsSignalHigh(voltage) ? 2u : 0u;
            if (phase == 0) return;

            int nextStep = FindNextSimulationStep(subsystem, element);
            int remaining = nextStep > subsystem.CircuitStep ? nextStep - subsystem.CircuitStep : 1;
            state.ButtonPhase = phase;
            state.ButtonRemainingSteps = (uint)MathUtils.Clamp(remaining, 1, 15);
            state.RelatedSequence = 0;
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_futureSimulateLists
        private int FindNextSimulationStep(SubsystemElectricity subsystem, ElectricElement element)
        {
            var future = (Dictionary<int, Dictionary<ElectricElement, bool>>)GetStaticParentField(
                subsystem, "m_futureSimulateLists", typeof(SubsystemElectricity));
            if (future == null)
                throw new InvalidOperationException("SubsystemElectricity.m_futureSimulateLists is null.");
            return future.Where(item => item.Key > subsystem.CircuitStep && item.Value.ContainsKey(element))
                .Select(item => item.Key).DefaultIfEmpty(0).Min();
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_futureSimulateLists
        private void ClearScheduledSimulation(SubsystemElectricity subsystem, ElectricElement element)
        {
            var future = (Dictionary<int, Dictionary<ElectricElement, bool>>)GetStaticParentField(
                subsystem, "m_futureSimulateLists", typeof(SubsystemElectricity));
            if (future == null)
                throw new InvalidOperationException("SubsystemElectricity.m_futureSimulateLists is null.");
            foreach (Dictionary<ElectricElement, bool> list in future.Values)
                list.Remove(element);
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.ApplyButtonSnapshotState
        private void ApplyButtonState(SubsystemElectricity subsystem, ElectricElement element,
            CircuitElementState state, int hostStep)
        {
            Type declaringType = GetButtonType(element);
            SetField(element, "m_wasPressed", state.ButtonPhase == 1, declaringType);
            SetField(element, "m_voltage", MathUtils.Max(state.VoltageLevel / 15f, 0f), declaringType);
            if (state.ButtonPhase != 0 && state.ButtonRemainingSteps > 0)
            {
                // The host owns the button release timing. The client only presents the
                // received phase and must not schedule a local simulation edge.
            }
        }

        // Source: EntitySystem/SuAPI/IModParentField.cs:IModParentField.GetParentField
        private T GetField<T>(object target, string fieldName, Type declaringType)
        {
            object value = m_parentField.GetParentField(target, fieldName, declaringType);
            if (!(value is T result))
                throw new InvalidOperationException("Private field " + declaringType.FullName + "." +
                    fieldName + " has unexpected type " + (value == null ? "null" : value.GetType().FullName) + ".");
            return result;
        }

        // Source: EntitySystem/SuAPI/IModParentField.cs:IModParentField.ModifyParentField
        private void SetField(object target, string fieldName, object value, Type declaringType)
        {
            m_parentField.ModifyParentField(target, fieldName, value, declaringType);
        }

        // Source: EntitySystem/SuAPI/IModParentField.cs:IModParentField.GetParentField
        private object GetStaticParentField(object target, string fieldName, Type declaringType)
        {
            object value = m_parentField.GetParentField(target, fieldName, declaringType);
            if (value == null)
                throw new InvalidOperationException("Private field " + declaringType.FullName + "." +
                    fieldName + " returned null.");
            return value;
        }

        // Source: Survivalcraft/Game/ElectricElement.cs:ElectricElement.GetOutputVoltage
        private static void EnsureSupportedType(ElectricElement element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            Type type = element.GetType();
            if (element is SuRandomGeneratorElectricElement)
                return;
            if (type.Assembly != typeof(ElectricElement).Assembly)
                throw new InvalidOperationException("Unknown electric element type " + type.FullName + ".");
            if (element is BatteryElectricElement || element is RealTimeClockElectricElement || element is CounterElectricElement ||
                IsButton(element) || element is BaseDelayGateElectricElement ||
                element is SRLatchElectricElement || element is MemoryBankElectricElement ||
                element is RandomGeneratorElectricElement)
                return;
            if (FindFieldDeclaringType(type, "m_voltage") == null)
                throw new InvalidOperationException("Unsupported electric element type " + type.FullName + ".");
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.TryGetButtonDeclaringType
        private static bool IsButton(ElectricElement element)
        {
            return element is ButtonElectricElement || element is ButtonFurnitureElectricElement;
        }

        // Source: Survivalcraft/Game/ButtonElectricElement.cs:ButtonElectricElement.Simulate
        private static Type GetButtonType(ElectricElement element)
        {
            if (element is ButtonElectricElement) return typeof(ButtonElectricElement);
            if (element is ButtonFurnitureElectricElement) return typeof(ButtonFurnitureElectricElement);
            throw new InvalidOperationException("Element is not a supported button.");
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.FindFieldDeclaringType
        private static Type FindFieldDeclaringType(Type type, string fieldName)
        {
            while (type != null && type != typeof(object))
            {
                if (type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null)
                    return type;
                type = type.BaseType;
            }
            return null;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.GetStableTypeCode
        private static uint GetStableTypeCode(Type type)
        {
            uint hash = FnvOffsetBasis;
            string name = type.FullName ?? string.Empty;
            foreach (char value in name)
            {
                hash = unchecked((hash ^ (byte)value) * FnvPrime);
                hash = unchecked((hash ^ (byte)(value >> 8)) * FnvPrime);
            }
            return hash & 0x7fffffffu;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.GetStableCellFace
        private static CellFace GetStableCellFace(ElectricElement element)
        {
            if (element.CellFaces.Count == 0)
                throw new InvalidOperationException("Electric element has no cell faces.");
            return element.CellFaces.OrderBy(face => face.X).ThenBy(face => face.Y)
                .ThenBy(face => face.Z).ThenBy(face => face.Face).First();
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.TryCaptureState
        private static int QuantizeVoltage(float voltage)
        {
            if (float.IsNaN(voltage) || float.IsInfinity(voltage))
                throw new InvalidOperationException("Circuit voltage is not finite.");
            return MathUtils.Clamp((int)MathUtils.Round(voltage * 15f), -15, 15);
        }

        // Source: Survivalcraft/Game/BatteryElectricElement.cs:BatteryElectricElement.GetOutputVoltage
        private static void ValidateOutputVoltages(ElectricElement element, CircuitElementState state)
        {
            if (state.OutputVoltageLevels.Count != 6)
                throw new InvalidOperationException("Static source snapshot must contain six output ports.");
            for (int port = 0; port < 6; port++)
            {
                if (QuantizeVoltage(element.GetOutputVoltage(port)) != state.OutputVoltageLevels[port])
                    throw new InvalidOperationException("Terrain-derived battery output differs from the snapshot at port " + port + ".");
            }
        }

        // Source: Survivalcraft/Game/SevenSegmentDisplayElectricElement.cs:m_patterns
        private string DescribeSevenSegmentState(ElectricElement element, CircuitElementState state)
        {
            int level = MathUtils.Clamp(state.VoltageLevel, 0, 15);
            int[] patterns = GetField<int[]>(element, "m_patterns",
                typeof(SevenSegmentDisplayElectricElement));
            if (patterns == null || patterns.Length != 16)
                throw new InvalidOperationException("Seven-segment display pattern cache is unavailable.");
            int mask = patterns[level];
            int maskForLog = mask;
            int litSegments = 0;
            while (mask != 0)
            {
                litSegments += mask & 1;
                mask >>= 1;
            }
            GlowPoint[] glowPoints = GetField<GlowPoint[]>(element, "m_glowPoints",
                typeof(SevenSegmentDisplayElectricElement));
            int activeGlowPoints = 0;
            int registeredGlowPoints = 0;
            uint activeColor = 0;
            if (glowPoints != null)
            {
                object glowRegistry = null;
                if (element.SubsystemElectricity?.Project != null)
                {
                    SubsystemGlow glow = element.SubsystemElectricity.Project
                        .FindSubsystem<SubsystemGlow>(false);
                    if (glow != null)
                        glowRegistry = m_parentField.GetParentField<object>(glow,
                            "m_glowPoints", typeof(SubsystemGlow));
                }
                System.Collections.IDictionary registered = glowRegistry as System.Collections.IDictionary;
                for (int i = 0; i < glowPoints.Length; i++)
                {
                    GlowPoint glowPoint = glowPoints[i];
                    if (glowPoint != null && registered != null && registered.Contains(glowPoint))
                        registeredGlowPoints++;
                    if (glowPoint != null && glowPoint.Color.A > 0)
                    {
                        activeGlowPoints++;
                        activeColor = glowPoint.Color.PackedValue;
                    }
                }
            }
            Color configuredColor = GetField<Color>(element, "m_color",
                typeof(SevenSegmentDisplayElectricElement));
            return state.X + "," + state.Y + "," + state.Z + " level=" +
                state.VoltageLevel + " digit=" + level.ToString("X") +
                " mask=0x" + maskForLog.ToString("X2") +
                " segments=" + litSegments +
                " glow=" + activeGlowPoints + "/" + (glowPoints == null ? 0 : glowPoints.Length) +
                " registered=" + registeredGlowPoints + "/" + (glowPoints == null ? 0 : glowPoints.Length) +
                " color=#" + activeColor.ToString("X8") +
                " configured=#" + configuredColor.PackedValue.ToString("X8");
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.ComputeStateHash
        private static int CompareStates(CircuitElementState left, CircuitElementState right)
        {
            int result = left.X.CompareTo(right.X);
            if (result != 0) return result;
            result = left.Y.CompareTo(right.Y);
            if (result != 0) return result;
            result = left.Z.CompareTo(right.Z);
            return result != 0 ? result : left.MountingFace.CompareTo(right.MountingFace);
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.Hash
        private static void Hash(ref uint hash, int value)
        {
            unchecked
            {
                hash = (hash ^ (byte)value) * FnvPrime;
                hash = (hash ^ (byte)(value >> 8)) * FnvPrime;
                hash = (hash ^ (byte)(value >> 16)) * FnvPrime;
                hash = (hash ^ (byte)(value >> 24)) * FnvPrime;
            }
        }
    }
}

using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using System;
using System.Collections.Generic;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// Captures client circuit interactions and predicts only their local presentation.
    /// Circuit element runtime state remains entirely host-authoritative.
    /// </summary>
    public sealed class CircuitUseInputSink : IEntityUseInputSink,
        ICircuitPresentationPort
    {
        private sealed class PendingOperation
        {
            public long RequestId;
            public CircuitInteractionKind Kind;
            public Point3 Point;
        }

        private struct CircuitCellKey : IEquatable<CircuitCellKey>
        {
            public CircuitCellKey(Point3 point, int face)
            {
                Point = point;
                Face = face;
            }

            public Point3 Point;
            public int Face;

            public bool Equals(CircuitCellKey other) => Point.Equals(other.Point) &&
                Face == other.Face;

            public override bool Equals(object obj) => obj is CircuitCellKey other &&
                Equals(other);

            public override int GetHashCode() => HashCode.Combine(Point, Face);
        }

        private const int MaximumPendingOperations = 256;
        private readonly IModParentField m_parentField;
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly Dictionary<CircuitCellKey, List<PendingOperation>> m_pending =
            new Dictionary<CircuitCellKey, List<PendingOperation>>();
        private readonly Dictionary<CircuitCellKey, int> m_authoritativeValues =
            new Dictionary<CircuitCellKey, int>();
        private Func<CircuitInteractionRequestData, bool> m_setCircuitRequest;
        private Project m_project;
        private long m_nextRequestId;

        public CircuitUseInputSink(IModParentField parentField,
            NetworkAvatarSpawner spawner = null)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_spawner = spawner;
        }

        public void SetCircuitRequest(Func<CircuitInteractionRequestData, bool> callback) =>
            m_setCircuitRequest = callback;

        public void SetCircuitRequestSink(Func<CircuitInteractionRequestData, bool> callback) =>
            m_setCircuitRequest = callback;

        // Source: Survivalcraft/Game/SubsystemElectricBlockBehavior.cs:SubsystemElectricBlockBehavior.OnInteract
        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Interact
        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.GetElectricElement
        public bool TryBuildHostUseRequest(int clientId,
            in CircuitInteractionRequestData circuitRequest,
            out BlockUseRequestData useRequest, out string reason)
        {
            useRequest = default;
            reason = null;
            if (clientId <= 0 || circuitRequest.RequestId <= 0 ||
                circuitRequest.MountingFace < 0 || circuitRequest.MountingFace > 5)
            {
                reason = "invalid-client-request";
                return false;
            }
            if (m_spawner == null || !m_spawner.TryGet(clientId,
                    out PlayerData playerData) || playerData?.ComponentPlayer?.ComponentMiner == null)
            {
                reason = "client-player-miner-unavailable";
                return false;
            }
            Project project = GameManager.Project;
            SubsystemTerrain terrainSubsystem = project?.FindSubsystem<SubsystemTerrain>(false);
            Game.Terrain terrain = terrainSubsystem?.Terrain;
            if (terrain == null || !terrain.IsCellValid(circuitRequest.X,
                    circuitRequest.Y, circuitRequest.Z) ||
                terrain.GetChunkAtCell(circuitRequest.X, circuitRequest.Z) == null)
            {
                reason = "target-cell-or-chunk-unavailable";
                return false;
            }
            if (!IsFinite(circuitRequest.RayX) || !IsFinite(circuitRequest.RayY) ||
                !IsFinite(circuitRequest.RayZ) || !IsFinite(circuitRequest.DirectionX) ||
                !IsFinite(circuitRequest.DirectionY) || !IsFinite(circuitRequest.DirectionZ))
            {
                reason = "non-finite-ray";
                return false;
            }
            Vector3 direction = new Vector3(circuitRequest.DirectionX,
                circuitRequest.DirectionY, circuitRequest.DirectionZ);
            if (direction.LengthSquared() < 0.0001f)
            {
                reason = "zero-ray-direction";
                return false;
            }
            var ray = new Ray3(new Vector3(circuitRequest.RayX, circuitRequest.RayY,
                circuitRequest.RayZ), Vector3.Normalize(direction));
            TerrainRaycastResult? hit;
            try
            {
                hit = playerData.ComponentPlayer.ComponentMiner.Raycast<TerrainRaycastResult>(
                    ray, RaycastMode.Interaction);
            }
            catch (Exception e)
            {
                reason = "host-raycast-" + e.GetType().Name;
                return false;
            }
            if (!hit.HasValue || hit.Value.CellFace.X != circuitRequest.X ||
                hit.Value.CellFace.Y != circuitRequest.Y ||
                hit.Value.CellFace.Z != circuitRequest.Z)
            {
                reason = "raycast-target-mismatch";
                return false;
            }
            int cellValue = terrain.GetCellValue(circuitRequest.X,
                circuitRequest.Y, circuitRequest.Z);
            if (Game.Terrain.ExtractContents(cellValue) !=
                Game.Terrain.ExtractContents(circuitRequest.ExpectedCellValue))
            {
                reason = "target-block-changed";
                return false;
            }
            SubsystemElectricity electricity = project.FindSubsystem<SubsystemElectricity>(false);
            ElectricElement element = null;
            int firstElementFace = -1;
            if (electricity != null)
            {
                for (int candidateFace = 0; candidateFace < 6; candidateFace++)
                {
                    element = electricity.GetElectricElement(circuitRequest.X,
                        circuitRequest.Y, circuitRequest.Z, candidateFace);
                    if (element != null)
                    {
                        firstElementFace = candidateFace;
                        break;
                    }
                }
            }
            if (firstElementFace != circuitRequest.MountingFace ||
                !MatchesOperation(element, circuitRequest.Kind))
            {
                reason = "target-operation-mismatch";
                return false;
            }
            Vector3 position = ray.Position;
            useRequest = new BlockUseRequestData(circuitRequest.RequestId,
                circuitRequest.X, circuitRequest.Y, circuitRequest.Z,
                hit.Value.CellFace.Face, Game.Terrain.ExtractContents(cellValue),
                BlockUseActions.Interact, position.X, position.Y, position.Z,
                ray.Direction.X, ray.Direction.Y, ray.Direction.Z, hasReach: true,
                reach: circuitRequest.Reach);
            return true;
        }

        /// <summary>Non-circuit interactions can be forwarded to the existing block-use sink.</summary>
        public IEntityUseInputSink Fallback { get; set; }

        public int PendingCount
        {
            get
            {
                int count = 0;
                foreach (List<PendingOperation> operations in m_pending.Values)
                    count += operations.Count;
                return count;
            }
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Raycast
        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.GetElectricElement
        public bool TryAcceptInteract(ComponentInput input, ComponentPlayer player)
        {
            if (input == null || player?.ComponentMiner == null) return false;
            Project project = GameManager.Project;
            if (project == null) return false;
            if (!ReferenceEquals(project, m_project))
            {
                m_project = project;
                m_pending.Clear();
                m_authoritativeValues.Clear();
            }
            ComponentMiner miner = player.ComponentMiner;
            Ray3? ray = input.PlayerInput.Interact;
            if (!ray.HasValue) return false;

            TerrainRaycastResult? hit;
            try
            {
                hit = miner.Raycast<TerrainRaycastResult>(ray.Value, RaycastMode.Interaction);
            }
            catch (Exception e)
            {
                LogProblem("raycast", e.GetType().Name + ": " + e.Message);
                return false;
            }
            if (!hit.HasValue) return false;

            CellFace face = hit.Value.CellFace;
            SubsystemTerrain terrainSubsystem = project.FindSubsystem<SubsystemTerrain>(false);
            Game.Terrain terrain = terrainSubsystem?.Terrain;
            if (terrain == null) return false;
            int cellValue = terrain.GetCellValue(face.X, face.Y, face.Z);
            SubsystemElectricity electricity = project.FindSubsystem<SubsystemElectricity>(false);
            ElectricElement element = null;
            int mountingFace = -1;
            if (electricity != null)
            {
                for (int candidateFace = 0; candidateFace < 6; candidateFace++)
                {
                    ElectricElement candidate = electricity.GetElectricElement(face.X,
                        face.Y, face.Z, candidateFace);
                    if (candidate != null)
                    {
                        element = candidate;
                        mountingFace = candidateFace;
                        break;
                    }
                }
            }

            CircuitInteractionKind kind;
            if (element is ButtonElectricElement || element is ButtonFurnitureElectricElement)
                kind = CircuitInteractionKind.ButtonPress;
            else if (element is SwitchElectricElement)
                kind = CircuitInteractionKind.SwitchToggle;
            else if (element is SwitchFurnitureElectricElement)
                kind = CircuitInteractionKind.FurnitureSwitch;
            else if (element is RotateableElectricElement)
                kind = CircuitInteractionKind.Rotate;
            else
            {
                return Fallback != null && Fallback.TryAcceptInteract(input, player);
            }

            if (!TryConsumeInteract(input)) return true;
            if (m_setCircuitRequest == null)
            {
                LogProblem("request-sink", "circuit interaction sink is not attached");
                return true;
            }
            if (PendingCount >= MaximumPendingOperations)
            {
                LogProblem("pending-limit", "pending operation limit reached");
                return true;
            }

            CircuitCellKey key = new CircuitCellKey(face.Point, mountingFace);
            int baseValue = GetPresentationBase(key, terrain, cellValue);
            long requestId = NextRequestId();
            Vector3 direction = ray.Value.Direction;
            var request = new CircuitInteractionRequestData(requestId, kind,
                face.X, face.Y, face.Z, mountingFace, cellValue,
                ray.Value.Position.X, ray.Value.Position.Y, ray.Value.Position.Z,
                direction.X, direction.Y, direction.Z, EngineReach.LocalReach());
            bool submitted;
            try
            {
                submitted = m_setCircuitRequest(request);
            }
            catch (Exception e)
            {
                submitted = false;
                LogProblem("request-send", e.GetType().Name + ": " + e.Message);
            }
            if (!submitted)
            {
                LogProblem("request-send", "request was not accepted by the circuit transport");
                return true;
            }

            if (!m_authoritativeValues.ContainsKey(key))
                m_authoritativeValues[key] = baseValue;
            var operation = new PendingOperation
            {
                RequestId = requestId,
                Kind = kind,
                Point = face.Point
            };
            if (!m_pending.TryGetValue(key, out List<PendingOperation> operations))
            {
                operations = new List<PendingOperation>();
                m_pending.Add(key, operations);
            }
            operations.Add(operation);
            ApplyPresentation(key, m_authoritativeValues[key], operations);
            PlayClick(electricity?.SubsystemAudio, face.Point);
            return true;
        }

        /// <summary>
        /// Resolves one request using the host's current cell value. Remaining predictions for
        /// that cell are replayed over the authoritative value in request order.
        /// </summary>
        public void ResolvePresentation(long requestId, int authoritativeCellValue)
        {
            ResolvePendingPresentation(requestId, authoritativeCellValue);
        }

        public void RejectPresentation(long requestId, int? authoritativeCellValue)
        {
            ResolvePendingPresentation(requestId, authoritativeCellValue);
        }

        private void ResolvePendingPresentation(long requestId, int? authoritativeCellValue)
        {
            if (!TryFindPending(requestId, out CircuitCellKey key,
                    out List<PendingOperation> operations, out int index))
                return;
            operations.RemoveAt(index);
            if (authoritativeCellValue.HasValue)
                m_authoritativeValues[key] = authoritativeCellValue.Value;
            else if (!m_authoritativeValues.TryGetValue(key, out _))
            {
                LogProblem("rollback-baseline", "rejected operation has no authoritative cell " +
                    "value or saved presentation baseline at " + key.Point.X + "," +
                    key.Point.Y + "," + key.Point.Z);
                m_pending.Remove(key);
                return;
            }
            ApplyOrForget(key, operations, m_authoritativeValues[key]);
        }

        /// <summary>Applies an authoritative CellChange and clears predictions for its cell.</summary>
        public void ApplyAuthoritativeCell(int x, int y, int z, int mountingFace,
            int authoritativeCellValue)
        {
            var key = new CircuitCellKey(new Point3(x, y, z), mountingFace);
            m_authoritativeValues[key] = authoritativeCellValue;
            if (m_pending.TryGetValue(key, out List<PendingOperation> operations))
            {
                ApplyPresentation(key, authoritativeCellValue, operations);
            }
            else
            {
                ApplyCellPresentation(key.Point, authoritativeCellValue);
                m_authoritativeValues.Remove(key);
            }
        }

        public void DetachPresentation()
        {
            foreach (KeyValuePair<CircuitCellKey, int> pair in m_authoritativeValues)
                ApplyCellPresentation(pair.Key.Point, pair.Value);
            m_pending.Clear();
            m_authoritativeValues.Clear();
            m_project = null;
        }

        private int GetPresentationBase(CircuitCellKey key, Game.Terrain terrain, int currentValue)
        {
            return m_authoritativeValues.TryGetValue(key, out int value) ? value : currentValue;
        }

        private void ApplyOrForget(CircuitCellKey key, List<PendingOperation> operations,
            int authoritativeValue)
        {
            SubsystemTerrain subsystem = m_project?.FindSubsystem<SubsystemTerrain>(false);
            if (subsystem == null || operations.Count == 0)
            {
                ApplyCellPresentation(key.Point, authoritativeValue);
                m_pending.Remove(key);
                m_authoritativeValues.Remove(key);
                return;
            }
            ApplyPresentation(key, authoritativeValue, operations);
        }

        // Source: Survivalcraft/Game/SwitchBlock.cs:SwitchBlock.SetLeverState
        // Source: Survivalcraft/Game/RotateableMountedElectricElementBlock.cs:SetRotation
        private void ApplyPresentation(CircuitCellKey key, int authoritativeValue,
            List<PendingOperation> operations)
        {
            int value = authoritativeValue;
            for (int i = 0; i < operations.Count; i++)
            {
                PendingOperation operation = operations[i];
                int contents = Game.Terrain.ExtractContents(value);
                int data = Game.Terrain.ExtractData(value);
                if (operation.Kind == CircuitInteractionKind.SwitchToggle &&
                    contents == SwitchBlock.Index)
                {
                    value = SwitchBlock.SetLeverState(value,
                        !SwitchBlock.GetLeverState(value));
                }
                else if (operation.Kind == CircuitInteractionKind.FurnitureSwitch)
                {
                    // Furniture variants live in their block entity; the host result supplies
                    // the authoritative design value when this click is accepted.
                    continue;
                }
                else if (operation.Kind == CircuitInteractionKind.Rotate)
                {
                    value = Game.Terrain.ReplaceData(value, RotateableMountedElectricElementBlock
                        .SetRotation(data, RotateableMountedElectricElementBlock
                            .GetRotation(data) + 1));
                }
            }
            Game.Terrain terrain = m_project?.FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            if (terrain == null || terrain.GetCellValue(key.Point.X, key.Point.Y,
                    key.Point.Z) == value)
                return;
            ApplyCellPresentation(key.Point, value);
        }

        private bool ApplyCellPresentation(Point3 point, int value)
        {
            Game.Terrain terrain = m_project?.FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            if (terrain == null)
            {
                LogProblem("presentation-world", "terrain is unavailable for predicted cell " +
                    point.X + "," + point.Y + "," + point.Z);
                return false;
            }
            if (terrain.GetChunkAtCell(point.X, point.Z) == null)
            {
                LogProblem("presentation-chunk", "chunk is unavailable for predicted cell " +
                    point.X + "," + point.Y + "," + point.Z);
                return false;
            }
            terrain.SetCellValueFast(point.X, point.Y, point.Z, value);
            TerrainChunk chunk = terrain.GetChunkAtCell(point.X, point.Z);
            if (chunk != null)
            {
                m_project.FindSubsystem<SubsystemTerrain>(false)?.TerrainUpdater?
                    .DowngradeChunkNeighborhoodState(chunk.Coords, 0,
                        TerrainChunkState.InvalidVertices1, forceGeometryRegeneration: true);
            }
            return true;
        }

        private bool TryConsumeInteract(ComponentInput input)
        {
            try
            {
                PlayerInput playerInput = input.PlayerInput;
                playerInput.Interact = null;
                m_parentField.ModifyParentField(input, "m_playerInput", playerInput,
                    typeof(ComponentInput));
                return true;
            }
            catch (Exception e)
            {
                LogProblem("input-clear", e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private bool TryFindPending(long requestId, out CircuitCellKey key,
            out List<PendingOperation> operations, out int index)
        {
            foreach (KeyValuePair<CircuitCellKey, List<PendingOperation>> pair in m_pending)
            {
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (pair.Value[i].RequestId != requestId) continue;
                    key = pair.Key;
                    operations = pair.Value;
                    index = i;
                    return true;
                }
            }
            key = default;
            operations = null;
            index = -1;
            return false;
        }

        private long NextRequestId()
        {
            m_nextRequestId = m_nextRequestId == long.MaxValue ? 1 : m_nextRequestId + 1;
            return m_nextRequestId;
        }

        private static bool MatchesOperation(ElectricElement element,
            CircuitInteractionKind kind)
        {
            if (kind == CircuitInteractionKind.ButtonPress)
                return element is ButtonElectricElement ||
                    element is ButtonFurnitureElectricElement;
            if (kind == CircuitInteractionKind.SwitchToggle)
                return element is SwitchElectricElement;
            if (kind == CircuitInteractionKind.FurnitureSwitch)
                return element is SwitchFurnitureElectricElement;
            return kind == CircuitInteractionKind.Rotate &&
                element is RotateableElectricElement;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        // Source: Survivalcraft/Game/ButtonElectricElement.cs:ButtonElectricElement.Press
        private static void PlayClick(SubsystemAudio audio, Point3 point)
        {
            audio?.PlaySound("Audio/Click", 1f, 0f,
                new Vector3(point.X, point.Y, point.Z), 2f, autoDelay: true);
        }

        private static void LogProblem(string code, string detail)
        {
            Log.Warning("[SuMP] circuit interaction " + code + ": " + detail);
        }
    }
}

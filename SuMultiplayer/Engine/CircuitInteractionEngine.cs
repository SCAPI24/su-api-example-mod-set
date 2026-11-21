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
    /// Sends client circuit interactions to the host and tracks their request IDs.
    /// Terrain presentation and circuit runtime state remain host-authoritative.
    /// </summary>
    public sealed class CircuitUseInputSink : IEntityUseInputSink,
        ICircuitPresentationPort
    {
        private sealed class PendingOperation
        {
            public long RequestId;
            public CircuitInteractionKind Kind;
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
        private Func<CircuitInteractionRequestData, bool> m_setCircuitRequest;
        private Project m_pendingProject;
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

        public bool HasPendingOperationAt(int x, int y, int z)
        {
            Point3 point = new Point3(x, y, z);
            for (int face = 0; face < 6; face++)
                if (m_pending.TryGetValue(new CircuitCellKey(point, face),
                        out List<PendingOperation> operations) && operations.Count > 0)
                    return true;
            return false;
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Raycast
        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.GetElectricElement
        public bool TryAcceptInteract(ComponentInput input, ComponentPlayer player)
        {
            if (input == null || player?.ComponentMiner == null) return false;
            Project project = GameManager.Project;
            if (project == null) return false;
            if (!ReferenceEquals(project, m_pendingProject))
            {
                m_pendingProject = project;
                m_pending.Clear();
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

            var operation = new PendingOperation
            {
                RequestId = requestId,
                Kind = kind
            };
            if (!m_pending.TryGetValue(key, out List<PendingOperation> operations))
            {
                operations = new List<PendingOperation>();
                m_pending.Add(key, operations);
            }
            // Source: CircuitSyncModule.cs:CircuitSyncModule.SubmitOperation
            operations.Add(operation);
            return true;
        }

        /// <summary>
        /// Resolves a powered input by request ID or applies the host cell value for
        /// Rotate and FurnitureSwitch.
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
            PendingOperation operation = operations[index];
            if (operation.Kind == CircuitInteractionKind.SwitchToggle ||
                operation.Kind == CircuitInteractionKind.ButtonPress)
            {
                // Source: Mod/SuMultiplayer/Modules/CircuitSyncModule.cs:CircuitSyncModule.ApplyClientEvent
                // Powered inputs carry no terrain presentation in their circuit event.
                operations.RemoveAt(index);
                if (operations.Count == 0)
                    m_pending.Remove(key);
                return;
            }
            operations.RemoveAt(index);
            if (authoritativeCellValue.HasValue)
            {
                ApplyCellPresentation(GameManager.Project, key.Point,
                    authoritativeCellValue.Value);
            }
            if (operations.Count == 0) m_pending.Remove(key);
        }

        /// <summary>
        /// Applies a host-authoritative cell value to the local replica.
        /// </summary>
        public void ApplyAuthoritativeCell(int x, int y, int z, int mountingFace,
            int authoritativeCellValue)
        {
            Project project = GameManager.Project;
            ApplyCellPresentation(project, new Point3(x, y, z), authoritativeCellValue);
        }

        // Source: CircuitWorldEngine.cs:CircuitWorldEngine.HandleReplicaCellApplied
        public void ApplyAuthoritativeCell(int x, int y, int z,
            int authoritativeCellValue)
        {
            Project project = GameManager.Project;
            Point3 point = new Point3(x, y, z);
            ApplyCellPresentation(project, point, authoritativeCellValue);
        }

        public void DetachPresentation()
        {
            m_pending.Clear();
            m_pendingProject = null;
        }

        private bool ApplyCellPresentation(Project project, Point3 point, int value)
        {
            Game.Terrain terrain = project?.FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            if (terrain == null)
            {
                // The current world has no terrain subsystem yet.
                LogProblem("presentation-world", "terrain is unavailable at " +
                    point.X + "," + point.Y + "," + point.Z);
                return false;
            }
            if (terrain.GetChunkAtCell(point.X, point.Z) == null)
            {
                LogProblem("presentation-chunk", "chunk is unavailable for authoritative cell " +
                    point.X + "," + point.Y + "," + point.Z);
                return false;
            }
            terrain.SetCellValueFast(point.X, point.Y, point.Z, value);
            TerrainChunk chunk = terrain.GetChunkAtCell(point.X, point.Z);
            if (chunk != null)
            {
                project.FindSubsystem<SubsystemTerrain>(false)?.TerrainUpdater?
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

        private static void LogProblem(string code, string detail)
        {
            ModLog.Warn("[SuMP] circuit interaction " + code + ": " + detail);
        }
    }
}

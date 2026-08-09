using Engine;
using Game;
using GameEntitySystem;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        private sealed class PendingFurnitureBuildContext
        {
            public Project Project;
            public Point3 Start;
            public Point3[] SourceCells = Array.Empty<Point3>();
            public int[] SourceValues = Array.Empty<int>();
            public double ExpiresAt;
        }

        private PendingFurnitureBuildContext m_pendingFurnitureBuild;
        private int m_nextFurnitureBuildRequestId;
        private readonly Dictionary<int, int> m_lastFurnitureBuildRequestIds =
            new Dictionary<int, int>();

        // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
        // SubsystemFurnitureBlockBehavior.ScanDesign
        internal void CapturePendingFurnitureBuild(CellFace start, ComponentMiner componentMiner)
        {
            Project project = componentMiner?.Project;
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null || componentMiner?.ComponentPlayer == null)
            {
                m_pendingFurnitureBuild = null;
                return;
            }

            var cells = new List<Point3>();
            var values = new List<int>();
            int startValue = terrain.Terrain.GetCellValue(
                start.X, start.Y, start.Z);
            if (BlocksManager.Blocks[Terrain.ExtractContents(startValue)] is FurnitureBlock)
            {
                cells.Add(start.Point);
                values.Add(Terrain.ReplaceLight(startValue, 0));
            }
            else
            {
                var pending = new Stack<Point3>();
                var visited = new HashSet<Point3>();
                pending.Push(start.Point);
                Point3 min = start.Point;
                Point3 max = start.Point;
                while (pending.Count > 0 && cells.Count <= 4096)
                {
                    Point3 point = pending.Pop();
                    if (!visited.Add(point)) continue;
                    int value = terrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
                    if (IsFurnitureBuildSourceValueDisallowed(value))
                    {
                        cells.Clear();
                        values.Clear();
                        break;
                    }
                    if (!IsFurnitureBuildSourceValueAllowed(value)) continue;
                    min.X = Math.Min(min.X, point.X);
                    min.Y = Math.Min(min.Y, point.Y);
                    min.Z = Math.Min(min.Z, point.Z);
                    max.X = Math.Max(max.X, point.X);
                    max.Y = Math.Max(max.Y, point.Y);
                    max.Z = Math.Max(max.Z, point.Z);
                    if (max.X - min.X >= 16 || max.Y - min.Y >= 16 ||
                        max.Z - min.Z >= 16)
                    {
                        cells.Clear();
                        values.Clear();
                        break;
                    }
                    cells.Add(point);
                    values.Add(Terrain.ReplaceLight(value, 0));
                    pending.Push(new Point3(point.X - 1, point.Y, point.Z));
                    pending.Push(new Point3(point.X + 1, point.Y, point.Z));
                    pending.Push(new Point3(point.X, point.Y - 1, point.Z));
                    pending.Push(new Point3(point.X, point.Y + 1, point.Z));
                    pending.Push(new Point3(point.X, point.Y, point.Z - 1));
                    pending.Push(new Point3(point.X, point.Y, point.Z + 1));
                }
            }

            m_pendingFurnitureBuild = cells.Count > 0 && cells.Count <= 4096
                ? new PendingFurnitureBuildContext
                {
                    Project = project,
                    Start = start.Point,
                    SourceCells = cells.ToArray(),
                    SourceValues = values.ToArray(),
                    ExpiresAt = Time.RealTime + 120.0
                }
                : null;
        }

        // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
        // SubsystemFurnitureBlockBehavior.ScanDesign
        internal bool TrySubmitPendingFurnitureBuild(Pickable pickable,
            ComponentPlayer player)
        {
            PendingFurnitureBuildContext pending = m_pendingFurnitureBuild;
            if (pending == null || pending.Project != GameManager.Project ||
                Time.RealTime > pending.ExpiresAt || pickable == null || player == null ||
                !(BlocksManager.Blocks[Terrain.ExtractContents(pickable.Value)] is FurnitureBlock))
                return false;

            SubsystemFurnitureBlockBehavior behavior = GameManager.Project?
                .FindSubsystem<SubsystemFurnitureBlockBehavior>(false);
            int designIndex = FurnitureBlock.GetDesignIndex(
                Terrain.ExtractData(pickable.Value));
            FurnitureDesign design = behavior?.GetDesign(designIndex);
            if (design == null)
            {
                m_pendingFurnitureBuild = null;
                return false;
            }

            int valuesCount = design.Resolution * design.Resolution * design.Resolution;
            var designValues = new int[valuesCount];
            for (int i = 0; i < valuesCount; i++)
                designValues[i] = Terrain.ReplaceLight(design.GetValue(i), 0);
            m_nextFurnitureBuildRequestId = m_nextFurnitureBuildRequestId == int.MaxValue
                ? 1 : m_nextFurnitureBuildRequestId + 1;
            NetworkMessageSender.SendFurnitureBuildRequest(
                new FurnitureBuildRequestMessage
                {
                    RequestId = m_nextFurnitureBuildRequestId,
                    Start = pending.Start,
                    Resolution = design.Resolution,
                    Name = design.Name,
                    InteractionMode = (byte)design.InteractionMode,
                    Values = designValues,
                    SourceCells = pending.SourceCells,
                    SourceValues = pending.SourceValues
                });
            m_pendingFurnitureBuild = null;
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
        // SubsystemFurnitureBlockBehavior.ScanDesign
        private void HandleFurnitureBuildRequest(FurnitureBuildRequestMessage message,
            int sourceClientId)
        {
            if (!IsHost || sourceClientId <= 0 || message == null ||
                GameManager.Project == null ||
                (m_lastFurnitureBuildRequestIds.TryGetValue(sourceClientId,
                    out int lastRequestId) && message.RequestId <= lastRequestId))
                return;
            m_lastFurnitureBuildRequestIds[sourceClientId] = message.RequestId;

            if (!m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData))
                return;
            ComponentPlayer player = playerData?.ComponentPlayer;
            ComponentMiner miner = player?.ComponentMiner;
            IInventory inventory = miner?.Inventory;
            if (player?.ComponentBody == null || inventory == null ||
                inventory.ActiveSlotIndex < 0 ||
                inventory.ActiveSlotIndex >= inventory.SlotsCount)
                return;
            int toolValue = inventory.GetSlotValue(inventory.ActiveSlotIndex);
            if (!(BlocksManager.Blocks[Terrain.ExtractContents(toolValue)] is HammerBlock) ||
                Vector3.DistanceSquared(player.ComponentBody.Position,
                    new Vector3(message.Start) + new Vector3(0.5f)) > 64f)
                return;

            int expectedDesignValues = message.Resolution * message.Resolution *
                message.Resolution;
            if (message.Resolution < FurnitureDesign.MinResolution ||
                message.Resolution > FurnitureDesign.MaxResolution ||
                message.Values == null || message.Values.Length != expectedDesignValues ||
                message.SourceCells == null || message.SourceValues == null ||
                message.SourceCells.Length == 0 ||
                message.SourceCells.Length > 4096 ||
                message.SourceCells.Length != message.SourceValues.Length ||
                message.Name == null || message.Name.Length > FurnitureDesign.MaxNameLength ||
                !Enum.IsDefined(typeof(FurnitureInteractionMode),
                    (FurnitureInteractionMode)message.InteractionMode))
                return;

            SubsystemTerrain terrain = GameManager.Project
                .FindSubsystem<SubsystemTerrain>(false);
            SubsystemFurnitureBlockBehavior furniture = GameManager.Project
                .FindSubsystem<SubsystemFurnitureBlockBehavior>(false);
            SubsystemPickables pickables = GameManager.Project
                .FindSubsystem<SubsystemPickables>(false);
            if (terrain == null || furniture == null || pickables == null) return;

            var unique = new HashSet<Point3>();
            for (int i = 0; i < message.SourceCells.Length; i++)
            {
                Point3 point = message.SourceCells[i];
                if (!unique.Add(point) || Math.Abs(point.X - message.Start.X) >= 16 ||
                    Math.Abs(point.Y - message.Start.Y) >= 16 ||
                    Math.Abs(point.Z - message.Start.Z) >= 16)
                    return;
                int current = Terrain.ReplaceLight(terrain.Terrain.GetCellValue(
                    point.X, point.Y, point.Z), 0);
                int expected = Terrain.ReplaceLight(message.SourceValues[i], 0);
                bool existingFurniture = BlocksManager.Blocks[
                    Terrain.ExtractContents(current)] is FurnitureBlock;
                if (current != expected ||
                    (!existingFurniture && !IsFurnitureBuildSourceValueAllowed(current)) ||
                    IsFurnitureBuildSourceValueDisallowed(current))
                    return;
            }
            if (!unique.Contains(message.Start)) return;

            FurnitureDesign design;
            try
            {
                design = new FurnitureDesign(terrain)
                {
                    Name = message.Name,
                    InteractionMode = (FurnitureInteractionMode)message.InteractionMode
                };
                design.SetValues(message.Resolution, message.Values);
                design = furniture.TryAddDesign(design);
            }
            catch
            {
                return;
            }
            if (design == null) return;

            SubsystemGameInfo gameInfo = GameManager.Project
                .FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo?.WorldSettings.GameMode != GameMode.Creative)
            {
                foreach (Point3 point in message.SourceCells)
                    terrain.DestroyCell(0, point.X, point.Y, point.Z, 0,
                        noDrop: true, noParticleSystem: true);
            }

            int furnitureValue = Terrain.MakeBlockValue(227, 0,
                FurnitureBlock.SetDesignIndex(0, design.Index,
                    design.ShadowStrengthFactor, design.IsLightEmitter));
            int count = MathUtils.Clamp(design.Resolution, 4, 8);
            Matrix matrix = player.ComponentBody.Matrix;
            Vector3 position = matrix.Translation + matrix.Forward + Vector3.UnitY;
            pickables.AddPickable(furnitureValue, count, position, null, null);
            miner.DamageActiveTool(1);
            miner.Poke(forceRestart: false);
            if (miner.ComponentCreature.PlayerStats != null)
                miner.ComponentCreature.PlayerStats.FurnitureItemsMade += count;
            m_worldObjectSynchronizer?.PublishLocalFurnitureChangesNow();
        }

        // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
        // SubsystemFurnitureBlockBehavior.IsValueAllowed
        private static bool IsFurnitureBuildSourceValueAllowed(int value)
        {
            return Terrain.ExtractContents(value) switch
            {
                21 or 3 or 67 or 7 or 72 or 5 or 26 or 4 or 68 or 73 or 150 or
                71 or 126 or 47 or 46 or 15 or 208 or 31 or 17 or 18 or 92 => true,
                _ => false
            };
        }

        // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
        // SubsystemFurnitureBlockBehavior.IsValueDisallowed
        private static bool IsFurnitureBuildSourceValueDisallowed(int value)
        {
            int contents = Terrain.ExtractContents(value);
            int data = Terrain.ExtractData(value);
            return (contents == 18 || contents == 92) &&
                FluidBlock.GetLevel(data) != 0 && FluidBlock.GetIsTop(data);
        }
    }
}

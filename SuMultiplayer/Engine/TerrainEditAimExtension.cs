using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuAPI;
using SuMultiplayer.Players;
using PA = SuMultiplayer.Protocol.Authority;

namespace SuMultiplayer.Engine
{
    // Source: ComponentPlayer.cs:ComponentPlayer.Update; ComponentMiner.cs:ComponentMiner.Dig/Place
    internal sealed class TerrainEditAimExtension : UpdateExtension<ComponentPlayer>
    {
        private readonly TerrainBridge m_bridge;
        private readonly NetworkAvatarSpawner m_players;
        private readonly List<PlayerData> m_localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
        private Game.Terrain m_terrain;
        private PlayerStats m_stats;
        private long m_dugBefore;
        private long m_placedBefore;
        private long m_lastMissingLogMs;
        private HitSample? m_dig;
        private HitSample? m_place;

        private readonly struct HitSample
        {
            public HitSample(TerrainRaycastResult hit, Game.Terrain terrain)
            {
                Hit = hit;
                Cell = hit.CellFace.Point;
                Adjacent = Cell + CellFace.FaceToPoint3(hit.CellFace.Face);
                CellBefore = Read(terrain, Cell);
                AdjacentBefore = Read(terrain, Adjacent);
            }

            public TerrainRaycastResult Hit { get; }
            public Point3 Cell { get; }
            public Point3 Adjacent { get; }
            public int CellBefore { get; }
            public int AdjacentBefore { get; }
        }

        public TerrainEditAimExtension(ComponentPlayer host, TerrainBridge bridge,
            NetworkAvatarSpawner players) : base(host)
        {
            m_bridge = bridge;
            m_players = players;
        }

        public override void Update(float dt)
        {
            ResetSample();
            base.Update(dt);
            if (!m_bridge.ClientReplica || !ReferenceEquals(Host.Project, GameManager.Project) ||
                m_players.Contains(Host.PlayerData)) return;

            PlayerInput input = Host.ComponentInput.PlayerInput;
            if (!input.Dig.HasValue && !input.Interact.HasValue) return;
            m_terrain = Host.Project.FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            m_stats = Host.PlayerStats;
            if (m_terrain == null || m_stats == null) return;

            m_dugBefore = m_stats.BlocksDug;
            m_placedBefore = m_stats.BlocksPlaced;
            m_dig = Sample(input.Dig, RaycastMode.Digging);
            m_place = Sample(input.Interact, RaycastMode.Interaction);
        }

        public override void AfterUpdate(float dt, InvocationResult result)
        {
            try
            {
                if (!result.HostExecuted || m_terrain == null || m_stats == null) return;
                // The miner increments these only after executing Dig or Place. Using a
                // tool that directly modifies a cell (for example a rake) does not count.
                if (m_stats.BlocksDug > m_dugBefore)
                    RecordDig();
                if (m_stats.BlocksPlaced > m_placedBefore)
                    RecordPlacement();
            }
            finally
            {
                ResetSample();
            }
        }

        private HitSample? Sample(Ray3? ray, RaycastMode mode)
        {
            if (!ray.HasValue) return null;
            TerrainRaycastResult? hit = Host.ComponentMiner.Raycast<TerrainRaycastResult>(ray.Value, mode);
            return hit.HasValue ? new HitSample(hit.Value, m_terrain) : null;
        }

        private void RecordDig()
        {
            if (m_dig is not HitSample sample)
            {
                WarnMissingHit("dig");
                return;
            }
            if (Read(m_terrain, sample.Cell) != sample.CellBefore)
                Record(sample.Hit, sample.Cell, isPlacement: false);
        }

        private void RecordPlacement()
        {
            if (m_place is not HitSample sample)
            {
                WarnMissingHit("place");
                return;
            }
            bool hitCellChanged = Read(m_terrain, sample.Cell) != sample.CellBefore;
            bool adjacentChanged = Read(m_terrain, sample.Adjacent) != sample.AdjacentBefore;
            // Source: SlabBlock.cs:SlabBlock.GetPlacementValue
            // A merged slab changes the struck cell. Normal placement uses the cell across the face.
            if (hitCellChanged && (!adjacentChanged || IsSlabMerge(sample)))
                Record(sample.Hit, sample.Cell, isPlacement: true);
            else if (adjacentChanged)
                Record(sample.Hit, sample.Adjacent, isPlacement: true);
        }

        private bool IsSlabMerge(HitSample sample)
        {
            int contents = Game.Terrain.ExtractContents(sample.CellBefore);
            return BlocksManager.Blocks[contents] is SlabBlock &&
                Game.Terrain.ExtractContents(Read(m_terrain, sample.Cell)) != contents &&
                sample.Hit.CellFace.Face == (SlabBlock.GetIsTop(Game.Terrain.ExtractData(sample.CellBefore)) ? 5 : 4);
        }

        private void Record(TerrainRaycastResult hit, Point3 target, bool isPlacement)
        {
            int slot = -1;
            if (m_players.TryCollectLocalPlayers(Host.Project, m_localPlayers, out _, out _))
            {
                for (int i = 0; i < m_localPlayers.Count; i++)
                {
                    if (!ReferenceEquals(m_localPlayers[i]?.ComponentPlayer, Host)) continue;
                    slot = i;
                    break;
                }
            }
            m_localPlayers.Clear();
            if (slot < 0) WarnMissingHit("local player slot");
            m_bridge.RecordClientCellEditAim(target.X, target.Y, target.Z, new PA.CellEditAim
            {
                Slot = slot,
                RayX = hit.Ray.Position.X,
                RayY = hit.Ray.Position.Y,
                RayZ = hit.Ray.Position.Z,
                DirectionX = hit.Ray.Direction.X,
                DirectionY = hit.Ray.Direction.Y,
                DirectionZ = hit.Ray.Direction.Z,
                HitX = hit.CellFace.X,
                HitY = hit.CellFace.Y,
                HitZ = hit.CellFace.Z,
                HitFace = hit.CellFace.Face,
                IsPlacement = isPlacement
            });
        }

        private static int Read(Game.Terrain terrain, Point3 cell) =>
            Game.Terrain.ReplaceLight(terrain.GetCellValue(cell.X, cell.Y, cell.Z), 0);

        private void ResetSample()
        {
            m_terrain = null;
            m_stats = null;
            m_dig = null;
            m_place = null;
        }

        private void WarnMissingHit(string operation)
        {
            long now = Environment.TickCount64;
            if (m_lastMissingLogMs != 0 && now - m_lastMissingLogMs < 5000) return;
            m_lastMissingLogMs = now;
            ModLog.Warn("[SuMP] No matching terrain aim for the completed " + operation + " action.");
        }
    }
}

using Engine;
using Game;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public sealed class SuSubsystemFireBlockBehavior : SubsystemFireBlockBehavior, IUpdateable
    {
        private const int MaximumSoundPointsPerFrame = 50;
        private const double ClientSoundRefreshInterval = 0.25;
        private const double HostFireChunkScanInterval = 0.5;

        private SubsystemTime m_subsystemTime;
        private SubsystemAudio m_subsystemAudio;
        private SubsystemAmbientSounds m_subsystemAmbientSounds;
        private IDictionary m_fireData;
        private SubsystemTerrain m_subsystemTerrain;
        private readonly List<Point3> m_soundPoints = new List<Point3>();
        private readonly HashSet<TerrainChunk> m_scannedHostFireChunks =
            new HashSet<TerrainChunk>();
        private int m_soundPointIndex;
        private float m_soundPointRemainder;
        private float m_soundIntensity;
        private float m_fireSoundVolume;
        private double m_nextClientSoundRefresh;
        private double m_nextHostFireChunkScan;
        private bool m_clientPresentationActive;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_subsystemTime = Project.FindSubsystem<SubsystemTime>(true);
            m_subsystemAudio = Project.FindSubsystem<SubsystemAudio>(true);
            m_subsystemAmbientSounds = Project.FindSubsystem<SubsystemAmbientSounds>(true);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(true);
            // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:SubsystemFireBlockBehavior.m_fireData
            m_fireData = Game.Program.ModManager.ModParentField.GetParentField<IDictionary>(
                this, "m_fireData", typeof(SubsystemFireBlockBehavior));
        }

        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
            {
                if (m_clientPresentationActive)
                {
                    // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:
                    // SubsystemFireBlockBehavior.Update
                    // Restart native timers when leaving client presentation-only mode.
                    Game.Program.ModManager.ModParentField.ModifyParentField(
                        this, "m_lastScanTime", m_subsystemTime.GameTime,
                        typeof(SubsystemFireBlockBehavior));
                    m_clientPresentationActive = false;
                    m_soundPoints.Clear();
                    m_soundPointIndex = 0;
                    m_soundPointRemainder = 0f;
                }
                if (ScMultiplayer.IsHost)
                    RegisterLoadedHostFireCells();
                base.Update(dt);
                return;
            }

            m_clientPresentationActive = true;
            UpdateClientFireSound(dt);
        }

        // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:
        // SubsystemFireBlockBehavior.Update
        // Clients receive authoritative fire terrain from the host. They keep only the ambient
        // fire sound calculation and never run random burn-away or expansion mutations locally.
        private void UpdateClientFireSound(float dt)
        {
            if (m_fireData == null || m_subsystemAudio == null ||
                m_subsystemAmbientSounds == null)
                return;
            double now = Time.RealTime;
            if (now >= m_nextClientSoundRefresh)
            {
                m_nextClientSoundRefresh = now + ClientSoundRefreshInterval;
                if (m_soundPoints.Count == 0)
                {
                    foreach (object key in m_fireData.Keys)
                    {
                        if (key is Point3 point)
                            m_soundPoints.Add(point);
                    }
                    m_soundPointIndex = 0;
                    m_soundPointRemainder = 0f;
                    m_soundIntensity = 0f;
                    if (m_soundPoints.Count == 0)
                        m_fireSoundVolume = 0f;
                }
            }

            if (m_soundPoints.Count > 0)
            {
                float work = MathUtils.Min(
                    1f * dt * m_soundPoints.Count + m_soundPointRemainder,
                    MaximumSoundPointsPerFrame);
                int count = (int)work;
                m_soundPointRemainder = work - count;
                int end = MathUtils.Min(m_soundPointIndex + count, m_soundPoints.Count);
                while (m_soundPointIndex < end)
                {
                    Point3 point = m_soundPoints[m_soundPointIndex++];
                    if (m_fireData.Contains(point))
                    {
                        m_soundIntensity += 1f /
                            (m_subsystemAudio.CalculateListenerDistanceSquared(
                                new Vector3(point)) + 0.01f);
                    }
                }
                if (m_soundPointIndex >= m_soundPoints.Count)
                {
                    m_fireSoundVolume = 0.75f * m_soundIntensity;
                    m_soundPoints.Clear();
                    m_soundPointIndex = 0;
                    m_soundIntensity = 0f;
                }
            }
            m_subsystemAmbientSounds.FireSoundVolume = MathUtils.Max(
                m_subsystemAmbientSounds.FireSoundVolume, m_fireSoundVolume);
        }

        // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:
        // SubsystemFireBlockBehavior.OnBlockGenerated
        // A transferred world can already contain fire before normal block notifications begin.
        // Scan each loaded host chunk once and register only missing fire cells in the original
        // timer table, so native burn-away and expansion remain host-authoritative.
        private void RegisterLoadedHostFireCells()
        {
            if (m_subsystemTerrain == null || m_fireData == null ||
                Time.RealTime < m_nextHostFireChunkScan)
                return;
            m_nextHostFireChunkScan = Time.RealTime + HostFireChunkScanInterval;
            TerrainChunk[] allocatedChunks = m_subsystemTerrain.Terrain.AllocatedChunks;
            m_scannedHostFireChunks.RemoveWhere(item => item == null ||
                !allocatedChunks.Contains(item));
            TerrainChunk chunk = allocatedChunks.FirstOrDefault(
                item => item != null && item.IsLoaded &&
                    !m_scannedHostFireChunks.Contains(item));
            if (chunk == null) return;
            m_scannedHostFireChunks.Add(chunk);
            for (int x = 0; x < 16; x++)
            {
                for (int z = 0; z < 16; z++)
                {
                    for (int y = 0; y < 256; y++)
                    {
                        int value = chunk.GetCellValueFast(x, y, z);
                        if (Terrain.ExtractContents(value) != 104) continue;
                        var point = new Point3(chunk.Origin.X + x, y, chunk.Origin.Y + z);
                        if (!m_fireData.Contains(point))
                            OnBlockGenerated(value, point.X, point.Y, point.Z, true);
                    }
                }
            }
        }
    }
}

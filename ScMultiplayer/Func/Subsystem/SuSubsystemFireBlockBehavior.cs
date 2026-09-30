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
        // 2026-10-01：客户端火声按**地形**算，扫描监听者（本端玩家）周边的火格。
        private const int ClientFireScanRadius = 24;
        private const int ClientFireScanMax = 256;
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
        // 2026-10-01：**建立领地时本来就在燃烧**的格（碳方块/营火这类方块自身的燃烧，
        // 燃烧时间 999、永不烧完）。只禁"新点燃/蔓延进来的火"，这些必须一直保留。
        private readonly HashSet<Point3> m_preexistingClaimFire = new HashSet<Point3>();

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
            bool networkActive = ScMultiplayer.currentInstance?.IsNetworkSessionActive(Project) == true;
            if (!networkActive || ScMultiplayer.currentInstance?.IsNetworkHost(Project) == true)
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
                if (ScMultiplayer.currentInstance?.IsNetworkHost(Project) == true)
                {
                    RegisterLoadedHostFireCells();
                    ExtinguishClaimFire(registerPreexisting: true);
                }
                base.Update(dt);
                // 《玩家领地》P6：领地内不允许有火（设计稿 §5：不得被点燃、火不蔓延进来）。
                // 引擎的 SetCellOnFire 不是虚方法（无法直接拦），这里用"点燃后同帧熄灭"的
                // 改回式执行：先清一次（旧火），跑完引擎更新再清一次（本帧新点燃/蔓延进来的）。
                if (ScMultiplayer.currentInstance?.IsNetworkHost(Project) == true)
                    ExtinguishClaimFire(registerPreexisting: false);
                return;
            }

            if (!m_clientPresentationActive)
            {
                m_clientPresentationActive = true;
                // 2026-10-01（用户口径）：**客户端完全不维护火表**。进入表现模式就把旧表清掉 ——
                // 旧实现读 `m_fireData` 放火声，而客户端不跑引擎的火表老化 ⇒ 表只增不减，
                // 方块早被主机烧没了却一直响（桥实测：客户端 23 条 vs 主机 10 条）。
                m_fireData?.Clear();
                m_soundPoints.Clear();
                m_soundPointIndex = 0;
                m_soundPointRemainder = 0f;
                m_soundIntensity = 0f;
                m_fireSoundVolume = 0f;
            }
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
                CollectClientFirePointsNearLocalPlayer();
                m_soundPointIndex = 0;
                m_soundPointRemainder = 0f;
                m_soundIntensity = 0f;
                if (m_soundPoints.Count == 0)
                    m_fireSoundVolume = 0f;
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
                    // 按**地形现实**判断：这一刻还是 FireBlock(104) 才计入（火表在客户端已不再维护）
                    if (m_subsystemTerrain.Terrain.GetCellContents(point.X, point.Y, point.Z) ==
                        FireBlock.Index)
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

        /// <summary>
        /// 客户端火声的取点：**扫描地形**，收集监听者（本端玩家）周边 `ClientFireScanRadius` 内的
        /// FireBlock 格。客户端不维护火表（用户 2026-10-01 口径），因此表现完全跟"地形现实"走。
        /// </summary>
        private void CollectClientFirePointsNearLocalPlayer()
        {
            m_soundPoints.Clear();
            Terrain terrain = m_subsystemTerrain?.Terrain;
            if (terrain == null || GameManager.Project == null)
                return;
            SubsystemPlayers players =
                GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer local = players != null && players.ComponentPlayers.Count > 0
                ? players.ComponentPlayers[0]
                : null;
            if (local?.ComponentBody == null)
                return;
            Vector3 position = local.ComponentBody.Position;
            int cx = Terrain.ToCell(position.X);
            int cy = Terrain.ToCell(position.Y);
            int cz = Terrain.ToCell(position.Z);
            for (int x = cx - ClientFireScanRadius; x <= cx + ClientFireScanRadius; x++)
            for (int z = cz - ClientFireScanRadius; z <= cz + ClientFireScanRadius; z++)
            for (int y = MathUtils.Max(0, cy - ClientFireScanRadius);
                y <= MathUtils.Min(254, cy + ClientFireScanRadius); y++)
            {
                if (m_soundPoints.Count >= ClientFireScanMax)
                    return;
                if (terrain.GetCellContentsFast(x, y, z) == FireBlock.Index)
                    m_soundPoints.Add(new Point3(x, y, z));
            }
        }

        // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:
        // SubsystemFireBlockBehavior.OnBlockGenerated
        // A transferred world can already contain fire before normal block notifications begin.
        // Scan each loaded host chunk once and register only missing fire cells in the original
        // timer table, so native burn-away and expansion remain host-authoritative.
        private void RegisterLoadedHostFireCells()        {
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

        /// <summary>
        /// 《玩家领地》P6：**领地内不允许有火**（设计稿 §5 锁定结论 7：不得被点燃、火不蔓延进来）。
        ///
        /// 引擎的 `SetCellOnFire` 不是虚方法、无法直接拦；这里用"点燃后同帧熄灭"的改回式执行：
        /// 每帧在 `base.Update(dt)` 前后各清一次 —— 已有的火、以及本帧刚被点燃/蔓延进来的火都不会留下，
        /// 火源被清掉后自然也不会再从领地内往外蔓延。熄灭用 `ChangeCell(...,0)`，会走主机地形广播，
        /// 客户端看到的也是"没着起来"。
        /// </summary>
        /// <summary>
        /// 该火格是否紧邻"火源方块"（营火等自身带火的方块）。这类火是方块本身的燃烧表现，
        /// 必须保留；只有"蔓延进来的野火"才在领地内被熄灭。
        /// </summary>
        private static bool IsAdjacentToFireSource(Terrain terrain, Point3 p)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        if (terrain.GetCellContents(p.X + dx, p.Y + dy, p.Z + dz) ==
                            CampfireBlock.Index)
                            return true;
                    }
            return false;
        }

        private void ExtinguishClaimFire(bool registerPreexisting)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null || m_fireData == null || m_fireData.Count == 0 ||
                m_subsystemTerrain?.Terrain == null)
                return;
            List<Point3> doomed = null;
            Terrain fireTerrain = m_subsystemTerrain.Terrain;
            foreach (object key in m_fireData.Keys)
            {
                if (!(key is Point3 point) || mod.OwnerClaimAt(point) == null)
                    continue;
                // ① base.Update **之前**这一遍：此刻火表里还在烧的领地内格 = "建领地之前就在燃烧"的，
                //    登记进保护集并保留 —— 否则建立领地的瞬间碳方块/营火的点燃效果就被清掉（实测）。
                if (registerPreexisting)
                {
                    m_preexistingClaimFire.Add(point);
                    continue;
                }
                // ② base.Update **之后**这一遍：只熄"本帧新点燃/蔓延进来的火"。
                if (m_preexistingClaimFire.Contains(point))
                    continue;
                if (IsAdjacentToFireSource(fireTerrain, point))
                    continue;                                  // 火源（营火等）上的火 → 保留
                (doomed ?? (doomed = new List<Point3>())).Add(point);
            }
            // 保护集里已不再燃烧的格及时释放
            if (m_preexistingClaimFire.Count > 0)
                m_preexistingClaimFire.RemoveWhere(cell => !m_fireData.Contains(cell));
            if (doomed == null)
                return;
            for (int i = 0; i < doomed.Count; i++)
            {
                Point3 point = doomed[i];
                m_fireData.Remove(point);
                m_subsystemTerrain.ChangeCell(point.X, point.Y, point.Z, 0);
            }
        }
    }
}

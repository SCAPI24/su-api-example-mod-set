using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 循环环境音同步的**引擎适配层**（<see cref="IAmbientSoundPort"/> +
    /// <see cref="IAmbientSoundVolumeSource"/> 的实现，组合根注入）。
    ///
    /// 主机：
    ///   · 用 <see cref="NetworkAvatarSpawner.ClientIds"/> / <c>TryGetAvatarCell</c> 取每个
    ///     在线玩家端的主机侧化身位置（与全局声音中继同一个位置来源）；
    ///   · 以该位置做一次**有界**只读扫描（<see cref="AmbientSoundScanner"/>，流体半径 8 /
    ///     火与营火半径 12），再用引擎真实的 <c>SubsystemAudio.CalculateVolume</c> 换算：
    ///       - 岩浆：<c>CalculateVolume(distance, 2f, 3.5f)</c>
    ///         （Source: SubsystemMagmaBlockBehavior.cs:42）；
    ///       - 水：<c>0.5f * CalculateVolume(distance, 2f, 3.5f)</c>
    ///         （Source: SubsystemWaterBlockBehavior.cs:37）；
    ///       - 营火：<c>CalculateVolume(distance, 2f)</c>
    ///         （Source: SubsystemCampfireBlockBehavior.cs:60）；
    ///       - 火方块：<c>0.75f * Σ 1/(distanceSquared + 0.01)</c>
    ///         （Source: SubsystemFireBlockBehavior.cs:128 / :164）；
    ///       - 着火实体：<c>CalculateVolume(distance, 2f, 5f)</c>
    ///         （Source: ComponentOnFire.cs:81）；
    ///   · **不修改**主机自己的 <c>SubsystemAmbientSounds</c> 字段 —— 主机音频路径与改前完全一致；
    ///   · 未加载区块由 <c>Terrain.GetCellValueFast</c> 返回 0，绝不触发区块加载。
    ///
    /// 客户端：
    ///   · <see cref="ApplyClientVolumes"/> 只把四个最终音量存进本实例；
    ///   · <see cref="SuSubsystemAmbientSounds"/> 每帧在 <c>base.Update</c> 前读取并施加，
    ///     引擎继续拥有循环 / 淡入淡出 / 暂停 / Sizzles；
    ///   · 断线 / 重连由模块调用 <see cref="ResetClientVolumes"/> 清空（循环暂停）。
    ///
    /// 无静态状态；所有上限 / 截断 / 失败都有计数与限流日志。
    /// </summary>
    public sealed class AmbientSoundReplicaEngine : IAmbientSoundPort, IAmbientSoundVolumeSource
    {
        private const int ProblemLogMs = 10000;
        private const int MaxOnFireEntitiesPerScan = 512;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private Project m_project;
        private SubsystemTerrain m_terrain;
        private SubsystemAudio m_audio;
        private SuSubsystemAmbientSounds m_shell;
        private TerrainCellSource m_cellSource;
        private AmbientContentMask m_mask;
        private bool m_clientReplica;

        // ---------------------------------------------------------------- 客户端状态
        private AmbientVolumes m_clientVolumes;
        private bool m_hasClientVolumes;
        private uint m_clientRevision;

        // ---------------------------------------------------------------- 计数
        private long m_projectResets;
        private long m_shellAttached;
        private long m_shellMissing;
        private long m_maskBuildFailures;
        private long m_scanFailures;
        private long m_scanCapped;
        private long m_onFireCapped;
        private long m_onFireEntities;
        private long m_computed;
        private long m_appliedMessages;
        private long m_rejectedMessages;
        private long m_resets;
        private long m_failures;

        public AmbientSoundReplicaEngine(NetworkAvatarSpawner spawner,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "ambient-sound" : ownerId;
        }

        public bool IsClientReplica => m_clientReplica;

        public long ScanCapped => m_scanCapped;

        // ================================================================= 生命周期

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                DetachShell();
                m_project = project;
                m_projectResets++;
                m_terrain = null;
                m_audio = null;
                m_cellSource = null;
                m_mask = null;
                m_clientReplica = false;
                // 客户端音量缓存**不在这里清**：消息可能早于世界安装到达（IsClientReplica
                // 尚未置位），清掉会让首条状态丢失、要等主机下一次"变化"才有声音。
                // 真正的清理时机：本端确定不是客户端副本（主机 / 单机）时，以及模块的
                // 断线 / 重连 / 下线路径（ResetClientVolumes）。
            }
            if (project == null)
            {
                m_clientReplica = false;
                return;
            }

            m_clientReplica =
                project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain terrainShell &&
                terrainShell.ClientReplica;

            SubsystemAmbientSounds ambient =
                project.FindSubsystem<SubsystemAmbientSounds>(false);
            if (ambient is SuSubsystemAmbientSounds shell)
            {
                if (!ReferenceEquals(shell, m_shell))
                {
                    DetachShell();
                    m_shell = shell;
                    shell.AttachVolumeSource(this);
                    m_shellAttached++;
                }
            }
            else if (m_shell == null)
            {
                m_shellMissing++;
                LogProblem("shell-missing",
                    "没有找到注入后的 SuSubsystemAmbientSounds（Game.SubsystemAmbientSounds 的 " +
                    "Class 替换未生效）→ 客户端无法施加环境音音量（累计 " + m_shellMissing + " 次）");
            }
        }

        public void Shutdown()
        {
            DetachShell();
            m_project = null;
            m_terrain = null;
            m_audio = null;
            m_cellSource = null;
            m_mask = null;
            m_clientReplica = false;
            m_hasClientVolumes = false;
            m_clientVolumes = AmbientVolumes.Zero;
            m_clientRevision = 0;
        }

        private void DetachShell()
        {
            if (m_shell == null) return;
            try { m_shell.DetachVolumeSource(this); } catch (Exception) { }
            m_shell = null;
        }

        // ================================================================= 主机：收集玩家端

        public bool TryCollectClients(List<AmbientSoundPeer> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "target-null";
                return false;
            }
            target.Clear();
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            int count = 0;
            try
            {
                foreach (int clientId in m_spawner.ClientIds)
                {
                    bool hasPosition = m_spawner.TryGetAvatarCell(clientId, out Vector3 position,
                        out _);
                    target.Add(new AmbientSoundPeer(clientId, hasPosition, position));
                    count++;
                }
            }
            catch (Exception e)
            {
                reason = "spawner:" + e.GetType().Name + " " + e.Message;
                m_failures++;
                LogProblem("collect:" + e.GetType().Name,
                    "收集玩家端位置失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 本轮不扫描（不猜位置 / 不往原点发）");
                return false;
            }
            if (count == 0)
            {
                reason = "no-clients";
                return false;
            }
            return true;
        }

        // ================================================================= 主机：扫描 + 换算

        public bool TryComputeVolumes(int clientId, Vector3 position,
            out AmbientVolumes volumes, out string reason)
        {
            volumes = AmbientVolumes.Zero;
            reason = null;
            if (!EnsureWorld(out reason))
            {
                m_scanFailures++;
                return false;
            }
            AmbientContentMask mask = EnsureMask(out string maskReason);
            if (mask == null)
            {
                m_scanFailures++;
                reason = "mask:" + maskReason;
                return false;
            }
            AmbientContentMask effectiveMask = mask;
            try
            {
                if (!AmbientSoundScanner.TryScan(position, m_cellSource, effectiveMask,
                        out AmbientScanResult scan, out string scanReason))
                {
                    m_scanFailures++;
                    reason = "scan:" + scanReason;
                    return false;
                }
                if (scan.CellCapHit || scan.FireCellCapHit)
                {
                    m_scanCapped++;
                    LogProblem("scan-cap",
                        "环境音扫描达到硬上界（cells=" + scan.CellsRead +
                        " fireCells=" + scan.FireCells + "）→ 结果被截断（累计 " + m_scanCapped +
                        " 次；客户端可能在火格极密处听到偏低的火声）");
                }

                float magma = scan.HasMagma
                    ? m_audio.CalculateVolume(scan.MagmaDistance, 2f, 3.5f)
                    : 0f;
                float water = scan.HasWater
                    ? 0.5f * m_audio.CalculateVolume(scan.WaterDistance, 2f, 3.5f)
                    : 0f;
                float campfire = scan.HasCampfire
                    ? m_audio.CalculateVolume(scan.CampfireDistance, 2f)
                    : 0f;
                float fire = AmbientSoundPolicy.FireVolume(scan.FireIntensity);
                float onFire = ComputeOnFireVolume(position, out bool onFireCapped);
                if (onFireCapped)
                {
                    m_onFireCapped++;
                    LogProblem("onfire-cap",
                        "着火实体扫描达到上限 " + MaxOnFireEntitiesPerScan +
                        " 个实体 → 剩余实体未参与火声计算（累计 " + m_onFireCapped + " 次）");
                }
                fire = MathUtils.Max(fire, onFire);

                // 火焰保留原始值（可能 >1；引擎 Sound.Volume 会夹到 0..1），
                // 水 / 岩浆 / 营火按引擎公式天然 <=1，仍做防御性夹取。
                volumes = new AmbientVolumes(fire, MathUtils.Saturate(water),
                    MathUtils.Saturate(magma), MathUtils.Saturate(campfire));
                m_computed++;
                return true;
            }
            catch (Exception e)
            {
                m_scanFailures++;
                reason = "exception:" + e.GetType().Name + " " + e.Message;
                LogProblem("scan-exception:" + e.GetType().Name,
                    "环境音扫描异常（clientId=" + clientId + " " + e.GetType().Name + " " +
                    e.Message + "）→ 该端本轮按 0 处理（明确计数，不静默）");
                volumes = AmbientVolumes.Zero;
                return false;
            }
        }

        /// <summary>
        /// 引擎 <c>ComponentOnFire.Update</c> 每 0.5 s 用
        /// <c>CalculateVolume(CalculateListenerDistance(ComponentBody.Position), 2f, 5f)</c>
        /// 参与 FireSoundVolume（Source: ComponentOnFire.cs:79-84）。主机侧对每个客户端
        /// 取**所有**着火实体的最近距离（上限 512 个实体，超出明确计数），与格子火焰取 max。
        /// </summary>
        private float ComputeOnFireVolume(Vector3 position, out bool capped)
        {
            capped = false;
            float best = float.MaxValue;
            int scanned = 0;
            foreach (Entity entity in m_project.Entities)
            {
                if (++scanned > MaxOnFireEntitiesPerScan)
                {
                    capped = true;
                    break;
                }
                ComponentOnFire onFire = entity.FindComponent<ComponentOnFire>();
                if (onFire == null || !onFire.IsOnFire) continue;
                ComponentBody body = onFire.ComponentBody;
                if (body == null) continue;
                m_onFireEntities++;
                float distanceSquared = Vector3.DistanceSquared(position, body.Position);
                if (distanceSquared < best) best = distanceSquared;
            }
            if (best == float.MaxValue) return 0f;
            return m_audio.CalculateVolume(MathUtils.Sqrt(best), 2f, 5f);
        }

        private bool EnsureWorld(out string reason)
        {
            reason = null;
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (m_terrain == null)
            {
                m_terrain = m_project.FindSubsystem<SubsystemTerrain>(false);
            }
            if (m_terrain == null || m_terrain.Terrain == null)
            {
                reason = "no-terrain";
                return false;
            }
            if (m_cellSource == null || !ReferenceEquals(m_cellSource.Terrain, m_terrain.Terrain))
            {
                m_cellSource = new TerrainCellSource(m_terrain.Terrain);
            }
            if (m_audio == null)
            {
                m_audio = m_project.FindSubsystem<SubsystemAudio>(false);
                if (m_audio == null)
                {
                    reason = "no-audio-subsystem";
                    return false;
                }
            }
            return true;
        }

        private AmbientContentMask EnsureMask(out string reason)
        {
            reason = null;
            if (m_mask != null) return m_mask;
            try
            {
                Block[] blocks = BlocksManager.Blocks;
                if (blocks == null || blocks.Length == 0)
                {
                    m_maskBuildFailures++;
                    reason = "blocks-not-loaded";
                    LogMaskFailure(reason);
                    return null;
                }
                var water = new bool[blocks.Length];
                var magma = new bool[blocks.Length];
                for (int i = 0; i < blocks.Length; i++)
                {
                    Block block = blocks[i];
                    if (block is WaterBlock) water[i] = true;
                    else if (block is MagmaBlock) magma[i] = true;
                }
                m_mask = new AmbientContentMask(water, magma);
                return m_mask;
            }
            catch (Exception e)
            {
                m_maskBuildFailures++;
                reason = e.GetType().Name + " " + e.Message;
                LogMaskFailure(reason);
                return null;
            }
        }

        private void LogMaskFailure(string reason)
        {
            LogProblem("mask:" + reason,
                "构建流体内容表失败（" + reason + "）→ 环境音扫描无法区分水 / 岩浆" +
                "（累计 " + m_maskBuildFailures + " 次；下一轮会重试）");
        }

        // ================================================================= 客户端：施加 / 重置

        public bool ApplyClientVolumes(uint revision, in AmbientVolumes volumes, out string reason)
        {
            reason = null;
            // 消息可能早于客户端世界安装（IsClientReplica 还没置位）到达：先缓存。
            // 是否真正施加由 TryGetClientVolumes 的 IsClientReplica 决定，主机外壳永远走
            // base.Update，不会读取这份缓存。
            if (!volumes.IsValid())
            {
                m_rejectedMessages++;
                reason = "volumes-invalid(" + volumes.Describe() + ")";
                return false;
            }
            if (revision <= m_clientRevision)
            {
                m_rejectedMessages++;
                reason = "stale-revision(" + revision + "<=" + m_clientRevision + ")";
                return false;
            }
            m_clientVolumes = volumes;
            m_hasClientVolumes = true;
            m_clientRevision = revision;
            m_appliedMessages++;
            return true;
        }

        public void ResetClientVolumes(string reason)
        {
            if (!m_hasClientVolumes && m_clientRevision == 0) return;
            m_hasClientVolumes = false;
            m_clientVolumes = AmbientVolumes.Zero;
            m_clientRevision = 0;
            m_resets++;
            m_diagnostics?.Info(m_ownerId,
                "客户端环境音已清零（" + reason + "）→ 三条循环下一帧暂停");
        }

        public bool TryGetClientVolumes(out AmbientVolumes volumes, out string reason)
        {
            volumes = m_clientVolumes;
            reason = null;
            if (!m_clientReplica)
            {
                reason = "not-client";
                return false;
            }
            if (!m_hasClientVolumes)
            {
                reason = "client-no-data";
                return false;
            }
            return true;
        }

        // ================================================================= 诊断

        public string Describe() =>
            "ambientEngine projects=" + m_projectResets +
            " shellAttached=" + m_shellAttached +
            " shellMissing=" + m_shellMissing +
            " maskFailures=" + m_maskBuildFailures +
            " computed=" + m_computed +
            " scanFailures=" + m_scanFailures +
            " scanCapped=" + m_scanCapped +
            " onFireCapped=" + m_onFireCapped +
            " onFireEntities=" + m_onFireEntities +
            " appliedMessages=" + m_appliedMessages +
            " rejectedMessages=" + m_rejectedMessages +
            " resets=" + m_resets +
            " failures=" + m_failures;

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        /// <summary>
        /// 只读 Terrain 源：<c>GetCellValueFast</c> 对未加载区块返回 0
        /// （Source: Survivalcraft/Game/Terrain.cs:304-307），不会加载区块。
        /// </summary>
        private sealed class TerrainCellSource : IAmbientCellSource
        {
            private readonly Game.Terrain m_terrain;

            public TerrainCellSource(Game.Terrain terrain)
            {
                m_terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
            }

            public Game.Terrain Terrain => m_terrain;

            public int GetCellValue(int x, int y, int z) =>
                m_terrain.GetCellValueFast(x, y, z);
        }
    }
}

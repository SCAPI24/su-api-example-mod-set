using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 水花表现的**引擎适配层**（<see cref="IWaterSplashPort"/> /
    /// <see cref="IWaterSplashHostSink"/> 的实现，组合根注入）。
    ///
    /// 主机：
    ///   · <see cref="Pump"/> 把本适配器挂到注入后的 <see cref="SuSubsystemPickables"/> 与
    ///     <see cref="SuSubsystemProjectiles"/> 的 <c>SplashSink</c> 上；两个外壳在各自的
    ///     <c>base.Update</c> 前后调用 <see cref="BeginSplashCapture"/> /
    ///     <see cref="EndSplashCapture"/>。
    ///   · 捕获点是 <c>SubsystemParticles.m_particleSystems</c> 的窗口前后差集：窗口内新增的
    ///     <c>WaterSplashParticleSystem</c> 就是引擎刚刚为落水拾取物 / 投射物创建的粒子系统。
    ///     位置取 <c>WaterSplashParticleSystem.m_position</c>（引擎构造参数）；<c>large</c> 在
    ///     当前两个调用点都是 false，但协议仍携带该位，避免将来大号水花在客户端悄悄变小。
    ///   · 声音：两个引擎分支都在粒子之后无条件调用
    ///     <c>PlayRandomSound("Audio/Splashes", 1f, random(-0.2, 0.2), position, 6f,
    ///     autoDelay: true)</c>（Source: SubsystemPickables.cs:328,
    ///     SubsystemProjectiles.cs:388）；因此 <c>sound = true</c> 是精确的既成事实。
    ///     本层不复制引擎的随机选择：客户端用同一目录 / 音量 / 音高区间 / 最小距离 / autoDelay
    ///     重新调用一次 <c>PlayRandomSound</c>（引擎本身也是每次随机选一个 clip）。
    ///
    /// 客户端：
    ///   · <see cref="Present"/> 只做两件事 —— 在主机给的 position 上
    ///     <c>AddParticleSystem(new WaterSplashParticleSystem(...))</c>，以及需要时播放
    ///     <c>Audio/Splashes</c>。没有 ChangeCell / DestroyCell / AddPickable / 伤害调用路径。
    ///
    /// 为什么不在外壳里复制“落水”判定：
    ///   · 引擎判定依赖 <c>SubsystemFluidBlockBehavior.CalculateFlowSpeed</c>、每帧位置与
    ///     拾取物 <c>SplashGenerated</c> 状态；复制一遍必然与引擎时序 / 浮点偏离。
    ///   · <c>SubsystemParticles.AddParticleSystem</c> 是非虚方法（Source:
    ///     Survivalcraft/Game/SubsystemParticles.cs:32），无法用派生外壳截获；读取同一实例上的
    ///     私有粒子表差集是精确且不碰引擎源码的做法。
    ///
    /// 无静态状态：反射入口 / 捕获缓冲 / 计数器都挂在实例上；每种失败都有计数 + 限流日志。
    /// </summary>
    public sealed class WaterSplashReplicaEngine : IWaterSplashPort, IWaterSplashHostSink
    {
        private const int ProblemLogMs = 10000;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly global::Engine.Random m_random = new global::Engine.Random();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Vector3> m_listeners = new List<Vector3>(4);
        private readonly HashSet<ParticleSystemBase> m_beforeParticles =
            new HashSet<ParticleSystemBase>();
        private readonly List<ParticleSystemBase> m_newParticles =
            new List<ParticleSystemBase>();

        private Project m_project;
        private SubsystemParticles m_particles;
        private SubsystemAudio m_audio;
        private SuSubsystemPickables m_pickables;
        private SuSubsystemProjectiles m_projectiles;
        private bool m_clientReplica;
        private bool m_hostCaptureEnabled;

        private FieldInfo m_particleTableField;
        private FieldInfo m_splashPositionField;
        private bool m_reflectionResolved;
        private string m_reflectionProblem;

        private IDictionary m_captureTable;
        private bool m_captureActive;

        // ---------------------------------------------------------------- 主机计数
        private long m_captureWindows;
        private long m_captureSplashes;
        private long m_captureEmpty;
        private long m_captureTableFailures;
        private long m_captureOverlap;
        private long m_captureProjectChanged;
        private long m_capturePositionFailures;
        private long m_emitFailures;
        private long m_emitWithoutListener;

        // ---------------------------------------------------------------- 客户端计数
        private long m_presentCalls;
        private long m_presentEvents;
        private long m_particlesCreated;
        private long m_soundsPlayed;
        private long m_skippedDistance;
        private long m_missingAudio;
        private long m_particleFaults;
        private long m_audioFaults;
        private long m_noListener;
        private long m_resets;

        public WaterSplashReplicaEngine(NetworkAvatarSpawner spawner,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner;
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "water-splash" : ownerId;
        }

        public event Action<WaterSplashData> HostSplash;

        /// <summary>
        /// 全局声音中继覆盖口（组合根注入）：主机引擎在拾取物 / 投射物落水时已经播过
        /// Audio/Splashes；客户端由本模块的定向事件播放，登记后全局捕获不再重复发送。
        /// </summary>
        public IHostSoundCoverPort SoundCover { get; set; }

        public bool IsClientReplica => m_clientReplica;

        // ================================================================= 挂接

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                ResetEngineState("project-change");
                m_project = project;
            }
            if (project == null) return;

            bool clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain
                shell && shell.ClientReplica;
            if (clientReplica != m_clientReplica)
            {
                ResetEngineState("role-change");
                m_project = project;
                m_clientReplica = clientReplica;
            }

            if (!ReferenceEquals(m_particles, project.FindSubsystem<SubsystemParticles>(false)))
            {
                m_particles = project.FindSubsystem<SubsystemParticles>(false);
                if (m_particles == null)
                {
                    LogOnce("particles-missing",
                        "没有找到 SubsystemParticles（引擎注入可能未生效）→ 水花表现同步不可用" +
                        "（主机无法捕获，客户端无法重建）");
                }
            }
            if (!ReferenceEquals(m_audio, project.FindSubsystem<SubsystemAudio>(false)))
                m_audio = project.FindSubsystem<SubsystemAudio>(false);

            SubsystemPickables pickables = project.FindSubsystem<SubsystemPickables>(false);
            SuSubsystemPickables pickableShell = pickables as SuSubsystemPickables;
            if (!ReferenceEquals(pickableShell, m_pickables))
            {
                DetachPickables();
                m_pickables = pickableShell;
                if (m_pickables != null) m_pickables.SplashSink = this;
            }
            else if (m_pickables != null && m_pickables.SplashSink == null)
            {
                m_pickables.SplashSink = this;
            }
            if (pickables != null && pickableShell == null)
            {
                LogOnce("pickable-shell-missing",
                    "没有注入 SuSubsystemPickables（当前：" + pickables.GetType().Name +
                    "）→ 拾取物落水的水花无法被主机捕获（请检查 Game.SubsystemPickables 注入）");
            }

            SubsystemProjectiles projectiles =
                project.FindSubsystem<SubsystemProjectiles>(false);
            SuSubsystemProjectiles projectileShell = projectiles as SuSubsystemProjectiles;
            if (!ReferenceEquals(projectileShell, m_projectiles))
            {
                DetachProjectiles();
                m_projectiles = projectileShell;
                if (m_projectiles != null) m_projectiles.SplashSink = this;
            }
            else if (m_projectiles != null && m_projectiles.SplashSink == null)
            {
                m_projectiles.SplashSink = this;
            }
            if (projectiles != null && projectileShell == null)
            {
                LogOnce("projectile-shell-missing",
                    "没有注入 SuSubsystemProjectiles（当前：" + projectiles.GetType().Name +
                    "）→ 投射物入水的水花无法被主机捕获（请检查 Game.SubsystemProjectiles 注入）");
            }
        }

        public void Shutdown()
        {
            ResetEngineState("shutdown");
            m_project = null;
        }

        private void ResetEngineState(string reason)
        {
            if (m_captureActive)
            {
                m_captureActive = false;
                m_captureProjectChanged++;
                LogProblem("capture-reset:" + reason,
                    "捕获窗口在 " + reason + " 时仍未关闭 → 丢弃本次水花（不会跨 Project 复用）");
            }
            m_captureTable = null;
            m_beforeParticles.Clear();
            m_newParticles.Clear();
            m_hostCaptureEnabled = false;
            DetachPickables();
            DetachProjectiles();
            m_particles = null;
            m_audio = null;
            m_clientReplica = false;
            m_resets++;
        }

        private void DetachPickables()
        {
            if (m_pickables == null) return;
            try { m_pickables.SplashSink = null; } catch (Exception) { }
            m_pickables = null;
        }

        private void DetachProjectiles()
        {
            if (m_projectiles == null) return;
            try { m_projectiles.SplashSink = null; } catch (Exception) { }
            m_projectiles = null;
        }

        // ================================================================= 主机：捕获窗口

        public bool SetHostCaptureEnabled(bool enabled, out string reason)
        {
            reason = null;
            if (!enabled)
            {
                m_hostCaptureEnabled = false;
                return true;
            }
            if (m_clientReplica)
            {
                reason = "client-replica";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            SubsystemParticles particles = project.FindSubsystem<SubsystemParticles>(false);
            if (particles == null)
            {
                reason = "no-particles-subsystem";
                return false;
            }
            if (!ResolveReflection(particles, out reason))
                return false;
            m_particles = particles;
            m_hostCaptureEnabled = true;
            return true;
        }

        public bool BeginSplashCapture(out string reason)
        {
            reason = null;
            if (!m_hostCaptureEnabled)
            {
                reason = "capture-disabled";
                return false;
            }
            if (m_captureActive)
            {
                m_captureOverlap++;
                reason = "capture-overlap";
                LogProblem("capture-overlap",
                    "水花捕获窗口重入（拾取物 / 投射物外壳不应嵌套）→ 本帧内层不捕获" +
                    "（累计 splashCaptureOverlap=" + m_captureOverlap + "）");
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            if (!ReferenceEquals(project, m_project))
            {
                // 组合根 / 模块每帧都会 Pump；这里仍明确拒绝跨 Project 捕获。
                reason = "project-not-pumped";
                return false;
            }
            SubsystemParticles particles = m_particles;
            if (particles == null)
            {
                reason = "no-particles-subsystem";
                return false;
            }
            if (!ResolveReflection(particles, out reason))
                return false;
            object tableObject;
            try
            {
                tableObject = m_particleTableField.GetValue(particles);
            }
            catch (Exception e)
            {
                reason = "particle-table-read-" + e.GetType().Name;
                m_captureTableFailures++;
                return false;
            }
            if (!(tableObject is IDictionary table))
            {
                reason = "particle-table-not-dictionary";
                m_captureTableFailures++;
                return false;
            }
            m_beforeParticles.Clear();
            try
            {
                foreach (object key in table.Keys)
                {
                    if (key is ParticleSystemBase system) m_beforeParticles.Add(system);
                }
            }
            catch (Exception e)
            {
                reason = "particle-table-enumerate-" + e.GetType().Name;
                m_captureTableFailures++;
                return false;
            }
            m_captureTable = table;
            m_captureActive = true;
            m_captureWindows++;
            return true;
        }

        public void EndSplashCapture()
        {
            if (!m_captureActive) return;
            m_captureActive = false;
            IDictionary table = m_captureTable;
            m_captureTable = null;
            Project project = GameManager.Project;
            if (table == null)
            {
                m_captureTableFailures++;
                return;
            }
            if (!ReferenceEquals(project, m_project))
            {
                m_captureProjectChanged++;
                m_beforeParticles.Clear();
                LogProblem("capture-project-changed",
                    "结束水花捕获时 Project 已变化 → 丢弃本窗口（不会把旧世界的水花发给新世界）");
                return;
            }
            m_newParticles.Clear();
            try
            {
                foreach (object key in table.Keys)
                {
                    if (key is ParticleSystemBase system && !m_beforeParticles.Contains(system))
                        m_newParticles.Add(system);
                }
            }
            catch (Exception e)
            {
                m_captureTableFailures++;
                LogProblem("capture-enumerate:" + e.GetType().Name,
                    "结束水花捕获时无法枚举粒子表（" + e.GetType().Name + " " + e.Message +
                    "）→ 本窗口水花不下发（累计 splashCaptureTableFailures=" +
                    m_captureTableFailures + "）");
                m_beforeParticles.Clear();
                return;
            }
            m_beforeParticles.Clear();
            if (m_newParticles.Count == 0)
            {
                m_captureEmpty++;
                return;
            }
            bool sound = m_audio != null;
            if (!sound)
            {
                m_missingAudio++;
                LogProblem("capture-no-audio",
                    "捕获窗口内本端没有 SubsystemAudio → 水花只发粒子、sound=false" +
                    "（客户端听不到；累计 splashMissingAudio=" + m_missingAudio + "）");
            }
            for (int i = 0; i < m_newParticles.Count; i++)
            {
                ParticleSystemBase system = m_newParticles[i];
                if (!(system is WaterSplashParticleSystem)) continue;
                Vector3 position;
                try
                {
                    object value = m_splashPositionField.GetValue(system);
                    if (!(value is Vector3 vector))
                    {
                        m_capturePositionFailures++;
                        LogProblem("capture-position-type",
                            "WaterSplashParticleSystem.m_position 不是 Vector3 → 本个水花丢弃" +
                            "（累计 splashCapturePositionFailures=" +
                            m_capturePositionFailures + "）");
                        continue;
                    }
                    position = vector;
                }
                catch (Exception e)
                {
                    m_capturePositionFailures++;
                    LogProblem("capture-position:" + e.GetType().Name,
                        "读不出 WaterSplashParticleSystem 的构造位置（" + e.GetType().Name +
                        " " + e.Message + "）→ 本个水花丢弃（累计 " +
                        m_capturePositionFailures + "）");
                    continue;
                }
                if (!IsFinite(position))
                {
                    m_capturePositionFailures++;
                    LogProblem("capture-position-nonfinite",
                        "WaterSplashParticleSystem 位置非有限 → 本个水花丢弃");
                    continue;
                }
                m_captureSplashes++;
                var splash = new WaterSplashData(position, false, sound);
                if (sound)
                {
                    SoundCover?.CoverPlayedSoundDirectory("Audio/Splashes", position, 1f, 6f, 1,
                        "water-splash");
                }
                Action<WaterSplashData> handler = HostSplash;
                if (handler == null)
                {
                    m_emitWithoutListener++;
                    LogProblem("capture-no-listener",
                        "捕获到水花 " + splash.Describe() +
                        "，但还没有监听者（模块未装配）→ 只计数不广播（累计 " +
                        m_emitWithoutListener + "）");
                    continue;
                }
                try
                {
                    handler(splash);
                }
                catch (Exception e)
                {
                    m_emitFailures++;
                    LogProblem("emit:" + e.GetType().Name,
                        "水花广播处理器异常（" + splash.Describe() + "）：" +
                        e.GetType().Name + " " + e.Message +
                        " → 本个水花丢弃，本帧其余捕获不受影响（累计 " + m_emitFailures + "）");
                }
            }
            m_newParticles.Clear();
        }

        private bool ResolveReflection(SubsystemParticles particles, out string reason)
        {
            reason = null;
            if (m_reflectionResolved) return true;
            Type particlesType = typeof(SubsystemParticles);
            m_particleTableField = particlesType.GetField("m_particleSystems",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (m_particleTableField == null ||
                !typeof(IDictionary).IsAssignableFrom(m_particleTableField.FieldType))
            {
                m_reflectionProblem = "m_particleSystems-not-dictionary";
                reason = m_reflectionProblem;
                return false;
            }
            m_splashPositionField = typeof(WaterSplashParticleSystem).GetField("m_position",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (m_splashPositionField == null ||
                m_splashPositionField.FieldType != typeof(Vector3))
            {
                m_reflectionProblem = "WaterSplashParticleSystem.m_position-not-vector3";
                reason = m_reflectionProblem;
                return false;
            }
            if (particles == null)
            {
                reason = "no-particles-subsystem";
                return false;
            }
            m_reflectionResolved = true;
            return true;
        }

        // ================================================================= 主机：玩家端位置

        public bool TryCollectPeers(List<RemoteSoundPeer> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "target-null";
                return false;
            }
            target.Clear();
            if (m_spawner == null)
            {
                reason = "no-spawner";
                return false;
            }
            int count = 0;
            foreach (int clientId in m_spawner.ClientIds)
            {
                bool hasPosition = m_spawner.TryGetAvatarCell(clientId, out Vector3 position,
                    out _);
                target.Add(new RemoteSoundPeer(clientId, hasPosition, position));
                count++;
            }
            if (count == 0) reason = "no-avatars";
            return count > 0;
        }

        // ================================================================= 客户端：只做表现

        public bool TryGetListenerPosition(out Vector3 position, out string reason)
        {
            position = Vector3.Zero;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            if (audio != null)
            {
                ReadOnlyList<Vector3> listeners = audio.ListenerPositions;
                if (listeners.Count > 0)
                {
                    position = listeners[0];
                    return true;
                }
            }
            if (m_spawner != null &&
                m_spawner.TryGetLocalPlayer(project, out PlayerData localPlayer, out _))
            {
                ComponentPlayer player = localPlayer?.ComponentPlayer;
                if (player?.ComponentBody != null)
                {
                    position = player.ComponentBody.Position;
                    return true;
                }
            }
            reason = "no-listener";
            return false;
        }

        public bool Present(in WaterSplashEventData data,
            out WaterSplashPresentationResult result, out string reason)
        {
            result = default;
            reason = null;
            m_presentCalls++;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            if (!ReferenceEquals(project, m_project)) Pump();
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            SubsystemParticles particles = m_particles ??
                project.FindSubsystem<SubsystemParticles>(false);
            if (particles == null)
            {
                reason = "no-particles-subsystem";
                return false;
            }
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null)
            {
                reason = "no-terrain-subsystem";
                return false;
            }
            SubsystemAudio audio = m_audio ?? project.FindSubsystem<SubsystemAudio>(false);
            if (!TryGetListenerPosition(out Vector3 listener, out reason))
            {
                m_noListener++;
                return false;
            }
            WaterSplashPresentationPlan plan = WaterSplashPresentationPolicy.Plan(
                data.Splashes, listener, WaterSplashPolicy.MaxRelayDistance);
            m_skippedDistance += plan.SkippedDistance;
            int created = 0;
            int sounds = 0;
            int missingAudio = 0;
            for (int i = 0; i < plan.Splashes.Length; i++)
            {
                WaterSplashData splash = plan.Splashes[i];
                try
                {
                    particles.AddParticleSystem(new WaterSplashParticleSystem(terrain,
                        splash.Position, splash.Large));
                    created++;
                    m_particlesCreated++;
                }
                catch (Exception e)
                {
                    m_particleFaults++;
                    LogProblem("particle:" + e.GetType().Name,
                        "客户端创建 WaterSplashParticleSystem 失败（" + splash.Describe() +
                        "）：" + e.GetType().Name + " " + e.Message +
                        " → 本个水花没有粒子（累计 splashParticleFaults=" +
                        m_particleFaults + "）");
                }
                if (!splash.Sound) continue;
                if (audio == null)
                {
                    missingAudio++;
                    m_missingAudio++;
                    continue;
                }
                try
                {
                    // 与引擎两个调用点逐参数一致：
                    // Source: SubsystemPickables.cs:328 / SubsystemProjectiles.cs:388
                    audio.PlayRandomSound(WaterSplashPolicy.SplashSoundDirectory,
                        WaterSplashPolicy.SplashSoundVolume,
                        m_random.Float(WaterSplashPolicy.SplashSoundPitchMin,
                            WaterSplashPolicy.SplashSoundPitchMax),
                        splash.Position, WaterSplashPolicy.SplashSoundMinDistance,
                        autoDelay: true);
                    sounds++;
                    m_soundsPlayed++;
                }
                catch (Exception e)
                {
                    m_audioFaults++;
                    LogProblem("audio:" + e.GetType().Name,
                        "客户端播放 Audio/Splashes 失败（" + splash.Describe() + "）：" +
                        e.GetType().Name + " " + e.Message +
                        " → 本个水花没有声音（累计 splashAudioFaults=" + m_audioFaults + "）");
                }
            }
            if (created > 0) m_presentEvents++;
            result = new WaterSplashPresentationResult(created, created, sounds,
                plan.SkippedDistance, missingAudio);
            return true;
        }

        // ================================================================= 诊断

        public string Describe() => "splashCaptureWindows=" + m_captureWindows +
            " splashCaptureSplashes=" + m_captureSplashes +
            " splashCaptureEmpty=" + m_captureEmpty +
            " splashCaptureTableFailures=" + m_captureTableFailures +
            " splashCaptureOverlap=" + m_captureOverlap +
            " splashCaptureProjectChanged=" + m_captureProjectChanged +
            " splashCapturePositionFailures=" + m_capturePositionFailures +
            " splashEmitFailures=" + m_emitFailures +
            " splashEmitWithoutListener=" + m_emitWithoutListener +
            " · splashPresentCalls=" + m_presentCalls +
            " splashPresentEvents=" + m_presentEvents +
            " splashParticlesCreated=" + m_particlesCreated +
            " splashSoundsPlayed=" + m_soundsPlayed +
            " splashSkippedDistance=" + m_skippedDistance +
            " splashMissingAudio=" + m_missingAudio +
            " splashParticleFaults=" + m_particleFaults +
            " splashAudioFaults=" + m_audioFaults +
            " splashNoListener=" + m_noListener +
            " splashResets=" + m_resets;

        private bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}

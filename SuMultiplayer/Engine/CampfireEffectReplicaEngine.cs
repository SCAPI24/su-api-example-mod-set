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
    /// 营火熄灭火声的**引擎适配层**（<see cref="ICampfireEffectPort"/> 的实现，组合根注入）。
    ///
    ///   · 主机：<see cref="CampfireReplicaPresentation.HostExtinguished"/> 在营火烧尽后给出
    ///     格子（contents 209 / data 0，引擎已经在本端播过 Sizzles）。这里分配单调 event_id /
    ///     server_tick 后抛给模块广播。
    ///   · 客户端：<see cref="Present"/> 只做一件事 —— 在格子上播 <c>Audio/Sizzles</c>
    ///     （与引擎 ResizeCampfire 的调用完全同参）。没有 ChangeCell / DestroyCell / 实体生成 /
    ///     伤害调用路径。
    ///
    /// 无静态状态：表现层 / 诊断都由组合根以实例注入。
    /// </summary>
    public sealed class CampfireEffectReplicaEngine : ICampfireEffectPort
    {
        private const int ProblemLogMs = 10000;

        private readonly CampfireReplicaPresentation m_presentation;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly global::Engine.Random m_random = new global::Engine.Random();
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private Project m_project;
        private bool m_clientReplica;
        private long m_nextEventId;
        private long m_lastEmittedTick;

        private long m_hostEvents;
        private long m_hostCells;
        private long m_hostDropped;
        private long m_hostRejected;
        private long m_emitWithoutListener;
        private long m_emitFailures;
        private long m_presented;
        private long m_missingAudio;
        private long m_audioFaults;
        private long m_resets;

        public CampfireEffectReplicaEngine(CampfireReplicaPresentation presentation,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "campfire-sync" : ownerId;
            m_presentation.HostExtinguished += OnHostExtinguished;
        }

        public event Action<CampfireExtinguishData> HostCampfireExtinguished;

        public bool IsClientReplica => m_clientReplica;

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                m_project = project;
                m_resets++;
            }
            if (project == null)
            {
                m_clientReplica = false;
                return;
            }
            m_clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                              shell.ClientReplica;
        }

        public void Shutdown()
        {
            m_presentation.HostExtinguished -= OnHostExtinguished;
            m_project = null;
            m_clientReplica = false;
        }

        // ------------------------------------------------------------ 主机：引擎已烧尽

        private void OnHostExtinguished(Point3[] cells)
        {
            if (cells == null || cells.Length == 0) return;
            m_hostEvents++;
            m_hostCells += cells.Length;
            if (m_clientReplica)
            {
                m_hostRejected++;
                LogProblem("host-on-client", "客户端副本上报了营火烧尽事件（" + cells.Length +
                    " 格）→ 丢弃（客户端绝不广播）");
                return;
            }
            Action<CampfireExtinguishData> handler = HostCampfireExtinguished;
            if (handler == null)
            {
                m_emitWithoutListener++;
                LogOnce("no-listener", "营火烧尽事件没有监听者（声音同步模块未装配）→ 本批只计数，" +
                    "不广播（累计 " + m_emitWithoutListener + " 批）");
                return;
            }
            long tick = CurrentTick();
            int limit = cells.Length < CampfireSoundPolicy.MaxEventsPerHostUpdate
                ? cells.Length
                : CampfireSoundPolicy.MaxEventsPerHostUpdate;
            if (cells.Length > limit)
            {
                m_hostDropped += cells.Length - limit;
                LogProblem("host-cap", "一次 Update 有 " + cells.Length +
                    " 格营火烧尽，超过单次上限 " + CampfireSoundPolicy.MaxEventsPerHostUpdate +
                    " → 本次少转发 " + (cells.Length - limit) + " 条（累计 " + m_hostDropped +
                    "；明确计数，不静默）");
            }
            for (int i = 0; i < limit; i++)
            {
                uint eventId = (uint)(++m_nextEventId);
                if (eventId == 0) eventId = (uint)(++m_nextEventId);
                var data = new CampfireExtinguishData(0, tick, eventId,
                    cells[i].X, cells[i].Y, cells[i].Z);
                try
                {
                    handler(data);
                }
                catch (Exception e)
                {
                    m_emitFailures++;
                    LogProblem("emit:" + e.GetType().Name, "营火烧尽事件广播处理器异常（" +
                        data.Describe() + "）：" + e.GetType().Name + " " + e.Message +
                        " → 本端引擎音效不受影响");
                }
            }
        }

        // ------------------------------------------------------------ 客户端：只做声音

        public bool Present(in CampfireExtinguishData data, out string reason)
        {
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            if (audio == null)
            {
                m_missingAudio++;
                reason = "no-audio";
                return false;
            }
            try
            {
                // 与引擎完全一致：Audio/Sizzles，音量 1，音高 ±0.25，最小距离 3，自动延迟。
                // Source: Survivalcraft/Game/SubsystemCampfireBlockBehavior.cs:ResizeCampfire
                audio.PlayRandomSound("Audio/Sizzles", 1f, m_random.Float(-0.25f, 0.25f),
                    new Vector3(data.X, data.Y, data.Z), 3f, autoDelay: true);
                m_presented++;
                reason = null;
                return true;
            }
            catch (Exception e)
            {
                m_audioFaults++;
                reason = "audio-fault:" + e.GetType().Name;
                LogProblem("audio:" + e.GetType().Name, "营火熄灭火声播放失败（" +
                    data.Describe() + "）：" + e.GetType().Name + " " + e.Message +
                    " → 本格没有声音（明确计数）");
                return false;
            }
        }

        public string Describe() =>
            "campfireSound hostDetected=" + m_hostCells +
            " hostEvents=" + m_hostEvents +
            " dropped=" + m_hostDropped +
            " rejected=" + m_hostRejected +
            " noListener=" + m_emitWithoutListener +
            " emitFailed=" + m_emitFailures +
            " presented=" + m_presented +
            " missingAudio=" + m_missingAudio +
            " audioFaults=" + m_audioFaults +
            " resets=" + m_resets;

        // ------------------------------------------------------------ 工具

        private long CurrentTick()
        {
            SubsystemGameInfo info = m_project?.FindSubsystem<SubsystemGameInfo>(false);
            long tick = info == null ? 0L : (long)(info.TotalElapsedGameTime * 1000.0);
            if (tick < m_lastEmittedTick) tick = m_lastEmittedTick;
            m_lastEmittedTick = tick;
            return tick;
        }

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}

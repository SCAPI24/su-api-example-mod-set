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
    /// 远程动作声音中继的**引擎适配层**（新文件；主机捕获 / 客户端播放）。
    ///
    /// 为什么不是"派生一个 SuSubsystemAudio 覆盖 PlaySound"：
    ///   · 引擎的 <c>SubsystemAudio.PlaySound/PlayRandomSound</c> 全部是**非虚方法**
    ///     （Source: Survivalcraft/Game/SubsystemAudio.cs:98/112/118/124/138/144，
    ///     签名里没有 <c>virtual</c>）；方块行为通过 <c>SubsystemAudio</c> 静态类型调用，
    ///     IL 是 callvirt 但方法非虚 ⇒ 派生类里用 <c>new</c> 隐藏的同名方法永远不会被调到。
    ///     因此"外壳转发 + 覆盖"在本引擎上无法截获调用。
    ///   · 可行且不碰引擎源码的截获点是**音频队列本身**：<c>PlaySound</c> 的所有重载最终都
    ///     把 <c>{Time, Name, Volume, Pitch, Pan}</c> 追加进
    ///     <c>SubsystemAudio.m_queuedSounds</c>（Source: SubsystemAudio.cs:23-34 结构 /
    ///     :48 字段 / :98-110 入队 / :176-206 消费）。方块交互是**同步执行**的：
    ///     在 <c>TryExecuteUse</c> 前后读同一个队列的 Count，窗口内新增的条目就恰好是
    ///     这次远端动作产生的声音；窗口结束不搬走条目，主机照常播放（不会静音 / 不会回声）。
    ///
    /// 位置：队列条目**不保存原始位置**（位置在入队前已被换成"相对主机监听者的衰减音量"）。
    /// 因此捕获用调用方给的**动作锚点**（被交互的方块格）作为位置，并按主机侧监听距离把
    /// 记录到的衰减音量还原成"在锚点上与主机听到的一样响"的有效发射参数：
    ///   在 d = 主机最近监听者到锚点的距离 上，客户端 CalculateVolume(d, minDistance) * volume
    ///   等于主机当时排队的 Volume。这样客户端按自己的距离得到合理衰减，而不是把主机距离
    ///   当成自己的距离。若主机没有监听者（无头服务端），衰减无法反解：明确计数并采用
    ///   引擎内容里最常见的 volume=1 / minDistance=2（不让客户端听不到）。
    ///
    /// 只做音频：粒子的 <c>SubsystemParticles</c> 没有可采的队列，任意复制会偏离主机画面，
    /// 本适配层不复制粒子（记录在案，不在本文件偷偷近似）。
    ///
    /// 无静态状态；反射结果只缓存在实例上；每一种失败都有实例计数 + 限流日志。
    /// </summary>
    public sealed class RemoteActionAudioReplica : IRemoteAudioPort
    {
        private const int ProblemLogMs = 10000;
        private const float DefaultMinDistance = 2f;
        private const float MaxDerivedMinDistance = 256f;
        private const int MaxCapturedPerWindow = 64;
        private const float AutoDelayEpsilon = 0.001f;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<Vector3> m_listeners = new List<Vector3>(4);

        private FieldInfo m_queueField;
        private FieldInfo m_soundNameField;
        private FieldInfo m_soundVolumeField;
        private FieldInfo m_soundPitchField;
        private FieldInfo m_soundTimeField;
        private FieldInfo m_soundPanField;
        private bool m_reflectionResolved;
        private string m_reflectionProblem;

        private SubsystemAudio m_audio;
        private IList m_queue;
        private int m_startCount;
        private Vector3 m_anchor;
        private double m_gameTime;
        private bool m_gameTimeKnown;
        private Project m_captureProject;
        private bool m_captureActive;

        // ---------------------------------------------------------------- 计数
        private long m_captureWindows;
        private long m_captureSounds;
        private long m_captureEmpty;
        private long m_captureFailures;
        private long m_captureQueueLost;
        private long m_captureOverflow;
        private long m_captureInvalid;
        private long m_captureNoListener;
        private long m_capturePositionConfirmed;
        private long m_capturePositionApprox;
        private long m_presentCalls;
        private long m_presented;
        private long m_presentFailures;
        private long m_presentNoAudio;
        private long m_peerCollects;
        private long m_peerNoPosition;

        public RemoteActionAudioReplica(NetworkAvatarSpawner spawner, IModuleDiagnostics diagnostics,
            string ownerId)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "remote-audio" : ownerId;
        }

        public bool IsClientReplica
        {
            get
            {
                Project project = GameManager.Project;
                return project != null &&
                    project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                    shell.ClientReplica;
            }
        }

        // ================================================================= 主机：捕获

        public bool TryBeginCapture(int requesterClientId, Vector3 anchor, out string reason)
        {
            reason = null;
            if (m_captureActive)
            {
                reason = "capture-already-open";
                return false;
            }
            if (requesterClientId <= 0)
            {
                reason = "requester-client-" + requesterClientId;
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
                reason = "no-subsystem-audio";
                return false;
            }
            if (!ResolveReflection(audio))
            {
                reason = m_reflectionProblem ?? "audio-queue-reflection-failed";
                return false;
            }
            object queueObject;
            try
            {
                queueObject = m_queueField.GetValue(audio);
            }
            catch (Exception e)
            {
                reason = "queue-read-" + e.GetType().Name;
                return false;
            }
            if (!(queueObject is IList queue))
            {
                reason = "queue-not-list";
                return false;
            }
            m_audio = audio;
            m_queue = queue;
            m_startCount = queue.Count;
            m_anchor = anchor;
            m_captureProject = project;
            m_listeners.Clear();
            ReadOnlyList<Vector3> listeners = audio.ListenerPositions;
            for (int i = 0; i < listeners.Count; i++) m_listeners.Add(listeners[i]);
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            m_gameTime = time?.GameTime ?? 0.0;
            m_gameTimeKnown = time != null;
            m_captureActive = true;
            m_captureWindows++;
            return true;
        }

        public bool EndCapture(out RemoteSoundClipData[] sounds, out string reason)
        {
            sounds = Array.Empty<RemoteSoundClipData>();
            reason = null;
            if (!m_captureActive)
            {
                reason = "capture-not-open";
                return false;
            }
            m_captureActive = false;
            SubsystemAudio audio = m_audio;
            IList queue = m_queue;
            int startCount = m_startCount;
            Vector3 anchor = m_anchor;
            double gameTime = m_gameTime;
            bool gameTimeKnown = m_gameTimeKnown;
            bool hadListeners = m_listeners.Count > 0;
            Project project = m_captureProject;
            m_audio = null;
            m_queue = null;
            m_captureProject = null;
            m_startCount = 0;
            if (audio == null || queue == null)
            {
                reason = "capture-state-missing";
                m_captureFailures++;
                return false;
            }
            if (!ReferenceEquals(GameManager.Project, project))
            {
                reason = "project-changed-during-capture";
                m_captureQueueLost++;
                return false;
            }
            object currentQueue;
            try
            {
                currentQueue = m_queueField.GetValue(audio);
            }
            catch (Exception e)
            {
                reason = "queue-read-" + e.GetType().Name;
                m_captureFailures++;
                return false;
            }
            if (!ReferenceEquals(currentQueue, queue))
            {
                // 引擎把队列整个换了（正常流程不会发生）：窗口内数据无法归属，明确失败。
                reason = "queue-replaced";
                m_captureQueueLost++;
                return false;
            }
            int endCount;
            try
            {
                endCount = queue.Count;
            }
            catch (Exception e)
            {
                reason = "queue-count-" + e.GetType().Name;
                m_captureFailures++;
                return false;
            }
            if (endCount < startCount)
            {
                reason = "queue-shrank-" + endCount + "<" + startCount;
                m_captureQueueLost++;
                return false;
            }
            int added = endCount - startCount;
            if (added == 0)
            {
                m_captureEmpty++;
                return true;
            }
            int overflowInWindow = 0;
            if (added > MaxCapturedPerWindow)
            {
                overflowInWindow = added - MaxCapturedPerWindow;
                m_captureOverflow += overflowInWindow;
                added = MaxCapturedPerWindow;
            }
            int invalidInWindow = 0;
            int noListenerInWindow = 0;
            var captured = new List<RemoteSoundClipData>(added);
            for (int i = 0; i < added; i++)
            {
                object entry;
                try
                {
                    entry = queue[startCount + i];
                }
                catch (Exception)
                {
                    m_captureInvalid++;
                    invalidInWindow++;
                    continue;
                }
                if (entry == null)
                {
                    m_captureInvalid++;
                    invalidInWindow++;
                    continue;
                }
                string name;
                float volume;
                float pitch;
                double time;
                float pan;
                try
                {
                    name = (string)m_soundNameField.GetValue(entry);
                    volume = (float)m_soundVolumeField.GetValue(entry);
                    pitch = (float)m_soundPitchField.GetValue(entry);
                    time = (double)m_soundTimeField.GetValue(entry);
                    pan = (float)m_soundPanField.GetValue(entry);
                }
                catch (Exception)
                {
                    m_captureInvalid++;
                    invalidInWindow++;
                    continue;
                }
                if (!RemoteSoundPolicy.IsValidSoundName(name) ||
                    float.IsNaN(volume) || float.IsInfinity(volume) || volume < 0f)
                {
                    m_captureInvalid++;
                    invalidInWindow++;
                    continue;
                }
                double delay = gameTimeKnown ? time - gameTime : 0.0;
                if (delay < 0.0) delay = 0.0;
                bool autoDelay = delay > AutoDelayEpsilon;
                float d = nearestListenerDistance(anchor);
                if (!hadListeners)
                {
                    m_captureNoListener++;
                    noListenerInWindow++;
                    captured.Add(new RemoteSoundClipData(name, anchor, 1f, pitch,
                        DefaultMinDistance, autoDelay));
                    continue;
                }
                float outVolume;
                float outMinDistance;
                EffectiveEmission(audio, d, volume, out outVolume, out outMinDistance);
                if (autoDelay && gameTimeKnown && delay < 2.99)
                {
                    float implied = (float)(delay * 120.0);
                    if (Math.Abs(implied - d) <= 0.5f + 0.02f * d) m_capturePositionConfirmed++;
                    else m_capturePositionApprox++;
                }
                else
                {
                    m_capturePositionApprox++;
                }
                // 非位置调用（pan != 0）在队列里没有位置；这里同样按锚点呈现，明确计数。
                if (Math.Abs(pan) > 0.0001f) m_capturePositionApprox++;
                captured.Add(new RemoteSoundClipData(name, anchor, outVolume, pitch,
                    outMinDistance, autoDelay));
            }
            if (overflowInWindow > 0)
                LogProblem("capture-overflow", "一次远程动作的音频队列新增条目超过单窗口上限 " +
                    MaxCapturedPerWindow + " → 丢弃 " + overflowInWindow +
                    " 条（本端仍会正常播放；客户端收不到这部分）");
            if (invalidInWindow > 0)
                LogProblem("capture-invalid", "远程动作窗口内有 " + invalidInWindow +
                    " 条音频条目读不出合法 {Name,Volume,Pitch} → 明确丢弃（不猜默认值）");
            if (noListenerInWindow > 0)
                LogProblem("capture-no-listener", "本端没有音频监听者（无头主机 / 首帧），" +
                    noListenerInWindow + " 条声音无法反解音量 → 按 volume=1 / minDistance=" +
                    DefaultMinDistance.ToString("F1") + " 中继（客户端按自己的距离衰减）");
            m_captureSounds += captured.Count;
            sounds = captured.ToArray();
            return true;
        }

        /// <summary>
        /// 把"主机监听者处听到的音量"还原成锚点上的有效发射参数：
        /// 取 volume=1，求 minDistance 使 CalculateVolume(d, minDistance) == 捕获音量；
        /// 捕获音量 ≥1（在最小距离内）时取 minDistance = max(d, 2)。
        /// </summary>
        private void EffectiveEmission(SubsystemAudio audio, float d, float capturedVolume,
            out float volume, out float minDistance)
        {
            if (d < 0.001f)
            {
                volume = MathUtils.Clamp(capturedVolume, 0f, RemoteSoundPolicy.MaxVolume);
                minDistance = DefaultMinDistance;
                return;
            }
            if (capturedVolume >= 0.999f)
            {
                volume = 1f;
                minDistance = MathUtils.Max(d, DefaultMinDistance);
                if (minDistance > RemoteSoundPolicy.MaxMinDistance)
                    minDistance = RemoteSoundPolicy.MaxMinDistance;
                return;
            }
            // v = m / (m + 2(d-m)) = m / (2d - m)  =>  m = 2vd / (1+v)
            float derived = 2f * capturedVolume * d / (1f + capturedVolume);
            if (derived >= RemoteSoundPolicy.MinMinDistance && derived <= MaxDerivedMinDistance)
            {
                volume = 1f;
                minDistance = derived;
                return;
            }
            float attenuation = audio.CalculateVolume(d, DefaultMinDistance);
            volume = attenuation > 0.0001f ? capturedVolume / attenuation : 1f;
            volume = MathUtils.Clamp(volume, 0f, RemoteSoundPolicy.MaxVolume);
            minDistance = DefaultMinDistance;
        }

        private float nearestListenerDistance(Vector3 position)
        {
            float best = float.MaxValue;
            for (int i = 0; i < m_listeners.Count; i++)
            {
                float d = Vector3.DistanceSquared(m_listeners[i], position);
                if (d < best) best = d;
            }
            return MathUtils.Sqrt(best);
        }

        private bool ResolveReflection(SubsystemAudio audio)
        {
            if (m_reflectionResolved) return true;
            Type type = typeof(SubsystemAudio);
            m_queueField = type.GetField("m_queuedSounds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (m_queueField == null || !typeof(IList).IsAssignableFrom(m_queueField.FieldType))
            {
                m_reflectionProblem = "m_queuedSounds-not-found";
                m_queueField = null;
                return false;
            }
            object queue;
            try
            {
                queue = m_queueField.GetValue(audio);
            }
            catch (Exception e)
            {
                m_reflectionProblem = "m_queuedSounds-read-" + e.GetType().Name;
                return false;
            }
            if (queue == null)
            {
                m_reflectionProblem = "m_queuedSounds-null";
                return false;
            }
            Type itemType = queue.GetType().GetGenericArguments()[0];
            m_soundNameField = itemType.GetField("Name", BindingFlags.Instance | BindingFlags.Public);
            m_soundVolumeField = itemType.GetField("Volume",
                BindingFlags.Instance | BindingFlags.Public);
            m_soundPitchField = itemType.GetField("Pitch", BindingFlags.Instance | BindingFlags.Public);
            m_soundTimeField = itemType.GetField("Time", BindingFlags.Instance | BindingFlags.Public);
            m_soundPanField = itemType.GetField("Pan", BindingFlags.Instance | BindingFlags.Public);
            if (m_soundNameField == null || m_soundVolumeField == null ||
                m_soundPitchField == null || m_soundTimeField == null || m_soundPanField == null)
            {
                m_reflectionProblem = "SoundInfo-fields-missing";
                return false;
            }
            m_reflectionProblem = null;
            m_reflectionResolved = true;
            return true;
        }

        public void Pump()
        {
            if (!m_captureActive) return;
            Project project = GameManager.Project;
            if (ReferenceEquals(project, m_captureProject)) return;
            // 窗口跨 Project（理论上不会发生；捕获窗口是同步的）：丢弃并计数，绝不留在新世界上。
            m_captureActive = false;
            m_captureQueueLost++;
            m_audio = null;
            m_queue = null;
            m_captureProject = null;
            LogProblem("capture-pump-project-changed",
                "捕获窗口跨 Project 变化 → 丢弃本次远程动作声音（不会把旧世界的音量套到新世界）");
        }

        // ================================================================= 主机：玩家端

        public bool TryCollectPeers(List<RemoteSoundPeer> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "target-null";
                return false;
            }
            target.Clear();
            m_peerCollects++;
            int count = 0;
            foreach (int clientId in m_spawner.ClientIds)
            {
                bool hasPosition = m_spawner.TryGetAvatarCell(clientId, out Vector3 position,
                    out _);
                if (!hasPosition) m_peerNoPosition++;
                target.Add(new RemoteSoundPeer(clientId, hasPosition, position));
                count++;
            }
            if (count == 0) reason = "no-avatars";
            return count > 0;
        }

        // ================================================================= 客户端：监听者 / 播放

        public bool TryGetLocalListenerPosition(out Vector3 position, out string reason)
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
            if (m_spawner.TryGetLocalPlayer(project, out PlayerData localPlayer, out string playerReason))
            {
                ComponentPlayer player = localPlayer.ComponentPlayer;
                if (player?.ComponentBody != null)
                {
                    position = player.ComponentBody.Position;
                    return true;
                }
                reason = "local-player-body-missing";
                return false;
            }
            reason = playerReason;
            return false;
        }

        public int Present(RemoteSoundClipData[] sounds, out string reason)
        {
            reason = null;
            m_presentCalls++;
            if (sounds == null || sounds.Length == 0)
            {
                reason = "no-sounds";
                return 0;
            }
            if (!IsClientReplica)
            {
                m_presentFailures++;
                reason = "not-client-replica";
                return 0;
            }
            Project project = GameManager.Project;
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            if (audio == null)
            {
                m_presentNoAudio++;
                reason = "no-subsystem-audio";
                return 0;
            }
            int presented = 0;
            int failures = 0;
            string firstFailure = null;
            for (int i = 0; i < sounds.Length; i++)
            {
                RemoteSoundClipData clip = sounds[i];
                if (!RemoteSoundPolicy.IsValidSoundName(clip.Sound) ||
                    float.IsNaN(clip.Volume) || float.IsInfinity(clip.Volume) ||
                    float.IsNaN(clip.Pitch) || float.IsInfinity(clip.Pitch) ||
                    float.IsNaN(clip.MinDistance) || float.IsInfinity(clip.MinDistance))
                {
                    failures++;
                    if (firstFailure == null) firstFailure = "clip-" + i + "-invalid";
                    continue;
                }
                try
                {
                    audio.PlaySound(clip.Sound, clip.Volume, clip.Pitch, clip.Position,
                        clip.MinDistance, clip.AutoDelay);
                    presented++;
                }
                catch (Exception e)
                {
                    failures++;
                    if (firstFailure == null)
                        firstFailure = "clip-" + i + "-" + e.GetType().Name;
                }
            }
            m_presented += presented;
            if (failures > 0)
            {
                m_presentFailures += failures;
                reason = firstFailure + " failed=" + failures;
            }
            return presented;
        }

        // ================================================================= 诊断

        public string Describe() => "engine capture windows=" + m_captureWindows +
            " sounds=" + m_captureSounds + " empty=" + m_captureEmpty +
            " failed=" + m_captureFailures + " queueLost=" + m_captureQueueLost +
            " overflow=" + m_captureOverflow + " invalid=" + m_captureInvalid +
            " noListener=" + m_captureNoListener +
            " positionConfirmed=" + m_capturePositionConfirmed +
            " positionApprox=" + m_capturePositionApprox +
            " · client presentCalls=" + m_presentCalls + " presented=" + m_presented +
            " failed=" + m_presentFailures + " noAudio=" + m_presentNoAudio +
            " · peers noPosition=" + m_peerNoPosition + " collects=" + m_peerCollects;

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}

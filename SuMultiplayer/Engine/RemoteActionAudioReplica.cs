using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 主机声音中继的**引擎适配层**：既保留原来的"远端动作捕获窗口"，又提供
    /// **每帧连续捕获**（全局主机声音中继）。
    ///
    /// 捕获点：
    ///   · <c>SubsystemAudio.PlaySound / PlayRandomSound</c> 全部非虚（Source:
    ///     Survivalcraft/Game/SubsystemAudio.cs:98-166），派生外壳无法截获；所有重载最终把
    ///     <c>{Time,Name,Volume,Pitch,Pan}</c> 追加进 <c>m_queuedSounds</c>（:23-34 / :48 / :98-110）；
    ///   · 队列由 <c>SubsystemAudio.Update</c> 排空（:176-206）。组合根把数据库里的
    ///     <c>Game.SubsystemAudio</c> 替换为 <see cref="SuSubsystemAudio"/>，它在
    ///     <c>base.Update</c> **之前**回调 <see cref="CaptureBeforeDrain"/>：
    ///       - 立即播放（delay 0）与延迟播放的声音都能看到；
    ///       - 主机照常播放（队列条目只读，不删不改）；
    ///       - 无头 / 单机 / 客户端也能安全运行（没有监听者时明确计数跳过）。
    ///
    /// 队列条目**没有位置**（位置在入队前已折算成距离衰减），所以全局中继使用
    /// <see cref="RemoteSoundAttenuation.RelativeToHost"/>：
    ///   · position = 主机监听者位置（capture 时 `ListenerPositions` 仍是入队时的同一份值）；
    ///   · volume = 引擎实际排队的（已衰减）音量；
    ///   · source_distance = 反解距离：
    ///       1) <c>Time - GameTime</c> 是 autoDelay 的延迟 ⇒ 距离 ≈ delay * 120（引擎
    ///          CalculateDelay 的逆，上限 3 s / 360 m）；
    ///       2) 否则用 CalculateVolume 的逆函数（minDistance=2）从衰减音量反解；
    ///       3) 取两者较大值（delay 是几何距离的下界）；方向无法反解，客户端按
    ///          sqrt(自己到主机监听者距离² + source_distance²) 做各向同性重投影。
    /// 每一条"位置是推断的"都计入 positionFromDelay / positionFromVolume / nonPositional，
    /// 不是静默近似。
    ///
    /// 去重 / 防双播：
    ///   · 远端动作窗口 EndCapture 把刚捕获的条目登记进 covered 账本，全局捕获**下一次**
    ///     读队列时按 (名字, 衰减音量) 精确消费，绝不会把同一次交互的声音再发一遍；
    ///   · 营火烧尽 / 烧尽 / 拾取 / 水花 / 爆炸 / 武器开火等已有定向事件的适配器通过
    ///     <see cref="CoverPlayedSound"/> / <see cref="CoverPlayedSoundDirectory"/> 登记；
    ///   · 客户端侧：本机排进队列的声音记入 ClientLocalSoundLedger，收到同名的主机中继
    ///     第一条时消费并跳过（LocalEchoSkipped）；中继自己 Present 的声音不记入本机账本。
    ///
    /// 已知 bypass（明确不覆盖，不是静默兜底）：
    ///   · <c>SubsystemAmbientSounds</c> 的 <c>Audio/Fire</c> / <c>Audio/Water</c> /
    ///     <c>Audio/Magma</c> 是 <c>CreateSound</c> 的**循环 Sound**，从不进入
    ///     <c>m_queuedSounds</c>；连续低鸣不随本中继复制（随机 Sizzles 仍会被捕获）；
    ///   · UI 点击走 <c>AudioManager.PlaySound</c> 直接播放，不经过队列，天然不会中继；
    ///   · 主机排空时的**拥塞丢弃**（<c>UpdateCongestion</c>）无法在排空前观测；
    ///     中继发送的是"排入队列且通过音量 / 时间闸门"的声音，客户端另有每端限速。
    ///
    /// 多本机角色归属（分裂屏 ≤4，Source: Survivalcraft/Game/SubsystemPlayers.cs:20）：
    ///   · 主机**每个本机角色实体**（PlayersData 里不是 NetworkAvatarSpawner 化身的那几条）
    ///     的**每个 IUpdateable 组件** Update 都被 Engine/LocalPlayerAudioWindows.cs 包了
    ///     一层捕获窗口；窗口内新增的队列条目 = 该角色产生的声音；
    ///   · 归属后的声音用 <see cref="RemoteSoundAttenuation.SlotSource"/> 发出：
    ///     position = 该角色 ComponentBody.Position，volume = 主机最近监听者处的衰减音量，
    ///     source_distance = 该位置到主机最近监听者的距离；
    ///   · 抑制账本（<see cref="HostSlotSuppressionLedger"/>）保证同一队列条目不会被全局
    ///     路径再发一次；与远端动作窗口重叠时放弃归属（精确锚点优先），明确计数；
    ///   · 每端按"该端到角色真实位置"做 48 m 裁剪；客户端再按"自己最近的本地角色"
    ///     用 <see cref="RemoteSoundPolicy.RelativeVolume"/> 重投影音量；
    ///   · 不能归属的声音（子系统声 / 无窗口 / 窗口重叠 / 无位置）仍走原来的全局路径
    ///     （锚在主机监听者 + 距离反解），行为不变。
    ///
    /// 性能：捕获只用预分配的 shadow 数组做**值比较**；新增条目通过 Reflection.Emit 生成的
    /// 强类型读取器直接读 List&lt;SoundInfo&gt; 内部数组（不装箱、无每帧分配）；只在队列
    /// 增长 / 重同步时扩容。无静态状态。
    /// </summary>
    public sealed class RemoteActionAudioReplica : IRemoteAudioPort, IHostAudioCaptureSink,
        ILocalPlayerAudioWindowSink
    {
        private const int ProblemLogMs = 10000;
        private const int SummaryExpireMs = 1000;
        private const float DefaultMinDistance = 2f;
        private const float MaxDerivedMinDistance = 256f;
        private const int MaxCapturedPerWindow = 64;
        private const float AutoDelayEpsilon = 0.001f;
        private const float DefaultMaxRelayDistance = RemoteSoundPolicy.MaxRelayDistance;
        private const int LocalEchoWindowMs = 400;
        private const int PresentedNameTtlMs = 2000;
        private const int CoveredTtlMs = 1500;
        private const int CaptureRingCapacity = 512;
        private const int SlotSuppressionCapacity = 512;
        /// <summary>
        /// 与引擎的 <c>AudioManager.MinAudibleVolume</c> 同口径的**原始**音量阈值
        /// （引擎里 MinAudibleVolume = 0.05 * SoundsVolume，比较时约掉 SoundsVolume）。
        /// 中继**不**乘以主机的 SettingsManager.SoundsVolume：无头服务端常把主音量设为 0，
        /// 客户端应当按自己的音量设置听到世界声音。
        /// </summary>
        private const float MinRelayVolume = HostSlotSoundCapturePolicy.MinRelayVolume;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<Vector3> m_listeners = new List<Vector3>(4);
        private readonly List<RemoteSoundPeer> m_peerScratch = new List<RemoteSoundPeer>(8);
        private readonly List<Vector3> m_virtualListeners = new List<Vector3>(4);

        // ---------------------------------------------------------------- 反射
        private FieldInfo m_queueField;
        private FieldInfo m_soundNameField;
        private FieldInfo m_soundVolumeField;
        private FieldInfo m_soundPitchField;
        private FieldInfo m_soundTimeField;
        private FieldInfo m_soundPanField;
        private FieldInfo m_listItemsField;
        private FieldInfo m_listenerListField;
        private bool m_reflectionResolved;
        private string m_reflectionProblem;
        private string m_fastReaderProblem;
        private SoundEntryReader m_fastReader;

        private delegate void SoundEntryReader(object list, int index, out double time,
            out string name, out float volume, out float pitch, out float pan);

        // 引擎队列条目的只读镜像（纯逻辑层的同一类型，见 Net/HostAudioRelayNet.cs）。
        // 槽位归属 / 抑制账本 / 全局差集都用同一份 5 字段定义，避免两份结构漂移。
        // （旧实现是本文件里的私有 struct QueueEntry；字段与语义完全一致。）

        // ---------------------------------------------------------------- 引擎对象
        private Project m_project;
        private SubsystemAudio m_audio;
        private SubsystemTime m_time;
        private SuSubsystemAudio m_hook;
        private bool m_clientReplica;
        private bool m_hostCaptureEnabled;
        private bool m_hookMissingLogged;

        // ---------------------------------------------------------------- 全局捕获
        private IList m_queue;
        private RemoteSoundQueueEntry[] m_shadow = new RemoteSoundQueueEntry[64];
        private int m_shadowCount;
        private double m_shadowGameTime;
        private bool m_shadowValid;
        private long m_lastExpireMs;
        private readonly HostSoundCaptureRing m_capture =
            new HostSoundCaptureRing(CaptureRingCapacity);
        private readonly HostSoundCoveredLedger m_covered = new HostSoundCoveredLedger();
        private readonly HostSoundCoveredLedger m_presentedNames =
            new HostSoundCoveredLedger();
        private readonly ClientLocalSoundLedger m_localEcho = new ClientLocalSoundLedger();

        // ---------------------------------------------------------------- 本机多角色槽位归属
        //
        // 主机的每个本机角色实体（PlayersData 里不是 NetworkAvatarSpawner 化身的那几条）
        // 的每个 IUpdateable 组件 Update 都被 Engine/LocalPlayerAudioWindows.cs 包了一层
        // 捕获窗口；窗口内新增的队列条目 = 该角色产生的声音，位置取该角色 ComponentBody.Position。
        private LocalPlayerAudioWindows m_localWindows;
        private readonly HostSlotSuppressionLedger m_slotSuppress =
            new HostSlotSuppressionLedger(SlotSuppressionCapacity);
        private readonly List<LocalSlotWindow> m_slotWindows = new List<LocalSlotWindow>(8);
        private readonly List<Vector3> m_localListeners = new List<Vector3>(4);
        private long m_slotPlayers;
        private long m_slotWindowsOpened;
        private long m_slotWindowsWithSounds;
        private long m_slotSounds;
        private long m_slotRejectedInvalid;
        private long m_slotRejectedInaudible;
        private long m_slotRejectedInactive;
        private long m_slotRejectedUi;
        private long m_slotRejectedNoPosition;
        private long m_slotRejectedNoListener;
        private long m_slotWindowFaults;
        private long m_slotWindowOverlaps;
        private long m_slotGlobalSuppressed;
        private long m_slotSuppressCleared;

        private struct LocalSlotWindow
        {
            public int Slot;
            public int PlayerIndex;
            public ComponentPlayer Player;
            public IList Queue;
            public int StartIndex;
            public double GameTime;
            /// <summary>窗口打开时已有别的捕获窗口（远端动作 / 嵌套）：放弃归属，避免双发。</summary>
            public bool Overlap;
            /// <summary>远端动作窗口代数：窗口期间新开了远端动作窗口 ⇒ 重叠，放弃归属。</summary>
            public long Epoch;
        }

        // ---------------------------------------------------------------- 远端动作窗口
        private int m_startCount;
        private Vector3 m_anchor;
        private double m_gameTime;
        private bool m_gameTimeKnown;
        private Project m_captureProject;
        private bool m_captureActive;
        /// <summary>每打开一次远端动作窗口 +1；角色窗口据此发现"窗口期间被远端动作插入"。</summary>
        private long m_captureWindowEpoch;

        // ---------------------------------------------------------------- 计数：捕获窗口
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

        // ---------------------------------------------------------------- 计数：全局捕获
        private long m_globalFrames;
        private long m_globalSounds;
        private long m_globalSeedEntries;
        private long m_globalResyncs;
        private long m_globalFaults;
        private long m_globalInaudible;
        private long m_globalInactive;
        private long m_globalNoListener;
        private long m_globalUiSkipped;
        private long m_globalClamped;
        private long m_globalPositionFromDelay;
        private long m_globalPositionFromVolume;
        private long m_globalPositionNonPositional;
        private long m_globalMultiListener;
        private long m_globalSlowReads;
        private long m_globalDisabledDropped;
        private long m_virtualListenerFrames;
        private long m_virtualListenerClients;
        private long m_virtualListenerEmpty;
        private long m_virtualListenerFailures;
        private long m_globalCoveredSuppressed;
        private long m_globalPresentedConsumed;
        private string m_lastFault;

        // ---------------------------------------------------------------- 计数：收件人 / 播放
        private long m_presentCalls;
        private long m_presented;
        private long m_presentFailures;
        private long m_presentNoAudio;
        private long m_peerCollects;
        private long m_peerNoPosition;
        private long m_coverRequests;
        private long m_coverFailures;
        private long m_localEchoSkipped;

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

        // ================================================================= 生命周期

        /// <summary>每帧调用（模块 Tick）：换 Project 时摘/挂捕获外壳并清空所有跨世界状态。</summary>
        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                DetachHook();
                ResetCaptureState();
                m_project = project;
                m_audio = null;
                m_time = null;
                m_hookMissingLogged = false;
            }
            if (project == null)
            {
                m_clientReplica = false;
                PumpLocalWindows(allowRestore: true);
                return;
            }
            m_clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain
                shellTerrain && shellTerrain.ClientReplica;
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            if (!ReferenceEquals(audio, m_audio))
            {
                m_audio = audio;
                m_time = project.FindSubsystem<SubsystemTime>(false);
                m_queue = null;
                m_shadowValid = false;
            }
            if (audio is SuSubsystemAudio hook)
            {
                if (!ReferenceEquals(hook, m_hook))
                {
                    DetachHook();
                    m_hook = hook;
                    hook.AttachCaptureSink(this);
                }
            }
            else if (!m_hookMissingLogged)
            {
                m_hookMissingLogged = true;
                LogProblem("hook-missing",
                    "没有找到注入后的 SuSubsystemAudio（数据库 Class 替换未生效）→ 全局主机声音中继" +
                    "停用；远端动作窗口仍可用（同一反射路径），但客户端听不到主机世界声音");
            }
            PumpLocalWindows(allowRestore: true);
        }

        private void DetachHook()
        {
            if (m_hook == null) return;
            m_hook.DetachCaptureSink(this);
            m_hook = null;
        }

        private void ResetCaptureState()
        {
            m_captureActive = false;
            m_captureProject = null;
            m_queue = null;
            m_shadowCount = 0;
            m_shadowValid = false;
            m_shadowGameTime = 0.0;
            DiscardCaptureRing("project-change");
            m_covered.Reset();
            m_presentedNames.Reset();
            m_localEcho.Reset();
            m_listeners.Clear();
            m_peerScratch.Clear();
            m_slotWindows.Clear();
            m_slotSuppress.Clear();
            m_localListeners.Clear();
        }

        /// <summary>
        /// 模块决定是否开启全局捕获（主机 + 已连接 + 至少一个读得到位置的玩家端）。
        /// 关闭时清空待发 / shadow，单机完全不做队列差集（只保留客户端本机回声账本）。
        /// </summary>
        public void SetHostCaptureEnabled(bool enabled)
        {
            if (m_hostCaptureEnabled == enabled) return;
            m_hostCaptureEnabled = enabled;
            m_shadowValid = false;
            m_shadowCount = 0;
            if (!enabled)
            {
                DiscardCaptureRing("capture-disabled");
                m_covered.Reset();
                m_slotSuppress.Clear();
            }
            // 同一帧安装 / 还原本机角色更新窗口：下一帧派发生效（不会与原组件重复更新）。
            // 这里传 false：本 Tick 可能刚装好代理、还没经过一次完整派发，禁止立即还原
            // （否则会把"移除 + 添加"压成对已存在组件再 Add，SubsystemUpdate 会抛异常）。
            PumpLocalWindows(allowRestore: false);
        }

        /// <summary>清空捕获环并显式计数（换世界 / 停止捕获时不能静默丢已捕获的声音）。</summary>
        private void DiscardCaptureRing(string reason)
        {
            long lost = m_capture.Count;
            m_capture.Clear();
            if (lost <= 0) return;
            m_globalDisabledDropped += lost;
            LogProblem("capture-discard:" + reason,
                "全局音频捕获在 " + reason + " 时仍有 " + lost +
                " 条未排空 → 明确丢弃（不会跨世界 / 跨连接补发；累计 " +
                m_globalDisabledDropped + "）");
        }

        /// <summary>模块每帧排空全局捕获（引擎侧只负责采集，发送由模块 / 纯逻辑调度器做）。</summary>
        public bool TryDrainHostSounds(List<RemoteSoundClipData> target, out int droppedCap,
            out string reason)
        {
            droppedCap = 0;
            reason = null;
            if (target == null)
            {
                reason = "target-null";
                return false;
            }
            if (!m_hostCaptureEnabled)
            {
                reason = "capture-disabled";
                return false;
            }
            long before = m_capture.DroppedCap;
            int start = target.Count;
            m_capture.DrainTo(target, CaptureRingCapacity);
            droppedCap = (int)(m_capture.DroppedCap - before);
            if (target.Count > start)
            {
                long nowMs = NowMs();
                int write = start;
                for (int i = start; i < target.Count; i++)
                {
                    RemoteSoundClipData clip = target[i];
                    if (m_covered.TryConsume(clip.Sound, clip.Volume, nowMs))
                    {
                        m_globalCoveredSuppressed++;
                        continue;
                    }
                    target[write++] = clip;
                }
                if (write < target.Count) target.RemoveRange(write, target.Count - write);
            }
            return true;
        }

        /// <summary>取走最近一次捕获故障（模块用于限流告警）；没有 = false。</summary>
        public bool TryTakeHostCaptureFault(out string reason)
        {
            reason = m_lastFault;
            m_lastFault = null;
            return reason != null;
        }

        // ================================================================= 连续捕获（SuSubsystemAudio 回调）

        public void CaptureBeforeDrain(SubsystemAudio audio)
        {
            if (audio == null) return;
            if (!m_clientReplica && !m_hostCaptureEnabled) return;
            Project project = GameManager.Project;
            if (project == null) return;
            if (!ReferenceEquals(project, m_project) || !ReferenceEquals(audio, m_audio))
            {
                // Pump 还没跑到新世界：不猜，等下一帧由 Pump 重新挂接。
                m_shadowValid = false;
                return;
            }
            if (!ResolveReflection(audio)) return;
            IList queue;
            try
            {
                queue = m_queueField.GetValue(audio) as IList;
            }
            catch (Exception e)
            {
                Fault("queue-read:" + e.GetType().Name, e);
                return;
            }
            if (queue == null)
            {
                Fault("queue-not-list", null);
                return;
            }
            if (!ReferenceEquals(queue, m_queue))
            {
                m_queue = queue;
                m_shadowValid = false;
            }
            int count;
            try
            {
                count = queue.Count;
            }
            catch (Exception e)
            {
                Fault("queue-count:" + e.GetType().Name, e);
                return;
            }
            double gameTime = m_time?.GameTime ?? 0.0;
            long nowMs = (long)(Time.RealTime * 1000.0);
            m_globalFrames++;
            if (count == 0)
            {
                m_shadowCount = 0;
                m_shadowValid = true;
                m_shadowGameTime = gameTime;
                // 队列已空 = 之前槽位窗口捕获的条目都已被 drain 处理；残留的抑制账本只会
                // 污染后续同名条目，显式清掉并计数（正常路径不应出现）。
                ClearSlotSuppression("queue-empty");
                ExpireLedgers(nowMs);
                return;
            }
            if (!m_shadowValid)
            {
                Seed(queue, count, gameTime, nowMs);
                return;
            }
            int survivors = 0;
            for (int i = 0; i < m_shadowCount; i++)
            {
                if (m_shadow[i].Time > m_shadowGameTime) m_shadow[survivors++] = m_shadow[i];
            }
            if (survivors > count)
            {
                Resync(queue, count, gameTime, nowMs, "queue-shrank");
                return;
            }
            if (survivors > 0)
            {
                if (!TryReadEntry(queue, survivors - 1, out RemoteSoundQueueEntry boundary) ||
                    !SameEntry(in boundary, in m_shadow[survivors - 1]))
                {
                    Resync(queue, count, gameTime, nowMs, "boundary-mismatch");
                    return;
                }
            }
            int added = count - survivors;
            if (m_shadow.Length < count) Array.Resize(ref m_shadow, NextCapacity(count));
            for (int i = 0; i < added; i++)
            {
                if (!TryReadEntry(queue, survivors + i, out RemoteSoundQueueEntry entry))
                {
                    m_captureInvalid++;
                    continue;
                }
                m_shadow[survivors + i] = entry;
                if (m_slotSuppress.TryConsume(in entry, survivors + i, out _))
                {
                    // 该条已在某个本机角色的更新窗口里被归属并以 SlotSource 入队：
                    // 全局路径绝不重复发送（防双发的核心）。
                    m_slotGlobalSuppressed++;
                    continue;
                }
                HandleNewEntry(in entry, gameTime, nowMs);
            }
            m_shadowCount = count;
            m_shadowGameTime = gameTime;
            ExpireLedgers(nowMs);
        }

        private static int NextCapacity(int needed)
        {
            int capacity = 64;
            while (capacity < needed)
            {
                if (capacity > int.MaxValue / 2) return needed;
                capacity *= 2;
            }
            return capacity;
        }

        private void Seed(IList queue, int count, double gameTime, long nowMs)
        {
            if (m_shadow.Length < count) Array.Resize(ref m_shadow, NextCapacity(count));
            int read = 0;
            for (int i = 0; i < count; i++)
            {
                if (!TryReadEntry(queue, i, out RemoteSoundQueueEntry entry)) continue;
                m_shadow[read++] = entry;
            }
            m_shadowCount = read;
            m_shadowGameTime = gameTime;
            m_shadowValid = true;
            if (!m_clientReplica) m_globalSeedEntries += read;
            // 重新播种 = 本帧不处理"新增"（已捕获的槽位声音早已作为 SlotSource 入队）。
            // 账本里剩下的条目不会再被全局差集看到（已在 shadow 里 / 到点被移除），
            // 必须清掉，否则同名同音量的下一条声音会被误吞（漏发）。计数并限流告警。
            ClearSlotSuppression("reseed");
            ExpireLedgers(nowMs);
        }

        /// <summary>清空槽位抑制账本；有残留时计 <c>slotSuppressCleared</c> 并限流记录原因。</summary>
        private void ClearSlotSuppression(string reason)
        {
            int dropped = m_slotSuppress.Clear();
            if (dropped <= 0) return;
            m_slotSuppressCleared += dropped;
            LogProblem("slot-suppress-clear:" + reason,
                "本机角色声音窗口账本在 " + reason + " 时清掉 " + dropped +
                " 条未匹配记录 → 这些条目已作为 SlotSource 发出，全局差集不会重复发送" +
                "（累计清理 " + m_slotSuppressCleared + "）");
        }

        private void Resync(IList queue, int count, double gameTime, long nowMs, string reason)
        {
            m_globalResyncs++;
            LogProblem("capture-resync:" + reason,
                "全局音频队列差集失配（" + reason + "）→ 重新同步 shadow 并丢弃本帧新增（" +
                "不重复发送；持续出现请检查是否有其他音频队列写入者）");
            Seed(queue, count, gameTime, nowMs);
        }

        private void HandleNewEntry(in RemoteSoundQueueEntry entry, double gameTime, long nowMs)
        {
            if (string.IsNullOrEmpty(entry.Name) ||
                !RemoteSoundPolicy.IsValidSoundName(entry.Name))
            {
                m_captureInvalid++;
                return;
            }
            if (m_clientReplica)
            {
                // 客户端本机排进队列的声音：只用于"主机回声"抑制。
                if (m_presentedNames.TryConsume(entry.Name, 0f, nowMs))
                {
                    m_globalPresentedConsumed++;
                    return;
                }
                m_localEcho.Record(entry.Name, nowMs);
                return;
            }
            if (!m_hostCaptureEnabled) return;
            // 注意：定向事件的覆盖账本在**排空时**消费（TryDrainHostSounds），不是在这里。
            // 原因：拾取声音由引擎在 SubsystemPickables.Update 里入队，而对应的定向事件
            // 在模块 Tick（本帧 project update 之后）才登记；如果在这里就消费，同一帧
            // 已经被捕获的声音会绕过覆盖，导致客户端双播。延后到模块排空时匹配，
            // 两个顺序（音频先于 / 后于发射者）都能正确处理。
            float volume = entry.Volume;
            float pitch = entry.Pitch;
            if (float.IsNaN(volume) || float.IsInfinity(volume) || volume <= 0f)
            {
                m_captureInvalid++;
                return;
            }
            if (volume <= MinRelayVolume)
            {
                m_globalInaudible++;
                return;
            }
            SubsystemTime time = m_time;
            if (time != null &&
                (time.GameTimeFactor != 1f || time.FixedTimeStep.HasValue))
            {
                m_globalInactive++;
                return;
            }
            if (entry.Name.StartsWith("Audio/UI/", StringComparison.Ordinal))
            {
                m_globalUiSkipped++;
                return;
            }
            if (!TryGetHostListener(out Vector3 listener))
            {
                m_globalNoListener++;
                LogProblem("capture-no-listener",
                    "主机没有音频监听者（无头服务端 / 首帧）→ 世界声音无法反解位置，明确跳过" +
                    "（不会拿原点或随机方向猜；有玩家端但无本机监听者时全局中继不生效）");
                return;
            }
            if (!float.IsNaN(pitch) && !float.IsInfinity(pitch))
            {
                float clamped = MathUtils.Clamp(pitch, RemoteSoundPolicy.MinPitch,
                    RemoteSoundPolicy.MaxPitch);
                if (clamped != pitch)
                {
                    m_globalClamped++;
                    pitch = clamped;
                }
            }
            else
            {
                m_captureInvalid++;
                return;
            }
            float maxVolume = RemoteSoundPolicy.MaxVolume;
            if (volume > maxVolume)
            {
                m_globalClamped++;
                volume = maxVolume;
            }
            double delay = entry.Time - gameTime;
            if (delay < 0.0) delay = 0.0;
            bool autoDelay = delay > AutoDelayEpsilon;
            float fromDelay = 0f;
            if (autoDelay)
            {
                double distance = delay * 120.0;
                if (distance > RemoteSoundPolicy.HostSourceDistanceMax)
                    distance = RemoteSoundPolicy.HostSourceDistanceMax;
                fromDelay = (float)distance;
                m_globalPositionFromDelay++;
            }
            float fromVolume = RemoteSoundPolicy.InverseDistance(volume,
                RemoteSoundPolicy.DefaultMinDistance,
                RemoteSoundPolicy.HostSourceDistanceMax);
            float sourceDistance = MathUtils.Max(fromDelay, fromVolume);
            bool nonPositional = Math.Abs(entry.Pan) > 0.001f;
            if (nonPositional)
            {
                m_globalPositionNonPositional++;
                sourceDistance = 0f;
            }
            else if (autoDelay && fromDelay >= fromVolume)
            {
                // 由 delay 反解（精确几何距离）；上面已经计过。
            }
            else
            {
                m_globalPositionFromVolume++;
            }
            var clip = new RemoteSoundClipData(entry.Name, listener, volume, pitch,
                RemoteSoundPolicy.DefaultMinDistance, autoDelay,
                nonPositional
                    ? RemoteSoundAttenuation.NonPositional
                    : RemoteSoundAttenuation.RelativeToHost,
                sourceDistance);
            m_globalSounds++;
            m_capture.TryAdd(in clip);
        }

        private bool TryGetHostListener(out Vector3 listener)
        {
            listener = Vector3.Zero;
            SubsystemAudio audio = m_audio;
            if (audio == null) return false;
            ReadOnlyList<Vector3> positions;
            try
            {
                positions = audio.ListenerPositions;
            }
            catch (Exception)
            {
                return false;
            }
            if (positions.Count == 0) return false;
            if (positions.Count > 1) m_globalMultiListener++;
            listener = positions[0];
            return IsFinite(listener);
        }

        private void ExpireLedgers(long nowMs)
        {
            if (nowMs - m_lastExpireMs < SummaryExpireMs) return;
            m_lastExpireMs = nowMs;
            int expired = m_covered.Expire(nowMs);
            m_presentedNames.Expire(nowMs);
            m_localEcho.Expire(nowMs);
            if (expired > 0)
            {
                LogProblem("cover-unused",
                    "有 " + expired + " 条定向事件已登记的声音在 " + CoveredTtlMs +
                    " ms 内没有在主机音频队列里匹配到（主机实际没播 / 参数不一致）→ 已作废并计数");
            }
        }

        /// <summary>
        /// 无头主机（没有任何 GameWidget 监听者）的显式适配：
        /// 引擎的 <c>CalculateListenerDistance</c> 在没有监听者时返回 float.MaxValue，
        /// 所有位置声音入队时音量都被衰减成 0 —— 队列里只剩名字，连距离都反解不出来。
        /// 在 <c>base.Update</c> 清空监听者表之后，把**客户端化身位置**装入
        /// <c>m_listenerPositions</c>（只读反射；不碰队列 / 不碰播放判定），
        /// 下一次入队就能得到"到最近客户端"的真实距离与音量，捕获端照常反解。
        ///
        /// 范围与限制（明确计数，不静默）：
        ///   · 只在"主机 + 全局捕获已启用 + 本帧没有任何监听者"时执行；
        ///   · 用所有可定位玩家端（≤4）作为监听者：引擎按**最近**的一个算衰减；
        ///     捕获锚点取 <c>ListenerPositions[0]</c>（第一个客户端），因此单客户端
        ///     会话完全正确；多客户端时其他端按 RelativeToHost 的同一估计模型重投影
        ///     （方向本身无法从引擎队列反解，文档已写明）；
        ///   · 这是引擎监听者的**替代**，不是静音 / 不改变主机对真实玩家的播放。
        /// </summary>
        public void AfterAudioUpdate(SubsystemAudio audio)
        {
            if (audio == null || m_clientReplica || !m_hostCaptureEnabled) return;
            if (m_listenerListField == null) return;
            ReadOnlyList<Vector3> current;
            try
            {
                current = audio.ListenerPositions;
            }
            catch (Exception e)
            {
                m_virtualListenerFailures++;
                LogProblem("virtual-listener-read:" + e.GetType().Name,
                    "读取音频监听者失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 无头主机本帧无法建立虚拟监听者");
                return;
            }
            if (current.Count > 0) return;
            m_virtualListeners.Clear();
            int limit = 4;
            foreach (int clientId in m_spawner.ClientIds)
            {
                if (m_virtualListeners.Count >= limit) break;
                if (!m_spawner.TryGetAvatarCell(clientId, out Vector3 position, out _)) continue;
                if (!IsFinite(position)) continue;
                if (m_virtualListeners.Contains(position)) continue;
                m_virtualListeners.Add(position + Vector3.UnitY * 1.6f);
            }
            if (m_virtualListeners.Count == 0)
            {
                m_virtualListenerEmpty++;
                return;
            }
            List<Vector3> list;
            try
            {
                list = m_listenerListField.GetValue(audio) as List<Vector3>;
            }
            catch (Exception e)
            {
                m_virtualListenerFailures++;
                LogProblem("virtual-listener-field:" + e.GetType().Name,
                    "读不到 SubsystemAudio.m_listenerPositions（" + e.GetType().Name + " " +
                    e.Message + "）→ 无头主机全局中继不可用（明确计数）");
                return;
            }
            if (list == null)
            {
                m_virtualListenerFailures++;
                LogProblem("virtual-listener-null",
                    "SubsystemAudio.m_listenerPositions 为 null → 无头主机全局中继不可用");
                return;
            }
            try
            {
                list.Clear();
                for (int i = 0; i < m_virtualListeners.Count; i++)
                    list.Add(m_virtualListeners[i]);
            }
            catch (Exception e)
            {
                m_virtualListenerFailures++;
                LogProblem("virtual-listener-write:" + e.GetType().Name,
                    "写入虚拟监听者失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 无头主机全局中继不可用");
                return;
            }
            m_virtualListenerFrames++;
            m_virtualListenerClients += m_virtualListeners.Count;
            if (m_virtualListenerFrames == 1)
            {
                LogProblem("virtual-listener-on",
                    "主机没有本机音频监听者（无头服务端）→ 用 " + m_virtualListeners.Count +
                    " 个客户端化身位置作为虚拟监听者（只影响主机侧的声音距离计算；" +
                    "单客户端会话完全正确，多客户端按最近客户端衰减 + 客户端重投影）");
            }
        }

        // ================================================================= 本机多角色槽位归属

        /// <summary>
        /// 每帧对账（模块 Tick 调用）：决定是否给主机本机角色安装"组件 Update 捕获窗口"。
        /// 只在 **主机 + 已连接 + 至少一个可定位玩家端** 时安装；客户端 / 单机 / 无头服务端
        /// （没有本机角色）完全不安装，引擎更新派发保持原状。
        /// </summary>
        private void PumpLocalWindows(bool allowRestore)
        {
            Project project = GameManager.Project;
            bool enabled = project != null && !m_clientReplica && m_hostCaptureEnabled;
            if (m_localWindows == null)
            {
                if (!enabled)
                {
                    m_slotPlayers = 0;
                    return;
                }
                m_localWindows = new LocalPlayerAudioWindows(m_spawner, this);
            }
            m_localWindows.Pump(project, enabled, allowRestore);
            m_slotPlayers = m_localWindows.SlotCount;
        }

        void ILocalPlayerAudioWindowSink.BeginLocalAudioWindow(int slot, int playerIndex,
            ComponentPlayer player)
        {
            try
            {
                BeginLocalSlotWindow(slot, playerIndex, player);
            }
            catch (Exception e)
            {
                m_slotWindowFaults++;
                LogProblem("slot-begin:" + e.GetType().Name,
                    "打开本机角色声音窗口异常（playerIndex=" + playerIndex + " " +
                    e.GetType().Name + " " + e.Message + "）→ 本次不归属（声音按全局路径处理）");
            }
        }

        void ILocalPlayerAudioWindowSink.EndLocalAudioWindow(int slot, int playerIndex)
        {
            try
            {
                EndLocalSlotWindow(slot, playerIndex);
            }
            catch (Exception e)
            {
                m_slotWindowFaults++;
                LogProblem("slot-end:" + e.GetType().Name,
                    "关闭本机角色声音窗口异常（playerIndex=" + playerIndex + " " +
                    e.GetType().Name + " " + e.Message + "）→ 本次不归属（声音按全局路径处理）");
            }
        }

        void ILocalPlayerAudioWindowSink.NoteLocalPlayerWindowProblem(string key, string message) =>
            LogProblem(key, message);

        private void BeginLocalSlotWindow(int slot, int playerIndex, ComponentPlayer player)
        {
            if (m_clientReplica || !m_hostCaptureEnabled) return;
            SubsystemAudio audio = m_audio;
            if (audio == null)
            {
                m_slotWindowFaults++;
                return;
            }
            if (!ResolveReflection(audio))
            {
                m_slotWindowFaults++;
                LogProblem("slot-reflect",
                    "本机角色声音窗口无法解析音频队列（" + (m_reflectionProblem ?? "unknown") +
                    "）→ 该角色声音按全局路径处理（明确计数）");
                return;
            }
            if (!TryGetAudioQueue(audio, out IList queue))
            {
                m_slotWindowFaults++;
                return;
            }
            if (m_time == null || m_time.GameTimeFactor != 1f ||
                m_time.FixedTimeStep.HasValue)
            {
                // 暂停 / 睡觉快进 / 菜单：跟全局中继同一闸门，不做归属（全局路径会计 inactive）。
                m_slotRejectedInactive++;
                return;
            }
            var window = new LocalSlotWindow
            {
                Slot = slot,
                PlayerIndex = playerIndex,
                Player = player,
                Queue = queue,
                StartIndex = queue.Count,
                GameTime = m_time.GameTime,
                Epoch = m_captureWindowEpoch,
                Overlap = m_captureActive || m_slotWindows.Count > 0
            };
            m_slotWindows.Add(window);
            m_slotWindowsOpened++;
        }

        private void EndLocalSlotWindow(int slot, int playerIndex)
        {
            int last = m_slotWindows.Count - 1;
            if (last < 0)
            {
                m_slotWindowFaults++;
                LogProblem("slot-window-underflow",
                    "关闭本机角色声音窗口时栈为空（playerIndex=" + playerIndex +
                    "）→ 本次不归属（明确计数）");
                return;
            }
            LocalSlotWindow window = m_slotWindows[last];
            m_slotWindows.RemoveAt(last);
            if (window.Slot != slot || window.PlayerIndex != playerIndex)
            {
                m_slotWindowFaults++;
                LogProblem("slot-window-mismatch",
                    "本机角色声音窗口不匹配（打开 slot=" + window.Slot + "/playerIndex=" +
                    window.PlayerIndex + "，关闭 slot=" + slot + "/playerIndex=" +
                    playerIndex + "）→ 仍按打开时记录归属并计数");
            }
            if (window.Overlap || m_captureActive ||
                window.Epoch != m_captureWindowEpoch)
            {
                // 远端动作窗口与角色窗口重叠：远端动作的精确锚点优先，绝不双发。
                m_slotWindowOverlaps++;
                LogProblem("slot-window-overlap",
                    "本机角色声音窗口与远端动作捕获窗口重叠（playerIndex=" +
                    window.PlayerIndex + "）→ 放弃本次槽位归属（由精确锚点 / 全局路径处理）");
                return;
            }
            if (m_clientReplica || !m_hostCaptureEnabled) return;
            SubsystemAudio audio = m_audio;
            if (audio == null)
            {
                m_slotWindowFaults++;
                return;
            }
            if (!TryGetAudioQueue(audio, out IList queue) ||
                !ReferenceEquals(queue, window.Queue))
            {
                m_slotWindowFaults++;
                LogProblem("slot-window-queue",
                    "本机角色声音窗口期间音频队列对象被替换（playerIndex=" +
                    window.PlayerIndex + "）→ 本次不归属（明确计数）");
                return;
            }
            int end;
            try
            {
                end = queue.Count;
            }
            catch (Exception e)
            {
                m_slotWindowFaults++;
                LogProblem("slot-window-count:" + e.GetType().Name,
                    "读取本机角色声音窗口队列长度失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 本次不归属");
                return;
            }
            if (end < window.StartIndex)
            {
                m_slotWindowFaults++;
                LogProblem("slot-window-shrink",
                    "本机角色声音窗口内音频队列变短（" + end + " < " + window.StartIndex +
                    "，playerIndex=" + window.PlayerIndex + "）→ 本次不归属");
                return;
            }
            int added = end - window.StartIndex;
            if (added == 0) return;
            ComponentPlayer player = window.Player;
            ComponentBody body = player?.ComponentBody;
            if (body == null || !IsFinite(body.Position))
            {
                m_slotRejectedNoPosition += added;
                LogProblem("slot-no-body",
                    "本机角色声音窗口内新增 " + added + " 条声音，但角色没有可用的 " +
                    "ComponentBody.Position（playerIndex=" + window.PlayerIndex +
                    "）→ 不猜位置，改由全局路径处理");
                return;
            }
            Vector3 position = body.Position;
            float hostDistance;
            try
            {
                hostDistance = audio.CalculateListenerDistance(position);
            }
            catch (Exception e)
            {
                m_slotWindowFaults++;
                LogProblem("slot-distance:" + e.GetType().Name,
                    "计算本机角色到最近监听者距离失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 本次不归属");
                return;
            }
            if (float.IsNaN(hostDistance) || float.IsInfinity(hostDistance) ||
                hostDistance < 0f ||
                hostDistance >= RemoteSoundPolicy.HostSourceDistanceMax)
            {
                // 没有任何监听者（无头 / 首帧）：引擎侧音量会被衰减成 0，无从反解距离。
                m_slotRejectedNoListener += added;
                LogProblem("slot-no-listener",
                    "本机角色声音窗口没有可用的音频监听者（hostDistance=" + hostDistance +
                    "）→ 本次不归属（明确计数，不做距离猜测）");
                return;
            }
            int captured = 0;
            for (int i = window.StartIndex; i < end; i++)
            {
                if (!TryReadEntry(queue, i, out RemoteSoundQueueEntry entry))
                {
                    m_captureInvalid++;
                    continue;
                }
                if (!HostSlotSoundCapturePolicy.TryBuildSlotClip(in entry, true,
                        window.GameTime, position, hostDistance,
                        out RemoteSoundClipData clip,
                        out HostSlotSoundCapturePolicy.EntryRejection rejection))
                {
                    CountSlotRejection(rejection);
                    continue;
                }
                // 先登记抑制（全局差集下一次读到这条时跳过），再把槽位声音放进捕获环。
                m_slotSuppress.Add(in entry, i, window.Slot);
                m_capture.TryAdd(in clip);
                captured++;
            }
            if (captured > 0)
            {
                m_slotSounds += captured;
                m_slotWindowsWithSounds++;
            }
        }

        private void CountSlotRejection(HostSlotSoundCapturePolicy.EntryRejection rejection)
        {
            switch (rejection)
            {
                case HostSlotSoundCapturePolicy.EntryRejection.Inaudible:
                    m_slotRejectedInaudible++;
                    break;
                case HostSlotSoundCapturePolicy.EntryRejection.TimeInactive:
                    m_slotRejectedInactive++;
                    break;
                case HostSlotSoundCapturePolicy.EntryRejection.UiSound:
                    m_slotRejectedUi++;
                    break;
                case HostSlotSoundCapturePolicy.EntryRejection.InvalidHostDistance:
                    m_slotRejectedNoListener++;
                    break;
                default:
                    m_slotRejectedInvalid++;
                    break;
            }
        }

        /// <summary>读引擎音频队列实例（与全局捕获共用 m_queue 缓存；换对象 = 需要重播种）。</summary>
        private bool TryGetAudioQueue(SubsystemAudio audio, out IList queue)
        {
            queue = null;
            object value;
            try
            {
                value = m_queueField.GetValue(audio);
            }
            catch (Exception e)
            {
                LogProblem("slot-queue-read:" + e.GetType().Name,
                    "读取本机角色声音窗口的音频队列失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 本次不归属");
                return false;
            }
            if (!(value is IList list))
            {
                LogProblem("slot-queue-not-list",
                    "本机角色声音窗口的音频队列不是 IList → 本次不归属（明确计数）");
                return false;
            }
            if (!ReferenceEquals(list, m_queue))
            {
                m_queue = list;
                m_shadowValid = false;
            }
            queue = list;
            return true;
        }

        // ================================================================= 反射 / 读取

        private bool ResolveReflection(SubsystemAudio audio)
        {
            if (m_reflectionResolved) return true;
            Type type = typeof(SubsystemAudio);
            m_listenerListField = type.GetField("m_listenerPositions",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (m_listenerListField == null ||
                m_listenerListField.FieldType != typeof(List<Vector3>))
            {
                m_reflectionProblem = "m_listenerPositions-not-list";
                m_listenerListField = null;
                return false;
            }
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
            BuildFastReader(itemType);
            m_reflectionProblem = null;
            m_reflectionResolved = true;
            return true;
        }

        /// <summary>
        /// 生成不装箱的读取器：直接读 List&lt;SoundInfo&gt; 的 _items 数组元素与字段。
        /// 失败时退回 IList + FieldInfo（只影响性能，计数 globalSlowReads，不静默）。
        /// </summary>
        private void BuildFastReader(Type itemType)
        {
            m_fastReader = null;
            try
            {
                Type listType = typeof(List<>).MakeGenericType(itemType);
                m_listItemsField = listType.GetField("_items",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (m_listItemsField == null)
                {
                    m_fastReaderProblem = "List<T>._items-not-found";
                    return;
                }
                var method = new DynamicMethod("SuMP_ReadSoundInfo", typeof(void),
                    new[]
                    {
                        typeof(object), typeof(int), typeof(double).MakeByRefType(),
                        typeof(string).MakeByRefType(), typeof(float).MakeByRefType(),
                        typeof(float).MakeByRefType(), typeof(float).MakeByRefType()
                    }, typeof(RemoteActionAudioReplica).Module, true);
                ILGenerator il = method.GetILGenerator();
                LocalBuilder local = il.DeclareLocal(itemType);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, listType);
                il.Emit(OpCodes.Ldfld, m_listItemsField);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldelem, itemType);
                il.Emit(OpCodes.Stloc, local);
                EmitOutField(il, local, m_soundTimeField, 2, OpCodes.Stind_R8);
                EmitOutField(il, local, m_soundNameField, 3, OpCodes.Stind_Ref);
                EmitOutField(il, local, m_soundVolumeField, 4, OpCodes.Stind_R4);
                EmitOutField(il, local, m_soundPitchField, 5, OpCodes.Stind_R4);
                EmitOutField(il, local, m_soundPanField, 6, OpCodes.Stind_R4);
                il.Emit(OpCodes.Ret);
                m_fastReader = (SoundEntryReader)method.CreateDelegate(typeof(SoundEntryReader));
                m_fastReaderProblem = null;
            }
            catch (Exception e)
            {
                m_fastReader = null;
                m_listItemsField = null;
                m_fastReaderProblem = e.GetType().Name + ":" + e.Message;
                LogProblem("fast-reader:" + e.GetType().Name,
                    "音频队列强类型读取器生成失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 退回反射读取（正确但每条新增声音有装箱分配）");
            }
        }

        /// <summary>
        /// out 参数在 IL 里是托管指针：先 ldarg 拿到指针，再算字段值，最后 stind.* 写回。
        /// （直接 starg 会往"指针槽"里写值，生成非法 IL —— 这里必须走间接存储。）
        /// </summary>
        private static void EmitOutField(ILGenerator il, LocalBuilder local, FieldInfo field,
            short argument, OpCode storeIndirect)
        {
            il.Emit(OpCodes.Ldarg, argument);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(storeIndirect);
        }

        private bool TryReadEntry(IList queue, int index, out RemoteSoundQueueEntry entry)
        {
            entry = default;
            try
            {
                if (m_fastReader != null)
                {
                    m_fastReader(queue, index, out double time, out string name,
                        out float volume, out float pitch, out float pan);
                    entry = new RemoteSoundQueueEntry(time, name, volume, pitch, pan);
                    return true;
                }
                m_globalSlowReads++;
                object item = queue[index];
                if (item == null) return false;
                entry = new RemoteSoundQueueEntry(
                    (double)m_soundTimeField.GetValue(item),
                    (string)m_soundNameField.GetValue(item),
                    (float)m_soundVolumeField.GetValue(item),
                    (float)m_soundPitchField.GetValue(item),
                    (float)m_soundPanField.GetValue(item));
                return true;
            }
            catch (Exception e)
            {
                Fault("entry-read:" + e.GetType().Name, e);
                return false;
            }
        }

        private static bool SameEntry(in RemoteSoundQueueEntry a, in RemoteSoundQueueEntry b) =>
            a.SameAs(in b);

        private void Fault(string reason, Exception exception)
        {
            m_globalFaults++;
            m_lastFault = reason + (exception == null
                ? string.Empty
                : "(" + exception.GetType().Name + ": " + exception.Message + ")");
            LogProblem("capture-fault:" + reason,
                "主机声音连续捕获失败（" + reason + "）→ 本帧不采集（主机播放不受影响）" +
                (exception == null ? string.Empty : "：" + exception.Message));
            m_shadowValid = false;
        }

        public void NoteCaptureFault(Exception exception)
        {
            m_globalFaults++;
            m_lastFault = "hook-exception:" + exception?.GetType().Name;
            LogProblem("capture-hook:" + exception?.GetType().Name,
                "SuSubsystemAudio 捕获回调异常（" + exception?.GetType().Name + " " +
                exception?.Message + "）→ 本帧不采集（base.Update 仍执行）");
            m_shadowValid = false;
        }

        // ================================================================= 覆盖账本（防双播）

        /// <summary>登记"这条声音已经由别的定向事件中继过"（精确名字 + 主机侧衰减音量）。</summary>
        public void CoverPlayedSound(string sound, Vector3 position, float volume,
            float minDistance, int count, string owner)
        {
            Cover(sound, false, position, volume, minDistance, count, owner, 0f);
        }

        /// <summary>同上，但声音名是 PlayRandomSound 的目录前缀。</summary>
        public void CoverPlayedSoundDirectory(string directory, Vector3 position, float volume,
            float minDistance, int count, string owner)
        {
            Cover(directory, true, position, volume, minDistance, count, owner, 0f);
        }

        /// <summary>音量是区间（随机音量）时的覆盖：中心 + 半宽。</summary>
        public void CoverPlayedSoundRange(string sound, Vector3 position, float minVolume,
            float maxVolume, float minDistance, int count, string owner)
        {
            if (maxVolume < minVolume)
            {
                float swap = minVolume;
                minVolume = maxVolume;
                maxVolume = swap;
            }
            float center = (minVolume + maxVolume) * 0.5f;
            float half = (maxVolume - minVolume) * 0.5f;
            Cover(sound, false, position, center, minDistance, count, owner, half);
        }

        private void Cover(string name, bool prefix, Vector3 position, float volume,
            float minDistance, int count, string owner, float extraVolumeTolerance)
        {
            if (string.IsNullOrEmpty(name) || count <= 0) return;
            if (!m_hostCaptureEnabled) return;
            m_coverRequests += count;
            SubsystemAudio audio = m_audio;
            if (audio == null)
            {
                m_coverFailures += count;
                LogProblem("cover-no-audio",
                    "定向事件声音覆盖登记失败（" + owner + " " + name + "）：没有 SubsystemAudio" +
                    " → 全局中继可能多发这一条（明确计数）");
                return;
            }
            float distance;
            try
            {
                distance = audio.CalculateListenerDistance(position);
            }
            catch (Exception e)
            {
                m_coverFailures += count;
                LogProblem("cover-distance:" + e.GetType().Name,
                    "定向事件声音覆盖登记失败（" + owner + " " + name + "）：距离计算异常 " +
                    e.GetType().Name + " → 明确计数");
                return;
            }
            float attenuation = RemoteSoundPolicy.CalculateVolume(distance, minDistance);
            float queued = attenuation * volume;
            if (float.IsNaN(queued) || float.IsInfinity(queued) || queued < 0f)
            {
                m_coverFailures += count;
                LogProblem("cover-volume", "定向事件声音覆盖登记失败（" + owner + " " + name +
                    "）：衰减音量非法（" + queued + "）→ 明确计数");
                return;
            }
            float tolerance = 0.0001f + Math.Abs(queued) * 0.0001f +
                Math.Abs(extraVolumeTolerance) * attenuation;
            m_covered.Add(name, prefix, queued, tolerance, count, NowMs(), CoveredTtlMs);
        }

        // ================================================================= 主机：远端动作捕获窗口

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
            m_gameTime = m_time?.GameTime ?? project.FindSubsystem<SubsystemTime>(false)?.GameTime ?? 0.0;
            m_gameTimeKnown = m_time != null || project.FindSubsystem<SubsystemTime>(false) != null;
            m_captureActive = true;
            m_captureWindowEpoch++;
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
            long nowMs = NowMs();
            for (int i = 0; i < added; i++)
            {
                if (!TryReadEntry(queue, startCount + i, out RemoteSoundQueueEntry entry))
                {
                    m_captureInvalid++;
                    invalidInWindow++;
                    continue;
                }
                string name = entry.Name;
                float volume = entry.Volume;
                float pitch = entry.Pitch;
                float pan = entry.Pan;
                if (!RemoteSoundPolicy.IsValidSoundName(name) ||
                    float.IsNaN(volume) || float.IsInfinity(volume) || volume < 0f)
                {
                    m_captureInvalid++;
                    invalidInWindow++;
                    continue;
                }
                // 同一条声音即将由全局捕获下一次读到：登记精确覆盖，绝不重复发送。
                m_covered.Add(name, false, volume, 0.0001f + Math.Abs(volume) * 0.0001f, 1,
                    nowMs, CoveredTtlMs);
                double delay = gameTimeKnown ? entry.Time - gameTime : 0.0;
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
            m_localListeners.Clear();
            if (!TryGetLocalListenerPositions(m_localListeners, out reason)) return false;
            position = m_localListeners[0];
            return true;
        }

        /// <summary>
        /// 本机**所有本地角色**的听音位置（分裂屏 / 多角色：每个本机角色一条）。
        ///
        /// 取 <c>ComponentBody.Position + UnitY*1.6</c>（与无头主机虚拟监听者同一口径）：
        ///   · 只算 <c>PlayersData</c> 里**不是** NetworkAvatarSpawner 化身的那几条 ——
        ///     远端化身的相机也是引擎监听者，但那是别人的角色，不能用来裁剪"我能听多远"；
        ///   · 一个本机角色都读不到时明确失败（<paramref name="reason"/>），绝不拿原点猜；
        ///   · 调用方按"离声源最近的那条"做距离裁剪 / 音量重投影（多角色感知）。
        /// </summary>
        public bool TryGetLocalListenerPositions(List<Vector3> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "target-null";
                return false;
            }
            target.Clear();
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players != null)
            {
                string firstProblem = null;
                foreach (PlayerData data in players.PlayersData)
                {
                    if (data == null) continue;
                    if (m_spawner.Contains(data)) continue;   // 远端化身不是本机角色
                    ComponentPlayer player = data.ComponentPlayer;
                    ComponentBody body = player?.ComponentBody;
                    if (body == null)
                    {
                        if (firstProblem == null)
                            firstProblem = "local-player-body-missing:index=" + data.PlayerIndex;
                        continue;
                    }
                    Vector3 listener = body.Position + Vector3.UnitY * 1.6f;
                    if (!IsFinite(listener))
                    {
                        if (firstProblem == null)
                            firstProblem = "local-player-position-invalid:index=" + data.PlayerIndex;
                        continue;
                    }
                    target.Add(listener);
                }
                if (target.Count > 0) return true;
                if (firstProblem != null) reason = firstProblem;
            }
            // 兜底：引擎监听者（没有 SubsystemPlayers 的极早期帧 / 非标准世界）。这里取第一条
            // 只是"主角色"的可用值，绝不参与槽位归属（那是主机侧逻辑）。
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            if (audio != null)
            {
                ReadOnlyList<Vector3> listeners = audio.ListenerPositions;
                if (listeners.Count > 0 && IsFinite(listeners[0]))
                {
                    target.Add(listeners[0]);
                    return true;
                }
            }
            if (m_spawner.TryGetLocalPlayer(project, out PlayerData localPlayer,
                    out string playerReason))
            {
                ComponentPlayer player = localPlayer.ComponentPlayer;
                if (player?.ComponentBody != null && IsFinite(player.ComponentBody.Position))
                {
                    target.Add(player.ComponentBody.Position);
                    return true;
                }
                reason = "local-player-body-missing";
                return false;
            }
            if (reason == null) reason = playerReason;
            return false;
        }

        /// <summary>
        /// 客户端本机是否刚播过同名声音（主机中继回声抑制）。只在客户端副本上生效；
        /// 窗口 <see cref="LocalEchoWindowMs"/>；每次最多消费一条。
        /// </summary>
        public bool TryConsumeLocalEcho(string sound)
        {
            if (!m_clientReplica || string.IsNullOrEmpty(sound)) return false;
            bool consumed = m_localEcho.TryConsume(sound, NowMs(), LocalEchoWindowMs);
            if (consumed) m_localEchoSkipped++;
            return consumed;
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
            long nowMs = NowMs();
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
                // Present 自己排进队列的声音不能算"本机声音"（否则会反过来屏蔽后续中继）。
                m_presentedNames.Add(clip.Sound, false, 0f, float.MaxValue, 1, nowMs,
                    PresentedNameTtlMs);
                try
                {
                    // Delay > 0 = 客户端按自己的距离重算的传播延迟（autoDelay 的引擎语义）；
                    // 其余仍走 autoDelay / 立即播放两个原有重载。
                    if (clip.Delay > 0.0001f)
                    {
                        audio.PlaySound(clip.Sound, clip.Volume, clip.Pitch, clip.Position,
                            clip.MinDistance, clip.Delay);
                    }
                    else
                    {
                        audio.PlaySound(clip.Sound, clip.Volume, clip.Pitch, clip.Position,
                            clip.MinDistance, clip.AutoDelay);
                    }
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
            " · global frames=" + m_globalFrames + " sounds=" + m_globalSounds +
            " seed=" + m_globalSeedEntries + " resync=" + m_globalResyncs +
            " faults=" + m_globalFaults + " inaudible=" + m_globalInaudible +
            " inactive=" + m_globalInactive +
            " noListener=" + m_globalNoListener + " uiSkipped=" + m_globalUiSkipped +
            " clamped=" + m_globalClamped +
            " positionDelay=" + m_globalPositionFromDelay +
            " positionVolume=" + m_globalPositionFromVolume +
            " nonPositional=" + m_globalPositionNonPositional +
            " multiListener=" + m_globalMultiListener +
            " slowReads=" + m_globalSlowReads +
            " disabledDropped=" + m_globalDisabledDropped +
            " virtualListenerFrames=" + m_virtualListenerFrames +
            " virtualListenerClients=" + m_virtualListenerClients +
            " virtualListenerEmpty=" + m_virtualListenerEmpty +
            " virtualListenerFaults=" + m_virtualListenerFailures +
            (string.IsNullOrEmpty(m_fastReaderProblem)
                ? string.Empty
                : " fastReaderFailed=" + m_fastReaderProblem) +
            " coveredSuppressed=" + m_globalCoveredSuppressed +
            " presentedNames=" + m_globalPresentedConsumed +
            " ring=" + m_capture.Count + "/" + m_capture.Capacity +
            " ringDropped=" + m_capture.DroppedCap +
            " coverAdded=" + m_covered.Added +
            " coverSuppressed=" + m_covered.Suppressed +
            " coverUnused=" + m_covered.ExpiredUnused +
            " coverOverflow=" + m_covered.Overflow +
            " · slot slots=" + m_slotPlayers +
            " windows=" + m_slotWindowsOpened +
            " withSounds=" + m_slotWindowsWithSounds +
            " sounds=" + m_slotSounds +
            " invalid=" + m_slotRejectedInvalid +
            " inaudible=" + m_slotRejectedInaudible +
            " inactive=" + m_slotRejectedInactive +
            " uiSkipped=" + m_slotRejectedUi +
            " noPosition=" + m_slotRejectedNoPosition +
            " noListener=" + m_slotRejectedNoListener +
            " faults=" + m_slotWindowFaults +
            " overlaps=" + m_slotWindowOverlaps +
            " globalSuppressed=" + m_slotGlobalSuppressed +
            " suppressCleared=" + m_slotSuppressCleared +
            " ledger=" + m_slotSuppress.Count +
            " ledgerOverflow=" + m_slotSuppress.Overflow +
            " · client presentCalls=" + m_presentCalls + " presented=" + m_presented +
            " failed=" + m_presentFailures + " noAudio=" + m_presentNoAudio +
            " localEchoSkipped=" + m_localEchoSkipped +
            " localEchoRecorded=" + m_localEcho.Recorded +
            " · peers noPosition=" + m_peerNoPosition + " collects=" + m_peerCollects +
            " coverRequests=" + m_coverRequests + " coverFailures=" + m_coverFailures;

        private static long NowMs() => (long)(Time.RealTime * 1000.0);

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsNaN(value.Y) && !float.IsNaN(value.Z) &&
            !float.IsInfinity(value.X) && !float.IsInfinity(value.Y) &&
            !float.IsInfinity(value.Z);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}

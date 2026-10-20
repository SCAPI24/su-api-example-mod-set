using System;
using System.Collections.Generic;
using Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 引擎音频队列里的一条（<c>SubsystemAudio.SoundInfo</c> 的只读镜像）：
    /// **没有位置** —— 位置在入队前已折算成距离衰减。槽位归属（本机多角色）靠"更新窗口
    /// 内的队列差集"把条目绑定到某个角色的真实位置；因此这里只保存引擎给出的 5 个字段。
    /// </summary>
    public readonly struct RemoteSoundQueueEntry
    {
        public RemoteSoundQueueEntry(double time, string name, float volume, float pitch,
            float pan)
        {
            Time = time;
            Name = name;
            Volume = volume;
            Pitch = pitch;
            Pan = pan;
        }

        public double Time { get; }
        public string Name { get; }
        public float Volume { get; }
        public float Pitch { get; }
        public float Pan { get; }

        public bool SameAs(in RemoteSoundQueueEntry other) =>
            Time == other.Time && Volume == other.Volume && Pitch == other.Pitch &&
            Pan == other.Pan && string.Equals(Name, other.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// 主机"本机多角色"声音归属的**纯逻辑**判定（不引用 Game，tsverify 直接编译）。
    ///
    /// 引擎事实：<c>SubsystemAudio</c> 的 <c>PlaySound / PlayRandomSound</c> 都是非虚方法，
    /// 且队列条目只有 {Time,Name,Volume,Pitch,Pan}（Source: Survivalcraft/Game/SubsystemAudio.cs:
    /// 23-34 / 98-148）。玩家声音都发生在**该角色实体自己的组件 Update 里**
    /// （footstep: ComponentHumanModel.Update / ComponentLocomotion.Update → 
    /// SubsystemSoundMaterials.PlayFootstepSound:34；yell: ComponentPlayer.Update:100-117；
    /// moan/sneeze/cough: ComponentVitalStats / ComponentFlu / ComponentSickness；
    /// sizzle: ComponentOnFire:76），因此"在这个组件 Update 前后做队列差集"就能把新增条目
    /// 绑定到该角色槽位与真实位置。
    ///
    /// 本类只做"这条队列条目能否变成槽位声音 / 变成什么参数"的判定，与全局中继的闸门
    /// 完全同口径（名字 / 音量 / 时间因子 / UI / 音高），避免两条路径判定不一致导致漏发。
    /// </summary>
    public static class HostSlotSoundCapturePolicy
    {
        /// <summary>
        /// 与引擎 <c>AudioManager.MinAudibleVolume</c> 同口径的**原始**音量阈值
        /// （引擎里 MinAudibleVolume = 0.05 * SoundsVolume，比较时约掉 SoundsVolume）。
        /// 中继**不**乘主机的 SettingsManager.SoundsVolume。
        /// </summary>
        public const float MinRelayVolume = 0.05f;

        /// <summary>槽位条目被拒绝的原因（0 = 接受）。</summary>
        public enum EntryRejection
        {
            None = 0,
            EmptyName,
            InvalidName,
            InvalidVolume,
            Inaudible,
            TimeInactive,
            UiSound,
            InvalidPitch,
            InvalidHostDistance
        }

        /// <summary>
        /// 与全局中继同序的闸门：名字 → 音量合法性 → 可听阈值 → 时间因子 → UI → 音高。
        /// 通过时输出钳制后的音量 / 音高，<paramref name="clamped"/> 表示发生过钳制。
        /// </summary>
        public static EntryRejection Classify(in RemoteSoundQueueEntry entry, bool timeActive,
            out string name, out float volume, out float pitch, out bool clamped)
        {
            name = entry.Name;
            volume = entry.Volume;
            pitch = entry.Pitch;
            clamped = false;
            if (string.IsNullOrEmpty(name)) return EntryRejection.EmptyName;
            if (!RemoteSoundPolicy.IsValidSoundName(name)) return EntryRejection.InvalidName;
            if (float.IsNaN(volume) || float.IsInfinity(volume) || volume <= 0f)
                return EntryRejection.InvalidVolume;
            if (volume <= MinRelayVolume) return EntryRejection.Inaudible;
            if (!timeActive) return EntryRejection.TimeInactive;
            if (name.StartsWith("Audio/UI/", StringComparison.Ordinal))
                return EntryRejection.UiSound;
            if (float.IsNaN(pitch) || float.IsInfinity(pitch))
                return EntryRejection.InvalidPitch;
            float clampedPitch = MathUtils.Clamp(pitch, RemoteSoundPolicy.MinPitch,
                RemoteSoundPolicy.MaxPitch);
            if (clampedPitch != pitch)
            {
                pitch = clampedPitch;
                clamped = true;
            }
            if (volume > RemoteSoundPolicy.MaxVolume)
            {
                volume = RemoteSoundPolicy.MaxVolume;
                clamped = true;
            }
            return EntryRejection.None;
        }

        /// <summary>
        /// 把"本机角色更新窗口内新增的队列条目"变成一条 <see cref="RemoteSoundAttenuation.SlotSource"/>
        /// 声音：位置 = 该角色真实位置，音量 = 主机侧最近监听者处的衰减音量，
        /// source_distance = 该位置到主机最近监听者的距离（重投影用）。
        /// <paramref name="hostListenerDistance"/> 必须有限（拿不到监听者时调用方应放弃归属，
        /// 让全局路径照旧处理，而不是猜距离）。
        /// </summary>
        public static bool TryBuildSlotClip(in RemoteSoundQueueEntry entry, bool timeActive,
            double gameTime, Vector3 slotPosition, float hostListenerDistance,
            out RemoteSoundClipData clip, out EntryRejection rejection)
        {
            clip = default;
            rejection = Classify(in entry, timeActive, out string name, out float volume,
                out float pitch, out _);
            if (rejection != EntryRejection.None) return false;
            if (float.IsNaN(hostListenerDistance) || float.IsInfinity(hostListenerDistance) ||
                hostListenerDistance < 0f)
            {
                rejection = EntryRejection.InvalidHostDistance;
                return false;
            }
            double delay = entry.Time - gameTime;
            if (delay < 0.0) delay = 0.0;
            bool autoDelay = delay > 0.001;
            if (hostListenerDistance > RemoteSoundPolicy.HostSourceDistanceMax)
                hostListenerDistance = RemoteSoundPolicy.HostSourceDistanceMax;
            clip = new RemoteSoundClipData(name, slotPosition, volume, pitch,
                RemoteSoundPolicy.DefaultMinDistance, autoDelay,
                RemoteSoundAttenuation.SlotSource, hostListenerDistance);
            return true;
        }
    }

    /// <summary>
    /// 槽位声音的"全局路径抑制账本"（防双发）：槽位窗口捕获的条目在**下一次队列差集**里
    /// 必须被全局路径跳过；匹配同时看队列下标 + 全部 5 个字段，避免"同帧两条同名同音量"
    /// 时把没归属的那条误吞。
    ///
    /// 顺序性：队列在两次 drain 之间只追加不删除，槽位窗口按时间顺序记录下标；全局差集也按
    /// 队列顺序读出新增条目 —— 因此账本按 FIFO 只比对队头条目即可（中间夹着未归属的全局
    /// 声音时，它们匹配不上队头，原样走全局路径）。
    /// </summary>
    public sealed class HostSlotSuppressionLedger
    {
        private struct Item
        {
            public RemoteSoundQueueEntry Entry;
            public int Index;
            public int Slot;
        }

        private readonly Item[] m_items;
        private int m_head;
        private int m_count;

        public HostSlotSuppressionLedger(int capacity = 512)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            m_items = new Item[capacity];
        }

        /// <summary>槽位窗口捕获（并已作为 SlotSource 入队）的条目数。</summary>
        public long Added { get; private set; }
        /// <summary>全局差集按条目 + 下标成功抑制的次数（= 防双发的实际生效数）。</summary>
        public long Consumed { get; private set; }
        /// <summary>账本满时挤掉的最旧条目（之后的全局差集可能多发那一条，明确计数）。</summary>
        public long Overflow { get; private set; }
        /// <summary>重新同步 / 重新播种时清掉的未匹配条目（这些条目已作为槽位声音发出）。</summary>
        public long Cleared { get; private set; }
        /// <summary>全局差集发现队头下标已经过去（条目被别人消费 / 队列重建）而丢弃的条目数。</summary>
        public long Stale { get; private set; }

        public int Count => m_count;

        public bool Add(in RemoteSoundQueueEntry entry, int index, int slot)
        {
            if (m_count >= m_items.Length)
            {
                // 丢最旧：明确的容量上限，不静默
                m_head++;
                if (m_head >= m_items.Length) m_head = 0;
                m_count--;
                Overflow++;
            }
            int position = m_head + m_count;
            if (position >= m_items.Length) position -= m_items.Length;
            m_items[position] = new Item { Entry = entry, Index = index, Slot = slot };
            m_count++;
            Added++;
            return true;
        }

        /// <summary>
        /// 全局差集读到下标 <paramref name="index"/> 的新增条目：命中队头 = 该条已由槽位路径
        /// 发出，全局路径必须跳过（返回 true）。下标更旧的队头直接作废（Stale），不误吞后续声音。
        /// </summary>
        public bool TryConsume(in RemoteSoundQueueEntry entry, int index, out int slot)
        {
            slot = 0;
            while (m_count > 0)
            {
                Item head = m_items[m_head];
                if (head.Index > index) return false;
                if (head.Index < index)
                {
                    Pop();
                    Stale++;
                    continue;
                }
                if (!head.Entry.SameAs(in entry))
                {
                    // 同一下标却不是同一条（队列被替换 / 重排）：作废队头，绝不误吞。
                    Pop();
                    Stale++;
                    continue;
                }
                slot = head.Slot;
                Pop();
                Consumed++;
                return true;
            }
            return false;
        }

        public int Clear()
        {
            int dropped = m_count;
            m_head = 0;
            m_count = 0;
            Cleared += dropped;
            return dropped;
        }

        private void Pop()
        {
            m_head++;
            if (m_head >= m_items.Length) m_head = 0;
            m_count--;
        }
    }

    /// <summary>
    /// 全局主机声音中继的**纯逻辑层**（不引用 Game，P:\tmp\tsverify 直接编译同一份源码）。
    ///
    /// 数据流：
    ///   Engine/SuSubsystemAudio.cs（每次 SubsystemAudio.Update 排空前）
    ///     → Engine/RemoteActionAudioReplica.cs（队列差集 / 音量→距离）
    ///     → <see cref="HostSoundRelayScheduler"/>（每端 ≤16 条/消息、≤30 条/秒、48 m）
    ///     → Modules/RemoteAudioSyncModule.cs（编码 / 发送）
    ///     → 客户端 RemoteSoundPresentationPolicy（按本机监听者重算音量 + 距离裁剪）
    ///
    /// 无静态状态；所有上限 / 丢弃 / 限速都有计数器，绝不静默丢声音。
    /// </summary>
    public readonly struct HostSoundPeerBatch
    {
        public HostSoundPeerBatch(int clientId, RemoteSoundClipData[] sounds,
            int skippedDistance, int skippedRate)
            : this(clientId, sounds, skippedDistance, skippedRate, 0, 0)
        {
        }

        public HostSoundPeerBatch(int clientId, RemoteSoundClipData[] sounds,
            int skippedDistance, int skippedRate, int slotSkippedDistance, int slotSounds)
        {
            ClientId = clientId;
            Sounds = sounds ?? Array.Empty<RemoteSoundClipData>();
            SkippedDistance = skippedDistance;
            SkippedRate = skippedRate;
            SlotSkippedDistance = slotSkippedDistance;
            SlotSounds = slotSounds;
        }

        public int ClientId { get; }
        public RemoteSoundClipData[] Sounds { get; }
        public int SkippedDistance { get; }
        public int SkippedRate { get; }
        /// <summary>这批里因"该端到声源 &gt; 48 m"被裁剪的**槽位归属**声音数。</summary>
        public int SlotSkippedDistance { get; }
        /// <summary>这批里携带的槽位归属声音数（= <see cref="RemoteSoundAttenuation.SlotSource"/>）。</summary>
        public int SlotSounds { get; }
    }

    /// <summary>
    /// 主机全局声音的待发队列 + 每端批量 / 限速 / 距离裁剪。
    ///
    /// 语义：
    ///   · 声音按入队顺序保留，每个端有独立游标 —— 某个端被限速或距离裁剪不会影响其他端；
    ///   · 新接入的端从"当前队尾"开始（<see cref="SyncPeers"/>），**绝不补发**连接前的声音；
    ///   · 超过 <see cref="RemoteSoundPolicy.MaxPendingHostSounds"/> 时丢最旧的并计 CapDropped；
    ///   · 留到 <see cref="RemoteSoundPolicy.HostPendingRetentionMs"/> 仍没发出去的计 Expired；
    ///   · 每条消息最多 <see cref="RemoteSoundPolicy.MaxHostSoundsPerMessage"/> 条，每端每秒最多
    ///     <see cref="RemoteSoundPolicy.MaxHostSoundsPerSecondPerPeer"/> 条（按声音数，不是消息数）。
    /// </summary>
    public sealed class HostSoundRelayScheduler
    {
        private struct PendingSound
        {
            public long Sequence;
            public long EnqueuedMs;
            public RemoteSoundClipData Clip;
        }

        private sealed class PeerState
        {
            public long NextSequence;
            public long RateBlockedCountedThrough;
            public readonly RemoteSoundRateLimiter Rate =
                new RemoteSoundRateLimiter(RemoteSoundPolicy.MaxHostSoundsPerSecondPerPeer);
        }

        private readonly List<PendingSound> m_pending = new List<PendingSound>(
            RemoteSoundPolicy.MaxPendingHostSounds + 16);
        private readonly Dictionary<int, PeerState> m_peers = new Dictionary<int, PeerState>();
        private readonly HashSet<int> m_seenPeers = new HashSet<int>();
        private int m_head;
        private long m_nextSequence = 1;

        public long Enqueued { get; private set; }
        public long CapDropped { get; private set; }
        public long Expired { get; private set; }
        public long DroppedNoPeers { get; private set; }
        public long SkippedDistance { get; private set; }
        public long SkippedRate { get; private set; }
        public long SentMessages { get; private set; }
        public long SentSounds { get; private set; }
        public long Reconnects { get; private set; }

        public int PendingCount => m_pending.Count - m_head;
        public int PendingCapacity => RemoteSoundPolicy.MaxPendingHostSounds;
        public long NextSequence => m_nextSequence;

        public bool Enqueue(in RemoteSoundClipData clip, long nowMs)
        {
            if (m_pending.Count - m_head >= RemoteSoundPolicy.MaxPendingHostSounds)
            {
                DropOldest();
                CapDropped++;
            }
            var item = new PendingSound
            {
                Sequence = m_nextSequence,
                EnqueuedMs = nowMs,
                Clip = clip
            };
            m_nextSequence++;
            m_pending.Add(item);
            Enqueued++;
            return true;
        }

        private void DropOldest()
        {
            if (m_head >= m_pending.Count) return;
            long sequence = m_pending[m_head].Sequence;
            m_head++;
            AdvanceAllPeersPast(sequence);
            CompactIfNeeded();
        }

        private void AdvanceAllPeersPast(long sequence)
        {
            foreach (KeyValuePair<int, PeerState> pair in m_peers)
            {
                if (pair.Value.NextSequence <= sequence)
                    pair.Value.NextSequence = sequence + 1;
            }
        }

        private void CompactIfNeeded()
        {
            if (m_head < 256 || m_head * 2 < m_pending.Count) return;
            m_pending.RemoveRange(0, m_head);
            m_head = 0;
        }

        /// <summary>
        /// 同步在线玩家端：新端只从下一声开始（不补发历史），离开的端清掉游标。
        /// 同帧多次调用是幂等的。
        /// </summary>
        public void SyncPeers(IReadOnlyList<RemoteSoundPeer> peers)
        {
            m_seenPeers.Clear();
            int count = peers?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                int clientId = peers[i].ClientId;
                if (clientId <= 0) continue;
                if (!m_seenPeers.Add(clientId)) continue;
                if (m_peers.ContainsKey(clientId)) continue;
                m_peers.Add(clientId, new PeerState { NextSequence = m_nextSequence });
                Reconnects++;
            }
            var gone = new List<int>();
            foreach (KeyValuePair<int, PeerState> pair in m_peers)
            {
                if (!m_seenPeers.Contains(pair.Key)) gone.Add(pair.Key);
            }
            for (int i = 0; i < gone.Count; i++) m_peers.Remove(gone[i]);
        }
        private long MinimumCursor()
        {
            if (m_peers.Count == 0) return m_nextSequence;
            long minimum = long.MaxValue;
            foreach (KeyValuePair<int, PeerState> pair in m_peers)
            {
                if (pair.Value.NextSequence < minimum) minimum = pair.Value.NextSequence;
            }
            return minimum;
        }

        /// <summary>丢掉所有端都已越过 / 已超时的待发项；Expired 只计"还没被任何端处理就超时"的。</summary>
        public void Trim(long nowMs)
        {
            if (m_peers.Count == 0)
            {
                int dropped = m_pending.Count - m_head;
                if (dropped <= 0) return;
                DroppedNoPeers += dropped;
                m_head = m_pending.Count;
                CompactIfNeeded();
                return;
            }
            long minimum = MinimumCursor();
            while (m_head < m_pending.Count)
            {
                PendingSound item = m_pending[m_head];
                bool passed = item.Sequence < minimum;
                bool expired = nowMs - item.EnqueuedMs >=
                    RemoteSoundPolicy.HostPendingRetentionMs;
                if (!passed && !expired) break;
                if (expired && !passed)
                {
                    Expired++;
                    AdvanceAllPeersPast(item.Sequence);
                }
                m_head++;
            }
            CompactIfNeeded();
        }

        /// <summary>
        /// 计划一轮发送：对每个读得到位置的端生成一条批量（≤16 条、受每端 30 条/秒约束）。
        /// 距离超出的声音对该端永久跳过并计 SkippedDistance；因限速被推迟的计 SkippedRate
        /// （下一轮会补发；同一声音不重复计数）。
        /// </summary>
        public int Plan(IReadOnlyList<RemoteSoundPeer> peers, long nowMs, float earshot,
            List<HostSoundPeerBatch> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();
            Trim(nowMs);
            int count = peers?.Count ?? 0;
            float earshotSq = earshot * earshot;
            for (int i = 0; i < count; i++)
            {
                RemoteSoundPeer peer = peers[i];
                if (peer.ClientId <= 0 || !peer.HasPosition) continue;
                if (!m_peers.TryGetValue(peer.ClientId, out PeerState state)) continue;
                int budget = state.Rate.Remaining(nowMs);
                int skippedDistance = 0;
                int slotSkippedDistance = 0;
                if (budget <= 0)
                {
                    int blocked = CountNewlyBlocked(state, peer.Position, earshotSq);
                    SkippedRate += blocked;
                    output.Add(new HostSoundPeerBatch(peer.ClientId,
                        Array.Empty<RemoteSoundClipData>(), 0, blocked));
                    continue;
                }
                int cap = budget < RemoteSoundPolicy.MaxHostSoundsPerMessage
                    ? budget
                    : RemoteSoundPolicy.MaxHostSoundsPerMessage;
                var batch = new List<RemoteSoundClipData>(cap);
                int slotSounds = 0;
                for (int index = m_head; index < m_pending.Count; index++)
                {
                    PendingSound item = m_pending[index];
                    if (item.Sequence < state.NextSequence) continue;
                    if (batch.Count >= cap) break;
                    float distanceSq = Vector3.DistanceSquared(item.Clip.Position, peer.Position);
                    if (distanceSq > earshotSq)
                    {
                        state.NextSequence = item.Sequence + 1;
                        skippedDistance++;
                        if (item.Clip.Attenuation == RemoteSoundAttenuation.SlotSource)
                            slotSkippedDistance++;
                        continue;
                    }
                    batch.Add(item.Clip);
                    if (item.Clip.Attenuation == RemoteSoundAttenuation.SlotSource) slotSounds++;
                    state.NextSequence = item.Sequence + 1;
                }
                SkippedDistance += skippedDistance;
                if (batch.Count == 0)
                {
                    if (skippedDistance > 0)
                    {
                        output.Add(new HostSoundPeerBatch(peer.ClientId,
                            Array.Empty<RemoteSoundClipData>(), skippedDistance, 0,
                            slotSkippedDistance, 0));
                    }
                    continue;
                }
                if (!state.Rate.TryAccept(nowMs, batch.Count))
                {
                    // Remaining 已经保证不会走到这里；真出现说明状态不一致，明确计数而不是丢声音。
                    int blocked = CountNewlyBlocked(state, peer.Position, earshotSq);
                    SkippedRate += blocked;
                    output.Add(new HostSoundPeerBatch(peer.ClientId,
                        Array.Empty<RemoteSoundClipData>(), skippedDistance, blocked,
                        slotSkippedDistance, 0));
                    continue;
                }
                // 只有"本轮额度用完"才是限速推迟；额度还大于单条消息上限时，剩下的是
                // 正常分批（下一轮 100 ms 后发），不计 SkippedRate。
                int deferred = cap == budget
                    ? CountNewlyBlocked(state, peer.Position, earshotSq)
                    : 0;
                SkippedRate += deferred;
                SentMessages++;
                SentSounds += batch.Count;
                output.Add(new HostSoundPeerBatch(peer.ClientId, batch.ToArray(),
                    skippedDistance, deferred, slotSkippedDistance, slotSounds));
            }
            return output.Count;
        }

        /// <summary>统计本轮新被限速推迟的、对某端可达的声音数（同一声音只计一次）。</summary>
        private int CountNewlyBlocked(PeerState state, Vector3 peerPosition, float earshotSq)
        {
            int blocked = 0;
            long highest = state.RateBlockedCountedThrough;
            for (int index = m_head; index < m_pending.Count; index++)
            {
                PendingSound item = m_pending[index];
                if (item.Sequence < state.NextSequence) continue;
                if (item.Sequence <= state.RateBlockedCountedThrough) continue;
                if (Vector3.DistanceSquared(item.Clip.Position, peerPosition) > earshotSq)
                    continue;
                blocked++;
                if (item.Sequence > highest) highest = item.Sequence;
            }
            state.RateBlockedCountedThrough = highest;
            return blocked;
        }

        public void Clear()
        {
            m_pending.Clear();
            m_head = 0;
            m_peers.Clear();
            m_seenPeers.Clear();
            m_nextSequence = 1;
        }
    }

    /// <summary>
    /// 主机侧"这些声音已由别的定向事件中继过"的短时账本（防双播）。
    ///
    /// 典型来源：营火烧尽 / 烧尽表现 / 拾取 / 水花 / 爆炸 / 武器开火 / 远端动作窗口。
    /// 匹配规则：声音名（可前缀，覆盖 PlayRandomSound 的目录）+ 主机侧衰减后的音量
    /// （在注册时用同一套 CalculateVolume 算出，容差默认 1e-4）。每条登记有 count 与 TTL：
    /// 到期没被消费的计入 Expired（说明主机实际没播 / 参数不一致，明确记录，不静默）。
    /// </summary>
    public sealed class HostSoundCoveredLedger
    {
        private struct Entry
        {
            public string Name;
            public bool Prefix;
            public float Volume;
            public float VolumeTolerance;
            public int Remaining;
            public long ExpireMs;
            public bool Used;
        }

        private readonly Entry[] m_entries = new Entry[256];

        public long Added { get; private set; }
        public long Suppressed { get; private set; }
        public long ExpiredUnused { get; private set; }
        public long Overflow { get; private set; }

        public void Add(string name, bool prefix, float volume, float tolerance, int count,
            long nowMs, int ttlMs = 1500)
        {
            if (string.IsNullOrEmpty(name) || count <= 0) return;
            if (!(tolerance >= 0f)) tolerance = 0f;
            int slot = -1;
            for (int i = 0; i < m_entries.Length; i++)
            {
                if (m_entries[i].Used) continue;
                slot = i;
                break;
            }
            if (slot < 0)
            {
                // 找最早到期的一条替换；被替换的未消费条目计入 Overflow（不假装已覆盖）。
                long earliest = long.MaxValue;
                for (int i = 0; i < m_entries.Length; i++)
                {
                    if (m_entries[i].ExpireMs < earliest)
                    {
                        earliest = m_entries[i].ExpireMs;
                        slot = i;
                    }
                }
                Overflow++;
            }
            m_entries[slot] = new Entry
            {
                Name = name,
                Prefix = prefix,
                Volume = volume,
                VolumeTolerance = tolerance,
                Remaining = count,
                ExpireMs = nowMs + ttlMs,
                Used = true
            };
            Added += count;
        }

        public bool TryConsume(string name, float volume, long nowMs)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < m_entries.Length; i++)
            {
                ref Entry entry = ref m_entries[i];
                if (!entry.Used || entry.Remaining <= 0) continue;
                if (entry.ExpireMs <= nowMs) continue;
                bool nameMatch = entry.Prefix
                    ? name.StartsWith(entry.Name, StringComparison.Ordinal)
                    : string.Equals(name, entry.Name, StringComparison.Ordinal);
                if (!nameMatch) continue;
                if (Math.Abs(volume - entry.Volume) > entry.VolumeTolerance) continue;
                entry.Remaining--;
                if (entry.Remaining <= 0) entry.Used = false;
                Suppressed++;
                return true;
            }
            return false;
        }

        /// <summary>清理到期条目；返回这次过期的未消费数量（累加到 ExpiredUnused）。</summary>
        public int Expire(long nowMs)
        {
            int expired = 0;
            for (int i = 0; i < m_entries.Length; i++)
            {
                ref Entry entry = ref m_entries[i];
                if (!entry.Used) continue;
                if (entry.ExpireMs > nowMs) continue;
                expired += entry.Remaining;
                entry = default;
            }
            ExpiredUnused += expired;
            return expired;
        }

        public void Reset()
        {
            for (int i = 0; i < m_entries.Length; i++) m_entries[i] = default;
            Added = 0;
            Suppressed = 0;
            ExpiredUnused = 0;
            Overflow = 0;
        }
    }

    /// <summary>
    /// 客户端"本机刚播过这些声音"的短时账本。
    ///
    /// 规则（任务要求）：客户端自己本地播放的声音（自己的方块动作 / UI / 本地生物 / 预测表现）
    /// 绝不再被主机中继回同一端。Engine/SuSubsystemAudio 在客户端记录本机排进音频队列的每个
    /// 声音名（只记名字 + 实时时间，队列里没有位置）；收到中继时同名的第一条在窗口内被消费并
    /// 跳过，计 LocalEchoSkipped。窗口默认 400 ms，容量 64，超时未消费计入 Expired。
    /// </summary>
    public sealed class ClientLocalSoundLedger
    {
        public const int Capacity = 64;

        private struct Entry
        {
            public string Name;
            public long TimeMs;
            public bool Used;
        }

        private readonly Entry[] m_entries = new Entry[Capacity];
        private int m_next;

        public long Recorded { get; private set; }
        public long Consumed { get; private set; }
        public long Expired { get; private set; }

        public void Record(string name, long nowMs)
        {
            if (string.IsNullOrEmpty(name)) return;
            m_entries[m_next] = new Entry { Name = name, TimeMs = nowMs, Used = true };
            m_next++;
            if (m_next >= Capacity) m_next = 0;
            Recorded++;
        }

        public bool TryConsume(string name, long nowMs, long windowMs)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < Capacity; i++)
            {
                ref Entry entry = ref m_entries[i];
                if (!entry.Used) continue;
                if (nowMs - entry.TimeMs > windowMs)
                {
                    entry.Used = false;
                    Expired++;
                    continue;
                }
                if (!string.Equals(entry.Name, name, StringComparison.Ordinal)) continue;
                entry.Used = false;
                Consumed++;
                return true;
            }
            return false;
        }

        /// <summary>清掉超窗口的条目（避免长期占位）；返回清掉的条数。</summary>
        public int Expire(long nowMs, long windowMs = 400)
        {
            int expired = 0;
            for (int i = 0; i < Capacity; i++)
            {
                ref Entry entry = ref m_entries[i];
                if (!entry.Used) continue;
                if (nowMs - entry.TimeMs <= windowMs) continue;
                entry.Used = false;
                expired++;
            }
            Expired += expired;
            return expired;
        }

        public void Reset()
        {
            for (int i = 0; i < Capacity; i++) m_entries[i] = default;
            m_next = 0;
            Recorded = 0;
            Consumed = 0;
            Expired = 0;
        }
    }

    /// <summary>
    /// 引擎捕获 → 模块排空之间的有界环形缓冲（预分配，无每帧分配）。
    /// 满了丢最旧的并计 DroppedCap（明确计数，不静默）。
    /// </summary>
    public sealed class HostSoundCaptureRing
    {
        private readonly RemoteSoundClipData[] m_items;
        private int m_head;
        private int m_count;

        public HostSoundCaptureRing(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            m_items = new RemoteSoundClipData[capacity];
        }

        public int Capacity => m_items.Length;
        public int Count => m_count;
        public long Captured { get; private set; }
        public long DroppedCap { get; private set; }

        public bool TryAdd(in RemoteSoundClipData clip)
        {
            if (m_count >= m_items.Length)
            {
                m_head++;
                if (m_head >= m_items.Length) m_head = 0;
                m_count--;
                DroppedCap++;
            }
            int index = m_head + m_count;
            if (index >= m_items.Length) index -= m_items.Length;
            m_items[index] = clip;
            m_count++;
            Captured++;
            return true;
        }

        public int DrainTo(List<RemoteSoundClipData> target, int max)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (max <= 0) max = int.MaxValue;
            int drained = 0;
            while (m_count > 0 && drained < max)
            {
                target.Add(m_items[m_head]);
                m_head++;
                if (m_head >= m_items.Length) m_head = 0;
                m_count--;
                drained++;
            }
            return drained;
        }

        public void Clear()
        {
            m_head = 0;
            m_count = 0;
        }
    }
}

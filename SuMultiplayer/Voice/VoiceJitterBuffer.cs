using System;

namespace SuMultiplayer.Voice
{
    /// <summary>入队结果（显式计数，绝不静默丢弃）。</summary>
    public enum VoiceJitterResult
    {
        Queued = 0,
        Late = 1,
        Duplicate = 2,
        Overflow = 3,
        Invalid = 4
    }

    /// <summary>
    /// 每个说话者一个的小抖动缓冲（60–120 ms 目标，最多 320 ms）：
    ///   · 预灌 2 包（80 ms）后才开始播；只有 1 包时最多等 80 ms 就播（不饿死）；
    ///   · 按 sequence 排序，乱序到达也能按序播放；
    ///   · 缺包最多等 70 ms，然后跳到最小的待播包并计 GapSkipped（不无限等）；
    ///   · 已播过的 sequence 再来 = Late（计数丢弃）；
    ///   · 缓冲满（8 包）丢**最旧**的包并计数，保证延迟有界；
    ///   · 断流 500 ms 后复位（下一段说话从新的 sequence 重新开始）。
    ///
    /// <see cref="TryDequeue"/> 返回的 short[] 是**内部槽位**：调用方必须在同一次
    /// 调用里把它交给播放端（音频引擎会立即复制到 AL 缓冲），不得长期持有。
    /// 这样每个包零额外分配。
    /// </summary>
    public sealed class VoiceJitterBuffer
    {
        private readonly int m_prefill;
        private readonly int m_maxPackets;
        private readonly int m_gapWaitMs;
        private readonly int m_startWaitMs;
        private readonly int m_idleResetMs;
        private readonly int m_packetMs;

        private readonly short[][] m_pcm;
        private readonly uint[] m_seq;
        private readonly bool[] m_used;
        private readonly long[] m_arrivalMs;

        private int m_count;
        private bool m_started;
        private bool m_hasPlaySeq;
        private uint m_playSeq;
        private uint m_waitingSeq;
        private long m_waitingSince;
        private long m_nextPlayMs;
        private long m_lastArrivalMs;
        private long m_firstArrivalMs;

        public VoiceJitterBuffer(int prefillPackets = VoiceFormat.JitterPrefillPackets,
            int maxPackets = VoiceFormat.JitterMaxPackets,
            int gapWaitMs = VoiceFormat.JitterGapWaitMs,
            int startWaitMs = VoiceFormat.JitterStartWaitMs,
            int idleResetMs = VoiceFormat.JitterIdleResetMs,
            int packetMs = VoiceFormat.PacketSamples * 1000 / VoiceFormat.SampleRate)
        {
            if (prefillPackets < 1) throw new ArgumentOutOfRangeException(nameof(prefillPackets));
            if (maxPackets < prefillPackets) throw new ArgumentOutOfRangeException(nameof(maxPackets));
            if (gapWaitMs < 0) throw new ArgumentOutOfRangeException(nameof(gapWaitMs));
            if (startWaitMs < 0) throw new ArgumentOutOfRangeException(nameof(startWaitMs));
            if (idleResetMs < 0) throw new ArgumentOutOfRangeException(nameof(idleResetMs));
            if (packetMs <= 0) throw new ArgumentOutOfRangeException(nameof(packetMs));
            m_prefill = prefillPackets;
            m_maxPackets = maxPackets;
            m_gapWaitMs = gapWaitMs;
            m_startWaitMs = startWaitMs;
            m_idleResetMs = idleResetMs;
            m_packetMs = packetMs;
            m_pcm = new short[maxPackets][];
            for (int i = 0; i < maxPackets; i++) m_pcm[i] = new short[VoiceFormat.PacketSamples];
            m_seq = new uint[maxPackets];
            m_used = new bool[maxPackets];
            m_arrivalMs = new long[maxPackets];
        }

        public int Count => m_count;

        public bool IsStarted => m_started;

        public uint PlaySequence => m_playSeq;

        public long LateDropped { get; private set; }

        public long GapSkipped { get; private set; }

        public long OverflowDropped { get; private set; }

        public long DuplicateDropped { get; private set; }

        public long IdleResets { get; private set; }

        public VoiceJitterResult Enqueue(uint sequence, short[] pcm, int offset, int count,
            long nowMs)
        {
            if (sequence == 0 || pcm == null || count != VoiceFormat.PacketSamples ||
                offset < 0 || offset + count > pcm.Length)
                return VoiceJitterResult.Invalid;

            if (m_started && m_hasPlaySeq && Before(sequence, m_playSeq))
            {
                LateDropped++;
                return VoiceJitterResult.Late;
            }
            if (FindSlot(sequence) >= 0)
            {
                DuplicateDropped++;
                return VoiceJitterResult.Duplicate;
            }

            int slot = FindFreeSlot();
            if (slot < 0)
            {
                slot = FindLowestSequenceSlot();
                if (slot < 0) return VoiceJitterResult.Overflow;
                uint dropped = m_seq[slot];
                m_used[slot] = false;
                m_count--;
                OverflowDropped++;
                if (m_started && m_hasPlaySeq && dropped == m_playSeq) m_playSeq++;
                slot = FindFreeSlot();
                if (slot < 0) return VoiceJitterResult.Overflow;
            }

            Array.Copy(pcm, offset, m_pcm[slot], 0, count);
            m_seq[slot] = sequence;
            m_arrivalMs[slot] = nowMs;
            m_used[slot] = true;
            m_count++;
            if (m_count == 1) m_firstArrivalMs = nowMs;
            m_lastArrivalMs = nowMs;
            return VoiceJitterResult.Queued;
        }

        /// <summary>说话开始（flags 的 start 位）：丢掉上一段残留，按新 sequence 重新开始。</summary>
        public void Restart()
        {
            Clear();
            m_started = false;
            m_hasPlaySeq = false;
            m_playSeq = 0;
            m_waitingSince = 0;
        }

        public void Reset() => Restart();

        /// <summary>
        /// 到播放时点就取出一个包。返回 false = 现在不该播（预灌不够 / 还没到下一包时间 /
        /// 缺包等待中 / 没有数据）。
        /// </summary>
        public bool TryDequeue(long nowMs, out short[] pcm, out int samples)
        {
            pcm = null;
            samples = 0;
            if (m_count == 0)
            {
                MaybeIdleReset(nowMs);
                return false;
            }
            if (m_started && nowMs - m_lastArrivalMs >= m_idleResetMs)
            {
                // 断流超时：先复位，再让下一段说话重新预灌（不要拿旧 sequence 播）。
                IdleResets++;
                Reset();
                return false;
            }

            if (!m_started)
            {
                if (m_count < m_prefill && nowMs - m_firstArrivalMs < m_startWaitMs) return false;
                m_started = true;
                m_hasPlaySeq = true;
                m_playSeq = MinSequence();
                m_waitingSince = 0;
                m_nextPlayMs = nowMs;
            }

            if (nowMs < m_nextPlayMs) return false;

            int slot = FindSlot(m_playSeq);
            if (slot < 0)
            {
                uint minimum = MinSequence();
                if (Before(minimum, m_playSeq))
                {
                    // 理论上不会发生（早于播放序列的包已在 Enqueue 里判 late）；这里守卫。
                    m_playSeq = minimum;
                    slot = FindSlot(m_playSeq);
                }
                if (slot < 0)
                {
                    if (m_waitingSince == 0 || m_waitingSeq != m_playSeq)
                    {
                        m_waitingSeq = m_playSeq;
                        m_waitingSince = nowMs;
                    }
                    if (nowMs - m_waitingSince < m_gapWaitMs) return false;
                    uint skipped = minimum - m_playSeq;
                    GapSkipped += skipped;
                    m_playSeq = minimum;
                    m_waitingSince = 0;
                    slot = FindSlot(m_playSeq);
                    if (slot < 0) return false;
                }
            }

            m_waitingSince = 0;
            pcm = m_pcm[slot];
            samples = VoiceFormat.PacketSamples;
            m_used[slot] = false;
            m_count--;
            m_playSeq++;
            m_nextPlayMs = nowMs + m_packetMs;
            return true;
        }

        private void MaybeIdleReset(long nowMs)
        {
            if (!m_started) return;
            if (nowMs - m_lastArrivalMs < m_idleResetMs) return;
            IdleResets++;
            Reset();
        }

        private void Clear()
        {
            for (int i = 0; i < m_used.Length; i++) m_used[i] = false;
            m_count = 0;
            m_waitingSince = 0;
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < m_used.Length; i++) if (!m_used[i]) return i;
            return -1;
        }

        private int FindSlot(uint sequence)
        {
            for (int i = 0; i < m_used.Length; i++)
                if (m_used[i] && m_seq[i] == sequence) return i;
            return -1;
        }

        private int FindLowestSequenceSlot()
        {
            int best = -1;
            for (int i = 0; i < m_used.Length; i++)
            {
                if (!m_used[i]) continue;
                if (best < 0 || Before(m_seq[i], m_seq[best])) best = i;
            }
            return best;
        }

        private uint MinSequence()
        {
            int slot = FindLowestSequenceSlot();
            return slot < 0 ? 0u : m_seq[slot];
        }

        /// <summary>无符号回绕安全的 "a 在 b 之前"。</summary>
        private static bool Before(uint a, uint b) => (int)(a - b) < 0;
    }
}

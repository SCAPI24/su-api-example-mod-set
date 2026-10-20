using System;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 把 20 ms 帧拼成 40 ms 包（2 帧）。最后一个不满的包在说话结束时用静音补齐
    /// （由 <see cref="FlushPadded"/> 明确返回补齐前的真实样本数），不会把半包丢掉。
    /// </summary>
    public sealed class VoicePacketizer
    {
        private readonly short[] m_buffer = new short[VoiceFormat.PacketSamples];
        private int m_fill;

        public int Fill => m_fill;

        public bool IsEmpty => m_fill == 0;

        public bool IsFull => m_fill >= VoiceFormat.PacketSamples;

        public short[] Buffer => m_buffer;

        public void Reset() => m_fill = 0;

        /// <summary>
        /// 追加一帧（必须 ≤ 剩余空间）。返回 true = 已满，可以编码发送；
        /// 调用方负责在发送后调用 <see cref="Reset"/>。
        /// </summary>
        public bool Push(short[] pcm, int offset, int count)
        {
            if (pcm == null) throw new ArgumentNullException(nameof(pcm));
            if (count <= 0 || offset < 0 || offset + count > pcm.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (m_fill + count > VoiceFormat.PacketSamples)
                throw new InvalidOperationException(
                    "VoicePacketizer 溢出：fill=" + m_fill + " count=" + count);
            Array.Copy(pcm, offset, m_buffer, m_fill, count);
            m_fill += count;
            return m_fill >= VoiceFormat.PacketSamples;
        }

        /// <summary>
        /// 用 0 补齐到 40 ms 并返回补齐前的有效样本数；没有数据 = 0（调用方不发包）。
        /// </summary>
        public int FlushPadded()
        {
            int valid = m_fill;
            if (valid <= 0) return 0;
            for (int i = valid; i < VoiceFormat.PacketSamples; i++) m_buffer[i] = 0;
            m_fill = VoiceFormat.PacketSamples;
            return valid;
        }
    }
}

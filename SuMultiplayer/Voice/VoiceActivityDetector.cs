using System;

namespace SuMultiplayer.Voice
{
    /// <summary>一帧（20 ms）的 VAD 结论。</summary>
    public enum VoiceFrameClass
    {
        /// <summary>静音（不计入发送）。</summary>
        Silence = 0,

        /// <summary>语音。</summary>
        Speech = 1,

        /// <summary>挂起期（说话刚结束的 200–400 ms），仍然发送。</summary>
        Hangover = 2
    }

    /// <summary>
    /// 能量 + 过零率 VAD，带挂起。规则（全部可判定、无随机）：
    ///   · 起始：mean(|sample|) ≥ OpenThreshold；
    ///   · 维持：已在上一个语音帧时，mean(|sample|) ≥ ContinueThreshold 且
    ///     过零率 ≤ MaxZeroCrossing（浊音的低频成分；避免"高频底噪"把挂起拖长）；
    ///   · 语音结束后继续发 HangoverFrames 帧（默认 13 × 20 ms = 260 ms）。
    ///
    /// 只看当前帧，不跨帧平滑 —— 自检可以逐帧断言，不需要模拟时钟。
    /// </summary>
    public sealed class VoiceActivityDetector
    {
        private readonly int m_openThreshold;
        private readonly int m_continueThreshold;
        private readonly int m_hangoverFrames;
        private readonly double m_maxZeroCrossing;
        private int m_hangoverLeft;
        private bool m_talking;

        public VoiceActivityDetector(int openThreshold = VoiceFormat.VadOpenThreshold,
            int continueThreshold = VoiceFormat.VadContinueThreshold,
            int hangoverFrames = VoiceFormat.VadHangoverFrames,
            double maxZeroCrossing = VoiceFormat.VadMaxZeroCrossing)
        {
            if (openThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(openThreshold));
            if (continueThreshold < 0 || continueThreshold > openThreshold)
                throw new ArgumentOutOfRangeException(nameof(continueThreshold));
            if (hangoverFrames < 0) throw new ArgumentOutOfRangeException(nameof(hangoverFrames));
            m_openThreshold = openThreshold;
            m_continueThreshold = continueThreshold;
            m_hangoverFrames = hangoverFrames;
            m_maxZeroCrossing = maxZeroCrossing;
        }

        public bool IsTalking => m_talking;

        public int HangoverLeft => m_hangoverLeft;

        public long SpeechFrames { get; private set; }

        public long HangoverFrames { get; private set; }

        public long SilenceFrames { get; private set; }

        /// <summary>最近一帧的 mean(|sample|)，诊断 / HUD 电平用。</summary>
        public int LastLevel { get; private set; }

        public void Reset()
        {
            m_hangoverLeft = 0;
            m_talking = false;
        }

        public VoiceFrameClass Classify(short[] pcm, int offset, int count)
        {
            if (pcm == null) throw new ArgumentNullException(nameof(pcm));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset < 0 || offset + count > pcm.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            long sum = 0;
            int crossings = 0;
            int previous = pcm[offset];
            for (int i = 0; i < count; i++)
            {
                int sample = pcm[offset + i];
                int magnitude = sample < 0 ? -sample : sample;
                sum += magnitude;
                if ((sample >= 0) != (previous >= 0)) crossings++;
                previous = sample;
            }
            int meanAbs = (int)(sum / count);
            LastLevel = meanAbs;
            double zcr = crossings / (double)count;

            bool voiced = meanAbs >= m_openThreshold;
            if (!voiced && m_talking)
                voiced = meanAbs >= m_continueThreshold && zcr <= m_maxZeroCrossing;

            if (voiced)
            {
                m_talking = true;
                m_hangoverLeft = m_hangoverFrames;
                SpeechFrames++;
                return VoiceFrameClass.Speech;
            }

            if (m_talking && m_hangoverLeft > 0)
            {
                m_hangoverLeft--;
                if (m_hangoverLeft > 0)
                {
                    HangoverFrames++;
                    return VoiceFrameClass.Hangover;
                }
                // 最后一帧挂起：仍然发送，同时结束说话状态。
                m_talking = false;
                HangoverFrames++;
                return VoiceFrameClass.Hangover;
            }

            m_talking = false;
            SilenceFrames++;
            return VoiceFrameClass.Silence;
        }
    }
}

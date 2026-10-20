using System;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 1-bit CVSD（Continuously Variable Slope Delta modulation）—— **ultra** 档：
    /// 每样本 1 bit，8 kHz → 8 kbit/s 净荷；每包 320 bit = 40 B + 3 B 自包含头 = 43 B。
    ///
    /// 为什么用 CVSD 而不是 1-bit ADPCM：1 bit 没有幅度分级，只有"比预测值大/小"，
    /// 必须靠**斜率自适应**（音节压扩）才能跟上语音的幅度变化。CVSD 就是这套：
    ///   · 连续 <see cref="AdaptationRun"/> 个 bit 相同 → 判定斜率不够，step 增长
    ///     （step += step>>3 + 1，约 +12.5%/样本）；
    ///   · 否则 step 衰减（step -= step>>5，约 -3%/样本）；
    ///   · step 夹在 [16, 4080]，避免静音时彻底失步或过冲。
    /// 这是经典的 8 kbit/s 军用/蓝牙语音方案：噪声明显，但可懂。
    ///
    /// 自包含：块头 3 B = int16 初始预测值 + uint8 step/16（0 = 16）。
    /// 编码器与解码器用**同一个** <see cref="Advance"/> 更新状态（逐样本一致），
    /// 因此丢包 / 中途加入不会让解码器永久错位。无静态可变状态，AOT 安全。
    /// </summary>
    public static class VoiceCvsd
    {
        /// <summary>块头：int16 predictor + uint8 step code（与 ADPCM 档统一 3 字节）。</summary>
        public const int HeaderBytes = VoiceFormat.AdpcmHeaderBytes;

        /// <summary>每包 bit 数 = 样本数（1 bit/样本）。</summary>
        public const int PayloadBytes = HeaderBytes + VoiceFormat.PacketSamples / 8;   // 43

        /// <summary>初始 step（块头只在包内自包含；每包都从同一个初值开始）。</summary>
        public const int InitialStep = 256;

        public const int MinStep = 16;
        public const int MaxStep = 4080;

        /// <summary>连续多少个相同 bit 判定为斜率饱和（经典 CVSD 用 3–4）。</summary>
        public const int AdaptationRun = 4;

        /// <summary>step 增长 / 衰减的移位（+12.5% / -3.1%）。</summary>
        public const int GrowShift = 3;
        public const int DecayShift = 5;

        /// <summary>step code = step / <see cref="StepUnit"/>；0 表示 16（下限）。</summary>
        public const int StepUnit = 16;

        /// <summary>
        /// 编码恰好 <see cref="VoiceFormat.PacketSamples"/> 个样本。
        /// 失败原因显式返回（与 ADPCM 同一套 reason 前缀），绝不静默截断。
        /// </summary>
        public static bool TryEncode(short[] pcm, int offset, int count, byte[] target,
            int targetOffset, out int written, out string reason)
        {
            written = 0;
            reason = null;
            if (pcm == null) { reason = "voice-encode-pcm-null"; return false; }
            if (count != VoiceFormat.PacketSamples)
            {
                reason = "voice-encode-size-" + count;
                return false;
            }
            if (offset < 0 || offset + count > pcm.Length)
            {
                reason = "voice-encode-offset-" + offset + "+" + count + "/" + pcm.Length;
                return false;
            }
            if (target == null || targetOffset < 0 ||
                targetOffset + PayloadBytes > target.Length)
            {
                reason = "voice-encode-target";
                return false;
            }

            int predictor = pcm[offset];
            int step = InitialStep;
            int stepCode = StepCode(step);
            target[targetOffset] = (byte)(predictor & 0xFF);
            target[targetOffset + 1] = (byte)((predictor >> 8) & 0xFF);
            target[targetOffset + 2] = (byte)stepCode;

            // 只写 1 位；先清零载荷，避免上一包的残留位混进来。
            Array.Clear(target, targetOffset + HeaderBytes, PayloadBytes - HeaderBytes);
            int bitPos = 0;
            int run = 0;
            int lastBit = -1;
            for (int i = 0; i < count; i++)
            {
                int bit = pcm[offset + i] > predictor ? 1 : 0;
                if (bit != 0)
                    target[targetOffset + HeaderBytes + (bitPos >> 3)] |=
                        (byte)(1 << (bitPos & 7));
                bitPos++;
                Advance(bit, ref predictor, ref step, ref run, ref lastBit);
            }
            written = PayloadBytes;
            return true;
        }

        /// <summary>
        /// 解码一个自包含 CVSD 块：严格要求 length == <see cref="PayloadBytes"/>，
        /// 且 count == <see cref="VoiceFormat.PacketSamples"/>；否则整包拒绝。
        /// </summary>
        public static bool TryDecode(byte[] data, int offset, int length, short[] target,
            int targetOffset, int count, out string reason)
        {
            reason = null;
            if (data == null) { reason = "voice-decode-data-null"; return false; }
            if (length != PayloadBytes)
            {
                reason = "voice-decode-size-" + length + "/" + PayloadBytes;
                return false;
            }
            if (count != VoiceFormat.PacketSamples)
            {
                reason = "voice-decode-count-" + count;
                return false;
            }
            if (offset < 0 || offset + length > data.Length)
            {
                reason = "voice-decode-offset-" + offset + "+" + length + "/" + data.Length;
                return false;
            }
            if (target == null || targetOffset < 0 || targetOffset + count > target.Length)
            {
                reason = "voice-decode-target";
                return false;
            }

            int predictor = data[offset] | (data[offset + 1] << 8);
            if (predictor > 32767) predictor -= 65536;
            int step = StepFromCode(data[offset + 2]);
            int run = 0;
            int lastBit = -1;
            for (int i = 0; i < count; i++)
            {
                // 先输出当前预测值再按 bit 推进：i=0 就是块头 predictor（逐位相等），
                // 之后每个值都是编码端拿来与样本 i 比较的那个预测值。
                target[targetOffset + i] = (short)predictor;
                int bit = (data[offset + HeaderBytes + (i >> 3)] >> (i & 7)) & 1;
                Advance(bit, ref predictor, ref step, ref run, ref lastBit);
            }
            return true;
        }

        /// <summary>块头里的 step code（四舍五入到 16 的倍数；0 表示下限 16）。</summary>
        public static int StepCode(int step)
        {
            int code = (step + StepUnit / 2) / StepUnit;
            if (code < 1) code = 1;
            else if (code > 255) code = 255;
            return code;
        }

        /// <summary>块头 code → step（0 / 越界都夹到下限，绝不产生 0 步长）。</summary>
        public static int StepFromCode(int code)
        {
            if (code < 1) return MinStep;
            if (code > 255) code = 255;
            int step = code * StepUnit;
            if (step < MinStep) return MinStep;
            if (step > MaxStep) return MaxStep;
            return step;
        }

        /// <summary>
        /// 编码器 / 解码器共用的逐样本状态推进（**必须完全一致**）：
        /// 先按 bit 调整预测值，再按连续相同 bit 数做音节压扩。
        /// </summary>
        public static void Advance(int bit, ref int predictor, ref int step, ref int run,
            ref int lastBit)
        {
            predictor += bit != 0 ? step : -step;
            if (predictor > 32767) predictor = 32767;
            else if (predictor < -32768) predictor = -32768;

            if (bit == lastBit)
            {
                if (run < AdaptationRun) run++;
            }
            else
            {
                run = 1;
                lastBit = bit;
            }

            if (run >= AdaptationRun)
                step += (step >> GrowShift) + 1;
            else
                step -= step >> DecayShift;

            if (step < MinStep) step = MinStep;
            else if (step > MaxStep) step = MaxStep;
        }
    }
}

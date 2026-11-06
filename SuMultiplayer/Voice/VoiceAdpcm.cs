using System;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// N-bit IMA/DVI ADPCM（N = 4 / 3 / 2，对应 high / standard / low）。每块 =
    /// 2 字节 predictor + 1 字节 step index + 320 × N / 8 字节量化数据，
    /// 恰好 320 个样本（40 ms @ 8 kHz）：high 163 B / standard 123 B / low 83 B。
    ///
    /// 为什么每包独立（不复用上一包的 predictor/index）：
    ///   · 丢包 / 乱序 / 中途加入都不会让解码器状态永久错位（错位会变成噪声）；
    ///   · 代价只有每 40 ms 3 字节（75 B/s），换来的是可验证的鲁棒性。
    ///
    /// **2/3-bit 变体的量化表是从 4-bit IMA 公式严格化简出来的**（不是抄一张
    /// 来路不明的表）：
    ///   · step 表沿用 89 项 IMA 表；
    ///   · 重构电平：diff = step / 2^(N-1) + Σ bit_i × step / 2^(N-2-i)
    ///     （N=4 时就是标准 IMA 的 step>>3 + bit×step/…，逐位相等）；
    ///   · index 调整：幅度 v 的码在 v < 2^(N-2) 时 -1，否则 +2×(v-2^(N-2)+1)。
    ///     N=4 → {-1,-1,-1,-1,2,4,6,8}（标准表）；N=3 → {-1,-1,2,4}；
    ///     N=2 → {-1,2}。
    ///   · 编码用同一套阈值做逐次逼近，并用**同一个** code 更新状态，
    ///     保证编码器与解码器逐样本一致。
    ///
    /// 无静态可变状态；全部有界数组访问，AOT 安全。
    /// </summary>
    public static class VoiceAdpcm
    {
        public const int MaxStepIndex = 88;

        /// <summary>
        /// 估算起始步长索引时探测的样本数（10 ms @ 8 kHz）。
        /// 起始瞬态就在包开头，取 8 个样本（一阶差分）足以判断本包的量级；
        /// 窗口再长会把后续安静段平均进来，反而低估真正需要的起始步长。
        /// </summary>
        public const int StartStepProbeSamples = 8;

        private static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
            279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166,
            1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
            4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289,
            16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        };

        /// <summary>step 表（只读，自检用来核对首尾 / 单调性）。</summary>
        public static int StepForIndex(int index) => StepTable[index];

        /// <summary>
        /// 码字的 index 增量：幅度 v = code &amp; (2^(N-1)-1)，
        /// v &lt; 2^(N-2) → -1，否则 +2×(v-2^(N-2)+1)。符号位不影响 index。
        /// </summary>
        public static int IndexDelta(int code, int bits)
        {
            if (bits < 2 || bits > 4) throw new ArgumentOutOfRangeException(nameof(bits));
            int half = 1 << (bits - 2);
            int magnitude = code & ((1 << (bits - 1)) - 1);
            return magnitude < half ? -1 : 2 * (magnitude - half + 1);
        }

        /// <summary>
        /// 编码恰好 <see cref="VoiceFormat.PacketSamples"/> 个样本到 target。
        /// 失败原因显式返回（voice-encode-size-N / voice-encode-target），绝不静默截断。
        /// </summary>
        public static bool TryEncode(VoiceQuality quality, short[] pcm, int offset, int count,
            byte[] target, int targetOffset, out int written, out string reason)
        {
            written = 0;
            reason = null;
            int bits = VoiceProfiles.BitsPerSample(quality);
            if (bits == 0)
            {
                reason = "voice-quality-" + (int)quality;
                return false;
            }
            int encodedBytes = VoiceFormat.EncodedBytes(quality);
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
            if (target == null || targetOffset < 0 || targetOffset + encodedBytes > target.Length)
            {
                reason = "voice-encode-target";
                return false;
            }

            int predictor = pcm[offset];
            // 起始步长索引按**本包自身**的斜率估算，不再硬编码 0。
            //
            // 为什么（2026-11-06 修）：index=0 意味着每包起始步长恒为 StepTable[0]=7
            // （最小档）。大幅度信号（大声说话 / 唱歌）在每包开头都要用几十个样本把
            // 步长从 7 爬升到匹配量级，那段爬升期量化严重不足 → 每包（40 ms，即每秒
            // 25 次）一次短暂的阶梯失真。实机听感就是持续发声时的"噗噗"声，且与网络
            // 无关（实测 voiceLateDropped=0、voiceRecv≈voicePlayed）。
            //
            // 仍然每包独立（不复用上一包的 predictor / index），所以丢包、乱序、
            // 中途加入都不会让解码器状态错位 —— 鲁棒性完全保留；只是把"起始 points
            // 从最小步长"改成"起始点匹配本包信号幅度"。
            // 解码端不用改：它本来就从包头第 3 字节读取 index（TryDecode:data[offset+2]），
            // 线格式与协议完全不变。
            int index = EstimateStartStepIndex(pcm, offset, count);
            target[targetOffset] = (byte)(predictor & 0xFF);
            target[targetOffset + 1] = (byte)((predictor >> 8) & 0xFF);
            target[targetOffset + 2] = (byte)index;

            int outPos = targetOffset + VoiceFormat.AdpcmHeaderBytes;
            // 低位打包只写值为 1 的位：必须先把载荷清零，否则上一包残留位会被
            // 当成本包数据（全静音包会带上一个音的尾巴）。
            Array.Clear(target, outPos, encodedBytes - VoiceFormat.AdpcmHeaderBytes);
            int bitPos = 0;
            for (int sample = 1; sample < count; sample++)
            {
                int code = EncodeSample(pcm[offset + sample], bits, ref predictor, ref index);
                WriteBits(target, outPos, bitPos, code, bits);
                bitPos += bits;
            }

            written = outPos + (bitPos + 7) / 8 - targetOffset;
            if (written != encodedBytes)
            {
                reason = "voice-encode-written-" + written + "/" + encodedBytes;
                written = 0;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 解码一个自包含块。严格要求 length == 该档位的载荷字节数，
        /// 且 count == <see cref="VoiceFormat.PacketSamples"/>；否则整包拒绝（绝不部分应用）。
        /// </summary>
        public static bool TryDecode(VoiceQuality quality, byte[] data, int offset, int length,
            short[] target, int targetOffset, int count, out string reason)
        {
            reason = null;
            int bits = VoiceProfiles.BitsPerSample(quality);
            if (bits == 0)
            {
                reason = "voice-quality-" + (int)quality;
                return false;
            }
            if (data == null) { reason = "voice-decode-data-null"; return false; }
            if (length != VoiceFormat.EncodedBytes(quality))
            {
                reason = "voice-decode-size-" + length + "/" + VoiceFormat.EncodedBytes(quality);
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
            int index = data[offset + 2];
            if (index > MaxStepIndex) index = MaxStepIndex;
            target[targetOffset] = (short)predictor;

            int inPos = offset + VoiceFormat.AdpcmHeaderBytes;
            int bitPos = 0;
            for (int i = 1; i < count; i++)
            {
                int code = ReadBits(data, inPos, bitPos, bits);
                bitPos += bits;
                predictor = DecodeDelta(code, bits, predictor, ref index);
                target[targetOffset + i] = (short)predictor;
            }
            return true;
        }

        /// <summary>
        /// 按本包起始若干样本的**平均绝对一阶差分**，在 step 表里选一个合适的起始索引。
        ///
        /// 为什么要它（见 <see cref="TryEncode"/> 里调用处的注释）：每包独立是为了丢包鲁棒，
        /// 但 index 从 0 起意味着起始步长恒为最小值 7；大幅度信号在每包开头要花几十个
        /// 样本把步长爬升到匹配量级，那段爬升就是每秒 25 次的可听见失真。
        /// 用本包自身斜率选起点，既保留每包独立，又消掉爬升期。
        ///
        /// 为什么用一阶差分而不是幅度均值：ADPCM 的步长匹配的是**样本间变化量**，不是
        /// 绝对电平（大音量的低频正弦差分反而小）。差分直接对上一阶量化台阶。
        ///
        /// 为什么只探前 <see cref="StartStepProbeSamples"/> 个样本：起始瞬态就在包开头，
        /// 窗口取太长会把后续的安静段平均进来，反而低估真正需要的起始步长。
        /// 越界一律钳到合法索引（绝不让上层拿到非法值）。
        /// </summary>
        private static int EstimateStartStepIndex(short[] pcm, int offset, int count)
        {
            int samples = Math.Min(StartStepProbeSamples, count - 1);
            if (samples <= 0) return 0;
            long sum = 0;
            for (int i = 1; i <= samples; i++)
                sum += Math.Abs((int)pcm[offset + i] - pcm[offset + i - 1]);
            int averageDelta = (int)(sum / samples);
            for (int i = 0; i < StepTable.Length; i++)
                if (StepTable[i] >= averageDelta) return i;
            return MaxStepIndex;
        }

        private static int EncodeSample(int sample, int bits, ref int predictor, ref int index)
        {
            int step = StepTable[index];
            int diff = sample - predictor;
            int code = 0;
            if (diff < 0)
            {
                code = 1 << (bits - 1);
                diff = -diff;
            }
            int threshold = step;
            for (int i = bits - 2; i >= 0; i--)
            {
                if (diff >= threshold)
                {
                    code |= 1 << i;
                    diff -= threshold;
                }
                threshold >>= 1;
            }

            // 用**同一个** code 更新状态：编码器与解码器必须逐样本一致。
            predictor = DecodeDelta(code, bits, predictor, ref index);
            return code;
        }

        private static int DecodeDelta(int code, int bits, int predictor, ref int index)
        {
            int step = StepTable[index];
            int diff = step >> (bits - 1);
            for (int i = 0; i <= bits - 2; i++)
            {
                if ((code & (1 << i)) != 0) diff += step >> (bits - 2 - i);
            }
            if ((code & (1 << (bits - 1))) != 0) diff = -diff;
            predictor += diff;
            if (predictor > 32767) predictor = 32767;
            else if (predictor < -32768) predictor = -32768;
            index += IndexDelta(code, bits);
            if (index < 0) index = 0;
            else if (index > MaxStepIndex) index = MaxStepIndex;
            return predictor;
        }

        /// <summary>低位先写：4-bit 与原有实现逐字节一致（低 nibble 在前）。</summary>
        private static void WriteBits(byte[] target, int byteOffset, int bitPosition, int value,
            int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                if ((value & (1 << i)) == 0) continue;
                int pos = bitPosition + i;
                target[byteOffset + (pos >> 3)] |= (byte)(1 << (pos & 7));
            }
        }

        private static int ReadBits(byte[] data, int byteOffset, int bitPosition, int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++)
            {
                int pos = bitPosition + i;
                if (((data[byteOffset + (pos >> 3)] >> (pos & 7)) & 1) != 0)
                    value |= 1 << i;
            }
            return value;
        }
    }
}

using System;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 线格式与缓冲参数的**唯一来源**。单帧 20 ms、每包 2 帧（40 ms）、
    /// 8 kHz 单声道、每包一个自包含块（3 B 块头 + 量化数据）。
    ///
    /// 量化位数由 <see cref="VoiceQuality"/> 决定（4/3/2/1 bit）：
    ///   high     163 B / 40 ms   标准 4-bit IMA ADPCM（原档位）
    ///   standard 123 B / 40 ms   3-bit IMA ADPCM
    ///   low       83 B / 40 ms   2-bit IMA ADPCM（**默认**）
    ///   ultra     43 B / 40 ms   1-bit CVSD
    /// 每包自带 3 字节块头（int16 predictor + uint8 index/step），丢包 / 乱序 /
    /// 中途加入都不会让解码器状态永久错位；代价只有每 40 ms 3 字节。
    ///
    /// 带宽（25 包/秒，仅说话时）：Low 净荷 2075 B/s、含 16 B TCP 帧头 +
    /// protobuf 字段约 2.6 KB/s（≈21 kbit/s），约为 High 的 55%。
    /// 引擎没有随包发布任何可跨平台调用的原生编解码器（见 doc/VOICE-CHAT.md），
    /// 托管 ADPCM / CVSD 是唯一 AOT 安全、无新增原生库的路径。
    /// </summary>
    public static class VoiceFormat
    {
        public const int SampleRate = 8000;
        public const int FrameSamples = 160;       // 20 ms
        public const int FramesPerPacket = 2;      // 40 ms
        public const int PacketSamples = 320;      // 40 ms
        public const int PacketMs = 40;
        public const int AdpcmHeaderBytes = 3;     // int16 predictor + uint8 index/step

        /// <summary>最大的自包含块（= high 档 163 B）；编码 / 解码缓冲按它分配。</summary>
        public const int MaxEncodedBytes = 163;

        /// <summary>指定档位每包载荷字节数（3 + 320 × bits / 8）。</summary>
        public static int EncodedBytes(VoiceQuality quality) =>
            VoiceProfiles.PayloadBytes(quality);

        /// <summary>AL_FORMAT_MONO16 (0x1101) —— OpenAL 的常量，不用引用 OpenTK。</summary>
        public const int AlFormatMono16 = 0x1101;

        /// <summary>TCP 帧头（Net/TerrainNet.cs:TerrainFrame.HeaderBytes）。计入带宽口径。</summary>
        public const int WireHeaderBytes = 16;

        /// <summary>Jitter 缓冲默认预灌 2 包（80 ms）：首包不等待，抖动 1 包内可吸收。</summary>
        public const int JitterPrefillPackets = 2;
        public const int JitterMaxPackets = 8;     // 320 ms 上限
        public const int JitterGapWaitMs = 70;     // 缺包最多等 70 ms 再跳过
        public const int JitterStartWaitMs = 80;   // 只有 1 包时最多等 80 ms 就开始播
        public const int JitterIdleResetMs = 500;  // 断流 500 ms 复位

        /// <summary>VAD：起始门限 / 维持门限 / 挂起 160 ms（8 帧，可由配置覆盖）。</summary>
        public const int VadOpenThreshold = 350;     // |sample| 均值（16-bit）
        public const int VadContinueThreshold = 180;
        public const double VadMaxZeroCrossing = 0.30;
        /// <summary>默认挂起 160 ms（8 × 20 ms）。旧默认 260 ms 会多发 5 个空闲包/次。</summary>
        public const int VadHangoverFrames = 8;      // 160 ms
        public const int VadFrameMs = 20;
        public const int VadHangoverDefaultMs = VadHangoverFrames * VadFrameMs;   // 160
        public const int VadHangoverMaxMs = 2000;    // 配置上限（100 帧）
        public const int VadPreRollFrames = 2;       // 40 ms 预卷，首音节不被切

        /// <summary>客户端发包上限（防御性）：正常 25 包/秒。</summary>
        public const int MaxSendPacketsPerSecond = 60;

        public const int SummaryLogMs = 10000;
        public const int ProblemLogMs = 10000;

        public static int SamplesToMs(int samples) => samples * 1000 / SampleRate;

        /// <summary>配置毫秒 → VAD 帧数（0..100；向上取整，保证不会比配置更短）。</summary>
        public static int HangoverMsToFrames(int hangoverMs)
        {
            if (hangoverMs <= 0) return 0;
            if (hangoverMs > VadHangoverMaxMs) hangoverMs = VadHangoverMaxMs;
            return (hangoverMs + VadFrameMs - 1) / VadFrameMs;
        }
    }
}

using System;
using System.Globalization;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 语音质量档位（**线上 id 就是这个枚举值**，追加式协议：0 永远表示"未声明"）。
    ///   · High     = 4-bit IMA/DVI ADPCM（原有档位，兼容旧实现）
    ///   · Standard = 3-bit IMA ADPCM
    ///   · Low      = 2-bit IMA ADPCM（**默认**：会话可懂，带宽约为 High 的一半）
    ///   · Ultra    = 1-bit CVSD（噪声大，最后手段）
    ///
    /// 为什么把档位做成显式枚举而不是 bool / int：
    ///   · 收端必须能判断"这个包用哪种编码"，否则只能靠猜 → 静默乱码；
    ///   · 未知值必须**明确拒绝**（<see cref="VoiceProfiles.TryFromWireId"/> false），
    ///     绝不当成默认档位解码。
    /// </summary>
    public enum VoiceQuality
    {
        /// <summary>未声明 / 非法。任何消息里出现 0 都按"不支持"拒绝。</summary>
        Unspecified = 0,

        /// <summary>8 kHz mono 4-bit IMA ADPCM：163 B / 40 ms（原档位）。</summary>
        High = 1,

        /// <summary>8 kHz mono 3-bit IMA ADPCM：123 B / 40 ms。</summary>
        Standard = 2,

        /// <summary>8 kHz mono 2-bit IMA ADPCM：83 B / 40 ms（默认）。</summary>
        Low = 3,

        /// <summary>8 kHz mono 1-bit CVSD：43 B / 40 ms（噪声大）。</summary>
        Ultra = 4
    }

    /// <summary>
    /// 档位的**唯一事实来源**：名字、位数、每包字节数、带宽口径。
    /// 纯函数 + 只读数组，无静态可变状态；AOT 安全。
    ///
    /// 每包 320 样本（40 ms @ 8 kHz）自包含块，块头统一 3 字节
    /// （int16 predictor + uint8 index/step），因此：
    ///   payload = 3 + 320 × bits / 8
    ///   High 163 B / Standard 123 B / Low 83 B / Ultra 43 B
    /// 线上字节 = payload + protobuf 字段 + 16 B TCP 帧头（见 VoiceNet）。
    /// </summary>
    public static class VoiceProfiles
    {
        /// <summary>默认档位：low（2-bit ADPCM）。任务要求默认降到这一档。</summary>
        public const VoiceQuality Default = VoiceQuality.Low;

        /// <summary>全部合法档位（由高到低），供配置解析 / HUD / 自检遍历。</summary>
        public static readonly VoiceQuality[] All =
        {
            VoiceQuality.High,
            VoiceQuality.Standard,
            VoiceQuality.Low,
            VoiceQuality.Ultra
        };

        /// <summary>配置名（小写，写进 SuMultiplayer.json 的 "voicequality"）。</summary>
        public static string Name(VoiceQuality quality)
        {
            switch (quality)
            {
                case VoiceQuality.High: return "high";
                case VoiceQuality.Standard: return "standard";
                case VoiceQuality.Low: return "low";
                case VoiceQuality.Ultra: return "ultra";
                default: return "unspecified";
            }
        }

        /// <summary>严格解析配置 / 日志里的档位名；不接受别名，未知值明确失败。</summary>
        public static bool TryParse(string text, out VoiceQuality quality)
        {
            quality = VoiceQuality.Unspecified;
            if (string.IsNullOrEmpty(text)) return false;
            string value = text.Trim();
            if (string.Equals(value, "high", StringComparison.OrdinalIgnoreCase))
            {
                quality = VoiceQuality.High;
                return true;
            }
            if (string.Equals(value, "standard", StringComparison.OrdinalIgnoreCase))
            {
                quality = VoiceQuality.Standard;
                return true;
            }
            if (string.Equals(value, "low", StringComparison.OrdinalIgnoreCase))
            {
                quality = VoiceQuality.Low;
                return true;
            }
            if (string.Equals(value, "ultra", StringComparison.OrdinalIgnoreCase))
            {
                quality = VoiceQuality.Ultra;
                return true;
            }
            return false;
        }

        /// <summary>枚举值是否合法（排除 Unspecified 与越界强转）。</summary>
        public static bool IsDefined(VoiceQuality quality)
        {
            switch (quality)
            {
                case VoiceQuality.High:
                case VoiceQuality.Standard:
                case VoiceQuality.Low:
                case VoiceQuality.Ultra:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>线上 id（append-only）：1=high 2=standard 3=low 4=ultra；0/其它 = 拒绝。</summary>
        public static uint WireId(VoiceQuality quality) => (uint)(int)quality;

        /// <summary>把线上 id 严格解析成档位；0 / 未知 / 越界一律 false（不兜底成默认档）。</summary>
        public static bool TryFromWireId(uint wireId, out VoiceQuality quality)
        {
            quality = VoiceQuality.Unspecified;
            if (wireId < (uint)VoiceQuality.High || wireId > (uint)VoiceQuality.Ultra) return false;
            quality = (VoiceQuality)(int)wireId;
            return IsDefined(quality);
        }

        /// <summary>每样本位数：4 / 3 / 2 / 1。非法档位返回 0（调用方必须先校验）。</summary>
        public static int BitsPerSample(VoiceQuality quality)
        {
            switch (quality)
            {
                case VoiceQuality.High: return 4;
                case VoiceQuality.Standard: return 3;
                case VoiceQuality.Low: return 2;
                case VoiceQuality.Ultra: return 1;
                default: return 0;
            }
        }

        /// <summary>自包含块字节数 = 3 B 头 + 320 × bits / 8（非法档位返回 0）。</summary>
        public static int PayloadBytes(VoiceQuality quality)
        {
            int bits = BitsPerSample(quality);
            if (bits == 0) return 0;
            return VoiceFormat.AdpcmHeaderBytes +
                VoiceFormat.PacketSamples * bits / 8;
        }

        /// <summary>每包线上字节（payload + 16 B TCP 帧头，不含 protobuf 字段）。</summary>
        public static int WireBytes(VoiceQuality quality)
        {
            int payload = PayloadBytes(quality);
            return payload == 0 ? 0 : payload + VoiceFormat.WireHeaderBytes;
        }

        /// <summary>25 包/秒的线上字节速率（不含 protobuf 字段，供日志 / 自检对照）。</summary>
        public static int WireBytesPerSecond(VoiceQuality quality)
        {
            int wire = WireBytes(quality);
            return wire == 0 ? 0 : wire * (1000 / VoiceFormat.PacketMs);
        }

        /// <summary>给日志 / HUD 的一行描述（名字 + 编码 + 字节数 + 带宽）。</summary>
        public static string Describe(VoiceQuality quality)
        {
            int bits = BitsPerSample(quality);
            if (bits == 0) return "unspecified";
            int payload = PayloadBytes(quality);
            int wirePerSecond = WireBytesPerSecond(quality);
            string codec = quality == VoiceQuality.Ultra
                ? "1-bit CVSD"
                : bits + "-bit IMA ADPCM";
            return Name(quality) + " (" + codec + ", " + payload + " B/packet, " +
                (wirePerSecond / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) +
                " KiB/s with TCP framing)";
        }
    }
}

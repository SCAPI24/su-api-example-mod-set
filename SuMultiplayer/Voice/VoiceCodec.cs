using System;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 按档位分发到具体编解码器（**唯一入口**，模块不直接选 codec）：
    ///   high / standard / low → <see cref="VoiceAdpcm"/>（4 / 3 / 2-bit IMA）
    ///   ultra                 → <see cref="VoiceCvsd"/>（1-bit CVSD）
    /// 未知档位（0 / 越界）明确失败，绝不静默退化到某个默认编码。
    ///
    /// 每个包都自包含（块头带初始 predictor + index/step），所以同一说话者
    /// 中途切换档位时，接收端逐包按 format 解码即可，不需要重新握手。
    /// </summary>
    public static class VoiceCodec
    {
        public static bool TryEncode(VoiceQuality quality, short[] pcm, int offset, int count,
            byte[] target, int targetOffset, out int written, out string reason)
        {
            if (!VoiceProfiles.IsDefined(quality))
            {
                written = 0;
                reason = "voice-quality-" + (int)quality;
                return false;
            }
            if (quality == VoiceQuality.Ultra)
                return VoiceCvsd.TryEncode(pcm, offset, count, target, targetOffset,
                    out written, out reason);
            return VoiceAdpcm.TryEncode(quality, pcm, offset, count, target, targetOffset,
                out written, out reason);
        }

        public static bool TryDecode(VoiceQuality quality, byte[] data, int offset, int length,
            short[] target, int targetOffset, int count, out string reason)
        {
            if (!VoiceProfiles.IsDefined(quality))
            {
                reason = "voice-quality-" + (int)quality;
                return false;
            }
            if (quality == VoiceQuality.Ultra)
                return VoiceCvsd.TryDecode(data, offset, length, target, targetOffset, count,
                    out reason);
            return VoiceAdpcm.TryDecode(quality, data, offset, length, target, targetOffset,
                count, out reason);
        }
    }
}

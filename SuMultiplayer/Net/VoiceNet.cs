using System;
using SuMultiplayer.Protocol;
using SuMultiplayer.Voice;
using V = SuMultiplayer.Protocol.Voice;

namespace SuMultiplayer.Net
{
    /// <summary>VoicePacket.flags 的位定义（append-only）。</summary>
    public static class VoicePacketFlags
    {
        public const uint TalkStart = 1u;
        public const uint TalkEnd = 2u;
        public const uint KnownMask = TalkStart | TalkEnd;
    }

    /// <summary>
    /// VoicePacket 的**严格校验**与线上字节口径。任何不合规的包整包拒绝，
    /// 绝不部分应用（reason 直接写日志 / 计数，可定位）。
    ///
    /// 方向规则：
    ///   · 客户端 → 主机：source_client_id 必须 = 0（客户端不能冒充别的说话者）；
    ///   · 主机 → 客户端：source_client_id ≥ 0（0 = 主机自己）。
    ///
    /// 档位规则（append-only 字段 format）：
    ///   · 0 = 未声明 → 拒绝（旧端不会静默按某个默认档位解码）；
    ///   · 1..4 = high / standard / low / ultra（<see cref="VoiceProfiles"/>）；
    ///   · 其它值 → 拒绝（`voice-format-unknown-N`）；
    ///   · data 长度必须恰好等于该档位的载荷字节数，否则拒绝
    ///     （`voice-data-L-for-&lt;name&gt;`）。这样"用 low 的长度声称 standard"
    ///     之类的不匹配不可能被静默解码成噪声。
    /// </summary>
    public static class VoicePacketPolicy
    {
        public static bool TryValidate(V.VoicePacket packet, bool fromClient, out string reason)
        {
            reason = null;
            if (packet == null)
            {
                reason = "voice-packet-null";
                return false;
            }
            if (fromClient && packet.SourceClientId != 0)
            {
                reason = "voice-source-nonzero-" + packet.SourceClientId;
                return false;
            }
            if (!fromClient && packet.SourceClientId < 0)
            {
                reason = "voice-source-negative-" + packet.SourceClientId;
                return false;
            }
            if (packet.Sequence == 0)
            {
                reason = "voice-sequence-zero";
                return false;
            }
            if ((packet.Flags & ~VoicePacketFlags.KnownMask) != 0)
            {
                reason = "voice-flags-" + packet.Flags;
                return false;
            }
            if (packet.Format == 0)
            {
                reason = "voice-format-missing";
                return false;
            }
            if (!VoiceProfiles.TryFromWireId(packet.Format, out VoiceQuality quality))
            {
                reason = "voice-format-unknown-" + packet.Format;
                return false;
            }
            int length = packet.Data == null ? -1 : packet.Data.Length;
            int expected = VoiceFormat.EncodedBytes(quality);
            if (length != expected)
            {
                reason = "voice-data-" + length + "-for-" + VoiceProfiles.Name(quality);
                return false;
            }
            return true;
        }

        /// <summary>这条拒绝原因是否属于"档位"类（模块据此计入 formatRejected）。</summary>
        public static bool IsFormatFailure(string reason) =>
            reason != null && reason.StartsWith("voice-format-", StringComparison.Ordinal);

        public static uint BuildFlags(bool talkStart, bool talkEnd)
        {
            uint flags = 0;
            if (talkStart) flags |= VoicePacketFlags.TalkStart;
            if (talkEnd) flags |= VoicePacketFlags.TalkEnd;
            return flags;
        }

        /// <summary>线上字节 = 16 B TCP 帧头 + protobuf 载荷（带宽口径统一用它）。</summary>
        public static int WireBytes(int payloadBytes) => payloadBytes + VoiceFormat.WireHeaderBytes;

        /// <summary>该档位每包线上字节（payload + 16 B TCP 帧头，不含 protobuf 字段）。</summary>
        public static int WireBytes(VoiceQuality quality) =>
            WireBytes(VoiceFormat.EncodedBytes(quality));

        /// <summary>无符号回绕安全的"比上一个新"（last = 0 表示还没有收到过）。</summary>
        public static bool IsNewer(uint sequence, uint last) =>
            last == 0 || (int)(sequence - last) > 0;
    }

    /// <summary>
    /// 主机 fan-out 的唯一规则（纯函数，自检直接断言）：
    ///   · 只发给**声明支持语音**的端（没有 VoiceState 的旧端永远收不到语音包，
    ///     否则它们会把它当成未注册消息记日志）；
    ///   · 不回发给说话者本人（无回声）；
    ///   · 监听者静音了该说话者 → 不中继（省带宽）。
    /// </summary>
    public static class VoiceRelayPolicy
    {
        public static bool ShouldRelayTo(int listenerClientId, int speakerClientId,
            bool listenerSupportsVoice, bool listenerMutedSpeaker)
        {
            if (listenerClientId <= 0) return false;       // 主机自己不是中继目标
            if (listenerClientId == speakerClientId) return false;
            if (!listenerSupportsVoice) return false;
            if (listenerMutedSpeaker) return false;
            return true;
        }

        /// <summary>主机是否接受这个说话者的包（麦克风必须在线且声明支持）。</summary>
        public static bool CanAcceptSpeaker(VoicePeerState peer)
        {
            if (!peer.Supported) return false;
            if (!peer.MicEnabled) return false;
            return true;
        }
    }
}

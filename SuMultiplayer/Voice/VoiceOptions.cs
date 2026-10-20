using System;
using System.Globalization;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 语音参数（来自 SuMultiplayer.json 的 voice* 字段；组合根映射，不把
    /// SyncConfig 类型带进模块，自检可以独立构造）。
    /// </summary>
    public sealed class VoiceOptions
    {
        /// <summary>本端是否启用语音（关 = 不采集 / 不发送 / 不播放，并声明 supported=false）。</summary>
        public bool Enabled = true;

        /// <summary>
        /// true = HUD 的 TALK 按钮点按说话（**不绑定键盘键**）；false = 开麦 + VAD。
        /// 旧实现的“按住 V”键盘 PTT 已按产品要求移除（2026-10-19）。
        /// </summary>
        public bool PushToTalk;

        /// <summary>远端语音的本地播放增益（0..1）。</summary>
        public float PlaybackVolume = 1f;

        /// <summary>主机是否中继（false = 只做本机播放，不转发；用于诊断）。</summary>
        public bool RelayEnabled = true;

        /// <summary>
        /// 编码档位（默认 <see cref="VoiceProfiles.Default"/> = low，2-bit ADPCM）。
        /// 运行期改这个值即可切换：下一包按新档位编码并带上新的 format 字节，
        /// 接收端逐包解码，不需要重新握手。
        /// </summary>
        public VoiceQuality Quality = VoiceProfiles.Default;

        /// <summary>
        /// VAD 挂起毫秒（默认 160，旧默认 260）。挂起期间仍发包，
        /// 降低它会减少说话结尾的空闲包；0 = 说完立刻停发。
        /// </summary>
        public int VadHangoverMs = VoiceFormat.VadHangoverDefaultMs;

        /// <summary>挂起帧数（20 ms/帧，配置上限 2000 ms）；模块构造 VAD 时用。</summary>
        public int VadHangoverFrames => VoiceFormat.HangoverMsToFrames(VadHangoverMs);

        public VoiceOptions Clone() => new VoiceOptions
        {
            Enabled = Enabled,
            PushToTalk = PushToTalk,
            PlaybackVolume = PlaybackVolume,
            RelayEnabled = RelayEnabled,
            Quality = Quality,
            VadHangoverMs = VadHangoverMs
        };

        public string Describe() =>
            "enabled=" + Enabled + " ptt=" + PushToTalk + " volume=" +
            PlaybackVolume.ToString("0.##", CultureInfo.InvariantCulture) +
            " relay=" + RelayEnabled + " quality=" + VoiceProfiles.Name(Quality) +
            " vadHangover=" + VadHangoverFrames * VoiceFormat.VadFrameMs + "ms";
    }
}

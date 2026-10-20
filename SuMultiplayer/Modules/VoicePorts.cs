using System;

namespace SuMultiplayer.Modules
{
    /// <summary>采集失败的明确分类（不是所有失败都能重试）。</summary>
    public enum VoiceCaptureFailure
    {
        None = 0,
        /// <summary>没有录音设备（无头服务端 / 设备没有麦克风）。可稍后重试。</summary>
        NoDevice = 1,
        /// <summary>没有权限（Android RECORD_AUDIO 未声明 / 未授权 / 被撤销）。明确失败。</summary>
        NoPermission = 2,
        /// <summary>本端禁用了语音 / 没有音频上下文。</summary>
        Disabled = 4,
        /// <summary>设备忙 / 打开失败（其它程序占用）。可稍后重试。</summary>
        Busy = 5
    }

    /// <summary>
    /// RECORD_AUDIO 的运行时权限状态（纯值）。模块只看这个状态决定“是否弹窗 / 是否开设置页”。
    /// 不在 TryStartCapture 里隐式请求权限：只有用户把语音切到 ON 时才请求。
    /// </summary>
    public enum VoicePermissionState
    {
        /// <summary>平台不需要运行时权限（Windows）。</summary>
        NotRequired = 0,
        /// <summary>本会话还没问过，且当前未授权。</summary>
        Unknown = 1,
        /// <summary>已授权（本会话再开关不弹窗）。</summary>
        Granted = 2,
        /// <summary>本次被拒绝；再把语音切到 ON 会重新请求（不永久锁死）。</summary>
        Denied = 3,
        /// <summary>“不再询问”：系统不会再弹窗，只能去应用设置页开启。</summary>
        PermanentlyDenied = 4,
        /// <summary>拿不到 Activity / 权限对象（明确失败，不弹窗）。</summary>
        Unavailable = 5
    }

    /// <summary>
    /// 语音的**引擎侧端口**（实现 = Engine/VoiceAudioEngine.cs，组合根注入）。
    /// 模块层（Modules/）不依赖 Game / OpenAL：采集、播放、设备与权限全部在实现里，
    /// 因此编解码 / VAD / 中继 / 静音 / 计数都能被 P:\tmp\tsverify 用假实现直接编译。
    ///
    /// 线程：全部在游戏主线程调用（模块 Tick / 消息处理器），实现不得起线程。
    /// 失败一律显式返回 false + reason，绝不静默降级。
    /// </summary>
    public interface IVoiceAudioPort
    {
        /// <summary>播放上下文是否可用（无头服务端可能没有）。false 时 reason 见 Describe。</summary>
        bool IsPlaybackAvailable { get; }

        bool IsCapturing { get; }

        /// <summary>
        /// 打开并启动 8 kHz / mono16 采集。失败必须给出可定位的分类与原因。
        /// 已打开时返回 true（幂等）。
        /// </summary>
        bool TryStartCapture(out VoiceCaptureFailure failure, out string reason);

        void StopCapture();

        /// <summary>
        /// 取走当前可用的采集样本（最多 maxSamples）。返回实际写入的样本数（0 = 暂时没有）。
        /// 返回 0 且 reason != null 表示采集链路故障（设备被拔 / 上下文丢失）。
        /// </summary>
        int ReadSamples(short[] target, int offset, int maxSamples, out string reason);

        /// <summary>为一个说话者开一条低延迟流（OpenAL source + 环形 buffer 队列）。</summary>
        int OpenStream(out string reason);

        /// <summary>把 40 ms PCM 追加到流；队列满 / AL 错误 = false + reason。</summary>
        bool TrySubmit(int streamId, short[] pcm, int offset, int count, float gain,
            out string reason);

        void CloseStream(int streamId);

        /// <summary>每帧调用：回收已播完的 AL buffer / 重启被 underrun 停下的 source。</summary>
        void Pump();

        // ---------------------------------------------------------------- Android 录音权限

        /// <summary>本平台是否需要运行时录音权限（Android = true，Windows = false）。</summary>
        bool IsPermissionRequired { get; }

        /// <summary>只读查询当前权限状态：**绝不弹窗**。detail 是可直接写进日志的原因。</summary>
        VoicePermissionState QueryPermission(out string detail);

        /// <summary>
        /// 发起一次系统权限请求（异步）。false = 没发出去（原因写 reason）；
        /// true = 请求已发出，结果用 <see cref="TryTakePermissionResult"/> 取。
        /// </summary>
        bool RequestPermission(out string reason);

        /// <summary>取一次已到达的请求结果（一次性；没有结果时 false）。</summary>
        bool TryTakePermissionResult(out VoicePermissionState state, out string detail);

        /// <summary>打开本应用的系统设置页（ACTION_APPLICATION_DETAILS_SETTINGS）。</summary>
        bool OpenAppSettings(out string reason);

        string Describe();
    }
}

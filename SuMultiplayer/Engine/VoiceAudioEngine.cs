using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using SuMultiplayer.Modules;
using SuMultiplayer.Voice;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 语音的 OpenAL 实现（**唯一**碰原生音频的地方）。
    ///
    /// 为什么是 OpenAL：
    ///   · 引擎本身就通过 OpenAL Soft 播放（Windows: <c>OpenTK.Audio.AudioContext</c> →
    ///     <c>openal32.dll</c>；Android: <c>Engine.Audio.Mixer.Initialize</c> →
    ///     <c>alcOpenDevice</c>，P/Invoke 库名 <c>libopenal.so</c>）；
    ///   · 随游戏发布的 OpenAL Soft 已经导出 ALC_EXT_CAPTURE（Windows
    ///     `openal32.dll` 与 Android `libopenal.so` 都有 `alcCaptureOpenDevice` 等 5 个符号，
    ///     见 doc/VOICE-CHAT.md 的 objdump 证据），**不需要新增任何原生库**；
    ///   · 采集（alcCapture*）与播放（alGenSources / alSourceQueueBuffers）都在同一进程；
    ///     播放源复用引擎已经 MakeContextCurrent 的上下文（不自建 / 不切换，避免把引擎
    ///     音频弄哑）。
    ///
    /// 明确不做：不调用 OpenTK 的 <c>AudioCapture</c> / <c>AudioDeviceEnumerator</c>。
    /// 后者的静态构造会创建 dummy context 并在结尾 <c>alcMakeContextCurrent(NULL)</c>，
    /// 把引擎当前的 context 解绑（见 OpenTK/OpenTK.Audio/AudioDeviceEnumerator.cs:39-120）
    /// —— 那会让引擎之后所有 AL 调用失败。这里只 P/Invoke 需要的函数。
    ///
    /// 线程：全部在游戏主线程调用。无静态可变状态（两个 P/Invoke 壳只有 extern 方法）。
    /// </summary>
    public sealed class VoiceAudioEngine : IVoiceAudioPort
    {
        private const string WinLibrary = "openal32.dll";
        private const string AndroidLibrary = "libopenal.so";

        private const int AlcCaptureSamples = 0x312;
        private const int AlcCaptureDefaultDeviceSpecifier = 0x311;
        private const int AlcCaptureDeviceSpecifier = 0x310;
        private const int AlcNoError = 0;

        private const int AlFormatMono16 = 0x1101;
        private const int AlGain = 0x100A;
        private const int AlPosition = 0x1004;
        private const int AlSourceState = 0x1010;
        private const int AlBuffersQueued = 0x1015;
        private const int AlBuffersProcessed = 0x1016;
        private const int AlPlaying = 0x1012;
        private const int AlStopped = 0x1014;
        private const int AlInitial = 0x1013;

        private const int CaptureBufferSamples = 1600;   // 200 ms @ 8 kHz
        private const int StreamBufferCount = 6;          // 240 ms 队列上限
        private const string RecordAudioPermission = "android.permission.RECORD_AUDIO";

        /// <summary>一条说话者的播放流（OpenAL source + 6 个环形 buffer）。</summary>
        private sealed class Stream
        {
            public int Id;
            public int Source;
            public readonly int[] Buffers = new int[StreamBufferCount];
            public readonly bool[] Free = new bool[StreamBufferCount];
            public int Queued;
            public float LastGain = float.NaN;
            public long LastSubmitMs;
        }

        private static class Win
        {
            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcGetCurrentContext", ExactSpelling = true)]
            public static extern IntPtr GetCurrentContext();

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcGetError", ExactSpelling = true)]
            public static extern int GetError(IntPtr device);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcGetString", ExactSpelling = true)]
            public static extern IntPtr GetString(IntPtr device, int param);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcGetIntegerv", ExactSpelling = true)]
            public static extern void GetInteger(IntPtr device, int param, int size, out int value);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureOpenDevice", ExactSpelling = true)]
            public static extern IntPtr CaptureOpenDevice(IntPtr name, uint frequency,
                int format, int bufferSize);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureCloseDevice", ExactSpelling = true)]
            [return: MarshalAs(UnmanagedType.I1)]
            public static extern bool CaptureCloseDevice(IntPtr device);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureStart", ExactSpelling = true)]
            public static extern void CaptureStart(IntPtr device);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureStop", ExactSpelling = true)]
            public static extern void CaptureStop(IntPtr device);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureSamples", ExactSpelling = true)]
            public static extern void CaptureSamples(IntPtr device, short[] buffer, int samples);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alGetError", ExactSpelling = true)]
            public static extern int AlError();

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alGenSources", ExactSpelling = true)]
            public static extern void GenSources(int count, int[] sources);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alDeleteSources", ExactSpelling = true)]
            public static extern void DeleteSources(int count, int[] sources);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alGenBuffers", ExactSpelling = true)]
            public static extern void GenBuffers(int count, int[] buffers);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alDeleteBuffers", ExactSpelling = true)]
            public static extern void DeleteBuffers(int count, int[] buffers);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alBufferData", ExactSpelling = true)]
            public static extern void BufferData(int buffer, int format, short[] data, int size,
                int frequency);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourceQueueBuffers", ExactSpelling = true)]
            public static extern void QueueBuffers(int source, int count, int[] buffers);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourceUnqueueBuffers", ExactSpelling = true)]
            public static extern void UnqueueBuffers(int source, int count, int[] buffers);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourcePlay", ExactSpelling = true)]
            public static extern void SourcePlay(int source);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourceStop", ExactSpelling = true)]
            public static extern void SourceStop(int source);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alGetSourcei", ExactSpelling = true)]
            public static extern void GetSourcei(int source, int param, out int value);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourcef", ExactSpelling = true)]
            public static extern void Sourcef(int source, int param, float value);

            [DllImport(WinLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSource3f", ExactSpelling = true)]
            public static extern void Source3f(int source, int param, float x, float y, float z);
        }

        private static class Android
        {
            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcGetCurrentContext", ExactSpelling = true)]
            public static extern IntPtr GetCurrentContext();

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcGetError", ExactSpelling = true)]
            public static extern int GetError(IntPtr device);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcGetString", ExactSpelling = true)]
            public static extern IntPtr GetString(IntPtr device, int param);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcGetIntegerv", ExactSpelling = true)]
            public static extern void GetInteger(IntPtr device, int param, int size, out int value);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureOpenDevice", ExactSpelling = true)]
            public static extern IntPtr CaptureOpenDevice(IntPtr name, uint frequency,
                int format, int bufferSize);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureCloseDevice", ExactSpelling = true)]
            [return: MarshalAs(UnmanagedType.I1)]
            public static extern bool CaptureCloseDevice(IntPtr device);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureStart", ExactSpelling = true)]
            public static extern void CaptureStart(IntPtr device);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureStop", ExactSpelling = true)]
            public static extern void CaptureStop(IntPtr device);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alcCaptureSamples", ExactSpelling = true)]
            public static extern void CaptureSamples(IntPtr device, short[] buffer, int samples);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alGetError", ExactSpelling = true)]
            public static extern int AlError();

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alGenSources", ExactSpelling = true)]
            public static extern void GenSources(int count, int[] sources);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alDeleteSources", ExactSpelling = true)]
            public static extern void DeleteSources(int count, int[] sources);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alGenBuffers", ExactSpelling = true)]
            public static extern void GenBuffers(int count, int[] buffers);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alDeleteBuffers", ExactSpelling = true)]
            public static extern void DeleteBuffers(int count, int[] buffers);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alBufferData", ExactSpelling = true)]
            public static extern void BufferData(int buffer, int format, short[] data, int size,
                int frequency);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourceQueueBuffers", ExactSpelling = true)]
            public static extern void QueueBuffers(int source, int count, int[] buffers);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourceUnqueueBuffers", ExactSpelling = true)]
            public static extern void UnqueueBuffers(int source, int count, int[] buffers);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourcePlay", ExactSpelling = true)]
            public static extern void SourcePlay(int source);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourceStop", ExactSpelling = true)]
            public static extern void SourceStop(int source);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alGetSourcei", ExactSpelling = true)]
            public static extern void GetSourcei(int source, int param, out int value);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSourcef", ExactSpelling = true)]
            public static extern void Sourcef(int source, int param, float value);

            [DllImport(AndroidLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "alSource3f", ExactSpelling = true)]
            public static extern void Source3f(int source, int param, float x, float y, float z);
        }

        private readonly bool m_windows;
        private readonly bool m_android;
        private readonly Dictionary<int, Stream> m_streams = new Dictionary<int, Stream>();
        private readonly short[] m_readScratch = new short[CaptureBufferSamples];
        private readonly int[] m_one = new int[1];

        private IntPtr m_captureDevice;
        private string m_captureDeviceName = string.Empty;
        private int m_nextStreamId = 1;
        private long m_nowMs;

        // Android 运行时权限（反射；AOT 安全，不引用 Mono.Android）。
        // 只在用户把语音切 ON 时请求；结果经 EngineActivity.PermissionsChanged 事件回来。
        private const int PermissionRequestCode = 0x5331;

        private object m_activity;
        private MethodInfo m_checkPermission;
        private MethodInfo m_requestPermissions;
        private MethodInfo m_shouldShowRationale;
        private MethodInfo m_startActivity;
        private EventInfo m_permissionsEvent;
        private Delegate m_permissionsHandler;
        private bool m_permissionsHooked;
        private bool m_rationaleBroken;
        private object m_packageManager;
        private MethodInfo m_getPackageInfo;
        private string m_packageName;
        private bool m_permissionDeclared = true;
        private bool m_permissionDeniedOnce;
        private int m_permissionRequestCount;
        private bool m_awaitingPermissionResult;
        private bool m_hasPermissionResult;
        private VoicePermissionState m_permissionResultState;
        private string m_permissionResultDetail = string.Empty;
        private string m_permissionDetail = "not-android";

        public VoiceAudioEngine()
        {
            m_windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            m_android = !m_windows && Type.GetType("Android.App.Application, Mono.Android") != null;
        }

        public bool IsPlaybackAvailable => CurrentContext() != IntPtr.Zero;

        public bool IsCapturing => m_captureDevice != IntPtr.Zero;

        public bool TryStartCapture(out VoiceCaptureFailure failure, out string reason)
        {
            failure = VoiceCaptureFailure.None;
            reason = null;
            if (m_captureDevice != IntPtr.Zero) return true;

            if (m_android)
            {
                // 只读检查：请求权限的唯一入口是 RequestPermission（用户把语音切 ON）。
                if (!ResolvePermissionObjects() || CheckRecordAudio() != 0)
                {
                    failure = VoiceCaptureFailure.NoPermission;
                    reason = "android-record-audio-not-granted (" + m_permissionDetail + ")";
                    return false;
                }
            }

            IntPtr device = CaptureOpen(IntPtr.Zero, VoiceFormat.SampleRate,
                AlFormatMono16, CaptureBufferSamples);
            if (device == IntPtr.Zero)
            {
                int error = CaptureError(IntPtr.Zero);
                // NULL = 没有可用采集设备（没有麦克风 / 设备被占用 / 后端拒绝）。
                // 具体 alcError 写进 reason，计数统一按 NoDevice（可重试）。
                failure = VoiceCaptureFailure.NoDevice;
                reason = "alcCaptureOpenDevice returned NULL (alcError=" + error + ")";
                return false;
            }

            CaptureStart(device);
            int startError = CaptureError(device);
            if (startError != 0)
            {
                CaptureClose(device);
                failure = VoiceCaptureFailure.Busy;
                reason = "alcCaptureStart failed (alcError=" + startError + ")";
                return false;
            }

            m_captureDevice = device;
            m_captureDeviceName = ReadDeviceName();
            global::Engine.Log.Information("[SuMP][voice] OpenAL 采集设备已打开：" + m_captureDeviceName +
                " · " + VoiceFormat.SampleRate + " Hz mono16（库=" + LibraryName + "）");
            return true;
        }

        public void StopCapture()
        {
            if (m_captureDevice == IntPtr.Zero) return;
            CaptureStop(m_captureDevice);
            CaptureClose(m_captureDevice);
            m_captureDevice = IntPtr.Zero;
            global::Engine.Log.Information("[SuMP][voice] OpenAL 采集设备已关闭");
        }

        public int ReadSamples(short[] target, int offset, int maxSamples, out string reason)
        {
            reason = null;
            if (m_captureDevice == IntPtr.Zero)
            {
                reason = "capture-not-open";
                return 0;
            }
            if (target == null || maxSamples <= 0 || offset < 0 ||
                offset + maxSamples > target.Length)
            {
                reason = "bad-target";
                return 0;
            }
            int available;
            CaptureInteger(m_captureDevice, AlcCaptureSamples, 4, out available);
            int error = CaptureError(m_captureDevice);
            if (error != 0)
            {
                reason = "alcGetIntegerv(ALC_CAPTURE_SAMPLES) error=" + error;
                return 0;
            }
            if (available <= 0) return 0;
            int take = available < maxSamples ? available : maxSamples;
            if (take > m_readScratch.Length) take = m_readScratch.Length;
            CaptureSamples(m_captureDevice, m_readScratch, take);
            error = CaptureError(m_captureDevice);
            if (error != 0)
            {
                reason = "alcCaptureSamples error=" + error;
                return 0;
            }
            Array.Copy(m_readScratch, 0, target, offset, take);
            return take;
        }

        public int OpenStream(out string reason)
        {
            reason = null;
            if (CurrentContext() == IntPtr.Zero)
            {
                reason = "no-playback-context";
                return 0;
            }
            var stream = new Stream { Id = m_nextStreamId };
            for (int i = 0; i < stream.Buffers.Length; i++) stream.Free[i] = true;
            GenSources(1, m_one);
            stream.Source = m_one[0];
            if (stream.Source == 0)
            {
                reason = "alGenSources failed (alError=" + AlError() + ")";
                return 0;
            }
            GenBuffers(stream.Buffers.Length, stream.Buffers);
            int error = AlError();
            if (error != 0)
            {
                DeleteSources(1, new[] { stream.Source });
                reason = "alGenBuffers failed (alError=" + error + ")";
                return 0;
            }
            // 与引擎自身的非定位播放一致：source 放在 (0,0,-0.1)，
            // 引擎的 AL_DISTANCE_MODEL = NONE 下这是"居中"，不会被距离衰减。
            Source3f(stream.Source, AlPosition, 0f, 0f, -0.1f);
            Sourcef(stream.Source, AlGain, 1f);
            m_nextStreamId++;
            m_streams[stream.Id] = stream;
            return stream.Id;
        }

        public bool TrySubmit(int streamId, short[] pcm, int offset, int count, float gain,
            out string reason)
        {
            reason = null;
            if (!m_streams.TryGetValue(streamId, out Stream stream))
            {
                reason = "unknown-stream-" + streamId;
                return false;
            }
            if (pcm == null || count <= 0 || offset < 0 || offset + count > pcm.Length)
            {
                reason = "bad-pcm";
                return false;
            }
            if (CurrentContext() == IntPtr.Zero)
            {
                reason = "no-playback-context";
                return false;
            }

            PumpStream(stream);

            int freeIndex = -1;
            for (int i = 0; i < stream.Free.Length; i++)
                if (stream.Free[i]) { freeIndex = i; break; }
            if (freeIndex < 0)
            {
                reason = "queue-full-" + stream.Queued + "/" + StreamBufferCount;
                return false;
            }

            int buffer = stream.Buffers[freeIndex];
            BufferData(buffer, AlFormatMono16, pcm, count * 2, VoiceFormat.SampleRate);
            int error = AlError();
            if (error != 0)
            {
                reason = "alBufferData error=" + error;
                return false;
            }
            m_one[0] = buffer;
            QueueBuffers(stream.Source, 1, m_one);
            error = AlError();
            if (error != 0)
            {
                reason = "alSourceQueueBuffers error=" + error;
                return false;
            }
            stream.Free[freeIndex] = false;
            stream.Queued++;
            if (float.IsNaN(stream.LastGain) || Math.Abs(stream.LastGain - gain) >= 0.001f)
            {
                Sourcef(stream.Source, AlGain, gain);
                stream.LastGain = gain;
            }

            GetSourcei(stream.Source, AlSourceState, out int state);
            if (state != AlPlaying)
            {
                SourcePlay(stream.Source);
                error = AlError();
                if (error != 0)
                {
                    reason = "alSourcePlay error=" + error;
                    return false;
                }
            }
            stream.LastSubmitMs = m_nowMs;
            return true;
        }

        public void CloseStream(int streamId)
        {
            if (!m_streams.TryGetValue(streamId, out Stream stream)) return;
            m_streams.Remove(streamId);
            SourceStop(stream.Source);
            GetSourcei(stream.Source, AlBuffersQueued, out int queued);
            while (queued-- > 0)
            {
                m_one[0] = 0;
                UnqueueBuffers(stream.Source, 1, m_one);
            }
            DeleteBuffers(stream.Buffers.Length, stream.Buffers);
            DeleteSources(1, new[] { stream.Source });
            AlError();
        }

        public void Pump()
        {
            m_nowMs = Environment.TickCount64;
            foreach (Stream stream in m_streams.Values) PumpStream(stream);
        }

        private void PumpStream(Stream stream)
        {
            if (stream.Source == 0) return;
            GetSourcei(stream.Source, AlBuffersProcessed, out int processed);
            for (int i = 0; i < processed; i++)
            {
                m_one[0] = 0;
                UnqueueBuffers(stream.Source, 1, m_one);
                int id = m_one[0];
                for (int b = 0; b < stream.Buffers.Length; b++)
                {
                    if (stream.Buffers[b] != id) continue;
                    if (!stream.Free[b])
                    {
                        stream.Free[b] = true;
                        if (stream.Queued > 0) stream.Queued--;
                    }
                    break;
                }
            }
            AlError();
        }

        public string Describe()
        {
            return "audio=" + (m_windows ? "windows" : (m_android ? "android" : "other")) +
                " lib=" + LibraryName +
                " playback=" + (IsPlaybackAvailable ? "context" : "no-context") +
                " capture=" + (m_captureDevice != IntPtr.Zero ? "open" : "closed") +
                " device=\"" + m_captureDeviceName + "\"" +
                " permission=" + m_permissionDetail +
                (m_awaitingPermissionResult ? "(awaiting-result)" : "") +
                " streams=" + m_streams.Count;
        }

        public void Shutdown()
        {
            UnhookPermissionsChanged();
            StopCapture();
            var ids = new List<int>(m_streams.Keys);
            for (int i = 0; i < ids.Count; i++) CloseStream(ids[i]);
            m_streams.Clear();
        }

        // ---------------------------------------------------------------- 平台转发

        private string LibraryName => m_windows ? WinLibrary : AndroidLibrary;

        private IntPtr CurrentContext() =>
            m_windows ? Win.GetCurrentContext() : Android.GetCurrentContext();

        private int CaptureError(IntPtr device) =>
            m_windows ? Win.GetError(device) : Android.GetError(device);

        private IntPtr CaptureOpen(IntPtr name, uint frequency, int format, int bufferSize) =>
            m_windows
                ? Win.CaptureOpenDevice(name, frequency, format, bufferSize)
                : Android.CaptureOpenDevice(name, frequency, format, bufferSize);

        private void CaptureStart(IntPtr device)
        {
            if (m_windows) Win.CaptureStart(device); else Android.CaptureStart(device);
        }

        private void CaptureStop(IntPtr device)
        {
            if (m_windows) Win.CaptureStop(device); else Android.CaptureStop(device);
        }

        private bool CaptureClose(IntPtr device) =>
            m_windows ? Win.CaptureCloseDevice(device) : Android.CaptureCloseDevice(device);

        private void CaptureSamples(IntPtr device, short[] buffer, int count)
        {
            if (m_windows) Win.CaptureSamples(device, buffer, count);
            else Android.CaptureSamples(device, buffer, count);
        }

        private void CaptureInteger(IntPtr device, int param, int size, out int value)
        {
            if (m_windows) Win.GetInteger(device, param, size, out value);
            else Android.GetInteger(device, param, size, out value);
        }

        private int AlError() => m_windows ? Win.AlError() : Android.AlError();

        private void GenSources(int count, int[] sources)
        {
            if (m_windows) Win.GenSources(count, sources); else Android.GenSources(count, sources);
        }

        private void DeleteSources(int count, int[] sources)
        {
            if (m_windows) Win.DeleteSources(count, sources);
            else Android.DeleteSources(count, sources);
        }

        private void GenBuffers(int count, int[] buffers)
        {
            if (m_windows) Win.GenBuffers(count, buffers); else Android.GenBuffers(count, buffers);
        }

        private void DeleteBuffers(int count, int[] buffers)
        {
            if (m_windows) Win.DeleteBuffers(count, buffers);
            else Android.DeleteBuffers(count, buffers);
        }

        private void BufferData(int buffer, int format, short[] data, int size, int frequency)
        {
            if (m_windows) Win.BufferData(buffer, format, data, size, frequency);
            else Android.BufferData(buffer, format, data, size, frequency);
        }

        private void QueueBuffers(int source, int count, int[] buffers)
        {
            if (m_windows) Win.QueueBuffers(source, count, buffers);
            else Android.QueueBuffers(source, count, buffers);
        }

        private void UnqueueBuffers(int source, int count, int[] buffers)
        {
            if (m_windows) Win.UnqueueBuffers(source, count, buffers);
            else Android.UnqueueBuffers(source, count, buffers);
        }

        private void SourcePlay(int source)
        {
            if (m_windows) Win.SourcePlay(source); else Android.SourcePlay(source);
        }

        private void SourceStop(int source)
        {
            if (m_windows) Win.SourceStop(source); else Android.SourceStop(source);
        }

        private void GetSourcei(int source, int param, out int value)
        {
            if (m_windows) Win.GetSourcei(source, param, out value);
            else Android.GetSourcei(source, param, out value);
        }

        private void Sourcef(int source, int param, float value)
        {
            if (m_windows) Win.Sourcef(source, param, value);
            else Android.Sourcef(source, param, value);
        }

        private void Source3f(int source, int param, float x, float y, float z)
        {
            if (m_windows) Win.Source3f(source, param, x, y, z);
            else Android.Source3f(source, param, x, y, z);
        }

        private string ReadDeviceName()
        {
            IntPtr pointer = m_windows
                ? Win.GetString(IntPtr.Zero, AlcCaptureDefaultDeviceSpecifier)
                : Android.GetString(IntPtr.Zero, AlcCaptureDefaultDeviceSpecifier);
            if (pointer == IntPtr.Zero) return "<none>";
            string name = Marshal.PtrToStringAnsi(pointer);
            return string.IsNullOrEmpty(name) ? "<unnamed>" : name;
        }

        // ---------------------------------------------------------------- Android 权限

        public bool IsPermissionRequired => m_android;

        /// <summary>
        /// 只读查询：**绝不弹窗**。返回当前授权状态 + 可直接写进日志的原因。
        /// Denied / PermanentlyDenied 的区分靠 <c>ShouldShowRequestPermissionRationale</c>
        /// （先有一次拒绝记录；系统连"为什么"都不再问 = 永久拒绝）。
        /// </summary>
        public VoicePermissionState QueryPermission(out string detail)
        {
            if (!m_android)
            {
                detail = "not-android";
                return VoicePermissionState.NotRequired;
            }
            if (!ResolvePermissionObjects() || m_checkPermission == null)
            {
                detail = m_permissionDetail;
                return VoicePermissionState.Unavailable;
            }
            if (CheckRecordAudio() == 0)
            {
                m_permissionDetail = "granted";
                detail = "granted";
                return VoicePermissionState.Granted;
            }
            if (!m_permissionDeclared)
            {
                detail = "android-record-audio-not-declared（需要把 RECORD_AUDIO 加进 APK 的 " +
                    "AndroidManifest.xml 并重新打包）";
                return VoicePermissionState.Unavailable;
            }
            if (m_permissionRequestCount == 0)
            {
                detail = "not-requested-yet";
                return VoicePermissionState.Unknown;
            }
            detail = DescribeDenied();
            return IsPermanentlyDenied() ?
                VoicePermissionState.PermanentlyDenied : VoicePermissionState.Denied;
        }

        /// <summary>
        /// 发起一次系统权限请求（异步）。true = 请求已发出，结果用
        /// <see cref="TryTakePermissionResult"/> 取；false = 根本没发出去（reason 定位原因）。
        /// 收不到结果通道（Activity 没有 PermissionsChanged 事件）时明确失败，
        /// 绝不让模块停在"永远 pending"。
        /// </summary>
        public bool RequestPermission(out string reason)
        {
            reason = null;
            if (!m_android)
            {
                reason = "not-android";
                return false;
            }
            if (!ResolvePermissionObjects() || m_activity == null || m_requestPermissions == null)
            {
                reason = "android-activity-unavailable: " + m_permissionDetail;
                return false;
            }
            if (!m_permissionDeclared)
            {
                reason = "android-record-audio-not-declared（需要把 RECORD_AUDIO 加进 APK 的 " +
                    "AndroidManifest.xml 并重新打包）";
                return false;
            }
            if (!m_permissionsHooked)
            {
                reason = "android-permission-result-channel-unavailable: " + m_permissionDetail;
                return false;
            }
            if (m_awaitingPermissionResult)
            {
                reason = "android-permission-awaiting-result";
                return false;
            }
            try
            {
                m_requestPermissions.Invoke(m_activity,
                    new object[] { new[] { RecordAudioPermission }, PermissionRequestCode });
                m_awaitingPermissionResult = true;
                m_permissionRequestCount++;
                m_permissionDetail = "requested-" + m_permissionRequestCount;
                global::Engine.Log.Information("[SuMP][voice] 已调用 Activity.RequestPermissions(" +
                    RecordAudioPermission + ", requestCode=" + PermissionRequestCode + ")；" +
                    "等待用户答复");
                return true;
            }
            catch (Exception e)
            {
                reason = "android-request-permission-failed: " + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>取走一次权限结果（一次性）。没有结果时 false。</summary>
        public bool TryTakePermissionResult(out VoicePermissionState state, out string detail)
        {
            if (!m_hasPermissionResult)
            {
                state = VoicePermissionState.Unknown;
                detail = null;
                return false;
            }
            m_hasPermissionResult = false;
            state = m_permissionResultState;
            detail = m_permissionResultDetail;
            return true;
        }

        /// <summary>
        /// 打开本应用的系统设置页（Settings.ACTION_APPLICATION_DETAILS_SETTINGS +
        /// package:&lt;name&gt;），让用户给"不再询问"的录音权限重新授权。
        /// </summary>
        public bool OpenAppSettings(out string reason)
        {
            reason = null;
            if (!m_android)
            {
                reason = "not-android";
                return false;
            }
            if (!ResolvePermissionObjects() || m_activity == null || m_startActivity == null)
            {
                reason = "android-activity-unavailable: " + m_permissionDetail;
                return false;
            }
            if (string.IsNullOrEmpty(m_packageName))
            {
                reason = "android-package-name-unavailable";
                return false;
            }
            try
            {
                Type settingsType = Type.GetType("Android.Provider.Settings, Mono.Android");
                FieldInfo actionField = settingsType?.GetField("ActionApplicationDetailsSettings",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                string action = actionField?.GetValue(null) as string;
                if (string.IsNullOrEmpty(action))
                {
                    reason = "android-settings-action-unavailable（Android.Provider.Settings." +
                        "ActionApplicationDetailsSettings 找不到）";
                    return false;
                }
                Type intentType = Type.GetType("Android.Content.Intent, Mono.Android");
                Type uriType = Type.GetType("Android.Net.Uri, Mono.Android");
                ConstructorInfo ctor = intentType?.GetConstructor(new[] { typeof(string) });
                MethodInfo parse = uriType?.GetMethod("Parse", BindingFlags.Static |
                    BindingFlags.Public, null, new[] { typeof(string) }, null);
                MethodInfo setData = intentType?.GetMethod("SetData", new[] { uriType });
                if (ctor == null || parse == null || setData == null)
                {
                    reason = "android-settings-intent-unavailable（Intent / Uri 绑定缺失）";
                    return false;
                }
                object uri = parse.Invoke(null, new object[] { "package:" + m_packageName });
                object intent = ctor.Invoke(new object[] { action });
                setData.Invoke(intent, new[] { uri });
                m_startActivity.Invoke(m_activity, new[] { intent });
                global::Engine.Log.Information("[SuMP][voice] 已打开应用设置页：" + action +
                    " data=package:" + m_packageName);
                return true;
            }
            catch (Exception e)
            {
                reason = "android-open-settings-failed: " + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- 权限内部

        private bool ResolvePermissionObjects()
        {
            if (!m_android) return false;
            // 只有拿到完整 Activity（能请求 / 能开设置）才认为解析完成；
            // 只有 Application.Context 时继续重试解析 Activity。
            if (m_checkPermission != null && m_requestPermissions != null &&
                m_startActivity != null)
                return true;
            try
            {
                Type activityType = Type.GetType("Engine.EngineActivity, Engine");
                FieldInfo field = activityType?.GetField("m_activity",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                object activity = field?.GetValue(null);
                if (activity != null)
                {
                    m_activity = activity;
                    Type activityClr = activity.GetType();
                    m_checkPermission = activityClr.GetMethod("CheckSelfPermission",
                        new[] { typeof(string) });
                    m_requestPermissions = activityClr.GetMethod("RequestPermissions",
                        new[] { typeof(string[]), typeof(int) });
                    m_shouldShowRationale = activityClr.GetMethod(
                        "ShouldShowRequestPermissionRationale", new[] { typeof(string) });
                    Type intentType = Type.GetType("Android.Content.Intent, Mono.Android");
                    if (intentType != null)
                    {
                        m_startActivity = activityClr.GetMethod("StartActivity",
                            new[] { intentType });
                    }
                    m_rationaleBroken = m_shouldShowRationale == null;
                    TryHookPermissionsChanged(activityType, activity);
                    ResolvePackageDeclaration(activityClr);
                    m_permissionDetail = m_permissionDeclared
                        ? "declared"
                        : "not-declared";
                    return m_checkPermission != null && m_requestPermissions != null;
                }
                // 只读退路（非静默：detail 会明确写 application-context）：
                // Activity 还没建好时仍可用 Application.Context 检查已授权状态；
                // 这条路径不能弹窗、不能开设置页（模块会收到明确失败）。
                Type appType = Type.GetType("Android.App.Application, Mono.Android");
                PropertyInfo contextProperty = appType?.GetProperty("Context",
                    BindingFlags.Static | BindingFlags.Public);
                object context = contextProperty?.GetValue(null);
                if (context != null)
                {
                    m_activity = context;
                    m_checkPermission = context.GetType().GetMethod("CheckSelfPermission",
                        new[] { typeof(string) });
                    m_permissionDetail = "application-context";
                    return m_checkPermission != null;
                }
                m_permissionDetail = "engine-activity-null";
                return false;
            }
            catch (Exception e)
            {
                m_permissionDetail = "resolve-failed: " + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        private void TryHookPermissionsChanged(Type activityType, object activity)
        {
            if (m_permissionsHooked) return;
            try
            {
                EventInfo info = activityType?.GetEvent("PermissionsChanged",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (info == null || info.EventHandlerType != typeof(Action<int>))
                {
                    global::Engine.Log.Warning("[SuMP][voice] EngineActivity.PermissionsChanged " +
                        "不可用或签名不是 Action<int> → 收不到录音权限结果" +
                        "（Android 上无法打开语音；其它平台不受影响）");
                    m_permissionDetail = "permissions-event-missing";
                    return;
                }
                // Action<int> 是编译期已知的委托类型（不走 Delegate.CreateDelegate），
                // Android AOT 下不需要为事件动态生成委托签名。
                Action<int> handler = OnPermissionsChanged;
                info.AddEventHandler(activity, handler);
                m_permissionsEvent = info;
                m_permissionsHandler = handler;
                m_permissionsHooked = true;
            }
            catch (Exception e)
            {
                global::Engine.Log.Warning("[SuMP][voice] 订阅 EngineActivity.PermissionsChanged " +
                    "失败：" + e.GetType().Name + " " + e.Message);
                m_permissionDetail = "permissions-hook-failed";
            }
        }

        private void UnhookPermissionsChanged()
        {
            if (!m_permissionsHooked) return;
            try
            {
                m_permissionsEvent?.RemoveEventHandler(m_activity, m_permissionsHandler);
            }
            catch (Exception e)
            {
                global::Engine.Log.Warning("[SuMP][voice] 取消订阅 PermissionsChanged 失败：" +
                    e.GetType().Name + " " + e.Message);
            }
            m_permissionsHooked = false;
            m_permissionsHandler = null;
        }

        /// <summary>EngineActivity.OnRequestPermissionsResult → PermissionsChanged(requestCode)。</summary>
        private void OnPermissionsChanged(int requestCode)
        {
            if (requestCode != PermissionRequestCode) return;
            if (!m_awaitingPermissionResult)
            {
                global::Engine.Log.Warning("[SuMP][voice] 收到未在等待中的录音权限结果" +
                    "（requestCode=" + requestCode + "）→ 忽略");
                return;
            }
            m_awaitingPermissionResult = false;
            m_permissionResultState = EvaluatePermissionResult(out string detail);
            m_permissionResultDetail = detail;
            m_hasPermissionResult = true;
            global::Engine.Log.Information("[SuMP][voice] 录音权限结果：" +
                m_permissionResultState + "（" + detail + "）");
        }

        private VoicePermissionState EvaluatePermissionResult(out string detail)
        {
            int permission = CheckRecordAudio();
            if (permission == 0)
            {
                m_permissionDeniedOnce = false;
                m_permissionDetail = "granted";
                detail = "granted";
                return VoicePermissionState.Granted;
            }
            detail = DescribeDenied();
            m_permissionDeniedOnce = true;
            return IsPermanentlyDenied() ?
                VoicePermissionState.PermanentlyDenied : VoicePermissionState.Denied;
        }

        /// <summary>拒绝原因（rationale 可用性 + 请求次数），写进日志 / HUD。</summary>
        private string DescribeDenied()
        {
            string rationale = m_rationaleBroken
                ? "unknown"
                : (ShouldShowRationale() ? "true" : "false");
            string detail = "android-record-audio-denied (rationale=" + rationale +
                ", requests=" + m_permissionRequestCount + ")";
            m_permissionDetail = detail;
            return detail;
        }

        /// <summary>
        /// "不再询问"判定：先有一次拒绝记录，且系统说不需要解释
        /// （rationale=false），或（rationale API 不可用时）已经请求过两次。
        /// </summary>
        private bool IsPermanentlyDenied()
        {
            if (!m_permissionDeniedOnce) return false;
            if (m_rationaleBroken) return m_permissionRequestCount >= 2;
            return !ShouldShowRationale();
        }

        private bool ShouldShowRationale()
        {
            if (m_shouldShowRationale == null || m_activity == null) return false;
            try
            {
                object result = m_shouldShowRationale.Invoke(m_activity,
                    new object[] { RecordAudioPermission });
                return result is bool allowed && allowed;
            }
            catch (Exception e)
            {
                m_rationaleBroken = true;
                m_permissionDetail = "rationale-failed: " + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>CheckSelfPermission 的返回值（0 = granted；-1 = 调用失败 / 对象缺失）。</summary>
        private int CheckRecordAudio()
        {
            if (m_checkPermission == null || m_activity == null) return -1;
            try
            {
                object granted = m_checkPermission.Invoke(m_activity,
                    new object[] { RecordAudioPermission });
                if (granted is int code) return code;
                if (granted == null) return -1;
                return Convert.ToInt32(granted, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                m_permissionDetail = "check-permission-failed: " + e.GetType().Name + " " +
                    e.Message;
                return -1;
            }
        }

        private void ResolvePackageDeclaration(Type activityClr)
        {
            try
            {
                PropertyInfo packageName = activityClr.GetProperty("PackageName");
                PropertyInfo packageManager = activityClr.GetProperty("PackageManager");
                m_packageName = packageName?.GetValue(m_activity) as string;
                m_packageManager = packageManager?.GetValue(m_activity);
                if (m_packageManager == null || string.IsNullOrEmpty(m_packageName)) return;
                Type flagsType = Type.GetType("Android.Content.PM.PackageInfoFlags, Mono.Android");
                if (flagsType == null || !flagsType.IsEnum) return;
                object flags = Enum.Parse(flagsType, "Permissions");
                m_getPackageInfo = m_packageManager.GetType().GetMethod("GetPackageInfo",
                    new[] { typeof(string), flagsType });
                if (m_getPackageInfo == null) return;
                object info = m_getPackageInfo.Invoke(m_packageManager,
                    new[] { m_packageName, flags });
                PropertyInfo requestedProperty = info?.GetType()
                    .GetProperty("RequestedPermissions");
                string[] requested = requestedProperty?.GetValue(info) as string[];
                if (requested == null) return;
                m_permissionDeclared = false;
                for (int i = 0; i < requested.Length; i++)
                {
                    if (string.Equals(requested[i], RecordAudioPermission,
                            StringComparison.Ordinal))
                    {
                        m_permissionDeclared = true;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                // 查询失败不改变 m_permissionDeclared（默认 true）：由 RequestPermissions
                // 的实际结果说话，不在这里制造"未声明"的假结论。
                global::Engine.Log.Warning("[SuMP][voice] 读取 APK 的 RequestedPermissions 失败：" +
                    e.GetType().Name + " " + e.Message);
            }
        }
    }
}

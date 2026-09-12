using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>录制会话的对外状态（控制面 `ai.status` 与 HUD 提示用）。</summary>
    public enum RecordingPhase
    {
        /// <summary>没在录。</summary>
        Idle,

        /// <summary>正在录（人类操作照常，行为树暂停）。</summary>
        Recording,

        /// <summary>录制暂停（再按 PgUp 继续；时长不计入）。</summary>
        Paused,

        /// <summary>已停止但**还没命名保存**（按 PgDn 停下的那一刻起就是这个状态）。</summary>
        PendingSave
    }

    /// <summary>录制会话的只读快照。</summary>
    public sealed class AiRecordingStatus
    {
        public RecordingPhase Phase = RecordingPhase.Idle;

        public string Name;

        /// <summary>累计录制时长（秒，暂停期间不计）。</summary>
        public double Duration;

        /// <summary>累计采样帧数（P0 只计数；帧流 P1 写）。</summary>
        public long Frames;

        /// <summary>待保存捕获的默认名字（PendingSave 时有效）。</summary>
        public string SuggestedName;

        /// <summary>最近一次落盘的文件路径（成功保存后有效）。</summary>
        public string LastSavedPath;

        /// <summary>最近一次错误/说明。</summary>
        public string Message;

        public bool IsActive
        {
            get
            {
                return Phase == RecordingPhase.Recording || Phase == RecordingPhase.Paused
                    || Phase == RecordingPhase.PendingSave;
            }
        }

        public string ToWireName()
        {
            switch (Phase)
            {
                case RecordingPhase.Recording: return "recording";
                case RecordingPhase.Paused: return "paused";
                case RecordingPhase.PendingSave: return "pending-save";
                default: return "idle";
            }
        }

        public override string ToString()
        {
            return "recording(" + ToWireName() + " " + Duration.ToString("0.00") + "s frames="
                + Frames + (string.IsNullOrEmpty(Name) ? string.Empty : " name=" + Name) + ")";
        }
    }

    /// <summary>
    /// 录制控制面（纯接口：命令层与自检只认它）。
    /// 每个方法返回一句人类可读的结果，出错抛 <see cref="AiCommandException"/>（带稳定错误码）。
    /// </summary>
    public interface IAiRecordingControl
    {
        AiRecordingStatus Status { get; }

        /// <summary>开始录制（录制中先停掉上一次）。</summary>
        string Start(string name);

        /// <summary>录制 ⇄ 暂停。</summary>
        string TogglePause();

        /// <summary>停止录制（进入"待命名保存"）。</summary>
        string Stop();

        /// <summary>把待保存的录制写成 `<名字>.scatpak`（<paramref name="overwrite"/> 为假时同名报错）。</summary>
        string Save(string name, bool overwrite);

        /// <summary>丢弃待保存的录制。</summary>
        string Discard();
    }

    /// <summary>
    /// 录制会话（P0-9 骨架）：**纯逻辑，不依赖游戏**。
    ///
    /// 状态机：
    /// <code>
    /// Idle --Start--> Recording --PgDn/Stop--> PendingSave --Save(name)--> Idle
    ///                     ^  |                                   \\--Discard--> Idle
    ///                     |  v
    ///                   Paused   （暂停期间不计时长、不采样）
    /// </code>
    ///
    /// P1 起：帧末由 <see cref="PlayerInputSampler"/> 每帧调一次 <see cref="CaptureFrame"/>，
    /// 于是 `tracks/input.bin`（逐帧原始输入）、`keyframes.json`（每 0.5 s 的位置/朝向）
    /// 与 `tracks/events.json`（稀疏语义事件）都是真数据，包可以回放。
    ///
    /// 落盘位置：`<实例根>/PlayerAi/BehaviorTrees/*.scatpak`（与树包同路径，计划 §4.4）。
    /// Mod 只读目录里不放录制产物 —— 用户的东西写在用户目录，才不会被 Mod 更新冲掉。
    /// </summary>
    public sealed class AiRecordingSession : IAiRecordingControl
    {
        public const string DefaultNamePrefix = "action_";
        public const int MaxNameLength = 64;
        public const int DefaultSampleRate = 60;

        private readonly AiRecordingStatus m_status = new AiRecordingStatus();
        private readonly List<string> m_history = new List<string>();

        private readonly ScatTrack.Track m_track = new ScatTrack.Track();
        private readonly List<PackageValue> m_keyframes = new List<PackageValue>();
        private readonly List<PackageValue> m_events = new List<PackageValue>();

        private RecordingStartState m_startState;
        private double m_accumulated;
        private double m_lastCaptureTime;
        private double m_nextKeyframeAt;
        private string m_targetDirectory;
        private bool m_hasCapture;

        public AiRecordingSession(string targetDirectory = null)
        {
            m_targetDirectory = targetDirectory;
        }

        /// <summary>录制产物目录（实例包目录）。可在开始录制前设置。</summary>
        public string TargetDirectory
        {
            get { return m_targetDirectory; }
            set { m_targetDirectory = value; }
        }

        public AiRecordingStatus Status
        {
            get { return m_status; }
        }

        /// <summary>是否正在录（含暂停）—— 模式层据此进入"录制中"模式。</summary>
        public bool IsActive
        {
            get { return m_status.IsActive; }
        }

        /// <summary>逐帧输入轨道（保存时写进 tracks/input.bin）。</summary>
        public ScatTrack.Track Track
        {
            get { return m_track; }
        }

        /// <summary>关键帧条数（漂移检查用；每 <see cref="PlayerAiConfig.KeyframeIntervalSeconds"/> 一条）。</summary>
        public int KeyframeCount
        {
            get { return m_keyframes.Count; }
        }

        /// <summary>语义事件条数（快捷栏切换、鼠标按下等稀疏事件）。</summary>
        public int EventCount
        {
            get { return m_events.Count; }
        }

        /// <summary>本次录制的起点（由调用方在 Start 时提供）。</summary>
        public RecordingStartState StartState
        {
            get { return m_startState; }
            set { m_startState = value; }
        }

        /// <summary>最近若干次操作记录（控制面与日志用）。</summary>
        public IReadOnlyList<string> History
        {
            get { return m_history; }
        }

        // ---------------------------------------------------------------- 状态机

        public string Start(string name)
        {
            if (IsActive)
            {
                // 已经在录：等价于"重开一段"，先按正常收尾丢弃上一段，避免留下半截状态。
                Discard();
            }

            m_accumulated = 0.0;
            m_lastCaptureTime = 0.0;
            m_nextKeyframeAt = 0.0;
            m_hasCapture = false;
            m_track.KeyNames.Clear();
            m_track.Frames.Clear();
            m_keyframes.Clear();
            m_events.Clear();
            m_status.Phase = RecordingPhase.Recording;
            m_status.Name = SanitizeName(name);
            m_status.SuggestedName = string.IsNullOrEmpty(m_status.Name)
                ? SuggestName()
                : m_status.Name;
            m_status.Duration = 0.0;
            m_status.Frames = 0;
            m_status.LastSavedPath = null;
            m_status.Message = "recording started";
            Record("start name=" + m_status.SuggestedName);
            return "recording started (" + m_status.SuggestedName + ")";
        }

        public string TogglePause()
        {
            if (m_status.Phase == RecordingPhase.Recording)
            {
                m_status.Phase = RecordingPhase.Paused;
                m_status.Message = "recording paused";
                Record("pause at " + m_accumulated.ToString("0.00") + "s");
                return "recording paused";
            }

            if (m_status.Phase == RecordingPhase.Paused)
            {
                m_status.Phase = RecordingPhase.Recording;
                m_status.Message = "recording resumed";
                Record("resume at " + m_accumulated.ToString("0.00") + "s");
                return "recording resumed";
            }

            throw new AiCommandException("not_recording",
                "nothing is being recorded (phase=" + m_status.ToWireName() + ")");
        }

        public string Stop()
        {
            if (m_status.Phase == RecordingPhase.Idle)
            {
                throw new AiCommandException("not_recording", "nothing is being recorded");
            }

            if (m_status.Phase == RecordingPhase.PendingSave)
                return "already stopped; waiting for a name (see ai.record.save name=...)";

            m_status.Phase = RecordingPhase.PendingSave;
            m_hasCapture = true;
            m_status.Message = "stopped; waiting for a name";
            Record("stop duration=" + m_accumulated.ToString("0.00") + "s frames=" + m_status.Frames);
            return "stopped after " + m_accumulated.ToString("0.00") + "s (" + m_status.Frames
                + " frames); name it to save";
        }

        /// <summary>
        /// 收一帧（帧末由 <see cref="PlayerInputSampler"/> 调用）。返回 false 表示当前不在录。
        ///
        /// 帧的间隔直接用录制时钟算（不是调用方给的），这样"采样点抖动"不会让时间轴跑偏。
        /// </summary>
        public bool CaptureFrame(RecordingFrame frame, List<string> heldKeyNames = null,
            List<string> pressedKeyNames = null)
        {
            if (m_status.Phase != RecordingPhase.Recording)
                return false;

            if (m_track.Frames.Count == 0)
            {
                frame.DeltaMs = 0;
            }
            else
            {
                double delta = m_accumulated - m_lastCaptureTime;
                int milliseconds = (int)Math.Round(delta * 1000.0);
                frame.DeltaMs = milliseconds < 0 ? 0 : (milliseconds > 65535 ? 65535 : milliseconds);
            }
            m_lastCaptureTime = m_accumulated;

            frame.KeysHeld = InternKeys(heldKeyNames);
            frame.KeysPressed = InternKeys(pressedKeyNames);
            m_track.Frames.Add(frame);
            m_status.Frames = m_track.Frames.Count;
            m_status.Duration = m_track.DurationSeconds;
            return true;
        }

        /// <summary>把键名登记进轨道名表并返回索引（自描述格式：换版本也不会认错键）。</summary>
        private byte[] InternKeys(List<string> names)
        {
            if (names == null || names.Count == 0)
                return new byte[0];

            var indices = new List<byte>(names.Count);
            for (int i = 0; i < names.Count && indices.Count < ScatTrack.MaxKeysPerFrame; i++)
            {
                if (string.IsNullOrEmpty(names[i]))
                    continue;
                int index = m_track.EnsureKey(names[i]);
                if (index >= 0)
                    indices.Add((byte)index);
            }
            return indices.ToArray();
        }

        /// <summary>
        /// 记一个关键帧（位置/朝向）。录制时每 <see cref="PlayerAiConfig.KeyframeIntervalSeconds"/> 秒一条；
        /// 回放时用它做漂移检查（超限即失败，不做瞬移修正）。
        /// </summary>
        public bool CaptureKeyframe(double time, float x, float y, float z, float yawDegrees,
            float pitchDegrees, bool force = false)
        {
            if (m_status.Phase != RecordingPhase.Recording)
                return false;
            if (!force && time < m_nextKeyframeAt)
                return false;

            PackageValue frame = PackageValue.Object();
            frame.Set("t", PackageValue.Number(Math.Round(time, 3)));

            PackageValue state = PackageValue.Object();
            state.Set("position", PackageValue.Array(new[]
            {
                PackageValue.Number(x), PackageValue.Number(y), PackageValue.Number(z)
            }));
            state.Set("yaw", PackageValue.Number(yawDegrees));
            state.Set("pitch", PackageValue.Number(pitchDegrees));
            frame.Set("start", state);

            m_keyframes.Add(frame);
            m_nextKeyframeAt = time + Math.Max(0.1, PlayerAiConfig.KeyframeIntervalSeconds);
            return true;
        }

        /// <summary>记一条稀疏语义事件（快捷栏切换、鼠标按下、开关切换…）。</summary>
        public bool CaptureEvent(double time, string kind, string detail)
        {
            if (m_status.Phase != RecordingPhase.Recording)
                return false;

            PackageValue item = PackageValue.Object();
            item.Set("t", PackageValue.Number(Math.Round(time, 3)));
            item.Set("kind", PackageValue.Str(kind));
            if (!string.IsNullOrEmpty(detail))
                item.Set("detail", PackageValue.Str(detail));
            m_events.Add(item);
            return true;
        }

        /// <summary>关键帧 JSON（保存时写进 keyframes.json；没有就用起点补一条）。</summary>
        public PackageValue BuildKeyframes()
        {
            PackageValue root = PackageValue.Object();
            root.Set("format", PackageValue.Str("scat-keyframes"));
            root.Set("version", PackageValue.Number(1));
            root.Set("interval", PackageValue.Number(Math.Max(0.1,
                PlayerAiConfig.KeyframeIntervalSeconds)));

            PackageValue frames = PackageValue.Array();
            if (m_keyframes.Count == 0)
                frames.Add(ScatPackage.StartKeyframe(BuildManifest("preview")));
            else
            {
                for (int i = 0; i < m_keyframes.Count; i++)
                    frames.Add(m_keyframes[i]);
            }
            root.Set("frames", frames);
            return root;
        }

        /// <summary>事件 JSON（保存时写进 tracks/events.json）。</summary>
        public PackageValue BuildEvents()
        {
            PackageValue root = PackageValue.Object();
            root.Set("format", PackageValue.Str("scat-events"));
            root.Set("version", PackageValue.Number(1));

            PackageValue events = PackageValue.Array();
            for (int i = 0; i < m_events.Count; i++)
                events.Add(m_events[i]);
            root.Set("events", events);
            return root;
        }

        /// <summary>帧首推进（录制中才累计）。暂停与待保存都不计时。</summary>
        public void Tick(float deltaTime)
        {
            if (m_status.Phase != RecordingPhase.Recording)
                return;

            double step = deltaTime > 0f ? deltaTime : 0.0;
            m_accumulated += step;
            m_status.Duration = m_accumulated;
            m_status.Frames++;

            // P1 在这里把"人在这一帧的意图"追加进 tracks/input.bin：
            // 采样点在帧末读 ComponentInput.PlayerInput（计划 §6.2），帧流格式见 §4.3。
        }

        // ---------------------------------------------------------------- 落盘

        public string Save(string name, bool overwrite)
        {
            if (!m_hasCapture)
            {
                throw new AiCommandException("not_recording",
                    "there is no finished recording to save (press PgDn first, or ai.record.stop)");
            }

            string safeName = SanitizeName(name);
            if (string.IsNullOrEmpty(safeName))
            {
                throw new AiCommandException("invalid_argument",
                    "a file name is required (letters/digits/_/-/., max " + MaxNameLength + ")");
            }
            if (string.IsNullOrEmpty(m_targetDirectory))
            {
                throw new AiCommandException("not_ready",
                    "no writable folder for action packages (PlayerAi/BehaviorTrees)");
            }

            string path = Path.Combine(m_targetDirectory, safeName + ScatManifest.Extension);
            if (File.Exists(path) && !overwrite)
            {
                throw new AiCommandException("already_exists",
                    "'" + safeName + ScatManifest.Extension
                    + "' already exists; pass overwrite=true or choose another name");
            }

            ScatManifest manifest = BuildManifest(safeName);
            string error;
            if (!ScatPackage.TryWrite(path, manifest, m_track, BuildEvents(), BuildKeyframes(),
                out error))
            {
                m_status.Message = "save failed: " + error;
                Record("save failed: " + error);
                throw new AiCommandException("io_error", "cannot write the action package: " + error);
            }

            m_status.Name = safeName;
            m_status.SuggestedName = safeName;
            m_status.LastSavedPath = path;
            m_status.Message = "saved";
            m_status.Phase = RecordingPhase.Idle;
            m_hasCapture = false;
            Record("saved " + Path.GetFileName(path) + " (" + manifest.Duration.ToString("0.00")
                + "s, " + manifest.Frames + " frames, " + m_track.KeyNames.Count + " keys, "
                + m_keyframes.Count + " keyframes, " + m_events.Count + " events)");

            // 返回**真实路径**：调用方（命令层的 path 字段、脚本）要拿它去开文件。
            // 人类可读的那句 "saved <路径>" 已经在事件日志和 recording.message 里了，
            // 别再把它塞进 path 字段 —— 那会让"照着 path 去读文件"的人（和脚本）摔一跤。
            return path;
        }

        public string Discard()
        {
            bool hadSomething = IsActive;
            m_status.Phase = RecordingPhase.Idle;
            m_status.Message = hadSomething ? "discarded" : "nothing to discard";
            m_hasCapture = false;
            m_accumulated = 0.0;
            m_track.KeyNames.Clear();
            m_track.Frames.Clear();
            m_keyframes.Clear();
            m_events.Clear();
            m_status.Duration = 0.0;
            m_status.Frames = 0;
            m_status.SuggestedName = null;
            Record(hadSomething ? "discard" : "discard (noop)");
            return m_status.Message;
        }

        /// <summary>组装 manifest（P0：只有元数据与起点）。</summary>
        public ScatManifest BuildManifest(string safeName)
        {
            var manifest = new ScatManifest
            {
                // id 是机器用的键（引用/去重/迁移都靠它），只允许 [A-Za-z0-9._-]；
                // name 是给人看的，可以是中文。文件名的规则与 name 一致（见 SanitizeName）。
                Id = AsciiId(safeName),
                Name = safeName,
                RecordedUtc = DateTime.UtcNow.ToString("o"),
                Duration = Math.Round(m_track.FrameCount > 0 ? m_track.DurationSeconds : m_accumulated,
                    3),
                SampleRate = DefaultSampleRate,
                Frames = m_track.FrameCount > 0 ? m_track.FrameCount : m_status.Frames,
                Start = m_startState ?? new RecordingStartState()
            };
            return manifest;
        }

        /// <summary>
        /// 把（可能含中文的）名字压成合法的 `manifest.id`。
        /// 全是非 ASCII 字符时退回"带时间戳的建议名"，保证唯一且可读（`action_20260912_000255`）。
        /// </summary>
        public static string AsciiId(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return SuggestName();

            var builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '_' || c == '-' || c == '.';
                builder.Append(ok ? c : '_');
            }

            string result = builder.ToString().Trim('.', '_', ' ');
            if (result.Replace("_", string.Empty).Length == 0)
                result = SuggestName(); // 「进入游戏」这种全中文名：机器键退回时间戳

            return result.Length > MaxNameLength ? result.Substring(0, MaxNameLength) : result;
        }

        /// <summary>待保存捕获的默认名字（`action_20260912_101530`）。</summary>
        public static string SuggestName(DateTime? utcNow = null)
        {
            DateTime now = utcNow ?? DateTime.UtcNow;
            return DefaultNamePrefix + now.ToString("yyyyMMdd_HHmmss");
        }

        /// <summary>
        /// 名字消毒：只留字母/数字/下划线/连字符/点，其它一律换掉。
        /// 录制的名字会变成文件名，必须在这里挡住路径分隔符与保留字符（计划 §6.1）。
        ///
        /// 这里用 `char.IsLetterOrDigit`（而不是只认 ASCII）是**故意的**：
        /// 包名是给人看的，用户要的包里就有「进入游戏」这种中文名；
        /// 真正危险的是 `/ \ : * ? " &lt; &gt; |` 与控制字符 —— 它们都不是字母数字，仍然会被换掉。
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length && builder.Length < MaxNameLength; i++)
            {
                char c = name[i];
                bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
                builder.Append(ok ? c : '_');
            }

            string result = builder.ToString().Trim('.', '_', ' ');
            if (result.Length == 0)
                return null;

            // 点号打头/结尾在 Windows 上仍然不安全，统一清掉
            return result.Trim('.');
        }

        private void Record(string entry)
        {
            m_history.Add(entry);
            while (m_history.Count > 32)
                m_history.RemoveAt(0);

            if (PlayerAiConfig.VerboseLogging)
                Engine.Log.Information("[PlayerAi][rec] " + entry);
        }

        public override string ToString()
        {
            return m_status.ToString();
        }
    }
}

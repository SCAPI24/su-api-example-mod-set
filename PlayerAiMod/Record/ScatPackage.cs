using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 动作包 `.scatpak` 的 manifest（格式 v1，见 doc/player-ai-plan.md §4.3）。
    ///
    /// 关键约定：**坐标/朝向一律相对录制起点** —— 这样同一个动作包能跨地图、跨设备复用
    /// （回放时以当前起点为基准重放相对运动）。所以这里只记起点，不记绝对坐标轨迹。
    /// </summary>
    public sealed class ScatManifest
    {
        public const string FormatName = "scat";
        public const int FormatVersion = 1;
        public const string FileName = "manifest.json";
        public const string Extension = ".scatpak";

        public string Id;

        public string Name;

        /// <summary>录制时刻（UTC，ISO 8601）。</summary>
        public string RecordedUtc;

        /// <summary>时长（秒）。</summary>
        public double Duration;

        /// <summary>采样率（帧/秒），P1 的帧流按它写。</summary>
        public int SampleRate = 60;

        /// <summary>采样帧数（P0 只计数，帧流留 P1）。</summary>
        public long Frames;

        public RecordingStartState Start;

        /// <summary>轨道路径（相对包内）。</summary>
        public string InputTrack = "tracks/input.bin";

        public string EventTrack = "tracks/events.json";

        public string KeyframeFile = "keyframes.json";

        /// <summary>P0 说明：帧流尚未写入（P1 填满）。</summary>
        public string Note;

        public PackageValue ToValue()
        {
            PackageValue start = PackageValue.Object();
            if (Start != null)
            {
                start.Set("position", PackageValue.Array(new[]
                {
                    PackageValue.Number(Start.X),
                    PackageValue.Number(Start.Y),
                    PackageValue.Number(Start.Z)
                }));
                start.Set("yaw", PackageValue.Number(Start.YawDegrees));
                start.Set("pitch", PackageValue.Number(Start.PitchDegrees));
                start.Set("worldName", PackageValue.Str(Start.WorldName ?? string.Empty));
                start.Set("playerName", PackageValue.Str(Start.PlayerName ?? string.Empty));
            }

            PackageValue tracks = PackageValue.Object();
            tracks.Set("input", PackageValue.Str(InputTrack));
            tracks.Set("events", PackageValue.Str(EventTrack));

            PackageValue manifest = PackageValue.Object();
            manifest.Set("format", PackageValue.Str(FormatName));
            manifest.Set("version", PackageValue.Number(FormatVersion));
            manifest.Set("id", PackageValue.Str(Id));
            manifest.Set("name", PackageValue.Str(Name));
            manifest.Set("recordedUtc", PackageValue.Str(RecordedUtc));
            manifest.Set("duration", PackageValue.Number(Duration));
            manifest.Set("sampleRate", PackageValue.Number(SampleRate));
            manifest.Set("frames", PackageValue.Number(Frames));
            manifest.Set("startState", start);
            manifest.Set("tracks", tracks);
            manifest.Set("keyframes", PackageValue.Str(KeyframeFile));
            if (!string.IsNullOrEmpty(Note))
                manifest.Set("note", PackageValue.Str(Note));
            return manifest;
        }

        public string ToJson(bool indented = true)
        {
            return ToValue().ToJson(indented);
        }

        public override string ToString()
        {
            return "scat(" + (Id ?? "?") + " " + Duration.ToString("0.00") + "s frames=" + Frames + ")";
        }
    }

    /// <summary>录制起点相对基准（P0 用绝对读数的快照，回放时换算成相对量）。</summary>
    public sealed class RecordingStartState
    {
        public float X;
        public float Y;
        public float Z;
        public float YawDegrees;
        public float PitchDegrees;
        public string WorldName;
        public string PlayerName;

        public override string ToString()
        {
            return "(" + X.ToString("0.0") + "," + Y.ToString("0.0") + "," + Z.ToString("0.0") + ")"
                + " yaw=" + YawDegrees.ToString("0.0") + " pitch=" + PitchDegrees.ToString("0.0")
                + " world=" + (WorldName ?? "?");
        }
    }

    /// <summary>
    /// `.scatpak` 打包（P0：manifest + 空的轨道文件 + 起点关键帧；帧流 P1 填）。
    ///
    /// 与 `.scbtpak` 一样走 <see cref="PackageWriter"/> 的原子写：先写 `.tmp` 再替换，
    /// 于是"正在被读"的旧文件永远不会变成半截。
    /// </summary>
    public static class ScatPackage
    {
        public const string NotesForP0 =
            "P0 recording skeleton: metadata only. The per-frame input track (tracks/input.bin) "
            + "is written by P1; events/keyframes currently contain just the start state.";

        /// <summary>P1 起：包里有真帧流，说明不再是骨架。</summary>
        public const string NotesForP1 =
            "Recorded playback track: tracks/input.bin holds one raw-input frame per recorded frame "
            + "(held/pressed keys, mouse buttons, wheel, look delta); keyframes.json holds "
            + "position/view samples for drift checking; start position is relative-encoded.";

        /// <summary>打成 zip 字节（P0 骨架：只有元数据）。</summary>
        public static byte[] ToBytes(ScatManifest manifest, PackageValue events = null,
            PackageValue keyframes = null)
        {
            return ToBytes(manifest, null, events, keyframes);
        }

        /// <summary>打成 zip 字节（P1：带逐帧轨道）。</summary>
        public static byte[] ToBytes(ScatManifest manifest, ScatTrack.Track track,
            PackageValue events = null, PackageValue keyframes = null)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            bool playable = track != null && track.FrameCount > 0;
            if (string.IsNullOrEmpty(manifest.Note))
                manifest.Note = playable ? NotesForP1 : NotesForP0;

            return WriteArchive(manifest, track, events, keyframes);
        }

        private static byte[] WriteArchive(ScatManifest manifest, ScatTrack.Track track,
            PackageValue events, PackageValue keyframes)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                using (var archive = SuAPI.ZipArchive.Create(stream, keepStreamOpen: true))
                {
                    var utf8 = new System.Text.UTF8Encoding(false);
                    Add(archive, ScatManifest.FileName, utf8.GetBytes(manifest.ToJson(true)));
                    Add(archive, manifest.EventTrack ?? "tracks/events.json",
                        utf8.GetBytes((events ?? EmptyEvents()).ToJson(true)));
                    Add(archive, manifest.KeyframeFile ?? "keyframes.json",
                        utf8.GetBytes((keyframes ?? StartKeyframe(manifest)).ToJson(true)));

                    // P1：逐帧输入轨道（没录到帧就不写这个条目 —— 包会明确"不可回放"）
                    if (track != null && track.FrameCount > 0)
                        Add(archive, manifest.InputTrack ?? "tracks/input.bin", ScatTrack.ToBytes(track));
                }
                return stream.ToArray();
            }
        }

        private static void Add(SuAPI.ZipArchive archive, string name, byte[] data)
        {
            using (var source = new System.IO.MemoryStream(data))
            {
                archive.AddStream(name, source);
            }
        }

        /// <summary>空事件轨（P0）：语义事件（UI 点击/快捷栏切换等）由 P1 填。</summary>
        public static PackageValue EmptyEvents()
        {
            PackageValue root = PackageValue.Object();
            root.Set("format", PackageValue.Str("scat-events"));
            root.Set("version", PackageValue.Number(1));
            root.Set("events", PackageValue.Array());
            return root;
        }

        /// <summary>起点关键帧：回放校验与漂移纠正的基准（P1 每 0.5 s 追加一个）。</summary>
        public static PackageValue StartKeyframe(ScatManifest manifest)
        {
            PackageValue frame = PackageValue.Object();
            frame.Set("t", PackageValue.Number(0));

            PackageValue start = PackageValue.Object();
            if (manifest != null && manifest.Start != null)
            {
                start.Set("position", PackageValue.Array(new[]
                {
                    PackageValue.Number(manifest.Start.X),
                    PackageValue.Number(manifest.Start.Y),
                    PackageValue.Number(manifest.Start.Z)
                }));
                start.Set("yaw", PackageValue.Number(manifest.Start.YawDegrees));
                start.Set("pitch", PackageValue.Number(manifest.Start.PitchDegrees));
            }
            frame.Set("start", start);

            PackageValue root = PackageValue.Object();
            root.Set("format", PackageValue.Str("scat-keyframes"));
            root.Set("version", PackageValue.Number(1));
            root.Set("interval", PackageValue.Number(0.5));
            root.Set("frames", PackageValue.Array(new[] { frame }));
            return root;
        }

        /// <summary>把动作包写到指定路径（原子写）。</summary>
        public static bool TryWrite(string path, ScatManifest manifest, out string error)
        {
            return TryWrite(path, manifest, null, null, null, out error);
        }

        /// <summary>把动作包（含逐帧轨道）写到指定路径（原子写）。</summary>
        public static bool TryWrite(string path, ScatManifest manifest, ScatTrack.Track track,
            PackageValue events, PackageValue keyframes, out string error)
        {
            error = null;
            try
            {
                byte[] bytes = ToBytes(manifest, track, events, keyframes);
                return PackageWriter.TryWriteFile(path, bytes, out error);
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }
    }
}

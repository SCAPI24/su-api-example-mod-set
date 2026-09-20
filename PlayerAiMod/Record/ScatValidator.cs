using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>一个读进内存的动作包（`.scatpak`）：元数据 + 逐帧轨道 + 事件/关键帧。</summary>
    public sealed class ScatActionPackage
    {
        public string Path;
        public string FileName;
        public string Hash;
        public long ByteCount;

        public ScatManifest Manifest;

        /// <summary>逐帧轨道；P0 骨架录的包没有它（<see cref="CanReplay"/> 为 false）。</summary>
        public ScatTrack.Track Track;

        public PackageValue Events;
        public PackageValue Keyframes;

        /// <summary>能不能回放（必须真有逐帧输入流）。</summary>
        public bool CanReplay
        {
            get { return Track != null && Track.FrameCount > 0; }
        }

        public double Duration
        {
            get
            {
                if (Track != null && Track.FrameCount > 0)
                    return Track.DurationSeconds;
                return Manifest != null ? Manifest.Duration : 0.0;
            }
        }

        public int Frames
        {
            get { return Track != null ? Track.FrameCount : (Manifest != null ? (int)Manifest.Frames : 0); }
        }

        /// <summary>轨道里真正用到过的键（回放时按名字注入）。</summary>
        public List<string> UsedKeyNames()
        {
            var used = new SortedSet<string>(StringComparer.Ordinal);
            if (Track == null)
                return new List<string>();

            for (int i = 0; i < Track.Frames.Count; i++)
            {
                RecordingFrame frame = Track.Frames[i];
                AddNames(used, Track, frame.KeysHeld);
                AddNames(used, Track, frame.KeysPressed);
            }
            return new List<string>(used);
        }

        private static void AddNames(SortedSet<string> into, ScatTrack.Track track, byte[] indices)
        {
            if (indices == null)
                return;
            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                if (index >= 0 && index < track.KeyNames.Count)
                    into.Add(track.KeyNames[index]);
            }
        }

        /// <summary>一句话摘要（CLI/控制面/后续编辑器都用它）。</summary>
        public string Describe()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(FileName ?? "?");
            if (Manifest != null)
            {
                builder.Append(" id=").Append(Manifest.Id ?? "?");
                builder.Append(" ").Append(Duration.ToString("0.00")).Append("s/")
                    .Append(Frames).Append(" 帧");
                if (Manifest.Start != null)
                    builder.Append(" world=").Append(Manifest.Start.WorldName ?? "?");
            }
            builder.Append(CanReplay ? " 可回放" : " **不可回放（无逐帧输入流）**");
            if (CanReplay)
            {
                List<string> keys = UsedKeyNames();
                builder.Append(" keys=[").Append(keys.Count > 0 ? string.Join(",", keys.ToArray()) : "-")
                    .Append(']');
            }
            return builder.ToString();
        }

        public override string ToString()
        {
            return "ScatActionPackage(" + Describe() + ")";
        }
    }

    /// <summary>
    /// 动作包校验器（`.scatpak`）：结构 + 语义 + **可回放性**。
    ///
    /// 存在的理由很实在：P0 只落元数据（`tracks/input.bin` 留到 P1），
    /// 所以"这个包能不能回放"必须**明确报告**，而不是回放时才发现没数据。
    /// </summary>
    public static class ScatValidator
    {
        public const string TrackEntry = "tracks/input.bin";
        public const string EventsEntry = "tracks/events.json";
        public const string KeyframesEntry = "keyframes.json";

        /// <summary>读并校验一个动作包文件。</summary>
        public static bool TryLoad(string path, out ScatActionPackage package, out PackageReport report)
        {
            package = null;
            report = new PackageReport();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                report.Error(PackageCodes.FileMissing, path ?? "<none>", "action package not found");
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception exception)
            {
                report.Error(PackageCodes.FileUnreadable, path,
                    exception.GetType().Name + ": " + exception.Message);
                return false;
            }

            return TryParse(bytes, System.IO.Path.GetFileName(path), path, out package, out report);
        }

        /// <summary>从字节读并校验（自检/推送重载用）。</summary>
        public static bool TryParse(byte[] bytes, string fileName, string path,
            out ScatActionPackage package, out PackageReport report)
        {
            package = null;
            report = new PackageReport();

            if (bytes == null || bytes.Length == 0)
            {
                report.Error(PackageCodes.FileUnreadable, fileName ?? "<none>", "package is empty");
                return false;
            }
            if (bytes.Length > 16 * 1024 * 1024)
            {
                report.Error(PackageCodes.FileTooLarge, fileName ?? "<none>",
                    "action package is " + bytes.Length + " bytes (limit 16 MB)");
                return false;
            }

            var loaded = new ScatActionPackage
            {
                Path = path,
                FileName = fileName,
                ByteCount = bytes.Length,
                Hash = PackageLoader.ComputeHash(bytes)
            };

            byte[] manifestBytes = null;
            byte[] trackBytes = null;
            byte[] eventsBytes = null;
            byte[] keyframeBytes = null;

            try
            {
                using (var stream = new MemoryStream(bytes, writable: false))
                using (var archive = SuAPI.ZipArchive.Open(stream, keepStreamOpen: true))
                {
                    List<SuAPI.ZipArchiveEntry> entries = archive.ReadCentralDir();
                    for (int i = 0; i < entries.Count; i++)
                    {
                        string name = (entries[i].FilenameInZip ?? string.Empty).Replace('\\', '/').Trim('/');
                        if (string.Equals(name, ScatManifest.FileName, StringComparison.OrdinalIgnoreCase))
                            manifestBytes = Extract(archive, entries[i], fileName, report);
                        else if (string.Equals(name, TrackEntry, StringComparison.OrdinalIgnoreCase))
                            trackBytes = Extract(archive, entries[i], fileName, report);
                        else if (string.Equals(name, EventsEntry, StringComparison.OrdinalIgnoreCase))
                            eventsBytes = Extract(archive, entries[i], fileName, report);
                        else if (string.Equals(name, KeyframesEntry, StringComparison.OrdinalIgnoreCase))
                            keyframeBytes = Extract(archive, entries[i], fileName, report);
                    }
                }
            }
            catch (Exception exception)
            {
                report.Error(PackageCodes.ZipInvalid, fileName ?? "<none>",
                    "cannot read action package as zip: " + exception.GetType().Name + ": "
                    + exception.Message);
                return false;
            }

            if (manifestBytes == null)
            {
                report.Error(PackageCodes.ZipEntryMissing, fileName ?? "<none>",
                    "action package has no " + ScatManifest.FileName + " at its root");
                return false;
            }

            PackageValue manifestValue;
            if (!PackageJson.TryParseUtf8(manifestBytes, fileName + ":" + ScatManifest.FileName,
                report, out manifestValue))
            {
                return false;
            }
            loaded.Manifest = ReadManifest(manifestValue, fileName, report);

            if (trackBytes != null && trackBytes.Length > 0)
            {
                ScatTrack.Track track;
                string trackError;
                if (ScatTrack.TryParse(trackBytes, out track, out trackError))
                {
                    loaded.Track = track;
                }
                else
                {
                    report.Error(PackageCodes.PropertyValue, fileName + ":" + TrackEntry,
                        "frame track is not readable: " + trackError);
                }
            }
            else
            {
                // P0 骨架录的包就长这样：有元数据、没有逐帧数据。这不是"坏包"，而是"不可回放"。
                report.Warn(PackageCodes.PropertyValue, fileName + ":" + TrackEntry,
                    "no per-frame input track -> this package cannot be replayed "
                    + "(P0 recordings only carried metadata; record again with the P1 build)");
            }

            if (eventsBytes != null)
                PackageJson.TryParseUtf8(eventsBytes, fileName + ":" + EventsEntry, report,
                    out loaded.Events);
            if (keyframeBytes != null)
                PackageJson.TryParseUtf8(keyframeBytes, fileName + ":" + KeyframesEntry, report,
                    out loaded.Keyframes);

            ValidateSemantics(loaded, report);
            package = loaded;
            return !report.HasErrors;
        }

        private static byte[] Extract(SuAPI.ZipArchive archive, SuAPI.ZipArchiveEntry entry,
            string fileName, PackageReport report)
        {
            if (entry.FileSize > PackageJson.MaxBytes * 4)
            {
                report.Error(PackageCodes.FileTooLarge, fileName + ":" + entry.FilenameInZip,
                    "entry is " + entry.FileSize + " bytes");
                return null;
            }
            using (var memory = new MemoryStream())
            {
                archive.ExtractFile(entry, memory);
                return memory.ToArray();
            }
        }

        private static ScatManifest ReadManifest(PackageValue root, string fileName,
            PackageReport report)
        {
            string where = fileName + ":" + ScatManifest.FileName;
            if (root == null || !root.IsObject)
            {
                report.Error(PackageCodes.ManifestNotObject, where, "manifest must be a JSON object");
                return null;
            }

            var manifest = new ScatManifest
            {
                Id = root.Get("id").AsString(null),
                Name = root.Get("name").AsString(null),
                RecordedUtc = root.Get("recordedUtc").AsString(null),
                Duration = root.Get("duration").AsNumber(0.0),
                SampleRate = root.Get("sampleRate").AsInt(ScatManifest.FormatVersion > 0 ? 60 : 60),
                Frames = (long)root.Get("frames").AsNumber(0),
                Note = root.Get("note").AsString(null)
            };

            string format = root.Get("format").AsString(null);
            if (!string.Equals(format, ScatManifest.FormatName, StringComparison.OrdinalIgnoreCase))
            {
                report.Error(PackageCodes.ManifestFormat, where + ".format",
                    "format must be '" + ScatManifest.FormatName + "', found '" + (format ?? "<null>") + "'");
            }

            int version = root.Get("version").AsInt(0);
            if (version != ScatManifest.FormatVersion)
            {
                report.Error(PackageCodes.ManifestVersion, where + ".version",
                    "unsupported format version " + version + " (this build understands "
                    + ScatManifest.FormatVersion + ")");
            }

            if (!PackageJson.IsValidIdentifier(manifest.Id))
            {
                report.Error(PackageCodes.ManifestId, where + ".id",
                    "id must be 1-64 characters of [A-Za-z0-9._-], found '"
                    + (manifest.Id ?? "<null>") + "'");
            }

            PackageValue start = root.Get("startState");
            if (start.IsObject)
            {
                PackageValue position = start.Get("position");
                manifest.Start = new RecordingStartState
                {
                    X = position.Count > 0 ? position.Item(0).AsFloat() : 0f,
                    Y = position.Count > 1 ? position.Item(1).AsFloat() : 0f,
                    Z = position.Count > 2 ? position.Item(2).AsFloat() : 0f,
                    YawDegrees = start.Get("yaw").AsFloat(),
                    PitchDegrees = start.Get("pitch").AsFloat(),
                    WorldName = start.Get("worldName").AsString(null),
                    PlayerName = start.Get("playerName").AsString(null)
                };
            }
            else
            {
                report.Warn(PackageCodes.PropertyRequired, where + ".startState",
                    "no start state: relative playback has no reference point");
            }

            PackageValue tracks = root.Get("tracks");
            if (tracks.IsObject)
            {
                manifest.InputTrack = tracks.Get("input").AsString(TrackEntry);
                manifest.EventTrack = tracks.Get("events").AsString(EventsEntry);
            }

            return manifest;
        }

        private static void ValidateSemantics(ScatActionPackage package, PackageReport report)
        {
            if (package == null)
                return;

            string where = package.FileName ?? "<none>";

            if (package.Manifest != null && package.Manifest.Duration < 0.0)
            {
                report.Error(PackageCodes.PropertyValue, where + ".duration",
                    "duration must be >= 0, found " + package.Manifest.Duration);
            }
            if (package.Manifest != null && package.Manifest.SampleRate <= 0)
            {
                report.Error(PackageCodes.PropertyValue, where + ".sampleRate",
                    "sampleRate must be > 0, found " + package.Manifest.SampleRate);
            }

            if (package.Track == null)
                return;

            // 轨道与元数据要对得上（对不上说明录制/写盘过程有问题）
            double trackDuration = package.Track.DurationSeconds;
            if (package.Manifest != null && package.Manifest.Duration > 0.01
                && Math.Abs(trackDuration - package.Manifest.Duration) > 0.5)
            {
                report.Warn(PackageCodes.PropertyValue, where + ":duration",
                    "manifest says " + package.Manifest.Duration.ToString("0.00")
                    + "s but the track is " + trackDuration.ToString("0.00") + "s");
            }
            if (package.Manifest != null && package.Manifest.Frames > 0
                && package.Manifest.Frames != package.Track.FrameCount)
            {
                report.Warn(PackageCodes.PropertyValue, where + ":frames",
                    "manifest says " + package.Manifest.Frames + " frames but the track has "
                    + package.Track.FrameCount);
            }

            int longGaps = 0;
            int badIndices = 0;
            for (int i = 0; i < package.Track.Frames.Count; i++)
            {
                RecordingFrame frame = package.Track.Frames[i];
                if (frame.DeltaMs > 5000)
                    longGaps++;
                badIndices += CountBadIndices(package.Track, frame.KeysHeld);
                badIndices += CountBadIndices(package.Track, frame.KeysPressed);
            }

            if (longGaps > 0)
            {
                report.Warn(PackageCodes.PropertyValue, where + ":tracks/input.bin",
                    longGaps + " frame(s) have a gap longer than 5 s (a stall during recording?)");
            }
            if (badIndices > 0)
            {
                report.Error(PackageCodes.PropertyValue, where + ":tracks/input.bin",
                    badIndices + " key index(es) are out of the track's name table");
            }

            List<string> keys = package.UsedKeyNames();
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i].StartsWith("<key#", StringComparison.Ordinal))
                {
                    report.Warn(PackageCodes.PropertyValue, where + ":tracks/input.bin",
                        "unnamed key reference " + keys[i]);
                }
            }

            if (package.Track.FrameCount == 0)
            {
                report.Warn(PackageCodes.PropertyValue, where + ":tracks/input.bin",
                    "the track exists but has no frames");
            }
        }

        private static int CountBadIndices(ScatTrack.Track track, byte[] indices)
        {
            if (indices == null)
                return 0;

            int bad = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= track.KeyNames.Count)
                    bad++;
            }
            return bad;
        }

        /// <summary>人类可读的汇总（CLI 与编辑器都用它）。</summary>
        public static Dictionary<string, object> Summarize(ScatActionPackage package)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            if (package == null)
                return info;

            info["file"] = package.FileName;
            info["path"] = package.Path;
            info["hash"] = package.Hash;
            info["bytes"] = package.ByteCount;
            info["id"] = package.Manifest != null ? package.Manifest.Id : null;
            info["duration"] = Math.Round(package.Duration, 3);
            info["frames"] = package.Frames;
            info["replayable"] = package.CanReplay;
            info["keys"] = package.UsedKeyNames();
            if (package.Manifest != null && package.Manifest.Start != null)
            {
                info["world"] = package.Manifest.Start.WorldName;
                info["startPosition"] = new List<object>
                {
                    Math.Round(package.Manifest.Start.X, 2),
                    Math.Round(package.Manifest.Start.Y, 2),
                    Math.Round(package.Manifest.Start.Z, 2)
                };
                info["startYaw"] = Math.Round(package.Manifest.Start.YawDegrees, 2);
            }
            return info;
        }
    }
}

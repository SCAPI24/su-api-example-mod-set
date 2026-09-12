using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 录制会话自检（P0-9）：**纯逻辑、不依赖游戏**（会写临时目录里的真文件）。
    ///
    /// 覆盖：状态机（开始/暂停继续/停止/待命名）、时长与帧数只在录制中累计、
    /// 名字消毒（路径分隔符与非法字符）、同名不覆盖（报 `already_exists`）、覆盖保存、
    /// 丢弃、以及**落盘产物真的是一个合法的 .scatpak**（解 zip 读回 manifest 与起点）。
    /// </summary>
    public static class AiRecordingSelfTest
    {
        private const float Dt = 1f / 60f;

        private const string TempFolderName = "pai-rec-selftest";

        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "AiRecordingSelfTest" };
            string root = null;
            try
            {
                root = Path.Combine(Path.GetTempPath(), TempFolderName);
                if (System.IO.Directory.Exists(root))
                    System.IO.Directory.Delete(root, true);
                System.IO.Directory.CreateDirectory(root);

                StateMachine(result, root);
                Sampling(result, root);
                NamingAndSafety(result, root);
                SaveAndOverwrite(result, root);
                WrittenPackage(result, root);
            }
            catch (Exception exception)
            {
                result.Check("AiRecordingSelfTest no unexpected exception", false,
                    exception.GetType().Name + ": " + exception.Message);
            }
            return result;
        }

        private static void StateMachine(BtSelfTest.TestResult result, string root)
        {
            var session = new AiRecordingSession(root);
            result.Check("recording starts idle",
                session.Status.Phase == RecordingPhase.Idle && !session.IsActive,
                session.Status.ToString());

            string started = session.Start("walk_around");
            result.Check("start moves to recording",
                session.Status.Phase == RecordingPhase.Recording && session.IsActive,
                started);

            string paused = session.TogglePause();
            result.Check("PgUp toggles to paused",
                session.Status.Phase == RecordingPhase.Paused && paused.Contains("paused"), paused);

            session.TogglePause();
            result.Check("PgUp toggles back to recording",
                session.Status.Phase == RecordingPhase.Recording, session.Status.ToString());

            string stopped = session.Stop();
            result.Check("PgDn/stop moves to pending-save",
                session.Status.Phase == RecordingPhase.PendingSave && session.IsActive, stopped);
            result.Check("stop keeps the capture for naming", session.Status.SuggestedName != null,
                "<no suggested name>");

            string again = session.Stop();
            result.Check("stopping twice is harmless", again.Contains("already stopped"), again);

            // 待命名状态下再开始 = 放弃上一段，重新录
            session.Start("second");
            result.Check("starting again replaces the pending capture",
                session.Status.Phase == RecordingPhase.Recording && session.Status.Name == "second",
                session.Status.ToString());

            session.Discard();
            result.Check("discard returns to idle",
                session.Status.Phase == RecordingPhase.Idle && !session.IsActive,
                session.Status.ToString());

            bool threw = false;
            try
            {
                session.TogglePause();
            }
            catch (AiCommandException exception)
            {
                threw = exception.Code == "not_recording";
            }
            result.Check("pausing while idle reports not_recording", threw, "no error raised");
        }

        private static void Sampling(BtSelfTest.TestResult result, string root)
        {
            var session = new AiRecordingSession(root);
            session.Start("timing");

            for (int i = 0; i < 60; i++)
                session.Tick(Dt);
            result.Check("recording accumulates duration and frames",
                session.Status.Frames == 60 && Math.Abs(session.Status.Duration - 1.0) < 0.02,
                session.Status.ToString());

            session.TogglePause();
            for (int i = 0; i < 60; i++)
                session.Tick(Dt);
            result.Check("paused recording does not accumulate",
                session.Status.Frames == 60 && Math.Abs(session.Status.Duration - 1.0) < 0.02,
                session.Status.ToString());

            session.TogglePause();
            for (int i = 0; i < 30; i++)
                session.Tick(Dt);
            result.Check("resumed recording continues counting",
                session.Status.Frames == 90 && session.Status.Duration > 1.4,
                session.Status.ToString());

            session.Stop();
            for (int i = 0; i < 30; i++)
                session.Tick(Dt);
            result.Check("stopped recording does not accumulate",
                session.Status.Frames == 90, session.Status.ToString());

            session.Discard();
        }

        private static void NamingAndSafety(BtSelfTest.TestResult result, string root)
        {
            result.Check("name sanitization strips path separators",
                AiRecordingSession.SanitizeName("../../etc/passwd") == ".._.._etc_passwd"
                    || AiRecordingSession.SanitizeName("../../etc/passwd") == "etc_passwd",
                AiRecordingSession.SanitizeName("../../etc/passwd") ?? "<null>");
            result.Check("name sanitization keeps a safe name untouched",
                AiRecordingSession.SanitizeName("walk_around-1") == "walk_around-1",
                AiRecordingSession.SanitizeName("walk_around-1") ?? "<null>");
            result.Check("name sanitization rejects empty names",
                AiRecordingSession.SanitizeName("   ") == null
                && AiRecordingSession.SanitizeName(null) == null, "<not null>");
            result.Check("name sanitization drops leading/trailing dots",
                AiRecordingSession.SanitizeName(".hidden.") == "hidden",
                AiRecordingSession.SanitizeName(".hidden.") ?? "<null>");
            result.Check("name sanitization keeps non-ASCII names (中文包名要能存下来)",
                AiRecordingSession.SanitizeName("进入游戏") == "进入游戏",
                AiRecordingSession.SanitizeName("进入游戏") ?? "<null>");
            result.Check("name sanitization still blocks path separators in CJK names",
                AiRecordingSession.SanitizeName("进入/游戏") == "进入_游戏",
                AiRecordingSession.SanitizeName("进入/游戏") ?? "<null>");
            result.Check("a CJK display name still gets a legal ASCII machine id",
                AiRecordingSession.AsciiId("进入游戏").StartsWith("action_",
                    StringComparison.Ordinal),
                AiRecordingSession.AsciiId("进入游戏"));
            result.Check("an ASCII name keeps its own id",
                AiRecordingSession.AsciiId("walk_around-1") == "walk_around-1",
                AiRecordingSession.AsciiId("walk_around-1"));
            result.Check("long names are truncated",
                (AiRecordingSession.SanitizeName(new string('a', 200)) ?? string.Empty).Length
                == AiRecordingSession.MaxNameLength,
                "length=" + (AiRecordingSession.SanitizeName(new string('a', 200)) ?? "").Length);
            result.Check("suggested name carries a timestamp",
                AiRecordingSession.SuggestName(new DateTime(2026, 9, 12, 10, 15, 30, DateTimeKind.Utc))
                == "action_20260912_101530",
                AiRecordingSession.SuggestName());

            var session = new AiRecordingSession(root);
            bool noCapture = false;
            try
            {
                session.Save("nope", false);
            }
            catch (AiCommandException exception)
            {
                noCapture = exception.Code == "not_recording";
            }
            result.Check("saving without a finished recording reports not_recording", noCapture,
                "no error raised");

            var noDirectory = new AiRecordingSession(null);
            noDirectory.Start("x");
            noDirectory.Stop();
            bool noFolder = false;
            try
            {
                noDirectory.Save("x", false);
            }
            catch (AiCommandException exception)
            {
                noFolder = exception.Code == "not_ready";
            }
            result.Check("saving without a target folder reports not_ready", noFolder,
                "no error raised");
        }

        private static void SaveAndOverwrite(BtSelfTest.TestResult result, string root)
        {
            var session = new AiRecordingSession(root);
            session.StartState = new RecordingStartState
            {
                X = 10f,
                Y = 20f,
                Z = 30f,
                YawDegrees = 90f,
                PitchDegrees = -15f,
                WorldName = "Rebritish",
                PlayerName = "selftest"
            };
            session.Start("save_me");
            for (int i = 0; i < 120; i++)
                session.Tick(Dt);
            session.Stop();

            string path = session.Save("save_me", false);
            result.Check("saving writes a .scatpak next to the tree packages",
                File.Exists(Path.Combine(root, "save_me" + ScatManifest.Extension)),
                path);
            result.Check("saving returns to idle",
                session.Status.Phase == RecordingPhase.Idle && !session.IsActive,
                session.Status.ToString());

            bool exists = false;
            try
            {
                // 第二段也真录一点：覆盖写下去的内容要能和断言对上
                session.Start("save_me");
                for (int i = 0; i < 30; i++)
                    session.Tick(Dt);
                session.Stop();
                session.Save("save_me", false);
            }
            catch (AiCommandException exception)
            {
                exists = exception.Code == "already_exists";
            }
            result.Check("saving over an existing file needs overwrite=true", exists,
                "no already_exists error");

            string overwritten = session.Save("save_me", true);
            result.Check("overwrite=true replaces the file",
                overwritten.EndsWith("save_me" + ScatManifest.Extension, StringComparison.Ordinal),
                overwritten);
            session.Discard();
        }

        private static void WrittenPackage(BtSelfTest.TestResult result, string root)
        {
            string path = Path.Combine(root, "save_me" + ScatManifest.Extension);
            if (!File.Exists(path))
            {
                result.Check("written package exists for verification", false, path);
                return;
            }

            byte[] bytes = File.ReadAllBytes(path);
            result.Check("package bytes are a zip (PK header)", bytes.Length > 4
                && bytes[0] == 0x50 && bytes[1] == 0x4B, "length=" + bytes.Length);

            var report = new PackageReport();
            PackageValue manifestValue = null;
            bool read = false;
            try
            {
                using (var stream = new MemoryStream(bytes, writable: false))
                using (var archive = SuAPI.ZipArchive.Open(stream, keepStreamOpen: true))
                {
                    List<SuAPI.ZipArchiveEntry> entries = archive.ReadCentralDir();
                    for (int i = 0; i < entries.Count; i++)
                    {
                        string name = (entries[i].FilenameInZip ?? string.Empty).Replace('\\', '/').Trim('/');
                        if (!string.Equals(name, ScatManifest.FileName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        using (var memory = new MemoryStream())
                        {
                            archive.ExtractFile(entries[i], memory);
                            read = PackageJson.TryParseUtf8(memory.ToArray(), name, report,
                                out manifestValue);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                result.Check("package can be reopened as a zip", false,
                    exception.GetType().Name + ": " + exception.Message);
                return;
            }

            result.Check("package contains a readable manifest.json", read && manifestValue != null,
                report.Summary());

            PackageValue manifest = manifestValue;
            result.Check("manifest declares the scat format",
                manifest != null && manifest.Get("format").AsString(null) == ScatManifest.FormatName
                && manifest.Get("version").AsInt() == ScatManifest.FormatVersion,
                manifest != null ? manifest.Preview(80) : "<null>");
            // 文件里是"覆盖保存"那一段（30 帧 ≈ 0.5 s），不是最初那一段
            result.Check("manifest carries duration and frames",
                manifest != null && Math.Abs(manifest.Get("duration").AsFloat() - 0.5f) < 0.05f
                && manifest.Get("frames").AsInt() == 30,
                manifest != null
                    ? manifest.Get("duration").AsFloat().ToString("0.000") + "s frames="
                      + manifest.Get("frames").AsInt()
                    : "?");
            result.Check("manifest records the start state (relative playback needs it)",
                manifest != null
                && Math.Abs(manifest.Get("startState").Get("yaw").AsFloat() - 90f) < 0.01f
                && manifest.Get("startState").Get("worldName").AsString(null) == "Rebritish",
                manifest != null ? manifest.Get("startState").Preview(80) : "<null>");
        }
    }
}

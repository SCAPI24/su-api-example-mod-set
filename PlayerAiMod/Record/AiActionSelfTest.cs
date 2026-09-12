using Engine;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 动作包自检（P1）：**纯逻辑、不依赖游戏**（写临时目录里的真文件）。
    ///
    /// 覆盖四件事：
    ///   1. **轨道格式**：写出去再读回来，帧数/键名/按键序列/视角增量/滚轮都不变（自描述名表）；
    ///   2. **校验器**：好包 → 可回放；P0 骨架包（只有元数据）→ 明确"不可回放"而不是静默通过；坏 zip/坏帧 → 报错；
    ///   3. **回放**：按录制节奏驱动执行器 —— 按住类保持、按下类只按一次、鼠标/滚轮/视角还原，
    ///      放完 Succeeded 且**释放输入**；执行器拒绝的键如实报出来；
    ///   4. **漂移**：位置/朝向偏离关键帧超阈值 → **Failed**（交回行为树，绝不做瞬移修正）；
    ///   5. **行为树集成**：`Task.PlayActionPackage` 真能播放同路径的动作包；包缺失 → Failed。
    ///
    /// 另外验证**我自己造的动作包**（`sample_walk`）与**测试树**（`test.action`）——
    /// 它们是出厂内容，任何一次运行的数据都完全确定。
    /// </summary>
    public static class AiActionSelfTest
    {
        private const float Dt = 1f / 60f;

        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "AiActionSelfTest" };
            string root = null;
            try
            {
                root = Path.Combine(Path.GetTempPath(), "pai-action-selftest");
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
                Directory.CreateDirectory(root);

                TrackRoundTrip(result, root);
                Validator(result, root);
                Replay(result, root);
                Drift(result, root);
                UiEvents(result, root);
                BehaviourTreeIntegration(result, root);
                SampleContent(result, root);
            }
            catch (Exception exception)
            {
                result.Check("AiActionSelfTest no unexpected exception", false,
                    exception.GetType().Name + ": " + exception.Message);
            }
            return result;
        }

        // ---------------------------------------------------------------- 1) 轨道格式

        private static void TrackRoundTrip(BtSelfTest.TestResult result, string root)
        {
            ScatTrack.Track track = PlayerAiPackages.BuildSampleTrack();
            byte[] bytes = ScatTrack.ToBytes(track);

            result.Check("sample track has the expected shape",
                track.FrameCount == 120 && Math.Abs(track.DurationSeconds - 2.0) < 0.02,
                "frames=" + track.FrameCount + " duration=" + track.DurationSeconds.ToString("0.000"));
            result.Check("track bytes are compact",
                bytes.Length < 120 * 64, bytes.Length + " bytes for " + track.FrameCount + " frames");

            ScatTrack.Track read;
            string error;
            result.Check("track bytes parse back",
                ScatTrack.TryParse(bytes, out read, out error), error ?? "<ok>");

            if (read == null)
                return;

            result.Check("round-trip keeps frame count and key table",
                read.FrameCount == track.FrameCount && read.KeyNames.Count == track.KeyNames.Count,
                "frames=" + read.FrameCount + " keys=" + read.KeyNames.Count);

            bool sameKeys = true;
            for (int i = 0; i < track.FrameCount; i++)
            {
                RecordingFrame a = track.Frames[i];
                RecordingFrame b = read.Frames[i];
                if (a.KeysHeld.Length != b.KeysHeld.Length || a.KeysPressed.Length != b.KeysPressed.Length)
                {
                    sameKeys = false;
                    break;
                }
                for (int k = 0; k < a.KeysHeld.Length; k++)
                {
                    if (a.KeysHeld[k] != b.KeysHeld[k])
                        sameKeys = false;
                }
                for (int k = 0; k < a.KeysPressed.Length; k++)
                {
                    if (a.KeysPressed[k] != b.KeysPressed[k])
                        sameKeys = false;
                }
                if (!sameKeys)
                    break;
            }
            result.Check("round-trip keeps every frame's keys", sameKeys, "key mismatch");

            RecordingFrame lookFrame = read.Frames[70];
            result.Check("round-trip keeps the look delta",
                Math.Abs(lookFrame.LookDeltaX - 0.02f) < 1e-6f, lookFrame.LookDeltaX.ToString("0.0000"));

            RecordingFrame jumpFrame = read.Frames[(int)(1.4 * 60)];
            result.Check("round-trip keeps the pressed-key edge",
                jumpFrame.KeysPressed.Length == 1 && read.KeyNames[jumpFrame.KeysPressed[0]] == "Space",
                "pressed=" + jumpFrame.KeysPressed.Length);

            result.Check("sample package declares the keys it uses",
                read.AllKeyNames().Count == 3, string.Join(",", read.AllKeyNames().ToArray()));

            // 坏数据必须报错，而不是"半个包也当好的"
            byte[] truncated = new byte[bytes.Length - 3];
            Array.Copy(bytes, truncated, truncated.Length);
            ScatTrack.Track ignored;
            string truncatedError;
            result.Check("a truncated track is rejected",
                !ScatTrack.TryParse(truncated, out ignored, out truncatedError)
                && !string.IsNullOrEmpty(truncatedError), truncatedError ?? "<no error>");

            string magicError;
            byte[] wrongMagic = (byte[])bytes.Clone();
            wrongMagic[0] = (byte)'X';
            result.Check("a wrong magic is rejected",
                !ScatTrack.TryParse(wrongMagic, out ignored, out magicError)
                && magicError.Contains("magic"), magicError ?? "<no error>");
        }

        // ---------------------------------------------------------------- 2) 校验器

        private static void Validator(BtSelfTest.TestResult result, string root)
        {
            // 好包（我自己造的）
            string goodPath = Path.Combine(root, PlayerAiPackages.SampleActionName + ScatManifest.Extension);
            PackageWriter.TryWriteFile(goodPath, PlayerAiPackages.BuildSampleActionPackage(), out string error);

            ScatActionPackage package;
            PackageReport report;
            bool ok = ScatValidator.TryLoad(goodPath, out package, out report);
            result.Check("validator accepts my own action package",
                ok && package != null && package.CanReplay && report.ErrorCount == 0,
                report.Summary() + " :: " + (package != null ? package.Describe() : "<null>"));
            result.Check("validator reports duration/frames from the manifest and track",
                package != null && package.Frames == 120
                && Math.Abs(package.Duration - 2.0) < 0.05,
                package == null ? "<null>" : package.Frames + " frames / "
                    + package.Duration.ToString("0.00") + "s");
            result.Check("validator lists the keys used for replay",
                package != null && package.UsedKeyNames().Count == 3,
                package == null ? "<null>" : string.Join(",", package.UsedKeyNames().ToArray()));

            // P0 骨架包：只有元数据 → 不是坏包，但**不可回放**（这正是用户现在那个包的样子）
            var manifest = new ScatManifest
            {
                Id = "skeleton",
                Name = "skeleton",
                RecordedUtc = "2026-01-01T00:00:00Z",
                Duration = 5.795,
                Frames = 1043,
                Start = new RecordingStartState { WorldName = "Rebirth", PlayerName = "host" }
            };
            string skeletonPath = Path.Combine(root, "skeleton" + ScatManifest.Extension);
            PackageWriter.TryWriteFile(skeletonPath, ScatPackage.ToBytes(manifest), out error);

            ScatActionPackage skeleton;
            PackageReport skeletonReport;
            bool skeletonOk = ScatValidator.TryLoad(skeletonPath, out skeleton, out skeletonReport);
            result.Check("a P0 skeleton package validates but is reported as NOT replayable",
                skeletonOk && skeleton != null && !skeleton.CanReplay
                && skeletonReport.ErrorCount == 0 && skeletonReport.WarningCount > 0,
                skeletonReport.Summary() + " :: " + (skeleton != null ? skeleton.Describe() : "<null>"));
            result.Check("the skeleton warning explains why",
                skeletonReport.HasCode(PackageCodes.PropertyValue)
                && skeletonReport.Summarize(4).Exists(line => line.Contains("cannot be replayed")),
                skeletonReport.Summarize(4).Count > 0 ? skeletonReport.Summarize(4)[0] : "<none>");

            // 坏文件
            string brokenPath = Path.Combine(root, "broken" + ScatManifest.Extension);
            File.WriteAllBytes(brokenPath, new byte[] { 1, 2, 3, 4, 5 });
            ScatActionPackage broken;
            PackageReport brokenReport;
            result.Check("a non-zip file is rejected",
                !ScatValidator.TryLoad(brokenPath, out broken, out brokenReport)
                && brokenReport.HasErrors, brokenReport.Summary());

            // 缺 manifest
            string noManifestPath = Path.Combine(root, "nomanifest" + ScatManifest.Extension);
            PackageWriter.TryWriteFile(noManifestPath, BuildZipWithoutManifest(), out error);
            ScatActionPackage noManifest;
            PackageReport noManifestReport;
            result.Check("a package without manifest.json is rejected",
                !ScatValidator.TryLoad(noManifestPath, out noManifest, out noManifestReport)
                && noManifestReport.HasErrors, noManifestReport.Summary());

            // 库：列目录
            List<Dictionary<string, object>> described = ScatLibrary.Describe(root);
            result.Check("action library lists the packages it finds",
                described.Count >= 2, "count=" + described.Count);
        }

        /// <summary>造一个合法 zip、但没有 manifest.json（用来验证"缺清单"这条错误路径）。</summary>
        private static byte[] BuildZipWithoutManifest()
        {
            using (var stream = new MemoryStream())
            {
                using (var archive = SuAPI.ZipArchive.Create(stream, keepStreamOpen: true))
                {
                    byte[] payload = System.Text.Encoding.UTF8.GetBytes("{\"format\":\"scat-keyframes\"}");
                    using (var source = new MemoryStream(payload))
                        archive.AddStream("keyframes.json", source);
                }
                return stream.ToArray();
            }
        }

        // ---------------------------------------------------------------- 3) 回放

        private static void Replay(BtSelfTest.TestResult result, string root)
        {
            string path = Path.Combine(root, PlayerAiPackages.SampleActionName + ScatManifest.Extension);
            ScatActionPackage package;
            PackageReport report;
            ScatValidator.TryLoad(path, out package, out report);

            var actuator = new BtTestActuator();
            var player = new ScatPlayer(package, actuator);
            player.Start();

            result.Check("replay starts and reports playing state",
                player.State == ScatPlayState.Playing && player.FrameIndex == 0,
                player.Describe());

            bool sawW = false;
            bool sawLook = false;
            var heldOverTime = new List<bool>();
            int steps = 0;
            while (player.State == ScatPlayState.Playing && steps++ < 600)
            {
                player.Tick(Dt);
                if (actuator.HeldKeys.Contains("W"))
                    sawW = true;
                if (actuator.LookDeltaCalls > 0)
                    sawLook = true;
                heldOverTime.Add(actuator.HeldKeys.Count > 0);
            }

            result.Check("replay reaches the end in about the recorded time",
                player.State == ScatPlayState.Finished
                && Math.Abs(player.Elapsed - 2.0) < 0.35,
                player.Describe());
            result.Check("replay holds the recorded movement key", sawW, "W was never held");
            result.Check("replay applies the recorded look delta",
                sawLook && Math.Abs(actuator.LookDeltaYaw - 0.48f) < 0.02f,
                "lookYaw=" + actuator.LookDeltaYaw.ToString("0.000") + " calls="
                + actuator.LookDeltaCalls);
            result.Check("replay releases everything when finished",
                actuator.HeldKeys.Count == 0 && actuator.ReleaseAllCalls > 0,
                "held=" + actuator.HeldKeys.Count + " releaseAll=" + actuator.ReleaseAllCalls);
            result.Check("replay pulses the pressed key exactly once",
                CountPulses(actuator, "Space") == 1,
                "pulses=" + CountPulses(actuator, "Space"));
            result.Check("replay keeps input applied for most of the run",
                CountTrue(heldOverTime) > heldOverTime.Count / 2,
                "held frames=" + CountTrue(heldOverTime) + "/" + heldOverTime.Count);

            // 一个"执行器拒绝按键"的场景：必须如实报出来，不能静默丢
            var refusing = new BtTestActuator { RejectKeys = true };
            var refusingPlayer = new ScatPlayer(package, refusing);
            refusingPlayer.Start();
            for (int i = 0; i < 30 && refusingPlayer.State == ScatPlayState.Playing; i++)
                refusingPlayer.Tick(Dt);
            refusingPlayer.Stop("test");
            result.Check("rejected keys are recorded as missing (no silent input loss)",
                refusing.RejectedKeyCount > 0, "rejected=" + refusing.RejectedKeyCount);

            // 没有轨道的包不能播
            var skeleton = new ScatActionPackage { Manifest = new ScatManifest { Id = "x" } };
            var noTrackPlayer = new ScatPlayer(skeleton, new BtTestActuator());
            noTrackPlayer.Start();
            result.Check("a package without a track fails instead of pretending to play",
                noTrackPlayer.State == ScatPlayState.Failed
                && (noTrackPlayer.LastError ?? string.Empty).Contains("cannot replay"),
                noTrackPlayer.LastError ?? "<none>");
        }

        private static int CountPulses(BtTestActuator actuator, string key)
        {
            int count = 0;
            for (int i = 0; i < actuator.PulsedKeys.Count; i++)
            {
                if (string.Equals(actuator.PulsedKeys[i], key, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private static int CountTrue(List<bool> values)
        {
            int count = 0;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i])
                    count++;
            }
            return count;
        }

        // ---------------------------------------------------------------- 4) 漂移

        private static void Drift(BtSelfTest.TestResult result, string root)
        {
            // 用一条"位置会漂"的样例：关键帧都在 (0,64,0)，传感器却越走越远
            var track = new ScatTrack.Track();
            byte keyW = (byte)track.EnsureKey("W");
            for (int i = 0; i < 120; i++)
            {
                track.Frames.Add(new RecordingFrame
                {
                    DeltaMs = i == 0 ? 0 : 16,
                    KeysHeld = new[] { keyW }
                });
            }

            var manifest = new ScatManifest
            {
                Id = "drift",
                Name = "drift",
                Duration = 2.0,
                Frames = 120,
                Start = new RecordingStartState { WorldName = "test" }
            };

            PackageValue keyframes = PackageValue.Object();
            keyframes.Set("format", PackageValue.Str("scat-keyframes"));
            keyframes.Set("version", PackageValue.Number(1));
            keyframes.Set("interval", PackageValue.Number(0.5));
            PackageValue frames = PackageValue.Array();
            for (double t = 0.0; t <= 2.0; t += 0.5)
            {
                PackageValue frame = PackageValue.Object();
                frame.Set("t", PackageValue.Number(t));
                PackageValue state = PackageValue.Object();
                state.Set("position", PackageValue.Array(new[]
                {
                    PackageValue.Number(0.0), PackageValue.Number(64.0), PackageValue.Number(0.0)
                }));
                state.Set("yaw", PackageValue.Number(0.0));
                frame.Set("start", state);
                frames.Add(frame);
            }
            keyframes.Set("frames", frames);

            string path = Path.Combine(root, "drift" + ScatManifest.Extension);
            PackageWriter.TryWriteFile(path,
                ScatPackage.ToBytes(manifest, track, ScatPackage.EmptyEvents(), keyframes),
                out string error);

            ScatActionPackage package;
            PackageReport report;
            ScatValidator.TryLoad(path, out package, out report);

            var actuator = new BtTestActuator();
            var sensor = new BtTestSensor();
            sensor.SetTarget(new AiActorView
            {
                Name = "anchor",
                Position = new Vector3(0f, 64f, 0f),
                Distance = 0f
            });
            // 假的"漂移"：让传感器的位置随回放推进越走越远
            var driftingSensor = new DriftingSensor();
            var player = new ScatPlayer(package, actuator, driftingSensor);

            result.Check("drift fixture loads a replayable package", package != null && package.CanReplay,
                report.Summary());

            player.Start();
            int steps = 0;
            while (player.State == ScatPlayState.Playing && steps++ < 600)
            {
                driftingSensor.Advance(Dt);
                player.Tick(Dt);
            }

            result.Check("drifting beyond the keyframe tolerance fails the replay",
                player.State == ScatPlayState.Failed
                && player.LastDrift.Exceeded
                && (player.LastError ?? string.Empty).Contains("drift"),
                player.Describe() + " :: " + player.LastDrift.Describe());
            result.Check("a failed replay releases input (no ghost walking)",
                actuator.HeldKeys.Count == 0 && actuator.ReleaseAllCalls > 0,
                "held=" + actuator.HeldKeys.Count);
            result.Check("drift failure does not correct the position (no teleport-style fixes)",
                actuator.LookAtCalls == 0 && driftingSensor.Position.X > 3.0,
                "lookAtCalls=" + actuator.LookAtCalls + " sensor x="
                + driftingSensor.Position.X.ToString("0.00"));
        }

        /// <summary>位置随回放时间线性远离原点的假传感器（制造可控漂移）。</summary>
        private sealed class DriftingSensor : IAiSensor
        {
            private float m_elapsed;

            public bool IsReady
            {
                get { return true; }
            }

            public bool IsInputAccepted
            {
                get { return true; }
            }

            public string PlayerName
            {
                get { return "drifter"; }
            }

            public Vector3 Position
            {
                get { return new Vector3(m_elapsed * 4f, 64f, 0f); }
            }

            public Vector3 EyePosition
            {
                get { return Position; }
            }

            public Vector3 Velocity
            {
                get { return Vector3.Zero; }
            }

            public float YawRadians
            {
                get { return 0f; }
            }

            public float Health
            {
                get { return 1f; }
            }

            public bool TryGetPitch(out float pitchRadians)
            {
                pitchRadians = 0f;
                return true;
            }

            public bool TryFindPlayer(string name, out AiActorView view)
            {
                view = default(AiActorView);
                return false;
            }

            public bool TryFindNearestPlayer(out AiActorView view)
            {
                view = default(AiActorView);
                return false;
            }

            /// <summary>回放推进时把时间喂进来（测试用）。</summary>
            public void Advance(float deltaSeconds)
            {
                m_elapsed += deltaSeconds;
            }
        }

        // ---------------------------------------------------------------- 4.5) UI 事件（菜单点击）回放

        /// <summary>
        /// 「进入游戏」这类包整段都在菜单里：菜单点击在**原始输入层没有痕迹**（走引擎软光标），
        /// 所以它是以 `ui.click` 语义事件记录的。这一节验证：事件能读出来、按时间点重放、
        /// 而且不影响逐帧输入通道。
        /// </summary>
        private static void UiEvents(BtSelfTest.TestResult result, string root)
        {
            var manifest = new ScatManifest
            {
                Id = "ui_fixture",
                Name = "ui_fixture",
                Duration = 1.0,
                SampleRate = 60,
                Frames = 60,
                Start = new RecordingStartState { WorldName = "menu", PlayerName = "none" }
            };

            var track = new ScatTrack.Track();
            byte keyW = (byte)track.EnsureKey("W");
            for (int i = 0; i < 60; i++)
            {
                var frame = new RecordingFrame { DeltaMs = 17 };
                frame.KeysHeld = i < 30 ? new[] { keyW } : new byte[0];
                track.Frames.Add(frame);
            }

            PackageValue events = PackageValue.Object();
            events.Set("format", PackageValue.Str("scat-events"));
            events.Set("version", PackageValue.Number(1));
            PackageValue list = PackageValue.Array();
            PackageValue first = PackageValue.Object();
            first.Set("t", PackageValue.Number(0.2));
            first.Set("kind", PackageValue.Str("ui.click"));
            first.Set("detail", PackageValue.Str("Play"));
            list.Add(first);
            PackageValue second = PackageValue.Object();
            second.Set("t", PackageValue.Number(0.6));
            second.Set("kind", PackageValue.Str("ui.click"));
            second.Set("detail", PackageValue.Str("1010.6,64.8"));
            list.Add(second);
            events.Set("events", list);

            string path = Path.Combine(root, "ui_fixture" + ScatManifest.Extension);
            PackageWriter.TryWriteFile(path, ScatPackage.ToBytes(manifest, track, events,
                ScatPackage.StartKeyframe(manifest)), out string error);

            ScatActionPackage package;
            PackageReport report;
            ScatValidator.TryLoad(path, out package, out report);
            result.Check("a package can carry UI click events", package != null, report.Summary());
            if (package == null)
                return;

            var actuator = new BtTestActuator();
            var player = new ScatPlayer(package, actuator);
            player.Start();
            int steps = 0;
            while (player.State == ScatPlayState.Playing && steps++ < 200)
                player.Tick(Dt);

            result.Check("the replay performed the recorded UI clicks in order",
                actuator.UiClicks.Count == 2 && actuator.UiClicks[0] == "Play"
                && actuator.UiClicks[1] == "1010.6,64.8",
                string.Join(" | ", actuator.UiClicks.ToArray()));
            result.Check("UI clicks are reported for diagnostics",
                player.PerformedUiClicks.Count == 2, player.Describe());
            result.Check("UI events do not disturb the per-frame input channel",
                actuator.HeldKeysSeen.Contains("W") && actuator.HeldKeys.Count == 0,
                "held=" + actuator.HeldKeys.Count + " seen=" + actuator.HeldKeysSeen.Count);

            UiClickRetry(result, root);
        }

        /// <summary>
        /// UI 点击被引擎拒绝时必须**重试**，而且**顺序不能乱**。
        ///
        /// 实测来源（2026-09-12）：进游戏包里的"点世界列表"落在 Play 屏的切场动画中间，
        /// 引擎如实回 `rejected (missing/occluded)`；上一版直接把这步丢掉 ——
        /// 于是"选世界"没了，后面的 `Play!` 点在一个没有选中世界的界面上，
        /// 整条链看起来就是"跑完了但什么也没发生"。这里用假执行器把那两种情况钉住：
        ///   · 拒绝几次后会成功 → 必须重试到成功，并且**后面的点击要等它**；
        ///   · 一直点不到 → 如实记错误、**不能假装点过**，回放本身不崩。
        /// </summary>
        private static void UiClickRetry(BtSelfTest.TestResult result, string root)
        {
            // ---- ① 拒绝 3 次后成功：后面的点击必须等它（顺序铁律）
            ScatActionPackage package = BuildUiFixture(root, "ui_retry",
                new[] { 0.05, 0.10 }, new[] { "Play", "list:WorldsList@Rebritish" });
            if (package == null)
            {
                result.Check("ui retry fixture builds", false, "fixture missing");
                return;
            }

            var actuator = new BtTestActuator { UiClicksToReject = 3 };
            var player = new ScatPlayer(package, actuator);
            player.Start();
            int steps = 0;
            while (player.State == ScatPlayState.Playing && steps++ < 400)
                player.Tick(Dt);

            int firstSucceeded = actuator.UiClickSucceeded.IndexOf("Play");
            int secondAttempt = actuator.UiClicks.IndexOf("list:WorldsList@Rebritish");
            result.Check("a rejected UI click is retried until it lands",
                firstSucceeded == 0 && actuator.UiClicks.Count >= 4,
                "attempts=" + actuator.UiClicks.Count + " ok=" + actuator.UiClickSucceeded.Count);
            result.Check("a later UI click waits for the pending one (order kept)",
                secondAttempt > firstSucceeded && secondAttempt >= 0
                && actuator.UiClickSucceeded.Count == 2
                && actuator.UiClickSucceeded[1] == "list:WorldsList@Rebritish",
                "succeeded=" + string.Join(" | ", actuator.UiClickSucceeded.ToArray()));
            result.Check("retried clicks are reported as performed (not as attempts)",
                player.PerformedUiClicks.Count == 2, player.Describe());

            // ---- ② 一直点不到：如实记错误，但绝不假装点过
            ScatActionPackage ghost = BuildUiFixture(root, "ui_ghost",
                new[] { 0.05 }, new[] { "Ghost" });
            if (ghost == null)
            {
                result.Check("ui ghost fixture builds", false, "fixture missing");
                return;
            }

            var ghostActuator = new BtTestActuator();
            ghostActuator.UiClickAlwaysFails.Add("Ghost");
            var ghostPlayer = new ScatPlayer(ghost, ghostActuator) { UiClickRetrySeconds = 0.2 };
            ghostPlayer.Start();
            steps = 0;
            while (ghostPlayer.State == ScatPlayState.Playing && steps++ < 400)
                ghostPlayer.Tick(Dt);

            result.Check("a never-ready UI click is retried, then reported honestly",
                ghostActuator.UiClicks.Count > 1 && ghostPlayer.PerformedUiClicks.Count == 0
                && ghostPlayer.LastError != null && ghostPlayer.LastError.Contains("Ghost"),
                "attempts=" + ghostActuator.UiClicks.Count
                + " performed=" + ghostPlayer.PerformedUiClicks.Count
                + " error=" + (ghostPlayer.LastError ?? "<null>"));
        }

        /// <summary>造一个只带 UI 事件的短包（回放用，不关心输入轨道）。</summary>
        private static ScatActionPackage BuildUiFixture(string root, string id, double[] times,
            string[] targets)
        {
            var manifest = new ScatManifest
            {
                Id = id,
                Name = id,
                Duration = 0.5,
                SampleRate = 60,
                Frames = 30,
                Start = new RecordingStartState { WorldName = "menu", PlayerName = "none" }
            };

            var track = new ScatTrack.Track();
            for (int i = 0; i < 30; i++)
                track.Frames.Add(new RecordingFrame { DeltaMs = 17 });

            PackageValue events = PackageValue.Object();
            events.Set("format", PackageValue.Str("scat-events"));
            events.Set("version", PackageValue.Number(1));
            PackageValue list = PackageValue.Array();
            for (int i = 0; i < targets.Length && i < times.Length; i++)
            {
                PackageValue entry = PackageValue.Object();
                entry.Set("t", PackageValue.Number(times[i]));
                entry.Set("kind", PackageValue.Str("ui.click"));
                entry.Set("detail", PackageValue.Str(targets[i]));
                list.Add(entry);
            }
            events.Set("events", list);

            string path = Path.Combine(root, id + ScatManifest.Extension);
            if (!PackageWriter.TryWriteFile(path, ScatPackage.ToBytes(manifest, track, events,
                    ScatPackage.StartKeyframe(manifest)), out string error))
            {
                return null;
            }

            ScatActionPackage package;
            PackageReport report;
            ScatValidator.TryLoad(path, out package, out report);
            return package;
        }

        // ---------------------------------------------------------------- 5) 行为树集成

        private static void BehaviourTreeIntegration(BtSelfTest.TestResult result, string root)
        {
            // 把示例包与测试树放到同一个目录（动作包按"与树包同路径"解析）
            string directory = Path.Combine(root, "trees");
            Directory.CreateDirectory(directory);

            string treePath = Path.Combine(directory, PlayerAiPackages.TestTreeName + PackageRoots.Extension);
            PackageTemplate template = PlayerAiPackages.BuildTestTreeTemplate();
            PackageWriter.TryWritePackage(treePath, template.Manifest, template.Tree, null, out string error);

            string actionPath = Path.Combine(directory, PlayerAiPackages.SampleActionName + ScatManifest.Extension);
            PackageWriter.TryWriteFile(actionPath, PlayerAiPackages.BuildSampleActionPackage(), out error);

            var roots = new PackageRoots(directory);
            var options = new PackageLoadOptions { Roots = roots };
            ScbtPackageSet set = PackageLoader.Load(treePath, options);
            result.Check("the test tree package loads and validates",
                set.Root != null && !set.HasErrors, set.Describe());

            CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
            result.Check("the test tree compiles",
                !compiled.HasErrors && compiled.Root != null && compiled.NodeCount == 5,
                compiled.Describe() + " " + compiled.Report.Summary());

            var playTask = compiled.Root.Find("play") as BtPlayActionPackageTask;
            result.Check("the test tree contains a PlayActionPackage task pointing at my sample",
                playTask != null && playTask.Packages.Count == 1
                && playTask.Packages[0] == PlayerAiPackages.SampleActionName,
                playTask == null ? "<missing>" : playTask.ToString());

            var actuator = new BtTestActuator();
            var runtime = new BtRuntime(new AiBlackboard(), new BtTestSensor(), actuator);
            runtime.SetRoot(compiled.Root, compiled.PackageId, compiled.SourcePath, compiled.SourceHash);
            runtime.Start();

            int steps = 0;
            while (runtime.CompletedLoops == 0 && steps++ < 1200)
                runtime.Tick(Dt);

            result.Check("the test tree runs to completion (action package replayed)",
                runtime.CompletedLoops >= 1 && runtime.LastError == null,
                "loops=" + runtime.CompletedLoops + " last=" + runtime.LastResult
                + " error=" + (runtime.LastError ?? "<none>"));
            result.Check("replaying through the tree actually held the recorded key",
                actuator.HeldKeysSeen.Contains("W") && actuator.ReleaseAllCalls > 0,
                "held seen=" + string.Join(",", actuator.HeldKeysSeen.ToArray()));

            // 包缺失 → 节点失败（交回行为树），而不是静默成功
            string missingTreePath = Path.Combine(directory, "missing.scbtpak");
            PackageTemplate missing = PlayerAiPackages.BuildTestTreeTemplate("no_such_action");
            PackageWriter.TryWritePackage(missingTreePath, missing.Manifest, missing.Tree, null, out error);
            ScbtPackageSet missingSet = PackageLoader.Load(missingTreePath, options);
            CompiledTree missingCompiled = TreeCompiler.Compile(missingSet, missingSet.Root);
            var missingRuntime = new BtRuntime(new AiBlackboard(), new BtTestSensor(),
                new BtTestActuator());
            missingRuntime.SetRoot(missingCompiled.Root, "missing", missingTreePath, "hash");
            missingRuntime.Start();
            for (int i = 0; i < 10; i++)
                missingRuntime.Tick(Dt);

            var missingTask = missingCompiled.Root.Find("play") as BtPlayActionPackageTask;
            result.Check("a missing action package makes the node fail (tree decides, no silent success)",
                missingTask != null && missingTask.LastError != null
                && missingTask.LastError.Contains("not found"),
                missingTask != null ? (missingTask.LastError ?? "<none>") : "<missing>");
        }

        // ---------------------------------------------------------------- 6) 出厂内容

        private static void SampleContent(BtSelfTest.TestResult result, string root)
        {
            string directory = Path.Combine(root, "installed");
            Directory.CreateDirectory(directory);
            var roots = new PackageRoots(Path.Combine(directory, "instance"));

            List<string> installed;
            string error;
            int count = PackageTemplates.Install(roots, out installed, out error);
            result.Check("factory install now also ships the sample action package",
                count >= 3 && error == null, "written=" + count + " " + (error ?? string.Empty));

            string actionPath = Path.Combine(roots.InstanceRoot.Path, PackageTemplates.SampleActionFile);
            result.Check("sample action package exists next to the trees",
                File.Exists(actionPath), actionPath);

            ScatActionPackage sample;
            PackageReport report;
            result.Check("shipped sample action package is replayable",
                ScatValidator.TryLoad(actionPath, out sample, out report)
                && sample.CanReplay, report.Summary());

            // 样例必须是"在哪都能放"的：中途关键帧记绝对坐标，回放时超容差就判失败
            // （ScatPlayer.CheckDrift）。样例若带虚构的中途坐标，真实世界里第一帧就崩。
            PackageValue sampleKeyframes = sample != null ? sample.Keyframes : null;
            int keyframeCount = sampleKeyframes != null && sampleKeyframes.IsObject
                ? sampleKeyframes.Get("frames").Count : -1;
            result.Check("the shipped sample asserts no mid-run positions (playable anywhere)",
                keyframeCount == 1, "keyframes=" + keyframeCount);

            string treePath = Path.Combine(roots.InstanceRoot.Path, PackageTemplates.TestTreeFile);
            result.Check("shipped test tree exists",
                File.Exists(treePath), treePath);

            var options = new PackageLoadOptions { Roots = roots };
            ScbtPackageSet set = PackageLoader.Load(treePath, options);
            result.Check("shipped test tree loads without errors",
                set.Root != null && !set.HasErrors, set.Describe());
        }
    }
}

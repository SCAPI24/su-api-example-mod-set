using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 出厂/自检用的**示例内容生成器**：我自己造的动作包与简单测试行为树。
    ///
    /// 为什么要有它：
    ///   · 动作包回放需要"确定的数据"才能回归测试 —— 人手录一段没法当测试基准；
    ///   · 用户拿到模组后应该有一条**开箱可跑**的链路：`test.action.scbtpak` 播放
    ///     `sample_walk.scatpak`（走两步 → 转身 → 跳 → 侧移 → 停），一眼看出回放是活的；
    ///   · 编辑器（P2）也需要一个不依赖真机录制的样例文件。
    ///
    /// 数据全是确定的（固定帧率 60、固定时长、固定按键序列），所以任何一次运行的期望值都一样。
    /// </summary>
    public static class PlayerAiPackages
    {
        public const string SampleActionName = "sample_walk";
        public const string TestTreeName = "test.action";

        /// <summary>示例动作包的时长（秒）。</summary>
        public const double SampleDurationSeconds = 2.0;

        public const int SampleFrameRate = 60;

        /// <summary>造一条确定的输入轨道：走 → 转身 → 跳 → 侧移 → 停。</summary>
        public static ScatTrack.Track BuildSampleTrack()
        {
            var track = new ScatTrack.Track();
            byte keyW = (byte)track.EnsureKey("W");
            byte keyA = (byte)track.EnsureKey("A");
            byte keySpace = (byte)track.EnsureKey("Space");

            int totalFrames = (int)Math.Round(SampleDurationSeconds * SampleFrameRate);
            int previousMs = 0;
            for (int i = 0; i < totalFrames; i++)
            {
                double t = (double)i / SampleFrameRate;

                // 每帧的时长用"累计时间取整后的差"算：120 帧加起来正好 2000 ms（样例要能当基准）
                int elapsedMs = (int)Math.Round((i + 1) * 1000.0 / SampleFrameRate);
                int deltaMs = elapsedMs - previousMs;
                previousMs = elapsedMs;

                var frame = new RecordingFrame { DeltaMs = deltaMs };

                var held = new List<byte>();
                var pressed = new List<byte>();

                if (t < 1.0)
                {
                    // 0.0~1.0s：向前走
                    held.Add(keyW);
                    frame.MoveZ = 1f;
                }
                else if (t < 1.4)
                {
                    // 1.0~1.4s：边走边左转（视角增量）
                    held.Add(keyW);
                    frame.MoveZ = 1f;
                    frame.LookDeltaX = 0.02f;
                }
                else if (t < 1.6)
                {
                    // 1.4~1.6s：走 + 跳（Space 只在进入这一段的第一帧按下）
                    held.Add(keyW);
                    frame.MoveZ = 1f;
                    frame.Jump = true;
                    if (i == (int)(1.4 * SampleFrameRate))
                        pressed.Add(keySpace);
                }
                else if (t < 1.8)
                {
                    // 1.6~1.8s：侧移
                    held.Add(keyA);
                    frame.MoveX = -1f;
                }
                else
                {
                    // 1.8~2.0s：停住
                }

                frame.KeysHeld = held.ToArray();
                frame.KeysPressed = pressed.ToArray();
                if (t >= 1.0 && t < 1.4)
                    frame.MouseButtons = 0; // 样例不按鼠标
                track.Frames.Add(frame);
            }

            return track;
        }

        /// <summary>把示例动作包打成 `.scatpak` 字节。</summary>
        public static byte[] BuildSampleActionPackage(string name = SampleActionName)
        {
            ScatTrack.Track track = BuildSampleTrack();

            var manifest = new ScatManifest
            {
                Id = name,
                Name = name,
                RecordedUtc = "2026-01-01T00:00:00.0000000Z",
                Duration = track.DurationSeconds,
                SampleRate = SampleFrameRate,
                Frames = track.FrameCount,
                Start = new RecordingStartState
                {
                    X = 0f,
                    Y = 64f,
                    Z = 0f,
                    YawDegrees = 0f,
                    PitchDegrees = 0f,
                    WorldName = "sample",
                    PlayerName = "sample"
                }
            };

            // 关键帧：样例**只带起点这一条**。
            //
            // 为什么不留中途关键帧：关键帧记的是**绝对世界坐标**，回放时逐条比对
            // （ScatPlayer.CheckDrift，容差 3 m，超限即失败、不瞬移纠偏）。
            // 样例是"假设"的动作，用户在任何世界、任何位置都可能放它；要是写上虚构的
            // 中途坐标，在真实世界里第一帧就会被判成"漂移 200 米"→ 开箱即失败。
            // 真实录制照旧每 0.5 s 记一条（那是**在哪个世界录的就在哪放**的语义）。
            PackageValue keyframes = PackageValue.Object();
            keyframes.Set("format", PackageValue.Str("scat-keyframes"));
            keyframes.Set("version", PackageValue.Number(1));
            keyframes.Set("interval", PackageValue.Number(0.5));

            PackageValue startFrame = PackageValue.Object();
            startFrame.Set("t", PackageValue.Number(0));
            PackageValue startState = PackageValue.Object();
            startState.Set("position", PackageValue.Array(new[]
            {
                PackageValue.Number(0.0),
                PackageValue.Number(64.0),
                PackageValue.Number(0.0)
            }));
            startState.Set("yaw", PackageValue.Number(0.0));
            startState.Set("pitch", PackageValue.Number(0.0));
            startFrame.Set("start", startState);
            keyframes.Set("frames", PackageValue.Array(new[] { startFrame }));

            PackageValue events = PackageValue.Object();
            events.Set("format", PackageValue.Str("scat-events"));
            events.Set("version", PackageValue.Number(1));
            PackageValue eventList = PackageValue.Array();
            eventList.Add(MakeEvent(1.4, "jump", "space"));
            eventList.Add(MakeEvent(1.6, "strafe", "left"));
            events.Set("events", eventList);

            return ScatPackage.ToBytes(manifest, track, events, keyframes);
        }

        private static PackageValue MakeEvent(double time, string kind, string detail)
        {
            PackageValue item = PackageValue.Object();
            item.Set("t", PackageValue.Number(time));
            item.Set("kind", PackageValue.Str(kind));
            item.Set("detail", PackageValue.Str(detail));
            return item;
        }

        /// <summary>
        /// 简单的测试行为树（`test.action.scbtpak`）：写日志 → 播放示例动作包 → 等半秒。
        /// 它就是"行为树 + 动作包"这条链的最小可跑样例。
        /// </summary>
        public static PackageTemplate BuildTestTreeTemplate(string actionName = SampleActionName)
        {
            PackageValue manifest = PackageValue.Object();
            manifest.Set("format", PackageValue.Str(ScbtManifest.FormatName));
            manifest.Set("version", PackageValue.Number(ScbtManifest.FormatVersion));
            manifest.Set("id", PackageValue.Str(TestTreeName));
            manifest.Set("name", PackageValue.Str("测试·播放动作包"));
            manifest.Set("entry", PackageValue.Str("root"));
            manifest.Set("blackboard", PackageValue.Array());

            PackageValue root = PackageValue.Object();
            root.Set("id", PackageValue.Str("root"));
            root.Set("type", PackageValue.Str("Root"));

            PackageValue sequence = PackageValue.Object();
            sequence.Set("id", PackageValue.Str("seq"));
            sequence.Set("type", PackageValue.Str("Sequence"));

            PackageValue children = PackageValue.Array();

            PackageValue log = PackageValue.Object();
            log.Set("id", PackageValue.Str("log"));
            log.Set("type", PackageValue.Str("Task.Log"));
            PackageValue logProperties = PackageValue.Object();
            logProperties.Set("message", PackageValue.Str("test.action: playing " + actionName));
            log.Set("properties", logProperties);
            children.Add(log);

            PackageValue play = PackageValue.Object();
            play.Set("id", PackageValue.Str("play"));
            play.Set("type", PackageValue.Str("Task.PlayActionPackage"));
            PackageValue playProperties = PackageValue.Object();
            playProperties.Set("packages", PackageValue.Array(new[]
            {
                PackageValue.Str(actionName)
            }));
            playProperties.Set("mode", PackageValue.Str("Sequence"));
            playProperties.Set("repeat", PackageValue.Number(1));
            playProperties.Set("abortOnFail", PackageValue.Bool(true));
            play.Set("properties", playProperties);
            children.Add(play);

            PackageValue wait = PackageValue.Object();
            wait.Set("id", PackageValue.Str("settle"));
            wait.Set("type", PackageValue.Str("Task.Wait"));
            PackageValue waitProperties = PackageValue.Object();
            waitProperties.Set("seconds", PackageValue.Number(0.5));
            wait.Set("properties", waitProperties);
            children.Add(wait);

            sequence.Set("children", children);
            root.Set("children", PackageValue.Array(new[] { sequence }));

            return new PackageTemplate(TestTreeName + PackageRoots.Extension,
                "测试·播放动作包", TestTreeDescription, manifest, root);
        }

        private const string TestTreeDescription =
            "# 测试·播放动作包（test.action.scbtpak）\n" +
            "\n" +
            "出厂的最小链路测试：`Task.PlayActionPackage` 播放同路径的 `sample_walk.scatpak`。\n" +
            "\n" +
            "- 行为树决定**做什么**（这里是「播放一段录好的动作」），动作包提供**怎么做**（逐帧输入）。\n" +
            "- 回放失败（漂移超限、包缺失）会让这个节点判失败，交回行为树决策 —— 不会瞬移纠偏。\n" +
            "- 想试自己的包：把 `packages` 改成你的文件名（放同一目录），或直接\n" +
            "  `sccmd ai action play <你的包名>`。\n";
    }
}

using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>回放状态。</summary>
    public enum ScatPlayState
    {
        Idle,
        Playing,
        Finished,

        /// <summary>失败（漂移超限 / 输入注入失败 / 数据不可用）—— 交回行为树决策，不做任何特权修正。</summary>
        Failed
    }

    /// <summary>回放时的漂移报告（计划 §6.3：失败即交回行为树，禁止瞬移类修正）。</summary>
    public sealed class ScatDriftReport
    {
        public int KeyframesChecked;

        public double MaxPositionDrift;

        public double MaxYawDriftDegrees;

        public double AtTime;

        public bool Exceeded;

        public string Reason;

        public string Describe()
        {
            if (!Exceeded)
            {
                return "drift ok (checked=" + KeyframesChecked
                    + " maxPos=" + MaxPositionDrift.ToString("0.00") + "m"
                    + " maxYaw=" + MaxYawDriftDegrees.ToString("0.0") + "°)";
            }
            return "drift exceeded at t=" + AtTime.ToString("0.00") + "s: " + Reason;
        }

        public override string ToString()
        {
            return "ScatDriftReport(" + Describe() + ")";
        }
    }

    /// <summary>
    /// 动作包回放器（P1，计划 §6.3）：
    ///
    ///   · **按录制节奏**逐帧喂给同一个执行器（<see cref="IAiActuator"/>）——
    ///     关键帧的 `dt` 决定这一帧的输入保持多久，于是录得 60 FPS、回放跑 144 FPS 也不会快放；
    ///   · 每帧只在**帧切换**时写入输入（按住类保持、按下类只按一次），与真人输入语义一致；
    ///   · **漂移检查**：越过关键帧时比对实际位置/朝向，超阈值就判失败 ——
    ///     失败交回行为树（可重试/换分支），**绝不瞬移修正**（决策 D6）；
    ///   · 任何结束路径都释放输入（`OnExit` 铁律），不留"回放完了还在走"。
    ///
    /// 它不认识游戏类型：只要一个执行器 +（可选的）传感器即可，所以能脱离游戏自检。
    /// </summary>
    public sealed class ScatPlayer
    {
        private readonly List<ScatKeyframe> m_keyframes = new List<ScatKeyframe>();
        private readonly List<string> m_missingKeys = new List<string>();

        /// <summary>包里的 UI 事件（`ui.click`）：按时间点重放，菜单/背包这类操作全靠它。</summary>
        private readonly List<ScatUiEvent> m_uiEvents = new List<ScatUiEvent>();

        private double m_frameTimeMs;
        private int m_nextKeyframe;
        private int m_nextUiEvent;

        public ScatPlayer(ScatActionPackage package, IAiActuator actuator, IAiSensor sensors = null)
        {
            Package = package;
            Actuator = actuator;
            Sensors = sensors;
            LoadKeyframes(package);
            LoadUiEvents(package);
        }

        /// <summary>回放里真的点过的 UI 元素（诊断/自检用）。</summary>
        public IReadOnlyList<string> PerformedUiClicks
        {
            get { return m_performedUiClicks; }
        }

        /// <summary>
        /// 包里带了多少条 UI 点击事件（诊断用）：0 说明"这个包没有菜单操作"，
        /// 而不是"点击被拒绝了" —— 这两件事在日志里必须能分开看。
        /// </summary>
        public int UiEventCount
        {
            get { return m_uiEvents.Count; }
        }

        private readonly List<string> m_performedUiClicks = new List<string>();

        /// <summary>
        /// 被引擎如实拒绝、还在重试的 UI 点击（`目标` + 放弃时刻）。
        ///
        /// 为什么要重试而不是"到点打一枪"：UI 事件的时间轴是**录制时**的，
        /// 而回放时"目标存不存在"取决于**屏幕切场动画/列表加载**这些实时过程——
        /// 两者不可能永远对齐（实测：进游戏包第二次点击落在 Play 屏切场动画中间，
        /// 引擎如实回一句 `rejected (missing/occluded)`，于是"选世界"这一步整条丢掉了）。
        /// 语义目标（`Play` / `list:WorldsList@世界名`）本来就能随时现解析，
        /// 所以"没到点"就该等一等，而不是判失败。
        /// </summary>
        private sealed class PendingUiClick
        {
            public string Target;
            public double DueTime;
            public double Deadline;
        }

        private readonly List<PendingUiClick> m_pendingUiClicks = new List<PendingUiClick>();

        /// <summary>UI 点击被拒绝后最多重试多久（秒）。切场动画 ~0.5s，列表加载偶尔更久。</summary>
        public double UiClickRetrySeconds { get; set; } = 5.0;

        public ScatActionPackage Package { get; }

        public IAiActuator Actuator { get; }

        public IAiSensor Sensors { get; }

        public ScatPlayState State { get; private set; } = ScatPlayState.Idle;

        public string LastError { get; private set; }

        /// <summary>当前播放到第几帧（0 基）。</summary>
        public int FrameIndex { get; private set; }

        /// <summary>已回放时间（秒）。</summary>
        public double Elapsed { get; private set; }

        /// <summary>本轮播放完成了几次（配合 Repeat 使用）。</summary>
        public int CompletedLoops { get; private set; }

        public int Repeat { get; set; } = 1;

        public ScatDriftReport LastDrift { get; private set; } = new ScatDriftReport();

        /// <summary>位置漂移阈值（米）。</summary>
        public double DriftToleranceMeters { get; set; } = PlayerAiConfig.ReplayDriftToleranceMeters;

        /// <summary>朝向漂移阈值（度）。</summary>
        public double DriftToleranceDegrees { get; set; } = PlayerAiConfig.ReplayDriftToleranceDegrees;

        /// <summary>回放期间执行器拒绝的键（例如键名在这台机器上不存在）——会如实报出来，不静默丢。</summary>
        public IReadOnlyList<string> MissingKeys
        {
            get { return m_missingKeys; }
        }

        public double Progress
        {
            get
            {
                int frames = Package != null && Package.Track != null ? Package.Track.FrameCount : 0;
                return frames <= 0 ? 0.0 : Math.Min(1.0, (double)FrameIndex / frames);
            }
        }

        public bool IsFinished
        {
            get { return State == ScatPlayState.Finished || State == ScatPlayState.Failed; }
        }

        // ---------------------------------------------------------------- 生命周期

        public bool CanPlay
        {
            get { return Package != null && Package.CanReplay && Actuator != null && Actuator.IsReady; }
        }

        public void Start()
        {
            LastError = null;
            m_missingKeys.Clear();
            FrameIndex = 0;
            Elapsed = 0.0;
            CompletedLoops = 0;
            m_nextKeyframe = 0;
            m_nextUiEvent = 0;
            m_pendingUiClicks.Clear();
            m_performedUiClicks.Clear();
            LastDrift = new ScatDriftReport();

            if (Package == null)
            {
                Fail("no action package");
                return;
            }
            if (!Package.CanReplay)
            {
                Fail("package has no per-frame input track (P0 skeleton) -> cannot replay");
                return;
            }
            if (Actuator == null || !Actuator.IsReady)
            {
                Fail("actuator is not ready (input injection unavailable)");
                return;
            }

            State = ScatPlayState.Playing;
            m_frameTimeMs = 0.0;
            ApplyFrame(0);
        }

        /// <summary>停止并释放输入（任何结束路径都会走它）。</summary>
        public void Stop(string reason)
        {
            ReleaseInput();
            if (State == ScatPlayState.Playing)
            {
                State = ScatPlayState.Idle;
                LastError = reason;
            }
        }

        /// <summary>
        /// 推进一帧。返回：
        ///   InProgress = 还在放；
        ///   Succeeded  = 放完（按 Repeat 次数）；
        ///   Failed     = 漂移超限/输入不可用/没数据（交回行为树）。
        /// </summary>
        public BtResult Tick(float deltaTime)
        {
            if (State == ScatPlayState.Finished)
                return BtResult.Succeeded;
            if (State == ScatPlayState.Failed)
                return BtResult.Failed;
            if (State != ScatPlayState.Playing)
            {
                Start();
                if (State != ScatPlayState.Playing)
                    return BtResult.Failed;
            }

            ScatTrack.Track track = Package.Track;
            if (deltaTime < 0f)
                deltaTime = 0f;

            Elapsed += deltaTime;
            CheckDrift(Elapsed);
            if (State == ScatPlayState.Failed)
                return BtResult.Failed;

            FireUiEvents(Elapsed);
            if (State == ScatPlayState.Failed)
                return BtResult.Failed;

            m_frameTimeMs -= deltaTime * 1000.0;

            // 一帧的输入要保持它自己那段时间；到点了才切下一帧（于是回放帧率与录制帧率解耦）
            int guard = 0;
            while (m_frameTimeMs <= 0.0 && guard++ < 8)
            {
                int next = FrameIndex + 1;
                if (next >= track.FrameCount)
                {
                    CompletedLoops++;
                    if (Repeat > 0 && CompletedLoops >= Repeat)
                    {
                        ReleaseInput();
                        State = ScatPlayState.Finished;
                        return BtResult.Succeeded;
                    }
                    next = 0; // 循环播放
                }

                FrameIndex = next;
                m_frameTimeMs += Math.Max(1.0, track.Frames[FrameIndex].DeltaMs);
                ApplyFrame(FrameIndex);
            }

            return State == ScatPlayState.Playing ? BtResult.InProgress : BtResult.Failed;
        }

        // ---------------------------------------------------------------- 内部

        /// <summary>把某一帧的输入写进执行器（按住保持、按下只按一次、松开不按的键）。</summary>
        private void ApplyFrame(int index)
        {
            ScatTrack.Track track = Package.Track;
            if (track == null || index < 0 || index >= track.FrameCount)
                return;

            RecordingFrame frame = track.Frames[index];

            // 1) 按住类：这一帧按着的键 = 保持，上一帧按着这一帧没按的 = 松开
            List<string> held = ScatTrack.KeyNamesOf(track, frame.KeysHeld);
            for (int i = 0; i < held.Count; i++)
                Inject(delegate (IAiActuator actuator, string name) { actuator.HoldKey(name, true); }, held[i]);

            for (int i = 0; i < m_appliedHeld.Count; i++)
            {
                string previous = m_appliedHeld[i];
                if (held.Contains(previous))
                    continue;
                Inject(delegate (IAiActuator actuator, string name) { actuator.HoldKey(name, false); },
                    previous);
            }
            m_appliedHeld.Clear();
            m_appliedHeld.AddRange(held);

            // 2) 按下类（downOnce）：只在进入这一帧时按一次（开关类动作全靠它）
            List<string> pressed = ScatTrack.KeyNamesOf(track, frame.KeysPressed);
            for (int i = 0; i < pressed.Count; i++)
                Inject(delegate (IAiActuator actuator, string name) { actuator.PulseKey(name, 0); }, pressed[i]);

            // 3) 视角增量（录的是效果，不是鼠标计数：直接加到 lookAngles 上，等效）
            if (frame.LookDeltaX != 0f || frame.LookDeltaY != 0f)
            {
                try
                {
                    Actuator.LookDelta(frame.LookDeltaX, frame.LookDeltaY);
                }
                catch (Exception exception)
                {
                    LastError = "LookDelta failed: " + exception.Message;
                }
            }

            // 4) 鼠标键：按位图还原按下/松开
            SetMouseButton(MouseButtonLeft, (frame.MouseButtons & 0x1) != 0);
            SetMouseButton(MouseButtonRight, (frame.MouseButtons & 0x2) != 0);
            SetMouseButton(MouseButtonMiddle, (frame.MouseButtons & 0x4) != 0);

            // 5) 滚轮
            if (frame.Wheel != 0)
            {
                try
                {
                    Actuator.Wheel(frame.Wheel);
                }
                catch (Exception exception)
                {
                    LastError = "Wheel failed: " + exception.Message;
                }
            }
        }

        private const string MouseButtonLeft = "left";
        private const string MouseButtonRight = "right";
        private const string MouseButtonMiddle = "middle";

        private readonly List<string> m_appliedHeld = new List<string>();
        private readonly List<string> m_mouseDown = new List<string>();

        private void SetMouseButton(string button, bool down)
        {
            bool currently = m_mouseDown.Contains(button);
            if (currently == down)
                return;

            try
            {
                Actuator.MouseButton(button, down);
                if (down)
                    m_mouseDown.Add(button);
                else
                    m_mouseDown.Remove(button);
            }
            catch (Exception exception)
            {
                LastError = "MouseButton(" + button + ") failed: " + exception.Message;
            }
        }

        private delegate void KeyAction(IAiActuator actuator, string key);

        private void Inject(KeyAction action, string key)
        {
            try
            {
                action(Actuator, key);
            }
            catch (Exception exception)
            {
                LastError = "input injection failed for '" + key + "': " + exception.Message;
            }
        }

        /// <summary>释放全部输入（结束/失败/被打断都要走）。</summary>
        public void ReleaseInput()
        {
            m_appliedHeld.Clear();
            m_mouseDown.Clear();
            if (m_pendingUiClicks.Count > 0)
            {
                // 回放结束了还有没点成的点击：如实说，不能装作"这步做过了"。
                var names = new List<string>();
                for (int i = 0; i < m_pendingUiClicks.Count; i++)
                    names.Add(m_pendingUiClicks[i].Target);
                m_pendingUiClicks.Clear();
                string message = "ui click(s) never became clickable: " + string.Join(", ", names.ToArray());
                if (LastError == null)
                    LastError = message;
                Engine.Log.Warning("[PlayerAi][act] " + message);
            }
            try
            {
                if (Actuator != null)
                    Actuator.ReleaseAll();
            }
            catch (Exception exception)
            {
                LastError = "ReleaseAll failed: " + exception.Message;
            }
        }

        /// <summary>漂移检查：越过关键帧时比对位置/朝向（超限就失败，不做修正）。</summary>
        private void CheckDrift(double elapsedSeconds)
        {
            if (m_keyframes.Count == 0 || Sensors == null || !Sensors.IsReady)
                return;

            while (m_nextKeyframe < m_keyframes.Count
                && m_keyframes[m_nextKeyframe].Time <= elapsedSeconds)
            {
                ScatKeyframe keyframe = m_keyframes[m_nextKeyframe];
                m_nextKeyframe++;

                if (keyframe.IsStart)
                    continue; // 起点是基准，不是检查点

                LastDrift.KeyframesChecked++;
                var actual = Sensors.Position;
                double dx = actual.X - keyframe.X;
                double dy = actual.Y - keyframe.Y;
                double dz = actual.Z - keyframe.Z;
                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double yawDrift = Math.Abs(keyframe.YawDegrees - Sensors.YawRadians * 180.0 / Math.PI);

                if (distance > LastDrift.MaxPositionDrift)
                    LastDrift.MaxPositionDrift = distance;
                if (yawDrift > LastDrift.MaxYawDriftDegrees)
                    LastDrift.MaxYawDriftDegrees = yawDrift;
                LastDrift.AtTime = elapsedSeconds;

                if (distance > DriftToleranceMeters)
                {
                    LastDrift.Exceeded = true;
                    LastDrift.Reason = "position drifted " + distance.ToString("0.00")
                        + "m from the keyframe (tolerance " + DriftToleranceMeters.ToString("0.0")
                        + "m)";
                    Fail("replay drift: " + LastDrift.Reason);
                    return;
                }

                if (yawDrift > DriftToleranceDegrees)
                {
                    LastDrift.Exceeded = true;
                    LastDrift.Reason = "view drifted " + yawDrift.ToString("0.0")
                        + "° from the keyframe (tolerance " + DriftToleranceDegrees.ToString("0.0") + "°)";
                    Fail("replay drift: " + LastDrift.Reason);
                    return;
                }
            }
        }

        /// <summary>
        /// 读出包里的 UI 事件（`tracks/events.json` 里 `kind=ui.click`）并按时间排好。
        ///
        /// 为什么 UI 操作要单独一条通道：菜单/背包点击走的是引擎**软光标**（CM-1），
        /// 原始输入层里没有痕迹（视角/按键/鼠标位图都录不到"点了哪个控件"），
        /// 所以录制端把它记成语义事件，回放端在同样的时间点重放同一套点击。
        /// </summary>
        private void LoadUiEvents(ScatActionPackage package)
        {
            m_uiEvents.Clear();
            PackageValue events = package != null ? package.Events : null;
            if (events == null || !events.IsObject)
                return;

            PackageValue list = events.Get("events");
            for (int i = 0; i < list.Count; i++)
            {
                PackageValue item = list.Item(i);
                if (!item.IsObject)
                    continue;
                if (!string.Equals(item.Get("kind").AsString(null), "ui.click", StringComparison.Ordinal))
                    continue;

                string detail = item.Get("detail").AsString(null);
                if (string.IsNullOrEmpty(detail))
                    continue;

                m_uiEvents.Add(new ScatUiEvent
                {
                    Time = item.Get("t").AsNumber(0.0),
                    Target = detail
                });
            }

            m_uiEvents.Sort((left, right) => left.Time.CompareTo(right.Time));
        }

        /// <summary>到点就重放 UI 点击（与帧切换无关：菜单操作有自己的时间点）。</summary>
        private void FireUiEvents(double elapsedSeconds)
        {
            // ① 先把上一帧（或前几帧）被拒绝的点击重试掉。
            //    顺序铁律：**只要还有没完成的点击，就不许放行后面的点击** ——
            //    `选世界` 必须发生在 `Play!` 之前，否则 Play! 点到的是"没有选中世界"的状态。
            for (int i = 0; i < m_pendingUiClicks.Count; i++)
            {
                PendingUiClick pending = m_pendingUiClicks[i];
                if (TryUiClick(pending.Target))
                {
                    m_performedUiClicks.Add(pending.Target);
                    Engine.Log.Information("[PlayerAi][act] ui click " + pending.Target
                        + " succeeded after a retry (due t=" + pending.DueTime.ToString("0.00") + "s)");
                    m_pendingUiClicks.RemoveAt(i);
                    i--;
                    continue;
                }
                if (elapsedSeconds >= pending.Deadline)
                {
                    m_pendingUiClicks.RemoveAt(i);
                    i--;
                    LastError = "ui click '" + pending.Target + "' was rejected for "
                        + (pending.Deadline - pending.DueTime).ToString("0.0") + "s (missing/occluded)";
                    Engine.Log.Warning("[PlayerAi][act] " + LastError);
                }
            }

            if (m_pendingUiClicks.Count > 0)
                return;   // 前面的还没成，后面的排队等（见上面顺序铁律）

            while (m_nextUiEvent < m_uiEvents.Count && m_uiEvents[m_nextUiEvent].Time <= elapsedSeconds)
            {
                ScatUiEvent uiEvent = m_uiEvents[m_nextUiEvent];
                m_nextUiEvent++;

                // 事件里存的是 `<动词>:<目标>`（`click:Play` / `click:list:WorldsList@世界名`）——
                // 动词是给读包的人看的，注入时要把它剥掉，否则选择器变成 "click:Play" 谁也点不到。
                //
                // 但**只剥认识的动词**：`list:` 这类语义前缀自己也带冒号，
                // 早先"无条件剥到第一个冒号"会把手写的 `list:WorldsList@世界名` 削成
                // `WorldsList@世界名` —— 于是它既不是 `list:` 目标、也不是控件选择器，静默点空。
                string target = StripVerb(uiEvent.Target);

                if (TryUiClick(target))
                {
                    m_performedUiClicks.Add(target);
                    if (PlayerAiConfig.VerboseLogging)
                    {
                        Engine.Log.Information("[PlayerAi][act] ui click " + target
                            + " at t=" + uiEvent.Time.ToString("0.00") + "s");
                    }
                    continue;
                }

                // 引擎如实拒绝（元素不在/被挡住）：多半是"还没轮到它"（切场动画、列表还在填）。
                // 记成待重试，时间轴继续走，后面的点击等它（见 ①）。
                m_pendingUiClicks.Add(new PendingUiClick
                {
                    Target = target,
                    DueTime = elapsedSeconds,
                    Deadline = elapsedSeconds + Math.Max(0.5, UiClickRetrySeconds)
                });
                Engine.Log.Information("[PlayerAi][act] ui click " + target
                    + " not ready at t=" + uiEvent.Time.ToString("0.00") + "s; retrying for "
                    + Math.Max(0.5, UiClickRetrySeconds).ToString("0.0") + "s");
            }
        }

        /// <summary>点一次（吞异常）：返回 false 表示"这次点不到"，调用方决定重试还是放弃。</summary>
        private bool TryUiClick(string target)
        {
            try
            {
                return Actuator != null && Actuator.UiClick(target);
            }
            catch (Exception exception)
            {
                LastError = "ui click '" + target + "' failed: " + exception.Message;
                Engine.Log.Warning("[PlayerAi][act] " + LastError);
                return false;
            }
        }

        /// <summary>
        /// 去掉事件里的动词前缀（`click:Play` → `Play`）。
        /// **只认已知动词**：否则 `list:WorldsList@世界名` 会被削成 `WorldsList@世界名`，
        /// 那既不是 `list:` 语义目标也不是控件选择器 —— 回放看着"点了"，其实点了个空。
        /// 想加新动词就往这张表里加。
        /// </summary>
        private static string StripVerb(string detail)
        {
            if (string.IsNullOrEmpty(detail))
                return detail;
            int colon = detail.IndexOf(':');
            if (colon <= 0)
                return detail;

            string verb = detail.Substring(0, colon);
            if (string.Equals(verb, "click", StringComparison.OrdinalIgnoreCase)
                || string.Equals(verb, "tap", StringComparison.OrdinalIgnoreCase)
                || string.Equals(verb, "uiclick", StringComparison.OrdinalIgnoreCase)
                || string.Equals(verb, "ui.click", StringComparison.OrdinalIgnoreCase))
            {
                return detail.Substring(colon + 1);
            }
            return detail;
        }

        private void LoadKeyframes(ScatActionPackage package)
        {
            m_keyframes.Clear();
            PackageValue keyframes = package != null ? package.Keyframes : null;
            if (keyframes == null || !keyframes.IsObject)
                return;

            PackageValue frames = keyframes.Get("frames");
            for (int i = 0; i < frames.Count; i++)
            {
                PackageValue item = frames.Item(i);
                if (!item.IsObject)
                    continue;

                PackageValue state = item.Get("start");
                PackageValue position = state.Get("position");
                m_keyframes.Add(new ScatKeyframe
                {
                    Time = item.Get("t").AsNumber(0.0),
                    X = position.Count > 0 ? position.Item(0).AsFloat() : 0f,
                    Y = position.Count > 1 ? position.Item(1).AsFloat() : 0f,
                    Z = position.Count > 2 ? position.Item(2).AsFloat() : 0f,
                    YawDegrees = state.Get("yaw").AsFloat(),
                    IsStart = i == 0
                });
            }
        }

        private void Fail(string reason)
        {
            LastError = reason;
            State = ScatPlayState.Failed;
            ReleaseInput();
        }

        private sealed class ScatUiEvent
        {
            public double Time;
            public string Target;
        }

        private sealed class ScatKeyframe
        {
            public double Time;
            public float X;
            public float Y;
            public float Z;
            public float YawDegrees;
            public bool IsStart;
        }

        public string Describe()
        {
            string name = Package != null ? Package.FileName : "<none>";
            return "replay(" + name + " " + State.ToString().ToLowerInvariant()
                + " frame=" + FrameIndex + "/" + (Package != null ? Package.Frames : 0)
                + " t=" + Elapsed.ToString("0.00") + "s loops=" + CompletedLoops
                + (string.IsNullOrEmpty(LastError) ? string.Empty : " err=" + LastError) + ")";
        }

        public override string ToString()
        {
            return Describe();
        }
    }
}

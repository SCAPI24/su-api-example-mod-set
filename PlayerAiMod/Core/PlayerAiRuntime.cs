using Engine;
using Engine.Graphics;
using Game;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 全局运行时：登记所有 玩家 AI，并把 tick 排到"下一帧帧首"统一执行。
    ///
    /// 为什么必须帧首：
    /// <code>
    /// Window: BeforeFrameAll() → Dispatcher.BeforeFrame() → Keyboard/Mouse.BeforeFrame()
    ///         → 帧体（含各 Component.Update、Program 触发 Frame.Update）→ AfterFrameAll()
    /// Keyboard/Mouse 的 downOnce 数组在帧末被清空。
    /// </code>
    /// 所以插件在帧末的 <c>Frame.Update</c> 里 <c>Dispatcher.Dispatch</c>，真正的 tick 会在
    /// 下一帧 <c>Dispatcher.BeforeFrame()</c> 执行 —— 位置早于输入设备读取与整个帧体，语义与真人输入一致。
    ///
    /// Source: Engine/Engine/Window.cs:464 BeforeFrameAll / Engine/Engine/Dispatcher.cs
    /// Source: Survivalcraft/Game/Program.cs:147 TriggerEvent("Frame.Update")（帧末触发）
    /// </summary>
    public sealed class PlayerAiRuntime
    {
        private readonly List<AiActor> m_actors = new List<AiActor>();
        private readonly List<AiActor> m_scratch = new List<AiActor>();
        private bool m_tickScheduled;
        private bool m_loggedPumpPath;

        /// <summary>当前 Mod 实例的运行时（未加载时为 null）。</summary>
        public static PlayerAiRuntime Instance { get; set; }

        private PackageRoots m_packageRoots;
        private PackageReloader m_reloader;
        private bool m_autoLoadPending;
        private AiRecordingSession m_recording;
        private TreeLibrary m_library;
        private ScatPlayer m_actionPlayer;
        private string m_actionName;
        private CmdBridgeMod.CmdBridgeInputFrame m_lastInputFrame;
        private AiEventLog m_eventLog;

        // ---------------------------------------------------------------- 录制（P0-9）

        /// <summary>
        /// 录制会话（全局一个）。落盘目录是**实例包目录** `<实例根>/PlayerAi/BehaviorTrees/` ——
        /// 与树包同路径（计划 §4.4），且只写用户可写的那一份，Mod 分发目录永不写入。
        /// </summary>
        public AiRecordingSession Recording
        {
            get
            {
                if (m_recording == null)
                    m_recording = new AiRecordingSession(ResolveActionDirectory());
                return m_recording;
            }
        }

        /// <summary>录制产物的目标目录（实例包目录；必要时创建）。</summary>
        public string ResolveActionDirectory()
        {
            PackageRoots roots = m_packageRoots;
            if (roots == null)
                return null;

            PackageRoot instance = roots.InstanceRoot;
            if (instance == null)
                return null;

            try
            {
                Directory.CreateDirectory(instance.Path);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][rec] cannot create " + instance.Path + ": "
                    + exception.Message);
            }
            return instance.Path;
        }

        /// <summary>
        /// 动作包的查找链：**实例包目录在前，Mod 只读分发目录在后**（前面的优先）。
        /// 与树包的引用解析同源：出厂示例装在 Mod 目录里，直接播放也要找得到；
        /// 用户自己录的包落在实例目录，同名时覆盖分发目录里的那一份。
        /// </summary>
        public List<string> ResolveActionDirectories()
        {
            var directories = new List<string>();

            string primary = ResolveActionDirectory();
            if (!string.IsNullOrEmpty(primary))
                directories.Add(primary);

            PackageRoots roots = m_packageRoots;
            string mod = roots != null && roots.ModRoot != null ? roots.ModRoot.Path : null;
            if (!string.IsNullOrEmpty(mod))
            {
                bool duplicate = false;
                for (int i = 0; i < directories.Count; i++)
                {
                    if (string.Equals(directories[i], mod, StringComparison.OrdinalIgnoreCase))
                        duplicate = true;
                }
                if (!duplicate)
                    directories.Add(mod);
            }

            return directories;
        }

        // ---------------------------------------------------------------- 动作包（P1）

        /// <summary>当前直接播放的动作包（未播放时为 null）。</summary>
        public ScatPlayer ActionPlayer
        {
            get { return m_actionPlayer; }
        }

        /// <summary>
        /// 直接播放一个动作包（不经行为树）—— 给人测试包用，也是 `ai.action.play` 的实现。
        /// 播放每帧在帧首推进（与录制同一条时间轴）。
        /// </summary>
        public string PlayAction(string nameOrPath, int repeat)
        {
            List<string> directories = ResolveActionDirectories();
            string folder;
            string path = ScatLibrary.ResolveIn(directories, nameOrPath, out folder);
            if (path == null)
            {
                throw new AiCommandException("file_missing",
                    "action package not found: '" + (nameOrPath ?? "<none>") + "' in "
                    + (directories.Count > 0 ? string.Join("; ", directories.ToArray())
                        : "<no directory>"));
            }

            ScatActionPackage package;
            PackageReport report;
            if (!ScatValidator.TryLoad(path, out package, out report))
            {
                throw new AiCommandException("invalid_argument",
                    "action package is invalid: "
                    + (report.FirstError != null ? report.FirstError.Describe() : report.Summary()));
            }
            if (!package.CanReplay)
            {
                throw new AiCommandException("not_replayable",
                    "package has no per-frame input track (P0 skeleton) -> cannot replay: "
                    + package.Describe());
            }

            IAiTreeHost host = ResolveTreeHost();
            IAiActuator actuator = host != null ? host.Actuators : null;
            IAiSensor sensors = host != null ? host.Sensors : null;
            if (actuator == null || !actuator.IsReady)
            {
                // 主菜单/世界未加载时没有角色，但 **UI 类动作包照样要能放**：
                // 「进入游戏」这种包整段都在菜单里（点 Play → 选世界 → Play!），
                // 只需要输入注入通道（CM-1 软光标那套），不需要角色、也不需要传感器。
                // 这个兜底执行器**只做 UI 点击**：世界外的按键/鼠标本来也没有意义。
                if (CmdBridgeActuator.IsAvailable)
                    actuator = new UiOnlyActuator();
            }
            if (actuator == null || !actuator.IsReady)
            {
                throw new AiCommandException("not_ready",
                    "input injection is unavailable (CmdBridgeMod missing or disabled)");
            }

            StopAction();

            m_actionPlayer = new ScatPlayer(package, actuator, sensors)
            {
                Repeat = repeat > 0 ? repeat : 1
            };
            m_actionName = package.FileName;
            m_actionPlayer.Start();
            if (m_actionPlayer.State != ScatPlayState.Playing)
            {
                string error = m_actionPlayer.LastError;
                m_actionPlayer = null;
                throw new AiCommandException("failed", error ?? "replay could not start");
            }

            EventLog.Write("action-play", package.Describe() + " repeat=" + repeat
                + " uiEvents=" + m_actionPlayer.UiEventCount);
            ShowMessage("AI: 回放 " + package.FileName);
            return "playing " + package.Describe();
        }

        /// <summary>停止直接播放并释放输入。</summary>
        public string StopAction()
        {
            if (m_actionPlayer == null)
                return "nothing is playing";

            string name = m_actionName;
            m_actionPlayer.Stop("stopped by user");
            m_actionPlayer.ReleaseInput();
            m_actionPlayer = null;
            m_actionName = null;
            EventLog.Write("action-stop", name ?? "<unknown>");
            return "stopped " + (name ?? "<unknown>");
        }

        /// <summary>播放状态（`ai.action.status` / `ai.status.action`）。</summary>
        public Dictionary<string, object> ActionStatus()
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["directory"] = ResolveActionDirectory(),
                ["playing"] = m_actionPlayer != null,
                ["name"] = m_actionName
            };
            if (m_actionPlayer != null)
            {
                info["state"] = m_actionPlayer.State.ToString().ToLowerInvariant();
                info["frame"] = m_actionPlayer.FrameIndex;
                info["frames"] = m_actionPlayer.Package.Frames;
                info["elapsed"] = Math.Round(m_actionPlayer.Elapsed, 3);
                info["progress"] = Math.Round(m_actionPlayer.Progress, 3);
                info["loops"] = m_actionPlayer.CompletedLoops;
                info["lastError"] = m_actionPlayer.LastError;
                info["drift"] = m_actionPlayer.LastDrift.Describe();
            }
            return info;
        }

        /// <summary>把当前角色/世界状态抓成录制起点（P1 的回放以它为基准算相对量）。</summary>
        public RecordingStartState CaptureStartState()
        {
            var state = new RecordingStartState();
            IAiTreeHost host = ResolveTreeHost();
            if (host != null && host.Sensors != null)
            {
                state.X = host.Sensors.Position.X;
                state.Y = host.Sensors.Position.Y;
                state.Z = host.Sensors.Position.Z;
                state.YawDegrees = host.Sensors.YawRadians * 180f / MathUtils.PI;
                float pitch;
                if (host.Sensors.TryGetPitch(out pitch))
                    state.PitchDegrees = pitch * 180f / MathUtils.PI;
                state.PlayerName = host.HostName;
            }

            try
            {
                if (GameManager.Project != null)
                {
                    SubsystemGameInfo info = GameManager.Project.FindSubsystem<SubsystemGameInfo>(false);
                    if (info != null && info.WorldSettings != null)
                        state.WorldName = info.WorldSettings.Name;
                }
            }
            catch (Exception)
            {
                // 世界信息拿不到不影响录制（只是少一个溯源字段）
            }
            return state;
        }

        /// <summary>开始录制（PgUp 未录制时、或 `ai.record.start`）。返回一句结果说明。</summary>
        public string StartRecording(string name = null)
        {
            AiRecordingSession session = Recording;
            if (string.IsNullOrEmpty(session.TargetDirectory))
            {
                ShowMessage("AI: 没有可写的动作包目录");
                throw new AiCommandException("not_ready",
                    "no writable folder for action packages (PlayerAi/BehaviorTrees)");
            }

            session.StartState = CaptureStartState();
            string result = session.Start(name);
            string text = "AI: REC " + (session.Status.SuggestedName ?? "?")
                + "  (PgDn 结束并命名)";
            ShowMessage(text);
            Engine.Log.Information("[PlayerAi][rec] " + result + " start=" + session.StartState);
            return result;
        }

        /// <summary>PgUp：未录制 → 开始；录制中 → 暂停/继续；待命名 → 直接把命名框再弹一次。</summary>
        public void OnRecordHotkey()
        {
            AiRecordingSession session = Recording;
            switch (session.Status.Phase)
            {
                case RecordingPhase.Idle:
                    try
                    {
                        StartRecording(null);
                    }
                    catch (AiCommandException exception)
                    {
                        ShowMessage("AI: 录制未开始（" + exception.Message + "）");
                    }
                    return;

                case RecordingPhase.PendingSave:
                    ShowMessage("AI: 等待命名（PgDn 再弹一次命名框）");
                    ShowRecordingNameDialog();
                    return;

                default:
                    try
                    {
                        string result = session.TogglePause();
                        ShowMessage(result == "recording paused"
                            ? "AI: REC 暂停"
                            : "AI: REC 继续  " + session.Status.Duration.ToString("0.0") + "s");
                    }
                    catch (AiCommandException exception)
                    {
                        ShowMessage("AI: " + exception.Message);
                    }
                    return;
            }
        }

        /// <summary>PgDn：结束录制 → 弹出命名框（取消也不会丢，仍可 ai.record.save）。</summary>
        public void OnRecordStopHotkey()
        {
            AiRecordingSession session = Recording;
            if (session.Status.Phase == RecordingPhase.Idle)
            {
                ShowMessage("AI: 没有在录制");
                return;
            }

            if (session.Status.Phase != RecordingPhase.PendingSave)
            {
                string result = session.Stop();
                ShowMessage("AI: REC 结束 " + session.Status.Duration.ToString("0.0") + "s / "
                    + session.Status.Frames + " 帧");
                Engine.Log.Information("[PlayerAi][rec] " + result);
            }

            ShowRecordingNameDialog();
        }

        /// <summary>弹命名框；名字确认后走"保存/覆盖询问"。</summary>
        public void ShowRecordingNameDialog()
        {
            AiRecordingSession session = Recording;
            if (!session.Status.IsActive)
                return;

            string suggested = session.Status.SuggestedName;
            if (string.IsNullOrEmpty(suggested))
                suggested = AiRecordingSession.SuggestName();

            AiRecordingUi.ShowNameDialog(suggested, OnRecordingNameEntered);
        }

        private void OnRecordingNameEntered(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                ShowMessage("AI: 未命名（用 sccmd ai record save <名字> 另存，或 ai record discard 丢弃）");
                return;
            }

            TrySaveRecording(name, false);
        }

        /// <summary>保存（同名先问覆盖）。返回是否已落盘。</summary>
        public bool TrySaveRecording(string name, bool overwrite)
        {
            AiRecordingSession session = Recording;
            try
            {
                string path = session.Save(name, overwrite);
                ShowMessage("AI: 已保存 " + Path.GetFileName(session.Status.LastSavedPath));
                Engine.Log.Information("[PlayerAi][rec] " + path);
                return true;
            }
            catch (AiCommandException exception) when (exception.Code == "already_exists")
            {
                string fileName = AiRecordingSession.SanitizeName(name) + ScatManifest.Extension;
                AiRecordingUi.ShowOverwriteDialog(fileName, delegate (bool confirmed)
                {
                    if (confirmed)
                        TrySaveRecording(name, true);
                    else
                        ShowMessage("AI: 未覆盖；换名字请用 ai record save <新名字>");
                });
                return false;
            }
            catch (AiCommandException exception)
            {
                ShowMessage("AI: 保存失败（" + exception.Message + "）");
                Engine.Log.Warning("[PlayerAi][rec] save failed: " + exception.Message);
                return false;
            }
        }

        /// <summary>当前的行为树宿主（哪个角色在跑树）。控制面装载树时确定。</summary>
        public IAiTreeHost TreeHost { get; private set; }

        /// <summary>
        /// 找一个可以接管的宿主：优先已绑定的 <see cref="TreeHost"/>，
        /// 否则取第一个"已接管且就绪"的角色。
        /// </summary>
        public IAiTreeHost ResolveTreeHost()
        {
            if (TreeHost != null && TreeHost.Enabled)
                return TreeHost;

            for (int i = 0; i < m_actors.Count; i++)
            {
                AiActor actor = m_actors[i];
                if (actor == null || !actor.Enabled || !actor.IsReady)
                    continue;
                TreeHost = actor;
                return actor;
            }
            return TreeHost;
        }

        /// <summary>绑定行为树宿主（控制面装载成功后调用）。</summary>
        public void BindTreeHost(IAiTreeHost host)
        {
            TreeHost = host;
        }

        /// <summary>包目录（实例可写 / Mod 只读），未初始化时为 null。</summary>
        public PackageRoots Roots
        {
            get { return m_packageRoots; }
        }

        /// <summary>推送式重载器（P0-5），未初始化时为 null。</summary>
        public PackageReloader Reloader
        {
            get { return m_reloader; }
        }

        /// <summary>树库（P0-11）：预编译常驻 + 毫秒级切换，未初始化时为 null。</summary>
        public TreeLibrary Library
        {
            get
            {
                if (m_library == null && m_reloader != null)
                {
                    m_library = new TreeLibrary(m_reloader) { Log = EventLog };
                    if (m_reloader != null)
                        m_reloader.Log = EventLog;
                }
                return m_library;
            }
        }

        /// <summary>
        /// 事件日志（P0-10）：`&lt;实例根&gt;/PlayerAi/Logs/PlayerAi.log`，按大小滚动、限量。
        /// 目录不可用时仍可返回对象（只是不写文件），保证调用点不用到处判空。
        /// </summary>
        public AiEventLog EventLog
        {
            get
            {
                if (m_eventLog == null)
                {
                    string directory = null;
                    PackageRoots roots = m_packageRoots;
                    if (roots != null && roots.InstanceRoot != null)
                        directory = Path.Combine(roots.InstanceRoot.Path, "Logs");
                    else
                        directory = Path.Combine(AppContext.BaseDirectory, "PlayerAi", "Logs");

                    m_eventLog = new AiEventLog(directory, AiEventLog.DefaultFileName,
                        PlayerAiConfig.LogMaxBytes, PlayerAiConfig.LogMaxFiles,
                        PlayerAiConfig.LogRecentCapacity)
                    {
                        Enabled = PlayerAiConfig.LogToFile
                    };
                }
                return m_eventLog;
            }
        }

        /// <summary>
        /// 惰性建立包目录 + 重载器，并把出厂示例包装进 Mod 只读目录（已有文件不动）。
        /// 失败只记日志：包不可用不应该影响 FSM 那条已经能跑的链路。
        /// </summary>
        public PackageReloader EnsurePackages()
        {
            if (m_reloader != null)
                return m_reloader;

            try
            {
                PackageRoots roots = PackageRoots.Discover();
                List<string> installed;
                string error;
                PackageTemplates.Install(roots, out installed, out error);
                if (!string.IsNullOrEmpty(error))
                    Engine.Log.Warning("[PlayerAi][pkg] template install: " + error);

                var options = new PackageLoadOptions { Roots = roots };
                m_packageRoots = roots;
                m_reloader = new PackageReloader(roots, options)
                {
                    PackageWatchSeconds = PlayerAiConfig.PackageWatchSeconds,
                    Log = EventLog
                };
                EventLog.Write("startup", "package folders: " + roots.Describe());

                // 出厂示例树：等世界就绪、有角色可接管时再装（装载要在 tick 边界做）。
                if (PlayerAiConfig.AutoLoadTreeOnStart)
                    m_autoLoadPending = true;
                Engine.Log.Information("[PlayerAi][pkg] package folders: " + roots.Describe());
                return m_reloader;
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][pkg] preparing package folders failed: "
                    + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        /// <summary>
        /// 在 tick 边界把排队的热重载应用到某个行为树运行时（P0-8 起由树驱动角色调用）。
        /// 返回 true 表示发生过替换。
        /// </summary>
        public bool ApplyPendingReloads(BtRuntime runtime)
        {
            if (m_reloader == null || runtime == null)
                return false;
            return m_reloader.ApplyPending(runtime);
        }

        public IReadOnlyList<AiActor> Actors
        {
            get { return m_actors; }
        }

        public int TickCount { get; private set; }

        /// <summary>上一次帧首 tick 里出错的角色数（异常隔离计数）。</summary>
        public int LastFailureCount { get; private set; }

        /// <summary>
        /// 行为树是否已暂停（`Home` 或控制面切换）。
        /// 暂停**不重置运行态**：恢复后从当前位置继续执行；暂停瞬间会释放 AI 按住的输入，
        /// 避免出现"暂停了还在走"。
        /// </summary>
        public bool Paused { get; private set; }

        /// <summary>最近一次暂停/恢复的原因（供 `ai.status` 与日志）。</summary>
        public string PauseReason { get; private set; }

        public void Pause(string reason)
        {
            if (Paused)
                return;

            Paused = true;
            PauseReason = reason;
            ReleaseAll();
            Engine.Log.Information("[PlayerAi] behaviour tree PAUSED (" + (reason ?? "?") + ")");
        }

        public void Resume(string reason)
        {
            if (!Paused)
                return;

            Paused = false;
            PauseReason = reason;
            Engine.Log.Information("[PlayerAi] behaviour tree RESUMED (" + (reason ?? "?") + ")");
        }

        /// <summary>切换暂停。返回 true 表示现在是暂停状态。</summary>
        public bool TogglePause(string reason)
        {
            if (Paused)
            {
                Resume(reason);
                return false;
            }

            Pause(reason);
            return true;
        }

        /// <summary>
        /// 帧末采样（P1）：录制中就把这一帧的输入收进轨道，并顺手记关键帧/语义事件。
        /// 由插件在 `Frame.Update`（帧末）调用 —— 那时 PlayerInput 已是这一帧的完整意图。
        /// </summary>
        public void SampleRecordingFrame()
        {
            if (m_recording == null || m_recording.Status.Phase != RecordingPhase.Recording)
                return;

            IAiTreeHost host = ResolveTreeHost();
            string error;
            try
            {
                PlayerInputSampler.TryCaptureFrame(m_recording,
                    host != null ? host.Sensors : null, ref m_lastInputFrame, out error);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][rec] sampling failed: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <summary>在本端玩家屏幕上显示一条小提示（暂停/恢复、录制状态等）。</summary>
        public void ShowMessage(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            for (int i = 0; i < m_actors.Count; i++)
            {
                AiActor actor = m_actors[i];
                if (actor == null || actor.Player == null || actor.Player.ComponentGui == null)
                    continue;

                try
                {
                    actor.Player.ComponentGui.DisplaySmallMessage(text, Color.White, false, false);
                }
                catch (Exception)
                {
                    // 界面未就绪时忽略即可，提示不是关键路径。
                }
            }
        }

        /// <summary>
        /// 是否是本端玩家：它的 GameWidget 在 SubsystemGameWidgets 里注册着（远端玩家没有本地视图）。
        /// 远端角色的操作要在远端执行、本端复现；在主机端直接操控远端角色会两边不同步，
        /// 所以接管前必须先过这一关。组件装配与角色自检都用同一套判断。
        /// Source: Survivalcraft/Game/ComponentPlayer.cs:35（GameWidget => PlayerData.GameWidget）
        /// </summary>
        public static bool IsLocalPlayerOf(ComponentPlayer player)
        {
            try
            {
                if (player == null || player.PlayerData == null)
                    return false;

                GameWidget widget = player.GameWidget;
                if (widget == null)
                    return false;

                SubsystemGameWidgets gameWidgets = GameManager.Project != null
                    ? GameManager.Project.FindSubsystem<SubsystemGameWidgets>(false)
                    : null;
                if (gameWidgets == null)
                    return false;

                for (int i = 0; i < gameWidgets.GameWidgets.Count; i++)
                {
                    if (ReferenceEquals(gameWidgets.GameWidgets[i], widget))
                        return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Register(AiActor actor)
        {
            if (actor == null || m_actors.Contains(actor))
                return;
            m_actors.Add(actor);
        }

        public bool Unregister(AiActor actor)
        {
            if (actor == null)
                return false;
            if (!m_actors.Remove(actor))
                return false;
            actor.Disable();
            return true;
        }

        public AiActor FindByPlayer(ComponentPlayer player)
        {
            if (player == null)
                return null;
            for (int i = 0; i < m_actors.Count; i++)
            {
                if (ReferenceEquals(m_actors[i].Player, player))
                    return m_actors[i];
            }
            return null;
        }

        /// <summary>
        /// 帧末调用：把真正的 tick 排到**下一帧帧首**执行（一帧最多排一次）。
        ///
        /// 为什么优先走 CmdBridgeMod 的帧首泵：
        ///   `Dispatcher.Dispatch` 在**主线程调用会立即执行**（Engine/Engine/Dispatcher.cs:40-44），
        ///   只有后台线程调用才入队到下一帧 `BeforeFrame`；而 `Mouse/Keyboard.AfterFrame` 会在帧末
        ///   清空 downOnce 数组（Engine/Engine/Input/Mouse.cs:132-138）——所以"脉冲式输入"必须写在帧首，
        ///   落在帧末会被同帧清掉。CmdBridgeMod 的 FrameStartPump 正是干这个的（后台线程派发），
        ///   依赖它的门面即可拿到帧首语义，无需自己起线程。
        /// </summary>
        public void ScheduleFrameStartTick(float frameDuration)
        {
            if (m_tickScheduled)
                return;

            m_tickScheduled = true;
            float deltaTime = frameDuration;

            if (TryScheduleOnBridgePump(deltaTime))
                return;

            // 退化路径：没有 CmdBridgeMod（或帧首泵不可用）时，仍按原来的方式排一次。
            // 注意此时 tick 实际落在帧末，脉冲式输入无效，但按住类输入（移动/视角）依然可用。
            if (!IsDispatcherReady())
            {
                m_tickScheduled = false;
                return;
            }

            Dispatcher.Dispatch(delegate
            {
                m_tickScheduled = false;
                TickFrameStart(deltaTime);
            });
        }

        /// <summary>用 CmdBridgeMod 的帧首泵排一次 tick（拿到真正的帧首语义）。</summary>
        private bool TryScheduleOnBridgePump(float deltaTime)
        {
            try
            {
                CmdBridgeMod.CmdBridgeMod bridge = CmdBridgeMod.CmdBridgeMod.Instance;
                CmdBridgeMod.CmdBridgeInput input = bridge != null ? bridge.Input : null;
                if (input == null || !input.IsAvailable || !input.FrameStartPumpRunning)
                    return false;

                bool posted = input.PostToFrameStart(delegate
                {
                    m_tickScheduled = false;
                    TickFrameStart(deltaTime);
                });

                if (!posted)
                    return false;

                if (!m_loggedPumpPath)
                {
                    m_loggedPumpPath = true;
                    Log.Information("[PlayerAi] AI tick is scheduled on CmdBridgeMod's frame-start pump "
                        + "(true frame-start semantics: input pulses survive).");
                }
                return true;
            }
            catch (Exception exception)
            {
                Log.Warning("[PlayerAi] frame-start pump unavailable, falling back: " + exception.Message);
                return false;
            }
        }

        /// <summary>帧首执行：逐个角色 tick；单个角色的异常不影响其它角色，也不让游戏崩溃。</summary>
        public void TickFrameStart(float deltaTime)
        {
            // 录制采样先于暂停判断：暂停的是"AI 决策"，不是"录制"
            //（边录边把 AI 停掉是完全合理的用法）。
            if (m_recording != null)
                m_recording.Tick(deltaTime);

            // 直接播放的动作包也在帧首推进（与录制同一条时间轴）
            if (m_actionPlayer != null)
            {
                BtResult result = m_actionPlayer.Tick(deltaTime);
                if (result != BtResult.InProgress)
                {
                    if (result == BtResult.Failed)
                    {
                        Engine.Log.Warning("[PlayerAi][act] replay failed: "
                            + (m_actionPlayer.LastError ?? "?"));
                        EventLog.Write("action-play-failed", m_actionPlayer.Describe());
                    }
                    else
                    {
                        EventLog.Write("action-play-done", m_actionPlayer.Describe());
                    }
                    m_actionPlayer.ReleaseInput();
                    m_actionPlayer = null;
                    m_actionName = null;
                }
            }

            // 暂停期间不推进决策（运行态保留，恢复后继续）。
            if (Paused)
                return;

            TickCount++;
            LastFailureCount = 0;

            // 可选低频扫描（默认关闭）：只负责"发现文件被外部改过"，真正的替换仍在 tick 边界。
            if (m_reloader != null)
                m_reloader.Tick(deltaTime);

            if (GameManager.Project == null)
            {
                // 没有世界：释放所有输入，避免残留"按住 W"。
                ReleaseAll();
                return;
            }

            // 拷贝一份再遍历：tick 期间允许注册/注销角色。
            m_scratch.Clear();
            m_scratch.AddRange(m_actors);

            for (int i = 0; i < m_scratch.Count; i++)
            {
                AiActor actor = m_scratch[i];
                try
                {
                    actor.Tick(deltaTime);
                }
                catch (Exception exception)
                {
                    LastFailureCount++;
                    Engine.Log.Warning("[PlayerAi] Actor tick failed ("
                        + exception.GetType().Name + ": " + exception.Message
                        + ") -> releasing its inputs and unregistering.");
                    Unregister(actor);
                }
            }

            ApplyPendingReloadsNow();
            TryAutoLoadTree();
        }

        /// <summary>
        /// 在 tick 边界把排队的热重载应用到宿主的行为树（**只在角色 tick 之间**，绝不在节点执行中途）。
        /// 返回 true 表示发生过替换。
        /// </summary>
        private bool ApplyPendingReloadsNow()
        {
            if (m_reloader == null || m_reloader.PendingCount == 0)
                return false;

            IAiTreeHost host = ResolveTreeHost();
            if (host == null || host.Tree == null)
                return false;

            try
            {
                bool replaced = m_reloader.ApplyPending(host.Tree);
                if (replaced)
                    host.Enabled = true;
                return replaced;
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][pkg] applying pending reloads failed: "
                    + exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        /// <summary>
        /// 世界就绪后自动装载启动包（`PlayerAiConfig.AutoLoadTreeOnStart`）。
        /// 只试一次：装不上就记日志并保持"待机"，不反复重试刷屏。
        /// </summary>
        private void TryAutoLoadTree()
        {
            if (!m_autoLoadPending || m_reloader == null)
                return;

            IAiTreeHost host = ResolveTreeHost();
            if (host == null)
                return;

            m_autoLoadPending = false;
            try
            {
                TreeReloadResult result = m_reloader.Load(PlayerAiConfig.StartupTreePackage, host.Tree);
                if (result.Replaced)
                {
                    if (!host.Tree.IsRunning)
                        host.Tree.Start();
                    host.Enabled = true;
                    TreeHost = host;
                    Engine.Log.Information("[PlayerAi][pkg] startup tree ready: " + result.Describe());
                    EventLog.Write("startup-tree", result.Describe());
                }
                else
                {
                    Engine.Log.Warning("[PlayerAi][pkg] startup tree NOT loaded: " + result.Describe());
                    EventLog.Write("startup-tree-failed", result.Describe());
                }
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][pkg] startup tree failed: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <summary>释放全部输入并清空角色（世界卸载、Mod 卸载、手动急停）。</summary>
        public void ReleaseAll()
        {
            for (int i = 0; i < m_actors.Count; i++)
                m_actors[i].Disable();
        }

        public void Clear()
        {
            ReleaseAll();
            m_actors.Clear();
        }

        /// <summary>Dispatcher 是否已初始化（游戏启动瞬间它还不存在）。</summary>
        public static bool IsDispatcherReady()
        {
            try
            {
                int ignored = Dispatcher.MainThreadId;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}

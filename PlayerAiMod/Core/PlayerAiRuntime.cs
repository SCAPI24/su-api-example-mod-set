using Engine;
using Engine.Graphics;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;
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
    public sealed class PlayerAiRuntime : IMemoryVersionSource
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

        /// <summary>世界外那一帧释放过一次没有（见 TickFrameStart 里的注释）。</summary>
        private bool m_releasedForNoProject;

        /// <summary>相位（§4.13）跟踪 + 去抖（G15）：只有它承认的边界才会触发交接。</summary>
        private readonly PhaseTracker m_phases = new PhaseTracker(PlayerAiConfig.PhaseConfirmFrames);

        /// <summary>过渡态闸门累计拦下的帧数（见 <see cref="PhaseGatedFrames"/>）。</summary>
        private long m_phaseGatedFrames;

        /// <summary>相位 → 树包绑定（`ai.pool.bind`；空 = 不自动换树）。</summary>
        private PhaseTreeBinding m_phaseBinding;

        /// <summary>绑定没生效的原因去重（同一种原因只报一次）。</summary>
        private readonly HashSet<string> m_phaseBindingWarned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>上一次看到的"跑完了几轮"（用来识别循环树的**步骤边界**，见 <see cref="TickPoolSchedule"/>）。</summary>
        private long m_poolLastLoops;

        private AiRecordingSession m_recording;
        private TreeLibrary m_library;
        private ScatPlayer m_actionPlayer;
        private string m_actionName;
        private ActionScriptRuntime m_scriptRuntime;
        private PoolScheduler m_pool;
        private bool m_poolEnabled;
        private bool m_poolHadTree;
        private GameActionServices m_actionServices;
        private LayaRuntimeService m_laya;
        private MemoryAssetStore m_assets;
        private AutoSaveCache m_assetCache;
        private string m_assetActive;
        private string m_assetSeenSource;
        private long m_assetEditCount;
        private long m_assetCaptureCount;
        private bool m_assetTreeWasDirty;
        private bool m_assetRecoveredWarned;
        private AssetRecoveryTask m_assetRetry;
        private AssetOrigin m_assetDirtyOrigin = AssetOrigin.Unknown;
        private double m_assetAutoSaveTimer;
        private string m_assetCachedHash;
        private string m_assetContentHash;
        private CmdBridgeMod.CmdBridgeInputFrame m_lastInputFrame;
        private AiEventLog m_eventLog;
        private ControllerTreeHost m_controller;

        // ---------------------------------------------------------------- 录制（P0-9）

        /// <summary>
        /// 录制会话（全局一个）。落盘目录是**唯一的包目录** `<实例根>/PlayerAi/BehaviorTrees/` ——
        /// 与树包同路径（计划 §4.4）。
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
        /// 动作包目录：与树包**同一个**目录（`&lt;实例根&gt;/PlayerAi/BehaviorTrees`）。
        /// 以前还有第二级"Mod 分发目录兜底"，2026-09-12 按用户要求简化成单一目录。
        /// </summary>
        public List<string> ResolveActionDirectories()
        {
            var directories = new List<string>();

            string primary = ResolveActionDirectory();
            if (!string.IsNullOrEmpty(primary))
                directories.Add(primary);

            return directories;
        }

        // ---------------------------------------------------------------- 动作包（P1）

        /// <summary>当前直接播放的动作包（未播放时为 null）。</summary>
        public ScatPlayer ActionPlayer
        {
            get { return m_actionPlayer; }
        }

        /// <summary>
        /// 池调度要用树库：把"怎么拿树库"登记给 <see cref="PoolRuntimeHost"/>，
        /// 于是 <see cref="BtPoolCallTask"/> 不必认识 PlayerAiRuntime（编辑器才能编译它）。
        /// </summary>
        public static void InstallPoolHost()
        {
            PoolRuntimeHost.LibraryProvider = () => Instance != null ? Instance.Library : null;
        }

        /// <summary>
        /// Laya（System One）服务（P3）：配置 + 客户端 + 问题库。同样由运行时持有当唯一权威。
        /// 配置从 `<实例根>/PlayerAi/Laya.local.json` + 环境变量 `LAYA_API_KEY` 装载（§5.5 预填链）。
        /// </summary>
        public LayaRuntimeService Laya
        {
            get
            {
                if (m_laya == null)
                {
                    string instanceRoot = GameActionServices.ResolveInstanceRoot(this);
                    LayaConfig config = LayaConfig.Load(instanceRoot);
                    m_laya = new LayaRuntimeService(this, config, LayaRuntimeService.BankDirectoriesFor(instanceRoot));
                    // §4.12：问题库取不到时送进"四码分流 + 退避 + 熔断"那条链
                    // （编辑器正在原子替换 .qbank 的那一瞬间撞上"读不到"是常态，
                    //   几十毫秒后重取就好；而"校验不过"会被分流成**不重取**）。
                    m_laya.BankFailure = delegate(string name, string error)
                    {
                        NoteAssetFailure(name, error, null, AssetFailureKind.None,
                            AssetResourceKind.QuestionBank);
                    };
                    m_laya.BankSuccess = delegate(string name) { NoteAssetSuccess(name); };
                    // 磁盘上的库被改过 → 事件日志留一行（这是"编辑器改完就能立刻生效"的那一步）
                    m_laya.BankReloaded = delegate(string name)
                    {
                        EventLog.Write("laya-bank", name + " changed on disk -> reloaded");
                    };
                    LayaRuntimeService.Current = m_laya;
                    LayaRuntimeHost.Current = m_laya;
                    // 让纯逻辑层（`BtLayaAskTask`）也能**按需**拿到服务：它不认识本类，
                    // 只认这个委托（直接引用运行时会把编辑器的编译打断，见 ILayaRuntime 的注释）。
                    // 这里故意返回字段而不是递归访问属性：属性正在计算中。
                    LayaRuntimeHost.Provider = delegate { return m_laya; };
                }
                return m_laya;
            }
        }
        /// <summary>
        /// 动作脚本服务（P1）。**由运行时持有并当唯一权威**，不再依赖跨程序集表面的静态字段 ——
        /// 实机踩过：命令层读到的静态与运行时写入的静态会不一致（重启后偶尔全部 `not_ready`）。
        /// 懒创建，创建时顺带 Publish 一份给静态（兼容其它读取点）。
        /// </summary>
        public GameActionServices ActionServices
        {
            get
            {
                if (m_actionServices == null)
                {
                    string instanceRoot = GameActionServices.ResolveInstanceRoot(this);
                    m_actionServices = new GameActionServices(this, GameActionServices.DirectoriesFor(instanceRoot));
                    GameActionServices.Publish(m_actionServices);
                }

                // 纯逻辑层（`BtRunActionScriptTask`）要能**按需取**：它不认识本类，只认这个委托
                // （直接引用运行时会把编辑器的编译打断，见 `ActionPlayServices.Provider` 的注释）。
                // 每次访问都重挂：静态缝可能被别处清掉，清掉后树里所有动作节点都会以
                // `action services are not initialized` 失败（A34，平板实测）。
                ActionPlayServices.Provider = delegate { return m_actionServices; };
                return m_actionServices;
            }
        }

        /// <summary>
        /// 动作脚本的运行入口（P1）。与动作包回放并列，两者共用帧首时间轴。
        /// 懒创建：没用到脚本时不产生任何对象。
        /// </summary>
        public ActionScriptRuntime ScriptRuntime
        {
            get
            {
                if (m_scriptRuntime == null)
                    m_scriptRuntime = new ActionScriptRuntime(this);
                return m_scriptRuntime;
            }
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
                // §4.12：动作包取不到也走同一条分流链。重取只做"读取+校验"，
                // **不会自己重放** —— 重放的副作用是角色真的动起来（见 RetryActionAsset）。
                NoteAssetFailure(nameOrPath, PackageCodes.FileMissing + ": " + nameOrPath,
                    null, AssetFailureKind.None, AssetResourceKind.Action);
                throw new AiCommandException("file_missing",
                    "action package not found: '" + (nameOrPath ?? "<none>") + "' in "
                    + (directories.Count > 0 ? string.Join("; ", directories.ToArray())
                        : "<no directory>"));
            }

            ScatActionPackage package;
            PackageReport report;
            if (!ScatValidator.TryLoad(path, out package, out report))
            {
                NoteAssetFailure(path, AssetFailureClassifier.TextOf(report)
                    ?? (PackageCodes.ZipInvalid + ": " + path),
                    AssetFailureClassifier.DetailOf(report),
                    AssetFailureClassifier.ClassifyReport(report), AssetResourceKind.Action);
                throw new AiCommandException("invalid_argument",
                    "action package is invalid: "
                    + (report.FirstError != null ? report.FirstError.Describe() : report.Summary()));
            }
            // 读到了 → 清掉这个资源的重试历史与失败标记
            NoteAssetSuccess(path);
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

        /// <summary>
        /// **控制器宿主**：行为树绑定的对象。它不是角色（用户明确要求），所以它一直存在 ——
        /// 主菜单、世界里、退出世界后都是同一个宿主，树的运行态因此能跨世界切换保留。
        /// 惰性创建；命令层与帧首 tick 共用同一个实例。
        /// </summary>
        public IAiTreeHost ControllerHost
        {
            get { return ControllerHostInternal; }
        }

        /// <summary>`ControllerTreeHost` 是内部类型（外部只该通过 <see cref="IAiTreeHost"/> 用它），
        /// 但帧首推进 / 绑定世界输入需要具体类型上的成员。</summary>
        internal ControllerTreeHost ControllerHostInternal
        {
            get
            {
                if (m_controller == null)
                    m_controller = new ControllerTreeHost(new UiOnlyActuator(), m_eventLog);
                return m_controller;
            }
        }

        /// <summary>
        /// 行为树宿主 = **控制器**。顺带把"现在能不能驱动某个角色"同步进去：
        /// 世界里接上第一个就绪的角色，世界没了（或没有可驱动角色）就摘掉 ——
        /// **只换输入路由，不卸载树、不重置运行态**（这正是"进游戏前点按钮、退出世界后接着干"
        /// 能成立的原因；换宿主就意味着树被卸下重装，运行态丢失）。
        /// </summary>
        public IAiTreeHost ResolveTreeHost()
        {
            SyncWorldInput();
            return ControllerHostInternal;
        }

        /// <summary>把当前世界里"就绪的本端角色"接给控制器（没有就摘掉）。</summary>
        private void SyncWorldInput()
        {
            ControllerTreeHost controller = ControllerHostInternal;
            AiActor best;
            TryFindActor(true, out best);
            controller.Bind(best);
        }

        /// <summary>
        /// 扫角色表。`readyOnly=true` 只认"AI 能驱动"的（**输入路由**用：没启用的角色不该被接管），
        /// `false` 只认"实体存在"（**相位判定**用，见 <see cref="AiActor.Exists"/> 的注释）。
        /// 两种都优先本端玩家（远端角色必须在远端执行，本端驱动它会两边不同步）。
        /// </summary>
        private bool TryFindActor(bool readyOnly, out AiActor found)
        {
            found = null;
            if (GameManager.Project == null)
                return false;

            for (int i = 0; i < m_actors.Count; i++)
            {
                AiActor actor = m_actors[i];
                if (actor == null)
                    continue;
                if (readyOnly ? !actor.IsReady : !actor.Exists)
                    continue;
                if (found == null || (actor.IsLocalPlayer && !found.IsLocalPlayer))
                    found = actor;
            }
            return found != null;
        }

        /// <summary>
        /// 当前相位（plan §4.13）：`front` / `loading` / `world`。
        ///
        /// 只回答**缓存的**那个值（由每一帧的 <see cref="UpdatePhase"/> 维护）：命令层可能在别的线程读它，
        /// 而"扫一遍角色表"必须留在主线程那一帧里。模块刚加载、第一帧还没跑时默认 `front` —— 那时候
        /// 世界确实还没加载。
        /// </summary>
        public string Phase
        {
            get { return m_phases.Phase; }
        }

        /// <summary>相位切换过几次（`ai.status` 里看得到，复盘"为什么那时候树不动"）。</summary>
        public long PhaseChanges
        {
            get { return m_phases.Changes; }
        }

        /// <summary>被过渡态闸门拦下的帧数（累计）。运行中 `>0` 且还在涨 = 现在正被闸住。</summary>
        public long PhaseGatedFrames
        {
            get { return m_phaseGatedFrames; }
        }

        /// <summary>本帧树是不是被闸住的（`loading`）。</summary>
        public bool PhaseGateActive
        {
            get { return PhaseNames.IsLoading(m_phases.Phase); }
        }

        /// <summary>
        /// 相位观测 + 交接（plan §4.13）：**世界的进/出边界上做一次**，不是每帧做。
        ///
        /// 每一帧都做会在主菜单里每帧清一次引擎输入数组的按下沿 —— 真实鼠标的按下沿写在同一个数组里，
        /// 于是主菜单的按钮永远派生不出 Click（**实机踩过**，见 <see cref="TickFrameStart"/> 里那段注释）。
        /// 所以"释放/清缓存"必须挂在**状态变化**上，而不是挂在"当前状态"上；
        /// **状态变化**本身再由 <see cref="PhaseTracker"/> 去抖（G15：连续 N 帧才算数）。
        /// </summary>
        private void UpdatePhase()
        {
            AiActor actor;
            // 相位里的"有角色"用的是**实体存在**，不是"AI 就绪"（见 AiActor.Exists）：
            // `ai.disable` 不该让相位从 world 掉回 loading，否则状态面板会谎报、树还会被闸住。
            string observed = PhaseNames.Of(GameManager.Project != null, TryFindActor(false, out actor));

            PhaseStep step = m_phases.Observe(observed);
            if (step.From == null)
            {
                // 首次观测：只记相位（这一帧没有任何"旧相位"的残留要交接）
                EventLog.Write("phase", "init " + step.To);
                return;
            }
            if (step.Changed)
                ApplyPhaseStep(step);
        }

        /// <summary>HUD 状态行（G19）。</summary>
        public AiHudOverlay Hud
        {
            get { return m_hud ?? (m_hud = new AiHudOverlay()); }
        }

        private AiHudOverlay m_hud;
        private double m_hudClock;

        /// <summary>
        /// 每帧更新 HUD 那一行（plan G19）。**只读**：它不改变任何决策状态，
        /// 也不碰输入（`AiHudOverlay` 的控件是 `IsHitTestVisible=false`）。
        /// </summary>
        private void UpdateHud(float deltaTime)
        {
            m_hudClock += deltaTime;

            var facts = new HudStatusFacts
            {
                // `ai.disable`（模式 inactive）时整行不显示：那时候"AI 在干嘛"这个问题没有意义，
                // 留一行 "ai=idle" 反而让人以为它还活着。
                Visible = ControllerHostInternal.Mode != AiMode.Inactive,
                Paused = Paused,
                PauseReason = PauseReason,
                Phase = Phase,
                GateActive = PhaseGateActive,
                Mode = ControllerHostInternal.Mode.ToWireName(),
                FailCount = HudFailCount()
            };

            BtRuntime tree = ControllerHostInternal.Tree;
            if (tree != null)
            {
                facts.TreeId = tree.TreeId;
                facts.TreeRunning = tree.IsRunning;
                facts.Loops = tree.CompletedLoops;
                facts.Error = tree.LastError;
                facts.Node = LastPathSegment(tree.Snapshot().DescribeActivePath());
            }

            try
            {
                if (m_scriptRuntime != null)
                {
                    Dictionary<string, object> status = m_scriptRuntime.Status();
                    object playing;
                    if (status.TryGetValue("playing", out playing) && playing is bool && (bool)playing)
                    {
                        facts.Action = status.ContainsKey("name") ? status["name"] as string : null;
                        object elapsed;
                        if (status.TryGetValue("elapsedMs", out elapsed))
                            facts.ActionSeconds = Convert.ToDouble(elapsed, CultureInfo.InvariantCulture) / 1000.0;
                    }
                }
            }
            catch (Exception)
            {
            }

            try
            {
                LayaRuntimeService laya = Laya;
                if (laya != null)
                {
                    Dictionary<string, object> info = laya.Client.Describe();
                    object busy;
                    if (info.TryGetValue("busy", out busy) && busy is bool && (bool)busy)
                    {
                        facts.LayaBusy = true;
                        object ms;
                        if (info.TryGetValue("inFlightMs", out ms))
                            facts.LayaSeconds = Convert.ToDouble(ms, CultureInfo.InvariantCulture) / 1000.0;
                    }
                }
            }
            catch (Exception)
            {
            }

            Hud.Update(m_hudClock, facts);
        }

        /// <summary>
        /// 连败计数（动作反射层阶梯用的 `fail.count`）。拿不到就当 0：
        /// HUD 上少一个数字比因为读黑板抛异常而整行失效要好。
        /// </summary>
        private int HudFailCount()
        {
            try
            {
                AiBlackboard board = ControllerHostInternal.Blackboard;
                int value;
                return board != null && board.TryGet<int>(PackageTemplates.FailCountKey, out value) ? value : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 活动节点路径的**末段**。路径的形状是 `Root#root > Selector#sel > Task.Wait#idle`
        /// —— 分隔符是 `&gt;`，**不是 `/`**。实测踩到：按 `/` 切等于没切，
        /// 整条路径原样进了 HUD，里面的空格把它撑成好几个 token：
        /// 既破坏了"一项一个 token"的编码，又把后面的 `act=` 挤出了预算。
        /// 空路径与 `&lt;none&gt;` 哨兵都返回 null（没有活动节点就不该显示这一项）。
        /// </summary>
        private static string LastPathSegment(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string value = path.Trim();
            int separator = value.LastIndexOf('>');
            if (separator >= 0 && separator + 1 < value.Length)
                value = value.Substring(separator + 1).Trim();
            return value.Length == 0 || value == "<none>" ? null : value;
        }

        /// <summary>照 <see cref="PhaseStep"/> 执行交接（自检可以直接喂一个人造的 step 来钉住执行效果）。</summary>
        public void ApplyPhaseStep(PhaseStep step)
        {
            if (!step.Changed)
                return;

            // 1) 输入：旧相位按住的键/鼠标键不许漏进新相位（世界外尤其致命：菜单会自己乱走）
            if (step.ReleaseInput)
            {
                ReleaseInputForPhase();   // 控制器那一层：世界里=角色输入路由，世界外=UI 点击注入
                ReleaseAll();             // 角色那一层
            }

            // 2) Laya：在途请求取消 + 答案缓存清空 + 失败去重清空（跨相位的答案绝不能落地，plan G8）
            if (step.ClearLaya && m_laya != null)
            {
                m_laya.Client.Reset(step.Describe());
                m_laya.ForgetAllReportedBanks();
            }

            // 3) "世界外只释放一次"的闸门与新相位对齐：本步已经释放过了，就别让主菜单分支再释放一次
            if (step.To != null)
                m_releasedForNoProject = PhaseNames.IsFront(step.To);

            EventLog.Write("phase", step.Describe());

            // 4) 相位 → 树包绑定（§4.13 的"两套树包"）：**只在边沿应用一次**，
            //    真正换树交给池调度（D9：换树只有一个来源），于是"正在跑的那一步"永不被打断。
            ApplyPhaseBinding(step.To);
        }

        /// <summary>相位 → 树包绑定（`ai.pool.bind`）。null = 没绑。</summary>
        public PhaseTreeBinding PhaseBinding
        {
            get { return m_phaseBinding; }
            set { m_phaseBinding = value; }
        }

        /// <summary>
        /// 按当前相位应用一次绑定（设置绑定时立刻调一次，不然"开局就在世界外"这种情况
        /// 一辈子等不到相位边沿）。写的是 `pool.next` —— 池在**步骤边界**自己去切。
        /// </summary>
        public string ApplyPhaseBinding(string phase)
        {
            if (m_phaseBinding == null || m_phaseBinding.Count == 0)
                return null;

            string effective = string.IsNullOrEmpty(phase) ? m_phases.Phase : phase;
            string key;
            if (!m_phaseBinding.TryResolve(effective, out key))
                return null;

            if (m_pool == null || m_pool.Pool.Count == 0)
            {
                ReportPhaseBindingOnce("pool-empty",
                    "phase binding (" + m_phaseBinding.Describe() + ") is ignored: the pool is empty");
                return null;
            }
            if (m_pool.Find(key) == null)
            {
                ReportPhaseBindingOnce("not-in-pool-" + key,
                    "phase binding says " + effective + "=" + key + " but that package is not in the pool ("
                    + m_pool.DescribePool() + ")");
                return null;
            }

            PoolEntry active = m_pool.Active;
            if (active != null && string.Equals(active.Key, key, StringComparison.OrdinalIgnoreCase))
                return null;   // 已经是它了：不重复写（否则池每帧都被推一次）

            AiBlackboard blackboard = ControllerHostInternal.Blackboard;
            if (blackboard == null)
                return null;

            blackboard.Set(new AiBlackboardKey<string>(PoolScheduler.KeyNext), key);
            EventLog.Write("pool-bind", effective + " -> " + key);
            return key;
        }

        /// <summary>绑定没生效时**每种原因只报一次**（每帧报会把事件日志刷满）。</summary>
        private void ReportPhaseBindingOnce(string key, string message)
        {
            if (!m_phaseBindingWarned.Add(key))
                return;
            EventLog.Write("pool-bind-skip", message);
            Engine.Log.Warning("[PlayerAi][pool] " + message);
        }

        /// <summary>释放控制器（含 UI 注入）那一层 —— 世界外它管着菜单点击，也必须放干净。</summary>
        private void ReleaseInputForPhase()
        {
            if (m_controller != null)
                m_controller.ReleaseInput();
        }


        /// <summary>控制面/插件"接管控制器"（`ai.enable` 也走这里）。</summary>
        public void EnableController(string reason)
        {
            ControllerTreeHost controller = ControllerHostInternal;
            controller.Enabled = true;
            EventLog.Write("enable", (reason ?? "?") + " -> " + controller.Situation
                + (controller.PlayerName != null ? " player=" + controller.PlayerName : string.Empty));
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

                // 树里 `Task.Log` 的行也进这份事件日志（见 BtLogSink 的注释：
                // 没有这一步，"异常分支先记日志再清旗标"在默认配置下一条都看不到）。
                //
                // ⚠️ **每次访问都重挂**（而不是只在创建那一次挂）：`BtLogSink.Write` 是全局静态缝，
                //   任何写它的地方（自检、编辑器、别的 Mod）都能把它清掉；清掉之后**整局树内记账全哑**，
                //   而现象只是"日志里少了几行"，极难查到。
                //   （实机踩过：自检跑完 sink=null，之后 demo.laya 的决策痕迹一条都不记。）
                BtLogSink.Write = delegate(string kind, string message)
                {
                    m_eventLog.Write(string.IsNullOrEmpty(kind) ? BtLogSink.TreeKind : kind, message);
                };
                return m_eventLog;
            }
        }

        /// <summary>
        /// 惰性建立包目录 + 重载器，并把出厂示例包装进包目录（已有文件不动）。
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

                // 出厂示例动作脚本（P1）：装在 `<实例根>/PlayerAi/Scripts/`，
                // 于是装完 Mod 就能 `ai.action.script.list` 看到东西、直接 run 起来。
                try
                {
                    PackageRoot instanceRoot = roots.InstanceRoot;
                    if (instanceRoot != null)
                    {
                        var playerAiDirectory = System.IO.Directory.GetParent(instanceRoot.Path);
                        string gameRoot = playerAiDirectory != null && playerAiDirectory.Parent != null
                            ? playerAiDirectory.Parent.FullName
                            : null;
                        List<string> scriptFiles;
                        string scriptError;
                        ActionScriptTemplates.Install(gameRoot, out scriptFiles, out scriptError);
                        List<string> questionFiles;
                        string questionError;
                        QuestionBankTemplates.Install(gameRoot, out questionFiles, out questionError);
                        if (!string.IsNullOrEmpty(questionError))
                            Engine.Log.Warning("[PlayerAi][state] question bank install: " + questionError);
                        if (!string.IsNullOrEmpty(scriptError))
                            Engine.Log.Warning("[PlayerAi][act] script template install: " + scriptError);
                    }
                }
                catch (Exception scriptException)
                {
                    Engine.Log.Warning("[PlayerAi][act] installing example scripts failed: "
                        + scriptException.GetType().Name + ": " + scriptException.Message);
                }

                var options = new PackageLoadOptions { Roots = roots };
                // G4：装载期就核对问题库引用（plan 的"构建期引用校验"）。
                // 没有这道闸门时，"题目改名 / 选项 key 改名 / 答案类型写错"要等到**第一次真正发问**
                // 才现形，而那时已经付过往返了、而且表现是"分支永远不成立"这种静默降质。
                // 库目录与 Laya 运行时同源；拿不到实例根时留 null ——
                // 校验器会报 `bank.unverified` 警告（**不是**假装通过）。
                if (roots != null && roots.InstanceRoot != null)
                {
                    var playerAiDirectory = System.IO.Directory.GetParent(roots.InstanceRoot.Path);
                    string bankRoot = playerAiDirectory != null && playerAiDirectory.Parent != null
                        ? playerAiDirectory.Parent.FullName
                        : null;
                    if (!string.IsNullOrEmpty(bankRoot))
                    {
                        options.Banks = new QuestionBankDirectorySource(
                            QuestionBankDirectorySource.DirectoriesFor(bankRoot));
                    }
                }
                m_packageRoots = roots;
                m_reloader = new PackageReloader(roots, options)
                {
                    PackageWatchSeconds = PlayerAiConfig.PackageWatchSeconds,
                    Log = EventLog
                };
                // G25：推送重载要能问"内存库现在是什么版本"，否则"编辑器保存"会静默盖掉
                // 内存里那些还没落盘的改动。这里问的是**运行时**（它会先按需抓一次活树），
                // 不是直接问内存库 —— 库的 dirty 是"抓取过之后"的状态，会有最多一个节流窗口的延迟。
                m_reloader.MemoryVersion = this;
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
            actor.ReleaseInput();
            // 摘掉的正是当前接给控制器的那个 → 立刻断开，免得"角色没了还有人在按键"。
            if (ReferenceEquals(ControllerHostInternal.World, actor))
                ControllerHostInternal.Bind(null);
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

            if (TryScheduleOnBridgePump(frameDuration))
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
                TickFrameStart(ConsumeRealDelta(frameDuration));
            });
        }

        /// <summary>
        /// 帧首 tick 的 `deltaTime` = **距上一次 tick 的真实时间**，不是"排这一帧的帧时长"。
        ///
        /// 为什么必须这样（实测踩过，用户报的现象是"同一个包以前能进游戏，现在选世界那步没了"）：
        /// 帧首泵是靠 AutoResetEvent 逐帧举手、后台线程收信号再派发的，**信号会合并**——
        /// 泵慢一拍就少跑一帧的 tick，可是每次 tick 拿到的 `Time.FrameDuration` 仍然只有
        /// "它自己那一帧"的时长。于是 AI 的时间轴比真实时间慢：上一版进游戏包的
        /// manifest 是 `frames=723 / duration=2.231s`，而 723 帧在当时的帧率（~180 FPS）
        /// 下是 4.0s —— 时间轴只走了 56%。回放时泵不漏帧，时间轴≈真实时间，
        /// 于是包里"隔 0.45s 的点世界列表"实际发生在 Play 屏切场动画正中间，被引擎如实拒绝。
        ///
        /// 这里改成单调时钟测量（并且 clamp 到 0.25s，避免长时间卡顿后一次性跳一大步），
        /// 录制与回放就用同一条"真实时间"轴，包里的 `t` 才真的是秒。
        /// </summary>
        private float ConsumeRealDelta(float fallbackSeconds)
        {
            double now = m_tickClock.Elapsed.TotalSeconds;
            if (m_lastTickSeconds < 0.0)
            {
                m_lastTickSeconds = now;
                return fallbackSeconds;
            }

            double elapsed = now - m_lastTickSeconds;
            m_lastTickSeconds = now;
            if (elapsed <= 0.0)
                return 0f;
            return (float)Math.Min(elapsed, MaxTickDeltaSeconds);
        }

        /// <summary>单调时钟（只用来量"两次 tick 之间过了多久"）。</summary>
        private readonly System.Diagnostics.Stopwatch m_tickClock =
            System.Diagnostics.Stopwatch.StartNew();

        private double m_lastTickSeconds = -1.0;

        /// <summary>单次 tick 最多认多少秒（游戏卡住/最小化时不要把时间轴一次拉爆）。</summary>
        private const double MaxTickDeltaSeconds = 0.25;

        /// <summary>用 CmdBridgeMod 的帧首泵排一次 tick（拿到真正的帧首语义）。</summary>
        private bool TryScheduleOnBridgePump(float frameDuration)
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
                    TickFrameStart(ConsumeRealDelta(frameDuration));
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

            // 动作脚本（P1）也在帧首推进：与录制/回放同一条时间轴，
            // 于是"人类的操作、录制的回放、脚本生成的输入"三种来源不会互相错帧。
            if (m_scriptRuntime != null)
                m_scriptRuntime.Tick(deltaTime);

            // 相位（§4.13）：**放在暂停判断之前** —— 暂停期间人在主菜单退出世界，
            // 相位变了却没人观察，恢复时就会拿着世界内的缓存去跑世界外的树。
            UpdatePhase();

            // HUD（G19）也放在暂停判断**之前**：暂停时这一行恰恰最该说"paused"，
            // 放到后面就等于"一暂停 HUD 就不更新了"，人看到的是上一次的状态（比没有更坏）。
            UpdateHud(deltaTime);

            // 暂停期间不推进决策（运行态保留，恢复后继续）。
            if (Paused)
                return;

            TickCount++;
            LastFailureCount = 0;

            // 可选低频扫描（默认关闭）：只负责"发现文件被外部改过"，真正的替换仍在 tick 边界。
            if (m_reloader != null)
                m_reloader.Tick(deltaTime);

            // 世界有没有加载，只影响**输入路由**，不影响树跑不跑：
            SyncWorldInput();                 // 世界里接上就绪角色，世界外摘掉（只换路由，不卸载树）
            ApplyPendingReloadsNow();         // 热重载在 tick 边界应用（世界内外都一样）

            try
            {
                ControllerTreeHost controller = ControllerHostInternal;
                controller.Paused = Paused;       // 全局暂停对世界外同样有效
                controller.IsRecording = m_recording != null && m_recording.IsActive;

                // §4.13 过渡态闸门：世界已加载、本端角色还没就绪 —— 树**一拍都不推进**
                // （「过渡态只 obs.waitFor」）。不闸会怎样：世界树在这几十帧里每个节点都取不到角色，
                // 连续失败 → 兜底分支被触发、失败计数被打脏，等角色真就绪时树已经跑到"异常恢复"里去了。
                // 闸门是可逆的：角色一就绪就照常从当前节点继续（节点自己的 ActiveTime 只在 tick 时累加，
                // 所以过渡期不会被判超时）。
                if (!PhaseNames.IsLoading(m_phases.Phase))
                {
                    controller.Tick(deltaTime);   // 唯一的行为树 tick

                    // 池调度（P5）：**在 tick 边界**按 PoolScheduler 的决策换树。
                    // 放在 controller.Tick 之后 = 本帧的树已经跑完它这一拍，换树不会截断正在跑的那一步；
                    // 下一帧 controller.Tick 跑的就是新树。这就是 S1 在实现里的落点。
                    // 它与树 tick 同进同出：树被闸住时"上一帧结果"是陈旧的，照它换树会换错。
                    TickPoolSchedule();
                }
                else
                {
                    // 被闸住的帧要**数得出来**：`gatedFrames` 是"闸门真的生效过"的唯一硬证据，
                    // 也是人复盘"树为什么不动"时该看的字段（否则只能靠事件日志推断）。
                    m_phaseGatedFrames++;
                }
            }
            catch (Exception exception)
            {
                LastFailureCount++;
                Engine.Log.Warning("[PlayerAi] controller tick failed ("
                    + exception.GetType().Name + ": " + exception.Message + ")");
                ControllerHostInternal.ReleaseInput();
            }

            // 资源库（P6）：活动树换了就登记进内存库；内存里有改动就按节流写自动缓存。
            // 放在池调度之后 = 本帧的最终活动树就是被登记的那一棵（顺序错了会登记上一棵）。
            SyncActiveAsset();
            TickAssetAutoSave(deltaTime);

            // 拒包重取（§4.12）：退避到期的资源在这一拍再取一次。放在树 tick 之后，
            // 于是"重取成功换树"与池切换一样，都落在步骤边界上。
            TickAssetRecovery(deltaTime);

            if (GameManager.Project == null)
            {
                // 没有世界：把角色输入都释放掉（避免残留"按住 W"）。
                // 控制器本身刚刚已经 tick 过了 —— 它现在的路由是"只点 UI"。
                //
                // ⚠️ **只在刚回到世界外的那一帧释放一次**：这个分支在主菜单是**每帧**都会走到的常态，
                //    而"释放"会清掉引擎输入数组里的按下沿；真实鼠标的按下沿写在同一个数组里，
                //    每帧清一次 → 玩家在主菜单点按钮只有按下视觉、永远派生不出 Click
                //    （用户实测：进过世界、Quit 回主菜单后，四个按钮几乎点不动）。
                if (!m_releasedForNoProject)
                {
                    ReleaseAll();
                    m_releasedForNoProject = true;
                }
                return;
            }

            m_releasedForNoProject = false;

            TryAutoLoadTree();
        }

        /// <summary>
        /// 在 tick 边界把排队的热重载应用到宿主的行为树（**只在角色 tick 之间**，绝不在节点执行中途）。
        /// 返回 true 表示发生过替换。
        /// </summary>
        /// <summary>
        /// 池调度（P5）：在 **tick 边界**按 <see cref="PoolScheduler"/> 的决策换树。
        ///
        /// 三条不变量的实现落点：
        ///   · **S1**：本方法只在 `controller.Tick()` **之后**调用 → 本帧的树已经跑完这一拍，
        ///     换树不会截断正在跑的那一步。
        ///   · **S2**：切换走既有 `TreeLibrary.Switch`，它内部会 `ResetSubtreeState()`。
        ///   · **S3**：切之前先 `ReleaseInput()` —— 否则旧树按着的键会漏到新树。
        /// </summary>
        public void TickPoolSchedule()
        {
            if (m_pool == null || !m_poolEnabled)
                return;

            ControllerTreeHost controller = ControllerHostInternal;
            BtRuntime tree = controller != null ? controller.Tree : null;
            bool hasTree = controller != null && controller.HasTree;
            bool running = hasTree && tree != null && tree.IsRunning;

            // ⚠️ **"正在跑"不等于"不能换树"**：`Root(loop=true)` 的树 `IsRunning` 永远是 true
            //    （实机踩过：相位绑定写了 pool.next，池却永远在 S1 上退让，树一辈子换不过去）。
            //    真正的"步骤边界"是**刚跑完一轮**（`CompletedLoops` 涨了）——那一刻正在下一轮的起点，
            //    换树不会截断任何一步。`loop=false` 的树跑完会把 IsRunning 置 false，同样落在"不是 running"。
            long loops = tree != null ? tree.CompletedLoops : 0;
            bool atStepBoundary = tree != null && loops != m_poolLastLoops;
            m_poolLastLoops = loops;
            bool inTheMiddleOfAStep = running && !atStepBoundary;

            // 活动树的"上一次结果"（在它跑完的那一帧读；下一帧会被重置成默认值）
            bool lastResultKnown = false;
            bool lastSucceeded = false;
            bool lastFailed = false;
            if (hasTree && tree != null && m_poolHadTree)
            {
                lastResultKnown = true;
                lastSucceeded = tree.LastResult == BtResult.Succeeded;
                lastFailed = tree.LastResult == BtResult.Failed || tree.LastResult == BtResult.Aborted;
            }

            PoolDecision decision = m_pool.Decide(controller != null ? controller.Blackboard : null,
                hasTree, inTheMiddleOfAStep, lastSucceeded, lastFailed);

            m_poolHadTree = hasTree;
            if (decision.Action != PoolAction.Run || decision.Entry == null)
                return;

            // S3：先释放（旧树可能按着键），再切
            controller.ReleaseInput();

            TreeSwitchResult switched = Library != null
                ? Library.Switch(decision.Entry.Key, controller, decision.Entry.EntryId)
                : null;

            if (switched == null || !switched.Switched)
            {
                string reason = switched != null ? switched.Describe() : "the tree library is not available";
                EventLog.Write("pool-switch-failed", decision.Entry.Key + " - " + reason);
                Engine.Log.Warning("[PlayerAi][pool] switch to '" + decision.Entry.Key
                    + "' failed: " + reason);
                // 拒包 / 重取（§4.12）：池切不过去也要按四码分流 —— 缺文件会退避重取，
                // 校验不过就停手（否则池会一直卡在这一项上，每帧重试一次）。
                NoteAssetFailure(decision.Entry.Key,
                    switched != null && switched.Issues.Count > 0
                        ? string.Join(" | ", switched.Issues.ToArray()) : reason);
                // 切不过去也要启动它，否则池会停在这一项上（并且下一帧还会再试）
                return;
            }

            EventLog.Write("pool-switch", decision.Entry.Key + " (" + decision.Reason + ")"
                + " switchMs=" + switched.SwitchMilliseconds.ToString("0.00"));
            Engine.Log.Information("[PlayerAi][pool] -> " + decision.Entry.Key
                + " (" + decision.Reason + ") switchMs=" + switched.SwitchMilliseconds.ToString("0.00")
                + " migration=" + (switched.Migration != null ? switched.Migration.Describe() : "-"));
            if (decision.Entry.RunCount == 1)
                ShowMessage("AI: 池 -> " + decision.Entry.Key);
        }

        /// <summary>池调度器（未启用时也存在，便于 `ai.pool.*` 查看与配置）。</summary>
        public PoolScheduler Pool
        {
            get
            {
                if (m_pool == null)
                    m_pool = new PoolScheduler();
                return m_pool;
            }
        }

        /// <summary>池调度是否启用（**默认关闭**：不打开时完全是原有行为）。</summary>
        public bool PoolEnabled
        {
            get { return m_poolEnabled; }
            set { m_poolEnabled = value; }
        }

        /// <summary>
        /// **G25 问的那个问题**：这个包在内存里是什么版本、有没有未落盘的改动。
        ///
        /// ⚠️ 由**运行时**回答而不是直接交给内存库，是因为内存库的 `Dirty` 是"**抓取过之后**"的状态：
        /// 自动缓存按 5 秒节流抓取，于是"刚改完的那 5 秒内"库里还是干净的 ——
        /// 编辑器偏偏最可能在这时候保存并发通知，那就正好**静默盖掉**刚改的东西（实测踩过）。
        /// 所以这里先**按需抓一次**（活树有未抓取的改动时），再问库。
        /// </summary>
        public bool TryGetMemoryVersion(string pathOrName, out string memoryHash, out bool dirty)
        {
            memoryHash = null;
            dirty = false;

            if (string.IsNullOrEmpty(pathOrName))
                return false;

            // 活树确实有"还没进库"的改动 → 先抓（抓取本身是语义哈希去重的，没改就什么都不做）
            if (m_assets != null && !string.IsNullOrEmpty(m_assetActive))
            {
                IAiTreeHost host = ResolveTreeHost();
                BtRuntime tree = host != null ? host.Tree : null;
                bool treeDirty = tree != null && tree.IsDirty && tree.Root != null;
                if (AssetDirtyPending || treeDirty)
                    CaptureLiveTree(m_assetDirtyOrigin);
            }

            MemoryAssetStore store = m_assets;
            return store != null && store.TryGetMemoryVersion(pathOrName, out memoryHash, out dirty);
        }

        // ---------------------------------------------------------------- 拒包 / 重取（P6，§4.12）
        /// <summary>
        /// **拒包 / 重取的异常处理**（四码分流 + 指数退避 + 熔断 + 失败标记）。
        /// 惰性创建；策略取自 <see cref="PlayerAiConfig.NewRetryPolicy"/>。
        /// </summary>
        public AssetRecoveryTask AssetRetry
        {
            get
            {
                if (m_assetRetry == null)
                    m_assetRetry = new AssetRecoveryTask(PlayerAiConfig.NewRetryPolicy());
                return m_assetRetry;
            }
        }

        /// <summary>
        /// 记一次**资源取用失败**：分流 → 失败标记进黑板 → 排入退避队列或熔断 → 记事件日志。
        ///
        /// 调用点只有"取包/取库"那几处（`ai.tree.load` / `ai.tree.switch` / 池切换 / 重取本身），
        /// 不散到节点里 —— 节点有自己的局部错误键（例如 `Task.PoolCall` 的 `failKey`）。
        /// </summary>
        public AssetFailureDecision NoteAssetFailure(string resource, string error, string detail = null,
            AssetFailureKind kind = AssetFailureKind.None,
            AssetResourceKind resourceKind = AssetResourceKind.Tree)
        {
            AssetFailureDecision decision = AssetRetry.Fail(resource, error, detail, kind, resourceKind);
            PublishAssetMarks(decision);

            string category = decision.Exhausted ? "asset-exhausted"
                : (decision.Retry ? "asset-retry" : "asset-failed");
            EventLog.Write(category, decision.Describe());

            if (decision.Exhausted)
            {
                Engine.Log.Warning("[PlayerAi][asset] " + decision.Describe());
                ShowMessage("AI: 资源取用失败已熔断 - " + decision.Resource);
            }
            return decision;
        }

        /// <summary>取用成功 → 清掉这个资源的重试历史与失败标记。</summary>
        public void NoteAssetSuccess(string resource)
        {
            AssetRetry.Succeed(resource);
            ClearAssetMarks();
        }

        /// <summary>人处置过了（编辑器保存了新版本 / 手动重载）→ 解除熔断。</summary>
        public bool ResetAssetRetry(string resource)
        {
            bool was = AssetRetry.Reset(resource);
            // Laya 服务那边也有一份"已经报过失败"的去重集合：人一复位就得让它忘掉，
            // 否则这个库接下来的失败**再也不会**被上报（去重变成永远闭嘴，实机踩过）。
            if (m_laya != null)
                m_laya.ForgetReportedBank(resource);
            ClearAssetMarks();
            EventLog.Write("asset-retry-reset", (resource ?? "<all>")
                + (was ? " (was exhausted)" : string.Empty));
            return was;
        }

        /// <summary>
        /// 把这一拍的处置写成**失败标记**（feed the tree, not the model）。
        ///
        /// ⚠️ 这些键**绝不进摘要、绝不交给 Laya**：它们描述的是基础设施，不是游戏世界。
        /// 消费方式是树里的 `Blackboard` 装饰器（例如 `pkg.fail` IsSet → 走备用子树）。
        /// </summary>
        public void PublishAssetMarks(AssetFailureDecision decision)
        {
            if (decision == null)
                return;
            AiBlackboard board = AssetMarksBoard();
            if (board == null)
                return;

            Dictionary<string, object> marks = decision.BlackboardMarks();
            foreach (KeyValuePair<string, object> pair in marks)
            {
                if (pair.Value is int)
                    board.Set(new AiBlackboardKey<int>(pair.Key), (int)pair.Value);
                else
                    board.Set(new AiBlackboardKey<string>(pair.Key),
                        Convert.ToString(pair.Value) ?? string.Empty);
            }
        }

        /// <summary>清掉失败标记（成功/人处置之后 —— 否则异常子树会一直挂在"出过错"上）。</summary>
        public void ClearAssetMarks()
        {
            AiBlackboard board = AssetMarksBoard();
            if (board == null)
                return;
            for (int i = 0; i < AssetFailureCodes.AllKeys.Length; i++)
            {
                string key = AssetFailureCodes.AllKeys[i];
                board.Remove(new AiBlackboardKey<string>(key));
                board.Remove(new AiBlackboardKey<int>(key));
            }
        }

        private AiBlackboard AssetMarksBoard()
        {
            IAiTreeHost host = ResolveTreeHost();
            return host != null ? host.Blackboard : null;
        }

        /// <summary>
        /// 推进重取队列：退避到期的资源**再取一次**（成功 → 清历史；失败 → 继续退避或熔断）。
        /// 在 tick 边界调用（与树 tick 同一拍，不会打断正在跑的一步）。
        /// </summary>
        public void TickAssetRecovery(float deltaTime)
        {
            if (m_assetRetry == null)
                return;

            List<AssetPendingRetry> due = m_assetRetry.Tick(deltaTime);
            for (int i = 0; i < due.Count; i++)
                RetryAssetLoad(due[i]);
        }

        private void RetryAssetLoad(AssetPendingRetry retry)
        {
            if (retry == null || string.IsNullOrEmpty(retry.Resource))
                return;

            // 按**资源类别**分派：树包是"重新装树"，问题库是"重新解析 .qbank"，
            // 动作包/脚本是"重新读取校验"。混成一条路会让问题库的失败去重装行为树。
            switch (retry.ResourceKind)
            {
                case AssetResourceKind.QuestionBank:
                    RetryQuestionBank(retry);
                    return;
                case AssetResourceKind.Action:
                case AssetResourceKind.Script:
                    RetryActionAsset(retry);
                    return;
                default:
                    RetryTreePackage(retry);
                    return;
            }
        }

        /// <summary>
        /// **问题库**重取：让 Laya 服务重新解析一次（它的失败结果不进缓存，所以再解析是有意义的）。
        ///
        /// 典型场景：编辑器正在原子替换 `<实例根>/PlayerAi/Questions/x.qbank`，
        /// 这一瞬间树里的 `Task.LayaAsk` 撞上"读不到/半截"，几十毫秒后重试就能成功。
        /// </summary>
        private void RetryQuestionBank(AssetPendingRetry retry)
        {
            try
            {
                QuestionBank bank;
                string error;
                if (Laya.TryResolveBank(retry.Resource, null, out bank, out error) && bank != null)
                {
                    NoteAssetSuccess(retry.Resource);
                    EventLog.Write("asset-retry-ok", retry.Resource + " (question bank) after "
                        + retry.Attempt + " attempt(s)");
                    return;
                }

                NoteAssetFailure(retry.Resource, error, null, AssetFailureKind.None,
                    AssetResourceKind.QuestionBank);
            }
            catch (Exception exception)
            {
                NoteAssetFailure(retry.Resource, exception.GetType().Name + ": " + exception.Message,
                    null, AssetFailureKind.None, AssetResourceKind.QuestionBank);
            }
        }

        /// <summary>
        /// **动作包 / 脚本**重取：只做"重新读取并校验"，**不自动重放**。
        ///
        /// 为什么不自动重放：重放的副作用是**角色真的动起来**。一个后台定时器擅自重放
        /// 会打断玩家正在做的事、也会和树里的当前动作打架（§4.14 共控纪律）。
        /// 想重放就由树里的节点（或人）在拿到"文件已经好了"之后自己再发一次。
        /// </summary>
        private void RetryActionAsset(AssetPendingRetry retry)
        {
            string error;
            if (ScriptRuntime != null && retry.ResourceKind == AssetResourceKind.Script)
            {
                if (TryProbeScript(retry.Resource, out error))
                {
                    NoteAssetSuccess(retry.Resource);
                    EventLog.Write("asset-retry-ok", retry.Resource + " (script) after "
                        + retry.Attempt + " attempt(s)");
                    return;
                }
                NoteAssetFailure(retry.Resource, error, null, AssetFailureKind.None,
                    AssetResourceKind.Script);
                return;
            }

            if (TryProbeActionPackage(retry.Resource, out error))
            {
                NoteAssetSuccess(retry.Resource);
                EventLog.Write("asset-retry-ok", retry.Resource + " (action package) after "
                    + retry.Attempt + " attempt(s)");
                return;
            }
            NoteAssetFailure(retry.Resource, error, null, AssetFailureKind.None,
                AssetResourceKind.Action);
        }

        /// <summary>探一次动作包能不能读+校验（**不播放**）。名字与路径都收（重取时手里可能只有名字）。</summary>
        private bool TryProbeActionPackage(string pathOrName, out string error)
        {
            error = null;
            try
            {
                // 先按"名字或路径"解析一遍：失败时记的键可能是**名字**（当时盘上还没有这个文件），
                // 重取时文件已经出现，只有走解析器才找得到它（只做 File.Exists 会永远失败）。
                string folder;
                string resolved = ScatLibrary.ResolveIn(ResolveActionDirectories(), pathOrName,
                    out folder) ?? pathOrName;

                if (!System.IO.File.Exists(resolved))
                {
                    error = PackageCodes.FileMissing + ": " + resolved;
                    return false;
                }
                ScatActionPackage package;
                PackageReport report;
                if (!ScatValidator.TryLoad(resolved, out package, out report) || package == null)
                {
                    error = AssetFailureClassifier.TextOf(report)
                        ?? (PackageCodes.ZipInvalid + ": " + resolved);
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        /// <summary>探一次动作脚本能不能读+编译（**不播放**）。</summary>
        private bool TryProbeScript(string pathOrName, out string error)
        {
            error = null;
            try
            {
                ActionScriptEntry entry = m_actionServices != null
                    ? m_actionServices.Library.ResolveEntry(pathOrName, out error) : null;
                if (m_actionServices == null)
                {
                    error = "action services are not installed";
                    return false;
                }
                if (entry == null)
                    return false;
                if (!entry.Ok)
                {
                    error = entry.Error ?? (PackageCodes.JsonInvalid + ": " + pathOrName);
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private void RetryTreePackage(AssetPendingRetry retry)
        {
            if (retry == null || string.IsNullOrEmpty(retry.Resource))
                return;

            IAiTreeHost host = ResolveTreeHost();
            if (host == null || host.Tree == null)
                return;

            try
            {
                TreeReloadResult result = m_reloader != null
                    ? m_reloader.Load(retry.Resource, host.Tree) : null;
                if (result != null && result.Replaced)
                {
                    if (!host.Tree.IsRunning)
                        host.Tree.Start();
                    host.Enabled = true;
                    NoteAssetSuccess(retry.Resource);
                    EventLog.Write("asset-retry-ok", retry.Resource + " after " + retry.Attempt
                        + " attempt(s): " + result.Describe());
                    Engine.Log.Information("[PlayerAi][asset] retry succeeded: " + retry.Describe());
                    return;
                }

                // 还是不行：拿**校验报告的码**分流（报告的错误码才是权威判据）
                PackageReport report = m_reloader != null ? m_reloader.Validate(retry.Resource) : null;
                string error = AssetFailureClassifier.TextOf(report)
                    ?? (result != null ? result.Describe() : "the package reloader is not available");
                NoteAssetFailure(retry.Resource, error,
                    AssetFailureClassifier.DetailOf(report) ?? (result != null ? result.Reason : null),
                    AssetFailureClassifier.ClassifyReport(report));
            }
            catch (Exception exception)
            {
                NoteAssetFailure(retry.Resource,
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        // ---------------------------------------------------------------- 资源库（P6：存盘库 / 内存库 / 临时缓存）

        /// <summary>
        /// **内存库**（plan §4.11）：树包的运行态以内存为准，**读进内存才算加载**，默认不回写磁盘。
        /// 惰性创建；包目录还没建立时为 null（调用方判空，不抛）。
        /// </summary>
        public MemoryAssetStore Assets
        {
            get
            {
                if (m_assets == null && m_packageRoots != null)
                    m_assets = new MemoryAssetStore(m_packageRoots);
                return m_assets;
            }
        }

        /// <summary>自动缓存（`&lt;实例根&gt;/PlayerAi/.autosave/`，R1~R4）。</summary>
        public AutoSaveCache AssetCache
        {
            get
            {
                if (m_assetCache == null)
                    m_assetCache = new AutoSaveCache(GameActionServices.ResolveInstanceRoot(this));
                return m_assetCache;
            }
        }

        /// <summary>内存库里"当前活动的那份资源"的名字（没登记过就是 null）。</summary>
        public string AssetActiveName
        {
            get { return m_assetActive; }
        }

        /// <summary>内存库现在有没有**还没进缓存**的改动。</summary>
        public bool AssetDirtyPending
        {
            get { return m_assetEditCount != m_assetCaptureCount; }
        }

        /// <summary>实例根（缓存与手动存档都要按它定位）。</summary>
        public string AssetInstanceRoot
        {
            get { return GameActionServices.ResolveInstanceRoot(this); }
        }

        /// <summary>
        /// 登记一份"已经从磁盘读进来"的资源（活动树换包时由 <see cref="SyncActiveAsset"/> 调用）。
        /// 读不进来**不算加载**，所以失败时活动资源名保持不变。
        /// </summary>
        public AssetRecord NoteTreeLoaded(string nameOrPath)
        {
            MemoryAssetStore store = Assets;
            if (store == null)
                return null;

            // **恢复回来的那份不能被这次磁盘载入顶掉**。
            //
            // 断电恢复把"没来得及保存的内存版"装进库（origin=Cache、dirty）；随后世界加载、
            // 活动树换包会走到这里。此处若照常 store.Load(磁盘版)，那份成果就被静默抹掉了 ——
            // 而它恰恰是断电恢复唯一要保住的东西。所以：**保内存版**，活树继续跑磁盘版，
            // 并把冲突如实记进事件日志（怎么处置由人定：save 留下 / drop 丢掉）。
            AssetRecord existing;
            if (store.TryGet(nameOrPath, out existing) && existing != null && existing.Dirty
                && existing.Origin == AssetOrigin.Cache)
            {
                m_assetActive = existing.Name;
                m_assetCaptureCount = m_assetEditCount;
                m_assetDirtyOrigin = AssetOrigin.Cache;
                m_assetTreeWasDirty = false;
                m_assetAutoSaveTimer = 0.0;
                m_assetCachedHash = null;
                m_assetContentHash = ContentHashOfLiveTree();   // 活树实际跑的是磁盘版
                m_assetRecoveredWarned = false;
                EventLog.Write("asset-recovered-kept", existing.Describe()
                    + " | the live tree runs the package on disk; ai.asset.save keeps the memory"
                    + " version, ai.asset.drop discards it");
                return existing;
            }

            string error;
            AssetRecord record = store.Load(nameOrPath, out error);
            if (record == null)
            {
                EventLog.Write("asset-load-failed", (nameOrPath ?? "?") + " - " + (error ?? "?"));
                return null;
            }

            m_assetRecoveredWarned = false;
            m_assetActive = record.Name;
            m_assetCaptureCount = m_assetEditCount;   // 刚载入 = 没有待抓取的改动
            m_assetDirtyOrigin = AssetOrigin.Disk;
            m_assetTreeWasDirty = false;
            m_assetAutoSaveTimer = 0.0;
            m_assetCachedHash = null;   // 新载入的那一版还没进过缓存

            // 记下"刚载入时活树的语义哈希"：这样"改完又改回来"或"重新装载同一个包"
            // 都能被识别成**没有改动**，不会被误判成 dirty（zip 字节带时间戳，不能拿字节判）。
            m_assetContentHash = ContentHashOfLiveTree();

            EventLog.Write("asset-load", record.Describe());
            return record;
        }

        /// <summary>活树此刻的语义哈希（取不到给 null —— 那下一次抓取会被当成"变了"，宁多写一次缓存也不错判）。</summary>
        private string ContentHashOfLiveTree()
        {
            try
            {
                string contentHash;
                string error;
                return TreeWriter.TryGetContentHash(ResolveTreeHost() != null
                    ? ResolveTreeHost().Tree : null,
                    m_reloader != null && m_reloader.ActiveSet != null
                        && m_reloader.ActiveSet.Root != null ? m_reloader.ActiveSet.Root.Manifest : null,
                    m_assetActive, out contentHash, out error) ? contentHash : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 记一次"内存被改了"（谁改的）。只置标记，**不当场序列化**：
        /// 改动可能每帧发生，序列化要按 <see cref="PlayerAiConfig.AssetAutoSaveSeconds"/> 节流。
        ///
        /// 还没有活动资源时**直接忽略**：那说明此刻没有"可以缓存的运行态"（例如世界外、
        /// 或自检里对着一个假宿主改树）。这不是丢信息 —— 活动树一登记，
        /// <see cref="TickAssetAutoSave"/> 的 `IsDirty` 兜底会在下一拍把改动捡回来。
        /// </summary>
        public void NoteTreeEdited(AssetOrigin origin)
        {
            if (string.IsNullOrEmpty(m_assetActive))
                return;

            m_assetDirtyOrigin = origin;
            m_assetEditCount++;
        }

        /// <summary>
        /// 把**活树**序列化成包字节（不落盘）。"库只管字节"的分界线就在这里。
        ///
        /// <paramref name="contentHash"/> 是**语义哈希**，它才是"内容变了没有"的判据。
        /// **不能**用字节哈希：SuAPI 的 zip 写入会给条目打 `DateTime.Now`，
        /// 同一棵树两次序列化出的字节必然不同 —— 拿它当判据会每 5 秒当成一次新改动。
        /// </summary>
        public bool TrySerializeLiveTree(out byte[] bytes, out string contentHash, out string error)
        {
            bytes = null;
            contentHash = null;
            error = null;

            IAiTreeHost host = ResolveTreeHost();
            BtRuntime tree = host != null ? host.Tree : null;
            if (tree == null || tree.Root == null)
            {
                error = "there is no live tree to capture";
                return false;
            }

            ScbtManifest source = m_reloader != null && m_reloader.ActiveSet != null
                && m_reloader.ActiveSet.Root != null ? m_reloader.ActiveSet.Root.Manifest : null;
            return TreeWriter.TrySerializePackage(tree, source, m_assetActive, out bytes,
                out contentHash, out error);
        }

        /// <summary>
        /// 把活树的当前内容装进内存库（`origin` 记来源）。这一步**只改内存**，
        /// 落盘必须走 `ai.asset.save`（人的决定）。
        ///
        /// **语义没变就什么都不做**（返回 true、`changed=false`）：活着的树会一直 `IsDirty`
        /// （直到人 save/drop），自动缓存按 5 秒一拍调这里，不去重的话代次、缓存文件、
        /// 事件日志都会被无意义地刷。
        /// </summary>
        public bool CaptureLiveTree(AssetOrigin origin)
        {
            bool ignored;
            return CaptureLiveTreeCore(origin, out ignored);
        }

        private bool CaptureLiveTreeCore(AssetOrigin origin, out bool changed)
        {
            changed = false;
            MemoryAssetStore store = Assets;
            if (store == null)
                return false;

            if (string.IsNullOrEmpty(m_assetActive))
            {
                IAiTreeHost host = ResolveTreeHost();
                string source = host != null && host.Tree != null ? host.Tree.SourcePackage : null;
                if (string.IsNullOrEmpty(source))
                    return false;
                NoteTreeLoaded(source);
                if (string.IsNullOrEmpty(m_assetActive))
                    return false;
            }

            // **库里有一份"恢复回来的、还没保存的"内存版，而活树跑的是另一份时，抓取会把它盖掉。**
            // 这是整个资源库里唯一的真·数据丢失路径，必须挡住。
            //
            // 判据直接问库（`origin == Cache` 且 dirty），不另设标记：
            //   · 断电恢复装进来 → Cache → 挡住；
            //   · `ai.asset.save` / `ai.asset.drop` 之后 origin 变回 Disk → 自动放行。
            // 少一处"记得清标记"，就少一个能丢数据的漏点。
            AssetRecord pending;
            if (store.TryGet(m_assetActive, out pending) && pending != null && pending.Dirty
                && pending.Origin == AssetOrigin.Cache)
            {
                m_assetCaptureCount = m_assetEditCount;
                if (!m_assetRecoveredWarned)
                {
                    m_assetRecoveredWarned = true;
                    EventLog.Write("asset-capture-blocked", m_assetActive
                        + ": a recovered memory version is waiting to be saved or dropped;"
                        + " the live tree keeps running the package on disk");
                }
                return false;
            }
            m_assetRecoveredWarned = false;

            byte[] bytes;
            string contentHash;
            string error;
            if (!TrySerializeLiveTree(out bytes, out contentHash, out error))
            {
                EventLog.Write("asset-capture-failed", error ?? "?");
                return false;
            }

            if (string.Equals(contentHash, m_assetContentHash, StringComparison.Ordinal))
            {
                // 内容没变：不算改动、不推进代次、也不需要写缓存
                m_assetCaptureCount = m_assetEditCount;
                return true;
            }

            AssetRecord record = store.Adopt(m_assetActive, bytes, origin, out error);
            if (record == null)
            {
                EventLog.Write("asset-capture-failed", error ?? "?");
                return false;
            }

            m_assetContentHash = contentHash;
            m_assetCaptureCount = m_assetEditCount;
            m_assetDirtyOrigin = AssetOrigin.Disk;
            changed = true;
            EventLog.Write("asset-dirty", record.Describe());
            return true;
        }

        /// <summary>立刻把内存版写进自动缓存（`ai.asset.autosave` 与节流定时器共用）。</summary>
        public bool TryAutoSaveActiveAsset(out string error)
        {
            error = null;
            MemoryAssetStore store = Assets;
            if (store == null || string.IsNullOrEmpty(m_assetActive))
            {
                error = "no active asset in the memory store";
                return false;
            }
            if (!store.WriteCache(m_assetActive, AssetCache, out error))
            {
                EventLog.Write("asset-autosave-failed", error ?? "?");
                return false;
            }

            AssetRecord record;
            store.TryGet(m_assetActive, out record);
            m_assetCachedHash = m_assetContentHash;
            EventLog.Write("asset-autosave", m_assetActive);
            return true;
        }

        /// <summary>
        /// 丢弃内存改动 → 回到最近载入的磁盘版。
        ///
        /// ⚠️ **必须连活树一起退回**：只把库里的字节改回去、让树还跑着改动版，
        /// 那是"假装丢弃"（下一次 IsDirty 扫描又会把它捡回来）。所以这里重新装载来源包。
        /// </summary>
        public bool DropActiveAsset(string nameOrPath, out string error)
        {
            error = null;
            MemoryAssetStore store = Assets;
            if (store == null)
            {
                error = "the package folder is not configured";
                return false;
            }

            string name = string.IsNullOrEmpty(nameOrPath) ? m_assetActive : nameOrPath;
            if (string.IsNullOrEmpty(name))
            {
                error = "no active asset to drop";
                return false;
            }

            AssetRecord record;
            store.TryGet(name, out record);
            if (!store.Drop(name, out error))
                return false;

            m_assetCaptureCount = m_assetEditCount;   // 待抓取的改动已经"丢"掉了
            m_assetRecoveredWarned = false;
            m_assetAutoSaveTimer = 0.0;
            m_assetCachedHash = null;
            m_assetContentHash = null;                // 下面重装之后重新算

            // 活树也退回磁盘版（内存库与运行态必须一致）
            IAiTreeHost host = ResolveTreeHost();
            if (record != null && host != null && host.Tree != null && m_reloader != null
                && !string.IsNullOrEmpty(record.SourcePath))
            {
                try
                {
                    TreeReloadResult reloaded = m_reloader.Load(record.SourcePath, host.Tree);
                    if (!reloaded.Replaced)
                    {
                        error = "the memory version was dropped, but the live tree could not be"
                            + " reloaded: " + reloaded.Describe();
                        EventLog.Write("asset-drop-reload-failed", error);
                        return false;
                    }
                    if (!host.Tree.IsRunning)
                        host.Tree.Start();
                    host.Enabled = true;
                }
                catch (Exception exception)
                {
                    error = "the memory version was dropped, but reloading the live tree failed: "
                        + exception.GetType().Name + ": " + exception.Message;
                    EventLog.Write("asset-drop-reload-failed", error);
                    return false;
                }
            }

            EventLog.Write("asset-drop", (record != null ? record.Describe() : name));

            // 重装之后重新锚定"活树的语义哈希"（否则第一次抓取会被当成一次改动）
            m_assetTreeWasDirty = false;
            m_assetContentHash = ContentHashOfLiveTree();
            return true;
        }

        /// <summary>按恢复决议把缓存/磁盘版装回内存库（`ai.asset.restore` / 启动恢复）。</summary>
        public List<string> RestoreAssetsFromCache(string nameOrPath, out string error)
        {
            error = null;
            MemoryAssetStore store = Assets;
            if (store == null)
            {
                error = "the package folder is not configured";
                return null;
            }

            var plans = new List<AssetRecoveryPlan>();
            if (string.IsNullOrEmpty(nameOrPath))
            {
                plans = AssetRecovery.Plan(store.Roots, AssetCache);
            }
            else
            {
                AssetRecord record;
                if (!store.TryGet(nameOrPath, out record) || record == null)
                {
                    error = "not in the memory store: '" + nameOrPath + "' (load it first)";
                    return null;
                }
                AssetRecoveryPlan plan = AssetRecovery.PlanFor(record, AssetCache);
                if (plan != null)
                    plans.Add(plan);
            }

            List<string> lines = AssetRecovery.Apply(store, plans);
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].FromCache && !string.IsNullOrEmpty(m_assetActive)
                    && string.Equals(plans[i].Name, m_assetActive, StringComparison.OrdinalIgnoreCase))
                {
                    // 恢复进来的是"和磁盘不同的内存版" —— 那就是一份待抓取的改动
                    m_assetDirtyOrigin = AssetOrigin.Cache;
                    m_assetEditCount++;
                }
            }
            EventLog.Write("asset-restore", AssetRecovery.Summarize(plans));
            return lines;
        }

        /// <summary>
        /// **启动恢复**（plan §4.11）：`读发行/存档 → 有 .autosave？→ 比时间戳 + 内容哈希 → 用哪一个`。
        /// 决议与理由都会进事件日志 —— R1 要求"绝不静默"。
        /// </summary>
        public List<string> RecoverAssetsOnStart()
        {
            var lines = new List<string>();
            try
            {
                MemoryAssetStore store = Assets;
                if (store == null)
                    return lines;

                List<AssetRecoveryPlan> plans = AssetRecovery.Plan(store.Roots, AssetCache);
                if (plans.Count == 0)
                    return lines;

                lines = AssetRecovery.Apply(store, plans);
                for (int i = 0; i < lines.Count; i++)
                    EventLog.Write("asset-recovery", lines[i]);
                Engine.Log.Information("[PlayerAi][asset] " + AssetRecovery.Summarize(plans));
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][asset] recovery failed: " + exception.GetType().Name
                    + ": " + exception.Message);
            }
            return lines;
        }

        /// <summary>三态视图的数据（`ai.asset.list` / `ai.asset.status` / 编辑器角标）。</summary>
        public List<Dictionary<string, object>> DescribeAssets()
        {
            var list = new List<Dictionary<string, object>>();
            MemoryAssetStore store = Assets;
            if (store == null)
                return list;

            AssetRecoveryPlan[] plans = PlansForRecords(store);
            for (int i = 0; i < store.Records.Count; i++)
            {
                AssetRecord record = store.Records[i];
                AssetRecoveryPlan plan = i < plans.Length ? plans[i] : null;
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = record.Name,
                    ["active"] = string.Equals(record.Name, m_assetActive,
                        StringComparison.OrdinalIgnoreCase),
                    ["dirty"] = record.Dirty,
                    ["generation"] = record.Generation,
                    ["origin"] = record.Origin.ToString(),
                    ["diskHash"] = AssetRecord.Short(record.DiskHash),
                    ["memoryHash"] = AssetRecord.Short(record.MemoryHash),
                    ["diskBytes"] = record.DiskByteCount,
                    ["memoryBytes"] = record.MemoryByteCount,
                    ["cacheChoice"] = plan != null ? plan.Choice.ToString() : "none",
                    ["cacheFromCache"] = plan != null && plan.FromCache,
                    ["cacheExplanation"] = plan != null ? plan.Explanation : null,
                    // 只有**真的来自缓存**的那份才叫 "cache 字节"；
                    // 决议是"用磁盘版"时 Payload 是磁盘内容，记成 cache 字节会误导
                    ["cachePayloadBytes"] = plan != null && plan.FromCache && plan.Payload != null
                        ? plan.Payload.Length : 0,
                    ["sourcePath"] = record.SourcePath
                });
            }
            return list;
        }

        private AssetRecoveryPlan[] PlansForRecords(MemoryAssetStore store)
        {
            IReadOnlyList<AssetRecord> records = store.Records;
            var plans = new AssetRecoveryPlan[records.Count];
            AutoSaveCache cache = AssetCache;
            for (int i = 0; i < records.Count; i++)
            {
                try
                {
                    plans[i] = AssetRecovery.PlanFor(records[i], cache);
                }
                catch (Exception)
                {
                    plans[i] = null;
                }
            }
            return plans;
        }

        /// <summary>
        /// 活动树换包时把它登记进内存库。
        ///
        /// 为什么用"轮询活动树的来源包"而不是在每个装载点插一句：装载路径有
        /// `ai.tree.load` / 池切换 / 热重载 / 启动包四条，分散插桩必然会漏一条；
        /// 这里只认"来源包变了"这一个事实，四条路径自动全覆盖。
        /// </summary>
        private void SyncActiveAsset()
        {
            if (m_packageRoots == null)
                return;

            IAiTreeHost host = ResolveTreeHost();
            BtRuntime tree = host != null ? host.Tree : null;
            string source = tree != null ? tree.SourcePackage : null;
            if (string.IsNullOrEmpty(source))
                return;
            if (string.Equals(source, m_assetSeenSource, PackageRoots.PathComparison))
                return;

            m_assetSeenSource = source;
            NoteTreeLoaded(source);
        }

        /// <summary>
        /// 内存改动的**自动缓存**（节流到 <see cref="PlayerAiConfig.AssetAutoSaveSeconds"/>）。
        ///
        /// 两条判据缺一不可：
        ///   · **"有没有待抓取的改动"用计数**（<see cref="NoteTreeEdited"/> 每改一次 +1），
        ///     **不能用活树的 `IsDirty`**：那是个**电平**信号，一旦被改就一直是 true，
        ///     直到 reload/drop 为止 —— 拿它判断会变成"每 5 秒重写一次缓存"（实测踩过）。
        ///     `IsDirty` 只用来做**第一次发现**的兜底（没走 `ai.edit.*` 的改动）。
        ///   · **"内容变了没有"用语义哈希**：zip 字节带写入时间，每次都不一样。
        /// </summary>
        public void TickAssetAutoSave(float deltaTime)
        {
            if (m_assets == null || string.IsNullOrEmpty(m_assetActive))
                return;

            IAiTreeHost probe = ResolveTreeHost();
            BtRuntime probeTree = probe != null ? probe.Tree : null;
            bool treeDirty = probeTree != null && probeTree.IsDirty && probeTree.Root != null;
            if (treeDirty && !m_assetTreeWasDirty)
            {
                // 兜底：这棵树变脏了但不是我们的命令改的（例如外部直接把活树改了）。
                // 只认"变脏的那一下"，不认"一直脏着"。
                if (m_assetDirtyOrigin == AssetOrigin.Disk)
                    m_assetDirtyOrigin = AssetOrigin.Unknown;
                m_assetEditCount++;
            }
            m_assetTreeWasDirty = treeDirty;

            if (!AssetDirtyPending)
                return;

            m_assetAutoSaveTimer += deltaTime;
            double interval = PlayerAiConfig.AssetAutoSaveSeconds;
            if (interval > 0.0 && m_assetAutoSaveTimer < interval)
                return;

            m_assetAutoSaveTimer = 0.0;

            bool changed;
            if (!CaptureLiveTreeCore(m_assetDirtyOrigin, out changed))
                return;
            if (!changed)
                return;   // 语义没变：没有要缓存的东西（改回原值 = 没改）

            // 内容与上次缓存相同就不必再写一次（同一份内容不需要两条缓存记录）
            if (string.Equals(m_assetContentHash, m_assetCachedHash, StringComparison.Ordinal))
                return;

            string error;
            TryAutoSaveActiveAsset(out error);
        }
        private bool ApplyPendingReloadsNow()        {
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
        ///
        /// **宿主已经在跑树时不顶掉它**：用户"播放这棵树 → 进游戏"的时候，世界一加载
        /// 启动包就会把人家那棵树换掉（日志里能看到 `[startup-tree] demo.greet … replaced`），
        /// 于是"我播放的树进世界后不见了"。启动包只负责"什么都没跑时给个默认的"。
        /// </summary>
        private void TryAutoLoadTree()
        {
            if (!m_autoLoadPending || m_reloader == null)
                return;

            IAiTreeHost host = ResolveTreeHost();
            if (host == null)
                return;

            if (host.HasTree)
            {
                m_autoLoadPending = false;
                string running = host.Tree.SourcePackage;
                Engine.Log.Information("[PlayerAi][pkg] startup tree skipped: host already runs "
                    + (running ?? "?"));
                EventLog.Write("startup-tree-skip", "host already runs " + (running ?? "?"));
                return;
            }

            m_autoLoadPending = false;
            try
            {
                TreeReloadResult result = m_reloader.Load(PlayerAiConfig.StartupTreePackage, host.Tree);
                if (result.Replaced)
                {
                    if (!host.Tree.IsRunning)
                        host.Tree.Start();
                    host.Enabled = true;
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

        /// <summary>
        /// 释放**角色**输入（世界卸载、世界外每帧、手动急停）。
        ///
        /// ⚠️ **不要顺手 `m_controller.ReleaseInput()`**：这个函数在世界外**每帧**都会调用，
        /// 而控制器的释放会把在飞的软光标点击一起取消（UI 点击要跨好几帧才能被引擎识别成 Click）——
        /// 于是"菜单里点按钮"就变成"光标移过去、界面纹丝不动"（实测踩过）。
        /// 要全停用 <see cref="StopEverything"/>。
        /// </summary>
        public void ReleaseAll()
        {
            for (int i = 0; i < m_actors.Count; i++)
                m_actors[i].ReleaseInput();
        }

        /// <summary>彻底停手（Mod 卸载 / 手动急停）：角色输入 + 控制器（含 UI 注入）一起释放。</summary>
        public void StopEverything()
        {
            ReleaseAll();
            if (m_controller != null)
                m_controller.ReleaseInput();
        }

        public void Clear()
        {
            StopEverything();
            m_actors.Clear();
            if (m_controller != null)
                m_controller.Bind(null);
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

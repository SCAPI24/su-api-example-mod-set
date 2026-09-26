using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace PlayerAiMod
{
    /// <summary>
    /// 玩家 AI Mod 入口。
    ///
    /// 两件事：
    ///   1. <c>GameDatabase.GameDatabase</c>：注册 <see cref="PlayerAiComponent"/> 的组件模板
    ///      （与 Mod/WatchMod/Plug/WatchMod.cs 同一套写法）。
    ///   2. <c>Frame.Update</c>：帧末把 AI tick 排到"下一帧帧首"（见 PlayerAiRuntime 的时序说明）。
    ///
    /// 本 Mod 不替换任何 Subsystem/Component/Parameter，不注册 Injector，不碰渲染与帧率，
    /// 所以玩家可以照常操作，AI 只是"另一个玩家控制器使用者"。
    ///
    /// 依赖：CmdBridgeMod —— 复用它的玩家控制器注入门面（CmdBridgeMod.CmdBridgeInput）。
    /// 两个 Mod 各自打包成独立 scmod 放进 Mods/，由加载器各自加载；
    /// ModLoader 会按 ModInfo 里的依赖声明做拓扑排序，保证 CmdBridgeMod 先加载。
    /// </summary>
    public sealed class PlayerAiMod : IMod
    {
        // 组件模板注册用的固定 GUID —— 一旦发布就不要改（改了等于换了组件身份）。
        private static readonly Guid ComponentTemplateGuid =
            new Guid("98EC6C61-A8A4-4365-A02C-2C4DB2580DF7");
        private static readonly Guid ClassParameterGuid =
            new Guid("E23D1309-174F-485E-AEE9-798F8397CC52");
        private static readonly Guid MemberComponentTemplateGuid =
            new Guid("6BA88EDF-CE95-4F46-9081-87D9C588B827");

        // Source: Mod/WatchMod/Plug/WatchMod.cs:38 —— 继承引擎已有的 ComponentTemplate 基模板。
        private static readonly Guid BaseComponentTemplateGuid =
            new Guid("b05700ed-7e4e-4679-98f5-b597f421496b");

        public string Name
        {
            get { return "玩家 AI"; }
        }

        public string Version
        {
            get { return PlayerAiConfig.ModVersion; }
        }

        public IEnumerable<string> Dependencies
        {
            // mod 依赖：复用 CmdBridgeMod 的玩家控制器注入门面（CmdBridgeMod.CmdBridgeInput）。
            get { return new[] { "CmdBridgeMod" }; }
        }

        public bool IsEnabled { get; set; } = true;

        public bool IsMergeLib
        {
            get { return true; }
        }

        private const string HotkeyPauseName = "playerai.pause";
        private const string HotkeyRecordName = "playerai.record";
        private const string HotkeyRecordStopName = "playerai.record.stop";

        private bool m_hotkeysBound;

        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_databaseToken;
        private EventSubscriptionToken m_frameToken;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            if (eventBus == null)
                throw new ArgumentNullException(nameof(eventBus));

            PlayerAiConfig.Validate();
            PlayerAiRuntime.Instance = new PlayerAiRuntime();
            m_eventBus = eventBus;

            // 建立包目录并装上出厂示例包（缺什么补什么，绝不覆盖用户改过的包）。
            PlayerAiRuntime.Instance.EnsurePackages();
            PlayerAiRuntime.InstallPoolHost();

            // 资源库（P6 第二步）：启动恢复 —— 先看 `.autosave/` 里的影子副本能不能用
            // （比"磁盘版 vs 缓存版"的时间戳与内容哈希），**用了哪一份、为什么**都进事件日志。
            if (PlayerAiConfig.AssetRecoverOnStart)
            {
                try
                {
                    List<string> recovered = PlayerAiRuntime.Instance.RecoverAssetsOnStart();
                    for (int i = 0; i < recovered.Count; i++)
                        Log.Information("[PlayerAi][asset] " + recovered[i]);
                }
                catch (Exception exception)
                {
                    Log.Warning("[PlayerAi][asset] startup recovery failed: "
                        + exception.GetType().Name + ": " + exception.Message);
                }
            }

            // 装配动作脚本服务（P1）：脚本目录 = `<实例根>/PlayerAi/Scripts/`（可写）
            // + `<实例根>/PlayerAi/BehaviorTrees/`（兼容：脚本与树包放一起也行）。
            // 必须在 EnsurePackages 之后 —— 实例根由包目录推出来。
            try
            {
                string instanceRoot = ResolveInstanceRoot(PlayerAiRuntime.Instance);
                GameActionServices.Install(PlayerAiRuntime.Instance, instanceRoot);

                // 纯逻辑层（`BtRunActionScriptTask`）的"按需取"（A34）：`ActionPlayServices.Current`
                // 是全局静态缝，**自检/编辑器/别的 Mod 都可能把它清成 null** —— 那之后树里的动作节点
                // 全部以 `action services are not initialized` 失败，而现象只是"树里动作没反应"。
                // 挂上这个委托后，节点永远能把服务要回来（运行时的 `ActionServices` 会懒创建）。
                ActionPlayServices.Provider = delegate
                {
                    PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
                    return runtime != null ? runtime.ActionServices : null;
                };

                Log.Information("[PlayerAi][act] action services installed, scripts in: "
                    + string.Join("; ", ActionPlayServices.Current != null
                        ? new List<string>(ActionPlayServices.Current.ScriptDirectories).ToArray()
                        : new string[0]));
            }
            catch (Exception exception)
            {
                Log.Warning("[PlayerAi][act] installing action services failed: "
                    + exception.GetType().Name + ": " + exception.Message);
            }

            // Laya 服务（P3）：**开机就建一次**，而不是等第一次有人问。
            //
            // 为什么必须提前建：`LayaRuntimeHost.Current` 只在运行时 `Laya` 属性第一次被访问时填上，
            // 而"开机后第一棵用到 Task.LayaAsk 的树"完全可能比任何命令都早 —— 那时节点读到 null，
            // 报 `Laya service is not initialized`（实机踩过：出厂异常子树第一次跑就撞上）。
            // 现在节点侧也有兜底（`ResolveService`），这里是让"正常路径"一开始就是对的。
            try
            {
                LayaRuntimeService laya = PlayerAiRuntime.Instance.Laya;
                if (laya != null)
                {
                    Log.Information("[PlayerAi][laya] service ready: " + laya.Config.ToString()
                        + " banks=" + laya.BankDirectories.Count
                        + " key=" + laya.Config.ApiKeySource);
                }
            }
            catch (Exception exception)
            {
                Log.Warning("[PlayerAi][laya] creating the Laya service failed: "
                    + exception.GetType().Name + ": " + exception.Message);
            }

            // 把 ai.* 命令挂到 CmdBridgeMod 的控制通道上（一套通道、一个 token、一个 CLI）。
            AiCommandBridge.RegisterAll(PlayerAiRuntime.Instance);

            m_databaseToken = eventBus.SubscribeEvent(
                "GameDatabase.GameDatabase", OnGameDatabase, EventPriority.HIGHEST);
            m_frameToken = eventBus.SubscribeEvent(
                "Frame.Update", OnFrameUpdate, EventPriority.LOWEST);

            TryBindHotkeys();

            Log.Information("[PlayerAi] loaded v" + Version + ", injector="
                + (CmdBridgeActuator.IsAvailable
                    ? "CmdBridgeMod facade ready"
                    : "UNAVAILABLE (CmdBridgeMod missing or input injection disabled)"));
        }

        /// <summary>
        /// 实例根由 <see cref="GameActionServices.ResolveInstanceRoot"/> 统一负责：
        /// 脚本目录必须与包目录**同源**，否则会出现"包装得上、脚本找不到"这种分裂。
        /// </summary>
        private static string ResolveInstanceRoot(PlayerAiRuntime runtime)
        {
            return GameActionServices.ResolveInstanceRoot(runtime);
        }

        public void OnUnload()
        {
            if (m_eventBus != null)
            {
                if (m_databaseToken != null)
                    m_eventBus.UnsubscribeEvent(m_databaseToken);
                if (m_frameToken != null)
                    m_eventBus.UnsubscribeEvent(m_frameToken);
            }

            m_databaseToken = null;
            m_frameToken = null;
            m_eventBus = null;

            // 先解绑本 Mod 的热键，避免卸载后注册表里还留着指向已卸载 Mod 的回调。
            if (m_hotkeysBound)
            {
                try
                {
                    CmdBridgeMod.CmdBridgeMod bridge = CmdBridgeMod.CmdBridgeMod.Instance;
                    CmdBridgeMod.CmdBridgeInput input = bridge != null ? bridge.Input : null;
                    if (input != null)
                    {
                        input.UnregisterHotkey(HotkeyPauseName);
                        input.UnregisterHotkey(HotkeyRecordName);
                        input.UnregisterHotkey(HotkeyRecordStopName);
                    }
                }
                catch (Exception)
                {
                    // 卸载路径不因为解绑失败而中断。
                }
                m_hotkeysBound = false;
            }

            // 先摘掉控制面命令，避免卸载后命令还在（回调指向已卸载的类型）。
            try
            {
                AiCommandBridge.UnregisterAll();
            }
            catch (Exception)
            {
                // 卸载路径不因为摘命令失败而中断。
            }

            // 再摘掉动作脚本服务（它持有运行时引用，且里面有全局静态装配点）。
            try
            {
                GameActionServices.Uninstall();
            }
            catch (Exception)
            {
                // 同上：卸载路径不抛。
            }

            // 先释放注入状态，避免卸载后游戏里残留"按住 W"。
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime != null)
            {
                runtime.Clear();
                PlayerAiRuntime.Instance = null;
            }

            // 兜底：可能刚按下键就被卸载，直接释放（不等帧首队列）。
            CmdBridgeActuator.ReleaseAllImmediate();

            Log.Information("[PlayerAi] stopped.");
        }

        /// <summary>
        /// 把 `Home` 绑到"行为树执行/暂停"。
        ///
        /// 热键注册表在 CmdBridgeMod（输入层，帧首判定、线程安全），本 Mod 只是使用者 ——
        /// 与"PlayerAiMod 建立在 CmdBridgeMod 之上"一致；`PgUp`/`PgDn` 的动作包录制后续走同一机制。
        /// </summary>
        private void TryBindHotkeys()
        {
            try
            {
                CmdBridgeMod.CmdBridgeMod bridge = CmdBridgeMod.CmdBridgeMod.Instance;
                CmdBridgeMod.CmdBridgeInput input = bridge != null ? bridge.Input : null;
                if (input == null || !input.IsAvailable)
                {
                    Log.Warning("[PlayerAi] hotkeys unavailable: CmdBridgeMod input facade is not ready.");
                    return;
                }

                if (!input.RegisterHotkey("Home", OnTogglePauseHotkey, HotkeyPauseName))
                {
                    Log.Warning("[PlayerAi] failed to bind Home hotkey.");
                    return;
                }

                // 录制热键（计划 §6.1）：PgUp 开始 / 暂停继续；PgDn 结束并命名。
                // 都走 CmdBridgeMod 的热键注册表（帧首判定、线程安全），Android 上没有这两个键时
                // 用等价命令 `ai.record.start/pause/stop`。
                bool recordBound = input.RegisterHotkey("PageUp", OnRecordHotkey, HotkeyRecordName);
                bool recordStopBound = input.RegisterHotkey("PageDown", OnRecordStopHotkey,
                    HotkeyRecordStopName);
                if (!recordBound || !recordStopBound)
                    Log.Warning("[PlayerAi] recording hotkeys partially bound: PageUp=" + recordBound
                        + " PageDown=" + recordStopBound);

                m_hotkeysBound = true;
                Log.Information("[PlayerAi] hotkeys bound: Home -> tree run/pause, "
                    + "PageUp -> record start/pause, PageDown -> record stop+naming");
            }
            catch (Exception exception)
            {
                Log.Warning("[PlayerAi] binding hotkeys failed: " + exception.Message);
            }
        }

        private void OnTogglePauseHotkey()
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                return;

            bool paused = runtime.TogglePause("hotkey:Home");
            runtime.ShowMessage(paused ? "AI: paused  (Home to resume)" : "AI: running");
        }

        /// <summary>PgUp：开始录制 / 暂停继续（未录制时开始，录制中切换暂停）。</summary>
        private void OnRecordHotkey()
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                return;

            try
            {
                runtime.OnRecordHotkey();
            }
            catch (Exception exception)
            {
                Log.Warning("[PlayerAi][rec] PageUp handler failed: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <summary>PgDn：结束录制并弹出命名框。</summary>
        private void OnRecordStopHotkey()
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                return;

            try
            {
                runtime.OnRecordStopHotkey();
            }
            catch (Exception exception)
            {
                Log.Warning("[PlayerAi][rec] PageDown handler failed: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private object[] OnGameDatabase(object[] args)
        {
            if (args == null || args.Length == 0 || !(args[0] is Database))
            {
                Log.Warning("[PlayerAi] GameDatabase event without a Database argument.");
                return new object[] { false, null };
            }

            var database = (Database)args[0];
            RegisterComponentTemplate(database);
            Log.Information("[PlayerAi] registered PlayerAi ComponentTemplate on Player");
            return new object[] { true, database };
        }

        private object[] OnFrameUpdate(object[] args)
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime != null)
            {
                // 帧末采样（P1 录制）：这一帧的输入必须**当场**收进轨道。
                // 采样点选帧末是有原因的：此刻 PlayerInput 已是这一帧的完整意图，键盘/鼠标状态也定型了。
                //
                // 为什么值得写这么长一段注释：少了这一句，录出来的包**结构依然合法**
                // （manifest + keyframes + events 都在），只是没有 `tracks/input.bin`，
                // 校验器只给一条 WARN、`replayable=false` —— 于是"录了却永远放不出来"
                // 会一直装成正常，直到有人真的去回放才发现（实测就漏了一整轮）。
                runtime.SampleRecordingFrame();

                // Source: Survivalcraft/Game/Program.cs:147 —— 本回调在帧末触发；
                // 排到下一帧帧首执行，才早于键盘/鼠标设备读取。
                runtime.ScheduleFrameStartTick(Time.FrameDuration);
            }
            return null;
        }

        private static void RegisterComponentTemplate(Database database)
        {
            // Step 1: ComponentTemplate
            DatabaseObject componentTemplate = new DatabaseObject(
                database.FindDatabaseObjectType("ComponentTemplate", true),
                ComponentTemplateGuid,
                "PlayerAi",
                null);
            componentTemplate.Description = "Player AI brain (behaviour tree on a state-machine mode layer).";
            componentTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
                BaseComponentTemplateGuid,
                database.FindDatabaseObjectType("ComponentTemplate", true),
                true);
            componentTemplate.NestingParent = database.FindDatabaseObject(
                "Gameplay",
                database.FindDatabaseObjectType("Folder", true),
                true);

            // Step 2: Parameter "Class" 指向本 Mod 的组件实现
            DatabaseObject parameterClass = new DatabaseObject(
                database.FindDatabaseObjectType("Parameter", true),
                ClassParameterGuid,
                "Class",
                "PlayerAiMod.PlayerAiComponent");
            parameterClass.NestingParent = componentTemplate;

            // Step 3: MemberComponentTemplate —— 把组件挂到 Player 实体模板下
            DatabaseObject memberComponent = new DatabaseObject(
                database.FindDatabaseObjectType("MemberComponentTemplate", true),
                MemberComponentTemplateGuid,
                "PlayerAi",
                null);
            memberComponent.Description = "Player AI brain (behaviour tree on a state-machine mode layer).";
            memberComponent.ExplicitInheritanceParent = database.FindDatabaseObject(
                ComponentTemplateGuid,
                database.FindDatabaseObjectType("ComponentTemplate", true),
                true);
            memberComponent.NestingParent = database.FindDatabaseObject(
                "Player",
                database.FindDatabaseObjectType("EntityTemplate", true),
                true);
        }
    }
}

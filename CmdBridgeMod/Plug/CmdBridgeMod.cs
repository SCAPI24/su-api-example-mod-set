using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.IO;

namespace CmdBridgeMod
{
    /// <summary>
    /// CmdBridge —— 玩家机器人桥。
    ///
    /// 定位（Mod/doc/cmd-bridge-plan.md）：
    ///   · 允许无限查看游戏内数据；一切交互都必须通过玩家控制器（视角/键盘/鼠标/UI 点击）。
    ///   · 优势（无限读取、超人输入速度）允许；特权（直接改游戏状态）禁止。
    ///   · 不替换任何 Subsystem/Component/Parameter，不注册 Injector，不碰渲染与帧率，
    ///     因此玩家可以照常操作界面，AI/脚本同时通过本桥操作。
    /// </summary>
    public sealed class CmdBridgeMod : IMod
    {
        public string Name => "命令行桥";

        public string Version => "1.0.0";

        public IEnumerable<string> Dependencies => Array.Empty<string>();

        public bool IsEnabled { get; set; } = true;

        public bool IsMergeLib => true;

        /// <summary>当前已加载的实例（供依赖本 Mod 的 Mod 使用；未加载时为 null）。</summary>
        public static CmdBridgeMod Instance { get; private set; }

        /// <summary>
        /// 玩家控制器注入门面 —— 依赖本 Mod 的 Mod（例如 PlayerAiMod）通过它复用同一套
        /// 注入实现（白名单字段、帧首时序、UI 点击两帧、释放残留按键）。
        /// </summary>
        public CmdBridgeInput Input { get; private set; }

        private string m_instanceRoot;
        private CmdBridgeConfig m_config;
        private GameThreadInvoker m_invoker;
        private InputInjector m_injector;
        private EventRecorder m_eventRecorder;
        private CommandRouter m_router;
        private ControlServer m_server;
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_frameToken;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            if (eventBus == null)
                throw new ArgumentNullException(nameof(eventBus));

            m_instanceRoot = Path.GetFullPath(AppContext.BaseDirectory);
            m_config = CmdBridgeConfig.LoadOrCreate(m_instanceRoot);

            // 注入器与帧首调度器先建好：它们是"玩家控制器"的公共实现，
            // 依赖本 Mod 的 Mod 通过 CmdBridgeInput 门面复用，
            // 不应被本 Mod 自己的 TCP 服务开关连坐。
            m_invoker = new GameThreadInvoker(m_config.RequestTimeoutSeconds);
            m_injector = new InputInjector(m_config, m_invoker);
            Input = new CmdBridgeInput(m_injector);
            Instance = this;

            // 真实焦点跟踪：Window.Activated/Deactivated 只在**真实**焦点变化时触发，
            // 因此不会被我们"强制引擎活跃"（直接写 m_state）干扰 —— 策略据此在
            // "前台跟随"与"失焦脱离"之间自动切换。
            Window.Activated += OnWindowActivated;
            Window.Deactivated += OnWindowDeactivated;

            if (!m_config.Enabled)
            {
                Log.Information("[CmdBridge] TCP server disabled by CmdBridge.json; "
                    + "input facade is still available for dependent mods.");
                return;
            }

            m_eventRecorder = new EventRecorder(m_config.EventRingCapacity);
            m_router = new CommandRouter(m_config, m_invoker, m_injector, m_eventRecorder);
            m_server = new ControlServer(m_config, m_router);

            try
            {
                // Source: Survivalcraft/Game/Program.cs:147 —— Frame.Update 在控件树 Update 之后触发，
                // 因此此刻读到的 ClickableWidget.IsClicked 就是本帧被点击的元素。
                m_eventBus = eventBus;
                m_frameToken = eventBus.SubscribeEvent(
                    "Frame.Update", OnFrameUpdate, EventPriority.LOWEST);
                m_server.Start();
                CmdBridgeRuntime.Write(m_instanceRoot, m_config, null);
                Log.Information(
                    "[CmdBridge] listening " + m_config.BindAddress + ":" + m_config.Port +
                    ", inputInjection=" + m_config.EnableInputInjection);
            }
            catch
            {
                m_server?.Stop();
                m_server = null;
                throw;
            }
        }

        private object[] OnFrameUpdate(object[] args)
        {
            m_eventRecorder?.Tick();

            // 帧末：控件树刚按"界面是否需要光标"写过 Mouse.IsMouseVisible，
            // 脱离模式必须在此刻覆盖它，否则下一帧 Mouse.BeforeFrame() 会隐藏系统光标。
            m_injector?.Focus?.ApplyFrameEnd();

            if (m_injector != null)
            {
                // 每帧排一次帧首工作：先应用焦点策略（早于 Keyboard/Mouse.BeforeFrame 与整个帧体，
                // 所以"切断真实鼠标增量""合并真实输入"都在正确时机生效），再判定热键。
                m_injector.Pump.Enqueue(delegate
                {
                    m_injector.Focus.ApplyFrameStart();
                    m_injector.Hotkeys.Tick();
                });
                m_injector.SignalFrameEnd();
            }
            return null;
        }

        private void OnWindowActivated()
        {
            m_injector?.Focus?.NotifyFocus(true);
        }

        private void OnWindowDeactivated()
        {
            m_injector?.Focus?.NotifyFocus(false);
        }

        public void OnUnload()
        {
            if (m_eventBus != null && m_frameToken != null)
                m_eventBus.UnsubscribeEvent(m_frameToken);
            m_frameToken = null;
            m_eventBus = null;
            m_eventRecorder = null;
            Window.Activated -= OnWindowActivated;
            Window.Deactivated -= OnWindowDeactivated;

            // 清掉本 Mod 注册的热键，避免卸载后还有回调指向已卸载的 Mod。
            m_injector?.Hotkeys?.Clear();

            // 把引擎焦点恢复成真实值，避免卸载后引擎"以为"窗口还活跃。
            m_injector?.Focus?.RestoreNaturalFocus();

            // 先停泵再释放注入状态，避免卸载后游戏里残留"按住 W"或会话按键。
            m_injector?.Pump.Stop();
            m_injector?.ReleaseAllDirect();
            m_injector = null;

            if (m_server != null)
            {
                m_server.Stop();
                m_server = null;
            }

            if (m_instanceRoot != null)
                CmdBridgeRuntime.Delete(m_instanceRoot);

            // 门面先失效：依赖本 Mod 的 Mod 不应再拿到已停用的注入器。
            Input = null;
            if (ReferenceEquals(Instance, this))
                Instance = null;

            m_router = null;
            m_invoker = null;
            m_config = null;
            Log.Information("[CmdBridge] stopped.");
        }
    }
}

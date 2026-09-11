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
            if (!m_config.Enabled)
            {
                Log.Information("[CmdBridge] Disabled by CmdBridge.json.");
                return;
            }

            m_invoker = new GameThreadInvoker(m_config.RequestTimeoutSeconds);
            m_injector = new InputInjector(m_config, m_invoker);
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
            return null;
        }

        public void OnUnload()
        {
            if (m_eventBus != null && m_frameToken != null)
                m_eventBus.UnsubscribeEvent(m_frameToken);
            m_frameToken = null;
            m_eventBus = null;
            m_eventRecorder = null;
            // 先释放注入状态，避免卸载后游戏里残留"按住 W"。
            m_injector?.ReleaseAllDirect();
            m_injector = null;

            if (m_server != null)
            {
                m_server.Stop();
                m_server = null;
            }

            if (m_instanceRoot != null)
                CmdBridgeRuntime.Delete(m_instanceRoot);

            m_router = null;
            m_invoker = null;
            m_config = null;
            Log.Information("[CmdBridge] stopped.");
        }
    }
}

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
    ///   · 不替换任何 Subsystem/Parameter、不注册 Injector、不改帧率，
    ///     因此玩家可以照常操作界面，AI/脚本同时通过本桥操作。
    ///   · **唯一的渲染**是 `Server/BridgeOverlayDrawable`（`box.*` 命令画的固定坐标边框，
    ///     DrawOrder=2001）。它只在**确实有边框要画**时才挂到本机玩家实体上（无边框时连组件
    ///     都不创建 = 零开销），不替换任何子系统、不碰数据库节点，与其他 Mod 的绘制互不影响
    ///     （各自的 draw order 不同）。纯内存、不落盘。
    /// </summary>
    public sealed class CmdBridgeMod : IMod
    {
        public string Name => "命令行桥";

        public string Version => CommandRouter.ModVersion;

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
        private JumpAssist m_jumpAssist;
        private EventRecorder m_eventRecorder;
        private CommandRouter m_router;
        private ControlServer m_server;
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_frameToken;
        // Source: FrameStartPump.cs:FrameStartPump.Stop; IModEventBus.cs:UnsubscribeEvent
        private bool m_loaded;
        private int m_lifetime;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            if (eventBus == null)
                throw new ArgumentNullException(nameof(eventBus));
            if (m_loaded)
                throw new InvalidOperationException("CmdBridgeMod is already loaded.");

            m_loaded = true;
            m_lifetime++;
            try
            {
                m_instanceRoot = Path.GetFullPath(AppContext.BaseDirectory);
                m_config = CmdBridgeConfig.LoadOrCreate(m_instanceRoot);

                // 注入器与帧首调度器先建好：它们是"玩家控制器"的公共实现，
                // 依赖本 Mod 的 Mod 通过 CmdBridgeInput 门面复用，
                // 不应被本 Mod 自己的 TCP 服务开关连坐。
                m_invoker = new GameThreadInvoker(m_config.RequestTimeoutSeconds);
                m_injector = new InputInjector(m_config, m_invoker);
                // 空格/跳跃审计与可选缓冲：纯观察 + 只写 ComponentLocomotion.JumpOrder，不碰注入路径。
                // 恒久打开（写死在代码里，不再落 CmdBridge.json）：运行时用 `jump.buffer state=off` 只关本次。
                m_jumpAssist = new JumpAssist();
                // 输入阶段注入的落点：order -11 的 CursorSoftGuard 会转调 JumpAssist.PumpInputStage()，
                // 位置紧贴 ComponentInput(-10) 之前（与原生输入同一阶段）。
                m_injector.JumpAssist = m_jumpAssist;
                // 事件环序号用**惰性**取法：`m_eventRecorder` 在本行之后才建（顺序不能动），
                // 而 `CmdBridgeInput` 现在就要拿到门面。闭包每次调用现读，事件环没建好时给 null
                // （守卫据此判"判不了"，不会误判成"有新事件"）。
                Input = new CmdBridgeInput(m_injector,
                    () => m_eventRecorder != null ? m_eventRecorder.LastSeq : (long?)null);
                Instance = this;

                // 真实焦点跟踪：Window.Activated/Deactivated 只在**真实**焦点变化时触发，
                // 因此不会被我们"强制引擎活跃"（直接写 m_state）干扰 —— 策略据此在
                // "前台跟随"与"失焦脱离"之间自动切换。
                Window.Activated += OnWindowActivated;
                Window.Deactivated += OnWindowDeactivated;

                // Source: FrameStartPump.cs:FrameStartPump.SignalFrameEnd
                // The input facade and focus policy need the frame service even without the TCP listener.
                m_eventBus = eventBus;
                m_frameToken = eventBus.SubscribeEvent(
                    "Frame.Update", OnFrameUpdate, EventPriority.LOWEST);

                if (!m_config.Enabled)
                {
                    Log.Information("[CmdBridge] TCP server disabled by CmdBridge.json; "
                        + "input facade is still available for dependent mods.");
                    return;
                }

                m_eventRecorder = new EventRecorder(m_config.EventRingCapacity);
                m_router = new CommandRouter(m_config, m_invoker, m_injector, m_eventRecorder, m_jumpAssist);
                m_server = new ControlServer(m_config, m_router);

                // Source: Survivalcraft/Game/Program.cs:Program.Run — observation stays after widget updates.
                m_server.Start();
                CmdBridgeRuntime.Write(m_instanceRoot, m_config, null);
                Log.Information(
                    "[CmdBridge] listening " + m_config.BindAddress + ":" + m_config.Port +
                    ", inputInjection=" + m_config.EnableInputInjection);
            }
            catch
            {
                try { OnUnload(); }
                catch (Exception cleanupError)
                {
                    Log.Error("[CmdBridge] Failed-load cleanup failed: " + cleanupError);
                }
                throw;
            }
        }

        private object[] OnFrameUpdate(object[] args)
        {
            if (!m_loaded) return null;
            m_eventRecorder?.Tick();

            // 边框绘制组件：幂等挂载（只在有边框时动作，无边框直接返回）。
            // 放在帧末这里而不是某个子系统的 Update，避免与任何 Mod 抢更新顺序。
            BridgeOverlayState.EnsureAttached();

            // 帧末：控件树刚按"界面是否需要光标"写过 Mouse.IsMouseVisible，
            // 脱离模式必须在此刻覆盖它，否则下一帧 Mouse.BeforeFrame() 会隐藏系统光标。
            m_injector?.Focus?.ApplyFrameEnd();

            if (m_injector != null)
            {
                InputInjector injector = m_injector;
                int lifetime = m_lifetime;
                // 每帧排一次帧首工作：先应用焦点策略（早于 Keyboard/Mouse.BeforeFrame 与整个帧体，
                // 所以"切断真实鼠标增量""合并真实输入"都在正确时机生效），再判定热键。
                injector.Pump.Enqueue(delegate
                {
                    if (!m_loaded || lifetime != m_lifetime || !ReferenceEquals(injector, m_injector)) return;
                    injector.Focus.ApplyFrameStart();
                    injector.Hotkeys.Tick();
                    // 跳跃审计/缓冲放在热键之后：它会读本帧的空格边沿，并可能写 JumpOrder，
                    // 所以既要早于 ComponentInput.Update（本批次就是帧首），也不要干扰热键判定。
                    m_jumpAssist?.Tick();
                });
                injector.SignalFrameEnd();
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
            if (!m_loaded) return;
            m_loaded = false;
            m_lifetime++;
            if (m_eventBus != null && m_frameToken != null)
                m_eventBus.UnsubscribeEvent(m_frameToken);
            m_frameToken = null;
            m_eventBus = null;
            m_eventRecorder = null;
            Window.Activated -= OnWindowActivated;
            Window.Deactivated -= OnWindowDeactivated;

            // Source: ControlServer.cs:ControlServer.Stop — stop accepting producers before disposing their pump.
            m_server?.Stop();
            m_server = null;
            Input = null;
            if (ReferenceEquals(Instance, this)) Instance = null;

            // 清掉本 Mod 注册的热键，避免卸载后还有回调指向已卸载的 Mod。
            m_injector?.Hotkeys?.Clear();
            // 跳跃审计订阅了引擎的 Keyboard.KeyDown，卸载时必须退订。
            m_jumpAssist?.Shutdown();

            // Source: SubsystemUpdate.cs:SubsystemUpdate.RemoveUpdateable; FrameStartPump.cs:FrameStartPump.Stop
            // Stop queued work and invalidate the guard before restoring natural input state.
            try { m_injector?.Pump.Dispose(); }
            finally
            {
                try { m_injector?.Focus?.DetachCursorGuard(); }
                finally
                {
                    // 把引擎焦点恢复成真实值，再释放按键；泵停止失败也不能留下输入状态。
                    m_injector?.Focus?.RestoreNaturalFocus();
                    m_injector?.ReleaseAllDirect();
                    m_injector = null;
                }
            }

            if (m_instanceRoot != null)
                CmdBridgeRuntime.Delete(m_instanceRoot);

            // 边框是纯内存状态：卸载即清空，不留任何残留。
            BridgeOverlayState.Reset();

            m_router = null;
            m_invoker = null;
            m_jumpAssist = null;
            m_config = null;
            Log.Information("[CmdBridge] stopped.");
        }
    }
}

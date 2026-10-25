using System;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 世界控制的引擎侧端口（实现 <see cref="IWorldControlPort"/> 与
    /// <see cref="IWorldControlGuiSink"/>；组合根创建，模块每帧 Pump）。
    ///
    /// 三个职责：
    ///   · **客户端输入捕获**：本端是联机客户端时，把 <see cref="SuComponentGui"/>
    ///     （Injector 替换的 ComponentGui 外壳）的 WorldControlSink 指向自己；
    ///     拦截点在 ComponentGui.Update 内、base.Update（HandleInput 读四按钮）之前
    ///     —— 与 widget 置位**同一帧**，采集后写回 false，原生本地分支整段不执行。
    ///     （旧实现挂在 ComponentInput(-10) 抢点击晚一帧，实测客户端本地会先执行一次：
    ///     本地全尺寸爆炸 + 复制事件 = 双份特效，2026-10-26 双重爆炸根因。）
    ///     Source: Survivalcraft/Game/ComponentGui.cs:679-737（四按钮的引擎行为）
    ///     Source: Survivalcraft/Game/ClickableWidget.cs:29-41（IsClicked 每帧现算）
    ///   · **主机执行**：收到请求后在权威世界调用引擎同款 API
    ///     （ManualPrecipitationStart/End、ManualFogStart/End、ManualLightingStrike、
    ///     TimeOfDayOffset 区间判定），状态变化由既有 WorldInfo 快照广播生效。
    ///   · **客户端提示**：回执到达后用本机 ComponentGui.DisplaySmallMessage 显示
    ///     与引擎一致的文案（"Precipitation On"/"Dawn"…）。
    ///
    /// 无静态可变状态；反射 FieldInfo 全部静态缓存（每帧读 4 个按钮字段）。
    /// </summary>
    public sealed class WorldControlEngine : IWorldControlPort, IWorldControlGuiSink
    {
        // ---- 反射缓存（引擎私有字段名以源码为准，不臆造） ----
        private static readonly FieldInfo s_guiTimeButton = typeof(ComponentGui).GetField(
            "m_timeOfDayButtonWidget", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_guiPrecipButton = typeof(ComponentGui).GetField(
            "m_precipitationButtonWidget", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_guiFogButton = typeof(ComponentGui).GetField(
            "m_fogButtonWidget", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_guiLightningButton = typeof(ComponentGui).GetField(
            "m_lightningButtonWidget", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_inputPlayerInput = typeof(ComponentInput).GetField(
            "m_playerInput", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_bitmapClickable = typeof(BitmapButtonWidget).GetField(
            "m_clickableWidget", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_clickableIsClicked = typeof(ClickableWidget).GetField(
            "<IsClicked>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly string[] m_buttonNames =
            { "m_timeOfDayButtonWidget", "m_precipitationButtonWidget", "m_fogButtonWidget", "m_lightningButtonWidget" };

        private Project m_project;
        private bool m_clientReplica;
        private SuComponentGui m_gui;
        private IWorldControlRequestSink m_requestSink;

        // 计数（状态行 / 排障用；热路径不写日志）
        private long m_captured;
        private long m_consumeClickFailures;
        private long m_aimMissing;
        private long m_showFailures;
        private long m_inputShellMissing;
        private bool m_inputShellMissingLogged;
        private bool m_reflectionFaultLogged;

        public WorldControlEngine(NetworkAvatarSpawner spawner, IModuleDiagnostics diagnostics,
            string ownerId)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "world-control" : ownerId;
        }

        private void Info(string message) => m_diagnostics.Info(m_ownerId, message);
        private void Warn(string message) => m_diagnostics.Warn(m_ownerId, message);

        public string DescribeCounters() =>
            "captured=" + m_captured + " consumeClickFailures=" + m_consumeClickFailures +
            " aimMissing=" + m_aimMissing + " showFailures=" + m_showFailures +
            " inputShellMissing=" + m_inputShellMissing;

        // ================================================================= IWorldControlPort：挂接

        public void Pump()
        {
            if (s_guiTimeButton == null || s_guiPrecipButton == null || s_guiFogButton == null ||
                s_guiLightningButton == null || s_inputPlayerInput == null ||
                s_bitmapClickable == null || s_clickableIsClicked == null)
            {
                if (!m_reflectionFaultLogged)
                {
                    m_reflectionFaultLogged = true;
                    Warn("引擎私有字段反射绑定失败（字段名变化？）→ 世界控制捕获不可用：" +
                        "time=" + (s_guiTimeButton != null) + " precip=" + (s_guiPrecipButton != null) +
                        " fog=" + (s_guiFogButton != null) + " lightning=" + (s_guiLightningButton != null) +
                        " playerInput=" + (s_inputPlayerInput != null) +
                        " clickable=" + (s_bitmapClickable != null) +
                        " isClicked=" + (s_clickableIsClicked != null));
                }
                return;
            }
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                m_project = project;
            }
            if (project == null)
            {
                DetachInput();
                return;
            }
            bool client = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                shell.ClientReplica;
            if (client != m_clientReplica)
            {
                m_clientReplica = client;
                if (!client) DetachInput();
            }
            if (client)
            {
                UpdateInput(project);
            }
        }

        public void SetRequestSink(IWorldControlRequestSink sink)
        {
            m_requestSink = sink;
        }

        /// <summary>客户端：找到本机玩家的 ComponentGui 外壳并挂接（Injector 注入的 SuComponentGui）。</summary>
        private void UpdateInput(Project project)
        {
            if (m_spawner.TryGetLocalPlayer(project, out PlayerData local, out _))
            {
                ComponentPlayer player = local?.ComponentPlayer;
                if (player?.ComponentGui is SuComponentGui gui)
                {
                    if (!ReferenceEquals(gui, m_gui)) m_gui = gui;
                    gui.WorldControlSink = this;
                    return;
                }
                if (player != null && !m_inputShellMissingLogged)
                {
                    m_inputShellMissingLogged = true;
                    m_inputShellMissing++;
                    Warn("客户端的 ComponentGui 不是 SuComponentGui（Injector 未生效？当前：" +
                        (player.ComponentGui == null ? "null" : player.ComponentGui.GetType().Name) +
                        "）→ 创意四按钮无法同帧拦截，本地会先执行一次再被快照拉回");
                }
            }
            else
            {
                DetachInput();
            }
        }

        private void DetachInput()
        {
            if (m_gui != null && ReferenceEquals(m_gui.WorldControlSink, this))
            {
                m_gui.WorldControlSink = null;
            }
            m_gui = null;
        }

        // ================================================================= IWorldControlGuiSink：客户端捕获（同帧）

        bool IWorldControlGuiSink.TryAcceptWorldControl(ComponentGui gui, ComponentPlayer player)
        {
            if (gui == null || player == null) return false;
            IWorldControlRequestSink sink = m_requestSink;
            if (sink == null) return false;

            ButtonWidget timeButton = s_guiTimeButton?.GetValue(gui) as ButtonWidget;
            ButtonWidget precipButton = s_guiPrecipButton?.GetValue(gui) as ButtonWidget;
            ButtonWidget fogButton = s_guiFogButton?.GetValue(gui) as ButtonWidget;
            ButtonWidget lightningButton = s_guiLightningButton?.GetValue(gui) as ButtonWidget;
            ComponentInput input = player.Entity?.FindComponent<ComponentInput>();
            PlayerInput playerInput = input != null ? input.PlayerInput : default(PlayerInput);
            bool timeClick = timeButton?.IsClicked == true;
            bool precipClick = precipButton?.IsClicked == true;
            bool fogClick = fogButton?.IsClicked == true;
            bool lightningClick = lightningButton?.IsClicked == true;

            WorldControlActions actions = WorldControlActions.None;
            if (playerInput.TimeOfDay || timeClick) actions |= WorldControlActions.TimeOfDay;
            if (playerInput.Precipitation || precipClick) actions |= WorldControlActions.Precipitation;
            if (playerInput.Fog || fogClick) actions |= WorldControlActions.Fog;
            if (playerInput.Lighting || lightningClick) actions |= WorldControlActions.Lightning;
            if (actions == WorldControlActions.None) return false;

            // 闪电瞄准：与引擎同源（ComponentGui.cs:681-682 的眼睛位置 + 视线前向）。
            bool hasAim = false;
            float ax = 0f, ay = 0f, az = 0f, dx = 0f, dy = 0f, dz = 0f;
            if ((actions & WorldControlActions.Lightning) != 0)
            {
                ComponentCreatureModel model = player.ComponentCreatureModel;
                if (model != null)
                {
                    Vector3 eye = model.EyePosition;
                    Vector3 forward = Matrix.CreateFromQuaternion(model.EyeRotation).Forward;
                    ax = eye.X; ay = eye.Y; az = eye.Z;
                    dx = forward.X; dy = forward.Y; dz = forward.Z;
                    hasAim = true;
                }
                else
                {
                    m_aimMissing++;
                    Warn("闪电意图拿不到 ComponentCreatureModel（眼睛位置未知）→ 本帧不带闪电动作提交");
                    actions &= ~WorldControlActions.Lightning;
                    if (actions == WorldControlActions.None) return false;
                }
            }

            if (!sink.TrySubmit(actions, hasAim, ax, ay, az, dx, dy, dz))
            {
                // 未发送（无会话 / 通道不可用 / 模块拒绝）→ 放行原生行为，不消费点击。
                return false;
            }
            // 已提交：消费本帧标志，base.Update 里的 HandleInput 随后读到的全是 false，
            // 原生本地分支（改本地天气 / 本地落雷）整段不执行。
            if (timeClick) ConsumeClick(timeButton);
            if (precipClick) ConsumeClick(precipButton);
            if (fogClick) ConsumeClick(fogButton);
            if (lightningClick) ConsumeClick(lightningButton);
            if (input != null && (playerInput.TimeOfDay || playerInput.Precipitation ||
                playerInput.Fog || playerInput.Lighting))
            {
                playerInput.TimeOfDay = false;
                playerInput.Precipitation = false;
                playerInput.Fog = false;
                playerInput.Lighting = false;
                try
                {
                    s_inputPlayerInput.SetValue(input, playerInput);
                }
                catch (Exception)
                {
                    m_consumeClickFailures++;
                }
            }
            m_captured++;
            return true;
        }

        /// <summary>
        /// 消费按钮点击：把内层 ClickableWidget 的 IsClicked 置回 false
        /// （ButtonWidget.IsClicked 抽象只读；与旧联机 mod 的 ConsumeButtonClick 同款）。
        /// </summary>
        private void ConsumeClick(ButtonWidget button)
        {
            try
            {
                if (button is BitmapButtonWidget bitmap)
                {
                    ClickableWidget clickable = s_bitmapClickable.GetValue(bitmap) as ClickableWidget;
                    if (clickable != null) s_clickableIsClicked.SetValue(clickable, false);
                    return;
                }
                m_consumeClickFailures++;
            }
            catch (Exception)
            {
                m_consumeClickFailures++;
            }
        }

        // ================================================================= IWorldControlPort：主机执行

        public WorldControlOutcome ExecuteHost(WorldControlActions actions, bool hasAim,
            float aimX, float aimY, float aimZ, float dirX, float dirY, float dirZ,
            int requestingClientId)
        {
            var outcome = new WorldControlOutcome();
            Project project = GameManager.Project;
            if (project == null)
            {
                outcome.RejectReason = "host has no loaded world";
                return outcome;
            }
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(false);
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            SubsystemTimeOfDay timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(false);
            if (gameInfo == null || weather == null || timeOfDay == null)
            {
                outcome.RejectReason = "world subsystems missing (gameInfo/weather/timeOfDay)";
                return outcome;
            }
            // 引擎按钮的同一道门：四个原生分支都要求 Creative（ComponentGui.cs:679-710）。
            if (gameInfo.WorldSettings.GameMode != GameMode.Creative)
            {
                outcome.RejectReason = "world is not in creative mode";
                return outcome;
            }
            WorldControlActions applied = WorldControlActions.None;
            // 降水：引擎同款切换（ComponentGui.cs:684-696）。
            if ((actions & WorldControlActions.Precipitation) != 0)
            {
                if (weather.IsPrecipitationStarted) weather.ManualPrecipitationEnd();
                else weather.ManualPrecipitationStart();
                applied |= WorldControlActions.Precipitation;
            }
            // 雾：引擎同款切换（ComponentGui.cs:697-709）。
            if ((actions & WorldControlActions.Fog) != 0)
            {
                if (weather.IsFogStarted) weather.ManualFogEnd();
                else weather.ManualFogStart();
                applied |= WorldControlActions.Fog;
            }
            // 闪电：请求者眼睛 + 视线（ComponentGui.cs:679-683）。
            if ((actions & WorldControlActions.Lightning) != 0)
            {
                if (!hasAim)
                {
                    outcome.RejectReason = "lightning request without finite aim";
                    return outcome;
                }
                weather.ManualLightingStrike(new Vector3(aimX, aimY, aimZ),
                    new Vector3(dirX, dirY, dirZ));
                applied |= WorldControlActions.Lightning;
            }
            // 时间：跳到最近的 晨/午/暮/夜（ComponentGui.cs:710-737 的区间判定原样）。
            if ((actions & WorldControlActions.TimeOfDay) != 0)
            {
                float toDawn = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middawn);
                float toNoon = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midday);
                float toDusk = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middusk);
                float toMidnight = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midnight);
                float min = MathUtils.Min(toDawn, toNoon, toDusk, toMidnight);
                if (toDawn == min)
                {
                    timeOfDay.TimeOfDayOffset += toDawn;
                    outcome.TimeResult = WorldControlTimeResult.Dawn;
                }
                else if (toNoon == min)
                {
                    timeOfDay.TimeOfDayOffset += toNoon;
                    outcome.TimeResult = WorldControlTimeResult.Noon;
                }
                else if (toDusk == min)
                {
                    timeOfDay.TimeOfDayOffset += toDusk;
                    outcome.TimeResult = WorldControlTimeResult.Dusk;
                }
                else
                {
                    timeOfDay.TimeOfDayOffset += toMidnight;
                    outcome.TimeResult = WorldControlTimeResult.Midnight;
                }
                applied |= WorldControlActions.TimeOfDay;
            }
            outcome.Accepted = true;
            outcome.Applied = applied;
            outcome.PrecipitationStarted = weather.IsPrecipitationStarted;
            outcome.FogStarted = weather.IsFogStarted;
            Info("世界控制落地：client=" + requestingClientId + " actions=" + actions +
                " → 降水=" + outcome.PrecipitationStarted + " 雾=" + outcome.FogStarted +
                " 时间=" + outcome.TimeResult + " · " + DescribeCounters());
            return outcome;
        }

        // ================================================================= IWorldControlPort：客户端提示

        public void ShowClientResult(bool accepted, string rejectReason, WorldControlActions applied,
            bool precipitationStarted, bool fogStarted, WorldControlTimeResult timeResult)
        {
            Project project = GameManager.Project;
            ComponentGui gui = null;
            if (project != null && m_spawner.TryGetLocalPlayer(project, out PlayerData local, out _))
            {
                gui = local?.ComponentPlayer?.ComponentGui;
            }
            if (gui == null)
            {
                m_showFailures++;
                Warn("世界控制回执到达，但找不到本机 ComponentGui → 提示不显示（" +
                    (accepted ? "accepted" : "rejected: " + rejectReason) + "）");
                return;
            }
            if (!accepted)
            {
                gui.DisplaySmallMessage("World Control Rejected: " + rejectReason,
                    Color.White, blinking: false, playNotificationSound: false);
                return;
            }
            // 文案与引擎原生一致（ComponentGui.cs:689-705/720-736 的 DisplaySmallMessage）。
            if ((applied & WorldControlActions.Precipitation) != 0)
            {
                gui.DisplaySmallMessage(precipitationStarted ? "Precipitation On" : "Precipitation Off",
                    Color.White, blinking: false, playNotificationSound: false);
            }
            if ((applied & WorldControlActions.Fog) != 0)
            {
                gui.DisplaySmallMessage(fogStarted ? "Fog On" : "Fog Off",
                    Color.White, blinking: false, playNotificationSound: false);
            }
            if ((applied & WorldControlActions.TimeOfDay) != 0)
            {
                gui.DisplaySmallMessage(TimeResultText(timeResult),
                    Color.White, blinking: false, playNotificationSound: false);
            }
        }

        private static string TimeResultText(WorldControlTimeResult result)
        {
            switch (result)
            {
                case WorldControlTimeResult.Dawn: return "Dawn";
                case WorldControlTimeResult.Noon: return "Noon";
                case WorldControlTimeResult.Dusk: return "Dusk";
                case WorldControlTimeResult.Midnight: return "Midnight";
                default: return "Time";
            }
        }
    }
}

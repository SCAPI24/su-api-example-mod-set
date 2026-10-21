using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 组合根 → <see cref="MirrorViewFeedback"/> 的**主机权威冻结读取器**
    /// （实现 = <c>PlayerVitalsReplica.TryGetAuthorityFreeze</c>）。
    /// 第三分屏的冰覆盖只允许用它的返回值（95 字段 22 的原值），不得再按温度自己算。
    /// </summary>
    public delegate bool MirrorViewAuthorityFreezeProbe(PlayerKey key, out float progress,
        out uint stage, out string reason);

    /// <summary>
    /// replace 模式（<c>mirrorcharacter</c> / <c>mirrorcharactermode</c>）下**第三分屏的客户端反馈镜像**：
    /// 把客户端化身的掉血 / 血条 / 食物条 / 温度档位 / 等级文字 / 死亡表现重放到**视图所有者 H3** 的显示层上。
    ///
    /// 引擎事实（为什么必须由本类补写；逐条已核对源码）：
    ///   · 第三分屏 = H3 的 <c>GameWidget</c>；<c>ViewWidget.Draw</c> 只看 H3 自己的
    ///     <c>PlayerData.ComponentPlayer / IsReadyForPlaying</c>（<c>ViewWidget.cs:45-51</c>），
    ///     HUD 控件（血条 / 食物条 / 温度条 / 等级 / 快捷栏）也属于 H3 的 <c>ComponentGui</c>；
    ///     相机 Target 是客户端的 <c>ComponentPlayer</c>（<c>FppCamera</c> 读它的眼睛位置，<c>FppCamera.cs:21-44</c>）。
    ///     所以"看到的是客户端的画面，HUD 却是 H3 的"。
    ///   · 掉血反馈只在 <c>ComponentHealth.Update</c> 里写，且写的是**该组件所属玩家**的 GUI / 覆盖层
    ///     （<c>ComponentHealth.cs:251-265</c>）。客户端的 <c>ComponentHealth.Update</c> 在客户端上跑
    ///     （客户端自己有红闪），主机侧 H3 的 <c>ComponentHealth.Update</c> 在绑定期间被摘掉
    ///     （<c>MirrorCharacterEngine.TryBind</c>）→ 第三分屏什么都不闪。
    ///   · <c>ComponentScreenOverlays.Update</c> 每拍把 <c>RedoutFactor / BlackoutFactor / IceFactor</c> 清 0
    ///     （<c>:60-77</c>，UpdateOrder.Reset），而本类在 <c>Frame.Update</c> 里跑（<c>Program.cs:146-147</c>，
    ///     在 <c>ScreensManager.Update → GameManager.UpdateProject</c> 之后、<c>ScreensManager.Draw</c> 之前），
    ///     所以有值就必须**每拍**重写（与 <c>PlayerVitalsReplica</c> 写 <c>IceFactor</c> 同一条时序事实）。
    ///
    /// 硬约束（呈现层，绝不碰权威状态）：
    ///   · 只读客户端化身的 <c>ComponentHealth</c> / <c>ComponentVitalStats</c>（含只读
    ///     <c>m_targetTemperature</c>）与 <c>PlayerData.Level</c>；**不写**它们的任何字段、不 Injure、
    ///     不复制背包 / 衣物 / 位置 / 动作（那是 copy 模式的事，replace 模式绝不复制）。
    ///   · 只写 H3 的显示层：<c>HealthBarWidget.Value/Flash</c>、<c>FoodBarWidget.Value</c>、
    ///     <c>TemperatureBarWidget.BarSubtexture/Value</c>、<c>LevelLabelWidget.Text</c>、
    ///     <c>ComponentScreenOverlays.RedoutFactor/BlackoutFactor/IceFactor</c>、
    ///     <c>ComponentGui.DisplayLargeMessage</c>。
    ///   · **不切相机**：第三分屏的 <c>ActiveCamera</c> 必须留在客户端第一人称 <c>FppCamera</c>
    ///     （否则 <c>MirrorCharacterEngine.ValidateBound</c> 会把相机重新对回客户端）。
    ///     死亡表现 = "YOU HAVE DIED" 大消息（与引擎 <c>PlayerData.PlayerDead</c> 同文本 / 同时长；
    ///     产品口径 2026-10-20 起不再压黑屏，改由 <c>MirrorCharacterEngine</c> 切 <c>DeathCamera</c>）。
    ///   · **死亡大消息控件的位置**（2026-10-20 修复"客户端死亡时第三分屏看不到死亡文字"）：
    ///     引擎的 <c>Gui/LargeMessage</c> 在绑定期间被移到 <c>GameWidget</c> 根下。根因：客户端角色死亡 →
    ///     它自己的 HUD 被隐藏（<c>DeathCamera.IsEntityControlEnabled = false</c> →
    ///     <c>ComponentGui.UpdateWidgets</c> 关掉 <c>ControlsContainer</c>），UI 镜像把这个显隐写到
    ///     **同一个镜像视图**的 <c>GuiWidget.IsVisible</c>（<c>Engine/UiMirrorEngine.cs:271</c>，主机日志
    ///     <c>[SuMP][ui-state] … hudHidden=1</c>）—— 大消息是 <c>Gui</c> 的子控件，于是"写进去了却画不出来"
    ///     （实测 <c>viewDeathOverlay=1</c> 而屏上无字）。移到 <c>GameWidget</c> 根下后不受 <c>Gui</c> 显隐
    ///     影响，文字 / 淡入仍由引擎 <c>ComponentGui.UpdateWidgets</c> 驱动（外观同款），解绑按原索引移回。
    ///   · 解绑（离开房间 / 世界卸载 / 客户端离开 / 回滚）时把绑定前抓到的 HUD 显示值写回、
    ///     清掉我们设置的大消息与覆盖层，并停止更新（<see cref="Release"/>）。
    ///
    /// 实机问题 1（2026-10-21）· **冰覆盖必须与客户端同一条权威值 / 同一条平滑**：
    ///   · 客户端：<c>freeze_progress</c>（vitals.proto 字段 22 = 主机侧客户端化身的
    ///     <c>PlayerFreezePolicy.TryCapture(Temperature)</c>）经 95 下发，再由
    ///     <c>PlayerVitalsReplica.ApplyLocalHud</c> 用 <see cref="PlayerPresentationInterpolator"/>
    ///     （时间常数 0.2 s / 限速 3 每秒 / 0.002 贴合）每帧逼近后写 <c>IceFactor</c>；
    ///   · 旧实现的第三屏**自己**按 <c>vitals.Temperature</c> 算 <c>Saturate(1 - T/6)</c> 并**阶跃**写入
    ///     —— 与客户端不是同一条数据 / 时序，于是"客户端先冻、第三屏晚很多、屏幕占比不一样"；
    ///   · 现在：目标值只来自 <see cref="MirrorViewAuthorityFreezeProbe"/>（= <c>PlayerVitalsReplica</c>
    ///     缓存的**95 原值**），平滑复用**同一个** <see cref="PlayerPresentationInterpolator"/>；
    ///     拿不到权威值就明确跳过 + 计数（<c>viewIceSource=authority-missing</c>），绝不按温度顶替。
    ///
    /// 实机问题 2（2026-10-21）· **客户端流感 / 食物中毒 / 着火在第三屏也要有同款表现**：
    ///   · 主机侧客户端化身的 <c>ComponentFlu / ComponentSickness / ComponentOnFire</c> Update
    ///     **在主机上跑**（<c>PlayerVitalsReplica.SuppressRemoteAvatars</c> 只在客户端调用），所以
    ///     咳嗽 / 喷嚏 / 呕吐音效、<c>PukeParticleSystem</c>、<c>OnFireParticleSystem</c> 主机世界里
    ///     本来就有；第三分屏的相机既渲染同一批粒子，也**本身就是**音频监听点
    ///     （<c>SubsystemAudio.Update:178-182</c> 收集每个 GameWidget 的 <c>ActiveCamera</c>），
    ///     所以这里**绝不重播**音效 / 粒子（重播会双份）；
    ///   · 真正缺的是**屏幕覆盖层**：<c>ComponentFlu.Update</c> 把黑屏因子写在**化身自己的**
    ///     <c>ComponentScreenOverlays</c> 上，而 <c>ComponentScreenOverlays.Draw</c> 只在
    ///     <c>m_componentPlayer.GameWidget == camera.GameWidget</c> 时画，化身的 GameWidget 已被
    ///     <c>NetworkAvatarSpawner</c> 从界面子系统摘掉 ⇒ 第三屏永远看不到绿屏 / 黑屏；
    ///   · 现在：每拍把主机权威 <c>m_blackoutFactor</c> / <c>m_greenoutFactor</c>（引擎已按
    ///     0.5/s 渐入 0.95 / 0.5/s 渐出算好的曲线，与客户端 <c>AdvanceLocalEffectPresentation</c>
    ///     同一组式子）镜像到 H3 的 <c>BlackoutFactor / GreenoutFactor</c>，并用
    ///     <see cref="MirrorViewEffectWatch"/> 计 <c>viewFluEffectPresented / Duplicate / Stale / Rejected</c>。
    ///
    /// 无静态可变状态：视图所有者 / 客户端 / 时钟 / 诊断都由组合根经 <c>MirrorCharacterEngine</c> 注入。
    /// </summary>
    public sealed class MirrorViewFeedback
    {
        private const string OwnerId = "mirror-view-feedback";

        /// <summary>计数器日志最小间隔（需求：10 秒）。</summary>
        private const double LogIntervalSeconds = 10.0;

        private readonly IModParentField m_parentField;
        private readonly Func<double> m_clock;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly MirrorViewFeedbackCounters m_counters = new MirrorViewFeedbackCounters();
        private readonly MirrorViewFeedbackDiff m_diff = new MirrorViewFeedbackDiff();

        /// <summary>
        /// 病程 / 火焰表现的逐帧状态机（实机问题 2）：读**主机侧客户端化身**的权威表现因子
        /// （<c>ComponentFlu.m_blackoutFactor</c> / <c>ComponentSickness.m_greenoutFactor</c>）
        /// 并镜像到 H3 的覆盖层。纯逻辑在 <see cref="MirrorViewEffectWatch"/>。
        /// </summary>
        private readonly MirrorViewEffectWatch m_effects = new MirrorViewEffectWatch();

        private readonly HashSet<string> m_reportedOnce =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>问题日志的 10 秒限流表（问题键 → 上次写日志的 <c>Time.RealTime</c>）。</summary>
        private readonly Dictionary<string, double> m_problemLogTime =
            new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>病程表现的数据源稳定键（状态行 <c>viewFluEffectSource</c>）。</summary>
        private const string FluEffectSource = "authority-host-avatar";

        private bool m_linked;
        private ComponentPlayer m_viewOwner;
        private ComponentGui m_gui;
        private ComponentScreenOverlays m_overlays;
        private string m_viewOwnerName = string.Empty;
        private string m_clientName = string.Empty;
        private int m_clientId = -1;
        private double m_lastLogTime;

        // 我们真的写过覆盖层 / 大消息才需要在解绑时清（H3 自己的覆盖层不归我们管）。
        private bool m_wroteRedout;
        private bool m_wroteBlackout;
        private bool m_wroteGreenout;
        private bool m_wroteIce;
        private bool m_showedDeathMessage;
        // 死亡大消息控件（Gui/LargeMessage）：绑定期间移到 GameWidget 根下，解绑按原索引移回。
        private ContainerWidget m_deathMessageWidget;
        private ContainerWidget m_deathMessageOriginParent;
        private int m_deathMessageOriginIndex = -1;
        private bool m_deathMessageRelocated;

        // 死亡大消息的生命周期（下发边沿 / 引擎同款到期时刻）。
        private bool m_deathTextShown;
        private double m_deathTextExpiresAt;

        // 绑定前 H3 的显示值（解绑写回；只碰显示层，不碰任何组件字段）。
        private bool m_savedHud;
        private float m_savedHealthBarValue;
        private float m_savedFoodBarValue;
        private Subtexture m_savedTemperatureTexture;
        private float m_savedTemperatureBarValue;
        private string m_savedLevelText;

        // 温度贴图按档位缓存（ContentManager.Get 只在档位变化时调用一次）。
        private int m_cachedTemperatureIndex = int.MinValue;
        private Subtexture m_cachedTemperatureTexture;

        public MirrorViewFeedback(IModParentField parentField, Func<double> clock,
            IModuleDiagnostics diagnostics)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_clock = clock;
            m_diagnostics = diagnostics;
        }

        /// <summary>计数器（10 秒日志 / 状态行）。</summary>
        public MirrorViewFeedbackCounters Counters => m_counters;

        /// <summary>当前是否已接入（true 才允许每拍 <see cref="Tick"/>）。</summary>
        public bool IsLinked => m_linked;

        /// <summary>绑定前的 HUD 显示值是否已抓取（解绑写回的依据）。</summary>
        public bool HasSavedHud => m_savedHud;

        /// <summary>
        /// 绑定 replace 视图后调用：抓 H3 的 HUD 显示值快照，并复位差分状态。
        /// 失败返回 false + 明确原因（调用方记 <c>viewFeedbackFailures</c> 并保持绑定，不静默）。
        /// </summary>
        public bool Attach(ComponentPlayer viewOwner, int clientId, string clientName,
            out string reason)
        {
            reason = null;
            // 重复接入（重新绑定 / 上一轮没有释放）：先把上一轮的显示值写回，再抓新的。
            Release("重新接入前释放上一轮");
            if (viewOwner == null)
            {
                reason = "视图所有者（H3）为空，第三分屏的 HUD 不存在";
                m_counters.NoteFailure(reason);
                return false;
            }
            ComponentGui gui = viewOwner.ComponentGui;
            if (gui == null)
            {
                reason = "H3 没有 ComponentGui（第三分屏的 HUD 不存在）：客户端反馈无处镜像";
                m_counters.NoteFailure(reason);
                return false;
            }
            ComponentScreenOverlays overlays = viewOwner.ComponentScreenOverlays;
            if (overlays == null)
            {
                reason = "H3 没有 ComponentScreenOverlays（红屏 / 黑屏 / 冰冻覆盖无处镜像）";
                m_counters.NoteFailure(reason);
                return false;
            }
            if (gui.HealthBarWidget == null)
            {
                reason = "H3 的 ComponentGui 没有 HealthBarWidget（引擎 XML 变了）：伤情反馈无法镜像";
                m_counters.NoteFailure(reason);
                return false;
            }
            m_viewOwner = viewOwner;
            m_gui = gui;
            m_overlays = overlays;
            m_clientId = clientId;
            m_clientName = clientName ?? string.Empty;
            m_viewOwnerName = viewOwner.PlayerData == null
                ? string.Empty
                : (viewOwner.PlayerData.Name ?? string.Empty);
            m_wroteRedout = false;
            m_wroteBlackout = false;
            m_wroteGreenout = false;
            m_wroteIce = false;
            m_showedDeathMessage = false;
            m_deathTextShown = false;
            m_deathTextExpiresAt = 0.0;
            m_cachedTemperatureIndex = int.MinValue;
            m_cachedTemperatureTexture = null;
            m_diff.Reset();
            m_effects.Reset();
            m_problemLogTime.Clear();
            CaptureSavedHud();
            // 死亡大消息控件必须离开 HUD 根：客户端死亡时 UI 镜像会关掉 Gui 根，文字会跟着被藏掉。
            RelocateDeathMessage(viewOwner);
            m_linked = true;
            m_lastLogTime = m_clock == null ? 0.0 : m_clock();
            Log("第三分屏客户端反馈镜像已接入：视图所有者「" + m_viewOwnerName + "」← 客户端 " +
                m_clientId + "「" + m_clientName + "」· 控件=" + DescribeWidgets() +
                " · 死亡大消息=" + DescribeDeathMessagePlacement() +
                " · 只写显示层（血条 / 食物条 / 温度档位 / 等级文字 / 红屏 / 大消息）；" +
                "不写 H3 的生命 / 体征 / 背包，不切相机");
            return true;
        }

        /// <summary>
        /// 每拍驱动（replace 绑定期间由 <c>MirrorCharacterEngine.ValidateBound</c> 调用）。
        /// 只读客户端化身的组件；把计划里的显示值写进 H3 的控件。异常 / 控件缺失一律计数 + 限流告警。
        ///
        /// <paramref name="freeze"/> = 主机权威冻结采样（<c>PlayerVitalsReplica.TryGetAuthorityFreeze</c>，
        /// 由 <c>MirrorCharacterEngine</c> 每拍取一次喂进来）：第三屏冰覆盖**只**用它，不再按温度自己算。
        /// </summary>
        public void Tick(ComponentPlayer viewOwner, ComponentPlayer source, string clientName,
            float dt, in MirrorViewFreezeSample freeze)
        {
            m_counters.NoteFrame();
            if (!m_linked)
            {
                m_counters.NoteSkipped("not-linked");
                return;
            }
            if (viewOwner != null && !ReferenceEquals(viewOwner, m_viewOwner))
            {
                m_counters.NoteSkipped("view-owner-changed");
                WarnOnce("view-owner-changed",
                    "第三分屏的所有者变了（不是接入时的那个 ComponentPlayer）→ 本拍不写显示层" +
                    "（重新绑定会重新接入）");
                return;
            }
            if (source == null)
            {
                m_counters.NoteSkipped("source-player-missing");
                LogRateLimited();
                return;
            }
            ComponentHealth health = source.ComponentHealth;
            if (health == null)
            {
                m_counters.NoteSkipped("source-health-missing");
                LogRateLimited();
                return;
            }
            // 病程 / 火焰表现（实机问题 2）：与体征 HUD 相互独立 —— 即使温度读不到本拍也照样呈现
            // （覆盖层每拍被引擎清 0，漏一拍就闪一下）。
            ApplyEffects(source);
            ComponentVitalStats vitals = source.ComponentVitalStats;
            PlayerData sourceData = source.PlayerData;
            if (sourceData == null)
            {
                m_counters.NoteSkipped("source-player-data-missing");
                LogRateLimited();
                return;
            }
            float targetTemperature = 0f;
            if (vitals != null)
            {
                try
                {
                    targetTemperature = m_parentField.GetParentField<float>(vitals,
                        "m_targetTemperature", typeof(ComponentVitalStats));
                }
                catch (Exception e)
                {
                    m_counters.NoteFailure("target-temperature-unreadable: " +
                        e.GetType().Name + " " + e.Message);
                    WarnOnce("target-temperature",
                        "读不到客户端化身的 ComponentVitalStats.m_targetTemperature（温度条档位无法镜像）→ " +
                        "本拍跳过 HUD：" + e.GetType().Name + " " + e.Message);
                    LogRateLimited();
                    return;
                }
            }
            var sample = new MirrorViewSourceSample(hasHealth: true, health: health.Health,
                causeOfDeath: health.CauseOfDeath, hasVitals: vitals != null,
                food: vitals == null ? 0f : vitals.Food,
                temperature: vitals == null ? 0f : vitals.Temperature,
                targetTemperature: targetTemperature, level: sourceData.Level,
                hasFreeze: freeze.Available, freezeProgress: freeze.Progress,
                freezeStage: freeze.Stage);
            MirrorViewFramePlan plan = m_diff.Advance(in sample, dt, clientName ?? m_clientName);
            if (plan.SkipReason != null)
            {
                m_counters.NoteSkipped(plan.SkipReason);
                LogRateLimited();
                return;
            }
            if (plan.FailureReason != null)
            {
                m_counters.NoteFailure(plan.FailureReason);
                WarnOnce("plan-failure:" + plan.FailureReason,
                    "第三分屏客户端反馈镜像收到非法输入（" + plan.FailureReason +
                    "）→ 保留上一次有效显示，本拍不写控件 · " + sample.Describe());
                LogRateLimited();
                return;
            }
            if (plan.HudSkipReason != null)
            {
                m_counters.NoteSkipped(plan.HudSkipReason);
                WarnOnce("hud-skip:" + plan.HudSkipReason,
                    "第三分屏 HUD 数值这一拍不可观测（" + plan.HudSkipReason +
                    "）→ 红屏 / 死亡反馈照常，HUD 保留上一次有效显示");
            }
            Apply(plan);
            LogRateLimited();
        }

        /// <summary>
        /// 解绑 / 回滚 / 世界卸载：把绑定前的 HUD 显示值写回，清掉我们设置的大消息与覆盖层，停止更新。
        /// 可重复调用（幂等）；返回是否真的写回过 HUD 显示值。
        /// </summary>
        public bool Release(string reason)
        {
            bool linked = m_linked;
            bool restored = false;
            try
            {
                if (m_gui != null)
                {
                    if (m_savedHud)
                    {
                        if (m_gui.HealthBarWidget != null)
                            m_gui.HealthBarWidget.Value = m_savedHealthBarValue;
                        if (m_gui.FoodBarWidget != null)
                            m_gui.FoodBarWidget.Value = m_savedFoodBarValue;
                        if (m_gui.TemperatureBarWidget != null)
                        {
                            m_gui.TemperatureBarWidget.BarSubtexture = m_savedTemperatureTexture;
                            m_gui.TemperatureBarWidget.Value = m_savedTemperatureBarValue;
                        }
                        if (m_gui.LevelLabelWidget != null && m_savedLevelText != null)
                            m_gui.LevelLabelWidget.Text = m_savedLevelText;
                        restored = true;
                    }
                    if (m_showedDeathMessage)
                    {
                        m_gui.DisplayLargeMessage(string.Empty, string.Empty, 0f, 0f);
                        m_showedDeathMessage = false;
                    }
                    // 把死亡大消息控件按原索引移回 HUD 根（不移回会留下"文字盖在 Gui 上"的副作用）。
                    RestoreDeathMessage();
                }
                if (m_overlays != null)
                {
                    // 这三个因子每拍被引擎清 0（ComponentScreenOverlays.Update:60-77），
                    // 停止更新本身就等于清除；这里对"我们自己写过的"再显式写 0，
                    // 保证解绑当帧（同帧还有 Draw）不残留一次客户端画面。
                    // 绿屏（病程表现，实机问题 2）同理。
                    if (m_wroteRedout) m_overlays.RedoutFactor = 0f;
                    if (m_wroteBlackout) m_overlays.BlackoutFactor = 0f;
                    if (m_wroteGreenout) m_overlays.GreenoutFactor = 0f;
                    if (m_wroteIce) m_overlays.IceFactor = 0f;
                }
            }
            catch (Exception e)
            {
                m_counters.NoteFailure("release: " + e.GetType().Name + " " + e.Message);
                m_diagnostics?.Warn(OwnerId,
                    "解除第三分屏客户端反馈镜像时写回显示值失败（停止更新，显示值可能停在客户端最后值）：" +
                    e.GetType().Name + " " + e.Message);
            }
            m_linked = false;
            m_viewOwner = null;
            m_gui = null;
            m_overlays = null;
            m_wroteRedout = false;
            m_wroteBlackout = false;
            m_wroteGreenout = false;
            m_wroteIce = false;
            m_showedDeathMessage = false;
            m_deathTextShown = false;
            m_deathTextExpiresAt = 0.0;
            m_savedHud = false;
            m_savedLevelText = null;
            m_savedTemperatureTexture = null;
            m_cachedTemperatureIndex = int.MinValue;
            m_cachedTemperatureTexture = null;
            m_clientId = -1;
            m_clientName = string.Empty;
            m_viewOwnerName = string.Empty;
            m_diff.Reset();
            m_effects.Reset();
            m_problemLogTime.Clear();
            if (linked)
            {
                Log("第三分屏客户端反馈镜像已解除（" + (reason ?? "未给原因") + "）· HUD 显示值写回=" +
                    (restored ? "是" : "否（控件在解绑时已不可用）") + " · " +
                    m_counters.Describe());
            }
            return restored;
        }

        /// <summary>可审计的一行（10 秒日志 / 状态行）。</summary>
        public string Describe() =>
            "linked=" + (m_linked ? "1" : "0") + " viewOwner=\"" + m_viewOwnerName +
            "\" client=" + m_clientId + " clientName=\"" + m_clientName + "\" " +
            m_counters.Describe() + " diff{" + m_diff.Describe() + "}" +
            " effects{" + m_effects.Describe() + "}";

        // ---------------------------------------------------------------- 写显示层

        private void Apply(in MirrorViewFramePlan plan)
        {
            MirrorViewHudWrites hud = plan.Hud;
            MirrorViewDamageStep damage = plan.Damage;
            MirrorViewDeathStep death = plan.Death;
            try
            {
                if (death.KeepBlackout)
                {
                    m_overlays.BlackoutFactor = 1f;
                    m_wroteBlackout = true;
                }
                ApplyDeathText(in death);
                if (damage.WriteRedout)
                {
                    m_overlays.RedoutFactor = MathUtils.Max(m_overlays.RedoutFactor,
                        damage.RedoutFactor);
                    m_wroteRedout = true;
                    m_counters.NoteDamageFlash();
                }
                if (damage.FlashCount > 0 && m_gui.HealthBarWidget != null)
                {
                    m_gui.HealthBarWidget.Flash(damage.FlashCount);
                    m_counters.NoteHealthBarFlash();
                }
                if (hud.WriteHealthBar)
                    WriteHealthBar(hud.HealthBarValue);
                if (hud.WriteFoodBar)
                    WriteFoodBar(hud.FoodBarValue);
                if (hud.WriteTemperatureTexture)
                    WriteTemperature(hud.TemperatureTextureIndex, hud.TemperatureTextureName);
                ApplyIce(plan.Ice);
                if (hud.WriteLevelLabel)
                    WriteLevelLabel(hud.Level);
            }
            catch (Exception e)
            {
                m_counters.NoteFailure("apply: " + e.GetType().Name + " " + e.Message);
                m_diagnostics?.Warn(OwnerId,
                    "第三分屏客户端反馈写入失败（本拍停止写剩余显示值；下一拍照常重试）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 冰覆盖（实机问题 1）：把**主机权威 freeze_progress 的插值结果**写进 H3 的 <c>IceFactor</c>。
        ///
        /// 时序事实：<c>ComponentScreenOverlays.Update</c> 每拍先清 0（<c>:68-73</c>），
        /// 所以只要 &gt; 0 就必须每拍重写；为 0 时不写（引擎已经是 0）。
        /// 计数：<c>viewIceSource / viewIceValue / viewIceFrames / viewIceSnap / viewIceMissing / viewIceFailures</c>。
        /// 独立 try/catch：写失败只计 <c>viewIceFailures</c>，不影响血条 / 食物条 / 温度档位。
        /// </summary>
        private void ApplyIce(MirrorViewIceStep ice)
        {
            m_counters.NoteIceSource(ice.Source, ice.Value);
            if (ice.Advanced)
            {
                // 只有"真的有权威目标"的帧才计帧数 / 贴合次数；死亡归零帧只体现在
                // viewIceSource=authority-dead（否则尸体期间 viewIceSnap 会每拍 +1）。
                m_counters.NoteIceFrame();
                if (ice.Snapped) m_counters.NoteIceSnap();
            }
            if (ice.Source == MirrorViewIceSource.Missing)
            {
                m_counters.NoteIceMissing("authority-freeze-missing");
                NoteProblem("view-ice-missing",
                    "第三分屏冰覆盖：主机权威冻结进度这一拍不可观测（" +
                    (m_counters.LastFailureReason ?? "未给原因") +
                    "）→ 本拍不写 IceFactor（绝不按温度自己算，也不拿 0 顶替）· " +
                    m_diff.Describe());
                return;
            }
            if (!ice.Write) return;
            try
            {
                m_overlays.IceFactor = ice.Value;
                m_wroteIce = true;
                m_counters.NoteIceWrite();
            }
            catch (Exception e)
            {
                string reason = "view-ice-write: " + e.GetType().Name + " " + e.Message;
                m_counters.NoteIceFailure(reason);
                NoteProblem("view-ice-write",
                    "第三分屏冰覆盖写 IceFactor 失败（本拍不写；下一拍重试）：" + reason);
            }
        }

        /// <summary>
        /// 病程 / 火焰表现（实机问题 2）：每拍读**主机侧客户端化身**的权威表现因子并镜像到 H3 的覆盖层。
        ///
        /// 为什么读组件状态而不是重放 109 事件（选型理由）：
        ///   ① 109 的产生端 <c>PlayerVitalsReplica.PollAuthorityEffects</c> 由 <c>PlayerVitalsModule</c>
        ///      的主机 tick 独占消费（它每拍推进边沿状态并清空容器），要在第三屏再播一遍就得把事件
        ///      广播回引擎层（新增端口 + 改模块 + 组合根多行接线），并且要自己再维护一份
        ///      "已播 / 过期" 账本 —— 与"不改权威模拟、不重复扣血"的目标相悖；
        ///   ② 第三分屏与权威在**同一个进程、同一帧**：直接读化身的权威表现因子，边沿与曲线天然一致、
        ///      零网络延迟，也不会把同一次咳嗽在主机世界与第三屏播两遍；
        ///   ③ 声音 / 粒子本来就由主机模拟产生（咳嗽音效、<c>PukeParticleSystem</c>、
        ///      <c>OnFireParticleSystem</c>），第三分屏的相机渲染同一批粒子、也是音频监听点之一，
        ///      **只需要补屏幕覆盖层**（<c>ComponentScreenOverlays.Draw</c> 只画自己 GameWidget 的那一份）。
        ///
        /// 计数：<c>viewFluEffectPresented / Frames / Rejected / Duplicate / Stale</c> +
        /// <c>viewFluBlackoutFrames / viewFluGreenoutFrames / viewFluCoughFrames / viewFluPukeFrames /
        /// viewFireFrames</c>；失败按 10 秒限流（<see cref="NoteProblem"/>）。
        /// </summary>
        private void ApplyEffects(ComponentPlayer source)
        {
            if (!TryReadEffectSample(source, out MirrorViewEffectSample sample,
                    out string readReason))
            {
                m_counters.NoteFluRejected(readReason);
                NoteProblem("view-effect-read",
                    "第三分屏病程表现：读主机侧客户端化身的表现因子失败（本拍不写覆盖层）：" +
                    readReason);
                return;
            }
            MirrorViewEffectPlan plan = m_effects.Advance(in sample);
            if (plan.Failure != null)
            {
                m_counters.NoteFluRejected(plan.Failure);
                NoteProblem("view-effect-invalid",
                    "第三分屏病程表现：主机侧客户端化身的表现因子非法（" + plan.Failure +
                    "）→ 本拍不写覆盖层 · " + sample.Describe());
                return;
            }
            m_counters.NoteFluSource(FluEffectSource);
            if (plan.Stale)
            {
                m_counters.NoteFluStale();
                Log("第三分屏病程表现：客户端化身换代（复活换实体）→ 上一段黑屏 / 绿屏 / 着火表现作废" +
                    "（累计 viewFluEffectStale=" + m_counters.ViewFluEffectStale + "）");
            }
            try
            {
                if (plan.WriteBlackout)
                {
                    m_overlays.BlackoutFactor = MathUtils.Max(m_overlays.BlackoutFactor,
                        plan.Blackout);
                    m_wroteBlackout = true;
                    m_counters.NoteFluBlackoutFrame();
                }
                if (plan.WriteGreenout)
                {
                    m_overlays.GreenoutFactor = MathUtils.Max(m_overlays.GreenoutFactor,
                        plan.Greenout);
                    m_wroteGreenout = true;
                    m_counters.NoteFluGreenoutFrame();
                }
            }
            catch (Exception e)
            {
                string reason = "view-effect-write: " + e.GetType().Name + " " + e.Message;
                m_counters.NoteFluRejected(reason);
                NoteProblem("view-effect-write",
                    "第三分屏病程表现写覆盖层失败（本拍不写；下一拍重试）：" + reason);
                return;
            }
            if (plan.BlackoutOnset || plan.GreenoutOnset || plan.FireOnset)
                m_counters.NoteFluPresented();
            if (plan.Duplicate) m_counters.NoteFluDuplicate();
            if (plan.Coughing) m_counters.NoteFluCoughFrame();
            if (plan.Puking) m_counters.NoteFluPukeFrame();
            if (plan.FireOn) m_counters.NoteFireFrame();
            if (plan.WriteBlackout || plan.WriteGreenout || plan.FireOn)
                m_counters.NoteFluFrame();
        }

        /// <summary>
        /// 读主机侧客户端化身的**权威**病程 / 火焰表现状态（只读，绝不写）：
        ///   · <c>ComponentFlu.m_blackoutFactor</c>（引擎已按 0.5/s 渐入 0.95 / 渐出算好的黑屏因子）；
        ///   · <c>ComponentSickness.m_greenoutFactor</c>（同款绿屏因子）；
        ///   · 公开的 <c>IsCoughing</c> / <c>IsPuking</c> / <c>IsOnFire</c>（音效 / 粒子由主机模拟产生）。
        /// 任何一次私有字段读取失败都返回 false + 明确原因（不拿 0 顶替）。
        /// </summary>
        private bool TryReadEffectSample(ComponentPlayer source,
            out MirrorViewEffectSample sample, out string reason)
        {
            sample = default;
            reason = null;
            Entity entity = source == null ? null : source.Entity;
            if (entity == null)
            {
                reason = "source-entity-missing（客户端化身这一拍没有实体）";
                return false;
            }
            ComponentFlu flu = entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = entity.FindComponent<ComponentSickness>();
            ComponentOnFire onFire = entity.FindComponent<ComponentOnFire>();
            float blackout = 0f;
            float greenout = 0f;
            try
            {
                if (flu != null)
                    blackout = m_parentField.GetParentField<float>(flu, "m_blackoutFactor",
                        typeof(ComponentFlu));
                if (sickness != null)
                    greenout = m_parentField.GetParentField<float>(sickness, "m_greenoutFactor",
                        typeof(ComponentSickness));
            }
            catch (Exception e)
            {
                reason = "effect-factor-unreadable: " + e.GetType().Name + " " + e.Message;
                return false;
            }
            sample = new MirrorViewEffectSample(
                RuntimeHelpers.GetHashCode(entity),
                flu != null, blackout, flu != null && flu.IsCoughing,
                sickness != null, greenout, sickness != null && sickness.IsPuking,
                onFire != null, onFire != null && onFire.IsOnFire);
            return true;
        }

        /// <summary>
        /// 死亡大消息的**生命周期**（只写 H3 的显示层）：死亡边沿下发**一次**、到期由引擎自己丢弃、
        /// 复活时显式清除 —— 与引擎单机口径完全一致（实机问题 5）。
        ///
        /// 为什么不再续期：旧实现用负延迟每 27~30 秒续期，好让第三屏"一直显示到客户端复活"。
        /// 实测客户端死亡后长时间不复活时，第三屏 "CAUSE OF DEATH" 会一直挂着（日志里死亡 9 分钟
        /// 后 `viewDeathTextWrite` 仍在增长），而单机 / 客户端自己的死亡文字 30 秒后自己消失。
        /// 现在：下发一次 → 引擎在 <c>StartTime + Duration</c> 时丢弃（<c>ComponentGui.cs:385-388</c>）
        /// → 复活边沿再显式清一次（防止玩家在 30 秒内复活时文字残留）。
        ///
        /// 独立 try/catch：异常单独计 <c>viewDeathTextFailures</c>（复活清除失败另计
        /// <c>viewDeathTextClearFailures</c>），不混进 HUD 写入失败（下一拍重试）。
        /// </summary>
        private void ApplyDeathText(in MirrorViewDeathStep death)
        {
            bool clearing = death.ClearOverlay;
            try
            {
                if (clearing)
                {
                    // 复活：黑屏因子由引擎每拍清 0，这里只清我们下发的大消息。
                    if (m_showedDeathMessage || m_deathTextShown)
                    {
                        m_gui.DisplayLargeMessage(string.Empty, string.Empty, 0f, 0f);
                        m_counters.NoteDeathTextCleared();
                    }
                    m_showedDeathMessage = false;
                    m_deathTextShown = false;
                    m_deathTextExpiresAt = 0.0;
                    m_counters.NoteRespawnClear();
                }
                if (death.ShowOverlay)
                {
                    // 与引擎同款：30 秒 / 1.5 秒延迟；只下发这一次，不再续期。
                    m_gui.DisplayLargeMessage(MirrorViewDeathText.LargeText, death.SmallText,
                        MirrorViewDeathText.DurationSeconds, MirrorViewDeathText.DelaySeconds);
                    m_showedDeathMessage = true;
                    m_deathTextShown = true;
                    m_deathTextExpiresAt = MirrorViewDeathText.ExpiryAt(CurrentTime(),
                        MirrorViewDeathText.DelaySeconds);
                    m_counters.NoteDeathOverlay();
                    m_counters.NoteDeathTextShown();
                    m_counters.NoteDeathTextWrite();
                    Log("客户端 " + m_clientId + "「" + m_clientName + "」死亡 → 第三分屏 \"" +
                        MirrorViewDeathText.LargeText + "\"（死因：" + (death.Cause ?? "Unknown") +
                        "）；与引擎同款 30 秒到期或复活时清除（不再续期）· 死亡大消息控件=" +
                        DescribeDeathMessagePlacement());
                    return;
                }
                if (m_deathTextShown && CurrentTime() >= m_deathTextExpiresAt)
                {
                    // 引擎已经在 StartTime + Duration 之后丢弃消息（ComponentGui.cs:385-388）。
                    // 这里只做状态收尾：**不再**写控件（续期正是文字永不消失的根因）。
                    m_deathTextShown = false;
                    m_showedDeathMessage = false;
                    m_deathTextExpiresAt = 0.0;
                    m_counters.NoteDeathTextExpired();
                }
            }
            catch (Exception e)
            {
                string reason = "death-text: " + e.GetType().Name + " " + e.Message;
                m_counters.NoteDeathTextFailures(reason);
                if (clearing) m_counters.NoteDeathTextClearFailures(reason);
                m_diagnostics?.Warn(OwnerId,
                    (clearing ? "第三分屏复活清除死亡大消息失败" : "第三分屏死亡大消息写入失败") +
                    "（本拍不写，下一拍重试）：" + reason +
                    " · 死亡大消息控件=" + DescribeDeathMessagePlacement());
            }
        }

        /// <summary>
        /// 把 H3 的死亡大消息控件（<c>Gui/LargeMessage</c>）在绑定期间**移出 <c>Gui</c> 根**、挂到
        /// <c>GameWidget</c> 下（与 <c>View</c> / <c>Gui</c> 平级，Source: Pak/Widgets/GameWidget.xml:3-5）。
        ///
        /// 根因（2026-10-20 主机日志坐实）：
        ///   ① 客户端角色死亡 → 客户端把自己的相机换成 <c>DeathCamera</c>
        ///      （<c>Engine/PlayerVitalsReplica.cs:914</c>），而 <c>DeathCamera.IsEntityControlEnabled = false</c>；
        ///   ② 客户端引擎的 <c>ComponentGui.UpdateWidgets</c> 于是把 <c>ControlsContainer.IsVisible</c>
        ///      置 false（Source: Survivalcraft/Game/ComponentGui.cs:394-395），客户端把 HUD 显隐上报；
        ///   ③ 主机侧 UI 镜像把这个显隐写到**同一个镜像视图**的 <c>GameWidget.GuiWidget.IsVisible</c>
        ///      （<c>Engine/UiMirrorEngine.cs:271</c>，日志 <c>[SuMP][ui-state] … hudHidden=1</c>）；
        ///   ④ 死亡大消息是 <c>Gui</c> 的子控件（Pak/Widgets/GameWidget.xml:88-96）→
        ///      **消息写进去了却画不出来**（实测 <c>viewDeathOverlay=1</c>、屏上没有文字）。
        ///
        /// 移到 <c>GameWidget</c> 根下后不受 <c>Gui.IsVisible</c> 影响；显示 / 淡入 / 文字仍由引擎
        /// <c>ComponentGui.UpdateWidgets</c> 驱动（Source: ComponentGui.cs:373-390），外观与引擎同款。
        /// 解绑按原索引移回（<see cref="RestoreDeathMessage"/>）。定位失败 = 明确计数 + 告警
        /// （<c>viewDeathTextMissing</c>），不静默。
        /// </summary>
        private void RelocateDeathMessage(ComponentPlayer viewOwner)
        {
            m_deathMessageRelocated = false;
            m_deathMessageWidget = null;
            m_deathMessageOriginParent = null;
            m_deathMessageOriginIndex = -1;
            GameWidget gameWidget = viewOwner == null ? null : viewOwner.GameWidget;
            if (gameWidget == null)
            {
                NoteDeathTextProblem("view-game-widget-missing",
                    "第三分屏死亡大消息：视图所有者的 GameWidget 不存在 → 死亡文字无法移出 HUD 根，" +
                    "客户端死亡时文字会被 HUD 显隐一起藏掉");
                return;
            }
            ContainerWidget gui = gameWidget.GuiWidget;
            if (gui == null)
            {
                NoteDeathTextProblem("view-gui-missing",
                    "第三分屏死亡大消息：视图的 GameWidget 没有 Gui 根 → 死亡文字无法移出 HUD 根，" +
                    "客户端死亡时文字会被 HUD 显隐一起藏掉");
                return;
            }
            ContainerWidget message = null;
            int index = -1;
            for (int i = 0; i < gui.Children.Count; i++)
            {
                Widget child = gui.Children[i];
                if (child is ContainerWidget container &&
                    string.Equals(child.Name, "LargeMessage", StringComparison.Ordinal))
                {
                    message = container;
                    index = i;
                    break;
                }
            }
            if (message == null)
            {
                NoteDeathTextProblem("large-message-widget-missing",
                    "第三分屏死亡大消息：Gui 下没有 LargeMessage 控件（引擎 XML 改版，应为 " +
                    "Pak/Widgets/GameWidget.xml:88 的 Gui/LargeMessage）→ 死亡文字无法移出 HUD 根");
                return;
            }
            try
            {
                gui.Children.Remove(message);
                gameWidget.Children.Add(message);
                m_deathMessageWidget = message;
                m_deathMessageOriginParent = gui;
                m_deathMessageOriginIndex = index;
                m_deathMessageRelocated = true;
                Log("第三分屏死亡大消息控件已移出 HUD 根：Gui/LargeMessage → GameWidget#" +
                    gameWidget.GameWidgetIndex + " 末尾（原索引 " + index +
                    "）；客户端死亡时文字不再随 HUD 显隐一起消失（解绑按原索引移回）");
            }
            catch (Exception e)
            {
                // 移出失败要把控件放回原位（不放回会从 HUD 树上脱落）。
                try
                {
                    if (message.ParentWidget is ContainerWidget current)
                        current.Children.Remove(message);
                    if (!gui.Children.Contains(message))
                    {
                        int restoreIndex = index < 0 || index > gui.Children.Count
                            ? gui.Children.Count
                            : index;
                        gui.Children.Insert(restoreIndex, message);
                    }
                }
                catch (Exception rollbackError)
                {
                    m_diagnostics?.Error(OwnerId,
                        "第三分屏死亡大消息：移出 Gui 失败后放回也失败（控件可能已从 HUD 树上脱落）：" +
                        rollbackError.GetType().Name + " " + rollbackError.Message, rollbackError);
                }
                NoteDeathTextProblem("relocate: " + e.GetType().Name + " " + e.Message,
                    "第三分屏死亡大消息：把 Gui/LargeMessage 移到 GameWidget 根下失败 → 死亡文字会被 " +
                    "HUD 显隐一起藏掉：" + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 解绑：把死亡大消息控件按原索引移回 <c>Gui</c>（幂等；没移过 / 控件已不可用 = 什么都不做）。
        /// </summary>
        private void RestoreDeathMessage()
        {
            ContainerWidget message = m_deathMessageWidget;
            ContainerWidget origin = m_deathMessageOriginParent;
            int index = m_deathMessageOriginIndex;
            bool relocated = m_deathMessageRelocated;
            m_deathMessageRelocated = false;
            m_deathMessageWidget = null;
            m_deathMessageOriginParent = null;
            m_deathMessageOriginIndex = -1;
            if (!relocated || message == null) return;
            try
            {
                if (message.ParentWidget is ContainerWidget current)
                    current.Children.Remove(message);
                if (origin == null) return;
                if (origin.Children.Contains(message)) return;
                int restoreIndex = index < 0 || index > origin.Children.Count
                    ? origin.Children.Count
                    : index;
                origin.Children.Insert(restoreIndex, message);
            }
            catch (Exception e)
            {
                m_counters.NoteDeathTextFailures("restore: " + e.GetType().Name + " " + e.Message);
                m_diagnostics?.Warn(OwnerId,
                    "解绑时把死亡大消息控件移回 Gui 根失败（文字控件可能停在 GameWidget 根下）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>死亡大消息控件当前在哪（日志 / 状态行用）。</summary>
        private string DescribeDeathMessagePlacement()
        {
            if (m_deathMessageRelocated)
                return "已移出 Gui 根（不受 HUD 显隐影响，原索引 " + m_deathMessageOriginIndex + "）";
            return "仍在 Gui 根内（" + (m_counters.LastDeathTextProblem ?? "未定位") +
                "）→ 客户端死亡时文字会被 HUD 显隐一起藏掉";
        }

        /// <summary>定位 / 移出死亡大消息控件失败：计数 + 每个原因只告警一次。</summary>
        private void NoteDeathTextProblem(string reason, string message)
        {
            m_counters.NoteDeathTextMissing(reason);
            WarnOnce("death-text:" + reason, message);
        }

        /// <summary>当前实时时间（与引擎 <c>ComponentGui</c> 用的是同一个 <c>Time.RealTime</c>）。</summary>
        private double CurrentTime() => m_clock == null ? 0.0 : m_clock();
        private void WriteHealthBar(float value)
        {
            ValueBarWidget bar = m_gui.HealthBarWidget;
            if (bar == null)
            {
                m_counters.NoteFailure("health-bar-missing");
                return;
            }
            bar.Value = value;
            m_counters.NoteHudNumber();
        }

        private void WriteFoodBar(float value)
        {
            ValueBarWidget bar = m_gui.FoodBarWidget;
            if (bar == null)
            {
                m_counters.NoteFailure("food-bar-missing");
                return;
            }
            bar.Value = value;
            m_counters.NoteHudNumber();
        }

        private void WriteTemperature(int textureIndex, string textureName)
        {
            ValueBarWidget bar = m_gui.TemperatureBarWidget;
            if (bar == null)
            {
                m_counters.NoteFailure("temperature-bar-missing");
                return;
            }
            if (m_cachedTemperatureIndex != textureIndex)
            {
                Subtexture texture;
                try
                {
                    texture = textureName == null
                        ? null
                        : ContentManager.Get<Subtexture>(textureName);
                }
                catch (Exception e)
                {
                    m_counters.NoteFailure("temperature-texture: " + e.GetType().Name + " " +
                        e.Message);
                    WarnOnce("temperature-texture:" + textureName,
                        "读取温度贴图 " + (textureName ?? "(null)") + " 抛异常：" +
                        e.GetType().Name + " " + e.Message + " → 保留上一张");
                    return;
                }
                if (texture == null)
                {
                    m_counters.NoteFailure("temperature-texture-missing:" + textureName);
                    WarnOnce("temperature-texture-missing:" + textureName,
                        "温度贴图 " + (textureName ?? "(null)") + " 不存在 → 保留上一张");
                    return;
                }
                m_cachedTemperatureIndex = textureIndex;
                m_cachedTemperatureTexture = texture;
            }
            bar.BarSubtexture = m_cachedTemperatureTexture;
            // 温度条不是进度条：XML 里 Value 固定为 1（PlayerVitalsPresentation.TemperatureBarValue）。
            bar.Value = PlayerVitalsPresentation.TemperatureBarValue;
            m_counters.NoteHudNumber();
        }

        private void WriteLevelLabel(int level)
        {
            LabelWidget label = m_gui.LevelLabelWidget;
            if (label == null)
            {
                m_counters.NoteFailure("level-label-missing");
                return;
            }
            label.Text = MirrorViewFeedbackMath.LevelText(level);
            m_counters.NoteHudNumber();
        }

        // ---------------------------------------------------------------- 快照 / 日志

        /// <summary>
        /// 抓 H3 的**显示值**（不是组件字段）：解绑时写回，避免第三分屏停在客户端最后一次的值上。
        /// 血条 / 食物条 / 温度档位引擎下一拍会自己重写，等级文字是边沿触发（ComponentLevel.cs:334-338）
        /// —— 所以等级文字必须由我们写回，否则会一直停在客户端的等级上。
        /// </summary>
        private void CaptureSavedHud()
        {
            m_savedHud = false;
            if (m_gui == null) return;
            m_savedHealthBarValue = m_gui.HealthBarWidget == null
                ? 0f
                : m_gui.HealthBarWidget.Value;
            m_savedFoodBarValue = m_gui.FoodBarWidget == null
                ? 0f
                : m_gui.FoodBarWidget.Value;
            m_savedTemperatureTexture = m_gui.TemperatureBarWidget == null
                ? null
                : m_gui.TemperatureBarWidget.BarSubtexture;
            m_savedTemperatureBarValue = m_gui.TemperatureBarWidget == null
                ? PlayerVitalsPresentation.TemperatureBarValue
                : m_gui.TemperatureBarWidget.Value;
            m_savedLevelText = m_gui.LevelLabelWidget == null
                ? null
                : m_gui.LevelLabelWidget.Text;
            m_savedHud = true;
        }

        private string DescribeWidgets()
        {
            if (m_gui == null) return "none";
            return "health=" + (m_gui.HealthBarWidget != null ? "1" : "0") +
                " food=" + (m_gui.FoodBarWidget != null ? "1" : "0") +
                " temperature=" + (m_gui.TemperatureBarWidget != null ? "1" : "0") +
                " level=" + (m_gui.LevelLabelWidget != null ? "1" : "0");
        }

        /// <summary>10 秒限流的计数日志（需求：计数器每 10 秒一行）。</summary>
        private void LogRateLimited()
        {
            if (m_diagnostics == null || m_clock == null) return;
            double now = m_clock();
            if (now - m_lastLogTime < LogIntervalSeconds) return;
            m_lastLogTime = now;
            Log("第三分屏客户端反馈镜像（10 秒）：" + Describe());
        }

        private void Log(string message)
        {
            m_diagnostics?.Info(OwnerId, message);
        }

        private void WarnOnce(string key, string message)
        {
            if (m_diagnostics == null) return;
            if (!m_reportedOnce.Add(key)) return;
            m_diagnostics.Warn(OwnerId, message);
        }

        /// <summary>
        /// 问题日志的 **10 秒限流**（需求：失败 10 秒限流）。同一问题键 10 秒内只写一行，
        /// 之后只在计数继续增长时再写下一行 —— 不静默、也不刷屏。
        /// </summary>
        private void NoteProblem(string key, string message)
        {
            if (m_diagnostics == null || m_clock == null) return;
            double now = m_clock();
            if (m_problemLogTime.TryGetValue(key, out double last) &&
                now - last < LogIntervalSeconds)
                return;
            m_problemLogTime[key] = now;
            m_diagnostics.Warn(OwnerId, "[10s] " + message);
        }
    }
}

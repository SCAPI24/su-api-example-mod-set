using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuAPI;
using SuMultiplayer.Modules;

namespace SuMultiplayer.Engine
{
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
    ///     死亡表现 = 黑屏 + "YOU HAVE DIED" 大消息（与引擎 <c>PlayerData.PlayerDead</c> 同文本 / 同时长）。
    ///   · 解绑（离开房间 / 世界卸载 / 客户端离开 / 回滚）时把绑定前抓到的 HUD 显示值写回、
    ///     清掉我们设置的大消息与覆盖层，并停止更新（<see cref="Release"/>）。
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
        private readonly HashSet<string> m_reportedOnce =
            new HashSet<string>(StringComparer.Ordinal);

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
        private bool m_wroteIce;
        private bool m_showedDeathMessage;

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
            m_wroteIce = false;
            m_showedDeathMessage = false;
            m_cachedTemperatureIndex = int.MinValue;
            m_cachedTemperatureTexture = null;
            m_diff.Reset();
            CaptureSavedHud();
            m_linked = true;
            m_lastLogTime = m_clock == null ? 0.0 : m_clock();
            Log("第三分屏客户端反馈镜像已接入：视图所有者「" + m_viewOwnerName + "」← 客户端 " +
                m_clientId + "「" + m_clientName + "」· 控件=" + DescribeWidgets() +
                " · 只写显示层（血条 / 食物条 / 温度档位 / 等级文字 / 红屏 / 黑屏 / 大消息）；" +
                "不写 H3 的生命 / 体征 / 背包，不切相机");
            return true;
        }

        /// <summary>
        /// 每拍驱动（replace 绑定期间由 <c>MirrorCharacterEngine.ValidateBound</c> 调用）。
        /// 只读客户端化身的组件；把计划里的显示值写进 H3 的控件。异常 / 控件缺失一律计数 + 限流告警。
        /// </summary>
        public void Tick(ComponentPlayer viewOwner, ComponentPlayer source, string clientName,
            float dt)
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
                targetTemperature: targetTemperature, level: sourceData.Level);
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
                }
                if (m_overlays != null)
                {
                    // 这三个因子每拍被引擎清 0（ComponentScreenOverlays.Update:60-77），
                    // 停止更新本身就等于清除；这里对"我们自己写过的"再显式写 0，
                    // 保证解绑当帧（同帧还有 Draw）不残留一次客户端画面。
                    if (m_wroteRedout) m_overlays.RedoutFactor = 0f;
                    if (m_wroteBlackout) m_overlays.BlackoutFactor = 0f;
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
            m_wroteIce = false;
            m_showedDeathMessage = false;
            m_savedHud = false;
            m_savedLevelText = null;
            m_savedTemperatureTexture = null;
            m_cachedTemperatureIndex = int.MinValue;
            m_cachedTemperatureTexture = null;
            m_clientId = -1;
            m_clientName = string.Empty;
            m_viewOwnerName = string.Empty;
            m_diff.Reset();
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
            m_counters.Describe() + " diff{" + m_diff.Describe() + "}";

        // ---------------------------------------------------------------- 写显示层

        private void Apply(in MirrorViewFramePlan plan)
        {
            MirrorViewHudWrites hud = plan.Hud;
            MirrorViewDamageStep damage = plan.Damage;
            MirrorViewDeathStep death = plan.Death;
            try
            {
                if (death.ClearOverlay)
                {
                    // 复活：黑屏因子由引擎每拍清 0，这里只清我们自己写的大消息。
                    if (m_showedDeathMessage)
                    {
                        m_gui.DisplayLargeMessage(string.Empty, string.Empty, 0f, 0f);
                        m_showedDeathMessage = false;
                    }
                    m_counters.NoteRespawnClear();
                }
                if (death.KeepBlackout)
                {
                    m_overlays.BlackoutFactor = 1f;
                    m_wroteBlackout = true;
                }
                if (death.ShowOverlay)
                {
                    m_gui.DisplayLargeMessage(MirrorViewDeathText.LargeText, death.SmallText,
                        MirrorViewDeathText.DurationSeconds, MirrorViewDeathText.DelaySeconds);
                    m_showedDeathMessage = true;
                    m_counters.NoteDeathOverlay();
                    Log("客户端 " + m_clientId + "「" + m_clientName + "」死亡 → 第三分屏显示黑屏 + \"" +
                        MirrorViewDeathText.LargeText + "\"（死因：" +
                        (death.Cause ?? "Unknown") + "）；复活后自动清除");
                }
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
                if (hud.WriteIce)
                {
                    m_overlays.IceFactor = hud.IceFactor;
                    m_wroteIce = true;
                    m_counters.NoteIceWrite();
                }
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
    }
}

using Engine;
using Game;
using System;

namespace ScMultiplayer
{
    /// <summary>
    /// `ComponentVitalStats` 的联机替换：**主机算、客户端播**（本地不做任何预测/模拟）。
    ///
    /// 引擎里饥饿 / 疲劳 / 喘气 / 溺水 / 潮湿 / 温度（含"冻结前"那一串提示）、进食反馈、
    /// 温度条闪烁、呻吟声，全部写在 `ComponentVitalStats.UpdateFood/UpdateStamina/
    /// UpdateSleep/UpdateTemperature/UpdateWetness`（ComponentVitalStats.cs:275-694）里。
    /// 客户端原本跳过 `base.Update`，等于把这些表现整批丢掉 —— 实测"冻结前的提示没有、
    /// 声音也没有"。
    ///
    /// 现在的分工：
    ///   · **主机**照常跑原生更新（无头端没有屏幕，但它显示的提示会被下面读出来），
    ///     每出现一条新提示就把它编成 `HintSequence` + `HintText` 随状态快照发给该客户端。
    ///   · **客户端**不跑模拟，只在收到新序号时把这条提示**播**出来（含提示音）；
    ///     HUD 数值仍由主机周期快照纠正（`ApplyAuthoritativePlayerStats`，每秒一次）。
    ///
    /// ⚠️ `ApplyAuthoritativePlayerStats` 里**不能**写 `m_lastFood` / `m_lastStamina` /
    /// `m_lastSleep` / `m_lastTemperature` / `m_lastWetness`：那五个是主机侧"跨越阈值才提示一次"
    /// 的边沿判据（ComponentVitalStats.cs:312/374/445/546/651），写平了主机自己就不再产生提示。
    /// </summary>
    public class SuComponentVitalStats : ComponentVitalStats, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;
        private float m_authoritativeTargetTemperature;
        private int m_temperatureTextureIndex = -1;
        private LabelWidget m_lastHostHintLabel;
        private int m_hostHintSequence;
        private int m_lastAuthoritativeHintSequence;
        private string m_hostHintText = string.Empty;
        // 客户端**自己**刚播过的提示（用来对主机转发来的同一条去重）。
        // 未被替换的组件（`ComponentLocomotion` 的 "Running barefoot is slow, wear shoes"、
        // `ComponentClothing` 的 "Your clothing has worn out" 等）两端都会产生同一条提示：
        // 客户端本地已经播过一次，主机的提示又被读出来转发一次 —— 不去重就会出现两个来源。
        private LabelWidget m_lastOwnHintLabel;
        private string m_lastOwnHintText = string.Empty;
        private double m_lastOwnHintTime;
        private const double OwnHintDedupeWindow = 3.0;

        /// <summary>主机侧提示事件序号（随状态快照发出去）。</summary>
        public int HintSequence => m_hostHintSequence;

        /// <summary>主机侧最新一条提示文本。</summary>
        public string HintText => m_hostHintText;

        protected override void Load(TemplatesDatabase.ValuesDictionary valuesDictionary,
            GameEntitySystem.IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(true);
            m_authoritativeTargetTemperature = Temperature;
        }

        internal void ApplyAuthoritativeTargetTemperature(float targetTemperature)
        {
            m_authoritativeTargetTemperature = targetTemperature;
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
            {
                base.Update(dt);
                CaptureHostHint();
                return;
            }

            // 客户端：不跑模拟（数值由主机每秒纠正），只刷新 HUD 表现。
            ComponentGui gui = m_componentPlayer?.ComponentGui;
            if (gui?.FoodBarWidget != null)
                gui.FoodBarWidget.Value = Food;
            if (m_componentPlayer?.ComponentScreenOverlays != null)
            {
                m_componentPlayer.ComponentScreenOverlays.IceFactor =
                    MathUtils.Saturate(1f - Temperature / 6f);
            }
            UpdateTemperatureTexture(gui);
            CaptureOwnHint();
        }

        // Source: ScMultiplayer.HandleGamePlayerHealthMessage
        // 客户端播放主机发来的提示（含提示音）。序号幂等：漏收不补播，重复收不重播。
        internal void ApplyAuthoritativeHint(int sequence, string text)
        {
            if (sequence <= 0 || sequence == m_lastAuthoritativeHintSequence) return;
            m_lastAuthoritativeHintSequence = sequence;
            if (string.IsNullOrEmpty(text)) return;
            // 去重：客户端本地（未被替换的组件）刚播过同一条，就不要再播第二遍 ——
            // 实测 "Running barefoot is slow, wear shoes" 会因此出现两个提示来源。
            if (string.Equals(text, m_lastOwnHintText, StringComparison.Ordinal) &&
                Time.RealTime - m_lastOwnHintTime < OwnHintDedupeWindow)
            {
                return;
            }
            m_componentPlayer?.ComponentGui?.DisplaySmallMessage(
                text, Color.White, blinking: true, playNotificationSound: true);
        }

        // Source: Survivalcraft/Game/MessageWidget.cs:MessageWidget.DisplayMessage
        // 客户端侧只做观察：记住自己刚播出来的那条提示（最新 LabelWidget 实例变化即为新的一条），
        // 供 `ApplyAuthoritativeHint` 去重用。不改变任何显示行为。
        private void CaptureOwnHint()
        {
            ComponentGui gui = m_componentPlayer?.ComponentGui;
            if (gui == null) return;
            MessageWidget widget = ScMultiplayer.ModManager.ModParentField.GetParentField(
                gui, "m_messageWidget", typeof(ComponentGui)) as MessageWidget;
            if (widget == null) return;
            LabelWidget newest = null;
            foreach (Widget child in widget.Children)
            {
                if (child is LabelWidget label && !string.IsNullOrEmpty(label.Text))
                    newest = label;
            }
            if (newest == null || ReferenceEquals(newest, m_lastOwnHintLabel)) return;
            m_lastOwnHintLabel = newest;
            m_lastOwnHintText = newest.Text;
            m_lastOwnHintTime = Time.RealTime;
        }

        // Source: Survivalcraft/Game/MessageWidget.cs:MessageWidget.DisplayMessage
        // 主机刚显示的提示就是小消息控件里最新的那个 LabelWidget（AddMessage 用 Children.Add 追加）。
        // 用 LabelWidget 的实例变化识别"新的一条" —— 同一条文字再次出现也算新的一次。
        //
        // ⚠️ 有些提示**不转发**：由本地计时器节流的那种，两端各有一套自己的计时器
        // （典型：`ComponentLocomotion` 的 "Running barefoot is slow, wear shoes"，
        // `m_shoesWarningTime` 每 300 秒才允许播一次，见 ComponentLocomotion.cs:486-498）。
        // 按约定这种用**客户端自己那份** —— 它的计时器就在组件实例里、节流天然正确；
        // 主机这份再转发过去就变成两个来源。这里按文本排除。
        private static readonly string[] ClientOwnedHintTexts =
        {
            "Running barefoot is slow, wear shoes"
        };

        private void CaptureHostHint()
        {
            ComponentGui gui = m_componentPlayer?.ComponentGui;
            if (gui == null) return;
            // 非泛型 getter（返回 object）才是 null 安全的：泛型重载内部只做 `value is T`，
            // 字段为 null 时会抛 "Member ... is not of type ..."。
            MessageWidget widget = ScMultiplayer.ModManager.ModParentField.GetParentField(
                gui, "m_messageWidget", typeof(ComponentGui)) as MessageWidget;
            if (widget == null) return;
            LabelWidget newest = null;
            foreach (Widget child in widget.Children)
            {
                if (child is LabelWidget label && !string.IsNullOrEmpty(label.Text))
                    newest = label;
            }
            if (newest == null || ReferenceEquals(newest, m_lastHostHintLabel)) return;
            m_lastHostHintLabel = newest;
            string text = newest.Text;
            foreach (string clientOwned in ClientOwnedHintTexts)
            {
                if (string.Equals(text, clientOwned, StringComparison.Ordinal))
                    return;
            }
            m_hostHintSequence = m_hostHintSequence == int.MaxValue ? 1 : m_hostHintSequence + 1;
            m_hostHintText = text.Length <= 160 ? text : text.Substring(0, 160);
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.UpdateTemperature
        private void UpdateTemperatureTexture(ComponentGui gui)
        {
            if (gui?.TemperatureBarWidget == null) return;
            int textureIndex = m_authoritativeTargetTemperature > 22f ? 6
                : m_authoritativeTargetTemperature > 18f ? 5
                : m_authoritativeTargetTemperature > 14f ? 4
                : m_authoritativeTargetTemperature > 10f ? 3
                : m_authoritativeTargetTemperature > 6f ? 2
                : m_authoritativeTargetTemperature > 2f ? 1
                : 0;
            if (textureIndex == m_temperatureTextureIndex) return;
            m_temperatureTextureIndex = textureIndex;
            gui.TemperatureBarWidget.BarSubtexture = ContentManager.Get<Subtexture>(
                $"Textures/Atlas/Temperature{textureIndex}");
        }
    }
}

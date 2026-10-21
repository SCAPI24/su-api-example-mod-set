using System;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 体征 HUD 的**纯映射**：主机权威值 → 引擎显示属性（不引用 Game，自检可直接编译）。
    ///
    /// 引擎事实（客户端本机 <c>ComponentVitalStats.Update</c> 被
    /// <see cref="PlayerVitalsReplica.DisableLocalSimulation"/> 从 <c>SubsystemUpdate</c> 摘掉以后，
    /// 下面这些显示属性不会再有人写）：
    ///   · 温度条 = <c>ComponentGui.TemperatureBarWidget</c>（<c>Pak/Widgets/GameWidget.xml:52</c>）：
    ///     XML 里 <c>BarsCount=1</c> / <c>Value=1</c>，它会变的只有 <c>BarSubtexture</c> ——
    ///     按 <c>m_targetTemperature</c> 选 Temperature0..6 七档图标（阈值 22/18/14/10/6/2，
    ///     全部是**严格大于**）。它**不是**数值进度条，<c>Value</c> 固定为 1；
    ///   · <c>BarSubtexture</c> 只在 <c>ComponentVitalStats.UpdateTemperature</c> 末尾写
    ///     （<c>ComponentVitalStats.cs:589-616</c>）；不写 = 保持 null，ValueBarWidget.Draw
    ///     退回 FlatBatch 画一个纯色矩形（<c>ValueBarWidget.cs:154-161</c>），即"温度条没渲染好"；
    ///   · 冰冻屏幕覆盖 <c>ComponentScreenOverlays.IceFactor</c> 只在
    ///     <c>ComponentVitalStats.UpdateTemperature:579</c> 写；而
    ///     <c>ComponentScreenOverlays.Update:68-73</c> 每帧把它清 0（更新序 Reset，
    ///     早于 Default），所以客户端必须**每帧**重写，不能只在收到快照时写一次；
    ///   · 血条 / 食物条的值分别由 <c>ComponentHealth.Update:265</c> /
    ///     <c>ComponentVitalStats.UpdateFood:345</c> 写，同样是摘掉 Update 后需要显示层补写。
    ///
    /// Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.UpdateTemperature
    /// Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Load:249 / UpdateWidgets:403
    /// Source: Survivalcraft/Game/ComponentScreenOverlays.cs:ComponentScreenOverlays.Update:68-73
    /// Source: Survivalcraft/Game/ValueBarWidget.cs:ValueBarWidget.Draw:154-161
    /// Source: Pak/Widgets/GameWidget.xml:52
    /// </summary>
    public static class PlayerVitalsPresentation
    {
        /// <summary>引擎温度贴图档位数（Temperature0..Temperature6）。</summary>
        public const int TemperatureTextureCount = 7;

        /// <summary>温度条在 XML 里的固定 Value（它不是进度条）。</summary>
        public const float TemperatureBarValue = 1f;

        public static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// 目标体温 → 温度条贴图档位（0..6）。语义与引擎
        /// <c>ComponentVitalStats.UpdateTemperature:589-616</c> 完全一致（严格大于）。
        /// 非有限值返回 -1（调用方必须明确失败，不得拿 0 档顶替）。
        /// </summary>
        public static int TemperatureTextureIndex(float targetTemperature)
        {
            if (!IsFinite(targetTemperature)) return -1;
            if (targetTemperature > 22f) return 6;
            if (targetTemperature > 18f) return 5;
            if (targetTemperature > 14f) return 4;
            if (targetTemperature > 10f) return 3;
            if (targetTemperature > 6f) return 2;
            if (targetTemperature > 2f) return 1;
            return 0;
        }

        /// <summary>档位 → 引擎贴图名；越界返回 null（调用方明确失败）。</summary>
        public static string TemperatureTextureName(int textureIndex) =>
            textureIndex >= 0 && textureIndex < TemperatureTextureCount
                ? "Textures/Atlas/Temperature" + textureIndex
                : null;

        /// <summary>
        /// 冰冻覆盖：与 <c>ComponentVitalStats.UpdateTemperature:579</c> 相同的派生
        /// （<c>Saturate(1 - Temperature / 6)</c>）。非有限值返回 false。
        /// </summary>
        public static bool TryIceFactor(float temperature, out float iceFactor)
        {
            iceFactor = 0f;
            if (!IsFinite(temperature)) return false;
            iceFactor = MathUtils.Saturate(1f - temperature / 6f);
            return true;
        }

        /// <summary>
        /// 把一份主机权威体征折算成 HUD 需要的全部显示值。任何输入不是有限数都返回 false
        /// （调用方保留上一次有效显示并计数，绝不写 NaN / 不拿默认值顶替）。
        /// </summary>
        public static bool TryCreate(float health, float food, float temperature,
            float targetTemperature, bool dead, out PlayerVitalsHudValues values)
        {
            values = default;
            if (!IsFinite(health) || !IsFinite(food) || !IsFinite(temperature) ||
                !IsFinite(targetTemperature))
                return false;
            int textureIndex = TemperatureTextureIndex(targetTemperature);
            if (textureIndex < 0) return false;
            if (!TryIceFactor(temperature, out float iceFactor)) return false;
            values = new PlayerVitalsHudValues(
                dead ? 0f : MathUtils.Saturate(health),
                MathUtils.Saturate(food),
                TemperatureBarValue,
                textureIndex,
                iceFactor);
            return true;
        }

        /// <summary>
        /// 与 <see cref="TryCreate"/> 相同，但冰冻覆盖**直接使用主机权威冻结进度**
        /// （vitals.proto 字段 22，见 <c>PlayerFreezePolicy</c>），不再从客户端手里的温度推导。
        /// 客户端每帧再把这个目标值插值后写进 <c>IceFactor</c>（<see cref="PlayerPresentationInterpolator"/>）。
        /// 非法输入返回 false（调用方保留上一次有效显示并计数，绝不写 NaN）。
        /// </summary>
        public static bool TryCreateWithFreeze(float health, float food, float temperature,
            float targetTemperature, float freezeProgress, bool dead,
            out PlayerVitalsHudValues values)
        {
            values = default;
            if (!IsFinite(health) || !IsFinite(food) || !IsFinite(temperature) ||
                !IsFinite(targetTemperature) || !IsFinite(freezeProgress))
                return false;
            if (freezeProgress < 0f || freezeProgress > 1f) return false;
            int textureIndex = TemperatureTextureIndex(targetTemperature);
            if (textureIndex < 0) return false;
            values = new PlayerVitalsHudValues(
                dead ? 0f : MathUtils.Saturate(health),
                MathUtils.Saturate(food),
                TemperatureBarValue,
                textureIndex,
                freezeProgress);
            return true;
        }
    }

    /// <summary>
    /// 引擎同款**受伤红屏因子**（客户端本机角色）。客户端本机
    /// <c>ComponentHealth.Update</c> 被摘掉后，<c>m_redScreenFactor</c> 的
    /// "衰减 + 掉血增量" 没有任何代码在跑，而 <c>ComponentScreenOverlays.Update</c>
    /// 每帧把 <c>RedoutFactor</c> 清 0 —— 只在掉血那一拍写一次 = 屏幕上只闪一帧（实机问题 3：
    /// 客户端受伤"没有红屏"）。
    ///
    /// 引擎口径（Source: <c>ComponentHealth.cs:139-147</c> / <c>:251-261</c>）：
    ///   · 每拍：<c>if (m_redScreenFactor &gt; 0.01f) m_redScreenFactor *= Pow(0.2f, dt); else 0f;</c>
    ///   · 掉血：<c>HealthChange = Health - m_lastHealth &lt; 0</c> →
    ///     <c>m_redScreenFactor += -4f * HealthChange</c>（= 4 × 掉血量）；
    ///   · 每拍：<c>RedoutFactor = Max(RedoutFactor, m_redScreenFactor)</c>。
    /// 所以客户端必须每帧衰减 + 每帧重写（本类只做纯算术，写控件在 <c>PlayerVitalsReplica</c>）。
    /// </summary>
    public static class PlayerHurtFeedback
    {
        /// <summary>红屏因子衰减到该值以下即归零（引擎 <c>ComponentHealth.Update:141-147</c>）。</summary>
        public const float RedoutCutoff = 0.01f;

        /// <summary>每 1.0 点掉血增加的红屏因子（引擎 <c>-4f * HealthChange</c>）。</summary>
        public const float RedFactorPerHealth = 4f;

        /// <summary>血条闪烁强度系数（引擎 <c>(0f - HealthChange) * 30f</c>）。</summary>
        public const float FlashStrengthPerHealth = 30f;

        /// <summary>血条闪烁上限（引擎 <c>Clamp(..., 0, 10)</c>）。</summary>
        public const int MaxHealthBarFlash = 10;

        /// <summary>一拍的衰减（与引擎同一条式子）；非有限输入按 0 处理（调用方已保证有限）。</summary>
        public static float Decay(float factor, float dt)
        {
            if (!PlayerVitalsPresentation.IsFinite(factor) ||
                !PlayerVitalsPresentation.IsFinite(dt))
                return 0f;
            if (!(factor > RedoutCutoff)) return 0f;
            return factor * MathUtils.Pow(0.2f, dt);
        }

        /// <summary>一次掉血要加的因子（掉血 &lt;= 0 返回 0；加血不闪红屏）。</summary>
        public static float AddDamage(float factor, float healthLoss)
        {
            if (!PlayerVitalsPresentation.IsFinite(factor)) factor = 0f;
            if (!(healthLoss > 0f) || !PlayerVitalsPresentation.IsFinite(healthLoss)) return factor;
            return factor + RedFactorPerHealth * healthLoss;
        }

        /// <summary>血条闪烁次数（引擎 <c>Clamp((int)(drop * 30f), 0, 10)</c>）。</summary>
        public static int FlashCount(float healthLoss)
        {
            if (!(healthLoss > 0f) || !PlayerVitalsPresentation.IsFinite(healthLoss)) return 0;
            if (healthLoss * FlashStrengthPerHealth >= MaxHealthBarFlash) return MaxHealthBarFlash;
            int count = (int)(healthLoss * FlashStrengthPerHealth);
            if (count < 0) return 0;
            return count > MaxHealthBarFlash ? MaxHealthBarFlash : count;
        }
    }

    /// <summary>
    /// 表现值的**连续插值器**（每帧按时间常数 + 最大速率逼近目标，绝不阶跃）。
    ///
    /// 为什么需要：主机快照是 5 Hz（200 ms），直接把快照值写进显示属性会让冰冻覆盖 /
    /// 连续量一顿一顿地跳变（实机问题 2）。这里用指数逼近（时间常数 <see cref="TimeConstant"/>）
    /// 再夹一个最大速率（<see cref="MaxRate"/> 单位/秒），保证：
    ///   · 单调收敛：每一拍的步长符号 = 目标差符号，且绝不过冲；
    ///   · 限速：|step| &lt;= MaxRate * dt（不会一帧从 0 跳到 1）；
    ///   · 收敛：接近到 <see cref="SnapEpsilon"/> 以内直接贴合（不会永远差一点点）；
    ///   · NaN / Inf / dt &lt; 0 → 明确返回 false + 原因，调用方计数并保留当前值；
    ///   · 第一次推进（未播种）直接取目标值（绑定 / 复活时不该从 0 淡入）。
    /// 纯逻辑、无静态可变状态；每个表现实例持有一个。
    /// </summary>
    public sealed class PlayerPresentationInterpolator
    {
        /// <summary>默认时间常数（秒）：约 0.6 秒收敛 95%（3τ）。</summary>
        public const float DefaultTimeConstant = 0.20f;

        /// <summary>默认最大速率（单位/秒）：冰冻覆盖 0→1 至少 1/3 秒。</summary>
        public const float DefaultMaxRate = 3.0f;

        /// <summary>与目标的距离小于它就直接贴合（不给引擎写"永远差 0.0001"的抖动值）。</summary>
        public const float SnapEpsilon = 0.002f;

        private float m_value;
        private bool m_primed;

        public PlayerPresentationInterpolator()
            : this(DefaultTimeConstant, DefaultMaxRate)
        {
        }

        public PlayerPresentationInterpolator(float timeConstant, float maxRate)
        {
            if (!PlayerVitalsPresentation.IsFinite(timeConstant) || timeConstant <= 0f)
                throw new ArgumentOutOfRangeException(nameof(timeConstant),
                    "时间常数必须是有限正数（收到 " + timeConstant + "）");
            if (!PlayerVitalsPresentation.IsFinite(maxRate) || maxRate <= 0f)
                throw new ArgumentOutOfRangeException(nameof(maxRate),
                    "最大速率必须是有限正数（收到 " + maxRate + "）");
            TimeConstant = timeConstant;
            MaxRate = maxRate;
        }

        public float TimeConstant { get; }

        public float MaxRate { get; }

        /// <summary>当前表现值（未播种时 = 0）。</summary>
        public float Value => m_value;

        /// <summary>是否已经有有效值（第一次 <see cref="TryAdvance"/> 会直接播种）。</summary>
        public bool Primed => m_primed;

        /// <summary>解绑 / 换实体：下次推进重新播种（不从上一次的值淡入）。</summary>
        public void Reset()
        {
            m_value = 0f;
            m_primed = false;
        }

        /// <summary>直接贴合到某个值（初始化 / 强制同步用）；非有限值明确失败。</summary>
        public bool TrySetImmediate(float value, out string error)
        {
            error = null;
            if (!PlayerVitalsPresentation.IsFinite(value))
            {
                error = "目标值不是有限数（" + value + "）";
                return false;
            }
            m_value = value;
            m_primed = true;
            return true;
        }

        /// <summary>
        /// 推进一拍。返回 false = 输入非法（<paramref name="error"/> 给出原因，<paramref name="value"/>
        /// 保留当前值）；返回 true = <paramref name="value"/> 是本拍要写进显示属性的值。
        /// </summary>
        public bool TryAdvance(float target, float dt, out float value, out string error)
        {
            value = m_value;
            error = null;
            if (!PlayerVitalsPresentation.IsFinite(target))
            {
                error = "目标值不是有限数（" + target + "）";
                return false;
            }
            if (!PlayerVitalsPresentation.IsFinite(dt) || dt < 0f)
            {
                error = "帧时长非法（" + dt + "）";
                return false;
            }
            if (!m_primed || !PlayerVitalsPresentation.IsFinite(m_value))
            {
                // 第一次推进 / 内部值损坏：直接播种到目标（不产生一次假的淡入）。
                m_value = target;
                m_primed = true;
                value = m_value;
                return true;
            }
            float delta = target - m_value;
            if (Math.Abs(delta) <= SnapEpsilon)
            {
                m_value = target;
                value = m_value;
                return true;
            }
            if (dt <= 0f)
            {
                value = m_value;
                return true;
            }
            float alpha = 1f - MathUtils.Exp(-dt / TimeConstant);
            float step = delta * alpha;
            float maxStep = MaxRate * dt;
            if (step > maxStep) step = maxStep;
            else if (step < -maxStep) step = -maxStep;
            // 绝不过冲（指数 + 限速都不会，但显式夹一次让"单调收敛"成为不变量）。
            if (delta > 0f)
            {
                if (step <= 0f) step = Math.Min(delta, maxStep);
                if (step > delta) step = delta;
            }
            else
            {
                if (step >= 0f) step = Math.Max(delta, -maxStep);
                if (step < delta) step = delta;
            }
            m_value += step;
            value = m_value;
            return true;
        }

        public string Describe() =>
            "value=" + m_value.ToString("F4") + (m_primed ? "" : "(unprimed)") +
            " tau=" + TimeConstant.ToString("0.##") + " maxRate=" + MaxRate.ToString("0.##");
    }

    /// <summary>
    /// 一次 HUD 显示值（纯值类型）。只由 <see cref="PlayerVitalsPresentation.TryCreate"/> 构造，
    /// 构造成功即保证每个字段都可直接写进引擎控件。
    /// </summary>
    public readonly struct PlayerVitalsHudValues
    {
        public PlayerVitalsHudValues(float healthBarValue, float foodBarValue,
            float temperatureBarValue, int temperatureTextureIndex, float iceFactor)
        {
            HealthBarValue = healthBarValue;
            FoodBarValue = foodBarValue;
            TemperatureBarValue = temperatureBarValue;
            TemperatureTextureIndex = temperatureTextureIndex;
            IceFactor = iceFactor;
        }

        /// <summary>血条值（死亡 = 0，否则引擎 0..1）。</summary>
        public float HealthBarValue { get; }

        /// <summary>食物条值（引擎 0..1）。</summary>
        public float FoodBarValue { get; }

        /// <summary>温度条固定值（= 1，见 <see cref="PlayerVitalsPresentation.TemperatureBarValue"/>）。</summary>
        public float TemperatureBarValue { get; }

        /// <summary>温度条贴图档位（0..6，见 <see cref="PlayerVitalsPresentation.TemperatureTextureIndex"/>）。</summary>
        public int TemperatureTextureIndex { get; }

        /// <summary>冰冻覆盖强度（0..1）。</summary>
        public float IceFactor { get; }

        /// <summary>温度条贴图名；档位由构造保证合法。</summary>
        public string TemperatureTextureName =>
            PlayerVitalsPresentation.TemperatureTextureName(TemperatureTextureIndex);

        /// <summary>可审计的一行（状态行 / 日志用）。</summary>
        public string Describe() =>
            "hud{health=" + HealthBarValue.ToString("F3") + " food=" + FoodBarValue.ToString("F3") +
            " temperatureBar=" + TemperatureBarValue.ToString("F2") + " tempTexture=" +
            TemperatureTextureIndex + " ice=" + IceFactor.ToString("F3") + "}";
    }
}

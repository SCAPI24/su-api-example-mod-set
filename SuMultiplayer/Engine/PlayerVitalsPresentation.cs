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

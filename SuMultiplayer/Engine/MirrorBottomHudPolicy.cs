using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 第三分屏**底部 HUD 条**（血条 / 食物条 / 温度条）的显示口径（**纯逻辑，不依赖 Game**）。
    ///
    /// 根因（2026-10-21 主机日志 + 引擎源码坐实）：第三屏的宿主是自动镜像宿主 <c>Mirror#N</c>；
    /// replace 绑定**保留**了宿主的 <c>ComponentHealth.Update</c> / <c>ComponentVitalStats.Update</c>
    /// （<c>MirrorCharacterEngine.TryBind</c>：replace 不摘这两个 updateable）。它们**每帧**把组件
    /// 自己的值写进底部 HUD：
    ///   · <c>ComponentHealth.Update</c> → <c>HealthBarWidget.Value = Health</c>
    ///     （Source: Survivalcraft/Game/ComponentHealth.cs:265）；
    ///   · <c>ComponentVitalStats.UpdateFood</c> → <c>FoodBarWidget.Value = Food</c>
    ///     （Source: Survivalcraft/Game/ComponentVitalStats.cs:345）；
    ///   · <c>ComponentVitalStats.UpdateTemperature</c> → <c>TemperatureBarWidget.BarSubtexture</c>
    ///     （按 <c>m_targetTemperature</c> 分 7 档，Source: ComponentVitalStats.cs:589-616）。
    /// 而自动宿主每拍**最后**被 <c>MirrorHostImmunity</c> 把 Health / 体征钉回安全值
    /// （Health=1 · Food=1 · Temperature=12 · m_targetTemperature=12 → 温度条档 3，
    /// Source: Modules/MirrorHostImmunity.cs:MirrorHostImmunityFields）：宿主 Update 每帧把免疫值写上屏，
    /// 第三屏底部条就停在 1 / 满 / 档 3。第三分屏的客户端反馈镜像（<c>MirrorViewFeedback</c>）是**差分**写
    /// （值没变不写），只在"客户端值刚变"的那一帧写一次，下一帧又被宿主 Update 覆盖 ——
    /// 实测日志：<c>diff{healthBar=1.0000 foodBar=0.9714 tempTex=4}</c>（客户端值）对
    /// <c>Mirror#2</c> vitals=<c>food=1 / temperature=12</c>（宿主免疫值），而屏上看到的是后者。
    ///
    /// 修复口径（本类型是唯一事实来源；<c>MirrorCharacterEngine.RefreshBottomHud</c> 按它执行）：
    ///   · **数据源只有一个** = 主机侧客户端化身的权威值（与属性面板同源），**不是**宿主组件值；
    ///   · **写入目标** = 宿主自己的 <c>ComponentGui</c> 里三个 <c>ValueBarWidget</c>（第三分屏的 HUD）；
    ///   · **每帧都写**（不是 diff）：宿主 Update 每帧都会覆盖，只在变化时写等于没写；
    ///   · 宿主权威值（Health / Air / 体征）一律不写 —— 那是"宿主自己的血"，不是被镜像角色的血。
    ///
    /// 无静态可变状态：本类只有 const / 纯函数，自检可直接编译并逐条断言。
    /// </summary>
    public static class MirrorBottomHudPolicy
    {
        /// <summary>未绑定时的数据源口径（状态行 <c>viewBottomBarSource</c>）。</summary>
        public const string SourceNone = "none";

        /// <summary>绑定期间唯一的数据源：主机侧客户端化身的权威值（与属性面板同源）。</summary>
        public const string SourceClientAvatar = "client-avatar";

        /// <summary>血条控件名（Source: Pak/Widgets/GameWidget.xml，ComponentGui.Load:249）。</summary>
        public const string HealthBarWidgetName = "HealthBar";

        /// <summary>食物条控件名（ComponentGui.Load:250）。</summary>
        public const string FoodBarWidgetName = "FoodBar";

        /// <summary>温度条控件名（ComponentGui.Load:251）。</summary>
        public const string TemperatureBarWidgetName = "TemperatureBar";

        /// <summary>一帧正常写入的控件数（血条 + 食物条 + 温度条）。</summary>
        public const int WidgetCount = 3;

        /// <summary>
        /// 底部条必须**每帧**刷新（不是"值变了才写"）：宿主自己的 <c>ComponentHealth.Update</c> /
        /// <c>ComponentVitalStats.Update</c> 每帧都会把宿主（自动宿主 = 免疫安全值）写进这三个控件，
        /// 差分支路写完当帧就被覆盖。绑定期间可见值 = 本帧最后写入者，所以只能每帧写。
        /// </summary>
        public static bool RequiresContinuousRefresh => true;

        /// <summary>当前数据源：未绑定 = <see cref="SourceNone"/>；绑定 = 客户端化身。</summary>
        public static string SourceOf(bool bound) => bound ? SourceClientAvatar : SourceNone;

        /// <summary>本帧写入失败时的数据源口径（带可直接定位的失败键，不静默）。</summary>
        public static string BlockedSource(string failureKey) =>
            SourceClientAvatar + "(blocked:" +
            (string.IsNullOrEmpty(failureKey) ? "unknown" : failureKey) + ")";

        /// <summary>
        /// 状态行口径：<c>viewBottomBarWrite</c> / <c>viewBottomBarFailures</c> /
        /// <c>viewBottomBarSource</c> 三键（进现有 <c>mirror-character</c> / <c>mirror-view-feedback</c> 状态行）。
        /// </summary>
        public static string DescribeCounters(long writes, long failures, string source) =>
            "viewBottomBarWrite=" + writes + " viewBottomBarFailures=" + failures +
            " viewBottomBarSource=" + (string.IsNullOrEmpty(source) ? SourceNone : source);

        /// <summary>写入目标（日志 / 自检口径）：HealthBar / FoodBar / TemperatureBar。</summary>
        public static string DescribeTargets() =>
            HealthBarWidgetName + " / " + FoodBarWidgetName + " / " + TemperatureBarWidgetName;

        /// <summary>
        /// 客户端化身的权威值 → 三个底部条要写的显示值（复用 <see cref="PlayerVitalsPresentation"/>，
        /// 与客户端自己屏幕上的 HUD / 引擎同一份映射；死亡 = 血条 0）。
        /// 非有限值返回 false —— 调用方必须明确失败并保留上一帧显示，**不得**拿默认值顶替。
        /// </summary>
        public static bool TryCreateValues(float health, float food, float temperature,
            float targetTemperature, out PlayerVitalsHudValues values) =>
            PlayerVitalsPresentation.TryCreate(health, food, temperature, targetTemperature,
                health <= 0f, out values);
    }
}

using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 一次睡眠表现刷新的计数（引擎侧 → 模块 10 秒状态行）。
    ///
    /// 单位口径（写清楚，避免把"帧"和"睡眠期"混在一起）：
    ///   · <see cref="Applied"/> / <see cref="Zzz"/> / <see cref="Blackout"/> = **角色-帧**
    ///     （每个正在表现的槽位每帧 +1；模块累加成 <c>sleepOverlayApplied</c> 等）；
    ///   · <see cref="Forced"/> / <see cref="Legacy"/> = **睡眠期边沿**（进入强制睡眠 /
    ///     老版本主机没有睡眠权威字段的睡眠期各 +1）；
    ///   · <see cref="MirrorApplied"/> / <see cref="MirrorCleared"/> = 主机 H3 第三分屏
    ///     视图所有者的角色-帧 / 解绑清理次数；
    ///   · <see cref="Missing"/> / <see cref="Failures"/> = 本拍没找到角色 / 写入失败次数。
    /// </summary>
    public readonly struct PlayerSleepPresentationCounts
    {
        public PlayerSleepPresentationCounts(int applied, int zzz, int blackout, int forced,
            int manualWakeDisabled, int legacy, int mirrorApplied, int mirrorCleared, int missing,
            int failures)
        {
            Applied = applied;
            Zzz = zzz;
            Blackout = blackout;
            Forced = forced;
            ManualWakeDisabled = manualWakeDisabled;
            Legacy = legacy;
            MirrorApplied = mirrorApplied;
            MirrorCleared = mirrorCleared;
            Missing = missing;
            Failures = failures;
        }

        public int Applied { get; }
        public int Zzz { get; }
        public int Blackout { get; }
        public int Forced { get; }
        public int ManualWakeDisabled { get; }
        public int Legacy { get; }
        public int MirrorApplied { get; }
        public int MirrorCleared { get; }
        public int Missing { get; }
        public int Failures { get; }

        public static PlayerSleepPresentationCounts Empty =>
            new PlayerSleepPresentationCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// 睡眠表现 + 点击唤醒的**引擎侧端口**（实现 = <c>Engine/PlayerVitalsReplica.cs</c>，
    /// 组合根注入）。模块层（Modules/）不引用 Game，所以黑屏淡入 / Zzz / 提示文本 / 控件隐藏 /
    /// 主机 <c>WakeUp()</c> 这些引擎动作全在端口后面；门槛 / 强制判定 / 编解码 / 门控是纯逻辑
    /// （<c>Net/PlayerSleepNet.cs</c>），<c>P:\tmp\tsverify</c> 用假端口直接驱动同一份模块源码。
    ///
    /// 权威口径（不可回退）：
    ///   · **主机是唯一睡眠权威**：主机对每个角色跑真正的 <c>ComponentSleep.Update</c>；
    ///     睡眠起始时 / 允许手动唤醒 / 强制睡眠随 <c>PLAYER_VITALS_REPORT</c> 下发；
    ///   · **客户端不再跑 ComponentSleep.Update**（<see cref="IPlayerVitalsPort.DisableLocalSimulation"/>
    ///     摘掉后**不重新加回**）：只按主机状态**写表现字段** —— <c>m_sleepFactor</c> 淡入 / 淡出、
    ///     <c>BlackoutFactor</c>、<c>FloatingMessage("Zzz...")</c>、
    ///     <c>Message("Tap to wake up early" / "You fell unconscious from lack of sleep")</c>、
    ///     <c>ControlsContainerWidget.IsVisible</c>。客户端不做任何唤醒决定；
    ///   · 点击唤醒 = 客户端发 <c>PLAYER_WAKE_REQUEST</c>（id 103），主机复检后调用引擎
    ///     <c>ComponentSleep.WakeUp()</c>，再由权威上报（sleeping=false）驱动客户端淡出。
    /// </summary>
    public interface IPlayerSleepPort
    {
        /// <summary>
        /// **客户端侧**：记录一份主机权威睡眠状态（每一条**未过期**的本机条目都要调用，
        /// 包括同 revision 的 1 Hz 心跳 —— 心跳携带新的 <c>sleep_elapsed</c>，客户端用它
        /// 校准外推起点）。只更新表现缓存，不写引擎字段。
        /// 返回 false + <paramref name="reason"/> = 本机没有这个槽位（不静默丢弃）。
        /// </summary>
        bool NoteLocalSleepReport(PlayerKey key, in PlayerVitalsSnapshot snapshot,
            out string reason);

        /// <summary>
        /// **客户端侧 / 每帧**：推进本机角色的睡眠表现并写引擎显示层。必须在
        /// <c>Frame.Update</c>（帧末，晚于 <c>ComponentScreenOverlays.Update</c> 的每帧 Reset）
        /// 调用。返回本拍写入的计数；<paramref name="reason"/> 非空 = 有槽位失败（模块限流日志）。
        /// </summary>
        PlayerSleepPresentationCounts RefreshSleepPresentation(float dt, out string reason);

        /// <summary>
        /// **客户端侧**：读某个本机角色当前的睡眠表现状态（主机权威 + 客户端外推的 elapsed）。
        /// 门控 / 点击判定只读它，不凭猜测。
        /// </summary>
        bool TryReadLocalSleep(PlayerKey key, out PlayerSleepViewState state, out string reason);

        /// <summary>
        /// **客户端侧 / 每帧**：睡眠中是否发生了一次输入（tap / click / key）。口径与引擎
        /// <c>ComponentSleep.Update:133</c> 一致：
        /// <c>!DialogsManager.HasDialogs(GameWidget) &amp;&amp; GameWidget.Input.Any</c>。
        /// 只读输入，不发送、不改变状态。
        /// </summary>
        bool TryPollLocalSleepTap(PlayerKey key, out bool tapped, out string reason);

        /// <summary>
        /// **客户端侧**：真正发出唤醒请求时清掉本帧输入（引擎在 <c>WakeUp()</c> 前做
        /// <c>GameWidget.Input.Clear()</c>，避免同一个点击又触发别的操作）。
        /// </summary>
        bool TryConsumeLocalSleepInput(PlayerKey key, out string reason);

        /// <summary>
        /// **主机侧**：读主机自己引擎里某个角色的**实时**睡眠权威状态（本机角色或客户端化身）。
        /// 唤醒请求的门槛只用这份值，绝不信任客户端上报的 elapsed。
        /// </summary>
        bool TryReadAuthoritySleep(PlayerKey key, out PlayerSleepAuthorityState state,
            out string reason);

        /// <summary>
        /// **主机侧**：对主机侧那个角色调用引擎 <c>ComponentSleep.WakeUp()</c>
        /// （清 <c>m_sleepStartTime</c>，引擎自己写 SpawnPosition）。只由校验通过的
        /// <c>PLAYER_WAKE_REQUEST</c> 触发；不做任何客户端本地唤醒。
        /// </summary>
        bool TryWakeAuthorityPlayer(PlayerKey key, out string reason);

        /// <summary>
        /// **主机侧 / 每帧**：H3 第三分屏（replace 模式）的睡眠表现镜像。H3 自己的
        /// <c>ComponentSleep.Update</c> 被调试镜像摘掉了，但第三个视图画的是 H3 自己的
        /// <c>ComponentGui / ComponentScreenOverlays</c>（<c>ViewWidget</c> → H3 的 GameWidget），
        /// 所以用**同一个表现写入器**把"被镜像客户端"的睡眠黑屏 / Zzz / 提示 / 控件隐藏写到
        /// 视图所有者身上。只写表现字段（不写 <c>m_sleepStartTime</c>，H3 的存档状态不变）。
        /// 与调试镜像引擎解耦：通过"GameWidget.Target != 自己 + Target 是某个客户端化身"识别
        /// 绑定，不修改 <c>Engine/MirrorCharacterEngine.cs</c>。
        /// </summary>
        PlayerSleepPresentationCounts RefreshMirrorSleepPresentation(float dt,
            out string reason);
    }
}

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
    ///   · **入睡也必须由主机执行**：引擎唯一的入睡入口是 <c>ClothingWidget</c> 的睡眠按钮，
    ///     它跑在持有控件的那一端。客户端如果就地 <c>Sleep()</c>，下一拍权威上报
    ///     （sleeping=false，因为主机的角色根本没睡）会立刻把角色拉起来 —— 这正是
    ///     "点击睡觉 → 躺下 → 瞬间站起"。所以客户端只发 <c>PLAYER_SLEEP_REQUEST</c>（id 104），
    ///     由主机用**它自己的** <c>CanSleep()</c> 复检并 <c>Sleep(true)</c>；
    ///   · 点击唤醒 = 客户端发 <c>PLAYER_WAKE_REQUEST</c>（id 103），主机复检后调用引擎
    ///     <c>ComponentSleep.WakeUp()</c>，再由权威上报（sleeping=false）驱动客户端淡出。
    /// </summary>
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

        /// <summary>真正发出唤醒请求时清掉本帧输入（引擎在 <c>WakeUp()</c> 前做
        /// <c>GameWidget.Input.Clear()</c>）。</summary>
        bool TryConsumeLocalSleepInput(PlayerKey key, out string reason);

        /// <summary>
        /// **客户端侧 / 每帧**：本机角色是否刚刚在**本地**被引擎放倒（= 睡眠按钮 / 床的唯一入口
        /// <c>ClothingWidget.Update</c> 调用了本地 ComponentSleep 的 Sleep 方法）。
        ///
        /// 端口必须**当场收回**这次本地睡眠（客户端的 <c>IsSleeping</c> 只允许由主机确认的状态
        /// 驱动，见 <see cref="TryReadLocalSleep"/>），并把这一帧报告为一次"入睡申请"边沿：
        /// <paramref name="requested"/> = true。模块据此发 <c>PLAYER_SLEEP_REQUEST</c>（id 104）。
        ///
        /// 为什么不直接让客户端睡着：客户端本地 <c>Sleep()</c> 只改自己的组件，主机那个角色
        /// 没睡；下一份权威上报（sleeping=false）会立刻把客户端刚躺下的角色拉起来 —— 实测
        /// 故障"点击睡觉 → 躺下 → 秒起"。
        ///
        /// **无副作用探测**：<paramref name="requested"/> 为边沿；模块用来"发现本机槽位"的调用
        /// 绝不能吞掉它（会把第一次点击吃掉）。
        /// </summary>
        bool TryPollLocalSleepRequest(PlayerKey key, out PlayerSleepRequestViewState state,
            out bool requested, out string reason);

        /// <summary>
        /// **客户端侧**：本机槽位是否可用（有没有这个本机角色 + 有没有主机权威睡眠状态），
        /// **完全不改变任何状态**（包括不吃掉入睡申请的边沿）。
        /// 模块每帧用它发现"本机槽位表"——不能用 <see cref="TryPollLocalSleepRequest"/>
        /// 做发现，那会把还没被处理的那一次点击吞掉。
        /// </summary>
        bool TryDescribeLocalSleep(PlayerKey key, out string reason);

        /// <summary>
        /// **客户端侧**：确实发出了入睡请求时清掉本帧输入（同一个按下不要再触发别的操作）。
        /// </summary>
        bool TryConsumeLocalSleepRequestInput(PlayerKey key, out string reason);

        /// <summary>
        /// **客户端侧**：把主机拒绝入睡的原因显示在本机 HUD（引擎 <c>CanSleep</c> 原文，
        /// 与单机 <c>ClothingWidget</c> 的 <c>DisplaySmallMessage(reason)</c> 同一条路径）。
        /// </summary>
        bool TryShowLocalSleepRejected(PlayerKey key, string detail, out string reason);

        /// <summary>
        /// **主机侧**：用**主机自己**的引擎组件给某个角色重新判定并入睡（
        /// <c>ComponentSleep.CanSleep()</c> → <c>Sleep(allowManualWakeup: true)</c>）。
        /// 客户端从不本地入睡。失败给出稳定原因码（<c>already-sleeping</c> / <c>dead</c> /
        /// <c>can-sleep:&lt;引擎原句&gt;</c> / <c>sleep-failed</c>）与可显示细节。
        /// </summary>
        bool TrySleepAuthorityPlayer(PlayerKey key, bool allowManualWake, out string reasonCode,
            out string detail, out string reason);

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

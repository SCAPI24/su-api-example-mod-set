using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>主机把一次自我伤害施加到主机侧角色上的结果（模块据此映射拒绝原因）。</summary>
    public enum PlayerSelfDamageApplyStatus
    {
        /// <summary>引擎 <c>Injure</c> 已改掉主机侧那个角色的血量。</summary>
        Applied = 0,

        /// <summary>主机上没有这个玩家键的角色（客户端化身还没建好 / 已移除 / 端口未接）。</summary>
        NoAvatar = 1,

        /// <summary>角色已经死了（<c>Health &lt;= 0</c>）：尸体不能再受自我伤害。</summary>
        NotAlive = 2,

        /// <summary>量 / cause 不合法（在调用引擎之前就拒绝，绝不"照着扣一点"）。</summary>
        Invalid = 3,

        /// <summary>结构和身份都合法，但引擎没有把血量改掉（明确失败，不假装成功）。</summary>
        ApplyFailed = 4
    }

    /// <summary>
    /// 客户端**自我伤害**（角色信息界面的骷髅按钮）的**引擎侧端口**（实现 =
    /// <c>Engine/PlayerVitalsReplica.cs</c>，组合根注入）。模块层（<c>Modules/</c>）不引用 Game，
    /// 所以"读控件点击"与"在主机侧调用引擎 Injure"这两件引擎动作都在端口后面；
    /// 边沿门控 / 编解码 / 原因码是纯逻辑（<c>Net/PlayerSelfDamageNet.cs</c>），
    /// <c>P:\tmp\tsverify</c> 用假端口直接驱动同一份模块源码。
    ///
    /// 权威口径（不可回退）：
    ///   · 引擎的骷髅按钮是**纯本地**伤害（<c>Survivalcraft/Game/VitalStatsWidget.cs:176-179</c>）；
    ///     联机客户端本地那一次 <c>Injure</c> 只是**预测**，下一拍主机权威上报
    ///     （<c>PLAYER_VITALS_REPORT</c> = 95）会写回 <c>Health</c> —— 这正是"点了不掉血"；
    ///   · 所以客户端把点击变成 <c>PLAYER_SELF_DAMAGE_REQUEST</c>（id 106）；
    ///   · **主机是唯一裁决者**：它只接受该 clientId 自己槽位的角色，校验存活，然后用
    ///     **同一个引擎调用** <c>ComponentHealth.Injure(amount, null, ignoreInvulnerability: true,
    ///     "Choked")</c> 施加到主机侧客户端化身；既有的体征下发把新血量推回客户端
    ///     （接受 → 预测值与权威值一致；拒绝 → 上报把血量写回，等于回滚预测）；
    ///   · 端口**不**决定谁该受伤（那是模块的归属 / 存活 / 冷却校验），端口只做
    ///     "读本机控件"与"在主机侧那个角色上执行引擎 Injure"。
    /// </summary>
    public interface IPlayerSelfDamagePort
    {
        /// <summary>
        /// **客户端侧 / 每帧**：读本机角色这一帧的自我伤害按键状态。
        ///   · <see cref="PlayerSelfDamagePressState.Pressed"/> =
        ///     <c>ComponentGui.ModalPanelWidget is VitalStatsWidget</c> 时其子控件
        ///     <c>ButtonWidget</c> 名 <c>ChokeButton</c> 的 <c>IsClicked</c>（其余情形恒为 false）；
        ///   · <see cref="PlayerSelfDamagePressState.Dead"/> = 本机角色当前
        ///     <c>ComponentHealth.Health &lt;= 0</c>（主机权威值）。
        /// 读操作**没有副作用**（不发送、不清输入、不吃掉边沿）—— 边沿由模块的门控保存，
        /// 所以发现本机槽位时可以放心用同一次读法。
        /// false + <paramref name="reason"/> = 本机没有这个槽位 / 控件异常（明确失败，不猜）。
        /// </summary>
        bool TryPollLocalSelfDamageClick(PlayerKey key, out PlayerSelfDamagePressState state,
            out string reason);

        /// <summary>
        /// **主机侧**：在主机自己的那份角色上执行**引擎**自我伤害
        /// （<c>ComponentHealth.Injure(amount, null, ignoreInvulnerability: true, cause)</c>，
        /// cause 逐字等于单机的 <c>"Choked"</c>）。
        /// 只由校验通过的 <c>PLAYER_SELF_DAMAGE_REQUEST</c> 调用；客户端的本地 Injure
        /// 永远只是预测，主机这一次才是权威。
        /// <paramref name="healthAfter"/> = 施加之后主机侧的权威生命（
        /// <see cref="PlayerSelfDamageApplyStatus.Applied"/> 时有效）。
        /// 其它状态明确区分"没有这个角色 / 已经死了 / 数值非法 / 引擎没改掉"。
        /// </summary>
        PlayerSelfDamageApplyStatus TryApplyAuthoritySelfDamage(PlayerKey key, float amount,
            string cause, out float healthAfter, out string reason);

        /// <summary>
        /// **客户端侧**：应用一条**已接受**的 107 结果。
        ///   · 把主机权威 <c>health_after</c> 写进本机角色（只在当前本地值更高时写：
        ///     主机上报可能已经先到，不能把更新的权威值改回旧值）；
        ///   · 驱动与单机掉血一致的**受伤表现**：受伤音效 + 红屏因子 + 血条闪烁
        ///     （客户端 <c>ComponentHealth.Update</c> 已被摘掉，这一行表现不会自己发生）；
        ///   · <c>health_after == 0</c> 时按主机权威写死因 + 0 血，让引擎原生死亡流程启动
        ///     （不是客户端自己判死：0 来自主机的 107 结果）。
        /// 返回 false = 没有本机角色 / 写入异常（<paramref name="reason"/> 明确）；
        /// <paramref name="playedSound"/> / <paramref name="redoutArmed"/> = 本次是否真的
        /// 播了受伤音效 / 起了红屏（模块计数用；重复的结果不会重复表现）。
        /// </summary>
        bool TryApplyLocalSelfDamageResult(PlayerKey key, in PlayerSelfDamageResultData result,
            out bool playedSound, out bool redoutArmed, out string reason);

        /// <summary>
        /// **客户端侧**：主机拒绝之后立刻按**缓存的主机权威生命**回写本地（107 拒绝结果不带
        /// <c>health_after</c>，所以用最近一份 95 上报 / 已接受结果缓存的值）。
        /// 只在本地值不同时写；返回 false = 没有权威缓存 / 没有本机角色（不猜）。
        /// </summary>
        bool TryRestoreLocalAuthorityHealth(PlayerKey key, out float health, out string reason);

        /// <summary>
        /// **客户端侧 / 每帧**（Frame.Update，晚于 <c>ComponentScreenOverlays.Update</c> 的每帧
        /// Reset）：把 107 结果 / 95 上报触发的红屏因子按引擎速度衰减并写回
        /// <c>ComponentScreenOverlays.RedoutFactor</c>。返回本拍写到的槽位数；
        /// <paramref name="reason"/> 非空 = 有槽位失败（模块限流日志）。
        /// 只写显示层，不跑任何本地生命模拟。
        /// </summary>
        int RefreshLocalSelfDamagePresentation(float deltaTime, out string reason);

        /// <summary>
        /// **客户端侧**：移除"本地预测回滚"守卫（离开世界 / 断开主机 / 模块下线）。
        /// 守卫是 <c>SubsystemUpdate</c> 里的一个 <c>IUpdateable</c>（UpdateOrder.Reset，
        /// 早于 <c>SubsystemPlayers</c> 的 −20）：它在引擎控件把本地 <c>Health</c> 扣掉之后、
        /// <c>PlayerData.Update</c> 判死之前，把本地值写回**最近一份主机权威生命**，
        /// 从而禁止"客户端自己判死"。重复调用幂等。
        /// </summary>
        void DetachSelfDamage();

        /// <summary>状态行用：引擎侧自我伤害计数（点击读数 / 主机施加 / 失败），不改变行为。</summary>
        string DescribeSelfDamage();
    }
}

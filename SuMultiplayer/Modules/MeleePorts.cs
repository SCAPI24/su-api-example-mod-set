using System;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 近战攻击的引擎侧端口（实现 = <c>Engine/MeleeReplicaEngine.cs</c>，组合根注入）。
    ///
    /// 模块层（Modules/）不引用 Game，因此协议 / 裁决次序 / 计数全部能用
    /// <c>P:\tmp\tsverify</c> 直接编译同一份源码（假世界实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · <see cref="TryExecuteAuthoritative"/> 只在**主机**调用一次：主机解析权威攻击者 /
    ///     目标、做几何裁决，并调一次引擎 <c>ComponentMiner.Hit</c>（命中 / 伤害 / 击退 /
    ///     护甲都由引擎算），返回唯一裁决；
    ///   · 客户端**没有**任何"本地扣血"入口：端口只在客户端做预测表现与结果表现；
    ///   · <see cref="LocalAttackCaptured"/> 由输入拦截（IHitInputSink）在
    ///     <c>ComponentInput.Update</c> 之后、<c>ComponentPlayer.Update</c> 之前抛出；
    ///     原生攻击分支因此整段不执行（客户端不 roll 命中、不扣血）。
    /// </summary>
    public interface IMeleeWorldPort
    {
        /// <summary>本端是不是联机客户端副本（与地形 <c>SuSubsystemTerrain.ClientReplica</c> 同一事实）。</summary>
        bool IsClientReplica { get; }

        /// <summary>
        /// 本端此刻是否接管近战（模块每帧写）：主机 = 正在开房且在世界里；客户端 = 已连接
        /// 且在世界里。false → 输入拦截不吞原生攻击（单机 / 未联机完全走引擎原路径）。
        /// </summary>
        bool Active { get; set; }

        /// <summary>
        /// <c>meleeprediction</c>（默认开）：客户端是否展示乐观预测（挥手 / 挥击声 / 红色命中标记）。
        /// 关掉只影响表现，不影响"客户端绝不本地扣血"。
        /// </summary>
        bool PredictionEnabled { get; set; }

        /// <summary>主机配置的近战距离上限（米，<c>meleereachmax</c>；默认 = 交互上限）。</summary>
        float ServerMaxReach { get; }

        /// <summary>主机权威 tick（SubsystemTime.GameTime × 60；基础设施失败的回执也用它）。</summary>
        long AuthoritativeTick { get; }

        /// <summary>本端引擎 reach（<c>EngineReach.LocalReach()</c>；主机裁决时作为上限的一部分）。</summary>
        float HostEngineReach { get; }

        /// <summary>
        /// 序列号分配器（模块提供，保证每连接单调递增 &gt; 0）。null = 模块未就绪
        /// → 输入拦截返回 false，本帧完全走原生路径（明确失败，不伪造序列号）。
        /// </summary>
        Func<long> SequenceAllocator { get; set; }

        /// <summary>
        /// 客户端战斗随机流镜像（模块持有并在 Attach 时注入）。客户端攻击当帧用它取
        /// <c>attackIndex</c> + <c>u</c> 预测命中；null = 没有流 → 不预测（计数
        /// predictionUnavailable），仍然上行请求，等主机结果与 COMBAT_RNG_SYNC 纠正。
        /// </summary>
        CombatRngClientState CombatRngClient { get; set; }

        /// <summary>
        /// 主机战斗随机流（每个 <c>(clientId, slot)</c> 一条，模块持有并在 Attach 时注入）。
        /// 主机结算**只**从这里抽命中 / 方差；拒绝路径不消耗。
        /// </summary>
        CombatRngHostState CombatRngHost { get; set; }

        /// <summary>共享计数（模块创建；引擎侧的预测 / 审计 / lag-comp 计数写同一实例）。</summary>
        CombatRngTelemetry CombatTelemetry { get; set; }

        /// <summary>
        /// 本地攻击意图（客户端 = 待上行；主机 = 待本地裁决 + 广播）。适配层已经完成
        /// 目标捕获、距离预检（客户端）与预测表现；模块只负责发送 / 执行。
        /// </summary>
        event Action<MeleeLocalAttack> LocalAttackCaptured;

        /// <summary>每帧调用：Project 换新 / 本机玩家变化时重新挂接输入拦截、清理超时预测。</summary>
        void Pump();

        /// <summary>
        /// **主机**：校验并执行一条攻击请求（含主机自己角色的攻击）。
        /// false = 基础设施失败（没有 Project / 没有攻击者 / 目标解析不了），
        /// <paramref name="reason"/> 是可直接写进回执的稳定原因码；
        /// true = 已裁决（<paramref name="result"/> 里 accepted / hit / reason 都是权威结论）。
        /// </summary>
        bool TryExecuteAuthoritative(int sourceClientId, in MeleeAttackRequestData request,
            out MeleeAttackResultData result, out string reason);

        /// <summary>
        /// **客户端**：应用主机结果（攻击者 = 确认 / 回滚预测；观测端 = 表现命中 / 未命中）。
        /// 只播声音 / 粒子 / 伤害数字；绝不写生命、绝不生成实体。
        /// </summary>
        bool ApplyResult(in MeleeAttackResultData result, out string reason);

        /// <summary>
        /// 请求**没有发出去**（通道不可用 / 未注册）：立刻回滚这条预测并写明原因，
        /// 不让它挂在屏幕上等 3 秒超时（也绝不假装发出去了）。
        /// </summary>
        void NoteRequestNotSent(in MeleeAttackRequestData request, string reason);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }

    /// <summary>Hit 输入的分流结果（纯逻辑，见 <see cref="MeleeHitGate"/>）。</summary>
    public enum MeleeHitGateKind
    {
        /// <summary>
        /// 没有引擎等价的身体目标：Hit 不是攻击（左键按下时它就是挖掘按下 / 空点），
        /// 必须原样留给原生 <c>ComponentPlayer.Update</c>，不接管、不发请求、不播表现。
        /// </summary>
        NoBodyTarget,

        /// <summary>射线命中身体：攻击，接管并走主机权威请求 / 客户端预测。</summary>
        BodyTarget
    }

    /// <summary>
    /// Hit 输入的分流规则（纯逻辑，<c>P:\tmp\tsverify</c> 直接编译；实现 =
    /// <c>Engine/MeleeReplicaEngine.cs</c> 的输入拦截）。
    ///
    /// 为什么规则只有"射线是否命中身体"一条：单机原生攻击分支
    /// （Source: Survivalcraft/Game/ComponentPlayer.cs:230-242）先做
    /// <c>ComponentMiner.Raycast&lt;BodyRaycastResult&gt;(Hit, RaycastMode.Interaction)</c>，
    /// **没有命中身体就整段 no-op**；随后 Dig 分支照常执行（:243-250）。左键按下同一帧
    /// 同时置 Dig 与 Hit（Source: Survivalcraft/Game/ComponentInput.cs:183-184），所以
    /// "瞄准方块按下左键"在单机里只有挖掘、没有 Poke / Swoosh / 战斗流消耗。
    ///
    /// 主机本机角色与联机客户端都必须用同一条规则（函数不接收角色 / 端类型参数），
    /// 否则就会重现"挖掘前先响一声攻击"的回归。
    /// </summary>
    public static class MeleeHitGate
    {
        /// <summary>
        /// <paramref name="hasBodyTarget"/> = 引擎等价身体射线（排除自己 / 子身体、
        /// 夹角 &gt; 0.7、距离 ≤ reach、未被地形 / 移动方块遮挡）是否返回了身体。
        /// </summary>
        public static MeleeHitGateKind Classify(bool hasBodyTarget) =>
            hasBodyTarget ? MeleeHitGateKind.BodyTarget : MeleeHitGateKind.NoBodyTarget;

        /// <summary>只有 <see cref="MeleeHitGateKind.BodyTarget"/> 才允许接管 Hit。</summary>
        public static bool ShouldCapture(bool hasBodyTarget) =>
            Classify(hasBodyTarget) == MeleeHitGateKind.BodyTarget;
    }
}

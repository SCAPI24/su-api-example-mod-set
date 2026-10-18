using System;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 客户端一次"建本地活塞表现集合"的结果。每一种都必须出现在状态行里（禁止静默丢）。
    /// </summary>
    public enum PistonPresentOutcome
    {
        /// <summary>已在本地 SubsystemMovingBlocks 里建好只表现集合（不写格 / 不生成实体）。</summary>
        Presented = 0,

        /// <summary>同一活塞的上一条本地动画还没结束：先移除旧的再建（旧动画计入 aborted）。</summary>
        Replaced = 1,

        /// <summary>超出客户端可见范围（SubsystemSky.VisibilityRange）+ 余量 → 不建本地集合。</summary>
        SkippedDistance = 2,

        /// <summary>本地并存的只表现集合已达上限（<see cref="PistonMovePolicy.MaxConcurrentLocalMoves"/>）。</summary>
        SkippedCap = 3,

        /// <summary>活塞所在区块还没加载 / 内容未定稿（建了也看不到、也不会动）。</summary>
        SkippedChunk = 4,

        /// <summary>本地该格不是活塞方块（主机的格变化还没到 / 世界不一致）→ 不表现。</summary>
        SkippedCellMismatch = 5,

        /// <summary>本机玩家 / 身体还没就绪（还没进世界 / 角色切换中）。</summary>
        SkippedNoPlayer = 6,

        /// <summary>明确失败（reason 可直接写日志 / 计数），本次没有画面。</summary>
        Failed = 7
    }

    /// <summary>
    /// 移动组（MovingBlockSet）运动表现的引擎侧端口（实现方 = Engine/PistonReplicaEngine.cs，
    /// 组合根注入）。覆盖两种集合：kind=PISTON（活塞推 / 拉 / 收回）与 kind=COLLAPSING
    /// （沙子 / 沙砾塌落）。模块层（Modules/）保持不依赖 Game，因此编解码 / 排序去重 /
    /// 距离与时长 / 计数都能被 P:\tmp\tsverify 用同一份源码直接编译（假世界实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：引擎 <c>SubsystemPistonBlockBehavior</c> / <c>SubsystemCollapsingBlockBehavior</c>
    ///     是唯一模拟方，<see cref="HostPistonMoveStarted"/> / <see cref="HostPistonMoveEnded"/>
    ///     只上报**引擎已经发生**的运动（含中断 / 拆除 / 区块卸载 / 塌落落地）；
    ///   · 客户端：<see cref="Present"/> 只允许在 <c>SubsystemMovingBlocks</c> 里建本地只表现集合，
    ///     禁止写格 / 生成实体 / 伤害；<see cref="Cancel"/> 只移除本地对象；
    ///     最终状态一律取主机的 CellChange。
    /// </summary>
    public interface IPistonMovePort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>主机：引擎开始了一次运动（一套 MovingBlockSet 刚刚被创建；种类见数据里的 Kind）。</summary>
        event Action<PistonMoveData> HostPistonMoveStarted;

        /// <summary>主机：一次运动的集合已经被引擎移除（completed 语义按 kind，见端口数据定义）。</summary>
        event Action<PistonMoveEndData> HostPistonMoveEnded;

        /// <summary>客户端：引擎侧主动放弃了某条本地动画（世界卸载 / 角色切换 / 看门狗超时）。</summary>
        event Action<MovingBlockKind, Point3, string> ClientMoveAborted;

        /// <summary>每帧调用：Project 换新 / 注入子系统出现时重新挂接；客户端跑本地表现与看门狗。</summary>
        void Pump();

        /// <summary>
        /// 客户端：建一条本地只表现集合。按引擎同一份参数（起始 / 目标 / 速度 / 加速度 / 阻力 /
        /// 平滑度 / 方块 / 种类）调 <c>SubsystemMovingBlocks.AddMovingBlockSet(testCollision:false)</c>。
        /// 不做大距离 / 区块 / 方块判定之外的事；不写格、不生成实体。
        /// </summary>
        /// <param name="startEventId">
        /// 这条运动所属的 PistonMoveEvent.event_id。客户端把它记在本地集合上：
        /// 同一格连续两轮运动时，旧的 PistonMoveEnd 不会误删新一轮动画。
        /// </param>
        PistonPresentOutcome Present(uint startEventId, in PistonMoveData move, out string reason);

        /// <summary>
        /// 客户端：移除该格的本地只表现集合（正常结束 / 主机说提前结束 / 看门狗）。
        /// <paramref name="found"/> = 之前确实有一条本地动画（false = 客户端当时因距离 / 区块跳过了它）。
        /// </summary>
        bool Cancel(in PistonMoveEndData end, out bool found, out string reason);

        /// <summary>
        /// 客户端：放弃全部本地动画（断线 / 退出房间 / 世界卸载），返回移除条数。
        /// <paramref name="pistonAborted"/> / <paramref name="collapsingAborted"/> 是**按种类**的
        /// 拆分（活塞 / 塌落各自的 collapsingMoveAborted 口径）—— 调用方必须分别计数，不混口径。
        /// </summary>
        int AbortAll(string reason, out int pistonAborted, out int collapsingAborted);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }

    /// <summary>
    /// 主机侧"某个玩家端在哪 / 兴趣半径多大"的只读视图（实现方 = Engine/PistonReplicaEngine.cs：
    /// 位置取主机侧联机角色的 <c>ComponentBody.Position</c>，半径取该端上报的兴趣窗口）。
    /// 作用：活塞运动只发给**看得到**的玩家端（远处玩家端不产生本地集合）。
    /// </summary>
    public readonly struct PistonPeerView
    {
        public PistonPeerView(bool hasPosition, Vector3 position, bool hasInterestWindow,
            int radiusChunks, string reason)
        {
            HasPosition = hasPosition;
            Position = position;
            HasInterestWindow = hasInterestWindow;
            RadiusChunks = radiusChunks;
            Reason = reason ?? string.Empty;
        }

        /// <summary>主机侧联机角色的真实身体位置是否可读（角色实体还没建好 = false）。</summary>
        public bool HasPosition { get; }
        public Vector3 Position { get; }

        /// <summary>该端是否已经上报兴趣窗口（区块中心 + 半径）。</summary>
        public bool HasInterestWindow { get; }
        public int RadiusChunks { get; }

        /// <summary>读不到时的明确原因（调用方写限流日志，不猜）。</summary>
        public string Reason { get; }
    }

    /// <summary>主机：读某玩家端当前兴趣窗口（区块中心 + 半径）；实现方 = 地形同步模块。</summary>
    public delegate bool PistonClientRangeResolver(int clientId, out int centerChunkX,
        out int centerChunkZ, out int radiusChunks);

    /// <summary>主机侧玩家端视图端口（端口缺失时模块按"发"处理并明确计数，绝不静默丢）。</summary>
    public interface IPistonPeerViewportPort
    {
        bool TryGetPeerView(int clientId, out PistonPeerView view);
    }
}

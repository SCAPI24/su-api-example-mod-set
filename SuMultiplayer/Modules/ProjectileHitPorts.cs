using System;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 投射物命中反馈的引擎侧端口（实现方 = Engine/ProjectileHitReplicaEngine.cs，组合根注入）。
    /// 模块层（Modules/）保持不依赖 Game，因此编解码 / 排序去重 / 兴趣窗口路由都能被
    /// 同一份源码直接编译（假实现实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：引擎在自己 Update 里**已经**执行过那次命中（AttackBody 扣血 + 建伤害数字），
    ///     <see cref="HostProjectileHit"/> 只上报这个既成事实；
    ///   · 客户端：<see cref="Present"/> 只允许创建 <c>HitValueParticleSystem</c>，禁止扣血 /
    ///     改格 / 生成实体。血量权威仍在 PLAYER_VITALS_REPORT。
    /// </summary>
    public interface IProjectileHitPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>主机：一次投射物命中已经在引擎里结算完成（伤害 = 更新前后血量差 × 韧性）。</summary>
        event Action<ProjectileHitData> HostProjectileHit;

        /// <summary>每帧调用：Project 换新 / 子系统外壳换新后重新挂接捕获窗口。</summary>
        void Pump();

        /// <summary>
        /// 主机是否需要打开命中捕获窗口。只有"本端是主机 + 至少一个玩家端在线"才需要：
        /// 单机 / 主机还没人加入时为 false，引擎因此不做每帧身体与地形射线（纯性能，不影响
        /// 正确性：模块在 Tick 里先置 true，早于下一次子系统 Update）。
        /// </summary>
        bool WantHostCapture { get; set; }

        /// <summary>
        /// 客户端：按主机权威命中点在本地创建伤害数字（与引擎同一位置 / 速度 / 文字）。
        /// false = 明确失败（<paramref name="reason"/> 可直接写日志 / 计数）；
        /// 距离之外 / 区块未载入也走 false，但原因必须可区分。
        /// </summary>
        bool Present(in ProjectileHitData data, out string reason);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }

    /// <summary>
    /// 主机命中捕获窗口（实现方 = Engine/ProjectileHitReplicaEngine.cs）。
    /// <c>SuSubsystemProjectiles.Update</c> 在主机分支用它包住 <c>base.Update</c>：
    /// 命中前拍下"这个投射物将命中谁、命中前血量"，命中后算差 —— 这是唯一能拿到
    /// "引擎实际扣了多少血"的位置（投射物不走任何回执消息）。
    ///
    /// 契约：<see cref="BeginHostHitCapture"/> 之后**必须**调用 <see cref="EndHostHitCapture"/>，
    /// 即使 <c>base.Update</c> 抛异常（外壳用 try/finally 保证）。
    /// </summary>
    public interface IProjectileHitHostSink
    {
        /// <summary>主机：<c>base.Update</c> 之前开始捕获（客户端副本 / 无 Project 时自行忽略）。</summary>
        void BeginHostHitCapture(float deltaTime);

        /// <summary>主机：<c>base.Update</c> 之后结束捕获、算伤害并按需上报。</summary>
        void EndHostHitCapture();
    }

    /// <summary>
    /// 主机：某个玩家端当前兴趣窗口（区块中心 + 半径）。
    /// 实现方 = Modules/TerrainSyncModule.cs（数据源 = 客户端 <c>INTEREST_UPDATE</c>），
    /// 与地形下发 / 编辑校验用的是**同一份**窗口 —— 世界分区口径只有一处真相。
    /// </summary>
    public interface IClientInterestWindowPort
    {
        /// <summary>false = 该端还没上报过窗口（调用方必须明确计数，不允许改用"发给所有人"）。</summary>
        bool TryGetClientRange(int clientId, out int centerChunkX, out int centerChunkZ,
            out int radiusChunks);
    }

    /// <summary>
    /// 委托直通实现（组合根用）：把 <c>TerrainSyncModule.TryGetClientRange</c> 适配成
    /// <see cref="IClientInterestWindowPort"/>，让命中模块不必依赖编辑端口这样的宽接口。
    /// 数据源与地形下发 / 编辑校验是**同一份**窗口，不存在第二套区间口径。
    /// </summary>
    public sealed class ClientInterestWindowAdapter : IClientInterestWindowPort
    {
        public delegate bool Resolver(int clientId, out int centerChunkX, out int centerChunkZ,
            out int radiusChunks);

        private readonly Resolver m_resolver;

        public ClientInterestWindowAdapter(Resolver resolver)
        {
            m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public bool TryGetClientRange(int clientId, out int centerChunkX, out int centerChunkZ,
            out int radiusChunks) =>
            m_resolver(clientId, out centerChunkX, out centerChunkZ, out radiusChunks);
    }
}

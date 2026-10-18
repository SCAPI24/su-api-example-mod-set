using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 客户端引擎适配层上报的"可以请主机拾取"的候选。
    ///
    /// 只由客户端产生：引擎原生的拾取在客户端被
    /// <c>Engine/PickableReplicaSubsystems.cs:SuSubsystemPickables</c> 抑制，
    /// 由适配层按与引擎相同的距离/创建宽限判据扫描后交给模块发请求。
    /// Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
    ///         （3.0625 = 1.75m 吸引，1.0 = 直接拾取，CreationTime 0.5s 宽限）
    /// Source: Mod/ScMultiplayer/Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs:170-260
    ///         （RequestNearbyPickableAcquisition）
    /// </summary>
    public readonly struct PickablePickupCandidate
    {
        public PickablePickupCandidate(int pickableId, Vector3 position, Vector3 target)
        {
            PickableId = pickableId;
            Position = position;
            Target = target;
        }

        public int PickableId { get; }
        /// <summary>副本当前位置（诊断 / 日志）。</summary>
        public Vector3 Position { get; }
        /// <summary>本机玩家眼睛位置（请求里只作诊断；主机用自己算出的位置校验）。</summary>
        public Vector3 Target { get; }
    }

    /// <summary>
    /// 主机执行一次客户端拾取请求的结果（成功时构造）。
    /// 背包增量是**稀疏**的（只含真的变了的格子），语义与 PlayerInventoryDelta 一致。
    /// Source: Mod/ScMultiplayer/Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs:263-440
    ///         （AcquireItems + TryBuildInventoryDelta + response.Count/CollectorClientId）
    /// </summary>
    public readonly struct PickableAcquireOutcome
    {
        public PickableAcquireOutcome(int remainingCount, bool removed, int[] indices,
            int[] values, int[] counts, int inventoryVersion)
        {
            RemainingCount = remainingCount;
            Removed = removed;
            Indices = indices ?? Array.Empty<int>();
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            InventoryVersion = inventoryVersion;
        }

        public int RemainingCount { get; }
        public bool Removed { get; }
        public int[] Indices { get; }
        public int[] Values { get; }
        public int[] Counts { get; }
        public int InventoryVersion { get; }

        public bool HasInventory => Indices.Length > 0;
    }

    /// <summary>
    /// 实体复制的计数器（状态行 / 自检可审计）。**实例对象**，由模块持有；没有任何静态状态。
    /// 命名与需求逐条对应：pickableAdd/Update/Remove/Rejected/Dropped、projectile*。
    /// </summary>
    public sealed class EntityReplicationCounters
    {
        // ---- 拾取物（发送侧：主机；接收侧：客户端）
        public long PickableAdd;
        public long PickableUpdate;
        public long PickableRemove;
        public long PickableRejected;
        public long PickableDropped;
        public long PickableReceived;
        public long PickableApplied;

        // ---- 投射物
        public long ProjectileAdd;
        public long ProjectileUpdate;
        public long ProjectileRemove;
        public long ProjectileRejected;
        public long ProjectileDropped;
        public long ProjectileReceived;
        public long ProjectileApplied;

        // ---- 增量批次 / 快照
        public long StateMessagesSent;
        public long StateSendFailures;
        public long StateRecordsSent;
        public long StateReceived;
        public long StateDropped;
        public long StateRejected;
        public long SnapshotsSent;
        public long SnapshotSendFailures;
        public long SnapshotsReceived;
        public long SnapshotRecords;
        public long SnapshotDropped;
        public long SnapshotStaleRemoved;

        // ---- 拾取请求 / 回执
        public long PickupRequestsSent;
        public long PickupRequestFailures;
        public long PickupRequestsReceived;
        public long PickupAccepted;
        public long PickupRejected;
        public long PickupDuplicate;
        public long PickupTimeouts;
        public long PickupInventoryApplied;
        public long PickupInventoryFailures;
        public long PickupResultUnknown;
        public long PickupSuppressed;
        public long PickupNoInventory;
        public long PickupPersisted;
        public long PickupPersistFailures;

        // ---- resync / 捕获
        public long ResyncRequestsSent;
        public long ResyncRequestsReceived;
        public long CaptureFailures;
        public long ClientStateOnHostRejected;

        // ---- 客户端丢弃请求（dropRequestSent / Accepted / Rejected(reason) / Timeout）
        public long DropRequestsSent;
        public long DropRequestFailures;
        public long DropRequestsReceived;
        public long DropAccepted;
        public long DropRejected;
        public long DropDuplicate;
        public long DropTimeouts;
        public long DropSuppressed;
        public long DropResultsUnknown;
        public long DropInventoryApplied;
        public long DropInventoryFailures;
        public long DropPersisted;
        public long DropPersistFailures;
        public long DropResultSendFailures;

        // ---- 客户端投掷 / 开火请求
        public long FireRequestsSent;
        public long FireRequestFailures;
        public long FireRequestsReceived;
        public long FireAccepted;
        public long FireRejected;
        public long FireDuplicate;
        public long FireTimeouts;
        public long FireSuppressed;
        public long FireResultsUnknown;
        public long FireInventoryApplied;
        public long FireInventoryFailures;
        public long FirePersisted;
        public long FirePersistFailures;
        public long FireResultSendFailures;
        /// <summary>主机权威生成的投射物总数（含霰弹的多发）。</summary>
        public long FireProjectilesSpawned;
        /// <summary>主机接受但没有生成投射物的次数（空包弹 / 没装箭 / 弩没拉满）。</summary>
        public long FireBlankShots;

        // ---- 客户端回执应用（丢弃 + 开火共用，需求里的 inventoryDeltaApplied）
        public long InventoryDeltaApplied;
        public long InventoryDeltaFailures;

        /// <summary>拒绝原因计数（按固定原因词汇；实例对象，无静态状态）。</summary>
        public readonly RequestReasonTally DropRejectReasons = new RequestReasonTally();
        public readonly RequestReasonTally FireRejectReasons = new RequestReasonTally();
    }

    /// <summary>
    /// 拾取物 / 投射物复制的**引擎侧端口**（实现方 = Engine/PickableReplicaEngine.cs，
    /// 组合根注入）。模块层（Modules/）保持不依赖 Game：
    ///   · 主机：抓当前权威实体 / 执行客户端拾取、丢弃、投掷与开火请求；
    ///   · 客户端：把主机的状态变成引擎里的**副本**（只渲染，不拾取、不参与任何权威结算）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机是唯一创建 / 更新 / 删除拾取物与投射物的一方；
    ///   · 客户端只调用 <see cref="TryApplyPickable"/> / <see cref="TryApplyProjectile"/>，
    ///     引擎侧实现里没有客户端自己的 FireProjectile / AddPickable / AcquireItems 路径；
    ///   · 客户端的拾取只产生 <see cref="ClientPickupSubmitted"/> 请求；
    ///   · 客户端的丢弃 / 投掷 / 开火只产生 <see cref="IEntityRequestSink"/> 请求。
    /// </summary>
    public interface IEntityReplicaPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>引擎侧的拾取物 / 投射物子系统是否已经就绪（客户端没进世界时为 false）。</summary>
        bool IsReplicaReady { get; }

        /// <summary>客户端：扫描到可以请求拾取的副本（模块负责序号 / 去重 / 发送）。</summary>
        event Action<PickablePickupCandidate> ClientPickupSubmitted;

        /// <summary>
        /// 模块在 Attach 里挂上的请求入口（引擎侧输入钩子 / 方块行为外壳调用它）。
        /// null = 模块没上线：丢掉 / 开火意图会明确失败，绝不本地创建实体。
        /// </summary>
        IEntityRequestSink RequestSink { get; set; }

        /// <summary>客户端安全网计数：被清除的“非主机复制”拾取物（正常联机流程应保持 0）。</summary>
        long ForeignPickablesRemoved { get; }

        /// <summary>客户端安全网计数：被清除的“非主机复制”投射物（正常联机流程应保持 0）。</summary>
        long ForeignProjectilesRemoved { get; }

        /// <summary>每帧调用：Project 换新 / 注入子系统出现 / 角色变化时重新挂接。</summary>
        void Pump();

        /// <summary>
        /// 主机：抓当前权威拾取物（Id 由适配层按对象稳定分配；Action 填 ADD 占位）。
        /// false + reason = 子系统缺失 / 本端不是主机（调用方计数，绝不当成"世界空了"）。
        /// </summary>
        bool TryCaptureHostPickables(List<PickableStateData> target, out string reason);

        /// <summary>主机：抓当前权威投射物（同上）。</summary>
        bool TryCaptureHostProjectiles(List<ProjectileStateData> target, out string reason);

        /// <summary>
        /// 主机：校验并执行一条客户端拾取请求（距离 / 创建宽限 / 背包容量都由引擎侧权威判断）。
        /// false + reason = 明确拒绝，调用方必须回一条带原因的拒绝回执。
        /// </summary>
        bool TryAcquire(int clientId, in PickableAcquireRequestData request,
            out PickableAcquireOutcome outcome, out string reason);

        /// <summary>
        /// 主机：校验并执行一条客户端丢弃请求（整格丢出）。
        /// 校验：玩家端 / 化身存活 / 活跃槽一致 / 槽内容与主机权威背包完全一致 / 距离 / 道具合法。
        /// 执行：从**主机权威背包**移除整格 → 用引擎 AddPickable 在主机权威位置生成 →
        /// 回传稀疏背包增量（客户端据此对账，本地之前没有任何扣除）。
        /// false + reason = 明确拒绝（两端都不掉物品）。
        /// </summary>
        bool TryDrop(int clientId, in PickableDropRequestData request,
            out PickableDropOutcome outcome, out string reason);

        /// <summary>
        /// 主机：校验并执行一条客户端投掷 / 开火请求（长矛 / 雪球 / 弓 / 弩 / 火枪）。
        /// 执行：用引擎自己的 FireProjectile 生成权威投射物，并消费道具 / 耐久
        /// （与对应方块行为的 Completed 分支同形）。
        /// false + reason = 明确拒绝（不生成投射物、不消费）；成功但 spawned=0 是引擎本身的
        /// 合法结果（空包弹 / 没装箭 / 弩没拉满），此时仍执行权威状态切换。
        /// </summary>
        bool TryFireProjectile(int clientId, in ProjectileFireRequestData request,
            out ProjectileFireOutcome outcome, out string reason);

        /// <summary>客户端：创建 / 更新 / 移除一个拾取物副本（按 state.Action）。</summary>
        bool TryApplyPickable(in PickableStateData state, out string reason);

        /// <summary>客户端：创建 / 更新 / 移除一个投射物副本（按 state.Action）。</summary>
        bool TryApplyProjectile(in ProjectileStateData state, out string reason);

        /// <summary>客户端：每帧推进副本插值 + 扫描附近可拾取副本（不修改主机世界）。</summary>
        void TickClient(float deltaTime);

        /// <summary>客户端：清空全部副本（断线 / 换房间 / 世界卸载）。</summary>
        bool ResetClientReplicas(string reason);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }

    /// <summary>
    /// 模块实现的**客户端请求入口**：引擎侧采集到“丢弃 / 投掷 / 开火”意图后调用它。
    /// 返回 true = 请求已入队发送（客户端可以安全吞掉本地预测）；
    /// false + reason = 明确失败（客户端不扣物品、不生成实体，调用方必须写日志）。
    /// </summary>
    public interface IEntityRequestSink
    {
        /// <summary>整格丢弃（客户端本地还没有任何扣除）。</summary>
        bool TrySubmitDrop(in PickableDropIntent intent, out string reason);

        /// <summary>投掷 / 弓 / 弩 / 火枪开火（客户端本地还没有扣道具 / 扣耐久）。</summary>
        bool TrySubmitProjectileFire(in ProjectileFireIntent intent, out string reason);
    }
}

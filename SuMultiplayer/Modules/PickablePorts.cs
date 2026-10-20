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
            int[] values, int[] counts, int inventoryVersion, int itemValue,
            Vector3 position, bool playPickupSound)
        {
            RemainingCount = remainingCount;
            Removed = removed;
            Indices = indices ?? Array.Empty<int>();
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            InventoryVersion = inventoryVersion;
            ItemValue = itemValue;
            Position = position;
            PlayPickupSound = playPickupSound;
        }

        public int RemainingCount { get; }
        public bool Removed { get; }
        public int[] Indices { get; }
        public int[] Values { get; }
        public int[] Counts { get; }
        public int InventoryVersion { get; }
        /// <summary>主机权威拾取物的完整物品值（contents + data）。</summary>
        public int ItemValue { get; }
        /// <summary>主机权威拾取位置（客户端上报的位置不参与）。</summary>
        public Vector3 Position { get; }
        /// <summary>
        /// 主机这次拾取是否需要播放 <c>Audio/PickableCollected</c>。
        /// 与引擎 <c>SubsystemPickables.Update</c> 同口径：只有**整堆被拾完**（Count 归零）且
        /// 不是经验币（contents=248，引擎走 <c>AddExperience</c> 的 ExperienceCollected）时才播。
        /// Source: Survivalcraft/Game/SubsystemPickables.cs:210-213
        /// </summary>
        public bool PlayPickupSound { get; }

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
        /// <summary>客户端：快照的 state_version 比已应用的增量状态更旧 → 整条拒绝（不误删新副本）。</summary>
        public long SnapshotStale;

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
        /// <summary>客户端：掉落者拾取宽限内**没有**发请求的次数（pickupGraceBlocked 的客户端侧）。</summary>
        public long PickupGraceSuppressed;
        /// <summary>客户端：主机以 pickup-grace 拒绝后登记本地宽限的次数（回执明确原因）。</summary>
        public long PickupGraceBlockedByHost;
        /// <summary>客户端：本机造成的掉落副本被认领并登记拾取宽限的数量（挖 / 放 / 手丢）。</summary>
        public long OwnDropGraceRegistered;
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

        // ---- 客户端 UI 拖出丢弃（inventory drag-to-world；与键盘 Drop 分开计数）
        /// <summary>客户端：UI 拖出被拦截并交给请求链的次数（= 收到 Source=UI 的意图）。</summary>
        public long UiDropIntercepted;
        /// <summary>客户端：UI 拖出请求**成功发送**的次数（主机裁决前的入队/发送）。</summary>
        public long UiDropRequestSent;
        /// <summary>客户端：UI 拖出被本端抑制（未就绪 / 请求表满 / 发送失败）——物品留在背包。</summary>
        public long UiDropSuppressed;
        /// <summary>客户端：主机接受了 UI 拖出请求（权威拾取物已生成）。</summary>
        public long UiDropAccepted;
        /// <summary>客户端：主机拒绝了 UI 拖出请求（原因见 <see cref="UiDropRejectReasons"/>）。</summary>
        public long UiDropRejected;
        /// <summary>客户端：UI 拖出请求回执超时（本地从未扣除，物品不丢）。</summary>
        public long UiDropTimeouts;
        /// <summary>主机：收到的 UI 拖出丢弃请求条数。</summary>
        public long UiDropRequestsReceived;
        /// <summary>
        /// UI 拖出被拒绝的原因计数（固定原因词汇；实例对象，无静态状态）。
        /// 客户端 = 收到的主机拒绝；主机 = 自己拒绝的 UI 请求。
        /// </summary>
        public readonly RequestReasonTally UiDropRejectReasons = new RequestReasonTally();

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

        // ---- 开火复位 / 声音中继（本轮新增）
        /// <summary>主机：开火后给所有端重播的权威手持物复位次数。</summary>
        public long FireResetSent;
        /// <summary>主机：复位广播失败次数。</summary>
        public long FireResetFailures;
        /// <summary>开火者客户端：应用回执稀疏背包增量完成弓 / 弩 / 火枪复位的次数。</summary>
        public long FireResetApplied;
        /// <summary>主机：成功广播的武器开火表现事件条数（按接收端计）。</summary>
        public long FireSoundRelayed;
        /// <summary>客户端：收到的武器开火表现事件条数（含自己的回声）。</summary>
        public long FireSoundReceived;
        /// <summary>本机成功播放的开火声音次数（主机本机 / 客户端）。</summary>
        public long FireSoundPresented;
        /// <summary>开火者客户端跳过的自己回声条数（本地已经播过）。</summary>
        public long FireSoundOwnEchoSkipped;
        /// <summary>开火声音播放失败 / 事件非法的次数。</summary>
        public long FireSoundFailures;

        // ---- 拾取声音中继（主机接受客户端拾取请求后的纯表现）
        /// <summary>主机：成功入队的 PickupSoundEvent 条数（按接收端计；请求者恒定在内）。</summary>
        public long PickupSoundRelayed;
        /// <summary>客户端：收到的 PickupSoundEvent 条数（含非法载荷；方向错误的主机不计）。</summary>
        public long PickupSoundReceived;
        /// <summary>本机实际播放 Audio/PickableCollected 的次数（主机本机 + 客户端）。</summary>
        public long PickupSoundPresented;
        /// <summary>
        /// 跳过的回声：主机本机拾取（引擎已原生播放，绝不再发事件）/ 主机收到方向错误的事件。
        /// </summary>
        public long PickupSoundEchoSkipped;
        /// <summary>因超出附近半径而未发送 / 未播放的端数（主机收件人裁剪 + 客户端二次裁剪）。</summary>
        public long PickupSoundSkippedDistance;
        /// <summary>主机：读不到角色位置而没有收到拾取声音的端数（不猜原点）。</summary>
        public long PickupSoundSkippedNoPosition;
        /// <summary>客户端：载荷非法 / 解析失败而整条丢弃的事件数。</summary>
        public long PickupSoundMalformed;
        /// <summary>客户端：重复 / 更旧事件 id（重放）被拒绝的次数。</summary>
        public long PickupSoundReplay;
        /// <summary>发送 / 接收超过限速而跳过的事件数。</summary>
        public long PickupSoundRateSkipped;
        /// <summary>客户端：需要监听者位置却读不到（明确计数，不猜原点）。</summary>
        public long PickupSoundNoListener;
        /// <summary>主机：编码 / 发送失败次数（请求者可能听不到这次声音）。</summary>
        public long PickupSoundSendFailures;
        /// <summary>两端：引擎播放调用失败次数。</summary>
        public long PickupSoundPresentFailures;

        // ---- 客户端丢弃副本物化（本轮新增）
        /// <summary>客户端：从主机回执（带 pickable_id）立即物化的丢弃副本数量。</summary>
        public long DropReplicaMaterialised;
        /// <summary>客户端：随后被 EntityState(ADD/UPDATE) 确认的丢弃副本数量。</summary>
        public long DropReplicaConfirmed;
        /// <summary>客户端：回执有 pickable_id 但迟迟没有状态确认的次数。</summary>
        public long DropReplicaTimeouts;
        /// <summary>客户端：按回执物化丢弃副本失败的次数（仍等待 EntityState）。</summary>
        public long DropReplicaFailures;

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

        /// <summary>
        /// 客户端本机**当前有效交互距离**（引擎 ComponentMiner.Raycast 的 reach：
        /// 创造 = SettingsManager.CreativeReach，生存 = 5）。拾取 / 丢弃 / 开火请求都带上它；
        /// 主机按 min(reach + 容差, 主机上限) 校验。
        /// </summary>
        float LocalReach { get; }

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

        /// <summary>主机：从**主机化身**出生点调用 FireProjectile 的次数（客户端 origin 不参与）。</summary>
        long FireSpawnAtHost { get; }

        /// <summary>主机：FireProjectile 返回 null（出生射线被地形 / 自身包围盒挡住）的次数。</summary>
        long FireSpawnCollided { get; }

        /// <summary>客户端：主机丢弃副本已出现后清掉的本地预测拾取物数量。</summary>
        long DropPredictionCulled { get; }

        /// <summary>客户端：主机丢弃副本还没到时暂缓清理的本地预测拾取物次数。</summary>
        long DropPredictionDeferred { get; }

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
        /// 主机：校验并执行一条客户端丢弃请求。
        ///   · <c>Source=INPUT</c>（键盘 / 手柄）：校验玩家端 / 化身存活 / 活跃槽 / 槽内容与
        ///     主机权威背包一致 / 距离 / 道具合法；从权威背包移除**整格**。
        ///   · <c>Source=UI</c>（背包拖出到世界）：同样校验槽内容 / 距离 / 道具合法，但移除
        ///     **恰好 request.Count 个**（必须 1 ≤ count ≤ 权威槽数量，否则 "count-unavailable"），
        ///     速度按引擎 ViewWidget.DragDrop 的 12 m/s × 拖拽方向生成。
        /// 执行：从**主机权威背包**移除 → 用引擎 AddPickable 在主机权威位置生成 →
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

        /// <summary>
        /// 客户端：这个刚到的拾取物副本是不是**本机刚造成的掉落**（本机挖 / 放方块时本地引擎预测的掉落，
        /// 或本机手丢预测）？true ⇒ 模块给该副本登记拾取宽限（1.5s 内不发 PickableAcquireRequest）；
        /// 一条预测只匹配一个副本（调用即消费）。主机也会用 <c>pickup-grace</c> 拒绝，两头一致。
        /// </summary>
        bool TryClaimOwnDropGrace(in PickableStateData state);

        /// <summary>客户端：创建 / 更新 / 移除一个投射物副本（按 state.Action）。</summary>
        bool TryApplyProjectile(in ProjectileStateData state, out string reason);

        /// <summary>客户端：每帧推进副本插值 + 扫描附近可拾取副本（不修改主机世界）。</summary>
        void TickClient(float deltaTime);

        /// <summary>客户端：清空全部副本（断线 / 换房间 / 世界卸载）。</summary>
        bool ResetClientReplicas(string reason);

        /// <summary>
        /// 武器开火声音（纯表现）：主机执行客户端请求后本机播一次；客户端收到
        /// WeaponFireEvent 后按同一入口播。绝不生成实体 / 扣物品 / 造成伤害。
        /// false + reason = 没有 Project / 未知 kind（调用方计数并限流写日志）。
        /// </summary>
        bool TryPresentWeaponFire(in WeaponFireEventData fire, out string reason);

        /// <summary>
        /// 拾取声音（纯表现，两端同一条引擎参数）：只调 <c>SubsystemAudio.PlaySound</c>，
        /// 绝不授予物品 / 删除拾取物 / 改地形。
        /// 声音名 / 音量 / 音高 / 最小距离与引擎逐条一致：
        /// <c>Audio/PickableCollected, 0.7f, -0.4f, position, 2f, autoDelay=false</c>。
        /// Source: Survivalcraft/Game/SubsystemPickables.cs:210-213
        /// </summary>
        bool TryPresentPickupSound(in PickupSoundEventData sound, out string reason);

        /// <summary>
        /// 客户端：本机音频监听者位置（活动相机 → 本机角色）；读不到 false + reason
        /// （调用方明确计数，不猜原点）。
        /// </summary>
        bool TryGetAudioListenerPosition(out Vector3 position, out string reason);

        /// <summary>
        /// 主机：收集在线玩家端（clientId + 主机侧角色位置），用于拾取声音的“附近客户端”裁剪。
        /// 读不到位置的端仍然返回（HasPosition=false），由收件人策略明确计数。
        /// </summary>
        bool TryCollectPlayerPeers(List<RemoteSoundPeer> target, out string reason);

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
        /// <summary>
        /// 丢弃（客户端本地还没有任何扣除）。<see cref="PickableDropIntent.Source"/> 区分：
        /// 键盘 / 手柄整格丢弃（<see cref="DropSources.Input"/>）与背包拖出到世界
        /// （<see cref="DropSources.Ui"/>，携带拖拽方向与明确数量）。
        /// </summary>
        bool TrySubmitDrop(in PickableDropIntent intent, out string reason);

        /// <summary>投掷 / 弓 / 弩 / 火枪开火（客户端本地还没有扣道具 / 扣耐久）。</summary>
        bool TrySubmitProjectileFire(in ProjectileFireIntent intent, out string reason);
    }
}

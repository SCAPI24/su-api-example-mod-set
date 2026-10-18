using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 主机侧"按需单块加载"的可调参数（一个组合根一个实例，**没有静态状态**）。
    ///
    /// 口径（用户 2026-10-18 定）：客户端上报的**边缘**块（已经进入客户端内存、但主机的地形更新位置
    /// 覆盖不到）也要加载；但主机**不**把任何角色的兴趣半径 / 内容距离放大一圈（那会带来性能开销与
    /// 局部不同步）。所以加载是**逐块、按需、有界、可回收**的，而不是把范围整体加大。
    ///
    /// 并发口径（用户 2026-10-18 第二次实机故障后定）：
    ///   · <see cref="MaxPending"/> 是**台账**上限（排队 + 活动 + 保留窗口），不是同时向引擎请求的上限；
    ///     客户端可以一次报上几百块，台账先把它们全部收下，**不因为 64 满就拒绝**；
    ///   · <see cref="MaxActive"/> 才是**同时存在的引擎临时单块加载位置**上限（活动请求 + 已服务保留）；
    ///     到上限的请求在台账里按**身份轮转 FIFO**等待，绝不丢弃；
    ///   · 只有"台账真满 / 冷却中 / 引擎明确拒绝"才是拒绝（计数 + 点名原因行）。
    /// </summary>
    public sealed class OnDemandLoadOptions
    {
        /// <summary>
        /// 在册（台账）上限：排队 + 活动 + 保留窗口内的块数上限。
        /// 客户端可一次上报几百块，所以这里要足够大；满了才是**明确拒绝**（不挤掉正在等待的块）。
        /// </summary>
        public int MaxPending { get; set; } = 512;

        /// <summary>
        /// 同时存在的**引擎临时单块加载位置**上限（活动请求 + 已服务保留窗口）。
        /// 到上限后新请求在台账里按身份轮转 FIFO 等待，**不拒绝**；32~64 是实机建议值。
        /// </summary>
        public int MaxActive { get; set; } = 64;

        /// <summary>
        /// 单次 Tick 最多向引擎补发多少个请求（重试与新提升各自最多这么多）。
        /// 首个请求在收到上报时立即发出（受 <see cref="MaxActive"/> 约束），所以"每 tick 的请求数"
        /// 这一项只约束后续推进。
        /// </summary>
        public int RequestsPerTick { get; set; } = 8;

        /// <summary>第一次重试前的退避（毫秒），之后每次翻倍。</summary>
        public int RetryBaseMs { get; set; } = 1000;

        /// <summary>退避上限（毫秒）。</summary>
        public int RetryMaxMs { get; set; } = 4000;

        /// <summary>单块最多向引擎请求几次（含首次）。</summary>
        public int MaxAttempts { get; set; } = 5;

        /// <summary>
        /// 单块从**首次请求**起算的总等待上限（毫秒）：到点仍未拿到内容 → 明确超时放弃。
        /// 排队等待不算在这里（那由 <see cref="QueueTimeoutMs"/> 单独约束）。
        /// </summary>
        public int TotalTimeoutMs { get; set; } = 20000;

        /// <summary>
        /// 在台账里排队等待引擎槽位的上限（毫秒）：到点仍未轮到 → 明确超时（防无限黄框）。
        /// 正常情况下 FIFO + 有界 <see cref="MaxActive"/> 会在远小于这个值的时间内轮到。
        /// </summary>
        public int QueueTimeoutMs { get; set; } = 120000;

        /// <summary>
        /// 明确放弃后的冷却时间（毫秒）：这段时间内不再为同一块自动发起加载（新的上报只回 NotReady）。
        /// 只用于**真实失败**（引擎拒绝 / 超时），排队等待不产生冷却。
        /// </summary>
        public int CooldownMs { get; set; } = 60000;

        /// <summary>
        /// 内容到达（可裁决）后临时加载位置的**最短保留**时间（毫秒）：给"裁决 → 回传快照编码"留出窗口，
        /// 免得块在编码前被引擎回收（那会退化成 NotReady → 客户端重报 → 又加载的循环）。
        /// </summary>
        public int ServedMinHoldMs { get; set; } = 10000;

        /// <summary>
        /// **排队有压力时**的保留窗口（毫秒）：队列里还有块等引擎槽位时，已服务块到这个点就让位。
        /// 为什么需要它：<see cref="MaxActive"/> 同时限制活动 + 保留，若保留块一律占满 10s/60s，
        /// 后面几百块要等很久，客户端 NotReady 重报次数先用尽（`放弃` 增长）才轮到裁决。
        /// 默认 1000ms 覆盖"裁决 → 回传快照编码"（65536 格 ≈ 14 帧 @60fps ≈ 0.23s）的正常窗口；
        /// 没有排队压力时仍走 <see cref="ServedMinHoldMs"/> / <see cref="ServedMaxHoldMs"/>。
        /// </summary>
        public int ServedPressureHoldMs { get; set; } = 1000;

        /// <summary>临时加载位置的**最长保留**时间（毫秒）：即使客户端还报着这块，也到这个点回收。</summary>
        public int ServedMaxHoldMs { get; set; } = 60000;

        /// <summary>汇总 / 拒绝原因行的最小间隔（毫秒），避免一次上报潮刷屏。</summary>
        public int LogIntervalMs { get; set; } = 10000;

        /// <summary>一行里最多点名几个区块键（超出写"还有 N 块"）。</summary>
        public int MaxKeysPerLog { get; set; } = 6;
    }

    /// <summary>
    /// 主机侧的**按需单块加载台账**（纯托管、可被 P:\tmp\tsverify 直接编译；引擎调用只通过
    /// <see cref="ITerrainWorld"/> 的两个端口方法）。
    ///
    /// 为什么需要它（实机故障 2026-10-18）：
    ///   · 主机给每个联机角色挂一条地形更新位置，半径 = 客户端**上报**的兴趣半径
    ///     （Engine/NetworkAvatarSpawner.cs → Terrain/HostTerrainLocations.cs），两边口径相同；
    ///   · 但引擎的 <c>SetUpdateLocation</c> 有 8 格滞回（Survivalcraft/Game/TerrainUpdater.cs:258），
    ///     主机这条圆最多比客户端那条偏 8 格 ⇒ 客户端内存里**贴着边缘的一圈**块落在主机内容距离之外，
    ///     主机永远拿不到内容 ⇒ 那几条上报永远没法裁决（日志：`已请求加载 1 次` 却一直是黄框），
    ///     客户端不动就永远不消失；客户端稍微一动，主机那条圆滑过去，立刻变绿；
    ///   · 修复前"请求加载"这条路的终点只是 <c>TerrainBridge.RequestLoad</c> 的一个事件 +
    ///     模块里一行 verbose 日志（Modules/TerrainSyncModule.cs:HandleLoadRequested），
    ///     **没有任何引擎调用**；而且 <c>TerrainBridge.m_requested</c> 去重后同一块一辈子只发一次事件。
    ///
    /// 第二次实机故障（2026-10-18，真机）：台账把"在册块数"和"同时向引擎请求的块数"混成了一个
    /// 64 的上限。两个客户端各在内存里报着 ~200 块，边缘块一次涌进来超过 64 → 第 65 块起被**当场拒绝**：
    /// 上报不挂待裁决（实时地图灰色 s=0，`未核对` / `放弃` 继续增长），客户端重报也永远只在
    /// "冷却 / 上限"之间打转。**容量必须限制活动的引擎请求，而不是排队台账。**
    ///
    /// 现在的做法（**只加"这一块"，不加范围**）：
    ///   · 收到一条"主机没有内容"的上报 → <see cref="Request"/>：为**这一个**块建一条台账记录；
    ///     台账有界（<see cref="OnDemandLoadOptions.MaxPending"/>），先全部收下，**不因并发满而拒绝**；
    ///   · 台账按身份轮转 FIFO 提升到"活动"：只有活动 + 已服务保留 ≤
    ///     <see cref="OnDemandLoadOptions.MaxActive"/> 时才向引擎挂临时单块地形更新位置；
    ///   · 内容到达 → 台账把它标成 <c>Served</c> 并交给宿主**立刻裁决**（不需要玩家移动）；
    ///   · 没到 → 按 <see cref="OnDemandLoadOptions.RetryBaseMs"/> 起的指数退避重试，最多
    ///     <see cref="OnDemandLoadOptions.MaxAttempts"/> 次 / <see cref="OnDemandLoadOptions.TotalTimeoutMs"/>；
    ///   · 超时 / 引擎明确拒绝 → **明确放弃这一轮**：撤掉临时位置、写一行点名原因、进冷却（有界），
    ///     宿主要把这条上报从待裁决里摘掉（绝不"永远挂着等一个永远不来的内容"）；
    ///   · 内容到达后临时位置只保留"最短保留 / 不再需要"这段窗口，之后撤掉
    ///     （<see cref="ITerrainWorld.ReleaseSingleChunkLoad"/>），主机不会长期维持一圈更大的范围；
    ///     排队有压力时，到了最短保留窗口的"仍被客户端需要"的块也会让位（否则少量保留块会挡住
    ///     后面几百块，黄框迟迟轮不到），让步同样有明确计数与汇总行；
    ///   · 客户端离开（<see cref="ReleaseOwner"/>）/ 会话结束（<see cref="Clear"/>）→ 立刻撤掉它的全部请求。
    ///
    /// 有界性：在册块数 ≤ <see cref="OnDemandLoadOptions.MaxPending"/>；引擎临时位置 ≤
    /// <see cref="OnDemandLoadOptions.MaxActive"/>（活动 + 保留）；每 tick 引擎请求数 ≤
    /// 2 × <see cref="OnDemandLoadOptions.RequestsPerTick"/>；每 tick 的台账扫描是 O(在册块数)。
    /// 日志限流：超时行每 tick 至多一行（点名 ≤ MaxKeysPerLog 块），拒绝行与汇总行按 LogIntervalMs 限流。
    /// </summary>
    public sealed class OnDemandChunkLoads
    {
        /// <summary>一条按需加载记录（**块**是主语；同一块可被多个身份同时需要，见 <see cref="Entry.Owners"/>）。</summary>
        private enum EntryState
        {
            /// <summary>在台账里排队，还没有拿到引擎临时位置。</summary>
            Queued,
            /// <summary>已经拿到引擎临时位置，内容还没到（或正在按退避重试）。</summary>
            Active,
            /// <summary>内容已到位，临时位置在保留窗口内（可以裁决 / 编码回传）。</summary>
            Served
        }

        private sealed class Entry
        {
            public ChunkKey Key;
            /// <summary>需要这块的身份稳定键（<see cref="PlayerSessionRecord.StableKey"/>）。空 = 没有身份归属。</summary>
            public readonly List<string> Owners = new List<string>(1);
            public EntryState State;
            /// <summary>本轮进入排队台账的时刻（TickCount64 口径；排队超时从这里算）。</summary>
            public long EnqueuedMs;
            /// <summary>首次真正交给引擎的时刻（TickCount64 口径；总超时从这里算）。</summary>
            public long FirstRequestMs;
            /// <summary>已经交给引擎几次（不含"已经有内容所以没请求"的情况）。</summary>
            public int Requests;
            /// <summary>下一次允许重试的时刻。</summary>
            public long NextRequestMs;
            /// <summary>内容到位的时刻（保留窗口从这里算）。</summary>
            public long ServedMs;
            /// <summary>当前是否占着一条引擎临时单块加载位置（活动 / 保留）。</summary>
            public bool HasSlot;
            /// <summary>是否已经计入 <see cref="Chunks"/>（台账接受过的块只计一次）。</summary>
            public bool CountedChunk;
            /// <summary>本轮排队是否已经计过 <see cref="Deferred"/>（同一轮不重复计数）。</summary>
            public bool DeferredCounted;
            /// <summary>是否已经排进某个身份的队列（防同一轮重复入队）。</summary>
            public bool QueueEnqueued;
            /// <summary>当前排队归属的身份键（空串 = 无身份归属的退化队列）。</summary>
            public string QueueOwner;
            /// <summary>本 tick 内被判定要删除（不在遍历字典时改字典）。</summary>
            public bool Removed;
        }

        private enum RejectKind { Cap, Cooldown, Engine }

        /// <summary>没有身份归属时的退化队列键（与任何真实稳定键都不冲突）。</summary>
        private const string NoOwner = "";

        private readonly ITerrainWorld m_world;
        private readonly OnDemandLoadOptions m_options;
        private readonly Dictionary<ChunkKey, Entry> m_entries = new Dictionary<ChunkKey, Entry>();
        /// <summary>身份 → 排队键（每个身份一条 FIFO；身份之间轮转，避免一个客户端饿死另一个）。</summary>
        private readonly Dictionary<string, Queue<ChunkKey>> m_ownerQueues =
            new Dictionary<string, Queue<ChunkKey>>(StringComparer.Ordinal);
        /// <summary>当前有排队工作的身份轮转环（顺序 = 首次入队顺序；出队后指针前移）。</summary>
        private readonly List<string> m_ring = new List<string>(8);
        private int m_ringIndex;
        private int m_queuedCount;
        private int m_activeCount;
        private int m_heldCount;
        private long m_deferred;
        /// <summary>明确放弃的块 → 冷却到期的时刻（有界：只在超过上限时清理过期项）。</summary>
        private readonly Dictionary<ChunkKey, long> m_cooldowns = new Dictionary<ChunkKey, long>();
        private readonly List<ChunkKey> m_timedOutKeys = new List<ChunkKey>(8);
        private readonly List<string> m_timedOutReasons = new List<string>(8);
        private readonly List<ChunkKey> m_servedKeys = new List<ChunkKey>(8);
        private readonly List<ChunkKey> m_rejectKeys = new List<ChunkKey>(8);
        private readonly List<string> m_rejectReasons = new List<string>(4);
        private readonly List<string> m_pendingKeys = new List<string>(8);
        private readonly List<ChunkKey> m_expired = new List<ChunkKey>(8);
        private readonly List<ChunkKey> m_dead = new List<ChunkKey>(8);
        private readonly List<string> m_lines = new List<string>(4);
        /// <summary>汇总行用的"按身份排队"临时统计（复用，不在热路径分配大字典）。</summary>
        private readonly Dictionary<string, int> m_queueOwnerCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private long m_lastSummaryMs;
        private long m_lastRejectLogMs;
        private long m_rejectedSinceLog;
        private long m_rejectedCapSinceLog;
        private long m_rejectedCooldownSinceLog;
        private long m_rejectedEngineSinceLog;
        private long m_lastSummaryRequests;
        private long m_lastSummaryServed;
        private long m_lastSummaryTimeouts;
        private long m_lastSummaryRejected;
        private long m_lastSummaryDeferred;
        private long m_lastSummaryUnderPressure;

        public OnDemandChunkLoads(ITerrainWorld world, OnDemandLoadOptions options = null)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_options = options ?? new OnDemandLoadOptions();
            if (m_options.MaxPending < 1) m_options.MaxPending = 1;
            if (m_options.MaxActive < 1) m_options.MaxActive = 1;
            if (m_options.MaxPending < m_options.MaxActive) m_options.MaxPending = m_options.MaxActive;
            if (m_options.RequestsPerTick < 1) m_options.RequestsPerTick = 1;
            if (m_options.MaxAttempts < 1) m_options.MaxAttempts = 1;
            if (m_options.RetryBaseMs < 1) m_options.RetryBaseMs = 1;
            if (m_options.RetryMaxMs < m_options.RetryBaseMs) m_options.RetryMaxMs = m_options.RetryBaseMs;
            if (m_options.TotalTimeoutMs < m_options.RetryBaseMs) m_options.TotalTimeoutMs = m_options.RetryBaseMs;
            if (m_options.QueueTimeoutMs < 1) m_options.QueueTimeoutMs = 1;
            if (m_options.CooldownMs < 0) m_options.CooldownMs = 0;
            if (m_options.ServedMinHoldMs < 0) m_options.ServedMinHoldMs = 0;
            if (m_options.ServedPressureHoldMs < 0) m_options.ServedPressureHoldMs = 0;
            if (m_options.ServedPressureHoldMs > m_options.ServedMinHoldMs)
                m_options.ServedPressureHoldMs = m_options.ServedMinHoldMs;
            if (m_options.ServedMaxHoldMs < m_options.ServedMinHoldMs)
                m_options.ServedMaxHoldMs = m_options.ServedMinHoldMs;
            if (m_options.LogIntervalMs < 1) m_options.LogIntervalMs = 1;
            if (m_options.MaxKeysPerLog < 1) m_options.MaxKeysPerLog = 1;
        }

        // ------------------------------------------------------------ 计数器（全部只增，口径写在这里）

        /// <summary>真正交给引擎的加载请求**次数**（含重试；同一块重试 3 次 = 3）。</summary>
        public long Requests { get; private set; }
        /// <summary>台账接受过的区块**数**（排队 / 活动 / 已服务都算，重试不重复计；被拒绝的不算）。</summary>
        public long Chunks { get; private set; }
        /// <summary>内容真正到位、可以裁决的区块数（同一块内容丢失后再次到位会再计一次）。</summary>
        public long Served { get; private set; }
        /// <summary>超时放弃的区块数（每一轮只算一次）。</summary>
        public long Timeouts { get; private set; }
        /// <summary>被拒绝的请求次数（= 台账满 + 冷却 + 引擎明确拒绝三项之和；排队不算拒绝）。</summary>
        public long Rejected { get; private set; }
        /// <summary>因为"台账在册块数已达上限"被拒绝的次数（真正的容量拒绝）。</summary>
        public long RejectedCap { get; private set; }
        /// <summary>因为"上一轮超时/引擎拒绝后的冷却还没过"被拒绝的次数。</summary>
        public long RejectedCooldown { get; private set; }
        /// <summary>因为引擎明确不接受（世界未加载 / 没有 TerrainUpdater / 临时位置满了）被拒绝的次数。</summary>
        public long RejectedEngine { get; private set; }
        /// <summary>内容到位后按需撤掉的临时加载次数（服务完成 / 不再需要）。</summary>
        public long ReleasedServed { get; private set; }
        /// <summary>为排队有压力而提前让位的"仍被客户端需要"的保留位置次数（有界、可审计）。</summary>
        public long ReleasedUnderPressure { get; private set; }

        /// <summary>当前在**排队**（还没拿到引擎临时位置）的块数 = onDemandQueued。</summary>
        public int Queued => m_queuedCount;
        /// <summary>当前**活动**（已拿到临时位置、内容还没到）的块数 = onDemandActive。</summary>
        public int Active => m_activeCount;
        /// <summary>当前**保留窗口**内（内容已到、临时位置还没回收）的块数 = onDemandHeld。</summary>
        public int Held => m_heldCount;
        /// <summary>当前台账在册（排队 + 活动 + 保留）的块数 = pendingOnDemand。</summary>
        public int Pending => m_entries.Count;
        /// <summary>台账接受但当时没有引擎槽位、进入排队等待的累计次数 = onDemandDeferred。</summary>
        public long Deferred => m_deferred;

        /// <summary>状态行口径（测试与状态行都读这一份，不另立一份计数）。</summary>
        public string Describe() =>
            "onDemandQueued " + Queued + " · onDemandActive " + Active + " · onDemandHeld " + Held +
            " · onDemandHeldReleasedUnderPressure " + ReleasedUnderPressure +
            " · onDemandDeferred " + Deferred + " · onDemandServed " + Served +
            " · onDemandTimeouts " + Timeouts + " · onDemandRejected " + Rejected +
            "（cap " + RejectedCap + " · cooldown " + RejectedCooldown + " · engine " + RejectedEngine +
            "）· onDemandLoadRequests " + Requests + "（区块 " + Chunks + "）· pendingOnDemand " + Pending;

        /// <summary>等待 / 拒绝 / 汇总的行（**已限流**；调用方取走后写日志，不在热路径逐条打印）。</summary>
        public void DrainLines(List<string> into)
        {
            if (into == null || m_lines.Count == 0) return;
            into.AddRange(m_lines);
            m_lines.Clear();
        }

        /// <summary>这块现在有没有一条在册的按需加载（= 不需要再发请求）。</summary>
        public bool IsActive(ChunkKey key) => m_entries.ContainsKey(key);

        /// <summary>这块现在是不是在"明确放弃后的冷却期"里（此期间不再自动请求；新上报只回 NotReady）。</summary>
        public bool InCooldown(ChunkKey key) => m_cooldowns.ContainsKey(key);

        /// <summary>
        /// 给"主机一直拿不到内容"的原因行用：这块在按需加载台账里的状态（没有记录就返回 false）。
        /// </summary>
        public bool TryDescribe(ChunkKey key, long nowMs, out string text)
        {
            if (m_entries.TryGetValue(key, out Entry entry))
            {
                string state;
                if (entry.State == EntryState.Queued)
                {
                    long queuedFor = Math.Max(0L, nowMs - entry.EnqueuedMs);
                    state = "排队等待引擎槽位（已排 " +
                        (queuedFor / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s / 在册 " +
                        m_entries.Count + " 块 / 活动 " + m_activeCount + " · 保留 " + m_heldCount +
                        " / 上限 " + m_options.MaxActive + "）";
                }
                else if (entry.State == EntryState.Active)
                {
                    long waited = Math.Max(0L, nowMs - entry.FirstRequestMs);
                    long next = Math.Max(0L, entry.NextRequestMs - nowMs);
                    state = "已请求引擎 " + entry.Requests + " 次 / 已等 " +
                        (waited / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s / " +
                        (next > 0 ? next + "ms 后重试" : "下一拍重试") +
                        "（上限 " + m_options.MaxAttempts + " 次 · " + m_options.TotalTimeoutMs + "ms）";
                }
                else
                {
                    state = "内容已到位，等待回收窗口";
                }
                text = "按需单块加载：" + state + " · 归属 " +
                    (entry.Owners.Count == 0 ? "none" : string.Join(",", entry.Owners));
                return true;
            }
            if (m_cooldowns.TryGetValue(key, out long until))
            {
                long left = Math.Max(0L, until - nowMs);
                text = "按需单块加载已放弃这一轮（冷却还剩 " +
                    (left / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s，期间不再自动请求）";
                return true;
            }
            text = null;
            return false;
        }

        // ------------------------------------------------------------ 登记 / 释放

        /// <summary>
        /// 为**这一个块**登记一次按需加载（幂等：同一块已有记录 → 只追加归属，不重复请求引擎）。
        /// 返回 true = 台账收下了（可能马上拿到引擎槽位，也可能先排队；两种情况**都**该挂待裁决 / 黄框）。
        /// 返回 false = **明确拒绝**（台账满 / 冷却 / 引擎明确拒绝；原因写 <paramref name="rejectedReason"/>，
        /// 调用方不得把这条上报挂进"待裁决"——那块没有任何人在加载，挂上就是永远裁决不了的黄框）。
        /// </summary>
        public bool Request(ChunkKey key, string owner, long nowMs, out string rejectedReason)
        {
            rejectedReason = null;
            if (m_entries.TryGetValue(key, out Entry existing))
            {
                AddOwner(existing, owner);
                if (existing.State == EntryState.Queued && existing.QueueOwner == NoOwner &&
                    !string.IsNullOrEmpty(owner))
                {
                    existing.QueueOwner = owner;
                    existing.QueueEnqueued = false;
                    Enqueue(existing);
                }
                return true;
            }
            if (m_cooldowns.TryGetValue(key, out long until))
            {
                if (nowMs < until)
                {
                    rejectedReason = "冷却中（上一轮已明确放弃，还剩 " +
                        ((until - nowMs + 999) / 1000) + "s 才允许再次自动请求）";
                    NoteRejected(key, rejectedReason, RejectKind.Cooldown);
                    return false;
                }
                m_cooldowns.Remove(key);
            }
            if (m_entries.Count >= m_options.MaxPending)
            {
                rejectedReason = "台账在册上限 " + m_options.MaxPending +
                    " 已满（排队 + 活动 + 保留；不挤掉正在等待的块）";
                NoteRejected(key, rejectedReason, RejectKind.Cap);
                return false;
            }
            var entry = new Entry
            {
                Key = key,
                State = EntryState.Queued,
                EnqueuedMs = nowMs,
                NextRequestMs = nowMs,
                QueueOwner = string.IsNullOrEmpty(owner) ? NoOwner : owner
            };
            AddOwner(entry, owner);
            m_entries[key] = entry;
            m_queuedCount++;
            Enqueue(entry);
            if (m_world.HasContents(key))
            {
                // 已经有内容（调用方刚查过，这里做一次并发下的二次确认）：不请求引擎，
                // 下一拍 Tick 会把它标成 Served 并交给宿主裁决。
                CountChunk(entry);
                return true;
            }
            if (m_activeCount + m_heldCount >= m_options.MaxActive)
            {
                // 容量满：**排队等待**，不拒绝。调用方照样挂 Requested（黄），到点会轮转提升。
                CountChunk(entry);
                MarkDeferred(entry);
                return true;
            }
            if (TryIssue(entry, nowMs, out string engineReason))
            {
                Promote(entry);
                CountChunk(entry);
                return true;
            }
            // 引擎明确不接受 → **不占位、不留待裁决**：撤掉记录、进冷却（不反复砸同一个坏引擎）、如实计数。
            m_entries.Remove(key);
            m_queuedCount--;
            m_cooldowns[key] = nowMs + m_options.CooldownMs;
            rejectedReason = engineReason;
            NoteRejected(key, engineReason, RejectKind.Engine);
            return false;
        }

        /// <summary>
        /// 某个身份不再需要它登记过的那些块（客户端离开 / 会话解绑）：只去掉它的归属；
        /// 没有归属的记录**立刻撤掉临时加载位置**（块交回引擎按常规管理）。
        /// 返回撤掉的记录数。
        /// </summary>
        public int ReleaseOwner(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return 0;
            int released = 0;
            bool removed = false;
            foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
            {
                Entry entry = pair.Value;
                if (entry.Removed) continue;
                if (!entry.Owners.Remove(owner)) continue;
                if (entry.Owners.Count == 0)
                {
                    Drop(entry);
                    removed = true;
                    released++;
                    continue;
                }
                // 排队归属是刚离开的身份 → 改挂到还需要的身份上（否则这条记录会掉出轮转环）。
                if (entry.State == EntryState.Queued &&
                    string.Equals(entry.QueueOwner, owner, StringComparison.Ordinal))
                {
                    entry.QueueOwner = entry.Owners[0];
                    entry.QueueEnqueued = false;
                    Enqueue(entry);
                }
            }
            if (m_ownerQueues.TryGetValue(owner, out Queue<ChunkKey> queue))
            {
                queue.Clear();               // 旧队列里可能还有已改挂 / 已删除的键，一律作废
                m_ownerQueues.Remove(owner);
            }
            m_ring.Remove(owner);
            if (m_ringIndex >= m_ring.Count) m_ringIndex = 0;
            if (removed) Compact();
            return released;
        }

        /// <summary>会话 / 房间结束：撤掉**全部**临时加载位置（不写盘、不留状态）。返回撤掉的记录数。</summary>
        public int Clear()
        {
            int count = 0;
            foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
            {
                Entry entry = pair.Value;
                if (entry.Removed) continue;
                Drop(entry);
                count++;
            }
            m_entries.Clear();
            m_cooldowns.Clear();
            m_ownerQueues.Clear();
            m_ring.Clear();
            m_ringIndex = 0;
            m_queuedCount = 0;
            m_activeCount = 0;
            m_heldCount = 0;
            return count;
        }

        // ------------------------------------------------------------ 每 tick 推进

        /// <summary>
        /// 推进台账（主线程每 tick 一次，宿主已持有自己的锁）：
        ///   · 内容到了 → 标 <c>Served</c> + 放进 <paramref name="served"/>（宿主**立刻裁决**这些块）；
        ///   · 没到且退避到点 → 预算内重发引擎请求；到上限 / 超时 → 放进 <paramref name="timedOut"/>；
        ///   · 排队中的块 → 按身份轮转 FIFO 补进空闲的引擎槽位（不拒绝、不丢）；
        ///   · 已服务的块 → 过了保留窗口 / 不再被需要 / 排队有压力 → 撤掉临时加载位置。
        /// </summary>
        /// <param name="stillNeeded">
        /// "这块现在还该不该留在主机内存里"（宿主的判定：有身份还在内存集合里报着它 / 还有待裁决 /
        /// 还有回传快照没发完）。null = 没有额外理由（只按保留窗口回收）。
        /// </param>
        public void Tick(long nowMs, Func<ChunkKey, bool> stillNeeded, List<ChunkKey> served,
            List<ChunkKey> timedOut)
        {
            served?.Clear();
            timedOut?.Clear();
            m_timedOutKeys.Clear();
            m_timedOutReasons.Clear();
            m_servedKeys.Clear();
            bool removed = false;

            // ① 状态推进（只做判定，不向引擎发请求）。
            foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
            {
                Entry entry = pair.Value;
                if (entry.Removed) { removed = true; continue; }
                if (entry.State == EntryState.Served)
                {
                    // 内容在保留窗口内又被引擎回收了（别处触发卸载 / 换世界）：回到"排队等待"，
                    // 下一拍按退避重新请求 —— 绝不停在"已服务"状态假装主机还拿着内容。
                    if (!m_world.HasContents(entry.Key))
                    {
                        RequeueAfterContentLoss(entry, nowMs);
                        continue;
                    }
                    long held = nowMs - entry.ServedMs;
                    if (held >= m_options.ServedMaxHoldMs)
                    {
                        Drop(entry);
                        removed = true;
                        continue;
                    }
                    if (held >= m_options.ServedMinHoldMs)
                    {
                        bool needed = stillNeeded != null && stillNeeded(entry.Key);
                        if (!needed)
                        {
                            Drop(entry);
                            removed = true;
                            continue;
                        }
                    }
                    // 排队有压力时让位：否则少数"客户端还拿着"的保留块会占满 MaxActive，
                    // 后面几百块永远轮不到（黄框迟迟不变绿），客户端 NotReady 重报次数先用尽。
                    // 让位有独立计数；仍受 ServedPressureHoldMs（默认 1s）保护，覆盖回传快照编码窗口。
                    if (m_queuedCount > 0 && held >= m_options.ServedPressureHoldMs)
                    {
                        ReleasedUnderPressure++;
                        Drop(entry);
                        removed = true;
                    }
                    continue;
                }
                if (m_world.HasContents(entry.Key))
                {
                    MarkServed(entry, nowMs, served);
                    removed = true;
                    continue;
                }
                if (entry.State == EntryState.Queued)
                {
                    if (nowMs - entry.EnqueuedMs >= m_options.QueueTimeoutMs)
                    {
                        // 排队等到上限：明确超时（清黄框 + 冷却），绝不无限挂着。
                        Timeout(entry, nowMs,
                            "排队等引擎槽位超时（活动 " + m_activeCount + " · 保留 " + m_heldCount +
                            " / 上限 " + m_options.MaxActive + "，在册 " + m_entries.Count + "）",
                            timedOut, engineRefusal: false);
                        removed = true;
                    }
                    continue;
                }
                // 活动：总超时 / 次数上限到点（不必等下一次退避）。
                if (entry.Requests >= m_options.MaxAttempts ||
                    nowMs - entry.FirstRequestMs >= m_options.TotalTimeoutMs)
                {
                    Timeout(entry, nowMs,
                        "引擎请求 " + entry.Requests + " 次 / 等满 " + m_options.TotalTimeoutMs +
                        "ms 仍未拿到内容",
                        timedOut, engineRefusal: false);
                    removed = true;
                }
            }

            // ② 排队 → 活动：按身份轮转 FIFO 补满空闲槽位（新负载优先于重试）。
            int dispatchBudget = m_options.RequestsPerTick;
            removed |= DispatchQueued(nowMs, ref dispatchBudget, served, timedOut);

            // ③ 活动 → 引擎重试（退避到点才发；引擎明确拒绝 = 明确放弃，绝不无限重试）。
            int retryBudget = m_options.RequestsPerTick;
            if (retryBudget > 0)
            {
                foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
                {
                    Entry entry = pair.Value;
                    if (entry.Removed || entry.State != EntryState.Active) continue;
                    if (nowMs < entry.NextRequestMs) continue;
                    if (entry.Requests >= m_options.MaxAttempts) continue;   // ① 已处理
                    if (retryBudget <= 0) break;
                    if (TryIssue(entry, nowMs, out string reason))
                    {
                        retryBudget--;
                        continue;
                    }
                    Timeout(entry, nowMs, "引擎拒绝：" + reason, timedOut, engineRefusal: true);
                    retryBudget--;
                    removed = true;
                }
            }

            if (removed) Compact();
            PruneCooldowns(nowMs);
            if (m_timedOutKeys.Count > 0) AppendTimeoutLine();
            FlushRejectLine(nowMs);
            MaybeSummary(nowMs);
        }

        // ------------------------------------------------------------ 内部

        private static void AddOwner(Entry entry, string owner)
        {
            if (string.IsNullOrEmpty(owner)) return;
            for (int i = 0; i < entry.Owners.Count; i++)
                if (string.Equals(entry.Owners[i], owner, StringComparison.Ordinal)) return;
            entry.Owners.Add(owner);
        }

        /// <summary>把块挂进它的身份 FIFO；同一个身份队列为空时登记进轮转环。</summary>
        private void Enqueue(Entry entry)
        {
            if (entry.QueueEnqueued) return;
            string owner = entry.QueueOwner ?? NoOwner;
            if (!m_ownerQueues.TryGetValue(owner, out Queue<ChunkKey> queue))
            {
                queue = new Queue<ChunkKey>();
                m_ownerQueues[owner] = queue;
            }
            queue.Enqueue(entry.Key);
            entry.QueueEnqueued = true;
            bool inRing = false;
            for (int i = 0; i < m_ring.Count; i++)
            {
                if (!string.Equals(m_ring[i], owner, StringComparison.Ordinal)) continue;
                inRing = true;
                break;
            }
            if (!inRing) m_ring.Add(owner);
        }

        /// <summary>排队 → 活动（占一条引擎临时位置）。</summary>
        private void Promote(Entry entry)
        {
            if (entry.State != EntryState.Queued) return;
            entry.State = EntryState.Active;
            entry.QueueEnqueued = false;
            m_queuedCount--;
            m_activeCount++;
        }

        private void CountChunk(Entry entry)
        {
            if (entry.CountedChunk) return;
            entry.CountedChunk = true;
            Chunks++;
        }

        private void MarkDeferred(Entry entry)
        {
            if (entry.DeferredCounted) return;
            entry.DeferredCounted = true;
            m_deferred++;
        }

        /// <summary>交一次请求给引擎；返回 false 时 <paramref name="reason"/> 是引擎给出的原因。</summary>
        private bool TryIssue(Entry entry, long nowMs, out string reason)
        {
            reason = null;
            if (!m_world.TryRequestSingleChunkLoad(entry.Key, out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = "引擎没有给出原因";
                return false;
            }
            entry.HasSlot = true;
            if (entry.FirstRequestMs == 0) entry.FirstRequestMs = nowMs;
            entry.Requests++;
            entry.NextRequestMs = nowMs + BackoffMs(entry.Requests);
            Requests++;
            return true;
        }

        private long BackoffMs(int attempts)
        {
            long backoff = m_options.RetryBaseMs;
            for (int i = 1; i < attempts; i++)
            {
                backoff *= 2;
                if (backoff >= m_options.RetryMaxMs) return m_options.RetryMaxMs;
            }
            return Math.Min(backoff, m_options.RetryMaxMs);
        }

        /// <summary>内容到位：进入保留窗口；没有临时位置的（内容本来就在）立即交付并撤掉台账记录。</summary>
        private void MarkServed(Entry entry, long nowMs, List<ChunkKey> served)
        {
            if (entry.State == EntryState.Queued) m_queuedCount--;
            else if (entry.State == EntryState.Active) m_activeCount--;
            entry.State = EntryState.Served;
            entry.QueueEnqueued = false;
            entry.ServedMs = nowMs;
            Served++;
            CountChunk(entry);
            m_servedKeys.Add(entry.Key);
            served?.Add(entry.Key);
            if (entry.HasSlot)
            {
                m_heldCount++;
                return;
            }
            // 内容本来就在 / 引擎独立加载：没有临时位置要保留，裁决用 HasContents 即可，直接交付。
            Drop(entry);
        }

        /// <summary>保留窗口内内容丢失：撤掉旧位置、回到排队（重新走有界退避 / 超时）。</summary>
        private void RequeueAfterContentLoss(Entry entry, long nowMs)
        {
            if (entry.HasSlot)
            {
                m_world.ReleaseSingleChunkLoad(entry.Key);
                entry.HasSlot = false;
                m_heldCount--;
            }
            entry.State = EntryState.Queued;
            entry.ServedMs = 0L;
            entry.FirstRequestMs = 0L;
            entry.Requests = 0;
            entry.NextRequestMs = nowMs;
            entry.EnqueuedMs = nowMs;
            entry.DeferredCounted = false;
            entry.QueueOwner = entry.Owners.Count > 0 ? entry.Owners[0] : NoOwner;
            entry.QueueEnqueued = false;
            m_queuedCount++;
            Enqueue(entry);
            if (m_activeCount + m_heldCount >= m_options.MaxActive) MarkDeferred(entry);
        }

        /// <summary>按身份轮转 FIFO 把排队块提升为活动；引擎明确拒绝 → 明确放弃 + 冷却 + 通知宿主。</summary>
        private bool DispatchQueued(long nowMs, ref int budget, List<ChunkKey> served, List<ChunkKey> timedOut)
        {
            if (budget <= 0) return false;
            bool removed = false;
            int guard = 0;
            int maxGuard = m_ring.Count * 4 + m_entries.Count * 2 + 16;
            while (budget > 0 && m_activeCount + m_heldCount < m_options.MaxActive &&
                   m_ring.Count > 0 && guard++ < maxGuard)
            {
                if (m_ringIndex >= m_ring.Count) m_ringIndex = 0;
                string owner = m_ring[m_ringIndex];
                if (!m_ownerQueues.TryGetValue(owner, out Queue<ChunkKey> queue) || queue.Count == 0)
                {
                    m_ownerQueues.Remove(owner);
                    m_ring.RemoveAt(m_ringIndex);
                    if (m_ringIndex >= m_ring.Count) m_ringIndex = 0;
                    continue;
                }
                ChunkKey key = queue.Dequeue();
                if (!m_entries.TryGetValue(key, out Entry entry) || entry.Removed ||
                    entry.State != EntryState.Queued || !entry.QueueEnqueued ||
                    !string.Equals(entry.QueueOwner, owner, StringComparison.Ordinal))
                {
                    // 过期队列项（已经直接提升 / 已改挂 / 已删除）：跳过它，**不换身份**，
                    // 继续取同一身份队列里的下一块 —— 否则"首个请求立即发出"留下的队首空位
                    // 会让另一个身份插队（身份轮转仍由每次成功请求后的指针前移保证）。
                    continue;
                }
                entry.QueueEnqueued = false;
                if (m_world.HasContents(key))
                {
                    // 排队期间内容自己到了：直接交付（不占引擎位置）。
                    MarkServed(entry, nowMs, served);
                    removed = true;
                    continue;
                }
                if (!TryIssue(entry, nowMs, out string reason))
                {
                    Timeout(entry, nowMs, "引擎拒绝：" + reason, timedOut, engineRefusal: true);
                    removed = true;
                    budget--;
                    continue;
                }
                Promote(entry);
                budget--;
                m_ringIndex = (m_ringIndex + 1) % m_ring.Count;
            }
            return removed;
        }

        /// <summary>超时放弃一轮：撤位置 + 计数 + 冷却 + 点名（宿主随后把这条上报从待裁决里摘掉）。</summary>
        private void Timeout(Entry entry, long nowMs, string reason, List<ChunkKey> timedOut, bool engineRefusal)
        {
            if (engineRefusal && !string.IsNullOrEmpty(reason))
                NoteRejected(entry.Key, reason, RejectKind.Engine);
            Drop(entry);
            Timeouts++;
            m_cooldowns[entry.Key] = nowMs + m_options.CooldownMs;
            m_timedOutKeys.Add(entry.Key);
            m_timedOutReasons.Add(string.IsNullOrEmpty(reason) ? "未拿到内容" : reason);
            timedOut?.Add(entry.Key);
        }

        /// <summary>撤掉一条记录的临时加载位置（真正交给引擎的回收动作只有这一句）。</summary>
        private void Drop(Entry entry)
        {
            if (entry.Removed) return;
            entry.Removed = true;
            switch (entry.State)
            {
                case EntryState.Queued: m_queuedCount--; break;
                case EntryState.Active: m_activeCount--; break;
                case EntryState.Served: if (entry.HasSlot) m_heldCount--; break;
            }
            if (entry.HasSlot)
            {
                entry.HasSlot = false;
                m_world.ReleaseSingleChunkLoad(entry.Key);
                if (entry.State == EntryState.Served) ReleasedServed++;
            }
        }

        private void Compact()
        {
            if (m_entries.Count == 0) return;
            m_dead.Clear();
            foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
                if (pair.Value.Removed) m_dead.Add(pair.Key);
            for (int i = 0; i < m_dead.Count; i++) m_entries.Remove(m_dead[i]);
        }

        /// <summary>冷却表只在超过在册上限时清理过期项（有界，不在热路径扫全表）。</summary>
        private void PruneCooldowns(long nowMs)
        {
            if (m_cooldowns.Count <= m_options.MaxPending) return;
            m_expired.Clear();
            foreach (KeyValuePair<ChunkKey, long> pair in m_cooldowns)
                if (pair.Value <= nowMs) m_expired.Add(pair.Key);
            for (int i = 0; i < m_expired.Count; i++) m_cooldowns.Remove(m_expired[i]);
        }

        private void NoteRejected(ChunkKey key, string reason, RejectKind kind)
        {
            Rejected++;
            m_rejectedSinceLog++;
            switch (kind)
            {
                case RejectKind.Cap: RejectedCap++; m_rejectedCapSinceLog++; break;
                case RejectKind.Cooldown: RejectedCooldown++; m_rejectedCooldownSinceLog++; break;
                default: RejectedEngine++; m_rejectedEngineSinceLog++; break;
            }
            if (m_rejectKeys.Count < m_options.MaxKeysPerLog && !m_rejectKeys.Contains(key))
                m_rejectKeys.Add(key);
            if (!string.IsNullOrEmpty(reason) && m_rejectReasons.Count < 4 &&
                !m_rejectReasons.Contains(reason)) m_rejectReasons.Add(reason);
        }

        /// <summary>超时行：每 tick 至多一行，点名 ≤ MaxKeysPerLog 块（其余写"等 N 块"），并给出原因。</summary>
        private void AppendTimeoutLine()
        {
            var builder = new StringBuilder(260);
            builder.Append("按需单块加载超时：");
            int listed = Math.Min(m_timedOutKeys.Count, m_options.MaxKeysPerLog);
            for (int i = 0; i < listed; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(m_timedOutKeys[i]);
            }
            if (m_timedOutKeys.Count > listed)
                builder.Append(" 等 ").Append(m_timedOutKeys.Count).Append(" 块");
            builder.Append(" → **明确放弃这一轮**（撤掉临时位置，").Append(m_options.CooldownMs / 1000)
                .Append("s 内不再自动请求；这些上报的裁决不再挂着，客户端按 NotReady 退避重问）")
                .Append(" · 原因：");
            int reasons = 0;
            for (int i = 0; i < m_timedOutReasons.Count && reasons < 3; i++)
            {
                string reason = m_timedOutReasons[i];
                if (string.IsNullOrEmpty(reason)) continue;
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                {
                    if (!string.Equals(m_timedOutReasons[j], reason, StringComparison.Ordinal)) continue;
                    duplicate = true;
                    break;
                }
                if (duplicate) continue;
                if (reasons > 0) builder.Append("；");
                builder.Append(reason);
                reasons++;
            }
            if (reasons == 0) builder.Append("(未记录)");
            builder.Append(" · 累计 onDemandTimeouts ").Append(Timeouts)
                .Append("（onDemandLoadTimeouts）· pendingOnDemand ").Append(Pending)
                .Append(" · onDemandQueued ").Append(Queued)
                .Append(" · onDemandActive ").Append(Active)
                .Append(" · onDemandHeld ").Append(Held);
            Append(builder.ToString());
        }

        /// <summary>拒绝原因行（按 LogIntervalMs 限流，点名块键与原因，计数照常累加）。</summary>
        private void FlushRejectLine(long nowMs)
        {
            if (m_rejectedSinceLog == 0) return;
            if (m_lastRejectLogMs != 0 && nowMs - m_lastRejectLogMs < m_options.LogIntervalMs) return;
            m_lastRejectLogMs = nowMs;
            var builder = new StringBuilder(260);
            builder.Append("按需单块加载被拒绝：最近 ")
                .Append((m_options.LogIntervalMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture))
                .Append("s 共 ").Append(m_rejectedSinceLog).Append(" 次（台账满 ").Append(m_rejectedCapSinceLog)
                .Append(" · cooldown ").Append(m_rejectedCooldownSinceLog)
                .Append(" · engine ").Append(m_rejectedEngineSinceLog).Append("）· 区块 ");
            if (m_rejectKeys.Count == 0) builder.Append("(未点名)");
            for (int i = 0; i < m_rejectKeys.Count; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(m_rejectKeys[i]);
            }
            builder.Append(" · 原因：")
                .Append(m_rejectReasons.Count == 0 ? "(未记录)" : string.Join("；", m_rejectReasons))
                .Append(" → 这些上报只用 NotReady 回答、**不挂待裁决**（主机明确不为它发起加载，绝不假装还会裁决）")
                .Append(" · 累计 onDemandRejected ").Append(Rejected)
                .Append("（onDemandLoadRejected）");
            Append(builder.ToString());
            m_rejectedSinceLog = 0;
            m_rejectedCapSinceLog = 0;
            m_rejectedCooldownSinceLog = 0;
            m_rejectedEngineSinceLog = 0;
            m_rejectKeys.Clear();
            m_rejectReasons.Clear();
        }

        /// <summary>汇总行：计数有变化才写，且按 LogIntervalMs 限流（点名本窗口服务 / 等待中的块与按身份排队）。</summary>
        private void MaybeSummary(long nowMs)
        {
            bool changed = Requests != m_lastSummaryRequests || Served != m_lastSummaryServed ||
                Timeouts != m_lastSummaryTimeouts || Rejected != m_lastSummaryRejected ||
                Deferred != m_lastSummaryDeferred ||
                ReleasedUnderPressure != m_lastSummaryUnderPressure;
            if (!changed) return;
            if (m_lastSummaryMs != 0 && nowMs - m_lastSummaryMs < m_options.LogIntervalMs) return;
            m_lastSummaryMs = nowMs;
            long deltaRequests = Requests - m_lastSummaryRequests;
            long deltaServed = Served - m_lastSummaryServed;
            long deltaTimeouts = Timeouts - m_lastSummaryTimeouts;
            long deltaRejected = Rejected - m_lastSummaryRejected;
            long deltaDeferred = Deferred - m_lastSummaryDeferred;
            long deltaPressure = ReleasedUnderPressure - m_lastSummaryUnderPressure;
            m_lastSummaryRequests = Requests;
            m_lastSummaryServed = Served;
            m_lastSummaryTimeouts = Timeouts;
            m_lastSummaryRejected = Rejected;
            m_lastSummaryDeferred = Deferred;
            m_lastSummaryUnderPressure = ReleasedUnderPressure;
            var builder = new StringBuilder(300);
            builder.Append("按需单块加载：onDemandQueued ").Append(Queued)
                .Append("（在册 ").Append(Pending).Append(" · onDemandActive ").Append(Active)
                .Append(" · onDemandHeld ").Append(Held).Append('）')
                .Append(" · onDemandDeferred +").Append(deltaDeferred)
                .Append("（累计 ").Append(Deferred).Append('）');
            if (deltaPressure > 0)
            {
                builder.Append(" · onDemandHeldReleasedUnderPressure +").Append(deltaPressure)
                    .Append("（累计 ").Append(ReleasedUnderPressure)
                    .Append("；排队有压力时让出已过最短保留窗口的临时位置）");
            }
            builder
                .Append(" · onDemandServed +").Append(deltaServed)
                .Append("（onDemandLoadServed，累计 ").Append(Served);
            if (m_servedKeys.Count > 0)
            {
                builder.Append("，本窗口 ");
                int listed = Math.Min(m_servedKeys.Count, m_options.MaxKeysPerLog);
                for (int i = 0; i < listed; i++)
                {
                    if (i > 0) builder.Append('/');
                    builder.Append(m_servedKeys[i]);
                }
                if (m_servedKeys.Count > listed)
                    builder.Append(" 等 ").Append(m_servedKeys.Count).Append(" 块");
            }
            builder.Append('）').Append(" · onDemandTimeouts +").Append(deltaTimeouts)
                .Append("（onDemandLoadTimeouts，累计 ").Append(Timeouts).Append('）')
                .Append(" · onDemandRejected +").Append(deltaRejected)
                .Append("（onDemandLoadRejected，累计 ").Append(Rejected).Append("：cap ").Append(RejectedCap)
                .Append(" · cooldown ").Append(RejectedCooldown).Append(" · engine ").Append(RejectedEngine)
                .Append('）').Append(" · onDemandLoadRequests +").Append(deltaRequests)
                .Append("（累计 ").Append(Requests).Append("，区块 ").Append(Chunks).Append('）')
                .Append(" · pendingOnDemand ").Append(Pending);
            if (Queued > 0)
            {
                AppendQueueOwners(builder);
            }
            if (Pending > 0)
            {
                m_pendingKeys.Clear();
                foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
                {
                    if (pair.Value.Removed || pair.Value.State == EntryState.Served) continue;
                    if (m_pendingKeys.Count >= m_options.MaxKeysPerLog) break;
                    m_pendingKeys.Add(pair.Key.ToString());
                }
                if (m_pendingKeys.Count > 0)
                    builder.Append(" · 等待中 ").Append(string.Join("/", m_pendingKeys));
            }
            builder.Append("（临时单块加载位置只在等待 / 保留窗口内存在，**不动**任何角色的兴趣半径与内容距离）");
            Append(builder.ToString());
        }

        /// <summary>汇总行里的"按身份排队"：最多点名 MaxKeysPerLog 个身份（超出写"等 N 个身份"）。</summary>
        private void AppendQueueOwners(StringBuilder builder)
        {
            m_queueOwnerCounts.Clear();
            foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
            {
                Entry entry = pair.Value;
                if (entry.Removed || entry.State != EntryState.Queued) continue;
                if (entry.Owners.Count == 0)
                {
                    AddOwnerCount(NoOwner);
                    continue;
                }
                for (int i = 0; i < entry.Owners.Count; i++) AddOwnerCount(entry.Owners[i]);
            }
            if (m_queueOwnerCounts.Count == 0) return;
            builder.Append(" · 按身份排队 ");
            int listed = 0;
            foreach (KeyValuePair<string, int> pair in m_queueOwnerCounts)
            {
                if (listed >= m_options.MaxKeysPerLog) break;
                if (listed > 0) builder.Append(" / ");
                builder.Append(string.IsNullOrEmpty(pair.Key) ? "(无归属)" : pair.Key)
                    .Append(' ').Append(pair.Value);
                listed++;
            }
            if (m_queueOwnerCounts.Count > listed)
                builder.Append(" 等 ").Append(m_queueOwnerCounts.Count).Append(" 个身份");
        }

        private void AddOwnerCount(string owner)
        {
            string key = owner ?? NoOwner;
            m_queueOwnerCounts.TryGetValue(key, out int count);
            m_queueOwnerCounts[key] = count + 1;
        }

        private void Append(string line)
        {
            if (m_lines.Count >= 8) return;      // 每 tick 至多 8 行（防一次上报潮刷屏）
            m_lines.Add(line);
        }
    }
}

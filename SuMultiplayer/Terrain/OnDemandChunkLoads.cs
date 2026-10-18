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
    /// </summary>
    public sealed class OnDemandLoadOptions
    {
        /// <summary>
        /// 同时在册的按需加载块数上限（= 主机侧临时单块加载位置的上限）。
        /// 满了就**明确拒绝**新请求（计数 + 限流原因行），绝不挤掉正在等待的块、也绝不静默排队。
        /// </summary>
        public int MaxPending { get; set; } = 64;

        /// <summary>
        /// 单次 Tick 最多向引擎补发几个加载请求（重试）。首个请求在收到上报时立即发出（受
        /// <see cref="MaxPending"/> 约束），所以"每 tick 的请求数"这一项只约束重试。
        /// </summary>
        public int RequestsPerTick { get; set; } = 8;

        /// <summary>第一次重试前的退避（毫秒），之后每次翻倍。</summary>
        public int RetryBaseMs { get; set; } = 1000;

        /// <summary>退避上限（毫秒）。</summary>
        public int RetryMaxMs { get; set; } = 4000;

        /// <summary>单块最多向引擎请求几次（含首次）。</summary>
        public int MaxAttempts { get; set; } = 5;

        /// <summary>单块从首次请求起算的总等待上限（毫秒）：到点仍未拿到内容 → 明确超时放弃。</summary>
        public int TotalTimeoutMs { get; set; } = 20000;

        /// <summary>
        /// 明确放弃后的冷却时间（毫秒）：这段时间内不再为同一块自动发起加载（新的上报只回 NotReady）。
        /// 冷却是有界的：到点后客户端再上报（或玩家真的把主机带过去）会重新尝试。
        /// </summary>
        public int CooldownMs { get; set; } = 60000;

        /// <summary>
        /// 内容到达（可裁决）后临时加载位置的**最短保留**时间（毫秒）：给"裁决 → 回传快照编码"留出窗口，
        /// 免得块在编码前被引擎回收（那会退化成 NotReady → 客户端重报 → 又加载的循环）。
        /// </summary>
        public int ServedMinHoldMs { get; set; } = 10000;

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
    /// 现在的做法（**只加"这一块"，不加范围**）：
    ///   · 收到一条"主机没有内容"的上报 → <see cref="Request"/>：为**这一个**块建一条台账记录，
    ///     并通过 <see cref="ITerrainWorld.TryRequestSingleChunkLoad"/> 让引擎挂一个**只覆盖该块**的
    ///     临时地形更新位置（中心 = 该块中心，内容距离 = 8 格 → 命中集合恒为该块本身）；
    ///   · 内容到达 → 台账把它标成 <c>Served</c> 并交给宿主**立刻裁决**（不需要玩家移动）；
    ///   · 没到 → 按 <see cref="OnDemandLoadOptions.RetryBaseMs"/> 起的指数退避重试，最多
    ///     <see cref="OnDemandLoadOptions.MaxAttempts"/> 次 / <see cref="OnDemandLoadOptions.TotalTimeoutMs"/>；
    ///   · 超时 / 引擎明确拒绝 → **明确放弃这一轮**：撤掉临时位置、写一行点名原因、进冷却（有界），
    ///     宿主要把这条上报从待裁决里摘掉（绝不"永远挂着等一个永远不来的内容"）；
    ///   · 内容到达后临时位置只保留"最短保留 / 不再需要"这段窗口，之后撤掉
    ///     （<see cref="ITerrainWorld.ReleaseSingleChunkLoad"/>），主机不会长期维持一圈更大的范围；
    ///   · 客户端离开（<see cref="ReleaseOwner"/>）/ 会话结束（<see cref="Clear"/>）→ 立刻撤掉它的全部请求。
    ///
    /// 有界性：在册块数 ≤ <see cref="OnDemandLoadOptions.MaxPending"/>，每 tick 引擎请求数 ≤
    /// <see cref="OnDemandLoadOptions.RequestsPerTick"/>，每 tick 的台账扫描是 O(在册块数)（≤ 64 次字典查找）。
    /// 日志限流：超时行每 tick 至多一行（点名 ≤ MaxKeysPerLog 块），拒绝行与汇总行按 LogIntervalMs 限流。
    /// </summary>
    public sealed class OnDemandChunkLoads
    {
        /// <summary>一条按需加载记录（**块**是主语；同一块可被多个身份同时需要，见 <see cref="Entry.Owners"/>）。</summary>
        private sealed class Entry
        {
            public ChunkKey Key;
            /// <summary>需要这块的身份稳定键（<see cref="PlayerSessionRecord.StableKey"/>）。空 = 没有身份归属。</summary>
            public readonly List<string> Owners = new List<string>(1);
            /// <summary>首次登记时刻（TickCount64 口径）。</summary>
            public long FirstMs;
            /// <summary>已经交给引擎几次（不含"已经有内容所以没请求"的情况）。</summary>
            public int Requests;
            /// <summary>下一次允许重试的时刻。</summary>
            public long NextRequestMs;
            /// <summary>内容已到位（可以裁决），等待回收窗口。</summary>
            public bool Served;
            public long ServedMs;
            /// <summary>本 tick 内被判定要删除（不在遍历字典时改字典）。</summary>
            public bool Removed;
        }

        private enum RejectKind { Cap, Cooldown, Engine }

        private readonly ITerrainWorld m_world;
        private readonly OnDemandLoadOptions m_options;
        private readonly Dictionary<ChunkKey, Entry> m_entries = new Dictionary<ChunkKey, Entry>();
        /// <summary>明确放弃的块 → 冷却到期的时刻（有界：只在超过上限时清理过期项）。</summary>
        private readonly Dictionary<ChunkKey, long> m_cooldowns = new Dictionary<ChunkKey, long>();
        private readonly List<ChunkKey> m_timedOutKeys = new List<ChunkKey>(8);
        private readonly List<ChunkKey> m_servedKeys = new List<ChunkKey>(8);
        private readonly List<ChunkKey> m_rejectKeys = new List<ChunkKey>(8);
        private readonly List<string> m_rejectReasons = new List<string>(4);
        private readonly List<string> m_pendingKeys = new List<string>(8);
        private readonly List<ChunkKey> m_expired = new List<ChunkKey>(8);
        private readonly List<ChunkKey> m_dead = new List<ChunkKey>(8);
        private readonly List<string> m_lines = new List<string>(4);

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

        public OnDemandChunkLoads(ITerrainWorld world, OnDemandLoadOptions options = null)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_options = options ?? new OnDemandLoadOptions();
            if (m_options.MaxPending < 1) m_options.MaxPending = 1;
            if (m_options.RequestsPerTick < 1) m_options.RequestsPerTick = 1;
            if (m_options.MaxAttempts < 1) m_options.MaxAttempts = 1;
            if (m_options.RetryBaseMs < 1) m_options.RetryBaseMs = 1;
            if (m_options.RetryMaxMs < m_options.RetryBaseMs) m_options.RetryMaxMs = m_options.RetryBaseMs;
            if (m_options.TotalTimeoutMs < m_options.RetryBaseMs) m_options.TotalTimeoutMs = m_options.RetryBaseMs;
            if (m_options.CooldownMs < 0) m_options.CooldownMs = 0;
            if (m_options.ServedMinHoldMs < 0) m_options.ServedMinHoldMs = 0;
            if (m_options.ServedMaxHoldMs < m_options.ServedMinHoldMs)
                m_options.ServedMaxHoldMs = m_options.ServedMinHoldMs;
            if (m_options.LogIntervalMs < 1) m_options.LogIntervalMs = 1;
            if (m_options.MaxKeysPerLog < 1) m_options.MaxKeysPerLog = 1;
        }

        // ------------------------------------------------------------ 计数器（全部只增，口径写在这里）

        /// <summary>真正交给引擎的加载请求**次数**（含重试；同一块重试 3 次 = 3）。</summary>
        public long Requests { get; private set; }
        /// <summary>台账接受、真的向引擎发起过加载（或当时已有内容）的区块**数**（重试不重复计）。</summary>
        public long Chunks { get; private set; }
        /// <summary>内容真正到位、可以裁决的区块数。</summary>
        public long Served { get; private set; }
        /// <summary>超时放弃的区块数（每一轮只算一次）。</summary>
        public long Timeouts { get; private set; }
        /// <summary>被拒绝的请求次数（= cap + cooldown + engine 三项之和）。</summary>
        public long Rejected { get; private set; }
        /// <summary>因为"在册块数已达上限"被拒绝的次数。</summary>
        public long RejectedCap { get; private set; }
        /// <summary>因为"上一轮超时/引擎拒绝后的冷却还没过"被拒绝的次数。</summary>
        public long RejectedCooldown { get; private set; }
        /// <summary>因为引擎明确不接受（世界未加载 / 没有 TerrainUpdater / 临时位置满了）被拒绝的次数。</summary>
        public long RejectedEngine { get; private set; }
        /// <summary>内容到位后按需撤掉的临时加载次数（服务完成 / 不再需要）。</summary>
        public long ReleasedServed { get; private set; }
        /// <summary>当前在册（等待中 + 服务中）的按需加载块数 = pendingOnDemand。</summary>
        public int Pending => m_entries.Count;

        /// <summary>状态行口径（测试与状态行都读这一份，不另立一份计数）。</summary>
        public string Describe() =>
            "onDemandLoadRequests " + Requests + "（区块 " + Chunks + "）· onDemandLoadServed " + Served +
            " · onDemandLoadTimeouts " + Timeouts + " · onDemandLoadRejected " + Rejected +
            "（cap " + RejectedCap + " · cooldown " + RejectedCooldown + " · engine " + RejectedEngine +
            "）· pendingOnDemand " + Pending;

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
                long waited = nowMs - entry.FirstMs;
                long next = Math.Max(0L, entry.NextRequestMs - nowMs);
                text = "按需单块加载：" + (entry.Served ? "内容已到位，等待回收窗口" :
                        "已请求引擎 " + entry.Requests + " 次 / 已等 " +
                        (waited / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s / " +
                        (next > 0 ? next + "ms 后重试" : "下一拍重试") +
                        "（上限 " + m_options.MaxAttempts + " 次 · " + m_options.TotalTimeoutMs + "ms）") +
                    " · 归属 " + (entry.Owners.Count == 0 ? "none" : string.Join(",", entry.Owners));
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
        /// 返回 false = **明确拒绝**（原因写 <paramref name="rejectedReason"/>，调用方不得把这条上报
        /// 挂进"待裁决"——那块没有人会去加载，挂上就是永远裁决不了的黄框）。
        /// </summary>
        public bool Request(ChunkKey key, string owner, long nowMs, out string rejectedReason)
        {
            rejectedReason = null;
            if (m_entries.TryGetValue(key, out Entry existing))
            {
                AddOwner(existing, owner);
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
                rejectedReason = "并发上限 " + m_options.MaxPending + " 已满（不挤掉正在等待的块）";
                NoteRejected(key, rejectedReason, RejectKind.Cap);
                return false;
            }
            var entry = new Entry { Key = key, FirstMs = nowMs };
            AddOwner(entry, owner);
            m_entries[key] = entry;
            if (m_world.HasContents(key))
            {
                // 已经有内容（调用方刚查过，这里做一次并发下的二次确认）：不请求引擎，下一拍 Tick 判 Served。
                Chunks++;
                return true;
            }
            if (TryIssue(entry, nowMs, out string engineReason))
            {
                Chunks++;
                return true;
            }
            // 引擎明确不接受 → **不占位、不留待裁决**：撤掉记录、进冷却（不反复砸同一个坏引擎）、如实计数。
            m_entries.Remove(key);
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
                if (entry.Owners.Count > 0) continue;
                Drop(entry);
                removed = true;
                released++;
            }
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
            return count;
        }

        // ------------------------------------------------------------ 每 tick 推进

        /// <summary>
        /// 推进台账（主线程每 tick 一次，宿主已持有自己的锁）：
        ///   · 内容到了 → 标 <c>Served</c> + 放进 <paramref name="served"/>（宿主**立刻裁决**这些块）；
        ///   · 没到且退避到点 → 预算内重发引擎请求；到上限 / 超时 → 放进 <paramref name="timedOut"/>；
        ///   · 已服务的块 → 过了保留窗口 / 不再被需要 → 撤掉临时加载位置。
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
            m_servedKeys.Clear();
            int issued = 0;
            bool removed = false;
            foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
            {
                Entry entry = pair.Value;
                if (entry.Removed) { removed = true; continue; }
                if (entry.Served)
                {
                    // 内容在保留窗口内又被引擎回收了（别处触发卸载 / 换世界）：回到"等待加载"，
                    // 下一拍按退避重新请求 —— 绝不停在"已服务"状态假装主机还拿着内容。
                    if (!m_world.HasContents(entry.Key))
                    {
                        // 新一轮：重试预算从头算（上一轮已经真的把内容交付过一次了）。
                        entry.Served = false;
                        entry.ServedMs = 0L;
                        entry.FirstMs = nowMs;
                        entry.Requests = 0;
                        entry.NextRequestMs = nowMs;
                        continue;
                    }
                    long held = nowMs - entry.ServedMs;
                    bool needed = stillNeeded != null && stillNeeded(entry.Key);
                    if (held >= m_options.ServedMaxHoldMs ||
                        (held >= m_options.ServedMinHoldMs && !needed))
                    {
                        Drop(entry);
                        removed = true;
                    }
                    continue;
                }
                if (m_world.HasContents(entry.Key))
                {
                    entry.Served = true;
                    entry.ServedMs = nowMs;
                    Served++;
                    m_servedKeys.Add(entry.Key);
                    served?.Add(entry.Key);
                    continue;
                }
                if (nowMs < entry.NextRequestMs) continue;
                if (entry.Requests >= m_options.MaxAttempts ||
                    nowMs - entry.FirstMs >= m_options.TotalTimeoutMs)
                {
                    Timeout(entry, nowMs, null, timedOut);
                    removed = true;
                    continue;
                }
                if (issued >= m_options.RequestsPerTick) continue;      // 本拍预算用完：下一拍再发（不丢）
                if (TryIssue(entry, nowMs, out string engineReason))
                {
                    issued++;
                    continue;
                }
                // 重试时引擎明确不接受：同一出口（明确放弃 + 冷却 + 一行原因），绝不无限重试。
                Timeout(entry, nowMs, engineReason, timedOut);
                removed = true;
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

        /// <summary>交一次请求给引擎；返回 false 时 <paramref name="reason"/> 是引擎给出的原因。</summary>
        private bool TryIssue(Entry entry, long nowMs, out string reason)
        {
            reason = null;
            if (!m_world.TryRequestSingleChunkLoad(entry.Key, out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = "引擎没有给出原因";
                return false;
            }
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

        /// <summary>超时放弃一轮：撤位置 + 计数 + 冷却 + 点名（宿主随后把这条上报从待裁决里摘掉）。</summary>
        private void Timeout(Entry entry, long nowMs, string engineReason, List<ChunkKey> timedOut)
        {
            if (!string.IsNullOrEmpty(engineReason))
                NoteRejected(entry.Key, engineReason, RejectKind.Engine);
            Drop(entry);
            Timeouts++;
            m_cooldowns[entry.Key] = nowMs + m_options.CooldownMs;
            m_timedOutKeys.Add(entry.Key);
            timedOut?.Add(entry.Key);
        }

        /// <summary>撤掉一条记录的临时加载位置（真正交给引擎的回收动作只有这一句）。</summary>
        private void Drop(Entry entry)
        {
            if (entry.Removed) return;
            entry.Removed = true;
            if (entry.Requests > 0) m_world.ReleaseSingleChunkLoad(entry.Key);
            if (entry.Served) ReleasedServed++;
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

        /// <summary>超时行：每 tick 至多一行，点名 ≤ MaxKeysPerLog 块（其余写"等 N 块"）。</summary>
        private void AppendTimeoutLine()
        {
            var builder = new StringBuilder(200);
            builder.Append("按需单块加载超时：");
            int listed = Math.Min(m_timedOutKeys.Count, m_options.MaxKeysPerLog);
            for (int i = 0; i < listed; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(m_timedOutKeys[i]);
            }
            if (m_timedOutKeys.Count > listed)
                builder.Append(" 等 ").Append(m_timedOutKeys.Count).Append(" 块");
            builder.Append(" 已请求引擎 ").Append(m_options.MaxAttempts).Append(" 次 / 等满 ")
                .Append(m_options.TotalTimeoutMs).Append("ms 仍未拿到内容 → **明确放弃这一轮**")
                .Append("（撤掉临时单块加载位置，").Append(m_options.CooldownMs / 1000)
                .Append("s 内不再自动请求；这些上报的裁决不再挂着，客户端按 NotReady 退避重问）")
                .Append(" · 累计 onDemandLoadTimeouts ").Append(Timeouts)
                .Append(" · pendingOnDemand ").Append(Pending);
            Append(builder.ToString());
        }

        /// <summary>拒绝原因行（按 LogIntervalMs 限流，点名块键与原因，计数照常累加）。</summary>
        private void FlushRejectLine(long nowMs)
        {
            if (m_rejectedSinceLog == 0) return;
            if (m_lastRejectLogMs != 0 && nowMs - m_lastRejectLogMs < m_options.LogIntervalMs) return;
            m_lastRejectLogMs = nowMs;
            var builder = new StringBuilder(220);
            builder.Append("按需单块加载被拒绝：最近 ")
                .Append((m_options.LogIntervalMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture))
                .Append("s 共 ").Append(m_rejectedSinceLog).Append(" 次（cap ").Append(m_rejectedCapSinceLog)
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
                .Append(" · 累计 onDemandLoadRejected ").Append(Rejected);
            Append(builder.ToString());
            m_rejectedSinceLog = 0;
            m_rejectedCapSinceLog = 0;
            m_rejectedCooldownSinceLog = 0;
            m_rejectedEngineSinceLog = 0;
            m_rejectKeys.Clear();
            m_rejectReasons.Clear();
        }

        /// <summary>汇总行：计数有变化才写，且按 LogIntervalMs 限流（点名本窗口服务 / 等待中的块）。</summary>
        private void MaybeSummary(long nowMs)
        {
            bool changed = Requests != m_lastSummaryRequests || Served != m_lastSummaryServed ||
                Timeouts != m_lastSummaryTimeouts || Rejected != m_lastSummaryRejected;
            if (!changed) return;
            if (m_lastSummaryMs != 0 && nowMs - m_lastSummaryMs < m_options.LogIntervalMs) return;
            m_lastSummaryMs = nowMs;
            long deltaRequests = Requests - m_lastSummaryRequests;
            long deltaServed = Served - m_lastSummaryServed;
            long deltaTimeouts = Timeouts - m_lastSummaryTimeouts;
            long deltaRejected = Rejected - m_lastSummaryRejected;
            m_lastSummaryRequests = Requests;
            m_lastSummaryServed = Served;
            m_lastSummaryTimeouts = Timeouts;
            m_lastSummaryRejected = Rejected;
            var builder = new StringBuilder(240);
            builder.Append("按需单块加载：onDemandLoadRequests +").Append(deltaRequests)
                .Append("（累计 ").Append(Requests).Append("，区块 ").Append(Chunks).Append("）")
                .Append(" · onDemandLoadServed +").Append(deltaServed).Append("（累计 ").Append(Served);
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
            builder.Append('）').Append(" · onDemandLoadTimeouts +").Append(deltaTimeouts)
                .Append("（累计 ").Append(Timeouts).Append('）')
                .Append(" · onDemandLoadRejected +").Append(deltaRejected)
                .Append("（累计 ").Append(Rejected).Append("：cap ").Append(RejectedCap)
                .Append(" · cooldown ").Append(RejectedCooldown).Append(" · engine ").Append(RejectedEngine)
                .Append('）').Append(" · pendingOnDemand ").Append(Pending);
            if (Pending > 0)
            {
                m_pendingKeys.Clear();
                foreach (KeyValuePair<ChunkKey, Entry> pair in m_entries)
                {
                    if (pair.Value.Removed || pair.Value.Served) continue;
                    if (m_pendingKeys.Count >= m_options.MaxKeysPerLog) break;
                    m_pendingKeys.Add(pair.Key.ToString());
                }
                if (m_pendingKeys.Count > 0)
                    builder.Append(" · 等待中 ").Append(string.Join("/", m_pendingKeys));
            }
            builder.Append("（临时单块加载位置只在等待 / 保留窗口内存在，**不动**任何角色的兴趣半径与内容距离）");
            Append(builder.ToString());
        }

        private void Append(string line)
        {
            if (m_lines.Count >= 8) return;      // 每 tick 至多 8 行（防一次上报潮刷屏）
            m_lines.Add(line);
        }
    }
}

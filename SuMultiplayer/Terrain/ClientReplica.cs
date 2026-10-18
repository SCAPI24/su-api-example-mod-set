using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Engine;
using SuMultiplayer.Net;

using Google.Protobuf;
using PA = SuMultiplayer.Protocol.Authority;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// "客户端只是副本"的纯策略层（**不依赖引擎**，P:\tmp\tsverify 直接编译本文件做真实 TCP 自检）。
    ///
    /// 架构决定：**主机是唯一权威，客户端是副本**。
    ///   · 客户端不跑任何会改地形的世界模拟（流体/火/天气/生长/爆炸……）；
    ///   · 客户端对地形的写入只有两个来源：主机 <c>CellChange</c> / <c>ChunkResync</c>，
    ///     以及"玩家编辑的请求回执"（主机批准后同样经 CellChange 到达）；
    ///   · 玩家编辑先由客户端捕获为 <see cref="ClientEditRequest"/> 发给主机，
    ///     主机校验（区块已加载 / 距离 / 方块合法性 / 权限）后走自己的权威路径落地并广播；
    ///     被拒绝或超时就回到主机最后的值（拒绝回执携带权威值，超时走强制核对）。
    ///
    /// 为什么这些逻辑放在 Terrain/ 而不是 Engine/：它们全是纯策略（没有 Game 类型），
    /// 放在这里就能与 TerrainSync 用同一份真实源码跑离线自检，不会出现"测试里复制一份实现"。
    /// </summary>
    public enum ReplicaMutationSource : byte
    {
        Fluid = 0,
        Magma = 1,
        Fire = 2,
        Weather = 3,
        Grass = 4,
        Plant = 5,
        DeciduousLeaves = 6,
        Cactus = 7,
        Rot = 8,
        Carpet = 9,
        FallenLeaves = 10,
        Soil = 11,
        Ivy = 12,
        Sapling = 13,
        Explosion = 14,
        CreatureSpawn = 15,
        Spawn = 16,
        Electricity = 17,
        Unknown = 18,
        /// <summary>
        /// 爆炸物引信（火药桶的倒计时由主机写进格子 data 并下发；客户端只表现、不倒计时）。
        /// 见 <see cref="FuseCellState"/> 与 Engine/ClientReplicaSubsystems.cs 的引信外壳。
        /// </summary>
        Fuse = 19,
        /// <summary>
        /// 塌落物理：客户端不跑 SubsystemCollapsingBlockBehavior（OnNeighborBlockChanged / MovingBlocks
        /// 的 Stopped / CollidedWithTerrain 都会 ChangeCell / DestroyCell）；只收主机的最终格。
        /// 见 Engine/ClientReplicaSubsystems.cs 的 SuSubsystemCollapsingBlockBehavior。
        /// </summary>
        Collapsing = 20,
        /// <summary>营火燃料烧尽（SubsystemCampfireBlockBehavior.Update / OnChunkDiscarding 写 data）。</summary>
        Campfire = 21,
        /// <summary>草陷阱受击后破坏周边（SubsystemGrassTrapBlockBehavior.Update → DestroyCell）。</summary>
        GrassTrap = 22,
        /// <summary>树叶失去原木后的腐烂（SubsystemWoodBlockBehavior.Update → ChangeCell）。</summary>
        WoodDecay = 23,
        /// <summary>宝藏生成器被邻接触发后自毁 + 生成拾取物（SubsystemTreasureGeneratorBlockBehavior）。</summary>
        Treasure = 24,
        /// <summary>延迟写格队列（SubsystemCellChangeQueue.Update → ChangeCell）。</summary>
        CellChangeQueue = 25,
        /// <summary>方块轮询扫描驱动（SubsystemBlocksScanner.Update → OnPoll，世界演化定时器入口）。</summary>
        BlocksScanner = 26,
        /// <summary>
        /// 活塞运动表现（主机把推 / 拉的中间过程作为 PistonMoveEvent 下发；客户端只建本地
        /// 只表现集合，不跑活塞逻辑、不写格）。见 Engine/ClientReplicaSubsystems.cs 的活塞外壳。
        /// </summary>
        Piston = 27
    }

    public static class ReplicaMutationSourceNames
    {
        private static readonly string[] s_names =
        {
            "fluid", "magma", "fire", "weather", "grass", "plant", "deciduous-leaves",
            "cactus", "rot", "carpet", "fallen-leaves", "soil", "ivy", "sapling",
            "explosion", "creature-spawn", "spawn", "electricity", "unknown", "fuse",
            "collapsing", "campfire", "grass-trap", "wood-decay", "treasure",
            "cell-change-queue", "blocks-scanner", "piston"
        };

        public static string Of(ReplicaMutationSource source)
        {
            int index = (int)source;
            return index >= 0 && index < s_names.Length ? s_names[index] : "unknown";
        }

        public static int Count => s_names.Length;
    }

    /// <summary>
    /// 客户端世界模拟闸门：**唯一判定入口**（引擎侧每个会改地形的更新都问它）。
    /// 主机 / 单机 = 恒允许；客户端副本 = 恒拒绝并计数（每类一个计数器，可审计、可限流打印）。
    /// 实例状态挂在注入后的 <c>SuSubsystemTerrain</c> 上（**没有静态字段**）。
    /// </summary>
    public sealed class ReplicaSimulationGate
    {
        private readonly long[] m_suppressed = new long[ReplicaMutationSourceNames.Count];
        private long m_allowed;
        private long m_denied;

        /// <summary>本端是不是"联机客户端副本"。由模块在角色确定后每帧写入。</summary>
        public bool ClientReplica { get; set; }

        public long Allowed => m_allowed;
        public long Denied => m_denied;
        public bool AnythingDenied => m_denied > 0;

        /// <summary>引擎侧更新在**改任何格子之前**调用；返回 false = 本帧不要推进模拟。</summary>
        public bool Allow(ReplicaMutationSource source)
        {
            if (!ClientReplica)
            {
                m_allowed++;
                return true;
            }
            int index = (int)source;
            if (index >= 0 && index < m_suppressed.Length) m_suppressed[index]++;
            m_denied++;
            return false;
        }

        public long Suppressed(ReplicaMutationSource source)
        {
            int index = (int)source;
            return index >= 0 && index < m_suppressed.Length ? m_suppressed[index] : 0;
        }

        public void Reset()
        {
            Array.Clear(m_suppressed, 0, m_suppressed.Length);
            m_allowed = 0;
            m_denied = 0;
        }

        /// <summary>可审计的一行（只列非零项；没有否决就是 "none"）。</summary>
        public string Describe()
        {
            var builder = new StringBuilder(96);
            builder.Append("replica=").Append(ClientReplica ? "client" : "authority");
            builder.Append(" allowed=").Append(m_allowed.ToString(CultureInfo.InvariantCulture));
            builder.Append(" denied=").Append(m_denied.ToString(CultureInfo.InvariantCulture));
            builder.Append(" [");
            bool any = false;
            for (int i = 0; i < m_suppressed.Length; i++)
            {
                if (m_suppressed[i] == 0) continue;
                if (any) builder.Append(',');
                any = true;
                builder.Append(ReplicaMutationSourceNames.Of((ReplicaMutationSource)i));
                builder.Append('=').Append(m_suppressed[i].ToString(CultureInfo.InvariantCulture));
            }
            if (!any) builder.Append("none");
            builder.Append(']');
            return builder.ToString();
        }
    }

    /// <summary>客户端捕获到的一次本地地形写入（contents + data，光照不算）。</summary>
    public readonly struct ClientCellEdit
    {
        public ClientCellEdit(int x, int y, int z, int contents, int data)
        {
            X = x; Y = y; Z = z; Contents = contents; Data = data;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Contents { get; }
        public int Data { get; }
        public ChunkKey Key => new ChunkKey(X >> 4, Z >> 4);
    }

    /// <summary>
    /// 客户端捕获暂存里一格被**明确丢弃**的原因。每一种都必须出现在状态行里（禁止静默丢）。
    /// </summary>
    public enum ClientCaptureDropReason : byte
    {
        /// <summary>世界 / Project 已经卸载：这些格子属于旧世界，不可能再上报（重试就是串到新世界）。</summary>
        WorldUnloaded = 0,
        /// <summary>本端角色切换（客户端 ⇄ 主机 / 重连）：旧角色下的待捕获作废。</summary>
        RoleChanged = 1,
        /// <summary>待捕获超过上限：按"最旧优先"丢弃并计数（绝不无限增长）。</summary>
        Overflow = 2,
        /// <summary>连续 <see cref="ClientEditCaptureQueue.MaxRetryFrames"/> 帧都读不到地形 / 没有订阅者。</summary>
        Stale = 3,
        /// <summary>模块下线。</summary>
        Detached = 4,
        /// <summary>坐标越界（引擎不允许 y &lt; 0 或 y &gt; 255；不该出现，出现就计数）。</summary>
        InvalidCoordinate = 5,
        Count = 6
    }

    public static class ClientCaptureDropReasonNames
    {
        private static readonly string[] s_names =
        {
            "worldUnloaded", "roleChanged", "overflow", "stale", "detached", "badCoord"
        };

        public static string Of(ClientCaptureDropReason reason)
        {
            int index = (int)reason;
            return index >= 0 && index < s_names.Length ? s_names[index] : "unknown";
        }

        public static int Count => s_names.Length;
    }

    /// <summary>
    /// 客户端"本地地形写入 → CellEditRequest"的**捕获暂存**（纯托管，P:\tmp\tsverify 直接编译本文件自检）。
    ///
    /// 为什么必须有这一层（实机丢编辑根因）：
    ///   · 引擎的 <c>SubsystemTerrain.m_modifiedCells</c> 会被**多个** SubsystemFluidBlockBehavior
    ///     派生类（水 / 岩浆 / 水下 / 水草 / 海参）在各自 Update 里 <c>ProcessModifiedCells()</c> 清掉
    ///     （Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:312）。玩家挖/放写在 Default 序，
    ///     地形更新是 Terrain 序（100）；清表若落在两者之间，那一格在被捕获之前就没了；
    ///   · 捕获那一帧如果读不到 Project / Terrain（世界切换、引用瞬时失效）或没有订阅者，
    ///     旧实现直接 return，调用方随后照常 ProcessModifiedCells 清表 ⇒ 本地写入永远丢失。
    ///
    /// 所以这里把"要捕获的格子"**跨帧保留**：值在真正读得到的那一刻才读；读不到就继续等，
    /// 只有世界卸载 / 角色切换 / 超过重试上限 / 超过容量才**明确丢弃并计数**（每种原因一个计数器）。
    /// 纯策略、无静态状态。
    /// </summary>
    public sealed class ClientEditCaptureQueue
    {
        /// <summary>待捕获上限（正常一帧最多几格；超过是异常，必须计数并限流告警）。</summary>
        public const int MaxPending = 4096;
        /// <summary>最多重试多少帧（60fps ≈ 2s）：再读不到就明确丢弃并计数。</summary>
        public const int MaxRetryFrames = 120;

        private struct PendingCell
        {
            public long FirstFrame;
            public long LastFrame;
        }

        private readonly Dictionary<Point3, PendingCell> m_pending =
            new Dictionary<Point3, PendingCell>();
        private readonly long[] m_dropped = new long[(int)ClientCaptureDropReason.Count];
        private readonly List<Point3> m_expired = new List<Point3>(8);

        /// <summary>累计"从引擎改动表/接住集合里看到的格子"（每格每帧一次）。</summary>
        public long Staged { get; private set; }
        /// <summary>累计成功读值并交给模块的格子数。</summary>
        public long Delivered { get; private set; }
        /// <summary>累计"这一帧没能读/没能交付、保留到下一帧"的次数（不是格数）。</summary>
        public long DeferredFrames { get; private set; }
        /// <summary>当前还留着等待重试的格数。</summary>
        public int PendingCount => m_pending.Count;

        public long Dropped(ClientCaptureDropReason reason)
        {
            int index = (int)reason;
            return index >= 0 && index < m_dropped.Length ? m_dropped[index] : 0;
        }

        /// <summary>把这一帧的格子放进暂存（同一格只保留一份；值在真正读得到时才读）。返回新增格数。</summary>
        public int Stage(IEnumerable<Point3> cells, long frame)
        {
            if (cells == null) return 0;
            int added = 0;
            foreach (Point3 cell in cells)
            {
                if (cell.Y < 0 || cell.Y > 255)
                {
                    m_dropped[(int)ClientCaptureDropReason.InvalidCoordinate]++;
                    continue;
                }
                Staged++;
                if (m_pending.TryGetValue(cell, out PendingCell existing))
                {
                    existing.LastFrame = frame;
                    m_pending[cell] = existing;
                    continue;
                }
                if (m_pending.Count >= MaxPending) DropOldest();
                m_pending[cell] = new PendingCell { FirstFrame = frame, LastFrame = frame };
                added++;
            }
            return added;
        }

        /// <summary>扫描一遍待捕获格（调用方负责不要边扫边改）。</summary>
        public void CopyPending(List<Point3> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();
            foreach (Point3 cell in m_pending.Keys) output.Add(cell);
        }

        /// <summary>一格已经读值并交给模块：从暂存移除（不存在返回 false，不重复计数）。</summary>
        public bool Complete(Point3 cell)
        {
            if (!m_pending.Remove(cell)) return false;
            Delivered++;
            return true;
        }

        /// <summary>这一帧没能读值 / 没能交付：保留格子，下一帧重试（只记一次"帧"，不是丢格）。</summary>
        public void NoteDeferredFrame() => DeferredFrames++;

        /// <summary>超过重试上限的格子：移除并计入 <see cref="ClientCaptureDropReason.Stale"/>。返回丢弃格数。</summary>
        public int Expire(long frame)
        {
            if (m_pending.Count == 0) return 0;
            m_expired.Clear();
            foreach (KeyValuePair<Point3, PendingCell> pair in m_pending)
            {
                if (frame - pair.Value.FirstFrame < MaxRetryFrames) continue;
                m_expired.Add(pair.Key);
            }
            for (int i = 0; i < m_expired.Count; i++) m_pending.Remove(m_expired[i]);
            int dropped = m_expired.Count;
            if (dropped > 0) m_dropped[(int)ClientCaptureDropReason.Stale] += dropped;
            return dropped;
        }

        /// <summary>放弃全部待捕获格（世界卸载 / 角色切换 / 模块下线）：按原因计数，返回丢弃格数。</summary>
        public int Reset(ClientCaptureDropReason reason)
        {
            int dropped = m_pending.Count;
            m_pending.Clear();
            if (dropped > 0)
            {
                int index = (int)reason;
                if (index >= 0 && index < m_dropped.Length) m_dropped[index] += dropped;
            }
            return dropped;
        }

        private void DropOldest()
        {
            if (m_pending.Count == 0) return;
            Point3 oldest = default(Point3);
            long oldestFrame = long.MaxValue;
            foreach (KeyValuePair<Point3, PendingCell> pair in m_pending)
            {
                if (pair.Value.FirstFrame >= oldestFrame) continue;
                oldestFrame = pair.Value.FirstFrame;
                oldest = pair.Key;
            }
            m_pending.Remove(oldest);
            m_dropped[(int)ClientCaptureDropReason.Overflow]++;
        }

        /// <summary>可审计的一行：每一级计数 + 每种丢弃原因（禁止静默丢）。</summary>
        public string Describe()
        {
            var builder = new StringBuilder(192);
            builder.Append("captureStaged=").Append(Staged.ToString(CultureInfo.InvariantCulture));
            builder.Append(" captureDelivered=").Append(Delivered.ToString(CultureInfo.InvariantCulture));
            builder.Append(" captureDeferredFrames=").Append(DeferredFrames.ToString(CultureInfo.InvariantCulture));
            builder.Append(" capturePending=").Append(m_pending.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(" captureDropped[");
            bool any = false;
            for (int i = 0; i < m_dropped.Length; i++)
            {
                if (m_dropped[i] == 0) continue;
                if (any) builder.Append(',');
                any = true;
                builder.Append(ClientCaptureDropReasonNames.Of((ClientCaptureDropReason)i));
                builder.Append('=').Append(m_dropped[i].ToString(CultureInfo.InvariantCulture));
            }
            if (!any) builder.Append("none");
            builder.Append(']');
            return builder.ToString();
        }
    }

    /// <summary>
    /// 一条待确认的客户端编辑请求（客户端侧）。
    /// 序号 <see cref="Sequence"/> 每条连接从 1 开始单调递增；主机按 (clientId, seq) 去重。
    /// </summary>
    public sealed class ClientEditRequest
    {
        public long Sequence;
        public int X;
        public int Y;
        public int Z;
        public int Contents;
        public int Data;
        /// <summary>第一次发送的时刻（毫秒，调用方时钟口径）。</summary>
        public long FirstSentMs;
        /// <summary>最近一次发送的时刻（毫秒）。</summary>
        public long LastSentMs;
        /// <summary>已经发送过几次（含第一次）。</summary>
        public int Attempts;
        public ChunkKey Key => new ChunkKey(X >> 4, Z >> 4);
    }

    /// <summary>
    /// 客户端编辑请求的待确认表：
    ///   · <see cref="Begin"/> 登记一次捕获（有界；超过上限 = 明确计数 + 拒绝登记，不静默丢）；
    ///   · <see cref="CollectDue"/> 按重发间隔给出要重发的请求（丢包 / 回执丢失都能自愈）；
    ///   · <see cref="CollectExpired"/> 超过总时限的请求（调用方负责回滚 + 强制核对 + 写日志）；
    ///   · <see cref="Confirm"/> / <see cref="Reject"/> 由主机回执驱动。
    /// </summary>
    public sealed class ClientEditTracker
    {
        /// <summary>同时在途的上限（正常玩家操作远低于它；超出是异常，必须计数）。</summary>
        public const int MaxPending = 256;

        private readonly Dictionary<long, ClientEditRequest> m_pending =
            new Dictionary<long, ClientEditRequest>();
        private readonly List<ClientEditRequest> m_due = new List<ClientEditRequest>();
        private readonly List<ClientEditRequest> m_expired = new List<ClientEditRequest>();
        private long m_nextSequence = 1;

        /// <summary>重发间隔（毫秒）：250ms 对局域网足够，又不至于把通道灌满。</summary>
        public int ResendIntervalMs { get; set; } = 250;

        /// <summary>总时限（毫秒）：超过就回滚并强制核对（默认 5s）。</summary>
        public int TimeoutMs { get; set; } = 5000;

        /// <summary>同一请求最多发送几次（含第一次）；到达上限后按超时处理。</summary>
        public int MaxAttempts { get; set; } = 12;

        public long Submitted { get; private set; }
        public long Confirmed { get; private set; }
        public long Rejected { get; private set; }
        public long Expired { get; private set; }
        public long Resent { get; private set; }
        public long Overflow { get; private set; }
        public long UnknownResult { get; private set; }

        public int PendingCount => m_pending.Count;
        public long NextSequence => m_nextSequence;

        /// <summary>下一次分配出去的序号（测试用：可读回实际发出去的 seq）。</summary>
        public long LastAllocatedSequence { get; private set; }

        /// <summary>登记一次本地编辑；返回 null = 超过上限（<see cref="Overflow"/> 已计数）。</summary>
        public ClientEditRequest Begin(int x, int y, int z, int contents, int data, long nowMs)
        {
            if (m_pending.Count >= MaxPending)
            {
                Overflow++;
                return null;
            }
            long sequence = m_nextSequence;
            m_nextSequence = sequence == long.MaxValue ? 1L : sequence + 1L;
            var request = new ClientEditRequest
            {
                Sequence = sequence,
                X = x,
                Y = y,
                Z = z,
                Contents = contents,
                Data = data,
                FirstSentMs = nowMs,
                LastSentMs = nowMs,
                Attempts = 1
            };
            m_pending[sequence] = request;
            LastAllocatedSequence = sequence;
            Submitted++;
            return request;
        }

        /// <summary>到点要重发的请求（只读列表，复用内部缓冲；调用方立刻消费）。</summary>
        public IReadOnlyList<ClientEditRequest> CollectDue(long nowMs)
        {
            m_due.Clear();
            if (m_pending.Count == 0) return m_due;
            foreach (ClientEditRequest request in m_pending.Values)
            {
                if (nowMs - request.FirstSentMs >= TimeoutMs) continue;      // 交给 CollectExpired
                if (request.Attempts >= MaxAttempts) continue;
                if (nowMs - request.LastSentMs < ResendIntervalMs) continue;
                request.LastSentMs = nowMs;
                request.Attempts++;
                Resent++;
                m_due.Add(request);
            }
            return m_due;
        }

        /// <summary>超过总时限 / 重发次数用尽的请求：从表里移除并返回（调用方负责回滚）。</summary>
        public IReadOnlyList<ClientEditRequest> CollectExpired(long nowMs)
        {
            m_expired.Clear();
            if (m_pending.Count == 0) return m_expired;
            List<long> remove = null;
            foreach (ClientEditRequest request in m_pending.Values)
            {
                bool timeout = nowMs - request.FirstSentMs >= TimeoutMs;
                bool exhausted = request.Attempts >= MaxAttempts &&
                    nowMs - request.FirstSentMs >= ResendIntervalMs;
                if (!timeout && !exhausted) continue;
                (remove ?? (remove = new List<long>(4))).Add(request.Sequence);
            }
            if (remove == null) return m_expired;
            for (int i = 0; i < remove.Count; i++)
            {
                if (!m_pending.TryGetValue(remove[i], out ClientEditRequest request)) continue;
                m_pending.Remove(remove[i]);
                m_expired.Add(request);
                Expired++;
            }
            return m_expired;
        }

        /// <summary>主机批准（或主机回执说"值已经等于请求值"）：移除该请求。</summary>
        public bool Confirm(long sequence)
        {
            if (!m_pending.Remove(sequence)) return false;
            Confirmed++;
            return true;
        }

        /// <summary>主机明确拒绝：移除并计数（回滚由调用方用回执里的权威值执行）。</summary>
        public bool Reject(long sequence)
        {
            if (!m_pending.Remove(sequence)) return false;
            Rejected++;
            return true;
        }

        /// <summary>主机回执的 seq 不在表里（重复回执 / 已超时 / 伪造）：计数，不猜这是哪一格。</summary>
        public void NoteUnknownResult(long sequence)
        {
            _ = sequence;
            UnknownResult++;
        }

        public void Clear()
        {
            m_pending.Clear();
            m_due.Clear();
            m_expired.Clear();
        }

        public string Describe()
        {
            return "submitted=" + Submitted.ToString(CultureInfo.InvariantCulture) +
                " confirmed=" + Confirmed.ToString(CultureInfo.InvariantCulture) +
                " rejected=" + Rejected.ToString(CultureInfo.InvariantCulture) +
                " expired=" + Expired.ToString(CultureInfo.InvariantCulture) +
                " resent=" + Resent.ToString(CultureInfo.InvariantCulture) +
                " overflow=" + Overflow.ToString(CultureInfo.InvariantCulture) +
                " unknown=" + UnknownResult.ToString(CultureInfo.InvariantCulture) +
                " pending=" + m_pending.Count.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// CellEditRequest（客户端 → 主机，proto：Protocol/authority.proto）。
    /// sequence 必须 > 0；重复序号由主机按上一次裁决幂等返回。
    /// </summary>
    public static class CellEditRequestCodec
    {
        public static byte[] Encode(long sequence, int x, int y, int z, int contents, int data)
        {
            var message = new PA.CellEditRequest
            {
                Sequence = sequence,
                X = x,
                Y = y,
                Z = z,
                Contents = contents,
                Data = data
            };
            return message.ToByteArray();
        }

        public static byte[] Encode(ClientCellEdit edit, long sequence) =>
            Encode(sequence, edit.X, edit.Y, edit.Z, edit.Contents, edit.Data);

        public static bool TryDecode(byte[] payload, out long sequence, out int x, out int y, out int z,
            out int contents, out int data)
        {
            sequence = 0; x = 0; y = 0; z = 0; contents = 0; data = 0;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PA.CellEditRequest message = PA.CellEditRequest.Parser.ParseFrom(payload);
                sequence = message.Sequence;
                x = message.X;
                y = message.Y;
                z = message.Z;
                contents = message.Contents;
                data = message.Data;
                return sequence > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static class CellEditResultCodec
    {
        public static byte[] Encode(long sequence, bool accepted, int x, int y, int z,
            int contents, int data, string reason, bool hasValue = true)
        {
            var message = new PA.CellEditResult
            {
                Sequence = sequence,
                Accepted = accepted,
                X = x,
                Y = y,
                Z = z,
                Contents = contents,
                Data = data,
                Reason = reason ?? string.Empty,
                HasValue = hasValue
            };
            return message.ToByteArray();
        }

        public static bool TryDecode(byte[] payload, out long sequence, out bool accepted,
            out int x, out int y, out int z, out int contents, out int data, out string reason,
            out bool hasValue)
        {
            sequence = 0; accepted = false; x = 0; y = 0; z = 0; contents = 0; data = 0;
            reason = string.Empty;
            hasValue = true;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PA.CellEditResult message = PA.CellEditResult.Parser.ParseFrom(payload);
                sequence = message.Sequence;
                accepted = message.Accepted;
                x = message.X;
                y = message.Y;
                z = message.Z;
                contents = message.Contents;
                data = message.Data;
                reason = message.Reason ?? string.Empty;
                hasValue = message.HasValue;
                return sequence > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>主机侧校验 / 落地要问世界的事实（测试用合成世界实现；引擎侧由 TerrainBridge 实现）。</summary>
    public interface IEditAuthorityWorld
    {
        /// <summary>这一块的内容是否可用（主机已加载）。</summary>
        bool HasContents(ChunkKey key);
        /// <summary>读当前值（contents + data）。</summary>
        bool TryReadCell(int x, int y, int z, out int contents, out int data);
        /// <summary>
        /// 按主机的权威路径落地（引擎里就是 <c>SubsystemTerrain.ChangeCell</c> 那条路）。
        ///
        /// 额外参数只服务于"被销毁方块的掉落"（见 <see cref="EditApplyOutcome"/>）：
        ///   · <paramref name="clientId"/> = 发起编辑的玩家端（主机生成掉落时给它登记拾取宽限）；
        ///   · <paramref name="toolLevel"/>/<paramref name="hasToolLevel"/> = 该玩家手持物的
        ///     <c>Block.ToolLevel</c>（与 <c>ComponentMiner.Dig</c> 传给 <c>GetDropValues</c> 的口径一致）。
        /// 引擎侧实现必须在 **改格之前** 调 <c>GetDropValues + OnItemHarvested</c>（可编辑方块的数据
        /// 会在 ChangeCell 的 OnBlockRemoved 里被删掉），改格之后再 <c>AddPickable</c>。
        /// </summary>
        bool TryApplyCell(int clientId, int x, int y, int z, int contents, int data,
            int toolLevel, bool hasToolLevel,
            out EditApplyOutcome outcome, out string reason);
    }

    /// <summary>主机对一条编辑请求的裁决。</summary>
    public sealed class ClientEditDecision
    {
        public long Sequence;
        public bool Accepted;
        public int X;
        public int Y;
        public int Z;
        /// <summary>决策后该格的权威 value（拒绝时 = 客户端应回到的值）。</summary>
        public int Contents;
        public int Data;
        public string Reason = string.Empty;
        /// <summary>true = 重复序号，原样重发上一次的裁决（幂等，不重复落地）。</summary>
        public bool Duplicate;
        /// <summary>
        /// <see cref="Contents"/>/<see cref="Data"/> 是否是**读到的权威值**。
        /// false（区块不在主机内存 / 坐标越界读不到）= 客户端不能用 0/0 回滚，必须走强制核对。
        /// </summary>
        public bool HasValue;
    }

    /// <summary>
    /// 主机侧编辑裁决器（纯策略）：**唯一入口**，负责
    ///   ① 解析载荷（畸形 = 明确拒绝，不猜）；
    ///   ② 每连接序号去重 / 重放安全（重复序号返回上一次裁决，不重复落地）；
    ///   ③ 校验：区块已加载、距离（相对该客户端兴趣中心）、方块合法性、y 范围、只收玩家端；
    ///   ④ 落地（交给世界实现的权威路径），并把决策后的值回给调用方。
    ///
    /// 主机把改动广播给**所有**客户端是既有 CellChange 广播链的职责：这里落地后，
    /// 主机引擎的 m_modifiedCells 会捕获它并由 TerrainSyncHost.BroadcastCellChange 下发。
    /// </summary>
    public sealed class ClientEditAuthority
    {
        private sealed class ClientState
        {
            public long LastSequence;
            public ClientEditDecision LastDecision;
        }

        private readonly IEditAuthorityWorld m_world;
        private readonly Dictionary<int, ClientState> m_clients = new Dictionary<int, ClientState>();

        /// <summary>方块销毁掉落账本（dropFromEditSpawned / Skipped[reason] / Mismatch）。</summary>
        public EditDropLedger Drops { get; } = new EditDropLedger();

        /// <summary>
        /// 主机解析"该客户端手持物的 ToolLevel"（引擎侧由组合根注入：读联机化身权威背包的活跃槽）。
        /// 返回 false = 化身 / 背包还没就绪 → 掉落明确记 no-tool-level，不猜 0。
        /// </summary>
        public delegate bool ClientToolLevelResolver(int clientId, out int toolLevel);

        /// <summary>ToolLevel 解析器（组合根注入；null = 拿不到 → 记 no-tool-level）。</summary>
        public ClientToolLevelResolver ToolLevels { get; set; }

        /// <summary>方块索引上限（= BlocksManager.Blocks.Length；由引擎侧注入）。</summary>
        public int BlockCount { get; set; } = 1024;

        /// <summary>距离上限（格）：客户端只能改自己兴趣窗口内的格子，外加这个边距。</summary>
        public int MaxDistanceMargin { get; set; } = 16;

        /// <summary>允许的最大序号跳跃（防伪造序号把去重状态推到未来）。</summary>
        public long MaxSequenceJump { get; set; } = 4096;

        public long Requests { get; private set; }
        public long Accepted { get; private set; }
        public long Rejected { get; private set; }
        public long Duplicates { get; private set; }
        public long Malformed { get; private set; }
        public long OutOfRange { get; private set; }
        public long NotPlayer { get; private set; }
        public long ApplyFailures { get; private set; }
        public long ChunkNotLoaded { get; private set; }

        public ClientEditAuthority(IEditAuthorityWorld world)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>连接建立 / 断开时调用（序号按**连接**归档；重连是新 clientId，序号从头开始）。</summary>
        public void ResetClient(int clientId)
        {
            m_clients.Remove(clientId);
        }

        public void Clear()
        {
            m_clients.Clear();
        }

        /// <summary>
        /// 处理一条原始载荷。
        /// <paramref name="isPlayerPeer"/> = false（调试端）一律拒绝；
        /// <paramref name="hasRange"/> = false（还没上报兴趣中心）也拒绝 —— 没有距离依据就不批。
        /// </summary>
        public ClientEditDecision Handle(int clientId, byte[] payload, bool isPlayerPeer,
            bool hasRange, int centerCellX, int centerCellZ, int radiusChunks)
        {
            if (!CellEditRequestCodec.TryDecode(payload, out long sequence, out int x, out int y,
                out int z, out int contents, out int data))
            {
                Requests++;
                Malformed++;
                var malformed = new ClientEditDecision
                {
                    Reason = "载荷非法（protobuf 解析失败 / sequence 非正）"
                };
                Rejected++;
                return malformed;
            }
            return Handle(clientId, new PA.CellEditRequest
            {
                Sequence = sequence, X = x, Y = y, Z = z, Contents = contents, Data = data
            }, isPlayerPeer, hasRange, centerCellX, centerCellZ, radiusChunks);
        }

        /// <summary>强类型入口（MessageRegistry 分发用）：不再"解析→再序列化→再解析"。</summary>
        public ClientEditDecision Handle(int clientId, PA.CellEditRequest request, bool isPlayerPeer,
            bool hasRange, int centerCellX, int centerCellZ, int radiusChunks)
        {
            Requests++;
            var decision = new ClientEditDecision();
            if (!isPlayerPeer)
            {
                NotPlayer++;
                decision.Reason = "不是玩家端（调试端只读）";
                Rejected++;
                return decision;
            }
            long sequence = request?.Sequence ?? 0;
            int x = request?.X ?? 0, y = request?.Y ?? 0, z = request?.Z ?? 0;
            int contents = request?.Contents ?? 0, data = request?.Data ?? 0;
            if (sequence <= 0)
            {
                Malformed++;
                decision.Reason = "载荷非法（sequence 必须 > 0）";
                Rejected++;
                return decision;
            }
            decision.Sequence = sequence;
            decision.X = x;
            decision.Y = y;
            decision.Z = z;
            if (!m_clients.TryGetValue(clientId, out ClientState state))
            {
                state = new ClientState();
                m_clients[clientId] = state;
            }
            if (sequence <= state.LastSequence)
            {
                if (sequence == state.LastSequence && state.LastDecision != null)
                {
                    Duplicates++;
                    // 幂等重放：原样返回上一次裁决（客户端重发 / 回执丢失都能收敛）。
                    return state.LastDecision;
                }
                Rejected++;
                decision.Reason = "序号过期（已处理到 " + state.LastSequence.ToString(CultureInfo.InvariantCulture) + "）";
                state.LastDecision = decision;
                return decision;
            }
            if (sequence - state.LastSequence > MaxSequenceJump)
            {
                Rejected++;
                decision.Reason = "序号跳跃过大（+" + (sequence - state.LastSequence).ToString(CultureInfo.InvariantCulture) +
                    "，上限 " + MaxSequenceJump.ToString(CultureInfo.InvariantCulture) + "）";
                state.LastSequence = sequence;
                state.LastDecision = decision;
                return decision;
            }
            state.LastSequence = sequence;

            if (y <= 0 || y >= 255)
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "y 越界（只允许 1..254）：" + y.ToString(CultureInfo.InvariantCulture);
            }
            else if (contents < 0 || contents >= BlockCount)
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "方块索引非法：" + contents.ToString(CultureInfo.InvariantCulture) +
                    "（0.." + (BlockCount - 1).ToString(CultureInfo.InvariantCulture) + "）";
            }
            else if (data < 0 || data > 65535)
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "方块 data 越界：" + data.ToString(CultureInfo.InvariantCulture);
            }
            else if (!hasRange)
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "该客户端还没有上报兴趣中心 → 没有距离依据，不批准编辑";
            }
            else if (!InRange(x, z, centerCellX, centerCellZ, radiusChunks))
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "距离过远（格 " + x.ToString(CultureInfo.InvariantCulture) + "," +
                    z.ToString(CultureInfo.InvariantCulture) + " 不在兴趣窗口中心 " +
                    centerCellX.ToString(CultureInfo.InvariantCulture) + "," +
                    centerCellZ.ToString(CultureInfo.InvariantCulture) + " 半径 " +
                    radiusChunks.ToString(CultureInfo.InvariantCulture) + " 内）";
            }
            else
            {
                var key = new ChunkKey(x >> 4, z >> 4);
                if (!m_world.HasContents(key))
                {
                    Rejected++;
                    ChunkNotLoaded++;
                    decision.Reason = "区块未加载：" + key.X.ToString(CultureInfo.InvariantCulture) + "," +
                        key.Z.ToString(CultureInfo.InvariantCulture);
                }
                else if (!m_world.TryReadCell(x, y, z, out int currentContents, out int currentData))
                {
                    Rejected++;
                    ChunkNotLoaded++;
                    decision.Reason = "读不到该格当前值（区块不在内存）";
                }
                else if (currentContents == contents && currentData == data)
                {
                    // 已经是目标值：批准（幂等），权威值就是当前值。
                    Accepted++;
                    decision.Accepted = true;
                    decision.HasValue = true;
                    decision.Contents = currentContents;
                    decision.Data = currentData;
                    decision.Reason = "already";
                }
                else
                {
                    // 这次请求要销毁 / 替换一个非空气方块吗？（只有此时才需要 ToolLevel 算掉落）
                    bool destroying = currentContents != 0 && currentContents != contents;
                    int toolLevel = 0;
                    bool hasToolLevel = false;
                    if (destroying && ToolLevels != null)
                        hasToolLevel = ToolLevels(clientId, out toolLevel);
                    if (!m_world.TryApplyCell(clientId, x, y, z, contents, data, toolLevel,
                            hasToolLevel, out EditApplyOutcome outcome, out string applyReason))
                    {
                        Rejected++;
                        ApplyFailures++;
                        decision.Reason = "主机落地失败：" + (applyReason ?? "unknown");
                        if (m_world.TryReadCell(x, y, z, out int afterContents, out int afterData))
                        {
                            decision.HasValue = true;
                            decision.Contents = afterContents;
                            decision.Data = afterData;
                        }
                    }
                    else
                    {
                        Accepted++;
                        decision.Accepted = true;
                        decision.HasValue = true;
                        decision.Contents = outcome.ResultContents;
                        decision.Data = outcome.ResultData;
                        decision.Reason = "ok";
                        // 方块销毁掉落账本：spawned（个数）/ skipped（原因）/ mismatch（两端分歧）。
                        Drops.Record(in outcome);
                    }
                }
            }
            if (!decision.Accepted && !decision.HasValue &&
                m_world.TryReadCell(x, y, z, out int rejectedContents, out int rejectedData))
            {
                // 拒绝也要带**主机当前权威值**：客户端的回滚目标就是它（否则只能等整块回传）。
                decision.HasValue = true;
                decision.Contents = rejectedContents;
                decision.Data = rejectedData;
            }
            state.LastDecision = decision;
            return decision;
        }

        private bool InRange(int x, int z, int centerCellX, int centerCellZ, int radiusChunks)
        {
            int limit = Math.Max(1, radiusChunks) * 16 + MaxDistanceMargin;
            return Math.Abs(x - centerCellX) <= limit && Math.Abs(z - centerCellZ) <= limit;
        }

        public string Describe()
        {
            return "requests=" + Requests.ToString(CultureInfo.InvariantCulture) +
                " accepted=" + Accepted.ToString(CultureInfo.InvariantCulture) +
                " rejected=" + Rejected.ToString(CultureInfo.InvariantCulture) +
                " duplicates=" + Duplicates.ToString(CultureInfo.InvariantCulture) +
                " malformed=" + Malformed.ToString(CultureInfo.InvariantCulture) +
                " outOfRange=" + OutOfRange.ToString(CultureInfo.InvariantCulture) +
                " notPlayer=" + NotPlayer.ToString(CultureInfo.InvariantCulture) +
                " chunkNotLoaded=" + ChunkNotLoaded.ToString(CultureInfo.InvariantCulture) +
                " applyFailures=" + ApplyFailures.ToString(CultureInfo.InvariantCulture) +
                " · " + Drops.Describe();
        }
    }
    /// <summary>
    /// 客户端"塌落"副本计数器：**实例状态**（挂在 SuSubsystemTerrain 上，没有静态字段）。
    ///   · Gated            = 客户端拒绝本地塌落行为（OnNeighborBlockChanged / 移动组落地）的次数；
    ///   · LandingSkipped   = 客户端看到主机复制的 "CollapsingBlock" 移动组落地，但**不写格**的次数
    ///                        （最终格由主机 CellChange 下发，视觉由移动组复制负责）；
    ///   · TablesCleared    = 客户端清掉的引擎内部工作表条目（本引擎版本没有 m_toMove；
    ///                        若未来版本加了表，这里会计数，不静默）。
    /// collapsingSkipped = TablesCleared（用户口径：清掉的引擎工作表）；LandingSkipped 单独列出
    /// （客户端看到主机复制的移动组落地但只做表现）。状态行两个数都写，避免混口径。
    /// </summary>
    public sealed class CollapsingReplicaState
    {
        private long m_gated;
        private long m_landingSkipped;
        private long m_tablesCleared;

        public long Gated => m_gated;
        public long LandingSkipped => m_landingSkipped;
        public long TablesCleared => m_tablesCleared;
        /// <summary>清掉的引擎工作表（本版本没有 m_toMove；未来版本加表会计数）。</summary>
        public long Skipped => m_tablesCleared;

        public void NoteGated() => m_gated++;
        public void NoteLandingSkipped() => m_landingSkipped++;
        public void NoteTablesCleared(int count)
        {
            if (count > 0) m_tablesCleared += count;
        }

        public string Describe()
        {
            return "collapsingGated=" + m_gated.ToString(CultureInfo.InvariantCulture) +
                " collapsingSkipped=" + Skipped.ToString(CultureInfo.InvariantCulture) +
                " (tables=" + m_tablesCleared.ToString(CultureInfo.InvariantCulture) +
                " landing=" + m_landingSkipped.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
    /// 客户端"活塞副本"计数器：**实例状态**（挂在 SuSubsystemTerrain 上，没有静态字段）。
    ///   · BaseSubscriptionsDetached = 从 SubsystemMovingBlocks 的 Stopped / CollidedWithTerrain 上
    ///     摘掉的**引擎基类私有写格委托**数（SubsystemPistonBlockBehavior.Load:172-173 的
    ///     MovingBlocksStopped / MovingBlocksCollidedWithTerrain）；
    ///   · ClientShellBaseWriteAttempts = 客户端拦下的、本来会走引擎基类写格路径的次数
    ///     （base 的停止动作队列 / OnChunkDiscarding / OnBlockRemoved，以及 SuSubsystemMovingBlocks
    ///     的移动组 base 写格窗口恢复的格）；ClientBasePistonWriteAttempt = 其中活塞来源；
    ///     ClientShellBaseWriteNotReverted = 拦下但没能恢复原值的次数（明确失败，不静默）；
    ///   · StraySetsRemoved = 下载世界残留 / 主机已拆活塞的纯表现移动组被移除的条数（只
    ///     RemoveMovingBlockSet，不写任何格）。
    /// 全部是"客户端闸门"口径，主机 / 单机恒为 0。
    /// </summary>
    public sealed class PistonReplicaState
    {
        private long m_baseSubscriptionsDetached;
        private long m_clientShellBaseWriteAttempts;
        private long m_clientBasePistonWriteAttempts;
        private long m_clientShellBaseWriteNotReverted;
        private long m_straySetsRemoved;
        private long m_detachFailures;

        public long BaseSubscriptionsDetached => m_baseSubscriptionsDetached;
        public long ClientShellBaseWriteAttempts => m_clientShellBaseWriteAttempts;
        /// <summary>其中活塞来源（Id=="Piston"）的次数（item 3 的 clientBasePistonWriteAttempt）。</summary>
        public long ClientBasePistonWriteAttempts => m_clientBasePistonWriteAttempts;
        /// <summary>拦下但**没能恢复原值**的次数（明确失败，不静默当作已拦下）。</summary>
        public long ClientShellBaseWriteNotReverted => m_clientShellBaseWriteNotReverted;
        public long StraySetsRemoved => m_straySetsRemoved;
        public long DetachFailures => m_detachFailures;

        public void NoteBaseSubscriptionsDetached(int count)
        {
            if (count > 0) m_baseSubscriptionsDetached += count;
        }

        /// <summary>兼容旧调用点：现有调用点（base 停止队列 / OnBlockRemoved / 区块卸载）全是活塞来源。</summary>
        public void NoteClientShellBaseWriteAttempts(int count) =>
            NoteClientShellBaseWriteAttempts(count, piston: true);

        /// <summary>底座的 ChangeCell/DestroyCell 路径在客户端被拦下（piston = 活塞移动组来源）。</summary>
        public void NoteClientShellBaseWriteAttempts(int count, bool piston)
        {
            if (count <= 0) return;
            m_clientShellBaseWriteAttempts += count;
            if (piston) m_clientBasePistonWriteAttempts += count;
        }

        public void NoteClientShellBaseWriteNotReverted() => m_clientShellBaseWriteNotReverted++;

        public void NoteStraySetsRemoved(int count)
        {
            if (count > 0) m_straySetsRemoved += count;
        }

        public void NoteDetachFailure() => m_detachFailures++;

        public string Describe()
        {
            return "pistonBaseSubscriptionsDetached=" +
                m_baseSubscriptionsDetached.ToString(CultureInfo.InvariantCulture) +
                " clientShellBaseWriteAttempts=" +
                m_clientShellBaseWriteAttempts.ToString(CultureInfo.InvariantCulture) +
                " clientBasePistonWriteAttempt=" +
                m_clientBasePistonWriteAttempts.ToString(CultureInfo.InvariantCulture) +
                " clientShellBaseWriteNotReverted=" +
                m_clientShellBaseWriteNotReverted.ToString(CultureInfo.InvariantCulture) +
                " pistonStraySetsRemoved=" +
                m_straySetsRemoved.ToString(CultureInfo.InvariantCulture) +
                " pistonDetachFailures=" +
                m_detachFailures.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 客户端摘除引擎行为私有事件订阅的结果（纯反射，不依赖 Game）。
    /// <see cref="Failure"/> == null 表示**后置条件已确认**：declaringType 的委托一个都不剩。
    /// </summary>
    public sealed class ClientEventDetachResult
    {
        public ClientEventDetachResult(bool eventFound, int removed, int remaining, string failure)
        {
            EventFound = eventFound;
            Removed = removed;
            Remaining = remaining;
            Failure = failure;
        }

        public bool EventFound { get; }
        public int Removed { get; }
        public int Remaining { get; }
        public string Failure { get; }
        public bool Succeeded => Failure == null;
    }

    /// <summary>
    /// 客户端摘事件订阅的反射工具。**只摘 declaringType 声明的方法**：外壳自己的表现回调
    /// （SuSubsystemPistonBlockBehavior.OnPresentationStopped）声明在派生类，必须保留。
    /// 为什么按声明类型而不是按方法名/目标实例猜：base.Load 订阅的私有方法名随引擎版本可能变，
    /// "声明类型 + 后置条件复核（剩余 0 个）"能明确失败（日志里点名事件 / 委托），不静默兜底。
    /// Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:Load:165-174
    ///         Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs:Load:32-38
    /// </summary>
    public static class ClientReplicaEventDetach
    {
        private const BindingFlags EventFieldFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>
        /// 从 <paramref name="eventOwner"/> 的 <paramref name="eventName"/> 事件上摘掉所有
        /// 由 <paramref name="declaringType"/> 声明的方法（委托）。返回后置条件复核结果：
        /// 只有"事件存在且剩余该类委托 = 0"才算成功。
        /// </summary>
        public static ClientEventDetachResult DetachDeclaredHandlers(object eventOwner,
            string eventName, Type declaringType)
        {
            if (eventOwner == null)
                return Fail(eventName, "eventOwner=null（找不到事件宿主）");
            if (string.IsNullOrEmpty(eventName))
                return Fail(eventName, "eventName 为空");
            if (declaringType == null)
                return Fail(eventName, "declaringType=null");
            FieldInfo field = eventOwner.GetType().GetField(eventName, EventFieldFlags);
            if (field == null)
                return Fail(eventName, "事件 backing field 不存在（宿主 " +
                    eventOwner.GetType().FullName + "）");
            if (field.IsStatic)
                return Fail(eventName, "事件 backing field 是静态的（拒绝改动）");
            Delegate handlers;
            try
            {
                handlers = field.GetValue(eventOwner) as Delegate;
            }
            catch (Exception e)
            {
                return Fail(eventName, "读取事件委托失败：" + e.GetType().Name + " " + e.Message);
            }
            int removed = 0;
            if (handlers != null)
            {
                Delegate remaining = handlers;
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    if (handler?.Method == null || handler.Method.DeclaringType != declaringType)
                        continue;
                    remaining = Delegate.Remove(remaining, handler);
                    removed++;
                }
                if (removed > 0)
                {
                    try
                    {
                        field.SetValue(eventOwner, remaining);
                    }
                    catch (Exception e)
                    {
                        return Fail(eventName, "写回事件委托失败：" + e.GetType().Name + " " +
                            e.Message);
                    }
                }
            }
            Delegate after;
            try
            {
                after = field.GetValue(eventOwner) as Delegate;
            }
            catch (Exception e)
            {
                return Fail(eventName, "复核事件委托失败：" + e.GetType().Name + " " + e.Message);
            }
            int stillAttached = CountDeclaredHandlers(after, declaringType);
            if (stillAttached != 0)
                return new ClientEventDetachResult(true, removed, stillAttached,
                    "事件 '" + eventName + "' 仍挂着 " + stillAttached + " 个 " +
                    declaringType.FullName + " 委托");
            return new ClientEventDetachResult(true, removed, 0, null);
        }

        /// <summary>复核某事件上是否还挂着 <paramref name="target"/> 的指定方法（方法名可为空）。</summary>
        public static bool HasHandler(object eventOwner, string eventName, object target,
            string methodName)
        {
            if (eventOwner == null || string.IsNullOrEmpty(eventName) || target == null)
                return false;
            FieldInfo field = eventOwner.GetType().GetField(eventName, EventFieldFlags);
            if (field == null || field.IsStatic) return false;
            Delegate handlers;
            try
            {
                handlers = field.GetValue(eventOwner) as Delegate;
            }
            catch (Exception)
            {
                return false;
            }
            if (handlers == null) return false;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                if (!ReferenceEquals(handler?.Target, target)) continue;
                if (string.IsNullOrEmpty(methodName) ||
                    string.Equals(handler.Method?.Name, methodName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static int CountDeclaredHandlers(Delegate handlers, Type declaringType)
        {
            if (handlers == null) return 0;
            int count = 0;
            foreach (Delegate handler in handlers.GetInvocationList())
                if (handler?.Method != null && handler.Method.DeclaringType == declaringType) count++;
            return count;
        }

        private static ClientEventDetachResult Fail(string eventName, string reason) =>
            new ClientEventDetachResult(false, 0, 0,
                (string.IsNullOrEmpty(eventName) ? "event" : "event '" + eventName + "'") +
                "：" + reason);
    }

    /// <summary>
    /// 副本覆盖清单（**审计用**，不是运行时判定）：Pak/Database.xml 的 Subsystems 全集 → 分类。
    /// 目的：任何新增的子系统都必须在这里显式归类；`unprotectedWorldSim` 非空就说明有"会改地形却没闸门"
    /// 的世界模拟。Engine 侧在进入世界后限流打印一次完整清单（见 SuSubsystemTerrain）。
    ///
    /// 分类口径（2026-10-19 逐个读引擎源码得出）：
    ///   · Gated                    客户端不推进（外壳拒绝并计数），主机照常；
    ///   · Presentation             不改格（渲染 / 音频 / 注册表 / 实体状态 / 扫描游标）；
    ///   · DeterministicEdit        只在玩家同一次编辑的 OnBlockAdded/Removed/Neighbor 回调里写格
    ///                              （含 SubsystemElectricBlockBehavior 把邻居回调转给 ElectricElement，
    ///                              例如挂载电池失去支撑）；主机对同一条编辑会执行同一回调，
    ///                              结果同格同值（幂等），不是独立模拟；
    ///   · InteractionOnly          只在 OnUse/OnInteract/OnEditBlock/OnEditInventoryItem 里写格；
    ///                              客户端交互走主机请求（BlockUse/Authority），不本地落地；
    ///   · ElectricityDependent     只在 SubsystemElectricity 求解里写格；客户端电力已闸门；
    ///   · ProtectedByHandlerDetach 自身不写格；最终落格在别的行为的 Stopped/CollidedWithTerrain 里，
    ///                              客户端由 Piston/Collapsing 外壳摘掉这些订阅（移动组只做视觉）；
    ///   · UnprotectedWorldSim      **不应该存在**：会独立改地形却没闸门的子系统（清单非空即缺口）；
    ///   · MissingSource            引擎源码里没有该类（数据库有模板但源码缺失），无写格实现可闸门。
    /// </summary>
    public static class ClientReplicaCoverage
    {

        public static readonly string[] Gated =
        {
            "Game.SubsystemFallenLeavesBlockBehavior", "Game.SubsystemFireBlockBehavior",
            "Game.SubsystemDeciduousLeavesBlockBehavior", "Game.SubsystemMatchBlockBehavior",
            "Game.SubsystemPlantBlockBehavior", "Game.SubsystemSaplingBlockBehavior",
            "Game.SubsystemCactusBlockBehavior", "Game.SubsystemWoodBlockBehavior",
            "Game.SubsystemRotBlockBehavior", "Game.SubsystemMagmaBlockBehavior",
            "Game.SubsystemSoilBlockBehavior", "Game.SubsystemCampfireBlockBehavior",
            "Game.SubsystemMusketBlockBehavior", "Game.SubsystemGrassBlockBehavior",
            "Game.SubsystemGrassTrapBlockBehavior", "Game.SubsystemIvyBlockBehavior",
            "Game.SubsystemBottomSuckerBlockBehavior", "Game.SubsystemTreasureGeneratorBlockBehavior",
            "Game.SubsystemCrossbowBlockBehavior", "Game.SubsystemWaterPlantBlockBehavior",
            "Game.SubsystemBowBlockBehavior", "Game.SubsystemThrowableBlockBehavior",
            "Game.SubsystemFireworksBlockBehavior", "Game.SubsystemWaterBlockBehavior",
            "Game.SubsystemExplosivesBlockBehavior", "Game.SubsystemInWaterBlockBehavior",
            "Game.SubsystemCarpetBlockBehavior", "Game.SubsystemCollapsingBlockBehavior",
            "Game.SubsystemCellChangeQueue", "Game.SubsystemPickables",
            "Game.SubsystemSpawn", "Game.SubsystemWeather",
            "Game.SubsystemTerrain", "Game.SubsystemBlocksScanner",
            "Game.SubsystemCreatureSpawn", "Game.SubsystemProjectiles",
            "Game.SubsystemElectricity", "Game.SubsystemExplosions",
            // 活塞物理（由 90abe952 的 moving-block 复制工作流加外壳；客户端只做表现，
            // 最终格收主机 CellChange）。Source: SubsystemPistonBlockBehavior.cs:295-469。
            "Game.SubsystemPistonBlockBehavior",
            // 移动组驱动：客户端由 SuSubsystemMovingBlocks 外壳替换（本地只建只表现集合，
            // 出格写入被 ClientMovingBlockWriteGuard 快照/恢复并计数）。
            // Source: SubsystemMovingBlocks.cs:Update:277-353 TerrainCollision:508-535
            "Game.SubsystemMovingBlocks"
        };

        public static readonly string[] Presentation =
        {
            "Game.SubsystemNames", "Game.SubsystemPalette",
            "Game.SubsystemPlayers", "Game.SubsystemPathfinding",
            "Game.SubsystemItemsScanner", "Game.SubsystemTime",
            "Game.SubsystemUpdate", "Game.SubsystemNoise",
            "Game.SubsystemTimeOfDay", "Game.SubsystemGameInfo",
            "Game.SubsystemBodies", "Game.SubsystemSky",
            "Game.SubsystemPlayerStats", "Game.SubsystemBlockEntities",
            "Game.SubsystemSeasons", "Game.SubsystemBlockBehaviors",
            "Game.SubsystemAnimatedTextures", "Game.SubsystemGlow",
            "Game.SubsystemGameWidgets", "Game.SubsystemModelsRenderer",
            "Game.SubsystemPostprocessor", "Game.SubsystemBlocksTexture",
            "Game.SubsystemDrawing", "Game.SubsystemParticles",
            "Game.SubsystemShadows", "Game.SubsystemAudio",
            "Game.SubsystemAmbientSounds",
            // 只做撞击音效解析，不写格（SubsystemSoundMaterials.cs 无 ChangeCell/DestroyCell）。
            "Game.SubsystemSoundMaterials"
        };

        public static readonly string[] DeterministicEdit =
        {
            "Game.SubsystemSignBlockBehavior", "Game.SubsystemTorchBlockBehavior",
            "Game.SubsystemLadderBlockBehavior", "Game.SubsystemTrapdoorBlockBehavior",
            "Game.SubsystemGravestoneBlockBehavior", "Game.SubsystemSnowBlockBehavior",
            "Game.SubsystemMetersBlockBehavior", "Game.SubsystemFenceBlockBehavior",
            "Game.SubsystemMagnetBlockBehavior", "Game.SubsystemStairsBlockBehavior",
            "Game.SubsystemDoorBlockBehavior", "Game.SubsystemElectricBlockBehavior"
        };

        public static readonly string[] InteractionOnly =
        {
            "Game.SubsystemButtonBlockBehavior", "Game.SubsystemFertilizerBlockBehavior",
            "Game.SubsystemTruthTableCircuitBlockBehavior", "Game.SubsystemRakeBlockBehavior",
            "Game.SubsystemBatteryBlockBehavior", "Game.SubsystemFurnitureBlockBehavior",
            "Game.SubsystemBucketBlockBehavior", "Game.SubsystemSwitchBlockBehavior",
            "Game.SubsystemChestBlockBehavior", "Game.SubsystemMemoryBankBlockBehavior",
            "Game.SubsystemFenceGateBlockBehavior", "Game.SubsystemSaddleBlockBehavior",
            "Game.SubsystemCraftingTableBlockBehavior", "Game.SubsystemBulletBlockBehavior",
            "Game.SubsystemWhistleBlockBehavior", "Game.SubsystemFurnaceBlockBehavior",
            "Game.SubsystemArrowBlockBehavior", "Game.SubsystemAdjustableDelayGateBlockBehavior",
            "Game.SubsystemImpactExplosivesBlockBehavior", "Game.SubsystemDispenserBlockBehavior",
            "Game.SubsystemBombBlockBehavior", "Game.SubsystemEggBlockBehavior",
            "Game.SubsystemHammerBlockBehavior", "Game.SubsystemBoatBlockBehavior"
        };

        public static readonly string[] ElectricityDependent =
        {
            // 只在 SpikedPlankElectricElement.Simulate → RetractExtendSpikes 里写格；
            // 客户端 SubsystemElectricity 已闸门，求解不会发生。
            "Game.SubsystemSpikesBlockBehavior"
        };

        public static readonly string[] ProtectedByHandlerDetach =
        {
            // 空：MovingBlocks 现在由 SuSubsystemMovingBlocks 外壳驱动（客户端只做表现 + 写格守卫），
            // 已归入 Gated；保留该分类以免 TotalCount 口径变化。
        };

        public static readonly string[] MissingSource =
        {
            "Game.SubsystemLedBlockBehavior"
        };

        // 没有缺口：Database.xml 的 106 个 Subsystems 类全部显式归类。
        public static readonly string[] UnprotectedWorldSim = Array.Empty<string>();

        public static int TotalCount =>
            Gated.Length + Presentation.Length + DeterministicEdit.Length +
            InteractionOnly.Length + ElectricityDependent.Length +
            ProtectedByHandlerDetach.Length + MissingSource.Length +
            UnprotectedWorldSim.Length;

        private static string Join(string[] names)
        {
            if (names == null || names.Length == 0) return "none";
            var builder = new StringBuilder(names.Length * 48);
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(names[i]);
            }
            return builder.ToString();
        }

        /// <summary>完整一份（gated + 每个 un-gated 分类 + unprotected 缺口）——日志里一眼看全。</summary>
        public static string DescribeAudit()
        {
            return "replicaGates total=" + TotalCount.ToString(CultureInfo.InvariantCulture) +
                " gated=" + Gated.Length.ToString(CultureInfo.InvariantCulture) +
                " [" + Join(Gated) + "]" +
                " · ungated presentation=" + Presentation.Length.ToString(CultureInfo.InvariantCulture) +
                " [" + Join(Presentation) + "]" +
                " · deterministicEdit=" + DeterministicEdit.Length.ToString(CultureInfo.InvariantCulture) +
                " [" + Join(DeterministicEdit) + "]" +
                " · interactionOnly=" + InteractionOnly.Length.ToString(CultureInfo.InvariantCulture) +
                " [" + Join(InteractionOnly) + "]" +
                " · electricityDependent=" +
                ElectricityDependent.Length.ToString(CultureInfo.InvariantCulture) +
                " [" + Join(ElectricityDependent) + "]" +
                " · handlerDetached=" +
                ProtectedByHandlerDetach.Length.ToString(CultureInfo.InvariantCulture) +
                " [" + Join(ProtectedByHandlerDetach) + "]" +
                " · missingSource=" + MissingSource.Length.ToString(CultureInfo.InvariantCulture) +
                " [" + Join(MissingSource) + "]" +
                " · unprotectedWorldSim=" +
                UnprotectedWorldSim.Length.ToString(CultureInfo.InvariantCulture) +
                " [" + Join(UnprotectedWorldSim) + "]";
        }

        /// <summary>只有缺口（用于 Warning；空字符串 = 没有缺口）。</summary>
        public static string DescribeGaps()
        {
            return UnprotectedWorldSim.Length == 0
                ? string.Empty
                : "replicaGatesGap unprotectedWorldSim=" +
                    UnprotectedWorldSim.Length.ToString(CultureInfo.InvariantCulture) +
                    " [" + Join(UnprotectedWorldSim) + "]";
        }
    }
}

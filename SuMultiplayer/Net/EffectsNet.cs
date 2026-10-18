using System;
using System.Collections;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using P = SuMultiplayer.Protocol.Effects;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 烧尽表现（burn-away）协议的**纯逻辑层**（不引用 Game）：编解码、合法性校验、
    /// 批次封顶、按 <c>(server_tick, event_id)</c> 的排序 / 去重、客户端距离与每批上限、
    /// 客户端限速。主机与客户端处理器、以及 P:\tmp\tsverify 的自检都编译这同一份源码。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:227-256）：
    /// 主机每 5 游戏秒消费 <c>m_toBurnAway</c>，把每格替换成空气，然后
    ///   · 最多 15 个 <c>BurntDebrisParticleSystem</c>（最近视点 &lt; 24 m）；
    ///   · 最多 4 次 <c>Audio/Sizzles</c>（最近视点 &lt; 16 m）。
    /// 这两类表现都不在 CellChange 流里，客户端必须由本协议补上。
    ///
    /// 本文件只做数据与判定；真正的粒子 / 声音在 Engine/BurnAwayEffectReplicaEngine.cs，
    /// 且只有表现 API（不写格、不生成实体、不伤害）。
    /// </summary>
    public sealed class BurnAwayEffectPolicy
    {
        /// <summary>引擎的烧尽碎屑距离（m）：<c>CalculateDistanceFromNearestView(...) &lt; 24f</c>。</summary>
        public const float DebrisDistance = 24f;

        /// <summary>引擎的烧尽嘶嘶声距离（m）：<c>... &lt; 16f</c>。</summary>
        public const float SoundDistance = 16f;

        /// <summary>引擎每批最多 15 个烧尽碎屑粒子系统。</summary>
        public const int MaxDebrisPerBatch = 15;

        /// <summary>引擎每批最多 4 次嘶嘶声。</summary>
        public const int MaxSoundsPerBatch = 4;

        /// <summary>一条 BurnAwayEffect 最多携带的格数（主机封顶，超出明确计数）。</summary>
        public const int MaxCellsPerMessage = 128;

        /// <summary>主机在烧尽批次前快照 m_toBurnAway 的格数上限（超出明确计数）。</summary>
        public const int MaxPendingTargets = 256;

        /// <summary>客户端两条烧尽批次之间的最小间隔（约 10 批/秒，防音频刷屏）。</summary>
        public const int MinBatchIntervalMs = 100;

        /// <summary>坐标合法范围（引擎世界远小于此值；越界整条拒绝，不猜）。</summary>
        public const int MaxCellCoordinate = 30000000;

        /// <summary>
        /// 一格 cell value 的 contents 位。与 <c>Game.Terrain.ExtractContents</c> 完全一致
        /// （Source: Survivalcraft/Game/Terrain.cs:404 <c>value &amp; 0x3FF</c>）；
        /// 本层不引用 Game，因此在这里给出同一掩码。
        /// </summary>
        public static int ContentsOf(int cellValue) => cellValue & 0x3FF;

        /// <summary>
        /// 这一对 before/after 是否表示"方块被烧成空气"：之前有内容、之后是空气。
        /// 只有火更新窗口内的候选格才会调用它；普通内容变化（data-only / 换成别的方块）
        /// 明确返回 false。
        /// </summary>
        public static bool IsBurnedAwayChange(int beforeValue, int afterValue) =>
            ContentsOf(beforeValue) != 0 && ContentsOf(afterValue) == 0;
    }

    /// <summary>一格烧尽（世界坐标 + 是否允许嘶嘶声）。</summary>
    public readonly struct BurnAwayCellData
    {
        public BurnAwayCellData(int x, int y, int z, bool sizzle)
        {
            X = x;
            Y = y;
            Z = z;
            Sizzle = sizzle;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public bool Sizzle { get; }

        public string Describe() => "(" + X + "," + Y + "," + Z + ")" + (Sizzle ? "+sizzle" : "");
    }

    /// <summary>一条烧尽批次（已解码 / 待编码）。</summary>
    public readonly struct BurnAwayEffectData
    {
        public BurnAwayEffectData(int clientId, long serverTick, uint eventId,
            BurnAwayCellData[] cells)
        {
            ClientId = clientId;
            ServerTick = serverTick;
            EventId = eventId;
            Cells = cells ?? Array.Empty<BurnAwayCellData>();
        }

        public int ClientId { get; }
        public long ServerTick { get; }
        public uint EventId { get; }
        public BurnAwayCellData[] Cells { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTick + " client=" +
            ClientId + " cells=" + Cells.Length;
    }

    /// <summary>
    /// 线上编解码 + 严格校验。非法载荷整条拒绝（返回 false + 原因），不做"猜一个默认值"。
    /// 合法批次要求：event_id &gt; 0、server_tick ≥ 0、client_id ≥ 0、1..128 格、
    /// 坐标在范围内、同一批里没有重复格。
    /// </summary>
    public static class BurnAwayEffectCodec
    {
        public static bool TryEncode(in BurnAwayEffectData data, out P.BurnAwayEffect message,
            out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            var encoded = new P.BurnAwayEffect
            {
                ClientId = data.ClientId,
                ServerTick = data.ServerTick,
                EventId = data.EventId
            };
            for (int i = 0; i < data.Cells.Length; i++)
            {
                BurnAwayCellData cell = data.Cells[i];
                encoded.Cells.Add(new P.CellKey
                {
                    X = cell.X,
                    Y = cell.Y,
                    Z = cell.Z,
                    Sizzle = cell.Sizzle
                });
            }
            message = encoded;
            reason = null;
            return true;
        }

        public static P.BurnAwayEffect Encode(in BurnAwayEffectData data)
        {
            if (!TryEncode(in data, out P.BurnAwayEffect message, out string reason))
                throw new InvalidOperationException("BurnAwayEffect 无法编码：" + reason);
            return message;
        }

        public static bool TryDecode(P.BurnAwayEffect message, out BurnAwayEffectData data,
            out string reason)
        {
            data = default;
            if (message == null)
            {
                reason = "null";
                return false;
            }
            if (message.EventId == 0)
            {
                reason = "event-id-zero";
                return false;
            }
            if (message.ServerTick < 0)
            {
                reason = "server-tick-negative";
                return false;
            }
            if (message.ClientId < 0)
            {
                reason = "client-id-negative";
                return false;
            }
            int count = message.Cells.Count;
            if (count == 0)
            {
                reason = "cells-empty";
                return false;
            }
            if (count > BurnAwayEffectPolicy.MaxCellsPerMessage)
            {
                reason = "cells-count-" + count;
                return false;
            }
            var cells = new BurnAwayCellData[count];
            var seen = new HashSet<Point3>();
            for (int i = 0; i < count; i++)
            {
                P.CellKey cell = message.Cells[i];
                if (cell == null)
                {
                    reason = "cell-null-" + i;
                    return false;
                }
                if (cell.Y < 0 || cell.Y > 255)
                {
                    reason = "cell-y-" + cell.Y;
                    return false;
                }
                if (cell.X < -BurnAwayEffectPolicy.MaxCellCoordinate ||
                    cell.X > BurnAwayEffectPolicy.MaxCellCoordinate ||
                    cell.Z < -BurnAwayEffectPolicy.MaxCellCoordinate ||
                    cell.Z > BurnAwayEffectPolicy.MaxCellCoordinate)
                {
                    reason = "cell-xz-" + cell.X + "," + cell.Z;
                    return false;
                }
                if (!seen.Add(new Point3(cell.X, cell.Y, cell.Z)))
                {
                    reason = "cell-duplicate-" + cell.X + "," + cell.Y + "," + cell.Z;
                    return false;
                }
                cells[i] = new BurnAwayCellData(cell.X, cell.Y, cell.Z, cell.Sizzle);
            }
            data = new BurnAwayEffectData(message.ClientId, message.ServerTick,
                message.EventId, cells);
            reason = null;
            return true;
        }

        private static bool TryValidate(in BurnAwayEffectData data, out string reason)
        {
            if (data.EventId == 0)
            {
                reason = "event-id-zero";
                return false;
            }
            if (data.ServerTick < 0)
            {
                reason = "server-tick-negative";
                return false;
            }
            if (data.ClientId < 0)
            {
                reason = "client-id-negative";
                return false;
            }
            if (data.Cells == null || data.Cells.Length == 0)
            {
                reason = "cells-empty";
                return false;
            }
            if (data.Cells.Length > BurnAwayEffectPolicy.MaxCellsPerMessage)
            {
                reason = "cells-count-" + data.Cells.Length;
                return false;
            }
            var seen = new HashSet<Point3>();
            for (int i = 0; i < data.Cells.Length; i++)
            {
                BurnAwayCellData cell = data.Cells[i];
                if (cell.Y < 0 || cell.Y > 255)
                {
                    reason = "cell-y-" + cell.Y;
                    return false;
                }
                if (cell.X < -BurnAwayEffectPolicy.MaxCellCoordinate ||
                    cell.X > BurnAwayEffectPolicy.MaxCellCoordinate ||
                    cell.Z < -BurnAwayEffectPolicy.MaxCellCoordinate ||
                    cell.Z > BurnAwayEffectPolicy.MaxCellCoordinate)
                {
                    reason = "cell-xz-" + cell.X + "," + cell.Z;
                    return false;
                }
                if (!seen.Add(new Point3(cell.X, cell.Y, cell.Z)))
                {
                    reason = "cell-duplicate";
                    return false;
                }
            }
            reason = null;
            return true;
        }
    }

    /// <summary>
    /// 客户端事件闸门：按 <c>(server_tick, event_id)</c> 排序 + 按 event_id 去重。
    /// TCP 本身有序，但重连 / 主机重启仍可能让旧批次赶到；重复的碎屑 = 重复的视觉错觉，
    /// 一律显式拒绝并计数，绝不重放。
    /// </summary>
    public sealed class BurnAwayEffectOrderer
    {
        private readonly HashSet<uint> m_seen = new HashSet<uint>();
        private readonly Queue<uint> m_seenOrder = new Queue<uint>();
        private readonly int m_capacity;
        private long m_lastTick = long.MinValue;
        private uint m_lastEventId;

        public BurnAwayEffectOrderer(int capacity = 256)
        {
            m_capacity = capacity <= 0 ? 256 : capacity;
        }

        public long Accepted { get; private set; }
        public long Duplicates { get; private set; }
        public long OutOfOrder { get; private set; }
        public long Resets { get; private set; }
        public long LastTick => m_lastTick;
        public uint LastEventId => m_lastEventId;

        public void Reset()
        {
            m_seen.Clear();
            m_seenOrder.Clear();
            m_lastTick = long.MinValue;
            m_lastEventId = 0;
            Resets++;
        }

        /// <summary>false = 不应用（<paramref name="reason"/> = duplicate / stale / bad-id）。</summary>
        public bool TryAccept(in BurnAwayEffectData data, out string reason)
        {
            if (data.EventId == 0)
            {
                reason = "bad-id";
                return false;
            }
            if (m_seen.Contains(data.EventId))
            {
                Duplicates++;
                reason = "duplicate";
                return false;
            }
            if (m_lastTick != long.MinValue &&
                (data.ServerTick < m_lastTick ||
                 (data.ServerTick == m_lastTick && data.EventId <= m_lastEventId)))
            {
                OutOfOrder++;
                reason = "stale";
                return false;
            }
            m_seen.Add(data.EventId);
            m_seenOrder.Enqueue(data.EventId);
            while (m_seenOrder.Count > m_capacity)
                m_seen.Remove(m_seenOrder.Dequeue());
            m_lastTick = data.ServerTick;
            m_lastEventId = data.EventId;
            Accepted++;
            reason = null;
            return true;
        }

        public string Describe() => "accepted=" + Accepted + " duplicate=" + Duplicates +
            " stale=" + OutOfOrder + " resets=" + Resets;
    }

    /// <summary>
    /// 客户端限速：两条批次至少间隔 <see cref="BurnAwayEffectPolicy.MinBatchIntervalMs"/>。
    /// 引擎正常节奏是每 5 游戏秒一批；限速只用于挡住重连回放 / 异常对端造成的突发音频。
    /// </summary>
    public sealed class BurnAwayEffectRateLimiter
    {
        private readonly long m_minIntervalMs;
        private bool m_hasLast;
        private long m_lastMs;

        public BurnAwayEffectRateLimiter(int minIntervalMs = BurnAwayEffectPolicy.MinBatchIntervalMs)
        {
            m_minIntervalMs = minIntervalMs <= 0
                ? BurnAwayEffectPolicy.MinBatchIntervalMs
                : minIntervalMs;
        }

        public long Skipped { get; private set; }

        public void Reset()
        {
            m_hasLast = false;
            m_lastMs = 0;
        }

        public bool TryAccept(long nowMs)
        {
            // 时间回退（世界重载）不沿用旧时间戳：接受并把时间基准拉回现在。
            if (!m_hasLast || nowMs < m_lastMs || nowMs - m_lastMs >= m_minIntervalMs)
            {
                m_hasLast = true;
                m_lastMs = nowMs;
                return true;
            }
            Skipped++;
            return false;
        }
    }

    /// <summary>
    /// 主机侧一批烧尽格的累加器：去重、封顶 <see cref="BurnAwayEffectPolicy.MaxCellsPerMessage"/>，
    /// 超出的格明确计入 <see cref="Dropped"/>（由调用方写进限流日志 / 诊断，绝不静默丢）。
    /// 实例复用（每个火子系统一个），没有静态状态。
    /// </summary>
    public sealed class BurnAwayBatchAccumulator
    {
        private readonly HashSet<Point3> m_seen = new HashSet<Point3>();
        private readonly List<BurnAwayCellData> m_cells =
            new List<BurnAwayCellData>(BurnAwayEffectPolicy.MaxCellsPerMessage);

        public int Count => m_cells.Count;

        public int Dropped { get; private set; }

        public void Reset()
        {
            m_seen.Clear();
            m_cells.Clear();
            Dropped = 0;
        }

        public bool Add(int x, int y, int z, bool sizzle)
        {
            if (y < 0 || y > 255)
            {
                Dropped++;
                return false;
            }
            var cell = new Point3(x, y, z);
            if (!m_seen.Add(cell)) return false;      // 同一批里重复报告：合并，不算丢弃
            if (m_cells.Count >= BurnAwayEffectPolicy.MaxCellsPerMessage)
            {
                Dropped++;
                return false;
            }
            m_cells.Add(new BurnAwayCellData(x, y, z, sizzle));
            return true;
        }

        public BurnAwayCellData[] ToArray() => m_cells.ToArray();
    }

    /// <summary>
    /// 主机侧烧尽批次收集器（纯逻辑，火外壳在每次 Update 里用同一份）：
    ///   1. 更新前快照 <c>m_toBurnAway</c> 的键（<see cref="SnapshotPending"/>）；
    ///   2. 更新后若该表由非空变空 ⇒ 引擎的 5 秒分支确实跑了，快照里的每一格都被烧掉，
    ///      全部计入（<see cref="CollectProcessedPending"/>）；
    ///   3. 同帧新排队的目标不在快照里：用火候选格的 before/after 差异补齐
    ///      （<see cref="CollectCandidateChanges"/>，只认"有内容 → 空气"）。
    ///
    /// 普通内容变化（data-only、换成别的方块、非火更新窗口）不会进入任何一步，
    /// 因此不会产生 BurnAwayEffect。
    /// </summary>
    public sealed class BurnAwayBatchCollector
    {
        private readonly BurnAwayBatchAccumulator m_batch = new BurnAwayBatchAccumulator();
        private readonly List<Point3> m_pending = new List<Point3>(64);
        private int m_pendingDropped;

        public int Count => m_batch.Count;
        public int PendingCount => m_pending.Count;
        public int Dropped => m_batch.Dropped + m_pendingDropped;

        public void BeginBatch()
        {
            m_batch.Reset();
            m_pending.Clear();
            m_pendingDropped = 0;
        }

        /// <summary>
        /// 更新前快照引擎私有表 m_toBurnAway 的键；超过上限的键明确计入 Dropped。
        /// 参数用非泛型 <see cref="IDictionary"/>，与火外壳通过 ModParentField 读到的类型一致。
        /// </summary>
        public void SnapshotPending(IDictionary table,
            int maxTargets = BurnAwayEffectPolicy.MaxPendingTargets)
        {
            if (table == null) return;
            foreach (object key in table.Keys)
            {
                if (key is not Point3 point) continue;
                if (m_pending.Count >= maxTargets)
                {
                    m_pendingDropped++;
                    continue;
                }
                m_pending.Add(point);
            }
        }

        /// <summary>true = 更新前表非空、更新后表为空（引擎 5 秒烧尽分支执行过）。</summary>
        public void CollectProcessedPending(bool periodicBranchRan)
        {
            if (!periodicBranchRan) return;
            for (int i = 0; i < m_pending.Count; i++)
            {
                Point3 point = m_pending[i];
                m_batch.Add(point.X, point.Y, point.Z, sizzle: true);
            }
        }

        /// <summary>
        /// 同一帧内新排队、并被同帧 5 秒分支消费的目标：它们不在更新前快照里，
        /// 但会出现在火候选格中，并在这次火 Update 里由"有内容"变成空气。
        /// </summary>
        public void CollectCandidateChanges(IReadOnlyList<Point3> candidates,
            IReadOnlyList<int> before, IReadOnlyList<int> after)
        {
            if (candidates == null || before == null || after == null) return;
            int count = candidates.Count;
            if (count > before.Count) count = before.Count;
            if (count > after.Count) count = after.Count;
            for (int i = 0; i < count; i++)
            {
                if (!BurnAwayEffectPolicy.IsBurnedAwayChange(before[i], after[i])) continue;
                Point3 point = candidates[i];
                m_batch.Add(point.X, point.Y, point.Z, sizzle: true);
            }
        }

        public BurnAwayCellData[] ToArray() => m_batch.ToArray();
    }

    /// <summary>客户端一批烧尽格的表现计划（只描述"该放什么"，不执行）。</summary>
    public readonly struct BurnAwayPresentationPlan
    {
        public BurnAwayPresentationPlan(BurnAwayCellData[] debrisCells,
            BurnAwayCellData[] soundCells, int presented, int skippedDistance, int skippedCap)
        {
            DebrisCells = debrisCells ?? Array.Empty<BurnAwayCellData>();
            SoundCells = soundCells ?? Array.Empty<BurnAwayCellData>();
            Presented = presented;
            SkippedDistance = skippedDistance;
            SkippedCap = skippedCap;
        }

        public BurnAwayCellData[] DebrisCells { get; }
        public BurnAwayCellData[] SoundCells { get; }
        /// <summary>至少得到一种表现的格数。</summary>
        public int Presented { get; }
        /// <summary>因为超过 24 m 而什么都没得到的格数。</summary>
        public int SkippedDistance { get; }
        /// <summary>距离够但本批 15 / 4 上限已满而没得到的格数。</summary>
        public int SkippedCap { get; }
    }

    /// <summary>
    /// 客户端表现口径（与引擎逐视点体验一致）：
    ///   · 碎屑：距离本地玩家 &lt; 24 m，每批最多 15 个；
    ///   · 嘶嘶声：距离 &lt; 16 m 且该格 sizzle，每批最多 4 次；
    ///   · 一批里超出上限 / 距离的格明确计数（skippedCap / skippedDistance）。
    /// 距离与引擎一样按格角 <c>new Vector3(x,y,z)</c> 计算（不是格中心）。
    /// </summary>
    public static class BurnAwayPresentationPolicy
    {
        public static BurnAwayPresentationPlan Plan(IReadOnlyList<BurnAwayCellData> cells,
            Vector3 listener)
        {
            if (cells == null || cells.Count == 0)
                return new BurnAwayPresentationPlan(null, null, 0, 0, 0);
            var debris = new List<BurnAwayCellData>(BurnAwayEffectPolicy.MaxDebrisPerBatch);
            var sounds = new List<BurnAwayCellData>(BurnAwayEffectPolicy.MaxSoundsPerBatch);
            float debrisDistanceSquared =
                BurnAwayEffectPolicy.DebrisDistance * BurnAwayEffectPolicy.DebrisDistance;
            float soundDistanceSquared =
                BurnAwayEffectPolicy.SoundDistance * BurnAwayEffectPolicy.SoundDistance;
            int presented = 0;
            int skippedDistance = 0;
            int skippedCap = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                BurnAwayCellData cell = cells[i];
                float dx = cell.X - listener.X;
                float dy = cell.Y - listener.Y;
                float dz = cell.Z - listener.Z;
                float distanceSquared = dx * dx + dy * dy + dz * dz;
                bool presentedThisCell = false;
                if (distanceSquared < debrisDistanceSquared)
                {
                    if (debris.Count < BurnAwayEffectPolicy.MaxDebrisPerBatch)
                    {
                        debris.Add(cell);
                        presentedThisCell = true;
                    }
                    else
                    {
                        skippedCap++;
                    }
                }
                else
                {
                    skippedDistance++;
                }
                if (cell.Sizzle && distanceSquared < soundDistanceSquared)
                {
                    if (sounds.Count < BurnAwayEffectPolicy.MaxSoundsPerBatch)
                    {
                        sounds.Add(cell);
                        presentedThisCell = true;
                    }
                    else if (!presentedThisCell)
                    {
                        skippedCap++;
                    }
                }
                if (presentedThisCell) presented++;
            }
            return new BurnAwayPresentationPlan(debris.ToArray(), sounds.ToArray(), presented,
                skippedDistance, skippedCap);
        }
    }
}

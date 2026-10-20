using System;
using System.Collections.Generic;
using Engine;
using P = SuMultiplayer.Protocol.Effects;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 营火**客户端表现**的纯逻辑（不引用 Game）：格子判据、粒子参数、状态机、越界扫除。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemCampfireBlockBehavior.cs）：
    ///   · <c>OnBlockAdded</c>（:74）→ AddCampfireParticleSystem：<c>data &gt; 0</c> 才建
    ///     <c>FireParticleSystem(new Vector3(x, y, z) + (0.5, 0.15, 0.5), size, 256f)</c>，
    ///     <c>size = MathUtils.Lerp(0.2f, 0.5f, data / 15f)</c>（:138-148）；
    ///   · <c>OnBlockRemoved</c>（:79）/ <c>OnBlockModified</c>（:84）把粒子从表里摘掉并
    ///     <c>IsStopped = true</c>；燃料烧到 0（data = 0，格子仍是营火 209 = 灰烬）时同样走
    ///     OnBlockModified → 摘掉粒子（主机在 Update 的 5 秒分支里把 data -1，见 :44-67）；
    ///   · 区块丢弃 <c>OnChunkDiscarding</c>（:107-124）先 ResizeCampfire(-15) 再摘粒子。
    ///
    /// 客户端的地形由 <c>TerrainBridge.ApplyCells</c> 用 <c>SetCellValueFast</c> 落地（绕过
    /// <c>ChangeCell</c>），所以这些回调原本永远不会到达客户端 —— 营火粒子会一直留着。
    /// 本层只描述"该建 / 该换 / 该收 / 保持"，真正的粒子对象在
    /// <c>Engine/CampfireReplicaPresentation.cs</c>，且只有表现 API（不写格 / 不生成实体）。
    ///
    /// 无静态可变状态：每个会话一个 <see cref="CampfireReplicaState"/> 实例。
    /// </summary>
    public static class CampfirePresentationPolicy
    {
        /// <summary>营火方块索引（Source: Survivalcraft/Game/CampfireBlock.cs:CampfireBlock.Index = 209）。</summary>
        public const int CampfireBlockIndex = 209;

        /// <summary>引擎营火燃料 data 的有效上限（0..15；0 = 灰烬，不冒火）。</summary>
        public const int MaxFuelData = 15;

        /// <summary>引擎粒子的最大可见距离（Source: AddCampfireParticleSystem 的 256f）。</summary>
        public const float MaxVisibilityDistance = 256f;

        /// <summary>客户端同时跟踪的营火粒子上限（超出明确计数并拒绝，不无限增长）。</summary>
        public const int MaxTrackedParticles = 4096;

        /// <summary>一次越界扫除最多检查的粒子数（每帧/每秒有界，不卡主线程）。</summary>
        public const int MaxSweepChecksPerRun = 64;

        /// <summary>引擎格子位布局（Source: Survivalcraft/Game/Terrain.cs:ExtractContents/ExtractData）。</summary>
        public static int ContentsOf(int value) => value & 0x3FF;

        public static int DataOf(int value) => value >>> 14;

        public static bool IsCampfire(int contents) => contents == CampfireBlockIndex;

        /// <summary>真正在烧的营火：格子还是营火，且燃料 data &gt; 0（data = 0 是灰烬）。</summary>
        public static bool IsBurning(int contents, int data) =>
            IsCampfire(contents) && data > 0;

        public static bool IsBurningValue(int value) =>
            IsBurning(ContentsOf(value), DataOf(value));

        /// <summary>引擎粒子位置偏移（Source: AddCampfireParticleSystem 的 vector）。</summary>
        public static Vector3 ParticleOffset => new Vector3(0.5f, 0.15f, 0.5f);

        /// <summary>引擎粒子尺寸（Source: AddCampfireParticleSystem 的 MathUtils.Lerp(0.2f, 0.5f, data/15f)）。</summary>
        public static float ParticleSize(int data)
        {
            int clamped = data < 0 ? 0 : (data > MaxFuelData ? MaxFuelData : data);
            return 0.2f + (0.5f - 0.2f) * ((float)clamped / MaxFuelData);
        }    }

    /// <summary>客户端一格营火表现该做什么（由 <see cref="CampfireReplicaState"/> 决定）。</summary>
    public enum CampfirePresentationAction
    {
        /// <summary>这格本来就没有营火表现，现在也不需要（无变化）。</summary>
        None = 0,

        /// <summary>新建一个营火粒子（这格第一次变成燃烧的营火）。</summary>
        Add = 1,

        /// <summary>已经在烧且燃料 data 没变：保持现有粒子（绝不重复创建）。</summary>
        Keep = 2,

        /// <summary>燃料 data 变了：按新的 data 重建一个粒子（旧粒子 IsStopped）。</summary>
        Replace = 3,

        /// <summary>这格不再是燃烧的营火（烧成灰烬 / 被替换 / 被移除）：收掉粒子。</summary>
        Remove = 4,

        /// <summary>客户端跟踪的粒子已达上限（明确计数，不创建）。</summary>
        Rejected = 5
    }

    /// <summary>
    /// 营火客户端表现的**状态机**（纯托管、无引擎对象）。记录"哪些格当前应该有粒子"，
    /// 由 <c>Engine/CampfireReplicaPresentation</c> 在同一个漏斗里调用。
    ///
    /// 不变式（自检直接断言）：
    ///   · 同一格最多只有一个活动粒子（重复的 <see cref="Apply"/> 返回 Keep，不新增）；
    ///   · 烧成灰烬 / 空气后返回 Remove，之后同一格再报告非燃烧值返回 None（绝不重新添加）；
    ///   · <see cref="Sweep"/> 只返回**本状态机跟踪的**、且格子已经不是燃烧营火的格；
    ///     不认识的粒子（别的表现系统）永远不在返回值里。
    /// </summary>
    public sealed class CampfireReplicaState
    {
        private readonly Dictionary<Point3, int> m_tracked = new Dictionary<Point3, int>();
        private readonly List<Point3> m_sweepKeys = new List<Point3>(64);
        private int m_sweepCursor;

        /// <summary>累计新建的粒子数。</summary>
        public long Added { get; private set; }

        /// <summary>累计收掉的粒子数（烧尽 / 被替换 / 移除）。</summary>
        public long Removed { get; private set; }

        /// <summary>燃料 data 变化导致的粒子重建次数（旧粒子收掉 + 新粒子建立）。</summary>
        public long Replaced { get; private set; }

        /// <summary>重复报告同一格同一 data 而被抑制的创建次数（防重复粒子）。</summary>
        public long DuplicateSuppressed { get; private set; }

        /// <summary>因达到 <see cref="CampfirePresentationPolicy.MaxTrackedParticles"/> 而拒绝的格数。</summary>
        public long CapHits { get; private set; }

        /// <summary>越界扫除累计收掉的陈旧粒子数（counter: campfireStaleSweep）。</summary>
        public long StaleSwept { get; private set; }

        /// <summary>越界扫除运行次数。</summary>
        public long SweepRuns { get; private set; }

        /// <summary>越界扫除累计检查的格数。</summary>
        public long SweepChecks { get; private set; }

        /// <summary>越界扫除遇到"区块未加载 / 读不到"而跳过的格数。</summary>
        public long SweepSkippedUnreadable { get; private set; }

        /// <summary>当前跟踪的粒子数。</summary>
        public int TrackedCount => m_tracked.Count;

        public bool IsTracked(Point3 cell) => m_tracked.ContainsKey(cell);

        /// <summary>
        /// 按一格的主机权威值更新状态机。返回调用方该执行的表现动作。
        /// </summary>
        public CampfirePresentationAction Apply(int value, int x, int y, int z)
        {
            var cell = new Point3(x, y, z);
            bool burning = CampfirePresentationPolicy.IsBurningValue(value);
            bool tracked = m_tracked.TryGetValue(cell, out int trackedData);
            if (!burning)
            {
                if (!tracked) return CampfirePresentationAction.None;
                m_tracked.Remove(cell);
                Removed++;
                return CampfirePresentationAction.Remove;
            }
            int data = CampfirePresentationPolicy.DataOf(value);
            if (tracked)
            {
                if (trackedData == data)
                {
                    DuplicateSuppressed++;
                    return CampfirePresentationAction.Keep;
                }
                m_tracked[cell] = data;
                Replaced++;
                return CampfirePresentationAction.Replace;
            }
            if (m_tracked.Count >= CampfirePresentationPolicy.MaxTrackedParticles)
            {
                CapHits++;
                return CampfirePresentationAction.Rejected;
            }
            m_tracked[cell] = data;
            Added++;
            return CampfirePresentationAction.Add;
        }

        /// <summary>这格明确不再有营火（移除 / 区块丢弃 / 世界切换）：返回 true = 之前跟踪过。</summary>
        public bool Forget(Point3 cell)
        {
            if (!m_tracked.Remove(cell)) return false;
            Removed++;
            return true;
        }

        /// <summary>粒子创建失败（引擎对象异常）：只摘跟踪、不计数 Removed，允许下一拍重试。</summary>
        public bool Untrack(Point3 cell) => m_tracked.Remove(cell);

        /// <summary>世界切换：旧粒子随旧 Project 销毁，这里只清跟踪表（不重复 Stop 旧对象）。</summary>
        public void ClearTracked()
        {
            m_tracked.Clear();
            m_sweepKeys.Clear();
            m_sweepCursor = 0;
        }

        /// <summary>
        /// 有界越界扫除：最多检查 <paramref name="maxChecks"/> 个**本状态机跟踪的**格。
        /// <paramref name="readValue"/> 返回该格的引擎 value；返回负数 = 读不到（区块未加载，
        /// 本次跳过并计数，绝不当成"不是营火"而误删）。
        /// 返回本次应收掉的格（调用方只 Stop 这些粒子；从不触碰别的粒子系统）。
        /// </summary>
        public void Sweep(Func<int, int, int, int> readValue,
            List<Point3> staleCells, int maxChecks = CampfirePresentationPolicy.MaxSweepChecksPerRun)
        {
            if (staleCells == null) throw new ArgumentNullException(nameof(staleCells));
            if (readValue == null) throw new ArgumentNullException(nameof(readValue));
            SweepRuns++;
            if (m_tracked.Count == 0)
            {
                m_sweepCursor = 0;
                return;
            }
            int limit = maxChecks <= 0 ? 0 : maxChecks;
            m_sweepKeys.Clear();
            foreach (Point3 cell in m_tracked.Keys) m_sweepKeys.Add(cell);
            if (m_sweepKeys.Count == 0) return;
            int start = m_sweepCursor % m_sweepKeys.Count;
            int checks = 0;
            for (int i = 0; i < m_sweepKeys.Count && checks < limit; i++)
            {
                Point3 cell = m_sweepKeys[(start + i) % m_sweepKeys.Count];
                checks++;
                int value = readValue(cell.X, cell.Y, cell.Z);
                if (value < 0)
                {
                    SweepSkippedUnreadable++;
                    continue;
                }
                if (CampfirePresentationPolicy.IsBurningValue(value)) continue;
                if (!m_tracked.Remove(cell)) continue;      // 同一轮里已被其它路径收掉
                staleCells.Add(cell);
                StaleSwept++;
            }
            SweepChecks += checks;
            m_sweepCursor = (start + checks) % m_sweepKeys.Count;
        }

        public string Describe() =>
            "campfireParticleAdded=" + Added + " campfireParticleRemoved=" + Removed +
            " replaced=" + Replaced + " duplicateSuppressed=" + DuplicateSuppressed +
            " capHits=" + CapHits + " active=" + m_tracked.Count +
            " campfireStaleSweep=" + StaleSwept + " sweepRuns=" + SweepRuns +
            " sweepChecks=" + SweepChecks + " sweepUnreadable=" + SweepSkippedUnreadable;
    }

    /// <summary>
    /// 客户端**方块行为通知**策略（纯逻辑）。
    ///
    /// 引擎的 <c>SubsystemTerrain.ChangeCell</c> 在内容变化时对旧内容的 behaviors 派发
    /// <c>OnBlockRemoved</c>、对新内容的 behaviors 派发 <c>OnBlockAdded</c>，内容不变时派发
    /// <c>OnBlockModified</c>（Source: Survivalcraft/Game/SubsystemTerrain.cs:254-276）。
    /// 客户端的地形由 <c>SetCellValueFast</c> 落地，必须补上同一套通知 —— 但不能把所有
    /// behavior 都叫起来：衣柜 / 工作台 / 熔炉 / 分配器会 AddEntity，门 / 栅栏 / 楼梯会写格。
    ///
    /// 因此只有一个**单一漏斗**，且只派发到"纯表现"行为：
    ///   · <c>SuSubsystemCampfireBlockBehavior</c> —— 营火粒子（本 mod 的外壳，客户端走表现层）；
    ///   · <c>SuSubsystemFurnaceBlockBehavior</c> —— 熔炉火焰（本 mod 的外壳，客户端只建
    ///     FireParticleSystem；base 会 AddEntity / RemoveEntity，客户端绝不放行）；
    ///   · <c>SubsystemTorchBlockBehavior</c> —— 火把粒子（Add/Remove/Modified 只碰粒子）。
    /// 其余 behavior 一律**拒绝通知**并计入 <c>clientBehaviorDeniedWrites</c>（它们可能写格 /
    /// 生成实体；客户端副本保持 mutation-denied，不静默应用）。
    /// </summary>
    public static class BlockBehaviorNotifyPolicy
    {
        public const string CampfireShellType = "SuSubsystemCampfireBlockBehavior";
        public const string FurnaceShellType = "SuSubsystemFurnaceBlockBehavior";
        public const string TorchType = "SubsystemTorchBlockBehavior";

        /// <summary>内容是否变化决定派发 Added/Removed 还是 Modified（与 ChangeCell 完全一致）。</summary>
        public static BlockBehaviorNotifyKind Classify(int oldContents, int newContents) =>
            oldContents == newContents
                ? BlockBehaviorNotifyKind.Modified
                : BlockBehaviorNotifyKind.Replaced;

        /// <summary>
        /// 只允许"纯表现" behavior 在客户端收到通知。其它 behavior（可能写格 / 生成实体）
        /// 返回 false，由调用方计入 clientBehaviorDeniedWrites 并跳过。
        /// </summary>
        public static bool IsPresentationBehavior(string typeName) => typeName switch
        {
            CampfireShellType => true,
            FurnaceShellType => true,
            TorchType => true,
            _ => false
        };

        /// <summary>被拒绝的具体类型与原因（组合根 / 诊断日志用；不是静态可变状态）。</summary>
        public static string ExplainDenied(string typeName) => typeName switch
        {
            "SubsystemChestBlockBehavior" => "创建箱子方块实体（客户端由容器副本维护，行为通知会重复建实体）",
            "SubsystemCraftingTableBlockBehavior" => "创建工作台方块实体（客户端由容器副本维护）",
            "SubsystemFurnaceBlockBehavior" => "创建熔炉方块实体 / DropAllItems / RemoveEntity（火焰由 SuSubsystemFurnaceBlockBehavior 外壳单独表现）",
            "SubsystemDispenserBlockBehavior" => "创建分配器方块实体（客户端由容器副本维护）",
            "SubsystemDoorBlockBehavior" => "OnBlockAdded/Removed 会 ChangeCell 门的上/下半格",
            "SubsystemFenceBlockBehavior" => "OnBlockAdded 会 ChangeCell 改写栅栏变体",
            "SubsystemStairsBlockBehavior" => "OnBlockAdded 会 ChangeCell 改写楼梯转角",
            "SubsystemFurnitureBlockBehavior" => "依赖设计表 m_furnitureDesigns，客户端缺表时 AddTerrainFurniture 会 NullReference",
            "SubsystemExplosivesBlockBehavior" => "引信表现由 FuseReplicaPresentation 单独维护（避免双份粒子）",
            "SubsystemPistonBlockBehavior" => "活塞由 PistonReplicaEngine 维护（行为通知会写格）",
            "SubsystemFireBlockBehavior" => "火由主机权威 / 烧尽表现模块维护（客户端不跑 base 回调）",
            _ => "非纯表现行为（可能写格 / 生成实体），客户端保持 mutation-denied"
        };
    }

    public enum BlockBehaviorNotifyKind
    {
        /// <summary>内容不变（data-only）：派发 OnBlockModified。</summary>
        Modified = 0,

        /// <summary>内容变化：旧内容 OnBlockRemoved + 新内容 OnBlockAdded。</summary>
        Replaced = 1
    }

    /// <summary>一格营火熄灭火声（世界坐标 + 线上元数据）。</summary>
    public readonly struct CampfireExtinguishData
    {
        public CampfireExtinguishData(int clientId, long serverTick, uint eventId,
            int x, int y, int z)
        {
            ClientId = clientId;
            ServerTick = serverTick;
            EventId = eventId;
            X = x;
            Y = y;
            Z = z;
        }

        public int ClientId { get; }
        public long ServerTick { get; }
        public uint EventId { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public Point3 Cell => new Point3(X, Y, Z);

        public string Describe() => "id=" + EventId + " tick=" + ServerTick +
            " client=" + ClientId + " cell=(" + X + "," + Y + "," + Z + ")";
    }

    /// <summary>
    /// 营火熄灭火声协议（append-only）。引擎事实：主机 <c>ResizeCampfire</c> 在燃料降到 0 时
    /// 播 <c>Audio/Sizzles</c>（Source: SubsystemCampfireBlockBehavior.cs:ResizeCampfire），
    /// 这是本端音效，不随 CellChange 下发；客户端需要一条小事件补上。
    ///
    /// 只承载**已经烧尽**（格子仍是营火 209、data = 0）的事件；不放普通燃料递减
    /// （每 30 秒一次会变成持续嘶嘶声）。客户端只播放，不写格、不生成实体。
    /// </summary>
    public static class CampfireSoundPolicy
    {
        /// <summary>客户端两条熄灭火声之间的最小间隔（毫秒）。</summary>
        public const int MinClientIntervalMs = 200;

        /// <summary>主机一次 Update 最多转发的事件数（多格同时烧尽时有界）。</summary>
        public const int MaxEventsPerHostUpdate = 32;

        /// <summary>坐标合法范围（与烧尽协议一致）。</summary>
        public const int MaxCellCoordinate = 30000000;
    }

    /// <summary>线上编解码 + 严格校验（非法载荷整条拒绝，不猜默认值）。</summary>
    public static class CampfireExtinguishCodec
    {
        public static bool TryEncode(in CampfireExtinguishData data, out P.CampfireExtinguishEvent message,
            out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            message = new P.CampfireExtinguishEvent
            {
                ClientId = data.ClientId,
                ServerTick = data.ServerTick,
                EventId = data.EventId,
                X = data.X,
                Y = data.Y,
                Z = data.Z
            };
            reason = null;
            return true;
        }

        public static P.CampfireExtinguishEvent Encode(in CampfireExtinguishData data)
        {
            if (!TryEncode(in data, out P.CampfireExtinguishEvent message, out string reason))
                throw new InvalidOperationException("CampfireExtinguishEvent 无法编码：" + reason);
            return message;
        }

        public static bool TryDecode(P.CampfireExtinguishEvent message, out CampfireExtinguishData data,
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
            if (message.Y < 0 || message.Y > 255)
            {
                reason = "cell-y-" + message.Y;
                return false;
            }
            if (message.X < -CampfireSoundPolicy.MaxCellCoordinate ||
                message.X > CampfireSoundPolicy.MaxCellCoordinate ||
                message.Z < -CampfireSoundPolicy.MaxCellCoordinate ||
                message.Z > CampfireSoundPolicy.MaxCellCoordinate)
            {
                reason = "cell-xz-" + message.X + "," + message.Z;
                return false;
            }
            data = new CampfireExtinguishData(message.ClientId, message.ServerTick,
                message.EventId, message.X, message.Y, message.Z);
            reason = null;
            return true;
        }

        private static bool TryValidate(in CampfireExtinguishData data, out string reason)
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
            if (data.Y < 0 || data.Y > 255)
            {
                reason = "cell-y-" + data.Y;
                return false;
            }
            if (data.X < -CampfireSoundPolicy.MaxCellCoordinate ||
                data.X > CampfireSoundPolicy.MaxCellCoordinate ||
                data.Z < -CampfireSoundPolicy.MaxCellCoordinate ||
                data.Z > CampfireSoundPolicy.MaxCellCoordinate)
            {
                reason = "cell-xz-" + data.X + "," + data.Z;
                return false;
            }
            reason = null;
            return true;
        }
    }

    /// <summary>
    /// 客户端事件闸门：按 <c>(server_tick, event_id)</c> 排序 + 按 event_id 去重
    /// （重连 / 主机重启后旧事件不重放）。
    /// </summary>
    public sealed class CampfireExtinguishOrderer
    {
        private readonly HashSet<uint> m_seen = new HashSet<uint>();
        private readonly Queue<uint> m_seenOrder = new Queue<uint>();
        private readonly int m_capacity;
        private long m_lastTick = long.MinValue;
        private uint m_lastEventId;

        public CampfireExtinguishOrderer(int capacity = 256)
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

        public bool TryAccept(in CampfireExtinguishData data, out string reason)
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

    /// <summary>客户端熄灭火声限速（两条至少间隔 <see cref="CampfireSoundPolicy.MinClientIntervalMs"/>）。</summary>
    public sealed class CampfireExtinguishRateLimiter
    {
        private readonly long m_minIntervalMs;
        private bool m_hasLast;
        private long m_lastMs;

        public CampfireExtinguishRateLimiter(int minIntervalMs = CampfireSoundPolicy.MinClientIntervalMs)
        {
            m_minIntervalMs = minIntervalMs < 0 ? CampfireSoundPolicy.MinClientIntervalMs : minIntervalMs;
        }

        public long Skipped { get; private set; }

        public void Reset()
        {
            m_hasLast = false;
            m_lastMs = 0;
        }

        public bool TryAccept(long nowMs)
        {
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
    /// 主机侧"哪些格刚刚烧尽"的纯逻辑（壳在 Update 前后各喂一次快照）。
    /// 只认"之前是燃烧的营火、之后格子仍是营火 209 且 data = 0"：
    /// 被玩家拆掉 / 被替换成别的方块不会触发（引擎也不在那条路径播 Sizzles）。
    /// </summary>
    public sealed class CampfireExtinguishDetector
    {
        private readonly List<Point3> m_snapshot = new List<Point3>(64);
        private readonly HashSet<Point3> m_relayed = new HashSet<Point3>();
        private readonly List<Point3> m_batch = new List<Point3>(8);

        public long Scanned { get; private set; }
        public long Detected { get; private set; }
        public long SnapshotCapHits { get; private set; }

        /// <summary>base.Update 之前：记录当前引擎营火表里的每一格（有界）。</summary>
        public void Begin(IEnumerable<Point3> campfireCells)
        {
            m_snapshot.Clear();
            if (campfireCells == null) return;
            foreach (Point3 cell in campfireCells)
            {
                if (m_snapshot.Count >= CampfirePresentationPolicy.MaxTrackedParticles)
                {
                    SnapshotCapHits++;
                    continue;
                }
                m_snapshot.Add(cell);
                Scanned++;
            }
        }

        /// <summary>
        /// base.Update 之后：返回本次真正"烧尽"的格（已按格去重）。同一格重新点燃后会再次
        /// 允许上报（<paramref name="readValue"/> 读到燃烧值就把它从已上报集合里移除）。
        /// </summary>
        public Point3[] End(Func<int, int, int, int> readValue)
        {
            m_batch.Clear();
            if (readValue == null || m_snapshot.Count == 0) return Array.Empty<Point3>();
            for (int i = 0; i < m_snapshot.Count; i++)
            {
                Point3 cell = m_snapshot[i];
                int value = readValue(cell.X, cell.Y, cell.Z);
                if (value < 0) continue;                    // 区块已丢弃：不是烧尽
                if (CampfirePresentationPolicy.IsBurningValue(value))
                {
                    m_relayed.Remove(cell);                 // 重新点燃：下次烧尽还能上报
                    continue;
                }
                int contents = CampfirePresentationPolicy.ContentsOf(value);
                int data = CampfirePresentationPolicy.DataOf(value);
                if (contents != CampfirePresentationPolicy.CampfireBlockIndex || data != 0) continue;
                if (!m_relayed.Add(cell)) continue;         // 同一格只上报一次
                Detected++;
                m_batch.Add(cell);
            }
            m_snapshot.Clear();
            return m_batch.Count == 0 ? Array.Empty<Point3>() : m_batch.ToArray();
        }

        public void Reset()
        {
            m_snapshot.Clear();
            m_relayed.Clear();
            m_batch.Clear();
        }

        public string Describe() => "scanned=" + Scanned + " detected=" + Detected +
            " capHits=" + SnapshotCapHits + " relayedCells=" + m_relayed.Count;
    }
}

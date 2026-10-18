using System;
using System.Collections.Generic;
using Engine;
using P = SuMultiplayer.Protocol.Piston;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 活塞运动表现协议的**纯逻辑层**（不引用 Game）：常量 / 位布局 / 编解码 / 严格校验 /
    /// 批次封顶 / 排序去重 / 客户端距离与时长估计。主机与客户端处理器、以及
    /// P:\tmp\tsverify 的自检都编译这同一份源码。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs）：
    ///   · 推 / 拉 / 收回的中间过程**不是格子数据**：MovePiston（:295-431）往
    ///     SubsystemMovingBlocks 里放一个 Id=="Piston" 的 MovingBlockSet，集合随后被
    ///     SubsystemMovingBlocks.Update（:277-352）按 speed/smoothness 插值移动，Draw 直接画；
    ///   · 只有运动结束（Stopped → StopPiston :433-469）才把最终结果写回格子（走 CellChange 下发）；
    ///   · 客户端不跑活塞逻辑 ⇒ 没有这些集合 ⇒ 中间过程完全看不到。
    ///
    /// 所以"运动开始"必须以**和主机 AddMovingBlockSet 完全相同的参数**下发：起始 / 目标位置、
    /// 速度、加速度、阻力、平滑度、方块集合（Offset + Value）。本层逐字段校验这些参数，
    /// 任何不一致都整条拒绝（返回原因，绝不猜默认值）。
    /// </summary>
    public static class PistonMovePolicy
    {
        // ---------------------------------------------------------------- 引擎上限
        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:36-38
        //   PistonMaxMovedBlocks = 8 / PistonMaxExtension = 8 / PistonMaxSpeedSetting = 3

        /// <summary>引擎单次最多推移 / 拉动的方块数。</summary>
        public const int MaxMovedBlocks = 8;

        /// <summary>引擎最大伸出格数（length 会被 clamp 到 0..maxExtension+1 = 0..9）。</summary>
        public const int MaxExtension = 8;

        /// <summary>
        /// 一个运动集合最多携带的方块数：已有活塞头（&lt;8）+ 新活塞头（1）+ 被推移方块（&lt;8）= 17。
        /// Source: SubsystemPistonBlockBehavior.MovePiston 的两个 while（Count&lt;8 / num3&lt;8）
        /// </summary>
        public const int MaxBlocksPerMove = 17;

        /// <summary>方块偏移的绝对值上限（length clamp 到 9 ⇒ 偏移最多 9 格）。</summary>
        public const int MaxBlockOffset = MaxExtension + 1;

        // ---------------------------------------------------------------- 塌落（CollapsingBlock）
        // Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs
        //   · HandledBlocks = { 7 (SandBlock.Index), 6 (GravelBlock.Index) }（:18）
        //   · TryCollapseColumn:87-99 从 p.Y 往上把整列可塌落方块收进集合（最多到 255）
        //   · AddMovingBlockSet(new Vector3(p), new Vector3(p.X, -list.Count - 1, p.Z),
        //                       0f, 10f, 0.7f, new Vector2(0f), list, "CollapsingBlock", null, true)

        /// <summary>SandBlock.Index（引擎 HandledBlocks 之一）。Source: SandBlock.cs:5</summary>
        public const int SandContents = 7;

        /// <summary>GravelBlock.Index（引擎 HandledBlocks 之一）。Source: GravelBlock.cs:7</summary>
        public const int GravelContents = 6;

        /// <summary>引擎一列最多收集的方块数（p.Y..255 ⇒ 256）。</summary>
        public const int MaxCollapseBlocks = 256;

        /// <summary>塌落列方块偏移的绝对值上限（列底 = 255 - p.Y ≤ 255）。</summary>
        public const int MaxCollapseOffset = 255;

        /// <summary>引擎认为"可塌落"的方块（与 IsCollapsibleBlock 同一集合）。</summary>
        public static bool IsCollapsibleContents(int contents) =>
            contents == SandContents || contents == GravelContents;

        /// <summary>一条消息最多携带的运动条数（主机按 tick 合并，超出拆成多条；有界批次）。</summary>
        public const int MaxMovesPerMessage = 16;

        /// <summary>主机一个 tick 内待发送队列的硬上限（超出明确计数丢弃，不无限增长）。</summary>
        public const int MaxPendingMoves = 64;

        /// <summary>坐标合法范围（引擎世界远小于此值；越界整条拒绝，不猜）。</summary>
        public const int MaxCellCoordinate = 30000000;

        /// <summary>引擎一次运动的最大位移（9 格 + 起始/目标各 0.01 的 epsilon）。</summary>
        public const float MaxMoveDistance = MaxExtension + 1.01f;

        /// <summary>引擎速度上限（GetSpeedAndSmoothness 默认 5，最快档 3.5）。</summary>
        public const float MaxSpeed = 6f;

        /// <summary>引擎平滑度上限（GetSpeedAndSmoothness 最大 1.2）。</summary>
        public const float MaxSmoothness = 2f;

        /// <summary>主机透传的加速度 / 阻力上限（活塞恒为 0，只做合法性上限，不猜）。</summary>
        public const float MaxAcceleration = 32f;

        /// <summary>位移必须与活塞朝向平行（引擎插值只沿 face 轴移动），容差。</summary>
        public const float DirectionEpsilon = 1e-2f;

        /// <summary>客户端同时存在的本地只表现集合上限（超出明确计数并跳过，不无限增长）。</summary>
        public const int MaxConcurrentLocalMoves = 64;

        /// <summary>客户端可见范围之外额外允许的表现余量（米）。</summary>
        public const float PresentationMargin = 16f;

        /// <summary>客户端可见范围下限（米）：Sky 的可见范围异常小时也不至于一格都不表现。</summary>
        public const float MinViewDistance = 32f;

        /// <summary>主机侧判断"这个玩家端要不要收"的区块半径下限（区块）。</summary>
        public const int MinPeerRadiusChunks = 4;

        /// <summary>结束事件迟到多久后客户端放弃本地动画（毫秒；客户端 <c>pistonMoveAborted</c>）。</summary>
        public const int EndGraceMs = 3000;

        /// <summary>无结束事件时本地动画的总超时（按估计时长再加这个上界，毫秒）。</summary>
        public const int WatchdogSlackMs = 2000;

        // ---------------------------------------------------------------- 位布局
        // Source: Survivalcraft/Game/Terrain.cs:394-417（contents = value & 0x3FF，data = value >>> 14）
        // Source: Survivalcraft/Game/PistonBlock.cs:157-215（mode=(data>>1)&3, face=(data>>3)&7）
        // Source: Survivalcraft/Game/PistonHeadBlock.cs:70-98（mode=data&3, isShaft=(data&4)!=0, face=(data>>3)&7）

        public const int ContentsMask = 0x3FF;
        public const int PistonContents = 237;
        public const int PistonHeadContents = 238;

        public static int ContentsOf(int cellValue) => cellValue & ContentsMask;
        public static int DataOf(int cellValue) => cellValue >>> 14;
        public static int PistonModeOfData(int data) => (data >> 1) & 3;
        public static int PistonFaceOfData(int data) => (data >> 3) & 7;
        public static int PistonSpeedOfData(int data) => (data >> 12) & 7;
        public static int HeadModeOfData(int data) => data & 3;
        public static int HeadFaceOfData(int data) => (data >> 3) & 7;
        public static bool HeadIsShaftOfData(int data) => (data & 4) != 0;

        /// <summary>
        /// 挂载面 → 方向向量。与引擎逐项一致：
        /// Source: Survivalcraft/Game/CellFace.cs:17-24 m_faceToPoint3
        /// </summary>
        public static Point3 FaceToOffset(int face)
        {
            switch (face)
            {
                case 0: return new Point3(0, 0, 1);
                case 1: return new Point3(1, 0, 0);
                case 2: return new Point3(0, 0, -1);
                case 3: return new Point3(-1, 0, 0);
                case 4: return new Point3(0, 1, 0);
                case 5: return new Point3(0, -1, 0);
                default: return Point3.Zero;
            }
        }

        public static Vector3 FaceToVector(int face)
        {
            Point3 p = FaceToOffset(face);
            return new Vector3(p.X, p.Y, p.Z);
        }
    }

    /// <summary>
    /// 移动组种类（线上 = piston.proto 的 MovingBlockKind）。
    /// <see cref="Unspecified"/> 只在**本端内部**表示"未知"；线上 0 由
    /// <see cref="MovingBlockKinds.TryNormalizeWire"/> 明确解释为"旧主机（那时只有活塞）"。
    /// </summary>
    public enum MovingBlockKind
    {
        Unspecified = 0,
        Piston = 1,
        Collapsing = 2
    }

    /// <summary>
    /// 线上 kind 数值 → 种类。**唯一**的兼容口径（只此一处，避免各处各自解释）：
    ///   · 0 = 旧主机不带 kind 字段（那个版本只复制活塞）⇒ PISTON；
    ///   · 1 = PISTON，2 = COLLAPSING；
    ///   · 其余数值 = 未知（调用方整条拒绝并计数，绝不猜）。
    /// </summary>
    public static class MovingBlockKinds
    {
        public static bool TryNormalizeWire(int wireValue, out MovingBlockKind kind,
            out string reason)
        {
            switch (wireValue)
            {
                case 0:
                case 1:
                    kind = MovingBlockKind.Piston;
                    reason = null;
                    return true;
                case 2:
                    kind = MovingBlockKind.Collapsing;
                    reason = null;
                    return true;
                default:
                    kind = MovingBlockKind.Unspecified;
                    reason = "kind-" + wireValue;
                    return false;
            }
        }

        /// <summary>编码前的内部校验：只能写 PISTON / COLLAPSING（不写 Unspecified）。</summary>
        public static bool IsEncodable(MovingBlockKind kind) =>
            kind == MovingBlockKind.Piston || kind == MovingBlockKind.Collapsing;

        public static string Name(MovingBlockKind kind)
        {
            switch (kind)
            {
                case MovingBlockKind.Piston: return "piston";
                case MovingBlockKind.Collapsing: return "collapsing";
                default: return "unspecified";
            }
        }
    }

    /// <summary>
    /// 本地集合 / 待发送队列 / 结束消息的键：**种类 + 格**。
    /// 活塞格与塌落列起点格不可能同时是两个种类，但键里带上 kind 就永远不需要"看格子猜种类"。
    /// </summary>
    public readonly struct MovingBlockKey : IEquatable<MovingBlockKey>
    {
        public MovingBlockKey(MovingBlockKind kind, Point3 cell)
        {
            Kind = kind;
            Cell = cell;
        }

        public MovingBlockKind Kind { get; }
        public Point3 Cell { get; }

        public bool Equals(MovingBlockKey other) => Kind == other.Kind && Cell == other.Cell;

        public override bool Equals(object obj) => obj is MovingBlockKey other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 397) ^ Cell.GetHashCode();

        public override string ToString() =>
            MovingBlockKinds.Name(Kind) + "(" + Cell.X + "," + Cell.Y + "," + Cell.Z + ")";

        public static bool operator ==(MovingBlockKey left, MovingBlockKey right) =>
            left.Equals(right);

        public static bool operator !=(MovingBlockKey left, MovingBlockKey right) =>
            !left.Equals(right);
    }

    /// <summary>集合里的一个方块：Offset 相对活塞格，Value 是完整 cell value（contents + data）。</summary>
    public readonly struct PistonBlockData
    {
        public PistonBlockData(Point3 offset, int value)
        {
            Offset = offset;
            Value = value;
        }

        public Point3 Offset { get; }
        public int Value { get; }
        public int Contents => PistonMovePolicy.ContentsOf(Value);

        public string Describe() => "off(" + Offset.X + "," + Offset.Y + "," + Offset.Z +
            ") value=" + Value.ToString("X4");
    }

    /// <summary>一次移动组运动的完整引擎参数（与主机 AddMovingBlockSet 的入参一一对应）。</summary>
    public readonly struct PistonMoveData
    {
        public PistonMoveData(MovingBlockKind kind, int x, int y, int z, int pistonValue,
            int mode, int face, Vector3 start, Vector3 target, float speed, float acceleration,
            float drag, Vector2 smoothness, PistonBlockData[] blocks)
        {
            Kind = kind;
            X = x;
            Y = y;
            Z = z;
            PistonValue = pistonValue;
            Mode = mode;
            Face = face;
            Start = start;
            Target = target;
            Speed = speed;
            Acceleration = acceleration;
            Drag = drag;
            Smoothness = smoothness;
            Blocks = blocks ?? Array.Empty<PistonBlockData>();
        }

        /// <summary>
        /// 活塞的运动（兼容既有调用点）：kind = <see cref="MovingBlockKind.Piston"/>。
        /// 塌落运动必须用带 kind 的重载，绝不靠"默认值"掩盖种类。
        /// </summary>
        public PistonMoveData(int x, int y, int z, int pistonValue, int mode, int face,
            Vector3 start, Vector3 target, float speed, float acceleration, float drag,
            Vector2 smoothness, PistonBlockData[] blocks)
            : this(MovingBlockKind.Piston, x, y, z, pistonValue, mode, face, start, target,
                speed, acceleration, drag, smoothness, blocks)
        {
        }

        public MovingBlockKind Kind { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        /// <summary>活塞格完整 value（contents=237 + light + data）。塌落运动恒 0（源格已被引擎清掉）。</summary>
        public int PistonValue { get; }
        /// <summary>PistonMode：0 推 / 1 拉 / 2 严格拉（与 PistonValue 的 data 位一致）。塌落运动恒 0。</summary>
        public int Mode { get; }
        /// <summary>挂载面 0..5（与 PistonValue 的 data 位一致）。塌落运动恒 0。</summary>
        public int Face { get; }
        public Vector3 Start { get; }
        public Vector3 Target { get; }
        public float Speed { get; }
        public float Acceleration { get; }
        public float Drag { get; }
        public Vector2 Smoothness { get; }
        public PistonBlockData[] Blocks { get; }

        public Point3 Cell => new Point3(X, Y, Z);

        /// <summary>本地账本 / 待发送队列的键（种类 + 格）。</summary>
        public MovingBlockKey Key => new MovingBlockKey(Kind, Cell);

        public string Describe() => MovingBlockKinds.Name(Kind) + "(" + X + "," + Y + "," + Z +
            ")" + (Kind == MovingBlockKind.Collapsing
                ? " blocks=" + Blocks.Length
                : " face=" + Face + " mode=" + Mode + " blocks=" + Blocks.Length) +
            " start=(" + Start.X.ToString("F2") + "," + Start.Y.ToString("F2") + "," +
            Start.Z.ToString("F2") + ") target=(" + Target.X.ToString("F2") + "," +
            Target.Y.ToString("F2") + "," + Target.Z.ToString("F2") + ") speed=" +
            Speed.ToString("F2");
    }

    /// <summary>一条运动结束（主机侧判定 + 客户端移除本地集合的依据）。</summary>
    public readonly struct PistonMoveEndData
    {
        public PistonMoveEndData(MovingBlockKind kind, int x, int y, int z, bool completed,
            string reason, uint startEventId)
        {
            Kind = kind;
            X = x;
            Y = y;
            Z = z;
            Completed = completed;
            Reason = reason ?? string.Empty;
            StartEventId = startEventId;
        }

        /// <summary>活塞的结束（兼容既有调用点）：kind = <see cref="MovingBlockKind.Piston"/>。</summary>
        public PistonMoveEndData(int x, int y, int z, bool completed, string reason,
            uint startEventId)
            : this(MovingBlockKind.Piston, x, y, z, completed, reason, startEventId)
        {
        }

        public MovingBlockKind Kind { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        /// <summary>
        /// PISTON：true = 集合走到了 TargetPosition；false = 引擎提前结束。
        /// COLLAPSING：true = 掉到目标点（没撞到任何方块）；false = 撞到支撑方块正常落地（正常结束）。
        /// </summary>
        public bool Completed { get; }
        public string Reason { get; }
        /// <summary>这条结束对应的 PistonMoveEvent.event_id（客户端只移除同一次开起的本地集合）。</summary>
        public uint StartEventId { get; }

        public Point3 Cell => new Point3(X, Y, Z);

        public MovingBlockKey Key => new MovingBlockKey(Kind, Cell);

        public string Describe() => MovingBlockKinds.Name(Kind) + "(" + X + "," + Y + "," + Z +
            ") completed=" + Completed + " startId=" + StartEventId +
            (string.IsNullOrEmpty(Reason) ? "" : " reason=" + Reason);
    }

    /// <summary>一条 PistonMoveEvent（sender 侧已组好 / receiver 侧已解码）。</summary>
    public readonly struct PistonMoveStartBatch
    {
        public PistonMoveStartBatch(uint eventId, long serverTick, PistonMoveData[] moves)
        {
            EventId = eventId;
            ServerTick = serverTick;
            Moves = moves ?? Array.Empty<PistonMoveData>();
        }

        public uint EventId { get; }
        public long ServerTick { get; }
        public PistonMoveData[] Moves { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTick +
            " moves=" + Moves.Length;
    }

    /// <summary>一条 PistonMoveEnd（sender 侧已组好 / receiver 侧已解码）。</summary>
    public readonly struct PistonMoveEndBatch
    {
        public PistonMoveEndBatch(uint eventId, long serverTick, PistonMoveEndData[] moves)
        {
            EventId = eventId;
            ServerTick = serverTick;
            Moves = moves ?? Array.Empty<PistonMoveEndData>();
        }

        public uint EventId { get; }
        public long ServerTick { get; }
        public PistonMoveEndData[] Moves { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTick +
            " ends=" + Moves.Length;
    }

    /// <summary>
    /// 线上编解码 + 严格校验。非法载荷整条拒绝（返回 false + 原因），不做"猜一个默认值"。
    ///
    /// 校验项（全部来自引擎实际参数，不是启发式）：
    ///   · piston_value 的 contents 必须是 237；mode / face 必须与 data 位逐一相等；
    ///   · 位移必须沿 face 轴（引擎插值只沿该轴），且 |位移| ≤ 10.01 格；
    ///   · 每个 offset 必须沿 face 轴（点积 ≥ 0）、|分量| ≤ 9、同一批不允许重复 offset；
    ///   · 方块 value 的 contents 必须在 1..1023；活塞头（238）的 face / mode 必须与活塞一致。
    /// </summary>
    public static class PistonMoveCodec
    {
        public static bool TryEncodeStart(in PistonMoveStartBatch batch, out P.PistonMoveEvent message,
            out string reason)
        {
            message = null;
            if (!TryValidateStart(in batch, out reason)) return false;
            var encoded = new P.PistonMoveEvent
            {
                EventId = batch.EventId,
                ServerTick = batch.ServerTick
            };
            for (int i = 0; i < batch.Moves.Length; i++)
            {
                PistonMoveData move = batch.Moves[i];
                var item = new P.PistonMove
                {
                    X = move.X,
                    Y = move.Y,
                    Z = move.Z,
                    PistonValue = move.PistonValue,
                    Mode = move.Mode,
                    Face = move.Face,
                    Start = new P.Float3 { X = move.Start.X, Y = move.Start.Y, Z = move.Start.Z },
                    Target = new P.Float3 { X = move.Target.X, Y = move.Target.Y, Z = move.Target.Z },
                    Speed = move.Speed,
                    Acceleration = move.Acceleration,
                    Drag = move.Drag,
                    SmoothnessX = move.Smoothness.X,
                    SmoothnessY = move.Smoothness.Y,
                    Kind = (P.MovingBlockKind)(int)move.Kind
                };
                for (int k = 0; k < move.Blocks.Length; k++)
                {
                    PistonBlockData block = move.Blocks[k];
                    item.Blocks.Add(new P.PistonBlock
                    {
                        X = block.Offset.X,
                        Y = block.Offset.Y,
                        Z = block.Offset.Z,
                        Value = block.Value
                    });
                }
                encoded.Moves.Add(item);
            }
            message = encoded;
            reason = null;
            return true;
        }

        public static P.PistonMoveEvent EncodeStart(in PistonMoveStartBatch batch)
        {
            if (!TryEncodeStart(in batch, out P.PistonMoveEvent message, out string reason))
                throw new InvalidOperationException("PistonMoveEvent 无法编码：" + reason);
            return message;
        }

        public static bool TryDecodeStart(P.PistonMoveEvent message, out PistonMoveStartBatch batch,
            out string reason)
        {
            batch = default;
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
            int count = message.Moves.Count;
            if (count == 0)
            {
                reason = "moves-empty";
                return false;
            }
            if (count > PistonMovePolicy.MaxMovesPerMessage)
            {
                reason = "moves-count-" + count;
                return false;
            }
            var moves = new PistonMoveData[count];
            var seen = new HashSet<Point3>();
            for (int i = 0; i < count; i++)
            {
                P.PistonMove item = message.Moves[i];
                if (item == null)
                {
                    reason = "move-null-" + i;
                    return false;
                }
                if (!seen.Add(new Point3(item.X, item.Y, item.Z)))
                {
                    reason = "move-duplicate-" + item.X + "," + item.Y + "," + item.Z;
                    return false;
                }
                if (!TryDecodeMove(item, out PistonMoveData move, out string moveReason))
                {
                    reason = "move-" + i + ":" + moveReason;
                    return false;
                }
                if (!TryValidateMove(in move, out string invalidReason))
                {
                    reason = "move-" + i + ":" + invalidReason;
                    return false;
                }
                moves[i] = move;
            }
            batch = new PistonMoveStartBatch(message.EventId, message.ServerTick, moves);
            reason = null;
            return true;
        }

        /// <summary>
        /// 逐字段解码一条 move。kind 的线上兼容口径只在
        /// <see cref="MovingBlockKinds.TryNormalizeWire"/> 一处：0 = 旧主机（只有活塞），
        /// 未知数值整条拒绝（不猜）。
        /// </summary>
        private static bool TryDecodeMove(P.PistonMove item, out PistonMoveData move,
            out string reason)
        {
            if (!MovingBlockKinds.TryNormalizeWire((int)item.Kind, out MovingBlockKind kind,
                    out reason))
            {
                move = default;
                return false;
            }
            var blocks = new PistonBlockData[item.Blocks.Count];
            for (int k = 0; k < blocks.Length; k++)
            {
                P.PistonBlock block = item.Blocks[k];
                blocks[k] = block == null
                    ? new PistonBlockData(new Point3(int.MinValue), 0)
                    : new PistonBlockData(new Point3(block.X, block.Y, block.Z), block.Value);
            }
            Vector3 start = item.Start == null ? Vector3.Zero
                : new Vector3(item.Start.X, item.Start.Y, item.Start.Z);
            Vector3 target = item.Target == null ? Vector3.Zero
                : new Vector3(item.Target.X, item.Target.Y, item.Target.Z);
            move = new PistonMoveData(kind, item.X, item.Y, item.Z, item.PistonValue, item.Mode,
                item.Face, start, target, item.Speed, item.Acceleration, item.Drag,
                new Vector2(item.SmoothnessX, item.SmoothnessY), blocks);
            reason = null;
            return true;
        }

        public static bool TryEncodeEnd(in PistonMoveEndBatch batch, out P.PistonMoveEnd message,
            out string reason)
        {
            message = null;
            if (!TryValidateEnd(in batch, out reason)) return false;
            var encoded = new P.PistonMoveEnd
            {
                EventId = batch.EventId,
                ServerTick = batch.ServerTick
            };
            for (int i = 0; i < batch.Moves.Length; i++)
            {
                PistonMoveEndData item = batch.Moves[i];
                encoded.Moves.Add(new P.PistonMoveEndEntry
                {
                    X = item.X,
                    Y = item.Y,
                    Z = item.Z,
                    Completed = item.Completed,
                    Reason = item.Reason ?? string.Empty,
                    StartEventId = item.StartEventId,
                    Kind = (P.MovingBlockKind)(int)item.Kind
                });
            }
            message = encoded;
            reason = null;
            return true;
        }

        public static P.PistonMoveEnd EncodeEnd(in PistonMoveEndBatch batch)
        {
            if (!TryEncodeEnd(in batch, out P.PistonMoveEnd message, out string reason))
                throw new InvalidOperationException("PistonMoveEnd 无法编码：" + reason);
            return message;
        }

        public static bool TryDecodeEnd(P.PistonMoveEnd message, out PistonMoveEndBatch batch,
            out string reason)
        {
            batch = default;
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
            int count = message.Moves.Count;
            if (count == 0)
            {
                reason = "moves-empty";
                return false;
            }
            if (count > PistonMovePolicy.MaxMovesPerMessage)
            {
                reason = "moves-count-" + count;
                return false;
            }
            var moves = new PistonMoveEndData[count];
            var seen = new HashSet<Point3>();
            for (int i = 0; i < count; i++)
            {
                P.PistonMoveEndEntry item = message.Moves[i];
                if (item == null)
                {
                    reason = "end-null-" + i;
                    return false;
                }
                if (!IsCellValid(item.X, item.Y, item.Z))
                {
                    reason = "end-cell-" + item.X + "," + item.Y + "," + item.Z;
                    return false;
                }
                if (item.Reason != null && item.Reason.Length > 64)
                {
                    reason = "end-reason-length-" + item.Reason.Length;
                    return false;
                }
                if (item.StartEventId == 0)
                {
                    reason = "end-start-event-id-zero";
                    return false;
                }
                if (!seen.Add(new Point3(item.X, item.Y, item.Z)))
                {
                    reason = "end-duplicate-" + item.X + "," + item.Y + "," + item.Z;
                    return false;
                }
                if (!MovingBlockKinds.TryNormalizeWire((int)item.Kind, out MovingBlockKind kind,
                        out reason))
                {
                    return false;
                }
                moves[i] = new PistonMoveEndData(kind, item.X, item.Y, item.Z, item.Completed,
                    item.Reason ?? string.Empty, item.StartEventId);
            }
            batch = new PistonMoveEndBatch(message.EventId, message.ServerTick, moves);
            reason = null;
            return true;
        }

        private static bool TryValidateStart(in PistonMoveStartBatch batch, out string reason)
        {
            if (batch.EventId == 0)
            {
                reason = "event-id-zero";
                return false;
            }
            if (batch.ServerTick < 0)
            {
                reason = "server-tick-negative";
                return false;
            }
            if (batch.Moves == null || batch.Moves.Length == 0)
            {
                reason = "moves-empty";
                return false;
            }
            if (batch.Moves.Length > PistonMovePolicy.MaxMovesPerMessage)
            {
                reason = "moves-count-" + batch.Moves.Length;
                return false;
            }
            var seen = new HashSet<Point3>();
            for (int i = 0; i < batch.Moves.Length; i++)
            {
                PistonMoveData move = batch.Moves[i];
                if (!seen.Add(move.Cell))
                {
                    reason = "move-duplicate-" + move.X + "," + move.Y + "," + move.Z;
                    return false;
                }
                if (!TryValidateMove(in move, out string moveReason))
                {
                    reason = "move-" + i + ":" + moveReason;
                    return false;
                }
            }
            reason = null;
            return true;
        }

        private static bool TryValidateEnd(in PistonMoveEndBatch batch, out string reason)
        {
            if (batch.EventId == 0)
            {
                reason = "event-id-zero";
                return false;
            }
            if (batch.ServerTick < 0)
            {
                reason = "server-tick-negative";
                return false;
            }
            if (batch.Moves == null || batch.Moves.Length == 0)
            {
                reason = "moves-empty";
                return false;
            }
            if (batch.Moves.Length > PistonMovePolicy.MaxMovesPerMessage)
            {
                reason = "moves-count-" + batch.Moves.Length;
                return false;
            }
            var seen = new HashSet<Point3>();
            for (int i = 0; i < batch.Moves.Length; i++)
            {
                PistonMoveEndData item = batch.Moves[i];
                if (!IsCellValid(item.X, item.Y, item.Z))
                {
                    reason = "end-cell-" + item.X + "," + item.Y + "," + item.Z;
                    return false;
                }
                if (!MovingBlockKinds.IsEncodable(item.Kind))
                {
                    reason = "end-kind-" + (int)item.Kind;
                    return false;
                }
                if (item.Reason != null && item.Reason.Length > 64)
                {
                    reason = "end-reason-length-" + item.Reason.Length;
                    return false;
                }
                if (item.StartEventId == 0)
                {
                    reason = "end-start-event-id-zero";
                    return false;
                }
                if (!seen.Add(item.Cell))
                {
                    reason = "end-duplicate-" + item.X + "," + item.Y + "," + item.Z;
                    return false;
                }
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// 一次运动的完整合法性判定（编码前 / 解码后共用同一份）。按 kind 分派：
        ///   · PISTON     = 活塞的参数（contents=237 / mode / face / 沿 face 轴的 9 格位移 / 活塞头）；
        ///   · COLLAPSING = 塌落列的参数（整列沙子 / 沙砾 / 沿 -Y 的整列偏移 / 引擎目标公式）。
        /// 未知 kind 直接拒绝（不猜）。
        /// </summary>
        public static bool TryValidateMove(in PistonMoveData move, out string reason)
        {
            switch (move.Kind)
            {
                case MovingBlockKind.Piston:
                    return TryValidatePistonMove(in move, out reason);
                case MovingBlockKind.Collapsing:
                    return TryValidateCollapsingMove(in move, out reason);
                default:
                    reason = "kind-unspecified";
                    return false;
            }
        }

        private static bool TryValidatePistonMove(in PistonMoveData move, out string reason)
        {
            if (!IsCellValid(move.X, move.Y, move.Z))
            {
                reason = "cell-" + move.X + "," + move.Y + "," + move.Z;
                return false;
            }
            if (PistonMovePolicy.ContentsOf(move.PistonValue) != PistonMovePolicy.PistonContents)
            {
                reason = "piston-contents-" + PistonMovePolicy.ContentsOf(move.PistonValue);
                return false;
            }
            int data = PistonMovePolicy.DataOf(move.PistonValue);
            int expectedMode = PistonMovePolicy.PistonModeOfData(data);
            int expectedFace = PistonMovePolicy.PistonFaceOfData(data);
            if (move.Mode != expectedMode)
            {
                reason = "mode-mismatch-" + move.Mode + "-" + expectedMode;
                return false;
            }
            if (move.Face != expectedFace)
            {
                reason = "face-mismatch-" + move.Face + "-" + expectedFace;
                return false;
            }
            if (expectedMode > 2)
            {
                reason = "mode-" + expectedMode;
                return false;
            }
            if (expectedFace > 5)
            {
                reason = "face-" + expectedFace;
                return false;
            }
            if (!IsFinite(move.Speed) || move.Speed <= 0f ||
                move.Speed > PistonMovePolicy.MaxSpeed)
            {
                reason = "speed-" + move.Speed;
                return false;
            }
            if (!IsFinite(move.Acceleration) ||
                Math.Abs(move.Acceleration) > PistonMovePolicy.MaxAcceleration)
            {
                reason = "acceleration-" + move.Acceleration;
                return false;
            }
            if (!IsFinite(move.Drag) || move.Drag < 0f || move.Drag >= 1f)
            {
                reason = "drag-" + move.Drag;
                return false;
            }
            if (!IsFinite(move.Smoothness.X) || !IsFinite(move.Smoothness.Y) ||
                move.Smoothness.X < 0f || move.Smoothness.Y < 0f ||
                move.Smoothness.X > PistonMovePolicy.MaxSmoothness ||
                move.Smoothness.Y > PistonMovePolicy.MaxSmoothness)
            {
                reason = "smoothness-" + move.Smoothness.X + "," + move.Smoothness.Y;
                return false;
            }
            if (!IsPositionValid(move.Start) || !IsPositionValid(move.Target))
            {
                reason = "position-(" + move.Start.X + "," + move.Start.Y + "," + move.Start.Z +
                    ")-(" + move.Target.X + "," + move.Target.Y + "," + move.Target.Z + ")";
                return false;
            }
            Point3 direction = PistonMovePolicy.FaceToOffset(move.Face);
            Vector3 delta = move.Target - move.Start;
            float along = Vector3.Dot(delta, new Vector3(direction.X, direction.Y, direction.Z));
            Vector3 perpendicular = delta -
                new Vector3(direction.X, direction.Y, direction.Z) * along;
            if (perpendicular.Length() > PistonMovePolicy.DirectionEpsilon)
            {
                reason = "not-along-face";
                return false;
            }
            if (Math.Abs(along) < 1e-3f || Math.Abs(along) > PistonMovePolicy.MaxMoveDistance)
            {
                reason = "distance-" + along;
                return false;
            }
            PistonBlockData[] blocks = move.Blocks;
            if (blocks == null || blocks.Length == 0)
            {
                reason = "blocks-empty";
                return false;
            }
            if (blocks.Length > PistonMovePolicy.MaxBlocksPerMove)
            {
                reason = "blocks-count-" + blocks.Length;
                return false;
            }
            var offsets = new HashSet<Point3>();
            bool hasHead = false;
            for (int i = 0; i < blocks.Length; i++)
            {
                PistonBlockData block = blocks[i];
                Point3 offset = block.Offset;
                if (Math.Abs(offset.X) > PistonMovePolicy.MaxBlockOffset ||
                    Math.Abs(offset.Y) > PistonMovePolicy.MaxBlockOffset ||
                    Math.Abs(offset.Z) > PistonMovePolicy.MaxBlockOffset)
                {
                    reason = "block-offset-" + offset.X + "," + offset.Y + "," + offset.Z;
                    return false;
                }
                // 引擎的集合只沿 face 轴展开（MovePiston 用 offset += point）。
                int dot = offset.X * direction.X + offset.Y * direction.Y + offset.Z * direction.Z;
                int perpX = offset.X - direction.X * dot;
                int perpY = offset.Y - direction.Y * dot;
                int perpZ = offset.Z - direction.Z * dot;
                if (perpX != 0 || perpY != 0 || perpZ != 0)
                {
                    reason = "block-not-along-face-" + offset.X + "," + offset.Y + "," + offset.Z;
                    return false;
                }
                if (dot < 0)
                {
                    reason = "block-behind-piston-" + offset.X + "," + offset.Y + "," + offset.Z;
                    return false;
                }
                if (!offsets.Add(offset))
                {
                    reason = "block-duplicate-" + offset.X + "," + offset.Y + "," + offset.Z;
                    return false;
                }
                int contents = PistonMovePolicy.ContentsOf(block.Value);
                if (contents <= 0 || contents > PistonMovePolicy.ContentsMask)
                {
                    reason = "block-contents-" + contents;
                    return false;
                }
                if (move.Y + offset.Y < 0 || move.Y + offset.Y > 255)
                {
                    reason = "block-y-" + (move.Y + offset.Y);
                    return false;
                }
                if (contents != PistonMovePolicy.PistonHeadContents) continue;
                int headData = PistonMovePolicy.DataOf(block.Value);
                if (PistonMovePolicy.HeadFaceOfData(headData) != move.Face ||
                    PistonMovePolicy.HeadModeOfData(headData) != move.Mode)
                {
                    reason = "head-face-mode-" + PistonMovePolicy.HeadFaceOfData(headData) + "-" +
                        PistonMovePolicy.HeadModeOfData(headData);
                    return false;
                }
                hasHead = true;
            }
            if (!hasHead)
            {
                reason = "no-piston-head";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// 塌落列的合法性判定。全部来自引擎 TryCollapseColumn 的实际入参，不是启发式：
        ///   · 起点格 = 列最上面一格，y ≥ 1（引擎 :77 对 p.Y &lt;= 0 直接返回）；
        ///   · 起始位置必须**正好**是该格 (x,y,z)（引擎传 new Vector3(p)）；
        ///   · 方块 = 从起点往上连续的整列：offset = (0,i,0)，i = 0..count-1，count ≤ 256，
        ///     每个 value 的 contents 必须是沙子(7) / 沙砾(6)（引擎 HandledBlocks）；
        ///   · 目标位置 = (x, -count-1, z)（引擎 :100 的原式）；掉到目标点是"没撞到任何方块"。
        ///   · 速度 / 加速度 / 阻力 / 平滑度只做合法性区间（引擎当前是 0 / 10 / 0.7 / (0,0)），
        ///     客户端照搬主机数值复现同一段插值，不在这里写死引擎常量。
        /// </summary>
        private static bool TryValidateCollapsingMove(in PistonMoveData move, out string reason)
        {
            if (!IsCellValid(move.X, move.Y, move.Z))
            {
                reason = "cell-" + move.X + "," + move.Y + "," + move.Z;
                return false;
            }
            if (move.Y < 1)
            {
                reason = "collapsing-origin-y-" + move.Y;
                return false;
            }
            if (!IsFinite(move.Start.X) || !IsFinite(move.Start.Y) || !IsFinite(move.Start.Z) ||
                Math.Abs(move.Start.X - move.X) > 1e-4f ||
                Math.Abs(move.Start.Y - move.Y) > 1e-4f ||
                Math.Abs(move.Start.Z - move.Z) > 1e-4f)
            {
                reason = "start-not-cell-(" + move.Start.X + "," + move.Start.Y + "," +
                    move.Start.Z + ")";
                return false;
            }
            if (!IsFinite(move.Speed) || move.Speed < 0f || move.Speed > PistonMovePolicy.MaxSpeed)
            {
                reason = "speed-" + move.Speed;
                return false;
            }
            if (!IsFinite(move.Acceleration) ||
                Math.Abs(move.Acceleration) > PistonMovePolicy.MaxAcceleration)
            {
                reason = "acceleration-" + move.Acceleration;
                return false;
            }
            if (!IsFinite(move.Drag) || move.Drag < 0f || move.Drag >= 1f)
            {
                reason = "drag-" + move.Drag;
                return false;
            }
            if (!IsFinite(move.Smoothness.X) || !IsFinite(move.Smoothness.Y) ||
                move.Smoothness.X < 0f || move.Smoothness.Y < 0f ||
                move.Smoothness.X > PistonMovePolicy.MaxSmoothness ||
                move.Smoothness.Y > PistonMovePolicy.MaxSmoothness)
            {
                reason = "smoothness-" + move.Smoothness.X + "," + move.Smoothness.Y;
                return false;
            }
            PistonBlockData[] blocks = move.Blocks;
            if (blocks == null || blocks.Length == 0)
            {
                reason = "blocks-empty";
                return false;
            }
            if (blocks.Length > PistonMovePolicy.MaxCollapseBlocks)
            {
                reason = "blocks-count-" + blocks.Length;
                return false;
            }
            var offsets = new HashSet<Point3>();
            for (int i = 0; i < blocks.Length; i++)
            {
                PistonBlockData block = blocks[i];
                Point3 offset = block.Offset;
                if (offset.X != 0 || offset.Z != 0)
                {
                    reason = "block-offset-" + offset.X + "," + offset.Y + "," + offset.Z;
                    return false;
                }
                if (offset.Y < 0 || offset.Y > PistonMovePolicy.MaxCollapseOffset)
                {
                    reason = "block-offset-y-" + offset.Y;
                    return false;
                }
                if (offset.Y >= blocks.Length)
                {
                    // 引擎收集的是从起点开始**连续**的一列；空洞 = 不是引擎产生的集合。
                    reason = "block-column-gap-" + offset.Y + "-" + blocks.Length;
                    return false;
                }
                if (move.Y + offset.Y > 255)
                {
                    reason = "block-y-" + (move.Y + offset.Y);
                    return false;
                }
                if (!offsets.Add(offset))
                {
                    reason = "block-duplicate-" + offset.X + "," + offset.Y + "," + offset.Z;
                    return false;
                }
                int contents = PistonMovePolicy.ContentsOf(block.Value);
                if (!PistonMovePolicy.IsCollapsibleContents(contents))
                {
                    reason = "block-contents-" + contents;
                    return false;
                }
            }
            if (!IsFinite(move.Target.X) || !IsFinite(move.Target.Y) ||
                !IsFinite(move.Target.Z) ||
                move.Target.Y > -1f || move.Target.Y < -PistonMovePolicy.MaxCellCoordinate)
            {
                reason = "target-position-(" + move.Target.X + "," + move.Target.Y + "," +
                    move.Target.Z + ")";
                return false;
            }
            if (Math.Abs(move.Target.X - move.X) > 1e-4f ||
                Math.Abs(move.Target.Z - move.Z) > 1e-4f)
            {
                reason = "target-not-column-(" + move.Target.X + "," + move.Target.Z + ")";
                return false;
            }
            float expectedY = -blocks.Length - 1f;
            if (Math.Abs(move.Target.Y - expectedY) > 1e-4f)
            {
                reason = "target-y-" + move.Target.Y + "-" + expectedY;
                return false;
            }
            reason = null;
            return true;
        }

        private static bool IsCellValid(int x, int y, int z) =>
            y >= 0 && y <= 255 &&
            x >= -PistonMovePolicy.MaxCellCoordinate && x <= PistonMovePolicy.MaxCellCoordinate &&
            z >= -PistonMovePolicy.MaxCellCoordinate && z <= PistonMovePolicy.MaxCellCoordinate;

        private static bool IsPositionValid(Vector3 position) =>
            IsFinite(position.X) && IsFinite(position.Y) && IsFinite(position.Z) &&
            position.Y > -32f && position.Y < 288f &&
            position.X > -PistonMovePolicy.MaxCellCoordinate &&
            position.X < PistonMovePolicy.MaxCellCoordinate &&
            position.Z > -PistonMovePolicy.MaxCellCoordinate &&
            position.Z < PistonMovePolicy.MaxCellCoordinate;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// 客户端事件闸门：按 <c>(server_tick, event_id)</c> 排序 + 按 event_id 去重。
    /// TCP 本身有序，但重连 / 主机重开世界仍可能让旧批次赶到；重复的本地集合 = 两个活塞头，
    /// 一律显式拒绝并计数，绝不重放。
    /// </summary>
    public sealed class PistonMoveOrderer
    {
        private readonly HashSet<uint> m_seen = new HashSet<uint>();
        private readonly Queue<uint> m_seenOrder = new Queue<uint>();
        private readonly int m_capacity;
        private long m_lastTick = long.MinValue;
        private uint m_lastEventId;

        public PistonMoveOrderer(int capacity = 512)
        {
            m_capacity = capacity <= 0 ? 512 : capacity;
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
        public bool TryAccept(uint eventId, long serverTick, out string reason)
        {
            if (eventId == 0)
            {
                reason = "bad-id";
                return false;
            }
            if (m_seen.Contains(eventId))
            {
                Duplicates++;
                reason = "duplicate";
                return false;
            }
            if (m_lastTick != long.MinValue &&
                (serverTick < m_lastTick ||
                 (serverTick == m_lastTick && eventId <= m_lastEventId)))
            {
                OutOfOrder++;
                reason = "stale";
                return false;
            }
            m_seen.Add(eventId);
            m_seenOrder.Enqueue(eventId);
            while (m_seenOrder.Count > m_capacity)
                m_seen.Remove(m_seenOrder.Dequeue());
            m_lastTick = serverTick;
            m_lastEventId = eventId;
            Accepted++;
            reason = null;
            return true;
        }

        public string Describe() => "accepted=" + Accepted + " duplicate=" + Duplicates +
            " stale=" + OutOfOrder + " resets=" + Resets;
    }

    /// <summary>
    /// 主机侧待发送的运动开始队列：同一帧（tick）内多个活塞先全部登记，再由模块按
    /// <see cref="PistonMovePolicy.MaxMovesPerMessage"/> 拆成若干条消息（有界批次）。
    ///
    /// 队列本身有硬上限 <see cref="PistonMovePolicy.MaxPendingMoves"/>：超过时明确计入
    /// <see cref="Dropped"/>（调用方必须计数 + 限流日志），绝不无限增长、也不静默丢。
    /// 同一活塞重复上报（正常引擎不会发生）按"后者替换"处理并计入 <see cref="Replaced"/>。
    /// 实例复用，没有静态状态。
    /// </summary>
    public sealed class PistonMoveAccumulator
    {
        private readonly List<PistonMoveData> m_moves =
            new List<PistonMoveData>(PistonMovePolicy.MaxMovesPerMessage * 2);
        private readonly Dictionary<MovingBlockKey, int> m_index =
            new Dictionary<MovingBlockKey, int>();

        public int Count => m_moves.Count;
        /// <summary>因为队列上限而被明确丢掉的条数（调用方必须计数并限流记录）。</summary>
        public int Dropped { get; private set; }
        /// <summary>同一 tick 里同一个活塞再次上报（后者替换前者；正常引擎不会发生）。</summary>
        public int Replaced { get; private set; }

        public void Reset()
        {
            m_moves.Clear();
            m_index.Clear();
            Dropped = 0;
            Replaced = 0;
        }

        /// <summary>true = 已登记；false = 已计入 <see cref="Dropped"/>（不静默）。</summary>
        public bool TryAdd(in PistonMoveData move)
        {
            if (m_index.TryGetValue(move.Key, out int existing))
            {
                m_moves[existing] = move;
                Replaced++;
                return true;
            }
            if (m_moves.Count >= PistonMovePolicy.MaxPendingMoves)
            {
                Dropped++;
                return false;
            }
            m_index[move.Key] = m_moves.Count;
            m_moves.Add(move);
            return true;
        }

        /// <summary>取队首最多 count 条并从队列里移除（拆批发送用）。</summary>
        public PistonMoveData[] Take(int count)
        {
            int take = Math.Min(Math.Max(0, count), m_moves.Count);
            var result = new PistonMoveData[take];
            for (int i = 0; i < take; i++) result[i] = m_moves[i];
            if (take == 0) return result;
            m_moves.RemoveRange(0, take);
            RebuildIndex();
            return result;
        }

        /// <summary>当前队列内容（测试 / 诊断用；不改变队列）。</summary>
        public PistonMoveData[] ToArray() => m_moves.ToArray();

        private void RebuildIndex()
        {
            m_index.Clear();
            for (int i = 0; i < m_moves.Count; i++) m_index[m_moves[i].Key] = i;
        }
    }

    /// <summary>
    /// 客户端表现口径（纯逻辑）：距离上限与时长估计。
    ///   · 客户端只表现**本地可见范围 + 16 m 余量**内的活塞（相机看不到时不建本地集合）；
    ///   · 主机侧按玩家端上报的兴趣半径（区块 × 16 m）判"这个端要不要收"，与客户端同一口径；
    ///   · 时长只用于看门狗（结束事件迟迟不到时放弃本地动画），不参与插值。
    /// 距离与引擎一样按格角 <c>new Vector3(x,y,z)</c> 计算。
    /// </summary>
    public static class PistonPresentationPolicy
    {
        public static float DistanceToCell(int x, int y, int z, Vector3 observer)
        {
            float dx = x - observer.X;
            float dy = y - observer.Y;
            float dz = z - observer.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static float DistanceToCell(in PistonMoveData move, Vector3 observer) =>
            DistanceToCell(move.X, move.Y, move.Z, observer);

        /// <summary>客户端可见范围（米）→ 允许表现的距离上限（米）。</summary>
        public static float ClientViewLimit(float visibilityRange) =>
            Math.Max(PistonMovePolicy.MinViewDistance, visibilityRange) +
            PistonMovePolicy.PresentationMargin;

        /// <summary>主机侧：该玩家端兴趣半径（区块）→ 允许下发的最远距离（米）。</summary>
        public static float PeerRangeLimit(int radiusChunks) =>
            Math.Max(PistonMovePolicy.MinPeerRadiusChunks, radiusChunks) * 16f +
            PistonMovePolicy.PresentationMargin;

        public static bool IsWithinClientView(in PistonMoveData move, Vector3 observer,
            float visibilityRange, out float distance)
        {
            distance = DistanceToCell(in move, observer);
            return distance <= ClientViewLimit(visibilityRange);
        }

        public static bool IsPeerInRange(in PistonMoveData move, Vector3 peerPosition,
            int radiusChunks, out float distance)
        {
            distance = DistanceToCell(in move, peerPosition);
            return distance <= PeerRangeLimit(radiusChunks);
        }

        /// <summary>
        /// 估计一次运动的时长（毫秒）。引擎插值在两端按 smoothness 减速，这里按 1.5 倍保守估计，
        /// 只用于看门狗阈值（<see cref="WatchdogMs"/>），不影响运动本身。
        /// Source: Survivalcraft/Game/SubsystemMovingBlocks.cs:277-352（speed / smoothness 的双端减速）
        /// </summary>
        public static long EstimateDurationMs(in PistonMoveData move)
        {
            float distance = Vector3.Distance(move.Start, move.Target);
            float speed = move.Speed > 0.01f ? move.Speed : 0.01f;
            double seconds = distance / (double)speed * 1.5 + 0.25;
            double ms = seconds * 1000.0;
            if (ms < 250.0) ms = 250.0;
            if (ms > 30000.0) ms = 30000.0;
            return (long)ms;
        }

        /// <summary>结束事件迟迟不到时的放弃阈值（毫秒）。</summary>
        public static long WatchdogMs(in PistonMoveData move) =>
            EstimateDurationMs(in move) * 3 + PistonMovePolicy.WatchdogSlackMs;
    }
}

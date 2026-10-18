using System;
using Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 一条权威 CellChange 落到**本地只表现移动组**（活塞 / 塌落）影响区时的裁决结果。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs /
    /// SubsystemCollapsingBlockBehavior.cs）：
    ///   · 运动**开始**时引擎立刻写格：活塞格 → extended=true（contents 仍 237），
    ///     被移动方块格 → 0（空气）；塌落同理把整列源格清成空气。这些写格是"运动开始"的
    ///     一部分，本地表现集合必须继续插值，不能被它们取消。
    ///   · 运动**结束**（碰撞 / 拆除 / 落地）时 StopPiston / MovingBlocksStopped 把集合
    ///     **当前**位置（round(position)+offset）的方块值写回格子（非空气），并同步下发。
    ///     这些写格就是权威结果：本地表现必须立刻停在这里（否则会继续插值到原目标位置，
    ///     表现为"穿过被推方块约半格"）。本类只做判定，不写格。
    /// </summary>
    public enum PistonCellChangeOutcome
    {
        /// <summary>该格不在本地集合的影响区（起点格 / 方块扫掠区）内。</summary>
        Unrelated = 0,

        /// <summary>该格在影响区内，但属于引擎"运动开始"的写格（空气清源格 / 活塞格 extended 位）。</summary>
        StartStateAccepted = 1,

        /// <summary>权威结果落在影响区内：立刻移除本地集合，接受主机格值（还没被本地视觉越过）。</summary>
        Cancel = 2,

        /// <summary>同上，但本地视觉已经越过 / 压进该格（穿透已可见）→ 立刻剪掉这一段。</summary>
        CancelPenetration = 3
    }

    /// <summary>
    /// 本地只表现移动组的"影响区 + 权威格变化"判定（**纯逻辑**，不引用 Game，不写格）。
    /// 由 <c>Engine/PistonReplicaEngine.cs</c> 在 <c>TerrainBridge.ApplyCells</c> 落地主机
    /// CellChange 后逐格调用；P:\tmp\tsverify 直接编译同一份源码做 (a) / (d) 用例。
    ///
    /// 影响区（与引擎 StopPiston / 塌落落地写格的格子口径一致）：
    ///   · 起点格（活塞格 / 塌落列起点格 = 集合 Tag）；
    ///   · 每个方块的扫掠区：从 round(Start)+offset 到 round(Target)+offset 沿运动轴的整段格子
    ///     （活塞按挂载面方向；塌落按 -Y）。
    /// 判定：
    ///   · 起点格：活塞格 contents 仍是 237 → 开始状态（extended 位），接受不前取消；
    ///     其它内容 = 活塞被拆 / 替换 → 取消。塌落起点格被清成空气 → 开始状态；
    ///     出现非空气方块 → 取消。
    ///   · 方块扫掠区内的格子：空气 = 引擎开始时的清源格 → 接受；非空气 = 权威落地结果 → 取消。
    ///     "本地视觉是否已经越过该格"用 round(当前位置)+offset 沿运动轴的投影判断。
    /// </summary>
    public static class PistonLocalSetPolicy
    {
        /// <summary>运动轴（单位向量）：活塞 = 挂载面方向；塌落 = 向下。</summary>
        public static Point3 MovementAxis(in PistonMoveData move) =>
            move.Kind == MovingBlockKind.Collapsing
                ? new Point3(0, -1, 0)
                : PistonMovePolicy.FaceToOffset(move.Face);

        /// <summary>该格是否在本地集合的影响区内（起点格或任一方块的扫掠区）。</summary>
        public static bool CoversCell(in PistonMoveData move, Point3 cell)
        {
            if (cell == move.Cell) return true;
            Point3 axis = MovementAxis(in move);
            if (axis == Point3.Zero) return false;
            for (int i = 0; i < move.Blocks.Length; i++)
            {
                if (CoversCellForBlock(in move, axis, move.Blocks[i].Offset, cell)) return true;
            }
            return false;
        }

        /// <summary>
        /// 判定一条已落地的权威 CellChange（<paramref name="newContents"/> = 主机写入后的 contents）
        /// 对这条本地集合意味着什么。<paramref name="currentPosition"/> = 本地集合**当前**插值位置。
        /// </summary>
        public static PistonCellChangeOutcome Classify(in PistonMoveData move,
            Vector3 currentPosition, Point3 cell, int newContents, out string reason)
        {
            reason = null;
            if (cell == move.Cell)
            {
                if (move.Kind == MovingBlockKind.Collapsing)
                {
                    if (newContents == 0)
                    {
                        reason = "collapsing-source-cleared";
                        return PistonCellChangeOutcome.StartStateAccepted;
                    }
                    reason = "collapsing-source-occupied-" + newContents;
                    return PistonCellChangeOutcome.Cancel;
                }
                if (newContents == PistonMovePolicy.PistonContents)
                {
                    // 引擎开始运动时把活塞格写成 extended=true（contents 仍是活塞）；
                    // 本地集合自己画活塞头 / 杆，这一笔不能取消动画。
                    reason = "piston-extended-flag";
                    return PistonCellChangeOutcome.StartStateAccepted;
                }
                reason = "piston-anchor-" + newContents;
                return PistonCellChangeOutcome.Cancel;
            }
            Point3 axis = MovementAxis(in move);
            if (axis == Point3.Zero)
            {
                reason = null;
                return PistonCellChangeOutcome.Unrelated;
            }
            for (int i = 0; i < move.Blocks.Length; i++)
            {
                Point3 offset = move.Blocks[i].Offset;
                if (!CoversCellForBlock(in move, axis, offset, cell)) continue;
                if (newContents == 0)
                {
                    // 引擎开始运动时把这些方块原来的格子清成空气；本地集合正是从那里出发的。
                    reason = "start-clear";
                    return PistonCellChangeOutcome.StartStateAccepted;
                }
                reason = "authoritative-landing-" + newContents;
                return IsPenetrating(in move, axis, offset, currentPosition, cell)
                    ? PistonCellChangeOutcome.CancelPenetration
                    : PistonCellChangeOutcome.Cancel;
            }
            reason = null;
            return PistonCellChangeOutcome.Unrelated;
        }

        /// <summary>
        /// 本地视觉的该方块是否已经到达 / 越过权威格：比较 round(当前位置)+offset 与格子在运动轴上的投影。
        /// 引擎落地写格用的正是 round(position)+offset（Source: SubsystemPistonBlockBehavior.StopPiston）。
        /// </summary>
        public static bool IsPenetrating(in PistonMoveData move, Point3 axis, Point3 offset,
            Vector3 currentPosition, Point3 cell)
        {
            Point3 current = new Point3(Round(currentPosition.X) + offset.X,
                Round(currentPosition.Y) + offset.Y, Round(currentPosition.Z) + offset.Z);
            Point3 delta = cell - current;
            int along = delta.X * axis.X + delta.Y * axis.Y + delta.Z * axis.Z;
            return along <= 0;
        }

        private static bool CoversCellForBlock(in PistonMoveData move, Point3 axis, Point3 offset,
            Point3 cell)
        {
            // 方块在起点 / 终点的"落格"（引擎写格口径）沿运动轴各向延伸；中间整段都算影响区。
            Point3 start = new Point3(Round(move.Start.X) + offset.X,
                Round(move.Start.Y) + offset.Y, Round(move.Start.Z) + offset.Z);
            Point3 target = new Point3(Round(move.Target.X) + offset.X,
                Round(move.Target.Y) + offset.Y, Round(move.Target.Z) + offset.Z);
            Point3 delta = cell - start;
            int along = delta.X * axis.X + delta.Y * axis.Y + delta.Z * axis.Z;
            Point3 perpendicular = delta - new Point3(along * axis.X, along * axis.Y, along * axis.Z);
            if (perpendicular != Point3.Zero) return false;
            int targetAlong = (target.X - start.X) * axis.X + (target.Y - start.Y) * axis.Y +
                (target.Z - start.Z) * axis.Z;
            int min = Math.Min(0, targetAlong);
            int max = Math.Max(0, targetAlong);
            return along >= min && along <= max;
        }

        /// <summary>引擎 MathUtils.Round 是 float；落格统一按四舍五入取整（与 StopPiston 一致）。</summary>
        private static int Round(float value) => (int)Math.Round(value);
    }

    /// <summary>
    /// 一条 PistonMoveEnd 相对本地账本的处置结果（纯逻辑）。
    /// 排序口径：模块已按 (server_tick, event_id) 拒绝过期批次；这里再按**同一条运动的
    /// start_event_id** 做一次键级检查，保证"新一轮 Start 不会被旧一轮 End 取消"。
    /// </summary>
    public enum PistonEndDisposition
    {
        /// <summary>start_event_id 与当前本地集合一致 → 应用结束（移除本地集合）。</summary>
        Apply = 0,

        /// <summary>该键没有本地集合，且这条结束指向的 Start 之前已经收到过（被跳过 / 已移除）→ 正常忽略。</summary>
        NoLocalSet = 1,

        /// <summary>该键没有本地集合，结束指向的 Start 还没到过 → 结束先于开始（明确失败，计数）。</summary>
        EndBeforeStart = 2,

        /// <summary>该键已有更新一轮的本地集合；这条结束属于旧一轮 → 拒绝（绝不取消新动画，计数）。</summary>
        StaleEnd = 3
    }

    /// <summary>
    /// 结束消息的键级排序判定（纯逻辑）。由 <c>Engine/PistonReplicaEngine.Cancel</c> 使用；
    /// 自检直接调用本方法验证"旧结束不取消新一轮开始"。
    /// </summary>
    public static class PistonLocalSetOrdering
    {
        public static PistonEndDisposition Classify(bool hasLiveSet, uint liveStartEventId,
            bool hasSeenStart, uint lastSeenStartEventId, uint endStartEventId)
        {
            if (hasLiveSet)
                return liveStartEventId == endStartEventId
                    ? PistonEndDisposition.Apply
                    : PistonEndDisposition.StaleEnd;
            if (!hasSeenStart) return PistonEndDisposition.EndBeforeStart;
            return endStartEventId <= lastSeenStartEventId
                ? PistonEndDisposition.NoLocalSet
                : PistonEndDisposition.EndBeforeStart;
        }
    }
}

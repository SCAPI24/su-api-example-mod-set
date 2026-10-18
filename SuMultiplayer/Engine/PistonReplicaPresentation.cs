using System;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 移动组表现的引擎侧辅助（**只读 / 只表现**，不写格、不生成实体）。
    ///
    /// ① 客户端：本地只表现集合的活塞杆 / 活塞头（与引擎
    ///    <c>SubsystemPistonBlockBehavior.UpdateMovableBlocks</c> 同一份判定）。
    ///    引擎那段逻辑在主机上给 Id=="Piston" 的集合补 / 收活塞杆方块；客户端不跑活塞逻辑，
    ///    但运动中间必须看到杆在伸长 / 收回 —— 这里对**本地副本集合**做同样的
    ///    <c>IMovingBlockSet.SetBlock</c>（只改内存里的集合，绝不碰任何格子）。
    ///    塌落（Id=="CollapsingBlock"）没有杆，客户端只按消息建集合，不需要在本类维护外观。
    ///
    /// ② 主机：读引擎私有嵌套类 <c>SubsystemMovingBlocks+MovingBlockSet</c> 的运动字段
    ///    （StartPosition / TargetPosition / Speed / Acceleration / Drag / Smoothness）。这些字段是
    ///    <c>AddMovingBlockSet</c> 的入参，也是客户端复现同一段插值唯一需要的参数；
    ///    读不到时**明确失败**（返回原因，调用方计数 + 限流日志），绝不猜一个默认值。
    ///    StartPosition 只用于塌落：引擎给塌落集合的 Tag 是 null，主机 / 客户端只能按起点位置
    ///    识别"这条集合是哪一列"。
    /// </summary>
    internal static class PistonReplicaPresentation
    {
        /// <summary>客户端本地活塞只表现集合的 Id（引擎活塞逻辑只认 "Piston"，两者绝不混淆）。</summary>
        public const string ReplicaId = "PistonReplica";

        /// <summary>客户端本地塌落只表现集合的 Id（引擎塌落逻辑只认 "CollapsingBlock"）。</summary>
        public const string CollapsingReplicaId = "CollapsingBlockReplica";

        /// <summary>引擎自己的活塞集合 Id。Source: SubsystemPistonBlockBehavior.cs:34</summary>
        public const string EnginePistonId = "Piston";

        /// <summary>引擎自己的塌落集合 Id。Source: SubsystemCollapsingBlockBehavior.cs:10</summary>
        public const string EngineCollapsingId = "CollapsingBlock";

        /// <summary>按种类给客户端本地集合选 Id（两种集合绝不共用同一个 Id）。</summary>
        public static string ReplicaIdFor(SuMultiplayer.Net.MovingBlockKind kind) =>
            kind == SuMultiplayer.Net.MovingBlockKind.Collapsing ? CollapsingReplicaId : ReplicaId;

        /// <summary>这个 Id 是不是本 Mod 建的只表现集合（清理残留时用）。</summary>
        public static bool IsReplicaId(string id) =>
            string.Equals(id, ReplicaId, StringComparison.Ordinal) ||
            string.Equals(id, CollapsingReplicaId, StringComparison.Ordinal);

        /// <summary>
        /// 维护一个本地集合的活塞杆（只 SetBlock）。与引擎逐行一致：
        /// Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:233-270 UpdateMovableBlocks
        ///
        /// 返回 false = 该格已经不是活塞（区块没同步 / 世界不一致）→ 本帧不动它（调用方计数）。
        /// </summary>
        public static bool UpdateRod(SubsystemTerrain terrain, IMovingBlockSet set, out string reason)
        {
            reason = null;
            if (terrain?.Terrain == null)
            {
                reason = "no-terrain";
                return false;
            }
            if (!(set.Tag is Point3 position))
            {
                reason = "bad-tag";
                return false;
            }
            int cellValue = terrain.Terrain.GetCellValue(position.X, position.Y, position.Z);
            if (Game.Terrain.ExtractContents(cellValue) != PistonBlock.Index)
            {
                reason = "not-piston";
                return false;
            }
            int data = Game.Terrain.ExtractData(cellValue);
            PistonMode mode = PistonBlock.GetMode(data);
            int face = PistonBlock.GetFace(data);
            Point3 direction = CellFace.FaceToPoint3(face);
            int minOffset = int.MaxValue;
            ReadOnlyList<MovingBlock> blocks = set.Blocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                MovingBlock block = blocks[i];
                minOffset = MathUtils.Min(minOffset, block.Offset.X * direction.X +
                    block.Offset.Y * direction.Y + block.Offset.Z * direction.Z);
            }
            if (minOffset == int.MaxValue)
            {
                reason = "empty-set";
                return false;
            }
            Vector3 position3 = set.Position;
            float moving = position3.X * direction.X + position3.Y * direction.Y +
                position3.Z * direction.Z;
            float origin = position.X * direction.X + position.Y * direction.Y +
                position.Z * direction.Z;
            if (moving > origin)
            {
                // 向外：头已经离开 1 格以上 → 在**头后面**补一截活塞杆。
                if ((float)minOffset + moving - origin > 1f)
                {
                    set.SetBlock(direction * (minOffset - 1),
                        Game.Terrain.MakeBlockValue(PistonHeadBlock.Index, 0,
                            PistonHeadBlock.SetFace(PistonHeadBlock.SetIsShaft(
                                PistonHeadBlock.SetMode(0, mode), isShaft: true), face)));
                }
            }
            else if (moving < origin && (float)minOffset + moving - origin <= 0f)
            {
                // 向内：最靠后的一截杆已经落到活塞格里 → 收掉它。
                set.SetBlock(direction * minOffset, 0);
            }
            return true;
        }
    }

    /// <summary>
    /// 引擎私有嵌套类 <c>SubsystemMovingBlocks+MovingBlockSet</c> 的运动字段读取器。
    ///
    /// 为什么必须读它们：客户端要用**主机同一份参数**（起始 / 目标 / 速度 / 加速度 / 阻力 /
    /// 平滑度）复现插值；<see cref="IMovingBlockSet"/> 只暴露 Position / Blocks，TargetPosition /
    /// Speed / Smoothness 是私有嵌套类的 public 字段（引擎保存 / 加载也读这些字段）。
    /// 解析失败时**明确失败**（true 的取反 + reason），调用方计数并限流写日志 —— 不猜、不降级。
    /// 实例状态（不是静态），随活塞外壳存活。
    /// </summary>
    internal sealed class MovingBlockSetMotion
    {
        private Type m_type;
        private FieldInfo m_targetPosition;
        private FieldInfo m_speed;
        private FieldInfo m_acceleration;
        private FieldInfo m_drag;
        private FieldInfo m_smoothness;
        private string m_missing;
        private Type m_startType;
        private FieldInfo m_startPosition;
        private string m_startMissing;

        public bool TryRead(IMovingBlockSet set, out Vector3 targetPosition, out float speed,
            out float acceleration, out float drag, out Vector2 smoothness, out string reason)
        {
            targetPosition = Vector3.Zero;
            speed = 0f;
            acceleration = 0f;
            drag = 0f;
            smoothness = Vector2.Zero;
            if (set == null)
            {
                reason = "null-set";
                return false;
            }
            if (!EnsureResolved(set.GetType()))
            {
                reason = "missing-field:" + m_missing;
                return false;
            }
            try
            {
                targetPosition = (Vector3)m_targetPosition.GetValue(set);
                speed = (float)m_speed.GetValue(set);
                acceleration = (float)m_acceleration.GetValue(set);
                drag = (float)m_drag.GetValue(set);
                smoothness = (Vector2)m_smoothness.GetValue(set);
            }
            catch (Exception e)
            {
                reason = "read:" + e.GetType().Name;
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// 读集合的 <c>StartPosition</c>（= 主机 AddMovingBlockSet 的 position 入参）。
        /// 塌落集合的 Tag 是 null，只能靠它把"引擎残留集合"与"主机这条运动"对应起来。
        /// 读不到时明确失败（调用方计数 + 限流日志），绝不按当前 Position 猜（它已经在下落途中）。
        /// 与 <see cref="TryRead"/> 分开解析字段：StartPosition 缺失不能拖累活塞路径。
        /// </summary>
        public bool TryReadStartPosition(IMovingBlockSet set, out Vector3 startPosition,
            out string reason)
        {
            startPosition = Vector3.Zero;
            if (set == null)
            {
                reason = "null-set";
                return false;
            }
            if (!EnsureStartResolved(set.GetType()))
            {
                reason = "missing-field:" + m_startMissing;
                return false;
            }
            try
            {
                startPosition = (Vector3)m_startPosition.GetValue(set);
            }
            catch (Exception e)
            {
                reason = "read-start:" + e.GetType().Name;
                return false;
            }
            reason = null;
            return true;
        }

        private bool EnsureStartResolved(Type setType)
        {
            if (m_startType == setType) return m_startMissing == null;
            m_startType = setType;
            m_startPosition = setType.GetField("StartPosition", BindingFlags.Instance |
                BindingFlags.Public);
            m_startMissing = m_startPosition == null ? "StartPosition" : null;
            return m_startMissing == null;
        }

        private bool EnsureResolved(Type setType)
        {
            if (m_type == setType) return m_missing == null;
            m_type = setType;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            m_targetPosition = setType.GetField("TargetPosition", flags);
            m_speed = setType.GetField("Speed", flags);
            m_acceleration = setType.GetField("Acceleration", flags);
            m_drag = setType.GetField("Drag", flags);
            m_smoothness = setType.GetField("Smoothness", flags);
            if (m_targetPosition == null) m_missing = "TargetPosition";
            else if (m_speed == null) m_missing = "Speed";
            else if (m_acceleration == null) m_missing = "Acceleration";
            else if (m_drag == null) m_missing = "Drag";
            else if (m_smoothness == null) m_missing = "Smoothness";
            else m_missing = null;
            return m_missing == null;
        }
    }
}

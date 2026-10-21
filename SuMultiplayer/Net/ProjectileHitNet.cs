using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using PE = SuMultiplayer.Protocol.Entities;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 一次投射物命中身体的**表现**事件（主机中继）。
    ///
    /// 引擎事实（Source）：
    ///   · <c>ComponentMiner.AttackBody</c>（ComponentMiner.cs:506-526）在"攻击者是玩家且真的掉血"
    ///     时于命中点创建 <c>HitValueParticleSystem</c>（文字 = -伤害，速度 = 方向 + 攻击者速度）；
    ///   · 投射物命中生物也走它（SubsystemProjectiles.cs:221-240，<c>isMeleeAttack:false</c>）。
    /// 新 mod 的客户端副本**不跑** <c>SubsystemProjectiles.Update</c>
    /// （Engine/PickableReplicaSubsystems.cs:SuSubsystemProjectiles），所以永远不会执行那条
    /// AttackBody → 没有伤害数字。主机把**已经执行过**的命中中继给客户端，客户端只重放同一个
    /// 粒子：不扣血、不改格、不生成实体（血量权威仍在 PLAYER_VITALS_REPORT 95）。
    /// </summary>
    public readonly struct ProjectileHitData
    {
        public ProjectileHitData(long serverTick, uint eventId, Vector3 position, Vector3 direction,
            Vector3 velocity, float damage)
        {
            ServerTick = serverTick;
            EventId = eventId;
            Position = position;
            Direction = direction;
            Velocity = velocity;
            Damage = damage;
        }

        public long ServerTick { get; }

        /// <summary>主机会话内严格递增（客户端据此去重 / 排序）。</summary>
        public uint EventId { get; }

        /// <summary>主机权威命中点（身体射线结果）。</summary>
        public Vector3 Position { get; }

        /// <summary>投射物飞行方向（单位向量）。</summary>
        public Vector3 Direction { get; }

        /// <summary>命中瞬间攻击者的身体速度（引擎把它加进粒子速度）。</summary>
        public Vector3 Velocity { get; }

        /// <summary>(命中前血量 - 命中后血量) * AttackResilience，与引擎同一公式。</summary>
        public float Damage { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTick +
            " pos=(" + Position.X.ToString("F2") + "," + Position.Y.ToString("F2") + "," +
            Position.Z.ToString("F2") + ") damage=" + Damage.ToString("F3");
    }

    /// <summary>协议上限（超出一律拒绝，不夹紧、不猜默认值）。</summary>
    public static class ProjectileHitPolicy
    {
        /// <summary>主机一次 Update 最多中继的命中数（连发 / 散弹时有界）。</summary>
        public const int MaxEventsPerHostUpdate = 32;

        /// <summary>坐标合法范围（与其它事件协议同一口径）。</summary>
        public const float MaxPositionCoordinate = 3.0e7f;

        /// <summary>单次伤害上限（超过视为非法载荷）。</summary>
        public const float MaxDamage = 10000f;

        /// <summary>
        /// 低于该值视为"没掉血"：引擎 <c>num2 &gt; 0</c> 才建粒子，这里同一门槛。
        /// Source: Survivalcraft/Game/ComponentMiner.cs:521（<c>if (num2 &gt; 0f)</c>）
        /// </summary>
        public const float MinDamage = 1.0e-4f;

        /// <summary>Terrain 区块边长（Game.Terrain.ChunkSize；Net 层不引用 Game）。</summary>
        public const int ChunkSize = 16;
    }

    /// <summary>线上编解码 + 严格校验（非法载荷整条拒绝，不猜默认值）。</summary>
    public static class ProjectileHitCodec
    {
        public static bool TryEncode(in ProjectileHitData data,
            out PE.ProjectileHitEvent message, out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            message = new PE.ProjectileHitEvent
            {
                EventId = data.EventId,
                ServerTick = data.ServerTick,
                Position = ToVec3(data.Position),
                Direction = ToVec3(data.Direction),
                Velocity = ToVec3(data.Velocity),
                Damage = data.Damage
            };
            reason = null;
            return true;
        }

        public static bool TryDecode(PE.ProjectileHitEvent message, out ProjectileHitData data,
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
            if (!TryReadVec3(message.Position, out Vector3 position, out reason))
            {
                reason = "position-" + reason;
                return false;
            }
            if (!TryReadVec3(message.Direction, out Vector3 direction, out reason))
            {
                reason = "direction-" + reason;
                return false;
            }
            if (!TryReadVec3(message.Velocity, out Vector3 velocity, out reason))
            {
                reason = "velocity-" + reason;
                return false;
            }
            if (!IsFinite(message.Damage) || message.Damage <= 0f ||
                message.Damage > ProjectileHitPolicy.MaxDamage)
            {
                reason = "damage-" + message.Damage.ToString("F3");
                return false;
            }
            data = new ProjectileHitData(message.ServerTick, message.EventId, position, direction,
                velocity, message.Damage);
            reason = null;
            return true;
        }

        private static bool TryValidate(in ProjectileHitData data, out string reason)
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
            if (!IsFinite(data.Position) || MathUtils.Abs(data.Position.X) >
                    ProjectileHitPolicy.MaxPositionCoordinate ||
                MathUtils.Abs(data.Position.Y) > ProjectileHitPolicy.MaxPositionCoordinate ||
                MathUtils.Abs(data.Position.Z) > ProjectileHitPolicy.MaxPositionCoordinate)
            {
                reason = "position";
                return false;
            }
            if (!IsFinite(data.Direction) || data.Direction.LengthSquared() <= 1.0e-6f)
            {
                reason = "direction";
                return false;
            }
            if (!IsFinite(data.Velocity))
            {
                reason = "velocity";
                return false;
            }
            if (!IsFinite(data.Damage) || data.Damage <= 0f ||
                data.Damage > ProjectileHitPolicy.MaxDamage)
            {
                reason = "damage-" + data.Damage.ToString("F3");
                return false;
            }
            reason = null;
            return true;
        }

        private static PP.Vec3 ToVec3(Vector3 value) =>
            new PP.Vec3 { X = value.X, Y = value.Y, Z = value.Z };

        private static bool TryReadVec3(PP.Vec3 message, out Vector3 value, out string reason)
        {
            value = Vector3.Zero;
            if (message == null)
            {
                reason = "null";
                return false;
            }
            value = new Vector3(message.X, message.Y, message.Z);
            if (!IsFinite(value))
            {
                reason = "not-finite";
                return false;
            }
            if (MathUtils.Abs(message.X) > ProjectileHitPolicy.MaxPositionCoordinate ||
                MathUtils.Abs(message.Y) > ProjectileHitPolicy.MaxPositionCoordinate ||
                MathUtils.Abs(message.Z) > ProjectileHitPolicy.MaxPositionCoordinate)
            {
                reason = "out-of-range";
                return false;
            }
            reason = null;
            return true;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
    }

    /// <summary>
    /// 客户端事件闸门：按 <c>(server_tick, event_id)</c> 排序 + 按 event_id 去重
    /// （重连 / 主机重启后旧事件不重放）。
    /// </summary>
    public sealed class ProjectileHitOrderer
    {
        private readonly HashSet<uint> m_seen = new HashSet<uint>();
        private readonly Queue<uint> m_seenOrder = new Queue<uint>();
        private readonly int m_capacity;
        private long m_lastTick = long.MinValue;
        private uint m_lastEventId;

        public ProjectileHitOrderer(int capacity = 256)
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

        public bool TryAccept(in ProjectileHitData data, out string reason)
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
    /// "这个命中该不该（发给 / 表现在）这个玩家端"——世界是分区处理的：
    /// 主机载入整张世界，客户端只载入自己兴趣窗口内的区块。
    ///   · 主机：命中点所在区块必须落在该客户端上报的兴趣窗口（区块中心 + 半径）内；
    ///   · 客户端：命中点必须在本地可见范围内，且该区块在本地已经载入（否则不建粒子）。
    /// 两处都用同一份区块口径（<see cref="ProjectileHitPolicy.ChunkSize"/>）。
    /// </summary>
    public static class ProjectileHitViewPolicy
    {
        /// <summary>客户端可见范围（米）内的命中才表现；超出不建粒子（不夹紧到 0 半径）。</summary>
        public static bool IsWithinClientView(Vector3 position, Vector3 observer,
            float visibilityRange, out float distance)
        {
            distance = (position - observer).Length();
            float limit = visibilityRange > 0f ? visibilityRange : 0f;
            return distance <= limit;
        }

        /// <summary>命中点所在区块是否落在该客户端的兴趣窗口内（闭区间，单位 = 区块）。</summary>
        public static bool IsCellInInterestWindow(Vector3 position, int centerChunkX,
            int centerChunkZ, int radiusChunks)
        {
            int chunkX = ChunkOf(position.X);
            int chunkZ = ChunkOf(position.Z);
            int radius = radiusChunks < 0 ? 0 : radiusChunks;
            return Math.Abs(chunkX - centerChunkX) <= radius &&
                   Math.Abs(chunkZ - centerChunkZ) <= radius;
        }

        /// <summary>
        /// 世界坐标 → 区块下标。引擎口径是算术右移（负数向下取整），
        /// 所以这里用 floor 除法而不是强制转换。
        /// </summary>
        public static int ChunkOf(float coordinate) =>
            (int)Math.Floor(coordinate / ProjectileHitPolicy.ChunkSize);
    }
}

using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using P = SuMultiplayer.Protocol.Explosion;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// Explosion 协议的**纯逻辑层**（不引用 Game）：编解码、合法性校验、按
    /// (server_tick, event_id) 的排序 / 去重。主机与客户端处理器、以及 P:\tmp\tsverify
    /// 的自检都编译这同一份源码。
    ///
    /// 设计口径（与 Protocol/explosion.proto 的注释一致）：
    ///   · 主机是唯一模拟方：每个爆炸实例一条 <see cref="ExplosionEventData"/>；
    ///   · 客户端只做表现：事件里的 terrain / entity / damage 字段一律**不**落地；
    ///   · 客户端用道具（投掷 / 点燃引信）走请求 / 回执，被拒绝时**绝不**本地放爆炸。
    /// </summary>
    public static class ExplosionKinds
    {
    public const int Unspecified = 0;
    public const int Block = 1;
    public const int Chain = 2;
    public const int Throwable = 3;
    public const int Firework = 4;
    public const int Keg = 5;
    public const int Detonator = 6;
    public const int Lightning = 7;

    public static bool IsKnown(int kind) => kind >= Block && kind <= Lightning;

    public static string Name(int kind)
    {
        switch (kind)
        {
            case Block: return "block";
            case Chain: return "chain";
            case Throwable: return "throwable";
            case Firework: return "firework";
            case Keg: return "keg";
            case Detonator: return "detonator";
            case Lightning: return "lightning";
            default: return "kind-" + kind;
        }
    }
    }

    /// <summary>
    /// 爆炸表现口径（主机与客户端共用，避免两边各算一套）：
    ///   · radius = 由引擎压力换算的**可视半径**（格），夹紧 0.5..16；压力本身放在 ext 里，
    ///     客户端用它决定音量 / 镜头震动强度；
    ///   · 半径按引擎自己的扩散 / 粒子阈值标定（2026-10-26 修正）：引擎只给
    ///     "单格压力 ≳1.5" 的格子布置爆炸粒子（SubsystemExplosions.cs:477-489，
    ///     num5 = Saturate(单格压力/15 - 衰减)×rand(0.2,1) > 0.1），波前压力按
    ///     p/(格数^0.66 + 5·max(环-7,0)) 稀释（SimulateExplosion:299）。
    ///     用引擎已知毁伤半径标定 0.45·p^0.4：闪电 p=19→1.5 格（实测主机"只有一点"；
    ///     旧公式 1.5·√p=6.5 格，客户端铺满整球 = "很大一个圆"的根因）、
    ///     TNT p≈300→4 格、火药桶 p≈1500→8 格、p≥3000→11+ 格。
    ///   · 2026-10-26 二调（用户实测反馈）：0.45·p^0.4 铺满整球后客户端仍比主机
    ///     "多一圈"——引擎侧是阈值滤过的**稀疏**分布（外圈大量格子被 &gt;0.1 过滤），
    ///     客户端实心球的等效半径天然偏大一圈 → 公式结果整体再减 1 格
    ///     （最外一圈不再铺粒子）：闪电→1 格（下限钳住）、TNT→3 格、
    ///     火药桶→6.6 格、p≥3000→10+ 格。
    ///   · 2026-10-26 三调（用户实测反馈）：闪电档（p=19 → 公式 0.47）被下限钳回
    ///     1 格半径 = 客户端铺了中心 + 6 邻格共 7 格火团；主机实测**只有中间一格火团**，
    ///     周围一圈是**炸碎方块的碎屑投射物**（沙地上 = 沙块，由投射物实体复制下发）。
    ///     → 下限 1 → 0.5：距离 ≤0.5 只有中心格，闪电档只渲染 1 格火，
    ///     碎屑圈交给实体复制；中大压力档不受影响（p &gt; 27.5 公式结果本就 &gt; 1）。
    /// 这是表现口径，不是物理爆炸范围；物理范围始终由主机的 SubsystemExplosions 决定。
    /// </summary>
    public static class ExplosionVisualPolicy
    {
        public const float MaxRadius = 16f;
        public const float MinRadius = 0.5f;

        public static float RadiusFromPressure(float pressure)
        {
            if (!ExplosionEventCodec.IsFinite(pressure) || pressure <= 0f) return MinRadius;
            return MathUtils.Clamp(0.45f * MathUtils.Pow(pressure, 0.4f) - 1f, MinRadius, MaxRadius);
        }
    }

    /// <summary>ExplosionRequestKind（与 proto 数值一致）。</summary>
    public static class ExplosionRequestKinds
    {
        public const int Ignite = 1;
        public const int Throw = 2;

        public static bool IsKnown(int kind) => kind == Ignite || kind == Throw;
    }

    public readonly struct ExplosionEventData
    {
        public ExplosionEventData(uint eventId, long serverTick, Vector3 position, int kind,
            float radius, uint blockValue, bool cosmeticOnly, float pressure, bool incendiary)
        {
            EventId = eventId;
            ServerTick = serverTick;
            Position = position;
            Kind = kind;
            Radius = radius;
            BlockValue = blockValue;
            CosmeticOnly = cosmeticOnly;
            Pressure = pressure;
            Incendiary = incendiary;
        }

        public uint EventId { get; }
        public long ServerTick { get; }
        public Vector3 Position { get; }
        public int Kind { get; }
        public float Radius { get; }
        public uint BlockValue { get; }
        public bool CosmeticOnly { get; }
        public float Pressure { get; }
        public bool Incendiary { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTick + " " +
            ExplosionKinds.Name(Kind) + " pos=(" + Position.X.ToString("F2") + "," +
            Position.Y.ToString("F2") + "," + Position.Z.ToString("F2") + ") r=" +
            Radius.ToString("F2") + " p=" + Pressure.ToString("F2") +
            (CosmeticOnly ? " cosmetic" : string.Empty);
    }

    public readonly struct ExplosionRequestData
    {
        public ExplosionRequestData(long sequence, int kind, Vector3 position, Vector3 direction,
            int itemValue, int activeSlot, bool hasReach = false, float reach = 0f)
        {
            Sequence = sequence;
            Kind = kind;
            Position = position;
            Direction = direction;
            ItemValue = itemValue;
            ActiveSlot = activeSlot;
            HasReach = hasReach;
            Reach = reach;
        }

        public long Sequence { get; }
        public int Kind { get; }
        public Vector3 Position { get; }
        public Vector3 Direction { get; }
        public int ItemValue { get; }
        public int ActiveSlot { get; }
        /// <summary>请求是否携带了有效 reach（false = 旧客户端 / 旧载荷）。</summary>
        public bool HasReach { get; }
        /// <summary>请求者的有效交互距离（米）；<see cref="HasReach"/> = false 时无意义。</summary>
        public float Reach { get; }

        public string Describe() => "seq=" + Sequence + " kind=" +
            (Kind == ExplosionRequestKinds.Ignite ? "ignite" : Kind == ExplosionRequestKinds.Throw
                ? "throw" : "kind-" + Kind) + " item=" + ItemValue + " slot=" + ActiveSlot;
    }

    public readonly struct ExplosionResultData
    {
        public ExplosionResultData(long sequence, bool accepted, string reason, Vector3 position)
        {
            Sequence = sequence;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            Position = position;
        }

        public long Sequence { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public Vector3 Position { get; }
    }

    public readonly struct PlayerDamageData
    {
        public PlayerDamageData(uint eventId, long serverTick, int clientId, string identity,
            string name, Vector3 position, float damage, float healthAfter)
        {
            EventId = eventId;
            ServerTick = serverTick;
            ClientId = clientId;
            Identity = identity ?? string.Empty;
            Name = name ?? string.Empty;
            Position = position;
            Damage = damage;
            HealthAfter = healthAfter;
        }

        public uint EventId { get; }
        public long ServerTick { get; }
        public int ClientId { get; }
        public string Identity { get; }
        public string Name { get; }
        public Vector3 Position { get; }
        public float Damage { get; }
        public float HealthAfter { get; }
    }

    /// <summary>
    /// 线上编解码 + 严格校验。非法载荷整条拒绝（返回 false + 原因），不做"猜一个默认值"。
    /// </summary>
    public static class ExplosionEventCodec
    {
        /// <summary>ext = float32 pressure + byte flags（bit0 = incendiary）。</summary>
        public const int ExtBytes = 5;

        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static SuMultiplayer.Protocol.Position.Vec3 ToVec3(Vector3 value) =>
            new SuMultiplayer.Protocol.Position.Vec3 { X = value.X, Y = value.Y, Z = value.Z };

        public static byte[] EncodeExt(float pressure, bool incendiary)
        {
            var ext = new byte[ExtBytes];
            Buffer.BlockCopy(BitConverter.GetBytes(pressure), 0, ext, 0, 4);
            ext[4] = incendiary ? (byte)1 : (byte)0;
            return ext;
        }

        public static bool TryDecodeExt(ByteString ext, out float pressure, out bool incendiary,
            out string reason)
        {
            pressure = 0f;
            incendiary = false;
            if (ext == null || ext.Length == 0)
            {
                reason = "ext-missing";
                return false;
            }
            if (ext.Length != ExtBytes)
            {
                reason = "ext-length-" + ext.Length;
                return false;
            }
            byte[] bytes = ext.ToByteArray();
            pressure = BitConverter.ToSingle(bytes, 0);
            if (!IsFinite(pressure) || pressure < 0f || pressure > 5000f)
            {
                reason = "ext-pressure-" + pressure;
                return false;
            }
            byte flags = bytes[4];
            if ((flags & ~1) != 0)
            {
                reason = "ext-flags-" + flags;
                return false;
            }
            incendiary = (flags & 1) != 0;
            reason = null;
            return true;
        }

        public static P.ExplosionEvent Encode(in ExplosionEventData data) =>
            new P.ExplosionEvent
            {
                EventId = data.EventId,
                ServerTick = data.ServerTick,
                Position = ToVec3(data.Position),
                Kind = data.Kind,
                Radius = data.Radius,
                BlockValue = data.BlockValue,
                CosmeticOnly = data.CosmeticOnly,
                Ext = ByteString.CopyFrom(EncodeExt(data.Pressure, data.Incendiary))
            };

        public static bool TryDecode(P.ExplosionEvent message, out ExplosionEventData data,
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
            if (!ExplosionKinds.IsKnown(message.Kind))
            {
                reason = "kind-" + message.Kind;
                return false;
            }
            if (message.Position == null)
            {
                reason = "position-missing";
                return false;
            }
            var position = new Vector3(message.Position.X, message.Position.Y, message.Position.Z);
            if (!IsFinite(position.X) || !IsFinite(position.Y) || !IsFinite(position.Z) ||
                MathUtils.Abs(position.X) > 100000f || MathUtils.Abs(position.Y) > 100000f ||
                MathUtils.Abs(position.Z) > 100000f)
            {
                reason = "position-nonfinite";
                return false;
            }
            if (!IsFinite(message.Radius) || message.Radius < 0f || message.Radius > 5000f)
            {
                reason = "radius-" + message.Radius;
                return false;
            }
            if (!TryDecodeExt(message.Ext, out float pressure, out bool incendiary, out reason))
                return false;
            if (message.CosmeticOnly && message.Kind != ExplosionKinds.Firework)
            {
                reason = "cosmetic-nonfirework";
                return false;
            }
            if (message.Kind == ExplosionKinds.Firework && !message.CosmeticOnly)
            {
                reason = "firework-not-cosmetic";
                return false;
            }
            data = new ExplosionEventData(message.EventId, message.ServerTick, position, message.Kind,
                message.Radius, message.BlockValue, message.CosmeticOnly, pressure, incendiary);
            reason = null;
            return true;
        }

        public static P.ExplosionRequest EncodeRequest(in ExplosionRequestData request)
        {
            var message = new P.ExplosionRequest
            {
                Sequence = request.Sequence,
                Kind = (P.ExplosionRequestKind)request.Kind,
                Position = ToVec3(request.Position),
                Direction = ToVec3(request.Direction),
                ItemValue = request.ItemValue,
                ActiveSlot = request.ActiveSlot
            };
            if (request.HasReach) message.Reach = request.Reach;
            return message;
        }

        public static bool TryDecodeRequest(P.ExplosionRequest message,
            out ExplosionRequestData request, out string reason)
        {
            request = default;
            if (message == null)
            {
                reason = "null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "sequence-" + message.Sequence;
                return false;
            }
            int kind = (int)message.Kind;
            if (!ExplosionRequestKinds.IsKnown(kind))
            {
                reason = "kind-" + kind;
                return false;
            }
            if (message.Position == null || message.Direction == null)
            {
                reason = "vector-missing";
                return false;
            }
            var position = new Vector3(message.Position.X, message.Position.Y, message.Position.Z);
            var direction = new Vector3(message.Direction.X, message.Direction.Y, message.Direction.Z);
            if (!IsFinite(position.X) || !IsFinite(position.Y) || !IsFinite(position.Z) ||
                !IsFinite(direction.X) || !IsFinite(direction.Y) || !IsFinite(direction.Z))
            {
                reason = "vector-nonfinite";
                return false;
            }
            if (kind == ExplosionRequestKinds.Throw)
            {
                if (message.ItemValue == 0)
                {
                    reason = "item-zero";
                    return false;
                }
                if (direction.Length() < 0.5f)
                {
                    reason = "direction-zero";
                    return false;
                }
            }
            if (message.ActiveSlot < -1 || message.ActiveSlot > 64)
            {
                reason = "active-slot-" + message.ActiveSlot;
                return false;
            }
            request = new ExplosionRequestData(message.Sequence, kind, position, direction,
                message.ItemValue, message.ActiveSlot, message.HasReach, message.Reach);
            reason = null;
            return true;
        }

        public static P.ExplosionResult EncodeResult(in ExplosionResultData result) =>
            new P.ExplosionResult
            {
                Sequence = result.Sequence,
                Accepted = result.Accepted,
                Reason = result.Reason ?? string.Empty,
                Position = ToVec3(result.Position)
            };

        public static bool TryDecodeResult(P.ExplosionResult message, out ExplosionResultData result,
            out string reason)
        {
            result = default;
            if (message == null)
            {
                reason = "null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "sequence-" + message.Sequence;
                return false;
            }
            if (!message.Accepted && string.IsNullOrWhiteSpace(message.Reason))
            {
                reason = "rejection-without-reason";
                return false;
            }
            var position = message.Position == null
                ? Vector3.Zero
                : new Vector3(message.Position.X, message.Position.Y, message.Position.Z);
            result = new ExplosionResultData(message.Sequence, message.Accepted, message.Reason,
                position);
            reason = null;
            return true;
        }

        public static P.PlayerDamaged EncodeDamage(in PlayerDamageData damage) =>
            new P.PlayerDamaged
            {
                EventId = damage.EventId,
                ServerTick = damage.ServerTick,
                ClientId = damage.ClientId,
                Identity = damage.Identity ?? string.Empty,
                Name = damage.Name ?? string.Empty,
                Position = ToVec3(damage.Position),
                Damage = damage.Damage,
                HealthAfter = damage.HealthAfter
            };

        public static bool TryDecodeDamage(P.PlayerDamaged message, out PlayerDamageData damage,
            out string reason)
        {
            damage = default;
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
            if (message.ClientId < 0)
            {
                reason = "client-id-" + message.ClientId;
                return false;
            }
            if (!IsFinite(message.Damage) || message.Damage <= 0f ||
                !IsFinite(message.HealthAfter) || message.HealthAfter < 0f || message.HealthAfter > 1f)
            {
                reason = "damage-value";
                return false;
            }
            var position = message.Position == null
                ? Vector3.Zero
                : new Vector3(message.Position.X, message.Position.Y, message.Position.Z);
            damage = new PlayerDamageData(message.EventId, message.ServerTick, message.ClientId,
                message.Identity, message.Name, position, message.Damage, message.HealthAfter);
            reason = null;
            return true;
        }
    }

    /// <summary>
    /// 客户端事件闸门：按 (server_tick, event_id) 排序 + 按 event_id 去重。
    /// TCP 本身有序，但重连 / 主机重启仍可能让旧事件赶到；一律显式拒绝并计数，
    /// 绝不"再放一次"（重复的爆炸表现就是重复的伤害错觉）。
    /// </summary>
    public sealed class ExplosionEventOrderer
    {
        private readonly HashSet<uint> m_seen = new HashSet<uint>();
        private readonly Queue<uint> m_seenOrder = new Queue<uint>();
        private readonly int m_capacity;
        private long m_lastTick = long.MinValue;
        private uint m_lastEventId;

        public ExplosionEventOrderer(int capacity = 512)
        {
            m_capacity = capacity <= 0 ? 512 : capacity;
        }

        public long Accepted { get; private set; }
        public long Duplicates { get; private set; }
        public long OutOfOrder { get; private set; }
        public long Resets { get; private set; }
        public long LastTick => m_lastTick;
        public uint LastEventId => m_lastEventId;

        public void Reset(string reason)
        {
            m_seen.Clear();
            m_seenOrder.Clear();
            m_lastTick = long.MinValue;
            m_lastEventId = 0;
            Resets++;
        }

        /// <summary>false = 不应用（<paramref name="reason"/> 为 duplicate / stale / bad-id）。</summary>
        public bool TryAccept(in ExplosionEventData data, out string reason)
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
}

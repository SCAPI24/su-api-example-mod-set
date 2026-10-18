using System;
using Engine;
using Google.Protobuf;
using PE = SuMultiplayer.Protocol.Entities;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 一次武器开火的表现事件（主机中继，纯表现）。
    ///
    /// 客户端开火时只发 ProjectileFireRequest；主机用引擎自己的 FireProjectile 执行，
    /// 但引擎的 Completed 分支不播声音（声音在方块行为里）。因此主机执行成功后广播本事件：
    ///   · 主机本机按 shooter 位置播一次；
    ///   · 其他客户端按同一位置播一次；
    ///   · 开火者客户端跳过自己的回声（它已经在本地播过）。
    /// 本事件**不创建实体、不扣物品、不造成伤害**。
    ///
    /// Source: Survivalcraft/Game/SubsystemBowBlockBehavior.cs:OnAim（Audio/Bow）
    /// Source: Survivalcraft/Game/SubsystemCrossbowBlockBehavior.cs:OnAim（Bow + CrossbowBoing）
    /// Source: Survivalcraft/Game/SubsystemThrowableBlockBehavior.cs:OnAim（Audio/Throw）
    /// Source: Survivalcraft/Game/SubsystemMusketBlockBehavior.cs:OnAim（MusketFire / MusketMisfire）
    /// </summary>
    public readonly struct WeaponFireEventData
    {
        public WeaponFireEventData(int clientId, long serverTick, uint eventId, int kind,
            Vector3 position, Vector3 direction, bool misfire)
        {
            ClientId = clientId;
            ServerTick = serverTick;
            EventId = eventId;
            Kind = kind;
            Position = position;
            Direction = direction;
            Misfire = misfire;
        }

        /// <summary>开火者的主机传输 id（0 = 主机自己）。</summary>
        public int ClientId { get; }
        public long ServerTick { get; }
        /// <summary>主机会话内严格递增；客户端据此去重。</summary>
        public uint EventId { get; }
        /// <summary>ProjectileFireKinds（1..4）。</summary>
        public int Kind { get; }
        /// <summary>主机权威开火位置（客户端上报 origin 不参与）。</summary>
        public Vector3 Position { get; }
        /// <summary>主机权威视线方向。</summary>
        public Vector3 Direction { get; }
        public bool Misfire { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTick + " client=" +
            ClientId + " kind=" + ProjectileFireKinds.Name(Kind) + " pos=(" +
            Position.X.ToString("F2") + "," + Position.Y.ToString("F2") + "," +
            Position.Z.ToString("F2") + ")" + (Misfire ? " misfire" : string.Empty);
    }

    /// <summary>
    /// 线上编解码 + 严格校验：非法载荷整条拒绝（返回 false + 原因），不猜默认值。
    /// </summary>
    public static class WeaponFireCodec
    {
        public static bool TryEncode(in WeaponFireEventData data, out PE.WeaponFireEvent message,
            out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            message = new PE.WeaponFireEvent
            {
                ClientId = data.ClientId,
                ServerTick = data.ServerTick,
                EventId = data.EventId,
                Kind = data.Kind,
                Position = ToVec3(data.Position),
                Direction = ToVec3(Vector3.Normalize(data.Direction)),
                Misfire = data.Misfire
            };
            return true;
        }

        public static byte[] Encode(in WeaponFireEventData data)
        {
            if (!TryEncode(in data, out PE.WeaponFireEvent message, out string reason))
                throw new InvalidOperationException("武器开火表现编解码失败：" + reason);
            return message.ToByteArray();
        }

        public static bool TryDecode(PE.WeaponFireEvent message, out WeaponFireEventData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "weapon-fire-null";
                return false;
            }
            if (message.ClientId < 0)
            {
                reason = "weapon-fire-client-" + message.ClientId;
                return false;
            }
            if (message.EventId == 0)
            {
                reason = "weapon-fire-event-id-zero";
                return false;
            }
            if (!ProjectileFireKinds.IsKnown(message.Kind))
            {
                reason = "weapon-fire-kind-" + message.Kind;
                return false;
            }
            if (!TryReadVec3(message.Position, out Vector3 position))
            {
                reason = "weapon-fire-position";
                return false;
            }
            if (!TryReadVec3(message.Direction, out Vector3 direction) ||
                direction.LengthSquared() < 1e-6f)
            {
                reason = "weapon-fire-direction";
                return false;
            }
            data = new WeaponFireEventData(message.ClientId, message.ServerTick,
                message.EventId, message.Kind, position, Vector3.Normalize(direction),
                message.Misfire);
            return true;
        }

        public static bool TryDecode(byte[] payload, out WeaponFireEventData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (payload == null || payload.Length == 0)
            {
                reason = "weapon-fire-empty";
                return false;
            }
            PE.WeaponFireEvent message;
            try
            {
                message = PE.WeaponFireEvent.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "weapon-fire-parse-" + e.GetType().Name;
                return false;
            }
            return TryDecode(message, out data, out reason);
        }

        private static bool TryValidate(in WeaponFireEventData data, out string reason)
        {
            reason = null;
            if (data.ClientId < 0)
            {
                reason = "weapon-fire-client-" + data.ClientId;
                return false;
            }
            if (data.EventId == 0)
            {
                reason = "weapon-fire-event-id-zero";
                return false;
            }
            if (data.ServerTick < 0)
            {
                reason = "weapon-fire-server-tick-" + data.ServerTick;
                return false;
            }
            if (!ProjectileFireKinds.IsKnown(data.Kind))
            {
                reason = "weapon-fire-kind-" + data.Kind;
                return false;
            }
            if (!IsFinite(data.Position) || data.Position.LengthSquared() >
                    EntityReplicationLimits.MaxCoordinate * EntityReplicationLimits.MaxCoordinate)
            {
                reason = "weapon-fire-position";
                return false;
            }
            if (!IsFinite(data.Direction) || data.Direction.LengthSquared() < 1e-6f)
            {
                reason = "weapon-fire-direction";
                return false;
            }
            return true;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);

        private static bool TryReadVec3(PP.Vec3 message, out Vector3 value)
        {
            value = Vector3.Zero;
            if (message == null) return false;
            value = new Vector3(message.X, message.Y, message.Z);
            return IsFinite(value);
        }

        private static PP.Vec3 ToVec3(Vector3 value) =>
            new PP.Vec3 { X = value.X, Y = value.Y, Z = value.Z };
    }
}

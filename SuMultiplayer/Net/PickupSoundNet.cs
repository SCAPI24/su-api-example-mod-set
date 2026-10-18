using System;
using Engine;
using Google.Protobuf;
using PE = SuMultiplayer.Protocol.Entities;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 拾取声音中继的**纯逻辑层**（不引用 Game）：边界 / 编解码 / 严格校验 / 去重 / 限速 /
    /// 距离裁剪。主机侧（Modules/EntityReplicationModule.cs）与 P:\tmp\tsverify 编译同一份源码。
    ///
    /// 引擎事实：拾取声音只出现在主机侧 <c>SubsystemPickables.Update</c>：
    ///   <c>m_subsystemAudio.PlaySound("Audio/PickableCollected", 0.7f, -0.4f,
    ///   pickable.Position, 2f, autoDelay: false)</c>
    /// （Source: Survivalcraft/Game/SubsystemPickables.cs:210-213）。
    /// 客户端拾取走 PickableAcquireRequest → 主机执行 AcquireItems → 这段表现不在任何同步流里，
    /// 所以请求者听不到。本层只是那条声音的**表现复制**：不授予物品、不删除拾取物、不改地形。
    /// </summary>
    public static class PickupSoundPolicy
    {
        /// <summary>“附近客户端”的半径（m）：请求者恒定收到，其他玩家端按这个距离裁剪。</summary>
        public const float MaxDistance = 16f;

        /// <summary>主机每秒最多发出的拾取声音事件数（防请求刷屏）。</summary>
        public const int MaxEventsPerSecond = 12;

        /// <summary>客户端每秒最多接受 / 表现的事件数。</summary>
        public const int MaxReceiveEventsPerSecond = 30;

        /// <summary>坐标绝对值上限（与其它复制协议口径一致）。</summary>
        public const float MaxPositionCoordinate = 30000000f;

        public static bool IsValidPosition(Vector3 position) =>
            IsFinite(position.X) && IsFinite(position.Y) && IsFinite(position.Z) &&
            position.LengthSquared() <= MaxPositionCoordinate * MaxPositionCoordinate;

        public static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>一次拾取声音事件（纯值；与线上 PickupSoundEvent 一一对应）。</summary>
    public readonly struct PickupSoundEventData
    {
        public PickupSoundEventData(int sourceClientId, long serverTickMs, uint eventId,
            Vector3 position)
        {
            SourceClientId = sourceClientId;
            ServerTickMs = serverTickMs;
            EventId = eventId;
            Position = position;
        }

        /// <summary>拾取者的主机传输 id（恒 &gt; 0；主机自己的原生拾取不发事件）。</summary>
        public int SourceClientId { get; }
        public long ServerTickMs { get; }
        /// <summary>主机会话内严格递增；客户端据此去重 / 拒绝重放。</summary>
        public uint EventId { get; }
        /// <summary>主机权威拾取位置（客户端上报的位置不参与）。</summary>
        public Vector3 Position { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTickMs + " source=" +
            SourceClientId + " pos=(" + Position.X.ToString("F2") + "," +
            Position.Y.ToString("F2") + "," + Position.Z.ToString("F2") + ")";
    }

    /// <summary>线上编解码 + 严格校验：非法载荷整条拒绝（返回 false + 原因），不猜默认值。</summary>
    public static class PickupSoundCodec
    {
        public static bool TryEncode(in PickupSoundEventData data,
            out PE.PickupSoundEvent message, out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            message = new PE.PickupSoundEvent
            {
                SourceClientId = data.SourceClientId,
                ServerTickMs = data.ServerTickMs,
                EventId = data.EventId,
                Position = new PP.Vec3
                {
                    X = data.Position.X,
                    Y = data.Position.Y,
                    Z = data.Position.Z
                }
            };
            return true;
        }

        public static byte[] Encode(in PickupSoundEventData data)
        {
            if (!TryEncode(in data, out PE.PickupSoundEvent message, out string reason))
                throw new InvalidOperationException("拾取声音事件编码失败：" + reason);
            return message.ToByteArray();
        }

        public static bool TryDecode(PE.PickupSoundEvent message,
            out PickupSoundEventData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "pickup-sound-null";
                return false;
            }
            if (message.SourceClientId <= 0)
            {
                reason = "pickup-sound-source-" + message.SourceClientId;
                return false;
            }
            if (message.ServerTickMs < 0)
            {
                reason = "pickup-sound-tick-" + message.ServerTickMs;
                return false;
            }
            if (message.EventId == 0)
            {
                reason = "pickup-sound-event-id-zero";
                return false;
            }
            var position = new Vector3(message.Position == null ? float.NaN : message.Position.X,
                message.Position == null ? float.NaN : message.Position.Y,
                message.Position == null ? float.NaN : message.Position.Z);
            if (!PickupSoundPolicy.IsValidPosition(position))
            {
                reason = "pickup-sound-position";
                return false;
            }
            data = new PickupSoundEventData(message.SourceClientId, message.ServerTickMs,
                message.EventId, position);
            return true;
        }

        public static bool TryDecode(byte[] payload, out PickupSoundEventData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (payload == null || payload.Length == 0)
            {
                reason = "pickup-sound-empty-payload";
                return false;
            }
            PE.PickupSoundEvent message;
            try
            {
                message = PE.PickupSoundEvent.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "pickup-sound-parse-" + e.GetType().Name;
                return false;
            }
            return TryDecode(message, out data, out reason);
        }

        private static bool TryValidate(in PickupSoundEventData data, out string reason)
        {
            reason = null;
            if (data.SourceClientId <= 0)
            {
                reason = "pickup-sound-source-" + data.SourceClientId;
                return false;
            }
            if (data.ServerTickMs < 0)
            {
                reason = "pickup-sound-tick-" + data.ServerTickMs;
                return false;
            }
            if (data.EventId == 0)
            {
                reason = "pickup-sound-event-id-zero";
                return false;
            }
            if (!PickupSoundPolicy.IsValidPosition(data.Position))
            {
                reason = "pickup-sound-position";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 客户端：主机事件 id 只允许严格递增（TCP 有序）。重复 / 更旧 = 重放 / 乱序，丢弃并计数；
    /// 中间缺号 = 可恢复的丢失（仍然表现，计数 gap，不把后续事件一起丢掉）。
    /// </summary>
    public sealed class PickupSoundOrderer
    {
        public uint LastEventId { get; private set; }
        public long Accepted { get; private set; }
        public long Duplicates { get; private set; }
        public long Stale { get; private set; }
        public long Gaps { get; private set; }

        public bool TryAccept(in PickupSoundEventData data, out string reason)
        {
            reason = null;
            if (data.EventId == 0)
            {
                reason = "bad-id";
                return false;
            }
            if (data.EventId == LastEventId)
            {
                Duplicates++;
                reason = "duplicate";
                return false;
            }
            if (data.EventId < LastEventId)
            {
                Stale++;
                reason = "stale";
                return false;
            }
            if (LastEventId != 0 && data.EventId > LastEventId + 1) Gaps++;
            LastEventId = data.EventId;
            Accepted++;
            return true;
        }

        public void Reset()
        {
            LastEventId = 0;
            Accepted = 0;
            Duplicates = 0;
            Stale = 0;
            Gaps = 0;
        }
    }

    /// <summary>客户端：这条事件要不要在本机播放（请求者恒定播放；其他端按监听者距离裁剪）。</summary>
    public readonly struct PickupSoundPresentationPlan
    {
        public PickupSoundPresentationPlan(bool present, bool requester, int skippedDistance,
            bool needsListener)
        {
            Present = present;
            Requester = requester;
            SkippedDistance = skippedDistance;
            NeedsListener = needsListener;
        }

        public bool Present { get; }
        /// <summary>本端就是请求者：无论距离 / 监听者是否可用都要播（它必须听到）。</summary>
        public bool Requester { get; }
        public int SkippedDistance { get; }
        /// <summary>需要监听者位置却读不到（明确计数，不猜原点）。</summary>
        public bool NeedsListener { get; }
    }

    public static class PickupSoundPresentationPolicy
    {
        public static PickupSoundPresentationPlan Plan(in PickupSoundEventData sound,
            int localClientId, Vector3 listener, bool hasListener, float maxDistance)
        {
            bool requester = localClientId > 0 && sound.SourceClientId == localClientId;
            if (requester)
                return new PickupSoundPresentationPlan(true, true, 0, false);
            if (!hasListener)
                return new PickupSoundPresentationPlan(false, false, 0, true);
            if (Vector3.DistanceSquared(sound.Position, listener) >
                maxDistance * maxDistance)
            {
                return new PickupSoundPresentationPlan(false, false, 1, false);
            }
            return new PickupSoundPresentationPlan(true, false, 0, false);
        }
    }
}

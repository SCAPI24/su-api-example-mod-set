using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using P = SuMultiplayer.Protocol.Effects;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 水花表现的**纯逻辑层**（不引用 Game）：边界常量、编解码 / 严格校验、
    /// 主机收件人裁剪、客户端距离裁剪、排序去重、限速。主机 / 客户端的引擎适配与
    /// P:\tmp\tsverify 的自检编译同一份源码。
    ///
    /// 引擎事实：
    ///   · <c>SubsystemPickables.Update</c> 里，落水且 <c>!SplashGenerated</c> 的拾取物会
    ///     <c>AddParticleSystem(new WaterSplashParticleSystem(terrain, pickable.Position,
    ///     large: false))</c>，随后 <c>PlayRandomSound("Audio/Splashes", 1f,
    ///     random(-0.2, 0.2), pickable.Position, 6f, autoDelay: true)</c>，并把
    ///     <c>SplashGenerated</c> 置 true。
    ///     Source: Survivalcraft/Game/SubsystemPickables.cs:325-330
    ///   · <c>SubsystemProjectiles.Update</c> 里，投射物进入水时同样生成
    ///     <c>WaterSplashParticleSystem(large: false)</c> 与 <c>Audio/Splashes</c>
    ///     （位置 Y 取水面高度）。
    ///     Source: Survivalcraft/Game/SubsystemProjectiles.cs:383-390
    ///   · 粒子系统构造参数只有 (terrain, position, large)，其余（60 个粒子、大小 / 速度、
    ///     水面浮动）由 <c>WaterSplashParticleSystem</c> 自己决定。
    ///     Source: Survivalcraft/Game/WaterSplashParticleSystem.cs
    ///
    /// 这些都是主机本端表现，不在 EntityState 里；本协议只补表现，绝不让客户端写格 /
    /// 生成实体 / 造成伤害。
    /// </summary>
    public static class WaterSplashPolicy
    {
        /// <summary>一条 <c>WaterSplashEvent</c> 最多携带的水花数（主机封顶，超出留到下一批）。</summary>
        public const int MaxSplashesPerEvent = 16;

        /// <summary>水花中继距离（m）：超出最近玩家端距离的客户端收不到（明确计数）。</summary>
        public const float MaxRelayDistance = 48f;

        /// <summary>主机每秒最多发出的水花事件数（同时也是客户端每秒最多表现的事件数）。</summary>
        public const int MaxEventsPerSecond = 20;

        /// <summary>主机待发水花队列上限（超过时丢最旧的，明确计数 + 限流日志）。</summary>
        public const int MaxPendingSplashes = 256;

        /// <summary>坐标绝对值上限（与其它复制协议的坐标闸门口径一致）。</summary>
        public const float MaxPositionCoordinate = 30000000f;

        /// <summary>引擎水花声音参数（两组调用点完全一致）。</summary>
        public const string SplashSoundDirectory = "Audio/Splashes";
        public const float SplashSoundVolume = 1f;
        public const float SplashSoundPitchMin = -0.2f;
        public const float SplashSoundPitchMax = 0.2f;
        public const float SplashSoundMinDistance = 6f;
        public const bool SplashSoundAutoDelay = true;
    }

    /// <summary>一个已由主机创建的水花（纯值；与线上 <c>WaterSplash</c> 一一对应）。</summary>
    public readonly struct WaterSplashData
    {
        public WaterSplashData(Vector3 position, bool large, bool sound)
        {
            Position = position;
            Large = large;
            Sound = sound;
        }

        public Vector3 Position { get; }
        /// <summary><c>WaterSplashParticleSystem(..., large)</c> 的构造参数。</summary>
        public bool Large { get; }
        /// <summary>主机同时排了引擎的 <c>Audio/Splashes</c>（客户端应播放同一调用）。</summary>
        public bool Sound { get; }

        public string Describe() => "splash@(" + Position.X.ToString("F2") + "," +
            Position.Y.ToString("F2") + "," + Position.Z.ToString("F2") + ")" +
            (Large ? " large" : " small") + (Sound ? " sound" : " silent");
    }

    /// <summary>一次主机水花批次（同 tick，最多 <see cref="WaterSplashPolicy.MaxSplashesPerEvent"/> 个）。</summary>
    public readonly struct WaterSplashEventData
    {
        public WaterSplashEventData(long serverTickMs, uint eventId,
            WaterSplashData[] splashes)
        {
            ServerTickMs = serverTickMs;
            EventId = eventId;
            Splashes = splashes ?? Array.Empty<WaterSplashData>();
        }

        public long ServerTickMs { get; }
        /// <summary>主机会话内严格递增；客户端据此去重 / 拒绝重放。</summary>
        public uint EventId { get; }
        public WaterSplashData[] Splashes { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTickMs +
            " splashes=" + Splashes.Length;
    }

    /// <summary>
    /// 线上编解码 + 严格校验：非法载荷整条拒绝（返回 false + 原因），不猜默认值。
    /// 合法事件要求：server_tick_ms ≥ 0、event_id &gt; 0、1..Max 个水花，
    /// 每个水花坐标有限且在 <see cref="WaterSplashPolicy.MaxPositionCoordinate"/> 内。
    /// </summary>
    public static class WaterSplashCodec
    {
        public static bool TryEncode(in WaterSplashEventData data,
            out P.WaterSplashEvent message, out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            var encoded = new P.WaterSplashEvent
            {
                ServerTickMs = data.ServerTickMs,
                EventId = data.EventId
            };
            for (int i = 0; i < data.Splashes.Length; i++)
            {
                WaterSplashData splash = data.Splashes[i];
                encoded.Splashes.Add(new P.WaterSplash
                {
                    Position = ToVec3(splash.Position),
                    Large = splash.Large,
                    Sound = splash.Sound
                });
            }
            message = encoded;
            return true;
        }

        public static byte[] Encode(in WaterSplashEventData data)
        {
            if (!TryEncode(in data, out P.WaterSplashEvent message, out string reason))
                throw new InvalidOperationException("水花事件编解码失败：" + reason);
            return message.ToByteArray();
        }

        public static bool TryDecode(P.WaterSplashEvent message,
            out WaterSplashEventData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "splash-event-null";
                return false;
            }
            if (message.ServerTickMs < 0)
            {
                reason = "splash-tick-" + message.ServerTickMs;
                return false;
            }
            if (message.EventId == 0)
            {
                reason = "splash-event-id-zero";
                return false;
            }
            int count = message.Splashes == null ? 0 : message.Splashes.Count;
            if (count <= 0)
            {
                reason = "splash-empty";
                return false;
            }
            if (count > WaterSplashPolicy.MaxSplashesPerEvent)
            {
                reason = "splash-count-" + count;
                return false;
            }
            var splashes = new WaterSplashData[count];
            for (int i = 0; i < count; i++)
            {
                P.WaterSplash splash = message.Splashes[i];
                if (splash == null)
                {
                    reason = "splash-null-" + i;
                    return false;
                }
                if (!TryReadVec3(splash.Position, out Vector3 position))
                {
                    reason = "splash-position-" + i;
                    return false;
                }
                splashes[i] = new WaterSplashData(position, splash.Large, splash.Sound);
            }
            data = new WaterSplashEventData(message.ServerTickMs, message.EventId, splashes);
            return true;
        }

        public static bool TryDecode(byte[] payload, out WaterSplashEventData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (payload == null || payload.Length == 0)
            {
                reason = "splash-empty-payload";
                return false;
            }
            P.WaterSplashEvent message;
            try
            {
                message = P.WaterSplashEvent.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "splash-parse-" + e.GetType().Name;
                return false;
            }
            return TryDecode(message, out data, out reason);
        }

        private static bool TryValidate(in WaterSplashEventData data, out string reason)
        {
            reason = null;
            if (data.ServerTickMs < 0)
            {
                reason = "splash-tick-" + data.ServerTickMs;
                return false;
            }
            if (data.EventId == 0)
            {
                reason = "splash-event-id-zero";
                return false;
            }
            if (data.Splashes == null || data.Splashes.Length == 0)
            {
                reason = "splash-empty";
                return false;
            }
            if (data.Splashes.Length > WaterSplashPolicy.MaxSplashesPerEvent)
            {
                reason = "splash-count-" + data.Splashes.Length;
                return false;
            }
            for (int i = 0; i < data.Splashes.Length; i++)
            {
                if (!IsFinite(data.Splashes[i].Position) ||
                    data.Splashes[i].Position.LengthSquared() >
                        WaterSplashPolicy.MaxPositionCoordinate *
                        WaterSplashPolicy.MaxPositionCoordinate)
                {
                    reason = "splash-position-" + i;
                    return false;
                }
            }
            return true;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);

        private static PP.Vec3 ToVec3(Vector3 value) =>
            new PP.Vec3 { X = value.X, Y = value.Y, Z = value.Z };

        private static bool TryReadVec3(PP.Vec3 message, out Vector3 value)
        {
            value = Vector3.Zero;
            if (message == null) return false;
            if (!IsFinite(new Vector3(message.X, message.Y, message.Z))) return false;
            if (MathUtils.Abs(message.X) > WaterSplashPolicy.MaxPositionCoordinate ||
                MathUtils.Abs(message.Y) > WaterSplashPolicy.MaxPositionCoordinate ||
                MathUtils.Abs(message.Z) > WaterSplashPolicy.MaxPositionCoordinate)
                return false;
            value = new Vector3(message.X, message.Y, message.Z);
            return true;
        }
    }

    /// <summary>主机裁剪后发给一个客户端的水花子集。</summary>
    public readonly struct WaterSplashRecipient
    {
        public WaterSplashRecipient(int clientId, WaterSplashData[] splashes)
        {
            ClientId = clientId;
            Splashes = splashes ?? Array.Empty<WaterSplashData>();
        }

        public int ClientId { get; }
        public WaterSplashData[] Splashes { get; }
    }

    /// <summary>主机一次水花批次的收件人裁剪结果（按每个客户端到每个水花的距离分别裁剪）。</summary>
    public sealed class WaterSplashRelayPlan
    {
        public WaterSplashRelayPlan(WaterSplashRecipient[] recipients, int skippedDistance,
            int skippedNoPosition, int duplicatePeers)
        {
            Recipients = recipients ?? Array.Empty<WaterSplashRecipient>();
            SkippedDistance = skippedDistance;
            SkippedNoPosition = skippedNoPosition;
            DuplicatePeers = duplicatePeers;
        }

        public WaterSplashRecipient[] Recipients { get; }
        /// <summary>因为超出 <see cref="WaterSplashPolicy.MaxRelayDistance"/> 而没有下发的 (端, 水花) 对数。</summary>
        public int SkippedDistance { get; }
        /// <summary>读不到位置的端 × 本批水花数（不猜位置、不发）。</summary>
        public int SkippedNoPosition { get; }
        public int DuplicatePeers { get; }
    }

    /// <summary>
    /// 主机：这一批水花发给谁。规则：
    ///   · 每个在线玩家端按**自己的角色位置**到每个水花的距离分别裁剪；
    ///   · 读不到位置的端不猜、不发，计入 <see cref="WaterSplashRelayPlan.SkippedNoPosition"/>；
    ///   · 同一 clientId 重复出现只算一次，多余计数 <see cref="WaterSplashRelayPlan.DuplicatePeers"/>。
    /// </summary>
    public static class WaterSplashRelayPolicy
    {
        public static WaterSplashRelayPlan Plan(IReadOnlyList<RemoteSoundPeer> peers,
            WaterSplashData[] splashes, float maxDistance)
        {
            var recipients = new List<WaterSplashRecipient>(peers == null ? 0 : peers.Count);
            int skippedDistance = 0;
            int skippedNoPosition = 0;
            int duplicatePeers = 0;
            if (splashes == null || splashes.Length == 0)
                return new WaterSplashRelayPlan(Array.Empty<WaterSplashRecipient>(), 0, 0, 0);
            float maxDistanceSq = maxDistance * maxDistance;
            var seen = new HashSet<int>();
            if (peers != null)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    RemoteSoundPeer peer = peers[i];
                    if (peer.ClientId <= 0) continue;
                    if (!seen.Add(peer.ClientId))
                    {
                        duplicatePeers++;
                        continue;
                    }
                    if (!peer.HasPosition)
                    {
                        skippedNoPosition += splashes.Length;
                        continue;
                    }
                    var selected = new List<WaterSplashData>(splashes.Length);
                    for (int j = 0; j < splashes.Length; j++)
                    {
                        if (Vector3.DistanceSquared(peer.Position, splashes[j].Position) >
                            maxDistanceSq)
                        {
                            skippedDistance++;
                            continue;
                        }
                        selected.Add(splashes[j]);
                    }
                    if (selected.Count == 0) continue;
                    recipients.Add(new WaterSplashRecipient(peer.ClientId,
                        selected.ToArray()));
                }
            }
            return new WaterSplashRelayPlan(recipients.ToArray(), skippedDistance,
                skippedNoPosition, duplicatePeers);
        }
    }

    /// <summary>客户端按本机监听者距离裁剪一批水花后的结果。</summary>
    public readonly struct WaterSplashPresentationPlan
    {
        public WaterSplashPresentationPlan(WaterSplashData[] splashes, int skippedDistance)
        {
            Splashes = splashes ?? Array.Empty<WaterSplashData>();
            SkippedDistance = skippedDistance;
        }

        public WaterSplashData[] Splashes { get; }
        public int SkippedDistance { get; }
    }

    public static class WaterSplashPresentationPolicy
    {
        public static WaterSplashPresentationPlan Plan(WaterSplashData[] splashes,
            Vector3 listener, float maxDistance)
        {
            if (splashes == null || splashes.Length == 0)
                return new WaterSplashPresentationPlan(Array.Empty<WaterSplashData>(), 0);
            var present = new List<WaterSplashData>(splashes.Length);
            int skippedDistance = 0;
            float maxDistanceSq = maxDistance * maxDistance;
            for (int i = 0; i < splashes.Length; i++)
            {
                if (Vector3.DistanceSquared(splashes[i].Position, listener) > maxDistanceSq)
                {
                    skippedDistance++;
                    continue;
                }
                present.Add(splashes[i]);
            }
            return new WaterSplashPresentationPlan(present.ToArray(), skippedDistance);
        }
    }

    /// <summary>
    /// 客户端：主机事件 id 只允许严格递增（TCP 有序）。重复 / 更旧 = 重放 / 乱序，丢弃并计数；
    /// 中间缺号 = 可恢复的丢失（仍然表现，计数 gap，不把后续事件一起丢掉）。
    /// </summary>
    public sealed class WaterSplashOrderer
    {
        public uint LastEventId { get; private set; }
        public long Accepted { get; private set; }
        public long Duplicates { get; private set; }
        public long Stale { get; private set; }
        public long Gaps { get; private set; }

        public bool TryAccept(in WaterSplashEventData data, out string reason)
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

    /// <summary>按 1 秒滑窗计数的限速器（主机发 / 客户端收共用；无静态状态）。</summary>
    public sealed class WaterSplashRateLimiter
    {
        private readonly int m_maxPerSecond;
        private long m_windowStartMs;
        private int m_count;

        public WaterSplashRateLimiter(int maxPerSecond)
        {
            if (maxPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPerSecond));
            m_maxPerSecond = maxPerSecond;
        }

        public int MaxPerSecond => m_maxPerSecond;
        public long Skipped { get; private set; }

        public bool TryAccept(long nowMs)
        {
            if (m_windowStartMs == 0 || nowMs - m_windowStartMs >= 1000)
            {
                m_windowStartMs = nowMs == 0 ? 1 : nowMs;
                m_count = 0;
            }
            if (m_count >= m_maxPerSecond)
            {
                Skipped++;
                return false;
            }
            m_count++;
            return true;
        }

        public void Reset()
        {
            m_windowStartMs = 0;
            m_count = 0;
            Skipped = 0;
        }
    }
}

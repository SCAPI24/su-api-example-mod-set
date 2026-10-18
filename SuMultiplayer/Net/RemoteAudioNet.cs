using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using P = SuMultiplayer.Protocol.Remotesound;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 远程动作声音中继的**纯逻辑层**（不引用 Game）：边界常量、编解码 / 严格校验、
    /// 主机收件人裁剪（请求者恒定 + 附近端）、客户端距离裁剪、排序去重、限速。
    /// 主机 / 客户端处理器与 P:\tmp\tsverify 的自检编译同一份源码。
    ///
    /// 引擎事实：方块行为里的声音都走本端 <c>SubsystemAudio</c>（例如
    /// <c>SwitchElectricElement.cs:27</c> 的 Audio/Click、<c>SubsystemDoorBlockBehavior.cs:24</c>）。
    /// 主机替远端玩家执行交互时，这些调用只发生在主机 ⇒ 请求交互的客户端听不到。
    /// 本协议把主机在执行**远端请求**期间实际排进音频队列的声音补发给客户端（纯表现）。
    ///
    /// 真正的捕获 / 播放只在 Engine/RemoteActionAudioReplica.cs；本层不含任何引擎类型依赖，
    /// 因此边界 / 距离 / 去重 / 畸形判定都能无设备验证。
    /// </summary>
    public static class RemoteSoundPolicy
    {
        /// <summary>一条 <c>RemoteActionSound</c> 最多携带的声音数（主机封顶，超出明确计数）。</summary>
        public const int MaxSoundsPerEvent = 8;

        /// <summary>声音内容名长度上限（引擎内容名远短于此；超长整条拒绝）。</summary>
        public const int MaxSoundNameLength = 160;

        /// <summary>中继距离（m）：请求者恒定收到，其他玩家端按此距离裁剪。</summary>
        public const float MaxRelayDistance = 48f;

        /// <summary>主机每秒最多打开的捕获窗口数（也是每秒最多发出的中继事件数）。</summary>
        public const int MaxEventsPerSecond = 20;

        /// <summary>客户端每秒最多接受 / 表现的中继事件数（防音频刷屏）。</summary>
        public const int MaxReceiveEventsPerSecond = 20;

        /// <summary>坐标绝对值上限（与其它复制协议的坐标闸门口径一致）。</summary>
        public const float MaxPositionCoordinate = 30000000f;

        /// <summary>音量合法区间（0 = 静音但仍允许，由引擎的 MinAudibleVolume 过滤）。</summary>
        public const float MaxVolume = 4f;

        /// <summary>音高合法区间（引擎 ToEnginePitch = 2^pitch）。</summary>
        public const float MinPitch = -2f;
        public const float MaxPitch = 2f;

        /// <summary>最小距离合法区间（引擎内容里常见 2 / 3 / 4 / 5 / 10 / 80）。</summary>
        public const float MinMinDistance = 0.05f;
        public const float MaxMinDistance = 1024f;

        /// <summary>主机侧事件 id 会话内严格递增；客户端按它去重 / 拒绝重放。</summary>
        public static bool IsValidSoundName(string sound)
        {
            if (string.IsNullOrWhiteSpace(sound)) return false;
            if (sound.Length > MaxSoundNameLength) return false;
            for (int i = 0; i < sound.Length; i++)
            {
                char c = sound[i];
                if (c < 0x20 || c == 0x7F) return false;
            }
            return true;
        }
    }

    /// <summary>一条已捕获 / 待播放的声音（纯值；与线上 <c>RemoteSoundClip</c> 一一对应）。</summary>
    public readonly struct RemoteSoundClipData
    {
        public RemoteSoundClipData(string sound, Vector3 position, float volume, float pitch,
            float minDistance, bool autoDelay)
        {
            Sound = sound;
            Position = position;
            Volume = volume;
            Pitch = pitch;
            MinDistance = minDistance;
            AutoDelay = autoDelay;
        }

        public string Sound { get; }
        public Vector3 Position { get; }
        public float Volume { get; }
        public float Pitch { get; }
        public float MinDistance { get; }
        public bool AutoDelay { get; }

        public string Describe() => Sound + "@(" + Position.X.ToString("F2") + "," +
            Position.Y.ToString("F2") + "," + Position.Z.ToString("F2") + ") vol=" +
            Volume.ToString("F3") + " pitch=" + Pitch.ToString("F2") + " min=" +
            MinDistance.ToString("F2") + (AutoDelay ? " autoDelay" : string.Empty);
    }

    /// <summary>一次远程动作产生的整批声音（主机 → 请求者 + 附近端）。</summary>
    public readonly struct RemoteSoundEventData
    {
        public RemoteSoundEventData(int sourceClientId, long serverTickMs, uint eventId,
            RemoteSoundClipData[] sounds)
        {
            SourceClientId = sourceClientId;
            ServerTickMs = serverTickMs;
            EventId = eventId;
            Sounds = sounds ?? Array.Empty<RemoteSoundClipData>();
        }

        /// <summary>产生这次动作的客户端（主机传输 id；恒 &gt; 0）。</summary>
        public int SourceClientId { get; }
        public long ServerTickMs { get; }
        /// <summary>主机会话内严格递增；客户端据此去重 / 拒绝重放。</summary>
        public uint EventId { get; }
        public RemoteSoundClipData[] Sounds { get; }

        public string Describe() => "id=" + EventId + " tick=" + ServerTickMs + " source=" +
            SourceClientId + " sounds=" + Sounds.Length;
    }

    /// <summary>
    /// 线上编解码 + 严格校验：非法载荷整条拒绝（返回 false + 原因），不猜默认值。
    /// 合法事件要求：source_client_id &gt; 0、server_tick_ms ≥ 0、event_id &gt; 0、
    /// 1..<see cref="RemoteSoundPolicy.MaxSoundsPerEvent"/> 条声音，每条名字 / 坐标 /
    /// 音量 / 音高 / 最小距离合法。
    /// </summary>
    public static class RemoteSoundCodec
    {
        public static bool TryEncode(in RemoteSoundEventData data, out P.RemoteActionSound message,
            out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            var encoded = new P.RemoteActionSound
            {
                SourceClientId = data.SourceClientId,
                ServerTickMs = data.ServerTickMs,
                EventId = data.EventId
            };
            for (int i = 0; i < data.Sounds.Length; i++)
            {
                RemoteSoundClipData clip = data.Sounds[i];
                encoded.Sounds.Add(new P.RemoteSoundClip
                {
                    Sound = clip.Sound,
                    Position = ToVec3(clip.Position),
                    Volume = clip.Volume,
                    Pitch = clip.Pitch,
                    MinDistance = clip.MinDistance,
                    AutoDelay = clip.AutoDelay
                });
            }
            message = encoded;
            return true;
        }

        public static byte[] Encode(in RemoteSoundEventData data)
        {
            if (!TryEncode(in data, out P.RemoteActionSound message, out string reason))
                throw new InvalidOperationException("远程动作声音编解码失败：" + reason);
            return message.ToByteArray();
        }

        public static bool TryDecode(P.RemoteActionSound message, out RemoteSoundEventData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "remote-sound-null";
                return false;
            }
            if (message.SourceClientId <= 0)
            {
                reason = "remote-sound-source-" + message.SourceClientId;
                return false;
            }
            if (message.ServerTickMs < 0)
            {
                reason = "remote-sound-tick-" + message.ServerTickMs;
                return false;
            }
            if (message.EventId == 0)
            {
                reason = "remote-sound-event-id-zero";
                return false;
            }
            int count = message.Sounds == null ? 0 : message.Sounds.Count;
            if (count <= 0)
            {
                reason = "remote-sound-empty";
                return false;
            }
            if (count > RemoteSoundPolicy.MaxSoundsPerEvent)
            {
                reason = "remote-sound-count-" + count;
                return false;
            }
            var clips = new RemoteSoundClipData[count];
            for (int i = 0; i < count; i++)
            {
                P.RemoteSoundClip clip = message.Sounds[i];
                if (clip == null)
                {
                    reason = "remote-sound-clip-null-" + i;
                    return false;
                }
                if (!RemoteSoundPolicy.IsValidSoundName(clip.Sound))
                {
                    reason = "remote-sound-name-" + i;
                    return false;
                }
                if (!TryReadVec3(clip.Position, out Vector3 position))
                {
                    reason = "remote-sound-position-" + i;
                    return false;
                }
                if (!IsFinite(clip.Volume) || clip.Volume < 0f ||
                    clip.Volume > RemoteSoundPolicy.MaxVolume)
                {
                    reason = "remote-sound-volume-" + i;
                    return false;
                }
                if (!IsFinite(clip.Pitch) || clip.Pitch < RemoteSoundPolicy.MinPitch ||
                    clip.Pitch > RemoteSoundPolicy.MaxPitch)
                {
                    reason = "remote-sound-pitch-" + i;
                    return false;
                }
                if (!IsFinite(clip.MinDistance) ||
                    clip.MinDistance < RemoteSoundPolicy.MinMinDistance ||
                    clip.MinDistance > RemoteSoundPolicy.MaxMinDistance)
                {
                    reason = "remote-sound-min-distance-" + i;
                    return false;
                }
                clips[i] = new RemoteSoundClipData(clip.Sound, position, clip.Volume,
                    clip.Pitch, clip.MinDistance, clip.AutoDelay);
            }
            data = new RemoteSoundEventData(message.SourceClientId, message.ServerTickMs,
                message.EventId, clips);
            return true;
        }

        public static bool TryDecode(byte[] payload, out RemoteSoundEventData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (payload == null || payload.Length == 0)
            {
                reason = "remote-sound-empty-payload";
                return false;
            }
            P.RemoteActionSound message;
            try
            {
                message = P.RemoteActionSound.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "remote-sound-parse-" + e.GetType().Name;
                return false;
            }
            return TryDecode(message, out data, out reason);
        }

        private static bool TryValidate(in RemoteSoundEventData data, out string reason)
        {
            reason = null;
            if (data.SourceClientId <= 0)
            {
                reason = "remote-sound-source-" + data.SourceClientId;
                return false;
            }
            if (data.ServerTickMs < 0)
            {
                reason = "remote-sound-tick-" + data.ServerTickMs;
                return false;
            }
            if (data.EventId == 0)
            {
                reason = "remote-sound-event-id-zero";
                return false;
            }
            if (data.Sounds == null || data.Sounds.Length == 0)
            {
                reason = "remote-sound-empty";
                return false;
            }
            if (data.Sounds.Length > RemoteSoundPolicy.MaxSoundsPerEvent)
            {
                reason = "remote-sound-count-" + data.Sounds.Length;
                return false;
            }
            for (int i = 0; i < data.Sounds.Length; i++)
            {
                RemoteSoundClipData clip = data.Sounds[i];
                if (!RemoteSoundPolicy.IsValidSoundName(clip.Sound))
                {
                    reason = "remote-sound-name-" + i;
                    return false;
                }
                if (!IsFinite(clip.Position) || clip.Position.LengthSquared() >
                        RemoteSoundPolicy.MaxPositionCoordinate *
                        RemoteSoundPolicy.MaxPositionCoordinate)
                {
                    reason = "remote-sound-position-" + i;
                    return false;
                }
                if (!IsFinite(clip.Volume) || clip.Volume < 0f ||
                    clip.Volume > RemoteSoundPolicy.MaxVolume)
                {
                    reason = "remote-sound-volume-" + i;
                    return false;
                }
                if (!IsFinite(clip.Pitch) || clip.Pitch < RemoteSoundPolicy.MinPitch ||
                    clip.Pitch > RemoteSoundPolicy.MaxPitch)
                {
                    reason = "remote-sound-pitch-" + i;
                    return false;
                }
                if (!IsFinite(clip.MinDistance) ||
                    clip.MinDistance < RemoteSoundPolicy.MinMinDistance ||
                    clip.MinDistance > RemoteSoundPolicy.MaxMinDistance)
                {
                    reason = "remote-sound-min-distance-" + i;
                    return false;
                }
            }
            return true;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

        private static PP.Vec3 ToVec3(Vector3 value) =>
            new PP.Vec3 { X = value.X, Y = value.Y, Z = value.Z };

        private static bool TryReadVec3(PP.Vec3 message, out Vector3 value)
        {
            value = Vector3.Zero;
            if (message == null) return false;
            value = new Vector3(message.X, message.Y, message.Z);
            return IsFinite(value) &&
                value.LengthSquared() <= RemoteSoundPolicy.MaxPositionCoordinate *
                RemoteSoundPolicy.MaxPositionCoordinate;
        }
    }

    /// <summary>主机侧一个玩家端的可见位置快照（读不到位置时 <see cref="HasPosition"/> = false）。</summary>
    public readonly struct RemoteSoundPeer
    {
        public RemoteSoundPeer(int clientId, bool hasPosition, Vector3 position)
        {
            ClientId = clientId;
            HasPosition = hasPosition;
            Position = position;
        }

        public int ClientId { get; }
        public bool HasPosition { get; }
        public Vector3 Position { get; }
    }

    /// <summary>一次中继的收件人裁剪结果（请求者恒定包含）。</summary>
    public sealed class RemoteSoundRecipientPlan
    {
        public RemoteSoundRecipientPlan(int[] recipients, int requesterIncluded,
            int skippedDistance, int skippedNoPosition, int duplicatePeers)
        {
            Recipients = recipients ?? Array.Empty<int>();
            RequesterIncluded = requesterIncluded;
            SkippedDistance = skippedDistance;
            SkippedNoPosition = skippedNoPosition;
            DuplicatePeers = duplicatePeers;
        }

        public int[] Recipients { get; }
        /// <summary>1 = 请求者被包含（恒为 1；0 只会出现在没有请求者的非法调用里）。</summary>
        public int RequesterIncluded { get; }
        /// <summary>因为超出 <see cref="RemoteSoundPolicy.MaxRelayDistance"/> 而没有收到的端数。</summary>
        public int SkippedDistance { get; }
        /// <summary>读不到位置（角色还没建好 / 已移除）而没有收到的端数。</summary>
        public int SkippedNoPosition { get; }
        /// <summary>同一 clientId 在 peer 列表里出现多次的去重数。</summary>
        public int DuplicatePeers { get; }
    }

    /// <summary>
    /// 主机：这次中继发给谁。规则（与任务口径一致）：
    ///   · **请求者恒定收到**（即使它的位置读不到 / 远端角色暂时没建好）；
    ///   · 其他玩家端只有读得到位置且到动作锚点 ≤ <see cref="RemoteSoundPolicy.MaxRelayDistance"/> 才收到；
    ///   · 读不到位置的端不猜、不发，计入 <see cref="RemoteSoundRecipientPlan.SkippedNoPosition"/>。
    /// </summary>
    public static class RemoteSoundRelayPolicy
    {
        public static RemoteSoundRecipientPlan Plan(int requesterClientId,
            IReadOnlyList<RemoteSoundPeer> peers, Vector3 anchor, float maxDistance)
        {
            var recipients = new List<int>(peers == null ? 1 : peers.Count + 1);
            int requesterIncluded = 0;
            int skippedDistance = 0;
            int skippedNoPosition = 0;
            int duplicatePeers = 0;
            float maxDistanceSq = maxDistance * maxDistance;
            if (peers != null)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    RemoteSoundPeer peer = peers[i];
                    if (peer.ClientId <= 0) continue;
                    if (peer.ClientId == requesterClientId)
                    {
                        // 请求者由下面无条件加入；这里只记录一次，避免重复。
                        if (requesterIncluded == 0)
                        {
                            requesterIncluded = 1;
                            recipients.Add(peer.ClientId);
                        }
                        else
                        {
                            duplicatePeers++;
                        }
                        continue;
                    }
                    if (recipients.Contains(peer.ClientId))
                    {
                        duplicatePeers++;
                        continue;
                    }
                    if (!peer.HasPosition)
                    {
                        skippedNoPosition++;
                        continue;
                    }
                    if (Vector3.DistanceSquared(peer.Position, anchor) > maxDistanceSq)
                    {
                        skippedDistance++;
                        continue;
                    }
                    recipients.Add(peer.ClientId);
                }
            }
            if (requesterClientId > 0 && requesterIncluded == 0)
            {
                requesterIncluded = 1;
                recipients.Insert(0, requesterClientId);
            }
            return new RemoteSoundRecipientPlan(recipients.ToArray(), requesterIncluded,
                skippedDistance, skippedNoPosition, duplicatePeers);
        }
    }

    /// <summary>客户端：按本机监听者距离裁剪一批声音（超出 <see cref="RemoteSoundPolicy.MaxRelayDistance"/> 的不表现）。</summary>
    public readonly struct RemoteSoundPresentationPlan
    {
        public RemoteSoundPresentationPlan(RemoteSoundClipData[] clips, int skippedDistance)
        {
            Clips = clips ?? Array.Empty<RemoteSoundClipData>();
            SkippedDistance = skippedDistance;
        }

        public RemoteSoundClipData[] Clips { get; }
        public int SkippedDistance { get; }
    }

    public static class RemoteSoundPresentationPolicy
    {
        public static RemoteSoundPresentationPlan Plan(RemoteSoundClipData[] sounds,
            Vector3 listener, float maxDistance)
        {
            if (sounds == null || sounds.Length == 0)
                return new RemoteSoundPresentationPlan(Array.Empty<RemoteSoundClipData>(), 0);
            var present = new List<RemoteSoundClipData>(sounds.Length);
            int skippedDistance = 0;
            float maxDistanceSq = maxDistance * maxDistance;
            for (int i = 0; i < sounds.Length; i++)
            {
                if (Vector3.DistanceSquared(sounds[i].Position, listener) > maxDistanceSq)
                {
                    skippedDistance++;
                    continue;
                }
                present.Add(sounds[i]);
            }
            return new RemoteSoundPresentationPlan(present.ToArray(), skippedDistance);
        }
    }

    /// <summary>
    /// 客户端：主机事件 id 只允许严格递增（TCP 有序）。重复 / 更旧 = 重放 / 乱序，丢弃并计数；
    /// 中间缺号 = 可恢复的丢失（仍然表现，计数 gap，不把后续事件一起丢掉）。
    /// </summary>
    public sealed class RemoteSoundOrderer
    {
        public uint LastEventId { get; private set; }
        public long Accepted { get; private set; }
        public long Duplicates { get; private set; }
        public long Stale { get; private set; }
        public long Gaps { get; private set; }

        public bool TryAccept(in RemoteSoundEventData data, out string reason)
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
    public sealed class RemoteSoundRateLimiter
    {
        private readonly int m_maxPerSecond;
        private long m_windowStartMs;
        private int m_count;

        public RemoteSoundRateLimiter(int maxPerSecond)
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

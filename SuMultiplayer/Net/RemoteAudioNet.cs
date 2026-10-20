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

        // ---------------------------------------------------------------- 全局主机声音中继
        //
        // 与"远端动作窗口"共用同一条 REMOTE_ACTION_SOUND (id 68) 消息；全局中继把每帧
        // 主机音频队列里**实际排入并即将播放**的声音按 100 ms 一轮、每端 ≤16 条批量下发。
        // 位置无法从引擎队列反解（SoundInfo 只有 Time/Name/Volume/Pitch/Pan，位置在入队前
        // 已折算成距离衰减），因此每条声音带 attenuation_mode + source_distance：
        //   · Absolute     = 位置 / 音量就是最终值（远端动作窗口，锚点精确）；
        //   · RelativeToHost = 位置 = 主机监听者，source_distance = 声源到主机监听者的距离；
        //   · NonPositional = 无位置的 2D 声音，只在主机监听者附近播放。
        // 客户端按自己到主机监听者的距离 re-project 音量（见 RemoteSoundPresentationPolicy）。

        /// <summary>一条全局中继消息最多携带的声音数（主机封顶；未发出的留在下一轮，不静默丢）。</summary>
        public const int MaxHostSoundsPerMessage = 16;

        /// <summary>每个玩家端每秒最多收到的全局中继声音数（按声音计，不是按消息计）。</summary>
        public const int MaxHostSoundsPerSecondPerPeer = 30;

        /// <summary>全局待发队列上限（声音数）。超出时丢最旧的并计入 CapDropped。</summary>
        public const int MaxPendingHostSounds = 256;

        /// <summary>待发声音最长保留时间；超时仍没发给任何端 → Expired（明确计数）。</summary>
        public const int HostPendingRetentionMs = 2000;

        /// <summary>全局中继轮询周期（毫秒）：100 ms = 每秒最多 10 轮批量。</summary>
        public const int HostBatchIntervalMs = 100;

        /// <summary>远端声音距离上界（米）：主机 / 客户端都按这个"可听半径"裁剪。</summary>
        public const float HostSourceDistanceMax = 1024f;

        /// <summary>引擎位置音频的默认最小距离（与内容里最常见的 2 一致）。</summary>
        public const float DefaultMinDistance = 2f;

        /// <summary>
        /// 复刻 <c>SubsystemAudio.CalculateVolume</c>（Source: Survivalcraft/Game/SubsystemAudio.cs:
        /// CalculateVolume）。position 重载用默认 rolloffFactor=2。
        /// 用于把"主机听感音量"反解成距离、以及客户端按自己的距离重算衰减。
        /// </summary>
        public static float CalculateVolume(float distance, float minDistance,
            float rolloffFactor = 2f)
        {
            if (distance > minDistance)
            {
                return minDistance / (minDistance +
                    MathUtils.Max(rolloffFactor * (distance - minDistance), 0f));
            }
            return 1f;
        }

        /// <summary>
        /// 距离反解：已知主机侧的衰减音量 v 与最小距离 m，求 d 使
        /// CalculateVolume(d, m) == v。v ≥ 1 表示落在最小距离内（d = 0 是安全下界）；
        /// v ≤ 0 视为不可听，返回 <paramref name="maxDistance"/>。
        /// 公式：v = m / (m + 2(d - m)) ⇒ d = m * (1 + 1/v) / 2。
        /// </summary>
        public static float InverseDistance(float volume, float minDistance,
            float maxDistance)
        {
            if (maxDistance < 0f) maxDistance = 0f;
            if (volume >= 1f) return 0f;
            if (volume <= 0.0001f) return maxDistance;
            float distance = minDistance * (1f + 1f / volume) * 0.5f;
            if (float.IsNaN(distance) || float.IsInfinity(distance)) return maxDistance;
            return MathUtils.Clamp(distance, 0f, maxDistance);
        }

        /// <summary>
        /// 客户端按自己的估算距离重算音量：主机记录的 volume 是"主机监听者处的音量"，
        /// 先除掉主机距离的衰减、再乘客户端距离的衰减。source_distance ≤ 0 时按未衰减处理。
        /// </summary>
        public static float RelativeVolume(float hostVolume, float sourceDistance,
            float clientDistance, float minDistance)
        {
            if (hostVolume <= 0f) return 0f;
            float hostAttenuation = sourceDistance <= 0f
                ? 1f
                : CalculateVolume(sourceDistance, minDistance);
            if (hostAttenuation <= 0.0001f) return 0f;
            float clientAttenuation = CalculateVolume(clientDistance, minDistance);
            float result = hostVolume * clientAttenuation / hostAttenuation;
            if (float.IsNaN(result) || float.IsInfinity(result)) return 0f;
            return MathUtils.Clamp(result, 0f, MaxVolume);
        }

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

    /// <summary>
    /// 客户端如何把主机记录的音量落到自己的监听者上（线上 <c>attenuation_mode</c>）：
    /// <list type="bullet">
    /// <item><see cref="Absolute"/>：position / volume 就是最终播放参数（远端动作窗口）；</item>
    /// <item><see cref="RelativeToHost"/>：position = 主机监听者，source_distance = 声源距离；</item>
    /// <item><see cref="NonPositional"/>：引擎里没有位置的 2D 声音，只在主机监听者附近播；</item>
    /// <item><see cref="SlotSource"/>：position = 主机某个**本机角色的真实位置**，
    ///       volume = 主机侧最近监听者处的衰减音量，source_distance = 该声源到主机最近
    ///       监听者的距离。客户端先用"自己最近的本地角色"到 position 的**精确距离**做
    ///       48 m 裁剪，再用 <see cref="RemoteSoundPolicy.RelativeVolume"/> 把主机侧衰减
    ///       还原成自己的距离衰减（不再需要方向各向同性假设）。</item>
    /// </list>
    /// 追加在末尾（append-only）：老载荷的 0/1/2 语义不变。
    /// </summary>
    public enum RemoteSoundAttenuation : byte
    {
        Absolute = 0,
        RelativeToHost = 1,
        NonPositional = 2,
        /// <summary>声源位置精确 = 主机某个本机角色（多角色槽位归属）；见类注释。</summary>
        SlotSource = 3
    }

    /// <summary>一条已捕获 / 待播放的声音（纯值；与线上 <c>RemoteSoundClip</c> 一一对应）。</summary>
    public readonly struct RemoteSoundClipData
    {
        public RemoteSoundClipData(string sound, Vector3 position, float volume, float pitch,
            float minDistance, bool autoDelay)
            : this(sound, position, volume, pitch, minDistance, autoDelay,
                RemoteSoundAttenuation.Absolute, 0f, 0f)
        {
        }

        public RemoteSoundClipData(string sound, Vector3 position, float volume, float pitch,
            float minDistance, bool autoDelay, RemoteSoundAttenuation attenuation,
            float sourceDistance)
            : this(sound, position, volume, pitch, minDistance, autoDelay, attenuation,
                sourceDistance, 0f)
        {
        }

        public RemoteSoundClipData(string sound, Vector3 position, float volume, float pitch,
            float minDistance, bool autoDelay, RemoteSoundAttenuation attenuation,
            float sourceDistance, float delay)
        {
            Sound = sound;
            Position = position;
            Volume = volume;
            Pitch = pitch;
            MinDistance = minDistance;
            AutoDelay = autoDelay;
            Attenuation = attenuation;
            SourceDistance = sourceDistance;
            Delay = delay;
        }

        public string Sound { get; }
        public Vector3 Position { get; }
        public float Volume { get; }
        public float Pitch { get; }
        public float MinDistance { get; }
        public bool AutoDelay { get; }
        /// <summary>音量的解释方式；见 <see cref="RemoteSoundAttenuation"/>。</summary>
        public RemoteSoundAttenuation Attenuation { get; }
        /// <summary>RelativeToHost 时 = 声源到主机监听者的距离（米）；其它模式为 0。</summary>
        public float SourceDistance { get; }
        /// <summary>
        /// 客户端播放前要等待的秒数（**只在本地表现计划里计算，不上线**）。
        /// 引擎 autoDelay 的声音按客户端自己的距离重算 = min(距离/120, 3)（Source:
        /// Survivalcraft/Game/SubsystemAudio.cs:CalculateDelay）；Present 用带 delay 的重载播放。
        /// </summary>
        public float Delay { get; }

        public string Describe() => Sound + "@(" + Position.X.ToString("F2") + "," +
            Position.Y.ToString("F2") + "," + Position.Z.ToString("F2") + ") vol=" +
            Volume.ToString("F3") + " pitch=" + Pitch.ToString("F2") + " min=" +
            MinDistance.ToString("F2") + (AutoDelay ? " autoDelay" : string.Empty) +
            (Attenuation == RemoteSoundAttenuation.Absolute
                ? string.Empty
                : " mode=" + Attenuation + " src=" + SourceDistance.ToString("F2")) +
            (Delay > 0.0001f ? " delay=" + Delay.ToString("F3") : string.Empty);
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
                    AutoDelay = clip.AutoDelay,
                    AttenuationMode = (uint)clip.Attenuation,
                    SourceDistance = clip.SourceDistance
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
            if (message.SourceClientId < 0)
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
            int maxCount = message.SourceClientId == 0
                ? RemoteSoundPolicy.MaxHostSoundsPerMessage
                : RemoteSoundPolicy.MaxSoundsPerEvent;
            if (count > maxCount)
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
                if (clip.AttenuationMode > (uint)RemoteSoundAttenuation.SlotSource)
                {
                    reason = "remote-sound-attenuation-" + i;
                    return false;
                }
                if (!IsFinite(clip.SourceDistance) || clip.SourceDistance < 0f ||
                    clip.SourceDistance > RemoteSoundPolicy.HostSourceDistanceMax)
                {
                    reason = "remote-sound-source-distance-" + i;
                    return false;
                }
                var attenuation = (RemoteSoundAttenuation)clip.AttenuationMode;
                // 两种"相对"模式都必须带主机侧距离；Absolute / NonPositional 忽略该字段。
                float sourceDistance = attenuation == RemoteSoundAttenuation.RelativeToHost ||
                    attenuation == RemoteSoundAttenuation.SlotSource
                    ? clip.SourceDistance
                    : 0f;
                clips[i] = new RemoteSoundClipData(clip.Sound, position, clip.Volume,
                    clip.Pitch, clip.MinDistance, clip.AutoDelay, attenuation,
                    sourceDistance);
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
            if (data.SourceClientId < 0)
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
            int maxCount = data.SourceClientId == 0
                ? RemoteSoundPolicy.MaxHostSoundsPerMessage
                : RemoteSoundPolicy.MaxSoundsPerEvent;
            if (data.Sounds.Length > maxCount)
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
                if (clip.Attenuation > RemoteSoundAttenuation.SlotSource)
                {
                    reason = "remote-sound-attenuation-" + i;
                    return false;
                }
                if (!IsFinite(clip.SourceDistance) || clip.SourceDistance < 0f ||
                    clip.SourceDistance > RemoteSoundPolicy.HostSourceDistanceMax)
                {
                    reason = "remote-sound-source-distance-" + i;
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
            : this(clips, skippedDistance, Array.Empty<float>(), 0, 0)
        {
        }

        public RemoteSoundPresentationPlan(RemoteSoundClipData[] clips, int skippedDistance,
            float[] effectiveDistances, int relativeCount, int nonPositionalCount)
        {
            Clips = clips ?? Array.Empty<RemoteSoundClipData>();
            SkippedDistance = skippedDistance;
            EffectiveDistances = effectiveDistances ?? Array.Empty<float>();
            RelativeCount = relativeCount;
            NonPositionalCount = nonPositionalCount;
        }

        /// <summary>
        /// 已按本机监听者换算好的最终播放参数：
        /// · Absolute 原样保留；
        /// · RelativeToHost / NonPositional 已改成"在监听者位置、按重算音量播放"，
        ///   因此 <see cref="IRemoteAudioPort"/> 的 Present 不需要再懂距离。
        /// </summary>
        public RemoteSoundClipData[] Clips { get; }
        public int SkippedDistance { get; }
        /// <summary>与 <see cref="Clips"/> 一一对应的"客户端到声源"估算距离（米）。</summary>
        public float[] EffectiveDistances { get; }
        public int RelativeCount { get; }
        public int NonPositionalCount { get; }
    }

    public static class RemoteSoundPresentationPolicy
    {
        /// <summary>
        /// 找一个点最近的监听者（= 接收端**最近的本地角色**）。多角色分裂屏时每个角色都是
        /// 一个监听者；空列表返回 -1（调用方必须显式失败，不猜原点）。
        /// </summary>
        public static int NearestListenerIndex(Vector3 position,
            IReadOnlyList<Vector3> listeners)
        {
            if (listeners == null) return -1;
            int best = -1;
            float bestDistanceSq = float.MaxValue;
            for (int i = 0; i < listeners.Count; i++)
            {
                Vector3 listener = listeners[i];
                if (!IsFinite(listener)) continue;
                float distanceSq = Vector3.DistanceSquared(listener, position);
                if (float.IsNaN(distanceSq)) continue;
                if (distanceSq < bestDistanceSq)
                {
                    bestDistanceSq = distanceSq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// 估算"这个声源到某个监听者"的距离：
        /// Absolute / SlotSource 用记录位置（SlotSource 的位置就是角色真实位置）；
        /// RelativeToHost 用 sqrt(到主机监听者距离² + 声源距离²)
        /// （方向无法从引擎队列反解，取各向同性期望值）；NonPositional 用监听者到主机监听者的距离。
        /// </summary>
        public static float EstimateClientDistance(in RemoteSoundClipData clip,
            Vector3 listener)
        {
            float direct = Vector3.Distance(clip.Position, listener);
            if (clip.Attenuation != RemoteSoundAttenuation.RelativeToHost) return direct;
            float source = clip.SourceDistance;
            return MathUtils.Sqrt(direct * direct + source * source);
        }

        /// <summary>单监听者重载（旧行为 / 单元测试用）。</summary>
        public static RemoteSoundPresentationPlan Plan(RemoteSoundClipData[] sounds,
            Vector3 listener, float maxDistance) =>
            Plan(sounds, new[] { listener }, maxDistance);

        /// <summary>
        /// 多监听者（多本机角色）重载：每条声音都按"离它最近的本地角色"算距离 / 重投影。
        ///   · 距离裁剪（<paramref name="maxDistance"/>）对所有模式一致；
        ///   · <see cref="RemoteSoundAttenuation.SlotSource"/>：位置 = 主机角色的真实位置，
        ///     用精确距离 + <see cref="RemoteSoundPolicy.RelativeVolume"/> 把"主机侧最近监听者
        ///     处的音量"换算成"本机最近角色处的音量"，并在该角色位置播放（引擎衰减 =
        ///     距离 0，最终音量就是换算值）；
        ///   · <see cref="RemoteSoundAttenuation.RelativeToHost"/>：保持原有各向同性重投影；
        ///   · 空监听者列表 = 明确失败（空计划 + skipped = 全部），绝不拿原点猜。
        /// </summary>
        public static RemoteSoundPresentationPlan Plan(RemoteSoundClipData[] sounds,
            IReadOnlyList<Vector3> listeners, float maxDistance)
        {
            if (sounds == null || sounds.Length == 0)
                return new RemoteSoundPresentationPlan(Array.Empty<RemoteSoundClipData>(), 0);
            var present = new List<RemoteSoundClipData>(sounds.Length);
            var distances = new List<float>(sounds.Length);
            int skippedDistance = 0;
            int relativeCount = 0;
            int nonPositionalCount = 0;
            if (listeners == null || listeners.Count == 0)
            {
                return new RemoteSoundPresentationPlan(Array.Empty<RemoteSoundClipData>(),
                    sounds.Length, Array.Empty<float>(), 0, 0);
            }
            float maxDistanceSq = maxDistance * maxDistance;
            for (int i = 0; i < sounds.Length; i++)
            {
                RemoteSoundClipData clip = sounds[i];
                int listenerIndex = NearestListenerIndex(clip.Position, listeners);
                if (listenerIndex < 0)
                {
                    skippedDistance++;
                    continue;
                }
                Vector3 listener = listeners[listenerIndex];
                float distance = EstimateClientDistance(in clip, listener);
                if (distance * distance > maxDistanceSq)
                {
                    skippedDistance++;
                    continue;
                }
                switch (clip.Attenuation)
                {
                    case RemoteSoundAttenuation.SlotSource:
                    {
                        // 主机音量 = 最近主机监听者处的衰减音量；hostDistance 是该监听者到
                        // 声源的距离。除法与乘法都用 DefaultMinDistance（与全局中继同口径），
                        // 因为引擎队列里没有 minDistance。
                        float volume = RemoteSoundPolicy.RelativeVolume(clip.Volume,
                            clip.SourceDistance, distance, RemoteSoundPolicy.DefaultMinDistance);
                        float delay = clip.AutoDelay
                            ? MathUtils.Min(distance / 120f, 3f)
                            : 0f;
                        present.Add(new RemoteSoundClipData(clip.Sound, listener, volume,
                            clip.Pitch, RemoteSoundPolicy.DefaultMinDistance, false,
                            RemoteSoundAttenuation.Absolute, 0f, delay));
                        relativeCount++;
                        break;
                    }
                    case RemoteSoundAttenuation.RelativeToHost:
                    {
                        float volume = RemoteSoundPolicy.RelativeVolume(clip.Volume,
                            clip.SourceDistance, distance, RemoteSoundPolicy.DefaultMinDistance);
                        float delay = clip.AutoDelay
                            ? MathUtils.Min(distance / 120f, 3f)
                            : 0f;
                        present.Add(new RemoteSoundClipData(clip.Sound, listener, volume,
                            clip.Pitch, RemoteSoundPolicy.DefaultMinDistance, false,
                            RemoteSoundAttenuation.Absolute, 0f, delay));
                        relativeCount++;
                        break;
                    }
                    case RemoteSoundAttenuation.NonPositional:
                    {
                        // 引擎里本来就没有位置：不额外衰减，只要求监听者在主机监听者附近。
                        present.Add(new RemoteSoundClipData(clip.Sound, listener, clip.Volume,
                            clip.Pitch, RemoteSoundPolicy.DefaultMinDistance, false,
                            RemoteSoundAttenuation.Absolute, 0f));
                        nonPositionalCount++;
                        break;
                    }
                    default:
                        present.Add(clip);
                        break;
                }
                distances.Add(distance);
            }
            return new RemoteSoundPresentationPlan(present.ToArray(), skippedDistance,
                distances.ToArray(), relativeCount, nonPositionalCount);
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsNaN(value.Y) && !float.IsNaN(value.Z) &&
            !float.IsInfinity(value.X) && !float.IsInfinity(value.Y) &&
            !float.IsInfinity(value.Z);
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

    /// <summary>
    /// 客户端重放保护：记住最近 <see cref="Capacity"/> 个 (server_tick_ms, event_id) 对。
    /// <see cref="RemoteSoundOrderer"/> 只能拒绝"当前会话内"更旧 / 重复的 id；重连时 orderer
    /// 会重置（主机的 event id 也从 1 重新开始），旧事件在网络里重放就会再次被接受。
    /// 本保护**不**随重连清空：同一进程里已经播放过的事件永不重播。
    /// </summary>
    public sealed class RemoteSoundReplayGuard
    {
        public const int Capacity = 64;

        private struct Entry
        {
            public long Tick;
            public uint EventId;
            public bool Used;
        }

        private readonly Entry[] m_entries = new Entry[Capacity];
        private int m_next;

        public long Rejected { get; private set; }
        public long Accepted { get; private set; }

        public bool TryAccept(in RemoteSoundEventData data)
        {
            for (int i = 0; i < Capacity; i++)
            {
                if (!m_entries[i].Used) continue;
                if (m_entries[i].EventId != data.EventId) continue;
                if (m_entries[i].Tick != data.ServerTickMs) continue;
                Rejected++;
                return false;
            }
            m_entries[m_next].Used = true;
            m_entries[m_next].Tick = data.ServerTickMs;
            m_entries[m_next].EventId = data.EventId;
            m_next++;
            if (m_next >= Capacity) m_next = 0;
            Accepted++;
            return true;
        }

        /// <summary>新连接只清计数，不清记忆（旧事件仍拒绝）。</summary>
        public void NewConnection() => Accepted = 0;

        public void Reset()
        {
            for (int i = 0; i < Capacity; i++) m_entries[i] = default;
            m_next = 0;
            Accepted = 0;
            Rejected = 0;
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

        /// <summary>当前 1 秒窗口里还能接受多少个"单位"（声音 / 事件）。</summary>
        public int Remaining(long nowMs)
        {
            if (m_windowStartMs == 0 || nowMs - m_windowStartMs >= 1000)
                return m_maxPerSecond;
            int remaining = m_maxPerSecond - m_count;
            return remaining > 0 ? remaining : 0;
        }

        public bool TryAccept(long nowMs) => TryAccept(nowMs, 1);

        /// <summary>按权重接受（全局声音中继按"声音数"计，不是按消息数）。</summary>
        public bool TryAccept(long nowMs, int weight)
        {
            if (weight <= 0) return true;
            if (m_windowStartMs == 0 || nowMs - m_windowStartMs >= 1000)
            {
                m_windowStartMs = nowMs == 0 ? 1 : nowMs;
                m_count = 0;
            }
            if (m_count + weight > m_maxPerSecond)
            {
                Skipped++;
                return false;
            }
            m_count += weight;
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

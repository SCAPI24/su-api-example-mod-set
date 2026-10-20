using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Google.Protobuf;
using PM = SuMultiplayer.Protocol.Melee;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 主机权威近战攻击的**纯逻辑**（编解码 / 距离规则 / 裁决 / 预测状态机 / 结果去重）。
    /// 本文件不引用 Game，因此 <c>P:\tmp\tsverify</c> 直接编译同一份源码做无设备自检；
    /// 引擎动作（读 ComponentMiner / ComponentBody / 播声音粒子）全部在
    /// <c>Engine/MeleeReplicaEngine.cs</c>，模块（协议 / 计数 / 广播）在
    /// <c>Modules/MeleeSyncModule.cs</c>。
    ///
    /// 权威口径（产品要求，不可回退）：
    ///   · 是否命中、伤害、目标剩余生命**只由主机**算一次：主机调引擎
    ///     <c>ComponentMiner.Hit</c>（Source: Survivalcraft/Game/ComponentMiner.cs:293）；
    ///   · 客户端**不 roll 命中**、**不扣血**：本机原生攻击输入在
    ///     <c>ComponentInput.Update</c> 之后被吞掉（见 Engine/MeleeReplicaEngine.cs 的
    ///     IHitInputSink），只保留预测表现；
    ///   · 距离口径与交互 / 挖掘一致：<c>allowed = min(requestReach + 0.5, serverMax, engineReach)</c>，
    ///     两侧调用同一个 <see cref="MeleeReachRule"/>；
    ///   · 拒绝原因必须是稳定原因码（见 <see cref="MeleeReasons"/>），不许静默丢弃。
    /// </summary>
    public static class MeleeLimits
    {
        /// <summary>请求 / 结果的单条载荷上限（字节）；超过整条拒绝。</summary>
        public const int MaxPayloadBytes = 2048;

        /// <summary>一个请求里 <c>ext</c> 必须为空（与其它内置消息一致，第三方扩展走 custom）。</summary>
        public const int MaxExtBytes = 0;

        /// <summary>序列号上限（主机 / 客户端同一口径；超过视为畸形）。</summary>
        public const long MaxSequence = long.MaxValue - 1;

        /// <summary>玩家端传输编号上限（防御畸形 clientId）。</summary>
        public const int MaxClientId = 1_000_000;

        /// <summary>
        /// 战斗随机流 index 上限（防御畸形 / 恶意的超大 index：重同步要按 index 跳状态）。
        /// 正常会话远达不到（0.66 s 冷却 → 每小时约 5454 次）。
        /// </summary>
        public const long MaxAttackIndex = 10_000_000L;

        /// <summary>引擎生命 / 掉落值等标量的合法上限（防御 NaN / 1e30）。</summary>
        public const float MaxScalar = 100000f;

        /// <summary>
        /// 请求 / 结果里 eye / position / 方向坐标的合法上限（米）。
        /// 与实体复制的 <c>EntityReplicationLimits.MaxCoordinate</c> 同源。
        /// </summary>
        public const float MaxCoordinate = EntityReplicationLimits.MaxCoordinate;

        /// <summary>方向向量长度平方的合法下限（再小视为没有方向）。</summary>
        public const float MinDirectionLengthSquared = 1e-6f;

        /// <summary>方向向量长度平方的合法上限。</summary>
        public const float MaxDirectionLengthSquared = 1e12f;
    }

    /// <summary>表现开关位（结果里由主机算好，客户端 / 观测端照做，不自行推断）。</summary>
    public static class MeleeEffectFlags
    {
        /// <summary>播 <c>Audio/Impacts/Body</c>（引擎 AttackBody 的命中音）。</summary>
        public const uint ImpactSound = 1u << 0;
        /// <summary>显示伤害数字（引擎 HitValueParticleSystem；damage &gt; 0 才置位）。</summary>
        public const uint DamagePopup = 1u << 1;
        /// <summary>显示引擎的 "Miss" 标记（原生未命中表现）。</summary>
        public const uint MissMarker = 1u << 2;
        /// <summary>播 <c>Audio/Swoosh</c> 挥击声（引擎 Hit 在攻击者为玩家时总会播）。</summary>
        public const uint Swoosh = 1u << 3;
        /// <summary>目标处于无敌（创造模式玩家 / IsInvulnerable）：命中但不掉血，**明确标注**。</summary>
        public const uint Invulnerable = 1u << 4;
        /// <summary>对空气挥击（没有目标实体）。</summary>
        public const uint AirSwing = 1u << 5;
        /// <summary>目标生命真的下降了（受害端的红屏 / 血条闪烁来自生命体征流）。</summary>
        public const uint HealthChanged = 1u << 6;

        /// <summary>本构建认识的位；未知位按 forward-compatible 忽略（不拒绝，只不表现）。</summary>
        public const uint KnownMask = ImpactSound | DamagePopup | MissMarker | Swoosh |
            Invulnerable | AirSwing | HealthChanged;

        public static string Describe(uint flags)
        {
            if (flags == 0) return "none";
            var builder = new System.Text.StringBuilder();
            Append(builder, flags, ImpactSound, "impact");
            Append(builder, flags, DamagePopup, "popup");
            Append(builder, flags, MissMarker, "miss");
            Append(builder, flags, Swoosh, "swoosh");
            Append(builder, flags, Invulnerable, "invuln");
            Append(builder, flags, AirSwing, "air");
            Append(builder, flags, HealthChanged, "hp-");
            return builder.ToString();
        }

        private static void Append(System.Text.StringBuilder builder, uint flags, uint bit,
            string name)
        {
            if ((flags & bit) == 0) return;
            if (builder.Length > 0) builder.Append('|');
            builder.Append(name);
        }
    }

    /// <summary>
    /// 稳定拒绝 / 结果原因码（限流日志的 key 必须用它：带数字的整句会让每个距离各占一个 key）。
    /// </summary>
    public static class MeleeReasons
    {
        public const string None = "";
        public const string AirSwing = "air-swing";
        public const string AttackerUnknown = "attacker-unknown";
        public const string AttackerDead = "attacker-dead";
        public const string TargetMissing = "target-missing";
        public const string TargetDead = "target-dead";
        public const string TargetSelf = "target-self";
        public const string ToolLevel = "tool-level";
        public const string FriendlyFire = "friendly-fire";
        public const string Cooldown = "cooldown";
        public const string ReachInvalid = "reach-invalid";
        public const string OutOfReach = "out-of-reach";
        public const string Aim = "aim";
        public const string LineOfSight = "line-of-sight";
        public const string TargetMismatch = "target-mismatch";
        public const string HostUnavailable = "host-unavailable";
        /// <summary>已执行但未命中（主机权威未命中，不是拒绝）。</summary>
        public const string Miss = "miss";
        public const string Accepted = "accepted";

        /// <summary>原因码的稳定日志 key（空串 = 正常命中 / 未命中，返回 <c>accepted</c>）。</summary>
        public static string Key(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return Accepted;
            int colon = reason.IndexOf(':');
            string code = (colon > 0 ? reason.Substring(0, colon) : reason).Trim();
            return code.Length == 0 ? Accepted : code;
        }

        /// <summary>
        /// 原因码是不是"攻击被拒绝"（false = 攻击已执行，hit 决定命中 / 未命中；
        /// 空气挥击 air-swing 与主机未命中 miss 都不是拒绝）。
        /// </summary>
        public static bool IsRejection(string reason) =>
            !string.IsNullOrEmpty(reason) && reason != AirSwing && reason != Miss;
    }

    /// <summary>目标引用（纯值）：空气 / 生物网络键 / 玩家键。</summary>
    public readonly struct MeleeTargetRef : IEquatable<MeleeTargetRef>
    {
        public MeleeTargetRef(PM.MeleeTargetKind kind, int id, int clientId, int slot)
        {
            Kind = kind;
            Id = id;
            ClientId = clientId;
            Slot = slot;
        }

        public PM.MeleeTargetKind Kind { get; }

        /// <summary>生物网络键（Kind = CREATURE 时 &gt; 0）。</summary>
        public int Id { get; }

        /// <summary>玩家键的传输编号（Kind = AVATAR；主机 = 0）。</summary>
        public int ClientId { get; }

        /// <summary>玩家键的本机槽位（Kind = AVATAR；0..3）。</summary>
        public int Slot { get; }

        public bool IsAir => Kind == PM.MeleeTargetKind.MeleeTargetNone;
        public bool IsCreature => Kind == PM.MeleeTargetKind.MeleeTargetCreature;
        public bool IsAvatar => Kind == PM.MeleeTargetKind.MeleeTargetAvatar;

        public static MeleeTargetRef Air => new MeleeTargetRef(
            PM.MeleeTargetKind.MeleeTargetNone, 0, 0, 0);

        public static MeleeTargetRef Creature(int key) => new MeleeTargetRef(
            PM.MeleeTargetKind.MeleeTargetCreature, key, 0, 0);

        public static MeleeTargetRef Avatar(int clientId, int slot) => new MeleeTargetRef(
            PM.MeleeTargetKind.MeleeTargetAvatar, 0, clientId, slot);

        /// <summary>玩家键（Kind 必须是 AVATAR；否则返回 false 并写明原因）。</summary>
        public bool TryGetPlayerKey(out Players.PlayerKey key, out string reason)
        {
            key = default;
            reason = null;
            if (!IsAvatar)
            {
                reason = "目标种类 " + Kind + " 不是玩家角色（不是 PlayerKey）";
                return false;
            }
            if (!Players.PlayerKey.TryCreate(ClientId, Slot, out key, out reason)) return false;
            return true;
        }

        public string Describe()
        {
            switch (Kind)
            {
                case PM.MeleeTargetKind.MeleeTargetCreature:
                    return "creature#" + Id;
                case PM.MeleeTargetKind.MeleeTargetAvatar:
                    return "avatar#" + ClientId + ":" + Slot;
                default:
                    return "air";
            }
        }

        public bool Equals(MeleeTargetRef other) => Kind == other.Kind && Id == other.Id &&
            ClientId == other.ClientId && Slot == other.Slot;

        public override bool Equals(object obj) => obj is MeleeTargetRef other && Equals(other);

        public override int GetHashCode() =>
            ((int)Kind * 397 ^ Id) * 397 ^ ClientId * 31 ^ Slot;

        public static bool operator ==(MeleeTargetRef left, MeleeTargetRef right) =>
            left.Equals(right);

        public static bool operator !=(MeleeTargetRef left, MeleeTargetRef right) =>
            !left.Equals(right);
    }

    /// <summary>一次客户端 → 主机的攻击请求（纯值）。</summary>
    public readonly struct MeleeAttackRequestData
    {
        public MeleeAttackRequestData(long sequence, long clientTick, int attackerSlot,
            MeleeTargetRef target, Vector3 eyePosition, Vector3 lookDirection,
            bool hasReach, float reach, long attackIndex = 0L)
        {
            Sequence = sequence;
            ClientTick = clientTick;
            AttackerSlot = attackerSlot;
            Target = target;
            EyePosition = eyePosition;
            LookDirection = lookDirection;
            HasReach = hasReach;
            Reach = reach;
            AttackIndex = attackIndex;
        }

        public long Sequence { get; }
        public long ClientTick { get; }
        public int AttackerSlot { get; }
        public MeleeTargetRef Target { get; }
        /// <summary>客户端自报的眼睛位置（主机只用于诊断；几何以自己的权威身体为准）。</summary>
        public Vector3 EyePosition { get; }
        /// <summary>客户端自报的视线方向（解码时已归一化）。</summary>
        public Vector3 LookDirection { get; }
        public bool HasReach { get; }
        public float Reach { get; }

        /// <summary>
        /// 客户端认为主机将为这次攻击使用的战斗随机 index（主机拒绝时不消耗；
        /// 主机实际用的 index 在结果里回传）。
        /// </summary>
        public long AttackIndex { get; }

        public string Describe() =>
            "seq=" + Sequence + " slot=" + AttackerSlot + " target=" + Target.Describe() +
            " reach=" + (HasReach ? Fmt(Reach) + "m" : "missing") + " rng=" + AttackIndex +
            " tick=" + ClientTick;

        internal static string Fmt(float value) =>
            float.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "NaN";
    }

    /// <summary>主机对一次攻击的唯一裁决（纯值）。</summary>
    public readonly struct MeleeAttackResultData
    {
        public MeleeAttackResultData(long sequence, long serverTick, int attackerClientId,
            int attackerSlot, bool accepted, bool hit, string reason, MeleeTargetRef target,
            float damage, float healthAfter, float healthDrop, Vector3 position,
            Vector3 direction, uint effectFlags, float measuredDistance,
            float allowedDistance, float serverMaxReach, float hostEngineReach,
            long attackIndex = 0L, long streamIndex = -1L, float rollU = 0f, float rollF = 1f,
            float rollA = 0f, float rollReserved = 0f, float hitProbability = 0f,
            bool hasRolls = false)
        {
            Sequence = sequence;
            ServerTick = serverTick;
            AttackerClientId = attackerClientId;
            AttackerSlot = attackerSlot;
            Accepted = accepted;
            Hit = hit;
            Reason = reason ?? string.Empty;
            Target = target;
            Damage = damage;
            HealthAfter = healthAfter;
            HealthDrop = healthDrop;
            Position = position;
            Direction = direction;
            EffectFlags = effectFlags;
            MeasuredDistance = measuredDistance;
            AllowedDistance = allowedDistance;
            ServerMaxReach = serverMaxReach;
            HostEngineReach = hostEngineReach;
            AttackIndex = attackIndex;
            StreamIndex = streamIndex;
            RollU = rollU;
            RollF = rollF;
            RollA = rollA;
            RollReserved = rollReserved;
            HitProbability = hitProbability;
            HasRolls = hasRolls;
        }

        public long Sequence { get; }
        public long ServerTick { get; }
        public int AttackerClientId { get; }
        public int AttackerSlot { get; }
        /// <summary>false = 主机拒绝（<see cref="Reason"/> 必填，<see cref="Hit"/> 必为 false）。</summary>
        public bool Accepted { get; }
        /// <summary>主机 roll 的唯一结果（引擎 ComponentMiner.Hit 判定）。</summary>
        public bool Hit { get; }
        public string Reason { get; }
        public MeleeTargetRef Target { get; }
        /// <summary>引擎口径的显示伤害（生命掉落 × AttackResilience）。</summary>
        public float Damage { get; }
        /// <summary>目标攻击后的生命；-1 = 没有 ComponentHealth。</summary>
        public float HealthAfter { get; }
        /// <summary>生命实际掉落（生命点）。</summary>
        public float HealthDrop { get; }
        public Vector3 Position { get; }
        public Vector3 Direction { get; }
        public uint EffectFlags { get; }
        public float MeasuredDistance { get; }
        public float AllowedDistance { get; }
        public float ServerMaxReach { get; }
        public float HostEngineReach { get; }

        /// <summary>
        /// 主机为这次攻击**实际使用**的战斗随机 index（拒绝 = 请求里的 index，不消耗）。
        /// 客户端据此对账：与自己的预测 index 不同 → 回滚 + 重同步。
        /// </summary>
        public long AttackIndex { get; }
        /// <summary>主机结算后的流位置（下一发攻击的 index）；-1 = 旧主机没有该字段。</summary>
        public long StreamIndex { get; }
        /// <summary>槽 [0] 命中均匀值（0..1）。</summary>
        public float RollU { get; }
        /// <summary>槽 [1] 伤害方差（0.8..1.2）。</summary>
        public float RollF { get; }
        /// <summary>槽 [2] 护甲槽随机值（0..1，当前仅审计）。</summary>
        public float RollA { get; }
        /// <summary>槽 [3] 预留。</summary>
        public float RollReserved { get; }
        /// <summary>主机结算用的命中概率 p（0.66 或 block.GetMeleeHitProbability，×3 慢目标）。</summary>
        public float HitProbability { get; }
        /// <summary>false = 旧主机 / 拒绝回执：rolls 字段无意义。</summary>
        public bool HasRolls { get; }

        public bool IsRejection => !Accepted && MeleeReasons.IsRejection(Reason);

        public string Describe() =>
            "seq=" + Sequence + " attacker=" + AttackerClientId + ":" + AttackerSlot +
            " target=" + Target.Describe() +
            (Accepted
                ? (Hit ? " hit" : " miss") + " dmg=" + Fmt(Damage) + " hp=" + Fmt(HealthAfter)
                : " rejected(" + Reason + ")") +
            " rng=" + AttackIndex + (HasRolls
                ? "(u=" + Fmt(RollU) + " f=" + Fmt(RollF) + " a=" + Fmt(RollA) +
                  " p=" + Fmt(HitProbability) + " next=" + StreamIndex + ")"
                : "(no-rolls next=" + StreamIndex + ")") +
            " dist=" + Fmt(MeasuredDistance) + "/" + Fmt(AllowedDistance) + "m" +
            " flags=" + MeleeEffectFlags.Describe(EffectFlags) + " tick=" + ServerTick;

        private static string Fmt(float value) =>
            float.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "NaN";
    }

    /// <summary>
    /// 线上编解码 + 严格校验：畸形载荷整条拒绝（返回 false + 稳定原因），不猜默认值、不夹紧冒充。
    /// </summary>
    public static class MeleeCodec
    {
        // ---------------------------------------------------------------- 请求

        public static bool TryEncode(in MeleeAttackRequestData data, out PM.MeleeAttackRequest message,
            out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            message = new PM.MeleeAttackRequest
            {
                Sequence = data.Sequence,
                ClientTick = data.ClientTick,
                AttackerSlot = data.AttackerSlot,
                TargetKind = data.Target.Kind,
                TargetId = data.Target.Id,
                TargetClientId = data.Target.ClientId,
                TargetSlot = data.Target.Slot,
                EyePosition = ToVec3(data.EyePosition),
                LookDirection = ToVec3(Vector3.Normalize(data.LookDirection)),
                HasReach = data.HasReach,
                Reach = data.Reach,
                AttackIndex = data.AttackIndex
            };
            return true;
        }

        public static byte[] Encode(in MeleeAttackRequestData data)
        {
            if (!TryEncode(in data, out PM.MeleeAttackRequest message, out string reason))
                throw new InvalidOperationException("近战攻击请求编解码失败：" + reason);
            return message.ToByteArray();
        }

        /// <summary>编码成 proto 消息（<c>IMessageSender.Send</c> 用它按类型取 MessageType）。</summary>
        public static PM.MeleeAttackRequest EncodeRequest(in MeleeAttackRequestData data)
        {
            if (!TryEncode(in data, out PM.MeleeAttackRequest message, out string reason))
                throw new InvalidOperationException("近战攻击请求编解码失败：" + reason);
            return message;
        }

        public static bool TryDecode(PM.MeleeAttackRequest message, out MeleeAttackRequestData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "melee-request-null";
                return false;
            }
            if (message.Ext != null && message.Ext.Length > MeleeLimits.MaxExtBytes)
            {
                reason = "melee-request-ext-length-" + message.Ext.Length;
                return false;
            }
            if (!TryReadTarget(message.TargetKind, message.TargetId, message.TargetClientId,
                    message.TargetSlot, "melee-request", out MeleeTargetRef target, out reason))
            {
                return false;
            }
            if (!TryReadVec3(message.EyePosition, out Vector3 eye))
            {
                reason = "melee-request-eye";
                return false;
            }
            if (!TryReadDirection(message.LookDirection, out Vector3 direction))
            {
                reason = "melee-request-direction";
                return false;
            }
            data = new MeleeAttackRequestData(message.Sequence, message.ClientTick,
                message.AttackerSlot, target, eye, direction, message.HasReach, message.Reach,
                message.AttackIndex);
            if (!TryValidate(in data, out reason))
            {
                data = default;
                return false;
            }
            return true;
        }

        public static bool TryDecode(byte[] payload, out MeleeAttackRequestData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (payload == null || payload.Length == 0)
            {
                reason = "melee-request-empty";
                return false;
            }
            if (payload.Length > MeleeLimits.MaxPayloadBytes)
            {
                reason = "melee-request-too-long-" + payload.Length;
                return false;
            }
            PM.MeleeAttackRequest message;
            try
            {
                message = PM.MeleeAttackRequest.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "melee-request-parse-" + e.GetType().Name;
                return false;
            }
            return TryDecode(message, out data, out reason);
        }

        private static bool TryValidate(in MeleeAttackRequestData data, out string reason)
        {
            reason = null;
            if (data.Sequence <= 0 || data.Sequence > MeleeLimits.MaxSequence)
            {
                reason = "melee-request-sequence-" + data.Sequence;
                return false;
            }
            if (data.ClientTick < 0)
            {
                reason = "melee-request-client-tick-" + data.ClientTick;
                return false;
            }
            if (data.AttackerSlot < 0 || data.AttackerSlot > Players.PlayerKey.MaxSlot)
            {
                reason = "melee-request-attacker-slot-" + data.AttackerSlot;
                return false;
            }
            if (data.AttackIndex < 0L || data.AttackIndex > MeleeLimits.MaxAttackIndex)
            {
                reason = "melee-request-attack-index-" + data.AttackIndex;
                return false;
            }
            if (!TryValidateTarget(data.Target, "melee-request", out reason)) return false;
            if (!IsFinite(data.EyePosition) ||
                Math.Abs(data.EyePosition.X) > MeleeLimits.MaxCoordinate ||
                Math.Abs(data.EyePosition.Y) > MeleeLimits.MaxCoordinate ||
                Math.Abs(data.EyePosition.Z) > MeleeLimits.MaxCoordinate)
            {
                reason = "melee-request-eye";
                return false;
            }
            float directionSquared = data.LookDirection.LengthSquared();
            if (!IsFinite(data.LookDirection) ||
                directionSquared < MeleeLimits.MinDirectionLengthSquared ||
                directionSquared > MeleeLimits.MaxDirectionLengthSquared)
            {
                reason = "melee-request-direction";
                return false;
            }
            if (!float.IsFinite(data.Reach))
            {
                reason = "melee-request-reach-nan";
                return false;
            }
            if (data.HasReach && (data.Reach < InteractionReachGate.MinClaimMeters ||
                data.Reach > InteractionReachGate.MaxClaimMeters))
            {
                reason = "melee-request-reach-" + MeleeAttackRequestData.Fmt(data.Reach);
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 结果

        public static bool TryEncode(in MeleeAttackResultData data, out PM.MeleeAttackResult message,
            out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            Vector3 direction = data.Direction;
            if (direction.LengthSquared() < MeleeLimits.MinDirectionLengthSquared)
                direction = new Vector3(0f, 0f, 1f);
            message = new PM.MeleeAttackResult
            {
                Sequence = data.Sequence,
                ServerTick = data.ServerTick,
                AttackerClientId = data.AttackerClientId,
                AttackerSlot = data.AttackerSlot,
                Accepted = data.Accepted,
                Hit = data.Hit,
                Reason = data.Reason ?? string.Empty,
                TargetKind = data.Target.Kind,
                TargetId = data.Target.Id,
                TargetClientId = data.Target.ClientId,
                TargetSlot = data.Target.Slot,
                Damage = data.Damage,
                HealthAfter = data.HealthAfter,
                HealthDrop = data.HealthDrop,
                Position = ToVec3(data.Position),
                Direction = ToVec3(Vector3.Normalize(direction)),
                EffectFlags = data.EffectFlags,
                MeasuredDistance = data.MeasuredDistance,
                AllowedDistance = data.AllowedDistance,
                ServerMaxReach = data.ServerMaxReach,
                HostEngineReach = data.HostEngineReach,
                AttackIndex = data.AttackIndex,
                StreamIndex = data.StreamIndex,
                RollU = data.RollU,
                RollF = data.RollF,
                RollA = data.RollA,
                RollReserved = data.RollReserved,
                HitProbability = data.HitProbability,
                HasRolls = data.HasRolls
            };
            return true;
        }

        public static byte[] Encode(in MeleeAttackResultData data)
        {
            if (!TryEncode(in data, out PM.MeleeAttackResult message, out string reason))
                throw new InvalidOperationException("近战攻击结果编解码失败：" + reason);
            return message.ToByteArray();
        }

        /// <summary>编码成 proto 消息（<c>IMessageSender.Send</c> 用它按类型取 MessageType）。</summary>
        public static PM.MeleeAttackResult EncodeResult(in MeleeAttackResultData data)
        {
            if (!TryEncode(in data, out PM.MeleeAttackResult message, out string reason))
                throw new InvalidOperationException("近战攻击结果编解码失败：" + reason);
            return message;
        }

        public static bool TryDecode(PM.MeleeAttackResult message, out MeleeAttackResultData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "melee-result-null";
                return false;
            }
            if (message.Ext != null && message.Ext.Length > MeleeLimits.MaxExtBytes)
            {
                reason = "melee-result-ext-length-" + message.Ext.Length;
                return false;
            }
            if (!TryReadTarget(message.TargetKind, message.TargetId, message.TargetClientId,
                    message.TargetSlot, "melee-result", out MeleeTargetRef target, out reason))
            {
                return false;
            }
            if (!TryReadVec3(message.Position, out Vector3 position))
            {
                reason = "melee-result-position";
                return false;
            }
            if (!TryReadDirection(message.Direction, out Vector3 direction))
            {
                reason = "melee-result-direction";
                return false;
            }
            data = new MeleeAttackResultData(message.Sequence, message.ServerTick,
                message.AttackerClientId, message.AttackerSlot, message.Accepted, message.Hit,
                message.Reason, target, message.Damage, message.HealthAfter, message.HealthDrop,
                position, direction, message.EffectFlags, message.MeasuredDistance,
                message.AllowedDistance, message.ServerMaxReach, message.HostEngineReach,
                message.AttackIndex, message.StreamIndex, message.RollU, message.RollF,
                message.RollA, message.RollReserved, message.HitProbability, message.HasRolls);
            if (!TryValidate(in data, out reason))
            {
                data = default;
                return false;
            }
            return true;
        }

        public static bool TryDecode(byte[] payload, out MeleeAttackResultData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (payload == null || payload.Length == 0)
            {
                reason = "melee-result-empty";
                return false;
            }
            if (payload.Length > MeleeLimits.MaxPayloadBytes)
            {
                reason = "melee-result-too-long-" + payload.Length;
                return false;
            }
            PM.MeleeAttackResult message;
            try
            {
                message = PM.MeleeAttackResult.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "melee-result-parse-" + e.GetType().Name;
                return false;
            }
            return TryDecode(message, out data, out reason);
        }

        private static bool TryValidate(in MeleeAttackResultData data, out string reason)
        {
            reason = null;
            if (data.Sequence <= 0 || data.Sequence > MeleeLimits.MaxSequence)
            {
                reason = "melee-result-sequence-" + data.Sequence;
                return false;
            }
            if (data.ServerTick < 0)
            {
                reason = "melee-result-server-tick-" + data.ServerTick;
                return false;
            }
            if (data.AttackerClientId < 0 || data.AttackerClientId > MeleeLimits.MaxClientId)
            {
                reason = "melee-result-attacker-client-" + data.AttackerClientId;
                return false;
            }
            if (data.AttackerSlot < 0 || data.AttackerSlot > Players.PlayerKey.MaxSlot)
            {
                reason = "melee-result-attacker-slot-" + data.AttackerSlot;
                return false;
            }
            if (!TryValidateTarget(data.Target, "melee-result", out reason)) return false;
            if (!data.Accepted)
            {
                if (!MeleeReasons.IsRejection(data.Reason))
                {
                    reason = "melee-result-rejected-without-reason";
                    return false;
                }
                if (data.Hit)
                {
                    reason = "melee-result-rejected-but-hit";
                    return false;
                }
            }
            else if (data.Target.IsAir)
            {
                // 空气挥击：hit 必须 false，原因必须 air-swing（客户端据此按未命中收尾）。
                if (data.Hit)
                {
                    reason = "melee-result-air-swing-hit";
                    return false;
                }
                if (data.Reason != MeleeReasons.AirSwing)
                {
                    reason = "melee-result-air-swing-reason-" + MeleeReasons.Key(data.Reason);
                    return false;
                }
            }
            else if (data.Hit)
            {
                // 已接受的真实目标攻击命中：reason 必须为空（hit 已表达结论）。
                if (!string.IsNullOrEmpty(data.Reason))
                {
                    reason = "melee-result-accepted-reason-" + MeleeReasons.Key(data.Reason);
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(data.Reason) && data.Reason != MeleeReasons.Miss)
            {
                // 已接受的真实目标攻击未命中：reason 只能是 "miss"（或旧主机的空串）。
                reason = "melee-result-accepted-miss-reason-" + MeleeReasons.Key(data.Reason);
                return false;
            }
            if (!float.IsFinite(data.Damage) || data.Damage < 0f || data.Damage > MeleeLimits.MaxScalar)
            {
                reason = "melee-result-damage";
                return false;
            }
            if (!float.IsFinite(data.HealthAfter) || data.HealthAfter < -1f || data.HealthAfter > 1f)
            {
                reason = "melee-result-health-after";
                return false;
            }
            if (!float.IsFinite(data.HealthDrop) || data.HealthDrop < 0f || data.HealthDrop > 1f)
            {
                reason = "melee-result-health-drop";
                return false;
            }
            if (!float.IsFinite(data.MeasuredDistance) || data.MeasuredDistance < 0f ||
                data.MeasuredDistance > MeleeLimits.MaxCoordinate)
            {
                reason = "melee-result-measured";
                return false;
            }
            if (!float.IsFinite(data.AllowedDistance) || data.AllowedDistance < 0f ||
                data.AllowedDistance > InteractionReachGate.MaxClaimMeters)
            {
                reason = "melee-result-allowed";
                return false;
            }
            if (!float.IsFinite(data.ServerMaxReach) ||
                data.ServerMaxReach < InteractionReachGate.MinServerMaxMeters ||
                data.ServerMaxReach > InteractionReachGate.MaxServerMaxMeters)
            {
                reason = "melee-result-server-max";
                return false;
            }
            if (!float.IsFinite(data.HostEngineReach) || data.HostEngineReach < 0f ||
                data.HostEngineReach > InteractionReachGate.MaxClaimMeters)
            {
                reason = "melee-result-host-engine-reach";
                return false;
            }
            if (data.AttackIndex < 0L || data.AttackIndex > MeleeLimits.MaxAttackIndex)
            {
                reason = "melee-result-attack-index-" + data.AttackIndex;
                return false;
            }
            if (data.StreamIndex < -1L || data.StreamIndex > MeleeLimits.MaxAttackIndex)
            {
                reason = "melee-result-stream-index-" + data.StreamIndex;
                return false;
            }
            if (data.HasRolls)
            {
                if (!data.Accepted)
                {
                    reason = "melee-result-rejected-with-rolls";
                    return false;
                }
                var rolls = new CombatSlotRolls(data.AttackIndex, data.RollU, data.RollF,
                    data.RollA, data.RollReserved);
                if (!rolls.IsValid)
                {
                    reason = "melee-result-rolls";
                    return false;
                }
                if (data.StreamIndex != data.AttackIndex + 1L)
                {
                    reason = "melee-result-stream-index-not-advanced-" + data.StreamIndex +
                        "/" + data.AttackIndex;
                    return false;
                }
                if (!float.IsFinite(data.HitProbability) || data.HitProbability < 0f ||
                    data.HitProbability > 10f)
                {
                    reason = "melee-result-hit-probability-" +
                        MeleeAttackRequestData.Fmt(data.HitProbability);
                    return false;
                }
                if (!data.Target.IsAir &&
                    data.Hit != CombatHitProbability.Hit(data.RollU, data.HitProbability))
                {
                    reason = "melee-result-hit-does-not-match-stream";
                    return false;
                }
            }
            if (!float.IsFinite(data.Position.X) || Math.Abs(data.Position.X) > MeleeLimits.MaxCoordinate ||
                Math.Abs(data.Position.Y) > MeleeLimits.MaxCoordinate ||
                Math.Abs(data.Position.Z) > MeleeLimits.MaxCoordinate)
            {
                reason = "melee-result-position";
                return false;
            }
            float directionSquared = data.Direction.LengthSquared();
            if (!IsFinite(data.Direction) ||
                directionSquared < MeleeLimits.MinDirectionLengthSquared ||
                directionSquared > MeleeLimits.MaxDirectionLengthSquared)
            {
                reason = "melee-result-direction";
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 公共校验

        private static bool TryValidateTarget(in MeleeTargetRef target, string prefix,
            out string reason)
        {
            reason = null;
            switch (target.Kind)
            {
                case PM.MeleeTargetKind.MeleeTargetNone:
                    if (target.Id != 0 || target.ClientId != 0 || target.Slot != 0)
                    {
                        reason = prefix + "-air-swing-has-target";
                        return false;
                    }
                    return true;
                case PM.MeleeTargetKind.MeleeTargetCreature:
                    if (target.Id <= 0 || target.Id > EntityReplicationLimits.MaxNetworkId)
                    {
                        reason = prefix + "-creature-id-" + target.Id;
                        return false;
                    }
                    if (target.ClientId != 0 || target.Slot != 0)
                    {
                        reason = prefix + "-creature-has-player";
                        return false;
                    }
                    return true;
                case PM.MeleeTargetKind.MeleeTargetAvatar:
                    if (target.Id != 0)
                    {
                        reason = prefix + "-avatar-has-creature-id-" + target.Id;
                        return false;
                    }
                    if (target.ClientId < 0 || target.ClientId > MeleeLimits.MaxClientId)
                    {
                        reason = prefix + "-avatar-client-" + target.ClientId;
                        return false;
                    }
                    if (target.Slot < 0 || target.Slot > Players.PlayerKey.MaxSlot)
                    {
                        reason = prefix + "-avatar-slot-" + target.Slot;
                        return false;
                    }
                    return true;
                default:
                    reason = prefix + "-target-kind-" + (int)target.Kind;
                    return false;
            }
        }

        private static bool TryReadTarget(PM.MeleeTargetKind kind, int id, int clientId, int slot,
            string prefix, out MeleeTargetRef target, out string reason)
        {
            target = new MeleeTargetRef(kind, id, clientId, slot);
            return TryValidateTarget(in target, prefix, out reason);
        }

        internal static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private static bool TryReadVec3(PP.Vec3 message, out Vector3 value)
        {
            value = Vector3.Zero;
            if (message == null) return false;
            value = new Vector3(message.X, message.Y, message.Z);
            return IsFinite(value);
        }

        private static bool TryReadDirection(PP.Vec3 message, out Vector3 direction)
        {
            direction = Vector3.Zero;
            if (message == null) return false;
            var raw = new Vector3(message.X, message.Y, message.Z);
            if (!IsFinite(raw)) return false;
            float squared = raw.LengthSquared();
            if (squared < MeleeLimits.MinDirectionLengthSquared ||
                squared > MeleeLimits.MaxDirectionLengthSquared)
                return false;
            direction = Vector3.Normalize(raw);
            return true;
        }

        private static PP.Vec3 ToVec3(Vector3 value) =>
            new PP.Vec3 { X = value.X, Y = value.Y, Z = value.Z };
    }

    /// <summary>主机对一次请求的 reach 裁决（纯值）。</summary>
    public readonly struct MeleeReachDecision
    {
        internal MeleeReachDecision(bool valid, bool usedDefault, bool clampedByServerMax,
            bool clampedByEngineReach, float effectiveReachMeters, float allowedDistanceMeters,
            string invalidReason)
        {
            Valid = valid;
            UsedDefault = usedDefault;
            ClampedByServerMax = clampedByServerMax;
            ClampedByEngineReach = clampedByEngineReach;
            EffectiveReachMeters = effectiveReachMeters;
            AllowedDistanceMeters = allowedDistanceMeters;
            InvalidReason = invalidReason;
        }

        public bool Valid { get; }
        public bool UsedDefault { get; }
        public bool ClampedByServerMax { get; }
        public bool ClampedByEngineReach { get; }
        public float EffectiveReachMeters { get; }
        public float AllowedDistanceMeters { get; }
        /// <summary>非空 = <c>reach-invalid: ...</c> / <c>server-reach-invalid: ...</c>。</summary>
        public string InvalidReason { get; }

        public string Describe() => Valid
            ? "allowed=" + MeleeAttackRequestData.Fmt(AllowedDistanceMeters) + "m (reach=" +
              MeleeAttackRequestData.Fmt(EffectiveReachMeters) + "m" +
              (UsedDefault ? ", default" : string.Empty) +
              (ClampedByServerMax ? ", serverMax" : string.Empty) +
              (ClampedByEngineReach ? ", engine" : string.Empty) + ")"
            : InvalidReason;
    }

    /// <summary>
    /// 近战距离的**唯一规则**（客户端预检与主机裁决调用同一个函数）：
    ///
    ///   allowed = min(requesterReach + 0.5 m, serverMax, engineReach)
    ///
    ///   · requesterReach = 请求声明的有效 reach（缺字段 → 世界模式默认：生存 5 / 创造 7.5，
    ///     与 <c>ComponentMiner.Raycast</c> 同源）；
    ///   · 容差 0.5 m = 一个 RTT 的位移 + 眼 / 身体中心差（与 InteractionReachGate 相同）；
    ///   · serverMax = 主机配置 <c>meleereachmax</c>（默认 = 交互上限，默认 16 m）；
    ///   · engineReach = 主机自己的引擎 reach（<c>EngineReach.LocalReach()</c>：创造 =
    ///     SettingsManager.CreativeReach，生存 = 5）。
    ///
    /// 客户端拿不到主机配置时先用自己配置里的同一个值 + 最近一条结果回传的
    /// <c>server_max_reach / host_engine_reach</c>（结果里带），收敛后用同一公式判定。
    /// 客户端预检只是"要不要发带目标的请求"的保守判断；最终裁决永远是主机。
    /// </summary>
    public static class MeleeReachRule
    {
        /// <summary>与 InteractionReachGate 同源的容差（米）。</summary>
        public const float ToleranceMeters = InteractionReachGate.ToleranceMeters;

        public const float MinClaimMeters = InteractionReachGate.MinClaimMeters;
        public const float MaxClaimMeters = InteractionReachGate.MaxClaimMeters;
        public const float MinServerMaxMeters = InteractionReachGate.MinServerMaxMeters;
        public const float MaxServerMaxMeters = InteractionReachGate.MaxServerMaxMeters;
        public const float MinEngineReachMeters = 0.5f;
        public const float MaxEngineReachMeters = InteractionReachGate.MaxClaimMeters;

        public static MeleeReachDecision Evaluate(bool hasReach, float claimedReach,
            bool hostCreative, float serverMaxMeters, float engineReachMeters)
        {
            if (!float.IsFinite(serverMaxMeters) || serverMaxMeters < MinServerMaxMeters ||
                serverMaxMeters > MaxServerMaxMeters)
            {
                return Invalid("server-reach-invalid: " +
                    MeleeAttackRequestData.Fmt(serverMaxMeters) + " m（合法 " +
                    MeleeAttackRequestData.Fmt(MinServerMaxMeters) + ".." +
                    MeleeAttackRequestData.Fmt(MaxServerMaxMeters) + " m）");
            }
            if (!float.IsFinite(engineReachMeters) || engineReachMeters < MinEngineReachMeters ||
                engineReachMeters > MaxEngineReachMeters)
            {
                return Invalid("engine-reach-invalid: " +
                    MeleeAttackRequestData.Fmt(engineReachMeters) + " m（合法 " +
                    MeleeAttackRequestData.Fmt(MinEngineReachMeters) + ".." +
                    MeleeAttackRequestData.Fmt(MaxEngineReachMeters) + " m）");
            }
            float effective;
            bool usedDefault = false;
            if (!hasReach)
            {
                effective = InteractionReachGate.DefaultReach(hostCreative);
                usedDefault = true;
            }
            else
            {
                if (!float.IsFinite(claimedReach) || claimedReach < MinClaimMeters ||
                    claimedReach > MaxClaimMeters)
                {
                    return Invalid("reach-invalid: " + MeleeAttackRequestData.Fmt(claimedReach) +
                        " m（合法 " + MeleeAttackRequestData.Fmt(MinClaimMeters) + ".." +
                        MeleeAttackRequestData.Fmt(MaxClaimMeters) + " m）");
                }
                effective = claimedReach;
            }
            float withTolerance = effective + ToleranceMeters;
            float boundedByServer = Math.Min(withTolerance, serverMaxMeters);
            float allowed = Math.Min(boundedByServer, engineReachMeters);
            if (!float.IsFinite(allowed) || allowed <= 0f)
            {
                return Invalid("reach-invalid: allowed=" + MeleeAttackRequestData.Fmt(allowed) + " m");
            }
            return new MeleeReachDecision(true, usedDefault,
                clampedByServerMax: withTolerance > serverMaxMeters,
                clampedByEngineReach: boundedByServer > engineReachMeters,
                effectiveReachMeters: effective, allowedDistanceMeters: allowed,
                invalidReason: null);
        }

        public static MeleeReachDecision Invalid(string reason) =>
            new MeleeReachDecision(false, false, false, false, 0f, 0f, reason);
    }

    /// <summary>几何工具（纯函数）：主机与客户端用**同一个**距离 / 锥角定义。</summary>
    public static class MeleeGeometry
    {
        /// <summary>
        /// 引擎身体筛选的锥角阈值：<c>ComponentMiner.Raycast</c> 只接受
        /// <c>dot(normalize(bodyCenter - start), direction) &gt; 0.7</c> 的身体。
        /// Source: Survivalcraft/Game/ComponentMiner.cs:384
        /// </summary>
        public const float MinConeDot = 0.7f;

        public static Vector3 BoxCenter(Vector3 min, Vector3 max) => 0.5f * (min + max);

        /// <summary>
        /// 距离口径（主客两端一致）：攻击者眼睛 → 目标身体包围盒**中心**。
        ///
        /// 为什么用中心而不是命中点：引擎的锥角筛选用的就是包围盒中心
        /// （ComponentMiner.cs:384），命中点会随客户端 / 主机射线与目标位移而不同；
        /// 中心是两端都能从权威 / 复制体包围盒直接算出的同一个量。代价是大型目标在
        /// 边缘会略早判"够不到"（中心比入射点远），这是有意的防作弊取严。
        /// </summary>
        public static float EyeToTargetCenter(Vector3 eye, Vector3 boxMin, Vector3 boxMax) =>
            Vector3.Distance(eye, BoxCenter(boxMin, boxMax));

        /// <summary>引擎同款锥角判定（目标中心方向与视线方向的点积 &gt;= 0.7）。</summary>
        public static bool InCone(Vector3 eye, Vector3 direction, Vector3 targetCenter)
        {
            Vector3 toTarget = targetCenter - eye;
            float toTargetSquared = toTarget.LengthSquared();
            if (toTargetSquared < 1e-8f) return true;
            if (direction.LengthSquared() < MeleeLimits.MinDirectionLengthSquared) return false;
            Vector3 normalized = Vector3.Normalize(toTarget);
            return Vector3.Dot(Vector3.Normalize(direction), normalized) >= MinConeDot;
        }
    }

    /// <summary>
    /// 主机裁决需要的**全部事实**（由引擎适配层在权威实体上采集；纯逻辑只做判定）。
    /// </summary>
    public readonly struct MeleeAdjudicationInputs
    {
        public MeleeAdjudicationInputs(bool attackerKnown, bool attackerAlive,
            bool targetExists, bool targetAlive, bool targetIsSelf, bool targetInvulnerable,
            bool cooldownReady, bool toolLevelSufficient, bool friendlyFireAllowed,
            bool aimInCone, bool aimRayHitsTarget, bool aimRayHitsOtherBody,
            float measuredMeters, float serverMaxMeters, float engineReachMeters,
            bool hostCreative)
        {
            AttackerKnown = attackerKnown;
            AttackerAlive = attackerAlive;
            TargetExists = targetExists;
            TargetAlive = targetAlive;
            TargetIsSelf = targetIsSelf;
            TargetInvulnerable = targetInvulnerable;
            CooldownReady = cooldownReady;
            ToolLevelSufficient = toolLevelSufficient;
            FriendlyFireAllowed = friendlyFireAllowed;
            AimInCone = aimInCone;
            AimRayHitsTarget = aimRayHitsTarget;
            AimRayHitsOtherBody = aimRayHitsOtherBody;
            MeasuredMeters = measuredMeters;
            ServerMaxMeters = serverMaxMeters;
            EngineReachMeters = engineReachMeters;
            HostCreative = hostCreative;
        }

        public bool AttackerKnown { get; }
        public bool AttackerAlive { get; }
        public bool TargetExists { get; }
        public bool TargetAlive { get; }
        public bool TargetIsSelf { get; }
        public bool TargetInvulnerable { get; }
        public bool CooldownReady { get; }
        public bool ToolLevelSufficient { get; }
        public bool FriendlyFireAllowed { get; }
        public bool AimInCone { get; }
        public bool AimRayHitsTarget { get; }
        public bool AimRayHitsOtherBody { get; }
        public float MeasuredMeters { get; }
        public float ServerMaxMeters { get; }
        public float EngineReachMeters { get; }
        /// <summary>主机世界是不是创造模式（只影响"缺 reach 字段"时的默认值 5 / 7.5）。</summary>
        public bool HostCreative { get; }
    }

    /// <summary>裁决结论（纯值）：accepted=false 时 reason 必填。</summary>
    public readonly struct MeleeVerdict
    {
        internal MeleeVerdict(bool accepted, string reason, in MeleeReachDecision reach,
            float measuredMeters)
        {
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            Reach = reach;
            MeasuredMeters = measuredMeters;
        }

        public bool Accepted { get; }
        /// <summary>拒绝原因码；已接受时 = "" 或 <c>air-swing</c>。</summary>
        public string Reason { get; }
        public MeleeReachDecision Reach { get; }
        public float MeasuredMeters { get; }
    }

    /// <summary>
    /// 主机裁决的**唯一顺序**（两侧文档 / 自检都按这个顺序断言）：
    ///
    ///   1 attacker-unknown → 2 attacker-dead
    ///   3（有目标时）target-missing → 4 target-dead → 5 target-self
    ///   6 tool-level → 7 friendly-fire → 8 cooldown
    ///   9 reach-invalid → 10 out-of-reach
    ///   11 aim（锥角）→ 12 target-mismatch（射线先打到别的身体）/ line-of-sight（被地形挡住）
    ///
    /// 空气挥击（target = none）只过 1/2/8：主机接受，hit=false、reason=air-swing、不伤害。
    /// 走到这里的请求**还没有 roll 命中**：roll 只在主机执行引擎 <c>Hit</c> 时发生一次。
    /// </summary>
    public static class MeleeAdjudicator
    {
        public static MeleeVerdict Evaluate(in MeleeAttackRequestData request,
            in MeleeAdjudicationInputs inputs)
        {
            if (!inputs.AttackerKnown)
                return Reject(MeleeReasons.AttackerUnknown, in inputs);
            if (!inputs.AttackerAlive)
                return Reject(MeleeReasons.AttackerDead, in inputs);

            if (request.Target.IsAir)
            {
                if (!inputs.CooldownReady) return Reject(MeleeReasons.Cooldown, in inputs);
                return new MeleeVerdict(true, MeleeReasons.AirSwing,
                    MeleeReachRule.Evaluate(request.HasReach, request.Reach, inputs.HostCreative,
                        inputs.ServerMaxMeters, inputs.EngineReachMeters), 0f);
            }

            if (!inputs.TargetExists) return Reject(MeleeReasons.TargetMissing, in inputs);
            if (!inputs.TargetAlive) return Reject(MeleeReasons.TargetDead, in inputs);
            if (inputs.TargetIsSelf) return Reject(MeleeReasons.TargetSelf, in inputs);
            if (!inputs.ToolLevelSufficient) return Reject(MeleeReasons.ToolLevel, in inputs);
            if (!inputs.FriendlyFireAllowed) return Reject(MeleeReasons.FriendlyFire, in inputs);
            if (!inputs.CooldownReady) return Reject(MeleeReasons.Cooldown, in inputs);

            MeleeReachDecision reach = MeleeReachRule.Evaluate(request.HasReach, request.Reach,
                inputs.HostCreative, inputs.ServerMaxMeters, inputs.EngineReachMeters);
            if (!reach.Valid)
                return new MeleeVerdict(false, MeleeReasons.ReachInvalid, in reach,
                    inputs.MeasuredMeters);
            if (!(inputs.MeasuredMeters <= reach.AllowedDistanceMeters))
                return new MeleeVerdict(false, MeleeReasons.OutOfReach, in reach,
                    inputs.MeasuredMeters);
            if (!inputs.AimInCone)
                return new MeleeVerdict(false, MeleeReasons.Aim, in reach, inputs.MeasuredMeters);
            if (!inputs.AimRayHitsTarget)
            {
                return new MeleeVerdict(false, inputs.AimRayHitsOtherBody
                    ? MeleeReasons.TargetMismatch
                    : MeleeReasons.LineOfSight, in reach, inputs.MeasuredMeters);
            }
            return new MeleeVerdict(true, MeleeReasons.None, in reach, inputs.MeasuredMeters);
        }

        private static MeleeVerdict Reject(string reason, in MeleeAdjudicationInputs inputs) =>
            new MeleeVerdict(false, reason,
                MeleeReachRule.Evaluate(false, 0f, inputs.HostCreative, inputs.ServerMaxMeters,
                    inputs.EngineReachMeters), inputs.MeasuredMeters);
    }

    /// <summary>结果构造器（主机侧；纯逻辑，测试可直接断言字段）。</summary>
    public static class MeleeResults
    {
        /// <summary>拒绝结果：明确原因 + 可观测距离 / 上限，hit 必须 false、伤害 0。</summary>
        public static MeleeAttackResultData Rejected(in MeleeAttackRequestData request,
            int attackerClientId, int attackerSlot, string reason, long serverTick,
            float measuredMeters, float allowedMeters, float serverMaxMeters,
            float engineReachMeters, Vector3 position, Vector3 direction, float healthAfter = -1f,
            long attackIndex = 0L, long streamIndex = -1L)
        {
            if (!MeleeReasons.IsRejection(reason))
                reason = MeleeReasons.HostUnavailable;
            // 拒绝 = 没有执行攻击：效果只有"挥了一下但没打中"（挥击声 + Miss 标记），
            // 客户端据此回滚预测并按未命中展示；目标生命不受影响（health_after 是读到的值）。
            uint flags = MeleeEffectFlags.Swoosh | MeleeEffectFlags.MissMarker;
            if (request.Target.IsAir) flags |= MeleeEffectFlags.AirSwing;
            return new MeleeAttackResultData(request.Sequence, serverTick, attackerClientId,
                attackerSlot, false, false, reason, request.Target, 0f, healthAfter, 0f, position,
                direction, flags, measuredMeters, allowedMeters, serverMaxMeters,
                engineReachMeters, attackIndex, streamIndex);
        }

        /// <summary>
        /// 执行结果：主机已经调过引擎 <c>Hit</c> 一次。
        ///   · hit = 引擎的命中判定（由玩家统计 MeleeHits 增量得到，不重算）；
        ///   · damage = 引擎显示口径（生命掉落 × AttackResilience）；
        ///   · effect flags 由这里统一算（客户端 / 观测端照做）。
        /// </summary>
        public static MeleeAttackResultData Executed(in MeleeAttackRequestData request,
            int attackerClientId, int attackerSlot, long serverTick, bool hit,
            float damage, float healthAfter, float healthDrop, bool targetInvulnerable,
            Vector3 position, Vector3 direction, float measuredMeters, float allowedMeters,
            float serverMaxMeters, float engineReachMeters, long attackIndex = 0L,
            long streamIndex = -1L, float rollU = 0f, float rollF = 1f, float rollA = 0f,
            float rollReserved = 0f, float hitProbability = 0f, bool hasRolls = false)
        {
            uint flags = MeleeEffectFlags.Swoosh;
            if (request.Target.IsAir)
            {
                flags |= MeleeEffectFlags.AirSwing;
            }
            else if (hit)
            {
                // 引擎 AttackBody：命中时只要攻击力 > 0 就播 Audio/Impacts/Body；
                // 伤害数字只在生命真的掉了（num2 > 0）时出现。
                flags |= MeleeEffectFlags.ImpactSound;
                if (damage > 0f) flags |= MeleeEffectFlags.DamagePopup;
                if (healthDrop > 0f) flags |= MeleeEffectFlags.HealthChanged;
                if (targetInvulnerable) flags |= MeleeEffectFlags.Invulnerable;
            }
            else
            {
                flags |= MeleeEffectFlags.MissMarker;
            }
            return new MeleeAttackResultData(request.Sequence, serverTick, attackerClientId,
                attackerSlot, true, hit, request.Target.IsAir
                    ? MeleeReasons.AirSwing
                    : (hit ? MeleeReasons.None : MeleeReasons.Miss),
                request.Target, damage, healthAfter, healthDrop,
                position, direction, flags, measuredMeters, allowedMeters, serverMaxMeters,
                engineReachMeters, attackIndex, streamIndex, rollU, rollF, rollA, rollReserved,
                hitProbability, hasRolls);
        }
    }

    /// <summary>预测对账结果。</summary>
    public enum MeleePredictionOutcome
    {
        /// <summary>预测存在且结果 = 命中（保留预测表现）。</summary>
        Confirmed = 0,
        /// <summary>预测存在且结果 = 未命中 / 拒绝（必须回滚预测表现）。</summary>
        RolledBack = 1,
        /// <summary>没有这条预测（超时 / 重复 / 结果先于预测到达）—— 只做观测端表现。</summary>
        Unknown = 2,
        /// <summary>同一个 sequence 的结果重复到达（忽略，不重放）。</summary>
        Duplicate = 3
    }

    /// <summary>一条待对账预测的完整记录（客户端预测命中 + 战斗随机 index）。</summary>
    public readonly struct MeleePredictionRecord
    {
        public MeleePredictionRecord(long sequence, MeleeTargetRef target, bool predictedHit,
            long attackIndex, float predictedU, float predictedProbability, long begunMs)
        {
            Sequence = sequence;
            Target = target;
            PredictedHit = predictedHit;
            AttackIndex = attackIndex;
            PredictedU = predictedU;
            PredictedProbability = predictedProbability;
            BegunMs = begunMs;
        }

        public long Sequence { get; }
        public MeleeTargetRef Target { get; }
        /// <summary>客户端用主机同一条流算出的预测结果（命中 = true / Miss = false）。</summary>
        public bool PredictedHit { get; }
        public long AttackIndex { get; }
        public float PredictedU { get; }
        public float PredictedProbability { get; }
        public long BegunMs { get; }
    }

    /// <summary>
    /// 客户端预测状态机（纯逻辑）：每次挥击开始登记一条预测，收到主机结果时确认 / 回滚。
    /// 引擎表现句柄（粒子 / 声音）由适配层按 sequence 自己保存；这里只维护状态与计数。
    ///
    /// 计数（状态行可审计）：Predicted / Confirmed / RolledBack / TimedOut / Unknown /
    /// Duplicate / Overflow。上限 <see cref="MaxPending"/> 条、<see cref="TimeoutMs"/> 超时。
    /// </summary>
    public sealed class MeleePredictionTracker
    {
        public const int MaxPending = 16;
        public const long TimeoutMs = 3000;

        private readonly Dictionary<long, Entry> m_pending = new Dictionary<long, Entry>();
        private readonly Queue<long> m_order = new Queue<long>();
        private readonly HashSet<long> m_resolved = new HashSet<long>();
        private readonly Queue<long> m_resolvedOrder = new Queue<long>();

        private struct Entry
        {
            public long BegunMs;
            public MeleeTargetRef Target;
            public bool PredictedHit;
            public long AttackIndex;
            public float PredictedU;
            public float PredictedProbability;
        }

        public long Predicted { get; private set; }
        public long Confirmed { get; private set; }
        public long RolledBack { get; private set; }
        public long TimedOut { get; private set; }
        public long Unknown { get; private set; }
        public long Duplicate { get; private set; }
        public long Overflow { get; private set; }

        public int PendingCount => m_pending.Count;

        public bool TryGetTarget(long sequence, out MeleeTargetRef target)
        {
            if (m_pending.TryGetValue(sequence, out Entry entry))
            {
                target = entry.Target;
                return true;
            }
            target = default;
            return false;
        }

        /// <summary>登记一条预测；返回 false = 超出上限（调用方必须明说并丢弃该条表现）。</summary>
        public bool Begin(long sequence, in MeleeTargetRef target, long nowMs,
            bool predictedHit = false, long attackIndex = -1L, float predictedU = 0f,
            float predictedProbability = 0f)
        {
            if (sequence <= 0) return false;
            if (m_pending.Count >= MaxPending)
            {
                Overflow++;
                return false;
            }
            if (m_pending.ContainsKey(sequence)) return false;
            m_pending[sequence] = new Entry
            {
                BegunMs = nowMs,
                Target = target,
                PredictedHit = predictedHit,
                AttackIndex = attackIndex,
                PredictedU = predictedU,
                PredictedProbability = predictedProbability
            };
            m_order.Enqueue(sequence);
            Predicted++;
            return true;
        }

        /// <summary>取一条待对账预测的完整记录（客户端据此比较 index / 预测命中）。</summary>
        public bool TryGet(long sequence, out MeleePredictionRecord record)
        {
            if (m_pending.TryGetValue(sequence, out Entry entry))
            {
                record = new MeleePredictionRecord(sequence, entry.Target, entry.PredictedHit,
                    entry.AttackIndex, entry.PredictedU, entry.PredictedProbability, entry.BegunMs);
                return true;
            }
            record = default;
            return false;
        }

        /// <summary>
        /// 用主机结果对账：
        ///   · 命中 → <see cref="MeleePredictionOutcome.Confirmed"/>（保留预测表现）；
        ///   · 未命中 / 拒绝 → <see cref="MeleePredictionOutcome.RolledBack"/>（调用方删除预测粒子）；
        ///   · 没有登记 / 已超时 → <see cref="MeleePredictionOutcome.Unknown"/>；
        ///   · 同一 sequence 第二次结果 → <see cref="MeleePredictionOutcome.Duplicate"/>。
        /// </summary>
        public MeleePredictionOutcome Resolve(long sequence, bool accepted, bool hit)
        {
            if (m_resolved.Contains(sequence))
            {
                Duplicate++;
                return MeleePredictionOutcome.Duplicate;
            }
            RememberResolved(sequence);
            if (!m_pending.TryGetValue(sequence, out Entry entry))
            {
                Unknown++;
                return MeleePredictionOutcome.Unknown;
            }
            m_pending.Remove(sequence);
            if (!accepted || !hit)
            {
                RolledBack++;
                return MeleePredictionOutcome.RolledBack;
            }
            Confirmed++;
            return MeleePredictionOutcome.Confirmed;
        }

        /// <summary>把超时的预测移出并返回 sequence 列表（调用方删除对应表现并计数）。</summary>
        public int CollectExpired(long nowMs, List<long> target)
        {
            if (target == null) return 0;
            target.Clear();
            while (m_order.Count > 0)
            {
                long sequence = m_order.Peek();
                if (m_pending.TryGetValue(sequence, out Entry entry))
                {
                    if (nowMs - entry.BegunMs < TimeoutMs) break;
                    m_pending.Remove(sequence);
                    m_order.Dequeue();
                    target.Add(sequence);
                    TimedOut++;
                    continue;
                }
                m_order.Dequeue();
            }
            return target.Count;
        }

        /// <summary>世界 / 会话切换时清空（不改变累计计数）。</summary>
        public void Reset()
        {
            m_pending.Clear();
            m_order.Clear();
            m_resolved.Clear();
            m_resolvedOrder.Clear();
        }

        private void RememberResolved(long sequence)
        {
            // 只记住最近 128 条，避免无限增长（结果按 TCP 顺序到达，重复极少）。
            const int capacity = 128;
            if (m_resolved.Add(sequence)) m_resolvedOrder.Enqueue(sequence);
            while (m_resolvedOrder.Count > capacity)
                m_resolved.Remove(m_resolvedOrder.Dequeue());
        }

        public string Describe() => "meleePredicted=" + Predicted + " meleePredictionConfirmed=" +
            Confirmed + " meleeRollback=" + RolledBack + " meleePredictionTimeout=" + TimedOut +
            " meleePredictionUnknown=" + Unknown + " meleePredictionDuplicate=" + Duplicate +
            " meleePredictionOverflow=" + Overflow + " meleePredictionPending=" + m_pending.Count;
    }

    /// <summary>
    /// 结果去重（纯逻辑）：同一 (attackerClientId, sequence) 只接受一次；
    /// 结果按 TCP 顺序到达，重复 / 陈旧是链路层异常，计数并拒绝，不重放表现。
    /// </summary>
    public sealed class MeleeResultDedupe
    {
        private const int Capacity = 256;
        private readonly HashSet<long> m_seen = new HashSet<long>();
        private readonly Queue<long> m_order = new Queue<long>();

        public long Accepted { get; private set; }
        public long Duplicates { get; private set; }

        public bool TryAccept(int attackerClientId, long sequence)
        {
            if (sequence <= 0 || attackerClientId < 0) return false;
            long key = ((long)attackerClientId << 40) ^ (sequence & 0xFFFFFFFFFFL);
            if (!m_seen.Add(key))
            {
                Duplicates++;
                return false;
            }
            m_order.Enqueue(key);
            while (m_order.Count > Capacity) m_seen.Remove(m_order.Dequeue());
            Accepted++;
            return true;
        }

        public void Reset()
        {
            m_seen.Clear();
            m_order.Clear();
        }

        public string Describe() =>
            "meleeResultAccepted=" + Accepted + " meleeResultDuplicate=" + Duplicates;
    }

    /// <summary>客户端的本地攻击意图（适配层采集 → 模块发送 / 主机本地执行）。</summary>
    public readonly struct MeleeLocalAttack
    {
        public MeleeLocalAttack(in MeleeAttackRequestData request, bool predicted,
            bool outOfRangeFallback)
        {
            Request = request;
            Predicted = predicted;
            OutOfRangeFallback = outOfRangeFallback;
        }

        public MeleeAttackRequestData Request { get; }
        /// <summary>是否已经展示了预测表现（挥手 / 挥击声 / 红色命中标记）。</summary>
        public bool Predicted { get; }
        /// <summary>
        /// 客户端预检认为够不到：发出去的是**空气挥击**（target = none），本地直接按未命中展示。
        /// </summary>
        public bool OutOfRangeFallback { get; }
    }
}

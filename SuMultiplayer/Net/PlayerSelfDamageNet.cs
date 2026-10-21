using System;
using Google.Protobuf;
using SuMultiplayer.Players;
using SD = SuMultiplayer.Protocol.SelfDamage;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 客户端请求主机裁决的**自我伤害**（角色信息界面的骷髅按钮）的线路上限与默认值。
    ///
    /// 引擎事实（这不是战斗伤害，是玩家自己的界面动作）：
    /// <c>Survivalcraft/Game/VitalStatsWidget.cs:176-179</c>
    /// <code>
    /// if (m_chokeButton.IsClicked)
    ///     m_componentPlayer.ComponentHealth.Injure(0.1f, null,
    ///         ignoreInvulnerability: true, "Choked");
    /// </code>
    /// 它跑在**持有控件的那一端**：单机立刻掉 10%，联机客户端只是**预测**，下一拍主机
    /// 权威体征上报（<c>PLAYER_VITALS_REPORT</c> = 95）会把 <c>Health</c> 写回 → 用户看到
    /// 的就是"点了骷髅按钮不掉血"。所以客户端把这次点击改成请求（106 / 107）。
    /// </summary>
    public static class PlayerSelfDamageLimits
    {
        /// <summary>
        /// 引擎骷髅按钮的伤害量（<c>VitalStatsWidget.cs:178</c> 的 <c>0.1f</c>，**逐字一致**；
        /// 客户端不能自己另定一个数）。
        /// </summary>
        public const float ChokedAmount = 0.1f;

        /// <summary>
        /// 单次自我伤害的上限：<c>ComponentHealth.Health</c> 的值域是 0..1
        /// （<c>Injure</c> 用 <c>MathUtils.Saturate(Health - amount)</c> 写回），超过 1 没有意义。
        /// </summary>
        public const float MaxAmount = 1f;

        /// <summary><c>cause</c> 的稳定小写码长度上限（短码，不是句子）。</summary>
        public const int MaxCauseLength = 32;

        /// <summary>一条请求的字节上限。</summary>
        public const int MaxRequestBytes = 512;

        /// <summary>一条结果的字节上限（带原因码 + 回显 cause，比请求略宽）。</summary>
        public const int MaxResultBytes = 1024;

        /// <summary>稳定原因码长度上限。</summary>
        public const int MaxReasonLength = 64;

        /// <summary>请求序号上限（防御性；一局不可能到达）。</summary>
        public const long MaxSequence = long.MaxValue - 1;

        /// <summary><c>ext</c> 必须为空（内置消息；第三方扩展走 <c>custom</c>）。</summary>
        public const int MaxExtBytes = 0;

        /// <summary>
        /// 主机对同一个角色的两次自我伤害之间的最小间隔（防连发 / 防重复投递）。
        /// 与唤醒 / 复活请求的 250 ms 同一量级：客户端本来每次按下也只发一条。
        /// </summary>
        public const int HostRequestGapMs = 250;
    }

    /// <summary>
    /// <c>cause</c> 的白名单。主机**只**接受这里列出的码；白名单以外的值整条拒绝
    /// （绝不"忽略未知 cause 照着扣血"，那等于让客户端随便命名一个死因写进
    /// <c>ComponentHealth.CauseOfDeath</c>）。
    /// </summary>
    public static class PlayerSelfDamageCauses
    {
        /// <summary>线路上的稳定小写码：骷髅按钮（引擎那段 Injure 的唯一调用点）。</summary>
        public const string Choked = "choked";

        /// <summary>
        /// 传给引擎 <c>ComponentHealth.Injure(..., cause)</c> 的死因字符串。**逐字等于单机**
        /// （<c>VitalStatsWidget.cs:178</c> 的 <c>"Choked"</c>）：死亡记录 / 死亡屏上显示的死因
        /// 必须与单机一致，所以这里不做任何大小写转换。
        /// </summary>
        public const string ChokedEngineDeathCause = "Choked";

        /// <summary>白名单判定（区分大小写：线路码只有全小写这一种写法）。</summary>
        public static bool IsKnown(string cause)
        {
            if (string.IsNullOrEmpty(cause)) return false;
            if (cause.Length > PlayerSelfDamageLimits.MaxCauseLength) return false;
            switch (cause)
            {
                case Choked:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 归一化：合法返回 true + 原码；非法返回 false + 可直接写日志的原因（不夹紧、不猜）。
        /// </summary>
        public static bool TryNormalize(string cause, out string normalized, out string reason)
        {
            normalized = null;
            reason = null;
            if (string.IsNullOrEmpty(cause))
            {
                reason = "cause 为空（白名单：" + Choked + "）";
                return false;
            }
            if (cause.Length > PlayerSelfDamageLimits.MaxCauseLength)
            {
                reason = "cause 长度 " + cause.Length + " 超过 " +
                    PlayerSelfDamageLimits.MaxCauseLength + "（" + cause + "）";
                return false;
            }
            if (!IsKnown(cause))
            {
                reason = "cause=\"" + cause + "\" 不在白名单（只接受 " + Choked + "）";
                return false;
            }
            normalized = cause;
            return true;
        }

        /// <summary>线路码 → 引擎死因字符串；未知码返回 null（调用方必须先过白名单）。</summary>
        public static string EngineDeathCause(string cause)
        {
            switch (cause)
            {
                case Choked:
                    return ChokedEngineDeathCause;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// 主机拒绝自我伤害请求的**稳定原因码**（线路 / 状态行 / 日志同一个口径）。
    /// 产品要求的五个原因：<c>wrong-owner / unknown-player / not-alive / invalid /
    /// cooldown</c>；<c>apply-failed</c> 只表示"结构与身份都合法，但主机引擎没把血量改掉"，
    /// <c>malformed</c> 表示载荷本身不合法。
    /// </summary>
    public static class PlayerSelfDamageReasons
    {
        public const string Accepted = "accepted";
        public const string WrongOwner = "wrong-owner";
        public const string UnknownPlayer = "unknown-player";
        public const string NotAlive = "not-alive";
        public const string Invalid = "invalid";
        public const string Cooldown = "cooldown";
        public const string ApplyFailed = "apply-failed";
        public const string Malformed = "malformed";

        /// <summary>状态行计数的稳定键：第一个 ':' 之前的部分（空 = accepted）。</summary>
        public static string Key(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return Accepted;
            int colon = reason.IndexOf(':');
            string code = (colon > 0 ? reason.Substring(0, colon) : reason).Trim();
            return code.Length == 0 ? Accepted : code;
        }

        public static bool IsKnownRejection(string reason)
        {
            switch (Key(reason))
            {
                case WrongOwner:
                case UnknownPlayer:
                case NotAlive:
                case Invalid:
                case Cooldown:
                case ApplyFailed:
                case Malformed:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 一条已验证的 <c>PLAYER_SELF_DAMAGE_REQUEST</c>（client -> host）：客户端在自己的
    /// 角色信息界面按了自我伤害动作，请主机在**主机侧**那个角色上执行引擎 Injure。
    /// </summary>
    public readonly struct PlayerSelfDamageRequestData
    {
        public PlayerSelfDamageRequestData(int clientId, int slot, long sequence, string cause,
            float amount)
        {
            ClientId = clientId;
            Slot = slot;
            Sequence = sequence;
            Cause = cause;
            Amount = amount;
        }

        public int ClientId { get; }
        public int Slot { get; }
        public long Sequence { get; }

        /// <summary>白名单码（目前只有 <see cref="PlayerSelfDamageCauses.Choked"/>）。</summary>
        public string Cause { get; }

        /// <summary>伤害量（0 之后的有限数，上限 1）。</summary>
        public float Amount { get; }

        public PlayerKey Key => new PlayerKey(ClientId, Slot);

        public string Describe() => "self-damage-request{key=" + ClientId + ":" + Slot +
            " seq=" + Sequence + " cause=" + (Cause ?? string.Empty) + " amount=" +
            Amount.ToString("0.####") + "}";
    }

    /// <summary>
    /// 一条已验证的 <c>PLAYER_SELF_DAMAGE_RESULT</c>（host -> 请求方）：主机对一次自我伤害
    /// 请求的裁决。接受**也**能从权威体征上报看到（那份上报才是客户端血量的唯一来源）；
    /// 这条结果存在的意义是"拒绝必须可解释"，否则就是"点了没反应"。
    /// </summary>
    public readonly struct PlayerSelfDamageResultData
    {
        public PlayerSelfDamageResultData(int clientId, int slot, long sequence, bool accepted,
            string reason, string cause, float amount, float healthAfter)
        {
            ClientId = clientId;
            Slot = slot;
            Sequence = sequence;
            Accepted = accepted;
            Reason = reason;
            Cause = cause;
            Amount = amount;
            HealthAfter = healthAfter;
        }

        public int ClientId { get; }
        public int Slot { get; }
        public long Sequence { get; }
        public bool Accepted { get; }

        /// <summary>稳定原因码（<see cref="PlayerSelfDamageReasons"/>；接受恒为 accepted）。</summary>
        public string Reason { get; }

        /// <summary>回显的 cause（白名单码）。</summary>
        public string Cause { get; }

        /// <summary>实际施加的量（拒绝时必须是 0）。</summary>
        public float Amount { get; }

        /// <summary>主机侧施加之后的权威生命（拒绝时必须是 0，客户端不得据此写血量）。</summary>
        public float HealthAfter { get; }

        public PlayerKey Key => new PlayerKey(ClientId, Slot);

        public string Describe() => "self-damage-result{key=" + ClientId + ":" + Slot +
            " seq=" + Sequence + " " + (Accepted ? "accepted" : "rejected:" + Reason) +
            " amount=" + Amount.ToString("0.####") +
            " health=" + HealthAfter.ToString("0.###") + "}";
    }

    /// <summary>自我伤害数值 / 原因码的**纯规则**（无 Game 依赖，自检直接覆盖生产代码）。</summary>
    public static class PlayerSelfDamageRules
    {
        /// <summary>
        /// 可施加的伤害量：有限数、<c>&gt; 0</c>、<c>&lt;= 1</c>。
        /// <c>0</c> 被明确拒绝而不是"接受但什么也不做"：引擎
        /// <c>ComponentHealth.Injure</c> 对 <c>amount &lt;= 0</c> 直接 return
        /// （<c>ComponentHealth.cs:76</c>），接受它就等于把无操作包装成"主机已裁决发生"。
        /// </summary>
        public static bool IsAcceptedAmount(float amount, out string reason)
        {
            reason = null;
            if (!PlayerVitalsSnapshot.IsFiniteValue(amount))
            {
                reason = "amount 不是有限数：" + amount;
                return false;
            }
            if (!(amount > 0f))
            {
                reason = "amount = " + amount + " 非法（必须 > 0；引擎 Injure 对 0 是无操作）";
                return false;
            }
            if (amount > PlayerSelfDamageLimits.MaxAmount)
            {
                reason = "amount = " + amount + " 超过上限 " +
                    PlayerSelfDamageLimits.MaxAmount;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// <c>PLAYER_SELF_DAMAGE_REQUEST</c>（id 106）/ <c>PLAYER_SELF_DAMAGE_RESULT</c>（id 107）
    /// 的有界编解码。与入睡 / 唤醒请求同一套纪律：编码对非法数据抛异常；解码返回 false +
    /// 可直接定位的原因（绝不静默丢弃 / 绝不夹紧）。
    /// </summary>
    public static class PlayerSelfDamageCodec
    {
        public static SD.PlayerSelfDamageRequest Encode(in PlayerSelfDamageRequestData data)
        {
            if (!TryEncode(in data, out SD.PlayerSelfDamageRequest message, out string reason))
                throw new InvalidOperationException("自我伤害请求编码失败：" + reason);
            return message;
        }

        /// <summary>编码成线上载荷字节（客户端用 <c>IMessageSender.Send(0, ...)</c> 发给主机）。</summary>
        public static byte[] EncodeBytes(in PlayerSelfDamageRequestData data)
        {
            SD.PlayerSelfDamageRequest message = Encode(in data);
            byte[] payload = message.ToByteArray();
            if (payload.Length > PlayerSelfDamageLimits.MaxRequestBytes)
                throw new InvalidOperationException("自我伤害请求 " + payload.Length +
                    " 字节超过上限 " + PlayerSelfDamageLimits.MaxRequestBytes);
            return payload;
        }

        public static bool TryDecode(SD.PlayerSelfDamageRequest message,
            out PlayerSelfDamageRequestData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "self-damage-request-null";
                return false;
            }
            if (!CheckExt(message.Ext, out reason)) return false;
            if (message.ClientId > int.MaxValue)
            {
                reason = "client_id=" + message.ClientId + " 超出 int 范围";
                return false;
            }
            int clientId = (int)message.ClientId;
            if (clientId <= 0)
            {
                reason = "client_id=" + clientId + " 非法（请求只能来自玩家端，> 0）";
                return false;
            }
            if (message.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + message.Slot + " 非法（0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            if (message.Sequence == 0 || message.Sequence > (ulong)PlayerSelfDamageLimits.MaxSequence)
            {
                reason = "sequence=" + message.Sequence + " 非法（1.." +
                    PlayerSelfDamageLimits.MaxSequence + "）";
                return false;
            }
            if (!PlayerSelfDamageCauses.TryNormalize(message.Cause, out string cause,
                    out string causeReason))
            {
                reason = causeReason;
                return false;
            }
            if (!PlayerSelfDamageRules.IsAcceptedAmount(message.Amount, out string amountReason))
            {
                reason = amountReason;
                return false;
            }
            int size = message.CalculateSize();
            if (size > PlayerSelfDamageLimits.MaxRequestBytes)
            {
                reason = "请求 " + size + " 字节超过上限 " +
                    PlayerSelfDamageLimits.MaxRequestBytes;
                return false;
            }
            data = new PlayerSelfDamageRequestData(clientId, (int)message.Slot,
                (long)message.Sequence, cause, message.Amount);
            return true;
        }

        public static SD.PlayerSelfDamageResult EncodeResult(in PlayerSelfDamageResultData data)
        {
            if (!TryEncodeResult(in data, out SD.PlayerSelfDamageResult message, out string reason))
                throw new InvalidOperationException("自我伤害结果编码失败：" + reason);
            return message;
        }

        /// <summary>编码成线上载荷字节（主机用 <c>ITransportPort.TrySendTo</c> 定向回执）。</summary>
        public static byte[] EncodeResultBytes(in PlayerSelfDamageResultData data)
        {
            SD.PlayerSelfDamageResult message = EncodeResult(in data);
            byte[] payload = message.ToByteArray();
            if (payload.Length > PlayerSelfDamageLimits.MaxResultBytes)
                throw new InvalidOperationException("自我伤害结果 " + payload.Length +
                    " 字节超过上限 " + PlayerSelfDamageLimits.MaxResultBytes);
            return payload;
        }

        public static bool TryEncodeResult(in PlayerSelfDamageResultData data,
            out SD.PlayerSelfDamageResult message, out string reason)
        {
            message = null;
            reason = null;
            if (data.ClientId <= 0)
            {
                reason = "client_id=" + data.ClientId + " 非法（必须 > 0）";
                return false;
            }
            if (data.Slot < 0 || data.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + data.Slot + " 非法（0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            if (data.Sequence <= 0 || data.Sequence > PlayerSelfDamageLimits.MaxSequence)
            {
                reason = "sequence=" + data.Sequence + " 非法";
                return false;
            }
            string code = PlayerSelfDamageReasons.Key(data.Reason);
            if (data.Accepted && code != PlayerSelfDamageReasons.Accepted)
            {
                reason = "接受的结果不能带原因码 " + code;
                return false;
            }
            if (!data.Accepted && code == PlayerSelfDamageReasons.Accepted)
            {
                reason = "拒绝的结果必须有原因码";
                return false;
            }
            if (code.Length > PlayerSelfDamageLimits.MaxReasonLength)
            {
                reason = "reason 长度 " + code.Length + " 超过 " +
                    PlayerSelfDamageLimits.MaxReasonLength;
                return false;
            }
            string cause = data.Cause ?? string.Empty;
            if (cause.Length > PlayerSelfDamageLimits.MaxCauseLength)
            {
                reason = "cause 长度 " + cause.Length + " 超过 " +
                    PlayerSelfDamageLimits.MaxCauseLength;
                return false;
            }
            if (data.Accepted)
            {
                if (!PlayerSelfDamageRules.IsAcceptedAmount(data.Amount, out string amountReason))
                {
                    reason = "接受的结果带非法 amount：" + amountReason;
                    return false;
                }
                if (!PlayerVitalsSnapshot.IsFiniteValue(data.HealthAfter) ||
                    data.HealthAfter < 0f || data.HealthAfter > 1f)
                {
                    reason = "接受的结果带非法 health_after=" + data.HealthAfter + "（0..1）";
                    return false;
                }
            }
            else
            {
                // 拒绝**绝不**回传"施加后的血量"：那会让客户端把一次没发生的事当成权威值。
                if (data.Amount != 0f)
                {
                    reason = "拒绝的结果不能带 amount=" + data.Amount;
                    return false;
                }
                if (data.HealthAfter != 0f)
                {
                    reason = "拒绝的结果不能带 health_after=" + data.HealthAfter;
                    return false;
                }
            }
            message = new SD.PlayerSelfDamageResult
            {
                ClientId = (uint)data.ClientId,
                Slot = (uint)data.Slot,
                Sequence = (ulong)data.Sequence,
                Kind = data.Accepted
                    ? SD.PlayerSelfDamageResultKind.Accepted
                    : SD.PlayerSelfDamageResultKind.Rejected,
                Reason = code,
                Cause = cause,
                Amount = data.Amount,
                HealthAfter = data.HealthAfter
            };
            if (message.CalculateSize() > PlayerSelfDamageLimits.MaxResultBytes)
            {
                reason = "结果超过 " + PlayerSelfDamageLimits.MaxResultBytes + " 字节";
                message = null;
                return false;
            }
            return true;
        }

        public static bool TryDecodeResult(SD.PlayerSelfDamageResult message,
            out PlayerSelfDamageResultData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "self-damage-result-null";
                return false;
            }
            if (!CheckExt(message.Ext, out reason)) return false;
            if (message.ClientId > int.MaxValue)
            {
                reason = "client_id=" + message.ClientId + " 超出 int 范围";
                return false;
            }
            int clientId = (int)message.ClientId;
            if (clientId <= 0)
            {
                reason = "client_id=" + clientId + " 非法（结果只发给玩家端，> 0）";
                return false;
            }
            if (message.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + message.Slot + " 非法（0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            if (message.Sequence == 0 || message.Sequence > (ulong)PlayerSelfDamageLimits.MaxSequence)
            {
                reason = "sequence=" + message.Sequence + " 非法（1.." +
                    PlayerSelfDamageLimits.MaxSequence + "）";
                return false;
            }
            bool accepted;
            switch (message.Kind)
            {
                case SD.PlayerSelfDamageResultKind.Accepted:
                    accepted = true;
                    break;
                case SD.PlayerSelfDamageResultKind.Rejected:
                    accepted = false;
                    break;
                default:
                    reason = "kind=" + message.Kind + " 非法（必须是 ACCEPTED / REJECTED）";
                    return false;
            }
            string code = PlayerSelfDamageReasons.Key(message.Reason);
            if (accepted && code != PlayerSelfDamageReasons.Accepted)
            {
                reason = "接受的结果却带 reason=" + message.Reason;
                return false;
            }
            if (!accepted && code == PlayerSelfDamageReasons.Accepted)
            {
                reason = "拒绝的结果没有原因码（reason 为空 / accepted）";
                return false;
            }
            if (code.Length > PlayerSelfDamageLimits.MaxReasonLength)
            {
                reason = "reason 长度 " + code.Length + " 超过 " +
                    PlayerSelfDamageLimits.MaxReasonLength;
                return false;
            }
            if (message.Cause.Length > PlayerSelfDamageLimits.MaxCauseLength)
            {
                reason = "cause 长度 " + message.Cause.Length + " 超过 " +
                    PlayerSelfDamageLimits.MaxCauseLength;
                return false;
            }
            if (accepted)
            {
                if (!PlayerSelfDamageRules.IsAcceptedAmount(message.Amount, out string amountReason))
                {
                    reason = "接受的结果带非法 amount：" + amountReason;
                    return false;
                }
                if (!PlayerVitalsSnapshot.IsFiniteValue(message.HealthAfter) ||
                    message.HealthAfter < 0f || message.HealthAfter > 1f)
                {
                    reason = "接受的结果带非法 health_after=" + message.HealthAfter + "（0..1）";
                    return false;
                }
            }
            else
            {
                if (message.Amount != 0f || message.HealthAfter != 0f)
                {
                    reason = "拒绝的结果带 amount=" + message.Amount +
                        " health_after=" + message.HealthAfter + "（都必须是 0）";
                    return false;
                }
            }
            int size = message.CalculateSize();
            if (size > PlayerSelfDamageLimits.MaxResultBytes)
            {
                reason = "结果 " + size + " 字节超过上限 " +
                    PlayerSelfDamageLimits.MaxResultBytes;
                return false;
            }
            data = new PlayerSelfDamageResultData(clientId, (int)message.Slot,
                (long)message.Sequence, accepted, code, message.Cause, message.Amount,
                message.HealthAfter);
            return true;
        }

        private static bool CheckExt(Google.Protobuf.ByteString ext, out string reason)
        {
            reason = null;
            int length = ext?.Length ?? 0;
            if (length <= PlayerSelfDamageLimits.MaxExtBytes) return true;
            reason = "ext=" + length + " 字节（内置消息必须为空；第三方扩展走 custom）";
            return false;
        }

        private static bool TryEncode(in PlayerSelfDamageRequestData data,
            out SD.PlayerSelfDamageRequest message, out string reason)
        {
            message = null;
            reason = null;
            if (data.ClientId <= 0)
            {
                reason = "client_id=" + data.ClientId + " 非法（必须 > 0）";
                return false;
            }
            if (data.Slot < 0 || data.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + data.Slot + " 非法（0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            if (data.Sequence <= 0 || data.Sequence > PlayerSelfDamageLimits.MaxSequence)
            {
                reason = "sequence=" + data.Sequence + " 非法";
                return false;
            }
            if (!PlayerSelfDamageCauses.TryNormalize(data.Cause, out string cause,
                    out string causeReason))
            {
                reason = causeReason;
                return false;
            }
            if (!PlayerSelfDamageRules.IsAcceptedAmount(data.Amount, out string amountReason))
            {
                reason = amountReason;
                return false;
            }
            message = new SD.PlayerSelfDamageRequest
            {
                ClientId = (uint)data.ClientId,
                Slot = (uint)data.Slot,
                Sequence = (ulong)data.Sequence,
                Cause = cause,
                Amount = data.Amount
            };
            if (message.CalculateSize() > PlayerSelfDamageLimits.MaxRequestBytes)
            {
                reason = "请求超过 " + PlayerSelfDamageLimits.MaxRequestBytes + " 字节";
                message = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 客户端一帧的自我伤害读数：<see cref="Pressed"/> = 角色信息界面的骷髅按钮
    /// （<c>VitalStatsWidget</c> 的 <c>ChokeButton</c>）这一帧的 <c>IsClicked</c>；
    /// <see cref="Dead"/> = 本机角色当前 <c>Health &lt;= 0</c>（主机权威值）。
    ///
    /// <see cref="HasAuthorityHealth"/> / <see cref="AuthorityHealth"/> = 客户端缓存的主机
    /// 权威生命（最近一份 <c>PLAYER_VITALS_REPORT</c> / 已接受的 107 结果）。**没有权威值就不发
    /// 请求**：引擎控件那一行 <c>Injure(0.1f, ...)</c> 是纯本地预测，没有权威值就无法在
    /// <c>PlayerData.Update</c> 之前把它回滚，客户端可能自己判死 —— 这是明确失败，不是猜。
    /// </summary>
    public readonly struct PlayerSelfDamagePressState
    {
        public PlayerSelfDamagePressState(bool pressed, bool dead)
            : this(pressed, dead, false, 0f)
        {
        }

        public PlayerSelfDamagePressState(bool pressed, bool dead, bool hasAuthorityHealth,
            float authorityHealth)
        {
            Pressed = pressed;
            Dead = dead;
            HasAuthorityHealth = hasAuthorityHealth;
            AuthorityHealth = authorityHealth;
        }

        public bool Pressed { get; }
        public bool Dead { get; }

        /// <summary>false = 还没收到主机权威生命（本帧不发送，客户端不自行判死）。</summary>
        public bool HasAuthorityHealth { get; }

        /// <summary>缓存的主机权威生命（<see cref="HasAuthorityHealth"/> 为 true 时有效）。</summary>
        public float AuthorityHealth { get; }

        public override string ToString() =>
            "selfDamagePress{pressed=" + Pressed + " dead=" + Dead + " authority=" +
            (HasAuthorityHealth ? AuthorityHealth.ToString("0.###") : "missing") + "}";
    }

    /// <summary>一次点击采样的判定结果（客户端门控）。</summary>
    public enum PlayerSelfDamageGateAction
    {
        None = 0,

        /// <summary>发送一条 <c>PLAYER_SELF_DAMAGE_REQUEST</c>。</summary>
        SendRequest = 1,

        /// <summary>同一个按下（按钮还没松手 / 同一帧被读了两次），不是新的点击。</summary>
        SuppressedSamePress = 2,

        /// <summary>角色已经死亡（不再请求自我伤害）。</summary>
        SuppressedDead = 3,

        /// <summary>请求序号非法。</summary>
        SuppressedInvalidSequence = 4,

        /// <summary>本次按下已经发过请求（同一次按下只发一条）。</summary>
        SuppressedAlreadySent = 5,

        /// <summary>
        /// 还没有主机权威生命（客户端不能证明本地预测能回滚到哪个值）→ 不发请求。
        /// 这是明确的抑制原因，不是静默丢弃：模块计数 + 限流日志。
        /// </summary>
        SuppressedNoAuthority = 6
    }

    /// <summary>
    /// 客户端**一次按下最多一条** <c>PLAYER_SELF_DAMAGE_REQUEST</c> 的门控。
    ///
    /// 为什么需要边沿检测（必须解决的问题）：<c>ClickableWidget.IsClicked</c> 是
    /// <c>input.Click</c> 在那一帧为真时置位（<c>ClickableWidget.cs:31-40</c>），而模块与
    /// 引擎控件的更新顺序不保证在同一帧内只看一次：同一份"真"可能被读到两次（一帧内多次
    /// 采样 / 下一帧才被控件清掉）。所以这里保存**上一次采样值**，只有
    /// <c>false → true</c> 才算一次新的按下；一次按下最多发一条请求，必须松手后才可能
    /// 产生下一次（<see cref="OnSample"/> 在按下沿置位 <c>SentThisPress</c>）。
    ///
    /// 发送失败**不**重发同一次按下（那会在按住时按帧刷请求）：玩家重新点一次即可。
    /// </summary>
    public sealed class PlayerSelfDamagePressGate
    {
        private sealed class Entry
        {
            /// <summary>上一次采样时按钮是否是按下的（边沿检测的唯一状态）。</summary>
            public bool Pressed;
            /// <summary>本次按下是否已经发过请求。</summary>
            public bool SentThisPress;
            public long LastSequence;
        }

        private readonly System.Collections.Generic.Dictionary<PlayerKey, Entry> m_entries =
            new System.Collections.Generic.Dictionary<PlayerKey, Entry>();

        public int Count => m_entries.Count;

        public bool Contains(PlayerKey key) => m_entries.ContainsKey(key);

        public void CopyKeys(System.Collections.Generic.List<PlayerKey> target)
        {
            if (target == null) return;
            target.Clear();
            foreach (PlayerKey key in m_entries.Keys) target.Add(key);
        }

        public void Clear() => m_entries.Clear();

        private Entry Get(PlayerKey key)
        {
            if (!m_entries.TryGetValue(key, out Entry entry))
            {
                entry = new Entry();
                m_entries[key] = entry;
            }
            return entry;
        }

        /// <summary>
        /// 登记一个本机槽位（**不改变按下沿**）：初始状态是"没有按下"，所以登记后的第一次
        /// 按下就是一次合法的新点击。重复登记幂等。
        /// </summary>
        public void Register(PlayerKey key) => Get(key);

        /// <summary>
        /// 采样一帧。返回 <see cref="PlayerSelfDamageGateAction.SendRequest"/> = 这一帧是一次
        /// **新的按下**并且应当发出请求；其它值 = 明确抑制（<paramref name="reason"/> 给出稳定码）。
        /// </summary>
        public PlayerSelfDamageGateAction OnSample(PlayerKey key, in PlayerSelfDamagePressState state,
            long sequence, out string reason)
        {
            reason = null;
            Entry entry = Get(key);
            if (!state.Pressed)
            {
                // 松手（或这一帧没有点击）：边沿复位，下一次按下才是新请求。
                entry.Pressed = false;
                entry.SentThisPress = false;
                return PlayerSelfDamageGateAction.None;
            }
            if (entry.Pressed)
            {
                // 同一个按下：绝不当成第二次点击（一帧内被读两次 / 按钮还没松手）。
                reason = "same-press";
                return PlayerSelfDamageGateAction.SuppressedSamePress;
            }
            entry.Pressed = true;
            if (entry.SentThisPress)
            {
                reason = "already-sent";
                return PlayerSelfDamageGateAction.SuppressedAlreadySent;
            }
            if (!state.HasAuthorityHealth)
            {
                // 没有权威生命就没有"回滚到哪个值"：本地 Injure 可能把角色自己写死。
                // 不发请求（与"发送失败"不同：按下沿已经消耗，必须松手后再按）。
                reason = "no-authority-health";
                return PlayerSelfDamageGateAction.SuppressedNoAuthority;
            }
            if (state.Dead)
            {
                reason = PlayerSelfDamageReasons.NotAlive;
                return PlayerSelfDamageGateAction.SuppressedDead;
            }
            if (sequence <= 0 || sequence > PlayerSelfDamageLimits.MaxSequence)
            {
                reason = "invalid-sequence";
                return PlayerSelfDamageGateAction.SuppressedInvalidSequence;
            }
            entry.SentThisPress = true;
            entry.LastSequence = sequence;
            return PlayerSelfDamageGateAction.SendRequest;
        }

        /// <summary>诊断：这个键的按下沿状态（<c>false</c> = 没登记）。</summary>
        public bool TryGetPress(PlayerKey key, out bool pressed, out bool sentThisPress,
            out long sequence)
        {
            pressed = false;
            sentThisPress = false;
            sequence = 0;
            if (!m_entries.TryGetValue(key, out Entry entry)) return false;
            pressed = entry.Pressed;
            sentThisPress = entry.SentThisPress;
            sequence = entry.LastSequence;
            return true;
        }
    }
}

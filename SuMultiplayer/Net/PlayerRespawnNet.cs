using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Players;
using R = SuMultiplayer.Protocol.Respawn;
using V = SuMultiplayer.Protocol.Vitals;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// Client-requested respawn after a host-decided death: **pure logic** (codec,
    /// game-mode rules, 1.5 s tap guard, one-request-per-death gate).  This file
    /// references no Game type, so <c>P:\tmp\tsverify</c> compiles the same source
    /// and drives it without a device; the engine side (death camera / large
    /// message / input polling) lives in <c>Engine/PlayerVitalsReplica.cs</c> and
    /// the protocol / counters live in <c>Modules/PlayerVitalsModule.cs</c>.
    ///
    /// Engine facts this mirrors exactly:
    ///   · <c>PlayerData.PlayerDead</c> enter handler:
    ///     <c>GameWidget.ActiveCamera = GameWidget.FindCamera&lt;DeathCamera&gt;()</c>,
    ///     then <c>ComponentGui.DisplayLargeMessage("YOU HAVE DIED", ..., 30f, 1.5f)</c>
    ///     (<c>Survivalcraft/Game/PlayerData.cs:275-300</c>);
    ///   · the tap gate is <c>Time.RealTime - m_playerDeathTime &gt; 1.5</c> plus
    ///     <c>!DialogsManager.HasDialogs(...)</c> plus <c>GameWidget.Input.Any</c>
    ///     (<c>PlayerData.cs:303-306</c>);
    ///   · Cruel → "Cannot respawn in \"Cruel\" game mode" and the engine's tap
    ///     opens <c>GameMenuDialog</c>; Adventure without
    ///     <c>IsAdventureRespawnAllowed</c> → "Tap to restart adventure" and the
    ///     engine switches to <c>GameLoading / AdventureRestart</c>;
    ///     every other mode → "Tap to respawn" and removes the entity.
    ///
    /// Differences (deliberate, host-authoritative): the client does NOT enter the
    /// engine state machine and never removes its entity; the tap sends one
    /// request to the host and the host decides.
    ///
    /// Source: Survivalcraft/Game/PlayerData.cs:275-330
    /// Source: Survivalcraft/Game/GameWidget.cs:26-80（ActiveCamera setter + 每个 widget 的 DeathCamera）
    /// Source: Survivalcraft/Game/ComponentGui.cs:178-187（DisplayLargeMessage）
    /// </summary>
    public static class PlayerRespawnLimits
    {
        /// <summary>One request / result payload; above this the message is rejected.</summary>
        public const int MaxPayloadBytes = 512;

        /// <summary>Request / result reason text is a short stable code (never a sentence).</summary>
        public const int MaxReasonLength = 64;

        /// <summary>Request sequence upper bound (defensive; a session cannot reach it).</summary>
        public const long MaxSequence = long.MaxValue - 1;

        /// <summary>Coordinate bound for the accepted spawn point (metres).</summary>
        public const float MaxCoordinate = 10000000f;

        /// <summary><c>ext</c> must be empty for built-in messages (third parties use custom).</summary>
        public const int MaxExtBytes = 0;
    }

    /// <summary>Game-mode outcome of the engine rules (see <see cref="PlayerRespawnRules"/>).</summary>
    public enum PlayerRespawnMode
    {
        /// <summary>World mode could not be read: show the death screen but do not request.</summary>
        Unknown = 0,

        /// <summary>Vanilla / Survival / Challenging / Adventure with respawn allowed: tap requests.</summary>
        TapToRespawn = 1,

        /// <summary>Cruel: engine never respawns; no request path.</summary>
        Cruel = 2,

        /// <summary>Adventure with <c>IsAdventureRespawnAllowed == false</c>: engine restarts the adventure.</summary>
        AdventureRestart = 3
    }

    /// <summary>
    /// Stable rejection reasons (wire + status line keys).  These are exactly the
    /// reasons required by the product rule; <see cref="RespawnFailed"/> is the
    /// only additional code (engine refused a structurally valid request).
    /// </summary>
    public static class PlayerRespawnReasons
    {
        public const string Accepted = "accepted";
        public const string NotDead = "not-dead";
        public const string WrongOwner = "wrong-owner";
        public const string RespawnForbidden = "respawn-forbidden";
        public const string Cooldown = "cooldown";
        public const string UnknownPlayer = "unknown-player";
        public const string RespawnFailed = "respawn-failed";
        public const string Malformed = "malformed";
        public const string ClientDirection = "client-direction";

        /// <summary>Stable counter key: text before the first ':' (empty = accepted).</summary>
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
                case NotDead:
                case WrongOwner:
                case RespawnForbidden:
                case Cooldown:
                case UnknownPlayer:
                case RespawnFailed:
                case Malformed:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Pure mirror of the engine's death-screen mode rules and exact texts.
    /// </summary>
    public static class PlayerRespawnRules
    {
        /// <summary>Engine tap guard: <c>Time.RealTime - deathTime &gt; 1.5</c> seconds.</summary>
        public const int TapGuardMs = 1500;

        /// <summary>
        /// Same value the host applies before accepting a request (a client that
        /// taps too early is rejected with <c>cooldown</c>).  Network latency means
        /// an honest client always clears it; this is the anti-spam floor.
        /// </summary>
        public const int HostGuardMs = TapGuardMs;

        /// <summary>Client gives up waiting for a result after this long (explicit counter, no resend).</summary>
        public const int RequestTimeoutMs = 5000;

        /// <summary>
        /// 主机以<b>可恢复</b>原因（<c>cooldown</c>：请求早于主机的 1.5 s 门槛）拒绝之后，
        /// 客户端允许再点一次之前必须等的时间。其它拒绝原因（<c>not-dead</c> /
        /// <c>wrong-owner</c> / <c>respawn-forbidden</c> / <c>unknown-player</c>）是终局，
        /// 不重试 —— 重试也不会变成成功，只会刷屏。
        /// </summary>
        public const int RetryAfterRejectMs = 2000;

        /// <summary>
        /// 两次输入采样间隔在这个窗口内、且两次都按着 = **同一个按下**（按住 / 键盘自动重复）：
        /// 窗口按引擎帧时间给（60 Hz ≈ 16.7 ms，30 Hz ≈ 33 ms，20 Hz ≈ 50 ms），
        /// 所以 50 ms 覆盖到 20 fps 的"持续按住"；再长的间隔说明中间根本没采样到（掉帧），
        /// 那种情况下不能把新输入当成旧按下的延续（否则会漏掉真实点击）。
        /// </summary>
        public const int SamePressWindowMs = 50;

        /// <summary>Engine <c>DisplayLargeMessage</c> duration / delay (PlayerData.cs:275-300).</summary>
        public const float MessageDurationSeconds = 30f;
        public const float MessageDelaySeconds = 1.5f;

        /// <summary>Exact engine large text.</summary>
        public const string DeathLargeText = "YOU HAVE DIED";

        /// <summary>
        /// 复活进度画面（引擎 <c>SpawnDialog</c>，<c>PlayerData.PrepareSpawn</c> 的 Respawn 分支）
        /// 的小字：引擎原句，一字不改。
        /// Source: Survivalcraft/Game/PlayerData.cs:200-215
        /// </summary>
        public const string RespawnSmallText =
            "Your items will remain for a short time at the place of death.";

        /// <summary>
        /// 复活进度画面的大标题，与引擎同一格式串
        /// <c>$"Respawning {Name} (Level {MathUtils.Floor(Level)})"</c>。
        /// Source: Survivalcraft/Game/PlayerData.cs:203
        /// </summary>
        public static string BuildRespawnLargeText(string name, float level) =>
            "Respawning " + (string.IsNullOrEmpty(name) ? string.Empty : name) +
            " (Level " + MathUtils.Floor(level) + ")";

        public static PlayerRespawnMode Evaluate(bool cruel, bool adventure,
            bool adventureRespawnAllowed)
        {
            if (cruel) return PlayerRespawnMode.Cruel;
            if (adventure && !adventureRespawnAllowed) return PlayerRespawnMode.AdventureRestart;
            return PlayerRespawnMode.TapToRespawn;
        }

        /// <summary>Only tap-to-respawn modes have a request path.</summary>
        public static bool AllowsRequest(PlayerRespawnMode mode) =>
            mode == PlayerRespawnMode.TapToRespawn;

        /// <summary>
        /// Exact engine small text (cause + mode line).  <paramref name="cause"/>
        /// empty becomes <c>Unknown</c> (PlayerData.cs:279-283).
        /// </summary>
        public static string BuildDeathSmallText(string cause, PlayerRespawnMode mode,
            string gameModeName)
        {
            string causeText = string.IsNullOrEmpty(cause) ? "Unknown" : cause;
            string text = "Cause of death: " + causeText;
            switch (mode)
            {
                case PlayerRespawnMode.Cruel:
                    return text + "\n\nCannot respawn in \"" +
                        (string.IsNullOrEmpty(gameModeName) ? "Cruel" : gameModeName) +
                        "\" game mode";
                case PlayerRespawnMode.AdventureRestart:
                    return text + "\n\nTap to restart adventure";
                case PlayerRespawnMode.TapToRespawn:
                    return text + "\n\nTap to respawn";
                default:
                    // No silent fallback to "Tap to respawn": an unreadable world
                    // mode must be visible to the player and must not invent a
                    // request path.
                    return text + "\n\nRespawn unavailable (world mode unavailable)";
            }
        }

        public static string Describe(PlayerRespawnMode mode)
        {
            switch (mode)
            {
                case PlayerRespawnMode.TapToRespawn: return "tap-to-respawn";
                case PlayerRespawnMode.Cruel: return "cruel";
                case PlayerRespawnMode.AdventureRestart: return "adventure-restart";
                default: return "unknown";
            }
        }
    }

    // ================================================================ 死亡 epoch / 出生后重放

    /// <summary>
    /// 死亡 epoch：**每次主机裁决复活都会推进的一个版本号**（取"这次死亡的 revision"与
    /// "这次复活事件的 revision"的较大值）。它只做一件事：把复活之前产生的
    /// <c>Dead = true</c> 快照 / Death 事件挡在门外。
    ///
    /// 为什么必须有它（无中间态证明）：方案 A 下客户端的新角色是**新实体**，出生时引擎给的
    /// 血是模板值 1；而网络上仍可能在飞的旧死亡上报（revision 更小、Health = 0）一旦被写进
    /// 新实体，引擎 <c>PlayerData.Playing</c> 会立刻再判死一次（<c>PlayerData.cs:270</c>），
    /// 玩家会看到"刚复活就又死了"。epoch 让这类陈旧状态被丢弃并显式计数，而不是靠时序碰运气。
    /// </summary>
    public static class PlayerRespawnEpoch
    {
        /// <summary>revision 不新于 epoch 的死亡状态 = 陈旧的，必须丢弃（epoch = 0 表示还没有复活过）。</summary>
        public static bool IsStaleDead(uint epoch, uint revision) =>
            epoch != 0 && revision != 0 && revision <= epoch;

        /// <summary>推进 epoch：只增不减（同一死亡的多个信号取最大）。</summary>
        public static uint Advance(uint current, uint candidate) =>
            candidate > current ? candidate : current;
    }

    /// <summary>出生后重放（<c>TryReplayLocalRespawn</c>）的结果。</summary>
    public enum PlayerRespawnReplayStatus
    {
        /// <summary>引擎还在 PrepareSpawn / WaitForTerrain（本机还没有新实体）：下一帧再问。</summary>
        Waiting = 0,

        /// <summary>新实体已出生，本次调用完成了权威重放（落点 / 背包 / 衣服 / 禁本地模拟 / HUD）。</summary>
        Applied = 1,

        /// <summary>没有待重放的复活（已经应用过 / 根本没死）。</summary>
        NoPending = 2,

        /// <summary>重放失败（原因由 <c>reason</c> 给出，调用方计数 + 限流日志，绝不假装成功）。</summary>
        Failed = 3
    }

    /// <summary>
    /// 一次"出生后权威重放"请求（模块 → 引擎端口）。主机裁决的落点 + 两级 revision：
    ///   · <see cref="DeathRevision"/> = 这次死亡的 revision（主机 Death 事件里的那个）；
    ///   · <see cref="RespawnRevision"/> = 主机 Respawn 事件的 revision（可能比死亡更新）。
    /// <see cref="Epoch"/> 是两者的较大值，模块用它挡陈旧死亡状态。
    /// </summary>
    public readonly struct PlayerRespawnReplayRequest
    {
        public PlayerRespawnReplayRequest(uint deathRevision, uint respawnRevision,
            bool hasPosition, Vector3 position)
        {
            DeathRevision = deathRevision;
            RespawnRevision = respawnRevision;
            HasPosition = hasPosition;
            Position = position;
        }

        public uint DeathRevision { get; }
        public uint RespawnRevision { get; }
        public bool HasPosition { get; }
        public Vector3 Position { get; }

        public uint Epoch => PlayerRespawnEpoch.Advance(DeathRevision, RespawnRevision);

        public bool HasFinitePosition =>
            PlayerVitalsSnapshot.IsFiniteValue(Position.X) &&
            PlayerVitalsSnapshot.IsFiniteValue(Position.Y) &&
            PlayerVitalsSnapshot.IsFiniteValue(Position.Z);

        public string Describe() => "respawn-replay{death=" + DeathRevision + " respawn=" +
            RespawnRevision + (HasPosition ? " @" + Position : " no-position") + "}";
    }

    /// <summary>One validated respawn request (client -> host).</summary>
    public readonly struct PlayerRespawnRequestData
    {
        public PlayerRespawnRequestData(int clientId, int slot, uint deathRevision,
            long requestSequence, double clientDeathTime)
        {
            ClientId = clientId;
            Slot = slot;
            DeathRevision = deathRevision;
            RequestSequence = requestSequence;
            ClientDeathTime = clientDeathTime;
        }

        public int ClientId { get; }
        public int Slot { get; }
        public uint DeathRevision { get; }
        public long RequestSequence { get; }
        public double ClientDeathTime { get; }

        public PlayerKey Key => new PlayerKey(ClientId, Slot);

        public string Describe() => "respawn-request{key=" + ClientId + ":" + Slot +
            " rev=" + DeathRevision + " seq=" + RequestSequence + "}";
    }

    /// <summary>One validated respawn verdict (host -> owner).</summary>
    public readonly struct PlayerRespawnResultData
    {
        public PlayerRespawnResultData(int clientId, int slot, long requestSequence,
            bool accepted, string reason, bool hasPosition, Vector3 position)
        {
            ClientId = clientId;
            Slot = slot;
            RequestSequence = requestSequence;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            HasPosition = hasPosition;
            Position = position;
        }

        public int ClientId { get; }
        public int Slot { get; }
        public long RequestSequence { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public bool HasPosition { get; }
        public Vector3 Position { get; }

        public PlayerKey Key => new PlayerKey(ClientId, Slot);
    }

    /// <summary>
    /// Bounded codec for ids 101 / 102.  Encoding throws on invalid data;
    /// decoding returns false + a directly actionable reason (never a silent drop).
    /// </summary>
    public static class PlayerRespawnCodec
    {
        public static R.PlayerRespawnRequest EncodeRequest(in PlayerRespawnRequestData data)
        {
            if (!TryEncode(in data, out R.PlayerRespawnRequest message, out string reason))
                throw new InvalidOperationException("复活请求编码失败：" + reason);
            return message;
        }

        public static byte[] EncodeRequestBytes(in PlayerRespawnRequestData data)
        {
            R.PlayerRespawnRequest message = EncodeRequest(in data);
            byte[] payload = message.ToByteArray();
            if (payload.Length > PlayerRespawnLimits.MaxPayloadBytes)
                throw new InvalidOperationException("复活请求 " + payload.Length +
                    " 字节超过上限 " + PlayerRespawnLimits.MaxPayloadBytes);
            return payload;
        }

        public static bool TryDecodeRequest(R.PlayerRespawnRequest message,
            out PlayerRespawnRequestData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "respawn-request-null";
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
            if (message.RequestSequence <= 0 ||
                message.RequestSequence > PlayerRespawnLimits.MaxSequence)
            {
                reason = "request_sequence=" + message.RequestSequence + " 非法（1.." +
                    PlayerRespawnLimits.MaxSequence + "）";
                return false;
            }
            if (!IsFinite(message.ClientDeathTime))
            {
                reason = "client_death_time 不是有限数：" + message.ClientDeathTime;
                return false;
            }
            int size = message.CalculateSize();
            if (size > PlayerRespawnLimits.MaxPayloadBytes)
            {
                reason = "请求 " + size + " 字节超过上限 " + PlayerRespawnLimits.MaxPayloadBytes;
                return false;
            }
            data = new PlayerRespawnRequestData(clientId, (int)message.Slot,
                message.DeathRevision, message.RequestSequence, message.ClientDeathTime);
            return true;
        }

        public static R.PlayerRespawnResult EncodeResult(in PlayerRespawnResultData data)
        {
            if (!TryEncode(in data, out R.PlayerRespawnResult message, out string reason))
                throw new InvalidOperationException("复活结果编码失败：" + reason);
            return message;
        }

        public static byte[] EncodeResultBytes(in PlayerRespawnResultData data)
        {
            R.PlayerRespawnResult message = EncodeResult(in data);
            byte[] payload = message.ToByteArray();
            if (payload.Length > PlayerRespawnLimits.MaxPayloadBytes)
                throw new InvalidOperationException("复活结果 " + payload.Length +
                    " 字节超过上限 " + PlayerRespawnLimits.MaxPayloadBytes);
            return payload;
        }

        public static bool TryDecodeResult(R.PlayerRespawnResult message,
            out PlayerRespawnResultData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "respawn-result-null";
                return false;
            }
            if (!CheckExt(message.Ext, out reason)) return false;
            if (message.ClientId > int.MaxValue)
            {
                reason = "client_id=" + message.ClientId + " 超出 int 范围";
                return false;
            }
            int clientId = (int)message.ClientId;
            if (clientId < 0)
            {
                reason = "client_id=" + clientId + " 非法（>= 0）";
                return false;
            }
            if (message.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + message.Slot + " 非法（0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            if (message.RequestSequence <= 0 ||
                message.RequestSequence > PlayerRespawnLimits.MaxSequence)
            {
                reason = "request_sequence=" + message.RequestSequence + " 非法";
                return false;
            }
            bool accepted;
            if (message.Kind == R.PlayerRespawnResultKind.Accepted) accepted = true;
            else if (message.Kind == R.PlayerRespawnResultKind.Rejected) accepted = false;
            else
            {
                reason = "result kind 未指定（0）→ 无法判断接受 / 拒绝";
                return false;
            }
            string code = message.Reason ?? string.Empty;
            if (code.Length == 0 || code.Length > PlayerRespawnLimits.MaxReasonLength)
            {
                reason = "reason 长度非法（1.." + PlayerRespawnLimits.MaxReasonLength + "）：" +
                    code.Length;
                return false;
            }
            if (accepted && (code != PlayerRespawnReasons.Accepted ||
                             message.HasPosition == false))
            {
                reason = accepted
                    ? "已接受的 result 必须带 reason=accepted 和落点（当前 reason=" + code +
                      " has_position=" + message.HasPosition + "）"
                    : string.Empty;
                return false;
            }
            if (!accepted && message.HasPosition)
            {
                reason = "被拒绝的 result 不得带落点";
                return false;
            }
            Vector3 position = Vector3.Zero;
            if (message.HasPosition)
            {
                if (message.Position == null)
                {
                    reason = "has_position=true 但 position 缺失";
                    return false;
                }
                position = new Vector3(message.Position.X, message.Position.Y,
                    message.Position.Z);
                if (!IsFinite(position) || MathUtils.Abs(position.X) >
                        PlayerRespawnLimits.MaxCoordinate ||
                    MathUtils.Abs(position.Y) > PlayerRespawnLimits.MaxCoordinate ||
                    MathUtils.Abs(position.Z) > PlayerRespawnLimits.MaxCoordinate)
                {
                    reason = "复活落点不是有限值 / 超出坐标上限：" + position;
                    return false;
                }
            }
            int size = message.CalculateSize();
            if (size > PlayerRespawnLimits.MaxPayloadBytes)
            {
                reason = "结果 " + size + " 字节超过上限 " + PlayerRespawnLimits.MaxPayloadBytes;
                return false;
            }
            data = new PlayerRespawnResultData(clientId, (int)message.Slot,
                message.RequestSequence, accepted, code, message.HasPosition, position);
            return true;
        }

        private static bool TryEncode(in PlayerRespawnRequestData data,
            out R.PlayerRespawnRequest message, out string reason)
        {
            message = null;
            reason = null;
            if (data.ClientId <= 0)
            {
                reason = "client_id=" + data.ClientId + " 非法（> 0）";
                return false;
            }
            if (data.Slot < 0 || data.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + data.Slot + " 非法";
                return false;
            }
            if (data.RequestSequence <= 0 ||
                data.RequestSequence > PlayerRespawnLimits.MaxSequence)
            {
                reason = "request_sequence=" + data.RequestSequence + " 非法";
                return false;
            }
            if (!IsFinite(data.ClientDeathTime))
            {
                reason = "client_death_time 不是有限数：" + data.ClientDeathTime;
                return false;
            }
            message = new R.PlayerRespawnRequest
            {
                ClientId = (uint)data.ClientId,
                Slot = (uint)data.Slot,
                DeathRevision = data.DeathRevision,
                RequestSequence = data.RequestSequence,
                ClientDeathTime = data.ClientDeathTime
            };
            if (message.CalculateSize() > PlayerRespawnLimits.MaxPayloadBytes)
            {
                reason = "请求超过 " + PlayerRespawnLimits.MaxPayloadBytes + " 字节";
                message = null;
                return false;
            }
            return true;
        }

        private static bool TryEncode(in PlayerRespawnResultData data,
            out R.PlayerRespawnResult message, out string reason)
        {
            message = null;
            reason = null;
            if (data.ClientId < 0)
            {
                reason = "client_id=" + data.ClientId + " 非法（>= 0）";
                return false;
            }
            if (data.Slot < 0 || data.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + data.Slot + " 非法";
                return false;
            }
            if (data.RequestSequence <= 0 ||
                data.RequestSequence > PlayerRespawnLimits.MaxSequence)
            {
                reason = "request_sequence=" + data.RequestSequence + " 非法";
                return false;
            }
            string code = data.Reason ?? string.Empty;
            if (code.Length == 0 || code.Length > PlayerRespawnLimits.MaxReasonLength)
            {
                reason = "reason 长度非法（1.." + PlayerRespawnLimits.MaxReasonLength + "）";
                return false;
            }
            if (data.Accepted && code != PlayerRespawnReasons.Accepted)
            {
                reason = "接受的 result 的 reason 必须是 accepted（当前 " + code + "）";
                return false;
            }
            if (!data.Accepted && code == PlayerRespawnReasons.Accepted)
            {
                reason = "拒绝的 result 不能写 reason=accepted";
                return false;
            }
            if (!data.Accepted && data.HasPosition)
            {
                reason = "拒绝的 result 不得带落点";
                return false;
            }
            if (data.Accepted && !data.HasPosition)
            {
                reason = "接受的 result 必须带落点";
                return false;
            }
            message = new R.PlayerRespawnResult
            {
                ClientId = (uint)data.ClientId,
                Slot = (uint)data.Slot,
                RequestSequence = data.RequestSequence,
                Kind = data.Accepted
                    ? R.PlayerRespawnResultKind.Accepted
                    : R.PlayerRespawnResultKind.Rejected,
                Reason = code,
                HasPosition = data.HasPosition
            };
            if (data.HasPosition)
            {
                if (!IsFinite(data.Position))
                {
                    reason = "复活落点不是有限值：" + data.Position;
                    message = null;
                    return false;
                }
                message.Position = new V.VitalsVec3
                {
                    X = data.Position.X,
                    Y = data.Position.Y,
                    Z = data.Position.Z
                };
            }
            if (message.CalculateSize() > PlayerRespawnLimits.MaxPayloadBytes)
            {
                reason = "结果超过 " + PlayerRespawnLimits.MaxPayloadBytes + " 字节";
                message = null;
                return false;
            }
            return true;
        }

        private static bool CheckExt(ByteString ext, out string reason)
        {
            reason = null;
            int length = ext?.Length ?? 0;
            if (length <= PlayerRespawnLimits.MaxExtBytes) return true;
            reason = "ext=" + length + " 字节（内置消息必须为空；第三方扩展走 custom）";
            return false;
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
    }

    /// <summary>
    /// 一帧死亡屏输入里与"点击"有关的原始信号（**纯数据**，引擎侧由
    /// <c>PlayerVitalsReplica.TryPollLocalDeathTap</c> 从 <c>GameWidget.Input</c> 采样）。
    ///
    /// 为什么不能直接用引擎 <c>Input.Any</c> 判定点击（根因）：
    ///   · <c>WidgetInput.Any</c> 是**每帧重置的事件标志**（<c>WidgetInput.Update</c> 先
    ///     <c>ClearInput()</c>，再由各设备 update 置位），但键盘那条是
    ///     <c>if (LastKey.HasValue &amp;&amp; LastKey != Escape) Any = true;</c>
    ///     （<c>Survivalcraft/Game/WidgetInput.cs:704</c>），而 <c>Keyboard.LastKey</c> 在**按键自动重复**
    ///     时也会被置位（<c>Engine/Engine/Input/Keyboard.cs:170</c> <c>ProcessKeyDown</c> 无条件
    ///     <c>m_lastKey = key</c>），只有 <c>m_keysDownOnceArray</c> 才区分"第一次按下"
    ///     （<c>Keyboard.cs:171-176</c>）。因此**按住任意一个键（例如死亡瞬间正按着的 W/A/S/D）**
    ///     会让 <c>Any</c> 以按键重复频率反复为真 —— 死后 1.5 秒门槛一到就被当成"点击"，
    ///     玩家没点也发复活请求，表现就是"刚死就瞬间复活"。
    ///   · 真正的按下沿信号是：触摸 / 鼠标的 <c>Tap</c>（只在 <c>TouchLocationState.Pressed</c>
    ///     那一帧 / <c>IsMouseButtonDownOnce</c> 那一帧有值，<c>WidgetInput.cs:864-923</c>）、
    ///     鼠标 / 手柄的 <c>Click</c> / <c>SpecialClick</c>（<c>WidgetInput.cs:845</c> +
    ///     释放那一帧）、手柄的 <c>Ok</c>（<c>WidgetInput.cs:845</c>），以及键盘的
    ///     "<c>LastKey</c> 有值且 <c>IsKeyDownOnce(LastKey)</c>"（排除自动重复）。
    /// </summary>
    public readonly struct PlayerDeathInputSample
    {
        public PlayerDeathInputSample(bool any, bool tap, bool click, bool specialClick, bool ok,
            bool lastKeyPresent, bool lastKeyDownOnce)
        {
            Any = any;
            Tap = tap;
            Click = click;
            SpecialClick = specialClick;
            Ok = ok;
            LastKeyPresent = lastKeyPresent;
            LastKeyDownOnce = lastKeyDownOnce;
        }

        /// <summary>引擎 <c>WidgetInput.Any</c>（电平 / 按键重复都会置位）：**只用于日志与对照**。</summary>
        public bool Any { get; }

        /// <summary>触摸按下帧 / 鼠标左(右)键按下帧 / 手柄 A 按下帧。</summary>
        public bool Tap { get; }

        /// <summary>鼠标 / 手柄的"按下后原地释放"（一次完整点击）。</summary>
        public bool Click { get; }

        /// <summary>鼠标 / 手柄的"按住拖动后释放"。</summary>
        public bool SpecialClick { get; }

        /// <summary>手柄确认键（<c>WidgetInput.Ok</c>，只在按下帧为真）。</summary>
        public bool Ok { get; }

        /// <summary><c>WidgetInput.LastKey.HasValue</c>（自动重复期间也为真）。</summary>
        public bool LastKeyPresent { get; }

        /// <summary><c>WidgetInput.IsKeyDownOnce(LastKey.Value)</c>（自动重复期间为 false）。</summary>
        public bool LastKeyDownOnce { get; }

        /// <summary>
        /// 本帧是否出现**真正的按下沿**。按住不放 / 按键自动重复**不**算。
        /// </summary>
        public bool IsFreshPress =>
            Tap || Click || SpecialClick || Ok || (LastKeyPresent && LastKeyDownOnce);

        /// <summary>电平为真但没有任何按下沿 = 按住 / 自动重复（明确计数，不静默丢弃）。</summary>
        public bool IsLevelOnly => Any && !IsFreshPress;
    }

    /// <summary>What <see cref="PlayerRespawnGate.OnTap"/> / <see cref="PlayerRespawnGate.OnTick"/> decided.</summary>
    public enum PlayerRespawnGateAction
    {
        None = 0,
        SendRequest = 1,
        SuppressedEarly = 2,
        SuppressedMode = 3,
        SuppressedAlreadyRequested = 4,
        SuppressedResolved = 5,
        IgnoredNoDeath = 6,
        Timeout = 7,
        ResultAccepted = 8,
        ResultRejected = 9,
        ResultStale = 10,

        /// <summary>同一个按下的后续帧（按住 / 按键自动重复）：不是新点击（<see cref="PlayerRespawnGate.NoteInput"/>）。</summary>
        SuppressedSamePress = 11
    }

    /// <summary>Read-only snapshot of one dead local character (diagnostics / tests).</summary>
    public readonly struct PlayerRespawnPending
    {
        public PlayerRespawnPending(PlayerKey key, uint deathRevision, long deathAtMs,
            PlayerRespawnMode mode, bool requestSent, long requestAtMs, long requestSequence,
            bool resolved, bool timedOut, string resultReason)
        {
            Key = key;
            DeathRevision = deathRevision;
            DeathAtMs = deathAtMs;
            Mode = mode;
            RequestSent = requestSent;
            RequestAtMs = requestAtMs;
            RequestSequence = requestSequence;
            Resolved = resolved;
            TimedOut = timedOut;
            ResultReason = resultReason;
        }

        public PlayerKey Key { get; }
        public uint DeathRevision { get; }
        public long DeathAtMs { get; }
        public PlayerRespawnMode Mode { get; }
        public bool RequestSent { get; }
        public long RequestAtMs { get; }
        public long RequestSequence { get; }
        public bool Resolved { get; }
        public bool TimedOut { get; }
        public string ResultReason { get; }
    }

    /// <summary>
    /// Per-death client state machine: show screen once, wait 1.5 s, send **one**
    /// request per death, then ignore every further tap until the death is
    /// resolved (respawn event) or the result request times out.  The host never
    /// auto-respawns, so this gate is the only client-side request path.
    ///
    /// 点击口径是**按下沿**（<see cref="NoteInput"/> + <see cref="PlayerDeathInputSample.IsFreshPress"/>）：
    /// 按住不放 / 键盘自动重复的电平永远不是点击。
    /// </summary>
    public sealed class PlayerRespawnGate
    {
        private sealed class Entry
        {
            public uint DeathRevision;
            public long DeathAtMs;
            public PlayerRespawnMode Mode;
            public bool RequestSent;
            public long RequestAtMs;
            public long RequestSequence;
            public bool Resolved;
            public bool TimedOut;
            public string ResultReason = string.Empty;

            /// <summary>
            /// 主机以可恢复原因（<c>cooldown</c>）拒绝之后，允许下一次点击的最早时刻。
            /// 0 = 没有重试窗口。见 <see cref="PlayerRespawnGate.OnRejected"/>。
            /// </summary>
            public long RetryNotBeforeMs;

            /// <summary>
            /// 按下沿：true = 自上一次输入以来已经"松手过"，下一次输入可以算新按下。
            /// 只是电平 / 自动重复的持续为真**不会**重新置位（这是修复"按住输入就秒复活"的关键）。
            /// </summary>
            public bool PressReleased = true;

            /// <summary>上一次采样输入的时刻（判定"两次采样之间有没有松手帧"用）。</summary>
            public long LastInputMs;
        }

        private readonly Dictionary<PlayerKey, Entry> m_entries =
            new Dictionary<PlayerKey, Entry>();

        /// <summary>
        /// 帧时钟（毫秒），用于"两次采样之间的间隔"判定（掉帧补偿）。
        /// 生产模块在构造时喂 <c>NowMs</c>；没有时钟（自检里用带时刻的重载）时
        /// <see cref="NoteInput(PlayerKey,bool)"/> 只认松手帧。
        /// </summary>
        private readonly Func<long> m_now;

        public PlayerRespawnGate()
        {
        }

        public PlayerRespawnGate(Func<long> nowMs)
        {
            m_now = nowMs;
        }

        public int Count => m_entries.Count;

        public void Clear() => m_entries.Clear();

        public bool Contains(PlayerKey key) => m_entries.ContainsKey(key);

        public IReadOnlyList<PlayerKey> Keys
        {
            get
            {
                var keys = new List<PlayerKey>(m_entries.Count);
                foreach (PlayerKey key in m_entries.Keys) keys.Add(key);
                return keys;
            }
        }

        public bool TryGet(PlayerKey key, out PlayerRespawnPending pending)
        {
            pending = default;
            if (!m_entries.TryGetValue(key, out Entry entry)) return false;
            pending = new PlayerRespawnPending(key, entry.DeathRevision, entry.DeathAtMs,
                entry.Mode, entry.RequestSent, entry.RequestAtMs, entry.RequestSequence,
                entry.Resolved, entry.TimedOut, entry.ResultReason);
            return true;
        }

        /// <summary>
        /// Register a death edge / dead snapshot.  Returns true only when this is a
        /// **new** death episode (the caller then counts <c>deathScreenShown</c>
        /// and has already shown the screen via the port).
        /// <paramref name="isEventEdge"/> = true for a host Death event (a higher
        /// revision is a new death episode even if the previous one was never
        /// resolved, e.g. a Cruel death followed by a new life); false for a
        /// dead snapshot, which only updates the existing episode (never resets
        /// the 1.5 s / one-request state).
        /// </summary>
        public bool OnDeath(PlayerKey key, uint revision, PlayerRespawnMode mode, long nowMs,
            bool isEventEdge = true)
        {
            if (m_entries.TryGetValue(key, out Entry existing))
            {
                if (isEventEdge && revision > existing.DeathRevision)
                {
                    // A newer Death event is a new death episode: reset the one-shot gate.
                }
                else if (!existing.Resolved && !existing.TimedOut)
                {
                    if (revision > existing.DeathRevision) existing.DeathRevision = revision;
                    existing.Mode = mode;
                    return false;
                }
                else if (!isEventEdge)
                {
                    if (revision > existing.DeathRevision) existing.DeathRevision = revision;
                    existing.Mode = mode;
                    return false;
                }
            }
            m_entries[key] = new Entry
            {
                DeathRevision = revision,
                DeathAtMs = nowMs,
                Mode = mode,
                RequestSent = false,
                RequestAtMs = 0,
                RequestSequence = 0,
                Resolved = false,
                TimedOut = false,
                ResultReason = string.Empty
            };
            return true;
        }

        public bool OnRespawn(PlayerKey key) => m_entries.Remove(key);

        /// <summary>
        /// **每帧采样一次死亡屏输入**（按下沿判定，必须在 <see cref="OnTap"/> 之前调用）。
        /// 返回 true = 这一帧出现了一次**状态边沿**：
        ///   · <paramref name="pressed"/> = true 且上一次采样**已经松手** → <b>按下沿</b>（真点击）；
        ///   · <paramref name="pressed"/> = false 且上一次采样**还按着** → <b>松手沿</b>（"重新允许下一次按下"）。
        ///     **松手沿不是点击**：调用方只在 <paramref name="pressed"/> 为 true 时才喂给
        ///     <see cref="OnTap"/>；
        ///   · 状态没变（本来就是松手 / 本来就按着）→ false。
        ///
        /// 这正是"电平不触发、按下沿触发"的口径：
        ///   · 按住不放 / 键盘自动重复（每帧 <c>pressed</c> 都为 true）→ 只有第一帧是边沿；
        ///   · 生产模块每帧调用它（端口 <c>TryPollLocalDeathTap</c> 已经只报按下沿，
        ///     这里再挡一层"端口信号退化成电平"的退化）；
        ///   · 采样时刻由**门控自己的时钟**给出（构造时注入模块的 <c>NowMs</c>）：两次
        ///     <c>pressed=true</c> 的采样间隔超过
        ///     <see cref="PlayerRespawnRules.SamePressWindowMs"/> 就是两次**不同的按下**
        ///     —— 玩家连着点两次不会被吞掉（rc3 "1.4 s 后再点一次"）；
        ///   · 没有注入时钟时只按"上一次采样是否按着"判边沿（自检里用带时刻的重载）；
        ///   · 有现成采样时刻的调用方用 <see cref="NoteInput(PlayerKey,long,bool)"/>。
        ///
        /// 于是"死于同一输入的残余"最多在门槛期内被消费掉一次（那时本来就被
        /// <see cref="PlayerRespawnRules.TapGuardMs"/> 拒掉），之后按住再久也不会变成复活请求。
        /// 没有死亡期（还没登记死亡）时返回 false，并且**不会**创建条目。
        /// </summary>
        public bool NoteInput(PlayerKey key, bool pressed) => m_now == null
            ? NoteInputEdge(key, 0, pressed, false)
            : NoteInputEdge(key, m_now(), pressed, true);

        /// <summary>
        /// 与 <see cref="NoteInput(PlayerKey,bool)"/> 同一口径，但**带采样时刻**：
        /// 上一次采样就按着、且两次采样间隔不超过
        /// <see cref="PlayerRespawnRules.SamePressWindowMs"/> 才算"同一个按下"；
        /// 间隔更大说明中间根本没采样到（掉帧），这时不能把新输入当成旧按下的延续
        /// —— 否则真实的第二次点击会被吞掉（te4 用例）。
        ///
        /// 两个重载**不要混用**同一个键：无时间戳版本会把
        /// <see cref="Entry.LastInputMs"/> 归零（= "没有可用的采样时刻"），
        /// 之后的带时刻版本因此失去掉帧补偿（退化成保守的无时间戳口径），绝不会因此
        /// 把按住误判成新按下。
        /// </summary>
        public bool NoteInput(PlayerKey key, long nowMs, bool pressed) =>
            NoteInputEdge(key, nowMs, pressed, true);

        private bool NoteInputEdge(PlayerKey key, long nowMs, bool pressed, bool hasTime)
        {
            if (!m_entries.TryGetValue(key, out Entry entry)) return false;
            if (!pressed)
            {
                bool released = !entry.PressReleased;
                entry.PressReleased = true;
                entry.LastInputMs = hasTime ? nowMs : 0;
                return released;
            }
            bool continuous = !entry.PressReleased;
            if (continuous && hasTime && entry.LastInputMs > 0)
            {
                // "同一个按下"只可能是**紧邻的采样**都按着（两帧之间没有松手帧）。间隔超过
                // SamePressWindowMs 说明这中间根本没采样到（掉帧，或两帧之间玩家已经松过手），
                // 不能再把这次输入当成上一次按下的延续 —— 否则会漏掉真实的第二次点击。
                continuous = nowMs - entry.LastInputMs <=
                    PlayerRespawnRules.SamePressWindowMs;
            }
            entry.LastInputMs = hasTime ? nowMs : 0;
            if (continuous) return false;
            entry.PressReleased = false;
            return true;
        }

        /// <summary>只读：这一刻是否"已松手"（诊断 / 自检用；false = 正按着，下一帧的输入不算新按下）。</summary>
        public bool TryGetPressEdge(PlayerKey key, out bool pressReleased)
        {
            pressReleased = false;
            if (!m_entries.TryGetValue(key, out Entry entry)) return false;
            pressReleased = entry.PressReleased;
            return true;
        }

        /// <summary>
        /// A tap was observed.  Order matters and mirrors the engine: the 1.5 s
        /// guard first, then mode rules, then the one-request-per-death rule.
        ///
        /// 调用方必须先用 <see cref="NoteInput"/> 判"这一帧是不是新按下"，只在返回 true 时调用本方法
        /// —— 直接把 <c>GameWidget.Input.Any</c> 的每一帧结果喂进来会让按住 / 按键自动重复在
        /// 1.5 s 门槛后自动发请求（实测故障：刚死就秒复活）。
        /// </summary>
        public PlayerRespawnGateAction OnTap(PlayerKey key, long nowMs,
            long requestSequence, out string reason)
        {
            reason = null;
            if (!m_entries.TryGetValue(key, out Entry entry))
            {
                reason = "no-death";
                return PlayerRespawnGateAction.IgnoredNoDeath;
            }
            if (entry.Resolved || entry.TimedOut)
            {
                reason = entry.TimedOut ? "request-timeout" : "already-resolved";
                return PlayerRespawnGateAction.SuppressedResolved;
            }
            if (nowMs - entry.DeathAtMs < PlayerRespawnRules.TapGuardMs)
            {
                reason = "pre-guard";
                return PlayerRespawnGateAction.SuppressedEarly;
            }
            if (entry.RetryNotBeforeMs > 0 && nowMs < entry.RetryNotBeforeMs)
            {
                reason = "retry-gap";
                return PlayerRespawnGateAction.SuppressedEarly;
            }
            if (entry.Mode != PlayerRespawnMode.TapToRespawn)
            {
                reason = PlayerRespawnRules.Describe(entry.Mode);
                return PlayerRespawnGateAction.SuppressedMode;
            }
            if (entry.RequestSent)
            {
                reason = "already-sent";
                return PlayerRespawnGateAction.SuppressedAlreadyRequested;
            }
            if (requestSequence <= 0)
            {
                reason = "invalid-sequence";
                return PlayerRespawnGateAction.SuppressedResolved;
            }
            entry.RequestSent = true;
            entry.RequestAtMs = nowMs;
            entry.RequestSequence = requestSequence;
            reason = null;
            return PlayerRespawnGateAction.SendRequest;
        }

        /// <summary>Called when the send itself failed: the death is still unresolved, so allow one later tap.</summary>
        public void OnSendFailed(PlayerKey key)
        {
            if (!m_entries.TryGetValue(key, out Entry entry)) return;
            if (entry.Resolved) return;
            entry.RequestSent = false;
            entry.RequestAtMs = 0;
            entry.RequestSequence = 0;
        }

        /// <summary>Request timeout: explicit counter + no resend (exactly once per death).</summary>
        public PlayerRespawnGateAction OnTick(PlayerKey key, long nowMs, out string reason)
        {
            reason = null;
            if (!m_entries.TryGetValue(key, out Entry entry)) return PlayerRespawnGateAction.None;
            if (!entry.RequestSent || entry.Resolved || entry.TimedOut)
                return PlayerRespawnGateAction.None;
            if (nowMs - entry.RequestAtMs < PlayerRespawnRules.RequestTimeoutMs)
                return PlayerRespawnGateAction.None;
            entry.TimedOut = true;
            reason = "timeout";
            return PlayerRespawnGateAction.Timeout;
        }

        /// <summary>Apply the host verdict.  A stale / mismatched sequence is ignored.</summary>
        public PlayerRespawnGateAction OnResult(PlayerKey key, long requestSequence,
            bool accepted, string reason, out string staleReason)
        {
            staleReason = null;
            if (!m_entries.TryGetValue(key, out Entry entry))
            {
                staleReason = "no-death";
                return PlayerRespawnGateAction.IgnoredNoDeath;
            }
            if (!entry.RequestSent || entry.RequestSequence != requestSequence)
            {
                staleReason = "sequence-mismatch (sent=" +
                    (entry.RequestSent ? entry.RequestSequence : 0) + " got=" +
                    requestSequence + ")";
                return PlayerRespawnGateAction.ResultStale;
            }
            if (entry.Resolved)
            {
                staleReason = "already-resolved";
                return PlayerRespawnGateAction.ResultStale;
            }
            entry.Resolved = true;
            entry.ResultReason = reason ?? string.Empty;
            return accepted
                ? PlayerRespawnGateAction.ResultAccepted
                : PlayerRespawnGateAction.ResultRejected;
        }

        /// <summary>
        /// 主机拒绝之后的处理：**只有可恢复原因**才重新武装请求（<see cref="PlayerRespawnReasons.Cooldown"/>，
        /// 也就是"客户端点得太早，主机还不接受"）。其它原因（not-dead / wrong-owner /
        /// respawn-forbidden / unknown-player / respawn-failed）是终局，保持已解决 ——
        /// 重发不会变成成功，只会刷屏。
        ///
        /// 返回 true = 已重新武装：<paramref name="retryAtMs"/> 之后的下一次点击会再发一条请求，
        /// 且调用方必须显式计数（这是唯一允许"同一死亡期第二次请求"的路径）。
        /// </summary>
        public bool OnRejected(PlayerKey key, long nowMs, out long retryAtMs, out string reason)
        {
            retryAtMs = 0;
            reason = null;
            if (!m_entries.TryGetValue(key, out Entry entry)) return false;
            if (!entry.Resolved) return false;
            if (PlayerRespawnReasons.Key(entry.ResultReason) != PlayerRespawnReasons.Cooldown)
            {
                reason = "不可恢复的拒绝原因：" +
                    PlayerRespawnReasons.Key(entry.ResultReason);
                return false;
            }
            entry.Resolved = false;
            entry.RequestSent = false;
            entry.RequestAtMs = 0;
            entry.RequestSequence = 0;
            entry.TimedOut = false;
            entry.RetryNotBeforeMs = nowMs + PlayerRespawnRules.RetryAfterRejectMs;
            retryAtMs = entry.RetryNotBeforeMs;
            reason = "主机以 cooldown 拒绝 → " + PlayerRespawnRules.RetryAfterRejectMs +
                " ms 后允许再点一次";
            return true;
        }
    }
}

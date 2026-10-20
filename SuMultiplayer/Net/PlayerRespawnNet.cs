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

        /// <summary>Engine <c>DisplayLargeMessage</c> duration / delay (PlayerData.cs:275-300).</summary>
        public const float MessageDurationSeconds = 30f;
        public const float MessageDelaySeconds = 1.5f;

        /// <summary>Exact engine large text.</summary>
        public const string DeathLargeText = "YOU HAVE DIED";

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
        ResultStale = 10
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
        }

        private readonly Dictionary<PlayerKey, Entry> m_entries =
            new Dictionary<PlayerKey, Entry>();

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
        /// A tap was observed.  Order matters and mirrors the engine: the 1.5 s
        /// guard first, then mode rules, then the one-request-per-death rule.
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
    }
}

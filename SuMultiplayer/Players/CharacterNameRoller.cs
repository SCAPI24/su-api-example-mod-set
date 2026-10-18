using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SuMultiplayer.Players
{
    /// <summary>一次默认名字摇号的结果（纯值：来源 / 重名次数 / 是否在上限后接受重名）。</summary>
    public sealed class CharacterNameRollResult
    {
        /// <summary>最终写进 Name 输入框的名字。</summary>
        public string Name = string.Empty;
        /// <summary><see cref="CharacterNameRoller.SourceIdentitySalt"/> 或 <see cref="CharacterNameRoller.SourceEngine"/>。</summary>
        public string Source = CharacterNameRoller.SourceIdentitySalt;
        /// <summary>本次摇号遇到的重名次数（含达到上限后接受的那一次）。</summary>
        public int Collisions;
        /// <summary>true = 重试到上限仍然重名，按需求接受重名（不是静默失败）。</summary>
        public bool AcceptedDuplicate;
        /// <summary>引擎回退 / 空名字表等明确原因；正常身份摇号时为空。</summary>
        public string Reason = string.Empty;
    }

    /// <summary>
    /// 首次加入角色创建屏的**默认名字**摇号（纯逻辑，不引用 Game，自检可直接编译）。
    ///
    /// 为什么不用共享的引擎随机：
    ///   旧实现（以及移植版最初的写法）在创建屏里用 <c>new Engine.Random()</c> 选皮肤、再把皮肤显示名
    ///   当默认名字。两台客户端如果从同一 RNG 状态出发，会摇到同一名字（实机：两个客户端都叫
    ///   "Geoffrey"）。这里改成**按身份稳定键 + 本次会话盐**播种：
    ///     seed = FNV1a32(identity) ⊕ sessionSalt，再按重试次数 attempt 派生下一个种子；
    ///   身份来自 <c>Game.UserManager.ActiveUser.UniqueId</c>（UserId.dat，见 PlayerIdentity），
    ///   不同机器必然不同，所以“每个客户端都摇到同一个名字”不会再发生。
    ///
    /// 名字表**不在这里硬编码**：调用方（创建屏）从游戏自带的
    /// <c>Game.CharacterSkinsManager.CharacterSkinsNames</c>（内置皮肤 <c>$Male1..4 / $Female1..4</c>）
    /// 经 <c>CharacterSkinsManager.GetDisplayName</c> 取显示名，
    /// Source: Survivalcraft/Game/CharacterSkinsManager.cs:58-93（Walter/Basil/Geoffrey/Zachary/Doris/Mabel/Ada/Shirley）
    /// Source: Survivalcraft/Game/PlayerData.cs:347-351（ResetName = 皮肤显示名，引擎自己的生成方式）
    ///
    /// 重名：调用方传入"当前会话里已知的名字"（主机 + 名册里已经出现的人），摇到重名就换一个派生盐再摇；
    /// 最多 <see cref="MaxCollisionRetries"/> 次重试，到上限后**接受重名**并显式记录（需求允许偶发重名）。
    ///
    /// 没有静态可变状态：会话盐 / 计数器 / 限流时间戳都在实例上（组合根建一个，随本次会话存活）。
    /// </summary>
    public sealed class CharacterNameRoller
    {
        /// <summary>身份键 + 会话盐摇号（正常路径）。</summary>
        public const string SourceIdentitySalt = "identity-salt";

        /// <summary>身份不可用 / 名字表为空 → 交给引擎自己的生成方式（显式记录，不静默）。</summary>
        public const string SourceEngine = "engine";

        /// <summary>重名后的最大重试次数（有界；到上限接受重名）。</summary>
        public const int MaxCollisionRetries = 8;

        /// <summary>同一条摇号日志的最小间隔（毫秒）；计数器仍逐次累加。</summary>
        public const long LogIntervalMs = 10000;

        private readonly Action<string> m_log;
        private readonly Action<string> m_logWarn;
        private readonly int m_sessionSalt;
        private long m_lastLogTimestamp;
        private string m_lastLogKey = string.Empty;
        private long m_suppressedLogs;

        /// <param name="log">普通日志（组合根传 [SuMP] 前缀的 Log.Information）。</param>
        /// <param name="logWarn">回退/重名上限等需要显眼记录的日志（组合根传 Log.Warning）。</param>
        /// <param name="sessionSalt">本次会话的盐；见 <see cref="NewSessionSalt"/>。</param>
        public CharacterNameRoller(Action<string> log, Action<string> logWarn, int sessionSalt)
        {
            m_log = log ?? throw new ArgumentNullException(nameof(log));
            m_logWarn = logWarn ?? throw new ArgumentNullException(nameof(logWarn));
            m_sessionSalt = sessionSalt;
        }

        /// <summary>用游戏自己的随机 API 生成一个会话盐（每个 mod 会话一次，不是每帧）。</summary>
        public static int NewSessionSalt() => new global::Engine.Random().Int();

        public int SessionSalt => m_sessionSalt;

        /// <summary>按身份摇号的次数（正常路径）。</summary>
        public long IdentityRolls { get; private set; }

        /// <summary>回退到引擎生成方式的次数（身份不可用 / 名字表为空）。</summary>
        public long EngineRolls { get; private set; }

        /// <summary>累计重名次数（含达到上限后接受的那一次）。</summary>
        public long Collisions { get; private set; }

        /// <summary>最近一次摇号的来源（日志/状态行用）。</summary>
        public string LastSource { get; private set; } = string.Empty;

        /// <summary>最近一次摇出的名字（状态行用；不是 GUID）。</summary>
        public string LastName { get; private set; } = string.Empty;

        /// <summary>被限流抑制的日志行数（计数器不受影响）。</summary>
        public long SuppressedLogs => m_suppressedLogs;

        /// <summary>
        /// 身份稳定键：FNV-1a 32 位（UTF-8 字节）。同一个 UserId 在任意机器 / 任意次启动结果一致，
        /// 不同 UserId 基本不可能相同；日志里只允许出现 <c>PlayerIdentity.Shorten</c> 的短形式。
        /// </summary>
        public static uint StableKey(string identity)
        {
            string text = (identity ?? string.Empty).Trim();
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            uint hash = 2166136261u;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash = unchecked(hash * 16777619u);
            }
            return hash;
        }

        /// <summary>
        /// 第 <paramref name="attempt"/> 次尝试的 32 位种子：
        /// <c>FNV1a(identity) ⊕ sessionSalt</c> 再混入 attempt（每次重试都是不同盐，不是复用同一 RNG 状态）。
        /// </summary>
        public static int SeedFor(uint identityKey, int sessionSalt, int attempt)
        {
            uint mixed = identityKey ^ (uint)sessionSalt;
            mixed = unchecked(mixed * 16777619u);
            mixed ^= unchecked((uint)attempt * 0x9E3779B9u);
            return unchecked((int)mixed);
        }

        /// <summary>
        /// 摇一个默认名字。
        /// </summary>
        /// <param name="identity">身份（UserId.dat）；空 = 不可用 → 引擎生成并**显式**记录原因。</param>
        /// <param name="candidates">游戏自带名字表（创建屏从 CharacterSkinsManager 取）。</param>
        /// <param name="takenNames">当前会话里已知的名字（本机自己的旧名字由调用方排除）。</param>
        /// <param name="engineDefaultName">引擎自己的默认名（当前皮肤的显示名，等价 PlayerData.ResetName）。</param>
        /// <param name="engineFallbackReason">身份不可用 / 名字表为空时的明确原因。</param>
        /// <param name="subject">日志用短身份（PlayerIdentity.Shorten 的结果；空 = 不可用）。</param>
        public CharacterNameRollResult Roll(string identity, IReadOnlyList<string> candidates,
            IReadOnlyCollection<string> takenNames, string engineDefaultName, string engineFallbackReason,
            string subject)
        {
            if (string.IsNullOrWhiteSpace(identity))
            {
                return FallbackToEngine(engineDefaultName, engineFallbackReason,
                    "身份不可用（UserManager.ActiveUser 为空或 UniqueId 为空）");
            }
            if (candidates == null || candidates.Count == 0)
            {
                return FallbackToEngine(engineDefaultName, engineFallbackReason, "内置名字表为空");
            }

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (takenNames != null)
            {
                foreach (string raw in takenNames)
                {
                    if (!string.IsNullOrWhiteSpace(raw)) taken.Add(raw.Trim());
                }
            }

            uint key = StableKey(identity);
            int attempt = 0;
            int collisions = 0;
            bool acceptedDuplicate = false;
            string chosen;
            while (true)
            {
                var random = new global::Engine.Random(SeedFor(key, m_sessionSalt, attempt));
                chosen = (candidates[random.Int(0, candidates.Count - 1)] ?? string.Empty).Trim();
                if (chosen.Length == 0)
                {
                    throw new InvalidOperationException(
                        "默认名字摇号失败：名字表摇到了空项（身份=" + (subject ?? "(unknown)") +
                        "）→ 不拿空名字兜底");
                }
                if (!taken.Contains(chosen)) break;
                collisions++;
                if (attempt >= MaxCollisionRetries)
                {
                    acceptedDuplicate = true;
                    break;
                }
                attempt++;
            }

            IdentityRolls++;
            Collisions += collisions;
            LastSource = SourceIdentitySalt;
            LastName = chosen;

            string message = "默认名字：nameRollSource=" + SourceIdentitySalt +
                " · name=" + chosen +
                " · nameRollCollisions=" + collisions +
                " · 身份=" + NonEmpty(subject, "(unknown)") +
                (acceptedDuplicate
                    ? " · 重试达到上限 " + MaxCollisionRetries + " 次仍重名 → 接受重名（需求允许偶发重名）"
                    : string.Empty);
            LogRateLimited("identity:" + chosen + ":" + collisions + ":" + subject, message,
                acceptedDuplicate);
            return new CharacterNameRollResult
            {
                Name = chosen,
                Source = SourceIdentitySalt,
                Collisions = collisions,
                AcceptedDuplicate = acceptedDuplicate
            };
        }

        /// <summary>计数器状态行：字段名固定为 nameRollSource / nameRollCollisions，实机排查直接读。</summary>
        public string StatusLine() =>
            "nameRollSource=" + (string.IsNullOrEmpty(LastSource) ? "none" : LastSource) +
            " nameRollCollisions=" + Collisions +
            " nameRolls=" + (IdentityRolls + EngineRolls) +
            " nameRollIdentity=" + IdentityRolls +
            " nameRollEngine=" + EngineRolls +
            " nameRollLastName=" + (string.IsNullOrEmpty(LastName) ? "(none)" : LastName) +
            " nameRollLogSuppressed=" + m_suppressedLogs +
            " nameRollSessionSalt=" + m_sessionSalt;

        private CharacterNameRollResult FallbackToEngine(string engineDefaultName, string reason,
            string detail)
        {
            string name = (engineDefaultName ?? string.Empty).Trim();
            string why = string.IsNullOrWhiteSpace(reason) ? detail : reason + "；" + detail;
            if (name.Length == 0)
            {
                throw new InvalidOperationException(
                    "默认名字回退失败：引擎默认名字为空（原因：" + why + "）→ 不拿空名字/占位名兜底");
            }
            EngineRolls++;
            LastSource = SourceEngine;
            LastName = name;
            // 回退必须显眼：用户明确要求"显式记录，不静默"。
            string message = "默认名字回退到引擎生成：nameRollSource=" + SourceEngine +
                " · name=" + name + " · nameRollCollisions=0 · 原因=" + why;
            LogRateLimited("engine:" + name + ":" + why, message, warn: true);
            return new CharacterNameRollResult
            {
                Name = name,
                Source = SourceEngine,
                Collisions = 0,
                AcceptedDuplicate = false,
                Reason = why
            };
        }

        private static string NonEmpty(string value, string fallback) =>
            string.IsNullOrEmpty(value) ? fallback : value;

        private void LogRateLimited(string key, string message, bool warn)
        {
            long nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
            if (string.Equals(key, m_lastLogKey, StringComparison.Ordinal) &&
                nowMs - m_lastLogTimestamp < LogIntervalMs)
            {
                m_suppressedLogs++;
                return;
            }
            m_lastLogKey = key;
            m_lastLogTimestamp = nowMs;
            if (warn) m_logWarn(message);
            else m_log(message);
        }
    }
}

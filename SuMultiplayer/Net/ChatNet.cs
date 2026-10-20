using System;
using System.Collections.Generic;
using System.Text;
using Google.Protobuf;
using SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 聊天文本策略：**唯一**的净化 / 长度判定入口（主机与客户端共用，tsverify 直接编译）。
    ///
    /// 规则（Source: Mod/ScMultiplayer/Message/ChatMessage.cs:ChatMessage 的语义）：
    ///   · 长度按 **Unicode 标量值**计（CJK / emoji 都算 1 个），上限 200；
    ///     不做 UTF-16 截断，绝不切坏代理对（实机会把 emoji 切成 "�"）。
    ///   · 控制字符（C0/C1、换行、制表符）与 Unicode Format 字符（RTL override /
    ///     零宽字符 / BOM）一律去掉；换行 / 制表按空白处理，折叠成一个空格。
    ///   · 首尾空白去掉；净化后为空 → 明确拒绝（empty），绝不发一条空消息。
    ///   · **不转义、不拼 UI 标记**：聊天 UI 用 LabelWidget.Text 原样绘制，
    ///     文本里的 &lt; &gt; &amp; 就是普通字符，不会被当成标记解析；这里只去掉不可见字符。
    ///   · 超过 200 个标量值 → 明确拒绝（too_long），**不静默截断**（截断会让对端收到
    ///     一条用户没写过的消息）。
    /// </summary>
    public static class ChatTextPolicy
    {
        /// <summary>消息正文上限（Unicode 标量值）。任务要求 ~200。</summary>
        public const int MaxTextLength = 200;

        /// <summary>发送者显示名上限（与聊天 UI 的宽度 / 防伪造一致）。</summary>
        public const int MaxSenderNameLength = 32;

        /// <summary>身份串（UserId.dat）上限：只用于显示 / 诊断，不参与绑定。</summary>
        public const int MaxIdentityLength = 128;

        /// <summary>拒绝原因 Detail 的上限（写进 ChatRejected.detail）。</summary>
        public const int MaxDetailLength = 120;

        /// <summary>
        /// 净化并校验一条消息正文。失败时 <paramref name="reason"/> 是明确的拒绝原因
        /// （empty / too_long），调用方必须计数并回 ChatRejected，不得当成成功。
        /// </summary>
        public static bool TrySanitize(string raw, out string text, out ChatRejectReason reason)
        {
            text = string.Empty;
            reason = ChatRejectReason.ChatRejectUnspecified;
            if (raw == null)
            {
                reason = ChatRejectReason.ChatRejectEmpty;
                return false;
            }

            var builder = new StringBuilder(Math.Min(raw.Length, MaxTextLength * 2) + 1);
            int scalars = 0;
            bool hasContent = false;
            bool pendingSpace = false;

            foreach (Rune rune in raw.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune))
                {
                    if (hasContent) pendingSpace = true;
                    continue;
                }
                if (Rune.IsControl(rune) || IsFormatRune(rune))
                {
                    // 控制 / 格式字符直接去掉（不进正文，也不当空白）。
                    continue;
                }
                if (pendingSpace)
                {
                    if (scalars >= MaxTextLength)
                    {
                        reason = ChatRejectReason.ChatRejectTooLong;
                        return false;
                    }
                    builder.Append(' ');
                    scalars++;
                    pendingSpace = false;
                }
                if (scalars >= MaxTextLength)
                {
                    reason = ChatRejectReason.ChatRejectTooLong;
                    return false;
                }
                builder.Append(rune.ToString());
                scalars++;
                hasContent = true;
            }

            if (!hasContent)
            {
                reason = ChatRejectReason.ChatRejectEmpty;
                return false;
            }
            text = builder.ToString();
            return true;
        }

        /// <summary>
        /// 净化显示名（主机从 ClientJoined 记录 / Hello 提示里拿到的名字）。
        /// 控制字符 / 格式字符去掉、空白折叠、最多 32 个标量值；净化后为空 → 明确失败
        /// （调用方用 "Player" 并计数，绝不把空名字画到聊天 UI 上）。
        /// </summary>
        public static bool TrySanitizeName(string raw, out string name)
        {
            name = string.Empty;
            if (raw == null) return false;
            var builder = new StringBuilder(Math.Min(raw.Length, MaxSenderNameLength) + 1);
            int scalars = 0;
            bool hasContent = false;
            bool pendingSpace = false;
            foreach (Rune rune in raw.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune))
                {
                    if (hasContent) pendingSpace = true;
                    continue;
                }
                if (Rune.IsControl(rune) || IsFormatRune(rune)) continue;
                if (pendingSpace)
                {
                    if (scalars >= MaxSenderNameLength) break;
                    builder.Append(' ');
                    scalars++;
                    pendingSpace = false;
                }
                if (scalars >= MaxSenderNameLength) break;
                builder.Append(rune.ToString());
                scalars++;
                hasContent = true;
            }
            if (!hasContent) return false;
            name = builder.ToString();
            return true;
        }

        /// <summary>
        /// 净化身份串：去掉控制 / 格式字符，按 UTF-16 截到 128 个字符（身份只用于显示与
        /// 诊断，不参与任何绑定；截断不会改变绑定）。空串是合法结果（旧客户端没有身份）。
        /// </summary>
        public static string SanitizeIdentity(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var builder = new StringBuilder(Math.Min(raw.Length, MaxIdentityLength));
            int units = 0;
            foreach (Rune rune in raw.EnumerateRunes())
            {
                if (Rune.IsControl(rune) || IsFormatRune(rune) || Rune.IsWhiteSpace(rune)) continue;
                string piece = rune.ToString();
                if (units + piece.Length > MaxIdentityLength) break;
                builder.Append(piece);
                units += piece.Length;
            }
            return builder.ToString();
        }

        /// <summary>detail 字段的净化：只保留可打印字符，最长 120。</summary>
        public static string SanitizeDetail(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var builder = new StringBuilder(Math.Min(raw.Length, MaxDetailLength));
            int scalars = 0;
            foreach (Rune rune in raw.EnumerateRunes())
            {
                if (Rune.IsControl(rune) || IsFormatRune(rune)) continue;
                if (scalars >= MaxDetailLength) break;
                builder.Append(rune.ToString());
                scalars++;
            }
            return builder.ToString();
        }

        /// <summary>统计 Unicode 标量值个数（不做截断，只用于长度判定 / 测试）。</summary>
        public static int CountScalars(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            foreach (Rune unused in text.EnumerateRunes()) count++;
            return count;
        }

        private static bool IsFormatRune(Rune rune) =>
            Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format;
    }

    /// <summary>
    /// 自动化聊天文本注入策略（纯逻辑，不引用 Game / UI，tsverify 直接编译）。
    ///
    /// 作用：把外部（CmdBridge）给的一段文本**追加**到当前聊天输入缓冲区，
    /// 并保证它和手打消息走**同一套净化规则 + 同一组上限**：
    ///   · 净化复用 <see cref="ChatTextPolicy.TrySanitize"/>（控制 / Format 字符去掉、
    ///     空白折叠、首尾去空白、净化后为空 → 明确拒绝），不另写一套"差不多"的规则；
    ///   · 缓冲区上限 = <see cref="MaxBufferUtf16"/> 个 UTF-16 码元（与
    ///     <c>ChatTextBoxWidget.MaximumLength</c> 同值），整条消息上限 = 200 个
    ///     Unicode 标量值（<see cref="ChatTextPolicy.MaxTextLength"/>，线上协议口径）。
    ///     任一超出 → 明确拒绝（too_long），**不静默截断**：截断会让对端收到一条
    ///     自动化没有写过的消息。
    ///   · 追加是**字面拼接**，不自动插入空格；需要分隔符时由调用方把空格写进文本
    ///     （缓冲区原有末尾空白会被净化保留为一个空格）。
    /// </summary>
    public static class ChatInjectionPolicy
    {
        /// <summary>输入控件缓冲区上限（UTF-16 码元，等于 ChatTextBoxWidget.MaximumLength）。</summary>
        public const int MaxBufferUtf16 = ChatTextPolicy.MaxTextLength;

        /// <summary>
        /// 把 <paramref name="raw"/> 净化后追加到 <paramref name="existing"/> 末尾。
        /// 失败时 <paramref name="error"/> 带稳定前缀：<c>empty_text:</c> / <c>too_long:</c>。
        /// </summary>
        public static bool TryCompose(string existing, string raw, out string text, out string error)
        {
            text = null;
            error = null;

            // 先单独校验"这次要注入的文本本身"：全空白 / 只有控制字符不能被当成"注入成功"。
            if (!ChatTextPolicy.TrySanitize(raw, out string fragment, out ChatRejectReason reason))
            {
                error = (reason == ChatRejectReason.ChatRejectTooLong ? "too_long: " : "empty_text: ")
                    + ChatRejectText.Describe(reason);
                return false;
            }

            // 再按"缓冲区原文 + 本次文本"整体净化一次：这样原有末尾空白会与后续内容
            // 正确折叠成一个空格，而不是出现 "hello  world" 或丢掉分隔。
            string combinedRaw = (existing ?? string.Empty) + (raw ?? string.Empty);
            if (!ChatTextPolicy.TrySanitize(combinedRaw, out string combined,
                    out ChatRejectReason combinedReason))
            {
                error = (combinedReason == ChatRejectReason.ChatRejectTooLong
                        ? "too_long: " : "empty_text: ")
                    + ChatRejectText.Describe(combinedReason);
                return false;
            }

            if (ChatTextPolicy.CountScalars(combined) > ChatTextPolicy.MaxTextLength)
            {
                error = "too_long: combined message exceeds " + ChatTextPolicy.MaxTextLength
                    + " Unicode scalars";
                return false;
            }
            if (combined.Length > MaxBufferUtf16)
            {
                error = "too_long: combined text is " + combined.Length + " UTF-16 code units, "
                    + "above the chat input buffer limit " + MaxBufferUtf16
                    + " (a non-BMP character costs 2 units)";
                return false;
            }

            text = combined;
            return true;
        }
    }

    /// <summary>
    /// 每客户端聊天限速（主机侧唯一执行点）。
    ///   · <see cref="WindowMs"/> 内最多 <see cref="MaxPerWindow"/> 条 → flood；
    ///   · 相邻两条至少间隔 <see cref="MinIntervalMs"/> → rate_limited。
    /// 两个规则都返回明确的 <see cref="ChatRejectReason"/> + retry_after_ms，不静默丢弃。
    /// </summary>
    public sealed class ChatRateLimitPolicy
    {
        public const int WindowMs = 10000;
        public const int MaxPerWindow = 5;
        public const int MinIntervalMs = 750;
    }

    /// <summary>
    /// 有界滑动窗口限速器（每个 clientId 一条状态；主机自己的 clientId=0 也走同一个策略）。
    /// 无静态状态；客户端断开 / 会话结束时由模块 <see cref="Remove"/> / <see cref="Reset"/>。
    /// </summary>
    public sealed class ChatRateLimiter
    {
        private sealed class State
        {
            public readonly Queue<double> Accepted = new Queue<double>();
            public double LastAccepted = double.NegativeInfinity;
        }

        private readonly Dictionary<int, State> m_states = new Dictionary<int, State>();

        public bool TryAccept(int clientId, double nowSeconds, out ChatRejectReason reason,
            out int retryAfterMs)
        {
            reason = ChatRejectReason.ChatRejectUnspecified;
            retryAfterMs = 0;
            if (!m_states.TryGetValue(clientId, out State state))
            {
                state = new State();
                m_states[clientId] = state;
            }
            double nowMs = nowSeconds * 1000.0;
            double windowStart = nowMs - ChatRateLimitPolicy.WindowMs;
            while (state.Accepted.Count > 0 && state.Accepted.Peek() < windowStart)
                state.Accepted.Dequeue();

            if (state.Accepted.Count >= ChatRateLimitPolicy.MaxPerWindow)
            {
                reason = ChatRejectReason.ChatRejectFlood;
                double oldest = state.Accepted.Peek();
                retryAfterMs = (int)Math.Ceiling(Math.Max(0.0,
                    oldest + ChatRateLimitPolicy.WindowMs - nowMs));
                return false;
            }
            double sinceLast = nowMs - state.LastAccepted;
            if (sinceLast < ChatRateLimitPolicy.MinIntervalMs)
            {
                reason = ChatRejectReason.ChatRejectRateLimited;
                retryAfterMs = (int)Math.Ceiling(
                    ChatRateLimitPolicy.MinIntervalMs - sinceLast);
                return false;
            }
            state.Accepted.Enqueue(nowMs);
            state.LastAccepted = nowMs;
            return true;
        }

        public void Remove(int clientId) => m_states.Remove(clientId);

        public void Reset() => m_states.Clear();
    }

    /// <summary>
    /// 一条已经通过主机校验、可以显示的聊天记录。UI 只读它。
    /// 时间用接收方的真实秒（模块传入），因此淡出不需要依赖墙钟。
    /// </summary>
    public sealed class ChatFeedEntry
    {
        public long MessageId;
        public int SenderClientId;
        public string SenderIdentity = string.Empty;
        public string SenderName = string.Empty;
        public string Text = string.Empty;
        public long ServerTick;
        public ChatChannel Channel;
        public long RequestId;
        public double ReceivedAt;
        /// <summary>本机发出去的（主机 echo 回来 / 主机自己发的）。UI 可选高亮，不改变颜色到蓝色。</summary>
        public bool IsSelf;

        public double Age(double nowSeconds) => nowSeconds - ReceivedAt;

        /// <summary>
        /// 显示透明度：0..7 秒全不透明，7..8 秒淡出到 0（任务要求的 ~8 s 淡出）。
        /// 到 0 后由 <see cref="ChatFeed.Advance"/> 移除。
        /// </summary>
        public float Alpha(double nowSeconds)
        {
            double age = Age(nowSeconds);
            if (age <= ChatFeed.FadeStartSeconds) return 1f;
            if (age >= ChatFeed.LifetimeSeconds) return 0f;
            double span = ChatFeed.LifetimeSeconds - ChatFeed.FadeStartSeconds;
            return (float)Math.Max(0.0, Math.Min(1.0,
                (ChatFeed.LifetimeSeconds - age) / span));
        }
    }

    /// <summary>
    /// 底部消息流（最近 8 条，超出丢最旧；8 秒后淡出移除）。**纯内存、不落盘**：
    /// 重连不会重放历史（模块在连接变化时直接 <see cref="Clear"/>）。
    /// </summary>
    public sealed class ChatFeed
    {
        public const int MaxEntries = 8;
        public const double LifetimeSeconds = 8.0;
        /// <summary>淡出起点：7 秒开始淡，8 秒完全消失。</summary>
        public const double FadeStartSeconds = 7.0;

        private readonly List<ChatFeedEntry> m_entries = new List<ChatFeedEntry>(MaxEntries);

        public IReadOnlyList<ChatFeedEntry> Entries => m_entries;

        public int Count => m_entries.Count;

        /// <summary>
        /// 追加一条。返回被挤掉的最旧条目数（上限 8）。重复 message_id **不在这里**去重：
        /// 去重属于模块的协议判定（要计数 duplicate），Feed 只负责可见窗口。
        /// </summary>
        public int Append(ChatFeedEntry entry, double nowSeconds)
        {
            if (entry == null) return 0;
            entry.ReceivedAt = nowSeconds;
            m_entries.Add(entry);
            int removed = 0;
            while (m_entries.Count > MaxEntries)
            {
                m_entries.RemoveAt(0);
                removed++;
            }
            return removed;
        }

        /// <summary>每帧调用：移除已经淡出到 0 的条目。返回移除条数。</summary>
        public int Advance(double nowSeconds)
        {
            int removed = 0;
            for (int i = m_entries.Count - 1; i >= 0; i--)
            {
                if (m_entries[i].Age(nowSeconds) >= LifetimeSeconds)
                {
                    m_entries.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        public bool ContainsMessageId(long messageId)
        {
            if (messageId <= 0) return false;
            for (int i = 0; i < m_entries.Count; i++)
                if (m_entries[i].MessageId == messageId) return true;
            return false;
        }

        public void Clear() => m_entries.Clear();
    }

    /// <summary>
    /// 聊天消息的线上编解码 / 语义校验。
    /// protobuf 解析本身由 <c>MessageRegistry</c> 完成（非法字节在那里计入
    /// MalformedMessages 并整帧丢弃）；本类负责**字段级**校验与显式失败原因。
    /// 任何返回 false 的路径都必须带可写日志的原因。
    /// </summary>
    public static class ChatCodec
    {
        public static bool TryValidateRequest(ChatSendRequest message, out string error)
        {
            error = null;
            if (message == null)
            {
                error = "消息为空";
                return false;
            }
            if (message.RequestId <= 0)
            {
                error = "request_id 必须为正数（本次 " + message.RequestId + "）";
                return false;
            }
            if (message.Channel != ChatChannel.All &&
                message.Channel != ChatChannel.Nearby)
            {
                error = "channel 非法（本次 " + (int)message.Channel + "）";
                return false;
            }
            if (message.Text == null)
            {
                error = "text 为 null";
                return false;
            }
            return true;
        }

        public static bool TryValidateMessage(ChatMessage message, out string error)
        {
            error = null;
            if (message == null)
            {
                error = "消息为空";
                return false;
            }
            if (message.MessageId <= 0)
            {
                error = "message_id 必须为正数（本次 " + message.MessageId + "）";
                return false;
            }
            if (message.SenderClientId < 0)
            {
                error = "sender_client_id 非法（本次 " + message.SenderClientId + "）";
                return false;
            }
            if (message.ServerTick < 0)
            {
                error = "server_tick 非法（本次 " + message.ServerTick + "）";
                return false;
            }
            if (message.Channel != ChatChannel.All &&
                message.Channel != ChatChannel.Nearby)
            {
                error = "channel 非法（本次 " + (int)message.Channel + "）";
                return false;
            }
            if (string.IsNullOrEmpty(message.Text))
            {
                error = "text 为空（空消息不允许广播）";
                return false;
            }
            if (ChatTextPolicy.CountScalars(message.Text) > ChatTextPolicy.MaxTextLength)
            {
                error = "text 超过 " + ChatTextPolicy.MaxTextLength + " 个标量值";
                return false;
            }
            if (string.IsNullOrEmpty(message.SenderName))
            {
                error = "sender_name 为空（UI 必须能显示是谁说的）";
                return false;
            }
            if (ChatTextPolicy.CountScalars(message.SenderName) >
                ChatTextPolicy.MaxSenderNameLength)
            {
                error = "sender_name 超过 " + ChatTextPolicy.MaxSenderNameLength + " 个标量值";
                return false;
            }
            if (message.SenderIdentity != null &&
                message.SenderIdentity.Length > ChatTextPolicy.MaxIdentityLength * 2)
            {
                error = "sender_identity 过长";
                return false;
            }
            return true;
        }

        public static bool TryValidateRejected(ChatRejected message, out string error)
        {
            error = null;
            if (message == null)
            {
                error = "消息为空";
                return false;
            }
            if (message.RequestId <= 0)
            {
                error = "request_id 必须为正数（本次 " + message.RequestId + "）";
                return false;
            }
            if (message.Reason == ChatRejectReason.ChatRejectUnspecified)
            {
                error = "拒绝原因为 Unspecified（无法解释为什么失败）";
                return false;
            }
            return true;
        }

        public static byte[] EncodeRequest(ChatSendRequest message) =>
            (message ?? throw new ArgumentNullException(nameof(message))).ToByteArray();

        public static byte[] EncodeMessage(ChatMessage message) =>
            (message ?? throw new ArgumentNullException(nameof(message))).ToByteArray();

        public static byte[] EncodeRejected(ChatRejected message) =>
            (message ?? throw new ArgumentNullException(nameof(message))).ToByteArray();
    }

    /// <summary>拒绝原因的稳定字符串键（计数 / 日志 / UI 显示共用；不随枚举改名而变）。</summary>
    public static class ChatRejectText
    {
        public static string Key(ChatRejectReason reason)
        {
            switch (reason)
            {
                case ChatRejectReason.ChatRejectEmpty: return "empty";
                case ChatRejectReason.ChatRejectTooLong: return "too_long";
                case ChatRejectReason.ChatRejectRateLimited: return "rate_limited";
                case ChatRejectReason.ChatRejectFlood: return "flood";
                case ChatRejectReason.ChatRejectUnsupportedChannel: return "unsupported_channel";
                case ChatRejectReason.ChatRejectNotInRoom: return "not_in_room";
                case ChatRejectReason.ChatRejectMalformed: return "malformed";
                case ChatRejectReason.ChatRejectNotPlayer: return "not_player";
                default: return "unspecified";
            }
        }

        /// <summary>面向玩家的短原因（中文；UI 用暖色系统行显示，不使用蓝色）。</summary>
        public static string Describe(ChatRejectReason reason)
        {
            switch (reason)
            {
                case ChatRejectReason.ChatRejectEmpty: return "消息为空或只有空白";
                case ChatRejectReason.ChatRejectTooLong:
                    return "消息超过 " + ChatTextPolicy.MaxTextLength + " 字";
                case ChatRejectReason.ChatRejectRateLimited: return "发送过快，请稍后再试";
                case ChatRejectReason.ChatRejectFlood:
                    return "刷屏限制：" + (ChatRateLimitPolicy.WindowMs / 1000) + " 秒内最多 " +
                        ChatRateLimitPolicy.MaxPerWindow + " 条";
                case ChatRejectReason.ChatRejectUnsupportedChannel: return "暂不支持该频道（目前只有全体）";
                case ChatRejectReason.ChatRejectNotInRoom: return "当前不在联机会话里";
                case ChatRejectReason.ChatRejectMalformed: return "消息格式非法";
                case ChatRejectReason.ChatRejectNotPlayer: return "不是玩家端，不能发言";
                default: return "消息被拒绝（未知原因）";
            }
        }
    }
}

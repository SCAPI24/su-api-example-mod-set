using Engine;
using Game;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// **聊天观察（只读）**：把"玩家可见的小提示"里属于聊天的那些结构化出来。
    ///
    /// 为什么不直接读 ScMultiplayer 的聊天历史：那要跨 Mod 反射它的实例字段
    /// （`m_recentChatMessages`），而**根本没有必要** —— ScMP 发聊天时走的就是引擎自己的
    /// 显示路径（`ScMultiplayerUpdateLoop.DisplayChatMessage`）：
    ///
    ///     componentPlayer.ComponentGui.DisplaySmallMessage(sender + ": " + text, …)
    ///
    /// 而"读小提示"这件事本 Mod 早就有了一份实现（<see cref="MessageObserver"/>）。
    /// 于是聊天观察 = **在小提示里认出哪几条是聊天**，一行 ScMP 代码都不用改，
    /// 也不新增任何写入口（观察面铁律）。
    ///
    /// 两条落地纪律：
    ///   1. **按"已知名字 + `: `"切分**，不能按第一个冒号切 —— 玩家名里允许有空格
    ///      （实测本机就是 `Android User`），按冒号切会把 `Android` 当发送者、`User: 过来` 当正文。
    ///   2. **去重靠"在场/离场"而不是计时**：同一条消息在屏幕上停留 4~6 秒，每帧都读得到；
    ///      只有"这一帧还在、上一帧不在"才算新的一条。这样"同样的话隔一会儿再说一次"
    ///      仍然是新消息（计时去重会把它吃掉）。
    /// </summary>
    internal static class ChatObserver
    {
        /// <summary>一条聊天行。`Seq` 单调递增，供增量消费（上层记住上次消费到哪）。</summary>
        public sealed class Line
        {
            public long Seq;
            public string Sender;
            public string Text;
            public bool IsSelf;
            public bool IsSystem;
            public double SeenAt;
        }

        /// <summary>保留多少条最近聊天（够上层事后回看，又不至于无限涨）。</summary>
        private const int MaximumRecentLines = 64;

        /// <summary>
        /// 会被当成"发送者"的系统名。玩家名之外的这些前缀也算聊天（否则整条会被当成
        /// 游戏自己的提示，混进 `obs.messages` 但没人能识别）。
        /// </summary>
        private static readonly string[] s_systemSenders = { "ScMP", "Host", "Server" };

        private static readonly List<Line> s_recent = new List<Line>();
        private static readonly HashSet<string> s_active = new HashSet<string>(StringComparer.Ordinal);
        private static long s_seq;
        private static string s_localName;

        /// <summary>本端玩家名（跨 Mod 门面要用它判"这是不是我自己"）。</summary>
        public static string LocalName
        {
            get
            {
                CollectPlayerNames();
                return s_localName;
            }
        }

        /// <summary>最近一条聊天的序号（上层用它判断"有没有新的"）。</summary>
        public static long Sequence
        {
            get { return s_seq; }
        }

        /// <summary>给 `obs.chat` 与跨 Mod 门面用的快照（只读）。</summary>
        public static Dictionary<string, object> Describe(int maxLines = 16)
        {
            Scan();
            var lines = new List<Dictionary<string, object>>();
            int start = Math.Max(0, s_recent.Count - Math.Max(1, maxLines));
            for (int i = start; i < s_recent.Count; i++)
                lines.Add(ToDictionary(s_recent[i]));

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["seq"] = s_seq,
                ["localName"] = s_localName,
                // 能拿来匹配发送者的名字表（本端 + 远端 + 系统名）。排查"聊天里的人名对不上角色"
                // 时这是**唯一**能一眼看清的东西：远端玩家在本端的 `PlayerData.Name` 未必
                // 等于它在聊天里报的名字（资料同步与聊天名是两条路）。
                ["players"] = CollectPlayerNames(),
                // **两张名单都报出来**（只读排查用）：`ComponentPlayers` 只含本机操控的角色，
                // `PlayersData` 才是全部角色（联机时远端在这里）。聊天指令"走过去"要按名字/
                // 最近关系找发话者，而这两张名单的差别正是那条链最容易断的地方 ——
                // 一次把两边都打出来，比在 AI 日志里猜快得多。
                ["roster"] = DescribeRoster(),
                ["lines"] = lines,
                ["recent"] = s_recent.Count
            };
        }

        /// <summary>只要 `seq &gt; sinceSeq` 的那些（增量消费）。</summary>
        public static List<Dictionary<string, object>> DescribeSince(long sinceSeq)
        {
            Scan();
            var lines = new List<Dictionary<string, object>>();
            for (int i = 0; i < s_recent.Count; i++)
                if (s_recent[i].Seq > sinceSeq)
                    lines.Add(ToDictionary(s_recent[i]));
            return lines;
        }

        private static Dictionary<string, object> ToDictionary(Line line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["seq"] = line.Seq,
                ["sender"] = line.Sender,
                ["text"] = line.Text,
                ["isSelf"] = line.IsSelf,
                ["isSystem"] = line.IsSystem
            };
        }

        /// <summary>
        /// 清空"最近聊天"与去重表（换世界/重连、自检收尾）。
        ///
        /// ⚠️ **`seq` 故意不归零**：消费方记的是"我读到哪了"，把 seq 倒回去会让它
        /// **以为再也收不到新消息**（`seq &gt; lastSeq` 永远不成立）——
        /// 自检里动过一次静态状态，游戏里就再也听不见聊天，那是最难查的一类 bug。
        /// </summary>
        public static void Reset()
        {
            s_recent.Clear();
            s_active.Clear();
            s_localName = null;
        }

        /// <summary>读一遍当前可见的小提示，认出聊天行并维护 seq/去重（游戏线程调用）。</summary>
        public static void Scan()
        {
            // 离开世界 → 清空：跨世界的旧聊天不该在新世界里触发动作
            // （也让"消费方从 seq=0 开始"不会读到上一个世界的遗留）。
            if (GameManager.Project == null)
            {
                if (s_recent.Count > 0 || s_active.Count > 0 || s_seq != 0)
                    Reset();
                return;
            }

            List<string> visible;
            try
            {
                visible = MessageObserver.CollectSmallMessages();
            }
            catch (Exception)
            {
                return;
            }
            ScanTexts(visible, CollectPlayerNames());
        }

        /// <summary>
        /// 核心：给"这一帧屏幕上有什么" + "已知名字"，认出聊天行、按时序入队、按在场/离场去重。
        /// 抽出来是为了让自检能直接喂合成输入（不依赖游戏里真的有聊天）。
        /// </summary>
        internal static void ScanTexts(List<string> visible, List<string> names)
        {
            var nowVisible = new HashSet<string>(StringComparer.Ordinal);
            if (visible != null)
            {
                for (int i = 0; i < visible.Count; i++)
                {
                    string sender, text;
                    bool isSystem;
                    if (!TrySplit(visible[i], names, out sender, out text, out isSystem))
                        continue;   // 不是聊天（游戏自己的提示），跳过

                    string key = sender + "\u0001" + text;
                    nowVisible.Add(key);
                    if (s_active.Contains(key))
                        continue;   // 上一帧就在屏幕上 → 不是新消息

                    var line = new Line
                    {
                        Seq = ++s_seq,
                        Sender = sender,
                        Text = text,
                        IsSystem = isSystem,
                        IsSelf = !isSystem && sender != null && s_localName != null
                            && string.Equals(sender, s_localName, StringComparison.Ordinal),
                        SeenAt = Time.RealTime
                    };
                    s_recent.Add(line);
                    while (s_recent.Count > MaximumRecentLines)
                        s_recent.RemoveAt(0);
                }
            }

            // 去重口径 = "这一帧还在不在"：离场的键下一帧再出现就是新消息
            s_active.Clear();
            foreach (string key in nowVisible)
                s_active.Add(key);
        }

        /// <summary>
        /// 认出聊天行：`"&lt;名字&gt;: &lt;正文&gt;"`。名字取"已知玩家名 + 系统名"的并集，
        /// **最长优先**（名字互为前缀时不至于切错）。
        /// </summary>
        internal static bool TrySplit(string message, List<string> names,
            out string sender, out string text, out bool isSystem)
        {
            sender = null;
            text = null;
            isSystem = false;
            if (string.IsNullOrEmpty(message))
                return false;

            string best = null;
            if (names != null)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    string candidate = names[i];
                    if (string.IsNullOrEmpty(candidate))
                        continue;
                    if (message.Length <= candidate.Length + 2)
                        continue;
                    if (!message.StartsWith(candidate, StringComparison.Ordinal))
                        continue;
                    if (message[candidate.Length] != ':' || message[candidate.Length + 1] != ' ')
                        continue;
                    if (best == null || candidate.Length > best.Length)
                        best = candidate;
                }
            }

            if (best != null)
            {
                sender = best;
                text = message.Substring(best.Length + 2);
                isSystem = IsSystemName(best);
                return !string.IsNullOrWhiteSpace(text);
            }

            // 名字不认识（对端还没生成角色、或名字里有特殊字符）：
            // **仍然按"第一处 `: `"尝试** —— "能不能听懂"只取决于正文，
            // 而"能不能走过去"匹配不到实体时会退回"最近的另一个玩家"。
            int colon = message.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
                return false;
            sender = message.Substring(0, colon);
            text = message.Substring(colon + 2);
            isSystem = IsSystemName(sender);
            return !string.IsNullOrWhiteSpace(text);
        }

        private static bool IsSystemName(string name)
        {
            for (int i = 0; i < s_systemSenders.Length; i++)
                if (string.Equals(name, s_systemSenders[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>场上的玩家名（本端 + 远端）。远端名字来自 `PlayerData.Name`（联机时由资料同步）。</summary>
        internal static List<string> CollectPlayerNames()
        {
            var names = new List<string>();
            try
            {
                SubsystemPlayers players = GameManager.Project != null
                    ? GameManager.Project.FindSubsystem<SubsystemPlayers>(false)
                    : null;
                if (players != null)
                {
                    // 本端名字：`ComponentPlayers` 只是**本机**操控的角色（联机时远端不在里面）
                    if (players.ComponentPlayers.Count > 0)
                    {
                        ComponentPlayer local = players.ComponentPlayers[0];
                        string localName = local != null && local.PlayerData != null
                            ? local.PlayerData.Name : null;
                        if (!string.IsNullOrEmpty(localName))
                        {
                            s_localName = localName;
                            if (!names.Contains(localName))
                                names.Add(localName);
                        }
                    }
                    // 用来匹配"谁说的"的名单：`PlayersData`（**全部**角色，含远端）
                    for (int i = 0; i < players.PlayersData.Count; i++)
                    {
                        PlayerData data = players.PlayersData[i];
                        string name = null;
                        try { name = data != null ? data.Name : null; }
                        catch (Exception) { name = null; }
                        if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                            names.Add(name);
                    }
                }
            }
            catch (Exception)
            {
                // 拿不到玩家名不算错：系统名仍然能识别
            }
            for (int i = 0; i < s_systemSenders.Length; i++)
                if (!names.Contains(s_systemSenders[i]))
                    names.Add(s_systemSenders[i]);
            return names;
        }

        /// <summary>
        /// 玩家名册快照（只读）：`ComponentPlayers`（本机操控的角色）与 `PlayersData`（全部角色）
        /// 两张表逐条列出 —— 名字、PlayerIndex、有没有生成实体、实体坐标。
        /// </summary>
        internal static List<Dictionary<string, object>> DescribeRoster()
        {
            var roster = new List<Dictionary<string, object>>();
            try
            {
                SubsystemPlayers players = GameManager.Project != null
                    ? GameManager.Project.FindSubsystem<SubsystemPlayers>(false)
                    : null;
                if (players == null)
                {
                    roster.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["source"] = "none", ["note"] = "no SubsystemPlayers (world not loaded?)"
                    });
                    return roster;
                }

                for (int i = 0; i < players.PlayersData.Count; i++)
                    roster.Add(DescribeEntry("PlayersData", i, players.PlayersData[i], null));
                for (int i = 0; i < players.ComponentPlayers.Count; i++)
                {
                    ComponentPlayer component = players.ComponentPlayers[i];
                    PlayerData data = null;
                    try { data = component != null ? component.PlayerData : null; }
                    catch (Exception) { data = null; }
                    roster.Add(DescribeEntry("ComponentPlayers", i, data, component));
                }
            }
            catch (Exception exception)
            {
                roster.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["source"] = "error",
                    ["note"] = exception.GetType().Name + ": " + exception.Message
                });
            }
            return roster;
        }

        private static Dictionary<string, object> DescribeEntry(string source, int index,
            PlayerData data, ComponentPlayer component)
        {
            var entry = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["source"] = source,
                ["i"] = index
            };
            try { entry["name"] = data != null ? data.Name : null; }
            catch (Exception exception) { entry["name"] = "<error " + exception.GetType().Name + ">"; }
            try { entry["playerIndex"] = data != null ? (int?)data.PlayerIndex : null; }
            catch (Exception) { entry["playerIndex"] = null; }
            if (component == null && data != null)
            {
                try { component = data.ComponentPlayer; }
                catch (Exception) { component = null; }
            }
            entry["hasEntity"] = component != null;
            if (component != null && component.ComponentBody != null)
            {
                Vector3 position = component.ComponentBody.Position;
                entry["x"] = Math.Round(position.X, 1);
                entry["y"] = Math.Round(position.Y, 1);
                entry["z"] = Math.Round(position.Z, 1);
            }
            return entry;
        }

        // ------------------------------------------------------------------ 自检钩子

        /// <summary>
        /// **测试/调试用**：凭空塞一条聊天行（`dev.chat.inject`）。
        ///
        /// 为什么需要它：真实验收要**另一个人**在聊天里打字，而"AI 能不能听懂并走过来"
        /// 这件事必须能反复、确定地验证（调短语表时尤其如此）。它只写观察层的队列，
        /// **不碰游戏状态、不发送任何网络消息** —— 与 `ai.laya.ask` 同一类"仅调试"入口。
        ///
        /// `sender` 会被当成**真实发送者**：`ChatWatch` 会拿它去场上找同名的玩家
        /// （找不到就退回"最近的另一个玩家"），所以用对端玩家的名字注入能走完整条链。
        /// </summary>
        public static Line InjectLine(string sender, string text, bool isSelf = false,
            bool isSystem = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var line = new Line
            {
                Seq = ++s_seq,
                Sender = string.IsNullOrEmpty(sender) ? "Tester" : sender,
                Text = text.Trim(),
                IsSelf = isSelf,
                IsSystem = isSystem,
                SeenAt = Time.RealTime
            };
            s_recent.Add(line);
            while (s_recent.Count > MaximumRecentLines)
                s_recent.RemoveAt(0);
            return line;
        }

        internal static bool TrySplitForTest(string message, string[] names,
            out string sender, out string text, out bool isSystem)
        {
            return TrySplit(message, new List<string>(names), out sender, out text, out isSystem);
        }

        internal static void SetLocalNameForTest(string name)
        {
            s_localName = name;
        }

        /// <summary>自检用：喂一帧"屏幕上有什么"。</summary>
        internal static void FeedForTest(string[] messages, string[] names)
        {
            ScanTexts(new List<string>(messages), new List<string>(names));
        }

        internal static int RecentCountForTest()
        {
            return s_recent.Count;
        }

        internal static Line RecentForTest(int indexFromEnd)
        {
            int index = s_recent.Count - 1 - indexFromEnd;
            return index >= 0 && index < s_recent.Count ? s_recent[index] : null;
        }
    }
}

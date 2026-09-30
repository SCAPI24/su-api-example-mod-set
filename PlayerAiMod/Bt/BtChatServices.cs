using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// **聊天监听服务**：把"有人在聊天里说话"变成树里的确定性格局。
    ///
    /// 数据流（三段各自只做一件事）：
    ///
    ///     CmdBridge 的 ChatObserver（只读）        →  seq + {sender, text, isSelf, isSystem}
    ///        ↓  DescribeChat(sinceSeq)
    ///     本服务：增量消费 → 短语表匹配 → 写黑板     →  chat.intent / chat.target / chat.digest
    ///        ↓  Blackboard 装饰器
    ///     树：come → 走过去 / follow → 跟着 / stop → 站住
    ///
    /// 三条设计纪律：
    ///   ① **首帧只记水位**（`m_lastSeq = 当前 seq`），不消费历史 —— 否则换树/重载后
    ///      会把十分钟前那句"过来"当成新指令再执行一次；
    ///   ② **只认 `isSelf=false` 的非系统消息**：AI 自己发的、以及 `ScMP:` 那类系统通知
    ///      （领地否决之类）都不是指令；
    ///   ③ **未命中短语表时不留空**：把原话写进 `chat.digest` 并把 `chat.bank` 置成兜底问题库，
    ///      让树去问一次模型（`Task.LayaAsk digestKey=chat.digest`）—— 摘要**只带那句原话**，
    ///      绝不动 `world_goal` 的摘要（§12.1：往摘要里塞无关 token 会把答案带偏）。
    /// </summary>
    public sealed class BtChatWatchService : BtService
    {
        /// <summary>黑板键前缀（默认 `chat.`）。</summary>
        public string Prefix { get; set; } = "chat.";

        /// <summary>发话者解析出的目标（<see cref="AiActorView"/>）写进哪个键。</summary>
        public string TargetKey { get; set; } = "chat.target";

        /// <summary>短语表没命中时，是否留一个"去问模型"的标记（`chat.bank`）。</summary>
        public bool UseFallback { get; set; } = true;

        /// <summary>兜底问题库名（`PlayerAi/Questions/` 下）。</summary>
        public string FallbackBank { get; set; } = "chat_intent";

        /// <summary>只认这个玩家名说的话（大小写不敏感）；空 = 谁说的都认。</summary>
        public string VoiceName { get; set; }

        /// <summary>发话者解析不到实体时是否清掉目标键（默认清）。</summary>
        public bool ClearWhenMissing { get; set; } = true;

        /// <summary>上次消费到的 seq（`-1` = 还没同步过水位）。</summary>
        public long LastSeq { get; private set; } = -1;

        public string LastIntent { get; private set; }
        public string LastSender { get; private set; }
        public string LastText { get; private set; }
        public string LastError { get; private set; }
        public long Consumed { get; private set; }

        public BtChatWatchService()
        {
            // 聊天窗口很短（小提示停留 4~6 秒），但没必要每帧走一遍控件树：
            // 0.2 秒足够及时，也不会给游戏线程添压力（`BtService` 自带限流）。
            Interval = 0.2f;
        }

        public override string NodeType
        {
            get { return "Service.ChatWatch"; }
        }

        protected override void OnTick(BtContext context)
        {
            AiBlackboard board = context.Blackboard;
            if (board == null)
                return;

            CmdBridgeMod.CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
            if (facade == null)
            {
                LastError = "CmdBridge facade is not available";
                return;
            }

            Dictionary<string, object> snapshot;
            try
            {
                snapshot = facade.DescribeChat(LastSeq > 0 ? LastSeq : 0, 8);
            }
            catch (Exception exception)
            {
                LastError = exception.GetType().Name + ": " + exception.Message;
                return;
            }
            if (snapshot == null)
                return;

            long seq = ReadLong(snapshot, "seq");
            if (LastSeq < 0)
            {
                // 首帧：只记水位（不消费历史指令）
                LastSeq = seq;
                board.Set(new AiBlackboardKey<int>(Prefix + "seq"), (int)seq);
                return;
            }
            LastError = null;

            var lines = snapshot.ContainsKey("lines")
                ? snapshot["lines"] as List<Dictionary<string, object>> : null;
            if (lines == null)
                return;

            ChatPhrases table = ChatPhraseCatalog.Current;
            for (int i = 0; i < lines.Count; i++)
            {
                Dictionary<string, object> line = lines[i];
                if (line == null)
                    continue;
                long lineSeq = ReadLong(line, "seq");
                if (lineSeq <= LastSeq)
                    continue;

                // 消费到这一条（无论要不要动作，水位都必须前进，否则同一条会被反复处理）
                LastSeq = lineSeq;
                Consumed++;

                if (ReadBool(line, "isSelf") || ReadBool(line, "isSystem"))
                    continue;
                string sender = ReadString(line, "sender");
                string text = ReadString(line, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (!string.IsNullOrEmpty(VoiceName)
                    && !string.Equals(sender, VoiceName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (table != null && table.IsIgnoredSender(sender))
                    continue;

                ChatMatch match = ChatPhrases.MatchEx(table, text);
                string intent = match != null ? match.Intent : null;
                LastSender = sender;
                LastText = text;
                LastIntent = intent;

                board.Set(new AiBlackboardKey<int>(Prefix + "seq"), (int)lineSeq);
                board.Set(new AiBlackboardKey<string>(Prefix + "sender"), sender ?? string.Empty);
                board.Set(new AiBlackboardKey<string>(Prefix + "text"), text);
                // 带参意图（目前只有 `hunt`）的参数：树里的"按名找生物"读它。
                // 没命中带参意图时**显式清空**，否则上一条"打牛"的物种词会粘到下一条指令上。
                board.Set(new AiBlackboardKey<string>(Prefix + "arg"),
                    match != null && match.HasArgument ? match.Argument : string.Empty);
                // **"有一条待处理指令"** 这一个布尔就是树的门：树处理完把它清掉
                // （与 `chat.intent` / `chat.bank` / `chat.digest` 一起消费）。
                // 单独立一个键而不是让树去判 `intent != ""`：兜底路径下 intent 是**空的**
                // （要先去问模型），"空"不能同时表示"没事"和"待问"。
                board.Set(new AiBlackboardKey<bool>(Prefix + "pending"), true);

                // 目标先把解析出来：`come` 要用，兜底判成 come 时也要用
                ResolveTarget(context, sender, ReadString(snapshot, "localName"));

                if (!string.IsNullOrEmpty(intent))
                {
                    board.Set(new AiBlackboardKey<string>(Prefix + "intent"), intent);
                    board.Set(new AiBlackboardKey<string>(Prefix + "bank"), string.Empty);
                    board.Set(new AiBlackboardKey<string>(Prefix + "digest"), string.Empty);
                }
                else if (UseFallback)
                {
                    // 短语表没命中 → 让树拿**这句话本身**去问一次模型。
                    // 摘要里只放这句原话（`chat=<原话>`）：它短、且正是模型要判的东西。
                    board.Set(new AiBlackboardKey<string>(Prefix + "intent"), string.Empty);
                    board.Set(new AiBlackboardKey<string>(Prefix + "bank"),
                        string.IsNullOrEmpty(FallbackBank) ? string.Empty : FallbackBank);
                    board.Set(new AiBlackboardKey<string>(Prefix + "digest"),
                        "chat=" + Shorten(text, 40));
                }
                else
                {
                    board.Set(new AiBlackboardKey<string>(Prefix + "intent"), string.Empty);
                    board.Set(new AiBlackboardKey<string>(Prefix + "bank"), string.Empty);
                    board.Set(new AiBlackboardKey<string>(Prefix + "digest"), string.Empty);
                }
            }
        }

        /// <summary>
        /// 发话者 → 实体：先按名字找，找不到退回"最近的其它玩家"
        /// （对端角色还没生成、名字带特殊字符时会走这条）。
        ///
        /// 为什么不用 `Service.UpdateNearestPlayer` 那个 `player` 键：那个服务每 0.25 秒
        /// 就会把它改成"最近的人"，多人同场时会把"谁叫我"覆盖掉；聊天目标必须自己占一个键。
        /// </summary>
        private void ResolveTarget(BtContext context, string sender, string localName)
        {
            // 玩家查找在**基础传感器**上（`IAiSensor.TryFindPlayer/TryFindNearestPlayer`），
            // 生物/掉落物那些扩展在 `IAiWorldSensor` 上 —— 这两个接口不要混用。
            IAiSensor sensors = context.Sensors;
            AiBlackboard board = context.Blackboard;
            if (sensors == null || board == null || !sensors.IsReady)
                return;

            AiActorView view = default(AiActorView);
            bool found = false;
            if (!string.IsNullOrEmpty(sender))
            {
                try { found = sensors.TryFindPlayer(sender, out view); }
                catch (Exception) { found = false; }
            }

            // **按名字找到"自己"就改走"最近的另一个玩家"**（2026-09-26 实测）：
            // 联机时双方显示名**可能一模一样**（本端与远端都叫 "Basil"），于是
            // "按名字找发话者"必然有歧义；而"最近的另一个玩家"是确定性的。
            if (found && LooksLikeSelf(view, localName))
                found = false;

            // 名字没匹配上（或匹配到自己）→ **无条件退回"最近的另一个玩家"**。
            // 这一步不能省：聊天里的显示名来自对端资料，而本端对远端角色的
            // `PlayerData.Name` 未必就是那个名字（实测两边都叫 "Basil"）——
            // 名字匹配失败是**常态**，确定性兜底才是主路径。
            if (!found)
            {
                try { found = sensors.TryFindNearestPlayer(out view); }
                catch (Exception) { found = false; }
            }

            // 兜底也拿到自己 → 当作"没有目标"（宁可不动，也不要对着自己原地"到达"）
            if (found && LooksLikeSelf(view, localName))
                found = false;

            if (!found)
            {
                context.Log("ChatWatch: no target for sender='" + (sender ?? "?") + "' (local='"
                    + (localName ?? "?") + "') -> come/follow will be skipped");
                if (ClearWhenMissing)
                    BtUpdateNearestCreatureService.Clear(board, TargetKey);
                return;
            }

            context.Log("ChatWatch: sender='" + (sender ?? "?") + "' -> target name='"
                + (view.Name ?? "<null>") + "' index=" + view.PlayerIndex
                + " distance=" + view.Distance.ToString("0.##")
                + " selfFlag=" + view.IsSelf);

            board.Set(new AiBlackboardKey<AiActorView>(TargetKey), view);
            board.Set(new AiBlackboardKey<float>(TargetKey + "Distance"), view.Distance);
            board.Set(new AiBlackboardKey<string>(TargetKey + "Name"), view.Name);
            board.Set(new AiBlackboardKey<bool>(TargetKey + "IsSet"), true);
        }

        /// <summary>
        /// "这个 view 是不是我自己"。**不看名字**（联机时双方显示名可能相同，名字不足以区分）；
        /// 只看两件确定的事：传感器给的 <see cref="AiActorView.IsSelf"/> 标记，
        /// 以及**位置重合**（距离 ≈ 0 只可能是自己）。
        /// </summary>
        private static bool LooksLikeSelf(AiActorView view, string localName)
        {
            if (view.IsSelf)
                return true;
            if (view.Distance < 0.75f && !string.IsNullOrEmpty(view.Name)
                && !string.IsNullOrEmpty(localName)
                && string.Equals(view.Name, localName, StringComparison.OrdinalIgnoreCase))
            {
                return true;   // 名字相同 **且** 贴在一起 → 只可能是自己
            }
            if (view.Distance < 0.35f)
                return true;   // 名字不可信时的硬兜底：几乎完全重合
            return false;
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            string trimmed = text.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max);
        }

        private static long ReadLong(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                return 0L;
            if (raw is long) return (long)raw;
            if (raw is int) return (int)raw;
            if (raw is double) return (long)(double)raw;
            if (raw is float) return (long)(float)raw;
            long parsed;
            return long.TryParse(Convert.ToString(raw), out parsed) ? parsed : 0L;
        }

        private static bool ReadBool(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw))
                return false;
            return raw is bool && (bool)raw;
        }

        private static string ReadString(Dictionary<string, object> source, string key)
        {
            object raw;
            return source != null && source.TryGetValue(key, out raw) ? raw as string : null;
        }
    }
}

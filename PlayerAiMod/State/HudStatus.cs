using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// HUD 那一行状态要显示的**事实**（plan G19）。由游戏侧填，纯数据。
    /// </summary>
    public sealed class HudStatusFacts
    {
        /// <summary>Mod 是否启用/宿主是否存在。false = 整行隐藏（不显示"AI off"占位）。</summary>
        public bool Visible = true;

        public bool Paused;
        public string PauseReason;

        /// <summary>`front` / `loading` / `world`。</summary>
        public string Phase;

        /// <summary>相位闸门生效中（过渡态：树一拍都不走，这是**正常状态**不是卡死）。</summary>
        public bool GateActive;

        /// <summary>`inactive` / `observe` / `control`。</summary>
        public string Mode;

        public string TreeId;

        /// <summary>树是否正在跑（`loop=false` 的树跑完会 false）。</summary>
        public bool TreeRunning;

        /// <summary>已完成轮数（循环树里这是"跑到第几轮"）。</summary>
        public int Loops;

        /// <summary>活动节点路径的**末段**（`go_mine`）—— 比整条路径短得多，HUD 放得下。</summary>
        public string Node;

        /// <summary>正在跑的动作脚本名。</summary>
        public string Action;

        public double ActionSeconds;

        /// <summary>在途的 Laya 询问（"思考中"）。</summary>
        public bool LayaBusy;

        public double LayaSeconds;

        public int LayaQuestions;

        /// <summary>连败计数（动作连续失败几次）。</summary>
        public int FailCount;

        /// <summary>最近一次错误（树或动作的），可以是整句话，渲染时会截断。</summary>
        public string Error;
    }

    /// <summary>
    /// 把 HUD 事实编译成**一行**状态文本（plan G19：玩家侧可见性）。
    ///
    /// 为什么需要它：共控/接管时人看不出"AI 在等 Laya / 卡住 / 已放弃"，
    /// 只会觉得"AI 发呆" —— 而"发呆"和"坏了"在玩家眼里是同一件事。
    ///
    /// 三条纪律（与摘要同一套）：
    ///   · **一行、无空格**：HUD 是一行文本，值里出现空格会被误读成两项；
    ///   · **顺序即优先级**：预算不够时从后往前丢，末尾标 `+N` —— "被裁了"这件事要看得见；
    ///   · **只写 ASCII 关键字**：HUD 用的是游戏自带的 `Pericles18` 位图字体（拉丁），
    ///     中文要另外的字体资源；为了"装了就有、不依赖 TranslationMod"，
    ///     标签一律英文（`paused` / `act=` / `ask=`），值里的中文会被消毒掉。
    /// </summary>
    public static class HudStatusLine
    {
        /// <summary>
        /// 一行最多几个字符。HUD 是给人扫一眼的，太长就不看了。
        /// 96 是量出来的：世界里最完整的一行（相位+模式+树#轮数+节点+动作@秒+失败数）约 95 字符 ——
        /// 而**"正在跑什么动作"必须放得下**，它正是"AI 在干嘛"这个问题的主语。
        /// </summary>
        public const int MaxChars = 96;

        /// <summary>错误信息在行里最多留几个字符（它可能是一整句话）。</summary>
        public const int MaxErrorChars = 22;

        public static string Compile(HudStatusFacts facts)
        {
            if (facts == null || !facts.Visible)
                return null;

            var items = new List<string>();

            // `ai=` 放在第一位：这一行到底是什么状态，一眼就要看到。
            items.Add("ai=" + State(facts));
            items.Add("phase=" + Token(facts.Phase, 12, "?"));
            if (!string.IsNullOrEmpty(facts.Mode))
                items.Add("mode=" + Token(facts.Mode, 12, null));
            if (!string.IsNullOrEmpty(facts.TreeId))
            {
                string tree = Token(facts.TreeId, 28, null);
                items.Add(facts.Loops > 0
                    ? "tree=" + tree + "#" + facts.Loops.ToString(CultureInfo.InvariantCulture)
                    : "tree=" + tree);
            }
            // 顺序即优先级：`act=`（正在跑什么动作）**排在 `node=` 前面** ——
            // "AI 在干嘛"的主语是动作，树的当前节点只是补充；反过来会出现
            // "节点名很长 → 把动作挤掉"这种最坏的裁剪（本轮用例抓到的）。
            if (!string.IsNullOrEmpty(facts.Action))
            {
                string action = Token(facts.Action, 24, null);
                items.Add(facts.ActionSeconds > 0.05
                    ? "act=" + action + "@" + Seconds(facts.ActionSeconds)
                    : "act=" + action);
            }
            if (!string.IsNullOrEmpty(facts.Node) && !IsAbsent(facts.Node))
                items.Add("node=" + Token(facts.Node, 20, null));
            if (facts.LayaBusy)
            {
                items.Add("ask=" + (facts.LayaQuestions > 0
                    ? facts.LayaQuestions.ToString(CultureInfo.InvariantCulture) + "q@"
                    : string.Empty) + Seconds(facts.LayaSeconds));
            }
            if (facts.FailCount > 0)
                items.Add("fail=" + facts.FailCount.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(facts.Error))
                items.Add("err=" + Token(facts.Error, MaxErrorChars, null));

            return Join(items);
        }

        /// <summary>
        /// 任何**自由文本**值的唯一出口：消毒 + 截断 + 空值回退。
        ///
        /// 为什么每个值都要过这里（哪怕它"看起来"很安全）：HUD 的编码是"空格分隔的 `key=value`"，
        /// 只要有一个值里带空格，它就会被读成两项 —— 本轮实机就踩到了
        /// （活动节点路径 `Root#root &gt; Selector#sel` 整条进来，撑出一堆假 token，
        /// 还把真正的 `act=` 挤出了预算）。**在编码这一层兜住**，
        /// 比指望每个调用方都记得消毒可靠。
        /// </summary>
        /// <summary>
        /// "这个值其实表示没有"的哨兵。路径/字段缺失时上游会给 `<none>` 之类的占位，
        /// 它**不是**一个值 —— 显示出来只会让人以为"有个节点叫 none"。
        /// </summary>
        private static bool IsAbsent(string value)
        {
            if (string.IsNullOrEmpty(value))
                return true;
            string trimmed = value.Trim();
            return trimmed == "<none>" || trimmed == "none" || trimmed == "-";
        }

        private static string Token(string value, int maxChars, string fallback)
        {
            string cleaned = UiListDigest.CleanToken(value, maxChars);
            return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
        }

        /// <summary>
        /// 一个词说清"现在是什么状态"。**顺序有意为之**：
        /// 暂停/闸门比"在不在跑"更能解释"为什么不动"，所以排在前面。
        /// </summary>
        private static string State(HudStatusFacts facts)
        {
            if (facts.Paused)
            {
                string reason = UiListDigest.CleanToken(facts.PauseReason, 12);
                return string.IsNullOrEmpty(reason) ? "paused" : "paused:" + reason;
            }
            if (facts.GateActive)
                return "gated";
            if (!facts.TreeRunning)
                return "idle";
            return facts.LayaBusy ? "thinking" : "run";
        }

        /// <summary>秒数：小于 10 秒留一位小数，再大就取整（HUD 上不需要更高精度）。</summary>
        private static string Seconds(double value)
        {
            if (value < 0)
                value = 0;
            return value < 10
                ? value.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                : ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture) + "s";
        }

        private static string Join(List<string> items)
        {
            var text = new StringBuilder();
            var kept = new List<string>();
            int dropped = 0;
            bool full = false;
            for (int i = 0; i < items.Count; i++)
            {
                string item = items[i];
                if (item == null)
                    continue;

                // **一旦装不下，后面的一律不要** —— 顺序就是优先级。
                // 反例（本轮用例抓到的）：只丢"当前装不下的那一项"，会变成
                // "长的 `act=` 被丢掉、短的 `fail=2` 反而留下" —— 人看到的是一个更次要的数字，
                // 而"正在跑什么动作"消失了，那恰好是最该看到的一项。
                if (full)
                {
                    dropped++;
                    continue;
                }

                int projected = 0;
                for (int k = 0; k < kept.Count; k++)
                    projected += kept[k].Length + 1;
                projected += item.Length;
                if (projected > MaxChars && kept.Count > 0)
                {
                    full = true;
                    dropped++;
                    continue;
                }
                kept.Add(item);
            }

            for (int i = 0; i < kept.Count; i++)
            {
                if (i > 0)
                    text.Append(' ');
                text.Append(kept[i]);
            }
            if (dropped > 0)
                text.Append(" +").Append(dropped.ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }
}

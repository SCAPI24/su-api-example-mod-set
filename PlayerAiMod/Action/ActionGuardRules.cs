using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayerAiMod
{
    /// <summary>
    /// 一个语义 UI 目标现在的**事实**（守卫判定的输入）。由游戏侧填（`CmdBridgeInput.QueryUiElement`），
    /// 这里只是纯数据 —— 于是判定规则能在没有游戏的临时工程里逐条自检。
    /// </summary>
    public sealed class UiElementFacts
    {
        /// <summary>这次查询本身成功了没有（false = 判不了，守卫**必须**判不通过）。</summary>
        public bool Known;

        public bool Present;
        public bool Hittable;
        public bool Clickable;

        /// <summary>人话原因（不存在 / 点不到 / 为什么判不了）。</summary>
        public string Reason;

        public static UiElementFacts Unknown(string reason)
        {
            return new UiElementFacts { Known = false, Reason = reason };
        }

        public static UiElementFacts Absent(string reason)
        {
            return new UiElementFacts { Known = true, Present = false, Reason = reason };
        }

        public static UiElementFacts Found(bool hittable, bool clickable, string reason)
        {
            return new UiElementFacts
            {
                Known = true,
                Present = true,
                Hittable = hittable,
                Clickable = clickable,
                Reason = reason
            };
        }
    }

    /// <summary>
    /// 动作脚本 / 行为树**守卫的判定规则**（plan §3.2）：纯逻辑，不认识游戏类型。
    ///
    /// 纪律（与 `obs.waitFor` 同一套词汇，plan 反复强调"不新造条件语言"）：
    ///   · **判不了 ≠ 通过**：`element.*` 查询失败（不在游戏线程 / UI 没就绪）时返回 false + 原因，
    ///     绝不"默默放行"——守卫的全部价值就在于"不满足就别动手"；
    ///   · **能判的都在这里，判不了的要说得清**：`handled=false` 由调用方报 `target_unavailable`，
    ///     不认识的写法不能被当成"通过"；
    ///   · `screen.is:X` 与实际屏幕名**精确比较**（大小写不敏感）。屏幕名来自 `UiInspector.ScreenName()`
    ///     （`MainMenu` / `Play` / `Game` …），失败时把**实际屏幕名**写进原因，省得人去猜。
    /// </summary>
    public static class ActionGuardRules
    {
        public const string ScreenPrefix = "screen.is:";
        public const string ModalPrefix = "modal.is:";
        public const string ElementPresentPrefix = "element.present:";
        public const string ElementHittablePrefix = "element.hittable:";
        public const string ElementClickablePrefix = "element.clickable:";

        /// <summary>
        /// `events.since:&lt;序号&gt;` —— 事件环里有没有**新**事件。
        ///
        /// 为什么事后才补：CmdBridge 的 `obs.waitFor` 一直支持这条，而动作守卫这边当初没接，
        /// 于是同一套"条件语言"在两边**不一样**（plan P2 的验收口径写明"同词汇同判定"）。
        /// 语义两边完全一致：`事件环最新序号 &gt; 给的序号` 即成立
        /// （`waitFor` 是"等到成立"，守卫是"现在成立吗"）。
        /// </summary>
        public const string EventsSincePrefix = "events.since:";

        /// <summary>
        /// `aim.block` / `aim.entity` / `aim.none` —— 准星此刻指着什么（词汇与摘要里的 `aim=` 同源）。
        ///
        /// 为什么需要它们（第 41 轮的结论）：实测那个模型会对"什么都没瞄"的状态答 `mine`，
        /// 所以**动作层必须自己把住可行性** —— "该不该做"由 C# 判，"做哪一件"才交给 Laya。
        /// 有了 `aim.block`，`mine_*` 这类脚本在"没瞄方块"时**直接拒绝执行**，
        /// 而不是把一次注定失败的动作跑进连败阶梯。
        /// </summary>
        public const string AimBlock = "aim.block";
        public const string AimEntity = "aim.entity";
        public const string AimNone = "aim.none";

        /// <summary>本类真的会判的那几种（`ai.action.script.validate` 与帮助文案都读它）。</summary>
        public static readonly string[] Wired =
        {
            ActionGuards.Alive, ActionGuards.Dead, ActionGuards.WorldLoaded, ActionGuards.WorldUnloaded,
            ActionGuards.NoModal, ActionGuards.NoDialog, ActionGuards.Awake, ActionGuards.Sleeping,
            AimBlock, AimEntity, AimNone,
            ModalPrefix + "<面板名>", ScreenPrefix + "<屏幕名>",
            ElementPresentPrefix + "<目标>", ElementHittablePrefix + "<目标>", ElementClickablePrefix + "<目标>",
            EventsSincePrefix + "<序号>"
        };

        /// <summary>
        /// 判定一条守卫。返回 **false = 这条守卫不认识**（调用方按"未接线"报错，不许当通过）。
        /// 认识的话 `result` 就是判定结果，`error` 是不通过的原因（人话）。
        /// </summary>
        public static bool TryEvaluate(string guard, Dictionary<string, object> context,
            Func<string, UiElementFacts> probeElement, out bool result, out string error)
        {
            result = false;
            error = null;

            string text = (guard ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                result = true;
                return true;
            }

            // ---- 无参数的固定词汇 ----
            if (string.Equals(text, ActionGuards.WorldLoaded, StringComparison.OrdinalIgnoreCase))
            {
                result = Bool(context, "worldLoaded");
                if (!result)
                    error = "no world is loaded";
                return true;
            }
            if (string.Equals(text, ActionGuards.WorldUnloaded, StringComparison.OrdinalIgnoreCase))
            {
                result = !Bool(context, "worldLoaded");
                if (!result)
                    error = "a world is loaded";
                return true;
            }
            if (string.Equals(text, ActionGuards.Alive, StringComparison.OrdinalIgnoreCase))
            {
                if (!Bool(context, "hasPlayer"))
                {
                    error = "no player is available";
                    return true;
                }
                result = Bool(context, "playerAlive");
                if (!result)
                    error = "the player is dead";
                return true;
            }
            if (string.Equals(text, ActionGuards.Dead, StringComparison.OrdinalIgnoreCase))
            {
                if (!Bool(context, "hasPlayer"))
                {
                    error = "no player is available";
                    return true;
                }
                result = !Bool(context, "playerAlive");
                if (!result)
                    error = "the player is alive";
                return true;
            }
            if (string.Equals(text, ActionGuards.NoModal, StringComparison.OrdinalIgnoreCase))
            {
                result = !Bool(context, "modalOpen");
                if (!result)
                    error = "a modal panel is open: " + Text(context, "modalPanel");
                return true;
            }
            if (string.Equals(text, ActionGuards.NoDialog, StringComparison.OrdinalIgnoreCase))
            {
                result = !Bool(context, "dialogsOpen");
                if (!result)
                    error = "a dialog is open";
                return true;
            }
            if (string.Equals(text, ActionGuards.Awake, StringComparison.OrdinalIgnoreCase))
            {
                result = !Bool(context, "sleeping");
                if (!result)
                    error = "the player is sleeping";
                return true;
            }
            if (string.Equals(text, ActionGuards.Sleeping, StringComparison.OrdinalIgnoreCase))
            {
                result = Bool(context, "sleeping");
                if (!result)
                    error = "the player is awake";
                return true;
            }

            // ---- 带参数的：模态面板 / 屏幕 / 元素 ----
            string expected;
            if (TryArgument(text, ModalPrefix, out expected))
            {
                string actual = Text(context, "modalPanel");
                result = actual != null && actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!result)
                    error = "the open modal panel is '" + (actual ?? "<none>") + "', not '" + expected + "'";
                return true;
            }

            if (TryArgument(text, ScreenPrefix, out expected))
            {
                string actual = Text(context, "screen");
                result = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                if (!result)
                {
                    error = "the current screen is '" + (actual ?? "<unknown>") + "', not '"
                        + expected + "' (screen names come from the game: MainMenu / Play / Game …)";
                }
                return true;
            }

            if (TryArgument(text, ElementPresentPrefix, out expected)
                || TryArgument(text, ElementHittablePrefix, out expected)
                || TryArgument(text, ElementClickablePrefix, out expected))
            {
                // 用哪一条前缀判断"要不要查可点" —— 三者共用一次查询（游戏侧一次解析三件事）
                bool wantHittable = text.StartsWith(ElementHittablePrefix, StringComparison.OrdinalIgnoreCase);
                bool wantClickable = text.StartsWith(ElementClickablePrefix, StringComparison.OrdinalIgnoreCase);

                UiElementFacts facts = probeElement != null ? probeElement(expected) : null;
                if (facts == null || !facts.Known)
                {
                    // **判不了就判不通过**（守卫不许默默放行）
                    error = "cannot tell whether '" + expected + "' is there right now"
                        + (facts != null && !string.IsNullOrEmpty(facts.Reason) ? ": " + facts.Reason : string.Empty);
                    return true;
                }

                if (!facts.Present)
                {
                    result = false;
                    error = "no element matches '" + expected + "' on the current screen"
                        + (string.IsNullOrEmpty(facts.Reason) ? string.Empty : ": " + facts.Reason);
                    return true;
                }

                if (wantClickable && !facts.Clickable)
                {
                    result = false;
                    error = "'" + expected + "' is there but not clickable right now"
                        + (string.IsNullOrEmpty(facts.Reason) ? string.Empty : ": " + facts.Reason);
                    return true;
                }
                if (wantHittable && !facts.Hittable)
                {
                    result = false;
                    error = "'" + expected + "' is there but nothing hits at its centre right now"
                        + (string.IsNullOrEmpty(facts.Reason) ? string.Empty : ": " + facts.Reason);
                    return true;
                }

                result = true;
                return true;
            }

            // ---- `aim.*`：准星此刻指着什么（动作层的可行性闸门）
            if (string.Equals(text, AimBlock, StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, AimEntity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, AimNone, StringComparison.OrdinalIgnoreCase))
            {
                string kind = Text(context, "aimKind");
                if (kind == null)
                {
                    // 拿不到就**判不了**（不当成通过）——与 `element.*` 同一条纪律。
                    error = "cannot tell what the crosshair is on right now";
                    return true;
                }
                string wanted = text.Trim().Substring(text.Trim().IndexOf('.') + 1).ToLowerInvariant();
                string actual = kind.Trim().ToLowerInvariant();
                if (actual.Length == 0)
                    actual = "none";
                result = string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase);
                if (!result)
                    error = "the crosshair is on '" + actual + "', not '" + wanted + "'";
                return true;
            }
            // ---- `events.since:<序号>`：与 `obs.waitFor` **同一个判定、同一个词汇**
            //
            // 语义：事件环的**最新序号 > 给的序号**就算成立。`waitFor` 是"等到它成立"，
            // 守卫是"现在成立吗" —— 同一条谓词，两个用法。
            if (text.StartsWith(EventsSincePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string raw = text.Substring(EventsSincePrefix.Length).Trim();
                long wanted;
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out wanted))
                {
                    // 写错了不是"判不了"，是这条守卫**本身非法**；照样判不通过并把写法说清楚。
                    error = "'" + text + "' needs a numeric sequence (events.since:<序号>)";
                    return true;
                }

                long? current = Number(context, "eventSeq");
                if (!current.HasValue)
                {
                    // 拿不到序号（门面不可用 / 事件环还没建）：**判不了就判不通过**，
                    // 与 `element.*` 的"查询失败绝不判通过"是同一条纪律。
                    error = "cannot tell whether a new event has arrived yet (no event sequence is available)";
                    return true;
                }

                result = current.Value > wanted;
                if (!result)
                {
                    error = "no new event since #" + wanted.ToString(CultureInfo.InvariantCulture)
                        + " (the ring is at #" + current.Value.ToString(CultureInfo.InvariantCulture) + ")";
                }
                return true;
            }

            return false;   // 不认识：调用方报 target_unavailable
        }

        /// <summary>认不认识这条守卫（校验器与编辑器用它给"写错了"的场景提前报错）。</summary>
        public static bool IsWired(string guard)
        {
            bool result;
            string error;
            return TryEvaluate(guard, null, null, out result, out error);
        }

        public static string DescribeWired()
        {
            return string.Join(" | ", Wired);
        }

        // ---------------------------------------------------------------- 内部

        private static bool TryArgument(string text, string prefix, out string value)
        {
            value = null;
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            value = text.Substring(prefix.Length).Trim();
            return value.Length > 0;
        }

        private static bool Bool(Dictionary<string, object> context, string key)
        {
            object raw;
            if (context == null || !context.TryGetValue(key, out raw) || raw == null)
                return false;
            if (raw is bool)
                return (bool)raw;
            string text = raw as string;
            return text != null && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static string Text(Dictionary<string, object> context, string key)
        {
            object raw;
            if (context == null || !context.TryGetValue(key, out raw) || raw == null)
                return null;
            return raw as string ?? Convert.ToString(raw);
        }

        /// <summary>
        /// 读一个整数（长期/包装类型都认）。**拿不到返回 null** —— 调用方必须区分
        /// "值是 0" 与 "读不到"，否则 `events.since:0` 在拿不到序号时会被判成"成立"。
        /// </summary>
        private static long? Number(Dictionary<string, object> context, string key)
        {
            object raw;
            if (context == null || !context.TryGetValue(key, out raw) || raw == null)
                return null;
            if (raw is long)
                return (long)raw;
            if (raw is int)
                return (int)raw;
            if (raw is float)
                return (long)(float)raw;
            if (raw is double)
                return (long)(double)raw;
            long parsed;
            string text = raw as string;
            return text != null && long.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed) ? parsed : (long?)null;
        }
    }
}

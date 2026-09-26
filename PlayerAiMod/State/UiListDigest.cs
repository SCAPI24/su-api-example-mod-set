using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 一行列表项的**事实**（列表候选的输入）。由游戏侧填（`UiInspector.DescribeActiveList`），
    /// 这里只是纯数据 —— 于是渲染规则能在没有游戏的临时工程里逐条自检。
    /// </summary>
    public sealed class UiListRow
    {
        /// <summary>列表内的**绝对**序号（0 起）—— 它就是 `list:WorldsList#N` 里的 N。</summary>
        public int Index;

        public string Text;

        /// <summary>是不是当前选中行（`ListPanelWidget.SelectedIndex`）。</summary>
        public bool Selected;

        /// <summary>几何上落在这个面板的可视区里（滚动出去的行为 false）。</summary>
        public bool Visible;
    }

    /// <summary>
    /// 屏幕上"当前这个列表"的快照（G16：Laya 的视觉盲区）。
    ///
    /// 为什么需要它：世界外选世界/翻页这类决策，**答案就是"第几行"**，
    /// 而列表行是 `ListPanelWidget` **自绘**的（条目不是控件），模型看不见内容就只能在
    /// "第 1/2/3 行"里瞎猜 —— 摘要必须把候选行结构化地喂给它。
    /// </summary>
    public sealed class UiListSnapshot
    {
        /// <summary>面板名（`WorldsList`）—— 与 `list:<面板>#<行>` 选择器同名。</summary>
        public string Panel;

        /// <summary>列表总行数（不只是可见的那几行）。</summary>
        public int ItemsCount;

        /// <summary>当前选中行；-1 = 没有选中（列表还没填/不支持选中）。</summary>
        public int SelectedIndex = -1;

        public bool CanScrollUp;
        public bool CanScrollDown;

        /// <summary>
        /// 屏幕上**同时可见**的列表个数。&gt;1 = 有歧义（摘要用 `?` 标出来），
        /// 因为"我说的这个列表"和"模型看到的那个列表"可能不是同一个。
        /// </summary>
        public int VisibleCandidates = 1;

        public List<UiListRow> Rows = new List<UiListRow>();

        /// <summary>
        /// 从 CmdBridge 的观察字典里读 `uiList`（缺字段 = null = **这个屏幕上没有列表**）。
        ///
        /// 纪律（与 `ActionGuardRules` 同一套）：**判不了 ≠ 空列表**。
        /// 观察块里带 `error`（取行的时候抛了）时返回 null 并带上原因 ——
        /// 否则"读失败"会被渲染成 `WorldsList[]/0`，模型会以为"一个世界都没有"，
        /// 进而决定"去创建一个新世界"（真的踩过这一类：A42 的 `present` 缺失）。
        /// </summary>
        public static UiListSnapshot FromObservation(Dictionary<string, object> raw)
        {
            UiListSnapshot list;
            string error;
            return TryFromObservation(raw, out list, out error) ? list : null;
        }

        public static bool TryFromObservation(Dictionary<string, object> raw,
            out UiListSnapshot list, out string error)
        {
            list = null;
            error = null;

            object block;
            if (raw == null || !raw.TryGetValue("uiList", out block) || block == null)
                return false;

            var map = block as Dictionary<string, object>;
            if (map == null)
            {
                error = "uiList is not an object";
                return false;
            }

            object failure;
            if (map.TryGetValue("error", out failure) && failure != null)
            {
                error = Convert.ToString(failure, CultureInfo.InvariantCulture);
                return false;
            }

            var snapshot = new UiListSnapshot();
            snapshot.Panel = Text(map, "panel");
            snapshot.ItemsCount = Math.Max(0, Number(map, "itemsCount"));
            snapshot.SelectedIndex = Number(map, "selectedIndex", -1);
            snapshot.CanScrollUp = Flag(map, "canScrollUp");
            snapshot.CanScrollDown = Flag(map, "canScrollDown");
            snapshot.VisibleCandidates = Math.Max(1, Number(map, "visibleCandidates", 1));

            object rows;
            if (map.TryGetValue("rows", out rows))
            {
                var array = rows as List<object>;
                if (array != null)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        var rowMap = array[i] as Dictionary<string, object>;
                        if (rowMap == null)
                            continue;
                        snapshot.Rows.Add(new UiListRow
                        {
                            Index = Number(rowMap, "index", snapshot.Rows.Count),
                            Text = Text(rowMap, "text"),
                            Selected = Flag(rowMap, "selected"),
                            Visible = Flag(rowMap, "visible")
                        });
                    }
                }
            }

            // 列表名都没有 = 这个观察块不是列表（不是"空列表"）。
            if (string.IsNullOrEmpty(snapshot.Panel))
            {
                error = "uiList has no panel name";
                return false;
            }

            list = snapshot;
            return true;
        }

        private static object Get(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value : null;
        }

        private static bool Flag(Dictionary<string, object> source, string key)
        {
            object raw = Get(source, key);
            return raw is bool && (bool)raw;
        }

        private static string Text(Dictionary<string, object> source, string key)
        {
            return Get(source, key) as string;
        }

        private static int Number(Dictionary<string, object> source, string key)
        {
            return Number(source, key, 0);
        }

        private static int Number(Dictionary<string, object> source, string key, int fallback)
        {
            object raw = Get(source, key);
            if (raw == null)
                return fallback;
            if (raw is int)
                return (int)raw;
            if (raw is long)
                return (int)(long)raw;
            if (raw is float)
                return (int)Math.Round((float)raw);
            if (raw is double)
                return (int)Math.Round((double)raw);
            int parsed;
            string text = raw as string;
            return text != null && int.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }

    /// <summary>
    /// 把列表快照渲染成喂给 Laya 的一小段文本（G16）。纯逻辑。
    ///
    /// 格式（**故意做成一行、无空格**：摘要是空格分隔的 `key=value` 序列，
    /// value 里出现空格会把这个字段切成两半）：
    ///
    /// <code>
    ///   list=WorldsList[0:Base*|1:Cave|2:Nether]/9v
    ///        │        │  │            │  │└─ v = 还能往下滚（^ = 还能往上滚）
    ///        │        │  │            │  └── /9 = 列表总行数（不只可见这几行）
    ///        │        │  │            └───── ] 结束
    ///        │        │  └────────────────── 行：<绝对序号>:<文字>，选中行带 *
    ///        │        └───────────────────── [ 开始
    ///        └────────────────────────────── 面板名（与 list:<面板>#N 同名）
    ///
    ///   list=?WorldsList[0:a|1:b]/2      ← 前缀 ? = 屏幕上有多个可见列表，这只是最大的那个
    ///   list=WorldsList[0:a|1:b]/9v!sel=7 ← 当前选中行不在发出来的这几行里（模型看不到它）
    ///   list=WorldsList[]/0               ← 列表真的存在但是空的（"还没建过世界"）
    /// </code>
    ///
    /// 三条纪律：
    ///   · **没有列表就整个字段不发**（`null`），不要发 `list=none`：少一项比发一个
    ///     模型没法用的值更好（摘要预算本来就紧，且"没有"和"有但是空的"是两件事）；
    ///   · **文字必须消毒**：世界名里可能有空格/`|`/`*`，不消毒会把编码弄坏，
    ///     而"摘要被编码破坏"在模型侧表现为莫名其妙的判断，最难查；
    ///   · **截断与丢弃都要留痕**：被截的行标 `..`，被丢掉的行用 `+N` 计数 ——
    ///     模型必须知道"我看到的不是全部"。
    /// </summary>
    public static class UiListDigest
    {
        /// <summary>默认最多发几行。世界外选世界 4 行足够，且要留预算给别的字段。</summary>
        public const int DefaultMaxRows = 4;

        /// <summary>默认每行文字最多几个字符（世界名通常很短，长的截断并留 `..`）。</summary>
        public const int DefaultMaxTextChars = 14;

        /// <summary>
        /// 消毒时**必须清掉**的结构字符（它们在这个编码里有含义）。
        /// `&lt;` `&gt;` 也在里面：两种编码都用 `<none>` 这类尖括号哨兵表示"没有"，
        /// 值里出现尖括号只会让人（和模型）把哨兵与真实文本混起来。
        /// </summary>
        private const string Structural = "[]|*?/+^!~=<>";

        public static string Compile(UiListSnapshot list)
        {
            return Compile(list, DefaultMaxRows, DefaultMaxTextChars);
        }

        public static string Compile(UiListSnapshot list, int maxRows, int maxTextChars)
        {
            if (list == null || string.IsNullOrEmpty(list.Panel))
                return null;
            if (maxRows <= 0)
                return null;

            var rows = SelectRows(list, maxRows, out int dropped);

            var text = new StringBuilder();
            if (list.VisibleCandidates > 1)
                text.Append('?');
            text.Append(CleanToken(list.Panel, 24));
            text.Append('[');
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                    text.Append('|');
                UiListRow row = rows[i];
                text.Append(row.Index.ToString(CultureInfo.InvariantCulture));
                text.Append(':');
                string label = CleanToken(row.Text, maxTextChars);
                text.Append(string.IsNullOrEmpty(label) ? "?" : label);
                if (row.Selected)
                    text.Append('*');
            }
            text.Append(']');
            text.Append('/').Append(list.ItemsCount.ToString(CultureInfo.InvariantCulture));

            // 选中行不在发出去的行里 = **它真的滚出可视区了**（不是"被行数上限挤掉"：
            // 那种情况上面已经把它换进来了）。这条必须显式写出来：
            // 它正是"要不要先滚动/翻页"的依据。
            if (list.SelectedIndex >= 0 && !ContainsIndex(rows, list.SelectedIndex))
                text.Append("!sel=").Append(list.SelectedIndex.ToString(CultureInfo.InvariantCulture));

            if (list.CanScrollUp)
                text.Append('^');
            if (list.CanScrollDown)
                text.Append('v');
            if (dropped > 0)
                text.Append('+').Append(dropped.ToString(CultureInfo.InvariantCulture));

            return text.ToString();
        }

        /// <summary>
        /// 挑要发的行：**只发可见行**（滚动出去的行对模型没用，还会挤掉别的字段）；
        /// 一个"可见"标记都没有时（几何未知/纯逻辑测试）退回"从头取 maxRows 行"。
        /// 顺序保持列表内的绝对顺序，模型才能按 `#N` 说回来。
        ///
        /// **选中行必须挤进来**（实机踩到的）：在"内容管理"里点了第 12 行之后，
        /// 可视区是 4~12（9 行），而只发前 4 行 → 发出去的是 4~7，
        /// **模型看不到"现在选的是哪个"** —— 而"当前选中项的文本"恰恰是
        /// "要不要换一行/删掉它"这类决策最重要的输入。所以：先取 maxRows 行，
        /// 若选中行不在里面（但确实在候选集里），就**用它换掉最后一行**再按序号排序。
        /// `!sel=` 于是只剩"选中行真的滚出可视区了"这一种含义。
        /// </summary>
        private static List<UiListRow> SelectRows(UiListSnapshot list, int maxRows,
            out int dropped)
        {
            var visible = new List<UiListRow>();
            var all = new List<UiListRow>();
            for (int i = 0; i < list.Rows.Count; i++)
            {
                UiListRow row = list.Rows[i];
                if (row == null)
                    continue;
                all.Add(row);
                if (row.Visible)
                    visible.Add(row);
            }

            List<UiListRow> source = visible.Count > 0 ? visible : all;
            var rows = new List<UiListRow>();
            for (int i = 0; i < source.Count && rows.Count < maxRows; i++)
                rows.Add(source[i]);

            if (list.SelectedIndex >= 0 && !ContainsIndex(rows, list.SelectedIndex))
            {
                UiListRow selected = Find(source, list.SelectedIndex);
                if (selected != null)
                {
                    if (rows.Count >= maxRows && rows.Count > 0)
                        rows.RemoveAt(rows.Count - 1);
                    rows.Add(selected);
                    rows.Sort((left, right) => left.Index.CompareTo(right.Index));
                }
            }

            // 丢了多少行要如实说，但**只算"候选行里被 maxRows 挤掉的"**：
            // 滚动出去的行由 `/总数` + `^`/`v` 表达，再计一次是重复报账、白占预算。
            dropped = source.Count - rows.Count;
            return rows;
        }

        private static UiListRow Find(List<UiListRow> rows, int index)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Index == index)
                    return rows[i];
            }
            return null;
        }

        private static bool ContainsIndex(List<UiListRow> rows, int index)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Index == index)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 消毒：清掉结构字符、把空白折成 `_`、按字符数截断（截断留 `..`）。
        /// 不清会怎样：世界名叫 `My|World` 时摘要变成 `0:My|World`，
        /// 读的人（和模型）都会把一行读成两行。
        ///
        /// `internal` 而不是 private：HUD 那行状态（`HudStatusLine`）用**同一套**编码纪律
        /// （一行、无空格、结构字符消毒），两处各写一份必然漂移。
        /// </summary>
        internal static string CleanToken(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            var text = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                // 空白与结构字符**统一折成一个 `_`**（连续的一串只留一个）：
                // 世界名里 "My |World*" 折成 `My_World`，而不是 `My__World_` ——
                // 摘要长度是预算，连续的占位符既不好看也白花钱；首尾的 `_` 最后再裁掉。
                if (char.IsWhiteSpace(c) || Structural.IndexOf(c) >= 0)
                {
                    if (text.Length == 0 || text[text.Length - 1] == '_')
                        continue;
                    text.Append('_');
                    continue;
                }
                text.Append(c);
            }

            string cleaned = text.ToString().Trim('_');
            // 截断后的长度**不超过** maxChars（`..` 也算在里面）：摘要预算是硬的，
            // "每行最多 N 个字符"如果实际是 N+2，行数一多照样撑爆。
            if (maxChars > 0 && cleaned.Length > maxChars)
            {
                cleaned = maxChars <= 2
                    ? cleaned.Substring(0, maxChars)
                    : cleaned.Substring(0, maxChars - 2) + "..";
            }
            return cleaned;
        }
    }
}

using System;
using System.Globalization;

namespace CmdBridgeMod
{
    /// <summary>
    /// **语义目标**解析（CmdBridge 侧的唯一实现，`ui.locate` / `ui.click` / `act.uiclick` 都用它）。
    ///
    /// 用户的要求（原话）："把相应的方法做成 CmdBridgeMod 能提供的服务……避免硬编码由于分辨率变化
    /// 或窗口尺寸变化导致无法使用"。所以定位一律**现解析**，像素只是最后兜底：
    ///
    /// | 写法 | 含义 | 定位方式 |
    /// |------|------|----------|
    /// | `Play` | 控件名 / 文本 | `UiInspector.Resolve`（唯一匹配，歧义就报错不猜） |
    /// | `[MainMenuScreen#0]/…/Play` | 控件路径 | 同上（路径精确匹配） |
    /// | `list:WorldsList#0` | 列表第 0 行 | `UiInspector.TryResolveListRow`（跟着滚动位置走） |
    /// | `list:WorldsList@Rebritish` | 文字里含 `Rebritish` 的那一行 | 同上 |
    /// | `1010.6,64.83` | 客户区坐标（**兜底**） | 直接用 —— 窗口一变就可能点到别处，尽量别用 |
    ///
    /// 注意别把 `#`/`@` 直接缀在控件路径后面：`UiInspector.BuildPath` 生成的路径本身就带
    /// `[Type#id]`，那样分不清"行号"和"路径里的 id"。列表行一律加 `list:` 前缀。
    ///
    /// 这份实现与 <c>PlayerAiMod/Actor/UiClickTarget.cs</c>（离线自检用、不依赖游戏）是**同一份约定**：
    /// 运行时解析走这里，两边改一处必须改另一处（`Mod/Packages` 的自检会同时钉住）。
    /// </summary>
    public static class UiTarget
    {
        public const string ListPrefix = "list:";

        public enum TargetKind
        {
            /// <summary>控件选择器（名字/路径/文本/序号）。</summary>
            Selector,

            /// <summary>列表里的一行（按序号或文字）。</summary>
            ListRow,

            /// <summary>客户区坐标（兜底，会随窗口尺寸/UI 缩放失效）。</summary>
            Point
        }

        public struct Parsed
        {
            public TargetKind Kind;

            /// <summary>列表选择器（`ListRow` 时有效）。</summary>
            public string Selector;

            /// <summary>行号（-1 = 按文字找）。</summary>
            public int RowIndex;

            /// <summary>行文字（null = 按行号找）。</summary>
            public string RowText;

            /// <summary>客户区坐标（`Point` 时有效）。</summary>
            public float X;

            public float Y;

            public override string ToString()
            {
                switch (Kind)
                {
                    case TargetKind.ListRow:
                        return ListPrefix + Selector
                            + (string.IsNullOrEmpty(RowText)
                                ? "#" + RowIndex.ToString(CultureInfo.InvariantCulture)
                                : "@" + RowText);
                    case TargetKind.Point:
                        return X.ToString("0.##", CultureInfo.InvariantCulture) + ","
                            + Y.ToString("0.##", CultureInfo.InvariantCulture);
                    default:
                        return Selector;
                }
            }
        }

        /// <summary>解析目标；无法解析返回 false（调用方如实报 invalid_argument）。</summary>
        public static bool TryParse(string target, out Parsed parsed)
        {
            parsed = default(Parsed);
            if (string.IsNullOrWhiteSpace(target))
                return false;

            string text = target.Trim();

            // ① 列表行：`list:<选择器>#<行号>` / `list:<选择器>@<文字>`
            if (text.StartsWith(ListPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = text.Substring(ListPrefix.Length);
                int hash = rest.LastIndexOf('#');
                int at = rest.LastIndexOf('@');
                int split = Math.Max(hash, at);
                if (split <= 0 || split >= rest.Length - 1)
                    return false;

                string selector = rest.Substring(0, split).Trim();
                string value = rest.Substring(split + 1).Trim();
                if (selector.Length == 0 || value.Length == 0)
                    return false;

                parsed.Kind = TargetKind.ListRow;
                parsed.Selector = selector;
                parsed.RowIndex = -1;
                parsed.RowText = null;

                if (split == at)
                {
                    parsed.RowText = value;
                    return true;
                }

                int index;
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                    || index < 0)
                {
                    return false;
                }
                parsed.RowIndex = index;
                return true;
            }

            // ② 坐标兜底：`x,y`（路径里会带 `/` 和 `[`，那种一定不是坐标）
            int comma = text.IndexOf(',');
            if (comma > 0 && text.IndexOf('/') < 0 && text.IndexOf('[') < 0)
            {
                float x;
                float y;
                if (float.TryParse(text.Substring(0, comma), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out x)
                    && float.TryParse(text.Substring(comma + 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out y))
                {
                    parsed.Kind = TargetKind.Point;
                    parsed.X = x;
                    parsed.Y = y;
                    return true;
                }
            }

            // ③ 控件选择器（名字 / 路径 / 文本 / 序号）
            parsed.Kind = TargetKind.Selector;
            parsed.Selector = text;
            parsed.RowIndex = -1;
            parsed.RowText = null;
            return true;
        }

        /// <summary>把"列表第几行 / 哪一行文字"写成事件里的写法（录制端用它记语义目标）。</summary>
        public static string FormatListRow(string selector, int rowIndex, string rowText)
        {
            if (string.IsNullOrEmpty(selector))
                return null;
            if (!string.IsNullOrEmpty(rowText))
                return ListPrefix + selector + "@" + rowText;
            return rowIndex >= 0
                ? ListPrefix + selector + "#" + rowIndex.ToString(CultureInfo.InvariantCulture)
                : null;
        }
    }
}

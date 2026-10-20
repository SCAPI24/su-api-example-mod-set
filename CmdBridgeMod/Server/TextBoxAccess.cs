using Game;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace CmdBridgeMod
{
    /// <summary>
    /// `TextBoxWidget` 的只读/写入访问器（它是 `internal class`，Mod 只能经反射用它）。
    ///
    /// 为什么必须**直接写 `Text` 属性**，而不是模拟键盘：
    ///   · `TextBoxWidget.Text` 的 setter 会裁剪到 `MaximumLength`、重设 `CaretPosition`，
    ///     并触发 `TextChanged`（Source: `Survivalcraft/Game/TextBoxWidget.cs:47-63`）。
    ///     对话框正是靠这个事件把"界面上的字符串"同步到自己的临时数据：
    ///     例如 `EditMemoryBankDialog.TextBox_TextChanged` 把 16 行文本拼起来
    ///     `m_tmpMemoryBankData.LoadString(...)`（Source: `EditMemoryBankDialog.cs:55-74`），
    ///     `EditTruthTableDialog` 的 LinearText 同理（Source: `EditTruthTableDialog.cs:48-55`）。
    ///     也就是说，`Text` setter 与"真人敲键盘"走的是**同一个事件**，内部状态一定一致。
    ///   · 键盘路径另有两个硬限制：只有 `HasFocus` 的文本框才读 `LastChar`
    ///     （Source: `TextBoxWidget.cs:164-237`），而内存条 16 行 `MaximumLength="16"` 且
    ///     `OverwriteMode="true"`（Source: `Pak/Dialogs/EditMemoryBankDialog.xml`），
    ///     光标在行尾时 `EnterText` 的 `m_text.Length + s.Length &lt;= MaximumLength`
    ///     恒不成立（Source: `TextBoxWidget.cs:293-325`）—— 往满行"追加"永远进不去。
    ///
    /// 反射只读/只写**控件的公开属性**（Text / MaximumLength / HasFocus / CaretPosition /
    /// OverwriteMode）；不碰任何引擎输入数组，也不改游戏世界状态。
    /// </summary>
    internal static class TextBoxAccess
    {
        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

        /// <summary>引擎类名（`TextBoxWidget` 在 `Game` 命名空间，且是 internal）。</summary>
        private const string TypeName = "TextBoxWidget";

        /// <summary>按 "类型全名|属性名" 缓存 PropertyInfo（TextBoxWidget 不止一个属性）。</summary>
        private static readonly Dictionary<string, PropertyInfo> s_properties =
            new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        /// <summary>控件是不是 `TextBoxWidget`（含其派生类；按类型名判断，内部类型无法用 is）。</summary>
        internal static bool IsTextBox(Widget widget)
        {
            if (widget == null)
                return false;
            for (Type type = widget.GetType(); type != null; type = type.BaseType)
            {
                if (string.Equals(type.Name, TypeName, StringComparison.Ordinal)
                    && string.Equals(type.Namespace, "Game", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        internal static string Text(Widget widget)
        {
            return (string)Get(widget, "Text");
        }

        /// <summary>
        /// 写入文本。这是 `TextBoxWidget.Text` 的公开 setter：
        /// 裁剪到 `MaximumLength` → `CaretPosition = CaretPosition` → `TextChanged`。
        /// 内部类型本身不可见，但它的 **public 成员**可以用反射正常调用。
        /// </summary>
        internal static void SetText(Widget widget, string text)
        {
            Property(widget, "Text").SetValue(widget, text ?? string.Empty, null);
        }

        internal static int MaximumLength(Widget widget)
        {
            return (int)Get(widget, "MaximumLength");
        }

        internal static bool HasFocus(Widget widget)
        {
            return (bool)Get(widget, "HasFocus");
        }

        internal static int CaretPosition(Widget widget)
        {
            return (int)Get(widget, "CaretPosition");
        }

        internal static bool OverwriteMode(Widget widget)
        {
            return (bool)Get(widget, "OverwriteMode");
        }

        private static object Get(Widget widget, string propertyName)
        {
            return Property(widget, propertyName).GetValue(widget, null);
        }

        private static PropertyInfo Property(Widget widget, string propertyName)
        {
            if (widget == null)
                throw new BridgeCommandException("invalid_argument", "A text box widget is required.");

            Type type = widget.GetType();
            string key = type.FullName + "|" + propertyName;
            lock (s_properties)
            {
                PropertyInfo property;
                if (s_properties.TryGetValue(key, out property))
                    return property;

                property = type.GetProperty(propertyName, PublicInstance);
                if (property == null)
                {
                    throw new BridgeCommandException(
                        "engine_mismatch",
                        "TextBoxWidget." + propertyName + " was not found (engine version mismatch).");
                }
                s_properties[key] = property;
                return property;
            }
        }
    }
}

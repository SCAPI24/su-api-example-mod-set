using Engine;
using Engine.Input;
using Game;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ScMultiplayer
{
    internal sealed class WindowsTalkDialog : TextBoxDialog
    {
        private sealed class ImeInputCaptureWidget : Widget
        {
            private readonly WindowsTalkDialog m_dialog;

            public ImeInputCaptureWidget(WindowsTalkDialog dialog)
            {
                m_dialog = dialog;
                IsHitTestVisible = false;
            }

            public override void Update()
            {
                m_dialog.CaptureCommittedText(Input);
            }
        }

        private readonly TextBoxAccessor m_textBox;
        private bool m_inputSessionClosed;

        public WindowsTalkDialog(string title, string text, int maximumLength,
            Action<string> handler)
            : base(title, text, maximumLength, handler)
        {
            m_textBox = new TextBoxAccessor(
                Children.Find<Widget>("TextBoxDialog.TextBox", true));

            // Source: Survivalcraft/Game/Widget.cs:Widget.UpdateWidgetsHierarchy
            // Children update from last to first. This capture widget therefore consumes the
            // complete IME commit before the stock TextBoxWidget can keep only Keyboard.LastChar.
            Children.Add(new ImeInputCaptureWidget(this));
            KeyboardInput.GetInput();
            KeyboardInput.DeletePressed = false;
            Keyboard.Clear();
        }

        public override void Update()
        {
            base.Update();
            if (!m_inputSessionClosed && Input.Devices == WidgetInputDevice.None)
                CloseInputSession();
        }

        // Source: Engine/Engine/Input/Keyboard.cs:Keyboard.KeyPressHandler
        // Source: Survivalcraft/Game/TextBoxWidget.cs:TextBoxWidget.Update
        private void CaptureCommittedText(WidgetInput input)
        {
            if (!HasKeyboardEvent(input)) return;

            string queuedText = GetPrintableText(KeyboardInput.GetInput());
            if (WindowsIme.IsCompositionActive())
            {
                // Source: Engine/Engine/Input/Keyboard.cs:KeyDownHandler
                // IME preedit keys must not reach TextBoxWidget. It handles Backspace and Enter
                // itself, which would otherwise delete committed text or dismiss this dialog.
                KeyboardInput.DeletePressed = false;
                input.Clear();
                return;
            }

            if (queuedText.Length > 0)
                InsertText(queuedText);
            // Source: Survivalcraft/Game/Widget.cs:Widget.Input
            // The capture and stock TextBoxWidget share the dialog hierarchy input. Clear that
            // shared object so TextBoxWidget cannot append Keyboard.LastChar a second time.
            if (queuedText.Length > 0 || input.LastChar.HasValue)
                m_textBox.Input.Clear();
        }

        // Source: Engine/Engine/Input/Keyboard.cs:KeyPressHandler
        private static bool HasKeyboardEvent(WidgetInput input)
        {
            return input.LastChar.HasValue || input.LastKey.HasValue ||
                KeyboardInput.Chars.Count > 0;
        }

        private string Text
        {
            get => m_textBox.Text ?? string.Empty;
            set => m_textBox.Text = value ?? string.Empty;
        }

        private int CaretPosition
        {
            get => m_textBox.CaretPosition;
            set => m_textBox.CaretPosition = MathUtils.Clamp(value, 0, Text.Length);
        }

        private int MaximumLength => m_textBox.MaximumLength;

        private sealed class TextBoxAccessor
        {
            private readonly Widget m_widget;
            private readonly PropertyInfo m_textProperty;
            private readonly PropertyInfo m_caretPositionProperty;
            private readonly PropertyInfo m_maximumLengthProperty;
            private readonly PropertyInfo m_hasFocusProperty;

            // Source: Survivalcraft/Game/TextBoxWidget.cs:TextBoxWidget
            // TextBoxWidget is internal to the game assembly. Cache its public property
            // accessors once per chat dialog instead of probing the type during input.
            public TextBoxAccessor(Widget widget)
            {
                m_widget = widget ?? throw new ArgumentNullException(nameof(widget));
                Type type = widget.GetType();
                m_textProperty = GetRequiredProperty(type, "Text");
                m_caretPositionProperty = GetRequiredProperty(type, "CaretPosition");
                m_maximumLengthProperty = GetRequiredProperty(type, "MaximumLength");
                m_hasFocusProperty = GetRequiredProperty(type, "HasFocus");
            }

            public WidgetInput Input => m_widget.Input;

            public string Text
            {
                get => m_textProperty.GetValue(m_widget) as string ?? string.Empty;
                set => m_textProperty.SetValue(m_widget, value ?? string.Empty);
            }

            public int CaretPosition
            {
                get => (int)m_caretPositionProperty.GetValue(m_widget);
                set => m_caretPositionProperty.SetValue(m_widget, value);
            }

            public int MaximumLength => (int)m_maximumLengthProperty.GetValue(m_widget);

            public bool HasFocus
            {
                set => m_hasFocusProperty.SetValue(m_widget, value);
            }

            private static PropertyInfo GetRequiredProperty(Type type, string name)
            {
                return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) ??
                    throw new InvalidOperationException($"TextBoxWidget.{name} was not found.");
            }
        }

        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.HideDialog
        // Hiding a dialog replaces its hierarchy input with WidgetInputDevice.None. Drain both
        // input paths once so the first gameplay key cannot resume an old IME composition.
        private void CloseInputSession()
        {
            m_inputSessionClosed = true;
            m_textBox.HasFocus = false;
            KeyboardInput.GetInput();
            KeyboardInput.DeletePressed = false;
            Keyboard.Clear();
        }

        private void InsertText(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            string text = Text;
            int caret = CaretPosition;
            int available = MaximumLength - text.Length;
            if (available <= 0) return;
            if (value.Length > available) value = value.Substring(0, available);
            Text = text.Insert(caret, value);
            CaretPosition = caret + value.Length;
        }

        private static string GetPrintableText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var result = new StringBuilder(text.Length);
            foreach (char character in text)
            {
                if (!char.IsControl(character)) result.Append(character);
            }
            return result.ToString();
        }

        private static class WindowsIme
        {
            private const int GcsCompStr = 0x0008;

            // Source: Windows IMM32 ImmGetCompositionStringW documentation
            // The game window is necessarily foreground while it accepts keyboard input, so this
            // avoids accessing Engine's internal OpenTK window object from the Mod assembly.
            public static bool IsCompositionActive()
            {
                if (!OperatingSystem.IsWindows()) return false;

                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero) return false;
                IntPtr context = ImmGetContext(window);
                if (context == IntPtr.Zero) return false;
                try
                {
                    return ImmGetCompositionStringW(context, GcsCompStr,
                        IntPtr.Zero, 0) > 0;
                }
                finally
                {
                    ImmReleaseContext(window, context);
                }
            }

            [DllImport("user32.dll")]
            private static extern IntPtr GetForegroundWindow();

            [DllImport("imm32.dll")]
            private static extern IntPtr ImmGetContext(IntPtr window);

            [DllImport("imm32.dll")]
            private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);

            [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
            private static extern int ImmGetCompositionStringW(IntPtr context, int index,
                IntPtr buffer, int bufferLength);
        }
    }
}

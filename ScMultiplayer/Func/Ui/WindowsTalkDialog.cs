using Engine;
using Engine.Input;
using Game;
using System;
using System.Reflection;
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

        private readonly Widget m_textBox;
        private readonly PropertyInfo m_textProperty;
        private readonly PropertyInfo m_caretPositionProperty;
        private readonly PropertyInfo m_maximumLengthProperty;

        public WindowsTalkDialog(string title, string text, int maximumLength,
            Action<string> handler)
            : base(title, text, maximumLength, handler)
        {
            m_textBox = Children.Find("TextBoxDialog.TextBox", true);
            Type textBoxType = m_textBox.GetType();
            m_textProperty = textBoxType.GetProperty("Text");
            m_caretPositionProperty = textBoxType.GetProperty("CaretPosition");
            m_maximumLengthProperty = textBoxType.GetProperty("MaximumLength");
            if (m_textProperty == null || m_caretPositionProperty == null ||
                m_maximumLengthProperty == null)
            {
                throw new MissingMemberException(textBoxType.FullName,
                    "Text/CaretPosition/MaximumLength");
            }

            // Source: Survivalcraft/Game/Widget.cs:Widget.UpdateWidgetsHierarchy
            // Children update from last to first. This capture widget therefore consumes the
            // complete IME commit before the stock TextBoxWidget can keep only Keyboard.LastChar.
            Children.Add(new ImeInputCaptureWidget(this));
            KeyboardInput.GetInput();
            KeyboardInput.DeletePressed = false;
        }

        // Source: Engine/Engine/Input/Keyboard.cs:Keyboard.KeyPressHandler
        // Source: Survivalcraft/Game/TextBoxWidget.cs:TextBoxWidget.Update
        private void CaptureCommittedText(WidgetInput input)
        {
            string queuedText = GetPrintableText(KeyboardInput.GetInput());
            if (queuedText.Length == 0) return;
            InsertText(queuedText);
            input.Clear();
        }

        private string Text
        {
            get => (string)m_textProperty.GetValue(m_textBox) ?? string.Empty;
            set => m_textProperty.SetValue(m_textBox, value ?? string.Empty);
        }

        private int CaretPosition
        {
            get => (int)m_caretPositionProperty.GetValue(m_textBox);
            set => m_caretPositionProperty.SetValue(
                m_textBox, MathUtils.Clamp(value, 0, Text.Length));
        }

        private int MaximumLength => (int)m_maximumLengthProperty.GetValue(m_textBox);

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
    }
}

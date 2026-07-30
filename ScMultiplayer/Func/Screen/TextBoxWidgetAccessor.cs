using Game;
using System;
using System.Reflection;

namespace ScMultiplayer
{
    internal sealed class TextBoxWidgetAccessor
    {
        private readonly Widget m_widget;
        private readonly PropertyInfo m_textProperty;
        private readonly PropertyInfo m_maximumLengthProperty;

        // Source: Survivalcraft/Game/TextBoxWidget.cs:TextBoxWidget.Text
        public TextBoxWidgetAccessor(Widget widget)
        {
            m_widget = widget ?? throw new ArgumentNullException(nameof(widget));
            Type type = widget.GetType();
            m_textProperty = type.GetProperty("Text", BindingFlags.Public |
                BindingFlags.Instance) ?? throw new InvalidOperationException(
                "TextBoxWidget.Text was not found.");
            m_maximumLengthProperty = type.GetProperty("MaximumLength",
                BindingFlags.Public | BindingFlags.Instance) ??
                throw new InvalidOperationException(
                    "TextBoxWidget.MaximumLength was not found.");
        }

        public string Text
        {
            get => m_textProperty.GetValue(m_widget) as string ?? string.Empty;
            set => m_textProperty.SetValue(m_widget, value ?? string.Empty);
        }

        public int MaximumLength
        {
            set => m_maximumLengthProperty.SetValue(m_widget, value);
        }

        public bool IsEnabled
        {
            set => m_widget.IsEnabled = value;
        }
    }
}

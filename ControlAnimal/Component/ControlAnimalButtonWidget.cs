using System.Xml.Linq;
using Engine;
using Engine.Media;
using Game;

namespace ControlAnimal;

public class ControlAnimalButtonWidget : ButtonWidget
{
    private readonly BevelledRectangleWidget m_rectangleWidget;
    private readonly LabelWidget m_labelWidget;
    private readonly ClickableWidget m_clickableWidget;
    public override bool IsClicked => m_clickableWidget.IsClicked;
    public bool IsPressed => m_clickableWidget.IsPressed;

    public override bool IsChecked
    {
        get => m_clickableWidget.IsChecked;
        set => m_clickableWidget.IsChecked = value;
    }

    public override bool IsAutoCheckingEnabled
    {
        get => m_clickableWidget.IsAutoCheckingEnabled;
        set => m_clickableWidget.IsAutoCheckingEnabled = value;
    }

    public override string Text
    {
        get => m_labelWidget.Text;
        set => m_labelWidget.Text = value;
    }

    public override BitmapFont Font
    {
        get => m_labelWidget.Font;
        set => m_labelWidget.Font = value;
    }

    public override Color Color { get; set; } = Color.White;
    public Color CenterColor { get; set; } = new Color(80, 80, 80);
    public Color BevelColor { get; set; } = new Color(160, 160, 160);
    public float BevelSize { get; set; } = 2f;

    public ControlAnimalButtonWidget()
    {
        XElement node = ContentManager.Get<XElement>("Widgets/BevelledButtonContents");
        LoadChildren(this, node);
        m_rectangleWidget = Children.Find<BevelledRectangleWidget>("BevelledButton.Rectangle");
        m_labelWidget = Children.Find<LabelWidget>("BevelledButton.Label");
        m_clickableWidget = Children.Find<ClickableWidget>("BevelledButton.Clickable");
        LoadProperties(this, node);
    }

    public override void MeasureOverride(Vector2 parentAvailableSize)
    {
        bool enabled = IsEnabledGlobal;
        m_labelWidget.Color = enabled ? Color : new Color(112, 112, 112);
        m_rectangleWidget.CenterColor = CenterColor;
        m_rectangleWidget.BevelColor = BevelColor;
        m_rectangleWidget.BevelSize = BevelSize;
        base.MeasureOverride(parentAvailableSize);
    }
}

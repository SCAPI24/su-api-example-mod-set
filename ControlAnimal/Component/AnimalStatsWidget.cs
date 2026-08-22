using Engine;
using Engine.Graphics;
using Game;

namespace ControlAnimal;

public class AnimalStatsWidget : CanvasWidget
{
    private readonly ComponentCreature m_animal;
    private readonly LabelWidget m_title;
    private readonly LabelWidget m_stats;

    public AnimalStatsWidget(ComponentCreature animal)
    {
        m_animal = animal;
        Size = new Vector2(520f, 360f);
        HorizontalAlignment = WidgetAlignment.Center;
        VerticalAlignment = WidgetAlignment.Center;

        RectangleWidget background = new RectangleWidget
        {
            FillColor = new Color(25, 25, 25, 235),
            OutlineColor = Color.White,
            OutlineThickness = 2f,
            HorizontalAlignment = WidgetAlignment.Stretch,
            VerticalAlignment = WidgetAlignment.Stretch
        };
        StackPanelWidget stack = new StackPanelWidget
        {
            Direction = LayoutDirection.Vertical,
            HorizontalAlignment = WidgetAlignment.Center,
            VerticalAlignment = WidgetAlignment.Center,
            Margin = new Vector2(24f)
        };
        ModelWidget modelWidget = CreateAnimalModelWidget(animal);
        m_title = new LabelWidget
        {
            Color = new Color(120, 230, 130),
            FontScale = 1.2f,
            HorizontalAlignment = WidgetAlignment.Center
        };
        m_stats = new LabelWidget
        {
            Color = Color.White,
            FontScale = 0.8f,
            HorizontalAlignment = WidgetAlignment.Center
        };
        if (modelWidget != null)
        {
            stack.Children.Add(modelWidget);
        }
        stack.Children.Add(m_title);
        stack.Children.Add(m_stats);
        Children.Add(background);
        Children.Add(stack);
        UpdateText();
    }

    public override void Update()
    {
        UpdateText();
    }

    private static ModelWidget CreateAnimalModelWidget(ComponentCreature animal)
    {
        ComponentModel componentModel = animal.Entity.FindComponent<ComponentModel>();
        if (componentModel?.Model == null)
        {
            return null;
        }
        Matrix[] transforms = new Matrix[componentModel.Model.Bones.Count];
        componentModel.Model.CopyAbsoluteBoneTransformsTo(transforms);
        BoundingBox box = componentModel.Model.CalculateAbsoluteBoundingBox(transforms);
        float extent = MathUtils.Max(box.Size().X, 1.4f * box.Size().Y, box.Size().Z);
        return new ModelWidget
        {
            Size = new Vector2(220f, 180f),
            Model = componentModel.Model,
            TextureOverride = componentModel.TextureOverride,
            ViewPosition = box.Center() + 2.6f * MathUtils.Pow(extent, 0.75f) * new Vector3(1f, 0f, -1f),
            ViewTarget = box.Center(),
            ViewFov = 0.3f,
            AutoRotationVector = new Vector3(0f, 0.4f, 0f),
            HorizontalAlignment = WidgetAlignment.Center
        };
    }

    private void UpdateText()
    {
        ComponentVitalStats vital = m_animal.Entity.FindComponent<ComponentVitalStats>();
        ComponentMiner miner = m_animal.Entity.FindComponent<ComponentMiner>();
        Vector3 position = m_animal.ComponentBody.Position;
        m_title.Text = m_animal.DisplayName ?? "动物";
        m_stats.Text = string.Format(
            "位置: {0:F1}, {1:F1}, {2:F1}\n生命: {3:F2}\n食物: {4}\n耐力: {5}\n速度: {6:F2}\n攻击: {7:F2}",
            position.X,
            position.Y,
            position.Z,
            m_animal.ComponentHealth.Health,
            vital == null ? "-" : vital.Food.ToString("F2"),
            vital == null ? "-" : vital.Stamina.ToString("F2"),
            m_animal.ComponentLocomotion.WalkSpeed,
            miner == null ? 0f : miner.AttackPower);
    }
}

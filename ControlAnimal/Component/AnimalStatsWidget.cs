using System.Globalization;
using Engine;
using Engine.Graphics;
using Game;

namespace ControlAnimal;

public class AnimalStatsWidget : ClothingWidget
{
    private readonly ComponentCreature m_animal;
    private readonly LabelWidget m_title;
    private readonly LabelWidget m_leftStats;
    private readonly LabelWidget m_rightStats;

    public AnimalStatsWidget(ComponentPlayer player, ComponentCreature animal)
        : base(player)
    {
        m_animal = animal;
        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.IsClothingVisible
        // Keep ClothingWidget identity for native C/button toggling, but replace its visual contents.
        Children.Clear();
        Size = new Vector2(640f, 360f);
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
        StackPanelWidget statsColumns = new StackPanelWidget
        {
            Direction = LayoutDirection.Horizontal,
            HorizontalAlignment = WidgetAlignment.Center,
            Margin = new Vector2(12f, 6f)
        };
        m_leftStats = new LabelWidget
        {
            Size = new Vector2(250f, float.PositiveInfinity),
            Color = Color.White,
            FontScale = 0.8f,
            TextAnchor = TextAnchor.HorizontalCenter | TextAnchor.Top
        };
        m_rightStats = new LabelWidget
        {
            Size = new Vector2(310f, float.PositiveInfinity),
            Color = Color.White,
            FontScale = 0.8f,
            TextAnchor = TextAnchor.HorizontalCenter | TextAnchor.Top
        };
        statsColumns.Children.Add(m_leftStats);
        statsColumns.Children.Add(m_rightStats);
        if (modelWidget != null)
        {
            stack.Children.Add(modelWidget);
        }
        stack.Children.Add(m_title);
        stack.Children.Add(statsColumns);
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
        m_title.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0} - Animal Stats",
            m_animal.DisplayName ?? "Animal");
        float speed = MathUtils.Max(
            m_animal.ComponentLocomotion.WalkSpeed,
            m_animal.ComponentLocomotion.FlySpeed,
            m_animal.ComponentLocomotion.SwimSpeed);
        m_leftStats.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Health: {0:F2}\nFood: {1}\nStamina: {2}",
            m_animal.ComponentHealth.Health,
            vital == null ? "N/A" : vital.Food.ToString("F2", CultureInfo.InvariantCulture),
            vital == null ? "N/A" : vital.Stamina.ToString("F2", CultureInfo.InvariantCulture));
        m_rightStats.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Speed: {0:F2}\nAttack: {1:F2}\nPosition: {2:F1}, {3:F1}, {4:F1}",
            speed,
            miner == null ? 0f : miner.AttackPower,
            position.X,
            position.Y,
            position.Z);
    }
}

using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace WatchMod
{
    // Source: SubsystemGameWidgets.cs:SubsystemGameWidgets.Update — retain the native Views update stage.
    public sealed class WatchGameWidgetsExtension : UpdateExtension<SubsystemGameWidgets>
    {
        // Source: ComponentCraftingTable.cs:ComponentCraftingTable.Load — slot 2 is the lower-left handcrafting slot.
        private const int HandcraftingSlotIndex = 2;
        private readonly Dictionary<GameWidget, WatchView> m_views = new Dictionary<GameWidget, WatchView>();
        private readonly HashSet<GameWidget> m_currentWidgets = new HashSet<GameWidget>();
        private readonly List<GameWidget> m_removedWidgets = new List<GameWidget>();
        private SubsystemTimeOfDay m_timeOfDay;

        public WatchGameWidgetsExtension(SubsystemGameWidgets host) : base(host) { }

        // Source: UpdateExtensionProjectBinding.cs:UpdateExtensionProjectBinding — Attached runs after Project loading.
        public override void Attached()
        {
            m_timeOfDay = Host.Project.FindSubsystem<SubsystemTimeOfDay>(throwOnError: true);
        }

        // Source: UpdateExtension.cs:UpdateExtension.Update/AfterUpdate — inherited Update permits the unique host call.
        public override void AfterUpdate(float dt, InvocationResult result)
        {
            m_currentWidgets.Clear();
            foreach (GameWidget gameWidget in Host.GameWidgets)
                m_currentWidgets.Add(gameWidget);

            m_removedWidgets.Clear();
            foreach (GameWidget gameWidget in m_views.Keys)
                if (!m_currentWidgets.Contains(gameWidget)) m_removedWidgets.Add(gameWidget);
            foreach (GameWidget gameWidget in m_removedWidgets) RemoveView(gameWidget);

            foreach (GameWidget gameWidget in Host.GameWidgets)
            {
                // Source: PlayerData.cs:PlayerData.ComponentPlayer — null is the normal spawn/respawn interval.
                ComponentPlayer player = gameWidget.PlayerData.ComponentPlayer;
                if (player == null || !player.IsAddedToProject)
                {
                    RemoveView(gameWidget);
                    continue;
                }
                if (!ReferenceEquals(player.Project, Host.Project))
                    throw new InvalidOperationException("[WatchMod] A player view belongs to a different Project.");

                ComponentCraftingTable crafting = player.Entity.FindComponent<ComponentCraftingTable>(throwOnError: true);
                if (crafting.SlotsCount <= HandcraftingSlotIndex)
                    throw new InvalidOperationException("[WatchMod] The player's crafting table has no handcrafting slot 2.");
                if (crafting.GetSlotCount(HandcraftingSlotIndex) == 0
                    || Terrain.ExtractContents(crafting.GetSlotValue(HandcraftingSlotIndex)) != RealTimeClockBlock.Index)
                {
                    RemoveView(gameWidget);
                    continue;
                }

                // Source: GameWidget.xml:LeftControlsContainer — add only this Mod's panel to the actual player tree.
                StackPanelWidget controls = gameWidget.Children.Find<StackPanelWidget>("LeftControlsContainer", true);
                if (m_views.TryGetValue(gameWidget, out WatchView view) && !ReferenceEquals(view.Panel.ParentWidget, controls))
                {
                    RemoveView(gameWidget);
                    view = null;
                }
                if (view == null)
                {
                    if (gameWidget.Children.Find("WatchMod.Panel", false) != null)
                        throw new InvalidOperationException("[WatchMod] A WatchMod.Panel already exists outside this extension's ownership.");
                    view = new WatchView();
                    m_views.Add(gameWidget, view);
                    controls.Children.Add(view.Panel);
                }
                view.UpdateDisplay(m_timeOfDay);
            }
        }

        // Source: UpdateExtension.cs:UpdateExtension.Detached — token removal and world disposal share this cleanup.
        public override void Detached()
        {
            var errors = new List<Exception>();
            foreach (WatchView view in m_views.Values)
            {
                try { view.Dispose(); }
                catch (Exception error) { errors.Add(error); }
            }
            m_views.Clear();
            m_currentWidgets.Clear();
            m_removedWidgets.Clear();
            m_timeOfDay = null;
            if (errors.Count != 0)
                throw new AggregateException("[WatchMod] Failed to release player HUD panels.", errors);
        }

        private void RemoveView(GameWidget gameWidget)
        {
            if (!m_views.TryGetValue(gameWidget, out WatchView view)) return;
            view.Dispose();
            m_views.Remove(gameWidget);
        }

        private sealed class WatchView : IDisposable
        {
            public CanvasWidget Panel { get; }
            private readonly LabelWidget m_timeLabel;
            private readonly LabelWidget m_dayLabel;

            // Source: SuWatchComponent.cs:SuWatchComponent.AttachUI — preserve the original watch dimensions and styling.
            public WatchView()
            {
                Panel = new CanvasWidget
                {
                    Name = "WatchMod.Panel",
                    HorizontalAlignment = WidgetAlignment.Near,
                    Size = new Vector2(64f, 48f),
                    Margin = new Vector2(0f, 3f),
                    IsHitTestVisible = false
                };
                var border = new RectangleWidget
                {
                    FillColor = Color.Transparent,
                    OutlineColor = Color.White,
                    OutlineThickness = 1.5f,
                    HorizontalAlignment = WidgetAlignment.Stretch,
                    VerticalAlignment = WidgetAlignment.Stretch,
                    IsHitTestVisible = false
                };
                var stack = new StackPanelWidget
                {
                    Direction = LayoutDirection.Vertical,
                    HorizontalAlignment = WidgetAlignment.Center,
                    VerticalAlignment = WidgetAlignment.Center,
                    Margin = new Vector2(3f, 2f),
                    IsHitTestVisible = false
                };
                m_timeLabel = new LabelWidget
                {
                    Name = "WatchMod.Time",
                    Color = Color.White,
                    FontScale = 0.65f,
                    HorizontalAlignment = WidgetAlignment.Center
                };
                m_dayLabel = new LabelWidget
                {
                    Name = "WatchMod.Day",
                    Color = new Color(200, 200, 200),
                    FontScale = 0.45f,
                    HorizontalAlignment = WidgetAlignment.Center
                };
                stack.Children.Add(m_timeLabel);
                stack.Children.Add(m_dayLabel);
                Panel.Children.Add(border);
                Panel.Children.Add(stack);
            }

            // Source: SubsystemTimeOfDay.cs:SubsystemTimeOfDay.TimeOfDay/Day — use the real world's configured clock.
            public void UpdateDisplay(SubsystemTimeOfDay timeOfDay)
            {
                int totalMinutes = (int)(timeOfDay.TimeOfDay * 24 * 60);
                int day = (int)Math.Floor(timeOfDay.Day) + 1;
                m_timeLabel.Text = string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", totalMinutes / 60, totalMinutes % 60);
                m_dayLabel.Text = string.Format(CultureInfo.InvariantCulture, "Day {0}", day);
            }

            // Source: ContainerWidget.cs:ContainerWidget.Dispose; WidgetsList.cs:WidgetsList.Remove
            public void Dispose()
            {
                Panel.ParentWidget?.Children.Remove(Panel);
                Panel.Dispose();
            }
        }
    }
}

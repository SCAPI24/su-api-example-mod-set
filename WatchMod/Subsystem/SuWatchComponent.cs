using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using System;
using TemplatesDatabase;

namespace WatchMod
{
    public class SuWatchComponent : Component, IUpdateable
    {
        // Source: ComponentCraftingTable — handcrafting grid slots 0-3 (2x2)
        // Slot layout: 0=左上, 1=右上, 2=左下, 3=右下
        private const int HandcraftingSlotIndex = 2; // 左下格子

        // Source: RealTimeClockBlock.Index = 187
        private const int RealTimeClockBlockIndex = 187;

        private SubsystemTimeOfDay m_subsystemTimeOfDay;
        private SubsystemGameWidgets m_subsystemGameWidgets;
        private ComponentCraftingTable m_craftingTable;

        // UI elements
        private CanvasWidget m_watchPanel;
        private RectangleWidget m_watchBorder;
        private LabelWidget m_timeLabel;
        private LabelWidget m_dateLabel;
        private bool m_uiAttached = false;

        public UpdateOrder UpdateOrder => UpdateOrder.Views; // Source: enum UpdateOrder

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_subsystemTimeOfDay = Project.FindSubsystem<SubsystemTimeOfDay>(throwOnError: true);
            m_subsystemGameWidgets = Project.FindSubsystem<SubsystemGameWidgets>(throwOnError: true);
            m_craftingTable = Entity.FindComponent<ComponentCraftingTable>(throwOnError: true);
        }

        void IUpdateable.Update(float dt)
        {
            // Source: MiniMap — deferred attach until GameWidgets populated
            if (m_subsystemGameWidgets.GameWidgets.Count == 0) return;

            bool shouldShow = ShouldShowWatch();

            if (shouldShow && !m_uiAttached)
            {
                AttachUI();
            }
            else if (!shouldShow && m_uiAttached)
            {
                DetachUI();
            }

            if (m_uiAttached)
            {
                UpdateDisplay();
            }
        }

        // Source: ComponentPlayer.Entity.FindComponent<ComponentCraftingTable>
        // Source: IInventory.GetSlotValue / GetSlotCount
        private bool ShouldShowWatch()
        {
            if (m_craftingTable == null) return false;

            int slotCount = m_craftingTable.GetSlotCount(HandcraftingSlotIndex);
            if (slotCount == 0) return false;

            int slotValue = m_craftingTable.GetSlotValue(HandcraftingSlotIndex);
            int blockIndex = Terrain.ExtractContents(slotValue);
            return blockIndex == RealTimeClockBlockIndex;
        }

        private void AttachUI()
        {
            if (m_uiAttached) return;
            if (m_subsystemGameWidgets.GameWidgets.Count == 0) return;

            // Source: GameWidget.xml — LeftControlsContainer is the left-side vertical StackPanel
            GameWidget gameWidget = m_subsystemGameWidgets.GameWidgets[0];
            StackPanelWidget leftControls = gameWidget.Children.Find<StackPanelWidget>("LeftControlsContainer", true);
            if (leftControls == null) return;

            // Watch panel — white border box below InventoryButton
            m_watchPanel = new CanvasWidget();
            m_watchPanel.HorizontalAlignment = WidgetAlignment.Near;
            m_watchPanel.Size = new Vector2(64f, 48f);
            m_watchPanel.Margin = new Vector2(0f, 3f);

            // White border
            m_watchBorder = new RectangleWidget();
            m_watchBorder.FillColor = Color.Transparent;
            m_watchBorder.OutlineColor = Color.White;
            m_watchBorder.OutlineThickness = 1.5f;
            m_watchBorder.HorizontalAlignment = WidgetAlignment.Stretch;
            m_watchBorder.VerticalAlignment = WidgetAlignment.Stretch;

            // Stack for time + day display
            StackPanelWidget stack = new StackPanelWidget();
            stack.Direction = LayoutDirection.Vertical;
            stack.HorizontalAlignment = WidgetAlignment.Center;
            stack.VerticalAlignment = WidgetAlignment.Center;
            stack.Margin = new Vector2(3f, 2f);

            // Time label
            m_timeLabel = new LabelWidget();
            m_timeLabel.Color = Color.White;
            m_timeLabel.FontScale = 0.65f;
            m_timeLabel.HorizontalAlignment = WidgetAlignment.Center;

            // Date/day label
            m_dateLabel = new LabelWidget();
            m_dateLabel.Color = new Color(200, 200, 200);
            m_dateLabel.FontScale = 0.45f;
            m_dateLabel.HorizontalAlignment = WidgetAlignment.Center;

            stack.Children.Add(m_timeLabel);
            stack.Children.Add(m_dateLabel);

            m_watchPanel.Children.Add(m_watchBorder);
            m_watchPanel.Children.Add(stack);

            // Add below last button in LeftControlsContainer
            leftControls.Children.Add(m_watchPanel);

            m_uiAttached = true;
        }

        private void DetachUI()
        {
            if (!m_uiAttached || m_watchPanel == null) return;

            if (m_watchPanel.ParentWidget != null)
            {
                m_watchPanel.ParentWidget.Children.Remove(m_watchPanel);
            }

            m_watchPanel = null;
            m_watchBorder = null;
            m_timeLabel = null;
            m_dateLabel = null;
            m_uiAttached = false;
        }

        // Source: SubsystemTimeOfDay — TimeOfDay 0-1 (0=midnight, ~0.25=dawn, ~0.5=noon, ~0.75=dusk)
        // Source: SubsystemTimeOfDay — Day = totalElapsedGameTime / 1200 + offset
        private void UpdateDisplay()
        {
            if (m_timeLabel == null) return;

            try
            {
                float timeOfDay = m_subsystemTimeOfDay.TimeOfDay;
                int totalGameMinutes = (int)(timeOfDay * 24 * 60);
                int hours = totalGameMinutes / 60;
                int minutes = totalGameMinutes % 60;

                double day = m_subsystemTimeOfDay.Day;
                int dayCount = (int)Math.Floor(day);

                m_timeLabel.Text = string.Format("{0:D2}:{1:D2}", hours, minutes);
                m_dateLabel.Text = string.Format("Day {0}", dayCount + 1);
            }
            catch (Exception)
            {
                m_timeLabel.Text = "--:--";
                m_dateLabel.Text = "";
            }
        }
    }
}
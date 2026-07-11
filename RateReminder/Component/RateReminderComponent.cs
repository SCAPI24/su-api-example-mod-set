using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using System;
using TemplatesDatabase;

namespace RateReminder
{
    public class RateReminderComponent : Component, IUpdateable
    {
        // Source: SubsystemTimeOfDay.DayDuration = 1200f (one game day = 1200 seconds)
        private const double DayDuration = 1200.0;

        private SubsystemGameInfo m_subsystemGameInfo;

        private bool m_dialogShown;
        private bool m_dialogDismissed;
        private Dialog m_rateDialog;
        private BevelledButtonWidget m_rateButton;
        private BevelledButtonWidget m_laterButton;

        public UpdateOrder UpdateOrder => UpdateOrder.Views;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_subsystemGameInfo = Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
        }

        void IUpdateable.Update(float dt)
        {
            if (m_dialogDismissed) return;
            if (m_subsystemGameInfo == null) return;
            if (GameManager.Project == null) return;

            // If dialog is shown, check button clicks
            if (m_dialogShown)
            {
                CheckDialogButtons();
                return;
            }

            // Check if one game day has elapsed
            if (m_subsystemGameInfo.TotalElapsedGameTime > DayDuration)
            {
                ShowRateDialog();
            }
        }

        private void ShowRateDialog()
        {
            m_dialogShown = true;

            try
            {
                // Build dialog UI
                m_rateDialog = new Dialog();

                StackPanelWidget mainStack = new StackPanelWidget();
                mainStack.Direction = LayoutDirection.Vertical;
                mainStack.HorizontalAlignment = WidgetAlignment.Center;
                mainStack.VerticalAlignment = WidgetAlignment.Center;
                mainStack.Margin = new Vector2(20f, 20f);

                // Title
                LabelWidget titleLabel = new LabelWidget();
                titleLabel.Text = "喜欢这个游戏吗？";
                titleLabel.Color = new Color(255, 215, 0); // Gold
                titleLabel.FontScale = 1.2f;
                titleLabel.HorizontalAlignment = WidgetAlignment.Center;
                titleLabel.Margin = new Vector2(0f, 8f);

                // Message
                LabelWidget messageLabel = new LabelWidget();
                messageLabel.Text = "给个五星好评吧！您的支持是我们前进的动力！";
                messageLabel.Color = Color.White;
                messageLabel.FontScale = 0.8f;
                messageLabel.HorizontalAlignment = WidgetAlignment.Center;
                messageLabel.Margin = new Vector2(0f, 8f);

                // Buttons stack (horizontal)
                StackPanelWidget buttonStack = new StackPanelWidget();
                buttonStack.Direction = LayoutDirection.Horizontal;
                buttonStack.HorizontalAlignment = WidgetAlignment.Center;
                buttonStack.VerticalAlignment = WidgetAlignment.Far;
                buttonStack.Margin = new Vector2(0f, 16f);

                // "五星好评" button — BevelledButtonWidget is concrete subclass of ButtonWidget
                m_rateButton = new BevelledButtonWidget();
                m_rateButton.Text = "五星好评";
                m_rateButton.Color = new Color(80, 180, 80); // Green text
                m_rateButton.BevelColor = new Color(60, 160, 60); // Green bevel
                m_rateButton.CenterColor = new Color(40, 100, 40); // Dark green center
                m_rateButton.Size = new Vector2(160f, 40f);
                m_rateButton.Margin = new Vector2(8f, 0f);
                m_rateButton.HorizontalAlignment = WidgetAlignment.Center;

                // "暂不评价" button
                m_laterButton = new BevelledButtonWidget();
                m_laterButton.Text = "暂不评价";
                m_laterButton.Color = new Color(180, 80, 80); // Red text
                m_laterButton.BevelColor = new Color(160, 60, 60); // Red bevel
                m_laterButton.CenterColor = new Color(100, 40, 40); // Dark red center
                m_laterButton.Size = new Vector2(160f, 40f);
                m_laterButton.Margin = new Vector2(8f, 0f);
                m_laterButton.HorizontalAlignment = WidgetAlignment.Center;

                buttonStack.Children.Add(m_rateButton);
                buttonStack.Children.Add(m_laterButton);

                mainStack.Children.Add(titleLabel);
                mainStack.Children.Add(messageLabel);
                mainStack.Children.Add(buttonStack);

                // Background panel
                RectangleWidget background = new RectangleWidget();
                background.FillColor = new Color(30, 30, 50, 230);
                background.OutlineColor = new Color(255, 215, 0);
                background.OutlineThickness = 2f;
                background.HorizontalAlignment = WidgetAlignment.Stretch;
                background.VerticalAlignment = WidgetAlignment.Stretch;

                m_rateDialog.Children.Add(background);
                m_rateDialog.Children.Add(mainStack);

                // Show dialog using DialogsManager
                ContainerWidget parentWidget = ScreensManager.CurrentScreen ?? ScreensManager.RootWidget;
                DialogsManager.ShowDialog(parentWidget, m_rateDialog);

                Log.Information("[RateReminder] Rate dialog shown after 1 game day");
            }
            catch (Exception ex)
            {
                Log.Error("[RateReminder] Failed to show rate dialog: " + ex.Message);
            }
        }

        private void CheckDialogButtons()
        {
            if (m_rateDialog == null) return;

            try
            {
                if (m_rateButton != null && m_rateButton.IsClicked)
                {
                    // User clicked "五星好评" - open marketplace and continue game
                    m_dialogDismissed = true;
                    DialogsManager.HideDialog(m_rateDialog);
                    MarketplaceManager.ShowMarketplace();
                    Log.Information("[RateReminder] User rated 5 stars, opening marketplace");
                }
                else if (m_laterButton != null && m_laterButton.IsClicked)
                {
                    // User clicked "暂不评价" - go back to main menu
                    m_dialogDismissed = true;
                    DialogsManager.HideDialog(m_rateDialog);
                    GameManager.SaveProject(waitForCompletion: true, showErrorDialog: false);
                    GameManager.DisposeProject();
                    ScreensManager.SwitchScreen("MainMenu");
                    Log.Information("[RateReminder] User declined rating, returning to main menu");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RateReminder] Error handling dialog buttons: " + ex.Message);
            }
        }
    }
}
using Engine;
using Engine.Input;
using Game;
using GameEntitySystem;
using ScMultiplayer.Ports;
using System;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public class MultiplayerUiComponent : Component, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;
        private StackPanelWidget m_moreContents;
        private BevelledButtonWidget m_createButton;
        private BevelledButtonWidget m_talkButton;
        private BevelledButtonWidget m_manageButton;

        public UpdateOrder UpdateOrder => UpdateOrder.Views;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.GameWidget
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(throwOnError: true);
            ScMultiplayer.currentInstance?.ApplyRemoteWeatherState();
        }

        public override void Dispose()
        {
            // Source: GameEntitySystem/Component.cs:Component.Dispose
            // Project disposal is the reliable signal that a client has left the network world.
            ScMultiplayer.currentInstance?.NotifyPlayerComponentDisposing(m_componentPlayer?.PlayerData);
            if (m_createButton?.ParentWidget != null)
                m_createButton.ParentWidget.Children.Remove(m_createButton);
            if (m_talkButton?.ParentWidget != null)
                m_talkButton.ParentWidget.Children.Remove(m_talkButton);
            if (m_manageButton?.ParentWidget != null)
                m_manageButton.ParentWidget.Children.Remove(m_manageButton);
            base.Dispose();
        }

        void IUpdateable.Update(float dt)
        {
            ScMultiplayer.currentInstance?.NotifyProjectSimulationStep(Project);
            // Source: Mod/ConsoleMod/Subsystem/ConsoleSubsystemGameWidgets.cs:AttachConsoleButton
            if (m_moreContents == null) AttachButtons();
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            IMultiplayerUiCommandPort commands = multiplayer?.UiCommands;
            if (m_createButton != null)
            {
                bool joined = commands?.IsInRoom == true;
                string buttonText = joined ? "IF" : "CR";
                if (m_createButton.Text != buttonText)
                    m_createButton.Text = buttonText;
                if (m_createButton.IsClicked)
                {
                    if (joined)
                        commands.ShowJoinedPlayerInformation();
                    else
                        commands?.ShowCreateRoomDialog();
                }
            }
            // Source: Survivalcraft/Game/WidgetInput.cs:WidgetInput.IsKeyDownOnce
            // Enter opens Windows chat only while the game owns keyboard input. Once a dialog is
            // visible, TextBoxDialog keeps its native Enter-to-submit behavior instead.
            bool talkHotkey = OperatingSystem.IsWindows() && commands?.IsInRoom == true &&
                m_componentPlayer?.GameWidget?.Input.IsKeyDownOnce(Key.Enter) == true &&
                !DialogsManager.HasDialogs(m_componentPlayer.GuiWidget);
            if (m_talkButton != null && m_talkButton.IsClicked || talkHotkey)
            {
                if (talkHotkey)
                    m_componentPlayer.GameWidget.Input.Clear();
                commands?.ShowTalkDialog();
            }
            if (m_manageButton != null && m_manageButton.IsClicked)
                commands?.ShowMultiplayerManagementDialog();
        }

        private void AttachButtons()
        {
            GameWidget gameWidget = m_componentPlayer?.GameWidget;
            if (gameWidget == null) return;

            m_moreContents = gameWidget.Children.Find<StackPanelWidget>("MoreContents", true);
            if (m_moreContents == null) return;

            m_createButton = CreateButton("CR", new Color(45, 115, 75));
            m_talkButton = CreateButton("TA", new Color(45, 85, 135));
            m_manageButton = CreateButton("MP", new Color(115, 75, 45));
            m_moreContents.Children.Add(m_createButton);
            m_moreContents.Children.Add(m_talkButton);
            m_moreContents.Children.Add(m_manageButton);
        }

        // Source: Survivalcraft/Game/BevelledButtonWidget.cs:BevelledButtonWidget
        private static BevelledButtonWidget CreateButton(string text, Color centerColor)
        {
            return new BevelledButtonWidget
            {
                Text = text,
                Size = new Vector2(76f, 64f),
                Margin = new Vector2(3f, 0f),
                Color = Color.White,
                CenterColor = centerColor,
                BevelColor = new Color(120, 120, 120)
            };
        }
    }
}

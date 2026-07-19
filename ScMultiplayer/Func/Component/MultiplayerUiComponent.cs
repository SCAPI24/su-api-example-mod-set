using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public class MultiplayerUiComponent : Component, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;
        private StackPanelWidget m_moreContents;
        private BevelledButtonWidget m_createButton;
        private BevelledButtonWidget m_talkButton;

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
            base.Dispose();
        }

        void IUpdateable.Update(float dt)
        {
            // Source: Mod/ConsoleMod/Subsystem/ConsoleSubsystemGameWidgets.cs:AttachConsoleButton
            if (m_moreContents == null) AttachButtons();
            if (m_createButton != null && m_createButton.IsClicked)
                ScMultiplayer.currentInstance?.ShowCreateRoomDialog();
            if (m_talkButton != null && m_talkButton.IsClicked)
                ScMultiplayer.currentInstance?.ShowTalkDialog();
        }

        private void AttachButtons()
        {
            GameWidget gameWidget = m_componentPlayer?.GameWidget;
            if (gameWidget == null) return;

            m_moreContents = gameWidget.Children.Find<StackPanelWidget>("MoreContents", true);
            if (m_moreContents == null) return;

            m_createButton = CreateButton("CR", new Color(45, 115, 75));
            m_talkButton = CreateButton("TA", new Color(45, 85, 135));
            m_moreContents.Children.Add(m_createButton);
            m_moreContents.Children.Add(m_talkButton);
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

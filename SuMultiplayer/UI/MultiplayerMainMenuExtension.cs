using System;
using System.Linq;
using System.Xml.Linq;
using Game;
using SuAPI;

namespace SuMultiplayer.UI
{
    /// <summary>Adds a native-styled multiplayer entry to the existing main menu.</summary>
    public sealed class MultiplayerMainMenuExtension : ScreenExtension<MainMenuScreen>
    {
        private ButtonWidget m_button;

        public MultiplayerMainMenuExtension(MainMenuScreen host) : base(host) { }

        public override void Attached()
        {
            ButtonWidget play = Host.Children.Find<ButtonWidget>("Play");
            ContainerWidget parent = play?.ParentWidget as ContainerWidget;
            XElement layout = ContentManager.Get<XElement>("Screens/MainMenuScreen");
            XElement original = layout.DescendantsAndSelf().FirstOrDefault(e => (string)e.Attribute("Name") == "Play");
            if (parent == null || original == null) return;
            var node = new XElement(original);
            node.SetAttributeValue("Name", "SuMultiplayer.MainMenu");
            node.SetAttributeValue("Text", "MULTIPLAYER");
            m_button = Widget.LoadWidget(Host, node, null) as ButtonWidget;
            if (m_button != null) parent.Children.Add(m_button);
        }

        public override void Update()
        {
            if (m_button?.IsClicked == true)
            {
                ScreensManager.SwitchScreen("Play", SuPlayScreenExtension.MultiplayerMarker);
                base.Update(false);
                return;
            }
            base.Update();
        }

        public override void Detached()
        {
            m_button?.ParentWidget?.Children.Remove(m_button);
            m_button = null;
            base.Detached();
        }
    }
}

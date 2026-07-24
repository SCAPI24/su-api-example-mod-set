using Engine;
using Engine.Graphics;
using Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ScMultiplayer
{
    public class SuNetworkPlayerScreen : Screen
    {
        private readonly CharacterSkinsCache m_characterSkinsCache = new CharacterSkinsCache();
        private readonly PlayerModelWidget m_playerModel;
        private readonly ButtonWidget m_playerClassButton;
        private readonly Widget m_nameTextBox;
        private readonly LabelWidget m_characterSkinLabel;
        private readonly ButtonWidget m_characterSkinButton;
        private readonly LabelWidget m_descriptionLabel;
        private readonly ButtonWidget m_playButton;
        private Action<string, PlayerClass, string> m_completed;
        private PlayerClass m_playerClass;
        private string m_skinName;
        private string m_defaultName;

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.PlayerScreen
        public SuNetworkPlayerScreen()
        {
            XElement node = ContentManager.Get<XElement>("Screens/PlayerScreen");
            LoadContents(this, node);
            m_playerModel = Children.Find<PlayerModelWidget>("Model");
            m_playerClassButton = Children.Find<ButtonWidget>("PlayerClassButton");
            m_nameTextBox = Children.Find<Widget>("Name", true);
            m_characterSkinLabel = Children.Find<LabelWidget>("CharacterSkinLabel");
            m_characterSkinButton = Children.Find<ButtonWidget>("CharacterSkinButton");
            m_descriptionLabel = Children.Find<LabelWidget>("DescriptionLabel");
            m_playButton = Children.Find<ButtonWidget>("PlayButton");
            m_playerModel.CharacterSkinsCache = m_characterSkinsCache;

            Children.Find<ButtonWidget>("AddButton").IsVisible = false;
            Children.Find<ButtonWidget>("AddAnotherButton").IsVisible = false;
            Children.Find<ButtonWidget>("DeleteButton").IsVisible = false;
            Children.Find<ButtonWidget>("ControlsButton").IsVisible = false;
            Children.Find<LabelWidget>("ControlsLabel").IsVisible = false;
        }

        public override void Enter(object[] parameters)
        {
            m_completed = parameters.FirstOrDefault() as Action<string, PlayerClass, string>;
            m_playerClass = PlayerClass.Male;
            RandomizeSkin();
            ResetDefaultName();
        }

        public override void Leave()
        {
            m_characterSkinsCache.Clear();
            m_completed = null;
        }

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.Update
        public override void Update()
        {
            m_characterSkinsCache.GetTexture(m_skinName);
            m_playerModel.PlayerClass = m_playerClass;
            m_playerModel.CharacterSkinName = m_skinName;
            m_playerClassButton.Text = m_playerClass.ToString();
            m_characterSkinLabel.Text = CharacterSkinsManager.GetDisplayName(m_skinName);
            m_descriptionLabel.Text = DatabaseManager.FindValuesDictionaryForComponent(
                DatabaseManager.FindEntityValuesDictionary(
                    m_playerClass == PlayerClass.Male ? "MalePlayer" : "FemalePlayer", true),
                typeof(ComponentCreature)).GetValue<string>("Description");

            if (m_playerClassButton.IsClicked)
            {
                bool isDefaultName = NameText == m_defaultName;
                m_playerClass = m_playerClass == PlayerClass.Male ? PlayerClass.Female : PlayerClass.Male;
                RandomizeSkin();
                if (isDefaultName) ResetDefaultName();
            }
            if (m_characterSkinButton.IsClicked) ShowSkinSelection();
            if (m_playButton.IsClicked && VerifyName()) Complete();
            if (Input.Back || Input.Cancel || Children.Find<ButtonWidget>("TopBar.Back").IsClicked)
            {
                ScMultiplayer.currentInstance?.CancelPendingJoin();
                ScreensManager.SwitchScreen("Play");
            }
        }

        private void Complete()
        {
            Action<string, PlayerClass, string> completed = m_completed;
            string name = NameText.Trim();
            ScreensManager.SwitchScreen("Play");
            completed?.Invoke(name, m_playerClass, m_skinName);
        }

        private bool VerifyName()
        {
            if (PlayerData.VerifyName(NameText.Trim())) return true;
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Invalid Name", "Name must contain 2-14 letters, digits or spaces.", "OK", null, null));
            return false;
        }

        private void RandomizeSkin()
        {
            CharacterSkinsManager.UpdateCharacterSkinsList();
            string[] skins = CharacterSkinsManager.CharacterSkinsNames.Where(name =>
                CharacterSkinsManager.IsBuiltIn(name) &&
                CharacterSkinsManager.GetPlayerClass(name) == m_playerClass).ToArray();
            m_skinName = skins.Length > 0 ? skins[new Engine.Random().Int(0, skins.Length - 1)] :
                CharacterSkinsManager.CharacterSkinsNames.First();
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.ResetName
        private void ResetDefaultName()
        {
            m_defaultName = CharacterSkinsManager.GetDisplayName(m_skinName);
            NameText = m_defaultName;
        }

        private void ShowSkinSelection()
        {
            CharacterSkinsManager.UpdateCharacterSkinsList();
            IEnumerable<string> skins = CharacterSkinsManager.CharacterSkinsNames.Where(name =>
                CharacterSkinsManager.GetPlayerClass(name) == m_playerClass ||
                !CharacterSkinsManager.GetPlayerClass(name).HasValue);
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Select Character Skin", skins, 64f, item =>
                {
                    XElement node = ContentManager.Get<XElement>("Widgets/CharacterSkinItem");
                    ContainerWidget widget = (ContainerWidget)Widget.LoadWidget(this, node, null);
                    string skin = (string)item;
                    Texture2D texture = m_characterSkinsCache.GetTexture(skin);
                    widget.Children.Find<LabelWidget>("CharacterSkinItem.Text").Text =
                        CharacterSkinsManager.GetDisplayName(skin);
                    widget.Children.Find<LabelWidget>("CharacterSkinItem.Details").Text =
                        $"{texture.Width}x{texture.Height}";
                    PlayerModelWidget model = widget.Children.Find<PlayerModelWidget>("CharacterSkinItem.Model");
                    model.PlayerClass = m_playerClass;
                    model.CharacterSkinTexture = texture;
                    return widget;
                }, item =>
                {
                    bool isDefaultName = NameText == m_defaultName;
                    m_skinName = (string)item;
                    if (isDefaultName) ResetDefaultName();
                }));
        }

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.m_nameTextBox
        private string NameText
        {
            get => ScMultiplayer.ModManager.ModParentField.GetParentField<string>(
                m_nameTextBox, "Text", m_nameTextBox.GetType());
            set => ScMultiplayer.ModManager.ModParentField.ModifyParentField(
                m_nameTextBox, "Text", value, m_nameTextBox.GetType());
        }
    }
}

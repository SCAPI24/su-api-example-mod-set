using Game;

namespace ScMultiplayer
{
    public sealed class SuModifyNetWorldScreen : ModifyWorldScreen
    {
        private readonly TextBoxWidgetAccessor m_nameTextBox;
        private readonly LabelWidget m_seedLabel;
        private readonly ButtonWidget m_gameModeButton;
        private readonly ButtonWidget m_worldOptionsButton;
        private readonly LabelWidget m_errorLabel;
        private readonly LabelWidget m_descriptionLabel;
        private readonly ButtonWidget m_applyButton;
        private readonly ButtonWidget m_deleteButton;
        private readonly ButtonWidget m_uploadButton;
        private string m_recordId;

        // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.ModifyWorldScreen
        public SuModifyNetWorldScreen()
        {
            m_nameTextBox = new TextBoxWidgetAccessor(Children.Find<Widget>("Name"));
            m_seedLabel = Children.Find<LabelWidget>("Seed");
            m_gameModeButton = Children.Find<ButtonWidget>("GameMode");
            m_worldOptionsButton = Children.Find<ButtonWidget>("WorldOptions");
            m_errorLabel = Children.Find<LabelWidget>("Error");
            m_descriptionLabel = Children.Find<LabelWidget>("Description");
            m_applyButton = Children.Find<ButtonWidget>("Apply");
            m_deleteButton = Children.Find<ButtonWidget>("Delete");
            m_uploadButton = Children.Find<ButtonWidget>("Upload");
        }

        // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.Enter
        public override void Enter(object[] parameters)
        {
            m_recordId = parameters != null && parameters.Length > 0
                ? parameters[0] as string
                : null;
            PersonalServerRecord record = PersonalServerDirectory.Find(m_recordId);
            // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.Enter
            // Initialize the base class's private WorldSettings before its TextChanged handler can
            // run. The synthetic directory is never passed to WorldsManager by this screen.
            base.Enter(new object[]
            {
                "scmp-personal:" + (m_recordId ?? string.Empty),
                new WorldSettings { Name = record?.Name ?? string.Empty }
            });
            Children.Find<LabelWidget>("TopBar.Label").Text = "Net World";
            m_nameTextBox.MaximumLength = 50;
            m_nameTextBox.Text = record?.Name ?? string.Empty;
            m_nameTextBox.IsEnabled = false;
            m_seedLabel.Text = record?.Address ?? string.Empty;
            m_gameModeButton.Text = "Net World";
            m_gameModeButton.IsEnabled = false;
            m_worldOptionsButton.IsEnabled = false;
            m_errorLabel.IsVisible = false;
            m_descriptionLabel.IsVisible = true;
            m_descriptionLabel.Text = record != null
                ? "Saved personal server address. Online status and ping are checked from the world list."
                : "This personal Net World no longer exists.";
            m_applyButton.IsEnabled = false;
            m_uploadButton.IsEnabled = false;
            m_deleteButton.IsEnabled = record != null;
        }

        // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.Update
        public override void Update()
        {
            PersonalServerRecord record = PersonalServerDirectory.Find(m_recordId);
            m_deleteButton.IsEnabled = record != null;
            if (m_deleteButton.IsClicked && record != null)
                ConfirmDelete(record);
            if (Input.Back || Input.Cancel ||
                Children.Find<ButtonWidget>("TopBar.Back").IsClicked)
                ScreensManager.SwitchScreen("Play");
        }

        // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.Update
        private void ConfirmDelete(PersonalServerRecord record)
        {
            MessageDialog dialog = null;
            dialog = new MessageDialog("Are you sure?",
                $"Remove {record.Name} ({record.Address}) from personal Net Worlds?",
                "Yes", "No", button =>
                {
                    if (button == MessageDialogButton.Button1)
                    {
                        if (PersonalServerDirectory.Remove(record.Id, out string error))
                        {
                            DialogsManager.HideDialog(dialog);
                            ScreensManager.SwitchScreen("Play");
                        }
                        else
                        {
                            DialogsManager.HideDialog(dialog);
                            DialogsManager.ShowDialog(null, new MessageDialog(
                                "Error", error ?? "The personal Net World was not found.",
                                "OK", null, null));
                        }
                    }
                    else
                    {
                        DialogsManager.HideDialog(dialog);
                    }
                });
            dialog.AutoHide = false;
            DialogsManager.ShowDialog(null, dialog);
        }
    }
}

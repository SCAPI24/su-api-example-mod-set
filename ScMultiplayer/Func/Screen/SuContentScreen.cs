using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace ScMultiplayer
{
    internal sealed class TextBoxWidgetAccessor
    {
        private readonly Widget m_widget;
        private readonly PropertyInfo m_textProperty;
        private readonly PropertyInfo m_maximumLengthProperty;

        // Source: Survivalcraft/Game/TextBoxWidget.cs:TextBoxWidget.Text
        public TextBoxWidgetAccessor(Widget widget)
        {
            m_widget = widget ?? throw new ArgumentNullException(nameof(widget));
            Type type = widget.GetType();
            m_textProperty = type.GetProperty("Text", BindingFlags.Public |
                BindingFlags.Instance) ?? throw new InvalidOperationException(
                "TextBoxWidget.Text was not found.");
            m_maximumLengthProperty = type.GetProperty("MaximumLength",
                BindingFlags.Public | BindingFlags.Instance) ??
                throw new InvalidOperationException(
                    "TextBoxWidget.MaximumLength was not found.");
        }

        public string Text
        {
            get => m_textProperty.GetValue(m_widget) as string ?? string.Empty;
            set => m_textProperty.SetValue(m_widget, value ?? string.Empty);
        }

        public int MaximumLength
        {
            set => m_maximumLengthProperty.SetValue(m_widget, value);
        }

        public bool IsEnabled
        {
            set => m_widget.IsEnabled = value;
        }
    }

    public sealed class SuContentScreen : ContentScreen
    {
        private readonly ButtonWidget m_externalContentButton;
        private readonly ButtonWidget m_communityContentButton;
        private readonly ButtonWidget m_linkButton;
        private readonly ButtonWidget m_manageButton;

        // Source: Survivalcraft/Game/ContentScreen.cs:ContentScreen.ContentScreen
        public SuContentScreen()
        {
            m_externalContentButton = Children.Find<ButtonWidget>("External");
            m_communityContentButton = Children.Find<ButtonWidget>("Community");
            m_linkButton = Children.Find<ButtonWidget>("Link");
            m_manageButton = Children.Find<ButtonWidget>("Manage");
        }

        // Source: Survivalcraft/Game/ContentScreen.cs:ContentScreen.Update
        public override void Update()
        {
            m_communityContentButton.IsEnabled =
                SettingsManager.CommunityContentMode != CommunityContentMode.Disabled;
            if (m_externalContentButton.IsClicked)
                ScreensManager.SwitchScreen("ExternalContent");
            if (m_communityContentButton.IsClicked)
                ScreensManager.SwitchScreen("CommunityContent");
            if (m_linkButton.IsClicked)
                DialogsManager.ShowDialog(null, new SuDownloadContentFromLinkDialog());
            if (m_manageButton.IsClicked)
                ScreensManager.SwitchScreen("ManageContent");
            if (Input.Back || Input.Cancel ||
                Children.Find<ButtonWidget>("TopBar.Back").IsClicked)
                ScreensManager.SwitchScreen("MainMenu");
        }
    }

    internal sealed class SuDownloadContentFromLinkDialog : DownloadContentFromLinkDialog
    {
        private readonly TextBoxWidgetAccessor m_linkTextBoxWidget;
        private readonly TextBoxWidgetAccessor m_nameTextBoxWidget;
        private readonly RectangleWidget m_typeIconWidget;
        private readonly LabelWidget m_typeLabelWidget;
        private readonly ButtonWidget m_changeTypeButtonWidget;
        private readonly ButtonWidget m_downloadButtonWidget;
        private readonly ButtonWidget m_cancelButtonWidget;
        private bool m_updateContentName;
        private bool m_updateContentType;
        private ExternalContentType m_type;
        private bool m_isNetWorld;
        private string m_lastLinkText;

        // Source: Survivalcraft/Game/DownloadContentFromLinkDialog.cs:DownloadContentFromLinkDialog.DownloadContentFromLinkDialog
        public SuDownloadContentFromLinkDialog()
        {
            m_linkTextBoxWidget = new TextBoxWidgetAccessor(Children.Find<Widget>(
                "DownloadContentFromLinkDialog.Link"));
            m_nameTextBoxWidget = new TextBoxWidgetAccessor(Children.Find<Widget>(
                "DownloadContentFromLinkDialog.Name"));
            m_typeIconWidget = Children.Find<RectangleWidget>(
                "DownloadContentFromLinkDialog.TypeIcon");
            m_typeLabelWidget = Children.Find<LabelWidget>(
                "DownloadContentFromLinkDialog.Type");
            m_changeTypeButtonWidget = Children.Find<ButtonWidget>(
                "DownloadContentFromLinkDialog.ChangeType");
            m_downloadButtonWidget = Children.Find<ButtonWidget>(
                "DownloadContentFromLinkDialog.Download");
            m_cancelButtonWidget = Children.Find<ButtonWidget>(
                "DownloadContentFromLinkDialog.Cancel");
            m_lastLinkText = m_linkTextBoxWidget.Text;
        }

        private void DetectLinkTextChange()
        {
            string link = m_linkTextBoxWidget.Text;
            if (!string.Equals(link, m_lastLinkText, StringComparison.Ordinal))
            {
                m_lastLinkText = link;
                m_updateContentName = true;
                if (!m_isNetWorld)
                    m_updateContentType = true;
            }
        }

        // Source: Survivalcraft/Game/DownloadContentFromLinkDialog.cs:DownloadContentFromLinkDialog.Update
        public override void Update()
        {
            DetectLinkTextChange();
            string link = m_linkTextBoxWidget.Text.Trim();
            string name = m_nameTextBoxWidget.Text.Trim();
            m_typeLabelWidget.Text = m_isNetWorld
                ? "Net World"
                : ExternalContentManager.GetEntryTypeDescription(m_type);
            m_typeIconWidget.Subtexture = ExternalContentManager.GetEntryTypeIcon(
                m_isNetWorld ? ExternalContentType.World : m_type);

            bool requiresName = m_isNetWorld ||
                ExternalContentManager.DoesEntryTypeRequireName(m_type);
            if (requiresName)
            {
                m_nameTextBoxWidget.IsEnabled = true;
                m_downloadButtonWidget.IsEnabled = link.Length > 0 && name.Length > 0 &&
                    (m_isNetWorld || m_type != ExternalContentType.Unknown);
                if (m_updateContentName)
                {
                    if (!m_isNetWorld || m_nameTextBoxWidget.Text.Trim().Length == 0)
                        m_nameTextBoxWidget.Text = m_isNetWorld
                            ? GetNetWorldName(link)
                            : GetNameFromLink(link);
                    m_updateContentName = false;
                }
            }
            else
            {
                m_nameTextBoxWidget.IsEnabled = false;
                m_nameTextBoxWidget.Text = string.Empty;
                m_downloadButtonWidget.IsEnabled = link.Length > 0 &&
                    m_type != ExternalContentType.Unknown;
            }
            if (m_updateContentType)
            {
                m_type = GetTypeFromLink(link);
                m_updateContentType = false;
            }

            if (m_changeTypeButtonWidget.IsClicked)
            {
                DialogsManager.ShowDialog(ParentWidget,
                    new SuSelectExternalContentTypeDialog("Select Content Type", item =>
                    {
                        m_isNetWorld = item.IsNetWorld;
                        m_type = item.Type;
                        m_updateContentType = false;
                        m_updateContentName = true;
                    }));
            }
            else if (Input.Cancel || m_cancelButtonWidget.IsClicked)
            {
                DialogsManager.HideDialog(this);
            }
            else if (m_downloadButtonWidget.IsClicked)
            {
                if (m_isNetWorld)
                    SaveNetWorld(link, name);
                else
                    DownloadExternalContent(link, name);
            }
        }

        // Source: Survivalcraft/Game/DownloadContentFromLinkDialog.cs:DownloadContentFromLinkDialog.Update
        private void SaveNetWorld(string link, string name)
        {
            if (PersonalServerDirectory.TryAddOrUpdate(link, name, out _,
                out string error))
            {
                DialogsManager.HideDialog(this);
                return;
            }
            DialogsManager.ShowDialog(ParentWidget, new MessageDialog(
                "Error", error ?? "Unable to save the personal Net World.",
                "OK", null, null));
        }

        // Source: Survivalcraft/Game/DownloadContentFromLinkDialog.cs:DownloadContentFromLinkDialog.Update
        private void DownloadExternalContent(string link, string name)
        {
            var busyDialog = new CancellableBusyDialog("Downloading",
                autoHideOnCancel: false);
            DialogsManager.ShowDialog(ParentWidget, busyDialog);
            WebManager.Get(link, null, null, busyDialog.Progress, data =>
            {
                ExternalContentManager.ImportExternalContent(new MemoryStream(data), m_type,
                    name, delegate
                    {
                        DialogsManager.HideDialog(busyDialog);
                        DialogsManager.HideDialog(this);
                    }, error =>
                    {
                        DialogsManager.HideDialog(busyDialog);
                        DialogsManager.ShowDialog(ParentWidget, new MessageDialog(
                            "Error", error.Message, "OK", null, null));
                    });
            }, error =>
            {
                DialogsManager.HideDialog(busyDialog);
                DialogsManager.ShowDialog(ParentWidget, new MessageDialog(
                    "Error", error.Message, "OK", null, null));
            });
        }

        // Source: Survivalcraft/Game/DownloadContentFromLinkDialog.cs:DownloadContentFromLinkDialog.UnclutterLink
        private static string UnclutterLink(string address)
        {
            try
            {
                string text = address;
                int ampersand = text.IndexOf('&');
                if (ampersand > 0) text = text.Remove(ampersand);
                int question = text.IndexOf('?');
                if (question > 0) text = text.Remove(question);
                return Uri.UnescapeDataString(text);
            }
            catch
            {
                return string.Empty;
            }
        }

        // Source: Survivalcraft/Game/DownloadContentFromLinkDialog.cs:DownloadContentFromLinkDialog.GetNameFromLink
        private static string GetNameFromLink(string address)
        {
            try
            {
                return Storage.GetFileNameWithoutExtension(UnclutterLink(address));
            }
            catch
            {
                return string.Empty;
            }
        }

        // Source: Survivalcraft/Game/DownloadContentFromLinkDialog.cs:DownloadContentFromLinkDialog.GetTypeFromLink
        private static ExternalContentType GetTypeFromLink(string address)
        {
            try
            {
                return ExternalContentManager.ExtensionToType(
                    Storage.GetExtension(UnclutterLink(address)));
            }
            catch
            {
                return ExternalContentType.Unknown;
            }
        }

        // Source: Mod/ScMultiplayer/Networking/PersonalServerDirectory.cs:PersonalServerDirectory.TryNormalizeAddress
        private static string GetNetWorldName(string address)
        {
            return PersonalServerDirectory.TryNormalizeAddress(address,
                out string normalizedAddress, out _)
                ? normalizedAddress
                : address?.Trim() ?? string.Empty;
        }
    }

    internal sealed class SuSelectExternalContentTypeDialog : ListSelectionDialog
    {
        internal sealed class SelectionItem
        {
            public ExternalContentType Type { get; }

            public bool IsNetWorld { get; }

            public SelectionItem(ExternalContentType type, bool isNetWorld)
            {
                Type = type;
                IsNetWorld = isNetWorld;
            }
        }

        // Source: Survivalcraft/Game/SelectExternalContentTypeDialog.cs:SelectExternalContentTypeDialog.SelectExternalContentTypeDialog
        public SuSelectExternalContentTypeDialog(string title,
            Action<SelectionItem> selectionHandler)
            : base(title, CreateItems(), 64f, item => CreateItemWidget((SelectionItem)item),
                item => selectionHandler((SelectionItem)item))
        {
        }

        // Source: Survivalcraft/Game/SelectExternalContentTypeDialog.cs:SelectExternalContentTypeDialog.SelectExternalContentTypeDialog
        private static IEnumerable<SelectionItem> CreateItems()
        {
            foreach (object value in EnumUtils.GetEnumValues(typeof(ExternalContentType)))
            {
                var type = (ExternalContentType)value;
                if (ExternalContentManager.IsEntryTypeDownloadSupported(type))
                    yield return new SelectionItem(type, false);
            }
            yield return new SelectionItem(ExternalContentType.World, true);
        }

        // Source: Survivalcraft/Game/SelectExternalContentTypeDialog.cs:SelectExternalContentTypeDialog.SelectExternalContentTypeDialog
        private static Widget CreateItemWidget(SelectionItem item)
        {
            XElement node = ContentManager.Get<XElement>(
                "Widgets/SelectExternalContentTypeItem");
            var container = (ContainerWidget)Widget.LoadWidget(null, node, null);
            container.Children.Find<RectangleWidget>("SelectExternalContentType.Icon")
                .Subtexture = ExternalContentManager.GetEntryTypeIcon(
                    item.IsNetWorld ? ExternalContentType.World : item.Type);
            container.Children.Find<LabelWidget>("SelectExternalContentType.Text").Text =
                item.IsNetWorld
                    ? "Net World"
                    : ExternalContentManager.GetEntryTypeDescription(item.Type);
            return container;
        }
    }
}

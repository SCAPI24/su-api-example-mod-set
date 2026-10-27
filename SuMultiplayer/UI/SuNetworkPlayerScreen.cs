using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Engine;
using Engine.Graphics;
using Game;
using SuAPI;
using SuMultiplayer.Players;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 首次加入"没有角色"时用的角色创建屏 —— 旧实现 <c>SuNetworkPlayerScreen</c> 的逐行移植。
    /// 入口（唯一正确的那一个）：
    ///   主机在 Hello 里判定这个身份没有角色记录、客户端也没带完整资料 → HelloAck.profile_required
    ///   → 联机角色模块打开本屏 → 玩家选名字/性别/皮肤 → 回调写进资料存储并**重新握手**。
    ///
    /// Source: Mod/ScMultiplayer/Func/Screen/SuNetworkPlayerScreen.cs:1-161
    /// Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen（屏幕布局与名字校验的宿主实现）
    /// </summary>
    public sealed class SuNetworkPlayerScreen : Screen
    {
        private readonly IModParentField m_parentField;
        private readonly CharacterNameRoller m_nameRoller;
        private readonly Action m_cancelled;
        /// <summary>Complete 真正返回 Play 之前调用（Session 用它保留本次加入的连接）。</summary>
        private readonly Action m_aboutToReturnToPlay;
        /// <summary>构造时固定的完成回调（组合根直接接联机角色模块）；Enter 参数可覆盖。</summary>
        private readonly Action<PlayerSkinChangeRequest> m_constructedCompleted;
        private readonly CharacterSkinsCache m_characterSkinsCache = new CharacterSkinsCache();
        private readonly PlayerModelWidget m_playerModel;
        private readonly ButtonWidget m_playerClassButton;
        private readonly Widget m_nameTextBox;
        private readonly LabelWidget m_characterSkinLabel;
        private readonly ButtonWidget m_characterSkinButton;
        private readonly LabelWidget m_descriptionLabel;
        private readonly ButtonWidget m_playButton;
        private Action<PlayerSkinChangeRequest> m_completed;
        private PlayerClass m_playerClass;
        private string m_skinName;
        private string m_defaultName;
        /// <summary>主机 + 名册里已知的会话名字：摇默认名字时用来避开重名（模块传入，不含本机旧名字）。</summary>
        private string[] m_knownSessionNames = Array.Empty<string>();

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.PlayerScreen
        public SuNetworkPlayerScreen(IModParentField parentField, CharacterNameRoller nameRoller,
            Action cancelled = null, Action aboutToReturnToPlay = null,
            Action<PlayerSkinChangeRequest> completed = null)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_nameRoller = nameRoller ?? throw new ArgumentNullException(nameof(nameRoller));
            m_cancelled = cancelled;
            m_aboutToReturnToPlay = aboutToReturnToPlay;
            m_constructedCompleted = completed;
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
            var fromParameters = parameters?.FirstOrDefault() as Action<PlayerSkinChangeRequest>;
            m_completed = fromParameters ?? m_constructedCompleted;
            // 第二个参数 = 当前会话里已知的名字（模块收集：主机 + 名册里已经出现的人）。
            m_knownSessionNames = parameters != null && parameters.Length > 1 &&
                parameters[1] is string[] names
                ? names
                : Array.Empty<string>();
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
                // 取消加入（旧实现 CancelPendingJoin）：回 Play 屏并停掉本次通道。
                ScreensManager.SwitchScreen("Play");
                m_cancelled?.Invoke();
            }
        }

        private void Complete()
        {
            Action<PlayerSkinChangeRequest> completed = m_completed;
            string name = NameText.Trim();
            // 自定义皮肤：在离开屏幕前把资源读出来并**完整校验**。失败就留在本屏并明确提示，
            // 绝不把"列表里选过"当成资源可用（也不换成内置皮肤提交）。
            byte[] customAsset = Array.Empty<byte>();
            if (SkinAssetPolicy.IsCustomSkinName(m_skinName))
            {
                if (!CharacterSkinCatalog.TryReadCustomAsset(m_skinName,
                        out customAsset, out string assetReason))
                {
                    global::Engine.Log.Warning("[SuMP][skin] custom skin unavailable: " + m_skinName + " · " + assetReason);
                    ShowError("Custom skin unavailable",
                        "Cannot use custom skin \"" + m_skinName + "\": invalid or unreadable skin file.");
                    return;
                }
            }
            else if (!CharacterSkinsManager.IsBuiltIn(m_skinName))
            {
                ShowError("Skin unavailable", "Skin \"" + m_skinName + "\" is not an available built-in skin.");
                return;
            }
            // 返回 Play 之前先让 Session 知道"这是同一次加入的继续"：
            // Play 屏 Enter 会据此跳过 LeaveRoom，否则会停掉连接、永远收不到世界。
            m_aboutToReturnToPlay?.Invoke();
            // 再切回 Play，最后执行回调（旧实现顺序一致：回调里会重新 JoinGame）。
            ScreensManager.SwitchScreen("Play");
            completed?.Invoke(new PlayerSkinChangeRequest
            {
                Name = name,
                Sex = (PlayerSex)(int)m_playerClass,
                SkinName = m_skinName,
                SkinAsset = customAsset,
                Source = "character-screen"
            });
        }

        private void ShowError(string title, string message)
        {
            DialogsManager.ShowDialog(null, new MessageDialog(title, message, "OK", null, null));
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
            m_skinName = skins.Length > 0
                ? skins[new global::Engine.Random().Int(0, skins.Length - 1)]
                : CharacterSkinsManager.CharacterSkinsNames.First();
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.ResetName
        // 引擎自己的生成方式 = 当前皮肤的显示名（Walter/Basil/Geoffrey/...）。
        // 本重写版改成**按身份稳定键 + 会话盐**摇游戏内置名字表里的名字：
        //   旧写法与移植版都从同一 RNG 状态出发，两台客户端会摇到同一个名字（实机：都叫 Geoffrey）。
        // 身份不可用时才回退到引擎生成，并且由 CharacterNameRoller 显式写 Warning（不静默）。
        private void ResetDefaultName()
        {
            string identity = PlayerIdentity.LocalIdentity();
            CharacterNameRollResult result = m_nameRoller.Roll(
                identity,
                BuiltInNameTable(),
                m_knownSessionNames,
                CharacterSkinsManager.GetDisplayName(m_skinName),
                engineFallbackReason: identity.Length == 0
                    ? "身份不可用：UserManager.ActiveUser 为空或 UniqueId 为空"
                    : string.Empty,
                subject: PlayerIdentity.Shorten(identity));
            m_defaultName = result.Name;
            NameText = m_defaultName;
        }

        /// <summary>
        /// 游戏自带的**内置名字表**：不自己维护名字列表，直接取引擎内置皮肤
        /// <c>$Male1..4 / $Female1..4</c> 的显示名（Walter/Basil/Geoffrey/Zachary/Doris/Mabel/Ada/Shirley）。
        /// Source: Survivalcraft/Game/CharacterSkinsManager.cs:58-93（GetDisplayName）
        /// Source: Survivalcraft/Game/CharacterSkinsManager.cs:175-193（UpdateCharacterSkinsList 的内置皮肤集合）
        /// </summary>
        private static string[] BuiltInNameTable()
        {
            CharacterSkinsManager.UpdateCharacterSkinsList();
            var names = new List<string>();
            foreach (string skin in CharacterSkinsManager.CharacterSkinsNames)
            {
                if (!CharacterSkinsManager.IsBuiltIn(skin)) continue;
                string displayName = CharacterSkinsManager.GetDisplayName(skin);
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                if (!names.Contains(displayName)) names.Add(displayName);
            }
            return names.ToArray();
        }

        /// <summary>
        /// 皮肤列表：内置 + **本机自定义皮肤**（后者由 CharacterSkinCatalog 只读校验后列出）。
        /// 选中自定义皮肤时，<see cref="Complete"/> 会再读一次资源并交给模块随首次加入上传。
        /// Source: Mod/ScMultiplayer/Func/Screen/SuNetworkPlayerScreen.cs:123-150（旧实现列出全部皮肤）
        /// </summary>
        private void ShowSkinSelection()
        {
            string[] skins = CharacterSkinCatalog.NamesForClass(m_playerClass, includeCustom: true);
            if (skins.Length == 0)
            {
                ShowError("No skins", "No available " + m_playerClass + " skins on this device.");
                return;
            }
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
                        $"{texture.Width}x{texture.Height}" +
                        (CharacterSkinsManager.IsBuiltIn(skin) ? string.Empty : " | Custom");
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
            get => m_parentField.GetParentField<string>(m_nameTextBox, "Text", m_nameTextBox.GetType());
            set => m_parentField.ModifyParentField(m_nameTextBox, "Text", value, m_nameTextBox.GetType());
        }
    }
}

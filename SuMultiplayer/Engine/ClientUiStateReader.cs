using System;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端界面层的**只读采集**（<see cref="IClientUiStateSource"/> 的实现）。
    ///
    /// 每帧只读四类引擎事实（全部是 getter，不改任何状态、不调用任何交互方法）：
    ///   ① 输入形态：本机玩家 <c>PlayerData.InputDevice</c>（用户选的设备组合）+ 活动的
    ///      <c>ComponentInput.IsControlledByTouch</c>（引擎真正用来决定触摸控件显隐的开关）。
    ///      Source: Survivalcraft/Game/ComponentInput.cs:30；
    ///      Source: Survivalcraft/Game/ComponentGui.cs:395-419（唯一消费者）。
    ///   ② HUD 显隐：视图自己的 HUD 根 <c>ComponentGui.ControlsContainerWidget.IsVisible</c>
    ///      （= IsReadyForPlaying && 相机可操控 && 不困；Source: ComponentGui.cs:395），
    ///      再与"当前是否在游戏屏"相与 —— 客户端在设置 / 帮助等屏幕时游戏 HUD 本来就不在屏幕上。
    ///   ③ 打开的界面：
    ///      · 模态面板 = <c>ComponentGui.ModalPanelWidget</c> 的类名
    ///        （背包 / 创造背包 / 衣物 / 箱子 / 熔炉 / 工作台 / 分配器；
    ///        Source: ComponentGui.cs:536-559, SubsystemChestBlockBehavior.cs:53,
    ///        SubsystemFurnaceBlockBehavior.cs:84, SubsystemCraftingTableBlockBehavior.cs:42,
    ///        SubsystemDispenserBlockBehavior.cs:61）；
    ///      · 对话框 = <c>DialogsManager.Dialogs</c> 里父窗口正好是本机玩家 <c>GuiWidget</c>
    ///        的那一个（取最上面一层；Source: DialogsManager.cs:32-64）。
    ///   ④ 当前屏幕 = <c>ScreensManager.CurrentScreen</c> 的类名（Source: ScreensManager.cs:46）。
    ///
    /// 聊天输入行由组合根以委托注入（mod 自己的 ChatOverlay 状态），本类不反向引用 UI 类型。
    ///
    /// 采集范围 = 本机**主角色（槽位 0）**：主机跟随某个客户端的分屏视图同样只绑定槽位 0
    /// （Engine/ClientViewEngine.cs 的候选过滤 / Engine/MirrorCharacterEngine.cs 的目标）。
    ///
    /// 无静态状态；不写引擎字段；不读网络。
    /// </summary>
    public sealed class ClientUiStateReader : IClientUiStateSource
    {
        private readonly NetworkAvatarSpawner m_avatars;
        private readonly Func<bool> m_chatInputOpen;

        public ClientUiStateReader(NetworkAvatarSpawner avatars, Func<bool> chatInputOpen)
        {
            m_avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            m_chatInputOpen = chatInputOpen;
        }

        public bool TryCapture(out ClientUiStateData state, out string reason)
        {
            state = default;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            if (!m_avatars.TryGetLocalPlayer(project, out PlayerData local, out reason))
                return false;
            ComponentPlayer player = local.ComponentPlayer;
            if (player == null)
            {
                reason = "no-component-player";
                return false;
            }
            ComponentGui gui = player.ComponentGui;
            ComponentInput input = player.ComponentInput;
            if (gui == null || input == null)
            {
                reason = "no-gui-or-input";
                return false;
            }
            Screen screen = ScreensManager.CurrentScreen;
            if (screen == null)
            {
                // 屏幕切换的一瞬间 CurrentScreen 可能为空：本帧不发送，下一帧再采（不猜）。
                reason = "no-current-screen";
                return false;
            }
            string screenName = screen.GetType().Name;
            if (!UiStatePolicy.TryMapInputKind((uint)local.InputDevice,
                    input.IsControlledByTouch, out UiInputKind inputKind, out reason))
                return false;

            bool dialogPresent = false;
            string dialogType = string.Empty;
            ContainerWidget guiWidget = player.GuiWidget;
            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            for (int i = 0; i < dialogs.Count; i++)
            {
                Dialog dialog = dialogs[i];
                if (dialog == null) continue;
                if (!ReferenceEquals(dialog.ParentWidget, guiWidget)) continue;
                // 列表末尾 = 最后打开的那一个（DialogsManager 按打开顺序追加）。
                dialogPresent = true;
                dialogType = dialog.GetType().Name;
            }

            Widget modal = gui.ModalPanelWidget;
            bool modalPresent = modal != null;
            string modalType = modalPresent ? modal.GetType().Name : string.Empty;

            bool hudVisible = gui.ControlsContainerWidget.IsVisible &&
                string.Equals(screenName, UiStatePolicy.GameScreenName, StringComparison.Ordinal);
            bool chatInputOpen = m_chatInputOpen != null && m_chatInputOpen();

            state = new ClientUiStateData(inputKind, hudVisible, screenName, dialogPresent,
                dialogType, modalPresent, modalType, chatInputOpen);
            return true;
        }
    }
}

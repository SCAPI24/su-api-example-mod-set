using System;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端界面层的**只读采集**（<see cref="IClientUiStateSource"/> 的实现）。
    ///
    /// 每帧只读这些引擎事实（全部是 getter，不改任何状态、不调用任何交互方法）：
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
    ///   ⑤ 底部冒泡消息 = <c>ComponentGui.m_messageWidget.m_messages</c>（只读投影，含已经显示
    ///      多久；Source: MessageWidget.cs:12-39,70-111）——第三屏要按同一时限显示 / 消失；
    ///   ⑥ 屏幕覆盖文字 = <c>ComponentScreenOverlays.FloatingMessage / Message</c>（含 factor，
    ///      Source: ComponentScreenOverlays.cs:46-52,119-126）；
    ///   ⑦ 手工制作网格 = 背包面板左半绑定的 <c>ComponentCraftingTable</c> 槽位
    ///      （Source: FullInventoryWidget.cs:35-47）——结果槽由主机用引擎配方自己重算，不在这里读。
    ///   ⑧ 本机视角模式 = 本机 <c>GameWidget.ActiveCamera</c> 的实际类型（Fpp / Tpp / Orbit / Fixed
    ///      → 1..4；死亡相机与引擎其它相机 = 0 不改变，Source: ComponentGui.cs:645-670）。
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
        private readonly UiMirrorMessageAccess m_messageAccess;

        public ClientUiStateReader(NetworkAvatarSpawner avatars, IModParentField parentField,
            Func<bool> chatInputOpen)
        {
            m_avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            m_chatInputOpen = chatInputOpen;
            m_messageAccess = new UiMirrorMessageAccess(
                parentField ?? throw new ArgumentNullException(nameof(parentField)));
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

            // ---- ⑧ 本机视角模式（第三屏按它选 Fpp / Tpp / Orbit / Fixed；只读本机 GameWidget.ActiveCamera）
            // 引擎的 V 键 / 视角按钮在 Fpp → Tpp → Orbit → Fixed 之间循环
            // （Source: Survivalcraft/Game/ComponentGui.cs:645-670）；四种相机按**实际类型**映射
            // 1..4，其余（死亡相机 / Intro / Loading）上报 0 = 主机不改变，保留最后已知模式
            // （复活后就回到它）。
            Camera activeCamera = player.GameWidget.ActiveCamera;
            UiCameraMode cameraMode = (UiCameraMode)ViewCameraPolicy.ModeOfCameraTypes(
                activeCamera is FppCamera, activeCamera is TppCamera,
                activeCamera is OrbitCamera, activeCamera is FixedCamera);

            // ---- ⑤ 底部冒泡消息（MessageWidget 私有列表的只读投影；age 用于主机对齐时限）
            if (!m_messageAccess.TryCapture(gui, Time.FrameStartTime,
                    out UiSmallMessageData[] smallMessages, out string messageReason))
            {
                reason = "messages-" + messageReason;
                return false;
            }

            // ---- ⑥ 屏幕覆盖文字（引擎每拍重置，这里读的是本帧各组件刚写入的值）
            ComponentScreenOverlays overlays = player.ComponentScreenOverlays;
            if (overlays == null)
            {
                reason = "no-ComponentScreenOverlays";
                return false;
            }
            string floatingMessage = overlays.FloatingMessage ?? string.Empty;
            float floatingFactor = overlays.FloatingMessageFactor;
            string overlayMessage = overlays.Message ?? string.Empty;
            float overlayFactor = overlays.MessageFactor;

            // ---- ⑦ 手工制作网格（只有 FullInventoryWidget 左半需要；结果槽由主机自己推导）
            bool handcraftPresent = false;
            UiHandcraftData handcraft = default;
            if (UiStateMirrorPlan.IsHandcraftModal(modalPresent, modalType))
            {
                ComponentCraftingTable table = player.Entity.FindComponent<ComponentCraftingTable>();
                if (table == null)
                {
                    // 引擎里 FullInventoryWidget 的构造必须带 ComponentCraftingTable
                    // （ComponentGui.cs:547），所以这里缺失就是引擎状态异常：明确失败，不猜空网格。
                    reason = "no-ComponentCraftingTable-for-handcraft";
                    return false;
                }
                int slots = table.SlotsCount;
                if (slots <= 0 || slots > UiStatePolicy.MaxHandcraftSlots)
                {
                    reason = "handcraft-slots-out-of-range(" + slots + ")";
                    return false;
                }
                var values = new int[slots];
                var counts = new int[slots];
                for (int i = 0; i < slots; i++)
                {
                    values[i] = table.GetSlotValue(i);
                    counts[i] = table.GetSlotCount(i);
                }
                handcraftPresent = true;
                handcraft = new UiHandcraftData(values, counts);
            }

            state = new ClientUiStateData(inputKind, hudVisible, screenName, dialogPresent,
                dialogType, modalPresent, modalType, chatInputOpen, smallMessages,
                floatingMessage, floatingFactor, overlayMessage, overlayFactor,
                handcraftPresent, handcraft, cameraMode);
            return true;
        }
    }
}

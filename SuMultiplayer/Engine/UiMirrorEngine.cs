using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// 主机侧把客户端界面层施加到**跟随该客户端的分屏视图**上
    /// （<see cref="IUiMirrorPort"/> 的实现）。
    ///
    /// 视图解析（引擎自己的绑定，不猜）：遍历 <c>SubsystemGameWidgets.GameWidgets</c>，取
    /// <c>GameWidget.Target == 客户端化身的 ComponentPlayer</c> 的那一个。自动客户端视图
    /// （Engine/ClientViewEngine.cs:TryCreate 的 <c>gameWidget.Target = remotePlayer</c>）与
    /// H3 replace 模式（Engine/MirrorCharacterEngine.cs:BindViewToSource）都建立同一绑定，
    /// 所以这里只有一条规则；两人同时跟随同一客户端时第一个视图生效（计数 duplicateViews）。
    ///
    /// 施加内容（只碰视图自己的东西，**绝不写客户端化身**）：
    ///   ① 输入形态：写视图本机玩家的 <c>ComponentInput.IsControlledByTouch</c>（引擎里
    ///      "显示不显示触摸控件 / 侧栏滑出"的唯一开关；Source: ComponentGui.cs:395-419）。
    ///      **不改** <c>PlayerData.InputDevice</c>：它是存盘字段（PlayerData.cs:388,401），
    ///      并且参与 <c>GameWidget.DetermineInputDevices</c> 对**全部本机玩家**的设备划分
    ///      （GameWidget.cs:122-163）——改它会把主机自己视图的键鼠分走。
    ///      **不重建 GameWidget 输入面**：每个视图的 <c>WidgetInput.Devices</c> 每帧由引擎
    ///      从本机全部玩家派生并恒含 <c>Touch</c>（GameWidget.cs:143-162），所以不存在
    ///      "设备列表变了要重建输入面"的情况（计数 uiInputSurfaceRebuilds 恒为 0）。
    ///   ② HUD 显隐：切换视图的 <c>GameWidget.GuiWidget.IsVisible</c>（视图自己的 HUD 根；
    ///      引擎每帧只重写其子节点 <c>ControlsContainer.IsVisible</c>，不会覆盖这个开关）。
    ///   ③ 打开的界面：用**引擎自己的 widget 类**围绕主机侧已同步的数据实例化，挂进视图
    ///      GuiWidget 里由本类拥有的 CanvasWidget（居中），关闭时摘除并 Dispose。整棵子树
    ///      <c>IsHitTestVisible=false</c> —— 纯显示，主机点不到、拖不动，因此不会产生任何
    ///      主机侧 / 客户端侧编辑。
    ///      · 背包 <c>FullInventoryWidget</c>：化身背包 + 化身 <c>ComponentCraftingTable</c>；
    ///      · 创造背包 <c>CreativeInventoryWidget</c>：化身实体（创造世界模板才有该组件）；
    ///      · 衣物 <c>ClothingWidget</c>：化身 ComponentPlayer；
    ///      · 容器（箱子 / 熔炉 / 工作台 / 分配器）：玩家背包 + 主机**权威方块实体**库存
    ///        （坐标来自被该客户端打开的容器记录，见 <see cref="IUiContainerCellPort"/>）。
    ///      数据口径：物品槽只有前 10 格实时同步（PlayerInventoryMath.MaxSyncSlots，
    ///      Source: Net/PlayerInventoryNet.cs:376-381），其余槽位是主机侧化身的最近记录值 ——
    ///      快捷栏精确、整包其余槽位可能滞后（不伪造、不藏起来，见报告）。
    ///   ④ 对话框：只实例化能从类名重建的引擎对话框（GameMenuDialog / KeyboardHelpDialog /
    ///      GamepadHelpDialog）。GameMenuDialog 构造时会按"设备快 / 慢"弹一次性能提示
    ///      （Source: GameMenuDialog.cs:24-40，静态一次性标志 + Analytics）；镜像构造期间用
    ///      SuAPI 的静态字段入口临时置位并还原，避免镜像动作在主机上弹窗 / 记事件。
    ///      构造失败 / 类型不在白名单 → 明确计数（UnsupportedDialog），绝不摆假框。
    ///   ⑤ 全屏 Screen / 聊天输入行：不支持（原因见 UiStateSyncModule 文档），只计数。
    ///
    /// 所有改动都在 <see cref="ViewMirror"/> 里记了原值，ResetClient / ResetAll 时恢复：
    /// IsControlledByTouch、GuiWidget.IsVisible、我们挂上去的 widget 全部摘除并 Dispose。
    ///
    /// 无静态可变状态；反射入口与端口由组合根注入。
    /// </summary>
    public sealed class UiMirrorEngine : IUiMirrorPort
    {
        private const string OwnerId = "ui-mirror";
        private const int ProblemLogMs = 10000;

        private sealed class ViewMirror
        {
            public int ClientId;
            public Project Project;
            public GameWidget GameWidget;
            public PlayerData ViewPlayerData;
            public ComponentPlayer Remote;
            public ComponentInput Input;
            public ContainerWidget Gui;
            public bool OriginCaptured;
            public bool OriginControlledByTouch;
            public bool OriginGuiVisible;
            public CanvasWidget Overlay;
            public Widget ModalWidget;
            public UiModalKind ModalKind;
            public string ModalType = string.Empty;
            public int ModalCellX;
            public int ModalCellY;
            public int ModalCellZ;
            public Dialog DialogWidget;
            public UiDialogKind DialogKind;
            public string DialogType = string.Empty;
            public string UnsupportedDialogType = string.Empty;
            public string UnsupportedModalType = string.Empty;
            public string UnsupportedScreen = string.Empty;
            public UiInputKind AppliedInput = UiInputKind.Unknown;
            public bool AppliedHud = true;
            public string AppliedScreen = string.Empty;
            public int DuplicateViews;
            /// <summary>
            /// 视图玩家是否是一个**真正的本机玩家**（在 SubsystemPlayers.PlayersData 里）。
            /// false = 自动客户端分屏的 camera-only 壳玩家：它的组件从不更新，所以
            /// ComponentGui 不会把 ControlsContainer 设为可见 → 这个视图本来就没有 HUD，
            /// 输入形态 / HUD 显隐的镜像对它没有可见效果（计数 uiMirrorViewsWithoutHud，
            /// 打开的界面仍然照常镜像）。
            /// </summary>
            public bool HudIsLive;
            public bool HasDesired;
            public ClientUiStateData Desired;
        }

        private readonly NetworkAvatarSpawner m_avatars;
        private readonly IUiContainerCellPort m_containers;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly MethodInfo m_setControlledByTouch;
        private readonly string m_inputSwitchError;
        private readonly Dictionary<int, ViewMirror> m_views = new Dictionary<int, ViewMirror>();
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastWarnMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        // ---------------------------------------------------------------- 计数
        private long m_applied;
        private long m_noView;
        private long m_duplicateViews;
        private long m_viewsWithoutHud;
        private long m_inputWritesWithoutHud;
        private long m_buildFailures;
        private long m_containerMissing;
        private long m_inputSwitchFailures;
        private long m_dialogOpened;
        private long m_modalOpened;
        private long m_closed;
        private long m_restores;
        private long m_hudHidden;
        private long m_restoreFailures;
        /// <summary>
        /// 恒为 0：本类**从不**重建 GameWidget 输入面（见类文档 ①）。保留为状态行字段，
        /// 让"没有重建"这件事在日志里可核对，而不是靠阅读代码推断。
        /// </summary>
        private const long InputSurfaceRebuilds = 0;
        private const long InputSurfaceRebuildRequestsRejected = 0;

        public UiMirrorEngine(NetworkAvatarSpawner avatars, IUiContainerCellPort containers,
            IModParentField parentField, IModuleDiagnostics diagnostics)
        {
            m_avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            m_containers = containers;
            m_parentField = parentField;
            m_diagnostics = diagnostics;
            PropertyInfo property = typeof(ComponentInput).GetProperty("IsControlledByTouch",
                BindingFlags.Instance | BindingFlags.Public);
            m_setControlledByTouch = property?.GetSetMethod(true);
            if (m_setControlledByTouch == null)
            {
                m_inputSwitchError = "引擎缺少 ComponentInput.IsControlledByTouch 的 setter";
                Warn("input-switch-missing",
                    "UI 镜像： " + m_inputSwitchError + "（Source: ComponentInput.cs:30）→ " +
                    "输入形态无法镜像；HUD / 打开的界面仍然镜像（每次施加显式计数，不静默）");
            }
            if (m_containers == null)
            {
                Warn("container-port-missing",
                    "UI 镜像：没有容器坐标端口（block-use 模块未装配）→ 箱子 / 熔炉 / " +
                    "工作台 / 分配器面板会显式计为 unsupported（不打开空面板）");
            }
        }

        // ================================================================= 施加

        public bool TryApply(int clientId, in ClientUiStateData state, out UiMirrorOutcome outcome,
            out string reason)
        {
            outcome = UiMirrorOutcome.None;
            reason = null;
            if (clientId <= 0)
            {
                outcome = UiMirrorOutcome.ViewMissing;
                reason = "clientId<=0";
                return false;
            }
            ViewMirror view;
            try
            {
                if (!TryResolveView(clientId, out view, out reason))
                {
                    m_noView++;
                    outcome = UiMirrorOutcome.ViewMissing;
                    return false;
                }
            }
            catch (Exception e)
            {
                reason = "resolve:" + Describe(e);
                outcome = UiMirrorOutcome.ViewMissing;
                m_noView++;
                return false;
            }

            bool any = false;

            // ---- ① 输入形态
            if (state.InputKind == UiInputKind.Unknown)
            {
                // 0 = unknown 只由未来版本发送；保留视图当前输入形态并计数，
                // 绝不猜一个"大概是触摸/键鼠"。真正的写入失败走 UnsupportedInput。
                outcome |= UiMirrorOutcome.InputUnknown;
            }
            else if (state.InputKind != view.AppliedInput)
            {
                if (TrySetInputKind(view, state.InputKind, out string inputReason))
                {
                    view.AppliedInput = state.InputKind;
                    outcome |= UiMirrorOutcome.Input;
                    any = true;
                    if (!view.HudIsLive) m_inputWritesWithoutHud++;
                }
                else
                {
                    m_inputSwitchFailures++;
                    outcome |= UiMirrorOutcome.UnsupportedInput;
                    reason = inputReason;
                    Warn("input:" + inputReason,
                        "UI 镜像：写 IsControlledByTouch 失败（" + inputReason + "）→ 该视图输入形态" +
                        "没有镜像（累计 " + m_inputSwitchFailures + "；HUD / 界面镜像继续）");
                }
            }
            else if (!IsInputApplied(view, state.InputKind))
            {
                // 引擎（或别的代码）把开关改回去了：重新断言，不改变"已镜像类型"的语义。
                if (TrySetInputKind(view, state.InputKind, out string reassertReason))
                {
                    outcome |= UiMirrorOutcome.InputReasserted;
                    any = true;
                }
                else
                {
                    m_inputSwitchFailures++;
                    outcome |= UiMirrorOutcome.UnsupportedInput;
                    Warn("reassert:" + reassertReason,
                        "UI 镜像：重新断言 IsControlledByTouch 失败（" + reassertReason + "）");
                }
            }

            // ---- ② HUD 显隐
            if (view.Gui != null && view.AppliedHud != state.HudVisible)
            {
                CaptureOrigin(view);
                view.Gui.IsVisible = state.HudVisible;
                view.AppliedHud = state.HudVisible;
                outcome |= UiMirrorOutcome.Hud;
                any = true;
                if (!state.HudVisible) m_hudHidden++;
            }

            // ---- ③ 全屏屏幕
            if (!string.Equals(view.AppliedScreen, state.Screen, StringComparison.Ordinal))
            {
                view.AppliedScreen = state.Screen;
                if (UiStateMirrorPlan.IsScreenMirrorable(state.Screen))
                {
                    view.UnsupportedScreen = string.Empty;
                    outcome |= UiMirrorOutcome.Screen;
                }
                else if (!string.Equals(view.UnsupportedScreen, state.Screen,
                        StringComparison.Ordinal))
                {
                    view.UnsupportedScreen = state.Screen;
                    outcome |= UiMirrorOutcome.UnsupportedScreen;
                }
            }

            // ---- ④ 模态面板（先加，对话框后加 → 对话框画在上面）
            ApplyModal(view, in state, ref outcome, ref any, out string modalFailure);
            if (!string.IsNullOrEmpty(modalFailure) && string.IsNullOrEmpty(reason))
                reason = modalFailure;

            // ---- ⑤ 对话框
            ApplyDialog(view, in state, ref outcome, ref any, out string dialogFailure);
            if (!string.IsNullOrEmpty(dialogFailure) && string.IsNullOrEmpty(reason))
                reason = dialogFailure;

            // ---- ⑥ 聊天输入行（mod 自己的 HUD，per-view 克隆会变成主机侧输入）
            if (state.ChatInputOpen) outcome |= UiMirrorOutcome.UnsupportedChat;

            m_applied++;
            view.Remote = TryGetRemotePlayer(clientId) ?? view.Remote;
            view.Desired = state;
            view.HasDesired = true;
            return any || outcome != UiMirrorOutcome.None;
        }

        public bool IsApplied(int clientId)
        {
            if (!m_views.TryGetValue(clientId, out ViewMirror view) || view == null) return false;
            if (!IsViewAlive(view))
            {
                // 视图已被引擎/控制器拆除（客户端离开 / 世界卸载）：只丢引用，不触碰已释放对象。
                m_views.Remove(clientId);
                Warn("view-gone:" + clientId,
                    "UI 镜像：clientId=" + clientId + " 的视图已不在 SubsystemGameWidgets 里 → " +
                    "丢弃镜像条目（其 widget 随视图一起释放）");
                return false;
            }
            if (!view.HasDesired) return false;
            ClientUiStateData desired = view.Desired;
            if (view.Input != null && desired.InputKind != UiInputKind.Unknown &&
                !IsInputApplied(view, desired.InputKind))
                return false;
            if (view.Gui != null && view.Gui.IsVisible != desired.HudVisible) return false;
            // 镜像 widget 被别的东西摘掉 / 视图重建后丢了：让调用方补施加。
            bool modalMatches = desired.ModalPresent
                ? (view.ModalWidget != null &&
                    string.Equals(view.ModalType, desired.ModalType, StringComparison.Ordinal))
                : view.ModalWidget == null;
            if (!modalMatches && !string.Equals(view.UnsupportedModalType, desired.ModalType,
                    StringComparison.Ordinal))
                return false;
            bool dialogMatches = desired.DialogPresent
                ? (view.DialogWidget != null &&
                    string.Equals(view.DialogType, desired.DialogType, StringComparison.Ordinal))
                : view.DialogWidget == null;
            if (!dialogMatches && !string.Equals(view.UnsupportedDialogType, desired.DialogType,
                    StringComparison.Ordinal))
                return false;
            return true;
        }

        private bool IsInputApplied(ViewMirror view, UiInputKind kind)
        {
            if (view.Input == null) return true;
            bool touch = kind == UiInputKind.Touch;
            try { return view.Input.IsControlledByTouch == touch; }
            catch (Exception) { return false; }
        }

        private bool TrySetInputKind(ViewMirror view, UiInputKind kind, out string reason)
        {
            reason = null;
            if (view.Input == null)
            {
                reason = "no-component-input";
                return false;
            }
            if (m_setControlledByTouch == null)
            {
                reason = m_inputSwitchError ?? "engine-missing-IsControlledByTouch";
                return false;
            }
            CaptureOrigin(view);
            bool touch = kind == UiInputKind.Touch;
            try
            {
                m_setControlledByTouch.Invoke(view.Input, new object[] { touch });
                return true;
            }
            catch (Exception e)
            {
                reason = "invoke:" + Describe(e);
                return false;
            }
        }

        private void CaptureOrigin(ViewMirror view)
        {
            if (view.OriginCaptured) return;
            view.OriginCaptured = true;
            view.OriginGuiVisible = view.Gui == null || view.Gui.IsVisible;
            view.OriginControlledByTouch = view.Input != null && view.Input.IsControlledByTouch;
        }

        // ================================================================= 模态面板

        private void ApplyModal(ViewMirror view, in ClientUiStateData state,
            ref UiMirrorOutcome outcome, ref bool any, out string failure)
        {
            failure = null;
            UiModalKind desired = state.ModalPresent
                ? UiStateMirrorPlan.ClassifyModal(state.ModalType)
                : UiModalKind.None;
            bool unsupported = state.ModalPresent && desired == UiModalKind.None;
            bool container = UiStateMirrorPlan.IsContainerModal(desired);
            int cellX = 0, cellY = 0, cellZ = 0;
            if (container && state.ModalPresent)
                TryGetContainerCell(view.ClientId, out cellX, out cellY, out cellZ);
            bool matches;
            if (!state.ModalPresent)
            {
                matches = view.ModalWidget == null;
            }
            else
            {
                matches = view.ModalWidget != null && view.ModalKind == desired &&
                    string.Equals(view.ModalType, state.ModalType, StringComparison.Ordinal) &&
                    (!container || (view.ModalCellX == cellX && view.ModalCellY == cellY &&
                        view.ModalCellZ == cellZ));
            }
            if (matches)
            {
                if (!unsupported) view.UnsupportedModalType = string.Empty;
                return;
            }
            if (view.ModalWidget != null)
            {
                RemoveModal(view);
                outcome |= UiMirrorOutcome.ModalClosed;
                m_closed++;
                any = true;
            }
            if (!state.ModalPresent)
            {
                view.UnsupportedModalType = string.Empty;
                return;
            }
            if (unsupported)
            {
                if (!string.Equals(view.UnsupportedModalType, state.ModalType,
                        StringComparison.Ordinal))
                {
                    view.UnsupportedModalType = state.ModalType;
                    outcome |= UiMirrorOutcome.UnsupportedModal;
                }
                return;
            }
            view.UnsupportedModalType = string.Empty;
            Widget widget = TryBuildModal(view, desired, state.ModalType, out cellX, out cellY,
                out cellZ, out failure);
            if (widget == null)
            {
                m_buildFailures++;
                outcome |= UiMirrorOutcome.UnsupportedModal;
                view.UnsupportedModalType = state.ModalType;
                Warn("modal-build:" + desired + ":" + (failure ?? "unknown"),
                    "UI 镜像：无法实例化面板 " + state.ModalType + "（" + (failure ?? "unknown") +
                    "）→ 该视图不显示这个面板（累计 buildFailures=" + m_buildFailures +
                    "；不摆空面板）");
                return;
            }
            CanvasWidget overlay = EnsureOverlay(view);
            if (overlay == null)
            {
                SafeDispose(widget);
                m_buildFailures++;
                failure = "no-overlay";
                outcome |= UiMirrorOutcome.UnsupportedModal;
                view.UnsupportedModalType = state.ModalType;
                return;
            }
            widget.HorizontalAlignment = WidgetAlignment.Center;
            widget.VerticalAlignment = WidgetAlignment.Center;
            MakeDisplayOnly(widget);
            overlay.Children.Add(widget);
            view.ModalWidget = widget;
            view.ModalKind = desired;
            view.ModalType = state.ModalType;
            view.ModalCellX = cellX;
            view.ModalCellY = cellY;
            view.ModalCellZ = cellZ;
            m_modalOpened++;
            outcome |= UiMirrorOutcome.ModalOpened;
            any = true;
        }

        private Widget TryBuildModal(ViewMirror view, UiModalKind kind, string typeName,
            out int cellX, out int cellY, out int cellZ, out string reason)
        {
            cellX = cellY = cellZ = 0;
            reason = null;
            ComponentPlayer remote = view.Remote;
            if (remote == null || !remote.IsAddedToProject)
            {
                reason = "remote-player-missing";
                return null;
            }
            ComponentMiner miner = remote.ComponentMiner;
            IInventory playerInventory = miner?.Inventory;
            if (playerInventory == null)
            {
                reason = "remote-inventory-missing";
                return null;
            }
            Entity entity = remote.Entity;
            try
            {
                switch (kind)
                {
                    case UiModalKind.Inventory:
                    {
                        ComponentCraftingTable crafting =
                            entity.FindComponent<ComponentCraftingTable>();
                        if (crafting == null)
                        {
                            reason = "no-ComponentCraftingTable";
                            return null;
                        }
                        return new FullInventoryWidget(playerInventory, crafting);
                    }
                    case UiModalKind.CreativeInventory:
                    {
                        if (entity.FindComponent<ComponentCreativeInventory>() == null)
                        {
                            reason = "no-ComponentCreativeInventory";
                            return null;
                        }
                        // 创造背包面板的"当前页 / 分类"由客户端本地状态决定、不在同步范围：
                        // 显示主机侧化身记录的页/分类（可能滞后，见模块文档）。
                        return new CreativeInventoryWidget(entity);
                    }
                    case UiModalKind.Clothing:
                        return new ClothingWidget(remote);
                    case UiModalKind.VitalStats:
                        // 玩家信息/属性面板：引擎里由 ClothingWidget.cs:78 打开，
                        // 构造只依赖 ComponentPlayer（Source: VitalStatsWidget.cs:64）。
                        return new VitalStatsWidget(remote);
                    case UiModalKind.Chest:
                    case UiModalKind.Furnace:
                    case UiModalKind.CraftingTable:
                    case UiModalKind.Dispenser:
                        break;
                    default:
                        reason = "kind-not-buildable:" + (int)kind;
                        return null;
                }

                if (m_containers == null)
                {
                    reason = "no-container-cell-port";
                    return null;
                }
                if (!m_containers.TryGetClientContainerCell(view.ClientId, out cellX, out cellY,
                        out cellZ))
                {
                    m_containerMissing++;
                    reason = "no-open-container-cell";
                    return null;
                }
                SubsystemBlockEntities blockEntities =
                    view.Project?.FindSubsystem<SubsystemBlockEntities>(false);
                if (blockEntities == null)
                {
                    reason = "no-SubsystemBlockEntities";
                    return null;
                }
                ComponentBlockEntity blockEntity =
                    blockEntities.GetBlockEntity(cellX, cellY, cellZ);
                if (blockEntity == null)
                {
                    m_containerMissing++;
                    reason = "block-entity-missing(" + cellX + "," + cellY + "," + cellZ + ")";
                    return null;
                }
                switch (kind)
                {
                    case UiModalKind.Chest:
                    {
                        ComponentChest chest = blockEntity.Entity.FindComponent<ComponentChest>();
                        if (chest == null)
                        {
                            reason = "not-a-chest(" + cellX + "," + cellY + "," + cellZ + ")";
                            return null;
                        }
                        return new ChestWidget(playerInventory, chest);
                    }
                    case UiModalKind.Furnace:
                    {
                        ComponentFurnace furnace =
                            blockEntity.Entity.FindComponent<ComponentFurnace>();
                        if (furnace == null)
                        {
                            reason = "not-a-furnace(" + cellX + "," + cellY + "," + cellZ + ")";
                            return null;
                        }
                        return new FurnaceWidget(playerInventory, furnace);
                    }
                    case UiModalKind.CraftingTable:
                    {
                        ComponentCraftingTable table =
                            blockEntity.Entity.FindComponent<ComponentCraftingTable>();
                        if (table == null)
                        {
                            reason = "not-a-crafting-table(" + cellX + "," + cellY + "," + cellZ + ")";
                            return null;
                        }
                        return new CraftingTableWidget(playerInventory, table);
                    }
                    default:
                    {
                        ComponentDispenser dispenser =
                            blockEntity.Entity.FindComponent<ComponentDispenser>();
                        if (dispenser == null)
                        {
                            reason = "not-a-dispenser(" + cellX + "," + cellY + "," + cellZ + ")";
                            return null;
                        }
                        return new DispenserWidget(playerInventory, dispenser);
                    }
                }
            }
            catch (Exception e)
            {
                reason = typeName + ":" + Describe(e);
                return null;
            }
        }

        private bool TryGetContainerCell(int clientId, out int x, out int y, out int z)
        {
            x = y = z = 0;
            if (m_containers == null) return false;
            try { return m_containers.TryGetClientContainerCell(clientId, out x, out y, out z); }
            catch (Exception) { return false; }
        }

        private void RemoveModal(ViewMirror view)
        {
            Widget widget = view.ModalWidget;
            view.ModalWidget = null;
            view.ModalKind = UiModalKind.None;
            view.ModalType = string.Empty;
            view.ModalCellX = view.ModalCellY = view.ModalCellZ = 0;
            SafeRemove(widget);
        }

        // ================================================================= 对话框

        private void ApplyDialog(ViewMirror view, in ClientUiStateData state,
            ref UiMirrorOutcome outcome, ref bool any, out string failure)
        {
            failure = null;
            UiDialogKind desired = state.DialogPresent
                ? UiStateMirrorPlan.ClassifyDialog(state.DialogType)
                : UiDialogKind.None;
            bool unsupported = state.DialogPresent && desired == UiDialogKind.None;
            bool matches = state.DialogPresent
                ? (view.DialogWidget != null && view.DialogKind == desired &&
                    string.Equals(view.DialogType, state.DialogType, StringComparison.Ordinal))
                : view.DialogWidget == null;
            if (matches)
            {
                if (!unsupported) view.UnsupportedDialogType = string.Empty;
                return;
            }
            if (view.DialogWidget != null)
            {
                RemoveDialog(view);
                outcome |= UiMirrorOutcome.DialogClosed;
                m_closed++;
                any = true;
            }
            if (!state.DialogPresent)
            {
                view.UnsupportedDialogType = string.Empty;
                return;
            }
            if (unsupported)
            {
                if (!string.Equals(view.UnsupportedDialogType, state.DialogType,
                        StringComparison.Ordinal))
                {
                    view.UnsupportedDialogType = state.DialogType;
                    outcome |= UiMirrorOutcome.UnsupportedDialog;
                }
                return;
            }
            view.UnsupportedDialogType = string.Empty;
            Dialog dialog = TryBuildDialog(view, desired, state.DialogType, out failure);
            if (dialog == null)
            {
                m_buildFailures++;
                outcome |= UiMirrorOutcome.UnsupportedDialog;
                view.UnsupportedDialogType = state.DialogType;
                Warn("dialog-build:" + desired + ":" + (failure ?? "unknown"),
                    "UI 镜像：无法实例化对话框 " + state.DialogType + "（" +
                    (failure ?? "unknown") + "）→ 该视图不显示这个对话框" +
                    "（累计 buildFailures=" + m_buildFailures + "；不摆假框）");
                return;
            }
            CanvasWidget overlay = EnsureOverlay(view);
            if (overlay == null)
            {
                SafeDispose(dialog);
                m_buildFailures++;
                failure = "no-overlay";
                outcome |= UiMirrorOutcome.UnsupportedDialog;
                view.UnsupportedDialogType = state.DialogType;
                return;
            }
            MakeDisplayOnly(dialog);
            overlay.Children.Add(dialog);
            view.DialogWidget = dialog;
            view.DialogKind = desired;
            view.DialogType = state.DialogType;
            m_dialogOpened++;
            outcome |= UiMirrorOutcome.DialogOpened;
            any = true;
        }

        private Dialog TryBuildDialog(ViewMirror view, UiDialogKind kind, string typeName,
            out string reason)
        {
            reason = null;
            try
            {
                switch (kind)
                {
                    case UiDialogKind.GameMenu:
                        return TryBuildGameMenuDialog(view, out reason);
                    case UiDialogKind.KeyboardHelp:
                        return new KeyboardHelpDialog();
                    case UiDialogKind.GamepadHelp:
                        return new GamepadHelpDialog();
                    default:
                        reason = "kind-not-buildable:" + (int)kind;
                        return null;
                }
            }
            catch (Exception e)
            {
                reason = typeName + ":" + Describe(e);
                return null;
            }
        }

        /// <summary>
        /// GameMenuDialog 的构造会读世界 / 统计（只读），但有两个**一次性提示**副作用：
        /// 设备快 / 慢时弹 MessageDialog 并写 Analytics（Source: GameMenuDialog.cs:24-40）。
        /// 镜像不是玩家操作，不能触发它们：构造期间把两个静态标志临时置 true，构造完还原。
        /// 标志拿不到 = 明确失败（不构造），避免在主机上弹窗。
        /// </summary>
        private Dialog TryBuildGameMenuDialog(ViewMirror view, out string reason)
        {
            reason = null;
            ComponentPlayer remote = view.Remote;
            if (remote == null || !remote.IsAddedToProject)
            {
                reason = "remote-player-missing";
                return null;
            }
            if (m_parentField == null)
            {
                reason = "no-static-field-accessor";
                return null;
            }
            bool increase;
            bool decrease;
            try
            {
                increase = m_parentField.GetStaticField<bool>(typeof(GameMenuDialog),
                    "m_increaseDetailDialogShown");
                decrease = m_parentField.GetStaticField<bool>(typeof(GameMenuDialog),
                    "m_decreaseDetailDialogShown");
            }
            catch (Exception e)
            {
                reason = "gamedialog-flags-unavailable:" + Describe(e);
                Warn("gamedialog-flags",
                    "UI 镜像：读不到 GameMenuDialog 的一次性提示标志（" + Describe(e) +
                    "）→ 不构造暂停菜单镜像（构造它可能在主机上弹性能提示框 / 记 Analytics）");
                return null;
            }
            try
            {
                m_parentField.ModifyStaticField(typeof(GameMenuDialog),
                    "m_increaseDetailDialogShown", true);
                m_parentField.ModifyStaticField(typeof(GameMenuDialog),
                    "m_decreaseDetailDialogShown", true);
                return new GameMenuDialog(remote);
            }
            catch (Exception e)
            {
                reason = "gamedialog-ctor:" + Describe(e);
                return null;
            }
            finally
            {
                try
                {
                    m_parentField.ModifyStaticField(typeof(GameMenuDialog),
                        "m_increaseDetailDialogShown", increase);
                    m_parentField.ModifyStaticField(typeof(GameMenuDialog),
                        "m_decreaseDetailDialogShown", decrease);
                }
                catch (Exception e)
                {
                    Warn("gamedialog-flags-restore",
                        "UI 镜像：还原 GameMenuDialog 一次性提示标志失败（" + Describe(e) +
                        "）——只影响主机自己的提示是否出现，不影响镜像界面");
                }
            }
        }

        private void RemoveDialog(ViewMirror view)
        {
            Dialog dialog = view.DialogWidget;
            view.DialogWidget = null;
            view.DialogKind = UiDialogKind.None;
            view.DialogType = string.Empty;
            SafeRemove(dialog);
        }

        // ================================================================= 视图 / 覆盖层

        private bool TryResolveView(int clientId, out ViewMirror view, out string reason)
        {
            view = null;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            if (!m_avatars.TryGet(new PlayerKey(clientId, 0), out PlayerData remote) ||
                remote == null)
            {
                reason = "no-avatar";
                return false;
            }
            ComponentPlayer remotePlayer = remote.ComponentPlayer;
            if (remotePlayer == null)
            {
                reason = "avatar-no-component-player";
                return false;
            }
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null)
            {
                reason = "no-subsystem-game-widgets";
                return false;
            }
            GameWidget gameWidget = null;
            int duplicates = 0;
            for (int i = 0; i < widgets.GameWidgets.Count; i++)
            {
                GameWidget candidate = widgets.GameWidgets[i];
                if (candidate == null || candidate.PlayerData == null) continue;
                if (ReferenceEquals(candidate.PlayerData, remote)) continue;
                if (!ReferenceEquals(candidate.Target, remotePlayer)) continue;
                if (gameWidget == null) gameWidget = candidate;
                else duplicates++;
            }
            if (gameWidget == null)
            {
                reason = "no-view-following-client";
                return false;
            }
            if (m_views.TryGetValue(clientId, out ViewMirror existing) && existing != null)
            {
                if (ReferenceEquals(existing.GameWidget, gameWidget) &&
                    ReferenceEquals(existing.Project, project))
                {
                    existing.Remote = remotePlayer;
                    existing.DuplicateViews = duplicates;
                    view = existing;
                    return true;
                }
                // 视图换了（自动视图重建 / H3 重新绑定）：先按旧视图恢复，再建新条目。
                RestoreView(existing, "view-changed");
            }
            if (duplicates > 0) m_duplicateViews += duplicates;
            bool liveHud = IsLiveLocalPlayer(project, gameWidget.PlayerData);
            if (!liveHud) m_viewsWithoutHud++;
            ComponentPlayer viewPlayer = gameWidget.PlayerData.ComponentPlayer;
            ContainerWidget gui = gameWidget.GuiWidget;
            if (gui == null)
            {
                reason = "view-gui-missing";
                return false;
            }
            var created = new ViewMirror
            {
                ClientId = clientId,
                Project = project,
                GameWidget = gameWidget,
                ViewPlayerData = gameWidget.PlayerData,
                Remote = remotePlayer,
                Input = viewPlayer?.ComponentInput,
                Gui = gui,
                AppliedHud = gui.IsVisible,
                DuplicateViews = duplicates,
                HudIsLive = liveHud
            };
            m_views[clientId] = created;
            view = created;
            if (!liveHud)
            {
                Warn("view-no-hud:" + clientId,
                    "UI 镜像：clientId=" + clientId + " 的视图是 camera-only 壳玩家（不在 " +
                    "SubsystemPlayers.PlayersData 里，组件从不更新）→ 该视图本来就没有 HUD，" +
                    "输入形态 / HUD 显隐镜像对它没有可见效果（打开的面板/对话框仍然镜像）；" +
                    "H3 replace 模式（真实本机玩家）才有 HUD");
            }
            LogRateLimited("view:" + clientId,
                "UI 镜像：clientId=" + clientId + " 的视图 = GameWidget#" +
                gameWidget.GameWidgetIndex + "（Target=该客户端 ComponentPlayer" +
                (duplicates > 0 ? "，另有 " + duplicates + " 个重复视图未镜像" : "") +
                "）· 视图玩家=" + gameWidget.PlayerData.Name);
            return true;
        }

        /// <summary>
        /// 视图玩家是不是真正的本机玩家：只有它们会被 SubsystemUpdate 驱动，
        /// ComponentGui.UpdateWidgets 才会跑（Source: Survivalcraft/Game/ComponentGui.cs:361-419）。
        /// </summary>
        private static bool IsLiveLocalPlayer(Project project, PlayerData playerData)
        {
            if (project == null || playerData == null) return false;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return false;
            for (int i = 0; i < players.PlayersData.Count; i++)
            {
                if (ReferenceEquals(players.PlayersData[i], playerData)) return true;
            }
            return false;
        }

        private ComponentPlayer TryGetRemotePlayer(int clientId)
        {
            if (!m_avatars.TryGet(new PlayerKey(clientId, 0), out PlayerData remote) ||
                remote == null)
                return null;
            return remote.ComponentPlayer;
        }

        private bool IsViewAlive(ViewMirror view)
        {
            Project project = GameManager.Project;
            if (project == null || !ReferenceEquals(project, view.Project)) return false;
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null) return false;
            for (int i = 0; i < widgets.GameWidgets.Count; i++)
            {
                if (ReferenceEquals(widgets.GameWidgets[i], view.GameWidget)) return true;
            }
            return false;
        }

        private CanvasWidget EnsureOverlay(ViewMirror view)
        {
            if (view.Overlay != null) return view.Overlay;
            if (view.Gui == null) return null;
            var overlay = new CanvasWidget
            {
                IsHitTestVisible = false,
                HorizontalAlignment = WidgetAlignment.Stretch,
                VerticalAlignment = WidgetAlignment.Stretch
            };
            view.Gui.Children.Add(overlay);
            view.Overlay = overlay;
            return overlay;
        }

        private static void MakeDisplayOnly(Widget widget)
        {
            if (widget == null) return;
            widget.IsHitTestVisible = false;
            if (widget is ContainerWidget container)
            {
                foreach (Widget child in container.Children) MakeDisplayOnly(child);
            }
        }

        private void SafeRemove(Widget widget)
        {
            if (widget == null) return;
            try
            {
                if (widget.ParentWidget is ContainerWidget parent) parent.Children.Remove(widget);
            }
            catch (Exception e)
            {
                Warn("remove:" + widget.GetType().Name,
                    "UI 镜像：摘除镜像面板时异常（继续 Dispose）：" + Describe(e));
            }
            SafeDispose(widget);
        }

        private void SafeDispose(Widget widget)
        {
            if (widget == null) return;
            try { widget.Dispose(); }
            catch (Exception e)
            {
                Warn("dispose:" + widget.GetType().Name,
                    "UI 镜像：释放镜像面板时异常：" + Describe(e));
            }
        }

        // ================================================================= 重置 / 恢复

        public void ResetClient(int clientId, string reason)
        {
            if (!m_views.TryGetValue(clientId, out ViewMirror view) || view == null)
            {
                m_views.Remove(clientId);
                return;
            }
            RestoreView(view, reason);
        }

        public void ResetAll(string reason)
        {
            if (m_views.Count == 0) return;
            var clients = new List<int>(m_views.Keys);
            for (int i = 0; i < clients.Count; i++) ResetClient(clients[i], reason);
            m_views.Clear();
        }

        private void RestoreView(ViewMirror view, string reason)
        {
            m_views.Remove(view.ClientId);
            RemoveModal(view);
            RemoveDialog(view);
            if (view.Overlay != null)
            {
                SafeRemove(view.Overlay);
                view.Overlay = null;
            }
            if (!IsViewAlive(view))
            {
                // 视图已随世界 / 控制器释放：只清引用，绝不在已释放对象上写字段。
                LogRateLimited("restore-gone:" + view.ClientId,
                    "UI 镜像：clientId=" + view.ClientId + " 的视图已不存在（" + reason +
                    "）→ 只清引用（镜像 widget 随视图释放）");
                return;
            }
            try
            {
                if (view.OriginCaptured)
                {
                    if (view.Input != null && m_setControlledByTouch != null)
                        m_setControlledByTouch.Invoke(view.Input,
                            new object[] { view.OriginControlledByTouch });
                    if (view.Gui != null) view.Gui.IsVisible = view.OriginGuiVisible;
                }
                m_restores++;
                LogRateLimited("restore:" + view.ClientId,
                    "UI 镜像：clientId=" + view.ClientId + " 的视图已恢复（" + reason +
                    "）· 输入形态=" + (view.OriginControlledByTouch ? "touch" : "non-touch") +
                    " · HUD 可见=" + view.OriginGuiVisible +
                    " · 镜像面板已摘除 · 累计恢复=" + m_restores);
            }
            catch (Exception e)
            {
                m_restoreFailures++;
                Warn("restore-fail:" + view.ClientId,
                    "UI 镜像：恢复视图原值失败（clientId=" + view.ClientId + " " + reason + "）：" +
                    Describe(e) + "（累计 " + m_restoreFailures + "）");
            }
        }

        // ================================================================= 状态行

        public string Describe() =>
            "uiMirrorViews=" + m_views.Count +
            " uiMirrorApplied=" + m_applied +
            " uiMirrorNoView=" + m_noView +
            " uiMirrorDuplicateViews=" + m_duplicateViews +
            " uiMirrorViewsWithoutHud=" + m_viewsWithoutHud +
            " uiMirrorInputWritesWithoutHud=" + m_inputWritesWithoutHud +
            " uiMirrorBuildFailures=" + m_buildFailures +
            " uiMirrorContainerMissing=" + m_containerMissing +
            " uiMirrorInputSwitchFailures=" + m_inputSwitchFailures +
            " uiMirrorDialogsOpened=" + m_dialogOpened +
            " uiMirrorModalsOpened=" + m_modalOpened +
            " uiMirrorWidgetsClosed=" + m_closed +
            " uiMirrorHudHidden=" + m_hudHidden +
            " uiMirrorRestores=" + m_restores +
            " uiMirrorRestoreFailures=" + m_restoreFailures +
            " uiInputSurfaceRebuilds=" + InputSurfaceRebuilds +
            " uiInputSurfaceRebuildRequestsRejected=" + InputSurfaceRebuildRequestsRejected;

        // ================================================================= 日志

        private static string Describe(Exception e) =>
            e == null ? "null" : e.GetType().Name + " " + e.Message;

        private void LogRateLimited(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastLogMs[key] = nowMs;
            if (m_diagnostics == null) return;
            m_diagnostics.Info(OwnerId, message);
        }

        private void Warn(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastWarnMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastWarnMs[key] = nowMs;
            if (m_diagnostics == null) return;
            m_diagnostics.Warn(OwnerId, message);
        }
    }
}

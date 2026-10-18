using System;
using System.Collections.Generic;
using System.Linq;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端 UI 拖出到世界的请求入口（实现方 = <see cref="PickableReplicaEngine"/>）。
    /// 返回值语义与 <see cref="IEntityDropInputSink"/> 一致：true = 请求已入队发送；
    /// 调用方随后**无论成功失败**都要撤销本次拖拽（物品留在背包里），绝不本地扣除。
    /// </summary>
    public interface IUiDropSink
    {
        /// <summary>本端是不是联机客户端副本（主机 / 单机必须保持引擎原生拖出行为）。</summary>
        bool IsUiDropInterceptionClient { get; }

        /// <summary>提交一次 UI 拖出丢弃意图（Source 必须是 <see cref="DropSources.Ui"/>）。</summary>
        bool TrySubmitUiDrop(in PickableDropIntent intent, out string reason);
    }

    /// <summary>
    /// <c>DragHostWidget</c> 的**客户端外壳**：在所有 UI 拖拽真正落地之前接管"拖到世界"的那一种。
    ///
    /// 为什么这是唯一入口：
    ///   · 背包 / 容器 / 快捷栏的每一次拖拽都由 <c>InventorySlotWidget</c> 发起，
    ///     统一交给本地 <c>GameWidget</c> 里**唯一**的 <c>DragHostWidget</c>；
    ///   · 松手时 <c>DragHostWidget.Update</c> 命中 <c>IDragTargetWidget</c> 并调用
    ///     <c>DragDrop</c> —— 世界里唯一的拖放目标是 <c>ViewWidget</c>；
    ///   · <c>ViewWidget.DragDrop</c> 会先 <c>Inventory.RemoveSlotItems</c> 再
    ///     <c>SubsystemPickables.AddPickable</c>（本机扣除 + 本机实体）。客户端不允许这两件事，
    ///     所以必须在**调用它之前**截住（此时物品还在背包里，可以原样发给主机）。
    ///
    /// 只接管"源背包 = 本机玩家 <c>ComponentMiner.Inventory</c>"的拖出；容器 / 家具 / 创造仓库
    /// 之外的拖放不在本模块职责内（容器走容器同步链），明确计数 + 限流日志后交回引擎原生路径。
    ///
    /// 无静态状态：父类反射入口 / sink 都是实例属性。
    /// Source: Survivalcraft/Game/DragHostWidget.cs:DragHostWidget.Update
    /// Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
    /// Source: Survivalcraft/Game/InventorySlotWidget.cs:InventorySlotWidget.Update
    /// </summary>
    public sealed class SuDropDragHostWidget : DragHostWidget
    {
        private readonly Dictionary<string, long> m_lastFaultMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>父类私有字段读取入口（组合根注入；只在该拖拽松手的这一帧使用）。</summary>
        public IModParentField ParentField { get; set; }

        /// <summary>UI 拖出请求入口（组合根注入；null = 主菜单 / 未装配，完全走原生行为）。</summary>
        public IUiDropSink Sink { get; set; }

        /// <summary>非玩家背包的拖出次数（容器 / 家具等，由各自的同步链负责）。</summary>
        public long ForeignInventoryDrops { get; private set; }

        /// <summary>识别失败 / 父类字段读取失败的次数（限流日志，绝不静默）。</summary>
        public long InterceptionFaults { get; private set; }

        public override void Update()
        {
            if (IsDragInProgress && !Input.Drag.HasValue && Sink != null &&
                Sink.IsUiDropInterceptionClient && TryInterceptDrop())
                return;
            base.Update();
        }

        /// <summary>
        /// 拖拽松手时先于 <c>base.Update</c> 判定：只有"拖进本机玩家视野"的拖出才由本外壳接管。
        /// 返回 true = 已接管（调用方不要再跑 base.Update）。
        /// </summary>
        private bool TryInterceptDrop()
        {
            try
            {
                if (ParentField == null) return false;
                // 拖拽状态 / 数据 / 松手位置：父类私有字段；只有松手这一帧读（低频）。
                Widget dragWidget = ParentField.GetParentField<Widget>(this, "m_dragWidget",
                    typeof(DragHostWidget));
                if (dragWidget == null) return false;
                object dragData = ParentField.GetParentField(this, "m_dragData",
                    typeof(DragHostWidget));
                Vector2 dragPosition = ParentField.GetParentField<Vector2>(this, "m_dragPosition",
                    typeof(DragHostWidget));
                IDragTargetWidget target = HitTestGlobal(dragPosition,
                    widget => widget is IDragTargetWidget) as IDragTargetWidget;
                if (!(target is ViewWidget viewWidget) || viewWidget.GameWidget == null)
                    return false;   // 拖到别的 UI 上：原生处理
                if (!(dragData is InventoryDragData inventoryDragData))
                    return false;
                GameWidget gameWidget = viewWidget.GameWidget;
                ComponentPlayer player = gameWidget.PlayerData?.ComponentPlayer;
                IInventory inventory = player?.ComponentMiner?.Inventory;
                Camera camera = gameWidget.ActiveCamera;
                if (player == null || inventory == null || camera == null) return false;
                if (!ReferenceEquals(inventoryDragData.Inventory, inventory))
                {
                    // 从容器 / 家具面板拖到世界：不是本机玩家背包。
                    // 本模块只接管"请求宿主权威背包"的丢弃；容器槽位的改动由容器同步链负责
                    // （显式告警 + 计数，绝不假装已经处理）。
                    ForeignInventoryDrops++;
                    NoteForeignInventoryDrop(inventoryDragData.Inventory);
                    return false;
                }
                int slot = inventoryDragData.SlotIndex;
                if (slot < 0 || slot >= inventory.SlotsCount) return false;
                int itemValue = inventory.GetSlotValue(slot);
                int slotCount = inventory.GetSlotCount(slot);
                if (itemValue == 0 || slotCount <= 0) return false;
                bool splitSource = player.ComponentInput != null &&
                    player.ComponentInput.SplitSourceInventory == inventory &&
                    player.ComponentInput.SplitSourceSlotIndex == slot;
                bool singleItem = inventoryDragData.DragMode == DragMode.SingleItem;
                if (!UiDropPolicy.TryResolveDropCount(slotCount, singleItem, splitSource,
                        out int count, out string reason))
                {
                    // 空槽 / 数量非法：ViewWidget 自己也不会丢东西，交回原生（无副作用）。
                    NoteFault("count:" + reason, "UI 拖出数量无法确定（" + reason + "）", false);
                    return false;
                }
                // 方向 / 落点与 ViewWidget.DragDrop 完全同形：
                //   pos = ActiveCamera.ViewPosition
                //   dir = Normalize(ScreenToWorld(dragWidget 中心) - ViewPosition)
                Vector2 screenPosition = dragWidget.WidgetToScreen(dragWidget.ActualSize / 2f);
                Vector3 worldPoint = camera.ScreenToWorld(
                    new Vector3(screenPosition.X, screenPosition.Y, 1f), Matrix.Identity);
                Vector3 viewPosition = camera.ViewPosition;
                if (!UiDropPolicy.TryBuildIntent(slot, itemValue, count, slotCount, viewPosition,
                        worldPoint - viewPosition, out PickableDropIntent intent, out reason))
                {
                    NoteFault("intent:" + reason,
                        "UI 拖出意图构造失败（" + reason + "）→ 撤销本次拖出（物品留在背包）", true);
                    EndDrag();
                    return true;   // 已识别为玩家背包拖出：绝不交给 ViewWidget 本地扣除
                }
                bool submitted = false;
                try
                {
                    submitted = Sink.TrySubmitUiDrop(in intent, out reason);
                }
                catch (Exception e)
                {
                    reason = "sink-exception-" + e.GetType().Name;
                    NoteFault(reason, "UI 拖出请求入口异常（" + e.GetType().Name + " " +
                        e.Message + "）", true);
                }
                if (!submitted)
                {
                    NoteFault("submit:" + reason,
                        "UI 拖出没有发出去（" + reason + "）→ 撤销本次拖出：" +
                        "物品仍在背包里（" + intent.Describe() + "）", true);
                }
                EndDrag();
                return true;
            }
            catch (Exception e)
            {
                // 识别阶段异常：没有任何本地扣除发生，交回原生路径（并明确记录一次）。
                NoteFault("intercept:" + e.GetType().Name,
                    "UI 拖出拦截异常（" + e.GetType().Name + " " + e.Message +
                    "）→ 本次按原生拖出处理（若物品消失，请上报这条日志）", false);
                return false;
            }
        }

        private void NoteForeignInventoryDrop(IInventory source)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            const string key = "foreign-inventory";
            if (m_lastFaultMs.TryGetValue(key, out long last) && nowMs - last < 5000) return;
            if (m_lastFaultMs.Count > 32) m_lastFaultMs.Clear();
            m_lastFaultMs[key] = nowMs;
            Log.Warning("[SuMP][ui-drop] 从非玩家背包拖到世界（源: " +
                (source == null ? "null" : source.GetType().Name) +
                "）→ 不由 UI 丢弃请求链接管（容器槽位改动走容器同步链；" +
                "累计 " + ForeignInventoryDrops + " 次）");
        }

        private void NoteFault(string key, string message, bool swallow)
        {
            InterceptionFaults++;
            // 限流：同一个 key 最多 5 秒一条（计数每次都加，绝不静默）。
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastFaultMs.TryGetValue(key, out long last) && nowMs - last < 5000) return;
            if (m_lastFaultMs.Count > 32) m_lastFaultMs.Clear();
            m_lastFaultMs[key] = nowMs;
            Log.Error("[SuMP][ui-drop] " + message +
                (swallow ? "（已撤销拖出，不本地扣除）" : string.Empty));
        }
    }

    /// <summary>
    /// 每帧把本机 <c>GameWidget</c> 里的 <c>DragHostWidget</c> 换成
    /// <see cref="SuDropDragHostWidget"/>（旧实现同款手法，见
    /// <c>Mod/ScMultiplayer/Func/Ui/SuNetworkDragHostWidget.cs</c> +
    /// <c>ScMultiplayerClientEvents.EnsureClientDropDragHost</c>）。
    ///
    /// 不替换任何子系统（<c>SubsystemGameWidgets</c> 已被 ConsoleMod 占用），也不依赖静态单例：
    /// 组合根持有一个实例，每帧在模块 tick 之后调用 <see cref="Tick"/>。
    /// 不复制任何逻辑：替换只做属性搬运 + 修正 <c>InventorySlotWidget.m_dragHostWidget</c> 缓存。
    /// </summary>
    public sealed class UiDropAttacher
    {
        private readonly IUiDropSink m_sink;
        private readonly IModParentField m_parentField;
        private readonly Action<string> m_info;
        private readonly Action<string> m_warn;
        private readonly HashSet<GameWidget> m_installed = new HashSet<GameWidget>();
        private bool m_missingDragHostLogged;

        public UiDropAttacher(IUiDropSink sink, IModParentField parentField,
            Action<string> info = null, Action<string> warn = null)
        {
            m_sink = sink ?? throw new ArgumentNullException(nameof(sink));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_info = info;
            m_warn = warn;
        }

        /// <summary>已安装外壳的 GameWidget 数量（状态行 / 诊断）。</summary>
        public int InstalledCount => m_installed.Count;

        public void Tick()
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                m_installed.Clear();
                return;
            }
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null) return;
            ReadOnlyList<GameWidget> gameWidgets = widgets.GameWidgets;
            if (gameWidgets.Count == 0) return;
            m_installed.RemoveWhere(widget => !Contains(gameWidgets, widget));
            for (int i = 0; i < gameWidgets.Count; i++)
            {
                GameWidget gameWidget = gameWidgets[i];
                if (gameWidget == null || m_installed.Contains(gameWidget)) continue;
                DragHostWidget dragHost =
                    gameWidget.Children.Find<DragHostWidget>("DragHost", false);
                if (dragHost == null)
                {
                    if (m_missingDragHostLogged) continue;
                    m_missingDragHostLogged = true;
                    m_warn?.Invoke("[SuMP][ui-drop] GameWidget 里没有 DragHost（引擎界面结构变化）→ " +
                        "UI 拖出无法在本地扣除之前拦截；请检查 Content/Widgets/GameWidget.xml");
                    continue;
                }
                if (dragHost is SuDropDragHostWidget existing)
                {
                    // 上一个世界 / 组合根重建后复用同一个外壳：只补依赖。
                    existing.ParentField = m_parentField;
                    existing.Sink = m_sink;
                    m_installed.Add(gameWidget);
                    continue;
                }
                if (dragHost.IsDragInProgress || dragHost.ParentWidget == null)
                {
                    // 正在拖拽时不能换控件；下一帧再装（开局第一帧通常已经装好）。
                    continue;
                }
                Replace(gameWidget, dragHost);
                m_installed.Add(gameWidget);
                m_info?.Invoke("[SuMP][ui-drop] 已挂载 UI 拖出拦截器（客户端拖到世界 → " +
                    "PickableDropRequest；本地不扣物品、不生成拾取物）");
            }
        }

        private void Replace(GameWidget gameWidget, DragHostWidget dragHost)
        {
            ContainerWidget parentWidget = dragHost.ParentWidget;
            int childIndex = parentWidget.Children.IndexOf(dragHost);
            if (childIndex < 0) return;
            var replacement = new SuDropDragHostWidget
            {
                Name = dragHost.Name,
                Tag = dragHost.Tag,
                IsVisible = dragHost.IsVisible,
                IsEnabled = dragHost.IsEnabled,
                IsHitTestVisible = dragHost.IsHitTestVisible,
                ClampToBounds = dragHost.ClampToBounds,
                Margin = dragHost.Margin,
                HorizontalAlignment = dragHost.HorizontalAlignment,
                VerticalAlignment = dragHost.VerticalAlignment,
                LayoutTransform = dragHost.LayoutTransform,
                RenderTransform = dragHost.RenderTransform,
                ParentField = m_parentField,
                Sink = m_sink
            };
            parentWidget.Children.Remove(dragHost);
            parentWidget.Children.Insert(childIndex, replacement);
            // InventorySlotWidget 会缓存第一次查到的 DragHost 实例：必须指向新外壳，
            // 否则热栏 / 面板里的拖拽仍然走旧控件（幽灵 + 物品消失）。
            foreach (InventorySlotWidget slotWidget in
                gameWidget.AllChildren.OfType<InventorySlotWidget>())
            {
                try
                {
                    m_parentField.ModifyParentField(slotWidget, "m_dragHostWidget", replacement,
                        typeof(InventorySlotWidget));
                }
                catch (Exception e)
                {
                    m_warn?.Invoke("[SuMP][ui-drop] 修正 InventorySlotWidget.m_dragHostWidget 失败（" +
                        e.GetType().Name + " " + e.Message + "）→ 该槽的拖拽可能仍走旧控件");
                }
            }
        }

        private static bool Contains(ReadOnlyList<GameWidget> widgets, GameWidget widget)
        {
            for (int i = 0; i < widgets.Count; i++)
                if (ReferenceEquals(widgets[i], widget)) return true;
            return false;
        }
    }
}

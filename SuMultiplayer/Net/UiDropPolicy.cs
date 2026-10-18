using System;
using Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 客户端 **UI 拖出到世界**（<c>ViewWidget.DragDrop</c>）的纯判定层
    /// （不依赖 Game / EntitySystem，自检直接编译本文件）。
    ///
    /// 口径必须与引擎完全同形，否则主机收到的 count / 方向会和玩家看到的不一致：
    ///   · split source（<c>ComponentInput.SplitSourceInventory/SlotIndex</c> 指向该槽）→ 只丢 1 个；
    ///   · 否则 <c>DragMode.SingleItem</c> → 丢 1 个；<c>DragMode.AllItems</c> → 丢整格；
    ///   · 位置 = <c>ActiveCamera.ViewPosition</c>；
    ///   · 方向 = Normalize(<c>ScreenToWorld(拖拽点)</c> - <c>ViewPosition</c>)（主机按 12 m/s 生成速度）。
    ///
    /// Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
    /// Source: Survivalcraft/Game/InventorySlotWidget.cs:InventorySlotWidget.Update
    ///         （split source 会把 dragMode 强制改成 SingleItem）
    /// </summary>
    public static class UiDropPolicy
    {
        /// <summary>拖拽方向的最小可用长度（与引擎 Normalize 前的实际距离远大于此）。</summary>
        private const float MinDirectionLengthSquared = 1e-6f;

        /// <summary>
        /// 按引擎 <c>ViewWidget.DragDrop</c> 的规则算这次拖拽要丢几个。
        /// false + reason = 明确失败（空槽 / 数量非法），调用方不发送请求、也不本地扣除。
        /// </summary>
        public static bool TryResolveDropCount(int slotCount, bool dragModeSingleItem,
            bool splitSource, out int count, out string reason)
        {
            count = 0;
            reason = null;
            if (slotCount <= 0)
            {
                reason = "empty-slot";
                return false;
            }
            if (splitSource)
            {
                count = Math.Min(slotCount, 1);
                return true;
            }
            count = dragModeSingleItem ? Math.Min(slotCount, 1) : slotCount;
            if (count <= 0)
            {
                reason = "drop-count-" + count;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 组一次 UI 丢弃意图（Source 固定 = <see cref="DropSources.Ui"/>）。
        /// <paramref name="dropDirection"/> 是 **未归一化** 的"世界点 - 视点"向量（引擎自己在
        /// DragDrop 里做 Normalize）；这里做有限性 / 非零校验后归一化。
        /// false + reason = 明确失败（槽位 / 物品 / 数量 / 方向非法）。
        /// </summary>
        public static bool TryBuildIntent(int sourceSlot, int itemValue, int dropCount,
            int slotCount, Vector3 viewPosition, Vector3 dropDirection,
            out PickableDropIntent intent, out string reason)
        {
            intent = default;
            reason = null;
            if (sourceSlot < 0 || sourceSlot > EntityRequestCodec.MaxActiveSlot)
            {
                reason = "slot-" + sourceSlot;
                return false;
            }
            if (itemValue <= 0)
            {
                reason = "item-" + itemValue;
                return false;
            }
            if (dropCount <= 0)
            {
                reason = "count-" + dropCount;
                return false;
            }
            if (slotCount < dropCount)
            {
                reason = "count-exceeds-slot-" + dropCount + "/" + slotCount;
                return false;
            }
            if (!IsFinite(viewPosition))
            {
                reason = "position-nonfinite";
                return false;
            }
            if (!IsFinite(dropDirection) ||
                dropDirection.LengthSquared() <= MinDirectionLengthSquared)
            {
                reason = "direction-invalid";
                return false;
            }
            Vector3 direction = Vector3.Normalize(dropDirection);
            if (!IsFinite(direction) || direction.LengthSquared() <= MinDirectionLengthSquared)
            {
                reason = "direction-invalid";
                return false;
            }
            intent = new PickableDropIntent(sourceSlot, itemValue, dropCount, viewPosition,
                direction, DropSources.Ui);
            return true;
        }

        /// <summary>引擎 <c>ViewWidget.DragDrop</c> 的落点 = 当前相机视点（精确相等）。</summary>
        public static bool IsDropAtViewPosition(Vector3 position, Vector3 viewPosition)
        {
            if (!IsFinite(position) || !IsFinite(viewPosition)) return false;
            return Vector3.DistanceSquared(position, viewPosition) <=
                UiDropPhantomPolicy.ViewPositionEpsilonSquared;
        }

        internal static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
    }

    /// <summary>
    /// 客户端 **AddPickable 安全网** 的纯判定层：识别"本地引擎刚创建的 UI 幽灵拾取物"，
    /// 并找出它是从哪个背包槽扣掉的（用于把物品放回、再走请求链）。
    ///
    /// 什么时候需要它：正常路径由 <c>Engine/UiDropInterception.cs</c> 在
    /// <c>ViewWidget.DragDrop</c> 之前拦下，本地根本不会创建拾取物；但如果拦截器没有
    /// 装上（注入失败 / 别的 UI 路径），引擎仍会按原生路径 RemoveSlotItems + AddPickable。
    /// 这里按"落点 == 相机视点 + 背包刚好少了同样物品/数量"识别并撤销它 —— 不猜、不静默：
    /// 匹配不到唯一槽位就明确记原因，绝不把物品凭空丢掉。
    ///
    /// 无静态状态；纯托管（只依赖 Engine.Vector3）。
    /// </summary>
    public static class UiDropPhantomPolicy
    {
        /// <summary>落点判定容差（引擎直接传 ViewPosition，实际上精确相等）。</summary>
        public const float ViewPositionEpsilonSquared = 1e-4f;

        public static bool IsViewPositionDrop(Vector3 position, Vector3 viewPosition) =>
            UiDropPolicy.IsDropAtViewPosition(position, viewPosition);

        /// <summary>
        /// 在"上一帧快照 vs 当前背包"的差分里找**唯一**一个被这次拖拽扣掉的槽：
        /// 该槽数量正好减少 <paramref name="count"/>，且值等于 <paramref name="itemValue"/>。
        /// 精确值匹配失败时退一步按 contents（低 10 位）匹配，但**必须唯一** —— 多个候选
        /// 一律返回 false + reason（绝不猜槽位，宁可明确失败也不动错格子）。
        /// false + reason = 无法唯一确定（调用方记录原因，不接管）。
        /// </summary>
        public static bool TryFindDroppedSlot(int[] beforeValues, int[] beforeCounts,
            int[] currentValues, int[] currentCounts, int itemValue, int count,
            out int slot, out string reason)
        {
            slot = -1;
            reason = null;
            if (count <= 0)
            {
                reason = "count-" + count;
                return false;
            }
            if (beforeValues == null || beforeCounts == null || currentValues == null ||
                currentCounts == null)
            {
                reason = "no-snapshot";
                return false;
            }
            int slots = Math.Min(beforeValues.Length,
                Math.Min(beforeCounts.Length,
                    Math.Min(currentValues.Length, currentCounts.Length)));
            if (slots <= 0)
            {
                reason = "no-slots";
                return false;
            }
            int exact = -1;
            int exactMatches = 0;
            int contents = itemValue & 0x3FF;
            int loose = -1;
            int looseMatches = 0;
            for (int i = 0; i < slots; i++)
            {
                int before = beforeCounts[i];
                if (before <= 0) continue;
                if (before - currentCounts[i] != count) continue;
                // 扣空时 GetSlotValue 返回 0；没扣空时值必须还是同一个物品。
                bool sameValue = currentCounts[i] == 0
                    ? true
                    : currentValues[i] == itemValue;
                if (beforeValues[i] == itemValue && sameValue)
                {
                    exactMatches++;
                    if (exact < 0) exact = i;
                    continue;
                }
                bool sameContents = currentCounts[i] == 0
                    ? true
                    : (currentValues[i] & 0x3FF) == contents;
                if ((beforeValues[i] & 0x3FF) == contents && sameContents)
                {
                    looseMatches++;
                    if (loose < 0) loose = i;
                }
            }
            if (exactMatches == 1)
            {
                slot = exact;
                return true;
            }
            if (exactMatches > 1)
            {
                reason = "ambiguous-exact-" + exactMatches;
                return false;
            }
            if (looseMatches == 1)
            {
                slot = loose;
                return true;
            }
            if (looseMatches > 1)
            {
                reason = "ambiguous-contents-" + looseMatches;
                return false;
            }
            reason = "no-slot-delta";
            return false;
        }
    }
}

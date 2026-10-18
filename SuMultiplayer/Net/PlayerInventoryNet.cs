using System;
using Google.Protobuf;
using PA = SuMultiplayer.Protocol.Action;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 手持物分类（协议 <c>HeldItemAimKind</c> 的领域镜像）。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:141-190
    ///         （ApplyPersistentAimPresentation 按块类型给手部姿势：Bow / Crossbow / Musket / Spear / IsAimable）
    /// </summary>
    public enum HeldItemAimKind
    {
        None = 0,
        Bow = 1,
        Crossbow = 2,
        Musket = 3,
        Spear = 4,
        Aimable = 5
    }

    /// <summary>
    /// 手持物快照（槽位 + 内容 + 数据 + 堆叠）。内容与数据按引擎的 block value 打包在
    /// <see cref="ItemValue"/> 里（Terrain.MakeBlockValue 的值域，两端一致）。
    ///
    /// Source: Mod/ScMultiplayer/Message/PlayerEquipmentMessage.cs:PlayerEquipmentMessage
    ///         （ActiveSlotIndex + 槽位值/数量；旧实现整包发，本重写版把手持物单独成一条小消息）
    /// Source: Survivalcraft/Game/ComponentCreatureModel.cs（InHandItemOffsetOrder 等手部姿势字段）
    ///         + Survivalcraft/Game/ComponentHumanModel.cs:DrawExtras（用 ComponentMiner.ActiveBlockValue 画手持物）
    /// </summary>
    public readonly struct PlayerHeldItemSnapshot
    {
        public PlayerHeldItemSnapshot(int revision, int inventoryVersion, int activeSlotIndex,
            int slotIndex, int itemValue, int itemCount, HeldItemAimKind aimKind, bool aiming)
        {
            Revision = revision;
            InventoryVersion = inventoryVersion;
            ActiveSlotIndex = activeSlotIndex;
            SlotIndex = slotIndex;
            ItemValue = itemValue;
            ItemCount = itemCount;
            AimKind = aimKind;
            Aiming = aiming;
        }

        public int Revision { get; }
        public int InventoryVersion { get; }
        public int ActiveSlotIndex { get; }
        public int SlotIndex { get; }
        public int ItemValue { get; }
        public int ItemCount { get; }
        public HeldItemAimKind AimKind { get; }
        public bool Aiming { get; }

        /// <summary>空手 = 没有值或数量为 0（与引擎 ActiveBlockValue == 0 同口径）。</summary>
        public bool HasItem => ItemValue != 0 && ItemCount > 0;

        /// <summary>变化驱动判据：槽位 / 内容 / 堆叠 / 分类 / 瞄准任一项变化才算变化。</summary>
        public bool EqualsState(in PlayerHeldItemSnapshot other) =>
            SlotIndex == other.SlotIndex && ActiveSlotIndex == other.ActiveSlotIndex &&
            ItemValue == other.ItemValue && ItemCount == other.ItemCount &&
            AimKind == other.AimKind && Aiming == other.Aiming;
    }

    /// <summary>手持物消息的编解码（proto：Protocol/action.proto 的 <c>PlayerEquipment</c>）。</summary>
    public static class PlayerEquipmentCodec
    {
        public static PA.PlayerEquipment ToMessage(int clientId, in PlayerHeldItemSnapshot item)
        {
            return new PA.PlayerEquipment
            {
                ClientId = clientId,
                Revision = item.Revision,
                InventoryVersion = item.InventoryVersion,
                ActiveSlotIndex = item.ActiveSlotIndex,
                SlotIndex = item.SlotIndex,
                ItemValue = item.ItemValue,
                ItemCount = item.ItemCount,
                HasItem = item.HasItem,
                AimKind = (PA.HeldItemAimKind)(int)item.AimKind,
                Aiming = item.Aiming
            };
        }

        public static byte[] Encode(int clientId, in PlayerHeldItemSnapshot item) =>
            ToMessage(clientId, in item).ToByteArray();

        public static bool TryConvert(PA.PlayerEquipment message, out int clientId,
            out PlayerHeldItemSnapshot item, out string error)
        {
            clientId = message?.ClientId ?? -1;
            item = default;
            error = null;
            if (message == null)
            {
                error = "手持物消息为空";
                return false;
            }
            int aimKind = (int)message.AimKind;
            if (aimKind < (int)HeldItemAimKind.None || aimKind > (int)HeldItemAimKind.Aimable)
            {
                error = "手持物分类非法：" + aimKind;
                return false;
            }
            if (message.HasItem && (message.ItemValue == 0 || message.ItemCount <= 0))
            {
                error = "声明 has_item 但 value=" + message.ItemValue + " count=" + message.ItemCount;
                return false;
            }
            item = new PlayerHeldItemSnapshot(message.Revision, message.InventoryVersion,
                message.ActiveSlotIndex, message.SlotIndex, message.ItemValue, message.ItemCount,
                (HeldItemAimKind)aimKind, message.Aiming);
            return true;
        }

        public static bool TryDecode(byte[] payload, out int clientId,
            out PlayerHeldItemSnapshot item, out string error)
        {
            clientId = -1;
            item = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            PA.PlayerEquipment message;
            try
            {
                message = PA.PlayerEquipment.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析手持物消息失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvert(message, out clientId, out item, out error);
        }
    }

    /// <summary>
    /// 背包变更（变更槽位 + 活跃槽 + 版本号）。
    ///
    /// Source: Mod/ScMultiplayer/Message/PlayerEquipmentMessage.cs:PlayerEquipmentMessage
    ///         （HasDelta + SlotIndices + SlotValues/SlotCounts + ActiveSlotIndex + InventoryVersion）
    /// Source: Mod/ScMultiplayer/Message/PlayerActionMessage.cs:InventorySlotIndices/BaseValues/BaseCounts
    /// Source: Mod/ScMultiplayer/Message/PickableSyncMessage.cs:HasInventoryDelta（稀疏增量而不是整包）
    ///
    /// full = true 表示全量关键帧（加入 / 重连 / 追帧恢复）：slots 覆盖 0..SlotsCount-1。
    /// </summary>
    public sealed class PlayerInventoryDelta
    {
        public int ClientId;
        /// <summary>每个 clientId 单调递增；接收端只应用更大的（旧实现的 revision 口径）。</summary>
        public int Revision;
        /// <summary>背包版本号：主机每次权威落地自增，客户端只应用不旧于本地的版本。</summary>
        public int InventoryVersion;
        public int ActiveSlotIndex = -1;
        public bool Full;
        /// <summary>背包槽位总数（full 时为权威槽位数；增量时为 0）。</summary>
        public int SlotsCount;
        public int[] Indices = Array.Empty<int>();
        public int[] Values = Array.Empty<int>();
        public int[] Counts = Array.Empty<int>();
        /// <summary>
        /// 客户端 → 主机：请主机重发一次全量关键帧（刚进世界 / 上一条关键帧没赶上）。
        /// 这种消息不携带槽位，主机收到后立即回 full=true。
        /// </summary>
        public bool RequestKeyframe;

        public bool HasSlots => Indices.Length > 0;

        public int SlotCount => Indices.Length;

        public static PlayerInventoryDelta CreateDelta(int clientId, int revision, int inventoryVersion,
            int activeSlotIndex, int[] indices, int[] values, int[] counts)
        {
            return new PlayerInventoryDelta
            {
                ClientId = clientId,
                Revision = revision,
                InventoryVersion = inventoryVersion,
                ActiveSlotIndex = activeSlotIndex,
                Full = false,
                Indices = indices ?? Array.Empty<int>(),
                Values = values ?? Array.Empty<int>(),
                Counts = counts ?? Array.Empty<int>()
            };
        }

        public static PlayerInventoryDelta CreateFull(int clientId, int revision, int inventoryVersion,
            int activeSlotIndex, int[] values, int[] counts)
        {
            int slotsCount = Math.Min(values?.Length ?? 0, counts?.Length ?? 0);
            var indices = new int[slotsCount];
            for (int i = 0; i < slotsCount; i++) indices[i] = i;
            return new PlayerInventoryDelta
            {
                ClientId = clientId,
                Revision = revision,
                InventoryVersion = inventoryVersion,
                ActiveSlotIndex = activeSlotIndex,
                Full = true,
                SlotsCount = slotsCount,
                Indices = indices,
                Values = values ?? Array.Empty<int>(),
                Counts = counts ?? Array.Empty<int>()
            };
        }

        public static PlayerInventoryDelta CreateKeyframeRequest(int clientId, int revision)
        {
            return new PlayerInventoryDelta
            {
                ClientId = clientId,
                Revision = revision,
                RequestKeyframe = true
            };
        }

        public string Describe()
        {
            if (RequestKeyframe) return "client=" + ClientId + " rev=" + Revision + " request-keyframe";
            return "client=" + ClientId + " rev=" + Revision + " invRev=" + InventoryVersion +
                " active=" + ActiveSlotIndex + (Full ? " full" : " delta") +
                " slots=" + Indices.Length;
        }
    }

    /// <summary>背包消息的编解码（proto：Protocol/action.proto 的 <c>PlayerInventory</c>）。</summary>
    public static class PlayerInventoryCodec
    {
        public static PA.PlayerInventory ToMessage(PlayerInventoryDelta delta)
        {
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            var message = new PA.PlayerInventory
            {
                ClientId = delta.ClientId,
                Revision = delta.Revision,
                InventoryVersion = delta.InventoryVersion,
                ActiveSlotIndex = delta.ActiveSlotIndex,
                Full = delta.Full,
                SlotsCount = delta.Full ? delta.SlotsCount : delta.Indices.Length,
                RequestKeyframe = delta.RequestKeyframe
            };
            int count = Math.Min(delta.Indices.Length, Math.Min(delta.Values.Length, delta.Counts.Length));
            for (int i = 0; i < count; i++)
            {
                message.Slots.Add(new PA.InventorySlotChange
                {
                    Index = delta.Indices[i],
                    Value = delta.Values[i],
                    Count = delta.Counts[i]
                });
            }
            return message;
        }

        public static byte[] Encode(PlayerInventoryDelta delta) => ToMessage(delta).ToByteArray();

        public static bool TryConvert(PA.PlayerInventory message, out PlayerInventoryDelta delta,
            out string error)
        {
            delta = null;
            error = null;
            if (message == null)
            {
                error = "背包消息为空";
                return false;
            }
            if (message.RequestKeyframe)
            {
                // 关键帧请求：不携带槽位（合法），主机收到后回 full=true。
                delta = new PlayerInventoryDelta
                {
                    ClientId = message.ClientId,
                    Revision = message.Revision,
                    InventoryVersion = message.InventoryVersion,
                    ActiveSlotIndex = message.ActiveSlotIndex,
                    RequestKeyframe = true
                };
                return true;
            }
            if (message.Slots.Count == 0)
            {
                error = "背包消息没有任何槽位（空增量没有意义，不应用）";
                return false;
            }
            int count = message.Slots.Count;
            var indices = new int[count];
            var values = new int[count];
            var counts = new int[count];
            for (int i = 0; i < count; i++)
            {
                PA.InventorySlotChange slot = message.Slots[i];
                if (slot == null)
                {
                    error = "第 " + (i + 1) + " 个槽位为空";
                    return false;
                }
                if (slot.Index < 0)
                {
                    error = "第 " + (i + 1) + " 个槽位下标非法：" + slot.Index;
                    return false;
                }
                if (slot.Count < 0)
                {
                    error = "槽位 " + slot.Index + " 的数量为负：" + slot.Count;
                    return false;
                }
                if (slot.Count > 0 && slot.Value == 0)
                {
                    error = "槽位 " + slot.Index + " 有数量却是空值";
                    return false;
                }
                indices[i] = slot.Index;
                // 空槽的 value 无意义：统一按 0 处理（不是兜底，是同一状态的标准写法）。
                values[i] = slot.Count == 0 ? 0 : slot.Value;
                counts[i] = slot.Count;
            }
            if (message.Full && message.SlotsCount < 0)
            {
                error = "全量背包的槽位数为负：" + message.SlotsCount;
                return false;
            }
            delta = new PlayerInventoryDelta
            {
                ClientId = message.ClientId,
                Revision = message.Revision,
                InventoryVersion = message.InventoryVersion,
                ActiveSlotIndex = message.ActiveSlotIndex,
                Full = message.Full,
                SlotsCount = message.Full ? message.SlotsCount : 0,
                Indices = indices,
                Values = values,
                Counts = counts
            };
            return true;
        }

        public static bool TryDecode(byte[] payload, out PlayerInventoryDelta delta, out string error)
        {
            delta = null;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            PA.PlayerInventory message;
            try
            {
                message = PA.PlayerInventory.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析背包消息失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvert(message, out delta, out error);
        }
    }

    /// <summary>
    /// 背包的纯数组运算：变更检测、增量构造、槽位套用。
    /// 生产侧（引擎 IInventory）与自检共用同一组判据，避免"两边各写一套"。
    ///
    /// Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.GetSlotValue/GetSlotCount
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:606-652（EquipmentSnapshotsEqual / GetInventoryDelta）
    /// </summary>
    public static class PlayerInventoryMath
    {
        /// <summary>
        /// 同步的背包槽位数上限（= 引擎快捷栏 10 槽）。
        /// Source: Survivalcraft/Game/ComponentInventory.cs:ShortInventorySlotsCount = 10
        /// 创造仓库（<c>ComponentCreativeInventory</c> 有 1622 槽）只同步前 10 槽（快捷栏）：
        /// 整包下发会重演旧实现"创造背包每次同步巨大网络尖峰"的问题
        /// （Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:409-413）。
        /// 记录里的完整槽位仍由主机的 5 秒抓取 + 离开时抓取落盘。
        /// </summary>
        public const int MaxSyncSlots = 10;

        public static bool ArraysEqual(int[] left, int[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        /// <summary>
        /// 两次背包快照是否相同（值 + 数量 + 活跃槽）。用于"(e) 未变化不发包"。
        /// </summary>
        public static bool SnapshotsEqual(int[] leftValues, int[] leftCounts, int leftActiveSlot,
            int[] rightValues, int[] rightCounts, int rightActiveSlot) =>
            leftActiveSlot == rightActiveSlot && ArraysEqual(leftValues, rightValues) &&
            ArraysEqual(leftCounts, rightCounts);

        /// <summary>
        /// 构造变更槽位；槽位数变化（创造仓库 ↔ 普通仓库）或第一次采样时返回全量关键帧。
        /// 返回 null = 没有变化（**不发包**）。
        /// </summary>
        public static PlayerInventoryDelta BuildDelta(int clientId, int revision, int inventoryVersion,
            int activeSlotIndex, int[] previousValues, int[] previousCounts, int previousActiveSlot,
            int[] currentValues, int[] currentCounts)
        {
            if (currentValues == null || currentCounts == null ||
                currentValues.Length != currentCounts.Length)
                return null;
            if (previousValues == null || previousCounts == null ||
                previousValues.Length != currentValues.Length ||
                previousCounts.Length != currentCounts.Length)
            {
                return PlayerInventoryDelta.CreateFull(clientId, revision, inventoryVersion,
                    activeSlotIndex, currentValues, currentCounts);
            }
            var indices = new System.Collections.Generic.List<int>();
            var values = new System.Collections.Generic.List<int>();
            var counts = new System.Collections.Generic.List<int>();
            for (int i = 0; i < currentValues.Length; i++)
            {
                if (currentValues[i] == previousValues[i] && currentCounts[i] == previousCounts[i])
                    continue;
                indices.Add(i);
                values.Add(currentValues[i]);
                counts.Add(currentCounts[i]);
            }
            bool activeSlotChanged = activeSlotIndex != previousActiveSlot;
            if (indices.Count == 0 && !activeSlotChanged) return null;
            PlayerInventoryDelta delta = PlayerInventoryDelta.CreateDelta(clientId, revision,
                inventoryVersion, activeSlotIndex, indices.ToArray(), values.ToArray(),
                counts.ToArray());
            return delta;
        }

        /// <summary>
        /// 把一条变更套用到纯数组（自检用的参考实现；引擎侧走 IInventory 的同一条判据）。
        /// 返回值 = 是否有槽位真的变了；false + error = 载荷非法（整条丢弃，不半条应用）。
        /// </summary>
        public static bool TryApply(PlayerInventoryDelta delta, int[] values, int[] counts,
            out bool changed, out string error)
        {
            changed = false;
            error = null;
            if (delta == null)
            {
                error = "增量对象为空";
                return false;
            }
            if (values == null || counts == null || values.Length != counts.Length)
            {
                error = "目标背包数组为空或长度不一致";
                return false;
            }
            if (delta.Full && delta.SlotsCount > values.Length)
            {
                error = "全量背包声明 " + delta.SlotsCount + " 槽，目标只有 " + values.Length + " 槽";
                return false;
            }
            int slots = Math.Min(delta.Indices.Length, Math.Min(delta.Values.Length, delta.Counts.Length));
            for (int i = 0; i < slots; i++)
            {
                int index = delta.Indices[i];
                int value = delta.Values[i];
                int count = delta.Counts[i];
                if (index < 0 || index >= values.Length)
                {
                    error = "槽位下标越界：" + index + "（目标 " + values.Length + " 槽）";
                    return false;
                }
                if (count < 0)
                {
                    error = "槽位 " + index + " 的数量为负：" + count;
                    return false;
                }
                if (count > 0 && value == 0)
                {
                    error = "槽位 " + index + " 有数量却是空值";
                    return false;
                }
                int normalized = count == 0 ? 0 : value;
                if (values[index] == normalized && counts[index] == count) continue;
                values[index] = normalized;
                counts[index] = count;
                changed = true;
            }
            return true;
        }
    }
}

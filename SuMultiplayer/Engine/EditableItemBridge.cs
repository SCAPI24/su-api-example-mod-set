using System;
using System.Collections.Generic;
using System.Text;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>Client item-dialog commit sink (implemented by the sync module).</summary>
    public interface IEditableItemRequestSink
    {
        bool TrySubmitItemEdit(in EditableItemEditRequestData request, out string reason);
    }

    public readonly struct EditableItemEditRequestData
    {
        public EditableItemEditRequestData(long sequence, int kind, int slotIndex,
            int expectedValue, byte[] baseData, byte[] data)
        {
            Sequence = sequence;
            Kind = kind;
            SlotIndex = slotIndex;
            ExpectedValue = expectedValue;
            BaseData = baseData ?? Array.Empty<byte>();
            Data = data ?? Array.Empty<byte>();
        }

        public long Sequence { get; }
        public int Kind { get; }
        public int SlotIndex { get; }
        public int ExpectedValue { get; }
        public byte[] BaseData { get; }
        public byte[] Data { get; }
    }

    /// <summary>
    /// Adapter for SubsystemEditableItemBehavior.m_itemsData (truth table / memory
    /// bank item payloads). Host sends the authoritative registry; clients apply it
    /// to their replica. Client item dialogs submit through this bridge instead of
    /// writing the replica directly.
    /// </summary>
    public sealed class EditableItemBridge
    {
        public readonly struct ItemEntry
        {
            public ItemEntry(int kind, int dataId, byte[] data)
            {
                Kind = kind;
                DataId = dataId;
                Data = data ?? Array.Empty<byte>();
            }

            public int Kind { get; }
            public int DataId { get; }
            public byte[] Data { get; }
        }

        private readonly NetworkAvatarSpawner m_spawner;
        private Project m_project;
        private int m_sessionSerial;
        private SuSubsystemTruthTableCircuitBlockBehavior m_truth;
        private SuSubsystemMemoryBankBlockBehavior m_memory;
        private IEditableItemRequestSink m_sink;
        private IInventory m_pendingItemInventory;
        private int m_pendingItemSlot = -1;
        /// <summary>
        /// 主机物品内容缓存：SubsystemEditableItemBehavior 的 m_itemsData 会按
        /// “扫描到的物品”做 GC；主机远端化身的背包不一定被扫描，刚由客户端写入的
        /// data id 可能在下一次 GC 被删。这里保留主机权威副本，捕获时补回。
        /// </summary>
        private readonly Dictionary<string, byte[]> m_hostItemCache =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> m_itemDataCache =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public EditableItemBridge(NetworkAvatarSpawner spawner)
        {
            m_spawner = spawner;
        }

        public void SetRequestSink(IEditableItemRequestSink sink) => m_sink = sink;

        public int SessionSerial => m_sessionSerial;

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                m_project = project;
                m_sessionSerial++;
                m_hostItemCache.Clear();
                m_itemDataCache.Clear();
                m_truth = null;
                m_memory = null;
            }
            if (m_project == null) return;
            if (m_truth == null)
                m_truth = m_project.FindSubsystem<SuSubsystemTruthTableCircuitBlockBehavior>(false);
            if (m_memory == null)
                m_memory = m_project.FindSubsystem<SuSubsystemMemoryBankBlockBehavior>(false);

        }

        public bool TryCapture(List<ItemEntry> target, out long fingerprint, out string reason)
        {
            target?.Clear();
            fingerprint = 0L;
            reason = null;
            Pump();
            if (m_truth == null || m_memory == null)
            {
                reason = "editable-item-subsystem-missing";
                return false;
            }
            try
            {
                Append(target, EditableKinds.TruthTable, m_truth.CaptureNetworkItemDataBytes());
                Append(target, EditableKinds.MemoryBank, m_memory.CaptureNetworkItemDataBytes());
                fingerprint = Hash(target);
                return true;
            }
            catch (Exception error)
            {
                reason = "editable-item-capture:" + error.GetType().Name + ":" + error.Message;
                return false;
            }
        }

        public bool TryApply(IReadOnlyList<ItemEntry> entries, out string reason)
        {
            reason = null;
            Pump();
            if (m_truth == null || m_memory == null)
            {
                reason = "editable-item-subsystem-missing";
                return false;
            }
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    ItemEntry entry = entries[i];
                    if (entry.Kind == EditableKinds.TruthTable)
                    {
                        m_truth.ApplyNetworkItemDataBytes(entry.DataId, entry.Data);
                    }
                    else if (entry.Kind == EditableKinds.MemoryBank)
                    {
                        m_memory.ApplyNetworkItemDataBytes(entry.DataId, entry.Data);
                    }
                    else
                    {
                        reason = "editable-item-kind:" + entry.Kind;
                        return false;
                    }
                    m_itemDataCache[CacheKey(entry.Kind, entry.DataId)] = entry.Data;
                }
                return true;
            }
            catch (Exception error)
            {
                reason = "editable-item-apply:" + error.GetType().Name + ":" + error.Message;
                return false;
            }
        }

        /// <summary>
        /// 物品内容会按扫描到的物品做 GC；打开编辑对话框前把缓存里的权威 payload
        /// 重新写回子系统，避免“槽位 data id 正确但 GetItemData 返回 null”。
        /// </summary>
        public bool TryReapplyCachedItem(int kind, int dataId)
        {
            if (dataId < 0 || dataId >= 1000) return false;
            if (!m_itemDataCache.TryGetValue(CacheKey(kind, dataId), out byte[] payload))
                return false;
            Pump();
            if (m_truth == null || m_memory == null) return false;
            try
            {
                if (kind == EditableKinds.TruthTable)
                {
                    if (m_truth.GetItemData(dataId) != null) return true;
                    m_truth.ApplyNetworkItemDataBytes(dataId, payload);
                }
                else if (kind == EditableKinds.MemoryBank)
                {
                    if (m_memory.GetItemData(dataId) != null) return true;
                    m_memory.ApplyNetworkItemDataBytes(dataId, payload);
                }
                else return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 客户端提交成功后先本地乐观写入同一 data id：UI 立即显示编辑后的内容，
        /// 主机权威状态/结果随后覆盖；主机拒绝时由权威 state 回滚。
        /// </summary>
        public bool ApplyClientOptimisticItem(int kind, int dataId, byte[] data)
        {
            Pump();
            try
            {
                if (kind == EditableKinds.TruthTable)
                    m_truth?.ApplyNetworkItemDataBytes(dataId, data);
                else if (kind == EditableKinds.MemoryBank)
                    m_memory?.ApplyNetworkItemDataBytes(dataId, data);
                else return false;
                m_itemDataCache[CacheKey(kind, dataId)] = data ?? Array.Empty<byte>();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Client dialog commit: validate local slot, hand request to module.</summary>
        public bool SubmitClientItemEdit(int kind, IInventory inventory, int slotIndex,
            ComponentPlayer player, int expectedValue, byte[] baseData, byte[] data,
            out string reason)
        {
            reason = null;
            Pump();
            if (inventory == null || slotIndex < 0 || slotIndex >= inventory.SlotsCount)
            {
                reason = "item-slot-invalid";
                return false;
            }
            if (inventory.GetSlotCount(slotIndex) <= 0 ||
                inventory.GetSlotValue(slotIndex) != expectedValue)
            {
                reason = "item-slot-changed";
                return false;
            }
            if (m_sink == null)
            {
                reason = "editable-item-sink-missing";
                return false;
            }
            m_pendingItemInventory = inventory;
            m_pendingItemSlot = slotIndex;
            var request = new EditableItemEditRequestData(0L, kind, slotIndex, expectedValue,
                baseData, data);
            return m_sink.TrySubmitItemEdit(in request, out reason);
        }

        /// <summary>Host: validate/apply a client item edit and return authoritative value.</summary>
        /// <summary>
        /// 主机放置可编辑方块后，把物品 data id 的内容写进主机 block data 表。
        /// 这样之后挖掘掉落（OnItemHarvested）能复制到新 item data id。
        /// </summary>
        public bool TryCopyItemDataToBlock(int x, int y, int z, int contents, int itemDataId,
            out string reason)
        {
            reason = null;
            Pump();
            int kind = contents == 188 ? EditableKinds.TruthTable :
                contents == 186 ? EditableKinds.MemoryBank : -1;
            if (kind < 0) { reason = "not-editable-contents"; return false; }
            Dictionary<int, byte[]> items = kind == EditableKinds.TruthTable
                ? m_truth?.CaptureNetworkItemDataBytes()
                : m_memory?.CaptureNetworkItemDataBytes();
            if (items == null || !items.TryGetValue(itemDataId, out byte[] data))
            { reason = "item-data-missing:" + itemDataId; return false; }
            IEditableBlockContentHost host = FindBlockHost(kind);
            if (host == null) { reason = "block-host-missing"; return false; }
            if (!host.ApplyContent(x, y, z, contents, data, hostAuthoritative: true, out reason))
                return false;
            return true;
        }

        private IEditableBlockContentHost FindBlockHost(int kind)
        {
            Project project = m_project;
            if (project == null) return null;
            foreach (IEditableBlockContentHost host in project.FindSubsystems<IEditableBlockContentHost>())
                if (host != null && host.EditableKind == kind) return host;
            return null;
        }

        /// <summary>
        /// 客户端本地乐观写入**同一 data id**（仅 id>0）：编辑后立即放置时，
        /// 方块复制的是这份新内容，而不是旧 data id 对应的空数据。
        /// </summary>
        public bool ApplyClientOptimisticItemAtExistingId(int kind, int dataId, byte[] data)
        {
            if (dataId <= 0 || dataId >= 1000) return false;
            Pump();
            if (kind == EditableKinds.TruthTable)
            {
                if (m_truth == null || !m_truth.StoreNetworkItemDataAtId(data, dataId)) return false;
            }
            else if (kind == EditableKinds.MemoryBank)
            {
                if (m_memory == null || !m_memory.StoreNetworkItemDataAtId(data, dataId)) return false;
            }
            else return false;
            m_itemDataCache[CacheKey(kind, dataId)] = data ?? Array.Empty<byte>();
            return true;
        }

        public bool TryApplyHostItemEdit(int clientId, in EditableItemEditRequestData request,
            out int dataId, out int authoritativeValue, out byte[] authoritativeData,
            out string reason)
        {
            dataId = -1;
            authoritativeValue = 0;
            authoritativeData = null;
            reason = null;
            Pump();
            if (m_truth == null || m_memory == null)
            {
                reason = "editable-item-subsystem-missing";
                return false;
            }
            if (m_spawner == null || !m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer?.ComponentMiner?.Inventory == null)
            {
                reason = "client-" + clientId + "-inventory-missing";
                return false;
            }
            IInventory inventory = playerData.ComponentPlayer.ComponentMiner.Inventory;
            int slot = request.SlotIndex;
            if (slot < 0 || slot >= inventory.SlotsCount || inventory.GetSlotCount(slot) <= 0)
            {
                reason = "slot-invalid:" + slot;
                return false;
            }
            int currentValue = inventory.GetSlotValue(slot);
            if (currentValue != request.ExpectedValue ||
                global::Game.Terrain.ExtractContents(currentValue) !=
                    EditableKinds.BlockContents(request.Kind))
            {
                reason = "slot-value-changed";
                return false;
            }
            int existingId = global::Game.Terrain.ExtractData(currentValue);
            int stored;
            if (existingId > 0 && existingId < 1000)
            {
                // 复用物品现有 data id：编辑后物品 value 不变，客户端立刻放置也能
                // 指向同一份刚写入的数据，不会拿到尚未回包的新 id。
                bool ok = request.Kind == EditableKinds.TruthTable
                    ? m_truth.StoreNetworkItemDataAtId(request.Data, existingId)
                    : m_memory.StoreNetworkItemDataAtId(request.Data, existingId);
                stored = ok ? existingId : -1;
            }
            else
            {
                stored = request.Kind == EditableKinds.TruthTable
                    ? m_truth.StoreNetworkItemDataBytes(request.Data)
                    : m_memory.StoreNetworkItemDataBytes(request.Data);
            }
            if (stored < 0 || stored >= 1000)
            {
                reason = "store-failed";
                return false;
            }
            authoritativeValue = global::Game.Terrain.ReplaceData(currentValue, stored);
            inventory.RemoveSlotItems(slot, int.MaxValue);
            inventory.AddSlotItems(slot, authoritativeValue, 1);
            dataId = stored;
            authoritativeData = request.Data;
            CacheHostItem(request.Kind, stored, request.Data);
            return true;
        }

        /// <summary>Host-local item dialog commit (for the same broadcast path).</summary>
        public bool TryApplyHostLocalItemEdit(int kind, IInventory inventory, int slotIndex,
            int expectedValue, byte[] data, out int dataId, out int authoritativeValue,
            out string reason)
        {
            dataId = -1;
            authoritativeValue = 0;
            reason = null;
            Pump();
            if (inventory == null || slotIndex < 0 || slotIndex >= inventory.SlotsCount ||
                inventory.GetSlotCount(slotIndex) <= 0 ||
                inventory.GetSlotValue(slotIndex) != expectedValue)
            {
                reason = "local-slot-changed";
                return false;
            }
            int existingId = global::Game.Terrain.ExtractData(expectedValue);
            int stored;
            if (existingId > 0 && existingId < 1000)
            {
                bool ok = kind == EditableKinds.TruthTable
                    ? m_truth.StoreNetworkItemDataAtId(data, existingId)
                    : m_memory.StoreNetworkItemDataAtId(data, existingId);
                stored = ok ? existingId : -1;
            }
            else
            {
                stored = kind == EditableKinds.TruthTable
                    ? m_truth.StoreNetworkItemDataBytes(data)
                    : m_memory.StoreNetworkItemDataBytes(data);
            }
            if (stored < 0) return false;
            authoritativeValue = global::Game.Terrain.ReplaceData(expectedValue, stored);
            inventory.RemoveSlotItems(slotIndex, int.MaxValue);
            inventory.AddSlotItems(slotIndex, authoritativeValue, 1);
            dataId = stored;
            CacheHostItem(kind, stored, data);
            Log.Information("[SuMP][editable-item] host local edit kind=" + kind +
                " slot=" + slotIndex + " dataId=" + stored + " payloadBytes=" +
                (data == null ? 0 : data.Length) + " first=" +
                (data != null && data.Length > 0 ? data[0].ToString() : "0"));
            return true;
        }

        /// <summary>Client: apply authoritative item data + slot value to the local inventory.</summary>
        public bool TryApplyAuthoritativeItem(int kind, int dataId, int authoritativeValue,
            IInventory inventory, int slotIndex, out string reason)
        {
            reason = null;
            Pump();
            try
            {
                // 必须写回**发起对话框时的那个 inventory**：本机可能同时存在
                // 玩家背包 / 创造仓库等多个槽位表，用 FindLocalInventory 猜会写到别的槽位。
                if (m_pendingItemInventory != null && m_pendingItemSlot >= 0)
                {
                    inventory = m_pendingItemInventory;
                    slotIndex = m_pendingItemSlot;
                }
                if (inventory != null && slotIndex >= 0 && slotIndex < inventory.SlotsCount &&
                    authoritativeValue != 0 && authoritativeValue != inventory.GetSlotValue(slotIndex))
                {
                    inventory.RemoveSlotItems(slotIndex, int.MaxValue);
                    inventory.AddSlotItems(slotIndex, authoritativeValue, 1);
                }
                m_pendingItemInventory = null;
                m_pendingItemSlot = -1;
                return true;
            }
            catch (Exception error)
            {
                reason = "apply-item-slot:" + error.GetType().Name + ":" + error.Message;
                return false;
            }
        }

        public IInventory FindLocalInventory(out int activeSlot)
        {
            activeSlot = -1;
            Pump();
            Project project = m_project;
            if (project == null || m_spawner == null ||
                !m_spawner.TryGetLocalPlayer(project, out PlayerData local, out _))
                return null;
            ComponentMiner miner = local?.ComponentPlayer?.ComponentMiner;
            activeSlot = miner?.Inventory?.ActiveSlotIndex ?? -1;
            return miner?.Inventory;
        }

        private void CacheHostItem(int kind, int dataId, byte[] data)
        {
            if (dataId < 0 || dataId >= 1000) return;
            byte[] payload = data ?? Array.Empty<byte>();
            m_hostItemCache[CacheKey(kind, dataId)] = payload;
            m_itemDataCache[CacheKey(kind, dataId)] = payload;
        }

        private void CaptureCurrentItemData()
        {
            try
            {
                if (m_truth != null)
                {
                    foreach (KeyValuePair<int, byte[]> item in m_truth.CaptureNetworkItemDataBytes())
                    {
                        if (item.Value == null || item.Value.Length == 0) continue;
                        m_itemDataCache[CacheKey(EditableKinds.TruthTable, item.Key)] = item.Value;
                    }
                }
                if (m_memory != null)
                {
                    foreach (KeyValuePair<int, byte[]> item in m_memory.CaptureNetworkItemDataBytes())
                    {
                        if (item.Value == null || item.Value.Length == 0) continue;
                        m_itemDataCache[CacheKey(EditableKinds.MemoryBank, item.Key)] = item.Value;
                    }
                }
            }
            catch (Exception)
            {
                // 诊断/表现缓存失败不能打断主流程。
            }
        }

        private void AppendHostCache(List<ItemEntry> target)
        {
            if (target == null || m_hostItemCache.Count == 0) return;
            foreach (KeyValuePair<string, byte[]> item in m_hostItemCache)
            {
                string[] parts = item.Key.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int kind) ||
                    !int.TryParse(parts[1], out int dataId)) continue;
                bool present = false;
                for (int i = 0; i < target.Count; i++)
                    if (target[i].Kind == kind && target[i].DataId == dataId)
                    {
                        present = true;
                        break;
                    }
                if (!present) target.Add(new ItemEntry(kind, dataId, item.Value));
            }
        }

        private static string CacheKey(int kind, int dataId) =>
            kind.ToString() + ":" + dataId.ToString();

        private static void Append(List<ItemEntry> target, int kind,
            Dictionary<int, byte[]> items)
        {
            if (target == null || items == null) return;
            var keys = new List<int>(items.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
                target.Add(new ItemEntry(kind, keys[i], items[keys[i]] ?? Array.Empty<byte>()));
        }

        private static long Hash(IReadOnlyList<ItemEntry> entries)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                for (int i = 0; i < entries.Count; i++)
                {
                    hash ^= (uint)entries[i].Kind;
                    hash *= 1099511628211L;
                    hash ^= (uint)entries[i].DataId;
                    hash *= 1099511628211L;
                    byte[] data = entries[i].Data;
                    if (data == null) continue;
                    for (int b = 0; b < data.Length; b++)
                    {
                        hash ^= data[b];
                        hash *= 1099511628211L;
                    }
                }
                return hash;
            }
        }
    }
}

using System.Collections.Generic;
using Engine;
using Game;

namespace ScMultiplayer
{
    public sealed class SuSubsystemTruthTableCircuitBlockBehavior :
        SubsystemTruthTableCircuitBlockBehavior
    {
        // Source: Survivalcraft/Game/SubsystemTruthTableCircuitBlockBehavior.cs:OnEditInventoryItem
        public override bool OnEditInventoryItem(IInventory inventory, int slotIndex,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditInventoryItem(inventory, slotIndex, componentPlayer);

            int value = inventory.GetSlotValue(slotIndex);
            int count = inventory.GetSlotCount(slotIndex);
            TruthTableData data = GetItemData(Terrain.ExtractData(value));
            data = data != null ? (TruthTableData)data.Copy() : new TruthTableData();
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditTruthTableDialog(data, delegate
                {
                    if (!multiplayer.TrySubmitEditableItemData(
                        EditableDataKind.TruthTable, inventory, slotIndex,
                        componentPlayer, value, data.SaveString()))
                    {
                        int dataId = StoreItemDataAtUniqueId(data);
                        inventory.RemoveSlotItems(slotIndex, count);
                        inventory.AddSlotItems(slotIndex, Terrain.ReplaceData(value, dataId), 1);
                    }
                }));
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemTruthTableCircuitBlockBehavior.cs:OnEditBlock
        public override bool OnEditBlock(int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditBlock(x, y, z, value, componentPlayer);

            Point3 point = new Point3(x, y, z);
            TruthTableData currentData = GetBlockData(point);
            TruthTableData data = currentData != null
                ? (TruthTableData)currentData.Copy()
                : new TruthTableData();
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditTruthTableDialog(data, delegate
                {
                    if (!multiplayer.TrySubmitEditableBlockData(
                        EditableDataKind.TruthTable, point, componentPlayer,
                        value, data.SaveString()))
                    {
                        ApplyNetworkBlockData(point, data.SaveString());
                    }
                }));
            return true;
        }

        internal int StoreNetworkItemData(string payload)
        {
            var data = new TruthTableData();
            data.LoadString(payload ?? string.Empty);
            return StoreItemDataAtUniqueId(data);
        }

        internal void ApplyNetworkItemData(int dataId, string payload)
        {
            var data = new TruthTableData();
            data.LoadString(payload ?? string.Empty);
            GetItemDataDictionary()[dataId] = data;
        }

        internal void ApplyNetworkBlockData(Point3 point, string payload)
        {
            var data = new TruthTableData();
            data.LoadString(payload ?? string.Empty);
            SetBlockData(point, data);
            int value = SubsystemTerrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
            if (Terrain.ExtractContents(value) != 188) return;
            int face = ((TruthTableCircuitBlock)BlocksManager.Blocks[188]).GetFace(value);
            SubsystemElectricity electricity =
                SubsystemTerrain.Project.FindSubsystem<SubsystemElectricity>(false);
            ElectricElement element = electricity?.GetElectricElement(
                point.X, point.Y, point.Z, face);
            if (element != null)
                electricity.QueueElectricElementForSimulation(element, electricity.CircuitStep + 1);
        }

        internal Dictionary<int, string> CaptureNetworkItemData()
        {
            var result = new Dictionary<int, string>();
            foreach (KeyValuePair<int, TruthTableData> item in GetItemDataDictionary())
                result[item.Key] = item.Value?.SaveString() ?? string.Empty;
            return result;
        }

        internal Dictionary<Point3, string> CaptureNetworkBlockData()
        {
            var result = new Dictionary<Point3, string>();
            foreach (KeyValuePair<Point3, TruthTableData> item in GetBlockDataDictionary())
                result[item.Key] = item.Value?.SaveString() ?? string.Empty;
            return result;
        }

        private Dictionary<int, TruthTableData> GetItemDataDictionary() =>
            ScMultiplayer.ModManager.ModParentField.GetParentField<Dictionary<int, TruthTableData>>(
                this, "m_itemsData", typeof(SubsystemEditableItemBehavior<TruthTableData>));

        private Dictionary<Point3, TruthTableData> GetBlockDataDictionary() =>
            ScMultiplayer.ModManager.ModParentField.GetParentField<Dictionary<Point3, TruthTableData>>(
                this, "m_blocksData", typeof(SubsystemEditableItemBehavior<TruthTableData>));
    }
}

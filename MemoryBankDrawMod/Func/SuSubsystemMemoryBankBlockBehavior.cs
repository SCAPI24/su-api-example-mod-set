using Engine;
using Game;
using GameEntitySystem;

namespace MemoryBankDrawMod
{
    // Source: SubsystemMemoryBankBlockBehavior.cs — replaces dialog creation
    public class SuSubsystemMemoryBankBlockBehavior : SubsystemMemoryBankBlockBehavior
    {
        // Source: SubsystemMemoryBankBlockBehavior.OnEditInventoryItem
        // Replaces EditMemoryBankDialog with SuEditMemoryBankDialog
        public override bool OnEditInventoryItem(IInventory inventory, int slotIndex, ComponentPlayer componentPlayer)
        {
            int value = inventory.GetSlotValue(slotIndex);
            int count = inventory.GetSlotCount(slotIndex);
            int id = Terrain.ExtractData(value);
            MemoryBankData memoryBankData = GetItemData(id);
            if (memoryBankData != null)
            {
                memoryBankData = (MemoryBankData)memoryBankData.Copy();
            }
            else
            {
                memoryBankData = new MemoryBankData();
            }
            DialogsManager.ShowDialog(componentPlayer.GuiWidget, new SuEditMemoryBankDialog(memoryBankData, delegate
            {
                int data = StoreItemDataAtUniqueId(memoryBankData);
                int value2 = Terrain.ReplaceData(value, data);
                inventory.RemoveSlotItems(slotIndex, count);
                inventory.AddSlotItems(slotIndex, value2, 1);
            }));
            return true;
        }

        // Source: SubsystemMemoryBankBlockBehavior.OnEditBlock
        public override bool OnEditBlock(int x, int y, int z, int value, ComponentPlayer componentPlayer)
        {
            MemoryBankData memoryBankData = GetBlockData(new Point3(x, y, z)) ?? new MemoryBankData();
            DialogsManager.ShowDialog(componentPlayer.GuiWidget, new SuEditMemoryBankDialog(memoryBankData, delegate
            {
                SetBlockData(new Point3(x, y, z), memoryBankData);
                int face = ((MemoryBankBlock)BlocksManager.Blocks[186]).GetFace(value);
                SubsystemElectricity subsystemElectricity = Project.FindSubsystem<SubsystemElectricity>(throwOnError: true);
                ElectricElement electricElement = subsystemElectricity.GetElectricElement(x, y, z, face);
                if (electricElement != null)
                {
                    subsystemElectricity.QueueElectricElementForSimulation(electricElement, subsystemElectricity.CircuitStep + 1);
                }
            }));
            return true;
        }
    }
}

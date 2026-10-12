using Engine;
using Game;

namespace SuEventBus
{
    /// <summary>
    /// 请求：为某个电学方块创建 <see cref="ElectricElement"/>。
    /// 订阅者（如 ScMultiplayer）填 <see cref="Result"/> 即可接管，否则调用方用原版实现。
    /// Source: Survivalcraft/Game/ButtonBlock.cs:ButtonBlock.CreateElectricElement
    /// </summary>
    public sealed class CircuitElementRequest
    {
        public SubsystemElectricity SubsystemElectricity;
        public int Value;
        public int X;
        public int Y;
        public int Z;
        public ElectricElement Result;
    }

    /// <summary>
    /// 请求：这次"编辑键"操作由谁弹窗处理。
    /// 订阅者可以：
    ///   · Suppressed = true —— 本次不要弹窗（远端玩家编辑的本地抑制）；
    ///   · Handled   = true —— 我接管（自己弹窗并自行提交）。
    /// 都不设则调用方按原版行为弹窗。
    /// Source: Survivalcraft/Game/SubsystemButtonBlockBehavior.cs:OnEditBlock/OnEditInventoryItem
    /// </summary>
    public sealed class ButtonEditRequest
    {
        public ComponentPlayer Player;
        public bool IsBlock;
        public Point3 Point;
        public IInventory Inventory;
        public int SlotIndex;
        public int Value;
        public bool Handled;
        public bool Suppressed;
    }

    /// <summary>与 <see cref="ButtonEditRequest"/> 同形，用于信道格的通道号编辑。</summary>
    public sealed class ChannelEditRequest
    {
        public ComponentPlayer Player;
        public bool IsBlock;
        public Point3 Point;
        public IInventory Inventory;
        public int SlotIndex;
        public int Value;
        public bool Handled;
        public bool Suppressed;
    }

    /// <summary>
    /// 请求：提交一次信道通道号修改。
    /// Handled = true 表示订阅者负责落地（联机时由主机权威应用），调用方不再本地写。
    /// </summary>
    public sealed class ChannelEditSubmitRequest
    {
        public ComponentPlayer Player;
        public bool IsBlock;
        public Point3 Point;
        public IInventory Inventory;
        public int SlotIndex;
        public int OldValue;
        public int NewData;
        public bool Handled;
    }
}

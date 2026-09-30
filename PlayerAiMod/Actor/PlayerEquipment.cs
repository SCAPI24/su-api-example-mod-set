using System;
using System.Collections.Generic;
using Game;

namespace PlayerAiMod
{
    /// <summary>
    /// **把武器拿到手上**（玩家 2026-09-26 明确要求："攻击要换上武器，创造模式用钻石刀"）。
    ///
    /// 这是本 Mod 里**唯一**会改玩家背包的地方，所以单独一个类、并写清出处：
    ///
    ///   · **创造模式"取物品"是引擎自带的玩法**，不是外挂 —— 原版"中键取方块"
    ///     （`ComponentPlayer.cs:304-330`）做的就是：在快捷栏 0..9 里找同物品的槽 → 找空槽 →
    ///     都没有就用当前活动槽，然后 `RemoveSlotItems` + `AddSlotItems(1)` + `ActiveSlotIndex = 槽`。
    ///     这里走的是**同一条路**（`IInventory.cs` 的公开接口），只是"取什么"由 AI 决定；
    ///   · SC 里没有叫 "Sword" 的东西，近战武器是**砍刀 Machete**，钻石那把是 `DiamondMacheteBlock`
    ///     （`DiamondMacheteBlock.cs:3`、`MacheteBlock.cs:6`）—— 用户说的"钻石刀"就是它；
    ///   · 生存模式没有创造背包，就退回"背包里**近战威力最大**的那件"并切到它
    ///     （威力取自 `Block.GetMeleePower`，`Block.cs:333`：方块默认 1，武器更高）。
    /// </summary>
    /// <summary>
    /// "我包着一层，里面才是真的观察层"。
    ///
    /// 为什么需要它：运行时 `context.Sensors` 拿到的是**控制器传感器**（`ControllerSensor`），
    /// 不是 `PlayerSensor` —— 于是任务里写 `context.Sensors as PlayerSensor` 永远得到 null，
    /// 表现是"任务跑了、什么都不做、也不报错"（2026-09-26 实测：换武器静默失效）。
    /// 有了这个接口，需要引擎对象的能力可以**一路剥到**真实观察层。
    /// </summary>
    public interface IAiSensorWrapper
    {
        IAiSensor InnerSensor { get; }
    }

    public static class PlayerEquipment
    {
        /// <summary>快捷栏槽位数（原版取方块也是只看这 10 个槽）。</summary>
        public const int HotbarSlots = 10;

        /// <summary>内置武器优先级：钻石砍刀优先（用户指定的"钻石刀"），依次降级。</summary>
        public const string DefaultPriority = "diamond_machete,iron_machete,copper_machete,machete";

        /// <summary>
        /// 从（可能被包装了好几层的）观察层里找出**真实**的 <see cref="PlayerSensor"/>。
        /// 找不到就返回 null —— 调用方必须把这个当"拿不到玩家"，而不是当"没武器"。
        /// </summary>
        public static PlayerSensor FindPlayerSensor(IAiSensor sensors)
        {
            var seen = new HashSet<object>();
            object current = sensors;
            while (current != null)
            {
                var playerSensor = current as PlayerSensor;
                if (playerSensor != null)
                    return playerSensor;
                if (!seen.Add(current))
                    return null;                       // 环：别再转了
                var wrapper = current as IAiSensorWrapper;
                current = wrapper != null ? (object)wrapper.InnerSensor : null;
            }
            return null;
        }

        /// <summary>
        /// 结果：拿到手了没有 + 拿到了什么（写日志/黑板用）+ 失败原因（说人话）。
        /// </summary>
        public sealed class WeaponResult
        {
            public bool Equipped;
            public string Name;
            public int Slot = -1;
            public bool FromCreative;
            public string Reason;

            public string Describe()
            {
                if (Equipped)
                    return Name + " -> slot " + Slot + (FromCreative ? " (creative grab)" : " (from inventory)");
                return "not equipped: " + (Reason ?? "unknown");
            }
        }

        public static WeaponResult TryEquip(ComponentPlayer player, string priority, bool allowCreativeGrab)
        {
            var result = new WeaponResult();
            if (player == null)
            {
                result.Reason = "no local player";
                return result;
            }

            ComponentMiner miner = player.ComponentMiner;
            IInventory inventory = miner != null ? miner.Inventory : null;
            if (inventory == null)
            {
                result.Reason = "the player has no inventory";
                return result;
            }

            List<string> wanted = SplitPriority(priority);

            // ① 快捷栏里已经有想要的 → 直接切过去（不动背包，最省事）
            for (int w = 0; w < wanted.Count; w++)
            {
                int value = ValueOf(wanted[w]);
                if (value == 0)
                    continue;
                int slot = FindInHotbar(inventory, value);
                if (slot < 0)
                    continue;
                inventory.ActiveSlotIndex = slot;
                result.Equipped = true;
                result.Name = wanted[w];
                result.Slot = slot;
                return result;
            }

            // ② 创造模式：直接取一把（引擎自带的"取物品"路径）
            ComponentCreativeInventory creative = player.Entity != null
                ? player.Entity.FindComponent<ComponentCreativeInventory>(false) : null;
            if (allowCreativeGrab && creative != null)
            {
                for (int w = 0; w < wanted.Count; w++)
                {
                    int value = ValueOf(wanted[w]);
                    if (value == 0)
                        continue;
                    int slot = PickHotbarSlot(inventory, value);
                    inventory.RemoveSlotItems(slot, int.MaxValue);
                    inventory.AddSlotItems(slot, value, 1);
                    inventory.ActiveSlotIndex = slot;
                    result.Equipped = true;
                    result.Name = wanted[w];
                    result.Slot = slot;
                    result.FromCreative = true;
                    return result;
                }
                result.Reason = "creative mode, but this build has none of: " + string.Join(",", wanted.ToArray());
                return result;
            }

            // ③ 生存模式：背包里近战威力最大的那件
            int bestSlot = -1;
            float bestPower = 1f;                     // 只认"比方块强"的：方块威力就是 1
            for (int slot = 0; slot < inventory.SlotsCount; slot++)
            {
                if (inventory.GetSlotCount(slot) <= 0)
                    continue;
                float power = MeleePowerOf(inventory.GetSlotValue(slot));
                if (power > bestPower)
                {
                    bestPower = power;
                    bestSlot = slot;
                }
            }
            if (bestSlot < 0)
            {
                result.Reason = "no melee weapon in the inventory (nothing with melee power > 1)";
                return result;
            }

            if (bestSlot < HotbarSlots)
            {
                inventory.ActiveSlotIndex = bestSlot;
                result.Equipped = true;
                result.Name = "best melee (power " + bestPower.ToString("0.##") + ")";
                result.Slot = bestSlot;
                return result;
            }

            // 不在快捷栏里：搬到快捷栏再切过去（同 ② 的搬法）
            int carried = inventory.GetSlotValue(bestSlot);
            int count = inventory.RemoveSlotItems(bestSlot, int.MaxValue);
            int target = PickHotbarSlot(inventory, carried);
            inventory.AddSlotItems(target, carried, Math.Max(1, count));
            inventory.ActiveSlotIndex = target;
            result.Equipped = true;
            result.Name = "best melee (power " + bestPower.ToString("0.##") + ")";
            result.Slot = target;
            return result;
        }

        /// <summary>`"diamond_machete,iron_machete"` → 列表（去空、去重、保持顺序）。</summary>
        public static List<string> SplitPriority(string priority)
        {
            var list = new List<string>();
            string text = string.IsNullOrEmpty(priority) ? DefaultPriority : priority;
            string[] parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string key = parts[i].Trim().ToLowerInvariant();
                if (key.Length > 0 && !list.Contains(key))
                    list.Add(key);
            }
            return list;
        }

        /// <summary>武器短名 → 方块值（0 = 这个版本里没有这种武器）。</summary>
        public static int ValueOf(string key)
        {
            Type type = TypeOf(key);
            if (type == null)
                return 0;
            for (int i = 1; i < BlocksManager.Blocks.Length; i++)
            {
                Block block = BlocksManager.Blocks[i];
                if (block != null && type.IsInstanceOfType(block))
                    return Terrain.MakeBlockValue(i);
            }
            return 0;
        }

        private static Type TypeOf(string key)
        {
            switch ((key ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "diamond_machete": return typeof(DiamondMacheteBlock);
                case "iron_machete": return typeof(IronMacheteBlock);
                case "copper_machete": return typeof(CopperMacheteBlock);
                case "machete": return typeof(MacheteBlock);      // 任意一把砍刀
                default: return null;
            }
        }

        private static float MeleePowerOf(int value)
        {
            int contents = Terrain.ExtractContents(value);
            if (contents <= 0 || contents >= BlocksManager.Blocks.Length)
                return 0f;
            Block block = BlocksManager.Blocks[contents];
            return block == null ? 0f : block.GetMeleePower(value);
        }

        private static int FindInHotbar(IInventory inventory, int value)
        {
            int limit = Math.Min(HotbarSlots, inventory.SlotsCount);
            for (int slot = 0; slot < limit; slot++)
            {
                if (inventory.GetSlotCount(slot) > 0 && inventory.GetSlotValue(slot) == value)
                    return slot;
            }
            return -1;
        }

        /// <summary>挑一个放武器的快捷栏槽：先找空槽，没有就用当前活动槽（与原版取方块一致）。</summary>
        private static int PickHotbarSlot(IInventory inventory, int value)
        {
            int limit = Math.Min(HotbarSlots, inventory.SlotsCount);
            for (int slot = 0; slot < limit; slot++)
            {
                if (inventory.GetSlotCapacity(slot, value) > 0
                    && (inventory.GetSlotCount(slot) == 0 || inventory.GetSlotValue(slot) == 0))
                    return slot;
            }
            return Math.Min(Math.Max(0, inventory.ActiveSlotIndex), Math.Max(0, limit - 1));
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Net;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 第三屏 <c>FullInventoryWidget</c> 左半（手工制作区）的**表现用**合成台。
    ///
    /// 为什么不能直接用客户端化身（远端角色）的 <c>ComponentCraftingTable</c>：
    ///   · 那个组件属于主机侧化身实体，会被 <c>NetworkAvatarSpawner.CapturePlayerRecord</c>
    ///     每 5 秒抓进 <c>SuMultiplayerPlayers.xml</c>（<c>CapturePersistentCraftingSlots</c>）——
    ///     往里写客户端网格就等于写客户端记录（任务硬约束：只写表现层）；
    ///   · 它还是主机的权威副本；往它里面写会污染主机状态。
    /// 所以这里用**引擎自己的实体模板**建一个只服务该面板的合成台：只取客户端化身模板里的
    /// <c>CraftingTable</c> 成员组件（玩家模板 = 6 格 2x2），实体**不加入 Project.m_entities**
    /// （不参与更新 / 不落盘 / 不进存档），面板关闭即 Dispose。
    ///
    /// 写入口径（与引擎同一条规则，但不是第二套配方模型）：
    ///   · 网格槽 = 客户端网格（值 + 数量）；
    ///   · 结果槽 = 把网格换成引擎的 ingredient 字符串，调用**引擎自己的**
    ///     <c>CraftingRecipesManager.FindMatchingRecipe</c>，再按
    ///     <c>ComponentCraftingTable.UpdateCraftingResult:124-137</c> 的同一算式
    ///     （<c>ResultCount * batches</c>）填结果；不采信客户端的结果槽值；
    ///   · remains 槽原样跟随客户端（引擎只在取走产物时才改写它）。
    /// 槽位写入走 <c>ComponentInventoryBase.m_slots</c> 的 <c>Slot.Value/Count</c> 字段
    /// （与 <c>ContainerReplicaEngine.SetSlotDirect</c> / <c>NetworkAvatarSpawner.SetCraftingSlotDirect</c>
    /// 同一手法）——**不调用** <c>AddSlotItems/RemoveSlotItems</c>，因为它们会跑行为链并可能
    /// 触发 <c>UpdateCraftingResult</c> 的玩家消息副作用。
    /// </summary>
    internal sealed class UiMirrorCraftingTable : IDisposable
    {
        private readonly IModParentField m_parentField;
        private readonly Entity m_entity;
        private readonly ComponentCraftingTable m_table;
        private readonly SubsystemTerrain m_terrain;
        private FieldInfo m_slotValueField;
        private FieldInfo m_slotCountField;
        private int[] m_lastCounts = Array.Empty<int>();
        private bool m_hasApplied;
        private float m_lastPlayerLevel;

        private UiMirrorCraftingTable(IModParentField parentField, Entity entity,
            ComponentCraftingTable table, SubsystemTerrain terrain)
        {
            m_parentField = parentField;
            m_entity = entity;
            m_table = table;
            m_terrain = terrain;
        }

        public ComponentCraftingTable Table => m_table;

        public int SlotsCount => m_table?.SlotsCount ?? 0;

        /// <summary>
        /// 用 <paramref name="sourceEntity"/> 的实体模板里的 CraftingTable 成员组件造一个
        /// 独立的、不加入世界的合成台。失败 = 明确原因（不退回客户端化身的权威组件）。
        /// </summary>
        public static bool TryCreate(Project project, Entity sourceEntity, IModParentField parentField,
            out UiMirrorCraftingTable table, out string reason)
        {
            table = null;
            reason = null;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            if (sourceEntity == null)
            {
                reason = "source-entity-missing";
                return false;
            }
            if (parentField == null)
            {
                reason = "no-field-accessor";
                return false;
            }
            ValuesDictionary sourceValues = sourceEntity.ValuesDictionary;
            DatabaseObject databaseObject = sourceValues?.DatabaseObject;
            string templateName = databaseObject?.Name;
            if (string.IsNullOrEmpty(templateName))
            {
                reason = "source-entity-template-name-missing";
                return false;
            }
            ValuesDictionary template;
            try
            {
                template = DatabaseManager.FindEntityValuesDictionary(templateName, false);
            }
            catch (Exception e)
            {
                reason = "template:" + Describe(e);
                return false;
            }
            if (template == null)
            {
                reason = "entity-template-missing(" + templateName + ")";
                return false;
            }
            ValuesDictionary member = DatabaseManager.FindValuesDictionaryForComponent(template,
                typeof(ComponentCraftingTable));
            if (member == null)
            {
                reason = "template-without-crafting-table(" + templateName + ")";
                return false;
            }
            string memberKey = null;
            foreach (KeyValuePair<string, object> pair in template)
            {
                if (!ReferenceEquals(pair.Value, member)) continue;
                memberKey = pair.Key;
                break;
            }
            if (string.IsNullOrEmpty(memberKey))
            {
                reason = "crafting-table-member-key-missing(" + templateName + ")";
                return false;
            }
            // 只保留 CraftingTable 这一个成员组件；DatabaseObject 仍指向源实体模板
            // （Project.CreateEntity 要求 EntityTemplate 类型）。
            var filtered = new ValuesDictionary { DatabaseObject = template.DatabaseObject };
            filtered.SetValue(memberKey, member);
            Entity entity = null;
            try
            {
                entity = project.CreateEntity(filtered);
                ComponentCraftingTable crafting = entity?.FindComponent<ComponentCraftingTable>();
                if (crafting == null)
                {
                    reason = "created-entity-without-crafting-table";
                    TryDisposeEntity(entity);
                    return false;
                }
                SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
                if (terrain == null)
                {
                    reason = "no-SubsystemTerrain";
                    TryDisposeEntity(entity);
                    return false;
                }
                table = new UiMirrorCraftingTable(parentField, entity, crafting, terrain);
                return true;
            }
            catch (Exception e)
            {
                TryDisposeEntity(entity);
                reason = "create:" + Describe(e);
                return false;
            }
        }

        /// <summary>
        /// 把客户端网格 + 引擎配方推导出的结果写进表现合成台。
        /// <paramref name="changed"/> = 真的写了不同的槽位值（计数 viewCraftingRefresh）。
        /// </summary>
        public bool TryApply(in UiHandcraftData data, float playerLevel, out bool changed,
            out string reason)
        {
            changed = false;
            reason = null;
            int slots = data.SlotsCount;
            if (slots != m_table.SlotsCount)
            {
                reason = "slot-count-mismatch(payload=" + slots + ",table=" + m_table.SlotsCount + ")";
                return false;
            }
            if (data.Values.Length != slots || data.Counts.Length != slots)
            {
                reason = "array-length-mismatch(values=" + data.Values.Length + ",counts=" +
                    data.Counts.Length + ",slots=" + slots + ")";
                return false;
            }
            if (!UiHandcraftPlan.TryGetLayout(slots, out int gridSize, out int resultSlot,
                    out int remainsSlot))
            {
                reason = "layout(" + slots + ")";
                return false;
            }
            // 网格 / 等级都没变（心跳重复上报）→ 不重复跑引擎配方表（一次 FindMatchingRecipe
            // 会遍历全部方块的自定义配方）；结果槽仍保持上一次算出的值。
            if (m_hasApplied && Math.Abs(playerLevel - m_lastPlayerLevel) < 1e-6f &&
                GridMatches(in data, resultSlot))
            {
                changed = false;
                return true;
            }
            m_hasApplied = false;
            // 网格 + remains 是客户端给的值：逐个校验（结果槽由主机重算，不采信客户端值）。
            for (int i = 0; i < slots; i++)
            {
                if (i == resultSlot) continue;
                int count = data.Counts[i];
                int value = data.Values[i];
                if (count < 0)
                {
                    reason = "count-negative(slot " + i + ")";
                    return false;
                }
                if (count == 0) continue;
                if (value == 0)
                {
                    reason = "value-zero-with-count(slot " + i + ")";
                    return false;
                }
                int contents = Game.Terrain.ExtractContents(value);
                if (contents <= 0 || contents >= BlocksManager.Blocks.Length)
                {
                    reason = "slot-content-invalid(slot " + i + ",contents=" + contents + ")";
                    return false;
                }
                int capacity;
                if (i == remainsSlot)
                {
                    // ComponentCraftingTable.GetSlotCapacity 对 remains 返回 0，容量按方块上限。
                    capacity = BlocksManager.Blocks[contents].MaxStacking;
                }
                else
                {
                    try
                    {
                        capacity = m_table.GetSlotCapacity(i, value);
                    }
                    catch (Exception e)
                    {
                        reason = "slot-capacity(slot " + i + "):" + Describe(e);
                        return false;
                    }
                }
                if (capacity <= 0 || count > capacity)
                {
                    reason = "count-over-capacity(slot " + i + ",count=" + count + ",capacity=" +
                        capacity + ")";
                    return false;
                }
            }
            if (!UiHandcraftPlan.TryBuildIngredients(data.Values, data.Counts, gridSize,
                    CraftingKeyOf, out string[] ingredients, out int batches, out string buildReason))
            {
                reason = "ingredients-" + buildReason;
                return false;
            }
            CraftingRecipe recipe;
            try
            {
                recipe = CraftingRecipesManager.FindMatchingRecipe(m_terrain, ingredients, 0f,
                    playerLevel);
            }
            catch (Exception e)
            {
                reason = "recipe:" + Describe(e);
                return false;
            }
            int resultValue = 0;
            int resultCount = 0;
            if (recipe != null)
            {
                // 配方存在但 ResultValue == 0 表示"等级 / 热量不足"（引擎会返回一条提示配方，
                // Source: CraftingRecipesManager.cs:133-153）：结果槽为空，这与客户端一致。
                if (!UiHandcraftPlan.TryComposeResult(true, recipe.ResultValue, recipe.ResultCount,
                        batches, out resultValue, out resultCount, out _))
                {
                    resultValue = 0;
                    resultCount = 0;
                }
            }
            var desiredValues = new int[slots];
            var desiredCounts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                if (i == resultSlot)
                {
                    desiredValues[i] = resultValue;
                    desiredCounts[i] = resultCount;
                }
                else
                {
                    desiredValues[i] = data.Values[i];
                    desiredCounts[i] = data.Counts[i];
                }
            }
            bool wrote = false;
            for (int i = 0; i < slots; i++)
            {
                if (m_table.GetSlotValue(i) == desiredValues[i] &&
                    m_table.GetSlotCount(i) == desiredCounts[i])
                    continue;
                if (!TryWriteSlot(m_table, i, desiredValues[i], desiredCounts[i], out reason))
                {
                    changed = wrote;
                    return false;
                }
                wrote = true;
            }
            m_lastCounts = desiredCounts;
            m_hasApplied = true;
            m_lastPlayerLevel = playerLevel;
            changed = wrote;
            return true;
        }

        /// <summary>表现合成台的网格 / remains 槽是否已经等于客户端这一份输入（不含结果槽）。</summary>
        private bool GridMatches(in UiHandcraftData data, int resultSlot)
        {
            for (int i = 0; i < m_table.SlotsCount; i++)
            {
                if (i == resultSlot) continue;
                if (m_table.GetSlotValue(i) != data.Values[i]) return false;
                if (m_table.GetSlotCount(i) != data.Counts[i]) return false;
            }
            return true;
        }

        /// <summary>审计行（状态行 / 日志用）：表现合成台的槽位数与最后一次应用的非空格数。</summary>
        public string Describe()
        {
            int filled = 0;
            for (int i = 0; i < m_lastCounts.Length; i++)
            {
                if (m_lastCounts[i] > 0) filled++;
            }
            return "slots=" + SlotsCount + " appliedFilled=" + filled;
        }

        public void Dispose() => TryDisposeEntity(m_entity);

        /// <summary>释放表现实体；异常必须由调用方计数（不静默）。</summary>
        public bool TryRelease(out string reason)
        {
            reason = null;
            try
            {
                m_entity.Dispose();
                return true;
            }
            catch (Exception e)
            {
                reason = "dispose:" + Describe(e);
                return false;
            }
        }

        private string CraftingKeyOf(int value)
        {
            int contents = Game.Terrain.ExtractContents(value);
            if (contents <= 0 || contents >= BlocksManager.Blocks.Length) return null;
            Block block = BlocksManager.Blocks[contents];
            if (block == null) return null;
            // **与引擎逐字相同**：ComponentCraftingTable.UpdateCraftingResult:115 直接拼
            // block.CraftingId + ":" + data；CraftingId 为空的方块会得到 ":data"（匹配不到任何
            // 配方 → 结果槽为空），客户端也是如此 —— 这里不能当成"无法解析"而拒绝整格。
            return block.CraftingId + ":" +
                Game.Terrain.ExtractData(value).ToString(CultureInfo.InvariantCulture);
        }

        private bool TryWriteSlot(ComponentInventoryBase inventory, int slotIndex, int value,
            int count, out string reason)
        {
            reason = null;
            if (inventory == null)
            {
                reason = "inventory-null";
                return false;
            }
            if (slotIndex < 0 || slotIndex >= inventory.SlotsCount)
            {
                reason = "slot-out-of-range(" + slotIndex + ")";
                return false;
            }
            try
            {
                IList slots = m_parentField.GetParentField(inventory, "m_slots",
                    typeof(ComponentInventoryBase)) as IList;
                if (slots == null || slotIndex >= slots.Count)
                {
                    reason = "m_slots-unavailable";
                    return false;
                }
                object slot = slots[slotIndex];
                if (slot == null)
                {
                    reason = "slot-null(" + slotIndex + ")";
                    return false;
                }
                if (m_slotValueField == null || m_slotCountField == null)
                {
                    Type slotType = slot.GetType();
                    m_slotValueField = slotType.GetField("Value",
                        BindingFlags.Instance | BindingFlags.Public);
                    m_slotCountField = slotType.GetField("Count",
                        BindingFlags.Instance | BindingFlags.Public);
                }
                if (m_slotValueField == null || m_slotCountField == null)
                {
                    reason = "slot-fields-missing";
                    return false;
                }
                m_slotValueField.SetValue(slot, count > 0 ? value : 0);
                m_slotCountField.SetValue(slot, count);
                return true;
            }
            catch (Exception e)
            {
                reason = "slot-write(" + slotIndex + "):" + Describe(e);
                return false;
            }
        }

        private static void TryDisposeEntity(Entity entity)
        {
            if (entity == null) return;
            try { entity.Dispose(); }
            catch (Exception) { }
        }

        private static string Describe(Exception e) =>
            e == null ? "null" : e.GetType().Name + " " + e.Message;
    }
}

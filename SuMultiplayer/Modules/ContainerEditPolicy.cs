using System;
using System.Collections.Generic;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 工作台配方的**纯值**描述：主机从 <c>ComponentCraftingTable</c> 的
    /// <c>m_matchedRecipe</c> / <c>m_matchedIngredients</c> 读出后传给判定层，
    /// 判定层不接触任何 Engine / Game 类型（tsverify 直接编译并测试）。
    /// 每个 matched 槽每批消耗 1 个。
    /// Source: Survivalcraft/Game/ComponentCraftingTable.cs:ComponentCraftingTable.RemoveSlotItems
    /// </summary>
    public readonly struct CraftingRecipeRules
    {
        public CraftingRecipeRules(int resultValue, int resultCountPerBatch, int remainsValue,
            int remainsCountPerBatch, int[] matchedSlots)
        {
            ResultValue = resultValue;
            ResultCountPerBatch = resultCountPerBatch;
            RemainsValue = remainsValue;
            RemainsCountPerBatch = remainsCountPerBatch;
            MatchedSlots = matchedSlots ?? Array.Empty<int>();
        }

        /// <summary>一批产物的方块值（= 主机权威 result 槽的值）。</summary>
        public int ResultValue { get; }
        /// <summary>一批产物的数量（RemoveSlotItems 按它的整数倍取走）。</summary>
        public int ResultCountPerBatch { get; }
        /// <summary>每批留下的 remains 方块值（0 = 该配方没有 remains）。</summary>
        public int RemainsValue { get; }
        /// <summary>每批留下的 remains 数量。</summary>
        public int RemainsCountPerBatch { get; }
        /// <summary>参与配方的配方格槽位下标（每批每个槽消耗 1 个）。</summary>
        public int[] MatchedSlots { get; }

        public bool GeneratesRemains => RemainsValue != 0 && RemainsCountPerBatch > 0;
    }

    /// <summary>
    /// 一次容器编辑的**请求者背包类型**（纯值，不含 Game 类型）：
    ///   · Survival  —— 普通背包（<c>ComponentInventory</c>）：容器净增必须由玩家背包净减覆盖；
    ///   · Creative  —— 创造仓库（<c>ComponentCreativeInventory</c>）：引擎的
    ///     <c>GetSlotCount</c> 对非空槽恒为 9999、<c>RemoveSlotItems</c> 对可复制物品**不扣减**，
    ///     所以"容器 +1 / 玩家背包 −0"是创造模式的正常语义，不是凭空生成。
    ///
    /// 创造模式改为校验（见 <see cref="ContainerEditPolicy.TryBuildPlan"/>）：
    ///   · 写进容器的每个物品值必须由创造仓库提供（<see cref="CreativeOfferedValues"/>），
    ///     或来自主机权威 base 容器里的原有存货（允许移动 / 拆分 / 取用，不允许复制）；
    ///   · 槽容量 / 堆叠上限由引擎适配层校验（<c>ValidateDesiredSlots</c>）；
    ///   · base / revision / 打开者 / 距离 / 方块未变照旧。
    ///
    /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerContainerHandlers.cs:530-540
    ///         （旧实现在守恒链里写 <c>playerInventory is ComponentCreativeInventory || ...</c>，
    ///         即对创造仓库跳过"合并物品守恒"要求）
    /// Source: Survivalcraft/Game/ComponentCreativeInventory.cs:129-140（GetSlotCount 恒 9999）、
    ///         :183-189（AddSlotItems 只写槽值）、:220-237（RemoveSlotItems 不扣减可复制物品）
    /// </summary>
    public readonly struct ContainerEditRequester
    {
        public ContainerEditRequester(bool creativeInventory, int[] creativeOfferedValues,
            int[] playerInventoryValues, int[] playerInventoryCounts)
        {
            CreativeInventory = creativeInventory;
            CreativeOfferedValues = creativeOfferedValues ?? Array.Empty<int>();
            PlayerInventoryValues = playerInventoryValues ?? Array.Empty<int>();
            PlayerInventoryCounts = playerInventoryCounts ?? Array.Empty<int>();
        }

        /// <summary>请求者的主机侧背包是 <c>ComponentCreativeInventory</c>（= 创造模式角色）。</summary>
        public bool CreativeInventory { get; }

        /// <summary>
        /// 主机创造仓库能提供的物品值（创造仓库组件自己的槽值：快捷栏当前物品 + 全部分类页）。
        /// 只在 <see cref="CreativeInventory"/> 时有意义；普通背包请求传 null / 空。
        /// 判定时同时比较**完整值**与 **contents 位**（见 TryCheckCreativeItems）。
        /// </summary>
        public int[] CreativeOfferedValues { get; }

        /// <summary>
        /// 请求者**权威背包**（客户端权威上报的背包快照 = 请求里的 player_base_*）。
        /// 创造模式允许把背包里已经存在的物品搬进容器（这是移动，不是创造）；
        /// 普通背包请求传 null / 空。
        /// </summary>
        public int[] PlayerInventoryValues { get; }
        public int[] PlayerInventoryCounts { get; }

        /// <summary>普通（生存）背包请求者的缺省值。</summary>
        public static ContainerEditRequester Survival => default;
    }

    /// <summary>
    /// 一次容器编辑的**判定计划**（不含任何 Game / Engine 类型，tsverify 直接编译并测试）。
    ///
    /// 语义（与旧实现 ScMultiplayerContainerHandlers 的容器事务一致）：
    ///   · 容器 base 必须等于主机权威快照（调用方先校验 revision + base）；
    ///   · 编辑的**整体**必须按物品种类守恒：容器（取产物之后的基线）+ 玩家背包的变化
    ///     只能来自配方变换（消耗 b 批原料、产出 b 批产物、留下 b 批 remains），
    ///     其余部分只能是同物品的槽位移动（拆分 / 取出 / 放入）。
    ///   · 工作台的 result 槽是主机推导的：客户端 desired result 只当本地预测，主机
    ///     UpdateCraftingResult 重算；取产物用主机 RemoveSlotItems(result, n) 落地。
    ///   · 玩家背包在主机侧只镜像前 <see cref="PlayerInventoryMath.MaxSyncSlots"/> 槽，
    ///     所以计划只带这段"实时镜像"的客户端 desired 槽值。
    /// </summary>
    public sealed class ContainerEditPlan
    {
        /// <summary>配方格（不含 result / remains）是否有变化。</summary>
        public bool GridChanged { get; set; }

        /// <summary>主机要重放的配方批次（0 = 不取产物）。</summary>
        public int TakeBatches { get; set; }
        /// <summary>主机 RemoveSlotItems 要取走的产物总数（= TakeBatches × 每批数量）。</summary>
        public int ResultTakeCount { get; set; }
        public int ResultValue { get; set; }
        public int BaseResultCount { get; set; }
        public int DesiredResultCount { get; set; }
        public bool ResultTake => ResultTakeCount > 0;
        public bool ResultPrediction => DesiredResultCount > BaseResultCount;

        /// <summary>客户端 desired 的实时镜像区槽值（长度 = liveSlots；null = 不改玩家背包）。</summary>
        public int[] LivePlayerValues { get; set; }
        public int[] LivePlayerCounts { get; set; }
        /// <summary>实时镜像区是否真的有槽位变化（false = 玩家侧无事可做）。</summary>
        public bool LivePlayerChanged { get; set; }

        /// <summary>本次编辑是一次"同物品两槽拆分"（source −k / destination +k）。</summary>
        public bool SplitApplied { get; set; }
        /// <summary>本次编辑是容器内的物品移动（非拆分、非取产物）。</summary>
        public bool MoveApplied { get; set; }
        /// <summary>本次编辑同时做了"取产物 + 把物品放进容器"（同一条编辑）。</summary>
        public bool TakeAndPlaceApplied { get; set; }

        /// <summary>
        /// 请求者是创造仓库：本次编辑**跳过**"容器增加 ⇒ 玩家背包减少"的物品守恒，
        /// 改按创造模式规则校验（物品值必须由创造仓库提供或来自容器原有存货）。
        /// </summary>
        public bool CreativeInventory { get; set; }

        /// <summary>
        /// 创造模式：因物品 **contents 位**由创造仓库提供而放行的 data 变体件数
        /// （内存条地图 / 告示牌 / 家具设计等“data 是内容”的物品；实机日志
        /// rejected[creative-item-not-offered=2] 的根因）。
        /// </summary>
        public int CreativeDataVariantAllowed { get; set; }

        /// <summary>
        /// 创造模式：因物品已在请求者权威背包里而放行的“移动”件数（move not creation）。
        /// </summary>
        public int CreativeInventoryMoveAllowed { get; set; }

        public string Describe() =>
            "grid=" + (GridChanged ? "changed" : "same") + " take=" + ResultTakeCount +
            " batches=" + TakeBatches + " result=" + ResultValue + "x" + BaseResultCount +
            "→" + DesiredResultCount + " playerMirror=" +
            (LivePlayerChanged ? "changed" : "same") +
            " kind=" + (TakeAndPlaceApplied ? "take+place" : SplitApplied ? "split" :
                MoveApplied ? "move" : "none") +
            " requester=" + (CreativeInventory ? "creative" : "survival");
    }

    /// <summary>
    /// 容器编辑的纯判定层：输入权威 base / 客户端 desired / 双方玩家背包快照，输出计划或明确拒绝原因。
    /// 无静态状态；所有原因都以稳定的 ASCII code 开头（<c>code: 具体说明</c>），便于按 code 限流与计数。
    /// </summary>
    public static class ContainerEditPolicy
    {
        /// <summary>工作台配方格最多 3x3。</summary>
        public const int MaxGridSlots = 9;

        /// <summary>取原因里的稳定 code（第一个 ':' 之前）；没有 code 时返回整串。</summary>
        public static string ReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "unknown";
            int colon = reason.IndexOf(':');
            string code = colon > 0 ? reason.Substring(0, colon) : reason;
            code = code.Trim();
            return code.Length == 0 ? "unknown" : code;
        }

        /// <summary>
        /// 工作台槽位布局：SlotsCount = gridSize² + 2（result 在倒数第二、remains 在最后）。
        /// Source: Survivalcraft/Game/ComponentCraftingTable.cs:ResultSlotIndex / RemainsSlotIndex
        /// </summary>
        public static bool TryGetCraftingLayout(int slotsCount, out int gridSize,
            out int resultSlot, out int remainsSlot)
        {
            gridSize = 0;
            resultSlot = -1;
            remainsSlot = -1;
            if (slotsCount < 3) return false;
            int grid = (int)Math.Sqrt(slotsCount - 2);
            if (grid < 1 || grid > 3 || grid * grid + 2 != slotsCount) return false;
            gridSize = grid;
            resultSlot = slotsCount - 2;
            remainsSlot = slotsCount - 1;
            return true;
        }

        /// <summary>
        /// 构建一次编辑的计划。返回 false = 明确拒绝，<paramref name="reason"/> 必有值且以稳定 code 开头。
        ///
        /// 请求者背包类型决定守恒语义（<paramref name="requester"/>）：
        ///   · 普通背包：容器净增必须由玩家背包净减覆盖（<c>item-creation</c> / <c>container-loss</c> /
        ///     <c>craft-result-unclaimed</c>）；
        ///   · 创造仓库：**跳过**上述守恒（创造仓库不扣减可复制物品），改校验
        ///     "物品值由创造仓库提供或来自容器原有存货"（<c>creative-item-not-offered</c> /
        ///     <c>creative-item-duplicate</c>），配方格变化直接接受，result 槽仍由主机重算；
        ///     纯取产物（配方格恰好少 b 批）由主机 <c>RemoveSlotItems</c> 原子消费。
        /// </summary>
        /// <param name="rules">
        /// 主机权威的工作台配方（result / remains / 每批消耗的配方格）。
        /// 非工作台传 null；工作台当前没有匹配配方时也传 null（此时不可能合法取产物）。
        /// </param>
        public static bool TryBuildPlan(int slotsCount,
            int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts,
            int resultSlot, int remainsSlot,
            CraftingRecipeRules? rules,
            int[] playerBaseValues, int[] playerBaseCounts,
            int[] playerDesiredValues, int[] playerDesiredCounts,
            int liveSlots, in ContainerEditRequester requester,
            out ContainerEditPlan plan, out string reason)
        {
            plan = null;
            reason = null;
            if (slotsCount <= 0 || slotsCount > ContainerStateData.MaxSlots)
            {
                reason = "slot-range: 容器槽位数非法（" + slotsCount + "）";
                return false;
            }
            if (baseValues == null || baseCounts == null || desiredValues == null ||
                desiredCounts == null || baseValues.Length != slotsCount ||
                baseCounts.Length != slotsCount || desiredValues.Length != slotsCount ||
                desiredCounts.Length != slotsCount)
            {
                reason = "desired-shape: 请求槽位数组长度与容器不一致（base " +
                    (baseValues?.Length ?? -1) + "/" + (baseCounts?.Length ?? -1) +
                    "，desired " + (desiredValues?.Length ?? -1) + "/" +
                    (desiredCounts?.Length ?? -1) + "，容器 " + slotsCount + "）";
                return false;
            }
            if (resultSlot >= slotsCount || remainsSlot >= slotsCount ||
                (resultSlot >= 0 && resultSlot == remainsSlot) ||
                ((resultSlot < 0) != (remainsSlot < 0)))
            {
                reason = "layout-invalid: result/remains 槽位非法（" + resultSlot + "/" +
                    remainsSlot + "，容器 " + slotsCount + " 槽）";
                return false;
            }
            if (playerBaseValues == null || playerBaseCounts == null ||
                playerDesiredValues == null || playerDesiredCounts == null ||
                playerBaseValues.Length != playerBaseCounts.Length ||
                playerDesiredValues.Length != playerDesiredCounts.Length ||
                playerBaseValues.Length != playerDesiredValues.Length ||
                playerBaseValues.Length == 0)
            {
                reason = "player-snapshot-shape: 玩家背包快照长度非法（base " +
                    (playerBaseValues?.Length ?? -1) + "/" + (playerBaseCounts?.Length ?? -1) +
                    "，desired " + (playerDesiredValues?.Length ?? -1) + "/" +
                    (playerDesiredCounts?.Length ?? -1) + "）";
                return false;
            }

            var result = new ContainerEditPlan();
            // ---------------------------------------------------------------- 槽位形状
            for (int i = 0; i < slotsCount; i++)
            {
                int count = desiredCounts[i];
                int value = desiredValues[i];
                if (count < 0)
                {
                    reason = "slot-content: 槽 " + i + " 的数量为负（" + count + "）";
                    return false;
                }
                if (count > 0 && value == 0)
                {
                    reason = "slot-content: 槽 " + i + " 有数量却是空值（count=" + count + "）";
                    return false;
                }
                if (i != resultSlot && i != remainsSlot &&
                    (count != baseCounts[i] || (count > 0 && value != baseValues[i])))
                {
                    result.GridChanged = true;
                }
            }

            if (resultSlot >= 0)
            {
                result.BaseResultCount = baseCounts[resultSlot];
                result.DesiredResultCount = desiredCounts[resultSlot];
                result.ResultValue = baseValues[resultSlot];
            }

            // ---------------------------------------------------------------- 配方规则
            CraftingRecipeRules? effective = null;
            int[] matchedValues = Array.Empty<int>();
            if (resultSlot >= 0 && rules.HasValue)
            {
                CraftingRecipeRules candidate = rules.Value;
                if (candidate.ResultValue == 0 || candidate.ResultCountPerBatch <= 0)
                {
                    reason = "craft-recipe-invalid: 配方产物非法（value=" +
                        candidate.ResultValue + " count=" + candidate.ResultCountPerBatch + "）";
                    return false;
                }
                int[] matched = candidate.MatchedSlots ?? Array.Empty<int>();
                var values = new int[matched.Length];
                var seen = new HashSet<int>();
                for (int m = 0; m < matched.Length; m++)
                {
                    int slot = matched[m];
                    if (slot < 0 || slot >= slotsCount || slot == resultSlot ||
                        slot == remainsSlot || !seen.Add(slot))
                    {
                        reason = "craft-recipe-invalid: 配方原料槽位非法（" + slot + "）";
                        return false;
                    }
                    if (baseCounts[slot] <= 0 || baseValues[slot] == 0)
                    {
                        reason = "craft-recipe-invalid: 原料槽 " + slot + " 在主机权威快照里为空";
                        return false;
                    }
                    values[m] = baseValues[slot];
                }
                if (values.Length == 0)
                {
                    reason = "craft-recipe-invalid: 配方没有原料槽";
                    return false;
                }
                effective = candidate;
                matchedValues = values;
            }

            // ---------------------------------------------------------------- 创造仓库分支
            // 创造模式的语义（与生存不同，且不是"放行一切"）：
            //   · **跳过**"容器增加 ⇒ 玩家背包减少"的整体守恒：创造仓库的 GetSlotCount 对非空槽
            //     恒为 9999、RemoveSlotItems 对可复制物品不扣减，所以玩家侧快照根本不反映取出/放入；
            //   · 改为逐物品校验：写进容器的物品值必须由创造仓库提供（无限供应）或来自主机权威
            //     base 容器的原有存货（允许移动 / 拆分 / 取用，不允许复制容器独有物品）；
            //   · result 槽仍由主机 UpdateCraftingResult 重算；只有"纯取产物"（配方格恰好少 b 批）
            //     才由主机 RemoveSlotItems 原子消费配方格（保住 remains 容量 / 统计等引擎语义）。
            // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerContainerHandlers.cs:530-540
            if (requester.CreativeInventory)
            {
                if (!TryCheckCreativeItems(baseValues, baseCounts, desiredValues, desiredCounts,
                        resultSlot, remainsSlot, requester.CreativeOfferedValues,
                        requester.PlayerInventoryValues, requester.PlayerInventoryCounts,
                        out reason, out int creativeDataVariants,
                        out int creativeInventoryMoves))
                {
                    return false;
                }
                result.CreativeDataVariantAllowed = creativeDataVariants;
                result.CreativeInventoryMoveAllowed = creativeInventoryMoves;
                int creativeBatches = 0;
                if (resultSlot >= 0 && effective.HasValue && result.BaseResultCount > 0)
                {
                    creativeBatches = FindCreativeTakeBatches(baseValues, baseCounts,
                        desiredValues, desiredCounts, resultSlot, remainsSlot, effective.Value,
                        matchedValues);
                }
                // remains 槽是**主机配方推导**的（引擎只在 RemoveSlotItems 里按 b 批写入）。
                // 纯取产物时 CreativeGridMatches 已校验它等于 replay 后的值；其余编辑只允许
                // "不动"或"取走 remains"（不允许凭空写新的 remains 物品值）。
                if (creativeBatches == 0 && remainsSlot >= 0 &&
                    !CreativeRemainsAllowed(baseValues, baseCounts, desiredValues,
                        desiredCounts, remainsSlot, out reason))
                {
                    return false;
                }
                result.CreativeInventory = true;
                result.TakeBatches = creativeBatches;
                result.ResultTakeCount = effective.HasValue
                    ? creativeBatches * effective.Value.ResultCountPerBatch
                    : 0;
                FinalizePlan(result, baseValues, baseCounts, desiredValues, desiredCounts,
                    resultSlot, remainsSlot, effective, matchedValues, creativeBatches,
                    playerBaseValues, playerBaseCounts, playerDesiredValues,
                    playerDesiredCounts, liveSlots);
                plan = result;
                return true;
            }

            // ---------------------------------------------------------------- 取产物批量
            // 关键：客户端 desired result 只是**本地预测**。拆分 / 移动配方格里的物品会改变
            // 配方批量，从而让预测产物数量下降——不能把它当成"取走产物"。
            // 这里按守恒方程反解批次 b：容器（按 b 批 replay 后的基线）+ 玩家背包的变化，
            // 除了配方变换（−b×每个原料槽 / +b×产物 / +b×remains）之外必须能由移动解释。
            int chosenBatches = -1;
            string claimReason = null;
            if (resultSlot >= 0 && effective.HasValue && result.BaseResultCount > 0)
            {
                int maxBatches = result.BaseResultCount / effective.Value.ResultCountPerBatch;
                for (int b = 0; b <= maxBatches; b++)
                {
                    if (!TryBuildExpectedAfterTake(baseValues, baseCounts, slotsCount, b,
                            effective, matchedValues, resultSlot, remainsSlot,
                            out int[] expectedValues, out int[] expectedCounts,
                            out string expectedReason))
                    {
                        if (b == 0)
                        {
                            reason = string.IsNullOrEmpty(expectedReason)
                                ? "craft-balance: 取产物基线构建失败"
                                : expectedReason;
                            return false;
                        }
                        continue;
                    }
                    long crafted = (long)b * effective.Value.ResultCountPerBatch;
                    if (TryCheckConservation(expectedValues, expectedCounts, desiredValues,
                            desiredCounts, resultSlot, playerBaseValues, playerBaseCounts,
                            playerDesiredValues, playerDesiredCounts, result.ResultValue,
                            crafted, checkCraftedClaim: true, out string checkReason))
                    {
                        chosenBatches = b;
                        break;
                    }
                    if (checkReason != null && checkReason.StartsWith("craft-result-unclaimed",
                            StringComparison.Ordinal))
                    {
                        claimReason = checkReason;
                    }
                }
                if (chosenBatches < 0)
                {
                    if (claimReason != null)
                    {
                        reason = claimReason;
                        return false;
                    }
                    string fallbackExpectedReason = null;
                    string fallbackReason = null;
                    bool fallbackBuilt = TryBuildExpectedAfterTake(baseValues, baseCounts,
                        slotsCount, 0, effective, matchedValues, resultSlot, remainsSlot,
                        out int[] fallbackValues, out int[] fallbackCounts,
                        out fallbackExpectedReason);
                    bool fallbackBalanced = fallbackBuilt &&
                        TryCheckConservation(fallbackValues, fallbackCounts, desiredValues,
                            desiredCounts, resultSlot, playerBaseValues, playerBaseCounts,
                            playerDesiredValues, playerDesiredCounts, result.ResultValue, 0,
                            checkCraftedClaim: true, out fallbackReason);
                    if (!fallbackBalanced)
                    {
                        reason = string.IsNullOrEmpty(fallbackReason)
                            ? (string.IsNullOrEmpty(fallbackExpectedReason)
                                ? "craft-balance: 取产物批量无法与客户端状态守恒"
                                : fallbackExpectedReason)
                            : fallbackReason;
                        return false;
                    }
                    reason = "craft-balance: 取产物批量无法与客户端状态守恒";
                    return false;
                }
            }
            else
            {
                if (!TryBuildExpectedAfterTake(baseValues, baseCounts, slotsCount, 0, effective,
                        matchedValues, resultSlot, remainsSlot, out int[] expectedValues,
                        out int[] expectedCounts, out string expectedReason) ||
                    !TryCheckConservation(expectedValues, expectedCounts, desiredValues,
                        desiredCounts, resultSlot, playerBaseValues, playerBaseCounts,
                        playerDesiredValues, playerDesiredCounts, result.ResultValue, 0,
                        checkCraftedClaim: true, out reason))
                {
                    if (string.IsNullOrEmpty(reason)) reason = expectedReason;
                    if (string.IsNullOrEmpty(reason)) reason = "craft-balance: 编辑不守恒";
                    return false;
                }
                chosenBatches = 0;
            }

            result.TakeBatches = chosenBatches;
            result.ResultTakeCount = effective.HasValue
                ? chosenBatches * effective.Value.ResultCountPerBatch
                : 0;

            FinalizePlan(result, baseValues, baseCounts, desiredValues, desiredCounts, resultSlot,
                remainsSlot, effective, matchedValues, chosenBatches, playerBaseValues,
                playerBaseCounts, playerDesiredValues, playerDesiredCounts, liveSlots);
            plan = result;
            return true;
        }

        /// <summary>
        /// 收尾：编辑分类（计数用）+ 实时镜像区槽位。生存 / 创造两条路径共用。
        /// </summary>
        private static void FinalizePlan(ContainerEditPlan result,
            int[] baseValues, int[] baseCounts, int[] desiredValues, int[] desiredCounts,
            int resultSlot, int remainsSlot, CraftingRecipeRules? effective, int[] matchedValues,
            int chosenBatches, int[] playerBaseValues, int[] playerBaseCounts,
            int[] playerDesiredValues, int[] playerDesiredCounts, int liveSlots)
        {
            // ---------------------------------------------------------------- 编辑分类（计数用）
            if (chosenBatches > 0)
            {
                // "取产物 + 放进容器"：replay 取产物后的容器基线之外还有容器槽位变化。
                result.TakeAndPlaceApplied = ContainerDiffersAfterTake(baseValues, baseCounts,
                    desiredValues, desiredCounts, effective, matchedValues, resultSlot,
                    remainsSlot, chosenBatches);
            }
            else
            {
                bool containerChanged = ContainerChanged(baseValues, baseCounts, desiredValues,
                    desiredCounts, resultSlot);
                if (containerChanged)
                {
                    result.SplitApplied = TryDetectContainerSplit(baseValues, baseCounts,
                        desiredValues, desiredCounts, resultSlot) &&
                        PlayerTotalsEqual(playerBaseValues, playerBaseCounts,
                            playerDesiredValues, playerDesiredCounts);
                    result.MoveApplied = !result.SplitApplied;
                }
                else
                {
                    // 容器没变、只有玩家背包变化：面板里的拆分 / 移动由客户端权威背包负责，
                    // 这里只做接受判定，并把拆分计数出来（不猜槽位、不改容器）。
                    result.SplitApplied = TryDetectArraySplit(playerBaseValues,
                        playerBaseCounts, playerDesiredValues, playerDesiredCounts, -1);
                }
            }

            // ---------------------------------------------------------------- 实时镜像区槽位
            // 只镜像前 liveSlots 槽：背包槽没有实时上报，主机不能凭快照猜槽位。
            int live = Math.Max(0, Math.Min(liveSlots, playerDesiredValues.Length));
            if (live > 0)
            {
                result.LivePlayerValues = new int[live];
                result.LivePlayerCounts = new int[live];
                Array.Copy(playerDesiredValues, result.LivePlayerValues, live);
                Array.Copy(playerDesiredCounts, result.LivePlayerCounts, live);
                for (int i = 0; i < live; i++)
                {
                    if (playerBaseValues[i] != playerDesiredValues[i] ||
                        playerBaseCounts[i] != playerDesiredCounts[i])
                    {
                        result.LivePlayerChanged = true;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 主机按 b 批 replay 取产物后的容器基线（result 槽清零；remains 增加）。
        /// 返回 false = b 超出可行范围 / 配方规则非法。
        /// </summary>
        private static bool TryBuildExpectedAfterTake(int[] baseValues, int[] baseCounts,
            int slotsCount, int batches, CraftingRecipeRules? rules, int[] matchedValues,
            int resultSlot, int remainsSlot, out int[] expectedValues, out int[] expectedCounts,
            out string reason)
        {
            expectedValues = (int[])baseValues.Clone();
            expectedCounts = (int[])baseCounts.Clone();
            reason = null;
            if (resultSlot >= 0)
            {
                expectedValues[resultSlot] = 0;
                expectedCounts[resultSlot] = 0;
            }
            if (batches <= 0) return true;
            if (!rules.HasValue || matchedValues == null || matchedValues.Length == 0)
            {
                reason = "craft-recipe-unavailable: 取产物需要主机匹配配方，但配方不可用";
                return false;
            }
            for (int m = 0; m < matchedValues.Length; m++)
            {
                int slot = rules.Value.MatchedSlots[m];
                int count = expectedCounts[slot] - batches;
                if (count < 0)
                {
                    reason = "craft-take-range: 第 " + m + " 个原料槽只剩 " +
                        expectedCounts[slot] + " 个，不足以取 " + batches + " 批";
                    return false;
                }
                expectedCounts[slot] = count;
                if (count == 0) expectedValues[slot] = 0;
            }
            if (rules.Value.GeneratesRemains && remainsSlot >= 0)
            {
                expectedValues[remainsSlot] = rules.Value.RemainsValue;
                expectedCounts[remainsSlot] = baseCounts[remainsSlot] +
                    batches * rules.Value.RemainsCountPerBatch;
            }
            return true;
        }

        /// <summary>
        /// 引擎 cell / item value 的 contents 位（Source: Survivalcraft/Game/Terrain.cs:404
        /// <c>value &amp; 0x3FF</c>）。本层不引用 Game，因此给出同一掩码。
        /// </summary>
        public static int ContentsOf(int value) => value & 0x3FF;

        /// <summary>
        /// 创造模式的物品合法性校验（替代生存的合并物品守恒）。写进容器的物品值只要满足以下
        /// **任一**条件即合法：
        ///   1. **完整值**由创造仓库提供（<paramref name="offeredValues"/> 的精确值，无限供应）；
        ///   2. **contents 位**由创造仓库提供（data 是内容的物品：内存条地图 / 告示牌文字 /
        ///      家具设计 id 等。创造仓库只列出 data=0 的基值，写过内容的物品完整值不同，
        ///      旧实现因此误判成“不是创造物品” → 实机 rejected[creative-item-not-offered=2]）；
        ///   3. 已存在于请求者**权威背包**里（客户端权威的 player_base_* 快照；这是把已有物品
        ///      搬进容器，属于移动而不是创造）。
        /// 否则只允许是主机权威 base 容器里的原有存货（可移动 / 拆分 / 取用），且**总数不能超过**
        /// 原有存货（不能复制创造仓库不提供、背包也没有的容器独有物品）。
        /// result / remains 由主机推导，不参与；槽容量 / 堆叠由引擎适配层校验。
        /// 返回 false 时 reason 以稳定 code 开头：
        ///   creative-item-not-offered  物品值的 **contents** 既不在创造仓库、不在请求者背包，
        ///                              也不是容器原有存货（真正未知的内容）；
        ///   creative-item-duplicate    创造仓库与背包都没有、只在容器里的存货被增加。
        /// </summary>
        private static bool TryCheckCreativeItems(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts, int resultSlot, int remainsSlot,
            int[] offeredValues, int[] playerInventoryValues, int[] playerInventoryCounts,
            out string reason, out int dataVariantAllowed, out int inventoryMoveAllowed)
        {
            reason = null;
            dataVariantAllowed = 0;
            inventoryMoveAllowed = 0;
            var offered = new HashSet<int>();
            var offeredContents = new HashSet<int>();
            if (offeredValues != null)
            {
                for (int i = 0; i < offeredValues.Length; i++)
                {
                    int offeredValue = offeredValues[i];
                    if (offeredValue == 0) continue;
                    offered.Add(offeredValue);
                    offeredContents.Add(ContentsOf(offeredValue));
                }
            }
            // 请求者权威背包（客户端的 player_base_*）：只按精确值统计，与容器存货同口径。
            var inventoryTotals = new Dictionary<int, long>();
            Accumulate(inventoryTotals, playerInventoryValues, playerInventoryCounts, -1, -1);
            // result / remains 都是主机推导槽（result 由 UpdateCraftingResult 重算、remains 只在
            // RemoveSlotItems 里按配方写入），不参与创造仓库的物品值校验；remains 另有
            // CreativeRemainsAllowed 约束。
            var baseTotals = new Dictionary<int, long>();
            Accumulate(baseTotals, baseValues, baseCounts, resultSlot, remainsSlot);
            var desiredTotals = new Dictionary<int, long>();
            Accumulate(desiredTotals, desiredValues, desiredCounts, resultSlot, remainsSlot);
            for (int i = 0; i < desiredCounts.Length; i++)
            {
                // result 由主机重算；remains 由主机配方规则推导（见 CreativeRemainsAllowed）
                if (i == resultSlot || i == remainsSlot) continue;
                int count = desiredCounts[i];
                if (count <= 0) continue;
                int value = desiredValues[i];
                if (value == 0)
                {
                    reason = "slot-content: 槽 " + i + " 有数量却是空值（count=" + count + "）";
                    return false;
                }
                if (offered.Contains(value)) continue;
                // 计数只记**这次编辑真正改动**的槽：未变槽在 base 里已经合法存在，
                // 每次编辑都重算一遍会把累计计数越滚越大（实机 / 自检都会误导）。
                bool slotChanged = desiredCounts[i] != baseCounts[i] ||
                    (desiredCounts[i] > 0 && desiredValues[i] != baseValues[i]);
                int contents = ContentsOf(value);
                if (offeredContents.Contains(contents))
                {
                    // data 变体：contents 由创造仓库提供（地图 / 告示牌 / 家具设计等）。
                    if (slotChanged) dataVariantAllowed++;
                    continue;
                }
                if (inventoryTotals.TryGetValue(value, out long inInventory) && inInventory > 0)
                {
                    // 请求者权威背包里已有这件物品：搬进容器是移动，不是创造。
                    if (slotChanged) inventoryMoveAllowed++;
                    continue;
                }
                baseTotals.TryGetValue(value, out long baseTotal);
                if (baseTotal <= 0)
                {
                    reason = "creative-item-not-offered: 物品值 " + value + "（contents=" +
                        contents + "）的 contents 既不在创造仓库里，也不在请求者背包 / " +
                        "容器原有存货里（槽 " + i + "）";
                    return false;
                }
            }
            foreach (KeyValuePair<int, long> item in desiredTotals)
            {
                if (offered.Contains(item.Key)) continue;
                if (offeredContents.Contains(ContentsOf(item.Key))) continue;
                if (inventoryTotals.TryGetValue(item.Key, out long inInventory) &&
                    inInventory > 0)
                {
                    continue;
                }
                baseTotals.TryGetValue(item.Key, out long baseTotal);
                if (item.Value > baseTotal)
                {
                    reason = "creative-item-duplicate: 物品 " + item.Key +
                        "（contents=" + ContentsOf(item.Key) + "）既不在创造仓库、也不在请求者" +
                        "背包，只是容器原有存货，容器里从 " + baseTotal + " 增加到 " +
                        item.Value + "（只允许移动 / 拆出现有存货，不允许复制）";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 创造模式 remains 槽规则：没有取产物（b=0）时，remains 只能不动或**取走**
        /// （数量减少、物品值不变）；不允许写入新的 remains 物品值 —— 那是主机配方推导的结果，
        /// 不是客户端可自由写入的容器槽。纯取产物（b&gt;0）由 <see cref="CreativeGridMatches"/>
        /// 对照 replay 后的期望值校验。
        /// </summary>
        private static bool CreativeRemainsAllowed(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts, int remainsSlot, out string reason)
        {
            reason = null;
            int baseCount = baseCounts[remainsSlot];
            int baseValue = baseCount > 0 ? baseValues[remainsSlot] : 0;
            int desiredCount = desiredCounts[remainsSlot];
            int desiredValue = desiredCount > 0 ? desiredValues[remainsSlot] : 0;
            if (desiredCount == baseCount && desiredValue == baseValue) return true;
            if (desiredCount <= baseCount && (desiredCount == 0 || desiredValue == baseValue))
            {
                return true;
            }
            reason = "creative-remains: 没有取产物却改动了工作台 remains 槽（base " + baseValue +
                "x" + baseCount + " → " + desiredValue + "x" + desiredCount +
                "）；remains 由主机配方规则推导，只允许不动或取走";
            return false;
        }

        /// <summary>
        /// 创造模式的"纯取产物"识别：配方格（含 remains）恰好等于主机按 b 批 replay 后的状态。
        /// 返回 0 = 不是纯取产物（放料 / 移动 / 拆分 / 取产物+放料同一条编辑），
        /// 此时配方格按客户端 desired 落地、result 由主机重算。
        /// </summary>
        private static int FindCreativeTakeBatches(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts, int resultSlot, int remainsSlot,
            CraftingRecipeRules rules, int[] matchedValues)
        {
            if (rules.ResultCountPerBatch <= 0 || matchedValues == null ||
                matchedValues.Length == 0)
            {
                return 0;
            }
            int maxBatches = baseCounts[resultSlot] / rules.ResultCountPerBatch;
            for (int batches = maxBatches; batches >= 1; batches--)
            {
                if (!TryBuildExpectedAfterTake(baseValues, baseCounts, baseCounts.Length, batches,
                        rules, matchedValues, resultSlot, remainsSlot, out int[] expectedValues,
                        out int[] expectedCounts, out _))
                {
                    continue;
                }
                if (CreativeGridMatches(expectedValues, expectedCounts, desiredValues,
                        desiredCounts, resultSlot))
                {
                    return batches;
                }
            }
            return 0;
        }

        private static bool CreativeGridMatches(int[] expectedValues, int[] expectedCounts,
            int[] desiredValues, int[] desiredCounts, int resultSlot)
        {
            int slots = Math.Min(expectedValues.Length, desiredValues.Length);
            if (slots != expectedCounts.Length || slots != desiredCounts.Length) return false;
            for (int i = 0; i < slots; i++)
            {
                if (i == resultSlot) continue;
                if (desiredCounts[i] != expectedCounts[i]) return false;
                if (desiredCounts[i] > 0 && desiredValues[i] != expectedValues[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// 创造模式取产物的 replay 校验：主机 RemoveSlotItems 之后的容器必须与客户端 desired
        /// 一致（result 槽除外，它由主机重算覆盖）。只比配方格 + remains，不要求物品守恒。
        /// </summary>
        public static bool TryCheckCreativeTakeGrid(int[] hostValues, int[] hostCounts,
            int[] desiredValues, int[] desiredCounts, int resultSlot, out string reason)
        {
            reason = null;
            int slots = Math.Min(hostValues?.Length ?? 0, hostCounts?.Length ?? 0);
            if (slots <= 0 || desiredValues == null || desiredCounts == null ||
                desiredValues.Length != slots || desiredCounts.Length != slots)
            {
                reason = "craft-grid-mismatch: 取产物后主机容器槽位数与客户端 desired 不一致";
                return false;
            }
            for (int i = 0; i < slots; i++)
            {
                if (i == resultSlot) continue;
                int hostCount = hostCounts[i];
                int desiredCount = desiredCounts[i];
                int hostValue = hostCount > 0 ? hostValues[i] : 0;
                int desiredValue = desiredCount > 0 ? desiredValues[i] : 0;
                if (hostCount != desiredCount || hostValue != desiredValue)
                {
                    reason = "craft-grid-mismatch: 取产物后槽 " + i + " 主机=" + hostValue + "x" +
                        hostCount + "，客户端 desired=" + desiredValue + "x" + desiredCount;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 守恒校验：expected（取产物后的容器基线）→ desired 的容器变化，必须由玩家背包
        /// （base→desired，加上本批产出的 crafted 件数）的变化按物品种类覆盖。
        /// 返回 false 时 reason 以稳定 code 开头：
        ///   item-creation   容器凭空多出物品；
        ///   container-loss  容器物品去向不明；
        ///   craft-result-unclaimed  客户端声称取产物，但产物没有出现在玩家背包/容器里
        ///                           （物品值不符 / 被转走）。
        /// </summary>
        public static bool TryCheckConservation(int[] expectedValues, int[] expectedCounts,
            int[] desiredValues, int[] desiredCounts, int resultSlot,
            int[] playerBaseValues, int[] playerBaseCounts,
            int[] playerDesiredValues, int[] playerDesiredCounts, int craftedValue,
            long craftedCount, bool checkCraftedClaim, out string reason)
        {
            reason = null;
            var expected = new Dictionary<int, long>();
            var desired = new Dictionary<int, long>();
            Accumulate(expected, expectedValues, expectedCounts, resultSlot, -1);
            Accumulate(desired, desiredValues, desiredCounts, resultSlot, -1);
            var playerBase = new Dictionary<int, long>();
            var playerDesired = new Dictionary<int, long>();
            Accumulate(playerBase, playerBaseValues, playerBaseCounts, -1, -1);
            Accumulate(playerDesired, playerDesiredValues, playerDesiredCounts, -1, -1);

            if (checkCraftedClaim && craftedCount > 0 && craftedValue != 0)
            {
                expected.TryGetValue(craftedValue, out long expectedCombined);
                desired.TryGetValue(craftedValue, out long desiredCombined);
                playerBase.TryGetValue(craftedValue, out long expectedPlayer);
                playerDesired.TryGetValue(craftedValue, out long desiredPlayer);
                long expectedTotal = expectedCombined + expectedPlayer;
                long desiredTotal = desiredCombined + desiredPlayer;
                if (desiredTotal - expectedTotal < craftedCount)
                {
                    reason = "craft-result-unclaimed: 取走产物 " + craftedCount + "x" +
                        craftedValue + "，但玩家背包/容器里只多出 " +
                        (desiredTotal - expectedTotal) + "（物品值不符或去向不明）";
                    return false;
                }
            }

            // 本批产出记到玩家 base 上：合法取产物后，玩家背包（或容器）里就应该有这些物品。
            if (craftedCount > 0 && craftedValue != 0)
            {
                playerBase.TryGetValue(craftedValue, out long credited);
                playerBase[craftedValue] = credited + craftedCount;
            }

            var keys = new HashSet<int>(expected.Keys);
            foreach (int key in desired.Keys) keys.Add(key);
            foreach (int value in keys)
            {
                expected.TryGetValue(value, out long expectedCount);
                desired.TryGetValue(value, out long desiredCount);
                long gain = desiredCount - expectedCount;
                if (gain > 0)
                {
                    playerBase.TryGetValue(value, out long before);
                    playerDesired.TryGetValue(value, out long after);
                    long decrease = before - after;
                    if (decrease < gain)
                    {
                        reason = "item-creation: 容器里增加 " + gain + "x" + value +
                            "（取产物后的基线），但玩家背包只减少 " + decrease +
                            "（不允许凭空生成）";
                        return false;
                    }
                }
                long loss = expectedCount - desiredCount;
                if (loss > 0)
                {
                    playerBase.TryGetValue(value, out long before);
                    playerDesired.TryGetValue(value, out long after);
                    long increase = after - before;
                    if (increase < loss)
                    {
                        reason = "container-loss: 容器里减少 " + loss + "x" + value +
                            "（取产物后的基线），但玩家背包只增加 " + increase + "（去向不明）";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>取产物后容器是否还有额外槽位变化（= "取产物 + 放进容器" 的同一条编辑）。</summary>
        private static bool ContainerDiffersAfterTake(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts, CraftingRecipeRules? rules,
            int[] matchedValues, int resultSlot, int remainsSlot, int batches)
        {
            if (!rules.HasValue || matchedValues == null || batches <= 0) return false;
            int slots = Math.Min(baseValues.Length, baseCounts.Length);
            for (int i = 0; i < slots; i++)
            {
                if (i == resultSlot) continue;
                int expectedValue = baseValues[i];
                int expectedCount = baseCounts[i];
                for (int m = 0; m < matchedValues.Length; m++)
                {
                    if (rules.Value.MatchedSlots[m] != i) continue;
                    expectedCount = baseCounts[i] - batches;
                    if (expectedCount <= 0) expectedValue = 0;
                }
                if (i == remainsSlot && rules.Value.GeneratesRemains)
                {
                    expectedValue = rules.Value.RemainsValue;
                    expectedCount = baseCounts[i] + batches * rules.Value.RemainsCountPerBatch;
                }
                if (desiredCounts[i] != expectedCount ||
                    (desiredCounts[i] > 0 && desiredValues[i] != expectedValue))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainerChanged(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts, int resultSlot)
        {
            int slots = Math.Min(baseValues.Length, baseCounts.Length);
            for (int i = 0; i < slots; i++)
            {
                if (i == resultSlot) continue;
                if (desiredCounts[i] != baseCounts[i] ||
                    (desiredCounts[i] > 0 && desiredValues[i] != baseValues[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>容器内是否恰好是"同物品两槽拆分"（source −k / destination +k，其余不动）。</summary>
        private static bool TryDetectContainerSplit(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts, int resultSlot)
        {
            int slots = Math.Min(baseValues.Length, baseCounts.Length);
            var changed = new List<int>(4);
            for (int i = 0; i < slots; i++)
            {
                if (i == resultSlot) continue;
                if (desiredCounts[i] != baseCounts[i] ||
                    (desiredCounts[i] > 0 && desiredValues[i] != baseValues[i]))
                {
                    changed.Add(i);
                    if (changed.Count > 2) return false;
                }
            }
            if (changed.Count != 2) return false;
            return IsTwoSlotMove(baseValues, baseCounts, desiredValues, desiredCounts,
                changed[0], changed[1]);
        }

        /// <summary>数组内是否恰好是"同物品两槽拆分"（用于面板里的玩家背包拆分）。</summary>
        private static bool TryDetectArraySplit(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts, int skipSlot)
        {
            int slots = Math.Min(baseValues.Length, baseCounts.Length);
            var changed = new List<int>(4);
            for (int i = 0; i < slots; i++)
            {
                if (i == skipSlot) continue;
                if (desiredCounts[i] != baseCounts[i] ||
                    (desiredCounts[i] > 0 && desiredValues[i] != baseValues[i]))
                {
                    changed.Add(i);
                    if (changed.Count > 2) return false;
                }
            }
            if (changed.Count != 2) return false;
            return IsTwoSlotMove(baseValues, baseCounts, desiredValues, desiredCounts,
                changed[0], changed[1]);
        }

        private static bool IsTwoSlotMove(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts, int a, int b)
        {
            bool aDown = desiredCounts[a] < baseCounts[a];
            bool bDown = desiredCounts[b] < baseCounts[b];
            if (aDown == bDown) return false;
            int source = aDown ? a : b;
            int destination = aDown ? b : a;
            int amount = baseCounts[source] - desiredCounts[source];
            if (amount <= 0) return false;
            int itemValue = baseValues[source];
            if (itemValue == 0) return false;
            if (desiredCounts[destination] - baseCounts[destination] != amount) return false;
            if (desiredCounts[destination] > 0 && desiredValues[destination] != itemValue)
                return false;
            if (baseCounts[destination] > 0 && baseValues[destination] != itemValue) return false;
            return true;
        }

        private static bool PlayerTotalsEqual(int[] baseValues, int[] baseCounts,
            int[] desiredValues, int[] desiredCounts)
        {
            var before = new Dictionary<int, long>();
            var after = new Dictionary<int, long>();
            Accumulate(before, baseValues, baseCounts, -1, -1);
            Accumulate(after, desiredValues, desiredCounts, -1, -1);
            if (before.Count != after.Count) return false;
            foreach (KeyValuePair<int, long> item in before)
            {
                after.TryGetValue(item.Key, out long count);
                if (count != item.Value) return false;
            }
            return true;
        }

        private static void Accumulate(Dictionary<int, long> totals, int[] values, int[] counts,
            int skipA, int skipB)
        {
            int length = Math.Min(values?.Length ?? 0, counts?.Length ?? 0);
            for (int i = 0; i < length; i++)
            {
                if (i == skipA || i == skipB) continue;
                int count = counts[i];
                if (count <= 0) continue;
                int value = values[i];
                if (value == 0) continue;
                totals.TryGetValue(value, out long current);
                totals[value] = current + count;
            }
        }
    }
}

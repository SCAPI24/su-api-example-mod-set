using System;
using System.Collections.Generic;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 玩家背包快照（容器守恒校验用）。玩家自己的背包仍然由客户端权威上报
    /// （Modules/PlayerActionModule.cs），这里只用它证明**容器没有凭空多出物品**。
    /// </summary>
    public sealed class PlayerInventorySnapshot
    {
        public PlayerInventorySnapshot(int[] values, int[] counts)
        {
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
        }

        public int[] Values { get; }
        public int[] Counts { get; }

        public int SlotsCount => Math.Min(Values.Length, Counts.Length);

        public PlayerInventorySnapshot Clone() =>
            new PlayerInventorySnapshot((int[])Values.Clone(), (int[])Counts.Clone());

        public string Describe() => Values.Length + " slots";
    }

    /// <summary>主机对一次方块使用请求的判定结果。</summary>
    public enum BlockUseTargetKind
    {
        /// <summary>拒绝 / 没有可执行的交互。</summary>
        None = 0,
        /// <summary>由引擎方块行为在主机执行（门 / 按钮 / 家具 / 营火 / ...），结果随 CellChange 下发。</summary>
        Native = 1,
        /// <summary>方块实体库存：主机授权并下发权威 ContainerState，客户端打开副本面板。</summary>
        Container = 2
    }

    /// <summary>主机执行一次使用请求后的结果（容器时带上权威状态）。</summary>
    public sealed class BlockUseHostOutcome
    {
        public BlockUseHostOutcome(BlockUseTargetKind kind, int containerKind,
            ContainerStateData state)
        {
            Kind = kind;
            ContainerKind = containerKind;
            State = state;
        }

        public BlockUseTargetKind Kind { get; }
        public int ContainerKind { get; }
        public ContainerStateData State { get; }

        public static BlockUseHostOutcome Native { get; } =
            new BlockUseHostOutcome(BlockUseTargetKind.Native, ContainerKinds.None, null);
    }

    /// <summary>
    /// 一次容器编辑在主机**落地后**的结果：权威状态 + 主机实际做了什么（供状态行 / 日志计数）。
    /// </summary>
    public sealed class ContainerEditOutcome
    {
        public ContainerEditOutcome(ContainerStateData state, bool craftGridApplied,
            int resultTakenCount, int resultValue, bool resultPredictionMismatch,
            int playerRemoved, int playerAdded, int playerLeftover,
            bool splitApplied = false, bool moveApplied = false, bool takeAndPlaceApplied = false,
            bool creativeInventory = false, int creativeDataVariantAllowed = 0,
            int creativeInventoryMoveAllowed = 0)
        {
            State = state;
            CraftGridApplied = craftGridApplied;
            ResultTakenCount = resultTakenCount;
            ResultValue = resultValue;
            ResultPredictionMismatch = resultPredictionMismatch;
            PlayerRemoved = playerRemoved;
            PlayerAdded = playerAdded;
            PlayerLeftover = playerLeftover;
            SplitApplied = splitApplied;
            MoveApplied = moveApplied;
            TakeAndPlaceApplied = takeAndPlaceApplied;
            CreativeInventory = creativeInventory;
            CreativeDataVariantAllowed = creativeDataVariantAllowed;
            CreativeInventoryMoveAllowed = creativeInventoryMoveAllowed;
        }

        /// <summary>落地后的权威容器快照（rev 已前进）。</summary>
        public ContainerStateData State { get; }
        /// <summary>配方格是否真的变了（工作台）。</summary>
        public bool CraftGridApplied { get; }
        /// <summary>主机 RemoveSlotItems 实际取走的合成产物数量（0 = 没取）。</summary>
        public int ResultTakenCount { get; }
        /// <summary>取走产物的方块值（ResultTakenCount &gt; 0 时有意义）。</summary>
        public int ResultValue { get; }
        /// <summary>客户端本地预测的产物与主机 UpdateCraftingResult 的结果不一致（以主机为准）。</summary>
        public bool ResultPredictionMismatch { get; }
        /// <summary>主机从实时镜像背包扣除的物品数。</summary>
        public int PlayerRemoved { get; }
        /// <summary>主机放进实时镜像背包的物品数。</summary>
        public int PlayerAdded { get; }
        /// <summary>放进玩家背包时放不下的剩余数（明确计数，不静默丢弃）。</summary>
        public int PlayerLeftover { get; }
        /// <summary>本次编辑是一次同物品两槽拆分（source −k / destination +k）。</summary>
        public bool SplitApplied { get; }
        /// <summary>本次编辑是容器内的物品移动（非拆分、非取产物）。</summary>
        public bool MoveApplied { get; }
        /// <summary>本次编辑同时做了"取产物 + 把物品放进容器"。</summary>
        public bool TakeAndPlaceApplied { get; }
        /// <summary>
        /// 请求者的主机侧背包是创造仓库（<c>ComponentCreativeInventory</c>）：本次编辑按创造模式
        /// 规则校验（跳过"容器增加 ⇒ 玩家减少"的物品守恒），不是生存守恒。
        /// </summary>
        public bool CreativeInventory { get; }

        /// <summary>
        /// 创造模式放行的 **data 变体** 件数（物品完整值不在创造仓库，但 contents 位由创造仓库
        /// 提供：内存条地图 / 告示牌文字 / 家具设计 id）。允许计数，不是拒绝。
        /// </summary>
        public int CreativeDataVariantAllowed { get; }

        /// <summary>创造模式放行的"请求者权威背包里已有该物品"件数（移动，不是创造）。</summary>
        public int CreativeInventoryMoveAllowed { get; }

        public string Describe() =>
            "craftGrid=" + (CraftGridApplied ? "applied" : "same") + " resultTaken=" +
            ResultTakenCount + (ResultTakenCount > 0 ? "x" + ResultValue : string.Empty) +
            " predMismatch=" + (ResultPredictionMismatch ? "yes" : "no") +
            " player-" + PlayerRemoved + "+" + PlayerAdded + " leftover=" + PlayerLeftover +
            " kind=" + (TakeAndPlaceApplied ? "take+place" : SplitApplied ? "split" :
                MoveApplied ? "move" : "none") +
            " requester=" + (CreativeInventory ? "creative" : "survival") +
            " creativeDataVariant=" + CreativeDataVariantAllowed +
            " creativeInventoryMove=" + CreativeInventoryMoveAllowed;
    }

    /// <summary>
    /// 主机一次熔炉轮询的目标：一个熔炉 + 需要收到炉况的客户端
    /// （打开它的客户端，以及 <c>NearFurnaceDistance</c> 内看得见它的客户端）。
    /// 由模块按"被打开的容器"先建，端口再补"附近的燃烧熔炉"（关闭但可见也能同步外观）。
    /// </summary>
    public sealed class FurnacePollTarget
    {
        public FurnacePollTarget(ContainerKey key)
        {
            Key = key;
        }

        public ContainerKey Key { get; }

        /// <summary>去重后的接收者 clientId（打开者 + 附近可见者）。</summary>
        public System.Collections.Generic.List<int> ClientIds { get; } =
            new System.Collections.Generic.List<int>(4);

        public bool AddClient(int clientId)
        {
            if (ClientIds.Contains(clientId)) return false;
            ClientIds.Add(clientId);
            return true;
        }

        public static FurnacePollTarget Find(
            System.Collections.Generic.List<FurnacePollTarget> targets, ContainerKey key)
        {
            if (targets == null) return null;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] != null && targets[i].Key.Equals(key)) return targets[i];
            }
            return null;
        }
    }

    /// <summary>
    /// 客户端输入外壳（Engine/EntityRequestInput.cs:SuComponentInput）到模块的提交入口。
    /// 返回 true = 请求已入队发送（调用方随后必须吞掉原生输入，客户端不产生本地效果）。
    /// </summary>
    public interface IBlockUseRequestSink
    {
        bool TrySubmitUse(in BlockUseRequestData request, out string reason);
    }

    /// <summary>
    /// 引擎适配端口（实现 = Engine/ContainerReplicaEngine.cs）。
    ///
    /// 模块只做协议 / 序号 / 去重 / 超时 / 计数 / 广播；所有涉及 Game 类型的操作都在实现里：
    ///   · 客户端：抓交互射线命中的方块、打开副本面板、应用权威槽位、抓/恢复背包快照；
    ///   · 主机：校验玩家/距离/方块/方块行为、执行原生交互、创建（缺失的）方块实体库存、
    ///     校验并落地容器编辑、抓取权威状态、轮询"被客户端打开的容器"的变化。
    ///
    /// 用 <c>in</c> 传值类型，容器状态用不可变类；没有静态状态。
    /// </summary>
    public interface IBlockUsePort
    {
        /// <summary>每帧一次（模块 Tick 调用）：挂接 / 摘除输入外壳与副本实体。</summary>
        void Pump();

        /// <summary>模块在 Attach 时写入自己，Detach 时写 null（客户端输入 → 请求的唯一入口）。</summary>
        void SetRequestSink(IBlockUseRequestSink sink);

        // ------------------------------------------------------------------ 客户端
        /// <summary>本机玩家的当前背包快照（容器守恒校验的 base / desired）。</summary>
        bool TryCapturePlayerInventory(out PlayerInventorySnapshot snapshot, out string reason);

        /// <summary>副本面板是否仍然打开（玩家可能用 ESC / 点击外部关闭）。</summary>
        bool IsContainerViewOpen { get; }

        /// <summary>抓当前副本面板的槽位（客户端本地显示值）。</summary>
        bool TryCaptureOpenContainer(out int[] values, out int[] counts, out string reason);

        /// <summary>打开副本面板（副本组件不存在时按 kind 创建；绝不改地形）。</summary>
        bool TryOpenContainerView(ContainerStateData state, out string reason);

        /// <summary>把主机权威状态写进副本面板（客户端不由此产生新的编辑请求）。</summary>
        void ApplyContainerStateView(ContainerStateData state);

        /// <summary>关闭副本面板（reason 只用于日志 / 计数）。</summary>
        void CloseContainerView(string reason);

        /// <summary>拒绝 / 超时回滚：把玩家背包恢复到编辑前的快照（撤销本地 UI 操作）。</summary>
        void RestorePlayerInventory(PlayerInventorySnapshot snapshot, string reason);

        // ------------------------------------------------------------------ 主机
        /// <summary>
        /// 校验并执行一次使用请求。返回 false = 明确拒绝（reason 必有值）。
        /// 容器请求成功时 <paramref name="outcome"/>.State 是该库存的权威快照。
        /// </summary>
        bool TryExecuteUse(int clientId, in BlockUseRequestData request,
            out BlockUseHostOutcome outcome, out string reason);

        /// <summary>
        /// 校验并落地一次容器编辑（base 必须与主机权威值一致；配方格 + 玩家背包按计划原子落地）。
        /// </summary>
        bool TryApplyContainerEdit(int clientId, in ContainerEditRequestData request,
            out ContainerEditOutcome outcome, out string reason);

        /// <summary>
        /// 轮询这些被客户端打开的容器：值变化时写入 <paramref name="states"/> 并返回数量。
        /// 主机自己的面板（原生 UI）改动、熔炉烧炼等都在这里被发现并广播。
        /// </summary>
        int PollContainerStates(IReadOnlyList<ContainerKey> keys, List<ContainerStateData> states);

        /// <summary>
        /// 主机：给 <paramref name="targets"/> 补上"任一连机角色
        /// <c>NearFurnaceDistance</c> 内的熔炉"（**只补真正在烧 / 刚熄灭的**，关闭但可见的
        /// 熔炉外观也能同步）。已有 target（被客户端打开）只追加附近客户端，不重复建。
        /// 返回因"不在任何客户端附近且没人打开"而跳过本拍的燃烧熔炉数
        /// （<paramref name="targets"/> 已满时也计入跳过并写明确日志）。
        /// </summary>
        int AddNearbyFurnaceTargets(List<FurnacePollTarget> targets, int maxTargets);

        /// <summary>
        /// 主机：抓一个熔炉的权威运行状态（热量 / 烧炼进度 / 剩余燃烧时间 / 燃烧外观格子值）。
        /// 方块实体不存在 / 区块卸载 / 方块已不是熔炉 → 返回 false 并给出原因。
        /// </summary>
        bool TryCaptureFurnaceState(ContainerKey key, out FurnaceStateData state,
            out string reason);

        /// <summary>
        /// 客户端：把权威炉况写进副本组件（热量 / 进度 / 剩余燃烧时间）并落地点亮格子值。
        /// 纯表现：不推进烧炼、不消耗燃料、不写权威库存、不产生本地地形编辑。
        /// 返回 false 仅在"两样都没应用"时；<paramref name="reason"/> 说明哪一部分失败。
        /// </summary>
        bool ApplyFurnacePresentation(FurnaceStateData state, out bool litApplied,
            out bool progressApplied, out string reason);

        /// <summary>抓取一个容器的权威状态（拒绝编辑时回滚 / 首次打开时下发）。</summary>
        bool TryCaptureContainerState(ContainerKey key, out ContainerStateData state,
            out string reason);

        string Describe();
    }
}

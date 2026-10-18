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

        /// <summary>校验并落地一次容器编辑（base 必须与主机权威值一致）。</summary>
        bool TryApplyContainerEdit(int clientId, in ContainerEditRequestData request,
            out ContainerStateData state, out string reason);

        /// <summary>
        /// 轮询这些被客户端打开的容器：值变化时写入 <paramref name="states"/> 并返回数量。
        /// 主机自己的面板（原生 UI）改动、熔炉烧炼等都在这里被发现并广播。
        /// </summary>
        int PollContainerStates(IReadOnlyList<ContainerKey> keys, List<ContainerStateData> states);

        /// <summary>抓取一个容器的权威状态（拒绝编辑时回滚 / 首次打开时下发）。</summary>
        bool TryCaptureContainerState(ContainerKey key, out ContainerStateData state,
            out string reason);

        string Describe();
    }
}

using System;
using System.Collections.Generic;
using Game;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 一次"客户端编辑销毁了一个方块"的掉落计划（**只在主线程 / 一次落地内有效**）。
    ///
    /// 为什么需要计划对象（而不是简单地改完格再算掉落）：
    ///   · 引擎 <c>SubsystemTerrain.DestroyCell</c> 的顺序是
    ///     <c>GetDropValues</c> → 逐条 <c>OnItemHarvested</c>（可改写 <c>newValue</c>）→ <c>AddPickable</c>
    ///     → 最后 <c>ChangeCell</c>；
    ///   · <c>SubsystemEditableItemBehavior.OnItemHarvested</c> 会读该格的方块私有数据
    ///     （标牌 / 家具设计 id），而 <c>ChangeCell</c> 的 <c>OnBlockRemoved</c> 会把它删掉。
    ///     于是**必须**在改格之前算好掉落，改格之后再生成拾取物。
    ///
    /// Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.DestroyCell
    /// </summary>
    public sealed class HostBlockDropPlan
    {
        public int X;
        public int Y;
        public int Z;
        /// <summary>改格前的权威值（掉落按它算）。</summary>
        public int OldValue;
        /// <summary>真正写进格子的值：= 请求值，除非 <c>OnItemHarvested</c> 改写了它（引擎同形）。</summary>
        public int NewValue;
        /// <summary>发起编辑的玩家端（&gt; 0 = 给它登记拾取宽限）。</summary>
        public int ClientId;
        /// <summary>进入本次落地时 <c>SubsystemPickables.Pickables</c> 的条数（用于判断引擎是否已生成）。</summary>
        public int PickableCountBefore;
        /// <summary>已通过 <c>GetDropValues</c> + <c>OnItemHarvested</c> 过滤后的掉落（Count &gt; 0 且非空气）。</summary>
        internal readonly List<BlockDropValue> Drops = new List<BlockDropValue>();
    }

    /// <summary>
    /// 主机"客户端编辑造成的方块销毁 → 掉落"的引擎端口（实现方 = <c>Engine/PickableReplicaEngine.cs</c>，
    /// 组合根注入给 <c>TerrainBridge</c>）。
    ///
    /// 分成"准备"和"生成"两步是**必须**的（见 <see cref="HostBlockDropPlan"/> 的注释）：准备发生在
    /// <c>ChangeCell</c> 之前，生成发生在之后。两步都只在主线程、同一次落地内调用。
    /// </summary>
    public interface IHostBlockDropSink
    {
        /// <summary>
        /// 改格之前：按引擎 <c>Block.GetDropValues</c> + 每个掉落的 <c>OnItemHarvested</c> 算出掉落，
        /// 并给出真正要写进格子的值（行为可改写）。返回 false + reason = 引擎侧不可用（明确失败，不猜）。
        /// 掉落为空（草 / 水 / 花盆）不是失败：<see cref="HostBlockDropPlan.Drops"/> 为空即可。
        /// </summary>
        bool TryPrepareBlockDrops(int clientId, int x, int y, int z, int oldValue, int newValue,
            int toolLevel, out HostBlockDropPlan plan, out string reason);

        /// <summary>
        /// 改格之后：把准备好的掉落按引擎 <c>DestroyCell</c> 的口径生成
        /// （位置 = 格心 (x,y,z)+0.5，velocity = null → 引擎默认随机上抛）。
        /// <paramref name="engineSpawned"/> = 引擎自己的落地路径已经在该格生成过拾取物（不要再生成）；
        /// 返回 false + reason = 没有生成（engine-spawned / no-drop-values / spawn-failed / not-host）。
        /// </summary>
        bool TrySpawnPreparedDrops(HostBlockDropPlan plan, out int spawned,
            out bool engineSpawned, out string reason);
    }
}

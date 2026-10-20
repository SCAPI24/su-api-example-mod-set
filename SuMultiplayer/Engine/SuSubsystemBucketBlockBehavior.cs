using Engine;
using Game;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎注入点：替换 <c>Game.SubsystemBucketBlockBehavior</c>。
    ///
    /// 只在下述情况改变行为：**联机客户端 + 手持颜料桶（contents 128/129）+ 目标是家具方块**。
    /// 这时不能走 base（base 会调用 FurnitureBlock.Paint → 在客户端本地 TryAddDesignChain，
    /// 把联机设计写进客户端家具库，而且客户端本地分配的槽位号与主机不一致）。
    /// 改为把 "格子 + 颜色" 发给主机：主机用自己的权威设计链 clone/paint/TryAddDesignChain、
    /// 改格并广播，客户端只等结果。其余颜料桶用法（其它可上色方块）保持原样。
    ///
    /// Source: Survivalcraft/Game/SubsystemBucketBlockBehavior.cs:OnUse（contents 128/129 分支）
    /// Source: Survivalcraft/Game/FurnitureBlock.cs:FurnitureBlock.Paint
    /// </summary>
    public class SuSubsystemBucketBlockBehavior : SubsystemBucketBlockBehavior
    {
        /// <summary>组合根每帧注入（Engine/FurnitureReplicaEngine.cs）；null = 单机 / 未装配。</summary>
        public FurnitureReplicaEngine Runtime { get; set; }

        public override bool OnUse(Ray3 ray, ComponentMiner componentMiner)
        {
            if (Runtime != null && Runtime.IsClientReplica &&
                Runtime.TryInterceptClientFurniturePaint(ray, componentMiner))
            {
                return true;   // 已转成主机请求；本地不执行 Paint
            }
            return base.OnUse(ray, componentMiner);
        }
    }
}

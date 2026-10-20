using Engine;
using Game;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎注入点：替换 <c>Game.SubsystemHammerBlockBehavior</c>。
    ///
    /// 主机 / 单机：完全走 base（引擎原生 ScanDesign：扫描 → BuildFurnitureDialog →
    /// TryAddDesign → 销毁源方块 → 生成家具物品）。
    /// 联机客户端：**不执行** base.OnUse（那会在本地 TryAddDesign，污染客户端家具库），
    /// 改为 <see cref="FurnitureReplicaEngine.TryStartClientDesign"/>：
    /// 同样的扫描 + 对话框，点 Build 后只把设计 + 源格发给主机；本地不新增设计、
    /// 不销毁方块、不生成物品 —— 全部由主机校验后执行并广播。
    ///
    /// Source: Survivalcraft/Game/SubsystemHammerBlockBehavior.cs:OnUse
    /// Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:ScanDesign
    /// </summary>
    public class SuSubsystemHammerBlockBehavior : SubsystemHammerBlockBehavior
    {
        /// <summary>组合根每帧注入（Engine/FurnitureReplicaEngine.cs）；null = 单机 / 未装配。</summary>
        public FurnitureReplicaEngine Runtime { get; set; }

        public override bool OnUse(Ray3 ray, ComponentMiner componentMiner)
        {
            if (Runtime != null && Runtime.IsClientReplica)
            {
                return Runtime.TryStartClientDesign(ray, componentMiner);
            }
            return base.OnUse(ray, componentMiner);
        }
    }
}

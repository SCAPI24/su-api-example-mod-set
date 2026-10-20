using Engine;
using Game;
using SuMultiplayer.Modules;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎注入点：替换方块 227 <c>Game.FurnitureBlock</c>（IModInjector.RegisterBlock）。
    ///
    /// 只做两道客户端闸门（主机 / 单机完全走 base）：
    ///   · <see cref="GetAdHocCraftingRecipe"/>：该调用会 TryAddDesignChain；联机客户端返回
    ///     null 并计数（客户端不跑合成配方，权威产物由主机 ContainerState 下发）。
    ///
    /// Source: Survivalcraft/Game/FurnitureBlock.cs:Paint / GetAdHocCraftingRecipe
    /// </summary>
    public class SuFurnitureBlock : FurnitureBlock
    {
        /// <summary>组合根每帧注入（Engine/FurnitureReplicaEngine.cs）；null = 单机 / 未装配。</summary>
        public IFurnitureEngineEvents Runtime { get; set; }

        public override CraftingRecipe GetAdHocCraftingRecipe(SubsystemTerrain terrain,
            string[] ingredients, float heatLevel, float playerLevel)
        {
            if (Runtime != null && Runtime.IsMultiplayerClient)
            {
                Runtime.NoteClientCraftBlocked();
                return null;
            }
            return base.GetAdHocCraftingRecipe(terrain, ingredients, heatLevel, playerLevel);
        }
    }
}

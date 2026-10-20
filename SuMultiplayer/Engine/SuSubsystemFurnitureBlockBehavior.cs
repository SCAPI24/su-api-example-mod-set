using System;
using Engine;
using Game;
using SuMultiplayer.Furniture;
using SuMultiplayer.Modules;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎注入点：替换 <c>Game.SubsystemFurnitureBlockBehavior</c>。
    ///
    /// 主机 / 单机：完全走 base（世界存档里的设计表就是权威，Save/Load 原样）。
    /// 联机客户端：
    ///   · Save **写空** FurnitureDesigns / FurnitureSets —— 联机设计只存在于当前会话的
    ///     副本表里，绝不写进客户端自己的家具库 / 世界存档（<see cref="FurnitureSavePolicy"/>）；
    ///   · 地形里的家具格引用了本地设计表没有的设计时（客户端存档不含联机设计，或设计
    ///     还没同步到），base 的 AddTerrainFurniture 会 NullReference；这里逐入口显式计数
    ///     并跳过，**不崩溃、不猜设计**。设计到达后 GetDesign 正常，渲染恢复。
    ///
    /// Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:Load/Save
    /// Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:OnBlockAdded/Removed/
    ///         Modified/Generated（AddTerrainFurniture / RemoveTerrainFurniture 的 NRE 面）
    /// </summary>
    public class SuSubsystemFurnitureBlockBehavior : SubsystemFurnitureBlockBehavior
    {
        private long m_missingDesignCells;
        private long m_clientSaveSuppressions;

        /// <summary>组合根每帧注入（Engine/FurnitureReplicaEngine.cs）；null = 单机 / 未装配。</summary>
        public IFurnitureEngineEvents Runtime { get; set; }

        /// <summary>
        /// 本世界在本次会话里是否为联机客户端副本。由引擎适配器每帧写入，不随模块下线清除：
        /// 即使家具同步模块被熔断 / 卸载，客户端 Save 仍然必须写空，
        /// 否则副本设计会在下一次保存时污染客户端家具库（不能依赖模块存活）。
        /// </summary>
        public bool ClientReplicaSaveGuard { get; set; }

        /// <summary>客户端地形里引用了缺失设计的家具格数（模块状态行 / 自检可读）。</summary>
        public long MissingDesignCells => m_missingDesignCells;

        /// <summary>客户端 Save 被改写成空表的次数（只用于诊断）。</summary>
        public long ClientSaveSuppressions => m_clientSaveSuppressions;

        protected override void Save(ValuesDictionary valuesDictionary)
        {
            if ((ClientReplicaSaveGuard ||
                    (Runtime != null && Runtime.IsMultiplayerClient)) &&
                !FurnitureSavePolicy.WritesDesigns(true))
            {
                // 唯一允许的客户端保存形态：设计表为空。不要先调 base.Save —— 它会跑
                // GarbageCollectDesigns 并把 m_furnitureDesigns 全量写进 FurnitureDesigns。
                int designs = CountDesigns();
                m_clientSaveSuppressions++;
                Runtime.NoteClientSaveSuppressed(designs);
                valuesDictionary.SetValue("FurnitureDesigns", new ValuesDictionary());
                valuesDictionary.SetValue("FurnitureSets", new ValuesDictionary());
                return;
            }
            base.Save(valuesDictionary);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (IsFurnitureWithMissingDesign(value, out int designIndex))
            {
                NoteMissingDesign(designIndex, x, y, z, "added");
                return;   // base 会 NullReference；粒子也不可能存在（设计缺失时不会创建）
            }
            base.OnBlockAdded(value, oldValue, x, y, z);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (IsFurnitureWithMissingDesign(value, out int designIndex))
            {
                NoteMissingDesign(designIndex, x, y, z, "removed");
                return;   // 没有 useCount 可减，也没有本类创建的粒子可收
            }
            base.OnBlockRemoved(value, newValue, x, y, z);
        }

        public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
        {
            if (IsFurnitureWithMissingDesign(oldValue, out int designIndex))
            {
                // 旧设计的 useCount / 粒子都不存在：只处理新值（新值也缺失时同样计数跳过）。
                NoteMissingDesign(designIndex, x, y, z, "modified-old");
                OnBlockAdded(value, oldValue, x, y, z);
                return;
            }
            base.OnBlockModified(value, oldValue, x, y, z);
        }

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            if (!isLoaded && IsFurnitureWithMissingDesign(value, out int designIndex))
            {
                NoteMissingDesign(designIndex, x, y, z, "generated");
                return;
            }
            base.OnBlockGenerated(value, x, y, z, isLoaded);
        }

        private int CountDesigns()
        {
            int count = 0;
            for (int i = 0; i < MaxDesigns; i++)
            {
                if (GetDesign(i) != null) count++;
            }
            return count;
        }

        private bool IsFurnitureWithMissingDesign(int value, out int designIndex)
        {
            designIndex = -1;
            if (Game.Terrain.ExtractContents(value) != FurnitureBlock.Index) return false;
            designIndex = FurnitureBlock.GetDesignIndex(Game.Terrain.ExtractData(value));
            return GetDesign(designIndex) == null;
        }

        private void NoteMissingDesign(int designIndex, int x, int y, int z, string kind)
        {
            m_missingDesignCells++;
            Runtime?.NoteMissingDesignCell(designIndex);
        }
    }
}

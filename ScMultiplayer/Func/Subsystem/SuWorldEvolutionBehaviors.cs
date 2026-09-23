using Engine;
using Game;
using System;
using System.Collections;
using GameEntitySystem;

namespace ScMultiplayer
{
    // 客户端在联机房间里**不跑"世界自发演化"**：仙人掌生长、腐坏、地毯/落叶的邻接消失、
    // 耕地干湿、常春藤蔓延、树苗长成树。全部由主机算，结果经已有的格子下发同步到客户端。
    //
    // 为什么必须停：客户端自己长出来的方块，主机从来没有过；区块校验是**单向补漏**
    // （SendHostTerrainChunkSync 只发 Sequence > knownRevision 的格），这种多余格子永远不在校验内容里，
    // 于是永远纠不回来。实测表现：只有某一端长出的仙人掌，在该端能挖掉、主机侧本来就是空气，
    // 结果 dig.result accepted=False（真拒绝）且不产生掉落物。
    //
    // 闸门与已有的 SuSubsystemPlantBlockBehavior / SuSubsystemGrassBlockBehavior /
    // SuSubsystemDeciduousLeavesBlockBehavior 完全一致：用 HostTerrainAuthority 判断本端是否权威，
    // 单机/主机照旧，联机客户端一律不推进。
    //
    // ⚠️ 注册方式必须是 `Pak/Database.xml` 的 **GUID 补丁**（见 ScMultiplayerLifecycle.OnLoad 里
    // `database.FindDatabaseObject(new Guid(...))` 那一组），**不能**用
    // `modInjector.Register("Game.SubsystemXBlockBehavior", …)` —— 后者实测会让主机初始化在
    // `[ScMP] Database hooks applied` 之后挂死，世界永远加载不上（2026-09-23 已复现并记录在
    // doc/PROJECT-LOG.md）。
    internal static class ClientGrowthGate
    {
        internal static bool Allows(SubsystemTerrain terrain, int x, int z)
        {
            return HostTerrainAuthority.IsAuthoritative ||
                HostTerrainAuthority.IsReadyForAuthoritativeMutation(terrain, x, z);
        }

        // 客户端不跑 Update：把该行为内部的待处理表清空，避免它在客户端累积
        // （与 SuSubsystemGrassBlockBehavior 在客户端清 m_toUpdate 同一做法）。
        internal static void ClearPending(object owner, string fieldName, Type declaringType)
        {
            object table = ScMultiplayer.ModManager?.ModParentField?
                .GetParentField<object>(owner, fieldName, declaringType);
            (table as IDictionary)?.Clear();
        }
    }

    // Source: Survivalcraft/Game/SubsystemCactusBlockBehavior.cs
    // 仙人掌：OnPoll 里 QueueCellChange(x, y+1, z, MakeBlockValue(127)) 就是生长（127 = 仙人掌）；
    // OnNeighborBlockChanged 负责"底下不是沙子就消失"。
    public sealed class SuSubsystemCactusBlockBehavior : SubsystemCactusBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(
            int x, int y, int z, int neighborX, int neighborY, int neighborZ)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }
    }

    // Source: Survivalcraft/Game/SubsystemRotBlockBehavior.cs:SubsystemRotBlockBehavior.OnPoll
    // 食物腐坏：OnPoll 里直接 ChangeCell 成腐坏物。
    public sealed class SuSubsystemRotBlockBehavior : SubsystemRotBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnPoll(value, x, y, z, pollPass);
        }
    }

    // Source: Survivalcraft/Game/SubsystemCarpetBlockBehavior.cs
    // 地毯：邻接/轮询判定支撑，不成立就 DestroyCell。
    public sealed class SuSubsystemCarpetBlockBehavior : SubsystemCarpetBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(
            int x, int y, int z, int neighborX, int neighborY, int neighborZ)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }
    }

    // Source: Survivalcraft/Game/SubsystemFallenLeavesBlockBehavior.cs
    // 落叶：轮询/邻接判定后会 DestroyCell。
    public sealed class SuSubsystemFallenLeavesBlockBehavior : SubsystemFallenLeavesBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(
            int x, int y, int z, int neighborX, int neighborY, int neighborZ)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }
    }

    // Source: Survivalcraft/Game/SubsystemSoilBlockBehavior.cs
    // 耕地：OnPoll 入队干湿变化，Update 里 ChangeCell。
    public sealed class SuSubsystemSoilBlockBehavior : SubsystemSoilBlockBehavior, IUpdateable
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(
            int x, int y, int z, int neighborX, int neighborY, int neighborZ)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        void IUpdateable.Update(float dt)
        {
            if (HostTerrainAuthority.IsAuthoritative)
            {
                base.Update(dt);
                return;
            }
            ClientGrowthGate.ClearPending(this, "m_toDegrade",
                typeof(SubsystemSoilBlockBehavior));
            ClientGrowthGate.ClearPending(this, "m_toHydrate",
                typeof(SubsystemSoilBlockBehavior));
        }
    }

    // Source: Survivalcraft/Game/SubsystemIvyBlockBehavior.cs
    // 常春藤：OnPoll/Update 蔓延，OnNeighborBlockChanged 判定支撑。
    public sealed class SuSubsystemIvyBlockBehavior : SubsystemIvyBlockBehavior, IUpdateable
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(
            int x, int y, int z, int neighborX, int neighborY, int neighborZ)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        void IUpdateable.Update(float dt)
        {
            if (HostTerrainAuthority.IsAuthoritative)
            {
                base.Update(dt);
                return;
            }
            ClientGrowthGate.ClearPending(this, "m_toUpdate", typeof(SubsystemIvyBlockBehavior));
        }
    }

    // Source: Survivalcraft/Game/SubsystemSaplingBlockBehavior.cs
    // 树苗：OnBlockAdded/OnNeighborBlockChanged 登记，Update 里长成树（ChangeCell）。
    public sealed class SuSubsystemSaplingBlockBehavior : SubsystemSaplingBlockBehavior, IUpdateable
    {
        public override void OnNeighborBlockChanged(
            int x, int y, int z, int neighborX, int neighborY, int neighborZ)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnBlockAdded(value, oldValue, x, y, z);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (ClientGrowthGate.Allows(SubsystemTerrain, x, z))
                base.OnBlockRemoved(value, newValue, x, y, z);
        }

        void IUpdateable.Update(float dt)
        {
            if (HostTerrainAuthority.IsAuthoritative)
            {
                base.Update(dt);
                return;
            }
            ClientGrowthGate.ClearPending(this, "m_saplings",
                typeof(SubsystemSaplingBlockBehavior));
        }
    }
}

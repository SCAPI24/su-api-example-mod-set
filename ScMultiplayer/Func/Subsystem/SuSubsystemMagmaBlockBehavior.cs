using Engine;
using Game;
using System.Collections;
using TemplatesDatabase;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P7c：**岩浆不得流进他人领地**（设计稿 §5 统一执法入口、§7 P7c）。
    ///
    /// 与 <see cref="SuSubsystemWaterBlockBehavior"/> 同样是整型替换 Database 里的
    /// <c>Game.SubsystemMagmaBlockBehavior</c>（Class 参数 GUID
    /// <c>828db45d-1460-434d-8879-ccd0d3810518</c>，见 <c>Pak/Database.xml</c>）+ 显式接口实现。
    ///
    /// 比水多两个窗口（源码依据）：岩浆除 <c>SpreadFluid()</c> 外，还在
    /// <c>OnBlockAdded</c>（自身 3×3×3 邻域）与 <c>OnNeighborBlockChanged</c>（被改动的那一格）
    /// 里调用**私有**的 <c>ApplyMagmaNeighborhoodEffect</c>：点燃可燃物（火由 P6 的
    /// <c>SuSubsystemFireBlockBehavior</c> 熄灭）并 <c>DestroyCell</c> 掉 61/62 号方块。
    /// 这两条路径不经过流体工作表窗口，所以必须各自开窗快照。
    ///
    /// <c>OnFluidInteract</c>（岩浆遇水 → 水被销毁、该格变成 67）落在 <c>SpreadFluid()</c> 内，
    /// 由 Update 窗口覆盖，无需单独处理。
    /// </summary>
    public sealed class SuSubsystemMagmaBlockBehavior : SubsystemMagmaBlockBehavior, IUpdateable
    {
        private readonly SuFluidClaimGuard m_guard = new SuFluidClaimGuard();
        private SubsystemTerrain m_subsystemTerrain;
        // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:SubsystemFluidBlockBehavior.m_toUpdate
        private IDictionary m_toUpdate;
        private int m_fluidBlockIndex;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(true);
            m_fluidBlockIndex = HandledBlocks[0];
            m_toUpdate = Game.Program.ModManager.ModParentField.GetParentField<IDictionary>(
                this, "m_toUpdate", typeof(SubsystemFluidBlockBehavior));
        }

        void IUpdateable.Update(float dt)
        {
            ScMultiplayer mod = ActiveHostMod();
            if (mod == null)
            {
                m_guard.Reset();
                base.Update(dt);
                return;
            }
            m_guard.CaptureFluidWorkList(mod, m_subsystemTerrain?.Terrain, m_toUpdate);
            base.Update(dt);
            m_guard.Revert(mod, m_subsystemTerrain, m_fluidBlockIndex);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            ScMultiplayer mod = ActiveHostMod();
            if (mod == null)
            {
                base.OnBlockAdded(value, oldValue, x, y, z);
                return;
            }
            // 邻域效应作用在自身 ±1（3×3×3），半径取 1 即可
            m_guard.CaptureNeighborhood(mod, m_subsystemTerrain?.Terrain, x, y, z, 1);
            base.OnBlockAdded(value, oldValue, x, y, z);
            m_guard.Revert(mod, m_subsystemTerrain, m_fluidBlockIndex);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            ScMultiplayer mod = ActiveHostMod();
            if (mod == null)
            {
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
                return;
            }
            // 效应作用在**邻居格**及其 ±1，因此相对岩浆自身取半径 2 才能覆盖完整
            m_guard.CaptureNeighborhood(mod, m_subsystemTerrain?.Terrain, x, y, z, 2);
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            m_guard.Revert(mod, m_subsystemTerrain, m_fluidBlockIndex);
        }

        /// <summary>只有本机是联机主机时才执法（理由同水的实现）。</summary>
        private ScMultiplayer ActiveHostMod()
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null || !mod.IsNetworkSessionActive(Project) || !mod.IsNetworkHost(Project))
                return null;
            return mod;
        }
    }
}

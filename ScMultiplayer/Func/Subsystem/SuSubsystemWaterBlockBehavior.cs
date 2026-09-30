using Engine;
using Game;
using System.Collections;
using TemplatesDatabase;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P7c：**水不得流进他人领地**（设计稿 §5 统一执法入口、§7 P7c）。
    ///
    /// 挂钩方式与 <see cref="SuSubsystemFireBlockBehavior"/> 一致：整型替换 Database 里的
    /// <c>Game.SubsystemWaterBlockBehavior</c>（Class 参数 GUID
    /// <c>4ecb005a-64f7-4036-b470-cc163fab65c2</c>，见 <c>Pak/Database.xml</c>），
    /// 用**显式接口实现** <c>IUpdateable.Update</c> 保证引擎的子系统更新派发落到这里
    /// （引擎 <c>SubsystemWaterBlockBehavior.Update</c> 不是虚方法，普通覆写挂不上）。
    ///
    /// 具体执法逻辑见 <see cref="SuFluidClaimGuard"/>：唯一能拦的窗口就是
    /// <c>base.Update(dt)</c> 前后（引擎的流体写入点在 <c>SpreadFluid()</c> 里，非虚且自行
    /// 冲刷 <c>m_modifiedCells</c>）。
    /// </summary>
    public sealed class SuSubsystemWaterBlockBehavior : SubsystemWaterBlockBehavior, IUpdateable
    {
        private readonly SuFluidClaimGuard m_guard = new SuFluidClaimGuard();
        private SubsystemTerrain m_subsystemTerrain;
        // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:SubsystemFluidBlockBehavior.m_toUpdate
        // 引擎自己的流体待处理工作表：本帧会被模拟的格都在这里，是"本帧可能被写入哪些格"的
        // 唯一可用来源。
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
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            // 只在本机是**联机主机**时执法：客户端用 clientId 0 会被当成主机玩家身份，
            // 结论必然错误；客户端的地形最终以主机广播为准，本地不执法。
            if (mod == null || !mod.IsNetworkSessionActive(Project) || !mod.IsNetworkHost(Project))
            {
                base.Update(dt);
                return;
            }
            m_guard.CaptureFluidWorkList(mod, m_subsystemTerrain?.Terrain, m_toUpdate);
            CaptureFluidDataValues();
            base.Update(dt);
            m_guard.Revert(mod, m_subsystemTerrain, m_fluidBlockIndex);
            RegisterFluidDataChanges();
        }
        // 2026-10-01（用户口径）：流体 **data-only**（水位 / 顶面标志）变化也要同步给其他端。
        // 引擎 `SubsystemFluidBlockBehavior.cs:53` 是用 `SetCellValueFast` 直接写 data 的，不进改动表；
        // 这里在 `base.Update` 前后各取一次快照，把"内容不变、data 变了"的格主动登记进下发批次
        //（内容变化本来就经 `ChangeCell` 自动下发，不重复登记）。每帧最多登记 64 格。
        private readonly System.Collections.Generic.List<Point3> m_fluidDataCells =
            new System.Collections.Generic.List<Point3>();
        private readonly System.Collections.Generic.List<int> m_fluidDataValues =
            new System.Collections.Generic.List<int>();

        private void CaptureFluidDataValues()
        {
            m_fluidDataCells.Clear();
            m_fluidDataValues.Clear();
            if (m_toUpdate == null || m_subsystemTerrain?.Terrain == null)
                return;
            foreach (object key in m_toUpdate.Keys)
            {
                if (m_fluidDataCells.Count >= 64)
                    return;
                if (!(key is Point3 point) || point.Y < 0 || point.Y > 255)
                    continue;
                if (m_subsystemTerrain.Terrain.GetCellContents(point.X, point.Y, point.Z) !=
                    m_fluidBlockIndex)
                    continue;
                m_fluidDataCells.Add(point);
                m_fluidDataValues.Add(
                    m_subsystemTerrain.Terrain.GetCellValue(point.X, point.Y, point.Z));
            }
        }

        private void RegisterFluidDataChanges()
        {
            if (m_subsystemTerrain is not SuSubsystemTerrain suTerrain ||
                m_subsystemTerrain.Terrain == null)
                return;
            for (int i = 0; i < m_fluidDataCells.Count; i++)
            {
                Point3 point = m_fluidDataCells[i];
                int now = m_subsystemTerrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
                int before = m_fluidDataValues[i];
                if (now == before)
                    continue;
                if (Terrain.ExtractContents(now) != Terrain.ExtractContents(before))
                    continue;                       // 内容变化 → ChangeCell 已下发
                suTerrain.MarkCellModifiedForBroadcast(point);   // 只改 data ⇒ 主动登记
            }
            m_fluidDataCells.Clear();
            m_fluidDataValues.Clear();
        }

    }
}

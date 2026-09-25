using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    /// <summary>
    /// P7a：主机本机挖/放的领地执法（Database 替换 ComponentMiner，GUID 9dc356e5-7dc8-45f6-8779-827ddee9966c）。
    ///
    /// **挖掘这一路的正确做法是"零进度"，不是"挖完再改回"**（2026-09-25 按用户口径修正）：
    ///   · 引擎的挖掘进度 = `(游戏时间 - m_digStartTime) / 挖掘耗时`，`Dig()` 每帧重算，
    ///     进度到 1 才 `DestroyCell` 掉方块（引擎 `ComponentMiner.cs:108-161`）；
    ///   · 本类的 `void IUpdateable.Update(dt)` 是更新循环**唯一**入口：先跑守卫、再 `base.Update(dt)`
    ///     （玩家输入与 `Dig()` 都在 base 里执行）⇒ 在 `base` **之前**清掉被否决的挖掘状态后，
    ///     `Dig()` 下一次只能"从这一刻重新开始"（`m_digStartTime = 当前时间`，进度恒为 0），
    ///     **领地里根本不会出现挖掘进度**，方块也就永远不会被挖掉；
    ///   · "挖完再改回"只保留为**最后一道保证**：万一某一帧进度仍然跑满（极软的方块 + 长帧），
    ///     把该格改回"本帧开始时的原值"并再 `Poke` 一次。
    ///
    /// 纪律不变：只在主机分支、只认本机玩家、只处理非本人拥有的领地格；其余一律原样走 base。
    /// </summary>
    public class SuComponentMiner : ComponentMiner, IUpdateable
    {
        private double m_nextDeniedNoticeTime;
        private CellFace? m_deniedCell;
        private int m_deniedCellValue;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            Log.Information("[ScMP-P7a] SuComponentMiner active (host-local region enforcement)");
        }

        void IUpdateable.Update(float dt)
        {
            bool denied = SuppressDeniedDig();
            base.Update(dt);
            RestoreDeniedDigCell(denied);
        }

        /// <summary>
        /// 在引擎累加/结算挖掘**之前**把被否决的挖掘状态清零。
        /// 返回 true 表示本帧的挖掘目标已被否决（后续兜底需要用到快照）。
        ///
        /// Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Dig（119-125 行：新目标才重设
        /// `m_digStartTime` / `DigCellFace`，进度由两者算出）、`:96 Poke`、`:82 DigProgress`
        /// （`DigCellFace` 为空时进度读出来就是 0 —— 所以清掉它，界面上也不会有进度）。
        /// </summary>
        private bool SuppressDeniedDig()
        {
            m_deniedCell = null;
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null || !ScMultiplayer.IsHost || ScMultiplayer.client?.IsConnected != true)
                return false;
            ComponentPlayer player = Entity.FindComponent<ComponentPlayer>(false);
            if (player?.PlayerData == null || !mod.IsLocalPlayerData(player.PlayerData))
                return false;
            // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs
            // "<DigCellFace>k__BackingField"（本 Mod 既有的私有字段读取方式）
            CellFace? face = ScMultiplayer.ModManager.ModParentField.GetParentField<CellFace?>(
                this, "<DigCellFace>k__BackingField", typeof(ComponentMiner));
            if (!face.HasValue)
                return false;
            var cell = new Point3(face.Value.X, face.Value.Y, face.Value.Z);
            if (mod.CanRegionModifyCell(0, cell, out RegionClaim claim, out string reason))
                return false;
            SubsystemTerrain terrain = Project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
                return false;
            // 兜底用的"本帧原值"快照
            m_deniedCell = face.Value;
            m_deniedCellValue = terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z);
            // 清零：目标格置空 ⇒ 引擎下一次 Dig() 视为"全新一次挖掘"，进度从 0 起；
            // 再把已累计的进度显式归零（界面上 DigProgress 也读作 0）。
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(this,
                "<DigCellFace>k__BackingField", (CellFace?)null, typeof(ComponentMiner));
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(this,
                "m_digProgress", 0f, typeof(ComponentMiner));
            if (Time.RealTime >= m_nextDeniedNoticeTime)
            {
                m_nextDeniedNoticeTime = Time.RealTime + 1.0;
                mod.NotifyRegionModificationDenied(0, cell, claim, "dig", reason);
            }
            return true;
        }

        /// <summary>最后一道保证：若该格在本帧内仍然被改掉（真的被挖掉了），改回本帧原值。</summary>
        private void RestoreDeniedDigCell(bool denied)
        {
            if (!denied || !m_deniedCell.HasValue)
                return;
            CellFace face = m_deniedCell.Value;
            m_deniedCell = null;
            SubsystemTerrain terrain = Project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
                return;
            if (terrain.Terrain.GetCellValue(face.X, face.Y, face.Z) == m_deniedCellValue)
                return;   // 没被改动：绝大多数情况走这里（零进度 ⇒ 挖不掉）
            terrain.ChangeCell(face.X, face.Y, face.Z, m_deniedCellValue);
            Poke(forceRestart: true);
        }
    }
}

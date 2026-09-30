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
    /// **主机端与客户端都跑这一套**（2026-10-01 用户口径）：
    ///   · 主机：`CanRegionModifyCell(0, …)`（本端身份）；
    ///   · 客户端：`CanLocalPlayerModifyRegionCell(…)`（本机身份比对拥有者）—— 否则"等主机否决再回滚"
    ///     之前，本地预测的进度/裂纹已经画在屏幕上了；被赋予/被剥夺领地后 claims 一到即生效。
    ///
    /// 纪律不变：只认本机玩家、只处理**非本人拥有**的领地格；其余一律原样走 base。
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
            if (mod == null)
                return false;
            // 主机：本机玩家的挖由主机自己执法（P7a 起）；
            // 客户端：本机预测的进度/裂纹也必须挡在 base 之前，别等主机否决再回滚。
            bool isHost = ScMultiplayer.IsHost;
            bool isClient = !isHost && ScMultiplayer.client?.IsConnected == true;
            if (!isHost && !isClient)
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
            // 主机走原口径（clientId=0 → 本端身份）；客户端用本机身份直接比对拥有者
            //（客户端没有主机的 `m_clientRecordKeys`；被赋予/剥夺后 claims 一到就生效）。
            RegionClaim claim;
            string reason;
            bool allowed = isHost
                ? mod.CanRegionModifyCell(0, cell, out claim, out reason)
                : mod.CanLocalPlayerModifyRegionCell(cell, out claim, out reason);
            if (allowed)
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
                if (isHost)
                    mod.NotifyRegionModificationDenied(0, cell, claim, "dig", reason);
                else
                    // 客户端只写本端日志：不冒充主机审计，也不重复发聊天（通知由主机那条路负责）
                    Log.Information("[ScMP] " + (string.IsNullOrEmpty(reason)
                        ? "这里不能挖（领地）" : reason) + "（dig）");
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

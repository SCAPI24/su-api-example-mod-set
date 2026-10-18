using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端**方块行为通知**的单一漏斗（presentation-only）。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemTerrain.cs:ChangeCell:229-277）：
    ///   · 内容变化：旧内容的 behaviors → <c>OnBlockRemoved(old, new, x, y, z)</c>，
    ///     新内容的 behaviors → <c>OnBlockAdded(new, old, x, y, z)</c>；
    ///   · 内容不变（data-only）：该内容的 behaviors → <c>OnBlockModified(new, old, x, y, z)</c>。
    /// 客户端的地形走 <c>TerrainBridge.ApplyCells</c> 的 <c>SetCellValueFast</c>（绕过 ChangeCell），
    /// 这条通知路径原本完全不会发生 —— 营火 / 火把等"粒子由 behavior 自己维护"的表现就会永久陈旧。
    ///
    /// 本类**镜像**上面三条派发规则，但只派发到
    /// <see cref="BlockBehaviorNotifyPolicy.IsPresentationBehavior"/> 允许的纯表现行为：
    ///   · campfire（本 mod 的 SuSubsystemCampfireBlockBehavior 外壳，客户端走
    ///     <see cref="CampfireReplicaPresentation"/>）；
    ///   · torch（Add/Remove/Modified 只碰 FireParticleSystem）。
    /// 其它 behavior（衣柜 / 工作台 / 熔炉 / 分配器会 AddEntity；门 / 栅栏 / 楼梯会 ChangeCell）
    /// 一律**拒绝通知**并计入 <c>clientBehaviorDeniedWrites</c>：客户端副本保持 mutation-denied，
    /// 不允许它们通过这条补通知路径写格 / 生成实体。
    ///
    /// **不补 OnNeighborBlockChanged**：引擎在 <c>SubsystemTerrain.ProcessModifiedCells</c>
    /// （Source: SubsystemTerrain.cs:64-86）里派发它，而 <c>ApplyCells</c> 特意不写
    /// <c>m_modifiedCells</c>（否则回传内容会把块重新标脏、又触发一轮同步）。
    /// 邻居变化的后果由主机权威执行并作为普通 CellChange 下发 —— 客户端只跟着格子值收表现。
    ///
    /// 无静态状态：每个 <c>TerrainBridge</c> 一个实例；只在主线程调用。
    /// </summary>
    public sealed class ClientBlockBehaviorNotifier
    {
        private const long SummaryLogMs = 10000;

        private readonly List<SubsystemBlockBehavior> m_dispatched =
            new List<SubsystemBlockBehavior>(4);
        /// <summary>整块下发一次会上千格：SubsystemBlockBehaviors 按 Project 缓存，不逐格 FindSubsystem。</summary>
        private Project m_project;
        private SubsystemBlockBehaviors m_behaviors;
        private long m_notified;
        private long m_deniedWrites;
        private long m_missingSubsystem;
        private long m_missingBehaviors;
        private long m_notifyFaults;
        private long m_cells;
        private long m_lastLogMs;
        private long m_lastProblemLogMs;
        private long m_lastLoggedNotified;
        private long m_lastLoggedDenied;
        private string m_lastDeniedType;
        private string m_lastDeniedReason;

        /// <summary>累计补发的 behavior 回调数（counter: clientBehaviorNotify）。</summary>
        public long Notified => m_notified;

        /// <summary>累计被拒绝的 mutation-capable behavior 通知数（counter: clientBehaviorDeniedWrites）。</summary>
        public long DeniedWrites => m_deniedWrites;

        public long Cells => m_cells;
        public long MissingSubsystem => m_missingSubsystem;
        public long MissingBehaviors => m_missingBehaviors;
        public long NotifyFaults => m_notifyFaults;

        /// <summary>
        /// 一格由主机权威值落地后调用（old → new）。本方法**绝不写格 / 不生成实体**：
        /// 只按引擎 ChangeCell 的同一套规则把通知交给纯表现行为。
        /// </summary>
        public void Notify(SubsystemTerrain terrain, int oldValue, int newValue, int x, int y, int z)
        {
            if (terrain == null)
            {
                m_missingSubsystem++;
                MaybeLog();
                return;
            }
            oldValue = Game.Terrain.ReplaceLight(oldValue, 0);
            newValue = Game.Terrain.ReplaceLight(newValue, 0);
            if (oldValue == newValue) return;
            SubsystemBlockBehaviors all = ResolveBehaviors(terrain);
            if (all == null)
            {
                m_missingSubsystem++;
                MaybeLog();
                return;
            }
            int oldContents = Game.Terrain.ExtractContents(oldValue);
            int newContents = Game.Terrain.ExtractContents(newValue);
            BlockBehaviorNotifyKind kind = BlockBehaviorNotifyPolicy.Classify(oldContents, newContents);
            m_cells++;
            if (kind == BlockBehaviorNotifyKind.Modified)
            {
                Dispatch(terrain, all, newContents, BlockBehaviorDispatch.Modified,
                    newValue, oldValue, x, y, z);
            }
            else
            {
                Dispatch(terrain, all, oldContents, BlockBehaviorDispatch.Removed,
                    oldValue, newValue, x, y, z);
                Dispatch(terrain, all, newContents, BlockBehaviorDispatch.Added,
                    newValue, oldValue, x, y, z);
            }
            MaybeLog();
        }

        private SubsystemBlockBehaviors ResolveBehaviors(SubsystemTerrain terrain)
        {
            Project project = terrain.Project;
            if (!ReferenceEquals(project, m_project))
            {
                m_project = project;
                m_behaviors = null;
            }
            if (m_behaviors == null)
                m_behaviors = project?.FindSubsystem<SubsystemBlockBehaviors>(false);
            return m_behaviors;
        }

        private void Dispatch(SubsystemTerrain terrain, SubsystemBlockBehaviors all, int contents,
            BlockBehaviorDispatch dispatch, int value, int oldValue, int x, int y, int z)
        {
            if (Game.BlocksManager.Blocks == null ||
                contents < 0 || contents >= Game.BlocksManager.Blocks.Length)
            {
                m_missingBehaviors++;
                return;
            }
            SubsystemBlockBehavior[] behaviors = all.GetBlockBehaviors(contents);
            if (behaviors == null || behaviors.Length == 0) return;
            m_dispatched.Clear();
            for (int i = 0; i < behaviors.Length; i++)
            {
                SubsystemBlockBehavior behavior = behaviors[i];
                if (behavior == null) continue;
                if (!m_dispatched.Contains(behavior)) m_dispatched.Add(behavior);
            }
            for (int i = 0; i < m_dispatched.Count; i++)
            {
                SubsystemBlockBehavior behavior = m_dispatched[i];
                string typeName = behavior.GetType().Name;
                if (!BlockBehaviorNotifyPolicy.IsPresentationBehavior(typeName))
                {
                    // 客户端 mutation-denied：这些行为可能写格 / 生成实体，不允许通过补通知执行。
                    m_deniedWrites++;
                    m_lastDeniedType = typeName;
                    m_lastDeniedReason = BlockBehaviorNotifyPolicy.ExplainDenied(typeName);
                    continue;
                }
                try
                {
                    if (dispatch == BlockBehaviorDispatch.Modified)
                    {
                        behavior.OnBlockModified(value, oldValue, x, y, z);
                    }
                    else if (dispatch == BlockBehaviorDispatch.Removed)
                    {
                        behavior.OnBlockRemoved(value, oldValue, x, y, z);
                    }
                    else
                    {
                        behavior.OnBlockAdded(value, oldValue, x, y, z);
                    }
                    m_notified++;
                }
                catch (Exception e)
                {
                    m_notifyFaults++;
                    LogProblem("behavior 通知异常（" + typeName + " @ " + x + "," + y + "," + z +
                        "）：" + e.GetType().Name + " " + e.Message +
                        "（累计 " + m_notifyFaults + " 次；本格表现可能缺失，明确失败不静默）");
                }
            }
        }

        // ---------------------------------------------------------------- 诊断

        private void MaybeLog()
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (nowMs - m_lastLogMs < SummaryLogMs) return;
            bool changed = m_notified != m_lastLoggedNotified || m_deniedWrites != m_lastLoggedDenied;
            if (!changed) return;
            m_lastLogMs = nowMs;
            m_lastLoggedNotified = m_notified;
            m_lastLoggedDenied = m_deniedWrites;
            Log.Information("[SuMP] " + Describe() +
                (m_lastDeniedType == null ? "" :
                    " · lastDenied=" + m_lastDeniedType + "（" + m_lastDeniedReason + "）"));
        }

        private void LogProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemLogMs != 0 && nowMs - m_lastProblemLogMs < SummaryLogMs) return;
            m_lastProblemLogMs = nowMs;
            Log.Warning("[SuMP] " + message);
        }

        public string Describe() =>
            "clientBehaviorNotify=" + m_notified +
            " clientBehaviorDeniedWrites=" + m_deniedWrites +
            " cells=" + m_cells +
            " missingSubsystem=" + m_missingSubsystem +
            " missingBehaviors=" + m_missingBehaviors +
            " notifyFaults=" + m_notifyFaults;
    }

    /// <summary>本次派发对应引擎 ChangeCell 的哪一条分支。</summary>
    internal enum BlockBehaviorDispatch
    {
        Modified = 0,
        Removed = 1,
        Added = 2
    }
}

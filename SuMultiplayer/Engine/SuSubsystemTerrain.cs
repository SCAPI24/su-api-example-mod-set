using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎注入点：替换 <c>Game.SubsystemTerrain</c>。
    ///
    /// **刻意保持薄**：这里只做三件事 ——
    ///   ① 每帧先让桥泵一次（派发网络消息、产生区块加载/卸载事件）；
    ///   ② 跑引擎原本的 <c>TerrainUpdater.Update()</c>，然后在
    ///      <c>ProcessModifiedCells()</c> 清空之前把改动格子交给桥（主机才下发）；
    ///   ③ 照常调用 <c>ProcessModifiedCells()</c>，不改引擎原有语义。
    ///
    /// 业务（谁该收、发什么报文、摘要怎么算）全在模块与 <c>Terrain/</c> 服务里，
    /// 不在这里。旧实现把 900 多行网络逻辑塞进这个类，新 mod 不这么做。
    ///
    /// 演示页标记：ENG01。
    /// </summary>
    public class SuSubsystemTerrain : SubsystemTerrain, IUpdateable
    {
        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.m_modifiedCells
        private Dictionary<Point3, bool> m_modifiedCells;
        private bool m_bound;
        private long m_frames;

        // 临时缓冲，避免每帧分配
        private readonly List<Point3> m_capture = new List<Point3>(1024);

        /// <summary>
        /// 主机侧改动捕获的**唯一事实来源**（接住 / 合并 / 去重 / 计数都在这里，纯托管无引擎依赖）。
        /// 每帧 <see cref="IUpdateable.Update"/> 用它把"引擎改动表 ∪ 被流体清表前接住的格子"合并成
        /// 一份下发清单 —— 见 <see cref="RescuePendingCells"/> 里的引擎时序说明。
        /// </summary>
        private readonly HostCellCapture m_captureState = new HostCellCapture();

        /// <summary>捕获计数器（模块 5s 状态行写出来；自检也直接断言它）。</summary>
        public HostCellCapture CaptureState => m_captureState;

        /// <summary>
        /// 依赖**注入**进来（模块每帧找到本实例后赋值），不使用任何静态字段。
        /// 为空时本类完全退回原生行为，等价于没有 mod。
        /// </summary>
        public TerrainBridge Bridge { get; set; }

        /// <summary>
        /// 本端是不是"联机客户端副本"（模块在角色确定后每帧写入）。
        /// **唯一事实来源**：所有会改地形的引擎更新（SuSubsystemFluid*/Fire/Grass/…）都读它，
        /// 通过 <see cref="AllowSimulation"/> 决定本帧是否推进。主机 / 单机恒 false。
        /// </summary>
        public bool ClientReplica { get; set; }

        private readonly ReplicaSimulationGate m_gate = new ReplicaSimulationGate();

        /// <summary>客户端世界模拟闸门（实例状态，挂在注入后的子系统中，**没有静态字段**）。</summary>
        public ReplicaSimulationGate Gate => m_gate;

        /// <summary>引擎侧会改地形的更新在动手之前问这一句；客户端返回 false 并**按来源计数**。</summary>
        /// <summary>
        /// 主机：把一次**绕过 ChangeCell 的直接写格**登记进改动表（下一步由既有桥下发）。
        /// 典型：流体 `UpdateIsTop` 用 <c>chunk.SetCellValueFast</c> 只改顶层 data，不进 m_modifiedCells；
        /// 不登记的话，主机本地的 data 变了而客户端永远收不到 → 摘要（含 data）必然长期不一致。
        /// Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemTerrain.cs:MarkCellModifiedForBroadcast
        /// </summary>
        public void MarkCellModifiedForBroadcast(Point3 cell)
        {
            if (m_modifiedCells == null || cell.Y < 0 || cell.Y > 255) return;
            m_modifiedCells[cell] = true;
        }

        /// <summary>
        /// 主机：在**会把引擎改动表清空的调用之前**接住当前待下发的格子。
        ///
        /// 为什么必须有这一步：引擎 <c>SubsystemFluidBlockBehavior.SpreadFluid</c> 末尾
        /// （Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:312）自己调
        /// <c>SubsystemTerrain.ProcessModifiedCells()</c>，而水 / 岩浆的 UpdateOrder 是
        /// <c>Default = 0</c>、地形是 <c>Terrain = 100</c>；同序对象按 <c>GetHashCode</c> 排序
        /// （Survivalcraft/Game/SubsystemUpdate.cs:26）⇒ 玩家组件（ComponentPlayer / ComponentMiner，
        /// 同为 Default）的挖 / 放在某些进程里排在流体之前，那一帧刚写入 <c>m_modifiedCells</c> 的格子
        /// 就会被流体这次清表抹掉，地形 Update 再也读不到 ⇒ 主机本地改了、客户端收不到。
        /// 接住后由 <see cref="IUpdateable.Update"/> 与表合并、去重、只下发一次。
        ///
        /// 调用点：<c>Engine/ClientReplicaSubsystems.cs</c> 的水 / 岩浆外壳，在 <c>base.Update</c> 之前。
        /// </summary>
        public void RescuePendingCells()
        {
            if (Bridge == null) return;                  // 没有会话：不产生任何捕获工作
            if (!EnsureModifiedCellsBound()) return;
            if (m_modifiedCells.Count == 0) return;
            m_captureState.Rescue(m_modifiedCells.Keys);
        }

        public bool AllowSimulation(ReplicaMutationSource source)
        {
            m_gate.ClientReplica = ClientReplica;
            return m_gate.Allow(source);
        }

        public long Frames => m_frames;
        public bool BridgeBound => m_bound;

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.Update
        void IUpdateable.Update(float dt)
        {
            TerrainBridge bridge = Bridge;
            if (bridge == null)
            {
                // 没有会话：不捕获、不下发（接住集合里也不会有东西 —— RescuePendingCells 直接返回）。
                m_captureState.Clear();
                base.Update(dt);
                return;
            }
            if (!m_bound && !EnsureModifiedCellsBound())
            {
                // 拿不到引擎的改动表就退回原生行为（绝不假装成功）；接住的格子明确计数丢弃。
                m_captureState.Clear();
                base.Update(dt);
                return;
            }
            m_frames++;
            m_gate.ClientReplica = ClientReplica;

            // 派发网络消息**不在这里**：加入房间发生在主菜单（没有 Project，本子系统的 Update
            // 根本不会被调用）。派发统一由模块 tick 驱动（同样是主线程），见 TerrainSyncModule.Tick。

            // 引擎正常推进：流体/电/火/活塞/天气等都会写 m_modifiedCells。
            // 客户端上那些"世界自发演化"已经在各自子系统入口被拦掉（见 ClientReplicaSubsystems.cs）；
            // 走到这里的本地写入只可能是**玩家编辑**（挖掘/放置）—— 由桥捕获后发给主机审批。
            TerrainUpdater.Update();

            // ③ 在 ProcessModifiedCells 清空之前，把**全部**待下发格子交给桥：
            //    · 引擎改动表（ChangeCell / DestroyCell / 行为登记）；
            //    · 被流体 SpreadFluid 内部那次清表接住的格子（RescuePendingCells）。
            //    两堆合并去重：同一格只下发一次，值以"读表这一刻"为准（同帧后续改写不会丢）。
            //    主机 = 下发；客户端 = 捕获成 CellEditRequest（见 Part B）。
            m_capture.Clear();
            int duplicates = m_captureState.Collect(m_modifiedCells.Keys, m_capture);
            if (duplicates > 0) bridge.NoteHostCaptureDuplicates(duplicates);
            if (m_capture.Count > 0)
            {
                if (ClientReplica)
                    bridge.CaptureClientEdits(m_capture);
                else
                    bridge.PublishHostCells(m_capture);
            }

            // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ProcessModifiedCells
            ProcessModifiedCells();
        }

        private bool EnsureModifiedCellsBound()
        {
            if (m_modifiedCells != null) return true;
            try
            {
                // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.m_modifiedCells
                m_modifiedCells = Game.Program.ModManager.ModParentField
                    .GetParentField<Dictionary<Point3, bool>>(this, "m_modifiedCells",
                        typeof(SubsystemTerrain));
                m_bound = m_modifiedCells != null;
            }
            catch (Exception)
            {
                m_modifiedCells = null;
                m_bound = false;
            }
            return m_modifiedCells != null;
        }
    }
}

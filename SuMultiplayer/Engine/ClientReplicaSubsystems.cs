using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using TemplatesDatabase;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎侧"客户端 = 副本"闸门（**新文件，尽量不改既有类**）。
    ///
    /// 架构决定：**主机是唯一权威，客户端是副本**。客户端上任何会改地形的"世界自发演化"都不许跑：
    /// 流体 / 岩浆 / 火 / 冰·雪（天气冻结沉积）/ 草蔓延 / 植物 / 落叶季相 / 仙人掌 / 腐坏 /
    /// 地毯 / 落叶 / 耕地干湿 / 常春藤 / 树苗 / 爆炸。改地形只有一个来源：主机下发的
    /// <c>CellChange</c> / <c>ChunkResync</c>，以及主机批准后的玩家编辑（同样经 CellChange 到达）。
    ///
    /// 挂钩方式（与旧实现一致）：
    ///   · 子系统整型替换：`modInjector.Register("Game.SubsystemX", "SuMultiplayer.Engine.SuSubsystemX")`
    ///     后由 <c>modInjector.Apply(database, modName)</c> 改 Database 里的 <c>Class</c> 参数；
    ///   · 引擎的 `Update` / `OnPoll` / `OnNeighborBlockChanged` 不是虚方法时用**显式接口实现**
    ///     （引擎按 IUpdateable 派发）；是虚方法时直接 override。
    ///
    /// 每个闸门都走 <see cref="SuSubsystemTerrain.AllowSimulation"/>：主机 / 单机恒 true，
    /// 客户端恒 false 并**按来源计数**（<see cref="ReplicaSimulationGate"/>，一句日志可审计）。
    /// </summary>
    internal static class ReplicaGates
    {
        internal static bool IsClient(SubsystemTerrain terrain) =>
            terrain is SuSubsystemTerrain shell && shell.ClientReplica;

        internal static bool Allow(SubsystemTerrain terrain, ReplicaMutationSource source) =>
            !(terrain is SuSubsystemTerrain shell) || shell.AllowSimulation(source);

        /// <summary>客户端把某个引擎私有工作表清空，避免"只登记不消费"越积越多。</summary>
        internal static void ClearTable(SubsystemTerrain terrain, object owner, string fieldName,
            Type declaringType)
        {
            if (!IsClient(terrain) || owner == null) return;
            try
            {
                object table = Game.Program.ModManager?.ModParentField?
                    .GetParentField<object>(owner, fieldName, declaringType);
                if (table is IDictionary dictionary) dictionary.Clear();
                else if (table is IList list) list.Clear();
            }
            catch (Exception)
            {
                // 字段不存在 = 引擎版本差异：Loud 一次由调用方计数器体现；不静默假装清了。
            }
        }

        /// <summary>
        /// 主机：跑一次"会直接写格"的引擎回调，并把**真的变了**的格子登记进下发批次。
        /// 客户端不调用（那些钩子已经在外面被闸门挡住）。
        ///
        /// <paramref name="cellAlreadyChanged"/>：ChangeCell / DestroyCell 的行为回调
        /// （OnBlockAdded / OnBlockModified / OnBlockRemoved）是在**写入之后**才被调用的 ——
        /// 这一格本身一定变了；before/after 是为了抓 base 回调里 <c>UpdateIsTop</c> 的 data 直写
        /// （它绕过 ChangeCell，只改顶层标志位，不进引擎改动表）。
        /// </summary>
        internal static void RunAndCaptureCell(SubsystemTerrain terrain, Point3 cell, Action action,
            bool cellAlreadyChanged, bool dataOnly)
        {
            if (action == null) return;
            SuSubsystemTerrain shell = terrain as SuSubsystemTerrain;
            if (terrain?.Terrain == null)
            {
                action();
                return;
            }
            int before = ReadCellValue(terrain, cell);
            if (cellAlreadyChanged) shell?.NoteFluidCellChanged(cell, dataOnly);
            action();
            int after = ReadCellValue(terrain, cell);
            if (before != after) shell?.NoteFluidCandidate(cell, before, after);
        }

        /// <summary>一次比较多个候选格（邻居回调可能同时改 (x,y,z) 与 (x,y+1,z)，岩浆还会改 3×3×3）。</summary>
        internal static void RunAndCaptureCells(SubsystemTerrain terrain, Point3[] cells, Action action)
        {
            if (action == null) return;
            if (terrain?.Terrain == null || cells == null || cells.Length == 0)
            {
                action();
                return;
            }
            SuSubsystemTerrain shell = terrain as SuSubsystemTerrain;
            var before = new int[cells.Length];
            for (int i = 0; i < cells.Length; i++) before[i] = ReadCellValue(terrain, cells[i]);
            action();
            for (int i = 0; i < cells.Length; i++)
            {
                int after = ReadCellValue(terrain, cells[i]);
                if (before[i] == after) continue;
                shell?.NoteFluidCandidate(cells[i], before[i], after);
            }
        }

        internal static int ReadCellValue(SubsystemTerrain terrain, Point3 cell)
        {
            if (terrain?.Terrain == null || cell.Y < 0 || cell.Y > 255) return 0;
            return terrain.Terrain.GetCellValueFast(cell.X, cell.Y, cell.Z);
        }

        /// <summary>回调前后读同一格：真的变了才交给流体捕获窗口（无变化不产生任何下发）。</summary>
        internal static void NoteCandidateIfChanged(SubsystemTerrain terrain, Point3 cell, int before)
        {
            if (terrain?.Terrain == null || cell.Y < 0 || cell.Y > 255) return;
            int after = ReadCellValue(terrain, cell);
            if (before == after) return;
            (terrain as SuSubsystemTerrain)?.NoteFluidCandidate(cell, before, after);
        }

        internal static int[] ReadCellValues(SubsystemTerrain terrain, Point3[] cells)
        {
            if (terrain?.Terrain == null || cells == null) return Array.Empty<int>();
            var values = new int[cells.Length];
            for (int i = 0; i < cells.Length; i++) values[i] = ReadCellValue(terrain, cells[i]);
            return values;
        }

        /// <summary>
        /// 主机：比较一次模拟前后的候选格，把**真的变了**的格登记进下发批次。
        /// 流体 / 岩浆的写入点（<c>m_toSet</c> → ChangeCell / DestroyCell，以及 UpdateIsTop 的直写）
        /// 都在 base.Update 内部，而且 base 自己会 ProcessModifiedCells 清空改动表 ——
        /// 不在这里补登记，主机的流体变化永远不会下发给客户端（摘要含 data 会长期不一致）。
        /// 注意：候选快照只是**补漏网**；SpreadFluid 第 1 遍的新前沿不在快照里，靠行为回调窗口
        /// （<see cref="SuSubsystemTerrain.BeginFluidCapture"/>）捕获，二者在窗口里合并去重。
        /// </summary>
        internal static int RegisterChangedCells(SubsystemTerrain terrain, Point3[] cells, int[] before)
        {
            if (terrain?.Terrain == null || cells == null || before == null) return 0;
            int registered = 0;
            SuSubsystemTerrain shell = terrain as SuSubsystemTerrain;
            for (int i = 0; i < cells.Length && i < before.Length; i++)
            {
                Point3 cell = cells[i];
                if (cell.Y < 0 || cell.Y > 255) continue;
                int after = ReadCellValue(terrain, cell);
                if (before[i] == after) continue;
                shell?.NoteFluidCandidate(cell, before[i], after);
                registered++;
            }
            return registered;
        }

        internal static object GetField(object owner, string fieldName, Type declaringType)
        {
            try
            {
                return Game.Program.ModManager?.ModParentField?
                    .GetParentField<object>(owner, fieldName, declaringType);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // =====================================================================================
    // 流体：水 / 岩浆。引擎 Update 里 SpreadFluid() 直接 SetCellValueFast（不进改动表），
    // 所以必须**整段不跑**；OnBlockAdded / OnNeighborBlockChanged 里还有顶层 data 的直写。
    // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:SubsystemFluidBlockBehavior.Update/SpreadFluid
    // =====================================================================================

    public sealed class SuSubsystemWaterBlockBehavior : SubsystemWaterBlockBehavior, IUpdateable
    {
        void IUpdateable.Update(float dt)
        {
            // Source: Survivalcraft/Game/SubsystemWaterBlockBehavior.cs:SubsystemWaterBlockBehavior.Update
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fluid))
            {
                ClearWorkTables();
                return;
            }
            // 主机：流体写入点在 base.Update 内部（且 base 自己会在**每一遍**末尾 ProcessModifiedCells 清表）。
            // ① 前置候选快照（m_toUpdate 键 + 水平邻居 + 上下格，沿用有界 64 键）补漏；
            // ② 行为回调把**本次 Update 内所有真实写入**收进捕获窗口 —— 包括 SpreadFluid 第 1 遍
            //    才排进来的新前沿（不在 ① 的快照里）；窗口在 finally 里并回引擎改动表，内部清表抹不掉。
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            bool capturing = shell != null && shell.BeginFluidCapture();
            try
            {
                Point3[] candidates = SnapshotCandidates(64);
                int[] before = ReplicaGates.ReadCellValues(SubsystemTerrain, candidates);
                // 引擎 SpreadFluid 末尾会**自己**调 ProcessModifiedCells 清空改动表（见本文件顶部与
                // SuSubsystemTerrain.RescuePendingCells 的时序说明）。先把"本次流体 Update 之前就已待下发"
                // 的格子接住（玩家挖/放、其它 Default 序行为），否则它们会被这次清表抹掉、永远不会广播
                // —— 实机 desync 根因（某一格在主机日志里没有"下发改动"）。
                shell?.RescuePendingCells();
                base.Update(dt);
                ReplicaGates.RegisterChangedCells(SubsystemTerrain, candidates, before);
            }
            finally
            {
                if (capturing) shell?.EndFluidCapture();
            }
        }

        /// <summary>
        /// 本帧可能被流体写入的格：工作表 m_toUpdate 的键 + 四个水平邻居 + 上下格（有界 64 键）。
        /// 注意这只是**补漏**：第 0 遍末尾 ProcessModifiedCells 才排进 m_toUpdate 的新前沿不在快照里，
        /// 由行为回调窗口捕获（见 Update 注释）。
        /// Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:SubsystemFluidBlockBehavior.m_toUpdate/m_toSet
        /// </summary>
        private Point3[] SnapshotCandidates(int maxKeys)
        {
            object table = ReplicaGates.GetField(this, "m_toUpdate", typeof(SubsystemFluidBlockBehavior));
            if (table is not IDictionary dictionary || dictionary.Count == 0) return Array.Empty<Point3>();
            if (dictionary.Count > maxKeys)
                (SubsystemTerrain as SuSubsystemTerrain)?.NoteFluidCandidateCapHit(dictionary.Count - maxKeys);
            var cells = new List<Point3>(maxKeys * 6);
            int keys = 0;
            foreach (object key in dictionary.Keys)
            {
                if (key is not Point3 point) continue;
                if (++keys > maxKeys) break;
                cells.Add(point);
                cells.Add(new Point3(point.X - 1, point.Y, point.Z));
                cells.Add(new Point3(point.X + 1, point.Y, point.Z));
                cells.Add(new Point3(point.X, point.Y, point.Z - 1));
                cells.Add(new Point3(point.X, point.Y, point.Z + 1));
                if (point.Y > 0) cells.Add(new Point3(point.X, point.Y - 1, point.Z));
                if (point.Y < 255) cells.Add(new Point3(point.X, point.Y + 1, point.Z));
            }
            return cells.ToArray();
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fluid)) return;
            // ChangeCell 已写入 → 这一格本身一定变了（内容变化）；before/after 抓 base 里的 UpdateIsTop data 直写。
            ReplicaGates.RunAndCaptureCell(SubsystemTerrain, new Point3(x, y, z),
                delegate { base.OnBlockAdded(value, oldValue, x, y, z); },
                cellAlreadyChanged: true, dataOnly: false);
        }

        public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fluid)) return;
            // ChangeCell 只在"内容不变、值确实变了"时走这一支 ⇒ 这一格是 data-only 变化。
            ReplicaGates.RunAndCaptureCell(SubsystemTerrain, new Point3(x, y, z),
                delegate { base.OnBlockModified(value, oldValue, x, y, z); },
                cellAlreadyChanged: true, dataOnly: true);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fluid)) return;
            // 水/岩浆被替换掉（DestroyCell → ChangeCell 已写入）⇒ 内容变化，必须登记；
            // 否则 SpreadFluid 内部清表后这一格永远不下发（客户端还留着旧水）。
            ReplicaGates.RunAndCaptureCell(SubsystemTerrain, new Point3(x, y, z),
                delegate { base.OnBlockRemoved(value, newValue, x, y, z); },
                cellAlreadyChanged: true, dataOnly: false);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fluid)) return;
            // 邻居在上方时引擎会重算"顶层" data（UpdateIsTop 直写 (x,y,z)）。回调前后各读一次，
            // 只有真的变了才登记（无变化不产生下发）。
            var cell = new Point3(x, y, z);
            var above = new Point3(x, Math.Min(255, y + 1), z);
            int beforeCell = ReplicaGates.ReadCellValue(SubsystemTerrain, cell);
            int beforeAbove = ReplicaGates.ReadCellValue(SubsystemTerrain, above);
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            ReplicaGates.NoteCandidateIfChanged(SubsystemTerrain, cell, beforeCell);
            ReplicaGates.NoteCandidateIfChanged(SubsystemTerrain, above, beforeAbove);
        }

        public override void OnChunkDiscarding(TerrainChunk chunk)
        {
            base.OnChunkDiscarding(chunk);
            ClearWorkTables();
        }

        private void ClearWorkTables()
        {
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:SubsystemFluidBlockBehavior.m_toUpdate/m_toSet/m_visited
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toUpdate", typeof(SubsystemFluidBlockBehavior));
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toSet", typeof(SubsystemFluidBlockBehavior));
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_visited", typeof(SubsystemFluidBlockBehavior));
        }
    }

    public sealed class SuSubsystemMagmaBlockBehavior : SubsystemMagmaBlockBehavior, IUpdateable
    {
        void IUpdateable.Update(float dt)
        {
            // Source: Survivalcraft/Game/SubsystemMagmaBlockBehavior.cs:SubsystemMagmaBlockBehavior.Update
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Magma))
            {
                ClearWorkTables();
                return;
            }
            // 与水完全相同的两段捕获：前置候选快照补漏 + 行为回调窗口（覆盖 SpreadFluid 第 1 遍的新前沿）。
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            bool capturing = shell != null && shell.BeginFluidCapture();
            try
            {
                Point3[] candidates = SnapshotCandidates(64);
                int[] before = ReplicaGates.ReadCellValues(SubsystemTerrain, candidates);
                // 引擎 SpreadFluid 末尾会**自己**调 ProcessModifiedCells 清空改动表（见本文件顶部与
                // SuSubsystemTerrain.RescuePendingCells 的时序说明）。先把"本次流体 Update 之前就已待下发"
                // 的格子接住（玩家挖/放、其它 Default 序行为），否则它们会被这次清表抹掉、永远不会广播
                // —— 实机 desync 根因（某一格在主机日志里没有"下发改动"）。
                shell?.RescuePendingCells();
                base.Update(dt);
                ReplicaGates.RegisterChangedCells(SubsystemTerrain, candidates, before);
            }
            finally
            {
                if (capturing) shell?.EndFluidCapture();
            }
        }

        private Point3[] SnapshotCandidates(int maxKeys)
        {
            object table = ReplicaGates.GetField(this, "m_toUpdate", typeof(SubsystemFluidBlockBehavior));
            if (table is not IDictionary dictionary || dictionary.Count == 0) return Array.Empty<Point3>();
            if (dictionary.Count > maxKeys)
                (SubsystemTerrain as SuSubsystemTerrain)?.NoteFluidCandidateCapHit(dictionary.Count - maxKeys);
            var cells = new List<Point3>(maxKeys * 6);
            int keys = 0;
            foreach (object key in dictionary.Keys)
            {
                if (key is not Point3 point) continue;
                if (++keys > maxKeys) break;
                cells.Add(point);
                cells.Add(new Point3(point.X - 1, point.Y, point.Z));
                cells.Add(new Point3(point.X + 1, point.Y, point.Z));
                cells.Add(new Point3(point.X, point.Y, point.Z - 1));
                cells.Add(new Point3(point.X, point.Y, point.Z + 1));
                if (point.Y > 0) cells.Add(new Point3(point.X, point.Y - 1, point.Z));
                if (point.Y < 255) cells.Add(new Point3(point.X, point.Y + 1, point.Z));
            }
            return cells.ToArray();
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Magma)) return;
            // ChangeCell 已写入 → 这一格本身是内容变化；before/after 抓 base 里的 UpdateIsTop data 直写。
            ReplicaGates.RunAndCaptureCell(SubsystemTerrain, new Point3(x, y, z),
                delegate { base.OnBlockAdded(value, oldValue, x, y, z); },
                cellAlreadyChanged: true, dataOnly: false);
        }

        public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Magma)) return;
            ReplicaGates.RunAndCaptureCell(SubsystemTerrain, new Point3(x, y, z),
                delegate { base.OnBlockModified(value, oldValue, x, y, z); },
                cellAlreadyChanged: true, dataOnly: true);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Magma)) return;
            ReplicaGates.RunAndCaptureCell(SubsystemTerrain, new Point3(x, y, z),
                delegate { base.OnBlockRemoved(value, newValue, x, y, z); },
                cellAlreadyChanged: true, dataOnly: false);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Magma)) return;
            var cell = new Point3(x, y, z);
            var above = new Point3(x, Math.Min(255, y + 1), z);
            int beforeCell = ReplicaGates.ReadCellValue(SubsystemTerrain, cell);
            int beforeAbove = ReplicaGates.ReadCellValue(SubsystemTerrain, above);
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            ReplicaGates.NoteCandidateIfChanged(SubsystemTerrain, cell, beforeCell);
            ReplicaGates.NoteCandidateIfChanged(SubsystemTerrain, above, beforeAbove);
        }

        public override void OnChunkDiscarding(TerrainChunk chunk)
        {
            base.OnChunkDiscarding(chunk);
            ClearWorkTables();
        }

        private void ClearWorkTables()
        {
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toUpdate", typeof(SubsystemFluidBlockBehavior));
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toSet", typeof(SubsystemFluidBlockBehavior));
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_visited", typeof(SubsystemFluidBlockBehavior));
        }
    }

    // =====================================================================================
    // 火：客户端**完全不维护火表**。引擎 Update 会随机烧尽 / 蔓延，OnNeighborBlockChanged 还会
    // 直接 ChangeCell —— 两条都必须停。
    // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:SubsystemFireBlockBehavior.Update/OnNeighborBlockChanged
    // =====================================================================================

    public sealed class SuSubsystemFireBlockBehavior : SubsystemFireBlockBehavior, IUpdateable
    {
        private double m_nextClearTime;

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire))
            {
                ClearFireTable();
                return;
            }
            base.Update(dt);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            // 引擎这一支最后会 ChangeCell（把失去支撑的火格改回空气 / 更新连接位）。
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire)) return;
            base.OnBlockAdded(value, oldValue, x, y, z);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire)) return;
            base.OnBlockRemoved(value, newValue, x, y, z);
        }

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire)) return;
            base.OnBlockGenerated(value, x, y, z, isLoaded);
        }

        private void ClearFireTable()
        {
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            double now = Time.RealTime;
            if (now < m_nextClearTime) return;
            m_nextClearTime = now + 1.0;
            // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:SubsystemFireBlockBehavior.m_fireData
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_fireData", typeof(SubsystemFireBlockBehavior));
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toBurnAway", typeof(SubsystemFireBlockBehavior));
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toExpand", typeof(SubsystemFireBlockBehavior));
        }
    }

    // =====================================================================================
    // 草 / 植物 / 落叶季相：OnPoll / OnBlockGenerated / OnNeighborBlockChanged 都会 ChangeCell 或
    // 直接写格；客户端一律不推进。
    // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs
    //         Survivalcraft/Game/SubsystemPlantBlockBehavior.cs
    //         Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs
    // =====================================================================================

    public sealed class SuSubsystemGrassBlockBehavior : SubsystemGrassBlockBehavior, IUpdateable
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Grass)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Grass)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        public override void OnExplosion(int value, int x, int y, int z, float damage)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Grass)) return;
            base.OnExplosion(value, x, y, z, damage);
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Grass))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toUpdate",
                    typeof(SubsystemGrassBlockBehavior));
                return;
            }
            base.Update(dt);
        }
    }

    public sealed class SuSubsystemPlantBlockBehavior : SubsystemPlantBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Plant)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Plant)) return;
            base.OnBlockGenerated(value, x, y, z, isLoaded);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Plant)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }
    }

    public sealed class SuSubsystemDeciduousLeavesBlockBehavior :
        SubsystemDeciduousLeavesBlockBehavior
    {
        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            // 引擎这一支会 SetCellValueFast 改季相 data（不进改动表，客户端会永久分歧）。
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.DeciduousLeaves)) return;
            base.OnBlockGenerated(value, x, y, z, isLoaded);
        }

        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.DeciduousLeaves)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }

        // 引擎的 IUpdateable.Update 只推进落叶粒子（不改地形），客户端照常保留。
        // 会改地形的 OnPoll / OnBlockGenerated 已经在上面拦掉了。
    }

    // =====================================================================================
    // 世界演化（旧实现 SuWorldEvolutionBehaviors.cs 的同一份清单）：
    // 仙人掌 / 腐坏 / 地毯 / 落叶 / 耕地 / 常春藤 / 树苗。
    // 为什么必须停：客户端自己长出来的方块主机从来没有过，区块校验是**单向补漏**，
    // 这种"多余格子"永远纠不回来（旧实现实测：只有一端长出的仙人掌，挖掉时被主机真拒绝）。
    // Source: Mod/ScMultiplayer/Func/Subsystem/SuWorldEvolutionBehaviors.cs
    // =====================================================================================

    public sealed class SuSubsystemCactusBlockBehavior : SubsystemCactusBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Cactus)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Cactus)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }
    }

    public sealed class SuSubsystemRotBlockBehavior : SubsystemRotBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Rot)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }
    }

    public sealed class SuSubsystemCarpetBlockBehavior : SubsystemCarpetBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Carpet)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Carpet)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }
    }

    public sealed class SuSubsystemFallenLeavesBlockBehavior : SubsystemFallenLeavesBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.FallenLeaves)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.FallenLeaves)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }
    }

    public sealed class SuSubsystemSoilBlockBehavior : SubsystemSoilBlockBehavior, IUpdateable
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Soil)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Soil)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Soil))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toDegrade",
                    typeof(SubsystemSoilBlockBehavior));
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toHydrate",
                    typeof(SubsystemSoilBlockBehavior));
                return;
            }
            base.Update(dt);
        }
    }

    public sealed class SuSubsystemIvyBlockBehavior : SubsystemIvyBlockBehavior, IUpdateable
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Ivy)) return;
            base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Ivy)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Ivy))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toUpdate",
                    typeof(SubsystemIvyBlockBehavior));
                return;
            }
            base.Update(dt);
        }
    }

    public sealed class SuSubsystemSaplingBlockBehavior : SubsystemSaplingBlockBehavior, IUpdateable
    {
        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Sapling)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Sapling)) return;
            base.OnBlockAdded(value, oldValue, x, y, z);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Sapling)) return;
            base.OnBlockRemoved(value, newValue, x, y, z);
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Sapling))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_saplings",
                    typeof(SubsystemSaplingBlockBehavior));
                return;
            }
            base.Update(dt);
        }
    }

    // =====================================================================================
    // 电路：客户端不跑电路求解（活塞 / 分配器 / 门 / LED 的落地都由主机算，结果经 CellChange 下发）。
    // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
    // =====================================================================================

    public sealed class SuSubsystemElectricity : SubsystemElectricity, IUpdateable
    {
        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Electricity)) return;
            base.Update(dt);
        }
    }

    // =====================================================================================
    // 爆炸：主机算、客户端只看结果（客户端本地算爆炸会改地形 + 掉物品 + 伤血）。
    // Source: Survivalcraft/Game/SubsystemExplosions.cs:SubsystemExplosions.Update
    // =====================================================================================

    public sealed class SuSubsystemExplosions : SubsystemExplosions, IUpdateable
    {
        void IUpdateable.Update(float dt)
        {
            SubsystemTerrain terrain = base.Project.FindSubsystem<SubsystemTerrain>(false);
            if (!ReplicaGates.Allow(terrain, ReplicaMutationSource.Explosion))
            {
                ReplicaGates.ClearTable(terrain, this, "m_queuedExplosions",
                    typeof(SubsystemExplosions));
                return;
            }
            base.Update(dt);
        }
    }

    // =====================================================================================
    // 天气：客户端保留表现（雨/雪粒子、雾、闪电视觉），但**不推进任何模拟**：
    //   · 不调 base.Update（不随机选降水/雾起止、不老化闪电）；
    //   · 摘掉 base.Load 注册的两个"持久改地形"回调（FreezeThawAndDepositSnow 会 ChangeCell）。
    // 状态由主机 2 Hz 的 WorldInfo 覆盖（见 Engine/ClientReplicaEngine.cs）。
    // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.Load（两个回调）
    //         Mod/ScMultiplayer/Func/Subsystem/SuSubsystemWeather.cs:DisableClientPersistentTerrainHandlers
    // =====================================================================================

    public sealed class SuSubsystemWeather : SubsystemWeather, IUpdateable
    {
        private bool m_clientPersistentTerrainHandlersDisabled;

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Weather))
            {
                DisableClientPersistentTerrainHandlers();
                // 客户端天气状态由 WorldInfo 覆盖；这里绝不 base.Update（否则就是本地模拟）。
                return;
            }
            base.Update(dt);
        }

        /// <summary>
        /// 摘掉 <c>SubsystemWeather.Load</c> 注册的"扫描完一块 / 块初始化完成"回调：
        /// 它们会调用 FreezeThawAndDepositSnow（ChangeCell）。两个回调都必须被移除，
        /// 否则客户端仍会自己改地形（雨雪冻结 / 积雪）。
        /// 返回 true = 两个回调都已确认移除。
        /// </summary>
        internal bool DisableClientPersistentTerrainHandlers()
        {
            if (m_clientPersistentTerrainHandlersDisabled) return true;
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return false;
            object scanner = ReplicaGates.GetField(this, "m_subsystemBlocksScanner", typeof(SubsystemWeather));
            bool scannerRemoved = RemoveHandlers(scanner, "ScanningChunkCompleted");
            bool chunkInitRemoved = RemoveHandlers(SubsystemTerrain?.TerrainUpdater, "ChunkInitialized");
            m_clientPersistentTerrainHandlersDisabled = scannerRemoved && chunkInitRemoved;
            return m_clientPersistentTerrainHandlersDisabled;
        }

        private bool RemoveHandlers(object owner, string fieldName)
        {
            if (owner == null) return false;
            FieldInfo field = owner.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return false;
            if (field.GetValue(owner) is not Delegate handlers) return true;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                if (!IsWeatherPersistentHandler(handler)) continue;
                field.SetValue(owner, Delegate.Remove(field.GetValue(owner) as Delegate, handler));
            }
            if (field.GetValue(owner) is not Delegate remaining) return true;
            foreach (Delegate handler in remaining.GetInvocationList())
                if (IsWeatherPersistentHandler(handler)) return false;
            return true;
        }

        private bool IsWeatherPersistentHandler(Delegate handler)
        {
            Type declaring = handler?.Method?.DeclaringType;
            return ReferenceEquals(handler?.Target, this) ||
                declaring == typeof(SubsystemWeather) ||
                declaring?.FullName?.StartsWith(
                    typeof(SubsystemWeather).FullName + "+", StringComparison.Ordinal) == true;
        }

        public bool ClientHandlersDisabled => m_clientPersistentTerrainHandlersDisabled;
    }
}

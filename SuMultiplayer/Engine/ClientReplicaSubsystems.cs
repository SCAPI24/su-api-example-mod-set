using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using TemplatesDatabase;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
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

        // ---------------------------------------------------------------- 火（主机侧捕获）
        //
        // 火的写格点与流体一样在行为内部：ChangeCell 的 OnBlockAdded / OnBlockModified /
        // OnBlockRemoved 是在**写入之后**才调用的，回调前后读同一格抓不到那一次写；
        // 所以"确实变了"的格直接登记（cellAlreadyChanged），before/after 只用来抓回调里额外的改写。

        /// <summary>
        /// 主机：跑一次火的写格回调并把**确定发生**的写入登记进火捕获。
        /// <paramref name="cellAlreadyChanged"/>：ChangeCell 的行为回调在写入之后才调用 —— 这一格一定变了；
        /// before/after 只用于发现 base 回调里的额外改写（不进改动表的直写）。
        /// </summary>
        internal static void RunAndCaptureFireCell(SubsystemTerrain terrain, Point3 cell, Action action,
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
            if (cellAlreadyChanged) shell?.NoteFireCellChanged(cell, dataOnly);
            action();
            int after = ReadCellValue(terrain, cell);
            if (before != after) shell?.NoteFireCandidate(cell, before, after);
        }

        /// <summary>火邻居回调前后读同一格：真的变了才登记（无变化明确计入 skipped，不产生下发）。</summary>
        internal static void NoteFireCandidateIfChanged(SubsystemTerrain terrain, Point3 cell, int before)
        {
            if (terrain?.Terrain == null || cell.Y < 0 || cell.Y > 255) return;
            int after = ReadCellValue(terrain, cell);
            (terrain as SuSubsystemTerrain)?.NoteFireCandidate(cell, before, after);
        }

        /// <summary>
        /// 主机：比较一次火 Update 前后的候选格，把**真的变了**的格登记进火捕获。
        /// 候选快照只补漏网；引擎改动表被流体 ProcessModifiedCells 清掉的那些格靠这里 + 行为回调。
        /// </summary>
        internal static int RegisterFireChangedCells(SubsystemTerrain terrain, Point3[] cells, int[] before)
        {
            if (terrain?.Terrain == null || cells == null || before == null) return 0;
            int registered = 0;
            SuSubsystemTerrain shell = terrain as SuSubsystemTerrain;
            for (int i = 0; i < cells.Length && i < before.Length; i++)
            {
                Point3 cell = cells[i];
                if (cell.Y < 0 || cell.Y > 255) continue;
                int after = ReadCellValue(terrain, cell);
                shell?.NoteFireCandidate(cell, before[i], after);
                if (before[i] != after) registered++;
            }
            return registered;
        }

        /// <summary>
        /// 主机：比较一次引信 Update 前后的候选格，把**真的变了**的格登记进引信捕获。
        /// 引信 tick 的写入点在外壳里（ChangeCell 只写 data），候选复核负责把"确认写入"之外的
        /// 真实差异也收进窗口；候选没变则计入 fuseCellSkipped（不静默、也不制造下发）。
        /// </summary>
        internal static int RegisterFuseChangedCells(SubsystemTerrain terrain, Point3[] cells, int[] before)
        {
            if (terrain?.Terrain == null || cells == null || before == null) return 0;
            int registered = 0;
            SuSubsystemTerrain shell = terrain as SuSubsystemTerrain;
            for (int i = 0; i < cells.Length && i < before.Length; i++)
            {
                Point3 cell = cells[i];
                if (cell.Y < 0 || cell.Y > 255) continue;
                int after = ReadCellValue(terrain, cell);
                shell?.NoteFuseCandidate(cell, before[i], after);
                if (before[i] != after) registered++;
            }
            return registered;
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
    // 水族"只清表"子系统：InWater / WaterPlant / BottomSucker 都继承 SubsystemWaterBlockBehavior
    // 的 Update → SpreadFluid → **每 0.25 game-sec 两次 ProcessModifiedCells()**（清空引擎改动表）。
    // 它们 HandledBlocks 为空，所以除了清表几乎什么都不做 —— 但**不注入**时：
    //   · 客户端：vanilla Update 照跑，玩家挖/放的格子若落在"挖 → 清表 → 地形捕获"之间就永久丢失
    //     （实机丢编辑根因：客户端 submitted 不涨、captureIgnored=0、主机 requests 不涨）；
    //   · 主机：清表同样会抹掉尚未下发的玩家编辑（只有水/岩浆外壳做了 Rescue）。
    // 所以三个都注入：客户端整段不跑并清工作表；主机 base.Update 之前 Rescue + 流体捕获窗口。
    // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:SpreadFluid → ProcessModifiedCells (:312)
    // Source: Survivalcraft/Game/SubsystemWaterBlockBehavior.cs:Update (:20-30)
    // =====================================================================================

    /// <summary>水族子系统外壳的公共实现（三条子类只差 base.Update 的落点）。</summary>
    internal static class WaterFamilyShell
    {
        internal static void Update(SubsystemFluidBlockBehavior owner, SubsystemTerrain terrain, float dt,
            Action baseUpdate)
        {
            if (!ReplicaGates.Allow(terrain, ReplicaMutationSource.Fluid))
            {
                ClearWorkTables(terrain, owner);
                return;
            }
            SuSubsystemTerrain shell = terrain as SuSubsystemTerrain;
            bool capturing = shell != null && shell.BeginFluidCapture();
            try
            {
                Point3[] candidates = SnapshotCandidates(owner, terrain, 64);
                int[] before = ReplicaGates.ReadCellValues(terrain, candidates);
                // base.Update 内部会 ProcessModifiedCells 清表：先把本帧已待下发的玩家编辑接住。
                shell?.RescuePendingCells();
                baseUpdate();
                ReplicaGates.RegisterChangedCells(terrain, candidates, before);
            }
            finally
            {
                if (capturing) shell?.EndFluidCapture();
            }
        }

        private static Point3[] SnapshotCandidates(SubsystemFluidBlockBehavior owner,
            SubsystemTerrain terrain, int maxKeys)
        {
            object table = ReplicaGates.GetField(owner, "m_toUpdate", typeof(SubsystemFluidBlockBehavior));
            if (table is not IDictionary dictionary || dictionary.Count == 0) return Array.Empty<Point3>();
            if (dictionary.Count > maxKeys)
                (terrain as SuSubsystemTerrain)?.NoteFluidCandidateCapHit(dictionary.Count - maxKeys);
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

        private static void ClearWorkTables(SubsystemTerrain terrain, SubsystemFluidBlockBehavior owner)
        {
            if (!ReplicaGates.IsClient(terrain)) return;
            ReplicaGates.ClearTable(terrain, owner, "m_toUpdate", typeof(SubsystemFluidBlockBehavior));
            ReplicaGates.ClearTable(terrain, owner, "m_toSet", typeof(SubsystemFluidBlockBehavior));
            ReplicaGates.ClearTable(terrain, owner, "m_visited", typeof(SubsystemFluidBlockBehavior));
        }
    }

    public sealed class SuSubsystemInWaterBlockBehavior : SubsystemInWaterBlockBehavior, IUpdateable
    {
        void IUpdateable.Update(float dt) =>
            WaterFamilyShell.Update(this, SubsystemTerrain, dt, delegate { base.Update(dt); });
    }

    public sealed class SuSubsystemWaterPlantBlockBehavior : SubsystemWaterPlantBlockBehavior, IUpdateable
    {
        void IUpdateable.Update(float dt) =>
            WaterFamilyShell.Update(this, SubsystemTerrain, dt, delegate { base.Update(dt); });
    }

    public sealed class SuSubsystemBottomSuckerBlockBehavior : SubsystemBottomSuckerBlockBehavior, IUpdateable
    {
        void IUpdateable.Update(float dt) =>
            WaterFamilyShell.Update(this, SubsystemTerrain, dt, delegate { base.Update(dt); });
    }

    // =====================================================================================
    // 火：客户端**完全不维护火表**。引擎 Update 会随机烧尽 / 蔓延，OnNeighborBlockChanged 还会
    // 直接 ChangeCell —— 两条都必须停。
    //
    // 主机：火的写格点全在 base.Update（:231 烧尽目标块 / :237 邻居点火 / :254 蔓延）与
    // OnNeighborBlockChanged（:305-310 把失去支撑的火格改成空气）里；而引擎的流体子系统每 0.25s
    // 会在 SubsystemFluidBlockBehavior.cs:312 调 `ProcessModifiedCells()`（SpreadFluid 的
    // `for i in 0..1` 两遍各调一次，附近没有流体也照跑），它在 SubsystemTerrain.cs:71 **先清表再
    // 通知邻居** —— 火格被邻居回调改成空气正好是通知内部、清表之后入表，只靠引擎改动表必丢。
    // 所以主机侧开一个火捕获窗口（与流体同一套窗口/汇合点），并用 Update 前后的候选格差异兜底：
    //   · 候选 = 火表 m_fireData 的键 + 6 邻格 + m_toBurnAway / m_toExpand 的键 + 6 邻格；
    //   · 窗口 + 行为回调（窗口外亦然）捕获的格子进 HostCellCapture 的 carry，跨清表存活，
    //     由 HostCellCapture.Collect 与引擎改动表合并去重 —— 每格恰好下发一次。
    // Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:SubsystemFireBlockBehavior.Update/OnNeighborBlockChanged
    // =====================================================================================

    public sealed class SuSubsystemFireBlockBehavior : SubsystemFireBlockBehavior, IUpdateable
    {
        /// <summary>
        /// 每帧参与 before/after 比较的火候选键上限（每个键展开 7 格）。超过的部分**明确计数**
        /// （fireCandidateCapHits），并由行为回调（OnBlockAdded/Modified/Removed/OnNeighborBlockChanged）
        /// 覆盖 —— 不静默丢。
        /// </summary>
        private const int FireCandidateKeyCap = 256;

        private double m_nextClearTime;

        // ---------------------------------------------------------------- 烧尽表现上报（主机）
        //
        // 引擎的 5 秒分支（Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:227-256）：
        //   foreach (m_toBurnAway) { ChangeCell(..., 0); 碎屑 < 24m 最多 15; Sizzles < 16m 最多 4 }
        //   m_toBurnAway.Clear(); m_toExpand.Clear();
        // 这两类表现都不在 CellChange 流里，所以主机在**分支真正跑过**之后把这一批格上报给
        // Engine/BurnAwayEffectReplicaEngine.cs → EffectsSyncModule 广播。
        //
        // 判定（只认烧尽，不认普通内容变化）：
        //   ① 更新前快照 m_toBurnAway 的键；更新后该表由非空变空（只有 5 秒分支会清它）
        //      ⇒ 快照里的每一格都被引擎烧掉（引擎对每个条目都生成表现，不论 ChangeCell 是否 no-op）；
        //   ② 同一帧内新排队并被同帧 5 秒分支消费的目标不在快照里，用火候选格的 before/after
        //      差异补齐：只有"有内容 → 空气"才算（data-only / 换成别的方块 / 非火更新窗口都不算）。
        // 不上报普通 CellChange：那些仍由火捕获窗口 + 引擎改动表按原路径下发。
        // ----------------------------------------------------------------
        private readonly BurnAwayBatchCollector m_burnAwayCollector = new BurnAwayBatchCollector();
        private IBurnAwayEffectSink m_burnAwayReplica;
        private IDictionary m_burnAwayTable;
        private long m_burnAwayTableFailures;
        private long m_burnAwaySinkFailures;
        private long m_lastBurnAwayProblemLogMs;

        /// <summary>主机烧尽批次上报点（Engine/BurnAwayEffectReplicaEngine.cs 的 Pump 挂接）。</summary>
        internal IBurnAwayEffectSink BurnAwayReplica
        {
            get => m_burnAwayReplica;
            set => m_burnAwayReplica = value;
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire))
            {
                ClearFireTable();
                return;
            }
            // 主机：① 前置候选快照（补漏网）；② 行为回调把本次 Update 内所有真实写入收进火窗口；
            // ③ 窗口在 finally 里并进 carry（跨流体 ProcessModifiedCells 清表），由地形 Update 的
            //    Collect 与引擎改动表合并 → 每格恰好下发一次。
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            bool capturing = shell != null && shell.BeginFireCapture();
            IBurnAwayEffectSink burnAwaySink = m_burnAwayReplica;
            bool burnCapture = burnAwaySink != null;
            if (burnCapture)
            {
                m_burnAwayCollector.BeginBatch();
                m_burnAwayCollector.SnapshotPending(BurnAwayTable);
            }
            try
            {
                Point3[] candidates = capturing ? SnapshotFireCandidates(FireCandidateKeyCap) : Array.Empty<Point3>();
                int[] before = capturing ? ReplicaGates.ReadCellValues(SubsystemTerrain, candidates) : Array.Empty<int>();
                base.Update(dt);
                if (capturing) ReplicaGates.RegisterFireChangedCells(SubsystemTerrain, candidates, before);
                if (burnCapture)
                {
                    IDictionary table = BurnAwayTable;
                    bool periodicBranchRan = table != null &&
                        m_burnAwayCollector.PendingCount > 0 && table.Count == 0;
                    m_burnAwayCollector.CollectProcessedPending(periodicBranchRan);
                    if (capturing)
                    {
                        int[] after = ReplicaGates.ReadCellValues(SubsystemTerrain, candidates);
                        m_burnAwayCollector.CollectCandidateChanges(candidates, before, after);
                    }
                }
            }
            finally
            {
                if (capturing) shell?.EndFireCapture();
            }
            if (burnCapture && m_burnAwayCollector.Count > 0)
                EmitBurnAwayBatch(burnAwaySink);
        }

        /// <summary>引擎私有表 m_toBurnAway（只用于"5 秒分支是否清空"的判定与更新前快照）。</summary>
        private IDictionary BurnAwayTable
        {
            get
            {
                if (m_burnAwayTable != null) return m_burnAwayTable;
                m_burnAwayTable = ReplicaGates.GetField(this, "m_toBurnAway",
                    typeof(SubsystemFireBlockBehavior)) as IDictionary;
                if (m_burnAwayTable == null)
                {
                    m_burnAwayTableFailures++;
                    LogBurnAwayProblem("读不到引擎私有表 m_toBurnAway → 主机无法上报烧尽批次" +
                        "（客户端不会有碎屑/声音；累计 " + m_burnAwayTableFailures + " 次，明确失败不静默）");
                }
                return m_burnAwayTable;
            }
        }

        /// <summary>一批烧尽格已经由引擎处理完：只上报一次（去重 + 封顶由收集器保证）。</summary>
        private void EmitBurnAwayBatch(IBurnAwayEffectSink sink)
        {
            BurnAwayCellData[] cells = m_burnAwayCollector.ToArray();
            int dropped = m_burnAwayCollector.Dropped;
            try
            {
                sink.OnHostBurnAwayBatch(cells, dropped);
            }
            catch (Exception e)
            {
                m_burnAwaySinkFailures++;
                LogBurnAwayProblem("烧尽批次上报异常（" + cells.Length + " 格）：" +
                    e.GetType().Name + " " + e.Message +
                    " → 本批客户端没有碎屑/声音（累计 " + m_burnAwaySinkFailures +
                    " 次，明确计数，不静默）");
            }
        }

        /// <summary>
        /// 烧尽上报路径的限流日志（10s 一行，带累计次数）：反射失败 / 上报异常的可见出口。
        /// 不用一次性标志 —— 后续失败也必须能被看见。
        /// </summary>
        private void LogBurnAwayProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastBurnAwayProblemLogMs != 0 &&
                nowMs - m_lastBurnAwayProblemLogMs < 10000) return;
            m_lastBurnAwayProblemLogMs = nowMs;
            Log.Warning("[SuMP] burnAwayEffect: " + message);
        }

        /// <summary>
        /// 本帧可能被火写入的格：火表 m_fireData 的键 + 6 邻格（烧尽目标 / 点火目标都在邻格里），
        /// 以及本帧待烧尽 m_toBurnAway、待蔓延 m_toExpand 的键 + 6 邻格。有界 256 键，超出明确计数。
        /// Source: Survivalcraft/Game/SubsystemFireBlockBehavior.cs:SubsystemFireBlockBehavior.m_fireData /
        ///         m_toBurnAway / m_toExpand
        /// </summary>
        private Point3[] SnapshotFireCandidates(int maxKeys)
        {
            // 没有火在烧（绝大多数帧）：三张火表都是空的 ⇒ 一个候选也不比较，零分配。
            if (!HasFireWork()) return Array.Empty<Point3>();
            var seen = new HashSet<Point3>();
            var cells = new List<Point3>(128);
            int keyBudget = maxKeys;
            int capHits = 0;
            AddFireTableCells("m_fireData", ref keyBudget, ref capHits, seen, cells);
            AddFireTableCells("m_toBurnAway", ref keyBudget, ref capHits, seen, cells);
            AddFireTableCells("m_toExpand", ref keyBudget, ref capHits, seen, cells);
            if (capHits > 0)
                (SubsystemTerrain as SuSubsystemTerrain)?.NoteFireCandidateCapHit(capHits);
            return cells.ToArray();
        }

        /// <summary>三张火表里有没有任何条目（有火 / 有待烧尽 / 有待蔓延）。</summary>
        private bool HasFireWork() =>
            TableCount("m_fireData") + TableCount("m_toBurnAway") + TableCount("m_toExpand") > 0;

        private int TableCount(string fieldName)
        {
            object table = ReplicaGates.GetField(this, fieldName, typeof(SubsystemFireBlockBehavior));
            return table is IDictionary dictionary ? dictionary.Count : 0;
        }

        private void AddFireTableCells(string fieldName, ref int keyBudget, ref int capHits,
            HashSet<Point3> seen, List<Point3> cells)
        {
            object table = ReplicaGates.GetField(this, fieldName, typeof(SubsystemFireBlockBehavior));
            if (table is not IDictionary dictionary || dictionary.Count == 0) return;
            foreach (object key in dictionary.Keys)
            {
                if (key is not Point3 point) continue;
                if (keyBudget <= 0)
                {
                    capHits++;
                    continue;
                }
                keyBudget--;
                AddFireCandidateCell(point, seen, cells);
                AddFireCandidateCell(new Point3(point.X - 1, point.Y, point.Z), seen, cells);
                AddFireCandidateCell(new Point3(point.X + 1, point.Y, point.Z), seen, cells);
                AddFireCandidateCell(new Point3(point.X, point.Y - 1, point.Z), seen, cells);
                AddFireCandidateCell(new Point3(point.X, point.Y + 1, point.Z), seen, cells);
                AddFireCandidateCell(new Point3(point.X, point.Y, point.Z - 1), seen, cells);
                AddFireCandidateCell(new Point3(point.X, point.Y, point.Z + 1), seen, cells);
            }
        }

        private static void AddFireCandidateCell(Point3 cell, HashSet<Point3> seen, List<Point3> cells)
        {
            if (cell.Y < 0 || cell.Y > 255) return;
            if (seen.Add(cell)) cells.Add(cell);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            // 引擎这一支最后会 ChangeCell（把失去支撑的火格改回空气 / 更新连接位）。
            // 它经常是从 ProcessModifiedCells 的邻居通知里被调用的（清表之后），
            // 所以回调前后各读一次，真的变了就登记 —— 只靠引擎改动表这一格会丢。
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire)) return;
            var cell = new Point3(x, y, z);
            int before = ReplicaGates.ReadCellValue(SubsystemTerrain, cell);
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            ReplicaGates.NoteFireCandidateIfChanged(SubsystemTerrain, cell, before);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire)) return;
            // ChangeCell 已写入 → 这一格本身一定变了（内容变化：空气 → 火 / 其它 → 火）。
            ReplicaGates.RunAndCaptureFireCell(SubsystemTerrain, new Point3(x, y, z),
                () => base.OnBlockAdded(value, oldValue, x, y, z),
                cellAlreadyChanged: true, dataOnly: false);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire)) return;
            // 火被改回空气 / 换成别的方块（烧尽、浇水、挖掘）→ 内容变化，必须捕获。
            ReplicaGates.RunAndCaptureFireCell(SubsystemTerrain, new Point3(x, y, z),
                () => base.OnBlockRemoved(value, newValue, x, y, z),
                cellAlreadyChanged: true, dataOnly: false);
        }

        public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fire)) return;
            // ChangeCell 只在"内容不变、值确实变了"时走这一支 ⇒ 这一格是 data-only 变化
            // （火焰朝向 data 位变化：同样是"燃烧中"与"熄灭"之间的可见差异）。
            ReplicaGates.RunAndCaptureFireCell(SubsystemTerrain, new Point3(x, y, z),
                () => base.OnBlockModified(value, oldValue, x, y, z),
                cellAlreadyChanged: true, dataOnly: true);
        }

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            // 这一支只 AddFire（维护火表），不改格；客户端照样不维护火表。
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
    // 爆炸物引信：引擎的引信**不在格子里**，必须由主机把它变成格子状态。
    //
    // 引擎事实（Survivalcraft/Game/SubsystemExplosivesBlockBehavior.cs）：
    //   · `m_explosiveDataByPoint`（:33）→ `ExplosiveData.TimeToExplosion`（:16）是**主机私有表**；
    //     `IgniteFuse`（:41-55，火药桶 6~7s / 起爆器 0.8~1.2s）只往表里加一条，**不写格**；
    //     `Update`（:57-92）每帧扣时间、到 0 才 `TryExplodeBlock`，也不写格；
    //   · 主机看到的"引信在烧"只是 `FuseParticleSystem`（:69-73，**仅** GunpowderKegBlock）——
    //     本端表现，不随 CellChange 下发 ⇒ 客户端在爆炸前一直看到没点着的桶（实机问题）。
    //   · 熄灭/卸下没有独立入口：只有 `OnBlockRemoved`（:102-106）摘表项，以及
    //     `OnChunkDiscarding`（:108-122）在区块卸载时丢表项（引擎语义 = 该引信取消）。
    //
    // 修复：主机在 Update 的捕获窗口里读这张表，把**剩余引信 tick**写进火药桶格子的 data
    // （Terrain/FuseCellState.cs：第 8..13 位，1/4 秒一格），走既有 ChangeCell → CellChange 管线。
    // 客户端只按格子 data 做表现（FuseReplicaPresentation），**不跑 base.Update、不倒计时、不爆炸**。
    //
    // 覆盖：
    //   · 主机用火柴点火：`SubsystemMatchBlockBehavior.OnUse` → `IgniteFuse` → 下一帧本外壳写 data；
    //   · 客户端请求点火：`ExplosionReplicaEngine.TryExecuteRequest` 在主机调 `IgniteFuse` → 同上；
    //   · 逐 tick 倒计时：本外壳每帧比对 data 位，变了才写（同一帧多个 tick 合并成一次）；
    //   · 熄灭/卸下：表项消失且格子仍是火药桶 ⇒ 清位（内容已变 ⇒ 不再补写，爆炸只由爆炸路径负责）；
    //   · 区块卸载（引擎会丢表项）：在 SaveChunk 之前把该块里已点着的 data 位清掉，避免存档里留下
    //     "永远在烧、永远不会炸"的桶；
    //   · 起爆器：data 0..5 是挂载面，写高位会让方块不渲染（DetonatorBlock.cs:41-44/54-62），
    //     引擎也不给它建引信粒子 ⇒ **不能**用格子 data 表示，只显式计数 + 限流日志（不静默）。
    // Source: Survivalcraft/Game/SubsystemExplosivesBlockBehavior.cs:IgniteFuse/Update/OnBlockRemoved/OnChunkDiscarding
    // =====================================================================================

    public sealed class SuSubsystemExplosivesBlockBehavior : SubsystemExplosivesBlockBehavior, IUpdateable
    {
        /// <summary>每帧参与 before/after 比较的引信候选键上限（每个键展开 7 格）。超出明确计数。</summary>
        private const int FuseCandidateKeyCap = 256;
        /// <summary>引信活动 / 起爆器不可表示 / 表读取失败的限流日志间隔。</summary>
        private const long FuseWarningIntervalMs = 10000;
        /// <summary>引信表反射解析失败后的重试间隔（字段名变化时不要每帧反射）。</summary>
        private const double FuseTableRetrySeconds = 5.0;

        /// <summary>引擎的 <c>m_explosiveDataByPoint</c>（主机私有引信表）。</summary>
        private IDictionary m_fuseTable;
        private FieldInfo m_fusePointField;
        private FieldInfo m_fuseTimeField;
        private double m_nextFuseTableRetry;
        private long m_fuseTableFailures;

        /// <summary>本帧表里的火药桶（cell → 剩余 tick）。复用，避免每帧分配。</summary>
        private readonly Dictionary<Point3, int> m_currentFuse = new Dictionary<Point3, int>();
        /// <summary>本端写过引信位、还没清掉的格子（跨帧账本，实例状态）。</summary>
        private readonly HashSet<Point3> m_litFuseCells = new HashSet<Point3>();
        private readonly List<Point3> m_staleFuseCells = new List<Point3>(16);
        private readonly HashSet<Point3> m_detonatorCells = new HashSet<Point3>();
        private readonly List<Point3> m_detonatorSeen = new List<Point3>(4);

        private long m_fuseWrites;
        private long m_fuseClears;
        private long m_fuseDiscardClears;
        private long m_detonatorIgnitions;
        private long m_lastWarningMs;
        private long m_lastLoggedWrites;
        private long m_lastLoggedClears;
        private long m_lastLoggedDiscardClears;
        private long m_lastLoggedDetonator;
        private long m_lastLoggedFailures;

        // 客户端：从主机存档里可能带出引信表；客户端绝不消费它（不跑 base.Update），定期清表。
        private double m_nextClientTableClear;

        void IUpdateable.Update(float dt)
        {
            // Source: Survivalcraft/Game/SubsystemExplosivesBlockBehavior.cs:SubsystemExplosivesBlockBehavior.Update
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Fuse))
            {
                // 客户端副本：不推进引信、不写格、不爆炸；表现只按主机下发的格子 data 做。
                ClearClientFuseTable();
                return;
            }
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            bool capturing = shell != null && shell.BeginFuseCapture();
            if (!capturing)
            {
                // 单机 / 无桥：保持引擎原生行为（引信粒子 / 声音照旧），不把引信写进格子。
                base.Update(dt);
                return;
            }
            try
            {
                Point3[] candidates = SnapshotFuseCandidates(FuseCandidateKeyCap);
                int[] before = ReplicaGates.ReadCellValues(SubsystemTerrain, candidates);
                base.Update(dt);
                SyncFuseCells(shell, candidates, before);
            }
            finally
            {
                shell?.EndFuseCapture();
            }
        }

        /// <summary>
        /// 本帧可能被引信写入的格：引信表里各点的格子 + 6 邻格，以及上帧写过引信位的格子 + 6 邻格
        /// （熄灭清理要覆盖到）。有界 <see cref="FuseCandidateKeyCap"/> 键，超出明确计数。
        /// Source: Survivalcraft/Game/SubsystemExplosivesBlockBehavior.cs:m_explosiveDataByPoint
        /// </summary>
        private Point3[] SnapshotFuseCandidates(int maxKeys)
        {
            if (!EnsureFuseTable()) return Array.Empty<Point3>();
            if (m_fuseTable.Count == 0 && m_litFuseCells.Count == 0) return Array.Empty<Point3>();
            var seen = new HashSet<Point3>();
            var cells = new List<Point3>(64);
            int keyBudget = maxKeys;
            int capHits = 0;
            foreach (object value in m_fuseTable.Values)
            {
                if (!TryReadFuse(value, out Point3 point, out _)) continue;
                AddFuseCandidateKey(point, ref keyBudget, ref capHits, seen, cells);
            }
            foreach (Point3 point in m_litFuseCells)
                AddFuseCandidateKey(point, ref keyBudget, ref capHits, seen, cells);
            if (capHits > 0)
                (SubsystemTerrain as SuSubsystemTerrain)?.NoteFuseCandidateCapHit(capHits);
            return cells.ToArray();
        }

        private static void AddFuseCandidateKey(Point3 point, ref int keyBudget, ref int capHits,
            HashSet<Point3> seen, List<Point3> cells)
        {
            if (keyBudget <= 0)
            {
                capHits++;
                return;
            }
            keyBudget--;
            AddFuseCandidateCell(point, seen, cells);
            AddFuseCandidateCell(new Point3(point.X - 1, point.Y, point.Z), seen, cells);
            AddFuseCandidateCell(new Point3(point.X + 1, point.Y, point.Z), seen, cells);
            AddFuseCandidateCell(new Point3(point.X, point.Y - 1, point.Z), seen, cells);
            AddFuseCandidateCell(new Point3(point.X, point.Y + 1, point.Z), seen, cells);
            AddFuseCandidateCell(new Point3(point.X, point.Y, point.Z - 1), seen, cells);
            AddFuseCandidateCell(new Point3(point.X, point.Y, point.Z + 1), seen, cells);
        }

        private static void AddFuseCandidateCell(Point3 cell, HashSet<Point3> seen, List<Point3> cells)
        {
            if (cell.Y < 0 || cell.Y > 255) return;
            if (seen.Add(cell)) cells.Add(cell);
        }

        /// <summary>
        /// 把主机私有引信表**同步进格子 data**：
        ///   ① 表里每个火药桶 → 剩余 tick 写进 data 第 8..13 位（没变就不写，同帧多个 tick 自然合并）；
        ///   ② 上帧写过、本帧表里没了 / 内容已变的格 → 熄灭清位（内容已变时不再补写，爆炸路径自己下发）；
        ///   ③ 候选格 before/after 复核 → 进引信捕获窗口（与确认写入合并去重）；
        ///   ④ 起爆器 / 表读取失败 / 区块丢弃清位 → 计数 + 限流日志（不静默）。
        /// </summary>
        private void SyncFuseCells(SuSubsystemTerrain shell, Point3[] candidates, int[] before)
        {
            if (!EnsureFuseTable())
            {
                // 读取失败已由 EnsureFuseTable 计数 / 限流日志；本帧不写格（不猜、不伪造）。
                MaybeLogFuseActivity();
                return;
            }
            m_currentFuse.Clear();
            m_detonatorSeen.Clear();
            foreach (object value in m_fuseTable.Values)
            {
                if (value == null) continue;
                if (!TryReadFuse(value, out Point3 point, out float time)) continue;
                if (point.Y < 0 || point.Y > 255) continue;
                Block block = BlockAt(point);
                if (block is GunpowderKegBlock)
                {
                    m_currentFuse[point] = FuseCellState.TicksFromSeconds(time);
                }
                else if (block is DetonatorBlock)
                {
                    // 起爆器引信：data 0..5 是挂载面，写高位会让方块不渲染（DetonatorBlock 的
                    // GenerateTerrainVertices 用 data < 6 判网格）⇒ 只能显式记账，不伪装成格子状态。
                    m_detonatorSeen.Add(point);
                }
                // 其它内容：表项已过期（OnBlockRemoved 会摘掉），不猜、不写。
            }

            int writes = 0;
            foreach (KeyValuePair<Point3, int> pair in m_currentFuse)
            {
                Point3 cell = pair.Key;
                int value = ReplicaGates.ReadCellValue(SubsystemTerrain, cell);
                int contents = Game.Terrain.ExtractContents(value);
                if (!(BlockOf(contents) is GunpowderKegBlock)) continue;
                int data = Game.Terrain.ExtractData(value);
                int desired = FuseCellState.WithTicks(data, pair.Value);
                if (desired == data)
                {
                    m_litFuseCells.Add(cell);   // 位已经对了（重连 / 刚加载）：不制造无意义写入
                    continue;
                }
                // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
                SubsystemTerrain.ChangeCell(cell.X, cell.Y, cell.Z,
                    Game.Terrain.ReplaceData(value, desired));
                shell.NoteFuseCellChanged(cell, dataOnly: true);
                m_litFuseCells.Add(cell);
                writes++;
            }

            // 熄灭 / 卸下：内容已变（爆炸 / 挖掉）⇒ 该格的主机改动已经在引擎表里，这里不再补写，
            // 只清本端账本 —— 否则爆炸后还会多一条 CellChange。
            m_staleFuseCells.Clear();
            foreach (Point3 cell in m_litFuseCells)
            {
                if (m_currentFuse.ContainsKey(cell)) continue;
                int value = ReplicaGates.ReadCellValue(SubsystemTerrain, cell);
                int contents = Game.Terrain.ExtractContents(value);
                int data = Game.Terrain.ExtractData(value);
                if (BlockOf(contents) is GunpowderKegBlock && FuseCellState.IsLit(data))
                {
                    SubsystemTerrain.ChangeCell(cell.X, cell.Y, cell.Z,
                        Game.Terrain.ReplaceData(value, FuseCellState.Clear(data)));
                    shell.NoteFuseCellChanged(cell, dataOnly: true);
                    m_fuseClears++;
                }
                m_staleFuseCells.Add(cell);
            }
            for (int i = 0; i < m_staleFuseCells.Count; i++) m_litFuseCells.Remove(m_staleFuseCells[i]);

            // 候选复核：真的变了才登记（没有变化的候选计入 fuseCellSkipped），
            // 与上面"确认写入"的登记在窗口/carry 里合并去重。
            ReplicaGates.RegisterFuseChangedCells(SubsystemTerrain, candidates, before);
            m_fuseWrites += writes;

            // 起爆器：只统计"新点着"的点（同一格只算一次），消失的从账本摘掉。
            for (int i = 0; i < m_detonatorSeen.Count; i++)
                if (m_detonatorCells.Add(m_detonatorSeen[i])) m_detonatorIgnitions++;
            m_staleFuseCells.Clear();
            foreach (Point3 point in m_detonatorCells)
                if (!m_detonatorSeen.Contains(point)) m_staleFuseCells.Add(point);
            for (int i = 0; i < m_staleFuseCells.Count; i++) m_detonatorCells.Remove(m_staleFuseCells[i]);
            m_staleFuseCells.Clear();

            MaybeLogFuseActivity();
        }

        public override void OnChunkDiscarding(TerrainChunk chunk)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                // 客户端：这一块的引信表现一起收掉；表项由 base 摘掉。
                Presentation?.ForgetChunk(chunk);
            }
            else
            {
                // 主机：引擎的 OnChunkDiscarding 会丢掉该块的所有引信表项（= 这些引信永远不会炸）。
                // 若把点着的 data 位留在存档里，客户端重载这块后会看到"永远在烧"的桶。
                // 在 SaveChunk 之前直接清位（不走 ChangeCell：区块即将释放，不能登记新改动/降级光照）。
                ClearFuseBitsInChunk(chunk);
            }
            base.OnChunkDiscarding(chunk);
        }

        private void ClearFuseBitsInChunk(TerrainChunk chunk)
        {
            if (m_litFuseCells.Count == 0) return;
            m_staleFuseCells.Clear();
            int originX = chunk.Origin.X;
            int originZ = chunk.Origin.Y;
            foreach (Point3 cell in m_litFuseCells)
            {
                if (cell.X < originX || cell.X >= originX + 16) continue;
                if (cell.Z < originZ || cell.Z >= originZ + 16) continue;
                int localX = cell.X - originX;
                int localZ = cell.Z - originZ;
                int value = chunk.GetCellValueFast(localX, cell.Y, localZ);
                int contents = Game.Terrain.ExtractContents(value);
                int data = Game.Terrain.ExtractData(value);
                if (BlockOf(contents) is GunpowderKegBlock && FuseCellState.IsLit(data))
                {
                    chunk.SetCellValueFast(localX, cell.Y, localZ,
                        Game.Terrain.ReplaceData(value, FuseCellState.Clear(data)));
                    m_fuseDiscardClears++;
                }
                m_staleFuseCells.Add(cell);
            }
            for (int i = 0; i < m_staleFuseCells.Count; i++) m_litFuseCells.Remove(m_staleFuseCells[i]);
            m_staleFuseCells.Clear();
        }

        // ---------------------------------------------------------------- 客户端表现（只读格子 data）

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            base.OnBlockGenerated(value, x, y, z, isLoaded);
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            Presentation?.Present(x, y, z, Game.Terrain.ExtractContents(value), Game.Terrain.ExtractData(value));
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            base.OnBlockAdded(value, oldValue, x, y, z);
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            Presentation?.Present(x, y, z, Game.Terrain.ExtractContents(value), Game.Terrain.ExtractData(value));
        }

        public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
        {
            base.OnBlockModified(value, oldValue, x, y, z);
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            Presentation?.Present(x, y, z, Game.Terrain.ExtractContents(value), Game.Terrain.ExtractData(value));
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            base.OnBlockRemoved(value, newValue, x, y, z);
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            Presentation?.Forget(x, y, z);
        }

        /// <summary>客户端表现层（由组合根挂在 <c>TerrainBridge.FusePresenter</c>；主机为 null 也不走这里）。</summary>
        private FuseReplicaPresentation Presentation =>
            (SubsystemTerrain as SuSubsystemTerrain)?.Bridge?.FusePresenter;

        private void ClearClientFuseTable()
        {
            double now = Time.RealTime;
            if (now < m_nextClientTableClear) return;
            m_nextClientTableClear = now + 1.0;
            // Source: Survivalcraft/Game/SubsystemExplosivesBlockBehavior.cs:m_explosiveDataByPoint
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_explosiveDataByPoint",
                typeof(SubsystemExplosivesBlockBehavior));
        }

        // ---------------------------------------------------------------- 私有表反射 + 计数/日志

        /// <summary>
        /// 解析引擎私有引信表。<c>m_explosiveDataByPoint</c> 与嵌套类 <c>ExplosiveData</c>
        /// 的 <c>Point</c> / <c>TimeToExplosion</c> 字段（SubsystemExplosivesBlockBehavior.cs:16/33）。
        /// 解析失败 5s 后重试，并计入 <c>fuseTableFailures</c>（不静默当空表）。
        /// </summary>
        private bool EnsureFuseTable()
        {
            if (m_fuseTable != null) return true;
            double now = Time.RealTime;
            if (now < m_nextFuseTableRetry) return false;
            m_nextFuseTableRetry = now + FuseTableRetrySeconds;
            object raw = ReplicaGates.GetField(this, "m_explosiveDataByPoint",
                typeof(SubsystemExplosivesBlockBehavior));
            m_fuseTable = raw as IDictionary;
            Type dataType = typeof(SubsystemExplosivesBlockBehavior).GetNestedType("ExplosiveData",
                BindingFlags.NonPublic);
            if (dataType != null)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic;
                m_fusePointField = dataType.GetField("Point", flags);
                m_fuseTimeField = dataType.GetField("TimeToExplosion", flags);
            }
            if (m_fuseTable == null || m_fusePointField == null || m_fuseTimeField == null)
            {
                m_fuseTable = null;
                m_fuseTableFailures++;
                MaybeLogFuseActivity();
                return false;
            }
            return true;
        }

        private bool TryReadFuse(object data, out Point3 point, out float time)
        {
            point = default;
            time = 0f;
            if (data == null || m_fusePointField == null || m_fuseTimeField == null) return false;
            try
            {
                object rawPoint = m_fusePointField.GetValue(data);
                if (!(rawPoint is Point3 parsedPoint)) return false;
                object rawTime = m_fuseTimeField.GetValue(data);
                if (!(rawTime is float parsedTime)) return false;
                point = parsedPoint;
                time = parsedTime;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private Block BlockAt(Point3 cell) =>
            BlockOf(Game.Terrain.ExtractContents(ReplicaGates.ReadCellValue(SubsystemTerrain, cell)));

        private static Block BlockOf(int contents) =>
            contents > 0 && BlocksManager.Blocks != null && contents < BlocksManager.Blocks.Length
                ? BlocksManager.Blocks[contents]
                : null;

        /// <summary>
        /// 引信活动限流日志（10s 一行，计数没变化就不写）：火药桶写格 / 熄火清位 / 区块丢弃清位 /
        /// 起爆器点火（无格子状态可表示）/ 引信表读取失败。所有计数都在这里可见，不静默。
        /// </summary>
        private void MaybeLogFuseActivity()
        {
            if (m_fuseWrites == m_lastLoggedWrites &&
                m_fuseClears == m_lastLoggedClears &&
                m_fuseDiscardClears == m_lastLoggedDiscardClears &&
                m_detonatorIgnitions == m_lastLoggedDetonator &&
                m_fuseTableFailures == m_lastLoggedFailures)
            {
                return;
            }
            long now = Environment.TickCount64;
            if (m_lastWarningMs != 0 && now - m_lastWarningMs < FuseWarningIntervalMs) return;
            m_lastWarningMs = now;
            m_lastLoggedWrites = m_fuseWrites;
            m_lastLoggedClears = m_fuseClears;
            m_lastLoggedDiscardClears = m_fuseDiscardClears;
            m_lastLoggedDetonator = m_detonatorIgnitions;
            m_lastLoggedFailures = m_fuseTableFailures;
            Log.Information("[SuMP] fuseCellCapture: 火药桶写格 " + m_fuseWrites +
                " 次（data-only，剩余 tick 写入格子）· 熄火清位 " + m_fuseClears +
                " 次 · 区块丢弃清位 " + m_fuseDiscardClears + " 次 · 起爆器点火 " +
                m_detonatorIgnitions + " 次（DetonatorBlock 的 data 0..5 是挂载面、引擎无引信粒子 ⇒ " +
                "不写格子，客户端只在爆炸事件里看到结果；不伪造状态）· 引信表读取失败 " +
                m_fuseTableFailures + " 次");
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
        /// <summary>
        /// 主机爆炸复制挂接点（实现 = Engine/ExplosionReplicaEngine.cs，由组合根装配的适配器在
        /// Pump 时写入；客户端也写入，用于拦下"投掷 / 点燃引信"请求）。**实例字段，无静态状态。**
        /// 为 null = 适配器还没挂上：主机照旧跑 base.Update（本地爆炸正常，只是没有网络广播，
        /// 适配器会按未挂接计数并写明原因）。
        /// </summary>
        public ExplosionReplicaEngine Replica { get; set; }

        void IUpdateable.Update(float dt)
        {
            SubsystemTerrain terrain = base.Project.FindSubsystem<SubsystemTerrain>(false);
            if (!ReplicaGates.Allow(terrain, ReplicaMutationSource.Explosion))
            {
                ReplicaGates.ClearTable(terrain, this, "m_queuedExplosions",
                    typeof(SubsystemExplosions));
                return;
            }
            // 主机：由适配器复刻 base.Update 的队列消费循环，逐实例上报（含链式爆炸）。
            // 客户端永远不会走到这里（上面已返回）。
            ExplosionReplicaEngine replica = Replica;
            if (replica != null && replica.UpdateHostExplosions(this, dt)) return;
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

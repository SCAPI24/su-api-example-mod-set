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

        /// <summary>
        /// 客户端把某个引擎私有工作表清空，避免"只登记不消费"越积越多。
        /// 返回 true = 字段存在且已清空（数量不计）；false = 字段不存在 / 本端不是客户端 / 读取失败。
        /// 调用方把 false 计入各自 skipped 计数（不静默假装清了）。
        /// </summary>
        internal static bool ClearTable(SubsystemTerrain terrain, object owner, string fieldName,
            Type declaringType)
        {
            if (!IsClient(terrain) || owner == null) return false;
            try
            {
                object table = Game.Program.ModManager?.ModParentField?
                    .GetParentField<object>(owner, fieldName, declaringType);
                if (table is IDictionary dictionary)
                {
                    dictionary.Clear();
                    return true;
                }
                if (table is IList list)
                {
                    list.Clear();
                    return true;
                }
                // HashSet<T> 等只实现 ICollection<T>，不是 IDictionary/IList（例如
                // SubsystemWoodBlockBehavior.m_leavesToCheck:19）；用公共 Clear() 兜住，
                // 仍然返回 false = 没有可清的集合（不静默假装清了）。
                MethodInfo clear = table.GetType().GetMethod("Clear", Type.EmptyTypes);
                if (clear != null)
                {
                    clear.Invoke(table, null);
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                // 字段不存在 = 引擎版本差异：Loud 一次由调用方计数器体现；不静默假装清了。
                return false;
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

        /// <summary>
        /// 主机：把引擎改动表里待下发的格子**立刻**接进捕获 carry（跨流体清表存活）。
        /// 世界演化外壳在每个会写格的回调 / Update 之后调用：这些邻居回调恰好是在流体
        /// <c>ProcessModifiedCells()</c> 的邻居通知循环里被调用的（清表之后才写入），
        /// 不在这里接住就会被同一 Update 的第二遍清表抹掉。
        /// 客户端 / 单机 / 无桥由 <see cref="SuSubsystemTerrain.RescueEvolutionCells"/> 自己判断。
        /// </summary>
        internal static void RescueEvolutionCells(SubsystemTerrain terrain) =>
            (terrain as SuSubsystemTerrain)?.RescueEvolutionCells();

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
            ModLog.VerboseInfo("[SuMP] fuseCellCapture: 火药桶写格 " + m_fuseWrites +
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
            // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs:OnPoll（把转换排进 m_toUpdate）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Grass)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs:OnNeighborBlockChanged:76/82/86
            // （草 → 泥土 / data 改写）—— 这个回调就是从流体 ProcessModifiedCells 的通知里打进来的，
            // 写入落在清表之后，必须立刻接住，否则第二遍清表就抹掉（实机：主机泥土、客户端草）。
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnExplosion(int value, int x, int y, int z, float damage)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Grass)) return;
            base.OnExplosion(value, x, y, z, damage);
            // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs:OnExplosion:94（爆炸把草改回泥土）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Grass))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toUpdate",
                    typeof(SubsystemGrassBlockBehavior));
                return;
            }
            // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs:Update:128（草 → 泥土）
            base.Update(dt);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    public sealed class SuSubsystemPlantBlockBehavior : SubsystemPlantBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Plant)) return;
            base.OnPoll(value, x, y, z, pollPass);
            // Source: Survivalcraft/Game/SubsystemPlantBlockBehavior.cs:OnPoll/Grow*（经 CellChangeQueue 写格）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Plant)) return;
            base.OnBlockGenerated(value, x, y, z, isLoaded);
            // Source: Survivalcraft/Game/SubsystemPlantBlockBehavior.cs:OnBlockGenerated:56
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Plant)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemPlantBlockBehavior.cs:OnNeighborBlockChanged:35/43/50
            // （DestroyCell：植物失去支撑被拔掉）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    public sealed class SuSubsystemDeciduousLeavesBlockBehavior :
        SubsystemDeciduousLeavesBlockBehavior
    {
        // 引擎的 QueueLeafParticles 是 private；客户端只借它排队，不执行会改季相的
        // UpdateTimeOfYear / CreateFallenLeaves。
        private static MethodInfo s_queueLeafParticles;
        private static bool s_queueLeafParticlesMissingLogged;

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.DeciduousLeaves))
            {
                QueueLeafParticlesOnly(value, x, y, z);
                return;
            }
            base.OnBlockGenerated(value, x, y, z, isLoaded);
            // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:OnBlockGenerated:62
            // （applyImmediately:true → ChangeCell）与 CreateFallenLeaves（QueueCellChange）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.DeciduousLeaves))
            {
                QueueLeafParticlesOnly(value, x, y, z);
                return;
            }
            base.OnPoll(value, x, y, z, pollPass);
            // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:OnPoll:68
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        /// <summary>
        /// 客户端：只调用引擎私有的 QueueLeafParticles（把叶子位置排进 m_leafParticles），
        /// 不调用 UpdateTimeOfYear / CreateFallenLeaves（它们会写格）。
        /// IUpdateable.Update 会照常消费队列并生成 LeavesParticleSystem。
        /// </summary>
        private void QueueLeafParticlesOnly(int value, int x, int y, int z)
        {
            if (s_queueLeafParticles == null)
            {
                s_queueLeafParticles = typeof(SubsystemDeciduousLeavesBlockBehavior)
                    .GetMethod("QueueLeafParticles", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            if (s_queueLeafParticles == null)
            {
                if (!s_queueLeafParticlesMissingLogged)
                {
                    s_queueLeafParticlesMissingLogged = true;
                    Log.Warning("[SuMP] evergreen-leaves: 引擎 QueueLeafParticles 不可用 → " +
                        "客户端秋季落叶粒子缺失（明确失败，不静默）");
                }
                return;
            }
            try
            {
                s_queueLeafParticles.Invoke(this, new object[] { value, x, y, z });
            }
            catch (Exception e)
            {
                if (!s_queueLeafParticlesMissingLogged)
                {
                    s_queueLeafParticlesMissingLogged = true;
                    Log.Warning("[SuMP] evergreen-leaves: QueueLeafParticles 调用失败 → " +
                        "客户端秋季落叶粒子缺失：" + e.GetType().Name + " " + e.Message);
                }
            }
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
            // Source: Survivalcraft/Game/SubsystemCactusBlockBehavior.cs:OnPoll:40（QueueCellChange 长高）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Cactus)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemCactusBlockBehavior.cs:OnNeighborBlockChanged:23
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    public sealed class SuSubsystemRotBlockBehavior : SubsystemRotBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Rot)) return;
            base.OnPoll(value, x, y, z, pollPass);
            // Source: Survivalcraft/Game/SubsystemRotBlockBehavior.cs:OnPoll:53（ChangeCell 腐坏一档）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    public sealed class SuSubsystemCarpetBlockBehavior : SubsystemCarpetBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Carpet)) return;
            base.OnPoll(value, x, y, z, pollPass);
            // Source: Survivalcraft/Game/SubsystemCarpetBlockBehavior.cs:OnPoll:35（被雨雪冲掉）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Carpet)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemCarpetBlockBehavior.cs:OnNeighborBlockChanged:24
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    public sealed class SuSubsystemFallenLeavesBlockBehavior : SubsystemFallenLeavesBlockBehavior
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.FallenLeaves)) return;
            base.OnPoll(value, x, y, z, pollPass);
            // Source: Survivalcraft/Game/SubsystemFallenLeavesBlockBehavior.cs:OnPoll:28
            // / UpdateFallenLeaves:69（DestroyCell）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.FallenLeaves)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemFallenLeavesBlockBehavior.cs:OnNeighborBlockChanged:19
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    public sealed class SuSubsystemSoilBlockBehavior : SubsystemSoilBlockBehavior, IUpdateable
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Soil)) return;
            base.OnPoll(value, x, y, z, pollPass);
            // Source: Survivalcraft/Game/SubsystemSoilBlockBehavior.cs:OnPoll（排进 m_toHydrate）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Soil)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemSoilBlockBehavior.cs:OnNeighborBlockChanged:55
            // （耕地/土被压成泥土）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
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
            // Source: Survivalcraft/Game/SubsystemSoilBlockBehavior.cs:Update:74/92（退化 / 干湿 data）
            base.Update(dt);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    public sealed class SuSubsystemIvyBlockBehavior : SubsystemIvyBlockBehavior, IUpdateable
    {
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Ivy)) return;
            base.OnPoll(value, x, y, z, pollPass);
            // Source: Survivalcraft/Game/SubsystemIvyBlockBehavior.cs:OnPoll:59（排进 m_toUpdate）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Ivy)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemIvyBlockBehavior.cs:OnNeighborBlockChanged:50
            // （DestroyCell：常春藤失去附着面）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Ivy))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toUpdate",
                    typeof(SubsystemIvyBlockBehavior));
                return;
            }
            // Source: Survivalcraft/Game/SubsystemIvyBlockBehavior.cs:Update:29（常春藤向下生长）
            base.Update(dt);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    public sealed class SuSubsystemSaplingBlockBehavior : SubsystemSaplingBlockBehavior, IUpdateable
    {
        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Sapling)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemSaplingBlockBehavior.cs:OnNeighborBlockChanged:41
            // （DestroyCell：树苗失去支撑）
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Sapling)) return;
            base.OnBlockAdded(value, oldValue, x, y, z);
            // 这一支只维护 m_saplings（不改格）；接住的是触发它的那一笔写入
            // （若来自流体通知循环，同样会被下一遍清表抹掉）。
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Sapling)) return;
            base.OnBlockRemoved(value, newValue, x, y, z);
            // 同上：只摘表，不改格；接住触发它的那一笔写入。
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Sapling))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_saplings",
                    typeof(SubsystemSaplingBlockBehavior));
                return;
            }
            // Source: Survivalcraft/Game/SubsystemSaplingBlockBehavior.cs:Update:173/176/181/186/191
            // （树苗成熟：ChangeCell + TerrainBrush.Paint 整棵树）
            base.Update(dt);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
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
        private bool m_clientRampFaultLogged;
        private long m_lastFogStatusLogMs;

        // ---- 客户端视觉推进的反射字段（引擎私有，静态缓存；Source: SubsystemWeather.cs:2-49） ----
        private static readonly FieldInfo s_clientPrecipRamp = typeof(SubsystemWeather).GetField(
            "m_precipitationRampTime", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_clientFogRamp = typeof(SubsystemWeather).GetField(
            "m_fogRampTime", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_clientFogStart = typeof(SubsystemWeather).GetField(
            "m_fogStartTime", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_clientFogEnd = typeof(SubsystemWeather).GetField(
            "m_fogEndTime", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_clientPrecipIntensity = typeof(SubsystemWeather).GetField(
            "<PrecipitationIntensity>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_clientFogIntensity = typeof(SubsystemWeather).GetField(
            "<FogIntensity>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_clientFogProgress = typeof(SubsystemWeather).GetField(
            "<FogProgress>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_clientFogSeed = typeof(SubsystemWeather).GetField(
            "<FogSeed>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Weather))
            {
                DisableClientPersistentTerrainHandlers();
                // 客户端天气状态由 WorldInfo 覆盖；这里绝不 base.Update（否则就是本地模拟）。
                UpdateClientVisualRamp(dt);
                return;
            }
            base.Update(dt);
        }

        /// <summary>
        /// 2026-10-26 修复"客户端雨/雾过渡一顿一顿"：客户端不跑 base.Update，强度 / 雾进度
        /// 此前由 2 Hz WorldInfo 快照硬写（500ms 一阶，主机端却是每帧连续 ramp）。
        /// 现在快照只在**状态翻转**时写窗口（见 ClientReplicaEngine.ApplyWeather），
        /// 连续推进由这里按引擎 Update 的同款数学完成（Source:
        /// Survivalcraft/Game/SubsystemWeather.cs:378-391 / 494-505）：
        /// 降水强度 ±dt/ramp；雾强度 ±dt/ramp、雾进度 += dt/(end-start)、FogSeed=Hash(start)。
        /// 只做视觉：不选天气、不打雷、不改地形（那些仍是主机权威）。
        /// </summary>
        private void UpdateClientVisualRamp(float dt)
        {
            try
            {
                if (s_clientPrecipRamp == null || s_clientFogRamp == null ||
                    s_clientFogStart == null || s_clientFogEnd == null ||
                    s_clientPrecipIntensity == null || s_clientFogIntensity == null ||
                    s_clientFogProgress == null || s_clientFogSeed == null)
                {
                    if (!m_clientRampFaultLogged)
                    {
                        m_clientRampFaultLogged = true;
                        Log.Warning("[SuMP] 客户端天气视觉推进的字段反射绑定失败（引擎字段名变化？）" +
                            " → 雨/雾过渡退回 2Hz 快照阶跃（只记录一次）");
                    }
                    return;
                }
                SubsystemGameInfo gameInfo = Project?.FindSubsystem<SubsystemGameInfo>(false);
                if (gameInfo == null) return;
                if (!gameInfo.WorldSettings.AreWeatherEffectsEnabled)
                {
                    s_clientPrecipIntensity.SetValue(this, 0f);
                    s_clientFogIntensity.SetValue(this, 0f);
                    return;
                }
                float precipRamp = (float)s_clientPrecipRamp.GetValue(this);
                if (precipRamp > 0f)
                {
                    float precip = PrecipitationIntensity + (IsPrecipitationStarted ? dt : -dt) / precipRamp;
                    s_clientPrecipIntensity.SetValue(this, MathUtils.Saturate(precip));
                }
                float fogRamp = (float)s_clientFogRamp.GetValue(this);
                if (fogRamp > 0f)
                {
                    s_clientFogIntensity.SetValue(this, MathUtils.Saturate(
                        FogIntensity + (IsFogStarted ? dt : -dt) / fogRamp));
                    long nowLog = (long)(Time.RealTime * 1000.0);
                    if (nowLog - m_lastFogStatusLogMs >= 10000)
                    {
                        m_lastFogStatusLogMs = nowLog;
                        ModLog.VerboseInfo("[SuMP][fog] 客户端雾状态：intensity=" +
                            FogIntensity.ToString("F2") + " progress=" +
                            FogProgress.ToString("F3") + " started=" + IsFogStarted +
                            " ramp=" + fogRamp.ToString("F1"));
                    }
                    if (IsFogStarted)
                    {
                        double fogStart = (double)s_clientFogStart.GetValue(this);
                        double fogEnd = (double)s_clientFogEnd.GetValue(this);
                        if (fogEnd > fogStart)
                        {
                            s_clientFogProgress.SetValue(this,
                                FogProgress + dt / (float)(fogEnd - fogStart));
                        }
                        // FogSeed 必须保持主机快照值：本地按 client 自己的 fogStart 重算会让
                        // 每端的雾密度随机因子不同（实机 C1 雾异常浓、其他端正常）。
                        // 主机 WorldInfo 已在首帧 / 雾开始时写入 FogSeed。
                    }
                }
            }
            catch (Exception e)
            {
                if (!m_clientRampFaultLogged)
                {
                    m_clientRampFaultLogged = true;
                    Log.Warning("[SuMP] 客户端天气视觉推进异常（" + e.GetType().Name + " " + e.Message +
                        "）→ 本会话退回 2Hz 快照阶跃（只记录一次）");
                }
            }
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

    // =====================================================================================
    // 塌落物理：Game.SubsystemCollapsingBlockBehavior（本文件新增的客户端闸门）
    //
    // 引擎事实（Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs）：
    //   · OnNeighborBlockChanged:22-28 → TryCollapseColumn；
    //   · TryCollapseColumn:86-108 把整列沙子/沙砾登记成 "CollapsingBlock" 移动组，
    //     然后 ChangeCell(source,0)（:107）——源格在**本地**立刻消失；
    //   · MovingBlocksCollidedWithTerrain:40-54 在撞到非支撑可破坏方块时 DestroyCell（:51）；
    //   · MovingBlocksStopped:56-73 在落地时 DestroyCell(..., block.Value)（:66）——目标格在**本地**出现；
    //   · 载入时订阅 SubsystemMovingBlocks.Stopped / CollidedWithTerrain（:36-37）。
    // 客户端如果照跑：源格删除 + 目标格放置都会进 m_modifiedCells，被 CaptureClientEdits 当成
    // 玩家编辑提交；主机自己又跑一遍塌落 ⇒ 目标格被两份写入叠加 = 实机"放一格沙子、落地后变两格"。
    //
    // 客户端策略：
    //   · OnNeighborBlockChanged 直接拒绝（并清掉可能存在的引擎工作表）；
    //   · 摘掉 base.Load 订阅的两个事件（否则主机复制的 "CollapsingBlock" 移动组在客户端落地时
    //     仍会 DestroyCell）；改为**只判停、绝不写格**的同条件碰撞处理（保住下落动画停在地面）；
    //   · 主机复制的移动组只做视觉；最终格只收主机 CellChange（90abe952 的 moving-block 复制）。
    //   · 本引擎版本的 SubsystemCollapsingBlockBehavior **没有 m_toMove / 工作表字段**（逐字段读过：
    //     m_subsystemGameInfo / m_subsystemSoundMaterials / m_subsystemMovingBlocks）；候选表名
    //     仍然显式清理，未来版本若加表会自动计数，不静默。
    // =====================================================================================

    public sealed class SuSubsystemCollapsingBlockBehavior : SubsystemCollapsingBlockBehavior, IUpdateable
    {
        // ---- 主机侧：Id=="CollapsingBlock" 移动组检测（与活塞外壳同口径）----------------------
        // 引擎事实（Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs）：
        //   · TryCollapseColumn:86-108 把整列沙子 / 沙砾 AddMovingBlockSet(id:"CollapsingBlock",
        //     tag:null) 并立刻 ChangeCell 清源格；
        //   · MovingBlocksStopped:56-73 只在集合落地时 DestroyCell 写目标格（走既有 CellChange 下发）。
        // 客户端闸门已让客户端不跑这段逻辑 ⇒ 不做复制的话，客户端只看到"源格消失 + 落地格出现"。
        // 这里在**主机**上按"集合是否已跟踪"检测开始 / 结束（读档时存档里带的集合也补发），
        // 把引擎 AddMovingBlockSet 的原始参数交给 Engine/PistonReplicaEngine.cs 广播。
        // 塌落集合的 Tag 是 null：起点格只能由 StartPosition（AddMovingBlockSet 的 position 入参）得到。
        private sealed class TrackedCollapse
        {
            public IMovingBlockSet Set;
            public Vector3 Target;
        }

        private const long HostProblemLogMs = 10000;
        private readonly Dictionary<Point3, TrackedCollapse> m_hostTracked =
            new Dictionary<Point3, TrackedCollapse>();
        private readonly List<IMovingBlockSet> m_hostScratch = new List<IMovingBlockSet>();
        private readonly List<Point3> m_hostEnded = new List<Point3>();
        private readonly MovingBlockSetMotion m_hostMotion = new MovingBlockSetMotion();
        private SubsystemMovingBlocks m_hostMovingBlocks;
        private PistonReplicaEngine m_replica;
        private long m_hostCellFailures;
        private long m_hostMotionFailures;
        private long m_hostStartReadFailures;
        private long m_hostInvalidMoves;
        private long m_hostEmitFailures;
        private long m_lastHostProblemLogMs;

        /// <summary>
        /// 移动组复制的挂接点（实现 = Engine/PistonReplicaEngine.cs，组合根在 Pump 里写入）。
        /// **实例字段，无静态状态。** null = 适配器还没挂上：主机照旧跑引擎塌落，只是不广播
        /// （客户端没有中间过程；最终格仍由 CellChange 同步）。
        /// </summary>
        public PistonReplicaEngine Replica
        {
            get => m_replica;
            set => m_replica = value;
        }

        /// <summary>base.Load 的两个写格订阅已摘 + 只判停替代已挂上（false = 限流 Error，客户端不静默）。</summary>
        private bool m_clientMotionHandlersDetached;

        /// <summary>
        /// 摘除永久失败后停止每帧重试的闸门（防反射风暴；错误已单独 Error 点名）。
        /// 2026-10-21 平板帧率：原实现只要摘除失败就**每帧重试 + 每帧重复订阅**，
        /// 事件委托链无限增长（3.5h 累计 46 万次）；基类字段查找修好后此闸门仅作兜底。
        /// </summary>
        private bool m_clientDetachAbandoned;
        private const long ClientDetachMaxAttempts = 300;
        /// <summary>摘除失败时的重试/日志计数（限流 10s 一条，不逐帧刷屏）。</summary>
        private const long ClientDetachFailureLogMs = 10000;
        private long m_clientDetachAttempts;
        private long m_lastClientDetachLogMs;
        private MethodInfo m_isCollapseSupportBlock;
        private bool m_clientCollisionReflectionFailed;

        /// <summary>
        /// **不能只在 Load 里判断**：世界/Project 先加载，角色（ClientReplica）由地形模块在加入房间后
        /// 每帧写入；Load 时很可能还不是客户端。这个 Update 排在 MovingBlocks（Default）之前，
        /// 一旦角色变成客户端就立刻摘订阅，避免主机复制的移动组在本帧落地写格。
        /// </summary>
        public UpdateOrder UpdateOrder => UpdateOrder.Input;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            EnsureClientHandlersDetached();
        }

        void IUpdateable.Update(float dt)
        {
            EnsureClientHandlersDetached();
            UpdateHostDetection();
        }

        // ------------------------------------------------------------ 主机：检测开始 / 结束

        private SubsystemMovingBlocks MovingBlocks
        {
            get
            {
                if (m_hostMovingBlocks != null) return m_hostMovingBlocks;
                m_hostMovingBlocks = base.Project?.FindSubsystem<SubsystemMovingBlocks>(false);
                return m_hostMovingBlocks;
            }
        }

        /// <summary>
        /// 主机：每帧比对 SubsystemMovingBlocks 里的 "CollapsingBlock" 集合。
        ///   · 不在跟踪表里的集合 = 一次塌落开始（读档时存档里带的集合也会补发）；
        ///   · 跟踪中的集合已经不在列表里 = 引擎把它移除了（落地 / 掉到目标点）。
        /// 客户端绝不走这里（ReplicaGates.Allow 为 false）：客户端不模拟塌落、也绝不广播。
        /// </summary>
        private void UpdateHostDetection()
        {
            SubsystemTerrain terrain = SubsystemTerrain;
            if (!ReplicaGates.Allow(terrain, ReplicaMutationSource.Collapsing)) return;
            PistonReplicaEngine replica = m_replica;
            SubsystemMovingBlocks moving = MovingBlocks;
            if (replica == null || moving == null) return;
            m_hostScratch.Clear();
            for (int i = 0; i < moving.MovingBlockSets.Count; i++)
            {
                IMovingBlockSet set = moving.MovingBlockSets[i];
                if (set == null) continue;
                if (!string.Equals(set.Id, PistonReplicaPresentation.EngineCollapsingId,
                        StringComparison.Ordinal)) continue;
                if (!TryReadSetCell(set, out Point3 cell, out string reason))
                {
                    m_hostCellFailures++;
                    NoteHostProblem("塌落集合无法确定起点格（" + reason + "）→ 本次不广播" +
                        "（累计 " + m_hostCellFailures + "）");
                    continue;
                }
                if (m_hostTracked.TryGetValue(cell, out TrackedCollapse tracked) &&
                    ReferenceEquals(tracked.Set, set)) continue;
                m_hostScratch.Add(set);
            }
            for (int i = 0; i < m_hostScratch.Count; i++)
                EmitHostStarted(replica, m_hostScratch[i]);
            m_hostScratch.Clear();
            if (m_hostTracked.Count > 0) EmitHostEnded(replica, moving);
        }

        /// <summary>
        /// 主机：塌落集合的起点格 = 引擎 AddMovingBlockSet 的 position（new Vector3(p)，整数）。
        /// StartPosition 读不到 / 不是整数格 → 明确失败（计数 + 限流日志），绝不四舍五入猜。
        /// </summary>
        private bool TryReadSetCell(IMovingBlockSet set, out Point3 cell, out string reason)
        {
            cell = Point3.Zero;
            if (!m_hostMotion.TryReadStartPosition(set, out Vector3 start, out reason)) return false;
            cell = new Point3(Game.Terrain.ToCell(start.X), Game.Terrain.ToCell(start.Y),
                Game.Terrain.ToCell(start.Z));
            if (Math.Abs(start.X - cell.X) > 1e-3f || Math.Abs(start.Y - cell.Y) > 1e-3f ||
                Math.Abs(start.Z - cell.Z) > 1e-3f)
            {
                reason = "start-not-integer-(" + start.X + "," + start.Y + "," + start.Z + ")";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// 主机：一次塌落刚刚开始。把引擎 AddMovingBlockSet 的全部参数（起始 / 目标 / 速度 /
        /// 加速度 / 阻力 / 平滑度 / 整列方块）读出来，交给适配器广播；参数不合法时明确计数。
        /// </summary>
        private void EmitHostStarted(PistonReplicaEngine replica, IMovingBlockSet set)
        {
            if (!TryReadSetCell(set, out Point3 cell, out string cellReason))
            {
                m_hostCellFailures++;
                NoteHostProblem("塌落集合无法确定起点格（" + cellReason + "）→ 本次不广播" +
                    "（累计 " + m_hostCellFailures + "）");
                return;
            }
            if (!m_hostMotion.TryReadStartPosition(set, out Vector3 start, out string startReason))
            {
                m_hostStartReadFailures++;
                NoteHostProblem("读不到塌落集合的 StartPosition（" + startReason +
                    "）→ 本次不广播（累计 " + m_hostStartReadFailures + "）");
                return;
            }
            if (!m_hostMotion.TryRead(set, out Vector3 target, out float speed,
                    out float acceleration, out float drag, out Vector2 smoothness,
                    out string motionReason))
            {
                m_hostMotionFailures++;
                NoteHostProblem("读不到引擎塌落集合的运动参数（" + motionReason +
                    "）→ 本次不广播（客户端看不到这一段；累计 " + m_hostMotionFailures + "）");
                return;
            }
            ReadOnlyList<MovingBlock> blocks = set.Blocks;
            var blockData = new PistonBlockData[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
                blockData[i] = new PistonBlockData(blocks[i].Offset, blocks[i].Value);
            // 引擎塌落没有"活塞格 value"：源格在 AddMovingBlockSet 之后立刻被 ChangeCell 清掉，
            // 原始 value 已经读不到 ⇒ piston_value / mode / face 填 0（客户端不依赖它们）。
            var move = new PistonMoveData(MovingBlockKind.Collapsing, cell.X, cell.Y, cell.Z, 0, 0,
                0, start, target, speed, acceleration, drag, smoothness, blockData);
            if (!PistonMoveCodec.TryValidateMove(in move, out string invalid))
            {
                m_hostInvalidMoves++;
                NoteHostProblem("主机塌落运动参数不合法（" + invalid + "）：" + move.Describe() +
                    " → 本次不广播（累计 " + m_hostInvalidMoves + "）");
                return;
            }
            if (m_hostTracked.TryGetValue(cell, out TrackedCollapse previous) &&
                !ReferenceEquals(previous.Set, set))
            {
                // 同一列上一条集合还没登记结束就被换掉了（引擎不该发生）：先把旧的按结束上报，
                // 否则客户端会同时持有两条动画。
                m_hostTracked.Remove(cell);
                EmitHostEnd(replica, cell,
                    IsAtTarget(previous.Set.Position, previous.Target), "replaced");
            }
            m_hostTracked[cell] = new TrackedCollapse { Set = set, Target = target };
            try
            {
                replica.OnHostMoveStarted(in move);
            }
            catch (Exception e)
            {
                m_hostEmitFailures++;
                NoteHostProblem("塌落运动开始上报异常（" + e.GetType().Name + " " + e.Message +
                    "）；累计 " + m_hostEmitFailures);
            }
        }

        /// <summary>
        /// 主机：跟踪中的集合已经不在 <c>MovingBlockSets</c> 里 —— 引擎的 MovingBlocksStopped
        /// 已经把它移除并写了落地格（走既有 CellChange 下发）。
        /// completed = 真的走到了 TargetPosition（没撞到任何方块）；塌落正常情况下是 false（落地）。
        /// </summary>
        private void EmitHostEnded(PistonReplicaEngine replica, SubsystemMovingBlocks moving)
        {
            m_hostEnded.Clear();
            foreach (KeyValuePair<Point3, TrackedCollapse> pair in m_hostTracked)
            {
                bool present = false;
                for (int i = 0; i < moving.MovingBlockSets.Count; i++)
                {
                    if (!ReferenceEquals(moving.MovingBlockSets[i], pair.Value.Set)) continue;
                    present = true;
                    break;
                }
                if (!present) m_hostEnded.Add(pair.Key);
            }
            for (int i = 0; i < m_hostEnded.Count; i++)
            {
                Point3 cell = m_hostEnded[i];
                TrackedCollapse tracked = m_hostTracked[cell];
                m_hostTracked.Remove(cell);
                bool atTarget = IsAtTarget(tracked.Set.Position, tracked.Target);
                EmitHostEnd(replica, cell, atTarget, atTarget ? "target" : "landed");
            }
            m_hostEnded.Clear();
        }

        /// <summary>
        /// 主机：上报一次塌落结束。StartEventId 先留 0 —— 真正的 event_id 由模块在发送时按
        /// "这条运动的开始批次"填（PistonSyncModule.OnHostMoveEnded 用 m_sentStartIds 补）。
        /// </summary>
        private void EmitHostEnd(PistonReplicaEngine replica, Point3 cell, bool completed,
            string reason)
        {
            var end = new PistonMoveEndData(MovingBlockKind.Collapsing, cell.X, cell.Y, cell.Z,
                completed, reason, 0);
            try
            {
                replica.OnHostMoveEnded(in end);
            }
            catch (Exception e)
            {
                m_hostEmitFailures++;
                NoteHostProblem("塌落运动结束上报异常（" + e.GetType().Name + " " + e.Message +
                    "）；累计 " + m_hostEmitFailures);
            }
        }

        private static bool IsAtTarget(Vector3 position, Vector3 target) =>
            Math.Abs(position.X - target.X) <= 1e-4f &&
            Math.Abs(position.Y - target.Y) <= 1e-4f &&
            Math.Abs(position.Z - target.Z) <= 1e-4f;

        /// <summary>主机侧塌落检测的限流日志（10s 一条，消息里带累计次数，不静默）。</summary>
        private void NoteHostProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastHostProblemLogMs != 0 &&
                nowMs - m_lastHostProblemLogMs < HostProblemLogMs) return;
            m_lastHostProblemLogMs = nowMs;
            Log.Warning("[SuMP] collapsing(host): " + message);
        }

        private void EnsureClientHandlersDetached()
        {
            if (m_clientMotionHandlersDetached) return;
            if (m_clientDetachAbandoned) return;
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;

            object moving = ReplicaGates.GetField(this, "m_subsystemMovingBlocks",
                typeof(SubsystemCollapsingBlockBehavior));
            m_clientDetachAttempts++;
            // 按**声明类型**摘：只摘引擎基类自己的两个写格回调
            //   · Stopped            → MovingBlocksStopped:56-73（DestroyCell 落地格 :66）
            //   · CollidedWithTerrain → MovingBlocksCollidedWithTerrain:40-54（DestroyCell :51）
            // 本外壳自己的 OnClient* 委托声明在 SuSubsystemCollapsingBlockBehavior 上，不会被摘，
            // 所以重试是幂等的（不会像"按 Target 全摘再重挂"那样在失败路径逐帧累积委托）。
            ClientEventDetachResult stopped = ClientReplicaEventDetach.DetachDeclaredHandlers(
                moving, "Stopped", typeof(SubsystemCollapsingBlockBehavior));
            ClientEventDetachResult collision = ClientReplicaEventDetach.DetachDeclaredHandlers(
                moving, "CollidedWithTerrain", typeof(SubsystemCollapsingBlockBehavior));
            // 全类保护：SubsystemPistonBlockBehavior 与塌落共享同一个 SubsystemMovingBlocks，
            // 它的 base.Load 也订阅了同名的私有写格回调（活塞落地时写格）。
            // 优先让**活塞外壳自己**按 DeclaringType 摘（它知道自己的 OnPresentationStopped 要保留，
            // 而且本 Update 排在 SubsystemMovingBlocks（Default）之前，角色切换那一帧也不会漏网）；
            // 只有活塞外壳不存在（注入缺失）时才按引擎方法名兜底。幂等、有日志。
            SuSubsystemPistonBlockBehavior pistonShell =
                base.Project.FindSubsystem<SubsystemPistonBlockBehavior>(false)
                    as SuSubsystemPistonBlockBehavior;
            bool pistonDetachedByShell =
                pistonShell != null && pistonShell.EnsureClientBaseHandlersDetached();
            int pistonBaseHandlersDetached = pistonDetachedByShell
                ? 0
                : DetachForeignEngineHandlers(moving, "Stopped", "MovingBlocksStopped") +
                  DetachForeignEngineHandlers(moving, "CollidedWithTerrain",
                      "MovingBlocksCollidedWithTerrain");
            // 只判停的替代处理：保住"沙子落到支撑面上就停"的视觉，但绝不 DestroyCell。
            // EnsureEventHandler 先按（目标实例 + 方法名）复核，重试时不会重复挂同一个委托。
            bool collisionStopAdded = EnsureEventHandler(moving, "CollidedWithTerrain",
                "OnClientCollidingWithTerrain",
                new Action<IMovingBlockSet, Point3>(OnClientCollidingWithTerrain));
            // 统计"主机复制的塌落组在客户端落地但被跳过（不写格）"。
            bool stoppedCountAdded = EnsureEventHandler(moving, "Stopped",
                "OnClientMovingBlocksStopped",
                new Action<IMovingBlockSet>(OnClientMovingBlocksStopped));
            if (!(stopped.Succeeded && collision.Succeeded && collisionStopAdded &&
                  stoppedCountAdded))
            {
                NoteClientDetachFailure(stopped, collision, collisionStopAdded, stoppedCountAdded);
                if (m_clientDetachAttempts >= ClientDetachMaxAttempts)
                {
                    m_clientDetachAbandoned = true;
                    Log.Error("[SuMP] collapsing: 客户端摘订阅连续失败 " +
                        m_clientDetachAttempts + " 次 → 停止每帧重试（明确失败，不再重试；" +
                        "客户端本地塌落仍可能写格，需按日志修反射/引擎版本差异）");
                }
                return;
            }
            m_isCollapseSupportBlock = typeof(SubsystemCollapsingBlockBehavior).GetMethod(
                "IsCollapseSupportBlock", BindingFlags.Instance | BindingFlags.NonPublic);
            m_clientMotionHandlersDetached = true;
            ModLog.VerboseInfo("[SuMP] collapsing 客户端闸门：本地塌落 = " +
                "OnNeighborBlockChanged 拒绝 + 移动组落地订阅已摘" +
                "（stopped=" + stopped.Removed + " collision=" + collision.Removed +
                "）；引擎工作表 m_toMove=" +
                (typeof(SubsystemCollapsingBlockBehavior).GetField("m_toMove",
                    BindingFlags.Instance | BindingFlags.NonPublic) != null ? "存在" : "本版本不存在") +
                "；Piston 引擎写格订阅：外壳自摘=" + pistonDetachedByShell +
                " 兜底摘掉 " + pistonBaseHandlersDetached + " 个；最终格只收主机 CellChange");
        }

        /// <summary>
        /// 摘除失败：**限流 Error**（10s 一条，带重试次数与具体哪条事件失败），下一帧继续重试。
        /// 明确失败而不是静默 —— 客户端本地塌落仍可能写格时，日志里一定看得见。
        /// </summary>
        private void NoteClientDetachFailure(ClientEventDetachResult stopped,
            ClientEventDetachResult collision, bool collisionStopAdded, bool stoppedCountAdded)
        {
            long now = Environment.TickCount64;
            if (m_lastClientDetachLogMs != 0 &&
                now - m_lastClientDetachLogMs < ClientDetachFailureLogMs) return;
            m_lastClientDetachLogMs = now;
            Log.Error("[SuMP] collapsing: 客户端无法摘掉 SubsystemMovingBlocks 的落地/碰撞订阅" +
                "（Stopped=" + DescribeDetach(stopped) +
                " CollidedWithTerrain=" + DescribeDetach(collision) +
                " stopOnly=" + collisionStopAdded + " count=" + stoppedCountAdded +
                "，第 " + m_clientDetachAttempts + " 次尝试）→ 本地塌落仍可能写格，" +
                "明确失败而不是静默（下一帧继续重试，失败日志 10s 一条）");
        }

        private static string DescribeDetach(ClientEventDetachResult result)
        {
            if (result == null) return "null";
            return result.Succeeded
                ? "ok(removed=" + result.Removed + ")"
                : "fail(" + result.Failure + ")";
        }

        /// <summary>幂等挂事件：已经挂着（本实例 + 方法名）就不重复加；返回 false = 挂不上。</summary>
        private bool EnsureEventHandler(object owner, string eventName, string methodName,
            Delegate handler)
        {
            if (owner == null || handler == null) return false;
            if (ClientReplicaEventDetach.HasHandler(owner, eventName, this, methodName)) return true;
            return AddEventHandler(owner, eventName, handler);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Collapsing))
            {
                NoteCollapsingGated();
                ClearClientWorkTables();
                return;
            }
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            // Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs:TryCollapseColumn:100-108
            // （ChangeCell 清源格）。这个回调也可能从流体 ProcessModifiedCells 的通知里打进来；
            // 与其它演化外壳一致，在 base 之后立刻把写入接进跨清表 carry。
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        private void NoteCollapsingGated()
        {
            (SubsystemTerrain as SuSubsystemTerrain)?.CollapsingState.NoteGated();
        }

        /// <summary>
        /// 客户端清掉引擎内部工作表。**本引擎版本没有 m_toMove**；这里仍显式检查一组候选字段，
        /// 未来版本新增表时会被清掉并计入 collapsingSkipped（tables）。
        /// </summary>
        private void ClearClientWorkTables()
        {
            int tables = 0;
            if (ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toMove",
                    typeof(SubsystemCollapsingBlockBehavior))) tables++;
            if (ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toCollapse",
                    typeof(SubsystemCollapsingBlockBehavior))) tables++;
            if (ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toUpdate",
                    typeof(SubsystemCollapsingBlockBehavior))) tables++;
            if (ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toRemove",
                    typeof(SubsystemCollapsingBlockBehavior))) tables++;
            if (tables > 0)
                (SubsystemTerrain as SuSubsystemTerrain)?.CollapsingState.NoteTablesCleared(tables);
        }

        /// <summary>
        /// 按**引擎私有方法名**摘掉别的行为的写格订阅（Piston 与塌落共享 MovingBlocks）。
        /// 只动 method.Name 完全匹配的委托；Piston 外壳自己的表现回调（OnPresentationStopped）保留。
        /// </summary>
        private int DetachForeignEngineHandlers(object owner, string eventName, string methodName)
        {
            if (owner == null) return 0;
            FieldInfo field = owner.GetType().GetField(eventName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return 0;
            if (field.GetValue(owner) is not Delegate handlers) return 0;
            Delegate remaining = handlers;
            int removed = 0;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                if (handler.Method == null ||
                    !string.Equals(handler.Method.Name, methodName, StringComparison.Ordinal))
                    continue;
                remaining = Delegate.Remove(remaining, handler);
                removed++;
            }
            if (removed > 0) field.SetValue(owner, remaining);
            return removed;
        }

        private bool AddEventHandler(object owner, string eventName, Delegate handler)
        {
            if (owner == null || handler == null) return false;
            EventInfo info = owner.GetType().GetEvent(eventName,
                BindingFlags.Instance | BindingFlags.Public);
            if (info == null || info.EventHandlerType != handler.GetType()) return false;
            info.AddEventHandler(owner, handler);
            return true;
        }

        /// <summary>
        /// 客户端：主机复制的塌落组落地 → 只计数 + 移除**表现集合**；绝不 DestroyCell / ChangeCell。
        /// 移除是必要的：集合停在目标位置后若不摘掉，会和主机最终格叠画成"两块沙子"的视觉。
        /// 最终地形格仍由主机 CellChange 下发（本方法不写任何格）。
        /// </summary>
        private void OnClientMovingBlocksStopped(IMovingBlockSet movingBlockSet)
        {
            // 只处理引擎自己 Id 的残留集合；本 Mod 的 "CollapsingBlockReplica" 由适配器在
            // PistonMoveEnd / 看门狗到达时移除（停在原地等主机最终格，避免闪烁）。
            if (movingBlockSet == null) return;
            if (movingBlockSet.Id != PistonReplicaPresentation.EngineCollapsingId) return;
            (SubsystemTerrain as SuSubsystemTerrain)?.CollapsingState.NoteLandingSkipped();
            object moving = ReplicaGates.GetField(this, "m_subsystemMovingBlocks",
                typeof(SubsystemCollapsingBlockBehavior));
            if (moving is SubsystemMovingBlocks movingBlocks)
                movingBlocks.RemoveMovingBlockSet(movingBlockSet);
        }

        /// <summary>
        /// 客户端：与引擎 MovingBlocksCollidedWithTerrain 的**判停条件一致**，但绝不写格。
        /// 引擎在"支撑块"上 Stop；在"可破坏块"上 DestroyCell —— 后者由主机做并下发。
        /// </summary>
        private void OnClientCollidingWithTerrain(IMovingBlockSet movingBlockSet, Point3 p)
        {
            if (movingBlockSet == null) return;
            // 引擎残留集合（下载世界存档）与**本 Mod 的只表现集合**用同一份判停条件：
            // 都只调 set.Stop()（内存集合），绝不 DestroyCell / ChangeCell。
            if (movingBlockSet.Id != PistonReplicaPresentation.EngineCollapsingId &&
                movingBlockSet.Id != PistonReplicaPresentation.CollapsingReplicaId) return;
            if (m_isCollapseSupportBlock == null)
            {
                NoteCollisionReflectionFailure("引擎没有 IsCollapseSupportBlock");
                return;
            }
            int value = ReplicaGates.ReadCellValue(SubsystemTerrain, p);
            try
            {
                if (m_isCollapseSupportBlock.Invoke(this, new object[] { value }) is bool support && support)
                    movingBlockSet.Stop();
            }
            catch (Exception e)
            {
                NoteCollisionReflectionFailure(e.GetType().Name + " " + e.Message);
            }
        }

        private void NoteCollisionReflectionFailure(string reason)
        {
            if (m_clientCollisionReflectionFailed) return;
            m_clientCollisionReflectionFailed = true;
            Log.Warning("[SuMP] collapsing: 客户端判停条件不可用（" + reason +
                "）→ 塌落组会一直走到目标点（只影响表现；本地仍然绝不写格）");
        }
    }

    // =====================================================================================
    // 营火：客户端只保留粒子表现，不推进燃料（Game.SubsystemCampfireBlockBehavior）
    //   · Update:44-67 每 5 game-sec 调 ResizeCampfire(-1) → ChangeCell 写 data（燃料烧尽）；
    //     每 0.5s 的第二段只更新音量（纯表现）。
    //   · OnNeighborBlockChanged:69-75 下方透明 → DestroyCell。
    //   · OnChunkDiscarding:107-124 区块丢弃前 ResizeCampfire(-15) → ChangeCell。
    // 客户端 base.Update 会本地烧掉燃料并把 data 当客户端编辑提交（与主机重复改写同一格）。
    // =====================================================================================

    public sealed class SuSubsystemCampfireBlockBehavior : SubsystemCampfireBlockBehavior, IUpdateable
    {
        // 客户端表现层（组合根挂在 TerrainBridge.CampfirePresenter）：只维护 FireParticleSystem。
        // 主机侧同一实例只用于"烧尽检测"（Update 前后快照 Campfires），不建任何客户端粒子。
        private CampfireReplicaPresentation Presentation =>
            (SubsystemTerrain as SuSubsystemTerrain)?.Bridge?.CampfirePresenter;

        // 桥注入前（世界刚加载的第一帧）OnBlockGenerated 可能已经跑过：先记住格子（有界），
        // 等表现层可用时按**当前格子值**重放一次（不是静态状态，也不静默丢）。
        private const int PendingGeneratedCap = 512;
        private readonly List<Point3> m_pendingGenerated = new List<Point3>(16);
        private long m_pendingGeneratedDropped;
        private bool m_pendingGeneratedReplayLogged;

        private void ReplayPendingGenerated(CampfireReplicaPresentation presentation)
        {
            if (m_pendingGenerated.Count == 0) return;
            if (SubsystemTerrain?.Terrain == null) return;
            int replayed = 0;
            for (int i = 0; i < m_pendingGenerated.Count; i++)
            {
                Point3 cell = m_pendingGenerated[i];
                if (cell.Y < 0 || cell.Y > 255) continue;
                int value = SubsystemTerrain.Terrain.GetCellValueFast(cell.X, cell.Y, cell.Z);
                presentation.Present(value, cell.X, cell.Y, cell.Z);
                replayed++;
            }
            int dropped = (int)MathUtils.Min(m_pendingGeneratedDropped, int.MaxValue);
            m_pendingGenerated.Clear();
            if (!m_pendingGeneratedReplayLogged)
            {
                m_pendingGeneratedReplayLogged = true;
                ModLog.VerboseInfo("[SuMP] 营火表现：桥注入前生成的 " + replayed +
                    " 格已按当前格子值重放（超出上限丢弃 " + dropped + " 格）");
            }
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Campfire))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toReduce",
                    typeof(SubsystemCampfireBlockBehavior));
                // 客户端：不推进燃料，只做 1 秒一次的有界越界扫除（campfireStaleSweep）。
                CampfireReplicaPresentation presentation = Presentation;
                if (presentation != null)
                {
                    ReplayPendingGenerated(presentation);
                    presentation.Tick(dt);
                    presentation.MaybeLog(Time.RealTime);
                }
                return;
            }
            // 主机：base.Update 的 5 秒分支把燃料 -1（data 到 0 时引擎播 Audio/Sizzles）。
            // 前后快照 engine Campfires → 只把"刚烧尽"的格交给声音同步模块转给其它端。
            Presentation?.BeginHostUpdate(this);
            base.Update(dt);
            Presentation?.EndHostUpdate();
            Presentation?.MaybeLog(Time.RealTime);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                // 区块从本地存档生成：客户端粒子由本 mod 的表现层建（引擎 base 会再建一份 → 双份）。
                CampfireReplicaPresentation presentation = Presentation;
                if (presentation != null)
                {
                    presentation.Present(value, x, y, z);
                    return;
                }
                if (m_pendingGenerated.Count < PendingGeneratedCap)
                {
                    m_pendingGenerated.Add(new Point3(x, y, z));
                }
                else
                {
                    m_pendingGeneratedDropped++;
                }
                return;
            }
            base.OnBlockGenerated(value, x, y, z, isLoaded);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                // ApplyCells 落地主机"放置营火"后由 ClientBlockBehaviorNotifier 补发到这里。
                Presentation?.Present(value, x, y, z);
                return;
            }
            base.OnBlockAdded(value, oldValue, x, y, z);
        }

        public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                // data-only（燃料递减 / 到 0 变灰烬）：表现层按新 data 重建或收掉粒子。
                Presentation?.Present(value, x, y, z);
                return;
            }
            base.OnBlockModified(value, oldValue, x, y, z);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                Presentation?.Forget(x, y, z);
                return;
            }
            base.OnBlockRemoved(value, newValue, x, y, z);
        }

        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Campfire))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toReduce",
                    typeof(SubsystemCampfireBlockBehavior));
                return;
            }
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnChunkDiscarding(TerrainChunk chunk)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Campfire))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toReduce",
                    typeof(SubsystemCampfireBlockBehavior));
                // base 会 ResizeCampfire(-15) → ChangeCell（客户端绝不写格）；
                // 只把该块里的营火粒子收掉，避免陈旧粒子留在已丢弃的区块上。
                Presentation?.ForgetChunk(chunk);
                return;
            }
            base.OnChunkDiscarding(chunk);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    // =====================================================================================
    // 熔炉：客户端只保留火焰粒子表现（Game.SubsystemFurnaceBlockBehavior）
    //   · OnBlockAdded:30-49   contents 64/65 时 FindDatabaseObject("Furnace") → CreateEntity/AddEntity
    //                          （方块实体），contents 65 时 AddFire（本地粒子）；
    //   · OnBlockRemoved:51-70 非熔炉时 DropAllItems + RemoveEntity；contents 65 时 RemoveFire；
    //   · OnBlockGenerated:72-78 / OnBlockModified 的 AddFire 调用点同理；
    //   · OnChunkDiscarding:80-94 把本块里的火焰全部收掉；
    //   · 没有 Update：火焰完全由"格子 contents == 65"驱动（ComponentFurnace.Update 每帧写 65/64）。
    // 客户端 base 会建/删方块实体并掉落物品（都是主机权威），所以客户端的四个回调全部改走
    // FurnaceReplicaPresentation（只建/收 FireParticleSystem），绝不调用 base。
    // =====================================================================================

    public sealed class SuSubsystemFurnaceBlockBehavior : SubsystemFurnaceBlockBehavior,
        IUpdateable
    {
        private const int PendingGeneratedCap = 256;

        // 桥注入前（世界刚加载的第一帧）OnBlockGenerated 可能已经跑过：先记住格子（有界），
        // 等表现层可用时按**当前格子值**重放一次（不是静态状态，也不静默丢）。
        private readonly List<Point3> m_pendingGenerated = new List<Point3>(16);
        private long m_pendingGeneratedDropped;
        private long m_missingPresenter;
        private long m_lastMissingLogMs;
        private bool m_replayLogged;
        private bool m_dropLogged;

        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        private FurnaceReplicaPresentation Presentation =>
            (SubsystemTerrain as SuSubsystemTerrain)?.Bridge?.FurnacePresenter;

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            // 客户端：没有本地炉况模拟；只做 1 秒一次的有界越界扫除（furnaceFlameStaleSweep）
            // 与"表现层接线前生成的格"重放。主机侧引擎行为本来就没有 Update，这里不额外推进。
            FurnaceReplicaPresentation presentation = Presentation;
            if (presentation == null)
            {
                NoteMissingPresenter("Update", 0, 0, 0);
                return;
            }
            ReplayPendingGenerated(presentation);
            presentation.Tick(dt);
            presentation.MaybeLog(Time.RealTime);
        }

        private void ReplayPendingGenerated(FurnaceReplicaPresentation presentation)
        {
            if (m_pendingGenerated.Count == 0) return;
            if (SubsystemTerrain?.Terrain == null) return;
            int replayed = 0;
            for (int i = m_pendingGenerated.Count - 1; i >= 0; i--)
            {
                Point3 cell = m_pendingGenerated[i];
                if (cell.Y < 0 || cell.Y > 255)
                {
                    m_pendingGenerated.RemoveAt(i);
                    continue;
                }
                TerrainChunk chunk = SubsystemTerrain.Terrain.GetChunkAtCell(cell.X, cell.Z);
                if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                    continue;   // 区块内容还没定稿：保留待重放
                int value = SubsystemTerrain.Terrain.GetCellValueFast(cell.X, cell.Y, cell.Z);
                presentation.PresentCell(value, cell.X, cell.Y, cell.Z);
                m_pendingGenerated.RemoveAt(i);
                replayed++;
            }
            if (replayed > 0 && !m_replayLogged)
            {
                m_replayLogged = true;
                ModLog.VerboseInfo("[SuMP] 熔炉火焰表现：桥注入前生成的 " + replayed +
                    " 格已按当前格子值重放（超出上限丢弃 " + m_pendingGeneratedDropped + " 格）");
            }
        }

        /// <summary>
        /// 表现层未接线（TerrainBridge.FurnacePresenter == null）：明确计数 + 限流 Warning，
        /// 不假装火焰已显示、也不退回 base（base 会建实体）。
        /// </summary>
        private void NoteMissingPresenter(string where, int x, int y, int z)
        {
            m_missingPresenter++;
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastMissingLogMs != 0 && nowMs - m_lastMissingLogMs < 10000) return;
            m_lastMissingLogMs = nowMs;
            Log.Warning("[SuMP] 熔炉火焰表现层未接线（" + where + " @ " + x + "," + y + "," + z +
                "）→ 本格火焰暂不显示；累计 " + m_missingPresenter +
                " 次（TerrainBridge.FurnacePresenter == null，检查组合根装配）");
        }

        public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                FurnaceReplicaPresentation presentation = Presentation;
                if (presentation != null)
                {
                    presentation.PresentCell(value, x, y, z);
                    return;
                }
                if (m_pendingGenerated.Count < PendingGeneratedCap)
                {
                    m_pendingGenerated.Add(new Point3(x, y, z));
                }
                else
                {
                    m_pendingGeneratedDropped++;
                    if (!m_dropLogged)
                    {
                        m_dropLogged = true;
                        Log.Warning("[SuMP] 熔炉火焰表现：待重放格子超过上限 " +
                            PendingGeneratedCap + " → 之后的生成格不再排队（累计丢弃 " +
                            m_pendingGeneratedDropped + " 格；表现层接线后由炉况 / 格子变化重试）");
                    }
                }
                NoteMissingPresenter("OnBlockGenerated", x, y, z);
                return;
            }
            base.OnBlockGenerated(value, x, y, z, isLoaded);
        }

        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                // ApplyCells 落地主机"放置 / 点燃熔炉"后由 ClientBlockBehaviorNotifier 补发到这里。
                // 绝不 base：引擎的 OnBlockAdded 会 CreateEntity/AddEntity。
                FurnaceReplicaPresentation presentation = Presentation;
                if (presentation != null) presentation.PresentCell(value, x, y, z);
                else NoteMissingPresenter("OnBlockAdded", x, y, z);
                return;
            }
            base.OnBlockAdded(value, oldValue, x, y, z);
        }

        public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                // data-only（朝向变化）不会改变火焰参数：表现层只 Keep，不会重建第二个粒子。
                FurnaceReplicaPresentation presentation = Presentation;
                if (presentation != null) presentation.PresentCell(value, x, y, z);
                else NoteMissingPresenter("OnBlockModified", x, y, z);
                return;
            }
            base.OnBlockModified(value, oldValue, x, y, z);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                // 绝不 base：引擎的 OnBlockRemoved 会 DropAllItems + RemoveEntity。
                FurnaceReplicaPresentation presentation = Presentation;
                if (presentation != null) presentation.Forget(x, y, z);
                else NoteMissingPresenter("OnBlockRemoved", x, y, z);
                return;
            }
            base.OnBlockRemoved(value, newValue, x, y, z);
        }

        public override void OnChunkDiscarding(TerrainChunk chunk)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                // base 只收火焰（不像营火会写格），但客户端仍需先按本块收掉自己建的粒子，
                // 避免陈旧粒子留在已丢弃的区块上。
                FurnaceReplicaPresentation presentation = Presentation;
                if (presentation != null) presentation.ForgetChunk(chunk);
                else NoteMissingPresenter("OnChunkDiscarding", 0, 0, 0);
                return;
            }
            base.OnChunkDiscarding(chunk);
        }
    }

    // =====================================================================================
    // 草陷阱：Game.SubsystemGrassTrapBlockBehavior
    //   · OnCollide:20-32 累计 Damage（不写格）；
    //   · Update:34-59 Damage>1 时 DestroyCell 周边 5 格草陷阱（:47）。
    // 客户端本地累计 + 破坏 = 主机没有的编辑；客户端只做碰撞表现（无），不推进。
    // =====================================================================================

    public sealed class SuSubsystemGrassTrapBlockBehavior : SubsystemGrassTrapBlockBehavior, IUpdateable
    {
        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.GrassTrap))
            {
                ClearTables();
                return;
            }
            base.Update(dt);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnCollide(CellFace cellFace, float velocity, ComponentBody componentBody)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.GrassTrap)) return;
            base.OnCollide(cellFace, velocity, componentBody);
        }

        private void ClearTables()
        {
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_trapValues",
                typeof(SubsystemGrassTrapBlockBehavior));
            ReplicaGates.ClearTable(SubsystemTerrain, this, "m_toRemove",
                typeof(SubsystemGrassTrapBlockBehavior));
        }
    }

    // =====================================================================================
    // 树叶腐烂：Game.SubsystemWoodBlockBehavior
    //   · OnBlockRemoved:20-51 原木被移除时把 3×3×3 内的树叶登记进 m_leavesToCheck（不写格）；
    //   · Update:87-106 每 20 game-sec 取最多 200 片 → DecayLeavesIfNeeded:110-176
    //     找不到原木就 ChangeCell(leaf,0)（:158）；
    //   · OnChunkDiscarding:53-78 对丢弃区块内的树叶立即 DecayLeavesIfNeeded（同样写格）。
    // 客户端本地砍树后再本地腐烂 = 主机没有的新状态；且清表竞争下与主机不同步。
    // =====================================================================================

    public sealed class SuSubsystemWoodBlockBehavior : SubsystemWoodBlockBehavior, IUpdateable
    {
        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.WoodDecay))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_leavesToCheck",
                    typeof(SubsystemWoodBlockBehavior));
                return;
            }
            base.Update(dt);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.WoodDecay))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_leavesToCheck",
                    typeof(SubsystemWoodBlockBehavior));
                return;
            }
            base.OnBlockRemoved(value, newValue, x, y, z);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }

        public override void OnChunkDiscarding(TerrainChunk chunk)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.WoodDecay))
            {
                ReplicaGates.ClearTable(SubsystemTerrain, this, "m_leavesToCheck",
                    typeof(SubsystemWoodBlockBehavior));
                return;
            }
            base.OnChunkDiscarding(chunk);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    // =====================================================================================
    // 宝藏生成器：Game.SubsystemTreasureGeneratorBlockBehavior
    //   · OnNeighborBlockChanged:399-455 邻居是空气/树叶 → ChangeCell(self,0) 自毁（:401），
    //     25% 概率 AddPickable 一大批宝藏（:430-450）。
    // 客户端本地触发会同时删格 + 生成主机没有的拾取物实体（实体复制还会再收一份）；
    // 主机对同一次邻居变化会自己执行并广播，客户端只收结果。
    // =====================================================================================

    public sealed class SuSubsystemTreasureGeneratorBlockBehavior :
        SubsystemTreasureGeneratorBlockBehavior
    {
        public override void OnNeighborBlockChanged(int x, int y, int z, int neighborX, int neighborY,
            int neighborZ)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Treasure)) return;
            base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
            ReplicaGates.RescueEvolutionCells(SubsystemTerrain);
        }
    }

    // =====================================================================================
    // 延迟写格队列：Game.SubsystemCellChangeQueue
    //   · Update:45-51 每 20 game-sec ApplyCellChanges:55-69 → ChangeCell。
    // 目前只有已经被闸门的植物/仙人掌/落叶季相会入队；但它是**所有**延迟写格的总入口，
    // 客户端统一不推进并清表，未来新增生产者也不会把延迟写格漏成客户端编辑。
    // =====================================================================================

    public sealed class SuSubsystemCellChangeQueue : SubsystemCellChangeQueue, IUpdateable
    {
        private SubsystemTerrain m_terrain;
        private MethodInfo m_baseUpdate;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_terrain = base.Project.FindSubsystem<SubsystemTerrain>(throwOnError: true);
            // 引擎的 Update 是**显式接口实现**（SubsystemCellChangeQueue.cs:45），
            // 派生类不能 base.Update；用接口映射拿到基类实现，只在真的有排队的格时调用
            //（空表时 base 只做一次时间判断，没有副作用）。
            InterfaceMapping map = typeof(SubsystemCellChangeQueue)
                .GetInterfaceMap(typeof(IUpdateable));
            m_baseUpdate = null;
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i].Name == "Update")
                {
                    m_baseUpdate = map.TargetMethods[i];
                    break;
                }
            if (m_baseUpdate == null)
            {
                Log.Error("[SuMP] cellChangeQueue: 找不到 IUpdateable.Update 的基类实现 → " +
                    "主机不会应用延迟写格（明确失败，不静默）");
            }
        }

        void IUpdateable.Update(float dt)
        {
            if (!ReplicaGates.Allow(m_terrain, ReplicaMutationSource.CellChangeQueue))
            {
                ReplicaGates.ClearTable(m_terrain, this, "m_toChange",
                    typeof(SubsystemCellChangeQueue));
                return;
            }
            object table = ReplicaGates.GetField(this, "m_toChange",
                typeof(SubsystemCellChangeQueue));
            if (table is IDictionary dictionary && dictionary.Count == 0) return;
            if (m_baseUpdate == null) return;
            try
            {
                m_baseUpdate.Invoke(this, new object[] { dt });
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] cellChangeQueue: 调用引擎 Update 失败：" +
                    e.GetType().Name + " " + e.Message + "（延迟写格未应用）");
                return;
            }
            ReplicaGates.RescueEvolutionCells(m_terrain);
        }
    }

    // =====================================================================================
    // 方块轮询驱动：Game.SubsystemBlocksScanner
    //   · Update:35-99 遍历区块并调用每个 SubsystemPollableBlockBehavior.OnPoll。
    // 它是所有"定时世界演化"（草/植物/仙人掌/腐坏/地毯/落叶/耕地/常春藤）的驱动入口。
    // 这些目标已经各自有闸门；客户端再关掉驱动，等于给未来的 pollable 行为加一道总闸
    // （不会改变主机；客户端也不会因为漏注入某个新 pollable 而本地演化）。
    // =====================================================================================

    public sealed class SuSubsystemBlocksScanner : SubsystemBlocksScanner, IUpdateable
    {
        private SubsystemTerrain m_terrain;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_terrain = base.Project.FindSubsystem<SubsystemTerrain>(throwOnError: true);
        }

        void IUpdateable.Update(float dt)
        {
            // 客户端也要跑轮询驱动，否则 DeciduousLeavesBlockBehavior.OnPoll 永远不会被调用，
            // 秋季落叶粒子无法排队。所有会改格的行为在各自 OnPoll 里已由 ReplicaGates 拦下；
            // 这里只在客户端恢复“扫描/表现”本身，不恢复世界演化写格。
            if (m_terrain is SuSubsystemTerrain shell && shell.ClientReplica)
            {
                base.Update(dt);
                return;
            }
            if (!ReplicaGates.Allow(m_terrain, ReplicaMutationSource.BlocksScanner)) return;
            base.Update(dt);
        }
    }
    // =====================================================================================
    // 活塞：**主机唯一模拟，客户端只表现**。
    //
    // 引擎事实（Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs）：
    //   · Update → ProcessQueuedActions（:176-231，每 0.125 游戏秒）→ MovePiston（:295-431）
    //     往 SubsystemMovingBlocks 里放一个 Id=="Piston" 的 MovingBlockSet，并 ChangeCell 清掉
    //     被移动的格子、把活塞改成"伸出"；
    //   · 运动过程由 SubsystemMovingBlocks.Update 插值，UpdateMovableBlocks（:233-270）给集合补活塞杆；
    //   · 结束（Stopped → StopPiston :433-469）才 DestroyCell / ChangeCell 落地 —— 走既有 CellChange 下发。
    //
    // 客户端不跑这段逻辑：它没有电路（SuSubsystemElectricity 已挡住），也绝不允许 StopPiston 的
    // DestroyCell 在本地写格。客户端只保留**表现**：
    //   · 本地副本集合（Id=="PistonReplica"，由 Engine/PistonReplicaEngine.cs 按主机消息创建）的
    //     活塞杆由本外壳每帧维护（只 SetBlock 集合，不写格）；
    //   · 下载世界里可能残留主机"移动到一半"的集合（Id=="Piston"）：同样只维护杆，到点后直接
    //     移除（绝不调用 StopPiston）。
    // =====================================================================================

    public sealed class SuSubsystemPistonBlockBehavior : SubsystemPistonBlockBehavior, IUpdateable
    {
        /// <summary>同一类问题最短 10s 一条（错误都带累计次数，不静默）。</summary>
        private const long ProblemLogMs = 10000;

        /// <summary>主机侧跟踪中的运动集合（活塞格 → 集合 + 目标位置）。</summary>
        private sealed class TrackedMove
        {
            public IMovingBlockSet Set;
            public Vector3 Target;
        }

        private readonly Dictionary<Point3, TrackedMove> m_tracked =
            new Dictionary<Point3, TrackedMove>();
        private readonly List<IMovingBlockSet> m_scratch = new List<IMovingBlockSet>();
        private readonly List<Point3> m_ended = new List<Point3>();
        private readonly MovingBlockSetMotion m_motion = new MovingBlockSetMotion();

        private SubsystemMovingBlocks m_movingBlocks;
        private PistonReplicaEngine m_replica;
        private long m_motionFieldFailures;
        private long m_missingCellFailures;
        private long m_invalidMoveFailures;
        private long m_emitFailures;
        private long m_rodFailures;
        private long m_lastProblemLogMs;
        private long m_lastErrorLogMs;
        private long m_clientActionQueueFieldFailures;
        private bool m_loaded;
        /// <summary>base 的两个私有写格订阅是否已摘（false = 已 Error 点名，客户端每帧重试）。</summary>
        private bool m_clientBaseHandlersDetached;

        /// <summary>活塞 base 订阅摘除的每帧重试计数与放弃闸门（同塌落外壳，防反射风暴）。</summary>
        private long m_clientBaseDetachAttempts;
        private bool m_clientBaseDetachAbandoned;
        private const long ClientBaseDetachMaxAttempts = 300;
        private bool m_clientStateMissingLogged;
        /// <summary>防护窗口外壳是否缺失（只 Error 一次；缺失 ≠ 静默：事件摘除仍生效）。</summary>
        private bool m_guardShellMissingLogged;

        /// <summary>
        /// 主机运动检测挂接点（实现 = Engine/PistonReplicaEngine.cs，组合根适配器在 Pump 里写入）。
        /// **实例字段，无静态状态。** 为 null = 适配器还没挂上：主机照旧跑 base.Update，
        /// 只是不广播（客户端没有中间过程；最终格仍由 CellChange 同步）。
        /// </summary>
        public PistonReplicaEngine Replica
        {
            get => m_replica;
            set => m_replica = value;
        }

        void IUpdateable.Update(float dt)
        {
            SubsystemTerrain terrain = SubsystemTerrain;
            if (!ReplicaGates.Allow(terrain, ReplicaMutationSource.Piston))
            {
                // 客户端：绝不 base.Update（ProcessQueuedActions / MovePiston / StopPiston 都会写格）。
                EnsureClientBaseHandlersDetached();
                UpdateClientPresentation(terrain);
                return;
            }
            PistonReplicaEngine replica = m_replica;
            SubsystemMovingBlocks moving = MovingBlocks;
            if (replica == null || moving == null)
            {
                base.Update(dt);
                return;
            }
            base.Update(dt);
            // ① 不在跟踪表里的 Piston 集合 = 一次运动开始。
            //    按"是否已跟踪"而不是"这一帧新出现"：读档时存档里就带着的主机中途集合
            //    （SubsystemMovingBlocks.Save 会保存 MovingBlockSets）也必须补发，否则客户端
            //    下载到"移动到一半"的世界后永远看不到剩下的行程。
            m_scratch.Clear();
            for (int i = 0; i < moving.MovingBlockSets.Count; i++)
            {
                IMovingBlockSet set = moving.MovingBlockSets[i];
                if (set == null || set.Id != PistonReplicaPresentation.EnginePistonId) continue;
                if (!(set.Tag is Point3 position)) continue;
                if (m_tracked.TryGetValue(position, out TrackedMove tracked) &&
                    ReferenceEquals(tracked.Set, set)) continue;
                m_scratch.Add(set);
            }
            for (int i = 0; i < m_scratch.Count; i++) EmitStarted(replica, terrain, m_scratch[i]);
            // ③ 跟踪中的集合已经不在列表里 = 引擎把它移除了（StopPiston / 拆除 / 区块卸载）。
            if (m_tracked.Count > 0) EmitEnded(replica, moving);
        }

        public override void OnChunkDiscarding(TerrainChunk chunk)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Piston))
            {
                // 客户端：区块卸载时只移除**纯表现**集合；绝不调用 StopPiston（它会 DestroyCell）。
                RemovePresentationSetsInChunk(chunk);
                return;
            }
            base.OnChunkDiscarding(chunk);
        }

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_movingBlocks = base.Project.FindSubsystem<SubsystemMovingBlocks>(false);
            if (m_movingBlocks != null) m_movingBlocks.Stopped += OnPresentationStopped;
            m_loaded = true;
            // 客户端分支：base.Load 已把引擎的两个私有写格回调挂到共享的 SubsystemMovingBlocks 上。
            // 角色（ClientReplica）通常在 Load 之后才由地形模块写入，所以这里先尝试一次；
            // Update 的客户端分支每帧兜底（塌落外壳也会在本帧 MovingBlocks.Update 之前调用本方法）。
            EnsureClientBaseHandlersDetached();
        }

        private SubsystemMovingBlocks MovingBlocks
        {
            get
            {
                if (m_movingBlocks != null) return m_movingBlocks;
                m_movingBlocks = base.Project?.FindSubsystem<SubsystemMovingBlocks>(false);
                return m_movingBlocks;
            }
        }

        // ------------------------------------------------------------ 客户端：摘掉 base 的写格订阅

        /// <summary>
        /// 客户端：摘掉 base.Load 挂上的**引擎私有写格回调**，只保留本外壳的 OnPresentationStopped。
        /// Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:Load:165-174
        ///   m_subsystemMovingBlocks.Stopped += MovingBlocksStopped;
        ///   m_subsystemMovingBlocks.CollidedWithTerrain += MovingBlocksCollidedWithTerrain;
        /// 不摘的后果：下载世界里"移动到一半"的 Id=="Piston" 集合在客户端触发
        /// MovingBlocksStopped（排队）或 MovingBlocksCollidedWithTerrain（直接写格）→ 引擎
        /// StopPiston / ProcessQueuedActions 在本地落格（客户端禁止，最终格只收主机 CellChange）。
        /// 返回 false = 本帧未完成（不是客户端 / 尚未 Load / 反射失败已 Error 点名），下一帧重试。
        /// </summary>
        internal bool EnsureClientBaseHandlersDetached()
        {
            if (m_clientBaseHandlersDetached) return true;
            if (m_clientBaseDetachAbandoned) return false;
            if (!m_loaded) return false;
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return false;
            SubsystemMovingBlocks moving = MovingBlocks;
            if (moving == null)
            {
                NoteDetachFailure("SubsystemMovingBlocks 不可用");
                return false;
            }
            ClientEventDetachResult stopped = ClientReplicaEventDetach.DetachDeclaredHandlers(
                moving, "Stopped", typeof(SubsystemPistonBlockBehavior));
            ClientEventDetachResult collision = ClientReplicaEventDetach.DetachDeclaredHandlers(
                moving, "CollidedWithTerrain", typeof(SubsystemPistonBlockBehavior));
            bool ownStopped = ClientReplicaEventDetach.HasHandler(moving, "Stopped", this,
                "OnPresentationStopped");
            // base 可能已经排了停止动作（本帧摘除之前 / 上一轮摘除失败）：清空并计数，不留给 base.Update。
            DropClientBaseActionQueue();
            if (!stopped.Succeeded || !collision.Succeeded || !ownStopped)
            {
                string reason = !stopped.Succeeded
                    ? stopped.Failure
                    : !collision.Succeeded
                        ? collision.Failure
                        : "事件 'Stopped' 上找不到本外壳的 OnPresentationStopped 委托";
                NoteDetachFailure(reason);
                m_clientBaseDetachAttempts++;
                if (m_clientBaseDetachAttempts >= ClientBaseDetachMaxAttempts)
                {
                    m_clientBaseDetachAbandoned = true;
                    Log.Error("[SuMP] piston: 客户端 base 写格订阅连续摘除失败 " +
                        m_clientBaseDetachAttempts + " 次 → 停止每帧重试（明确失败，不再重试；" +
                        "原因见上一条 Error，需修反射/引擎版本差异）");
                }
                return false;
            }
            m_clientBaseHandlersDetached = true;
            int detached = stopped.Removed + collision.Removed;
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            if (shell == null)
            {
                if (!m_clientStateMissingLogged)
                {
                    m_clientStateMissingLogged = true;
                    Log.Error("[SuMP] piston: 客户端已摘掉 base 写格订阅（" + detached +
                        " 个），但 SubsystemTerrain 不是 SuSubsystemTerrain → 计数器无法登记" +
                        "（明确失败；闸门本身已生效）");
                }
            }
            else
            {
                shell.PistonState.NoteBaseSubscriptionsDetached(detached);
                ModLog.VerboseInfo("[SuMP] piston 客户端闸门：base 写格订阅已摘" +
                    "（Stopped=" + stopped.Removed + " CollidedWithTerrain=" + collision.Removed +
                    "，pistonBaseSubscriptionsDetached=" +
                    shell.PistonState.BaseSubscriptionsDetached +
                    "）· 本外壳 OnPresentationStopped 保留 · 残留 Piston 集合只移除不写格" +
                    (detached == 0
                        ? "（本次 0 个 = 共享事件已被塌落外壳兜底摘掉 / 本引擎版本未订阅）"
                        : ""));
            }
            if (moving is not SuSubsystemMovingBlocks && !m_guardShellMissingLogged)
            {
                m_guardShellMissingLogged = true;
                NoteError("SubsystemMovingBlocks 不是 SuSubsystemMovingBlocks → 客户端移动组 base " +
                    "写格防护窗口不可用（事件摘除仍在生效；检查组合根是否注册了 " +
                    "Game.SubsystemMovingBlocks）");
            }
            return true;
        }

        private void NoteDetachFailure(string reason)
        {
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            shell?.PistonState.NoteDetachFailure();
            NoteError("客户端摘不掉 base 写格订阅：" + reason +
                " → 本地活塞落地仍可能写格（明确失败，下一帧重试；失败累计 " +
                (shell != null ? shell.PistonState.DetachFailures : 0) + "）");
        }

        /// <summary>
        /// 客户端：base 的停止队列 m_actions 只是"下一次 base.Update 写格"的意图；客户端不跑
        /// base.Update，这里清空并计入 clientShellBaseWriteAttempts（不静默留着）。
        /// 字段读不到 = 引擎版本差异：点名 m_actions 告警（不假装清过）。
        /// </summary>
        private void DropClientBaseActionQueue()
        {
            object actions = ReplicaGates.GetField(this, "m_actions",
                typeof(SubsystemPistonBlockBehavior));
            if (actions == null)
            {
                m_clientActionQueueFieldFailures++;
                NoteProblem("客户端读不到 base 停止队列表 m_actions（字段缺失）→ 无法确认队列为空（累计 " +
                    m_clientActionQueueFieldFailures + "）");
                return;
            }
            if (actions is not IDictionary dictionary || dictionary.Count == 0) return;
            int pending = dictionary.Count;
            dictionary.Clear();
            NoteClientShellBaseWriteAttempt(pending, "base 停止队列 m_actions");
        }

        private void NoteClientShellBaseWriteAttempt(int count, string reason)
        {
            if (count <= 0) return;
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            if (shell == null)
            {
                NoteError("客户端拦下 base 写格路径（" + reason + "）" + count +
                    " 次，但 SubsystemTerrain 不是 SuSubsystemTerrain → 计数无法登记");
                return;
            }
            shell.PistonState.NoteClientShellBaseWriteAttempts(count);
            NoteProblem("客户端拦下 base 写格路径（" + reason + "）" + count +
                " 次；clientShellBaseWriteAttempts 累计 " +
                shell.PistonState.ClientShellBaseWriteAttempts);
        }

        /// <summary>
        /// 客户端：base.OnBlockRemoved 会对活塞 / 活塞头调 StopPiston（DestroyCell / ChangeCell）。
        /// 主机下发的 CellChange 落到本地时这个回调也会被调用；客户端只移除纯表现集合 + 计数。
        /// Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:OnBlockRemoved:103-149
        /// </summary>
        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.Piston))
            {
                int contents = Game.Terrain.ExtractContents(value);
                if (contents != PistonBlock.Index && contents != PistonHeadBlock.Index) return;
                NoteClientShellBaseWriteAttempt(1, "OnBlockRemoved");
                RemovePresentationSetNear(value, new Point3(x, y, z));
                return;
            }
            base.OnBlockRemoved(value, newValue, x, y, z);
        }

        private void RemovePresentationSetNear(int removedValue, Point3 cell)
        {
            Point3 anchor = cell;
            if (Game.Terrain.ExtractContents(removedValue) == PistonHeadBlock.Index)
            {
                int data = Game.Terrain.ExtractData(removedValue);
                Point3 point = CellFace.FaceToPoint3(PistonHeadBlock.GetFace(data));
                anchor = new Point3(cell.X - point.X, cell.Y - point.Y, cell.Z - point.Z);
            }
            RemovePresentationSetsAt(anchor);
        }

        /// <summary>只 RemoveMovingBlockSet（不写格）；倒序删除避免移动集合列表位移。</summary>
        private int RemovePresentationSetsAt(Point3 anchor)
        {
            SubsystemMovingBlocks moving = MovingBlocks;
            if (moving == null) return 0;
            int removed = 0;
            for (int i = moving.MovingBlockSets.Count - 1; i >= 0; i--)
            {
                IMovingBlockSet set = moving.MovingBlockSets[i];
                if (set == null) continue;
                if (set.Id != PistonReplicaPresentation.ReplicaId &&
                    set.Id != PistonReplicaPresentation.EnginePistonId) continue;
                if (!(set.Tag is Point3 tag) || tag != anchor) continue;
                moving.RemoveMovingBlockSet(set);
                removed++;
            }
            if (removed > 0)
            {
                SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
                shell?.PistonState.NoteStraySetsRemoved(removed);
                NoteProblem("客户端移除已拆活塞的纯表现集合 " + removed + " 条（不写格）；" +
                    "pistonStraySetsRemoved 累计 " +
                    (shell != null ? shell.PistonState.StraySetsRemoved : removed));
            }
            return removed;
        }

        // ------------------------------------------------------------ 主机：检测开始 / 结束

        /// <summary>
        /// 主机：一次运动刚刚开始。把引擎 AddMovingBlockSet 的**全部参数**读出来交给适配器
        /// （目标位置 / 速度 / 加速度 / 阻力 / 平滑度在私有嵌套类里，读不到时明确失败并计数）。
        /// </summary>
        private void EmitStarted(PistonReplicaEngine replica, SubsystemTerrain terrain,
            IMovingBlockSet set)
        {
            if (!(set.Tag is Point3 position))
            {
                m_invalidMoveFailures++;
                NoteProblem("活塞集合的 Tag 不是 Point3 → 本次运动无法广播（累计 " +
                    m_invalidMoveFailures + "）");
                return;
            }
            int cellValue = terrain.Terrain.GetCellValue(position.X, position.Y, position.Z);
            if (Game.Terrain.ExtractContents(cellValue) != PistonBlock.Index)
            {
                m_missingCellFailures++;
                NoteProblem("活塞格 (" + position.X + "," + position.Y + "," + position.Z +
                    ") 不是活塞（contents=" + Game.Terrain.ExtractContents(cellValue) +
                    "）→ 本次运动不广播（累计 " + m_missingCellFailures + "）");
                return;
            }
            if (!m_motion.TryRead(set, out Vector3 target, out float speed, out float acceleration,
                    out float drag, out Vector2 smoothness, out string motionReason))
            {
                m_motionFieldFailures++;
                NoteProblem("读不到引擎 MovingBlockSet 的运动参数（" + motionReason +
                    "）→ 本次运动不广播（客户端看不到这一段；累计 " + m_motionFieldFailures + "）");
                return;
            }
            int data = Game.Terrain.ExtractData(cellValue);
            PistonMode mode = PistonBlock.GetMode(data);
            int face = PistonBlock.GetFace(data);
            ReadOnlyList<MovingBlock> blocks = set.Blocks;
            var blockData = new PistonBlockData[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
                blockData[i] = new PistonBlockData(blocks[i].Offset, blocks[i].Value);
            var move = new PistonMoveData(position.X, position.Y, position.Z, cellValue,
                (int)mode, face, set.Position, target, speed, acceleration, drag, smoothness,
                blockData);
            if (!PistonMoveCodec.TryValidateMove(in move, out string invalid))
            {
                m_invalidMoveFailures++;
                NoteProblem("主机活塞运动参数不合法（" + invalid + "）：" + move.Describe() +
                    " → 本次不广播（累计 " + m_invalidMoveFailures + "）");
                return;
            }
            if (m_tracked.TryGetValue(position, out TrackedMove previous) &&
                !ReferenceEquals(previous.Set, set))
            {
                // 同一活塞上一条集合还没登记结束就被换掉了（引擎不该发生）：先把旧的按中断结束，
                // 否则客户端会同时持有两条动画。
                m_tracked.Remove(position);
                EmitEnd(replica, position, IsAtTarget(previous.Set.Position, previous.Target),
                    "replaced");
            }
            m_tracked[position] = new TrackedMove { Set = set, Target = target };
            try
            {
                replica.OnHostMoveStarted(in move);
            }
            catch (Exception e)
            {
                m_emitFailures++;
                NoteProblem("活塞运动开始上报异常（" + e.GetType().Name + " " + e.Message +
                    "）；累计 " + m_emitFailures);
            }
        }

        /// <summary>
        /// 主机：跟踪中的集合已经不在 <c>MovingBlockSets</c> 里 —— 引擎通过 StopPiston 把它移除了。
        /// completed = 集合真的走到了 TargetPosition；false = 碰撞 / 拆除 / 区块卸载等提前结束。
        /// Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:433-469 StopPiston
        /// </summary>
        private void EmitEnded(PistonReplicaEngine replica, SubsystemMovingBlocks moving)
        {
            m_ended.Clear();
            foreach (KeyValuePair<Point3, TrackedMove> pair in m_tracked)
            {
                bool present = false;
                for (int i = 0; i < moving.MovingBlockSets.Count; i++)
                {
                    if (!ReferenceEquals(moving.MovingBlockSets[i], pair.Value.Set)) continue;
                    present = true;
                    break;
                }
                if (!present) m_ended.Add(pair.Key);
            }
            for (int i = 0; i < m_ended.Count; i++)
            {
                Point3 cell = m_ended[i];
                TrackedMove tracked = m_tracked[cell];
                m_tracked.Remove(cell);
                bool completed = IsAtTarget(tracked.Set.Position, tracked.Target);
                EmitEnd(replica, cell, completed, completed ? "target" : "early");
            }
        }

        /// <summary>
        /// 主机：上报一次运动结束。StartEventId 先留 0 —— 真正的 event_id 由模块在发送时按
        /// "这条运动的开始批次"填（PistonSyncModule.OnHostMoveEnded 用 m_sentStartIds 补），
        /// 这里只是引擎事实。
        /// </summary>
        private void EmitEnd(PistonReplicaEngine replica, Point3 cell, bool completed, string reason)
        {
            var end = new PistonMoveEndData(cell.X, cell.Y, cell.Z, completed, reason, 0);
            try
            {
                replica.OnHostMoveEnded(in end);
            }
            catch (Exception e)
            {
                m_emitFailures++;
                NoteProblem("活塞运动结束上报异常（" + e.GetType().Name + " " + e.Message +
                    "）；累计 " + m_emitFailures);
            }
        }

        private static bool IsAtTarget(Vector3 position, Vector3 target) =>
            Math.Abs(position.X - target.X) <= 1e-4f &&
            Math.Abs(position.Y - target.Y) <= 1e-4f &&
            Math.Abs(position.Z - target.Z) <= 1e-4f;

        // ------------------------------------------------------------ 客户端：只表现

        /// <summary>
        /// 客户端：维护本地集合的活塞杆（只 <c>SetBlock</c>，绝不写格）。集合本身由适配器创建 /
        /// 移除；这里只负责"杆在伸长 / 收回"的中间外观。
        /// </summary>
        private void UpdateClientPresentation(SubsystemTerrain terrain)
        {
            SubsystemMovingBlocks moving = MovingBlocks;
            if (moving == null || terrain?.Terrain == null) return;
            m_scratch.Clear();
            for (int i = 0; i < moving.MovingBlockSets.Count; i++)
            {
                IMovingBlockSet set = moving.MovingBlockSets[i];
                if (set == null) continue;
                if (set.Id != PistonReplicaPresentation.ReplicaId &&
                    set.Id != PistonReplicaPresentation.EnginePistonId) continue;
                m_scratch.Add(set);
            }
            for (int i = 0; i < m_scratch.Count; i++)
            {
                if (PistonReplicaPresentation.UpdateRod(terrain, m_scratch[i], out string reason))
                    continue;
                // "not-piston" 是主机格变化还没到时的正常瞬时状态（下一帧就好），不算失败。
                if (reason == "not-piston") continue;
                m_rodFailures++;
                NoteProblem("活塞杆表现跳过（" + reason + "）；累计 " + m_rodFailures);
            }
        }

        /// <summary>
        /// 客户端：下载世界里残留的主机中途集合（Id=="Piston"）到点后只移除表现对象。
        /// 引擎自己的 StopPiston 会 DestroyCell / ChangeCell —— 客户端绝不走它。
        /// </summary>
        private void OnPresentationStopped(IMovingBlockSet set)
        {
            if (set == null) return;
            if (!ReplicaGates.IsClient(SubsystemTerrain)) return;
            if (!string.Equals(set.Id, PistonReplicaPresentation.EnginePistonId,
                    StringComparison.Ordinal)) return;
            SubsystemMovingBlocks moving = MovingBlocks;
            if (moving == null) return;
            moving.RemoveMovingBlockSet(set);
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            shell?.PistonState.NoteStraySetsRemoved(1);
            NoteProblem("客户端移除下载世界里残留的活塞中途集合（不写格，最终格取主机 CellChange）；" +
                "pistonStraySetsRemoved 累计 " +
                (shell != null ? shell.PistonState.StraySetsRemoved : 1));
        }

        /// <summary>客户端：区块卸载 → 只移除纯表现集合（绝不 StopPiston / DestroyCell）。</summary>
        private void RemovePresentationSetsInChunk(TerrainChunk chunk)
        {
            SubsystemMovingBlocks moving = MovingBlocks;
            if (moving == null || chunk == null) return;
            var box = new BoundingBox(chunk.BoundingBox.Min - new Vector3(16f),
                chunk.BoundingBox.Max + new Vector3(16f));
            var result = new DynamicArray<IMovingBlockSet>();
            moving.FindMovingBlocks(box, extendToFillCells: false, result);
            int removed = 0;
            int engineSets = 0;
            for (int i = 0; i < result.Count; i++)
            {
                IMovingBlockSet set = result.Array[i];
                if (set == null) continue;
                if (set.Id != PistonReplicaPresentation.ReplicaId &&
                    set.Id != PistonReplicaPresentation.EnginePistonId) continue;
                if (set.Id == PistonReplicaPresentation.EnginePistonId) engineSets++;
                moving.RemoveMovingBlockSet(set);
                removed++;
            }
            if (removed <= 0) return;
            SuSubsystemTerrain shell = SubsystemTerrain as SuSubsystemTerrain;
            shell?.PistonState.NoteStraySetsRemoved(removed);
            if (engineSets > 0)
                shell?.PistonState.NoteClientShellBaseWriteAttempts(engineSets);
            NoteProblem("客户端区块卸载 → 移除 " + removed + " 条纯表现活塞集合（不写格；" +
                "其中 base 会落地写格的 Piston 集合 " + engineSets + " 条已拦下）；" +
                "pistonStraySetsRemoved 累计 " +
                (shell != null ? shell.PistonState.StraySetsRemoved : removed) +
                " clientShellBaseWriteAttempts 累计 " +
                (shell != null ? shell.PistonState.ClientShellBaseWriteAttempts : engineSets));
        }

        private void NoteProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemLogMs != 0 && nowMs - m_lastProblemLogMs < ProblemLogMs) return;
            m_lastProblemLogMs = nowMs;
            Log.Warning("[SuMP] piston: " + message);
        }

        /// <summary>"摘不掉 base 写格订阅"是明确失败，用 Error 单独限流（不与普通表现告警混在一起）。</summary>
        private void NoteError(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastErrorLogMs != 0 && nowMs - m_lastErrorLogMs < ProblemLogMs) return;
            m_lastErrorLogMs = nowMs;
            Log.Error("[SuMP] piston: " + message);
        }
    }

    // =====================================================================================
    // 移动组驱动：Game.SubsystemMovingBlocks（**item 3 的防御性兜底**）
    //
    // 引擎事实：
    //   · SubsystemMovingBlocks.Update:277-353 插值后调 TerrainCollision:508-535，
    //     对每个碰撞格 Invoke(CollidedWithTerrain)；
    //   · 订阅者 SubsystemPistonBlockBehavior.MovingBlocksCollidedWithTerrain:471-497 与
    //     SubsystemCollapsingBlockBehavior.MovingBlocksCollidedWithTerrain:40-54 直接调
    //     SubsystemTerrain.DestroyCell / ChangeCell（:229 / :278，**都不是虚方法**）。
    // 正常路径：Piston / Collapsing 外壳在客户端把这两个私有委托从事件上摘掉
    // （见 SuSubsystemPistonBlockBehavior.EnsureClientBaseHandlersDetached / ClientReplicaEventDetach）。
    // 万一摘除失败 / 将来引擎改事件名：本外壳在客户端给 base.Update 套一个写格窗口
    // （ClientMovingBlockWriteGuard），窗口内被移动组 base 回调改掉的格恢复原值 + 移出本地编辑捕获
    // + 计入 clientShellBaseWriteAttempts / clientBasePistonWriteAttempt。客户端绝不把这类写入提交给主机。
    //
    // 主机 / 单机：直接 base.Update（一次 IsClient 判断，无窗口 / 无额外分配）。
    // 无静态状态：窗口实例挂在本子系统实例上。
    // =====================================================================================

    public sealed class SuSubsystemMovingBlocks : SubsystemMovingBlocks, IUpdateable
    {
        private const long GuardProblemLogMs = 10000;

        private readonly ClientMovingBlockWriteGuard m_clientWriteGuard =
            new ClientMovingBlockWriteGuard();
        private readonly List<ClientMovingBlockSetRef> m_guardSets =
            new List<ClientMovingBlockSetRef>(4);

        private SubsystemTerrain m_terrain;
        private long m_lastGuardProblemLogMs;
        private long m_guardSkippedTotal;
        private bool m_missingShellLogged;

        void IUpdateable.Update(float dt)
        {
            SubsystemTerrain terrain = m_terrain ??
                (m_terrain = base.Project?.FindSubsystem<SubsystemTerrain>(false));
            if (!ReplicaGates.IsClient(terrain) || terrain.Terrain == null)
            {
                base.Update(dt);
                return;
            }
            SuSubsystemTerrain shell = terrain as SuSubsystemTerrain;
            if (shell == null)
            {
                if (!m_missingShellLogged)
                {
                    m_missingShellLogged = true;
                    Log.Error("[SuMP] movingBlocks: SubsystemTerrain 不是 SuSubsystemTerrain → " +
                        "客户端移动组 base 写格防护窗口不可用（明确失败；活塞/塌落外壳的事件摘除仍在生效）");
                }
                base.Update(dt);
                return;
            }
            BeginClientWriteGuard(shell);
            try
            {
                base.Update(dt);
            }
            finally
            {
                m_clientWriteGuard.End(shell);
            }
        }

        /// <summary>
        /// 客户端：快照所有"引擎 base 回调可能写格"的移动组（Id=="Piston" / "CollapsingBlock"）
        /// 当前包围盒（外扩 2 格）。只读集合，不写格。
        /// </summary>
        private void BeginClientWriteGuard(SuSubsystemTerrain shell)
        {
            m_guardSets.Clear();
            IReadOnlyList<IMovingBlockSet> sets = MovingBlockSets;
            for (int i = 0; i < sets.Count; i++)
            {
                IMovingBlockSet set = sets[i];
                if (set == null) continue;
                string source;
                if (string.Equals(set.Id, PistonReplicaPresentation.EnginePistonId,
                        StringComparison.Ordinal))
                    source = ClientMovingBlockWriteGuard.PistonSource;
                else if (string.Equals(set.Id, "CollapsingBlock", StringComparison.Ordinal))
                    // Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs:IdString "CollapsingBlock"
                    source = ClientMovingBlockWriteGuard.CollapsingSource;
                else
                    continue;
                ReadOnlyList<MovingBlock> blocks = set.Blocks;
                if (blocks.Count == 0) continue;
                Point3 min = blocks[0].Offset;
                Point3 max = min;
                for (int b = 1; b < blocks.Count; b++)
                {
                    Point3 offset = blocks[b].Offset;
                    min = Point3.Min(min, offset);
                    max = Point3.Max(max, offset);
                }
                m_guardSets.Add(new ClientMovingBlockSetRef(source, set.Position, min, max));
            }
            if (m_guardSets.Count == 0) return;
            m_clientWriteGuard.Begin(m_guardSets, shell);
            if (m_clientWriteGuard.LastSkippedSets <= 0) return;
            m_guardSkippedTotal += m_clientWriteGuard.LastSkippedSets;
            NoteGuardProblem("移动组几何超出防护窗口上限 → " +
                m_clientWriteGuard.LastSkippedSets + " 条未快照（累计 " + m_guardSkippedTotal +
                "；这些集合的 base 写格未被窗口兜底，事件摘除仍然是主防线）");
        }

        private void NoteGuardProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastGuardProblemLogMs != 0 &&
                nowMs - m_lastGuardProblemLogMs < GuardProblemLogMs) return;
            m_lastGuardProblemLogMs = nowMs;
            Log.Warning("[SuMP] movingBlocks: " + message);
        }
    }

}

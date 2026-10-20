using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// <c>SubsystemTruthTableCircuitBlockBehavior</c> 的注入外壳（新文件）。
    ///
    /// 为什么必须替换行为本身：引擎的编辑入口在
    /// <c>ComponentGui.Update</c> → <c>SubsystemBlockBehavior.OnEditBlock</c>，
    /// 而对话框 OK 的提交回调里直接调 <c>SetBlockData</c>（在运行该行为的一侧）。
    /// 客户端副本上这次写入只改本地 —— 主机永远收不到（真实缺陷）。派生外壳覆盖
    /// <c>OnEditBlock</c> 是**唯一**能同时保留原生对话框与拦截提交的入口
    /// （ComponentGui / 对话框都不是可替换的注入点）。
    ///
    /// 三种角色：
    ///   · 主机 / 单机：对话框照旧，提交走 <see cref="EditableBlockBridge.TryApplyHostLocalEdit"/>
    ///     → 权威内容落地（SetBlockData + 排队电路模拟），端口再触发广播给附近客户端；
    ///   · 客户端副本：对话框改的是**副本的复制品**，提交只发请求
    ///     （<see cref="EditableBlockBridge.SubmitClientEdit"/>），**绝不 SetBlockData**；
    ///   · 没有桥（mod 未接线）的客户端副本：明确拒绝打开对话框（不本地写），计数 + 限流日志。
    ///
    /// Source: Survivalcraft/Game/SubsystemTruthTableCircuitBlockBehavior.cs:38-53
    /// Source: Survivalcraft/Game/EditTruthTableDialog.cs:89-97（OK 时写 m_truthTableData.Data）
    /// </summary>
    public sealed class SuSubsystemTruthTableCircuitBlockBehavior :
        SubsystemTruthTableCircuitBlockBehavior, IEditableBlockContentHost
    {
        private const int Contents = 188;
        private const int ProblemLogMs = 10000;

        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public int EditableKind => EditableKinds.TruthTable;

        private EditableBlockBridge Bridge =>
            (SubsystemTerrain as SuSubsystemTerrain)?.Bridge?.EditableBlocks;

        public override bool OnEditBlock(int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            EditableBlockBridge bridge = Bridge;
            if (bridge == null)
            {
                if (ReplicaGates.IsClient(SubsystemTerrain))
                {
                    LogProblem("no-bridge",
                        "客户端副本没有可编辑内容桥 → 拒绝打开真值表对话框（不本地写副本内容）");
                    return true;
                }
                return base.OnEditBlock(x, y, z, value, componentPlayer);
            }
            return BeginEdit(bridge, x, y, z, value, componentPlayer);
        }

        /// <summary>打开对话框；客户端改副本复制品，主机改权威内容。永远不在这里 SetBlockData。</summary>
        private bool BeginEdit(EditableBlockBridge bridge, int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            if (componentPlayer == null) return true;
            int contents = Game.Terrain.ExtractContents(value);
            if (contents != Contents)
            {
                LogProblem("contents:" + contents, "真值表编辑入口的 contents=" + contents +
                    " 不是 " + Contents + " → 不打开对话框");
                return true;
            }
            var point = new Point3(x, y, z);
            TruthTableData current = GetBlockData(point) ?? new TruthTableData();
            byte[] baseData = EditableEngineContent.EncodeTruthTable(current);
            int baseRevision = bridge.GetClientRevision(EditableKinds.TruthTable, x, y, z);
            var draft = (TruthTableData)current.Copy();
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditTruthTableDialog(draft, delegate (bool accepted)
                {
                    // 取消：什么都没发生（副本从未被改）。
                    if (!accepted) return;
                    byte[] desired = EditableEngineContent.EncodeTruthTable(draft);
                    Submit(bridge, x, y, z, value, contents, baseRevision, baseData, desired);
                }));
            return true;
        }

        /// <summary>对话框提交：客户端发请求；主机 / 单机直接落权威内容。两条路都不在此写副本。</summary>
        private void Submit(EditableBlockBridge bridge, int x, int y, int z, int value,
            int contents, int baseRevision, byte[] baseData, byte[] desired)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                if (!bridge.SubmitClientEdit(EditableKinds.TruthTable, x, y, z, contents,
                        baseRevision, baseData, desired, out string reason))
                {
                    bridge.NotifyEditRejected(EditableKinds.TruthTable, x, y, z, reason);
                }
                return;
            }
            if (!bridge.TryApplyHostLocalEdit(EditableKinds.TruthTable, x, y, z, value, desired,
                    out _, out string hostReason))
            {
                bridge.NotifyEditRejected(EditableKinds.TruthTable, x, y, z, hostReason);
            }
        }

        // ================================================================= IEditableBlockContentHost

        public bool CaptureContent(int x, int y, int z, out byte[] data, out string reason)
        {
            reason = null;
            TruthTableData current = GetBlockData(new Point3(x, y, z));
            data = EditableEngineContent.EncodeTruthTable(current ?? new TruthTableData());
            return true;
        }

        public bool ApplyContent(int x, int y, int z, int contents, byte[] data,
            bool hostAuthoritative, out string reason)
        {
            reason = null;
            if (contents != Contents)
            {
                reason = "contents=" + contents + " 不是真值表（" + Contents + "）";
                return false;
            }
            if (!EditableEngineContent.TryDecodeTruthTable(data, out TruthTableData decoded,
                    out reason))
            {
                return false;
            }
            SetBlockData(new Point3(x, y, z), decoded);
            if (hostAuthoritative) QueueHostSimulation(x, y, z);
            return true;
        }

        public void DropContent(int x, int y, int z) => SetBlockData(new Point3(x, y, z), null);

        /// <summary>
        /// 主机侧复刻引擎提交回调里的"电路重新排队"（客户端不调：客户端电力已闸门，
        /// 排队只会让引擎私有队列越积越多）。Source: SubsystemTruthTableCircuitBlockBehavior.cs:44-50
        /// </summary>
        private void QueueHostSimulation(int x, int y, int z)
        {
            try
            {
                SubsystemTerrain terrain = SubsystemTerrain;
                if (terrain?.Terrain == null) return;
                int value = terrain.Terrain.GetCellValue(x, y, z);
                if (Game.Terrain.ExtractContents(value) != Contents) return;
                if (BlocksManager.Blocks == null || Contents >= BlocksManager.Blocks.Length) return;
                var block = BlocksManager.Blocks[Contents] as TruthTableCircuitBlock;
                if (block == null)
                {
                    LogProblem("queue-block", "contents=" + Contents +
                        " 不是 TruthTableCircuitBlock（" + BlocksManager.Blocks[Contents]?.GetType().Name +
                        "）→ 内容已落地，但没排队电路模拟");
                    return;
                }
                int face = block.GetFace(value);
                SubsystemElectricity electricity = Project?.FindSubsystem<SubsystemElectricity>(false);
                if (electricity == null)
                {
                    LogProblem("queue-electricity", "找不到 SubsystemElectricity → 真值表内容已落地，" +
                        "但没排队电路模拟（电路要等下一次自然求解）");
                    return;
                }
                ElectricElement element = electricity.GetElectricElement(x, y, z, face);
                if (element != null)
                    electricity.QueueElectricElementForSimulation(element,
                        electricity.CircuitStep + 1);
            }
            catch (Exception e)
            {
                LogProblem("queue-fault", "真值表电路重新排队异常（" + e.GetType().Name + " " +
                    e.Message + "）→ 内容已落地，电路可能晚一拍");
            }
        }

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            Log.Warning("[SuMP] 真值表：" + message);
        }
    }

    /// <summary>
    /// <c>SubsystemMemoryBankBlockBehavior</c> 的注入外壳（新文件）。
    /// 与真值表外壳同一条纪律；内容上界为 256 个 nibble + LastOutput（257 字节），
    /// 不需要分片（见 Net/EditableNet.cs:EditableKinds）。
    ///
    /// Source: Survivalcraft/Game/SubsystemMemoryBankBlockBehavior.cs:38-53
    /// Source: Survivalcraft/Game/EditMemoryBankDialog.cs（OK 时写 m_memoryBankData.Data）
    /// </summary>
    public sealed class SuSubsystemMemoryBankBlockBehavior :
        SubsystemMemoryBankBlockBehavior, IEditableBlockContentHost
    {
        private const int Contents = 186;
        private const int ProblemLogMs = 10000;

        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public int EditableKind => EditableKinds.MemoryBank;

        private EditableBlockBridge Bridge =>
            (SubsystemTerrain as SuSubsystemTerrain)?.Bridge?.EditableBlocks;

        public override bool OnEditBlock(int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            EditableBlockBridge bridge = Bridge;
            if (bridge == null)
            {
                if (ReplicaGates.IsClient(SubsystemTerrain))
                {
                    LogProblem("no-bridge",
                        "客户端副本没有可编辑内容桥 → 拒绝打开内存条对话框（不本地写副本内容）");
                    return true;
                }
                return base.OnEditBlock(x, y, z, value, componentPlayer);
            }
            return BeginEdit(bridge, x, y, z, value, componentPlayer);
        }

        private bool BeginEdit(EditableBlockBridge bridge, int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            if (componentPlayer == null) return true;
            int contents = Game.Terrain.ExtractContents(value);
            if (contents != Contents)
            {
                LogProblem("contents:" + contents, "内存条编辑入口的 contents=" + contents +
                    " 不是 " + Contents + " → 不打开对话框");
                return true;
            }
            var point = new Point3(x, y, z);
            MemoryBankData current = GetBlockData(point) ?? new MemoryBankData();
            byte[] baseData = EditableEngineContent.EncodeMemoryBank(current);
            int baseRevision = bridge.GetClientRevision(EditableKinds.MemoryBank, x, y, z);
            var draft = (MemoryBankData)current.Copy();
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditMemoryBankDialog(draft, delegate
                {
                    byte[] desired = EditableEngineContent.EncodeMemoryBank(draft);
                    Submit(bridge, x, y, z, value, contents, baseRevision, baseData, desired);
                }));
            return true;
        }

        private void Submit(EditableBlockBridge bridge, int x, int y, int z, int value,
            int contents, int baseRevision, byte[] baseData, byte[] desired)
        {
            if (ReplicaGates.IsClient(SubsystemTerrain))
            {
                if (!bridge.SubmitClientEdit(EditableKinds.MemoryBank, x, y, z, contents,
                        baseRevision, baseData, desired, out string reason))
                {
                    bridge.NotifyEditRejected(EditableKinds.MemoryBank, x, y, z, reason);
                }
                return;
            }
            if (!bridge.TryApplyHostLocalEdit(EditableKinds.MemoryBank, x, y, z, value, desired,
                    out _, out string hostReason))
            {
                bridge.NotifyEditRejected(EditableKinds.MemoryBank, x, y, z, hostReason);
            }
        }

        // ================================================================= IEditableBlockContentHost

        public bool CaptureContent(int x, int y, int z, out byte[] data, out string reason)
        {
            reason = null;
            MemoryBankData current = GetBlockData(new Point3(x, y, z));
            data = EditableEngineContent.EncodeMemoryBank(current ?? new MemoryBankData());
            return true;
        }

        public bool ApplyContent(int x, int y, int z, int contents, byte[] data,
            bool hostAuthoritative, out string reason)
        {
            reason = null;
            if (contents != Contents)
            {
                reason = "contents=" + contents + " 不是内存条（" + Contents + "）";
                return false;
            }
            if (!EditableEngineContent.TryDecodeMemoryBank(data, out MemoryBankData decoded,
                    out reason))
            {
                return false;
            }
            SetBlockData(new Point3(x, y, z), decoded);
            if (hostAuthoritative) QueueHostSimulation(x, y, z);
            return true;
        }

        public void DropContent(int x, int y, int z) => SetBlockData(new Point3(x, y, z), null);

        /// <summary>Source: SubsystemMemoryBankBlockBehavior.cs:44-50（与真值表同一套排队逻辑）。</summary>
        private void QueueHostSimulation(int x, int y, int z)
        {
            try
            {
                SubsystemTerrain terrain = SubsystemTerrain;
                if (terrain?.Terrain == null) return;
                int value = terrain.Terrain.GetCellValue(x, y, z);
                if (Game.Terrain.ExtractContents(value) != Contents) return;
                if (BlocksManager.Blocks == null || Contents >= BlocksManager.Blocks.Length) return;
                var block = BlocksManager.Blocks[Contents] as MemoryBankBlock;
                if (block == null)
                {
                    LogProblem("queue-block", "contents=" + Contents +
                        " 不是 MemoryBankBlock（" + BlocksManager.Blocks[Contents]?.GetType().Name +
                        "）→ 内容已落地，但没排队电路模拟");
                    return;
                }
                int face = block.GetFace(value);
                SubsystemElectricity electricity = Project?.FindSubsystem<SubsystemElectricity>(false);
                if (electricity == null)
                {
                    LogProblem("queue-electricity", "找不到 SubsystemElectricity → 内存条内容已落地，" +
                        "但没排队电路模拟（电路要等下一次自然求解）");
                    return;
                }
                ElectricElement element = electricity.GetElectricElement(x, y, z, face);
                if (element != null)
                    electricity.QueueElectricElementForSimulation(element,
                        electricity.CircuitStep + 1);
            }
            catch (Exception e)
            {
                LogProblem("queue-fault", "内存条电路重新排队异常（" + e.GetType().Name + " " +
                    e.Message + "）→ 内容已落地，电路可能晚一拍");
            }
        }

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            Log.Warning("[SuMP] 内存条：" + message);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 一格可编辑方块内容的**引擎宿主**（实现 = Engine/EditableBlockSubsystems.cs 的两个外壳）。
    /// 端口只按种类找到宿主，由宿主自己读写 <c>SubsystemEditableItemBehavior&lt;T&gt;</c> 的方块数据。
    /// 这样端口不需要知道 T，也不需要反射私有字典。
    /// </summary>
    public interface IEditableBlockContentHost
    {
        /// <summary>EditableKinds.TruthTable / MemoryBank。</summary>
        int EditableKind { get; }

        /// <summary>抓该格当前内容（没有数据 = 全零内容，不是失败）。</summary>
        bool CaptureContent(int x, int y, int z, out byte[] data, out string reason);

        /// <summary>
        /// 把内容写进本方行为的数据表。
        /// <paramref name="hostAuthoritative"/> = true：主机权威路径（写数据 + 排队电路模拟）；
        /// false：客户端副本（只写数据；客户端不模拟电路）。
        /// </summary>
        bool ApplyContent(int x, int y, int z, int contents, byte[] data,
            bool hostAuthoritative, out string reason);

        /// <summary>丢掉该格的副本内容（主机已判定方块不存在）。</summary>
        void DropContent(int x, int y, int z);
    }

    /// <summary>
    /// 引擎内容 &lt;-&gt; 线上字节（无反射；只用两个引擎数据类型的公开字段）。
    ///
    /// 布局：
    ///   · 真值表：16 字节，每字节一个 nibble（0..15），顺序 = TruthTableData.Data[0..15]；
    ///   · 内存条：257 字节 = 地址 0..255 的 nibble（Data 不足 / 尾部零一律写 0）
    ///     + 第 257 字节 = LastOutput（0..15）。
    /// 引擎数据本身就是有界的（见 Net/EditableNet.cs:EditableKinds），所以没有分片路径。
    /// </summary>
    public static class EditableEngineContent
    {
        public static byte[] EncodeTruthTable(TruthTableData data)
        {
            var bytes = new byte[EditableKinds.DataBytes(EditableKinds.TruthTable)];
            if (data?.Data == null) return bytes;
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)MathUtils.Clamp(data.Data[i], 0, 15);
            return bytes;
        }

        public static bool TryDecodeTruthTable(byte[] bytes, out TruthTableData data,
            out string reason)
        {
            data = null;
            if (!EditableCodec.ValidateContent(EditableKinds.TruthTable, bytes, "data",
                    out reason))
                return false;
            var decoded = new TruthTableData();
            for (int i = 0; i < decoded.Data.Length; i++) decoded.Data[i] = bytes[i];
            data = decoded;
            return true;
        }

        public static byte[] EncodeMemoryBank(MemoryBankData data)
        {
            var bytes = new byte[EditableKinds.DataBytes(EditableKinds.MemoryBank)];
            int nibbles = EditableKinds.NibbleBytes(EditableKinds.MemoryBank);
            if (data?.Data != null)
            {
                int count = MathUtils.Min(data.Data.Count, nibbles);
                for (int i = 0; i < count; i++)
                    bytes[i] = (byte)MathUtils.Clamp(data.Data.Array[i], 0, 15);
                bytes[nibbles] = (byte)MathUtils.Clamp(data.LastOutput, 0, 15);
            }
            return bytes;
        }

        public static bool TryDecodeMemoryBank(byte[] bytes, out MemoryBankData data,
            out string reason)
        {
            data = null;
            if (!EditableCodec.ValidateContent(EditableKinds.MemoryBank, bytes, "data",
                    out reason))
                return false;
            var decoded = new MemoryBankData();
            int nibbles = EditableKinds.NibbleBytes(EditableKinds.MemoryBank);
            for (int i = 0; i < nibbles; i++)
            {
                if (bytes[i] == 0) continue;
                if (decoded.Data.Count <= i) decoded.Data.Count = i + 1;
                decoded.Data.Array[i] = bytes[i];
            }
            decoded.LastOutput = bytes[nibbles];
            data = decoded;
            return true;
        }
    }

    /// <summary>
    /// 可编辑方块内容同步的**引擎适配层**（新文件）。
    ///
    /// 客户端：
    ///   · 外壳（<see cref="SuSubsystemTruthTableCircuitBlockBehavior"/> /
    ///     <see cref="SuSubsystemMemoryBankBlockBehavior"/>）在 <c>OnEditBlock</c> 里把对话框
    ///     改成"改副本 + 提交请求"，本类负责 base revision / 内容编码 / 交给模块发送；
    ///   · 收到主机 EDITABLE_STATE → <see cref="ApplyEditableState"/> 写副本数据（不排队模拟）。
    ///
    /// 主机：
    ///   · <see cref="TryApplyEditableEdit"/> 校验请求者身份 / 距离 / 方块格 / 内容形状 /
    ///     起始内容仍是权威内容，然后由宿主外壳写权威数据并排队电路模拟；
    ///   · <see cref="TryApplyHostLocalEdit"/> 处理主机本机玩家的对话框提交，
    ///     成功后触发 <see cref="HostStateChanged"/>，由模块广播；
    ///   · <see cref="CollectEditableStateRecipients"/> 按角色距离收集接收者（远处跳过并计数）。
    ///
    /// 无静态状态；不使用反射；只读写公开 API 与 SubsystemEditableItemBehavior 的公开方法。
    /// </summary>
    public sealed class EditableBlockBridge : IEditableBlockPort
    {
        /// <summary>权威内容广播距离（格²）：超出 = 看不到该格，跳过并计数。</summary>
        private const float MaxBroadcastDistanceSq = 64f * 64f;
        /// <summary>每端 revision 表上限（超出整表清空并明确写一行；只影响 base 判定，不丢内容）。</summary>
        private const int MaxTrackedRevisions = 4096;
        private const int ProblemLogMs = 10000;

        private readonly struct CellKey : IEquatable<CellKey>
        {
            public CellKey(int x, int y, int z, int kind)
            {
                X = x;
                Y = y;
                Z = z;
                Kind = kind;
            }

            public int X { get; }
            public int Y { get; }
            public int Z { get; }
            public int Kind { get; }

            public bool Equals(CellKey other) =>
                X == other.X && Y == other.Y && Z == other.Z && Kind == other.Kind;

            public override bool Equals(object obj) => obj is CellKey other && Equals(other);

            public override int GetHashCode() => ((X * 397) ^ Y) * 397 ^ Z ^ (Kind * 31);

            public string Describe() => X + "," + Y + "," + Z + ":" + EditableKinds.Name(Kind);
        }

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly InteractionReachGate m_reachGate;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<CellKey, int> m_clientRevisions =
            new Dictionary<CellKey, int>();
        private readonly Dictionary<CellKey, int> m_hostRevisions = new Dictionary<CellKey, int>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private Project m_project;
        private bool m_clientReplica;
        private IEditableRequestSink m_sink;

        // ---------------------------------------------------------------- 计数
        private long m_clientSubmits;
        private long m_clientSubmitFailures;
        private long m_clientNotReplica;
        private long m_clientNotConnected;
        private long m_clientStatesApplied;
        private long m_clientStatesStale;
        private long m_clientDrops;
        private long m_clientNotifyRejected;
        private long m_clientNotifyMissing;
        private long m_hostApplied;
        private long m_hostRejected;
        private long m_hostLocalApplied;
        private long m_hostLocalRejected;
        private long m_hostCaptures;
        private long m_hostCaptureFailures;
        private long m_recipients;
        private long m_recipientsSkipped;
        private long m_recipientsNoAvatar;
        private long m_revisionClears;
        private long m_broadcastEvents;
        private long m_broadcastFaults;

        public EditableBlockBridge(NetworkAvatarSpawner spawner, IModuleDiagnostics diagnostics,
            string ownerId, InteractionReachGate reachGate)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_reachGate = reachGate ?? throw new ArgumentNullException(nameof(reachGate));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "editable-sync" : ownerId;
        }

        /// <summary>主机本机玩家的权威内容已变化（模块订阅后广播给附近客户端）。</summary>
        public event Action<EditableStateData> HostStateChanged;

        // ================================================================= IEditableBlockPort（生命周期）

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                m_project = project;
                ResetRevisions("project-change");
            }
            bool client = IsClientReplica(project);
            if (client != m_clientReplica)
            {
                m_clientReplica = client;
                ResetRevisions("role-change");
            }
        }

        public void SetRequestSink(IEditableRequestSink sink) => m_sink = sink;

        private void ResetRevisions(string reason)
        {
            int client = m_clientRevisions.Count;
            int host = m_hostRevisions.Count;
            m_clientRevisions.Clear();
            m_hostRevisions.Clear();
            if (client != 0 || host != 0)
                Log("可编辑内容 revision 表作废（" + reason + "）：client=" + client +
                    " host=" + host + "（之后按 base 内容重新判定，不把旧 revision 当当前值）");
        }

        private static bool IsClientReplica(Project project) =>
            project != null &&
            project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
            shell.ClientReplica;

        // ================================================================= 客户端

        public int GetClientRevision(int kind, int x, int y, int z)
        {
            return m_clientRevisions.TryGetValue(new CellKey(x, y, z, kind), out int revision)
                ? revision
                : 0;
        }

        public bool SubmitClientEdit(int kind, int x, int y, int z, int contents, int baseRevision,
            byte[] baseData, byte[] data, out string reason)
        {
            reason = null;
            if (!IsClientReplica(GameManager.Project))
            {
                m_clientNotReplica++;
                reason = "not-client-replica";
                LogProblem("submit-not-replica", "可编辑内容提交被拒：本端不是客户端副本（" +
                    EditableKinds.Name(kind) + " @" + x + "," + y + "," + z + "）");
                return false;
            }
            IEditableRequestSink sink = m_sink;
            if (sink == null)
            {
                m_clientNotConnected++;
                reason = "module-detached";
                LogProblem("submit-no-sink", "可编辑内容提交失败：同步模块没有挂接（" +
                    EditableKinds.Name(kind) + " @" + x + "," + y + "," + z +
                    "）→ 客户端不写本地副本（明确失败）");
                return false;
            }
            var request = new EditableEditRequestData(0L, x, y, z, contents, kind, baseRevision,
                baseData, data, hasReach: true, reach: EngineReach.LocalReach());
            if (!sink.TrySubmitBlockEdit(in request, out reason))
            {
                m_clientSubmitFailures++;
                return false;
            }
            m_clientSubmits++;
            return true;
        }

        public EditableStateApplyOutcome ApplyEditableState(EditableStateData state,
            out string reason)
        {
            reason = null;
            if (state == null)
            {
                reason = "empty-state";
                return EditableStateApplyOutcome.Failed;
            }
            if (!IsClientReplica(GameManager.Project))
            {
                reason = "not-client-replica";
                return EditableStateApplyOutcome.Failed;
            }
            var key = new CellKey(state.X, state.Y, state.Z, state.Kind);
            if (m_clientRevisions.TryGetValue(key, out int applied) &&
                state.Revision < applied)
            {
                m_clientStatesStale++;
                reason = "revision " + state.Revision + " < 已应用 " + applied;
                return EditableStateApplyOutcome.Stale;
            }
            IEditableBlockContentHost host = FindContentHost(GameManager.Project, state.Kind);
            if (host == null)
            {
                reason = "subsystem-missing: " + EditableKinds.Name(state.Kind) +
                    "（引擎注入未生效？）";
                return EditableStateApplyOutcome.Failed;
            }
            if (!host.ApplyContent(state.X, state.Y, state.Z, state.Contents, state.Data,
                    hostAuthoritative: false, out reason))
            {
                return EditableStateApplyOutcome.Failed;
            }
            m_clientRevisions[key] = state.Revision;
            m_clientStatesApplied++;
            return EditableStateApplyOutcome.Applied;
        }

        public void DropReplicaContent(int kind, int x, int y, int z, string reason)
        {
            var key = new CellKey(x, y, z, kind);
            m_clientRevisions.Remove(key);
            IEditableBlockContentHost host = FindContentHost(GameManager.Project, kind);
            if (host == null)
            {
                LogProblem("drop-no-host", "丢弃副本内容失败：找不到 " + EditableKinds.Name(kind) +
                    " 的副本子系统（@" + x + "," + y + "," + z + "，" + reason + "）");
                return;
            }
            host.DropContent(x, y, z);
            m_clientDrops++;
            LogProblem("drop:" + x + "," + y + "," + z, "已丢弃副本内容 " +
                EditableKinds.Name(kind) + " @" + x + "," + y + "," + z + "（" + reason + "）");
        }

        public void NotifyEditRejected(int kind, int x, int y, int z, string reason)
        {
            m_clientNotifyRejected++;
            LogProblem("notify-reject", "可编辑内容编辑未通过：" + reason + " @" + x + "," + y + "," +
                z + " · " + EditableKinds.Name(kind));
            Project project = GameManager.Project;
            if (project == null) return;
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData local, out string why))
            {
                m_clientNotifyMissing++;
                LogProblem("notify-no-player", "取不到本机玩家，无法显示编辑失败提示（" + why +
                    "）；原因已写入日志");
                return;
            }
            ComponentGui gui = local?.ComponentPlayer?.ComponentGui;
            if (gui == null)
            {
                m_clientNotifyMissing++;
                LogProblem("notify-no-gui", "本机玩家没有 ComponentGui，无法显示编辑失败提示；" +
                    "原因已写入日志");
                return;
            }
            string text = "编辑未通过：" + reason;
            if (text.Length > 120) text = text.Substring(0, 117) + "...";
            try
            {
                gui.DisplaySmallMessage(text, Color.White, blinking: false,
                    playNotificationSound: false);
            }
            catch (Exception e)
            {
                m_clientNotifyMissing++;
                LogProblem("notify-fault", "显示编辑失败提示异常（" + e.GetType().Name + " " +
                    e.Message + "）；原因已写入日志");
            }
        }

        // ================================================================= 主机

        public bool TryApplyEditableEdit(int clientId, in EditableEditRequestData request,
            out EditableHostOutcome outcome, out string reason)
        {
            outcome = null;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project: 没有加载中的 Project";
                m_hostRejected++;
                return false;
            }
            if (IsClientReplica(project))
            {
                reason = "not-host: 客户端副本不能落地可编辑内容";
                m_hostRejected++;
                return false;
            }
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer?.ComponentBody == null)
            {
                reason = "requester-missing: clientId=" + clientId +
                    " 没有联机角色（未加入 / 已离开）";
                m_hostRejected++;
                return false;
            }
            ComponentPlayer player = playerData.ComponentPlayer;
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
            {
                reason = "no-terrain: 主机地形子系统不可用";
                m_hostRejected++;
                return false;
            }
            int x = request.X, y = request.Y, z = request.Z;
            if (!terrain.Terrain.IsCellValid(x, y, z))
            {
                reason = "block-changed: 坐标越界 " + x + "," + y + "," + z;
                m_hostRejected++;
                return false;
            }
            if (terrain.Terrain.GetChunkAtCell(x, z) == null)
            {
                reason = "block-changed: 目标区块未加载 " + x + "," + y + "," + z;
                m_hostRejected++;
                return false;
            }
            var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            float measured = MathUtils.Sqrt(Vector3.DistanceSquared(
                player.ComponentBody.Position, center));
            if (!m_reachGate.TryCheckDistance(request.HasReach, request.Reach,
                    EngineReach.IsCreative(project), measured, out string reachReason))
            {
                reason = reachReason;
                m_hostRejected++;
                return false;
            }
            int contents = Game.Terrain.ExtractContents(terrain.Terrain.GetCellValue(x, y, z));
            if (contents != request.Contents)
            {
                reason = "block-changed: 主机该格 contents=" + contents + "，请求 contents=" +
                    request.Contents;
                m_hostRejected++;
                return false;
            }
            int expected = EditableKinds.BlockContents(request.Kind);
            if (contents != expected)
            {
                reason = "block-changed: 主机该格 contents=" + contents + " 不是 " +
                    EditableKinds.Name(request.Kind) + "（contents=" + expected + "）";
                m_hostRejected++;
                return false;
            }
            if (!EditableCodec.ValidateContent(request.Kind, request.Data, "data",
                    out string contentError))
            {
                reason = "malformed: " + contentError;
                m_hostRejected++;
                return false;
            }
            IEditableBlockContentHost host = FindContentHost(project, request.Kind);
            if (host == null)
            {
                reason = "subsystem-missing: 主机没有 " + EditableKinds.Name(request.Kind) +
                    " 的方块行为（引擎注入未生效？）";
                m_hostRejected++;
                return false;
            }
            if (!host.CaptureContent(x, y, z, out byte[] current, out string captureReason))
            {
                reason = "capture-failed: " + captureReason;
                m_hostRejected++;
                return false;
            }
            var key = new CellKey(x, y, z, request.Kind);
            int hostRevision = GetRevision(m_hostRevisions, key);
            if (!EditableCodec.SameUserContent(request.Kind, current, request.BaseData))
            {
                reason = "stale-content: 主机内容已变化（主机 rev=" + hostRevision + "，请求 baseRev=" +
                    request.BaseRevision + "，主机=" +
                    EditableCodec.DescribeData(current) + "，请求 base=" +
                    EditableCodec.DescribeData(request.BaseData) + "）";
                m_hostRejected++;
                return false;
            }
            if (request.BaseRevision > hostRevision)
            {
                reason = "stale-revision: 请求 baseRev=" + request.BaseRevision + " > 主机 rev=" +
                    hostRevision + "（主机重置 / 请求来自未来）";
                m_hostRejected++;
                return false;
            }
            // 玩家只编辑 Data；内存条 LastOutput 是主机运行期状态，落地时保留主机当前值。
            byte[] merged = EditableCodec.MergeUserContent(request.Kind, request.Data, current);
            if (!host.ApplyContent(x, y, z, contents, merged, hostAuthoritative: true,
                    out string applyReason))
            {
                reason = "apply-failed: " + applyReason;
                m_hostRejected++;
                return false;
            }
            int revision = hostRevision + 1;
            SetRevision(m_hostRevisions, key, revision, "host");
            var state = new EditableStateData(x, y, z, contents, request.Kind, revision,
                merged);
            outcome = new EditableHostOutcome(state);
            m_hostApplied++;
            Log("主机落地可编辑内容 clientId=" + clientId + " " + state.Describe());
            return true;
        }

        public bool TryCaptureEditableState(int x, int y, int z, int kind,
            out EditableStateData state, out string reason)
        {
            state = null;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                m_hostCaptureFailures++;
                return false;
            }
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
            {
                reason = "no-terrain";
                m_hostCaptureFailures++;
                return false;
            }
            if (!terrain.Terrain.IsCellValid(x, y, z))
            {
                reason = "block-changed: 坐标越界 " + x + "," + y + "," + z;
                m_hostCaptureFailures++;
                return false;
            }
            if (terrain.Terrain.GetChunkAtCell(x, z) == null)
            {
                reason = "chunk-not-loaded: " + x + "," + y + "," + z;
                m_hostCaptureFailures++;
                return false;
            }
            int contents = Game.Terrain.ExtractContents(terrain.Terrain.GetCellValue(x, y, z));
            int expected = EditableKinds.BlockContents(kind);
            if (contents != expected)
            {
                reason = "block-changed: contents=" + contents + " 不是 " +
                    EditableKinds.Name(kind) + "（contents=" + expected + "）";
                m_hostCaptureFailures++;
                return false;
            }
            IEditableBlockContentHost host = FindContentHost(project, kind);
            if (host == null)
            {
                reason = "subsystem-missing: " + EditableKinds.Name(kind);
                m_hostCaptureFailures++;
                return false;
            }
            if (!host.CaptureContent(x, y, z, out byte[] data, out string captureReason))
            {
                reason = "capture-failed: " + captureReason;
                m_hostCaptureFailures++;
                return false;
            }
            int revision = GetRevision(m_hostRevisions, new CellKey(x, y, z, kind));
            state = new EditableStateData(x, y, z, contents, kind, revision, data);
            m_hostCaptures++;
            return true;
        }

        public int CollectEditableStateRecipients(int x, int y, int z, int exceptClientId,
            List<int> recipients)
        {
            if (recipients == null) return 0;
            Project project = GameManager.Project;
            if (project == null || IsClientReplica(project)) return 0;
            var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            int skipped = 0;
            foreach (int clientId in m_spawner.ClientIds)
            {
                if (clientId == exceptClientId) continue;
                if (!m_spawner.TryGetAvatarCell(clientId, out Vector3 position, out string why))
                {
                    skipped++;
                    m_recipientsNoAvatar++;
                    LogProblem("recipient-no-avatar:" + clientId, "可编辑内容广播跳过 clientId=" +
                        clientId + "（" + why + "）");
                    continue;
                }
                if (Vector3.DistanceSquared(position, center) > MaxBroadcastDistanceSq)
                {
                    skipped++;
                    m_recipientsSkipped++;
                    continue;
                }
                recipients.Add(clientId);
                m_recipients++;
            }
            return skipped;
        }

        public bool TryApplyHostLocalEdit(int kind, int x, int y, int z, int cellValue, byte[] data,
            out EditableStateData state, out string reason)
        {
            state = null;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                m_hostLocalRejected++;
                return false;
            }
            if (IsClientReplica(project))
            {
                reason = "not-host: 客户端副本不能落地本机编辑";
                m_hostLocalRejected++;
                return false;
            }
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
            {
                reason = "no-terrain";
                m_hostLocalRejected++;
                return false;
            }
            if (!terrain.Terrain.IsCellValid(x, y, z))
            {
                reason = "block-changed: 坐标越界 " + x + "," + y + "," + z;
                m_hostLocalRejected++;
                return false;
            }
            if (terrain.Terrain.GetChunkAtCell(x, z) == null)
            {
                reason = "block-changed: 目标区块未加载 " + x + "," + y + "," + z;
                m_hostLocalRejected++;
                return false;
            }
            int contents = Game.Terrain.ExtractContents(cellValue);
            int expected = EditableKinds.BlockContents(kind);
            if (contents != expected)
            {
                reason = "block-changed: 对话框打开时的方块 contents=" + contents + " 不是 " +
                    EditableKinds.Name(kind) + "（contents=" + expected + "）";
                m_hostLocalRejected++;
                return false;
            }
            int currentContents =
                Game.Terrain.ExtractContents(terrain.Terrain.GetCellValue(x, y, z));
            if (currentContents != contents)
            {
                reason = "block-changed: 主机该格现在是 contents=" + currentContents +
                    "（对话框打开时 " + contents + "）";
                m_hostLocalRejected++;
                return false;
            }
            if (!EditableCodec.ValidateContent(kind, data, "data", out string contentError))
            {
                reason = "malformed: " + contentError;
                m_hostLocalRejected++;
                return false;
            }
            IEditableBlockContentHost host = FindContentHost(project, kind);
            if (host == null)
            {
                reason = "subsystem-missing: 主机没有 " + EditableKinds.Name(kind) + " 的方块行为";
                m_hostLocalRejected++;
                return false;
            }
            // 与远端提交同一口径：只接受玩家编辑的部分，内存条 LastOutput 保留主机当前值。
            if (!host.CaptureContent(x, y, z, out byte[] current, out string captureReason))
            {
                reason = "capture-failed: " + captureReason;
                m_hostLocalRejected++;
                return false;
            }
            byte[] merged = EditableCodec.MergeUserContent(kind, data, current);
            if (!host.ApplyContent(x, y, z, contents, merged, hostAuthoritative: true,
                    out string applyReason))
            {
                reason = "apply-failed: " + applyReason;
                m_hostLocalRejected++;
                return false;
            }
            var key = new CellKey(x, y, z, kind);
            int revision = GetRevision(m_hostRevisions, key) + 1;
            SetRevision(m_hostRevisions, key, revision, "host-local");
            state = new EditableStateData(x, y, z, contents, kind, revision, merged);
            m_hostLocalApplied++;
            m_broadcastEvents++;
            Log("主机本机落地可编辑内容 " + state.Describe());
            Action<EditableStateData> handler = HostStateChanged;
            if (handler != null)
            {
                try
                {
                    handler(state);
                }
                catch (Exception e)
                {
                    m_broadcastFaults++;
                    LogProblem("host-state-handler", "可编辑内容广播回调异常（" + e.GetType().Name +
                        " " + e.Message + "）→ 主机内容已落地，客户端可能收不到这次内容");
                }
            }
            return true;
        }

        // ================================================================= 内部

        private static IEditableBlockContentHost FindContentHost(Project project, int kind)
        {
            if (project == null) return null;
            foreach (IEditableBlockContentHost host in
                     project.FindSubsystems<IEditableBlockContentHost>())
            {
                if (host != null && host.EditableKind == kind) return host;
            }
            return null;
        }

        private static int GetRevision(Dictionary<CellKey, int> table, CellKey key) =>
            table.TryGetValue(key, out int revision) ? revision : 0;

        private void SetRevision(Dictionary<CellKey, int> table, CellKey key, int revision,
            string what)
        {
            if (!table.ContainsKey(key) && table.Count >= MaxTrackedRevisions)
            {
                int dropped = table.Count;
                table.Clear();
                m_revisionClears++;
                LogProblem("revision-cap:" + what, "可编辑内容 revision 表（" + what + "）超过 " +
                    MaxTrackedRevisions + " 项（" + dropped + "）→ 整表清空" +
                    "（之后按 base 内容重新判定；客户端 baseRev 高于主机的请求会被明确拒绝一次，" +
                    "权威内容随后补发）");
            }
            table[key] = revision;
        }

        public string Describe() =>
            "editable(clientSubmits=" + m_clientSubmits +
            " submitFailed=" + m_clientSubmitFailures +
            " notReplica=" + m_clientNotReplica +
            " noSink=" + m_clientNotConnected +
            " statesApplied=" + m_clientStatesApplied +
            " statesStale=" + m_clientStatesStale +
            " drops=" + m_clientDrops +
            " notifyRejected=" + m_clientNotifyRejected +
            " notifyMissing=" + m_clientNotifyMissing +
            " · hostApplied=" + m_hostApplied +
            " hostRejected=" + m_hostRejected +
            " hostLocalApplied=" + m_hostLocalApplied +
            " hostLocalRejected=" + m_hostLocalRejected +
            " captures=" + m_hostCaptures +
            " captureFailed=" + m_hostCaptureFailures +
            " recipients=" + m_recipients +
            " skippedFar=" + m_recipientsSkipped +
            " skippedNoAvatar=" + m_recipientsNoAvatar +
            " revisionClears=" + m_revisionClears +
            " broadcastEvents=" + m_broadcastEvents +
            " broadcastFaults=" + m_broadcastFaults + ")";

        // ================================================================= 日志

        private void Log(string message) =>
            m_diagnostics?.Info(m_ownerId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}

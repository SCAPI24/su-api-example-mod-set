using System;
using Google.Protobuf;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 可编辑方块内容种类（协议 <c>EditableKind</c> 的领域镜像 + 尺寸契约）。
    ///
    /// 引擎事实（决定线上边界，不是拍脑袋的上限）：
    ///   · 真值表 <c>TruthTableData.Data</c> 恒为 16 个 nibble（4 位）——
    ///     Source: Survivalcraft/Game/TruthTableData.cs:15；
    ///   · 内存条 <c>MemoryBankData.Data</c> 最多 256 个 nibble（地址 0..255），
    ///     另有 <c>LastOutput</c> 一个 nibble ——
    ///     Source: Survivalcraft/Game/MemoryBankData.cs:35（address &lt; 256）、:59（LoadString 截到 256）。
    ///
    /// 因此**整份内容有硬上界**（16 / 257 字节），一条消息就能装下；不存在需要分片的内容，
    /// 编解码对超长 / 过短载荷一律拒绝（不是静默截断）。
    /// </summary>
    public static class EditableKinds
    {
        public const int None = 0;
        public const int TruthTable = 1;
        public const int MemoryBank = 2;

        public static bool IsValid(int kind) => kind == TruthTable || kind == MemoryBank;

        public static string Name(int kind)
        {
            switch (kind)
            {
                case TruthTable: return "truth-table";
                case MemoryBank: return "memory-bank";
                default: return kind == None ? "none" : "unknown(" + kind + ")";
            }
        }

        /// <summary>该种类方块的地形 contents（引擎写死；见两个 Subsystem 的 base(contents)）。</summary>
        public static int BlockContents(int kind)
        {
            switch (kind)
            {
                case TruthTable: return 188;
                case MemoryBank: return 186;
                default: return 0;
            }
        }

        /// <summary>线上内容字节数：真值表 16；内存条 256 个 nibble + 1 个 LastOutput = 257。</summary>
        public static int DataBytes(int kind)
        {
            switch (kind)
            {
                case TruthTable: return 16;
                case MemoryBank: return 257;
                default: return 0;
            }
        }

        /// <summary>各类内容里 nibble 的个数（内存条最后一个字节是 LastOutput，单独校验）。</summary>
        public static int NibbleBytes(int kind)
        {
            switch (kind)
            {
                case TruthTable: return 16;
                case MemoryBank: return 256;
                default: return 0;
            }
        }
    }

    /// <summary>客户端 -&gt; 主机：一次"对话框提交"（纯值形态；序号由模块分配）。</summary>
    public readonly struct EditableEditRequestData
    {
        public EditableEditRequestData(long sequence, int x, int y, int z, int contents, int kind,
            int baseRevision, byte[] baseData, byte[] data, bool hasReach = false,
            float reach = 0f)
        {
            Sequence = sequence;
            X = x;
            Y = y;
            Z = z;
            Contents = contents;
            Kind = kind;
            BaseRevision = baseRevision;
            BaseData = baseData;
            Data = data;
            HasReach = hasReach;
            Reach = reach;
        }

        public long Sequence { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Contents { get; }
        public int Kind { get; }
        public int BaseRevision { get; }
        /// <summary>对话框开始编辑时的权威内容（主机用它判定"没有丢更新"）。</summary>
        public byte[] BaseData { get; }
        /// <summary>对话框产出的新内容。</summary>
        public byte[] Data { get; }
        /// <summary>请求是否携带了有效 reach（false = 旧客户端 / 旧载荷）。</summary>
        public bool HasReach { get; }
        /// <summary>请求者的有效交互距离（米）；<see cref="HasReach"/> = false 时无意义。</summary>
        public float Reach { get; }

        public EditableEditRequestData WithSequence(long sequence) =>
            new EditableEditRequestData(sequence, X, Y, Z, Contents, Kind, BaseRevision, BaseData,
                Data, HasReach, Reach);

        public string Describe() =>
            EditableKinds.Name(Kind) + " @" + X + "," + Y + "," + Z + " contents=" + Contents +
            " baseRev=" + BaseRevision + " base=" +
            EditableCodec.DescribeData(BaseData) + " data=" + EditableCodec.DescribeData(Data) +
            " seq=" + Sequence;
    }

    /// <summary>主机 -&gt; 客户端：一格可编辑方块的权威内容（不可变快照）。</summary>
    public sealed class EditableStateData
    {
        public EditableStateData(int x, int y, int z, int contents, int kind, int revision,
            byte[] data)
        {
            X = x;
            Y = y;
            Z = z;
            Contents = contents;
            Kind = kind;
            Revision = revision;
            Data = data ?? Array.Empty<byte>();
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Contents { get; }
        public int Kind { get; }
        public int Revision { get; }
        public byte[] Data { get; }

        public bool SameCell(int x, int y, int z, int kind) =>
            X == x && Y == y && Z == z && Kind == kind;

        public bool SameContent(byte[] data) => EditableCodec.SameData(Data, data);

        public string Describe() =>
            EditableKinds.Name(Kind) + " @" + X + "," + Y + "," + Z + " contents=" + Contents +
            " rev=" + Revision + " data=" + EditableCodec.DescribeData(Data);
    }

    /// <summary>主机 -&gt; 请求者：一次可编辑内容提交的裁决。</summary>
    public readonly struct EditableEditResultData
    {
        public EditableEditResultData(long sequence, bool accepted, string reason, int kind, int x,
            int y, int z, int revision)
        {
            Sequence = sequence;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            Kind = kind;
            X = x;
            Y = y;
            Z = z;
            Revision = revision;
        }

        public long Sequence { get; }
        public bool Accepted { get; }
        /// <summary>稳定原因 code 在第一个 ':' 之前（stale-content / block-changed / ...）。</summary>
        public string Reason { get; }
        public int Kind { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Revision { get; }

        public string Describe() =>
            (Accepted ? "accepted" : "rejected:" + EditableCodec.ReasonCode(Reason)) + " " +
            EditableKinds.Name(Kind) + " @" + X + "," + Y + "," + Z + " rev=" + Revision +
            " seq=" + Sequence;
    }

    /// <summary>客户端把一条权威内容应用到副本的结果（计数口径由模块决定）。</summary>
    public enum EditableStateApplyOutcome
    {
        /// <summary>已写入副本（同 revision 的重发也走这里，幂等）。</summary>
        Applied = 0,
        /// <summary>revision 比已应用的更旧 → 丢弃。</summary>
        Stale = 1,
        /// <summary>该格已经不是这种方块（主机在别处已移除 / 替换）→ 副本内容被丢弃。</summary>
        BlockChanged = 2,
        /// <summary>端口明确失败（找不到副本子系统 / 内容解码失败 / ...）。</summary>
        Failed = 3
    }

    /// <summary>
    /// 可编辑方块内容的编解码 + 校验（无 Game 依赖：tsverify 直接编译本文件）。
    ///
    /// 纪律：
    ///   · 载荷长度 / 取值域 / 坐标范围全部显式校验，非法整条拒绝（不截断、不猜值）；
    ///   · 线上只传内容本身，不传引擎类型名 / 序列化字符串；
    ///   · 日志里只给内容指纹与长度，不打印整张内存条地图。
    /// </summary>
    public static class EditableCodec
    {
        /// <summary>可选坐标的绝对值上限（防御恶意 / 溢出载荷；主机仍会做 IsCellValid + 区块检查）。</summary>
        public const int MaxAbsCoordinate = 1 << 24;

        public static P.EditableEditRequest EncodeRequest(in EditableEditRequestData request)
        {
            var message = new P.EditableEditRequest
            {
                Sequence = request.Sequence,
                X = request.X,
                Y = request.Y,
                Z = request.Z,
                Contents = request.Contents,
                Kind = (P.EditableKind)request.Kind,
                BaseRevision = request.BaseRevision,
                BaseData = ByteString.CopyFrom(request.BaseData ?? Array.Empty<byte>()),
                Data = ByteString.CopyFrom(request.Data ?? Array.Empty<byte>())
            };
            if (request.HasReach) message.Reach = request.Reach;
            return message;
        }

        public static byte[] EncodeRequestBytes(in EditableEditRequestData request) =>
            EncodeRequest(in request).ToByteArray();

        public static P.EditableEditResult EncodeResult(in EditableEditResultData result)
        {
            return new P.EditableEditResult
            {
                Sequence = result.Sequence,
                Accepted = result.Accepted,
                Reason = result.Reason ?? string.Empty,
                Kind = (P.EditableKind)result.Kind,
                X = result.X,
                Y = result.Y,
                Z = result.Z,
                Revision = result.Revision
            };
        }

        public static byte[] EncodeResultBytes(in EditableEditResultData result) =>
            EncodeResult(in result).ToByteArray();

        public static P.EditableState EncodeState(EditableStateData state)
        {
            return new P.EditableState
            {
                X = state.X,
                Y = state.Y,
                Z = state.Z,
                Contents = state.Contents,
                Kind = (P.EditableKind)state.Kind,
                Revision = state.Revision,
                Data = ByteString.CopyFrom(state.Data ?? Array.Empty<byte>())
            };
        }

        public static byte[] EncodeStateBytes(EditableStateData state) =>
            EncodeState(state).ToByteArray();

        public static bool TryConvertRequest(P.EditableEditRequest message,
            out EditableEditRequestData request, out string error)
        {
            request = default;
            error = null;
            if (message == null)
            {
                error = "EditableEditRequest 为空";
                return false;
            }
            int kind = (int)message.Kind;
            if (!EditableKinds.IsValid(kind))
            {
                error = "kind 非法：" + kind;
                return false;
            }
            if (!ValidateCell(message.X, message.Y, message.Z, message.Contents,
                    EditableKinds.BlockContents(kind), out error))
            {
                return false;
            }
            if (message.Sequence < 0)
            {
                error = "sequence 非法：" + message.Sequence;
                return false;
            }
            if (message.BaseRevision < 0)
            {
                error = "base_revision 非法：" + message.BaseRevision;
                return false;
            }
            byte[] baseData = message.BaseData?.ToByteArray() ?? Array.Empty<byte>();
            byte[] data = message.Data?.ToByteArray() ?? Array.Empty<byte>();
            if (!ValidateContent(kind, baseData, "base_data", out error)) return false;
            if (!ValidateContent(kind, data, "data", out error)) return false;
            request = new EditableEditRequestData(message.Sequence, message.X, message.Y,
                message.Z, message.Contents, kind, message.BaseRevision, baseData, data,
                message.HasReach, message.Reach);
            return true;
        }

        public static bool TryDecodeRequest(byte[] payload, out EditableEditRequestData request,
            out string error)
        {
            request = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            P.EditableEditRequest message;
            try
            {
                message = P.EditableEditRequest.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "protobuf 解析失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvertRequest(message, out request, out error);
        }

        public static bool TryConvertResult(P.EditableEditResult message,
            out EditableEditResultData result, out string error)
        {
            result = default;
            error = null;
            if (message == null)
            {
                error = "EditableEditResult 为空";
                return false;
            }
            int kind = (int)message.Kind;
            if (!EditableKinds.IsValid(kind))
            {
                error = "kind 非法：" + kind;
                return false;
            }
            if (message.Sequence < 0)
            {
                error = "sequence 非法：" + message.Sequence;
                return false;
            }
            if (message.Revision < 0)
            {
                error = "revision 非法：" + message.Revision;
                return false;
            }
            if (!message.Accepted && string.IsNullOrEmpty(message.Reason))
            {
                error = "拒绝但没有 reason（不允许静默拒绝）";
                return false;
            }
            result = new EditableEditResultData(message.Sequence, message.Accepted,
                message.Reason, kind, message.X, message.Y, message.Z, message.Revision);
            return true;
        }

        public static bool TryConvertResult(byte[] payload, out EditableEditResultData result,
            out string error)
        {
            result = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            P.EditableEditResult message;
            try
            {
                message = P.EditableEditResult.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "protobuf 解析失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvertResult(message, out result, out error);
        }

        public static bool TryConvertState(P.EditableState message,
            out EditableStateData state, out string error)
        {
            state = null;
            error = null;
            if (message == null)
            {
                error = "EditableState 为空";
                return false;
            }
            int kind = (int)message.Kind;
            if (!EditableKinds.IsValid(kind))
            {
                error = "kind 非法：" + kind;
                return false;
            }
            if (!ValidateCell(message.X, message.Y, message.Z, message.Contents,
                    EditableKinds.BlockContents(kind), out error))
            {
                return false;
            }
            if (message.Revision < 0)
            {
                error = "revision 非法：" + message.Revision;
                return false;
            }
            byte[] data = message.Data?.ToByteArray() ?? Array.Empty<byte>();
            if (!ValidateContent(kind, data, "data", out error)) return false;
            state = new EditableStateData(message.X, message.Y, message.Z, message.Contents,
                kind, message.Revision, data);
            return true;
        }

        public static bool TryConvertState(byte[] payload, out EditableStateData state,
            out string error)
        {
            state = null;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            P.EditableState message;
            try
            {
                message = P.EditableState.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "protobuf 解析失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvertState(message, out state, out error);
        }

        /// <summary>一格坐标 + contents 的校验（kind 的方块 contents 必须与请求一致）。</summary>
        public static bool ValidateCell(int x, int y, int z, int contents, int expectedContents,
            out string error)
        {
            error = null;
            if (y < 0 || y > 255)
            {
                error = "y 越界：" + y;
                return false;
            }
            if (x < -MaxAbsCoordinate || x > MaxAbsCoordinate ||
                z < -MaxAbsCoordinate || z > MaxAbsCoordinate)
            {
                error = "x/z 越界：" + x + "," + z;
                return false;
            }
            if (contents <= 0)
            {
                error = "contents 非法：" + contents;
                return false;
            }
            if (expectedContents != 0 && contents != expectedContents)
            {
                error = "contents 与 kind 不符：contents=" + contents + " expected=" +
                    expectedContents;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 内容形状校验：长度必须**恰好**是引擎上界（16 / 257），每个 nibble 必须 0..15。
        /// 不做"补零 / 截断"（静默改变玩家内容比拒绝更糟）。
        /// </summary>
        public static bool ValidateContent(int kind, byte[] data, string label, out string error)
        {
            error = null;
            if (!EditableKinds.IsValid(kind))
            {
                error = label + "：kind 非法：" + kind;
                return false;
            }
            int expected = EditableKinds.DataBytes(kind);
            int length = data?.Length ?? 0;
            if (length != expected)
            {
                error = label + "：长度 " + length + " ≠ " + expected + "（" +
                    EditableKinds.Name(kind) + "）";
                return false;
            }
            int nibbles = EditableKinds.NibbleBytes(kind);
            for (int i = 0; i < length; i++)
            {
                int value = data[i];
                if (value > 15)
                {
                    error = label + "：第 " + i + " 字节 " + value + " 超出 nibble 范围 0..15";
                    return false;
                }
            }
            if (kind == EditableKinds.MemoryBank && nibbles != length - 1)
            {
                error = label + "：LastOutput 位置不成立（nibbles=" + nibbles + " len=" + length +
                    "）";
                return false;
            }
            return true;
        }

        public static bool SameData(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        /// <summary>
        /// 玩家**可编辑**的字节数：真值表 16；内存条 256（最后一个字节是 LastOutput，
        /// 由 <c>MemoryBankElectricElement.Simulate</c> 在主机运行期写入，
        /// 对话框的 OK 只复制 <c>Data</c>，不改它 —— Source: MemoryBankElectricElement.cs:105、
        /// EditMemoryBankDialog.cs:95）。
        /// </summary>
        public static int UserDataBytes(int kind) =>
            kind == EditableKinds.MemoryBank
                ? EditableKinds.NibbleBytes(EditableKinds.MemoryBank)
                : EditableKinds.DataBytes(kind);

        /// <summary>
        /// 只比较玩家可编辑部分（内存条忽略 LastOutput）。编辑冲突判定必须是这个口径：
        /// LastOutput 是主机电学运行状态，客户端副本本来就只按主机下发刷新，
        /// 用它判"内容被改过"会让每一次内存条编辑都被误判 stale-content。
        /// </summary>
        public static bool SameUserContent(int kind, byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            int bytes = UserDataBytes(kind);
            if (left.Length < bytes || right.Length < bytes) return false;
            for (int i = 0; i < bytes; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        /// <summary>
        /// 落地时保留主机权威的**运行期字节**（内存条 LastOutput）：玩家只编辑 Data，
        /// 不因为一次编辑把主机当前输出值写回旧值（与引擎原生提交"改同一个对象"等价）。
        /// </summary>
        public static byte[] MergeUserContent(int kind, byte[] userData, byte[] authoritative)
        {
            if (userData == null) return null;
            var merged = (byte[])userData.Clone();
            if (kind == EditableKinds.MemoryBank && merged.Length ==
                    EditableKinds.DataBytes(EditableKinds.MemoryBank) &&
                authoritative != null &&
                authoritative.Length == EditableKinds.DataBytes(EditableKinds.MemoryBank))
            {
                merged[EditableKinds.NibbleBytes(EditableKinds.MemoryBank)] =
                    authoritative[EditableKinds.NibbleBytes(EditableKinds.MemoryBank)];
            }
            return merged;
        }

        /// <summary>内容指纹（FNV-1a）：日志里用它标识"哪份内容"，不打印整张地图。</summary>
        public static long Fingerprint(byte[] data)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                if (data != null)
                {
                    for (int i = 0; i < data.Length; i++)
                        hash = (hash ^ data[i]) * 1099511628211L;
                }
                return hash;
            }
        }

        public static string DescribeData(byte[] data) =>
            data == null ? "null" : data.Length + "B/fnv=" + Fingerprint(data).ToString("x16");

        /// <summary>稳定原因 code：第一个 ':' 之前的部分（空 / 无 reason → "unknown"）。</summary>
        public static string ReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "unknown";
            int colon = reason.IndexOf(':');
            string code = colon > 0 ? reason.Substring(0, colon) : reason;
            code = code.Trim();
            return code.Length == 0 ? "unknown" : code;
        }
    }
}

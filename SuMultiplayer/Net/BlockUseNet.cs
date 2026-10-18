using System;
using System.Collections.Generic;
using System.Globalization;
using Google.Protobuf;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 容器种类（协议 <c>ContainerKind</c> 的领域镜像 + 校验）。
    ///
    /// **线上只传 id，不传类型名**：客户端绝不允许按对端给的类名去实例化类型。
    /// Source: Survivalcraft/Game/ComponentCraftingTable.cs:ComponentCraftingTable
    /// Source: Survivalcraft/Game/ComponentChest.cs:ComponentChest
    /// Source: Survivalcraft/Game/ComponentFurnace.cs:ComponentFurnace
    /// Source: Survivalcraft/Game/ComponentDispenser.cs:ComponentDispenser
    /// </summary>
    public static class ContainerKinds
    {
        public const int None = 0;
        public const int CraftingTable = 1;
        public const int Chest = 2;
        public const int Furnace = 3;
        public const int Dispenser = 4;

        public static bool IsContainer(int kind) =>
            kind == CraftingTable || kind == Chest || kind == Furnace || kind == Dispenser;

        public static string Name(int kind)
        {
            switch (kind)
            {
                case CraftingTable: return "crafting-table";
                case Chest: return "chest";
                case Furnace: return "furnace";
                case Dispenser: return "dispenser";
                default: return kind == None ? "none" : "unknown(" + kind + ")";
            }
        }
    }

    /// <summary>方块使用动作（协议 <c>BlockUseAction</c> 的领域镜像）。</summary>
    public static class BlockUseActions
    {
        public const int Interact = 0;
        public const int Close = 1;

        public static bool IsValid(int action) => action == Interact || action == Close;

        public static string Name(int action) => action == Close ? "close" : "interact";
    }

    /// <summary>
    /// 一个容器（方块实体库存）的稳定键：坐标 + 种类。
    /// 同一坐标不可能同时是两种容器（方块内容决定种类），kind 参与键是为了在方块被替换后
    /// 不会把旧库存当成新容器继续下发。
    /// </summary>
    public readonly struct ContainerKey : IEquatable<ContainerKey>
    {
        public ContainerKey(int x, int y, int z, int kind)
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

        public bool IsValid => ContainerKinds.IsContainer(Kind);

        public bool Equals(ContainerKey other) =>
            X == other.X && Y == other.Y && Z == other.Z && Kind == other.Kind;

        public override bool Equals(object obj) => obj is ContainerKey other && Equals(other);

        public override int GetHashCode() => ((X * 397) ^ Y) * 397 ^ Z ^ (Kind * 31);

        public string Describe() => X + "," + Y + "," + Z + ":" + ContainerKinds.Name(Kind);
    }

    /// <summary>客户端 -> 主机：一次方块使用意图（纯值形态；序号由模块分配）。</summary>
    public readonly struct BlockUseRequestData
    {
        public BlockUseRequestData(long sequence, int x, int y, int z, int face, int contents,
            int action, float rayX, float rayY, float rayZ, float directionX, float directionY,
            float directionZ)
        {
            Sequence = sequence;
            X = x;
            Y = y;
            Z = z;
            Face = face;
            Contents = contents;
            Action = action;
            RayX = rayX;
            RayY = rayY;
            RayZ = rayZ;
            DirectionX = directionX;
            DirectionY = directionY;
            DirectionZ = directionZ;
        }

        public long Sequence { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Face { get; }
        public int Contents { get; }
        public int Action { get; }
        public float RayX { get; }
        public float RayY { get; }
        public float RayZ { get; }
        public float DirectionX { get; }
        public float DirectionY { get; }
        public float DirectionZ { get; }

        public BlockUseRequestData WithSequence(long sequence) => new BlockUseRequestData(sequence,
            X, Y, Z, Face, Contents, Action, RayX, RayY, RayZ, DirectionX, DirectionY, DirectionZ);

        public string Describe() => BlockUseActions.Name(Action) + " @" + X + "," + Y + "," + Z +
            " face=" + Face + " contents=" + Contents + " seq=" + Sequence;
    }

    /// <summary>主机 -> 客户端：一次方块使用的裁决。</summary>
    public readonly struct BlockUseResultData
    {
        public BlockUseResultData(long sequence, bool accepted, string reason, int action,
            int containerKind, int x, int y, int z)
        {
            Sequence = sequence;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            Action = action;
            ContainerKind = containerKind;
            X = x;
            Y = y;
            Z = z;
        }

        public long Sequence { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public int Action { get; }
        public int ContainerKind { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public bool OpensContainer => Accepted && Action == BlockUseActions.Interact &&
            ContainerKinds.IsContainer(ContainerKind);
    }

    /// <summary>
    /// 一个方块实体库存的权威快照（主机 -> 客户端，或客户端 -> 主机的编辑请求体）。
    /// 值/数量数组等长 = 库存槽位数；count == 0 表示空槽（value 必须为 0）。
    /// </summary>
    public sealed class ContainerStateData
    {
        public const int MaxSlots = 4096;

        public ContainerStateData(int x, int y, int z, int containerKind, int revision,
            int[] values, int[] counts)
        {
            X = x;
            Y = y;
            Z = z;
            ContainerKind = containerKind;
            Revision = revision;
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int ContainerKind { get; }
        public int Revision { get; }
        public int[] Values { get; }
        public int[] Counts { get; }

        public ContainerKey Key => new ContainerKey(X, Y, Z, ContainerKind);

        public int SlotsCount => Math.Min(Values.Length, Counts.Length);

        public bool SameValues(in ContainerStateData other) =>
            other != null && SameArrays(Values, other.Values) && SameArrays(Counts, other.Counts);

        public bool SameValues(int[] values, int[] counts) =>
            SameArrays(Values, values) && SameArrays(Counts, counts);

        public static bool SameArrays(int[] a, int[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        public int NonEmptySlots
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Counts.Length; i++)
                {
                    if (Counts[i] > 0) count++;
                }
                return count;
            }
        }

        public string Describe()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(Key.Describe()).Append(" rev=").Append(Revision)
                .Append(" slots=").Append(SlotsCount);
            int shown = 0;
            for (int i = 0; i < SlotsCount && shown < 6; i++)
            {
                if (Counts[i] <= 0) continue;
                builder.Append(" [").Append(i).Append("]=").Append(Values[i]).Append("x")
                    .Append(Counts[i]);
                shown++;
            }
            return builder.ToString();
        }
    }

    /// <summary>客户端 -> 主机：一次容器编辑（UI 槽位变化 + 玩家背包快照）。</summary>
    public readonly struct ContainerEditRequestData
    {
        public ContainerEditRequestData(long sequence, int x, int y, int z, int containerKind,
            int baseRevision, int[] baseValues, int[] baseCounts, int[] desiredValues,
            int[] desiredCounts, int[] playerBaseValues, int[] playerBaseCounts,
            int[] playerDesiredValues, int[] playerDesiredCounts)
        {
            Sequence = sequence;
            X = x;
            Y = y;
            Z = z;
            ContainerKind = containerKind;
            BaseRevision = baseRevision;
            BaseValues = baseValues ?? Array.Empty<int>();
            BaseCounts = baseCounts ?? Array.Empty<int>();
            DesiredValues = desiredValues ?? Array.Empty<int>();
            DesiredCounts = desiredCounts ?? Array.Empty<int>();
            PlayerBaseValues = playerBaseValues ?? Array.Empty<int>();
            PlayerBaseCounts = playerBaseCounts ?? Array.Empty<int>();
            PlayerDesiredValues = playerDesiredValues ?? Array.Empty<int>();
            PlayerDesiredCounts = playerDesiredCounts ?? Array.Empty<int>();
        }

        public long Sequence { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int ContainerKind { get; }
        public int BaseRevision { get; }
        public int[] BaseValues { get; }
        public int[] BaseCounts { get; }
        public int[] DesiredValues { get; }
        public int[] DesiredCounts { get; }
        public int[] PlayerBaseValues { get; }
        public int[] PlayerBaseCounts { get; }
        public int[] PlayerDesiredValues { get; }
        public int[] PlayerDesiredCounts { get; }

        public ContainerKey Key => new ContainerKey(X, Y, Z, ContainerKind);

        public ContainerEditRequestData WithSequence(long sequence) => new ContainerEditRequestData(
            sequence, X, Y, Z, ContainerKind, BaseRevision, BaseValues, BaseCounts, DesiredValues,
            DesiredCounts, PlayerBaseValues, PlayerBaseCounts, PlayerDesiredValues,
            PlayerDesiredCounts);

        public string Describe() => Key.Describe() + " baseRev=" + BaseRevision + " seq=" +
            Sequence;
    }

    /// <summary>主机 -> 客户端：一次容器编辑的裁决（权威状态另发 <c>ContainerState</c>）。</summary>
    public readonly struct ContainerEditResultData
    {
        public ContainerEditResultData(long sequence, bool accepted, string reason, int revision,
            int containerKind)
        {
            Sequence = sequence;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            Revision = revision;
            ContainerKind = containerKind;
        }

        public long Sequence { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public int Revision { get; }
        public int ContainerKind { get; }
    }

    /// <summary>
    /// 方块使用 / 容器消息的编解码 + 载荷校验。纯托管（只依赖 Google.Protobuf），
    /// 因此模块与 P:\tmp\tsverify 编译同一份源码。
    /// </summary>
    public static class BlockUseCodec
    {
        public const int MaxSlots = ContainerStateData.MaxSlots;

        public static P.BlockUseRequest EncodeRequest(in BlockUseRequestData request)
        {
            return new P.BlockUseRequest
            {
                Sequence = request.Sequence,
                X = request.X,
                Y = request.Y,
                Z = request.Z,
                Face = request.Face,
                Contents = request.Contents,
                Action = (P.BlockUseAction)request.Action,
                RayX = request.RayX,
                RayY = request.RayY,
                RayZ = request.RayZ,
                DirectionX = request.DirectionX,
                DirectionY = request.DirectionY,
                DirectionZ = request.DirectionZ
            };
        }

        public static byte[] EncodeRequestBytes(in BlockUseRequestData request) =>
            EncodeRequest(in request).ToByteArray();

        public static bool TryDecodeRequest(P.BlockUseRequest message,
            out BlockUseRequestData request, out string error)
        {
            request = default;
            error = null;
            if (message == null)
            {
                error = "BlockUseRequest 为空";
                return false;
            }
            int action = (int)message.Action;
            if (!BlockUseActions.IsValid(action))
            {
                error = "action 非法：" + action;
                return false;
            }
            if (message.Face < 0 || message.Face > 5)
            {
                error = "face 越界：" + message.Face;
                return false;
            }
            if (message.Contents < 0)
            {
                error = "contents 非法：" + message.Contents;
                return false;
            }
            if (!IsFinite(message.RayX) || !IsFinite(message.RayY) || !IsFinite(message.RayZ) ||
                !IsFinite(message.DirectionX) || !IsFinite(message.DirectionY) ||
                !IsFinite(message.DirectionZ))
            {
                error = "射线包含 NaN/Infinity";
                return false;
            }
            request = new BlockUseRequestData(message.Sequence, message.X, message.Y, message.Z,
                message.Face, message.Contents, action, message.RayX, message.RayY, message.RayZ,
                message.DirectionX, message.DirectionY, message.DirectionZ);
            return true;
        }

        public static bool TryDecodeRequest(byte[] payload, out BlockUseRequestData request,
            out string error)
        {
            request = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            P.BlockUseRequest message;
            try
            {
                message = P.BlockUseRequest.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析 BlockUseRequest 失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryDecodeRequest(message, out request, out error);
        }

        public static P.BlockUseResult EncodeResult(in BlockUseResultData result)
        {
            return new P.BlockUseResult
            {
                Sequence = result.Sequence,
                Accepted = result.Accepted,
                Reason = result.Reason ?? string.Empty,
                Action = (P.BlockUseAction)result.Action,
                ContainerKind = (P.ContainerKind)result.ContainerKind,
                X = result.X,
                Y = result.Y,
                Z = result.Z
            };
        }

        public static byte[] EncodeResultBytes(in BlockUseResultData result) =>
            EncodeResult(in result).ToByteArray();

        public static bool TryConvertResult(P.BlockUseResult message,
            out BlockUseResultData result, out string error)
        {
            result = default;
            error = null;
            if (message == null)
            {
                error = "BlockUseResult 为空";
                return false;
            }
            int action = (int)message.Action;
            int kind = (int)message.ContainerKind;
            if (!BlockUseActions.IsValid(action))
            {
                error = "action 非法：" + action;
                return false;
            }
            if (kind != ContainerKinds.None && !ContainerKinds.IsContainer(kind))
            {
                error = "container_kind 非法：" + kind;
                return false;
            }
            if (!message.Accepted && string.IsNullOrEmpty(message.Reason))
            {
                error = "accepted=false 但没有拒绝原因";
                return false;
            }
            result = new BlockUseResultData(message.Sequence, message.Accepted, message.Reason,
                action, kind, message.X, message.Y, message.Z);
            return true;
        }

        public static bool TryDecodeResult(byte[] payload, out BlockUseResultData result,
            out string error)
        {
            result = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            P.BlockUseResult message;
            try
            {
                message = P.BlockUseResult.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析 BlockUseResult 失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvertResult(message, out result, out error);
        }

        public static P.ContainerState EncodeContainerState(ContainerStateData state)
        {
            var message = new P.ContainerState
            {
                X = state.X,
                Y = state.Y,
                Z = state.Z,
                ContainerKind = (P.ContainerKind)state.ContainerKind,
                Revision = state.Revision
            };
            message.SlotValues.AddRange(state.Values);
            message.SlotCounts.AddRange(state.Counts);
            return message;
        }

        public static byte[] EncodeContainerStateBytes(ContainerStateData state) =>
            EncodeContainerState(state).ToByteArray();

        public static bool TryConvertContainerState(P.ContainerState message,
            out ContainerStateData state, out string error)
        {
            state = null;
            error = null;
            if (message == null)
            {
                error = "ContainerState 为空";
                return false;
            }
            if (!ContainerKinds.IsContainer((int)message.ContainerKind))
            {
                error = "container_kind 非法：" + message.ContainerKind;
                return false;
            }
            if (!TryCopySlots(message.SlotValues, message.SlotCounts, out int[] values,
                    out int[] counts, out error))
            {
                return false;
            }
            state = new ContainerStateData(message.X, message.Y, message.Z,
                (int)message.ContainerKind, message.Revision, values, counts);
            return true;
        }

        public static bool TryDecodeContainerState(byte[] payload,
            out ContainerStateData state, out string error)
        {
            state = null;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            P.ContainerState message;
            try
            {
                message = P.ContainerState.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析 ContainerState 失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvertContainerState(message, out state, out error);
        }

        public static P.ContainerEditRequest EncodeContainerEdit(
            in ContainerEditRequestData request)
        {
            var message = new P.ContainerEditRequest
            {
                Sequence = request.Sequence,
                X = request.X,
                Y = request.Y,
                Z = request.Z,
                ContainerKind = (P.ContainerKind)request.ContainerKind,
                BaseRevision = request.BaseRevision
            };
            message.BaseValues.AddRange(request.BaseValues);
            message.BaseCounts.AddRange(request.BaseCounts);
            message.DesiredValues.AddRange(request.DesiredValues);
            message.DesiredCounts.AddRange(request.DesiredCounts);
            message.PlayerBaseValues.AddRange(request.PlayerBaseValues);
            message.PlayerBaseCounts.AddRange(request.PlayerBaseCounts);
            message.PlayerDesiredValues.AddRange(request.PlayerDesiredValues);
            message.PlayerDesiredCounts.AddRange(request.PlayerDesiredCounts);
            return message;
        }

        public static byte[] EncodeContainerEditBytes(in ContainerEditRequestData request) =>
            EncodeContainerEdit(in request).ToByteArray();

        public static bool TryConvertContainerEdit(P.ContainerEditRequest message,
            out ContainerEditRequestData request, out string error)
        {
            request = default;
            error = null;
            if (message == null)
            {
                error = "ContainerEditRequest 为空";
                return false;
            }
            if (!ContainerKinds.IsContainer((int)message.ContainerKind))
            {
                error = "container_kind 非法：" + message.ContainerKind;
                return false;
            }
            if (!TryCopySlots(message.BaseValues, message.BaseCounts, out int[] baseValues,
                    out int[] baseCounts, out error) ||
                !TryCopySlots(message.DesiredValues, message.DesiredCounts, out int[] desiredValues,
                    out int[] desiredCounts, out error) ||
                !TryCopySlots(message.PlayerBaseValues, message.PlayerBaseCounts,
                    out int[] playerBaseValues, out int[] playerBaseCounts, out error) ||
                !TryCopySlots(message.PlayerDesiredValues, message.PlayerDesiredCounts,
                    out int[] playerDesiredValues, out int[] playerDesiredCounts, out error))
            {
                return false;
            }
            if (desiredValues.Length != baseValues.Length ||
                desiredCounts.Length != baseCounts.Length)
            {
                error = "容器 base/desired 槽位数不一致（base=" + baseValues.Length +
                    " desired=" + desiredValues.Length + "）";
                return false;
            }
            if (playerDesiredValues.Length != playerBaseValues.Length ||
                playerDesiredCounts.Length != playerBaseCounts.Length)
            {
                error = "玩家背包 base/desired 槽位数不一致";
                return false;
            }
            if (baseValues.Length == 0)
            {
                error = "容器槽位为空";
                return false;
            }
            request = new ContainerEditRequestData(message.Sequence, message.X, message.Y,
                message.Z, (int)message.ContainerKind, message.BaseRevision, baseValues,
                baseCounts, desiredValues, desiredCounts, playerBaseValues, playerBaseCounts,
                playerDesiredValues, playerDesiredCounts);
            return true;
        }

        public static bool TryDecodeContainerEdit(byte[] payload,
            out ContainerEditRequestData request, out string error)
        {
            request = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            P.ContainerEditRequest message;
            try
            {
                message = P.ContainerEditRequest.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析 ContainerEditRequest 失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvertContainerEdit(message, out request, out error);
        }

        public static P.ContainerEditResult EncodeContainerEditResult(
            in ContainerEditResultData result)
        {
            return new P.ContainerEditResult
            {
                Sequence = result.Sequence,
                Accepted = result.Accepted,
                Reason = result.Reason ?? string.Empty,
                Revision = result.Revision,
                ContainerKind = (P.ContainerKind)result.ContainerKind
            };
        }

        public static byte[] EncodeContainerEditResultBytes(in ContainerEditResultData result) =>
            EncodeContainerEditResult(in result).ToByteArray();

        public static bool TryConvertContainerEditResult(P.ContainerEditResult message,
            out ContainerEditResultData result, out string error)
        {
            result = default;
            error = null;
            if (message == null)
            {
                error = "ContainerEditResult 为空";
                return false;
            }
            int kind = (int)message.ContainerKind;
            if (!ContainerKinds.IsContainer(kind))
            {
                error = "container_kind 非法：" + kind;
                return false;
            }
            if (!message.Accepted && string.IsNullOrEmpty(message.Reason))
            {
                error = "accepted=false 但没有拒绝原因";
                return false;
            }
            result = new ContainerEditResultData(message.Sequence, message.Accepted,
                message.Reason, message.Revision, kind);
            return true;
        }

        public static bool TryDecodeContainerEditResult(byte[] payload,
            out ContainerEditResultData result, out string error)
        {
            result = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            P.ContainerEditResult message;
            try
            {
                message = P.ContainerEditResult.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析 ContainerEditResult 失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvertContainerEditResult(message, out result, out error);
        }

        /// <summary>
        /// 校验并复制一组槽位数组：等长、数量非负、空槽 value 必须为 0、槽位数有上限。
        /// 返回 false 时 <paramref name="error"/> 是可直接写进日志的原因。
        /// </summary>
        public static bool TryCopySlots(IList<int> values, IList<int> counts, out int[] outValues,
            out int[] outCounts, out string error)
        {
            outValues = Array.Empty<int>();
            outCounts = Array.Empty<int>();
            error = null;
            if (values == null || counts == null)
            {
                error = "槽位数组为空";
                return false;
            }
            if (values.Count != counts.Count)
            {
                error = "value/count 长度不一致（" + values.Count + "/" + counts.Count + "）";
                return false;
            }
            if (values.Count > MaxSlots)
            {
                error = "槽位数超限（" + values.Count + " > " + MaxSlots + "）";
                return false;
            }
            for (int i = 0; i < values.Count; i++)
            {
                if (counts[i] < 0)
                {
                    error = "槽位 " + i + " 数量为负：" + counts[i];
                    return false;
                }
                if (values[i] < 0)
                {
                    error = "槽位 " + i + " 值为负：" + values[i];
                    return false;
                }
                if (counts[i] == 0 ? values[i] != 0 : values[i] == 0)
                {
                    error = "槽位 " + i + " value/count 不一致（value=" + values[i] +
                        " count=" + counts[i] + "）";
                    return false;
                }
            }
            outValues = new int[values.Count];
            outCounts = new int[counts.Count];
            for (int i = 0; i < values.Count; i++)
            {
                outValues[i] = values[i];
                outCounts[i] = counts[i];
            }
            return true;
        }

        /// <summary>按 value 汇总一件物品的总数（容器 / 背包守恒校验用）。</summary>
        public static void AccumulateItems(int[] values, int[] counts, System.Collections.Generic.Dictionary<int, long> target)
        {
            int slots = Math.Min(values?.Length ?? 0, counts?.Length ?? 0);
            for (int i = 0; i < slots; i++)
            {
                if (counts[i] <= 0 || values[i] == 0) continue;
                target.TryGetValue(values[i], out long current);
                target[values[i]] = current + counts[i];
            }
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        public static string DescribeSlots(int[] values, int[] counts)
        {
            int slots = Math.Min(values?.Length ?? 0, counts?.Length ?? 0);
            int nonEmpty = 0;
            for (int i = 0; i < slots; i++)
            {
                if (counts[i] > 0) nonEmpty++;
            }
            return slots.ToString(CultureInfo.InvariantCulture) + " slots, " +
                nonEmpty.ToString(CultureInfo.InvariantCulture) + " non-empty";
        }
    }
}

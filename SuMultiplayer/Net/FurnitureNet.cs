using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Google.Protobuf;
using SuMultiplayer.Furniture;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>家具设计请求的操作码（协议 <c>FurnitureDesignOp</c> 的领域镜像）。</summary>
    public static class FurnitureDesignOps
    {
        public const int Create = 0;
        public const int Paint = 1;
        public const int Rename = 2;

        public static bool IsValid(int op) => op == Create || op == Paint || op == Rename;

        public static string Name(int op) =>
            op == Paint ? "paint" : op == Rename ? "rename" : "create";
    }

    /// <summary>客户端 -> 主机：一次家具设计创建 / 编辑 / 上色意图（序号由模块分配）。</summary>
    public readonly struct FurnitureDesignRequestData
    {
        public FurnitureDesignRequestData(long sequence, int op, int startX, int startY,
            int startZ, int face, int resolution, string name, int interactionMode,
            int[] values, int[] sourceX, int[] sourceY, int[] sourceZ, int[] sourceValues,
            int baseDesignIndex, int paintColor, bool hasReach = false, float reach = 0f)
        {
            Sequence = sequence;
            Op = op;
            StartX = startX;
            StartY = startY;
            StartZ = startZ;
            Face = face;
            Resolution = resolution;
            Name = name ?? string.Empty;
            InteractionMode = interactionMode;
            Values = values ?? Array.Empty<int>();
            SourceX = sourceX ?? Array.Empty<int>();
            SourceY = sourceY ?? Array.Empty<int>();
            SourceZ = sourceZ ?? Array.Empty<int>();
            SourceValues = sourceValues ?? Array.Empty<int>();
            BaseDesignIndex = baseDesignIndex;
            PaintColor = paintColor;
            HasReach = hasReach;
            Reach = reach;
        }

        public long Sequence { get; }
        public int Op { get; }
        public int StartX { get; }
        public int StartY { get; }
        public int StartZ { get; }
        public int Face { get; }
        public int Resolution { get; }
        public string Name { get; }
        public int InteractionMode { get; }
        public int[] Values { get; }
        public int[] SourceX { get; }
        public int[] SourceY { get; }
        public int[] SourceZ { get; }
        public int[] SourceValues { get; }
        public int BaseDesignIndex { get; }
        public int PaintColor { get; }
        /// <summary>请求是否携带了有效 reach（false = 旧客户端 / 旧载荷）。</summary>
        public bool HasReach { get; }
        /// <summary>请求者的有效交互距离（米）；<see cref="HasReach"/> = false 时无意义。</summary>
        public float Reach { get; }

        public int SourceCount => Math.Min(Math.Min(SourceX.Length, SourceY.Length),
            Math.Min(SourceZ.Length, SourceValues.Length));

        public FurnitureDesignRequestData WithSequence(long sequence) =>
            new FurnitureDesignRequestData(sequence, Op, StartX, StartY, StartZ, Face,
                Resolution, Name, InteractionMode, Values, SourceX, SourceY, SourceZ,
                SourceValues, BaseDesignIndex, PaintColor, HasReach, Reach);

        public string Describe() =>
            FurnitureDesignOps.Name(Op) + " seq=" + Sequence.ToString(CultureInfo.InvariantCulture) +
            " @" + StartX.ToString(CultureInfo.InvariantCulture) + "," +
            StartY.ToString(CultureInfo.InvariantCulture) + "," +
            StartZ.ToString(CultureInfo.InvariantCulture) + " face=" + Face +
            " res=" + Resolution + " mode=" + InteractionMode +
            " base=" + BaseDesignIndex + " color=" + PaintColor +
            " values=" + Values.Length + " sources=" + SourceCount;
    }

    /// <summary>主机 -> 客户端：一次设计请求的裁决（序号回显）。</summary>
    public readonly struct FurnitureDesignResultData
    {
        public FurnitureDesignResultData(long sequence, bool accepted, string reason,
            int designIndex, long contentHash, bool duplicate, int revision,
            bool pickableSpawned)
        {
            Sequence = sequence;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            DesignIndex = designIndex;
            ContentHash = contentHash;
            Duplicate = duplicate;
            Revision = revision;
            PickableSpawned = pickableSpawned;
        }

        public long Sequence { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public int DesignIndex { get; }
        public long ContentHash { get; }
        public bool Duplicate { get; }
        public int Revision { get; }
        public bool PickableSpawned { get; }

        public string Describe() =>
            "seq=" + Sequence.ToString(CultureInfo.InvariantCulture) + " accepted=" + Accepted +
            " index=" + DesignIndex + " duplicate=" + Duplicate +
            (Accepted ? string.Empty : " reason=" + Reason);
    }

    /// <summary>主机 -> 客户端：一批权威设计记录 + 显式移除。</summary>
    public sealed class FurnitureDesignStateData
    {
        public FurnitureDesignStateData(int revision, bool full, string reason,
            IReadOnlyList<FurnitureDesignRecord> designs, IReadOnlyList<int> removed)
        {
            Revision = revision;
            Full = full;
            Reason = reason ?? string.Empty;
            Designs = designs ?? Array.Empty<FurnitureDesignRecord>();
            Removed = removed ?? Array.Empty<int>();
        }

        public int Revision { get; }
        public bool Full { get; }
        public string Reason { get; }
        public IReadOnlyList<FurnitureDesignRecord> Designs { get; }
        public IReadOnlyList<int> Removed { get; }
    }

    /// <summary>主机 -> 客户端：家具内容表（= 主机设计表里的全部设计索引 + 内容哈希）。</summary>
    public sealed class FurnitureContentData
    {
        public FurnitureContentData(int revision, bool full, string reason,
            IReadOnlyList<int> indices, IReadOnlyList<long> hashes)
        {
            Revision = revision;
            Full = full;
            Reason = reason ?? string.Empty;
            Indices = indices ?? Array.Empty<int>();
            Hashes = hashes ?? Array.Empty<long>();
        }

        public int Revision { get; }
        public bool Full { get; }
        public string Reason { get; }
        public IReadOnlyList<int> Indices { get; }
        public IReadOnlyList<long> Hashes { get; }

        public string Describe() =>
            "rev=" + Revision + (Full ? " full" : " delta") + " designs=" + Indices.Count +
            (string.IsNullOrEmpty(Reason) ? string.Empty : " reason=" + Reason);
    }

    /// <summary>
    /// 家具协议编解码 + 结构校验（纯逻辑，不引用 Game）。
    ///
    /// 设计值用 run-length 编码（run_counts / run_values 两个并行数组），
    /// 上限 <see cref="MaxValues"/> = 16³；一条消息最多 <see cref="MaxPayloadBytes"/>
    /// （帧上限 60 KiB，留出余量）。超限 / 越界 / 非法名字一律**明确拒绝**，不截断。
    ///
    /// Source: Survivalcraft/Game/FurnitureDesign.cs:SetValues/Save/Name
    /// Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:ScanDesign
    /// </summary>
    public static class FurnitureDesignCodec
    {
        public const int MinResolution = 2;
        public const int MaxResolution = 16;
        public const int MaxValues = MaxResolution * MaxResolution * MaxResolution;
        public const int MaxSourceCells = 4096;
        public const int MaxNameLength = 20;
        public const int MaxDesignIndex = 1023;
        public const int MaxDesignsPerState = 8;
        public const int MaxPayloadBytes = 48 * 1024;
        public const int MaxInteractionMode = 4;
        public const int MaxPaintColor = 255;

        // ------------------------------------------------------------------ 值 / 名字校验

        public static bool TryValidateName(string name, out string reason)
        {
            reason = null;
            if (name == null)
            {
                reason = "name-null";
                return false;
            }
            if (name.Length > MaxNameLength)
            {
                reason = "name-too-long:" + name.Length;
                return false;
            }
            if (name.Length == 0) return true;
            if (name[0] == ' ' || name[name.Length - 1] == ' ')
            {
                reason = "name-edge-space";
                return false;
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c > '\u007f' || (!char.IsLetterOrDigit(c) && c != ' '))
                {
                    reason = "name-char:" + ((int)c).ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }
            return true;
        }

        public static bool TryValidateValues(int resolution, IReadOnlyList<int> values,
            int blockCount, out string reason)
        {
            reason = null;
            if (resolution < MinResolution || resolution > MaxResolution)
            {
                reason = "resolution:" + resolution.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            int expected = resolution * resolution * resolution;
            if (values == null || values.Count != expected)
            {
                reason = "values-count:" + (values == null ? 0 : values.Count) + "!=" + expected;
                return false;
            }
            if (blockCount <= 0)
            {
                reason = "block-count-unavailable";
                return false;
            }
            for (int i = 0; i < values.Count; i++)
            {
                int value = FurnitureDesignHash.StripLight(values[i]);
                int contents = value & 0x3FF;
                if (contents >= blockCount)
                {
                    reason = "contents:" + contents.ToString(CultureInfo.InvariantCulture) +
                        ">=" + blockCount.ToString(CultureInfo.InvariantCulture) + "@" + i;
                    return false;
                }
            }
            return true;
        }

        /// <summary>结构校验（不含地形 / 距离 / 玩家等引擎侧校验；那些由端口做）。</summary>
        public static bool TryValidateRequest(in FurnitureDesignRequestData data, int blockCount,
            out string reason)
        {
            reason = null;
            if (!FurnitureDesignOps.IsValid(data.Op))
            {
                reason = "op:" + data.Op.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (data.Face < 0 || data.Face > 5)
            {
                reason = "face:" + data.Face.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (data.StartY < 0 || data.StartY > 255)
            {
                reason = "start-y:" + data.StartY.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (data.Op == FurnitureDesignOps.Paint)
            {
                if (data.BaseDesignIndex < 0 || data.BaseDesignIndex > MaxDesignIndex)
                {
                    reason = "paint-base:" + data.BaseDesignIndex.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (data.PaintColor < -1 || data.PaintColor > MaxPaintColor)
                {
                    reason = "paint-color:" + data.PaintColor.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                return true;
            }
            if (data.Op == FurnitureDesignOps.Rename)
            {
                if (data.BaseDesignIndex < 0 || data.BaseDesignIndex > MaxDesignIndex)
                {
                    reason = "rename-base:" + data.BaseDesignIndex.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                return TryValidateName(data.Name, out reason);
            }
            if (!TryValidateName(data.Name, out reason)) return false;
            if (data.InteractionMode < 0 || data.InteractionMode > MaxInteractionMode)
            {
                reason = "interaction-mode:" + data.InteractionMode.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (data.BaseDesignIndex < -1 || data.BaseDesignIndex > MaxDesignIndex)
            {
                reason = "base-design:" + data.BaseDesignIndex.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!TryValidateValues(data.Resolution, data.Values, blockCount, out reason))
                return false;
            int count = data.SourceCount;
            if (count <= 0)
            {
                reason = "sources-empty";
                return false;
            }
            if (count > MaxSourceCells)
            {
                reason = "sources-too-many:" + count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (data.SourceX.Length != count || data.SourceY.Length != count ||
                data.SourceZ.Length != count || data.SourceValues.Length != count)
            {
                reason = "sources-length";
                return false;
            }
            var unique = new HashSet<long>();
            for (int i = 0; i < count; i++)
            {
                int x = data.SourceX[i], y = data.SourceY[i], z = data.SourceZ[i];
                if (y < 0 || y > 255)
                {
                    reason = "source-y:" + y.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (Math.Abs(x - data.StartX) >= 16 || Math.Abs(y - data.StartY) >= 16 ||
                    Math.Abs(z - data.StartZ) >= 16)
                {
                    reason = "source-too-far:" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                long key = ((long)(uint)x << 40) ^ ((long)(uint)y << 20) ^ (uint)z;
                if (!unique.Add(key))
                {
                    reason = "source-duplicate:" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }
            return true;
        }

        public static bool TryValidateRecord(FurnitureDesignRecord record, int blockCount,
            out string reason)
        {
            reason = null;
            if (record == null)
            {
                reason = "record-null";
                return false;
            }
            if (!FurnitureDesignStore.IsValidIndex(record.Index))
            {
                reason = "index:" + record.Index.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (record.LinkedIndex < -1 || record.LinkedIndex > MaxDesignIndex)
            {
                reason = "linked:" + record.LinkedIndex.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (record.InteractionMode < 0 || record.InteractionMode > MaxInteractionMode)
            {
                reason = "interaction-mode:" + record.InteractionMode.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!TryValidateName(record.Name, out reason)) return false;
            if (record.FurnitureSet != null && record.FurnitureSet.Length > 20)
            {
                reason = "set-name-too-long:" + record.FurnitureSet.Length;
                return false;
            }
            return TryValidateValues(record.Resolution, record.Values, blockCount, out reason);
        }

        // ------------------------------------------------------------------ run-length

        public static bool TryEncodeRuns(IReadOnlyList<int> values, out int[] runCounts,
            out int[] runValues, out string reason)
        {
            runCounts = Array.Empty<int>();
            runValues = Array.Empty<int>();
            reason = null;
            if (values == null || values.Count == 0)
            {
                reason = "values-empty";
                return false;
            }
            if (values.Count > MaxValues)
            {
                reason = "values-too-many:" + values.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            var counts = new List<int>(values.Count / 4 + 1);
            var runs = new List<int>(values.Count / 4 + 1);
            int current = FurnitureDesignHash.StripLight(values[0]);
            int count = 1;
            for (int i = 1; i < values.Count; i++)
            {
                int value = FurnitureDesignHash.StripLight(values[i]);
                if (value == current)
                {
                    count++;
                    continue;
                }
                counts.Add(count);
                runs.Add(current);
                current = value;
                count = 1;
            }
            counts.Add(count);
            runs.Add(current);
            runCounts = counts.ToArray();
            runValues = runs.ToArray();
            return true;
        }

        public static bool TryDecodeRuns(IReadOnlyList<int> runCounts,
            IReadOnlyList<int> runValues, int resolution, out int[] values, out string reason)
        {
            values = Array.Empty<int>();
            reason = null;
            if (resolution < MinResolution || resolution > MaxResolution)
            {
                reason = "resolution:" + resolution.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            int expected = resolution * resolution * resolution;
            if (runCounts == null || runValues == null || runCounts.Count != runValues.Count)
            {
                reason = "runs-length";
                return false;
            }
            if (runCounts.Count == 0 || runCounts.Count > expected)
            {
                reason = "runs-count:" + runCounts.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            var buffer = new int[expected];
            int cursor = 0;
            for (int i = 0; i < runCounts.Count; i++)
            {
                int count = runCounts[i];
                if (count <= 0)
                {
                    reason = "run-count:" + count.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (cursor + count > expected)
                {
                    reason = "runs-overflow:" + (cursor + count) + ">" + expected;
                    return false;
                }
                int value = FurnitureDesignHash.StripLight(runValues[i]);
                for (int j = 0; j < count; j++) buffer[cursor + j] = value;
                cursor += count;
            }
            if (cursor != expected)
            {
                reason = "runs-total:" + cursor + "!=" + expected;
                return false;
            }
            values = buffer;
            return true;
        }

        // ------------------------------------------------------------------ record <-> proto

        public static P.FurnitureDesignRecord ToProto(FurnitureDesignRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (!TryEncodeRuns(record.Values, out int[] counts, out int[] runs,
                    out string reason))
                throw new InvalidOperationException("cannot encode design values: " + reason);
            var message = new P.FurnitureDesignRecord
            {
                Index = record.Index,
                Resolution = record.Resolution,
                Name = record.Name ?? string.Empty,
                InteractionMode = (uint)record.InteractionMode,
                LinkedIndex = record.LinkedIndex,
                FurnitureSet = record.FurnitureSet ?? string.Empty,
                ContentHash = record.ContentHash
            };
            message.RunCounts.AddRange(counts);
            message.RunValues.AddRange(runs);
            return message;
        }

        public static bool TryFromProto(P.FurnitureDesignRecord message,
            out FurnitureDesignRecord record, out string reason)
        {
            record = null;
            reason = null;
            if (message == null)
            {
                reason = "record-null";
                return false;
            }
            if (message.InteractionMode > (uint)MaxInteractionMode)
            {
                reason = "interaction-mode:" + message.InteractionMode;
                return false;
            }
            if (!TryDecodeRuns(message.RunCounts, message.RunValues, message.Resolution,
                    out int[] values, out reason))
                return false;
            var parsed = new FurnitureDesignRecord(message.Index, message.Resolution,
                message.Name ?? string.Empty, (int)message.InteractionMode,
                message.LinkedIndex, message.FurnitureSet ?? string.Empty, values);
            if (message.ContentHash != 0L && parsed.ContentHash != message.ContentHash)
            {
                reason = "content-hash-mismatch:" + parsed.ContentHash.ToString("x16") + "!=" +
                    message.ContentHash.ToString("x16");
                return false;
            }
            record = parsed;
            return true;
        }

        // ------------------------------------------------------------------ request

        public static bool TryEncodeRequest(in FurnitureDesignRequestData data,
            out P.FurnitureDesignRequest message, out string reason)
        {
            message = null;
            reason = null;
            if (!TryValidateRequest(in data, int.MaxValue, out reason)) return false;
            var encoded = new P.FurnitureDesignRequest
            {
                Sequence = data.Sequence,
                Op = (P.FurnitureDesignOp)data.Op,
                StartX = data.StartX,
                StartY = data.StartY,
                StartZ = data.StartZ,
                Face = data.Face,
                Resolution = data.Resolution,
                Name = data.Name ?? string.Empty,
                InteractionMode = (uint)data.InteractionMode,
                BaseDesignIndex = data.BaseDesignIndex,
                PaintColor = data.PaintColor
            };
            if (data.Op == FurnitureDesignOps.Create)
            {
                if (!TryEncodeRuns(data.Values, out int[] counts, out int[] runs,
                        out string runReason))
                {
                    reason = runReason;
                    return false;
                }
                encoded.RunCounts.AddRange(counts);
                encoded.RunValues.AddRange(runs);
                int count = data.SourceCount;
                for (int i = 0; i < count; i++)
                {
                    encoded.SourceX.Add(data.SourceX[i]);
                    encoded.SourceY.Add(data.SourceY[i]);
                    encoded.SourceZ.Add(data.SourceZ[i]);
                    encoded.SourceValues.Add(FurnitureDesignHash.StripLight(data.SourceValues[i]));
                }
            }
            if (data.HasReach) encoded.Reach = data.Reach;
            message = encoded;
            return true;
        }

        public static byte[] EncodeRequestBytes(in FurnitureDesignRequestData data)
        {
            if (!TryEncodeRequest(in data, out P.FurnitureDesignRequest message,
                    out string reason))
                throw new ArgumentException("invalid furniture design request: " + reason);
            return message.ToByteArray();
        }

        public static bool TryDecodeRequest(P.FurnitureDesignRequest message,
            out FurnitureDesignRequestData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "request-null";
                return false;
            }
            int op = (int)message.Op;
            if (!FurnitureDesignOps.IsValid(op))
            {
                reason = "op:" + op.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            int[] values = Array.Empty<int>();
            if (op == FurnitureDesignOps.Create)
            {
                if (!TryDecodeRuns(message.RunCounts, message.RunValues, message.Resolution,
                        out values, out reason))
                    return false;
            }
            int count = message.SourceX.Count;
            if (message.SourceY.Count != count || message.SourceZ.Count != count ||
                message.SourceValues.Count != count)
            {
                reason = "sources-length";
                return false;
            }
            var decoded = new FurnitureDesignRequestData(message.Sequence, op, message.StartX,
                message.StartY, message.StartZ, message.Face, message.Resolution,
                message.Name ?? string.Empty, (int)message.InteractionMode, values,
                message.SourceX.ToArray(), message.SourceY.ToArray(),
                message.SourceZ.ToArray(), message.SourceValues.ToArray(),
                message.BaseDesignIndex, message.PaintColor, message.HasReach, message.Reach);
            if (!TryValidateRequest(in decoded, int.MaxValue, out reason)) return false;
            data = decoded;
            return true;
        }

        // ------------------------------------------------------------------ result

        public static byte[] EncodeResultBytes(in FurnitureDesignResultData data)
        {
            var message = new P.FurnitureDesignResult
            {
                Sequence = data.Sequence,
                Accepted = data.Accepted,
                Reason = data.Reason ?? string.Empty,
                DesignIndex = data.DesignIndex,
                ContentHash = data.ContentHash,
                Duplicate = data.Duplicate,
                Revision = data.Revision,
                PickableSpawned = data.PickableSpawned
            };
            return message.ToByteArray();
        }

        public static bool TryDecodeResult(P.FurnitureDesignResult message,
            out FurnitureDesignResultData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "result-null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "result-sequence:" + message.Sequence.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!message.Accepted && string.IsNullOrEmpty(message.Reason))
            {
                reason = "result-reason-empty";
                return false;
            }
            if (message.Accepted && !FurnitureDesignStore.IsValidIndex(message.DesignIndex))
            {
                reason = "result-index:" + message.DesignIndex.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            data = new FurnitureDesignResultData(message.Sequence, message.Accepted,
                message.Reason ?? string.Empty, message.DesignIndex, message.ContentHash,
                message.Duplicate, message.Revision, message.PickableSpawned);
            return true;
        }

        // ------------------------------------------------------------------ state / content

        public static bool TryEncodeState(FurnitureDesignStateData state,
            out P.FurnitureDesignState message, out string reason)
        {
            message = null;
            reason = null;
            if (state == null)
            {
                reason = "state-null";
                return false;
            }
            if (state.Designs.Count > MaxDesignsPerState)
            {
                reason = "state-designs:" + state.Designs.Count.ToString(CultureInfo.InvariantCulture) +
                    ">" + MaxDesignsPerState;
                return false;
            }
            var encoded = new P.FurnitureDesignState
            {
                Revision = state.Revision,
                Full = state.Full,
                Reason = state.Reason ?? string.Empty
            };
            for (int i = 0; i < state.Designs.Count; i++)
            {
                encoded.Designs.Add(ToProto(state.Designs[i]));
            }
            for (int i = 0; i < state.Removed.Count; i++)
                encoded.RemovedIndices.Add(state.Removed[i]);
            message = encoded;
            return true;
        }

        public static bool TryDecodeState(P.FurnitureDesignState message,
            out FurnitureDesignStateData state, out string reason)
        {
            state = null;
            reason = null;
            if (message == null)
            {
                reason = "state-null";
                return false;
            }
            if (message.Designs.Count > MaxDesignsPerState)
            {
                reason = "state-designs:" + message.Designs.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            var designs = new List<FurnitureDesignRecord>(message.Designs.Count);
            for (int i = 0; i < message.Designs.Count; i++)
            {
                if (!TryFromProto(message.Designs[i], out FurnitureDesignRecord record,
                        out reason))
                    return false;
                designs.Add(record);
            }
            var removed = new List<int>(message.RemovedIndices.Count);
            for (int i = 0; i < message.RemovedIndices.Count; i++)
            {
                int index = message.RemovedIndices[i];
                if (!FurnitureDesignStore.IsValidIndex(index))
                {
                    reason = "removed-index:" + index.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                removed.Add(index);
            }
            state = new FurnitureDesignStateData(message.Revision, message.Full,
                message.Reason ?? string.Empty, designs, removed);
            return true;
        }

        public static bool TryEncodeContent(FurnitureContentData content,
            out P.FurnitureContentState message, out string reason)
        {
            message = null;
            reason = null;
            if (content == null)
            {
                reason = "content-null";
                return false;
            }
            if (content.Indices.Count != content.Hashes.Count)
            {
                reason = "content-length";
                return false;
            }
            if (content.Indices.Count > FurnitureDesignStore.MaxDesigns)
            {
                reason = "content-designs:" + content.Indices.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            var encoded = new P.FurnitureContentState
            {
                Revision = content.Revision,
                Full = content.Full,
                Reason = content.Reason ?? string.Empty
            };
            encoded.DesignIndices.AddRange(content.Indices);
            encoded.DesignHashes.AddRange(content.Hashes);
            message = encoded;
            return true;
        }

        public static bool TryDecodeContent(P.FurnitureContentState message,
            out FurnitureContentData content, out string reason)
        {
            content = null;
            reason = null;
            if (message == null)
            {
                reason = "content-null";
                return false;
            }
            if (message.DesignIndices.Count != message.DesignHashes.Count)
            {
                reason = "content-length:" + message.DesignIndices.Count + "!=" +
                    message.DesignHashes.Count;
                return false;
            }
            if (message.DesignIndices.Count > FurnitureDesignStore.MaxDesigns)
            {
                reason = "content-designs:" + message.DesignIndices.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            for (int i = 0; i < message.DesignIndices.Count; i++)
            {
                int index = message.DesignIndices[i];
                if (!FurnitureDesignStore.IsValidIndex(index))
                {
                    reason = "content-index:" + index.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }
            content = new FurnitureContentData(message.Revision, message.Full,
                message.Reason ?? string.Empty, message.DesignIndices.ToArray(),
                message.DesignHashes.ToArray());
            return true;
        }

        public static byte[] EncodeStateBytes(FurnitureDesignStateData state)
        {
            if (!TryEncodeState(state, out P.FurnitureDesignState message,
                    out string reason))
                throw new ArgumentException("invalid furniture design state: " + reason);
            return message.ToByteArray();
        }

        public static byte[] EncodeContentBytes(FurnitureContentData content)
        {
            if (!TryEncodeContent(content, out P.FurnitureContentState message,
                    out string reason))
                throw new ArgumentException("invalid furniture content state: " + reason);
            return message.ToByteArray();
        }

        /// <summary>估算一条记录编码后的大小（批量切分用；保守上界）。</summary>
        public static int EstimateRecordBytes(FurnitureDesignRecord record)
        {
            if (record == null) return 64;
            int runs = 0;
            if (record.ValueCount > 0)
            {
                runs = 1;
                int last = record.GetValue(0);
                for (int i = 1; i < record.ValueCount; i++)
                {
                    int value = record.GetValue(i);
                    if (value != last)
                    {
                        runs++;
                        last = value;
                    }
                }
            }
            return 96 + (record.Name == null ? 0 : record.Name.Length * 2) +
                (record.FurnitureSet == null ? 0 : record.FurnitureSet.Length * 2) +
                runs * 12;
        }
    }
}

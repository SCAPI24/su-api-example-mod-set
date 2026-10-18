using System;
using System.Collections.Generic;

using SuMultiplayer.Protocol;

using Google.Protobuf;
using PT = SuMultiplayer.Protocol.Terrain;
namespace SuMultiplayer.Net
{
    /// <summary>一条"当前内存区块集合"条目的状态位（1 = 在客户端内存里，0 = 已脱离内存）。</summary>
    public readonly struct ClientChunkEntry
    {
        public ClientChunkEntry(int x, int z, bool loaded)
        {
            X = x;
            Z = z;
            Loaded = loaded;
        }

        public int X { get; }
        public int Z { get; }
        public bool Loaded { get; }
    }

    /// <summary>一条 <see cref="MessageType.ClientChunks"/> 消息是整集分片还是增量。</summary>
    public enum ClientChunkSetMode : byte
    {
        /// <summary>加载 / 卸载增量：逐条 add / remove。</summary>
        Delta = 0,

        /// <summary>整集分片：必须收齐同一 <see cref="ClientChunkSetMessage.FullSetId"/> 的全部分片才整体替换。</summary>
        FullSet = 1
    }

    /// <summary>解码后的一条 <see cref="MessageType.ClientChunks"/> 消息。</summary>
    public sealed class ClientChunkSetMessage
    {
        public ClientChunkSetMode Mode;

        /// <summary>整集序号（同一客户端一次整集 = 一个 id；增量恒为 0）。</summary>
        public int FullSetId;

        /// <summary>本分片下标（0 起；增量恒为 0）。</summary>
        public int PartIndex;

        /// <summary>整集共几个分片（≥1；增量恒为 1）。</summary>
        public int PartCount = 1;

        public readonly List<ClientChunkEntry> Entries = new List<ClientChunkEntry>();
    }

    /// <summary>
    /// 客户端"当前内存区块集合"载荷（<see cref="MessageType.ClientChunks"/>）。
    ///
    /// 为什么需要它（实机口径）：实时地图的客户端线框此前取
    /// <c>PlayerSessionRecord.ReportedLoaded</c> —— 那是**会话累计**的摘要上报集合（只增不减），
    /// 客户端卸载区块后主机没有渠道知道，旧路径的方块就一直画在图上。用户要求：
    /// "客户端的方框只显示当前存在于客户端内存的，已经脱离内存的格子就取消绘制"。
    ///
    /// 载荷（proto：Protocol/terrain.proto 的 <c>ClientChunks</c>）：
    ///   version / mode / [FullSet: full_set_id + part_index + part_count] /
    ///   repeated Entry { x, z, loaded }
    ///
    /// 整集为什么要分片：一个客户端常驻 150+ 块，单条消息最多 64 条（≈600 B），
    /// 上限必须显式；主机按 fullSetId 收齐全部分片后**整体替换**该身份的内存集合
    /// （分片到达期间保留旧集合，不会把地图闪成半张）。整集里只允许 loaded=1。
    ///
    /// 非法载荷（版本不对 / 模式未知 / 条目超过上限 / 整集分片里出现 loaded=0 /
    /// 分片下标越界 / protobuf 非法）一律由 <see cref="TryDecode"/> 明确失败，
    /// 由主机计入 reject 计数并限流写一行原因 —— 绝不猜、绝不部分应用。
    /// </summary>
    public static class ClientChunkSetCodec
    {
        /// <summary>载荷版本（ClientChunks.version；不匹配就明确失败）。</summary>
        public const int Version = 1;

        /// <summary>单条消息最多几条区块条目（整集分片与增量同一上限，主机按它拒绝超限载荷）。</summary>
        public const int MaxEntriesPerMessage = 64;

        /// <summary>一次整集最多几个分片（1024 × 64 = 65536 块；超过就是客户端侧异常，明确失败）。</summary>
        public const int MaxFullSetParts = 1024;

        /// <summary>一条条目的固定字节数（旧线格式口径；现在由 protobuf 编码，保留给预算参考）。</summary>
        public const int EntryBytes = 9;

        /// <summary>本协议允许的最大载荷长度（主机可据此先做一次粗检）。</summary>
        public const int MaxPayloadBytes = 16 + 16 + MaxEntriesPerMessage * 12;

        public static byte[] EncodeDelta(IReadOnlyList<ClientChunkEntry> entries) =>
            Encode(PT.ChunkSetMode.Delta, 0, 0, 1, entries, requireLoaded: false);

        public static byte[] EncodeFullSetPart(int fullSetId, int partIndex, int partCount,
            IReadOnlyList<ClientChunkEntry> entries)
        {
            if (fullSetId <= 0) throw new ArgumentOutOfRangeException(nameof(fullSetId), "整集序号必须 > 0");
            if (partCount < 1 || partCount > MaxFullSetParts)
                throw new ArgumentOutOfRangeException(nameof(partCount), "整集分片数必须在 1.." + MaxFullSetParts);
            if (partIndex < 0 || partIndex >= partCount)
                throw new ArgumentOutOfRangeException(nameof(partIndex), "分片下标必须在 0..partCount-1");
            return Encode(PT.ChunkSetMode.FullSet, fullSetId, partIndex, partCount, entries,
                requireLoaded: true);
        }

        private static byte[] Encode(PT.ChunkSetMode mode, int fullSetId, int partIndex, int partCount,
            IReadOnlyList<ClientChunkEntry> entries, bool requireLoaded)
        {
            int count = entries?.Count ?? 0;
            if (count < 0 || count > MaxEntriesPerMessage)
                throw new ArgumentOutOfRangeException(nameof(entries),
                    "单条消息最多 " + MaxEntriesPerMessage + " 条区块条目（本次 " + count + "）");
            var message = new PT.ClientChunks
            {
                Version = Version,
                Mode = mode,
                FullSetId = fullSetId,
                PartIndex = partIndex,
                PartCount = partCount
            };
            for (int i = 0; i < count; i++)
            {
                ClientChunkEntry entry = entries[i];
                if (requireLoaded && !entry.Loaded)
                    throw new ArgumentException("整集分片里不允许出现 loaded=0 的条目", nameof(entries));
                message.Entries.Add(new PT.ClientChunks.Types.Entry
                {
                    X = entry.X,
                    Z = entry.Z,
                    Loaded = entry.Loaded
                });
            }
            return message.ToByteArray();
        }

        /// <summary>
        /// 解析一条载荷。返回 false = **明确失败**（消息被拒绝，调用方计入 reject 并限流写一行原因）；
        /// 返回 true 时载荷一定完整、条目数与上限严格一致，调用方可以放心逐条应用。
        /// </summary>
        public static bool TryDecode(byte[] payload, out ClientChunkSetMessage message, out string error)
        {
            message = null;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空（至少需要 ClientChunks.version）";
                return false;
            }
            PT.ClientChunks decoded;
            try
            {
                decoded = PT.ClientChunks.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "载荷不是合法 protobuf：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvert(decoded, out message, out error);
        }

        /// <summary>
        /// 校验一条已解析的 <see cref="PT.ClientChunks"/> 并转换成领域对象。
        /// 强类型处理器（MessageRegistry）用这个入口，避免"解析→再序列化→再解析"。
        /// </summary>
        public static bool TryConvert(PT.ClientChunks decoded, out ClientChunkSetMessage message,
            out string error)
        {
            message = null;
            error = null;
            if (decoded == null)
            {
                error = "消息为 null";
                return false;
            }
            if (decoded.Version != Version)
            {
                error = "载荷版本 v" + decoded.Version + " ≠ v" + Version + "（两端必须用同一份协议）";
                return false;
            }
            if (decoded.Mode != PT.ChunkSetMode.Delta && decoded.Mode != PT.ChunkSetMode.FullSet)
            {
                error = "未知的模式 " + (int)decoded.Mode + "（只有 0=增量 / 1=整集）";
                return false;
            }
            var result = new ClientChunkSetMessage
            {
                Mode = decoded.Mode == PT.ChunkSetMode.FullSet
                    ? ClientChunkSetMode.FullSet
                    : ClientChunkSetMode.Delta
            };
            if (result.Mode == ClientChunkSetMode.FullSet)
            {
                result.FullSetId = decoded.FullSetId;
                result.PartIndex = decoded.PartIndex;
                result.PartCount = decoded.PartCount;
                if (result.FullSetId <= 0)
                {
                    error = "整集序号 " + result.FullSetId + " 非法（必须 > 0）";
                    return false;
                }
                if (result.PartCount < 1 || result.PartCount > MaxFullSetParts)
                {
                    error = "整集分片数 " + result.PartCount + " 超出 1.." + MaxFullSetParts +
                        "（超上限的载荷整条拒绝）";
                    return false;
                }
                if (result.PartIndex < 0 || result.PartIndex >= result.PartCount)
                {
                    error = "分片下标 " + result.PartIndex + " 不在 0.." + (result.PartCount - 1) + " 内";
                    return false;
                }
            }
            int count = decoded.Entries.Count;
            if (count < 0 || count > MaxEntriesPerMessage)
            {
                error = "区块条目数 " + count + " 超出 0.." + MaxEntriesPerMessage + "（超上限的载荷整条拒绝）";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                PT.ClientChunks.Types.Entry entry = decoded.Entries[i];
                if (result.Mode == ClientChunkSetMode.FullSet && !entry.Loaded)
                {
                    error = "整集分片第 " + (i + 1) + " 条条目是 loaded=0（整集只描述**当前存在**的区块）";
                    return false;
                }
                result.Entries.Add(new ClientChunkEntry(entry.X, entry.Z, entry.Loaded));
            }
            message = result;
            return true;
        }
    }
}

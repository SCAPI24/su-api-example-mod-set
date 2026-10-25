using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Google.Protobuf;
using SuMultiplayer.Join;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;
using PTransfer = SuMultiplayer.Protocol.Transfer;

using SuMultiplayer.Protocol;

namespace SuMultiplayer.Transfer
{
    /// <summary>
    /// 世界传输用的通道（由持有 TCP 连接的模块提供）。
    /// 传输模块**不自己建连接**：它只拿到这个端口，从而与地形模块解耦。
    /// </summary>
    public interface ITransferChannel
    {
        bool TrySend(MessageType type, byte[] payload);
        bool IsConnected { get; }
        /// <summary>通道状态字（诊断/日志用）。</summary>
        string StateText { get; }
    }

    /// <summary>
    /// 世界 pak 的打包/解包。
    /// 容器用 BCL 的 ZipArchive（不引第三方、不引引擎），文件名一律走
    /// <see cref="WorldArchivePolicy"/> 净化 —— 旧实现在这里吃过路径穿越的亏。
    /// 演示页标记：XFR01。
    /// </summary>
    public static class WorldPack
    {
        /// <summary>打包一个世界目录 → 单个 pak 字节。</summary>
        public static byte[] Pack(string worldDirectory, out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (!Directory.Exists(worldDirectory)) return memory.ToArray();
                string root = Path.GetFullPath(worldDirectory);
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!WorldArchivePolicy.TrySanitizeEntryName(relative, out string safe, out _)) continue;
                    var info = new FileInfo(file);
                    if (!WorldArchivePolicy.IsEntrySizeAllowed(info.Length, out _)) continue;
                    ZipArchiveEntry entry = archive.CreateEntry(safe, CompressionLevel.Fastest);
                    using Stream target = entry.Open();
                    using FileStream source = File.OpenRead(file);
                    source.CopyTo(target);
                    fileCount++;
                    totalBytes += info.Length;
                }
            }
            return memory.ToArray();
        }

        /// <summary>解包 pak 到目标目录；返回写入的文件数。任何条目名/大小不合格直接拒绝。</summary>
        public static int Unpack(byte[] pak, string targetDirectory, out long writtenBytes, out string reason)
        {
            writtenBytes = 0;
            reason = null;
            int count = 0;
            try
            {
                Directory.CreateDirectory(targetDirectory);
                using var memory = new MemoryStream(pak ?? Array.Empty<byte>());
                using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!WorldArchivePolicy.TrySanitizeEntryName(entry.FullName, out string safe, out reason))
                        return count;
                    if (!WorldArchivePolicy.IsEntrySizeAllowed(entry.Length, out reason))
                        return count;
                    string path = Path.Combine(targetDirectory, safe.Replace('/', Path.DirectorySeparatorChar));
                    string parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    using Stream source = entry.Open();
                    using FileStream target = File.Create(path);
                    source.CopyTo(target);
                    writtenBytes += entry.Length;
                    count++;
                }
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
            }
            return count;
        }
    }

    /// <summary>世界传输的消息编解码（proto: Protocol/transfer.proto）。</summary>
    public static class WorldTransferCodec
    {
        public static byte[] EncodeBegin(int transferId, string worldName, string directoryName,
            int totalBytes, int chunkCount, int chunkBytes, string sessionId, long baseSequence)
        {
            return EncodeBegin(transferId, worldName, directoryName, totalBytes, chunkCount,
                chunkBytes, sessionId, baseSequence, 0, null);
        }

        public static byte[] EncodeBegin(int transferId, string worldName, string directoryName,
            int totalBytes, int chunkCount, int chunkBytes, string sessionId, long baseSequence,
            int randomSeed, IReadOnlyDictionary<string, long> randomStates)
        {
            var message = new PTransfer.WorldBegin
            {
                TransferId = transferId,
                WorldName = worldName ?? string.Empty,
                DirectoryName = directoryName ?? string.Empty,
                TotalBytes = totalBytes,
                ChunkCount = chunkCount,
                ChunkBytes = chunkBytes,
                SessionId = sessionId ?? string.Empty,
                BaseSequence = baseSequence,
                Random = RandomStateCodec.ToMessage(randomSeed, randomStates)
            };
            return message.ToByteArray();
        }

        public static bool TryDecodeBegin(byte[] payload, out int transferId, out string worldName,
            out string directoryName, out int totalBytes, out int chunkCount, out int chunkBytes,
            out string sessionId, out long baseSequence)
        {
            return TryDecodeBegin(payload, out transferId, out worldName, out directoryName,
                out totalBytes, out chunkCount, out chunkBytes, out sessionId, out baseSequence,
                out _, out _, out _);
        }

        public static bool TryDecodeBegin(byte[] payload, out int transferId, out string worldName,
            out string directoryName, out int totalBytes, out int chunkCount, out int chunkBytes,
            out string sessionId, out long baseSequence, out int randomSeed,
            out Dictionary<string, long> randomStates, out string randomReason)
        {
            transferId = 0; worldName = null; directoryName = null;
            totalBytes = 0; chunkCount = 0; chunkBytes = 0;
            sessionId = string.Empty; baseSequence = 0L;
            randomSeed = 0;
            randomStates = new Dictionary<string, long>(StringComparer.Ordinal);
            randomReason = null;
            if (payload == null || payload.Length == 0)
            {
                randomReason = "载荷为空";
                return false;
            }
            PTransfer.WorldBegin message;
            try
            {
                message = PTransfer.WorldBegin.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                randomReason = "载荷不是合法 protobuf：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            transferId = message.TransferId;
            worldName = message.WorldName ?? string.Empty;
            directoryName = message.DirectoryName ?? string.Empty;
            totalBytes = message.TotalBytes;
            chunkCount = message.ChunkCount;
            chunkBytes = message.ChunkBytes;
            sessionId = message.SessionId ?? string.Empty;
            baseSequence = message.BaseSequence;
            randomSeed = message.Random?.Seed ?? 0;
            randomStates = RandomStateCodec.FromMessage(message.Random, out int seed);
            randomSeed = seed;
            if (chunkCount <= 0) return false;
            return true;
        }

        public static byte[] EncodeChunk(int transferId, int index, int count, long checksum,
            byte[] data, int offset, int length)
        {
            var message = new PTransfer.WorldChunk
            {
                TransferId = transferId,
                Index = index,
                Count = count,
                Checksum = checksum,
                Data = ByteString.CopyFrom(data, offset, length)
            };
            return message.ToByteArray();
        }

        public static bool TryDecodeChunk(byte[] payload, out int transferId, out int index, out int count,
            out long checksum, out byte[] data)
        {
            transferId = 0; index = 0; count = 0; checksum = 0; data = null;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PTransfer.WorldChunk message = PTransfer.WorldChunk.Parser.ParseFrom(payload);
                transferId = message.TransferId;
                index = message.Index;
                count = message.Count;
                checksum = message.Checksum;
                data = message.Data?.ToByteArray() ?? Array.Empty<byte>();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static byte[] EncodeMissing(int transferId, IReadOnlyList<int> missing)
        {
            var message = new PTransfer.WorldMissing { TransferId = transferId };
            if (missing != null)
            {
                for (int i = 0; i < missing.Count; i++) message.Indexes.Add(missing[i]);
            }
            return message.ToByteArray();
        }

        public static bool TryDecodeMissing(byte[] payload, out int transferId, out List<int> missing)
        {
            transferId = 0;
            missing = new List<int>();
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PTransfer.WorldMissing message = PTransfer.WorldMissing.Parser.ParseFrom(payload);
                transferId = message.TransferId;
                for (int i = 0; i < message.Indexes.Count; i++) missing.Add(message.Indexes[i]);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static byte[] EncodeEnd(int transferId, bool ok, string reason)
        {
            var message = new PTransfer.WorldEnd
            {
                TransferId = transferId,
                Ok = ok,
                Reason = reason ?? string.Empty
            };
            return message.ToByteArray();
        }

        public static bool TryDecodeEnd(byte[] payload, out int transferId, out bool ok, out string reason)
        {
            transferId = 0; ok = false; reason = string.Empty;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PTransfer.WorldEnd message = PTransfer.WorldEnd.Parser.ParseFrom(payload);
                transferId = message.TransferId;
                ok = message.Ok;
                reason = message.Reason ?? string.Empty;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>主机侧世界导出（引擎实现：WorldsManager.ExportWorld）。</summary>
    public interface IWorldExportSource
    {
        bool TryExport(out byte[] pak, out string worldName, out string directoryName, out string reason);
    }

    /// <summary>客户端侧世界导入（引擎实现：WorldsManager.ImportWorld）。</summary>
    public interface IWorldImporter
    {
        bool TryImport(byte[] pak, out string installedDirectory, out string reason);
    }

    /// <summary>
    /// 下载世界的登记/删除（引擎实现：Storage 登记文件 + WorldsManager.DeleteWorld）。
    ///
    /// 为什么要有它（旧实现 <c>ScMultiplayer.RegisterDownloadedWorld</c> /
    /// <c>CleanupDownloadedWorldsIfIdle</c> 的契约）：
    /// **客户端下载来的世界是临时的** —— 回到世界列表时就要删掉。
    /// 否则每加入一次房间就多出一个世界目录（实测平板堆到 World2/World3…），
    /// 世界列表里全是同名副本，玩家分不清哪个是本机世界、哪个是下载来的。
    ///
    /// 会话标识（本版追加）：下载来的世界还要记住"它是哪个主机会话的快照"（sessionId + 导出时的
    /// 改动序号 baseSequence）。客户端重连时把它连同"上次应用序号"一起发回 Hello；
    /// 主机只接受会话一致、序号仍在历史里的重连，否则明确要求整图重下。
    /// </summary>
    public interface IDownloadedWorldStore
    {
        /// <summary>登记一个"下载来的世界"目录名（新登记没有会话标识，重连时会按首次加入处理）。</summary>
        void Register(string directory);

        /// <summary>已登记的目录（读登记文件；文件不存在就是空表）。</summary>
        IReadOnlyList<string> Registered { get; }

        /// <summary>
        /// 这个目录是不是"下载来的世界"（判定放在该世界目录自己的会话登记文件里）。
        /// 默认实现 = 遍历 <see cref="Registered"/>；实现方可以给更便宜的查法。
        /// </summary>
        bool IsDownloaded(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;
            IReadOnlyList<string> registered = Registered;
            if (registered == null) return false;
            for (int i = 0; i < registered.Count; i++)
            {
                if (string.Equals(registered[i], directory, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>把登记表写回（只剩删不掉的那些；已有条目的会话标识原样保留）。</summary>
        void SaveRegistry(IReadOnlyList<string> directories);

        /// <summary>删除一个下载来的世界目录。</summary>
        bool Delete(string directory, out string reason);

        /// <summary>
        /// 把下载世界与"主机会话标识 + 导出基线序号"登记在一起（WorldBegin 收到后、导入完成时调用）。
        /// 空 <paramref name="sessionId"/> 是**明确**的"没有会话标识"（旧主机），不猜。
        /// </summary>
        void RegisterSession(string directory, string sessionId, long baseSequence);

        /// <summary>读回登记；（没有登记 / 没有会话标识）返回 false，并给出空值。</summary>
        bool TryGetSession(string directory, out string sessionId, out long baseSequence);

        /// <summary>
        /// 明确把某个下载世界的会话标识**作废**（主机判定历史已裁剪 / 会话不一致 / 客户端要求整图重下）。
        /// 目录本身保留（还是"下载来的世界"，回到列表时会清理）；只是之后不能再声明"同一会话"。
        /// </summary>
        void ForgetSession(string directory);
    }

    /// <summary>
    /// 主机侧：把世界目录打成 pak，分片发给一个客户端；按需重发缺失分片。
    /// **不做连接管理**（那是通道端口的事），也不碰引擎（世界目录是文件）。
    /// 演示页标记：XFR02。
    /// </summary>
    public sealed class WorldTransferHost
    {
        private readonly ITransferChannel m_channel;
        private byte[] m_pak;
        private int m_chunkCount;
        private int m_chunkBytes;
        private int m_transferId;
        private int m_nextIndex;
        public string WorldName { get; private set; } = string.Empty;
        public int ChunksPerTick { get; set; } = 8;
        public long ChunksSent { get; private set; }
        public long BytesSent { get; private set; }
        public bool InProgress => m_pak != null && m_nextIndex < m_chunkCount;

        public WorldTransferHost(ITransferChannel channel) { m_channel = channel; }

        /// <summary>准备一份世界（打包一次，可发给多个客户端）。</summary>
        public bool Prepare(string worldDirectory, string worldName, string directoryName,
            string sessionId, long baseSequence, int chunkBytes = 48 * 1024,
            int randomSeed = 0, IReadOnlyDictionary<string, long> randomStates = null)
        {
            m_pak = WorldPack.Pack(worldDirectory, out int files, out long total);
            if (m_pak.Length == 0) return false;
            m_chunkBytes = Math.Max(1024, Math.Min(chunkBytes, TerrainFrame.MaxPayloadBytes - 64));
            m_chunkCount = WorldTransferReceiver.PlanChunkCount(m_pak.Length, m_chunkBytes);
            m_nextIndex = 0;
            m_transferId = Environment.TickCount & 0x7FFFFFF;
            ChunksSent = 0;
            BytesSent = 0;
            WorldName = worldName ?? string.Empty;
            m_directoryName = directoryName ?? string.Empty;
            SessionId = sessionId ?? string.Empty;
            BaseSequence = baseSequence;
            RandomSeed = randomSeed;
            RandomStates = randomStates == null
                ? new Dictionary<string, long>(StringComparer.Ordinal)
                : new Dictionary<string, long>(randomStates, StringComparer.Ordinal);
            WorldBytes = total;
            FileCount = files;
            return true;
        }

        private string m_directoryName = string.Empty;
        public long WorldBytes { get; private set; }
        public int FileCount { get; private set; }
        public int ChunkCount => m_chunkCount;
        public int TransferId => m_transferId;
        /// <summary>世界清单里的主机会话标识（建房时生成，绝不落盘；见 SessionIdentity）。</summary>
        public string SessionId { get; private set; } = string.Empty;
        /// <summary>
        /// 世界清单里的**导出基线序号**：导出开始那一刻主机已广播的改动序号。
        /// 客户端把它记为"我已应用到这个序号"；导出期间及之后发生的改动会在重连时追帧。
        /// </summary>
        public long BaseSequence { get; private set; }

        /// <summary>随世界清单下发的随机会话种子（0 = 没有同步；客户端明确按"不应用"处理）。</summary>
        public int RandomSeed { get; private set; }

        /// <summary>主机所有 Subsystems 上 Engine.Random 的状态（键 = 类型全名|字段名）。</summary>
        public IReadOnlyDictionary<string, long> RandomStates { get; private set; } =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>直接给 pak 字节（引擎自己的 ExportWorld 产物，比自压 zip 更正统）。</summary>
        public bool PrepareFromPak(byte[] pak, string worldName, string directoryName,
            string sessionId, long baseSequence, int chunkBytes = 48 * 1024,
            int randomSeed = 0, IReadOnlyDictionary<string, long> randomStates = null)
        {
            if (pak == null || pak.Length == 0) return false;
            m_pak = pak;
            m_chunkBytes = Math.Max(1024, Math.Min(chunkBytes, TerrainFrame.MaxPayloadBytes - 64));
            m_chunkCount = WorldTransferReceiver.PlanChunkCount(m_pak.Length, m_chunkBytes);
            m_nextIndex = 0;
            m_transferId = Environment.TickCount & 0x7FFFFFF;
            // 进度计数属于**这一次传输**：多客户端时主机用同一个实例一条一条发，
            // 计数必须跟着 Prepare 归零，否则第二个客户端的进度/完成判定会带着上一个客户端的数。
            ChunksSent = 0;
            BytesSent = 0;
            WorldName = worldName ?? string.Empty;
            m_directoryName = directoryName ?? string.Empty;
            SessionId = sessionId ?? string.Empty;
            BaseSequence = baseSequence;
            RandomSeed = randomSeed;
            RandomStates = randomStates == null
                ? new Dictionary<string, long>(StringComparer.Ordinal)
                : new Dictionary<string, long>(randomStates, StringComparer.Ordinal);
            WorldBytes = pak.Length;
            FileCount = 0;
            return true;
        }

        public void Restart()
        {
            m_nextIndex = 0;
            m_transferId = (Environment.TickCount & 0x7FFFFFF) ^ 0x5A5A;
        }

        /// <summary>
        /// 丢弃已经准备好的整图，**取消**这次发送（一个字节都不再发）。
        /// 与 <see cref="Restart"/> 的区别：Restart 是"从头再发一遍"，本方法是"这次不发了"——
        /// 重连端（Hello alreadyHasWorld=true）已经有过这个世界，重发会让它重新下载并导入一遍。
        /// </summary>
        public void AbortPendingSend()
        {
            m_pak = null;
            m_chunkCount = 0;
            m_chunkBytes = 0;
            m_nextIndex = 0;
            m_transferId = 0;
            WorldName = string.Empty;
            m_directoryName = string.Empty;
            SessionId = string.Empty;
            BaseSequence = 0L;
            RandomSeed = 0;
            RandomStates = new Dictionary<string, long>(StringComparer.Ordinal);
            WorldBytes = 0;
            FileCount = 0;
            ChunksSent = 0;
            BytesSent = 0;
        }

        /// <summary>发一批（每 tick 调一次）；返回本 tick 发出的分片数。</summary>
        public int Tick()
        {
            if (!InProgress || !m_channel.IsConnected) return 0;
            int sent = 0;
            while (sent < ChunksPerTick && m_nextIndex < m_chunkCount)
            {
                int offset = m_nextIndex * m_chunkBytes;
                int length = Math.Min(m_chunkBytes, m_pak.Length - offset);
                long checksum = WorldTransferReceiver.ComputeChecksum(m_pak, offset, length);
                byte[] payload = WorldTransferCodec.EncodeChunk(m_transferId, m_nextIndex, m_chunkCount,
                    checksum, m_pak, offset, length);
                if (!m_channel.TrySend(MessageType.WorldChunk, payload)) break;
                m_nextIndex++;
                sent++;
                ChunksSent++;
                BytesSent += length;
            }
            if (m_nextIndex == 0 && m_pak != null)
            {
                // 首个 tick 之前先把 Begin 发出去
            }
            return sent;
        }

        public bool SendBegin() => m_channel.TrySend(MessageType.WorldBegin,
            WorldTransferCodec.EncodeBegin(m_transferId, WorldName, m_directoryName,
                m_pak?.Length ?? 0, m_chunkCount, m_chunkBytes, SessionId, BaseSequence,
                RandomSeed, RandomStates));

        public bool SendEnd(bool ok, string reason) =>
            m_channel.TrySend(MessageType.WorldEnd, WorldTransferCodec.EncodeEnd(m_transferId, ok, reason));

        /// <summary>客户端报告缺片 → 重发。</summary>
        public int Resend(IReadOnlyList<int> indexes)
        {
            if (m_pak == null || indexes == null) return 0;
            int sent = 0;
            for (int i = 0; i < indexes.Count; i++)
            {
                int index = indexes[i];
                if (index < 0 || index >= m_chunkCount) continue;
                int offset = index * m_chunkBytes;
                int length = Math.Min(m_chunkBytes, m_pak.Length - offset);
                long checksum = WorldTransferReceiver.ComputeChecksum(m_pak, offset, length);
                if (!m_channel.TrySend(MessageType.WorldChunk,
                    WorldTransferCodec.EncodeChunk(m_transferId, index, m_chunkCount, checksum, m_pak, offset, length)))
                    break;
                sent++;
            }
            return sent;
        }
    }

    /// <summary>
    /// 客户端侧：收分片 → 组装 → 校验 → 解包进目标目录。
    /// 演示页标记：XFR03。
    /// </summary>
    public sealed class WorldTransferClient
    {
        private readonly ITransferChannel m_channel;
        private WorldTransferReceiver m_receiver;
        private int m_transferId;
        private string m_worldName = string.Empty;
        private string m_directoryName = string.Empty;
        private long m_missingRequestedMs;

        public WorldTransferClient(ITransferChannel channel) { m_channel = channel; }

        public bool Active { get; private set; }
        public string WorldName => m_worldName;
        public string DirectoryName => m_directoryName;
        /// <summary>世界清单里的主机会话标识（旧主机 / 缺字段 = 空串；空 = 不能声明"同一会话"）。</summary>
        public string SessionId { get; private set; } = string.Empty;
        /// <summary>世界清单里的导出基线序号（客户端应用这个世界的初始"上次应用序号"）。</summary>
        public long BaseSequence { get; private set; }
        public int Received => m_receiver?.ReceivedCount ?? 0;
        public int Total => m_receiver?.ChunkCount ?? 0;
        public double ProgressPercent => m_receiver?.ProgressPercent ?? 0;
        /// <summary>收齐并校验通过后的完整 pak（安装交给引擎的 IWorldImporter，本类不落盘）。</summary>
        public byte[] Assembled { get; private set; }
        /// <summary>是否已收齐并组装成功。</summary>
        public bool Complete { get; private set; }
        public string Error { get; private set; }
        /// <summary>世界清单里的随机会话种子（0 = 旧主机没有同步；客户端明确不应用）。</summary>
        public int RandomSeed { get; private set; }
        /// <summary>世界清单里的随机数状态表（旧主机 = 空）。</summary>
        public IReadOnlyDictionary<string, long> RandomStates { get; private set; } =
            new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>随机数状态区解码失败的原因（空 = 正常 / 旧主机没有该区）。</summary>
        public string RandomStateProblem { get; private set; } = string.Empty;

        public bool HandleBegin(PTransfer.WorldBegin message)
        {
            if (message == null) return false;
            int chunks = message.ChunkCount;
            int chunkBytes = message.ChunkBytes;
            if (chunks <= 0 || chunkBytes <= 0) return false;
            m_transferId = message.TransferId;
            m_worldName = message.WorldName ?? string.Empty;
            m_directoryName = message.DirectoryName ?? string.Empty;
            SessionId = message.SessionId ?? string.Empty;
            BaseSequence = message.BaseSequence;
            RandomSeed = message.Random?.Seed ?? 0;
            RandomStates = RandomStateCodec.FromMessage(message.Random, out _);
            RandomStateProblem = string.Empty;
            m_receiver = new WorldTransferReceiver(chunks, chunkBytes);
            m_receiver.MaxChunkBytes = chunkBytes;
            Assembled = null;
            Complete = false;
            Error = null;
            Active = true;
            return true;
        }

        public bool HandleChunk(PTransfer.WorldChunk message)
        {
            if (m_receiver == null || message == null) return false;
            if (message.TransferId != m_transferId || message.Count != m_receiver.ChunkCount) return false;
            byte[] data = message.Data?.ToByteArray() ?? Array.Empty<byte>();
            m_receiver.Accept(message.Index, data, message.Checksum);
            return true;
        }

        /// <summary>
        /// 收齐就组装并校验；返回 true = 已拿到完整 pak（**不落盘**）。
        /// 安装/进入世界是引擎侧的事（IWorldImporter → WorldsManager.ImportWorld）。
        /// </summary>
        public bool HandleEnd(PTransfer.WorldEnd message, long nowMs)
        {
            if (m_receiver == null) return false;
            if (Assembled == null)
            {
                Assembled = m_receiver.Assemble();
                if (Assembled == null)
                {
                    Error = "分片不齐：" + m_receiver.ReceivedCount + "/" + m_receiver.ChunkCount;
                    return false;
                }
            }
            Complete = true;
            Active = false;
            _ = message;
            _ = nowMs;
            return true;
        }

        /// <summary>缺片就请主机重发（限流）。返回请求数量。</summary>
        public int RequestMissing(long nowMs)
        {
            if (m_receiver == null || m_receiver.IsComplete) return 0;
            if (nowMs - m_missingRequestedMs < 1000) return 0;
            m_missingRequestedMs = nowMs;
            List<int> missing = m_receiver.MissingChunks(256);
            if (missing.Count == 0) return 0;
            return m_channel.TrySend(MessageType.WorldMissing,
                WorldTransferCodec.EncodeMissing(m_transferId, missing)) ? missing.Count : 0;
        }

    }
}

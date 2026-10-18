using System;
using System.Collections.Generic;

namespace SuMultiplayer.Join
{
    public enum ChunkAccept : byte
    {
        Accepted = 0,
        Duplicate = 1,
        OutOfRange = 2,
        ChecksumFailed = 3,
        TooLarge = 4,
        NotRequested = 5
    }

    /// <summary>
    /// 世界下载接收端：按分片序号重组、CRC 校验、缺失清单、进度与完整性。
    /// 迁移自旧实现的世界传输（分片 + 校验和 + 修复请求）的核心逻辑。
    /// 演示页标记：DL01。
    /// </summary>
    public sealed class WorldTransferReceiver
    {
        private readonly byte[][] m_chunks;
        private readonly bool[] m_received;
        private readonly long[] m_checksums;
        private int m_receivedCount;
        private int m_duplicateCount;
        private int m_rejectedCount;

        public WorldTransferReceiver(int chunkCount, int maxChunkBytes = 64 * 1024)
        {
            if (chunkCount <= 0) throw new ArgumentOutOfRangeException(nameof(chunkCount));
            m_chunks = new byte[chunkCount][];
            m_received = new bool[chunkCount];
            m_checksums = new long[chunkCount];
            MaxChunkBytes = maxChunkBytes;
        }

        public int ChunkCount => m_chunks.Length;
        public int ReceivedCount => m_receivedCount;
        public int DuplicateCount => m_duplicateCount;
        public int RejectedCount => m_rejectedCount;
        public int MaxChunkBytes { get; set; }
        public bool IsComplete => m_receivedCount == m_chunks.Length;
        public double ProgressPercent => (double)m_receivedCount / m_chunks.Length * 100.0;
        public long ReceivedBytes { get; private set; }

        /// <summary>接收一个分片。checksum 为 0 表示不校验。</summary>
        public ChunkAccept Accept(int index, byte[] data, long checksum)
        {
            if (index < 0 || index >= m_chunks.Length) { m_rejectedCount++; return ChunkAccept.OutOfRange; }
            if (m_received[index]) { m_duplicateCount++; return ChunkAccept.Duplicate; }
            if (data == null || data.Length == 0) { m_rejectedCount++; return ChunkAccept.TooLarge; }
            if (data.Length > MaxChunkBytes) { m_rejectedCount++; return ChunkAccept.TooLarge; }
            if (checksum != 0 && ComputeChecksum(data) != checksum) { m_rejectedCount++; return ChunkAccept.ChecksumFailed; }

            m_chunks[index] = data;
            m_checksums[index] = checksum;
            m_received[index] = true;
            m_receivedCount++;
            ReceivedBytes += data.Length;
            return ChunkAccept.Accepted;
        }

        /// <summary>缺失分片清单（用于向主机请求修复），最多返回 max 个。</summary>
        public List<int> MissingChunks(int max = 64)
        {
            var list = new List<int>();
            for (int i = 0; i < m_received.Length && list.Count < max; i++)
            {
                if (!m_received[i]) list.Add(i);
            }
            return list;
        }

        /// <summary>收齐后按序号拼出完整负载；未收齐返回 null。</summary>
        public byte[] Assemble()
        {
            if (!IsComplete) return null;
            long total = 0;
            for (int i = 0; i < m_chunks.Length; i++) total += m_chunks[i].Length;
            var result = new byte[total];
            int position = 0;
            for (int i = 0; i < m_chunks.Length; i++)
            {
                Buffer.BlockCopy(m_chunks[i], 0, result, position, m_chunks[i].Length);
                position += m_chunks[i].Length;
            }
            return result;
        }

        /// <summary>分片大小规划：把 totalBytes 切成固定大小的分片（末片可短）。</summary>
        public static int PlanChunkCount(long totalBytes, int chunkBytes)
        {
            if (totalBytes <= 0 || chunkBytes <= 0) return 0;
            return (int)((totalBytes + chunkBytes - 1) / chunkBytes);
        }

        /// <summary>区间校验和（世界 pak 分片用）。</summary>
        public static long ComputeChecksum(byte[] data, int offset, int length)
        {
            if (data == null || length <= 0) return 0;
            unchecked
            {
                long hash = 1469598103934665603L;
                for (int i = 0; i < length; i++)
                {
                    hash ^= data[offset + i];
                    hash *= 1099511628211L;
                }
                return hash == 0 ? 1 : hash;
            }
        }

        public static long ComputeChecksum(byte[] data)
        {
            unchecked
            {
                long hash = 1469598103934665603L;   // FNV-1a 64
                for (int i = 0; i < data.Length; i++)
                {
                    hash ^= data[i];
                    hash *= 1099511628211L;
                }
                return hash == 0 ? 1 : hash;
            }
        }
    }

    /// <summary>
    /// 世界包条目名净化：拒绝绝对路径、`..`、反斜杠、控制字符与空名。
    /// 迁移自旧实现的世界存档净化边界（防止 zip 穿越写文件）。
    /// 演示页标记：DL02。
    /// </summary>
    public static class WorldArchivePolicy
    {
        public const int MaxEntryNameLength = 200;
        public const long MaxEntryBytes = 32L * 1024 * 1024;

        public static bool TrySanitizeEntryName(string rawName, out string safeName, out string reason)
        {
            safeName = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(rawName)) { reason = "空条目名"; return false; }
            if (rawName.Length > MaxEntryNameLength) { reason = "条目名过长"; return false; }

            string name = rawName.Replace('\\', '/').TrimStart('/');
            if (name.Length == 0) { reason = "空条目名"; return false; }
            if (rawName.StartsWith("/", StringComparison.Ordinal) ||
                (rawName.Length > 1 && rawName[1] == ':'))
            { reason = "绝对路径"; return false; }

            string[] parts = name.Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..")
                {
                    reason = part == ".." ? "路径穿越（..）" : "非法路径段";
                    return false;
                }
                foreach (char c in part)
                {
                    if (c < 0x20 || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    {
                        reason = "非法字符";
                        return false;
                    }
                }
            }
            safeName = string.Join("/", parts);
            return true;
        }

        public static bool IsEntrySizeAllowed(long bytes, out string reason)
        {
            reason = bytes > MaxEntryBytes ? "条目过大（>" + (MaxEntryBytes / 1024 / 1024) + " MiB）" : null;
            return reason == null;
        }
    }
}

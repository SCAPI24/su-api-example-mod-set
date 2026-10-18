using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SuMultiplayer.Journal
{
    /// <summary>
    /// 导出/读取工具：把筛选结果落到独立文件，或把已有分段读回来做分析。
    /// 二进制导出沿用分段格式（头 + CRC 块），因此可以用同一个读取器回放。
    /// 演示页标记：EXP01。
    /// </summary>
    public static class JournalExporter
    {
        public const int RecordsPerBlock = 256;

        /// <summary>把筛选命中的记录导出成独立二进制文件。返回导出条数。</summary>
        public static int ExportBinary(StateChangeJournal journal, string path, in JournalFilterSpec spec)
        {
            if (journal == null) throw new ArgumentNullException(nameof(journal));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            JournalFilter filter = journal.CreateFilter(in spec);
            var buffer = new JournalRecord[RecordsPerBlock];
            byte[] block = new byte[JournalSegmentWriter.BlockHeaderBytes + RecordsPerBlock * JournalRecord.SizeInBytes];
            int total = 0;
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
            {
                WriteHeader(fs, filter.FromSequence + 1);
                long from = filter.FromSequence;
                while (true)
                {
                    var range = new JournalFilterSpec(spec.Categories, from,
                        spec.ToSequenceInclusive, spec.FromTimestampMs, spec.ToTimestampMs,
                        spec.ActorId, spec.MinimumSeverity, 0, RecordsPerBlock);
                    int count = journal.CreateFilter(in range).CopyTo(buffer.AsSpan());
                    if (count <= 0) break;
                    WriteBlock(fs, buffer, count, block);
                    total += count;
                    from = buffer[count - 1].Sequence;
                    if (count < RecordsPerBlock) break;
                }
                fs.Flush(true);
            }
            return total;
        }

        /// <summary>把筛选命中的记录导出成可读文本（每行一条，支持工单/复盘）。返回导出条数。</summary>
        public static int ExportText(StateChangeJournal journal, string path, in JournalFilterSpec spec)
        {
            JournalFilter filter = journal.CreateFilter(in spec);
            var sb = new StringBuilder(512);
            int total = 0;
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("#seq\ttimeMs\tcategory\tcode\trole\tseverity\tactor\ta\tb\tc\tpayload");
                foreach (JournalRecord record in filter)
                {
                    sb.Clear();
                    sb.Append(record.Sequence).Append('\t').Append(record.TimestampMs).Append('\t')
                        .Append(JournalNames.CategoryName(record.Category)).Append('\t')
                        .Append(JournalNames.CodeName(record.Code)).Append('\t')
                        .Append(record.Role).Append('\t').Append(record.Severity).Append('\t')
                        .Append(record.ActorId).Append('\t').Append(record.ValueA).Append('\t')
                        .Append(record.ValueB).Append('\t').Append(record.ValueC).Append('\t');
                    if (journal.TryGetPayload(in record, out string payload)) sb.Append(payload);
                    writer.WriteLine(sb.ToString());
                    total++;
                }
            }
            return total;
        }

        /// <summary>读取一个分段（或导出的）文件，按顺序回调。返回有效记录数；遇到损坏块即停止。</summary>
        public static long ReadFile(string path, Action<JournalRecord> visitor)
        {
            long count = 0;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] header = new byte[JournalSegmentWriter.SegmentHeaderBytes];
                if (ReadFull(fs, header, header.Length) < header.Length) return 0;
                if (BitConverter.ToUInt32(header, 0) != JournalSegmentWriter.SegmentMagic) return 0;
                byte[] blockHeader = new byte[JournalSegmentWriter.BlockHeaderBytes];
                byte[] payload = new byte[64 * 1024];
                while (true)
                {
                    if (ReadFull(fs, blockHeader, blockHeader.Length) < blockHeader.Length) break;
                    if (BitConverter.ToUInt32(blockHeader, 0) != JournalSegmentWriter.BlockMagic) break;
                    int records = BitConverter.ToUInt16(blockHeader, 4);
                    int payloadBytes = BitConverter.ToInt32(blockHeader, 16);
                    uint crc = BitConverter.ToUInt32(blockHeader, 20);
                    if (records <= 0 || payloadBytes != records * JournalRecord.SizeInBytes) break;
                    if (payload.Length < payloadBytes) payload = new byte[payloadBytes];
                    if (ReadFull(fs, payload, payloadBytes) < payloadBytes) break;
                    if (Crc32.Compute(payload, 0, payloadBytes) != crc) break;
                    for (int i = 0; i < records; i++)
                    {
                        visitor?.Invoke(ReadRecord(payload, i * JournalRecord.SizeInBytes));
                        count++;
                    }
                }
            }
            return count;
        }

        private static void WriteHeader(Stream stream, long firstSequence)
        {
            byte[] header = new byte[JournalSegmentWriter.SegmentHeaderBytes];
            JournalSegmentWriter.WriteSegmentHeaderTo(header, firstSequence);
            stream.Write(header, 0, header.Length);
        }

        private static void WriteBlock(Stream stream, JournalRecord[] records, int count, byte[] block)
        {
            int bytes = JournalSegmentWriter.WriteBlockTo(block, records.AsSpan(0, count));
            stream.Write(block, 0, bytes);
        }

        private static JournalRecord ReadRecord(byte[] buffer, int offset) =>
            JournalSegmentWriter.ReadRecord(buffer, offset);

        private static int ReadFull(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }
    }

    /// <summary>枚举名 → 短名（日志与 UI 用）。演示页表格与此一致。</summary>
    public static class JournalNames
    {
        private static readonly Dictionary<uint, string> Categories = new Dictionary<uint, string>
        {
            { (uint)JournalCategory.Session, "Session" },
            { (uint)JournalCategory.Approval, "Approval" },
            { (uint)JournalCategory.Transfer, "Transfer" },
            { (uint)JournalCategory.Terrain, "Terrain" },
            { (uint)JournalCategory.PlayerState, "PlayerState" },
            { (uint)JournalCategory.PlayerAction, "PlayerAction" },
            { (uint)JournalCategory.Inventory, "Inventory" },
            { (uint)JournalCategory.Entity, "Entity" },
            { (uint)JournalCategory.Circuit, "Circuit" },
            { (uint)JournalCategory.WorldControl, "WorldControl" },
            { (uint)JournalCategory.DataModification, "DataModification" },
            { (uint)JournalCategory.Region, "Region" },
            { (uint)JournalCategory.Capability, "Capability" },
            { (uint)JournalCategory.Chat, "Chat" },
            { (uint)JournalCategory.Metrics, "Metrics" },
            { (uint)JournalCategory.Diagnostics, "Diagnostics" }
        };

        public static string CategoryName(uint category)
        {
            string name;
            if (Categories.TryGetValue(category, out name)) return name;
            var sb = new StringBuilder();
            foreach (KeyValuePair<uint, string> pair in Categories)
            {
                if ((category & pair.Key) == 0) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(pair.Value);
            }
            return sb.Length > 0 ? sb.ToString() : "None";
        }

        public static string CodeName(ushort code)
        {
            foreach (JournalEventCode value in Enum.GetValues(typeof(JournalEventCode)))
            {
                if ((ushort)value == code) return value.ToString();
            }
            return "Code" + code.ToString(CultureInfo.InvariantCulture);
        }
    }
}

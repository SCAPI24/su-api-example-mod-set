using Engine;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessRenderingMod
{
    // Source: System.IO.StreamWriter and System.Threading.Tasks.Task
    // Game threads enqueue already-formatted, low-frequency audit records. Disk I/O stays here.
    internal sealed class ServerAuditLog : IDisposable
    {
        private const int MaximumQueuedRecords = 2048;
        private const long MaximumDirectoryBytes = 1024L * 1024L * 1024L;
        private const long RetainedDirectoryBytes = 700L * 1024L * 1024L;

        private readonly string m_directory;
        private readonly string m_filePrefix;
        private readonly int m_wakeBatchSize;
        private readonly ConcurrentQueue<string> m_records = new ConcurrentQueue<string>();
        private readonly AutoResetEvent m_signal = new AutoResetEvent(false);
        private readonly CancellationTokenSource m_cancellation = new CancellationTokenSource();
        private readonly Task m_worker;
        private int m_queuedRecords;
        private long m_droppedRecords;
        private long m_directoryBytes;
        private DateTime m_nextRescanUtc;
        private bool m_disposed;

        public ServerAuditLog(string directory)
            : this(directory, string.Empty, 1)
        {
        }

        public ServerAuditLog(string directory, string filePrefix, int wakeBatchSize)
        {
            m_directory = directory ?? throw new ArgumentNullException(nameof(directory));
            m_filePrefix = filePrefix ?? string.Empty;
            m_wakeBatchSize = Math.Max(1, wakeBatchSize);
            m_worker = Task.Factory.StartNew(WriterLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Enqueue(string record)
        {
            if (m_disposed || string.IsNullOrWhiteSpace(record)) return;
            if (Interlocked.Increment(ref m_queuedRecords) > MaximumQueuedRecords)
            {
                Interlocked.Decrement(ref m_queuedRecords);
                Interlocked.Increment(ref m_droppedRecords);
                return;
            }

            m_records.Enqueue(TrimRecord(record));
            if (Volatile.Read(ref m_queuedRecords) >= m_wakeBatchSize)
                m_signal.Set();
        }

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            m_cancellation.Cancel();
            m_signal.Set();
            try { m_worker.Wait(TimeSpan.FromSeconds(10)); }
            catch { }
            m_signal.Dispose();
            m_cancellation.Dispose();
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        private void WriterLoop()
        {
            StreamWriter writer = null;
            DateTime writerDate = DateTime.MinValue;
            try
            {
                Directory.CreateDirectory(m_directory);
                RescanAndTrim(null);
                while (!m_cancellation.IsCancellationRequested || !m_records.IsEmpty)
                {
                    m_signal.WaitOne(1000);
                    WritePendingRecords(ref writer, ref writerDate);
                }
                WritePendingRecords(ref writer, ref writerDate);
            }
            catch (Exception error)
            {
                Log.Error("[HeadlessRenderingMod] Server audit writer stopped: " +
                    error.GetType().Name + ": " + error.Message);
            }
            finally
            {
                writer?.Dispose();
            }
        }

        private void WritePendingRecords(ref StreamWriter writer, ref DateTime writerDate)
        {
            DateTime now = DateTime.Now;
            if (writer == null || writerDate != now.Date)
            {
                writer?.Dispose();
                writerDate = now.Date;
                string path = Path.Combine(m_directory,
                    m_filePrefix + writerDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
                RescanAndTrim(path);
            }

            long dropped = Interlocked.Exchange(ref m_droppedRecords, 0);
            if (dropped > 0)
                WriteLine(writer, now, "event=audit.queue_drop count=" + dropped.ToString(CultureInfo.InvariantCulture));

            bool wrote = false;
            while (m_records.TryDequeue(out string record))
            {
                Interlocked.Decrement(ref m_queuedRecords);
                WriteLine(writer, now, record);
                wrote = true;
            }
            if (!wrote && dropped == 0) return;

            writer.Flush();
            if (m_directoryBytes > MaximumDirectoryBytes || DateTime.UtcNow >= m_nextRescanUtc)
                RescanAndTrim(Path.Combine(m_directory,
                    m_filePrefix + writerDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"));
        }

        private void WriteLine(StreamWriter writer, DateTime now, string record)
        {
            string line = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                " " + record;
            writer.WriteLine(line);
            m_directoryBytes += new UTF8Encoding(false).GetByteCount(line) + Environment.NewLine.Length;
        }

        private void RescanAndTrim(string activePath)
        {
            FileInfo[] files = new DirectoryInfo(m_directory).GetFiles("*.log")
                .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
            long total = files.Sum(file => file.Length);
            if (total > MaximumDirectoryBytes)
            {
                foreach (FileInfo file in files)
                {
                    if (string.Equals(file.FullName, activePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        long length = file.Length;
                        file.Delete();
                        total -= length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (total <= RetainedDirectoryBytes) break;
                }
            }
            m_directoryBytes = total;
            m_nextRescanUtc = DateTime.UtcNow.AddHours(1);
        }

        private static string TrimRecord(string value)
        {
            string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= 768 ? normalized : normalized.Substring(0, 768);
        }
    }
}

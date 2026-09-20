using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 事件日志（P0-10，计划 §4.4 / §5.4）：写到 `<实例根>/PlayerAi/Logs/PlayerAi.log`。
    ///
    /// 用途：重载/切换/改写/录制这些**"发生了什么"**的记录 —— 游戏内 `Engine.Log` 会滚掉、
    /// 也不方便事后翻；这里留一份**按大小滚动、限量**的文件，供人排查与 P3 编辑器实时监视复用。
    ///
    /// 三条工程约束：
    ///   1. **写入失败绝不影响游戏**：任何 IO 异常只记 <see cref="LastError"/> 并停掉文件写入，
    ///      内存里的 <see cref="Recent"/> 仍然可用（`ai.status`/`ai.logs` 还能看）；
    ///   2. **滚动限量**：超过 <see cref="MaxBytes"/> 就滚 `PlayerAi.log` → `PlayerAi.1.log` → …，
    ///      只保留 <see cref="MaxFiles"/> 份，不会把玩家磁盘写满；
    ///   3. 线程安全：控制面可能从网络线程触达记录点。
    /// </summary>
    public sealed class AiEventLog
    {
        public const string DefaultFileName = "PlayerAi.log";

        private const string Header =
            "# PlayerAiMod event log (rolling: {0} bytes x {1} files). UTC timestamps, newest at the end.";

        private readonly object m_gate = new object();
        private readonly Queue<string> m_recent = new Queue<string>();

        private string m_path;
        private long m_bytes;
        private bool m_fileFailed;

        public AiEventLog(string directory, string fileName = DefaultFileName, int maxBytes = 256 * 1024,
            int maxFiles = 3, int recentCapacity = 64)
        {
            Directory = directory;
            FileName = string.IsNullOrEmpty(fileName) ? DefaultFileName : fileName;
            MaxBytes = maxBytes > 4096 ? maxBytes : 4096;
            MaxFiles = maxFiles > 1 ? maxFiles : 2;
            RecentCapacity = recentCapacity > 8 ? recentCapacity : 8;

            if (!string.IsNullOrEmpty(directory))
                m_path = System.IO.Path.Combine(directory, FileName);
        }

        public string Directory { get; }

        public string FileName { get; }

        public long MaxBytes { get; }

        public int MaxFiles { get; }

        public int RecentCapacity { get; }

        /// <summary>是否写文件（关掉仍保留内存里的最近记录）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>当前日志文件路径（目录不可用时为 null）。</summary>
        public string Path
        {
            get { return m_path; }
        }

        public long FileBytes
        {
            get
            {
                lock (m_gate)
                    return m_bytes;
            }
        }

        /// <summary>最近一次 IO 失败原因（成功写入后清空）。</summary>
        public string LastError { get; private set; }

        public int WriteCount { get; private set; }

        /// <summary>写一条记录（category 例如 reload/switch/edit/record/selftest）。</summary>
        public void Write(string category, string message)
        {
            string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                + " [" + (string.IsNullOrEmpty(category) ? "info" : category) + "] "
                + (message ?? string.Empty);

            lock (m_gate)
            {
                WriteCount++;
                m_recent.Enqueue(line);
                while (m_recent.Count > RecentCapacity)
                    m_recent.Dequeue();

                if (!Enabled || string.IsNullOrEmpty(m_path) || m_fileFailed)
                    return;

                try
                {
                    EnsureFile();
                    File.AppendAllText(m_path, line + Environment.NewLine, new UTF8Encoding(false));
                    m_bytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                    LastError = null;

                    if (m_bytes > MaxBytes)
                        Rotate();
                }
                catch (Exception exception)
                {
                    // 日志写不进去绝对不能影响游戏：记一次原因就不再尝试（本局只报一次）
                    m_fileFailed = true;
                    LastError = exception.GetType().Name + ": " + exception.Message;
                    Engine.Log.Warning("[PlayerAi][log] file logging disabled for this session: "
                        + LastError);
                }
            }
        }

        /// <summary>最近 N 条记录（内存环，供 `ai.logs` / `ai.status` / P3 实时监视）。</summary>
        public List<string> Recent(int count = 0)
        {
            lock (m_gate)
            {
                var list = new List<string>(m_recent);
                if (count <= 0 || count >= list.Count)
                    return list;
                return list.GetRange(list.Count - count, count);
            }
        }

        /// <summary>把日志清空重来（`ai.logs clear=true`）。</summary>
        public bool TryClear(out string error)
        {
            error = null;
            lock (m_gate)
            {
                m_recent.Clear();
                if (string.IsNullOrEmpty(m_path))
                    return true;

                try
                {
                    for (int i = 0; i < MaxFiles; i++)
                    {
                        string path = RotatedPath(i);
                        if (path != null && File.Exists(path))
                            File.Delete(path);
                    }
                    m_bytes = 0;
                    m_fileFailed = false;
                    LastError = null;
                    return true;
                }
                catch (Exception exception)
                {
                    error = exception.GetType().Name + ": " + exception.Message;
                    return false;
                }
            }
        }

        public string Describe()
        {
            return "eventlog(" + (m_path ?? "<no directory>")
                + " bytes=" + FileBytes + "/" + MaxBytes
                + " files=" + MaxFiles
                + " entries=" + WriteCount
                + (Enabled ? string.Empty : " disabled")
                + (m_fileFailed ? " FILE-FAILED" : string.Empty) + ")";
        }

        public override string ToString()
        {
            return Describe();
        }

        // ---------------------------------------------------------------- 内部

        private void EnsureFile()
        {
            string directory = System.IO.Path.GetDirectoryName(m_path);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                System.IO.Directory.CreateDirectory(directory);

            if (!File.Exists(m_path))
            {
                string header = string.Format(CultureInfo.InvariantCulture, Header, MaxBytes, MaxFiles);
                File.WriteAllText(m_path, header + Environment.NewLine, new UTF8Encoding(false));
                m_bytes = Encoding.UTF8.GetByteCount(header) + Environment.NewLine.Length;
            }
            else
            {
                m_bytes = new FileInfo(m_path).Length;
            }
        }

        private void Rotate()
        {
            // PlayerAi.(MaxFiles-1) 会被丢掉，其余依次后移，当前文件变成 .1
            string oldest = RotatedPath(MaxFiles - 1);
            if (oldest != null && File.Exists(oldest))
                File.Delete(oldest);

            for (int i = MaxFiles - 2; i >= 1; i--)
            {
                string from = RotatedPath(i);
                string to = RotatedPath(i + 1);
                if (from != null && to != null && File.Exists(from))
                {
                    if (File.Exists(to))
                        File.Delete(to);
                    File.Move(from, to);
                }
            }

            string first = RotatedPath(1);
            if (first != null)
            {
                if (File.Exists(first))
                    File.Delete(first);
                File.Move(m_path, first);
            }

            m_bytes = 0;
            EnsureFile();
        }

        /// <summary>第 i 份滚动文件（i=0 就是当前文件）。</summary>
        private string RotatedPath(int index)
        {
            if (string.IsNullOrEmpty(m_path))
                return null;
            if (index <= 0)
                return m_path;

            string directory = System.IO.Path.GetDirectoryName(m_path);
            string name = System.IO.Path.GetFileNameWithoutExtension(m_path);
            string extension = System.IO.Path.GetExtension(m_path);
            return System.IO.Path.Combine(directory ?? string.Empty,
                name + "." + index.ToString(CultureInfo.InvariantCulture) + extension);
        }
    }
}

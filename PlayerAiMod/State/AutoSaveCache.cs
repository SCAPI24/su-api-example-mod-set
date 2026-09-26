using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>一次"自动缓存"的写入结果。</summary>
    public sealed class AutoSaveResult
    {
        public bool Ok;
        public string Path;
        public string Error;
        public long Bytes;

        public string Describe()
        {
            return Ok ? "cached " + Bytes + " bytes -> " + Path : "cache failed: " + Error;
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>缓存里的一条记录（读回来时用）。</summary>
    public sealed class AutoSaveEntry
    {
        public int SchemaVersion;
        public string Name;
        public string SourcePath;
        public string SourceHash;
        public string MemoryHash;
        public string SavedUtc;
        public long Sequence;
        public bool Dirty;
        public string PayloadPath;

        /// <summary>内容哈希（用于和手动存档比"谁更新"，也用于查半截文件）。</summary>
        public string ContentHash;

        public DateTime SavedTimeUtc
        {
            get
            {
                DateTime parsed;
                return DateTime.TryParse(SavedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed)
                    ? parsed
                    : DateTime.MinValue;
            }
        }

        public override string ToString()
        {
            return Name + " @" + SavedUtc + (Dirty ? " dirty" : string.Empty);
        }
    }

    /// <summary>
    /// **自动缓存**（`&lt;实例根&gt;/PlayerAi/.autosave/`）—— plan §4.11 的 R1~R4。
    ///
    /// 用户的设计："中间过程用 tmp 的临时文件夹进行 cache 回存；断电了重新加载，
    /// 看 tmp 的 cache 能否恢复（自动存档）；不能加载，就重头读入新的、再读手动存档。"
    ///
    /// 四条不能省的规则（都实现成机制，不靠自觉）：
    ///
    /// | # | 规则 | 落点 |
    /// |---|---|---|
    /// | **R1** | **自动缓存永不覆盖手动存档** | 只写 `.autosave/`；恢复时按**时间戳 + 内容哈希**比较，并把比较结果**明说**（<see cref="ChooseRecovery"/>） |
    /// | **R2** | 缓存**带版本头** | 每条记录有 `schemaVersion`；版本不认 → **当不可恢复**（宁可读存档，不猜） |
    /// | **R3** | 缓存**必须原子写** | 复用 `PackageWriter.TryWriteFile`（先 `.tmp` 再替换）——断电正好发生在写缓存时也不会留半截 |
    /// | **R4** | `.autosave/` **不是正式包** | 不参与"包存在"判断、不列进可分发清单；缺正式文件仍然拒包 |
    ///
    /// **为什么把这几条做成机制而不是注释**：断电恢复是"猜文件"的高危场景，
    /// 一次猜错（用了半截缓存 / 用缓存盖掉了人改过的正式包）就可能毁掉用户的手工成果，
    /// 而且**事后无法复盘**。
    /// </summary>
    public sealed class AutoSaveCache
    {
        public const int SchemaVersion = 1;
        public const string FolderName = ".autosave";
        public const string IndexFileName = "index.json";
        public const string PayloadExtension = ".pkg";

        private readonly string m_root;
        private long m_sequence;

        public AutoSaveCache(string instanceRoot)
        {
            m_root = ResolveDirectory(instanceRoot);
        }

        /// <summary>缓存目录（`&lt;实例根&gt;/PlayerAi/.autosave`；实例根为空时为 null）。</summary>
        public string Directory
        {
            get { return m_root; }
        }

        public bool Available
        {
            get { return !string.IsNullOrEmpty(m_root); }
        }

        public static string ResolveDirectory(string instanceRoot)
        {
            if (string.IsNullOrEmpty(instanceRoot))
                return null;
            return Path.Combine(instanceRoot, "PlayerAi", FolderName);
        }

        /// <summary>缓存目录是否被允许作为"包来源"（**R4：永远不允许**）。</summary>
        public static bool IsPackageSource(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            string normalized = path.Replace('\\', '/').ToLowerInvariant();
            return normalized.IndexOf("/" + FolderName + "/", StringComparison.Ordinal) >= 0
                || normalized.EndsWith("/" + FolderName, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- 写

        /// <summary>
        /// 写一条缓存（**原子写**，R3）。`payload` 是包字节；`memoryHash` 是内存态哈希，
        /// 用于"缓存 vs 存档谁更新"的比较。
        /// </summary>
        public AutoSaveResult Save(string name, byte[] payload, string sourcePath, string sourceHash,
            string memoryHash, bool dirty)
        {
            var result = new AutoSaveResult();
            if (!Available)
            {
                result.Error = "no autosave directory (instance root is unknown)";
                return result;
            }
            if (string.IsNullOrEmpty(name))
            {
                result.Error = "a cache entry needs a name";
                return result;
            }
            if (payload == null || payload.Length == 0)
            {
                result.Error = "refusing to cache an empty payload";
                return result;
            }

            try
            {
                System.IO.Directory.CreateDirectory(m_root);
            }
            catch (Exception exception)
            {
                result.Error = "cannot create " + m_root + ": " + exception.Message;
                return result;
            }

            string safe = Sanitize(name);
            long sequence = ++m_sequence;
            string payloadName = safe + "." + sequence.ToString(CultureInfo.InvariantCulture)
                + PayloadExtension;
            string payloadPath = Path.Combine(m_root, payloadName);

            // R3：原子写（先 .tmp 再替换）
            string writeError;
            if (!PackageWriter.TryWriteFile(payloadPath, payload, out writeError))
            {
                result.Error = "atomic write failed: " + writeError;
                return result;
            }

            var entry = new AutoSaveEntry
            {
                SchemaVersion = SchemaVersion,           // R2：带版本头
                Name = name,
                SourcePath = sourcePath,
                SourceHash = sourceHash,
                MemoryHash = memoryHash,
                SavedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Sequence = sequence,
                Dirty = dirty,
                PayloadPath = payloadName,
                ContentHash = PackageLoader.ComputeHash(payload)
            };

            if (!TryWriteIndex(entry, out string indexError))
            {
                result.Error = "index write failed: " + indexError;
                return result;
            }

            result.Ok = true;
            result.Path = payloadPath;
            result.Bytes = payload.Length;
            return result;
        }

        private bool TryWriteIndex(AutoSaveEntry entry, out string error)
        {
            try
            {
                var report = new PackageReport();
                PackageValue root = ReadIndexValue();
                if (root == null || !root.IsObject)
                {
                    root = PackageValue.Object();
                    root.Set("format", PackageValue.Str("autosave"));
                    root.Set("schemaVersion", PackageValue.Number(SchemaVersion));
                    root.Set("entries", PackageValue.Array());
                }
                else
                {
                    root.Set("schemaVersion", PackageValue.Number(SchemaVersion));
                }

                PackageValue entries = root.Get("entries");
                PackageValue array = entries.IsArray ? entries : PackageValue.Array();

                // 同名只留最新一条（缓存是影子副本，不需要历史）
                PackageValue kept = PackageValue.Array();
                var superseded = new List<string>();
                for (int i = 0; i < array.Count; i++)
                {
                    PackageValue item = array.Item(i);
                    if (item.IsObject && !string.Equals(item.Get("name").AsString(null), entry.Name,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        kept.Add(item);
                    }
                    else if (item.IsObject)
                    {
                        string old = item.Get("payload").AsString(null);
                        if (!string.IsNullOrEmpty(old))
                            superseded.Add(old);
                    }
                }
                kept.Add(EntryToValue(entry));
                root.Set("entries", kept);

                string json = root.ToJson(true);
                string indexPath = Path.Combine(m_root, IndexFileName);
                if (!PackageWriter.TryWriteFile(indexPath, new UTF8Encoding(false).GetBytes(json),
                    out error))
                {
                    return false;
                }

                // **索引写成功之后**才清理旧数据（顺序反了会删掉"旧索引还指着的那个文件"）
                for (int i = 0; i < superseded.Count; i++)
                    TryDeletePayload(superseded[i]);
                PruneOrphans(kept);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 清掉**没有任何索引条目引用**的 payload。
        ///
        /// 为什么必须有：`Save` 每次给同名资源写一个**新** payload 文件名（序号递增），
        /// 只靠"删被顶替的那一条"会漏掉更早的孤儿（进程中途退出、索引写失败都可能留下）。
        /// 不扫的话 `.autosave/` 会随着"改了又改"无限长下去 —— 缓存是影子，不该长成垃圾场。
        /// </summary>
        private void PruneOrphans(PackageValue entries)
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                PackageValue item = entries.Item(i);
                if (!item.IsObject)
                    continue;
                string payload = item.Get("payload").AsString(null);
                if (!string.IsNullOrEmpty(payload))
                    referenced.Add(payload);
            }

            string[] files;
            try
            {
                if (!System.IO.Directory.Exists(m_root))
                    return;
                files = System.IO.Directory.GetFiles(m_root, "*" + PayloadExtension,
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                return;
            }

            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (!referenced.Contains(name))
                    TryDeletePayload(name);
            }
        }

        private void TryDeletePayload(string payloadName)
        {
            try
            {
                string full = Path.Combine(m_root, payloadName);
                if (File.Exists(full))
                    File.Delete(full);
            }
            catch (Exception)
            {
                // 删不掉不影响正确性：下一次 Save 还会再扫一遍
            }
        }

        private static PackageValue EntryToValue(AutoSaveEntry entry)
        {
            PackageValue value = PackageValue.Object();
            value.Set("schemaVersion", PackageValue.Number(entry.SchemaVersion));
            value.Set("name", PackageValue.Str(entry.Name));
            value.Set("sourcePath", PackageValue.Str(entry.SourcePath));
            value.Set("sourceHash", PackageValue.Str(entry.SourceHash));
            value.Set("memoryHash", PackageValue.Str(entry.MemoryHash));
            value.Set("savedUtc", PackageValue.Str(entry.SavedUtc));
            value.Set("sequence", PackageValue.Number(entry.Sequence));
            value.Set("dirty", PackageValue.Bool(entry.Dirty));
            value.Set("payload", PackageValue.Str(entry.PayloadPath));
            value.Set("contentHash", PackageValue.Str(entry.ContentHash));
            return value;
        }

        // ---------------------------------------------------------------- 读

        /// <summary>
        /// 读出缓存索引（**R2：版本不认就当不可恢复**，不猜）。
        /// 返回的列表已经过滤掉"版本不对 / payload 缺失 / payload 内容对不上哈希"的记录 ——
        /// 即 **R3 的验证面**：半截文件不会被当成可恢复的缓存。
        /// </summary>
        public List<AutoSaveEntry> ReadEntries(int expectedSchema = SchemaVersion)
        {
            var entries = new List<AutoSaveEntry>();
            if (!Available)
                return entries;

            PackageValue root = ReadIndexValue();
            if (root == null || !root.IsObject)
                return entries;

            int version = root.Get("schemaVersion").AsInt(-1);
            if (version != expectedSchema)
            {
                // R2：版本不认 → 一条都不用（宁可读手动存档）
                LastRejectReason = "cache schema v" + version + " is not understood (this build reads v"
                    + expectedSchema + ")";
                return entries;
            }

            PackageValue array = root.Get("entries");
            if (!array.IsArray)
                return entries;

            for (int i = 0; i < array.Count; i++)
            {
                PackageValue item = array.Item(i);
                if (!item.IsObject)
                    continue;

                var entry = new AutoSaveEntry
                {
                    SchemaVersion = item.Get("schemaVersion").AsInt(-1),
                    Name = item.Get("name").AsString(null),
                    SourcePath = item.Get("sourcePath").AsString(null),
                    SourceHash = item.Get("sourceHash").AsString(null),
                    MemoryHash = item.Get("memoryHash").AsString(null),
                    SavedUtc = item.Get("savedUtc").AsString(null),
                    Sequence = (long)item.Get("sequence").AsNumber(0),
                    Dirty = item.Get("dirty").AsBool(false),
                    PayloadPath = item.Get("payload").AsString(null),
                    ContentHash = item.Get("contentHash").AsString(null)
                };

                if (string.IsNullOrEmpty(entry.Name) || string.IsNullOrEmpty(entry.PayloadPath))
                    continue;
                if (entry.SchemaVersion != expectedSchema)
                    continue;

                // 内容校验：读回来重算哈希，对不上就是半截/被改过 → 不可恢复
                try
                {
                    string full = Path.Combine(m_root, entry.PayloadPath);
                    if (!File.Exists(full))
                    {
                        LastRejectReason = "cache payload is missing: " + entry.PayloadPath;
                        continue;
                    }
                    string actual = PackageLoader.ComputeHash(File.ReadAllBytes(full));
                    if (!string.IsNullOrEmpty(entry.ContentHash)
                        && !string.Equals(actual, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        LastRejectReason = "cache payload does not match its hash (half-written?): "
                            + entry.PayloadPath;
                        continue;
                    }
                    entry.ContentHash = actual;
                    entries.Add(entry);
                }
                catch (Exception exception)
                {
                    LastRejectReason = "cache payload unreadable: " + exception.Message;
                }
            }
            return entries;
        }

        /// <summary>最近一次"拒绝缓存"的原因（`ai.asset.status` 用）。</summary>
        public string LastRejectReason { get; private set; }

        /// <summary>取某条缓存的字节（拿不到给 null）。</summary>
        public byte[] ReadPayload(AutoSaveEntry entry)
        {
            if (entry == null || !Available || string.IsNullOrEmpty(entry.PayloadPath))
                return null;
            try
            {
                return File.ReadAllBytes(Path.Combine(m_root, entry.PayloadPath));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- 恢复选择（R1）

        /// <summary>恢复时该用哪一份。</summary>
        public enum RecoveryChoice
        {
            /// <summary>没有缓存可用 → 读正式包。</summary>
            Disk,

            /// <summary>缓存比正式包新 → 用缓存（**但必须在界面上明说**）。</summary>
            Cache,

            /// <summary>正式包更新或与缓存同源 → 用正式包，并**盖掉**过期缓存。</summary>
            DiskNewer
        }

        /// <summary>
        /// **R1 的核心**：决定"用缓存还是用正式包"。绝不静默覆盖 —— 调用方必须把
        /// <see cref="RecoveryChoice"/> 与理由告诉用户（编辑器显示"将用缓存（新 X 分钟）"）。
        ///
        /// 判据顺序（**全部用记录在案的事实，不靠时间戳猜**；时间只做保守兜底）：
        ///   1. 没有缓存 → <see cref="RecoveryChoice.Disk"/>（没什么可恢复的）
        ///   2. **没有磁盘版**（包被删了 / 从来没保存过）→ <see cref="RecoveryChoice.Cache"/>
        ///      —— 缓存是**唯一副本**，这是断电恢复最值钱的一类
        ///   3. 缓存里的**内存版**与当前磁盘版**内容相同** → <see cref="RecoveryChoice.Disk"/>
        ///      （缓存没有额外价值：它记的就是盘上那一份）
        ///   4. 磁盘版在缓存写入之后**被人改过**（来源哈希对不上）→ <see cref="RecoveryChoice.DiskNewer"/>
        ///      （**人的改动优先**，过期缓存要被盖掉）
        ///   5. 时间上讲不通（缓存比磁盘还旧）→ <see cref="RecoveryChoice.Disk"/>（保守取磁盘）
        ///   6. 否则 → <see cref="RecoveryChoice.Cache"/>（磁盘没变，而缓存里有磁盘上没有的改动）
        ///
        /// ⚠️ **不能用"磁盘哈希 == 来源哈希"直接判 Disk**：来源哈希记的是*缓存写入那一刻的磁盘版*，
        /// 它等于当前磁盘哈希只说明"盘没动过"，**恰恰是缓存有价值的标准情形**
        /// （内存改完、还没保存、然后断电）。第一版就是这么写的，等于让缓存**永远用不上**；
        /// 现在改成比 `memoryHash`，因为那才是"缓存里装的是什么"。
        /// </summary>
        public static RecoveryChoice ChooseRecovery(AutoSaveEntry cached, string currentDiskHash,
            DateTime diskModifiedUtc)
        {
            if (cached == null)
                return RecoveryChoice.Disk;

            bool haveSource = !string.IsNullOrEmpty(cached.SourceHash);
            bool haveMemory = !string.IsNullOrEmpty(cached.MemoryHash);

            // 2. 没有磁盘版：缓存是唯一副本
            if (string.IsNullOrEmpty(currentDiskHash))
                return RecoveryChoice.Cache;

            // 3. 缓存里的内存版 == 当前磁盘版：没什么可恢复的
            if (haveMemory && string.Equals(cached.MemoryHash, currentDiskHash,
                StringComparison.OrdinalIgnoreCase))
            {
                return RecoveryChoice.Disk;
            }

            // 4. 正式包被外部改过（来源哈希与缓存记录的来源不符）→ 人的改动优先
            if (haveSource && !string.Equals(currentDiskHash, cached.SourceHash,
                StringComparison.OrdinalIgnoreCase))
            {
                return RecoveryChoice.DiskNewer;
            }

            // 5. 时间上讲不通（缓存比磁盘还旧）→ 保守取磁盘
            if (diskModifiedUtc != DateTime.MinValue && cached.SavedTimeUtc != DateTime.MinValue
                && cached.SavedTimeUtc < diskModifiedUtc)
            {
                return RecoveryChoice.Disk;
            }

            // 6. 磁盘没变、缓存里有磁盘上没有的改动
            return RecoveryChoice.Cache;
        }

        /// <summary>给界面/日志用的一句话解释（**必须能说清"为什么"**）。</summary>
        public static string Explain(RecoveryChoice choice, AutoSaveEntry cached, DateTime diskModifiedUtc)
        {
            switch (choice)
            {
                case RecoveryChoice.Cache:
                    if (diskModifiedUtc == DateTime.MinValue || cached == null)
                        return "using the autosave (there is no package on disk, so it is the only copy)";
                    double minutes = Math.Max(0.0, (cached.SavedTimeUtc - diskModifiedUtc).TotalMinutes);
                    return "using the autosave (it holds changes that were never saved, newer than the"
                        + " package on disk by " + minutes.ToString("0.#", CultureInfo.InvariantCulture)
                        + " min)";
                case RecoveryChoice.DiskNewer:
                    return "using the package on disk (it changed after the autosave was written)";
                default:
                    return "using the package on disk (no newer autosave)";
            }
        }

        // ---------------------------------------------------------------- 维护

        /// <summary>丢掉缓存（用户显式"丢弃改动"或恢复完成后调）。</summary>
        public bool TryDrop(out string error)
        {
            error = null;
            if (!Available)
                return true;
            try
            {
                if (System.IO.Directory.Exists(m_root))
                    System.IO.Directory.Delete(m_root, true);
                LastRejectReason = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        public Dictionary<string, object> Describe()
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            info["directory"] = m_root;
            info["available"] = Available;
            info["schemaVersion"] = SchemaVersion;
            info["lastRejectReason"] = LastRejectReason;
            List<AutoSaveEntry> entries = Available ? ReadEntries() : new List<AutoSaveEntry>();
            var names = new List<string>();
            for (int i = 0; i < entries.Count; i++)
                names.Add(entries[i].ToString());
            info["entries"] = names;
            return info;
        }

        private PackageValue ReadIndexValue()
        {
            if (!Available)
                return null;
            string path = Path.Combine(m_root, IndexFileName);
            if (!File.Exists(path))
                return null;
            try
            {
                var report = new PackageReport();
                PackageValue root;
                return PackageJson.TryParse(File.ReadAllText(path), path, report, out root) ? root : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>文件名消毒（只留安全字符；空名给 `entry`）。</summary>
        internal static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "entry";
            var text = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '_' || c == '.';
                text.Append(ok ? c : '_');
            }
            string result = text.ToString().Trim('.');
            return result.Length == 0 ? "entry" : result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// **内存库**（计划 §4.11）：资源先读进内存才算"加载"，之后在内存里随便改，
    /// **默认不回写磁盘**；要固化得显式 <see cref="Save"/>，要还原得显式 <see cref="Drop"/>。
    ///
    /// 为什么要有这一层（而不是"改完直接写文件"）：
    ///   · **运行态不该被磁盘抖动打断**：AI/Laya 每秒可能改好几次，落盘只能由人决定；
    ///   · **断电恢复要有锚点**：有"最近载入的磁盘版"才谈得上 discard（回到哪一份？）；
    ///   · **三态可显示**：磁盘版 / 内存版（dirty）/ 缓存版 —— 编辑器才能如实说明发生了什么。
    ///
    /// 这个类**不依赖游戏运行时**（只用 `PackageRoots` / `PackageLoader` / `PackageWriter` /
    /// `AutoSaveCache`），所以本地自检与编辑器都能编译、能跑。
    /// "把活的行为树变成字节"那一步由调用方（`PlayerAiRuntime` 走 `TreeWriter`）完成 ——
    /// 库只管字节，不碰 BtNode。
    /// </summary>
    public sealed class MemoryAssetStore : IAssetStore, IMemoryVersionSource
    {
        /// <summary>备份代次文件的后缀：`demo.greet.v2.bak`。</summary>
        public const string BackupExtension = ".bak";

        private readonly List<AssetRecord> m_records = new List<AssetRecord>();
        private readonly Dictionary<string, AssetRecord> m_byPath;

        public MemoryAssetStore(PackageRoots roots, string extension = PackageRoots.Extension)
        {
            Roots = roots;
            Extension = string.IsNullOrEmpty(extension) ? PackageRoots.Extension : extension;
            m_byPath = new Dictionary<string, AssetRecord>(PackageRoots.PathComparison
                == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        }

        public PackageRoots Roots { get; }

        public string Extension { get; }

        public string Kind
        {
            get { return "tree"; }
        }

        public IReadOnlyList<AssetRecord> Records
        {
            get { return m_records; }
        }

        /// <summary>内存库里有多少条是 dirty 的（`ai.asset.status` / 编辑器角标用）。</summary>
        public int DirtyCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < m_records.Count; i++)
                {
                    if (m_records[i].Dirty)
                        count++;
                }
                return count;
            }
        }

        public bool TryGet(string nameOrPath, out AssetRecord record)
        {
            record = null;
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return false;

            // 1) 先按"库里的键"找（路径或名字）
            AssetRecord byKey = FindByNameOrPath(nameOrPath);
            if (byKey != null)
            {
                record = byKey;
                return true;
            }

            // 2) 再按"磁盘上解析出来的路径"找
            string path = ResolveExisting(nameOrPath);
            if (path != null && m_byPath.TryGetValue(path, out record))
                return true;

            record = null;
            return false;
        }

        // ---------------------------------------------------------------- 载入

        /// <summary>
        /// **热重载要问的那个问题**（G25）：这个包在内存库里是什么版本、有没有未落盘的改动。
        /// 没登记过（库里没这个包）给 false —— 重载器据此退回"只比通知哈希与磁盘哈希"的老行为。
        /// </summary>
        public bool TryGetMemoryVersion(string pathOrName, out string memoryHash, out bool dirty)
        {
            memoryHash = null;
            dirty = false;

            AssetRecord record;
            if (!TryGet(pathOrName, out record) || record == null)
                return false;

            memoryHash = record.MemoryHash;
            dirty = record.Dirty;
            return true;
        }

        /// <summary>
        /// 磁盘 → 内存（clean）。这就是"**读进来才算加载**"的落点：
        /// 解析不到文件一律失败（缺文件是 `pkg_missing`，不是"空记录"）。
        /// </summary>
        public AssetRecord Load(string nameOrPath, out string error)
        {
            error = null;
            string path = ResolveExisting(nameOrPath);
            if (path == null)
            {
                error = PackageCodes.FileMissing + ": package not found: '" + (nameOrPath ?? "<null>")
                    + "' (roots: " + (Roots != null ? Roots.Describe() : "<none>") + ")";
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception exception)
            {
                error = PackageCodes.FileMissing + ": " + exception.GetType().Name + ": "
                    + exception.Message;
                return null;
            }

            if (bytes.Length == 0)
            {
                error = PackageCodes.JsonInvalid + ": refusing to load an empty file: " + path;
                return null;
            }

            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            AssetRecord record = FetchOrCreate(path);
            record.Name = NameOf(path);
            record.SourcePath = path;
            record.DiskBytes = bytes;
            record.Memory = bytes;
            record.DiskHash = PackageLoader.ComputeHash(bytes);
            record.MemoryHash = record.DiskHash;
            record.Dirty = false;
            record.Generation = 0;
            record.LoadedUtc = now;
            record.ChangedUtc = now;
            record.Origin = AssetOrigin.Disk;
            return record;
        }

        // ---------------------------------------------------------------- 内存改写

        /// <summary>
        /// 内存改写：把 <paramref name="memory"/> 装成当前运行态。哈希与磁盘版不同就置 dirty。
        ///
        /// **允许"盘上还没有"的包**（编辑器里先建在内存、之后才 save），
        /// 但目标路径必须落在白名单包目录里（路径穿越防线不因为"在内存里"而放宽）。
        /// </summary>
        public AssetRecord Adopt(string nameOrPath, byte[] memory, AssetOrigin origin, out string error)
        {
            error = null;
            if (memory == null || memory.Length == 0)
            {
                error = "an empty payload is not a package";
                return null;
            }

            AssetRecord record = FindByNameOrPath(nameOrPath);
            if (record == null)
            {
                string path = ResolveExisting(nameOrPath) ?? ResolveTarget(nameOrPath);
                if (path == null)
                {
                    error = "cannot place '" + (nameOrPath ?? "<null>")
                        + "' in the package folder (roots: "
                        + (Roots != null ? Roots.Describe() : "<none>") + ")";
                    return null;
                }

                // 盘上有就先读进来当 drop 的锚点；盘上没有就是"新建的内存包"
                if (File.Exists(path))
                {
                    string loadError;
                    record = Load(path, out loadError);
                    if (record == null)
                    {
                        error = loadError;
                        return null;
                    }
                }
                else
                {
                    record = FetchOrCreate(path);
                    record.Name = NameOf(path);
                    record.SourcePath = path;
                    record.DiskBytes = null;
                    record.DiskHash = null;
                    record.LoadedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    record.Origin = AssetOrigin.Unknown;
                }
            }

            string newHash = PackageLoader.ComputeHash(memory);
            if (record.Memory != null && string.Equals(newHash, record.MemoryHash,
                StringComparison.OrdinalIgnoreCase))
            {
                // **内容没变就不算一次改动**。
                //
                // 这条不是省事：活着的树会一直 `IsDirty`（直到人 save/drop 为止），
                // 自动缓存每 5 秒抓一次 —— 如果每次都推进代次、每次都重写缓存，
                // `.autosave/` 会以"每 5 秒一个文件"的速度无限长下去。
                // 用内容哈希而不是 `IsDirty` 判"真的变了没有"，才是唯一可靠的判据。
                return record;
            }

            record.Memory = (byte[])memory.Clone();
            record.MemoryHash = newHash;
            record.Dirty = !string.Equals(record.MemoryHash, record.DiskHash,
                StringComparison.OrdinalIgnoreCase);
            record.Generation++;
            record.ChangedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            record.Origin = origin;
            return record;
        }

        // ---------------------------------------------------------------- 手动保存

        /// <summary>
        /// 内存版 → 正式包。**绝不静默覆盖**：目标已存在且没给 `overwrite=true` 就失败；
        /// 给了 `overwrite=true` 也**先把上一代备份成 `name.vN.bak`**，备份失败就不动原文件。
        ///
        /// 约定俗成的落点是 `<实例根>/PlayerAi/Saves/`（见 <see cref="PackageRoots.SavesDirectoryFor"/>），
        /// 但任何**实例根之内**的目录都可以（编辑器可以另存到子目录）。
        /// **`.autosave/` 永远不是合法目标**（R4：缓存不是正式包）。
        /// </summary>
        public bool Save(string nameOrPath, string directory, bool overwrite, out string path,
            out string error)
        {
            path = null;
            error = null;

            AssetRecord record;
            if (!TryGet(nameOrPath, out record) || record == null)
            {
                error = "not in the memory store: '" + (nameOrPath ?? "<null>") + "' (load it first)";
                return false;
            }
            if (record.Memory == null || record.Memory.Length == 0)
            {
                error = "the memory version is empty; nothing to save";
                return false;
            }

            string target = ValidateTargetDirectory(directory, out error);
            if (target == null)
                return false;

            string safeName = AiRecordingSession.SanitizeName(record.Name);
            if (string.IsNullOrEmpty(safeName))
            {
                error = "a file name is required (letters/digits/_/-/., max 64)";
                return false;
            }

            path = System.IO.Path.Combine(target, safeName + Extension);

            bool exists = File.Exists(path);
            if (exists && !overwrite)
            {
                error = "already_exists: '" + System.IO.Path.GetFileName(path)
                    + "' already exists; pass overwrite=true to replace it (a backup is kept)";
                path = null;
                return false;
            }

            if (exists)
            {
                byte[] previous;
                try
                {
                    previous = File.ReadAllBytes(path);
                }
                catch (Exception exception)
                {
                    error = "cannot read the existing package for backup: "
                        + exception.GetType().Name + ": " + exception.Message;
                    path = null;
                    return false;
                }

                string backup = NextBackupPath(target, safeName);
                string backupError;
                if (!PackageWriter.TryWriteFile(backup, previous, out backupError))
                {
                    error = "refusing to overwrite: the backup failed (" + backupError + ")";
                    path = null;
                    return false;
                }
            }

            string writeError;
            if (!PackageWriter.TryWriteFile(path, record.Memory, out writeError))
            {
                error = "atomic write failed: " + writeError;
                path = null;
                return false;
            }

            // 落盘成功 → 内存版**成为**新的磁盘版（dirty 清掉，drop 的新锚点就是它）
            //
            // ⚠️ **不动 `SourcePath`**：它记的是"这份内存是从哪个包来的"（缓存版本头里的「来源路径」）。
            //    `save` 允许另存到别的目录（Saves/、子目录），如果顺手把 SourcePath 改过去，
            //    缓存里记的来源就变成了"上次另存的地方"，排查时再也回不到真正的来源包。
            record.DiskBytes = (byte[])record.Memory.Clone();
            record.DiskHash = record.MemoryHash;
            record.Dirty = false;
            record.LoadedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            record.Origin = AssetOrigin.Disk;
            return true;
        }

        // ---------------------------------------------------------------- 另存为新包

        /// <summary>
        /// 把**当前内存版**另存成**新名字**的包（G25 冲突的第 3 个选项："把内存另存为新包"）。
        ///
        /// 与 <see cref="Save"/> 的两点区别，都是刻意的：
        ///   · 写的是 <paramref name="newName"/>，**不动记录的身份**（记录还叫原来的名字）；
        ///   · **不清 `Dirty`** —— 另存是一份副本，内存版相对它自己的来源包仍然没保存。
        ///     想"这份改动就算落定了"要用 <see cref="Save"/>（它会更新 drop 锚点）。
        /// </summary>
        public bool SaveAs(string nameOrPath, string newName, string directory, bool overwrite,
            out string path, out string error)
        {
            path = null;
            error = null;

            AssetRecord record;
            if (!TryGet(nameOrPath, out record) || record == null)
            {
                error = "not in the memory store: '" + (nameOrPath ?? "<null>") + "' (load it first)";
                return false;
            }
            if (record.Memory == null || record.Memory.Length == 0)
            {
                error = "the memory version is empty; nothing to save";
                return false;
            }

            string safeName = AiRecordingSession.SanitizeName(newName);
            if (string.IsNullOrEmpty(safeName))
            {
                error = "a file name is required (letters/digits/_/-/., max 64)";
                return false;
            }

            string target = ValidateTargetDirectory(directory, out error);
            if (target == null)
                return false;

            path = System.IO.Path.Combine(target, safeName + Extension);
            if (File.Exists(path) && !overwrite)
            {
                error = "already_exists: '" + System.IO.Path.GetFileName(path)
                    + "' already exists; pass overwrite=true to replace it (a backup is kept)";
                path = null;
                return false;
            }

            if (File.Exists(path))
            {
                byte[] previous;
                try
                {
                    previous = File.ReadAllBytes(path);
                }
                catch (Exception exception)
                {
                    error = "cannot read the existing package for backup: "
                        + exception.GetType().Name + ": " + exception.Message;
                    path = null;
                    return false;
                }
                string backupError;
                if (!PackageWriter.TryWriteFile(NextBackupPath(target, safeName), previous,
                    out backupError))
                {
                    error = "refusing to overwrite: the backup failed (" + backupError + ")";
                    path = null;
                    return false;
                }
            }

            string writeError;
            if (!PackageWriter.TryWriteFile(path, record.Memory, out writeError))
            {
                error = "atomic write failed: " + writeError;
                path = null;
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 丢弃

        /// <summary>
        /// 丢弃内存改动 → 回到最近一次载入的磁盘版。
        /// 从来没有磁盘版（纯内存新建）时**如实拒绝**，不假装成功 —— 那不是"丢弃"，那是"删掉"。
        /// </summary>
        public bool Drop(string nameOrPath, out string error)
        {
            error = null;
            AssetRecord record;
            if (!TryGet(nameOrPath, out record) || record == null)
            {
                error = "not in the memory store: '" + (nameOrPath ?? "<null>") + "'";
                return false;
            }
            if (record.DiskBytes == null || record.DiskBytes.Length == 0)
            {
                error = "nothing_to_drop: this record has never been loaded from disk"
                    + " (save it instead of dropping it)";
                return false;
            }

            record.Memory = (byte[])record.DiskBytes.Clone();
            record.MemoryHash = record.DiskHash;
            record.Dirty = false;
            record.Generation++;
            record.ChangedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            record.Origin = AssetOrigin.Disk;
            return true;
        }

        // ---------------------------------------------------------------- 自动缓存

        /// <summary>当前内存版 → 自动缓存（影子副本，R1~R4 由 <see cref="AutoSaveCache"/> 保证）。</summary>
        public bool WriteCache(string nameOrPath, AutoSaveCache cache, out string error)
        {
            error = null;
            AssetRecord record;
            if (!TryGet(nameOrPath, out record) || record == null)
            {
                error = "not in the memory store: '" + (nameOrPath ?? "<null>") + "'";
                return false;
            }
            if (cache == null || !cache.Available)
            {
                error = "no autosave directory (instance root is unknown)";
                return false;
            }
            if (record.Memory == null || record.Memory.Length == 0)
            {
                error = "the memory version is empty; nothing to cache";
                return false;
            }

            AutoSaveResult result = cache.Save(record.Name, record.Memory, record.SourcePath,
                record.DiskHash, record.MemoryHash, record.Dirty);
            if (!result.Ok)
            {
                error = result.Error;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 自动缓存 → 内存（断电恢复）。同时尽量把**当前磁盘版**读进来当 drop 的锚点：
        /// 缓存只负责"内存是什么"，"回到哪一份"必须问磁盘。
        /// </summary>
        public bool ReadCache(string nameOrPath, AutoSaveCache cache, out string error)
        {
            error = null;
            if (cache == null || !cache.Available)
            {
                error = "no autosave directory (instance root is unknown)";
                return false;
            }

            List<AutoSaveEntry> entries = cache.ReadEntries();
            AutoSaveEntry entry = null;
            string wanted = NameOf(nameOrPath);
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Name, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entries[i].Name, nameOrPath, StringComparison.OrdinalIgnoreCase))
                {
                    entry = entries[i];
                    break;
                }
            }
            if (entry == null)
            {
                error = "no cached version of '" + (nameOrPath ?? "<null>") + "'"
                    + (string.IsNullOrEmpty(cache.LastRejectReason)
                        ? string.Empty : " (" + cache.LastRejectReason + ")");
                return false;
            }

            byte[] payload = cache.ReadPayload(entry);
            if (payload == null || payload.Length == 0)
            {
                error = "the cached payload could not be read back: " + entry.PayloadPath;
                return false;
            }

            string adoptError;
            AssetRecord record = Adopt(entry.Name, payload, AssetOrigin.Cache, out adoptError);
            if (record == null)
            {
                // 包目录不可用时退回"只按路径建记录"（恢复流程不该因为路径解析失败而丢数据）
                string path = ResolveTarget(entry.Name);
                if (path == null)
                {
                    error = adoptError;
                    return false;
                }
                record = FetchOrCreate(path);
                record.Name = entry.Name;
                record.SourcePath = string.IsNullOrEmpty(entry.SourcePath) ? path : entry.SourcePath;
                record.Memory = (byte[])payload.Clone();
                record.MemoryHash = PackageLoader.ComputeHash(record.Memory);
                record.Origin = AssetOrigin.Cache;
                record.Generation++;
                record.LoadedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                record.ChangedUtc = record.LoadedUtc;
            }

            // drop 的锚点 = 当前磁盘版（不是"缓存写入时的磁盘版"）
            if (record.DiskBytes == null && !string.IsNullOrEmpty(record.SourcePath)
                && File.Exists(record.SourcePath))
            {
                try
                {
                    byte[] disk = File.ReadAllBytes(record.SourcePath);
                    record.DiskBytes = disk;
                    record.DiskHash = PackageLoader.ComputeHash(disk);
                }
                catch (Exception)
                {
                    // 读不到就保持"没有锚点"，drop 会如实拒绝
                }
            }

            record.Dirty = !string.Equals(record.MemoryHash, record.DiskHash,
                StringComparison.OrdinalIgnoreCase);
            return true;
        }

        // ---------------------------------------------------------------- 描述

        public List<Dictionary<string, object>> DescribeRecords()
        {
            var list = new List<Dictionary<string, object>>();
            for (int i = 0; i < m_records.Count; i++)
            {
                AssetRecord record = m_records[i];
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = record.Name,
                    ["file"] = record.SourcePath != null
                        ? System.IO.Path.GetFileName(record.SourcePath) : null,
                    ["sourcePath"] = record.SourcePath,
                    ["dirty"] = record.Dirty,
                    ["generation"] = record.Generation,
                    ["origin"] = record.Origin.ToString(),
                    ["memoryHash"] = AssetRecord.Short(record.MemoryHash),
                    ["diskHash"] = AssetRecord.Short(record.DiskHash),
                    ["memoryBytes"] = record.MemoryByteCount,
                    ["diskBytes"] = record.DiskByteCount,
                    ["hasDiskVersion"] = record.DiskBytes != null,
                    ["loadedUtc"] = record.LoadedUtc,
                    ["changedUtc"] = record.ChangedUtc
                });
            }
            return list;
        }

        public string Describe()
        {
            return "MemoryAssetStore(" + Kind + " records=" + m_records.Count
                + " dirty=" + DirtyCount + " ext=" + Extension + ")";
        }

        public override string ToString()
        {
            return Describe();
        }

        // ---------------------------------------------------------------- 内部

        private AssetRecord FindByNameOrPath(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return null;

            string normalized = PackageRoots.NormalizePath(nameOrPath);
            for (int i = 0; i < m_records.Count; i++)
            {
                AssetRecord record = m_records[i];
                if (normalized != null && record.SourcePath != null
                    && string.Equals(record.SourcePath, normalized, PackageRoots.PathComparison))
                {
                    return record;
                }
                if (string.Equals(record.Name, nameOrPath, StringComparison.OrdinalIgnoreCase))
                    return record;
            }
            return null;
        }

        private AssetRecord FetchOrCreate(string path)
        {
            AssetRecord record;
            if (m_byPath.TryGetValue(path, out record))
                return record;

            record = new AssetRecord { SourcePath = path, Name = NameOf(path) };
            m_byPath[path] = record;
            m_records.Add(record);
            return record;
        }

        /// <summary>解析一个**磁盘上确实存在**（或在库里已有记录）的包路径。</summary>
        private string ResolveExisting(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return null;

            Func<string, bool> exists = delegate(string candidate)
            {
                return File.Exists(candidate) || m_byPath.ContainsKey(candidate);
            };

            if (Roots != null)
                return Roots.Resolve(nameOrPath, exists, Extension);

            // 没有包目录（自检里的极端情形）：只接受真实存在的绝对路径
            string absolute = PackageRoots.NormalizePath(nameOrPath);
            return absolute != null && exists(absolute) ? absolute : null;
        }

        /// <summary>
        /// 解析"盘上还没有、但允许在内存里新建"的目标路径。
        /// **必须落在白名单包目录内** —— 内存库不放宽路径穿越防线。
        /// </summary>
        private string ResolveTarget(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return null;

            string safeName = AiRecordingSession.SanitizeName(NameOf(nameOrPath));
            if (string.IsNullOrEmpty(safeName))
                return null;

            string fileName = safeName + Extension;

            if (Roots != null && Roots.InstanceRoot != null)
            {
                string combined = PackageRoots.NormalizePath(
                    System.IO.Path.Combine(Roots.InstanceRoot.Path, fileName));
                return combined != null && Roots.IsAllowed(combined) ? combined : null;
            }

            string absolute = PackageRoots.NormalizePath(nameOrPath);
            if (absolute == null)
                return null;
            return absolute.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                ? absolute : absolute + Extension;
        }

        /// <summary>
        /// 保存目标目录的合法性：必须是绝对路径、落在**实例根之内**、且不是 `.autosave/`。
        /// 这三条分别挡住"写到任意位置"、"写到包目录之外"、"把缓存当正式包"。
        /// </summary>
        private string ValidateTargetDirectory(string directory, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "a target directory is required";
                return null;
            }

            string target = PackageRoots.NormalizePath(directory);
            if (target == null)
            {
                error = "not a valid directory: " + directory;
                return null;
            }

            if (AutoSaveCache.IsPackageSource(target))
            {
                error = "refusing to save into '" + AutoSaveCache.FolderName
                    + "': the autosave cache is not a package folder (R4)";
                return null;
            }

            string instanceRoot = InstanceRootOf();
            if (instanceRoot == null)
            {
                error = "no instance root (the package folder is not configured)";
                return null;
            }

            if (!IsUnder(target, instanceRoot))
            {
                error = "refusing to write outside the instance root: " + target;
                return null;
            }
            return target;
        }

        private string InstanceRootOf()
        {
            if (Roots == null || Roots.InstanceRoot == null)
                return null;
            string packageDirectory = Roots.InstanceRoot.Path;
            if (string.IsNullOrEmpty(packageDirectory))
                return null;
            try
            {
                string playerAi = System.IO.Path.GetDirectoryName(packageDirectory);
                if (string.IsNullOrEmpty(playerAi))
                    return null;
                string root = System.IO.Path.GetDirectoryName(playerAi);
                return !string.IsNullOrEmpty(root) ? root : playerAi;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
                return false;
            string normalizedRoot = PackageRoots.NormalizePath(root);
            if (normalizedRoot == null)
                return false;
            if (string.Equals(path, normalizedRoot, PackageRoots.PathComparison))
                return true;
            if (path.Length <= normalizedRoot.Length)
                return false;
            return path.StartsWith(normalizedRoot, PackageRoots.PathComparison)
                && (path[normalizedRoot.Length] == System.IO.Path.DirectorySeparatorChar
                    || path[normalizedRoot.Length] == System.IO.Path.AltDirectorySeparatorChar);
        }

        /// <summary>下一个可用的备份代次：`name.v1.bak`、`name.v2.bak`…（找第一个空位）。</summary>
        internal static string NextBackupPath(string directory, string safeName)
        {
            for (int generation = 1; generation < 1000; generation++)
            {
                string candidate = System.IO.Path.Combine(directory, safeName + ".v"
                    + generation.ToString(CultureInfo.InvariantCulture) + BackupExtension);
                if (!File.Exists(candidate))
                    return candidate;
            }
            return System.IO.Path.Combine(directory, safeName + ".v999" + BackupExtension);
        }

        /// <summary>
        /// 从名字或路径里取逻辑名（不含扩展名）。
        ///
        /// ⚠️ **自己按两种分隔符切，不用 `Path.GetFileName`**：在 Unix/Android 上 `\` 是普通字符，
        /// `Path.GetFileName(@"C:\game\…\demo.greet.scbtpak")` 会**原样返回整串** —— 而缓存/记录里
        /// 存着别处（Windows）写下的来源路径是常态，于是包名会变成一整条路径（实机实测）。
        /// </summary>
        internal static string NameOf(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return null;

            string text = nameOrPath.Trim().TrimEnd('/', '\\');
            int slash = text.LastIndexOfAny(new[] { '/', '\\' });
            string name = slash >= 0 ? text.Substring(slash + 1) : text;

            if (name.EndsWith(PackageRoots.Extension, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(PackageRoots.ActionExtension, StringComparison.OrdinalIgnoreCase))
            {
                int dot = name.LastIndexOf('.');
                if (dot > 0)
                    name = name.Substring(0, dot);
            }
            return name;
        }
    }
}

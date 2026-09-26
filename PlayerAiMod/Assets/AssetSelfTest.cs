using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 资源库自检（P6 第二步）：**纯逻辑 + 真磁盘临时目录**（不依赖游戏运行时）。
    ///
    /// 钉住的是 plan §4.11 里那几条"猜错就毁掉用户成果"的语义：
    ///   · **读进来才算加载**（解析不到文件一律失败，不做空记录）；
    ///   · **默认不回写磁盘**（`save` 不覆盖来源包；`overwrite=true` 也先备份上一代）；
    ///   · **drop 回的是最近载入的磁盘版**（没有磁盘版时如实拒绝，不假装成功）；
    ///   · **R1**：恢复决议按"哈希 + 时间戳"判，并**必须给出理由**；
    ///   · **R4**：`.autosave/` 永远不是包来源，也不能当保存目标；
    ///   · **只有缓存、盘上没有**的包必须出现在恢复计划里（那才是断电恢复最值钱的一类）。
    /// </summary>
    public static class AssetSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "AssetSelfTest" };
            try { CaseLoadAdoptDrop(result); } catch (Exception e) { result.Check("case:load/adopt/drop", false, e.Message); }
            try { CaseSaveNeverOverwrites(result); } catch (Exception e) { result.Check("case:save never overwrites", false, e.Message); }
            try { CaseSaveTargetGuards(result); } catch (Exception e) { result.Check("case:save target guards", false, e.Message); }
            try { CaseCacheRoundTrip(result); } catch (Exception e) { result.Check("case:cache round-trip", false, e.Message); }
            try { CaseRecoveryDecide(result); } catch (Exception e) { result.Check("case:recovery decide", false, e.Message); }
            try { CaseCacheOnlyPackageIsPlanned(result); } catch (Exception e) { result.Check("case:cache-only package", false, e.Message); }
            try { CaseNameAndPathHygiene(result); } catch (Exception e) { result.Check("case:name/path hygiene", false, e.Message); }
            return result;
        }

        // ---------------------------------------------------------------- 夹具

        private static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "pai-asset-selftest-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            return root;
        }

        private static void Cleanup(string root)
        {
            try
            {
                if (System.IO.Directory.Exists(root))
                    System.IO.Directory.Delete(root, true);
            }
            catch (Exception)
            {
            }
        }

        private static byte[] Payload(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static string WritePackage(string root, string name, string content)
        {
            string directory = PackageRoots.DirectoryFor(root);
            System.IO.Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, Payload(content));
            return path;
        }

        private static MemoryAssetStore NewStore(string root)
        {
            return new MemoryAssetStore(new PackageRoots(PackageRoots.DirectoryFor(root)));
        }

        // ---------------------------------------------------------------- 用例

        private static void CaseLoadAdoptDrop(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                string path = WritePackage(root, "demo.greet.scbtpak", "v1");
                MemoryAssetStore store = NewStore(root);

                string error;
                AssetRecord record = store.Load("demo.greet", out error);
                result.Check("load: a package on disk is loaded into memory", record != null, error);
                if (record == null)
                    return;

                result.Check("load: a freshly loaded record is clean",
                    !record.Dirty, "dirty=" + record.Dirty);
                result.Check("load: the drop anchor is the disk bytes",
                    Same(record.DiskBytes, Payload("v1")) && Same(record.Memory, Payload("v1")),
                    "disk=" + record.DiskByteCount + " mem=" + record.MemoryByteCount);
                result.Check("load: disk and memory hashes agree",
                    string.Equals(record.DiskHash, record.MemoryHash, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(record.MemoryHash), record.MemoryHash);
                result.Check("load: the origin is Disk", record.Origin == AssetOrigin.Disk,
                    record.Origin.ToString());
                result.Check("load: generation starts at 0", record.Generation == 0,
                    "gen=" + record.Generation);
                result.Check("load: the logical name drops the extension",
                    record.Name == "demo.greet", record.Name);

                result.Check("load: a missing package fails instead of creating an empty record",
                    store.Load("no.such.package", out error) == null,
                    "returned a record for a missing package");
                result.Check("load: the missing-package error names the code",
                    !string.IsNullOrEmpty(error) && error.Contains(PackageCodes.FileMissing), error);

                // ---- 内存改写
                AssetRecord adopted = store.Adopt("demo.greet", Payload("v2"), AssetOrigin.Ai, out error);
                result.Check("adopt: the memory version is replaced", adopted != null, error);
                result.Check("adopt: memory now holds the new bytes", Same(record.Memory, Payload("v2")),
                    record.MemoryByteCount + " bytes");
                result.Check("adopt: it becomes dirty", record.Dirty, "dirty=false");
                result.Check("adopt: the hash changes", record.MemoryHash != record.DiskHash,
                    record.MemoryHash);
                result.Check("adopt: the generation advances", record.Generation == 1,
                    "gen=" + record.Generation);
                result.Check("adopt: the origin is recorded", record.Origin == AssetOrigin.Ai,
                    record.Origin.ToString());
                result.Check("adopt: **the file on disk is untouched**",
                    Same(File.ReadAllBytes(path), Payload("v1")),
                    "the source package changed behind our back");
                result.Check("adopt: the drop anchor still points at the disk version",
                    Same(record.DiskBytes, Payload("v1")), record.DiskByteCount + " bytes");

                result.Check("adopt: an empty payload is refused",
                    store.Adopt("demo.greet", new byte[0], AssetOrigin.Ai, out error) == null,
                    "accepted an empty payload");

                byte[] scratch = Payload("v3");
                store.Adopt("demo.greet", scratch, AssetOrigin.Ai, out error);
                scratch[0] = (byte)'X';
                result.Check("adopt: mutating the caller's array does not change the store",
                    Same(record.Memory, Payload("v3")), "the store aliased the caller's buffer");

                // 内容没变就不算改动（否则"活树一直是脏的"会把代次与缓存刷爆）
                long generation = record.Generation;
                store.Adopt("demo.greet", Payload("v3"), AssetOrigin.Ai, out error);
                result.Check("adopt: re-adopting identical bytes is a no-op for the generation",
                    record.Generation == generation, "gen=" + record.Generation
                    + " (was " + generation + ")");
                result.Check("adopt: re-adopting identical bytes keeps the memory version",
                    Same(record.Memory, Payload("v3")));

                // ---- 丢弃
                result.Check("drop: succeeds when a disk version was loaded",
                    store.Drop("demo.greet", out error), error);
                result.Check("drop: memory goes back to the disk bytes",
                    Same(record.Memory, Payload("v1")), record.MemoryByteCount + " bytes");
                result.Check("drop: the record becomes clean again", !record.Dirty, "dirty=true");
                result.Check("drop: the memory hash matches the disk hash",
                    string.Equals(record.MemoryHash, record.DiskHash, StringComparison.Ordinal),
                    record.MemoryHash);
                result.Check("drop: the generation advances (it was a change too)",
                    record.Generation == 3, "gen=" + record.Generation);

                // ---- 纯内存新建：没有磁盘版就该如实拒绝 drop
                AssetRecord created = store.Adopt("brand.new", Payload("m1"), AssetOrigin.Editor, out error);
                result.Check("adopt: a package that is not on disk can be created in memory",
                    created != null, error);
                result.Check("adopt: a memory-only record is dirty",
                    created != null && created.Dirty, "dirty=false");
                result.Check("drop: refuses when there is no disk version to go back to",
                    !store.Drop("brand.new", out error)
                    && error != null && error.Contains("nothing_to_drop"), error);
                result.Check("drop: the memory-only content survives the refused drop",
                    created != null && Same(created.Memory, Payload("m1")));

                result.Check("drop: refuses for a package that was never loaded",
                    !store.Drop("ghost.package", out error), "dropped a package that is not in the store");

                // ---- 查询
                AssetRecord found;
                result.Check("tryGet: finds a record by name", store.TryGet("demo.greet", out found)
                    && ReferenceEquals(found, record));
                result.Check("tryGet: finds a record by absolute path",
                    store.TryGet(path, out found) && ReferenceEquals(found, record));
                result.Check("tryGet: refuses a package that is not there",
                    !store.TryGet("ghost.package", out found) && found == null);
                result.Check("describe: reports records and the dirty count",
                    store.Records.Count == 2 && store.DirtyCount == 1,
                    "records=" + store.Records.Count + " dirty=" + store.DirtyCount);
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseSaveNeverOverwrites(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                string packageDir = PackageRoots.DirectoryFor(root);
                string path = WritePackage(root, "demo.greet.scbtpak", "v1");
                MemoryAssetStore store = NewStore(root);

                string error;
                store.Load("demo.greet", out error);
                store.Adopt("demo.greet", Payload("v2"), AssetOrigin.Ai, out error);

                string saved;
                result.Check("save: **the source package is not touched without overwrite=true**",
                    !store.Save("demo.greet", packageDir, false, out saved, out error),
                    "the save silently succeeded");
                result.Check("save: the refusal says already_exists",
                    error != null && error.Contains("already_exists"), error);
                result.Check("save: the file on disk is still the original",
                    Same(File.ReadAllBytes(path), Payload("v1")), "the source package was clobbered");

                result.Check("save: with overwrite=true it succeeds",
                    store.Save("demo.greet", packageDir, true, out saved, out error), error);
                result.Check("save: the file now holds the memory version",
                    Same(File.ReadAllBytes(path), Payload("v2")), "the file was not written");
                result.Check("save: it went to the expected path",
                    string.Equals(saved, path, PackageRoots.PathComparison), saved + " vs " + path);

                string backup = Path.Combine(packageDir, "demo.greet.v1" + MemoryAssetStore.BackupExtension);
                result.Check("save: **the previous generation was backed up**", File.Exists(backup), backup);
                result.Check("save: the backup holds exactly the previous bytes",
                    File.Exists(backup) && Same(File.ReadAllBytes(backup), Payload("v1")),
                    "the backup is not the previous generation");

                AssetRecord record;
                store.TryGet("demo.greet", out record);
                result.Check("save: the record becomes clean", record != null && !record.Dirty,
                    "still dirty after a successful save");
                result.Check("save: the memory version becomes the new drop anchor",
                    record != null && Same(record.DiskBytes, Payload("v2")));
                result.Check("save: the same name saved twice in a row is a no-op for the source path",
                    record != null && string.Equals(record.SourcePath, path, PackageRoots.PathComparison),
                    record != null ? record.SourcePath : "<null>");

                // 再改一次、再覆盖一次 → 第二代的备份
                store.Adopt("demo.greet", Payload("v3"), AssetOrigin.Editor, out error);
                result.Check("save: a second overwrite succeeds",
                    store.Save("demo.greet", packageDir, true, out saved, out error), error);
                string backup2 = Path.Combine(packageDir, "demo.greet.v2" + MemoryAssetStore.BackupExtension);
                result.Check("save: backups keep generations (v1 and v2 both exist)",
                    File.Exists(backup) && File.Exists(backup2), backup2);
                result.Check("save: the v2 backup holds the v2 bytes",
                    File.Exists(backup2) && Same(File.ReadAllBytes(backup2), Payload("v2")));

                // 另存到 Saves/（默认落点）：不覆盖、新建
                string saves = PackageRoots.SavesDirectoryFor(root);
                result.Check("save: the default manual-save folder is <instance>/PlayerAi/Saves",
                    saves != null && saves.EndsWith(PackageRoots.SavesFolder), saves);
                result.Check("save: saving into Saves/ works without overwrite",
                    store.Save("demo.greet", saves, false, out saved, out error), error);
                result.Check("save: the copy in Saves/ holds the memory version",
                    Same(File.ReadAllBytes(saved), Payload("v3")), saved);
                result.Check("save: **the source path is preserved across save-as**",
                    record != null && string.Equals(record.SourcePath, path, PackageRoots.PathComparison),
                    record != null ? record.SourcePath : "<null>");

                result.Check("save: an unknown package is refused",
                    !store.Save("ghost.package", packageDir, false, out saved, out error)
                    && error != null && error.Contains("not in the memory store"), error);
                result.Check("save: an empty target directory is refused",
                    !store.Save("demo.greet", null, false, out saved, out error), "accepted no directory");
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseSaveTargetGuards(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                WritePackage(root, "demo.greet.scbtpak", "v1");
                MemoryAssetStore store = NewStore(root);

                string error;
                store.Load("demo.greet", out error);
                store.Adopt("demo.greet", Payload("v2"), AssetOrigin.Ai, out error);

                string saved;
                string outside = Path.Combine(Path.GetTempPath(),
                    "pai-asset-outside-" + Guid.NewGuid().ToString("N"));
                result.Check("guards: writing outside the instance root is refused",
                    !store.Save("demo.greet", outside, false, out saved, out error)
                    && error != null && error.Contains("outside the instance root"), error);
                result.Check("guards: nothing was created outside the instance root",
                    !System.IO.Directory.Exists(outside), outside);

                string autosave = AutoSaveCache.ResolveDirectory(root);
                result.Check("guards: `.autosave/` is refused as a save target (R4)",
                    !store.Save("demo.greet", autosave, false, out saved, out error)
                    && error != null && error.Contains(AutoSaveCache.FolderName), error);
                result.Check("guards: `.autosave/` is not a package source (R4)",
                    AutoSaveCache.IsPackageSource(autosave)
                    && !AutoSaveCache.IsPackageSource(PackageRoots.DirectoryFor(root)),
                    autosave);

                PackageRoots roots = new PackageRoots(PackageRoots.DirectoryFor(root));
                result.Check("guards: the package roots cannot resolve anything inside `.autosave/`",
                    roots.Resolve(Path.Combine(autosave, "demo.greet.pkg")) == null,
                    "the autosave folder leaked into the package roots");
                result.Check("guards: `Saves/` is outside the package roots too",
                    roots.Resolve(Path.Combine(PackageRoots.SavesDirectoryFor(root),
                        "demo.greet.scbtpak")) == null,
                    "Saves/ leaked into the package roots");
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseCacheRoundTrip(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                string path = WritePackage(root, "demo.greet.scbtpak", "v1");
                MemoryAssetStore store = NewStore(root);

                string error;
                store.Load("demo.greet", out error);
                store.Adopt("demo.greet", Payload("v2"), AssetOrigin.Ai, out error);

                var cache = new AutoSaveCache(root);
                result.Check("cache: the memory version can be written to `.autosave/`",
                    store.WriteCache("demo.greet", cache, out error), error);

                List<AutoSaveEntry> entries = cache.ReadEntries();
                result.Check("cache: one entry is visible in the index", entries.Count == 1,
                    "entries=" + entries.Count);
                AutoSaveEntry entry = entries.Count > 0 ? entries[0] : null;
                result.Check("cache: the entry records the dirty flag",
                    entry != null && entry.Dirty, entry != null ? entry.Dirty.ToString() : "<none>");
                result.Check("cache: the entry records the disk version it came from",
                    entry != null && !string.IsNullOrEmpty(entry.SourceHash), "no sourceHash");
                result.Check("cache: the entry records the in-memory version",
                    entry != null && !string.IsNullOrEmpty(entry.MemoryHash), "no memoryHash");

                // 模拟重启：全新的库，只能从缓存恢复
                MemoryAssetStore restarted = NewStore(root);
                result.Check("cache: a fresh store can restore from the autosave",
                    restarted.ReadCache("demo.greet", cache, out error), error);

                AssetRecord restored;
                restarted.TryGet("demo.greet", out restored);
                result.Check("cache: the restored memory version is the cached one",
                    restored != null && Same(restored.Memory, Payload("v2")),
                    restored != null ? restored.MemoryByteCount + " bytes" : "<none>");
                result.Check("cache: the restore is marked as coming from the cache",
                    restored != null && restored.Origin == AssetOrigin.Cache,
                    restored != null ? restored.Origin.ToString() : "<none>");
                result.Check("cache: **the restored record is dirty (it differs from disk)**",
                    restored != null && restored.Dirty, "dirty=false");
                result.Check("cache: the drop anchor is the *current* disk version",
                    restored != null && Same(restored.DiskBytes, Payload("v1")),
                    restored != null ? restored.DiskByteCount + " bytes" : "<none>");

                result.Check("cache: restoring a package the cache never saw fails",
                    !restarted.ReadCache("ghost.package", cache, out error), "restored a ghost");

                // 恢复之后仍然可以丢弃（回磁盘版）
                result.Check("cache: drop works after a restore", restarted.Drop("demo.greet", out error),
                    error);
                result.Check("cache: after the drop the memory version is the disk one again",
                    restored != null && Same(restored.Memory, Payload("v1")));
                result.Check("cache: the disk file was never touched by cache operations",
                    Same(File.ReadAllBytes(path), Payload("v1")), "the source package moved");
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseRecoveryDecide(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                string path = WritePackage(root, "demo.greet.scbtpak", "v1");
                byte[] disk = Payload("v1");
                string diskHash = PackageLoader.ComputeHash(disk);
                DateTime older = new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc);
                DateTime newer = older.AddMinutes(5);

                var cachedSame = new AutoSaveEntry
                {
                    Name = "demo.greet",
                    SourcePath = path,
                    SourceHash = diskHash,
                    MemoryHash = PackageLoader.ComputeHash(Payload("v2")),
                    SavedUtc = newer.ToString("o"),
                    Dirty = true,
                    PayloadPath = "demo.greet.1.pkg"
                };
                var cachedClean = new AutoSaveEntry
                {
                    Name = "demo.greet",
                    SourcePath = path,
                    SourceHash = diskHash,
                    MemoryHash = diskHash,
                    SavedUtc = newer.ToString("o"),
                    Dirty = false,
                    PayloadPath = "demo.greet.1b.pkg"
                };

                AssetRecoveryPlan plan = AssetRecovery.Decide("demo.greet", path, disk, older,
                    cachedSame, Payload("v2"));
                result.Check("decide: a newer autosave wins",
                    plan.Choice == AutoSaveCache.RecoveryChoice.Cache && plan.FromCache
                    && Same(plan.Payload, Payload("v2")), plan.Describe());
                result.Check("decide: the cache decision comes with an explanation",
                    !string.IsNullOrEmpty(plan.Explanation) && plan.Explanation.Contains("never saved"),
                    plan.Explanation);

                // 磁盘版在缓存之后被人改过 → 人的改动优先
                var cachedStale = new AutoSaveEntry
                {
                    Name = "demo.greet",
                    SourcePath = path,
                    SourceHash = PackageLoader.ComputeHash(Payload("some other version")),
                    MemoryHash = PackageLoader.ComputeHash(Payload("v2")),
                    SavedUtc = newer.ToString("o"),
                    Dirty = true,
                    PayloadPath = "demo.greet.2.pkg"
                };
                plan = AssetRecovery.Decide("demo.greet", path, disk, newer.AddMinutes(1),
                    cachedStale, Payload("v2"));
                result.Check("decide: **a package changed after the cache wins over the cache**",
                    plan.Choice == AutoSaveCache.RecoveryChoice.DiskNewer && !plan.FromCache
                    && Same(plan.Payload, disk), plan.Describe());
                result.Check("decide: the disk-newer explanation mentions the change",
                    plan.Explanation.Contains("changed"), plan.Explanation);

                // 缓存里的内存版 == 磁盘版 → 没什么可恢复的
                plan = AssetRecovery.Decide("demo.greet", path, disk, newer, cachedClean, disk);
                result.Check("decide: a cache that matches the disk version has nothing to restore",
                    plan.Choice == AutoSaveCache.RecoveryChoice.Disk && !plan.FromCache,
                    plan.Describe());

                // 没有缓存
                plan = AssetRecovery.Decide("demo.greet", path, disk, older, null, null);
                result.Check("decide: without a cache the disk version is used",
                    plan.Choice == AutoSaveCache.RecoveryChoice.Disk && Same(plan.Payload, disk),
                    plan.Describe());

                // 缓存更新但字节读不回来 → 不猜，退回磁盘版
                plan = AssetRecovery.Decide("demo.greet", path, disk, older, cachedSame, null);
                result.Check("decide: **an unreadable cache payload falls back to the disk version**",
                    plan.Choice == AutoSaveCache.RecoveryChoice.Disk && !plan.FromCache
                    && Same(plan.Payload, disk), plan.Describe());
                result.Check("decide: the fallback says why", plan.Explanation.Contains("could not be read"),
                    plan.Explanation);

                // 盘上没有、只有缓存 → 必须能恢复
                plan = AssetRecovery.Decide("memory.only", null, null, DateTime.MinValue, cachedSame,
                    Payload("m1"));
                result.Check("decide: a cache-only package is recoverable",
                    plan.Choice == AutoSaveCache.RecoveryChoice.Cache && plan.FromCache
                    && Same(plan.Payload, Payload("m1")), plan.Describe());
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseCacheOnlyPackageIsPlanned(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                WritePackage(root, "demo.greet.scbtpak", "v1");
                MemoryAssetStore store = NewStore(root);

                string error;
                store.Load("demo.greet", out error);
                store.Adopt("demo.greet", Payload("v1-edited"), AssetOrigin.Ai, out error);
                store.Adopt("memory.only", Payload("m1"), AssetOrigin.Editor, out error);

                var cache = new AutoSaveCache(root);
                result.Check("plan: both records are cached",
                    store.WriteCache("demo.greet", cache, out error)
                    && store.WriteCache("memory.only", cache, out error), error);

                // 断电重启：`memory.only` 从来没写过盘，只有缓存里有
                MemoryAssetStore restarted = NewStore(root);
                var roots = new PackageRoots(PackageRoots.DirectoryFor(root));
                List<AssetRecoveryPlan> plans = AssetRecovery.Plan(roots, cache);

                result.Check("plan: the scan covers packages on disk as well as cache-only ones",
                    plans.Count >= 2, "plans=" + plans.Count);

                AssetRecoveryPlan diskOne = Find(plans, "demo.greet");
                result.Check("plan: a package present on disk gets a plan", diskOne != null);
                result.Check("plan: its payload is the edited memory version",
                    diskOne != null && Same(diskOne.Payload, Payload("v1-edited")),
                    diskOne != null ? diskOne.Describe() : "<none>");

                AssetRecoveryPlan only = Find(plans, "memory.only");
                result.Check("plan: **a cache-only package is planned too**", only != null);
                result.Check("plan: the cache-only plan comes from the cache",
                    only != null && only.FromCache && Same(only.Payload, Payload("m1")),
                    only != null ? only.Describe() : "<none>");

                result.Check("plan: the summary counts what comes from the cache",
                    AssetRecovery.Summarize(plans).Contains("from the autosave"),
                    AssetRecovery.Summarize(plans));

                // 应用到内存库
                List<string> lines = AssetRecovery.Apply(restarted, plans);
                result.Check("plan: applying writes one line per package",
                    lines.Count == plans.Count, "lines=" + lines.Count);
                AssetRecord restored;
                result.Check("plan: **the cache-only package is back in memory**",
                    restarted.TryGet("memory.only", out restored)
                    && Same(restored.Memory, Payload("m1")),
                    "memory.only was lost");
                result.Check("plan: recovery never writes to disk",
                    !File.Exists(Path.Combine(PackageRoots.DirectoryFor(root), "memory.only.scbtpak")),
                    "recovery created a package on disk");
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static AssetRecoveryPlan Find(List<AssetRecoveryPlan> plans, string name)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                if (string.Equals(plans[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return plans[i];
            }
            return null;
        }

        private static void CaseNameAndPathHygiene(BtSelfTest.TestResult result)
        {
            result.Check("name: a package file name loses its extension",
                MemoryAssetStore.NameOf(@"C:\game\PlayerAi\BehaviorTrees\demo.greet.scbtpak")
                == "demo.greet",
                MemoryAssetStore.NameOf(@"C:\game\PlayerAi\BehaviorTrees\demo.greet.scbtpak"));
            result.Check("name: an action package name loses its own extension",
                MemoryAssetStore.NameOf("sample_walk.scatpak") == "sample_walk",
                MemoryAssetStore.NameOf("sample_walk.scatpak"));
            result.Check("name: a bare name passes through",
                MemoryAssetStore.NameOf("demo.greet") == "demo.greet");
            result.Check("name: null and blank are null",
                MemoryAssetStore.NameOf(null) == null && MemoryAssetStore.NameOf("  ") == null);

            string root = Path.Combine(Path.GetTempPath(), "pai-hygiene");
            result.Check("path: a child path is under its parent",
                MemoryAssetStore.IsUnder(Path.Combine(root, "PlayerAi"), root));
            result.Check("path: the root itself counts as under",
                MemoryAssetStore.IsUnder(root, root));
            result.Check("path: a sibling prefix is not 'under'",
                !MemoryAssetStore.IsUnder(root + "x", root), root + "x");
            result.Check("path: an empty value is never under",
                !MemoryAssetStore.IsUnder(null, root) && !MemoryAssetStore.IsUnder(root, null));

            string dir = Path.Combine(Path.GetTempPath(), "pai-backup-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string first = MemoryAssetStore.NextBackupPath(dir, "demo.greet");
                result.Check("backup: the first free generation is v1",
                    first.EndsWith("demo.greet.v1" + MemoryAssetStore.BackupExtension), first);
                File.WriteAllBytes(first, Payload("x"));
                string second = MemoryAssetStore.NextBackupPath(dir, "demo.greet");
                result.Check("backup: the next generation is v2",
                    second.EndsWith("demo.greet.v2" + MemoryAssetStore.BackupExtension), second);
            }
            finally
            {
                Cleanup(dir);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 自动缓存自检（P6）：**纯逻辑 + 真磁盘临时目录**（不依赖游戏）。
    ///
    /// 重点钉住 plan §4.11 的四条规则 —— 它们都是"猜错就毁掉用户手工成果"的那类：
    ///   R1 自动缓存**永不覆盖**手动存档；
    ///   R2 缓存带**版本头**，版本不认就当不可恢复；
    ///   R3 缓存**原子写**，半截文件（哈希对不上）不被当成可恢复；
    ///   R4 `.autosave/` **不是正式包来源**。
    /// </summary>
    public static class AutoSaveSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "AutoSaveSelfTest" };
            try { CaseRoundTrip(result); } catch (Exception e) { result.Check("case:cache round-trip", false, e.Message); }
            try { CaseAtomicAndHalfWritten(result); } catch (Exception e) { result.Check("case:half-written rejected", false, e.Message); }
            try { CaseCacheDoesNotGrow(result); } catch (Exception e) { result.Check("case:cache growth bounded", false, e.Message); }
            try { CaseSchemaVersion(result); } catch (Exception e) { result.Check("case:schema version", false, e.Message); }
            try { CaseRecoveryChoice(result); } catch (Exception e) { result.Check("case:recovery choice", false, e.Message); }
            try { CaseNotAPackageSource(result); } catch (Exception e) { result.Check("case:not a package source", false, e.Message); }
            try { CaseNameHygiene(result); } catch (Exception e) { result.Check("case:name hygiene", false, e.Message); }
            return result;
        }

        private static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "pai-autosave-selftest-" + Guid.NewGuid().ToString("N"));
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

        private static void CaseRoundTrip(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                var cache = new AutoSaveCache(root);
                result.Check("cache directory is derived from the instance root",
                    cache.Directory != null && cache.Directory.EndsWith(AutoSaveCache.FolderName),
                    cache.Directory ?? "<null>");
                result.Check("cache is available when the root is given", cache.Available);

                AutoSaveResult saved = cache.Save("demo.greet", Payload("hello-tree"),
                    "C:/w/demo.greet.scbtpak", "sha256:aaa", "mem-hash-1", true);
                result.Check("saving an entry works", saved.Ok, saved.Describe());
                result.Check("the payload really hit the disk",
                    saved.Path != null && File.Exists(saved.Path), saved.Path ?? "<null>");
                result.Check("cache writes under .autosave", saved.Path != null
                    && AutoSaveCache.IsPackageSource(saved.Path));
                result.Check("no .tmp file is left behind",
                    System.IO.Directory.GetFiles(cache.Directory, "*.tmp").Length == 0);

                List<AutoSaveEntry> entries = cache.ReadEntries();
                result.Check("the entry reads back", entries.Count == 1, "count=" + entries.Count);
                if (entries.Count == 1)
                {
                    AutoSaveEntry entry = entries[0];
                    result.Check("round-trip keeps the metadata",
                        entry.Name == "demo.greet" && entry.SourceHash == "sha256:aaa"
                        && entry.MemoryHash == "mem-hash-1" && entry.Dirty
                        && entry.SchemaVersion == AutoSaveCache.SchemaVersion,
                        entry.ToString());
                    result.Check("round-trip keeps the bytes",
                        Encoding.UTF8.GetString(cache.ReadPayload(entry)) == "hello-tree");

                    // 同名再存一次：只留最新一条（缓存是影子副本，不留历史）
                    System.Threading.Thread.Sleep(20);
                    cache.Save("demo.greet", Payload("hello-tree-v2"),
                        "C:/w/demo.greet.scbtpak", "sha256:aaa", "mem-hash-2", true);
                    List<AutoSaveEntry> again = cache.ReadEntries();
                    result.Check("saving the same name replaces the entry (no history)",
                        again.Count == 1, "count=" + again.Count);
                    result.Check("the newest content wins",
                        again.Count == 1 && Encoding.UTF8.GetString(cache.ReadPayload(again[0])) == "hello-tree-v2");
                }

                result.Check("empty payloads are refused",
                    !cache.Save("x", new byte[0], null, null, null, false).Ok);
                result.Check("nameless entries are refused",
                    !cache.Save(null, Payload("x"), null, null, null, false).Ok);
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseCacheDoesNotGrow(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                var cache = new AutoSaveCache(root);

                AutoSaveResult first = cache.Save("a", Payload("v1"), "C:/a.scbtpak", "sha256:s1",
                    "sha256:m1", true);
                result.Check("cache growth: the first write lands", first.Ok, first.Describe());
                string firstPayload = Path.GetFileName(first.Path);

                AutoSaveResult second = cache.Save("a", Payload("v2"), "C:/a.scbtpak", "sha256:s1",
                    "sha256:m2", true);
                result.Check("cache growth: a second write for the same name lands", second.Ok,
                    second.Describe());
                string secondPayload = Path.GetFileName(second.Path);
                result.Check("cache growth: it uses a fresh payload file",
                    firstPayload != secondPayload, firstPayload + " / " + secondPayload);
                result.Check("*** cache growth: the superseded payload is deleted ***",
                    !File.Exists(Path.Combine(cache.Directory, firstPayload)), firstPayload);

                // 孤儿扫描：手工丢一个"没有索引条目指向"的 payload 进去
                string orphan = Path.Combine(cache.Directory, "orphan.99" + AutoSaveCache.PayloadExtension);
                File.WriteAllBytes(orphan, Payload("junk"));
                result.Check("cache growth: the orphan is there before the next write",
                    File.Exists(orphan));

                AutoSaveResult third = cache.Save("b", Payload("v9"), "C:/b.scbtpak", null,
                    "sha256:m9", true);
                result.Check("cache growth: a write for another name lands", third.Ok, third.Describe());
                result.Check("*** cache growth: payloads no entry points at are swept ***",
                    !File.Exists(orphan), "the orphan survived");

                List<AutoSaveEntry> entries = cache.ReadEntries();
                result.Check("cache growth: one entry per name", entries.Count == 2,
                    "entries=" + entries.Count);
                bool referencedAlive = true;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!File.Exists(Path.Combine(cache.Directory, entries[i].PayloadPath)))
                        referencedAlive = false;
                }
                result.Check("cache growth: every referenced payload survives the sweep",
                    referencedAlive, "a live payload was swept");
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseAtomicAndHalfWritten(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                var cache = new AutoSaveCache(root);
                AutoSaveResult saved = cache.Save("a", Payload("payload-a"), "C:/a.scbtpak", "sha256:a", "m", false);
                result.Check("baseline entry saved", saved.Ok, saved.Describe());

                // 模拟"断电写了一半"：把 payload 内容改掉，哈希必然对不上
                File.WriteAllText(saved.Path, "half-writ", new UTF8Encoding(false));
                List<AutoSaveEntry> entries = cache.ReadEntries();
                result.Check("a payload whose hash does not match is NOT recoverable",
                    entries.Count == 0, "count=" + entries.Count);
                result.Check("the rejection says why (half-written)",
                    cache.LastRejectReason != null
                    && cache.LastRejectReason.Contains("hash", StringComparison.OrdinalIgnoreCase),
                    cache.LastRejectReason);

                // payload 整个不见了
                File.Delete(saved.Path);
                result.Check("a missing payload is not recoverable", cache.ReadEntries().Count == 0);
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseSchemaVersion(BtSelfTest.TestResult result)
        {
            string root = NewRoot();
            try
            {
                var cache = new AutoSaveCache(root);
                cache.Save("a", Payload("payload-a"), "C:/a.scbtpak", "sha256:a", "m", false);

                // 把索引的 schemaVersion 改成一个"未来版本" → 必须当不可恢复
                string indexPath = Path.Combine(cache.Directory, AutoSaveCache.IndexFileName);
                string text = File.ReadAllText(indexPath);
                text = text.Replace("\"schemaVersion\": " + AutoSaveCache.SchemaVersion,
                    "\"schemaVersion\": 99");
                File.WriteAllText(indexPath, text, new UTF8Encoding(false));

                result.Check("an unknown cache schema version is treated as unrecoverable (R2)",
                    cache.ReadEntries().Count == 0, "count=" + cache.ReadEntries().Count);
                result.Check("the version rejection is explained",
                    cache.LastRejectReason != null && cache.LastRejectReason.Contains("schema"),
                    cache.LastRejectReason);

                // 坏 JSON 也不能抛
                File.WriteAllText(indexPath, "{ broken", new UTF8Encoding(false));
                result.Check("a corrupt index does not throw and yields nothing",
                    cache.ReadEntries().Count == 0);
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void CaseRecoveryChoice(BtSelfTest.TestResult result)
        {
            // 「来源哈希」= 缓存写入那一刻**磁盘版**的哈希
            // 「内存哈希」= 缓存里装的那一版（内存态）的哈希
            // 两者不同 = 缓存里有磁盘上没有的改动 = 这份缓存有价值
            var clean = new AutoSaveEntry
            {
                Name = "a",
                SourceHash = "sha256:aaa",
                MemoryHash = "sha256:aaa",
                SavedUtc = "2026-09-26T10:00:00.0000000Z",
                Dirty = false,
                SchemaVersion = AutoSaveCache.SchemaVersion
            };
            var dirty = new AutoSaveEntry
            {
                Name = "a",
                SourceHash = "sha256:aaa",
                MemoryHash = "sha256:mmm",
                SavedUtc = "2026-09-26T10:00:00.0000000Z",
                Dirty = true,
                SchemaVersion = AutoSaveCache.SchemaVersion
            };
            var older = new DateTime(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);
            var newer = new DateTime(2026, 9, 26, 11, 0, 0, DateTimeKind.Utc);

            result.Check("no cache -> use the package on disk",
                AutoSaveCache.ChooseRecovery(null, "sha256:aaa", older) == AutoSaveCache.RecoveryChoice.Disk);

            result.Check("a clean cache (memory == disk) has nothing to recover",
                AutoSaveCache.ChooseRecovery(clean, "sha256:aaa", older)
                == AutoSaveCache.RecoveryChoice.Disk);

            result.Check("*** an unchanged disk + unsaved memory edits -> use the cache (R1) ***",
                AutoSaveCache.ChooseRecovery(dirty, "sha256:aaa", older)
                == AutoSaveCache.RecoveryChoice.Cache,
                AutoSaveCache.ChooseRecovery(dirty, "sha256:aaa", older).ToString());

            result.Check("disk changed after the cache -> the human's edit wins (R1)",
                AutoSaveCache.ChooseRecovery(dirty, "sha256:bbb", newer)
                == AutoSaveCache.RecoveryChoice.DiskNewer);

            result.Check("a cache that equals the current disk never wins over the disk",
                AutoSaveCache.ChooseRecovery(dirty, "sha256:mmm", newer)
                == AutoSaveCache.RecoveryChoice.Disk);

            result.Check("no disk version at all -> the cache is the only copy (R1)",
                AutoSaveCache.ChooseRecovery(dirty, null, DateTime.MinValue)
                == AutoSaveCache.RecoveryChoice.Cache);
            result.Check("no disk version but a clean cache -> still the only copy",
                AutoSaveCache.ChooseRecovery(clean, null, DateTime.MinValue)
                == AutoSaveCache.RecoveryChoice.Cache);

            result.Check("a cache older than an unchanged disk is not trusted",
                AutoSaveCache.ChooseRecovery(dirty, "sha256:aaa", newer)
                == AutoSaveCache.RecoveryChoice.Disk);

            // 关键一条：**disk 与 cache 不同源**时永远不静默用缓存
            AutoSaveCache.RecoveryChoice choice =
                AutoSaveCache.ChooseRecovery(dirty, "sha256:ccc", older);
            result.Check("a mismatched disk hash never silently prefers the cache",
                choice == AutoSaveCache.RecoveryChoice.DiskNewer, choice.ToString());

            result.Check("the explanation is human readable and names the reason",
                AutoSaveCache.Explain(AutoSaveCache.RecoveryChoice.DiskNewer, dirty, newer).Contains("changed"),
                AutoSaveCache.Explain(AutoSaveCache.RecoveryChoice.DiskNewer, dirty, newer));
            result.Check("the cache explanation says the changes were never saved",
                AutoSaveCache.Explain(AutoSaveCache.RecoveryChoice.Cache, dirty, older).Contains("never saved"),
                AutoSaveCache.Explain(AutoSaveCache.RecoveryChoice.Cache, dirty, older));
            result.Check("the cache-only explanation says it is the only copy",
                AutoSaveCache.Explain(AutoSaveCache.RecoveryChoice.Cache, dirty, DateTime.MinValue)
                    .Contains("only copy"),
                AutoSaveCache.Explain(AutoSaveCache.RecoveryChoice.Cache, dirty, DateTime.MinValue));
        }

        private static void CaseNotAPackageSource(BtSelfTest.TestResult result)
        {
            string windows = @"C:\game\PlayerAi\.autosave\a.1.pkg";
            string unix = "/game/PlayerAi/.autosave/a.1.pkg";
            result.Check("windows .autosave paths are not a package source (R4)",
                AutoSaveCache.IsPackageSource(windows));
            result.Check("unix .autosave paths are not a package source (R4)",
                AutoSaveCache.IsPackageSource(unix));
            result.Check("normal package paths ARE a package source",
                !AutoSaveCache.IsPackageSource(@"C:\game\PlayerAi\BehaviorTrees\demo.greet.scbtpak"));
            result.Check("null/empty is not a package source",
                !AutoSaveCache.IsPackageSource(null) && !AutoSaveCache.IsPackageSource(""));
        }

        private static void CaseNameHygiene(BtSelfTest.TestResult result)
        {
            result.Check("path separators in names are neutralized",
                AutoSaveCache.Sanitize("../../etc/passwd").IndexOf('/') < 0
                && AutoSaveCache.Sanitize(@"..\..\win").IndexOf('\\') < 0,
                AutoSaveCache.Sanitize("../../etc/passwd"));
            result.Check("empty names fall back to a safe default",
                AutoSaveCache.Sanitize(null) == "entry" && AutoSaveCache.Sanitize("...") == "entry");
            result.Check("legitimate names survive",
                AutoSaveCache.Sanitize("demo.greet") == "demo.greet"
                && AutoSaveCache.Sanitize("su.watch_2") == "su.watch_2");
        }
    }
}

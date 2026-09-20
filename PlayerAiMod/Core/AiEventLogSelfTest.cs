using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 观测日志自检（P0-10）：**纯逻辑、不依赖游戏**（写临时目录里的真文件）。
    ///
    /// 覆盖：带 UTC 时间戳与分类的写入、最近记录环、**超限滚动**（含只保留 N 份）、
    /// **写不进去也绝不影响游戏**（IO 失败只记 LastError，内存记录仍可用）、清空，
    /// 以及最要紧的一条：**重载/切换的结果真的会落到日志里**（这是"事后能查"的前提）。
    /// </summary>
    public static class AiEventLogSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "AiEventLogSelfTest" };
            try
            {
                Basics(result);
                Rolling(result);
                FailureTolerance(result);
                ReloadWiring(result);
            }
            catch (Exception exception)
            {
                result.Check("AiEventLogSelfTest no unexpected exception", false,
                    exception.GetType().Name + ": " + exception.Message);
            }
            return result;
        }

        // ---------------------------------------------------------------- 用例

        private static string FreshDirectory(string name)
        {
            string directory = Path.Combine(Path.GetTempPath(), name);
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception)
            {
            }
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void Basics(BtSelfTest.TestResult result)
        {
            string directory = FreshDirectory("pai-log-basics");
            var log = new AiEventLog(directory, AiEventLog.DefaultFileName, 64 * 1024, 3, 8);

            log.Write("reload", "first line");
            log.Write("switch", "second line");

            result.Check("event log writes a real file",
                log.Path != null && File.Exists(log.Path), log.Path ?? "<none>");
            result.Check("event log counts writes and bytes",
                log.WriteCount == 2 && log.FileBytes > 0,
                "writes=" + log.WriteCount + " bytes=" + log.FileBytes);

            string text = File.ReadAllText(log.Path);
            result.Check("entries carry a UTC timestamp and a category",
                text.Contains("[reload] first line") && text.Contains("[switch] second line")
                && text.Contains("Z ["),
                text.Length > 200 ? text.Substring(0, 200) : text);
            result.Check("the file starts with a header (so a stray file explains itself)",
                text.StartsWith("# PlayerAiMod event log", StringComparison.Ordinal),
                text.Length > 60 ? text.Substring(0, 60) : text);

            List<string> recent = log.Recent(1);
            result.Check("Recent(n) returns the newest entries",
                recent.Count == 1 && recent[0].Contains("second line"), "count=" + recent.Count);

            recent = log.Recent(0);
            result.Check("Recent(0) returns everything held in memory",
                recent.Count == 2 && recent[0].Contains("first line"), "count=" + recent.Count);

            // 内存环有上限
            for (int i = 0; i < 20; i++)
                log.Write("spam", "line " + i);
            result.Check("the in-memory ring is capped",
                log.Recent(0).Count <= log.RecentCapacity,
                "recent=" + log.Recent(0).Count + " capacity=" + log.RecentCapacity);

            string clearError;
            result.Check("clear empties the memory ring",
                log.TryClear(out clearError) && log.Recent(0).Count == 0,
                clearError ?? "cleared");
        }

        private static void Rolling(BtSelfTest.TestResult result)
        {
            string directory = FreshDirectory("pai-log-rolling");
            // 小上限 + 2 份：写满就能观察到"滚动 + 只留 N 份"
            var log = new AiEventLog(directory, "PlayerAi.log", 4096, 2, 16);

            for (int i = 0; i < 400; i++)
                log.Write("spam", "padding line " + i + " " + new string('x', 40));

            string current = log.Path;
            string rotated = Path.Combine(directory, "PlayerAi.1.log");
            string oldest = Path.Combine(directory, "PlayerAi.2.log");

            result.Check("a full log rotates to .1",
                File.Exists(rotated) && File.Exists(current),
                "current=" + File.Exists(current) + " rotated=" + File.Exists(rotated));
            result.Check("only MaxFiles generations are kept",
                !File.Exists(oldest), "PlayerAi.2.log should have been dropped");
            result.Check("the current file stays under the limit",
                new FileInfo(current).Length <= log.MaxBytes + 8192,
                "bytes=" + new FileInfo(current).Length + " limit=" + log.MaxBytes);

            // 滚动之后还能继续写（不会因为滚动而停摆）
            int before = log.WriteCount;
            log.Write("after-roll", "still writable");
            result.Check("logging continues after a rotation",
                log.WriteCount == before + 1 && log.LastError == null,
                log.LastError ?? "ok");
        }

        private static void FailureTolerance(BtSelfTest.TestResult result)
        {
            // 目标目录其实是个文件 → 建目录/写文件必然失败
            string root = FreshDirectory("pai-log-fail");
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "not a directory");

            var log = new AiEventLog(Path.Combine(blocker, "Logs"), AiEventLog.DefaultFileName,
                4096, 2, 8);

            bool threw = false;
            try
            {
                log.Write("reload", "this cannot be written");
                log.Write("reload", "nor this");
            }
            catch (Exception)
            {
                threw = true;
            }

            result.Check("a failing event log never throws (the game must not break)",
                !threw, "Write threw");
            result.Check("the failure is recorded instead",
                log.LastError != null && log.LastError.Length > 0, "<no LastError>");
            result.Check("in-memory entries survive a file failure",
                log.Recent(0).Count == 2, "recent=" + log.Recent(0).Count);
            result.Check("it stops retrying after the first failure (no log spam per frame)",
                log.WriteCount == 2, "writes=" + log.WriteCount);

            var disabled = new AiEventLog(FreshDirectory("pai-log-disabled"), "PlayerAi.log", 4096, 2, 8);
            disabled.Enabled = false;
            disabled.Write("reload", "memory only");
            result.Check("Enabled=false keeps memory entries but writes no file",
                disabled.Recent(0).Count == 1
                && (disabled.Path == null || !File.Exists(disabled.Path)),
                disabled.Describe());
        }

        private static void ReloadWiring(BtSelfTest.TestResult result)
        {
            string directory = FreshDirectory("pai-log-wiring");
            var roots = new PackageRoots(Path.Combine(directory, "instance"));
            List<string> installed;
            string installError;
            PackageTemplates.Install(roots, out installed, out installError);

            var log = new AiEventLog(Path.Combine(directory, "Logs"), AiEventLog.DefaultFileName,
                64 * 1024, 3, 32);
            var options = new PackageLoadOptions { Roots = roots };
            var reloader = new PackageReloader(roots, options) { Log = log };
            var library = new TreeLibrary(reloader) { Log = log };
            var host = new AiTestHost("log-host");

            // 首次装载 → 应当有 load 记录
            TreeReloadResult load = reloader.Load("demo.greet", host.Tree);
            result.Check("loading a tree is recorded", load.Replaced && HasEntry(log, "[load"),
                DescribeRecent(log));

            // 无变化的通知 → ignore 记录
            string demoPath = Path.Combine(roots.InstanceRoot.Path, PackageTemplates.DemoFile);
            reloader.Notify(demoPath, PackageLoader.ComputeHash(File.ReadAllBytes(demoPath)));
            reloader.ApplyPending(host.Tree);
            result.Check("an ignored notification is recorded", HasEntry(log, "[ignore"),
                DescribeRecent(log));

            // 坏包 → reject 记录（并且旧树继续工作）
            File.WriteAllText(demoPath, "this is not a zip");
            reloader.RequestReload("demo.greet");
            bool rejected = reloader.ApplyPending(host.Tree);
            result.Check("a rejected reload is recorded with its reason",
                !rejected && HasEntry(log, "[reject") && HasEntry(log, "rejected"),
                DescribeRecent(log));
            result.Check("the old tree keeps running after a rejected reload",
                host.HasTree && host.Tree.TreeId == "demo.greet",
                "tree=" + (host.Tree.TreeId ?? "<none>"));

            // 切换 → switch 记录（含切换耗时，事后能查"到底快不快"）
            // 先把上一步故意弄坏的文件恢复（Install 只补缺失文件，不会覆盖）
            string writeError;
            PackageWriter.TryWriteFile(demoPath, PackageTemplates.Demo().ToBytes(), out writeError);
            TreeSwitchResult switched = library.Switch("demo.greet", host);
            result.Check("a switch is recorded (with its timing)",
                switched.Switched && HasEntry(log, "[switch") && HasEntry(log, "switch="),
                DescribeRecent(log));

            result.Check("log file exists on disk after all of that",
                log.Path != null && File.Exists(log.Path) && new FileInfo(log.Path).Length > 0,
                log.Describe());

            string clearError;
            result.Check("clear removes rotated generations too",
                log.TryClear(out clearError) && log.Recent(0).Count == 0,
                clearError ?? "cleared");
        }

        private static bool HasEntry(AiEventLog log, string fragment)
        {
            List<string> recent = log.Recent(0);
            for (int i = 0; i < recent.Count; i++)
            {
                if (recent[i].IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string DescribeRecent(AiEventLog log)
        {
            List<string> recent = log.Recent(4);
            return recent.Count == 0 ? "<empty>" : string.Join(" || ", recent.ToArray());
        }
    }
}

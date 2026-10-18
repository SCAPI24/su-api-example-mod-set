using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Game;
using SuMultiplayer.Transfer;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎侧的世界导出/导入适配器（**薄**）。
    /// 用引擎自带的 <c>WorldsManager.ExportWorld/ImportWorld</c>：
    /// 它们本来就是这个游戏分享/导入世界用的路径，格式与校验（含 Project.xml 检查）
    /// 都由引擎负责，不需要我们自己压包 —— 少一层自造格式就少一堆坑。
    /// 演示页标记：XFR04。
    /// </summary>
    public sealed class EngineWorldTransfer : IWorldExportSource, IWorldImporter, IDownloadedWorldStore
    {
        /// <summary>
        /// 主机：导出**自己正在玩的那个世界**。
        ///
        /// 顺序照旧实现（WorldSnapshotFileCopier 的契约）：
        ///   ① 主机必须已经在世界里（没在世界里就不该有"创建房间"这一步）
        ///   ② SaveProject 先落盘
        ///   ③ 把世界目录复制到临时目录 —— 源文件按 ReadWrite|Delete 共享打开，
        ///      这样才能绕过运行中游戏对 Region 文件的占用（直接 ExportWorld 会 IOException）
        ///   ④ 从**副本** ExportWorld（副本没人占用）
        ///   ⑤ 清理临时目录
        /// 演示页标记：XFR06。
        /// </summary>
        public bool TryExport(out byte[] pak, out string worldName, out string directoryName, out string reason)
        {
            pak = null;
            worldName = string.Empty;
            directoryName = string.Empty;
            reason = null;
            SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (info == null || string.IsNullOrEmpty(info.DirectoryName))
            {
                reason = "主机必须先进入世界，再创建房间";
                return false;
            }
            string temp = Storage.CombinePaths("data:/Temp", "sump-export-" + (Environment.TickCount & 0xFFFFFF));
            try
            {
                GameManager.SaveProject(waitForCompletion: true, showErrorDialog: false);
                ExportedFiles = CopyDirectoryShared(info.DirectoryName, temp);
                using var memory = new MemoryStream();
                WorldsManager.ExportWorld(temp, memory);
                pak = memory.ToArray();
                worldName = info.WorldSettings?.Name ?? string.Empty;
                directoryName = info.DirectoryName;
                if (pak.Length == 0)
                {
                    reason = "导出为空";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
                return false;
            }
            finally
            {
                CleanupTemp(temp);
            }
        }

        /// <summary>最近一次导出复制了多少个世界文件（诊断用）。</summary>
        public int ExportedFiles { get; private set; }

        /// <summary>把世界目录复制到临时目录；源文件按共享方式打开（照旧实现 WorldSnapshotFileCopier.CopyFile）。</summary>
        private static int CopyDirectoryShared(string sourcePath, string targetPath)
        {
            string source = Storage.GetSystemPath(sourcePath);
            string target = Storage.GetSystemPath(targetPath);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            Directory.CreateDirectory(target);
            int count = 0;
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string dest = Path.Combine(target, relative);
                string parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                using var output = new FileStream(dest, FileMode.Create, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.SequentialScan);
                input.CopyTo(output, 64 * 1024);
                count++;
            }
            return count;
        }

        private static void CleanupTemp(string tempPath)
        {
            try
            {
                string systemPath = Storage.GetSystemPath(tempPath);
                if (Directory.Exists(systemPath)) Directory.Delete(systemPath, true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>客户端：把收到的 pak 导入成一个新的世界目录（引擎自己分配不重名的目录）。</summary>
        public bool TryImport(byte[] pak, out string installedDirectory, out string reason)
        {
            installedDirectory = null;
            reason = null;
            try
            {
                using var memory = new MemoryStream(pak ?? Array.Empty<byte>());
                installedDirectory = WorldsManager.ImportWorld(memory);
                if (string.IsNullOrEmpty(installedDirectory))
                {
                    reason = "引擎未返回目录名";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>导入后刷新世界列表，并把该世界找出来（供进入游戏用）。</summary>
        public static WorldInfo FindImported(string directoryName)
        {
            try
            {
                var infos = WorldsManager.WorldInfos;
                for (int i = 0; i < infos.Count; i++)
                {
                    if (string.Equals(infos[i].DirectoryName, directoryName, StringComparison.OrdinalIgnoreCase))
                        return infos[i];
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // ---------------------------------------------------------------- 下载世界的登记与删除

        /// <summary>下载世界登记表（旧实现 ScMultiplayer 用的同一套做法：一行一个目录）。</summary>
        private const string DownloadedWorldsRegistryPath = "data:/SuMultiplayerDownloadedWorlds.txt";

        /// <summary>
        /// 登记表的一行：<c>sessionId \t baseSequence \t directory</c>。
        /// 旧文件里的裸目录行仍然可读（没有会话标识 ⇒ 重连时按首次加入处理，绝不猜）。
        /// </summary>
        private sealed class DownloadedWorldEntry
        {
            public string Directory = string.Empty;
            public string SessionId = string.Empty;
            public long BaseSequence;
        }

        // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:RegisterDownloadedWorld
        public void Register(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                List<DownloadedWorldEntry> entries = ReadRegistryEntries();
                if (FindEntry(entries, directory) != null) return;
                entries.Add(new DownloadedWorldEntry { Directory = directory });
                WriteRegistryEntries(entries);
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 登记下载世界失败（" + directory + "）：" + e.GetType().Name + " " + e.Message);
            }
        }

        // 会话标识是"这份下载世界属于哪个主机会话"的登记（WorldBegin 的 append-only 字段）。
        public void RegisterSession(string directory, string sessionId, long baseSequence)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                List<DownloadedWorldEntry> entries = ReadRegistryEntries();
                DownloadedWorldEntry entry = FindEntry(entries, directory);
                if (entry == null)
                {
                    entry = new DownloadedWorldEntry { Directory = directory };
                    entries.Add(entry);
                }
                entry.SessionId = (sessionId ?? string.Empty).Trim();
                entry.BaseSequence = Math.Max(0L, baseSequence);
                WriteRegistryEntries(entries);
                Log.Information("[SuMP] 下载世界已登记会话：" + directory + " · session=" +
                    (entry.SessionId.Length == 0 ? "(none)" : entry.SessionId) +
                    " · base=" + entry.BaseSequence);
            }
            catch (Exception e)
            {
                // 明确失败：没有登记成功 ⇒ 之后的重连会按"没有会话标识"处理（整图重下），不静默。
                Log.Error("[SuMP] 登记下载世界会话失败（" + directory + "）：" +
                    e.GetType().Name + " " + e.Message + " → 之后的重连会按首次加入处理");
            }
        }

        public bool TryGetSession(string directory, out string sessionId, out long baseSequence)
        {
            sessionId = string.Empty;
            baseSequence = 0L;
            if (string.IsNullOrEmpty(directory)) return false;
            DownloadedWorldEntry entry = FindEntry(ReadRegistryEntries(), directory);
            if (entry == null || string.IsNullOrEmpty(entry.SessionId)) return false;
            sessionId = entry.SessionId;
            baseSequence = entry.BaseSequence;
            return true;
        }

        public void ForgetSession(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                List<DownloadedWorldEntry> entries = ReadRegistryEntries();
                DownloadedWorldEntry entry = FindEntry(entries, directory);
                if (entry == null) return;
                entry.SessionId = string.Empty;
                entry.BaseSequence = 0L;
                WriteRegistryEntries(entries);
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 作废下载世界会话失败（" + directory + "）：" +
                    e.GetType().Name + " " + e.Message + " → 旧会话可能仍留在登记表里，主机侧会再次判定");
            }
        }

        private static DownloadedWorldEntry FindEntry(List<DownloadedWorldEntry> entries, string directory)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Directory, directory, StringComparison.OrdinalIgnoreCase))
                    return entries[i];
            }
            return null;
        }

        public IReadOnlyList<string> Registered
        {
            get
            {
                List<DownloadedWorldEntry> entries = ReadRegistryEntries();
                var directories = new List<string>(entries.Count);
                for (int i = 0; i < entries.Count; i++) directories.Add(entries[i].Directory);
                return directories;
            }
        }

        // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:WriteDownloadedWorldRegistry
        // 只保留 surviving 目录，但**已有条目的会话标识原样保留**（清世界不该把别的世界的会话标签弄丢）。
        public void SaveRegistry(IReadOnlyList<string> directories)
        {
            try
            {
                List<DownloadedWorldEntry> current = ReadRegistryEntries();
                var kept = new List<DownloadedWorldEntry>(directories?.Count ?? 0);
                if (directories != null)
                {
                    for (int i = 0; i < directories.Count; i++)
                    {
                        string directory = directories[i];
                        if (string.IsNullOrEmpty(directory)) continue;
                        DownloadedWorldEntry entry = FindEntry(current, directory);
                        kept.Add(entry ?? new DownloadedWorldEntry { Directory = directory });
                    }
                }
                WriteRegistryEntries(kept);
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 写下载世界登记表失败：" + e.GetType().Name + " " + e.Message);
            }
        }

        private static void WriteRegistryEntries(List<DownloadedWorldEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                if (Storage.FileExists(DownloadedWorldsRegistryPath))
                    Storage.DeleteFile(DownloadedWorldsRegistryPath);
                return;
            }
            var lines = new List<string>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                DownloadedWorldEntry entry = entries[i];
                // 空会话标识也照写占位（列数固定，读的时候不会把目录当成会话）。
                lines.Add((entry.SessionId ?? string.Empty) + "\t" +
                    entry.BaseSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" +
                    entry.Directory);
            }
            Storage.WriteAllText(DownloadedWorldsRegistryPath, string.Join("\n", lines));
        }

        // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:DeleteDownloadedWorldAndUnregister
        public bool Delete(string directory, out string reason)
        {
            reason = null;
            try
            {
                WorldsManager.DeleteWorld(directory);
                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:ReadDownloadedWorldRegistry
        private static List<DownloadedWorldEntry> ReadRegistryEntries()
        {
            var entries = new List<DownloadedWorldEntry>();
            try
            {
                if (!Storage.FileExists(DownloadedWorldsRegistryPath)) return entries;
                string text = Storage.ReadAllText(DownloadedWorldsRegistryPath);
                foreach (string line in (text ?? string.Empty).Split('\n'))
                {
                    // Storage.WriteAllText 会写 UTF-8 BOM，BOM 不是 char.IsWhiteSpace，
                    // 不剥掉的话第一行会变成 "\uFEFFdata:/Worlds/World"，删世界必然失败。
                    string trimmed = line.Trim().TrimStart('\uFEFF');
                    if (trimmed.Length == 0) continue;
                    string[] parts = trimmed.Split('\t');
                    if (parts.Length >= 3 &&
                        long.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out long baseSequence) &&
                        parts[2].Trim().Length > 0)
                    {
                        entries.Add(new DownloadedWorldEntry
                        {
                            SessionId = parts[0].Trim(),
                            BaseSequence = Math.Max(0L, baseSequence),
                            Directory = parts[2].Trim()
                        });
                        continue;
                    }
                    if (parts.Length == 1)
                    {
                        // 旧格式（裸目录）：没有会话标识 —— 明确按"没有"处理，重连时整图重下。
                        entries.Add(new DownloadedWorldEntry { Directory = trimmed });
                        continue;
                    }
                    Log.Warning("[SuMP] 跳过无法识别的下载世界登记行（列数 " + parts.Length +
                        "，既不是目录也不是 session\\tbase\\tdir）：" + trimmed);
                }
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 读下载世界登记表失败：" + e.GetType().Name + " " + e.Message);
            }
            return entries;
        }
    }

}

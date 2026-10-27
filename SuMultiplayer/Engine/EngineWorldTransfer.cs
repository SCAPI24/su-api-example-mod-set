using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        /// 导出世界前回调（组合根注入）：把 SuMultiplayerPlayers.xml 的脏记录（含客户端
        /// SpawnPosition）先 SaveIfDirty()，保证中途加入的客户端下载包里带的是最新复活点。
        /// 仅联机 Mod 使用；原版导出路径不依赖此项。
        /// </summary>
        public Action BeforeExport { get; set; }
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
                // 先落联机角色记录（客户端复活点），再 SaveProject / 复制世界目录。
                // 失败不静默：回调内部自己记录；这里仍继续原版导出路径。
                try { BeforeExport?.Invoke(); }
                catch (Exception hookError)
                {
                    reason = "导出前保存联机角色记录失败：" + hookError.GetType().Name + " " +
                        hookError.Message;
                    return false;
                }
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

        /// <summary>
        /// 下载来的世界是**临时目录**：客户端**不保存任何下载世界**（用户口径 2026-10-21）。
        /// 每个下载世界在自己的目录里有一个标记文件；回到 Play 屏 / 空闲时的清理流程会删除
        /// 所有带标记的世界 → 每次加入都从主机重新整图下载，不同世界之间不保留任何东西。
        /// 标记随世界目录一起删除，不会在仓库根留全局登记表。
        /// </summary>
        private const string DownloadedMarkerFileName = "SuMultiplayerDownloadedWorld.mark";

        /// <summary>
        /// **只在本次进程内**记住"下载世界 ↔ 主机会话/基线"，用于同一次游玩里的断线重连（追帧）。
        /// 进程重启 / 离开房间后就没有这份记忆 → Hello 不声明重连，主机按首次加入重新下发整图；
        /// 世界目录本身也会被清理删掉（客户端不保留下载世界）。
        /// </summary>
        private readonly Dictionary<string, SessionMemory> m_sessionMemory =
            new Dictionary<string, SessionMemory>(StringComparer.OrdinalIgnoreCase);

        private sealed class SessionMemory
        {
            public string SessionId = string.Empty;
            public long BaseSequence;
        }

        private static string MarkerPath(string directory) =>
            string.IsNullOrEmpty(directory)
                ? string.Empty
                : Storage.CombinePaths(directory, DownloadedMarkerFileName);

        /// <summary>该目录是不是"下载来的世界"（世界目录里有标记文件）。</summary>
        public bool IsDownloaded(string directory) =>
            !string.IsNullOrEmpty(directory) && Storage.FileExists(MarkerPath(directory));

        /// <summary>登记一个"下载来的世界"（写标记；没有会话标识，重连时按首次加入处理）。</summary>
        public void Register(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            if (Storage.FileExists(MarkerPath(directory))) return;
            try
            {
                Storage.WriteAllText(MarkerPath(directory), "downloaded\n");
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 标记下载世界失败（" + directory + "）：" + e.GetType().Name +
                    " " + e.Message + " → 该目录不会被自动清理（下次清理时也不会认得它）");
            }
        }

        // 会话标识是"这份下载世界属于哪个主机会话"的登记（WorldBegin 的 append-only 字段）。
        // **只放内存**：客户端不保存下载世界，重启后就不该再声称"我还拿着上一轮的会话"。
        public void RegisterSession(string directory, string sessionId, long baseSequence)
        {
            if (string.IsNullOrEmpty(directory)) return;
            Register(directory);
            m_sessionMemory[directory] = new SessionMemory
            {
                SessionId = (sessionId ?? string.Empty).Trim(),
                BaseSequence = Math.Max(0L, baseSequence)
            };
            Log.Information("[SuMP] 下载世界会话（仅本次进程内）：" + directory + " · session=" +
                (string.IsNullOrEmpty(sessionId) ? "(none)" : sessionId) +
                " · base=" + Math.Max(0L, baseSequence));
        }

        public bool TryGetSession(string directory, out string sessionId, out long baseSequence)
        {
            sessionId = string.Empty;
            baseSequence = 0L;
            if (string.IsNullOrEmpty(directory)) return false;
            if (!m_sessionMemory.TryGetValue(directory, out SessionMemory memory) || memory == null)
                return false;   // 重启 / 清过 = 没有会话 ⇒ 首次加入（整图重下）
            if (string.IsNullOrEmpty(memory.SessionId)) return false;
            sessionId = memory.SessionId;
            baseSequence = memory.BaseSequence;
            return true;
        }

        /// <summary>作废该世界的会话记忆（世界目录仍然保留到清理时删除）。</summary>
        public void ForgetSession(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            m_sessionMemory.Remove(directory);
        }

        /// <summary>当前所有"下载来的世界"目录（= 世界里带标记文件的那些，含上次异常退出的残留）。</summary>
        public IReadOnlyList<string> Registered
        {
            get
            {
                var directories = new List<string>();
                try
                {
                    ReadOnlyList<WorldInfo> infos = WorldsManager.WorldInfos;
                    for (int i = 0; i < infos.Count; i++)
                    {
                        string directory = infos[i]?.DirectoryName;
                        if (string.IsNullOrEmpty(directory)) continue;
                        if (IsDownloaded(directory)) directories.Add(directory);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[SuMP] 枚举下载世界失败：" + e.GetType().Name + " " + e.Message +
                        " → 本次清理按空表处理（不会误删非下载世界）");
                }
                return directories;
            }
        }

        /// <summary>
        /// 清理后对齐：不保留任何持久登记，只需要把"清理失败、但已经被判定不该留"的标记删掉。
        /// </summary>
        public void SaveRegistry(IReadOnlyList<string> directories)
        {
            try
            {
                ReadOnlyList<WorldInfo> infos = WorldsManager.WorldInfos;
                for (int i = 0; i < infos.Count; i++)
                {
                    string directory = infos[i]?.DirectoryName;
                    if (string.IsNullOrEmpty(directory)) continue;
                    string path = MarkerPath(directory);
                    if (!Storage.FileExists(path)) continue;
                    bool kept = false;
                    if (directories != null)
                    {
                        for (int j = 0; j < directories.Count; j++)
                        {
                            if (string.Equals(directories[j], directory, StringComparison.OrdinalIgnoreCase))
                            {
                                kept = true;
                                break;
                            }
                        }
                    }
                    if (!kept) Storage.DeleteFile(path);
                }
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 对齐下载世界标记失败：" + e.GetType().Name + " " + e.Message);
            }
        }

        // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:DeleteDownloadedWorldAndUnregister
        public bool Delete(string directory, out string reason)
        {
            reason = null;
            try
            {
                WorldsManager.DeleteWorld(directory);
                m_sessionMemory.Remove(directory);   // 下载世界删掉 = 会话记忆一起作废
                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
                return false;
            }
        }

    }

}

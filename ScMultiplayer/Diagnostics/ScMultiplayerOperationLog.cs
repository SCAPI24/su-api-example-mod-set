using Engine;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScMultiplayer.Diagnostics
{
    /// <summary>
    /// ScMP 自己的**操作记录**：客户端写 `data:/Logs/Client/&lt;日期&gt;.log`（本地决策），
    /// 主机写 `data:/Logs/Server/ScMP-op-&lt;日期&gt;.log`（玩家操作）。
    ///
    /// 分工（与无头端约定）：
    ///   · **操作记录** = ScMP 自己写，就是本类；
    ///   · **同步失败**（挖掘请求被权威门拒绝、取不到射线等）= 交给无头端记录，
    ///     ScMP 只通过 `ModEventBus` 上抛 `ScMultiplayer.ServerAudit`，由 HeadlessRenderingMod
    ///     写进 `Logs/Server/&lt;日期&gt;.log`（见 ScMultiplayerDiagnosticSink.RecordHostSyncFailure）。
    ///
    /// 形态照这两处：
    ///   · `Survivalcraft/Game/GameLogSink.cs`（同样的 `data:/Logs` 根、`CreateOrOpen` + 追加 + 每次 Flush）
    ///   · `Mod/HeadlessRenderingMod/Server/ServerAuditLog.cs`（按日期分文件、放在 `Logs/Server` 下）
    /// 主机侧另起文件名：`Logs/Server/&lt;日期&gt;.log` 由无头端的 ServerAuditLog 常开句柄持有，
    /// 同文件二次打开会失败，而且那个目录受它的容量裁剪管理。
    ///
    /// ⚠️ 当前内容全部是挖掘回退定位用的**临时诊断**：定位结束后连调用点一起删除或收敛。
    /// </summary>
    internal static class ScMultiplayerOperationLog
    {
        private static readonly object s_gate = new object();
        private static Stream m_stream;
        private static StreamWriter m_writer;
        private static string m_openKey;

        /// <summary>写一行操作记录；按"主机 / 客户端"自动分流到 Logs/Server 或 Logs/Client。</summary>
        internal static void Write(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            try
            {
                lock (s_gate)
                {
                    string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    bool host = ScMultiplayer.IsHost;
                    string key = (host ? "S/" : "C/") + date;
                    if (m_writer == null || m_openKey != key)
                    {
                        m_writer?.Dispose();
                        m_stream?.Dispose();
                        m_writer = null;
                        m_stream = null;

                        string directory = Storage.CombinePaths("data:/Logs", host ? "Server" : "Client");
                        Storage.CreateDirectory(directory);
                        string path = Storage.CombinePaths(directory,
                            (host ? "ScMP-op-" : string.Empty) + date + ".log");
                        m_stream = Storage.OpenFile(path, OpenFileMode.CreateOrOpen);
                        m_stream.Position = m_stream.Length;
                        m_writer = new StreamWriter(m_stream, new UTF8Encoding(false));
                        m_openKey = key;
                    }

                    m_writer.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    m_writer.Write(' ');
                    m_writer.WriteLine(message.Replace('\r', ' ').Replace('\n', ' '));
                    m_writer.Flush();
                }
            }
            catch
            {
                // 记录写不进去就算了：绝不能让日志影响游戏。
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PlayerAiMod.Editor
{
    /// <summary>
    /// PlayerAiEditor —— 行为树/动作包的**低代码编辑器**（P2）。
    ///
    /// 形态（计划 §7）：浏览器 + 内嵌静态资源，**运行期零依赖**（不需要 node、不需要联网）；
    /// 所有静态页面/脚本都作为嵌入资源打进 exe，`PlayerAiEditor.exe` 一个文件就能跑。
    ///
    /// 它复用游戏内那份**纯逻辑层**（包格式 / 校验器 / 编译器 / 节点 schema），
    /// 所以"编辑器说能存 = 游戏说能跑"；保存走原子写，并且只允许写实例目录；
    /// 保存后可以一键让游戏热重载（走 CmdBridge 控制通道的 `ai.tree.notify`）。
    ///
    /// 用法（装进游戏目录里跑，自己判定实例根）：
    ///   &lt;游戏目录&gt;\PlayerAi\PlayerAiEditor.exe [--port 8760] [--no-browser]
    ///   &lt;游戏目录&gt;\PlayerAi\PlayerAiEditor.exe --selftest      # 无头自检 API
    /// 判定规则：实例根 = 本 exe 所在目录的**父目录**，且那层必须有 `Survivalcraft.exe`；
    /// 判定不到就**拒绝启动**（不绑端口、不开浏览器）。

    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string root = DefaultInstanceRoot();
            int port = 8760;
            bool openBrowser = true;
            bool selfTest = false;
            bool printRoot = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out port);
                        break;
                    case "--no-browser":
                        openBrowser = false;
                        break;
                    case "--print-root":
                        printRoot = true;
                        openBrowser = false;
                        break;
                    case "--selftest":
                        selfTest = true;
                        openBrowser = false;
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;
                }
            }

            root = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root);
            // 位置不对不许运行：本程序按约定只放在 <实例根>\PlayerAi\ 里跑。
            // 所以实例根 = 本 exe 所在目录的父目录，且那一层必须能探测到 Survivalcraft.exe。
            string gameExe = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Survivalcraft.exe");
            if (gameExe == null || !File.Exists(gameExe))
            {
                Console.WriteLine("[PlayerAiEditor] 拒绝启动：本程序要放在游戏目录里运行。");
                Console.WriteLine(@"  要求位置：<游戏目录>\PlayerAi\PlayerAiEditor.exe");
                Console.WriteLine("  判定规则：实例根 = 本 exe 所在目录的父目录，且那一层要有 Survivalcraft.exe。");
                Console.WriteLine("  本次判定的实例根：" + (string.IsNullOrEmpty(root) ? "(未找到)" : root));
                Console.WriteLine("  用法：把本 exe 放进 <游戏目录>\\PlayerAi\\ 之后再运行。");
                return 2;
            }
            var api = new EditorApi(root);

            // 只报路径就走：给"编辑器读不到包"这种情况一个一眼能看清的排查入口，
            // 构建脚本也用它来守住"自动判定的实例根必须是 publish/Windows"。
            if (printRoot)
            {
                Console.WriteLine("root      = " + root);
                Console.WriteLine("roots     = " + api.Roots.Describe());
                return 0;
            }

            if (selfTest)
                return EditorSelfTest.Run(api, root);

            var router = new EditorRouter(api);
            using (var server = new HttpServer(port, router.Handle))
            {
                server.Start();
                string url = "http://127.0.0.1:" + server.Port + "/";
                Console.WriteLine("PlayerAiEditor 已启动");
                Console.WriteLine("  实例根  : " + root);
                Console.WriteLine("  包目录  : " + api.Roots.Describe());
                Console.WriteLine("  地址    : " + url);
                Console.WriteLine("  （Ctrl+C 退出）");
                if (openBrowser)
                    TryOpenBrowser(url);

                var stop = new ManualResetEvent(false);
                Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    stop.Set();
                };
                stop.WaitOne();
                Console.WriteLine("再见。");
            }
            return 0;
        }

        private static string DefaultInstanceRoot()        {
            // 布局是固定的：本 exe 放在 <实例根>\PlayerAi\ 里（见 README §9.5.19），
            // 所以实例根 = **本 exe 所在目录的父目录**，只认这一级：
            // 不沿父链往上乱找，也**不"找不到就退回当前目录"**。
            // 判据只有一个：父目录那一层必须有 Survivalcraft.exe（游戏本体）。
            //
            // 起点留三个，只是为了单文件发布下 `AppContext.BaseDirectory` 可能指向解包临时目录
            // （`%TEMP%\.net\PlayerAiEditor\...`）时，还能用 `Environment.ProcessPath`（真正的 exe）。
            var starts = new List<string>();
            try
            {
                string processPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(processPath))
                    starts.Add(Path.GetDirectoryName(processPath));
            }
            catch (Exception)
            {
                // ProcessPath 拿不到就算了，后面还有 AppContext.BaseDirectory 与当前目录
            }
            starts.Add(AppContext.BaseDirectory);
            starts.Add(Directory.GetCurrentDirectory());

            foreach (string start in starts)
            {
                if (string.IsNullOrEmpty(start)) continue;
                string trimmed = start.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                DirectoryInfo parent = Directory.GetParent(trimmed);
                if (parent == null) continue;
                if (File.Exists(Path.Combine(parent.FullName, "Survivalcraft.exe")))
                    return parent.FullName;
            }

            // 判定不到就返回 null —— 由 Main 拒绝启动，不再退"当前目录"
            return null;
        }

        private static void TryOpenBrowser(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                Console.WriteLine("（自动打开浏览器失败：" + exception.Message + "，请手动访问上面的地址）");
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("PlayerAiEditor —— 行为树低代码编辑器");
            Console.WriteLine("  用法：把本 exe 放进 <游戏目录>\\PlayerAi\\ 后直接运行（它自己判定实例根）");
            Console.WriteLine("  --port <端口>       监听端口（默认 8760）");
            Console.WriteLine("  --no-browser        不自动打开浏览器");
            Console.WriteLine("  --selftest          无头自检 API（不需要浏览器）");
            Console.WriteLine("  --print-root        只打印自动判定出来的实例根就退出（排查「读不到包」）");
        }

        // ---------------------------------------------------------------- 静态资源

        /// <summary>
        /// 构建时间戳（页面上显示，用来确认"你跑的是哪一版"）。
        /// 取 exe 自己的写入时间 —— 单文件发布/开发期跑都能反映真实构建时刻。
        /// </summary>
        public static string BuildStamp
        {
            get
            {
                if (s_buildStamp != null)
                    return s_buildStamp;

                try
                {
                    string path = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    s_buildStamp = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
                }
                catch (Exception)
                {
                    s_buildStamp = "unknown";
                }
                return s_buildStamp;
            }
        }

        private static string s_buildStamp;

        /// <summary>读取内嵌的 Web 资源（`Web/index.html` → `PlayerAiMod.Editor.Web.index.html`）。</summary>
        public static string ReadEmbeddedAsset(string fileName)
        {
            Assembly assembly = typeof(Program).Assembly;
            string suffix = ".Web." + fileName.Replace('/', '.');
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream stream = assembly.GetManifestResourceStream(name))
                    {
                        if (stream == null)
                            continue;
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                            return reader.ReadToEnd();
                    }
                }
            }
            return null;
        }

        public static List<string> ListEmbeddedAssets()
        {
            var list = new List<string>();
            Assembly assembly = typeof(Program).Assembly;
            foreach (string name in assembly.GetManifestResourceNames())
                list.Add(name);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }
}

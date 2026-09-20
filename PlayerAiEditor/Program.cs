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
    /// 用法：
    ///   PlayerAiEditor.exe --root "&lt;游戏目录&gt;" [--port 8760] [--no-browser]
    ///   PlayerAiEditor.exe --selftest --root "&lt;游戏目录&gt;"     # 无头自检 API
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
                    case "--root":
                        if (i + 1 < args.Length) root = args[++i];
                        break;
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

            root = Path.GetFullPath(root ?? ".");
            // 实例根指错的后果是"一个包都读不到"，而界面上只会显示空列表，很难看出是路径问题。
            // 所以这里明确吼一声：实例根下得有 Mods/ 或 PlayerAi/。
            if (!Directory.Exists(Path.Combine(root, "Mods"))
                && !Directory.Exists(Path.Combine(root, "PlayerAi")))
            {
                Console.WriteLine("[PlayerAiEditor] 警告：实例根 \"" + root
                    + "\" 下既没有 Mods/ 也没有 PlayerAi/，多半读不到任何包。");
                Console.WriteLine("                        用 --root \"<游戏目录>\" 指定（例如 "
                    + "publish\\Windows）。");
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
            // 实例根 = "有 Mods/ 的那个目录"。找不到就让编辑器读不到任何包，
            // 所以这里多试几个起点，且**允许往上走若干级**，最后还有"兄弟目录"这一招。
            //
            // 起点为什么有三个：单文件发布下 `AppContext.BaseDirectory` 有可能是解包临时目录
            // （`%TEMP%\.net\PlayerAiEditor\...`），光信它会一路找错 —— 实测就是这样：
            // 从 publish/editor 启动，实例根被判成 publish/editor（没有 Mods/），包列表是空的。
            // `Environment.ProcessPath` 才是那个真正的 exe。
            //
            // 为什么要往上走：编辑器现在装在**实例根里面**（`<实例根>/PlayerAi/editor/`），
            // 于是 exe 目录、父目录都没有 Mods/，得走到祖父目录（`<实例根>`）才是游戏实例。
            var starts = new List<string>();
            string current = Directory.GetCurrentDirectory();
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
            starts.Add(current);

            // ① 沿"起点 → 父 → 祖父 …"往上找游戏实例，按"离起点近"优先：
            //    第 0 轮只认**同时有 Mods/ 与 PlayerAi/** 的目录（最像游戏实例）；
            //    第 1 轮放宽成"有 Mods/ 就行"（全新实例可能还没跑过 Mod、没有 PlayerAi/）。
            for (int pass = 0; pass < 2; pass++)
            {
                bool needPlayerAi = pass == 0;
                for (int level = 0; level < 6; level++)
                {
                    foreach (string start in starts)
                    {
                        string dir = AncestorDirectory(start, level);
                        if (dir == null) continue;
                        if (!Directory.Exists(Path.Combine(dir, "Mods"))) continue;
                        if (needPlayerAi && !Directory.Exists(Path.Combine(dir, "PlayerAi")))
                            continue;
                        return dir;
                    }
                }
            }

            // ② 兄弟目录带 Mods/：编辑器与游戏实例并排放在 publish/ 下时就是这种
            //    （publish/editor/PlayerAiEditor.exe + publish/Windows/<游戏实例>）。
            //    只在"恰好一个兄弟目录带 Mods/"时才采用，免得瞎猜
            //    （有歧义就退回当前目录，并在启动时吼一声警告）。
            foreach (string start in starts)
            {
                if (string.IsNullOrEmpty(start)) continue;
                DirectoryInfo parent = Directory.GetParent(start);
                if (parent == null) continue;
                string unique = null;
                bool ambiguous = false;
                foreach (string dir in Directory.GetDirectories(parent.FullName))
                {
                    if (!Directory.Exists(Path.Combine(dir, "Mods"))) continue;
                    if (unique == null) unique = dir;
                    else { ambiguous = true; break; }
                }
                if (unique != null && !ambiguous)
                    return unique;
            }

            return current;
        }

        /// <summary>往上走 <paramref name="levels"/> 级的祖先目录（0 = 自己；走不到返回 null）。</summary>
        private static string AncestorDirectory(string start, int levels)
        {
            if (string.IsNullOrEmpty(start))
                return null;
            try
            {
                DirectoryInfo dir = new DirectoryInfo(start);
                for (int i = 0; i < levels; i++)
                {
                    if (dir.Parent == null)
                        return null;
                    dir = dir.Parent;
                }
                return dir.FullName;
            }
            catch (Exception)
            {
                return null;
            }
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
            Console.WriteLine("  --root <游戏目录>   实例根（默认自动探测：含 Mods/ 的目录）");
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

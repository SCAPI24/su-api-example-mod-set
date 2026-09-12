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
            var api = new EditorApi(root);

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

        private static string DefaultInstanceRoot()
        {
            // 编辑器 exe 通常放在 publish/Windows 旁边或里面；优先用"当前目录里有 Mods/ 的那个"
            string current = Directory.GetCurrentDirectory();
            if (Directory.Exists(Path.Combine(current, "Mods")))
                return current;

            string baseDirectory = AppContext.BaseDirectory;
            if (Directory.Exists(Path.Combine(baseDirectory, "Mods")))
                return baseDirectory;

            DirectoryInfo parent = Directory.GetParent(baseDirectory);
            if (parent != null && Directory.Exists(Path.Combine(parent.FullName, "Mods")))
                return parent.FullName;

            return current;
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
        }

        // ---------------------------------------------------------------- 静态资源

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

using Engine;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace HeadlessRenderingMod
{
    internal static class HeadlessAudioFallback
    {
        private static IntPtr s_openAlHandle;
        private static IntPtr s_gles2Handle;
        private static IntPtr s_openglHandle;


        private static readonly HashSet<Assembly> s_resolverAssemblies =
            new HashSet<Assembly>();

        // Linux 空壳句柄（与 Windows 的 3 个字段并列，互不干扰）
        private static IntPtr s_linuxGbmHandle;
        private static IntPtr s_linuxDrmHandle;
        private static IntPtr s_linuxEglHandle;
        private static IntPtr s_linuxGlesHandle;
        private static IntPtr s_linuxInputHandle;
        private static IntPtr s_linuxLibcHandle;

        // Engine 的 Linux 原生库名映射表（来源：
        // Engine/Engine/SuAPILinuxPlatform.cs:SuAPILinuxPlatform.s_nativeLibraryMap）。
        // 我们抢先注册了解析器，Engine 那边会被跳过，所以这张表必须在这里复制一份，
        // 否则 libX11 / libc / openal32 等名字会因为"系统里没有同名文件"而加载失败。
        private static readonly Dictionary<string, string> s_linuxNativeLibraryMap =
            new(StringComparer.Ordinal)
            {
                ["libc"] = "libc.so.6",
                ["dl"] = "libdl.so.2",
                ["libX11"] = "libX11.so.6",
                ["libXi"] = "libXi.so.6",
                ["libXxf86vm"] = "libXxf86vm.so.1",
                ["libXinerama"] = "libXinerama.so.1",
                ["openal32.dll"] = "libopenal.so.1",
                ["libopenal.so"] = "libopenal.so.1",
                ["SDL2.dll"] = "libSDL2-2.0.so.0",
                ["libEGL.dll"] = "libEGL.so.1",
                ["libGLES.dll"] = "libGLESv2.so.2",
                ["opengl32.dll"] = "libGL.so.1",
                ["OPENGL32.DLL"] = "libGL.so.1",
                ["libinput"] = "libinput.so.10",
                ["gbm"] = "libgbm.so.1",
                ["libdrm"] = "libdrm.so.2",
                ["libudev"] = "libudev.so.1"
            };

        // 抽取出来的原生库用**固定文件名**：每次启动覆盖同一份 → 实例根目录长期只有这两个文件。
        // 早期实现把 Environment.ProcessId 拼进文件名（`*-headless-<pid>.dll`），每次启动都会新增
        // 两个文件（远端服务器实测累计 300 个 / 7.4 MB）；遗留文件由 CleanupLegacyExtracts 清掉。
        private const string Gles2LibraryFileName = "libGLESv2-headless.dll";
        private const string OpenglLibraryFileName = "opengl32-headless.dll";

        // ── Linux 版空壳库（用户 2026-11-08 定：和 Windows 一样，让"有没有 GPU 都能跑"）──
        //
        // Linux 上 OpenTK 的初始化链比 Windows 长得多，一共要替换 5 个系统库：
        //   libgbm   —— 打开 /dev/dri/card0 拿 GBM device（探测 GPU）
        //   libdrm   —— 枚举 DRM 显示器（OpenTK 靠它确认"能画"）
        //   libEGL   —— 创建 EGL display / context
        //   libGLESv2—— 真正的 GL 入口（89 个函数全部打空）
        //   libinput —— 键盘/鼠标设备（无头服务器不需要输入）
        // 每个都是独立编译的 .so，源码在 Native/null_*.c，编译方式见该目录 README。
        private const string LinuxGlesLibraryFileName = "libGLESv2-headless.so";
        private const string LinuxEglLibraryFileName = "libEGL-headless.so";
        private const string LinuxGbmLibraryFileName = "libgbm-headless.so";
        private const string LinuxDrmLibraryFileName = "libdrm-headless.so";
        private const string LinuxInputLibraryFileName = "libinput-headless.so";
        // libc 代理：只改写 open("/dev/dri/cardN") 返回 eventfd，其余转发真 libc。
        private const string LinuxLibcLibraryFileName = "libc-headless.so";

        // Source: Engine/Engine/Audio/Mixer.cs:Mixer.Initialize
        // Source: OpenTK/OpenTK.Graphics.ES20/GL.cs:GL.Core
        public static bool Ensure(string instanceRoot, bool disableAudio, bool disableDrawing)
        {
            // Windows 与 Linux 走两套完全不同的空壳（库格式与初始化链都不同），
            // 其它平台（macOS 等）暂不提供 —— 明确说明，不静默假装成功。
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                Log.Warning("[HeadlessRenderingMod] Native headless backends are only provided "
                    + "for Windows and Linux; this platform gets no effect.");
                return false;
            }

            bool usingNullGles2 = false;
            CleanupLegacyExtracts(instanceRoot);

            // Linux 走完全独立的一套：5 个空壳库 + ALSOFT null 后端，给完直接返回。
            if (OperatingSystem.IsLinux())
            {
                return EnsureLinux(instanceRoot, disableAudio, disableDrawing);
            }

            if (disableAudio)
            {
                string configPath = Path.Combine(instanceRoot, "alsoft-headless.ini");
                File.WriteAllText(configPath, "[general]\r\ndrivers = null\r\n");
                string libraryPath = Path.Combine(instanceRoot, "openal32.dll");
                if (!File.Exists(libraryPath))
                    throw new FileNotFoundException("Headless OpenAL library is missing.", libraryPath);
                if (s_openAlHandle == IntPtr.Zero)
                    s_openAlHandle = NativeLibrary.Load(libraryPath);
            }
            if (disableDrawing && s_gles2Handle == IntPtr.Zero)
            {
                s_gles2Handle = TryLoadExistingGles2(instanceRoot);
                if (s_gles2Handle == IntPtr.Zero)
                {
                    string libraryPath = ExtractEmbeddedLibrary(
                        "HeadlessRenderingMod.Native.libGLESv2.dll",
                        instanceRoot,
                        Gles2LibraryFileName);
                    s_gles2Handle = NativeLibrary.Load(libraryPath);
                    usingNullGles2 = true;
                }
            }
            if (disableDrawing && s_openglHandle == IntPtr.Zero)
            {
                string libraryPath = ExtractEmbeddedLibrary(
                    "HeadlessRenderingMod.Native.opengl32-headless.dll",
                    instanceRoot,
                    OpenglLibraryFileName);
                s_openglHandle = NativeLibrary.Load(libraryPath);
            }
            InstallResolver(Assembly.Load("OpenTK"));
            InstallResolver(typeof(Engine.Window).Assembly);
            if (disableAudio)
                Log.Information("[HeadlessRenderingMod] OpenAL null audio backend initialized.");
            if (disableDrawing)
            {
                Log.Information(usingNullGles2
                    ? "[HeadlessRenderingMod] Null GLES2 backend initialized."
                    : "[HeadlessRenderingMod] Existing GLES2 backend selected.");
                Log.Information(
                    "[HeadlessRenderingMod] OpenGL compatibility proxy initialized; " +
                    "real GL2 entry points are preferred when available.");
            }
            return usingNullGles2;
        }

        /// <summary>
        /// Linux 版空壳初始化。
        ///
        /// 与 Windows 的差别：Windows 只需换掉 `opengl32.dll` / `libGLESv2.dll` 两个文件，
        /// Linux 上 OpenTK 的初始化链要经过 **5 个**系统库，任何一个缺失都会让进程在
        /// `Program.Main → Window.Run` 直接退出：
        ///
        ///   1. libgbm    —— 打开 DRM 设备建 GBM device
        ///   2. libdrm    —— 枚举显示器（OpenTK 认为"没有显示器"就抛 PlatformNotSupportedException）
        ///   3. libEGL    —— EGL display / context / GetProcAddress
        ///   4. libGLESv2 —— 89 个 GL 入口全部打空
        ///   5. libinput  —— 键盘鼠标（输入线程，否则 Keyboard 静态构造抛 NotSupportedException）
        ///
        /// 关键约束：**不能自己装 DllImportResolver**。Engine 的
        /// `SuAPILinuxPlatform.EnsureNativeLibraryResolver()` 已经给每个程序集装过解析器了
        /// （重复设置会抛 InvalidOperationException），而它已经把 `libdrm` / `gbm` /
        /// `libEGL.dll` / `libGLES.dll` 映射到游戏目录里的对应 .so。所以这里只要把
        /// 空壳**按 Engine 期望的文件名**放在实例根目录即可。
        ///
        /// 文件名对照（左边是 Engine 映射表里的目标名，右边是我们提取的文件）：
        ///   libgbm.so.1      ← libgbm-headless.so
        ///   libdrm.so.2      ← libdrm-headless.so
        ///   libEGL.so.1      ← libEGL-headless.so
        ///   libGLESv2.so.2   ← libGLESv2-headless.so
        ///   libinput.so.10   ← libinput-headless.so
        /// </summary>
        private static bool EnsureLinux(string instanceRoot, bool disableAudio, bool disableDrawing)
        {
            if (disableAudio)
            {
                // OpenAL 在无声卡的机器上初始化失败会表现为
                // DllNotFoundException("openal32.dll")，很容易误判成"库没装"。
                //
                // 关键：**不能靠 ALSOFT_CONF 环境变量**。OpenAL 在库加载时就把配置读完了，
                // 等 mod 再 Environment.SetEnvironmentVariable 已经晚了（strace 实测：它根本没去打开该路径）。
                // OpenAL 的搜索顺序里有一个是 **游戏目录/alsoft.conf**，
                // 所以直接把配置写成这个名字就能被读到。
                string configPath = Path.Combine(instanceRoot, "alsoft.conf");
                File.WriteAllText(configPath, "[general]\r\ndrivers = null\r\n");
                Environment.SetEnvironmentVariable("ALSOFT_CONF", configPath);
                Log.Information("[HeadlessRenderingMod] OpenAL null audio backend selected: "
                    + configPath);
            }
            if (!disableDrawing)
                return false;

            // 与 Windows 完全同构：空壳库用**独立文件名**落盘（`*-headless.so`），
            // 绝不覆盖系统库（libgbm.so.1 / libEGL.so.1 …）。系统库原样不动，
            // 因此卸载本 Mod 后目录里这些 -headless 文件虽然残留，但没有任何东西会引用它们，
            // 游戏照常按系统库运行。
            s_linuxGbmHandle = LoadLinuxStub(instanceRoot,
                "HeadlessRenderingMod.Native.linux.libgbm-headless.so", LinuxGbmLibraryFileName);
            s_linuxDrmHandle = LoadLinuxStub(instanceRoot,
                "HeadlessRenderingMod.Native.linux.libdrm-headless.so", LinuxDrmLibraryFileName);
            s_linuxEglHandle = LoadLinuxStub(instanceRoot,
                "HeadlessRenderingMod.Native.linux.libEGL-headless.so", LinuxEglLibraryFileName);
            s_linuxGlesHandle = LoadLinuxStub(instanceRoot,
                "HeadlessRenderingMod.Native.linux.libGLESv2-headless.so", LinuxGlesLibraryFileName);
            s_linuxInputHandle = LoadLinuxStub(instanceRoot,
                "HeadlessRenderingMod.Native.linux.libinput-headless.so", LinuxInputLibraryFileName);
            // libc 代理必须**最后**加载：它要能 dlsym 到真 libc 的 open 做转发。
            s_linuxLibcHandle = LoadLinuxStub(instanceRoot,
                "HeadlessRenderingMod.Native.linux.libc-headless.so", LinuxLibcLibraryFileName);

            // 抢在 Engine 之前注册解析器（Mod 在 Program.Main 的 LoadMods 阶段执行，
            // 早于 Window 静态构造里的 SuAPILinuxPlatform.EnsureNativeLibraryResolver）。
            // Engine 那边随后会拿到 InvalidOperationException 并静默保留我们的解析器。
            InstallResolver(Assembly.Load("OpenTK"));
            InstallResolver(typeof(Engine.Window).Assembly);

            Log.Information(
                "[HeadlessRenderingMod] Linux no-GPU backends installed " +
                "(gbm/drm/EGL/GLESv2/libinput); all OpenGL rendering is a no-op.");
            return true;
        }

        /// <summary>
        /// 把一个 Linux 空壳 .so 抽到实例根目录并用独立文件名加载。
        /// 返回加载到的原生句柄；抽不出来或加载失败时返回 IntPtr.Zero 并记日志。
        /// </summary>
        private static IntPtr LoadLinuxStub(
            string instanceRoot,
            string resourceName,
            string fileName)
        {
            string libraryPath = ExtractEmbeddedLibrary(resourceName, instanceRoot, fileName);
            try
            {
                // Linux 的 dlopen 要求文件有执行位。
                File.SetUnixFileMode(libraryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception error)
            {
                Log.Warning("[HeadlessRenderingMod] Failed to mark " + fileName +
                    " executable: " + error.GetType().Name + ": " + error.Message);
            }
            try
            {
                return NativeLibrary.Load(libraryPath);
            }
            catch (Exception error)
            {
                Log.Warning("[HeadlessRenderingMod] Failed to load Linux stub " + fileName +
                    ": " + error.GetType().Name + ": " + error.Message);
                return IntPtr.Zero;
            }
        }

        private static void InstallResolver(Assembly assembly)
        {
            if (assembly == null || !s_resolverAssemblies.Add(assembly))
                return;
            NativeLibrary.SetDllImportResolver(assembly, ResolveOpenTkLibrary);
        }

        /// <summary>
        /// 把内置原生库抽到实例根目录并返回最终路径。
        /// 优先用固定名（同名覆盖 → 长期只留一份）；如果同机已有实例把该文件载入并锁住
        ///（Windows 不允许覆盖已映射的 DLL），退回本进程专属的文件名，保证多实例也能启动。
        /// </summary>
        private static string ExtractEmbeddedLibrary(
            string resourceName,
            string instanceRoot,
            string fileName)
        {
            string libraryPath = Path.Combine(instanceRoot, fileName);
            try
            {
                WriteEmbeddedLibrary(resourceName, libraryPath);
                return libraryPath;
            }
            catch (IOException)
            {
                // 被别的实例锁住 → 走下面的本进程副本
            }
            catch (UnauthorizedAccessException)
            {
                // 同上，权限异常也退回副本
            }
            string fallbackPath = Path.Combine(
                instanceRoot,
                Path.GetFileNameWithoutExtension(fileName) +
                "-" + Environment.ProcessId +
                Path.GetExtension(fileName));
            WriteEmbeddedLibrary(resourceName, fallbackPath);
            return fallbackPath;
        }

        private static void WriteEmbeddedLibrary(string resourceName, string libraryPath)
        {
            using (Stream resource = typeof(HeadlessAudioFallback).Assembly
                .GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException(
                    "Embedded native resource is missing.",
                    resourceName))
            using (FileStream output = new FileStream(
                libraryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                resource.CopyTo(output);
            }
        }

        // 清理早期 `*-headless-<pid>.dll` 命名的遗留文件：固定名的两个文件不含 `-` 尾缀，
        // 不在匹配范围内。删不掉的（被其它实例锁住）跳过，下次启动再收。
        private static void CleanupLegacyExtracts(string instanceRoot)
        {
            try
            {
                foreach (string legacy in Directory.EnumerateFiles(
                    instanceRoot, "*-headless-*.dll", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(legacy); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static IntPtr TryLoadExistingGles2(string instanceRoot)
        {
            string localPath = Path.Combine(instanceRoot, "libGLESv2.dll");
            if (File.Exists(localPath) && TryLoadValidated(localPath, out IntPtr handle))
                return handle;

            if (TryLoadValidated("libGLESv2.dll", out handle))
                return handle;

            return IntPtr.Zero;
        }

        private static bool TryLoadValidated(string libraryName, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (!NativeLibrary.TryLoad(libraryName, out handle))
                return false;

            try
            {
                NativeLibrary.GetExport(handle, "glGetString");
                NativeLibrary.GetExport(handle, "glGenBuffers");
                NativeLibrary.GetExport(handle, "glCreateShader");
                return true;
            }
            catch
            {
                NativeLibrary.Free(handle);
                handle = IntPtr.Zero;
                return false;
            }
        }

        private static IntPtr ResolveOpenTkLibrary(
            string libraryName,
            Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            if (OperatingSystem.IsWindows())
                return ResolveWindowsLibrary(libraryName);
            if (OperatingSystem.IsLinux())
                return ResolveLinuxLibrary(libraryName, assembly, searchPath);
            return IntPtr.Zero;
        }

        /// <summary>Windows：只接管三个空壳（与改造前完全一致）。</summary>
        private static IntPtr ResolveWindowsLibrary(string libraryName)
        {
            string fileName = Path.GetFileName(libraryName);
            if (string.Equals(fileName, "openal32.dll", StringComparison.OrdinalIgnoreCase))
                return s_openAlHandle;
            if (string.Equals(fileName, "libGLESv2.dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "libGLESv2", StringComparison.OrdinalIgnoreCase))
                return s_gles2Handle;
            if (string.Equals(fileName, "opengl32.dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "opengl32", StringComparison.OrdinalIgnoreCase))
                return s_openglHandle;
            return IntPtr.Zero;
        }

        /// <summary>
        /// Linux：先接管 5 个空壳库，其余库名按 Engine 的映射表转发（我们自己加载）。
        ///
        /// 为什么"其余库名"也要在这里处理：Mod 在 Program.Main 的 LoadMods 阶段就注册了
        /// 解析器（早于 Window 静态构造里的 SuAPILinuxPlatform.EnsureNativeLibraryResolver），
        /// Engine 随后拿到 InvalidOperationException 会静默保留我们的解析器 —— 也就是说
        /// **Engine 的映射表不会再被使用**。OpenTK 里还有 libX11 / libc / openal32 等大量
        /// 裸名 P/Invoke，系统里没有同名文件（例如只有 libX11.so.6 而没有 libX11.so），
        /// 所以必须在这里按 Engine 的表把它们映射到位，否则会 DllNotFoundException。
        /// </summary>
        private static IntPtr ResolveLinuxLibrary(
            string libraryName,
            Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            // libc 代理：OpenTK 用它 open("/dev/dri/cardN") 拿 DRM 设备 fd，
            // 我们必须让它拿到 eventfd（否则 poll(fd,1,-1) 永久阻塞，见 null_libc.c 顶部说明）。
            // 只接管 OpenTK 程序集的请求；.NET 运行时自身的 libc 调用不受影响。
            if (s_linuxLibcHandle != IntPtr.Zero &&
                string.Equals(libraryName, "libc", StringComparison.Ordinal))
            {
                return s_linuxLibcHandle;
            }

            // Engine 的映射表键名是精确匹配（StringComparer.Ordinal），这里保持一致。
            if (s_linuxNativeLibraryMap.TryGetValue(libraryName, out string targetName))
            {
                // 5 个空壳库：名字先在表里映射成系统库名（libgbm.so.1 等），
                // 但真正要返回的是我们自己加载的空壳句柄。用映射后的名字判断更直观，
                // 也比逐个比较别名单更不容易漏。
                IntPtr stub = MatchLinuxStub(targetName);
                if (stub != IntPtr.Zero)
                    return stub;

                // 发包自带优先：Linux 发行包自带 libopenal.so.1 等，先查游戏目录副本。
                try
                {
                    string baseDirectory = AppContext.BaseDirectory;
                    if (!string.IsNullOrEmpty(baseDirectory))
                    {
                        string localPath = Path.Combine(baseDirectory, targetName);
                        if (File.Exists(localPath) &&
                            NativeLibrary.TryLoad(localPath, out IntPtr localHandle))
                        {
                            return localHandle;
                        }
                    }
                }
                catch (Exception)
                {
                }
                try
                {
                    return NativeLibrary.Load(targetName, assembly, searchPath);
                }
                catch (Exception error)
                {
                    // 不能静默：拿不到库必须能在日志里直接定位。
                    Log.Warning("[HeadlessRenderingMod] Failed to resolve native library '" +
                        libraryName + "' -> '" + targetName + "': " +
                        error.GetType().Name + ": " + error.Message);
                    return IntPtr.Zero;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>把 Engine 映射表里的目标名对到我们加载的空壳句柄。</summary>
        private static IntPtr MatchLinuxStub(string targetName)
        {
            switch (targetName)
            {
                case "libgbm.so.1": return s_linuxGbmHandle;
                case "libdrm.so.2": return s_linuxDrmHandle;
                case "libEGL.so.1": return s_linuxEglHandle;
                case "libGLESv2.so.2": return s_linuxGlesHandle;
                case "libinput.so.10": return s_linuxInputHandle;
                default: return IntPtr.Zero;
            }
        }
    }
}

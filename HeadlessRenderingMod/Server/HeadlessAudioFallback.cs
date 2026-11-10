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

        // Source: SuAPILinuxPlatform.cs:ResolveNativeLibrary — process-lifetime native mappings.
        // Never rewrite an ELF after dlopen, including retries and Mod unload/reload.
        private static readonly Dictionary<string, IntPtr> s_linuxLoadedLibraries = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> s_linuxInstalledMappings = new(StringComparer.Ordinal);
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

        // Source: SuAPILinuxPlatform.cs:EnsureNativeLibraryResolver/ResolveNativeLibrary.
        // SuAPICore can initialize Window before external Mods. Extend the existing Engine
        // resolver's exact SDK map instead of attempting to register a second resolver.
        private static bool EnsureLinux(string instanceRoot, bool disableAudio, bool disableDrawing)
        {
            if (disableAudio)
            {
                string configPath = Path.Combine(instanceRoot, "alsoft.conf");
                File.WriteAllText(configPath, "[general]\r\ndrivers = null\r\n");
                Environment.SetEnvironmentVariable("ALSOFT_CONF", configPath);
                Log.Information("[HeadlessRenderingMod] OpenAL null audio backend selected: " + configPath);
            }
            if (!disableDrawing) return false;

            var manager = SuAPI.ModManager.Instance
                ?? throw new InvalidOperationException("[HeadlessRenderingMod] SuAPI ModManager is unavailable.");
            var map = manager.ModParentField.GetStaticField<Dictionary<string, string>>(
                typeof(SuAPILinuxPlatform), "s_nativeLibraryMap")
                ?? throw new InvalidOperationException("[HeadlessRenderingMod] Engine native library map is unavailable.");
            var libraries = new[]
            {
                (Alias: "gbm", Original: "libgbm.so.1", File: LinuxGbmLibraryFileName),
                (Alias: "libdrm", Original: "libdrm.so.2", File: LinuxDrmLibraryFileName),
                (Alias: "libEGL.dll", Original: "libEGL.so.1", File: LinuxEglLibraryFileName),
                (Alias: "libGLES.dll", Original: "libGLESv2.so.2", File: LinuxGlesLibraryFileName),
                (Alias: "libinput", Original: "libinput.so.10", File: LinuxInputLibraryFileName),
                // Source: Native/null_libc.c:open — DRM device polling must use the eventfd proxy.
                (Alias: "libc", Original: "libc.so.6", File: LinuxLibcLibraryFileName)
            };
            lock (s_linuxLoadedLibraries)
            {
                // Validate every alias before extracting or mutating any mapping. A third-party
                // mapping is incompatible; do not silently override it or use a different backend.
                foreach (var library in libraries)
                {
                    string path = Path.GetFullPath(Path.Combine(instanceRoot, library.File));
                    string expected = s_linuxInstalledMappings.TryGetValue(library.Alias, out string installed)
                        ? installed : library.Original;
                    if (installed != null && !string.Equals(installed, path, StringComparison.Ordinal))
                        throw new InvalidOperationException("[HeadlessRenderingMod] Linux native backend is already bound to another instance root: " + installed);
                    if (!map.TryGetValue(library.Alias, out string actual) ||
                        !string.Equals(actual, expected, StringComparison.Ordinal))
                        throw new InvalidOperationException("[HeadlessRenderingMod] Incompatible Engine native mapping for '" +
                            library.Alias + "': expected '" + expected + "', actual '" + actual + "'.");
                }
                // libc must load last so the proxy can resolve and forward the real libc open.
                foreach (var library in libraries)
                    LoadLinuxStub(instanceRoot, library.File);
                foreach (var library in libraries)
                {
                    string path = Path.GetFullPath(Path.Combine(instanceRoot, library.File));
                    map[library.Alias] = path;
                    s_linuxInstalledMappings[library.Alias] = path;
                }
                SuAPILinuxPlatform.EnsureNativeLibraryResolver();
            }
            HeadlessLinuxDisplay.Ensure();
            Log.Information("[HeadlessRenderingMod] Linux no-GPU backends installed through the Engine native resolver.");
            return true;
        }

        // Source: Native/null_*.c; NativeLibrary.Load — a missing or invalid stub must fail startup.
        // Handles deliberately remain loaded for the process lifetime; Engine owns the resolver.
        private static void LoadLinuxStub(string instanceRoot, string fileName)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Linux headless libraries require Linux.");
            string libraryPath = Path.GetFullPath(Path.Combine(instanceRoot, fileName));
            if (s_linuxLoadedLibraries.ContainsKey(libraryPath)) return;
            WriteEmbeddedLibrary("HeadlessRenderingMod.Native.linux." + fileName, libraryPath);
            File.SetUnixFileMode(libraryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            IntPtr handle = NativeLibrary.Load(libraryPath);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("[HeadlessRenderingMod] Loading Linux stub returned a null handle: " + libraryPath);
            s_linuxLoadedLibraries.Add(libraryPath, handle);
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

    }
}

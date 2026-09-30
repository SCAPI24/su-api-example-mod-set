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

        // 抽取出来的原生库用**固定文件名**：每次启动覆盖同一份 → 实例根目录长期只有这两个文件。
        // 早期实现把 Environment.ProcessId 拼进文件名（`*-headless-<pid>.dll`），每次启动都会新增
        // 两个文件（远端服务器实测累计 300 个 / 7.4 MB）；遗留文件由 CleanupLegacyExtracts 清掉。
        private const string Gles2LibraryFileName = "libGLESv2-headless.dll";
        private const string OpenglLibraryFileName = "opengl32-headless.dll";

        // Source: Engine/Engine/Audio/Mixer.cs:Mixer.Initialize
        // Source: OpenTK/OpenTK.Graphics.ES20/GL.cs:GL.Core
        public static bool Ensure(string instanceRoot, bool disableAudio, bool disableDrawing)
        {
            if (!OperatingSystem.IsWindows()) return false;

            bool usingNullGles2 = false;
            CleanupLegacyExtracts(instanceRoot);

            if (disableAudio)
            {
                string configPath = Path.Combine(instanceRoot, "alsoft-headless.ini");
                File.WriteAllText(configPath, "[general]\r\ndrivers = null\r\n");
                Environment.SetEnvironmentVariable("ALSOFT_CONF", configPath);
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

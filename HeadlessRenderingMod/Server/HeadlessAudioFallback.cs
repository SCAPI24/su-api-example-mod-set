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

        // Source: Engine/Engine/Audio/Mixer.cs:Mixer.Initialize
        // Source: OpenTK/OpenTK.Graphics.ES20/GL.cs:GL.Core
        public static bool Ensure(string instanceRoot, bool disableAudio, bool disableDrawing)
        {
            if (!OperatingSystem.IsWindows()) return false;

            bool usingNullGles2 = false;

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
                    string libraryPath = Path.Combine(
                        instanceRoot,
                        $"libGLESv2-headless-{Environment.ProcessId}.dll");
                    using (Stream resource = typeof(HeadlessAudioFallback).Assembly
                        .GetManifestResourceStream("HeadlessRenderingMod.Native.libGLESv2.dll")
                        ?? throw new FileNotFoundException("Headless GLES2 resource is missing."))
                    using (FileStream output = new FileStream(
                        libraryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        resource.CopyTo(output);
                    }
                    s_gles2Handle = NativeLibrary.Load(libraryPath);
                    usingNullGles2 = true;
                }
            }
            if (disableDrawing && s_openglHandle == IntPtr.Zero)
            {
                string libraryPath = Path.Combine(
                    instanceRoot,
                    $"opengl32-headless-{Environment.ProcessId}.dll");
                ExtractEmbeddedLibrary(
                    "HeadlessRenderingMod.Native.opengl32-headless.dll",
                    libraryPath);
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

        private static void ExtractEmbeddedLibrary(string resourceName, string libraryPath)
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

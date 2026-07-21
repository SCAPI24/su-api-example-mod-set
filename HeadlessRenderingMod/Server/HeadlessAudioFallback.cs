using Engine;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HeadlessRenderingMod
{
    internal static class HeadlessAudioFallback
    {
        private static IntPtr s_openAlHandle;
        private static bool s_resolverInstalled;

        // Source: Engine/Engine/Audio/Mixer.cs:Mixer.Initialize
        public static void Ensure(string instanceRoot)
        {
            if (!OperatingSystem.IsWindows()) return;

            string configPath = Path.Combine(instanceRoot, "alsoft-headless.ini");
            File.WriteAllText(configPath, "[general]\r\ndrivers = null\r\n");
            Environment.SetEnvironmentVariable("ALSOFT_CONF", configPath);
            string libraryPath = Path.Combine(instanceRoot, "openal32.dll");
            if (!File.Exists(libraryPath))
                throw new FileNotFoundException("Headless OpenAL library is missing.", libraryPath);
            if (s_openAlHandle == IntPtr.Zero)
                s_openAlHandle = NativeLibrary.Load(libraryPath);
            if (!s_resolverInstalled)
            {
                Assembly openTkAssembly = Assembly.Load("OpenTK");
                NativeLibrary.SetDllImportResolver(openTkAssembly, ResolveOpenTkLibrary);
                s_resolverInstalled = true;
            }
            Log.Information(
                "[HeadlessRenderingMod] OpenAL null audio backend initialized.");
        }

        private static IntPtr ResolveOpenTkLibrary(
            string libraryName,
            Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            return string.Equals(
                Path.GetFileName(libraryName),
                "openal32.dll",
                StringComparison.OrdinalIgnoreCase)
                ? s_openAlHandle
                : IntPtr.Zero;
        }
    }
}

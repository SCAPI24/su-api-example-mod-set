using Engine;
using SuAPI;
using System;
using System.Reflection;

namespace HeadlessRenderingMod
{
    internal static class HeadlessLinuxDisplay
    {
        private static object s_ownedFactory;
        private static int s_fd;
        private static IntPtr s_gbm;
        private static IntPtr s_display;
        // Source: OpenTK.dll:OpenTK.Platform.Linux.LinuxFactory.gpu_fd/SetupDisplay
        // Seed the real factory's lazy display state with our native headless backend.
        // The ordinary CreateDisplay path enumerates /dev/dri before calling the stub.
        // This backend owns an eventfd instead, so it must never enumerate GPU nodes.
        public static void Ensure()
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("[HeadlessRenderingMod] Linux display bootstrap requires Linux.");

            SuAPILinuxPlatform.InitToolkit();
            Assembly openTk = Assembly.Load("OpenTK");
            Type factoryType = openTk.GetType("OpenTK.Platform.Factory", throwOnError: true);
            PropertyInfo defaultProperty = factoryType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMemberException(factoryType.FullName, "Default");
            object factory = defaultProperty.GetValue(null)
                ?? throw new InvalidOperationException("[HeadlessRenderingMod] OpenTK has no default platform factory.");
            if (factory.GetType().FullName != "OpenTK.Platform.Linux.LinuxFactory")
                throw new InvalidOperationException("[HeadlessRenderingMod] Expected OpenTK LinuxFactory, found "
                    + factory.GetType().FullName + ".");

            IModParentField fields = ModManager.Instance?.ModParentField
                ?? throw new InvalidOperationException("[HeadlessRenderingMod] SuAPI parent-field service is unavailable.");
            lock (factory)
            {
                int currentFd = fields.GetParentField<int>(factory, "_fd");
                IntPtr currentGbm = fields.GetParentField<IntPtr>(factory, "gbm_device");
                IntPtr currentDisplay = fields.GetParentField<IntPtr>(factory, "egl_display");
                if (ReferenceEquals(factory, s_ownedFactory))
                {
                    if (currentFd == s_fd && currentGbm == s_gbm && currentDisplay == s_display)
                        return;
                    throw new InvalidOperationException("[HeadlessRenderingMod] Owned Linux display state changed unexpectedly.");
                }
                if (s_ownedFactory != null || currentFd != 0 || currentGbm != IntPtr.Zero || currentDisplay != IntPtr.Zero)
                    throw new InvalidOperationException("[HeadlessRenderingMod] Linux display factory was already initialized; refusing to replace another backend.");

                // Source: OpenTK.dll:OpenTK.Platform.Linux.LinuxFactory.SetupDisplay
                // Source: Native/null_libc.c:open/is_drm_device_path
                // The proxy intercepts this virtual device name before any OS open.
                // Reuse OpenTK's exact GBM/EGL setup and validation; no filesystem
                // entry is created, and /dev/dri is never enumerated.
                IModParentMethod methods = ModManager.Instance.ModParentMethod
                    ?? throw new InvalidOperationException("[HeadlessRenderingMod] SuAPI parent-method service is unavailable.");
                Type pointerByRef = typeof(IntPtr).MakeByRefType();
                MethodInfo setup = methods.GetStaticMethodInfo(factory.GetType(), "SetupDisplay",
                    new[] { typeof(string), pointerByRef, pointerByRef })
                    ?? throw new MissingMethodException(factory.GetType().FullName, "SetupDisplay");
                object[] arguments = { "/dev/dri/card-headless", IntPtr.Zero, IntPtr.Zero };
                int fd;
                try
                {
                    fd = (int)setup.Invoke(null, arguments);
                }
                catch (TargetInvocationException error)
                {
                    throw new InvalidOperationException("[HeadlessRenderingMod] OpenTK headless display setup failed.",
                        error.InnerException ?? error);
                }
                IntPtr gbm = (IntPtr)arguments[1];
                IntPtr display = (IntPtr)arguments[2];
                if (fd <= 0 || gbm == IntPtr.Zero || display == IntPtr.Zero)
                    throw new InvalidOperationException("[HeadlessRenderingMod] OpenTK headless setup returned invalid fd/GBM/EGL state: "
                        + fd + "/" + gbm + "/" + display + ".");

                fields.ModifyParentField(factory, "gbm_device", gbm);
                fields.ModifyParentField(factory, "egl_display", display);
                // Publish _fd last: gpu_fd treats zero as not initialized.
                fields.ModifyParentField(factory, "_fd", fd);
                s_fd = fd;
                s_gbm = gbm;
                s_display = display;
                s_ownedFactory = factory;
                Log.Information("[HeadlessRenderingMod] Linux headless display initialized without GPU enumeration.");
            }
        }

    }
}

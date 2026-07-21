using Engine;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace HeadlessRenderingMod
{
    internal static class HeadlessDisplayDeviceFallback
    {
        // Source: Engine/Engine/Window.cs:Window.ScreenSize
        // Source: OpenTK/OpenTK/DisplayDevice.cs:DisplayDevice.Default
        public static void Ensure()
        {
            if (!OperatingSystem.IsWindows()) return;

            Assembly openTkAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(
                    assembly.GetName().Name,
                    "OpenTK",
                    StringComparison.Ordinal)) ?? Assembly.Load("OpenTK");
            Type displayDeviceType = openTkAssembly?.GetType("OpenTK.DisplayDevice");
            Type displayResolutionType = openTkAssembly?.GetType("OpenTK.DisplayResolution");
            if (displayDeviceType == null || displayResolutionType == null)
                throw new InvalidOperationException("OpenTK display types are unavailable.");

            PropertyInfo defaultProperty = displayDeviceType.GetProperty(
                "Default",
                BindingFlags.Public | BindingFlags.Static);
            if (defaultProperty?.GetValue(null) != null) return;

            FieldInfo implementationField = displayDeviceType.GetField(
                "implementation",
                BindingFlags.NonPublic | BindingFlags.Static);
            object implementation = implementationField?.GetValue(null);
            if (implementation == null)
                throw new InvalidOperationException("OpenTK display driver is unavailable.");

            ConstructorInfo resolutionConstructor = displayResolutionType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(constructor => constructor.GetParameters().Length == 6);
            object resolution = resolutionConstructor?.Invoke(
                new object[] { 0, 0, 1280, 720, 32, 60f });
            if (resolution == null)
                throw new InvalidOperationException("Cannot create a headless display resolution.");

            ConstructorInfo displayConstructor = displayDeviceType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(constructor => constructor.GetParameters().Length == 5);
            if (displayConstructor == null)
                throw new InvalidOperationException("Cannot find the OpenTK display constructor.");

            ParameterInfo[] parameters = displayConstructor.GetParameters();
            Type boundsType = parameters[3].ParameterType;
            object bounds = Activator.CreateInstance(boundsType, 0, 0, 1280, 720);
            Array resolutions = Array.CreateInstance(displayResolutionType, 1);
            resolutions.SetValue(resolution, 0);
            object display = displayConstructor.Invoke(
                new object[] { resolution, true, resolutions, bounds, null });

            FieldInfo primaryField = FindInstanceField(implementation.GetType(), "Primary");
            if (primaryField == null)
                throw new InvalidOperationException("OpenTK display driver has no primary display field.");
            primaryField.SetValue(implementation, display);

            FieldInfo devicesField = FindInstanceField(
                implementation.GetType(),
                "AvailableDevices");
            if (devicesField?.GetValue(implementation) is IList devices &&
                !devices.Contains(display))
            {
                devices.Add(display);
            }

            if (defaultProperty.GetValue(null) == null)
                throw new InvalidOperationException("Headless OpenTK display fallback was not accepted.");
            Log.Information(
                "[HeadlessRenderingMod] Installed 1280x720 OpenTK display metadata fallback.");
        }

        private static FieldInfo FindInstanceField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                    BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }
    }
}

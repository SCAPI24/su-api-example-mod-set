using Game;
using System;
using System.Reflection;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public class SuSubsystemWeather : SubsystemWeather
    {
        private bool m_clientPersistentTerrainHandlersDisabled;

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.Load
        // The client keeps weather presentation, but persistent freeze/thaw and snow deposition
        // must be produced only by the host and arrive through authoritative terrain updates.
        internal void DisableClientPersistentTerrainHandlers()
        {
            if (m_clientPersistentTerrainHandlersDisabled ||
                ScMultiplayer.currentInstance?.IsNetworkSessionActive(Project) != true ||
                ScMultiplayer.currentInstance?.IsNetworkHost(Project) == true)
                return;

            SubsystemBlocksScanner scanner = GetPrivateField<SubsystemBlocksScanner>(
                "m_subsystemBlocksScanner");
            bool scannerDisabled = RemoveHandlers(scanner, "ScanningChunkCompleted");
            bool chunkInitializationDisabled = RemoveHandlers(
                SubsystemTerrain?.TerrainUpdater, "ChunkInitialized");
            // Do not silently mark the client authority guard as installed when load order or
            // reflection prevented either native weather callback from being removed. The terrain
            // update calls this method again next frame until both callbacks are verified absent.
            m_clientPersistentTerrainHandlersDisabled =
                scannerDisabled && chunkInitializationDisabled;
        }

        private T GetPrivateField<T>(string name) where T : class
        {
            return typeof(SubsystemWeather).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(this) as T;
        }

        private bool RemoveHandlers(object owner, string fieldName)
        {
            if (owner == null)
                return false;
            FieldInfo field = owner.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                return false;
            if (field.GetValue(owner) is not Delegate handlers)
                return true;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                if (IsPersistentWeatherHandler(handler))
                {
                    field.SetValue(owner, Delegate.Remove(
                        field.GetValue(owner) as Delegate, handler));
                }
            }
            if (field.GetValue(owner) is not Delegate remainingHandlers)
                return true;
            foreach (Delegate handler in remainingHandlers.GetInvocationList())
            {
                if (IsPersistentWeatherHandler(handler))
                    return false;
            }
            return true;
        }

        private bool IsPersistentWeatherHandler(Delegate handler)
        {
            Type declaringType = handler?.Method?.DeclaringType;
            return ReferenceEquals(handler?.Target, this) ||
                declaringType == typeof(SubsystemWeather) ||
                declaringType?.FullName?.StartsWith(
                    typeof(SubsystemWeather).FullName + "+", StringComparison.Ordinal) == true;
        }
    }
}

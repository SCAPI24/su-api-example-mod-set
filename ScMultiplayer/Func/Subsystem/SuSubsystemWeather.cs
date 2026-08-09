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
                ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
                return;

            SubsystemBlocksScanner scanner = GetPrivateField<SubsystemBlocksScanner>(
                "m_subsystemBlocksScanner");
            RemoveHandlers(scanner, "ScanningChunkCompleted");
            if (SubsystemTerrain?.TerrainUpdater != null)
                RemoveHandlers(SubsystemTerrain.TerrainUpdater, "ChunkInitialized");
            m_clientPersistentTerrainHandlersDisabled = true;
        }

        private T GetPrivateField<T>(string name) where T : class
        {
            return typeof(SubsystemWeather).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(this) as T;
        }

        private void RemoveHandlers(object owner, string fieldName)
        {
            if (owner == null)
                return;
            FieldInfo field = owner.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(owner) is not Delegate handlers)
                return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                Type declaringType = handler.Method.DeclaringType;
                if (ReferenceEquals(handler.Target, this) ||
                    declaringType == typeof(SubsystemWeather) ||
                    declaringType?.FullName?.StartsWith(
                        typeof(SubsystemWeather).FullName + "+", StringComparison.Ordinal) == true)
                {
                    field.SetValue(owner, Delegate.Remove(
                        field.GetValue(owner) as Delegate, handler));
                }
            }
        }
    }
}

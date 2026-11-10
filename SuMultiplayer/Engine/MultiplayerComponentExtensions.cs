using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Game;
using GameEntitySystem;
using SuAPI;

namespace SuMultiplayer.Engine
{
    // Source: EntityRequestInput.cs; SuComponentGui.cs; NetworkAvatarSpawner.cs:TryCollectLocalPlayers
    // This cache belongs to one Mod instance. Construction can precede Attached without reading Host state.
    public sealed class MultiplayerComponentExtensions : IDisposable
    {
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly Func<bool> m_isMultiplayerActive;
        private readonly Action<Project> m_prepareInput;
        private readonly Action<Project> m_prepareGui;
        private readonly ModFieldRef<ComponentInput, PlayerInput> m_input;
        private readonly ConditionalWeakTable<ComponentInput, SuComponentInputExtension> m_inputs = new();
        private readonly ConditionalWeakTable<ComponentGui, SuComponentGuiExtension> m_guis = new();
        private readonly List<PlayerData> m_locals = new List<PlayerData>();
        private UpdateExtensionToken m_inputToken;
        private UpdateExtensionToken m_guiToken;
        private bool m_enabled;
        private bool m_disposed;

        public MultiplayerComponentExtensions(NetworkAvatarSpawner spawner, IModParentField parentField,
            Func<bool> isMultiplayerActive, Action<Project> prepareInput, Action<Project> prepareGui)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            if (parentField == null) throw new ArgumentNullException(nameof(parentField));
            m_input = parentField.BindFieldRef<ComponentInput, PlayerInput>("m_playerInput");
            m_isMultiplayerActive = isMultiplayerActive ?? throw new ArgumentNullException(nameof(isMultiplayerActive));
            m_prepareInput = prepareInput ?? throw new ArgumentNullException(nameof(prepareInput));
            m_prepareGui = prepareGui ?? throw new ArgumentNullException(nameof(prepareGui));
        }

        // Source: Survivalcraft/SuAPI/UpdateExtensionRegistry.cs:RegisterUpdateExtension
        public void Register(IModEventBus eventBus)
        {
            if (m_disposed || m_enabled) throw new InvalidOperationException("Multiplayer component extensions already registered or disposed.");
            m_inputToken = eventBus.RegisterUpdateExtension<ComponentInput>("SuMultiplayer.Input",
                host => GetInput(host), EventPriority.LOWEST);
            try
            {
                m_guiToken = eventBus.RegisterUpdateExtension<ComponentGui>("SuMultiplayer.Gui",
                    host => GetGui(host), EventPriority.HIGHEST);
                m_enabled = true;
            }
            catch { m_inputToken.Dispose(); m_inputToken = null; throw; }
        }

        public bool TryGetInput(ComponentInput host, out SuComponentInputExtension extension)
        {
            extension = null;
            if (!m_enabled || host == null || host.GetType() != typeof(ComponentInput) ||
                host.Entity == null || !host.Entity.IsAddedToProject) return false;
            extension = m_inputs.GetValue(host, key => new SuComponentInputExtension(key, this));
            return true;
        }

        public bool TryGetGui(ComponentGui host, out SuComponentGuiExtension extension)
        {
            extension = null;
            if (!m_enabled || host == null || host.GetType() != typeof(ComponentGui) ||
                host.Entity == null || !host.Entity.IsAddedToProject) return false;
            extension = m_guis.GetValue(host, key => new SuComponentGuiExtension(key, this));
            return true;
        }

        private SuComponentInputExtension GetInput(ComponentInput host) => TryGetInput(host, out var extension)
            ? extension : throw new InvalidOperationException("The multiplayer input extension is disabled or its exact Host was replaced.");
        private SuComponentGuiExtension GetGui(ComponentGui host) => TryGetGui(host, out var extension)
            ? extension : throw new InvalidOperationException("The multiplayer GUI extension is disabled or its exact Host was replaced.");

        public bool IsLocalPlayer(ComponentPlayer player)
        {
            if (!m_enabled || player?.Entity == null || !player.Entity.IsAddedToProject) return false;
            if (!m_spawner.TryCollectLocalPlayers(player.Entity.Project, m_locals, out _, out _)) return false;
            foreach (PlayerData local in m_locals)
                if (ReferenceEquals(local?.ComponentPlayer, player)) return true;
            return false;
        }

        internal bool PrepareInput(ComponentInput host)
        {
            if (!m_enabled || !m_isMultiplayerActive() || !IsLocalPlayer(host.Entity?.FindComponent<ComponentPlayer>())) return false;
            m_prepareInput(host.Project);
            return true;
        }

        internal bool PrepareGui(ComponentGui host)
        {
            if (!m_enabled || !m_isMultiplayerActive() || !IsLocalPlayer(host.Entity?.FindComponent<ComponentPlayer>())) return false;
            m_prepareGui(host.Project);
            return true;
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // A failed interceptor suspends Input updates; clear all input so Move/Dig cannot repeat stale state.
        internal void ClearFailedInput(ComponentInput host)
        {
            m_input(host) = default(PlayerInput);
        }

        internal void Release(SuComponentInputExtension extension)
        {
            extension.ClearSinks();
            if (m_inputs.TryGetValue(extension.Host, out var current) && ReferenceEquals(current, extension))
                m_inputs.Remove(extension.Host);
        }

        internal void Release(SuComponentGuiExtension extension)
        {
            extension.ClearSinks();
            if (m_guis.TryGetValue(extension.Host, out var current) && ReferenceEquals(current, extension))
                m_guis.Remove(extension.Host);
        }

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            m_enabled = false;
            m_inputToken?.Dispose();
            m_guiToken?.Dispose();
            foreach (var pair in m_inputs) pair.Value.ClearSinks();
            foreach (var pair in m_guis) pair.Value.ClearSinks();
            m_inputs.Clear();
            m_guis.Clear();
            m_locals.Clear();
        }
    }
}

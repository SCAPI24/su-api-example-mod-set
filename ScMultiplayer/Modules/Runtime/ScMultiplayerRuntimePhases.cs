using Comms;
using Engine;
using Game;
using GameEntitySystem;
using ScMultiplayer.Core;
using ScMultiplayer.Ports;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        void IMultiplayerRuntimeHost.RunSessionPhase(in ModuleTickContext tickContext) =>
            RunSessionPhaseCore(in tickContext);

        void IMultiplayerRuntimeHost.RunJoinPhase(in ModuleTickContext tickContext) =>
            RunJoinPhaseCore(in tickContext);

        void IMultiplayerRuntimeHost.RunWorldControlPhase(in ModuleTickContext tickContext) =>
            RunWorldControlPhaseCore(in tickContext);

        void IMultiplayerRuntimeHost.RunCircuitPhase(in ModuleTickContext tickContext) =>
            RunCircuitPhaseCore(in tickContext);

        void IMultiplayerRuntimeHost.RunWorldPhase(in ModuleTickContext tickContext) =>
            RunWorldPhaseCore(in tickContext);

        void IMultiplayerRuntimeHost.RunPlayerPhase(in ModuleTickContext tickContext) =>
            RunPlayerPhaseCore(in tickContext);

        void IMultiplayerRuntimeHost.RunEntityPhase(in ModuleTickContext tickContext) =>
            RunEntityPhaseCore(in tickContext);

        void IMultiplayerRuntimeHost.RunUiPhase(in ModuleTickContext tickContext) =>
            RunUiPhaseCore(in tickContext);

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.UpdateFrame
        internal void RunSessionPhaseCore(in ModuleTickContext tickContext)
        {
            m_remoteServerDirectory?.Update();
            EnsureNetworkComponentPlayers();
            EnsureLocalPlayerRecordApplied();
            DetectUnexpectedClientDisconnect();
            connectionSM?.Update();
            downloadSM?.Update();
            UpdatePendingLocalGameCreation();
            UpdateHostReconnect();
            UpdateReliableTransportHealth();
            UpdateHostJoinRequests();
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.UpdateFrame
        internal void RunJoinPhaseCore(in ModuleTickContext tickContext)
        {
            UpdateAutoHostCurrentWorld();
            UpdateWorldTransferBusyStatus();
            UpdateJoinWorldProgressTimeout();
            UpdateClientJoinBarrier();
            // Downloading clients have no Project yet, so repair requests remain on this
            // frame scheduler rather than entering the native world update loop.
            if (!IsHost && client?.IsConnected == true && m_worldTransferRegistry.IncomingTransfers.Count > 0)
                RequestMissingWorldTransferChunks();
            else if (!IsHost && client?.IsConnected == true && m_isLoadingDownloadedWorld &&
                Time.RealTime >= m_nextWorldTransferManifestRequestTime)
            {
                m_nextWorldTransferManifestRequestTime =
                    Time.RealTime + WorldTransferRepairInterval;
                NetworkMessageSender.SendPakWorldRepairRequest(
                    new GamePakWorldRepairRequestMessage
                    {
                        TransferId = 0,
                        RequestManifest = true
                    });
            }
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.UpdateFrame
        internal void RunWorldControlPhaseCore(in ModuleTickContext tickContext)
        {
            UpdatePendingWorldControlRequests();
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.Update
        internal void RunCircuitPhaseCore(in ModuleTickContext tickContext)
        {
            m_circuitSynchronizer?.SetWindowActive(Window.IsActive);
            Project project = GameManager.Project;
            if (project != null)
                m_circuitSynchronizer?.EnsureBound(project);
            if (!IsHost)
                CompletePendingClientSleepWakeups();
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.UpdateProject
        internal void RunWorldPhaseCore(in ModuleTickContext tickContext)
        {
            Project project = GameManager.Project;
            if (project == null)
                return;

            m_worldObjectSynchronizer?.Update(project);
            MaintainMultiplayerTimeFlow(project);
            // Settings and help screens can remove GameScreen from the update hierarchy. A host
            // still advances the authoritative Project exactly once per frame.
            if (IsHost && client?.IsConnected == true &&
                m_lastProjectSimulationFrameIndex != Time.FrameIndex)
                GameManager.UpdateProject();
            UpdateWorldSubsystem(tickContext.DeltaTime, project);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        internal void RunPlayerPhaseCore(in ModuleTickContext tickContext)
        {
            UpdateClientSuspensionState(GameManager.Project);
        }

        // Source: Mod/ScMultiplayer/Func/WorldObjectSynchronizer.cs:WorldObjectSynchronizer.Update
        internal void RunEntityPhaseCore(in ModuleTickContext tickContext)
        {
            MaintainHostAimPresentation();
        }

        // Source: Mod/ScMultiplayer/Func/Screen/SuNetPlayScreen.cs:SuNetPlayScreen.Update
        internal void RunUiPhaseCore(in ModuleTickContext tickContext)
        {
            RenderRemotePlayers();
        }
    }
}

using System.Collections.Generic;

namespace ScMultiplayer.Core
{
    // Source: Mod/ScMultiplayer/Modules/Join/ScMultiplayerWorldTransferHandlers.cs
    // Catch-up state is keyed by ClientID and is separate from world chunk transfer state.
    internal sealed class JoinCatchUpRegistry
    {
        public Dictionary<int, global::ScMultiplayer.JoinCatchUpJournal> Journals { get; } =
            new Dictionary<int, global::ScMultiplayer.JoinCatchUpJournal>();
        public Dictionary<int, global::ScMultiplayer.PendingJoinCatchUp> Pending { get; } =
            new Dictionary<int, global::ScMultiplayer.PendingJoinCatchUp>();
        public Dictionary<int, int> TransfersAwaitingReady { get; } = new Dictionary<int, int>();
        public Dictionary<int, int> HostProjectReadyTransfers { get; } = new Dictionary<int, int>();
        public Dictionary<int, int> CompletedReadyTransfers { get; } = new Dictionary<int, int>();

        public void RemoveClient(int clientId)
        {
            Journals.Remove(clientId);
            Pending.Remove(clientId);
            TransfersAwaitingReady.Remove(clientId);
            HostProjectReadyTransfers.Remove(clientId);
            CompletedReadyTransfers.Remove(clientId);
        }

        public void Reset()
        {
            Journals.Clear();
            Pending.Clear();
            TransfersAwaitingReady.Clear();
            HostProjectReadyTransfers.Clear();
            CompletedReadyTransfers.Clear();
        }
    }
}

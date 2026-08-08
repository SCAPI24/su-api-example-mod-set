using System.Collections.Generic;

namespace ScMultiplayer.Core
{
    // Source: Mod/ScMultiplayer/Modules/Join/ScMultiplayerWorldTransferHandlers.cs
    // Owns active transfer collections and Join barrier scalars.
    internal sealed class WorldTransferRegistry
    {
        public Dictionary<int, global::ScMultiplayer.OutgoingWorldTransfer> OutgoingTransfers { get; } =
            new Dictionary<int, global::ScMultiplayer.OutgoingWorldTransfer>();

        public Dictionary<int, global::ScMultiplayer.IncomingWorldTransfer> IncomingTransfers { get; } =
            new Dictionary<int, global::ScMultiplayer.IncomingWorldTransfer>();

        public long ClientTerrainChunkBaselineRevision { get; set; }
        public long ClientTerrainJoinBaselineRevision { get; set; }
        public int PendingWorldReadyTransferId { get; set; }
        public int PendingCircuitReadyTransferId { get; set; }
        public int ClientJoinReadyStageValue { get; set; }

        public void ResetClientTerrainBaselines()
        {
            ClientTerrainChunkBaselineRevision = 0L;
            ClientTerrainJoinBaselineRevision = 0L;
        }

        public void RemoveClient(int clientId)
        {
            OutgoingTransfers.Remove(clientId);
        }

        public void Reset()
        {
            OutgoingTransfers.Clear();
            IncomingTransfers.Clear();
            ResetClientTerrainBaselines();
            PendingWorldReadyTransferId = 0;
            PendingCircuitReadyTransferId = 0;
            ClientJoinReadyStageValue = 0;
        }
    }
}

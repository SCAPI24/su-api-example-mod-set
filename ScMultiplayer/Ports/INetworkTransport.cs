using System.Net;

namespace ScMultiplayer.Ports
{
    // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
    // This port exposes transport mechanics only. It deliberately has no multiplayer or
    // gameplay semantics so message senders can be moved without copying Comms behavior.
    internal interface INetworkTransport
    {
        IPEndPoint Address { get; }

        int Step { get; }

        void SendDirectInput(int targetClientId, byte[] payload,
            bool sequenced = false, bool latest = false);
    }
}

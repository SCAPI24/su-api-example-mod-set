using System.Net;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Transport
{
    // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
    // Compatibility adapter. The existing Client remains the transport owner; this class only
    // forwards calls and does not add buffering, retries or a second reliable sequence.
    internal sealed class CommsTransportAdapter : INetworkTransport
    {
        public IPEndPoint Address => ScMultiplayer.client.Address;

        public int Step => ScMultiplayer.client.Step;

        public void SendDirectInput(int targetClientId, byte[] payload,
            bool sequenced = false, bool latest = false)
        {
            ScMultiplayer.client.SendDirectInput(targetClientId, payload, sequenced, latest);
        }
    }
}

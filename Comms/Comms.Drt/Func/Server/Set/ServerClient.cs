using System.Collections.Generic;
using System.Net;

namespace Comms.Drt;

public class ServerClient
{
    internal List<byte[]> InputsBytes = new();

    internal PeerData PeerData { get; }

    public ServerGame ServerGame { get; }

    public int ClientID { get; }

    public string ClientName { get; }

    public IPEndPoint Address => PeerData.Address;

    internal ServerClient(ServerGame serverGame, PeerData peerData, int clientID, string clientName)
    {
        if (peerData.Tag != null)
        {
            throw new ProtocolViolationException("PeerData already has a ServerClient assigned.");
        }
        ServerGame = serverGame;
        PeerData = peerData;
        peerData.Tag = this;
        ClientID = clientID;
        ClientName = clientName;
    }

    internal static ServerClient FromPeerData(PeerData peerData)
    {
        return (ServerClient)peerData.Tag;
    }
}

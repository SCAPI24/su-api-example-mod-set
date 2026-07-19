using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Comms.Drt;

public class ServerGame
{
    private class JoinRequest
    {
        public PeerData PeerData;

        public int ClientID;

        public string ClientName;

        public byte[] JoinRequestBytes;

        public double RequestTime;

        public bool Forwarded;

        public ServerClient AcceptedBy;

        public ServerClient RefusedBy;

        public int NextStateRequestIndex;

        public double NextStateRequestTime;

        public bool Processed;

        public JoinRequest(PeerData peerData, int clientID, string clientName, byte[] joinRequestBytes)
        {
            PeerData = peerData;
            ClientID = clientID;
            ClientName = clientName;
            JoinRequestBytes = joinRequestBytes;
            RequestTime = Comm.GetTime();
            NextStateRequestIndex = clientID;
        }
    }

    private List<ServerClient> ServerClients = new();

    private List<JoinRequest> JoinRequests = new();

    private List<int> Leaves = new();

    private List<ServerTickMessage> SentTickMessages = new();

    private Dictionary<int, List<ClientDirectInputMessage>> PendingDirectInputs = new();

    private DesyncDetector DesyncDetector;

    private int NextClientID;

    private int GameDescriptionStep;

    private byte[] GameDescriptionBytes;

    private double NextTickTime;

    private double NextDescriptionRequestTime;

    private int NextDescriptionRequestIndex;

    internal DesyncDetectionMode DesyncDetectionMode { get; private set; }

    internal int DesyncDetectionPeriod { get; private set; }

    public Server Server { get; }

    public int GameID { get; }

    public int Tick { get; private set; }

    public IReadOnlyList<ServerClient> Clients => ServerClients;

    internal ServerGame(Server server, PeerData creatorPeerData, int gameID, ClientCreateGameRequestMessage message)
    {
        Server = server;
        GameID = gameID;
        GameDescriptionBytes = message.GameDescriptionBytes;
        ServerClients.Add(new ServerClient(this, creatorPeerData, NextClientID++, message.ClientName));
        DesyncDetectionMode = server.Settings.DesyncDetectionMode;
        DesyncDetectionPeriod = server.Settings.DesyncDetectionPeriod;
        DesyncDetector = new DesyncDetector(this);
    }

    internal void Handle(ClientJoinGameRequestMessage message, PeerData peerData)
    {
        JoinRequest joinRequest = new(peerData, NextClientID++, message.ClientName, message.JoinRequestBytes);
        JoinRequests.Add(joinRequest);
    }

    internal void Handle(ClientJoinGameAcceptedMessage message, ServerClient serverClient)
    {
        JoinRequest joinRequest = JoinRequests.FirstOrDefault((JoinRequest r) => object.Equals(r.ClientID, message.ClientID));
        if (joinRequest != null)
        {
            if (joinRequest.RefusedBy != null)
            {
                throw new ProtocolViolationException($"Join game accept from {serverClient.PeerData.Address} for client {message.ClientID} \"{joinRequest.ClientName}\" at {joinRequest.PeerData.Address}, which was already refused by {joinRequest.RefusedBy.PeerData.Address}.");
            }
            if (joinRequest.AcceptedBy == null)
            {
                joinRequest.AcceptedBy = serverClient;
                Server.Peer.SendDataMessage(serverClient.PeerData, DeliveryMode.Reliable, Server.MessageSerializer.Write(new ServerStateRequestMessage()));
                joinRequest.NextStateRequestTime = Comm.GetTime() + 1.0;
            }
            return;
        }
        throw new ProtocolViolationException($"Join game accept from {serverClient.PeerData.Address} for non-existent join request with client ID {message.ClientID}.");
    }

    internal void Handle(ClientJoinGameRefusedMessage message, ServerClient serverClient)
    {
        JoinRequest joinRequest = JoinRequests.FirstOrDefault((JoinRequest r) => object.Equals(r.ClientID, message.ClientID));
        if (joinRequest != null)
        {
            if (joinRequest.AcceptedBy != null)
            {
                throw new ProtocolViolationException($"Join game refuse from {serverClient.PeerData.Address} for client ID {message.ClientID}, which was already accepted by {joinRequest.AcceptedBy.PeerData.Address}.");
            }
            if (joinRequest.RefusedBy == null)
            {
                joinRequest.RefusedBy = serverClient;
                Server.Peer.RefuseConnect(joinRequest.PeerData, Server.MessageSerializer.Write(new ServerConnectRefusedMessage
                {
                    Reason = message.Reason
                }));
            }
            return;
        }
        throw new ProtocolViolationException($"Join game refuse from {serverClient.PeerData.Address} for non-existent client ID {message.ClientID}.");
    }

    internal void Handle(ClientInputMessage message, ServerClient serverClient)
    {
        serverClient.InputsBytes.Add(message.InputBytes);
    }

    // Source: Comms.Drt/Func/Server/Set/ServerGame.cs:Handle(ClientInputMessage, ServerClient)
    // Immediate broadcasts do not wait behind historical ticks. Targeted ordered transfers are
    // queued until the joining client has been accepted, then sent only to that endpoint.
    internal void Handle(ClientDirectInputMessage message, ServerClient serverClient)
    {
        if (message.TargetClientID < 0)
        {
            foreach (ServerClient target in ServerClients)
                SendDirectInput(target, serverClient.ClientID, message);
            return;
        }

        ServerClient targetClient = ServerClients.FirstOrDefault(
            client => client.ClientID == message.TargetClientID);
        if (targetClient != null)
        {
            SendDirectInput(targetClient, serverClient.ClientID, message);
            return;
        }

        if (JoinRequests.Any(request => request.ClientID == message.TargetClientID && !request.Processed))
        {
            if (!PendingDirectInputs.TryGetValue(message.TargetClientID,
                out List<ClientDirectInputMessage> pending))
            {
                pending = new List<ClientDirectInputMessage>();
                PendingDirectInputs.Add(message.TargetClientID, pending);
            }
            pending.Add(message);
        }
    }

    internal void Handle(ClientStateMessage message, ServerClient serverClient)
    {
        foreach (JoinRequest joinRequest in JoinRequests)
        {
            if (!joinRequest.Processed && joinRequest.AcceptedBy != null)
            {
                int minimumTick = (message.Step + Server.StepsPerTick - 1) / Server.StepsPerTick;
                ServerTickMessage[] array = SentTickMessages.Where((ServerTickMessage m) => m.Tick >= minimumTick).ToArray();
                int acceptedStep = message.Step;
                if (array.Length != 0 && array[0].Tick != minimumTick)
                {
                    // Source: Comms.Drt/Func/Server/Set/ServerGame.cs:Handle(ClientStateMessage, ServerClient)
                    // The multiplayer world is transferred independently from lockstep state. If
                    // the authority temporarily falls behind retained history, start the joining
                    // peer at the oldest complete tick instead of leaving it in Busy until timeout.
                    acceptedStep = array[0].Tick * Server.StepsPerTick;
                    Server.InvokeWarning($"State step {message.Step} is older than retained tick " +
                        $"{array[0].Tick}; joining client will start at step {acceptedStep}.");
                }
                ServerClient serverClient2 = new(this, joinRequest.PeerData, joinRequest.ClientID, joinRequest.ClientName);
                ServerClients.Add(serverClient2);
                Server.Peer.AcceptConnect(serverClient2.PeerData, Server.MessageSerializer.Write(new ServerJoinGameAcceptedMessage
                {
                    GameID = GameID,
                    ClientID = serverClient2.ClientID,
                    TickDuration = Server.TickDuration,
                    StepsPerTick = Server.StepsPerTick,
                    DesyncDetectionMode = DesyncDetectionMode,
                    DesyncDetectionPeriod = DesyncDetectionPeriod,
                    Step = acceptedStep,
                    StateBytes = message.StateBytes,
                    TickMessages = array
                }));
                FlushPendingDirectInputs(serverClient2);
                Server.InvokeInformation($"Client \"{joinRequest.ClientName}\" at {joinRequest.PeerData.Address} joined game {GameID} at step {acceptedStep} (state size {message.StateBytes.Length} bytes).");
                joinRequest.Processed = true;
            }
        }
    }

    internal void Handle(ClientDesyncStateMessage message, ServerClient serverClient)
    {
        DesyncDetector.HandleDesyncState(message.Step, message.StateBytes, message.IsDeflated, serverClient);
    }

    internal void Handle(ClientStateHashesMessage message, ServerClient serverClient)
    {
        DesyncDetector.HandleHashes(message.FirstHashStep, message.Hashes, serverClient);
    }

    internal void Handle(ClientGameDescriptionMessage message, ServerClient serverClient)
    {
        if (message.Step > GameDescriptionStep)
        {
            GameDescriptionStep = message.Step;
            GameDescriptionBytes = message.GameDescriptionBytes;
        }
    }

    internal void HandleDisconnect(ServerClient serverClient)
    {
        Server.InvokeInformation($"Client \"{serverClient.ClientName}\" at {serverClient.PeerData.Address} disconnected from game {GameID}.");
        ServerClients.Remove(serverClient);
        serverClient.PeerData.Tag = null;
        Leaves.Add(serverClient.ClientID);
        PendingDirectInputs.Remove(serverClient.ClientID);
    }

    internal double Run(double time)
    {
        double num2;
        if (Server.TickDuration > 0f)
        {
            if (NextTickTime == 0.0)
            {
                NextTickTime = CalculateNextTickTime(time);
            }
            if (time >= NextTickTime)
            {
                int num = 1 + (int)Math.Min(Math.Floor((time - NextTickTime) / (double)Server.TickDuration), 10.0);
                for (int i = 0; i < num; i++)
                {
                    ServerTickMessage serverTickMessage = CreateTickMessage();
                    SendDataMessageToAllClients(serverTickMessage);
                    SentTickMessages.Add(serverTickMessage);
                    int tick = Tick + 1;
                    Tick = tick;
                }
                NextTickTime = CalculateNextTickTime(time);
            }
            num2 = NextTickTime;
        }
        else
        {
            ServerTickMessage serverTickMessage2 = CreateTickMessage();
            if (!serverTickMessage2.IsEmpty)
            {
                SendDataMessageToAllClients(serverTickMessage2);
                SentTickMessages.Add(serverTickMessage2);
                int tick = Tick + 1;
                Tick = tick;
            }
            num2 = time + (double)Server.Settings.TurnBasedTickWaitTime;
        }
        JoinRequests.RemoveAll((JoinRequest r) => time - r.RequestTime >= (double)Server.Settings.JoinRequestTimeout);
        if (ServerClients.Count > 0)
        {
            foreach (JoinRequest joinRequest in JoinRequests)
            {
                if (!joinRequest.Processed && joinRequest.AcceptedBy != null && time >= joinRequest.NextStateRequestTime)
                {
                    ServerClient serverClient = ServerClients[joinRequest.NextStateRequestIndex % ServerClients.Count];
                    Server.Peer.SendDataMessage(serverClient.PeerData, DeliveryMode.Reliable, Server.MessageSerializer.Write(new ServerStateRequestMessage()));
                    joinRequest.NextStateRequestIndex++;
                    joinRequest.NextStateRequestTime = time + (double)Server.Settings.StateRequestPeriod;
                    num2 = Math.Min(num2, joinRequest.NextStateRequestTime);
                }
            }
            if (NextDescriptionRequestTime == 0.0)
            {
                NextDescriptionRequestTime = Server.Settings.GameDescriptionRequestPeriod;
            }
            if (time >= NextDescriptionRequestTime)
            {
                ServerClient serverClient2 = ServerClients[NextDescriptionRequestIndex % ServerClients.Count];
                Server.Peer.SendDataMessage(serverClient2.PeerData, DeliveryMode.Reliable, Server.MessageSerializer.Write(new ServerGameDescriptionRequestMessage()));
                NextDescriptionRequestTime = time + (double)Server.Settings.GameDescriptionRequestPeriod;
                NextDescriptionRequestIndex++;
            }
            num2 = Math.Min(num2, NextDescriptionRequestTime);
        }
        double earliestTimeToKeep = time - (double)Server.Settings.JoinRequestTimeout;
        int num3 = SentTickMessages.FindIndex((ServerTickMessage m) => m.SentTime >= earliestTimeToKeep);
        if (num3 > 0)
        {
            SentTickMessages.RemoveRange(0, num3);
        }
        DesyncDetector.Run();
        return num2;
    }

    internal GameDescription CreateGameDescription()
    {
        return new GameDescription
        {
            GameID = GameID,
            Step = GameDescriptionStep,
            ClientsCount = ServerClients.Count,
            GameDescriptionBytes = GameDescriptionBytes
        };
    }

    private ServerTickMessage CreateTickMessage()
    {
        ServerTickMessage serverTickMessage = new()
        {
            Tick = Tick,
            DesyncDetectedStep = DesyncDetector.DesyncDetectedStep,
            ClientsTickData = new List<ServerTickMessage.ClientTickData>()
        };
        foreach (ServerClient serverClient in ServerClients)
        {
            if (serverClient.InputsBytes.Count > 0)
            {
                serverTickMessage.ClientsTickData.Add(new ServerTickMessage.ClientTickData
                {
                    ClientID = serverClient.ClientID,
                    InputsBytes = serverClient.InputsBytes.ToList()
                });
                serverClient.InputsBytes.Clear();
            }
        }
        foreach (JoinRequest joinRequest in JoinRequests)
        {
            if (!joinRequest.Forwarded)
            {
                serverTickMessage.ClientsTickData.Add(new ServerTickMessage.ClientTickData
                {
                    ClientID = joinRequest.ClientID,
                    JoinAddress = joinRequest.PeerData.Address,
                    JoinBytes = joinRequest.JoinRequestBytes
                });
                joinRequest.Forwarded = true;
            }
        }
        foreach (int leaf in Leaves)
        {
            serverTickMessage.ClientsTickData.Add(new ServerTickMessage.ClientTickData
            {
                ClientID = leaf,
                Leave = true
            });
        }
        Leaves.Clear();
        return serverTickMessage;
    }

    internal void SendDataMessageToAllClients(Message message)
    {
        byte[] bytes = Server.MessageSerializer.Write(message);
        foreach (ServerClient serverClient in ServerClients)
        {
            Server.Peer.SendDataMessage(serverClient.PeerData, DeliveryMode.ReliableSequenced, bytes);
        }
    }

    private void FlushPendingDirectInputs(ServerClient targetClient)
    {
        if (!PendingDirectInputs.TryGetValue(targetClient.ClientID,
            out List<ClientDirectInputMessage> pending))
            return;
        PendingDirectInputs.Remove(targetClient.ClientID);
        foreach (ClientDirectInputMessage message in pending)
            SendDirectInput(targetClient, 0, message);
    }

    private void SendDirectInput(ServerClient targetClient, int sourceClientID,
        ClientDirectInputMessage message)
    {
        byte[] bytes = Server.MessageSerializer.Write(new ServerDirectInputMessage
        {
            SourceClientID = sourceClientID,
            InputBytes = message.InputBytes
        });
        DeliveryMode deliveryMode = message.IsLatest
            ? DeliveryMode.Unreliable
            : message.IsSequenced
                ? DeliveryMode.ReliableSequenced
                : DeliveryMode.Reliable;
        Server.Peer.SendDataMessage(targetClient.PeerData, deliveryMode, bytes);
    }

    private double CalculateNextTickTime(double time)
    {
        return Math.Floor(time / (double)Server.TickDuration + 1.0) * (double)Server.TickDuration;
    }
}

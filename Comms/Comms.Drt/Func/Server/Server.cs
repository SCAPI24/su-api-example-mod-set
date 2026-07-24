using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Comms.Drt;

public class Server : IDisposable
{
    private volatile bool IsDisposed;

    private Alarm Alarm;

    private int NextGameId;

    private ServerDiscoveryResponseMessage DiscoveryResponseMessage;

    private double DiscoveryResponseMessageTime;

    private List<ServerGame> ServerGames = new();

    internal MessageSerializer MessageSerializer;

    public int GameTypeID => MessageSerializer.GameTypeID;

    public float TickDuration { get; }

    public int StepsPerTick { get; }

    public Peer Peer { get; private set; }

    public IPEndPoint Address => Peer.Address;

    public IReadOnlyList<ServerGame> Games => ServerGames;

    public ServerSettings Settings { get; } = new ServerSettings();

    public event Action<ResourceRequestData> ResourceRequest;

    public event Action<DesyncData> Desync;

    public event Action<Exception> Error;

    public event Action<string> Warning;

    public event Action<string> Information;

    public event Action<string> Debug;

    public Server(int gameTypeID, float tickDuration, int stepsPerTick, int localPort)
        : this(gameTypeID, tickDuration, stepsPerTick, new UdpTransmitter(localPort))
    {
    }

    public Server(int gameTypeID, float tickDuration, int stepsPerTick, ITransmitter transmitter)
    {
        if (stepsPerTick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stepsPerTick));
        }
        if (tickDuration != 0f && (tickDuration < 0.01f || tickDuration > 10f))
        {
            throw new ArgumentOutOfRangeException(nameof(tickDuration));
        }
        if (tickDuration == 0f && stepsPerTick != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stepsPerTick));
        }
        TickDuration = tickDuration;
        StepsPerTick = stepsPerTick;
        MessageSerializer = new MessageSerializer(gameTypeID);
        Peer = new Peer(transmitter);
        Peer.Settings.SendPeerConnectDisconnectNotifications = false;
        Peer.Error += delegate (Exception e)
        {
            if (e is MalformedMessageException ex)
            {
                InvokeWarning($"Malformed message from {ex.SenderAddress} ignored. {ex.Message}");
            }
            else
            {
                InvokeError(e);
            }
        };
        Peer.PeerDiscoveryRequest += delegate (Packet p)
        {
            if (!IsDisposed)
            {
                Message message = MessageSerializer.Read(p.Bytes, p.Address);
                if (!(message is ClientDiscoveryRequestMessage message2))
                {
                    if (!(message is ClientResourceRequestMessage message3))
                    {
                        throw new ProtocolViolationException($"Unexpected message type {message.GetType()}.");
                    }
                    Handle(message3, p.Address);
                }
                else
                {
                    Handle(message2, p.Address);
                }
            }
        };
        Peer.ConnectRequest += delegate (PeerPacket p)
        {
            if (!IsDisposed)
            {
                Message message = MessageSerializer.Read(p.Bytes, p.PeerData.Address);
                if (!(message is ClientCreateGameRequestMessage message2))
                {
                    if (!(message is ClientJoinGameRequestMessage message3))
                    {
                        throw new ProtocolViolationException($"Unexpected message type {message.GetType()}.");
                    }
                    Handle(message3, p.PeerData);
                }
                else
                {
                    Handle(message2, p.PeerData);
                }
            }
        };
        Peer.DataMessageReceived += delegate (PeerPacket p)
        {
            if (!IsDisposed)
            {
                Message message = MessageSerializer.Read(p.Bytes, p.PeerData.Address);
                switch (message)
                {
                    case ClientJoinGameAcceptedMessage accepted:
                        Handle(accepted, p.PeerData);
                        break;
                    case ClientJoinGameRefusedMessage refused:
                        Handle(refused, p.PeerData);
                        break;
                    case ClientInputMessage input:
                        Handle(input, p.PeerData);
                        break;
                    case ClientDirectInputMessage directInput:
                        Handle(directInput, p.PeerData);
                        break;
                    case ClientStateMessage state:
                        Handle(state, p.PeerData);
                        break;
                    case ClientDesyncStateMessage desyncState:
                        Handle(desyncState, p.PeerData);
                        break;
                    case ClientStateHashesMessage stateHashes:
                        Handle(stateHashes, p.PeerData);
                        break;
                    case ClientGameDescriptionMessage description:
                        Handle(description, p.PeerData);
                        break;
                    default:
                        throw new ProtocolViolationException($"Unexpected message type {message.GetType()}.");
                }
            }
        };
        Peer.PeerDisconnected += delegate (PeerData p)
        {
            if (!IsDisposed)
            {
                HandleDisconnect(p);
            }
        };
    }

    public void Dispose()
    {
        lock (Peer.Lock)
        {
            if (IsDisposed)
            {
                return;
            }
            IsDisposed = true;
        }
        Alarm?.Dispose();
        Peer?.Dispose();
    }

    public void Start()
    {
        lock (Peer.Lock)
        {
            CheckNotDisposed();
            if (Alarm != null)
            {
                throw new InvalidOperationException("Server is already started.");
            }
            InvokeInformation($"Server {Address} started at {DateTime.UtcNow}");
            Peer.Start();
            Alarm = new Alarm(AlarmFunction);
            Alarm.Error += delegate (Exception e)
            {
                InvokeError(e);
            };
            Alarm.Set(0.0);
        }
    }

    public void DisconnectAllClients()
    {
        CheckNotDisposedAndStarted();
        Peer.DisconnectAllPeers();
    }

    public void SendResource(IPEndPoint address, string name, int version, byte[] bytes)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposed();
            Peer.RespondToDiscovery(address, DeliveryMode.Reliable, MessageSerializer.Write(new ServerResourceMessage
            {
                Name = name,
                Version = version,
                Bytes = bytes
            }));
        }
    }

    private void AlarmFunction()
    {
        lock (Peer.Lock)
        {
            if (IsDisposed)
            {
                return;
            }
            double time = Comm.GetTime();
            double num = 1.0 / 0.0;
            foreach (ServerGame serverGame in ServerGames)
            {
                double num2 = serverGame.Run(time);
                num = Math.Min(num, num2);
            }
            ServerGames.RemoveAll(delegate (ServerGame g)
            {
                if (g.Clients.Count == 0)
                {
                    InvokeInformation($"Game {g.GameID} finished.");
                    return true;
                }
                return false;
            });
            Alarm.Set(Math.Max(num - Comm.GetTime(), 0.0));
        }
    }

    private void CheckNotDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException("Server");
        }
    }

    private void CheckNotDisposedAndStarted()
    {
        CheckNotDisposed();
        if (Alarm == null)
        {
            throw new InvalidOperationException("Server is not started.");
        }
    }

    private void Handle(ClientDiscoveryRequestMessage message, IPEndPoint address)
    {
        double time = Comm.GetTime();
        if (DiscoveryResponseMessage == null || time > DiscoveryResponseMessageTime + (double)Settings.GameListCacheTime)
        {
            DiscoveryResponseMessageTime = time;
            DiscoveryResponseMessage = new ServerDiscoveryResponseMessage
            {
                Name = Settings.Name,
                Priority = Settings.Priority,
                GamesDescriptions = (from g in ServerGames.OrderBy((ServerGame g) => g.Tick).Take(Settings.MaxGamesToList)
                                     select g.CreateGameDescription()).ToArray()
            };
        }
        // Source: Comms/Comms/Peer.cs:KeepAliveResponseMessage.Handle
        // Echo the request timestamp so Explorer measures only this discovery round trip.
        Peer.RespondToDiscovery(address, DeliveryMode.Unreliable,
            MessageSerializer.Write(new ServerDiscoveryResponseMessage
            {
                Name = DiscoveryResponseMessage.Name,
                Priority = DiscoveryResponseMessage.Priority,
                GamesDescriptions = DiscoveryResponseMessage.GamesDescriptions,
                ProbeSendTime = message.ProbeSendTime
            }));
    }

    private void Handle(ClientResourceRequestMessage message, IPEndPoint address)
    {
        this.ResourceRequest?.Invoke(new ResourceRequestData
        {
            Address = address,
            Name = message.Name,
            MinimumVersion = message.MinimumVersion
        });
    }

    private void Handle(ClientCreateGameRequestMessage message, PeerData peerData)
    {
        if (Settings.Priority <= 0)
        {
            Peer.RefuseConnect(peerData, MessageSerializer.Write(new ServerConnectRefusedMessage
            {
                Reason = "Server is currently not accepting new games."
            }));
            InvokeWarning($"Create game request from client \"{message.ClientName}\" at {peerData.Address} refused because priority is <= 0.");
            return;
        }
        if (ServerGames.Count >= Settings.MaxGames)
        {
            Peer.RefuseConnect(peerData, MessageSerializer.Write(new ServerConnectRefusedMessage
            {
                Reason = "Too many games."
            }));
            InvokeWarning($"Create game request from client \"{message.ClientName}\" at {peerData.Address} refused because of too many games ({ServerGames.Count}).");
            return;
        }
        ServerGame serverGame = new(this, peerData, NextGameId++, message);
        ServerGames.Add(serverGame);
        InvokeInformation($"Client \"{message.ClientName}\" at {peerData.Address} created game {serverGame.GameID}.");
        Peer.AcceptConnect(peerData, MessageSerializer.Write(new ServerCreateGameAcceptedMessage
        {
            GameID = serverGame.GameID,
            CreatorAddress = peerData.Address,
            TickDuration = TickDuration,
            StepsPerTick = StepsPerTick,
            DesyncDetectionMode = serverGame.DesyncDetectionMode,
            DesyncDetectionPeriod = serverGame.DesyncDetectionPeriod
        }));
        Alarm.Set(0.0);
    }

    private void Handle(ClientJoinGameRequestMessage message, PeerData peerData)
    {
        ServerGame serverGame = ServerGames.FirstOrDefault((ServerGame g) => g.GameID == message.GameID);
        if (serverGame != null)
        {
            serverGame.Handle(message, peerData);
            return;
        }
        InvokeWarning($"Join game request from {peerData.Address} for nonexistent game {message.GameID}.");
        Peer.RefuseConnect(peerData, MessageSerializer.Write(new ServerConnectRefusedMessage
        {
            Reason = "Game does not exist."
        }));
    }

    private void Handle(ClientJoinGameAcceptedMessage message, PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.Handle(message, serverClient);
        }
        else
        {
            InvokeWarning($"Game join accepted from {peerData.Address}, which is not a connected client.");
        }
    }

    private void Handle(ClientJoinGameRefusedMessage message, PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.Handle(message, serverClient);
        }
        else
        {
            InvokeWarning($"Game join refused from {peerData.Address}, which is not a connected client.");
        }
    }

    private void Handle(ClientInputMessage message, PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.Handle(message, serverClient);
        }
        else
        {
            InvokeWarning($"Input from {peerData.Address}, which is not a connected client.");
        }
    }

    private void Handle(ClientDirectInputMessage message, PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.Handle(message, serverClient);
        }
        else
        {
            InvokeWarning($"Direct input from {peerData.Address}, which is not a connected client.");
        }
    }

    private void Handle(ClientStateMessage message, PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.Handle(message, serverClient);
        }
        else
        {
            InvokeWarning($"State from {peerData.Address}, which is not a connected client.");
        }
    }

    private void Handle(ClientDesyncStateMessage message, PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.Handle(message, serverClient);
        }
        else
        {
            InvokeWarning($"Desync state from {peerData.Address}, which is not a connected client.");
        }
    }

    private void Handle(ClientStateHashesMessage message, PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.Handle(message, serverClient);
        }
        else
        {
            InvokeWarning($"State hashes from {peerData.Address}, which is not a connected client.");
        }
    }

    private void Handle(ClientGameDescriptionMessage message, PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.Handle(message, serverClient);
        }
        else
        {
            InvokeWarning($"Game description from {peerData.Address}, which is not a connected client.");
        }
    }

    private void HandleDisconnect(PeerData peerData)
    {
        ServerClient serverClient = ServerClient.FromPeerData(peerData);
        if (serverClient != null)
        {
            serverClient.ServerGame.HandleDisconnect(serverClient);
        }
        else
        {
            InvokeWarning($"Disconnect received from {peerData.Address}, which is not a connected client.");
        }
    }

    internal void InvokeDesync(DesyncData desyncData)
    {
        this.Desync?.Invoke(desyncData);
    }

    internal void InvokeError(Exception error)
    {
        this.Error?.Invoke(error);
    }

    internal void InvokeWarning(string warning)
    {
        this.Warning?.Invoke(warning);
    }

    internal void InvokeInformation(string information)
    {
        this.Information?.Invoke(information);
    }

    [Conditional("DEBUG")]
    internal void InvokeDebug(string format, params object[] args)
    {
        if (this.Debug != null)
        {
            this.Debug?.Invoke(string.Format(format, args));
        }
    }
}

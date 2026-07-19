using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Comms.Drt;

public class Client : IDisposable
{
    private volatile bool IsDisposed;

    private Alarm Alarm;

    private Queue<ServerTickMessage> TickMessages = new();

    private int MaxAllowedStep;

    private double NextTickExpectedTime;

    private double LastStepTime;

    private int AlarmFunctionRunning;

    private const int MaxCatchUpStepsPerBatch = 16;

    private const double CatchUpSpeed = 4.0;

    private const int FastCatchUpThresholdSteps = 200;

    internal MessageSerializer MessageSerializer;

    private float TickDurationField;

    private int StepsPerTickField;

    private int GameIDField;

    private int ClientIDField;

    private int StepField;

    public int GameTypeID => MessageSerializer.GameTypeID;

    public float TickDuration
    {
        get
        {
            CheckNotDisposed();
            return TickDurationField;
        }
        private set
        {
            TickDurationField = value;
        }
    }

    public int StepsPerTick
    {
        get
        {
            CheckNotDisposed();
            return StepsPerTickField;
        }
        private set
        {
            StepsPerTickField = value;
        }
    }

    public float StepDuration => TickDuration / (float)StepsPerTick;

    public DesyncDetectionMode DesyncDetectionMode { get; private set; }

    public int DesyncDetectionPeriod { get; private set; }

    public int? DesyncDetectedStep { get; private set; }

    public int GameID
    {
        get
        {
            CheckNotDisposed();
            return GameIDField;
        }
        private set
        {
            GameIDField = value;
        }
    }

    public int ClientID
    {
        get
        {
            CheckNotDisposed();
            return ClientIDField;
        }
        private set
        {
            ClientIDField = value;
        }
    }

    public int Step
    {
        get
        {
            CheckNotDisposed();
            return StepField;
        }
        private set
        {
            StepField = value;
        }
    }

    public Peer Peer { get; private set; }

    public object Lock => Peer.Lock;

    public IPEndPoint Address => Peer.Address;

    public bool IsConnecting => Peer.ConnectingTo != null;

    public bool IsConnected => Peer.ConnectedTo != null;

    public ClientSettings Settings { get; } = new ClientSettings();

    public float StalledTime
    {
        get
        {
            lock (Peer.Lock)
            {
                if (IsConnected && StepDuration > 0f)
                {
                    return (float)Math.Max(Comm.GetTime() - (LastStepTime + (double)StepDuration), 0.0);
                }
                return 0f;
            }
        }
    }

    public event Action<GameCreatedData> GameCreated;

    public event Action<GameJoinedData> GameJoined;

    public event Action<ConnectRefusedData> ConnectRefused;

    public event Action<ConnectTimedOutData> ConnectTimedOut;

    public event Action<GameStateRequestData> GameStateRequest;

    public event Action<GameDesyncStateRequestData> GameDesyncStateRequest;

    public event Action<GameDescriptionRequestData> GameDescriptionRequest;

    public event Action<GameStepData> GameStep;

    public event Action<int, byte[]> DirectInput;

    public event Action<DisconnectedData> Disconnected;

    public event Action<Exception> Error;

    public event Action<string> Debug;

    public Client(int gameTypeID, int localPort = 0)
        : this(gameTypeID, new UdpTransmitter(localPort))
    {
    }

    public Client(int gameTypeID, ITransmitter transmitter)
    {
        if (transmitter == null)
        {
            throw new ArgumentNullException(nameof(transmitter));
        }
        MessageSerializer = new MessageSerializer(gameTypeID);
        Peer = new Peer(transmitter);
        Peer.Error += delegate (Exception e)
        {
            if (!(e is MalformedMessageException))
            {
                InvokeError(e);
            }
        };
        Peer.PeerDiscoveryRequest += delegate
        {
        };
        Peer.ConnectAccepted += delegate (PeerPacket p)
        {
            if (!IsDisposed)
            {
                Message message = MessageSerializer.Read(p.Bytes, p.PeerData.Address);
                if (!(message is ServerCreateGameAcceptedMessage message2))
                {
                    if (!(message is ServerJoinGameAcceptedMessage message3))
                    {
                        throw new ProtocolViolationException($"Unexpected message type {message.GetType()}.");
                    }
                    Handle(message3);
                }
                else
                {
                    Handle(message2);
                }
            }
        };
        Peer.ConnectRefused += delegate (Packet p)
        {
            if (!IsDisposed)
            {
                Message message = MessageSerializer.Read(p.Bytes, p.Address);
                if (!(message is ServerConnectRefusedMessage message2))
                {
                    throw new ProtocolViolationException($"Unexpected message type {message.GetType()}.");
                }
                Handle(message2, p.Address);
            }
        };
        Peer.ConnectTimedOut += delegate (IPEndPoint p)
        {
            if (!IsDisposed)
            {
                this.ConnectTimedOut?.Invoke(new ConnectTimedOutData
                {
                    Address = p
                });
            }
        };
        Peer.DataMessageReceived += delegate (PeerPacket p)
        {
            if (!IsDisposed)
            {
                Message message = MessageSerializer.Read(p.Bytes, p.PeerData.Address);
                switch (message)
                {
                    case ServerStateRequestMessage stateRequest:
                        Handle(stateRequest);
                        break;
                    case ServerDesyncStateRequestMessage desyncRequest:
                        Handle(desyncRequest);
                        break;
                    case ServerGameDescriptionRequestMessage descriptionRequest:
                        Handle(descriptionRequest);
                        break;
                    case ServerTickMessage tick:
                        Handle(tick);
                        break;
                    case ServerDirectInputMessage directInput:
                        Handle(directInput);
                        break;
                    default:
                        throw new ProtocolViolationException($"Unexpected message type {message.GetType()}.");
                }
            }
        };
        Peer.Disconnected += delegate
        {
            if (!IsDisposed)
            {
                HandleDisconnected();
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
        Alarm.Dispose();
        Peer.Dispose();
    }

    public void Start()
    {
        lock (Peer.Lock)
        {
            CheckNotDisposed();
            if (Alarm != null)
            {
                throw new InvalidOperationException("Client is already started.");
            }
            Peer.Start();
            Alarm = new Alarm(AlarmFunction);
            Alarm.Error += delegate (Exception e)
            {
                InvokeError(e);
            };
            Alarm.Set(0.0);
        }
    }

    public void CreateGame(IPEndPoint serverAddress, byte[] gameDescriptionBytes, string clientName = null)
    {
        Peer.Connect(serverAddress, MessageSerializer.Write(new ClientCreateGameRequestMessage
        {
            ClientName = clientName,
            GameDescriptionBytes = gameDescriptionBytes
        }));
    }

    public void JoinGame(IPEndPoint serverAddress, int gameID, byte[] joinRequestBytes = null, string clientName = null)
    {
        Peer.Connect(serverAddress, MessageSerializer.Write(new ClientJoinGameRequestMessage
        {
            GameID = gameID,
            JoinRequestBytes = joinRequestBytes,
            ClientName = clientName
        }));
    }

    public void LeaveGame()
    {
        Peer.Disconnect();
    }

    public void AcceptJoinGame(int clientID)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposedAndConnected();
            Peer.SendDataMessage(Peer.ConnectedTo, DeliveryMode.Reliable, MessageSerializer.Write(new ClientJoinGameAcceptedMessage
            {
                ClientID = clientID
            }));
        }
    }

    public void RefuseJoinGame(int clientID, string reason)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposedAndConnected();
            Peer.SendDataMessage(Peer.ConnectedTo, DeliveryMode.Reliable, MessageSerializer.Write(new ClientJoinGameRefusedMessage
            {
                ClientID = clientID,
                Reason = reason
            }));
        }
    }

    public void SendInput(byte[] inputBytes)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposedAndConnected();
            Peer.SendDataMessage(Peer.ConnectedTo, DeliveryMode.Reliable, MessageSerializer.Write(new ClientInputMessage
            {
                InputBytes = inputBytes
            }));
        }
    }

    // Source: Comms.Drt/Func/Client/Client.cs:SendInput
    // A negative target broadcasts immediately. A non-negative sequenced target is isolated to
    // one endpoint and is suitable for large ordered join transfers.
    public void SendDirectInput(int targetClientID, byte[] inputBytes, bool sequenced = false,
        bool latest = false)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposedAndConnected();
            // Source: Comms/Comm.cs:Comm.ProcessReceivedMessage
            // Different latest-state message types cannot share one unreliable sequence stream:
            // a projectile packet would otherwise invalidate a delayed player or animal packet.
            // Their application payloads carry ticks/IDs and discard stale state independently.
            DeliveryMode deliveryMode = latest
                ? DeliveryMode.Unreliable
                : DeliveryMode.Reliable;
            Peer.SendDataMessage(Peer.ConnectedTo, deliveryMode,
                MessageSerializer.Write(new ClientDirectInputMessage
                {
                    TargetClientID = targetClientID,
                    IsSequenced = sequenced,
                    IsLatest = latest,
                    InputBytes = inputBytes
                }));
        }
    }

    public void SendState(int step, byte[] stateBytes)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposedAndConnected();
            Peer.SendDataMessage(Peer.ConnectedTo, DeliveryMode.Reliable, MessageSerializer.Write(new ClientStateMessage
            {
                Step = step,
                StateBytes = stateBytes
            }));
        }
    }

    public void SendDesyncState(int step, byte[] stateBytes, bool isDeflated)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposedAndConnected();
            Peer.SendDataMessage(Peer.ConnectedTo, DeliveryMode.Reliable, MessageSerializer.Write(new ClientDesyncStateMessage
            {
                Step = step,
                StateBytes = stateBytes,
                IsDeflated = isDeflated
            }));
        }
    }

    public void SendStateHashes(int firstHashStep, ushort[] stateHashes)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposedAndConnected();
            Peer.SendDataMessage(Peer.ConnectedTo, DeliveryMode.Reliable, MessageSerializer.Write(new ClientStateHashesMessage
            {
                FirstHashStep = firstHashStep,
                Hashes = stateHashes
            }));
        }
    }

    public void SendGameDescription(byte[] gameDescriptionBytes)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposedAndConnected();
            Peer.SendDataMessage(Peer.ConnectedTo, DeliveryMode.Unreliable, MessageSerializer.Write(new ClientGameDescriptionMessage
            {
                Step = Step,
                GameDescriptionBytes = gameDescriptionBytes
            }));
        }
    }

    private void CheckNotDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException("Client");
        }
    }

    private void CheckNotDisposedAndConnected()
    {
        CheckNotDisposed();
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected.");
        }
    }

    [Conditional("DEBUG")]
    internal void InvokeDebug(string format, params object[] args)
    {
        if (this.Debug != null)
        {
            this.Debug?.Invoke(string.Format(format, args));
        }
    }

    internal void InvokeError(Exception error)
    {
        this.Error?.Invoke(error);
    }

    private void InitializeConnection(IEnumerable<ServerTickMessage> tickMessages)
    {
        MaxAllowedStep = 0;
        LastStepTime = Comm.GetTime() - (double)StepDuration;
        TickMessages.Clear();
        if (tickMessages != null)
        {
            foreach (ServerTickMessage tickMessage in tickMessages)
            {
                TickMessages.Enqueue(tickMessage);
            }
        }
        Alarm.Set(0.0);
    }

    private double GetStepWaitTime(double time)
    {
        if (TickDuration > 0f)
        {
            if (TickMessages.Count > 0)
            {
                ServerTickMessage serverTickMessage = TickMessages.Last();
                MaxAllowedStep = (TickMessages.Last().Tick + 1) * StepsPerTick;
                NextTickExpectedTime = serverTickMessage.ReceivedTime + (double)TickDuration;
            }
            if (Step >= MaxAllowedStep)
            {
                return 1.0 / 0.0;
            }
            int num = MaxAllowedStep - Step;
            double num2 = NextTickExpectedTime - time;
            double num3 = (double)((float)num * StepDuration) - num2;
            double waitTime = (double)Settings.SafetyLag - num3;
            // Source: Comms.Drt/Func/Client/Client.cs:GetStepWaitTime
            // Historical ticks remain ordered, but a peer more than two seconds behind processes
            // bounded batches immediately and releases Peer.Lock between every step. Smaller
            // timing differences use a smooth accelerated clock.
            if (waitTime <= 0.0 && num > FastCatchUpThresholdSteps)
                return 0.0;
            if (waitTime <= 0.0 && num > StepsPerTick * 2)
                return Math.Max(LastStepTime + (double)StepDuration / CatchUpSpeed - time, 0.0);
            return waitTime;
        }
        if (TickMessages.Count <= 0)
        {
            return 1.0 / 0.0;
        }
        return 0.0;
    }

    private void AlarmFunction()
    {
        if (Interlocked.Exchange(ref AlarmFunctionRunning, 1) != 0)
            return;
        double nextDelay = 1.0 / 0.0;
        try
        {
            int processedSteps = 0;
            while (processedSteps < MaxCatchUpStepsPerBatch)
            {
                GameStepData? gameStep = null;
                lock (Peer.Lock)
                {
                    if (IsDisposed || !IsConnected)
                    {
                        nextDelay = 1.0 / 0.0;
                        break;
                    }
                    double time = Comm.GetTime();
                    nextDelay = GetStepWaitTime(time);
                    if (nextDelay > 0.0)
                        break;
                    ServerTickMessage serverTickMessage;
                    if (Step % StepsPerTick == 0)
                    {
                        serverTickMessage = TickMessages.Dequeue();
                        if (serverTickMessage.Tick != Step / StepsPerTick)
                            throw new Exception($"Wrong tick message, expected {Step / StepsPerTick} got {serverTickMessage.Tick}.");
                    }
                    else
                    {
                        serverTickMessage = null;
                    }
                    Step++;
                    LastStepTime = time;
                    gameStep = CreateGameStepData(serverTickMessage);
                }
                if (!gameStep.HasValue)
                    break;
                InvokeGameStep(gameStep.Value);
                processedSteps++;
            }
            if (processedSteps == MaxCatchUpStepsPerBatch && nextDelay <= 0.0)
                nextDelay = 0.001;
        }
        catch (Exception error)
        {
            InvokeError(error);
        }
        finally
        {
            Interlocked.Exchange(ref AlarmFunctionRunning, 0);
            lock (Peer.Lock)
            {
                if (!IsDisposed)
                    Alarm.Set(Math.Max(nextDelay, 0.0));
            }
        }
    }

    private void InvokeGameStep(ServerTickMessage tickMessage)
    {
        InvokeGameStep(CreateGameStepData(tickMessage));
    }

    private void InvokeGameStep(GameStepData obj)
    {
        try
        {
            this.GameStep?.Invoke(obj);
        }
        catch (Exception obj2)
        {
            this.Error?.Invoke(obj2);
        }
    }

    private GameStepData CreateGameStepData(ServerTickMessage tickMessage)
    {
        if (tickMessage != null)
        {
            List<GameStepData.JoinData> list = new();
            List<GameStepData.LeaveData> list2 = new();
            List<GameStepData.InputData> list3 = new();
            foreach (ServerTickMessage.ClientTickData clientsTickDatum in tickMessage.ClientsTickData)
            {
                if (clientsTickDatum.JoinBytes != null)
                {
                    list.Add(new GameStepData.JoinData
                    {
                        ClientID = clientsTickDatum.ClientID,
                        Address = clientsTickDatum.JoinAddress,
                        JoinRequestBytes = clientsTickDatum.JoinBytes
                    });
                }
                else if (clientsTickDatum.Leave)
                {
                    list2.Add(new GameStepData.LeaveData
                    {
                        ClientID = clientsTickDatum.ClientID
                    });
                }
                else
                {
                    if (clientsTickDatum.InputsBytes == null)
                    {
                        continue;
                    }
                    foreach (byte[] inputsByte in clientsTickDatum.InputsBytes)
                    {
                        list3.Add(new GameStepData.InputData
                        {
                            ClientID = clientsTickDatum.ClientID,
                            InputBytes = inputsByte
                        });
                    }
                }
            }
            return new GameStepData
            {
                Step = Step,
                Joins = list.ToArray(),
                Leaves = list2.ToArray(),
                Inputs = list3.ToArray()
            };
        }
        return new GameStepData
        {
            Step = Step,
            Joins = Array.Empty<GameStepData.JoinData>(),
            Leaves = Array.Empty<GameStepData.LeaveData>(),
            Inputs = Array.Empty<GameStepData.InputData>()
        };
    }

    private void Handle(ServerCreateGameAcceptedMessage message)
    {
        GameID = message.GameID;
        ClientID = 0;
        TickDuration = message.TickDuration;
        StepsPerTick = message.StepsPerTick;
        DesyncDetectionMode = message.DesyncDetectionMode;
        DesyncDetectionPeriod = message.DesyncDetectionPeriod;
        Step = 0;
        InitializeConnection(null);
        this.GameCreated?.Invoke(new GameCreatedData
        {
            CreatorAddress = message.CreatorAddress
        });
    }

    private void Handle(ServerJoinGameAcceptedMessage message)
    {
        GameID = message.GameID;
        ClientID = message.ClientID;
        TickDuration = message.TickDuration;
        StepsPerTick = message.StepsPerTick;
        DesyncDetectionMode = message.DesyncDetectionMode;
        DesyncDetectionPeriod = message.DesyncDetectionPeriod;
        Step = message.Step;
        InitializeConnection(message.TickMessages);
        this.GameJoined?.Invoke(new GameJoinedData
        {
            Step = message.Step,
            StateBytes = message.StateBytes
        });
    }

    private void Handle(ServerConnectRefusedMessage message, IPEndPoint address)
    {
        this.ConnectRefused?.Invoke(new ConnectRefusedData
        {
            Address = address,
            Reason = message.Reason
        });
    }

    private void Handle(ServerStateRequestMessage message)
    {
        this.GameStateRequest?.Invoke(default);
    }

    private void Handle(ServerDesyncStateRequestMessage message)
    {
        this.GameDesyncStateRequest?.Invoke(new GameDesyncStateRequestData
        {
            Step = message.Step
        });
    }

    private void Handle(ServerGameDescriptionRequestMessage message)
    {
        this.GameDescriptionRequest?.Invoke(default);
    }

    private void Handle(ServerTickMessage message)
    {
        if (!DesyncDetectedStep.HasValue && message.DesyncDetectedStep.HasValue)
        {
            DesyncDetectedStep = message.DesyncDetectedStep;
        }
        TickMessages.Enqueue(message);
        Alarm.Set(0.0);
    }

    private void Handle(ServerDirectInputMessage message)
    {
        this.DirectInput?.Invoke(message.SourceClientID, message.InputBytes);
    }

    private void HandleDisconnected()
    {
        this.Disconnected?.Invoke(default);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Comms.Drt;

public class Explorer : IDisposable
{
    private class DnsCache
    {
        private Dictionary<string, IPAddress[]> Cache = new();

        public IPAddress[] Query(string host)
        {
            lock (Cache)
            {
                Cache.TryGetValue(host, out var result);
                return result;
            }
        }

        public void Add(string host, IPAddress[] addresses)
        {
            lock (Cache)
            {
                Cache[host] = addresses;
            }
        }

        public void Clear()
        {
            lock (Cache)
            {
                Cache.Clear();
            }
        }
    }

    private bool IsDisposed;

    private int[] ServerPorts;

    private bool LocalBroadcast;

    private string[] InternetHosts;

    private double LocalLastTime;

    private int LocalServerPortIndex;

    private double InternetLastTime;

    private Dictionary<IPEndPoint, double> InternetRequestTimes = new();

    private Dictionary<IPEndPoint, double> DirectRequestTimes = new();

    private Alarm Alarm;

    private DnsCache Cache = new();

    private List<ServerDescription> ServersList = new();

    private IReadOnlyList<ServerDescription> ServersReadonlyList;

    internal MessageSerializer MessageSerializer;

    public int GameTypeID => MessageSerializer.GameTypeID;

    public Peer Peer { get; }

    public IPEndPoint Address => Peer.Address;

    public ExplorerSettings Settings { get; } = new ExplorerSettings();

    public bool IsDiscoveryStarted => Alarm != null;

    public IReadOnlyList<ServerDescription> DiscoveredServers
    {
        get
        {
            lock (Peer.Lock)
            {
                CheckNotDisposed();
                if (ServersReadonlyList == null)
                {
                    ServersReadonlyList = ServersList.ToArray();
                }
                return ServersReadonlyList;
            }
        }
    }

    public event Action<ResourceData> ResourceReceived;

    public event Action<Exception> Error;

    public event Action<string> Debug;

    public event Action<ServerDescription> ServerDiscovered;

    public Explorer(int gameTypeID, int serverPort, int localPort = 0)
        : this(gameTypeID, new int[1] { serverPort }, localPort)
    {
    }

    public Explorer(int gameTypeID, IEnumerable<int> serverPorts, int localPort = 0)
        : this(gameTypeID, serverPorts, new UdpTransmitter(localPort))
    {
    }

    public Explorer(int gameTypeID, int serverPort, ITransmitter transmitter)
        : this(gameTypeID, new int[1] { serverPort }, transmitter)
    {
    }

    public Explorer(int gameTypeID, IEnumerable<int> serverPorts, ITransmitter transmitter)
    {
        if (transmitter == null)
        {
            throw new ArgumentNullException(nameof(transmitter));
        }
        if (serverPorts.Any((int p) => p < 0 || p > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(serverPorts));
        }
        MessageSerializer = new MessageSerializer(gameTypeID);
        ServerPorts = serverPorts.ToArray();
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
        Peer.PeerDiscovered += delegate (Packet p)
        {
            if (!IsDisposed)
            {
                Message message = MessageSerializer.Read(p.Bytes, p.Address);
                if (!(message is ServerDiscoveryResponseMessage message2))
                {
                    if (!(message is ServerResourceMessage message3))
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
        Peer.Start();
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
        if (Alarm != null)
        {
            Alarm.Dispose();
            Alarm = null;
        }
        Peer.Dispose();
    }

    public void StartDiscovery(bool localBroadcast = true, IEnumerable<string> internetHosts = null)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposed();
            LocalBroadcast = localBroadcast;
            InternetHosts = ((internetHosts != null) ? internetHosts.ToArray() : Array.Empty<string>());
            LocalLastTime = -1.7976931348623157E+308;
            LocalServerPortIndex = 0;
            InternetLastTime = -1.7976931348623157E+308;
            InternetRequestTimes.Clear();
            DirectRequestTimes.Clear();
            Cache.Clear();
            if (Alarm != null)
            {
                Alarm.Dispose();
            }
            Alarm = new Alarm(AlarmFunction);
            Alarm.Error += delegate (Exception e)
            {
                InvokeError(e);
            };
            Alarm.Set(0.0);
        }
    }

    public void StopDiscovery()
    {
        lock (Peer.Lock)
        {
            CheckNotDisposed();
            ServersList.Clear();
            ServersReadonlyList = null;
            if (Alarm != null)
            {
                Alarm.Dispose();
                Alarm = null;
            }
        }
    }

    public void RequestResource(IPEndPoint serverAddress, string name, int minimumVersion)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposed();
            Peer.DiscoverPeer(serverAddress, MessageSerializer.Write(new ClientResourceRequestMessage
            {
                Name = name,
                MinimumVersion = minimumVersion
            }));
        }
    }

    // Source: Comms.Drt/Func/Explorer/Explorer.DiscoverLocalServers
    // Probe a server endpoint learned from a peer's discovery request. This is needed when an
    // Android VpnService permits inbound ZeroTier broadcasts but blocks outbound broadcasts.
    public void DiscoverServer(IPEndPoint serverAddress)
    {
        lock (Peer.Lock)
        {
            CheckNotDisposed();
            if (serverAddress == null) throw new ArgumentNullException(nameof(serverAddress));
            double sendTime = Comm.GetTime();
            DirectRequestTimes[serverAddress] = sendTime;
            Peer.DiscoverPeer(serverAddress, MessageSerializer.Write(
                new ClientDiscoveryRequestMessage { ProbeSendTime = sendTime }));
        }
    }

    public void RequestResource(string host, int port, string name, int minimumVersion)
    {
        Task.Run(delegate
        {
            IPAddress[] array = DnsQueryHost(host);
            foreach (IPAddress iPAddress in array)
            {
                RequestResource(new IPEndPoint(iPAddress, port), name, minimumVersion);
            }
        });
    }

    private void AlarmFunction()
    {
        lock (Peer.Lock)
        {
            if (!IsDisposed)
            {
                double time = Comm.GetTime();
                if (time >= LocalLastTime + (double)Settings.LocalDiscoveryPeriod)
                {
                    LocalLastTime = time;
                    DiscoverLocalServers();
                }
                if (time >= InternetLastTime + (double)Settings.InternetDiscoveryPeriod)
                {
                    InternetLastTime = time;
                    DiscoverInternetServers(InternetHosts);
                }
                double num = (LocalBroadcast ? ((double)Settings.LocalDiscoveryPeriod - (time - LocalLastTime)) : 1.7976931348623157E+308);
                double num2 = ((InternetHosts.Count() > 0) ? ((double)Settings.InternetDiscoveryPeriod - (time - InternetLastTime)) : 1.7976931348623157E+308);
                double waitTime = Math.Min(num, num2);
                if (ServersList.RemoveAll(delegate (ServerDescription s)
                {
                    double num3 = Math.Max((double)(s.IsLocal ? Settings.LocalRemoveTime : Settings.InternetRemoveTime) - (time - s.DiscoveryTime), 0.0);
                    waitTime = Math.Min(waitTime, num3);
                    return num3 <= 0.0;
                }) > 0)
                {
                    ServersReadonlyList = null;
                }
                Alarm.Set(waitTime);
            }
        }
    }

    private void DiscoverLocalServers()
    {
        if (!LocalBroadcast)
        {
            return;
        }
        if (ServerPorts.Length == 0)
        {
            return;
        }
        // Source: Comms.Drt/Func/Explorer/Explorer.cs:Explorer.DiscoverLocalServers
        // Rotate a small port batch so VPN adapters are not flooded every discovery period.
        int count = Math.Min(
            Math.Max(Settings.LocalDiscoveryPortBatchSize, 1),
            ServerPorts.Length);
        double sendTime = Comm.GetTime();
        byte[] request = MessageSerializer.Write(
            new ClientDiscoveryRequestMessage { ProbeSendTime = sendTime });
        for (int i = 0; i < count; i++)
        {
            int peerPort = ServerPorts[LocalServerPortIndex];
            LocalServerPortIndex = (LocalServerPortIndex + 1) % ServerPorts.Length;
            try
            {
                Peer.DiscoverLocalPeers(peerPort, request);
            }
            catch (Exception error)
            {
                InvokeError(error);
            }
        }
    }

    private void DiscoverInternetServers(IEnumerable<string> hosts)
    {
        foreach (string host in hosts)
        {
            Task.Run(delegate
            {
                IPAddress[] array = DnsQueryHost(host);
                if (array != null)
                {
                    lock (Peer.Lock)
                    {
                        IPAddress[] array2 = array;
                        foreach (IPAddress iPAddress in array2)
                        {
                            int[] serverPorts = ServerPorts;
                            foreach (int num in serverPorts)
                            {
                                try
                                {
                                    IPEndPoint iPEndPoint = new(iPAddress, num);
                                    double sendTime = Comm.GetTime();
                                    InternetRequestTimes[iPEndPoint] = sendTime;
                                    Peer.DiscoverPeer(iPEndPoint, MessageSerializer.Write(
                                        new ClientDiscoveryRequestMessage
                                        {
                                            ProbeSendTime = sendTime
                                        }));
                                }
                                catch (Exception error)
                                {
                                    InvokeError(error);
                                }
                            }
                        }
                    }
                }
            });
        }
    }

    private void CheckNotDisposed()
    {
        if (Peer == null)
        {
            throw new ObjectDisposedException("Server");
        }
    }

    private IPAddress[] DnsQueryHost(string host)
    {
        IPAddress[] array = Cache.Query(host);
        if (array == null)
        {
            try
            {
                array = Dns.GetHostEntry(host).AddressList.Where((IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
                Cache.Add(host, array);
            }
            catch
            {
            }
        }
        return array ?? Array.Empty<IPAddress>();
    }

    private void Handle(ServerDiscoveryResponseMessage message, IPEndPoint address)
    {
        if (!IsDisposed && Alarm != null)
        {
            double time = Comm.GetTime();
            bool direct = DirectRequestTimes.TryGetValue(address,
                out var directRequestTime);
            bool internet = InternetRequestTimes.TryGetValue(address,
                out var internetRequestTime);
            bool isLocal = !internet;
            if (direct)
            {
                isLocal = true;
                DirectRequestTimes.Remove(address);
            }

            // Source: Comms/Comms/Peer.cs:KeepAliveResponseMessage.Handle
            // Prefer the echoed timestamp from this exact request. Older peers omit it and use
            // the per-endpoint fallback below without including a whole discovery scan period.
            double elapsed = time - message.ProbeSendTime;
            float ping;
            if (message.ProbeSendTime > 0.0 && elapsed >= 0.0 && elapsed <= 10.0)
            {
                ping = (float)elapsed;
            }
            else if (direct)
            {
                ping = (float)Math.Max(time - directRequestTime, 0.0);
            }
            else if (internet)
            {
                ping = (float)Math.Max(time - internetRequestTime, 0.0);
            }
            else
            {
                ping = (float)Math.Max(time - LocalLastTime, 0.0);
            }
            ServersList.RemoveAll((ServerDescription s) => object.Equals(s.Address, address));
            ServerDescription serverDescription = new()
            {
                Address = address,
                Name = message.Name,
                Priority = message.Priority,
                IsLocal = isLocal,
                Ping = ping,
                DiscoveryTime = time,
                GameDescriptions = message.GamesDescriptions
            };
            GameDescription[] gameDescriptions = serverDescription.GameDescriptions;
            for (int num2 = 0; num2 < gameDescriptions.Length; num2++)
            {
                gameDescriptions[num2].ServerDescription = serverDescription;
            }
            ServersList.Add(serverDescription);
            ServersReadonlyList = null;
            this.ServerDiscovered?.Invoke(serverDescription);
        }
    }

    private void Handle(ServerResourceMessage message, IPEndPoint address)
    {
        if (!IsDisposed)
        {
            this.ResourceReceived?.Invoke(new ResourceData
            {
                Name = message.Name,
                Version = message.Version,
                Bytes = message.Bytes
            });
        }
    }

    internal void InvokeError(Exception error)
    {
        this.Error?.Invoke(error);
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Comms
{
    public class Peer : IDisposable
    {
        private abstract class Message
        {
            private static Dictionary<int, Type> MessageTypesByMessageId;

            private static Dictionary<Type, int> MessageIdsByMessageTypes;

            static Message()
            {
                MessageTypesByMessageId = new Dictionary<int, Type>();
                MessageIdsByMessageTypes = new Dictionary<Type, int>();
                TypeInfo[] array = (from t in typeof(Message).GetTypeInfo().Assembly.DefinedTypes
                                    where typeof(Message).GetTypeInfo().IsAssignableFrom(t)
                                    orderby t.Name
                                    select t).ToArray();
                for (int num = 0; num < array.Length; num++)
                {
                    MessageTypesByMessageId[num] = array[num].AsType();
                    MessageIdsByMessageTypes[array[num].AsType()] = num;
                }
            }

            public static Message Read(byte[] bytes)
            {
                Reader reader = new(bytes);
                byte b = reader.ReadByte();
                if (MessageTypesByMessageId.TryGetValue(b, out var type))
                {
                    Message obj = (Message)Activator.CreateInstance(type);
                    obj.Read(reader);
                    return obj;
                }
                return null;
            }

            public static byte[] Write(Message message)
            {
                Writer writer = new();
                writer.WriteByte((byte)MessageIdsByMessageTypes[message.GetType()]);
                message.Write(writer);
                return writer.GetBytes();
            }

            public static void Handle(Peer peer, Packet packet)
            {
                Message message = Read(packet.Bytes);
                if (message != null)
                {
                    message.Handle(peer, packet.Address);
                    return;
                }
                throw new ProtocolViolationException("Unrecognized message, ignoring.");
            }

            protected abstract void Read(Reader reader);

            protected abstract void Write(Writer writer);

            protected virtual void Handle(Peer peer, IPEndPoint address)
            {
            }
        }

        private class DiscoveryRequestMessage : Message
        {
            public byte[] DiscoveryRequestData;

            protected override void Read(Reader reader)
            {
                DiscoveryRequestData = reader.ReadFixedBytes(reader.Length - reader.Position);
            }

            protected override void Write(Writer writer)
            {
                writer.WriteFixedBytes(DiscoveryRequestData);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                if (peer.PeerDiscoveryRequest != null)
                {
                    peer.PeerDiscoveryRequest(new Packet(address, DiscoveryRequestData));
                }
                else
                {
                    peer.RespondToDiscovery(address, DeliveryMode.Raw);
                }
            }
        }

        private class DiscoveryResponseMessage : Message
        {
            public byte[] DiscoveryResponseData;

            protected override void Read(Reader reader)
            {
                DiscoveryResponseData = reader.ReadFixedBytes(reader.Length - reader.Position);
            }

            protected override void Write(Writer writer)
            {
                writer.WriteFixedBytes(DiscoveryResponseData);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                peer.PeerDiscovered?.Invoke(new Packet(address, DiscoveryResponseData));
            }
        }

        private class ConnectRequestMessage : Message
        {
            public byte[] ConnectRequestData;

            protected override void Read(Reader reader)
            {
                ConnectRequestData = reader.ReadFixedBytes(reader.Length - reader.Position);
            }

            protected override void Write(Writer writer)
            {
                writer.WriteFixedBytes(ConnectRequestData);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                if (peer.FindPeer(address) == null)
                {
                    PeerData peerData = new(peer, address);
                    if (peer.ConnectRequest != null)
                    {
                        peer.ConnectRequest(new PeerPacket(peerData, ConnectRequestData));
                    }
                    else
                    {
                        peer.AcceptConnect(peerData);
                    }
                    return;
                }
                throw new ProtocolViolationException("Connect request ignored, peer already connected.");
            }
        }

        private class ConnectAcceptedMessage : Message
        {
            public IPEndPoint[] PeersAddresses;

            public byte[] ConnectAcceptedData;

            protected override void Read(Reader reader)
            {
                int num = reader.ReadUInt16();
                PeersAddresses = new IPEndPoint[num];
                for (int i = 0; i < num; i++)
                {
                    PeersAddresses[i] = reader.ReadIPEndPoint();
                }
                ConnectAcceptedData = reader.ReadFixedBytes(reader.Length - reader.Position);
            }

            protected override void Write(Writer writer)
            {
                int num = Math.Min(PeersAddresses.Length, 65535);
                writer.WriteUInt16((ushort)num);
                for (int i = 0; i < num; i++)
                {
                    writer.WriteIPEndPoint(PeersAddresses[i]);
                }
                writer.WriteFixedBytes(ConnectAcceptedData);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                if (peer.ConnectedTo != null)
                {
                    throw new ProtocolViolationException("Unexpected connection accept ignored, peer already connected.");
                }
                if (!object.Equals(peer.ConnectingTo, address))
                {
                    throw new ProtocolViolationException($"Unexpected connection accept from {address} ignored.");
                }
                peer.ConnectedTo = new PeerData(peer, address);
                peer.ConnectingTo = null;
                peer.PeersByAddress.Clear();
                IPEndPoint[] peersAddresses = PeersAddresses;
                foreach (IPEndPoint iPEndPoint in peersAddresses)
                {
                    peer.PeersByAddress.Add(iPEndPoint, new PeerData(peer, iPEndPoint));
                }
                peer.ConnectAccepted?.Invoke(new PeerPacket(peer.ConnectedTo, ConnectAcceptedData));
            }
        }

        private class ConnectRefusedMessage : Message
        {
            public byte[] ConnectRefusedData;

            protected override void Read(Reader reader)
            {
                ConnectRefusedData = reader.ReadFixedBytes(reader.Length - reader.Position);
            }

            protected override void Write(Writer writer)
            {
                writer.WriteFixedBytes(ConnectRefusedData);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                if (peer.ConnectedTo != null || !object.Equals(peer.ConnectingTo, address))
                {
                    throw new ProtocolViolationException("Unexpected connection refuse ignored.");
                }
                peer.ConnectingTo = null;
                peer.ConnectRefused?.Invoke(new Packet(address, ConnectRefusedData));
            }
        }

        private class DisconnectRequestMessage : Message
        {
            protected override void Read(Reader reader)
            {
            }

            protected override void Write(Writer writer)
            {
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                PeerData peerData = peer.FindPeer(address);
                if (peerData == null)
                {
                    return;
                }
                peer.PeersByAddress.Remove(address);
                if (peer.Settings.SendPeerConnectDisconnectNotifications)
                {
                    foreach (PeerData value in peer.PeersByAddress.Values)
                    {
                        peer.InternalSend(value.Address, DeliveryMode.ReliableSequenced, new PeerDisconnectedMessage
                        {
                            PeerAddress = address
                        });
                    }
                }
                peer.PeerDisconnected?.Invoke(peerData);
            }
        }

        private class DisconnectedMessage : Message
        {
            protected override void Read(Reader reader)
            {
            }

            protected override void Write(Writer writer)
            {
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                if (peer.ConnectedTo != null)
                {
                    peer.InternalDisconnect();
                }
            }
        }

        private class PeerConnectedMessage : Message
        {
            public IPEndPoint PeerAddress;

            protected override void Read(Reader reader)
            {
                PeerAddress = reader.ReadIPEndPoint();
            }

            protected override void Write(Writer writer)
            {
                writer.WriteIPEndPoint(PeerAddress);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                if (peer.ConnectedTo != null)
                {
                    if (peer.FindPeer(address) != null)
                    {
                        throw new ProtocolViolationException("Peer connection notification ignored, peer already connected.");
                    }
                    PeerData peerData = new(peer, PeerAddress);
                    peer.PeersByAddress.Add(PeerAddress, peerData);
                    peer.PeerConnected?.Invoke(peerData);
                }
            }
        }

        private class PeerDisconnectedMessage : Message
        {
            public IPEndPoint PeerAddress;

            protected override void Read(Reader reader)
            {
                PeerAddress = reader.ReadIPEndPoint();
            }

            protected override void Write(Writer writer)
            {
                writer.WriteIPEndPoint(PeerAddress);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                if (peer.ConnectedTo != null)
                {
                    PeerData peerData = peer.FindPeer(PeerAddress);
                    if (peerData != null)
                    {
                        peer.PeersByAddress.Remove(PeerAddress);
                        peer.PeerDisconnected?.Invoke(peerData);
                    }
                }
            }
        }

        private class KeepAliveRequestMessage : Message
        {
            public double RequestSendTime;

            protected override void Read(Reader reader)
            {
                RequestSendTime = reader.ReadDouble();
            }

            protected override void Write(Writer writer)
            {
                writer.WriteDouble(RequestSendTime);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                PeerData peerData = ((peer.ConnectedTo != null && object.Equals(address, peer.ConnectedTo.Address)) ? peer.ConnectedTo : peer.FindPeer(address));
                if (peerData != null)
                {
                    peerData.LastKeepAliveReceiveTime = Comm.GetTime();
                    peer.InternalSend(address, DeliveryMode.Unreliable, new KeepAliveResponseMessage
                    {
                        RequestSendTime = RequestSendTime
                    });
                }
            }
        }

        private class KeepAliveResponseMessage : Message
        {
            public double RequestSendTime;

            protected override void Read(Reader reader)
            {
                RequestSendTime = reader.ReadDouble();
            }

            protected override void Write(Writer writer)
            {
                writer.WriteDouble(RequestSendTime);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                PeerData peerData = ((peer.ConnectedTo != null && object.Equals(address, peer.ConnectedTo.Address)) ? peer.ConnectedTo : peer.FindPeer(address));
                if (peerData != null)
                {
                    double time = Comm.GetTime();
                    peerData.NextKeepAliveSendTime = time + (double)peer.Settings.KeepAlivePeriod;
                    peerData.Ping = (float)(time - RequestSendTime);
                }
            }
        }

        private class DataMessage : Message
        {
            public byte[] Bytes;

            protected override void Read(Reader reader)
            {
                Bytes = reader.ReadFixedBytes(reader.Length - reader.Position);
            }

            protected override void Write(Writer writer)
            {
                writer.WriteFixedBytes(Bytes);
            }

            protected override void Handle(Peer peer, IPEndPoint address)
            {
                if (peer.ConnectedTo != null && object.Equals(address, peer.ConnectedTo.Address))
                {
                    peer.DataMessageReceived?.Invoke(new PeerPacket(peer.ConnectedTo, Bytes));
                    return;
                }
                PeerData peerData = peer.FindPeer(address);
                if (peerData != null)
                {
                    peer.DataMessageReceived?.Invoke(new PeerPacket(peerData, Bytes));
                }
            }
        }

        private volatile bool IsDisposed;

        private Alarm Alarm;

        private double ConnectStartTime;

        private List<PeerData> PeersData = new();

        private Dictionary<IPEndPoint, PeerData> PeersByAddress = new();

        public object Lock => Comm.Lock;

        public Comm Comm { get; private set; }

        public PeerSettings Settings { get; } = new PeerSettings();

        public IPEndPoint Address => Comm.Address;

        public PeerData ConnectedTo { get; private set; }

        public IPEndPoint ConnectingTo { get; private set; }

        public IReadOnlyList<PeerData> Peers
        {
            get
            {
                lock (Lock)
                {
                    CheckNotDisposedAndStarted();
                    return PeersByAddress.Values.ToArray();
                }
            }
        }

        public event Action<Exception> Error;

        public event Action<string> Debug;

        public event Action<Packet> PeerDiscoveryRequest;

        public event Action<Packet> PeerDiscovered;

        public event Action<PeerPacket> ConnectRequest;

        public event Action<PeerPacket> ConnectAccepted;

        public event Action<Packet> ConnectRefused;

        public event Action<IPEndPoint> ConnectTimedOut;

        public event Action Disconnected;

        public event Action<PeerData> PeerConnected;

        public event Action<PeerData> PeerDisconnected;

        public event Action<PeerPacket> DataMessageReceived;

        public Peer(int localPort = 0)
            : this(new UdpTransmitter(localPort))
        {
        }

        public Peer(ITransmitter transmitter)
        {
            Comm = new Comm(transmitter);
            Comm.Error += delegate (Exception e)
            {
                InvokeError(e);
            };
            Comm.Received += delegate (Packet packet)
            {
                try
                {
                    if (!IsDisposed)
                    {
                        Message.Handle(this, packet);
                    }
                }
                catch (Exception e)
                {
                    InvokeError(e);
                }
            };
        }

        public void Dispose()
        {
            lock (Lock)
            {
                if (IsDisposed)
                {
                    return;
                }
                IsDisposed = true;
            }
            Alarm?.Dispose();
            Comm?.Dispose();
        }

        public void Start()
        {
            lock (Lock)
            {
                CheckNotDisposed();
                if (Alarm != null)
                {
                    throw new InvalidOperationException("Peer is already started.");
                }
                Comm.Start();
                Alarm = new Alarm(AlarmFunction);
                Alarm.Error += delegate (Exception e)
                {
                    InvokeError(e);
                };
                Alarm.Set(0.0);
            }
        }

        public PeerData FindPeer(IPEndPoint address)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                PeerData peerData;
                return PeersByAddress.TryGetValue(address, out peerData) ? peerData : null;
            }
        }

        public void DiscoverLocalPeers(int peerPort, byte[] discoveryQueryData = null)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                if (Comm.Address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    InternalSend(new IPEndPoint(UdpTransmitter.IPV6BroadcastAddress, peerPort), DeliveryMode.Raw, new DiscoveryRequestMessage
                    {
                        DiscoveryRequestData = (discoveryQueryData ?? new byte[0])
                    });
                }
                else
                {
                    InternalSend(new IPEndPoint(UdpTransmitter.IPV4BroadcastAddress, peerPort), DeliveryMode.Raw, new DiscoveryRequestMessage
                    {
                        DiscoveryRequestData = (discoveryQueryData ?? new byte[0])
                    });
                }
            }
        }

        public void DiscoverPeer(IPEndPoint address, byte[] discoveryQueryData = null)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                InternalSend(address, DeliveryMode.Raw, new DiscoveryRequestMessage
                {
                    DiscoveryRequestData = (discoveryQueryData ?? new byte[0])
                });
            }
        }

        public void Connect(IPEndPoint address, byte[] connectRequestData = null)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                if (ConnectedTo != null)
                {
                    throw new InvalidOperationException("Peer is already connected.");
                }
                if (ConnectingTo != null)
                {
                    throw new InvalidOperationException("Peer is already connecting.");
                }
                ConnectingTo = address;
                ConnectStartTime = Comm.GetTime();
                InternalSend(address, DeliveryMode.ReliableSequenced, new ConnectRequestMessage
                {
                    ConnectRequestData = (connectRequestData ?? new byte[0])
                });
            }
        }

        public void Disconnect()
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                if (ConnectedTo != null)
                {
                    InternalSend(ConnectedTo.Address, DeliveryMode.ReliableSequenced, new DisconnectRequestMessage());
                    InternalDisconnect();
                }
            }
        }

        public void RespondToDiscovery(IPEndPoint address, DeliveryMode deliveryMode, byte[] discoveryResponseData = null)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                InternalSend(address, deliveryMode, new DiscoveryResponseMessage
                {
                    DiscoveryResponseData = (discoveryResponseData ?? new byte[0])
                });
            }
        }

        public void AcceptConnect(PeerData peerData, byte[] connectAcceptedData = null)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                if (peerData == null || peerData == ConnectedTo || peerData.Address == ConnectingTo || Peers.Contains(peerData))
                {
                    throw new ArgumentException("peerData");
                }
                InternalSend(peerData.Address, DeliveryMode.ReliableSequenced, new ConnectAcceptedMessage
                {
                    ConnectAcceptedData = (connectAcceptedData ?? new byte[0]),
                    PeersAddresses = (Settings.SendPeerConnectDisconnectNotifications ? PeersByAddress.Keys.ToArray() : new IPEndPoint[0])
                });
                if (Settings.SendPeerConnectDisconnectNotifications && PeersByAddress.Count > 0)
                {
                    foreach (PeerData value in PeersByAddress.Values)
                    {
                        InternalSend(value.Address, DeliveryMode.ReliableSequenced, new PeerConnectedMessage
                        {
                            PeerAddress = peerData.Address
                        });
                    }
                }
                PeersByAddress.Add(peerData.Address, peerData);
                this.PeerConnected?.Invoke(peerData);
            }
        }

        public void RefuseConnect(PeerData peerData, byte[] refuseConnectData = null)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                if (peerData == null || peerData == ConnectedTo || peerData.Address == ConnectingTo || Peers.Contains(peerData))
                {
                    throw new ArgumentException("peerData");
                }
                InternalSend(peerData.Address, DeliveryMode.ReliableSequenced, new ConnectRefusedMessage
                {
                    ConnectRefusedData = (refuseConnectData ?? new byte[0])
                });
            }
        }

        public void SendDataMessage(PeerData peerData, DeliveryMode deliveryMode, byte[] bytes)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                if (peerData == null || (peerData != ConnectedTo && !Peers.Contains(peerData)))
                {
                    throw new ArgumentException("peerData");
                }
                InternalSend(peerData.Address, deliveryMode, new DataMessage
                {
                    Bytes = bytes
                });
            }
        }

        public void SendDataMessages(PeerData peerData, DeliveryMode deliveryMode, IEnumerable<byte[]> bytes)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                if (peerData == null || (peerData != ConnectedTo && !Peers.Contains(peerData)))
                {
                    throw new ArgumentException("peerData");
                }
                IEnumerable<DataMessage> messages = bytes.Select((byte[] b) => new DataMessage
                {
                    Bytes = b
                });
                InternalSend(peerData.Address, deliveryMode, messages);
            }
        }

        public void DisconnectPeer(PeerData peerData)
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                if (!PeersByAddress.ContainsValue(peerData))
                {
                    return;
                }
                InternalSend(peerData.Address, DeliveryMode.ReliableSequenced, new DisconnectedMessage());
                PeersByAddress.Remove(peerData.Address);
                if (Settings.SendPeerConnectDisconnectNotifications)
                {
                    foreach (PeerData value in PeersByAddress.Values)
                    {
                        InternalSend(value.Address, DeliveryMode.ReliableSequenced, new PeerDisconnectedMessage
                        {
                            PeerAddress = peerData.Address
                        });
                    }
                }
                this.PeerDisconnected?.Invoke(peerData);
            }
        }

        public void DisconnectAllPeers()
        {
            lock (Lock)
            {
                CheckNotDisposedAndStarted();
                PeerData[] array = PeersByAddress.Values.ToArray();
                foreach (PeerData peerData in array)
                {
                    DisconnectPeer(peerData);
                }
            }
        }

        private void AlarmFunction()
        {
            lock (Lock)
            {
                if (!IsDisposed)
                {
                    ProcessPeers();
                    float num = 0.25f;
                    float num2 = Math.Min(Settings.KeepAlivePeriod, Math.Min(Settings.KeepAliveResendPeriod, Math.Min(Settings.ConnectTimeOut, Settings.ConnectionLostPeriod)));
                    Alarm.Set(Math.Max(num2 * num, 0.01f));
                }
            }
        }

        private void ProcessPeers()
        {
            PeersData.Clear();
            if (ConnectedTo != null)
            {
                PeersData.Add(ConnectedTo);
            }
            else
            {
                PeersData.AddRange(PeersByAddress.Values);
            }
            double time = Comm.GetTime();
            foreach (PeerData peersDatum in PeersData)
            {
                double num = time - peersDatum.LastKeepAliveReceiveTime;
                if (num >= (double)Settings.ConnectionLostPeriod)
                {
                    if (peersDatum == ConnectedTo)
                    {
                        InvokeError(new KeepAliveTimeoutException($"Server {peersDatum.Address} did not respond for {num:0.0}s, disconnecting."));
                        Disconnect();
                    }
                    else
                    {
                        InvokeError(new KeepAliveTimeoutException($"Peer {peersDatum.Address} did not respond for {num:0.0}s, disconnecting."));
                        DisconnectPeer(peersDatum);
                    }
                }
                else if (time >= peersDatum.NextKeepAliveSendTime)
                {
                    InternalSend(peersDatum.Address, DeliveryMode.Unreliable, new KeepAliveRequestMessage
                    {
                        RequestSendTime = time
                    });
                    peersDatum.NextKeepAliveSendTime = time + (double)Settings.KeepAliveResendPeriod;
                }
            }
            if (ConnectingTo != null && time - ConnectStartTime >= (double)Settings.ConnectTimeOut)
            {
                IPEndPoint connectingTo = ConnectingTo;
                ConnectingTo = null;
                try
                {
                    this.ConnectTimedOut?.Invoke(connectingTo);
                }
                catch (Exception e)
                {
                    InvokeError(e);
                }
            }
        }

        private void InternalDisconnect()
        {
            ConnectedTo = null;
            ConnectingTo = null;
            PeersByAddress.Clear();
            this.Disconnected?.Invoke();
        }

        private void InternalSend(IPEndPoint address, DeliveryMode deliveryMode, Message message)
        {
            byte[] bytes = Message.Write(message);
            Comm.Send(address, deliveryMode, bytes);
        }

        private void InternalSend(IPEndPoint address, DeliveryMode deliveryMode, IEnumerable<Message> messages)
        {
            List<byte[]> list = new();
            foreach (Message message in messages)
            {
                list.Add(Message.Write(message));
            }
            Comm.Send(address, deliveryMode, list);
        }

        private void CheckNotDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("Peer");
            }
        }

        private void CheckNotDisposedAndStarted()
        {
            CheckNotDisposed();
            if (Alarm == null)
            {
                throw new InvalidOperationException("Peer is not started.");
            }
        }

        private void InvokeError(Exception e)
        {
            this.Error?.Invoke(e);
        }

        [Conditional("DEBUG")]
        private void InvokeDebug(string format, params object[] args)
        {
            if (this.Debug != null)
            {
                this.Debug?.Invoke(string.Format(format, args));
            }
        }
    }


}



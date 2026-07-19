using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;

namespace Comms;

public class Comm
{
    private class Connection
    {
        public Guid OurGuid = Guid.NewGuid();

        public Guid TheirGuid;

        public bool InitAckReceived;

        public bool InitAckConfirmed;

        public Dictionary<uint, UnackedPacket> UnackedPackets = new();

        public Dictionary<uint, MessageParts> MessageParts = new();

        public Dictionary<uint, byte[]> SequencedBytes = new();

        public List<uint> PacketIdsToAck = new();

        public HashSet<uint> ReceivedPacketIdsOld = new();

        public HashSet<uint> ReceivedPacketIdsCurrent = new();

        public double LastReceivedPacketsIdsSwitchTime;

        public uint? NextUnreliableReceiveSequenceIndex;

        public uint NextUnreliableSendSequenceIndex;

        public uint? NextReliableReceiveSequenceIndex;

        public uint NextReliableSendSequenceIndex;

        public double LastInitAckSendTime = double.MinValue;

        public double LastSendTime = double.MinValue;

        public double LastReceiveTime = double.MinValue;

        public double SmoothedRoundTripTime = 0.1;

        public void NewTheirGuid(Guid theirGuid)
        {
            bool isReplacementSession = TheirGuid != Guid.Empty && TheirGuid != theirGuid;
            if (isReplacementSession)
            {
                OurGuid = Guid.NewGuid();
                InitAckReceived = false;
                InitAckConfirmed = false;
                UnackedPackets.Clear();
                NextUnreliableSendSequenceIndex = 0u;
                NextReliableSendSequenceIndex = 0u;
            }
            TheirGuid = theirGuid;
            MessageParts.Clear();
            SequencedBytes.Clear();
            PacketIdsToAck.Clear();
            ReceivedPacketIdsOld.Clear();
            ReceivedPacketIdsCurrent.Clear();
            NextUnreliableReceiveSequenceIndex = 0u;
            NextReliableReceiveSequenceIndex = 0u;
            LastInitAckSendTime = double.MinValue;
        }
    }

    private class MessageParts
    {
        public double LastReceiveTime = double.MinValue;

        public int LastPartIndex = -1;

        public Dictionary<int, byte[]> Parts = new();
    }

    private enum PacketType : byte
    {
        RawData = 1,
        UnreliableData,
        ReliableData,
        DataAck,
        InitAck
    }

    private struct PacketHeader
    {
        public bool IsInvalid;

        public PacketType PacketType;

        public bool IsConnectionInit;

        public uint PacketId;

        public Guid InitGuid;

        public static PacketHeader Read(Reader reader)
        {
            PacketHeader result = default;
            if (reader.Length - reader.Position >= 1)
            {
                byte b = reader.ReadByte();
                result.PacketType = (PacketType)(b & 0xFu);
                if (result.PacketType == PacketType.RawData)
                {
                    return result;
                }
                if (result.PacketType == PacketType.UnreliableData || result.PacketType == PacketType.ReliableData)
                {
                    result.IsConnectionInit = (b & 0x80) != 0;
                    int num = (result.IsConnectionInit ? 20 : 4);
                    if (reader.Length - reader.Position >= num)
                    {
                        if (result.IsConnectionInit)
                        {
                            result.InitGuid = new Guid(reader.ReadFixedBytes(16));
                        }
                        result.PacketId = reader.ReadUInt32();
                        return result;
                    }
                }
                else if (result.PacketType == PacketType.DataAck)
                {
                    if (reader.Length - reader.Position >= 4)
                    {
                        return result;
                    }
                }
                else if (result.PacketType == PacketType.InitAck && reader.Length - reader.Position == 16)
                {
                    result.InitGuid = new Guid(reader.ReadFixedBytes(16));
                    return result;
                }
            }
            result.IsInvalid = true;
            return result;
        }

        public static void WriteRaw(Writer writer)
        {
            writer.WriteByte(1);
        }

        public static void WriteData(Writer writer, Guid? initGuid, uint packetId, bool requiresAck)
        {
            byte b = (byte)(requiresAck ? 3 : 2);
            if (initGuid.HasValue)
            {
                b = (byte)(b | 0x80u);
            }
            writer.WriteByte(b);
            if (initGuid.HasValue)
            {
                writer.WriteFixedBytes(initGuid.Value.ToByteArray());
            }
            writer.WriteUInt32(packetId);
        }

        public static void WriteDataAck(Writer writer)
        {
            writer.WriteByte(4);
        }

        public static void WriteInitAck(Writer writer, Guid initGuid)
        {
            writer.WriteByte(5);
            writer.WriteFixedBytes(initGuid.ToByteArray());
        }
    }

    private struct MessagePartHeader
    {
        public bool IsInvalid;

        public uint MessageId;

        public uint? SequenceIndex;

        public int PartIndex;

        public bool IsFinalPart;

        public int DataSize;

        public static MessagePartHeader Read(Reader reader)
        {
            MessagePartHeader result = default;
            try
            {
                byte b = reader.ReadByte();
                result.MessageId = ((((uint)b & (true ? 1u : 0u)) != 0 || (b & 4) == 0) ? reader.ReadUInt32() : 0u);
                result.SequenceIndex = (((b & 8u) != 0) ? new uint?(reader.ReadUInt32()) : null);
                result.PartIndex = ((((uint)b & (true ? 1u : 0u)) != 0) ? reader.ReadPackedInt32() : 0);
                result.DataSize = (((b & 2u) != 0) ? reader.ReadPackedInt32() : (-1));
                result.IsFinalPart = (b & 4) != 0;
            }
            catch (Exception)
            {
                result.IsInvalid = true;
            }
            return result;
        }

        public static void Write(Writer writer, uint messageId, uint? sequenceIndex, int partIndex, bool isFinalPart, int dataSize)
        {
            byte b = 0;
            if (partIndex != 0)
            {
                b = (byte)(b | 1u);
            }
            if (dataSize >= 0)
            {
                b = (byte)(b | 2u);
            }
            if (isFinalPart)
            {
                b = (byte)(b | 4u);
            }
            if (sequenceIndex.HasValue)
            {
                b = (byte)(b | 8u);
            }
            writer.WriteByte(b);
            if (partIndex != 0 || !isFinalPart)
            {
                writer.WriteUInt32(messageId);
            }
            if (sequenceIndex.HasValue)
            {
                writer.WriteUInt32(sequenceIndex.Value);
            }
            if (partIndex != 0)
            {
                writer.WritePackedInt32(partIndex);
            }
            if (dataSize >= 0)
            {
                writer.WritePackedInt32(dataSize);
            }
        }
    }

    private class UnackedPacket
    {
        public double LastSendTime;

        public int SendCount;

        public Packet Packet;
    }

    private volatile bool IsDisposed;

    private Alarm Alarm;

    private uint NextPacketId;

    private uint NextMessageId;

    private Dictionary<IPEndPoint, Connection> Connections = new();

    private List<uint> ToRemoveUInt = new();

    private List<IPEndPoint> ToRemoveEndpoint = new();

    private static long StartTimestamp = Stopwatch.GetTimestamp();

    public CommSettings Settings { get; private set; } = new CommSettings();


    public ITransmitter Transmitter { get; private set; }

    public IPEndPoint Address => Transmitter.Address;

    public object Lock { get; } = new object();


    public event Action<Packet> Received;

    public event Action<Exception> Error;

    public event Action<string> Debug;

    public Comm(int localPort = 0)
        : this(new UdpTransmitter(localPort))
    {
    }

    public Comm(ITransmitter transmitter)
    {
        Transmitter = transmitter ?? throw new ArgumentNullException(nameof(transmitter));
    }

    public void Start()
    {
        lock (Lock)
        {
            CheckNotDisposed();
            if (Alarm != null)
            {
                throw new InvalidOperationException("Comm is already started.");
            }
            Transmitter.Error += delegate (Exception e)
            {
                InvokeError(e);
            };
            Transmitter.PacketReceived += delegate (Packet packet)
            {
                lock (Lock)
                {
                    if (!IsDisposed)
                    {
                        ProcessReceivedPacket(packet);
                    }
                }
            };
            Alarm = new Alarm(AlarmFunction);
            Alarm.Error += delegate (Exception e)
            {
                InvokeError(e);
            };
            Alarm.Set(0.0);
        }
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
        Transmitter?.Dispose();
    }

    public void Send(IPEndPoint address, DeliveryMode deliveryMode, byte[] bytes)
    {
        Send(address, deliveryMode, new byte[1][] { bytes });
    }

    public void Send(IPEndPoint address, DeliveryMode deliveryMode, IEnumerable<byte[]> bytes)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            if ((object.Equals(address.Address, UdpTransmitter.IPV4BroadcastAddress) || object.Equals(address.Address, UdpTransmitter.IPV6BroadcastAddress)) && deliveryMode != 0)
            {
                throw new InvalidOperationException("Broadcast messages must use DeliveryMode.Raw");
            }
            SendMessages(address, bytes.ToArray(), deliveryMode);
        }
    }

    public int GetUnackedPacketsCount(IPEndPoint address)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            if (!Connections.TryGetValue(address, out var value))
            {
                return 0;
            }
            return value.UnackedPackets.Count;
        }
    }

    // Source: Comms/Comms/Comm.cs:SendMessages
    // A reconnect starts a new reliable session so stale fragments and sequence gaps cannot block
    // the new connect request.
    public void ResetConnection(IPEndPoint address)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            Connections.Remove(address);
        }
    }

    public void UpdateRoundTripTime(IPEndPoint address, double roundTripTime)
    {
        lock (Lock)
        {
            if (roundTripTime <= 0.0 || !Connections.TryGetValue(address, out Connection connection))
                return;
            connection.SmoothedRoundTripTime = 0.875 * connection.SmoothedRoundTripTime +
                0.125 * roundTripTime;
        }
    }

    public static double GetTime()
    {
        return (double)(Stopwatch.GetTimestamp() - StartTimestamp) / (double)Stopwatch.Frequency;
    }

    private void AlarmFunction()
    {
        lock (Lock)
        {
            if (!IsDisposed)
            {
                ProcessConnections();
                float num = 0.25f;
                Alarm.Set(num * Settings.ResendPeriods[0]);
            }
        }
    }

    private void ProcessReceivedPacket(Packet packet)
    {
        double time = GetTime();
        Reader reader = new(packet.Bytes);
        PacketHeader packetHeader = PacketHeader.Read(reader);
        if (packetHeader.IsInvalid)
        {
            InvokeError(new ProtocolViolationException($"Invalid packet header received from {packet.Address.ToString()}, dropping packet"));
            return;
        }
        if (packetHeader.PacketType == PacketType.RawData)
        {
            int count = reader.Length - reader.Position;
            byte[] bytes = reader.ReadFixedBytes(count);
            InvokeReceived(packet.Address, bytes);
            return;
        }
        if (!Connections.TryGetValue(packet.Address, out var value))
        {
            value = new Connection();
            Connections.Add(packet.Address, value);
        }
        value.LastReceiveTime = time;
        if (packetHeader.PacketType == PacketType.UnreliableData || packetHeader.PacketType == PacketType.ReliableData)
        {
            if (packetHeader.IsConnectionInit)
            {
                if (value.TheirGuid == Guid.Empty || packetHeader.InitGuid != value.TheirGuid)
                {
                    value.NewTheirGuid(packetHeader.InitGuid);
                }
                if (!value.InitAckConfirmed && time - value.LastInitAckSendTime >= (double)Settings.ResendPeriods[0])
                {
                    SendInitAckPacket(packet.Address, packetHeader.InitGuid, value);
                }
            }
            else
            {
                value.InitAckConfirmed = true;
            }
            bool flag = packetHeader.PacketType == PacketType.ReliableData;
            if (flag)
            {
                value.PacketIdsToAck.Add(packetHeader.PacketId);
            }
            if (value.ReceivedPacketIdsOld.Contains(packetHeader.PacketId))
            {
                value.ReceivedPacketIdsCurrent.Add(packetHeader.PacketId);
            }
            else
            {
                if (value.ReceivedPacketIdsCurrent.Contains(packetHeader.PacketId))
                {
                    return;
                }
                value.ReceivedPacketIdsCurrent.Add(packetHeader.PacketId);
                while (reader.Position < reader.Length)
                {
                    MessagePartHeader messagePartHeader = MessagePartHeader.Read(reader);
                    if (messagePartHeader.IsInvalid)
                    {
                        InvokeError(new ProtocolViolationException($"Invalid message part header received from {packet.Address.ToString()}, dropping rest of the packet"));
                        break;
                    }
                    int count2 = ((messagePartHeader.DataSize >= 0) ? messagePartHeader.DataSize : (reader.Length - reader.Position));
                    byte[] array = reader.ReadFixedBytes(count2);
                    if (messagePartHeader.PartIndex == 0 && messagePartHeader.IsFinalPart)
                    {
                        ProcessReceivedMessage(packet.Address, value, messagePartHeader, array, flag);
                        continue;
                    }
                    if (!value.MessageParts.TryGetValue(messagePartHeader.MessageId, out var value2))
                    {
                        value2 = new MessageParts();
                        value.MessageParts.Add(messagePartHeader.MessageId, value2);
                    }
                    value2.LastReceiveTime = time;
                    value2.Parts[messagePartHeader.PartIndex] = array;
                    if (messagePartHeader.IsFinalPart)
                    {
                        value2.LastPartIndex = messagePartHeader.PartIndex;
                    }
                    if (value2.LastPartIndex < 0 || value2.Parts.Count != value2.LastPartIndex + 1)
                    {
                        continue;
                    }
                    bool flag2 = true;
                    int num = 0;
                    for (int i = 0; i <= value2.LastPartIndex; i++)
                    {
                        if (!value2.Parts.TryGetValue(i, out var value3))
                        {
                            flag2 = false;
                            break;
                        }
                        num += value3.Length;
                    }
                    if (flag2)
                    {
                        byte[] array2 = new byte[num];
                        int j = 0;
                        int num2 = 0;
                        for (; j <= value2.LastPartIndex; j++)
                        {
                            byte[] array3 = value2.Parts[j];
                            Array.Copy(array3, 0, array2, num2, array3.Length);
                            num2 += array3.Length;
                        }
                        ProcessReceivedMessage(packet.Address, value, messagePartHeader, array2, flag);
                    }
                    value.MessageParts.Remove(messagePartHeader.MessageId);
                }
            }
        }
        else if (packetHeader.PacketType == PacketType.DataAck)
        {
            while (reader.Length - reader.Position >= 4)
            {
                uint key = reader.ReadUInt32();
                if (value.UnackedPackets.TryGetValue(key, out UnackedPacket acknowledgedPacket) &&
                    acknowledgedPacket.SendCount == 1)
                {
                    double sample = time - acknowledgedPacket.LastSendTime;
                    if (sample > 0.0)
                        value.SmoothedRoundTripTime = 0.875 * value.SmoothedRoundTripTime +
                            0.125 * sample;
                }
                value.UnackedPackets.Remove(key);
            }
        }
        else if (packetHeader.PacketType == PacketType.InitAck)
        {
            if (packetHeader.InitGuid == value.OurGuid)
            {
                value.InitAckReceived = true;
            }
            else
            {
                InvokeError(new ProtocolViolationException($"Invalid InitAck Guid received from {packet.Address.ToString()} (received {packetHeader.InitGuid.ToString()}, expected {value.OurGuid.ToString()}), ignoring"));
            }
        }
        else
        {
            InvokeError(new ProtocolViolationException($"Invalid packet type {(int)packetHeader.PacketType} received from {packet.Address.ToString()}, ignoring"));
        }
    }

    private void ProcessReceivedMessage(IPEndPoint address, Connection connection, MessagePartHeader messagePartHeader, byte[] bytes, bool isReliable)
    {
        if (messagePartHeader.SequenceIndex.HasValue)
        {
            if (isReliable)
            {
                if (!connection.NextReliableReceiveSequenceIndex.HasValue || messagePartHeader.SequenceIndex.Value == connection.NextReliableReceiveSequenceIndex)
                {
                    connection.NextReliableReceiveSequenceIndex = messagePartHeader.SequenceIndex.Value + 1;
                    InvokeReceived(address, bytes);
                    byte[] value;
                    while (connection.SequencedBytes.TryGetValue(connection.NextReliableReceiveSequenceIndex.Value, out value))
                    {
                        connection.SequencedBytes.Remove(connection.NextReliableReceiveSequenceIndex.Value);
                        connection.NextReliableReceiveSequenceIndex++;
                        InvokeReceived(address, value);
                    }
                }
                else
                {
                    connection.SequencedBytes.Add(messagePartHeader.SequenceIndex.Value, bytes);
                }
            }
            else if (!connection.NextUnreliableReceiveSequenceIndex.HasValue || CompareSequenceNumbers(messagePartHeader.SequenceIndex.Value, connection.NextUnreliableReceiveSequenceIndex.Value) >= 0)
            {
                connection.NextUnreliableReceiveSequenceIndex = messagePartHeader.SequenceIndex.Value + 1;
                InvokeReceived(address, bytes);
            }
        }
        else
        {
            InvokeReceived(address, bytes);
        }
    }

    private void ProcessConnections()
    {
        double time = GetTime();
        foreach (KeyValuePair<IPEndPoint, Connection> connection in Connections)
        {
            IPEndPoint key = connection.Key;
            Connection value = connection.Value;
            if (value.PacketIdsToAck.Count > 0)
            {
                SendDataAckPackets(key, value.PacketIdsToAck, value);
                value.PacketIdsToAck.Clear();
            }
            foreach (KeyValuePair<uint, UnackedPacket> unackedPacket in value.UnackedPackets)
            {
                float num = GetResendPeriod(value, unackedPacket.Value.SendCount);
                if (unackedPacket.Value.SendCount - 1 >= Settings.MaxResends)
                {
                    ToRemoveUInt.Add(unackedPacket.Key);
                }
                else if (time >= unackedPacket.Value.LastSendTime + (double)num)
                {
                    SendPacket(unackedPacket.Value.Packet, value);
                    unackedPacket.Value.LastSendTime = time;
                    unackedPacket.Value.SendCount++;
                }
            }
            foreach (uint item in ToRemoveUInt)
            {
                value.UnackedPackets.Remove(item);
            }
            ToRemoveUInt.Clear();
            foreach (KeyValuePair<uint, MessageParts> messagePart in value.MessageParts)
            {
                if (time - messagePart.Value.LastReceiveTime > (double)((float)Settings.MaxResends * Settings.ResendPeriods[Settings.ResendPeriods.Length - 1]))
                {
                    ToRemoveUInt.Add(messagePart.Key);
                }
            }
            foreach (uint item2 in ToRemoveUInt)
            {
                value.MessageParts.Remove(item2);
            }
            ToRemoveUInt.Clear();
            if (time >= value.LastReceivedPacketsIdsSwitchTime + (double)Settings.DuplicatePacketsDetectionTime)
            {
                value.ReceivedPacketIdsOld.Clear();
                HashSet<uint> receivedPacketIdsOld = value.ReceivedPacketIdsOld;
                value.ReceivedPacketIdsOld = value.ReceivedPacketIdsCurrent;
                value.ReceivedPacketIdsCurrent = receivedPacketIdsOld;
                value.LastReceivedPacketsIdsSwitchTime = time;
            }
            if (time - value.LastSendTime >= (double)Settings.IdleTime && time - value.LastReceiveTime >= (double)Settings.IdleTime)
            {
                ToRemoveEndpoint.Add(key);
            }
        }
        foreach (IPEndPoint item3 in ToRemoveEndpoint)
        {
            Connections.Remove(item3);
        }
        ToRemoveEndpoint.Clear();
    }

    private float GetResendPeriod(Connection connection, int sendCount)
    {
        // Source: Comms/Comms/Comm.cs:ProcessConnections
        // RTT is sampled from reliable ACKs and keep-alives. A small bounded backoff avoids both
        // the old 500ms recovery pause and overly aggressive retransmission on remote LAN links.
        double basePeriod = Math.Clamp(connection.SmoothedRoundTripTime * 1.5,
            Settings.MinimumResendPeriod, Settings.MaximumResendPeriod);
        double backoff = Math.Pow(1.25, Math.Min(Math.Max(sendCount - 1, 0), 3));
        return (float)Math.Clamp(basePeriod * backoff,
            Settings.MinimumResendPeriod, Settings.MaximumResendPeriod);
    }

    private void SendMessages(IPEndPoint address, byte[][] bytes, DeliveryMode deliveryMode)
    {
        if (deliveryMode == DeliveryMode.Raw)
        {
            foreach (byte[] bytes2 in bytes)
            {
                Writer writer = new();
                PacketHeader.WriteRaw(writer);
                writer.WriteFixedBytes(bytes2);
                Transmitter.SendPacket(new Packet(address, writer.GetBytes()));
            }
            return;
        }
        if (!Connections.TryGetValue(address, out var value))
        {
            value = new Connection();
            Connections.Add(address, value);
        }
        Writer writer2 = null;
        byte[] array = null;
        int num = 0;
        uint packetId = 0u;
        uint messageId = 0u;
        uint? sequenceIndex = 0u;
        int num2 = 0;
        int num3 = 0;
        while (true)
        {
            if (array == null)
            {
                if (num3 >= bytes.Length)
                {
                    break;
                }
                array = bytes[num3++];
                num = 0;
                messageId = NextMessageId++;
                sequenceIndex = deliveryMode switch
                {
                    DeliveryMode.UnreliableSequenced => value.NextUnreliableSendSequenceIndex++,
                    DeliveryMode.ReliableSequenced => value.NextReliableSendSequenceIndex++,
                    _ => null,
                };
                num2 = 0;
            }
            if (writer2 == null)
            {
                writer2 = new Writer();
                packetId = NextPacketId;
                NextPacketId++;
                Guid? initGuid = (value.InitAckReceived ? null : new Guid?(value.OurGuid));
                PacketHeader.WriteData(writer2, initGuid, packetId, deliveryMode == DeliveryMode.Reliable || deliveryMode == DeliveryMode.ReliableSequenced);
            }
            int position = writer2.Position;
            int num4 = array.Length - num;
            MessagePartHeader.Write(writer2, messageId, sequenceIndex, num2, isFinalPart: true, num4);
            if (writer2.Position + num4 <= Transmitter.MaxPacketSize)
            {
                writer2.WriteFixedBytes(array, num, num4);
                array = null;
                continue;
            }
            writer2.Length = position;
            MessagePartHeader.Write(writer2, messageId, sequenceIndex, num2, isFinalPart: true, -1);
            if (writer2.Position + num4 <= Transmitter.MaxPacketSize)
            {
                writer2.WriteFixedBytes(array, num, num4);
                array = null;
                continue;
            }
            writer2.Length = position;
            MessagePartHeader.Write(writer2, messageId, sequenceIndex, num2, isFinalPart: false, -1);
            num4 = Transmitter.MaxPacketSize - writer2.Position;
            if (num4 > 0)
            {
                writer2.WriteFixedBytes(array, num, num4);
                num += num4;
                num2++;
            }
            else
            {
                writer2.Length = position;
            }
            SendDataPacket(address, writer2.GetBytes(), packetId, deliveryMode, value);
            writer2 = null;
        }
        if (writer2 != null && writer2.Position > 0)
        {
            SendDataPacket(address, writer2.GetBytes(), packetId, deliveryMode, value);
        }
    }

    private void SendDataPacket(IPEndPoint address, byte[] bytes, uint packetId, DeliveryMode deliveryMode, Connection connection)
    {
        Packet packet = new(address, bytes);
        if (deliveryMode == DeliveryMode.Reliable || deliveryMode == DeliveryMode.ReliableSequenced)
        {
            connection.UnackedPackets.Add(packetId, new UnackedPacket
            {
                Packet = packet,
                SendCount = 1,
                LastSendTime = GetTime()
            });
        }
        SendPacket(packet, connection);
    }

    private void SendDataAckPackets(IPEndPoint address, List<uint> acks, Connection connection)
    {
        int num = 0;
        while (num < acks.Count)
        {
            Writer writer = new();
            PacketHeader.WriteDataAck(writer);
            int num2 = Transmitter.MaxPacketSize - writer.Position;
            int num3 = Math.Min(val2: acks.Count - num, val1: num2 / 4);
            for (int i = 0; i < num3; i++)
            {
                writer.WriteUInt32(acks[num++]);
            }
            SendPacket(new Packet(address, writer.GetBytes()), connection);
        }
    }

    private void SendInitAckPacket(IPEndPoint address, Guid initGuid, Connection connection)
    {
        connection.LastInitAckSendTime = GetTime();
        Writer writer = new();
        PacketHeader.WriteInitAck(writer, initGuid);
        SendPacket(new Packet(address, writer.GetBytes()), connection);
    }

    private void SendPacket(Packet packet, Connection connection)
    {
        connection.LastSendTime = GetTime();
        Transmitter.SendPacket(packet);
    }

    private void CheckNotDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException("Comm");
        }
    }

    private void CheckNotDisposedAndStarted()
    {
        CheckNotDisposed();
        if (Alarm == null)
        {
            throw new InvalidOperationException("Comm is not started.");
        }
    }

    private void InvokeReceived(IPEndPoint address, byte[] bytes)
    {
        try
        {
            this.Received?.Invoke(new Packet(address, bytes));
        }
        catch (Exception error)
        {
            InvokeError(error);
        }
    }

    private void InvokeError(Exception error)
    {
        this.Error?.Invoke(error);
    }

    [Conditional("DEBUG")]
    private void InvokeDebug(string format, params object[] args)
    {
        if (this.Debug != null)
        {
            this.Debug?.Invoke(string.Format(format, args));
        }
    }

    private static int CompareSequenceNumbers(uint s1, uint s2)
    {
        if (s1 == s2)
        {
            return 0;
        }
        if ((s1 < s2 && s2 - s1 < int.MaxValue) || (s1 > s2 && s1 - s2 > int.MaxValue))
        {
            return -1;
        }
        return 1;
    }
}

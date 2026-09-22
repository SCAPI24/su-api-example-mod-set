using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;

namespace Comms;

public class Comm
{
    private class Connection
    {
        public Guid OurGuid = Guid.NewGuid();

        // Source: Comms/Comms/Comm.cs:Comm.PacketHeader
        // 数据报路径的轻量身份标记（OurGuid 的 32 位派生值）。可靠流上的 16 字节 GUID 只在
        // 握手期出现，而数据报必须在任意时刻都能被归属到唯一一条连接上，所以单独带 4 字节 token。
        public uint OurDatagramToken;

        // Source: Comms/Comms/Comm.cs:Comm.ProcessReceivedPacket
        // 对端数据报带来的 token。由握手期的 GUID 前缀或对端数据报学习到，每个会话只学一次。
        public uint? TheirDatagramToken;

        // Source: Comms/Comms/Comm.cs:Comm.MoveConnection
        // 上一次成功把本连接搬到新地址的时间，用于阻止地址抖动被反复搬迁。
        public double LastDatagramAddressChangeTime = double.MinValue;

        public Guid TheirGuid;

        public Connection()
        {
            RefreshDatagramToken();
        }

        // Source: Comms/Comms/Comm.cs:Comm.DeriveDatagramToken
        public void RefreshDatagramToken()
        {
            OurDatagramToken = DeriveDatagramToken(OurGuid);
        }

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

        // Source: Comms/Comms/Comm.cs:Comm.ProcessConnections
        // Non-zero while a reliable sequenced stream is parked behind a gap that has not been
        // filled yet. A gap that never fills would otherwise block that stream forever.
        public double ReliableSequencedStallStartTime;

        public double LastInitAckSendTime = double.MinValue;

        public double LastSendTime = double.MinValue;

        public double LastReceiveTime = double.MinValue;

        public double SmoothedRoundTripTime = 0.1;

        public double SmoothedPacketLossRate;

        public long ReliableRetryLimitCount;

        // Source: Comms/Comms/Comm.cs:Comm.ProcessConnections
        // Source: Comms/Comms/Comm.cs:Comm.ProcessReceivedPacket
        public void RecordReliablePacketOutcome(bool wasRetransmitted)
        {
            const double smoothingFactor = 1.0 / 64.0;
            double sample = wasRetransmitted ? 1.0 : 0.0;
            SmoothedPacketLossRate += smoothingFactor *
                (sample - SmoothedPacketLossRate);
        }

        public void NewTheirGuid(Guid theirGuid)
        {
            bool isReplacementSession = TheirGuid != Guid.Empty && TheirGuid != theirGuid;
            if (isReplacementSession)
            {
                OurGuid = Guid.NewGuid();
                // Source: Comms/Comms/Comm.cs:Comm.Connection.OurDatagramToken
                // 新会话意味着双方都换了会话身份，旧的对端 token 必须重新学习。
                RefreshDatagramToken();
                TheirDatagramToken = null;
                LastDatagramAddressChangeTime = double.MinValue;
                InitAckReceived = false;
                InitAckConfirmed = false;
                UnackedPackets.Clear();
                NextUnreliableSendSequenceIndex = 0u;
                NextReliableSendSequenceIndex = 0u;
                SmoothedPacketLossRate = 0.0;
                ReliableRetryLimitCount = 0;
            }
            TheirGuid = theirGuid;
            MessageParts.Clear();
            SequencedBytes.Clear();
            PacketIdsToAck.Clear();
            ReceivedPacketIdsOld.Clear();
            ReceivedPacketIdsCurrent.Clear();
            NextUnreliableReceiveSequenceIndex = 0u;
            NextReliableReceiveSequenceIndex = 0u;
            ReliableSequencedStallStartTime = 0.0;
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

        // Source: Comms/Comms/Comm.cs:Comm.PacketHeader.WriteData
        // 0x40 位：本包携带 4 字节数据报 token。它只出现在走数据报的（不可靠）包上，TCP 可靠流
        // 上的包头保持原样，所以这个身份标记不会给世界传输等可靠流量增加任何字节。
        public const byte DatagramTokenFlag = 0x40;

        public bool HasDatagramToken;

        public uint DatagramToken;

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
                    result.HasDatagramToken = (b & DatagramTokenFlag) != 0;
                    int num = (result.IsConnectionInit ? 20 : 4) +
                        (result.HasDatagramToken ? 4 : 0);
                    if (reader.Length - reader.Position >= num)
                    {
                        if (result.IsConnectionInit)
                        {
                            result.InitGuid = new Guid(reader.ReadFixedBytes(16));
                        }
                        if (result.HasDatagramToken)
                        {
                            result.DatagramToken = reader.ReadUInt32();
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

        public static void WriteData(Writer writer, Guid? initGuid, uint? datagramToken,
            uint packetId, bool requiresAck)
        {
            byte b = (byte)(requiresAck ? 3 : 2);
            if (initGuid.HasValue)
            {
                b = (byte)(b | 0x80u);
            }
            if (datagramToken.HasValue)
            {
                b = (byte)(b | DatagramTokenFlag);
            }
            writer.WriteByte(b);
            if (initGuid.HasValue)
            {
                writer.WriteFixedBytes(initGuid.Value.ToByteArray());
            }
            if (datagramToken.HasValue)
            {
                writer.WriteUInt32(datagramToken.Value);
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

        public bool RetryLimitRecorded;

        public Packet Packet;

        public string DiagnosticSource;

        public byte[] DiagnosticPayload;
    }

    // Source: Comms/Comms/Comm.cs:Comm.Start
    // A synchronous in-process transmitter can re-enter PacketReceived while an
    // application callback is being dispatched. Keep a FIFO and drain it without
    // enumerating a collection that callbacks may mutate.
    private sealed class ReceivedPacketDispatchState
    {
        public readonly Queue<Packet> Pending = new();

        public bool IsDispatching;
    }

    private volatile bool IsDisposed;

    private Alarm Alarm;

    private uint NextPacketId;

    private uint NextMessageId;

    private Dictionary<IPEndPoint, Connection> Connections = new();

    // Source: Comms/Comms/Comm.cs:Comm.ProcessReceivedPacket
    // 数据报 token 归属修复的回调，由 Peer 设置。返回 true 表示该 peer 的通信地址已经搬到
    // 这个数据报的实际源地址；返回 false 时保持原有的「未知来源临时连接」行为，绝不丢弃合法
    // 但暂时无法归属的数据报（例如发现应答）。
    internal Func<IPEndPoint, uint, bool> DatagramAddressRepairHandler;

    private List<uint> ToRemoveUInt = new();

    private List<IPEndPoint> ToRemoveEndpoint = new();

    private readonly ThreadLocal<ReceivedPacketDispatchState> ReceivedPacketsToDispatch =
        new(() => new ReceivedPacketDispatchState());

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
                ReceivedPacketDispatchState dispatchState = ReceivedPacketsToDispatch.Value;
                lock (Lock)
                {
                    if (!IsDisposed)
                    {
                        ProcessReceivedPacket(packet);
                    }
                }
                DrainReceivedPackets(dispatchState);
            };
            Alarm = new Alarm(AlarmFunction);
            Alarm.Error += delegate (Exception e)
            {
                InvokeError(e);
            };
            Alarm.Set(0.0);
        }
    }

    private void DrainReceivedPackets(ReceivedPacketDispatchState dispatchState)
    {
        if (dispatchState.IsDispatching)
            return;
        dispatchState.IsDispatching = true;
        try
        {
            while (dispatchState.Pending.Count > 0)
            {
                Packet receivedPacket = dispatchState.Pending.Dequeue();
                // Source: Comms/Comms/Comm.cs:Comm.AlarmFunction
                // Dispatch outside Lock so application handlers cannot block ACK generation.
                InvokeReceived(receivedPacket.Address, receivedPacket.Bytes);
            }
        }
        finally
        {
            dispatchState.IsDispatching = false;
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

    public void Send(IPEndPoint address, DeliveryMode deliveryMode, byte[] bytes,
        string diagnosticSource = null, byte[] diagnosticPayload = null)
    {
        Send(address, deliveryMode, new byte[1][] { bytes }, diagnosticSource, diagnosticPayload);
    }

    public void Send(IPEndPoint address, DeliveryMode deliveryMode, IEnumerable<byte[]> bytes,
        string diagnosticSource = null, byte[] diagnosticPayload = null)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            if ((object.Equals(address.Address, UdpTransmitter.IPV4BroadcastAddress) || object.Equals(address.Address, UdpTransmitter.IPV6BroadcastAddress)) && deliveryMode != 0)
            {
                throw new InvalidOperationException("Broadcast messages must use DeliveryMode.Raw");
            }
            SendMessages(address, bytes.ToArray(), deliveryMode, diagnosticSource, diagnosticPayload);
        }
    }

    public int GetUnackedPacketsCount(IPEndPoint address)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            // Source: Comms/Comms/Comm.cs:Comm.GetUnackedPacketsCount
            // A reliable stream has no unacknowledged packets of its own; what the callers call
            // "in flight" is then the bytes queued inside the transport, so report that too.
            int transportPending = Transmitter.GetPendingSendCount(address);
            if (!Connections.TryGetValue(address, out var value))
            {
                return transportPending;
            }
            return value.UnackedPackets.Count + transportPending;
        }
    }

    // Source: Comms/Comms/Comm.cs:Comm.ProcessConnections
    // A Peer can be removed before Comm reaches its normal idle timeout. Drop the transport
    // state at disconnect time so unacknowledged reliable packets cannot keep retransmitting.
    public void RemoveConnection(IPEndPoint address)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            if (address != null)
                Connections.Remove(address);
        }
    }

    // Source: Comm.ProcessConnections
    public long GetReliableRetryLimitCount(IPEndPoint address)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            return Connections.TryGetValue(address, out var connection)
                ? connection.ReliableRetryLimitCount
                : 0L;
        }
    }

    // Source: Comms/Comm.cs:Comm.UpdateRoundTripTime
    public double GetSmoothedRoundTripTime(IPEndPoint address)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            return Connections.TryGetValue(address, out var connection)
                ? connection.SmoothedRoundTripTime
                : 0.0;
        }
    }

    // Source: Comms/Comms/Comm.cs:Connection.RecordReliablePacketOutcome
    public double GetPacketLossRate(IPEndPoint address)
    {
        lock (Lock)
        {
            CheckNotDisposedAndStarted();
            return Connections.TryGetValue(address, out var connection)
                ? connection.SmoothedPacketLossRate
                : 0.0;
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
            ReceivedPacketsToDispatch.Value.Pending.Enqueue(new Packet(packet.Address, bytes));
            return;
        }
        bool isNewConnection = !Connections.TryGetValue(packet.Address, out Connection value);
        if (isNewConnection)
        {
            value = new Connection();
            Connections.Add(packet.Address, value);
        }
        // Source: Comms/Comms/Comm.cs:Comm.DatagramAddressRepairHandler
        // 带 token 的数据报落在「不是该 token 主人」的地址上时，按 token 把 peer 的通信地址修正到
        // 实际源地址：NAT 改写端口后，可靠流握手里自报的端口是回不来的，只有数据报的源地址可达。
        // token 只由可靠流学到（见下方 FromStream 判断），所以匹配唯一；不是主人的地址（例如只有
        // InitAck 到过的新地址）允许被整体覆盖，覆盖规则见 MoveConnection。
        if (packetHeader.HasDatagramToken &&
            !IsDatagramTokenOwner(packet.Address, packetHeader.DatagramToken) &&
            DatagramAddressRepairHandler != null &&
            DatagramAddressRepairHandler(packet.Address, packetHeader.DatagramToken) &&
            Connections.TryGetValue(packet.Address, out Connection repaired))
        {
            value = repaired;
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
                // Source: Comms/Comms/Comm.cs:Comm.DeriveDatagramToken
                // 数据报 token 与握手 GUID 是同一个会话身份（32 位派生值）。只有可靠流上收到的
                // 握手包才算可信来源：数据报既可能来自被 NAT 改写的地址，也可能被伪造；若允许它
                // 学习 token，错误地址上的临时连接会抢走 token，使地址修复永久失效（已实测复现）。
                if (packet.FromStream)
                {
                    value.TheirDatagramToken ??= DeriveDatagramToken(packetHeader.InitGuid);
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
            // Source: Comms/Comms/ITransmitter.cs:ITransmitter.IsReliableStream
            // A reliable stream never loses a packet, so answering with ACKs would only add
            // traffic on the datagram path.
            if (flag && !Transmitter.IsReliableStream)
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
                if (value.UnackedPackets.TryGetValue(key, out UnackedPacket acknowledgedPacket))
                {
                    if (acknowledgedPacket.SendCount == 1)
                    {
                        double sample = time - acknowledgedPacket.LastSendTime;
                        if (sample > 0.0)
                            value.SmoothedRoundTripTime = 0.875 * value.SmoothedRoundTripTime +
                                0.125 * sample;
                    }
                    value.RecordReliablePacketOutcome(
                        acknowledgedPacket.SendCount > 1);
                    value.UnackedPackets.Remove(key);
                }
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
                // Source: Comms/Comms/Comm.cs:Comm.RecoverStalledReliableSequence
                // A gap that never fills would park this stream forever, so skip it once it
                // outlives the stall timeout and resume from the oldest buffered message.
                RecoverStalledReliableSequence(connection, address);
                if (!connection.NextReliableReceiveSequenceIndex.HasValue || messagePartHeader.SequenceIndex.Value == connection.NextReliableReceiveSequenceIndex)
                {
                    connection.NextReliableReceiveSequenceIndex = messagePartHeader.SequenceIndex.Value + 1;
                    ReceivedPacketsToDispatch.Value.Pending.Enqueue(new Packet(address, bytes));
                    byte[] value;
                    while (connection.SequencedBytes.TryGetValue(connection.NextReliableReceiveSequenceIndex.Value, out value))
                    {
                        connection.SequencedBytes.Remove(connection.NextReliableReceiveSequenceIndex.Value);
                        connection.NextReliableReceiveSequenceIndex++;
                        ReceivedPacketsToDispatch.Value.Pending.Enqueue(new Packet(address, value));
                    }
                    if (connection.SequencedBytes.Count == 0)
                    {
                        connection.ReliableSequencedStallStartTime = 0.0;
                    }
                }
                else
                {
                    if (connection.ReliableSequencedStallStartTime <= 0.0)
                    {
                        connection.ReliableSequencedStallStartTime = GetTime();
                    }
                    connection.SequencedBytes.Add(messagePartHeader.SequenceIndex.Value, bytes);
                }
            }
            else if (!connection.NextUnreliableReceiveSequenceIndex.HasValue || CompareSequenceNumbers(messagePartHeader.SequenceIndex.Value, connection.NextUnreliableReceiveSequenceIndex.Value) >= 0)
            {
                connection.NextUnreliableReceiveSequenceIndex = messagePartHeader.SequenceIndex.Value + 1;
                ReceivedPacketsToDispatch.Value.Pending.Enqueue(new Packet(address, bytes));
            }
        }
        else
        {
            ReceivedPacketsToDispatch.Value.Pending.Enqueue(new Packet(address, bytes));
        }
    }

    // Source: Comms/Comms/Comm.cs:Comm.ProcessReceivedMessage
    // A reliable sequenced stream parks behind a missing index. If the gap never fills, the stream
    // would stay blocked forever and only an application-level watchdog reconnect could clear it.
    // Skip the gap once it outlives ReliableSequencedStallTimeout and resume from the oldest
    // buffered message. Runs on the receive thread, so the recovered packets are drained by the
    // same PacketReceived dispatch that is already in progress.
    private void RecoverStalledReliableSequence(Connection connection, IPEndPoint address)
    {
        if (connection.SequencedBytes.Count == 0 ||
            connection.ReliableSequencedStallStartTime <= 0.0)
        {
            return;
        }
        double time = GetTime();
        if (time - connection.ReliableSequencedStallStartTime <
            (double)Settings.ReliableSequencedStallTimeout)
        {
            return;
        }
        uint resumeIndex = 0u;
        bool hasResumeIndex = false;
        foreach (KeyValuePair<uint, byte[]> buffered in connection.SequencedBytes)
        {
            if (!hasResumeIndex || CompareSequenceNumbers(buffered.Key, resumeIndex) < 0)
            {
                resumeIndex = buffered.Key;
                hasResumeIndex = true;
            }
        }
        if (!hasResumeIndex)
        {
            connection.ReliableSequencedStallStartTime = 0.0;
            return;
        }
        int skipped = 0;
        if (connection.NextReliableReceiveSequenceIndex.HasValue &&
            CompareSequenceNumbers(resumeIndex, connection.NextReliableReceiveSequenceIndex.Value) > 0)
        {
            skipped = (int)Math.Min(
                (long)(resumeIndex - connection.NextReliableReceiveSequenceIndex.Value),
                int.MaxValue);
        }
        connection.NextReliableReceiveSequenceIndex = resumeIndex;
        byte[] bufferedBytes;
        while (connection.SequencedBytes.TryGetValue(
            connection.NextReliableReceiveSequenceIndex.Value, out bufferedBytes))
        {
            connection.SequencedBytes.Remove(connection.NextReliableReceiveSequenceIndex.Value);
            connection.NextReliableReceiveSequenceIndex =
                connection.NextReliableReceiveSequenceIndex.Value + 1u;
            ReceivedPacketsToDispatch.Value.Pending.Enqueue(new Packet(address, bufferedBytes));
        }
        connection.ReliableSequencedStallStartTime = connection.SequencedBytes.Count > 0 ? time : 0.0;
        InvokeError(new ProtocolViolationException(
            $"Reliable sequenced stream from {address.ToString()} stalled for " +
            $"{Settings.ReliableSequencedStallTimeout:0.0}s, skipped {skipped} message(s) to resume"));
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
                bool retryLimitReached = unackedPacket.Value.SendCount - 1 >=
                    Settings.MaxResends;
                if (retryLimitReached && !unackedPacket.Value.RetryLimitRecorded)
                {
                    unackedPacket.Value.RetryLimitRecorded = true;
                    value.ReliableRetryLimitCount++;
                }
                // Source: Comm.ProcessConnections
                // A packet that already exhausted its retry budget must stay visible to the
                // transport-health watchdog, but must not be retransmitted forever. Otherwise one
                // lost ACK creates a permanent resend storm and an ever-growing network rate.
                if (!retryLimitReached &&
                    time >= unackedPacket.Value.LastSendTime + (double)num)
                {
                    // Source: Comms/Comms/Comm.cs:Comm.SendDataPacket
                    // Report before sending, but never let the optional observer affect retry.
                    ReliableRetransmitDiagnostics.Report(new ReliableRetransmitInfo(
                        key,
                        unackedPacket.Key,
                        unackedPacket.Value.SendCount,
                        unackedPacket.Value.Packet.Bytes?.Length ?? 0,
                        unackedPacket.Value.DiagnosticSource,
                        unackedPacket.Value.DiagnosticPayload));
                    SendPacket(unackedPacket.Value.Packet, value);
                    unackedPacket.Value.LastSendTime = time;
                    if (unackedPacket.Value.SendCount < int.MaxValue)
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
                if (time - messagePart.Value.LastReceiveTime > (double)Settings.MessagePartsTimeout)
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
        // RTT is sampled from reliable ACKs and keep-alives. The base period follows the RTT and
        // the exponential backoff is bounded by MaximumBackoffPeriod. Clamping the product by
        // MaximumResendPeriod (0.15 s) used to flatten the backoff after three attempts, which
        // turned one lost ACK into a constant ~150 ms resend storm for up to MaxResends packets.
        double basePeriod = Math.Max(connection.SmoothedRoundTripTime * 1.5,
            Settings.MinimumResendPeriod);
        double backoff = Math.Pow(Settings.ResendBackoffFactor,
            Math.Min(Math.Max(sendCount - 1, 0), Settings.MaximumResendBackoffSteps));
        return (float)Math.Clamp(basePeriod * backoff,
            Settings.MinimumResendPeriod, Settings.MaximumBackoffPeriod);
    }

    private void SendMessages(IPEndPoint address, byte[][] bytes, DeliveryMode deliveryMode,
        string diagnosticSource, byte[] diagnosticPayload)
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
                // Source: Comms/Comms/Comm.cs:Comm.SendMessages
                // The packet type keeps recording whether this is reliable traffic: the hybrid
                // transport routes reliable packets onto the stream and everything else onto the
                // datagram path. Suppressing ACK bookkeeping happens in SendDataPacket and
                // ProcessReceivedPacket, so the header keeps its original meaning.
                bool datagramDelivery = deliveryMode == DeliveryMode.Unreliable ||
                    deliveryMode == DeliveryMode.UnreliableSequenced;
                // Source: Comms/Comms/Comm.cs:Comm.PacketHeader.DatagramTokenFlag
                // 只有走数据报的包带 token：TCP 可靠流的包头不增加任何字节，而可靠流上的包也
                // 永远不需要按源地址重新归属。数据报上用 4 字节 token 取代 16 字节握手 GUID，
                // 所以跨度 NAT 首次加入时（握手期）不可靠包反而比原来少 12 字节。
                uint? datagramToken = datagramDelivery
                    ? new uint?(value.OurDatagramToken)
                    : null;
                Guid? initGuid = (!value.InitAckReceived && !datagramDelivery)
                    ? new Guid?(value.OurGuid)
                    : null;
                PacketHeader.WriteData(writer2, initGuid, datagramToken, packetId,
                    deliveryMode == DeliveryMode.Reliable || deliveryMode == DeliveryMode.ReliableSequenced);
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
                    SendDataPacket(address, writer2.GetBytes(), packetId, deliveryMode, value,
                        diagnosticSource, diagnosticPayload);
            writer2 = null;
        }
        if (writer2 != null && writer2.Position > 0)
        {
            SendDataPacket(address, writer2.GetBytes(), packetId, deliveryMode, value,
                diagnosticSource, diagnosticPayload);
        }
    }

    private void SendDataPacket(IPEndPoint address, byte[] bytes, uint packetId,
        DeliveryMode deliveryMode, Connection connection, string diagnosticSource,
        byte[] diagnosticPayload)
    {
        Packet packet = new(address, bytes);
        if (!Transmitter.IsReliableStream &&
            (deliveryMode == DeliveryMode.Reliable || deliveryMode == DeliveryMode.ReliableSequenced))
        {
            connection.UnackedPackets.Add(packetId, new UnackedPacket
            {
                Packet = packet,
                SendCount = 1,
                LastSendTime = GetTime(),
                DiagnosticSource = diagnosticSource ?? string.Empty,
                DiagnosticPayload = diagnosticPayload
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

    // Source: Comms/Comms/Comm.cs:Comm.Connection.OurDatagramToken
    // 数据报 token 是会话 GUID 的 32 位派生值：随 OurGuid 在换会话时自动变化，不需要新增
    // 协商字段，也不需要额外握手。
    internal static uint DeriveDatagramToken(Guid guid)
    {
        byte[] bytes = guid.ToByteArray();
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
    }

    // Source: Comms/Comms/Comm.cs:Comm.DatagramAddressRepairHandler
    // 按 token 在现有连接里找唯一归属。0 个或多个命中一律拒绝：绝不猜，也不合并两个会话。
    internal bool TryFindDatagramAddressByToken(uint token, IPEndPoint observed,
        out IPEndPoint known)
    {
        known = null;
        foreach (KeyValuePair<IPEndPoint, Connection> item in Connections)
        {
            if (item.Value.TheirDatagramToken != token)
            {
                continue;
            }
            if (known != null)
            {
                // 同一 token 命中多条连接：安全地放弃这次修复。
                known = null;
                return false;
            }
            known = item.Key;
        }
        if (known == null || known.Equals(observed))
        {
            known = null;
            return false;
        }
        return true;
    }

    // Source: Comms/Comms/Comm.cs:Comm.ProcessReceivedPacket
    // 该地址是否已经是这个 token 的主人（即它就是这个 peer 当前的数据报地址）。是主人就不再
    // 触发修复；不是主人（含只有 InitAck/发现应答到过的新地址）才允许按 token 搬迁过来。
    private bool IsDatagramTokenOwner(IPEndPoint address, uint token)
    {
        return Connections.TryGetValue(address, out Connection existing) && existing != null &&
            existing.TheirDatagramToken == token;
    }

    // Source: Comms/Comms/Comm.cs:Comm.ProcessReceivedPacket
    // 把一条连接整体搬到对端数据报的实际源地址：连接对象连同 ACK/序号/分片状态一起换键，
    // 并把 TCP 流的键改到同一地址，于是可靠流量继续走原来那条已建立的流（不重拨、不断开），
    // 数据报则发往真正可达的地址。
    internal bool MoveConnection(IPEndPoint known, IPEndPoint observed)
    {
        if (known == null || observed == null || known.Equals(observed))
        {
            return false;
        }
        if (!Connections.TryGetValue(known, out Connection connection) || connection == null)
        {
            return false;
        }
        if (!IsDatagramAddressChangeAllowed(known, observed, connection))
        {
            return false;
        }
        if (Connections.TryGetValue(observed, out Connection existing) && existing != null)
        {
            // Source: Comms/Comms/Comm.cs:Comm.Connection.TheirDatagramToken
            // 只允许覆盖「不是任何 token 主人」的地址：可能是只有 InitAck/发现应答到过的临时连接。
            // 真正的 peer 地址（已持有一个 token）绝不被抢走。
            if (existing.TheirDatagramToken != null)
            {
                return false;
            }
            Connections.Remove(observed);
        }
        Connections.Remove(known);
        Connections.Add(observed, connection);
        connection.LastDatagramAddressChangeTime = GetTime();
        // Source: Comms/Comms/TcpTransmitter.cs:TcpTransmitter.TryRebindDatagramAddress
        // 流改键失败不回滚数据报地址：下一次可靠发送会重新对键，最坏情况退化为一次单发。
        FindStreamTransmitter(Transmitter)?.TryRebindDatagramAddress(known, observed);
        InvokeDebug("Datagram address {0} moved to {1}", known, observed);
        return true;
    }

    private bool IsDatagramAddressChangeAllowed(IPEndPoint known, IPEndPoint observed,
        Connection connection)
    {        // 默认允许跨 IP（含公网 IP 变化）；把 Setting 关掉后只认「同 IP 的 NAT 端口漂移」。
        // Source: Comms/Comms/CommSettings.cs:CommSettings.AllowDatagramAddressIpChange
        if (!Settings.AllowDatagramAddressIpChange && !known.Address.Equals(observed.Address))
        {
            return false;
        }
        // 防抖：同一 peer 在最小间隔内只搬迁一次。
        if (GetTime() - connection.LastDatagramAddressChangeTime <
            (double)Settings.MinimumDatagramAddressChangeInterval)
        {
            return false;
        }
        // TCP+UDP 部署下可靠流是权威控制面：只为仍然持有活跃流的 peer 修数据报地址。
        TcpTransmitter stream = FindStreamTransmitter(Transmitter);
        if (stream != null && !stream.HasConnection(known))
        {
            return false;
        }
        return true;
    }

    // Source: Comms/Comms/IWrapperTransmitter.cs:IWrapperTransmitter.BaseTransmitter
    // 找到包装链里的流传输（DiagnosticTransmitter/LimiterTransmitter 等包装不影响判断）。
    private static TcpTransmitter FindStreamTransmitter(ITransmitter transmitter)
    {
        ITransmitter current = transmitter;
        while (current != null)
        {
            if (current is HybridTransmitter hybrid)
            {
                return hybrid.StreamTransmitter;
            }
            if (current is TcpTransmitter tcp)
            {
                return tcp;
            }
            current = (current as IWrapperTransmitter)?.BaseTransmitter;
        }
        return null;
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

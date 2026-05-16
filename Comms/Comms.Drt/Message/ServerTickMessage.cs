using System.Collections.Generic;
using System.Net;

namespace Comms.Drt;

internal class ServerTickMessage : Message
{
    public struct ClientTickData
    {
        public int ClientID;

        public IPEndPoint JoinAddress;

        public byte[] JoinBytes;

        public bool Leave;

        public List<byte[]> InputsBytes;

        public void Read(Reader reader)
        {
            ClientID = reader.ReadPackedInt32();
            int num = reader.ReadPackedInt32(0, 1048576);
            switch (num)
            {
                case 0:
                    JoinAddress = reader.ReadIPEndPoint();
                    JoinBytes = reader.ReadBytes();
                    return;
                case 1:
                    Leave = true;
                    return;
            }
            InputsBytes = new List<byte[]>(num - 2);
            for (int i = 0; i < InputsBytes.Capacity; i++)
            {
                InputsBytes.Add(reader.ReadBytes());
            }
        }

        public void Write(Writer writer)
        {
            writer.WritePackedInt32(ClientID);
            if (JoinAddress != null)
            {
                writer.WriteByte(0);
                writer.WriteIPEndPoint(JoinAddress);
                writer.WriteBytes(JoinBytes);
                return;
            }
            if (Leave)
            {
                writer.WriteByte(1);
                return;
            }
            writer.WritePackedInt32(InputsBytes.Count + 2);
            foreach (byte[] inputsByte in InputsBytes)
            {
                writer.WriteBytes(inputsByte);
            }
        }
    }

    public double ReceivedTime;

    public double SentTime;

    public int Tick;

    public int? DesyncDetectedStep;

    public List<ClientTickData> ClientsTickData;

    public bool IsEmpty
    {
        get
        {
            if (ClientsTickData.Count == 0)
            {
                return !DesyncDetectedStep.HasValue;
            }
            return false;
        }
    }

    internal override void Read(Reader reader)
    {
        ReceivedTime = Comm.GetTime();
        Tick = reader.ReadPackedInt32();
        int num = reader.ReadPackedInt32();
        DesyncDetectedStep = ((num > 0) ? new int?(num - 1) : ((int?)null));
        ClientsTickData = new List<ClientTickData>(reader.ReadPackedInt32(0, 1048576));
        for (int i = 0; i < ClientsTickData.Capacity; i++)
        {
            ClientTickData clientTickData = default;
            clientTickData.Read(reader);
            ClientsTickData.Add(clientTickData);
        }
    }

    internal override void Write(Writer writer)
    {
        SentTime = Comm.GetTime();
        writer.WritePackedInt32(Tick);
        writer.WritePackedInt32(DesyncDetectedStep.HasValue ? (DesyncDetectedStep.Value + 1) : 0);
        writer.WritePackedInt32(ClientsTickData.Count);
        foreach (ClientTickData clientsTickDatum in ClientsTickData)
        {
            clientsTickDatum.Write(writer);
        }
    }
}

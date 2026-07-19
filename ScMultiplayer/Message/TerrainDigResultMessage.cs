using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.DestroyCell
    [Serializable]
    public class TerrainDigResultMessage : Message
    {
        public int RequestId;
        public Point3 Cell;
        public bool Accepted;
        public int AuthoritativeValue;
        public int ServerTick;

        public TerrainDigResultMessage()
        {
        }

        public TerrainDigResultMessage(int requestId, Point3 cell, bool accepted,
            int authoritativeValue, int serverTick)
        {
            RequestId = requestId;
            Cell = cell;
            Accepted = accepted;
            AuthoritativeValue = authoritativeValue;
            ServerTick = serverTick;
        }

        protected override void Read(SuReader reader)
        {
            RequestId = reader.ReadInt32();
            Cell = reader.ReadPoint3();
            Accepted = reader.ReadBoolean();
            AuthoritativeValue = reader.ReadInt32();
            ServerTick = reader.ReadInt32();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(RequestId);
            writer.WritePoint3(Cell);
            writer.WriteBoolean(Accepted);
            writer.WriteInt32(AuthoritativeValue);
            writer.WriteInt32(ServerTick);
        }
    }
}

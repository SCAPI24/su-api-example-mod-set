using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class ContainerSyncMessage : Message
    {
        public Point3 Coordinates;
        public string ComponentType = string.Empty;
        public int Revision;
        public bool IsRequest;
        public int[] SlotValues = Array.Empty<int>();
        public int[] SlotCounts = Array.Empty<int>();

        protected override void Read(SuReader reader)
        {
            Coordinates = reader.ReadPoint3();
            ComponentType = reader.ReadString();
            Revision = reader.ReadInt32();
            IsRequest = reader.ReadBoolean();
            int count = reader.ReadPackedInt32();
            SlotValues = new int[count];
            SlotCounts = new int[count];
            for (int i = 0; i < count; i++)
            {
                SlotValues[i] = reader.ReadInt32();
                SlotCounts[i] = reader.ReadInt32();
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WritePoint3(Coordinates);
            writer.WriteString(ComponentType ?? string.Empty);
            writer.WriteInt32(Revision);
            writer.WriteBoolean(IsRequest);
            int count = Math.Min(SlotValues?.Length ?? 0, SlotCounts?.Length ?? 0);
            writer.WritePackedInt32(count);
            for (int i = 0; i < count; i++)
            {
                writer.WriteInt32(SlotValues[i]);
                writer.WriteInt32(SlotCounts[i]);
            }
        }
    }
}

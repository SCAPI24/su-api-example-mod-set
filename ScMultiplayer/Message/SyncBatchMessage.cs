using Comms;
using System;
using System.Collections.Generic;

namespace ScMultiplayer
{
    [Serializable]
    public class SyncBatchMessage : Message
    {
        public List<byte[]> Payloads = new List<byte[]>();

        protected override void Read(SuReader reader)
        {
            int count = reader.ReadPackedInt32();
            if (count < 2 || count > 64)
                throw new InvalidOperationException("Invalid sync batch count.");
            Payloads.Clear();
            int totalLength = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] payload = reader.ReadBytes();
                totalLength += payload?.Length ?? 0;
                if (payload == null || payload.Length == 0 || totalLength > 1100)
                    throw new InvalidOperationException("Invalid sync batch payload.");
                Payloads.Add(payload);
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WritePackedInt32(Payloads.Count);
            foreach (byte[] payload in Payloads)
                writer.WriteBytes(payload);
        }
    }
}

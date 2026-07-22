using Comms;
using System;
using System.Collections.Generic;

namespace ScMultiplayer
{
    public enum TerrainRecoveryStage
    {
        Request,
        ReplayBatch,
        Barrier,
        Acknowledge,
        Ready,
        ResyncRequired
    }

    public class TerrainSequenceRange
    {
        public long Start;
        public long End;

        public TerrainSequenceRange()
        {
        }

        public TerrainSequenceRange(long start, long end)
        {
            Start = start;
            End = end;
        }
    }

    [Serializable]
    public class TerrainRecoveryMessage : Message
    {
        public TerrainRecoveryStage Stage;
        public long LastAppliedSequence;
        public long HeadSequence;
        public int ServerStep;
        public List<TerrainSequenceRange> BufferedRanges = new List<TerrainSequenceRange>();
        public List<byte[]> Payloads = new List<byte[]>();

        protected override void Read(SuReader reader)
        {
            Stage = (TerrainRecoveryStage)reader.ReadInt32();
            LastAppliedSequence = reader.ReadInt64();
            HeadSequence = reader.ReadInt64();
            ServerStep = reader.ReadInt32();

            int rangeCount = reader.ReadPackedInt32();
            if (rangeCount < 0 || rangeCount > 64)
                throw new InvalidOperationException("Invalid terrain recovery range count.");
            BufferedRanges.Clear();
            for (int i = 0; i < rangeCount; i++)
            {
                long start = reader.ReadInt64();
                long end = reader.ReadInt64();
                if (start <= 0 || end < start)
                    throw new InvalidOperationException("Invalid terrain recovery range.");
                BufferedRanges.Add(new TerrainSequenceRange(start, end));
            }

            int payloadCount = reader.ReadPackedInt32();
            if (payloadCount < 0 || payloadCount > 64)
                throw new InvalidOperationException("Invalid terrain recovery payload count.");
            Payloads.Clear();
            int totalBytes = 0;
            for (int i = 0; i < payloadCount; i++)
            {
                byte[] payload = reader.ReadBytes();
                totalBytes += payload?.Length ?? 0;
                if (payload == null || payload.Length == 0 || totalBytes > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Invalid terrain recovery payload.");
                Payloads.Add(payload);
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32((int)Stage);
            writer.WriteInt64(LastAppliedSequence);
            writer.WriteInt64(HeadSequence);
            writer.WriteInt32(ServerStep);

            int rangeCount = Math.Min(BufferedRanges?.Count ?? 0, 64);
            writer.WritePackedInt32(rangeCount);
            for (int i = 0; i < rangeCount; i++)
            {
                writer.WriteInt64(BufferedRanges[i].Start);
                writer.WriteInt64(BufferedRanges[i].End);
            }

            int payloadCount = Math.Min(Payloads?.Count ?? 0, 64);
            writer.WritePackedInt32(payloadCount);
            for (int i = 0; i < payloadCount; i++)
                writer.WriteBytes(Payloads[i]);
        }
    }
}

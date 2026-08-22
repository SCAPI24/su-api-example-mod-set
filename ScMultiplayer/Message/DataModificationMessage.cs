using Comms;
using System;

namespace ScMultiplayer
{
    public enum DataModificationMessageStage : byte
    {
        FastRequest = 0,
        BulkBegin = 1,
        BulkChunk = 2,
        BulkComplete = 3,
        Cancel = 4,
        Result = 5
    }

    [Serializable]
    public sealed class DataModificationMessage : Message
    {
        public DataModificationMessageStage Stage;

        public DataModificationChannel Channel;

        public int RequestId;

        public int TransferId;

        public string ModId = string.Empty;

        public string Operation = string.Empty;

        public int ChunkIndex = -1;

        public int ChunkCount;

        public int TotalBytes;

        public byte[] Payload = Array.Empty<byte>();

        public DataModificationResultCode ResultCode;

        public string Details = string.Empty;

        public int ServerBulkChunksPerFrame;

        public int ServerBulkBytesPerFrame;

        protected override void Read(SuReader reader)
        {
            Stage = (DataModificationMessageStage)reader.ReadByte();
            Channel = (DataModificationChannel)reader.ReadByte();
            RequestId = reader.ReadInt32();
            TransferId = reader.ReadInt32();
            ModId = reader.ReadString() ?? string.Empty;
            Operation = reader.ReadString() ?? string.Empty;
            ChunkIndex = reader.ReadInt32();
            ChunkCount = reader.ReadInt32();
            TotalBytes = reader.ReadInt32();
            Payload = reader.ReadBytes() ?? Array.Empty<byte>();
            ResultCode = (DataModificationResultCode)reader.ReadByte();
            Details = reader.ReadString() ?? string.Empty;
            ServerBulkChunksPerFrame = reader.ReadInt32();
            ServerBulkBytesPerFrame = reader.ReadInt32();
            Validate();
        }

        protected override void Write(SuWriter writer)
        {
            Validate();
            writer.WriteByte((byte)Stage);
            writer.WriteByte((byte)Channel);
            writer.WriteInt32(RequestId);
            writer.WriteInt32(TransferId);
            writer.WriteString(ModId ?? string.Empty);
            writer.WriteString(Operation ?? string.Empty);
            writer.WriteInt32(ChunkIndex);
            writer.WriteInt32(ChunkCount);
            writer.WriteInt32(TotalBytes);
            writer.WriteBytes(Payload ?? Array.Empty<byte>());
            writer.WriteByte((byte)ResultCode);
            writer.WriteString(Details ?? string.Empty);
            writer.WriteInt32(ServerBulkChunksPerFrame);
            writer.WriteInt32(ServerBulkBytesPerFrame);
        }

        private void Validate()
        {
            if (!Enum.IsDefined(typeof(DataModificationMessageStage), Stage) ||
                !Enum.IsDefined(typeof(DataModificationChannel), Channel) ||
                !Enum.IsDefined(typeof(DataModificationResultCode), ResultCode))
                throw new InvalidOperationException("Invalid data modification message kind.");
            if (RequestId <= 0 || TransferId < 0 || ModId.Length == 0 || ModId.Length > 64 ||
                Operation.Length == 0 || Operation.Length > 64 ||
                ModId.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 ||
                Operation.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("Invalid data modification identity.");
            if (ChunkCount < 0 || ChunkCount > 65535 || ChunkIndex < -1 ||
                ChunkIndex >= Math.Max(ChunkCount, 1) || TotalBytes < 0 || TotalBytes > 16 * 1024 * 1024)
                throw new InvalidOperationException("Invalid data modification transfer range.");
            if (Payload.Length > (Stage == DataModificationMessageStage.BulkChunk ? 768 : 8192))
                throw new InvalidOperationException("Data modification payload is too large.");
            if (Details.Length > 256)
                throw new InvalidOperationException("Data modification result is too long.");
            if (ServerBulkChunksPerFrame < 0 || ServerBulkChunksPerFrame > 128 ||
                ServerBulkBytesPerFrame < 0 || ServerBulkBytesPerFrame > 1024 * 1024)
                throw new InvalidOperationException("Invalid data modification server budget.");
            switch (Stage)
            {
                case DataModificationMessageStage.FastRequest:
                    if (Channel != DataModificationChannel.Fast || TransferId != 0 ||
                        ChunkIndex != -1 || ChunkCount != 0 || TotalBytes != 0)
                        throw new InvalidOperationException(
                            "Invalid fast data modification request.");
                    break;
                case DataModificationMessageStage.BulkBegin:
                    if (Channel != DataModificationChannel.Bulk || TransferId <= 0 ||
                        ChunkIndex != -1 || ChunkCount <= 0 || TotalBytes <= 0 ||
                        Payload.Length != 0)
                        throw new InvalidOperationException(
                            "Invalid bulk data modification begin message.");
                    break;
                case DataModificationMessageStage.BulkChunk:
                    if (Channel != DataModificationChannel.Bulk || TransferId <= 0 ||
                        ChunkIndex < 0 || ChunkCount <= 0 || TotalBytes <= 0 ||
                        Payload.Length == 0)
                        throw new InvalidOperationException("Invalid data modification chunk.");
                    break;
                case DataModificationMessageStage.BulkComplete:
                    if (Channel != DataModificationChannel.Bulk || TransferId <= 0 ||
                        ChunkIndex != -1 || ChunkCount <= 0 || TotalBytes <= 0 ||
                        Payload.Length != 0)
                        throw new InvalidOperationException(
                            "Invalid bulk data modification completion message.");
                    break;
                case DataModificationMessageStage.Cancel:
                    if (Channel != DataModificationChannel.Bulk || TransferId <= 0 ||
                        ChunkIndex != -1 || Payload.Length != 0)
                        throw new InvalidOperationException(
                            "Invalid data modification cancellation message.");
                    break;
                case DataModificationMessageStage.Result:
                    if (Payload.Length != 0 ||
                        (Channel == DataModificationChannel.Fast && TransferId != 0) ||
                        (Channel == DataModificationChannel.Bulk && TransferId <= 0))
                        throw new InvalidOperationException(
                            "Invalid data modification result message.");
                    break;
            }
        }
    }
}

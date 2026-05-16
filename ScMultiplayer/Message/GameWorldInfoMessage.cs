using System;
using System.Net;
using Comms;
using Game;

namespace ScMultiplayer
{
    [Serializable]
    public class GameWorldInfoMessage : Message
    {
        public string Name = string.Empty;
        public long Size;
        public DateTime LastSaveTime;
        public GameMode GameMode;
        public EnvironmentBehaviorMode EnvironmentBehaviorMode;
        public string SerializationVersion = string.Empty;
        public IPEndPoint HostAddress;

        public GameWorldInfoMessage()
        {
        }

        public GameWorldInfoMessage(string name, long size, DateTime lastSaveTime, GameMode gameMode, EnvironmentBehaviorMode environmentBehaviorMode, string serializationVersion, IPEndPoint hostAddress/*, byte[] worldData*/)
        {
            Name = name;
            Size = size;
            LastSaveTime = lastSaveTime;
            GameMode = gameMode;
            EnvironmentBehaviorMode = environmentBehaviorMode;
            SerializationVersion = serializationVersion;
            HostAddress = hostAddress;
        }

        protected override void Read(SuReader reader)
        {
            Name = reader.ReadString();
            Size = reader.ReadInt64();
            LastSaveTime = DateTime.FromBinary(reader.ReadInt64());
            GameMode = (GameMode)reader.ReadInt32();
            EnvironmentBehaviorMode = (EnvironmentBehaviorMode)reader.ReadInt32();
            SerializationVersion = reader.ReadString();
            HostAddress=reader.ReadIPEndPoint();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteString(Name);
            writer.WriteInt64(Size);
            writer.WriteInt64(LastSaveTime.ToBinary());
            writer.WriteInt32((int)GameMode);
            writer.WriteInt32((int)EnvironmentBehaviorMode);
            writer.WriteString(SerializationVersion);
            writer.WriteIPEndPoint(HostAddress);
          
        }
    }
}
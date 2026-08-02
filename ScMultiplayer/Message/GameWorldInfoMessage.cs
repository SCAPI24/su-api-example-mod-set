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
        public string PlayerName = string.Empty;
        public string PlayerIdentity = string.Empty;
        public bool HasPlayerProfile;
        public PlayerClass PlayerClass;
        public string CharacterSkinName = string.Empty;
        public byte[] CharacterSkinSha256 = Array.Empty<byte>();
        public string MultiplayerModVersion = Message.ModVersion;
        public int MultiplayerProtocolVersion = Message.ProtocolVersion;
        public string MultiplayerProtocolHash = Message.ProtocolHash;
        public string MultiplayerBuildFingerprint = Message.BuildFingerprint;

        public GameWorldInfoMessage()
        {
        }

        public GameWorldInfoMessage(string name, long size, DateTime lastSaveTime, GameMode gameMode,
            EnvironmentBehaviorMode environmentBehaviorMode, string serializationVersion,
            IPEndPoint hostAddress, string playerName = "", string playerIdentity = "",
            bool hasPlayerProfile = false, PlayerClass playerClass = PlayerClass.Male,
            string characterSkinName = "")
        {
            Name = name;
            Size = size;
            LastSaveTime = lastSaveTime;
            GameMode = gameMode;
            EnvironmentBehaviorMode = environmentBehaviorMode;
            SerializationVersion = serializationVersion;
            HostAddress = hostAddress;
            PlayerName = playerName ?? string.Empty;
            PlayerIdentity = playerIdentity ?? string.Empty;
            HasPlayerProfile = hasPlayerProfile;
            PlayerClass = playerClass;
            CharacterSkinName = characterSkinName ?? string.Empty;
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
            PlayerName = reader.ReadString();
            PlayerIdentity = reader.ReadString();
            HasPlayerProfile = reader.ReadBoolean();
            PlayerClass = (PlayerClass)reader.ReadInt32();
            CharacterSkinName = reader.ReadString();
            // Source: Mod/ScMultiplayer/Message/Message.cs:Message.ProtocolHash
            // Legacy room descriptions have no protocol trailer and remain readable so the
            // client can show an explicit incompatibility message before joining.
            MultiplayerModVersion = reader.Position < reader.Length
                ? reader.ReadString()
                : string.Empty;
            MultiplayerProtocolVersion = reader.Position < reader.Length
                ? reader.ReadPackedInt32()
                : 0;
            MultiplayerProtocolHash = reader.Position < reader.Length
                ? reader.ReadString()
                : string.Empty;
            MultiplayerBuildFingerprint = reader.Position < reader.Length
                ? reader.ReadString()
                : string.Empty;
            CharacterSkinSha256 = reader.Position < reader.Length
                ? reader.ReadBytes()
                : Array.Empty<byte>();
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
            writer.WriteString(PlayerName ?? string.Empty);
            writer.WriteString(PlayerIdentity ?? string.Empty);
            writer.WriteBoolean(HasPlayerProfile);
            writer.WriteInt32((int)PlayerClass);
            writer.WriteString(CharacterSkinName ?? string.Empty);
            writer.WriteString(MultiplayerModVersion ?? string.Empty);
            writer.WritePackedInt32(MultiplayerProtocolVersion);
            writer.WriteString(MultiplayerProtocolHash ?? string.Empty);
            writer.WriteString(MultiplayerBuildFingerprint ?? string.Empty);
            writer.WriteBytes(CharacterSkinSha256 ?? Array.Empty<byte>());
        }
    }
}

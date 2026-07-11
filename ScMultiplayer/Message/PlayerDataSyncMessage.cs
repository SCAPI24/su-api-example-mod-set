using Engine;
using Game;
using System;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class PlayerDataSyncMessage : Message
    {
        public enum DataAction : byte
        {
            AddPlayer,
            RemovePlayer,
            ModifyPlayer,
            SetMain
        }

        public DataAction Action;
        public string PlayerGUID;
        public string Name;
        public PlayerClass PlayerClass;
        public string CharacterSkinName;
        public int InputDevice;
        public bool IsMainPlayer;

        public PlayerDataSyncMessage() { }

        public PlayerDataSyncMessage(DataAction action, string guid, string name, PlayerClass playerClass, string skin, int inputDevice, bool isMain)
        {
            Action = action; PlayerGUID = guid ?? string.Empty; Name = name ?? string.Empty;
            PlayerClass = playerClass; CharacterSkinName = skin ?? string.Empty;
            InputDevice = inputDevice; IsMainPlayer = isMain;
        }

        protected override void Read(SuReader reader)
        {
            Action = (DataAction)reader.ReadByte();
            PlayerGUID = reader.ReadString();
            Name = reader.ReadString();
            PlayerClass = (PlayerClass)reader.ReadInt32();
            CharacterSkinName = reader.ReadString();
            InputDevice = reader.ReadInt32();
            IsMainPlayer = reader.ReadBoolean();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Action);
            writer.WriteString(PlayerGUID ?? string.Empty);
            writer.WriteString(Name ?? string.Empty);
            writer.WriteInt32((int)PlayerClass);
            writer.WriteString(CharacterSkinName ?? string.Empty);
            writer.WriteInt32(InputDevice);
            writer.WriteBoolean(IsMainPlayer);
        }
    }
}

using Game;
using System;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class GamePakWorldMessage : Message
    {
        public string Name;
        public byte[] WorldData;
        public DateTime LastSaveTime;

        public GamePakWorldMessage() { }
        public GamePakWorldMessage(string name, byte[] worldData, DateTime lastSaveTime)
        {
            Name = name;
            WorldData = worldData;
            LastSaveTime = lastSaveTime;
        }

        protected override void Read(SuReader reader)
        {
            Name = reader.ReadString();

            // 读取 WorldData
            int dataLength = reader.ReadPackedInt32();
            if (dataLength > 0)
            {
                WorldData = reader.ReadFixedBytes(dataLength);
            }
            else
            {
                WorldData = null;
            }
            LastSaveTime = DateTime.FromBinary(reader.ReadInt64());
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteString(Name);

            // 写入 WorldData
            if (WorldData != null && WorldData.Length > 0)
            {
                writer.WritePackedInt32(WorldData.Length);
                writer.WriteFixedBytes(WorldData);
            }
            else
            {
                writer.WritePackedInt32(0); // 写入0表示没有数据
            }
            writer.WriteInt64(LastSaveTime.ToBinary());
        }
    }
}
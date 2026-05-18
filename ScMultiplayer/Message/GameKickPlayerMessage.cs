using System;
using Comms;

namespace ScMultiplayer
{
    /// <summary>
    /// 踢出玩家消息
    /// </summary>
    [Serializable]
    public class GameKickPlayerMessage : Message
    {
        public int TargetClientID;
        public string Reason;

        public GameKickPlayerMessage() { }

        public GameKickPlayerMessage(int targetClientID, string reason = null)
        {
            TargetClientID = targetClientID;
            Reason = reason ?? "Kicked by host";
        }

        protected override void Read(SuReader reader)
        {
            TargetClientID = reader.ReadInt32();
            Reason = reader.ReadString();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(TargetClientID);
            writer.WriteString(Reason ?? string.Empty);
        }
    }
}

using Comms;
using System;
using System.Collections.Generic;
using System.Net;

namespace ScMultiplayer
{
    // Source: GamePlayerPositionMessage.cs:GamePlayerPositionMessage.ReadPayload
    // One latest-state packet contains every authoritative player snapshot for a network tick.
    [Serializable]
    public class GamePlayerPositionsMessage : Message
    {
        public List<GamePlayerPositionMessage> Players = new List<GamePlayerPositionMessage>();

        public GamePlayerPositionsMessage()
        {
        }

        public GamePlayerPositionsMessage(IEnumerable<GamePlayerPositionMessage> players)
        {
            if (players != null) Players.AddRange(players);
        }

        protected override void Read(SuReader reader)
        {
            int count = reader.ReadPackedInt32();
            if (count < 0 || count > 16)
                throw new ProtocolViolationException("Invalid player position batch size.");
            Players = new List<GamePlayerPositionMessage>(count);
            for (int i = 0; i < count; i++)
            {
                var player = new GamePlayerPositionMessage();
                player.ReadPayload(reader);
                Players.Add(player);
            }
        }

        protected override void Write(SuWriter writer)
        {
            int count = Math.Min(Players?.Count ?? 0, 16);
            writer.WritePackedInt32(count);
            for (int i = 0; i < count; i++) Players[i].WritePayload(writer);
        }
    }
}

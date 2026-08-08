using Game;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ScMultiplayer.Modules.Join
{
    // Source: ScMultiplayer.CaptureHostedWorldSnapshot
    // Sanitizes an immutable exported archive. It does not access Project, Entity or Comms state.
    internal static class WorldArchiveSanitizer
    {
        public static byte[] RemoveNetworkPlayers(byte[] worldData,
            HashSet<string> networkPlayerIndices)
        {
            if (networkPlayerIndices == null || networkPlayerIndices.Count == 0)
                return worldData;
            using var sourceStream = new MemoryStream(worldData, writable: false);
            using Game.ZipArchive sourceArchive = Game.ZipArchive.Open(
                sourceStream, keepStreamOpen: true);
            using var targetStream = new MemoryStream();
            using (Game.ZipArchive targetArchive = Game.ZipArchive.Create(
                targetStream, keepStreamOpen: true))
            {
                foreach (Game.ZipArchiveEntry entry in sourceArchive.ReadCentralDir())
                {
                    using var entryStream = new MemoryStream();
                    sourceArchive.ExtractFile(entry, entryStream);
                    entryStream.Position = 0;
                    if (string.Equals(entry.FilenameInZip, "Project.xml",
                        StringComparison.OrdinalIgnoreCase))
                        RemoveNetworkPlayersFromProjectXml(entryStream, networkPlayerIndices);
                    entryStream.Position = 0;
                    targetArchive.AddStream(entry.FilenameInZip, entryStream);
                }
            }
            return targetStream.ToArray();
        }

        private static void RemoveNetworkPlayersFromProjectXml(MemoryStream stream,
            HashSet<string> networkPlayerIndices)
        {
            XDocument document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            XElement playersSubsystem = document.Root?.Element("Subsystems")?.Elements("Values")
                .FirstOrDefault(element => (string)element.Attribute("Name") == "Players");
            XElement playersValues = playersSubsystem?.Elements("Values")
                .FirstOrDefault(element => (string)element.Attribute("Name") == "Players");
            foreach (XElement player in playersValues?.Elements("Values").ToArray() ??
                Array.Empty<XElement>())
            {
                if (networkPlayerIndices.Contains((string)player.Attribute("Name")))
                    player.Remove();
            }

            XElement entities = document.Root?.Element("Entities");
            foreach (XElement entity in entities?.Elements("Entity").ToArray() ??
                Array.Empty<XElement>())
            {
                XElement player = entity.Elements("Values")
                    .FirstOrDefault(element => (string)element.Attribute("Name") == "Player");
                string playerIndex = (string)player?.Elements("Value")
                    .FirstOrDefault(element => (string)element.Attribute("Name") == "PlayerIndex")?
                    .Attribute("Value");
                if (playerIndex != null && networkPlayerIndices.Contains(playerIndex))
                    entity.Remove();
            }

            stream.SetLength(0);
            document.Save(stream, SaveOptions.DisableFormatting);
        }
    }
}

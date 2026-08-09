using Engine;
using Game;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        // Source: ScMultiplayerProfileHandlers.cs:EnsurePlayerRecordsLoaded
        // Multiplayer revision metadata stays beside Project.xml, leaving the original world
        // representation readable by an application that does not load ScMultiplayer.
        private void EnsureHostTerrainSyncStateLoaded()
        {
            if (!IsHost) return;
            string directory = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false)?
                .DirectoryName;
            if (string.IsNullOrEmpty(directory) || string.Equals(directory,
                    m_hostTerrainSyncWorldDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            m_hostTerrainSyncWorldDirectory = directory;
            m_hostTerrainSyncStateDirty = false;
            m_hostTerrainSequence = 0L;
            m_hostTerrainChunkRevisions.Clear();
            string path = Storage.CombinePaths(directory, TerrainSyncStateFileName);
            if (!Storage.FileExists(path)) return;
            try
            {
                XDocument document;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    document = XDocument.Load(stream);
                XElement root = document.Root;
                if (root == null || root.Name != "ScMultiplayerTerrainSync" ||
                    !TryParseTerrainSyncLong((string)root.Attribute("HeadRevision"), out long head))
                    return;

                m_hostTerrainSequence = Math.Max(head, 0L);
                foreach (XElement element in root.Elements("Chunk"))
                {
                    if (!TryParseTerrainSyncInt((string)element.Attribute("X"), out int x) ||
                        !TryParseTerrainSyncInt((string)element.Attribute("Z"), out int z) ||
                        !TryParseTerrainSyncLong((string)element.Attribute("Revision"), out long revision) ||
                        revision <= 0L)
                        continue;
                    m_hostTerrainChunkRevisions[new Point2(x, z)] = revision;
                }
            }
            catch (Exception ex)
            {
                m_hostTerrainSequence = 0L;
                m_hostTerrainChunkRevisions.Clear();
                Log.Warning($"[ScMP] Ignoring invalid terrain sync state: {ex.Message}");
            }
        }

        // Source: ScMultiplayerTerrainHandlers.cs:RecordHostTerrainChanges
        private void MarkHostTerrainSyncStateDirty()
        {
            if (!IsHost) return;
            EnsureHostTerrainSyncStateLoaded();
            if (!string.IsNullOrEmpty(m_hostTerrainSyncWorldDirectory))
                m_hostTerrainSyncStateDirty = true;
        }

        // Source: ScMultiplayerProfileHandlers.cs:SavePlayerRecords
        // Uses the existing host persistence cadence instead of saving once per terrain cell.
        private void SaveHostTerrainSyncState()
        {
            if (!IsHost || !m_hostTerrainSyncStateDirty ||
                string.IsNullOrEmpty(m_hostTerrainSyncWorldDirectory))
                return;
            try
            {
                var root = new XElement("ScMultiplayerTerrainSync",
                    new XAttribute("Version", 1),
                    new XAttribute("HeadRevision", m_hostTerrainSequence.ToString(
                        CultureInfo.InvariantCulture)));
                foreach (var item in m_hostTerrainChunkRevisions.Where(pair => pair.Value > 0L)
                    .OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y))
                {
                    root.Add(new XElement("Chunk",
                        new XAttribute("X", item.Key.X),
                        new XAttribute("Z", item.Key.Y),
                        new XAttribute("Revision", item.Value.ToString(
                            CultureInfo.InvariantCulture))));
                }
                string path = Storage.CombinePaths(m_hostTerrainSyncWorldDirectory,
                    TerrainSyncStateFileName);
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    new XDocument(root).Save(stream);
                m_hostTerrainSyncStateDirty = false;
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Failed to save terrain sync state: {ex.Message}");
            }
        }

        // Source: ScMultiplayerClientEvents.cs:ResetSessionState
        private void ResetHostTerrainSyncStateStorage()
        {
            m_hostTerrainSyncWorldDirectory = null;
            m_hostTerrainSyncStateDirty = false;
        }

        private static bool TryParseTerrainSyncInt(string value, out int result) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

        private static bool TryParseTerrainSyncLong(string value, out long result) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}

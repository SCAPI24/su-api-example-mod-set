using Engine;
using Game;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P2：领地数据**落盘**（`ScMultiplayerRegions.xml`，与 `ScMultiplayerPlayers.xml` /
    /// `ScMultiplayerTerrainSync.xml` 同款：放在世界目录里、跟 `Project.xml` 平级）。
    ///
    /// Source: ScMultiplayerTerrainSyncStatePersistence.cs（世界目录 + 脏标记 + 主机定时保存的写法）
    /// Source: Mod/ScMultiplayer/doc/REGION-CLAIM-DESIGN.md §1（记录格式）
    ///
    /// 只有主机写盘；客户端持有的只是内存副本。
    /// </summary>
    public partial class ScMultiplayer
    {
        private const string RegionClaimsFileName = "ScMultiplayerRegions.xml";

        private string m_regionClaimsWorldDirectory;
        private bool m_regionClaimsDirty;

        private void EnsureHostRegionClaimsLoaded()
        {
            if (!IsHost)
                return;
            string directory = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false)?
                .DirectoryName;
            if (string.IsNullOrEmpty(directory) || string.Equals(directory,
                    m_regionClaimsWorldDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            m_regionClaimsWorldDirectory = directory;
            m_regionClaimsDirty = false;
            m_regionClaims.Clear();
            m_regionClaimNextId = 1;
            m_regionClaimSequence = 0L;
            string path = Storage.CombinePaths(directory, RegionClaimsFileName);
            if (!Storage.FileExists(path))
                return;
            try
            {
                XDocument document;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    document = XDocument.Load(stream);
                XElement root = document.Root;
                if (root == null || root.Name != "Regions")
                    return;

                TryParseRegionInt((string)root.Attribute("NextId"), out int nextId);
                TryParseRegionLong((string)root.Attribute("Sequence"), out long sequence);
                foreach (XElement element in root.Elements("Region"))
                {
                    if (!TryParseRegionInt((string)element.Attribute("Id"), out int id) || id <= 0)
                        continue;
                    if (!TryParseRegionInt((string)element.Attribute("MinX"), out int minX) ||
                        !TryParseRegionInt((string)element.Attribute("MinZ"), out int minZ) ||
                        !TryParseRegionInt((string)element.Attribute("MaxX"), out int maxX) ||
                        !TryParseRegionInt((string)element.Attribute("MaxZ"), out int maxZ))
                        continue;
                    TryParseRegionInt((string)element.Attribute("MinY"), out int minY);
                    TryParseRegionInt((string)element.Attribute("MaxY"), out int maxY);
                    if (maxX < minX || maxZ < minZ)
                        continue;
                    var claim = new RegionClaim
                    {
                        Id = id,
                        MinX = minX,
                        MinY = RegionClaimMinimumY,
                        MinZ = minZ,
                        MaxX = maxX,
                        MaxY = RegionClaimMaximumY,
                        MaxZ = maxZ,
                        CreatedUtc = (string)element.Attribute("CreatedUtc") ?? string.Empty,
                        Name = (string)element.Attribute("Name") ?? string.Empty,
                        Flags = (string)element.Attribute("Flags") ?? string.Empty
                    };
                    foreach (XElement owner in element.Elements("Owner"))
                    {
                        claim.Owners.Add(new RegionClaimOwner
                        {
                            UserId = (string)owner.Attribute("UserId") ?? string.Empty,
                            Name = (string)owner.Attribute("Name") ?? string.Empty
                        });
                    }
                    m_regionClaims.Add(claim);
                    nextId = Math.Max(nextId, id + 1);
                }
                m_regionClaimNextId = Math.Max(nextId, 1);
                m_regionClaimSequence = Math.Max(sequence, 0L);
                Log.Information("[ScMP] Loaded " + m_regionClaims.Count +
                    " region claims (nextId=" + m_regionClaimNextId +
                    ", sequence=" + m_regionClaimSequence + ")");
            }
            catch (Exception ex)
            {
                m_regionClaims.Clear();
                m_regionClaimNextId = 1;
                m_regionClaimSequence = 0L;
                Log.Warning("[ScMP] Ignoring invalid region claims file: " + ex.Message);
            }
        }

        private void MarkHostRegionClaimsDirty()
        {
            if (!IsHost)
                return;
            EnsureHostRegionClaimsLoaded();
            if (!string.IsNullOrEmpty(m_regionClaimsWorldDirectory))
                m_regionClaimsDirty = true;
        }

        /// <summary>跟角色记录同一节拍保存（`PlayerRecordSaveInterval`），不是每次改动都写盘。</summary>
        private void SaveHostRegionClaims()
        {
            if (!IsHost || !m_regionClaimsDirty ||
                string.IsNullOrEmpty(m_regionClaimsWorldDirectory))
                return;
            try
            {
                var root = new XElement("Regions",
                    new XAttribute("Version", 1),
                    new XAttribute("NextId", m_regionClaimNextId.ToString(
                        CultureInfo.InvariantCulture)),
                    new XAttribute("Sequence", m_regionClaimSequence.ToString(
                        CultureInfo.InvariantCulture)));
                foreach (RegionClaim claim in m_regionClaims.OrderBy(item => item.Id))
                {
                    var element = new XElement("Region",
                        new XAttribute("Id", claim.Id),
                        new XAttribute("MinX", claim.MinX),
                        new XAttribute("MinY", claim.MinY),
                        new XAttribute("MinZ", claim.MinZ),
                        new XAttribute("MaxX", claim.MaxX),
                        new XAttribute("MaxY", claim.MaxY),
                        new XAttribute("MaxZ", claim.MaxZ),
                        new XAttribute("CreatedUtc", claim.CreatedUtc ?? string.Empty),
                        new XAttribute("Name", claim.Name ?? string.Empty),
                        new XAttribute("Flags", claim.Flags ?? string.Empty));
                    foreach (RegionClaimOwner owner in claim.Owners)
                    {
                        element.Add(new XElement("Owner",
                            new XAttribute("UserId", owner.UserId ?? string.Empty),
                            new XAttribute("Name", owner.Name ?? string.Empty)));
                    }
                    root.Add(element);
                }
                string path = Storage.CombinePaths(m_regionClaimsWorldDirectory,
                    RegionClaimsFileName);
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    new XDocument(root).Save(stream);
                m_regionClaimsDirty = false;
            }
            catch (Exception ex)
            {
                Log.Error("[ScMP] Failed to save region claims: " + ex.Message);
            }
        }

        /// <summary>会话/世界重置：清空本端副本与落盘挂点（主机下次载入世界时重新读盘）。</summary>
        private void ResetRegionClaimStorage()
        {
            m_regionClaimsWorldDirectory = null;
            m_regionClaimsDirty = false;
            m_regionClaims.Clear();
            m_regionClaimNextId = 1;
            m_regionClaimSequence = 0L;
        }

        private static bool TryParseRegionInt(string value, out int result) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

        private static bool TryParseRegionLong(string value, out long result) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}

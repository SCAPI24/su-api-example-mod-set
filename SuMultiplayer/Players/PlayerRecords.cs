using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Engine;
using Game;
using SuMultiplayer.Modules;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 联机角色记录：**只属于联机的角色数据**，与世界的 Project.xml 完全分开。
    ///
    /// Source: Mod/ScMultiplayer/Core/ScMultiplayerSupportTypes.cs:158-202（字段全集，逐字段照搬）
    /// </summary>
    public class NetworkPlayerRecord
    {
        public string Name;
        /// <summary>
        /// 这个名字是否来自**已确认的真实资料**（角色创建屏 / 客户端 Hello.profile）。
        /// 旧构建把引擎默认显示名 "Android User" 直接当角色名写进记录；这样的记录必须做一次
        /// 显式迁移（要求客户端重新创建角色），不能把默认名当成真实玩家名字。
        /// 新记录一律 true；读旧 XML 时缺失属性 = 按"名字是否为引擎默认名"推断（默认名 → false）。
        /// </summary>
        public bool NameConfirmed = true;
        public PlayerClass PlayerClass;
        public string SkinName;
        public byte[] SkinSha256 = Array.Empty<byte>();
        public Vector3 Position;
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPosition
        public Vector3 SpawnPosition;
        public float Level = 1f;
        public float Health = 1f;
        public float Air = 1f;
        public float Food = 0.9f;
        public float Stamina = 1f;
        public float Sleep = 0.9f;
        public float Temperature = 12f;
        public float TargetTemperature = 12f;
        public float Wetness;
        public float FluDuration;
        public float FluOnset;
        public float SicknessDuration;
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Save
        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Save
        // Source: Survivalcraft/Game/ComponentOnFire.cs:ComponentOnFire.Save
        public Quaternion BodyRotation = Quaternion.Identity;
        public Vector2 LookAngles;
        public float FireDuration;
        public Dictionary<int, float> Satiation = new Dictionary<int, float>();
        public bool IsCreativeFlying;
        public bool HasReceivedInitialItems = true;
        public bool InventoryWasCreative;
        public int ActiveSlotIndex;
        public int CreativeCategoryIndex;
        public int CreativePageIndex;
        public int[] SlotValues;
        public int[] SlotCounts;
        // Source: Survivalcraft/Game/ComponentCraftingTable.cs:ComponentCraftingTable.Save
        public int[] HandcraftSlotValues;
        public int[] HandcraftSlotCounts;
        public int[][] Clothes;
        // 联机角色的**游戏统计**：由客户端周期性上报（统计只有客户端算得准），
        // 存进本记录（SuMultiplayerPlayers.xml）而不是 Project.xml。
        // Source: Survivalcraft/Game/SubsystemPlayerStats.cs:SubsystemPlayerStats
        public PlayerStatsSnapshot Stats;
    }

    /// <summary>
    /// 角色记录的取值编解码。
    ///
    /// Source: Mod/ScMultiplayer/Modules/Player/PlayerProfileValueCodec.cs:12-37
    /// Source: Mod/ScMultiplayer/Modules/Player/PlayerRecordValuePolicy.cs:11-34
    /// Source: Mod/ScMultiplayer/Modules/Player/SkinHashCodec.cs:10-33
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:559-573
    ///         （LooksLikeLegacyCreativeInventory）
    /// 纯值编码，不持有任何状态；玩家状态的所有权仍在记录表与角色创建器手里。
    /// </summary>
    internal static class PlayerRecordCodec
    {
        public const float DefaultLevel = 1f;
        public const float DefaultHealth = 1f;
        public const float DefaultAir = 1f;
        public const float DefaultFood = 0.9f;
        public const float DefaultStamina = 1f;
        public const float DefaultSleep = 0.9f;
        public const float DefaultTemperature = 12f;

        public static int[][] CreateEmptyClothes() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };

        public static string FormatFloat(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        public static float ParseFloat(string value, float fallback = 0f) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                ? result : fallback;

        public static PlayerClass ParsePlayerClass(string value) =>
            Enum.TryParse(value, true, out PlayerClass result) ? result : PlayerClass.Male;

        public static bool ParseBool(string value, bool fallback) =>
            bool.TryParse(value, out bool result) ? result : fallback;

        public static string FormatIntArray(int[] values) =>
            values == null || values.Length == 0 ? string.Empty : string.Join(";", values);

        public static int[] ParseIntArray(string values)
        {
            if (string.IsNullOrWhiteSpace(values)) return Array.Empty<int>();
            return values.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int result) ? result : 0).ToArray();
        }

        public static bool ShouldPersistItem(int value, int count) => value != 0 && count > 0;

        public static byte[] CloneSkinHash(byte[] bytes) =>
            bytes == null || bytes.Length == 0 ? Array.Empty<byte>() : (byte[])bytes.Clone();

        public static bool IsSkinHashValid(byte[] hash) =>
            hash != null && hash.Length == 32 && hash.Any(value => value != 0);

        public static string FormatSkinHash(byte[] hash) =>
            IsSkinHashValid(hash) ? Convert.ToHexString(hash).ToLowerInvariant() : string.Empty;

        public static byte[] ParseSkinHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<byte>();
            string text = value.Trim();
            if (text.Length != 64) return Array.Empty<byte>();
            try
            {
                byte[] hash = Convert.FromHexString(text);
                return IsSkinHashValid(hash) ? hash : Array.Empty<byte>();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        // Source: Survivalcraft/Game/ComponentCreativeInventory.cs:ComponentCreativeInventory.GetSlotCount
        public static bool LooksLikeLegacyCreativeInventory(NetworkPlayerRecord record)
        {
            if (record == null) return false;
            if (record.InventoryWasCreative) return false;
            int slotsCount = Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0);
            if (slotsCount > 64) return true;
            int creativeStacks = 0;
            for (int i = 0; i < slotsCount; i++)
            {
                if (record.SlotCounts[i] >= 9999 && ++creativeStacks >= 8) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 联机角色记录文件：<c>&lt;世界目录&gt;/SuMultiplayerPlayers.xml</c>。
    ///
    /// 契约（照旧实现）：
    ///   · **只有主机读写**（<see cref="EnsureLoaded"/> 的 isHost 与 <see cref="Put"/> 的守卫）；
    ///     客户端只从握手里拿身份，不去读/写这份文件。
    ///   · 文件不存在 = 空表，不报错（第一次联机时本来就没有）；
    ///   · 文件损坏 = 明确 Log.Error + 保持空表，并且**本会话拒绝写盘** —— 不做"解析失败就当空表、
    ///     然后把空表写回去"的静默改写（那会把玩家所有人的角色记录一次抹掉）；
    ///   · 写盘时机由调用方决定（旧实现：每 5 秒周期刷新 + 角色创建/离开时）。
    ///
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:126-372
    ///         （EnsurePlayerRecordsLoaded / SavePlayerRecords，路径、根节点、Version、字段顺序全部照搬）
    /// </summary>
    public sealed class SuMultiplayerPlayersXml
    {
        public const string FileName = "SuMultiplayerPlayers.xml";
        public const int Version = 6;

        private readonly string m_ownerId;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly Dictionary<string, NetworkPlayerRecord> m_records =
            new Dictionary<string, NetworkPlayerRecord>(StringComparer.Ordinal);
        private string m_worldDirectory = string.Empty;
        private bool m_dirty;
        private bool m_isHost;
        private bool m_loadFailed;

        public SuMultiplayerPlayersXml(string ownerId, IModuleDiagnostics diagnostics)
        {
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "player-records" : ownerId;
            m_diagnostics = diagnostics;
        }

        /// <summary>已加载的角色记录数（空表 = 0）。</summary>
        public int Count => m_records.Count;

        /// <summary>当前记录表所属的世界目录（空 = 还没进世界）。</summary>
        public string WorldDirectory => m_worldDirectory;

        /// <summary>本次加载是否失败（损坏文件）。为 true 时本会话拒绝写盘。</summary>
        public bool LoadFailed => m_loadFailed;

        public IEnumerable<KeyValuePair<string, NetworkPlayerRecord>> Entries => m_records;

        public bool TryGet(string recordKey, out NetworkPlayerRecord record) =>
            m_records.TryGetValue(recordKey ?? string.Empty, out record);

        /// <summary>
        /// 世界目录变化时重新加载（同一个目录只加载一次）。
        /// Source: ScMultiplayerProfileHandlers.cs:126-271
        /// </summary>
        public void EnsureLoaded(string worldDirectory, bool isHost)
        {
            if (!isHost) return;
            if (string.IsNullOrEmpty(worldDirectory) ||
                string.Equals(worldDirectory, m_worldDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            m_records.Clear();
            m_worldDirectory = worldDirectory;
            m_isHost = true;
            m_dirty = false;
            m_loadFailed = false;

            string path = Storage.CombinePaths(worldDirectory, FileName);
            if (!Storage.FileExists(path)) return;   // 没有文件 = 空表，不是错误
            try
            {
                XDocument document;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    document = XDocument.Load(stream);
                foreach (XElement element in document.Root?.Elements("Player") ?? Enumerable.Empty<XElement>())
                {
                    string identity = (string)element.Attribute("Identity");
                    if (string.IsNullOrWhiteSpace(identity)) continue;
                    NetworkPlayerRecord record = ReadPlayer(element);
                    // Legacy records did not persist SpawnPosition. Keep their previous saved
                    // position as the respawn anchor instead of falling back to the creation point.
                    // Source: ScMultiplayerProfileHandlers.cs:197-200
                    if (record.SpawnPosition == Vector3.Zero) record.SpawnPosition = record.Position;
                    ReadInventory(element.Element("Inventory"), out int[] slotValues, out int[] slotCounts);
                    record.SlotValues = slotValues;
                    record.SlotCounts = slotCounts;
                    ReadHandcrafting(element.Element("Handcrafting"), record);
                    ReadSatiation(element.Element("Satiation"), record);
                    ReadClothes(element.Element("Clothes"), record);
                    record.Stats = PlayerStatsSnapshot.FromXml(element.Element("Stats"));
                    if (element.Attribute("InitialItems") == null)
                    {
                        bool hasClothes = record.Clothes.Any(slot => slot != null && slot.Length > 0);
                        record.HasReceivedInitialItems = hasClothes;
                    }
                    // 旧版本创造仓库的迁移：**显式记一条日志**，不做无声改写。
                    // Source: ScMultiplayerProfileHandlers.cs:240-247
                    if (!record.InventoryWasCreative && PlayerRecordCodec.LooksLikeLegacyCreativeInventory(record))
                    {
                        m_diagnostics?.Info(m_ownerId, "记录 " + PlayerIdentity.Describe(identity) +
                            " 是旧版本的创造仓库格式：改为创造仓库记录并清空槽位（会在角色创建时按创造仓库恢复）");
                        record.InventoryWasCreative = true;
                        record.SlotValues = Array.Empty<int>();
                        record.SlotCounts = Array.Empty<int>();
                        m_dirty = true;
                    }
                    m_records[identity] = record;
                }
                m_diagnostics?.Info(m_ownerId, "已加载联机角色记录 " + m_records.Count + " 条：" + path);
                if (m_dirty) SaveIfDirty();
            }
            catch (Exception ex)
            {
                // 明确失败：保持空表 + 本会话拒绝写盘（绝不把损坏文件覆盖成空表）。
                m_records.Clear();
                m_dirty = false;
                m_loadFailed = true;
                m_diagnostics?.Error(m_ownerId,
                    "联机角色记录文件损坏，解析失败；本会话保持空表并且不写盘（原文件保持原样）：" + path,
                    ex);
            }
        }

        /// <summary>写入一条记录（主机专属）。</summary>
        public bool Put(string recordKey, NetworkPlayerRecord record)
        {
            if (!m_isHost)
            {
                m_diagnostics?.Error(m_ownerId,
                    "拒绝写角色记录：本端不是主机（只有主机写 SuMultiplayerPlayers.xml）", null);
                return false;
            }
            if (record == null || string.IsNullOrEmpty(recordKey)) return false;
            m_records[recordKey] = record;
            m_dirty = true;
            return true;
        }

        /// <summary>
        /// 有改动就落盘（主机专属）。
        /// Source: ScMultiplayerProfileHandlers.cs:273-372
        /// </summary>
        public bool SaveIfDirty()
        {
            if (!m_isHost || !m_dirty) return false;
            if (string.IsNullOrEmpty(m_worldDirectory)) return false;
            if (m_loadFailed)
            {
                m_diagnostics?.Error(m_ownerId,
                    "拒绝写盘：本世界的角色记录文件先前解析失败，覆盖它会丢掉原有记录（原文件保持原样）",
                    null);
                return false;
            }
            try
            {
                var root = new XElement("SuMultiplayerPlayers", new XAttribute("Version", Version));
                foreach (KeyValuePair<string, NetworkPlayerRecord> item in
                    m_records.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    NetworkPlayerRecord record = item.Value;
                    if (record == null) continue;
                    root.Add(WritePlayer(item.Key, record));
                }
                string path = Storage.CombinePaths(m_worldDirectory, FileName);
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    new XDocument(root).Save(stream);
                m_dirty = false;
                return true;
            }
            catch (Exception ex)
            {
                m_diagnostics?.Error(m_ownerId,
                    "写联机角色记录失败：" + Storage.CombinePaths(m_worldDirectory, FileName), ex);
                return false;
            }
        }

        // ---------------------------------------------------------------- XML（逐字段对照旧实现）

        // Source: ScMultiplayerProfileHandlers.cs:148-262
        private static NetworkPlayerRecord ReadPlayer(XElement element) => new NetworkPlayerRecord
        {
            Name = (string)element.Attribute("Name") ?? "Player",
            NameConfirmed = PlayerRecordCodec.ParseBool((string)element.Attribute("NameConfirmed"),
                !PlayerIdentity.IsEngineDefaultName((string)element.Attribute("Name"))),
            PlayerClass = PlayerRecordCodec.ParsePlayerClass((string)element.Attribute("Class")),
            SkinName = (string)element.Attribute("Skin") ?? string.Empty,
            SkinSha256 = PlayerRecordCodec.ParseSkinHash((string)element.Attribute("SkinSha256")),
            Position = new Vector3(
                PlayerRecordCodec.ParseFloat((string)element.Attribute("X")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("Y")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("Z"))),
            SpawnPosition = new Vector3(
                PlayerRecordCodec.ParseFloat((string)element.Attribute("SpawnX")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("SpawnY")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("SpawnZ"))),
            Level = PlayerRecordCodec.ParseFloat((string)element.Attribute("Level"), PlayerRecordCodec.DefaultLevel),
            Health = PlayerRecordCodec.ParseFloat((string)element.Attribute("Health"), PlayerRecordCodec.DefaultHealth),
            Air = PlayerRecordCodec.ParseFloat((string)element.Attribute("Air"), PlayerRecordCodec.DefaultAir),
            Food = PlayerRecordCodec.ParseFloat((string)element.Attribute("Food"), PlayerRecordCodec.DefaultFood),
            Stamina = PlayerRecordCodec.ParseFloat((string)element.Attribute("Stamina"), PlayerRecordCodec.DefaultStamina),
            Sleep = PlayerRecordCodec.ParseFloat((string)element.Attribute("Sleep"), PlayerRecordCodec.DefaultSleep),
            Temperature = PlayerRecordCodec.ParseFloat((string)element.Attribute("Temperature"), PlayerRecordCodec.DefaultTemperature),
            TargetTemperature = PlayerRecordCodec.ParseFloat(
                (string)element.Attribute("TargetTemperature"), PlayerRecordCodec.DefaultTemperature),
            Wetness = PlayerRecordCodec.ParseFloat((string)element.Attribute("Wetness")),
            FluDuration = PlayerRecordCodec.ParseFloat((string)element.Attribute("FluDuration")),
            FluOnset = PlayerRecordCodec.ParseFloat((string)element.Attribute("FluOnset")),
            SicknessDuration = PlayerRecordCodec.ParseFloat((string)element.Attribute("SicknessDuration")),
            BodyRotation = new Quaternion(
                PlayerRecordCodec.ParseFloat((string)element.Attribute("BodyQX")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("BodyQY")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("BodyQZ")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("BodyQW"), 1f)),
            LookAngles = new Vector2(
                PlayerRecordCodec.ParseFloat((string)element.Attribute("LookX")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("LookY"))),
            FireDuration = PlayerRecordCodec.ParseFloat((string)element.Attribute("FireDuration")),
            IsCreativeFlying = PlayerRecordCodec.ParseBool((string)element.Attribute("CreativeFlying"), false),
            HasReceivedInitialItems = PlayerRecordCodec.ParseBool((string)element.Attribute("InitialItems"), true),
            InventoryWasCreative = PlayerRecordCodec.ParseBool((string)element.Attribute("CreativeInventory"), false),
            ActiveSlotIndex = (int?)element.Attribute("ActiveSlot") ?? 0,
            CreativeCategoryIndex = (int?)element.Attribute("CreativeCategory") ?? 0,
            CreativePageIndex = (int?)element.Attribute("CreativePage") ?? 0
        };

        // Source: ScMultiplayerProfileHandlers.cs:201-214
        private static void ReadInventory(XElement inventory,
            out int[] slotValues, out int[] slotCounts)
        {
            XElement[] slots = inventory?.Elements("Slot").OrderBy(slot =>
                (int?)slot.Attribute("Index") ?? 0).ToArray() ?? Array.Empty<XElement>();
            int slotsCount = slots.Length == 0 ? 0 : slots.Max(slot =>
                (int?)slot.Attribute("Index") ?? 0) + 1;
            slotValues = new int[slotsCount];
            slotCounts = new int[slotsCount];
            foreach (XElement slot in slots)
            {
                int index = (int?)slot.Attribute("Index") ?? -1;
                if (index < 0 || index >= slotsCount) continue;
                slotValues[index] = (int?)slot.Attribute("Value") ?? 0;
                slotCounts[index] = (int?)slot.Attribute("Count") ?? 0;
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:215-230
        private static void ReadHandcrafting(XElement handcrafting, NetworkPlayerRecord record)
        {
            XElement[] slots = handcrafting?.Elements("Slot").OrderBy(slot =>
                (int?)slot.Attribute("Index") ?? 0).ToArray() ?? Array.Empty<XElement>();
            int slotsCount = slots.Length == 0 ? 0 : slots.Max(slot =>
                (int?)slot.Attribute("Index") ?? 0) + 1;
            record.HandcraftSlotValues = new int[slotsCount];
            record.HandcraftSlotCounts = new int[slotsCount];
            foreach (XElement slot in slots)
            {
                int index = (int?)slot.Attribute("Index") ?? -1;
                if (index < 0 || index >= slotsCount) continue;
                record.HandcraftSlotValues[index] = (int?)slot.Attribute("Value") ?? 0;
                record.HandcraftSlotCounts[index] = (int?)slot.Attribute("Count") ?? 0;
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:231-239
        private static void ReadSatiation(XElement satiation, NetworkPlayerRecord record)
        {
            record.Satiation = new Dictionary<int, float>();
            foreach (XElement item in satiation?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                int value = (int?)item.Attribute("Value") ?? 0;
                float remaining = PlayerRecordCodec.ParseFloat((string)item.Attribute("Remaining"));
                if (value != 0 && remaining > 0f) record.Satiation[value] = remaining;
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:248-255
        private static void ReadClothes(XElement clothes, NetworkPlayerRecord record)
        {
            record.Clothes = PlayerRecordCodec.CreateEmptyClothes();
            foreach (XElement slot in clothes?.Elements("Slot") ?? Enumerable.Empty<XElement>())
            {
                int index = (int?)slot.Attribute("Index") ?? -1;
                if (index >= 0 && index < record.Clothes.Length)
                    record.Clothes[index] = PlayerRecordCodec.ParseIntArray((string)slot.Attribute("Values"));
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:283-361
        private static XElement WritePlayer(string recordKey, NetworkPlayerRecord record)
        {
            var player = new XElement("Player",
                new XAttribute("Identity", recordKey),
                new XAttribute("Name", record.Name ?? "Player"),
                new XAttribute("NameConfirmed", record.NameConfirmed),
                new XAttribute("Class", record.PlayerClass),
                new XAttribute("Skin", record.SkinName ?? string.Empty),
                new XAttribute("SkinSha256", PlayerRecordCodec.FormatSkinHash(record.SkinSha256)),
                new XAttribute("X", PlayerRecordCodec.FormatFloat(record.Position.X)),
                new XAttribute("Y", PlayerRecordCodec.FormatFloat(record.Position.Y)),
                new XAttribute("Z", PlayerRecordCodec.FormatFloat(record.Position.Z)),
                new XAttribute("SpawnX", PlayerRecordCodec.FormatFloat(record.SpawnPosition.X)),
                new XAttribute("SpawnY", PlayerRecordCodec.FormatFloat(record.SpawnPosition.Y)),
                new XAttribute("SpawnZ", PlayerRecordCodec.FormatFloat(record.SpawnPosition.Z)),
                new XAttribute("Level", PlayerRecordCodec.FormatFloat(record.Level)),
                new XAttribute("Health", PlayerRecordCodec.FormatFloat(record.Health)),
                new XAttribute("Air", PlayerRecordCodec.FormatFloat(record.Air)),
                new XAttribute("Food", PlayerRecordCodec.FormatFloat(record.Food)),
                new XAttribute("Stamina", PlayerRecordCodec.FormatFloat(record.Stamina)),
                new XAttribute("Sleep", PlayerRecordCodec.FormatFloat(record.Sleep)),
                new XAttribute("Temperature", PlayerRecordCodec.FormatFloat(record.Temperature)),
                new XAttribute("TargetTemperature", PlayerRecordCodec.FormatFloat(record.TargetTemperature)),
                new XAttribute("Wetness", PlayerRecordCodec.FormatFloat(record.Wetness)),
                new XAttribute("FluDuration", PlayerRecordCodec.FormatFloat(record.FluDuration)),
                new XAttribute("FluOnset", PlayerRecordCodec.FormatFloat(record.FluOnset)),
                new XAttribute("SicknessDuration", PlayerRecordCodec.FormatFloat(record.SicknessDuration)),
                new XAttribute("BodyQX", PlayerRecordCodec.FormatFloat(record.BodyRotation.X)),
                new XAttribute("BodyQY", PlayerRecordCodec.FormatFloat(record.BodyRotation.Y)),
                new XAttribute("BodyQZ", PlayerRecordCodec.FormatFloat(record.BodyRotation.Z)),
                new XAttribute("BodyQW", PlayerRecordCodec.FormatFloat(record.BodyRotation.W)),
                new XAttribute("LookX", PlayerRecordCodec.FormatFloat(record.LookAngles.X)),
                new XAttribute("LookY", PlayerRecordCodec.FormatFloat(record.LookAngles.Y)),
                new XAttribute("FireDuration", PlayerRecordCodec.FormatFloat(record.FireDuration)),
                new XAttribute("CreativeFlying", record.IsCreativeFlying),
                new XAttribute("InitialItems", record.HasReceivedInitialItems),
                new XAttribute("CreativeInventory", record.InventoryWasCreative),
                new XAttribute("ActiveSlot", record.ActiveSlotIndex),
                new XAttribute("CreativeCategory", record.CreativeCategoryIndex),
                new XAttribute("CreativePage", record.CreativePageIndex));
            var inventory = new XElement("Inventory");
            int slotsCount = Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0);
            for (int i = 0; i < slotsCount; i++)
            {
                inventory.Add(new XElement("Slot", new XAttribute("Index", i),
                    new XAttribute("Value", record.SlotValues[i]),
                    new XAttribute("Count", record.SlotCounts[i])));
            }
            player.Add(inventory);
            var handcrafting = new XElement("Handcrafting");
            int handcraftSlotsCount = Math.Min(
                record.HandcraftSlotValues?.Length ?? 0, record.HandcraftSlotCounts?.Length ?? 0);
            for (int i = 0; i < handcraftSlotsCount; i++)
            {
                if (!PlayerRecordCodec.ShouldPersistItem(
                    record.HandcraftSlotValues[i], record.HandcraftSlotCounts[i]))
                    continue;
                handcrafting.Add(new XElement("Slot", new XAttribute("Index", i),
                    new XAttribute("Value", record.HandcraftSlotValues[i]),
                    new XAttribute("Count", record.HandcraftSlotCounts[i])));
            }
            player.Add(handcrafting);
            var satiation = new XElement("Satiation");
            foreach (KeyValuePair<int, float> item in record.Satiation ?? new Dictionary<int, float>())
            {
                if (item.Key == 0 || item.Value <= 0f) continue;
                satiation.Add(new XElement("Item",
                    new XAttribute("Value", item.Key),
                    new XAttribute("Remaining", PlayerRecordCodec.FormatFloat(item.Value))));
            }
            player.Add(satiation);
            var clothes = new XElement("Clothes");
            int[][] clothesValues = record.Clothes ?? PlayerRecordCodec.CreateEmptyClothes();
            for (int i = 0; i < 4; i++)
            {
                clothes.Add(new XElement("Slot", new XAttribute("Index", i),
                    new XAttribute("Values", PlayerRecordCodec.FormatIntArray(
                        i < clothesValues.Length ? clothesValues[i] : null))));
            }
            player.Add(clothes);
            // 游戏统计：由客户端上报、存在角色记录里（不写 Project.xml）
            if (record.Stats != null && !record.Stats.IsEmpty) player.Add(record.Stats.ToXml());
            return player;
        }
    }
}

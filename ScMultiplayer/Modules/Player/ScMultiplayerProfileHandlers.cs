using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using ScMultiplayer.Control;
using ScMultiplayer.Diagnostics;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        private static bool IsValidRequestedProfile(GameWorldInfoMessage message)
        {
            if (message == null || !message.HasPlayerProfile ||
                !PlayerData.VerifyName((message.PlayerName ?? string.Empty).Trim()) ||
                string.IsNullOrWhiteSpace(message.PlayerIdentity) ||
                string.IsNullOrWhiteSpace(message.CharacterSkinName))
                return false;
            if (IsCustomSkinName(message.CharacterSkinName))
                return SkinHashCodec.IsValid(message.CharacterSkinSha256) &&
                    IsSkinClassCompatible(message.CharacterSkinName, message.PlayerClass);
            CharacterSkinsManager.UpdateCharacterSkinsList();
            if (!CharacterSkinsManager.CharacterSkinsNames.Contains(message.CharacterSkinName)) return false;
            PlayerClass? skinClass = CharacterSkinsManager.GetPlayerClass(message.CharacterSkinName);
            return !skinClass.HasValue || skinClass.Value == message.PlayerClass;
        }

        private static NetworkPlayerRecord CreateInitialPlayerRecord(GameWorldInfoMessage message)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            Vector3 position = players?.ComponentPlayers.FirstOrDefault()?.ComponentBody.Position ??
                players?.GlobalSpawnPosition ?? Vector3.Zero;
            return new NetworkPlayerRecord
            {
                Name = message.PlayerName.Trim(),
                PlayerClass = message.PlayerClass,
                SkinName = message.CharacterSkinName,
                SkinSha256 = SkinHashCodec.CloneBytes(message.CharacterSkinSha256),
                Position = position,
                Level = PlayerRecordValuePolicy.DefaultLevel,
                Health = PlayerRecordValuePolicy.DefaultHealth,
                HasReceivedInitialItems = false
            };
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        private static void InvokeInitialPlayerSpawn(PlayerData playerData, Vector3 position)
        {
            Type spawnModeType = typeof(PlayerData).GetNestedType(
                "SpawnMode", BindingFlags.NonPublic);
            if (spawnModeType == null)
                throw new MissingMemberException(typeof(PlayerData).FullName, "SpawnMode");
            object initialNoIntro = Enum.Parse(spawnModeType, "InitialNoIntro");
            // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.FindNoIntroSpawnPosition
            // Spread transient players around the requested host position before the native
            // collision/terrain search, and retain that result as their respawn anchor.
            float angle = 2f * MathUtils.PI * ((playerData.PlayerIndex - 1) % 3) / 3f;
            Vector3 desiredPosition = position + 3f * new Vector3(
                MathUtils.Cos(angle), 0f, MathUtils.Sin(angle));
            Vector3 spawnPosition = ModManager.ModParentMethod.InvokeParentMethod<Vector3>(
                playerData, "FindNoIntroSpawnPosition",
                new[] { typeof(Vector3), typeof(bool) }, desiredPosition, false);
            playerData.SpawnPosition = spawnPosition;
            ModManager.ModParentMethod.InvokeParentMethod(
                playerData, "SpawnPlayer", new[] { typeof(Vector3), spawnModeType },
                spawnPosition, initialNoIntro);
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        private static void InvokeClientPlaceholderPlayerSpawn(PlayerData playerData,
            Vector3 position)
        {
            Type spawnModeType = typeof(PlayerData).GetNestedType(
                "SpawnMode", BindingFlags.NonPublic);
            if (spawnModeType == null)
                throw new MissingMemberException(typeof(PlayerData).FullName, "SpawnMode");
            object respawn = Enum.Parse(spawnModeType, "Respawn");
            playerData.SpawnPosition = position;
            // Respawn creates only the local player entity. Starter items, clothing, and a possible
            // ocean boat remain host-authoritative and arrive in PlayerEquipmentMessage/world sync.
            ModManager.ModParentMethod.InvokeParentMethod(
                playerData, "SpawnPlayer", new[] { typeof(Vector3), spawnModeType },
                position, respawn);
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
        // Network identity records are independent from the base game's fixed local player slots.
        // A successful host sleep establishes the next death respawn anchor, not the logout point.
        private void UpdateNetworkPlayerRespawnAnchor(int clientId, PlayerData playerData)
        {
            if (!IsHost || clientId <= 0 || playerData?.ComponentPlayer?.ComponentBody == null ||
                !m_clientRecordKeys.TryGetValue(clientId, out string recordKey))
                return;
            Vector3 anchor = playerData.ComponentPlayer.ComponentBody.Position;
            playerData.SpawnPosition = anchor;
            NetworkPlayerRecord record = CapturePlayerRecord(playerData);
            record.SpawnPosition = anchor;
            m_playerRecords[recordKey] = record;
            m_playerRecordsDirty = true;
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.SaveProject
        // The multiplayer file is a sibling of Project.xml and is ignored by the base game.
        private void EnsurePlayerRecordsLoaded()
        {
            if (!IsHost) return;
            string directory = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false)?.DirectoryName;
            if (string.IsNullOrEmpty(directory) ||
                string.Equals(directory, m_playerRecordsWorldDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            m_playerRecords.Clear();
            m_playerRecordsWorldDirectory = directory;
            m_playerRecordsDirty = false;
            string path = Storage.CombinePaths(directory, PlayerRecordsFileName);
            if (!Storage.FileExists(path)) return;
            try
            {
                XDocument document;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    document = XDocument.Load(stream);
                foreach (XElement element in document.Root?.Elements("Player") ?? Enumerable.Empty<XElement>())
                {
                    string identity = (string)element.Attribute("Identity");
                    if (string.IsNullOrWhiteSpace(identity)) continue;
                    var record = new NetworkPlayerRecord
                    {
                        Name = (string)element.Attribute("Name") ?? "Player",
                        PlayerClass = PlayerProfileValueCodec.ParsePlayerClass((string)element.Attribute("Class")),
                        SkinName = (string)element.Attribute("Skin") ?? string.Empty,
                        SkinSha256 = SkinHashCodec.Parse((string)element.Attribute("SkinSha256")),
                        Position = new Vector3(
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("X")),
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("Y")),
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("Z"))),
                        SpawnPosition = new Vector3(
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("SpawnX")),
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("SpawnY")),
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("SpawnZ"))),
                        Level = PlayerRecordValuePolicy.ParseFloat((string)element.Attribute("Level"), PlayerRecordValuePolicy.DefaultLevel),
                        Health = PlayerRecordValuePolicy.ParseFloat((string)element.Attribute("Health"), PlayerRecordValuePolicy.DefaultHealth),
                        Air = PlayerRecordValuePolicy.ParseFloat((string)element.Attribute("Air"), PlayerRecordValuePolicy.DefaultAir),
                        Food = PlayerRecordValuePolicy.ParseFloat((string)element.Attribute("Food"), PlayerRecordValuePolicy.DefaultFood),
                        Stamina = PlayerRecordValuePolicy.ParseFloat((string)element.Attribute("Stamina"), PlayerRecordValuePolicy.DefaultStamina),
                        Sleep = PlayerRecordValuePolicy.ParseFloat((string)element.Attribute("Sleep"), PlayerRecordValuePolicy.DefaultSleep),
                        Temperature = PlayerRecordValuePolicy.ParseFloat((string)element.Attribute("Temperature"), PlayerRecordValuePolicy.DefaultTemperature),
                        TargetTemperature = PlayerProfileValueCodec.ParseFloat(
                            (string)element.Attribute("TargetTemperature"), PlayerRecordValuePolicy.DefaultTemperature),
                        Wetness = PlayerProfileValueCodec.ParseFloat((string)element.Attribute("Wetness")),
                        FluDuration = PlayerProfileValueCodec.ParseFloat((string)element.Attribute("FluDuration")),
                        FluOnset = PlayerProfileValueCodec.ParseFloat((string)element.Attribute("FluOnset")),
                        SicknessDuration = PlayerProfileValueCodec.ParseFloat(
                            (string)element.Attribute("SicknessDuration")),
                        BodyRotation = new Quaternion(
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("BodyQX")),
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("BodyQY")),
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("BodyQZ")),
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("BodyQW"), 1f)),
                        LookAngles = new Vector2(
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("LookX")),
                            PlayerProfileValueCodec.ParseFloat((string)element.Attribute("LookY"))),
                        FireDuration = PlayerProfileValueCodec.ParseFloat(
                            (string)element.Attribute("FireDuration")),
                        IsCreativeFlying = PlayerProfileValueCodec.ParseBool(
                            (string)element.Attribute("CreativeFlying"), false),
                        HasReceivedInitialItems = PlayerProfileValueCodec.ParseBool(
                            (string)element.Attribute("InitialItems"), true),
                        InventoryWasCreative = PlayerProfileValueCodec.ParseBool(
                            (string)element.Attribute("CreativeInventory"), false),
                        ActiveSlotIndex = (int?)element.Attribute("ActiveSlot") ?? 0,
                        CreativeCategoryIndex =
                            (int?)element.Attribute("CreativeCategory") ?? 0,
                        CreativePageIndex = (int?)element.Attribute("CreativePage") ?? 0
                    };
                    // Legacy records did not persist SpawnPosition. Keep their previous saved
                    // position as the respawn anchor instead of falling back to the creation point.
                    if (record.SpawnPosition == Vector3.Zero)
                        record.SpawnPosition = record.Position;
                    XElement inventory = element.Element("Inventory");
                    XElement[] slots = inventory?.Elements("Slot").OrderBy(slot =>
                        (int?)slot.Attribute("Index") ?? 0).ToArray() ?? Array.Empty<XElement>();
                    int slotsCount = slots.Length == 0 ? 0 : slots.Max(slot =>
                        (int?)slot.Attribute("Index") ?? 0) + 1;
                    record.SlotValues = new int[slotsCount];
                    record.SlotCounts = new int[slotsCount];
                    foreach (XElement slot in slots)
                    {
                        int index = (int?)slot.Attribute("Index") ?? -1;
                        if (index < 0 || index >= slotsCount) continue;
                        record.SlotValues[index] = (int?)slot.Attribute("Value") ?? 0;
                        record.SlotCounts[index] = (int?)slot.Attribute("Count") ?? 0;
                    }
                    XElement handcrafting = element.Element("Handcrafting");
                    XElement[] handcraftSlots = handcrafting?.Elements("Slot").OrderBy(slot =>
                        (int?)slot.Attribute("Index") ?? 0).ToArray() ?? Array.Empty<XElement>();
                    int handcraftSlotsCount = handcraftSlots.Length == 0 ? 0 :
                        handcraftSlots.Max(slot => (int?)slot.Attribute("Index") ?? 0) + 1;
                    record.HandcraftSlotValues = new int[handcraftSlotsCount];
                    record.HandcraftSlotCounts = new int[handcraftSlotsCount];
                    foreach (XElement slot in handcraftSlots)
                    {
                        int index = (int?)slot.Attribute("Index") ?? -1;
                        if (index < 0 || index >= handcraftSlotsCount) continue;
                        record.HandcraftSlotValues[index] =
                            (int?)slot.Attribute("Value") ?? 0;
                        record.HandcraftSlotCounts[index] =
                            (int?)slot.Attribute("Count") ?? 0;
                    }
                    record.Satiation = new Dictionary<int, float>();
                    foreach (XElement item in element.Element("Satiation")?.Elements("Item") ??
                        Enumerable.Empty<XElement>())
                    {
                        int value = (int?)item.Attribute("Value") ?? 0;
                        float remaining = PlayerProfileValueCodec.ParseFloat((string)item.Attribute("Remaining"));
                        if (value != 0 && remaining > 0f)
                            record.Satiation[value] = remaining;
                    }
                    if (!record.InventoryWasCreative &&
                        LooksLikeLegacyCreativeInventory(record))
                    {
                        record.InventoryWasCreative = true;
                        record.SlotValues = Array.Empty<int>();
                        record.SlotCounts = Array.Empty<int>();
                        m_playerRecordsDirty = true;
                    }
                    record.Clothes = PlayerProfileValueCodec.CreateEmptyClothes();
                    foreach (XElement slot in element.Element("Clothes")?.Elements("Slot") ??
                        Enumerable.Empty<XElement>())
                    {
                        int index = (int?)slot.Attribute("Index") ?? -1;
                        if (index >= 0 && index < record.Clothes.Length)
                            record.Clothes[index] = PlayerProfileValueCodec.ParseIntArray((string)slot.Attribute("Values"));
                    }
                    if (element.Attribute("InitialItems") == null)
                    {
                        bool hasClothes = record.Clothes.Any(slot => slot != null && slot.Length > 0);
                        record.HasReceivedInitialItems = hasClothes;
                    }
                    m_playerRecords[identity] = record;
                }
                Log.Information($"[ScMP] Loaded {m_playerRecords.Count} network player records");
                if (m_playerRecordsDirty) SavePlayerRecords();
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Failed to load network player records: {ex.Message}");
            }
        }

        private void SavePlayerRecords()
        {
            if (!IsHost || !m_playerRecordsDirty || string.IsNullOrEmpty(m_playerRecordsWorldDirectory)) return;
            try
            {
                var root = new XElement("ScMultiplayerPlayers", new XAttribute("Version", 5));
                foreach (KeyValuePair<string, NetworkPlayerRecord> item in m_playerRecords.OrderBy(pair => pair.Key))
                {
                    NetworkPlayerRecord record = item.Value;
                    if (record == null) continue;
                    var player = new XElement("Player",
                        new XAttribute("Identity", item.Key),
                        new XAttribute("Name", record.Name ?? "Player"),
                        new XAttribute("Class", record.PlayerClass),
                        new XAttribute("Skin", record.SkinName ?? string.Empty),
                        new XAttribute("SkinSha256", SkinHashCodec.Format(record.SkinSha256)),
                        new XAttribute("X", PlayerProfileValueCodec.FormatFloat(record.Position.X)),
                        new XAttribute("Y", PlayerProfileValueCodec.FormatFloat(record.Position.Y)),
                        new XAttribute("Z", PlayerProfileValueCodec.FormatFloat(record.Position.Z)),
                        new XAttribute("SpawnX", PlayerProfileValueCodec.FormatFloat(record.SpawnPosition.X)),
                        new XAttribute("SpawnY", PlayerProfileValueCodec.FormatFloat(record.SpawnPosition.Y)),
                        new XAttribute("SpawnZ", PlayerProfileValueCodec.FormatFloat(record.SpawnPosition.Z)),
                        new XAttribute("Level", PlayerProfileValueCodec.FormatFloat(record.Level)),
                        new XAttribute("Health", PlayerProfileValueCodec.FormatFloat(record.Health)),
                        new XAttribute("Air", PlayerProfileValueCodec.FormatFloat(record.Air)),
                        new XAttribute("Food", PlayerProfileValueCodec.FormatFloat(record.Food)),
                        new XAttribute("Stamina", PlayerProfileValueCodec.FormatFloat(record.Stamina)),
                        new XAttribute("Sleep", PlayerProfileValueCodec.FormatFloat(record.Sleep)),
                        new XAttribute("Temperature", PlayerProfileValueCodec.FormatFloat(record.Temperature)),
                        new XAttribute("TargetTemperature", PlayerProfileValueCodec.FormatFloat(record.TargetTemperature)),
                        new XAttribute("Wetness", PlayerProfileValueCodec.FormatFloat(record.Wetness)),
                        new XAttribute("FluDuration", PlayerProfileValueCodec.FormatFloat(record.FluDuration)),
                        new XAttribute("FluOnset", PlayerProfileValueCodec.FormatFloat(record.FluOnset)),
                        new XAttribute("SicknessDuration", PlayerProfileValueCodec.FormatFloat(record.SicknessDuration)),
                        new XAttribute("BodyQX", PlayerProfileValueCodec.FormatFloat(record.BodyRotation.X)),
                        new XAttribute("BodyQY", PlayerProfileValueCodec.FormatFloat(record.BodyRotation.Y)),
                        new XAttribute("BodyQZ", PlayerProfileValueCodec.FormatFloat(record.BodyRotation.Z)),
                        new XAttribute("BodyQW", PlayerProfileValueCodec.FormatFloat(record.BodyRotation.W)),
                        new XAttribute("LookX", PlayerProfileValueCodec.FormatFloat(record.LookAngles.X)),
                        new XAttribute("LookY", PlayerProfileValueCodec.FormatFloat(record.LookAngles.Y)),
                        new XAttribute("FireDuration", PlayerProfileValueCodec.FormatFloat(record.FireDuration)),
                        new XAttribute("CreativeFlying", record.IsCreativeFlying),
                        new XAttribute("InitialItems", record.HasReceivedInitialItems),
                        new XAttribute("CreativeInventory", record.InventoryWasCreative),
                        new XAttribute("ActiveSlot", record.ActiveSlotIndex),
                        new XAttribute("CreativeCategory", record.CreativeCategoryIndex),
                        new XAttribute("CreativePage", record.CreativePageIndex));
                    var inventory = new XElement("Inventory");
                    int slotsCount = Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0);
                    for (int i = 0; i < slotsCount; i++)
                        inventory.Add(new XElement("Slot", new XAttribute("Index", i),
                            new XAttribute("Value", record.SlotValues[i]),
                            new XAttribute("Count", record.SlotCounts[i])));
                    player.Add(inventory);
                    var handcrafting = new XElement("Handcrafting");
                    int handcraftSlotsCount = Math.Min(
                        record.HandcraftSlotValues?.Length ?? 0,
                        record.HandcraftSlotCounts?.Length ?? 0);
                    for (int i = 0; i < handcraftSlotsCount; i++)
                    {
                        if (!PlayerRecordValuePolicy.ShouldPersistItem(
                            record.HandcraftSlotValues[i], record.HandcraftSlotCounts[i]))
                            continue;
                        handcrafting.Add(new XElement("Slot", new XAttribute("Index", i),
                            new XAttribute("Value", record.HandcraftSlotValues[i]),
                            new XAttribute("Count", record.HandcraftSlotCounts[i])));
                    }
                    player.Add(handcrafting);
                    var satiation = new XElement("Satiation");
                    foreach (KeyValuePair<int, float> satiationItem in
                        record.Satiation ?? new Dictionary<int, float>())
                    {
                        if (satiationItem.Key == 0 || satiationItem.Value <= 0f) continue;
                        satiation.Add(new XElement("Item",
                            new XAttribute("Value", satiationItem.Key),
                            new XAttribute("Remaining", PlayerProfileValueCodec.FormatFloat(satiationItem.Value))));
                    }
                    player.Add(satiation);
                    var clothes = new XElement("Clothes");
                    int[][] clothesValues = record.Clothes ?? PlayerProfileValueCodec.CreateEmptyClothes();
                    for (int i = 0; i < 4; i++)
                        clothes.Add(new XElement("Slot", new XAttribute("Index", i),
                            new XAttribute("Values", PlayerProfileValueCodec.FormatIntArray(
                                i < clothesValues.Length ? clothesValues[i] : null))));
                    player.Add(clothes);
                    root.Add(player);
                }
                string path = Storage.CombinePaths(m_playerRecordsWorldDirectory, PlayerRecordsFileName);
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    new XDocument(root).Save(stream);
                m_playerRecordsDirty = false;
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Failed to save network player records: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Save
        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.Save
        private static NetworkPlayerRecord CapturePlayerRecord(PlayerData playerData)
        {
            ComponentPlayer player = playerData?.ComponentPlayer;
            ComponentVitalStats vitalStats = player?.ComponentVitalStats;
            ComponentFlu flu = player?.Entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = player?.Entity.FindComponent<ComponentSickness>();
            ComponentOnFire onFire = player?.Entity.FindComponent<ComponentOnFire>();
            ComponentCraftingTable handcrafting = player?.Entity.FindComponent<ComponentCraftingTable>();
            var record = new NetworkPlayerRecord
            {
                Name = playerData?.Name ?? "Player",
                PlayerClass = playerData?.PlayerClass ?? PlayerClass.Male,
                SkinName = playerData?.CharacterSkinName ?? string.Empty,
                SkinSha256 = GetLocalCharacterSkinSha256(playerData?.CharacterSkinName),
                Position = player?.ComponentBody.Position ?? playerData?.SpawnPosition ?? Vector3.Zero,
                SpawnPosition = playerData?.SpawnPosition ?? Vector3.Zero,
                Level = playerData?.Level ?? PlayerRecordValuePolicy.DefaultLevel,
                Health = player?.ComponentHealth?.Health ?? PlayerRecordValuePolicy.DefaultHealth,
                Air = player?.ComponentHealth?.Air ?? PlayerRecordValuePolicy.DefaultAir,
                Food = vitalStats?.Food ?? PlayerRecordValuePolicy.DefaultFood,
                Stamina = vitalStats?.Stamina ?? PlayerRecordValuePolicy.DefaultStamina,
                Sleep = vitalStats?.Sleep ?? PlayerRecordValuePolicy.DefaultSleep,
                Temperature = vitalStats?.Temperature ?? PlayerRecordValuePolicy.DefaultTemperature,
                TargetTemperature = vitalStats != null
                    ? ModManager.ModParentField.GetParentField<float>(
                        vitalStats, "m_targetTemperature", typeof(ComponentVitalStats))
                    : PlayerRecordValuePolicy.DefaultTemperature,
                Wetness = vitalStats?.Wetness ?? 0f,
                FluDuration = flu != null
                    ? ModManager.ModParentField.GetParentField<float>(
                        flu, "m_fluDuration", typeof(ComponentFlu))
                    : 0f,
                FluOnset = flu != null
                    ? ModManager.ModParentField.GetParentField<float>(
                        flu, "m_fluOnset", typeof(ComponentFlu))
                    : 0f,
                SicknessDuration = sickness != null
                    ? ModManager.ModParentField.GetParentField<float>(
                        sickness, "m_sicknessDuration", typeof(ComponentSickness))
                    : 0f,
                BodyRotation = player?.ComponentBody?.Rotation ?? Quaternion.Identity,
                LookAngles = player?.ComponentLocomotion?.LookAngles ?? Vector2.Zero,
                FireDuration = onFire != null
                    ? ModManager.ModParentField.GetParentField<float>(
                        onFire, "m_fireDuration", typeof(ComponentOnFire))
                    : 0f,
                Satiation = CapturePlayerSatiation(vitalStats),
                IsCreativeFlying = player?.ComponentLocomotion?.IsCreativeFlyEnabled == true,
                HasReceivedInitialItems = true,
                Clothes = CaptureClothes(player)
            };
            IInventory inventory = player?.ComponentMiner?.Inventory;
            record.InventoryWasCreative = inventory is ComponentCreativeInventory;
            record.ActiveSlotIndex = inventory?.ActiveSlotIndex ?? 0;
            if (inventory is ComponentCreativeInventory creativeInventory)
            {
                record.CreativeCategoryIndex = creativeInventory.CategoryIndex;
                record.CreativePageIndex = creativeInventory.PageIndex;
                int slotsCount = Math.Min(creativeInventory.OpenSlotsCount,
                    creativeInventory.SlotsCount);
                record.SlotValues = new int[slotsCount];
                record.SlotCounts = new int[slotsCount];
                for (int i = 0; i < slotsCount; i++)
                {
                    record.SlotValues[i] = creativeInventory.GetSlotValue(i);
                    record.SlotCounts[i] = creativeInventory.GetSlotCount(i);
                }
            }
            else if (inventory != null)
            {
                record.SlotValues = new int[inventory.SlotsCount];
                record.SlotCounts = new int[inventory.SlotsCount];
                for (int i = 0; i < inventory.SlotsCount; i++)
                {
                    record.SlotValues[i] = inventory.GetSlotValue(i);
                    record.SlotCounts[i] = inventory.GetSlotCount(i);
                }
            }
            CapturePersistentCraftingSlots(handcrafting, out record.HandcraftSlotValues,
                out record.HandcraftSlotCounts);
            return record;
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Save
        private static Dictionary<int, float> CapturePlayerSatiation(
            ComponentVitalStats vitalStats)
        {
            if (vitalStats == null) return new Dictionary<int, float>();
            Dictionary<int, float> satiation = ModManager.ModParentField
                .GetParentField<Dictionary<int, float>>(
                    vitalStats, "m_satiation", typeof(ComponentVitalStats));
            return satiation != null
                ? satiation.Where(item => item.Key != 0 && item.Value > 0f)
                    .ToDictionary(item => item.Key, item => item.Value)
                : new Dictionary<int, float>();
        }

        // Source: Survivalcraft/Game/ComponentCraftingTable.cs:
        // ComponentCraftingTable.UpdateCraftingResult
        private static void CapturePersistentCraftingSlots(ComponentCraftingTable craftingTable,
            out int[] values, out int[] counts)
        {
            if (craftingTable == null)
            {
                values = Array.Empty<int>();
                counts = Array.Empty<int>();
                return;
            }
            values = CaptureInventoryValues(craftingTable);
            counts = CaptureInventoryCounts(craftingTable);
            values[craftingTable.ResultSlotIndex] = 0;
            counts[craftingTable.ResultSlotIndex] = 0;
        }

        // Source: Survivalcraft/Game/ComponentCreativeInventory.cs:ComponentCreativeInventory.GetSlotCount
        private static bool LooksLikeLegacyCreativeInventory(NetworkPlayerRecord record)
        {
            if (record == null) return false;
            if (record.InventoryWasCreative) return false;
            int slotsCount = Math.Min(record.SlotValues?.Length ?? 0,
                record.SlotCounts?.Length ?? 0);
            if (slotsCount > 64) return true;
            int creativeStacks = 0;
            for (int i = 0; i < slotsCount; i++)
            {
                if (record.SlotCounts[i] >= 9999 && ++creativeStacks >= 8)
                    return true;
            }
            return false;
        }

        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.AddSlotItems
        private static void RestorePlayerRecordInventory(IInventory inventory,
            NetworkPlayerRecord record)
        {
            if (inventory == null || record == null) return;
            if (inventory is ComponentCreativeInventory creativeInventory)
            {
                int creativeSlotsCount = Math.Min(creativeInventory.OpenSlotsCount,
                    Math.Min(record.SlotValues?.Length ?? 0,
                        record.SlotCounts?.Length ?? 0));
                for (int i = 0; i < creativeSlotsCount; i++)
                {
                    creativeInventory.RemoveSlotItems(i, int.MaxValue);
                    if (record.SlotValues[i] != 0 && record.SlotCounts[i] > 0)
                        creativeInventory.AddSlotItems(i, record.SlotValues[i], 1);
                }
                creativeInventory.CategoryIndex = Math.Max(record.CreativeCategoryIndex, 0);
                creativeInventory.PageIndex = Math.Max(record.CreativePageIndex, 0);
                creativeInventory.ActiveSlotIndex = record.ActiveSlotIndex;
                return;
            }
            if (record.InventoryWasCreative || LooksLikeLegacyCreativeInventory(record)) return;
            int slotsCount = Math.Min(inventory.SlotsCount,
                Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0));
            for (int i = 0; i < slotsCount; i++)
            {
                int value = record.SlotValues[i];
                int count = record.SlotCounts[i];
                inventory.RemoveSlotItems(i, int.MaxValue);
                if (value == 0 || count <= 0) continue;
                int capacity;
                try
                {
                    capacity = inventory.GetSlotCapacity(i, value);
                }
                catch
                {
                    continue;
                }
                count = Math.Min(count, capacity);
                if (count > 0) inventory.AddSlotItems(i, value, count);
            }
            inventory.ActiveSlotIndex = record.ActiveSlotIndex;
        }

        // Source: Survivalcraft/Game/FullInventoryWidget.cs:FullInventoryWidget
        // The result slot is derived and intentionally absent from the player record.
        private static void RestorePlayerRecordCrafting(ComponentCraftingTable craftingTable,
            NetworkPlayerRecord record)
        {
            if (craftingTable == null || record == null ||
                record.HandcraftSlotValues == null || record.HandcraftSlotCounts == null)
                return;
            ApplyInventory(craftingTable, record.HandcraftSlotValues,
                record.HandcraftSlotCounts);
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Load
        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Load
        // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.Load
        private static void ApplyPlayerRecordState(ComponentPlayer player,
            NetworkPlayerRecord record)
        {
            if (player == null || record == null) return;
            if (player.ComponentBody != null)
                player.ComponentBody.Rotation = record.BodyRotation;
            if (player.ComponentLocomotion != null)
                ModManager.ModParentField.ModifyParentField(
                    player.ComponentLocomotion, "m_lookAngles", record.LookAngles,
                    typeof(ComponentLocomotion));
            bool creativeInventory = player.ComponentMiner?.Inventory is ComponentCreativeInventory;
            if (player.ComponentLocomotion != null)
                player.ComponentLocomotion.IsCreativeFlyEnabled =
                    creativeInventory && record.IsCreativeFlying;
            ComponentVitalStats vitalStats = player.ComponentVitalStats;
            if (vitalStats != null)
            {
                float targetTemperature = MathUtils.Clamp(record.TargetTemperature, 0f, 24f);
                ModManager.ModParentField.ModifyParentField(vitalStats,
                    "m_targetTemperature", targetTemperature, typeof(ComponentVitalStats));
                (vitalStats as SuComponentVitalStats)?
                    .ApplyAuthoritativeTargetTemperature(targetTemperature);
                Dictionary<int, float> satiation = ModManager.ModParentField
                    .GetParentField<Dictionary<int, float>>(
                        vitalStats, "m_satiation", typeof(ComponentVitalStats));
                if (satiation != null)
                {
                    satiation.Clear();
                    foreach (KeyValuePair<int, float> item in
                        record.Satiation ?? new Dictionary<int, float>())
                    {
                        if (item.Key != 0 && item.Value > 0f)
                            satiation[item.Key] = item.Value;
                    }
                }
            }
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            if (flu != null)
            {
                ModManager.ModParentField.ModifyParentField(flu, "m_fluDuration",
                    MathUtils.Max(record.FluDuration, 0f), typeof(ComponentFlu));
                ModManager.ModParentField.ModifyParentField(flu, "m_fluOnset",
                    MathUtils.Max(record.FluOnset, 0f), typeof(ComponentFlu));
            }
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            if (sickness != null)
                ModManager.ModParentField.ModifyParentField(sickness, "m_sicknessDuration",
                    MathUtils.Max(record.SicknessDuration, 0f), typeof(ComponentSickness));
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            if (onFire != null)
            {
                if (record.FireDuration > 0f)
                    onFire.SetOnFire(null, record.FireDuration);
                else
                    onFire.Extinguish();
            }
        }

        private static int[][] CaptureClothes(ComponentPlayer player)
        {
            int[][] result = PlayerProfileValueCodec.CreateEmptyClothes();
            ComponentClothing clothing = player?.Entity.FindComponent<ComponentClothing>();
            if (clothing == null) return result;
            for (int i = 0; i < result.Length; i++)
                result[i] = clothing.GetClothes((ClothingSlot)i).ToArray();
            return result;
        }

        private static void ApplyClothes(ComponentPlayer player, int[][] clothes)
        {
            if (player == null || clothes == null) return;
            ComponentClothing clothing = player.Entity.FindComponent<ComponentClothing>();
            if (clothing == null) return;
            for (int i = 0; i < Math.Min(4, clothes.Length); i++)
                clothing.SetClothes((ClothingSlot)i, clothes[i] ?? Array.Empty<int>());
        }

        private void RefreshHostPlayerRecords()
        {
            if (!IsHost) return;
            EnsurePlayerRecordsLoaded();
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
            {
                if (!m_clientRecordKeys.TryGetValue(item.Key, out string recordKey) ||
                    item.Value?.ComponentPlayer == null) continue;
                NetworkPlayerRecord record = CapturePlayerRecord(item.Value);
                ApplySessionSkinHash(item.Key, record);
                m_playerRecords[recordKey] = record;
                m_playerRecordsDirty = true;
            }
        }

        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.GetClothes
        private void SynchronizePlayerProfiles()
        {
            Project project = GameManager.Project;
            if (client?.IsConnected != true || project == null) return;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;

            if (IsHost)
            {
                ComponentPlayer hostPlayer = players.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
                if (hostPlayer != null)
                    NetworkMessageSender.SendPlayerProfileMessage(
                        client.ClientID, CapturePlayerRecord(hostPlayer.PlayerData));
                foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
                {
                    if (item.Value?.ComponentPlayer != null)
                    {
                        NetworkPlayerRecord record = CapturePlayerRecord(item.Value);
                        ApplySessionSkinHash(item.Key, record);
                        NetworkMessageSender.SendPlayerProfileMessage(
                            item.Key, record);
                    }
                }
            }
            else
            {
                ComponentPlayer localPlayer = players.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
                if (localPlayer != null)
                    NetworkMessageSender.SendPlayerProfileMessage(
                        client.ClientID, CapturePlayerRecord(localPlayer.PlayerData));
            }
        }

        private void HandlePlayerProfileMessage(PlayerProfileMessage message, int sourceClientId)
        {
            if (message == null) return;
            if (IsHost)
            {
                if (sourceClientId <= 0 || message.ClientId != sourceClientId ||
                    !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) ||
                    playerData?.ComponentPlayer == null || playerData.PlayerClass != message.PlayerClass)
                    return;
                if (PlayerData.VerifyName((message.Name ?? string.Empty).Trim()))
                    playerData.Name = message.Name.Trim();
                if (IsProfileSkinAccepted(message.SkinName, message.SkinSha256,
                    playerData.PlayerClass))
                {
                    playerData.CharacterSkinName = message.SkinName;
                    RegisterPlayerSkinHash(sourceClientId, message.SkinName,
                        message.SkinSha256);
                    RequestSkinAssetIfMissing(sourceClientId, message.SkinName,
                        playerData.PlayerClass, message.SkinSha256);
                }
                if (!m_equipmentSynchronizedClients.Contains(sourceClientId))
                    ApplyClothes(playerData.ComponentPlayer, message.Clothes);
                if (m_clientRecordKeys.TryGetValue(sourceClientId, out string recordKey))
                {
                    m_playerRecords[recordKey] = CapturePlayerRecord(playerData);
                    m_playerRecordsDirty = true;
                }
                return;
            }

            if (sourceClientId != 0) return;
            if (message.ClientId == client.ClientID)
            {
                ApplyProfileToLocalPlayer(message);
                return;
            }
            if (m_departedRemoteClientIds.Contains(message.ClientId))
                return;

            string networkKey = PlayerRecordKeyResolver.GetNetworkRecordKey(message.ClientId);
            NetworkPlayerRecord record = m_playerRecords.TryGetValue(networkKey, out NetworkPlayerRecord existing)
                ? existing : new NetworkPlayerRecord();
            record.Name = message.Name;
            record.PlayerClass = message.PlayerClass;
            record.SkinName = message.SkinName;
            record.SkinSha256 = SkinHashCodec.CloneBytes(message.SkinSha256);
            record.Clothes = message.Clothes;
            RegisterPlayerSkinHash(message.ClientId, record);
            RequestSkinAssetIfMissing(message.ClientId, record.SkinName,
                record.PlayerClass, record.SkinSha256);

            if (m_networkPlayerData.TryGetValue(message.ClientId, out PlayerData remotePlayer) &&
                remotePlayer.PlayerClass != record.PlayerClass)
            {
                RemoveNetworkPlayer(message.ClientId);
                m_playerRecords[networkKey] = record;
                CreateNetworkPlayer(message.ClientId, record.Name, networkKey);
                return;
            }

            m_playerRecords[networkKey] = record;
            if (remotePlayer?.ComponentPlayer != null)
            {
                remotePlayer.Name = record.Name;
                remotePlayer.CharacterSkinName = record.SkinName;
                if (!m_equipmentSynchronizedClients.Contains(message.ClientId))
                    ApplyClothes(remotePlayer.ComponentPlayer, record.Clothes);
                ApplySessionSkinToPlayer(remotePlayer.ComponentPlayer,
                    message.ClientId, record);
            }
            else
            {
                CreateNetworkPlayer(message.ClientId, record.Name, networkKey);
            }
        }

        private static bool IsSkinValidForClass(string skinName, PlayerClass playerClass)
        {
            if (string.IsNullOrWhiteSpace(skinName)) return false;
            CharacterSkinsManager.UpdateCharacterSkinsList();
            if (!CharacterSkinsManager.CharacterSkinsNames.Contains(skinName)) return false;
            PlayerClass? skinClass = CharacterSkinsManager.GetPlayerClass(skinName);
            return !skinClass.HasValue || skinClass.Value == playerClass;
        }

        private static bool IsCustomSkinName(string skinName) =>
            !string.IsNullOrWhiteSpace(skinName) &&
            !skinName.TrimStart().StartsWith("$", StringComparison.Ordinal) &&
            string.Equals(Storage.GetExtension(skinName), ".scskin",
                StringComparison.OrdinalIgnoreCase);

        private static bool IsSkinClassCompatible(string skinName, PlayerClass playerClass)
        {
            if (string.IsNullOrWhiteSpace(skinName)) return false;
            PlayerClass? skinClass = CharacterSkinsManager.GetPlayerClass(skinName);
            return !skinClass.HasValue || skinClass.Value == playerClass;
        }

        // Source: Survivalcraft/Game/CharacterSkinsManager.cs:CharacterSkinsManager.GetFileName
        // Source: Survivalcraft/Game/CharacterSkinsManager.cs:CharacterSkinsManager.ValidateCharacterSkin
        private static byte[] GetLocalCharacterSkinSha256(string skinName)
        {
            return TryReadLocalCharacterSkinAsset(skinName, null, out _, out byte[] hash)
                ? hash
                : Array.Empty<byte>();
        }

        // Source: Survivalcraft/Game/CharacterSkinsManager.cs:CharacterSkinsManager.LoadTexture
        private static bool TryReadLocalCharacterSkinAsset(string skinName,
            byte[] expectedHash, out byte[] data, out byte[] hash)
        {
            data = Array.Empty<byte>();
            hash = Array.Empty<byte>();
            if (!IsCustomSkinName(skinName)) return false;
            string fileName = CharacterSkinsManager.GetFileName(skinName);
            if (string.IsNullOrEmpty(fileName) || !Storage.FileExists(fileName)) return false;
            try
            {
                using Stream stream = Storage.OpenFile(fileName, OpenFileMode.Read);
                data = BoundedStreamReader.Read(stream, MaximumSkinAssetBytes);
                hash = SHA256.HashData(data);
                if (SkinHashCodec.IsValid(expectedHash) && !hash.SequenceEqual(expectedHash))
                    return false;
                ValidateSkinAssetData(data, skinName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[ScMP] Could not read character skin \"{skinName}\": {ex.Message}");
                data = Array.Empty<byte>();
                hash = Array.Empty<byte>();
                return false;
            }
        }

        // Source: Survivalcraft/Game/CharacterSkinsManager.cs:CharacterSkinsManager.ValidateCharacterSkin
        private static void ValidateSkinAssetData(byte[] data, string skinName)
        {
            SkinImageValidator.Validate(data, MaximumSkinAssetBytes);
            if (!IsSkinClassCompatible(skinName, CharacterSkinsManager.GetPlayerClass(skinName) ??
                PlayerClass.Male))
                throw new InvalidOperationException("Character skin class is invalid.");
        }

        private static bool IsProfileSkinAccepted(string skinName, byte[] skinSha256,
            PlayerClass playerClass)
        {
            if (IsCustomSkinName(skinName))
                return SkinHashCodec.IsValid(skinSha256) &&
                    IsSkinClassCompatible(skinName, playerClass);
            return IsSkinValidForClass(skinName, playerClass);
        }

        private void RegisterPlayerSkinHash(int clientId, NetworkPlayerRecord record)
        {
            if (record == null) return;
            RegisterPlayerSkinHash(clientId, record.SkinName, record.SkinSha256);
        }

        private void RegisterPlayerSkinHash(int clientId, string skinName, byte[] hash)
        {
            if (clientId < 0 || !IsCustomSkinName(skinName) || !SkinHashCodec.IsValid(hash))
            {
                if (clientId >= 0 && !IsCustomSkinName(skinName))
                    m_sessionAssetRegistry.PlayerSkinHashes.Remove(clientId);
                return;
            }
            m_sessionAssetRegistry.PlayerSkinHashes[clientId] = SkinHashCodec.Format(hash);
        }

        private void ApplySessionSkinHash(int clientId, NetworkPlayerRecord record)
        {
            if (record == null || !IsCustomSkinName(record.SkinName)) return;
            if (SkinHashCodec.IsValid(record.SkinSha256))
            {
                RegisterPlayerSkinHash(clientId, record);
                return;
            }
            if (m_sessionAssetRegistry.PlayerSkinHashes.TryGetValue(clientId, out string hashText))
                record.SkinSha256 = SkinHashCodec.Parse(hashText);
        }

        private void RequestSkinAssetIfMissing(int ownerClientId, string skinName,
            PlayerClass playerClass, byte[] hash)
        {
            if (client?.IsConnected != true || !IsCustomSkinName(skinName) ||
                !SkinHashCodec.IsValid(hash) || !IsSkinClassCompatible(skinName, playerClass))
                return;
            string hashText = SkinHashCodec.Format(hash);
            if (m_sessionAssetRegistry.SkinAssets.ContainsKey(hashText)) return;
            if (ownerClientId == client.ClientID)
            {
                SendOwnedSkinAsset(IsHost ? -1 : 0, ownerClientId, skinName,
                    playerClass, hash, force: false);
                return;
            }
            string requestKey = ownerClientId + ":" + hashText;
            if (!m_sessionAssetRegistry.RequestedSkinAssetKeys.Add(requestKey))
                return;
            int targetClientId = IsHost ? ownerClientId : 0;
            if (targetClientId < 0) return;
            NetworkMessageSender.SendPlayerSkinAssetMessage(targetClientId,
                new PlayerSkinAssetMessage(PlayerSkinAssetMessage.SkinAssetAction.Request,
                    ownerClientId, skinName, playerClass, hash));
        }

        private bool SendOwnedSkinAsset(int targetClientId, int ownerClientId,
            string skinName, PlayerClass playerClass, byte[] expectedHash, bool force)
        {
            if (client?.IsConnected != true || !IsCustomSkinName(skinName) ||
                !IsSkinClassCompatible(skinName, playerClass))
                return false;
            if (!TryReadLocalCharacterSkinAsset(skinName, expectedHash,
                out byte[] data, out byte[] hash) || !SkinHashCodec.IsValid(hash))
                return false;
            string hashText = SkinHashCodec.Format(hash);
            if (!force && m_sessionAssetRegistry.SentLocalSkinAssetHashes.Contains(hashText))
                return true;
            if (!StoreSessionSkinAsset(ownerClientId, skinName, playerClass, hash, data))
                return false;
            m_sessionAssetRegistry.SentLocalSkinAssetHashes.Add(hashText);
            NetworkMessageSender.SendPlayerSkinAssetMessage(targetClientId,
                new PlayerSkinAssetMessage(PlayerSkinAssetMessage.SkinAssetAction.Data,
                    ownerClientId, skinName, playerClass, hash, data));
            return true;
        }

        private bool StoreSessionSkinAsset(int ownerClientId, string skinName,
            PlayerClass playerClass, byte[] hash, byte[] data)
        {
            if (!IsCustomSkinName(skinName) || !SkinHashCodec.IsValid(hash) ||
                !IsSkinClassCompatible(skinName, playerClass) ||
                data == null || data.Length == 0 || data.Length > MaximumSkinAssetBytes)
                return false;
            string hashText = SkinHashCodec.Format(hash);
            if (!SHA256.HashData(data).SequenceEqual(hash)) return false;
            try
            {
                ValidateSkinAssetData(data, skinName);
            }
            catch (Exception ex)
            {
                Log.Warning($"[ScMP] Rejected character skin asset \"{skinName}\": {ex.Message}");
                return false;
            }
            if (!m_sessionAssetRegistry.SkinAssets.TryGetValue(hashText, out SkinSessionAsset asset))
            {
                asset = new SkinSessionAsset();
                m_sessionAssetRegistry.SkinAssets[hashText] = asset;
            }
            asset.SkinName = skinName;
            asset.PlayerClass = playerClass;
            asset.Hash = hashText;
            asset.Data = SkinHashCodec.CloneBytes(data);
            RegisterPlayerSkinHash(ownerClientId, skinName, hash);
            m_sessionAssetRegistry.RequestedSkinAssetKeys.Remove(ownerClientId + ":" + hashText);
            return true;
        }

        private void HandlePlayerSkinAssetMessage(PlayerSkinAssetMessage message,
            int sourceClientId)
        {
            if (message == null || !IsCustomSkinName(message.SkinName) ||
                !SkinHashCodec.IsValid(message.Sha256) ||
                !IsSkinClassCompatible(message.SkinName, message.PlayerClass))
                return;
            if (message.Action == PlayerSkinAssetMessage.SkinAssetAction.Request)
            {
                HandlePlayerSkinAssetRequest(message, sourceClientId);
                return;
            }
            if (message.Action != PlayerSkinAssetMessage.SkinAssetAction.Data)
                return;
            if (IsHost && sourceClientId > 0 && message.ClientId != sourceClientId)
                return;
            if (!IsHost && sourceClientId != 0)
                return;
            if (!TryAssemblePlayerSkinAsset(message, out byte[] assetData))
                return;
            if (!StoreSessionSkinAsset(message.ClientId, message.SkinName,
                message.PlayerClass, message.Sha256, assetData))
                return;
            ApplySessionSkinToKnownPlayer(message.ClientId);
            if (IsHost)
            {
                NetworkMessageSender.SendPlayerSkinAssetMessage(-1,
                    new PlayerSkinAssetMessage(PlayerSkinAssetMessage.SkinAssetAction.Data,
                        message.ClientId, message.SkinName, message.PlayerClass,
                        message.Sha256, assetData));
            }
        }

        private bool TryAssemblePlayerSkinAsset(PlayerSkinAssetMessage message,
            out byte[] data)
        {
            data = Array.Empty<byte>();
            if (message == null || message.Data == null ||
                message.ChunkCount <= 0 || message.ChunkIndex < 0 ||
                message.ChunkIndex >= message.ChunkCount ||
                message.TotalLength <= 0 || message.TotalLength > MaximumSkinAssetBytes ||
                message.Data.Length > message.TotalLength)
                return false;
            if (message.ChunkCount == 1)
            {
                if (message.ChunkIndex != 0 || message.Data.Length != message.TotalLength)
                    return false;
                data = message.Data;
                return true;
            }

            string key = message.ClientId + ":" + SkinHashCodec.Format(message.Sha256);
            if (!m_sessionAssetRegistry.IncomingSkinAssetTransfers.TryGetValue(key,
                    out SkinAssetTransfer transfer) ||
                transfer.TransferId != message.TransferId ||
                transfer.TotalLength != message.TotalLength ||
                transfer.Chunks.Length != message.ChunkCount)
            {
                transfer = new SkinAssetTransfer
                {
                    SkinName = message.SkinName,
                    PlayerClass = message.PlayerClass,
                    Hash = SkinHashCodec.Format(message.Sha256),
                    TransferId = message.TransferId,
                    TotalLength = message.TotalLength,
                    Chunks = new byte[message.ChunkCount][]
                };
                m_sessionAssetRegistry.IncomingSkinAssetTransfers[key] = transfer;
            }

            if (transfer.Chunks[message.ChunkIndex] == null)
            {
                transfer.Chunks[message.ChunkIndex] = SkinHashCodec.CloneBytes(message.Data);
                transfer.ReceivedChunks++;
                transfer.ReceivedBytes += message.Data.Length;
            }
            if (transfer.ReceivedChunks != transfer.Chunks.Length ||
                transfer.ReceivedBytes != transfer.TotalLength)
                return false;

            data = new byte[transfer.TotalLength];
            int offset = 0;
            foreach (byte[] chunk in transfer.Chunks)
            {
                if (chunk == null || offset + chunk.Length > data.Length)
                {
                    m_sessionAssetRegistry.IncomingSkinAssetTransfers.Remove(key);
                    data = Array.Empty<byte>();
                    return false;
                }
                Buffer.BlockCopy(chunk, 0, data, offset, chunk.Length);
                offset += chunk.Length;
            }
            m_sessionAssetRegistry.IncomingSkinAssetTransfers.Remove(key);
            return offset == data.Length;
        }

        private void HandlePlayerSkinAssetRequest(PlayerSkinAssetMessage message,
            int sourceClientId)
        {
            if (!IsHost)
            {
                if (sourceClientId == 0 && message.ClientId == client?.ClientID)
                    SendOwnedSkinAsset(0, client.ClientID, message.SkinName,
                        message.PlayerClass, message.Sha256, force: true);
                return;
            }
            if (sourceClientId <= 0) return;
            string hashText = SkinHashCodec.Format(message.Sha256);
            if (m_sessionAssetRegistry.SkinAssets.TryGetValue(hashText, out SkinSessionAsset asset) &&
                asset.Data?.Length > 0)
            {
                NetworkMessageSender.SendPlayerSkinAssetMessage(sourceClientId,
                    new PlayerSkinAssetMessage(PlayerSkinAssetMessage.SkinAssetAction.Data,
                        message.ClientId, asset.SkinName, asset.PlayerClass,
                        message.Sha256, asset.Data));
                return;
            }
            if (message.ClientId == client?.ClientID)
            {
                SendOwnedSkinAsset(sourceClientId, client.ClientID, message.SkinName,
                    message.PlayerClass, message.Sha256, force: true);
                return;
            }
            if (message.ClientId > 0 && m_networkPlayerData.ContainsKey(message.ClientId))
                NetworkMessageSender.SendPlayerSkinAssetMessage(message.ClientId, message);
        }

        private void ApplySessionSkinToKnownPlayer(int ownerClientId)
        {
            if (m_networkPlayerData.TryGetValue(ownerClientId, out PlayerData networkPlayer) &&
                networkPlayer?.ComponentPlayer != null)
            {
                ApplySessionSkinToPlayer(networkPlayer.ComponentPlayer, ownerClientId,
                    new NetworkPlayerRecord
                    {
                        SkinName = networkPlayer.CharacterSkinName,
                        PlayerClass = networkPlayer.PlayerClass,
                        SkinSha256 = m_sessionAssetRegistry.PlayerSkinHashes.TryGetValue(ownerClientId,
                            out string hashText) ? SkinHashCodec.Parse(hashText) : Array.Empty<byte>()
                    });
            }
            if (ownerClientId != client?.ClientID) return;
            ComponentPlayer localPlayer = GameManager.Project?
                .FindSubsystem<SubsystemPlayers>(false)?.ComponentPlayers
                .FirstOrDefault(player => player?.PlayerData != null &&
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer != null)
                ApplySessionSkinToPlayer(localPlayer, ownerClientId,
                    new NetworkPlayerRecord
                    {
                        SkinName = localPlayer.PlayerData.CharacterSkinName,
                        PlayerClass = localPlayer.PlayerData.PlayerClass,
                        SkinSha256 = m_sessionAssetRegistry.PlayerSkinHashes.TryGetValue(ownerClientId,
                            out string hashText) ? SkinHashCodec.Parse(hashText) : Array.Empty<byte>()
                    });
        }

        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.UpdateRenderTargets
        private void ApplySessionSkinToPlayer(ComponentPlayer player, int ownerClientId,
            NetworkPlayerRecord record)
        {
            if (player?.Entity == null || record == null ||
                !IsCustomSkinName(record.SkinName) ||
                !IsSkinClassCompatible(record.SkinName, record.PlayerClass))
                return;
            ApplySessionSkinHash(ownerClientId, record);
            if (!SkinHashCodec.IsValid(record.SkinSha256))
                return;
            string hashText = SkinHashCodec.Format(record.SkinSha256);
            if (!m_sessionAssetRegistry.SkinAssets.TryGetValue(hashText, out SkinSessionAsset asset) ||
                asset.Data == null || asset.Data.Length == 0)
            {
                RequestSkinAssetIfMissing(ownerClientId, record.SkinName,
                    record.PlayerClass, record.SkinSha256);
                return;
            }
            ComponentClothing clothing = player.Entity.FindComponent<ComponentClothing>();
            if (clothing == null) return;
            string textureTag = "ScMP:Skin:" + hashText;
            // Source: SuAPI/ModParentField.cs:ModParentField.GetParentField
            // ComponentClothing initializes these reference fields as null. The generic accessor
            // treats null as a failed type test, so use the raw accessor for nullable fields.
            Texture2D currentTexture = ModManager.ModParentField.GetParentField(
                clothing, "m_skinTexture", typeof(ComponentClothing)) as Texture2D;
            string currentName = ModManager.ModParentField.GetParentField(
                clothing, "m_skinTextureName", typeof(ComponentClothing)) as string;
            if (string.Equals(currentName, record.SkinName, StringComparison.Ordinal) &&
                currentTexture?.Tag is string tag &&
                string.Equals(tag, textureTag, StringComparison.Ordinal))
                return;
            Texture2D texture;
            try
            {
                texture = Texture2D.Load(new MemoryStream(asset.Data));
                texture.Tag = textureTag;
            }
            catch (Exception ex)
            {
                Log.Warning($"[ScMP] Could not create session skin texture \"{record.SkinName}\": {ex.Message}");
                return;
            }
            if (currentTexture != null && !ContentManager.IsContent(currentTexture))
                currentTexture.Dispose();
            DisposeClothingRenderTarget(clothing, "m_innerClothedTexture");
            DisposeClothingRenderTarget(clothing, "m_outerClothedTexture");
            player.PlayerData.CharacterSkinName = record.SkinName;
            ModManager.ModParentField.ModifyParentField(
                clothing, "m_skinTexture", texture, typeof(ComponentClothing));
            ModManager.ModParentField.ModifyParentField(
                clothing, "m_skinTextureName", record.SkinName, typeof(ComponentClothing));
            ModManager.ModParentField.ModifyParentField(
                clothing, "m_clothedTexturesValid", false, typeof(ComponentClothing));
        }

        private static void DisposeClothingRenderTarget(ComponentClothing clothing,
            string fieldName)
        {
            RenderTarget2D renderTarget = ModManager.ModParentField
                .GetParentField(clothing, fieldName, typeof(ComponentClothing))
                as RenderTarget2D;
            if (renderTarget != null) renderTarget.Dispose();
            ModManager.ModParentField.ModifyParentField(
                clothing, fieldName, null, typeof(ComponentClothing));
        }

        private void SynchronizeLocalProfileIfChanged(Project project)
        {
            if (client?.IsConnected != true || project == null)
            {
                m_lastLocalProfileSignature = null;
                return;
            }
            ComponentPlayer localPlayer = project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(player => player?.PlayerData != null &&
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer?.PlayerData == null) return;
            string signature = string.Join("|",
                localPlayer.PlayerData.Name ?? string.Empty,
                localPlayer.PlayerData.PlayerClass,
                localPlayer.PlayerData.CharacterSkinName ?? string.Empty);
            if (string.Equals(signature, m_lastLocalProfileSignature,
                StringComparison.Ordinal))
                return;
            m_lastLocalProfileSignature = signature;
            NetworkPlayerRecord record = CapturePlayerRecord(localPlayer.PlayerData);
            RegisterPlayerSkinHash(client.ClientID, record);
            NetworkMessageSender.SendPlayerProfileMessage(client.ClientID, record);
            if (IsCustomSkinName(record.SkinName) && SkinHashCodec.IsValid(record.SkinSha256))
                SendOwnedSkinAsset(IsHost ? -1 : 0, client.ClientID, record.SkinName,
                    record.PlayerClass, record.SkinSha256, force: false);
        }

        private void ApplyProfileToLocalPlayer(PlayerProfileMessage message)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer == null || localPlayer.PlayerData.PlayerClass != message.PlayerClass) return;
            if (PlayerData.VerifyName((message.Name ?? string.Empty).Trim()))
                localPlayer.PlayerData.Name = message.Name.Trim();
            if (IsProfileSkinAccepted(message.SkinName, message.SkinSha256,
                message.PlayerClass))
            {
                localPlayer.PlayerData.CharacterSkinName = message.SkinName;
                RegisterPlayerSkinHash(message.ClientId, message.SkinName,
                    message.SkinSha256);
                RequestSkinAssetIfMissing(message.ClientId, message.SkinName,
                    message.PlayerClass, message.SkinSha256);
            }
            if (!m_equipmentSynchronizedClients.Contains(message.ClientId))
                ApplyClothes(localPlayer, message.Clothes);
            ApplySessionSkinToPlayer(localPlayer, message.ClientId,
                new NetworkPlayerRecord
                {
                    SkinName = message.SkinName,
                    SkinSha256 = SkinHashCodec.CloneBytes(message.SkinSha256),
                    PlayerClass = message.PlayerClass
                });
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead
        // PlayerData removes only the dead Entity and later attaches a new ComponentPlayer to the
        // same Project. Observe that entity replacement without treating it as a room leave.
        private void ObserveLocalPlayerRespawn(Project project)
        {
            if (client?.IsConnected != true || project == null) return;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer?.Entity == null) return;
            if (ReferenceEquals(m_observedLocalPlayerEntity, localPlayer.Entity))
            {
                if (localPlayer.ComponentHealth?.Health <= 0f)
                    m_observedLocalPlayerWasDead = true;
                return;
            }

            bool respawned = m_observedLocalPlayerEntity != null &&
                m_observedLocalPlayerWasDead && localPlayer.ComponentHealth?.Health > 0f;
            m_observedLocalPlayerEntity = localPlayer.Entity;
            m_observedLocalPlayerWasDead = localPlayer.ComponentHealth?.Health <= 0f;
            if (!respawned) return;

            m_localRespawnSequence = m_localRespawnSequence == int.MaxValue
                ? 1
                : m_localRespawnSequence + 1;
            var message = new PlayerActionMessage(
                PlayerActionType.RespawnRequest, client.ClientID,
                m_localRespawnSequence, default)
            {
                Position = localPlayer.ComponentBody.Position
            };
            if (IsHost) NetworkMessageSender.BroadcastPlayerRespawn(message);
            else NetworkMessageSender.SendPlayerRespawnRequest(message);
            if (!IsHost) m_localRespawnPendingUntil = Time.RealTime + 5.0;
            m_hasObservedClientHealth = false;
        }

        private void EnsureLocalPlayerRecordApplied()
        {
            if (IsHost || m_pendingLocalPlayerRecord == null || GameManager.Project == null) return;
            if (m_localReplacementPlayerData == null)
            {
                if (m_localPlayerRecordQueued) return;
                m_localPlayerRecordQueued = true;
                QueueEndOfFrameAction(ReplaceLocalPlayerData);
                return;
            }
            if (m_localPlayerRecordApplied || m_localReplacementPlayerData.ComponentPlayer == null) return;

            ComponentPlayer player = m_localReplacementPlayerData.ComponentPlayer;
            NetworkPlayerRecord record = m_pendingLocalPlayerRecord;
            player.ComponentBody.Position = record.Position;
            player.ComponentBody.Velocity = Vector3.Zero;
            RestorePlayerRecordInventory(player.ComponentMiner?.Inventory, record);
            RestorePlayerRecordCrafting(player.Entity.FindComponent<ComponentCraftingTable>(),
                record);
            ApplyClothes(player, record.Clothes);
            ApplyAuthoritativePlayerStats(player, record.Health, record.Air, record.Food,
                record.Stamina, record.Sleep, record.Temperature, record.Wetness, record.Level);
            ApplyPlayerRecordState(player, record);
            m_localPlayerRecordApplied = true;
            TryApplyPendingPlayerEquipment(client.ClientID);
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.RemovePlayerData
        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.Update
        private void ReplaceLocalPlayerData()
        {
            m_localPlayerRecordQueued = false;
            Project project = GameManager.Project;
            SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || m_pendingLocalPlayerRecord == null) return;
            PlayerData current = players.PlayersData.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player));
            if (current == null) return;

            int playerIndex = current.PlayerIndex;
            WidgetInputDevice inputDevice = current.InputDevice;
            NetworkPlayerRecord record = m_pendingLocalPlayerRecord;
            PlayerData replacement;
            m_replacingLocalPlayerData = true;
            try
            {
                players.RemovePlayerData(current);
                replacement = new PlayerData(project)
                {
                    Name = record.Name,
                    PlayerClass = record.PlayerClass,
                    CharacterSkinName = record.SkinName,
                    Level = record.Level,
                    InputDevice = inputDevice,
                    // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.GlobalSpawnPosition
                    // Login position is applied after spawning; retain the persisted respawn point.
                    SpawnPosition = record.SpawnPosition != Vector3.Zero
                        ? record.SpawnPosition
                        : (players.GlobalSpawnPosition != Vector3.Zero
                            ? players.GlobalSpawnPosition
                            : record.Position)
                };
                ModManager.ModParentField.ModifyParentField(
                    players, "m_nextPlayerIndex", playerIndex, typeof(SubsystemPlayers));
                players.AddPlayerData(replacement);
                // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
                // Spawn an immediate client-only placeholder without executing InitialNoIntro.
                InvokeClientPlaceholderPlayerSpawn(replacement, record.Position);
                StateMachine stateMachine = ModManager.ModParentField
                    .GetParentField<StateMachine>(replacement, "m_stateMachine",
                        typeof(PlayerData));
                stateMachine.TransitionTo("Playing");
            }
            finally
            {
                m_replacingLocalPlayerData = false;
            }
            m_localReplacementPlayerData = replacement;
            m_localPlayerRecordApplied = false;
            m_frameProject = null;
        }

        // Source: Survivalcraft/Game/ShortInventoryWidget.cs:ShortInventoryWidget.MeasureOverride
        // Source: Survivalcraft/Game/ComponentInventory.cs:ComponentInventory.GetSlotCapacity
        private static void ConfigureNetworkPlayerInventory(IInventory inventory)
        {
            // Network avatars have no ShortInventoryWidget, so normal inventories otherwise retain
            // the template default of 10 and incorrectly treat reserved slots 7-9 as usable.
            if (inventory is ComponentInventory && inventory.VisibleSlotsCount != 7)
                inventory.VisibleSlotsCount = 7;
        }

        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.AddSlotItems
        private static void ApplyInventory(IInventory inventory, int[] values, int[] counts)
        {
            if (inventory == null || values == null || counts == null) return;
            int slotsCount = Math.Min(inventory.SlotsCount, Math.Min(values.Length, counts.Length));
            ComponentCraftingTable craftingTable = inventory as ComponentCraftingTable;
            bool craftingChanged = false;
            for (int i = 0; i < slotsCount; i++)
            {
                if (craftingTable != null)
                {
                    // Source: Survivalcraft/Game/ComponentCraftingTable.cs:
                    // ComponentCraftingTable.RemoveSlotItems
                    // Apply the authoritative grid atomically, then recalculate once. Per-slot
                    // Remove/Add calls expose transient recipes and can display false prompts.
                    if (i == craftingTable.ResultSlotIndex) continue;
                    int craftingValue = NormalizeCrossbowValue(values[i]);
                    int craftingCount = counts[i];
                    if (craftingCount < 0 || craftingCount > 0 && craftingValue == 0)
                        continue;
                    if (inventory.GetSlotValue(i) == craftingValue &&
                        inventory.GetSlotCount(i) == craftingCount)
                        continue;
                    SetCraftingSlotDirect(craftingTable, i, craftingValue, craftingCount);
                    craftingChanged = true;
                    continue;
                }
                int value = NormalizeCrossbowValue(values[i]);
                int count = counts[i];
                if (count < 0 || (count > 0 && value == 0)) continue;
                if (count > 0)
                {
                    int capacity;
                    try
                    {
                        capacity = inventory.GetSlotCapacity(i, value);
                    }
                    catch
                    {
                        continue;
                    }
                    if (count > capacity) continue;
                }
                if (inventory.GetSlotValue(i) == value && inventory.GetSlotCount(i) == count)
                    continue;
                inventory.RemoveSlotItems(i, int.MaxValue);
                if (count > 0) inventory.AddSlotItems(i, value, count);
            }
            if (craftingChanged)
            {
                // Source: Survivalcraft/Game/ComponentCraftingTable.cs:
                // ComponentCraftingTable.UpdateCraftingResult
                ModManager.ModParentMethod.InvokeParentMethod(craftingTable,
                    "UpdateCraftingResult");
            }
        }

        private static void SetCraftingSlotDirect(ComponentCraftingTable craftingTable,
            int slotIndex, int value, int count)
        {
            if (craftingTable == null || slotIndex < 0 ||
                slotIndex >= craftingTable.SlotsCount || count < 0 ||
                count > 0 && value == 0)
                return;
            if (count > 0)
            {
                int contents = Terrain.ExtractContents(value);
                if (contents < 0 || contents >= BlocksManager.Blocks.Length) return;
                count = Math.Min(count, BlocksManager.Blocks[contents].MaxStacking);
            }
            IList slots = ModManager.ModParentField.GetParentField(
                craftingTable, "m_slots", typeof(ComponentInventoryBase)) as IList;
            object slot = slots?[slotIndex];
            if (slot == null) return;
            Type slotType = slot.GetType();
            slotType.GetField("Value", BindingFlags.Instance | BindingFlags.Public)?
                .SetValue(slot, count > 0 ? value : 0);
            slotType.GetField("Count", BindingFlags.Instance | BindingFlags.Public)?
                .SetValue(slot, count);
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Load
        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Load
        private static void ApplyAuthoritativePlayerStats(ComponentPlayer player, float health,
            float air, float food, float stamina, float sleep, float temperature,
            float wetness, float level)
        {
            if (player == null) return;
            if (player.ComponentHealth != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    player.ComponentHealth, "<Health>k__BackingField",
                    MathUtils.Saturate(health), typeof(ComponentHealth));
                ModManager.ModParentField.ModifyParentField(
                    player.ComponentHealth, "<Air>k__BackingField",
                    MathUtils.Saturate(air), typeof(ComponentHealth));
                ModManager.ModParentField.ModifyParentField(
                    player.ComponentHealth, "m_lastHealth",
                    MathUtils.Saturate(health), typeof(ComponentHealth));
            }
            ComponentVitalStats vital = player.ComponentVitalStats;
            if (vital != null)
            {
                float safeFood = MathUtils.Saturate(food);
                float safeStamina = MathUtils.Saturate(stamina);
                float safeSleep = MathUtils.Saturate(sleep);
                float safeTemperature = MathUtils.Clamp(temperature, 0f, 24f);
                float safeWetness = MathUtils.Saturate(wetness);
                ModManager.ModParentField.ModifyParentField(vital, "m_food", safeFood, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_stamina", safeStamina, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_sleep", safeSleep, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_temperature", safeTemperature, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_wetness", safeWetness, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastFood", safeFood, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastStamina", safeStamina, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastSleep", safeSleep, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastTemperature", safeTemperature, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastWetness", safeWetness, typeof(ComponentVitalStats));
            }
            player.PlayerData.Level = MathUtils.Max(level, 1f);
        }

    }
}

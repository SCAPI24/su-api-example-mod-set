using Engine;
using Game;
using SuAPI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        // Source: EntitySystem/SuAPI/IModEventBus.cs:IModEventBus.SubscribeEvent
        // Host-only control surface for HeadlessRenderingMod. The response contains plain
        // dictionaries so the headless Mod does not reference ScMultiplayer assemblies.
        private object[] HandleNetworkPlayersEvent(object[] args)
        {
            IDictionary<string, object> request = args != null && args.Length > 0
                ? args[0] as IDictionary<string, object> : null;
            string operation = request != null && request.TryGetValue("operation", out object op)
                ? op as string : "list";
            if (!IsHost || GameManager.Project == null)
                return new object[] { new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["error"] = "An active host world is required."
                } };
            EnsurePlayerRecordsLoaded();
            if (string.Equals(operation, "list", StringComparison.OrdinalIgnoreCase))
                return new object[] { BuildNetworkPlayerList() };
            if (string.Equals(operation, "update", StringComparison.OrdinalIgnoreCase))
                return new object[] { UpdateNetworkPlayerRecord(request) };
            if (string.Equals(operation, "delete", StringComparison.OrdinalIgnoreCase))
                return new object[] { DeleteNetworkPlayerRecord(request) };
            return new object[] { new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["error"] = "Unsupported network-player operation."
            } };
        }

        private List<Dictionary<string, object>> BuildNetworkPlayerList()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (KeyValuePair<string, NetworkPlayerRecord> item in m_playerRecords.OrderBy(pair => pair.Key))
            {
                NetworkPlayerRecord record = item.Value;
                if (record == null || !item.Key.StartsWith("network:", StringComparison.Ordinal))
                    continue;
                int clientId = ParseNetworkClientId(item.Key);
                PlayerData playerData = m_networkPlayerData.TryGetValue(clientId, out PlayerData live)
                    ? live : null;
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["networkKey"] = item.Key,
                    ["playerIndex"] = playerData?.PlayerIndex ?? -1,
                    ["clientId"] = clientId,
                    ["name"] = record.Name ?? "Player",
                    ["playerClass"] = record.PlayerClass.ToString(),
                    ["skin"] = record.SkinName ?? string.Empty,
                    ["skinDisplayName"] = CharacterSkinsManager.GetDisplayName(record.SkinName ?? string.Empty),
                    ["level"] = record.Level,
                    ["health"] = record.Health,
                    ["air"] = record.Air,
                    ["food"] = record.Food,
                    ["stamina"] = record.Stamina,
                    ["sleep"] = record.Sleep,
                    ["temperature"] = record.Temperature,
                    ["wetness"] = record.Wetness,
                    ["position"] = FormatVector(record.Position),
                    ["spawnPosition"] = FormatVector(record.SpawnPosition),
                    ["inventorySlots"] = Math.Min(record.SlotValues?.Length ?? 0,
                        record.SlotCounts?.Length ?? 0),
                    ["handcraftSlots"] = Math.Min(record.HandcraftSlotValues?.Length ?? 0,
                        record.HandcraftSlotCounts?.Length ?? 0),
                    ["online"] = playerData?.ComponentPlayer != null
                });
            }
            return result;
        }

        private Dictionary<string, object> UpdateNetworkPlayerRecord(
            IDictionary<string, object> request)
        {
            string key = ReadRequiredString(request, "networkKey");
            if (!m_playerRecords.TryGetValue(key, out NetworkPlayerRecord record) ||
                !key.StartsWith("network:", StringComparison.Ordinal))
                throw new InvalidOperationException("Network player record was not found.");
            int clientId = ParseNetworkClientId(key);
            PlayerData playerData = m_networkPlayerData.TryGetValue(clientId, out PlayerData live)
                ? live : null;
            ApplyNetworkScalarChanges(request, record);
            ApplyNetworkInventoryChange(request, record);
            ApplyNetworkHandcraftChange(request, record);
            ApplyNetworkClothingChange(request, record);
            bool positionChanged = HasAny(request, "x", "y", "z");
            if (playerData?.ComponentPlayer != null)
            {
                ApplyRecordToLiveNetworkPlayer(playerData, record);
                if (clientId > 0)
                    SendPlayerAuthorityState(clientId,
                        positionChanged ? PlayerAuthorityAction.Teleport :
                            PlayerAuthorityAction.RespawnAnchor,
                        playerData, playerData.ComponentPlayer.ComponentBody);
            }
            m_playerRecords[key] = record;
            m_playerRecordsDirty = true;
            SavePlayerRecords();
            Dictionary<string, object> result = BuildNetworkPlayerList()
                .FirstOrDefault(item => string.Equals(item["networkKey"]?.ToString(), key,
                    StringComparison.Ordinal));
            return result ?? new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["updated"] = true,
                ["networkKey"] = key
            };
        }

        private Dictionary<string, object> DeleteNetworkPlayerRecord(
            IDictionary<string, object> request)
        {
            string key = ReadRequiredString(request, "networkKey");
            if (!m_playerRecords.ContainsKey(key) || !key.StartsWith("network:", StringComparison.Ordinal))
                throw new InvalidOperationException("Network player record was not found.");
            int clientId = ParseNetworkClientId(key);
            if (clientId == 0)
                throw new InvalidOperationException("The host network player cannot be deleted.");
            if (m_networkPlayerData.ContainsKey(clientId))
            {
                NetworkMessageSender.SendKickPlayerMessage(clientId,
                    "The network player was removed by the host.");
                RemoveNetworkPlayer(clientId);
            }
            m_playerRecords.Remove(key);
            m_playerRecordsDirty = true;
            SavePlayerRecords();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["deleted"] = true,
                ["networkKey"] = key,
                ["clientId"] = clientId
            };
        }

        private void ApplyNetworkScalarChanges(IDictionary<string, object> request,
            NetworkPlayerRecord record)
        {
            if (TryString(request, "name", out string name))
            {
                if (!PlayerData.VerifyName(name))
                    throw new InvalidOperationException("Invalid network player name.");
                record.Name = name.Trim();
            }
            if (TryString(request, "skin", out string skin))
            {
                if (!IsProfileSkinAccepted(skin, record.SkinSha256, record.PlayerClass))
                    throw new InvalidOperationException("The skin is invalid for this player class.");
                record.SkinName = skin;
            }
            SetFloat(request, "level", value => record.Level = MathUtils.Max(value, 1f));
            SetFloat(request, "health", value => record.Health = MathUtils.Saturate(value));
            SetFloat(request, "air", value => record.Air = MathUtils.Saturate(value));
            SetFloat(request, "food", value => record.Food = MathUtils.Saturate(value));
            SetFloat(request, "stamina", value => record.Stamina = MathUtils.Saturate(value));
            SetFloat(request, "sleep", value => record.Sleep = MathUtils.Saturate(value));
            SetFloat(request, "temperature", value => record.Temperature = MathUtils.Clamp(value, 0f, 24f));
            SetFloat(request, "wetness", value => record.Wetness = MathUtils.Saturate(value));
            SetFloat(request, "fluDuration", value => record.FluDuration = MathUtils.Max(value, 0f));
            SetFloat(request, "fluOnset", value => record.FluOnset = MathUtils.Max(value, 0f));
            SetFloat(request, "sicknessDuration", value => record.SicknessDuration = MathUtils.Max(value, 0f));
            SetFloat(request, "fireDuration", value => record.FireDuration = MathUtils.Max(value, 0f));
            if (TryFloat(request, "x", out float x) && TryFloat(request, "y", out float y) &&
                TryFloat(request, "z", out float z))
                record.Position = new Vector3(x, y, z);
            if (TryFloat(request, "spawnX", out float sx) && TryFloat(request, "spawnY", out float sy) &&
                TryFloat(request, "spawnZ", out float sz))
                record.SpawnPosition = new Vector3(sx, sy, sz);
        }

        private static void ApplyNetworkInventoryChange(IDictionary<string, object> request,
            NetworkPlayerRecord record)
        {
            if (!TryInt(request, "inventorySlot", out int slot) ||
                !TryInt(request, "inventoryValue", out int value) ||
                !TryInt(request, "inventoryCount", out int count)) return;
            EnsureSlot(ref record.SlotValues, ref record.SlotCounts, slot + 1);
            record.SlotValues[slot] = Math.Max(value, 0);
            record.SlotCounts[slot] = Math.Max(count, 0);
        }

        private static void ApplyNetworkHandcraftChange(IDictionary<string, object> request,
            NetworkPlayerRecord record)
        {
            if (!TryInt(request, "handcraftSlot", out int slot) ||
                !TryInt(request, "handcraftValue", out int value) ||
                !TryInt(request, "handcraftCount", out int count)) return;
            EnsureSlot(ref record.HandcraftSlotValues,
                ref record.HandcraftSlotCounts, slot + 1);
            record.HandcraftSlotValues[slot] = Math.Max(value, 0);
            record.HandcraftSlotCounts[slot] = Math.Max(count, 0);
        }

        private static void ApplyNetworkClothingChange(IDictionary<string, object> request,
            NetworkPlayerRecord record)
        {
            if (!TryInt(request, "clothingSlot", out int slot) ||
                !TryString(request, "clothingValues", out string values) || slot < 0 || slot >= 4)
                return;
            record.Clothes ??= PlayerProfileValueCodec.CreateEmptyClothes();
            record.Clothes[slot] = PlayerProfileValueCodec.ParseIntArray(values);
        }

        private void ApplyRecordToLiveNetworkPlayer(PlayerData playerData,
            NetworkPlayerRecord record)
        {
            ComponentPlayer player = playerData.ComponentPlayer;
            playerData.Name = record.Name;
            playerData.CharacterSkinName = record.SkinName;
            playerData.SpawnPosition = record.SpawnPosition;
            RestorePlayerRecordInventory(player.ComponentMiner?.Inventory, record);
            RestorePlayerRecordCrafting(player.Entity.FindComponent<ComponentCraftingTable>(), record);
            ApplyClothes(player, record.Clothes);
            ApplyAuthoritativePlayerStats(player, record.Health, record.Air, record.Food,
                record.Stamina, record.Sleep, record.Temperature, record.Wetness, record.Level);
            ApplyPlayerRecordState(player, record);
            if (player.ComponentBody != null)
            {
                player.ComponentBody.Position = record.Position;
                player.ComponentBody.Velocity = Vector3.Zero;
            }
        }

        private static string ReadRequiredString(IDictionary<string, object> request, string name)
        {
            if (request == null || !request.TryGetValue(name, out object value) ||
                string.IsNullOrWhiteSpace(value?.ToString()))
                throw new InvalidOperationException("Missing " + name + ".");
            return value.ToString();
        }

        private static bool TryString(IDictionary<string, object> request, string name, out string value)
        {
            value = request != null && request.TryGetValue(name, out object raw)
                ? raw?.ToString() : null;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryFloat(IDictionary<string, object> request, string name, out float value)
        {
            value = 0f;
            return request != null && request.TryGetValue(name, out object raw) &&
                float.TryParse(raw?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value);
        }

        private static bool TryInt(IDictionary<string, object> request, string name, out int value)
        {
            value = 0;
            return request != null && request.TryGetValue(name, out object raw) &&
                int.TryParse(raw?.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value);
        }

        private static void SetFloat(IDictionary<string, object> request, string name,
            Action<float> setter)
        {
            if (TryFloat(request, name, out float value)) setter(value);
        }

        private static bool HasAny(IDictionary<string, object> request, params string[] names)
        {
            if (request == null || names == null) return false;
            foreach (string name in names)
            {
                if (request.ContainsKey(name)) return true;
            }
            return false;
        }

        private static void EnsureSlot(ref int[] values, ref int[] counts, int length)
        {
            length = Math.Max(length, 0);
            if (values == null || values.Length < length) Array.Resize(ref values, length);
            if (counts == null || counts.Length < length) Array.Resize(ref counts, length);
        }

        private static int ParseNetworkClientId(string key)
        {
            return int.TryParse(key.Substring("network:".Length),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;
        }

        private static string FormatVector(Vector3 value) =>
            string.Join(",", value.X.ToString(CultureInfo.InvariantCulture),
                value.Y.ToString(CultureInfo.InvariantCulture),
                value.Z.ToString(CultureInfo.InvariantCulture));
    }
}
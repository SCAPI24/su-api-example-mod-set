using Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace ScMultiplayer
{
    internal sealed class PersonalServerRecord
    {
        public string Id { get; }

        public string Address { get; }

        public string Name { get; }

        public PersonalServerRecord(string address, string name)
        {
            Id = address;
            Address = address;
            Name = name;
        }
    }

    internal static class PersonalServerDirectory
    {
        private const string DirectoryPath = "data:/ScMultiplayerPersonalServers.json";
        private const string TemporaryDirectoryPath =
            "data:/ScMultiplayerPersonalServers.json.tmp";
        private const int MaximumRecords = 64;
        private const int MaximumNameLength = 50;
        private static readonly object s_sync = new object();
        private static readonly List<PersonalServerRecord> s_records =
            new List<PersonalServerRecord>();
        private static bool s_loaded;
        private static int s_revision;

        public static event Action Changed;

        public static int Revision => Volatile.Read(ref s_revision);

        public static PersonalServerRecord[] Records
        {
            get
            {
                EnsureLoaded();
                lock (s_sync)
                    return s_records.ToArray();
            }
        }

        // Source: Survivalcraft/Game/SettingsManager.cs:SettingsManager.LoadSettings
        public static void Load()
        {
            lock (s_sync)
            {
                if (s_loaded) return;
                s_loaded = true;
                s_records.Clear();
                if (!Storage.FileExists(DirectoryPath))
                {
                    Interlocked.Increment(ref s_revision);
                    return;
                }

                try
                {
                    using Stream stream = Storage.OpenFile(DirectoryPath, OpenFileMode.Read);
                    using JsonDocument document = JsonDocument.Parse(stream);
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Personal server directory is not an array.");
                    foreach (JsonElement item in document.RootElement.EnumerateArray())
                    {
                        if (s_records.Count >= MaximumRecords ||
                            !item.TryGetProperty("address", out JsonElement addressElement) ||
                            !item.TryGetProperty("name", out JsonElement nameElement) ||
                            addressElement.ValueKind != JsonValueKind.String ||
                            nameElement.ValueKind != JsonValueKind.String)
                            continue;

                        string address = addressElement.GetString();
                        string name = nameElement.GetString()?.Trim();
                        if (!TryNormalizeAddress(address, out string normalizedAddress,
                                out _) ||
                            !IsValidName(name) ||
                            s_records.Any(record => string.Equals(record.Address,
                                normalizedAddress, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        s_records.Add(new PersonalServerRecord(normalizedAddress, name));
                    }
                }
                catch (Exception error)
                {
                    s_records.Clear();
                    Log.Warning("[ScMP] Unable to load personal Net World directory: " +
                        error.Message);
                }
                Interlocked.Increment(ref s_revision);
            }
        }

        // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:RemoteServerDirectory.ParseHosts
        public static bool TryNormalizeAddress(string address, out string normalizedAddress,
            out string error)
        {
            normalizedAddress = string.Empty;
            error = null;
            string value = address?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                error = "Enter a DNS name or server address.";
                return false;
            }
            if (!RemoteServerDirectory.TryParseDirectoryEntry(value, out string host,
                    out int port))
            {
                error = "Use a DNS name, host:port, IP address, or [IPv6]:port.";
                return false;
            }
            normalizedAddress = RemoteServerDirectory.FormatDirectoryEntry(host, port);
            return true;
        }

        // Source: Survivalcraft/Game/DownloadContentFromLinkDialog.cs:DownloadContentFromLinkDialog.Update
        public static bool TryAddOrUpdate(string address, string name,
            out PersonalServerRecord record, out string error)
        {
            record = null;
            error = null;
            EnsureLoaded();
            if (!TryNormalizeAddress(address, out string normalizedAddress, out error))
                return false;
            string normalizedName = name?.Trim();
            if (!IsValidName(normalizedName))
            {
                error = $"Name must contain 1-{MaximumNameLength} printable characters.";
                return false;
            }

            bool changed = false;
            lock (s_sync)
            {
                int index = s_records.FindIndex(item => string.Equals(item.Address,
                    normalizedAddress, StringComparison.OrdinalIgnoreCase));
                PersonalServerRecord previous = index >= 0 ? s_records[index] : null;
                if (previous != null && string.Equals(previous.Name, normalizedName,
                    StringComparison.Ordinal))
                {
                    record = previous;
                    return true;
                }
                if (previous == null && s_records.Count >= MaximumRecords)
                {
                    error = $"A maximum of {MaximumRecords} personal Net Worlds is allowed.";
                    return false;
                }

                record = new PersonalServerRecord(normalizedAddress, normalizedName);
                if (index >= 0) s_records[index] = record;
                else s_records.Add(record);
                try
                {
                    SaveLocked();
                    changed = true;
                }
                catch (Exception saveError)
                {
                    if (index >= 0) s_records[index] = previous;
                    else s_records.Remove(record);
                    record = null;
                    error = "Unable to save the personal Net World: " + saveError.Message;
                }
            }
            if (changed)
                NotifyChanged();
            return changed;
        }

        // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.Update
        public static bool Remove(string id, out string error)
        {
            error = null;
            EnsureLoaded();
            PersonalServerRecord removed = null;
            int removedIndex = -1;
            lock (s_sync)
            {
                removedIndex = s_records.FindIndex(record => string.Equals(record.Id, id,
                    StringComparison.OrdinalIgnoreCase));
                if (removedIndex < 0) return false;
                removed = s_records[removedIndex];
                s_records.RemoveAt(removedIndex);
                try
                {
                    SaveLocked();
                }
                catch (Exception saveError)
                {
                    s_records.Insert(removedIndex, removed);
                    error = "Unable to remove the personal Net World: " + saveError.Message;
                    return false;
                }
            }
            NotifyChanged();
            return true;
        }

        // Source: Survivalcraft/Game/ModifyWorldScreen.cs:ModifyWorldScreen.Enter
        public static PersonalServerRecord Find(string id)
        {
            EnsureLoaded();
            lock (s_sync)
                return s_records.FirstOrDefault(record => string.Equals(record.Id, id,
                    StringComparison.OrdinalIgnoreCase));
        }

        // Source: Mod/ScMultiplayer/Func/Server/ScMultiplayerSettings.cs:ScMultiplayerSettings.Load
        private static void EnsureLoaded()
        {
            if (!s_loaded) Load();
        }

        // Source: Pak/Dialogs/DownloadContentFromLinkDialog.xml:DownloadContentFromLinkDialog.Name
        private static bool IsValidName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Length <= MaximumNameLength &&
                !name.Any(char.IsControl);
        }

        // Source: Survivalcraft/Game/SettingsManager.cs:SettingsManager.SaveSettings
        private static void SaveLocked()
        {
            using (Stream stream = Storage.OpenFile(TemporaryDirectoryPath,
                OpenFileMode.Create))
            using (var writer = new Utf8JsonWriter(stream,
                new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray();
                foreach (PersonalServerRecord record in s_records)
                {
                    writer.WriteStartObject();
                    writer.WriteString("address", record.Address);
                    writer.WriteString("name", record.Name);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.Flush();
                stream.Flush();
            }
            Storage.MoveFile(TemporaryDirectoryPath, DirectoryPath);
        }

        // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:RemoteServerDirectory.ApplyHosts
        private static void NotifyChanged()
        {
            Interlocked.Increment(ref s_revision);
            try
            {
                Changed?.Invoke();
            }
            catch (Exception error)
            {
                Log.Warning("[ScMP] Unable to refresh personal Net World discovery: " +
                    error.Message);
            }
        }
    }
}

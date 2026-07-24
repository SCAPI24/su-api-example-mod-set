using Engine;
using Game;
using GameEntitySystem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
    // SubsystemFurnitureBlockBehavior.Save
    // Source: Survivalcraft/Game/SubsystemSignBlockBehavior.cs:
    // SubsystemSignBlockBehavior.Save
    // Source: Survivalcraft/Game/ComponentFurnace.cs:ComponentFurnace.Update
    // Source: Survivalcraft/Game/SubsystemMovingBlocks.cs:SubsystemMovingBlocks.Save
    internal sealed class WorldObjectSynchronizer
    {
        private const int SnapshotChunkBytes = 24576;
        private const int MaximumSnapshotBytes = 16 * 1024 * 1024;
        private const double StaticScanInterval = 0.5;
        private const double FurnaceInterval = 0.25;
        private const double PistonInterval = 0.125;
        private const double IncomingRetention = 30.0;

        private sealed class IncomingSnapshot
        {
            public WorldObjectSnapshotKind Kind;
            public bool IsRequest;
            public int SourceClientId;
            public int Revision;
            public int TotalLength;
            public byte[][] Chunks;
            public int ReceivedCount;
            public double LastUpdateTime;
        }

        private sealed class FurnitureSnapshot
        {
            public readonly List<FurnitureSet> Sets = new List<FurnitureSet>();
            public readonly List<FurnitureDesignRecord> Designs =
                new List<FurnitureDesignRecord>();
        }

        private sealed class FurnitureDesignRecord
        {
            public int Index;
            public int Resolution;
            public string Name;
            public FurnitureInteractionMode InteractionMode;
            public int LinkedIndex;
            public int SetIndex;
            public int[] Values;
        }

        private sealed class SignRecord
        {
            public Point3 Point;
            public string[] Lines;
            public Color[] Colors;
            public string Url;
        }

        private readonly ScMultiplayer m_owner;
        private readonly Dictionary<string, IncomingSnapshot> m_incoming =
            new Dictionary<string, IncomingSnapshot>();
        private readonly Dictionary<WorldObjectSnapshotKind, byte[]> m_lastHashes =
            new Dictionary<WorldObjectSnapshotKind, byte[]>();
        private readonly Dictionary<WorldObjectSnapshotKind, byte[]> m_lastSnapshots =
            new Dictionary<WorldObjectSnapshotKind, byte[]>();
        private readonly Dictionary<WorldObjectSnapshotKind, byte[]> m_pendingHashes =
            new Dictionary<WorldObjectSnapshotKind, byte[]>();
        private readonly Dictionary<WorldObjectSnapshotKind, int> m_lastAppliedRevisions =
            new Dictionary<WorldObjectSnapshotKind, int>();
        private readonly Dictionary<int, Dictionary<int, int>> m_clientFurnitureMappings =
            new Dictionary<int, Dictionary<int, int>>();
        private readonly Dictionary<Point3, FurnaceStateRecord> m_lastFurnaceStates =
            new Dictionary<Point3, FurnaceStateRecord>();

        private Project m_project;
        private FurnitureSnapshot m_pendingFurnitureSnapshot;
        private double m_nextStaticScanTime;
        private double m_nextFurnaceTime;
        private double m_nextPistonTime;
        private int m_nextSnapshotId;
        private int m_furnitureRevision;
        private int m_signRevision;
        private int m_pistonMotionSequence;
        private int m_lastPistonMotionSequence;

        public WorldObjectSynchronizer(ScMultiplayer owner)
        {
            m_owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public void Update(Project project)
        {
            if (!ReferenceEquals(m_project, project)) Bind(project);
            if (m_project == null || ScMultiplayer.client?.IsConnected != true) return;
            double now = Time.RealTime;
            if (now >= m_nextStaticScanTime)
            {
                m_nextStaticScanTime = now + StaticScanInterval;
                DetectStaticChanges(WorldObjectSnapshotKind.Furniture);
                DetectStaticChanges(WorldObjectSnapshotKind.Signs);
                TrimIncoming(now);
            }
            if (ScMultiplayer.IsHost && now >= m_nextFurnaceTime)
            {
                m_nextFurnaceTime = now + FurnaceInterval;
                PublishFurnaces();
            }
            if (ScMultiplayer.IsHost && now >= m_nextPistonTime)
            {
                m_nextPistonTime = now + PistonInterval;
                PublishPistons();
            }
        }

        public void HandleMessage(WorldObjectSyncMessage message, int sourceClientId)
        {
            if (message == null || m_project == null) return;
            if (message.Stage == WorldObjectSyncStage.SnapshotRequest)
            {
                if (!ScMultiplayer.IsHost || sourceClientId <= 0) return;
                byte[] snapshot = CaptureSnapshot(message.SnapshotKind);
                if (snapshot != null)
                    SendSnapshot(message.SnapshotKind, snapshot, isRequest: false,
                        targetClientId: sourceClientId, NextRevision(message.SnapshotKind));
                return;
            }
            if (message.Stage == WorldObjectSyncStage.SnapshotChunk)
            {
                if (ScMultiplayer.IsHost)
                {
                    if (sourceClientId <= 0 || !message.IsRequest) return;
                }
                else if (sourceClientId != 0 || message.IsRequest)
                    return;
                AcceptSnapshotChunk(message, sourceClientId);
                return;
            }
            if (ScMultiplayer.IsHost || sourceClientId != 0) return;
            if (message.Stage == WorldObjectSyncStage.FurnaceBatch)
                ApplyFurnaces(message.Furnaces);
            else if (message.Stage == WorldObjectSyncStage.PistonBatch)
                ApplyPistons(message.Pistons, message.MotionSequence,
                    message.IsComplete);
        }

        // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
        // SubsystemFurnitureBlockBehavior.TryAddDesign
        public void PublishLocalFurnitureChangesNow()
        {
            if (m_project != null && ScMultiplayer.client?.IsConnected == true &&
                !ScMultiplayer.IsHost)
                DetectStaticChanges(WorldObjectSnapshotKind.Furniture);
        }

        // Source: Survivalcraft/Game/FurnitureBlock.cs:FurnitureBlock.SetDesignIndex
        public int RemapFurnitureValue(int sourceClientId, int value)
        {
            if (!ScMultiplayer.IsHost || Terrain.ExtractContents(value) < 0 ||
                !(BlocksManager.Blocks[Terrain.ExtractContents(value)] is FurnitureBlock) ||
                !m_clientFurnitureMappings.TryGetValue(sourceClientId,
                    out Dictionary<int, int> mapping))
                return value;
            int data = Terrain.ExtractData(value);
            int oldIndex = FurnitureBlock.GetDesignIndex(data);
            if (!mapping.TryGetValue(oldIndex, out int newIndex) || oldIndex == newIndex)
                return value;
            SubsystemFurnitureBlockBehavior behavior = m_project?
                .FindSubsystem<SubsystemFurnitureBlockBehavior>(false);
            FurnitureDesign design = behavior?.GetDesign(newIndex);
            if (design == null) return value;
            int newData = FurnitureBlock.SetDesignIndex(data, newIndex,
                design.ShadowStrengthFactor, design.IsLightEmitter);
            return Terrain.ReplaceData(value, newData);
        }

        public void Reset()
        {
            Bind(null);
        }

        public void ForgetClient(int clientId)
        {
            m_clientFurnitureMappings.Remove(clientId);
        }

        private void Bind(Project project)
        {
            m_project = project;
            m_incoming.Clear();
            m_lastHashes.Clear();
            m_lastSnapshots.Clear();
            m_pendingHashes.Clear();
            m_lastAppliedRevisions.Clear();
            m_clientFurnitureMappings.Clear();
            m_lastFurnaceStates.Clear();
            m_pendingFurnitureSnapshot = null;
            m_nextStaticScanTime = Time.RealTime + StaticScanInterval;
            m_nextFurnaceTime = Time.RealTime;
            m_nextPistonTime = Time.RealTime;
            m_pistonMotionSequence = 0;
            m_lastPistonMotionSequence = 0;
            if (project == null) return;
            foreach (WorldObjectSnapshotKind kind in Enum.GetValues<WorldObjectSnapshotKind>())
            {
                byte[] data = CaptureSnapshot(kind);
                if (data != null)
                {
                    m_lastHashes[kind] = SHA256.HashData(data);
                    m_lastSnapshots[kind] = data;
                }
            }
            if (!ScMultiplayer.IsHost && ScMultiplayer.client?.IsConnected == true)
            {
                RequestSnapshot(WorldObjectSnapshotKind.Furniture);
                RequestSnapshot(WorldObjectSnapshotKind.Signs);
            }
        }

        private static void RequestSnapshot(WorldObjectSnapshotKind kind)
        {
            NetworkMessageSender.SendWorldObjectSync(0, new WorldObjectSyncMessage
            {
                Stage = WorldObjectSyncStage.SnapshotRequest,
                SnapshotKind = kind
            });
        }

        private void DetectStaticChanges(WorldObjectSnapshotKind kind)
        {
            byte[] data = CaptureSnapshot(kind);
            if (data == null) return;
            byte[] hash = SHA256.HashData(data);
            if (m_lastHashes.TryGetValue(kind, out byte[] previous) &&
                previous.SequenceEqual(hash))
                return;
            if (!ScMultiplayer.IsHost && m_pendingHashes.TryGetValue(kind,
                out byte[] pending) && pending.SequenceEqual(hash))
                return;
            if (ScMultiplayer.IsHost)
            {
                m_lastHashes[kind] = hash;
                m_lastSnapshots[kind] = data;
                SendSnapshot(kind, data, isRequest: false, targetClientId: -1,
                    NextRevision(kind));
            }
            else
            {
                m_pendingHashes[kind] = hash;
                if (kind == WorldObjectSnapshotKind.Furniture)
                    m_pendingFurnitureSnapshot = ReadFurnitureSnapshot(data);
                byte[] requestData = kind == WorldObjectSnapshotKind.Signs &&
                    m_lastSnapshots.TryGetValue(kind, out byte[] previousData)
                    ? CreateSignDelta(previousData, data)
                    : data;
                SendSnapshot(kind, requestData, isRequest: true, targetClientId: 0,
                    revision: 0);
            }
        }

        private int NextRevision(WorldObjectSnapshotKind kind)
        {
            if (kind == WorldObjectSnapshotKind.Furniture)
                return m_furnitureRevision = m_furnitureRevision == int.MaxValue
                    ? 1 : m_furnitureRevision + 1;
            return m_signRevision = m_signRevision == int.MaxValue
                ? 1 : m_signRevision + 1;
        }

        private void SendSnapshot(WorldObjectSnapshotKind kind, byte[] data,
            bool isRequest, int targetClientId, int revision)
        {
            if (data == null || data.Length > MaximumSnapshotBytes) return;
            m_nextSnapshotId = m_nextSnapshotId == int.MaxValue
                ? 1 : m_nextSnapshotId + 1;
            int snapshotId = m_nextSnapshotId;
            int chunkCount = Math.Max((data.Length + SnapshotChunkBytes - 1) /
                SnapshotChunkBytes, 1);
            for (int index = 0; index < chunkCount; index++)
            {
                int offset = index * SnapshotChunkBytes;
                int count = Math.Min(SnapshotChunkBytes, data.Length - offset);
                var chunk = new byte[Math.Max(count, 0)];
                if (count > 0) Array.Copy(data, offset, chunk, 0, count);
                NetworkMessageSender.SendWorldObjectSync(targetClientId,
                    new WorldObjectSyncMessage
                    {
                        Stage = WorldObjectSyncStage.SnapshotChunk,
                        SnapshotKind = kind,
                        IsRequest = isRequest,
                        SnapshotId = snapshotId,
                        Revision = revision,
                        ChunkIndex = index,
                        ChunkCount = chunkCount,
                        TotalLength = data.Length,
                        Chunk = chunk
                    });
            }
        }

        private void AcceptSnapshotChunk(WorldObjectSyncMessage message, int sourceClientId)
        {
            string key = sourceClientId + ":" + (int)message.SnapshotKind + ":" +
                message.SnapshotId + ":" + (message.IsRequest ? "R" : "S");
            if (!m_incoming.TryGetValue(key, out IncomingSnapshot incoming))
            {
                incoming = new IncomingSnapshot
                {
                    Kind = message.SnapshotKind,
                    IsRequest = message.IsRequest,
                    SourceClientId = sourceClientId,
                    Revision = message.Revision,
                    TotalLength = message.TotalLength,
                    Chunks = new byte[message.ChunkCount][],
                    LastUpdateTime = Time.RealTime
                };
                m_incoming.Add(key, incoming);
            }
            if (incoming.Chunks.Length != message.ChunkCount ||
                incoming.TotalLength != message.TotalLength ||
                incoming.Revision != message.Revision) return;
            incoming.LastUpdateTime = Time.RealTime;
            if (incoming.Chunks[message.ChunkIndex] == null)
            {
                incoming.Chunks[message.ChunkIndex] = message.Chunk;
                incoming.ReceivedCount++;
            }
            if (incoming.ReceivedCount != incoming.Chunks.Length) return;
            m_incoming.Remove(key);
            int length = incoming.Chunks.Sum(item => item?.Length ?? 0);
            if (length != incoming.TotalLength || length > MaximumSnapshotBytes) return;
            var data = new byte[length];
            int offset = 0;
            foreach (byte[] chunk in incoming.Chunks)
            {
                Array.Copy(chunk, 0, data, offset, chunk.Length);
                offset += chunk.Length;
            }
            ApplySnapshot(incoming, data);
        }

        private void ApplySnapshot(IncomingSnapshot incoming, byte[] data)
        {
            try
            {
                if (!incoming.IsRequest &&
                    m_lastAppliedRevisions.TryGetValue(incoming.Kind, out int applied) &&
                    incoming.Revision <= applied)
                    return;
                if (incoming.Kind == WorldObjectSnapshotKind.Furniture)
                {
                    FurnitureSnapshot snapshot = ReadFurnitureSnapshot(data);
                    ApplyFurnitureSnapshot(snapshot, incoming.IsRequest,
                        incoming.SourceClientId);
                }
                else if (incoming.Kind == WorldObjectSnapshotKind.Signs)
                {
                    List<SignRecord> signs = ReadSignSnapshot(data);
                    ApplySignSnapshot(signs, incoming.IsRequest
                        ? incoming.SourceClientId : -1);
                }
                byte[] authoritative = CaptureSnapshot(incoming.Kind);
                if (authoritative == null) return;
                byte[] hash = SHA256.HashData(authoritative);
                m_lastHashes[incoming.Kind] = hash;
                m_lastSnapshots[incoming.Kind] = authoritative;
                m_pendingHashes.Remove(incoming.Kind);
                if (!incoming.IsRequest)
                    m_lastAppliedRevisions[incoming.Kind] = incoming.Revision;
                if (ScMultiplayer.IsHost)
                {
                    SendSnapshot(incoming.Kind, authoritative, isRequest: false,
                        targetClientId: -1, NextRevision(incoming.Kind));
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[SuAPI] [ScMP] World-object snapshot rejected: {ex.Message}");
            }
        }

        private byte[] CaptureSnapshot(WorldObjectSnapshotKind kind)
        {
            try
            {
                return kind == WorldObjectSnapshotKind.Furniture
                    ? WriteFurnitureSnapshot()
                    : WriteSignSnapshot();
            }
            catch (Exception ex)
            {
                Log.Warning($"[SuAPI] [ScMP] Could not capture {kind} snapshot: {ex.Message}");
                return null;
            }
        }

        private byte[] WriteFurnitureSnapshot()
        {
            SubsystemFurnitureBlockBehavior behavior = m_project?
                .FindSubsystem<SubsystemFurnitureBlockBehavior>(false);
            if (behavior == null) return null;
            using var raw = new MemoryStream();
            using (var writer = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(1);
                List<FurnitureSet> sets = behavior.FurnitureSets.ToList();
                writer.Write(sets.Count);
                foreach (FurnitureSet set in sets)
                {
                    writer.Write(set.Name ?? string.Empty);
                    writer.Write(set.ImportedFrom ?? string.Empty);
                }
                var designs = new List<FurnitureDesign>();
                for (int i = 0; i < SubsystemFurnitureBlockBehavior.MaxDesigns; i++)
                {
                    FurnitureDesign design = behavior.GetDesign(i);
                    if (design != null) designs.Add(design);
                }
                writer.Write(designs.Count);
                foreach (FurnitureDesign design in designs)
                {
                    writer.Write(design.Index);
                    writer.Write(design.Resolution);
                    writer.Write(design.Name ?? string.Empty);
                    writer.Write((byte)design.InteractionMode);
                    writer.Write(design.LinkedDesign?.Index ?? -1);
                    writer.Write(design.FurnitureSet != null
                        ? sets.IndexOf(design.FurnitureSet) : -1);
                    int count = design.Resolution * design.Resolution * design.Resolution;
                    writer.Write(count);
                    int index = 0;
                    while (index < count)
                    {
                        int value = design.GetValue(index);
                        int run = 1;
                        while (index + run < count && run < ushort.MaxValue &&
                            design.GetValue(index + run) == value) run++;
                        writer.Write(value);
                        writer.Write((ushort)run);
                        index += run;
                    }
                }
            }
            return Compress(raw.ToArray());
        }

        private FurnitureSnapshot ReadFurnitureSnapshot(byte[] data)
        {
            byte[] raw = Decompress(data);
            using var reader = new BinaryReader(new MemoryStream(raw), Encoding.UTF8);
            if (reader.ReadInt32() != 1) throw new InvalidOperationException("Furniture version mismatch.");
            var result = new FurnitureSnapshot();
            int setCount = ReadCount(reader, 0, 1024);
            for (int i = 0; i < setCount; i++)
            {
                result.Sets.Add(new FurnitureSet
                {
                    Name = ReadLimitedString(reader, 20),
                    ImportedFrom = ReadLimitedString(reader, 256)
                });
            }
            int designCount = ReadCount(reader, 0,
                SubsystemFurnitureBlockBehavior.MaxDesigns);
            var indices = new HashSet<int>();
            for (int i = 0; i < designCount; i++)
            {
                int index = reader.ReadInt32();
                int resolution = reader.ReadInt32();
                if (index < 0 || index >= SubsystemFurnitureBlockBehavior.MaxDesigns ||
                    !indices.Add(index) || resolution < 2 || resolution > 16)
                    throw new InvalidOperationException("Invalid furniture design header.");
                string name = ReadLimitedString(reader, 20);
                var mode = (FurnitureInteractionMode)reader.ReadByte();
                int linked = reader.ReadInt32();
                int set = reader.ReadInt32();
                if ((int)mode < (int)FurnitureInteractionMode.None ||
                    (int)mode > (int)FurnitureInteractionMode.ConnectedMultistate ||
                    linked < -1 || linked >= SubsystemFurnitureBlockBehavior.MaxDesigns ||
                    set < -1 || set >= setCount)
                    throw new InvalidOperationException("Invalid furniture design references.");
                int valueCount = reader.ReadInt32();
                int expected = resolution * resolution * resolution;
                if (valueCount != expected) throw new InvalidOperationException("Invalid furniture size.");
                var values = new int[valueCount];
                int cursor = 0;
                while (cursor < valueCount)
                {
                    int value = reader.ReadInt32();
                    int run = reader.ReadUInt16();
                    if (run < 1 || cursor + run > valueCount)
                        throw new InvalidOperationException("Invalid furniture value run.");
                    for (int j = 0; j < run; j++) values[cursor++] = value;
                }
                result.Designs.Add(new FurnitureDesignRecord
                {
                    Index = index,
                    Resolution = resolution,
                    Name = name,
                    InteractionMode = mode,
                    LinkedIndex = linked,
                    SetIndex = set,
                    Values = values
                });
            }
            HashSet<int> knownIndices = result.Designs.Select(item => item.Index).ToHashSet();
            if (result.Designs.Any(item => item.LinkedIndex >= 0 &&
                !knownIndices.Contains(item.LinkedIndex)))
                throw new InvalidOperationException("Furniture link target is missing.");
            return result;
        }

        private void ApplyFurnitureSnapshot(FurnitureSnapshot snapshot, bool mergeRequest,
            int sourceClientId)
        {
            SubsystemFurnitureBlockBehavior behavior = m_project?
                .FindSubsystem<SubsystemFurnitureBlockBehavior>(false);
            SubsystemTerrain terrain = m_project?.FindSubsystem<SubsystemTerrain>(false);
            if (behavior == null || terrain == null) return;
            if (!mergeRequest)
            {
                ApplyAuthoritativeFurnitureSnapshot(behavior, terrain, snapshot);
                return;
            }

            FurnitureDesign[] designs = ScMultiplayer.ModManager.ModParentField
                .GetParentField<FurnitureDesign[]>(behavior, "m_furnitureDesigns",
                    typeof(SubsystemFurnitureBlockBehavior));
            List<FurnitureSet> sets = ScMultiplayer.ModManager.ModParentField
                .GetParentField<List<FurnitureSet>>(behavior, "m_furnitureSets",
                    typeof(SubsystemFurnitureBlockBehavior));
            var setMapping = new Dictionary<int, FurnitureSet>();
            for (int i = 0; i < snapshot.Sets.Count; i++)
            {
                FurnitureSet incomingSet = snapshot.Sets[i];
                FurnitureSet targetSet = sets.FirstOrDefault(item =>
                    string.Equals(item.Name, incomingSet.Name, StringComparison.Ordinal) &&
                    string.Equals(item.ImportedFrom ?? string.Empty,
                        incomingSet.ImportedFrom ?? string.Empty, StringComparison.Ordinal));
                if (targetSet == null)
                {
                    targetSet = new FurnitureSet
                    {
                        Name = incomingSet.Name,
                        ImportedFrom = incomingSet.ImportedFrom
                    };
                    sets.Add(targetSet);
                }
                setMapping[i] = targetSet;
            }

            var mapping = new Dictionary<int, int>();
            var imported = new Dictionary<int, FurnitureDesign>();
            foreach (FurnitureDesignRecord record in snapshot.Designs)
            {
                FurnitureDesign candidate = CreateFurnitureDesign(terrain, record);
                FurnitureDesign existing = designs[record.Index];
                int targetIndex = record.Index;
                if (existing != null && !existing.Compare(candidate))
                {
                    FurnitureDesign matching = behavior.FindMatchingDesign(candidate);
                    if (matching != null) targetIndex = matching.Index;
                    else
                    {
                        targetIndex = Array.FindIndex(designs, item => item == null);
                        if (targetIndex < 0) continue;
                    }
                }
                FurnitureDesign target = designs[targetIndex];
                if (target == null || !target.Compare(candidate))
                {
                    target = candidate;
                    SetFurnitureDesignIndex(target, targetIndex);
                    designs[targetIndex] = target;
                }
                mapping[record.Index] = targetIndex;
                imported[record.Index] = target;
            }
            foreach (FurnitureDesignRecord record in snapshot.Designs)
            {
                if (!imported.TryGetValue(record.Index, out FurnitureDesign design)) continue;
                design.LinkedDesign = record.LinkedIndex >= 0 &&
                    mapping.TryGetValue(record.LinkedIndex, out int linkedIndex)
                    ? designs[linkedIndex] : null;
                design.FurnitureSet = record.SetIndex >= 0 &&
                    setMapping.TryGetValue(record.SetIndex, out FurnitureSet set)
                    ? set : null;
            }
            if (sourceClientId > 0)
                m_clientFurnitureMappings[sourceClientId] = mapping;
        }

        private void ApplyAuthoritativeFurnitureSnapshot(
            SubsystemFurnitureBlockBehavior behavior, SubsystemTerrain terrain,
            FurnitureSnapshot snapshot)
        {
            FurnitureDesign[] previous = ScMultiplayer.ModManager.ModParentField
                .GetParentField<FurnitureDesign[]>(behavior, "m_furnitureDesigns",
                    typeof(SubsystemFurnitureBlockBehavior));
            var designs = new FurnitureDesign[SubsystemFurnitureBlockBehavior.MaxDesigns];
            var sets = snapshot.Sets.Select(item => new FurnitureSet
            {
                Name = item.Name,
                ImportedFrom = item.ImportedFrom
            }).ToList();
            foreach (FurnitureDesignRecord record in snapshot.Designs)
            {
                FurnitureDesign design = CreateFurnitureDesign(terrain, record);
                SetFurnitureDesignIndex(design, record.Index);
                designs[record.Index] = design;
            }
            foreach (FurnitureDesignRecord record in snapshot.Designs)
            {
                FurnitureDesign design = designs[record.Index];
                design.LinkedDesign = record.LinkedIndex >= 0
                    ? designs[record.LinkedIndex] : null;
                design.FurnitureSet = record.SetIndex >= 0 ? sets[record.SetIndex] : null;
            }

            Dictionary<int, int> remapping = BuildFurnitureRemapping(previous, designs);
            if (m_pendingFurnitureSnapshot != null)
            {
                bool allPendingResolved = true;
                foreach (FurnitureDesignRecord record in m_pendingFurnitureSnapshot.Designs)
                {
                    FurnitureDesign pending = CreateFurnitureDesign(terrain, record);
                    int resolvedIndex = Array.FindIndex(designs, item =>
                        item != null && item.Compare(pending));
                    if (resolvedIndex >= 0)
                        remapping[record.Index] = resolvedIndex;
                    else
                        allPendingResolved = false;
                }
                if (allPendingResolved) m_pendingFurnitureSnapshot = null;
            }
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(behavior,
                "m_furnitureDesigns", designs, typeof(SubsystemFurnitureBlockBehavior));
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(behavior,
                "m_furnitureSets", sets, typeof(SubsystemFurnitureBlockBehavior));
            RemapLocalFurnitureInventory(remapping, designs);
        }

        private static FurnitureDesign CreateFurnitureDesign(SubsystemTerrain terrain,
            FurnitureDesignRecord record)
        {
            var design = new FurnitureDesign(terrain);
            design.SetValues(record.Resolution, record.Values);
            design.Name = record.Name;
            design.InteractionMode = record.InteractionMode;
            return design;
        }

        private static void SetFurnitureDesignIndex(FurnitureDesign design, int index)
        {
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(design,
                "m_index", index, typeof(FurnitureDesign));
        }

        private static Dictionary<int, int> BuildFurnitureRemapping(
            FurnitureDesign[] previous, FurnitureDesign[] authoritative)
        {
            var result = new Dictionary<int, int>();
            for (int oldIndex = 0; oldIndex < previous.Length; oldIndex++)
            {
                FurnitureDesign oldDesign = previous[oldIndex];
                if (oldDesign == null) continue;
                for (int newIndex = 0; newIndex < authoritative.Length; newIndex++)
                {
                    if (authoritative[newIndex] != null &&
                        authoritative[newIndex].Compare(oldDesign))
                    {
                        result[oldIndex] = newIndex;
                        break;
                    }
                }
            }
            return result;
        }

        private void RemapLocalFurnitureInventory(Dictionary<int, int> remapping,
            FurnitureDesign[] designs)
        {
            ComponentPlayer player = m_owner.GetCircuitPlayer(
                ScMultiplayer.client?.ClientID ?? -1);
            IInventory inventory = player?.ComponentMiner?.Inventory;
            if (inventory == null) return;
            for (int slot = 0; slot < inventory.SlotsCount; slot++)
            {
                int value = inventory.GetSlotValue(slot);
                int count = inventory.GetSlotCount(slot);
                if (value == 0 || count <= 0 ||
                    !(BlocksManager.Blocks[Terrain.ExtractContents(value)] is FurnitureBlock))
                    continue;
                int data = Terrain.ExtractData(value);
                int oldIndex = FurnitureBlock.GetDesignIndex(data);
                if (!remapping.TryGetValue(oldIndex, out int newIndex) ||
                    oldIndex == newIndex || designs[newIndex] == null)
                    continue;
                FurnitureDesign design = designs[newIndex];
                int newData = FurnitureBlock.SetDesignIndex(data, newIndex,
                    design.ShadowStrengthFactor, design.IsLightEmitter);
                int newValue = Terrain.ReplaceData(value, newData);
                inventory.RemoveSlotItems(slot, int.MaxValue);
                inventory.AddSlotItems(slot, newValue, count);
            }
        }

        private byte[] WriteSignSnapshot()
        {
            SubsystemSignBlockBehavior behavior = m_project?
                .FindSubsystem<SubsystemSignBlockBehavior>(false);
            if (behavior == null) return null;
            IDictionary dictionary = ScMultiplayer.ModManager.ModParentField
                .GetParentField<IDictionary>(behavior, "m_textsByPoint",
                    typeof(SubsystemSignBlockBehavior));
            var points = dictionary.Keys.Cast<Point3>().OrderBy(item => item.X)
                .ThenBy(item => item.Y).ThenBy(item => item.Z).ToList();
            using var raw = new MemoryStream();
            using (var writer = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(1);
                writer.Write(points.Count);
                foreach (Point3 point in points)
                {
                    SignData sign = behavior.GetSignData(point) ?? new SignData();
                    writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Z);
                    for (int i = 0; i < 4; i++) writer.Write(sign.Lines[i] ?? string.Empty);
                    for (int i = 0; i < 4; i++) writer.Write(sign.Colors[i].PackedValue);
                    writer.Write(sign.Url ?? string.Empty);
                }
            }
            return Compress(raw.ToArray());
        }

        private List<SignRecord> ReadSignSnapshot(byte[] data)
        {
            byte[] raw = Decompress(data);
            using var reader = new BinaryReader(new MemoryStream(raw), Encoding.UTF8);
            if (reader.ReadInt32() != 1) throw new InvalidOperationException("Sign version mismatch.");
            int count = ReadCount(reader, 0, 65536);
            var result = new List<SignRecord>(count);
            for (int n = 0; n < count; n++)
            {
                var record = new SignRecord
                {
                    Point = new Point3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
                    Lines = new string[4],
                    Colors = new Color[4]
                };
                for (int i = 0; i < 4; i++) record.Lines[i] = ReadLimitedString(reader, 256);
                for (int i = 0; i < 4; i++) record.Colors[i] = new Color(reader.ReadUInt32());
                record.Url = ReadLimitedString(reader, 2048);
                result.Add(record);
            }
            return result;
        }

        private byte[] CreateSignDelta(byte[] previousData, byte[] currentData)
        {
            List<SignRecord> previous = ReadSignSnapshot(previousData);
            List<SignRecord> current = ReadSignSnapshot(currentData);
            Dictionary<Point3, SignRecord> previousByPoint = previous.ToDictionary(
                item => item.Point);
            List<SignRecord> changed = current.Where(item =>
                !previousByPoint.TryGetValue(item.Point, out SignRecord old) ||
                !SignRecordEquals(old, item)).ToList();
            return WriteSignRecords(changed);
        }

        private static byte[] WriteSignRecords(List<SignRecord> records)
        {
            using var raw = new MemoryStream();
            using (var writer = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(1);
                writer.Write(records.Count);
                foreach (SignRecord record in records)
                {
                    writer.Write(record.Point.X);
                    writer.Write(record.Point.Y);
                    writer.Write(record.Point.Z);
                    for (int i = 0; i < 4; i++)
                        writer.Write(record.Lines[i] ?? string.Empty);
                    for (int i = 0; i < 4; i++)
                        writer.Write(record.Colors[i].PackedValue);
                    writer.Write(record.Url ?? string.Empty);
                }
            }
            return Compress(raw.ToArray());
        }

        private void ApplySignSnapshot(List<SignRecord> records, int sourceClientId)
        {
            SubsystemSignBlockBehavior behavior = m_project?
                .FindSubsystem<SubsystemSignBlockBehavior>(false);
            SubsystemTerrain terrain = m_project?.FindSubsystem<SubsystemTerrain>(false);
            if (behavior == null || terrain == null) return;
            ComponentPlayer source = sourceClientId > 0
                ? m_owner.GetCircuitPlayer(sourceClientId) : null;
            if (sourceClientId > 0 && source == null) return;
            if (sourceClientId <= 0)
            {
                IDictionary dictionary = ScMultiplayer.ModManager.ModParentField
                    .GetParentField<IDictionary>(behavior, "m_textsByPoint",
                        typeof(SubsystemSignBlockBehavior));
                var authoritativePoints = new HashSet<Point3>(records.Select(item => item.Point));
                foreach (Point3 stale in dictionary.Keys.Cast<Point3>().Where(point =>
                    !authoritativePoints.Contains(point)).ToArray())
                    dictionary.Remove(stale);
                IList lastUpdatePositions = ScMultiplayer.ModManager.ModParentField
                    .GetParentField<IList>(behavior, "m_lastUpdatePositions",
                        typeof(SubsystemSignBlockBehavior));
                lastUpdatePositions.Clear();
            }
            foreach (SignRecord record in records)
            {
                int value = terrain.Terrain.GetCellValue(record.Point.X,
                    record.Point.Y, record.Point.Z);
                if (!(BlocksManager.Blocks[Terrain.ExtractContents(value)] is SignBlock))
                    continue;
                if (source != null && (source.ComponentBody == null ||
                    Vector3.DistanceSquared(source.ComponentBody.Position,
                        new Vector3(record.Point) + new Vector3(0.5f)) > 64f))
                    continue;
                SignData current = behavior.GetSignData(record.Point);
                if (sourceClientId > 0 && current != null &&
                    SignEquals(current, record)) continue;
                behavior.SetSignData(record.Point, record.Lines, record.Colors, record.Url);
            }
        }

        private static bool SignEquals(SignData current, SignRecord incoming) =>
            current.Lines.SequenceEqual(incoming.Lines) &&
            current.Colors.SequenceEqual(incoming.Colors) &&
            string.Equals(current.Url ?? string.Empty, incoming.Url ?? string.Empty,
                StringComparison.Ordinal);

        private static bool SignRecordEquals(SignRecord first, SignRecord second) =>
            first.Lines.SequenceEqual(second.Lines) &&
            first.Colors.SequenceEqual(second.Colors) &&
            string.Equals(first.Url ?? string.Empty, second.Url ?? string.Empty,
                StringComparison.Ordinal);

        private void PublishFurnaces()
        {
            List<SuComponentFurnace> furnaces = m_project.Entities.Select(entity =>
                entity.FindComponent<SuComponentFurnace>()).Where(item => item != null).ToList();
            var records = new List<FurnaceStateRecord>();
            var currentPoints = new HashSet<Point3>();
            foreach (SuComponentFurnace furnace in furnaces)
            {
                var state = new FurnaceStateRecord
                {
                    Point = furnace.Coordinates,
                    FireTimeRemaining = furnace.FireTimeRemaining,
                    HeatLevel = furnace.HeatLevel,
                    SmeltingProgress = furnace.SmeltingProgress
                };
                currentPoints.Add(state.Point);
                bool active = state.FireTimeRemaining > 0f || state.HeatLevel > 0f ||
                    state.SmeltingProgress > 0f;
                bool changed = !m_lastFurnaceStates.TryGetValue(state.Point,
                    out FurnaceStateRecord previous) ||
                    MathUtils.Abs(previous.FireTimeRemaining - state.FireTimeRemaining) > 0.01f ||
                    MathUtils.Abs(previous.HeatLevel - state.HeatLevel) > 0.001f ||
                    MathUtils.Abs(previous.SmeltingProgress - state.SmeltingProgress) > 0.001f;
                if (active || (changed && previous != null)) records.Add(state);
                m_lastFurnaceStates[state.Point] = state;
            }
            foreach (Point3 stale in m_lastFurnaceStates.Keys.Where(point =>
                !currentPoints.Contains(point)).ToArray())
                m_lastFurnaceStates.Remove(stale);
            for (int offset = 0; offset < records.Count; offset += 64)
            {
                var message = new WorldObjectSyncMessage
                {
                    Stage = WorldObjectSyncStage.FurnaceBatch
                };
                message.Furnaces.AddRange(records.Skip(offset).Take(64));
                NetworkMessageSender.SendWorldObjectSync(-1, message, latest: false);
            }
        }

        private void ApplyFurnaces(List<FurnaceStateRecord> records)
        {
            var furnaces = m_project.Entities.Select(entity =>
                entity.FindComponent<SuComponentFurnace>()).Where(item => item != null)
                .ToDictionary(item => item.Coordinates);
            foreach (FurnaceStateRecord record in records)
                if (furnaces.TryGetValue(record.Point, out SuComponentFurnace furnace))
                    furnace.ApplyNetworkState(record.FireTimeRemaining,
                        record.HeatLevel, record.SmeltingProgress);
        }

        private void PublishPistons()
        {
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            List<IMovingBlockSet> pistonSets = moving.MovingBlockSets.Where(item =>
                item.Id == "Piston" && item.Tag is Point3).ToList();
            m_pistonMotionSequence = m_pistonMotionSequence == int.MaxValue
                ? 1 : m_pistonMotionSequence + 1;
            var message = new WorldObjectSyncMessage
            {
                Stage = WorldObjectSyncStage.PistonBatch,
                MotionSequence = m_pistonMotionSequence,
                IsComplete = pistonSets.Count <= 64
            };
            foreach (IMovingBlockSet set in pistonSets.Take(64))
            {
                Type type = set.GetType();
                var record = new PistonMotionRecord
                {
                    Point = (Point3)set.Tag,
                    StartPosition = GetField(set, "StartPosition", set.Position, type),
                    Position = set.Position,
                    TargetPosition = GetField(set, "TargetPosition", Vector3.Zero, type),
                    Speed = GetField(set, "Speed", 0f, type),
                    Acceleration = GetField(set, "Acceleration", 0f, type),
                    Drag = GetField(set, "Drag", 0f, type),
                    Smoothness = GetField(set, "Smoothness", Vector2.Zero, type)
                };
                record.Blocks.AddRange(set.Blocks.Take(16));
                message.Pistons.Add(record);
            }
            NetworkMessageSender.SendWorldObjectSync(-1, message, latest: true);
        }

        private void ApplyPistons(List<PistonMotionRecord> records, int motionSequence,
            bool isComplete)
        {
            if (motionSequence <= m_lastPistonMotionSequence) return;
            m_lastPistonMotionSequence = motionSequence;
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            var authoritative = new HashSet<Point3>(records.Select(item => item.Point));
            if (isComplete)
                foreach (IMovingBlockSet stale in moving.MovingBlockSets.Where(item =>
                    item.Id == "Piston" && item.Tag is Point3 point &&
                    !authoritative.Contains(point)).ToArray())
                    moving.RemoveMovingBlockSet(stale);
            foreach (PistonMotionRecord record in records)
            {
                IMovingBlockSet set = moving.MovingBlockSets.FirstOrDefault(item =>
                    item.Id == "Piston" && item.Tag is Point3 point && point == record.Point);
                if (set == null)
                {
                    IMovingBlockSet added = moving.AddMovingBlockSet(record.Position,
                        record.TargetPosition,
                        record.Speed, record.Acceleration, record.Drag, record.Smoothness,
                        record.Blocks, "Piston", record.Point, testCollision: false);
                    if (added != null)
                        ScMultiplayer.ModManager.ModParentField.ModifyParentField(added,
                            "StartPosition", record.StartPosition, added.GetType());
                    continue;
                }
                Type type = set.GetType();
                if (Vector3.DistanceSquared(set.Position, record.Position) > 0.01f)
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                        "Position", record.Position, type);
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                    "StartPosition", record.StartPosition, type);
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                    "TargetPosition", record.TargetPosition, type);
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                    "Speed", record.Speed, type);
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                    "Acceleration", record.Acceleration, type);
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                    "Drag", record.Drag, type);
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                    "Smoothness", record.Smoothness, type);
            }
        }

        private static T GetField<T>(object instance, string name, T fallback, Type type)
        {
            try
            {
                return ScMultiplayer.ModManager.ModParentField.GetParentField<T>(
                    instance, name, type);
            }
            catch
            {
                return fallback;
            }
        }

        private void TrimIncoming(double now)
        {
            IncomingSnapshot[] expired = m_incoming.Where(item =>
                now - item.Value.LastUpdateTime > IncomingRetention)
                .Select(item => item.Value).ToArray();
            foreach (string key in m_incoming.Where(item =>
                now - item.Value.LastUpdateTime > IncomingRetention)
                .Select(item => item.Key).ToArray())
                m_incoming.Remove(key);
            if (!ScMultiplayer.IsHost)
                foreach (WorldObjectSnapshotKind kind in expired.Where(item =>
                    !item.IsRequest).Select(item => item.Kind).Distinct())
                    RequestSnapshot(kind);
        }

        private static byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var stream = new DeflateStream(output, CompressionLevel.Fastest,
                leaveOpen: true))
                stream.Write(data, 0, data.Length);
            return output.ToArray();
        }

        private static byte[] Decompress(byte[] data)
        {
            using var input = new DeflateStream(new MemoryStream(data),
                CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaximumSnapshotBytes)
                    throw new InvalidOperationException("World-object snapshot is too large.");
            }
            return output.ToArray();
        }

        private static int ReadCount(BinaryReader reader, int minimum, int maximum)
        {
            int value = reader.ReadInt32();
            if (value < minimum || value > maximum)
                throw new InvalidOperationException("Invalid snapshot count.");
            return value;
        }

        private static string ReadLimitedString(BinaryReader reader, int maximumLength)
        {
            string value = reader.ReadString();
            if (value.Length > maximumLength)
                throw new InvalidOperationException("Snapshot string is too long.");
            return value;
        }
    }
}

using Engine;
using Game;
using GameEntitySystem;
using ScMultiplayer.Diagnostics;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
        // Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs:TryCollapseColumn
        // 塌落组（Id == "CollapsingBlock"）与活塞分开计数：活塞按 Tag(Point3) 认组，塌落组的
        // Tag 是 null，改用 StartPosition 所在格作键。
        private int m_collapsingMotionSequence;
        private int m_lastCollapsingMotionSequence;
        private readonly Dictionary<string, PendingHostMotionSet> m_pendingHostMotionSets =
            new Dictionary<string, PendingHostMotionSet>();
        private const double HostMotionSetReadyTimeout = 0.5;
        // 一条"等源格清空"的记录超过这个年龄就不再建组（主机那条组多半已经结束）。
        private const double HostMotionSetMaxStaleAge = 0.25;
        // 我们建的组超过这么久没被主机快照提到就移除（防止"结束"快照丢包留下幽灵）。
        private const double HostMotionSetMaxSilence = 0.6;
        // 组已从主机快照消失后，先等这么久再移除（等权威地形格落地，避免交接空档的小闪）。
        private const double HostMotionRemovalGrace = 0.25;
        private readonly Dictionary<IMovingBlockSet, double> m_hostMotionRemovalDue =
            new Dictionary<IMovingBlockSet, double>();
        private readonly Dictionary<string, double> m_hostMotionSetSeen =
            new Dictionary<string, double>();
        private HashSet<string> m_publishedPistonKeys = new HashSet<string>();
        private HashSet<string> m_publishedCollapsingKeys = new HashSet<string>();
        private bool m_motionStopHandlersDetached;
        private bool m_clientMotionCollisionAttached;
        private double m_nextClientMotionProbeTime;
        private double m_nextHostMotionProbeTime;
        // 我们自己按主机采样建出来的组。客户端自己也会跑活塞/塌落逻辑（电力触发
        // SubsystemPistonBlockBehavior.AdjustPiston → ProcessQueuedActions），它建的组用的是
        // 同一个 Tag，混在一起就会两个写入者互推（表现为穿过沙子、闪烁）。凡是带主机键但不在
        // 这个集合里的组，都是本端引擎自己建的，一律丢弃并请主机回权威值。
        private readonly HashSet<IMovingBlockSet> m_hostMotionSets =
            new HashSet<IMovingBlockSet>();
        // [SuAPI] 临时探针（跨端移动组定位用，验证后删除）：主机驱动型组的停/撞事件。
        private SubsystemMovingBlocks m_probeMovingBlocks;

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
            if (!ScMultiplayer.IsHost)
            {
                DetachClientMotionStopHandlers(m_project);
                EnsureHostMotionProbe(m_project);
                DiscardUnownedHostMotionSets(m_project);
                ExpireStaleHostMotionSets();
                ProbeClientMotionSets(now);
                FlushPendingHostMotionSets();
            }
            if (ScMultiplayer.IsHost)
            {
                bool due = now >= m_nextPistonTime;
                PublishPistons(force: due);
                PublishCollapsingSets(force: due);
                if (due)
                    m_nextPistonTime = now + PistonInterval;
                ProbeHostMotionSets(now);
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
            {
                ProbeHostMotionBatch("Piston", message.Pistons, message.MotionSequence);
                ApplyPistons(message.Pistons, message.MotionSequence,
                    message.IsComplete);
            }
            else if (message.Stage == WorldObjectSyncStage.CollapsingBatch)
            {
                ProbeHostMotionBatch("CollapsingBlock", message.Pistons,
                    message.MotionSequence);
                ApplyCollapsingSets(message.Pistons, message.MotionSequence,
                    message.IsComplete);
            }
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
            m_collapsingMotionSequence = 0;
            m_lastCollapsingMotionSequence = 0;
            m_pendingHostMotionSets.Clear();
            m_hostMotionSets.Clear();
            m_hostMotionSetSeen.Clear();
            m_publishedPistonKeys.Clear();
            m_publishedCollapsingKeys.Clear();
            m_motionStopHandlersDetached = false;
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

        private void PublishPistons(bool force)
        {
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            List<IMovingBlockSet> pistonSets = moving.MovingBlockSets.Where(item =>
                item.Id == "Piston" && item.Tag is Point3).ToList();
            // 组一出现/消失必须**当帧**发出去：活塞换向时主机先把杆子落成地形、下一帧又把它
            // 收进新组（地形格清空），若等 0.125s 定时器，客户端会先收到"清空"再等到组包，
            // 中间那段就是"杆子瞬间消失"。按 0.125s 节奏发只用于运动中的位置更新。
            var keys = new HashSet<string>(pistonSets.Select(item => HostMotionKey("Piston",
                (Point3)item.Tag)));
            bool changed = !keys.SetEquals(m_publishedPistonKeys);
            if (!changed && !force) return;
            m_publishedPistonKeys = keys;
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
                m_hostMotionSetSeen[HostMotionKey("Piston", record.Point)] = Time.RealTime;
                ApplyPistonRecord(record, force: false);
            }
            DropPendingMotionSets("Piston", authoritative);
        }

        private void ApplyPistonRecord(PistonMotionRecord record, bool force)
        {
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            IMovingBlockSet set = moving.MovingBlockSets.FirstOrDefault(item =>
                item.Id == "Piston" && item.Tag is Point3 point && point == record.Point);
            if (set != null && !m_hostMotionSets.Contains(set))
            {
                // 本端引擎自己建的活塞组（同一个 Tag）：丢弃，改用主机采样建我们自己的。
                RequestHostMotionCellRepair(set);
                moving.RemoveMovingBlockSet(set);
                set = null;
            }
            if (set != null && HostMotionBlocksDiffer(set, record))
            {
                // 原地同步方块列表（不重建，避免那一帧整组缺席造成闪烁）。
                UpdateHostMotionBlocks(set, record, set.GetType());
                VerifyHostMotionOriginCells(set);
            }
            if (set == null)
            {
                // 活塞组立即创建，**不能等**：被推的方块全在这个组里，等一格就等于整叠沙子
                // 缺席一瞬（实测"活塞推出去时整叠闪一下"）。引擎会把杆子/被推方块所在格清掉，
                // 客户端也已不再抢先落地，所以先把组建出来最稳。
                IMovingBlockSet added = moving.AddMovingBlockSet(record.Position,
                    record.TargetPosition,
                    record.Speed, record.Acceleration, record.Drag, record.Smoothness,
                    record.Blocks, "Piston", record.Point, testCollision: false);
                if (added != null)
                {
                    m_hostMotionSets.Add(added);
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(added,
                        "StartPosition", record.StartPosition, added.GetType());
                    VerifyHostMotionOriginCells(added);
                }
                return;
            }
            Type type = set.GetType();
            if (TryAdoptHostMotionSample(set, record, type, out Vector3 adoptedPosition))
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                    "Position", adoptedPosition, type);
            WriteHostMotionSetFields(set, record);
        }

        // Source: Survivalcraft/Game/SubsystemMovingBlocks.cs:SubsystemMovingBlocks.TerrainCollision
        // 主机驱动型移动方块组在客户端每帧都会做碰撞检查（testCollision:false 只跳过创建那一次），
        // 一旦判停，引擎就把方块按 origin+offset 写回原格并移除组。而"源格→空气"的地形消息与
        // 移动组包走两条通道：组先到时源格还是旧方块 → 组在原点就被判停。所以组要等本端这些
        // 格子已经空了再建（有 Deadline 兜底，避免永远不建）。
        private static string HostMotionKey(string id, Point3 key) =>
            $"{id}:{key.X},{key.Y},{key.Z}";

        private bool AreHostMotionCellsClear(PistonMotionRecord record)
        {
            Terrain terrain = m_project?
                .FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            if (terrain == null) return true;
            Point3 origin = Terrain.ToCell(MathUtils.Round(record.Position.X),
                MathUtils.Round(record.Position.Y), MathUtils.Round(record.Position.Z));
            foreach (MovingBlock block in record.Blocks)
            {
                Point3 cell = origin + block.Offset;
                if (!terrain.IsCellValid(cell.X, cell.Y, cell.Z)) continue;
                // 只等"同一种方块还在原地"的情况：那正是会被重复渲染的那一份（换向瞬间
                // 上一腿的杆子已经落成地形、这一腿的组又把它画了一遍 → 平板看到两个头部）。
                // 其它方块挡在前面不算（活塞本来就要把它们推走 / 塌落就是要压过去）。
                if (Terrain.ExtractContents(terrain.GetCellValue(cell.X, cell.Y, cell.Z)) ==
                    Terrain.ExtractContents(block.Value))
                    return false;
            }
            return true;
        }

        // 返回 true = 现在就可以建组；false = 已排队等源格变空。
        private bool TryScheduleHostMotionSet(string id, PistonMotionRecord record)
        {
            if (AreHostMotionCellsClear(record)) return true;
            string key = HostMotionKey(id, record.Point);
            if (!m_pendingHostMotionSets.TryGetValue(key, out PendingHostMotionSet pending))
            {
                // [SuAPI] 临时探针（跨端移动组原点回退定位用，验证后删除）
                ScMultiplayerOperationLog.Write("event=motion.defer id=" + id +
                    " cell=" + record.Point.X.ToString(CultureInfo.InvariantCulture) + "," +
                    record.Point.Y.ToString(CultureInfo.InvariantCulture) + "," +
                    record.Point.Z.ToString(CultureInfo.InvariantCulture) +
                    " pos=" + record.Position.X.ToString("0.###", CultureInfo.InvariantCulture) +
                    "," + record.Position.Y.ToString("0.###", CultureInfo.InvariantCulture) +
                    "," + record.Position.Z.ToString("0.###", CultureInfo.InvariantCulture));
                m_pendingHostMotionSets[key] = new PendingHostMotionSet
                {
                    Id = id,
                    Record = record,
                    Deadline = Time.RealTime + HostMotionSetReadyTimeout,
                    RecordedTime = Time.RealTime
                };
            }
            else
            {
                pending.Record = record;
            }
            return false;
        }

        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:Load
        // Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs:Load
        // 这两个行为把 SubsystemMovingBlocks.Stopped 订阅成"把移动组落成地形 / 翻转活塞扩展位 /
        // 移除组"。客户端不是权威：它抢先落地会在换向瞬间留下"组已移除、主机地形格还没到"的空档
        // （杆子瞬间消失），并用本端算出来的扩展位覆盖权威值；随后主机采样到达时，我这边只能把组
        // 建回**当前位置**（已经到最远），看起来就是"直接窜到最大伸长处"。
        // 所以客户端把这两个订阅摘掉，落地完全交给主机的地形格；组的生命周期仍由主机快照决定
        // （收到→建、不再出现→移除）。CollidedWithTerrain 同样摘掉：它会在客户端 DestroyCell
        // （砸掉挡路的方块并喷粒子，实测出现"爆了"的效果）或把塌落组判停，而客户端这份组只是
        // 表现，位置已由主机采样插值驱动，不该由本端地形决定走向。
        private void DetachClientMotionStopHandlers(Project project)
        {
            if (ScMultiplayer.IsHost || m_motionStopHandlersDetached) return;
            SubsystemMovingBlocks moving = project?.FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            SubsystemPistonBlockBehavior piston =
                project.FindSubsystem<SubsystemPistonBlockBehavior>(false);
            SubsystemCollapsingBlockBehavior collapsing =
                project.FindSubsystem<SubsystemCollapsingBlockBehavior>(false);
            Type type = moving.GetType();
            int removed = 0;
            // 只摘 Stopped：客户端不抢先"把组落成地形 / 翻转活塞扩展位 / 移除组"。
            removed += DetachMotionEventHandlers(moving, type, "Stopped", piston, collapsing);
            // 活塞的 CollidedWithTerrain 处理是"本端 DestroyCell 砸掉挡路方块"：客户端这份组
            // 每帧都在和地形/别的组重叠（实测该事件 2.6 万次），于是"本端砸掉 → 主机权威格
            // 又发回来 → 下一帧再砸"，表现就是被推的那一叠时不时闪、第一个方块像被穿过去。
            // 客户端只做表现，撞击结果由主机的权威格与组快照负责，所以这一个也摘掉。
            // 塌落组的 CollidedWithTerrain 保留：沙子要靠它按地形停住，否则会一直往地里沉。
            removed += DetachMotionEventHandlers(moving, type, "CollidedWithTerrain", piston);
            // 摘掉的是"本端 DestroyCell 砸掉挡路方块"那一半能力；主机那份组的这个事件还附带
            // "前方格不可推就 Stop()" —— 引擎随即把位置吸附到整格，就是"推到推不动的时候刚好
            // 停住"。摘干净以后客户端这份组既不停也不吸附，会一路跑到自己的目标点（实测比主机
            // 采样超前约半格），随后被主机采样拉回来，表现成"伸进沙子半格又被弹回"。
            // 所以这里补一个只判停、绝不写地形的版本（条件与主机 471-497 行逐条对齐）。
            if (!m_clientMotionCollisionAttached && piston != null)
            {
                moving.CollidedWithTerrain += ClientMotionCollidedWithTerrain;
                m_clientMotionCollisionAttached = true;
            }
            if (removed == 0)
            {
                m_motionStopHandlersDetached = true;
                return;
            }
            m_motionStopHandlersDetached = true;
            // [SuAPI] 临时探针（跨端移动组定位用，验证后删除）
            ScMultiplayerOperationLog.Write("event=motion.detach removed=" +
                removed.ToString(CultureInfo.InvariantCulture));
        }

        private static int DetachMotionEventHandlers(SubsystemMovingBlocks moving, Type type,
            string eventName, params object[] owners)
        {
            Delegate handlers = GetField<Delegate>(moving, eventName, null, type);
            if (handlers == null) return 0;
            Delegate[] list = handlers.GetInvocationList();
            Delegate kept = null;
            int removed = 0;
            foreach (Delegate handler in list)
            {
                bool drop = false;
                foreach (object owner in owners)
                {
                    if (owner != null && ReferenceEquals(handler.Target, owner))
                    {
                        drop = true;
                        break;
                    }
                }
                if (drop)
                {
                    removed++;
                    continue;
                }
                kept = Delegate.Combine(kept, handler);
            }
            if (removed > 0)
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(moving, eventName,
                    kept, type);
            return removed;
        }

        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:MovingBlocksCollidedWithTerrain
        // 主机版本的完整判定是：tag 格仍是活塞(237) → 只处理活塞**前方**的格 → 该格 IsCollidable
        // 就 movingBlockSet.Stop()（引擎随后把位置吸附到整格 = "推到推不动的时候刚好停住"），
        // 否则 DestroyCell 把挡路方块砸掉。客户端只要前一半：判停、绝不写地形（砸方块会喷粒子，
        // 且会被主机权威格改回来，表现为闪烁）。
        private const int ClientPistonContents = 237;

        private void ClientMotionCollidedWithTerrain(IMovingBlockSet set, Point3 p)
        {
            if (set == null || set.Id != "Piston") return;
            if (!(set.Tag is Point3 key)) return;
            Terrain terrain = m_project?
                .FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            if (terrain == null || !terrain.IsCellValid(p.X, p.Y, p.Z)) return;
            int tagValue = terrain.GetCellValue(key.X, key.Y, key.Z);
            if (Terrain.ExtractContents(tagValue) != ClientPistonContents) return;
            Point3 face = CellFace.FaceToPoint3(
                PistonBlock.GetFace(Terrain.ExtractData(tagValue)));
            int contact = p.X * face.X + p.Y * face.Y + p.Z * face.Z;
            int piston = key.X * face.X + key.Y * face.Y + key.Z * face.Z;
            if (contact <= piston) return;
            int cellValue = terrain.GetCellValue(p.X, p.Y, p.Z);
            if (Terrain.ExtractContents(cellValue) == 0) return;
            // 本端地形里那份"本组方块自己的副本"（主机拾取走方块时清掉的那些格，清空消息还没到）
            // 不算挡路；否则刚建组就会把自己判停。
            if (IsHostMotionOwnBlockCell(set, p, cellValue)) return;
            if (!BlocksManager.Blocks[Terrain.ExtractContents(cellValue)].IsCollidable) return;
            set.Stop();
        }

        // 该格在本端地形里是否仍是本组自己某个方块的一份：原点格（StartPosition + Offset，
        // 即空壳影子的位置）或本组那一格当前该在的坐标。同种方块的"真挡路方块"不在这些坐标上，
        // 所以不会被这条挡掉（被推的沙子就是这样判停的）。
        private static bool IsHostMotionOwnBlockCell(IMovingBlockSet set, Point3 p, int cellValue)
        {
            int contents = Terrain.ExtractContents(cellValue);
            Type type = set.GetType();
            Vector3 start = GetField(set, "StartPosition", set.Position, type);
            Point3 startCell = Terrain.ToCell(MathUtils.Round(start.X), MathUtils.Round(start.Y),
                MathUtils.Round(start.Z));
            if (p == startCell) return true;
            foreach (MovingBlock block in set.Blocks)
            {
                if (Terrain.ExtractContents(block.Value) != contents) continue;
                if (startCell + block.Offset == p) return true;
                Point3 current = Terrain.ToCell(
                    MathUtils.Round(set.Position.X + block.Offset.X),
                    MathUtils.Round(set.Position.Y + block.Offset.Y),
                    MathUtils.Round(set.Position.Z + block.Offset.Z));
                if (current == p) return true;
            }
            return false;
        }

        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:AdjustPiston
        // 客户端自己也会跑活塞/塌落逻辑并建出同 Tag 的组；主机才是权威，这里把"不是我们建的"
        // 主机键组（Id=Piston 或带 Point3 Tag 的 CollapsingBlock）丢掉，并对它动过的格子请
        // 主机回权威值（本地那些拾取/落位写入都是本端预测，容易被主机改回而闪烁）。
        // 放置者自己预测出来的塌落组 Tag 为 null，不在这里处理，保持原有本地表现。
        private void DiscardUnownedHostMotionSets(Project project)
        {
            SubsystemMovingBlocks moving = project?.FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            if (m_hostMotionSets.Count > 0)
                m_hostMotionSets.RemoveWhere(item => item == null ||
                    !moving.MovingBlockSets.Contains(item));
            foreach (IMovingBlockSet set in moving.MovingBlockSets.ToArray())
            {
                if (set == null) continue;
                if (!(set.Tag is Point3) ||
                    (set.Id != "Piston" && set.Id != "CollapsingBlock"))
                    continue;
                if (m_hostMotionSets.Contains(set)) continue;
                // [SuAPI] 临时探针（跨端移动组定位用，验证后删除）
                ScMultiplayerOperationLog.Write("event=motion.discard id=" + set.Id +
                    " tag=" + ((Point3)set.Tag).X.ToString(CultureInfo.InvariantCulture) + "," +
                    ((Point3)set.Tag).Y.ToString(CultureInfo.InvariantCulture) + "," +
                    ((Point3)set.Tag).Z.ToString(CultureInfo.InvariantCulture));
                RequestHostMotionCellRepair(set);
                moving.RemoveMovingBlockSet(set);
            }
        }

        private static void RequestHostMotionCellRepair(IMovingBlockSet set)
        {
            ScMultiplayer instance = ScMultiplayer.currentInstance;
            if (instance == null) return;
            Point3 origin = Terrain.ToCell(MathUtils.Round(set.Position.X),
                MathUtils.Round(set.Position.Y), MathUtils.Round(set.Position.Z));
            foreach (MovingBlock block in set.Blocks)
            {
                Point3 cell = origin + block.Offset;
                instance.RecordClientTerrainCellMissing(cell, 0L, "localmotion");
            }
        }

        // Source: Survivalcraft/Game/SubsystemMovingBlocks.cs:SubsystemMovingBlocks.Update
        // "原件位置的空壳影子"：主机把方块拾取进组后，本端那几格（= 组的 StartPosition 所在格
        // 加各方块 offset）应该是空气。若客户端地形还留着一份同种方块，就是残留副本。
        // 这里只在这些格**确实还脏**时按格请主机回权威值（空气），清干净后自然停止请求。
        private void VerifyHostMotionOriginCells(IMovingBlockSet set)
        {
            ScMultiplayer instance = ScMultiplayer.currentInstance;
            Terrain terrain = m_project?
                .FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            if (instance == null || terrain == null) return;
            Type type = set.GetType();
            Vector3 start = GetField(set, "StartPosition", set.Position, type);
            Point3 origin = Terrain.ToCell(MathUtils.Round(start.X), MathUtils.Round(start.Y),
                MathUtils.Round(start.Z));
            var dirty = new Dictionary<Point3, bool>();
            int appliedTick = 0;
            SuSubsystemTerrain subsystemTerrain = m_project?
                .FindSubsystem<SubsystemTerrain>(false) as SuSubsystemTerrain;
            foreach (MovingBlock block in set.Blocks)
            {
                Point3 cell = origin + block.Offset;
                if (!terrain.IsCellValid(cell.X, cell.Y, cell.Z)) continue;
                if (Terrain.ExtractContents(terrain.GetCellValue(cell.X, cell.Y, cell.Z)) !=
                    Terrain.ExtractContents(block.Value))
                    continue;
                // [SuAPI] 临时探针（验证后删除）
                ScMultiplayerOperationLog.Write("event=motion.shadow cell=" +
                    cell.X.ToString(CultureInfo.InvariantCulture) + "," +
                    cell.Y.ToString(CultureInfo.InvariantCulture) + "," +
                    cell.Z.ToString(CultureInfo.InvariantCulture) +
                    " contents=" + Terrain.ExtractContents(block.Value)
                        .ToString(CultureInfo.InvariantCulture));
                dirty[cell] = true;
                if (subsystemTerrain != null)
                    appliedTick = Math.Max(appliedTick,
                        subsystemTerrain.GetAppliedCellTick(cell));
                instance.RecordClientTerrainCellMissing(cell, 0L, "motionshadow");
            }
            if (dirty.Count == 0) return;
            // 主机把这些方块拾取进组时已经把它们从地形里清掉了（只是"清空"那条消息还没到）。
            // 这里按"网络应用"的口径立刻本地清一次（tick 用这些格已应用过的最大值，主机随后的
            // 更新 tick 更大仍能覆盖），原件位置就不再同时画一份地形副本 —— 那个"空壳影子"。
            var values = new List<int>();
            foreach (Point3 cell in dirty.Keys)
                values.Add(0);
            SuSubsystemTerrain.EnqueuePriorityNetworkBatch(new GameModifiedCellsMessage(
                dirty, values, appliedTick, isCatchUp: true,
                ScMultiplayer.client?.ClientID ?? 0, 0L));
        }

        // Source: Survivalcraft/Game/SubsystemMovingBlocks.cs:SubsystemMovingBlocks.Update
        // 客户端这份组按主机的 Speed/Acceleration/Drag 自己积分（60 帧，最顺），只在收到主机
        // 采样时做一次位置校正。校正原则（与木栅栏门那类"两个写入者互推同一份状态"同源）：
        //   · 前进采样一律采纳；
        //   · **很小的**后退当作抖动丢掉（避免反复闪烁）；
        //   · **明显的**后退必须采纳 —— 那是主机的决定（被沙子挡住提前停 / 换腿），
        //     继续"只许前进"会让客户端冲过主机停下的位置，看起来就是穿过沙子。
        private const float HostMotionJitterTolerance = 0.2f;

        private static bool TryAdoptHostMotionSample(IMovingBlockSet set,
            PistonMotionRecord record, Type type, out Vector3 adoptedPosition)
        {
            adoptedPosition = record.Position;
            // 主机采样已经落在它的目标点上：说明这一腿结束（引擎还会把位置吸附到整格）。
            // 这时必须原样采纳，不能用抖动过滤挡住 —— 否则我们的位置停在积分出来的小数点上，
            // 组被移除、方块改成地形时就会"向上跳一下"（换向时实测到的那个跳 + 小闪）。
            if (Vector3.DistanceSquared(record.Position, record.TargetPosition) <= 0.0004f)
                return Vector3.DistanceSquared(set.Position, record.Position) > 0.000001f;
            Vector3 previousStart = GetField(set, "StartPosition", set.Position, type);
            Vector3 previousTarget = GetField(set, "TargetPosition", Vector3.Zero, type);
            bool sameLeg =
                Vector3.DistanceSquared(previousStart, record.StartPosition) <= 0.01f &&
                Vector3.DistanceSquared(previousTarget, record.TargetPosition) <= 0.01f;
            if (!sameLeg)
                return Vector3.DistanceSquared(set.Position, record.Position) > 0.01f;
            Vector3 leg = record.TargetPosition - record.StartPosition;
            float lengthSquared = leg.LengthSquared();
            if (lengthSquared <= 0.0001f)
                return Vector3.DistanceSquared(set.Position, record.Position) > 0.01f;
            float currentAlong = Vector3.Dot(set.Position - record.StartPosition, leg) / lengthSquared;
            float sampleAlong = Vector3.Dot(record.Position - record.StartPosition, leg) / lengthSquared;
            float ahead = sampleAlong - currentAlong;
            if (ahead < 0f && -ahead <= HostMotionJitterTolerance)
                return false;
            return Vector3.DistanceSquared(set.Position, record.Position) > 0.01f;
        }

        // 主机那份组的方块列表会在一条腿里变（活塞拾取/放下被推方块、沙子加入/离开）：
        // 我们对已存在的组过去只更新 Start/Target/Speed 等参数，Blocks 一直是最初创建时那一份，
        // 于是多画/少画一格（实测"活塞推出去后沙子高出一格，换向时才消失"）。列表有差异就重建。
        private static bool HostMotionBlocksDiffer(IMovingBlockSet set, PistonMotionRecord record)
        {
            if (set.Blocks.Count != record.Blocks.Count) return true;
            int index = 0;
            foreach (MovingBlock block in set.Blocks)
            {
                MovingBlock other = record.Blocks[index++];
                if (block.Offset != other.Offset || block.Value != other.Value)
                    return true;
            }
            return false;
        }

        // 主机那份组的方块列表会在一条腿里变（活塞拾取/放下被推方块、沙子加入/离开）。
        // 必须同步，但**不能删除重建**：重建那一帧整组方块是缺席的，实测表现为"推出去时
        // 整叠时不时闪一下"、"第一个沙子像是被穿过去、到位后才挪过去"。这里原地改列表，
        // 再把局部包围盒与几何重生标记一起刷新（照 SubsystemMovingBlocks.UpdateBox 的做法）。
        private static void UpdateHostMotionBlocks(IMovingBlockSet set,
            PistonMotionRecord record, Type type)
        {
            if (GetField<object>(set, "Blocks", null, type) is List<MovingBlock> blocks)
            {
                // [SuAPI] 临时探针（验证后删除）
                ScMultiplayerOperationLog.Write("event=motion.blocks id=" + set.Id +
                    " was=" + blocks.Count.ToString(CultureInfo.InvariantCulture) +
                    " now=" + record.Blocks.Count.ToString(CultureInfo.InvariantCulture));
                blocks.Clear();
                blocks.AddRange(record.Blocks);
            }
            Point3? min = null;
            Point3? max = null;
            foreach (MovingBlock block in record.Blocks)
            {
                min = min.HasValue ? Point3.Min(min.Value, block.Offset) : block.Offset;
                max = max.HasValue ? Point3.Max(max.Value, block.Offset) : block.Offset;
            }
            if (min.HasValue && max.HasValue)
            {
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set, "Box",
                    new Box(min.Value.X, min.Value.Y, min.Value.Z,
                        max.Value.X - min.Value.X + 1, max.Value.Y - min.Value.Y + 1,
                        max.Value.Z - min.Value.Z + 1), type);
            }
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                "GeometryGenerationPosition", new Point3(int.MaxValue), type);
        }

        // 客户端这份组按主机参数自己积分；这里只写运动参数，位置交给上面的守卫校正。
        private static void WriteHostMotionSetFields(IMovingBlockSet set,
            PistonMotionRecord record)
        {
            Type type = set.GetType();
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

        // [SuAPI] 临时探针（跨端移动组定位用，验证后删除）：主机侧每秒 dump 一次活塞/塌落组，
        // 并给出"这些方块是否同时也已经写进地形"——用来判断组是不是卡住没结束（组与地形同时存在
        // ⇒ 玩家站上去等于站在移动方块上，且客户端会一直 defer）。
        private void ProbeHostMotionSets(double now)
        {
            if (now < m_nextHostMotionProbeTime) return;
            m_nextHostMotionProbeTime = now + 1.0;
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            Terrain terrain = m_project?
                .FindSubsystem<SubsystemTerrain>(false)?.Terrain;
            if (moving == null || terrain == null) return;
            foreach (IMovingBlockSet set in moving.MovingBlockSets)
            {
                if (set == null) continue;
                if (set.Id != "Piston" && set.Id != "CollapsingBlock") continue;
                Type type = set.GetType();
                // 引擎自己建的塌落组 Tag 是 null，按 StartPosition 所在格作键（与发布口径一致）。
                Point3 key = set.Tag is Point3 tagged
                    ? tagged
                    : Terrain.ToCell(MathUtils.Round(
                        GetField(set, "StartPosition", set.Position, type).X),
                        MathUtils.Round(GetField(set, "StartPosition", set.Position, type).Y),
                        MathUtils.Round(GetField(set, "StartPosition", set.Position, type).Z));
                Point3 origin = Terrain.ToCell(MathUtils.Round(set.Position.X),
                    MathUtils.Round(set.Position.Y), MathUtils.Round(set.Position.Z));
                int same = 0;
                foreach (MovingBlock block in set.Blocks)
                {
                    Point3 cell = origin + block.Offset;
                    if (terrain.IsCellValid(cell.X, cell.Y, cell.Z) &&
                        Terrain.ExtractContents(terrain.GetCellValue(cell.X, cell.Y, cell.Z)) ==
                        Terrain.ExtractContents(block.Value))
                        same++;
                }
                ScMultiplayerOperationLog.Write("event=motion.host id=" + set.Id +
                    " tag=" + key.X.ToString(CultureInfo.InvariantCulture) + "," +
                    key.Y.ToString(CultureInfo.InvariantCulture) + "," +
                    key.Z.ToString(CultureInfo.InvariantCulture) +
                    " pos=" + set.Position.X.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                    set.Position.Y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                    set.Position.Z.ToString("0.###", CultureInfo.InvariantCulture) +
                    " target=" + GetField(set, "TargetPosition", Vector3.Zero, type)
                        .Z.ToString("0.###", CultureInfo.InvariantCulture) +
                    " speed=" + GetField(set, "Speed", 0f, type)
                        .ToString("0.###", CultureInfo.InvariantCulture) +
                    " blocks=" + set.Blocks.Count.ToString(CultureInfo.InvariantCulture) +
                    " sameInTerrain=" + same.ToString(CultureInfo.InvariantCulture));
            }
        }

        // Source: Survivalcraft/Game/SubsystemMovingBlocks.cs:SubsystemMovingBlocks.Update
        // 兜底：我们建的每个组都要由主机的快照活着。主机只在该组出现/消失/运动中发布，
        // 若"组已结束"的那条空快照丢包，客户端就永远收不到收尾信号；而客户端又摘掉了
        // Stopped（不本地落地），于是组会停在原地变成幽灵（看得见、踩得住、挖不动）。
        // 所以超过 HostMotionSetMaxSilence 没被主机snapshot 提到的组，一律移除。
        private void ExpireStaleHostMotionSets()
        {
            if (m_hostMotionSets.Count == 0) return;
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            double now = Time.RealTime;
            foreach (IMovingBlockSet set in m_hostMotionSets.ToArray())
            {
                if (set == null) continue;
                if (!(set.Tag is Point3 point)) continue;
                string key = HostMotionKey(set.Id, point);
                if (m_hostMotionSetSeen.TryGetValue(key, out double seen) &&
                    now - seen <= HostMotionSetMaxSilence)
                {
                    // 主机快照里还在：撤销任何待移除计时。
                    m_hostMotionRemovalDue.Remove(set);
                    continue;
                }
                // 主机快照里已经不在了（这一腿结束、它已把方块落成地形）。先给一小段宽限，
                // 等权威地形格到达再移除 —— 否则组和地形之间会出现一小段"谁都没画"的空档，
                // 也就是推出去/换向时的小闪。
                if (!m_hostMotionRemovalDue.TryGetValue(set, out double due))
                {
                    m_hostMotionRemovalDue[set] = now + HostMotionRemovalGrace;
                    continue;
                }
                if (now < due) continue;
                m_hostMotionRemovalDue.Remove(set);
                // [SuAPI] 临时探针（验证后删除）
                ScMultiplayerOperationLog.Write("event=motion.expire id=" + set.Id +
                    " tag=" + point.X.ToString(CultureInfo.InvariantCulture) + "," +
                    point.Y.ToString(CultureInfo.InvariantCulture) + "," +
                    point.Z.ToString(CultureInfo.InvariantCulture));
                RequestHostMotionCellRepair(set);
                moving.RemoveMovingBlockSet(set);
                m_hostMotionSets.Remove(set);
                m_hostMotionSetSeen.Remove(key);
            }
        }

        // [SuAPI] 临时探针（跨端移动组定位用，验证后删除）：客户端每秒 dump 一次本端所有
        // 移动组（含引擎自建的 Tag=null 组、以及我们建的那些），用来钉死"空壳影子"到底是
        // 残留组还是地形副本。
        private void ProbeClientMotionSets(double now)
        {
            if (now < m_nextClientMotionProbeTime) return;
            m_nextClientMotionProbeTime = now + 1.0;
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            if (moving.MovingBlockSets.Count == 0) return;
            foreach (IMovingBlockSet set in moving.MovingBlockSets)
            {
                if (set == null) continue;
                if (set.Id != "Piston" && set.Id != "CollapsingBlock") continue;
                Type type = set.GetType();
                Vector3 start = GetField(set, "StartPosition", set.Position, type);
                ScMultiplayerOperationLog.Write("event=motion.client id=" + set.Id +
                    " owned=" + m_hostMotionSets.Contains(set).ToString() +
                    " tag=" + (set.Tag is Point3 point
                        ? point.X.ToString(CultureInfo.InvariantCulture) + "," +
                          point.Y.ToString(CultureInfo.InvariantCulture) + "," +
                          point.Z.ToString(CultureInfo.InvariantCulture)
                        : "none") +
                    " start=" + start.X.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                    start.Y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                    start.Z.ToString("0.###", CultureInfo.InvariantCulture) +
                    " pos=" + set.Position.X.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                    set.Position.Y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                    set.Position.Z.ToString("0.###", CultureInfo.InvariantCulture) +
                    " blocks=" + set.Blocks.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void FlushPendingHostMotionSets()
        {
            if (m_pendingHostMotionSets.Count == 0) return;
            double now = Time.RealTime;
            foreach (KeyValuePair<string, PendingHostMotionSet> item in
                m_pendingHostMotionSets.ToArray())
            {
                PendingHostMotionSet pending = item.Value;
                bool clear = AreHostMotionCellsClear(pending.Record);
                if (!clear && now < pending.Deadline) continue;
                m_pendingHostMotionSets.Remove(item.Key);
                if (!clear && now - pending.RecordedTime > HostMotionSetMaxStaleAge)
                {
                    // 记录已经过期（主机那条组多半早就结束了，只是它的空快照丢了）：
                    // 放弃建组，别造孤儿。
                    // [SuAPI] 临时探针（验证后删除）
                    ScMultiplayerOperationLog.Write("event=motion.defer.drop id=" + pending.Id +
                        " cell=" + pending.Record.Point.X.ToString(CultureInfo.InvariantCulture) +
                        "," + pending.Record.Point.Y.ToString(CultureInfo.InvariantCulture) +
                        "," + pending.Record.Point.Z.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
                if (!clear)
                {
                    // [SuAPI] 临时探针（验证后删除）
                    ScMultiplayerOperationLog.Write("event=motion.defer.expire id=" + pending.Id +
                        " cell=" + pending.Record.Point.X.ToString(CultureInfo.InvariantCulture) +
                        "," + pending.Record.Point.Y.ToString(CultureInfo.InvariantCulture) +
                        "," + pending.Record.Point.Z.ToString(CultureInfo.InvariantCulture));
                }
                if (pending.Id == "Piston")
                    ApplyPistonRecord(pending.Record, force: true);
                else
                    ApplyCollapsingRecord(pending.Record, force: true);
            }
        }

        private void DropPendingMotionSets(string id, HashSet<Point3> authoritative)
        {
            if (m_pendingHostMotionSets.Count == 0) return;
            foreach (KeyValuePair<string, PendingHostMotionSet> item in
                m_pendingHostMotionSets.ToArray())
            {
                if (item.Value.Id != id) continue;
                if (!authoritative.Contains(item.Value.Record.Point))
                    m_pendingHostMotionSets.Remove(item.Key);
            }
        }

        // Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs:TryCollapseColumn
        // 主机把正在下落的塌落组按 StartPosition 所在格发布；客户端只做表现
        // （testCollision:false，不写地形），落地仍由主机的地形消息决定。
        private void PublishCollapsingSets(bool force)
        {
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            List<IMovingBlockSet> collapsingSets = moving.MovingBlockSets.Where(item =>
                item.Id == "CollapsingBlock").ToList();
            if (collapsingSets.Count == 0 && m_lastCollapsingMotionSequence == 0)
                return;
            var keys = new HashSet<string>(collapsingSets.Select(item => HostMotionKey(
                "CollapsingBlock", Terrain.ToCell(MathUtils.Round(
                    GetField(item, "StartPosition", item.Position, item.GetType()).X),
                    MathUtils.Round(GetField(item, "StartPosition", item.Position,
                        item.GetType()).Y),
                    MathUtils.Round(GetField(item, "StartPosition", item.Position,
                        item.GetType()).Z)))));
            bool changed = !keys.SetEquals(m_publishedCollapsingKeys);
            if (!changed && !force) return;
            m_publishedCollapsingKeys = keys;
            m_collapsingMotionSequence = m_collapsingMotionSequence == int.MaxValue
                ? 1 : m_collapsingMotionSequence + 1;
            var message = new WorldObjectSyncMessage
            {
                Stage = WorldObjectSyncStage.CollapsingBatch,
                MotionSequence = m_collapsingMotionSequence,
                IsComplete = collapsingSets.Count <= 64
            };
            foreach (IMovingBlockSet set in collapsingSets.Take(64))
            {
                Type type = set.GetType();
                Vector3 startPosition = GetField(set, "StartPosition", set.Position, type);
                Point3 key = Terrain.ToCell(MathUtils.Round(startPosition.X),
                    MathUtils.Round(startPosition.Y), MathUtils.Round(startPosition.Z));
                var record = new PistonMotionRecord
                {
                    Point = key,
                    StartPosition = startPosition,
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

        private void ApplyCollapsingSets(List<PistonMotionRecord> records,
            int motionSequence, bool isComplete)
        {
            if (motionSequence <= m_lastCollapsingMotionSequence) return;
            m_lastCollapsingMotionSequence = motionSequence;
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            var authoritative = new HashSet<Point3>(records.Select(item => item.Point));
            if (isComplete)
                foreach (IMovingBlockSet stale in moving.MovingBlockSets.Where(item =>
                    item.Id == "CollapsingBlock" && item.Tag is Point3 point &&
                    !authoritative.Contains(point)).ToArray())
                    moving.RemoveMovingBlockSet(stale);
            foreach (PistonMotionRecord record in records)
            {
                m_hostMotionSetSeen[HostMotionKey("CollapsingBlock", record.Point)] =
                    Time.RealTime;
                ApplyCollapsingRecord(record, force: false);
            }
            DropPendingMotionSets("CollapsingBlock", authoritative);
        }

        private void ApplyCollapsingRecord(PistonMotionRecord record, bool force)
        {
            SubsystemMovingBlocks moving = m_project?
                .FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null) return;
            IMovingBlockSet set = moving.MovingBlockSets.FirstOrDefault(item =>
                item.Id == "CollapsingBlock" && item.Tag is Point3 point &&
                point == record.Point);
            if (set != null && !m_hostMotionSets.Contains(set))
            {
                RequestHostMotionCellRepair(set);
                moving.RemoveMovingBlockSet(set);
                set = null;
            }
            if (set != null && HostMotionBlocksDiffer(set, record))
            {
                UpdateHostMotionBlocks(set, record, set.GetType());
                RequestHostMotionCellRepair(set);
            }
            if (set == null)
            {
                if (!force && !TryScheduleHostMotionSet("CollapsingBlock", record))
                    return;
                // 放置者本地已经有一份"自己预测"的塌落组（Tag 为 null）：主机权威组到达后
                // 丢掉本地那份，避免同一次塌落被两个写入者各画一遍。
                foreach (IMovingBlockSet local in moving.MovingBlockSets
                    .Where(item => item.Id == "CollapsingBlock" && !(item.Tag is Point3))
                    .ToArray())
                {
                    Type localType = local.GetType();
                    Vector3 localStart = GetField(local, "StartPosition", local.Position,
                        localType);
                    Point3 localCell = Terrain.ToCell(MathUtils.Round(localStart.X),
                        MathUtils.Round(localStart.Y), MathUtils.Round(localStart.Z));
                    if (localCell.X == record.Point.X && localCell.Z == record.Point.Z)
                        moving.RemoveMovingBlockSet(local);
                }
                IMovingBlockSet added = moving.AddMovingBlockSet(record.Position,
                    record.TargetPosition,
                    record.Speed, record.Acceleration, record.Drag, record.Smoothness,
                    record.Blocks, "CollapsingBlock", record.Point, testCollision: false);
                if (added != null)
                {
                    m_hostMotionSets.Add(added);
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(added,
                        "StartPosition", record.StartPosition, added.GetType());
                    VerifyHostMotionOriginCells(added);
                }
                return;
            }
            Type type = set.GetType();
            if (TryAdoptHostMotionSample(set, record, type, out Vector3 adoptedPosition))
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(set,
                    "Position", adoptedPosition, type);
            WriteHostMotionSetFields(set, record);
        }

        // [SuAPI] 临时探针（跨端移动组定位用，验证后删除）：客户端也会跑自己的活塞/塌落逻辑，
        // 这里记录每个组的"撞到地形/停下"现场，用来判断某次停/回退是主机的决定还是本端引擎自己做的。
        private void EnsureHostMotionProbe(Project project)
        {
            SubsystemMovingBlocks moving = project?.FindSubsystem<SubsystemMovingBlocks>(false);
            if (moving == null || ReferenceEquals(moving, m_probeMovingBlocks)) return;
            if (m_probeMovingBlocks != null)
            {
                m_probeMovingBlocks.Stopped -= ProbeHostMotionStopped;
                m_probeMovingBlocks.CollidedWithTerrain -= ProbeHostMotionCollision;
            }
            m_probeMovingBlocks = moving;
            m_probeMovingBlocks.Stopped += ProbeHostMotionStopped;
            m_probeMovingBlocks.CollidedWithTerrain += ProbeHostMotionCollision;
        }

        // [SuAPI] 临时探针（跨端移动组定位用，验证后删除）：本端收到的移动组快照。
        private static void ProbeHostMotionBatch(string id, List<PistonMotionRecord> records,
            int sequence)
        {
            if (records == null || records.Count == 0) return;
            var parts = new List<string>();
            foreach (PistonMotionRecord record in records.Take(4))
                parts.Add(record.Point.X.ToString(CultureInfo.InvariantCulture) + "," +
                    record.Point.Y.ToString(CultureInfo.InvariantCulture) + "," +
                    record.Point.Z.ToString(CultureInfo.InvariantCulture) + "@" +
                    record.Position.X.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                    record.Position.Y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                    record.Position.Z.ToString("0.###", CultureInfo.InvariantCulture));
            ScMultiplayerOperationLog.Write("event=motion.recv id=" + id +
                " seq=" + sequence.ToString(CultureInfo.InvariantCulture) +
                " count=" + records.Count.ToString(CultureInfo.InvariantCulture) +
                " sample=" + string.Join(";", parts));
        }

        // [SuAPI] 临时探针（验证后删除）：这两个事件是**每帧级**的（实测单段 2.6 万次碰撞），
        // 全部写日志本身就会造成可见卡顿，所以按"每秒最多 4 条"限流。
        private static double s_motionProbeWindowTime;
        private static int s_motionProbeLinesInWindow;
        private static bool ShouldWriteMotionProbe()
        {
            double now = Time.RealTime;
            if (now >= s_motionProbeWindowTime)
            {
                s_motionProbeWindowTime = now + 1.0;
                s_motionProbeLinesInWindow = 0;
            }
            if (s_motionProbeLinesInWindow >= 4) return false;
            s_motionProbeLinesInWindow++;
            return true;
        }

        private static void ProbeHostMotionStopped(IMovingBlockSet set)
        {
            if (set == null) return;
            if (!ShouldWriteMotionProbe()) return;
            ScMultiplayerOperationLog.Write("event=motion.stop id=" + set.Id +
                " tag=" + (set.Tag is Point3 point
                    ? point.X.ToString(CultureInfo.InvariantCulture) + "," +
                      point.Y.ToString(CultureInfo.InvariantCulture) + "," +
                      point.Z.ToString(CultureInfo.InvariantCulture)
                    : "none") +
                " pos=" + set.Position.X.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                set.Position.Y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                set.Position.Z.ToString("0.###", CultureInfo.InvariantCulture) +
                " blocks=" + set.Blocks.Count.ToString(CultureInfo.InvariantCulture));
        }

        private static void ProbeHostMotionCollision(IMovingBlockSet set, Point3 cell)
        {
            if (set == null) return;
            if (!ShouldWriteMotionProbe()) return;
            ScMultiplayerOperationLog.Write("event=motion.collide id=" + set.Id +
                " cell=" + cell.X.ToString(CultureInfo.InvariantCulture) + "," +
                cell.Y.ToString(CultureInfo.InvariantCulture) + "," +
                cell.Z.ToString(CultureInfo.InvariantCulture) +
                " pos=" + set.Position.X.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                set.Position.Y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                set.Position.Z.ToString("0.###", CultureInfo.InvariantCulture));
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




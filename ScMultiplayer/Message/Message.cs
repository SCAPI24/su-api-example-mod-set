using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public abstract class Message
    {
        public const string ModVersion = "1.9.22";
        public const int ProtocolVersion = 1;

        private static readonly Dictionary<int, Type> MessageTypesById = new();
        private static readonly Dictionary<Type, int> MessageIdsByType = new();

        private static readonly Dictionary<Type, string> MessageNamesByType = new();
        private static readonly List<string> MessageWireSignatures = new();

        public static string ProtocolHash { get; }
        public static string BuildFingerprint { get; }

        /// <summary>
        /// 消息发送者的终端地址
        /// </summary>
        public IPEndPoint SenderEndPoint { get; set; }

        static Message()
        {
            // Source: Mod/ScMultiplayer/Message/Message.cs:Message.Message
            // Wire IDs must never depend on reflection order, type names or obfuscation.
            // IDs are append-only. Increment a message revision whenever its wire schema changes.
            Register<ChatMessage>(0, nameof(ChatMessage), 1);
            Register<CircuitSyncMessage>(1, nameof(CircuitSyncMessage), 5);
            Register<EditableDataRequestMessage>(2, nameof(EditableDataRequestMessage), 1);
            Register<EditableDataStateMessage>(3, nameof(EditableDataStateMessage), 1);
            Register<GameKickPlayerMessage>(4, nameof(GameKickPlayerMessage), 1);
            Register<GameModifiedCellsMessage>(5, nameof(GameModifiedCellsMessage), 2);
            Register<GamePakWorldMessage>(6, nameof(GamePakWorldMessage), 2);
            Register<GamePlayerHealthMessage>(7, nameof(GamePlayerHealthMessage), 1);
            Register<GamePlayerInputMessage>(8, nameof(GamePlayerInputMessage), 1);
            Register<GamePlayerPositionMessage>(9, nameof(GamePlayerPositionMessage), 1);
            Register<GamePlayerPositionsMessage>(10, nameof(GamePlayerPositionsMessage), 1);
            Register<GameWorldInfoMessage>(11, nameof(GameWorldInfoMessage), 2);
            Register<GameWorldInfoMessage1>(12, nameof(GameWorldInfoMessage1), 3);
            Register<PlayerAimMessage>(13, nameof(PlayerAimMessage), 1);
            Register<SyncBatchMessage>(14, nameof(SyncBatchMessage), 1);
            Register<TerrainDigRequestMessage>(15, nameof(TerrainDigRequestMessage), 1);
            Register<TerrainDigResultMessage>(16, nameof(TerrainDigResultMessage), 1);
            Register<WorldControlRequestMessage>(17, nameof(WorldControlRequestMessage), 2);
            Register<AnimalInteractionMessage>(18, nameof(AnimalInteractionMessage), 1);
            Register<BodyUpdateMessage>(19, nameof(BodyUpdateMessage), 1);
            Register<ContainerSyncMessage>(20, nameof(ContainerSyncMessage), 2);
            Register<EntityMessage>(21, nameof(EntityMessage), 1);
            Register<ExplosionSyncMessage>(22, nameof(ExplosionSyncMessage), 1);
            Register<GamePakWorldChunkMessage>(23, nameof(GamePakWorldChunkMessage), 1);
            Register<GamePakWorldReadyMessage>(24, nameof(GamePakWorldReadyMessage), 1);
            Register<GamePakWorldRepairRequestMessage>(25,
                nameof(GamePakWorldRepairRequestMessage), 1);
            Register<PickableSyncMessage>(26, nameof(PickableSyncMessage), 2);
            Register<PlayerActionMessage>(27, nameof(PlayerActionMessage), 4);
            Register<PlayerDataSyncMessage>(28, nameof(PlayerDataSyncMessage), 1);
            Register<PlayerEquipmentMessage>(29, nameof(PlayerEquipmentMessage), 1);
            Register<PlayerProfileMessage>(30, nameof(PlayerProfileMessage), 2);
            Register<ProjectileSyncMessage>(31, nameof(ProjectileSyncMessage), 1);
            Register<TerrainRecoveryMessage>(32, nameof(TerrainRecoveryMessage), 1);
            Register<WorldObjectSyncMessage>(33, nameof(WorldObjectSyncMessage), 1);
            Register<WorldControlResultMessage>(34, nameof(WorldControlResultMessage), 1);
            Register<PlayerSkinAssetMessage>(35, nameof(PlayerSkinAssetMessage), 2);
            Register<TerrainChunkSyncMessage>(36, nameof(TerrainChunkSyncMessage), 1);
            Register<DigPresentationMessage>(37, nameof(DigPresentationMessage), 1);
            Register<MeleeHitResultMessage>(38, nameof(MeleeHitResultMessage), 1);
            Register<AnimalSoundMessage>(39, nameof(AnimalSoundMessage), 1);

            foreach (TypeInfo typeInfo in typeof(Message).Assembly.DefinedTypes)
            {
                Type type = typeInfo.AsType();
                if (typeof(Message).IsAssignableFrom(type) && !typeInfo.IsAbstract &&
                    !MessageIdsByType.ContainsKey(type))
                    throw new InvalidOperationException(
                        $"Message type has no explicit wire ID: {type.FullName}");
            }

            string manifest = $"SCMP-WIRE|{ModVersion}|{ProtocolVersion}|" +
                string.Join("|", MessageWireSignatures);
            ProtocolHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
            // Source: System.Reflection.Module:ModuleVersionId
            // The Mod is loaded from bytes on Android, so Assembly.Location cannot provide a
            // portable DLL hash. The final module MVID identifies the exact ScMultiplayer build.
            BuildFingerprint = Convert.ToHexString(SHA256.HashData(
                typeof(Message).Assembly.ManifestModule.ModuleVersionId.ToByteArray()))
                .ToLowerInvariant();
        }

        private static void Register<T>(int id, string wireName, int wireRevision)
            where T : Message
        {
            Type type = typeof(T);
            if (id != MessageWireSignatures.Count || MessageTypesById.ContainsKey(id) ||
                MessageIdsByType.ContainsKey(type))
                throw new InvalidOperationException($"Invalid message wire registration: {id}/{wireName}");
            MessageTypesById.Add(id, type);
            MessageIdsByType.Add(type, id);
            MessageNamesByType.Add(type, wireName);
            MessageWireSignatures.Add($"{id}:{wireName}:{wireRevision}");
        }

        public static bool IsProtocolCompatible(string modVersion, int version, string hash,
            string buildFingerprint)
        {
            return string.Equals(modVersion, ModVersion, StringComparison.Ordinal) &&
                version == ProtocolVersion &&
                string.Equals(hash, ProtocolHash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(buildFingerprint, BuildFingerprint,
                    StringComparison.OrdinalIgnoreCase);
        }

        public static string GetProtocolLabel(string modVersion, int version, string hash,
            string buildFingerprint)
        {
            string normalizedVersion = string.IsNullOrWhiteSpace(modVersion)
                ? "unknown"
                : modVersion.Trim();
            string normalizedHash = string.IsNullOrWhiteSpace(hash) ? "missing" : hash.Trim();
            if (normalizedHash.Length > 12) normalizedHash = normalizedHash.Substring(0, 12);
            string normalizedBuild = string.IsNullOrWhiteSpace(buildFingerprint)
                ? "missing"
                : buildFingerprint.Trim();
            if (normalizedBuild.Length > 12)
                normalizedBuild = normalizedBuild.Substring(0, 12);
            return $"mod {normalizedVersion}, protocol v{version}/{normalizedHash}, " +
                $"build {normalizedBuild}";
        }

        public static Message Read(byte[] bytes, IPEndPoint senderEndPoint = null)
        {
            SuReader reader = new SuReader(bytes);

            int messageTypeId = reader.ReadPackedInt32();
            if (!MessageTypesById.TryGetValue(messageTypeId, out Type messageType))
            {
                throw new ProtocolViolationException($"Unknown message type ID: {messageTypeId}");
            }

            Message message = (Message)Activator.CreateInstance(messageType);

            // 从数据流中读取发送者信息
            bool hasSender = reader.ReadBoolean();
            if (hasSender)
            {
                message.SenderEndPoint = reader.ReadIPEndPoint();
            }
            else
            {
                // 如果数据流中没有发送者信息，使用传入的发送者信息
                message.SenderEndPoint = senderEndPoint;
            }

            message.Read(reader);
            return message;
        }

        public static byte[] Write(Message message, IPEndPoint senderEndPoint = null)
        {
            SuWriter writer = new SuWriter();

            if (!MessageIdsByType.TryGetValue(message.GetType(), out int messageTypeId))
            {
                throw new InvalidOperationException($"Unregistered message type: {message.GetType()}");
            }

            writer.WritePackedInt32(messageTypeId);

            // 序列化发送者信息
            bool hasSenderToSerialize = message.SenderEndPoint != null || senderEndPoint != null;
            writer.WriteBoolean(hasSenderToSerialize);

            if (hasSenderToSerialize)
            {
                // 优先使用消息中已有的发送者信息，否则使用传入的发送者信息
                IPEndPoint endPointToSerialize = message.SenderEndPoint ?? senderEndPoint;
                writer.WriteIPEndPoint(endPointToSerialize);
            }

            message.Write(writer);
            return writer.GetBytes();
        }

        /// <summary>
        /// 便捷方法：写入消息并记录发送者
        /// </summary>
        public static byte[] WriteWithSender(Message message, IPEndPoint senderEndPoint)
        {
            // 设置发送者信息后序列化
            message.SenderEndPoint = senderEndPoint;
            return Write(message, null); // 传入null，因为发送者信息已经在message中
        }

        // Source: Mod/ScMultiplayer/Message/Message.cs:Message.Read
        // This runs on the game frame after Comms has queued a resend event. It deliberately
        // records identifiers and sizes only; packet contents and skin/world bytes are omitted.
        public static string DescribeRetransmission(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "content=empty";

            try
            {
                Message message = Read(bytes);
                string name = MessageNamesByType.TryGetValue(message.GetType(), out string wireName)
                    ? wireName
                    : message.GetType().Name;
                StringBuilder details = new StringBuilder("content=").Append(name);
                switch (message)
                {
                    case CircuitSyncMessage circuit:
                        details.Append(" stage=").Append(circuit.Stage)
                            .Append(" requestId=").Append(circuit.RequestId)
                            .Append(" epoch=").Append(circuit.Epoch)
                            .Append(" baseSequence=").Append(circuit.BaseSequence)
                            .Append(" lastSequence=").Append(circuit.LastSequence)
                            .Append(" expectedSequence=").Append(circuit.ExpectedSequence)
                            .Append(" events=").Append(circuit.Events?.Count ?? 0)
                            .Append(" states=").Append(circuit.States?.Count ?? 0);
                        break;
                    case ContainerSyncMessage container:
                        details.Append(" request=").Append(container.IsRequest)
                            .Append(" requestId=").Append(container.RequestId)
                            .Append(" revision=").Append(container.Revision)
                            .Append(" playerRevision=").Append(container.PlayerRevision)
                            .Append(" component=").Append(SanitizeDiagnosticValue(container.ComponentType));
                        break;
                    case PickableSyncMessage pickable:
                        details.Append(" action=").Append(pickable.Action)
                            .Append(" id=").Append(pickable.Id)
                            .Append(" requestId=").Append(pickable.RequestId)
                            .Append(" collector=").Append(pickable.CollectorClientId)
                            .Append(" tick=").Append(pickable.ServerTick);
                        break;
                    case GamePakWorldChunkMessage chunk:
                        details.Append(" transferId=").Append(chunk.TransferId)
                            .Append(" target=").Append(chunk.TargetClientId)
                            .Append(" chunk=").Append(chunk.ChunkIndex).Append('/').Append(chunk.ChunkCount)
                            .Append(" dataBytes=").Append(chunk.Data?.Length ?? 0);
                        break;
                    case TerrainChunkSyncMessage terrainChunk:
                        details.Append(" stage=").Append(terrainChunk.Stage)
                            .Append(" chunk=").Append(terrainChunk.ChunkX).Append(',').Append(terrainChunk.ChunkZ)
                            .Append(" revision=").Append(terrainChunk.Revision)
                            .Append(" known=").Append(terrainChunk.KnownRevision)
                            .Append(" cells=").Append(terrainChunk.Cells?.Count ?? 0);
                        break;
                    case TerrainRecoveryMessage recovery:
                        details.Append(" stage=").Append(recovery.Stage)
                            .Append(" applied=").Append(recovery.LastAppliedSequence)
                            .Append(" head=").Append(recovery.HeadSequence)
                            .Append(" ranges=").Append(recovery.BufferedRanges?.Count ?? 0)
                            .Append(" payloads=").Append(recovery.Payloads?.Count ?? 0);
                        break;
                    case GameModifiedCellsMessage modified:
                        details.Append(" sequence=").Append(modified.Sequence)
                            .Append(" head=").Append(modified.HeadSequence)
                            .Append(" tick=").Append(modified.Tick)
                            .Append(" cells=").Append(modified.ModifiedCells?.Count ?? 0)
                            .Append(" catchUp=").Append(modified.IsCatchUp)
                            .Append(" target=").Append(modified.TargetClientId);
                        break;
                    case PlayerActionMessage action:
                        details.Append(" action=").Append(action.Action)
                            .Append(" player=").Append(action.PlayerIndex)
                            .Append(" sequence=").Append(action.Sequence)
                            .Append(" requestId=").Append(action.RequestId)
                            .Append(" cell=").Append(action.Cell.X).Append(',')
                            .Append(action.Cell.Y).Append(',').Append(action.Cell.Z);
                        break;
                    case TerrainDigRequestMessage dig:
                        details.Append(" requestId=").Append(dig.RequestId)
                            .Append(" cell=").Append(dig.Cell.X).Append(',')
                            .Append(dig.Cell.Y).Append(',').Append(dig.Cell.Z)
                            .Append(" ticks=").Append(dig.StartClientTick).Append('-').Append(dig.CompletedClientTick);
                        break;
                    case PlayerSkinAssetMessage skin:
                        details.Append(" action=").Append(skin.Action)
                            .Append(" client=").Append(skin.ClientId)
                            .Append(" transferId=").Append(skin.TransferId)
                            .Append(" chunk=").Append(skin.ChunkIndex).Append('/').Append(skin.ChunkCount)
                            .Append(" dataBytes=").Append(skin.Data?.Length ?? 0)
                            .Append(" skin=").Append(SanitizeDiagnosticValue(skin.SkinName));
                        break;
                    case DigPresentationMessage presentation:
                        details.Append(" player=").Append(presentation.PlayerIndex)
                            .Append(" sequence=").Append(presentation.Sequence)
                            .Append(" active=").Append(presentation.IsActive)
                            .Append(" progress=").Append(presentation.Progress.ToString("0.###", CultureInfo.InvariantCulture));
                        break;
                    case SyncBatchMessage batch:
                        details.Append(" payloads=").Append(batch.Payloads?.Count ?? 0)
                            .Append(" bytes=").Append(batch.Payloads?.Sum(item => item?.Length ?? 0) ?? 0);
                        break;
                }

                return details.Length <= 640 ? details.ToString() : details.ToString(0, 640);
            }
            catch (Exception error)
            {
                return "content=decode-failed bytes=" + bytes.Length.ToString(CultureInfo.InvariantCulture) +
                    " error=" + SanitizeDiagnosticValue(error.GetType().Name);
            }
        }

        private static string SanitizeDiagnosticValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
            return normalized.Length <= 96 ? normalized : normalized.Substring(0, 96);
        }

        /// <summary>
        /// 便捷方法：读取消息并记录发送者
        /// </summary>
        public static Message ReadWithSender(byte[] bytes, IPEndPoint senderEndPoint)
        {
            return Read(bytes, senderEndPoint);
        }

        protected abstract void Read(SuReader reader);
        protected abstract void Write(SuWriter writer);

        /// <summary>
        /// 获取发送者的IP地址（如果存在）
        /// </summary>
        public IPAddress GetSenderAddress()
        {
            return SenderEndPoint?.Address;
        }

        /// <summary>
        /// 获取发送者的端口号（如果存在）
        /// </summary>
        public int? GetSenderPort()
        {
            return SenderEndPoint?.Port;
        }

        /// <summary>
        /// 检查消息是否有发送者信息
        /// </summary>
        public bool HasSender()
        {
            return SenderEndPoint != null;
        }

        /// <summary>
        /// 获取发送者的字符串表示
        /// </summary>
        public string GetSenderString()
        {
            return SenderEndPoint?.ToString() ?? "Unknown";
        }

        /// <summary>
        /// 设置发送者信息（链式调用）
        /// </summary>
        public Message SetSender(IPEndPoint senderEndPoint)
        {
            SenderEndPoint = senderEndPoint;
            return this;
        }

        /// <summary>
        /// 设置发送者信息（链式调用）
        /// </summary>
        public Message SetSender(IPAddress address, int port)
        {
            SenderEndPoint = new IPEndPoint(address, port);
            return this;
        }
    }
}

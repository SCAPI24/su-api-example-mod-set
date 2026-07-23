using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
    public enum NetworkSyncRate
    {
        Hz1 = 1,
        Hz2 = 2,
        Hz4 = 4,
        Hz8 = 8,
        Hz16 = 16,
        Hz32 = 32
    }

    // ================================================================
    // PlayerMappingManager / PlayerOperationSyncManager / NetworkMessageHandler
    // NetworkMessageSender 保持原样，不变
    // ================================================================
    #region Helpers

    public class PlayerMappingManager
    {
        private Dictionary<int, int> clientIdToPlayerIndex = new Dictionary<int, int>();
        private Dictionary<int, int> playerIndexToClientId = new Dictionary<int, int>();
        public int MaxPlayerIndices { get; set; } = 4;

        public int AssignPlayerIndex(int clientId)
        {
            if (clientIdToPlayerIndex.ContainsKey(clientId))
                return clientIdToPlayerIndex[clientId];
            for (int i = 0; i < MaxPlayerIndices; i++)
            {
                if (playerIndexToClientId.ContainsKey(i)) continue;
                clientIdToPlayerIndex[clientId] = i;
                playerIndexToClientId[i] = clientId;
                return i;
            }
            return -1;
        }

        public void ReleasePlayerIndex(int clientId)
        {
            if (clientIdToPlayerIndex.TryGetValue(clientId, out int pi))
            {
                clientIdToPlayerIndex.Remove(clientId);
                playerIndexToClientId.Remove(pi);
            }
        }

        public int GetPlayerIndex(int clientId) =>
            clientIdToPlayerIndex.TryGetValue(clientId, out int pi) ? pi : -1;

        public int GetClientId(int playerIndex) =>
            playerIndexToClientId.TryGetValue(playerIndex, out int cid) ? cid : -1;

        public List<int> GetAllPlayerIndices() => playerIndexToClientId.Keys.ToList();

        public List<int> GetAllClientIds() => clientIdToPlayerIndex.Keys.ToList();

        public void Reset()
        {
            clientIdToPlayerIndex.Clear();
            playerIndexToClientId.Clear();
        }
    }

    public class PlayerOperationSyncManager
    {
        public int ConvertPlayerIndexForClient(int sourcePlayerIndex, int targetClientId)
        {
            int sourceClientId = ScMultiplayer.playerMappingManager.GetClientId(sourcePlayerIndex);
            if (sourceClientId == -1) return -1;
            int targetPlayerIndex = ScMultiplayer.playerMappingManager.GetPlayerIndex(targetClientId);
            if (targetPlayerIndex == -1) return -1;
            return (sourcePlayerIndex - targetPlayerIndex + ScMultiplayer.playerMappingManager.MaxPlayerIndices)
                % ScMultiplayer.playerMappingManager.MaxPlayerIndices;
        }

        public int ConvertLocalPlayerIndexToNetwork(int localPlayerIndex, int localClientId)
        {
            int localClientPlayerIndex = ScMultiplayer.playerMappingManager.GetPlayerIndex(localClientId);
            if (localClientPlayerIndex == -1) return -1;
            return (localPlayerIndex - localClientPlayerIndex + ScMultiplayer.playerMappingManager.MaxPlayerIndices)
                % ScMultiplayer.playerMappingManager.MaxPlayerIndices;
        }
    }

    // ================================================================
    // NetworkPlayerState: 远程玩家状态快照
    // ================================================================
    public class NetworkPlayerState
    {
        public int ClientID;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector2 LookAngles;
        public Vector2? WalkOrder;
        public float JumpOrder;
        public float PokingPhase;
        public bool AttackOrder;
        public bool RowLeftOrder;
        public bool RowRightOrder;
        public bool IsCrouching;
        public bool IsFlying;
        public bool IsRiding;
        public bool IsGrounded;
        public int ActiveSlotIndex;
        public int HandItemValue;
        public int HandItemCount;
        public Vector3 ItemOffset;
        public Vector3 ItemRotation;
        public float AimHandAngle;
        public float Health;
        public float MaxHealth = 1f;
        public bool IsDead;
        public int ServerTick;
        public float EstimatedDelay;
        public bool PresentationInitialized;
        public double LastUpdateTime;
        public double LastPokeEventTime;
    }

    public class NetworkPlayerRecord
    {
        public string Name;
        public PlayerClass PlayerClass;
        public string SkinName;
        public Vector3 Position;
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
        public bool IsCreativeFlying;
        public bool HasReceivedInitialItems = true;
        public bool InventoryWasCreative;
        public int ActiveSlotIndex;
        public int CreativeCategoryIndex;
        public int CreativePageIndex;
        public int[] SlotValues;
        public int[] SlotCounts;
        public int[][] Clothes;
    }

    internal sealed class EquipmentSnapshot
    {
        public int ActiveSlotIndex;
        public int[] SlotValues = Array.Empty<int>();
        public int[] SlotCounts = Array.Empty<int>();
        public int[][] Clothes = CreateEmptyClothes();

        private static int[][] CreateEmptyClothes() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };
    }

    public class PendingJoinRequest
    {
        public IPEndPoint ServerAddress;
        public int GameId;
        public GameWorldInfoMessage WorldInfo;
    }

    internal sealed class HostJoinRequest
    {
        public int ClientId;
        public IPEndPoint Address;
        public string RecordKey;
        public NetworkPlayerRecord PlayerRecord;
        public bool IsNewApproval;
        public bool Deferred;
        public double ReceivedTime;
    }

    public class NetworkPlayerInputState
    {
        public PlayerInput Input;
        public PlayerInput HeldInput;
        public Quaternion BodyRotation;
        public Vector2 LookAngles;
        public Vector3 BodyPosition;
        public Vector3 BodyVelocity;
        public int ClientTick;
        public bool InitialPositionApplied;
        public int Sequence = -1;
        public int ConsumedSequence = -1;
        public double LastReceivedTime;
        public readonly Queue<PlayerAimMessage> AimEvents = new Queue<PlayerAimMessage>();
        public readonly HashSet<int> QueuedAimCompletions = new HashSet<int>();
        public int ActiveAimSequence = -1;
        public int LastCompletedAimSequence = -1;
        public Ray3? HeldAim;
        public int ActiveAimSlotIndex = -1;
        public int ActiveAimItemValue;
        public int ActiveAimItemCount;
        public readonly Queue<PlayerActionMessage> InteractEvents = new Queue<PlayerActionMessage>();
        public int LastInteractSequence;
        public double NextInteractExecutionTime;
        public readonly Queue<PlayerActionMessage> HitEvents = new Queue<PlayerActionMessage>();
        public int LastHitSequence;
        public double NextHitExecutionTime;
        public readonly Queue<PlayerActionMessage> DropEvents = new Queue<PlayerActionMessage>();
        public int LastDropSequence;
        public int LastRespawnSequence;
        public int LastAuthoritativeInventoryTick;
    }

    public class RemotePickableRecord
    {
        public int Value;
        public int Count;
        public Matrix? StuckMatrix;
    }

    public class RemotePickableNetworkState
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 PresentationVelocity;
        public Vector3? FlyToPosition;
        public double LastUpdateTime;
        public bool PresentationInitialized;
    }

    public class PendingPickablePickupPresentation
    {
        public int CollectorClientId;
        public int RemainingCount;
        public double CompleteTime;
        public bool PlaySound;
    }

    public class ContainerNetworkState
    {
        public int Revision;
        public int[] Values = Array.Empty<int>();
        public int[] Counts = Array.Empty<int>();
    }

    public class TerrainCellState
    {
        public bool IsModified;
        public int CellValue;
        public int Tick;
    }

    public class TerrainJournalEntry
    {
        public long Sequence;
        public int ServerStep;
        public double CreatedTime;
        public byte[] Payload;
    }

    public class PendingTerrainPrediction
    {
        public TerrainDigRequestMessage Request;
        public double LastSendTime;
        public int SendCount;
        public TerrainDigResultMessage Result;
        public double ReconcileTime;
    }

    public class LocalTerrainDigIntent
    {
        public int ExpectedValue;
        public Ray3 DigRay;
        public int HitFace;
        public int StartClientTick;
        public int ActiveSlotIndex;
        public int ToolValue;
        public int ToolCount;
        public Vector3 BodyPosition;
        public double LastSeenTime;
    }

    public class OutgoingWorldTransfer
    {
        public int TransferId;
        public int TargetClientId;
        public double StartTime;
        public byte[] WorldData = Array.Empty<byte>();
        public int ChunkCount;
        public int NextChunkIndex;
        public int HighestContiguousChunkIndex = -1;
        public bool StartRequested;
        public bool InitialSendComplete;
        public GamePakWorldMessage Manifest;
        // Source: ScMultiplayer.cs:HandleGamePakWorldRepairRequestMessage
        public double[] ChunkLastQueueTimes = Array.Empty<double>();
        public readonly Queue<int> RepairChunkIndices = new Queue<int>();
        public readonly HashSet<int> QueuedRepairChunkIndices = new HashSet<int>();
        public int RepairChunkQueueCount;
    }

    public class IncomingWorldTransfer
    {
        public int TransferId;
        public int TargetClientId;
        public int TotalLength;
        public byte[][] Chunks;
        public int ReceivedChunkCount;
        public int ReceivedBytes;
        public int HighestContiguousChunkIndex = -1;
        public int HighestReceivedChunkIndex = -1;
        public GamePakWorldMessage Manifest;
        public double StartTime;
        public double LastProgressTime;
        public double LastStatusRequestTime;
        public double LastRepairRequestTime;
        public int RepairRequestCount;
    }

    public class WorldTransferChunkSendWork
    {
        public int Generation;
        public int TransferId;
        public int TargetClientId;
        public int ChunkIndex;
        public int ChunkCount;
        public byte[] WorldData;
    }

    public class JoinCatchUpMessage
    {
        public byte[] Payload;
        public bool Sequenced;
        public bool Latest;
    }

    public class JoinCatchUpJournal
    {
        public int StartTick;
        public int TotalBytes;
        public int TotalMessagesSent;
        public int TotalBytesSent;
        public int ReplayRound;
        public int DroppedMessages;
        public readonly List<JoinCatchUpMessage> Messages = new List<JoinCatchUpMessage>();
    }

    public class HostedWorldSnapshot
    {
        public string Name;
        public byte[] WorldData;
        public DateTime LastSaveTime;
        public int Tick;
        public long TerrainSequence;
        public Dictionary<string, long> RandomStates;
    }

    public class AnimalSyncMetadata
    {
        public double NextSendTime;
        public double HighPriorityUntil;
        public string BehaviorState = string.Empty;
        public int TargetEntityId;
        public string HerdName = string.Empty;
        public float LastHealth = 1f;
        public byte SyncTier;
        public bool AttackOrder;
        public bool FeedOrder;
        public int SimulationSeed;
        public bool HasSent;
    }

    public class AnimalSyncCandidate
    {
        public Entity Entity;
        public ComponentCreature Creature;
        public ComponentBody Body;
        public string BehaviorState;
        public int TargetEntityId;
        public string HerdName;
        public byte SyncTier;
        public bool StateChanged;
        public bool AttackOrder;
        public bool FeedOrder;
    }

    public class RemoteAnimalSyncState
    {
        public byte SyncTier;
        public string BehaviorState;
        public int TargetEntityId;
        public string HerdName;
        public float LastHealth;
        public bool HasHealth;
        public int LastServerTick;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Velocity;
        public Vector3 Acceleration;
        public Vector3 SmoothedVelocity;
        public Vector2 LookAngles;
        public bool AttackOrder;
        public bool FeedOrder;
        public bool HasTransform;
        public bool HasSmoothedVelocity;
        public bool PresentationInitialized;
        public float EstimatedSnapshotInterval = 0.1f;
        public float EstimatedDelay;
        public double LastUpdateTime;
        public Vector2? WalkOrder;
        public Vector3? FlyOrder;
        public Vector3? SwimOrder;
        public Vector2 TurnOrder;
        public float JumpOrder;
        public int SimulationSeed;
        public bool SimulationSeedApplied;
        public double? DeathTime;
        public bool LocalDespawnStarted;
    }

    public class NetworkMessageHandler
    {
        public static void HandleChatMessage(ChatMessage message, int clientID)
        {
            Log.Information($"[Chat] Client{clientID} {message.Sender}: {message.Text}");
            ScMultiplayer.currentInstance.DisplayChatMessage(message, clientID);
        }

        public static void HandleWorldInfoMessage(GameWorldInfoMessage1 message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGameWorldInfoMessage(message);
        }

        public static void HandleModifiedCellsMessage(GameModifiedCellsMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGameModifiedCellsMessage(message, clientID);
        }

        public static void HandlePakWorldMessage(GamePakWorldMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGamePakWorldMessage(message);
        }

        public static void HandlePlayerHealthMessage(GamePlayerHealthMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGamePlayerHealthMessage(message, clientID);
        }
    }

    public class NetworkMessageSender
    {
        private const int MaximumSyncBatchBytes = 1100;

        private sealed class PendingSyncBatch
        {
            public int TargetClientId;
            public bool Sequenced;
            public bool Latest;
            public int EstimatedBytes;
            public readonly List<byte[]> Payloads = new List<byte[]>();
        }

        private static readonly List<PendingSyncBatch> s_pendingSyncBatches =
            new List<PendingSyncBatch>();
        private static bool s_isSyncBatchActive;

        public static int PendingSyncBatchCount => s_pendingSyncBatches.Count;

        public static void BeginSyncBatch()
        {
            FlushSyncBatch();
            s_isSyncBatchActive = true;
        }

        public static void FlushSyncBatch()
        {
            bool wasActive = s_isSyncBatchActive;
            s_isSyncBatchActive = false;
            foreach (PendingSyncBatch batch in s_pendingSyncBatches.ToArray())
                SendPendingSyncBatch(batch);
            s_pendingSyncBatches.Clear();
            if (!wasActive) return;
        }

        public static void SendScheduledMessage(int targetClientId, Message message,
            bool sequenced = false, bool latest = false, bool batchable = true)
        {
            byte[] payload = Message.WriteWithSender(message, ScMultiplayer.client.Address);
            if (targetClientId < 0)
                ScMultiplayer.currentInstance?.RecordJoinCatchUpMessage(
                    payload, sequenced, latest);
            if (!s_isSyncBatchActive || !batchable || payload.Length + 24 > MaximumSyncBatchBytes)
            {
                if (s_isSyncBatchActive)
                {
                    FlushSyncBatch();
                    s_isSyncBatchActive = true;
                }
                ScMultiplayer.client.SendDirectInput(
                    targetClientId, payload, sequenced, latest);
                return;
            }

            PendingSyncBatch batch = s_pendingSyncBatches.LastOrDefault(item =>
                item.TargetClientId == targetClientId && item.Sequenced == sequenced &&
                item.Latest == latest);
            int entryBytes = payload.Length + 5;
            if (batch == null || batch.EstimatedBytes + entryBytes > MaximumSyncBatchBytes)
            {
                batch = new PendingSyncBatch
                {
                    TargetClientId = targetClientId,
                    Sequenced = sequenced,
                    Latest = latest,
                    EstimatedBytes = 24
                };
                s_pendingSyncBatches.Add(batch);
            }
            batch.Payloads.Add(payload);
            batch.EstimatedBytes += entryBytes;
        }

        private static void SendPendingSyncBatch(PendingSyncBatch batch)
        {
            if (batch.Payloads.Count == 0) return;
            byte[] payload = batch.Payloads.Count == 1
                ? batch.Payloads[0]
                : Message.WriteWithSender(new SyncBatchMessage
                {
                    Payloads = batch.Payloads
                }, ScMultiplayer.client.Address);
            ScMultiplayer.client.SendDirectInput(
                batch.TargetClientId, payload, batch.Sequenced, batch.Latest);
        }

        public static void SendPlayerPositionMessage(int playerIndex, int serverTick,
            Vector3 position, Quaternion rotation,
            Vector3 velocity, Vector2 lookAngles, Vector2? walkOrder, float jumpOrder,
            float pokingPhase, bool attackOrder, bool rowLeftOrder, bool rowRightOrder,
            bool isCrouching, bool isFlying, bool isRiding, bool isGrounded,
            int activeSlotIndex, int handItemValue, int handItemCount,
            Vector3 itemOffset, Vector3 itemRotation, float aimHandAngle,
            int[] slotValues, int[] slotCounts,
            List<GamePlayerPositionMessage> batch = null)
        {
            var msg = new GamePlayerPositionMessage(playerIndex, serverTick, position, rotation, velocity,
                lookAngles, walkOrder, jumpOrder, pokingPhase,
                attackOrder, rowLeftOrder, rowRightOrder,
                isCrouching, isFlying, isRiding, isGrounded,
                activeSlotIndex, handItemValue, handItemCount,
                itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts);
            if (batch != null)
            {
                batch.Add(msg);
                return;
            }
            SendScheduledMessage(-1, msg, latest: true);
        }

        public static void SendPlayerPositionBatch(List<GamePlayerPositionMessage> players)
        {
            if (players == null || players.Count == 0) return;
            var message = new GamePlayerPositionsMessage(players);
            // Source: ScMultiplayer.cs:HandleGamePlayerPositionMessage
            // Player snapshots carry ServerTick and reject stale state at the receiver. Keeping
            // them out of Comms' shared ReliableSequenced stream prevents a delayed fragmented
            // message from freezing every later player presentation update.
            SendScheduledMessage(-1, message, latest: true);
        }

        public static void SendPlayerInputMessage(int playerIndex, int sequence, int clientTick,
            Vector3 bodyPosition, Vector3 bodyVelocity, Quaternion bodyRotation,
            Vector2 lookAngles, PlayerInput playerInput, float pokingPhase,
            bool isControlledByTouch,
            bool isCrouching, bool isFlying, bool isRiding, ushort mountEntityId,
            int activeSlotIndex, int inventoryAuthorityTick,
            int[] slotValues, int[] slotCounts)
        {
            var msg = new GamePlayerInputMessage(
                playerIndex, sequence, clientTick, bodyPosition, bodyVelocity,
                bodyRotation, lookAngles, playerInput, pokingPhase, isControlledByTouch,
                isCrouching, isFlying, isRiding,
                mountEntityId, activeSlotIndex, inventoryAuthorityTick,
                slotValues, slotCounts);
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(msg, ScMultiplayer.client.Address), latest: true);
        }

        public static ChatMessage SendChatMessage(string sender, string senderIdentity, string text)
        {
            var msg = new ChatMessage(sender, senderIdentity, text);
            // Source: ScMultiplayer.cs:NetworkMessageHandler.HandleChatMessage
            SendScheduledMessage(-1, msg);
            return msg;
        }

        public static void SendWorldInfoMessage(double timeOfDayOffset, double totalElapsedGameTime,
            TimeOfDayMode currentTimeMode, SubsystemWeather weather, SubsystemSky sky,
            bool reliable = false)
        {
            // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.m_lightningStrikePosition
            // Nullable<T> boxes a present value as T. Use SuAPI's non-generic getter so its
            // generic result check does not reject the boxed Vector3 value.
            object lightningValue = ScMultiplayer.ModManager.ModParentField.GetParentField(
                sky, "m_lightningStrikePosition", typeof(SubsystemSky));
            Vector3? lightningPosition = lightningValue is Vector3 position
                ? position
                : (Vector3?)null;
            var msg = new GameWorldInfoMessage1(timeOfDayOffset, totalElapsedGameTime, currentTimeMode,
                weather.IsPrecipitationStarted, weather.PrecipitationIntensity,
                weather.IsFogStarted, weather.FogProgress, weather.FogIntensity, weather.FogSeed,
                lightningPosition.HasValue, lightningPosition ?? Vector3.Zero);
            SendScheduledMessage(-1, msg, latest: !reliable, batchable: !reliable);
        }

        public static void SendWorldControlRequest(WorldControlAction actions)
        {
            var msg = new WorldControlRequestMessage(actions);
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        // Source: ScMultiplayer.cs:HandleAnimalEntityMessage
        public static void SendEntityMessage(EntityMessage message) =>
            SendScheduledMessage(-1, message);

        // Source: ScMultiplayer.cs:HandleAnimalBodyUpdate
        public static void SendBodyUpdateMessage(BodyUpdateMessage message,
            bool reliable = false) =>
            SendScheduledMessage(-1, message, latest: !reliable,
                batchable: !reliable);

        // Source: ScMultiplayer.cs:HandlePickableSyncMessage
        public static void SendPickableMessage(PickableSyncMessage message) =>
            SendScheduledMessage(-1, message,
                sequenced: message.Action != PickableSyncMessage.PickAction.UpdatePosition,
                latest: message.Action == PickableSyncMessage.PickAction.UpdatePosition);

        public static void SendPakWorldManifest(int targetClientId, GamePakWorldMessage message)
        {
            ScMultiplayer.client.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, ScMultiplayer.client.Address));
        }

        public static void SendPakWorldChunk(int targetClientId,
            GamePakWorldChunkMessage message)
        {
            ScMultiplayer.client.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, ScMultiplayer.client.Address));
        }

        public static void SendPakWorldReady(GamePakWorldReadyMessage message)
        {
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(message, ScMultiplayer.client.Address), sequenced: true);
        }

        public static void SendPakWorldReady(int targetClientId, GamePakWorldReadyMessage message)
        {
            ScMultiplayer.client.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, ScMultiplayer.client.Address), sequenced: true);
        }

        public static void SendPakWorldRepairRequest(GamePakWorldRepairRequestMessage message)
        {
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(message, ScMultiplayer.client.Address));
        }

        public static void SendPlayerAimMessage(PlayerAimMessage message)
        {
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(message, ScMultiplayer.client.Address), sequenced: true);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public static void SendPlayerHitRequest(PlayerActionMessage message)
        {
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(message, ScMultiplayer.client.Address), sequenced: true);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public static void SendPlayerInteractRequest(PlayerActionMessage message)
        {
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(message, ScMultiplayer.client.Address));
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public static void SendPlayerDropRequest(PlayerActionMessage message)
        {
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(message, ScMultiplayer.client.Address), sequenced: true);
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        public static void SendProjectileHit(int targetClientId, ProjectileSyncMessage message)
        {
            ScMultiplayer.client.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, ScMultiplayer.client.Address));
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.LeaveGame
        public static void BroadcastPlayerLeave(PlayerActionMessage message)
        {
            SendScheduledMessage(-1, message);
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead
        public static void SendPlayerRespawnRequest(PlayerActionMessage message)
        {
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(message, ScMultiplayer.client.Address));
        }

        public static void BroadcastPlayerRespawn(PlayerActionMessage message)
        {
            SendScheduledMessage(-1, message);
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Poke
        public static void BroadcastPlayerPoke(PlayerActionMessage message)
        {
            SendScheduledMessage(-1, message);
        }

        // Source: Survivalcraft/Game/SubsystemWhistleBlockBehavior.cs:SubsystemWhistleBlockBehavior.OnUse
        public static void BroadcastPlayerWhistle(PlayerActionMessage message)
        {
            SendScheduledMessage(-1, message);
        }

        public static void SendTerrainDigRequest(TerrainDigRequestMessage message)
        {
            ScMultiplayer.client.SendDirectInput(0,
                Message.WriteWithSender(message, ScMultiplayer.client.Address));
        }

        public static void SendTerrainDigResult(int targetClientId,
            TerrainDigResultMessage message)
        {
            ScMultiplayer.client.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, ScMultiplayer.client.Address));
        }

        public static void SendPlayerProfileMessage(int clientId, NetworkPlayerRecord record)
        {
            var msg = new PlayerProfileMessage(clientId, record);
            // Source: ScMultiplayer.cs:HandlePlayerProfileMessage
            SendScheduledMessage(-1, msg);
        }

        public static void SendPlayerEquipmentMessage(int targetClientId,
            PlayerEquipmentMessage message)
        {
            if (message == null || ScMultiplayer.client == null) return;
            SendScheduledMessage(targetClientId, message, sequenced: true, latest: false,
                batchable: false);
        }

        public static void SendPlayerHealthMessage(int playerIndex, ComponentPlayer player,
            float healthChange, string cause = null, bool hasKnockback = false)
        {
            ComponentHealth health = player?.ComponentHealth;
            ComponentVitalStats vitalStats = player?.ComponentVitalStats;
            if (health == null || vitalStats == null) return;
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            var msg = new GamePlayerHealthMessage(
                playerIndex, health.Health, 1f, healthChange, health.Health <= 0f,
                health.Air, vitalStats.Food, vitalStats.Stamina, vitalStats.Sleep,
                vitalStats.Temperature,
                ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                    vitalStats, "m_targetTemperature", typeof(ComponentVitalStats)),
                vitalStats.Wetness, player.PlayerData.Level,
                player.ComponentBody?.Velocity ?? Vector3.Zero,
                hasKnockback,
                player.ComponentSleep?.IsSleeping == true,
                onFire != null ? ScMultiplayer.ModManager.ModParentField.GetParentField<float>(onFire, "m_fireDuration", typeof(ComponentOnFire)) : 0f,
                flu != null ? ScMultiplayer.ModManager.ModParentField.GetParentField<float>(flu, "m_fluDuration", typeof(ComponentFlu)) : 0f,
                sickness != null ? ScMultiplayer.ModManager.ModParentField.GetParentField<float>(sickness, "m_sicknessDuration", typeof(ComponentSickness)) : 0f,
                (flu as SuComponentFlu)?.CoughSequence ?? 0,
                flu?.IsCoughing == true,
                cause);
            // Source: ScMultiplayer.cs:NetworkMessageHandler.HandlePlayerHealthMessage
            SendScheduledMessage(-1, msg);
        }

        public static void SendKickPlayerMessage(int targetClientID, string reason = null)
        {
            var msg = new GameKickPlayerMessage(targetClientID, reason);
            // Source: ScMultiplayer.cs:HandleGameKickPlayerMessage
            SendScheduledMessage(-1, msg);
        }
    }

    #endregion

    // ================================================================
    // ScMultiplayer 主类
    // ================================================================
    public class ScMultiplayer : IMod
    {
        public static ModManager ModManager = (ModManager)AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => t.FullName == "Game.Program")?
            .GetField("ModManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        public static Server server;
        public static Client client;
        public static Explorer explorer;
        public static ScMultiplayer currentInstance;
        public static PlayerMappingManager playerMappingManager = new PlayerMappingManager();
        public static PlayerOperationSyncManager playerOperationSyncManager = new PlayerOperationSyncManager();
        public static bool IsHost = false;

        // ---------- 游戏描述缓存 (LanDiscovery 响应用) ----------
        public static byte[] LastGameDescription;

        // ---------- 远程玩家 ----------
        public static Dictionary<int, NetworkPlayerState> RemotePlayers = new Dictionary<int, NetworkPlayerState>();
        private PrimitivesRenderer3D m_primitivesRenderer3D;

        // ---------- 状态机 ----------
        public static NetworkConnectionStateMachine connectionSM;
        public static WorldDownloadStateMachine downloadSM;

        // ---------- IMod ----------
        public string Name => "SC联机";
        public string Version => "1.6.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public bool IsInRoom => client?.IsConnected == true && client.GameID >= 0;

        // ---------- 内部状态 ----------
        private float m_syncPulseAccumulator;
        private double m_lastSyncUpdateTime;
        private uint m_syncPulseIndex;
        private Project m_frameProject;
        private int m_lastWorldUpdateFrameIndex = -1;
        private Dictionary<int, float> m_playerHealthCache = new Dictionary<int, float>(); // clientID → last known health
        private readonly Dictionary<int, float> m_hostKnockbackHealthCache =
            new Dictionary<int, float>();
        private readonly Dictionary<int, double> m_hostPainSoundTimes =
            new Dictionary<int, double>();
        private readonly Dictionary<int, double> m_hostRemoteKnockbackUntil =
            new Dictionary<int, double>();
        private bool m_hasObservedClientHealth;
        private float m_observedClientHealth;
        private bool m_observedClientSleeping;
        private bool m_hasAuthoritativeLocalInventory;
        private int[] m_authoritativeLocalSlotValues = Array.Empty<int>();
        private int[] m_authoritativeLocalSlotCounts = Array.Empty<int>();
        private readonly Dictionary<int, PlayerData> m_networkPlayerData = new Dictionary<int, PlayerData>();
        private readonly HashSet<int> m_creatingNetworkPlayers = new HashSet<int>();
        private readonly Dictionary<int, string> m_pendingNetworkPlayers = new Dictionary<int, string>();
        private readonly Dictionary<int, string> m_pendingNetworkPlayerIdentities = new Dictionary<int, string>();
        private readonly Dictionary<int, NetworkPlayerInputState> m_networkPlayerInputs =
            new Dictionary<int, NetworkPlayerInputState>();
        // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Handle
        // Client IDs increase for the lifetime of a room, so a leave tombstone safely rejects
        // delayed profile/entity messages until the next room resets transient state.
        private readonly HashSet<int> m_departedRemoteClientIds = new HashSet<int>();
        private PlayerInput m_localPlayerInput;
        private Vector3 m_localInputBodyPosition;
        private Vector3 m_localInputBodyVelocity;
        private Quaternion m_localInputBodyRotation = Quaternion.Identity;
        private Vector2 m_localInputLookAngles;
        private int m_localInputSequence;
        private int m_lastSentInputSequence = -1;
        private int m_localInputResendsRemaining;
        private bool m_localAimActive;
        private int m_localAimSequence;
        private int m_localAimSlot = -1;
        private int m_localAimItemValue;
        private int m_localAimItemCount;
        private Ray3 m_localAimRay;
        private double m_lastAimUpdateSentTime;
        private float m_smoothedNetworkDelay;
        private readonly Dictionary<string, NetworkPlayerRecord> m_playerRecords = new Dictionary<string, NetworkPlayerRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> m_clientRecordKeys = new Dictionary<int, string>();
        private readonly Queue<ChatMessage> m_recentChatMessages = new Queue<ChatMessage>();
        private readonly HashSet<Guid> m_recentChatMessageIds = new HashSet<Guid>();
        private IModInjector m_modInjector;
        private LabelWidget m_networkStatsLabel;
        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticStats
        private DiagnosticStats m_serverNetworkStats;
        private DiagnosticStats m_clientNetworkStats;
        private long m_lastNetworkByteSample;
        private double m_lastNetworkByteSampleTime;
        private double m_nextNetworkStatsUpdateTime;
        private readonly Dictionary<IPAddress, double> m_reverseDiscoveryProbeTimes =
            new Dictionary<IPAddress, double>();
        private RemoteServerDirectory m_remoteServerDirectory;
        private string m_playerRecordsWorldDirectory;
        private bool m_playerRecordsDirty;
        private float m_playerRecordSaveTime;
        private float m_playerProfileSyncTime;
        private float m_inventoryKeyframeTime;
        private readonly Dictionary<int, int[]> m_lastSentInventoryValues =
            new Dictionary<int, int[]>();
        private readonly Dictionary<int, int[]> m_lastSentInventoryCounts =
            new Dictionary<int, int[]>();
        private readonly Dictionary<int, int> m_equipmentAuthorityRevisions =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_lastClientEquipmentRevisions =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_lastReceivedEquipmentRevisions =
            new Dictionary<int, int>();
        private readonly Dictionary<int, EquipmentSnapshot> m_lastEquipmentSnapshots =
            new Dictionary<int, EquipmentSnapshot>();
        private readonly HashSet<int> m_equipmentSynchronizedClients = new HashSet<int>();
        private int m_localEquipmentRevision;
        private PendingJoinRequest m_pendingJoinRequest;
        private PendingJoinRequest m_activeJoinRequest;
        private string m_activeJoinPlayerName;
        private PlayerClass m_activeJoinPlayerClass = PlayerClass.Male;
        private string m_activeJoinSkinName;
        private bool m_activeJoinHasPlayerProfile;
        private volatile bool m_reconnectRequested;
        private bool m_reconnectPending;
        private int m_reconnectAttempts;
        private double m_nextReconnectAttemptTime;
        private NetworkPlayerRecord m_pendingLocalPlayerRecord;
        private PlayerData m_localReplacementPlayerData;
        private bool m_localPlayerRecordQueued;
        private bool m_localPlayerRecordApplied;
        private bool m_replacingLocalPlayerData;
        private const float HealthSyncInterval = 1.0f; // 每秒同步一次生命
        private string m_downloadedWorldDirectory;
        private bool m_hostDisconnectHandled;
        private bool m_localLeaveInProgress;
        private bool m_shouldCreateHostAvatar;
        private bool m_isLoadingDownloadedWorld;
        private BusyDialog m_joinRoomBusyDialog;
        private bool m_createRoomPending;
        private Project m_autoHostProject;
        private bool m_autoHostAttempted;
        private double m_nextAutoHostAttemptTime;
        private Vector3 m_pendingLocalKnockbackVelocity;
        private double m_pendingLocalKnockbackUntil;
        private ushort m_nextAnimalId = 1;
        private ushort m_nextPickableId = 1;
        private float m_fullWorldObjectsSyncTime;
        private float m_fullAnimalSyncTime;
        private Project m_runawayCreatureCleanupProject;
        private readonly Queue<Entity> m_runawayCreatureCleanup = new Queue<Entity>();
        private double m_nextRunawayCreatureCheckTime;
        private double m_nextRemoteCreatureSpawnTime;
        private int m_remoteCreatureSpawnCursor;
        private Project m_clientWorldObjectsProject;
        private readonly ConcurrentQueue<Action> m_endOfFrameActions = new ConcurrentQueue<Action>();
        private readonly Dictionary<Entity, ushort> m_hostAnimalIds = new Dictionary<Entity, ushort>();
        private readonly List<Entity> m_hostAnimals = new List<Entity>();
        private readonly Dictionary<Entity, AnimalSyncMetadata> m_hostAnimalSync =
            new Dictionary<Entity, AnimalSyncMetadata>();
        private readonly Dictionary<ushort, Entity> m_remoteAnimals = new Dictionary<ushort, Entity>();
        private readonly Dictionary<ushort, string> m_remoteAnimalTemplates = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, RemoteAnimalSyncState> m_remoteAnimalSync =
            new Dictionary<ushort, RemoteAnimalSyncState>();
        private readonly HashSet<ushort> m_loggedRemoteAnimalFailures = new HashSet<ushort>();
        private int m_lastFullAnimalSnapshotTick;
        private readonly Dictionary<Pickable, ushort> m_hostPickableIds = new Dictionary<Pickable, ushort>();
        private SubsystemPickables m_hostPickablesSubsystem;
        private bool m_forceHostInventorySync;
        private readonly Dictionary<ushort, Pickable> m_remotePickables = new Dictionary<ushort, Pickable>();
        private bool m_applyingNetworkPickable;
        private int m_lastAuthoritativeLocalInventoryTick;
        private int[] m_lastLocalInventoryValues = Array.Empty<int>();
        private int[] m_lastLocalInventoryCounts = Array.Empty<int>();
        private int m_pendingLocalDropValue;
        private int m_pendingLocalDropCount;
        private Vector3 m_pendingLocalDropPosition;
        private double m_pendingLocalDropPredictionUntil;
        private readonly Dictionary<ushort, RemotePickableNetworkState> m_remotePickableStates =
            new Dictionary<ushort, RemotePickableNetworkState>();
        private readonly Dictionary<ushort, PendingPickablePickupPresentation>
            m_pendingPickablePickups =
                new Dictionary<ushort, PendingPickablePickupPresentation>();
        private readonly Dictionary<Projectile, ushort> m_hostProjectileIds = new Dictionary<Projectile, ushort>();
        private readonly Dictionary<ushort, Projectile> m_remoteProjectiles = new Dictionary<ushort, Projectile>();
        private readonly Dictionary<Projectile, double> m_clientPredictedProjectiles =
            new Dictionary<Projectile, double>();
        private readonly HashSet<long> m_displayedProjectileHits = new HashSet<long>();
        private ushort m_nextProjectileId = 1;
        private readonly Dictionary<string, ContainerNetworkState> m_containerStates =
            new Dictionary<string, ContainerNetworkState>();
        private readonly HashSet<IUpdateable> m_disabledClientContainerUpdates = new HashSet<IUpdateable>();
        private readonly Dictionary<ushort, RemotePickableRecord> m_remotePickableRecords = new Dictionary<ushort, RemotePickableRecord>();
        private readonly object m_terrainJournalLock = new object();
        private readonly Queue<TerrainJournalEntry> m_hostTerrainJournal =
            new Queue<TerrainJournalEntry>();
        private readonly Dictionary<int, long> m_hostTerrainRecoveryTargets =
            new Dictionary<int, long>();
        private long m_hostTerrainSequence;
        private long m_pendingTerrainSequenceBaseline;
        private volatile bool m_clientTerrainRecoveryActive;
        private volatile bool m_clientTerrainRecoveryPending;
        private bool m_clientTerrainRecoveryRequestInFlight;
        private volatile bool m_clientSuspensionRequested;
        private long m_clientTerrainRecoveryTarget = -1;
        private long m_clientTerrainRecoveryAcknowledged = -1;
        private long m_clientTerrainRecoveryReady = -1;
        private double m_clientTerrainGapDetectedTime;
        private bool m_clientGameplayScreenObserved;
        private bool m_wasClientGameScreenActive;
        private volatile bool m_clientWindowDeactivated;
        private int m_lastProjectSimulationFrameIndex = -1;
        private readonly Dictionary<Point3, TerrainCellState> m_terrainCheckpoint =
            new Dictionary<Point3, TerrainCellState>();
        private readonly Dictionary<Point3, TerrainCellState> m_pendingTerrainChanges =
            new Dictionary<Point3, TerrainCellState>();
        private readonly Dictionary<Point3, int> m_terrainRepairRepeats =
            new Dictionary<Point3, int>();
        private readonly Dictionary<int, PendingTerrainPrediction> m_pendingTerrainPredictions =
            new Dictionary<int, PendingTerrainPrediction>();
        private readonly Dictionary<Point3, int> m_pendingTerrainPredictionCells =
            new Dictionary<Point3, int>();
        private readonly Dictionary<long, TerrainDigResultMessage> m_processedTerrainDigRequests =
            new Dictionary<long, TerrainDigResultMessage>();
        private readonly Dictionary<Point3, LocalTerrainDigIntent> m_localTerrainDigIntents =
            new Dictionary<Point3, LocalTerrainDigIntent>();
        private readonly Dictionary<int, float> m_hostPlayerPokingPhases =
            new Dictionary<int, float>();
        private readonly Dictionary<int, int> m_hostPlayerPokeSequences =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_playerWhistleSequences =
            new Dictionary<int, int>();
        private readonly Engine.Random m_audioEventRandom = new Engine.Random();
        private readonly Dictionary<int, OutgoingWorldTransfer> m_outgoingWorldTransfers =
            new Dictionary<int, OutgoingWorldTransfer>();
        private readonly Dictionary<int, int> m_worldTransfersAwaitingReady =
            new Dictionary<int, int>();
        private readonly Dictionary<int, JoinCatchUpJournal> m_joinCatchUpJournals =
            new Dictionary<int, JoinCatchUpJournal>();
        private readonly Dictionary<int, string> m_pendingAcceptedJoinKeys =
            new Dictionary<int, string>();
        private readonly Dictionary<int, HostJoinRequest> m_hostJoinRequests =
            new Dictionary<int, HostJoinRequest>();
        private Dialog m_activeJoinDecisionDialog;
        private int m_activeJoinDecisionClientId = -1;
        private readonly Dictionary<int, IncomingWorldTransfer> m_incomingWorldTransfers =
            new Dictionary<int, IncomingWorldTransfer>();
        private readonly ConcurrentQueue<WorldTransferChunkSendWork> m_worldTransferSendQueue =
            new ConcurrentQueue<WorldTransferChunkSendWork>();
        private readonly SemaphoreSlim m_worldTransferSendSignal = new SemaphoreSlim(0);
        private CancellationTokenSource m_worldTransferSendCancellation;
        private Task m_worldTransferSendTask;
        private int m_worldTransferQueuedWorkCount;
        private int m_worldTransferGeneration;
        private Point3? m_localDigTarget;
        private int m_nextTerrainDigRequestId;
        private int m_localHitSequence;
        private double m_nextLocalHitRequestTime;
        private int m_localInteractSequence;
        private double m_nextLocalInteractRequestTime;
        private int m_localDropSequence;
        private Entity m_observedLocalPlayerEntity;
        private bool m_observedLocalPlayerWasDead;
        private int m_localRespawnSequence;
        private double m_localRespawnPendingUntil;
        private int m_nextWorldTransferId;
        private int m_worldTransferCursor;
        private double m_nextWorldTransferManifestRequestTime;
        private double m_nextWorldTransferUiUpdateTime;
        private int m_pendingWorldReadyTransferId;
        private float m_terrainMergeTime;
        private int m_sessionRandomSeed;
        private Dictionary<string, long> m_pendingRandomStates = new Dictionary<string, long>();
        private Project m_randomStateAppliedProject;
        private GameWorldInfoMessage1 m_remoteWeatherState;
        private bool m_remoteLightningActive;
        private bool m_hostLightningActive;
        private double m_localLightningPredictionUntil;
        private WorldControlAction m_pendingWorldControlActions;
        private double m_worldControlRequestDeadline;
        private byte[] m_pendingLocalCreateDescription;
        private IPEndPoint m_pendingLocalCreateAddress;
        private int m_localCreateAttempts;
        private double m_nextLocalCreateAttemptTime;
        private const float ServerTickDuration = 0.01f;
        private const float TransportTickDuration = 0.05f;
        private const int LogicStepsPerTransportTick = 5;
        private const int SyncBaseRate = 32;
        private const float SyncPulseDuration = 1f / SyncBaseRate;
        private const int MaxSyncPulsesPerUpdate = 4;
        private const int MaxReconnectAttempts = 5;
        private const int MaximumLocalCreateAttempts = 5;
        private const double LocalCreateRetryInterval = 1.5;
        private const float ReconnectInitialDelay = 1f;
        private const float ReconnectMaxDelay = 5f;
        private const float RemoteConnectionLostPeriod = 15f;
        private const float LocalHostConnectionLostPeriod = float.MaxValue;
        private const float LocalKnockbackHoldDuration = 0.35f;
        private const float RemoteInputHoldDuration = 0.75f;
        private const float RemoteDelaySampleLimit = 0.6f;
        private const float RemoteExtrapolationLimit = 0.35f;
        private const float RemotePresentationStaleTime = 2f;
        private const float RemoteAnimalPredictionLimit = 1.15f;
        private const float RemoteAnimalSnapDistance = 24f;
        private const float ClientProjectilePredictionGrace = 3f;
        private const float PlayerHitRequestInterval = 0.36f;
        private const float PlayerInteractRequestInterval = 0.36f;
        // Source: Comms/Comms/UdpTransmitter.cs:UdpTransmitter.MaxPacketSize
        // 940 bytes plus the nested ScMultiplayer, DRT, Peer and Comm headers fits in one
        // 1024-byte UDP packet for both IPv4 and IPv6 connection-init packets.
        private const int WorldTransferChunkSize = 940;
        private const int MaximumWorldTransferChunksPerNetworkTick = 8;
        private const int MaximumWorldTransferChunksPerGameplayTick = 4;
        private const int MaximumWorldTransferUnackedPackets = 32;
        private const int MaximumWorldTransferRepairChunks = 24;
        private const int MaximumQueuedWorldTransferChunks = 32;
        private const int WorldTransferWindowChunks = 24;
        private const int ReverseDiscoveryPortProbeCount = 4;
        private const double WorldTransferStatusInterval = 0.25;
        private const double WorldTransferRepairInterval = 0.75;
        private const double WorldTransferRepairRequestInterval = 1.5;
        private const int MaximumWorldTransferSize = 64 * 1024 * 1024;
        private const int MaximumJoinCatchUpBytes = 4 * 1024 * 1024;
        private const double TerrainRecoveryRetention = 15.0;
        private const int MaximumTerrainRecoveryRanges = 64;
        private const int MaximumTerrainRecoveryBatchBytes = 256 * 1024;
        private const double TerrainGapRecoveryDelay = 0.75;
        private const int RunawayCreatureThreshold = 256;
        private const int RunawayCreatureKeepCount = 52;
        private const int RunawayCreatureCleanupBatchSize = 256;
        private const float RemoteCreatureSpawnInterval = 1f;
        private const int RemoteSpawnRecordsPerInterval = 2;
        private const int RemoteCreatureTargetCount = 26;
        private const float RemoteCreaturePopulationRadius = 68f;
        private const float PlayerProfileSyncInterval = 5f;
        private const float WorldObjectFullSyncInterval = 5f;
        private const float PlayerRecordSaveInterval = 5f;
        private const float TerrainMergeInterval = 5f;
        private const int TerrainRepairRepeatCount = 3;
        private const int TerrainCatchUpBatchSize = 128;
        private const int AnimalSyncBatchSize = 12;
        private const int MaximumRecentChatMessages = 50;
        private const string DownloadedWorldsRegistryPath = "data:/ScMultiplayerDownloadedWorlds.txt";
        private const string PlayerRecordsFileName = "ScMultiplayerPlayers.xml";
        private const string PlayerProfileRequiredReason = "SCMP_PROFILE_REQUIRED";
        // Source: Mod/CircuitAutoRouter/SubsystemCircuitRouter.cs:CircuitColors
        private static readonly Color[] ChatColors =
        {
            Color.White,
            Color.Cyan,
            Color.Red,
            Color.Blue,
            Color.Yellow,
            Color.Green,
            new Color(255, 165, 0),
            new Color(160, 32, 240)
        };

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            currentInstance = this;
            ModManager = Game.Program.ModManager;
            m_modInjector = modInjector;
            modInjector.Register("Game.ComponentHumanModel",
                "ScMultiplayer.SuComponentHumanModel");
            ScMultiplayerSettings.Load();
            StartWorldTransferSender();

            // 初始化状态机
            connectionSM = new NetworkConnectionStateMachine(msg => Log.Information(msg));
            downloadSM = new WorldDownloadStateMachine(msg => Log.Information(msg));

            // 注册状态机回调
            connectionSM.OnDisconnectedEnter += () =>
            {
                if (client.IsConnected) { try { client.LeaveGame(); } catch { } }
            };
            connectionSM.OnPlayingEnter += () => IsHost = (client.ClientID == 0);

            downloadSM.OnCompleteEnter += () => connectionSM.TransitionTo(
                NetworkConnectionStateMachine.ConnectionState.Playing);
            downloadSM.OnFailedEnter += (reason) =>
            {
                Log.Error($"[DL] Failed: {reason}");
                Dispatcher.Dispatch(() =>
                {
                    HideJoinRoomBusyDialog();
                    DialogsManager.ShowDialog(null, new MessageDialog(
                        "Join Room", reason ?? "World download failed.",
                        "OK", null, null));
                });
            };

            // EventBus
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
                HandleGameDatabase((Database)args[0]), EventPriority.HIGHEST);
            eventBus.SubscribeEvent("Loading.Initialize", args =>
                HandleLoading(args), EventPriority.HIGHEST);
            eventBus.SubscribeEvent("Frame.Update", args =>
            {
                // Source: Survivalcraft/Game/Program.cs:Program.Run
                // Menu/loading state and the post-game-update network scheduler share this
                // once-per-rendered-frame entry point.
                UpdateFrame(Time.FrameDuration);
                ProcessEndOfFrameActions();
                UpdateClientTerrainRecoveryAfterNetworkActions();
                CleanupDownloadedWorldsIfIdle();
                return args;
            }, EventPriority.LOWEST);

            CleanupDownloadedWorldsIfIdle();
            GameManager.ProjectDisposed += HandleProjectDisposed;
            Window.Deactivated += HandleWindowDeactivated;
            Window.Activated += HandleWindowActivated;

            // 初始化网络
            // Source: Mod/Comms/Comms.Drt/Func/Server/Server.cs:Server.Server
            // Source: Comms.Drt/Func/Server/Server.cs:Server.Server
            // Five 10ms logic steps remain batched into one 50ms transport tick. The independent
            // 32Hz power-of-two message scheduler must not alter Client.Step's 100Hz logic clock.
            float tickDuration = TransportTickDuration;
            int stepsPerTick = LogicStepsPerTransportTick;
            IReadOnlyList<int> serverPorts = ScMultiplayerSettings.ServerPorts;
            Log.Information($"[ScMP] Scanning server ports {serverPorts[0]}-" +
                $"{serverPorts[serverPorts.Count - 1]}");

            // 探测物理 LAN IP（避免虚拟网卡如 ZeroTier/WSL/CFW 导致广播源不可达）
            var lanAddress = DetectLanAddress();
            Log.Information($"[ScMP] Detected LAN address: {lanAddress}");

            // UdpTransmitter(now) 只接受 localPort 参数，自动检测 LAN 地址
            var serverTransmitter = BindFirstAvailableServerPort(
                ScMultiplayerSettings.ServerBindPorts,
                out int port);
            var explorerTransmitter = new UdpTransmitter(0);
            var serverDiagnosticTransmitter = new DiagnosticTransmitter(serverTransmitter);
            m_serverNetworkStats = serverDiagnosticTransmitter.Stats;

            try
            {
                server = new Server(0x53634d70, tickDuration, stepsPerTick,
                    serverDiagnosticTransmitter);
                ConfigurePeerTimeout(server.Peer, RemoteConnectionLostPeriod);
                // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerSettings.cs:ServerSettings.JoinRequestTimeout
                // Manual approval can remain pending while the host finishes another action.
                server.Settings.JoinRequestTimeout = 300f;
                server.Information += Server_Information;
                server.Start();
                Log.Information($"[ScMP] Server started OK, address={server.Address}");
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Server start FAILED: {ex.Message}");
            }

            explorer = new Explorer(0x53634d70, serverPorts, explorerTransmitter);
            explorer.Error += ex => Log.Error($"[Explorer] {ex.Message}");
            // Source: Comms/Comms/Peer.cs:Peer.DiscoverLocalPeers
            // On Android, tun0 may receive a ZeroTier broadcast but reject sending one back.
            // Let the local Explorer unicast-probe the request source, with per-address throttling.
            server.Peer.PeerDiscoveryRequest += HandleReverseDiscoveryRequest;

            client = CreateStartedClient(RemoteConnectionLostPeriod);

            // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.StartDiscovery
            m_remoteServerDirectory = new RemoteServerDirectory(explorer);
            m_remoteServerDirectory.Start();
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Discovering);
            Log.Information($"[ScMP] Explorer discovery started (address={explorerTransmitter.Address})");

            // World synchronization is registered as a database subsystem and is recreated
            // together with every Project, including downloaded-world reloads.
        }

        // Source: Mod/Comms/Comms/UdpTransmitter.cs:UdpTransmitter.UdpTransmitter
        private static UdpTransmitter BindFirstAvailableServerPort(
            IReadOnlyList<int> serverPorts,
            out int selectedPort)
        {
            SocketException lastError = null;
            foreach (int port in serverPorts)
            {
                try
                {
                    UdpTransmitter transmitter = new UdpTransmitter(port);
                    selectedPort = port;
                    Log.Information($"[ScMP] Selected local server port {port}");
                    return transmitter;
                }
                catch (SocketException error) when (
                    error.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    lastError = error;
                }
            }
            throw new InvalidOperationException(
                $"No free UDP server port exists in {serverPorts[0]}-" +
                $"{serverPorts[serverPorts.Count - 1]}.",
                lastError);
        }

        // Source: Comms.Drt/Func/Server/Server.cs:Server.PeerDiscoveryRequest
        private void HandleReverseDiscoveryRequest(Packet packet)
        {
            if (packet.Address == null || packet.Address.AddressFamily != AddressFamily.InterNetwork ||
                server == null || explorer == null ||
                UdpTransmitter.IsLocalIPv4Address(packet.Address.Address))
                return;

            double now = Comm.GetTime();
            lock (m_reverseDiscoveryProbeTimes)
            {
                if (m_reverseDiscoveryProbeTimes.TryGetValue(packet.Address.Address, out double lastTime) &&
                    now - lastTime < 2.0)
                    return;
                m_reverseDiscoveryProbeTimes[packet.Address.Address] = now;
            }

            try
            {
                // Source: Comms.Drt/Func/Explorer/Explorer.cs:Explorer.DiscoverServer
                // Android clients normally bind at the first free base port. Probing the full
                // server range for every request amplifies VPN discovery traffic unnecessarily.
                IReadOnlyList<int> ports = ScMultiplayerSettings.ServerPorts;
                int count = Math.Min(ReverseDiscoveryPortProbeCount, ports.Count);
                for (int i = 0; i < count; i++)
                    explorer.DiscoverServer(new IPEndPoint(packet.Address.Address, ports[i]));
            }
            catch (Exception error)
            {
                Log.Error($"[SuAPI] Reverse discovery probe failed for {packet.Address.Address}: {error.Message}");
            }
        }

        // Source: Mod/Comms/Comms/Peer.cs:Peer.ProcessPeers
        // Keep host/client failure detection responsive without coupling it to the game tick rate.
        private static void ConfigurePeerTimeout(Peer peer, float connectionLostPeriod)
        {
            if (peer == null) return;
            peer.Settings.KeepAlivePeriod = 2f;
            peer.Settings.KeepAliveResendPeriod = 1f;
            peer.Settings.ConnectTimeOut = 300f;
            peer.Settings.ConnectionLostPeriod = connectionLostPeriod;
        }

        // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.Client
        private Client CreateStartedClient(float connectionLostPeriod)
        {
            var clientDiagnosticTransmitter = new DiagnosticTransmitter(new UdpTransmitter(0));
            m_clientNetworkStats = clientDiagnosticTransmitter.Stats;
            m_lastNetworkByteSample = 0;
            m_lastNetworkByteSampleTime = 0.0;
            var newClient = new Client(0x53634d70, clientDiagnosticTransmitter);
            ConfigurePeerTimeout(newClient.Peer, connectionLostPeriod);
            newClient.GameCreated += Client_GameCreated;
            newClient.GameJoined += Client_GameJoined;
            newClient.Error += Client_Error;
            newClient.GameDescriptionRequest += Client_GameDescriptionRequest;
            newClient.ConnectRefused += Client_ConnectRefused;
            newClient.ConnectTimedOut += Client_ConnectTimedOut;
            newClient.GameStateRequest += Client_GameStateRequest;
            newClient.GameStep += Client_GameStep;
            newClient.DirectInput += Client_DirectInput;
            newClient.Start();
            return newClient;
        }

        // Source: Mod/Comms/Comms/Peer.cs:Peer.ProcessPeers
        // A transient UDP timeout must not destroy the downloaded world immediately. Rejoin the
        // same room with bounded exponential backoff, then use the normal host-disconnect cleanup.
        private void UpdateHostReconnect()
        {
            if (m_reconnectRequested)
            {
                m_reconnectRequested = false;
                if (!IsHost && !m_localLeaveInProgress && !m_hostDisconnectHandled &&
                    m_activeJoinRequest?.WorldInfo != null)
                {
                    m_reconnectPending = true;
                    m_reconnectAttempts = 0;
                    m_nextReconnectAttemptTime = Time.RealTime + ReconnectInitialDelay;
                    Log.Information("[ScMP] Host connection interrupted; reconnect scheduled");
                }
            }

            if (!m_reconnectPending) return;
            if (IsHost || m_localLeaveInProgress || m_hostDisconnectHandled)
            {
                m_reconnectPending = false;
                return;
            }
            if (client == null || m_activeJoinRequest?.WorldInfo == null)
            {
                m_reconnectPending = false;
                HandleHostDisconnected();
                return;
            }
            if (client.IsConnected || client.IsConnecting || Time.RealTime < m_nextReconnectAttemptTime)
                return;
            if (m_reconnectAttempts >= MaxReconnectAttempts)
            {
                Log.Error($"[ScMP] Host reconnect failed after {m_reconnectAttempts} attempts");
                m_reconnectPending = false;
                HandleHostDisconnected();
                return;
            }

            m_reconnectAttempts++;
            double retryDelay = Math.Min(ReconnectMaxDelay,
                ReconnectInitialDelay * Math.Pow(2.0, m_reconnectAttempts - 1));
            m_nextReconnectAttemptTime = Time.RealTime + retryDelay;
            m_pendingJoinRequest = m_activeJoinRequest;
            m_isLoadingDownloadedWorld = true;
            try
            {
                Log.Information($"[ScMP] Host reconnect attempt {m_reconnectAttempts}/" +
                    $"{MaxReconnectAttempts} to {m_activeJoinRequest.ServerAddress}");
                SubmitPendingJoin(m_activeJoinPlayerName, m_activeJoinPlayerClass,
                    m_activeJoinSkinName, m_activeJoinHasPlayerProfile);
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Host reconnect attempt {m_reconnectAttempts} failed: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
        // Network damage arrives after the local physics step. Hold the authoritative velocity for
        // a few frames so local locomotion cannot erase the host-side knockback immediately.
        private void ApplyPendingLocalKnockback()
        {
            if (m_pendingLocalKnockbackUntil <= 0.0) return;
            double remaining = m_pendingLocalKnockbackUntil - Time.RealTime;
            if (remaining <= 0.0)
            {
                m_pendingLocalKnockbackUntil = 0.0;
                m_pendingLocalKnockbackVelocity = Vector3.Zero;
                return;
            }

            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            ComponentBody body = localPlayer?.ComponentBody;
            if (body == null) return;
            float blend = MathUtils.Clamp((float)(remaining / LocalKnockbackHoldDuration), 0.25f, 1f);
            body.Velocity = Vector3.Lerp(body.Velocity, m_pendingLocalKnockbackVelocity, blend);
            m_localInputBodyVelocity = body.Velocity;
        }

        private object[] HandleLoading(object[] args)
        {
            // Source: Survivalcraft/Game/Program.cs:Program.Initialize
            // Source: Survivalcraft/Game/LoadingManager.cs:LoadingManager.ReplaceItem
            Game.LoadingManager.QueueItem("Load ScMultiplayer Chinese Font",
                MultiplayerChineseFont.Load);
            if (!Game.LoadingManager.ReplaceItem("Initialize PlayScreen", delegate
            {
                ScreensManager.AddScreen("Play", new SuPlayScreen());
                // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.PlayerScreen
                ScreensManager.AddScreen("ScMultiplayerPlayer", new SuNetworkPlayerScreen());
            }))
            {
                throw new InvalidOperationException("Loading item 'Initialize PlayScreen' was not found.");
            }
            return args;
        }

        public object[] HandleGameDatabase(Database database)
        {
            var componentInput = database.FindDatabaseObject(
                new Guid("ec809766-ba61-434e-bfde-e677f506b887"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentInput.Value = "ScMultiplayer.SuComponentInput";

            m_modInjector?.Apply(database, "ScMultiplayer");

            // Source: Pak/Database.xml:ComponentVitalStats.Class
            var componentVitalStats = database.FindDatabaseObject(
                new Guid("aa7f845d-165e-4fff-95f0-453cd4e14cea"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentVitalStats.Value = "ScMultiplayer.SuComponentVitalStats";

            // Source: Pak/Database.xml:ComponentFlu.Class
            var componentFlu = database.FindDatabaseObject(
                new Guid("88c778ff-b238-4303-b1c5-468cb0f6c73a"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentFlu.Value = "ScMultiplayer.SuComponentFlu";

            var subsystemTerrain = database.FindDatabaseObject(
                new Guid("e2636c38-f179-4aa1-b087-ed6920d66e8e"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemTerrain.Value = "ScMultiplayer.SuSubsystemTerrain";

            // Source: Pak/Database.xml:SubsystemSpawn.Class
            var subsystemSpawn = database.FindDatabaseObject(
                new Guid("09091863-1852-4c05-ade0-d57fe04289e3"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemSpawn.Value = "ScMultiplayer.SuSubsystemSpawn";

            // Source: Pak/Database.xml:SubsystemCreatureSpawn.Class
            var subsystemCreatureSpawn = database.FindDatabaseObject(
                new Guid("d3764c71-e1e7-48b1-b12a-17428daad169"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemCreatureSpawn.Value = "ScMultiplayer.SuSubsystemCreatureSpawn";

            // Source: Pak/Database.xml:SubsystemGrassBlockBehavior.Class
            var subsystemGrass = database.FindDatabaseObject(
                new Guid("e167fcdc-6960-4487-ace1-6a56eecae003"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemGrass.Value = "ScMultiplayer.SuSubsystemGrassBlockBehavior";

            // Source: Pak/Database.xml:SubsystemExplosions.Class
            var subsystemExplosions = database.FindDatabaseObject(
                new Guid("96e79f99-a082-4190-9ab6-835dc49ebbdd"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemExplosions.Value = "ScMultiplayer.SuSubsystemExplosions";

            // Source: Pak/Database.xml:Projectiles.Class
            var subsystemProjectiles = database.FindDatabaseObject(
                new Guid("dafb8e14-11b9-44b7-a208-424b770aeaa9"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemProjectiles.Value = "ScMultiplayer.SuSubsystemProjectiles";

            // Source: Pak/Database.xml:SubsystemPickables.Class
            var subsystemPickables = database.FindDatabaseObject(
                new Guid("32d392de-69c1-4d04-9e0b-5c7463201892"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemPickables.Value = "ScMultiplayer.SuSubsystemPickables";

            // Source: Pak/Database.xml:WhistleBlockBehavior.Class
            var subsystemWhistle = database.FindDatabaseObject(
                new Guid("87c04d2e-b460-4934-a59d-3b63261e16e4"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemWhistle.Value = "ScMultiplayer.SuSubsystemWhistleBlockBehavior";

            // Source: Mod/WatchMod/Plug/WatchMod.cs:WatchMod.HandleGameDatabase
            // Register an independent player component instead of replacing SubsystemGameWidgets.
            var uiTemplate = new DatabaseObject(
                database.FindDatabaseObjectType("ComponentTemplate", true),
                new Guid("61f1848d-baa7-49b1-9652-66410aef1901"),
                "ScMultiplayerUI", null);
            uiTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
                database.FindDatabaseObjectType("ComponentTemplate", true), true);
            uiTemplate.NestingParent = database.FindDatabaseObject(
                "Gameplay", database.FindDatabaseObjectType("Folder", true), true);

            var uiClass = new DatabaseObject(
                database.FindDatabaseObjectType("Parameter", true),
                new Guid("a49522cb-eaf2-47de-acf5-43d20a035f25"),
                "Class", "ScMultiplayer.MultiplayerUiComponent");
            uiClass.NestingParent = uiTemplate;

            var uiMember = new DatabaseObject(
                database.FindDatabaseObjectType("MemberComponentTemplate", true),
                new Guid("e9d71741-c8ef-4b38-b423-e49b01b3ae5d"),
                "ScMultiplayerUI", null);
            uiMember.ExplicitInheritanceParent = uiTemplate;
            uiMember.NestingParent = database.FindDatabaseObject(
                "Player", database.FindDatabaseObjectType("EntityTemplate", true), true);

            Log.Information("[ScMP] Database hooks applied");
            return new object[] { true, database };
        }

        // ====================================================================
        // Update
        // ====================================================================
        private void UpdateFrame(float dt)
        {
            m_remoteServerDirectory?.Update();
            EnsureNetworkComponentPlayers();
            EnsureLocalPlayerRecordApplied();
            connectionSM.Update();
            downloadSM.Update();
            UpdatePendingLocalGameCreation();
            UpdateHostReconnect();
            UpdateHostJoinRequests();
            UpdateAutoHostCurrentWorld();
            UpdateWorldTransferBusyStatus();
            // Source: Survivalcraft/Game/Program.cs:Program.Run
            // Downloading clients have no Project yet, so repair requests must run from the
            // menu/loading frame path instead of UpdateWorldSubsystem.
            if (!IsHost && client?.IsConnected == true && m_incomingWorldTransfers.Count > 0)
                RequestMissingWorldTransferChunks();
            else if (!IsHost && client?.IsConnected == true && m_isLoadingDownloadedWorld &&
                Time.RealTime >= m_nextWorldTransferManifestRequestTime)
            {
                m_nextWorldTransferManifestRequestTime =
                    Time.RealTime + WorldTransferRepairInterval;
                NetworkMessageSender.SendPakWorldRepairRequest(
                    new GamePakWorldRepairRequestMessage
                    {
                        TransferId = 0,
                        RequestManifest = true
                    });
            }
            // Source: Survivalcraft/Game/Program.cs:Program.Run
            // Frame.Update runs after ScreensManager.Update, so all native game updates for this
            // rendered frame are complete. Keep networking outside SubsystemUpdate's catch-up
            // loop and execute it exactly once here.
            Project project = GameManager.Project;
            if (project != null)
            {
                MaintainMultiplayerTimeFlow(project);
                // Source: Survivalcraft/Game/GameScreen.cs:GameScreen.Update
                // Settings and help screens remove GameScreen from the update hierarchy. A room
                // host must continue advancing the authoritative Project exactly once per frame.
                if (IsHost && client?.IsConnected == true &&
                    m_lastProjectSimulationFrameIndex != Time.FrameIndex)
                    GameManager.UpdateProject();
                UpdateWorldSubsystem(dt, project);
            }
            UpdateClientSuspensionState(project);
            MaintainHostAimPresentation();
            RenderRemotePlayers();
            UpdateNetworkStatsOverlay();
        }

        public bool ShouldSuppressClientInput =>
            !IsHost && client?.IsConnected == true && m_clientTerrainRecoveryActive;

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.UpdateProject
        public void NotifyProjectSimulationStep(Project project)
        {
            if (project != null && ReferenceEquals(project, GameManager.Project))
                m_lastProjectSimulationFrameIndex = Time.FrameIndex;
        }

        // Source: Engine/Window.cs:Window.Deactivated
        private void HandleWindowDeactivated()
        {
            if (!IsHost && client?.IsConnected == true)
            {
                m_clientWindowDeactivated = true;
                m_clientSuspensionRequested = true;
                m_clientTerrainRecoveryActive = true;
            }
        }

        // Source: Engine/Window.cs:Window.Activated
        private void HandleWindowActivated()
        {
            if (!m_clientWindowDeactivated) return;
            m_clientWindowDeactivated = false;
            if (!IsHost && client?.IsConnected == true)
                m_clientTerrainRecoveryPending = true;
        }

        // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.CurrentScreen
        private void UpdateClientSuspensionState(Project project)
        {
            if (m_clientSuspensionRequested)
            {
                m_clientSuspensionRequested = false;
                BeginClientTerrainSuspension();
            }
            bool eligible = !IsHost && client?.IsConnected == true && project != null &&
                !m_isLoadingDownloadedWorld && m_pendingWorldReadyTransferId <= 0;
            bool gameScreenActive = eligible && ScreensManager.CurrentScreen is GameScreen;
            if (!eligible)
            {
                m_clientGameplayScreenObserved = false;
                m_wasClientGameScreenActive = false;
                return;
            }
            if (!m_clientGameplayScreenObserved)
            {
                if (gameScreenActive)
                {
                    m_clientGameplayScreenObserved = true;
                    m_wasClientGameScreenActive = true;
                }
                return;
            }
            if (m_wasClientGameScreenActive && !gameScreenActive)
                BeginClientTerrainSuspension();
            else if (!m_wasClientGameScreenActive && gameScreenActive &&
                m_clientTerrainRecoveryActive)
                m_clientTerrainRecoveryPending = true;
            m_wasClientGameScreenActive = gameScreenActive;
        }

    private void BeginClientTerrainSuspension()
    {
        if (IsHost || client?.IsConnected != true || !m_clientGameplayScreenObserved)
            return;
        m_clientTerrainRecoveryActive = true;
        // Queue the request immediately. If the Android lifecycle does not emit an Activated
        // event after returning to the game, waiting for that callback leaves input suppressed
        // forever with no recovery request in flight.
        m_clientTerrainRecoveryPending = true;
            m_clientTerrainRecoveryRequestInFlight = false;
            m_clientTerrainRecoveryTarget = -1;
            m_clientTerrainRecoveryAcknowledged = -1;
            m_clientTerrainRecoveryReady = -1;
            m_localPlayerInput = default;
            m_localInputResendsRemaining = 0;
            m_localAimActive = false;
            m_localAimSlot = -1;
            m_localTerrainDigIntents.Clear();
            m_pendingTerrainPredictions.Clear();
            m_pendingTerrainPredictionCells.Clear();
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.ProcessEndOfFrameActions
        private void UpdateClientTerrainRecoveryAfterNetworkActions()
        {
            if (IsHost || client?.IsConnected != true || GameManager.Project == null ||
                m_isLoadingDownloadedWorld || m_pendingWorldReadyTransferId > 0)
                return;

            bool gameScreenActive = ScreensManager.CurrentScreen is GameScreen;
            if (gameScreenActive && m_clientTerrainRecoveryActive &&
                !m_clientTerrainRecoveryPending && m_clientTerrainRecoveryTarget < 0 &&
                m_clientTerrainRecoveryReady < 0 && !m_clientTerrainRecoveryRequestInFlight)
                m_clientTerrainRecoveryPending = true;
            if (!m_clientTerrainRecoveryActive && SuSubsystemTerrain.HasBufferedSequenceGap())
            {
                if (m_clientTerrainGapDetectedTime <= 0.0)
                    m_clientTerrainGapDetectedTime = Time.RealTime;
                else if (Time.RealTime - m_clientTerrainGapDetectedTime >=
                    TerrainGapRecoveryDelay)
                {
                    m_clientTerrainRecoveryActive = true;
                    m_clientTerrainRecoveryPending = true;
                }
            }
            else if (!SuSubsystemTerrain.HasBufferedSequenceGap())
            {
                m_clientTerrainGapDetectedTime = 0.0;
            }

            if (m_clientTerrainRecoveryPending && gameScreenActive)
                SendClientTerrainRecoveryRequest();

            long applied = SuSubsystemTerrain.LastAppliedTerrainSequence;
            if (m_clientTerrainRecoveryTarget >= 0 &&
                applied >= m_clientTerrainRecoveryTarget &&
                m_clientTerrainRecoveryAcknowledged < m_clientTerrainRecoveryTarget)
            {
                m_clientTerrainRecoveryAcknowledged = m_clientTerrainRecoveryTarget;
                SendTerrainRecoveryMessage(0, new TerrainRecoveryMessage
                {
                    Stage = TerrainRecoveryStage.Acknowledge,
                    LastAppliedSequence = applied,
                    HeadSequence = m_clientTerrainRecoveryTarget,
                    ServerStep = client.Step
                });
            }

            if (m_clientTerrainRecoveryReady >= 0 &&
                applied >= m_clientTerrainRecoveryReady)
            {
                Log.Information($"[ScMP] Terrain recovery complete: Sequence={applied}");
                m_clientTerrainRecoveryActive = false;
                m_clientTerrainRecoveryPending = false;
                m_clientTerrainRecoveryRequestInFlight = false;
                m_clientTerrainRecoveryTarget = -1;
                m_clientTerrainRecoveryAcknowledged = -1;
                m_clientTerrainRecoveryReady = -1;
                m_clientTerrainGapDetectedTime = 0.0;
            }
        }

        private void SendClientTerrainRecoveryRequest()
        {
            m_clientTerrainRecoveryPending = false;
            m_clientTerrainRecoveryRequestInFlight = true;
            m_clientTerrainRecoveryTarget = -1;
            m_clientTerrainRecoveryAcknowledged = -1;
            m_clientTerrainRecoveryReady = -1;
            long applied = SuSubsystemTerrain.LastAppliedTerrainSequence;
            var request = new TerrainRecoveryMessage
            {
                Stage = TerrainRecoveryStage.Request,
                LastAppliedSequence = applied,
                ServerStep = client.Step,
                BufferedRanges = SuSubsystemTerrain.GetBufferedSequenceRanges(
                    applied, MaximumTerrainRecoveryRanges)
            };
            SendTerrainRecoveryMessage(0, request);
            Log.Information($"[ScMP] Terrain recovery requested: Applied={applied}, " +
                $"BufferedRanges={request.BufferedRanges.Count}");
        }

        private static void SendTerrainRecoveryMessage(int targetClientId,
            TerrainRecoveryMessage message)
        {
            if (client?.IsConnected != true || message == null) return;
            client.SendDirectInput(targetClientId,
                Message.WriteWithSender(message, client.Address), sequenced: true);
        }

        // Source: Survivalcraft/Game/PerformanceManager.cs:PerformanceManager.Draw
        private void UpdateNetworkStatsOverlay()
        {
            EnsureNetworkStatsLabel();
            if (m_networkStatsLabel == null) return;
            float rootScale = MathUtils.Max(
                ScreensManager.RootWidget?.GlobalScale ?? 1f, 0.01f);
            float displayScale = MathUtils.Round(
                MathUtils.Clamp(rootScale, 1f, 4f));
            float widgetScaleCompensation = displayScale / rootScale;
            BitmapFont statsFont = BitmapFont.DebugFont;
            float lineHeight = (statsFont.GlyphHeight + statsFont.Spacing.Y) *
                statsFont.Scale;
            m_networkStatsLabel.FontScale = widgetScaleCompensation;
            m_networkStatsLabel.Margin = new Vector2(
                0f, lineHeight * widgetScaleCompensation);
            bool visible = SettingsManager.DisplayFpsCounter &&
                client?.IsConnected == true;
            m_networkStatsLabel.IsVisible = visible;
            if (!visible || Time.RealTime < m_nextNetworkStatsUpdateTime) return;
            m_nextNetworkStatsUpdateTime = Time.RealTime + 1.0;
            ReadNetworkStats(out float throughputBytesPerSecond, out float latencyMs,
                out int syncQueue, out int applyQueue, out int reliableQueue,
                out float retransmitPercent);
            m_networkStatsLabel.Text = string.Format(CultureInfo.InvariantCulture,
                "NET {0}, {1:0}ms, Retr {5:0.0}%\nQ Sync {2}, Apply {3}, Rel {4}",
                FormatNetworkThroughput(throughputBytesPerSecond), latencyMs,
                syncQueue, applyQueue, reliableQueue,
                retransmitPercent);
        }

        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticStats.BytesSent/BytesReceived
        private static string FormatNetworkThroughput(float bytesPerSecond)
        {
            if (bytesPerSecond >= 1024f * 1024f)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0}MB/s", bytesPerSecond / (1024f * 1024f));
            if (bytesPerSecond >= 1024f)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0}KB/s", bytesPerSecond / 1024f);
            return string.Format(CultureInfo.InvariantCulture, "{0:0}B/s", bytesPerSecond);
        }

        private void EnsureNetworkStatsLabel()
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null) return;
            if (m_networkStatsLabel == null)
            {
                BitmapFont font = BitmapFont.DebugFont;
                float lineHeight = (font.GlyphHeight + font.Spacing.Y) * font.Scale;
                m_networkStatsLabel = new LabelWidget
                {
                    Name = "ScMultiplayer.NetworkStats",
                    Font = font,
                    FontScale = 1f,
                    TextureLinearFilter = false,
                    Color = Color.White,
                    HorizontalAlignment = WidgetAlignment.Far,
                    VerticalAlignment = WidgetAlignment.Near,
                    TextAnchor = TextAnchor.Right,
                    Margin = new Vector2(0f, lineHeight),
                    IsHitTestVisible = false,
                    IsVisible = false
                };
            }
            if (m_networkStatsLabel.ParentWidget == null)
                root.Children.Add(m_networkStatsLabel);
        }

        private void ReadNetworkStats(out float throughputBytesPerSecond, out float latencyMs,
            out int syncQueue, out int applyQueue, out int reliableQueue,
            out float retransmitPercent)
        {
            throughputBytesPerSecond = 0f;
            latencyMs = 0f;
            retransmitPercent = 0f;
            syncQueue = NetworkMessageSender.PendingSyncBatchCount;
            applyQueue = m_endOfFrameActions.Count;
            reliableQueue = 0;
            DiagnosticStats stats = IsHost ? m_serverNetworkStats : m_clientNetworkStats;
            if (stats != null)
            {
                long totalBytes = Math.Max(0L,
                    Volatile.Read(ref stats.BytesSent) + Volatile.Read(ref stats.BytesReceived));
                double now = Time.RealTime;
                if (m_lastNetworkByteSampleTime > 0.0 && now > m_lastNetworkByteSampleTime)
                {
                    long deltaBytes = totalBytes - m_lastNetworkByteSample;
                    if (deltaBytes >= 0)
                        throughputBytesPerSecond = (float)(deltaBytes /
                            (now - m_lastNetworkByteSampleTime));
                }
                m_lastNetworkByteSample = totalBytes;
                m_lastNetworkByteSampleTime = now;
            }
            if (IsHost && server?.Peer != null)
            {
                foreach (ServerClient remote in GetConnectedRemoteClients())
                {
                    PeerData peer = server.Peer.FindPeer(remote.Address);
                    if (peer == null) continue;
                    latencyMs = MathUtils.Max(latencyMs, peer.Ping * 1000f);
                    reliableQueue = Math.Max(reliableQueue,
                        server.Peer.Comm.GetUnackedPacketsCount(peer.Address));
                    retransmitPercent = MathUtils.Max(retransmitPercent,
                        (float)(100.0 * server.Peer.Comm.GetPacketLossRate(
                            peer.Address)));
                }
                return;
            }
            PeerData connected = client?.Peer?.ConnectedTo;
            if (connected == null) return;
            latencyMs = connected.Ping * 1000f;
            reliableQueue = client.Peer.Comm.GetUnackedPacketsCount(connected.Address);
            retransmitPercent = (float)(100.0 *
                client.Peer.Comm.GetPacketLossRate(connected.Address));
        }

        // Source: Survivalcraft/Game/GameLoadingScreen.cs:GameLoadingScreen.Enter
        // Source: ScMultiplayer.CreateRoomFromCurrentWorld
        private void UpdateAutoHostCurrentWorld()
        {
            if (!ScMultiplayerSettings.AutoCreateRoomFromCurrentWorld)
            {
                m_autoHostProject = null;
                m_autoHostAttempted = false;
                return;
            }

            Project project = GameManager.Project;
            if (project == null)
                return;
            if (!ReferenceEquals(m_autoHostProject, project))
            {
                m_autoHostProject = project;
                m_autoHostAttempted = false;
                m_nextAutoHostAttemptTime = 0.0;
            }
            if (client?.IsConnected == true || client?.IsConnecting == true || m_createRoomPending ||
                ScreensManager.IsAnimating || ScreensManager.CurrentScreen is not GameScreen ||
                (m_autoHostAttempted && Time.RealTime < m_nextAutoHostAttemptTime))
            {
                return;
            }

            m_autoHostAttempted = true;
            m_nextAutoHostAttemptTime = Time.RealTime + 10.0;
            try
            {
                SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
                m_createRoomPending = true;
                CreateRoomFromCurrentWorld(gameInfo);
            }
            catch (Exception ex)
            {
                m_createRoomPending = false;
                Log.Error("[ScMP] Automatic room creation failed: " + ex.Message);
            }
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        // Called once after native world updates, independently of SubsystemUpdate.UpdatesPerFrame.
        public void UpdateWorldSubsystem(float dt, Project project)
        {
            if (project == null || !ReferenceEquals(GameManager.Project, project)) return;
            // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.UpdatesPerFrame
            // Multiple logical catch-up steps can run in one rendered frame. Real-time network
            // collection and presentation must execute only once for that frame.
            if (m_lastWorldUpdateFrameIndex == Time.FrameIndex) return;
            m_lastWorldUpdateFrameIndex = Time.FrameIndex;
            MaintainMultiplayerTimeFlow(project);
            MaintainRemoteTerrainLocations(project);
            ObserveLocalPlayerRespawn(project);
            if (!ReferenceEquals(m_frameProject, project))
            {
                DetachHostPickableEvents();
                m_frameProject = project;
                m_hasObservedClientHealth = false;
                m_hasAuthoritativeLocalInventory = false;
                m_lastAuthoritativeLocalInventoryTick = 0;
                m_authoritativeLocalSlotValues = Array.Empty<int>();
                m_authoritativeLocalSlotCounts = Array.Empty<int>();
                m_containerStates.Clear();
                m_remoteProjectiles.Clear();
                m_hostProjectileIds.Clear();
                m_clientPredictedProjectiles.Clear();
                m_displayedProjectileHits.Clear();
                m_pendingTerrainPredictions.Clear();
                m_pendingTerrainPredictionCells.Clear();
                m_processedTerrainDigRequests.Clear();
                m_localTerrainDigIntents.Clear();
                m_hostPlayerPokingPhases.Clear();
                m_hostPlayerPokeSequences.Clear();
                m_playerWhistleSequences.Clear();
                m_equipmentAuthorityRevisions.Clear();
                m_lastClientEquipmentRevisions.Clear();
                m_lastReceivedEquipmentRevisions.Clear();
                m_lastEquipmentSnapshots.Clear();
                m_equipmentSynchronizedClients.Clear();
                m_localEquipmentRevision = 0;
                m_disabledClientContainerUpdates.Clear();
                if (!IsHost) m_isLoadingDownloadedWorld = false;
                if (!IsHost)
                    SuSubsystemTerrain.ConfigureTerrainSequence(
                        m_pendingTerrainSequenceBaseline);
                ApplyHostRandomStates(project);
                foreach (var pending in m_pendingNetworkPlayers.ToArray())
                {
                    m_pendingNetworkPlayerIdentities.TryGetValue(pending.Key, out string identity);
                    CreateNetworkPlayer(pending.Key, pending.Value, identity);
                }
                if (!IsHost && m_shouldCreateHostAvatar && !m_networkPlayerData.ContainsKey(0))
                    CreateNetworkPlayer(0, "Host", GetNetworkRecordKey(0));
                if (!IsHost && m_pendingWorldReadyTransferId > 0)
                {
                    NetworkMessageSender.SendPakWorldReady(
                        new GamePakWorldReadyMessage(m_pendingWorldReadyTransferId,
                            GamePakWorldReadyStage.ProjectReady));
                    Log.Information($"[ScMP] Client project ready: Transfer={m_pendingWorldReadyTransferId}");
                    if (m_joinRoomBusyDialog != null)
                        m_joinRoomBusyDialog.SmallMessage =
                            "Connected.\r\nWorld loaded.\r\nApplying host changes...";
                }
                AttachHostPickableEvents(project);
                QueueRunawayCreatureCleanup(project);
                m_nextRunawayCreatureCheckTime = Time.RealTime + 2.0;
                Log.Information("[ScMP] Multiplayer project runtime initialized");
            }
            SanitizeRunawayCreatureState(project);
            ProcessRunawayCreatureCleanup(project);
            if (IsHost)
            {
                BroadcastHostPlayerPokes();
                CaptureHostRemoteKnockbacks();
                ApplyHostRemoteFollowVelocities();
            }
            else
            {
                SuppressClientRandomLightning(project);
                ApplyPendingLocalKnockback();
                UpdateRemoteAnimalPresentations(dt);
                UpdateRemotePickablePresentations(dt);
                UpdateRemotePlayerPresentations(dt);
                UpdatePendingTerrainPredictions();
            }

            // Source: Engine/Time.cs:Time.RealTime
            // Real time avoids duplicate network time when SubsystemUpdate runs multiple game
            // updates in one rendered frame. The accumulator preserves fractional 32Hz pulses.
            double now = Time.RealTime;
            if (m_lastSyncUpdateTime <= 0.0)
                m_lastSyncUpdateTime = now;
            float elapsed = (float)MathUtils.Clamp(
                now - m_lastSyncUpdateTime, 0.0, SyncPulseDuration * MaxSyncPulsesPerUpdate);
            m_lastSyncUpdateTime = now;
            m_syncPulseAccumulator += elapsed;

            int ticks = 0;
            while (m_syncPulseAccumulator >= SyncPulseDuration && ticks < MaxSyncPulsesPerUpdate)
            {
                m_syncPulseAccumulator -= SyncPulseDuration;
                TriggerNetworkTick(SyncPulseDuration);
                ticks++;
            }
            if (ticks == MaxSyncPulsesPerUpdate && m_syncPulseAccumulator >= SyncPulseDuration)
                m_syncPulseAccumulator = 0f;

        }

        // Source: ScMultiplayer.Update keyboard J flow
        // Source: ConsoleMod.ConsoleSubsystemGameWidgets.Update touch-button command pattern
        public void ShowCreateRoomDialog()
        {
            var gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo == null || server == null)
            {
                DialogsManager.ShowDialog(null, new MessageDialog("Network", "No local server or world is available.", "OK", null, null));
                return;
            }
            if (m_createRoomPending)
                return;
            if (client?.IsConnected == true)
                return;

            // Source: Survivalcraft/Game/GameManager.cs:GameManager.DisposeProject
            // Keep a confirmation against accidental CR taps. After confirmation, the visible
            // save/unload/reload transition replaces busy and success dialogs.
            DialogsManager.ShowDialog(null,
                new MessageDialog("Create Room", gameInfo.WorldSettings.Name,
                    "Create", "Cancel", delegate (MessageDialogButton button)
                    {
                        if (button != MessageDialogButton.Button1) return;
                        try
                        {
                            m_createRoomPending = true;
                            CreateRoomFromCurrentWorld(gameInfo);
                        }
                        catch (Exception ex)
                        {
                            FinishCreateRoomFeedback(false, ex.Message);
                        }
                    }));
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        // Source: Mod/WeatherTips/Subsystem/SuSubsystemWeather.cs:SuSubsystemWeather.Update
        public void ShowTalkDialog()
        {
            if (client == null || !client.IsConnected)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Talk", "Join or create a room before sending messages.", "OK", null, null));
                return;
            }

            var dialog = new TextBoxDialog("Talk", "", 125, delegate (string text)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ChatMessage message = NetworkMessageSender.SendChatMessage(
                        GetLocalPlayerName(), GetLocalPlayerIdentity(), text.Trim());
                    DisplayChatMessage(message, client.ClientID);
                }
            });
            BitmapFont chatFont = MultiplayerChineseFont.TextInputFont ??
                MultiplayerChineseFont.Font;
            Widget textBox = dialog.Children.Find("TextBoxDialog.TextBox", false);
            if (chatFont != null && textBox != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    textBox, "Font", chatFont, textBox.GetType());
                ModManager.ModParentField.ModifyParentField(
                    textBox, "TextureLinearFilter", true, textBox.GetType());
            }
            DialogsManager.ShowDialog(ScreensManager.RootWidget, dialog);
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.ComponentPlayers
        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Position
        public void ShowJoinedPlayerInformation()
        {
            if (!IsInRoom || GameManager.Project == null)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Joined Players", "Join or create a room first.", "OK", null, null));
                return;
            }

            var entries = new List<string>();
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players != null)
            {
                foreach (ComponentPlayer componentPlayer in players.ComponentPlayers.Where(player =>
                    player?.PlayerData != null &&
                    !m_networkPlayerData.Values.Contains(player.PlayerData)))
                {
                    string role = IsHost ? "Host" : "You";
                    entries.Add(FormatJoinedPlayer(
                        componentPlayer.PlayerData.Name,
                        role,
                        componentPlayer.ComponentBody.Position));
                }
            }
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.OrderBy(pair => pair.Key))
            {
                PlayerData playerData = item.Value;
                Vector3 position = playerData?.ComponentPlayer?.ComponentBody?.Position ??
                    playerData?.SpawnPosition ?? Vector3.Zero;
                string role = item.Key == 0 ? "Host" : $"Client {item.Key}";
                entries.Add(FormatJoinedPlayer(playerData?.Name, role, position));
            }

            if (entries.Count == 0)
                entries.Add("No joined player information is available.");
            var dialog = new ListSelectionDialog(
                "Joined Players",
                entries,
                44f,
                item => CreateMultiplayerTextLabel(item.ToString(), 1f,
                    WidgetAlignment.Near),
                item => { });
            float availableWidth = MathUtils.Max(
                ScreensManager.RootWidget.ActualSize.X - 40f, 0f);
            dialog.ContentSize = new Vector2(
                MathUtils.Min(800f, availableWidth), dialog.ContentSize.Y);
            DialogsManager.ShowDialog(null, dialog);
        }

        private static string FormatJoinedPlayer(string name, string role, Vector3 position)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Player";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} | {1} | X {2:0.0} Y {3:0.0} Z {4:0.0}",
                name,
                role,
                position.X,
                position.Y,
                position.Z);
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.m_moreContentsWidget
        public void ShowMultiplayerManagementDialog()
        {
            if (GameManager.Project == null)
                return;

            List<ServerClient> connected = GetConnectedRemoteClients();
            var actions = new List<Tuple<string, Action>>();
            string roomState = IsHost && client?.IsConnected == true
                ? $"Hosting Room {client.GameID}"
                : client?.IsConnected == true
                    ? $"Connected to Room {client.GameID}"
                    : "No Active Room";
            actions.Add(Tuple.Create(roomState, (Action)ShowRoomStatus));
            if (client?.IsConnected != true)
                actions.Add(Tuple.Create("Create Room from Current World", (Action)ShowCreateRoomDialog));
            if (IsHost && client?.IsConnected == true)
            {
                string approval = ScMultiplayerSettings.AutoApproveJoinRequests ? "On" : "Off";
                actions.Add(Tuple.Create(
                    "Auto Approve Joins: " + approval,
                    (Action)ToggleAutoApproveJoinRequests));
                string autoHost = ScMultiplayerSettings.AutoCreateRoomFromCurrentWorld
                    ? "On"
                    : "Off";
                actions.Add(Tuple.Create(
                    "Auto Host Current World: " + autoHost,
                    (Action)ToggleAutoHostCurrentWorld));
            }
            if (IsHost && client?.IsConnected == true)
            {
                actions.Add(Tuple.Create(
                    $"Connected Players ({connected.Count})",
                    (Action)ShowConnectedPlayersDialog));
                actions.Add(Tuple.Create(
                    $"Pending Join Requests ({m_hostJoinRequests.Count})",
                    (Action)ShowPendingJoinRequestsDialog));
            }
            if (client?.IsConnected == true)
                actions.Add(Tuple.Create(
                    $"Talk ({m_recentChatMessages.Count})",
                    (Action)ShowRecentMessagesDialog));

            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Multiplayer",
                actions,
                60f,
                item => ((Tuple<string, Action>)item).Item1,
                item => ((Tuple<string, Action>)item).Item2()));
        }

        private void ShowRecentMessagesDialog()
        {
            ChatMessage[] messages = m_recentChatMessages.Reverse().ToArray();
            if (messages.Length == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Talk", "No recent messages.", "OK", null, null));
                return;
            }
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Talk",
                messages,
                60f,
                item => CreateMultiplayerTextLabel(
                    FormatChatMessage((ChatMessage)item), 0.82f,
                    WidgetAlignment.Near),
                item =>
                {
                    ChatMessage message = (ChatMessage)item;
                    string sender = string.IsNullOrWhiteSpace(message.Sender)
                        ? "Player"
                        : message.Sender;
                    var messageDialog = new MessageDialog(
                        sender, message.Text, "OK", null, null);
                    ApplyChatDialogFont(messageDialog);
                    DialogsManager.ShowDialog(null, messageDialog);
                }));
        }

        private static LabelWidget CreateMultiplayerTextLabel(string text,
            float fontScale, WidgetAlignment horizontalAlignment)
        {
            return new LabelWidget
            {
                Text = text ?? string.Empty,
                Font = MultiplayerChineseFont.Font ??
                    ContentManager.Get<BitmapFont>("Fonts/Pericles18"),
                FontScale = fontScale,
                TextureLinearFilter = true,
                HorizontalAlignment = horizontalAlignment,
                VerticalAlignment = WidgetAlignment.Center,
                TextAnchor = horizontalAlignment == WidgetAlignment.Near
                    ? TextAnchor.Left
                    : TextAnchor.Center
            };
        }

        private static void ApplyChatDialogFont(MessageDialog dialog)
        {
            if (dialog == null || MultiplayerChineseFont.Font == null) return;
            LabelWidget title = dialog.Children.Find<LabelWidget>(
                "MessageDialog.LargeLabel", true);
            LabelWidget message = dialog.Children.Find<LabelWidget>(
                "MessageDialog.SmallLabel", true);
            if (title != null)
            {
                title.Font = MultiplayerChineseFont.Font;
                title.TextureLinearFilter = true;
            }
            if (message != null)
            {
                message.Font = MultiplayerChineseFont.Font;
                message.TextureLinearFilter = true;
            }
        }

        private static string FormatChatMessage(ChatMessage message)
        {
            string sender = string.IsNullOrWhiteSpace(message?.Sender)
                ? "Player"
                : message.Sender;
            return $"[{message.Timestamp.ToLocalTime():HH:mm:ss}] {sender}: {message.Text}";
        }

        private void ShowRoomStatus()
        {
            string status;
            if (IsHost && client?.IsConnected == true)
            {
                status = $"Room ID: {client.GameID}\r\n" +
                    $"World: {SuPlayScreen.WorldDataName}\r\n" +
                    $"Connected players: {GetConnectedRemoteClients().Count}\r\n" +
                    $"Pending requests: {m_hostJoinRequests.Count}\r\n" +
                    "Auto approve: " +
                    (ScMultiplayerSettings.AutoApproveJoinRequests ? "On" : "Off");
            }
            else if (client?.IsConnected == true)
            {
                status = $"Connected to room {client.GameID}.";
            }
            else
            {
                status = "Create a room from the current world to begin hosting.";
            }
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Multiplayer", status, "OK", null, null));
        }

        private void ToggleAutoApproveJoinRequests()
        {
            bool value = !ScMultiplayerSettings.AutoApproveJoinRequests;
            ScMultiplayerSettings.SetAutoApproveJoinRequests(value);
            if (value)
            {
                Dialog active = m_activeJoinDecisionDialog;
                m_activeJoinDecisionDialog = null;
                m_activeJoinDecisionClientId = -1;
                if (active != null && DialogsManager.Dialogs.Contains(active))
                    DialogsManager.HideDialog(active);
                foreach (HostJoinRequest request in m_hostJoinRequests.Values.ToArray())
                    ApproveHostJoinRequest(request);
            }
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Auto Approve Joins",
                value
                    ? "New join requests will be accepted automatically."
                    : "The host must allow, reject or defer each join request.",
                "OK", null, null));
        }

        private void ToggleAutoHostCurrentWorld()
        {
            bool value = !ScMultiplayerSettings.AutoCreateRoomFromCurrentWorld;
            ScMultiplayerSettings.SetAutoCreateRoomFromCurrentWorld(value);
            m_autoHostAttempted = false;
            m_nextAutoHostAttemptTime = 0.0;
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Auto Host Current World",
                value
                    ? "A room will be created whenever a world finishes loading."
                    : "Loaded worlds will no longer be hosted automatically.",
                "OK", null, null));
        }

        private void ShowPendingJoinRequestsDialog()
        {
            HostJoinRequest[] requests = m_hostJoinRequests.Values
                .OrderBy(item => item.ReceivedTime)
                .ToArray();
            if (requests.Length == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Pending Join Requests",
                    "No players are waiting for approval.",
                    "OK", null, null));
                return;
            }
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Pending Join Requests",
                requests,
                60f,
                item =>
                {
                    var request = (HostJoinRequest)item;
                    return GetHostJoinRequestLabel(request) +
                        (request.Deferred ? " | Later" : string.Empty);
                },
                item =>
                {
                    var request = (HostJoinRequest)item;
                    request.Deferred = false;
                    ShowHostJoinDecision(request);
                }));
        }

        private void ShowConnectedPlayersDialog()
        {
            List<ServerClient> players = GetConnectedRemoteClients();
            if (players.Count == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Connected Players",
                    "No remote players are connected.",
                    "OK", null, null));
                return;
            }
            DialogsManager.ShowDialog(null, new ListSelectionDialog(
                "Connected Players",
                players,
                60f,
                item => GetConnectedPlayerLabel((ServerClient)item),
                item => ConfirmDisconnectPlayer((ServerClient)item)));
        }

        private void ConfirmDisconnectPlayer(ServerClient player)
        {
            DialogsManager.ShowDialog(null, new MessageDialog(
                "Disconnect Player",
                GetConnectedPlayerLabel(player),
                "Disconnect",
                "Cancel",
                button =>
                {
                    if (button == MessageDialogButton.Button1)
                        DisconnectNetworkClient(player);
                }));
        }

        private List<ServerClient> GetConnectedRemoteClients()
        {
            if (server?.Peer == null || client == null || client.GameID < 0)
                return new List<ServerClient>();
            lock (server.Peer.Lock)
            {
                ServerGame game = server.Games.FirstOrDefault(
                    item => item.GameID == client.GameID);
                return game?.Clients
                    .Where(item => item.ClientID != client.ClientID)
                    .ToList() ?? new List<ServerClient>();
            }
        }

        private string GetConnectedPlayerLabel(ServerClient player)
        {
            string name = null;
            if (player != null &&
                m_clientRecordKeys.TryGetValue(player.ClientID, out string key) &&
                m_playerRecords.TryGetValue(key, out NetworkPlayerRecord record))
            {
                name = record.Name;
            }
            if (string.IsNullOrWhiteSpace(name))
                name = string.IsNullOrWhiteSpace(player?.ClientName) ? "Player" : player.ClientName;
            return $"{name} | Client {player?.ClientID} | {player?.Address}";
        }

        // Source: Mod/Comms/Comms/Peer.cs:Peer.DisconnectPeer
        private void DisconnectNetworkClient(ServerClient player)
        {
            if (!IsHost || player == null || server?.Peer == null)
                return;
            lock (server.Peer.Lock)
            {
                PeerData peer = server.Peer.FindPeer(player.Address);
                if (peer != null)
                    server.Peer.DisconnectPeer(peer);
            }
        }

        public void DisplayChatMessage(ChatMessage message, int clientId)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Text)) return;
            if (!RecordChatMessage(message)) return;
            string identity = string.IsNullOrWhiteSpace(message.SenderIdentity)
                ? clientId.ToString()
                : message.SenderIdentity;
            int hash = 17;
            foreach (char c in identity)
                hash = unchecked(hash * 31 + c);
            Color color = ChatColors[(hash & int.MaxValue) % ChatColors.Length];
            string sender = string.IsNullOrWhiteSpace(message.Sender) ? "Player" : message.Sender;

            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;
            foreach (ComponentPlayer componentPlayer in players.ComponentPlayers)
            {
                if (m_networkPlayerData.Values.Contains(componentPlayer.PlayerData)) continue;
                componentPlayer.ComponentGui.DisplaySmallMessage(
                    sender + ": " + message.Text, color, blinking: true, playNotificationSound: true);
                ApplyLatestChatMessageFont(componentPlayer.ComponentGui);
            }
        }

        // Source: Survivalcraft/Game/MessageWidget.cs:MessageWidget.DisplayMessage
        private static void ApplyLatestChatMessageFont(ComponentGui gui)
        {
            if (gui == null || MultiplayerChineseFont.Font == null) return;
            MessageWidget messageWidget = ModManager.ModParentField
                .GetParentField<MessageWidget>(gui, "m_messageWidget", typeof(ComponentGui));
            LabelWidget label = messageWidget?.Children.LastOrDefault() as LabelWidget;
            if (label != null)
            {
                label.Font = MultiplayerChineseFont.Font;
                label.TextureLinearFilter = true;
            }
        }

        private bool RecordChatMessage(ChatMessage message)
        {
            if (message.MessageId == Guid.Empty)
                message.MessageId = Guid.NewGuid();
            if (!m_recentChatMessageIds.Add(message.MessageId))
                return false;
            m_recentChatMessages.Enqueue(message);
            while (m_recentChatMessages.Count > MaximumRecentChatMessages)
            {
                ChatMessage removed = m_recentChatMessages.Dequeue();
                m_recentChatMessageIds.Remove(removed.MessageId);
            }
            return true;
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.SaveProject
        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ExportWorld
        private void CreateRoomFromCurrentWorld(SubsystemGameInfo gameInfo)
        {
            PrepareClientForGameCreation();
            IPEndPoint localServerAddress = GetLocalServerConnectionAddress();
            if (localServerAddress == null)
                throw new InvalidOperationException("The local multiplayer server is not available.");
            string directoryName = gameInfo.DirectoryName;
            // Region files are opened exclusively while a project is running. Always save and
            // unload before export so the room snapshot represents the current running world,
            // rather than an older Play-screen cache.
            GameManager.SaveProject(waitForCompletion: true, showErrorDialog: true);
            GameManager.DisposeProject();
            WorldsManager.UpdateWorldsList();
            WorldInfo worldInfo = WorldsManager.WorldInfos.FirstOrDefault(
                world => world.DirectoryName == directoryName);
            if (worldInfo == null)
                throw new InvalidOperationException("Saved world was not found after unloading the project.");

            using (var stream = new MemoryStream())
            {
                WorldsManager.ExportWorld(worldInfo.DirectoryName, stream);
                SuPlayScreen.WorldData = stream.ToArray();
            }
            SuPlayScreen.WorldDataName = worldInfo.WorldSettings.Name;
            SuPlayScreen.WorldDataLastSaveTime = worldInfo.LastSaveTime;

            var worldMessage = new GameWorldInfoMessage(
                worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime,
                worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode,
                worldInfo.SerializationVersion, server.Address, GetLocalPlayerName(),
                GetLocalPlayerIdentity());
            IsHost = true;
            LastGameDescription = Message.WriteWithSender(worldMessage, client.Address);
            // Source: Mod/Comms/Comms.Drt/Func/Server/Server.cs:Server.Server
            // Every terminal owns a server bound to all interfaces. Connect the local client over
            // loopback; remote clients use the source endpoint returned by Explorer discovery.
            BeginLocalGameCreation(localServerAddress, LastGameDescription);

            if (GameManager.Project == null)
                ScreensManager.SwitchScreen("GameLoading", worldInfo, null);
        }

        // Source: Mod/Comms/Comms/UdpTransmitter.cs:UdpTransmitter.UdpTransmitter
        public static IPEndPoint GetLocalServerConnectionAddress()
        {
            return server == null ? null : new IPEndPoint(IPAddress.Loopback, server.Address.Port);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.Handle
        public static bool IsLocalServerEndpoint(IPEndPoint endpoint)
        {
            return endpoint != null && server != null && endpoint.Port == server.Address.Port &&
                UdpTransmitter.IsLocalIPv4Address(endpoint.Address);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.Dispose
        // A client that left through a failed VPN socket can retain an unusable Peer session.
        // Every manual remote join therefore starts with a new endpoint and handshake GUID.
        private void PrepareClientForRemoteJoin()
        {
            Client previousClient = client;
            if (previousClient?.IsConnected == true)
            {
                try { previousClient.LeaveGame(); }
                catch (Exception ex) { Log.Warning($"[ScMP] Could not leave previous room: {ex.Message}"); }
            }
            try { previousClient?.Dispose(); }
            catch (Exception ex) { Log.Warning($"[ScMP] Could not dispose previous client: {ex.Message}"); }
            client = CreateStartedClient(RemoteConnectionLostPeriod);
            m_localLeaveInProgress = false;
            m_hostDisconnectHandled = false;
        }

        // Source: Comms/Drt/Client.cs:Client.CreateGame
        // A peer can own only one game membership. Close an existing hosted/joined session before
        // every create entry point so repeated CR clicks cannot reuse a joined Peer.
        public void PrepareClientForGameCreation()
        {
            m_pendingLocalCreateDescription = null;
            m_pendingLocalCreateAddress = null;
            m_localCreateAttempts = 0;
            m_activeJoinRequest = null;
            m_reconnectRequested = false;
            m_reconnectPending = false;
            Client previousClient = client;
            if (previousClient?.IsConnected == true)
            {
                foreach (int clientId in m_networkPlayerData.Keys.ToArray())
                    RemoveNetworkPlayer(clientId);
                previousClient.LeaveGame();
            }
            // Source: Mod/Comms/Comms/Comm.cs:Comm.ResetConnection
            // A local Host retry must use a fresh UDP endpoint. Reusing the previous endpoint lets
            // delayed reliable packets replace the new handshake GUID and prevents stable recovery.
            try { previousClient?.Dispose(); }
            catch (Exception ex) { Log.Error($"[ScMP] Failed to dispose previous client: {ex.Message}"); }
            client = CreateStartedClient(LocalHostConnectionLostPeriod);
            LastGameDescription = null;
            ResetTransientNetworkState();
        }

        public void BeginLocalGameCreation(IPEndPoint serverAddress, byte[] description)
        {
            if (serverAddress == null || description == null || description.Length == 0)
                throw new ArgumentException("Local game creation requires an address and description.");
            m_pendingLocalCreateAddress = serverAddress;
            m_pendingLocalCreateDescription = description.ToArray();
            m_localCreateAttempts = 1;
            m_nextLocalCreateAttemptTime = Time.RealTime + LocalCreateRetryInterval;
            client.CreateGame(serverAddress, description, client.Address.Port.ToString());
            Log.Information($"[ScMP] CreateGame attempt 1/{MaximumLocalCreateAttempts}, local={serverAddress}, advertised={server?.Address}");
        }

        private void UpdatePendingLocalGameCreation()
        {
            if (m_pendingLocalCreateDescription == null || client?.IsConnected == true ||
                Time.RealTime < m_nextLocalCreateAttemptTime)
                return;
            if (m_localCreateAttempts >= MaximumLocalCreateAttempts)
            {
                m_pendingLocalCreateDescription = null;
                m_pendingLocalCreateAddress = null;
                Dispatcher.Dispatch(() => FinishCreateRoomFeedback(false,
                    "The local multiplayer server did not respond."));
                return;
            }

            try
            {
                Client previousClient = client;
                try { previousClient?.Dispose(); }
                catch (Exception ex) { Log.Warning($"[ScMP] Failed to dispose create client: {ex.Message}"); }
                client = CreateStartedClient(LocalHostConnectionLostPeriod);
                Message description = Message.Read(m_pendingLocalCreateDescription);
                m_pendingLocalCreateDescription = Message.WriteWithSender(description, client.Address);
                LastGameDescription = m_pendingLocalCreateDescription;
                m_localCreateAttempts++;
                m_nextLocalCreateAttemptTime = Time.RealTime + LocalCreateRetryInterval;
                client.CreateGame(m_pendingLocalCreateAddress, m_pendingLocalCreateDescription,
                    client.Address.Port.ToString());
                Log.Information($"[ScMP] CreateGame retry {m_localCreateAttempts}/{MaximumLocalCreateAttempts}, local={m_pendingLocalCreateAddress}");
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Local CreateGame retry failed: {ex.Message}");
                m_nextLocalCreateAttemptTime = Time.RealTime + LocalCreateRetryInterval;
            }
        }

        // Source: ScMultiplayer.Update keyboard K flow
        // Source: Comms.Drt.Explorer.DiscoveredServers
        public void ShowJoinRoomDialog()
        {
            var games = explorer?.DiscoveredServers?
                .SelectMany(serverDescription => serverDescription.GameDescriptions)
                .ToList() ?? new List<GameDescription>();
            if (games.Count == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog("Network", "No rooms were found.", "OK", null, null));
                return;
            }

            DialogsManager.ShowDialog(null,
                new ListSelectionDialog("Join Room", games, 60f,
                    item =>
                    {
                        var game = (GameDescription)item;
                        var info = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage;
                        return info != null ? info.Name : game.ToString();
                    },
                    item =>
                    {
                        var game = (GameDescription)item;
                        var info = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage;
                        if (info == null) return;
                        BeginJoinGame(game.ServerDescription.Address, game.GameID, info);
                    }));
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.JoinGame
        public void BeginJoinGame(IPEndPoint serverAddress, int gameId, GameWorldInfoMessage worldInfo)
        {
            if (serverAddress == null || worldInfo == null) return;
            PrepareClientForRemoteJoin();
            ShowJoinRoomBusyDialog();
            m_activeJoinRequest = new PendingJoinRequest
            {
                ServerAddress = serverAddress,
                GameId = gameId,
                WorldInfo = worldInfo
            };
            m_pendingJoinRequest = m_activeJoinRequest;
            m_activeJoinPlayerName = GetLocalPlayerName();
            m_activeJoinPlayerClass = PlayerClass.Male;
            m_activeJoinSkinName = null;
            m_activeJoinHasPlayerProfile = false;
            m_reconnectRequested = false;
            m_reconnectPending = false;
            m_reconnectAttempts = 0;
            SubmitPendingJoin(null, PlayerClass.Male, null, hasPlayerProfile: false);
        }

        public void CancelPendingJoin()
        {
            HideJoinRoomBusyDialog();
            m_pendingJoinRequest = null;
            m_activeJoinRequest = null;
            m_reconnectRequested = false;
            m_reconnectPending = false;
        }

        // Source: Survivalcraft/Game/BusyDialog.cs:BusyDialog
        private void ShowJoinRoomBusyDialog()
        {
            if (m_joinRoomBusyDialog != null) return;
            m_joinRoomBusyDialog = new BusyDialog(
                "Joining Room", "Connecting to the host and preparing the world...");
            DialogsManager.ShowDialog(ScreensManager.RootWidget, m_joinRoomBusyDialog);
        }

        private void HideJoinRoomBusyDialog()
        {
            if (m_joinRoomBusyDialog == null) return;
            DialogsManager.HideDialog(m_joinRoomBusyDialog);
            m_joinRoomBusyDialog = null;
        }

        private void UpdateWorldTransferBusyStatus()
        {
            if (m_joinRoomBusyDialog == null || !m_isLoadingDownloadedWorld || IsHost ||
                Time.RealTime < m_nextWorldTransferUiUpdateTime)
                return;
            m_nextWorldTransferUiUpdateTime = Time.RealTime + 0.25;
            IncomingWorldTransfer transfer = m_incomingWorldTransfers.Values
                .OrderByDescending(item => item.TransferId).FirstOrDefault();
            if (transfer == null)
            {
                m_joinRoomBusyDialog.SmallMessage =
                    "Connected.\r\nWaiting for the host world manifest...";
                return;
            }
            double percent = transfer.Chunks.Length > 0
                ? 100.0 * transfer.ReceivedChunkCount / transfer.Chunks.Length
                : 0.0;
            string stage = transfer.RepairRequestCount > 0 &&
                Time.RealTime - transfer.LastProgressTime >= WorldTransferRepairInterval
                    ? $"Recovering missing chunks (request {transfer.RepairRequestCount})..."
                    : "Downloading host world...";
            m_joinRoomBusyDialog.SmallMessage = string.Format(CultureInfo.InvariantCulture,
                "Connected.\r\n{0}\r\n{1} / {2} chunks ({3:0.0}%)\r\n{4:0.00} MB / {5:0.00} MB",
                stage, transfer.ReceivedChunkCount, transfer.Chunks.Length, percent,
                transfer.ReceivedBytes / 1048576.0, transfer.TotalLength / 1048576.0);
        }

        private void SubmitPendingJoin(string playerName, PlayerClass playerClass,
            string skinName, bool hasPlayerProfile)
        {
            PendingJoinRequest pending = m_pendingJoinRequest;
            if (pending?.WorldInfo == null) return;
            ShowJoinRoomBusyDialog();
            if (hasPlayerProfile)
            {
                m_activeJoinPlayerName = string.IsNullOrWhiteSpace(playerName)
                    ? GetLocalPlayerName()
                    : playerName;
                m_activeJoinPlayerClass = playerClass;
                m_activeJoinSkinName = skinName;
                m_activeJoinHasPlayerProfile = true;
            }
            IsHost = false;
            if (client.IsConnected) client.LeaveGame();
            GameWorldInfoMessage info = pending.WorldInfo;
            var joinInfo = new GameWorldInfoMessage(
                info.Name, info.Size, info.LastSaveTime, info.GameMode,
                info.EnvironmentBehaviorMode, info.SerializationVersion, client.Address,
                hasPlayerProfile ? playerName : GetLocalPlayerName(), GetLocalPlayerIdentity(),
                hasPlayerProfile, playerClass, skinName);
            client.JoinGame(pending.ServerAddress, pending.GameId,
                Message.WriteWithSender(joinInfo, client.Address), client.Address.Port.ToString());
        }

        private void TryKickPlayer()
        {
            // 踢出最后一个加入的非房主玩家
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            var allPlayers = subsystemPlayers.ComponentPlayers;

            int hostPlayerIndex = playerMappingManager.GetPlayerIndex(0);
            ComponentPlayer target = null;
            foreach (var p in allPlayers)
            {
                if (p.PlayerData.PlayerIndex != hostPlayerIndex)
                {
                    target = p;
                    break;
                }
            }

            if (target == null) { Log.Information("[ScMP] No players to kick"); return; }

            int targetClientID = playerMappingManager.GetClientId(target.PlayerData.PlayerIndex);
            if (targetClientID <= 0) { Log.Information("[ScMP] Cannot kick player with invalid client ID"); return; }

            Log.Information($"[ScMP] Kicking player ClientID={targetClientID}");
            NetworkMessageSender.SendKickPlayerMessage(targetClientID, "Kicked by host");
        }

        // ====================================================================
        // 32Hz 定时事件
        // ====================================================================
        private void TriggerNetworkTick(float tickDuration)
        {
            // Source: ScMultiplayer.ScMultiplayer.Update
            // Real-time messages share one aligned 1/2/4/8/16/32Hz phase ladder.
            if (!client.IsConnected) return;
            uint pulse = m_syncPulseIndex++;
            bool pulse1Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz1);
            bool pulse2Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz2);
            bool pulse4Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz4);
            bool pulse8Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz8);
            bool pulse16Hz = IsSyncPulse(pulse, NetworkSyncRate.Hz16);
            if (IsHost && pulse1Hz)
            {
                lock (m_terrainJournalLock)
                    TrimHostTerrainJournalLocked(Time.RealTime);
            }
            // Source: ScMultiplayer.cs:AcceptNetworkPlayerJoin
            // A joining client intentionally has no host-side avatar until it reports that the
            // Loading Project screen started. World chunks must therefore run before the
            // no-remote-avatar maintenance fast path.
            if (IsHost) SendPendingWorldTransferChunks();
            if (IsHost && !m_networkPlayerData.Any(item => item.Key > 0))
            {
                // Source: ScMultiplayer.cs:ScMultiplayer.TriggerNetworkTick
                // With no remote recipients, world-object scans and serialization cannot produce
                // useful network state. Keep only persistent host maintenance until a join creates
                // its authoritative avatar, at which point normal synchronization resumes.
                m_playerRecordSaveTime += tickDuration;
                if (m_playerRecordSaveTime >= PlayerRecordSaveInterval)
                {
                    m_playerRecordSaveTime -= PlayerRecordSaveInterval;
                    RefreshHostPlayerRecords();
                    SavePlayerRecords();
                }
                m_terrainMergeTime += tickDuration;
                if (m_terrainMergeTime >= TerrainMergeInterval)
                {
                    m_terrainMergeTime -= TerrainMergeInterval;
                    MergePendingTerrainChanges();
                }
                return;
            }
            NetworkMessageSender.BeginSyncBatch();
            try
            {
            if (IsHost) SendHostLightningEdge();

            m_inventoryKeyframeTime += tickDuration;
            bool inventoryKeyframe = pulse1Hz && m_inventoryKeyframeTime >= 5f;
            if (inventoryKeyframe) m_inventoryKeyframeTime -= 5f;
            bool forceHostInventorySync = IsHost && m_forceHostInventorySync;
            SendGamePlayerPositionMessage(
                pulse1Hz || forceHostInventorySync,
                inventoryKeyframe || forceHostInventorySync);
            if (forceHostInventorySync) m_forceHostInventorySync = false;
            SynchronizePlayerEquipment();
            if (IsHost)
                SendGamePlayerHealthMessage(false);
            else
                SendClientDamageRequest();

            if (pulse2Hz)
                SendGameWorldInfoMessage();

            m_playerProfileSyncTime += tickDuration;
            if (m_playerProfileSyncTime >= PlayerProfileSyncInterval)
            {
                m_playerProfileSyncTime -= PlayerProfileSyncInterval;
                SynchronizePlayerProfiles();
            }

            if (pulse1Hz)
                if (IsHost) SendGamePlayerHealthMessage(true);
            if (IsHost)
            {
                m_playerRecordSaveTime += tickDuration;
                if (m_playerRecordSaveTime >= PlayerRecordSaveInterval)
                {
                    m_playerRecordSaveTime -= PlayerRecordSaveInterval;
                    RefreshHostPlayerRecords();
                    SavePlayerRecords();
                }
                m_terrainMergeTime += tickDuration;
                if (m_terrainMergeTime >= TerrainMergeInterval)
                {
                    m_terrainMergeTime -= TerrainMergeInterval;
                    MergePendingTerrainChanges();
                }
                if (pulse1Hz)
                    BroadcastTerrainRepairs();
            }
            m_fullWorldObjectsSyncTime += tickDuration;
            if (pulse8Hz)
            {
                bool fullSync = m_fullWorldObjectsSyncTime >= WorldObjectFullSyncInterval;
                if (fullSync) m_fullWorldObjectsSyncTime -= WorldObjectFullSyncInterval;
                if (IsHost) SendWorldObjects(fullSync);
                else QueueEndOfFrameAction(MaintainClientWorldObjects);
            }
            m_fullAnimalSyncTime += tickDuration;
            if (IsHost && pulse16Hz)
            {
                bool fullSnapshot = m_fullAnimalSyncTime >= 1f;
                if (fullSnapshot) m_fullAnimalSyncTime -= 1f;
                SendAdaptiveAnimalUpdates(fullSnapshot);
            }
            SynchronizeProjectiles();
            if (pulse4Hz) SynchronizeContainers();
            }
            finally
            {
                NetworkMessageSender.FlushSyncBatch();
            }
        }

        // Source: ScMultiplayer.cs:TriggerNetworkTick
        // Every lower tier divides the same 32Hz phase, so all tiers coincide once per second.
        private static bool IsSyncPulse(uint pulse, NetworkSyncRate rate)
        {
            int divider = SyncBaseRate / (int)rate;
            return pulse % (uint)divider == 0u;
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        // Frame.Update runs after ScreensManager.Update, outside SubsystemUpdate.Update enumeration.
        private void QueueEndOfFrameAction(Action action)
        {
            if (action != null) m_endOfFrameActions.Enqueue(action);
        }

        private void ProcessEndOfFrameActions()
        {
            while (m_endOfFrameActions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] End-of-frame network action failed: {ex.Message}");
                }
            }
        }

        // ====================================================================
        // 发送: 玩家位置
        // ====================================================================
        private void SendGamePlayerPositionMessage(bool includeInventory, bool forceInventory)
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            if (!IsHost)
            {
                SendGamePlayerInputMessage(includeInventory);
                return;
            }
            var players = subsystemPlayers.ComponentPlayers;
            var positionBatch = new List<GamePlayerPositionMessage>();

            // Source: SubsystemPlayers.ComponentPlayers
            // Network IDs and persisted PlayerData indices are different domains. Send the one
            // locally controlled player, identified by exclusion from the remote avatar table.
            ComponentPlayer item = players.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (item != null)
            {
                // 发送方直接使用 ClientID 作为网络标识，避免 PlayerIndex 映射冲突
                int senderClientId = client.ClientID;

                bool isCrouching = item.ComponentBody.TargetCrouchFactor > 0f;
                bool isFlying = item.ComponentLocomotion.IsCreativeFlyEnabled;
                bool isRiding = item.ComponentRider?.Mount != null;

                IInventory inventory = item.ComponentMiner?.Inventory;
                int activeSlot = inventory?.ActiveSlotIndex ?? -1;
                int handVal = inventory != null && activeSlot >= 0 ? inventory.GetSlotValue(activeSlot) : 0;
                int handCnt = inventory != null && activeSlot >= 0 ? inventory.GetSlotCount(activeSlot) : 0;
                GetInventoryDelta(senderClientId, inventory, includeInventory, forceInventory,
                    out int[] slotValues, out int[] slotCounts);

                Vector3 itemOffset = item.ComponentCreatureModel.InHandItemOffsetOrder;
                Vector3 itemRotation = item.ComponentCreatureModel.InHandItemRotationOrder;
                float aimHandAngle = item.ComponentCreatureModel.AimHandAngleOrder;
                ApplyPersistentAimPresentation(senderClientId, item, handVal,
                    ref itemOffset, ref itemRotation, ref aimHandAngle);
                Vector2 lookAngles = item.ComponentLocomotion.LookAngles;
                Vector2? walkOrder = item.ComponentLocomotion.LastWalkOrder;
                float jumpOrder = item.ComponentLocomotion.LastJumpOrder;
                float pokingPhase = item.ComponentMiner?.PokingPhase ?? 0f;
                bool attackOrder = item.ComponentCreatureModel.AttackOrder;
                bool rowLeftOrder = item.ComponentCreatureModel.RowLeftOrder;
                bool rowRightOrder = item.ComponentCreatureModel.RowRightOrder;

                NetworkMessageSender.SendPlayerPositionMessage(
                    senderClientId, client.Step, item.ComponentBody.Position,
                    item.ComponentBody.Rotation, item.ComponentBody.Velocity, lookAngles,
                    walkOrder, jumpOrder, pokingPhase, attackOrder, rowLeftOrder, rowRightOrder,
                    isCrouching, isFlying, isRiding,
                    item.ComponentBody.StandingOnValue.HasValue,
                    activeSlot, handVal, handCnt,
                    itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts,
                    positionBatch);
                BroadcastPlayerPokeIfStarted(senderClientId, item.ComponentMiner);
            }
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0 && remote.Value?.ComponentPlayer != null)
                        SendAuthoritativePlayerState(remote.Key, remote.Value.ComponentPlayer,
                        includeInventory, forceInventory, positionBatch);
            }
            NetworkMessageSender.SendPlayerPositionBatch(positionBatch);
        }

        private void SendAuthoritativePlayerState(int networkClientId, ComponentPlayer item,
            bool includeInventory, bool forceInventory, List<GamePlayerPositionMessage> positionBatch)
        {
            IInventory inventory = item.ComponentMiner?.Inventory;
            int activeSlot = inventory?.ActiveSlotIndex ?? -1;
            int handValue = inventory != null && activeSlot >= 0 ? inventory.GetSlotValue(activeSlot) : 0;
            int handCount = inventory != null && activeSlot >= 0 ? inventory.GetSlotCount(activeSlot) : 0;
            GetInventoryDelta(networkClientId, inventory, includeInventory, forceInventory,
                out int[] slotValues, out int[] slotCounts);
            ComponentLocomotion locomotion = item.ComponentLocomotion;
            ComponentCreatureModel model = item.ComponentCreatureModel;
            Vector3 itemOffset = model.InHandItemOffsetOrder;
            Vector3 itemRotation = model.InHandItemRotationOrder;
            float aimHandAngle = model.AimHandAngleOrder;
            ApplyPersistentAimPresentation(networkClientId, item, handValue,
                ref itemOffset, ref itemRotation, ref aimHandAngle);
            NetworkMessageSender.SendPlayerPositionMessage(
                networkClientId, client.Step, item.ComponentBody.Position, item.ComponentBody.Rotation,
                item.ComponentBody.Velocity, locomotion.LookAngles,
                locomotion.LastWalkOrder, locomotion.LastJumpOrder,
                item.ComponentMiner?.PokingPhase ?? 0f, model.AttackOrder,
                model.RowLeftOrder, model.RowRightOrder,
                item.ComponentBody.TargetCrouchFactor > 0f,
                locomotion.IsCreativeFlyEnabled, item.ComponentRider?.Mount != null,
                item.ComponentBody.StandingOnValue.HasValue,
                activeSlot, handValue, handCount,
                itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts,
                positionBatch);
            BroadcastPlayerPokeIfStarted(networkClientId, item.ComponentMiner);
        }

        // Source: SubsystemBowBlockBehavior.cs:SubsystemBowBlockBehavior.OnAim
        // Source: SubsystemCrossbowBlockBehavior.cs:SubsystemCrossbowBlockBehavior.OnAim
        // Source: SubsystemMusketBlockBehavior.cs:SubsystemMusketBlockBehavior.OnAim
        // Source: SubsystemThrowableBlockBehavior.cs:SubsystemThrowableBlockBehavior.OnAim
        private void ApplyPersistentAimPresentation(int networkClientId, ComponentPlayer player,
            int handValue, ref Vector3 itemOffset, ref Vector3 itemRotation,
            ref float aimHandAngle)
        {
            bool isAiming;
            if (networkClientId == 0)
            {
                // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.m_aim
                // Nullable<T> boxes a present value as T and an empty value as null. SuAPI's
                // generic getter rejects the latter, so inspect the boxed value directly.
                object aimValue = ModManager.ModParentField.GetParentField(
                    player, "m_aim", typeof(ComponentPlayer));
                isAiming = aimValue is Ray3;
            }
            else
            {
                isAiming = m_networkPlayerInputs.TryGetValue(networkClientId,
                    out NetworkPlayerInputState state) && state.HeldAim.HasValue;
            }
            if (!isAiming || handValue == 0) return;

            Block block = BlocksManager.Blocks[Terrain.ExtractContents(handValue)];
            if (block is BowBlock)
            {
                itemOffset = Vector3.Zero;
                itemRotation = new Vector3(0f, -0.2f, 0f);
                aimHandAngle = 1.2f;
            }
            else if (block is CrossbowBlock)
            {
                itemOffset = new Vector3(-0.08f, -0.1f, 0.07f);
                itemRotation = new Vector3(-1.55f, 0f, 0f);
                aimHandAngle = 1.3f;
            }
            else if (block is MusketBlock)
            {
                itemOffset = new Vector3(-0.08f, -0.08f, 0.07f);
                itemRotation = new Vector3(-1.7f, 0f, 0f);
                aimHandAngle = 1.4f;
            }
            else if (block.IsAimable)
            {
                aimHandAngle = 3.2f;
                if (block is SpearBlock)
                {
                    itemOffset = new Vector3(0f, -0.25f, 0f);
                    itemRotation = new Vector3(3.14159f, 0f, 0f);
                }
            }
        }

        // Source: Survivalcraft/Game/ComponentHumanModel.cs:ComponentHumanModel.Update
        // Model orders are consumed and reset every frame. Reapply remote held aim after native
        // updates so the host sees a stable pose between network aim pulses.
        private void MaintainHostAimPresentation()
        {
            if (!IsHost || m_networkPlayerInputs.Count == 0) return;
            foreach (KeyValuePair<int, NetworkPlayerInputState> item in m_networkPlayerInputs)
            {
                if (!item.Value.HeldAim.HasValue ||
                    !m_networkPlayerData.TryGetValue(item.Key, out PlayerData playerData))
                    continue;
                ComponentPlayer player = playerData?.ComponentPlayer;
                ComponentCreatureModel model = player?.ComponentCreatureModel;
                IInventory inventory = player?.ComponentMiner?.Inventory;
                if (model == null || inventory == null) continue;
                int slot = inventory.ActiveSlotIndex;
                int handValue = slot >= 0 && slot < inventory.SlotsCount
                    ? inventory.GetSlotValue(slot)
                    : 0;
                Vector3 itemOffset = Vector3.Zero;
                Vector3 itemRotation = Vector3.Zero;
                float aimHandAngle = 0f;
                ApplyPersistentAimPresentation(item.Key, player, handValue,
                    ref itemOffset, ref itemRotation, ref aimHandAngle);
                model.AimHandAngleOrder = aimHandAngle;
                model.InHandItemOffsetOrder = itemOffset;
                model.InHandItemRotationOrder = itemRotation;
            }
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Update
        private void BroadcastPlayerPokeIfStarted(int playerIndex, ComponentMiner miner)
        {
            if (!IsHost || miner == null) return;
            float phase = miner.PokingPhase;
            m_hostPlayerPokingPhases.TryGetValue(playerIndex, out float previousPhase);
            bool started = phase > 0f &&
                (previousPhase <= 0f || phase + 0.05f < previousPhase);
            m_hostPlayerPokingPhases[playerIndex] = phase;
            if (!started) return;

            m_hostPlayerPokeSequences.TryGetValue(playerIndex, out int sequence);
            sequence = sequence == int.MaxValue ? 1 : sequence + 1;
            m_hostPlayerPokeSequences[playerIndex] = sequence;
            NetworkMessageSender.BroadcastPlayerPoke(new PlayerActionMessage(
                PlayerActionType.Poke, playerIndex, sequence, default));
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Update
        // A short poke can start between two network snapshots, so observe the authoritative
        // miners every rendered frame and retain the reliable edge message as the primary signal.
        private void BroadcastHostPlayerPokes()
        {
            if (!IsHost || GameManager.Project == null) return;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                player != null && !m_networkPlayerData.Values.Contains(player.PlayerData));
            BroadcastPlayerPokeIfStarted(0, localPlayer?.ComponentMiner);
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0)
                    BroadcastPlayerPokeIfStarted(remote.Key,
                        remote.Value?.ComponentPlayer?.ComponentMiner);
            }
        }

        // Source: Survivalcraft/Game/SubsystemWhistleBlockBehavior.cs:SubsystemWhistleBlockBehavior.OnUse
        internal void PublishAuthoritativeWhistle(ComponentMiner componentMiner,
            Vector3 position)
        {
            if (!IsHost || client?.IsConnected != true || componentMiner?.ComponentPlayer == null ||
                !IsFinite(position))
                return;
            int playerClientId = 0;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (ReferenceEquals(item.Value?.ComponentPlayer, componentMiner.ComponentPlayer))
                {
                    playerClientId = item.Key;
                    break;
                }
            }
            m_playerWhistleSequences.TryGetValue(playerClientId, out int sequence);
            sequence = sequence == int.MaxValue ? 1 : sequence + 1;
            m_playerWhistleSequences[playerClientId] = sequence;
            var message = new PlayerActionMessage(
                PlayerActionType.Whistle, playerClientId, sequence, default)
            {
                Position = position
            };
            NetworkMessageSender.BroadcastPlayerWhistle(message);
        }

        private void SendGamePlayerInputMessage(bool includeInventory)
        {
            if (m_localInputResendsRemaining <= 0 || m_localInputSequence <= 0) return;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer == null) return;
            IInventory inventory = localPlayer.ComponentMiner?.Inventory;
            int activeSlotIndex = inventory?.ActiveSlotIndex ?? -1;
            bool creativeInventory = inventory is ComponentCreativeInventory;
            bool localInventoryChanged = inventory != null && m_hasAuthoritativeLocalInventory &&
                !InventoryMatches(inventory, m_authoritativeLocalSlotValues,
                    m_authoritativeLocalSlotCounts);
            bool sendInventory = includeInventory && (creativeInventory || localInventoryChanged);
            int slotsCount = sendInventory
                ? (creativeInventory
                    ? Math.Min(inventory.SlotsCount, inventory.VisibleSlotsCount)
                    : inventory.SlotsCount)
                : 0;
            int[] slotValues = new int[slotsCount];
            int[] slotCounts = new int[slotsCount];
            for (int i = 0; i < slotsCount; i++)
            {
                slotValues[i] = inventory.GetSlotValue(i);
                slotCounts[i] = inventory.GetSlotCount(i);
            }
            NetworkMessageSender.SendPlayerInputMessage(
                client.ClientID, m_localInputSequence, client.Step,
                m_localInputBodyPosition, m_localInputBodyVelocity, m_localInputBodyRotation,
                m_localInputLookAngles, m_localPlayerInput,
                localPlayer.ComponentMiner?.PokingPhase ?? 0f,
                localPlayer.ComponentInput.IsControlledByTouch,
                localPlayer.ComponentBody.TargetCrouchFactor > 0f,
                localPlayer.ComponentLocomotion.IsCreativeFlyEnabled,
                localPlayer.ComponentRider?.Mount != null,
                GetClientMountEntityId(localPlayer), activeSlotIndex,
                m_lastAuthoritativeLocalInventoryTick, slotValues, slotCounts);
            m_lastSentInputSequence = m_localInputSequence;
            m_localInputResendsRemaining--;
        }

        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.SetClothes
        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.GetSlotValue
        // Equipment changes are sent as one reliable snapshot so a clothing move cannot be
        // observed as two independent inventory operations on the host.
        private void SynchronizePlayerEquipment()
        {
            if (client?.IsConnected != true || GameManager.Project == null) return;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;

            if (IsHost)
            {
                ComponentPlayer localPlayer = players.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
                SynchronizeHostEquipment(0, localPlayer);
                foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
                    SynchronizeHostEquipment(item.Key, item.Value?.ComponentPlayer);
                return;
            }

            ComponentPlayer local = players.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (local == null) return;
            EquipmentSnapshot snapshot = CaptureEquipmentSnapshot(local);
            if (m_lastEquipmentSnapshots.TryGetValue(client.ClientID, out EquipmentSnapshot previous) &&
                EquipmentSnapshotsEqual(previous, snapshot)) return;

            m_lastEquipmentSnapshots[client.ClientID] = snapshot;
            m_localEquipmentRevision = m_localEquipmentRevision == int.MaxValue
                ? 1 : m_localEquipmentRevision + 1;
            NetworkMessageSender.SendPlayerEquipmentMessage(0, new PlayerEquipmentMessage(
                client.ClientID, m_localEquipmentRevision, snapshot.ActiveSlotIndex,
                snapshot.SlotValues, snapshot.SlotCounts, snapshot.Clothes));
        }

        private void SynchronizeHostEquipment(int clientId, ComponentPlayer player)
        {
            if (player == null) return;
            EquipmentSnapshot snapshot = CaptureEquipmentSnapshot(player);
            if (m_lastEquipmentSnapshots.TryGetValue(clientId, out EquipmentSnapshot previous) &&
                EquipmentSnapshotsEqual(previous, snapshot)) return;

            m_lastEquipmentSnapshots[clientId] = snapshot;
            int revision = m_equipmentAuthorityRevisions.TryGetValue(clientId, out int current)
                ? (current == int.MaxValue ? 1 : current + 1) : 1;
            m_equipmentAuthorityRevisions[clientId] = revision;
            m_lastReceivedEquipmentRevisions[clientId] = revision;
            m_equipmentSynchronizedClients.Add(clientId);
            BroadcastPlayerEquipment(clientId, revision, snapshot);
        }

        private void BroadcastPlayerEquipment(int clientId, int revision, EquipmentSnapshot snapshot)
        {
            NetworkMessageSender.SendPlayerEquipmentMessage(-1, new PlayerEquipmentMessage(
                clientId, revision, snapshot.ActiveSlotIndex, snapshot.SlotValues,
                snapshot.SlotCounts, snapshot.Clothes));
        }

        private void HandlePlayerEquipmentMessage(PlayerEquipmentMessage message, int sourceClientId)
        {
            if (message == null) return;
            if (IsHost)
            {
                if (sourceClientId <= 0 || message.ClientId != sourceClientId ||
                    !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) ||
                    playerData?.ComponentPlayer == null) return;
                if (m_lastClientEquipmentRevisions.TryGetValue(sourceClientId, out int previousRevision) &&
                    message.Revision <= previousRevision) return;

                m_lastClientEquipmentRevisions[sourceClientId] = message.Revision;
                ApplyEquipmentSnapshot(playerData.ComponentPlayer, message);
                EquipmentSnapshot snapshot = CaptureEquipmentSnapshot(playerData.ComponentPlayer);
                m_lastEquipmentSnapshots[sourceClientId] = snapshot;
                int currentAuthority = m_equipmentAuthorityRevisions.TryGetValue(sourceClientId,
                    out int authorityRevision) ? authorityRevision : 0;
                int revision = Math.Max(currentAuthority + 1, message.Revision);
                m_equipmentAuthorityRevisions[sourceClientId] = revision;
                m_lastReceivedEquipmentRevisions[sourceClientId] = revision;
                m_equipmentSynchronizedClients.Add(sourceClientId);
                BroadcastPlayerEquipment(sourceClientId, revision, snapshot);
                if (m_clientRecordKeys.TryGetValue(sourceClientId, out string recordKey))
                {
                    m_playerRecords[recordKey] = CapturePlayerRecord(playerData);
                    m_playerRecordsDirty = true;
                }
                return;
            }

            if (sourceClientId != 0 || m_departedRemoteClientIds.Contains(message.ClientId)) return;
            if (m_lastReceivedEquipmentRevisions.TryGetValue(message.ClientId,
                out int lastRevision) && message.Revision <= lastRevision) return;
            if (message.ClientId == client.ClientID && message.Revision < m_localEquipmentRevision) return;

            ComponentPlayer player = null;
            if (message.ClientId == client.ClientID)
            {
                SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
                player = players?.ComponentPlayers.FirstOrDefault(item =>
                    !m_networkPlayerData.Values.Contains(item.PlayerData));
                m_localEquipmentRevision = Math.Max(m_localEquipmentRevision, message.Revision);
            }
            else if (m_networkPlayerData.TryGetValue(message.ClientId, out PlayerData remotePlayer))
            {
                player = remotePlayer?.ComponentPlayer;
            }
            if (player == null) return;

            ApplyEquipmentSnapshot(player, message);
            m_lastReceivedEquipmentRevisions[message.ClientId] = message.Revision;
            m_lastEquipmentSnapshots[message.ClientId] = CaptureEquipmentSnapshot(player);
            m_equipmentSynchronizedClients.Add(message.ClientId);
        }

        private static EquipmentSnapshot CaptureEquipmentSnapshot(ComponentPlayer player)
        {
            IInventory inventory = player?.ComponentMiner?.Inventory;
            return new EquipmentSnapshot
            {
                ActiveSlotIndex = inventory?.ActiveSlotIndex ?? -1,
                SlotValues = inventory == null ? Array.Empty<int>() : CaptureInventoryValues(inventory),
                SlotCounts = inventory == null ? Array.Empty<int>() : CaptureInventoryCounts(inventory),
                Clothes = CaptureClothes(player)
            };
        }

        private static void ApplyEquipmentSnapshot(ComponentPlayer player, PlayerEquipmentMessage message)
        {
            if (player == null || message == null) return;
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory != null)
            {
                if (message.ActiveSlotIndex >= 0 && message.ActiveSlotIndex < inventory.SlotsCount)
                    inventory.ActiveSlotIndex = message.ActiveSlotIndex;
                ApplyInventory(inventory, message.SlotValues, message.SlotCounts);
            }
            ApplyClothes(player, message.Clothes);
        }

        private static bool EquipmentSnapshotsEqual(EquipmentSnapshot left, EquipmentSnapshot right)
        {
            if (left == null || right == null || left.ActiveSlotIndex != right.ActiveSlotIndex ||
                !ArraysEqual(left.SlotValues, right.SlotValues) ||
                !ArraysEqual(left.SlotCounts, right.SlotCounts)) return false;
            int[][] leftClothes = left.Clothes ?? Array.Empty<int[]>();
            int[][] rightClothes = right.Clothes ?? Array.Empty<int[]>();
            if (leftClothes.Length != rightClothes.Length) return false;
            for (int i = 0; i < leftClothes.Length; i++)
                if (!ArraysEqual(leftClothes[i], rightClothes[i])) return false;
            return true;
        }

        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.GetSlotValue
        // Full inventory arrays are sent only on change, with a five-second recovery keyframe.
        private void GetInventoryDelta(int ownerId, IInventory inventory, bool check,
            bool force, out int[] values, out int[] counts)
        {
            values = Array.Empty<int>();
            counts = Array.Empty<int>();
            if (!check || inventory == null) return;
            int[] currentValues = CaptureInventoryValues(inventory);
            int[] currentCounts = CaptureInventoryCounts(inventory);
            bool changed = force || !m_lastSentInventoryValues.TryGetValue(ownerId,
                out int[] previousValues) || !ArraysEqual(currentValues, previousValues) ||
                !m_lastSentInventoryCounts.TryGetValue(ownerId, out int[] previousCounts) ||
                !ArraysEqual(currentCounts, previousCounts);
            if (!changed) return;
            m_lastSentInventoryValues[ownerId] = currentValues;
            m_lastSentInventoryCounts[ownerId] = currentCounts;
            values = currentValues;
            counts = currentCounts;
        }

        // ====================================================================
        // 发送: 世界信息 (仅Host)
        // ====================================================================
        private void SendGameWorldInfoMessage(bool reliable = false)
        {
            if (client.ClientID != 0) return;
            var gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(true);
            var timeOfDay = GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(true);
            var weather = GameManager.Project.FindSubsystem<SubsystemWeather>(true);
            var sky = GameManager.Project.FindSubsystem<SubsystemSky>(true);
            NetworkMessageSender.SendWorldInfoMessage(
                timeOfDay.TimeOfDayOffset,
                gameInfo.TotalElapsedGameTime,
                gameInfo.WorldSettings.TimeOfDayMode,
                weather,
                sky, reliable);
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike
        // Slow weather snapshots use latest delivery, while a short lightning edge remains reliable.
        private void SendHostLightningEdge()
        {
            SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(false);
            if (sky == null) return;
            object value = ModManager.ModParentField.GetParentField(
                sky, "m_lightningStrikePosition", typeof(SubsystemSky));
            bool active = value is Vector3;
            if (active && !m_hostLightningActive)
                SendGameWorldInfoMessage(reliable: true);
            m_hostLightningActive = active;
        }

        // Source: Survivalcraft/Game/SubsystemBodies.cs:SubsystemBodies.Bodies
        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
        private void SendWorldObjects(bool fullSync)
        {
            Project project = GameManager.Project;
            if (project == null) return;

            SubsystemBodies subsystemBodies = project.FindSubsystem<SubsystemBodies>(true);
            Entity[] animals = subsystemBodies.Bodies
                .Select(body => body?.Entity)
                .Where(entity => entity?.FindComponent<ComponentCreature>() != null &&
                    entity.FindComponent<ComponentPlayer>() == null)
                .Distinct()
                .ToArray();
            var currentAnimals = new HashSet<Entity>(animals);
            foreach (Entity removed in m_hostAnimalIds.Keys.Where(entity =>
                entity == null || !currentAnimals.Contains(entity) || !entity.IsAddedToProject).ToArray())
            {
                ushort id = m_hostAnimalIds[removed];
                NetworkMessageSender.SendEntityMessage(new EntityMessage(id, EntityMessage.EntityAction.Remove));
                m_hostAnimalIds.Remove(removed);
                m_hostAnimalSync.Remove(removed);
            }
            m_hostAnimals.Clear();
            foreach (Entity entity in animals)
            {
                if (!m_hostAnimalIds.TryGetValue(entity, out ushort id))
                {
                    id = m_nextAnimalId++;
                    m_hostAnimalIds.Add(entity, id);
                }
                if (!m_hostAnimalSync.ContainsKey(entity))
                {
                    int simulationSeed = CalculateAnimalSimulationSeed(id);
                    m_hostAnimalSync.Add(entity, new AnimalSyncMetadata
                    {
                        SimulationSeed = simulationSeed
                    });
                    ApplyAnimalSimulationSeed(entity, simulationSeed);
                }
                m_hostAnimals.Add(entity);
            }

            SubsystemPickables subsystemPickables = project.FindSubsystem<SubsystemPickables>(false);
            if (subsystemPickables == null) return;
            Pickable[] pickables = subsystemPickables.Pickables.Where(pickable => pickable != null && !pickable.ToRemove).ToArray();
            var currentPickables = new HashSet<Pickable>(pickables);
            foreach (Pickable removed in m_hostPickableIds.Keys.Where(pickable =>
                pickable == null || !currentPickables.Contains(pickable) || pickable.ToRemove).ToArray())
            {
                ushort id = m_hostPickableIds[removed];
                NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                    PickableSyncMessage.PickAction.Delete, id, 0, 0, Vector3.Zero, Vector3.Zero));
                m_hostPickableIds.Remove(removed);
            }

            var pickableUpdate = new PickableSyncMessage { Action = PickableSyncMessage.PickAction.UpdatePosition };
            foreach (Pickable pickable in pickables)
            {
                if (!m_hostPickableIds.TryGetValue(pickable, out ushort id))
                {
                    id = m_nextPickableId++;
                    m_hostPickableIds.Add(pickable, id);
                    fullSync = true;
                }
                if (fullSync)
                {
                    NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                        PickableSyncMessage.PickAction.Create, id, pickable.Value, pickable.Count,
                        pickable.Position, pickable.Velocity, pickable.FlyToPosition,
                        stuckMatrix: pickable.StuckMatrix));
                }
                pickableUpdate.Positions.Add(new PickableSyncMessage.PickablePos
                {
                    Id = id,
                    Position = pickable.Position,
                    Velocity = pickable.Velocity,
                    FlyToPosition = pickable.FlyToPosition
                });
            }
            if (pickableUpdate.Positions.Count > 0)
                NetworkMessageSender.SendPickableMessage(pickableUpdate);
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.PickableAdded
        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.PickableRemoved
        // A short-lived drop can be collected before the next 8Hz snapshot. Publish lifecycle
        // edges immediately and keep periodic snapshots for movement and recovery only.
        private void AttachHostPickableEvents(Project project)
        {
            if (project == null) return;
            SubsystemPickables subsystem = project.FindSubsystem<SubsystemPickables>(false);
            if (subsystem == null || ReferenceEquals(m_hostPickablesSubsystem, subsystem)) return;
            DetachHostPickableEvents();
            m_hostPickablesSubsystem = subsystem;
            subsystem.PickableAdded += HandleHostPickableAdded;
            subsystem.PickableRemoved += HandleHostPickableRemoved;
        }

        private void DetachHostPickableEvents()
        {
            if (m_hostPickablesSubsystem == null) return;
            m_hostPickablesSubsystem.PickableAdded -= HandleHostPickableAdded;
            m_hostPickablesSubsystem.PickableRemoved -= HandleHostPickableRemoved;
            m_hostPickablesSubsystem = null;
        }

        private void HandleHostPickableAdded(Pickable pickable)
        {
            if (!IsHost)
            {
                HandleClientPredictedPickableAdded(pickable);
                return;
            }
            if (client?.IsConnected != true || pickable == null ||
                !m_networkPlayerData.Any(item => item.Key > 0))
                return;
            if (!m_hostPickableIds.TryGetValue(pickable, out ushort id))
            {
                id = m_nextPickableId++;
                m_hostPickableIds.Add(pickable, id);
            }
            NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                PickableSyncMessage.PickAction.Create, id, pickable.Value, pickable.Count,
                pickable.Position, pickable.Velocity, pickable.FlyToPosition,
                stuckMatrix: pickable.StuckMatrix));
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        internal void PublishPickableWaterSplash(Pickable pickable)
        {
            if (!IsHost || client?.IsConnected != true || pickable == null || pickable.ToRemove ||
                !m_networkPlayerData.Any(item => item.Key > 0))
                return;
            if (!m_hostPickableIds.TryGetValue(pickable, out ushort id))
            {
                id = m_nextPickableId++;
                m_hostPickableIds.Add(pickable, id);
                NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                    PickableSyncMessage.PickAction.Create, id, pickable.Value, pickable.Count,
                    pickable.Position, pickable.Velocity, pickable.FlyToPosition,
                    stuckMatrix: pickable.StuckMatrix));
            }
            NetworkMessageSender.SendPickableMessage(new PickableSyncMessage
            {
                Action = PickableSyncMessage.PickAction.WaterSplash,
                Id = id,
                Position = pickable.Position
            });
        }

        private void HandleClientPredictedPickableAdded(Pickable pickable)
        {
            if (m_applyingNetworkPickable || pickable == null || client?.IsConnected != true ||
                GameManager.Project == null)
                return;
            ComponentPlayer player = GameManager.Project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(item =>
                    !m_networkPlayerData.Values.Contains(item.PlayerData));
            IInventory inventory = player?.ComponentMiner?.Inventory;
            if (inventory == null || player.ComponentBody == null) return;

            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            // Q/gamepad drop already sent a request before native prediction creates its pickable.
            if (Time.RealTime <= m_pendingLocalDropPredictionUntil &&
                pickable.Value == m_pendingLocalDropValue &&
                pickable.Count == m_pendingLocalDropCount &&
                Vector3.DistanceSquared(pickable.Position, m_pendingLocalDropPosition) <= 0.01f)
            {
                m_pendingLocalDropPredictionUntil = 0.0;
                pickable.ToRemove = true;
                return;
            }

            // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
            // A UI drag creates the pickable exactly at the active camera. This excludes mining,
            // creature and subsystem drops which must never be converted into player requests.
            if (player.GameWidget?.ActiveCamera == null ||
                Vector3.DistanceSquared(pickable.Position,
                    player.GameWidget.ActiveCamera.ViewPosition) > 0.0001f)
            {
                pickable.ToRemove = true;
                return;
            }

            int sourceSlot = -1;
            int sourceCount = 0;
            int slotsCount = Math.Min(inventory.SlotsCount,
                Math.Min(m_lastLocalInventoryValues.Length, m_lastLocalInventoryCounts.Length));
            for (int i = 0; i < slotsCount; i++)
            {
                if (m_lastLocalInventoryValues[i] != pickable.Value) continue;
                int currentCount = inventory.GetSlotValue(i) == pickable.Value
                    ? inventory.GetSlotCount(i)
                    : 0;
                if (m_lastLocalInventoryCounts[i] - currentCount < pickable.Count) continue;
                sourceSlot = i;
                sourceCount = m_lastLocalInventoryCounts[i];
                break;
            }
            if (sourceSlot < 0 || sourceCount <= 0) return;

            m_localDropSequence = m_localDropSequence == int.MaxValue
                ? 1
                : m_localDropSequence + 1;
            var message = new PlayerActionMessage(
                PlayerActionType.DropRequest, client.ClientID, m_localDropSequence,
                default, sourceSlot, pickable.Value, sourceCount)
            {
                DropCount = pickable.Count,
                Position = pickable.Position,
                Velocity = pickable.Velocity
            };
            NetworkMessageSender.SendPlayerDropRequest(message);
            // The host recreates and broadcasts the authoritative pickable. Keeping this local
            // prediction would leave an extra client-only item after the host response arrives.
            pickable.ToRemove = true;
        }

        private void HandleHostPickableRemoved(Pickable pickable)
        {
            if (!IsHost || pickable == null ||
                !m_hostPickableIds.TryGetValue(pickable, out ushort id))
                return;
            m_hostPickableIds.Remove(pickable);
            if (client?.IsConnected == true &&
                m_networkPlayerData.Any(item => item.Key > 0))
            {
                if (pickable.Count == 0 &&
                    TryGetHostPickableCollector(pickable,
                        out int collectorClientId, out IInventory inventory))
                {
                    m_forceHostInventorySync = true;
                    var message = new PickableSyncMessage
                    {
                        Action = PickableSyncMessage.PickAction.Acquire,
                        Id = id,
                        CollectorClientId = collectorClientId,
                        ServerTick = client.Step,
                        Count = 0,
                        PlaySound = true,
                        SlotValues = CaptureInventoryValues(inventory),
                        SlotCounts = CaptureInventoryCounts(inventory)
                    };
                    if (collectorClientId > 0)
                        MarkHostInventoryAuthoritative(collectorClientId);
                    NetworkMessageSender.SendPickableMessage(message);
                }
                else
                {
                    NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                        PickableSyncMessage.PickAction.Delete, id, 0, 0,
                        Vector3.Zero, Vector3.Zero));
                }
            }
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        private bool TryGetHostPickableCollector(Pickable pickable,
            out int collectorClientId, out IInventory inventory)
        {
            collectorClientId = -1;
            inventory = null;
            if (pickable == null || Terrain.ExtractContents(pickable.Value) == 248)
                return false;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return false;
            ComponentPlayer collector = players.ComponentPlayers
                .Where(player => player?.ComponentBody != null &&
                    player.ComponentMiner?.Inventory != null &&
                    player.ComponentHealth?.Health > 0f)
                .OrderBy(player => Vector3.DistanceSquared(
                    player.ComponentBody.Position + new Vector3(0f, 0.75f, 0f),
                    pickable.Position))
                .FirstOrDefault(player => Vector3.DistanceSquared(
                    player.ComponentBody.Position + new Vector3(0f, 0.75f, 0f),
                    pickable.Position) <= 2.25f);
            if (collector == null) return false;
            KeyValuePair<int, PlayerData> remote = m_networkPlayerData.FirstOrDefault(pair =>
                ReferenceEquals(pair.Value?.ComponentPlayer, collector));
            collectorClientId = remote.Value != null ? remote.Key : 0;
            inventory = collector.ComponentMiner.Inventory;
            return true;
        }

        // Source: Survivalcraft/Game/ComponentBehavior.cs:ComponentBehavior.IsActive
        // Source: Survivalcraft/Game/ComponentHerdBehavior.cs:ComponentHerdBehavior.CallNearbyCreaturesHelp
        private void SendAdaptiveAnimalUpdates(bool forceFullSnapshot)
        {
            Project project = GameManager.Project;
            if (project == null || (!forceFullSnapshot && m_hostAnimals.Count == 0)) return;

            double now = Time.RealTime;
            Vector3[] playerPositions = project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers
                .Where(player => player?.ComponentBody != null)
                .Select(player => player.ComponentBody.Position)
                .ToArray() ?? Array.Empty<Vector3>();
            var candidates = new List<AnimalSyncCandidate>(m_hostAnimals.Count);
            foreach (Entity entity in m_hostAnimals.ToArray())
            {
                if (entity?.IsAddedToProject != true) continue;
                ComponentCreature creature = entity.FindComponent<ComponentCreature>();
                ComponentBody body = creature?.ComponentBody;
                if (creature == null || body == null) continue;

                ComponentBehavior activeBehavior = entity.FindComponents<ComponentBehavior>()
                    .Where(behavior => behavior != null && behavior.IsActive)
                    .OrderByDescending(behavior => behavior.ImportanceLevel)
                    .FirstOrDefault();
                ComponentChaseBehavior chase = entity.FindComponent<ComponentChaseBehavior>();
                ComponentCreature target = chase?.Target;
                ComponentHerdBehavior herd = entity.FindComponent<ComponentHerdBehavior>();
                ComponentCreatureModel model = creature.ComponentCreatureModel;
                AnimalSyncMetadata metadata = m_hostAnimalSync[entity];
                float health = creature.ComponentHealth?.Health ?? 0f;
                bool wasAttacked = metadata.HasSent && health < metadata.LastHealth - 0.001f;
                if (wasAttacked) metadata.HighPriorityUntil = now + 3.0;
                string behaviorState = GetActiveBehaviorState(activeBehavior);
                bool isAttacking = IsAnimalAttackActive(chase, model);
                bool isFeeding = IsAnimalFeedActive(model, behaviorState);
                bool targetsPlayer = target?.Entity.FindComponent<ComponentPlayer>() != null;
                bool highPriorityInteraction = wasAttacked || now < metadata.HighPriorityUntil;

                byte tier = 0;
                float nearestPlayerDistanceSquared = playerPositions.Length > 0
                    ? playerPositions.Min(position => Vector3.DistanceSquared(position, body.Position))
                    : float.MaxValue;
                float nearPlayerThreshold = metadata.SyncTier >= 2 ? 12f : 10f;
                bool isNearPlayer = nearestPlayerDistanceSquared <=
                    nearPlayerThreshold * nearPlayerThreshold;
                if (targetsPlayer) tier = 1;
                if (isNearPlayer)
                    tier = Math.Max(tier, (byte)2);
                if (highPriorityInteraction) tier = 3;
                if (isAttacking && targetsPlayer) tier = 4;

                candidates.Add(new AnimalSyncCandidate
                {
                    Entity = entity,
                    Creature = creature,
                    Body = body,
                    BehaviorState = behaviorState,
                    TargetEntityId = GetCreatureTargetNetworkId(target),
                    HerdName = herd?.HerdName ?? string.Empty,
                    SyncTier = tier,
                    AttackOrder = isAttacking,
                    FeedOrder = isFeeding
                });
            }

            foreach (AnimalSyncCandidate source in candidates.Where(candidate =>
                candidate.SyncTier >= 3 && !string.IsNullOrEmpty(candidate.HerdName)).ToArray())
            {
                foreach (AnimalSyncCandidate member in candidates)
                {
                    if (member.HerdName == source.HerdName &&
                        Vector3.DistanceSquared(member.Body.Position, source.Body.Position) < 256f)
                        member.SyncTier = Math.Max(member.SyncTier, (byte)3);
                }
            }

            var bodyMessage = new BodyUpdateMessage
            {
                ServerTick = client.Step,
                IsFullSnapshot = forceFullSnapshot
            };
            bool bodyBatchRequiresReliable = false;
            foreach (AnimalSyncCandidate candidate in candidates)
            {
                AnimalSyncMetadata metadata = m_hostAnimalSync[candidate.Entity];
                bool isInitialState = !metadata.HasSent;

                candidate.StateChanged = !metadata.HasSent ||
                    metadata.BehaviorState != candidate.BehaviorState ||
                    metadata.TargetEntityId != candidate.TargetEntityId ||
                    metadata.HerdName != candidate.HerdName ||
                    metadata.SyncTier != candidate.SyncTier ||
                    metadata.AttackOrder != candidate.AttackOrder ||
                    metadata.FeedOrder != candidate.FeedOrder;
                if (!forceFullSnapshot && !candidate.StateChanged &&
                    now < metadata.NextSendTime)
                    continue;

                ComponentLocomotion locomotion = candidate.Creature.ComponentLocomotion;
                ComponentCreatureModel model = candidate.Creature.ComponentCreatureModel;
                BodyUpdateMessage.ChangeFlag flags = BodyUpdateMessage.ChangeFlag.Position |
                    BodyUpdateMessage.ChangeFlag.Rotation |
                    BodyUpdateMessage.ChangeFlag.Velocity |
                    BodyUpdateMessage.ChangeFlag.LookAngles |
                    BodyUpdateMessage.ChangeFlag.Movement |
                    BodyUpdateMessage.ChangeFlag.Health;
                if (isInitialState || forceFullSnapshot)
                    flags |= BodyUpdateMessage.ChangeFlag.Template;
                if (candidate.StateChanged || forceFullSnapshot)
                    flags |= BodyUpdateMessage.ChangeFlag.BehaviorState;
                bodyMessage.Bodies.Add(new BodyUpdateMessage.BodyItem
                {
                    EntityId = m_hostAnimalIds[candidate.Entity],
                    Flags = flags,
                    Position = candidate.Body.Position,
                    Rotation = candidate.Body.Rotation,
                    Velocity = candidate.Body.Velocity,
                    LookAngles = locomotion?.LookAngles ?? Vector2.Zero,
                    WalkOrder = locomotion?.LastWalkOrder,
                    FlyOrder = locomotion?.LastFlyOrder,
                    SwimOrder = locomotion?.LastSwimOrder,
                    TurnOrder = locomotion?.LastTurnOrder ?? Vector2.Zero,
                    JumpOrder = locomotion?.LastJumpOrder ?? 0f,
                    AttackOrder = candidate.AttackOrder,
                    FeedOrder = candidate.FeedOrder,
                    TemplateName = candidate.Entity.ValuesDictionary?.DatabaseObject?.Name,
                    SyncTier = candidate.SyncTier,
                    ActiveBehaviorState = candidate.BehaviorState,
                    TargetEntityId = candidate.TargetEntityId,
                    HerdName = candidate.HerdName,
                    SimulationSeed = metadata.SimulationSeed,
                    Health = candidate.Creature.ComponentHealth?.Health ?? 0f
                });
                bodyBatchRequiresReliable |= isInitialState;

                metadata.HasSent = true;
                metadata.BehaviorState = candidate.BehaviorState;
                metadata.TargetEntityId = candidate.TargetEntityId;
                metadata.HerdName = candidate.HerdName;
                metadata.SyncTier = candidate.SyncTier;
                metadata.AttackOrder = candidate.AttackOrder;
                metadata.FeedOrder = candidate.FeedOrder;
                metadata.LastHealth = candidate.Creature.ComponentHealth?.Health ?? 0f;
                metadata.NextSendTime = now + GetAnimalSyncInterval(candidate.SyncTier);

                if (!forceFullSnapshot && bodyMessage.Bodies.Count >= AnimalSyncBatchSize)
                {
                    NetworkMessageSender.SendBodyUpdateMessage(
                        bodyMessage, bodyBatchRequiresReliable);
                    bodyMessage = new BodyUpdateMessage { ServerTick = client.Step };
                    bodyBatchRequiresReliable = false;
                }
            }
            if (forceFullSnapshot)
            {
                // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
                // A reliable complete set lets clients recover missed add/remove datagrams and an
                // empty snapshot clears every stale replica after the host population reaches zero.
                NetworkMessageSender.SendBodyUpdateMessage(bodyMessage, true);
            }
            else if (bodyMessage.Bodies.Count > 0)
            {
                NetworkMessageSender.SendBodyUpdateMessage(
                    bodyMessage, bodyBatchRequiresReliable);
            }
        }

        private string GetActiveBehaviorState(ComponentBehavior behavior)
        {
            if (behavior == null) return string.Empty;
            for (Type type = behavior.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                FieldInfo field = type.GetField("m_stateMachine",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null || field.FieldType != typeof(StateMachine)) continue;
                StateMachine stateMachine = ModManager.ModParentField.GetParentField<StateMachine>(
                    behavior, field.Name, field.DeclaringType);
                return behavior.GetType().Name + ":" + (stateMachine?.CurrentState ?? string.Empty);
            }
            return behavior.GetType().Name;
        }

        private int GetCreatureTargetNetworkId(ComponentCreature target)
        {
            Entity targetEntity = target?.Entity;
            if (targetEntity == null || targetEntity.IsAddedToProject != true) return 0;
            if (m_hostAnimalIds.TryGetValue(targetEntity, out ushort animalId)) return animalId;
            ComponentPlayer targetPlayer = targetEntity.FindComponent<ComponentPlayer>();
            if (targetPlayer == null) return 0;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (item.Value?.ComponentPlayer == targetPlayer) return -(item.Key + 1);
            }
            return -(client.ClientID + 1);
        }

        private static double GetAnimalSyncInterval(byte tier)
        {
            // Source: ScMultiplayer.cs:HandleAnimalInteractionMessage
            // Player targets use 4Hz, any animal within 10 blocks uses 8Hz, and direct
            // interaction, herd help, or an active player attack uses 16Hz.
            return tier >= 3 ? 0.0625 : tier >= 2 ? 0.125 : tier >= 1 ? 0.25 : 0.5;
        }

        private int CalculateAnimalSimulationSeed(ushort id)
        {
            unchecked
            {
                uint value = (uint)m_sessionRandomSeed;
                value = (value ^ id) * 16777619u;
                value ^= value >> 16;
                return (int)value;
            }
        }

        // Source: Survivalcraft/Game/Random.cs:Random.Seed
        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.m_random
        private void ApplyAnimalSimulationSeed(Entity entity, int seed)
        {
            if (entity == null) return;
            foreach (Component component in entity.Components)
            {
                for (Type type = component.GetType();
                    type != null && typeof(Component).IsAssignableFrom(type);
                    type = type.BaseType)
                {
                    FieldInfo field = type.GetField("m_random",
                        BindingFlags.Instance | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                    if (field == null || field.FieldType != typeof(Game.Random)) continue;
                    int componentSeed = CombineSimulationSeed(seed, type.FullName);
                    ModManager.ModParentField.ModifyParentField(component, field.Name,
                        new Game.Random(componentSeed), field.DeclaringType);
                }
            }
        }

        private static int CombineSimulationSeed(int seed, string text)
        {
            unchecked
            {
                uint value = (uint)seed;
                foreach (char character in text ?? string.Empty)
                    value = (value ^ character) * 16777619u;
                return (int)value;
            }
        }

        // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Update
        private bool IsAnimalAttackActive(ComponentChaseBehavior chase,
            ComponentCreatureModel model)
        {
            if (model?.AttackOrder == true || model?.IsAttackHitMoment == true) return true;
            ComponentBody targetBody = chase?.Target?.ComponentBody;
            if (targetBody == null || chase.IsActive != true) return false;
            return ModManager.ModParentMethod.InvokeParentMethod<bool>(
                chase, "IsTargetInAttackRange", targetBody);
        }

        // Source: Survivalcraft/Game/ComponentEatPickableBehavior.cs:ComponentEatPickableBehavior.Update
        // Source: Survivalcraft/Game/ComponentRandomFeedBehavior.cs:ComponentRandomFeedBehavior.Update
        // Source: Survivalcraft/Game/ComponentRandomPeckBehavior.cs:ComponentRandomPeckBehavior.Update
        private static bool IsAnimalFeedActive(ComponentCreatureModel model,
            string behaviorState)
        {
            if (model?.FeedOrder == true) return true;
            int separator = behaviorState?.LastIndexOf(':') ?? -1;
            string stateName = separator >= 0 ? behaviorState.Substring(separator + 1) : string.Empty;
            return stateName == "Eat" || stateName == "Feed" || stateName == "Peck";
        }

        // ====================================================================
        // 发送: 生命值 (周期性)
        // ====================================================================
        private void SendGamePlayerHealthMessage(bool force)
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null || !IsHost) return;
            var players = subsystemPlayers.ComponentPlayers;

            int currentClientId = client.ClientID;
            ComponentPlayer item = players.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (item != null)
            {
                var health = item.ComponentHealth;
                if (health == null) return;

                float lastHealth;
                if (!m_playerHealthCache.TryGetValue(currentClientId, out lastHealth))
                    lastHealth = health.Health;

                float change = health.Health - lastHealth;
                if (force || Math.Abs(change) > 0.0001f)
                    NetworkMessageSender.SendPlayerHealthMessage(client.ClientID, item, change);
                m_playerHealthCache[currentClientId] = health.Health;
            }
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0 && remote.Value?.ComponentPlayer != null)
                    SendAuthoritativePlayerHealth(remote.Key, remote.Value.ComponentPlayer, force);
            }
        }

        private void SendAuthoritativePlayerHealth(int networkClientId, ComponentPlayer player, bool force)
        {
            ComponentHealth health = player.ComponentHealth;
            if (health == null) return;
            if (!m_playerHealthCache.TryGetValue(networkClientId, out float lastHealth))
                lastHealth = health.Health;
            float change = health.Health - lastHealth;
            if (force || Math.Abs(change) > 0.0001f)
                NetworkMessageSender.SendPlayerHealthMessage(networkClientId, player, change);
            m_playerHealthCache[networkClientId] = health.Health;
        }

        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Update
        internal void PublishAuthoritativeCough(ComponentPlayer player)
        {
            if (!IsHost || client?.IsConnected != true || player == null) return;
            int playerClientId = 0;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (ReferenceEquals(item.Value?.ComponentPlayer, player))
                {
                    playerClientId = item.Key;
                    break;
                }
            }
            NetworkMessageSender.SendPlayerHealthMessage(playerClientId, player, 0f);
        }

        // Source: Survivalcraft/Game/VitalStatsWidget.cs:VitalStatsWidget.Update
        // Client-side UI damage is a request. The host accepts only a lower health value and
        // remains authoritative for the resulting health, events and death state.
        private void SendClientDamageRequest()
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            ComponentHealth health = localPlayer?.ComponentHealth;
            if (health == null) return;
            if (!m_hasObservedClientHealth)
            {
                m_hasObservedClientHealth = true;
                m_observedClientHealth = health.Health;
                m_observedClientSleeping = localPlayer.ComponentSleep?.IsSleeping == true;
                return;
            }
            float change = health.Health - m_observedClientHealth;
            bool isSleeping = localPlayer.ComponentSleep?.IsSleeping == true;
            if (change < -0.0001f || isSleeping != m_observedClientSleeping)
                NetworkMessageSender.SendPlayerHealthMessage(
                    client.ClientID, localPlayer, change, "Client state request");
            m_observedClientHealth = health.Health;
            m_observedClientSleeping = isSleeping;
        }

        // ====================================================================
        // 渲染远程玩家
        // ====================================================================
        private void RenderRemotePlayers()
        {
            if (!client.IsConnected || RemotePlayers.Count == 0) return;

            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            var players = subsystemPlayers.ComponentPlayers;
            if (players.Count == 0) return;

            // 获取本地玩家相机
            var localPlayer = players[0];
            var camera = localPlayer.GameWidget?.ActiveCamera;
            if (camera == null) return;

            // 延迟初始化 PrimitivesRenderer3D
            if (m_primitivesRenderer3D == null)
                m_primitivesRenderer3D = new PrimitivesRenderer3D();

            float cubeSize = 0.4f;
            var color = Color.White;
            double now = Time.RealTime;

            foreach (var kvp in RemotePlayers)
            {
                var state = kvp.Value;
                // 超过 5 秒没有更新, 跳过
                if (now - state.LastUpdateTime > 5.0) continue;

                Vector3 pos = state.Position;
                Vector3 offset = new Vector3(-cubeSize, 0, -cubeSize);
                Vector3 p1 = pos + new Vector3(-cubeSize, 0, -cubeSize);
                Vector3 p2 = pos + new Vector3(cubeSize, 0, -cubeSize);
                Vector3 p3 = pos + new Vector3(cubeSize, 2 * cubeSize, cubeSize);
                Vector3 p4 = pos + new Vector3(-cubeSize, 2 * cubeSize, cubeSize);

                var flatBatch = m_primitivesRenderer3D.FlatBatch();
                flatBatch.QueueQuad(p1, p2, p3, p4, color);
            }

            m_primitivesRenderer3D.Flush(camera.ViewProjectionMatrix);
        }

        // ====================================================================
        // Client_GameStep: 处理每 Tick 的网络事件
        // ====================================================================
        private void Client_GameStep(GameStepData obj)
        {
            // 离开
            foreach (var item in obj.Leaves)
            {
                Log.Information($"[ScMP] Client left: {item.ClientID}");
                if (!IsHost && item.ClientID == 0)
                {
                    HandleHostDisconnected();
                    continue;
                }
                if (!IsHost)
                    m_departedRemoteClientIds.Add(item.ClientID);
                RemoveNetworkPlayer(item.ClientID);
                playerMappingManager.ReleasePlayerIndex(item.ClientID);
            }

            // 加入
            foreach (var item in obj.Joins)
            {
                Log.Information($"[ScMP] Client joining: {item.ClientID}");
                m_departedRemoteClientIds.Remove(item.ClientID);
                // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Handle
                // A single existing peer accepts or refuses a join. Only the room owner is allowed
                // to decide, otherwise another client can accept before the host requests a profile.
                if (!IsHost) continue;
                int joiningClientId = item.ClientID;
                IPEndPoint joiningAddress = item.Address;
                byte[] joinRequestBytes = item.JoinRequestBytes;
                QueueEndOfFrameAction(() => HandleHostJoinRequest(
                    joiningClientId,
                    joiningAddress,
                    joinRequestBytes));
            }

            // 输入消息
            foreach (var item in obj.Inputs)
            {
                if (item.InputBytes == null || item.InputBytes.Length == 0) continue;

                Message message;
                try
                {
                    message = Message.Read(item.InputBytes);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] Failed to parse message: {ex.Message}");
                    continue;
                }

                // 跳过自己发出的消息 (回环消息)
                if (message.GetSenderPort() == client.Address.Port)
                {
                    continue;
                }

                switch (message)
                {
                    case SyncBatchMessage syncBatch:
                        foreach (byte[] payload in syncBatch.Payloads)
                        {
                            try
                            {
                                if (Message.Read(payload) is SyncBatchMessage)
                                    throw new InvalidOperationException("Nested sync batch is not allowed.");
                                Client_DirectInput(item.ClientID, payload);
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"[ScMP] Failed to unpack sync batch item: {ex.Message}");
                            }
                        }
                        break;
                    case ChatMessage chat:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandleChatMessage(chat, item.ClientID));
                        break;
                    case GamePlayerPositionMessage pos:
                        QueueEndOfFrameAction(() => HandleGamePlayerPositionMessage(pos, item.ClientID));
                        break;
                    case GamePlayerPositionsMessage positions:
                        QueueEndOfFrameAction(() =>
                        {
                            if (positions.Players == null) return;
                            foreach (GamePlayerPositionMessage position in positions.Players)
                                HandleGamePlayerPositionMessage(position, item.ClientID);
                        });
                        break;
                    case GamePlayerInputMessage playerInput:
                        QueueEndOfFrameAction(() => HandleGamePlayerInputMessage(
                            playerInput, item.ClientID));
                        break;
                    case PlayerAimMessage playerAim:
                        QueueEndOfFrameAction(() => HandlePlayerAimMessage(playerAim, item.ClientID));
                        break;
                    case PlayerActionMessage playerAction:
                        QueueEndOfFrameAction(() =>
                            HandlePlayerActionMessage(playerAction, item.ClientID));
                        break;
                    case TerrainDigRequestMessage terrainDigRequest:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainDigRequest(terrainDigRequest, item.ClientID));
                        break;
                    case TerrainDigResultMessage terrainDigResult:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainDigResult(terrainDigResult, item.ClientID));
                        break;
                    case GameModifiedCellsMessage cells:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandleModifiedCellsMessage(cells, item.ClientID));
                        break;
                    case TerrainRecoveryMessage terrainRecovery:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainRecoveryMessage(terrainRecovery, item.ClientID));
                        break;
                    case GameWorldInfoMessage1 worldInfo:
                        if (item.ClientID == 0)
                            Dispatcher.Dispatch(() =>
                                NetworkMessageHandler.HandleWorldInfoMessage(worldInfo, item.ClientID));
                        break;
                    case WorldControlRequestMessage worldControl:
                        QueueEndOfFrameAction(() => HandleWorldControlRequest(worldControl, item.ClientID));
                        break;
                    case PlayerProfileMessage playerProfile:
                        QueueEndOfFrameAction(() => HandlePlayerProfileMessage(playerProfile, item.ClientID));
                        break;
                    case PlayerEquipmentMessage playerEquipment:
                        QueueEndOfFrameAction(() => HandlePlayerEquipmentMessage(
                            playerEquipment, item.ClientID));
                        break;
                    case GamePakWorldMessage pakWorld:
                        if (item.ClientID == 0)
                            QueueEndOfFrameAction(() =>
                                NetworkMessageHandler.HandlePakWorldMessage(pakWorld, item.ClientID));
                        break;
                    case GamePakWorldChunkMessage worldChunk:
                        if (item.ClientID == 0)
                            QueueEndOfFrameAction(() => HandleGamePakWorldChunkMessage(worldChunk));
                        break;
                    case GamePakWorldReadyMessage worldReady:
                        QueueEndOfFrameAction(() =>
                            HandleGamePakWorldReadyMessage(worldReady, item.ClientID));
                        break;
                    case GamePakWorldRepairRequestMessage repairRequest:
                        QueueEndOfFrameAction(() =>
                            HandleGamePakWorldRepairRequestMessage(repairRequest, item.ClientID));
                        break;
                    case GamePlayerHealthMessage health:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandlePlayerHealthMessage(health, item.ClientID));
                        break;
                    case GameKickPlayerMessage kick:
                        QueueEndOfFrameAction(() => HandleGameKickPlayerMessage(kick, item.ClientID));
                        break;
                    case EntityMessage entityMessage:
                        QueueEndOfFrameAction(() => HandleAnimalEntityMessage(entityMessage, item.ClientID));
                        break;
                    case BodyUpdateMessage bodyUpdate:
                        QueueEndOfFrameAction(() => HandleAnimalBodyUpdate(bodyUpdate, item.ClientID));
                        break;
                    case AnimalInteractionMessage animalInteraction:
                        QueueEndOfFrameAction(() => HandleAnimalInteractionMessage(
                            animalInteraction, item.ClientID));
                        break;
                    case PickableSyncMessage pickableSync:
                        QueueEndOfFrameAction(() => HandlePickableSyncMessage(pickableSync, item.ClientID));
                        break;
                    case ProjectileSyncMessage projectileSync:
                        QueueEndOfFrameAction(() => HandleProjectileSyncMessage(projectileSync, item.ClientID));
                        break;
                    case ExplosionSyncMessage explosionSync:
                        QueueEndOfFrameAction(() => HandleExplosionSyncMessage(explosionSync, item.ClientID));
                        break;
                    case ContainerSyncMessage containerSync:
                        QueueEndOfFrameAction(() => HandleContainerSyncMessage(containerSync, item.ClientID));
                        break;
                    default:
                        Log.Error($"[ScMP] Unknown message type: {message.GetType().Name}");
                        break;
                }
            }
        }

        // Source: Comms.Drt/Func/Client/Client.cs:Client.DirectInput
        // Reuse the normal message dispatcher while keeping direct network callbacks away from
        // game objects. Individual handlers enqueue their work on Frame.Update.
        private void Client_DirectInput(int sourceClientId, byte[] inputBytes)
        {
            Client_GameStep(new GameStepData
            {
                Joins = Array.Empty<GameStepData.JoinData>(),
                Leaves = Array.Empty<GameStepData.LeaveData>(),
                Inputs = new[]
                {
                    new GameStepData.InputData
                    {
                        ClientID = sourceClientId,
                        InputBytes = inputBytes
                    }
                }
            });
        }

        // Source: Mod/Comms/Comms.Drt/Data/GameStepData.cs:GameStepData.JoinData
        private void HandleHostJoinRequest(
            int joiningClientId,
            IPEndPoint joiningAddress,
            byte[] joinRequestBytes)
        {
            if (!IsHost || m_hostJoinRequests.ContainsKey(joiningClientId))
                return;

            int assignedPlayerIndex = playerMappingManager.AssignPlayerIndex(joiningClientId);
            if (assignedPlayerIndex == -1)
            {
                Log.Information($"[ScMP] Game full, refusing ClientID {joiningClientId}");
                client.RefuseJoinGame(joiningClientId, "Game is full");
                return;
            }

            try
            {
                GameWorldInfoMessage worldInfo = Message.Read(joinRequestBytes) as GameWorldInfoMessage;
                if (worldInfo == null ||
                    SuPlayScreen.WorldData == null ||
                    SuPlayScreen.WorldDataName != worldInfo.Name ||
                    SuPlayScreen.WorldDataLastSaveTime != worldInfo.LastSaveTime)
                {
                    playerMappingManager.ReleasePlayerIndex(joiningClientId);
                    client.RefuseJoinGame(joiningClientId, "Host world snapshot is unavailable");
                    return;
                }

                EnsurePlayerRecordsLoaded();
                string recordKey = GetPlayerRecordKey(
                    worldInfo.PlayerIdentity,
                    worldInfo.PlayerName);
                bool isNewApproval = !m_playerRecords.TryGetValue(
                    recordKey,
                    out NetworkPlayerRecord joiningRecord);
                if (isNewApproval)
                {
                    if (!IsValidRequestedProfile(worldInfo))
                    {
                        playerMappingManager.ReleasePlayerIndex(joiningClientId);
                        client.RefuseJoinGame(joiningClientId, PlayerProfileRequiredReason);
                        return;
                    }
                    joiningRecord = CreateInitialPlayerRecord(worldInfo);
                }

                var request = new HostJoinRequest
                {
                    ClientId = joiningClientId,
                    Address = joiningAddress,
                    RecordKey = recordKey,
                    PlayerRecord = joiningRecord,
                    IsNewApproval = isNewApproval,
                    ReceivedTime = Time.RealTime
                };
                Log.Information($"[ScMP] Reserved PlayerIndex {assignedPlayerIndex} for " +
                    $"ClientID {joiningClientId} ({joiningRecord.Name})");

                if (ScMultiplayerSettings.AutoApproveJoinRequests)
                {
                    ApproveHostJoinRequest(request);
                    return;
                }

                m_hostJoinRequests.Add(joiningClientId, request);
                TryShowNextHostJoinRequest();
            }
            catch (Exception ex)
            {
                playerMappingManager.ReleasePlayerIndex(joiningClientId);
                try
                {
                    client.RefuseJoinGame(joiningClientId, "Invalid join request: " + ex.Message);
                }
                catch
                {
                }
                Log.Error($"[ScMP] Failed to process ClientID {joiningClientId} join: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.Dialogs
        private void UpdateHostJoinRequests()
        {
            if (m_activeJoinDecisionDialog != null &&
                !DialogsManager.Dialogs.Contains(m_activeJoinDecisionDialog))
            {
                if (m_hostJoinRequests.TryGetValue(
                    m_activeJoinDecisionClientId,
                    out HostJoinRequest dismissed))
                {
                    dismissed.Deferred = true;
                }
                m_activeJoinDecisionDialog = null;
                m_activeJoinDecisionClientId = -1;
            }

            if (!IsHost || m_hostJoinRequests.Count == 0)
                return;

            HostJoinRequest[] expired = m_hostJoinRequests.Values
                .Where(request => Time.RealTime - request.ReceivedTime >= 285.0)
                .ToArray();
            foreach (HostJoinRequest request in expired)
                RejectHostJoinRequest(request, "Host approval timed out.");

            if (ScMultiplayerSettings.AutoApproveJoinRequests)
            {
                foreach (HostJoinRequest request in m_hostJoinRequests.Values.ToArray())
                    ApproveHostJoinRequest(request);
            }
            else
            {
                TryShowNextHostJoinRequest();
            }
        }

        private void TryShowNextHostJoinRequest()
        {
            if (!IsHost || ScMultiplayerSettings.AutoApproveJoinRequests ||
                m_activeJoinDecisionDialog != null)
            {
                return;
            }
            HostJoinRequest request = m_hostJoinRequests.Values
                .Where(item => !item.Deferred)
                .OrderBy(item => item.ReceivedTime)
                .FirstOrDefault();
            if (request != null)
                ShowHostJoinDecision(request);
        }

        // Source: Survivalcraft/Game/ListSelectionDialog.cs:ListSelectionDialog
        private void ShowHostJoinDecision(HostJoinRequest request)
        {
            if (request == null || !m_hostJoinRequests.ContainsKey(request.ClientId))
                return;

            string[] decisions = { "允许加入", "拒绝加入", "稍后处理" };
            var dialog = new ListSelectionDialog(
                "加入请求: " + GetHostJoinRequestLabel(request),
                decisions,
                60f,
                item => item.ToString(),
                item =>
                {
                    m_activeJoinDecisionDialog = null;
                    m_activeJoinDecisionClientId = -1;
                    string decision = item?.ToString();
                    if (decision == decisions[0])
                        ApproveHostJoinRequest(request);
                    else if (decision == decisions[1])
                        RejectHostJoinRequest(request, "Host declined the join request.");
                    else
                    {
                        request.Deferred = true;
                        TryShowNextHostJoinRequest();
                    }
                });
            m_activeJoinDecisionDialog = dialog;
            m_activeJoinDecisionClientId = request.ClientId;
            DialogsManager.ShowDialog(null, dialog);
        }

        private void ApproveHostJoinRequest(HostJoinRequest request)
        {
            if (request == null)
                return;
            m_hostJoinRequests.Remove(request.ClientId);
            CloseActiveJoinDecision(request.ClientId);
            AcceptNetworkPlayerJoin(
                request.ClientId,
                request.RecordKey,
                request.PlayerRecord,
                request.IsNewApproval);
            TryShowNextHostJoinRequest();
        }

        private void RejectHostJoinRequest(HostJoinRequest request, string reason)
        {
            if (request == null)
                return;
            m_hostJoinRequests.Remove(request.ClientId);
            CloseActiveJoinDecision(request.ClientId);
            playerMappingManager.ReleasePlayerIndex(request.ClientId);
            try
            {
                client.RefuseJoinGame(request.ClientId, reason);
            }
            catch (Exception ex)
            {
                Log.Warning($"[ScMP] Could not refuse ClientID {request.ClientId}: {ex.Message}");
            }
            TryShowNextHostJoinRequest();
        }

        private void CloseActiveJoinDecision(int clientId)
        {
            if (m_activeJoinDecisionClientId != clientId)
                return;
            Dialog dialog = m_activeJoinDecisionDialog;
            m_activeJoinDecisionDialog = null;
            m_activeJoinDecisionClientId = -1;
            if (dialog != null && DialogsManager.Dialogs.Contains(dialog))
                DialogsManager.HideDialog(dialog);
        }

        private static string GetHostJoinRequestLabel(HostJoinRequest request)
        {
            string name = string.IsNullOrWhiteSpace(request?.PlayerRecord?.Name)
                ? "Player"
                : request.PlayerRecord.Name;
            return request?.Address == null ? name : name + " | " + request.Address;
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.AcceptJoinGame
        private void AcceptNetworkPlayerJoin(int joiningClientId, string recordKey,
            NetworkPlayerRecord joiningRecord, bool isNewApproval = false)
        {
            try
            {
                m_lastSentInventoryValues.Remove(joiningClientId);
                m_lastSentInventoryCounts.Remove(joiningClientId);
                m_playerRecords[recordKey] = joiningRecord;
                m_pendingAcceptedJoinKeys[joiningClientId] = recordKey;
                m_containerStates.Clear();
                m_playerRecordsDirty = true;
                SavePlayerRecords();
                m_joinCatchUpJournals[joiningClientId] = new JoinCatchUpJournal
                {
                    StartTick = client.Step
                };
                HostedWorldSnapshot snapshot = CaptureHostedWorldSnapshot();
                // Source: Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.SendDataMessageToAllClients
                // Keep large direct broadcasts off this endpoint until its world is loaded.
                // Ordered ticks continue so the joining client's expected step stays current.
                SetServerClientGameTrafficEnabled(joiningClientId, enabled: false);
                client.AcceptJoinGame(joiningClientId);
                BeginWorldTransfer(
                    snapshot.Name, snapshot.WorldData, snapshot.LastSaveTime, joiningClientId,
                    m_sessionRandomSeed, snapshot.TerrainSequence,
                    snapshot.RandomStates, joiningRecord);
                Log.Information($"[ScMP] Accepted ClientID {joiningClientId} and queued live world snapshot " +
                    $"(Tick={snapshot.Tick}, Bytes={snapshot.WorldData.Length})");
            }
            catch (Exception ex)
            {
                SetServerClientGameTrafficEnabled(joiningClientId, enabled: true);
                RemoveNetworkPlayer(joiningClientId);
                if (isNewApproval)
                {
                    m_playerRecords.Remove(recordKey);
                    m_playerRecordsDirty = true;
                    SavePlayerRecords();
                }
                playerMappingManager.ReleasePlayerIndex(joiningClientId);
                client.RefuseJoinGame(joiningClientId, "Failed to prepare player: " + ex.Message);
                Log.Error($"[ScMP] Failed to accept ClientID {joiningClientId}: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.SaveProject
        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ExportWorld
        private HostedWorldSnapshot CaptureHostedWorldSnapshot()
        {
            Project project = GameManager.Project ??
                throw new InvalidOperationException("The hosted project is not loaded.");
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            GameManager.SaveProject(waitForCompletion: true, showErrorDialog: false);

            string snapshotDirectory = Storage.CombinePaths(
                Storage.GetDirectoryName(gameInfo.DirectoryName),
                ".ScMpJoinSnapshot-" + Guid.NewGuid().ToString("N"));
            string snapshotSystemPath = Storage.GetSystemPath(snapshotDirectory);
            byte[] exportedWorld;
            try
            {
                CopyWorldDirectoryForSnapshot(
                    Storage.GetSystemPath(gameInfo.DirectoryName), snapshotSystemPath);
                using var stream = new MemoryStream();
                WorldsManager.ExportWorld(snapshotDirectory, stream);
                exportedWorld = stream.ToArray();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(snapshotSystemPath))
                        Directory.Delete(snapshotSystemPath, recursive: true);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[ScMP] Failed to remove temporary join snapshot: {ex.Message}");
                }
            }

            var networkPlayerIndices = new HashSet<string>(m_networkPlayerData.Values
                .Where(player => player != null)
                .Select(player => player.PlayerIndex.ToString(CultureInfo.InvariantCulture)));
            byte[] sanitizedWorld = RemoveNetworkPlayersFromWorldArchive(
                exportedWorld, networkPlayerIndices);
            WorldInfo worldInfo = WorldsManager.GetWorldInfo(gameInfo.DirectoryName);
            return new HostedWorldSnapshot
            {
                Name = worldInfo?.WorldSettings?.Name ?? SuPlayScreen.WorldDataName,
                WorldData = sanitizedWorld,
                LastSaveTime = worldInfo?.LastSaveTime ?? DateTime.Now,
                Tick = client.Step,
                TerrainSequence = m_hostTerrainSequence,
                RandomStates = CaptureSubsystemRandomStates()
            };
        }

        // Source: Survivalcraft/Game/TerrainSerializer23.cs:RegionFileStorage.GetRegionStream
        // Source: Engine/Engine/Storage.cs:Storage.OpenFile
        private static void CopyWorldDirectoryForSnapshot(string sourceDirectory,
            string targetDirectory)
        {
            if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException("The hosted world directory does not exist.");
            Directory.CreateDirectory(targetDirectory);
            foreach (string sourcePath in Directory.EnumerateFiles(
                sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                string targetPath = Path.Combine(targetDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                CopySnapshotFile(sourcePath, targetPath);
            }
        }

        // Source: Engine/Engine/Storage.cs:Storage.OpenFile
        private static void CopySnapshotFile(string sourcePath, string targetPath)
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.SequentialScan);
            using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            source.CopyTo(target, 64 * 1024);
        }

        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.PackWorld
        // Source: GameEntitySystem/Project.cs:Project.SaveEntities
        private static byte[] RemoveNetworkPlayersFromWorldArchive(byte[] worldData,
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

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.Save
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

        // Source: RuthlessConquest/Net/Client.cs:Client.Client
        // The world uses Comms reliable UDP with an application-level sliding window and repair
        // requests. A full SHA-256 keeps the independently delivered chunks end-to-end verifiable.
        private void BeginWorldTransfer(string name, byte[] worldData, DateTime lastSaveTime,
            int targetClientId, int randomSeed, long terrainSequence,
            Dictionary<string, long> randomStates, NetworkPlayerRecord playerRecord)
        {
            if (worldData == null || worldData.Length == 0 ||
                worldData.Length > MaximumWorldTransferSize)
                throw new InvalidOperationException("Cached world data has an invalid size.");
            m_nextWorldTransferId = m_nextWorldTransferId == int.MaxValue
                ? 1
                : m_nextWorldTransferId + 1;
            int transferId = m_nextWorldTransferId;
            int chunkCount = (worldData.Length + WorldTransferChunkSize - 1) /
                WorldTransferChunkSize;
            var manifest = new GamePakWorldMessage(name, Array.Empty<byte>(), lastSaveTime,
                targetClientId, randomSeed, randomStates, playerRecord)
            {
                TransferId = transferId,
                ChunkCount = chunkCount,
                TotalLength = worldData.Length,
                WorldSha256 = SHA256.HashData(worldData),
                TerrainSequenceBaseline = terrainSequence
            };
            m_outgoingWorldTransfers[targetClientId] = new OutgoingWorldTransfer
            {
                TransferId = transferId,
                TargetClientId = targetClientId,
                StartTime = Time.RealTime,
                WorldData = worldData,
                ChunkCount = chunkCount,
                Manifest = manifest,
                ChunkLastQueueTimes = new double[chunkCount]
            };
            m_worldTransfersAwaitingReady[targetClientId] = transferId;
            // The joining peer requests the manifest after ConnectAccepted has been processed.
            // Sending it here can be ACKed by Comm and then discarded by Peer because the
            // application connection is not established yet.
        }

        internal void RecordJoinCatchUpMessage(byte[] payload, bool sequenced, bool latest)
        {
            if (!IsHost || payload == null || payload.Length == 0 ||
                m_joinCatchUpJournals.Count == 0)
                return;
            foreach (JoinCatchUpJournal journal in m_joinCatchUpJournals.Values)
            {
                if (journal.TotalBytes + payload.Length > MaximumJoinCatchUpBytes)
                {
                    journal.DroppedMessages++;
                    continue;
                }
                byte[] copy = new byte[payload.Length];
                Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
                journal.Messages.Add(new JoinCatchUpMessage
                {
                    Payload = copy,
                    Sequenced = sequenced,
                    Latest = latest
                });
                journal.TotalBytes += copy.Length;
            }
        }

        private void FlushJoinCatchUpJournal(int targetClientId)
        {
            if (!m_joinCatchUpJournals.TryGetValue(targetClientId,
                out JoinCatchUpJournal journal))
                return;
            JoinCatchUpMessage[] batch = journal.Messages.ToArray();
            journal.Messages.Clear();
            journal.TotalBytes = 0;
            journal.ReplayRound++;
            foreach (JoinCatchUpMessage item in batch)
            {
                if (item?.Payload == null) continue;
                client.SendDirectInput(targetClientId, item.Payload,
                    sequenced: true, latest: false);
                journal.TotalMessagesSent++;
                journal.TotalBytesSent += item.Payload.Length;
            }
            Log.Information($"[ScMP] Join catch-up batch sent: ClientID={targetClientId}, " +
                $"Round={journal.ReplayRound}, StartTick={journal.StartTick}, " +
                $"Messages={batch.Length}, Bytes={batch.Sum(item => item?.Payload?.Length ?? 0)}, " +
                $"Dropped={journal.DroppedMessages}");
            if (journal.DroppedMessages > 0)
                Log.Warning($"[ScMP] Join catch-up limit reached for ClientID={targetClientId}; " +
                    $"{journal.DroppedMessages} transient messages were replaced by subsequent full-state sync.");
        }

        private void SendPendingWorldTransferChunks()
        {
            if (m_outgoingWorldTransfers.Count == 0) return;
            // Source: ScMultiplayer.cs:RequestMissingWorldTransferChunks
            // Source: RuthlessConquest/Net/ServerGame.cs:ServerGame.Run
            // Keep a small reliable-UDP window so delayed ACKs on a lossy remote link do not turn
            // premature retransmissions into a self-sustaining burst.
            int maximumBudget = m_networkPlayerData.Any(item => item.Key > 0)
                ? MaximumWorldTransferChunksPerGameplayTick
                : MaximumWorldTransferChunksPerNetworkTick;
            int budget = maximumBudget;
            int[] targetClientIds = m_outgoingWorldTransfers.Keys.OrderBy(id => id).ToArray();
            if (targetClientIds.Length == 0) return;
            m_worldTransferCursor %= targetClientIds.Length;
            int attemptsRemaining = targetClientIds.Length *
                (MaximumWorldTransferChunksPerNetworkTick + 1);
            while (budget > 0 && attemptsRemaining-- > 0)
            {
                int targetClientId = targetClientIds[m_worldTransferCursor];
                m_worldTransferCursor = (m_worldTransferCursor + 1) % targetClientIds.Length;
                if (!m_outgoingWorldTransfers.TryGetValue(targetClientId,
                    out OutgoingWorldTransfer transfer) ||
                    !transfer.StartRequested ||
                    (transfer.InitialSendComplete && transfer.RepairChunkIndices.Count == 0))
                    continue;
                if (GetWorldTransferRelayUnackedPackets(targetClientId) >=
                    MaximumWorldTransferUnackedPackets)
                    continue;

                int chunkIndex;
                bool isRepair = transfer.RepairChunkIndices.Count > 0;
                if (isRepair)
                {
                    chunkIndex = transfer.RepairChunkIndices.Dequeue();
                    transfer.QueuedRepairChunkIndices.Remove(chunkIndex);
                }
                else
                {
                    int windowEnd = Math.Min(transfer.ChunkCount,
                        transfer.HighestContiguousChunkIndex + 1 + WorldTransferWindowChunks);
                    if (transfer.NextChunkIndex >= windowEnd)
                        continue;
                    chunkIndex = transfer.NextChunkIndex++;
                }
                if (!QueueWorldTransferChunk(transfer, chunkIndex))
                {
                    if (isRepair)
                    {
                        if (transfer.QueuedRepairChunkIndices.Add(chunkIndex))
                            transfer.RepairChunkIndices.Enqueue(chunkIndex);
                    }
                    else
                        transfer.NextChunkIndex--;
                    break;
                }
                transfer.ChunkLastQueueTimes[chunkIndex] = Time.RealTime;
                budget--;
                if (!transfer.InitialSendComplete &&
                    transfer.NextChunkIndex >= transfer.ChunkCount)
                {
                    transfer.InitialSendComplete = true;
                    Log.Information($"[ScMP] World transfer initially queued: ClientID={transfer.TargetClientId}, " +
                        $"Transfer={transfer.TransferId}, Chunks={transfer.ChunkCount}");
                }
            }
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
        private void StartWorldTransferSender()
        {
            if (m_worldTransferSendTask != null) return;
            m_worldTransferSendCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = m_worldTransferSendCancellation.Token;
            m_worldTransferSendTask = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await m_worldTransferSendSignal.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    int burstCount = 0;
                    while (m_worldTransferSendQueue.TryDequeue(
                        out WorldTransferChunkSendWork work))
                    {
                        Interlocked.Decrement(ref m_worldTransferQueuedWorkCount);
                        if (work == null || work.Generation !=
                                Volatile.Read(ref m_worldTransferGeneration) ||
                            cancellationToken.IsCancellationRequested)
                            continue;
                        try
                        {
                            int offset = work.ChunkIndex * WorldTransferChunkSize;
                            int count = Math.Min(WorldTransferChunkSize,
                                work.WorldData.Length - offset);
                            var data = new byte[count];
                            Array.Copy(work.WorldData, offset, data, 0, count);
                            NetworkMessageSender.SendPakWorldChunk(work.TargetClientId,
                                new GamePakWorldChunkMessage
                                {
                                    TransferId = work.TransferId,
                                    TargetClientId = work.TargetClientId,
                                    ChunkIndex = work.ChunkIndex,
                                    ChunkCount = work.ChunkCount,
                                    TotalLength = work.WorldData.Length,
                                    Data = data
                                });
                            if (++burstCount % 4 == 0)
                                await Task.Delay(1, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                                Log.Error($"[ScMP] World transfer sender failed: {ex.Message}");
                        }
                    }
                }
            }, cancellationToken);
        }

        private bool QueueWorldTransferChunk(OutgoingWorldTransfer transfer, int chunkIndex)
        {
            if (transfer == null || chunkIndex < 0 || chunkIndex >= transfer.ChunkCount ||
                Interlocked.CompareExchange(ref m_worldTransferQueuedWorkCount, 0, 0) >=
                    MaximumQueuedWorldTransferChunks)
                return false;
            Interlocked.Increment(ref m_worldTransferQueuedWorkCount);
            m_worldTransferSendQueue.Enqueue(new WorldTransferChunkSendWork
            {
                Generation = Volatile.Read(ref m_worldTransferGeneration),
                TransferId = transfer.TransferId,
                TargetClientId = transfer.TargetClientId,
                ChunkIndex = chunkIndex,
                ChunkCount = transfer.ChunkCount,
                WorldData = transfer.WorldData
            });
            m_worldTransferSendSignal.Release();
            return true;
        }

        // Source: ScMultiplayer.cs:HandleGamePakWorldChunkMessage
        private void RequestMissingWorldTransferChunks()
        {
            double now = Time.RealTime;
            foreach (IncomingWorldTransfer transfer in m_incomingWorldTransfers.Values.ToArray())
            {
                if (transfer == null || transfer.Chunks == null ||
                    transfer.ReceivedChunkCount >= transfer.Chunks.Length ||
                    now - transfer.LastStatusRequestTime < WorldTransferStatusInterval)
                    continue;
                bool stalled = now - transfer.LastProgressTime >= WorldTransferRepairInterval;
                bool requestRepair = stalled &&
                    now - transfer.LastRepairRequestTime >= WorldTransferRepairRequestInterval;
                int missingEnd = stalled
                    ? Math.Min(transfer.HighestContiguousChunkIndex + 1 +
                        WorldTransferWindowChunks, transfer.Chunks.Length)
                    : Math.Min(transfer.HighestReceivedChunkIndex + 1,
                        transfer.Chunks.Length);
                int[] missing = requestRepair
                    ? Enumerable.Range(
                            transfer.HighestContiguousChunkIndex + 1,
                            Math.Max(0, missingEnd - transfer.HighestContiguousChunkIndex - 1))
                        .Where(index => transfer.Chunks[index] == null)
                        .Take(MaximumWorldTransferRepairChunks)
                        .ToArray()
                    : Array.Empty<int>();
                transfer.LastStatusRequestTime = now;
                if (missing.Length > 0)
                {
                    transfer.LastRepairRequestTime = now;
                    transfer.RepairRequestCount++;
                }
                NetworkMessageSender.SendPakWorldRepairRequest(
                    new GamePakWorldRepairRequestMessage
                    {
                        TransferId = transfer.TransferId,
                        RequestManifest = transfer.Manifest == null,
                        HighestContiguousChunkIndex = transfer.HighestContiguousChunkIndex,
                        HighestReceivedChunkIndex = transfer.HighestReceivedChunkIndex,
                        MissingChunkIndices = missing
                    });
            }
        }

        private void HandleGamePakWorldRepairRequestMessage(
            GamePakWorldRepairRequestMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0 ||
                !m_outgoingWorldTransfers.TryGetValue(sourceClientId,
                    out OutgoingWorldTransfer transfer) ||
                (message.TransferId > 0 && transfer.TransferId != message.TransferId))
                return;
            if (message.RequestManifest && transfer.Manifest != null)
            {
                NetworkMessageSender.SendPakWorldManifest(sourceClientId, transfer.Manifest);
            }
            transfer.StartRequested = true;
            transfer.HighestContiguousChunkIndex = Math.Max(
                transfer.HighestContiguousChunkIndex,
                Math.Min(message.HighestContiguousChunkIndex, transfer.ChunkCount - 1));
            foreach (int index in message.MissingChunkIndices ?? Array.Empty<int>())
            {
                if (index < 0 || index >= transfer.NextChunkIndex ||
                    index >= transfer.ChunkLastQueueTimes.Length ||
                    Time.RealTime - transfer.ChunkLastQueueTimes[index] <
                        WorldTransferRepairRequestInterval ||
                    !transfer.QueuedRepairChunkIndices.Add(index))
                    continue;
                transfer.RepairChunkIndices.Enqueue(index);
                transfer.RepairChunkQueueCount++;
            }
        }

        // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Clients
        private int GetWorldTransferRelayUnackedPackets(int targetClientId)
        {
            IPEndPoint address = GetServerClientAddress(targetClientId);
            if (address == null)
                return MaximumWorldTransferUnackedPackets;
            return server.Peer.Comm.GetUnackedPacketsCount(address);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Clients
        private static IPEndPoint GetServerClientAddress(int targetClientId)
        {
            if (server == null || client == null)
                return null;
            ServerGame game = server.Games.FirstOrDefault(item => item.GameID == client.GameID);
            return game?.Clients.FirstOrDefault(item =>
                item.ClientID == targetClientId)?.Address;
        }

        // Source: Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.SetClientGameTrafficEnabled
        private void SetServerClientGameTrafficEnabled(int targetClientId, bool enabled)
        {
            if (server == null || client == null) return;
            ServerGame game = server.Games.FirstOrDefault(item => item.GameID == client.GameID);
            game?.SetClientGameTrafficEnabled(targetClientId, enabled);
        }

        // ====================================================================
        // 消息处理
        // ====================================================================
        public void HandleGamePlayerPositionMessage(GamePlayerPositionMessage msg, int clientID)
        {
            // Source: msg.PlayerIndex = 发送方的 ClientID
            // 写入 RemotePlayers 而非本地 ComponentPlayers, 避免覆盖本地玩家
            // Source: Comms/GameStepData.Inputs
            // The transport ClientID is authoritative. The ID serialized in a packet can belong
            // to an earlier connection after a client leaves and rejoins.
            if (IsHost || clientID != 0) return;
            int remoteClientId = msg.PlayerIndex;
            if (remoteClientId != client.ClientID &&
                RemotePlayers.TryGetValue(remoteClientId, out NetworkPlayerState previousState) &&
                msg.ServerTick < previousState.ServerTick)
                return;
            if (remoteClientId == client.ClientID)
                ApplyAuthoritativeLocalPlayerState(msg);
            if (remoteClientId == client.ClientID)
                return; // 忽略自己发回的消息
            // A delayed position packet can arrive after Client_GameStep reports a leave. Do not
            // recreate a presentation-only avatar until the current connection registers it again.
            if (!m_networkPlayerData.ContainsKey(remoteClientId))
                return;

            NetworkPlayerState state;
            if (!RemotePlayers.TryGetValue(remoteClientId, out state))
            {
                state = new NetworkPlayerState { ClientID = remoteClientId };
                RemotePlayers[remoteClientId] = state;
            }

            float previousPokingPhase = state.PokingPhase;
            bool pokeStarted = msg.PokingPhase > 0f &&
                (previousPokingPhase <= 0f ||
                    msg.PokingPhase + 0.05f < previousPokingPhase);
            state.Position = msg.Position;
            state.Rotation = msg.Rotation;
            state.Velocity = msg.Velocity;
            state.ServerTick = msg.ServerTick;
            state.LookAngles = msg.LookAngles;
            state.WalkOrder = msg.WalkOrder;
            state.JumpOrder = msg.JumpOrder;
            state.PokingPhase = msg.PokingPhase;
            state.AttackOrder = msg.AttackOrder;
            state.RowLeftOrder = msg.RowLeftOrder;
            state.RowRightOrder = msg.RowRightOrder;
            state.IsCrouching = msg.IsCrouching;
            state.IsFlying = msg.IsFlying;
            state.IsRiding = msg.IsRiding;
            state.IsGrounded = msg.IsGrounded;
            state.ActiveSlotIndex = msg.ActiveSlotIndex;
            state.HandItemValue = msg.HandItemValue;
            state.HandItemCount = msg.HandItemCount;
            state.ItemOffset = msg.ItemOffset;
            state.ItemRotation = msg.ItemRotation;
            state.AimHandAngle = msg.AimHandAngle;
            state.LastUpdateTime = Time.RealTime;

            if (m_networkPlayerData.TryGetValue(remoteClientId, out PlayerData playerData) &&
                playerData.ComponentPlayer != null)
            {
                // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Poke
                // Position snapshots are a fallback when the reliable poke edge is delayed.
                if (pokeStarted && Time.RealTime - state.LastPokeEventTime > 0.1)
                {
                    playerData.ComponentPlayer.ComponentMiner?.Poke(forceRestart: true);
                    state.LastPokeEventTime = Time.RealTime;
                }
                ComponentMiner remoteMiner = playerData.ComponentPlayer.ComponentMiner;
                if (remoteMiner != null)
                {
                    // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Update
                    // Human arm animation reads PokingPhase directly. Applying the authoritative
                    // phase also recovers a short poke whose reliable edge arrived late or was lost.
                    if (msg.PokingPhase > 0f ||
                        Time.RealTime - state.LastPokeEventTime > 0.3)
                    {
                        ModManager.ModParentField.ModifyParentField(
                            remoteMiner, "<PokingPhase>k__BackingField", msg.PokingPhase,
                            typeof(ComponentMiner));
                    }
                }
                ComponentBody body = playerData.ComponentPlayer.ComponentBody;
                body.TargetCrouchFactor = msg.IsCrouching ? 1f : 0f;
                // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.LookAngles
                // Body rotation carries yaw; pitch is stored separately in m_lookAngles.
                ComponentLocomotion locomotion = playerData.ComponentPlayer.ComponentLocomotion;
                if (locomotion != null)
                {
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "m_lookAngles", msg.LookAngles, typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "<LastWalkOrder>k__BackingField", msg.WalkOrder, typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "<LastJumpOrder>k__BackingField", msg.JumpOrder, typeof(ComponentLocomotion));
                    locomotion.IsCreativeFlyEnabled = msg.IsFlying;
                }
                ComponentCreatureModel remoteModel = playerData.ComponentPlayer.ComponentCreatureModel;
                if (remoteModel != null)
                {
                    remoteModel.AttackOrder = msg.AttackOrder;
                    remoteModel.RowLeftOrder = msg.RowLeftOrder;
                    remoteModel.RowRightOrder = msg.RowRightOrder;
                    remoteModel.InHandItemOffsetOrder = msg.ItemOffset;
                    remoteModel.InHandItemRotationOrder = msg.ItemRotation;
                    remoteModel.AimHandAngleOrder = msg.AimHandAngle;
                }
                IInventory inventory = playerData.ComponentPlayer.ComponentMiner?.Inventory;
                if (inventory != null && msg.SlotValues != null)
                {
                    if (msg.ActiveSlotIndex >= 0 && msg.ActiveSlotIndex < inventory.SlotsCount)
                        inventory.ActiveSlotIndex = msg.ActiveSlotIndex;
                    ApplyInventory(inventory, msg.SlotValues, msg.SlotCounts);
                }
            }
        }

        private void ApplyAuthoritativeLocalPlayerState(GamePlayerPositionMessage msg)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer == null) return;

            float delaySample = MathUtils.Clamp(
                (client.Step - msg.ServerTick) * ServerTickDuration, 0f, 0.5f);
            m_smoothedNetworkDelay = m_smoothedNetworkDelay <= 0f
                ? delaySample
                : MathUtils.Lerp(m_smoothedNetworkDelay, delaySample, 0.1f);
            // Client movement is predicted and never rewound. The host-side split-screen avatar
            // follows this trajectory with a bounded catch-up velocity instead.

            IInventory inventory = localPlayer.ComponentMiner?.Inventory;
            if (inventory == null) return;
            // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
            // The owning client selects its hotbar slot. The host records that selection for its
            // replica and other peers, but never writes a delayed slot selection back to the owner.
            if (msg.SlotValues == null || msg.SlotValues.Length == 0) return;
            // Source: ScMultiplayer.cs:HandleAuthoritativePickableAcquire
            // A position snapshot captured earlier in the same host tick can arrive after Acquire.
            // Do not let that equal-tick stale inventory erase the newly collected item.
            if (m_hasAuthoritativeLocalInventory &&
                msg.ServerTick <= m_lastAuthoritativeLocalInventoryTick)
                return;
            int slotsCount = Math.Min(inventory.SlotsCount,
                Math.Min(msg.SlotValues.Length, msg.SlotCounts?.Length ?? 0));
            bool localInventoryChanged = m_hasAuthoritativeLocalInventory &&
                !InventoryMatches(inventory, m_authoritativeLocalSlotValues,
                    m_authoritativeLocalSlotCounts);
            bool hostAcknowledgedLocalInventory = localInventoryChanged &&
                InventoryMatches(inventory, msg.SlotValues, msg.SlotCounts, slotsCount);
            if (localInventoryChanged && !hostAcknowledgedLocalInventory)
                return;
            ApplyInventory(inventory, msg.SlotValues, msg.SlotCounts);
            m_authoritativeLocalSlotValues = msg.SlotValues.Take(slotsCount).ToArray();
            m_authoritativeLocalSlotCounts = msg.SlotCounts.Take(slotsCount).ToArray();
            m_lastAuthoritativeLocalInventoryTick = msg.ServerTick;
            m_hasAuthoritativeLocalInventory = true;
        }

        private static bool InventoryMatches(IInventory inventory, int[] values, int[] counts,
            int slotsCount = -1)
        {
            if (inventory == null || values == null || counts == null) return false;
            int count = slotsCount >= 0
                ? Math.Min(slotsCount, Math.Min(values.Length, counts.Length))
                : Math.Min(inventory.SlotsCount, Math.Min(values.Length, counts.Length));
            if (count != inventory.SlotsCount && slotsCount < 0) return false;
            for (int i = 0; i < count; i++)
            {
                if (inventory.GetSlotValue(i) != values[i] ||
                    inventory.GetSlotCount(i) != counts[i])
                    return false;
            }
            return true;
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public bool TrySendAnimalAttackRequest(ComponentPlayer player, Ray3 hitRay)
        {
            if (IsHost || client?.IsConnected != true || player?.ComponentMiner == null ||
                GameManager.Project == null)
                return false;
            BodyRaycastResult? result = player.ComponentMiner.Raycast<BodyRaycastResult>(
                hitRay, RaycastMode.Interaction);
            if (!result.HasValue) return false;

            Entity targetEntity = result.Value.ComponentBody?.Entity;
            if (targetEntity == null) return false;
            ushort targetId = 0;
            foreach (KeyValuePair<ushort, Entity> item in m_remoteAnimals)
            {
                if (ReferenceEquals(item.Value, targetEntity))
                {
                    targetId = item.Key;
                    break;
                }
            }
            if (targetId == 0) return false;

            Vector3 hitPoint = result.Value.HitPoint();
            if (Vector3.DistanceSquared(hitPoint, player.ComponentCreatureModel.EyePosition) > 2.25f * 2.25f)
                return false;
            Vector3 direction = hitRay.Direction.LengthSquared() > 0.0001f
                ? Vector3.Normalize(hitRay.Direction)
                : Vector3.UnitZ;
            var message = new AnimalInteractionMessage(targetId, client.Step, hitPoint, direction);
            // Source: ScMultiplayer.cs:HandleAnimalInteractionMessage
            client.SendDirectInput(0, Message.WriteWithSender(message, client.Address));
            return true;
        }

        private void HandleAnimalInteractionMessage(AnimalInteractionMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0 || GameManager.Project == null)
                return;
            if (!m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData))
                return;
            ComponentPlayer attacker = playerData?.ComponentPlayer;
            ComponentMiner miner = attacker?.ComponentMiner;
            if (attacker == null || miner == null || attacker.ComponentHealth?.Health <= 0f)
                return;

            Entity targetEntity = m_hostAnimalIds
                .FirstOrDefault(item => item.Value == message.TargetAnimalId).Key;
            ComponentCreature target = targetEntity?.FindComponent<ComponentCreature>();
            ComponentBody targetBody = target?.ComponentBody;
            if (targetEntity?.IsAddedToProject != true || targetBody == null ||
                target.ComponentHealth?.Health <= 0f)
                return;

            Vector3 eyePosition = attacker.ComponentCreatureModel.EyePosition;
            Vector3 targetPoint = targetBody.BoundingBox.Center();
            Vector3 toTarget = targetPoint - eyePosition;
            if (toTarget.LengthSquared() > 4f * 4f) return;
            Vector3 direction = message.HitDirection.LengthSquared() > 0.0001f
                ? Vector3.Normalize(message.HitDirection)
                : Vector3.Normalize(toTarget);
            if (toTarget.LengthSquared() > 0.0001f &&
                Vector3.Dot(direction, Vector3.Normalize(toTarget)) < 0.2f)
                return;

            // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Attack
            // A network attack is already spatially validated above. Establish predator aggro
            // immediately instead of waiting for a second request when host-side hit RNG misses.
            CreatureCategory predatorMask = CreatureCategory.LandPredator | CreatureCategory.WaterPredator;
            if ((target.Category & predatorMask) != 0)
            {
                ComponentChaseBehavior chase = targetEntity.FindComponent<ComponentChaseBehavior>();
                if (chase != null && chase.Target == null)
                    chase.Attack(attacker, 30f, 60f, true);
                ComponentHerdBehavior herd = targetEntity.FindComponent<ComponentHerdBehavior>();
                herd?.CallNearbyCreaturesHelp(attacker, 20f, 30f, false);
            }

            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Hit
            // The host recomputes hit probability, tool power, damage and Attacked events.
            miner.Hit(targetBody, targetPoint, direction);
            if (m_hostAnimalSync.TryGetValue(targetEntity, out AnimalSyncMetadata metadata))
            {
                metadata.NextSendTime = 0.0;
                metadata.HighPriorityUntil = Time.RealTime + 3.0;
            }
        }

        private void HandleAnimalEntityMessage(EntityMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null) return;
            if (message.Action == EntityMessage.EntityAction.Add)
            {
                if (!string.IsNullOrWhiteSpace(message.TemplateName))
                    m_remoteAnimalTemplates[message.EntityId] = message.TemplateName;
                return;
            }

            RemoveRemoteAnimal(message.EntityId);
        }

        // Source: Survivalcraft/Game/ComponentSpawn.cs:ComponentSpawn.Update
        private void RemoveRemoteAnimal(ushort id)
        {
            if (m_remoteAnimals.TryGetValue(id, out Entity entity))
            {
                StopRemoteAnimalShapeshiftEffect(entity);
                if (entity?.IsAddedToProject == true && entity.Project == GameManager.Project)
                    entity.Project.RemoveEntity(entity, true);
                m_remoteAnimals.Remove(id);
            }
            m_remoteAnimalTemplates.Remove(id);
            m_remoteAnimalSync.Remove(id);
            m_loggedRemoteAnimalFailures.Remove(id);
        }

        private Entity EnsureRemoteAnimal(ushort id, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            if (m_remoteAnimals.TryGetValue(id, out Entity existing) && existing?.IsAddedToProject == true)
                return existing;
            if (!m_remoteAnimalTemplates.TryGetValue(id, out string templateName) ||
                string.IsNullOrWhiteSpace(templateName) || GameManager.Project == null)
                return null;

            try
            {
                Entity entity = DatabaseManager.CreateEntity(
                    GameManager.Project, templateName, new ValuesDictionary(), true);
                ComponentBody body = entity?.FindComponent<ComponentBody>();
                if (entity == null || body == null) return null;
                body.Position = position;
                body.Rotation = rotation;
                body.Velocity = velocity;
                GameManager.Project.AddEntity(entity);
                // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.RemoveUpdateable
                // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.Update
                // Remote animals are presentation replicas. Their local AI and spawn state machine
                // must not compete with authoritative movement or despawn them at chunk edges.
                SubsystemUpdate subsystemUpdate = GameManager.Project.FindSubsystem<SubsystemUpdate>(true);
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    if (updateable is ComponentBehavior || updateable is ComponentLocomotion ||
                        updateable is ComponentHealth || updateable is ComponentShapeshifter ||
                        ReferenceEquals(updateable, entity.FindComponent<ComponentSpawn>()))
                        subsystemUpdate.RemoveUpdateable(updateable);
                }
                StopRemoteAnimalShapeshiftEffect(entity);
                m_remoteAnimals[id] = entity;
                m_loggedRemoteAnimalFailures.Remove(id);
                return entity;
            }
            catch (Exception ex)
            {
                if (m_loggedRemoteAnimalFailures.Add(id))
                    Log.Error($"[ScMP] Failed to recreate animal {id} ({templateName}): {ex.Message}");
                m_remoteAnimals.Remove(id);
                return null;
            }
        }

        private void SetRemoteAnimalTemplate(ushort id, string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName)) return;
            bool changed = m_remoteAnimalTemplates.TryGetValue(id, out string oldTemplate) &&
                !string.Equals(oldTemplate, templateName, StringComparison.Ordinal);
            if (changed && m_remoteAnimals.TryGetValue(id, out Entity oldEntity))
            {
                // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.ComponentSpawn_Despawned
                StopRemoteAnimalShapeshiftEffect(oldEntity);
                if (oldEntity?.IsAddedToProject == true && oldEntity.Project == GameManager.Project)
                    oldEntity.Project.RemoveEntity(oldEntity, true);
                m_remoteAnimals.Remove(id);
                m_remoteAnimalSync.Remove(id);
            }
            m_remoteAnimalTemplates[id] = templateName;
        }

        private void HandleAnimalBodyUpdate(BodyUpdateMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message?.Bodies == null || GameManager.Project == null) return;
            MaintainClientWorldObjects();
            HashSet<ushort> fullSnapshotIds = message.IsFullSnapshot
                ? new HashSet<ushort>()
                : null;
            foreach (BodyUpdateMessage.BodyItem item in message.Bodies)
            {
                fullSnapshotIds?.Add(item.EntityId);
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Template) &&
                    !string.IsNullOrWhiteSpace(item.TemplateName))
                    SetRemoteAnimalTemplate(item.EntityId, item.TemplateName);
                Entity entity = EnsureRemoteAnimal(item.EntityId, item.Position, item.Rotation, item.Velocity);
                StopRemoteAnimalShapeshiftEffect(entity);
                ComponentCreature creature = entity?.FindComponent<ComponentCreature>();
                ComponentBody body = creature?.ComponentBody;
                if (creature == null || body == null) continue;

                if (!m_remoteAnimalSync.TryGetValue(item.EntityId,
                    out RemoteAnimalSyncState networkState))
                {
                    networkState = new RemoteAnimalSyncState();
                    m_remoteAnimalSync[item.EntityId] = networkState;
                }
                int previousServerTick = networkState.LastServerTick;
                Vector3 previousVelocity = networkState.Velocity;
                bool hadTransform = networkState.HasTransform;
                float snapshotInterval = networkState.EstimatedSnapshotInterval;
                if (message.ServerTick < previousServerTick) continue;
                if (previousServerTick > 0 && message.ServerTick > previousServerTick)
                {
                    snapshotInterval = MathUtils.Clamp(
                        (message.ServerTick - previousServerTick) * ServerTickDuration,
                        0.03f, RemoteAnimalPredictionLimit);
                    networkState.EstimatedSnapshotInterval = MathUtils.Lerp(
                        networkState.EstimatedSnapshotInterval, snapshotInterval, 0.2f);
                }
                networkState.LastServerTick = message.ServerTick;
                networkState.LastUpdateTime = Time.RealTime;
                float delaySample = MathUtils.Clamp(
                    (client.Step - message.ServerTick) * ServerTickDuration,
                    0f, RemoteAnimalPredictionLimit);
                networkState.EstimatedDelay = networkState.EstimatedDelay <= 0f
                    ? delaySample
                    : MathUtils.Lerp(networkState.EstimatedDelay, delaySample, 0.15f);

                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.BehaviorState))
                {
                    if (!networkState.SimulationSeedApplied ||
                        networkState.SimulationSeed != item.SimulationSeed)
                    {
                        networkState.SimulationSeed = item.SimulationSeed;
                        networkState.SimulationSeedApplied = true;
                        ApplyAnimalSimulationSeed(entity, item.SimulationSeed);
                    }
                    ApplyRemoteAnimalBehaviorState(item.EntityId, entity, item);
                }
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Health) &&
                    creature.ComponentHealth != null)
                {
                    // Source: ComponentHealth.cs:ComponentHealth.Update
                    if (!m_remoteAnimalSync.TryGetValue(item.EntityId,
                        out RemoteAnimalSyncState state))
                    {
                        state = new RemoteAnimalSyncState();
                        m_remoteAnimalSync[item.EntityId] = state;
                    }
                    float health = MathUtils.Saturate(item.Health);
                    bool wasInjured = state.HasHealth && health < state.LastHealth - 0.001f;
                    ModManager.ModParentField.ModifyParentField(
                        creature.ComponentHealth, "<Health>k__BackingField",
                        health, typeof(ComponentHealth));
                    ModManager.ModParentField.ModifyParentField(
                        creature.ComponentHealth, "m_lastHealth", health,
                        typeof(ComponentHealth));
                    ModManager.ModParentField.ModifyParentField(
                        creature.ComponentHealth, "<HealthChange>k__BackingField", 0f,
                        typeof(ComponentHealth));
                    if (wasInjured && creature.ComponentCreatureModel != null)
                    {
                        // Source: ComponentCreatureModel.cs:ComponentCreatureModel.Update
                        ModManager.ModParentField.ModifyParentField(
                            creature.ComponentCreatureModel, "m_injuryColorFactor", 1f,
                            typeof(ComponentCreatureModel));
                        // Source: Survivalcraft/Game/ComponentCreatureSounds.cs:ComponentCreatureSounds.PlayPainSound
                        creature.ComponentCreatureSounds?.PlayPainSound();
                    }
                    state.LastHealth = health;
                    state.HasHealth = true;
                    if (health <= 0f && !state.DeathTime.HasValue)
                    {
                        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                        SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<
                            SubsystemGameInfo>(false);
                        if (gameInfo != null)
                        {
                            state.DeathTime = gameInfo.TotalElapsedGameTime;
                            ModManager.ModParentField.ModifyParentField(
                                creature.ComponentHealth, "<DeathTime>k__BackingField",
                                state.DeathTime, typeof(ComponentHealth));
                        }
                    }
                    else if (health > 0f && state.DeathTime.HasValue)
                    {
                        state.DeathTime = null;
                        state.LocalDespawnStarted = false;
                        ModManager.ModParentField.ModifyParentField(
                            creature.ComponentHealth, "<DeathTime>k__BackingField",
                            (double?)null, typeof(ComponentHealth));
                    }
                }

                // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
                // Store authoritative keyframes here. UpdateRemoteAnimalPresentations performs
                // continuous prediction and correction instead of snapping on packet arrival.
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Position))
                    networkState.Position = item.Position;
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Rotation))
                    networkState.Rotation = item.Rotation;
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Velocity))
                {
                    networkState.Velocity = item.Velocity;
                    if (hadTransform && snapshotInterval > 0.001f)
                    {
                        Vector3 acceleration = (item.Velocity - previousVelocity) / snapshotInterval;
                        float accelerationLength = acceleration.Length();
                        const float maxAnimalAcceleration = 24f;
                        if (accelerationLength > maxAnimalAcceleration)
                            acceleration *= maxAnimalAcceleration / accelerationLength;
                        networkState.Acceleration = Vector3.Lerp(
                            networkState.Acceleration, acceleration, 0.35f);
                    }
                    else
                    {
                        networkState.Acceleration = Vector3.Zero;
                    }
                }
                if (!networkState.HasTransform &&
                    item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Position))
                {
                    body.Position = networkState.Position;
                    body.Rotation = networkState.Rotation;
                    body.Velocity = networkState.Velocity;
                    networkState.HasTransform = true;
                    networkState.PresentationInitialized = true;
                    networkState.SmoothedVelocity = networkState.Velocity;
                    networkState.HasSmoothedVelocity = true;
                }
                ComponentLocomotion locomotion = creature.ComponentLocomotion;
                if (locomotion != null)
                {
                    if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.LookAngles))
                        networkState.LookAngles = item.LookAngles;
                    if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Movement))
                    {
                        networkState.WalkOrder = item.WalkOrder;
                        networkState.FlyOrder = item.FlyOrder;
                        networkState.SwimOrder = item.SwimOrder;
                        networkState.TurnOrder = item.TurnOrder;
                        networkState.JumpOrder = item.JumpOrder;
                        locomotion.WalkOrder = item.WalkOrder;
                        locomotion.FlyOrder = item.FlyOrder;
                        locomotion.SwimOrder = item.SwimOrder;
                        locomotion.TurnOrder = item.TurnOrder;
                        locomotion.JumpOrder = item.JumpOrder;
                        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
                        // Publish Last* immediately; the restored local locomotion then advances
                        // these same orders between authoritative keyframes.
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastWalkOrder>k__BackingField", item.WalkOrder,
                            typeof(ComponentLocomotion));
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastFlyOrder>k__BackingField", item.FlyOrder,
                            typeof(ComponentLocomotion));
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastSwimOrder>k__BackingField", item.SwimOrder,
                            typeof(ComponentLocomotion));
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastTurnOrder>k__BackingField", item.TurnOrder,
                            typeof(ComponentLocomotion));
                        ModManager.ModParentField.ModifyParentField(locomotion,
                            "<LastJumpOrder>k__BackingField", item.JumpOrder,
                            typeof(ComponentLocomotion));
                    }
                }
                networkState.AttackOrder = item.AttackOrder;
                networkState.FeedOrder = item.FeedOrder;
                ComponentCreatureModel model = creature.ComponentCreatureModel;
                if (model != null)
                {
                    model.AttackOrder = networkState.AttackOrder;
                    model.FeedOrder = networkState.FeedOrder;
                }
            }
            if (fullSnapshotIds != null &&
                message.ServerTick >= m_lastFullAnimalSnapshotTick)
            {
                // Source: ScMultiplayer.cs:SendAdaptiveAnimalUpdates
                // Preserve a replica that already received a newer incremental add, otherwise the
                // complete snapshot is the authoritative membership set for this server tick.
                foreach (ushort id in m_remoteAnimals.Keys.Where(id =>
                    !fullSnapshotIds.Contains(id) &&
                    (!m_remoteAnimalSync.TryGetValue(id, out RemoteAnimalSyncState state) ||
                        state.LastServerTick <= message.ServerTick)).ToArray())
                    RemoveRemoteAnimal(id);
                m_lastFullAnimalSnapshotTick = message.ServerTick;
            }
        }

        private void ApplyRemoteAnimalBehaviorState(ushort id, Entity entity, BodyUpdateMessage.BodyItem item)
        {
            if (!m_remoteAnimalSync.TryGetValue(id, out RemoteAnimalSyncState state))
            {
                state = new RemoteAnimalSyncState();
                m_remoteAnimalSync[id] = state;
            }
            state.SyncTier = item.SyncTier;
            state.BehaviorState = item.ActiveBehaviorState ?? string.Empty;
            state.TargetEntityId = item.TargetEntityId;
            state.HerdName = item.HerdName ?? string.Empty;
            string activeBehaviorName = (item.ActiveBehaviorState ?? string.Empty).Split(':')[0];
            int separator = (item.ActiveBehaviorState ?? string.Empty).IndexOf(':');
            string stateName = separator >= 0
                ? item.ActiveBehaviorState.Substring(separator + 1)
                : string.Empty;
            foreach (ComponentBehavior behavior in entity.FindComponents<ComponentBehavior>())
            {
                if (behavior == null) continue;
                bool active = behavior.GetType().Name == activeBehaviorName;
                behavior.IsActive = active;
                if (!active || string.IsNullOrEmpty(stateName)) continue;
                StateMachine stateMachine = GetBehaviorStateMachine(behavior);
                if (stateMachine != null && stateMachine.CurrentState != stateName)
                {
                    try { stateMachine.TransitionTo(stateName); }
                    catch (Exception) { }
                }
            }
            ApplyRemoteAnimalAggroTarget(entity, state.TargetEntityId);
        }

        // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Attack
        private void ApplyRemoteAnimalAggroTarget(Entity entity, int targetEntityId)
        {
            ComponentChaseBehavior chase = entity?.FindComponent<ComponentChaseBehavior>();
            if (chase == null) return;
            ComponentCreature target = ResolveRemoteAnimalTarget(targetEntityId);
            ModManager.ModParentField.ModifyParentField(
                chase, "m_target", target, typeof(ComponentChaseBehavior));
        }

        // Source: ScMultiplayer.cs:GetCreatureTargetNetworkId
        private ComponentCreature ResolveRemoteAnimalTarget(int targetEntityId)
        {
            if (targetEntityId > 0)
            {
                return m_remoteAnimals.TryGetValue((ushort)targetEntityId, out Entity animal)
                    ? animal?.FindComponent<ComponentCreature>()
                    : null;
            }
            if (targetEntityId < 0)
            {
                int clientId = -targetEntityId - 1;
                return m_networkPlayerData.TryGetValue(clientId, out PlayerData playerData)
                    ? playerData?.ComponentPlayer
                    : null;
            }
            return null;
        }

        private StateMachine GetBehaviorStateMachine(ComponentBehavior behavior)
        {
            for (Type type = behavior?.GetType();
                type != null && type != typeof(object); type = type.BaseType)
            {
                FieldInfo field = type.GetField("m_stateMachine",
                    BindingFlags.Instance | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                if (field?.FieldType == typeof(StateMachine))
                    return ModManager.ModParentField.GetParentField<StateMachine>(
                        behavior, field.Name, field.DeclaringType);
            }
            return null;
        }

        // Source: ComponentShapeshifter.cs:ComponentShapeshifter.ComponentSpawn_Despawned
        private static void StopRemoteAnimalShapeshiftEffect(Entity entity)
        {
            ComponentShapeshifter shapeshifter = entity?.FindComponent<ComponentShapeshifter>();
            if (shapeshifter == null) return;
            // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.m_particleSystem
            // An inactive shapeshifter stores null; use the non-generic SuAPI getter so null is
            // accepted instead of aborting creation of the remote animal.
            ShapeshiftParticleSystem particleSystem =
                ModManager.ModParentField.GetParentField(
                    shapeshifter, "m_particleSystem", typeof(ComponentShapeshifter))
                    as ShapeshiftParticleSystem;
            if (particleSystem != null) particleSystem.Stopped = true;
            ModManager.ModParentField.ModifyParentField(
                shapeshifter, "m_particleSystem", null, typeof(ComponentShapeshifter));
            ModManager.ModParentField.ModifyParentField(
                shapeshifter, "m_spawnEntityTemplateName", string.Empty,
                typeof(ComponentShapeshifter));
        }

        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
        // Source: Survivalcraft/Game/ComponentCreatureModel.cs:ComponentCreatureModel.Update
        private void UpdateRemoteAnimalPresentations(float dt)
        {
            if (IsHost || GameManager.Project == null) return;
            float step = MathUtils.Clamp(dt, 0f, 0.05f);
            double now = Time.RealTime;
            SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<
                SubsystemGameInfo>(false);
            var expiredAnimals = new List<ushort>();
            foreach (KeyValuePair<ushort, RemoteAnimalSyncState> item in
                m_remoteAnimalSync.ToArray())
            {
                if (!m_remoteAnimals.TryGetValue(item.Key, out Entity entity) ||
                    entity?.IsAddedToProject != true)
                    continue;
                ComponentCreature creature = entity.FindComponent<ComponentCreature>();
                ComponentBody body = creature?.ComponentBody;
                RemoteAnimalSyncState state = item.Value;
                if (body == null || !state.HasTransform) continue;

                float arrivalAge = (float)MathUtils.Clamp(
                    now - state.LastUpdateTime, 0.0, RemoteAnimalPredictionLimit);
                float stepAge = MathUtils.Clamp(
                    (client.Step - state.LastServerTick) * ServerTickDuration,
                    0f, RemoteAnimalPredictionLimit);
                float predictionLimit = MathUtils.Clamp(
                    state.EstimatedSnapshotInterval * 1.25f, 0.1f,
                    RemoteAnimalPredictionLimit);
                float predictionTime = MathUtils.Min(
                    MathUtils.Max(MathUtils.Max(arrivalAge, stepAge), state.EstimatedDelay),
                    predictionLimit);
                float accelerationTime = MathUtils.Min(predictionTime, 0.35f);
                Vector3 predictedVelocity = state.Velocity +
                    state.Acceleration * accelerationTime;
                Vector3 predictedPosition = state.Position +
                    state.Velocity * predictionTime +
                    0.5f * state.Acceleration * accelerationTime * accelerationTime;
                Vector3 error = predictedPosition - body.Position;
                float errorSquared = error.LengthSquared();
                if (!state.PresentationInitialized ||
                    errorSquared > MathUtils.Sqr(RemoteAnimalSnapDistance))
                {
                    body.Position = state.Position;
                    body.Rotation = state.Rotation;
                    body.Velocity = state.Velocity;
                    state.SmoothedVelocity = state.Velocity;
                    state.HasSmoothedVelocity = true;
                    state.PresentationInitialized = true;
                }
                else
                {
                    float tierFactor = MathUtils.Saturate(state.SyncTier / 4f);
                    Vector3 remainingError = predictedPosition - body.Position;
                    float correctionHorizon = MathUtils.Lerp(0.5f, 0.2f, tierFactor);
                    Vector3 correctionVelocity = remainingError / correctionHorizon;
                    float maxCorrectionSpeed = MathUtils.Lerp(3f, 9f, tierFactor);
                    float correctionSpeed = correctionVelocity.Length();
                    if (correctionSpeed > maxCorrectionSpeed)
                        correctionVelocity *= maxCorrectionSpeed / correctionSpeed;
                    Vector3 desiredVelocity = predictedVelocity + correctionVelocity;
                    if (!state.HasSmoothedVelocity)
                    {
                        state.SmoothedVelocity = body.Velocity;
                        state.HasSmoothedVelocity = true;
                    }
                    float velocityBlend = 1f - (float)Math.Exp(
                        -MathUtils.Lerp(5f, 11f, tierFactor) * step);
                    state.SmoothedVelocity = Vector3.Lerp(
                        state.SmoothedVelocity, desiredVelocity, velocityBlend);
                    body.Velocity = state.SmoothedVelocity;

                    float rotationBlend = 1f - (float)Math.Exp(
                        -MathUtils.Lerp(7f, 14f, tierFactor) * step);
                    body.Rotation = Quaternion.Slerp(
                        body.Rotation, state.Rotation, rotationBlend);
                }

                ComponentLocomotion locomotion = creature.ComponentLocomotion;
                if (locomotion != null)
                {
                    // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
                    // Physics follows the smoothed authoritative trajectory. Publish the state
                    // machine orders only as Last* values so model animation cannot apply a second,
                    // differently-oriented locomotion force and make animals slide diagonally.
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastWalkOrder>k__BackingField", state.WalkOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastFlyOrder>k__BackingField", state.FlyOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastSwimOrder>k__BackingField", state.SwimOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastTurnOrder>k__BackingField", state.TurnOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(locomotion,
                        "<LastJumpOrder>k__BackingField", state.JumpOrder,
                        typeof(ComponentLocomotion));
                    float lookBlend = 1f - (float)Math.Exp(-8f * step);
                    Vector2 lookAngles = Vector2.Lerp(
                        locomotion.LookAngles, state.LookAngles, lookBlend);
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "m_lookAngles", lookAngles,
                        typeof(ComponentLocomotion));
                }
                ComponentCreature aggroTarget = ResolveRemoteAnimalTarget(state.TargetEntityId);
                ApplyRemoteAnimalAggroTarget(entity, state.TargetEntityId);
                ComponentCreatureModel model = creature.ComponentCreatureModel;
                if (model != null)
                {
                    if (aggroTarget?.ComponentCreatureModel != null)
                        model.LookAtOrder = aggroTarget.ComponentCreatureModel.EyePosition;
                    // Animal models consume and clear these orders every animation update.
                    model.AttackOrder = state.AttackOrder;
                    model.FeedOrder = state.FeedOrder;
                }

                // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                // Source: Survivalcraft/Game/ComponentSpawn.cs:ComponentSpawn.Update
                // Health and spawn updates are disabled on presentation replicas. Mirror only the
                // original corpse/despawn lifecycle as a fallback for a delayed/lost host removal.
                ComponentHealth health = creature.ComponentHealth;
                ComponentSpawn spawn = creature.ComponentSpawn;
                if (state.DeathTime.HasValue && gameInfo != null && health != null &&
                    spawn != null && health.CorpseDuration > 0f)
                {
                    double corpseAge = gameInfo.TotalElapsedGameTime - state.DeathTime.Value;
                    if (!state.LocalDespawnStarted && corpseAge >= health.CorpseDuration)
                    {
                        spawn.Despawn();
                        state.LocalDespawnStarted = true;
                    }
                    if (state.LocalDespawnStarted &&
                        corpseAge >= health.CorpseDuration + spawn.DespawnDuration)
                        expiredAnimals.Add(item.Key);
                }
            }
            foreach (ushort id in expiredAnimals) RemoveRemoteAnimal(id);
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        // Client physics can independently add attraction and collision velocity between host
        // snapshots. Reapply the host trajectory after the local update so idle items do not
        // oscillate sideways and moving items remain smooth between 10Hz snapshots.
        private void UpdateRemotePickablePresentations(float dt)
        {
            if (IsHost || GameManager.Project == null) return;
            float step = MathUtils.Clamp(dt, 0f, 0.05f);
            double now = Time.RealTime;
            SubsystemFluidBlockBehavior fluidBehavior = GameManager.Project.FindSubsystem<
                SubsystemFluidBlockBehavior>(false);
            foreach (KeyValuePair<ushort, RemotePickableNetworkState> item in
                m_remotePickableStates.ToArray())
            {
                if (!m_remotePickables.TryGetValue(item.Key, out Pickable pickable) ||
                    pickable == null || pickable.ToRemove)
                    continue;
                RemotePickableNetworkState state = item.Value;
                float age = (float)MathUtils.Clamp(now - state.LastUpdateTime, 0.0, 0.25);
                Vector3 predictedPosition = state.Position + state.Velocity * age;
                bool isInFluid = fluidBehavior?.CalculateFlowSpeed(
                    Terrain.ToCell(pickable.Position.X),
                    Terrain.ToCell(pickable.Position.Y + 0.1f),
                    Terrain.ToCell(pickable.Position.Z)) != null;
                bool isResting = !state.FlyToPosition.HasValue &&
                    state.Velocity.LengthSquared() < 0.04f;
                if (isResting && !isInFluid)
                {
                    pickable.Position = state.Position;
                    pickable.Velocity = Vector3.Zero;
                    state.PresentationVelocity = Vector3.Zero;
                    state.PresentationInitialized = true;
                }
                else
                {
                    Vector3 error = predictedPosition - pickable.Position;
                    if (!state.PresentationInitialized)
                    {
                        pickable.Position = state.Position;
                        pickable.Velocity = state.Velocity;
                        state.PresentationVelocity = state.Velocity;
                        state.PresentationInitialized = true;
                    }
                    else
                    {
                        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
                        // Preserve native collision and buoyancy, but translate every positional
                        // error toward the host so even sub-block drift cannot remain unresolved.
                        float positionResponse = isInFluid ? 5f : 12f;
                        float positionBlend = 1f - (float)Math.Exp(-positionResponse * step);
                        Vector3 positionCorrection = error * positionBlend;
                        float maxPositionStep = (isInFluid ? 5f : 24f) * step;
                        float positionCorrectionLength = positionCorrection.Length();
                        if (positionCorrectionLength > maxPositionStep)
                            positionCorrection *= maxPositionStep / positionCorrectionLength;
                        pickable.Position += positionCorrection;
                        if (Vector3.DistanceSquared(pickable.Position, predictedPosition) < 0.0004f)
                            pickable.Position = predictedPosition;

                        float correctionHorizon = isInFluid ? 0.45f : 0.18f;
                        Vector3 correctionVelocity = error / correctionHorizon;
                        float maxCorrectionSpeed = isInFluid ? 4f : 12f;
                        float correctionSpeed = correctionVelocity.Length();
                        if (correctionSpeed > maxCorrectionSpeed)
                            correctionVelocity *= maxCorrectionSpeed / correctionSpeed;
                        Vector3 desiredVelocity = state.Velocity + correctionVelocity;
                        float response = isInFluid ? 5f : 10f;
                        float blend = 1f - (float)Math.Exp(-response * step);
                        state.PresentationVelocity = Vector3.Lerp(
                            state.PresentationVelocity, desiredVelocity, blend);
                        pickable.Velocity = state.PresentationVelocity;
                    }
                }
                if (m_pendingPickablePickups.TryGetValue(item.Key,
                    out PendingPickablePickupPresentation pickup))
                {
                    Vector3 target = ResolvePickupPresentationTarget(
                        pickup.CollectorClientId, pickable.Position);
                    if (now < pickup.CompleteTime)
                    {
                        pickable.FlyToPosition = target;
                    }
                    else
                    {
                        m_pendingPickablePickups.Remove(item.Key);
                        if (pickup.PlaySound)
                        {
                            GameManager.Project.FindSubsystem<SubsystemAudio>(false)?.PlaySound(
                                "Audio/PickableCollected", 0.7f, -0.4f,
                                pickable.Position, 2f, autoDelay: false);
                        }
                        if (pickup.RemainingCount <= 0)
                        {
                            pickable.ToRemove = true;
                            m_remotePickables.Remove(item.Key);
                            m_remotePickableRecords.Remove(item.Key);
                            m_remotePickableStates.Remove(item.Key);
                            continue;
                        }
                        pickable.Count = pickup.RemainingCount;
                        pickable.FlyToPosition = state.FlyToPosition;
                    }
                }
                else
                {
                    pickable.FlyToPosition = state.FlyToPosition;
                }
            }
        }

        private Pickable EnsureRemotePickable(ushort id, Vector3 position, Vector3 velocity)
        {
            if (m_remotePickables.TryGetValue(id, out Pickable existing) && existing != null && !existing.ToRemove)
                return existing;
            if (!m_remotePickableRecords.TryGetValue(id, out RemotePickableRecord record)) return null;
            SubsystemPickables subsystem = GameManager.Project?.FindSubsystem<SubsystemPickables>(false);
            if (subsystem == null) return null;
            Pickable pickable;
            m_applyingNetworkPickable = true;
            try
            {
                pickable = subsystem.AddPickable(record.Value, record.Count,
                    position, velocity, record.StuckMatrix);
            }
            finally
            {
                m_applyingNetworkPickable = false;
            }
            if (pickable != null) m_remotePickables[id] = pickable;
            return pickable;
        }

        private void HandlePickableSyncMessage(PickableSyncMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null || GameManager.Project == null) return;
            MaintainClientWorldObjects();
            switch (message.Action)
            {
                case PickableSyncMessage.PickAction.Create:
                    m_remotePickableRecords[message.Id] = new RemotePickableRecord
                    {
                        Value = message.Value,
                        Count = message.Count,
                        StuckMatrix = message.StuckMatrix
                    };
                    if (!m_remotePickableStates.TryGetValue(message.Id,
                        out RemotePickableNetworkState createdState))
                    {
                        createdState = new RemotePickableNetworkState
                        {
                            Position = message.Position,
                            Velocity = message.Velocity,
                            PresentationVelocity = message.Velocity,
                            FlyToPosition = message.FlyToPosition,
                            LastUpdateTime = Time.RealTime
                        };
                        m_remotePickableStates[message.Id] = createdState;
                    }
                    Pickable created = EnsureRemotePickable(message.Id,
                        createdState.Position, createdState.Velocity);
                    if (created != null)
                    {
                        created.FlyToPosition = createdState.FlyToPosition;
                        created.StuckMatrix = message.StuckMatrix;
                    }
                    break;
                case PickableSyncMessage.PickAction.UpdatePosition:
                    foreach (PickableSyncMessage.PickablePos state in message.Positions)
                    {
                        if (!m_remotePickableStates.TryGetValue(state.Id,
                            out RemotePickableNetworkState networkState))
                        {
                            networkState = new RemotePickableNetworkState
                            {
                                PresentationVelocity = state.Velocity
                            };
                            m_remotePickableStates[state.Id] = networkState;
                        }
                        networkState.Position = state.Position;
                        networkState.Velocity = state.Velocity;
                        networkState.FlyToPosition = state.FlyToPosition;
                        networkState.LastUpdateTime = Time.RealTime;
                        Pickable pickable = EnsureRemotePickable(state.Id, state.Position, state.Velocity);
                        if (pickable == null) continue;
                        pickable.FlyToPosition = state.FlyToPosition;
                    }
                    break;
                case PickableSyncMessage.PickAction.Acquire:
                    HandleAuthoritativePickableAcquire(message);
                    break;
                case PickableSyncMessage.PickAction.Delete:
                    if (m_remotePickables.TryGetValue(message.Id, out Pickable removed) && removed != null)
                        removed.ToRemove = true;
                    m_remotePickables.Remove(message.Id);
                    m_remotePickableRecords.Remove(message.Id);
                    m_remotePickableStates.Remove(message.Id);
                    m_pendingPickablePickups.Remove(message.Id);
                    break;
                case PickableSyncMessage.PickAction.SetFlyTo:
                    if (m_remotePickables.TryGetValue(message.Id, out Pickable target) && target != null)
                        target.FlyToPosition = message.FlyToPosition;
                    if (m_remotePickableStates.TryGetValue(
                        message.Id, out RemotePickableNetworkState targetState))
                    {
                        targetState.FlyToPosition = message.FlyToPosition;
                        targetState.LastUpdateTime = Time.RealTime;
                    }
                    break;
                case PickableSyncMessage.PickAction.WaterSplash:
                    SubsystemTerrain terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(false);
                    SubsystemParticles particles = GameManager.Project.FindSubsystem<SubsystemParticles>(false);
                    particles?.AddParticleSystem(new WaterSplashParticleSystem(
                        terrain, message.Position, large: false));
                    GameManager.Project.FindSubsystem<SubsystemAudio>(false)?.PlayRandomSound(
                        "Audio/Splashes", 1f, m_audioEventRandom.Float(-0.2f, 0.2f),
                        message.Position, 6f, autoDelay: true);
                    break;
            }
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        private void HandleAuthoritativePickableAcquire(PickableSyncMessage message)
        {
            if (message.CollectorClientId == client.ClientID &&
                message.ServerTick >= m_lastAuthoritativeLocalInventoryTick &&
                message.SlotValues != null && message.SlotCounts != null)
            {
                SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
                ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
                IInventory inventory = localPlayer?.ComponentMiner?.Inventory;
                if (inventory != null)
                {
                    ApplyInventory(inventory, message.SlotValues, message.SlotCounts);
                    int slotsCount = Math.Min(inventory.SlotsCount,
                        Math.Min(message.SlotValues.Length, message.SlotCounts.Length));
                    m_authoritativeLocalSlotValues = message.SlotValues.Take(slotsCount).ToArray();
                    m_authoritativeLocalSlotCounts = message.SlotCounts.Take(slotsCount).ToArray();
                    m_lastAuthoritativeLocalInventoryTick = message.ServerTick;
                    m_hasAuthoritativeLocalInventory = true;
                    m_lastLocalInventoryValues = CaptureInventoryValues(inventory);
                    m_lastLocalInventoryCounts = CaptureInventoryCounts(inventory);
                }
            }

            if (m_remotePickableRecords.TryGetValue(message.Id,
                out RemotePickableRecord record))
                record.Count = message.Count;
            if (message.Count > 0)
            {
                if (m_remotePickables.TryGetValue(message.Id, out Pickable partial) &&
                    partial != null)
                    partial.Count = message.Count;
                return;
            }

            Vector3 target = ResolvePickupPresentationTarget(
                message.CollectorClientId, Vector3.Zero);
            double duration = 0.12;
            if (m_remotePickables.TryGetValue(message.Id, out Pickable pickable) &&
                pickable != null)
            {
                float distance = Vector3.Distance(pickable.Position, target);
                duration = MathUtils.Clamp(distance / 6f, 0.08f, 0.3f);
                pickable.FlyToPosition = target;
            }
            m_pendingPickablePickups[message.Id] = new PendingPickablePickupPresentation
            {
                CollectorClientId = message.CollectorClientId,
                RemainingCount = message.Count,
                CompleteTime = Time.RealTime + duration,
                PlaySound = message.PlaySound
            };
        }

        private Vector3 ResolvePickupPresentationTarget(int collectorClientId, Vector3 fallback)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer player;
            if (collectorClientId == client.ClientID)
            {
                player = players?.ComponentPlayers.FirstOrDefault(item =>
                    !m_networkPlayerData.Values.Contains(item.PlayerData));
            }
            else
            {
                player = m_networkPlayerData.TryGetValue(collectorClientId,
                    out PlayerData playerData) ? playerData?.ComponentPlayer : null;
            }
            return player?.ComponentBody != null
                ? player.ComponentBody.Position + new Vector3(0f, 0.75f, 0f)
                : fallback;
        }

        private void SynchronizeProjectiles()
        {
            SubsystemProjectiles subsystem = GameManager.Project?.FindSubsystem<SubsystemProjectiles>(false);
            if (subsystem == null) return;
            if (IsHost)
            {
                var active = new HashSet<Projectile>(subsystem.Projectiles.Where(p => p != null && !p.ToRemove));
                foreach (Projectile projectile in active)
                {
                    bool isNewProjectile = !m_hostProjectileIds.TryGetValue(projectile, out ushort id);
                    if (isNewProjectile) id = GetOrCreateHostProjectileId(projectile);
                    ushort ownerClientId = GetProjectileOwnerClientId(projectile);
                    var message = new ProjectileSyncMessage(id,
                        isNewProjectile
                            ? ProjectileSyncMessage.ProjectileType.Add
                            : ProjectileSyncMessage.ProjectileType.Update,
                        projectile.Value, projectile.Position, projectile.Velocity,
                        projectile.AngularVelocity, projectile.TrailOffset, ownerClientId,
                        client.Step, projectile.IsIncendiary);
                    // Source: ScMultiplayer.cs:HandleProjectileSyncMessage
                    NetworkMessageSender.SendScheduledMessage(-1, message,
                        latest: message.Action == ProjectileSyncMessage.ProjectileType.Update);
                }
                foreach (KeyValuePair<Projectile, ushort> item in m_hostProjectileIds.ToArray())
                {
                    if (active.Contains(item.Key)) continue;
                    NetworkMessageSender.SendScheduledMessage(-1, new ProjectileSyncMessage(
                        item.Value, ProjectileSyncMessage.ProjectileType.Remove, 0,
                        Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0,
                        client.Step, false));
                    m_hostProjectileIds.Remove(item.Key);
                }
                return;
            }

            var remoteSet = new HashSet<Projectile>(m_remoteProjectiles.Values.Where(p => p != null));
            foreach (Projectile projectile in subsystem.Projectiles.ToArray())
            {
                if (projectile == null || remoteSet.Contains(projectile)) continue;
                ComponentPlayer ownerPlayer = projectile.Owner?.Entity?.FindComponent<ComponentPlayer>();
                bool isLocallyOwned = ownerPlayer != null &&
                    !m_networkPlayerData.Values.Contains(ownerPlayer.PlayerData);
                if (isLocallyOwned)
                {
                    if (!m_clientPredictedProjectiles.ContainsKey(projectile))
                        m_clientPredictedProjectiles[projectile] = Time.RealTime;
                    if (Time.RealTime - m_clientPredictedProjectiles[projectile] <=
                        ClientProjectilePredictionGrace)
                        continue;
                }
                projectile.ToRemove = true;
                m_clientPredictedProjectiles.Remove(projectile);
            }
            foreach (Projectile predicted in m_clientPredictedProjectiles.Keys.Where(
                projectile => projectile == null || projectile.ToRemove ||
                    !subsystem.Projectiles.Contains(projectile)).ToArray())
                m_clientPredictedProjectiles.Remove(predicted);
        }

        // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.FireProjectile
        private ushort GetProjectileOwnerClientId(Projectile projectile)
        {
            ComponentPlayer owner = projectile?.Owner?.Entity?.FindComponent<ComponentPlayer>();
            if (owner == null) return ushort.MaxValue;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (!ReferenceEquals(item.Value, owner.PlayerData)) continue;
                return item.Key >= ushort.MaxValue ? ushort.MaxValue : (ushort)item.Key;
            }
            return 0;
        }

        private ushort GetOrCreateHostProjectileId(Projectile projectile)
        {
            if (projectile == null) return 0;
            if (m_hostProjectileIds.TryGetValue(projectile, out ushort id)) return id;
            do
            {
                id = m_nextProjectileId++;
            }
            while (id == 0 || m_hostProjectileIds.ContainsValue(id));
            m_hostProjectileIds[projectile] = id;
            return id;
        }

        internal int GetProjectileOwnerClientIdForHit(Projectile projectile)
        {
            ushort ownerClientId = GetProjectileOwnerClientId(projectile);
            return ownerClientId == ushort.MaxValue ? -1 : ownerClientId;
        }

        internal bool IsLocalPredictedProjectile(Projectile projectile)
        {
            return !IsHost && IsLocallyOwnedProjectile(projectile);
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        internal void PublishAuthoritativeProjectileHit(Projectile projectile,
            int ownerClientId, Vector3 hitPoint, Vector3 hitDirection, float damage)
        {
            if (!IsHost || client?.IsConnected != true || projectile == null ||
                ownerClientId <= 0 || damage <= 0f)
                return;
            ushort id = GetOrCreateHostProjectileId(projectile);
            if (id == 0) return;
            var message = new ProjectileSyncMessage(id,
                ProjectileSyncMessage.ProjectileType.Hit, projectile.Value,
                hitPoint, hitDirection,
                projectile.Owner?.ComponentBody?.Velocity ?? Vector3.Zero,
                Vector3.Zero, (ushort)ownerClientId, client.Step,
                projectile.IsIncendiary)
            {
                HitDamage = damage
            };
            NetworkMessageSender.SendProjectileHit(ownerClientId, message);
        }

        private void HandleProjectileSyncMessage(ProjectileSyncMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null) return;
            if (message.Action == ProjectileSyncMessage.ProjectileType.Hit)
            {
                HandleProjectileHitResult(message);
                return;
            }
            SubsystemProjectiles subsystem = GameManager.Project?.FindSubsystem<SubsystemProjectiles>(false);
            if (subsystem == null) return;
            if (message.Action == ProjectileSyncMessage.ProjectileType.Remove)
            {
                if (m_remoteProjectiles.TryGetValue(message.ProjectileId, out Projectile removed))
                    removed.ToRemove = true;
                m_remoteProjectiles.Remove(message.ProjectileId);
                return;
            }
            float age = MathUtils.Clamp(
                (client.Step - message.ServerStep) * ServerTickDuration, 0f, 0.35f);
            Vector3 targetPosition = message.Position + message.Velocity * age;
            targetPosition.Y -= 5f * age * age;
            Vector3 targetVelocity = message.Velocity + new Vector3(0f, -10f * age, 0f);
            bool adoptedPrediction = false;
            bool createdProjectile = false;
            if (!m_remoteProjectiles.TryGetValue(message.ProjectileId, out Projectile projectile) ||
                projectile == null || projectile.ToRemove)
            {
                // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.FireProjectile
                // Add can beat the 50ms prediction scan. Inspect live projectiles directly and
                // only adopt a prediction owned by the ClientID carried in the host message.
                projectile = FindClientPredictedProjectile(subsystem, message, targetPosition,
                    targetVelocity);
                if (projectile != null)
                {
                    adoptedPrediction = true;
                    m_clientPredictedProjectiles.Remove(projectile);
                    projectile.Owner = null;
                }
                else
                {
                    projectile = subsystem.AddProjectile(message.Value, targetPosition,
                        targetVelocity, message.AngularVelocity, null);
                    createdProjectile = projectile != null;
                }
                if (projectile == null) return;
                m_remoteProjectiles[message.ProjectileId] = projectile;
            }
            projectile.Value = message.Value;
            if (createdProjectile)
            {
                projectile.Position = targetPosition;
                projectile.Velocity = targetVelocity;
                projectile.AngularVelocity = message.AngularVelocity;
            }
            else
            {
                float distanceSquared = Vector3.DistanceSquared(projectile.Position, targetPosition);
                projectile.Position = distanceSquared > 25f
                    ? targetPosition
                    : Vector3.Lerp(projectile.Position, targetPosition,
                        adoptedPrediction ? 0.2f : 0.35f);
                projectile.Velocity = Vector3.Lerp(projectile.Velocity, targetVelocity,
                    adoptedPrediction ? 0.25f : 0.5f);
                projectile.AngularVelocity = Vector3.Lerp(projectile.AngularVelocity,
                    message.AngularVelocity, 0.5f);
            }
            projectile.TrailOffset = message.TrailOffset;
            projectile.IsIncendiary = message.IsFireProjectile;
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        private void HandleProjectileHitResult(ProjectileSyncMessage message)
        {
            if (message.OwnerEntityId != client.ClientID || message.HitDamage <= 0f ||
                !IsFinite(message.Position) || !IsFinite(message.Velocity))
                return;
            long hitKey = ((long)message.ProjectileId << 32) | (uint)message.ServerStep;
            if (!m_displayedProjectileHits.Add(hitKey)) return;
            if (m_displayedProjectileHits.Count > 512) m_displayedProjectileHits.Clear();

            Vector3 direction = message.Velocity.LengthSquared() > 0.0001f
                ? Vector3.Normalize(message.Velocity)
                : Vector3.UnitY;
            string text = (0f - message.HitDamage).ToString("0", CultureInfo.InvariantCulture);
            var particleSystem = new HitValueParticleSystem(
                message.Position + 0.75f * direction,
                direction + message.AngularVelocity, Color.White, text);
            GameManager.Project?.FindSubsystem<SubsystemParticles>(false)?
                .AddParticleSystem(particleSystem);
        }

        // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.Projectiles
        private Projectile FindClientPredictedProjectile(SubsystemProjectiles subsystem,
            ProjectileSyncMessage message, Vector3 targetPosition, Vector3 targetVelocity)
        {
            if (message.OwnerEntityId != client.ClientID) return null;
            var remoteSet = new HashSet<Projectile>(m_remoteProjectiles.Values.Where(
                item => item != null));
            return subsystem.Projectiles
                .Where(candidate => candidate != null && !candidate.ToRemove &&
                    !remoteSet.Contains(candidate) && candidate.Value == message.Value &&
                    IsLocallyOwnedProjectile(candidate) &&
                    Vector3.DistanceSquared(candidate.Position, targetPosition) <= 144f &&
                    AreProjectileDirectionsCompatible(candidate.Velocity, targetVelocity))
                .OrderBy(candidate =>
                    Vector3.DistanceSquared(candidate.Position, targetPosition) +
                    0.02f * Vector3.DistanceSquared(candidate.Velocity, targetVelocity))
                .FirstOrDefault();
        }

        private bool IsLocallyOwnedProjectile(Projectile projectile)
        {
            ComponentPlayer owner = projectile?.Owner?.Entity?.FindComponent<ComponentPlayer>();
            return owner != null && !m_networkPlayerData.Values.Contains(owner.PlayerData);
        }

        private static bool AreProjectileDirectionsCompatible(Vector3 first, Vector3 second)
        {
            float firstLengthSquared = first.LengthSquared();
            float secondLengthSquared = second.LengthSquared();
            return firstLengthSquared < 1f || secondLengthSquared < 1f ||
                Vector3.Dot(first / MathUtils.Sqrt(firstLengthSquared),
                    second / MathUtils.Sqrt(secondLengthSquared)) >= 0.5f;
        }

        public void BroadcastExplosion(int x, int y, int z, float pressure,
            bool incendiary, bool noSound)
        {
            if (!IsHost || client?.IsConnected != true) return;
            var message = new ExplosionSyncMessage(
                new Vector3(x, y, z), pressure, 0, incendiary, noSound);
            // Source: ScMultiplayer.cs:HandleExplosionSyncMessage
            NetworkMessageSender.SendScheduledMessage(-1, message);
        }

        private void HandleExplosionSyncMessage(ExplosionSyncMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null) return;
            Point3 point = new Point3(message.Position);
            GameManager.Project?.FindSubsystem<SubsystemExplosions>(false)?.AddExplosion(
                point.X, point.Y, point.Z, message.Radius,
                message.IsIncendiary, message.NoExplosionSound);
        }

        private void SynchronizeContainers()
        {
            Project project = GameManager.Project;
            if (project == null) return;
            foreach (Entity entity in project.Entities)
            {
                ComponentBlockEntity blockEntity = entity?.FindComponent<ComponentBlockEntity>();
                if (blockEntity == null) continue;
                foreach (ComponentInventoryBase inventory in entity.FindComponents<ComponentInventoryBase>())
                {
                    string key = GetContainerKey(blockEntity.Coordinates, inventory.GetType().FullName);
                    int[] values = CaptureInventoryValues(inventory);
                    int[] counts = CaptureInventoryCounts(inventory);
                    if (IsHost)
                    {
                        if (m_containerStates.TryGetValue(key, out ContainerNetworkState state) &&
                            ArraysEqual(values, state.Values) && ArraysEqual(counts, state.Counts))
                            continue;
                        state = state ?? new ContainerNetworkState();
                        state.Revision++;
                        state.Values = values;
                        state.Counts = counts;
                        m_containerStates[key] = state;
                        SendContainerMessage(blockEntity.Coordinates, inventory.GetType().FullName,
                            state, false);
                    }
                    else
                    {
                        if (inventory is IUpdateable updateable &&
                            m_disabledClientContainerUpdates.Add(updateable))
                            QueueEndOfFrameAction(() =>
                                project.FindSubsystem<SubsystemUpdate>(true).RemoveUpdateable(updateable));
                        if (m_containerStates.TryGetValue(key, out ContainerNetworkState state) &&
                            (!ArraysEqual(values, state.Values) || !ArraysEqual(counts, state.Counts)))
                            SendContainerMessage(blockEntity.Coordinates, inventory.GetType().FullName,
                                new ContainerNetworkState { Revision = state.Revision, Values = values, Counts = counts }, true);
                    }
                }
            }
        }

        private void HandleContainerSyncMessage(ContainerSyncMessage message, int sourceClientId)
        {
            if (message == null || GameManager.Project == null) return;
            ComponentInventoryBase inventory = FindContainer(message.Coordinates, message.ComponentType);
            if (inventory == null) return;
            string key = GetContainerKey(message.Coordinates, message.ComponentType);
            if (IsHost)
            {
                if (!message.IsRequest || sourceClientId <= 0 ||
                    !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData player) ||
                    player?.ComponentPlayer?.ComponentBody == null ||
                    Vector3.DistanceSquared(player.ComponentPlayer.ComponentBody.Position,
                        new Vector3(message.Coordinates) + new Vector3(0.5f)) > 8f * 8f)
                    return;
                if (!m_containerStates.TryGetValue(key, out ContainerNetworkState state) ||
                    message.Revision != state.Revision)
                {
                    if (state != null) SendContainerMessage(message.Coordinates, message.ComponentType, state, false);
                    return;
                }
                ApplyInventory(inventory, message.SlotValues, message.SlotCounts);
                return;
            }
            if (sourceClientId != 0 || message.IsRequest) return;
            if (m_containerStates.TryGetValue(key, out ContainerNetworkState oldState) &&
                (!InventoryMatches(inventory, oldState.Values, oldState.Counts)) &&
                !InventoryMatches(inventory, message.SlotValues, message.SlotCounts))
                return;
            ApplyInventory(inventory, message.SlotValues, message.SlotCounts);
            m_containerStates[key] = new ContainerNetworkState
            {
                Revision = message.Revision,
                Values = (int[])message.SlotValues.Clone(),
                Counts = (int[])message.SlotCounts.Clone()
            };
        }

        private static string GetContainerKey(Point3 point, string type) =>
            point.X + "," + point.Y + "," + point.Z + ":" + type;

        private static ComponentInventoryBase FindContainer(Point3 point, string type)
        {
            foreach (Entity entity in GameManager.Project.Entities)
            {
                ComponentBlockEntity blockEntity = entity?.FindComponent<ComponentBlockEntity>();
                if (blockEntity == null || blockEntity.Coordinates != point) continue;
                return entity.FindComponents<ComponentInventoryBase>()
                    .FirstOrDefault(item => item.GetType().FullName == type);
            }
            return null;
        }

        private static int[] CaptureInventoryValues(IInventory inventory) =>
            Enumerable.Range(0, inventory.SlotsCount).Select(inventory.GetSlotValue).ToArray();

        private static int[] CaptureInventoryCounts(IInventory inventory) =>
            Enumerable.Range(0, inventory.SlotsCount).Select(inventory.GetSlotCount).ToArray();

        private static bool ArraysEqual(int[] a, int[] b) =>
            a != null && b != null && a.SequenceEqual(b);

        private static void SendContainerMessage(Point3 point, string type,
            ContainerNetworkState state, bool isRequest)
        {
            var message = new ContainerSyncMessage
            {
                Coordinates = point,
                ComponentType = type,
                Revision = state.Revision,
                IsRequest = isRequest,
                SlotValues = state.Values,
                SlotCounts = state.Counts
            };
            // Source: ScMultiplayer.cs:HandleContainerSyncMessage
            NetworkMessageSender.SendScheduledMessage(isRequest ? 0 : -1, message);
        }

        private void MaintainClientWorldObjects()
        {
            Project project = GameManager.Project;
            if (project == null || IsHost) return;
            if (!ReferenceEquals(m_clientWorldObjectsProject, project))
            {
                m_clientWorldObjectsProject = project;
                m_remoteAnimals.Clear();
                m_remoteAnimalSync.Clear();
                m_lastFullAnimalSnapshotTick = 0;
                m_remotePickables.Clear();
                m_remotePickableStates.Clear();
                m_pendingPickablePickups.Clear();
            }

            var remoteAnimalSet = new HashSet<Entity>(m_remoteAnimals.Values.Where(entity => entity != null));
            foreach (Entity entity in project.Entities.Where(entity =>
                entity?.FindComponent<ComponentCreature>() != null &&
                entity.FindComponent<ComponentPlayer>() == null &&
                !remoteAnimalSet.Contains(entity)).ToArray())
            {
                if (entity?.IsAddedToProject == true) project.RemoveEntity(entity, true);
            }

            SubsystemPickables subsystem = project.FindSubsystem<SubsystemPickables>(false);
            if (subsystem == null) return;
            var remotePickableSet = new HashSet<Pickable>(m_remotePickables.Values.Where(pickable => pickable != null));
            foreach (Pickable pickable in subsystem.Pickables)
            {
                if (pickable != null && !remotePickableSet.Contains(pickable)) pickable.ToRemove = true;
            }
        }

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.Update
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        private void CreateNetworkPlayer(int clientId, string requestedName, string playerIdentity = null)
        {
            if (!IsHost && clientId != 0 && m_departedRemoteClientIds.Contains(clientId))
                return;
            if (GameManager.Project == null)
            {
                m_pendingNetworkPlayers[clientId] = requestedName;
                m_pendingNetworkPlayerIdentities[clientId] = playerIdentity ?? string.Empty;
                return;
            }

            lock (m_creatingNetworkPlayers)
            {
                if (m_networkPlayerData.ContainsKey(clientId) || !m_creatingNetworkPlayers.Add(clientId)) return;
            }

            Project project = GameManager.Project;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(true);
            PlayerData playerData = null;
            Entity entity = null;
            try
            {
                PlayerData hostPlayer = players.PlayersData.FirstOrDefault();
                string playerName = string.IsNullOrWhiteSpace(requestedName) ? "NetPlayer" + clientId : requestedName.Trim();
                if (playerName.Length > 14) playerName = playerName.Substring(0, 14);
                string recordKey = string.IsNullOrWhiteSpace(playerIdentity) ? playerName : playerIdentity;
                m_playerRecords.TryGetValue(recordKey, out NetworkPlayerRecord record);
                playerData = new PlayerData(project)
                {
                    Name = record?.Name ?? playerName,
                    PlayerClass = record?.PlayerClass ?? PlayerClass.Male,
                    Level = record?.Level ?? 1f,
                    InputDevice = WidgetInputDevice.None,
                    // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PrepareSpawn
                    // SpawnPosition is the death-respawn anchor. Saved record.Position is applied
                    // to the first entity separately and must not turn into an "original spot" respawn.
                    SpawnPosition = players.GlobalSpawnPosition != Vector3.Zero
                        ? players.GlobalSpawnPosition
                        : hostPlayer?.ComponentPlayer?.ComponentBody.Position ??
                            record?.Position ?? Vector3.Zero
                };
                if (!string.IsNullOrEmpty(record?.SkinName)) playerData.CharacterSkinName = record.SkinName;

                // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.AddPlayerData
                // PlayerIndex 0 belongs to the locally controlled player. Include detached network
                // avatars when selecting an index because they are intentionally absent from PlayersData.
                int freePlayerIndex = Enumerable.Range(1, Math.Max(0, SubsystemPlayers.MaxPlayers - 1))
                    .FirstOrDefault(index =>
                        !players.PlayersData.Any(player => player.PlayerIndex == index) &&
                        !m_networkPlayerData.Values.Any(player => player.PlayerIndex == index));
                if (freePlayerIndex == 0)
                    throw new InvalidOperationException("No free remote player index.");

                ModManager.ModParentField.ModifyParentField(
                    players, "m_nextPlayerIndex", freePlayerIndex, typeof(SubsystemPlayers));
                players.AddPlayerData(playerData);

                var overrides = new ValuesDictionary
                {
                    { "Player", new ValuesDictionary { { "PlayerIndex", playerData.PlayerIndex } } },
                    { "Intro", new ValuesDictionary { { "PlayIntro", false } } }
                };
                if (record != null && !record.HasReceivedInitialItems)
                {
                    InvokeInitialPlayerSpawn(playerData, record.Position);
                    entity = playerData.ComponentPlayer?.Entity ??
                        throw new InvalidOperationException("Initial network player spawn failed.");
                    record.HasReceivedInitialItems = true;
                }
                else
                {
                    entity = DatabaseManager.CreateEntity(
                        project, playerData.GetEntityTemplateName(), overrides, true);
                    ComponentBody body = entity.FindComponent<ComponentBody>(true);
                    body.Position = record != null
                        ? record.Position
                        : playerData.SpawnPosition + new Vector3(1f, 0f, 0f);
                    project.AddEntity(entity);
                }

                // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.RemoveUpdateable
                // Remote avatars are driven by network state. Local player/input/locomotion/miner
                // updates otherwise consume the local controls and clear animation orders.
                SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(true);
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    if (!IsHost && (updateable is ComponentPlayer || updateable is ComponentInput ||
                        updateable is ComponentLocomotion || updateable is ComponentMiner)
                    )
                        subsystemUpdate.RemoveUpdateable(updateable);
                }

                // Source: GameEntitySystem/Project.cs:Project.SaveEntities
                // Subsystems already received EntityAdded. Remove only from the persistence set;
                // runtime subsystem references remain active until RemoveNetworkPlayer fires removal events.
                Dictionary<Entity, bool> projectEntities = ModManager.ModParentField.GetParentField<Dictionary<Entity, bool>>(
                    project, "m_entities", typeof(Project));
                projectEntities.Remove(entity);

                IInventory inventory = playerData.ComponentPlayer?.ComponentMiner?.Inventory;
                ConfigureNetworkPlayerInventory(inventory);
                RestorePlayerRecordInventory(inventory, record);
                ApplyClothes(playerData.ComponentPlayer, record?.Clothes);
                if (record != null)
                {
                    ApplyAuthoritativePlayerStats(playerData.ComponentPlayer, record.Health,
                        record.Air, record.Food, record.Stamina, record.Sleep,
                        record.Temperature, record.Wetness, record.Level);
                    ApplyPlayerRecordState(playerData.ComponentPlayer, record);
                }

                // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerData
                StateMachine stateMachine = ModManager.ModParentField.GetParentField<StateMachine>(
                    playerData, "m_stateMachine", typeof(PlayerData));
                stateMachine.TransitionTo("Playing");

                // Source: Survivalcraft/Game/SubsystemGameWidgets.cs:SubsystemGameWidgets.RemoveGameWidget
                // A remote GameWidget would become a local audio listener. Terrain coverage is
                // registered separately by MaintainRemoteTerrainLocations, so remove the view.
                SubsystemGameWidgets gameWidgets = project.FindSubsystem<SubsystemGameWidgets>(true);
                GameWidget networkGameWidget = playerData.GameWidget;
                MethodInfo removeGameWidget = ModManager.ModParentMethod.GetInstanceMethodInfo(
                    typeof(SubsystemGameWidgets), "RemoveGameWidget", new[] { typeof(GameWidget) });
                removeGameWidget.Invoke(gameWidgets, new object[] { networkGameWidget });

                // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.Save
                // Keep network avatars in the runtime component list but out of PlayersData so
                // autosave and map exit never persist them as local split-screen players.
                List<PlayerData> playerList = ModManager.ModParentField.GetParentField<List<PlayerData>>(
                    players, "m_playersData", typeof(SubsystemPlayers));
                playerList.Remove(playerData);
                List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(
                    players, "m_componentPlayers", typeof(SubsystemPlayers));
                if (playerData.ComponentPlayer != null && !componentPlayers.Contains(playerData.ComponentPlayer))
                    componentPlayers.Add(playerData.ComponentPlayer);

                m_networkPlayerData.Add(clientId, playerData);
                m_clientRecordKeys[clientId] = recordKey;
                m_pendingNetworkPlayers.Remove(clientId);
                m_pendingNetworkPlayerIdentities.Remove(clientId);
                if (clientId == 0) m_shouldCreateHostAvatar = false;
                Log.Information($"[ScMP] Created transient network player for ClientID {clientId}, PlayerIndex={playerData.PlayerIndex}");
            }
            catch (Exception ex)
            {
                List<PlayerData> playerList = ModManager.ModParentField.GetParentField<List<PlayerData>>(
                    players, "m_playersData", typeof(SubsystemPlayers));
                if (playerData != null) playerList.Remove(playerData);
                if (entity?.IsAddedToProject == true) project.RemoveEntity(entity, true);
                RemoveNetworkSimulationView(playerData);
                playerData?.Dispose();
                m_pendingNetworkPlayers[clientId] = requestedName;
                m_pendingNetworkPlayerIdentities[clientId] = playerIdentity ?? string.Empty;
                Log.Error($"[ScMP] Failed to create network player for ClientID {clientId}: {ex.Message}");
            }
            finally
            {
                lock (m_creatingNetworkPlayers) m_creatingNetworkPlayers.Remove(clientId);
            }
        }

        private void RemoveNetworkPlayer(int clientId)
        {
            // Source: ScMultiplayer.cs:RenderRemotePlayers
            // The presentation cache is independent from m_networkPlayerData. Remove it here so
            // a transport leave cannot leave a departed avatar visible on other clients.
            RemotePlayers.Remove(clientId);
            m_outgoingWorldTransfers.Remove(clientId);
            m_worldTransfersAwaitingReady.Remove(clientId);
            m_joinCatchUpJournals.Remove(clientId);
            m_hostTerrainRecoveryTargets.Remove(clientId);
            m_pendingAcceptedJoinKeys.Remove(clientId);
            m_hostPlayerPokingPhases.Remove(clientId);
            m_hostPlayerPokeSequences.Remove(clientId);
            m_hostKnockbackHealthCache.Remove(clientId);
            m_hostPainSoundTimes.Remove(clientId);
            m_hostRemoteKnockbackUntil.Remove(clientId);
            if (!m_networkPlayerData.TryGetValue(clientId, out PlayerData playerData)) return;
            if (playerData?.PlayerIndex > 0)
                GameManager.Project?.FindSubsystem<SubsystemTerrain>(false)?.TerrainUpdater
                    .RemoveUpdateLocation(playerData.PlayerIndex);
            string recordKey = m_clientRecordKeys.TryGetValue(clientId, out string key) ? key : playerData.Name;
            NetworkPlayerRecord record = CapturePlayerRecord(playerData);
            m_playerRecords[recordKey] = record;
            if (IsHost)
            {
                m_playerRecordsDirty = true;
                SavePlayerRecords();
            }
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players != null)
            {
                // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.RemovePlayerData
                // PlayerData is outside PlayersData. This is a no-op for the normally detached
                // network view and cleans it if creation failed before detachment completed.
                RemoveNetworkSimulationView(playerData);
                List<PlayerData> playerList = ModManager.ModParentField.GetParentField<List<PlayerData>>(
                    players, "m_playersData", typeof(SubsystemPlayers));
                playerList.Remove(playerData);
                List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(
                    players, "m_componentPlayers", typeof(SubsystemPlayers));
                if (playerData.ComponentPlayer != null)
                {
                    componentPlayers.Remove(playerData.ComponentPlayer);
                    GameManager.Project.RemoveEntity(playerData.ComponentPlayer.Entity, true);
                }
                playerData.Dispose();
            }
            m_networkPlayerData.Remove(clientId);
            m_networkPlayerInputs.Remove(clientId);
            m_pendingNetworkPlayers.Remove(clientId);
            m_pendingNetworkPlayerIdentities.Remove(clientId);
            m_clientRecordKeys.Remove(clientId);
        }

        // Source: Survivalcraft/Game/SubsystemGameWidgets.cs:SubsystemGameWidgets.RemoveGameWidget
        private static void RemoveNetworkSimulationView(PlayerData playerData)
        {
            SubsystemGameWidgets gameWidgets = playerData?.SubsystemGameWidgets;
            GameWidget gameWidget = gameWidgets?.GameWidgets.FirstOrDefault(
                item => ReferenceEquals(item.PlayerData, playerData));
            if (gameWidget == null || gameWidgets == null ||
                !gameWidgets.GameWidgets.Contains(gameWidget))
                return;
            MethodInfo removeGameWidget = ModManager.ModParentMethod.GetInstanceMethodInfo(
                typeof(SubsystemGameWidgets), "RemoveGameWidget", new[] { typeof(GameWidget) });
            removeGameWidget.Invoke(gameWidgets, new object[] { gameWidget });
        }

        // Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.SetUpdateLocation
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerIndex
        // Split-screen registers one terrain/content center per player. Network avatars do not
        // own a visible GameWidget, so register the same center explicitly from their authority
        // position while retaining the remote presentation-only entity.
        private void MaintainRemoteTerrainLocations(Project project)
        {
            if (!IsHost || project == null) return;
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.TerrainUpdater == null) return;
            float visibility = MathUtils.Min(
                project.FindSubsystem<SubsystemSky>(false)?.VisibilityRange ?? 64f, 64f);
            foreach (PlayerData playerData in m_networkPlayerData.Values.ToArray())
            {
                if (playerData?.ComponentPlayer?.ComponentBody == null || playerData.PlayerIndex <= 0)
                    continue;
                Vector3 position = playerData.ComponentPlayer.ComponentBody.Position;
                terrain.TerrainUpdater.SetUpdateLocation(playerData.PlayerIndex,
                    position.XZ, visibility, 64f);
            }
        }

        // Source: Survivalcraft/Game/SubsystemGameWidgets.cs:SubsystemGameWidgets.m_gameWidgets
        // Source: Survivalcraft/Game/SubsystemSpawn.cs:SubsystemSpawn.Update
        // Source: Survivalcraft/Game/SubsystemCreatureSpawn.cs:SubsystemCreatureSpawn.Update
        // Remote widgets stay detached from rendering and audio. The two SuSubsystem classes use
        // this scope only while native split-screen spawn/despawn logic is executing.
        internal IDisposable BeginRemoteSimulationViewScope(Project project)
        {
            if (!IsHost || project == null || m_networkPlayerData.Count == 0) return null;
            SubsystemGameWidgets subsystemViews = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (subsystemViews == null) return null;
            List<GameWidget> gameWidgets = ModManager.ModParentField.GetParentField<List<GameWidget>>(
                subsystemViews, "m_gameWidgets", typeof(SubsystemGameWidgets));
            if (gameWidgets == null) return null;

            GameWidget[] originalViews = gameWidgets.ToArray();
            bool changed = false;
            foreach (PlayerData playerData in m_networkPlayerData.Values.ToArray())
            {
                object cachedView = playerData == null
                    ? null
                    : ModManager.ModParentField.GetParentField(
                        playerData, "m_gameWidget", typeof(PlayerData));
                if (!(cachedView is GameWidget gameWidget) || gameWidgets.Contains(gameWidget))
                    continue;
                gameWidget.ActiveCamera.Update(0f);
                gameWidgets.Add(gameWidget);
                changed = true;
            }
            return changed ? new GameWidgetListScope(gameWidgets, originalViews) : null;
        }

        // Source: Survivalcraft/Game/SubsystemSpawn.cs:SubsystemSpawn.SpawnChunks
        // Source: Survivalcraft/Game/SubsystemCreatureSpawn.cs:SubsystemCreatureSpawn.Load
        // Native SpawnChunks activates every chunk in a 48-block radius in one update. For a
        // distant network player, activate one ready chunk per second and let the original
        // SpawningChunk callback apply biome suitability and population limits.
        internal void MaintainRemoteCreatureSpawning(
            Project project, SubsystemSpawn subsystemSpawn)
        {
            if (!IsHost || project == null || subsystemSpawn == null ||
                m_networkPlayerData.Count == 0 ||
                Time.RealTime < m_nextRemoteCreatureSpawnTime)
                return;
            m_nextRemoteCreatureSpawnTime = Time.RealTime + RemoteCreatureSpawnInterval;

            Vector3[] remotePositions = m_networkPlayerData.Values
                .Select(playerData => playerData?.ComponentPlayer?.ComponentBody)
                .Where(body => body != null)
                .Select(body => body.Position)
                .Where(position => CountRemoteCreatures(
                    project, position.XZ, RemoteCreaturePopulationRadius) <
                    RemoteCreatureTargetCount)
                .ToArray();
            if (remotePositions.Length == 0) return;

            SubsystemGameWidgets subsystemViews =
                project.FindSubsystem<SubsystemGameWidgets>(false);
            SubsystemTerrain subsystemTerrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (subsystemViews == null || subsystemTerrain == null) return;
            Vector2[] visiblePositions = subsystemViews.GameWidgets
                .Select(gameWidget => gameWidget.ActiveCamera.ViewPosition.XZ)
                .ToArray();

            var candidatePoints = new HashSet<Point2>();
            foreach (Vector3 remotePosition in remotePositions)
            {
                Vector2 center = remotePosition.XZ;
                Point2 min = Terrain.ToChunk(center - new Vector2(48f));
                Point2 max = Terrain.ToChunk(center + new Vector2(48f));
                for (int x = min.X; x <= max.X; x++)
                {
                    for (int z = min.Y; z <= max.Y; z++)
                    {
                        Vector2 chunkCenter = new Vector2(
                            (x + 0.5f) * 16f, (z + 0.5f) * 16f);
                        if (Vector2.DistanceSquared(center, chunkCenter) >= 48f * 48f ||
                            visiblePositions.Any(position => Vector2.DistanceSquared(
                                position, chunkCenter) < 48f * 48f))
                            continue;
                        TerrainChunk terrainChunk = subsystemTerrain.Terrain.GetChunkAtCell(
                            Terrain.ToCell(chunkCenter.X), Terrain.ToCell(chunkCenter.Y));
                        if (terrainChunk == null ||
                            terrainChunk.State <= TerrainChunkState.InvalidPropagatedLight)
                            continue;
                        candidatePoints.Add(new Point2(x, z));
                    }
                }
            }
            Point2[] candidates = candidatePoints
                .OrderBy(point => point.X)
                .ThenBy(point => point.Y)
                .ToArray();
            if (candidates.Length == 0) return;

            Point2 selectedPoint = candidates[m_remoteCreatureSpawnCursor % candidates.Length];
            m_remoteCreatureSpawnCursor = (m_remoteCreatureSpawnCursor + 1) % candidates.Length;
            Vector2 selectedChunkCenter = new Vector2(
                (selectedPoint.X + 0.5f) * 16f,
                (selectedPoint.Y + 0.5f) * 16f);
            Vector2 selectedRemoteCenter = remotePositions
                .Select(position => position.XZ)
                .OrderBy(position => Vector2.DistanceSquared(
                    position, selectedChunkCenter))
                .First();
            MethodInfo getOrCreateSpawnChunk = ModManager.ModParentMethod.GetInstanceMethodInfo(
                typeof(SubsystemSpawn), "GetOrCreateSpawnChunk", new[] { typeof(Point2) });
            SpawnChunk spawnChunk = getOrCreateSpawnChunk?.Invoke(
                subsystemSpawn, new object[] { selectedPoint }) as SpawnChunk;
            if (spawnChunk == null) return;

            int localCreatureCount = CountRemoteCreatures(
                project, selectedRemoteCenter, RemoteCreaturePopulationRadius);
            int availableCreatureSlots = Math.Max(
                0, RemoteCreatureTargetCount - localCreatureCount);
            MethodInfo spawnEntity = ModManager.ModParentMethod.GetInstanceMethodInfo(
                typeof(SubsystemSpawn), "SpawnEntity", new[] { typeof(SpawnEntityData) });
            int recordsToRestore = Math.Min(
                Math.Min(RemoteSpawnRecordsPerInterval, availableCreatureSlots),
                spawnChunk.SpawnsData.Count);
            for (int i = 0; i < recordsToRestore; i++)
            {
                SpawnEntityData record = spawnChunk.SpawnsData[0];
                spawnChunk.SpawnsData.RemoveAt(0);
                spawnEntity?.Invoke(subsystemSpawn, new object[] { record });
            }
            localCreatureCount = CountRemoteCreatures(
                project, selectedRemoteCenter, RemoteCreaturePopulationRadius);
            if (spawnChunk.SpawnsData.Count == 0 &&
                localCreatureCount < RemoteCreatureTargetCount)
            {
                SubsystemCreatureSpawn creatureSpawn =
                    project.FindSubsystem<SubsystemCreatureSpawn>(false);
                MethodInfo spawnChunkCreatures = ModManager.ModParentMethod.GetInstanceMethodInfo(
                    typeof(SubsystemCreatureSpawn), "SpawnChunkCreatures",
                    new[] { typeof(SpawnChunk), typeof(int), typeof(bool) });
                if (creatureSpawn != null && spawnChunkCreatures != null)
                {
                    var existingCreatures = new HashSet<Entity>(FindRemoteCreatures(
                        project, selectedRemoteCenter,
                        RemoteCreaturePopulationRadius));
                    // CountCreatures uses SubsystemBodies globally. Restrict that read-only view
                    // during this call so each distant player receives the same local population
                    // target as the host, without changing the original spawn implementation.
                    using (BeginRemoteCreatureCountScope(
                        project, selectedRemoteCenter,
                        RemoteCreaturePopulationRadius))
                    {
                        int nonConstantAttempts = spawnChunk.IsSpawned ? 1 : 10;
                        spawnChunkCreatures.Invoke(creatureSpawn,
                            new object[] { spawnChunk, nonConstantAttempts, false });
                        if (CountRemoteCreatures(project, selectedRemoteCenter,
                            RemoteCreaturePopulationRadius) < RemoteCreatureTargetCount)
                        {
                            spawnChunkCreatures.Invoke(creatureSpawn,
                                new object[] { spawnChunk, 2, true });
                        }
                        TrimRemoteCreatureOverflow(project, selectedRemoteCenter,
                            RemoteCreaturePopulationRadius,
                            RemoteCreatureTargetCount, existingCreatures);
                    }
                }
                spawnChunk.IsSpawned = true;
            }
        }

        private static int CountRemoteCreatures(
            Project project, Vector2 center, float radius)
        {
            return FindRemoteCreatures(project, center, radius).Length;
        }

        private static Entity[] FindRemoteCreatures(
            Project project, Vector2 center, float radius)
        {
            SubsystemBodies subsystemBodies = project?.FindSubsystem<SubsystemBodies>(false);
            if (subsystemBodies == null) return Array.Empty<Entity>();
            float radiusSquared = radius * radius;
            return subsystemBodies.Bodies.Where(body =>
                body?.Entity.FindComponent<ComponentCreature>() != null &&
                body.Entity.FindComponent<ComponentPlayer>() == null &&
                Vector2.DistanceSquared(body.Position.XZ, center) <= radiusSquared)
                .Select(body => body.Entity)
                .Distinct()
                .ToArray();
        }

        private static void TrimRemoteCreatureOverflow(
            Project project,
            Vector2 center,
            float radius,
            int targetCount,
            HashSet<Entity> existingCreatures)
        {
            Entity[] creatures = FindRemoteCreatures(project, center, radius);
            int excess = creatures.Length - targetCount;
            if (excess <= 0) return;
            foreach (Entity entity in creatures
                .Where(entity => !existingCreatures.Contains(entity))
                .Take(excess)
                .ToArray())
            {
                if (entity?.IsAddedToProject == true)
                    project.RemoveEntity(entity, true);
            }
        }

        // Source: Survivalcraft/Game/SubsystemCreatureSpawn.cs:SubsystemCreatureSpawn.CountCreatures
        // Source: Survivalcraft/Game/SubsystemBodies.cs:SubsystemBodies.AddBody
        // Source: Survivalcraft/Game/SubsystemBodies.cs:SubsystemBodies.RemoveBody
        private IDisposable BeginRemoteCreatureCountScope(
            Project project, Vector2 center, float radius)
        {
            SubsystemBodies subsystemBodies = project?.FindSubsystem<SubsystemBodies>(false);
            if (subsystemBodies == null) return null;
            Dictionary<ComponentBody, Point2> areaByBody =
                ModManager.ModParentField.GetParentField<Dictionary<ComponentBody, Point2>>(
                    subsystemBodies, "m_areaByComponentBody", typeof(SubsystemBodies));
            if (areaByBody == null) return null;

            float radiusSquared = radius * radius;
            ComponentBody[] outsideBodies = areaByBody.Keys
                .Where(body => body?.Entity.FindComponent<ComponentCreature>() != null &&
                    Vector2.DistanceSquared(body.Position.XZ, center) > radiusSquared)
                .ToArray();
            if (outsideBodies.Length == 0) return null;
            MethodInfo removeBody = ModManager.ModParentMethod.GetInstanceMethodInfo(
                typeof(SubsystemBodies), "RemoveBody", new[] { typeof(ComponentBody) });
            MethodInfo addBody = ModManager.ModParentMethod.GetInstanceMethodInfo(
                typeof(SubsystemBodies), "AddBody", new[] { typeof(ComponentBody) });
            if (removeBody == null || addBody == null) return null;
            foreach (ComponentBody body in outsideBodies)
                removeBody.Invoke(subsystemBodies, new object[] { body });
            return new SubsystemBodiesScope(subsystemBodies, addBody, outsideBodies);
        }

        // Source: Survivalcraft/Game/SubsystemSpawn.cs:SubsystemSpawn.DespawnChunks
        // Source: Survivalcraft/Game/ComponentSpawn.cs:ComponentSpawn.AutoDespawn
        // DespawnChunks persists an entity before starting its two-second fade. Temporarily disable
        // auto-despawn for entities covered by a remote player so no duplicate SpawnsData is written.
        internal IDisposable BeginRemoteDespawnProtectionScope(Project project)
        {
            if (!IsHost || project == null || m_networkPlayerData.Count == 0) return null;
            Vector3[] remotePositions = m_networkPlayerData.Values
                .Select(playerData => playerData?.ComponentPlayer?.ComponentBody)
                .Where(body => body != null)
                .Select(body => body.Position)
                .ToArray();
            if (remotePositions.Length == 0) return null;

            SubsystemSpawn subsystemSpawn = project.FindSubsystem<SubsystemSpawn>(false);
            if (subsystemSpawn == null) return null;
            var protectedSpawns = new List<ComponentSpawn>();
            foreach (ComponentSpawn spawn in subsystemSpawn.Spawns.ToArray())
            {
                if (spawn?.AutoDespawn != true || spawn.ComponentFrame == null ||
                    !remotePositions.Any(position => Vector3.DistanceSquared(
                        position, spawn.ComponentFrame.Position) <= 60f * 60f))
                    continue;

                bool isDead = spawn.ComponentCreature?.ComponentHealth?.Health <= 0f;
                // Source: Survivalcraft/Game/ComponentShapeshifter.cs:ComponentShapeshifter.ShapeshiftTo
                // Shapeshifting deliberately uses ComponentSpawn.Despawn. Do not mistake that
                // transition for an out-of-range auto-despawn or the old form keeps emitting
                // particles forever without creating the replacement entity.
                ComponentShapeshifter shapeshifter =
                    spawn.Entity.FindComponent<ComponentShapeshifter>();
                string shapeshiftTarget = shapeshifter == null
                    ? null
                    : ModManager.ModParentField.GetParentField(
                        shapeshifter, "m_spawnEntityTemplateName",
                        typeof(ComponentShapeshifter)) as string;
                bool isShapeshifting = !string.IsNullOrEmpty(shapeshiftTarget);
                if (isShapeshifting && !spawn.IsDespawning)
                    spawn.Despawn();
                else if (spawn.IsDespawning && !isDead && !isShapeshifting)
                {
                    ModManager.ModParentField.ModifyParentField(
                        spawn, "<DespawnTime>k__BackingField", (double?)null,
                        typeof(ComponentSpawn));
                    RemovePersistedSpawnRecord(subsystemSpawn, spawn);
                }
                ModManager.ModParentField.ModifyParentField(
                    spawn, "<AutoDespawn>k__BackingField", false, typeof(ComponentSpawn));
                protectedSpawns.Add(spawn);
            }
            return protectedSpawns.Count > 0
                ? new AutoDespawnScope(protectedSpawns)
                : null;
        }

        private static void RemovePersistedSpawnRecord(
            SubsystemSpawn subsystemSpawn, ComponentSpawn spawn)
        {
            SpawnChunk chunk = subsystemSpawn.GetSpawnChunk(
                Terrain.ToChunk(spawn.ComponentFrame.Position.XZ));
            if (chunk == null || chunk.SpawnsData.Count == 0) return;
            string templateName = spawn.Entity.ValuesDictionary.DatabaseObject.Name;
            Vector3 position = spawn.ComponentFrame.Position;
            bool constantSpawn = spawn.ComponentCreature?.ConstantSpawn ?? false;
            chunk.SpawnsData.RemoveAll(record =>
                record.TemplateName == templateName &&
                record.ConstantSpawn == constantSpawn &&
                Vector3.DistanceSquared(record.Position, position) < 0.01f);
        }

        private sealed class GameWidgetListScope : IDisposable
        {
            private List<GameWidget> m_gameWidgets;
            private GameWidget[] m_originalViews;

            public GameWidgetListScope(List<GameWidget> gameWidgets, GameWidget[] originalViews)
            {
                m_gameWidgets = gameWidgets;
                m_originalViews = originalViews;
            }

            public void Dispose()
            {
                if (m_gameWidgets == null) return;
                m_gameWidgets.Clear();
                m_gameWidgets.AddRange(m_originalViews);
                m_gameWidgets = null;
                m_originalViews = null;
            }
        }

        private sealed class AutoDespawnScope : IDisposable
        {
            private List<ComponentSpawn> m_spawns;

            public AutoDespawnScope(List<ComponentSpawn> spawns)
            {
                m_spawns = spawns;
            }

            public void Dispose()
            {
                if (m_spawns == null) return;
                foreach (ComponentSpawn spawn in m_spawns)
                {
                    ModManager.ModParentField.ModifyParentField(
                        spawn, "<AutoDespawn>k__BackingField", true,
                        typeof(ComponentSpawn));
                }
                m_spawns = null;
            }
        }

        private sealed class SubsystemBodiesScope : IDisposable
        {
            private SubsystemBodies m_subsystemBodies;
            private MethodInfo m_addBody;
            private ComponentBody[] m_bodies;

            public SubsystemBodiesScope(
                SubsystemBodies subsystemBodies,
                MethodInfo addBody,
                ComponentBody[] bodies)
            {
                m_subsystemBodies = subsystemBodies;
                m_addBody = addBody;
                m_bodies = bodies;
            }

            public void Dispose()
            {
                if (m_subsystemBodies == null) return;
                foreach (ComponentBody body in m_bodies)
                    m_addBody.Invoke(m_subsystemBodies, new object[] { body });
                m_subsystemBodies = null;
                m_addBody = null;
                m_bodies = null;
            }
        }

        // Source: Survivalcraft/Game/SubsystemCreatureSpawn.cs:SubsystemCreatureSpawn.SpawnChunkCreatures
        // A previous remote-view implementation could persist the same non-constant creature on
        // every despawn cycle. Clean only clearly runaway auto-spawn populations, in small batches.
        private void QueueRunawayCreatureCleanup(Project project)
        {
            m_runawayCreatureCleanup.Clear();
            m_runawayCreatureCleanupProject = project;
            if (project == null) return;

            ComponentCreature[] creatures = project.Entities
                .Select(entity => entity?.FindComponent<ComponentCreature>())
                .Where(creature => creature != null &&
                    creature.Entity.FindComponent<ComponentPlayer>() == null &&
                    creature.Entity.FindComponent<ComponentSpawn>()?.AutoDespawn == true &&
                    !creature.ConstantSpawn)
                .ToArray();

            SubsystemSpawn subsystemSpawn = project.FindSubsystem<SubsystemSpawn>(false);
            Dictionary<Point2, SpawnChunk> spawnChunks = subsystemSpawn == null
                ? null
                : ModManager.ModParentField.GetParentField<Dictionary<Point2, SpawnChunk>>(
                    subsystemSpawn, "m_chunks", typeof(SubsystemSpawn));
            SpawnEntityData[] spawnRecords = spawnChunks?.Values
                .SelectMany(chunk => chunk.SpawnsData)
                .Where(record => record != null && !record.ConstantSpawn)
                .ToArray() ?? Array.Empty<SpawnEntityData>();
            if (creatures.Length + spawnRecords.Length <= RunawayCreatureThreshold) return;

            Vector3[] playerPositions = project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers
                .Where(player => player?.ComponentBody != null)
                .Select(player => player.ComponentBody.Position)
                .ToArray() ?? Array.Empty<Vector3>();
            IEnumerable<ComponentCreature> ordered = playerPositions.Length > 0
                ? creatures.OrderBy(creature => playerPositions.Min(position =>
                    Vector3.DistanceSquared(position, creature.ComponentBody.Position)))
                : creatures;
            int activeKeepCount = Math.Min(creatures.Length, RunawayCreatureKeepCount);
            foreach (ComponentCreature creature in ordered.Skip(activeKeepCount))
                m_runawayCreatureCleanup.Enqueue(creature.Entity);

            int recordKeepCount = Math.Max(0, RunawayCreatureKeepCount - activeKeepCount);
            IEnumerable<SpawnEntityData> orderedRecords = playerPositions.Length > 0
                ? spawnRecords.OrderBy(record => playerPositions.Min(position =>
                    Vector3.DistanceSquared(position, record.Position)))
                : spawnRecords;
            var keptRecords = new HashSet<SpawnEntityData>(orderedRecords.Take(recordKeepCount));
            int removedRecords = 0;
            if (spawnChunks != null)
            {
                foreach (SpawnChunk chunk in spawnChunks.Values)
                {
                    removedRecords += chunk.SpawnsData.RemoveAll(record =>
                        record != null && !record.ConstantSpawn && !keptRecords.Contains(record));
                }
            }
            Log.Warning($"[ScMP] Blocked runaway creature generation: " +
                $"Entities={m_runawayCreatureCleanup.Count}, SpawnRecords={removedRecords}.");
        }

        internal void SanitizeRunawayCreatureState(Project project)
        {
            if (project == null || m_runawayCreatureCleanup.Count > 0 ||
                Time.RealTime < m_nextRunawayCreatureCheckTime)
                return;
            m_nextRunawayCreatureCheckTime = Time.RealTime + 2.0;
            QueueRunawayCreatureCleanup(project);
        }

        private void ProcessRunawayCreatureCleanup(Project project)
        {
            if (!ReferenceEquals(project, m_runawayCreatureCleanupProject) ||
                m_runawayCreatureCleanup.Count == 0)
                return;
            int removed = 0;
            while (removed < RunawayCreatureCleanupBatchSize &&
                m_runawayCreatureCleanup.Count > 0)
            {
                Entity entity = m_runawayCreatureCleanup.Dequeue();
                if (entity?.IsAddedToProject == true)
                {
                    project.RemoveEntity(entity, true);
                    removed++;
                }
            }
            if (m_runawayCreatureCleanup.Count == 0)
                Log.Information("[ScMP] Runaway creature cleanup completed.");
        }

        // Source: Survivalcraft/Game/UserManager.cs:UserManager.UserManager
        private static string GetPlayerRecordKey(string identity, string fallbackName)
        {
            return !string.IsNullOrWhiteSpace(identity)
                ? identity.Trim()
                : "name:" + (fallbackName ?? string.Empty).Trim();
        }

        private static string GetNetworkRecordKey(int clientId) => "network:" + clientId;

        private static bool IsValidRequestedProfile(GameWorldInfoMessage message)
        {
            if (message == null || !message.HasPlayerProfile ||
                !PlayerData.VerifyName((message.PlayerName ?? string.Empty).Trim()) ||
                string.IsNullOrWhiteSpace(message.PlayerIdentity) ||
                string.IsNullOrWhiteSpace(message.CharacterSkinName))
                return false;
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
                Position = position,
                Level = 1f,
                Health = 1f,
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
                        PlayerClass = ParsePlayerClass((string)element.Attribute("Class")),
                        SkinName = (string)element.Attribute("Skin") ?? string.Empty,
                        Position = new Vector3(
                            ParseFloat((string)element.Attribute("X")),
                            ParseFloat((string)element.Attribute("Y")),
                            ParseFloat((string)element.Attribute("Z"))),
                        Level = ParseFloat((string)element.Attribute("Level"), 1f),
                        Health = ParseFloat((string)element.Attribute("Health"), 1f),
                        Air = ParseFloat((string)element.Attribute("Air"), 1f),
                        Food = ParseFloat((string)element.Attribute("Food"), 0.9f),
                        Stamina = ParseFloat((string)element.Attribute("Stamina"), 1f),
                        Sleep = ParseFloat((string)element.Attribute("Sleep"), 0.9f),
                        Temperature = ParseFloat((string)element.Attribute("Temperature"), 12f),
                        TargetTemperature = ParseFloat(
                            (string)element.Attribute("TargetTemperature"), 12f),
                        Wetness = ParseFloat((string)element.Attribute("Wetness")),
                        FluDuration = ParseFloat((string)element.Attribute("FluDuration")),
                        FluOnset = ParseFloat((string)element.Attribute("FluOnset")),
                        SicknessDuration = ParseFloat(
                            (string)element.Attribute("SicknessDuration")),
                        IsCreativeFlying = ParseBool(
                            (string)element.Attribute("CreativeFlying"), false),
                        HasReceivedInitialItems = ParseBool(
                            (string)element.Attribute("InitialItems"), true),
                        InventoryWasCreative = ParseBool(
                            (string)element.Attribute("CreativeInventory"), false),
                        ActiveSlotIndex = (int?)element.Attribute("ActiveSlot") ?? 0,
                        CreativeCategoryIndex =
                            (int?)element.Attribute("CreativeCategory") ?? 0,
                        CreativePageIndex = (int?)element.Attribute("CreativePage") ?? 0
                    };
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
                    if (!record.InventoryWasCreative &&
                        LooksLikeLegacyCreativeInventory(record))
                    {
                        record.InventoryWasCreative = true;
                        record.SlotValues = Array.Empty<int>();
                        record.SlotCounts = Array.Empty<int>();
                        m_playerRecordsDirty = true;
                    }
                    record.Clothes = CreateEmptyClothes();
                    foreach (XElement slot in element.Element("Clothes")?.Elements("Slot") ??
                        Enumerable.Empty<XElement>())
                    {
                        int index = (int?)slot.Attribute("Index") ?? -1;
                        if (index >= 0 && index < record.Clothes.Length)
                            record.Clothes[index] = ParseIntArray((string)slot.Attribute("Values"));
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
                var root = new XElement("ScMultiplayerPlayers", new XAttribute("Version", 3));
                foreach (KeyValuePair<string, NetworkPlayerRecord> item in m_playerRecords.OrderBy(pair => pair.Key))
                {
                    NetworkPlayerRecord record = item.Value;
                    if (record == null) continue;
                    var player = new XElement("Player",
                        new XAttribute("Identity", item.Key),
                        new XAttribute("Name", record.Name ?? "Player"),
                        new XAttribute("Class", record.PlayerClass),
                        new XAttribute("Skin", record.SkinName ?? string.Empty),
                        new XAttribute("X", FormatFloat(record.Position.X)),
                        new XAttribute("Y", FormatFloat(record.Position.Y)),
                        new XAttribute("Z", FormatFloat(record.Position.Z)),
                        new XAttribute("Level", FormatFloat(record.Level)),
                        new XAttribute("Health", FormatFloat(record.Health)),
                        new XAttribute("Air", FormatFloat(record.Air)),
                        new XAttribute("Food", FormatFloat(record.Food)),
                        new XAttribute("Stamina", FormatFloat(record.Stamina)),
                        new XAttribute("Sleep", FormatFloat(record.Sleep)),
                        new XAttribute("Temperature", FormatFloat(record.Temperature)),
                        new XAttribute("TargetTemperature", FormatFloat(record.TargetTemperature)),
                        new XAttribute("Wetness", FormatFloat(record.Wetness)),
                        new XAttribute("FluDuration", FormatFloat(record.FluDuration)),
                        new XAttribute("FluOnset", FormatFloat(record.FluOnset)),
                        new XAttribute("SicknessDuration", FormatFloat(record.SicknessDuration)),
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
                    var clothes = new XElement("Clothes");
                    int[][] clothesValues = record.Clothes ?? CreateEmptyClothes();
                    for (int i = 0; i < 4; i++)
                        clothes.Add(new XElement("Slot", new XAttribute("Index", i),
                            new XAttribute("Values", FormatIntArray(
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
            var record = new NetworkPlayerRecord
            {
                Name = playerData?.Name ?? "Player",
                PlayerClass = playerData?.PlayerClass ?? PlayerClass.Male,
                SkinName = playerData?.CharacterSkinName ?? string.Empty,
                Position = player?.ComponentBody.Position ?? playerData?.SpawnPosition ?? Vector3.Zero,
                Level = playerData?.Level ?? 1f,
                Health = player?.ComponentHealth?.Health ?? 1f,
                Air = player?.ComponentHealth?.Air ?? 1f,
                Food = vitalStats?.Food ?? 0.9f,
                Stamina = vitalStats?.Stamina ?? 1f,
                Sleep = vitalStats?.Sleep ?? 0.9f,
                Temperature = vitalStats?.Temperature ?? 12f,
                TargetTemperature = vitalStats != null
                    ? ModManager.ModParentField.GetParentField<float>(
                        vitalStats, "m_targetTemperature", typeof(ComponentVitalStats))
                    : 12f,
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
            return record;
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

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Load
        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Load
        // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.Load
        private static void ApplyPlayerRecordState(ComponentPlayer player,
            NetworkPlayerRecord record)
        {
            if (player == null || record == null) return;
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
        }

        private static int[][] CaptureClothes(ComponentPlayer player)
        {
            int[][] result = CreateEmptyClothes();
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

        private static int[][] CreateEmptyClothes() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };

        private static string FormatFloat(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static float ParseFloat(string value, float fallback = 0f) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                ? result : fallback;

        private static PlayerClass ParsePlayerClass(string value) =>
            Enum.TryParse(value, true, out PlayerClass result) ? result : PlayerClass.Male;

        private static bool ParseBool(string value, bool fallback) =>
            bool.TryParse(value, out bool result) ? result : fallback;

        private static string FormatIntArray(int[] values) =>
            values == null || values.Length == 0 ? string.Empty : string.Join(";", values);

        private static int[] ParseIntArray(string values)
        {
            if (string.IsNullOrWhiteSpace(values)) return Array.Empty<int>();
            return values.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int result) ? result : 0).ToArray();
        }

        private void RefreshHostPlayerRecords()
        {
            if (!IsHost) return;
            EnsurePlayerRecordsLoaded();
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
            {
                if (!m_clientRecordKeys.TryGetValue(item.Key, out string recordKey) ||
                    item.Value?.ComponentPlayer == null) continue;
                m_playerRecords[recordKey] = CapturePlayerRecord(item.Value);
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
                        NetworkMessageSender.SendPlayerProfileMessage(
                            item.Key, CapturePlayerRecord(item.Value));
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
                if (IsSkinValidForClass(message.SkinName, playerData.PlayerClass))
                    playerData.CharacterSkinName = message.SkinName;
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

            string networkKey = GetNetworkRecordKey(message.ClientId);
            NetworkPlayerRecord record = m_playerRecords.TryGetValue(networkKey, out NetworkPlayerRecord existing)
                ? existing : new NetworkPlayerRecord();
            record.Name = message.Name;
            record.PlayerClass = message.PlayerClass;
            record.SkinName = message.SkinName;
            record.Clothes = message.Clothes;

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

        private void ApplyProfileToLocalPlayer(PlayerProfileMessage message)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer == null || localPlayer.PlayerData.PlayerClass != message.PlayerClass) return;
            if (PlayerData.VerifyName((message.Name ?? string.Empty).Trim()))
                localPlayer.PlayerData.Name = message.Name.Trim();
            if (IsSkinValidForClass(message.SkinName, message.PlayerClass))
                localPlayer.PlayerData.CharacterSkinName = message.SkinName;
            if (!m_equipmentSynchronizedClients.Contains(message.ClientId))
                ApplyClothes(localPlayer, message.Clothes);
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
            ApplyClothes(player, record.Clothes);
            ApplyAuthoritativePlayerStats(player, record.Health, record.Air, record.Food,
                record.Stamina, record.Sleep, record.Temperature, record.Wetness, record.Level);
            ApplyPlayerRecordState(player, record);
            m_localPlayerRecordApplied = true;
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
                    // Login position is applied after spawning; retain the world birth point for death.
                    SpawnPosition = players.GlobalSpawnPosition != Vector3.Zero
                        ? players.GlobalSpawnPosition
                        : record.Position
                };
                ModManager.ModParentField.ModifyParentField(
                    players, "m_nextPlayerIndex", playerIndex, typeof(SubsystemPlayers));
                players.AddPlayerData(replacement);
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
            for (int i = 0; i < slotsCount; i++)
            {
                int value = values[i];
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

        public void HandleGamePlayerHealthMessage(GamePlayerHealthMessage msg, int clientID)
        {
            if (msg == null) return;
            if (IsHost)
            {
                if (clientID <= 0 || msg.PlayerIndex != clientID ||
                    !m_networkPlayerData.TryGetValue(clientID, out PlayerData requestedPlayer) ||
                    requestedPlayer?.ComponentPlayer?.ComponentHealth == null)
                    return;
                ComponentHealth requestedHealth = requestedPlayer.ComponentPlayer.ComponentHealth;
                float requestedValue = MathUtils.Saturate(msg.Health);
                if (msg.HealthChange < -0.0001f && requestedValue < requestedHealth.Health)
                {
                    float requestedPreviousHealth = requestedHealth.Health;
                    requestedHealth.Injure(requestedHealth.Health - requestedValue, null,
                        ignoreInvulnerability: true, "Client damage request");
                    if (requestedHealth.Health < requestedPreviousHealth - 0.0001f)
                        PlayAuthoritativePainSound(clientID, requestedPlayer.ComponentPlayer);
                }
                ComponentSleep requestedSleep = requestedPlayer.ComponentPlayer.ComponentSleep;
                if (requestedSleep != null && requestedSleep.IsSleeping != msg.IsSleeping)
                {
                    if (!msg.IsSleeping) requestedSleep.WakeUp();
                    else if (requestedSleep.CanSleep(out _)) requestedSleep.Sleep(true);
                }
                return;
            }
            if (clientID != 0) return;
            // msg.PlayerIndex = 发送方 ClientID, 写入 RemotePlayers
            int remoteClientId = msg.PlayerIndex;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer targetPlayer = remoteClientId == client.ClientID
                ? players?.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData))
                : (m_networkPlayerData.TryGetValue(remoteClientId, out PlayerData remoteData)
                    ? remoteData.ComponentPlayer
                    : null);
            float previousHealth = targetPlayer?.ComponentHealth?.Health ?? msg.Health;
            if (remoteClientId == client.ClientID &&
                Time.RealTime < m_localRespawnPendingUntil && msg.Health <= 0f)
                return;
            if (remoteClientId == client.ClientID && msg.Health > 0f)
                m_localRespawnPendingUntil = 0.0;
            ApplyAuthoritativePlayerStats(targetPlayer, msg.Health, msg.Air, msg.Food,
                msg.Stamina, msg.Sleep, msg.Temperature, msg.Wetness, msg.Level);
            (targetPlayer?.ComponentVitalStats as SuComponentVitalStats)?
                .ApplyAuthoritativeTargetTemperature(msg.TargetTemperature);
            ApplyAuthoritativePlayerEffects(targetPlayer, msg);
            if (targetPlayer?.ComponentHealth != null && msg.HealthChange < -0.0001f &&
                msg.Health < previousHealth - 0.0001f)
            {
                ModManager.ModParentField.ModifyParentField(
                    targetPlayer.ComponentHealth, "m_lastHealth", previousHealth,
                    typeof(ComponentHealth));
            }
            if (remoteClientId == client.ClientID)
            {
                m_hasObservedClientHealth = true;
                m_observedClientHealth = msg.Health;
                m_observedClientSleeping = msg.IsSleeping;
                if (msg.HasKnockback && targetPlayer?.ComponentBody != null)
                {
                    targetPlayer.ComponentBody.Velocity = msg.BodyVelocity;
                    m_pendingLocalKnockbackVelocity = msg.BodyVelocity;
                    m_pendingLocalKnockbackUntil = Time.RealTime + LocalKnockbackHoldDuration;
                    m_localInputBodyVelocity = msg.BodyVelocity;
                }
                return;
            }
            // Ignore delayed health snapshots from a client that has already left. Without this
            // guard the health handler can recreate the same stale RemotePlayers entry.
            if (!m_networkPlayerData.ContainsKey(remoteClientId))
                return;

            NetworkPlayerState state;
            if (!RemotePlayers.TryGetValue(remoteClientId, out state))
            {
                state = new NetworkPlayerState { ClientID = remoteClientId };
                RemotePlayers[remoteClientId] = state;
            }

            state.Health = msg.Health;
            state.MaxHealth = msg.MaxHealth;
            state.IsDead = msg.IsDead;
        }

        // Source: ComponentHealth.cs:ComponentHealth.Update
        // Source: ComponentCreatureSounds.cs:ComponentCreatureSounds.PlayPainSound
        private void PlayAuthoritativePainSound(int clientId, ComponentPlayer player)
        {
            ComponentCreatureSounds sounds = player?.ComponentCreatureSounds;
            SubsystemTime subsystemTime = player?.Project?.FindSubsystem<SubsystemTime>(false);
            if (sounds == null || subsystemTime == null) return;
            if (m_hostPainSoundTimes.TryGetValue(clientId, out double lastPainTime) &&
                subsystemTime.GameTime < lastPainTime + 1.0)
                return;
            m_hostPainSoundTimes[clientId] = subsystemTime.GameTime;
            // This accepted damage request is already deduplicated by its lower authoritative
            // health value. Let that edge through the shared one-second creature-sound limiter;
            // the following ComponentHealth update is then naturally suppressed as a duplicate.
            ModManager.ModParentField.ModifyParentField(
                sounds, "m_lastSoundTime", subsystemTime.GameTime - 2.0,
                typeof(ComponentCreatureSounds));
            sounds.PlayPainSound();
        }

        // Source: SubsystemTime.cs:SubsystemTime.NextFrame
        // Split-screen keeps running while at least one player is outside GameMenuDialog. A
        // network room has players on other devices, so local dialogs must never pause its clock.
        private static void MaintainMultiplayerTimeFlow(Project project)
        {
            if (project == null || client?.IsConnected != true) return;
            SubsystemTime subsystemTime = project.FindSubsystem<SubsystemTime>(false);
            if (subsystemTime == null) return;
            ModManager.ModParentField.ModifyParentField(
                subsystemTime, "m_gameTimeFactor", 1f, typeof(SubsystemTime));
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
        // Source: Survivalcraft/Game/ComponentOnFire.cs:ComponentOnFire.Update
        private static void ApplyAuthoritativePlayerEffects(
            ComponentPlayer player, GamePlayerHealthMessage message)
        {
            if (player == null || message == null) return;
            if (player.ComponentSleep != null && player.ComponentSleep.IsSleeping != message.IsSleeping)
            {
                if (message.IsSleeping) player.ComponentSleep.Sleep(true);
                else player.ComponentSleep.WakeUp();
            }
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            if (onFire != null)
                ModManager.ModParentField.ModifyParentField(
                    onFire, "m_fireDuration", MathUtils.Max(message.FireDuration, 0f), typeof(ComponentOnFire));
            if (flu != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    flu, "m_fluDuration", MathUtils.Max(message.FluDuration, 0f), typeof(ComponentFlu));
                (flu as SuComponentFlu)?.ApplyAuthoritativeCough(
                    message.CoughSequence, message.IsCoughing);
            }
            if (sickness != null)
                ModManager.ModParentField.ModifyParentField(
                    sickness, "m_sicknessDuration", MathUtils.Max(message.SicknessDuration, 0f), typeof(ComponentSickness));
        }

        public void HandleGameKickPlayerMessage(GameKickPlayerMessage msg, int sourceClientID)
        {
            // 仅 Host 可以处理踢人
            if (client.ClientID != 0) return;

            int targetID = msg.TargetClientID;
            Log.Information($"[ScMP] Kick request: ClientID {targetID}, reason: {msg.Reason}");

            // 释放玩家映射
            playerMappingManager.ReleasePlayerIndex(targetID);

            // 通过 Drt 框架断开玩家
            // Comms.Drt 内部管理连接，我们通过 RefuseJoinGame 已经可以阻止加入
            // Peer 层的 DisconnectPeer 需要 PeerData 引用
            Log.Information($"[ScMP] Player {targetID} kicked");
        }

        public void HandleGameWorldInfoMessage(GameWorldInfoMessage1 msg)
        {
            Project project = GameManager.Project;
            if (project == null || IsHost) return;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            var timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.TimeOfDay
            // TimeOfDay depends on both values. Synchronizing only the offset allows the imported
            // client clock to remain minutes away from the host clock.
            if (Math.Abs(gameInfo.TotalElapsedGameTime - msg.TotalElapsedGameTime) > 0.25)
            {
                ModManager.ModParentField.ModifyParentField(
                    gameInfo, "<TotalElapsedGameTime>k__BackingField",
                    msg.TotalElapsedGameTime, typeof(SubsystemGameInfo));
                ModManager.ModParentField.ModifyParentField(
                    gameInfo, "m_lastTotalElapsedGameTime",
                    (double?)msg.TotalElapsedGameTime, typeof(SubsystemGameInfo));
            }
            gameInfo.WorldSettings.TimeOfDayMode = msg.CurrentTimeMode;
            if (Math.Abs(timeOfDay.TimeOfDayOffset - msg.TimeOfDayOffset) > 0.0001)
                timeOfDay.TimeOfDayOffset = msg.TimeOfDayOffset;
            m_pendingWorldControlActions = WorldControlAction.None;
            m_remoteWeatherState = msg;
            ApplyRemoteWeatherState();
        }

        public void TrySendWorldControlRequest(ComponentPlayer componentPlayer, WorldControlAction actions)
        {
            if (actions == WorldControlAction.None || IsHost || client?.IsConnected != true ||
                componentPlayer == null || m_networkPlayerData.Values.Contains(componentPlayer.PlayerData))
                return;
            double now = Time.RealTime;
            WorldControlAction newActions = now < m_worldControlRequestDeadline
                ? actions & ~m_pendingWorldControlActions
                : actions;
            if (newActions == WorldControlAction.None) return;
            NetworkMessageSender.SendWorldControlRequest(newActions);
            m_pendingWorldControlActions |= newActions;
            m_worldControlRequestDeadline = now + 0.5;
            if (newActions.HasFlag(WorldControlAction.Lightning))
                m_localLightningPredictionUntil = now + 3.0;
        }

        private void HandleWorldControlRequest(WorldControlRequestMessage message, int sourceClientId)
        {
            // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
            Project project = GameManager.Project;
            if (!IsHost || sourceClientId <= 0 || message == null || project == null ||
                !m_networkPlayerData.ContainsKey(sourceClientId))
                return;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            if (gameInfo.WorldSettings.GameMode != GameMode.Creative) return;

            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(true);
            SubsystemTimeOfDay timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            ComponentGui hostGui = project.FindSubsystem<SubsystemPlayers>(true).ComponentPlayers
                .FirstOrDefault(player => !m_networkPlayerData.Values.Contains(player.PlayerData))?.ComponentGui;

            if (message.Actions.HasFlag(WorldControlAction.Precipitation))
            {
                if (weather.IsPrecipitationStarted)
                {
                    weather.ManualPrecipitationEnd();
                    hostGui?.DisplaySmallMessage("Precipitation Off", Color.White, false, false);
                }
                else
                {
                    weather.ManualPrecipitationStart();
                    hostGui?.DisplaySmallMessage("Precipitation On", Color.White, false, false);
                }
            }
            if (message.Actions.HasFlag(WorldControlAction.Fog))
            {
                if (weather.IsFogStarted)
                {
                    weather.ManualFogEnd();
                    hostGui?.DisplaySmallMessage("Fog Off", Color.White, false, false);
                }
                else
                {
                    weather.ManualFogStart();
                    hostGui?.DisplaySmallMessage("Fog On", Color.White, false, false);
                }
            }
            if (message.Actions.HasFlag(WorldControlAction.TimeOfDay))
            {
                float dawn = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middawn);
                float noon = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midday);
                float dusk = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middusk);
                float midnight = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midnight);
                float nearest = MathUtils.Min(dawn, noon, dusk, midnight);
                if (dawn == nearest)
                {
                    timeOfDay.TimeOfDayOffset += dawn;
                    hostGui?.DisplaySmallMessage("Dawn", Color.White, false, false);
                }
                else if (noon == nearest)
                {
                    timeOfDay.TimeOfDayOffset += noon;
                    hostGui?.DisplaySmallMessage("Noon", Color.White, false, false);
                }
                else if (dusk == nearest)
                {
                    timeOfDay.TimeOfDayOffset += dusk;
                    hostGui?.DisplaySmallMessage("Dusk", Color.White, false, false);
                }
                else
                {
                    timeOfDay.TimeOfDayOffset += midnight;
                    hostGui?.DisplaySmallMessage("Midnight", Color.White, false, false);
                }
            }
            if (message.Actions.HasFlag(WorldControlAction.Lightning) &&
                m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData sourcePlayer) &&
                sourcePlayer?.ComponentPlayer != null)
            {
                ComponentCreatureModel model = sourcePlayer.ComponentPlayer.ComponentCreatureModel;
                Matrix eyeMatrix = Matrix.CreateFromQuaternion(model.EyeRotation);
                weather.ManualLightingStrike(model.EyePosition, eyeMatrix.Forward);
            }
            SendGameWorldInfoMessage();
        }

        public void ApplyRemoteWeatherState()
        {
            GameWorldInfoMessage1 msg = m_remoteWeatherState;
            Project project = GameManager.Project;
            if (msg == null || project == null || IsHost) return;
            // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdatePrecipitation
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(true);
            if (weather.IsPrecipitationStarted != msg.IsPrecipitationStarted)
            {
                if (msg.IsPrecipitationStarted) weather.ManualPrecipitationStart();
                else weather.ManualPrecipitationEnd();
            }
            if (weather.IsFogStarted != msg.IsFogStarted)
            {
                if (msg.IsFogStarted) weather.ManualFogStart();
                else weather.ManualFogEnd();
            }
            ModManager.ModParentField.ModifyParentField(
                weather, "<PrecipitationIntensity>k__BackingField", msg.PrecipitationIntensity, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogProgress>k__BackingField", msg.FogProgress, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogIntensity>k__BackingField", msg.FogIntensity, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogSeed>k__BackingField", msg.FogSeed, typeof(SubsystemWeather));
            SuppressClientRandomLightning(project);

            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(true);
            if (msg.HasLightningStrike && !m_remoteLightningActive)
            {
                bool playThunder = Time.RealTime >= m_localLightningPredictionUntil;
                ApplyRemoteLightningVisual(sky, msg.LightningStrikePosition, playThunder);
                m_localLightningPredictionUntil = 0.0;
                m_remoteLightningActive = true;
            }
            else if (!msg.HasLightningStrike)
            {
                ClearRemoteLightningVisual(sky);
                m_remoteLightningActive = false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdateLightning
        private void SuppressClientRandomLightning(Project project)
        {
            if (IsHost || project == null) return;
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    weather, "m_lightningIntensity", 0f, typeof(SubsystemWeather));
            }
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike
        // The original method also damages creatures, starts fires and creates a random explosion.
        // A client replica must only render the host event; terrain effects arrive separately.
        private void ApplyRemoteLightningVisual(SubsystemSky sky, Vector3 position,
            bool playThunder)
        {
            if (sky == null) return;
            SubsystemTime subsystemTime = GameManager.Project?.FindSubsystem<SubsystemTime>(false);
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lastLightningStrikeTime", subsystemTime?.GameTime ?? 0.0,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikePosition", (Vector3?)position,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikeBrightness", 1f, typeof(SubsystemSky));
            if (playThunder) PlayRemoteThunder(sky, position);
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike
        // Reproduce only the listener-distance audio branch. Damage, fire and explosions remain
        // host-authoritative and arrive through their existing synchronization paths.
        private void PlayRemoteThunder(SubsystemSky sky, Vector3 position)
        {
            SubsystemAudio audio = GameManager.Project?.FindSubsystem<SubsystemAudio>(false);
            if (audio == null) return;
            float distance = float.MaxValue;
            foreach (Vector3 listenerPosition in audio.ListenerPositions)
            {
                distance = MathUtils.Min(distance, Vector2.Distance(
                    new Vector2(listenerPosition.X, listenerPosition.Z),
                    new Vector2(position.X, position.Z)));
            }
            if (distance >= 200f) return;
            Engine.Random random = ModManager.ModParentField.GetParentField<Engine.Random>(
                sky, "m_random", typeof(SubsystemSky));
            float pitch = random != null ? random.Float(-0.2f, 0.2f) : 0f;
            float delay = audio.CalculateDelay(distance);
            if (distance < 40f)
                audio.PlayRandomSound("Audio/ThunderNear", 1f, pitch, 0f, delay);
            else
                audio.PlayRandomSound("Audio/ThunderFar", 0.8f, pitch, 0f, delay);
        }

        private void ClearRemoteLightningVisual(SubsystemSky sky)
        {
            if (sky == null) return;
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikePosition", (Vector3?)null,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikeBrightness", 0f, typeof(SubsystemSky));
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        public void PublishTerrainChanges(Dictionary<Point3, bool> modifiedCells)
        {
            if (client?.IsConnected != true || modifiedCells == null || modifiedCells.Count == 0)
                return;
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            // Client terrain is prediction. The host executes the same remote input through the
            // original ComponentMiner and is the only source of authoritative terrain changes.
            if (!IsHost)
            {
                SubmitClientTerrainPredictions(modifiedCells);
                return;
            }
            var message = new GameModifiedCellsMessage(modifiedCells, client.Step);
            lock (m_terrainJournalLock)
                message.Sequence = ++m_hostTerrainSequence;
            RecordHostTerrainChanges(message, client.Step);
            RecordHostTerrainJournal(message);
            // Source: ScMultiplayer.cs:NetworkMessageHandler.HandleModifiedCellsMessage
            NetworkMessageSender.SendScheduledMessage(-1, message);
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        private void RecordHostTerrainJournal(GameModifiedCellsMessage message)
        {
            if (!IsHost || message?.Sequence <= 0) return;
            byte[] payload = Message.WriteWithSender(message, client.Address);
            lock (m_terrainJournalLock)
            {
                m_hostTerrainJournal.Enqueue(new TerrainJournalEntry
                {
                    Sequence = message.Sequence,
                    ServerStep = message.Tick,
                    CreatedTime = Time.RealTime,
                    Payload = payload
                });
                TrimHostTerrainJournalLocked(Time.RealTime);
            }
        }

        private void TrimHostTerrainJournalLocked(double now)
        {
            while (m_hostTerrainJournal.Count > 0 &&
                now - m_hostTerrainJournal.Peek().CreatedTime > TerrainRecoveryRetention)
                m_hostTerrainJournal.Dequeue();
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.Client_GameStep
        private void HandleTerrainRecoveryMessage(TerrainRecoveryMessage message,
            int sourceClientId)
        {
            if (message == null) return;
            if (IsHost)
            {
                if (sourceClientId <= 0 || !m_networkPlayerData.ContainsKey(sourceClientId))
                    return;
                if (message.Stage == TerrainRecoveryStage.Request)
                {
                    SendHostTerrainRecoveryRound(sourceClientId,
                        message.LastAppliedSequence, message.BufferedRanges);
                }
                else if (message.Stage == TerrainRecoveryStage.Acknowledge &&
                    m_hostTerrainRecoveryTargets.TryGetValue(sourceClientId,
                        out long target) && message.LastAppliedSequence >= target)
                {
                    long head;
                    lock (m_terrainJournalLock) head = m_hostTerrainSequence;
                    if (message.LastAppliedSequence < head)
                    {
                        SendHostTerrainRecoveryRound(sourceClientId,
                            message.LastAppliedSequence, null);
                    }
                    else
                    {
                        m_hostTerrainRecoveryTargets.Remove(sourceClientId);
                        m_forceHostInventorySync = true;
                        m_fullWorldObjectsSyncTime = WorldObjectFullSyncInterval;
                        m_fullAnimalSyncTime = 1f;
                        SendTerrainRecoveryMessage(sourceClientId,
                            new TerrainRecoveryMessage
                            {
                                Stage = TerrainRecoveryStage.Ready,
                                LastAppliedSequence = message.LastAppliedSequence,
                                HeadSequence = head,
                                ServerStep = client.Step
                            });
                    }
                }
                return;
            }

            if (sourceClientId != 0) return;
            switch (message.Stage)
            {
                case TerrainRecoveryStage.ReplayBatch:
                    foreach (byte[] payload in message.Payloads)
                    {
                        try
                        {
                            if (Message.Read(payload) is GameModifiedCellsMessage terrain &&
                                terrain.Sequence > 0 && terrain.Sequence <= message.HeadSequence)
                                SuSubsystemTerrain.EnqueueNetworkBatch(terrain);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[ScMP] Invalid terrain replay payload: {ex.Message}");
                        }
                    }
                    break;
                case TerrainRecoveryStage.Barrier:
                    m_clientTerrainRecoveryActive = true;
                    m_clientTerrainRecoveryRequestInFlight = false;
                    m_clientTerrainRecoveryTarget = message.HeadSequence;
                    m_clientTerrainRecoveryAcknowledged = -1;
                    break;
                case TerrainRecoveryStage.Ready:
                    m_clientTerrainRecoveryActive = true;
                    m_clientTerrainRecoveryRequestInFlight = false;
                    m_clientTerrainRecoveryReady = message.HeadSequence;
                    break;
                case TerrainRecoveryStage.ResyncRequired:
                    RestartClientWorldDownload();
                    break;
            }
        }

        private void SendHostTerrainRecoveryRound(int targetClientId, long lastApplied,
            List<TerrainSequenceRange> bufferedRanges)
        {
            List<TerrainJournalEntry> replay;
            long head;
            long oldest;
            lock (m_terrainJournalLock)
            {
                TrimHostTerrainJournalLocked(Time.RealTime);
                head = m_hostTerrainSequence;
                if (lastApplied < 0 || lastApplied > head)
                {
                    SendTerrainRecoveryResyncRequired(targetClientId, head);
                    return;
                }
                TerrainJournalEntry[] journal = m_hostTerrainJournal.ToArray();
                oldest = journal.Length > 0 ? journal[0].Sequence : head + 1;
                long unavailableEnd = Math.Min(head, oldest - 1);
                if (lastApplied < unavailableEnd && !RangesCoverInterval(
                    bufferedRanges, lastApplied + 1, unavailableEnd))
                {
                    SendTerrainRecoveryResyncRequired(targetClientId, head);
                    return;
                }
                replay = journal.Where(entry => entry.Sequence > lastApplied &&
                    entry.Sequence <= head &&
                    !SequenceIsBuffered(entry.Sequence, bufferedRanges)).ToList();
                m_hostTerrainRecoveryTargets[targetClientId] = head;
            }

            var payloads = new List<byte[]>();
            int payloadBytes = 0;
            foreach (TerrainJournalEntry entry in replay)
            {
                if (payloads.Count > 0 && (payloads.Count >= 64 ||
                    payloadBytes + entry.Payload.Length > MaximumTerrainRecoveryBatchBytes))
                {
                    SendTerrainRecoveryReplayBatch(targetClientId, head, payloads);
                    payloads = new List<byte[]>();
                    payloadBytes = 0;
                }
                payloads.Add(entry.Payload);
                payloadBytes += entry.Payload.Length;
            }
            if (payloads.Count > 0)
                SendTerrainRecoveryReplayBatch(targetClientId, head, payloads);
            SendTerrainRecoveryMessage(targetClientId, new TerrainRecoveryMessage
            {
                Stage = TerrainRecoveryStage.Barrier,
                LastAppliedSequence = lastApplied,
                HeadSequence = head,
                ServerStep = client.Step
            });
            Log.Information($"[ScMP] Terrain recovery round: ClientID={targetClientId}, " +
                $"Applied={lastApplied}, Oldest={oldest}, Head={head}, Replay={replay.Count}");
        }

        private static void SendTerrainRecoveryReplayBatch(int targetClientId,
            long head, List<byte[]> payloads)
        {
            SendTerrainRecoveryMessage(targetClientId, new TerrainRecoveryMessage
            {
                Stage = TerrainRecoveryStage.ReplayBatch,
                HeadSequence = head,
                ServerStep = client.Step,
                Payloads = payloads
            });
        }

        private void SendTerrainRecoveryResyncRequired(int targetClientId, long head)
        {
            m_hostTerrainRecoveryTargets.Remove(targetClientId);
            SendTerrainRecoveryMessage(targetClientId, new TerrainRecoveryMessage
            {
                Stage = TerrainRecoveryStage.ResyncRequired,
                HeadSequence = head,
                ServerStep = client.Step
            });
            Log.Warning($"[ScMP] Terrain recovery history expired for ClientID={targetClientId}");
        }

        private static bool SequenceIsBuffered(long sequence,
            List<TerrainSequenceRange> ranges) =>
            ranges != null && ranges.Any(range => range != null &&
                sequence >= range.Start && sequence <= range.End);

        private static bool RangesCoverInterval(List<TerrainSequenceRange> ranges,
            long start, long end)
        {
            if (start > end) return true;
            long cursor = start;
            foreach (TerrainSequenceRange range in (ranges ?? new List<TerrainSequenceRange>())
                .Where(item => item != null).OrderBy(item => item.Start))
            {
                if (range.End < cursor) continue;
                if (range.Start > cursor) return false;
                cursor = Math.Max(cursor, range.End + 1);
                if (cursor > end) return true;
            }
            return false;
        }

        private void RestartClientWorldDownload()
        {
            Log.Warning("[ScMP] Terrain recovery requires a fresh host world snapshot");
            if (m_activeJoinRequest?.WorldInfo == null)
            {
                HandleHostDisconnected();
                return;
            }
            ShowJoinRoomBusyDialog();
            if (m_joinRoomBusyDialog != null)
                m_joinRoomBusyDialog.SmallMessage =
                    "Terrain history expired.\r\nDownloading the current host world...";
            PrepareClientForRemoteJoin();
            m_pendingJoinRequest = m_activeJoinRequest;
            m_isLoadingDownloadedWorld = true;
            SubmitPendingJoin(m_activeJoinPlayerName, m_activeJoinPlayerClass,
                m_activeJoinSkinName, m_activeJoinHasPlayerProfile);
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.m_modifiedCells
        private void SubmitClientTerrainPredictions(Dictionary<Point3, bool> modifiedCells)
        {
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null) return;
            var repairCells = new Dictionary<Point3, bool>();
            foreach (Point3 cell in modifiedCells.Keys)
            {
                bool pending = m_pendingTerrainPredictionCells.ContainsKey(cell);
                bool hasIntent = m_localTerrainDigIntents.TryGetValue(cell,
                    out LocalTerrainDigIntent intent);
                double intentAge = hasIntent ? Time.RealTime - intent.LastSeenTime : -1.0;
                if (pending)
                    continue;
                int predictedValue = Terrain.ReplaceLight(
                    terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z), 0);
                if (hasIntent && intentAge <= 2.0 && predictedValue != intent.ExpectedValue)
                    QueueTerrainDigRequest(cell, intent, predictedValue);
                else if (!hasIntent || intentAge > 2.0)
                    repairCells[cell] = modifiedCells[cell];
            }
            RequestAuthoritativeTerrainRepair(repairCells);
        }

        private void RequestAuthoritativeTerrainRepair(Dictionary<Point3, bool> cells)
        {
            if (cells == null || cells.Count == 0 || client?.IsConnected != true) return;
            KeyValuePair<Point3, bool>[] items = cells.ToArray();
            for (int offset = 0; offset < items.Length; offset += TerrainCatchUpBatchSize)
            {
                var batch = new Dictionary<Point3, bool>();
                int count = Math.Min(TerrainCatchUpBatchSize, items.Length - offset);
                for (int i = 0; i < count; i++)
                    batch[items[offset + i].Key] = items[offset + i].Value;
                var message = new GameModifiedCellsMessage(batch, client.Step);
                client.SendDirectInput(0,
                    Message.WriteWithSender(message, client.Address), sequenced: true);
            }
        }

        private void QueueTerrainDigRequest(Point3 cell, LocalTerrainDigIntent intent,
            int predictedValue)
        {
            if (intent == null || m_pendingTerrainPredictionCells.ContainsKey(cell)) return;
            m_nextTerrainDigRequestId = m_nextTerrainDigRequestId == int.MaxValue
                ? 1
                : m_nextTerrainDigRequestId + 1;
            var request = new TerrainDigRequestMessage(m_nextTerrainDigRequestId, cell,
                intent.ExpectedValue, predictedValue, intent.DigRay,
                intent.HitFace, intent.StartClientTick, client.Step, intent.ActiveSlotIndex,
                intent.ToolValue)
            {
                ToolCount = intent.ToolCount,
                BodyPosition = intent.BodyPosition
            };
            m_pendingTerrainPredictions[request.RequestId] = new PendingTerrainPrediction
            {
                Request = request,
                LastSendTime = Time.RealTime,
                SendCount = 1
            };
            m_pendingTerrainPredictionCells[cell] = request.RequestId;
            m_localTerrainDigIntents.Remove(cell);
            NetworkMessageSender.SendTerrainDigRequest(request);
        }

        private void UpdatePendingTerrainPredictions()
        {
            if (client?.IsConnected != true) return;
            double now = Time.RealTime;
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain != null)
            {
                foreach (KeyValuePair<Point3, LocalTerrainDigIntent> item in
                    m_localTerrainDigIntents.ToArray())
                {
                    if (m_pendingTerrainPredictionCells.ContainsKey(item.Key)) continue;
                    int currentValue = Terrain.ReplaceLight(terrain.Terrain.GetCellValue(
                        item.Key.X, item.Key.Y, item.Key.Z), 0);
                    if (currentValue != item.Value.ExpectedValue)
                        QueueTerrainDigRequest(item.Key, item.Value, currentValue);
                }
            }
            foreach (Point3 cell in m_localTerrainDigIntents.Where(
                item => now - item.Value.LastSeenTime > 2.0).Select(item => item.Key).ToArray())
                m_localTerrainDigIntents.Remove(cell);
            if (m_pendingTerrainPredictions.Count == 0) return;
            foreach (PendingTerrainPrediction prediction in
                m_pendingTerrainPredictions.Values.ToArray())
            {
                if (prediction.Result != null)
                {
                    if (now >= prediction.ReconcileTime)
                    {
                        ApplyTerrainDigResult(prediction.Result);
                        RemovePendingTerrainPrediction(prediction.Request.RequestId);
                    }
                    continue;
                }
                double retryPeriod = prediction.SendCount < 8 ? 0.25 : 1.0;
                if (now - prediction.LastSendTime < retryPeriod) continue;
                prediction.LastSendTime = now;
                prediction.SendCount++;
                NetworkMessageSender.SendTerrainDigRequest(prediction.Request);
            }
        }

        private void HandleTerrainDigResult(TerrainDigResultMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null ||
                !m_pendingTerrainPredictions.TryGetValue(message.RequestId,
                    out PendingTerrainPrediction prediction) ||
                prediction.Request.Cell != message.Cell)
                return;
            if (message.Accepted || message.AuthoritativeValue == prediction.Request.PredictedValue)
            {
                ApplyTerrainDigResult(message);
                RemovePendingTerrainPrediction(message.RequestId);
            }
            else
            {
                // A direct request can overtake the matching inventory/input tick. Retry several
                // times before restoring so a valid local dig does not visibly pop back.
                if (prediction.SendCount < 4)
                {
                    prediction.LastSendTime = Time.RealTime;
                }
                else
                {
                    prediction.Result = message;
                    prediction.ReconcileTime = Time.RealTime + 0.25;
                }
            }
        }

        private void ApplyTerrainDigResult(TerrainDigResultMessage message)
        {
            var cells = new Dictionary<Point3, bool> { [message.Cell] = true };
            var values = new List<int> { message.AuthoritativeValue };
            SuSubsystemTerrain.EnqueueNetworkBatch(new GameModifiedCellsMessage(
                cells, values, message.ServerTick, false, client.ClientID));
        }

        private void RemovePendingTerrainPrediction(int requestId)
        {
            if (!m_pendingTerrainPredictions.TryGetValue(requestId,
                out PendingTerrainPrediction prediction))
                return;
            m_pendingTerrainPredictions.Remove(requestId);
            m_pendingTerrainPredictionCells.Remove(prediction.Request.Cell);
        }

        public void HandleGameModifiedCellsMessage(GameModifiedCellsMessage msg, int sourceClientId)
        {
            // Source: SuSubsystemTerrain.cs - 接收远程方块修改
            if (msg == null || (msg.TargetClientId >= 0 && msg.TargetClientId != client.ClientID))
                return;
            if (IsHost && sourceClientId != 0)
            {
                SendAuthoritativeTerrainRepair(sourceClientId, msg.ModifiedCells);
                return;
            }
            else if (!IsHost && sourceClientId == 0)
            {
                msg = FilterStaleTerrainRepairs(msg);
                if (msg == null || msg.ModifiedCells.Count == 0) return;
                ConfirmTerrainPredictions(msg);
            }
            SuSubsystemTerrain.EnqueueNetworkBatch(msg);
        }

        private void SendAuthoritativeTerrainRepair(int targetClientId,
            Dictionary<Point3, bool> requestedCells)
        {
            if (!IsHost || targetClientId <= 0 || requestedCells == null ||
                requestedCells.Count == 0 || !m_networkPlayerData.ContainsKey(targetClientId))
                return;
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null) return;
            var cells = new Dictionary<Point3, bool>();
            var values = new List<int>();
            foreach (KeyValuePair<Point3, bool> item in requestedCells)
            {
                if (!terrain.Terrain.IsCellValid(item.Key.X, item.Key.Y, item.Key.Z)) continue;
                cells[item.Key] = item.Value;
                int value = terrain.Terrain.GetCellValue(
                    item.Key.X, item.Key.Y, item.Key.Z);
                values.Add(Terrain.ReplaceLight(value, 0));
            }
            if (cells.Count == 0) return;
            var response = new GameModifiedCellsMessage(cells, values, client.Step,
                true, targetClientId);
            client.SendDirectInput(targetClientId,
                Message.WriteWithSender(response, client.Address), sequenced: true);
        }

        private GameModifiedCellsMessage FilterStaleTerrainRepairs(
            GameModifiedCellsMessage message)
        {
            if (message?.IsCatchUp != true || message.ModifiedCells == null ||
                message.CellValues == null || m_pendingTerrainPredictionCells.Count == 0)
                return message;

            var cells = new Dictionary<Point3, bool>();
            var values = new List<int>();
            int index = 0;
            foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
            {
                int value = index < message.CellValues.Count
                    ? message.CellValues[index]
                    : 0;
                bool staleRepair = m_pendingTerrainPredictionCells.TryGetValue(
                    item.Key, out int requestId) &&
                    m_pendingTerrainPredictions.TryGetValue(
                        requestId, out PendingTerrainPrediction prediction) &&
                    value != prediction.Request.PredictedValue;
                if (!staleRepair)
                {
                    cells[item.Key] = item.Value;
                    values.Add(value);
                }
                index++;
            }
            if (cells.Count == message.ModifiedCells.Count) return message;
            return cells.Count > 0
                ? new GameModifiedCellsMessage(cells, values, message.Tick,
                    message.IsCatchUp, message.TargetClientId, message.Sequence)
                : null;
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Dig
        private void HandleTerrainDigRequest(TerrainDigRequestMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0) return;
            long requestKey = ((long)sourceClientId << 32) | (uint)message.RequestId;
            if (m_processedTerrainDigRequests.TryGetValue(requestKey,
                out TerrainDigResultMessage previousResult))
            {
                NetworkMessageSender.SendTerrainDigResult(sourceClientId, previousResult);
                return;
            }

            Project project = GameManager.Project;
            bool accepted = false;
            int authoritativeValue = 0;
            if (project != null && m_networkPlayerData.TryGetValue(sourceClientId,
                out PlayerData playerData) && playerData?.ComponentPlayer != null)
            {
                ComponentPlayer player = playerData.ComponentPlayer;
                ComponentMiner miner = player.ComponentMiner;
                if (IsFinite(message.BodyPosition) &&
                    Vector3.DistanceSquared(player.ComponentBody.Position,
                        message.BodyPosition) <= 64f)
                    player.ComponentBody.Position = message.BodyPosition;
                SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(true);
                int currentCellValue = terrain.Terrain.GetCellValue(
                    message.Cell.X, message.Cell.Y, message.Cell.Z);
                authoritativeValue = Terrain.ReplaceLight(currentCellValue, 0);
                Vector3 center = new Vector3(message.Cell.X + 0.5f,
                    message.Cell.Y + 0.5f, message.Cell.Z + 0.5f);
                if (authoritativeValue == message.PredictedValue)
                {
                    accepted = true;
                }
                else if (miner != null &&
                    authoritativeValue == message.ExpectedValue)
                {
                    SubsystemGameInfo gameInfo =
                        project.FindSubsystem<SubsystemGameInfo>(true);
                    bool creative = gameInfo.WorldSettings.GameMode == GameMode.Creative;
                    float reach = creative ? SettingsManager.CreativeReach : 5f;
                    Vector3 authoritativePlayerPosition = player.ComponentBody.Position;
                    if (m_networkPlayerInputs.TryGetValue(sourceClientId,
                        out NetworkPlayerInputState inputState) &&
                        Time.RealTime - inputState.LastReceivedTime <= RemoteInputHoldDuration)
                    {
                        authoritativePlayerPosition = inputState.BodyPosition;
                    }
                    bool inReach = Vector3.DistanceSquared(
                        authoritativePlayerPosition, center) <=
                        MathUtils.Sqr(reach + 1.5f);
                    Vector3 rayDirection = message.DigRay.Direction;
                    TerrainRaycastResult? targetRaycast = null;
                    if (inReach && message.HitFace >= 0 && message.HitFace <= 5 &&
                        rayDirection.LengthSquared() > 0.0001f)
                    {
                        // Source: SubsystemTerrain.cs:SubsystemTerrain.Raycast
                        // Test the client's requested cell directly. A preceding predicted cell may
                        // still exist on the host while fast consecutive dig requests are in flight.
                        rayDirection = Vector3.Normalize(rayDirection);
                        var ray = new Ray3(message.DigRay.Position, rayDirection);
                        Block targetBlock = BlocksManager.Blocks[
                            Terrain.ExtractContents(currentCellValue)];
                        var localRay = new Ray3(ray.Position - new Vector3(
                            message.Cell.X, message.Cell.Y, message.Cell.Z), ray.Direction);
                        float? distance = targetBlock.Raycast(localRay, terrain,
                            currentCellValue, true, out int collisionBoxIndex,
                            out BoundingBox collisionBox);
                        if (!targetBlock.IsDiggingTransparent && distance.HasValue &&
                            distance.Value >= 0f && distance.Value <= 15f)
                        {
                            targetRaycast = new TerrainRaycastResult
                            {
                                Ray = ray,
                                Value = currentCellValue,
                                CellFace = new CellFace(message.Cell.X, message.Cell.Y,
                                    message.Cell.Z, message.HitFace),
                                CollisionBoxIndex = collisionBoxIndex,
                                Distance = distance.Value
                            };
                        }
                    }
                    if (targetRaycast.HasValue)
                    {
                        IInventory inventory = miner.Inventory;
                        bool validToolSlot = ApplyTerrainDigToolState(inventory, message);
                        int toolValue = validToolSlot ? miner.ActiveBlockValue : 0;
                        int toolContents = Terrain.ExtractContents(toolValue);
                        Block block = BlocksManager.Blocks[Terrain.ExtractContents(authoritativeValue)];
                        Block tool = BlocksManager.Blocks[toolContents];
                        bool levelSufficient = ModManager.ModParentMethod.InvokeParentMethod<bool>(
                            miner, "IsLevelSufficientForTool", toolValue);
                        float requiredTime = ModManager.ModParentMethod.InvokeParentMethod<float>(
                            miner, "CalculateDigTime", authoritativeValue, toolContents);
                        float elapsedTime = MathUtils.Max(0f,
                            (message.CompletedClientTick - message.StartClientTick) * ServerTickDuration);
                        BlockPlacementData digValue = block.GetDigValue(
                            terrain, miner, currentCellValue, toolValue, targetRaycast.Value);
                        Point3 digPoint = new Point3(digValue.CellFace.X,
                            digValue.CellFace.Y, digValue.CellFace.Z);
                        bool matchingDigProgress = miner.DigCellFace.HasValue &&
                            miner.DigCellFace.Value.X == message.Cell.X &&
                            miner.DigCellFace.Value.Y == message.Cell.Y &&
                            miner.DigCellFace.Value.Z == message.Cell.Z &&
                            miner.DigProgress >= 0.85f;
                        if (validToolSlot && levelSufficient && digPoint == message.Cell &&
                            (creative || matchingDigProgress ||
                                elapsedTime + 0.4f >= requiredTime))
                        {
                            terrain.DestroyCell(tool.ToolLevel, digPoint.X, digPoint.Y,
                                digPoint.Z, digValue.Value, false, false);
                            terrain.TerrainUpdater.RequestSynchronousUpdate();
                            miner.DamageActiveTool(1);
                            if (miner.ComponentCreature.PlayerStats != null)
                                miner.ComponentCreature.PlayerStats.BlocksDug++;
                            authoritativeValue = Terrain.ReplaceLight(
                                terrain.Terrain.GetCellValue(digPoint.X, digPoint.Y, digPoint.Z), 0);
                            accepted = true;
                        }
                    }
                }
            }

            var result = new TerrainDigResultMessage(message.RequestId, message.Cell,
                accepted, authoritativeValue, client.Step);
            if (accepted)
            {
                if (m_processedTerrainDigRequests.Count >= 2048)
                    m_processedTerrainDigRequests.Clear();
                m_processedTerrainDigRequests[requestKey] = result;
            }
            NetworkMessageSender.SendTerrainDigResult(sourceClientId, result);
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Dig
        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.AddSlotItems
        private static bool ApplyTerrainDigToolState(IInventory inventory,
            TerrainDigRequestMessage message)
        {
            if (inventory == null || message == null || message.ActiveSlotIndex < 0 ||
                message.ActiveSlotIndex >= inventory.VisibleSlotsCount ||
                message.ToolCount < 0 || (message.ToolValue != 0 && message.ToolCount <= 0))
                return false;
            int slot = message.ActiveSlotIndex;
            int count = message.ToolValue == 0 ? 0 : Math.Min(message.ToolCount,
                inventory.GetSlotCapacity(slot, message.ToolValue));
            if (message.ToolValue != 0 && count <= 0) return false;
            inventory.ActiveSlotIndex = slot;
            inventory.RemoveSlotItems(slot, int.MaxValue);
            if (count > 0) inventory.AddSlotItems(slot, message.ToolValue, count);
            return inventory.GetSlotValue(slot) == message.ToolValue &&
                inventory.GetSlotCount(slot) == count;
        }

        private void ConfirmTerrainPredictions(GameModifiedCellsMessage message)
        {
            if (message?.ModifiedCells == null || message.CellValues == null) return;
            int index = 0;
            foreach (Point3 cell in message.ModifiedCells.Keys)
            {
                if (index >= message.CellValues.Count) break;
                if (m_pendingTerrainPredictionCells.TryGetValue(cell, out int requestId) &&
                    m_pendingTerrainPredictions.TryGetValue(requestId,
                        out PendingTerrainPrediction prediction) &&
                    message.CellValues[index] == prediction.Request.PredictedValue)
                    RemovePendingTerrainPrediction(requestId);
                index++;
            }
        }

        private void RecordHostTerrainChanges(GameModifiedCellsMessage message, int authoritativeTick)
        {
            if (message?.ModifiedCells == null) return;
            lock (m_terrainJournalLock)
            {
                int index = 0;
                foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
                {
                    if (message.CellValues != null && index < message.CellValues.Count)
                    {
                        m_pendingTerrainChanges[item.Key] = new TerrainCellState
                        {
                            IsModified = item.Value,
                            CellValue = message.CellValues[index],
                            Tick = authoritativeTick
                        };
                        m_terrainRepairRepeats[item.Key] = TerrainRepairRepeatCount;
                    }
                    index++;
                }
            }
        }

        private void MergePendingTerrainChanges()
        {
            lock (m_terrainJournalLock)
            {
                foreach (KeyValuePair<Point3, TerrainCellState> item in m_pendingTerrainChanges)
                    m_terrainCheckpoint[item.Key] = item.Value;
                m_pendingTerrainChanges.Clear();
            }
        }

        // Source: Comms/Drt/GameStepData.Inputs
        // Live terrain deltas are sent once. Repeat each authoritative final value a bounded
        // number of times so a lost UDP input cannot leave one cell permanently divergent.
        private void BroadcastTerrainRepairs()
        {
            List<KeyValuePair<Point3, TerrainCellState>> repairs;
            lock (m_terrainJournalLock)
            {
                repairs = new List<KeyValuePair<Point3, TerrainCellState>>(m_terrainRepairRepeats.Count);
                foreach (Point3 point in m_terrainRepairRepeats.Keys.ToArray())
                {
                    TerrainCellState state;
                    if (!m_pendingTerrainChanges.TryGetValue(point, out state) &&
                        !m_terrainCheckpoint.TryGetValue(point, out state))
                    {
                        m_terrainRepairRepeats.Remove(point);
                        continue;
                    }
                    repairs.Add(new KeyValuePair<Point3, TerrainCellState>(point, state));
                    int repeats = m_terrainRepairRepeats[point] - 1;
                    if (repeats > 0) m_terrainRepairRepeats[point] = repeats;
                    else m_terrainRepairRepeats.Remove(point);
                }
            }
            if (repairs.Count == 0) return;

            int authoritativeTick = client.Step;
            for (int offset = 0; offset < repairs.Count; offset += TerrainCatchUpBatchSize)
            {
                var cells = new Dictionary<Point3, bool>();
                var values = new List<int>();
                int count = Math.Min(TerrainCatchUpBatchSize, repairs.Count - offset);
                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<Point3, TerrainCellState> item = repairs[offset + i];
                    cells[item.Key] = item.Value.IsModified;
                    values.Add(item.Value.CellValue);
                }
                // Source: ScMultiplayer.cs:NetworkMessageHandler.HandleModifiedCellsMessage
                NetworkMessageSender.SendScheduledMessage(-1, new GameModifiedCellsMessage(
                    cells, values, authoritativeTick, true, -1));
            }
        }

        // Source: Comms/Drt/Client.cs:Client.GameJoined
        // The base world is the room-creation snapshot. This targeted checkpoint advances a
        // rejoining client from that snapshot to the host's current authoritative terrain tick.
        private void SendTerrainCatchUp(int targetClientId)
        {
            List<KeyValuePair<Point3, TerrainCellState>> snapshot;
            int targetTick = client.Step;
            lock (m_terrainJournalLock)
            {
                foreach (KeyValuePair<Point3, TerrainCellState> item in m_pendingTerrainChanges)
                    m_terrainCheckpoint[item.Key] = item.Value;
                m_pendingTerrainChanges.Clear();
                snapshot = m_terrainCheckpoint
                    .OrderBy(item => item.Value.Tick)
                    .ThenBy(item => item.Key.X)
                    .ThenBy(item => item.Key.Y)
                    .ThenBy(item => item.Key.Z)
                    .ToList();
            }

            if (snapshot.Count == 0)
            {
                var marker = new GameModifiedCellsMessage(
                    new Dictionary<Point3, bool>(), new List<int>(), targetTick, true, targetClientId);
                client.SendDirectInput(targetClientId,
                    Message.WriteWithSender(marker, client.Address), sequenced: true);
                return;
            }

            for (int offset = 0; offset < snapshot.Count; offset += TerrainCatchUpBatchSize)
            {
                var cells = new Dictionary<Point3, bool>();
                var values = new List<int>();
                int count = Math.Min(TerrainCatchUpBatchSize, snapshot.Count - offset);
                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<Point3, TerrainCellState> item = snapshot[offset + i];
                    cells[item.Key] = item.Value.IsModified;
                    values.Add(item.Value.CellValue);
                }
                var message = new GameModifiedCellsMessage(
                    cells, values, targetTick, true, targetClientId);
                client.SendDirectInput(targetClientId,
                    Message.WriteWithSender(message, client.Address), sequenced: true);
            }
            Log.Information($"[ScMP] Sent terrain catch-up: ClientID={targetClientId}, Tick={targetTick}, Cells={snapshot.Count}");
        }

        private IncomingWorldTransfer GetOrCreateIncomingWorldTransfer(
            int transferId, int targetClientId, int chunkCount, int totalLength)
        {
            int expectedChunks = totalLength > 0
                ? (totalLength + WorldTransferChunkSize - 1) / WorldTransferChunkSize
                : 0;
            if (transferId <= 0 || targetClientId != client.ClientID ||
                totalLength <= 0 || totalLength > MaximumWorldTransferSize ||
                chunkCount <= 0 || chunkCount != expectedChunks)
                return null;
            if (m_incomingWorldTransfers.TryGetValue(transferId,
                out IncomingWorldTransfer existing))
            {
                return existing.TargetClientId == targetClientId &&
                    existing.TotalLength == totalLength && existing.Chunks.Length == chunkCount
                    ? existing
                    : null;
            }
            var transfer = new IncomingWorldTransfer
            {
                TransferId = transferId,
                TargetClientId = targetClientId,
                TotalLength = totalLength,
                Chunks = new byte[chunkCount][],
                StartTime = Time.RealTime,
                LastProgressTime = Time.RealTime
            };
            m_incomingWorldTransfers.Add(transferId, transfer);
            return transfer;
        }

        private void HandleGamePakWorldChunkMessage(GamePakWorldChunkMessage message)
        {
            if (IsHost || message == null) return;
            IncomingWorldTransfer transfer = GetOrCreateIncomingWorldTransfer(
                message.TransferId, message.TargetClientId,
                message.ChunkCount, message.TotalLength);
            if (transfer == null || message.ChunkIndex < 0 ||
                message.ChunkIndex >= transfer.Chunks.Length || message.Data == null)
                return;
            int expectedLength = Math.Min(WorldTransferChunkSize,
                transfer.TotalLength - message.ChunkIndex * WorldTransferChunkSize);
            if (message.Data.Length != expectedLength) return;
            if (transfer.Chunks[message.ChunkIndex] == null)
            {
                transfer.Chunks[message.ChunkIndex] = message.Data;
                transfer.ReceivedChunkCount++;
                transfer.ReceivedBytes += message.Data.Length;
                transfer.HighestReceivedChunkIndex = Math.Max(
                    transfer.HighestReceivedChunkIndex, message.ChunkIndex);
                while (transfer.HighestContiguousChunkIndex + 1 < transfer.Chunks.Length &&
                    transfer.Chunks[transfer.HighestContiguousChunkIndex + 1] != null)
                    transfer.HighestContiguousChunkIndex++;
                transfer.LastProgressTime = Time.RealTime;
            }
            TryCompleteIncomingWorldTransfer(transfer);
        }

        private void TryCompleteIncomingWorldTransfer(IncomingWorldTransfer transfer)
        {
            if (transfer?.Manifest == null ||
                transfer.ReceivedChunkCount != transfer.Chunks.Length)
                return;
            var worldData = new byte[transfer.TotalLength];
            int offset = 0;
            foreach (byte[] chunk in transfer.Chunks)
            {
                if (chunk == null || offset + chunk.Length > worldData.Length) return;
                Array.Copy(chunk, 0, worldData, offset, chunk.Length);
                offset += chunk.Length;
            }
            if (offset != worldData.Length) return;
            GamePakWorldMessage manifest = transfer.Manifest;
            byte[] expectedHash = manifest.WorldSha256;
            byte[] actualHash = SHA256.HashData(worldData);
            if (expectedHash == null || expectedHash.Length != actualHash.Length ||
                !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                ResetIncomingWorldTransferAfterChecksumFailure(transfer);
                return;
            }
            double elapsed = Math.Max(Time.RealTime - transfer.StartTime, 0.0);
            Log.Information($"[ScMP] World download complete: Transfer={transfer.TransferId}, " +
                $"Transport=UDP, Bytes={transfer.TotalLength}, Seconds={elapsed:0.00}, " +
                $"RepairRounds={transfer.RepairRequestCount}");
            if (m_joinRoomBusyDialog != null)
                m_joinRoomBusyDialog.SmallMessage =
                    "Connected.\r\nWorld download complete.\r\nImporting world...";
            manifest.WorldData = worldData;
            manifest.ChunkCount = 0;
            m_incomingWorldTransfers.Remove(transfer.TransferId);
            HandleGamePakWorldMessage(manifest);
        }

        // Source: ScMultiplayer.cs:RequestMissingWorldTransferChunks
        private static void ResetIncomingWorldTransferAfterChecksumFailure(
            IncomingWorldTransfer transfer)
        {
            Array.Clear(transfer.Chunks, 0, transfer.Chunks.Length);
            transfer.ReceivedChunkCount = 0;
            transfer.ReceivedBytes = 0;
            transfer.HighestContiguousChunkIndex = -1;
            transfer.HighestReceivedChunkIndex = -1;
            transfer.LastProgressTime = Time.RealTime;
            transfer.LastStatusRequestTime = 0.0;
            transfer.LastRepairRequestTime = 0.0;
            transfer.RepairRequestCount++;
            Log.Warning($"[ScMP] World transfer checksum failed; requesting all chunks again: " +
                $"Transfer={transfer.TransferId}, Attempt={transfer.RepairRequestCount}");
        }

        private void HandleGamePakWorldReadyMessage(
            GamePakWorldReadyMessage message, int sourceClientId)
        {
            if (message == null)
                return;

            if (!IsHost)
            {
                if (sourceClientId != 0 || m_pendingWorldReadyTransferId <= 0 ||
                    message.TransferId != m_pendingWorldReadyTransferId)
                    return;
                if (message.Stage == GamePakWorldReadyStage.CatchUpBatchComplete)
                {
                    NetworkMessageSender.SendPakWorldReady(new GamePakWorldReadyMessage(
                        message.TransferId, GamePakWorldReadyStage.CatchUpBatchApplied));
                    return;
                }
                if (message.Stage != GamePakWorldReadyStage.ReadyToPlay) return;
                Log.Information($"[ScMP] Client catch-up complete: Transfer={message.TransferId}");
                m_pendingWorldReadyTransferId = 0;
                m_isLoadingDownloadedWorld = false;
                // A fresh world transfer supersedes any lifecycle recovery state left by the
                // previous project. Never carry the input lock into the newly joined world.
                m_clientTerrainRecoveryActive = false;
                m_clientTerrainRecoveryPending = false;
                m_clientTerrainRecoveryRequestInFlight = false;
                m_clientTerrainRecoveryTarget = -1;
                m_clientTerrainRecoveryAcknowledged = -1;
                m_clientTerrainRecoveryReady = -1;
                m_clientTerrainGapDetectedTime = 0.0;
                HideJoinRoomBusyDialog();
                return;
            }

            if (sourceClientId <= 0 ||
                !m_worldTransfersAwaitingReady.TryGetValue(sourceClientId,
                    out int transferId) || transferId != message.TransferId)
                return;
            switch (message.Stage)
            {
                case GamePakWorldReadyStage.LoadingProject:
                    if (!m_pendingAcceptedJoinKeys.TryGetValue(sourceClientId,
                            out string recordKey) ||
                        !m_playerRecords.TryGetValue(recordKey, out NetworkPlayerRecord record))
                    {
                        AbortJoiningClient(sourceClientId,
                            "The approved player profile is unavailable.");
                        return;
                    }
                    CreateNetworkPlayer(sourceClientId, record.Name, recordKey);
                    if (!m_networkPlayerData.ContainsKey(sourceClientId))
                    {
                        AbortJoiningClient(sourceClientId,
                            "The host could not create the network player.");
                        return;
                    }
                    m_pendingAcceptedJoinKeys.Remove(sourceClientId);
                    Log.Information($"[ScMP] Client entered Loading Project: ClientID={sourceClientId}, " +
                        $"Transfer={transferId}");
                    break;

                case GamePakWorldReadyStage.ProjectReady:
                    if (!m_networkPlayerData.ContainsKey(sourceClientId))
                    {
                        AbortJoiningClient(sourceClientId,
                            "The network player was not initialized.");
                        return;
                    }
                    m_outgoingWorldTransfers.Remove(sourceClientId);
                    SendTerrainCatchUp(sourceClientId);
                    SendJoinCatchUpBatch(sourceClientId, transferId);
                    break;

                case GamePakWorldReadyStage.CatchUpBatchApplied:
                    if (!m_networkPlayerData.ContainsKey(sourceClientId))
                    {
                        AbortJoiningClient(sourceClientId,
                            "The network player was lost during join catch-up.");
                        return;
                    }
                    if (!m_joinCatchUpJournals.TryGetValue(sourceClientId,
                            out JoinCatchUpJournal journal))
                        return;
                    if (journal.Messages.Count > 0)
                    {
                        SendJoinCatchUpBatch(sourceClientId, transferId);
                        return;
                    }
                    CompleteJoiningClient(sourceClientId, transferId, journal);
                    break;
            }
        }

        // Source: Mod/Comms/Comms/Peer.cs:Peer.DisconnectPeer
        private void AbortJoiningClient(int sourceClientId, string reason)
        {
            SetServerClientGameTrafficEnabled(sourceClientId, enabled: true);
            m_pendingNetworkPlayers.Remove(sourceClientId);
            m_pendingNetworkPlayerIdentities.Remove(sourceClientId);
            RemoveNetworkPlayer(sourceClientId);
            playerMappingManager.ReleasePlayerIndex(sourceClientId);
            ServerGame game = server?.Games.FirstOrDefault(item => item.GameID == client?.GameID);
            ServerClient remoteClient = game?.Clients.FirstOrDefault(item =>
                item.ClientID == sourceClientId);
            if (remoteClient != null) DisconnectNetworkClient(remoteClient);
            Log.Error($"[ScMP] Aborted joining ClientID {sourceClientId}: {reason}");
        }

        // Source: ScMultiplayer.cs:RecordJoinCatchUpMessage
        private void SendJoinCatchUpBatch(int targetClientId, int transferId)
        {
            FlushJoinCatchUpJournal(targetClientId);
            NetworkMessageSender.SendPakWorldReady(targetClientId,
                new GamePakWorldReadyMessage(transferId,
                    GamePakWorldReadyStage.CatchUpBatchComplete));
        }

        // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.SendDataMessageToAllClients
        private void CompleteJoiningClient(int sourceClientId, int transferId,
            JoinCatchUpJournal journal)
        {
            m_joinCatchUpJournals.Remove(sourceClientId);
            m_worldTransfersAwaitingReady.Remove(sourceClientId);
            NetworkMessageSender.SendPakWorldReady(sourceClientId,
                new GamePakWorldReadyMessage(transferId,
                    GamePakWorldReadyStage.ReadyToPlay));
            SetServerClientGameTrafficEnabled(sourceClientId, enabled: true);
            m_fullWorldObjectsSyncTime = WorldObjectFullSyncInterval;
            Log.Information($"[ScMP] World transfer ready: ClientID={sourceClientId}, " +
                $"Transfer={transferId}, CatchUpRounds={journal.ReplayRound}, " +
                $"CatchUpMessages={journal.TotalMessagesSent}, " +
                $"CatchUpBytes={journal.TotalBytesSent}, Dropped={journal.DroppedMessages}");
        }

        public void HandleGamePakWorldMessage(GamePakWorldMessage msg)
        {
            if (msg == null || (msg.TargetClientId >= 0 && msg.TargetClientId != client.ClientID))
                return;
            if (msg.TransferId > 0 && msg.ChunkCount > 0)
            {
                IncomingWorldTransfer transfer = GetOrCreateIncomingWorldTransfer(
                    msg.TransferId, msg.TargetClientId, msg.ChunkCount, msg.TotalLength);
                if (transfer == null) return;
                if (msg.WorldSha256 == null || msg.WorldSha256.Length != 32)
                {
                    Log.Error($"[ScMP] Invalid world checksum manifest: Transfer={msg.TransferId}");
                    return;
                }
                transfer.Manifest = msg;
                transfer.LastProgressTime = Time.RealTime;
                TryCompleteIncomingWorldTransfer(transfer);
                return;
            }
            if (msg.WorldData == null || msg.WorldData.Length == 0) return;
            m_sessionRandomSeed = msg.RandomSeed;
            m_pendingTerrainSequenceBaseline = msg.TerrainSequenceBaseline;
            m_pendingRandomStates = msg.RandomStates ?? new Dictionary<string, long>();
            m_randomStateAppliedProject = null;
            m_pendingLocalPlayerRecord = new NetworkPlayerRecord
            {
                Name = msg.PlayerName,
                PlayerClass = msg.PlayerClass,
                SkinName = msg.SkinName,
                Position = msg.PlayerPosition,
                Level = msg.PlayerLevel,
                Health = msg.PlayerHealth,
                Air = msg.PlayerAir,
                Food = msg.PlayerFood,
                Stamina = msg.PlayerStamina,
                Sleep = msg.PlayerSleep,
                Temperature = msg.PlayerTemperature,
                TargetTemperature = msg.PlayerTargetTemperature,
                Wetness = msg.PlayerWetness,
                FluDuration = msg.PlayerFluDuration,
                FluOnset = msg.PlayerFluOnset,
                SicknessDuration = msg.PlayerSicknessDuration,
                IsCreativeFlying = msg.PlayerIsCreativeFlying,
                InventoryWasCreative = msg.InventoryWasCreative,
                ActiveSlotIndex = msg.ActiveSlotIndex,
                CreativeCategoryIndex = msg.CreativeCategoryIndex,
                CreativePageIndex = msg.CreativePageIndex,
                SlotValues = msg.SlotValues,
                SlotCounts = msg.SlotCounts,
                Clothes = msg.Clothes
            };
            m_localReplacementPlayerData = null;
            m_localPlayerRecordQueued = false;
            m_localPlayerRecordApplied = false;
            try
            {
                Log.Information($"[ScMP] Importing world: {msg.Name} ({msg.WorldData.Length} bytes)");
                // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ImportWorld
                var existingDirectories = new HashSet<string>(WorldsManager.WorldInfos.Select(world => world.DirectoryName));
                string importedDirectory = WorldsManager.ImportWorld(new MemoryStream(msg.WorldData));
                m_downloadedWorldDirectory = importedDirectory;
                RegisterDownloadedWorld(importedDirectory);
                WorldsManager.UpdateWorldsList();

                WorldInfo importedWorld = WorldsManager.WorldInfos.FirstOrDefault(world =>
                    world.DirectoryName == importedDirectory);
                if (importedWorld == null)
                    importedWorld = WorldsManager.WorldInfos.FirstOrDefault(world =>
                        !existingDirectories.Contains(world.DirectoryName) &&
                        world.WorldSettings.Name == msg.Name);
                if (importedWorld == null)
                    importedWorld = WorldsManager.WorldInfos.FirstOrDefault(world =>
                        world.WorldSettings.Name == msg.Name && world.LastSaveTime == msg.LastSaveTime);
                if (importedWorld != null)
                {
                    if (m_joinRoomBusyDialog != null)
                        m_joinRoomBusyDialog.SmallMessage =
                            "Connected.\r\nWorld imported.\r\nLoading project...";
                    SuPlayScreen.Play(importedWorld);
                    connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Playing);
                    m_shouldCreateHostAvatar = true;
                    m_pendingNetworkPlayers[0] = "Host";
                    m_pendingNetworkPlayerIdentities[0] = GetNetworkRecordKey(0);
                    // Source: Survivalcraft/Game/GameManager.cs:GameManager.Project
                    // Play() starts loading asynchronously. Acknowledging here lets catch-up
                    // messages arrive before the imported Project exists, so defer Ready until
                    // UpdateWorldSubsystem observes the initialized Project.
                    if (msg.TransferId > 0)
                    {
                        m_pendingWorldReadyTransferId = msg.TransferId;
                        NetworkMessageSender.SendPakWorldReady(
                            new GamePakWorldReadyMessage(msg.TransferId,
                                GamePakWorldReadyStage.LoadingProject));
                    }
                    Log.Information($"[ScMP] World imported, entering game: {importedWorld.DirectoryName}");
                    return;
                }
                Log.Error($"[ScMP] World imported but not found in world list: {msg.Name}");
                m_isLoadingDownloadedWorld = false;
                HideJoinRoomBusyDialog();
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Join Room", "The downloaded host world could not be opened.",
                    "OK", null, null));
            }
            catch (Exception ex)
            {
                m_isLoadingDownloadedWorld = false;
                HideJoinRoomBusyDialog();
                Log.Error($"[ScMP] Failed to import world: {ex.Message}");
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Join Room", "Failed to load the host world: " + ex.Message,
                    "OK", null, null));
            }
        }

        // ====================================================================
        // Client 事件回调
        // ====================================================================
        private void Client_GameCreated(GameCreatedData obj)
        {
            m_pendingLocalCreateDescription = null;
            m_pendingLocalCreateAddress = null;
            m_localCreateAttempts = 0;
            Log.Information($"[ScMP] GameCreated, ClientID={client.ClientID}, Creator={obj.CreatorAddress}");
            IsHost = true;
            m_localLeaveInProgress = false;
            m_shouldCreateHostAvatar = false;
            ResetTransientNetworkState();
            m_sessionRandomSeed = Guid.NewGuid().GetHashCode();
            if (m_sessionRandomSeed == 0) m_sessionRandomSeed = 1;
            playerMappingManager.AssignPlayerIndex(client.ClientID);
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Playing);
            Dispatcher.Dispatch(() => FinishCreateRoomFeedback(true,
                $"Room created (ID {client.GameID})."));
        }

        private void FinishCreateRoomFeedback(bool success, string message)
        {
            bool wasPending = m_createRoomPending;
            m_createRoomPending = false;
            if (success)
                return;
            if (!success)
            {
                if (wasPending)
                    DialogsManager.ShowDialog(null, new MessageDialog(
                        "Create Room", message ?? "Room creation failed.", "OK", null, null));
                return;
            }
            if (GameManager.Project == null) return;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            localPlayer?.ComponentGui.DisplaySmallMessage(
                message, Color.Green, blinking: true, playNotificationSound: true);
        }

        private void Client_GameJoined(GameJoinedData obj)
        {
            bool wasReconnect = m_reconnectPending;
            Log.Information($"[ScMP] GameJoined, Step={obj.Step}, ClientID={client.ClientID}");
            IsHost = false;
            m_hostDisconnectHandled = false;
            m_localLeaveInProgress = false;
            m_isLoadingDownloadedWorld = true;
            ResetTransientNetworkState();
            m_reconnectRequested = false;
            m_reconnectPending = false;
            m_reconnectAttempts = 0;
            m_nextReconnectAttemptTime = 0.0;
            m_pendingJoinRequest = null;
            playerMappingManager.AssignPlayerIndex(client.ClientID);
            downloadSM.TransitionTo(WorldDownloadStateMachine.DownloadState.Requesting);
            Dispatcher.Dispatch(() =>
            {
                if (m_joinRoomBusyDialog != null)
                    m_joinRoomBusyDialog.SmallMessage = "Connected. Downloading the host world...";
            });
            if (wasReconnect)
                Log.Information("[ScMP] Host reconnect succeeded; refreshing authoritative world state");
        }

        public static string GetLocalPlayerName()
        {
            string name = UserManager.ActiveUser?.DisplayName;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            PlayerData player = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false)?.PlayersData.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(player?.Name) ? player.Name : "Player";
        }

        // Source: Survivalcraft/Game/UserManager.cs:UserManager.UserManager
        // UniqueId is generated once and persisted in data:/UserId.dat on each installation.
        public static string GetLocalPlayerIdentity()
        {
            return UserManager.ActiveUser?.UniqueId ?? string.Empty;
        }

        // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:ResolveHostNames
        public static string GetServiceDiscoveryHost(IPEndPoint endpoint)
        {
            return currentInstance?.m_remoteServerDirectory?.GetHostName(endpoint);
        }

        private void Client_GameDescriptionRequest(GameDescriptionRequestData obj)
        {
            // Source: Comms.Drt Explorer queries server → server fires this on client
            // Client must respond via SendGameDescription for game to appear in DiscoveredServers
            if (LastGameDescription != null && LastGameDescription.Length > 0)
            {
                try
                {
                    client.SendGameDescription(LastGameDescription);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] SendGameDescription failed: {ex.Message}");
                }
            }
        }

        private void Client_ConnectRefused(ConnectRefusedData obj)
        {
            Log.Information($"[ScMP] Connect refused: {obj.Reason}");
            if (m_reconnectPending && obj.Reason != PlayerProfileRequiredReason)
            {
                m_nextReconnectAttemptTime = Math.Max(m_nextReconnectAttemptTime,
                    Time.RealTime + ReconnectInitialDelay);
                Log.Information("[ScMP] Reconnect was refused; another attempt remains scheduled");
                return;
            }
            if (m_reconnectPending && obj.Reason == PlayerProfileRequiredReason)
                m_reconnectPending = false;
            m_isLoadingDownloadedWorld = false;
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Disconnected);
            Dispatcher.Dispatch(() => FinishCreateRoomFeedback(false, obj.Reason));
            if (obj.Reason == PlayerProfileRequiredReason && m_pendingJoinRequest != null)
            {
                Dispatcher.Dispatch(() =>
                {
                    HideJoinRoomBusyDialog();
                    ScreensManager.SwitchScreen(
                        "ScMultiplayerPlayer",
                        new Action<string, PlayerClass, string>((name, playerClass, skinName) =>
                            SubmitPendingJoin(name, playerClass, skinName, hasPlayerProfile: true)));
                });
                return;
            }
            Dispatcher.Dispatch(() =>
            {
                HideJoinRoomBusyDialog();
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Join Room", obj.Reason ?? "The host refused the connection.",
                    "OK", null, null));
            });
            m_pendingJoinRequest = null;
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.ConnectTimedOut
        private void Client_ConnectTimedOut(ConnectTimedOutData obj)
        {
            string address = obj.Address?.ToString() ?? "host";
            if (m_reconnectPending)
            {
                m_nextReconnectAttemptTime = Math.Max(
                    m_nextReconnectAttemptTime, Time.RealTime + ReconnectInitialDelay);
                Log.Information($"[ScMP] Reconnect attempt to {address} timed out; retry remains scheduled");
                return;
            }

            Log.Error($"[ScMP] Join request to {address} timed out");
            m_isLoadingDownloadedWorld = false;
            m_pendingJoinRequest = null;
            m_activeJoinRequest = null;
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Disconnected);
            Dispatcher.Dispatch(() =>
            {
                HideJoinRoomBusyDialog();
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Join Room", "The host did not complete the join request in time.",
                    "OK", null, null));
            });
        }

        private void Client_GameStateRequest(GameStateRequestData obj)
        {
            client.SendState(client.Step,
                Message.WriteWithSender(new ChatMessage("StateSync", string.Empty, "OK"), client.Address));
        }

        // Source: Mod/Comms/Comms/UdpTransmitter.cs:UdpTransmitter.TaskFunction
        // Android can briefly rebuild its Wi-Fi route while the UDP socket and game process remain
        // usable. Do not turn that transient transport error into an immediate world exit.
        private static bool IsTransientNetworkSocketError(Exception error)
        {
            if (!(error is SocketException socketError)) return false;
            switch (socketError.SocketErrorCode)
            {
                case SocketError.NetworkDown:
                case SocketError.NetworkUnreachable:
                case SocketError.NetworkReset:
                case SocketError.HostDown:
                case SocketError.HostUnreachable:
                case SocketError.ConnectionAborted:
                case SocketError.ConnectionReset:
                    return true;
                default:
                    return false;
            }
        }

        private void Client_Error(Exception obj)
        {
            Log.Error($"[ScMP] Client error: {obj.Message}");
            // Source: Comms/Peer.cs:Peer.CheckKeepAlives
            // Error is raised before all public connection state is guaranteed to be updated.
            bool activeClientSession = !IsHost && !m_localLeaveInProgress &&
                (m_isLoadingDownloadedWorld || !string.IsNullOrEmpty(m_downloadedWorldDirectory) ||
                    m_shouldCreateHostAvatar);
            if (activeClientSession && IsTransientNetworkSocketError(obj))
            {
                Log.Warning($"[ScMP] Transient client network error; waiting for transport recovery: " +
                    obj.Message);
                return;
            }
            if (activeClientSession && obj is KeepAliveTimeoutException &&
                m_activeJoinRequest?.WorldInfo != null)
            {
                m_reconnectRequested = true;
                return;
            }
            Dispatcher.Dispatch(() =>
            {
                HideJoinRoomBusyDialog();
                FinishCreateRoomFeedback(false, obj.Message);
            });
            if (activeClientSession) HandleHostDisconnected();
        }

        private void HandleHostDisconnected()
        {
            if (IsHost || m_hostDisconnectHandled) return;
            m_hostDisconnectHandled = true;
            m_reconnectRequested = false;
            m_reconnectPending = false;
            m_activeJoinRequest = null;
            m_isLoadingDownloadedWorld = false;
            // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.LeaveGame
            // A host LeaveRequest can arrive while the transport is still healthy. Explicitly
            // leave so the local server does not retain this client after its world is removed.
            if (client?.IsConnected == true)
            {
                try { client.LeaveGame(); }
                catch (Exception ex) { Log.Error($"[ScMP] Failed to leave disconnected host: {ex.Message}"); }
            }
            QueueEndOfFrameAction(delegate
            {
                string downloadedDirectory = m_downloadedWorldDirectory;
                if (GameManager.Project != null)
                    GameManager.DisposeProject();
                if (!string.IsNullOrEmpty(downloadedDirectory))
                {
                    WorldsManager.DeleteWorld(downloadedDirectory);
                    WorldsManager.UpdateWorldsList();
                    m_downloadedWorldDirectory = null;
                }
                m_networkPlayerData.Clear();
                m_pendingNetworkPlayers.Clear();
                RemotePlayers.Clear();
                ScreensManager.SwitchScreen("Play");
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Host Disconnected", "The host left the room. The downloaded world was removed.",
                    "OK", "Cancel", null));
            });
        }

        public void NotifyPlayerComponentDisposing(PlayerData playerData)
        {
            if (playerData == null || IsHost || m_hostDisconnectHandled || m_localLeaveInProgress ||
                m_replacingLocalPlayerData) return;
            if (m_networkPlayerData.Values.Contains(playerData)) return;
            // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead
            // Death disposes only the local player Entity while the network Project remains alive.
            // ProjectDisposed is the sole authoritative signal for leaving a room.
            if (GameManager.Project != null) return;

            BeginLocalGameLeave();
            ResetTransientNetworkState();
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.DisposeProject
        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.LeaveGame
        private void BeginLocalGameLeave()
        {
            if (m_localLeaveInProgress || client?.IsConnected != true) return;
            m_localLeaveInProgress = true;
            m_reconnectRequested = false;
            m_reconnectPending = false;
            m_reconnectAttempts = 0;
            m_activeJoinRequest = null;
            m_shouldCreateHostAvatar = false;
            m_isLoadingDownloadedWorld = false;
            try
            {
                // Application-level notice removes the avatar before transport timeout. The peer
                // disconnect immediately afterwards remains the protocol-level fallback.
                NetworkMessageSender.BroadcastPlayerLeave(new PlayerActionMessage(
                    PlayerActionType.LeaveRequest, client.ClientID, 0, default));
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Failed to broadcast leave: {ex.Message}");
            }
            try { client.LeaveGame(); }
            catch (Exception ex) { Log.Error($"[ScMP] Failed to leave game: {ex.Message}"); }
        }

        private void ResetTransientNetworkState()
        {
            Interlocked.Increment(ref m_worldTransferGeneration);
            while (m_worldTransferSendQueue.TryDequeue(out _))
                Interlocked.Decrement(ref m_worldTransferQueuedWorkCount);
            m_networkPlayerData.Clear();
            lock (m_creatingNetworkPlayers) m_creatingNetworkPlayers.Clear();
            m_pendingNetworkPlayers.Clear();
            m_pendingNetworkPlayerIdentities.Clear();
            m_networkPlayerInputs.Clear();
            m_departedRemoteClientIds.Clear();
            m_clientRecordKeys.Clear();
            m_recentChatMessages.Clear();
            m_recentChatMessageIds.Clear();
            m_hostJoinRequests.Clear();
            if (m_activeJoinDecisionDialog != null &&
                DialogsManager.Dialogs.Contains(m_activeJoinDecisionDialog))
            {
                DialogsManager.HideDialog(m_activeJoinDecisionDialog);
            }
            m_activeJoinDecisionDialog = null;
            m_activeJoinDecisionClientId = -1;
            m_playerHealthCache.Clear();
            m_lastSentInventoryValues.Clear();
            m_lastSentInventoryCounts.Clear();
            m_equipmentAuthorityRevisions.Clear();
            m_lastClientEquipmentRevisions.Clear();
            m_lastReceivedEquipmentRevisions.Clear();
            m_lastEquipmentSnapshots.Clear();
            m_equipmentSynchronizedClients.Clear();
            m_localEquipmentRevision = 0;
            m_hostKnockbackHealthCache.Clear();
            m_hostPainSoundTimes.Clear();
            m_hostRemoteKnockbackUntil.Clear();
            RemotePlayers.Clear();
            m_hostAnimalIds.Clear();
            m_hostAnimals.Clear();
            m_hostAnimalSync.Clear();
            m_remoteAnimals.Clear();
            m_remoteAnimalTemplates.Clear();
            m_remoteAnimalSync.Clear();
            m_loggedRemoteAnimalFailures.Clear();
            m_lastFullAnimalSnapshotTick = 0;
            m_hostPickableIds.Clear();
            m_remotePickables.Clear();
            m_remotePickableRecords.Clear();
            m_remotePickableStates.Clear();
            m_pendingPickablePickups.Clear();
            m_applyingNetworkPickable = false;
            m_lastAuthoritativeLocalInventoryTick = 0;
            m_lastLocalInventoryValues = Array.Empty<int>();
            m_lastLocalInventoryCounts = Array.Empty<int>();
            m_pendingLocalDropValue = 0;
            m_pendingLocalDropCount = 0;
            m_pendingLocalDropPosition = Vector3.Zero;
            m_pendingLocalDropPredictionUntil = 0.0;
            m_hostProjectileIds.Clear();
            m_remoteProjectiles.Clear();
            m_clientPredictedProjectiles.Clear();
            m_displayedProjectileHits.Clear();
            m_nextProjectileId = 1;
            lock (m_terrainJournalLock)
            {
                m_hostTerrainJournal.Clear();
                m_terrainCheckpoint.Clear();
                m_pendingTerrainChanges.Clear();
                m_terrainRepairRepeats.Clear();
                m_hostTerrainSequence = 0;
            }
            m_hostTerrainRecoveryTargets.Clear();
            m_pendingTerrainSequenceBaseline = 0;
            m_clientTerrainRecoveryActive = false;
            m_clientTerrainRecoveryPending = false;
            m_clientTerrainRecoveryRequestInFlight = false;
            m_clientSuspensionRequested = false;
            m_clientTerrainRecoveryTarget = -1;
            m_clientTerrainRecoveryAcknowledged = -1;
            m_clientTerrainRecoveryReady = -1;
            m_clientTerrainGapDetectedTime = 0.0;
            m_clientGameplayScreenObserved = false;
            m_wasClientGameScreenActive = false;
            m_clientWindowDeactivated = false;
            m_pendingTerrainPredictions.Clear();
            m_pendingTerrainPredictionCells.Clear();
            m_processedTerrainDigRequests.Clear();
            m_localTerrainDigIntents.Clear();
            m_outgoingWorldTransfers.Clear();
            m_worldTransfersAwaitingReady.Clear();
            m_incomingWorldTransfers.Clear();
            m_hostPlayerPokingPhases.Clear();
            m_hostPlayerPokeSequences.Clear();
            m_playerWhistleSequences.Clear();
            m_localDigTarget = null;
            m_nextTerrainDigRequestId = 0;
            m_localHitSequence = 0;
            m_nextLocalHitRequestTime = 0.0;
            m_localInteractSequence = 0;
            m_nextLocalInteractRequestTime = 0.0;
            m_localDropSequence = 0;
            m_observedLocalPlayerEntity = null;
            m_observedLocalPlayerWasDead = false;
            m_localRespawnSequence = 0;
            m_localRespawnPendingUntil = 0.0;
            m_nextWorldTransferId = 0;
            m_worldTransferCursor = 0;
            m_nextWorldTransferManifestRequestTime = 0.0;
            m_nextWorldTransferUiUpdateTime = 0.0;
            m_terrainMergeTime = 0f;
            SuSubsystemTerrain.ResetNetworkState();
            m_sessionRandomSeed = 0;
            m_pendingRandomStates.Clear();
            m_randomStateAppliedProject = null;
            m_nextAnimalId = 1;
            m_nextPickableId = 1;
            m_fullWorldObjectsSyncTime = 0f;
            m_fullAnimalSyncTime = 0f;
            m_runawayCreatureCleanup.Clear();
            m_runawayCreatureCleanupProject = null;
            m_nextRunawayCreatureCheckTime = 0.0;
            m_nextRemoteCreatureSpawnTime = 0.0;
            m_remoteCreatureSpawnCursor = 0;
            m_syncPulseAccumulator = 0f;
            m_lastSyncUpdateTime = 0.0;
            m_syncPulseIndex = 0;
            m_playerProfileSyncTime = 0f;
            m_inventoryKeyframeTime = 0f;
            m_playerRecordSaveTime = 0f;
            m_localPlayerInput = default;
            m_localInputBodyPosition = Vector3.Zero;
            m_localInputBodyVelocity = Vector3.Zero;
            m_localInputBodyRotation = Quaternion.Identity;
            m_localInputLookAngles = Vector2.Zero;
            m_localInputSequence = 0;
            m_lastSentInputSequence = -1;
            m_localInputResendsRemaining = 0;
            m_localAimActive = false;
            m_localAimSequence = 0;
            m_localAimSlot = -1;
            m_localAimItemValue = 0;
            m_localAimItemCount = 0;
            m_lastAimUpdateSentTime = 0.0;
            m_smoothedNetworkDelay = 0f;
            m_pendingLocalKnockbackVelocity = Vector3.Zero;
            m_pendingLocalKnockbackUntil = 0.0;
            m_clientWorldObjectsProject = null;
            m_remoteWeatherState = null;
            m_remoteLightningActive = false;
            m_hostLightningActive = false;
            m_localLightningPredictionUntil = 0.0;
            m_pendingLocalPlayerRecord = null;
            m_localReplacementPlayerData = null;
            m_localPlayerRecordQueued = false;
            m_localPlayerRecordApplied = false;
            if (!IsHost)
            {
                m_playerRecords.Clear();
                m_playerRecordsWorldDirectory = null;
                m_playerRecordsDirty = false;
            }
            playerMappingManager.Reset();
        }

        // Source: Engine/Random.cs:Random.State
        // Subsystem random state is captured at join time. Component-level AI is still governed
        // by host-authoritative animal synchronization and is intentionally not reseeded here.
        private Dictionary<string, long> CaptureSubsystemRandomStates()
        {
            var states = new Dictionary<string, long>(StringComparer.Ordinal);
            Project project = GameManager.Project;
            if (project == null) return states;
            foreach (Subsystem subsystem in project.Subsystems)
            {
                foreach (FieldInfo field in GetSubsystemRandomFields(subsystem.GetType()))
                {
                    Engine.Random random = ModManager.ModParentField.GetParentField<Engine.Random>(
                        subsystem, field.Name, field.DeclaringType);
                    if (random != null)
                        states[GetRandomFieldKey(field)] = unchecked((long)random.State);
                }
            }
            return states;
        }

        private void ApplyHostRandomStates(Project project)
        {
            if (project == null || IsHost || m_sessionRandomSeed == 0 ||
                ReferenceEquals(m_randomStateAppliedProject, project))
                return;
            foreach (Subsystem subsystem in project.Subsystems)
            {
                foreach (FieldInfo field in GetSubsystemRandomFields(subsystem.GetType()))
                {
                    var random = new Engine.Random(DeriveRandomSeed(m_sessionRandomSeed, GetRandomFieldKey(field)));
                    if (m_pendingRandomStates.TryGetValue(GetRandomFieldKey(field), out long state))
                        random.State = unchecked((ulong)state);
                    ModManager.ModParentField.ModifyParentField(
                        subsystem, field.Name, random, field.DeclaringType);
                }
            }
            m_randomStateAppliedProject = project;
        }

        private static IEnumerable<FieldInfo> GetSubsystemRandomFields(Type type)
        {
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!field.IsInitOnly && field.FieldType == typeof(Engine.Random))
                        yield return field;
                }
            }
        }

        private static string GetRandomFieldKey(FieldInfo field) =>
            field.DeclaringType.FullName + "|" + field.Name;

        private static int DeriveRandomSeed(int seed, string key)
        {
            int hash = seed;
            foreach (char c in key)
                hash = unchecked(hash * 31 + c);
            return hash;
        }

        private void EnsureNetworkComponentPlayers()
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || m_networkPlayerData.Count == 0) return;
            List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(
                players, "m_componentPlayers", typeof(SubsystemPlayers));
            foreach (PlayerData playerData in m_networkPlayerData.Values)
            {
                if (playerData.ComponentPlayer != null && !componentPlayers.Contains(playerData.ComponentPlayer))
                    componentPlayers.Add(playerData.ComponentPlayer);
            }
        }

        private void HandleProjectDisposed(Project project)
        {
            // Source: Survivalcraft/Game/GameManager.cs:GameManager.DisposeProject
            // ProjectDisposed fires before Project.Dispose, so this covers the game-menu Quit path
            // even when component disposal callbacks have not run yet.
            if (ReferenceEquals(m_hostPickablesSubsystem,
                project?.FindSubsystem<SubsystemPickables>(false)))
                DetachHostPickableEvents();
            if (!m_hostDisconnectHandled)
                BeginLocalGameLeave();
            foreach (int clientId in m_networkPlayerData
                .Where(pair => pair.Value.ComponentPlayer?.Entity.Project == project)
                .Select(pair => pair.Key).ToArray())
            {
                RemoveNetworkPlayer(clientId);
            }
            ResetTransientNetworkState();
        }

        private static HashSet<string> ReadDownloadedWorldRegistry()
        {
            if (!Storage.FileExists(DownloadedWorldsRegistryPath))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(Storage.ReadAllText(DownloadedWorldsRegistryPath)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void WriteDownloadedWorldRegistry(HashSet<string> directories)
        {
            if (directories.Count == 0)
            {
                if (Storage.FileExists(DownloadedWorldsRegistryPath))
                    Storage.DeleteFile(DownloadedWorldsRegistryPath);
                return;
            }
            Storage.WriteAllText(DownloadedWorldsRegistryPath, string.Join("\r\n", directories));
        }

        private static void RegisterDownloadedWorld(string directoryName)
        {
            HashSet<string> directories = ReadDownloadedWorldRegistry();
            directories.Add(directoryName);
            WriteDownloadedWorldRegistry(directories);
        }

        private void CleanupDownloadedWorldsIfIdle()
        {
            if (GameManager.Project != null || m_isLoadingDownloadedWorld) return;
            HashSet<string> directories = ReadDownloadedWorldRegistry();
            if (directories.Count == 0) return;
            var failedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directoryName in directories)
            {
                try
                {
                    WorldsManager.DeleteWorld(directoryName);
                    if (string.Equals(m_downloadedWorldDirectory, directoryName, StringComparison.OrdinalIgnoreCase))
                        m_downloadedWorldDirectory = null;
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] Failed to delete downloaded world {directoryName}: {ex.Message}");
                    failedDirectories.Add(directoryName);
                    continue;
                }
            }
            WriteDownloadedWorldRegistry(failedDirectories);
            WorldsManager.UpdateWorldsList();
        }

        private void Server_Information(string obj)
        {
            Log.Information($"[Server] {obj}");
        }

        // Source: Comms/Drt/GameStepData.Inputs
        // Remote host/peer avatars are presentation replicas. Extrapolate the last authoritative
        // snapshot to the current server step, then interpolate the visible body toward it.
        private void UpdateRemotePlayerPresentations(float dt)
        {
            foreach (KeyValuePair<int, NetworkPlayerState> item in RemotePlayers.ToArray())
            {
                NetworkPlayerState state = item.Value;
                if (state == null || Time.RealTime - state.LastUpdateTime > RemotePresentationStaleTime ||
                    !m_networkPlayerData.TryGetValue(item.Key, out PlayerData playerData) ||
                    playerData.ComponentPlayer?.ComponentBody == null)
                    continue;
                ComponentBody body = playerData.ComponentPlayer.ComponentBody;
                ComponentCreatureModel model = playerData.ComponentPlayer.ComponentCreatureModel;
                ComponentLocomotion locomotion = playerData.ComponentPlayer.ComponentLocomotion;
                // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.NormalMovement
                // Creative flight disables gravity only while a movement order is present. A remote
                // presentation has no local PlayerInput, so preserve the authoritative flight mode
                // explicitly instead of letting the replica fall between snapshots.
                body.IsGravityEnabled = !state.IsFlying;
                body.IsGroundDragEnabled = !state.IsFlying;
                if (locomotion != null)
                {
                    locomotion.IsCreativeFlyEnabled = state.IsFlying;
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "m_lookAngles", state.LookAngles,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "<LastWalkOrder>k__BackingField", state.WalkOrder,
                        typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "<LastJumpOrder>k__BackingField", state.JumpOrder,
                        typeof(ComponentLocomotion));
                }
                if (model != null)
                {
                    // Source: ComponentHumanModel.cs:ComponentHumanModel.Animate
                    // Orders are consumed and reset every animation frame, so replay the latest
                    // authoritative presentation state until the next snapshot changes it.
                    model.AttackOrder = state.AttackOrder;
                    model.RowLeftOrder = state.RowLeftOrder;
                    model.RowRightOrder = state.RowRightOrder;
                    model.InHandItemOffsetOrder = state.ItemOffset;
                    model.InHandItemRotationOrder = state.ItemRotation;
                    model.AimHandAngleOrder = state.AimHandAngle;
                }
                // Source: Comms.Drt/Func/Client/Client.cs:GetStepWaitTime
                // ZeroTier can briefly exceed the network tick interval by an order of magnitude.
                // Keep using a bounded latest-state prediction instead of freezing the avatar.
                float delaySample = MathUtils.Clamp(
                    (client.Step - state.ServerTick) * ServerTickDuration, 0f,
                    RemoteDelaySampleLimit);
                state.EstimatedDelay = state.EstimatedDelay <= 0f
                    ? delaySample
                    : MathUtils.Lerp(state.EstimatedDelay, delaySample, 0.12f);
                float extrapolationTime = MathUtils.Min(state.EstimatedDelay,
                    RemoteExtrapolationLimit);
                Vector3 targetPosition = state.Position;
                if (!state.IsFlying)
                {
                    targetPosition.X += state.Velocity.X * extrapolationTime;
                    targetPosition.Z += state.Velocity.Z * extrapolationTime;
                }
                if (!state.IsGrounded && !state.IsFlying)
                {
                    targetPosition.Y += state.Velocity.Y * extrapolationTime -
                        4.9f * extrapolationTime * extrapolationTime;
                }
                float errorSquared = Vector3.DistanceSquared(body.Position, targetPosition);
                if (!state.PresentationInitialized || errorSquared > 64f)
                {
                    // Snap only to an authoritative sampled position. An extrapolated descending
                    // point can already be below terrain before the next grounded packet arrives.
                    body.Position = state.Position;
                    body.Rotation = state.Rotation;
                    body.Velocity = state.Velocity;
                    state.PresentationInitialized = true;
                }
                else
                {
                    if (state.IsFlying)
                    {
                        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.CreativeFly
                        // A flying replica has no local input and must not integrate the previous
                        // snapshot velocity beyond its last authoritative position. Smooth the
                        // sampled positions directly and leave movement animation to WalkOrder.
                        float positionBlend = 1f - MathUtils.Pow(
                            0.001f, MathUtils.Min(dt, 0.1f));
                        body.Position = Vector3.Lerp(body.Position,
                            state.Position, positionBlend);
                        body.Velocity = Vector3.Zero;
                        body.Rotation = Quaternion.Slerp(
                            body.Rotation, state.Rotation, positionBlend);
                        continue;
                    }
                    float delayFactor = MathUtils.Saturate(state.EstimatedDelay / 0.2f);
                    Vector3 error = targetPosition - body.Position;
                    float deadZone = state.IsGrounded ? 0.4f : 0.65f;
                    Vector3 targetVelocity = state.Velocity;
                    if (state.IsGrounded) targetVelocity.Y = 0f;
                    Vector3 desiredVelocity;
                    float blend;
                    if (error.LengthSquared() <= deadZone * deadZone)
                    {
                        desiredVelocity = targetVelocity;
                        blend = 0.45f;
                    }
                    else
                    {
                        float horizon = MathUtils.Lerp(0.35f, 0.2f, delayFactor);
                        Vector3 catchUpVelocity = error / horizon;
                        float maxExtraSpeed = MathUtils.Lerp(3f, 8f, delayFactor);
                        float extraSpeed = catchUpVelocity.Length();
                        if (extraSpeed > maxExtraSpeed)
                            catchUpVelocity *= maxExtraSpeed / extraSpeed;
                        desiredVelocity = targetVelocity + catchUpVelocity;
                        blend = MathUtils.Lerp(0.2f, 0.35f, delayFactor);
                    }
                    body.Velocity = Vector3.Lerp(body.Velocity, desiredVelocity, blend);
                    body.Rotation = Quaternion.Slerp(body.Rotation, state.Rotation, 0.4f);
                }
            }
        }

        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.NormalMovement
        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        // Capture ApplyImpulse before remote-follow correction can replace it with the client's
        // pre-hit velocity. StunTime also covers armored/invulnerable hits with zero health loss.
        private void CaptureHostRemoteKnockbacks()
        {
            double now = Time.RealTime;
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key <= 0 || remote.Value?.ComponentPlayer == null) continue;
                ComponentPlayer player = remote.Value.ComponentPlayer;
                ComponentHealth health = player.ComponentHealth;
                ComponentBody body = player.ComponentBody;
                if (health == null || body == null) continue;
                if (!m_hostKnockbackHealthCache.TryGetValue(remote.Key, out float previousHealth))
                {
                    m_hostKnockbackHealthCache[remote.Key] = health.Health;
                    continue;
                }

                bool healthDecreased = health.Health < previousHealth - 0.0001f;
                bool alreadyHeld = m_hostRemoteKnockbackUntil.TryGetValue(
                    remote.Key, out double heldUntil) && heldUntil > now;
                bool attackStun = player.ComponentLocomotion?.StunTime > 0f;
                if ((healthDecreased || (attackStun && !alreadyHeld)) &&
                    body.Velocity.LengthSquared() > 0.0001f)
                {
                    float lastSentHealth = m_playerHealthCache.TryGetValue(
                        remote.Key, out float cachedHealth) ? cachedHealth : previousHealth;
                    m_hostRemoteKnockbackUntil[remote.Key] = now + LocalKnockbackHoldDuration;
                    NetworkMessageSender.SendPlayerHealthMessage(
                        remote.Key, player, health.Health - lastSentHealth,
                        hasKnockback: true);
                    m_playerHealthCache[remote.Key] = health.Health;
                }
                m_hostKnockbackHealthCache[remote.Key] = health.Health;
            }
            foreach (int clientId in m_hostKnockbackHealthCache.Keys.Where(
                id => !m_networkPlayerData.ContainsKey(id)).ToArray())
            {
                m_hostKnockbackHealthCache.Remove(clientId);
                m_hostRemoteKnockbackUntil.Remove(clientId);
            }
        }

        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.NormalMovement
        // Remote host avatars keep original locomotion, then receive a bounded velocity addition
        // that makes them converge on the client's continuous A-B-C trajectory without teleporting.
        private void ApplyHostRemoteFollowVelocities()
        {
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key <= 0 || remote.Value?.ComponentPlayer == null ||
                    !m_networkPlayerInputs.TryGetValue(remote.Key, out NetworkPlayerInputState state) ||
                    Time.RealTime - state.LastReceivedTime > RemoteInputHoldDuration)
                    continue;
                if (m_hostRemoteKnockbackUntil.TryGetValue(remote.Key, out double knockbackUntil))
                {
                    if (Time.RealTime < knockbackUntil) continue;
                    m_hostRemoteKnockbackUntil.Remove(remote.Key);
                }
                ComponentPlayer player = remote.Value.ComponentPlayer;
                ComponentBody body = player.ComponentBody;
                ComponentLocomotion locomotion = player.ComponentLocomotion;
                float delay = MathUtils.Clamp(
                    (client.Step - state.ClientTick) * ServerTickDuration, 0f,
                    RemoteDelaySampleLimit);
                Vector3 targetPosition = state.BodyPosition + state.BodyVelocity * delay;
                Vector3 error = targetPosition - body.Position;
                if (!locomotion.IsCreativeFlyEnabled && body.StandingOnValue.HasValue &&
                    Math.Abs(error.Y) < 0.75f)
                    error.Y = 0f;
                float trackingRadius = locomotion.IsCreativeFlyEnabled ? 32f : 16f;
                float errorLength = error.Length();
                if (errorLength > trackingRadius)
                    error *= trackingRadius / errorLength;

                bool isInteracting = state.Input.Dig.HasValue || state.Input.Hit.HasValue ||
                    state.Input.Interact.HasValue || state.Input.Aim.HasValue;
                float deadZone = locomotion.IsCreativeFlyEnabled ? 0.35f : 0.15f;
                Vector3 desiredVelocity;
                float blend;
                if (error.LengthSquared() <= deadZone * deadZone)
                {
                    // Once close enough, stop chasing position and match velocity. This brakes
                    // the extra catch-up speed before it crosses the target and reverses direction.
                    desiredVelocity = state.BodyVelocity;
                    blend = 0.45f;
                }
                else
                {
                    float delayFactor = MathUtils.Saturate(delay / 0.2f);
                    float horizon = MathUtils.Lerp(0.45f, 0.22f, delayFactor);
                    Vector3 catchUpVelocity = error / horizon;
                    float maxExtraSpeed = locomotion.IsCreativeFlyEnabled
                        ? MathUtils.Lerp(4f, 10f, delayFactor)
                        : MathUtils.Lerp(2f, 6f, delayFactor);
                    float extraSpeed = catchUpVelocity.Length();
                    if (extraSpeed > maxExtraSpeed)
                        catchUpVelocity *= maxExtraSpeed / extraSpeed;
                    desiredVelocity = state.BodyVelocity + catchUpVelocity;
                    blend = MathUtils.Lerp(0.14f, 0.28f, delayFactor);
                    if (isInteracting) blend = MathUtils.Max(blend, 0.35f);
                }
                body.Velocity = Vector3.Lerp(body.Velocity, desiredVelocity, blend);
            }
        }

        public void CaptureLocalPlayerInput(ComponentPlayer player, PlayerInput playerInput)
        {
            if (IsHost || client?.IsConnected != true || player == null ||
                m_networkPlayerData.Values.Contains(player.PlayerData))
                return;
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            // Touch hold supplies both Dig and Aim. The local player suppresses Dig when the held
            // item is aimable; preserve that resolved intent for the host-side replica as well.
            IInventory inventory = player.ComponentMiner?.Inventory;
            int activeSlot = inventory?.ActiveSlotIndex ?? -1;
            UpdateLocalAimLifecycle(player, playerInput, inventory, activeSlot);
            if (playerInput.Aim.HasValue && inventory != null && activeSlot >= 0 &&
                activeSlot < inventory.SlotsCount)
            {
                int value = inventory.GetSlotValue(activeSlot);
                Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
                if (block.IsAimable) playerInput.Dig = null;
            }
            UpdateLocalDigTarget(player, playerInput.Dig);
            UpdateLocalHitRequests(playerInput.Hit);
            UpdateLocalInteractRequests(player, playerInput.Interact);
            UpdateLocalDropRequests(player, playerInput.Drop);
            // Aim uses its own edge-preserving lifecycle. Dig completion is authoritative through
            // TerrainDigRequestMessage; PokingPhase is synchronized separately for presentation.
            // Interact and Drop use reliable edges and must not execute again through snapshots.
            playerInput.Aim = null;
            playerInput.Drop = false;
            m_localPlayerInput = SanitizeNetworkPlayerInput(playerInput);
            m_localPlayerInput.Dig = null;
            m_localPlayerInput.Hit = null;
            m_localPlayerInput.Interact = null;
            m_localInputBodyPosition = player.ComponentBody.Position;
            m_localInputBodyVelocity = player.ComponentBody.Velocity;
            m_localInputBodyRotation = player.ComponentBody.Rotation;
            m_localInputLookAngles = player.ComponentLocomotion.LookAngles;
            m_localInputSequence = m_localInputSequence == int.MaxValue
                ? 1
                : m_localInputSequence + 1;
            m_localInputResendsRemaining = 3;
            if (inventory != null)
            {
                m_lastLocalInventoryValues = Enumerable.Range(0, inventory.SlotsCount)
                    .Select(inventory.GetSlotValue).ToArray();
                m_lastLocalInventoryCounts = Enumerable.Range(0, inventory.SlotsCount)
                    .Select(inventory.GetSlotCount).ToArray();
            }
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        private void UpdateLocalDigTarget(ComponentPlayer player, Ray3? digRay)
        {
            if (!digRay.HasValue || player?.ComponentMiner == null)
            {
                m_localDigTarget = null;
                return;
            }
            TerrainRaycastResult? hit = player.ComponentMiner.Raycast<TerrainRaycastResult>(
                digRay.Value, RaycastMode.Digging, raycastTerrain: true,
                raycastBodies: false, raycastMovingBlocks: false);
            if (!hit.HasValue)
            {
                m_localDigTarget = null;
                return;
            }
            Point3 point = new Point3(hit.Value.CellFace.X, hit.Value.CellFace.Y,
                hit.Value.CellFace.Z);
            int expectedValue = Terrain.ReplaceLight(hit.Value.Value, 0);
            if (!m_localDigTarget.HasValue || m_localDigTarget.Value != point)
            {
                m_localDigTarget = point;
            }
            IInventory inventory = player.ComponentMiner.Inventory;
            int activeSlot = inventory?.ActiveSlotIndex ?? -1;
            int toolValue = inventory != null && activeSlot >= 0 && activeSlot < inventory.SlotsCount
                ? inventory.GetSlotValue(activeSlot)
                : 0;
            int toolCount = inventory != null && activeSlot >= 0 && activeSlot < inventory.SlotsCount
                ? inventory.GetSlotCount(activeSlot)
                : 0;
            if (!m_localTerrainDigIntents.TryGetValue(point,
                out LocalTerrainDigIntent intent) || intent.ExpectedValue != expectedValue)
            {
                intent = new LocalTerrainDigIntent
                {
                    ExpectedValue = expectedValue,
                    StartClientTick = client.Step
                };
                m_localTerrainDigIntents[point] = intent;
            }
            intent.DigRay = digRay.Value;
            intent.HitFace = hit.Value.CellFace.Face;
            intent.ActiveSlotIndex = activeSlot;
            intent.ToolValue = toolValue;
            intent.ToolCount = toolCount;
            intent.BodyPosition = player.ComponentBody.Position;
            intent.LastSeenTime = Time.RealTime;
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        private void UpdateLocalAimLifecycle(ComponentPlayer player, PlayerInput playerInput,
            IInventory inventory, int activeSlot)
        {
            int itemValue = inventory != null && activeSlot >= 0 && activeSlot < inventory.SlotsCount
                ? inventory.GetSlotValue(activeSlot)
                : 0;
            bool isAimable = itemValue != 0 &&
                BlocksManager.Blocks[Terrain.ExtractContents(itemValue)].IsAimable;
            double now = Time.RealTime;

            if (playerInput.Aim.HasValue && isAimable)
            {
                Ray3 aim = playerInput.Aim.Value;
                if (!m_localAimActive || activeSlot != m_localAimSlot ||
                    Terrain.ExtractContents(itemValue) != Terrain.ExtractContents(m_localAimItemValue))
                {
                    if (m_localAimActive)
                        SendLocalAimEvent(player, PlayerAimAction.Cancel, m_localAimRay);
                    m_localAimSequence = m_localAimSequence == int.MaxValue
                        ? 1
                        : m_localAimSequence + 1;
                    m_localAimActive = true;
                    m_localAimSlot = activeSlot;
                    m_localAimItemValue = itemValue;
                    m_localAimItemCount = inventory.GetSlotCount(activeSlot);
                    m_localAimRay = aim;
                    m_lastAimUpdateSentTime = now;
                    SendLocalAimEvent(player, PlayerAimAction.Start, aim);
                }
                else
                {
                    m_localAimRay = aim;
                    m_localAimItemValue = itemValue;
                    m_localAimItemCount = inventory.GetSlotCount(activeSlot);
                    if (now - m_lastAimUpdateSentTime >= SyncPulseDuration)
                    {
                        m_lastAimUpdateSentTime = now;
                        SendLocalAimEvent(player, PlayerAimAction.Update, aim);
                    }
                }
                return;
            }

            if (!m_localAimActive) return;
            bool sameItem = isAimable && activeSlot == m_localAimSlot &&
                Terrain.ExtractContents(itemValue) == Terrain.ExtractContents(m_localAimItemValue);
            if (sameItem)
            {
                m_localAimItemValue = itemValue;
                m_localAimItemCount = inventory.GetSlotCount(activeSlot);
            }
            SendLocalAimEvent(player,
                sameItem ? PlayerAimAction.Release : PlayerAimAction.Cancel, m_localAimRay);
            m_localAimActive = false;
            m_localAimSlot = -1;
            m_localAimItemValue = 0;
            m_localAimItemCount = 0;
        }

        private void SendLocalAimEvent(ComponentPlayer player, PlayerAimAction action, Ray3 aim)
        {
            if (client?.IsConnected != true || player?.ComponentBody == null) return;
            NetworkMessageSender.SendPlayerAimMessage(new PlayerAimMessage(
                m_localAimSequence, action, aim, m_localAimSlot, m_localAimItemValue,
                m_localAimItemCount, player.ComponentBody.Position,
                player.ComponentBody.Rotation));
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // Hit is a cooldown-limited edge, not continuous movement state. Send it reliably at the
        // same cadence the original ComponentPlayer accepts while local prediction remains native.
        private void UpdateLocalHitRequests(Ray3? hitRay)
        {
            if (!hitRay.HasValue || Time.RealTime < m_nextLocalHitRequestTime ||
                client?.IsConnected != true)
                return;
            m_nextLocalHitRequestTime = Time.RealTime + PlayerHitRequestInterval;
            m_localHitSequence = m_localHitSequence == int.MaxValue ? 1 : m_localHitSequence + 1;
            NetworkMessageSender.SendPlayerHitRequest(new PlayerActionMessage(
                PlayerActionType.HitRequest, client.ClientID, m_localHitSequence, hitRay.Value));
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // Interact/Use/Place is a cooldown-limited edge. Include the inventory state observed
        // immediately before local prediction so the host executes the same native action once.
        private void UpdateLocalInteractRequests(ComponentPlayer player, Ray3? interactRay)
        {
            if (!interactRay.HasValue || Time.RealTime < m_nextLocalInteractRequestTime ||
                client?.IsConnected != true || player?.ComponentMiner?.Inventory == null)
                return;
            IInventory inventory = player.ComponentMiner.Inventory;
            int activeSlot = inventory.ActiveSlotIndex;
            if (activeSlot < 0 || activeSlot >= inventory.SlotsCount) return;
            m_nextLocalInteractRequestTime = Time.RealTime + PlayerInteractRequestInterval;
            m_localInteractSequence = m_localInteractSequence == int.MaxValue
                ? 1
                : m_localInteractSequence + 1;
            NetworkMessageSender.SendPlayerInteractRequest(new PlayerActionMessage(
                PlayerActionType.InteractRequest, client.ClientID, m_localInteractSequence,
                interactRay.Value, activeSlot, inventory.GetSlotValue(activeSlot),
                inventory.GetSlotCount(activeSlot)));
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // Drop is a one-frame edge. Send the pre-prediction slot reliably so a later empty
        // inventory snapshot cannot erase the item before the host executes the native action.
        private void UpdateLocalDropRequests(ComponentPlayer player, bool drop)
        {
            if (!drop || client?.IsConnected != true ||
                player?.ComponentMiner?.Inventory == null || player.ComponentBody == null)
                return;
            IInventory inventory = player.ComponentMiner.Inventory;
            int activeSlot = inventory.ActiveSlotIndex;
            if (activeSlot < 0 || activeSlot >= inventory.SlotsCount) return;
            int itemValue = inventory.GetSlotValue(activeSlot);
            int itemCount = inventory.GetSlotCount(activeSlot);
            if (itemValue == 0 || itemCount <= 0) return;

            m_localDropSequence = m_localDropSequence == int.MaxValue
                ? 1
                : m_localDropSequence + 1;
            var message = new PlayerActionMessage(
                PlayerActionType.DropRequest, client.ClientID, m_localDropSequence,
                default, activeSlot, itemValue, itemCount)
            {
                DropCount = itemCount,
                Position = player.ComponentBody.Position +
                    new Vector3(0f, player.ComponentBody.StanceBoxSize.Y * 0.66f, 0f) +
                    0.25f * player.ComponentBody.Matrix.Forward,
                Velocity = 8f * Matrix.CreateFromQuaternion(
                    player.ComponentCreatureModel.EyeRotation).Forward
            };
            NetworkMessageSender.SendPlayerDropRequest(message);
            m_pendingLocalDropValue = itemValue;
            m_pendingLocalDropCount = itemCount;
            m_pendingLocalDropPosition = message.Position;
            m_pendingLocalDropPredictionUntil = Time.RealTime + 0.5;
        }

        public bool TryGetNetworkPlayerInput(ComponentPlayer player, out PlayerInput playerInput)
        {
            playerInput = default;
            if (!IsHost || player == null) return false;
            int sourceClientId = m_networkPlayerData.FirstOrDefault(pair =>
                pair.Key > 0 && ReferenceEquals(pair.Value, player.PlayerData)).Key;
            if (sourceClientId <= 0) return false;
            if (!m_networkPlayerInputs.TryGetValue(sourceClientId, out NetworkPlayerInputState state) ||
                Time.RealTime - state.LastReceivedTime > RemoteInputHoldDuration)
                return true;

            if (state.DropEvents.Count > 0)
            {
                PlayerActionMessage drop = state.DropEvents.Dequeue();
                playerInput = state.ConsumedSequence != state.Sequence
                    ? state.Input
                    : state.HeldInput;
                state.ConsumedSequence = state.Sequence;
                ApplyInteractionInventory(player.ComponentMiner?.Inventory, drop);
                if (IsFinite(drop.Position) &&
                    Vector3.DistanceSquared(player.ComponentBody.Position, drop.Position) <= 64f)
                    player.ComponentBody.Position = drop.Position;
                playerInput.Drop = true;
                state.HeldInput = CreateHeldNetworkInput(playerInput);
                return true;
            }

            if (state.InteractEvents.Count > 0 &&
                Time.RealTime >= state.NextInteractExecutionTime)
            {
                PlayerActionMessage interact = state.InteractEvents.Dequeue();
                playerInput = state.ConsumedSequence != state.Sequence
                    ? state.Input
                    : state.HeldInput;
                state.ConsumedSequence = state.Sequence;
                ApplyInteractionInventory(player.ComponentMiner?.Inventory, interact);
                playerInput.Interact = interact.HitRay;
                state.HeldInput = CreateHeldNetworkInput(playerInput);
                state.NextInteractExecutionTime = Time.RealTime + PlayerInteractRequestInterval;
                return true;
            }

            if (state.AimEvents.Count > 0)
            {
                PlayerAimMessage aimEvent = state.AimEvents.Dequeue();
                if (state.ConsumedSequence != state.Sequence)
                {
                    playerInput = state.Input;
                    state.ConsumedSequence = state.Sequence;
                }
                else
                {
                    playerInput = state.HeldInput;
                }
                if (aimEvent.Action == PlayerAimAction.Start ||
                    aimEvent.Action == PlayerAimAction.Update)
                {
                    state.HeldAim = aimEvent.Aim;
                    playerInput.Aim = aimEvent.Aim;
                }
                else
                {
                    state.HeldAim = null;
                    playerInput.Aim = null;
                    state.LastCompletedAimSequence = Math.Max(
                        state.LastCompletedAimSequence, aimEvent.Sequence);
                    state.QueuedAimCompletions.Remove(aimEvent.Sequence);
                    if (aimEvent.Action == PlayerAimAction.Cancel)
                    {
                        player.ComponentMiner?.Aim(aimEvent.Aim, AimState.Cancelled);
                        ModManager.ModParentField.ModifyParentField(
                            player, "m_aim", (Ray3?)null, typeof(ComponentPlayer));
                    }
                    state.ActiveAimSequence = -1;
                    state.ActiveAimSlotIndex = -1;
                    state.ActiveAimItemValue = 0;
                    state.ActiveAimItemCount = 0;
                }
                state.HeldInput = CreateHeldNetworkInput(playerInput);
                return true;
            }

            if (state.HitEvents.Count > 0 && Time.RealTime >= state.NextHitExecutionTime)
            {
                PlayerActionMessage hit = state.HitEvents.Dequeue();
                playerInput = state.ConsumedSequence != state.Sequence
                    ? state.Input
                    : state.HeldInput;
                state.ConsumedSequence = state.Sequence;
                playerInput.Hit = hit.HitRay;
                state.HeldInput = CreateHeldNetworkInput(playerInput);
                state.NextHitExecutionTime = Time.RealTime + PlayerHitRequestInterval;
                return true;
            }

            if (state.ConsumedSequence != state.Sequence)
            {
                player.ComponentBody.Rotation = state.BodyRotation;
                ModManager.ModParentField.ModifyParentField(
                    player.ComponentLocomotion, "m_lookAngles",
                    state.LookAngles, typeof(ComponentLocomotion));
                playerInput = state.Input;
                // Aim is executed directly by HandlePlayerAimMessage. Feeding the client ray into
                // ComponentPlayer would re-run its host-camera guard and could complete twice.
                playerInput.Aim = null;
                state.ConsumedSequence = state.Sequence;
                state.HeldInput = CreateHeldNetworkInput(playerInput);
            }
            else
            {
                playerInput = state.HeldInput;
            }
            playerInput.Aim = null;
            return true;
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Aim
        // A remote player's aim ray belongs to the client's camera. ComponentPlayer's local
        // WorldToScreen guard rejects that ray against the host-side replica GameWidget, so run
        // the original ComponentMiner aim lifecycle directly on the host game thread.
        private void HandlePlayerAimMessage(PlayerAimMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0 ||
                !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer == null)
                return;

            ComponentPlayer player = playerData.ComponentPlayer;
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory == null || message.ActiveSlotIndex < 0 ||
                message.ActiveSlotIndex >= inventory.VisibleSlotsCount ||
                message.ItemCount <= 0 ||
                !BlocksManager.Blocks[Terrain.ExtractContents(message.ItemValue)].IsAimable)
                return;

            if (!m_networkPlayerInputs.TryGetValue(sourceClientId,
                out NetworkPlayerInputState state))
            {
                state = new NetworkPlayerInputState();
                m_networkPlayerInputs[sourceClientId] = state;
            }
            state.LastReceivedTime = Time.RealTime;
            if (message.Sequence <= state.LastCompletedAimSequence)
                return;

            // Source: SubsystemThrowableBlockBehavior.cs:SubsystemThrowableBlockBehavior.OnAim
            // The projectile origin is derived from the authoritative replica body, so align it
            // to the pose captured with this aim edge before the original throw runs.
            if (IsFinite(message.BodyPosition) &&
                Vector3.DistanceSquared(player.ComponentBody.Position, message.BodyPosition) <= 64f)
                player.ComponentBody.Position = message.BodyPosition;
            player.ComponentBody.Rotation = message.BodyRotation;

            if (message.Action == PlayerAimAction.Start ||
                message.Action == PlayerAimAction.Update)
            {
                if (state.ActiveAimSequence != message.Sequence)
                {
                    // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
                    // Preserve the original aim cooldown while excluding its local-camera check.
                    SubsystemTime subsystemTime = GameManager.Project?.FindSubsystem<SubsystemTime>(false);
                    SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
                    if (subsystemTime == null || gameInfo == null) return;
                    double lastActionTime = ModManager.ModParentField.GetParentField<double>(
                        player, "m_lastActionTime", typeof(ComponentPlayer));
                    float requiredDelay = gameInfo.WorldSettings.GameMode == GameMode.Creative
                        ? 0.1f
                        : 1.4f;
                    if (subsystemTime.GameTime - lastActionTime <= requiredDelay) return;

                    // Source: Survivalcraft/Game/SubsystemThrowableBlockBehavior.cs:OnAim
                    // A post-prediction inventory snapshot can overtake this reliable Start.
                    // Restore the pre-throw slot carried by Start, then keep it reserved until the
                    // original host ComponentPlayer receives Completed and consumes it once.
                    ApplyAimReservation(inventory, message);
                    state.ActiveAimSequence = message.Sequence;
                    state.ActiveAimSlotIndex = message.ActiveSlotIndex;
                    state.ActiveAimItemValue = message.ItemValue;
                    state.ActiveAimItemCount = message.ItemCount;
                    inventory.ActiveSlotIndex = message.ActiveSlotIndex;
                }
                else if (message.ActiveSlotIndex == state.ActiveAimSlotIndex &&
                    Terrain.ExtractContents(message.ItemValue) ==
                        Terrain.ExtractContents(state.ActiveAimItemValue))
                {
                    inventory.ActiveSlotIndex = state.ActiveAimSlotIndex;
                }
                else return;

                state.HeldAim = message.Aim;
                if (player.ComponentMiner.Aim(message.Aim, AimState.InProgress))
                {
                    player.ComponentMiner.Aim(message.Aim, AimState.Cancelled);
                    CompleteHostAimLifecycle(player, state, message.Sequence,
                        updateLastActionTime: false);
                }
                return;
            }

            if (state.ActiveAimSequence != message.Sequence || !state.HeldAim.HasValue ||
                message.ActiveSlotIndex != state.ActiveAimSlotIndex ||
                Terrain.ExtractContents(message.ItemValue) !=
                    Terrain.ExtractContents(state.ActiveAimItemValue))
                return;
            ApplyAimReservation(inventory, new PlayerAimMessage(message.Sequence,
                message.Action, message.Aim, state.ActiveAimSlotIndex,
                state.ActiveAimItemValue, state.ActiveAimItemCount,
                message.BodyPosition, message.BodyRotation));
            player.ComponentMiner.Aim(message.Aim,
                message.Action == PlayerAimAction.Release
                    ? AimState.Completed
                    : AimState.Cancelled);
            CompleteHostAimLifecycle(player, state, message.Sequence,
                updateLastActionTime: message.Action == PlayerAimAction.Release);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        private void CompleteHostAimLifecycle(ComponentPlayer player,
            NetworkPlayerInputState state, int sequence, bool updateLastActionTime)
        {
            state.LastCompletedAimSequence = Math.Max(state.LastCompletedAimSequence, sequence);
            state.HeldAim = null;
            state.HeldInput.Aim = null;
            state.ActiveAimSequence = -1;
            state.ActiveAimSlotIndex = -1;
            state.ActiveAimItemValue = 0;
            state.ActiveAimItemCount = 0;
            if (!updateLastActionTime) return;
            SubsystemTime subsystemTime = GameManager.Project?.FindSubsystem<SubsystemTime>(false);
            if (subsystemTime != null)
                ModManager.ModParentField.ModifyParentField(
                    player, "m_lastActionTime", subsystemTime.GameTime,
                    typeof(ComponentPlayer));
        }

        // Source: Survivalcraft/Game/SubsystemThrowableBlockBehavior.cs:SubsystemThrowableBlockBehavior.OnAim
        private static void ApplyAimReservation(IInventory inventory, PlayerAimMessage message)
        {
            inventory.ActiveSlotIndex = message.ActiveSlotIndex;
            inventory.RemoveSlotItems(message.ActiveSlotIndex, int.MaxValue);
            inventory.AddSlotItems(message.ActiveSlotIndex, message.ItemValue, message.ItemCount);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // Client hit requests execute through the host's original ComponentPlayer. The resulting
        // ComponentMiner.Poke edge is then broadcast by BroadcastPlayerPokeIfStarted.
        private void HandlePlayerActionMessage(PlayerActionMessage message, int sourceClientId)
        {
            if (message == null) return;
            if (message.Action == PlayerActionType.RespawnRequest)
            {
                if (IsHost)
                {
                    if (sourceClientId <= 0 || message.PlayerIndex != sourceClientId ||
                        !ResetNetworkPlayerAfterRespawn(sourceClientId, message.Position,
                            requireDead: true, sequence: message.Sequence))
                        return;
                    NetworkMessageSender.BroadcastPlayerRespawn(message);
                    SendGamePlayerHealthMessage(true);
                }
                else if (sourceClientId == 0 && message.PlayerIndex != client.ClientID)
                {
                    ResetNetworkPlayerAfterRespawn(message.PlayerIndex, message.Position,
                        requireDead: false, sequence: message.Sequence);
                }
                return;
            }
            if (message.Action == PlayerActionType.LeaveRequest)
            {
                if (sourceClientId < 0 || message.PlayerIndex != sourceClientId ||
                    sourceClientId == client.ClientID)
                    return;
                if (!IsHost && sourceClientId == 0)
                {
                    HandleHostDisconnected();
                    return;
                }
                if (!IsHost)
                    m_departedRemoteClientIds.Add(sourceClientId);
                RemoveNetworkPlayer(sourceClientId);
                playerMappingManager.ReleasePlayerIndex(sourceClientId);
                return;
            }
            if (!IsHost && message.Action == PlayerActionType.Whistle)
            {
                if (sourceClientId != 0 || message.PlayerIndex == client.ClientID ||
                    !IsFinite(message.Position))
                    return;
                if (m_playerWhistleSequences.TryGetValue(message.PlayerIndex,
                    out int lastSequence) && message.Sequence <= lastSequence)
                    return;
                m_playerWhistleSequences[message.PlayerIndex] = message.Sequence;
                // Source: Survivalcraft/Game/SubsystemWhistleBlockBehavior.cs:SubsystemWhistleBlockBehavior.OnUse
                GameManager.Project?.FindSubsystem<SubsystemAudio>(false)?.PlayRandomSound(
                    "Audio/Whistle", 1f, m_audioEventRandom.Float(-0.2f, 0f),
                    message.Position, 4f, autoDelay: true);
                return;
            }
            if (IsHost)
            {
                if (sourceClientId <= 0 ||
                    (message.Action != PlayerActionType.HitRequest &&
                        message.Action != PlayerActionType.InteractRequest &&
                        message.Action != PlayerActionType.DropRequest) ||
                    !m_networkPlayerData.ContainsKey(sourceClientId))
                    return;
                if (!m_networkPlayerInputs.TryGetValue(sourceClientId,
                    out NetworkPlayerInputState state))
                {
                    state = new NetworkPlayerInputState();
                    m_networkPlayerInputs[sourceClientId] = state;
                }
                state.LastReceivedTime = Time.RealTime;
                if (message.Action == PlayerActionType.DropRequest)
                {
                    if (message.Sequence <= state.LastDropSequence ||
                        message.PlayerIndex != sourceClientId ||
                        message.ActiveSlotIndex < 0 || message.ItemValue == 0 ||
                        message.ItemCount <= 0 || message.DropCount <= 0 ||
                        message.DropCount > message.ItemCount || !IsFinite(message.Position) ||
                        !IsFinite(message.Velocity))
                        return;
                    state.LastDropSequence = message.Sequence;
                    ExecuteHostDropRequest(sourceClientId, message);
                }
                else if (message.Action == PlayerActionType.InteractRequest)
                {
                    if (message.Sequence <= state.LastInteractSequence) return;
                    state.LastInteractSequence = message.Sequence;
                    state.InteractEvents.Enqueue(message);
                }
                else
                {
                    if (message.Sequence <= state.LastHitSequence) return;
                    state.LastHitSequence = message.Sequence;
                    state.HitEvents.Enqueue(message);
                }
                return;
            }

            if (sourceClientId != 0 || message.Action != PlayerActionType.Poke ||
                message.PlayerIndex == client.ClientID ||
                !m_networkPlayerData.TryGetValue(message.PlayerIndex, out PlayerData playerData))
                return;
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Poke
            double now = Time.RealTime;
            bool hasPlayerState = RemotePlayers.TryGetValue(message.PlayerIndex,
                out NetworkPlayerState playerState);
            if (!hasPlayerState || now - playerState.LastPokeEventTime > 0.1)
                playerData.ComponentPlayer?.ComponentMiner?.Poke(forceRestart: true);
            if (hasPlayerState)
            {
                playerState.PokingPhase = 0.0001f;
                playerState.LastPokeEventTime = now;
            }
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        private bool ResetNetworkPlayerAfterRespawn(int playerClientId, Vector3 position,
            bool requireDead, int sequence)
        {
            if (!m_networkPlayerData.TryGetValue(playerClientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer == null || !IsFinite(position))
                return false;
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentHealth health = player.ComponentHealth;
            if (health == null || (requireDead && health.Health > 0f)) return false;
            if (!m_networkPlayerInputs.TryGetValue(playerClientId,
                out NetworkPlayerInputState state))
            {
                state = new NetworkPlayerInputState();
                m_networkPlayerInputs[playerClientId] = state;
            }
            if (sequence <= state.LastRespawnSequence) return false;
            state.LastRespawnSequence = sequence;

            Vector3 spawnPosition = position;
            if (playerData.SpawnPosition != Vector3.Zero &&
                Vector3.DistanceSquared(spawnPosition, playerData.SpawnPosition) > 4096f)
                spawnPosition = playerData.SpawnPosition;
            player.ComponentBody.Position = spawnPosition;
            player.ComponentBody.Velocity = Vector3.Zero;
            player.ComponentBody.TargetCrouchFactor = 0f;
            playerData.SpawnPosition = spawnPosition;

            ModManager.ModParentField.ModifyParentField(
                health, "<Health>k__BackingField", 1f, typeof(ComponentHealth));
            ModManager.ModParentField.ModifyParentField(
                health, "<Air>k__BackingField", 1f, typeof(ComponentHealth));
            ModManager.ModParentField.ModifyParentField(
                health, "<HealthChange>k__BackingField", 0f, typeof(ComponentHealth));
            ModManager.ModParentField.ModifyParentField(
                health, "<DeathTime>k__BackingField", (double?)null, typeof(ComponentHealth));
            ModManager.ModParentField.ModifyParentField(
                health, "<CauseOfDeath>k__BackingField", string.Empty, typeof(ComponentHealth));
            ModManager.ModParentField.ModifyParentField(
                health, "m_lastHealth", 1f, typeof(ComponentHealth));
            ResetNetworkPlayerVitals(player);

            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    model, "<DeathPhase>k__BackingField", 0f,
                    typeof(ComponentCreatureModel));
                ModManager.ModParentField.ModifyParentField(
                    model, "<DeathCauseOffset>k__BackingField", Vector3.Zero,
                    typeof(ComponentCreatureModel));
            }
            ComponentSpawn spawn = player.Entity.FindComponent<ComponentSpawn>();
            if (spawn != null)
                ModManager.ModParentField.ModifyParentField(
                    spawn, "<DespawnTime>k__BackingField", (double?)null,
                    typeof(ComponentSpawn));
            player.Entity.FindComponent<ComponentOnFire>()?.Extinguish();

            state.Input = default;
            state.HeldInput = default;
            state.HeldAim = null;
            state.AimEvents.Clear();
            state.QueuedAimCompletions.Clear();
            state.InteractEvents.Clear();
            state.HitEvents.Clear();
            state.DropEvents.Clear();
            state.InitialPositionApplied = true;
            state.BodyPosition = spawnPosition;
            state.BodyVelocity = Vector3.Zero;
            state.LastReceivedTime = Time.RealTime;
            m_hostPlayerPokingPhases.Remove(playerClientId);
            m_hostPlayerPokeSequences.Remove(playerClientId);

            if (m_clientRecordKeys.TryGetValue(playerClientId, out string recordKey))
            {
                m_playerRecords[recordKey] = CapturePlayerRecord(playerData);
                if (IsHost) m_playerRecordsDirty = true;
            }
            return true;
        }

        // Source: Pak/Database.xml:Player.ComponentVitalStats defaults
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        private static void ResetNetworkPlayerVitals(ComponentPlayer player)
        {
            ComponentVitalStats vital = player?.ComponentVitalStats;
            if (vital != null)
            {
                ModManager.ModParentField.ModifyParentField(vital, "m_food", 1f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_stamina", 1f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_sleep", 1f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_temperature", 12f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_wetness", 0f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastFood", 1f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastStamina", 1f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastSleep", 1f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastTemperature", 12f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_lastWetness", 0f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_environmentTemperature", 8f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_targetTemperature", 12f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_targetTemperatureFlux", 0f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_sleepBlackoutFactor", 0f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_sleepBlackoutDuration", 0f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_temperatureBlackoutFactor", 0f, typeof(ComponentVitalStats));
                ModManager.ModParentField.ModifyParentField(vital, "m_temperatureBlackoutDuration", 0f, typeof(ComponentVitalStats));
                ModManager.ModParentField.GetParentField<Dictionary<int, float>>(
                    vital, "m_satiation", typeof(ComponentVitalStats))?.Clear();
            }

            ComponentSleep sleep = player?.ComponentSleep;
            sleep?.WakeUp();
            if (sleep != null)
            {
                ModManager.ModParentField.ModifyParentField(sleep, "m_sleepFactor", 0f, typeof(ComponentSleep));
                ModManager.ModParentField.ModifyParentField(sleep, "m_messageFactor", 0f, typeof(ComponentSleep));
            }

            ComponentFlu flu = player?.Entity.FindComponent<ComponentFlu>();
            if (flu != null)
            {
                foreach (string field in new[]
                {
                    "m_fluOnset", "m_fluDuration", "m_coughDuration", "m_sneezeDuration",
                    "m_blackoutDuration", "m_blackoutFactor"
                })
                {
                    ModManager.ModParentField.ModifyParentField(flu, field, 0f, typeof(ComponentFlu));
                }
            }

            ComponentSickness sickness = player?.Entity.FindComponent<ComponentSickness>();
            if (sickness != null)
            {
                PukeParticleSystem puke = ModManager.ModParentField.GetParentField(
                    sickness, "m_pukeParticleSystem", typeof(ComponentSickness)) as PukeParticleSystem;
                if (puke != null) puke.IsStopped = true;
                ModManager.ModParentField.ModifyParentField(
                    sickness, "m_pukeParticleSystem", null, typeof(ComponentSickness));
                ModManager.ModParentField.ModifyParentField(sickness, "m_sicknessDuration", 0f, typeof(ComponentSickness));
                ModManager.ModParentField.ModifyParentField(sickness, "m_greenoutDuration", 0f, typeof(ComponentSickness));
                ModManager.ModParentField.ModifyParentField(sickness, "m_greenoutFactor", 0f, typeof(ComponentSickness));
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
                !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
                !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // The request is captured before client prediction consumes a placed/used item. Restore
        // that exact pre-action slot on the host, then let ComponentPlayer.Use/Interact/Place
        // perform the authoritative mutation and terrain notification.
        private static void ApplyInteractionInventory(IInventory inventory,
            PlayerActionMessage message)
        {
            if (inventory == null || message == null || message.ActiveSlotIndex < 0 ||
                message.ActiveSlotIndex >= inventory.SlotsCount || message.ItemCount < 0)
                return;
            inventory.ActiveSlotIndex = message.ActiveSlotIndex;
            inventory.RemoveSlotItems(message.ActiveSlotIndex, int.MaxValue);
            if (message.ItemValue != 0 && message.ItemCount > 0)
                inventory.AddSlotItems(message.ActiveSlotIndex, message.ItemValue,
                    message.ItemCount);
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.DropSlotItems
        private void ExecuteHostDropRequest(int sourceClientId, PlayerActionMessage message)
        {
            if (!m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData)) return;
            ComponentPlayer player = playerData?.ComponentPlayer;
            IInventory inventory = player?.ComponentMiner?.Inventory;
            ComponentBody body = player?.ComponentBody;
            if (inventory == null || body == null || message.ActiveSlotIndex < 0 ||
                message.ActiveSlotIndex >= inventory.SlotsCount)
                return;
            ApplyInteractionInventory(inventory, message);
            int slotValue = inventory.GetSlotValue(message.ActiveSlotIndex);
            int count = Math.Min(message.DropCount,
                inventory.GetSlotCount(message.ActiveSlotIndex));
            if (slotValue != message.ItemValue || count <= 0) return;
            int removed = inventory.RemoveSlotItems(message.ActiveSlotIndex, count);
            if (removed <= 0) return;
            Vector3 defaultPosition = body.Position +
                new Vector3(0f, body.StanceBoxSize.Y * 0.66f, 0f) +
                0.25f * body.Matrix.Forward;
            Vector3 position = Vector3.DistanceSquared(body.Position, message.Position) <= 64f
                ? message.Position
                : defaultPosition;
            Vector3 velocity = message.Velocity;
            if (velocity.LengthSquared() > 20f * 20f)
                velocity = Vector3.Normalize(velocity) * 20f;
            GameManager.Project.FindSubsystem<SubsystemPickables>(true).AddPickable(
                slotValue, removed, position, velocity, null);
            MarkHostInventoryAuthoritative(sourceClientId);
        }

        private void MarkHostInventoryAuthoritative(int sourceClientId)
        {
            if (sourceClientId <= 0 ||
                !m_networkPlayerInputs.TryGetValue(sourceClientId,
                    out NetworkPlayerInputState state))
                return;
            state.LastAuthoritativeInventoryTick = Math.Max(
                state.LastAuthoritativeInventoryTick, client?.Step ?? 0);
            m_lastSentInventoryValues.Remove(sourceClientId);
            m_lastSentInventoryCounts.Remove(sourceClientId);
        }

        private void HandleGamePlayerInputMessage(GamePlayerInputMessage msg, int sourceClientId)
        {
            if (!IsHost || msg == null || sourceClientId <= 0 ||
                !m_networkPlayerData.ContainsKey(sourceClientId) ||
                (msg.PlayerIndex != 0 && msg.PlayerIndex != sourceClientId))
                return;
            if (!m_networkPlayerInputs.TryGetValue(sourceClientId, out NetworkPlayerInputState state))
            {
                state = new NetworkPlayerInputState();
                m_networkPlayerInputs[sourceClientId] = state;
            }
            if (!state.InitialPositionApplied &&
                m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) &&
                playerData.ComponentPlayer?.ComponentBody != null)
            {
                ComponentBody body = playerData.ComponentPlayer.ComponentBody;
                if (Vector3.DistanceSquared(body.Position, msg.BodyPosition) <= 64f)
                    body.Position = msg.BodyPosition;
                state.InitialPositionApplied = true;
            }
            if (msg.Sequence <= state.Sequence) return;
            PlayerData remotePlayerData = m_networkPlayerData[sourceClientId];
            ComponentPlayer remotePlayer = remotePlayerData.ComponentPlayer;
            if (remotePlayer != null)
            {
                ModManager.ModParentField.ModifyParentField(remotePlayer.ComponentInput,
                    "<IsControlledByTouch>k__BackingField", msg.IsControlledByTouch,
                    typeof(ComponentInput));
                if (remotePlayer.ComponentMiner != null)
                {
                    ModManager.ModParentField.ModifyParentField(remotePlayer.ComponentMiner,
                        "<PokingPhase>k__BackingField", msg.PokingPhase,
                        typeof(ComponentMiner));
                }
                // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
                // Touch buttons mutate these persistent states directly and may not set PlayerInput toggles.
                remotePlayer.ComponentBody.TargetCrouchFactor = msg.IsCrouching ? 1f : 0f;
                remotePlayer.ComponentLocomotion.IsCreativeFlyEnabled = msg.IsFlying;
                IInventory inventory = remotePlayer.ComponentMiner?.Inventory;
                bool aimLifecycleActive = state.HeldAim.HasValue ||
                    state.AimEvents.Count > 0 || state.QueuedAimCompletions.Count > 0;
                if (!aimLifecycleActive)
                {
                    if (inventory != null && msg.ActiveSlotIndex >= 0 &&
                        msg.ActiveSlotIndex < inventory.VisibleSlotsCount)
                        inventory.ActiveSlotIndex = msg.ActiveSlotIndex;
                    // Source: Survivalcraft/Game/SubsystemThrowableBlockBehavior.cs:OnAim
                    // The host-owned throw must consume the reserved item. Do not let the local
                    // prediction's already-decremented snapshot erase it before Aim Completed.
                    if (inventory != null && msg.SlotValues?.Length > 0 &&
                        !msg.PlayerInput.Drop &&
                        msg.InventoryAuthorityTick >= state.LastAuthoritativeInventoryTick)
                        ApplyInventory(inventory, msg.SlotValues, msg.SlotCounts);
                }
                MatchRemoteRidingState(remotePlayer, msg.IsRiding, msg.MountEntityId);
            }
            state.Input = SanitizeNetworkPlayerInput(msg.PlayerInput);
            state.BodyPosition = msg.BodyPosition;
            state.BodyVelocity = msg.BodyVelocity;
            state.ClientTick = msg.ClientTick;
            state.BodyRotation = msg.BodyRotation;
            state.LookAngles = msg.LookAngles;
            state.Sequence = msg.Sequence;
            state.LastReceivedTime = Time.RealTime;
        }

        private static PlayerInput SanitizeNetworkPlayerInput(PlayerInput input)
        {
            // Persistent touch-controlled states are synchronized explicitly to avoid applying a
            // toggle twice after the client has already changed its local ComponentGui state.
            input.ToggleCreativeFly = false;
            input.ToggleCrouch = false;
            input.ToggleMount = false;
            input.ScrollInventory = 0;
            input.SelectInventorySlot = null;
            input.ToggleInventory = false;
            input.ToggleClothing = false;
            input.TakeScreenshot = false;
            input.SwitchCameraMode = false;
            input.TimeOfDay = false;
            input.Lighting = false;
            input.Precipitation = false;
            input.Fog = false;
            input.KeyboardHelp = false;
            input.GamepadHelp = false;
            input.Dig = null;
            input.Aim = null;
            return input;
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
        private ushort GetClientMountEntityId(ComponentPlayer player)
        {
            Entity mountEntity = player?.ComponentRider?.Mount?.Entity;
            if (mountEntity == null) return 0;
            foreach (KeyValuePair<ushort, Entity> item in m_remoteAnimals)
            {
                if (ReferenceEquals(item.Value, mountEntity)) return item.Key;
            }
            return 0;
        }

        private void MatchRemoteRidingState(
            ComponentPlayer player, bool shouldBeRiding, ushort mountEntityId)
        {
            ComponentRider rider = player?.ComponentRider;
            if (rider == null || (rider.Mount != null) == shouldBeRiding) return;
            if (!shouldBeRiding)
            {
                rider.StartDismounting();
                return;
            }
            ComponentMount mount = null;
            if (mountEntityId != 0)
            {
                Entity mountEntity = m_hostAnimalIds.FirstOrDefault(
                    item => item.Value == mountEntityId).Key;
                mount = mountEntity?.FindComponent<ComponentMount>();
            }
            mount = mount ?? rider.FindNearestMount();
            if (mount != null) rider.StartMounting(mount);
        }

        private static PlayerInput CreateHeldNetworkInput(PlayerInput input)
        {
            input.Look = Vector2.Zero;
            input.CameraLook = Vector2.Zero;
            input.VrLook = null;
            input.ToggleCreativeFly = false;
            input.ToggleCrouch = false;
            input.ToggleMount = false;
            input.EditItem = false;
            input.Jump = false;
            input.ScrollInventory = 0;
            input.ToggleInventory = false;
            input.ToggleClothing = false;
            input.TakeScreenshot = false;
            input.SwitchCameraMode = false;
            input.TimeOfDay = false;
            input.Lighting = false;
            input.Precipitation = false;
            input.Fog = false;
            input.KeyboardHelp = false;
            input.GamepadHelp = false;
            input.Dig = null;
            input.Interact = null;
            input.Hit = null;
            input.PickBlockType = null;
            input.Drop = false;
            input.SelectInventorySlot = null;
            return input;
        }

        // ====================================================================
        // 异步注册 IUpdateable
        // ====================================================================
        /// <summary>
        /// 探测物理 LAN IP：优先选择非虚拟网卡的私网 IPv4 地址
        /// 逻辑：连 8.8.8.8 确定默认出口 IP，再验证是否为私网地址且非虚拟网卡
        /// </summary>
        private static System.Net.IPAddress DetectLanAddress()
        {
            try
            {
                // 方法1：通过 UDP 连 8.8.8.8 确定默认路由出口 IP
                using (var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram,
                    System.Net.Sockets.ProtocolType.Udp))
                {
                    socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
                    socket.Connect("8.8.8.8", 12345);
                    var defaultIp = ((System.Net.IPEndPoint)socket.LocalEndPoint).Address;

                    // 检查是否为私网地址 (10.x / 172.16-31.x / 192.168.x)
                    if (IsPrivateAddress(defaultIp))
                    {
                        return defaultIp;
                    }
                }

                // 非私网，继续搜索
            }
            catch { }

            try
            {
                // 方法2：遍历网卡，找第一个非虚拟的私网 IPv4
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    // 跳过虚拟/隧道/回环
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    // 跳过常见的虚拟网卡描述关键词
                    var desc = ni.Description.ToLowerInvariant();
                    if (desc.Contains("wireguard") ||
                        desc.Contains("vmware") || desc.Contains("virtualbox") ||
                        desc.Contains("hyper-v") || desc.Contains("wsl") ||
                        desc.Contains("docker") || desc.Contains("tunnel") ||
                        desc.Contains("cfw") || desc.Contains("clash"))
                        continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            IsPrivateAddress(ua.Address))
                        {
                            if (desc.Contains("zerotier")) return ua.Address;
                            if (ua.Address.ToString().StartsWith("10.160.", StringComparison.Ordinal))
                                return ua.Address;
                            return ua.Address;
                        }
                    }
                }
            }
            catch { }

            // 兜底：返回 Any（让系统自动选择）
            return System.Net.IPAddress.Any;
        }

        private static bool IsPrivateAddress(System.Net.IPAddress addr)
        {
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;
            var bytes = addr.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            return false;
        }

        public void OnUnload()
        {
            Window.Deactivated -= HandleWindowDeactivated;
            Window.Activated -= HandleWindowActivated;
            GameManager.ProjectDisposed -= HandleProjectDisposed;
            if (m_networkStatsLabel?.ParentWidget != null)
                m_networkStatsLabel.ParentWidget.Children.Remove(m_networkStatsLabel);
            m_networkStatsLabel = null;
            try
            {
                m_worldTransferSendCancellation?.Cancel();
                m_worldTransferSendSignal.Release();
                m_worldTransferSendTask?.Wait(1000);
            }
            catch { }
            try { client?.LeaveGame(); } catch { }
            try { server?.Dispose(); } catch { }
            try { explorer?.StopDiscovery(); } catch { }
            m_remoteServerDirectory = null;
        }
    }
}

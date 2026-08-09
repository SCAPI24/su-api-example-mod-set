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
        public Vector3 SpawnPosition;
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
        public double KnockbackCorrectionUntil;
        public int KnockbackCorrectionStartTick = -1;
        public int LastKnockbackSequence = -1;
    }

    public class NetworkPlayerRecord
    {
        public string Name;
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

    // Source: Mod/ScMultiplayer/Message/WorldControlRequestMessage.cs:WorldControlRequestMessage.RequestId
    internal sealed class PendingWorldControlRequest
    {
        public WorldControlAction Actions;
        public ComponentPlayer ComponentPlayer;
        public double ExpirationTime;
        public bool TimedOut;
        public string FailureMessage;
    }

    // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
    // ScMultiplayer.TrySendWorldControlRequest
    internal sealed class QueuedWorldControlRequest
    {
        public WorldControlAction Actions;
        public ComponentPlayer ComponentPlayer;
    }

    // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.HandleWorldControlRequest
    internal sealed class HostWorldControlRequestState
    {
        public int NextExpectedRequestId = 1;
        public readonly SortedDictionary<int, WorldControlRequestMessage> Pending =
            new SortedDictionary<int, WorldControlRequestMessage>();
        public readonly Dictionary<int, WorldControlResultMessage> Completed =
            new Dictionary<int, WorldControlResultMessage>();
        public readonly Queue<int> CompletedOrder = new Queue<int>();
    }

    public class NetworkPlayerInputState
    {
        public PlayerInput Input;
        public PlayerInput HeldInput;
        public Quaternion BodyRotation;
        public Vector2 LookAngles;
        public Vector3 BodyPosition;
        public Vector3 BodyVelocity;
        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.StandingOnValue
        public bool IsGrounded;
        // Source: Mod/Comms/Comms/PeerData.cs:PeerData.Ping
        public float LatestPositionRtt;
        public float SmoothedPositionRtt;
        public float PositionRttDeviation;
        public double NextPositionRttSampleTime;
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
        public readonly Queue<PlayerActionMessage> HitEvents = new Queue<PlayerActionMessage>();
        public int LastHitSequence;
        public double NextHitExecutionTime;
        public readonly Queue<PlayerActionMessage> DropEvents = new Queue<PlayerActionMessage>();
        public int LastDropSequence;
        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // Jump is a one-frame edge, so it cannot share the replaceable input snapshot queue.
        public readonly Queue<PendingNetworkJump> JumpEvents = new Queue<PendingNetworkJump>();
        public int LastJumpSequence;
        public int LastRespawnSequence;
        public int LastAuthoritativeInventoryTick;
    }

    public class PendingNetworkJump
    {
        public PlayerActionMessage Message;
        public double ReceivedTime;
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
    }

    public class PendingPickableAcquireRequest
    {
        public int RequestId;
        public double LastSendTime;
        public bool Rejected;
    }

    public class ProcessedPickableAcquireRequest
    {
        public int RequestId;
        public double ProcessedTime;
        public PickableSyncMessage Response;
    }

    public class ContainerNetworkState
    {
        public int Revision;
        public int[] Values = Array.Empty<int>();
        public int[] Counts = Array.Empty<int>();
    }

    internal sealed class NetworkContainerReference
    {
        public ComponentInventoryBase Inventory;
        public Point3 Coordinates;
        public int OwnerClientId = -1;
        public string ComponentType;
        public string Key;
    }

    public class PendingContainerTransaction
    {
        public ContainerSyncMessage Request;
        public double LastSendTime;
    }

    public class ProcessedContainerTransaction
    {
        public int RequestId;
        public double ProcessedTime;
        public ContainerSyncMessage Response;
    }

    public class TerrainCellState
    {
        public bool IsModified;
        public int CellValue;
        public int Tick;
        public long Sequence;
    }

    internal sealed class PendingTerrainChunkVerification
    {
        public long RequiredRevision;
        public double DueTime;
    }

    internal sealed class PendingTerrainChunkCheckpoint
    {
        public long Revision;
        public int ReceivedBatches;
        public int AppliedBatches;
        public bool CompleteReceived;
    }

    public class PendingFluidSettlement
    {
        public int TransientValue;
        public double DueGameTime;
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
        public int PredictedValue;
        public Ray3 DigRay;
        public int HitFace;
        public int StartClientTick;
        public int ActiveSlotIndex;
        public int ToolValue;
        public int ToolCount;
        public Vector3 BodyPosition;
        public double LastSeenTime;
    }

    public class LocalTerrainUsePrediction
    {
        public int ExpectedValue;
        public double LastSeenTime;
    }

    public class PendingTerrainPlacePrediction
    {
        public PlayerActionMessage Request;
        public int LocalPredictedValue;
        public bool IsCollapsingBlock;
        public bool HasLocalPrediction;
        public double LastSendTime;
        public int SendCount;
    }

    public class HostTerrainPlaceExecution
    {
        public int ClientId;
        public PlayerActionMessage Request;
        public PlayerStats PlayerStats;
        public long PreviousBlocksPlaced;
    }

    public class HostMeleeHitExecution
    {
        public int ClientId;
        public int RequestSequence;
        public ComponentHealth TargetHealth;
        public float PreviousHealth;
        public Vector3 HitPoint;
        public Vector3 HitDirection;
        public Vector3 AttackerVelocity;
    }

    public class LocalMeleePrediction
    {
        public ComponentMiner Miner;
        public double PreviousHitTime;
        public double CreatedTime;
    }

    public class PendingHostTerrainPlaceFallback
    {
        public int ExpectedValue;
        public int CheckAfterFrameIndex;
        public double ExpiresAt;
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
        public double BandwidthTokens;
        public double LastBandwidthTokenTime;
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
        public int ReservedPackets;
    }

    public class JoinCatchUpMessage
    {
        public byte[] Payload;
        public bool Sequenced;
        public bool Latest;
    }

    internal sealed class PendingJoinCatchUp
    {
        public int TargetClientId;
        public readonly Queue<JoinCatchUpMessage> Messages = new Queue<JoinCatchUpMessage>();
        public Action CompletionAction;
    }

    internal readonly struct JoinTransferTrafficSample
    {
        public JoinTransferTrafficSample(double time, long gameplayBytes)
        {
            Time = time;
            GameplayBytes = gameplayBytes;
        }

        public double Time { get; }

        public long GameplayBytes { get; }
    }

    internal sealed class JoinedPlayerInformation
    {
        public string Name;
        public string Role;
        public Vector3 Position;
        public bool IsSelf;
        public float Distance;
        public int ClockDirection;
    }

    public class JoinCatchUpJournal
    {
        public int StartTick;
        public int TotalBytes;
        public int TotalMessagesSent;
        public int TotalBytesSent;
        public int ReplayRound;
        public int DroppedMessages;
        public bool CutoffSealed;
        public readonly List<JoinCatchUpMessage> Messages = new List<JoinCatchUpMessage>();
        public readonly List<JoinCatchUpMessage> PostCutoffMessages =
            new List<JoinCatchUpMessage>();
    }

    public sealed class QueuedFrameAction
    {
        public Action Action;
        public long EnqueuedTimestamp;
        internal NetworkIngressCommand Command;
    }

    internal sealed class QueuedTerrainChunkSync
    {
        public TerrainChunkSyncMessage Message;
        public int SourceClientId;
        public long EnqueuedTimestamp;
        public NetworkIngressCommand Command;
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

    internal sealed class SkinSessionAsset
    {
        public string SkinName = string.Empty;
        public PlayerClass PlayerClass;
        public string Hash = string.Empty;
        public byte[] Data = Array.Empty<byte>();
    }

    internal sealed class SkinAssetTransfer
    {
        public string SkinName = string.Empty;
        public PlayerClass PlayerClass;
        public string Hash = string.Empty;
        public int TransferId;
        public int TotalLength;
        public byte[][] Chunks = Array.Empty<byte[]>();
        public int ReceivedChunks;
        public int ReceivedBytes;
    }

    internal sealed class NetworkWorldSessionAssets
    {
        public string BlocksTextureName = string.Empty;
        public byte[] BlocksTextureData = Array.Empty<byte>();
        public Texture2D BlocksTexture;
        public bool BlocksTextureLoadFailed;
        public Project AppliedProject;
    }

    public class AnimalSyncMetadata
    {
        public double NextSendTime;
        public double HighPriorityUntil;
        public string BehaviorState = string.Empty;
        public int TargetEntityId;
        public string HerdName = string.Empty;
        public float LastHealth = 1f;
        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        public int DamageSequence;
        public byte SyncTier;
        public bool AttackOrder;
        public bool FeedOrder;
        public int SimulationSeed;
        public string ShapeshiftTarget = string.Empty;
        // Source: Survivalcraft/Game/ComponentCreatureSounds.cs:ComponentCreatureSounds.PlayIdleSound
        public double LastCreatureSoundTime;
        // Source: Survivalcraft/Game/ComponentHowlBehavior.cs:ComponentHowlBehavior.Update
        public float LastHowlTime;
        public int SoundSequence;
        public bool SoundStateInitialized;
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
        public string ShapeshiftTarget;
    }

    public class RemoteAnimalSyncState
    {
        public byte SyncTier;
        public string BehaviorState;
        public int TargetEntityId;
        public string HerdName;
        public float LastHealth;
        public bool HasHealth;
        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        public int LastDamageSequence;
        public int LastSoundSequence;
        public int LastServerTick;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Velocity;
        public Vector3 Acceleration;
        public Vector3 SmoothedVelocity;
        // Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.Update
        // PresentationPosition is intentionally separate from ComponentBody.Position. A local
        // collision may move a presentation replica for one physics frame, but must never become
        // the baseline used to predict its next host-authoritative transform.
        public Vector3 PresentationPosition;
        public Vector2 LookAngles;
        public bool AttackOrder;
        public bool FeedOrder;
        public bool HasTransform;
        public bool HasSmoothedVelocity;
        public bool HasPresentationPosition;
        public bool PresentationInitialized;
        public float EstimatedSnapshotInterval = 0.1f;
        public float EstimatedDelay;
        public double LastUpdateTime;
        public Vector2? WalkOrder;
        public Vector3? FlyOrder;
        public Vector3? SwimOrder;
        public Vector2 TurnOrder;
        public float JumpOrder;
        public BodyUpdateMessage.BodyItem.MotionFlag MotionFlags;
        public int SimulationSeed;
        public bool SimulationSeedApplied;
        public double? DeathTime;
        public bool LocalDespawnStarted;
        public string ShapeshiftTarget = string.Empty;
    }

    // Source: Survivalcraft/Game/ComponentDiggingCracks.cs:ComponentDiggingCracks.Draw
    internal sealed class RemoteDigPresentation
    {
        public int Sequence;
        public CellFace CellFace;
        public float DisplayProgress;
        public float TargetProgress;
        public double LastUpdateTime;
    }

    #endregion

    // ================================================================
    // ScMultiplayer 主类
    // ================================================================
}

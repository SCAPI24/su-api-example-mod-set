namespace ScMultiplayer
{
    internal enum NetworkIngressCommandKind : byte
    {
        None = 0,
        Chat,
        PlayerPosition,
        PlayerPositions,
        PlayerInput,
        PlayerAim,
        PlayerAction,
        TerrainDigRequest,
        TerrainDigResult,
        DigPresentation,
        ModifiedCells,
        TerrainRecovery,
        TerrainChunkSync,
        WorldInfo,
        WorldControlRequest,
        WorldControlResult,
        PlayerProfile,
        PlayerSkinAsset,
        PlayerEquipment,
        EditableDataRequest,
        EditableDataState,
        CircuitSync,
        WorldObjectSync,
        PakWorld,
        PakWorldChunk,
        PakWorldReady,
        PakWorldRepair,
        PlayerHealth,
        KickPlayer,
        Entity,
        BodyUpdate,
        AnimalInteraction,
        AnimalSound,
        MeleeHitResult,
        PickableSync,
        ProjectileSync,
        ExplosionSync,
        ContainerSync,
        Unknown
    }

    internal enum NetworkIngressQueueKind : byte
    {
        None = 0,
        EndOfFrame,
        PriorityInput,
        WorldTransfer,
        TerrainChunk,
        Dispatcher
    }

    // Source: Mod/ScMultiplayer/Control/NetworkMessageRouter.cs:NetworkMessageRouter.Route
    // This immutable value carries correlation data only. The existing Action remains the
    // compatibility execution path, so queue order and game-thread ownership are unchanged.
    internal readonly struct NetworkIngressCommand
    {
        private NetworkIngressCommand(NetworkIngressCommandKind kind, int sourceClientId,
            int sequence, int payloadBytes, long receivedTimestamp,
            NetworkIngressQueueKind queueKind, long enqueuedTimestamp)
        {
            Kind = kind;
            SourceClientId = sourceClientId;
            Sequence = sequence;
            PayloadBytes = payloadBytes;
            ReceivedTimestamp = receivedTimestamp;
            QueueKind = queueKind;
            EnqueuedTimestamp = enqueuedTimestamp;
        }

        public NetworkIngressCommandKind Kind { get; }
        public int SourceClientId { get; }
        public int Sequence { get; }
        public int PayloadBytes { get; }
        public long ReceivedTimestamp { get; }
        public NetworkIngressQueueKind QueueKind { get; }
        public long EnqueuedTimestamp { get; }

        public bool IsValid => Kind != NetworkIngressCommandKind.None &&
            ReceivedTimestamp > 0L;

        public static NetworkIngressCommand Create(int sourceClientId, Message message,
            int payloadBytes, long receivedTimestamp)
        {
            return new NetworkIngressCommand(GetKind(message), sourceClientId,
                GetSequence(message), payloadBytes > 0 ? payloadBytes : 0,
                receivedTimestamp, NetworkIngressQueueKind.None, 0L);
        }

        public NetworkIngressCommand WithQueue(NetworkIngressQueueKind queueKind,
            long enqueuedTimestamp)
        {
            return new NetworkIngressCommand(Kind, SourceClientId, Sequence, PayloadBytes,
                ReceivedTimestamp, queueKind, enqueuedTimestamp);
        }

        private static NetworkIngressCommandKind GetKind(Message message)
        {
            return message switch
            {
                ChatMessage => NetworkIngressCommandKind.Chat,
                GamePlayerPositionMessage => NetworkIngressCommandKind.PlayerPosition,
                GamePlayerPositionsMessage => NetworkIngressCommandKind.PlayerPositions,
                GamePlayerInputMessage => NetworkIngressCommandKind.PlayerInput,
                PlayerAimMessage => NetworkIngressCommandKind.PlayerAim,
                PlayerActionMessage => NetworkIngressCommandKind.PlayerAction,
                TerrainDigRequestMessage => NetworkIngressCommandKind.TerrainDigRequest,
                TerrainDigResultMessage => NetworkIngressCommandKind.TerrainDigResult,
                DigPresentationMessage => NetworkIngressCommandKind.DigPresentation,
                GameModifiedCellsMessage => NetworkIngressCommandKind.ModifiedCells,
                TerrainRecoveryMessage => NetworkIngressCommandKind.TerrainRecovery,
                TerrainChunkSyncMessage => NetworkIngressCommandKind.TerrainChunkSync,
                GameWorldInfoMessage1 => NetworkIngressCommandKind.WorldInfo,
                WorldControlRequestMessage => NetworkIngressCommandKind.WorldControlRequest,
                WorldControlResultMessage => NetworkIngressCommandKind.WorldControlResult,
                PlayerProfileMessage => NetworkIngressCommandKind.PlayerProfile,
                PlayerSkinAssetMessage => NetworkIngressCommandKind.PlayerSkinAsset,
                PlayerEquipmentMessage => NetworkIngressCommandKind.PlayerEquipment,
                EditableDataRequestMessage => NetworkIngressCommandKind.EditableDataRequest,
                EditableDataStateMessage => NetworkIngressCommandKind.EditableDataState,
                CircuitSyncMessage => NetworkIngressCommandKind.CircuitSync,
                WorldObjectSyncMessage => NetworkIngressCommandKind.WorldObjectSync,
                GamePakWorldMessage => NetworkIngressCommandKind.PakWorld,
                GamePakWorldChunkMessage => NetworkIngressCommandKind.PakWorldChunk,
                GamePakWorldReadyMessage => NetworkIngressCommandKind.PakWorldReady,
                GamePakWorldRepairRequestMessage => NetworkIngressCommandKind.PakWorldRepair,
                GamePlayerHealthMessage => NetworkIngressCommandKind.PlayerHealth,
                GameKickPlayerMessage => NetworkIngressCommandKind.KickPlayer,
                EntityMessage => NetworkIngressCommandKind.Entity,
                BodyUpdateMessage => NetworkIngressCommandKind.BodyUpdate,
                AnimalInteractionMessage => NetworkIngressCommandKind.AnimalInteraction,
                AnimalSoundMessage => NetworkIngressCommandKind.AnimalSound,
                MeleeHitResultMessage => NetworkIngressCommandKind.MeleeHitResult,
                PickableSyncMessage => NetworkIngressCommandKind.PickableSync,
                ProjectileSyncMessage => NetworkIngressCommandKind.ProjectileSync,
                ExplosionSyncMessage => NetworkIngressCommandKind.ExplosionSync,
                ContainerSyncMessage => NetworkIngressCommandKind.ContainerSync,
                _ => NetworkIngressCommandKind.Unknown
            };
        }

        private static int GetSequence(Message message)
        {
            return message switch
            {
                PlayerActionMessage action => action.Sequence,
                PlayerAimMessage aim => aim.Sequence,
                DigPresentationMessage dig => dig.Sequence,
                _ => 0
            };
        }
    }
}

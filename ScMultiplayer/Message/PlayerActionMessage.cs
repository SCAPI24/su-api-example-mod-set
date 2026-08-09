using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    public enum PlayerActionType : byte
    {
        HitRequest,
        Poke,
        InteractRequest,
        LeaveRequest,
        RespawnRequest,
        DropRequest,
        Whistle,
        InteractResult,
        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        JumpRequest,
        // Source: Survivalcraft/Game/SubsystemMusketBlockBehavior.cs:SubsystemMusketBlockBehavior.OnAim
        MusketFire
    }

    [Serializable]
    public class PlayerActionMessage : Message
    {
        public PlayerActionType Action;
        public int PlayerIndex;
        public int Sequence;
        public Ray3 HitRay;
        public int ActiveSlotIndex = -1;
        public int ItemValue;
        public int ItemCount;
        public int DropCount;
        // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.DropSlotItems
        // The item count spawned on the ground can differ from the count requested for removal.
        // ComponentCreativeInventory, for example, clears an all-items slot but returns one item.
        public int RemoveCount;
        // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
        public int[] InventorySlotValues = Array.Empty<int>();
        public int[] InventorySlotCounts = Array.Empty<int>();
        public bool HasInventoryDelta;
        public int[] InventorySlotIndices = Array.Empty<int>();
        public int[] InventoryBaseValues = Array.Empty<int>();
        public int[] InventoryBaseCounts = Array.Empty<int>();
        public Vector3 Position;
        public Vector3 Velocity;
        public bool HasTerrainPrediction;
        public int RequestId;
        public Point3 Cell;
        public int ExpectedValue;
        public int PredictedValue;
        public bool Accepted;
        public int AuthoritativeValue;
        public int ServerTick;

        public PlayerActionMessage()
        {
        }

        public PlayerActionMessage(PlayerActionType action, int playerIndex,
            int sequence, Ray3 hitRay, int activeSlotIndex = -1,
            int itemValue = 0, int itemCount = 0)
        {
            Action = action;
            PlayerIndex = playerIndex;
            Sequence = sequence;
            HitRay = hitRay;
            ActiveSlotIndex = activeSlotIndex;
            ItemValue = itemValue;
            ItemCount = itemCount;
        }

        protected override void Read(SuReader reader)
        {
            Action = (PlayerActionType)reader.ReadByte();
            PlayerIndex = reader.ReadInt32();
            Sequence = reader.ReadInt32();
            if (Action == PlayerActionType.HitRequest ||
                Action == PlayerActionType.InteractRequest)
                HitRay = reader.ReadRay3(reader);
            if (Action == PlayerActionType.InteractRequest ||
                Action == PlayerActionType.DropRequest)
            {
                ActiveSlotIndex = reader.ReadInt32();
                ItemValue = reader.ReadInt32();
                ItemCount = reader.ReadInt32();
                if (Action == PlayerActionType.DropRequest)
                {
                    DropCount = reader.ReadInt32();
                    RemoveCount = reader.ReadInt32();
                    // Source: ScMultiplayer.UpdateLocalDropRequests
                    // Fences delayed pre-drop equipment snapshots on the host.
                    RequestId = reader.ReadInt32();
                    HasInventoryDelta = reader.ReadBoolean();
                    if (HasInventoryDelta)
                    {
                        int deltaCount = reader.ReadPackedInt32();
                        InventorySlotIndices = new int[deltaCount];
                        InventoryBaseValues = new int[deltaCount];
                        InventoryBaseCounts = new int[deltaCount];
                        InventorySlotValues = new int[deltaCount];
                        InventorySlotCounts = new int[deltaCount];
                        for (int i = 0; i < deltaCount; i++)
                        {
                            InventorySlotIndices[i] = reader.ReadInt32();
                            InventoryBaseValues[i] = reader.ReadInt32();
                            InventoryBaseCounts[i] = reader.ReadInt32();
                            InventorySlotValues[i] = reader.ReadInt32();
                            InventorySlotCounts[i] = reader.ReadInt32();
                        }
                    }
                    else
                    {
                        int slotsCount = reader.ReadPackedInt32();
                        InventorySlotValues = new int[slotsCount];
                        InventorySlotCounts = new int[slotsCount];
                        for (int i = 0; i < slotsCount; i++)
                        {
                            InventorySlotValues[i] = reader.ReadInt32();
                            InventorySlotCounts[i] = reader.ReadInt32();
                        }
                    }
                }
            }
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Place
            if (Action == PlayerActionType.InteractRequest)
            {
                HasTerrainPrediction = reader.ReadBoolean();
                if (HasTerrainPrediction)
                {
                    RequestId = reader.ReadInt32();
                    Cell = reader.ReadPoint3();
                    ExpectedValue = reader.ReadInt32();
                    PredictedValue = reader.ReadInt32();
                }
            }
            if (Action == PlayerActionType.InteractResult)
            {
                RequestId = reader.ReadInt32();
                Cell = reader.ReadPoint3();
                Accepted = reader.ReadBoolean();
                AuthoritativeValue = reader.ReadInt32();
                ServerTick = reader.ReadInt32();
            }
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            if (Action == PlayerActionType.JumpRequest)
                ServerTick = reader.ReadInt32();
            if (Action == PlayerActionType.RespawnRequest ||
                Action == PlayerActionType.DropRequest ||
                Action == PlayerActionType.Whistle ||
                Action == PlayerActionType.MusketFire)
                Position = reader.ReadVector3(reader);
            if (Action == PlayerActionType.DropRequest ||
                Action == PlayerActionType.MusketFire)
                Velocity = reader.ReadVector3(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Action);
            writer.WriteInt32(PlayerIndex);
            writer.WriteInt32(Sequence);
            if (Action == PlayerActionType.HitRequest ||
                Action == PlayerActionType.InteractRequest)
                writer.WriteRay3(writer, HitRay);
            if (Action == PlayerActionType.InteractRequest ||
                Action == PlayerActionType.DropRequest)
            {
                writer.WriteInt32(ActiveSlotIndex);
                writer.WriteInt32(ItemValue);
                writer.WriteInt32(ItemCount);
                if (Action == PlayerActionType.DropRequest)
                {
                    writer.WriteInt32(DropCount);
                    writer.WriteInt32(RemoveCount);
                    writer.WriteInt32(RequestId);
                    writer.WriteBoolean(HasInventoryDelta);
                    if (HasInventoryDelta)
                    {
                        int deltaCount = Math.Min(InventorySlotIndices?.Length ?? 0,
                            Math.Min(InventoryBaseValues?.Length ?? 0,
                                Math.Min(InventoryBaseCounts?.Length ?? 0,
                                    Math.Min(InventorySlotValues?.Length ?? 0,
                                        InventorySlotCounts?.Length ?? 0))));
                        writer.WritePackedInt32(deltaCount);
                        for (int i = 0; i < deltaCount; i++)
                        {
                            writer.WriteInt32(InventorySlotIndices[i]);
                            writer.WriteInt32(InventoryBaseValues[i]);
                            writer.WriteInt32(InventoryBaseCounts[i]);
                            writer.WriteInt32(InventorySlotValues[i]);
                            writer.WriteInt32(InventorySlotCounts[i]);
                        }
                    }
                    else
                    {
                        int slotsCount = Math.Min(InventorySlotValues?.Length ?? 0,
                            InventorySlotCounts?.Length ?? 0);
                        writer.WritePackedInt32(slotsCount);
                        for (int i = 0; i < slotsCount; i++)
                        {
                            writer.WriteInt32(InventorySlotValues[i]);
                            writer.WriteInt32(InventorySlotCounts[i]);
                        }
                    }
                }
            }
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Place
            if (Action == PlayerActionType.InteractRequest)
            {
                writer.WriteBoolean(HasTerrainPrediction);
                if (HasTerrainPrediction)
                {
                    writer.WriteInt32(RequestId);
                    writer.WritePoint3(Cell);
                    writer.WriteInt32(ExpectedValue);
                    writer.WriteInt32(PredictedValue);
                }
            }
            if (Action == PlayerActionType.InteractResult)
            {
                writer.WriteInt32(RequestId);
                writer.WritePoint3(Cell);
                writer.WriteBoolean(Accepted);
                writer.WriteInt32(AuthoritativeValue);
                writer.WriteInt32(ServerTick);
            }
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            if (Action == PlayerActionType.JumpRequest)
                writer.WriteInt32(ServerTick);
            if (Action == PlayerActionType.RespawnRequest ||
                Action == PlayerActionType.DropRequest ||
                Action == PlayerActionType.Whistle ||
                Action == PlayerActionType.MusketFire)
                writer.WriteVector3(writer, Position);
            if (Action == PlayerActionType.DropRequest ||
                Action == PlayerActionType.MusketFire)
                writer.WriteVector3(writer, Velocity);
        }
    }
}

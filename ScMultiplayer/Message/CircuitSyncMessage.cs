using Comms;
using Engine;
using System;
using System.Collections.Generic;

namespace ScMultiplayer
{
    public enum CircuitSyncStage : byte
    {
        Request = 1,
        EventBatch = 2,
        Clock = 3,
        RecoveryRequest = 4,
        SnapshotRequest = 5,
        Snapshot = 6,
        Fence = 7,
        ButtonReleaseConfirm = 8,
        HashReport = 9,
        RepairPlan = 10,
        CheckpointRequest = 11
    }

    public enum CircuitOperationType : byte
    {
        Interact = 1,
        Pressure = 2,
        Switch = 3,
        Rotate = 4,
        FurnitureSwitch = 5
    }

    public sealed class CircuitEventRecord
    {
        public int Sequence;
        public int HostCircuitStep;
        public Point3 Point;
        public Ray3 RequestRay;
        public byte MountingFace;
        public CircuitOperationType Operation;
        public byte Value;
        public long RequiredTerrainSequence;
        public double CreatedTime;
    }

    public sealed class CircuitStateRecord
    {
        public Point3 Point;
        public byte MountingFace;
        public sbyte VoltageLevel;
        public byte ButtonPhase;
        public byte RemainingSteps;
        public int RelatedSequence;
        public byte StateFlags;
        public int NextSimulationSteps;
        public int ElementTypeCode;
        public sbyte AuxiliaryVoltageLevel;
        public readonly List<CircuitScheduledVoltageRecord> ScheduledVoltages =
            new List<CircuitScheduledVoltageRecord>();
    }

    public sealed class CircuitScheduledVoltageRecord
    {
        public int RemainingSteps;
        public sbyte VoltageLevel;
    }

    // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
    [Serializable]
    public sealed class CircuitSyncMessage : Message
    {
        private const int MaximumEvents = 40;
        private const int MaximumStates = 40;

        public CircuitSyncStage Stage;
        public int Epoch;
        public int RequestId;
        public int ClientStep;
        public Point3 Point;
        public Ray3 RequestRay;
        public byte MountingFace;
        public CircuitOperationType Operation;
        public byte Value;
        public int BaseSequence;
        public int BaseHostCircuitStep;
        public long RequiredTerrainSequence;
        public readonly List<CircuitEventRecord> Events = new List<CircuitEventRecord>();
        public int ServerStep;
        public int HostCircuitStep;
        public int LastSequence;
        public int HashStep;
        public uint StateHash;
        public int NextHashStep;
        public int ExpectedSequence;
        public int SnapshotPartIndex;
        public int SnapshotPartCount;
        public int TimelineGeneration;
        public int SafeThroughHostCircuitStep;
        public bool IsPaused;
        public bool HashAvailable;
        public readonly List<CircuitStateRecord> States = new List<CircuitStateRecord>();

        protected override void Read(SuReader reader)
        {
            Stage = (CircuitSyncStage)reader.ReadByte();
            switch (Stage)
            {
                case CircuitSyncStage.Request:
                    RequestId = reader.ReadPackedInt32(1, int.MaxValue);
                    ClientStep = reader.ReadPackedInt32();
                    Point = ReadSignedPoint3(reader);
                    RequestRay = reader.ReadRay3(reader);
                    ReadOperation(reader, out MountingFace, out Operation, out Value);
                    break;
                case CircuitSyncStage.EventBatch:
                    ReadEventBatch(reader);
                    break;
                case CircuitSyncStage.Clock:
                    Epoch = reader.ReadPackedInt32(1, int.MaxValue);
                    ServerStep = reader.ReadPackedInt32();
                    HostCircuitStep = reader.ReadPackedInt32();
                    LastSequence = reader.ReadPackedInt32();
                    RequiredTerrainSequence = reader.ReadInt64();
                    HashStep = reader.ReadPackedInt32();
                    StateHash = reader.ReadUInt32();
                    NextHashStep = reader.ReadPackedInt32();
                    break;
                case CircuitSyncStage.RecoveryRequest:
                case CircuitSyncStage.SnapshotRequest:
                    Epoch = reader.ReadPackedInt32(1, int.MaxValue);
                    ExpectedSequence = reader.ReadPackedInt32();
                    break;
                case CircuitSyncStage.Snapshot:
                    ReadSnapshot(reader);
                    break;
                case CircuitSyncStage.Fence:
                    ReadFence(reader);
                    break;
                case CircuitSyncStage.ButtonReleaseConfirm:
                    Epoch = reader.ReadPackedInt32(1, int.MaxValue);
                    HostCircuitStep = reader.ReadPackedInt32();
                    ExpectedSequence = reader.ReadPackedInt32(1, int.MaxValue);
                    Point = ReadSignedPoint3(reader);
                    MountingFace = reader.ReadByte();
                    if (MountingFace > 5)
                        throw new InvalidOperationException("Invalid button mounting face.");
                    break;
                case CircuitSyncStage.HashReport:
                    Epoch = reader.ReadPackedInt32(1, int.MaxValue);
                    HashStep = reader.ReadPackedInt32();
                    byte hashAvailable = reader.ReadByte();
                    if (hashAvailable > 1)
                        throw new InvalidOperationException("Invalid circuit hash availability.");
                    HashAvailable = hashAvailable != 0;
                    StateHash = HashAvailable ? reader.ReadUInt32() : 0u;
                    break;
                case CircuitSyncStage.RepairPlan:
                    Epoch = reader.ReadPackedInt32(1, int.MaxValue);
                    HashStep = reader.ReadPackedInt32();
                    HostCircuitStep = reader.ReadPackedInt32();
                    break;
                case CircuitSyncStage.CheckpointRequest:
                    Epoch = reader.ReadPackedInt32(1, int.MaxValue);
                    break;
                default:
                    throw new InvalidOperationException("Invalid circuit sync stage.");
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Stage);
            switch (Stage)
            {
                case CircuitSyncStage.Request:
                    writer.WritePackedInt32(RequestId);
                    writer.WritePackedInt32(ClientStep);
                    WriteSignedPoint3(writer, Point);
                    writer.WriteRay3(writer, RequestRay);
                    WriteOperation(writer, MountingFace, Operation, Value);
                    break;
                case CircuitSyncStage.EventBatch:
                    WriteEventBatch(writer);
                    break;
                case CircuitSyncStage.Clock:
                    writer.WritePackedInt32(Epoch);
                    writer.WritePackedInt32(ServerStep);
                    writer.WritePackedInt32(HostCircuitStep);
                    writer.WritePackedInt32(LastSequence);
                    writer.WriteInt64(RequiredTerrainSequence);
                    writer.WritePackedInt32(HashStep);
                    writer.WriteUInt32(StateHash);
                    writer.WritePackedInt32(NextHashStep);
                    break;
                case CircuitSyncStage.RecoveryRequest:
                case CircuitSyncStage.SnapshotRequest:
                    writer.WritePackedInt32(Epoch);
                    writer.WritePackedInt32(ExpectedSequence);
                    break;
                case CircuitSyncStage.Snapshot:
                    WriteSnapshot(writer);
                    break;
                case CircuitSyncStage.Fence:
                    WriteFence(writer);
                    break;
                case CircuitSyncStage.ButtonReleaseConfirm:
                    if (ExpectedSequence <= 0 || MountingFace > 5)
                        throw new InvalidOperationException("Invalid button release confirmation.");
                    writer.WritePackedInt32(Epoch);
                    writer.WritePackedInt32(HostCircuitStep);
                    writer.WritePackedInt32(ExpectedSequence);
                    WriteSignedPoint3(writer, Point);
                    writer.WriteByte(MountingFace);
                    break;
                case CircuitSyncStage.HashReport:
                    writer.WritePackedInt32(Epoch);
                    writer.WritePackedInt32(HashStep);
                    writer.WriteByte(HashAvailable ? (byte)1 : (byte)0);
                    if (HashAvailable) writer.WriteUInt32(StateHash);
                    break;
                case CircuitSyncStage.RepairPlan:
                    writer.WritePackedInt32(Epoch);
                    writer.WritePackedInt32(HashStep);
                    writer.WritePackedInt32(HostCircuitStep);
                    break;
                case CircuitSyncStage.CheckpointRequest:
                    writer.WritePackedInt32(Epoch);
                    break;
                default:
                    throw new InvalidOperationException("Invalid circuit sync stage.");
            }
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
        // CircuitSynchronizer.SendFence
        private void ReadFence(SuReader reader)
        {
            Epoch = reader.ReadPackedInt32(1, int.MaxValue);
            TimelineGeneration = reader.ReadPackedInt32(1, int.MaxValue);
            ServerStep = reader.ReadPackedInt32();
            HostCircuitStep = reader.ReadPackedInt32();
            SafeThroughHostCircuitStep = reader.ReadPackedInt32();
            LastSequence = reader.ReadPackedInt32();
            RequiredTerrainSequence = reader.ReadInt64();
            NextHashStep = reader.ReadPackedInt32();
            byte paused = reader.ReadByte();
            if (paused > 1)
                throw new InvalidOperationException("Invalid circuit fence pause state.");
            IsPaused = paused != 0;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
        // CircuitSynchronizer.SendFence
        private void WriteFence(SuWriter writer)
        {
            if (TimelineGeneration <= 0)
                throw new InvalidOperationException("Invalid circuit timeline generation.");
            writer.WritePackedInt32(Epoch);
            writer.WritePackedInt32(TimelineGeneration);
            writer.WritePackedInt32(ServerStep);
            writer.WritePackedInt32(HostCircuitStep);
            writer.WritePackedInt32(SafeThroughHostCircuitStep);
            writer.WritePackedInt32(LastSequence);
            writer.WriteInt64(RequiredTerrainSequence);
            writer.WritePackedInt32(NextHashStep);
            writer.WriteByte(IsPaused ? (byte)1 : (byte)0);
        }

        // Source: Mod/ScMultiplayer/Message/SyncBatchMessage.cs:SyncBatchMessage.Read
        private void ReadEventBatch(SuReader reader)
        {
            Epoch = reader.ReadPackedInt32(1, int.MaxValue);
            ServerStep = reader.ReadPackedInt32();
            HostCircuitStep = reader.ReadPackedInt32();
            BaseSequence = reader.ReadPackedInt32(1, int.MaxValue);
            BaseHostCircuitStep = reader.ReadPackedInt32();
            RequiredTerrainSequence = reader.ReadInt64();
            int count = reader.ReadPackedInt32(1, MaximumEvents);
            Events.Clear();
            Point3 previous = default;
            for (int i = 0; i < count; i++)
            {
                int stepDelta = reader.ReadPackedInt32(0, 10000);
                Point3 point = new Point3(
                    previous.X + ReadSignedPackedInt32(reader),
                    previous.Y + ReadSignedPackedInt32(reader),
                    previous.Z + ReadSignedPackedInt32(reader));
                ReadOperation(reader, out byte face, out CircuitOperationType operation,
                    out byte value);
                Events.Add(new CircuitEventRecord
                {
                    Sequence = BaseSequence + i,
                    HostCircuitStep = BaseHostCircuitStep + stepDelta,
                    Point = point,
                    MountingFace = face,
                    Operation = operation,
                    Value = value
                });
                previous = point;
            }
        }

        // Source: Mod/ScMultiplayer/Message/SyncBatchMessage.cs:SyncBatchMessage.Write
        private void WriteEventBatch(SuWriter writer)
        {
            if (Events.Count < 1 || Events.Count > MaximumEvents)
                throw new InvalidOperationException("Invalid circuit event count.");
            writer.WritePackedInt32(Epoch);
            writer.WritePackedInt32(ServerStep);
            writer.WritePackedInt32(HostCircuitStep);
            writer.WritePackedInt32(BaseSequence);
            writer.WritePackedInt32(BaseHostCircuitStep);
            writer.WriteInt64(RequiredTerrainSequence);
            writer.WritePackedInt32(Events.Count);
            Point3 previous = default;
            foreach (CircuitEventRecord item in Events)
            {
                writer.WritePackedInt32(item.HostCircuitStep - BaseHostCircuitStep);
                WriteSignedPackedInt32(writer, item.Point.X - previous.X);
                WriteSignedPackedInt32(writer, item.Point.Y - previous.Y);
                WriteSignedPackedInt32(writer, item.Point.Z - previous.Z);
                WriteOperation(writer, item.MountingFace, item.Operation, item.Value);
                previous = item.Point;
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_electricElements
        private void ReadSnapshot(SuReader reader)
        {
            Epoch = reader.ReadPackedInt32(1, int.MaxValue);
            HostCircuitStep = reader.ReadPackedInt32();
            LastSequence = reader.ReadPackedInt32();
            SnapshotPartIndex = reader.ReadPackedInt32(0, 4095);
            SnapshotPartCount = reader.ReadPackedInt32(1, 4096);
            int count = reader.ReadPackedInt32(0, MaximumStates);
            States.Clear();
            Point3 previous = default;
            for (int i = 0; i < count; i++)
            {
                Point3 point = new Point3(
                    previous.X + ReadSignedPackedInt32(reader),
                    previous.Y + ReadSignedPackedInt32(reader),
                    previous.Z + ReadSignedPackedInt32(reader));
                byte mountingFace = reader.ReadByte();
                sbyte voltageLevel = unchecked((sbyte)reader.ReadByte());
                byte timing = reader.ReadByte();
                byte phase = (byte)(timing & 3);
                byte remaining = (byte)(timing >> 2);
                if (phase > 2 || remaining > 15 || (phase == 0 && remaining != 0))
                    throw new InvalidOperationException("Invalid button snapshot timing.");
                var state = new CircuitStateRecord
                {
                    Point = point,
                    MountingFace = mountingFace,
                    VoltageLevel = voltageLevel,
                    ButtonPhase = phase,
                    RemainingSteps = remaining,
                    RelatedSequence = phase != 0
                        ? reader.ReadPackedInt32(0, int.MaxValue)
                        : 0,
                    StateFlags = reader.ReadByte(),
                    NextSimulationSteps = reader.ReadPackedInt32(0, 10000),
                    ElementTypeCode = reader.ReadPackedInt32(),
                    AuxiliaryVoltageLevel = unchecked((sbyte)reader.ReadByte())
                };
                int scheduledCount = reader.ReadPackedInt32(0, 300);
                for (int j = 0; j < scheduledCount; j++)
                {
                    state.ScheduledVoltages.Add(new CircuitScheduledVoltageRecord
                    {
                        RemainingSteps = reader.ReadPackedInt32(0, 10000),
                        VoltageLevel = unchecked((sbyte)reader.ReadByte())
                    });
                }
                States.Add(state);
                previous = point;
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.m_electricElements
        private void WriteSnapshot(SuWriter writer)
        {
            if (States.Count > MaximumStates)
                throw new InvalidOperationException("Invalid circuit state count.");
            writer.WritePackedInt32(Epoch);
            writer.WritePackedInt32(HostCircuitStep);
            writer.WritePackedInt32(LastSequence);
            writer.WritePackedInt32(SnapshotPartIndex);
            writer.WritePackedInt32(SnapshotPartCount);
            writer.WritePackedInt32(States.Count);
            Point3 previous = default;
            foreach (CircuitStateRecord item in States)
            {
                WriteSignedPackedInt32(writer, item.Point.X - previous.X);
                WriteSignedPackedInt32(writer, item.Point.Y - previous.Y);
                WriteSignedPackedInt32(writer, item.Point.Z - previous.Z);
                writer.WriteByte(item.MountingFace);
                writer.WriteByte(unchecked((byte)item.VoltageLevel));
                if (item.ButtonPhase > 2 || item.RemainingSteps > 15 ||
                    (item.ButtonPhase == 0 && item.RemainingSteps != 0))
                    throw new InvalidOperationException("Invalid button snapshot timing.");
                writer.WriteByte((byte)(item.ButtonPhase | (item.RemainingSteps << 2)));
                if (item.ButtonPhase != 0)
                    writer.WritePackedInt32(Math.Max(item.RelatedSequence, 0));
                writer.WriteByte(item.StateFlags);
                writer.WritePackedInt32(MathUtils.Clamp(item.NextSimulationSteps, 0, 10000));
                writer.WritePackedInt32(item.ElementTypeCode);
                writer.WriteByte(unchecked((byte)item.AuxiliaryVoltageLevel));
                if (item.ScheduledVoltages.Count > 300)
                    throw new InvalidOperationException("Too many scheduled circuit voltages.");
                writer.WritePackedInt32(item.ScheduledVoltages.Count);
                foreach (CircuitScheduledVoltageRecord scheduled in item.ScheduledVoltages)
                {
                    writer.WritePackedInt32(MathUtils.Clamp(
                        scheduled.RemainingSteps, 0, 10000));
                    writer.WriteByte(unchecked((byte)scheduled.VoltageLevel));
                }
                previous = item.Point;
            }
        }

        private static void ReadOperation(SuReader reader, out byte face,
            out CircuitOperationType operation, out byte value)
        {
            byte packed = reader.ReadByte();
            face = (byte)(packed & 7);
            operation = (CircuitOperationType)(packed >> 3);
            if (face > 5 || !IsValidOperation(operation))
                throw new InvalidOperationException("Invalid circuit operation.");
            value = operation == CircuitOperationType.Pressure ||
                operation == CircuitOperationType.Switch
                ? reader.ReadByte()
                : (byte)0;
            if (operation == CircuitOperationType.Switch && value > 1)
                throw new InvalidOperationException("Invalid switch state.");
        }

        private static void WriteOperation(SuWriter writer, byte face,
            CircuitOperationType operation, byte value)
        {
            if (face > 5 || !IsValidOperation(operation))
                throw new InvalidOperationException("Invalid circuit operation.");
            writer.WriteByte((byte)(((byte)operation << 3) | face));
            if (operation == CircuitOperationType.Pressure ||
                operation == CircuitOperationType.Switch)
                writer.WriteByte(operation == CircuitOperationType.Switch
                    ? (value > 0 ? (byte)1 : (byte)0)
                    : value);
        }

        private static bool IsValidOperation(CircuitOperationType operation)
        {
            return operation == CircuitOperationType.Interact ||
                operation == CircuitOperationType.Pressure ||
                operation == CircuitOperationType.Switch ||
                operation == CircuitOperationType.Rotate ||
                operation == CircuitOperationType.FurnitureSwitch;
        }

        private static Point3 ReadSignedPoint3(SuReader reader) => new Point3(
            ReadSignedPackedInt32(reader), ReadSignedPackedInt32(reader),
            ReadSignedPackedInt32(reader));

        private static void WriteSignedPoint3(SuWriter writer, Point3 point)
        {
            WriteSignedPackedInt32(writer, point.X);
            WriteSignedPackedInt32(writer, point.Y);
            WriteSignedPackedInt32(writer, point.Z);
        }

        private static int ReadSignedPackedInt32(SuReader reader)
        {
            uint value = unchecked((uint)reader.ReadPackedInt32());
            return unchecked((int)((value >> 1) ^ (uint)-(int)(value & 1)));
        }

        private static void WriteSignedPackedInt32(SuWriter writer, int value)
        {
            uint packed = unchecked((uint)((value << 1) ^ (value >> 31)));
            writer.WritePackedInt32(unchecked((int)packed));
        }
    }
}

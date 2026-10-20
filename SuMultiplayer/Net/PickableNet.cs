using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using PE = SuMultiplayer.Protocol.Entities;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// EntityAction（与 <c>Protocol/entities.proto</c> 的 <c>EntityAction</c> 数值一一对应）。
    /// </summary>
    public static class EntityActions
    {
        public const int Add = 0;
        public const int Update = 1;
        public const int Remove = 2;

        public static bool IsKnown(int action) => action >= Add && action <= Remove;

        public static string Name(int action)
        {
            switch (action)
            {
                case Add: return "add";
                case Update: return "update";
                case Remove: return "remove";
                default: return "action-" + action;
            }
        }
    }

    /// <summary>
    /// 实体复制的边界（发送预算 / 快照上限 / 变化判据）。**唯一事实来源**：
    /// 生产模块与 P:\tmp\tsverify 的自检都读这几个常量，避免两边各写一套。
    /// </summary>
    public static class EntityReplicationLimits
    {
        /// <summary>一条 EntityState 里最多多少条拾取物记录。</summary>
        public const int MaxPickableRecordsPerMessage = 128;
        /// <summary>一条 EntityState 里最多多少条投射物记录。</summary>
        public const int MaxProjectileRecordsPerMessage = 64;
        /// <summary>一个 32Hz 脉冲里最多发几条 EntityState（其余留到下一拍，绝不丢）。</summary>
        public const int MaxStateMessagesPerPulse = 2;
        /// <summary>主机同时跟踪的拾取物 / 投射物上限（超出按丢弃计数并限流报日志）。</summary>
        public const int MaxTrackedPickables = 4096;
        public const int MaxTrackedProjectiles = 2048;
        /// <summary>待发送队列上限（超过时丢最旧的 UPDATE，ADD/REMOVE 优先保留）。</summary>
        public const int MaxPendingPickableChanges = 4096;
        public const int MaxPendingProjectileChanges = 2048;
        /// <summary>一次完整快照里最多多少条记录（超出 complete=false）。</summary>
        public const int MaxSnapshotPickables = 512;
        public const int MaxSnapshotProjectiles = 256;
        /// <summary>位置 / 速度变化判据（平方距离；避免浮点噪声变成网络流量）。</summary>
        public const float PositionEpsilonSquared = 0.0009f;   // 3 cm
        public const float VelocityEpsilonSquared = 0.0025f;   // 5 cm/s
        /// <summary>坐标 / 速度 / 角速度的合法性上限（非法载荷整条拒绝，不夹紧冒充）。</summary>
        public const float MaxCoordinate = 100000f;
        public const float MaxSpeed = 10000f;
        public const float MaxAngularSpeed = 1000f;
        /// <summary>网络 id 上限（主机单调分配；0 永远非法）。</summary>
        public const int MaxNetworkId = 1000000;
    }

    /// <summary>
    /// 一条拾取物状态（纯值）。
    ///
    /// Source: Mod/ScMultiplayer/Message/PickableSyncMessage.cs:PickableSyncMessage
    ///         （Action / Id / Value / Count / Position / Velocity / FlyToPosition / StuckMatrix）
    /// Source: Survivalcraft/Game/Pickable.cs:Pickable
    /// </summary>
    public readonly struct PickableStateData
    {
        public PickableStateData(int id, int action, int value, int count, Vector3 position,
            Vector3 velocity, bool hasStuckMatrix, Vector3 stuckPosition, Quaternion stuckRotation,
            bool hasFlyTo, Vector3 flyTo, int collectorClientId)
        {
            Id = id;
            Action = action;
            Value = value;
            Count = count;
            Position = position;
            Velocity = velocity;
            HasStuckMatrix = hasStuckMatrix;
            StuckPosition = stuckPosition;
            StuckRotation = stuckRotation;
            HasFlyTo = hasFlyTo;
            FlyTo = flyTo;
            CollectorClientId = collectorClientId;
        }

        public int Id { get; }
        public int Action { get; }
        public int Value { get; }
        public int Count { get; }
        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
        public bool HasStuckMatrix { get; }
        public Vector3 StuckPosition { get; }
        public Quaternion StuckRotation { get; }
        public bool HasFlyTo { get; }
        public Vector3 FlyTo { get; }
        /// <summary>-1 = 不是拾取导致的移除；否则是拾取者（主机传输 id）。</summary>
        public int CollectorClientId { get; }

        public PickableStateData WithAction(int action) =>
            new PickableStateData(Id, action, Value, Count, Position, Velocity, HasStuckMatrix,
                StuckPosition, StuckRotation, HasFlyTo, FlyTo, CollectorClientId);

        public PickableStateData WithCollector(int collectorClientId) =>
            new PickableStateData(Id, Action, Value, Count, Position, Velocity, HasStuckMatrix,
                StuckPosition, StuckRotation, HasFlyTo, FlyTo, collectorClientId);

        /// <summary>
        /// 变化判据：位置 / 速度超过阈值，或值 / 数量 / 贴附矩阵 / 磁吸目标变化。
        /// 变化驱动 + 32Hz 脉冲合并就靠逐条比较。
        /// </summary>
        public bool EqualsState(in PickableStateData other) =>
            Id == other.Id && Action == other.Action && Value == other.Value &&
            Count == other.Count && CollectorClientId == other.CollectorClientId &&
            Vector3.DistanceSquared(Position, other.Position) <=
                EntityReplicationLimits.PositionEpsilonSquared &&
            Vector3.DistanceSquared(Velocity, other.Velocity) <=
                EntityReplicationLimits.VelocityEpsilonSquared &&
            HasStuckMatrix == other.HasStuckMatrix &&
            (!HasStuckMatrix ||
                (Vector3.DistanceSquared(StuckPosition, other.StuckPosition) <=
                     EntityReplicationLimits.PositionEpsilonSquared &&
                 QuaternionsClose(StuckRotation, other.StuckRotation))) &&
            HasFlyTo == other.HasFlyTo &&
            (!HasFlyTo || Vector3.DistanceSquared(FlyTo, other.FlyTo) <=
                EntityReplicationLimits.PositionEpsilonSquared);

        public string Describe() => "pickable id=" + Id + " " + EntityActions.Name(Action) +
            " value=" + Value + " count=" + Count + " pos=(" + Position.X.ToString("F2") + "," +
            Position.Y.ToString("F2") + "," + Position.Z.ToString("F2") + ")" +
            (CollectorClientId >= 0 ? " collector=" + CollectorClientId : string.Empty);

        internal static bool QuaternionsClose(Quaternion left, Quaternion right)
        {
            // q 与 -q 表示同一旋转。
            float dot = left.X * right.X + left.Y * right.Y + left.Z * right.Z +
                left.W * right.W;
            return MathUtils.Abs(dot) >= 0.9999f;
        }
    }

    /// <summary>
    /// 一条投射物状态（纯值）。
    ///
    /// Source: Mod/ScMultiplayer/Message/ProjectileSyncMessage.cs:ProjectileSyncMessage
    ///         （ProjectileId / Action / Value / Position / Velocity / AngularVelocity /
    ///          TrailOffset / OwnerEntityId / IsFireProjectile）
    /// Source: Survivalcraft/Game/Projectile.cs:Projectile
    /// </summary>
    public readonly struct ProjectileStateData
    {
        public ProjectileStateData(int id, int action, int value, Vector3 position,
            Vector3 velocity, Vector3 rotation, Vector3 angularVelocity, int ownerClientId,
            bool stuck, bool incendiary, Vector3 trailOffset)
        {
            Id = id;
            Action = action;
            Value = value;
            Position = position;
            Velocity = velocity;
            Rotation = rotation;
            AngularVelocity = angularVelocity;
            OwnerClientId = ownerClientId;
            Stuck = stuck;
            Incendiary = incendiary;
            TrailOffset = trailOffset;
        }

        public int Id { get; }
        public int Action { get; }
        public int Value { get; }
        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
        public Vector3 Rotation { get; }
        public Vector3 AngularVelocity { get; }
        public int OwnerClientId { get; }
        public bool Stuck { get; }
        public bool Incendiary { get; }
        public Vector3 TrailOffset { get; }

        public ProjectileStateData WithAction(int action) =>
            new ProjectileStateData(Id, action, Value, Position, Velocity, Rotation,
                AngularVelocity, OwnerClientId, Stuck, Incendiary, TrailOffset);

        public bool EqualsState(in ProjectileStateData other) =>
            Id == other.Id && Action == other.Action && Value == other.Value &&
            OwnerClientId == other.OwnerClientId && Stuck == other.Stuck &&
            Incendiary == other.Incendiary &&
            Vector3.DistanceSquared(Position, other.Position) <=
                EntityReplicationLimits.PositionEpsilonSquared &&
            Vector3.DistanceSquared(Velocity, other.Velocity) <=
                EntityReplicationLimits.VelocityEpsilonSquared &&
            Vector3.DistanceSquared(Rotation, other.Rotation) <=
                EntityReplicationLimits.VelocityEpsilonSquared &&
            Vector3.DistanceSquared(AngularVelocity, other.AngularVelocity) <=
                EntityReplicationLimits.VelocityEpsilonSquared &&
            Vector3.DistanceSquared(TrailOffset, other.TrailOffset) <=
                EntityReplicationLimits.PositionEpsilonSquared;

        public string Describe() => "projectile id=" + Id + " " + EntityActions.Name(Action) +
            " value=" + Value + " pos=(" + Position.X.ToString("F2") + "," +
            Position.Y.ToString("F2") + "," + Position.Z.ToString("F2") + ") owner=" +
            OwnerClientId + (Stuck ? " stuck" : string.Empty);
    }

    /// <summary>一条增量批次（模块 → 编码器；自检也直接构造它）。</summary>
    public sealed class EntityStateData
    {
        public EntityStateData(uint version, long serverTick)
        {
            Version = version;
            ServerTick = serverTick;
        }

        public uint Version { get; }
        public long ServerTick { get; }
        public List<PickableStateData> Pickables { get; } = new List<PickableStateData>();
        public List<ProjectileStateData> Projectiles { get; } = new List<ProjectileStateData>();
    }

    /// <summary>一次完整快照（加入 / 重连 / 主动 resync）。</summary>
    public sealed class EntitySnapshotData
    {
        public EntitySnapshotData(uint snapshotId, long serverTick, bool complete,
            uint stateVersion = 0)
        {
            SnapshotId = snapshotId;
            ServerTick = serverTick;
            Complete = complete;
            StateVersion = stateVersion;
        }

        public uint SnapshotId { get; }
        public long ServerTick { get; }
        public bool Complete { get; }
        /// <summary>快照生成时的主机 EntityState 版本（0 = 旧主机未提供）。</summary>
        public uint StateVersion { get; }
        public List<PickableStateData> Pickables { get; } = new List<PickableStateData>();
        public List<ProjectileStateData> Projectiles { get; } = new List<ProjectileStateData>();
    }

    /// <summary>
    /// 客户端 → 主机：拾取请求（每连接单调序号）。
    /// Source: Mod/ScMultiplayer/Message/PickableSyncMessage.cs:PickAction.RequestAcquire
    /// </summary>
    public readonly struct PickableAcquireRequestData
    {
        public PickableAcquireRequestData(long sequence, int pickableId, Vector3 position,
            bool hasReach = false, float reach = 0f)
        {
            Sequence = sequence;
            PickableId = pickableId;
            Position = position;
            HasReach = hasReach;
            Reach = reach;
        }

        public long Sequence { get; }
        public int PickableId { get; }
        public Vector3 Position { get; }
        /// <summary>请求是否携带了有效 reach（false = 旧客户端 / 旧载荷）。</summary>
        public bool HasReach { get; }
        /// <summary>请求者的有效交互距离（米）；<see cref="HasReach"/> = false 时无意义。</summary>
        public float Reach { get; }

        public string Describe() => "seq=" + Sequence + " pickable=" + PickableId +
            " pos=(" + Position.X.ToString("F2") + "," + Position.Y.ToString("F2") + "," +
            Position.Z.ToString("F2") + ")";
    }

    /// <summary>
    /// 主机 → 拾取者：回执 + 稀疏背包增量（其余客户端只收 EntityState(REMOVE)）。
    /// Source: Mod/ScMultiplayer/Message/PickableSyncMessage.cs:PickAction.Acquire
    ///         （HasInventoryDelta + SlotIndices/SlotValues/SlotCounts + InventoryVersion）
    /// </summary>
    public readonly struct PickableAcquireResultData
    {
        public PickableAcquireResultData(long sequence, int pickableId, bool accepted,
            string reason, int remainingCount, bool removed, int[] indices, int[] values,
            int[] counts, int inventoryVersion)
        {
            Sequence = sequence;
            PickableId = pickableId;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            RemainingCount = remainingCount;
            Removed = removed;
            Indices = indices ?? Array.Empty<int>();
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            InventoryVersion = inventoryVersion;
        }

        public long Sequence { get; }
        public int PickableId { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public int RemainingCount { get; }
        public bool Removed { get; }
        public int[] Indices { get; }
        public int[] Values { get; }
        public int[] Counts { get; }
        public int InventoryVersion { get; }

        public bool HasInventory => Indices.Length > 0;

        /// <summary>把回执里的稀疏增量折成任务层已有的背包增量类型（客户端直接应用）。</summary>
        public PlayerInventoryDelta ToInventoryDelta(int clientId, int revision)
        {
            return PlayerInventoryDelta.CreateDelta(clientId, revision, InventoryVersion, -1,
                Indices, Values, Counts);
        }
    }

    /// <summary>
    /// 线上编解码 + 严格校验。任何非法载荷都返回 false + 原因（绝不猜默认值 / 半条应用）。
    /// </summary>
    public static class EntityCodec
    {
        // ------------------------------------------------------------ 拾取物

        public static PE.PickableState EncodePickable(in PickableStateData data)
        {
            var message = new PE.PickableState
            {
                Id = data.Id,
                Action = (PE.EntityAction)data.Action,
                Value = data.Value,
                Count = data.Count,
                CollectorClientId = data.CollectorClientId,
                HasFlyTo = data.HasFlyTo,
                HasStuckMatrix = data.HasStuckMatrix
            };
            if (data.Action != EntityActions.Remove)
            {
                message.Position = ToVec3(data.Position);
                message.Velocity = ToVec3(data.Velocity);
            }
            if (data.HasStuckMatrix)
            {
                message.StuckPosition = ToVec3(data.StuckPosition);
                message.StuckRotation = ToQuat(data.StuckRotation);
            }
            if (data.HasFlyTo) message.FlyTo = ToVec3(data.FlyTo);
            return message;
        }

        public static bool TryDecodePickable(PE.PickableState message, out PickableStateData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "pickable-null";
                return false;
            }
            int action = (int)message.Action;
            if (!EntityActions.IsKnown(action))
            {
                reason = "pickable-action-" + action;
                return false;
            }
            if (!IsValidId(message.Id))
            {
                reason = "pickable-id-" + message.Id;
                return false;
            }
            if (message.Count < 0)
            {
                reason = "pickable-count-" + message.Count;
                return false;
            }
            if (action != EntityActions.Remove)
            {
                if (message.Value <= 0)
                {
                    reason = "pickable-value-" + message.Value;
                    return false;
                }
                if (message.Count <= 0)
                {
                    reason = "pickable-count-" + message.Count;
                    return false;
                }
                if (!TryVector(message.Position, "pickable-position", out Vector3 position,
                        out reason))
                    return false;
                if (!TryVector(message.Velocity, "pickable-velocity", out Vector3 velocity,
                        out reason))
                    return false;
                Vector3 flyTo = Vector3.Zero;
                if (message.HasFlyTo &&
                    !TryVector(message.FlyTo, "pickable-fly-to", out flyTo, out reason))
                    return false;
                if (message.HasStuckMatrix)
                {
                    if (!TryVector(message.StuckPosition, "pickable-stuck-position",
                            out Vector3 stuckPosition, out reason))
                        return false;
                    if (!TryQuaternion(message.StuckRotation, out Quaternion stuckRotation,
                            out reason))
                        return false;
                    data = new PickableStateData(message.Id, action, message.Value, message.Count,
                        position, velocity, true, stuckPosition, stuckRotation,
                        message.HasFlyTo, flyTo, message.CollectorClientId);
                    return true;
                }
                data = new PickableStateData(message.Id, action, message.Value, message.Count,
                    position, velocity, false, Vector3.Zero, Quaternion.Identity,
                    message.HasFlyTo, flyTo, message.CollectorClientId);
                return true;
            }
            // REMOVE：位置/值可以省略（主机删除时不再关心它们）。
            data = new PickableStateData(message.Id, action, message.Value, message.Count,
                Vector3.Zero, Vector3.Zero, false, Vector3.Zero, Quaternion.Identity,
                false, Vector3.Zero, message.CollectorClientId);
            return true;
        }

        // ------------------------------------------------------------ 投射物

        public static PE.ProjectileState EncodeProjectile(in ProjectileStateData data)
        {
            var message = new PE.ProjectileState
            {
                Id = data.Id,
                Action = (PE.EntityAction)data.Action,
                Value = data.Value,
                OwnerClientId = data.OwnerClientId,
                Stuck = data.Stuck,
                Incendiary = data.Incendiary
            };
            if (data.Action != EntityActions.Remove)
            {
                message.Position = ToVec3(data.Position);
                message.Velocity = ToVec3(data.Velocity);
                message.Rotation = ToVec3(data.Rotation);
                message.AngularVelocity = ToVec3(data.AngularVelocity);
                message.TrailOffset = ToVec3(data.TrailOffset);
            }
            return message;
        }

        public static bool TryDecodeProjectile(PE.ProjectileState message,
            out ProjectileStateData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "projectile-null";
                return false;
            }
            int action = (int)message.Action;
            if (!EntityActions.IsKnown(action))
            {
                reason = "projectile-action-" + action;
                return false;
            }
            if (!IsValidId(message.Id))
            {
                reason = "projectile-id-" + message.Id;
                return false;
            }
            if (message.OwnerClientId < -1 || message.OwnerClientId > EntityReplicationLimits.MaxNetworkId)
            {
                reason = "projectile-owner-" + message.OwnerClientId;
                return false;
            }
            if (action == EntityActions.Remove)
            {
                data = new ProjectileStateData(message.Id, action, message.Value, Vector3.Zero,
                    Vector3.Zero, Vector3.Zero, Vector3.Zero, message.OwnerClientId, false,
                    message.Incendiary, Vector3.Zero);
                return true;
            }
            if (message.Value <= 0)
            {
                reason = "projectile-value-" + message.Value;
                return false;
            }
            if (!TryVector(message.Position, "projectile-position", out Vector3 position,
                    out reason) ||
                !TryVector(message.Velocity, "projectile-velocity", out Vector3 velocity,
                    out reason) ||
                !TryVector(message.Rotation, "projectile-rotation", out Vector3 rotation,
                    out reason) ||
                !TryVector(message.AngularVelocity, "projectile-angular-velocity",
                    out Vector3 angularVelocity, out reason) ||
                !TryVector(message.TrailOffset, "projectile-trail-offset", out Vector3 trailOffset,
                    out reason))
                return false;
            data = new ProjectileStateData(message.Id, action, message.Value, position, velocity,
                rotation, angularVelocity, message.OwnerClientId, message.Stuck,
                message.Incendiary, trailOffset);
            return true;
        }

        // ------------------------------------------------------------ 增量批次 / 快照

        public static PE.EntityState EncodeState(EntityStateData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var message = new PE.EntityState
            {
                Version = data.Version,
                ServerTick = data.ServerTick
            };
            for (int i = 0; i < data.Pickables.Count; i++)
                message.Pickables.Add(EncodePickable(data.Pickables[i]));
            for (int i = 0; i < data.Projectiles.Count; i++)
                message.Projectiles.Add(EncodeProjectile(data.Projectiles[i]));
            return message;
        }

        public static byte[] EncodeStateBytes(EntityStateData data) =>
            EncodeState(data).ToByteArray();

        public static bool TryDecodeState(PE.EntityState message, out EntityStateData data,
            out string reason)
        {
            data = null;
            reason = null;
            if (message == null)
            {
                reason = "state-null";
                return false;
            }
            if (message.Version == 0)
            {
                reason = "state-version-zero";
                return false;
            }
            if (message.Pickables.Count > EntityReplicationLimits.MaxPickableRecordsPerMessage)
            {
                reason = "state-pickables-" + message.Pickables.Count;
                return false;
            }
            if (message.Projectiles.Count > EntityReplicationLimits.MaxProjectileRecordsPerMessage)
            {
                reason = "state-projectiles-" + message.Projectiles.Count;
                return false;
            }
            var result = new EntityStateData(message.Version, message.ServerTick);
            for (int i = 0; i < message.Pickables.Count; i++)
            {
                if (!TryDecodePickable(message.Pickables[i], out PickableStateData pickable,
                        out string pickableReason))
                {
                    reason = "state-pickable[" + i + "]-" + pickableReason;
                    return false;
                }
                result.Pickables.Add(pickable);
            }
            for (int i = 0; i < message.Projectiles.Count; i++)
            {
                if (!TryDecodeProjectile(message.Projectiles[i],
                        out ProjectileStateData projectile, out string projectileReason))
                {
                    reason = "state-projectile[" + i + "]-" + projectileReason;
                    return false;
                }
                result.Projectiles.Add(projectile);
            }
            data = result;
            return true;
        }

        public static PE.EntitySnapshot EncodeSnapshot(EntitySnapshotData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var message = new PE.EntitySnapshot
            {
                SnapshotId = data.SnapshotId,
                ServerTick = data.ServerTick,
                Complete = data.Complete,
                StateVersion = data.StateVersion
            };
            for (int i = 0; i < data.Pickables.Count; i++)
                message.Pickables.Add(EncodePickable(data.Pickables[i]));
            for (int i = 0; i < data.Projectiles.Count; i++)
                message.Projectiles.Add(EncodeProjectile(data.Projectiles[i]));
            return message;
        }

        public static bool TryDecodeSnapshot(PE.EntitySnapshot message,
            out EntitySnapshotData data, out string reason)
        {
            data = null;
            reason = null;
            if (message == null)
            {
                reason = "snapshot-null";
                return false;
            }
            if (message.SnapshotId == 0)
            {
                reason = "snapshot-id-zero";
                return false;
            }
            if (message.Pickables.Count > EntityReplicationLimits.MaxSnapshotPickables)
            {
                reason = "snapshot-pickables-" + message.Pickables.Count;
                return false;
            }
            if (message.Projectiles.Count > EntityReplicationLimits.MaxSnapshotProjectiles)
            {
                reason = "snapshot-projectiles-" + message.Projectiles.Count;
                return false;
            }
            var result = new EntitySnapshotData(message.SnapshotId, message.ServerTick,
                message.Complete, message.StateVersion);
            for (int i = 0; i < message.Pickables.Count; i++)
            {
                if (!TryDecodePickable(message.Pickables[i], out PickableStateData pickable,
                        out string pickableReason))
                {
                    reason = "snapshot-pickable[" + i + "]-" + pickableReason;
                    return false;
                }
                result.Pickables.Add(pickable);
            }
            for (int i = 0; i < message.Projectiles.Count; i++)
            {
                if (!TryDecodeProjectile(message.Projectiles[i],
                        out ProjectileStateData projectile, out string projectileReason))
                {
                    reason = "snapshot-projectile[" + i + "]-" + projectileReason;
                    return false;
                }
                result.Projectiles.Add(projectile);
            }
            data = result;
            return true;
        }

        // ------------------------------------------------------------ 拾取请求 / 回执

        public static PE.PickableAcquireRequest EncodeRequest(
            in PickableAcquireRequestData request)
        {
            var message = new PE.PickableAcquireRequest
            {
                Sequence = request.Sequence,
                PickableId = request.PickableId,
                Position = ToVec3(request.Position)
            };
            if (request.HasReach) message.Reach = request.Reach;
            return message;
        }

        public static bool TryDecodeRequest(PE.PickableAcquireRequest message,
            out PickableAcquireRequestData request, out string reason)
        {
            request = default;
            reason = null;
            if (message == null)
            {
                reason = "request-null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "request-sequence-" + message.Sequence;
                return false;
            }
            if (!IsValidId(message.PickableId))
            {
                reason = "request-pickable-" + message.PickableId;
                return false;
            }
            if (!TryVector(message.Position, "request-position", out Vector3 position,
                    out reason))
                return false;
            request = new PickableAcquireRequestData(message.Sequence, message.PickableId,
                position, message.HasReach, message.Reach);
            return true;
        }

        public static PE.PickableAcquireResult EncodeResult(
            in PickableAcquireResultData result)
        {
            var message = new PE.PickableAcquireResult
            {
                Sequence = result.Sequence,
                PickableId = result.PickableId,
                Accepted = result.Accepted,
                Reason = result.Reason ?? string.Empty,
                RemainingCount = result.RemainingCount,
                Removed = result.Removed,
                InventoryVersion = result.InventoryVersion
            };
            int count = Math.Min(result.Indices.Length,
                Math.Min(result.Values.Length, result.Counts.Length));
            for (int i = 0; i < count; i++)
            {
                message.Inventory.Add(new PE.InventorySlotDelta
                {
                    Index = result.Indices[i],
                    Value = result.Values[i],
                    Count = result.Counts[i]
                });
            }
            return message;
        }

        public static bool TryDecodeResult(PE.PickableAcquireResult message,
            out PickableAcquireResultData result, out string reason)
        {
            result = default;
            reason = null;
            if (message == null)
            {
                reason = "result-null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "result-sequence-" + message.Sequence;
                return false;
            }
            if (!IsValidId(message.PickableId))
            {
                reason = "result-pickable-" + message.PickableId;
                return false;
            }
            if (!message.Accepted && string.IsNullOrEmpty(message.Reason))
            {
                reason = "rejection-without-reason";
                return false;
            }
            if (message.Accepted && !string.IsNullOrEmpty(message.Reason))
            {
                reason = "accepted-with-reason";
                return false;
            }
            if (message.RemainingCount < 0)
            {
                reason = "result-remaining-" + message.RemainingCount;
                return false;
            }
            var indices = new int[message.Inventory.Count];
            var values = new int[message.Inventory.Count];
            var counts = new int[message.Inventory.Count];
            for (int i = 0; i < message.Inventory.Count; i++)
            {
                PE.InventorySlotDelta slot = message.Inventory[i];
                if (slot == null)
                {
                    reason = "result-slot[" + i + "]-null";
                    return false;
                }
                if (slot.Index < 0)
                {
                    reason = "result-slot[" + i + "]-index-" + slot.Index;
                    return false;
                }
                if (slot.Count < 0)
                {
                    reason = "result-slot[" + i + "]-count-" + slot.Count;
                    return false;
                }
                if (slot.Count > 0 && slot.Value == 0)
                {
                    reason = "result-slot[" + i + "]-empty-value";
                    return false;
                }
                if (slot.Count == 0 && slot.Value != 0)
                {
                    // 空槽带值 = 语义含糊，绝不“按 0 猜”。
                    reason = "result-slot[" + i + "]-value-without-count";
                    return false;
                }
                indices[i] = slot.Index;
                values[i] = slot.Count == 0 ? 0 : slot.Value;
                counts[i] = slot.Count;
            }
            result = new PickableAcquireResultData(message.Sequence, message.PickableId,
                message.Accepted, message.Reason, message.RemainingCount, message.Removed,
                indices, values, counts, message.InventoryVersion);
            return true;
        }

        // ------------------------------------------------------------ 工具

        private static bool IsValidId(int id) => id > 0 && id <= EntityReplicationLimits.MaxNetworkId;

        /// <summary>
        /// 稀疏背包槽位增量的严格解码（拾取回执 / 丢弃回执 / 开火回执共用同一口径）：
        ///   · 下标 / 数量非负；
        ///   · 数量为 0 ⇒ 值必须为 0（空槽带值语义含糊，绝不"按 0 猜"）；
        ///   · 数量 &gt; 0 ⇒ 值必须非 0。
        /// 任何一条非法 ⇒ 整条消息拒绝（不半条应用）。
        /// </summary>
        internal static bool TryDecodeInventorySlots(IList<PE.InventorySlotDelta> slots,
            out int[] indices, out int[] values, out int[] counts, out string reason)
        {
            indices = Array.Empty<int>();
            values = Array.Empty<int>();
            counts = Array.Empty<int>();
            reason = null;
            if (slots == null) return true;
            var outIndices = new int[slots.Count];
            var outValues = new int[slots.Count];
            var outCounts = new int[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                PE.InventorySlotDelta slot = slots[i];
                if (slot == null)
                {
                    reason = "slot[" + i + "]-null";
                    return false;
                }
                if (slot.Index < 0)
                {
                    reason = "slot[" + i + "]-index-" + slot.Index;
                    return false;
                }
                if (slot.Count < 0)
                {
                    reason = "slot[" + i + "]-count-" + slot.Count;
                    return false;
                }
                if (slot.Count > 0 && slot.Value == 0)
                {
                    reason = "slot[" + i + "]-empty-value";
                    return false;
                }
                if (slot.Count == 0 && slot.Value != 0)
                {
                    reason = "slot[" + i + "]-value-without-count";
                    return false;
                }
                outIndices[i] = slot.Index;
                outValues[i] = slot.Count == 0 ? 0 : slot.Value;
                outCounts[i] = slot.Count;
            }
            indices = outIndices;
            values = outValues;
            counts = outCounts;
            return true;
        }

        /// <summary>请求里的方向向量：有限、非零（客户端发单位向量；主机随后自己归一化）。</summary>
        internal static bool TryDirection(PP.Vec3 value, string field, out Vector3 result,
            out string reason)
        {
            result = Vector3.Zero;
            if (!TryVector(value, field, out Vector3 raw, out reason)) return false;
            float lengthSquared = raw.LengthSquared();
            if (lengthSquared < 0.25f || lengthSquared > 4f)
            {
                reason = field + "-length-" + MathUtils.Sqrt(lengthSquared).ToString("F3");
                return false;
            }
            result = Vector3.Normalize(raw);
            return true;
        }

        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static PP.Vec3 ToVec3(Vector3 value) =>
            new PP.Vec3 { X = value.X, Y = value.Y, Z = value.Z };

        public static PP.Quat ToQuat(Quaternion value) =>
            new PP.Quat { X = value.X, Y = value.Y, Z = value.Z, W = value.W };

        public static Vector3 FromVec3(PP.Vec3 value) =>
            new Vector3(value.X, value.Y, value.Z);

        public static Quaternion FromQuat(PP.Quat value) =>
            new Quaternion(value.X, value.Y, value.Z, value.W);

        internal static bool TryVector(PP.Vec3 value, string field, out Vector3 result,
            out string reason)
        {
            result = Vector3.Zero;
            reason = null;
            if (value == null)
            {
                reason = field + "-missing";
                return false;
            }
            if (!IsFinite(value.X) || !IsFinite(value.Y) || !IsFinite(value.Z))
            {
                reason = field + "-nonfinite";
                return false;
            }
            if (MathUtils.Abs(value.X) > EntityReplicationLimits.MaxCoordinate ||
                MathUtils.Abs(value.Y) > EntityReplicationLimits.MaxCoordinate ||
                MathUtils.Abs(value.Z) > EntityReplicationLimits.MaxCoordinate)
            {
                reason = field + "-range";
                return false;
            }
            result = new Vector3(value.X, value.Y, value.Z);
            return true;
        }

        private static bool TryQuaternion(PP.Quat value, out Quaternion result, out string reason)
        {
            result = Quaternion.Identity;
            reason = null;
            if (value == null)
            {
                reason = "stuck-rotation-missing";
                return false;
            }
            if (!IsFinite(value.X) || !IsFinite(value.Y) || !IsFinite(value.Z) ||
                !IsFinite(value.W))
            {
                reason = "stuck-rotation-nonfinite";
                return false;
            }
            float lengthSquared = value.X * value.X + value.Y * value.Y + value.Z * value.Z +
                value.W * value.W;
            if (lengthSquared < 0.5f || lengthSquared > 1.5f)
            {
                reason = "stuck-rotation-length-" + lengthSquared.ToString("F3");
                return false;
            }
            result = Quaternion.Normalize(new Quaternion(value.X, value.Y, value.Z, value.W));
            return true;
        }
    }

    /// <summary>
    /// 增量批次 / 快照的**排序与去重**（与 ExplosionEventOrderer 同一套口径）：
    /// 每个客户端只应用比已应用版本更新的批次；重复 / 回退明确计数，不重放。
    /// </summary>
    public sealed class EntityStateOrderer
    {
        private uint m_lastVersion;
        private uint m_lastSnapshotId;

        /// <summary>最近一次已应用的 EntityState 版本（0 = 还没应用过）。</summary>
        public uint LastStateVersion => m_lastVersion;

        public long Accepted { get; private set; }
        public long Duplicates { get; private set; }
        public long OutOfOrder { get; private set; }
        public long SnapshotsAccepted { get; private set; }
        public long SnapshotsDuplicate { get; private set; }

        public bool TryAcceptState(uint version, out string reason)
        {
            reason = null;
            if (version == 0)
            {
                reason = "version-zero";
                return false;
            }
            if (m_lastVersion != 0 && version == m_lastVersion)
            {
                Duplicates++;
                reason = "duplicate";
                return false;
            }
            if (m_lastVersion != 0 && version < m_lastVersion)
            {
                OutOfOrder++;
                reason = "stale";
                return false;
            }
            m_lastVersion = version;
            Accepted++;
            return true;
        }

        public bool TryAcceptSnapshot(uint snapshotId, out string reason)
        {
            reason = null;
            if (snapshotId == 0)
            {
                reason = "snapshot-id-zero";
                return false;
            }
            if (m_lastSnapshotId != 0 && snapshotId <= m_lastSnapshotId)
            {
                SnapshotsDuplicate++;
                reason = "snapshot-duplicate";
                return false;
            }
            m_lastSnapshotId = snapshotId;
            SnapshotsAccepted++;
            return true;
        }

        public void Reset(string reason)
        {
            m_lastVersion = 0;
            m_lastSnapshotId = 0;
        }
    }
}

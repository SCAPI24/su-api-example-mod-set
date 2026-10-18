using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;

using Google.Protobuf;
using PC = SuMultiplayer.Protocol.Creature;

namespace SuMultiplayer.Creatures
{
    /// <summary>
    /// 生物同步动作。**只允许追加**（旧实现对应 EntityMessage.Add/Remove 与
    /// BodyUpdateMessage 的位置/姿态更新；见 Mod/ScMultiplayer/Message/EntityMessage.cs 与
    /// Mod/ScMultiplayer/Message/BodyUpdateMessage.cs）。
    /// </summary>
    public enum CreatureSyncAction : byte
    {
        Add = 0,
        Update = 1,
        Remove = 2
    }

    /// <summary>
    /// 主机侧生物活动标记：**只在主机内存里用于选择同步频率，不上线**（客户端不需要它：
    /// 客户端只按位置/姿态/速度插值）。
    ///
    /// 对应旧实现 SendAdaptiveAnimalUpdates 里算 SyncTier 用到的全部状态
    /// （攻击 / 追击玩家 / 刚受伤 / 上一条行为状态）。
    /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:SendAdaptiveAnimalUpdates
    /// </summary>
    [Flags]
    public enum CreatureActivity : byte
    {
        None = 0,

        /// <summary>位置或朝向在动（静止的生物走 Idle 档：1 Hz 心跳，不占高频率带宽）。</summary>
        Moving = 1,

        /// <summary>正在追击目标（ComponentChaseBehavior.IsActive 且 Target != null）。</summary>
        Aggro = 2,

        /// <summary>攻击动作进行中（ComponentCreatureModel.AttackOrder / IsAttackHitMoment）。</summary>
        Attacking = 4,

        /// <summary>逃跑类行为激活（RunAway / MoveAway / FlyAway / AvoidPlayer / SwimAway）。</summary>
        Fleeing = 8,

        /// <summary>最近 3 秒内掉过血（旧实现 metadata.HighPriorityUntil = now + 3.0）。</summary>
        Damaged = 16
    }

    /// <summary>
    /// 一条权威生物记录：**主机生成、客户端只复制**。
    ///
    /// 字段来源（旧实现）：
    ///   · Key          = 旧 EntityMessage.EntityId（主机分配的稳定网络 id）
    ///   · TemplateName = 旧 EntityMessage.TemplateName（实体模板名 = 生物种类）
    ///   · Position / Rotation / Velocity = 旧 BodyUpdateMessage.BodyItem
    ///   · Health       = 旧 BodyUpdateMessage.ChangeFlag.Health
    /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:SendWorldObjects
    /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:HandleAnimalBodyUpdate
    /// </summary>
    public sealed class CreatureStateRecord
    {
        /// <summary>主机侧身份令牌（Entity 对象，只在主机内存里用，绝不序列化）。</summary>
        public object Token;

        /// <summary>网络键（主机分配；客户端用它去重 / 更新 / 删除）。</summary>
        public int Key;

        /// <summary>生物种类（实体模板名，例如 "Wolf_Gray"）。</summary>
        public string TemplateName = string.Empty;

        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Velocity;

        /// <summary>生命值；主机没有 ComponentHealth 时按 1（满血）发布。</summary>
        public float Health = 1f;

        /// <summary>主机侧活动状态（选择同步频率用；不序列化，客户端只做插值）。</summary>
        public CreatureActivity Activity;

        public CreatureStateRecord Clone() => new CreatureStateRecord
        {
            Token = Token,
            Key = Key,
            TemplateName = TemplateName ?? string.Empty,
            Position = Position,
            Rotation = Rotation,
            Velocity = Velocity,
            Health = Health,
            Activity = Activity
        };
    }

    /// <summary>
    /// 一个客户端当前的生物区域：以它的联机角色位置为圆心、固定半径。
    /// 半径取旧实现维持远端生物时的 68 米计数半径（不是地形兴趣半径）。
    /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:869-971
    ///         （MaintainRemoteCreatureSpawning：CountRemoteCreatures(..., 68f) < 26）
    /// </summary>
    public readonly struct CreatureRegion
    {
        public CreatureRegion(int clientId, Vector3 position, float radius)
        {
            ClientId = clientId;
            Position = position;
            Radius = radius;
        }

        public int ClientId { get; }
        public Vector3 Position { get; }
        public float Radius { get; }
    }

    /// <summary>一条待发送记录（动作 + 记录）。</summary>
    public readonly struct CreatureSyncEntry
    {
        public CreatureSyncEntry(CreatureSyncAction action, CreatureStateRecord record)
        {
            Action = action;
            Record = record;
        }

        public CreatureSyncAction Action { get; }
        public CreatureStateRecord Record { get; }
    }

    /// <summary>
    /// **纯函数**的按客户端分配规则（无引擎、无网络，可直接自检）：
    ///   · 每个客户端只拿到"落在它区域里"的生物（区域 = 圆心 + 半径）；
    ///   · 两个客户端区域重叠时，同一只生物会同时出现在两端的集合里（两端都能显示）；
    ///   · 每个客户端最多 <paramref name="perClientLimit"/> 只，按距离由近到远、距离相同按键排序，
    ///     保证主机每一帧算出的结果稳定（不会因为字典迭代顺序抖动）。
    ///
    /// 这条规则对应用户口径：
    ///   "客户端的动物数量根据主机端分配的数量生成；主机端是所有端的动物数量，
    ///    客户端只有自己端的动物数量。如果动物出现在两个客户端的区域中，那么两端都应该能显示。"
    /// </summary>
    public static class CreatureAllocation
    {
        public const float DefaultRegionRadiusMeters = 68f;
        public const int DefaultPerClientLimit = 26;

        public static Dictionary<int, List<int>> Compute(
            IReadOnlyList<CreatureStateRecord> creatures,
            IReadOnlyList<CreatureRegion> regions,
            float regionRadius,
            int perClientLimit)
        {
            var result = new Dictionary<int, List<int>>();
            if (creatures == null || regions == null) return result;

            float radius = regionRadius > 0f ? regionRadius : DefaultRegionRadiusMeters;
            int limit = perClientLimit > 0 ? perClientLimit : DefaultPerClientLimit;
            float radiusSquared = radius * radius;

            for (int i = 0; i < regions.Count; i++)
            {
                CreatureRegion region = regions[i];
                float regionRadiusSquared = region.Radius > 0f ? region.Radius * region.Radius : radiusSquared;
                var candidates = new List<CreatureCandidate>();
                for (int j = 0; j < creatures.Count; j++)
                {
                    CreatureStateRecord creature = creatures[j];
                    if (creature == null || creature.Key == 0) continue;
                    if (string.IsNullOrWhiteSpace(creature.TemplateName)) continue;
                    float dx = creature.Position.X - region.Position.X;
                    float dz = creature.Position.Z - region.Position.Z;
                    float distanceSquared = dx * dx + dz * dz;
                    if (distanceSquared > regionRadiusSquared) continue;
                    candidates.Add(new CreatureCandidate(creature.Key, distanceSquared));
                }
                candidates.Sort(static (a, b) =>
                {
                    int byDistance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                    return byDistance != 0 ? byDistance : a.Key.CompareTo(b.Key);
                });
                var keys = new List<int>(Math.Min(limit, candidates.Count));
                for (int j = 0; j < candidates.Count && keys.Count < limit; j++)
                {
                    keys.Add(candidates[j].Key);
                }
                result[region.ClientId] = keys;
            }
            return result;
        }

        private readonly struct CreatureCandidate
        {
            public CreatureCandidate(int key, float distanceSquared)
            {
                Key = key;
                DistanceSquared = distanceSquared;
            }

            public int Key { get; }
            public float DistanceSquared { get; }
        }
    }

    /// <summary>
    /// 生物同步编解码（proto：Protocol/creature.proto 的 <c>CreatureState</c>）。
    /// 字段与旧 EntityMessage.Add/Remove + BodyUpdateMessage 对齐（只保留本次要同步的字段）：
    /// flags: bit0 = 全量快照（客户端在最后一批上裁掉不在快照里的复制体），
    ///        bit1 = 本批是该全量快照的最后一批。
    ///
    /// 对应旧实现：
    ///   · EntityMessage.Add/Remove（TemplateName + EntityId）
    ///   · BodyUpdateMessage（Position/Rotation/Velocity/Health）
    /// Source: Mod/ScMultiplayer/Message/EntityMessage.cs
    /// Source: Mod/ScMultiplayer/Message/BodyUpdateMessage.cs
    /// </summary>
    public static class CreatureSyncCodec
    {
        public const byte Version = 1;
        public const int MaxRecordsPerBatch = 64;
        public const byte FlagFullSnapshot = 1;
        public const byte FlagLastBatch = 2;

        public sealed class CreatureBatch
        {
            public int ServerTick;
            public byte Flags;
            public int SnapshotId;
            public readonly List<CreatureSyncEntry> Entries = new List<CreatureSyncEntry>();

            public bool IsFullSnapshot => (Flags & FlagFullSnapshot) != 0;
            public bool IsLastBatch => (Flags & FlagLastBatch) != 0;
        }

        public static byte[] Encode(IReadOnlyList<CreatureSyncEntry> entries, int serverTick,
            byte flags, int snapshotId)
        {
            var message = new PC.CreatureState
            {
                Version = Version,
                ServerTick = serverTick,
                Flags = flags,
                SnapshotId = snapshotId
            };
            int count = entries == null ? 0 : entries.Count;
            for (int i = 0; i < count; i++)
            {
                CreatureSyncEntry entry = entries[i];
                CreatureStateRecord record = entry.Record;
                var wire = new PC.CreatureRecord
                {
                    Action = (PC.CreatureAction)entry.Action,
                    Key = record.Key,
                    TemplateName = string.IsNullOrWhiteSpace(record.TemplateName)
                        ? string.Empty
                        : record.TemplateName,
                    Position = new PC.Vec3
                    {
                        X = record.Position.X, Y = record.Position.Y, Z = record.Position.Z
                    },
                    Rotation = new PC.Quat
                    {
                        X = record.Rotation.X, Y = record.Rotation.Y,
                        Z = record.Rotation.Z, W = record.Rotation.W
                    },
                    Velocity = new PC.Vec3
                    {
                        X = record.Velocity.X, Y = record.Velocity.Y, Z = record.Velocity.Z
                    },
                    Health = record.Health
                };
                message.Records.Add(wire);
            }
            return message.ToByteArray();
        }

        public static bool TryDecode(byte[] payload, out CreatureBatch batch, out string reason)
        {
            batch = null;
            reason = string.Empty;
            if (payload == null || payload.Length == 0) { reason = "载荷为空"; return false; }
            PC.CreatureState message;
            try
            {
                message = PC.CreatureState.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "载荷截断/格式错误：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvert(message, out batch, out reason);
        }

        /// <summary>把已解析的 protobuf 消息转成领域批次（强类型处理器用）。</summary>
        public static bool TryConvert(PC.CreatureState message, out CreatureBatch batch, out string reason)
        {
            batch = null;
            reason = string.Empty;
            if (message == null)
            {
                reason = "消息为 null";
                return false;
            }
            if (message.Version != Version)
            {
                reason = "载荷版本不支持：" + message.Version + "（本端 " + Version + "）";
                return false;
            }
            int count = message.Records.Count;
            if (count > MaxRecordsPerBatch * 16)
            {
                reason = "记录数超限：" + count;
                return false;
            }
            var result = new CreatureBatch
            {
                ServerTick = message.ServerTick,
                Flags = (byte)message.Flags,
                SnapshotId = message.SnapshotId
            };
            for (int i = 0; i < count; i++)
            {
                PC.CreatureRecord wire = message.Records[i];
                CreatureSyncAction action = (CreatureSyncAction)(int)wire.Action;
                if (wire.Action != PC.CreatureAction.Add && wire.Action != PC.CreatureAction.Update &&
                    wire.Action != PC.CreatureAction.Remove)
                {
                    reason = "未知动作：" + (int)wire.Action;
                    return false;
                }
                var record = new CreatureStateRecord { Key = wire.Key };
                if (action != CreatureSyncAction.Remove)
                {
                    // 网络键 = 主机分配的正整数 id（CreatureSpawnModule 从 1 开始递增）。
                    // 0/负数说明键类型或来源不对：整批明确失败并写清原因，绝不接受后当成
                    // "不在分配集合里"被静默裁剪（那会把世界里的实体删光）。
                    if (record.Key <= 0)
                    {
                        reason = "记录 key 非法：" + record.Key + "（网络键必须是主机分配的正整数 id）";
                        return false;
                    }
                    record.TemplateName = wire.TemplateName ?? string.Empty;
                    if (wire.Position == null || wire.Rotation == null || wire.Velocity == null)
                    {
                        reason = "记录 key=" + record.Key + " 缺少 position/rotation/velocity";
                        return false;
                    }
                    record.Position = new Vector3(wire.Position.X, wire.Position.Y, wire.Position.Z);
                    record.Rotation = new Quaternion(wire.Rotation.X, wire.Rotation.Y,
                        wire.Rotation.Z, wire.Rotation.W);
                    record.Velocity = new Vector3(wire.Velocity.X, wire.Velocity.Y, wire.Velocity.Z);
                    record.Health = wire.Health;
                    if (!IsFinite(record.Position) || !IsFinite(record.Rotation) ||
                        !IsFinite(record.Velocity) || !float.IsFinite(record.Health))
                    {
                        reason = "记录 key=" + record.Key + " 含非有限数值";
                        return false;
                    }
                }
                else if (record.Key <= 0)
                {
                    reason = "删除记录 key 非法：" + record.Key + "（网络键必须是主机分配的正整数 id）";
                    return false;
                }
                result.Entries.Add(new CreatureSyncEntry(action, record));
            }
            batch = result;
            return true;
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private static bool IsFinite(Quaternion value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) &&
            float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}

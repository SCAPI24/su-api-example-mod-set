using System;
using Engine;

namespace SuMultiplayer.Creatures
{
    /// <summary>
    /// 生物同步频率档位。**纯托管、无网络/无引擎依赖**，主机与自检工程共用同一份判定。
    ///
    /// 移植自旧实现的选择顺序（先看状态，再看距离）：
    ///   · 旧实现 `SendAdaptiveAnimalUpdates` 里 tier = 4（攻击且目标是玩家）/ 3（刚受伤、
    ///     攻击、群体求援、近距离飞鸟）/ 2（玩家 10~12 米内、飞鸟 64 米内）/ 1（目标是玩家）/
    ///     0（其它），对应 `GetAnimalSyncInterval` = 0.0625 / 0.125 / 0.25 / 0.5 秒。
    ///     Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:SendAdaptiveAnimalUpdates
    ///     Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:GetAnimalSyncInterval
    ///
    /// 本版按用户口径把"距离"展开成三档，并把"空闲"单独作为最低档：
    ///   · Priority：攻击 / 逃跑 / 追击 / 刚受伤 —— **不看距离**，最高频率；
    ///   · Near（≤16 m）：10 Hz；
    ///   · Mid（≤40 m）：5 Hz；
    ///   · Far（≤区域半径 68 m）：2 Hz；
    ///   · Idle（没在动、也没有战斗状态）：1 Hz 心跳（客户端插值靠它保持锚点，不会冻结）。
    ///
    /// 每个档位的间隔同时是"值有变化时的最小发送间隔"；心跳间隔另算（见
    /// <see cref="HeartbeatIntervalMs"/>），两者共同实现"只在值变化超过 epsilon 或心跳到点时发送"。
    /// </summary>
    public enum CreatureSyncTier : byte
    {
        /// <summary>静止且无战斗状态：1 Hz 心跳。</summary>
        Idle = 0,

        /// <summary>40 ~ 区域半径（68 m）：2 Hz。</summary>
        Far = 1,

        /// <summary>16 ~ 40 m：5 Hz。</summary>
        Mid = 2,

        /// <summary>≤16 m：10 Hz。</summary>
        Near = 3,

        /// <summary>攻击 / 逃跑 / 追击 / 刚受伤：16 Hz，与距离无关。</summary>
        Priority = 4
    }

    public static class CreatureSyncCadence
    {
        /// <summary>近距离档的半径（米）。</summary>
        public const float NearRadiusMeters = 16f;

        /// <summary>中距离档的半径（米）。</summary>
        public const float MidRadiusMeters = 40f;

        /// <summary>空闲档发送间隔：1 Hz 心跳。</summary>
        public const int IdleIntervalMs = 1000;

        /// <summary>远距离档发送间隔：2 Hz。</summary>
        public const int FarIntervalMs = 500;

        /// <summary>中距离档发送间隔：5 Hz。</summary>
        public const int MidIntervalMs = 200;

        /// <summary>近距离档发送间隔：10 Hz。</summary>
        public const int NearIntervalMs = 100;

        /// <summary>
        /// 战斗档发送间隔：旧实现 0.0625 s = 16 Hz。主机扫描粒度是 20 ms，取 50 ms 让
        /// "距上次发送 ≥ 50 ms"在 60 ms 处命中 ≈ 16.7 Hz；写 62 会被抬到 80 ms（12.5 Hz）。
        /// </summary>
        public const int PriorityIntervalMs = 50;

        /// <summary>心跳间隔：值没变化时也至少这么久发一条，防止客户端插值锚点过期。</summary>
        public const int HeartbeatIntervalMs = 1000;

        /// <summary>档位数量（计数器数组长度）。</summary>
        public const int TierCount = 5;

        /// <summary>会把档位直接抬到 Priority 的活动标记。</summary>
        public const CreatureActivity PriorityActivityMask =
            CreatureActivity.Aggro | CreatureActivity.Attacking |
            CreatureActivity.Fleeing | CreatureActivity.Damaged |
            // 被骑乘的坐骑按 Priority（50ms）同步：客户端是插值表现，10Hz 档位会看出"一小段
            // 一小段跳"，骑乘是玩家直接盯着看的对象（2026-10-26 骑乘平滑修复）。
            CreatureActivity.Ridden;

        /// <summary>值变化判定用的 epsilon（位置 5 mm、速度 0.05 m/s、生命 0.001）。</summary>
        public const float PositionEpsilonSquared = 0.000025f;

        public const float VelocityEpsilonSquared = 0.0025f;

        public const float HealthEpsilon = 0.001f;

        public static bool HasPriority(CreatureActivity activity) =>
            (activity & PriorityActivityMask) != 0;

        /// <summary>
        /// 选择档位：**先状态、后距离**（与旧实现同序：攻击/受伤/追击直接最高档，
        /// 静止生物走心跳档，其余按到该客户端角色的距离分近/中/远）。
        /// </summary>
        public static CreatureSyncTier SelectTier(CreatureActivity activity, float distanceSquared)
        {
            if (HasPriority(activity)) return CreatureSyncTier.Priority;
            if ((activity & CreatureActivity.Moving) == 0) return CreatureSyncTier.Idle;
            if (distanceSquared <= NearRadiusMeters * NearRadiusMeters) return CreatureSyncTier.Near;
            if (distanceSquared <= MidRadiusMeters * MidRadiusMeters) return CreatureSyncTier.Mid;
            return CreatureSyncTier.Far;
        }

        public static int IntervalMs(CreatureSyncTier tier)
        {
            switch (tier)
            {
                case CreatureSyncTier.Priority: return PriorityIntervalMs;
                case CreatureSyncTier.Near: return NearIntervalMs;
                case CreatureSyncTier.Mid: return MidIntervalMs;
                case CreatureSyncTier.Far: return FarIntervalMs;
                default: return IdleIntervalMs;
            }
        }

        public static string TierName(CreatureSyncTier tier)
        {
            switch (tier)
            {
                case CreatureSyncTier.Priority: return "prio";
                case CreatureSyncTier.Near: return "near";
                case CreatureSyncTier.Mid: return "mid";
                case CreatureSyncTier.Far: return "far";
                default: return "idle";
            }
        }

        /// <summary>水平距离平方（与区域分配同口径：只比 X/Z，不因高度差把生物判远）。</summary>
        public static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// 值是否变化超过 epsilon（位置 / 朝向 / 速度 / 生命）。朝向用四元数点积：
        /// 1 - |dot| ≈ (θ/2)²，阈值 0.00005 约等于 0.8° 的转向。
        /// </summary>
        public static bool ValuesDiffer(Vector3 previousPosition, Quaternion previousRotation,
            Vector3 previousVelocity, float previousHealth, CreatureStateRecord current)
        {
            if (current == null) return true;
            if (Vector3.DistanceSquared(previousPosition, current.Position) >
                PositionEpsilonSquared) return true;
            if (Vector3.DistanceSquared(previousVelocity, current.Velocity) >
                VelocityEpsilonSquared) return true;
            if (MathUtils.Abs(previousHealth - current.Health) > HealthEpsilon) return true;
            float dot = Quaternion.Dot(previousRotation, current.Rotation);
            return 1f - MathUtils.Abs(dot) > 0.00005f;
        }

        public static bool HasChangedBeyondEpsilon(CreatureStateRecord previous,
            CreatureStateRecord current)
        {
            if (previous == null || current == null) return true;
            return ValuesDiffer(previous.Position, previous.Rotation, previous.Velocity,
                previous.Health, current);
        }
    }
}

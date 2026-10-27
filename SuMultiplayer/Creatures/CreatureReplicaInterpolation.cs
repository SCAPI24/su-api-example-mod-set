using System;
using System.Collections.Generic;
using Engine;

namespace SuMultiplayer.Creatures
{
    /// <summary>一次"误差过大 → 直接对齐"事件（供模块限流日志与计数用）。</summary>
    public readonly struct CreatureSnapEvent
    {
        public CreatureSnapEvent(int key, float errorMeters)
        {
            Key = key;
            ErrorMeters = errorMeters;
        }

        public int Key { get; }

        /// <summary>对齐前，平滑轨迹与主机外推点的距离（米）。</summary>
        public float ErrorMeters { get; }
    }

    /// <summary>
    /// 客户端生物复制体的**表现层插值/外推**（纯托管，不碰引擎、不碰网络）。
    ///
    /// 移植自旧实现的动物表现层，不是新发明：
    ///   · 保留最近一条权威记录（位置/姿态/速度），每帧做**速度外推**；
    ///   · 平滑轨迹与主机外推点的误差超过 <see cref="SnapDistanceMeters"/> 才允许"直接对齐"
    ///     （并上报一次事件，由模块限流记录）；小误差用**修正速度**慢慢追，绝不瞬移；
    ///   · 水平方向按速度积分（连续运动），竖直方向直接跟外推轨迹（避免把起跳/落地的
    ///     冲量当成加速度来回抖），姿态用 Slerp；
    ///   · 静止（主机速度≈0 且误差进入毫米级）时**精确落回**记录位置，不残留抖动。
    ///
    /// 与旧实现的常量对照（Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:UpdateRemoteAnimalPresentations
    ///   + Mod/ScMultiplayer/Core/ScMultiplayerRuntimeState.cs:RemoteAnimalPredictionLimit / RemoteAnimalSnapDistance）：
    ///   · 预测上限 1.15 s（旧 RemoteAnimalPredictionLimit）；
    ///   · 对齐距离 3 m（旧 RemoteAnimalSnapDistance）；
    ///   · 修正视界 0.4 s、最大修正速度 3 m/s、速度混合 8/s、竖直混合 12/s、姿态混合 10/s。
    ///
    /// 本版唯一的受控差异：旧实现用"客户端步进 - 主机 ServerTick"估链路时延；
    /// 本模块的 ServerTick 是主机泵计数（不是 100 Hz 时钟），因此只用到手时间间隔估
    /// <see cref="State.EstimatedInterval"/>，并用"半个间隔"做外推提前量，保证记录边界处
    /// 目标位置连续（不会每收一条记录就往后拉一下）。
    /// </summary>
    public sealed class CreatureReplicaInterpolation
    {
        /// <summary>超过这个误差直接对齐（米）。旧实现 RemoteAnimalSnapDistance = 3。</summary>
        public const float SnapDistanceMeters = 3f;

        /// <summary>速度外推上限（秒）。旧实现 RemoteAnimalPredictionLimit = 1.15。</summary>
        public const float PredictionLimitSeconds = 1.15f;

        /// <summary>修正速度的视界（秒）：误差 / 0.4 得到期望修正速度。</summary>
        public const float CorrectionHorizonSeconds = 0.4f;

        /// <summary>修正速度上限（米/秒），保证"绝不瞬移"。</summary>
        public const float MaxCorrectionSpeed = 3f;

        /// <summary>速度混合速率（1/s，旧实现 8）。</summary>
        public const float VelocityBlendRate = 8f;

        /// <summary>竖直混合速率（1/s，旧实现 12）。</summary>
        public const float VerticalBlendRate = 12f;

        /// <summary>姿态混合速率（1/s，旧实现 10）。</summary>
        public const float RotationBlendRate = 10f;

        /// <summary>单个快照间隔的采样下限（秒），避免同一时刻的两条记录把间隔估成 0。</summary>
        public const float MinSnapshotIntervalSeconds = 0.03f;

        /// <summary>间隔估计的 EMA 系数（旧实现 0.2）。</summary>
        public const float SnapshotIntervalBlend = 0.2f;

        /// <summary>静止收敛判定：误差小于 1 cm 且主机速度≈0 时精确落位。</summary>
        public const float SettleEpsilonMeters = 0.01f;

        /// <summary>静止速度判定（平方米/平方秒）：0.01 m/s 以内当作静止。</summary>
        public const float SettleVelocitySquared = 0.0001f;

        private sealed class State
        {
            public bool HasRecord;
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.Identity;
            public Vector3 Velocity;
            /// <summary>主机 ComponentLocomotion.LookAngles（马/驴/骆驼被骑转向时的头部朝向）。</summary>
            public Vector2 LookAngles;
            public double LastReceiveTime;
            public float EstimatedInterval = 0.1f;

            public bool PresentationInitialized;
            public Vector3 PresentationPosition;
            public Quaternion PresentationRotation = Quaternion.Identity;
            public Vector2 PresentationLookAngles;
            public Vector3 SmoothedVelocity;
            public bool HasSmoothedVelocity;
            /// <summary>本机玩家正在骑/站的坐骑：始终平滑插值，禁止"静止贴记录"快照。</summary>
            public bool AlwaysSmooth;
        }

        private readonly Dictionary<int, State> m_states = new Dictionary<int, State>();

        /// <summary>当前复制体数量。</summary>
        public int Count => m_states.Count;

        /// <summary>累计"误差过大 → 直接对齐"次数（正常应长期为 0）。</summary>
        public long Snaps { get; private set; }

        public bool Contains(int key) => m_states.ContainsKey(key);

        /// <summary>
        /// 第一条记录（主机 Add / 重连全量）：实体就是按这条记录创建的，表现位置直接落在权威位置，
        /// 不经过平滑（否则会从原点飞过去）。
        /// </summary>
        public void Seed(CreatureStateRecord record, double now)
        {
            if (record == null) return;
            var state = new State
            {
                HasRecord = true,
                Position = record.Position,
                Rotation = record.Rotation,
                Velocity = record.Velocity,
                LookAngles = record.LookAngles,
                LastReceiveTime = now,
                EstimatedInterval = 0.1f,
                PresentationInitialized = true,
                PresentationPosition = record.Position,
                PresentationRotation = record.Rotation,
                PresentationLookAngles = record.LookAngles,
                SmoothedVelocity = record.Velocity,
                HasSmoothedVelocity = true
            };
            m_states[record.Key] = state;
        }

        /// <summary>
        /// 收到一条新的权威记录：更新外推用的锚点与快照间隔估计。
        /// 返回 true = 这是一个新 key（调用方应走 Seed 语义）；false = 已有复制体，走平滑。
        /// 平滑/对齐的判定发生在下一帧 <see cref="Advance"/> 里（与旧实现一致）。
        /// </summary>
        public bool Observe(CreatureStateRecord record, double now)
        {
            if (record == null) return false;
            if (!m_states.TryGetValue(record.Key, out State state) || !state.HasRecord)
            {
                Seed(record, now);
                return true;
            }
            double arrival = now - state.LastReceiveTime;
            if (arrival > 0.0)
            {
                float sample = MathUtils.Clamp((float)arrival, MinSnapshotIntervalSeconds,
                    PredictionLimitSeconds);
                // 变快（静止心跳 1 Hz → 重新跑动 10 Hz）要**立刻**跟上：否则外推提前量还停在
                // 旧心跳的一半（0.5 s），会先把生物往前推一段再拉回来。变慢则慢慢估（提前量
                // 偏小只会略微滞后，不会过冲）。
                if (sample < state.EstimatedInterval * 0.5f) state.EstimatedInterval = sample;
                else
                {
                    state.EstimatedInterval = MathUtils.Lerp(state.EstimatedInterval, sample,
                        SnapshotIntervalBlend);
                }
            }
            state.Position = record.Position;
            state.Rotation = record.Rotation;
            state.Velocity = record.Velocity;
            state.LookAngles = record.LookAngles;
            state.HasRecord = true;
            state.LastReceiveTime = now;
            return false;
        }

        /// <summary>
        /// 每帧推进一次。仅更新内部表现状态；把结果写进引擎由调用方通过端口完成。
        /// <paramref name="snapEvents"/> 非空时，每次"误差过大直接对齐"追加一条（供限流日志）。
        /// </summary>
        public void Advance(double now, float deltaTime, List<CreatureSnapEvent> snapEvents)
        {
            if (m_states.Count == 0) return;
            float step = MathUtils.Clamp(deltaTime, 0f, 0.05f);
            foreach (KeyValuePair<int, State> item in m_states)
            {
                int key = item.Key;
                State state = item.Value;
                if (!state.HasRecord) continue;

                double age = Math.Max(0.0, now - state.LastReceiveTime);
                // 半个快照间隔的提前量：记录边界处外推点连续（T 间隔、匀速 v 时
                // 上一条记录的外推点 P_n + v*(T + T/2) 与下一条的 P_{n+1} + v*(0 + T/2) 相等）。
                float lead = state.EstimatedInterval * 0.5f;
                float predictionTime = MathUtils.Min((float)age + lead, PredictionLimitSeconds);
                // 水平方向做速度外推；竖直方向**不外推速度**。
                // 主机记录里偶尔会带一点竖直速度（例如动物踩到另一只动物、刚落地时的
                // 碰撞分离速度），乘上半个快照间隔的外推量会在客户端放大成一次“跳起来”，
                // 下一份记录又回到地面 → 上下反复跳。主机位置本身是正常的，竖直轴直接
                // 跟随最近一份权威 Position.Y（仍用下面的 VerticalBlend 平滑），真实跳跃
                // 由连续记录的 Y 抬高来表现，不会瞬移。
                Vector3 predictedPosition = state.Position + new Vector3(
                    state.Velocity.X * predictionTime, 0f, state.Velocity.Z * predictionTime);

                if (!state.PresentationInitialized)
                {
                    Place(state, predictedPosition, state.Rotation);
                    continue;
                }

                Vector3 error = predictedPosition - state.PresentationPosition;
                float errorSquared = error.LengthSquared();
                if (errorSquared > SnapDistanceMeters * SnapDistanceMeters)
                {
                    // 大误差（丢包久了 / 传送 / 模板重建）：明确对齐一次。调用方计数 + 限流日志。
                    Place(state, state.Position, state.Rotation);
                    Snaps++;
                    snapEvents?.Add(new CreatureSnapEvent(key, MathUtils.Sqrt(errorSquared)));
                    continue;
                }

                // 水平：修正速度（有上限）+ 速度积分 —— 连续运动，绝不瞬移。
                Vector3 correctionVelocity = error / CorrectionHorizonSeconds;
                correctionVelocity.Y = 0f;
                float correctionSpeed = correctionVelocity.Length();
                if (correctionSpeed > MaxCorrectionSpeed)
                    correctionVelocity *= MaxCorrectionSpeed / correctionSpeed;
                Vector3 desiredVelocity = state.Velocity + correctionVelocity;
                desiredVelocity.Y = state.Velocity.Y;
                if (!state.HasSmoothedVelocity)
                {
                    state.SmoothedVelocity = state.Velocity;
                    state.HasSmoothedVelocity = true;
                }
                float velocityBlend = 1f - (float)Math.Exp(-VelocityBlendRate * step);
                state.SmoothedVelocity = Vector3.Lerp(state.SmoothedVelocity, desiredVelocity,
                    velocityBlend);
                // 上台阶：先抬 Y 再走水平。否则本帧水平已经走进台阶格，而 Y 还在
                // VerticalBlend 的插值路上，客户端会先看到动物穿进方块侧面、下一帧才出来。
                // 只对“主机几乎无竖直速度、抬升不足 1.5m”的台阶/浅坡生效；真实跳跃
                // （速度明显向上）仍走平滑，不会把弹跳变成瞬移。
                float stepUp = predictedPosition.Y - state.PresentationPosition.Y;
                bool risingStep = stepUp > 0.02f && stepUp <= 1.5f &&
                    MathUtils.Abs(state.Velocity.Y) < 1.0f;
                if (risingStep)
                {
                    state.PresentationPosition.Y = predictedPosition.Y;
                }
                state.PresentationPosition.X += state.SmoothedVelocity.X * step;
                state.PresentationPosition.Z += state.SmoothedVelocity.Z * step;

                // 竖直：直接跟外推轨迹（起跳/落地是离散冲量，不能当连续加速度外推）。
                float verticalBlend = 1f - (float)Math.Exp(-VerticalBlendRate * step);
                if (!risingStep)
                {
                    state.PresentationPosition.Y = MathUtils.Lerp(state.PresentationPosition.Y,
                        predictedPosition.Y, verticalBlend);
                }

                float rotationBlend = 1f - (float)Math.Exp(-RotationBlendRate * step);
                state.PresentationRotation = Quaternion.Slerp(state.PresentationRotation,
                    state.Rotation, rotationBlend);
                // 头部朝向同样平滑（否则马头会按快照节奏一小格一小格转）。
                state.PresentationLookAngles = Vector2.Lerp(state.PresentationLookAngles,
                    state.LookAngles, rotationBlend);

                // 静止收敛：主机速度为 0 且误差已进入毫米级 → 精确落回记录位置（消除残余抖动）。
                // 例外：本机玩家骑/站的坐骑必须**始终插值**——否则每来一条网络记录就贴一次
                // 记录位置/姿态，第一人称相机就按网络频率"一顿一顿"（骑船/骑马实机）。
                if (!state.AlwaysSmooth && state.Velocity.LengthSquared() <= SettleVelocitySquared)
                {
                    Vector3 settleError = state.Position - state.PresentationPosition;
                    if (settleError.LengthSquared() <= SettleEpsilonMeters * SettleEpsilonMeters)
                    {
                        Place(state, state.Position, state.Rotation);
                    }
                }
            }
        }

        /// <summary>取某个复制体当前的表现位置/姿态/速度（供端口写进引擎身体）。</summary>
        public bool TryGetPresentation(int key, out Vector3 position, out Quaternion rotation,
            out Vector3 velocity, out Vector2 lookAngles)
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;
            velocity = Vector3.Zero;
            lookAngles = Vector2.Zero;
            if (!m_states.TryGetValue(key, out State state) || !state.HasRecord) return false;
            if (!state.PresentationInitialized)
            {
                position = state.Position;
                rotation = state.Rotation;
                velocity = state.Velocity;
                lookAngles = state.LookAngles;
                return true;
            }
            position = state.PresentationPosition;
            rotation = state.PresentationRotation;
            velocity = state.HasSmoothedVelocity ? state.SmoothedVelocity : state.Velocity;
            lookAngles = state.PresentationLookAngles;
            return true;
        }

        /// <summary>标记/取消"本机玩家骑乘或站立的坐骑"（始终平滑，不贴快照）。</summary>
        public void SetAlwaysSmooth(int key, bool value)
        {
            if (m_states.TryGetValue(key, out State state)) state.AlwaysSmooth = value;
        }

        public void Remove(int key) => m_states.Remove(key);

        public void Clear()
        {
            m_states.Clear();
        }

        /// <summary>把当前 key 列表写进 target（复用列表，避免每帧分配）。</summary>
        public void CopyKeys(List<int> target)
        {
            if (target == null) return;
            target.Clear();
            foreach (int key in m_states.Keys) target.Add(key);
        }

        private static void Place(State state, Vector3 position, Quaternion rotation)
        {
            state.PresentationPosition = position;
            state.PresentationRotation = rotation;
            state.PresentationLookAngles = state.LookAngles;
            state.SmoothedVelocity = state.Velocity;
            state.HasSmoothedVelocity = true;
            state.PresentationInitialized = true;
        }
    }
}

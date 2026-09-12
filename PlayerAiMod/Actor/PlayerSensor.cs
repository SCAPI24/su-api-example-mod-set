using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 默认传感器：读取绑定玩家与世界里其它玩家的**公开**状态。
    ///
    /// 只读，不写任何游戏状态。需要读私有后端字段的项目（例如俯仰角在
    /// ComponentLocomotion.m_lookAngles 里）留到移植输入层时一起补，见 README §7。
    ///
    /// Source: Survivalcraft/Game/ComponentLocomotion.cs:303（yaw 提取公式）
    /// Source: Survivalcraft/Game/ComponentInput.cs:91-94（输入是否会被采纳）
    /// Source: Survivalcraft/Game/PlayerData.cs:84 Name / :124 IsReadyForPlaying
    /// Source: Survivalcraft/Game/SubsystemPlayers.cs（ComponentPlayers，含联机远端玩家）
    /// </summary>
    public sealed class PlayerSensor : IAiSensor, IAiWorldSensor
    {
        private readonly AiActor m_actor;

        public PlayerSensor(AiActor actor)
        {
            if (actor == null)
                throw new ArgumentNullException(nameof(actor));
            m_actor = actor;
        }

        private ComponentPlayer Player
        {
            get { return m_actor.Player; }
        }

        public bool IsReady
        {
            get
            {
                ComponentPlayer player = Player;
                return player != null
                    && player.ComponentBody != null
                    && player.ComponentHealth != null
                    && GameManager.Project != null;
            }
        }

        public bool IsInputAccepted
        {
            get
            {
                ComponentPlayer player = Player;
                if (player == null || player.PlayerData == null)
                    return false;

                // Source: ComponentInput.cs:91-94
                // !Window.IsActive || !PlayerData.IsReadyForPlaying 时，游戏把 PlayerInput 整个置空，
                // 于是"注入到引擎输入数组里的按键/鼠标"不会被采纳（肉眼看不出，AI 必须自查）。
                return Window.IsActive && player.PlayerData.IsReadyForPlaying;
            }
        }

        public string PlayerName
        {
            get
            {
                try
                {
                    return Player != null && Player.PlayerData != null ? Player.PlayerData.Name : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public Vector3 Position
        {
            get
            {
                ComponentPlayer player = Player;
                return player != null && player.ComponentBody != null
                    ? player.ComponentBody.Position
                    : Vector3.Zero;
            }
        }

        public Vector3 EyePosition
        {
            get
            {
                ComponentPlayer player = Player;
                if (player == null)
                    return Vector3.Zero;
                return player.ComponentCreatureModel != null
                    ? player.ComponentCreatureModel.EyePosition
                    : Position;
            }
        }

        public Vector3 Velocity
        {
            get
            {
                ComponentPlayer player = Player;
                return player != null && player.ComponentBody != null
                    ? player.ComponentBody.Velocity
                    : Vector3.Zero;
            }
        }

        public float YawRadians
        {
            get
            {
                ComponentPlayer player = Player;
                if (player == null || player.ComponentBody == null)
                    return 0f;

                Quaternion rotation = player.ComponentBody.Rotation;
                // Source: ComponentLocomotion.cs:303 —— 游戏自身就是这么从四元数里取 yaw 的。
                return MathUtils.Atan2(
                    2f * rotation.Y * rotation.W - 2f * rotation.X * rotation.Z,
                    1f - 2f * rotation.Y * rotation.Y - 2f * rotation.Z * rotation.Z);
            }
        }

        public bool TryGetPitch(out float pitchRadians)
        {
            // TODO(输入层移植): ComponentLocomotion 的俯仰角在私有后端字段 m_lookAngles.Y
            //（setter 为 private，范围 ±82°）。等执行器接入输入层时，一并用同一套
            // ModParentField 通道读取，避免现在就把白名单机制拆成两处。
            pitchRadians = 0f;
            return false;
        }

        public float Health
        {
            get
            {
                ComponentPlayer player = Player;
                return player != null && player.ComponentHealth != null
                    ? player.ComponentHealth.Health
                    : 0f;
            }
        }

        public bool TryFindPlayer(string name, out AiActorView view)
        {
            view = default(AiActorView);
            if (string.IsNullOrEmpty(name))
                return false;

            SubsystemPlayers players = FindPlayers();
            if (players == null)
                return false;

            for (int i = 0; i < players.ComponentPlayers.Count; i++)
            {
                ComponentPlayer candidate = players.ComponentPlayers[i];
                string candidateName = null;
                try
                {
                    candidateName = candidate != null && candidate.PlayerData != null
                        ? candidate.PlayerData.Name
                        : null;
                }
                catch (Exception)
                {
                    candidateName = null;
                }

                if (candidateName != null
                    && string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
                {
                    view = Describe(candidate);
                    return true;
                }
            }

            return false;
        }

        public bool TryFindNearestPlayer(out AiActorView view)
        {
            view = default(AiActorView);

            SubsystemPlayers players = FindPlayers();
            if (players == null)
                return false;

            bool found = false;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < players.ComponentPlayers.Count; i++)
            {
                ComponentPlayer candidate = players.ComponentPlayers[i];
                if (candidate == null || ReferenceEquals(candidate, Player))
                    continue;

                AiActorView candidateView = Describe(candidate);
                if (candidateView.Distance < bestDistance)
                {
                    bestDistance = candidateView.Distance;
                    view = candidateView;
                    found = true;
                }
            }

            return found;
        }

        private SubsystemPlayers FindPlayers()
        {
            Project project = GameManager.Project;
            return project != null ? project.FindSubsystem<SubsystemPlayers>(false) : null;
        }

        // ---------------------------------------------------------------- 扩展观察层（IAiWorldSensor）

        /// <summary>
        /// 自身生命体征 + 本体状态。
        /// Source: ComponentVitalStats.cs:72-126（Food/Stamina/Sleep/Temperature/Wetness 都是公开只读属性）
        /// Source: ComponentHealth.cs:40,46（Health / Air）
        /// Source: ComponentBody.cs:107,99（StandingOnValue / ImmersionDepth）
        /// </summary>
        public bool TryGetSelfState(out AiSelfState state)
        {
            state = default(AiSelfState);

            ComponentPlayer player = Player;
            if (player == null)
                return false;

            if (player.ComponentHealth != null)
            {
                state.Health = player.ComponentHealth.Health;
                state.Air = player.ComponentHealth.Air;
                state.AirCapacity = player.ComponentHealth.AirCapacity;
            }

            if (player.ComponentVitalStats != null)
            {
                state.Food = player.ComponentVitalStats.Food;
                state.Stamina = player.ComponentVitalStats.Stamina;
                state.Sleep = player.ComponentVitalStats.Sleep;
                state.Temperature = player.ComponentVitalStats.Temperature;
                state.Wetness = player.ComponentVitalStats.Wetness;
            }

            if (player.ComponentBody != null)
            {
                state.Position = player.ComponentBody.Position;
                state.Velocity = player.ComponentBody.Velocity;
                state.ImmersionDepth = player.ComponentBody.ImmersionDepth;
                if (player.ComponentBody.StandingOnValue.HasValue)
                {
                    state.IsOnGround = true;
                    state.StandingOnValue = player.ComponentBody.StandingOnValue.Value;
                }
            }

            state.YawRadians = YawRadians;
            return true;
        }

        /// <summary>
        /// 最近的生物（不含玩家）。
        /// Source: SubsystemBodies.cs:FindBodiesAroundPoint（只扫附近，不遍历全世界）
        /// Source: ComponentCreature.cs:Category / DisplayName；ComponentName.cs:Name
        /// </summary>
        // ---------------------------------------------------------------- 模型节点（骨骼）

        /// <summary>最近一次"找生物"命中的身体：模型节点要用它拿 `ComponentCreatureModel`。</summary>
        private ComponentBody m_lastCreatureBody;

        /// <summary>
        /// 把 actor 快照解析回引擎对象。
        ///
        /// 玩家用 `PlayerIndex`（在 `SubsystemPlayers.ComponentPlayers` 里唯一且稳定，网络玩家也一样）；
        /// 生物没有公开的稳定 id，就用"最近一次找生物命中的那只"——它与黑板里的值同一个来源、
        /// 差一个 tick 以内，够用；找不到再按位置在附近重扫一次兜底。
        /// </summary>
        private ComponentCreature ResolveCreature(AiActorView actor)
        {
            if (actor.IsPlayer && actor.PlayerIndex >= 0)
            {
                SubsystemPlayers players = FindPlayers();
                if (players != null)
                {
                    for (int i = 0; i < players.ComponentPlayers.Count; i++)
                    {
                        ComponentPlayer candidate = players.ComponentPlayers[i];
                        if (candidate != null && candidate.PlayerData != null
                            && candidate.PlayerData.PlayerIndex == actor.PlayerIndex)
                        {
                            return candidate;              // ComponentPlayer : ComponentCreature
                        }
                    }
                }
                return null;
            }

            ComponentBody cached = m_lastCreatureBody;
            if (cached != null && cached.Entity != null
                && Vector3.Distance(cached.Position, actor.Position) <= 2f)
            {
                return cached.Entity.FindComponent<ComponentCreature>(false);
            }

            // 兜底：在 actor 位置附近重扫（半径 2 m 足够，因为黑板里的值就是一个 tick 前的）
            Project project = GameManager.Project;
            SubsystemBodies bodies = project != null
                ? project.FindSubsystem<SubsystemBodies>(false) : null;
            if (bodies == null)
                return null;

            var result = new DynamicArray<ComponentBody>();
            bodies.FindBodiesAroundPoint(new Vector2(actor.Position.X, actor.Position.Z), 2f, result);
            ComponentBody best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < result.Count; i++)
            {
                ComponentBody body = result.Array[i];
                if (body == null || body.Entity == null)
                    continue;
                float distance = Vector3.Distance(body.Position, actor.Position);
                if (distance > 2f || distance >= bestDistance)
                    continue;
                if (body.Entity.FindComponent<ComponentCreature>(false) == null)
                    continue;
                best = body;
                bestDistance = distance;
            }
            m_lastCreatureBody = best;
            return best != null ? best.Entity.FindComponent<ComponentCreature>(false) : null;
        }

        /// <summary>骨骼在**模型空间**（脚底为原点）的静止姿态位置：从该骨骼往根逐级合成。</summary>
        private static Vector3 RestPoseOffset(ModelBone bone)
        {
            Vector3 local = Vector3.Zero;
            for (ModelBone current = bone; current != null; current = current.ParentBone)
                local = Vector3.Transform(local, current.Transform);
            return local;
        }

        public bool TryGetBoneWorldPosition(AiActorView actor, string boneName, out Vector3 world)
        {
            world = Vector3.Zero;
            if (string.IsNullOrEmpty(boneName) || !IsReady)
                return false;

            ComponentCreature creature = ResolveCreature(actor);
            if (creature == null || creature.ComponentCreatureModel == null
                || creature.ComponentBody == null)
                return false;

            Model model = creature.ComponentCreatureModel.Model;
            if (model == null)
                return false;

            ModelBone bone = model.FindBone(boneName, false);
            if (bone == null)
                return false;

            world = Vector3.Transform(RestPoseOffset(bone), creature.ComponentBody.Matrix);
            return true;
        }

        public bool TryListBones(AiActorView actor, out List<string> names,
            out List<Vector3> worldPositions)
        {
            names = new List<string>();
            worldPositions = new List<Vector3>();
            if (!IsReady)
                return false;

            ComponentCreature creature = ResolveCreature(actor);
            if (creature == null || creature.ComponentCreatureModel == null
                || creature.ComponentBody == null)
                return false;

            Model model = creature.ComponentCreatureModel.Model;
            if (model == null)
                return false;

            Matrix body = creature.ComponentBody.Matrix;
            for (int i = 0; i < model.Bones.Count; i++)
            {
                ModelBone bone = model.Bones[i];
                names.Add(bone.Name);
                worldPositions.Add(Vector3.Transform(RestPoseOffset(bone), body));
            }
            return names.Count > 0;
        }

        public bool TryFindNearestCreature(int categoryMask, float maxDistance, out AiActorView view)
        {
            view = default(AiActorView);

            Project project = GameManager.Project;
            SubsystemBodies bodies = project != null
                ? project.FindSubsystem<SubsystemBodies>(false) : null;
            if (bodies == null)
                return false;

            Vector3 origin = Position;
            float radius = maxDistance > 0f ? maxDistance : 64f;

            var result = new DynamicArray<ComponentBody>();
            bodies.FindBodiesAroundPoint(new Vector2(origin.X, origin.Z), radius, result);

            bool found = false;
            float best = float.MaxValue;
            for (int i = 0; i < result.Count; i++)
            {
                ComponentBody body = result.Array[i];
                if (body == null)
                    continue;

                ComponentCreature creature = body.Entity != null
                    ? body.Entity.FindComponent<ComponentCreature>(false) : null;
                if (creature == null)
                    continue;                                  // 不是生物（掉落物/投射物/方块）
                if (ReferenceEquals(creature, Player))
                    continue;                                  // 自己不算

                if (categoryMask != 0 && ((int)creature.Category & categoryMask) == 0)
                    continue;

                float distance = Vector3.Distance(body.Position, origin);
                if (distance > radius || distance >= best)
                    continue;

                best = distance;
                view = new AiActorView
                {
                    Name = CreatureName(creature),
                    CategoryMask = (int)creature.Category,
                    Kind = "creature",
                    IsPlayer = false,
                    IsSelf = false,
                    PlayerIndex = -1,
                    Position = body.Position,
                    Velocity = body.Velocity,
                    Distance = distance
                };
                found = true;
                m_lastCreatureBody = body;                     // 模型节点要用它拿 ComponentCreatureModel
            }

            return found;
        }

        /// <summary>
        /// 最近的掉落物（Pickable）。
        /// Source: SubsystemPickables.cs:49（Pickables 只读列表）、Pickable.cs:7,WorldItem.cs:9,11
        /// </summary>
        public bool TryFindNearestPickable(float maxDistance, out AiActorView view)
        {
            view = default(AiActorView);

            Project project = GameManager.Project;
            SubsystemPickables pickables = project != null
                ? project.FindSubsystem<SubsystemPickables>(false) : null;
            if (pickables == null)
                return false;

            Vector3 origin = Position;
            float radius = maxDistance > 0f ? maxDistance : 64f;

            bool found = false;
            float best = float.MaxValue;
            ReadOnlyList<Pickable> list = pickables.Pickables;
            for (int i = 0; i < list.Count; i++)
            {
                Pickable pickable = list[i];
                if (pickable == null || pickable.ToRemove)
                    continue;

                float distance = Vector3.Distance(pickable.Position, origin);
                if (distance > radius || distance >= best)
                    continue;

                best = distance;
                view = new AiActorView
                {
                    Name = BlockName(pickable.Value),
                    Kind = "pickable",
                    Value = pickable.Value,
                    Count = pickable.Count,
                    IsPlayer = false,
                    IsSelf = false,
                    PlayerIndex = -1,
                    Position = pickable.Position,
                    Velocity = pickable.Velocity,
                    Distance = distance
                };
                found = true;
            }

            return found;
        }

        /// <summary>
        /// 从眼睛沿某个方向射线，取第一个非空气方块。
        /// Source: SubsystemTerrain.cs:88（Raycast 的四参数版本，`action` 允许为 null）
        /// Source: TerrainRaycastResult.cs（Value / CellFace / Distance / HitPoint）
        /// </summary>
        public bool TryRaycastBlock(Vector3 direction, float maxDistance, out AiBlockHit hit)
        {
            // 老签名 = "从我眼睛出发"；换起点的能力走 TryRaycastBlockFrom（通用探测服务用）
            return TryRaycastBlockFrom(EyePosition, direction, maxDistance, out hit);
        }

        public bool TryRaycastBlockFrom(Vector3 origin, Vector3 direction, float maxDistance,
            out AiBlockHit hit)
        {
            hit = default(AiBlockHit);

            if (direction.LengthSquared() < 1e-6f)
                return false;

            Project project = GameManager.Project;
            SubsystemTerrain terrain = project != null
                ? project.FindSubsystem<SubsystemTerrain>(false) : null;
            if (terrain == null || terrain.Terrain == null)
                return false;

            Vector3 start = origin;
            Vector3 end = start + Vector3.Normalize(direction) * (maxDistance > 0f ? maxDistance : 8f);

            // skipAirBlocks: true —— 只要"看得见/挡得住"的那个方块，空气不算
            TerrainRaycastResult? result = terrain.Raycast(start, end, true, true, null);
            if (!result.HasValue)
                return false;

            TerrainRaycastResult value = result.Value;
            int contents = Terrain.ExtractContents(value.Value);
            hit = new AiBlockHit
            {
                Contents = contents,
                Value = value.Value,
                Name = BlockName(value.Value),
                Distance = value.Distance,
                X = value.CellFace.X,
                Y = value.CellFace.Y,
                Z = value.CellFace.Z,
                HitPoint = value.HitPoint()
            };
            return true;
        }

        /// <summary>
        /// 按坐标读一个格子（挖方块时用来判"挖掉了没"）。
        /// Source: Terrain.GetCellValue / Terrain.ExtractContents（与 CmdBridgeMod 的方块扫描同一套 API）
        /// </summary>
        public bool TryPeekBlock(int x, int y, int z, out int contents, out string name)
        {
            contents = 0;
            name = null;

            Project project = GameManager.Project;
            SubsystemTerrain terrain = project != null
                ? project.FindSubsystem<SubsystemTerrain>(false) : null;
            if (terrain == null || terrain.Terrain == null)
                return false;

            int value = terrain.Terrain.GetCellValue(x, y, z);
            contents = Terrain.ExtractContents(value);
            name = contents == 0 ? null : BlockName(value);
            return true;
        }

        /// <summary>到目标点的视线是否通畅（地形不挡就算通畅）。</summary>
        public bool HasLineOfSight(Vector3 target, out float distance)
        {
            Vector3 origin = EyePosition;
            distance = Vector3.Distance(origin, target);
            if (distance < 0.01f)
                return true;

            Project project = GameManager.Project;
            SubsystemTerrain terrain = project != null
                ? project.FindSubsystem<SubsystemTerrain>(false) : null;
            if (terrain == null || terrain.Terrain == null)
                return false;

            TerrainRaycastResult? result = terrain.Raycast(origin, target, true, true, null);
            if (!result.HasValue)
                return true;                                   // 一路没打到方块 = 通视

            // 命中的方块比目标更远 → 目标在它前面，视线仍然通畅
            return result.Value.Distance >= distance - 0.05f;
        }

        // ---------------------------------------------------------------- 寻路（游戏侧 A*）
        //
        // Source: SubsystemPathfinding.cs:231（QueuePathSearch 只入队，后台线程每 250ms 处理一条，
        //         队列上限 10 —— 满了会给出"空且已完成"的结果，所以要判空而不是当成成功）
        // Source: AStar.cs:39-47（BuildPathFromEndNode：数组是从**终点往起点**填的）
        // Source: ComponentPathfinding.cs:65,167（游戏自己传 minDistance=range、BoxSize=角色碰撞盒）

        private readonly PathfindingResult m_path = new PathfindingResult();
        private AiPathStatus m_pathStatus = AiPathStatus.Idle;

        public bool TryRequestPath(Vector3 destination, float arriveRadius, int maxPositionsToCheck,
            out string error)
        {
            error = null;

            Project project = GameManager.Project;
            SubsystemPathfinding subsystem = project != null
                ? project.FindSubsystem<SubsystemPathfinding>(false) : null;
            if (subsystem == null)
            {
                error = "no pathfinding subsystem (world not loaded?)";
                m_pathStatus = AiPathStatus.Failed;
                return false;
            }

            ComponentPlayer player = Player;
            if (player == null || player.ComponentBody == null)
            {
                error = "no controllable player body";
                m_pathStatus = AiPathStatus.Failed;
                return false;
            }

            m_path.Path.Clear();
            m_path.PathCost = 0f;
            m_path.PositionsChecked = 0;
            m_pathStatus = AiPathStatus.Searching;

            subsystem.QueuePathSearch(
                player.ComponentBody.Position,
                destination,
                arriveRadius > 0f ? arriveRadius : 1.5f,
                player.ComponentBody.BoxSize,
                false,                                       // ignoreDoors：门也算路障，让 A* 自己绕
                maxPositionsToCheck > 0 ? maxPositionsToCheck : 500,
                m_path);
            return true;
        }

        public AiPathStatus GetPathStatus()
        {
            // 后台线程算完会把 IsCompleted 置位（volatile）—— 只在观察到"完成"之后才去读 Path
            if (m_pathStatus == AiPathStatus.Searching && m_path.IsCompleted)
            {
                // 队列满（或没找到路）时游戏给的是"空且已完成"，那不是"走到了"
                m_pathStatus = m_path.Path.Count > 0 ? AiPathStatus.Ready : AiPathStatus.Failed;
            }
            return m_pathStatus;
        }

        public int CopyPath(Vector3[] buffer)
        {
            if (buffer == null || m_path.Path.Count == 0)
                return 0;

            // 从数组末尾往前拷：末尾 = 离自己最近的航点（见上面 BuildPathFromEndNode 的说明）
            int count = Math.Min(buffer.Length, m_path.Path.Count);
            for (int i = 0; i < count; i++)
                buffer[i] = m_path.Path.Array[m_path.Path.Count - 1 - i];
            return count;
        }

        public void ClearPath()
        {
            m_pathStatus = AiPathStatus.Idle;
            m_path.Path.Clear();
            m_path.IsCompleted = false;
            m_path.IsInProgress = false;
        }

        /// <summary>生物名：优先实体名（ComponentName），退回生物的显示名。</summary>
        private static string CreatureName(ComponentCreature creature)
        {
            try
            {
                ComponentName name = creature.Entity != null
                    ? creature.Entity.FindComponent<ComponentName>(false) : null;
                if (name != null && !string.IsNullOrEmpty(name.Name))
                    return name.Name;
                return creature.DisplayName;
            }
            catch (Exception)
            {
                return creature.DisplayName;
            }
        }

        /// <summary>方块名：`BlocksManager.Blocks[contents].GetDisplayName(...)`，取不到就给 `#id`。</summary>
        private static string BlockName(int value)
        {
            try
            {
                int contents = Terrain.ExtractContents(value);
                if (contents <= 0 || contents >= BlocksManager.Blocks.Length)
                    return "#" + contents;
                Block block = BlocksManager.Blocks[contents];
                if (block == null)
                    return "#" + contents;
                SubsystemTerrain terrain = GameManager.Project != null
                    ? GameManager.Project.FindSubsystem<SubsystemTerrain>(false) : null;
                string name = block.GetDisplayName(terrain, value);
                if (!string.IsNullOrEmpty(name))
                    return name;
                return string.IsNullOrEmpty(block.DefaultDisplayName)
                    ? "#" + contents : block.DefaultDisplayName;
            }
            catch (Exception)
            {
                return "#" + Terrain.ExtractContents(value);
            }
        }

        private AiActorView Describe(ComponentPlayer candidate)
        {
            var view = new AiActorView
            {
                IsSelf = ReferenceEquals(candidate, Player),
                IsPlayer = true,
                PlayerIndex = -1,
                Name = null,
                Position = Vector3.Zero,
                Velocity = Vector3.Zero,
                Distance = 0f
            };

            if (candidate == null)
                return view;

            try
            {
                if (candidate.PlayerData != null)
                {
                    view.PlayerIndex = candidate.PlayerData.PlayerIndex;
                    view.Name = candidate.PlayerData.Name;
                }
            }
            catch (Exception)
            {
                // 名字/索引读取失败不影响位置读取。
            }

            if (candidate.ComponentBody != null)
            {
                view.Position = candidate.ComponentBody.Position;
                view.Velocity = candidate.ComponentBody.Velocity;
                view.Distance = Vector3.Distance(view.Position, Position);
            }

            return view;
        }
    }
}

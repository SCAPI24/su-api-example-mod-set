using System;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Creatures;
using SuMultiplayer.Modules;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 生物世界的引擎适配器：把 <see cref="ICreatureWorld"/> 端口落到真实的
    /// <c>SubsystemSpawn</c> / <c>SubsystemCreatureSpawn</c> / <c>SubsystemBodies</c> 上。
    ///
    /// 这里是**旧实现逻辑的逐段移植**，不是重新设计：
    ///   · <c>BeginRemoteSimulationViewScope</c> ← 旧 SuSubsystemCreatureSpawn 的宿主分支
    ///     （Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:834-867）；
    ///   · <c>BeginRemoteDespawnProtectionScope</c> ← 旧 SuSubsystemSpawn 的宿主分支
    ///     （同文件 :1041-1089）；
    ///   · <c>MaintainHostPopulation</c> ← 旧 MaintainRemoteCreatureSpawning
    ///     （同文件 :869-971，含 1 秒节流、48 米候选块、26 只上限、恢复 SpawnsData）；
    ///   · <c>TryCreateReplica</c> / <c>TryApplyReplicaState</c> ← 旧 EnsureRemoteAnimal /
    ///     HandleAnimalBodyUpdate（Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs）。
    ///
    /// 没有任何静态运行时状态：适配器由组合根创建，子系统的 Coordinator 由模块每帧注入。
    /// </summary>
    public sealed class CreatureWorldAdapter : ICreatureWorld, ICreatureRegionSource
    {
        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly NetworkAvatarSpawner m_avatars;
        private readonly IModuleDiagnostics m_diagnostics;

        private Project m_project;
        private SuSpawnSubsystem m_spawnSubsystem;
        private SuCreatureSpawnSubsystem m_creatureSpawnSubsystem;
        private bool m_clientReplicaMode;
        private bool m_missingSubsystemLogged;
        private double m_nextMaintainTime;
        private int m_maintainCursor;

        private readonly Dictionary<int, Entity> m_replicas = new Dictionary<int, Entity>();
        private readonly Dictionary<int, string> m_replicaTemplates = new Dictionary<int, string>();

        // 主机：每只生物上一次被枚举时的状态（判断"在动 / 刚受伤"用；只影响同步频率，不上线）。
        private readonly Dictionary<object, HostCreatureActivityState> m_hostActivityStates =
            new Dictionary<object, HostCreatureActivityState>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> m_seenHostTokens =
            new HashSet<object>(ReferenceEqualityComparer.Instance);
        private double m_now;

        private long m_spawnedByMaintenance;
        private long m_restoredRecords;
        private long m_overflowTrimmed;
        private long m_unassignedRemoved;
        private long m_maintenanceRuns;
        private long m_viewScopeRuns;
        private long m_despawnScopeRuns;
        private long m_healthApplyFailures;

        // ---------------------------------------------------------------- 客户端复制体：分项计数
        //
        // 这些计数是"复制体到底有没有真的进世界"的唯一可审计口径，逐段对应：
        //   尝试创建 → 实体创建成功且**校验**已挂进 Project/SubsystemBodies（attached）
        //   → 每帧表现层写入成功（presentation）→ 键不在保留集合被裁掉（removedTrimmed）
        //   → 世界里不在分配集合的其它生物实体被清掉（removedUnassigned）
        //   → 创建/挂载失败（按原因分开，绝不把失败算成成功）。
        private long m_createAttempts;
        private long m_createDuplicate;
        private long m_createdRaw;
        private long m_createdAttached;
        private long m_createSkippedNoWorld;
        private long m_createRejectedRecord;
        private long m_createRejectedTemplate;
        private long m_createExceptions;
        private long m_attachFailed;
        private long m_presentationApplied;
        private long m_presentationFailed;
        private long m_removedTrimmed;
        private long m_entityLost;
        private long m_keyMismatch;

        /// <summary>复制体应该落在的半径（米；由模块告知，只用于可见性对账，不改变分配）。</summary>
        private float m_clientRegionRadius = CreatureAllocation.DefaultRegionRadiusMeters;

        // 最近一次可见性对账结果（状态行/日志用；全部是"采样值"，不是累积计数）。
        private long m_visibilityChecks;
        private int m_replicasInRegion;
        private int m_replicasOutOfRegion;
        private float m_nearestReplicaDistance = float.PositiveInfinity;
        private float m_farthestReplicaDistance = float.PositiveInfinity;

        /// <summary>限流日志：key → 上次写日志的时间（毫秒，m_now 口径）。</summary>
        private readonly Dictionary<string, long> m_lastLogTimes = new Dictionary<string, long>(StringComparer.Ordinal);

        public CreatureWorldAdapter(IModParentField parentField, IModParentMethod parentMethod,
            NetworkAvatarSpawner avatars, IModuleDiagnostics diagnostics)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_avatars = avatars;
            m_diagnostics = diagnostics;
        }

        /// <summary>两个生成子系统读取它：客户端 = 不跑原生生成。</summary>
        public bool SuppressNativeSpawning => m_clientReplicaMode;

        /// <summary>主机分配给本端的区域半径（米）：只用于"复制体是否真的在本地玩家周围"的对账。</summary>
        public void ConfigureClientRegion(float radiusMeters)
        {
            m_clientRegionRadius = radiusMeters > 0f
                ? radiusMeters
                : CreatureAllocation.DefaultRegionRadiusMeters;
        }

        public long ReplicaCount => m_replicas.Count;

        /// <summary>
        /// 世界是否真的可操作：Project 已加载、且没有被换新/卸载。
        /// 客户端在世界传输期间会先收到生物全量快照——模块靠它把整批记录延后，
        /// 而不是逐条创建失败（实机 createFail=50 / orphanUpdate=550 的根因）。
        /// </summary>
        public bool HasWorld
        {
            get
            {
                Project project = m_project;
                return project != null && ReferenceEquals(project, GameManager.Project);
            }
        }

        /// <summary>主机侧活动状态缓存（按实体对象身份；实体销毁后由枚举清理掉）。</summary>
        private sealed class HostCreatureActivityState
        {
            public bool HasPrevious;
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.Identity;
            public float Health = 1f;
            public double HighPriorityUntil;
        }

        // ------------------------------------------------------------------ 生命周期 / 绑定

        public bool BeginTick(bool isHost, bool clientReplicaMode, double now, out string reason)
        {
            reason = string.Empty;
            m_now = now;
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                // Project 换新（整图重下 / 重进世界）：旧实体已经随旧 Project 销毁，只丢掉引用。
                m_replicas.Clear();
                m_replicaTemplates.Clear();
                m_hostActivityStates.Clear();
                m_seenHostTokens.Clear();
                m_spawnSubsystem = null;
                m_creatureSpawnSubsystem = null;
                m_missingSubsystemLogged = false;
                m_project = project;
            }
            m_clientReplicaMode = clientReplicaMode;
            if (project == null)
            {
                reason = "当前没有世界（GameManager.Project 为空）";
                return false;
            }
            EnsureSubsystems(project, clientReplicaMode);
            if (!clientReplicaMode && m_replicas.Count > 0)
            {
                RemoveAllReplicas("切换到非复制模式（主机 / 单机），清掉客户端复制体");
            }
            return true;
        }

        private void EnsureSubsystems(Project project, bool clientReplicaMode)
        {
            if (m_spawnSubsystem == null)
            {
                m_spawnSubsystem = project.FindSubsystem<SuSpawnSubsystem>(false);
                if (m_spawnSubsystem != null) m_spawnSubsystem.Coordinator = this;
            }
            if (m_creatureSpawnSubsystem == null)
            {
                m_creatureSpawnSubsystem = project.FindSubsystem<SuCreatureSpawnSubsystem>(false);
                if (m_creatureSpawnSubsystem != null) m_creatureSpawnSubsystem.Coordinator = this;
            }
            if (clientReplicaMode && !m_missingSubsystemLogged &&
                (m_spawnSubsystem == null || m_creatureSpawnSubsystem == null))
            {
                m_missingSubsystemLogged = true;
                Warn("客户端没有找到注入后的生成子系统（Spawn=" +
                    (m_spawnSubsystem == null ? "缺失" : "已注入") + " / CreatureSpawn=" +
                    (m_creatureSpawnSubsystem == null ? "缺失" : "已注入") +
                    "）→ 引擎的原生随机生成无法被抑制；已收到的复制体仍会显示，" +
                    "不在主机分配集合里的生物会被每秒清理并计数（明确记录，不假装成功）");
            }
        }

        // ------------------------------------------------------------------ 客户端区域（ICreatureRegionSource）

        public IReadOnlyList<int> ClientIds
        {
            get
            {
                if (m_avatars == null) return Array.Empty<int>();
                var ids = new List<int>();
                foreach (int clientId in m_avatars.ClientIds) ids.Add(clientId);
                return ids;
            }
        }

        public bool TryGetClientPosition(int clientId, out Vector3 position, out string reason)
        {
            position = Vector3.Zero;
            reason = string.Empty;
            if (m_avatars == null)
            {
                reason = "联机角色表不可用（NetworkAvatarSpawner 未注入）";
                return false;
            }
            return m_avatars.TryGetAvatarCell(clientId, out position, out reason);
        }

        // ------------------------------------------------------------------ 主机：收集生物

        public int CaptureHostCreatures(List<CreatureStateRecord> target, out string reason)
        {
            reason = string.Empty;
            if (target == null) throw new ArgumentNullException(nameof(target));
            Project project = m_project;
            if (project == null) { reason = "当前没有世界"; return -1; }
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(false);
            if (bodies == null) { reason = "世界没有 SubsystemBodies"; return -1; }
            int count = 0;
            m_seenHostTokens.Clear();
            foreach (ComponentBody body in bodies.Bodies)
            {
                Entity entity = body?.Entity;
                if (entity == null) continue;
                ComponentCreature creature = entity.FindComponent<ComponentCreature>();
                if (creature == null) continue;
                if (entity.FindComponent<ComponentPlayer>() != null) continue;
                string template = entity.ValuesDictionary?.DatabaseObject?.Name;
                if (string.IsNullOrWhiteSpace(template)) continue;

                m_seenHostTokens.Add(entity);
                if (!m_hostActivityStates.TryGetValue(entity, out HostCreatureActivityState activityState))
                {
                    activityState = new HostCreatureActivityState();
                    m_hostActivityStates.Add(entity, activityState);
                }
                float health = creature.ComponentHealth != null ? creature.ComponentHealth.Health : 1f;
                CreatureActivity activity = ComputeHostActivity(entity, creature, body, activityState,
                    health, m_now);
                // 静止的生物速度常有重力/碰撞残余（位置一动不动、速度却在抖）。把它按 0 发布，
                // 否则客户端会拿这份速度外推，静止的动物会往一个方向漂、下一帧再被拉回来。
                // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs（静止拾取物同一处理）
                Vector3 publishedVelocity = (activity & CreatureActivity.Moving) != 0
                    ? body.Velocity
                    : Vector3.Zero;
                activityState.HasPrevious = true;
                activityState.Position = body.Position;
                activityState.Rotation = body.Rotation;
                activityState.Health = health;

                // 复用调用方列表里的记录对象（主机 50 Hz 扫描，不要每次都分配新对象）。
                CreatureStateRecord record;
                if (count < target.Count) record = target[count];
                else
                {
                    record = new CreatureStateRecord();
                    target.Add(record);
                }
                record.Token = entity;
                record.TemplateName = template;
                record.Position = body.Position;
                record.Rotation = body.Rotation;
                record.Velocity = publishedVelocity;
                record.Health = health;
                record.Activity = activity;
                count++;
            }
            if (count < target.Count) target.RemoveRange(count, target.Count - count);
            // 实体销毁后清掉活动状态，避免按对象身份无限增长（实体引用本身不会复用）。
            if (m_hostActivityStates.Count > m_seenHostTokens.Count)
            {
                var stale = new List<object>();
                foreach (object token in m_hostActivityStates.Keys)
                {
                    if (!m_seenHostTokens.Contains(token)) stale.Add(token);
                }
                for (int i = 0; i < stale.Count; i++) m_hostActivityStates.Remove(stale[i]);
            }
            if (count == 0) reason = "世界里当前没有非玩家生物";
            return count;
        }

        /// <summary>0.8° 的转向（1 - |dot| ≈ (θ/2)²）就算"在动"：原地转头也要按距离档平滑同步。</summary>
        private const float RotationMovingThreshold = 0.00005f;

        /// <summary>5 mm 的位移就算"在动"（与 CreatureSyncCadence.PositionEpsilonSquared 同口径）。</summary>
        private const float PositionMovingEpsilonSquared = 0.000025f;

        /// <summary>
        /// 计算一只生物的活动标记（决定同步档位）。逐条对应旧实现的判定：
        ///   · Aggro   ← ComponentChaseBehavior.IsActive && Target != null；
        ///   · Attacking ← ComponentCreatureModel.AttackOrder / IsAttackHitMoment；
        ///   · Fleeing ← 逃跑类 ComponentBehavior 处于激活状态；
        ///   · Damaged ← 生命值比上一次枚举低 0.001 以上，并把高优先级保持 3 秒
        ///               （旧实现 metadata.HighPriorityUntil = now + 3.0）；
        ///   · Moving  ← 位置/朝向变化超过 epsilon，或速度超过 0.1 m/s。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:SendAdaptiveAnimalUpdates
        /// </summary>
        private CreatureActivity ComputeHostActivity(Entity entity, ComponentCreature creature,
            ComponentBody body, HostCreatureActivityState state, float health, double now)
        {
            bool damagedNow = state.HasPrevious && health < state.Health - 0.001f;
            if (damagedNow) state.HighPriorityUntil = now + 3.0;
            bool damaged = now < state.HighPriorityUntil;

            bool positionMoved = !state.HasPrevious ||
                Vector3.DistanceSquared(body.Position, state.Position) >
                    PositionMovingEpsilonSquared;
            bool rotationMoved = !state.HasPrevious ||
                1f - MathUtils.Abs(Quaternion.Dot(body.Rotation, state.Rotation)) >
                    RotationMovingThreshold;
            bool velocityMoved = body.Velocity.LengthSquared() > 0.01f;
            bool moving = positionMoved || rotationMoved || velocityMoved;

            ComponentChaseBehavior chase = entity.FindComponent<ComponentChaseBehavior>();
            bool aggro = chase != null && chase.IsActive && chase.Target != null;
            ComponentCreatureModel model = creature.ComponentCreatureModel;
            bool attacking = model != null && (model.AttackOrder || model.IsAttackHitMoment);
            bool fleeing = false;
            foreach (ComponentBehavior behavior in entity.FindComponents<ComponentBehavior>())
            {
                if (behavior == null || !behavior.IsActive) continue;
                if (!IsFleeingBehavior(behavior)) continue;
                fleeing = true;
                break;
            }

            CreatureActivity activity = CreatureActivity.None;
            if (moving) activity |= CreatureActivity.Moving;
            if (aggro) activity |= CreatureActivity.Aggro;
            if (attacking) activity |= CreatureActivity.Attacking;
            if (fleeing) activity |= CreatureActivity.Fleeing;
            if (damaged) activity |= CreatureActivity.Damaged;
            return activity;
        }

        /// <summary>
        /// 逃离类行为（引擎里没有统一的 ComponentFleeBehavior，按旧实现的行为类逐个判）。
        /// Source: Survivalcraft/Game/ComponentRunAwayBehavior.cs / ComponentMoveAwayBehavior.cs /
        ///         ComponentFlyAwayBehavior.cs / ComponentAvoidPlayerBehavior.cs /
        ///         ComponentSwimAwayBehavior.cs
        /// </summary>
        private static bool IsFleeingBehavior(ComponentBehavior behavior) =>
            behavior is ComponentRunAwayBehavior || behavior is ComponentMoveAwayBehavior ||
            behavior is ComponentFlyAwayBehavior || behavior is ComponentAvoidPlayerBehavior ||
            behavior is ComponentSwimAwayBehavior;

        // ------------------------------------------------------------------ 主机：远端生物维持

        public void MaintainHostPopulation(IReadOnlyList<CreatureRegion> regions, double now, out string reason)
        {
            reason = string.Empty;
            if (m_clientReplicaMode) return;
            Project project = m_project;
            if (project == null) { reason = "当前没有世界"; return; }
            if (regions == null || regions.Count == 0) return;
            if (now < m_nextMaintainTime) return;
            m_nextMaintainTime = now + 1.0;
            m_maintenanceRuns++;

            SubsystemGameWidgets views = project.FindSubsystem<SubsystemGameWidgets>(false);
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            SubsystemSpawn spawn = project.FindSubsystem<SubsystemSpawn>(false);
            if (views == null || terrain?.Terrain == null || spawn == null)
            {
                reason = "缺少 SubsystemGameWidgets / SubsystemTerrain / SubsystemSpawn";
                return;
            }

            var remotePositions = new List<Vector3>();
            for (int i = 0; i < regions.Count; i++)
            {
                Vector3 position = regions[i].Position;
                if (CountRemoteCreatures(project, position.XZ, 68f) < 26) remotePositions.Add(position);
            }
            if (remotePositions.Count == 0) return;

            var visiblePositions = new List<Vector2>();
            foreach (GameWidget gameWidget in views.GameWidgets)
            {
                if (gameWidget?.ActiveCamera == null) continue;
                visiblePositions.Add(gameWidget.ActiveCamera.ViewPosition.XZ);
            }

            var candidateSet = new HashSet<Point2>();
            for (int i = 0; i < remotePositions.Count; i++)
            {
                Vector2 center = remotePositions[i].XZ;
                Point2 min = Game.Terrain.ToChunk(center - new Vector2(48f));
                Point2 max = Game.Terrain.ToChunk(center + new Vector2(48f));
                for (int x = min.X; x <= max.X; x++)
                {
                    for (int z = min.Y; z <= max.Y; z++)
                    {
                        Vector2 chunkCenter = new Vector2(((float)x + 0.5f) * 16f, ((float)z + 0.5f) * 16f);
                        if (Vector2.DistanceSquared(center, chunkCenter) >= 2304f) continue;
                        bool nearVisible = false;
                        for (int v = 0; v < visiblePositions.Count; v++)
                        {
                            if (Vector2.DistanceSquared(visiblePositions[v], chunkCenter) < 2304f)
                            {
                                nearVisible = true;
                                break;
                            }
                        }
                        if (nearVisible) continue;
                        TerrainChunk chunk = terrain.Terrain.GetChunkAtCell(
                            Game.Terrain.ToCell(chunkCenter.X), Game.Terrain.ToCell(chunkCenter.Y));
                        if (chunk != null && chunk.State > TerrainChunkState.InvalidPropagatedLight)
                        {
                            candidateSet.Add(new Point2(x, z));
                        }
                    }
                }
            }
            if (candidateSet.Count == 0) return;

            var candidates = new List<Point2>(candidateSet);
            candidates.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            Point2 selectedPoint = candidates[m_maintainCursor % candidates.Count];
            m_maintainCursor = (m_maintainCursor + 1) % candidates.Count;
            Vector2 selectedChunkCenter = new Vector2(((float)selectedPoint.X + 0.5f) * 16f,
                ((float)selectedPoint.Y + 0.5f) * 16f);
            Vector2 selectedRemoteCenter = remotePositions[0].XZ;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < remotePositions.Count; i++)
            {
                Vector2 candidate = remotePositions[i].XZ;
                float distance = Vector2.DistanceSquared(candidate, selectedChunkCenter);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    selectedRemoteCenter = candidate;
                }
            }

            SpawnChunk spawnChunk = GetOrCreateSpawnChunk(spawn, selectedPoint);
            if (spawnChunk == null)
            {
                reason = "拿不到 SpawnChunk（引擎私有方法不可用）";
                return;
            }
            int localCreatureCount = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
            int availableSlots = Math.Max(0, 26 - localCreatureCount);
            int recordsToRestore = Math.Min(Math.Min(2, availableSlots), spawnChunk.SpawnsData.Count);
            for (int i = 0; i < recordsToRestore; i++)
            {
                SpawnEntityData record = spawnChunk.SpawnsData[0];
                spawnChunk.SpawnsData.RemoveAt(0);
                if (SpawnEntity(spawn, record) != null) m_restoredRecords++;
            }
            localCreatureCount = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
            if (spawnChunk.SpawnsData.Count != 0 || localCreatureCount >= 26) return;

            SubsystemCreatureSpawn creatureSpawn = project.FindSubsystem<SubsystemCreatureSpawn>(false);
            MethodInfo spawnChunkCreatures;
            try
            {
                spawnChunkCreatures = m_parentMethod.GetInstanceMethodInfo(
                    typeof(SubsystemCreatureSpawn), "SpawnChunkCreatures",
                    new[] { typeof(SpawnChunk), typeof(int), typeof(bool) });
            }
            catch (Exception e)
            {
                reason = "拿不到 SpawnChunkCreatures：" + e.GetType().Name + " " + e.Message;
                return;
            }
            if (creatureSpawn == null || spawnChunkCreatures == null)
            {
                reason = "SubsystemCreatureSpawn 或 SpawnChunkCreatures 不可用";
                return;
            }
            var existing = new HashSet<Entity>(FindRemoteCreatures(project, selectedRemoteCenter, 68f));
            using (BeginRemoteCreatureCountScope(project, selectedRemoteCenter, 68f))
            {
                int nonConstantAttempts = spawnChunk.IsSpawned ? 1 : 10;
                int before = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
                spawnChunkCreatures.Invoke(creatureSpawn, new object[] { spawnChunk, nonConstantAttempts, false });
                int after = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
                if (after > before) m_spawnedByMaintenance += after - before;
                if (after < 26)
                {
                    spawnChunkCreatures.Invoke(creatureSpawn, new object[] { spawnChunk, 2, true });
                }
                TrimRemoteCreatureOverflow(project, selectedRemoteCenter, 68f, 26, existing);
            }
            spawnChunk.IsSpawned = true;
        }

        private SpawnChunk GetOrCreateSpawnChunk(SubsystemSpawn spawn, Point2 point)
        {
            try
            {
                MethodInfo method = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemSpawn),
                    "GetOrCreateSpawnChunk", new[] { typeof(Point2) });
                return method?.Invoke(spawn, new object[] { point }) as SpawnChunk;
            }
            catch (Exception e)
            {
                Warn("调用 GetOrCreateSpawnChunk 失败：" + e.GetType().Name + " " + e.Message);
                return null;
            }
        }

        private Entity SpawnEntity(SubsystemSpawn spawn, SpawnEntityData data)
        {
            try
            {
                MethodInfo method = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemSpawn),
                    "SpawnEntity", new[] { typeof(SpawnEntityData) });
                return method?.Invoke(spawn, new object[] { data }) as Entity;
            }
            catch (Exception e)
            {
                Warn("恢复 SpawnsData 记录失败（模板 " + data.TemplateName + "）：" +
                    e.GetType().Name + " " + e.Message);
                return null;
            }
        }

        private int CountRemoteCreatures(Project project, Vector2 center, float radius) =>
            FindRemoteCreatures(project, center, radius).Count;

        private List<Entity> FindRemoteCreatures(Project project, Vector2 center, float radius)
        {
            var result = new List<Entity>();
            SubsystemBodies bodies = project?.FindSubsystem<SubsystemBodies>(false);
            if (bodies == null) return result;
            float radiusSquared = radius * radius;
            var seen = new HashSet<Entity>();
            foreach (ComponentBody body in bodies.Bodies)
            {
                Entity entity = body?.Entity;
                if (entity == null || !seen.Add(entity)) continue;
                if (entity.FindComponent<ComponentCreature>() == null) continue;
                if (entity.FindComponent<ComponentPlayer>() != null) continue;
                if (Vector2.DistanceSquared(body.Position.XZ, center) <= radiusSquared) result.Add(entity);
            }
            return result;
        }

        private void TrimRemoteCreatureOverflow(Project project, Vector2 center, float radius,
            int targetCount, HashSet<Entity> existingCreatures)
        {
            List<Entity> creatures = FindRemoteCreatures(project, center, radius);
            int excess = creatures.Count - targetCount;
            if (excess <= 0) return;
            int removed = 0;
            for (int i = 0; i < creatures.Count && removed < excess; i++)
            {
                Entity entity = creatures[i];
                if (existingCreatures.Contains(entity)) continue;
                if (entity != null && entity.IsAddedToProject)
                {
                    project.RemoveEntity(entity, disposeEntity: true);
                    removed++;
                }
            }
            m_overflowTrimmed += removed;
        }

        private IDisposable BeginRemoteCreatureCountScope(Project project, Vector2 center, float radius)
        {
            SubsystemBodies bodies = project?.FindSubsystem<SubsystemBodies>(false);
            if (bodies == null) return null;
            Dictionary<ComponentBody, Point2> areaByBody;
            try
            {
                areaByBody = m_parentField.GetParentField<Dictionary<ComponentBody, Point2>>(bodies,
                    "m_areaByComponentBody", typeof(SubsystemBodies));
            }
            catch (Exception e)
            {
                Warn("读取 SubsystemBodies.m_areaByComponentBody 失败：" + e.GetType().Name + " " + e.Message);
                return null;
            }
            if (areaByBody == null) return null;
            float radiusSquared = radius * radius;
            var outside = new List<ComponentBody>();
            foreach (ComponentBody body in areaByBody.Keys)
            {
                if (body?.Entity == null) continue;
                if (body.Entity.FindComponent<ComponentCreature>() == null) continue;
                if (Vector2.DistanceSquared(body.Position.XZ, center) > radiusSquared) outside.Add(body);
            }
            if (outside.Count == 0) return null;
            MethodInfo removeBody;
            MethodInfo addBody;
            try
            {
                removeBody = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies),
                    "RemoveBody", new[] { typeof(ComponentBody) });
                addBody = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies),
                    "AddBody", new[] { typeof(ComponentBody) });
            }
            catch (Exception e)
            {
                Warn("拿不到 SubsystemBodies.RemoveBody/AddBody：" + e.GetType().Name + " " + e.Message);
                return null;
            }
            if (removeBody == null || addBody == null) return null;
            for (int i = 0; i < outside.Count; i++) removeBody.Invoke(bodies, new object[] { outside[i] });
            return new SubsystemBodiesScope(bodies, addBody, outside);
        }

        private sealed class SubsystemBodiesScope : IDisposable
        {
            private SubsystemBodies m_bodies;
            private MethodInfo m_addBody;
            private ComponentBody[] m_removed;

            public SubsystemBodiesScope(SubsystemBodies bodies, MethodInfo addBody, List<ComponentBody> removed)
            {
                m_bodies = bodies;
                m_addBody = addBody;
                m_removed = removed.ToArray();
            }

            public void Dispose()
            {
                if (m_bodies == null) return;
                for (int i = 0; i < m_removed.Length; i++)
                {
                    try { m_addBody.Invoke(m_bodies, new object[] { m_removed[i] }); }
                    catch (Exception) { }
                }
                m_bodies = null;
                m_addBody = null;
                m_removed = null;
            }
        }

        // ------------------------------------------------------------------ 主机：原生更新作用域

        /// <summary>
        /// 原生 SubsystemCreatureSpawn.Update 期间，把每个联机角色的 GameWidget 临时挂进
        /// SubsystemGameWidgets.m_gameWidgets，让 SpawnRandomCreature 也围绕远端角色选点。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:834-867
        /// </summary>
        public IDisposable BeginRemoteSimulationViewScope()
        {
            if (m_clientReplicaMode || m_project == null || m_avatars == null) return null;
            SubsystemGameWidgets views = m_project.FindSubsystem<SubsystemGameWidgets>(false);
            if (views == null) return null;
            List<GameWidget> list;
            try
            {
                list = m_parentField.GetParentField<List<GameWidget>>(views, "m_gameWidgets",
                    typeof(SubsystemGameWidgets));
            }
            catch (Exception e)
            {
                Warn("读取 SubsystemGameWidgets.m_gameWidgets 失败：" + e.GetType().Name + " " + e.Message);
                return null;
            }
            if (list == null) return null;
            var added = new List<GameWidget>();
            foreach (int clientId in m_avatars.ClientIds)
            {
                if (!m_avatars.TryGet(clientId, out PlayerData playerData)) continue;
                GameWidget gameWidget = playerData?.GameWidget;
                if (gameWidget == null || list.Contains(gameWidget)) continue;
                gameWidget.ActiveCamera.Update(0f);
                list.Add(gameWidget);
                added.Add(gameWidget);
            }
            if (added.Count == 0) return null;
            m_viewScopeRuns++;
            return new GameWidgetListScope(list, added);
        }

        private sealed class GameWidgetListScope : IDisposable
        {
            private List<GameWidget> m_list;
            private List<GameWidget> m_added;

            public GameWidgetListScope(List<GameWidget> list, List<GameWidget> added)
            {
                m_list = list;
                m_added = added;
            }

            public void Dispose()
            {
                if (m_list == null) return;
                for (int i = m_added.Count - 1; i >= 0; i--) m_list.Remove(m_added[i]);
                m_list = null;
                m_added = null;
            }
        }

        /// <summary>
        /// 原生 SubsystemSpawn.Update 期间保护远端角色附近的 AutoDespawn 生物，避免引擎按
        /// "本机 GameWidget" 判定把它们存回 SpawnsData 并 Despawn。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:1041-1089
        /// </summary>
        public IDisposable BeginRemoteDespawnProtectionScope()
        {
            if (m_clientReplicaMode || m_project == null || m_avatars == null) return null;
            var remotePositions = new List<Vector3>();
            foreach (int clientId in m_avatars.ClientIds)
            {
                if (m_avatars.TryGetAvatarCell(clientId, out Vector3 position, out _))
                    remotePositions.Add(position);
            }
            if (remotePositions.Count == 0) return null;
            SubsystemSpawn spawn = m_project.FindSubsystem<SubsystemSpawn>(false);
            if (spawn == null) return null;

            var protectedSpawns = new List<ComponentSpawn>();
            var allSpawns = new List<ComponentSpawn>();
            foreach (ComponentSpawn item in spawn.Spawns) allSpawns.Add(item);
            for (int i = 0; i < allSpawns.Count; i++)
            {
                ComponentSpawn componentSpawn = allSpawns[i];
                if (componentSpawn == null || !componentSpawn.AutoDespawn ||
                    componentSpawn.ComponentFrame == null) continue;
                bool nearRemote = false;
                for (int p = 0; p < remotePositions.Count; p++)
                {
                    if (Vector3.DistanceSquared(remotePositions[p], componentSpawn.ComponentFrame.Position) <= 3600f)
                    {
                        nearRemote = true;
                        break;
                    }
                }
                if (!nearRemote) continue;

                ComponentCreature creature = componentSpawn.ComponentCreature;
                bool isDead = creature != null && creature.ComponentHealth != null &&
                    creature.ComponentHealth.Health <= 0f;
                try
                {
                    bool isShapeshifting = false;
                    ComponentShapeshifter shapeshifter =
                        componentSpawn.Entity.FindComponent<ComponentShapeshifter>();
                    if (shapeshifter != null)
                    {
                        string target = m_parentField.GetParentField<string>(shapeshifter,
                            "m_spawnEntityTemplateName", typeof(ComponentShapeshifter));
                        isShapeshifting = !string.IsNullOrEmpty(target);
                    }
                    if (isShapeshifting && !componentSpawn.IsDespawning)
                    {
                        componentSpawn.Despawn();
                    }
                    else if (componentSpawn.IsDespawning && !isDead && !isShapeshifting)
                    {
                        m_parentField.ModifyParentField(componentSpawn, "<DespawnTime>k__BackingField",
                            null, typeof(ComponentSpawn));
                        RemovePersistedSpawnRecord(spawn, componentSpawn);
                    }
                    m_parentField.ModifyParentField(componentSpawn, "<AutoDespawn>k__BackingField", false,
                        typeof(ComponentSpawn));
                    protectedSpawns.Add(componentSpawn);
                }
                catch (Exception e)
                {
                    Warn("远端脱困保护跳过一只生物（key 未知：" + componentSpawn.Entity.ValuesDictionary
                        .DatabaseObject.Name + "）：" + e.GetType().Name + " " + e.Message);
                }
            }
            if (protectedSpawns.Count == 0) return null;
            m_despawnScopeRuns++;
            return new AutoDespawnScope(m_parentField, protectedSpawns);
        }

        private sealed class AutoDespawnScope : IDisposable
        {
            private IModParentField m_parentField;
            private List<ComponentSpawn> m_spawns;

            public AutoDespawnScope(IModParentField parentField, List<ComponentSpawn> spawns)
            {
                m_parentField = parentField;
                m_spawns = spawns;
            }

            public void Dispose()
            {
                if (m_spawns == null || m_parentField == null) return;
                for (int i = 0; i < m_spawns.Count; i++)
                {
                    try
                    {
                        m_parentField.ModifyParentField(m_spawns[i],
                            "<AutoDespawn>k__BackingField", true, typeof(ComponentSpawn));
                    }
                    catch (Exception) { }
                }
                m_spawns = null;
                m_parentField = null;
            }
        }

        private void RemovePersistedSpawnRecord(SubsystemSpawn spawn, ComponentSpawn componentSpawn)
        {
            SpawnChunk chunk = spawn.GetSpawnChunk(Game.Terrain.ToChunk(componentSpawn.ComponentFrame.Position.XZ));
            if (chunk == null || chunk.SpawnsData.Count == 0) return;
            string templateName = componentSpawn.Entity.ValuesDictionary.DatabaseObject.Name;
            Vector3 position = componentSpawn.ComponentFrame.Position;
            bool constantSpawn = componentSpawn.ComponentCreature?.ConstantSpawn ?? false;
            chunk.SpawnsData.RemoveAll(record => record.TemplateName == templateName &&
                record.ConstantSpawn == constantSpawn &&
                Vector3.DistanceSquared(record.Position, position) < 0.01f);
        }

        // ------------------------------------------------------------------ 客户端：复制体

        public bool TryCreateReplica(CreatureStateRecord record, out string reason)
        {
            reason = string.Empty;
            m_createAttempts++;
            if (record == null)
            {
                m_createRejectedRecord++;
                reason = "记录为空";
                return false;
            }
            Project project = m_project;
            if (project == null)
            {
                m_createSkippedNoWorld++;
                reason = "当前没有世界";
                return false;
            }
            if (!ReferenceEquals(project, GameManager.Project))
            {
                m_createSkippedNoWorld++;
                reason = "当前没有世界（Project 已卸载或换新）";
                return false;
            }
            if (!IsFinite(record))
            {
                m_createRejectedRecord++;
                reason = "记录含非有限数值";
                return false;
            }
            if (m_replicas.TryGetValue(record.Key, out Entity existing) &&
                existing != null && existing.IsAddedToProject)
            {
                m_createDuplicate++;
                return TryApplyReplicaState(record, out reason);
            }
            string template = record.TemplateName;
            if (string.IsNullOrWhiteSpace(template))
            {
                m_createRejectedTemplate++;
                reason = "记录没有生物模板名（种类未知）";
                return false;
            }
            Entity entity = null;
            try
            {
                entity = DatabaseManager.CreateEntity(project, template, new ValuesDictionary(), true);
                ComponentBody body = entity?.FindComponent<ComponentBody>();
                if (entity == null || body == null)
                {
                    m_createRejectedTemplate++;
                    reason = "模板 \"" + template + "\" 没有 ComponentBody";
                    entity?.Dispose();
                    return false;
                }
                body.Position = record.Position;
                body.Rotation = record.Rotation;
                body.Velocity = record.Velocity;
                // 复制体是纯表现层：关掉重力和地面阻力，避免引擎物理在两次插值写入之间把
                // 它往地下拉（位置每帧都会被表现层覆盖，但重力会让"当帧"看起来往下陷）。
                body.IsGravityEnabled = false;
                body.IsGroundDragEnabled = false;
                project.AddEntity(entity);
                m_createdRaw++;
                // Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:EnsureRemoteAnimal
                // 复制体是表现层：摘掉 AI / 移动 / 生命 / 变形 / 生成状态机，避免本地逻辑与主机权威冲突。
                SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(false);
                ComponentSpawn spawn = entity.FindComponent<ComponentSpawn>();
                if (subsystemUpdate != null)
                {
                    foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                    {
                        if (updateable is ComponentBehavior || updateable is ComponentLocomotion ||
                            updateable is ComponentHealth || updateable is ComponentShapeshifter ||
                            ReferenceEquals(updateable, spawn))
                        {
                            subsystemUpdate.RemoveUpdateable(updateable);
                        }
                    }
                }
                ApplyReplicaHealth(entity, record.Health);
                // "AddEntity 返回了"不等于"世界/观察口真的能看到它"（桥、SubsystemBodies 都以
                // Project 实体表 + body 注册为准）。这里当场校验，失败就明确失败并回滚 ——
                // 不允许出现"适配器字典里有 20 个复制体、世界里 0 个"的静默状态。
                if (!VerifyReplicaAttached(project, entity, out string attachReason))
                {
                    m_attachFailed++;
                    if (entity.IsAddedToProject && entity.Project == project)
                    {
                        try { project.RemoveEntity(entity, disposeEntity: true); } catch (Exception) { }
                    }
                    m_replicas.Remove(record.Key);
                    m_replicaTemplates.Remove(record.Key);
                    reason = "复制体创建后没有真正进入世界（key=" + record.Key + " 模板=\"" + template +
                        "\"）：" + attachReason;
                    LogThrottled("attach-failed:" + record.Key, reason +
                        " → 已回滚，请求主机重发全量（不伪造本地生物）", 5000, error: true);
                    return false;
                }
                m_replicas[record.Key] = entity;
                m_replicaTemplates[record.Key] = template;
                m_createdAttached++;
                return true;
            }
            catch (Exception e)
            {
                m_createExceptions++;
                if (entity != null && entity.IsAddedToProject && entity.Project == project)
                {
                    try { project.RemoveEntity(entity, disposeEntity: true); } catch (Exception) { }
                }
                m_replicas.Remove(record.Key);
                m_replicaTemplates.Remove(record.Key);
                reason = "创建模板 \"" + template + "\" 失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 创建后的**挂载校验**：实体真的加进了这个 Project、真的带 ComponentBody、并且真的
        /// 注册进了 <c>SubsystemBodies</c>（桥的 <c>world entities</c> / 引擎物理 / 渲染都以它为准）。
        /// 任何一条不成立都算失败（调用方回滚 + 记 attachFail），绝不把"字典里有引用"当成成功。
        /// </summary>
        private bool VerifyReplicaAttached(Project project, Entity entity, out string reason)
        {
            if (entity == null) { reason = "实体为空"; return false; }
            if (!entity.IsAddedToProject) { reason = "实体没有加入 Project（AddEntity 未生效）"; return false; }
            if (!ReferenceEquals(entity.Project, project))
            {
                reason = "实体属于另一个 Project（世界已被换新但实体没跟着走）";
                return false;
            }
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) { reason = "实体没有 ComponentBody"; return false; }
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(false);
            if (bodies == null) { reason = "Project 没有 SubsystemBodies（引擎世界不完整）"; return false; }
            foreach (ComponentBody candidate in bodies.Bodies)
            {
                if (ReferenceEquals(candidate, body)) { reason = string.Empty; return true; }
            }
            reason = "实体已加入 Project，但 SubsystemBodies 里没有它的 body（桥和物理都看不到它）";
            return false;
        }

        public bool TryApplyReplicaState(CreatureStateRecord record, out string reason)
        {
            reason = string.Empty;
            if (record == null) { reason = "记录为空"; return false; }
            if (!IsFinite(record)) { reason = "记录含非有限数值"; return false; }
            if (!m_replicas.TryGetValue(record.Key, out Entity entity) ||
                entity == null || !entity.IsAddedToProject || entity.Project != m_project)
            {
                m_replicas.Remove(record.Key);
                m_replicaTemplates.Remove(record.Key);
                reason = "复制体不存在或已随 Project 销毁";
                return false;
            }
            // 变形类生物：同一个网络 key 的模板名可能变（旧实现 SetRemoteAnimalTemplate 的处理）。
            // 模板变了就必须换实体，不能把新种类的外观套在旧实体上。
            if (!string.IsNullOrWhiteSpace(record.TemplateName) &&
                m_replicaTemplates.TryGetValue(record.Key, out string oldTemplate) &&
                !string.Equals(oldTemplate, record.TemplateName, StringComparison.Ordinal))
            {
                if (entity.IsAddedToProject && entity.Project == m_project)
                    m_project.RemoveEntity(entity, disposeEntity: true);
                m_replicas.Remove(record.Key);
                m_replicaTemplates.Remove(record.Key);
                return TryCreateReplica(record, out reason);
            }
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null)
            {
                reason = "复制体没有 ComponentBody";
                return false;
            }
            // 位置/姿态/速度**不在这里写**：收到记录就硬写 = 10 Hz 抖动（实测问题）。
            // 它们由 TryApplyReplicaPresentation 每帧按插值/外推结果驱动。
            ApplyReplicaHealth(entity, record.Health);
            return true;
        }

        public bool TryApplyReplicaPresentation(int key, Vector3 position, Quaternion rotation,
            Vector3 velocity, out string reason)
        {
            reason = string.Empty;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(position.Z) || !float.IsFinite(rotation.X) ||
                !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) ||
                !float.IsFinite(rotation.W) || !float.IsFinite(velocity.X) ||
                !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z))
            {
                m_presentationFailed++;
                reason = "插值结果含非有限数值（不写进引擎）";
                return false;
            }
            if (!m_replicas.TryGetValue(key, out Entity entity) || entity == null ||
                !entity.IsAddedToProject || !ReferenceEquals(entity.Project, m_project))
            {
                m_presentationFailed++;
                m_entityLost++;
                m_replicas.Remove(key);
                m_replicaTemplates.Remove(key);
                reason = "复制体不存在或已随 Project 销毁";
                return false;
            }
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null)
            {
                m_presentationFailed++;
                m_entityLost++;
                m_replicas.Remove(key);
                m_replicaTemplates.Remove(key);
                reason = "复制体没有 ComponentBody";
                return false;
            }
            body.Position = position;
            body.Rotation = rotation;
            body.Velocity = velocity;
            m_presentationApplied++;
            return true;
        }

        public bool TryRemoveReplica(int key, out string reason)
        {
            reason = string.Empty;
            if (!m_replicas.TryGetValue(key, out Entity entity))
            {
                reason = "本端没有 key=" + key + " 的复制体";
                return false;
            }
            m_replicas.Remove(key);
            m_replicaTemplates.Remove(key);
            if (entity != null && entity.IsAddedToProject && entity.Project == m_project)
            {
                m_project.RemoveEntity(entity, disposeEntity: true);
            }
            return true;
        }

        public int TrimReplicasTo(ICollection<int> keepKeys, out string reason)
        {
            reason = string.Empty;
            Project project = m_project;
            if (project == null) { reason = "当前没有世界"; return -1; }
            var keep = keepKeys ?? Array.Empty<int>();
            var keepSet = keep as HashSet<int> ?? new HashSet<int>(keep);
            int removed = 0;
            // 键对账（纯函数，可自检）：把"保留集合 vs 本地复制体键"的三种情况分开，
            // 键不一致/保留集合不可信时**不**按它清世界实体 —— 旧实现在这里会把刚建好的
            // 复制体当成"未分配"全部删掉，表现就是"计数器有 N 个复制体、世界里 0 个"。
            CreatureReplicaKeys.Ledger ledger = CreatureReplicaKeys.Reconcile(keepSet, m_replicas.Keys);
            int missingBefore = ledger.MissingKeys.Count;
            if (ledger.HasMismatch)
            {
                m_keyMismatch += missingBefore;
                LogThrottled("key-mismatch", "客户端的分配集合与本地复制体键不一致：" +
                    CreatureReplicaKeys.Describe(ledger) + "（主机分配的 key 必须为正整数网络 id；" +
                    "这些键只记录，不再被当成'未分配'去清世界实体）", 5000, error: true);
            }
            var replicaEntities = new HashSet<Entity>();
            foreach (KeyValuePair<int, Entity> pair in m_replicas)
            {
                if (keepSet.Contains(pair.Key))
                {
                    if (pair.Value != null) replicaEntities.Add(pair.Value);
                }
            }
            for (int i = 0; i < ledger.StaleKeys.Count; i++)
            {
                int key = ledger.StaleKeys[i];
                if (!m_replicas.TryGetValue(key, out Entity entity)) continue;
                m_replicas.Remove(key);
                m_replicaTemplates.Remove(key);
                if (entity != null && entity.IsAddedToProject && entity.Project == project)
                {
                    project.RemoveEntity(entity, disposeEntity: true);
                    m_removedTrimmed++;
                    removed++;
                }
                else
                {
                    // 表里有引用、世界里已经没有它：明确记 lost，不假装删除成功。
                    m_entityLost++;
                }
            }
            if (ledger.KeepSetUnusable)
            {
                // 分配集合同本地键一个都对不上 ⇒ 不可信（典型：Project 换新后本适配器的表被清空，
                // 而模块还拿着旧键）。此时禁止扫世界实体，否则会把当前世界里所有生物实体清光。
                LogThrottled("keep-set-unusable", "保留集合不可信（" + CreatureReplicaKeys.Describe(ledger) +
                    "）→ 本拍不清理世界里的生物实体（先让模块重新对齐/请求全量）", 5000, error: true);
                RefreshReplicaVisibility(project);
                return removed;
            }
            // 客户端不跑本地生成：不在复制体集合里的非玩家生物实体一律清掉（旧实现 MaintainClientWorldObjects）。
            var entities = new List<Entity>();
            foreach (Entity entity in project.Entities) entities.Add(entity);
            int unassigned = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (entity == null || !entity.IsAddedToProject || entity.Project != project) continue;
                if (replicaEntities.Contains(entity)) continue;
                if (entity.FindComponent<ComponentCreature>() == null) continue;
                if (entity.FindComponent<ComponentPlayer>() != null) continue;
                project.RemoveEntity(entity, disposeEntity: true);
                removed++;
                unassigned++;
                m_unassignedRemoved++;
            }
            if (unassigned > 0)
            {
                m_diagnostics?.Info("creature-spawn", "客户端清理了 " + unassigned +
                    " 个不在主机分配集合里的生物实体（本地生成结果 / 已不再分配给本端）");
            }
            RefreshReplicaVisibility(project);
            return removed;
        }

        /// <summary>
        /// 可见性对账：复制体是否真的落在**本机玩家**周围的主机分配半径内。
        /// 这不是分配逻辑（分配仍由主机按 68m/26 计算），而是把一个真实故障变成显式日志：
        /// 主机侧联机角色位置过期时，主机分配的生物全在 150 米外，客户端"创建成功、
        /// 表现正常、计数漂亮"，屏幕上一只也没有。状态行 + 限流日志必须把这件事说出来。
        /// </summary>
        private void RefreshReplicaVisibility(Project project)
        {
            m_visibilityChecks++;
            m_replicasInRegion = 0;
            m_replicasOutOfRegion = 0;
            m_nearestReplicaDistance = float.PositiveInfinity;
            m_farthestReplicaDistance = float.PositiveInfinity;
            if (m_replicas.Count == 0) return;
            if (!TryGetLocalPlayerPosition(project, out Vector3 origin))
            {
                LogThrottled("visibility-no-player", "可见性对账跳过：本机玩家没有 ComponentBody（位置未知，不猜）", 10000);
                return;
            }
            float radiusSquared = m_clientRegionRadius * m_clientRegionRadius;
            float nearestSquared = float.PositiveInfinity;
            float farthestSquared = 0f;
            foreach (KeyValuePair<int, Entity> pair in m_replicas)
            {
                Entity entity = pair.Value;
                if (entity == null || !entity.IsAddedToProject) { m_entityLost++; continue; }
                ComponentBody body = entity.FindComponent<ComponentBody>();
                if (body == null) continue;
                Vector3 delta = body.Position - origin;
                float distanceSquared = delta.X * delta.X + delta.Z * delta.Z;
                if (distanceSquared < nearestSquared) nearestSquared = distanceSquared;
                if (distanceSquared > farthestSquared) farthestSquared = distanceSquared;
                if (distanceSquared <= radiusSquared) m_replicasInRegion++;
                else m_replicasOutOfRegion++;
            }
            m_nearestReplicaDistance = float.IsPositiveInfinity(nearestSquared)
                ? float.PositiveInfinity
                : MathUtils.Sqrt(nearestSquared);
            m_farthestReplicaDistance = MathUtils.Sqrt(farthestSquared);
            if (m_replicasInRegion == 0 && m_replicasOutOfRegion > 0)
            {
                LogThrottled("replicas-out-of-region", "可见性对账：本端 " + m_replicas.Count +
                    " 个复制体全部在主机分配半径 " + m_clientRegionRadius.ToString("0.#") +
                    " m 之外（最近 " + DescribeDistance(m_nearestReplicaDistance) + " m / 最远 " +
                    DescribeDistance(m_farthestReplicaDistance) + " m，本机玩家在该半径内看到 0 只）" +
                    " → 主机对本端的分配中心与本机玩家位置不一致：检查主机侧该联机角色的位置是否过期" +
                    "（客户端位置快照是否被主机应用），而不是继续怀疑复制体没建出来", 10000, error: true);
            }
        }

        private static bool TryGetLocalPlayerPosition(Project project, out Vector3 position)
        {
            position = Vector3.Zero;
            SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return false;
            for (int i = 0; i < players.ComponentPlayers.Count; i++)
            {
                ComponentPlayer player = players.ComponentPlayers[i];
                ComponentBody body = player?.ComponentBody;
                if (body == null) continue;
                position = body.Position;
                return true;
            }
            return false;
        }

        private void RemoveAllReplicas(string reason)
        {
            Project project = m_project;
            var keys = new List<int>(m_replicas.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                if (!m_replicas.TryGetValue(keys[i], out Entity entity)) continue;
                if (entity != null && entity.IsAddedToProject && entity.Project == project)
                {
                    project.RemoveEntity(entity, disposeEntity: true);
                }
            }
            m_replicas.Clear();
            m_replicaTemplates.Clear();
            m_diagnostics?.Info("creature-spawn", reason);
        }

        private void ApplyReplicaHealth(Entity entity, float health)
        {
            ComponentHealth componentHealth = entity?.FindComponent<ComponentHealth>();
            if (componentHealth == null) return;
            try
            {
                float value = MathUtils.Saturate(health);
                m_parentField.ModifyParentField(componentHealth, "<Health>k__BackingField", value,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(componentHealth, "m_lastHealth", value,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(componentHealth, "<HealthChange>k__BackingField", 0f,
                    typeof(ComponentHealth));
            }
            catch (Exception e)
            {
                m_healthApplyFailures++;
                Warn("复制体生命值写入失败（位置/姿态仍按主机记录同步）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        private static bool IsFinite(CreatureStateRecord record) =>
            float.IsFinite(record.Position.X) && float.IsFinite(record.Position.Y) &&
            float.IsFinite(record.Position.Z) &&
            float.IsFinite(record.Rotation.X) && float.IsFinite(record.Rotation.Y) &&
            float.IsFinite(record.Rotation.Z) && float.IsFinite(record.Rotation.W) &&
            float.IsFinite(record.Velocity.X) && float.IsFinite(record.Velocity.Y) &&
            float.IsFinite(record.Velocity.Z) && float.IsFinite(record.Health);

        public string StatusLine() =>
            "creature-adapter: replicas=" + m_replicas.Count +
            " maintained=" + m_maintenanceRuns +
            " spawnedByMaintain=" + m_spawnedByMaintenance +
            " restored=" + m_restoredRecords +
            " overflowTrim=" + m_overflowTrimmed +
            " created=" + m_createdRaw +
            " attached=" + m_createdAttached +
            " createTry=" + m_createAttempts +
            " createDup=" + m_createDuplicate +
            " createNoWorld=" + m_createSkippedNoWorld +
            " createBadRecord=" + m_createRejectedRecord +
            " createBadTemplate=" + m_createRejectedTemplate +
            " createThrow=" + m_createExceptions +
            " attachFail=" + m_attachFailed +
            " presentation=" + m_presentationApplied +
            " presentFail=" + m_presentationFailed +
            " removedTrimmed=" + m_removedTrimmed +
            " unassignedRemoved=" + m_unassignedRemoved +
            " entityLost=" + m_entityLost +
            " keyMismatch=" + m_keyMismatch +
            " inRegion=" + m_replicasInRegion +
            " outRegion=" + m_replicasOutOfRegion +
            " nearest=" + DescribeDistance(m_nearestReplicaDistance) +
            " farthest=" + DescribeDistance(m_farthestReplicaDistance) +
            " visibilityChecks=" + m_visibilityChecks +
            " viewScopes=" + m_viewScopeRuns +
            " despawnScopes=" + m_despawnScopeRuns +
            " healthFail=" + m_healthApplyFailures;

        private void Warn(string message) => m_diagnostics?.Warn("creature-spawn", message);

        private void Info(string message) => m_diagnostics?.Info("creature-spawn", message);

        /// <summary>限流日志（热路径禁止每帧写日志；计数器才是每帧事实）。</summary>
        private void LogThrottled(string key, string message, int intervalMs, bool error = false)
        {
            long nowMs = (long)(m_now * 1000.0);
            if (m_lastLogTimes.TryGetValue(key, out long last) && nowMs - last < intervalMs) return;
            m_lastLogTimes[key] = nowMs;
            if (error) Warn(message); else Info(message);
        }

        private static string DescribeDistance(float distance) =>
            float.IsFinite(distance) ? distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
    }
}

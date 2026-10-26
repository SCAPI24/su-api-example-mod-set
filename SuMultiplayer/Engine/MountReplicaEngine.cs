using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Creatures;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 乘骑复制的**引擎适配层**（2026-10-26；客户端只是主机端的镜像）。
    ///
    ///   · 客户端：骑乘按钮边沿（<c>ComponentGui.Update:582-589</c> 的
    ///     <c>m_mountButtonWidget.IsClicked / playerInput.ToggleMount</c>）由
    ///     <see cref="SuComponentGui.MountSink"/> 同帧拦截 → 发 MountRequest；
    ///     **本地绝不** StartMounting/StartDismounting。收 MountState 后按
    ///     state_sequence 应用：化身 ParentBody 挂到权威坐骑（引擎自己的
    ///     ComponentRider 动画），观察端同步应用。
    ///   · 主机：收 MountRequest → 先按请求里的身体位姿校正化身后，跑**引擎原生**
    ///     FindNearestMount / StartMounting / StartDismounting（占用 / 存活校验由引擎做，
    ///     失败 = Rejected）→ MountState 定向回请求者 + 广播观察端。
    ///   · key 口径：生物 = CreatureSpawnModule 的网络键；船 = 本适配层的本地 key 表
    ///     （主机生成 / 客户端按 Add 顺序对齐，与生物键互不重叠：船用负数段）。
    ///
    /// 无静态状态；反射入口 / 端口由组合根注入。
    /// </summary>
    public sealed class MountReplicaEngine : IMountGuiSink, IMountReplicaPort
    {
        private readonly IModParentField m_parentField;
        private readonly ICreatureWorld m_creatureWorld;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);

        // ---- 客户端：待决请求 / 已收状态
        private long m_nextRequestSequence;
        private bool m_localRiding;
        private int m_localRideKey;
        private readonly Dictionary<int, MountStateData> m_remoteRiderStates =
            new Dictionary<int, MountStateData>();
        // key < 0 的船（客户端本地建副本时分配；与生物键互不重叠）
        private int m_nextBoatKey = -1;
        private readonly Dictionary<int, Entity> m_entityByKey =
            new Dictionary<int, Entity>();
        private readonly Dictionary<Entity, int> m_keyByEntity =
            new Dictionary<Entity, int>();

        public event Action<MountRequestData> RequestSubmitted;
        public event Action<MountStateData> StateBroadcast;

        public MountReplicaEngine(IModParentField parentField, ICreatureWorld creatureWorld,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_creatureWorld = creatureWorld;
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "mount-sync" : ownerId;
        }

        // ------------------------------------------------------------ 挂接（照 WorldControlEngine 模式）

        private Project m_guiProject;
        private SuComponentGui m_gui;

        /// <summary>组合根每帧调用：客户端把 MountSink 挂到本机玩家的 SuComponentGui 上。</summary>
        public void Pump(bool clientReplica)
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_guiProject))
            {
                DetachGui();
                m_guiProject = project;
            }
            if (project == null) return;
            if (!clientReplica) { DetachGui(); return; }
            if (m_gui != null) return;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || players.ComponentPlayers.Count == 0) return;
            ComponentPlayer player = players.ComponentPlayers[0];
            if (player?.ComponentGui is SuComponentGui shell)
            {
                shell.MountSink = this;
                m_gui = shell;
                LogThrottled("gui-attached", "乘骑拦截已挂接到本机玩家的 SuComponentGui", 60000);
            }
        }

        private void DetachGui()
        {
            if (m_gui != null && ReferenceEquals(m_gui.MountSink, this))
                m_gui.MountSink = null;
            m_gui = null;
        }

        // ------------------------------------------------------------ 客户端：按钮边沿

        public bool TryAcceptMount(ComponentGui gui, ComponentPlayer player)
        {
            if (player == null) return false;
            // 与引擎 ComponentGui.Update:582 同源：按钮点击 + 键盘意图（同一帧同一条请求）。
            bool pressed = false;
            try
            {
                object field = Game.Program.ModManager?.ModParentField?.GetParentField<object>(
                    gui, "m_mountButtonWidget", typeof(ComponentGui));
                pressed = (field as ButtonWidget)?.IsClicked == true;
            }
            catch (Exception)
            {
                pressed = false;
            }
            if (!pressed && player.ComponentInput?.PlayerInput.ToggleMount != true) return false;
            ComponentRider rider = player.ComponentRider;
            if (rider == null) return false;

            bool wantsMount = rider.Mount == null;
            ComponentMount target = wantsMount ? rider.FindNearestMount() : null;
            int targetKey = 0;
            if (target != null)
                m_keyByEntity.TryGetValue(target.ComponentBody?.Entity, out targetKey);
            // 消费本帧边沿：清按钮点击 + ToggleMount，原生本地分支整段不执行。
            ConsumeMountEdge(gui, player);
            m_nextRequestSequence = m_nextRequestSequence <= 0 ? 1 : m_nextRequestSequence + 1;
            var request = new MountRequestData(m_nextRequestSequence, wantsMount, targetKey,
                player.ComponentBody.Position, player.ComponentBody.Rotation,
                player.ComponentLocomotion?.LookAngles ?? Vector2.Zero);
            LogThrottled("request", "乘骑请求已提交：" + request.Describe(), 2000);
            RequestSubmitted?.Invoke(request);
            return true;
        }

        /// <summary>清掉本帧乘骑边沿（按钮 IsClicked 与 ToggleMount），原生分支不执行。</summary>
        private void ConsumeMountEdge(ComponentGui gui, ComponentPlayer player)
        {
            try
            {
                object field = Game.Program.ModManager?.ModParentField?.GetParentField<object>(
                    gui, "m_mountButtonWidget", typeof(ComponentGui));
                if (field is ButtonWidget button)
                {
                    // IsClicked 是 auto-property（ClickableWidget.cs:9）：ModParentField 改的是**字段**，
                    // 必须写 backing field，否则静默无效 → 原生 ComponentGui.Update 仍会挂/卸一次
                    //（实机：Android 本地未经主机授权挂载 + mount/dismount 交替请求）。
                    Game.Program.ModManager?.ModParentField?.ModifyParentField(
                        button, "<IsClicked>k__BackingField", false, typeof(ClickableWidget));
                    Game.Program.ModManager?.ModParentField?.ModifyParentField(
                        button, "<IsTapped>k__BackingField", false, typeof(ClickableWidget));
                }
            }
            catch (Exception)
            {
            }
            ComponentInput input = player.Entity?.FindComponent<ComponentInput>();
            if (input == null) return;
            PlayerInput pi = input.PlayerInput;
            pi.ToggleMount = false;
            Game.Program.ModManager?.ModParentField?.ModifyParentField(
                input, "m_playerInput", pi, typeof(ComponentInput));
        }

        // ------------------------------------------------------------ 客户端：状态应用

        public bool TryPresentState(in MountStateData state)
        {
            Project project = GameManager.Project;
            if (project == null) { LogThrottled("present-no-project", "没有世界", 5000); return false; }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            // 先按 RiderClientId 解析**远端化身**：观察端（客户端 B / 主机）必须把 A 的
            // MountState 应用到 A 的化身，而不是"本端随便一个本机玩家"。旧逻辑先找本端任意
            // SuComponentInput 玩家 → 客户端 B 把 A 的挂载状态套到自己身上、A 的化身留在马旁
            //（实机：B 看到 A 站在马侧面/上方；主机看 A 正常）。找不到化身（骑手自己 / 主机的
            // 本机玩家）才回退成本机玩家。2026-10-26 骑马状态未同步给其他客户端修复。
            ComponentPlayer rider = ResolveAvatarRider(state.RiderClientId);
            bool isLocal = false;
            if (rider == null && players != null)
            {
                foreach (ComponentPlayer p in players.ComponentPlayers)
                {
                    if (p.ComponentInput is SuComponentInput)
                    {
                        rider = p;
                        isLocal = true;
                        break;
                    }
                }
            }
            if (rider == null)
            {
                LogThrottled("present-no-rider:" + state.RiderClientId,
                    "收到 MountState 但找不到骑手（clientId=" + state.RiderClientId + "）", 5000);
                return false;
            }
            ComponentRider componentRider = rider.ComponentRider;
            if (componentRider == null) return false;
            if (isLocal)
            {
                // 本机玩家：只认更新的 state_sequence（请求 / 回执可能乱序到达）。
                if (state.Sequence == 0 && state.StateSequence <= m_lastLocalStateSequence) return true;
                m_lastLocalStateSequence = Math.Max(m_lastLocalStateSequence, state.StateSequence);
            }
            if (state.State == MountStateKind.Rejected)
            {
                LogThrottled("rejected:" + state.RiderClientId,
                    "乘骑请求被主机拒绝：" + state.Describe(), 2000);
                return true;
            }
            bool dismounting = state.State == MountStateKind.Dismounting ||
                state.State == MountStateKind.Dismounted;
            if (dismounting)
            {
                if (componentRider.Mount != null) componentRider.StartDismounting();
                if (isLocal) { m_localRiding = false; m_localRideKey = 0; }
                else m_remoteRiderStates[state.RiderClientId] = state;
                return true;
            }
            Entity mountEntity = ResolveMountEntity(state.MountKey);
            ComponentMount mount = mountEntity?.FindComponent<ComponentMount>();
            if (mount == null)
            {
                LogThrottled("no-mount:" + state.MountKey,
                    "MountState 的坐骑在本端不存在（key=" + state.MountKey + "）→ 等复制体/实体到达",
                    5000);
                return false;
            }
            if (componentRider.Mount == null)
            {
                componentRider.StartMounting(mount);
            }
            if (isLocal) { m_localRiding = true; m_localRideKey = state.MountKey; }
            else m_remoteRiderStates[state.RiderClientId] = state;
            return true;
        }

        private long m_lastLocalStateSequence;

        private ComponentPlayer ResolveAvatarRider(int clientId)
        {
            // 化身骑手解析由组合根注入（需要 NetworkAvatarSpawner；这里用端口回调）。
            return AvatarRiderResolver?.Invoke(clientId);
        }

        /// <summary>组合根注入：clientId → 联机化身（null = 本端没有这个化身）。</summary>
        public Func<int, ComponentPlayer> AvatarRiderResolver { get; set; }

        /// <summary>
        /// 组合根注入：主机侧实体 → 本会话复制网络键（CreatureSpawnModule.TryGetHostCreatureKey）。
        /// MountState 的 mount_key 必须用主机分配的键：观察端（客户端）拿它去解析生物/船副本。
        /// 旧实现只查客户端复制表，主机侧坐骑/船一律返回 0 → 客户端解析不到坐骑
        ///（实机：A 骑马，主机看正常，客户端 B 看到 A 站在马旁/马上方）。
        /// </summary>
        public Func<Entity, int> HostKeyResolver { get; set; }

        // ------------------------------------------------------------ 主机：执行请求

        public bool TryExecuteRequest(int clientId, in MountRequestData request,
            ComponentPlayer player, out int stateKind, out int mountKey, out string reason)
        {
            stateKind = MountStateKind.Rejected;
            mountKey = 0;
            ComponentRider rider = player?.ComponentRider;
            if (rider == null) { reason = "no-rider"; return false; }
            if (player.ComponentHealth != null && player.ComponentHealth.Health <= 0f)
            {
                reason = "rider-dead";
                return false;
            }
            // 位置敏感：先把请求携带的身体位姿应用（旧实现 ApplyHostActionPose 同款），
            // 再跑引擎原生的选择 / 校验。
            try
            {
                player.ComponentBody.Position = request.BodyPosition;
                player.ComponentBody.Rotation = request.BodyRotation;
                if (player.ComponentLocomotion != null)
                    m_parentField.ModifyParentField(player.ComponentLocomotion, "m_lookAngles",
                        request.LookAngles, typeof(ComponentLocomotion));
            }
            catch (Exception)
            {
            }
            bool currentlyRiding = rider.Mount != null;
            if (!request.Mount)
            {
                if (!currentlyRiding) { reason = "not-riding"; return false; }
                rider.StartDismounting();
                mountKey = KeyOfEntity(rider.Mount?.ComponentBody?.Entity);
                stateKind = MountStateKind.Dismounting;
                reason = null;
                return true;
            }
            if (currentlyRiding) { reason = "already-riding"; mountKey = KeyOfEntity(rider.Mount.ComponentBody.Entity); return false; }
            ComponentMount mount = ResolveHostMount(request.MountKey);
            if (mount == null || !IsMountTargetValid(rider, mount))
                mount = rider.FindNearestMount();
            if (mount == null) { reason = "no-mount-nearby"; return false; }
            if (mount.Rider != null && mount.Rider != rider)
            {
                reason = "mount-occupied";
                return false;
            }
            rider.StartMounting(mount);
            mountKey = KeyOfEntity(mount.ComponentBody.Entity);
            stateKind = MountStateKind.Mounting;
            reason = null;
            return true;
        }

        /// <summary>IMountReplicaPort：主机执行（包装 TryExecuteRequest，组合根注入玩家解析）。</summary>
        public bool TryExecuteHostRequest(int clientId, in MountRequestData request,
            out int stateKind, out int mountKey, out string reason)
        {
            ComponentPlayer player = AvatarRiderResolver?.Invoke(clientId);
            if (player == null)
            {
                stateKind = MountStateKind.Rejected;
                mountKey = 0;
                reason = "no-avatar";
                return false;
            }
            return TryExecuteRequest(clientId, request, player, out stateKind, out mountKey,
                out reason);
        }

        /// <summary>IMountReplicaPort：骑手当前骑乘的坐骑姿态（没有 = 骑手自身位置）。</summary>
        public void ResolveRiderPose(int clientId, out Vector3 mountPosition,
            out Quaternion mountRotation)
        {
            ComponentPlayer player = AvatarRiderResolver?.Invoke(clientId);
            ComponentBody mountBody = player?.ComponentRider?.Mount?.ComponentBody;
            if (mountBody != null)
            {
                mountPosition = mountBody.Position;
                mountRotation = mountBody.Rotation;
                return;
            }
            mountPosition = player?.ComponentBody.Position ?? Vector3.Zero;
            mountRotation = player?.ComponentBody.Rotation ?? Quaternion.Identity;
        }

        private ComponentMount ResolveHostMount(int key)
        {
            Entity entity = ResolveMountEntity(key);
            return entity?.FindComponent<ComponentMount>();
        }

        private static bool IsMountTargetValid(ComponentRider rider, ComponentMount mount)
        {
            if (mount.Rider != null && mount.Rider != rider) return false;
            ComponentBody riderBody = rider.ComponentCreature.ComponentBody;
            ComponentBody mountBody = mount.ComponentBody;
            return Vector3.DistanceSquared(riderBody.Position, mountBody.Position) <= 9f;
        }

        // ------------------------------------------------------------ key 表

        /// <summary>主机：注册船/杂项坐骑的 key（组合根在实体复制 Add 时调用）。</summary>
        public void RegisterHostMountEntity(Entity entity, int key)
        {
            if (entity == null || key == 0) return;
            m_entityByKey[key] = entity;
            m_keyByEntity[entity] = key;
        }

        public void ForgetEntity(Entity entity)
        {
            if (entity != null && m_keyByEntity.TryGetValue(entity, out int key))
            {
                m_keyByEntity.Remove(entity);
                m_entityByKey.Remove(key);
            }
        }

        /// <summary>客户端：生物复制体 Add 时登记（key → 本地复制体）。</summary>
        public void RegisterReplicaMount(int key, Entity replica)
        {
            if (key == 0 || replica == null) return;
            m_entityByKey[key] = replica;
            m_keyByEntity[replica] = key;
        }

        public void RemoveReplicaMount(int key)
        {
            if (m_entityByKey.TryGetValue(key, out Entity entity))
            {
                m_entityByKey.Remove(key);
                if (entity != null) m_keyByEntity.Remove(entity);
            }
        }

        /// <summary>客户端：给本地预测/未分配实体分配一个负数船 key（与生物键不重叠）。</summary>
        public int AcquireBoatKey(Entity boat)
        {
            if (boat == null) return 0;
            if (m_keyByEntity.TryGetValue(boat, out int existing)) return existing;
            int key = m_nextBoatKey--;
            m_entityByKey[key] = boat;
            m_keyByEntity[boat] = key;
            return key;
        }

        private int KeyOfEntity(Entity entity)
        {
            if (entity == null) return 0;
            if (m_keyByEntity.TryGetValue(entity, out int key)) return key;
            // 主机侧生物/船键由 CreatureSpawnModule 分配：优先用组合根注入的主机键反查
            //（2026-10-26 骑马状态未同步给其他客户端修复：旧实现只查客户端复制表 → 主机返回 0）。
            if (HostKeyResolver != null)
            {
                int hostKey = HostKeyResolver(entity);
                if (hostKey > 0)
                {
                    m_entityByKey[hostKey] = entity;
                    m_keyByEntity[entity] = hostKey;
                    return hostKey;
                }
            }
            // 客户端侧：生物复制体键。
            if (m_creatureWorld != null &&
                m_creatureWorld.TryGetReplicaKey(entity, out int replicaKey, out _))
            {
                m_entityByKey[replicaKey] = entity;
                m_keyByEntity[entity] = replicaKey;
                return replicaKey;
            }
            return 0;
        }

        private Entity ResolveMountEntity(int key)
        {
            if (key == 0) return null;
            if (m_entityByKey.TryGetValue(key, out Entity entity)) return entity;
            // 生物复制体晚到：从生物世界按 key 补一次。
            Entity replica = m_creatureWorld?.GetReplicaEntity(key);
            if (replica != null)
            {
                m_entityByKey[key] = replica;
                m_keyByEntity[replica] = key;
            }
            return replica;
        }

        // ------------------------------------------------------------ 主机：广播

        public void BroadcastState(in MountStateData state)
        {
            StateBroadcast?.Invoke(state);
        }

        public string Describe() => "localRiding=" + m_localRiding +
            " remoteRiders=" + m_remoteRiderStates.Count +
            " keys=" + m_entityByKey.Count;

        private void LogThrottled(string key, string message, int intervalMs)
        {
            // 简化限流：同 key 一次性（诊断证据足够；错误态会随状态更新反复出现）。
            if (!m_loggedOnce.Add(key + message)) return;
            m_diagnostics?.Info(m_ownerId, message);
        }
    }
}

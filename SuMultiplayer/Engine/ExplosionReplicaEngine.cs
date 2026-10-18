using System;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 爆炸复制的**引擎适配层**（<see cref="IExplosionReplicaPort"/> 的实现，组合根注入）。
    ///
    ///   · 主机：把注入后的 <c>SuSubsystemExplosions</c> 挂上
    ///     <see cref="ExplosionReplicaHost"/>（逐实例检测，含链式爆炸），把检测结果变成
    ///     <see cref="ExplosionEventData"/>；玩家伤害只上报"引擎已经扣掉的血量差"。
    ///   · 客户端：只调用 <see cref="ExplosionReplicaPresentation"/>（粒子 / 声音 / 震动）——
    ///     这里没有 ChangeCell / DestroyCell / AddPickable / AddProjectile / Injure 的调用路径。
    ///   · 客户端用爆炸物：投掷 / 点燃引信由方块行为转成请求（<see cref="SubmitClientRequest"/>），
    ///     主机收到后用 <see cref="TryExecuteRequest"/> 校验并在引擎里执行。
    ///
    /// 无静态状态：反射入口 / 化身表 / 诊断都由组合根以实例注入。
    /// </summary>
    public sealed class ExplosionReplicaEngine : IExplosionReplicaPort
    {
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly global::Engine.Random m_random = new global::Engine.Random();
        private readonly System.Collections.Generic.HashSet<string> m_loggedOnce =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private ExplosionReplicaHost m_host;
        private ExplosionReplicaPresentation m_presentation;
        private Project m_project;
        private SuSubsystemExplosions m_subsystem;
        private bool m_clientReplica;
        private long m_nextEventId;
        private long m_nextDamageId;
        /// <summary>上一次发出的 server_tick（世界重载后引擎时钟会回 0，这里保证不下退）。</summary>
        private long m_lastEmittedTick;
        private long m_eventsEmitted;
        private long m_eventsWithoutListener;
        private long m_damageEmitted;
        private long m_fireworksPublished;
        private long m_requestSubmissions;
        private long m_requestsAccepted;
        private long m_itemsConsumed;
        private long m_damagePresented;

        public ExplosionReplicaEngine(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModParentMethod parentMethod, IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner;
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "explosion-sync" : ownerId;
        }

        internal NetworkAvatarSpawner Spawner => m_spawner;

        public event Action<ExplosionEventData> HostExplosionExecuted;
        public event Action<PlayerDamageData> HostPlayerDamaged;
        public event Action<ExplosionRequestData> ClientRequestSubmitted;

        public bool IsClientReplica => m_clientReplica;

        // ------------------------------------------------------------ 挂接

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                DetachSubsystem();
                m_project = project;
            }
            if (project == null) return;

            SuSubsystemExplosions subsystem = project.FindSubsystem<SuSubsystemExplosions>(false);
            m_clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                              shell.ClientReplica;
            if (subsystem == null)
            {
                LogOnce("subsystem-missing",
                    "没有找到注入后的 SuSubsystemExplosions（引擎注入可能未生效）→ 爆炸同步无法挂接" +
                    "（主机照常本地爆炸，客户端不会有爆炸表现）");
                return;
            }
            if (!ReferenceEquals(subsystem, m_subsystem))
            {
                if (m_subsystem != null) m_subsystem.Replica = null;
                m_subsystem = subsystem;
                m_subsystem.Replica = this;
                m_host?.Reset();
                Log("已挂接注入后的爆炸子系统（" + (m_clientReplica ? "客户端=表现" : "主机=权威") +
                    "）：" + subsystem.GetType().Name);
            }
            if (!m_clientReplica && m_host == null)
                m_host = new ExplosionReplicaHost(m_parentField, m_parentMethod, this);
        }

        /// <summary>Mod 卸载：摘掉子系统挂接点，避免重载后旧适配器还挂着。</summary>
        public void Shutdown()
        {
            DetachSubsystem();
            m_project = null;
            m_clientReplica = false;
        }

        private void DetachSubsystem()
        {
            if (m_subsystem != null)
            {
                try { m_subsystem.Replica = null; } catch (Exception) { }
                m_subsystem = null;
            }
            m_host = null;
        }

        /// <summary>由 <c>SuSubsystemExplosions.Update</c> 调用；true = 本帧队列已消费完。</summary>
        internal bool UpdateHostExplosions(SuSubsystemExplosions subsystem, float dt)
        {
            if (m_clientReplica) return false;
            if (m_host == null) return false;
            return m_host.TryUpdate(subsystem, dt);
        }

        // ------------------------------------------------------------ 主机：上报检测结果

        internal void EmitHostExplosion(in HostExplosionRecord record)
        {
            uint eventId = (uint)(++m_nextEventId);
            if (eventId == 0) eventId = (uint)(++m_nextEventId);
            long tick = CurrentTick();
            var data = new ExplosionEventData(eventId, tick, record.Position, record.Kind,
                record.Radius, record.BlockValue, record.CosmeticOnly, record.Pressure,
                record.Incendiary);
            m_eventsEmitted++;
            Action<ExplosionEventData> handler = HostExplosionExecuted;
            if (handler == null)
            {
                m_eventsWithoutListener++;
                return;
            }
            try
            {
                handler(data);
            }
            catch (Exception e)
            {
                LogOnce("emit-explosion:" + e.GetType().Name,
                    "爆炸事件广播处理器异常（" + data.Describe() + "）：" +
                    e.GetType().Name + " " + e.Message + " → 本帧其余爆炸与本地模拟不受影响");
            }
        }

        internal void EmitHostDamage(in HostDamageRecord record)
        {
            uint eventId = (uint)(++m_nextDamageId);
            if (eventId == 0) eventId = (uint)(++m_nextDamageId);
            long tick = CurrentTick();
            var data = new PlayerDamageData(eventId, tick, record.ClientId, record.Identity,
                record.Name, record.Position, record.Damage, record.HealthAfter);
            m_damageEmitted++;
            try
            {
                HostPlayerDamaged?.Invoke(data);
            }
            catch (Exception e)
            {
                LogOnce("emit-damage:" + e.GetType().Name,
                    "玩家伤害广播处理器异常（clientId=" + data.ClientId + "）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 烟花的表现是"爆开"的瞬间（引擎 FireworksBlockBehavior 自己的延时回调），不是投掷瞬间。
        /// 这里由 <c>SuSubsystemFireworksBlockBehavior</c> 在爆开后调用，只发一条 cosmetic 事件。
        /// </summary>
        public void PublishHostFirework(Vector3 position, int projectileValue)
        {
            if (m_clientReplica) return;
            m_fireworksPublished++;
            EmitHostExplosion(new HostExplosionRecord(position, ExplosionKinds.Firework, 0f,
                (uint)projectileValue, cosmeticOnly: true, pressure: 0f, incendiary: false));
        }

        // ------------------------------------------------------------ 客户端：请求提交

        /// <summary>
        /// 客户端方块行为把"要投掷 / 点燃"交给这里。true = 请求已交给网络模块；
        /// false = 没有挂接 / 消息层缺失（调用方必须**不**本地放爆炸，并写原因）。
        /// </summary>
        public bool SubmitClientRequest(in ExplosionRequestData request, out string reason)
        {
            m_requestSubmissions++;
            Action<ExplosionRequestData> handler = ClientRequestSubmitted;
            if (handler == null)
            {
                reason = "no-request-listener";
                LogOnce("no-request-listener",
                    "客户端提交爆炸请求 " + request.Describe() + "，但爆炸同步模块没有挂接 → " +
                    "请求不会发出（不本地放爆炸）");
                return false;
            }
            try
            {
                handler(request);
            }
            catch (Exception e)
            {
                reason = "submit-exception:" + e.GetType().Name;
                LogOnce("submit:" + e.GetType().Name,
                    "提交爆炸请求 " + request.Describe() + " 异常：" + e.GetType().Name + " " +
                    e.Message + " → 请求没有发出（不本地放爆炸）");
                return false;
            }
            reason = null;
            return true;
        }

        // ------------------------------------------------------------ 主机：执行客户端请求

        public bool TryExecuteRequest(int clientId, in ExplosionRequestData request,
            out Vector3 resultPosition, out string reason)
        {
            resultPosition = Vector3.Zero;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
            {
                reason = "no-terrain";
                return false;
            }
            if (!TryResolveActor(project, clientId, out PlayerData playerData,
                    out ComponentPlayer player, out Vector3 actorPosition, out string actorReason))
            {
                reason = actorReason;
                return false;
            }
            if (request.Kind == ExplosionRequestKinds.Ignite)
            {
                int x = global::Game.Terrain.ToCell(request.Position.X);
                int y = global::Game.Terrain.ToCell(request.Position.Y);
                int z = global::Game.Terrain.ToCell(request.Position.Z);
                var target = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                if (Vector3.Distance(actorPosition, target) > 12f)
                {
                    reason = "distance";
                    return false;
                }
                int cellValue = terrain.Terrain.GetCellValue(x, y, z);
                int contents = global::Game.Terrain.ExtractContents(cellValue);
                Block block = contents > 0 && BlocksManager.Blocks != null &&
                              contents < BlocksManager.Blocks.Length
                    ? BlocksManager.Blocks[contents]
                    : null;
                if (!(block is GunpowderKegBlock) && !(block is DetonatorBlock))
                {
                    reason = "block-illegal";
                    return false;
                }
                int itemContents = global::Game.Terrain.ExtractContents(request.ItemValue);
                Block igniteItem = itemContents > 0 && BlocksManager.Blocks != null &&
                                   itemContents < BlocksManager.Blocks.Length
                    ? BlocksManager.Blocks[itemContents]
                    : null;
                if (!(igniteItem is MatchBlock))
                {
                    reason = "item-illegal";
                    return false;
                }
                SubsystemExplosivesBlockBehavior behavior =
                    project.FindSubsystem<SubsystemExplosivesBlockBehavior>(false);
                if (behavior == null)
                {
                    reason = "no-explosives-behavior";
                    return false;
                }
                if (!behavior.IgniteFuse(x, y, z))
                {
                    reason = "ignite-refused";
                    return false;
                }
                resultPosition = target;
                m_requestsAccepted++;
                reason = null;
                return true;
            }

            if (request.Kind == ExplosionRequestKinds.Throw)
            {
                int itemContents = global::Game.Terrain.ExtractContents(request.ItemValue);
                Block itemBlock = itemContents > 0 && BlocksManager.Blocks != null &&
                                  itemContents < BlocksManager.Blocks.Length
                    ? BlocksManager.Blocks[itemContents]
                    : null;
                if (!(itemBlock is BombBlock) && !(itemBlock is IncendiaryBombBlock) && !(itemBlock is FireworksBlock))
                {
                    reason = "item-illegal";
                    return false;
                }
                if (!itemBlock.IsAimable || itemBlock.ProjectileSpeed <= 0f)
                {
                    reason = "item-not-throwable";
                    return false;
                }
                if (Vector3.Distance(actorPosition, request.Position) > 6f)
                {
                    reason = "distance";
                    return false;
                }
                Vector3 direction = request.Direction;
                if (direction.Length() < 0.5f)
                {
                    reason = "direction-zero";
                    return false;
                }
                direction = Vector3.Normalize(direction);
                SubsystemProjectiles projectiles =
                    project.FindSubsystem<SubsystemProjectiles>(false);
                if (projectiles == null)
                {
                    reason = "no-projectiles";
                    return false;
                }
                ComponentBody body = player?.ComponentBody;
                if (body == null)
                {
                    reason = "no-body";
                    return false;
                }
                float speed = itemBlock.ProjectileSpeed;
                if (player.ComponentLevel != null)
                    speed *= 0.5f * (player.ComponentLevel.StrengthFactor - 1f) + 1f;
                Vector3 velocity = body.Velocity + direction * speed;
                Projectile projectile = projectiles.FireProjectile(request.ItemValue,
                    request.Position, velocity, m_random.Vector3(5f, 10f), player);
                if (projectile == null)
                {
                    reason = "projectile-blocked";
                    return false;
                }
                resultPosition = request.Position;
                m_requestsAccepted++;
                reason = null;
                return true;
            }

            reason = "kind-unknown";
            return false;
        }

        private bool TryResolveActor(Project project, int clientId, out PlayerData playerData,
            out ComponentPlayer player, out Vector3 position, out string reason)
        {
            playerData = null;
            player = null;
            position = Vector3.Zero;
            if (m_spawner == null)
            {
                reason = "no-spawner";
                return false;
            }
            if (clientId == 0)
            {
                if (!m_spawner.TryGetLocalPlayer(project, out playerData, out reason)) return false;
            }
            else if (!m_spawner.TryGet(clientId, out playerData) || playerData == null)
            {
                reason = "no-avatar";
                return false;
            }
            player = playerData.ComponentPlayer;
            if (player == null || player.ComponentBody == null)
            {
                reason = "no-player-body";
                return false;
            }
            position = player.ComponentBody.Position;
            reason = null;
            return true;
        }

        // ------------------------------------------------------------ 客户端：扣道具

        public bool TryConsumeRequestItem(in ExplosionRequestData request, out string reason)
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            if (m_spawner == null)
            {
                reason = "no-spawner";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData local, out reason))
                return false;
            ComponentMiner miner = local.ComponentPlayer?.ComponentMiner;
            IInventory inventory = miner?.Inventory;
            if (inventory == null)
            {
                reason = "no-inventory";
                return false;
            }
            int slot = request.ActiveSlot;
            if (slot >= 0 && slot < inventory.SlotsCount &&
                inventory.GetSlotValue(slot) == request.ItemValue &&
                inventory.GetSlotCount(slot) > 0)
            {
                if (!TryConsumeSlot(miner, inventory, slot, request.Kind))
                {
                    reason = "active-slot-changed";
                    return false;
                }
                m_itemsConsumed++;
                reason = null;
                return true;
            }
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                if (inventory.GetSlotValue(i) != request.ItemValue ||
                    inventory.GetSlotCount(i) <= 0) continue;
                if (!TryConsumeSlot(miner, inventory, i, request.Kind))
                {
                    reason = "active-slot-changed";
                    return false;
                }
                m_itemsConsumed++;
                reason = null;
                return true;
            }
            reason = "item-not-in-inventory";
            return false;
        }

        /// <summary>
        /// 扣道具的口径必须与引擎一致：
        ///   · 投掷 = 移除 1 个物品（SubsystemThrowableBlockBehavior 的 RemoveSlotItems）；
        ///   · 点火 = 消耗 1 点工具耐久（SubsystemMatchBlockBehavior 的 RemoveActiveTool(1)），
        ///     不是整件拿走。只有请求里的槽位仍是同一件物品时才扣，避免玩家中途换物品扣错。
        /// </summary>
        private static bool TryConsumeSlot(ComponentMiner miner, IInventory inventory, int slot,
            int kind)
        {
            if (kind == ExplosionRequestKinds.Ignite)
            {
                if (miner == null || miner.Inventory == null ||
                    miner.Inventory.ActiveSlotIndex != slot) return false;
                miner.RemoveActiveTool(1);
                return true;
            }
            inventory.RemoveSlotItems(slot, 1);
            return true;
        }

        // ------------------------------------------------------------ 客户端：表现

        public bool Present(in ExplosionEventData explosion, out string reason)
        {
            try
            {
                m_presentation ??= new ExplosionReplicaPresentation();
                return m_presentation.TryPresent(explosion, out reason);
            }
            catch (Exception e)
            {
                reason = "present-exception:" + e.GetType().Name;
                LogOnce("present:" + e.GetType().Name,
                    "客户端爆炸表现异常（" + explosion.Describe() + "）：" + e.GetType().Name +
                    " " + e.Message + " → 本次不表现（明确计数，不静默）");
                return false;
            }
        }

        public bool PresentPlayerDamage(in PlayerDamageData damage, out string reason)
        {
            if (GameManager.Project == null)
            {
                reason = "no-project";
                return false;
            }
            // 血量同步是后续项：客户端**不**写 ComponentHealth。这里只作为"主机的伤害结算
            // 已到达"的表现入口（当前无视觉元素，计数/日志由模块负责）。
            m_damagePresented++;
            reason = null;
            return true;
        }

        public string Describe()
        {
            return "clientReplica=" + m_clientReplica + " events=" + m_eventsEmitted +
                " noListener=" + m_eventsWithoutListener + " damage=" + m_damageEmitted +
                " fireworks=" + m_fireworksPublished + " requestSubmits=" +
                m_requestSubmissions + " accepted=" + m_requestsAccepted + " itemsConsumed=" + m_itemsConsumed +
                " damagePresented=" + m_damagePresented +
                (m_host == null ? " host=detached" : " " + m_host.Describe());
        }

        // ------------------------------------------------------------ 日志 / 时间

        internal void LogDetectionProblemOnce(string key, string message) =>
            LogOnce("detect:" + key, message);

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void Log(string message) => m_diagnostics?.Info(m_ownerId, message);

        private long CurrentTick()
        {
            SubsystemGameInfo info = m_project?.FindSubsystem<SubsystemGameInfo>(false);
            long tick = info == null ? 0L : (long)(info.TotalElapsedGameTime * 1000.0);
            // 世界重载 / 换 Project 会让引擎时钟回到 0；事件 id 是单调的，server_tick
            // 也必须是（否则客户端会把这些事件当成 stale 丢掉）。这里取历史最大值。
            if (tick < m_lastEmittedTick) tick = m_lastEmittedTick;
            m_lastEmittedTick = tick;
            return tick;
        }
    }
}

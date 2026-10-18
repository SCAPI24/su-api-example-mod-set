using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端投掷 / 弓 / 弩 / 火枪的**开火表现**（只发声 / 粒子 / 后坐，不改库存、不生成实体）。
    /// 数值与引擎对应分支逐条对齐；主机完全不进入这里（主机的方块行为走 base 原生路径）。
    /// </summary>
    internal static class ProjectileFirePresentation
    {
        public static void PlayThrow(Project project, ComponentMiner miner, Vector3 position)
        {
            global::Engine.Random random = new global::Engine.Random();
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            audio?.PlaySound("Audio/Throw", random.Float(0.2f, 0.3f),
                random.Float(-0.2f, 0.2f), position, 2f, autoDelay: true);
            miner?.Poke(forceRestart: false);
        }

        public static void PlayBow(Project project, Vector3 position, bool crossbow)
        {
            global::Engine.Random random = new global::Engine.Random();
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            audio?.PlaySound("Audio/Bow", 1f, random.Float(-0.1f, 0.1f), position, 3f,
                autoDelay: true);
            if (crossbow)
                audio?.PlaySound("Audio/CrossbowBoing", 1f, random.Float(-0.1f, 0.1f),
                    position, 3f, 0f);
        }

        public static void PlayMusket(Project project, ComponentMiner miner, Vector3 origin,
            Vector3 direction)
        {
            global::Engine.Random random = new global::Engine.Random();
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            SubsystemNoise noise = project?.FindSubsystem<SubsystemNoise>(false);
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            audio?.PlaySound("Audio/MusketFire", 1f, random.Float(-0.1f, 0.1f), origin, 10f,
                autoDelay: true);
            if (particles != null && terrain != null)
                particles.AddParticleSystem(new GunSmokeParticleSystem(terrain,
                    origin + 0.3f * direction, direction));
            noise?.MakeNoise(origin, 1f, 40f);
            miner?.ComponentCreature?.ComponentBody?.ApplyImpulse(-4f * direction);
        }

        public static void PlayMisfire(Project project, Vector3 origin)
        {
            global::Engine.Random random = new global::Engine.Random();
            project?.FindSubsystem<SubsystemAudio>(false)?.PlaySound("Audio/MusketMisfire", 1f,
                random.Float(-0.1f, 0.1f), origin, 3f, autoDelay: true);
        }

        /// <summary>
        /// 火枪开火的**表现版**：只播声音 + 枪口烟，不施加后坐力、不产生噪音
        /// （这两项是主机权威玩法副作用，客户端/中继表现绝不能做）。
        /// </summary>
        public static void PlayMusketPresentation(Project project, Vector3 origin,
            Vector3 direction, bool misfire)
        {
            global::Engine.Random random = new global::Engine.Random();
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            if (misfire)
            {
                audio?.PlaySound("Audio/MusketMisfire", 1f, random.Float(-0.1f, 0.1f),
                    origin, 3f, autoDelay: true);
                return;
            }
            audio?.PlaySound("Audio/MusketFire", 1f, random.Float(-0.1f, 0.1f), origin, 10f,
                autoDelay: true);
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            if (particles != null && terrain != null)
                particles.AddParticleSystem(new GunSmokeParticleSystem(terrain,
                    origin + 0.3f * Vector3.Normalize(direction), direction));
        }
    }

    /// <summary>
    /// 客户端弓开火拦截。
    ///
    /// 原引擎路径：<c>SubsystemBowBlockBehavior.OnAim(Completed)</c> 在**本机**
    /// <c>FireProjectile</c> + 扣弓的箭 + 扣耐久。联机时客户端不模拟投射物（本地预测会被
    /// 实体复制的安全网清掉），也不允许本地扣物品。这里改成：
    ///   · 客户端：发 ProjectileFireRequest（kind=Bow, charge=draw）→ 主机校验 / 生成权威箭 /
    ///     重置弓并扣耐久 → 稀疏背包增量回执；本机只播声音；
    ///   · 主机 / 单机：照旧 base.OnAim（原路径不动）。
    ///
    /// Source: Survivalcraft/Game/SubsystemBowBlockBehavior.cs:OnAim
    /// </summary>
    public sealed class SuSubsystemBowBlockBehavior : SubsystemBowBlockBehavior
    {
        private bool m_warnedNoReplica;

        public override bool OnAim(Ray3 aim, ComponentMiner componentMiner, AimState state)
        {
            if (!IsClientReplica || state != AimState.Completed)
                return base.OnAim(aim, componentMiner, state);
            IInventory inventory = componentMiner?.Inventory;
            if (inventory == null) return false;
            int slot = inventory.ActiveSlotIndex;
            if (slot < 0 || slot >= inventory.SlotsCount) return false;
            int value = inventory.GetSlotValue(slot);
            int count = inventory.GetSlotCount(slot);
            if (count <= 0 || value == 0) return false;
            int contents = global::Game.Terrain.ExtractContents(value);
            if (contents != BowBlock.Index) return base.OnAim(aim, componentMiner, state);
            Block block = BlocksManager.Blocks[contents];
            if (!(block is BowBlock)) return base.OnAim(aim, componentMiner, state);

            int data = global::Game.Terrain.ExtractData(value);
            if (!BowBlock.GetArrowType(data).HasValue)
                componentMiner.ComponentPlayer?.ComponentGui.DisplaySmallMessage(
                    "Load an arrow first", Color.White, blinking: true,
                    playNotificationSound: false);
            Vector3 origin = componentMiner.ComponentCreature.ComponentCreatureModel.EyePosition +
                componentMiner.ComponentCreature.ComponentBody.Matrix.Right * 0.3f -
                componentMiner.ComponentCreature.ComponentBody.Matrix.Up * 0.2f;
            int charge = MathUtils.Clamp(BowBlock.GetDraw(data), 0, 15);
            var intent = new ProjectileFireIntent(ProjectileFireKinds.Bow, value, slot, origin,
                Vector3.Normalize(aim.Direction), charge);
            PickableReplicaEngine replica = ProjectileFireClient.GetReplica(Project,
                out string replicaReason);
            if (replica == null)
            {
                WarnNoReplicaOnce(replicaReason);
                return false;
            }
            if (!replica.SubmitClientProjectileFire(in intent, out string reason))
                return false;
            ProjectileFirePresentation.PlayBow(Project, origin, crossbow: false);
            return true;
        }

        private bool IsClientReplica =>
            SubsystemTerrain is SuSubsystemTerrain shell && shell.ClientReplica;

        private void WarnNoReplicaOnce(string reason)
        {
            if (m_warnedNoReplica) return;
            m_warnedNoReplica = true;
            Log.Warning("[SuMP] 客户端开弓时没有挂上实体复制适配器（" + reason +
                "）→ 请求不会发出，本机也不生成幽灵箭（检查实体复制模块是否 Active）");
        }
    }

    /// <summary>
    /// 客户端弩开火拦截（与弓同一模式；只有 draw==15 且装了弩箭时主机才会生成投射物，
    /// 但 Completed 的"重置 draw + 扣耐久"由主机照引擎口径执行）。
    ///
    /// Source: Survivalcraft/Game/SubsystemCrossbowBlockBehavior.cs:OnAim
    /// </summary>
    public sealed class SuSubsystemCrossbowBlockBehavior : SubsystemCrossbowBlockBehavior
    {
        private bool m_warnedNoReplica;

        public override bool OnAim(Ray3 aim, ComponentMiner componentMiner, AimState state)
        {
            if (!IsClientReplica || state != AimState.Completed)
                return base.OnAim(aim, componentMiner, state);
            IInventory inventory = componentMiner?.Inventory;
            if (inventory == null) return false;
            int slot = inventory.ActiveSlotIndex;
            if (slot < 0 || slot >= inventory.SlotsCount) return false;
            int value = inventory.GetSlotValue(slot);
            int count = inventory.GetSlotCount(slot);
            if (count <= 0 || value == 0) return false;
            int contents = global::Game.Terrain.ExtractContents(value);
            if (contents != CrossbowBlock.Index) return base.OnAim(aim, componentMiner, state);
            Block block = BlocksManager.Blocks[contents];
            if (!(block is CrossbowBlock)) return base.OnAim(aim, componentMiner, state);

            int data = global::Game.Terrain.ExtractData(value);
            int draw = CrossbowBlock.GetDraw(data);
            if (draw != 15)
                componentMiner.ComponentPlayer?.ComponentGui.DisplaySmallMessage(
                    "Draw the crossbow first", Color.White, blinking: true,
                    playNotificationSound: false);
            else if (!CrossbowBlock.GetArrowType(data).HasValue)
                componentMiner.ComponentPlayer?.ComponentGui.DisplaySmallMessage(
                    "Load a bolt first", Color.White, blinking: true,
                    playNotificationSound: false);
            Vector3 origin = componentMiner.ComponentCreature.ComponentCreatureModel.EyePosition +
                componentMiner.ComponentCreature.ComponentBody.Matrix.Right * 0.3f -
                componentMiner.ComponentCreature.ComponentBody.Matrix.Up * 0.2f;
            var intent = new ProjectileFireIntent(ProjectileFireKinds.Crossbow, value, slot,
                origin, Vector3.Normalize(aim.Direction), MathUtils.Clamp(draw, 0, 15));
            PickableReplicaEngine replica = ProjectileFireClient.GetReplica(Project,
                out string replicaReason);
            if (replica == null)
            {
                WarnNoReplicaOnce(replicaReason);
                return false;
            }
            if (!replica.SubmitClientProjectileFire(in intent, out string reason))
                return false;
            ProjectileFirePresentation.PlayBow(Project, origin, crossbow: true);
            return true;
        }

        private bool IsClientReplica =>
            SubsystemTerrain is SuSubsystemTerrain shell && shell.ClientReplica;

        private void WarnNoReplicaOnce(string reason)
        {
            if (m_warnedNoReplica) return;
            m_warnedNoReplica = true;
            Log.Warning("[SuMP] 客户端开弩时没有挂上实体复制适配器（" + reason +
                "）→ 请求不会发出，本机也不生成幽灵弩箭（检查实体复制模块是否 Active）");
        }
    }

    /// <summary>
    /// 客户端火枪开火拦截。
    ///
    /// Source: Survivalcraft/Game/SubsystemMusketBlockBehavior.cs:OnAim
    /// </summary>
    public sealed class SuSubsystemMusketBlockBehavior : SubsystemMusketBlockBehavior
    {
        private bool m_warnedNoReplica;

        public override bool OnAim(Ray3 aim, ComponentMiner componentMiner, AimState state)
        {
            if (!IsClientReplica || state != AimState.Completed)
                return base.OnAim(aim, componentMiner, state);
            IInventory inventory = componentMiner?.Inventory;
            if (inventory == null) return false;
            int slot = inventory.ActiveSlotIndex;
            if (slot < 0 || slot >= inventory.SlotsCount) return false;
            int value = inventory.GetSlotValue(slot);
            int count = inventory.GetSlotCount(slot);
            if (count <= 0 || value == 0) return false;
            int contents = global::Game.Terrain.ExtractContents(value);
            if (contents != MusketBlock.Index) return base.OnAim(aim, componentMiner, state);
            Block block = BlocksManager.Blocks[contents];
            if (!(block is MusketBlock)) return base.OnAim(aim, componentMiner, state);

            int data = global::Game.Terrain.ExtractData(value);
            if (!MusketBlock.GetHammerState(data))
            {
                // 引擎口径：击锤没扳起来时 Completed 什么都不做（连提示都没有）。
                return true;
            }
            if (MusketBlock.GetLoadState(data) == MusketBlock.LoadState.Empty)
                componentMiner.ComponentPlayer?.ComponentGui.DisplaySmallMessage(
                    "Load gunpowder first", Color.White, blinking: true,
                    playNotificationSound: false);
            Vector3 origin = componentMiner.ComponentCreature.ComponentCreatureModel.EyePosition +
                componentMiner.ComponentCreature.ComponentBody.Matrix.Right * 0.3f -
                componentMiner.ComponentCreature.ComponentBody.Matrix.Up * 0.2f;
            Vector3 direction = Vector3.Normalize(aim.Direction);
            var intent = new ProjectileFireIntent(ProjectileFireKinds.Musket, value, slot, origin,
                direction, 1);
            PickableReplicaEngine replica = ProjectileFireClient.GetReplica(Project,
                out string replicaReason);
            if (replica == null)
            {
                WarnNoReplicaOnce(replicaReason);
                return false;
            }
            if (!replica.SubmitClientProjectileFire(in intent, out string reason))
                return false;
            if (componentMiner.ComponentCreature.ComponentBody.ImmersionFactor > 0.4f)
                ProjectileFirePresentation.PlayMisfire(Project, origin);
            else
                ProjectileFirePresentation.PlayMusket(Project, componentMiner, origin, direction);
            return true;
        }

        private bool IsClientReplica =>
            SubsystemTerrain is SuSubsystemTerrain shell && shell.ClientReplica;

        private void WarnNoReplicaOnce(string reason)
        {
            if (m_warnedNoReplica) return;
            m_warnedNoReplica = true;
            Log.Warning("[SuMP] 客户端开火枪时没有挂上实体复制适配器（" + reason +
                "）→ 请求不会发出，本机也不生成幽灵子弹（检查实体复制模块是否 Active）");
        }
    }

    /// <summary>三个武器外壳共用的适配器查找（与爆炸外壳同一模式：从注入后的子系统取实例）。</summary>
    internal static class ProjectileFireClient
    {
        public static PickableReplicaEngine GetReplica(Project project, out string reason)
        {
            reason = null;
            if (project == null)
            {
                reason = "no-project";
                return null;
            }
            SubsystemProjectiles projectiles =
                project.FindSubsystem<SubsystemProjectiles>(false);
            if (projectiles is SuSubsystemProjectiles shell)
            {
                if (shell.Replica != null) return shell.Replica;
                reason = "replica-not-pumped";
                return null;
            }
            reason = "projectile-shell-missing";
            return null;
        }
    }
}

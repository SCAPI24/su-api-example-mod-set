using System;
using Engine;
using Game;
using TemplatesDatabase;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端投掷拦截（**只拦爆炸物**：BombBlock / IncendiaryBombBlock / FireworksBlock）。
    ///
    /// 原引擎路径：<c>SubsystemThrowableBlockBehavior.OnAim(Completed)</c> 在**本机**
    /// FireProjectile + 扣道具。联机时客户端不模拟投掷物（否则客户端会出现一个主机不知道的
    /// "幽灵炸弹"，且爆炸只能在客户端本地发生）。这里改成：
    ///   · 客户端：发 ExplosionRequest(THROW) → 主机校验后 FireProjectile → 爆炸由主机广播；
    ///   · 主机 / 单机：照旧 base.OnAim（原路径不动）；
    ///   · 非爆炸类投掷物（长矛 / 雪球 …）保持原行为，不影响本次范围外的功能。
    ///
    /// Source: Survivalcraft/Game/SubsystemThrowableBlockBehavior.cs:OnAim
    /// </summary>
    public sealed class SuSubsystemThrowableBlockBehavior : SubsystemThrowableBlockBehavior
    {
        private bool m_warnedNoReplica;

        public override bool OnAim(Ray3 aim, ComponentMiner componentMiner, AimState state)
        {
            if (!IsClientReplica || state != AimState.Completed)
                return base.OnAim(aim, componentMiner, state);
            if (componentMiner?.Inventory == null) return false;
            int slot = componentMiner.Inventory.ActiveSlotIndex;
            int value = componentMiner.Inventory.GetSlotValue(slot);
            int count = componentMiner.Inventory.GetSlotCount(slot);
            if (count <= 0) return false;
            int contents = global::Game.Terrain.ExtractContents(value);
            if (contents <= 0 || BlocksManager.Blocks == null ||
                contents >= BlocksManager.Blocks.Length) return false;
            Block block = BlocksManager.Blocks[contents];
            if (!(block is BombBlock) && !(block is IncendiaryBombBlock) && !(block is FireworksBlock))
                return base.OnAim(aim, componentMiner, state);

            ExplosionReplicaEngine replica = GetReplica();
            if (replica == null)
            {
                if (!m_warnedNoReplica)
                {
                    m_warnedNoReplica = true;
                    Log.Warning("[SuMP] 客户端投掷爆炸物时没有挂上爆炸同步适配器 → 请求不会发出，" +
                        "本机也不放幽灵投掷物（检查爆炸同步模块是否 Active）");
                }
                return false;
            }
            Vector3 eye = componentMiner.ComponentCreature.ComponentCreatureModel.EyePosition;
            var request = new ExplosionRequestData(0L, ExplosionRequestKinds.Throw, eye,
                aim.Direction, value, slot);
            return replica.SubmitClientRequest(in request, out _);
        }

        private bool IsClientReplica =>
            SubsystemTerrain is SuSubsystemTerrain shell && shell.ClientReplica;

        private ExplosionReplicaEngine GetReplica()
        {
            SubsystemExplosions explosions =
                Project?.FindSubsystem<SubsystemExplosions>(false);
            return (explosions as SuSubsystemExplosions)?.Replica;
        }
    }

    /// <summary>
    /// 客户端"用火柴点引信"拦截（**只拦火药桶 / 雷管**）。
    ///
    /// 原引擎路径：<c>SubsystemMatchBlockBehavior.OnUse</c> 直接
    /// <c>IgniteFuse</c> + 扣火柴；客户端本地点火只会得到"本地引信粒子 + 永不爆炸"
    /// （爆炸队列在客户端被闸门清空），主机完全不知道。
    /// 这里改成：客户端只发 ExplosionRequest(IGNITE)，主机校验方块 / 距离后 IgniteFuse；
    /// 主机的爆炸检测随后照常广播。点普通方块 / 生物的火保持原路径。
    ///
    /// Source: Survivalcraft/Game/SubsystemMatchBlockBehavior.cs:OnUse
    /// </summary>
    public sealed class SuSubsystemMatchBlockBehavior : SubsystemMatchBlockBehavior
    {
        private bool m_warnedNoReplica;

        public override bool OnUse(Ray3 ray, ComponentMiner componentMiner)
        {
            if (!IsClientReplica || componentMiner?.Inventory == null)
                return base.OnUse(ray, componentMiner);
            if (!(componentMiner.Raycast(ray, RaycastMode.Digging) is TerrainRaycastResult result))
                return base.OnUse(ray, componentMiner);
            CellFace cell = result.CellFace;
            int cellValue = SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z);
            int contents = global::Game.Terrain.ExtractContents(cellValue);
            if (contents <= 0 || BlocksManager.Blocks == null ||
                contents >= BlocksManager.Blocks.Length) return base.OnUse(ray, componentMiner);
            Block block = BlocksManager.Blocks[contents];
            if (!(block is GunpowderKegBlock) && !(block is DetonatorBlock))
                return base.OnUse(ray, componentMiner);

            ExplosionReplicaEngine replica = GetReplica();
            if (replica == null)
            {
                if (!m_warnedNoReplica)
                {
                    m_warnedNoReplica = true;
                    Log.Warning("[SuMP] 客户端点燃爆炸物时没有挂上爆炸同步适配器 → 请求不会发出，" +
                        "本机也不点本地引信（检查爆炸同步模块是否 Active）");
                }
                return false;
            }
            var request = new ExplosionRequestData(0L, ExplosionRequestKinds.Ignite,
                new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f), Vector3.Zero,
                componentMiner.ActiveBlockValue, componentMiner.Inventory.ActiveSlotIndex);
            return replica.SubmitClientRequest(in request, out _);
        }

        private bool IsClientReplica =>
            SubsystemTerrain is SuSubsystemTerrain shell && shell.ClientReplica;

        private ExplosionReplicaEngine GetReplica()
        {
            SubsystemExplosions explosions =
                Project?.FindSubsystem<SubsystemExplosions>(false);
            return (explosions as SuSubsystemExplosions)?.Replica;
        }
    }

    /// <summary>
    /// 烟花表现同步（**cosmetic only**）。
    ///
    /// 烟花不经过 SubsystemExplosions：<c>FireworksBlockBehavior.OnFiredAsProjectile</c> 自己排队
    /// 延时，到点后 <c>ExplodeFireworks</c> 直接加 FireworksParticleSystem。因此：
    ///   · 主机：复刻同一段投掷表现（trail / whoosh / noise）+ 同一延时；到点时先跑原
    ///     ExplodeFireworks（主机本地表现），再发一条 cosmetic ExplosionEvent；
    ///   · 客户端：不跑 base.Update（不会自己放新年烟花），也没有本地烟花投掷物；
    ///     只消费主机的 cosmetic 事件（粒子 + 声音）。
    ///
    /// Source: Survivalcraft/Game/SubsystemFireworksBlockBehavior.cs:OnFiredAsProjectile
    /// Source: Survivalcraft/Game/SubsystemFireworksBlockBehavior.cs:ExplodeFireworks
    /// </summary>
    public sealed class SuSubsystemFireworksBlockBehavior : SubsystemFireworksBlockBehavior, IUpdateable
    {
        private readonly global::Engine.Random m_random = new global::Engine.Random();
        private SubsystemProjectiles m_projectiles;
        private SubsystemAudio m_audio;
        private SubsystemNoise m_noise;
        private SubsystemTime m_time;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_projectiles = Project.FindSubsystem<SubsystemProjectiles>(false);
            m_audio = Project.FindSubsystem<SubsystemAudio>(false);
            m_noise = Project.FindSubsystem<SubsystemNoise>(false);
            m_time = Project.FindSubsystem<SubsystemTime>(false);
        }

        void IUpdateable.Update(float dt)
        {
            if (IsClientReplica) return;
            base.Update(dt);
        }

        public override void OnFiredAsProjectile(Projectile projectile)
        {
            if (IsClientReplica) return;
            int data = global::Game.Terrain.ExtractData(projectile.Value);
            float delay = FireworksBlock.GetAltitude(data) == 0 ? 0.8f : 1.3f;
            m_projectiles?.AddTrail(projectile, Vector3.Zero, new FireworksTrailParticleSystem());
            m_audio?.PlayRandomSound("Audio/FireworksWhoosh", 1f,
                m_random.Float(-0.2f, 0.2f), projectile.Position, 8f, autoDelay: true);
            m_noise?.MakeNoise(projectile.Position, 1f, 10f);
            if (m_time == null)
            {
                Log.Warning("[SuMP] 烟花行为缺少 SubsystemTime：烟花爆开不会同步（明确失败）");
                return;
            }
            m_time.QueueGameTimeDelayedExecution(m_time.GameTime + delay, delegate
            {
                if (projectile.ToRemove) return;
                projectile.ToRemove = true;
                bool canBurst = CanBurst(projectile.Position);
                ExplodeFireworks(projectile.Position, data);
                if (canBurst) GetReplica()?.PublishHostFirework(projectile.Position, projectile.Value);
            });
        }

        /// <summary>
        /// 与引擎 <c>ExplodeFireworks</c> 的"头顶被挡就不爆"判定同形：3 条向下射线。
        /// 主机自己没爆（被天花板挡住）时不能给客户端发一条假烟花。
        /// </summary>
        private bool CanBurst(Vector3 position)
        {
            if (SubsystemTerrain == null) return false;
            for (int i = 0; i < 3; i++)
            {
                Vector3 offset = new Vector3(m_random.Float(-3f, 3f), -15f, m_random.Float(-3f, 3f));
                if (SubsystemTerrain.Raycast(position, position + offset, useInteractionBoxes: false,
                        skipAirBlocks: true, null).HasValue) return false;
            }
            return true;
        }

        private bool IsClientReplica =>
            SubsystemTerrain is SuSubsystemTerrain shell && shell.ClientReplica;

        private ExplosionReplicaEngine GetReplica()
        {
            SubsystemExplosions explosions =
                Project?.FindSubsystem<SubsystemExplosions>(false);
            return (explosions as SuSubsystemExplosions)?.Replica;
        }
    }
}

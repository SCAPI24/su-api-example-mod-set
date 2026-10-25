using System;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端爆炸**表现**（唯一允许的客户端反应）。
    ///
    /// 允许：粒子（火焰 / 烟 / 方块碎屑）、声音、镜头震动（ComponentBody.ApplyShaking）。
    /// 禁止：任何地形写入、任何实体 / 可拾取物 / 投掷物生成、任何伤害 / 血量写入。
    /// 这里不引用 SubsystemPickables / SubsystemProjectiles / ComponentHealth；
    /// SubsystemTerrain 仅以**只读**方式用于 BlockDebrisParticleSystem（构造器读光照、
    /// Simulate 做射线检测，与引擎 BurnAwayEffectReplicaEngine 的 BurntDebris 同口径）。
    ///
    /// 视觉参数口径来自引擎的 <c>SubsystemExplosions.SimulateExplosion/PostprocessExplosions</c>：
    /// 事件里的 radius = 爆炸压力值；表现半径取 sqrt(pressure) 并夹紧（引擎压力场没有随事件下发，
    /// 不伪造逐格压力值，只按距离做平滑衰减）。
    /// 雷击（ExplosionKinds.Lightning）：主机除了爆炸本身（1 格火团），还在被劈格子上挂
    /// BlockDebrisParticleSystem（SubsystemSky.cs:249-255，125 粒方块同色碎屑）——
    /// 事件 block_value 携带被劈格子的原值，这里原样复刻；且雷击是静音爆炸
    /// （noExplosionSound:true，雷声走音频路径），不播 Audio/Explosion*。
    /// </summary>
    internal sealed class ExplosionReplicaPresentation
    {
        private const int MaxParticleCells = 800;

        public bool TryPresent(in ExplosionEventData explosion, out string reason)
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            SubsystemParticles particles = project.FindSubsystem<SubsystemParticles>(false);
            if (particles == null)
            {
                reason = "no-particles";
                return false;
            }
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            // 雷击的碎屑粒子需要 SubsystemTerrain（构造器读光照、Simulate 做射线检测）；
            // 缺地形必须**在添加任何粒子之前**失败（明确原因，不静默少一半表现）。
            if (explosion.Kind == ExplosionKinds.Lightning &&
                project.FindSubsystem<SubsystemTerrain>(false) == null)
            {
                reason = "no-terrain";
                return false;
            }
            if (explosion.CosmeticOnly || explosion.Kind == ExplosionKinds.Firework)
            {
                if (!PresentFirework(explosion, particles, audio, out reason)) return false;
                reason = null;
                return true;
            }
            PresentExplosion(explosion, project, particles, audio);
            reason = null;
            return true;
        }

        private static void PresentExplosion(in ExplosionEventData explosion, Project project,
            SubsystemParticles particles, SubsystemAudio audio)
        {
            float pressure = explosion.Pressure;
            // 主机已经按 ExplosionVisualPolicy 换算好可视半径；压力只用于音量 / 震动强度。
            // 下限 0.5：距离 ≤ 0.5 只有中心格 → 闪电档只渲染 1 格火团（对齐主机实测；
            // 周围一圈碎屑由投射物实体复制负责，见 ExplosionVisualPolicy 三调注释）。
            float visualRadius = MathUtils.Clamp(explosion.Radius, 0.5f, 24f);
            var particleSystem = new ExplosionParticleSystem();
            int radius = (int)MathUtils.Ceiling(visualRadius);
            int cells = 0;
            int centerX = global::Game.Terrain.ToCell(explosion.Position.X);
            int centerY = global::Game.Terrain.ToCell(explosion.Position.Y);
            int centerZ = global::Game.Terrain.ToCell(explosion.Position.Z);
            for (int dx = -radius; dx <= radius && cells < MaxParticleCells; dx++)
            {
                for (int dy = -radius; dy <= radius && cells < MaxParticleCells; dy++)
                {
                    for (int dz = -radius; dz <= radius && cells < MaxParticleCells; dz++)
                    {
                        float distance = MathUtils.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (distance > visualRadius) continue;
                        float strength = MathUtils.Saturate(1f - distance / (visualRadius + 1f));
                        strength = 0.2f + 0.8f * strength;
                        particleSystem.SetExplosionCell(
                            new Point3(centerX + dx, centerY + dy, centerZ + dz), strength);
                        cells++;
                    }
                }
            }
            if (cells == 0)
                particleSystem.SetExplosionCell(new Point3(centerX, centerY, centerZ), 1f);
            particles.AddParticleSystem(particleSystem);

            // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike:249-255
            // 雷击：复刻主机挂在被劈格子上的方块碎屑粒子（block_value = 被劈格子的原值，
            // 主机在消费前读好；事件位置恰好等于主机碎屑出生点 x+0.5/y+1.5/z+0.5）。
            bool isLightning = explosion.Kind == ExplosionKinds.Lightning;
            if (isLightning)
                PresentLightningDebris(explosion, project, particles);

            // 雷击是静音爆炸（noExplosionSound:true，SubsystemSky.cs:247）：主机不播
            // Audio/Explosion*（雷声由 SubsystemSky 走自己的音频路径），客户端同样不播。
            if (audio != null && pressure > 0f && !isLightning)
            {
                string sound;
                float minDistance;
                if (pressure > 3000f) { sound = "Audio/Explosion7"; minDistance = 53f; }
                else if (pressure > 1500f) { sound = "Audio/Explosion6"; minDistance = 45f; }
                else if (pressure > 600f) { sound = "Audio/Explosion5"; minDistance = 38f; }
                else if (pressure > 250f) { sound = "Audio/Explosion4"; minDistance = 32f; }
                else if (pressure > 100f) { sound = "Audio/Explosion3"; minDistance = 27f; }
                else if (pressure > 30f) { sound = "Audio/Explosion2"; minDistance = 23f; }
                else { sound = "Audio/Explosion1"; minDistance = 20f; }
                audio.PlaySound(sound, 1f, 0f, explosion.Position, minDistance, autoDelay: true);
            }

            // 镜头震动：与引擎 ApplyBodiesShaking 相同的衰减形状；只作用于表现，不造成伤害。
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(false);
            if (bodies != null && pressure > 0f)
            {
                float maxStrength = 5f * MathUtils.Sqrt(pressure);
                float falloff = MathUtils.Max(1f * MathUtils.Sqrt(pressure), 0.1f);
                foreach (ComponentBody body in bodies.Bodies)
                {
                    float distance = Vector3.Distance(body.Position, explosion.Position);
                    float strength = maxStrength / (distance / falloff + 1f);
                    if (strength > 0.01f) body.ApplyShaking(strength);
                }
            }
        }

        /// <summary>
        /// Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike:249-255
        /// 主机落雷时给被劈格子挂 <c>block.CreateDebrisParticleSystem(..., 2.5f)</c>
        /// （125 粒方块同色碎屑，strength 2.5）。事件 block_value 由主机在破坏前读好
        /// （被劈格子的原值）；值非空才生成（主机同款 num6 != 0 检查）——被劈到空气 /
        /// 水面时两边一致地只有火团没有碎屑。只读地形（光照 / 射线），不写任何格。
        /// </summary>
        private static void PresentLightningDebris(in ExplosionEventData explosion, Project project,
            SubsystemParticles particles)
        {
            int cellValue = (int)explosion.BlockValue;
            int contents = global::Game.Terrain.ExtractContents(cellValue);
            if (contents <= 0 || BlocksManager.Blocks == null ||
                contents >= BlocksManager.Blocks.Length)
                return;
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            Block block = BlocksManager.Blocks[contents];
            particles.AddParticleSystem(block.CreateDebrisParticleSystem(
                terrain, explosion.Position, cellValue, 2.5f));
        }

        private static bool PresentFirework(in ExplosionEventData explosion,
            SubsystemParticles particles, SubsystemAudio audio, out string reason)
        {
            int data = global::Game.Terrain.ExtractData((int)explosion.BlockValue);
            FireworksBlock.Shape shape = FireworksBlock.GetShape(data);
            int colorIndex = FireworksBlock.GetColor(data);
            if (colorIndex < 0 || colorIndex >= FireworksBlock.FireworksColors.Length)
            {
                reason = "firework-color-" + colorIndex;
                return false;
            }
            float flickering = FireworksBlock.GetFlickering(data) ? 0.66f : 0f;
            float particleSize = FireworksBlock.GetAltitude(data) > 0 ? 1.1f : 1f;
            var particleSystem = new FireworksParticleSystem(explosion.Position,
                FireworksBlock.FireworksColors[colorIndex], shape, flickering, particleSize);
            particles.AddParticleSystem(particleSystem);
            if (audio != null)
            {
                audio.PlayRandomSound("Audio/FireworksPop", 1f, 0f, explosion.Position, 80f,
                    autoDelay: true);
            }
            reason = null;
            return true;
        }
    }
}

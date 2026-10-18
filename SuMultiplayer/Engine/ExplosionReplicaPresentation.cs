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
    /// 允许：粒子（火焰 / 烟）、声音、镜头震动（ComponentBody.ApplyShaking）。
    /// 禁止：任何地形写入、任何实体 / 可拾取物 / 投掷物生成、任何伤害 / 血量写入。
    /// 这里没有引用 SubsystemTerrain / SubsystemPickables / SubsystemProjectiles /
    /// ComponentHealth，靠类型系统把"客户端只表现"钉死。
    ///
    /// 视觉参数口径来自引擎的 <c>SubsystemExplosions.SimulateExplosion/PostprocessExplosions</c>：
    /// 事件里的 radius = 爆炸压力值；表现半径取 sqrt(pressure) 并夹紧（引擎压力场没有随事件下发，
    /// 不伪造逐格压力值，只按距离做平滑衰减）。
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
            float visualRadius = MathUtils.Clamp(explosion.Radius, 1f, 24f);
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

            if (audio != null && pressure > 0f)
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

using Engine;
using Engine.Graphics;
using Game;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端炸弹投射物的“点燃引线”尾迹：火药桶式的火焰/火花粒子跟随炸弹，
    /// 而不是普通白色烟雾。粒子只在世界表现层存在，不写地形、不推进爆炸。
    /// </summary>
    public sealed class BombFuseTrailParticleSystem : ParticleSystem<BombFuseTrailParticleSystem.Particle>, ITrailParticleSystem
    {
        public sealed class Particle : Game.Particle
        {
            public float Time;
            public float TimeToLive;
            public float Speed;
            public float TargetSpeed;
        }

        private readonly global::Engine.Random m_random = new global::Engine.Random();
        private Projectile m_target;
        private Vector3 m_position;
        private float m_toGenerate;
        private bool m_isStopped;

        public Vector3 Position
        {
            get => m_position;
            set => m_position = value;
        }

        public bool IsStopped
        {
            get => m_isStopped;
            set => m_isStopped = value;
        }

        public BombFuseTrailParticleSystem(Projectile target) : base(24)
        {
            m_target = target;
            Texture = ContentManager.Get<Texture2D>("Textures/FireParticle");
            TextureSlotsCount = 3;
        }

        public override bool Simulate(float dt)
        {
            if (m_isStopped || m_target == null || m_target.ToRemove) return false;
            // 与引擎 SubsystemProjectiles.Update 完全同口径：TrailOffset 要随投射物
            // 旋转矩阵变换后再加到 Position，否则旋转过的炸弹上会落到空气里。
            Vector3 trailOffset = m_target.TrailOffset;
            if (trailOffset != Vector3.Zero)
            {
                float length = m_target.Rotation.Length();
                if (length > 1e-5f)
                    trailOffset = Vector3.TransformNormal(trailOffset,
                        Matrix.CreateFromAxisAngle(m_target.Rotation / length, length));
            }
            m_position = m_target.Position + trailOffset;
            m_toGenerate += 22f * dt;
            for (int i = 0; i < Particles.Length; i++)
            {
                Particle particle = Particles[i];
                if (particle.IsActive)
                {
                    particle.Time += dt;
                    particle.TimeToLive -= dt;
                    if (particle.TimeToLive > 0f)
                    {
                        particle.Position.Y += particle.Speed * dt;
                        particle.Speed = MathUtils.Max(particle.Speed - 1.5f * dt,
                            particle.TargetSpeed);
                        particle.TextureSlot = (int)MathUtils.Min(9f * particle.Time / 0.75f, 8f);
                        particle.Size = new Vector2(0.07f * (1f + 2f * particle.Time));
                    }
                    else
                    {
                        particle.IsActive = false;
                    }
                }
                else if (m_toGenerate >= 1f)
                {
                    particle.IsActive = true;
                    particle.Position = m_position + 0.03f * new Vector3(
                        m_random.Float(-1f, 1f), m_random.Float(-1f, 1f),
                        m_random.Float(-1f, 1f));
                    particle.Color = Color.White;
                    particle.TargetSpeed = m_random.Float(0.45f, 0.55f) * 0.4f;
                    particle.Speed = m_random.Float(0.45f, 0.55f) * 2.5f;
                    particle.Time = 0f;
                    particle.Size = Vector2.Zero;
                    particle.TimeToLive = m_random.Float(0.3f, 1f);
                    particle.FlipX = m_random.Int(0, 1) == 0;
                    particle.FlipY = m_random.Int(0, 1) == 0;
                    m_toGenerate -= 1f;
                }
            }
            m_toGenerate = MathUtils.Remainder(m_toGenerate, 1f);
            return false;
        }
    }
}

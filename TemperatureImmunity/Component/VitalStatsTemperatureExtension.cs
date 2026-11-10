using System;
using Engine;
using Engine.Audio;
using Game;
using SuAPI;

namespace TemperatureImmunity
{
    // Source: ComponentVitalStats.cs:Update/UpdateTemperature; IModParentMethod.cs:BindInstanceMethod.
    // Validate the exact SDK contract once. Hot paths use bound delegates on the real host.
    internal sealed class VitalStatsTemperatureBindings
    {
        public readonly Action<ComponentVitalStats> UpdateFood;
        public readonly Action<ComponentVitalStats> UpdateStamina;
        public readonly Action<ComponentVitalStats> UpdateSleep;
        public readonly Action<ComponentVitalStats> UpdateWetness;
        public readonly ModFieldRef<ComponentVitalStats, Sound> PantingSound;
        public readonly ModFieldRef<ComponentVitalStats, float> Temperature;
        public readonly ModFieldRef<ComponentVitalStats, float> TargetTemperature;
        public readonly ModFieldRef<ComponentVitalStats, float> TargetTemperatureFlux;
        public readonly ModFieldRef<ComponentVitalStats, float> LastTemperature;
        public readonly ModFieldRef<ComponentVitalStats, float> BlackoutDuration;
        public readonly ModFieldRef<ComponentVitalStats, float> BlackoutFactor;
        public readonly ModFieldRef<ComponentVitalStats, float> EnvironmentTemperature;

        public VitalStatsTemperatureBindings(IModParentField fields, IModParentMethod methods)
        {
            UpdateFood = methods.BindInstanceMethod<ComponentVitalStats, Action<ComponentVitalStats>>("UpdateFood", Type.EmptyTypes);
            UpdateStamina = methods.BindInstanceMethod<ComponentVitalStats, Action<ComponentVitalStats>>("UpdateStamina", Type.EmptyTypes);
            UpdateSleep = methods.BindInstanceMethod<ComponentVitalStats, Action<ComponentVitalStats>>("UpdateSleep", Type.EmptyTypes);
            UpdateWetness = methods.BindInstanceMethod<ComponentVitalStats, Action<ComponentVitalStats>>("UpdateWetness", Type.EmptyTypes);
            PantingSound = fields.BindFieldRef<ComponentVitalStats, Sound>("m_pantingSound");
            Temperature = fields.BindFieldRef<ComponentVitalStats, float>("m_temperature");
            TargetTemperature = fields.BindFieldRef<ComponentVitalStats, float>("m_targetTemperature");
            TargetTemperatureFlux = fields.BindFieldRef<ComponentVitalStats, float>("m_targetTemperatureFlux");
            LastTemperature = fields.BindFieldRef<ComponentVitalStats, float>("m_lastTemperature");
            BlackoutDuration = fields.BindFieldRef<ComponentVitalStats, float>("m_temperatureBlackoutDuration");
            BlackoutFactor = fields.BindFieldRef<ComponentVitalStats, float>("m_temperatureBlackoutFactor");
            EnvironmentTemperature = fields.BindFieldRef<ComponentVitalStats, float>("m_environmentTemperature");
        }
    }

    internal sealed class VitalStatsTemperatureExtension : UpdateExtension<ComponentVitalStats>
    {
        private readonly VitalStatsTemperatureBindings m_state;
        private ComponentPlayer m_player;
        private SubsystemTime m_time;
        private SubsystemGameInfo m_gameInfo;
        private SubsystemMetersBlockBehavior m_meters;

        public VitalStatsTemperatureExtension(ComponentVitalStats host, VitalStatsTemperatureBindings state) : base(host)
        {
            m_state = state;
        }

        // Source: ComponentVitalStats.cs:Load — these references belong to this player's real host.
        public override void Attached()
        {
            m_player = Host.Entity.FindComponent<ComponentPlayer>(throwOnError: true);
            m_time = Host.Project.FindSubsystem<SubsystemTime>(throwOnError: true);
            m_gameInfo = Host.Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
            m_meters = Host.Project.FindSubsystem<SubsystemMetersBlockBehavior>(throwOnError: true);
            HoldTemperature();
        }

        // Source: ComponentVitalStats.cs:UpdateTemperature; UpdateExtension.cs:Update(float,bool).
        // Temperature damage occurs inside native Update, before an AfterUpdate correction could help.
        public override void Update(float dt)
        {
            base.Update(dt, false);
        }

        // Source: ComponentVitalStats.cs:Update — retain native order and the dead-player sound branch.
        // SuAPI reaches this only after every Mod has completed its pre-update callback.
        public override void AfterUpdate(float dt, InvocationResult result)
        {
            if (m_player.ComponentHealth.Health > 0f)
            {
                m_state.UpdateFood(Host);
                m_state.UpdateStamina(Host);
                m_state.UpdateSleep(Host);
                UpdateTemperature();
                m_state.UpdateWetness(Host);
            }
            else
            {
                m_state.PantingSound(Host).Stop();
            }
        }

        // Source: ComponentVitalStats.cs:UpdateTemperature — retain ambient sampling on the native schedule.
        private void UpdateTemperature()
        {
            if (m_time.PeriodicGameTimeEvent(1.0, (double)(Host.GetHashCode() % 1000) / 1000.0))
            {
                float insulation = m_player.ComponentClothing.Insulation *
                    MathUtils.Lerp(1f, 0.05f, MathUtils.Saturate(4f * Host.Wetness));
                if (m_gameInfo.WorldSettings.GameMode <= GameMode.Survival)
                    insulation = insulation * 1.5f + 1f;
                Vector3 position = m_player.ComponentBody.Position;
                m_meters.CalculateTemperature(Terrain.ToCell(position.X), Terrain.ToCell(position.Y + 0.1f),
                    Terrain.ToCell(position.Z), 12f, insulation, out _, out _, out float environment);
                m_state.EnvironmentTemperature(Host) = environment;
            }
            HoldTemperature();
            m_player.ComponentScreenOverlays.IceFactor = 0f;
            m_player.ComponentGui.TemperatureBarWidget.BarSubtexture =
                ContentManager.Get<Subtexture>("Textures/Atlas/Temperature3");
        }

        // Source: ComponentVitalStats.cs:UpdateTemperature — clear only temperature-owned blackout state.
        // Shared blackout/notification fields can belong to sleep, sickness, or another Mod.
        private void HoldTemperature()
        {
            m_state.Temperature(Host) = 12f;
            m_state.TargetTemperature(Host) = 12f;
            m_state.TargetTemperatureFlux(Host) = 0f;
            m_state.LastTemperature(Host) = 12f;
            m_state.BlackoutDuration(Host) = 0f;
            m_state.BlackoutFactor(Host) = 0f;
        }
    }
}

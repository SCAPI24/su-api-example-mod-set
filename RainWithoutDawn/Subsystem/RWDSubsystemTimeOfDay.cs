using Game;
using SuAPI;

namespace RainWithoutDawn
{
    public sealed class RWDWeatherExtension : UpdateExtension<SubsystemWeather>
    {
        private SubsystemGameInfo m_gameInfo;
        private SubsystemTimeOfDay m_timeOfDay;

        public RWDWeatherExtension(SubsystemWeather host) : base(host) { }

        // Source: RWDSubsystemTimeOfDay.cs:RWDSubsystemTimeOfDay.Load
        public override void Attached()
        {
            m_gameInfo = Host.Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
            m_timeOfDay = Host.Project.FindSubsystem<SubsystemTimeOfDay>(throwOnError: true);
        }

        // Source: RWDSubsystemTimeOfDay.cs:RWDSubsystemTimeOfDay.Update/IsNotMidnight
        // Keep the original midnight interval and public weather controls on the real hosts.
        public override void AfterUpdate(float dt, InvocationResult result)
        {
            if (!Host.IsPrecipitationStarted) Host.ManualPrecipitationStart();
            if (!Host.IsFogStarted) Host.ManualFogStart();

            if (!(IntervalUtils.IsBetween(m_timeOfDay.TimeOfDay, m_timeOfDay.Midnight - 0.1f, m_timeOfDay.Midnight)
                || IntervalUtils.IsBetween(m_timeOfDay.TimeOfDay, 0f, 0.1f)))
            {
                float timeToMidnight = IntervalUtils.Interval(
                    m_timeOfDay.CalculateTimeOfDay(m_gameInfo.TotalElapsedGameTime), m_timeOfDay.Midnight - 0.09f);
                m_timeOfDay.TimeOfDayOffset += timeToMidnight;
            }
        }
    }
}

using System;
using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace RainWithoutDawn
{
    public class RWDSubsystemTimeOfDay : Game.SubsystemTimeOfDay,IUpdateable
    {
        private SubsystemGameInfo m_subsystemGameInfo;
        private SubsystemWeather m_subsystemWeather;

        private SubsystemSeasons m_subsystemSeasons;

        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        public void Update(float dt)
        {
            if (m_subsystemGameInfo != null)
            {
                if (!m_subsystemWeather.IsPrecipitationStarted) m_subsystemWeather.ManualPrecipitationStart();
                if (!m_subsystemWeather.IsFogStarted) m_subsystemWeather.ManualFogStart();
            }
            if (IsNotMidnight())
            {            // 计算当前时间到午夜的偏移量
                float timeToMidnight = IntervalUtils.Interval(CalculateTimeOfDay(m_subsystemGameInfo.TotalElapsedGameTime), Midnight-0.09f);

                // 强制跳转到午夜
                TimeOfDayOffset += timeToMidnight;
            }

        }

        // 方法1：直接比较 TimeOfDay 和 Midnight
        private bool IsNotMidnight()
        {
            return !(IntervalUtils.IsBetween(TimeOfDay, Midnight - 0.1f, Midnight) || IntervalUtils.IsBetween(TimeOfDay, 0, 0.1f));
        }
        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_subsystemWeather = base.Project.FindSubsystem<SubsystemWeather>();
            m_subsystemGameInfo = base.Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
            m_subsystemSeasons = base.Project.FindSubsystem<SubsystemSeasons>(throwOnError: true);

        }

        protected override void Save(ValuesDictionary valuesDictionary)
        {
            base.Save(valuesDictionary);
        }
    }
}

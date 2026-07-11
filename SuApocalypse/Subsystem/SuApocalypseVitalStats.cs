using Engine;
using Engine.Audio;
using Game;
using GameEntitySystem;
using System;
using System.Collections.Generic;
using System.Globalization;
using TemplatesDatabase;

namespace SuApocalypse
{
    // 替换 ComponentVitalStats，只保留体力(Stamina)逻辑
    // Source: ComponentVitalStats.cs — 完整替换
    public class SuApocalypseVitalStats : Component, IUpdateable
    {
        private SubsystemGameInfo m_subsystemGameInfo;
        private SubsystemTime m_subsystemTime;
        private SubsystemAudio m_subsystemAudio;
        private ComponentPlayer m_componentPlayer;
        private Engine.Random m_random = new Engine.Random();
        private Sound m_pantingSound;
        private float m_stamina;
        private float m_lastStamina;
        private float m_densityModifierApplied;

        public float Food
        {
            get => 0.9f;
            private set { }
        }

        public float Stamina
        {
            get => m_stamina;
            private set => m_stamina = MathUtils.Saturate(value);
        }

        public float Sleep
        {
            get => 0.9f;
            private set { }
        }

        public float Temperature
        {
            get => 12f;
            private set { }
        }

        public float Wetness
        {
            get => 0f;
            private set { }
        }

        public float EnvironmentTemperature => 12f;

        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        public bool Eat(int value)
        {
            // 末日模式不允许吃东西
            return false;
        }

        public void MakeSleepy(float sleepValue)
        {
            // 末日模式不困
        }

        public void Update(float dt)
        {
            if (m_componentPlayer.ComponentHealth.Health > 0f)
            {
                UpdateStamina();
                // 强制保持 UI 显示
                m_componentPlayer.ComponentGui.FoodBarWidget.Value = 0.9f;
                m_componentPlayer.ComponentGui.TemperatureBarWidget.Value = 0.5f;
            }
            else
            {
                m_pantingSound.Stop();
            }
        }

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            m_subsystemGameInfo = Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
            m_subsystemTime = Project.FindSubsystem<SubsystemTime>(throwOnError: true);
            m_subsystemAudio = Project.FindSubsystem<SubsystemAudio>(throwOnError: true);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(throwOnError: true);
            m_pantingSound = m_subsystemAudio.CreateSound("Audio/HumanPanting");
            m_pantingSound.IsLooped = true;
            Stamina = valuesDictionary.GetValue<float>("Stamina");
            m_lastStamina = Stamina;
        }

        protected override void Save(ValuesDictionary valuesDictionary, EntityToIdMap entityToIdMap)
        {
            valuesDictionary.SetValue("Food", 0.9f);
            valuesDictionary.SetValue("Stamina", Stamina);
            valuesDictionary.SetValue("Sleep", 0.9f);
            valuesDictionary.SetValue("Temperature", 12f);
            valuesDictionary.SetValue("Wetness", 0f);
        }

        protected override void OnEntityRemoved()
        {
            m_pantingSound.Stop();
        }

        // Source: ComponentVitalStats.UpdateStamina — 简化版只保留体力消耗
        private void UpdateStamina()
        {
            float gameTimeDelta = m_subsystemTime.GameTimeDelta;
            float num = (m_componentPlayer.ComponentLocomotion.LastWalkOrder.HasValue
                ? m_componentPlayer.ComponentLocomotion.LastWalkOrder.Value.Length()
                : 0f);
            float lastJumpOrder = m_componentPlayer.ComponentLocomotion.LastJumpOrder;
            float eyeHeight = m_componentPlayer.ComponentCreatureModel.EyePosition.Y
                - m_componentPlayer.ComponentBody.Position.Y;
            bool inWater = m_componentPlayer.ComponentBody.ImmersionDepth > eyeHeight;
            bool swimming = m_componentPlayer.ComponentBody.ImmersionFactor > 0.33f
                && !m_componentPlayer.ComponentBody.StandingOnValue.HasValue;

            // 体力恢复和消耗 — Source: ComponentVitalStats.UpdateStamina
            float staminaCost = 1f / MathUtils.Max(m_componentPlayer.ComponentLevel.SpeedFactor, 0.75f);
            if (m_componentPlayer.ComponentSickness.IsSick || m_componentPlayer.ComponentFlu.HasFlu)
            {
                staminaCost *= 5f;
            }
            Stamina += gameTimeDelta * 0.07f;
            Stamina -= 0.025f * lastJumpOrder * staminaCost;
            if (swimming || inWater)
            {
                Stamina -= gameTimeDelta * (0.07f + 0.006f * staminaCost + 0.008f * num);
            }
            else
            {
                Stamina -= gameTimeDelta * (0.07f + 0.006f * staminaCost) * num;
            }

            if (!swimming && !inWater && Stamina < 0.33f && m_lastStamina >= 0.33f)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Low stamina, slow down!", Color.White, blinking: true, playNotificationSound: false);
            }
            if ((swimming || inWater) && Stamina < 0.4f && m_lastStamina >= 0.4f)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Low stamina, get ashore!", Color.White, blinking: true, playNotificationSound: true);
            }
            if (Stamina < 0.1f)
            {
                if (swimming || inWater)
                {
                    if (m_subsystemTime.PeriodicGameTimeEvent(5.0, 0.0))
                    {
                        m_componentPlayer.ComponentHealth.Injure(0.05f, null, ignoreInvulnerability: false, "Drowned");
                        m_componentPlayer.ComponentGui.DisplaySmallMessage(
                            "Drowning!", Color.White, blinking: true, playNotificationSound: false);
                    }
                    if (m_random.Float(0f, 1f) < 1f * gameTimeDelta)
                    {
                        m_componentPlayer.ComponentLocomotion.JumpOrder = 1f;
                    }
                }
                else if (m_subsystemTime.PeriodicGameTimeEvent(5.0, 0.0))
                {
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        "Rest up!", Color.White, blinking: true, playNotificationSound: true);
                }
            }

            m_lastStamina = Stamina;

            // 喘气音效 — Source: ComponentVitalStats.UpdateStamina
            float pantFactor = MathUtils.Saturate(2f * (0.5f - Stamina));
            bool isSwimming2 = inWater;
            if (!isSwimming2 && pantFactor > 0f)
            {
                float pitchOffset = (m_componentPlayer.PlayerData.PlayerClass == PlayerClass.Female) ? 0.2f : 0f;
                m_pantingSound.Volume = 1f * SettingsManager.SoundsVolume
                    * MathUtils.Saturate(1f * pantFactor)
                    * MathUtils.Lerp(0.8f, 1f,
                        SimplexNoise.Noise((float)MathUtils.Remainder(3.0 * Time.RealTime + 100.0, 1000.0)));
                m_pantingSound.Pitch = AudioManager.ToEnginePitch(pitchOffset
                    + MathUtils.Lerp(-0.15f, 0.05f, pantFactor)
                    * MathUtils.Lerp(0.8f, 1.2f,
                        SimplexNoise.Noise((float)MathUtils.Remainder(3.0 * Time.RealTime + 200.0, 1000.0))));
                m_pantingSound.Play();
            }
            else
            {
                m_pantingSound.Stop();
            }

            // 密度修正(体力低→减速) — Source: ComponentVitalStats.UpdateStamina
            float densityFactor = MathUtils.Saturate(3f * (0.33f - Stamina));
            if (densityFactor > 0f
                && SimplexNoise.Noise((float)MathUtils.Remainder(Time.RealTime, 1000.0)) < densityFactor)
            {
                ApplyDensityModifier(0.6f);
            }
            else
            {
                ApplyDensityModifier(0f);
            }
        }

        private void ApplyDensityModifier(float modifier)
        {
            float delta = modifier - m_densityModifierApplied;
            if (delta != 0f)
            {
                m_densityModifierApplied = modifier;
                m_componentPlayer.ComponentBody.Density += delta;
            }
        }
    }
}

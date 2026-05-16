using Engine;
using Engine.Content;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuMod;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using TemplatesDatabase;
using Random = Game.Random;

namespace Sumod
{

    public class SumodComponentFlu : Game.ComponentFlu
    {
        private SubsystemGameInfo m_subsystemGameInfo;

        private SubsystemTerrain m_subsystemTerrain;

        private SubsystemTime m_subsystemTime;

        private SubsystemAudio m_subsystemAudio;

        private SubsystemParticles m_subsystemParticles;

        private ComponentPlayer m_componentPlayer;

        private Random m_random = new Random();

        private float m_fluOnset;

        private float m_fluDuration;

        private float m_coughDuration;

        private float m_sneezeDuration;

        private float m_blackoutDuration;

        private float m_blackoutFactor;

        private double m_lastEffectTime = -1000.0;

        private double m_lastCoughTime = -1000.0;

        private double m_lastMessageTime = -1000.0;

        public bool HasFlu => m_fluDuration > 0f;

        public bool IsCoughing => m_coughDuration > 0f;

        public UpdateOrder UpdateOrder => UpdateOrder.Default;


        public new void StartFlu()
        {
        }

        public new void Sneeze()
        {
        }

        public new void Cough()
        {
        }

        public override void Update(float dt)
        {
            
            if (m_fluDuration != 0 || m_fluOnset != 0)
            {
                m_fluDuration = 0f;
                m_fluOnset = 0f;
                m_coughDuration = 0f;
                m_sneezeDuration = 0f;
                return;
            }
        }

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            var cachedImage = ContentCache.Get("Mod/Textures/Cracks1");

            base.Load(valuesDictionary, idToEntityMap);

        }
    }

}
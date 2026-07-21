using Engine;
using Game;

namespace ScMultiplayer
{
    public class SuComponentVitalStats : ComponentVitalStats, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;
        private float m_authoritativeTargetTemperature;
        private int m_temperatureTextureIndex = -1;

        protected override void Load(TemplatesDatabase.ValuesDictionary valuesDictionary,
            GameEntitySystem.IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(true);
            m_authoritativeTargetTemperature = Temperature;
        }

        internal void ApplyAuthoritativeTargetTemperature(float targetTemperature)
        {
            m_authoritativeTargetTemperature = targetTemperature;
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
            {
                base.Update(dt);
                return;
            }

            // Temperature, wetness and all resulting damage remain host-authoritative. The client
            // only refreshes widgets and overlays from the latest authoritative values.
            ComponentGui gui = m_componentPlayer?.ComponentGui;
            if (gui?.FoodBarWidget != null)
                gui.FoodBarWidget.Value = Food;
            if (m_componentPlayer?.ComponentScreenOverlays != null)
            {
                m_componentPlayer.ComponentScreenOverlays.IceFactor =
                    MathUtils.Saturate(1f - Temperature / 6f);
            }
            UpdateTemperatureTexture(gui);
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.UpdateTemperature
        private void UpdateTemperatureTexture(ComponentGui gui)
        {
            if (gui?.TemperatureBarWidget == null) return;
            int textureIndex = m_authoritativeTargetTemperature > 22f ? 6
                : m_authoritativeTargetTemperature > 18f ? 5
                : m_authoritativeTargetTemperature > 14f ? 4
                : m_authoritativeTargetTemperature > 10f ? 3
                : m_authoritativeTargetTemperature > 6f ? 2
                : m_authoritativeTargetTemperature > 2f ? 1
                : 0;
            if (textureIndex == m_temperatureTextureIndex) return;
            m_temperatureTextureIndex = textureIndex;
            gui.TemperatureBarWidget.BarSubtexture = ContentManager.Get<Subtexture>(
                $"Textures/Atlas/Temperature{textureIndex}");
        }
    }
}

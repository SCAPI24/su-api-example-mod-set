using Game;
using System.Collections;
using System.Reflection;

namespace ScMultiplayer
{
    public class SuSubsystemExplosions : SubsystemExplosions
    {
        public override void Update(float dt)
        {
            // Source: Survivalcraft/Game/SubsystemExplosions.cs:SubsystemExplosions.Update
            if (ScMultiplayer.IsHost && ScMultiplayer.client?.IsConnected == true)
            {
                IList queued = ScMultiplayer.ModManager.ModParentField.GetParentField<IList>(
                    this, "m_queuedExplosions", typeof(SubsystemExplosions));
                if (queued != null)
                {
                    foreach (object explosion in queued)
                    {
                        TypeInfo type = explosion.GetType().GetTypeInfo();
                        int x = (int)type.GetField("X").GetValue(explosion);
                        int y = (int)type.GetField("Y").GetValue(explosion);
                        int z = (int)type.GetField("Z").GetValue(explosion);
                        float pressure = (float)type.GetField("Pressure").GetValue(explosion);
                        bool incendiary = (bool)type.GetField("IsIncendiary").GetValue(explosion);
                        bool noSound = (bool)type.GetField("NoExplosionSound").GetValue(explosion);
                        ScMultiplayer.currentInstance?.BroadcastExplosion(
                            x, y, z, pressure, incendiary, noSound);
                    }
                }
            }
            base.Update(dt);
        }
    }
}

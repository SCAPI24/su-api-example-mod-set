using Engine;
using Game;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ScMultiplayer
{
    public class SuSubsystemExplosions : SubsystemExplosions, IUpdateable
    {
        private const double PredictedExplosionMatchLifetime = 2.0;

        private sealed class PredictedExplosion
        {
            public Point3 Point;
            public float Pressure;
            public bool IsIncendiary;
            public bool NoExplosionSound;
            public double Time;
        }

        private readonly List<PredictedExplosion> m_recentPredictedExplosions =
            new List<PredictedExplosion>();
        private readonly HashSet<object> m_skipQueuedExplosionPredictions =
            new HashSet<object>();
        private readonly List<object> m_deferredHostExplosions =
            new List<object>();

        void IUpdateable.Update(float dt)
        {
            if (!ScMultiplayer.IsHost && ScMultiplayer.client?.IsConnected == true)
                RecordQueuedPredictions();
            else
            {
                m_recentPredictedExplosions.Clear();
                m_skipQueuedExplosionPredictions.Clear();
            }
            // Source: Survivalcraft/Game/SubsystemExplosions.cs:SubsystemExplosions.Update
            if (ScMultiplayer.IsHost && ScMultiplayer.client?.IsConnected == true)
            {
                IList queued = ScMultiplayer.ModManager.ModParentField.GetParentField<IList>(
                    this, "m_queuedExplosions", typeof(SubsystemExplosions));
                if (queued != null)
                {
                    DeferUnreadyHostExplosions(queued);
                    foreach (object explosion in queued)
                    {
                        if (!TryReadExplosion(explosion, out Point3 point, out float pressure,
                            out bool incendiary, out bool noSound))
                            continue;
                        ScMultiplayer.currentInstance?.BroadcastExplosion(
                            point.X, point.Y, point.Z, pressure, incendiary, noSound);
                    }
                }
            }
            else
            {
                m_deferredHostExplosions.Clear();
            }
            base.Update(dt);
        }

        // Source: Survivalcraft/Game/SubsystemExplosions.cs:SubsystemExplosions.Update
        // The native explosion walk reads and writes terrain immediately. Keep a queued host
        // explosion out of that walk until the already allocated cells in its envelope are ready;
        // this preserves host authority without allocating or mutating remote chunks.
        private void DeferUnreadyHostExplosions(IList queued)
        {
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null) return;

            for (int i = m_deferredHostExplosions.Count - 1; i >= 0; i--)
            {
                object explosion = m_deferredHostExplosions[i];
                if (!TryReadExplosion(explosion, out Point3 point, out float pressure,
                    out bool _, out bool _) ||
                    !HostTerrainAuthority.IsExplosionEnvelopeReady(terrain, point, pressure))
                    continue;
                queued.Add(explosion);
                m_deferredHostExplosions.RemoveAt(i);
            }

            for (int i = queued.Count - 1; i >= 0; i--)
            {
                object explosion = queued[i];
                if (!TryReadExplosion(explosion, out Point3 point, out float pressure,
                    out bool _, out bool _) ||
                    HostTerrainAuthority.IsExplosionEnvelopeReady(terrain, point, pressure))
                    continue;
                queued.RemoveAt(i);
                m_deferredHostExplosions.Add(explosion);
            }
        }

        // Source: Survivalcraft/Game/SubsystemExplosions.cs:SubsystemExplosions.AddExplosion
        // A client may predict the same fuse one frame before the host broadcast arrives. Keep the
        // local simulation, but do not enqueue a second copy that would play a second sound.
        internal void ApplyNetworkExplosion(Vector3 position, float radius,
            bool incendiary, bool noSound)
        {
            Point3 point = new Point3(position);
            if (ConsumeQueuedPrediction(point, radius, incendiary, noSound) ||
                ConsumeRecentPrediction(point, radius, incendiary, noSound))
                return;

            IList queued = ScMultiplayer.ModManager.ModParentField.GetParentField<IList>(
                this, "m_queuedExplosions", typeof(SubsystemExplosions));
            int count = queued?.Count ?? -1;
            AddExplosion(point.X, point.Y, point.Z, radius, incendiary, noSound);
            if (queued != null && queued.Count > count && queued[count] != null)
                m_skipQueuedExplosionPredictions.Add(queued[count]);
        }

        private void RecordQueuedPredictions()
        {
            IList queued = ScMultiplayer.ModManager.ModParentField.GetParentField<IList>(
                this, "m_queuedExplosions", typeof(SubsystemExplosions));
            if (queued == null) return;
            double now = Time.RealTime;
            m_recentPredictedExplosions.RemoveAll(item =>
                now - item.Time > PredictedExplosionMatchLifetime);
            foreach (object explosion in queued)
            {
                if (m_skipQueuedExplosionPredictions.Remove(explosion) ||
                    !TryReadExplosion(explosion, out Point3 point, out float pressure,
                        out bool incendiary, out bool noSound))
                    continue;
                m_recentPredictedExplosions.Add(new PredictedExplosion
                {
                    Point = point,
                    Pressure = pressure,
                    IsIncendiary = incendiary,
                    NoExplosionSound = noSound,
                    Time = now
                });
            }
        }

        private bool ConsumeQueuedPrediction(Point3 point, float pressure,
            bool incendiary, bool noSound)
        {
            IList queued = ScMultiplayer.ModManager.ModParentField.GetParentField<IList>(
                this, "m_queuedExplosions", typeof(SubsystemExplosions));
            if (queued == null) return false;
            foreach (object explosion in queued)
            {
                if (!TryReadExplosion(explosion, out Point3 queuedPoint,
                        out float queuedPressure, out bool queuedIncendiary,
                        out bool queuedNoSound) ||
                    !MatchesExplosion(point, pressure, incendiary, noSound,
                        queuedPoint, queuedPressure, queuedIncendiary, queuedNoSound))
                    continue;
                m_skipQueuedExplosionPredictions.Add(explosion);
                return true;
            }
            return false;
        }

        private bool ConsumeRecentPrediction(Point3 point, float pressure,
            bool incendiary, bool noSound)
        {
            double now = Time.RealTime;
            for (int i = m_recentPredictedExplosions.Count - 1; i >= 0; i--)
            {
                PredictedExplosion predicted = m_recentPredictedExplosions[i];
                if (now - predicted.Time > PredictedExplosionMatchLifetime)
                {
                    m_recentPredictedExplosions.RemoveAt(i);
                    continue;
                }
                if (!MatchesExplosion(point, pressure, incendiary, noSound,
                    predicted.Point, predicted.Pressure, predicted.IsIncendiary,
                    predicted.NoExplosionSound))
                    continue;
                m_recentPredictedExplosions.RemoveAt(i);
                return true;
            }
            return false;
        }

        private static bool MatchesExplosion(Point3 point, float pressure,
            bool incendiary, bool noSound, Point3 otherPoint, float otherPressure,
            bool otherIncendiary, bool otherNoSound)
        {
            float tolerance = MathUtils.Max(0.01f, MathUtils.Abs(pressure) * 0.01f);
            return point == otherPoint && MathUtils.Abs(pressure - otherPressure) <= tolerance &&
                incendiary == otherIncendiary && noSound == otherNoSound;
        }

        private static bool TryReadExplosion(object explosion, out Point3 point,
            out float pressure, out bool incendiary, out bool noSound)
        {
            point = default;
            pressure = 0f;
            incendiary = false;
            noSound = false;
            if (explosion == null) return false;
            TypeInfo type = explosion.GetType().GetTypeInfo();
            FieldInfo x = type.GetField("X");
            FieldInfo y = type.GetField("Y");
            FieldInfo z = type.GetField("Z");
            FieldInfo p = type.GetField("Pressure");
            FieldInfo i = type.GetField("IsIncendiary");
            FieldInfo n = type.GetField("NoExplosionSound");
            if (x == null || y == null || z == null || p == null || i == null || n == null)
                return false;
            point = new Point3((int)x.GetValue(explosion), (int)y.GetValue(explosion),
                (int)z.GetValue(explosion));
            pressure = (float)p.GetValue(explosion);
            incendiary = (bool)i.GetValue(explosion);
            noSound = (bool)n.GetValue(explosion);
            return true;
        }
    }
}

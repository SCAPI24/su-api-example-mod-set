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
        // 《玩家领地》P6：爆炸前快照"领地内会被波及的格子"，爆炸后写回
        private readonly List<Point3> m_claimBlastCells = new List<Point3>();
        private readonly List<int> m_claimBlastValues = new List<int>();

        void IUpdateable.Update(float dt)
        {
            bool networkActive = ScMultiplayer.currentInstance?.IsNetworkSessionActive(Project) == true;
            bool networkHost = ScMultiplayer.currentInstance?.IsNetworkHost(Project) == true;
            if (networkActive && !networkHost)
                RecordQueuedPredictions();
            else
            {
                m_recentPredictedExplosions.Clear();
                m_skipQueuedExplosionPredictions.Clear();
            }
            // Source: Survivalcraft/Game/SubsystemExplosions.cs:SubsystemExplosions.Update
            bool claimSnapshotTaken = false;
            if (networkHost)
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
                    // 《玩家领地》P6：爆炸不得破坏他人领地内的方块（设计稿 §5）。
                    // 引擎的 SimulateExplosion 是私有的、无法逐格拦；这里在原生破坏**之前**把
                    // 爆炸包络内属于任何领地的格子快照下来，跑完原生破坏后**原样写回**（"否决 + 改回"）。
                    // 引燃由 SuSubsystemFireBlockBehavior 的同帧熄灭兜住。
                    claimSnapshotTaken = SnapshotClaimCellsInBlastEnvelope(queued);
                }
            }
            else
            {
                m_deferredHostExplosions.Clear();
            }
            base.Update(dt);
            if (claimSnapshotTaken)
                RestoreClaimCellsAfterBlast();
        }

        /// <summary>
        /// 把爆炸包络内**属于领地**的格子与它们的当前值记下来；返回是否记到了东西。
        /// 半径口径与 `HostTerrainAuthority.IsExplosionEnvelopeReady` 一致（`ceil(|pressure|)`），再放宽 1 格。
        /// </summary>
        private bool SnapshotClaimCellsInBlastEnvelope(IList queued)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            m_claimBlastCells.Clear();
            m_claimBlastValues.Clear();
            if (mod == null || terrain?.Terrain == null || m_regionClaimsOnHost(mod) == 0 || queued == null)
                return false;
            foreach (object explosion in queued)
            {
                if (!TryReadExplosion(explosion, out Point3 point, out float pressure,
                    out bool _, out bool _))
                    continue;
                int radius = MathUtils.Clamp((int)MathUtils.Ceiling(MathUtils.Abs(pressure)) + 1, 1, 24);
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            var cell = new Point3(point.X + dx, point.Y + dy, point.Z + dz);
                            if (mod.OwnerClaimAt(cell) == null)
                                continue;
                            m_claimBlastCells.Add(cell);
                            m_claimBlastValues.Add(
                                terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z));
                        }
                    }
                }
            }
            return m_claimBlastCells.Count > 0;
        }

        /// <summary>把爆炸波及到的领地格子写回原值（复用主机地形广播，客户端不会看到被炸掉）。</summary>
        private void RestoreClaimCellsAfterBlast()
        {
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain != null)
            {
                for (int i = 0; i < m_claimBlastCells.Count; i++)
                {
                    Point3 cell = m_claimBlastCells[i];
                    if (terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z) !=
                        m_claimBlastValues[i])
                        terrain.ChangeCell(cell.X, cell.Y, cell.Z, m_claimBlastValues[i]);
                }
            }
            if (m_claimBlastCells.Count > 0)
            {
                Log.Information("[ScMP] Region claim blocked an explosion from destroying " +
                    m_claimBlastCells.Count + " cell(s)");
            }
            m_claimBlastCells.Clear();
            m_claimBlastValues.Clear();
        }

        private static int m_regionClaimsOnHost(ScMultiplayer mod) => mod.RegionClaimCount;

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

using System;
using System.Collections.Generic;
using Engine;
using Game;

namespace SuAPIShadows;

// Source: foliage-wind-implementation-plan.md: memory-only lifecycle control.
public static class VegetationMotionControl
{
    private static bool s_enabled = true;
    private static long s_version = 1;
    public static bool Enabled
    {
        get => s_enabled;
        set { if (s_enabled != value) { s_enabled = value; s_version++; } }
    }
    public static long Version => s_version;
}

// Live frames are camera-owned and reused; Clone freezes an independent shadow-task snapshot.
internal sealed class VegetationMotionFrame
{
    public bool Enabled { get; internal set; }
    public long Version { get; internal set; }
    public float GameTime { get; internal set; }
    public double SimulationTime { get; internal set; }
    public Vector4 Wind { get; internal set; }
    public Vector3 ViewPosition { get; internal set; }
    public Vector4[] InfluencePositionRadius { get; } = new Vector4[8];
    public Vector4[] InfluenceDirectionStrength { get; } = new Vector4[8];
    public Vector4[] InfluenceTimingMedium { get; } = new Vector4[8];
    public VegetationMotionFrame Clone()
    {
        var copy = new VegetationMotionFrame { Enabled = Enabled, Version = Version, GameTime = GameTime, SimulationTime = SimulationTime, Wind = Wind, ViewPosition = ViewPosition };
        Array.Copy(InfluencePositionRadius, copy.InfluencePositionRadius, 8);
        Array.Copy(InfluenceDirectionStrength, copy.InfluenceDirectionStrength, 8);
        Array.Copy(InfluenceTimingMedium, copy.InfluenceTimingMedium, 8);
        return copy;
    }
}

// Source: SubsystemBodies.FindBodiesAroundPoint; ComponentBody immersion/velocity;
// SubsystemSeasons.Season/TimeOfSeason; SubsystemWeather.GetPrecipitationShaftInfo.
internal sealed class VegetationMotionState
{
    private sealed class Source
    {
        public ComponentBody Body;
        public Vector3 Position;
        public Vector2 Direction;
        public double SampleTime;
        public double ActiveTime;
        public float Strength;
        public float Height;
        public bool Water;
    }
    private struct Trace
    {
        public Vector3 Position;
        public Vector2 Direction;
        public double Time;
        public float Strength;
        public float Height;
        public bool Water;
        public bool Used;
    }
    private sealed class View
    {
        public Vector3 Position;
        public int LastCaptureFrame;
        public double NextQuery;
        public double NextWeather;
        public double LastWeather;
        public float Rain;
        public float RainTarget;
        public readonly VegetationMotionFrame Frame = new();
    }
    // All time-dependent shader frequencies must be integer multiples of 1/1024 rad/s.
    public const double PhasePeriod = 2.0 * Math.PI * 1024.0;
    private readonly SubsystemTerrain m_terrain;
    private readonly SubsystemTime m_time;
    private readonly SubsystemTimeOfDay m_timeOfDay;
    private readonly SubsystemWeather m_weather;
    private readonly SubsystemGameInfo m_info;
    private readonly SubsystemBodies m_bodies;
    private readonly Dictionary<Camera, View> m_views = new();
    private readonly List<Camera> m_expiredViews = new();
    private readonly Source[] m_sources = new Source[16];
    private readonly Trace[] m_traces = new Trace[16];
    private readonly DynamicArray<ComponentBody> m_candidates = new();
    private readonly int[] m_selected = new int[4];
    private readonly float[] m_scores = new float[4];
    private long m_version = -1;
    private double m_updated = double.NaN;
    private double m_nextEnvironment;
    private double m_now;
    private float m_strength;
    private Vector2 m_direction = Vector2.UnitX;
    private readonly float m_seedAngle;
    public bool Enabled => VegetationMotionControl.Enabled;
    public long Version => VegetationMotionControl.Version;
    public float GameTime => (float)(m_now % PhasePeriod);

    public VegetationMotionState(SubsystemTerrain terrain, SubsystemTime time, SubsystemSeasons seasons,
        SubsystemWeather weather, SubsystemGameInfo info, SubsystemBodies bodies)
    {
        if (seasons == null) throw new ArgumentNullException(nameof(seasons));
        m_timeOfDay = terrain.Project.FindSubsystem<SubsystemTimeOfDay>(true);
        m_terrain = terrain; m_time = time; m_weather = weather; m_info = info; m_bodies = bodies;
        uint seed = unchecked((uint)info.WorldSeed * 1664525u + 1013904223u);
        m_seedAngle = ((seed & 65535) / 65535f * 2f - 1f) * (float)Math.PI / 18f;
    }

    public void Update()
    {
        m_now = m_time.GameTime;
        if (m_version != Version)
        {
            m_version = Version;
            Array.Clear(m_sources, 0, m_sources.Length);
            Array.Clear(m_traces, 0, m_traces.Length);
            foreach (View view in m_views.Values)
            { view.NextQuery = 0; view.NextWeather = 0; view.LastWeather = m_now; view.Rain = 0; view.RainTarget = 0; }
            m_nextEnvironment = 0;
            m_updated = double.NaN;
        }
        if (!Enabled || m_updated == m_now) return;
        m_updated = m_now;
        if (m_now >= m_nextEnvironment)
        {
            m_nextEnvironment = m_now + 0.5;
            float year = m_info.WorldSettings.TimeOfYear;
            // Peak strength is centered on each season; interpolate continuously across boundaries.
            double phase = (year - 0.125) * 4.0;
            int first = (int)Math.Floor(phase);
            float t = (float)(phase - Math.Floor(phase));
            t = t * t * (3f - 2f * t);
            m_strength = SeasonStrength(first) * (1f - t) + SeasonStrength(first + 1) * t;
            // Stable UnitX solar-motion axis, fixed seed +/-10deg, smooth day +/-5deg,
            // season +/-3deg and natural +/-6deg; never follows the current sun position.
            double angle = m_seedAngle + Math.Sin((m_timeOfDay.TimeOfDay - m_timeOfDay.Midday) * 2.0 * Math.PI) * Math.PI / 36.0
                + Math.Sin(year * 2.0 * Math.PI) * Math.PI / 60.0
                + Math.Sin(m_now / 180.0) * Math.PI / 30.0;
            m_direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        }
        for (int i = 0; i < m_traces.Length; i++)
            if (m_traces[i].Used && m_now - m_traces[i].Time >= 1.2) m_traces[i].Used = false;
        for (int i = 0; i < m_sources.Length; i++)
            if (m_sources[i] != null && m_now - m_sources[i].SampleTime > 2.0) m_sources[i] = null;
        m_expiredViews.Clear();
        foreach (KeyValuePair<Camera, View> pair in m_views)
        {
            View view = pair.Value;
            int inactiveFrames = unchecked(Time.FrameIndex - view.LastCaptureFrame);
            if (inactiveFrames > 240) { m_expiredViews.Add(pair.Key); continue; }
            if (inactiveFrames > 2) continue;
            if (m_now >= view.NextQuery) { view.NextQuery = m_now + 0.1; Query(view.Position); }
            if (m_now >= view.NextWeather)
            {
                view.NextWeather = m_now + 0.5;
                int x = Terrain.ToCell(view.Position.X), z = Terrain.ToCell(view.Position.Z);
                if (m_terrain.Terrain.GetChunkAtCell(x, z) != null)
                {
                    PrecipitationShaftInfo info = m_weather.GetPrecipitationShaftInfo(x, z);
                    view.RainTarget = info.Type == PrecipitationType.Rain ? info.Intensity : 0f;
                }
                else view.RainTarget = 0f;
            }
            float dt = (float)Math.Max(0, m_now - view.LastWeather);
            view.Rain += (view.RainTarget - view.Rain) * (1f - (float)Math.Exp(-dt));
            view.LastWeather = m_now;
        }
        foreach (Camera camera in m_expiredViews) m_views.Remove(camera);
    }

    private static float SeasonStrength(int season)
    {
        return ((season % 4 + 4) % 4) switch { 0 => 0.55f, 1 => 0.90f, 2 => 0.80f, _ => 0.70f };
    }

    private void Query(Vector3 center)
    {
        m_candidates.Clear();
        m_bodies.FindBodiesAroundPoint(center.XZ, 18f, m_candidates);
        foreach (ComponentBody body in m_candidates)
        {
            if (body.Entity == null || !body.IsAddedToProject || body.Entity.FindComponent<ComponentCreature>() == null ||
                Vector3.DistanceSquared(body.Position, center) > 20f * 20f) continue;
            int slot = -1;
            for (int i = 0; i < m_sources.Length; i++)
                if (m_sources[i]?.Body == body) { slot = i; break; }
            if (slot < 0)
            {
                for (int i = 0; i < m_sources.Length; i++)
                    if (m_sources[i] == null) { slot = i; break; }
                if (slot < 0) continue;
                m_sources[slot] = new Source { Body = body, Position = body.Position, SampleTime = m_now, ActiveTime = -100 };
                continue;
            }
            Source source = m_sources[slot];
            double dt = m_now - source.SampleTime;
            if (dt < 0.075) continue; // A second camera must not advance an already sampled source.
            Vector3 delta = body.Position - source.Position;
            Vector2 horizontal = delta.XZ;
            float distance = horizontal.Length();
            float speed = body.Velocity.XZ.Length();
            bool moving = dt <= 0.5 && distance >= 0.015f && distance <= 2f &&
                delta.LengthSquared() <= 4f && speed >= 0.1f && speed < 20f && distance / dt < 15f;
            bool water = body.ImmersionFactor > 0.05f && body.ImmersionFluidBlock is WaterBlock;
            // Immersed bodies affect only water; lava/other fluids are not land sources.
            if (body.ImmersionFactor > 0.05f && !water) moving = false;
            if (moving)
            {
                source.Direction = horizontal / distance;
                source.Strength = MathUtils.Clamp(speed / 3f, 0f, 1f) * (water ? 0.025f : 0.07f);
                source.Height = MathUtils.Clamp(body.BoundingBox.Max.Y - body.BoundingBox.Min.Y, 0.3f, 3f) * 0.5f + 0.3f;
                source.Water = water;
                source.ActiveTime = m_now;
                // A trace records the previous real location; the live record moves to the new one.
                AddTrace(source);
            }
            else source.ActiveTime = -100;
            source.Position = body.Position;
            source.SampleTime = m_now;
        }
    }

    private void AddTrace(Source source)
    {
        int slot = 0;
        for (int i = 0; i < m_traces.Length; i++)
        {
            if (!m_traces[i].Used) { slot = i; break; }
            if (m_traces[i].Time < m_traces[slot].Time) slot = i;
        }
        m_traces[slot] = new Trace { Used = true, Position = source.Position, Direction = source.Direction,
            Time = m_now, Strength = source.Strength, Height = source.Height, Water = source.Water };
    }

    public VegetationMotionFrame Snapshot(Camera camera) => Capture(camera).Clone();

    public VegetationMotionFrame Capture(Camera camera)
    {
        if (!m_views.TryGetValue(camera, out View view))
        {
            view = new View { Position = camera.ViewPosition, LastWeather = m_time.GameTime };
            m_views.Add(camera, view);
        }
        view.Position = camera.ViewPosition;
        view.LastCaptureFrame = Time.FrameIndex;
        Update();
        VegetationMotionFrame frame = view.Frame;
        Vector4[] positions = frame.InfluencePositionRadius;
        Vector4[] directions = frame.InfluenceDirectionStrength;
        Vector4[] timing = frame.InfluenceTimingMedium;
        Array.Clear(positions, 0, 8); Array.Clear(directions, 0, 8); Array.Clear(timing, 0, 8);
        if (Enabled)
        {
            Select(camera.ViewPosition, false);
            for (int i = 0; i < 4; i++)
            {
                if (m_selected[i] < 0) continue;
                Source source = m_sources[m_selected[i]];
                Fill(i, source.Position, source.Direction, source.Strength, source.Height, source.Water,
                    (float)(m_now - source.ActiveTime));
            }
            Select(camera.ViewPosition, true);
            for (int i = 0; i < 4; i++)
            {
                if (m_selected[i] < 0) continue;
                Trace trace = m_traces[m_selected[i]];
                Fill(i + 4, trace.Position, trace.Direction, trace.Strength, trace.Height, trace.Water,
                    (float)(m_now - trace.Time));
            }
        }
        Vector4 wind = Enabled ? new Vector4(m_direction.X, m_direction.Y, m_strength * (1f + 0.15f * view.Rain), GameTime) : Vector4.Zero;
        frame.Enabled = Enabled; frame.Version = Version; frame.GameTime = GameTime; frame.SimulationTime = m_now; frame.Wind = wind; frame.ViewPosition = camera.ViewPosition;
        return frame;
        void Fill(int slot, Vector3 position, Vector2 direction, float strength, float height, bool water, float age)
        {
            // Source position uses body-center height for symmetric 3D overlap filtering.
            position.Y += Math.Max(0f, height - 0.3f);
            positions[slot] = new Vector4(position, water ? 2f : 1.5f);
            directions[slot] = new Vector4(direction.X, direction.Y, strength, 0f);
            timing[slot] = new Vector4(age, 1.2f, water ? 1f : 0f, height);
        }
    }

    private void Select(Vector3 center, bool traces)
    {
        for (int i = 0; i < 4; i++) { m_selected[i] = -1; m_scores[i] = float.MaxValue; }
        int count = traces ? m_traces.Length : m_sources.Length;
        for (int i = 0; i < count; i++)
        {
            Vector3 position;
            double age;
            if (traces) { if (!m_traces[i].Used) continue; position = m_traces[i].Position; age = m_now - m_traces[i].Time; }
            else
            {
                Source source = m_sources[i];
                if (source == null || source.Body.Entity == null || !source.Body.IsAddedToProject) continue;
                position = source.Position; age = m_now - source.ActiveTime;
            }
            if (age < 0 || age > (traces ? 1.2 : 0.2)) continue;
            float score = Vector3.DistanceSquared(position, center) + (float)age * 4f;
            if (score > 20f * 20f) continue;
            for (int slot = 0; slot < 4; slot++)
            {
                if (score >= m_scores[slot]) continue;
                for (int j = 3; j > slot; j--) { m_scores[j] = m_scores[j - 1]; m_selected[j] = m_selected[j - 1]; }
                m_scores[slot] = score; m_selected[slot] = i; break;
            }
        }
    }
}

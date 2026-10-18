using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎侧"主机权威 / 客户端副本"服务（**新文件**）：把纯策略层
    /// （<see cref="ClientEditAuthority"/> / <see cref="WorldInfoSnapshot"/> / <see cref="RandomStateTable"/>）
    /// 接到真实引擎上。组合根创建一个实例，注册成
    /// <see cref="IClientEditPort"/> / <see cref="IWorldInfoPort"/> / <see cref="IRandomStatePort"/>。
    ///
    /// 线程：所有方法只在主线程调用（模块 tick / 引擎 Update / 网络帧已经在主线程被泵出）。
    /// 没有静态运行时状态：桥、闸门、计数器都挂在实例上。
    /// </summary>
    public sealed class ClientReplicaEngine : IClientEditPort, IWorldInfoPort, IRandomStatePort
    {
        private sealed class BridgeEditWorld : IEditAuthorityWorld
        {
            private readonly TerrainBridge m_bridge;
            public BridgeEditWorld(TerrainBridge bridge) { m_bridge = bridge; }
            public bool HasContents(ChunkKey key) => m_bridge.HasContents(key);
            public bool TryReadCell(int x, int y, int z, out int contents, out int data) =>
                m_bridge.TryReadCell(x, y, z, out contents, out data);
            public bool TryApplyCell(int x, int y, int z, int contents, int data,
                out int resultContents, out int resultData, out string reason) =>
                m_bridge.TryApplyHostChange(x, y, z, contents, data,
                    out resultContents, out resultData, out reason);
        }

        private readonly TerrainBridge m_bridge;
        private readonly ClientEditAuthority m_authority;
        private readonly int m_sessionSeed;
        private ClientRangeResolver m_rangeResolver;
        private double m_initialGameTime;
        private long m_worldInfoApplied;
        private int m_lastLightningRevision = -1;
        private int m_lastAppliedLightningRevision = -1;
        private string m_lastLightningPosition = string.Empty;
        private long m_weatherFieldFailures;
        private long m_settingsFieldFailures;
        private bool m_weatherFailureLogged;

        private int m_pendingRandomSeed;
        private Dictionary<string, long> m_pendingRandomStates;
        private Project m_randomAppliedProject;
        private long m_randomAppliedFields;
        private long m_randomMissingFields;
        private long m_randomApplyRuns;
        private bool m_randomApplyFailureLogged;

        /// <summary>方块合法性上限 = BlocksManager.Blocks.Length（首次调用时读取，避免构造期引擎未初始化）。</summary>
        private int m_blockCount = -1;

        public ClientReplicaEngine(TerrainBridge bridge)
        {
            m_bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            m_authority = new ClientEditAuthority(new BridgeEditWorld(bridge));
            int seed = unchecked((int)(DateTime.UtcNow.Ticks ^ Environment.TickCount * 2654435761L));
            if (seed == 0) seed = 1;
            m_sessionSeed = seed;
        }

        /// <summary>模块注入：clientId → 该客户端当前兴趣窗口（区块中心 + 半径）。</summary>
        public delegate bool ClientRangeResolver(int clientId, out int centerChunkX,
            out int centerChunkZ, out int radiusChunks);

        public void SetRangeResolver(ClientRangeResolver resolver) => m_rangeResolver = resolver;

        // ================================================================= IClientEditPort

        public event Action<ClientCellEdit> LocalEditCaptured
        {
            add { m_bridge.ClientCellEdited += value; }
            remove { m_bridge.ClientCellEdited -= value; }
        }

        public bool IsClientReplica => m_bridge.ClientReplica;

        public bool TryReadCell(int x, int y, int z, out int contents, out int data) =>
            m_bridge.TryReadCell(x, y, z, out contents, out data);

        public bool IsChunkLoaded(int chunkX, int chunkZ) => m_bridge.IsChunkLoaded(chunkX, chunkZ);

        public bool TryApplyHostEdit(int x, int y, int z, int contents, int data,
            out int resultContents, out int resultData, out string reason) =>
            m_bridge.TryApplyHostChange(x, y, z, contents, data,
                out resultContents, out resultData, out reason);

        public bool TryApplyReplicaValue(int x, int y, int z, int contents, int data) =>
            m_bridge.TryApplyReplicaValue(x, y, z, contents, data);

        public bool TryGetClientRange(int clientId, out int centerChunkX, out int centerChunkZ,
            out int radiusChunks)
        {
            centerChunkX = 0;
            centerChunkZ = 0;
            radiusChunks = 0;
            return m_rangeResolver != null &&
                m_rangeResolver(clientId, out centerChunkX, out centerChunkZ, out radiusChunks);
        }


        public ClientEditDecision HandleEditRequest(int clientId, byte[] payload, bool isPlayerPeer,
            bool hasRange, int centerChunkX, int centerChunkZ, int radiusChunks)
        {
            EnsureBlockCount();
            return m_authority.Handle(clientId, payload, isPlayerPeer, hasRange,
                centerChunkX, centerChunkZ, radiusChunks);
        }

        public ClientEditDecision HandleEditRequest(int clientId,
            SuMultiplayer.Protocol.Authority.CellEditRequest request, bool isPlayerPeer,
            bool hasRange, int centerChunkX, int centerChunkZ, int radiusChunks)
        {
            EnsureBlockCount();
            return m_authority.Handle(clientId, request, isPlayerPeer, hasRange,
                centerChunkX, centerChunkZ, radiusChunks);
        }

        public string EditStats => m_authority.Describe();

        /// <summary>客户端捕获统计（暂存 / 交付 / 重试帧 / 每种丢弃原因）；见 TerrainBridge.ClientCaptureStats。</summary>
        public string CaptureStats => m_bridge.ClientCaptureStats;

        public void ResetEditClient(int clientId) => m_authority.ResetClient(clientId);

        private void EnsureBlockCount()
        {
            if (m_blockCount > 0) return;
            try
            {
                int count = BlocksManager.Blocks == null ? 0 : BlocksManager.Blocks.Length;
                if (count > 0)
                {
                    m_blockCount = count;
                    m_authority.BlockCount = count;
                }
            }
            catch (Exception)
            {
                m_blockCount = -1;
            }
        }

        // ================================================================= IWorldInfoPort

        public long AppliedCount => m_worldInfoApplied;

        public bool TryCaptureHost(out WorldInfoSnapshot snapshot)
        {
            snapshot = null;
            Project project = GameManager.Project;
            if (project == null) return false;
            SubsystemGameInfo info = project.FindSubsystem<SubsystemGameInfo>(false);
            if (info == null) return false;
            var result = new WorldInfoSnapshot();
            result.TotalElapsedGameTime = info.TotalElapsedGameTime;
            if (m_initialGameTime <= 0.0) m_initialGameTime = info.TotalElapsedGameTime;
            result.InitialGameTime = m_initialGameTime;
            SubsystemTimeOfDay tod = project.FindSubsystem<SubsystemTimeOfDay>(false);
            result.TimeOfDayOffset = tod == null ? 0.0 : tod.TimeOfDayOffset;
            result.TimeOfDayMode = (int)info.WorldSettings.TimeOfDayMode;
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            result.TimeAccelerated = time != null && time.GameTimeFactor > 1.0001f;
            result.TimeFactor = time == null ? 1f : time.GameTimeFactor;
            CaptureWeather(project, result);
            CaptureSettings(info.WorldSettings, result);
            CaptureLightning(project, result);
            snapshot = result;
            return true;
        }

        private static void CaptureWeather(Project project, WorldInfoSnapshot result)
        {
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather == null) return;
            result.PrecipitationStarted = weather.IsPrecipitationStarted;
            result.PrecipitationIntensity = weather.PrecipitationIntensity;
            result.FogStarted = weather.IsFogStarted;
            result.FogProgress = weather.FogProgress;
            result.FogIntensity = weather.FogIntensity;
            result.FogSeed = weather.FogSeed;
        }

        private void CaptureLightning(Project project, WorldInfoSnapshot result)
        {
            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(false);
            if (sky == null) return;
            try
            {
                object raw = Member(sky, "m_lightningStrikePosition", typeof(SubsystemSky));
                if (raw is not Vector3 position) return;
                object rawTime = Member(sky, "m_lastLightningStrikeTime", typeof(SubsystemSky));
                double strikeTime = rawTime is double time ? time : 0.0;
                int x = Game.Terrain.ToCell(position.X);
                int y = Game.Terrain.ToCell(position.Y);
                int z = Game.Terrain.ToCell(position.Z);
                string key = x.ToString(CultureInfo.InvariantCulture) + "," +
                    y.ToString(CultureInfo.InvariantCulture) + "," +
                    z.ToString(CultureInfo.InvariantCulture) + "|" +
                    strikeTime.ToString("F3", CultureInfo.InvariantCulture);
                result.HasLightning = true;
                result.LightningX = x;
                result.LightningY = y;
                result.LightningZ = z;
                if (!string.Equals(key, m_lastLightningPosition, StringComparison.Ordinal))
                {
                    m_lastLightningPosition = key;
                    m_lastLightningRevision++;
                }
                result.LightningRevision = m_lastLightningRevision;
            }
            catch (Exception)
            {
                // 引擎版本差异：宁可没有闪电字段，也不猜位置。
            }
        }

        private static readonly string[] s_settings =
        {
            "TimeOfDayMode", "EnvironmentBehaviorMode", "GameMode", "AreSeasonsChanging",
            "YearDays", "TimeOfYear", "AreWeatherEffectsEnabled", "StartingPositionMode",
            "IsAdventureRespawnAllowed", "AreAdventureSurvivalMechanicsEnabled",
            "AreSupernaturalCreaturesEnabled", "IsFriendlyFireEnabled",
            "TemperatureOffset", "HumidityOffset", "SeaLevelOffset"
        };

        private static void CaptureSettings(WorldSettings settings, WorldInfoSnapshot result)
        {
            if (settings == null) return;
            for (int i = 0; i < s_settings.Length; i++)
            {
                System.Reflection.FieldInfo field = typeof(WorldSettings).GetField(s_settings[i]);
                if (field == null) continue;
                object value = field.GetValue(settings);
                result.SetSetting(field.Name, SerializeSetting(value));
            }
        }

        private static string SerializeSetting(object value)
        {
            if (value == null) return string.Empty;
            if (value is bool flag) return flag ? "1" : "0";
            if (value is Enum) return Convert.ToInt64(value, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public void ApplyClient(WorldInfoSnapshot snapshot)
        {
            if (snapshot == null) return;
            Project project = GameManager.Project;
            if (project == null) return;
            SubsystemGameInfo info = project.FindSubsystem<SubsystemGameInfo>(false);
            if (info == null) return;
            // ① 时钟：漂移超过阈值才硬对齐（2 Hz 覆盖若每次都硬设会把本地时钟打成抖动）。
            if (WorldClockPolicy.NeedsSnap(snapshot.TotalElapsedGameTime,
                info.TotalElapsedGameTime, 0.35))
            {
                SetMember(info, "TotalElapsedGameTime", snapshot.TotalElapsedGameTime,
                    typeof(SubsystemGameInfo));
            }
            SubsystemTimeOfDay tod = project.FindSubsystem<SubsystemTimeOfDay>(false);
            if (tod != null) tod.TimeOfDayOffset = snapshot.TimeOfDayOffset;
            // ② 时间模式 + WorldSettings。
            if (snapshot.TimeOfDayMode >= 0 && snapshot.TimeOfDayMode <= 4)
                info.WorldSettings.TimeOfDayMode = (TimeOfDayMode)snapshot.TimeOfDayMode;
            ApplySettings(info.WorldSettings, snapshot);
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            if (time != null)
            {
                // 时间加速：照抄主机的 GameTimeFactor（世界时钟的绝对值另由 TotalElapsedGameTime 对齐）。
                float factor = snapshot.TimeFactor > 0f ? snapshot.TimeFactor : 1f;
                if (!SetMember(time, "GameTimeFactor", factor, typeof(SubsystemTime)))
                    m_weatherFieldFailures++;
            }
            // ③ 天气：整体覆盖（客户端不跑 SubsystemWeather.Update）。
            ApplyWeather(project, snapshot, info.TotalElapsedGameTime);
            // ④ 闪电表现（只写视觉字段，不调用 MakeLightningStrike：那个会伤血 / 点燃）。
            ApplyLightning(project, snapshot);
            m_worldInfoApplied++;
        }

        private void ApplySettings(WorldSettings settings, WorldInfoSnapshot snapshot)
        {
            if (settings == null) return;
            for (int i = 0; i < snapshot.WorldSettings.Count; i++)
            {
                WorldSettingEntry entry = snapshot.WorldSettings[i];
                if (Array.IndexOf(s_settings, entry.Key) < 0) continue;
                System.Reflection.FieldInfo field = typeof(WorldSettings).GetField(entry.Key);
                if (field == null) continue;
                try
                {
                    if (field.FieldType == typeof(bool))
                        field.SetValue(settings, entry.Value == "1");
                    else if (field.FieldType.IsEnum)
                        field.SetValue(settings, Enum.ToObject(field.FieldType,
                            long.Parse(entry.Value, CultureInfo.InvariantCulture)));
                    else if (field.FieldType == typeof(float))
                        field.SetValue(settings, float.Parse(entry.Value,
                            NumberStyles.Float, CultureInfo.InvariantCulture));
                    else if (field.FieldType == typeof(double))
                        field.SetValue(settings, double.Parse(entry.Value,
                            NumberStyles.Float, CultureInfo.InvariantCulture));
                    else if (field.FieldType == typeof(int))
                        field.SetValue(settings, int.Parse(entry.Value,
                            NumberStyles.Integer, CultureInfo.InvariantCulture));
                }
                catch (Exception)
                {
                    // 单字段解析失败：本端保持旧值，但**计数**，由状态行给出可定位的证据。
                    m_settingsFieldFailures++;
                }
            }
        }

        private void ApplyWeather(Project project, WorldInfoSnapshot snapshot, double totalElapsed)
        {
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather == null) return;
            double now = totalElapsed;
            // 时间窗：客户端不推进 base.Update，IsXStarted 只由时间窗决定 ⇒ 用窗口把主机的 bool 复现。
            double precipitationStart = snapshot.PrecipitationStarted ? now - 1.0 : double.PositiveInfinity;
            double precipitationEnd = snapshot.PrecipitationStarted ? double.PositiveInfinity : now - 1.0;
            double fogStart = snapshot.FogStarted ? now - 1.0 : double.PositiveInfinity;
            double fogEnd = snapshot.FogStarted ? double.PositiveInfinity : now - 1.0;
            int failures = 0;
            if (!SetMember(weather, "m_precipitationStartTime", precipitationStart, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "m_precipitationEndTime", precipitationEnd, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "m_precipitationRampTime", 1f, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "PrecipitationIntensity", snapshot.PrecipitationIntensity, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "m_fogStartTime", fogStart, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "m_fogEndTime", fogEnd, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "m_fogRampTime", 1f, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "FogProgress", snapshot.FogProgress, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "FogIntensity", snapshot.FogIntensity, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "FogSeed", snapshot.FogSeed, typeof(SubsystemWeather))) failures++;
            if (!SetMember(weather, "m_lightningIntensity", 0f, typeof(SubsystemWeather))) failures++;
            m_weatherFieldFailures += failures;
            if (failures > 0 && !m_weatherFailureLogged)
            {
                m_weatherFailureLogged = true;
                Log.Warning("[SuMP] 客户端应用世界信息时有 " + failures +
                    " 个天气字段写入失败（引擎字段名变化？）→ 已计数，天气仍以主机下发为准");
            }
        }

        private void ApplyLightning(Project project, WorldInfoSnapshot snapshot)
        {
            if (!snapshot.HasLightning) return;
            // 同一次闪电事件只应用一次：否则 2 Hz 快照会把亮度反复重置成 1（闪电一直不灭）。
            if (snapshot.LightningRevision == m_lastAppliedLightningRevision) return;
            string key = snapshot.LightningX.ToString(CultureInfo.InvariantCulture) + "," +
                snapshot.LightningY.ToString(CultureInfo.InvariantCulture) + "," +
                snapshot.LightningZ.ToString(CultureInfo.InvariantCulture);
            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(false);
            if (sky == null) return;
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            double now = time == null ? Time.RealTime : time.GameTime;
            if (!SetMember(sky, "m_lightningStrikePosition",
                    new Vector3(snapshot.LightningX, snapshot.LightningY, snapshot.LightningZ),
                    typeof(SubsystemSky)) ||
                !SetMember(sky, "m_lightningStrikeBrightness", 1f, typeof(SubsystemSky)) ||
                !SetMember(sky, "m_lastLightningStrikeTime", now, typeof(SubsystemSky)))
            {
                return;
            }
            m_lastAppliedLightningRevision = snapshot.LightningRevision;
            if (!string.Equals(key, m_lastLightningPosition, StringComparison.Ordinal))
                m_lastLightningPosition = key;
        }

        private static object Member(object owner, string name, Type declaringType)
        {
            try
            {
                return Game.Program.ModManager?.ModParentField?
                    .GetParentField<object>(owner, name, declaringType);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool SetMember(object owner, string name, object value, Type declaringType)
        {
            try
            {
                Game.Program.ModManager?.ModParentField?
                    .ModifyParentField(owner, name, value, declaringType);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ================================================================= IRandomStatePort

        public int SessionSeed => m_sessionSeed;

        public Dictionary<string, long> CaptureHostStates()
        {
            Project project = GameManager.Project;
            if (project == null) return new Dictionary<string, long>(StringComparer.Ordinal);
            return RandomStateTable.Capture(Subsystems(project));
        }

        public void ApplyClientStates(int seed, IReadOnlyDictionary<string, long> states)
        {
            if (seed == 0 || states == null || states.Count == 0)
            {
                if (!m_randomApplyFailureLogged)
                {
                    m_randomApplyFailureLogged = true;
                    Log.Warning("[SuMP] 主机下发的随机数状态为空（seed=" + seed + "，states=" +
                        (states == null ? -1 : states.Count) + "）→ 客户端不应用（不猜种子）");
                }
                return;
            }
            m_pendingRandomSeed = seed;
            m_pendingRandomStates = new Dictionary<string, long>(states, StringComparer.Ordinal);
            m_randomAppliedProject = null;
        }

        public void PumpApply()
        {
            if (m_pendingRandomStates == null || m_pendingRandomStates.Count == 0) return;
            Project project = GameManager.Project;
            if (project == null || ReferenceEquals(project, m_randomAppliedProject)) return;
            int applied = RandomStateTable.Apply(Subsystems(project), m_pendingRandomSeed,
                m_pendingRandomStates, out int missing);
            m_randomAppliedProject = project;
            m_randomAppliedFields = applied;
            m_randomMissingFields = missing;
            m_randomApplyRuns++;
            Log.Information("[SuMP] 随机数状态已应用（每个 Project 一次）：seed=" +
                m_pendingRandomSeed + " · 字段 " + applied + " · 主机表 " +
                m_pendingRandomStates.Count + " · 缺失/只读 " + missing +
                " · 第 " + m_randomApplyRuns + " 次");
        }

        public bool AppliedForCurrentProject =>
            m_pendingRandomStates != null && m_pendingRandomStates.Count > 0 &&
            ReferenceEquals(GameManager.Project, m_randomAppliedProject);

        public long AppliedFields => m_randomAppliedFields;
        public long MissingFields => m_randomMissingFields;

        private static List<object> Subsystems(Project project)
        {
            var list = new List<object>(64);
            foreach (Subsystem subsystem in project.Subsystems) list.Add(subsystem);
            return list;
        }

        public string DescribeWorldInfo() =>
            "applied=" + m_worldInfoApplied.ToString(CultureInfo.InvariantCulture) +
            " lightningRevisions=" + m_lastLightningRevision.ToString(CultureInfo.InvariantCulture) +
            " rngAppliedFields=" + m_randomAppliedFields.ToString(CultureInfo.InvariantCulture) +
            " rngMissing=" + m_randomMissingFields.ToString(CultureInfo.InvariantCulture);
    }
}

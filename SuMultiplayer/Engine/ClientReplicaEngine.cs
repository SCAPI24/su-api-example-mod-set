using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
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
            public bool TryApplyCell(int clientId, int x, int y, int z, int contents, int data,
                int toolLevel, bool hasToolLevel,
                out EditApplyOutcome outcome, out string reason) =>
                m_bridge.TryApplyHostChange(clientId, x, y, z, contents, data, toolLevel,
                    hasToolLevel, out outcome, out reason);
        }

        private readonly TerrainBridge m_bridge;
        private readonly ClientEditAuthority m_authority;
        private readonly InteractionReachGate m_reachGate;
        private readonly int m_sessionSeed;
        private ClientRangeResolver m_rangeResolver;
        private double m_initialGameTime;
        private long m_worldInfoApplied;
        private long m_lastHostWeatherLogMs;
        private int m_lastLightningRevision = -1;
        private int m_lastAppliedLightningRevision = -1;
        private string m_lastLightningPosition = string.Empty;
        private long m_weatherFieldFailures;
        private long m_settingsFieldFailures;
        private bool m_weatherFailureLogged;
        /// <summary>天气对齐的"当前项目"与上次应用的起止状态（2026-10-26 翻转驱动修复用）。</summary>
        private Project m_weatherProject;
        private bool m_lastPrecipStarted;
        private bool m_lastFogStarted;

        private int m_pendingRandomSeed;
        private Dictionary<string, long> m_pendingRandomStates;
        private Project m_randomAppliedProject;
        private long m_randomAppliedFields;
        private long m_randomMissingFields;
        private long m_randomApplyRuns;
        private bool m_randomApplyFailureLogged;

        /// <summary>方块合法性上限 = BlocksManager.Blocks.Length（首次调用时读取，避免构造期引擎未初始化）。</summary>
        private int m_blockCount = -1;

        public ClientReplicaEngine(TerrainBridge bridge, InteractionReachGate reachGate)
        {
            m_bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            m_reachGate = reachGate ?? throw new ArgumentNullException(nameof(reachGate));
            m_authority = new ClientEditAuthority(new BridgeEditWorld(bridge));
            m_authority.ReachGate = m_reachGate;
            int seed = unchecked((int)(DateTime.UtcNow.Ticks ^ Environment.TickCount * 2654435761L));
            if (seed == 0) seed = 1;
            m_sessionSeed = seed;
        }

        /// <summary>客户端本机有效交互距离（引擎 reach），由编辑请求携带给主机。</summary>
        public float LocalReach => EngineReach.LocalReach();

        /// <summary>
        /// 组合根注入：clientId → 主机侧联机化身的**权威身体位置**。
        /// 编辑请求的玩家相对距离校验用它（读不到 = 明确拒绝，不猜区块中心）。
        /// </summary>
        public void SetPlayerPositionResolver(ClientEditAuthority.ClientEditPositionResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            m_authority.PlayerPositions = resolver;
        }

        /// <summary>模块注入：clientId → 该客户端当前兴趣窗口（区块中心 + 半径）。</summary>
        public delegate bool ClientRangeResolver(int clientId, out int centerChunkX,
            out int centerChunkZ, out int radiusChunks);

        public void SetRangeResolver(ClientRangeResolver resolver) => m_rangeResolver = resolver;

        /// <summary>
        /// 组合根注入：家具值校验（contents == 227 时 data 里的设计索引必须在主机设计表）。
        /// 家具同步模块不依赖 Game，所以由组合根把 Game 查询包成这个委托。
        /// </summary>
        public void SetFurnitureValueValidator(Func<int, int, bool> validator) =>
            m_authority.FurnitureValueValidator = validator;

        /// <summary>
        /// 组合根注入：clientId → 该客户端手持物的 <c>Block.ToolLevel</c>（读主机侧联机化身的权威背包）。
        /// 掉落口径与 <c>ComponentMiner.Dig</c> 同源（它把手持方块 ToolLevel 传给 DestroyCell）。
        /// </summary>
        public void SetToolLevelResolver(ClientEditAuthority.ClientToolLevelResolver resolver) =>
            m_authority.ToolLevels = resolver;

        /// <summary>方块销毁掉落账本（状态行 / 自检可读；只读快照）。</summary>
        public EditDropLedger DropLedger => m_authority.Drops;

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

        public bool TryApplyHostEdit(int clientId, int x, int y, int z, int contents, int data,
            int toolLevel, bool hasToolLevel,
            out EditApplyOutcome outcome, out string reason) =>
            m_bridge.TryApplyHostChange(clientId, x, y, z, contents, data, toolLevel,
                hasToolLevel, out outcome, out reason);

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
            m_authority.HostWorldCreative = EngineReach.IsCreative(GameManager.Project);
            ClientEditDecision decision = m_authority.Handle(clientId, payload, isPlayerPeer,
                hasRange, centerChunkX, centerChunkZ, radiusChunks);
            MaybeLogDropSummary();
            return decision;
        }

        public ClientEditDecision HandleEditRequest(int clientId,
            SuMultiplayer.Protocol.Authority.CellEditRequest request, bool isPlayerPeer,
            bool hasRange, int centerChunkX, int centerChunkZ, int radiusChunks)
        {
            EnsureBlockCount();
            m_authority.HostWorldCreative = EngineReach.IsCreative(GameManager.Project);
            ClientEditDecision decision = m_authority.Handle(clientId, request, isPlayerPeer,
                hasRange, centerChunkX, centerChunkZ, radiusChunks);
            MaybeLogDropSummary();
            return decision;
        }

        /// <summary>
        /// 方块销毁掉落的限流汇总（10s 一行，计数没变化就不写）：spawned / skipped[reason] / mismatch。
        /// 热路径只做一次布尔判断。
        /// </summary>
        private void MaybeLogDropSummary()
        {
            if (m_authority.Drops.TryTakeSummary(Environment.TickCount64, out string summary))
                ModLog.VerboseInfo(summary);
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

        private void CaptureWeather(Project project, WorldInfoSnapshot result)
        {
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather == null) return;
            result.PrecipitationStarted = weather.IsPrecipitationStarted;
            result.PrecipitationIntensity = weather.PrecipitationIntensity;
            result.FogStarted = weather.IsFogStarted;
            result.FogProgress = weather.FogProgress;
            result.FogIntensity = weather.FogIntensity;
            result.FogSeed = weather.FogSeed;
            long nowLog = Environment.TickCount64;
            if (nowLog - m_lastHostWeatherLogMs >= 10000)
            {
                m_lastHostWeatherLogMs = nowLog;
                ModLog.VerboseInfo("[SuMP][fog] 主机雾状态：intensity=" +
                    weather.FogIntensity.ToString("F2") + " progress=" +
                    weather.FogProgress.ToString("F3") + " started=" + weather.IsFogStarted +
                    " seed=" + weather.FogSeed);
            }
            // 2026-10-26：把主机的**实际** ramp 带下来（自然雾几十秒级 ≠ 手动雾 3s）。
            // 客户端翻转时写主机值，浓度推进速率与主机一致（此前硬编码 3s = 漂移根因）。
            result.FogRampTime = Member(weather, "m_fogRampTime", typeof(SubsystemWeather)) is float fogRamp
                ? MathF.Max(0.1f, fogRamp) : 3f;
            result.PrecipitationRampTime = Member(weather, "m_precipitationRampTime",
                typeof(SubsystemWeather)) is float precipRamp ? MathF.Max(0.1f, precipRamp) : 1f;
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
            // 2026-10-26 修复"客户端雨/雾过渡一顿一顿"：不再每 2Hz 硬写强度 / 进度（500ms 一阶，
            // 主机端却是每帧连续 ramp）。改为**状态翻转驱动**：翻转时写入与引擎 Manual 同款的
            // 窗口（降水 ramp 1s、雾 ramp 3s，Source: SubsystemWeather.cs:159-181），连续推进交给
            // SuSubsystemWeather 客户端副本的引擎同款 ramp 数学（UpdateClientVisualRamp）。
            // 首次（换世界 / 加入）仍直接对齐一次强度值，中途加入不用从 0 慢慢爬。
            bool first = !ReferenceEquals(m_weatherProject, project);
            if (first)
            {
                m_weatherProject = project;
                ModLog.VerboseInfo("[SuMP][fog] 客户端首次天气快照：started=" + snapshot.FogStarted +
                    " intensity=" + snapshot.FogIntensity.ToString("F2") + " progress=" +
                    snapshot.FogProgress.ToString("F3") + " ramp=" +
                    snapshot.FogRampTime.ToString("F1") + " 本地 intensity=" +
                    weather.FogIntensity.ToString("F2"));
            }
            bool precip = snapshot.PrecipitationStarted;
            bool fog = snapshot.FogStarted;
            if (!first && precip == m_lastPrecipStarted && fog == m_lastFogStarted)
            {
                // 状态没翻转时本地 ramp 连续推进；但网络丢包 / 客户端中途加入若把雾强度
                // 留在旧值，会表现为“只有 C1 雾很浓、其他端能看很远”。这里做低敏感度
                // 对账：只有明显大于过渡误差才纠正，避免把 2Hz 快照重新变成每帧硬写。
                ReconcileWeatherDrift(weather, snapshot, fog);
                return;
            }
            double now = totalElapsed;
            int failures = 0;
            if (first || precip != m_lastPrecipStarted)
            {
                // 2026-10-26：写主机的实际 ramp（0 = 旧主机，退回旧硬编码值）。
                float precipRamp = snapshot.PrecipitationRampTime > 0f
                    ? snapshot.PrecipitationRampTime : 1f;
                if (!SetMember(weather, "m_precipitationRampTime", precipRamp, typeof(SubsystemWeather))) failures++;
                if (precip)
                {
                    if (!SetMember(weather, "m_precipitationStartTime", now, typeof(SubsystemWeather))) failures++;
                    if (!SetMember(weather, "m_precipitationEndTime", double.PositiveInfinity, typeof(SubsystemWeather))) failures++;
                }
                else if (!SetMember(weather, "m_precipitationEndTime", now + 1.0, typeof(SubsystemWeather))) failures++;
                if (first && !SetMember(weather, "PrecipitationIntensity", snapshot.PrecipitationIntensity, typeof(SubsystemWeather))) failures++;
            }
            if (first || fog != m_lastFogStarted)
            {
                // 2026-10-26：写主机的实际 ramp（自然雾几十秒 ≠ 手动 3s；浓度漂移修复）。
                float fogRamp = snapshot.FogRampTime > 0f ? snapshot.FogRampTime : 3f;
                if (!SetMember(weather, "m_fogRampTime", fogRamp, typeof(SubsystemWeather))) failures++;
                if (fog)
                {
                    if (!SetMember(weather, "m_fogStartTime", now, typeof(SubsystemWeather))) failures++;
                    if (!SetMember(weather, "m_fogEndTime", double.PositiveInfinity, typeof(SubsystemWeather))) failures++;
                    // 客户端不能用自己的 fogStart 重算 FogSeed：那会让每端的雾密度随机因子不同。
                    if (!SetMember(weather, "FogSeed", snapshot.FogSeed, typeof(SubsystemWeather)))
                        failures++;
                }
                else
                {
                    if (!SetMember(weather, "m_fogEndTime", now + 3.0, typeof(SubsystemWeather)))
                        failures++;
                    // 状态已明确结束：本地强度必须立刻归零（客户端不跑 base.Update，
                    // 不能指望本地 ramp 一定还能把旧浓度降下来）。
                    if (!SetMember(weather, "FogIntensity", 0f, typeof(SubsystemWeather)))
                        failures++;
                }
                if (first)
                {
                    if (!SetMember(weather, "FogIntensity", snapshot.FogIntensity, typeof(SubsystemWeather))) failures++;
                    if (!SetMember(weather, "FogProgress", snapshot.FogProgress, typeof(SubsystemWeather))) failures++;
                    if (!SetMember(weather, "FogSeed", snapshot.FogSeed, typeof(SubsystemWeather))) failures++;
                }
            }
            if (first && !SetMember(weather, "m_lightningIntensity", 0f, typeof(SubsystemWeather))) failures++;
            m_lastPrecipStarted = precip;
            m_lastFogStarted = fog;
            m_weatherFieldFailures += failures;
            if (failures > 0 && !m_weatherFailureLogged)
            {
                m_weatherFailureLogged = true;
                Log.Warning("[SuMP] 客户端应用世界信息时有 " + failures +
                    " 个天气字段写入失败（引擎字段名变化？）→ 已计数，天气仍以主机下发为准");
            }
        }

        /// <summary>
        /// 状态未翻转时的雾/降水对账：客户端本地是连续 ramp，正常情况下与主机快照只差
        /// 一点网络延迟；差异超过阈值才纠正。专门兜住 C1 中途加入 / 丢包导致雾强度长期停留在
        /// 旧值（实机：C1 只能看到一棵树，其他端能看很远）。
        /// </summary>
        private void ReconcileWeatherDrift(SubsystemWeather weather, WorldInfoSnapshot snapshot,
            bool fog)
        {
            int failures = 0;
            if (!fog)
            {
                if (weather.FogIntensity > 0.01f)
                {
                    float local = weather.FogIntensity;
                    if (!SetMember(weather, "FogIntensity", 0f, typeof(SubsystemWeather)))
                        failures++;
                    else
                        ModLog.VerboseInfo("[SuMP][fog] 客户端雾对账：主机 fog=off，本地 " +
                            local.ToString("F2") + " → 0");
                }
            }
            else
            {
                float localFog = weather.FogIntensity;
                float localProgress = weather.FogProgress;
                if (MathF.Abs(localFog - snapshot.FogIntensity) > 0.01f)
                {
                    if (!SetMember(weather, "FogIntensity", snapshot.FogIntensity,
                            typeof(SubsystemWeather))) failures++;
                    else
                        ModLog.VerboseInfo("[SuMP][fog] 客户端雾对账：本地 " + localFog.ToString("F2") +
                            " → 主机 " + snapshot.FogIntensity.ToString("F2"));
                }
                if (weather.FogSeed != snapshot.FogSeed &&
                    !SetMember(weather, "FogSeed", snapshot.FogSeed, typeof(SubsystemWeather)))
                    failures++;
                if (MathF.Abs(localProgress - snapshot.FogProgress) > 0.005f)
                {
                    if (!SetMember(weather, "FogProgress", snapshot.FogProgress,
                            typeof(SubsystemWeather))) failures++;
                    else
                        ModLog.VerboseInfo("[SuMP][fog] 客户端雾进度对账：本地 " +
                            localProgress.ToString("F3") + " → 主机 " +
                            snapshot.FogProgress.ToString("F3"));
                }
            }
            if (MathF.Abs(weather.PrecipitationIntensity - snapshot.PrecipitationIntensity) > 0.05f &&
                !SetMember(weather, "PrecipitationIntensity", snapshot.PrecipitationIntensity,
                    typeof(SubsystemWeather)))
                failures++;
            if (failures > 0)
            {
                m_weatherFieldFailures += failures;
                if (!m_weatherFailureLogged)
                {
                    m_weatherFailureLogged = true;
                    Log.Warning("[SuMP] 客户端雾/降水对账有 " + failures +
                        " 个字段写入失败（引擎字段名变化？）→ 已计数");
                }
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
            ModLog.VerboseInfo("[SuMP] 随机数状态已应用（每个 Project 一次）：seed=" +
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

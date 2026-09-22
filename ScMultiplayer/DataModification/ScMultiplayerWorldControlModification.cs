using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScMultiplayer
{
    /// <summary>
    /// 通用「世界控制」数据修改（自定义 operation，与 `ScMP.Data.WorldSettings` / `ScMP.Data.Cells` 同级）。
    ///
    /// 为什么需要它：`WorldSettings` 里只有天气**总开关** `AreWeatherEffectsEnabled`，没有"开始降雨 /
    /// 起雾 / 闪电"这类运行时状态；而引擎原生那几个按钮（`ComponentGui.Update` 里的 Precipitation /
    /// Fog / Lightning / TimeOfDay）要求 `GameMode.Creative` 或 `WorldControl` 能力，且完全绕过 DM 审批。
    /// 所以这里把"运行时天气 + 时间点"做成**主机落地的通用数据修改**：主机审批后自己改
    /// `SubsystemWeather` / `SubsystemTimeOfDay`，随后由主机既有的世界信息广播（2Hz）分发给所有客户端。
    ///
    /// 载荷沿用 `WorldSettingsDataCodec` 的纯文本 `字段名\t值`（不用 JSON：本 mod 会混淆改名）。
    /// </summary>
    internal static class WorldControlDataOperation
    {
        /// <summary>自定义 operation 名（与 `ScMP.Data.WorldSettings` / `ScMP.Data.Cells` 同一家族）。</summary>
        public const string Name = "ScMP.Data.WorldControl";

        /// <summary>降雨：`on` / `off` / `toggle`。</summary>
        public const string PrecipitationField = "Precipitation";

        /// <summary>雾气：`on` / `off` / `toggle`。</summary>
        public const string FogField = "Fog";

        /// <summary>闪电：`strike`（立即在请求者眼睛前方劈一次）。</summary>
        public const string LightningField = "Lightning";

        /// <summary>时间点：`dawn` / `noon` / `dusk` / `midnight`（引擎的中点是 Middawn/Midday/Middusk/Midnight）。</summary>
        public const string TimePointField = "TimePoint";

        /// <summary>精确时间：0..1，与引擎 `SubsystemTimeOfDay.TimeOfDay` 同口径。</summary>
        public const string TimeExactField = "TimeExact";

        public static bool IsWorldControlOperation(string operation) =>
            string.Equals(operation, Name, StringComparison.Ordinal);
    }

    public partial class ScMultiplayer
    {
        private const double MaximumRequestedTimeExact = 1.0;

        // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerTrustedDataModificationClients.cs:
        // ScMultiplayer.ApplyHostInternalDataModification
        // 「运行时天气 + 时间点」的主机落地：改的是子系统运行时状态，不是 `WorldSettings`。
        private DataModificationApplyResult ApplyHostWorldControlModification(
            DataModificationApplyContext context)
        {
            if (!IsHost || GameManager.Project == null)
                return DataModificationApplyResult.Reject("An active authoritative host is required.");
            if (context.Channel != DataModificationChannel.Fast || context.ChunkCount != 1 ||
                context.ChunkIndex != 0 || !context.IsFinalChunk)
            {
                return DataModificationApplyResult.Reject(
                    "World control requires one fast single-chunk request.");
            }
            if (!WorldSettingsDataCodec.TryDecode(context.Payload, out var entries,
                out string decodeError))
                return DataModificationApplyResult.Reject(decodeError);

            SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(false);
            SubsystemWeather weather = GameManager.Project.FindSubsystem<SubsystemWeather>(false);
            SubsystemTimeOfDay timeOfDay =
                GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(false);
            if (gameInfo == null || weather == null)
                return DataModificationApplyResult.Reject("No world is loaded on the host.");

            var applied = new List<string>();
            bool weatherTouched = false;
            foreach (KeyValuePair<string, string> entry in entries)
            {
                string name = (entry.Key ?? string.Empty).Trim();
                string value = (entry.Value ?? string.Empty).Trim();
                if (string.Equals(name, WorldControlDataOperation.PrecipitationField,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryResolveWorldControlSwitch(value, weather.IsPrecipitationStarted,
                        out bool startPrecipitation, out string precipitationError))
                    {
                        return DataModificationApplyResult.Reject(
                            "Precipitation: " + precipitationError);
                    }
                    if (startPrecipitation && !weather.IsPrecipitationStarted)
                        weather.ManualPrecipitationStart();
                    else if (!startPrecipitation && weather.IsPrecipitationStarted)
                        weather.ManualPrecipitationEnd();
                    weatherTouched = true;
                    applied.Add("precipitation=" +
                        (weather.IsPrecipitationStarted ? "on" : "off"));
                    continue;
                }
                if (string.Equals(name, WorldControlDataOperation.FogField,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryResolveWorldControlSwitch(value, weather.IsFogStarted,
                        out bool startFog, out string fogError))
                        return DataModificationApplyResult.Reject("Fog: " + fogError);
                    if (startFog && !weather.IsFogStarted)
                        weather.ManualFogStart();
                    else if (!startFog && weather.IsFogStarted)
                        weather.ManualFogEnd();
                    weatherTouched = true;
                    applied.Add("fog=" + (weather.IsFogStarted ? "on" : "off"));
                    continue;
                }
                if (string.Equals(name, WorldControlDataOperation.LightningField,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(value, "strike", StringComparison.OrdinalIgnoreCase))
                        return DataModificationApplyResult.Reject(
                            "Lightning only supports the value 'strike'.");
                    if (!TryResolveWorldControlSourcePlayer(context.SourceClientId,
                        out ComponentPlayer sourcePlayer))
                    {
                        return DataModificationApplyResult.Reject(
                            "Lightning requires an online player to aim the strike.");
                    }
                    // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
                    Matrix eyeMatrix = Matrix.CreateFromQuaternion(
                        sourcePlayer.ComponentCreatureModel.EyeRotation);
                    weather.ManualLightingStrike(sourcePlayer.ComponentCreatureModel.EyePosition,
                        eyeMatrix.Forward);
                    applied.Add("lightning=strike");
                    continue;
                }
                if (string.Equals(name, WorldControlDataOperation.TimePointField,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryResolveWorldControlTimePoint(value, timeOfDay, out float point,
                        out string pointError))
                        return DataModificationApplyResult.Reject("TimePoint: " + pointError);
                    ApplyHostTimeOfDayJump(gameInfo, timeOfDay, point, value, applied);
                    continue;
                }
                if (string.Equals(name, WorldControlDataOperation.TimeExactField,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double exact) || !double.IsFinite(exact) || exact < 0.0 ||
                        exact > MaximumRequestedTimeExact)
                    {
                        return DataModificationApplyResult.Reject(
                            "TimeExact must be a number between 0 and 1.");
                    }
                    ApplyHostTimeOfDayJump(gameInfo, timeOfDay, (float)exact, value, applied);
                    continue;
                }
                return DataModificationApplyResult.Reject(
                    "Unknown world control field: " + name);
            }
            if (applied.Count == 0)
                return DataModificationApplyResult.Reject("No world control action was requested.");

            // 客户端只在总开关打开时才渲染降水/雾（`SubsystemWeather.Update` 会提前返回），所以
            // "开雨 / 开雾"必须顺带把总开关打开，否则客户端看起来"点了没反应"。
            if (weatherTouched && !gameInfo.WorldSettings.AreWeatherEffectsEnabled)
            {
                gameInfo.WorldSettings.AreWeatherEffectsEnabled = true;
                applied.Add("weatherEffects=on");
            }

            // 落地即在主机生效；分发复用既有链路：世界信息广播（2Hz）带降水/雾/时间偏移，
            // 客户端在既有处理器里自行应用（联机 mod 的客户端不参与落地）。
            SendGameWorldInfoMessage();

            string summary = string.Join(", ", applied);
            Log.Information("[ScMP] Host applied world control from client " +
                context.SourceClientId + ": " + summary);
            PublishServerAudit("dm.world", context.SourceClientId,
                "operation=" + context.Operation + " " + summary + " mod=" +
                NormalizePlayerAdministrationAuditValue(context.ModId));
            return DataModificationApplyResult.Success("world control: " + summary);
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
        // 原生按钮是"切换"，GM 菜单要能明确"开/关"，所以三种值都收。
        private static bool TryResolveWorldControlSwitch(string value, bool currentState,
            out bool state, out string error)
        {
            state = currentState;
            error = null;
            if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "start", StringComparison.OrdinalIgnoreCase))
            {
                state = true;
                return true;
            }
            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "stop", StringComparison.OrdinalIgnoreCase))
            {
                state = false;
                return true;
            }
            if (string.Equals(value, "toggle", StringComparison.OrdinalIgnoreCase))
                return true;
            error = "expected on / off / toggle.";
            return false;
        }

        // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.Middawn
        // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.Midday
        // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.Middusk
        // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.Midnight
        private static bool TryResolveWorldControlTimePoint(string value,
            SubsystemTimeOfDay timeOfDay, out float target, out string error)
        {
            target = 0f;
            error = null;
            if (timeOfDay == null)
            {
                error = "the host has no time-of-day subsystem.";
                return false;
            }
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "dawn":
                case "middawn":
                    target = timeOfDay.Middawn;
                    return true;
                case "noon":
                case "midday":
                    target = timeOfDay.Midday;
                    return true;
                case "dusk":
                case "middusk":
                    target = timeOfDay.Middusk;
                    return true;
                case "midnight":
                    target = timeOfDay.Midnight;
                    return true;
                default:
                    error = "expected dawn / noon / dusk / midnight.";
                    return false;
            }
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
        // 引擎原生按钮的算法就是"给 TimeOfDayOffset 加上 Interval(当前, 目标)"；这里改成定点跳。
        // 注意：`TimeOfDayMode` 不是 `Changing` 时引擎把时间**固定**在对应档位，改 TimeOfDayOffset
        // 看不到任何变化 —— 所以跳时间点之前必须先把昼夜切回"循环"，否则用户会以为"点了没反应"。
        private static void ApplyHostTimeOfDayJump(SubsystemGameInfo gameInfo,
            SubsystemTimeOfDay timeOfDay, float target, string label, List<string> applied)
        {
            if (timeOfDay == null)
                return;
            if (gameInfo?.WorldSettings != null &&
                gameInfo.WorldSettings.TimeOfDayMode != TimeOfDayMode.Changing)
            {
                gameInfo.WorldSettings.TimeOfDayMode = TimeOfDayMode.Changing;
                applied.Add("timeOfDayMode=Changing");
            }
            float delta = IntervalUtils.Interval(timeOfDay.TimeOfDay, target);
            timeOfDay.TimeOfDayOffset += delta;
            applied.Add("time=" + label + "->" +
                target.ToString("0.###", CultureInfo.InvariantCulture));
        }

        // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerAdministration.cs:
        // ScMultiplayer.TryResolveAuthorityPlayer
        // 闪电要有个"眼睛"来定方向：优先用发起请求的客户端，主机本地玩家作兜底。
        private bool TryResolveWorldControlSourcePlayer(int sourceClientId,
            out ComponentPlayer player)
        {
            player = null;
            if (sourceClientId > 0 &&
                TryResolveAuthorityPlayer(sourceClientId, out _, out ComponentPlayer remote) &&
                remote?.ComponentCreatureModel != null)
            {
                player = remote;
                return true;
            }
            if (TryResolveAuthorityPlayer(0, out _, out ComponentPlayer local) &&
                local?.ComponentCreatureModel != null)
            {
                player = local;
                return true;
            }
            return false;
        }
    }
}

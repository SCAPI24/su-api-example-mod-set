using Engine;
using Engine.Audio;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using TemplatesDatabase;

namespace HeadlessRenderingMod
{
    public sealed class HeadlessRenderingMod : IMod
    {
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_frameToken;
        private EventSubscriptionToken m_serverAuditToken;
        private EventSubscriptionToken m_serverRetransmitToken;
        private EventSubscriptionToken m_dataModificationApprovalToken;
        private EventSubscriptionToken m_dataModificationResultToken;
        private HeadlessServerConfig m_config;
        private HeadlessControlServer m_server;
        private GameControlCommands m_gameCommands;
        private CommandSequenceManager m_sequences;
        private WindowsConsoleController m_consoleController;
        private FrameRateLimiter m_frameRateLimiter;
        private IDisposable m_windowPresentationLease;
        private bool m_rootDrawStateCaptured;
        private bool m_originalRootDrawEnabled;
        private bool m_settingsStateCaptured;
        private bool m_originalFpsCounter;
        private bool m_originalFpsRibbon;
        private bool m_audioStateCaptured;
        private float m_originalMasterVolume;
        private bool m_windowHideAttempted;
        private object m_gameWindow;
        private PropertyInfo m_windowVisibleProperty;
        private bool? m_originalWindowVisible;
        private string m_lastFrameError;
        private double m_nextMultiplayerTelemetryTime;
        private ServerAuditLog m_serverAuditLog;
        private ServerAuditLog m_serverRetransmitLog;

        // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:
        // DataModificationEvents.ApprovalRequested / ApprovalControl
        // 名单命中的 DM 请求不在这里直接 resolve：先入队，等这一帧的审批事件派发结束后再提交，
        // 避免在 ScMP 的事件回调里重入它自己的数据修改运行时。
        private readonly Queue<PendingAutoApproval> m_pendingAutoApprovals =
            new Queue<PendingAutoApproval>();

        // Source: HeadlessRenderingMod/Server/DataModificationFeed.cs
        // 主机侧 DM 决策记录（请求 / 自动同意 / 手动裁决 / 回执）：控制台菜单里直接可见。
        private readonly DataModificationFeed m_dataModificationFeed = new DataModificationFeed();

        private sealed class PendingAutoApproval
        {
            public int SourceClientId;
            public int RequestId;
            public int TransferId;
            public string ModId = string.Empty;
            public string Operation = string.Empty;
            public string SourceKey = string.Empty;
        }

        public string Name => "无画面服务器";

        public string Version => "1.3.4";

        public IEnumerable<string> Dependencies => Array.Empty<string>();

        public bool IsEnabled { get; set; } = true;

        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            if (eventBus == null)
                throw new ArgumentNullException(nameof(eventBus));

            // Source: Engine/Engine/Storage.cs:Storage.ProcessPath
            // Keep relative paths used by older game code aligned with the executable data root.
            string instanceRoot = Path.GetFullPath(AppContext.BaseDirectory);
            Environment.CurrentDirectory = instanceRoot;

            m_config = HeadlessServerConfig.LoadOrCreate(instanceRoot);
            if (!m_config.Enabled)
            {
                Log.Information("[HeadlessRenderingMod] Disabled by server.json.");
                return;
            }

            if (m_config.DisableAudio || m_config.DisableDrawing)
                HeadlessAudioFallback.Ensure(
                    instanceRoot, m_config.DisableAudio, m_config.DisableDrawing);
            HeadlessDisplayDeviceFallback.Ensure();

            m_server = new HeadlessControlServer(m_config);
            m_gameCommands = new GameControlCommands(instanceRoot, eventBus);
            m_sequences = new CommandSequenceManager();
            try
            {
                // Source: EntitySystem/SuAPI/WindowPresentationControl.cs:WindowPresentationControl.Request
                // Pass process-local presentation intent without coupling SuAPICore to this Mod.
                m_windowPresentationLease = WindowPresentationControl.Request(
                    new WindowPresentationParameters(
                        m_config.HideWindow,
                        m_config.DisableDrawing));
                m_server.Start();
                m_frameRateLimiter = new FrameRateLimiter(m_config.TargetFrameRate);
                m_eventBus = eventBus;

                // Source: Survivalcraft/Game/Program.cs:Program.Run
                m_frameToken = eventBus.SubscribeEvent(
                    "Frame.Update",
                    HandleFrameUpdate,
                    EventPriority.LOWEST);
                // Source: EntitySystem/SuAPI/IModEventBus.cs:IModEventBus.SubscribeEvent
                m_serverAuditLog = new ServerAuditLog(Path.Combine(instanceRoot, "Logs", "Server"));
                m_serverAuditToken = eventBus.SubscribeEvent(
                    "ScMultiplayer.ServerAudit", HandleServerAuditEvent, EventPriority.LOWEST);
                m_serverRetransmitLog = new ServerAuditLog(
                    Path.Combine(instanceRoot, "Logs", "Server"), "Retransmit-", 64);
                m_serverRetransmitToken = eventBus.SubscribeEvent(
                    "ScMultiplayer.ServerRetransmitAudit",
                    HandleServerRetransmitEvent, EventPriority.LOWEST);
                m_dataModificationApprovalToken = eventBus.SubscribeEvent(
                    "ScMultiplayer.DataModification.ApprovalRequested",
                    HandleDataModificationApprovalEvent,
                    EventPriority.LOWEST);
                // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:
                // DataModificationEvents.Result
                // DM 回执（主机批准并落地后的 Applied / Failed / Rejected…）。本 mod 不引用
                // ScMultiplayer 程序集（两边都能独立部署），只按成员名读这条载荷；详情同时进
                // Logs/Game.log 和控制台菜单里的决策记录。
                m_dataModificationResultToken = eventBus.SubscribeEvent(
                    "ScMultiplayer.DataModification.Result",
                    HandleDataModificationResultEvent,
                    EventPriority.LOWEST);
                if (m_config.EnableConsole && OperatingSystem.IsWindows())
                {
                    m_consoleController = new WindowsConsoleController(
                        m_server,
                        m_config);
                    m_consoleController.SetDataModificationFeed(m_dataModificationFeed);
                    if (!m_consoleController.Start())
                    {
                        Log.Warning(
                            "[HeadlessRenderingMod] Console unavailable; " +
                            "the game window will remain visible.");
                    }
                }

                Log.Information(
                    $"[HeadlessRenderingMod] Instance '{m_config.InstanceId}' listening on " +
                    $"{m_config.BindAddress}:{m_config.Port}, target={m_config.TargetFrameRate} Hz.");
            }
            catch
            {
                m_windowPresentationLease?.Dispose();
                m_windowPresentationLease = null;
                m_server.Stop();
                m_server = null;
                throw;
            }
        }

        public void OnUnload()
        {
            if (m_eventBus != null && m_frameToken != null)
                m_eventBus.UnsubscribeEvent(m_frameToken);
            if (m_eventBus != null && m_serverAuditToken != null)
                m_eventBus.UnsubscribeEvent(m_serverAuditToken);
            if (m_eventBus != null && m_serverRetransmitToken != null)
                m_eventBus.UnsubscribeEvent(m_serverRetransmitToken);
            if (m_eventBus != null && m_dataModificationApprovalToken != null)
                m_eventBus.UnsubscribeEvent(m_dataModificationApprovalToken);
            if (m_eventBus != null && m_dataModificationResultToken != null)
                m_eventBus.UnsubscribeEvent(m_dataModificationResultToken);

            m_frameToken = null;
            m_serverAuditToken = null;
            m_serverRetransmitToken = null;
            m_dataModificationApprovalToken = null;
            m_dataModificationResultToken = null;
            m_eventBus = null;
            m_serverAuditLog?.Dispose();
            m_serverAuditLog = null;
            m_serverRetransmitLog?.Dispose();
            m_serverRetransmitLog = null;
            if (m_consoleController != null)
            {
                m_consoleController.Stop();
                m_consoleController = null;
            }
            RestoreGameState();

            if (m_server != null)
            {
                m_server.Stop();
                m_server = null;
            }

            m_windowPresentationLease?.Dispose();
            m_windowPresentationLease = null;
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        private object[] HandleFrameUpdate(object[] args)
        {
            try
            {
                ApplyHeadlessState();
                m_server.ProcessQueuedCommands(ExecuteCommand, m_config.MaxCommandsPerFrame);
                m_sequences.Update(ExecuteCommand, EvaluateSequenceCondition);
                ProcessPendingAutoApprovals();
                UpdateMultiplayerTelemetry();
                m_frameRateLimiter.WaitForNextFrame();
                m_lastFrameError = null;
            }
            catch (Exception ex)
            {
                string message = ex.GetType().Name + ": " + ex.Message;
                if (!string.Equals(message, m_lastFrameError, StringComparison.Ordinal))
                {
                    m_lastFrameError = message;
                    Log.Error("[HeadlessRenderingMod] Frame handler failed: " + message);
                }
            }
            return null;
        }

        // Source: EntitySystem/SuAPI/IModEventBus.cs:IModEventBus.TriggerEvent
        private object[] HandleServerAuditEvent(object[] args)
        {
            if (args != null && args.Length > 0 && args[0] is string record)
                m_serverAuditLog?.Enqueue(record);
            return null;
        }

        // Source: EntitySystem/SuAPI/IModEventBus.cs:IModEventBus.TriggerEvent
        private object[] HandleServerRetransmitEvent(object[] args)
        {
            if (args != null && args.Length > 0 && args[0] is string record)
                m_serverRetransmitLog?.Enqueue(record);
            return null;
        }

        // Source: Mod/ScMultiplayer/DataModification/ScMultiplayerDataModificationRuntime.cs:
        // PublishDataModificationApprovalRequest
        private object[] HandleDataModificationApprovalEvent(object[] args)
        {
            if (args != null && args.Length > 0 &&
                args[0] is IDictionary<string, object> request)
            {
                string modId = request.TryGetValue("modId", out object modValue)
                    ? modValue?.ToString() ?? "Mod" : "Mod";
                string operation = request.TryGetValue("operation", out object operationValue)
                    ? operationValue?.ToString() ?? "operation" : "operation";
                string source = request.TryGetValue("sourceClientId", out object sourceValue)
                    ? sourceValue?.ToString() ?? "?" : "?";
                string sourceKey = request.TryGetValue("sourceKey", out object keyValue)
                    ? keyValue?.ToString() ?? string.Empty : string.Empty;
                var entry = new DataModificationFeed.Entry
                {
                    Kind = "request",
                    Code = "Request",
                    ModId = modId,
                    Operation = operation,
                    SourceKey = sourceKey,
                    Details = "from client " + source +
                        (string.IsNullOrEmpty(sourceKey) ? string.Empty : " key=" + sourceKey)
                };
                if (TryReadRequestInteger(request, "sourceClientId", out int requestSourceClientId))
                    entry.SourceClientId = requestSourceClientId;
                if (TryReadRequestInteger(request, "requestId", out int requestRequestId))
                    entry.RequestId = requestRequestId;
                RecordDataModificationDecision(entry, "Approval required: " + modId + " / " +
                    operation + " from client " + source +
                    (string.IsNullOrEmpty(sourceKey) ? string.Empty : " key=" + sourceKey) +
                    ". Open Multiplayer Hosting > Data modification.");
                QueueAutoApproval(request, modId, operation, sourceKey);
            }
            return null;
        }

        // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:
        // DataModificationEvents.Result -> DataModificationResult
        // 主机批准并落地后的回执。本 mod 与 ScMultiplayer 之间没有程序集引用（两边都能独立部署），
        // 所以只按成员名读那条载荷：对象按公开属性读，字典按同名/camelCase 键读。
        private object[] HandleDataModificationResultEvent(object[] args)
        {
            if (args == null || args.Length == 0 || args[0] == null)
                return null;
            object payload = args[0];
            string code = DescribeDataModificationResultCode(ReadPayloadMember(payload, "Code"));
            if (string.IsNullOrEmpty(code))
                return null;
            string modId = ReadPayloadText(payload, "ModId", "Mod");
            string operation = ReadPayloadText(payload, "Operation", "operation");
            string details = ReadPayloadText(payload, "Details", string.Empty);
            int sourceClientId = ReadPayloadInteger(payload, "SourceClientId", -1);
            int requestId = ReadPayloadInteger(payload, "RequestId", -1);
            var entry = new DataModificationFeed.Entry
            {
                Kind = "result",
                Code = code,
                ModId = modId,
                Operation = operation,
                SourceClientId = sourceClientId,
                RequestId = requestId,
                Details = details
            };
            RecordDataModificationDecision(entry, "Result " + code + "  " + modId + " / " +
                operation +
                (sourceClientId >= 0 ? "  client " + sourceClientId : string.Empty) +
                (requestId >= 0 ? "  request " + requestId : string.Empty) +
                (string.IsNullOrEmpty(details) ? string.Empty : "  -  " + details));
            return null;
        }

        /// <summary>
        /// 同一条 DM 决策落三处：控制台菜单里的决策记录、Logs/Game.log、控制台输出
        /// （菜单开着时由控制台控制器排队，菜单关掉再打印）。
        /// </summary>
        private void RecordDataModificationDecision(DataModificationFeed.Entry entry,
            string text)
        {
            m_dataModificationFeed.Add(entry);
            Log.Information("[HeadlessRenderingMod] [DM] " + text);
            WriteConsoleLine("[DM] " + text);
        }

        /// <summary>
        /// 控制台输出：菜单 / 输入提示 / 分页开着时交给控制台控制器排队，等交互结束再打印，
        /// 免得把远端唯一可用的交互菜单冲掉；没有控制台时退回标准输出。
        /// </summary>
        private void WriteConsoleLine(string text)
        {
            WindowsConsoleController controller = m_consoleController;
            if (controller != null && controller.IsRunning)
            {
                controller.Notify(text);
                return;
            }
            Console.WriteLine(text);
        }

        // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:
        // DataModificationEvents.ApprovalControl
        // 无头服务器没人点审批弹窗：server.json 里 autoApproveDataModificationUserIds 命中的请求
        // （"*" = 任意客户端）自动 allow。匹配用的是**记录键**（账号 userid；无身份时 "name:名字"），
        // 玩家名可以随便改、这个键不能改。这里**不区分 mod**：任何 mod 的数据修改走同一套审批。
        private void QueueAutoApproval(IDictionary<string, object> request, string modId,
            string operation, string sourceKey)
        {
            string[] userIds = m_config?.AutoApproveDataModificationUserIds;
            if (userIds == null || userIds.Length == 0)
                return;
            if (!TryReadRequestInteger(request, "sourceClientId", out int sourceClientId) ||
                !TryReadRequestInteger(request, "requestId", out int requestId) ||
                !TryReadRequestInteger(request, "transferId", out int transferId))
            {
                return;
            }
            if (!MatchesUserId(userIds, sourceKey))
                return;
            m_pendingAutoApprovals.Enqueue(new PendingAutoApproval
            {
                SourceClientId = sourceClientId,
                RequestId = requestId,
                TransferId = transferId,
                ModId = modId,
                Operation = operation,
                SourceKey = sourceKey
            });
            RecordDataModificationDecision(new DataModificationFeed.Entry
            {
                Kind = "auto-approve",
                Code = "AutoApproveQueued",
                ModId = modId,
                Operation = operation,
                SourceClientId = sourceClientId,
                RequestId = requestId,
                SourceKey = sourceKey,
                Details = string.IsNullOrEmpty(sourceKey)
                    ? "server.json allowlist" : "server.json allowlist " + sourceKey
            }, "Auto approve queued: " + modId + " / " + operation +
                " from client " + sourceClientId +
                (string.IsNullOrEmpty(sourceKey) ? " (no identity)" : " key=" + sourceKey));
        }

        private void ProcessPendingAutoApprovals()
        {
            while (m_pendingAutoApprovals.Count > 0)
            {
                PendingAutoApproval pending = m_pendingAutoApprovals.Dequeue();
                var values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["operation"] = "resolve",
                    ["sourceClientId"] = pending.SourceClientId,
                    ["requestId"] = pending.RequestId,
                    ["transferId"] = pending.TransferId,
                    ["allow"] = true
                };
                bool resolved = false;
                string outcome = "no response";
                try
                {
                    object[][] responses = m_eventBus?.TriggerEvent(
                        "ScMultiplayer.DataModification.ApprovalControl",
                        new object[] { values });
                    if (responses != null)
                    {
                        foreach (object[] response in responses)
                        {
                            if (response != null && response.Length > 0 &&
                                response[0] is Dictionary<string, object> state)
                            {
                                if (state.TryGetValue("resolved", out object resolvedValue) &&
                                    resolvedValue is bool resolvedFlag)
                                {
                                    resolved = resolvedFlag;
                                    outcome = "resolved=" + resolvedFlag;
                                }
                                else
                                {
                                    outcome = "handled";
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    outcome = "error: " + ex.Message;
                    Log.Warning("[HeadlessRenderingMod] Auto approval failed: " + ex.Message);
                }
                RecordDataModificationDecision(new DataModificationFeed.Entry
                {
                    Kind = "auto-approve",
                    Code = resolved ? "AutoApproved" : "AutoApproveFailed",
                    ModId = pending.ModId,
                    Operation = pending.Operation,
                    SourceClientId = pending.SourceClientId,
                    RequestId = pending.RequestId,
                    Details = outcome
                }, "Auto approve submitted: client " + pending.SourceClientId + " request " +
                    pending.RequestId + " -> " + outcome);
            }
        }

        private static bool TryReadRequestInteger(IDictionary<string, object> values,
            string name, out int result)
        {
            result = 0;
            if (values == null || !values.TryGetValue(name, out object value) || value == null)
                return false;
            try
            {
                result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool MatchesUserId(string[] userIds, string sourceKey)
        {
            foreach (string userId in userIds)
            {
                if (userId == "*")
                    return true;
                if (!string.IsNullOrEmpty(sourceKey) &&
                    string.Equals(userId, sourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>按成员名读一条 ScMultiplayer 载荷（不引用它的程序集）。</summary>
        private static object ReadPayloadMember(object payload, string name)
        {
            if (payload is IDictionary<string, object> map)
            {
                if (map.TryGetValue(name, out object mapped))
                    return mapped;
                string camelCase = char.ToLowerInvariant(name[0]) + name.Substring(1);
                return map.TryGetValue(camelCase, out mapped) ? mapped : null;
            }
            Type type = payload.GetType();
            PropertyInfo property = type.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanRead)
            {
                try
                {
                    return property.GetValue(payload);
                }
                catch (Exception)
                {
                }
            }
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null)
            {
                try
                {
                    return field.GetValue(payload);
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        private static string ReadPayloadText(object payload, string name, string fallback)
        {
            object value = ReadPayloadMember(payload, name);
            return value == null
                ? fallback
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int ReadPayloadInteger(object payload, string name, int fallback)
        {
            object value = ReadPayloadMember(payload, name);
            if (value == null)
                return fallback;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:
        // DataModificationResultCode
        // 枚举名在 ScMultiplayer/Obfuscar.xml 里是保名的（SkipType），所以优先用枚举名；
        // 万一只拿到数字（字典载荷），就按契约里的序号翻译。
        private static string DescribeDataModificationResultCode(object value)
        {
            if (value == null)
                return string.Empty;
            if (value is string text)
                return text;
            if (value.GetType().IsEnum)
                return value.ToString();
            try
            {
                int numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                switch (numeric)
                {
                    case 0: return "Accepted";
                    case 1: return "Applied";
                    case 2: return "Rejected";
                    case 3: return "Busy";
                    case 4: return "Invalid";
                    case 5: return "NotSupported";
                    case 6: return "Failed";
                    case 7: return "Cancelled";
                    default: return "Code" + numeric.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                return value.ToString();
            }
        }

        private void ApplyHeadlessState()
        {
            // Source: Survivalcraft/Game/Widget.cs:DrawContext.CollateDrawItems
            if (m_config.DisableDrawing && ScreensManager.RootWidget != null)
            {
                if (!m_rootDrawStateCaptured)
                {
                    m_originalRootDrawEnabled = ScreensManager.RootWidget.IsDrawEnabled;
                    m_rootDrawStateCaptured = true;
                }
                ScreensManager.RootWidget.IsDrawEnabled = false;
            }

            // Source: Survivalcraft/Game/PerformanceManager.cs:PerformanceManager.Draw
            if (!m_settingsStateCaptured)
            {
                m_originalFpsCounter = SettingsManager.DisplayFpsCounter;
                m_originalFpsRibbon = SettingsManager.DisplayFpsRibbon;
                m_settingsStateCaptured = true;
            }
            SettingsManager.DisplayFpsCounter = false;
            SettingsManager.DisplayFpsRibbon = false;

            // Source: Engine/Engine/Audio/Mixer.cs:Mixer.MasterVolume
            if (m_config.DisableAudio)
            {
                if (!m_audioStateCaptured)
                {
                    m_originalMasterVolume = Mixer.MasterVolume;
                    m_audioStateCaptured = true;
                }
                Mixer.MasterVolume = 0f;
            }

            bool consoleReady = !m_config.EnableConsole ||
                (m_consoleController != null && m_consoleController.IsRunning);
            if (m_config.HideWindow && consoleReady && !m_windowHideAttempted)
                HideWindowOnce();
        }

        // Source: EntitySystem/SuAPI/IModEventBus.cs:IModEventBus.TriggerEvent
        // A five-second console-title refresh keeps the server's traffic state visible without
        // writing over interactive console input or adding a per-frame networking operation.
        private void UpdateMultiplayerTelemetry()
        {
            if (m_consoleController == null || Time.RealTime < m_nextMultiplayerTelemetryTime)
                return;
            m_nextMultiplayerTelemetryTime = Time.RealTime + 5.0;
            object[][] results = m_eventBus?.TriggerEvent(
                "ScMultiplayer.ServerSettings",
                new object[] { new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["operation"] = "get"
                } }) ?? Array.Empty<object[]>();
            foreach (object[] result in results)
            {
                if (result != null && result.Length > 0 &&
                    result[0] is Dictionary<string, object> values)
                {
                    m_consoleController.SetMultiplayerTelemetry(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Clients {0} | UDP OUT {1} | UDP IN {2} | Join {3}",
                        ReadTelemetryNumber(values, "connectedClients"),
                        FormatLastSecondTraffic(values, "lastUdpOutBytes",
                            "lastUdpOutPackets"),
                        FormatLastSecondTraffic(values, "lastUdpInBytes",
                            "lastUdpInPackets"),
                        ReadTelemetryText(values, "joinState", "idle")));
                    break;
                }
            }
        }

        private static double ReadTelemetryNumber(Dictionary<string, object> values, string name)
        {
            return values.TryGetValue(name, out object value) && value != null
                ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
                : 0.0;
        }

        private static string ReadTelemetryText(Dictionary<string, object> values, string name,
            string fallback)
        {
            return values.TryGetValue(name, out object value) && value != null
                ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                : fallback;
        }

        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticTransmitter.SendPacket
        // Packet.Bytes is the actual UDP payload handled by Comms. It excludes IP/UDP headers and
        // includes ACKs, heartbeats, fragments and retransmissions, matching socket activity.
        private static string FormatLastSecondTraffic(Dictionary<string, object> values,
            string bytesName, string packetsName)
        {
            double bytes = ReadTelemetryNumber(values, bytesName);
            double packets = ReadTelemetryNumber(values, packetsName);
            if (bytes < 0.0) return "--";
            string rate;
            if (bytes >= 1024.0 * 1024.0)
            {
                rate = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.0} MiB/s", bytes / (1024.0 * 1024.0));
            }
            else if (bytes >= 1024.0)
            {
                rate = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.0} KiB/s", bytes / 1024.0);
            }
            else
            {
                rate = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0} B/s", bytes);
            }
            double average = packets > 0.0 ? bytes / packets : 0.0;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}, {1:0} pkt/s, {2:0} B/pkt", rate, Math.Max(0.0, packets), average);
        }

        // Source: Engine/Engine/Window.cs:Window.m_gameWindow
        private void HideWindowOnce()
        {
            m_windowHideAttempted = true;
            m_gameWindow = ModManager.Instance.ModParentField.GetStaticField(
                typeof(Engine.Window),
                "m_gameWindow");

            if (m_gameWindow == null)
                throw new InvalidOperationException("OpenTK GameWindow is not available.");

            m_windowVisibleProperty = m_gameWindow.GetType().GetProperty(
                "Visible",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (m_windowVisibleProperty == null ||
                m_windowVisibleProperty.PropertyType != typeof(bool) ||
                !m_windowVisibleProperty.CanRead ||
                !m_windowVisibleProperty.CanWrite)
            {
                throw new MissingMemberException(
                    m_gameWindow.GetType().FullName,
                    "Visible");
            }

            m_originalWindowVisible = (bool)m_windowVisibleProperty.GetValue(m_gameWindow);
            m_windowVisibleProperty.SetValue(m_gameWindow, false);
        }

        // Source: Survivalcraft/Game/ScreensManager.cs and GameManager.cs
        private object ExecuteCommand(ControlRequest request)
        {
            if (m_gameCommands.TryExecute(request, out object gameResult))
                return gameResult;

            switch (request.Command)
            {
                case "status":
                    return BuildStatus();
                case "multiplayer.settings":
                    return UpdateMultiplayerSettings(request);
                case "multiplayer.dm":
                    return ControlDataModificationApprovals(request);
                case "screen.list":
                    return ListScreens();
                case "screen.switch":
                    return SwitchScreen(request);
                case "dialog.list":
                    return ListDialogs();
                case "createworld":
                case "world.create":
                    return CreateWorld(request);
                case "sequence.start":
                    return m_sequences.Start(request);
                case "sequence.status":
                    return m_sequences.GetStatus(request);
                case "sequence.list":
                    return m_sequences.List();
                case "sequence.cancel":
                    return m_sequences.Cancel(request);
                case "shutdown":
                    return Shutdown();
                default:
                    throw new ControlCommandException(
                        "unknown_command",
                        $"Unknown command '{request.Command}'.");
            }
        }

        // Source: Survivalcraft/Game/GameManager.cs and SubsystemPlayers.cs
        private Dictionary<string, object> BuildStatus()
        {
            Dictionary<string, Screen> screens = GetScreens();
            string currentScreen = FindScreenName(screens, ScreensManager.CurrentScreen);
            int playersCount = 0;
            if (GameManager.Project != null)
            {
                SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
                if (players != null)
                    playersCount = players.PlayersData.Count;
            }

            bool? windowVisible = GetWindowVisible();
            string worldName = GameManager.WorldInfo != null
                ? GameManager.WorldInfo.WorldSettings.Name
                : null;

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["instanceId"] = m_config.InstanceId,
                ["modVersion"] = Version,
                ["processId"] = Environment.ProcessId,
                ["currentScreen"] = currentScreen,
                ["screenAnimating"] = ScreensManager.IsAnimating,
                ["worldLoaded"] = GameManager.Project != null,
                ["worldName"] = worldName,
                ["playersCount"] = playersCount,
                ["targetFrameRate"] = m_config.TargetFrameRate,
                ["lastFrameTime"] = Program.LastFrameTime,
                ["frameIndex"] = Time.FrameIndex,
                ["rootWidgetDrawEnabled"] = ScreensManager.RootWidget != null
                    ? ScreensManager.RootWidget.IsDrawEnabled
                    : null,
                ["masterVolume"] = Mixer.MasterVolume,
                ["windowVisible"] = windowVisible,
                ["queuedCommands"] = m_server.QueuedCommandCount,
                ["serverError"] = m_server.LastError,
                ["frameError"] = m_lastFrameError
            };
            object[][] settingsResults = m_eventBus?.TriggerEvent(
                "ScMultiplayer.ServerSettings",
                new object[] { new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["operation"] = "get"
                } }) ?? Array.Empty<object[]>();
            foreach (object[] settingsResult in settingsResults)
            {
                if (settingsResult != null && settingsResult.Length > 0 &&
                    settingsResult[0] is Dictionary<string, object> settings)
                {
                    result["multiplayer"] = settings;
                    break;
                }
            }
            return result;
        }

        // Source: EntitySystem/SuAPI/IModEventBus.cs:IModEventBus.TriggerEvent
        // Keep the headless server independent from ScMultiplayer. A missing listener simply
        // means this instance has no multiplayer configuration surface.
        private Dictionary<string, object> UpdateMultiplayerSettings(ControlRequest request)
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            bool changed = CopyOptionalBoolean(request, values, "autoCreateRoomFromCurrentWorld") |
                CopyOptionalBoolean(request, values, "autoApproveJoinRequests") |
                CopyOptionalBoolean(request, values, "serverDiagnosticsEnabled") |
                CopyOptionalString(request, values, "dataModificationMode") |
                CopyOptionalInteger(request, values, "dataModificationFastMaxConcurrent") |
                CopyOptionalInteger(request, values, "dataModificationBulkMaxConcurrent") |
                CopyOptionalInteger(request, values, "dataModificationBulkApplyChunksPerFrame") |
                CopyOptionalInteger(request, values, "dataModificationBulkApplyBytesPerFrame") |
                CopyOptionalBoolean(request, values, "bandwidthConfigurationEnabled") |
                CopyOptionalString(request, values, "bandwidthMode") |
                CopyOptionalInteger(request, values, "sharedTotalSafeCapKbps") |
                CopyOptionalInteger(request, values, "serverUploadLimitKbps") |
                CopyOptionalInteger(request, values, "serverDownloadLimitKbps") |
                CopyOptionalInteger(request, values, "joinTransferMaxKbps") |
                CopyOptionalInteger(request, values, "joinTransferGameplayHeadroomKbps") |
                CopyOptionalInteger(request, values, "joinTransferBurstKiB") |
                CopyOptionalInteger(request, values, "joinTransferPerJoinMaxKbps");
            values["operation"] = changed ? "set" : "get";
            object[][] results = m_eventBus?.TriggerEvent(
                "ScMultiplayer.ServerSettings", new object[] { values }) ??
                Array.Empty<object[]>();
            foreach (object[] result in results)
            {
                if (result != null && result.Length > 0 &&
                    result[0] is Dictionary<string, object> settings)
                {
                    return settings;
                }
            }
            throw new ControlCommandException("multiplayer_unavailable",
                "ScMultiplayer is not loaded on this server.");
        }

        private Dictionary<string, object> ControlDataModificationApprovals(
            ControlRequest request)
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            string operation = request.TryGetString("operation", out string requestedOperation)
                ? requestedOperation : "list";
            values["operation"] = operation;
            if (string.Equals(operation, "resolve", StringComparison.OrdinalIgnoreCase))
            {
                if (!(CopyOptionalInteger(request, values, "sourceClientId") &
                    CopyOptionalInteger(request, values, "requestId") &
                    CopyOptionalInteger(request, values, "transferId") &
                    CopyOptionalBoolean(request, values, "allow")))
                {
                    throw new ControlCommandException("invalid_request",
                        "resolve requires sourceClientId, requestId, transferId and allow.");
                }
            }
            object[][] results = m_eventBus?.TriggerEvent(
                "ScMultiplayer.DataModification.ApprovalControl",
                new object[] { values }) ?? Array.Empty<object[]>();
            foreach (object[] result in results)
            {
                if (result != null && result.Length > 0 &&
                    result[0] is Dictionary<string, object> response)
                    return response;
            }
            throw new ControlCommandException("multiplayer_unavailable",
                "ScMultiplayer DM approval control is unavailable.");
        }

        private static bool CopyOptionalInteger(ControlRequest request,
            Dictionary<string, object> values, string name)
        {
            if (!request.TryGetInteger(name, out int value))
                return false;
            values[name] = value;
            return true;
        }

        private static bool CopyOptionalString(ControlRequest request,
            Dictionary<string, object> values, string name)
        {
            if (!request.TryGetString(name, out string value))
                return false;
            values[name] = value;
            return true;
        }

        private static bool CopyOptionalBoolean(ControlRequest request,
            Dictionary<string, object> values, string name)
        {
            if (!request.TryGetBoolean(name, out bool value))
                return false;
            values[name] = value;
            return true;
        }

        // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.m_screens
        private List<string> ListScreens()
        {
            List<string> result = new List<string>(GetScreens().Keys);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.SwitchScreen
        private Dictionary<string, object> SwitchScreen(ControlRequest request)
        {
            if (ScreensManager.IsAnimating)
            {
                throw new ControlCommandException(
                    "screen_busy",
                    "A screen transition is already in progress.");
            }
            if (!request.TryGetString("screen", out string screenName) ||
                string.IsNullOrWhiteSpace(screenName))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "screen.switch requires a non-empty 'screen' argument.");
            }

            Dictionary<string, Screen> screens = GetScreens();
            if (!screens.ContainsKey(screenName))
            {
                throw new ControlCommandException(
                    "screen_not_found",
                    $"Screen '{screenName}' is not loaded.");
            }

            ScreensManager.SwitchScreen(screenName);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["screen"] = screenName
            };
        }

        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.Dialogs
        private List<Dictionary<string, object>> ListDialogs()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            for (int i = 0; i < DialogsManager.Dialogs.Count; i++)
            {
                Dialog dialog = DialogsManager.Dialogs[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["index"] = i,
                    ["type"] = dialog.GetType().FullName
                };
                if (dialog is MessageDialog)
                {
                    LabelWidget largeLabel = ModManager.Instance.ModParentField
                        .GetParentField<LabelWidget>(dialog, "m_largeLabelWidget", typeof(MessageDialog));
                    LabelWidget smallLabel = ModManager.Instance.ModParentField
                        .GetParentField<LabelWidget>(dialog, "m_smallLabelWidget", typeof(MessageDialog));
                    item["largeText"] = largeLabel?.Text;
                    item["smallText"] = smallLabel?.Text;
                }
                result.Add(item);
            }
            return result;
        }

        // Source: Survivalcraft/Game/NewWorldScreen.cs:NewWorldScreen.Update
        private Dictionary<string, object> CreateWorld(ControlRequest request)
        {
            if (ScreensManager.IsAnimating)
            {
                throw new ControlCommandException(
                    "screen_busy",
                    "A screen transition is in progress. Retry after screen.ready.");
            }
            if (GameManager.Project != null)
            {
                throw new ControlCommandException(
                    "world_already_loaded",
                    "A world is already loaded. Close it before creating another world.");
            }

            if (ScreensManager.FindScreen<GameLoadingScreen>("GameLoading") == null ||
                ScreensManager.FindScreen<GameScreen>("Game") == null)
            {
                throw new ControlCommandException(
                    "game_not_ready",
                    "World creation is not available until game loading has completed.");
            }

            if (!request.TryGetString("name", out string name) ||
                string.IsNullOrWhiteSpace(name))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "CreateWorld requires a non-empty 'name'.");
            }
            if (!WorldsManager.ValidateWorldName(name))
            {
                throw new ControlCommandException(
                    "invalid_world_name",
                    "World name must contain 1-14 ASCII letters, digits or spaces, " +
                    "and must start and end with a letter or digit.");
            }

            WorldsManager.UpdateWorldsList();
            if (WorldsManager.WorldInfos.Count >= WorldsManager.MaxAllowedWorlds)
            {
                throw new ControlCommandException(
                    "too_many_worlds",
                    $"A maximum of {WorldsManager.MaxAllowedWorlds} worlds is allowed.");
            }

            GameMode gameMode = ReadEnum(
                request,
                "gameMode",
                GameMode.Survival);
            if (gameMode == GameMode.Adventure)
            {
                throw new ControlCommandException(
                    "invalid_game_mode",
                    "Adventure mode cannot be selected for a new world.");
            }

            TerrainGenerationMode terrainGeneration = ReadEnum(
                request,
                "terrainGeneration",
                TerrainGenerationMode.Continent);
            if (gameMode != GameMode.Creative &&
                (terrainGeneration == TerrainGenerationMode.FlatContinent ||
                terrainGeneration == TerrainGenerationMode.FlatIsland))
            {
                throw new ControlCommandException(
                    "invalid_terrain",
                    "Flat terrain is available only in Creative mode.");
            }

            WorldSettings settings = new WorldSettings
            {
                Name = name,
                OriginalSerializationVersion = VersionsManager.SerializationVersion,
                GameMode = gameMode,
                StartingPositionMode = ReadEnum(
                    request,
                    "startingPosition",
                    StartingPositionMode.Easy),
                TerrainGenerationMode = terrainGeneration,
                EnvironmentBehaviorMode = ReadEnum(
                    request,
                    "environmentBehavior",
                    EnvironmentBehaviorMode.Living),
                TimeOfDayMode = ReadEnum(
                    request,
                    "timeOfDay",
                    TimeOfDayMode.Changing)
            };

            if (request.TryGetString("seed", out string seed))
                settings.Seed = seed ?? string.Empty;
            if (request.TryGetBoolean("weatherEffects", out bool weatherEffects))
                settings.AreWeatherEffectsEnabled = weatherEffects;
            if (request.TryGetBoolean("adventureRespawn", out bool adventureRespawn))
                settings.IsAdventureRespawnAllowed = adventureRespawn;
            if (request.TryGetBoolean(
                "adventureSurvivalMechanics",
                out bool adventureSurvivalMechanics))
            {
                settings.AreAdventureSurvivalMechanicsEnabled = adventureSurvivalMechanics;
            }
            if (request.TryGetBoolean(
                "supernaturalCreatures",
                out bool supernaturalCreatures))
            {
                settings.AreSupernaturalCreaturesEnabled = supernaturalCreatures;
            }
            if (request.TryGetBoolean("friendlyFire", out bool friendlyFire))
                settings.IsFriendlyFireEnabled = friendlyFire;
            if (request.TryGetBoolean("seasonsChanging", out bool seasonsChanging))
                settings.AreSeasonsChanging = seasonsChanging;

            // Source: Survivalcraft/Game/WorldOptionsScreen.cs:WorldOptionsScreen.Update
            settings.SeaLevelOffset = ReadIntegerOption(
                request, "seaLevelOffset", settings.SeaLevelOffset, -4, 4);
            settings.TemperatureOffset = ReadFloatOption(
                request, "temperatureOffset", settings.TemperatureOffset, -8f, 8f);
            settings.HumidityOffset = ReadFloatOption(
                request, "humidityOffset", settings.HumidityOffset, -8f, 8f);
            settings.BiomeSize = ReadChoiceFloatOption(
                request,
                "biomeSize",
                settings.BiomeSize,
                new[] { 0.25f, 0.33f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f });
            settings.YearDays = ReadChoiceFloatOption(
                request,
                "yearDays",
                settings.YearDays,
                new[] { 8f, 12f, 16f, 20f, 24f, 32f, 48f, 64f, 96f });
            settings.TimeOfYear = ReadFloatOption(
                request, "timeOfYear", settings.TimeOfYear, 0f, 0.999f);
            if (request.TryGetString("blocksTextureName", out string blocksTextureName))
                settings.BlocksTextureName = blocksTextureName ?? string.Empty;
            if (request.TryGetFloat("islandSizeEW", out float islandSizeEW))
                settings.IslandSize.X = ValidateRange("islandSizeEW", islandSizeEW, 30f, 2500f);
            if (request.TryGetFloat("islandSizeNS", out float islandSizeNS))
                settings.IslandSize.Y = ValidateRange("islandSizeNS", islandSizeNS, 30f, 2500f);
            if (request.TryGetInteger("terrainLevel", out int terrainLevel))
                settings.TerrainLevel = ValidateRange("terrainLevel", terrainLevel, 2, 252);
            if (request.TryGetFloat("shoreRoughness", out float shoreRoughness))
                settings.ShoreRoughness = ValidateRange("shoreRoughness", shoreRoughness, 0f, 1f);
            if (request.TryGetInteger("terrainBlockIndex", out int terrainBlockIndex))
                settings.TerrainBlockIndex = ValidateRange("terrainBlockIndex", terrainBlockIndex, 0, 1023);
            if (request.TryGetInteger("terrainOceanBlockIndex", out int terrainOceanBlockIndex))
                settings.TerrainOceanBlockIndex = ValidateRange("terrainOceanBlockIndex", terrainOceanBlockIndex, 0, 1023);
            bool hasPaletteColors = request.TryGetString(
                "paletteColors", out string paletteColors);
            bool hasPaletteNames = request.TryGetString(
                "paletteNames", out string paletteNames);
            if (hasPaletteColors || hasPaletteNames)
            {
                ValuesDictionary paletteValues = new ValuesDictionary();
                paletteValues.SetValue("Colors", paletteColors ?? new string(';', 15));
                paletteValues.SetValue("Names", paletteNames ?? new string(';', 15));
                settings.Palette = new WorldPalette(paletteValues);
            }

            if (settings.GameMode != GameMode.Creative)
                settings.ResetOptionsForNonCreativeMode(null);

            // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.CreateWorld
            WorldInfo worldInfo = WorldsManager.CreateWorld(settings);
            if (worldInfo == null)
            {
                throw new ControlCommandException(
                    "world_creation_failed",
                    "World files were created but could not be read back.");
            }

            // Source: Survivalcraft/Game/GameLoadingScreen.cs:GameLoadingScreen.Enter
            ScreensManager.SwitchScreen("GameLoading", worldInfo, null);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["worldName"] = worldInfo.WorldSettings.Name,
                ["directoryName"] = worldInfo.DirectoryName,
                ["gameMode"] = worldInfo.WorldSettings.GameMode.ToString(),
                ["startingPosition"] = worldInfo.WorldSettings.StartingPositionMode.ToString(),
                ["terrainGeneration"] = worldInfo.WorldSettings.TerrainGenerationMode.ToString(),
                ["screen"] = "GameLoading"
            };
        }

        // Source: Engine/Engine/Window.cs:Window.Close
        private Dictionary<string, object> Shutdown()
        {
            Engine.Window.Close();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["closing"] = true
            };
        }

        private Dictionary<string, Screen> GetScreens()
        {
            Dictionary<string, Screen> screens = ModManager.Instance.ModParentField
                .GetStaticField<Dictionary<string, Screen>>(
                    typeof(ScreensManager),
                    "m_screens");

            if (screens == null)
                throw new InvalidOperationException("ScreensManager screen registry is unavailable.");
            return screens;
        }

        private static string FindScreenName(
            Dictionary<string, Screen> screens,
            Screen currentScreen)
        {
            if (currentScreen == null)
                return null;

            foreach (KeyValuePair<string, Screen> pair in screens)
            {
                if (ReferenceEquals(pair.Value, currentScreen))
                    return pair.Key;
            }
            return currentScreen.GetType().Name;
        }

        private static T ReadEnum<T>(
            ControlRequest request,
            string argumentName,
            T defaultValue)
            where T : struct, Enum
        {
            if (!request.TryGetString(argumentName, out string value))
                return defaultValue;
            if (Enum.TryParse(value, true, out T result) && Enum.IsDefined(result))
                return result;

            throw new ControlCommandException(
                "invalid_argument",
                $"'{value}' is not a valid {argumentName} value.");
        }

        private static float ReadFloatOption(
            ControlRequest request,
            string argumentName,
            float defaultValue,
            float minimum,
            float maximum)
        {
            if (!request.TryGetFloat(argumentName, out float value))
                return defaultValue;
            return ValidateRange(argumentName, value, minimum, maximum);
        }

        private static int ReadIntegerOption(
            ControlRequest request,
            string argumentName,
            int defaultValue,
            int minimum,
            int maximum)
        {
            if (!request.TryGetInteger(argumentName, out int value))
                return defaultValue;
            return ValidateRange(argumentName, value, minimum, maximum);
        }

        private static float ReadChoiceFloatOption(
            ControlRequest request,
            string argumentName,
            float defaultValue,
            float[] choices)
        {
            if (!request.TryGetFloat(argumentName, out float value))
                return defaultValue;
            foreach (float choice in choices)
            {
                if (Math.Abs(choice - value) < 0.0001f)
                    return choice;
            }
            throw new ControlCommandException(
                "invalid_argument",
                $"'{argumentName}' must be one of: {string.Join(", ", choices)}.");
        }

        private static int ValidateRange(
            string argumentName,
            int value,
            int minimum,
            int maximum)
        {
            if (value < minimum || value > maximum)
                throw new ControlCommandException(
                    "invalid_argument",
                    $"'{argumentName}' must be between {minimum} and {maximum}.");
            return value;
        }

        private static float ValidateRange(
            string argumentName,
            float value,
            float minimum,
            float maximum)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < minimum || value > maximum)
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    $"'{argumentName}' must be between {minimum} and {maximum}.");
            }
            return value;
        }

        // Source: Survivalcraft/Game/ScreensManager.cs and GameManager.cs
        private bool EvaluateSequenceCondition(string condition)
        {
            if (string.Equals(condition, "screen.ready", StringComparison.OrdinalIgnoreCase))
                return !ScreensManager.IsAnimating;
            if (string.Equals(condition, "world.loaded", StringComparison.OrdinalIgnoreCase))
                return GameManager.Project != null;
            if (string.Equals(condition, "world.unloaded", StringComparison.OrdinalIgnoreCase))
                return GameManager.Project == null;
            if (string.Equals(condition, "world.ready", StringComparison.OrdinalIgnoreCase))
            {
                return GameManager.Project != null &&
                    ScreensManager.CurrentScreen is not GameLoadingScreen;
            }
            if (condition.StartsWith("screen:", StringComparison.OrdinalIgnoreCase))
            {
                string expected = condition.Substring("screen:".Length);
                string actual = FindScreenName(GetScreens(), ScreensManager.CurrentScreen);
                return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            }
            const string playersPrefix = "players.atleast:";
            if (condition.StartsWith(playersPrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(condition.Substring(playersPrefix.Length), out int count) &&
                GameManager.Project != null)
            {
                SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
                return players != null && players.PlayersData.Count >= count;
            }

            throw new ControlCommandException(
                "invalid_wait_condition",
                $"Unknown wait condition '{condition}'.");
        }

        private bool? GetWindowVisible()
        {
            if (m_gameWindow == null || m_windowVisibleProperty == null)
                return null;

            try
            {
                return (bool)m_windowVisibleProperty.GetValue(m_gameWindow);
            }
            catch
            {
                return null;
            }
        }

        private void RestoreGameState()
        {
            if (m_rootDrawStateCaptured && ScreensManager.RootWidget != null)
                ScreensManager.RootWidget.IsDrawEnabled = m_originalRootDrawEnabled;

            if (m_settingsStateCaptured)
            {
                SettingsManager.DisplayFpsCounter = m_originalFpsCounter;
                SettingsManager.DisplayFpsRibbon = m_originalFpsRibbon;
            }

            if (m_audioStateCaptured)
                Mixer.MasterVolume = m_originalMasterVolume;

            if (m_originalWindowVisible.HasValue &&
                m_gameWindow != null &&
                m_windowVisibleProperty != null)
            {
                try
                {
                    m_windowVisibleProperty.SetValue(
                        m_gameWindow,
                        m_originalWindowVisible.Value);
                }
                catch
                {
                }
            }
        }
    }
}

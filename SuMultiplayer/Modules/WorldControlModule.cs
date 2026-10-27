using System;
using System.Collections.Generic;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using WC = SuMultiplayer.Protocol.WorldControl;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 世界控制（**主机权威、纯瞬态**）：客户端创意四按钮（降水 / 雾 / 闪电 / 时间）。
    ///
    /// 语义（参照 = 旧联机 mod 的 WorldControlRequest/Result 一族 +
    /// Source: Survivalcraft/Game/ComponentGui.cs:679-737 引擎原生行为）：
    ///   · 客户端**不在本地执行**：输入外壳（Engine/EntityRequestInput.cs:SuComponentInput）
    ///     在 ComponentGui 读到按钮之前捕获意图，经本模块发 <c>WORLD_CONTROL_REQUEST</c>，
    ///     并消费点击（防本地执行后被 2 Hz 快照拉回的"按了又弹回"）；
    ///   · 主机收到请求后在**权威世界**上执行引擎同款调用，状态变化由既有
    ///     WorldInfo 快照（~2 Hz）广播给所有端生效——本模块**不重复做状态同步**；
    ///   · 主机回 <c>WORLD_CONTROL_RESULT</c>（应用了哪些动作 + 最新降水/雾状态 +
    ///     时间档位），客户端据此显示与引擎一致的提示；拒绝必须显式带原因；
    ///   · 没有会话 / 通道不可用时不拦截（按钮走原生单机行为），显式计数。
    ///
    /// 模块层不引用 Game：引擎触达全部走 <see cref="IWorldControlPort"/>。无静态状态。
    /// </summary>
    public sealed class WorldControlModule : IMultiplayerModule, ISuMPMessageModule,
        IWorldControlRequestSink
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;
        /// <summary>客户端在途请求超时：超过它仍未收到回执 → 明确失败（不假装成功）。</summary>
        private const int PendingTimeoutMs = 5000;
        /// <summary>两次提交的最小间隔（连点限流；被限流的点击仍会被消费，只计数不重发）。</summary>
        private const int MinSubmitIntervalMs = 200;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IWorldControlPort m_port;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;

        // 客户端：在途请求（同一时刻最多一条；按钮是低频操作，重入按限流处理）
        private long m_pendingRequestId;
        private long m_pendingSentMs;
        private WorldControlActions m_pendingActions;
        private long m_lastSubmitMs;
        private long m_nextSequence;

        // ---------------------------------------------------------------- 计数
        private long m_submitted;          // 客户端：TrySubmit 成功发出
        private long m_rateLimited;        // 客户端：间隔内被限流（消费点击但不重发）
        private long m_sendFailed;         // 客户端：发送失败（通道不可用 / 背压）
        private long m_timeouts;           // 客户端：在途超时
        private long m_results;            // 客户端：收到回执
        private long m_unknownResults;     // 客户端：对不上在途请求的回执（迟到/重复）
        private long m_hostRecv;           // 主机：收到请求
        private long m_hostApplied;        // 主机：成功落地
        private long m_hostRejected;       // 主机：明确拒绝
        private long m_hostResultFailed;   // 主机：回执发送失败
        private long m_wrongDirection;     // 方向错误的入站消息（客户端收到请求 / 主机收到结果）
        private long m_lastSummaryMs;

        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public WorldControlModule(IWorldControlPort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("world-control",
                "世界控制（创意四按钮：降水/雾/闪电/时间，主机权威）");
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        private bool IsHosting => m_control != null && m_control.IsHosting;

        private void Log(string message)
        {
            m_context?.Diagnostics.Info(m_descriptor.Id, message);
        }

        private void LogProblem(string key, string message)
        {
            long now = Environment.TickCount64;
            if (m_lastProblemMs.TryGetValue(key, out long last) && now - last < ProblemLogMs) return;
            m_lastProblemMs[key] = now;
            m_context?.Diagnostics.Warn(m_descriptor.Id, message);
        }

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 世界控制不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("没有拿到通道控制端口（ITransportControlPort）→ 无法判断主机/客户端，" +
                    "世界控制不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_messages) || m_messages == null)
            {
                Log("没有拿到消息注册表（IMessageRegistryPort）→ 收发都不成立，世界控制不启用");
                return;
            }
            m_messages.Registry.RegisterModule(this);
            m_port.SetRequestSink(this);
            ResetTransientState();
            m_attached = true;
            Log("世界控制就绪：客户端创意四按钮（降水/雾/闪电/时间）只发请求；" +
                "主机在权威世界执行（引擎同款调用），状态由 WorldInfo 快照广播生效；" +
                "回执带最新状态与时间档位，客户端显示与引擎一致的提示");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(m_descriptor.Id);
            registry.Register((int)MessageType.WorldControlRequest, WC.WorldControlRequest.Parser,
                new DelegateMessageHandler<WC.WorldControlRequest>(OnWorldControlRequest),
                m_descriptor.Id);
            registry.Register((int)MessageType.WorldControlResult, WC.WorldControlResult.Parser,
                new DelegateMessageHandler<WC.WorldControlResult>(OnWorldControlResult),
                m_descriptor.Id);
        }

        public void Detach(ModuleDetachReason reason)
        {
            m_port.SetRequestSink(null);
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(m_descriptor.Id); }
                catch (Exception) { }
                m_messages = null;
            }
            ResetTransientState();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("世界控制下线（" + reason + "）：" + Summary());
        }

        private void ResetTransientState()
        {
            m_pendingRequestId = 0;
            m_pendingActions = WorldControlActions.None;
            m_lastSubmitMs = 0;
            m_nextSequence = 0;
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            long now = Environment.TickCount64;
            // 客户端：在途超时（明确失败；下一击重新发起，不自动重发——按钮语义是用户意图）
            if (m_pendingRequestId != 0 && now - m_pendingSentMs > PendingTimeoutMs)
            {
                m_timeouts++;
                LogProblem("client-timeout",
                    "世界控制请求超时未回执（request=" + m_pendingRequestId + " actions=" +
                    m_pendingActions + "，" + PendingTimeoutMs + " ms）→ 本次明确失败，不自动重发");
                m_pendingRequestId = 0;
                m_pendingActions = WorldControlActions.None;
            }
            if (now - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = now;
                Log(Summary());
            }
        }

        public string StatusLine() => "world-control: " + Summary();

        private string Summary() =>
            "client(submitted=" + m_submitted + " rateLimited=" + m_rateLimited +
            " sendFailed=" + m_sendFailed + " timeouts=" + m_timeouts +
            " results=" + m_results + " unknownResults=" + m_unknownResults +
            ") · host(recv=" + m_hostRecv + " applied=" + m_hostApplied +
            " rejected=" + m_hostRejected + " resultFailed=" + m_hostResultFailed +
            ") · wrongDirection=" + m_wrongDirection;

        // ================================================================= 客户端：提交（输入外壳调用）

        bool IWorldControlRequestSink.TrySubmit(WorldControlActions actions, bool hasAim,
            float aimX, float aimY, float aimZ, float dirX, float dirY, float dirZ)
        {
            if (!m_attached || m_messages == null || m_transport == null)
            {
                m_sendFailed++;
                return false;
            }
            if (IsHosting)
            {
                // 主机本机角色的按钮由引擎原生路径直接执行（不需要请求），不该走到这里；
                // 计数 + 放行原生行为。
                m_wrongDirection++;
                LogProblem("submit-on-host",
                    "主机本机提交了世界控制请求（应走引擎原生路径）→ 放行本地行为");
                return false;
            }
            if (actions == WorldControlActions.None)
            {
                m_sendFailed++;
                return false;
            }
            if ((actions & WorldControlActions.Lightning) != 0 && !hasAim)
            {
                LogProblem("lightning-no-aim",
                    "闪电请求缺少瞄准参数（无 ComponentCreatureModel）→ 整条请求不发");
                m_sendFailed++;
                return false;
            }
            long now = Environment.TickCount64;
            if (now - m_lastSubmitMs < MinSubmitIntervalMs)
            {
                // 连点限流：消费这次点击（返回 true）但不重发——两次切换会被 2 Hz 快照
                // 合并成最后一次的状态，重发只会让主机多翻转几次。
                m_rateLimited++;
                return true;
            }
            if (!m_transport.IsConnected)
            {
                m_sendFailed++;
                LogProblem("not-connected", "未连接主机 → 世界控制请求不发（按钮走原生行为）");
                return false;
            }
            long requestId = ++m_nextSequence;
            var request = new WC.WorldControlRequest
            {
                RequestId = requestId,
                Precipitation = (actions & WorldControlActions.Precipitation) != 0,
                Fog = (actions & WorldControlActions.Fog) != 0,
                Lightning = (actions & WorldControlActions.Lightning) != 0,
                TimeOfDay = (actions & WorldControlActions.TimeOfDay) != 0,
            };
            if (request.Lightning)
            {
                request.LightningX = aimX;
                request.LightningY = aimY;
                request.LightningZ = aimZ;
                request.DirX = dirX;
                request.DirY = dirY;
                request.DirZ = dirZ;
            }
            if (!m_messages.Sender.Send(0, request))
            {
                m_sendFailed++;
                LogProblem("send-failed",
                    "世界控制请求发送失败（request=" + requestId + " 通道 " +
                    m_transport.StateText + "）→ 本次放行原生行为（本地改动会被下次快照纠正）");
                return false;
            }
            m_submitted++;
            m_lastSubmitMs = now;
            m_pendingRequestId = requestId;
            m_pendingSentMs = now;
            m_pendingActions = actions;
            return true;
        }

        // ================================================================= 消息处理

        private void OnWorldControlRequest(MessageContext context, WC.WorldControlRequest message)
        {
            if (!IsHosting)
            {
                m_wrongDirection++;
                LogProblem("request-on-client", "客户端收到 WORLD_CONTROL_REQUEST（方向错误）→ 忽略");
                return;
            }
            m_hostRecv++;
            WorldControlActions actions = WorldControlActions.None;
            if (message.Precipitation) actions |= WorldControlActions.Precipitation;
            if (message.Fog) actions |= WorldControlActions.Fog;
            if (message.Lightning) actions |= WorldControlActions.Lightning;
            if (message.TimeOfDay) actions |= WorldControlActions.TimeOfDay;
            bool hasAim = message.Lightning &&
                IsFinite(message.LightningX) && IsFinite(message.LightningY) &&
                IsFinite(message.LightningZ) && IsFinite(message.DirX) &&
                IsFinite(message.DirY) && IsFinite(message.DirZ);
            WorldControlOutcome outcome = actions == WorldControlActions.None
                ? new WorldControlOutcome
                {
                    Accepted = false,
                    RejectReason = "empty request (no action flags)",
                }
                : m_port.ExecuteHost(actions, hasAim,
                    message.LightningX, message.LightningY, message.LightningZ,
                    message.DirX, message.DirY, message.DirZ, context.ClientId);
            if (outcome.Accepted) m_hostApplied++;
            else m_hostRejected++;
            var result = new WC.WorldControlResult
            {
                RequestId = message.RequestId,
                Accepted = outcome.Accepted,
                RejectReason = outcome.RejectReason ?? string.Empty,
                PrecipitationStarted = outcome.PrecipitationStarted,
                FogStarted = outcome.FogStarted,
                TimeResult = (int)outcome.TimeResult,
                Applied = (int)outcome.Applied,
            };
            if (!m_messages.Sender.Send(context.ClientId, result))
            {
                m_hostResultFailed++;
                LogProblem("result-send-failed",
                    "世界控制回执发送失败（client=" + context.ClientId + " request=" +
                    message.RequestId + "）→ 客户端会按超时明确失败");
            }
        }

        private void OnWorldControlResult(MessageContext context, WC.WorldControlResult message)
        {
            if (IsHosting)
            {
                m_wrongDirection++;
                LogProblem("result-on-host", "主机收到 WORLD_CONTROL_RESULT（方向错误）→ 忽略");
                return;
            }
            m_results++;
            if (message.RequestId != m_pendingRequestId)
            {
                m_unknownResults++;
                LogProblem("unknown-result",
                    "世界控制回执对不上在途请求（recv=" + message.RequestId +
                    " pending=" + m_pendingRequestId + "，迟到 / 重复 / 超时后到达）→ 丢弃");
                return;
            }
            m_pendingRequestId = 0;
            m_pendingActions = WorldControlActions.None;
            m_port.ShowClientResult(message.Accepted, message.RejectReason,
                (WorldControlActions)message.Applied,
                message.PrecipitationStarted, message.FogStarted,
                (WorldControlTimeResult)message.TimeResult);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

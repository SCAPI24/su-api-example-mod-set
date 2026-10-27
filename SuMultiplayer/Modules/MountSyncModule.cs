using System;
using System.Collections.Generic;
using Engine;

using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using PM = SuMultiplayer.Protocol.Mount;

using Google.Protobuf;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 乘骑同步模块（2026-10-26；**主机权威，客户端纯镜像**）。
    ///
    /// 客户端：骑乘按钮边沿 → <see cref="MountReplicaEngine.RequestSubmitted"/> →
    /// 这里编码 <see cref="MessageType.MountRequest"/> 发给主机（序号去重）；
    /// 收 <see cref="MessageType.MountState"/> → 端口 <see cref="IMountReplicaPort.TryPresentState"/>
    /// （本机骑手按 state_sequence 应用；观察端同样应用——化身 ParentBody 挂权威坐骑）。
    ///
    /// 主机：收 MountRequest → 端口 TryExecuteRequest（引擎原生 FindNearestMount /
    /// StartMounting / StartDismounting；拒绝 = Rejected）→ MountState 定向回请求者 +
    /// 广播全部玩家端（状态序列按骑手单调递增）。
    /// </summary>
    public sealed class MountSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private readonly ModuleDescriptor m_descriptor;
        private readonly IMountReplicaPort m_port;
        private readonly Dictionary<int, long> m_lastStateSequenceByRider =
            new Dictionary<int, long>();
        private long m_nextStateSequence;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;

        // ---- 计数
        private long m_requestsSent;
        private long m_requestsSendFailed;
        private long m_requestsReceived;
        private long m_requestsRejectedDirection;
        private long m_executed;
        private long m_executeFailed;
        private long m_statesSent;
        private long m_statesReceived;
        private long m_statesMalformed;
        private long m_statesStale;
        private long m_statesApplied;

        public MountSyncModule(IMountReplicaPort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("mount-sync", "乘骑同步（主机权威 / 客户端镜像）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "mount.sync" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "mount-sync: req sent=" + m_requestsSent +
            " sendFail=" + m_requestsSendFailed + " recv=" + m_requestsReceived +
            " dirErr=" + m_requestsRejectedDirection + " exec=" + m_executed + "/" + m_executeFailed +
            " state sent=" + m_statesSent + " recv=" + m_statesReceived +
            " stale=" + m_statesStale + " applied=" + m_statesApplied + " · " + m_port.Describe();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                m_context?.Diagnostics.Info(ModuleId, "没有传输端口（transport 缺失）→ 乘骑同步不启用");
                return;
            }
            context.TryGetPort(out m_control);
            context.TryGetPort(out m_broadcast);
            if (context.TryGetPort(out IMessageRegistryPort messages) && messages != null)
                messages.Registry.RegisterModule(this);
            m_port.RequestSubmitted += OnRequestSubmitted;
            m_port.StateBroadcast += OnStateBroadcast;
            m_context?.Diagnostics.Info(ModuleId,
                "乘骑同步就绪：主机 = 引擎原生执行后回执+广播；客户端 = 拦截边沿发请求、按 MountState 镜像");
        }

        public void Register(MessageRegistry registry)
        {
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.MountRequest, PM.MountRequest.Parser,
                new DelegateMessageHandler<PM.MountRequest>(OnMountRequest), ModuleId);
            registry.Register((int)MessageType.MountState, PM.MountState.Parser,
                new DelegateMessageHandler<PM.MountState>(OnMountState), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            m_port.RequestSubmitted -= OnRequestSubmitted;
            m_port.StateBroadcast -= OnStateBroadcast;
            m_lastStateSequenceByRider.Clear();
            m_attached = false;
            m_context?.Diagnostics.Info(ModuleId, "乘骑同步下线（" + reason + "）：" + StatusLine());
        }

        public void Tick(in ModuleTickContext tick)
        {
        }

        private bool m_attached;

        // ------------------------------------------------------------ 客户端：请求发出

        private void OnRequestSubmitted(MountRequestData request)
        {
            if (m_control?.IsHosting == true) return;   // 主机自己走引擎原路径
            if (m_transport == null || !m_transport.IsConnected)
            {
                m_requestsSendFailed++;
                return;
            }
            if (m_transport.TrySend(MessageType.MountRequest, MountNetCodec.EncodeRequest(request)))
                m_requestsSent++;
            else
                m_requestsSendFailed++;
        }

        // ------------------------------------------------------------ 主机：执行

        private void OnMountRequest(MessageContext context, PM.MountRequest message)
        {
            if (m_control?.IsHosting != true)
            {
                m_requestsRejectedDirection++;
                return;
            }
            m_requestsReceived++;
            int clientId = context.ClientId;
            if (m_broadcast == null || !m_broadcast.IsPlayerClient(clientId)) return;
            if (!MountNetCodec.TryDecodeRequest(message.ToByteArray(), out MountRequestData request,
                    out string decodeReason))
            {
                m_requestsReceived--;
                m_context?.Diagnostics.Warn(ModuleId,
                    "MountRequest 解码失败（" + decodeReason + "，clientId=" + clientId + "）→ 丢弃");
                return;
            }
            // 主机执行（引擎原生）：结果 = MountState 回执 + 广播。
            bool ok = m_port.TryExecuteHostRequest(clientId, request,
                out int stateKind, out int mountKey, out string reason);
            if (ok) m_executed++;
            else
            {
                m_executeFailed++;
                m_context?.Diagnostics.Warn(ModuleId,
                    "乘骑请求执行失败（clientId=" + clientId + " " + request.Describe() + "）：" + reason);
            }
            m_port.ResolveRiderPose(clientId, out Vector3 mountPos, out Quaternion mountRot);
            var state = new MountStateData(
                clientId,
                request.Sequence,
                NextStateSequence(clientId),
                stateKind,
                mountKey,
                mountPos,
                mountRot);
            BroadcastState(state);
        }

        // ------------------------------------------------------------ 主机：广播

        private void OnStateBroadcast(MountStateData state)
        {
            if (m_transport == null) return;
            BroadcastState(state);
        }

        private void BroadcastState(in MountStateData state)
        {
            if (m_transport == null) return;
            byte[] payload = MountNetCodec.EncodeState(state);
            int sent = 0;
            if (m_broadcast != null)
                sent = m_broadcast.BroadcastToPlayers(MessageType.MountState, payload);
            // 主机本机骑手也要镜像自己的状态（本机表现与远端一致）。
            m_statesSent += sent;
            ApplyStateLocally(state);
        }

        private void ApplyStateLocally(in MountStateData state)
        {
            if (m_lastStateSequenceByRider.TryGetValue(state.RiderClientId, out long last) &&
                state.StateSequence <= last)
            {
                m_statesStale++;
                return;
            }
            m_lastStateSequenceByRider[state.RiderClientId] = state.StateSequence;
            if (m_port.TryPresentState(state)) m_statesApplied++;
        }

        // ------------------------------------------------------------ 客户端：状态应用

        private void OnMountState(MessageContext context, PM.MountState message)
        {
            if (m_control?.IsHosting == true) return;
            m_statesReceived++;
            if (!MountNetCodec.TryDecodeState(message.ToByteArray(), out MountStateData state,
                    out string decodeReason))
            {
                m_statesMalformed++;
                m_context?.Diagnostics.Warn(ModuleId,
                    "MountState 解码失败（" + decodeReason + "）→ 丢弃");
                return;
            }
            ApplyStateLocally(state);
        }

        private long NextStateSequence(int riderClientId)
        {
            // 不能在这里预写 m_lastStateSequenceByRider：BroadcastState → ApplyStateLocally
            // 会把它判成 stale（主机侧永远 applied 不涨、诊断误导）。由 ApplyStateLocally 统一记。
            m_nextStateSequence++;
            return m_nextStateSequence;
        }
    }
}

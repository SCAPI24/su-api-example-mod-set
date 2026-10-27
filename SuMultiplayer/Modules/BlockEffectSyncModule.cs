using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using PB = SuMultiplayer.Protocol.BlockDestroyEffect;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 方块破坏表现同步（**纯表现**：碎屑 / 秋季落叶粒子）。
    ///
    /// 方块值本身仍由既有 CellChange 同步，本模块绝不改格、不生成实体、不发掉落。
    /// 为什么需要它：引擎 DestroyCell 的 BlockDebrisParticleSystem 和
    /// DeciduousLeavesBlock.GetDigValue 的 LeavesParticleSystem 都不在网络消息里；
    /// 分屏时它们共享同一个 Project/SubsystemParticles，联机时不共享。
    ///
    /// 主机：观察器在 ChangeCell/DestroyCell 的 OnBlockRemoved 上报 old/new；
    ///   · 主机本地挖掘：引擎已建本端粒子 → 这里只按**客户端兴趣窗口**广播；
    ///   · 客户端编辑落地：主机走 ChangeCell，没有粒子 → 适配器补建主机端粒子，
    ///     并排除发起请求的客户端（它本地已经有同一份特效）。
    /// 客户端：只按消息里的 old_value 重建粒子，不调用 DestroyCell / GetDigValue。
    /// </summary>
    public sealed class BlockEffectSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly BlockEffectReplicaEngine m_engine;
        private readonly List<ClientWindowView> m_windows = new List<ClientWindowView>(4);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IWindowBroadcastPort m_window;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private long m_nextEventId;
        private long m_lastSummaryMs;
        private long m_lastProblemMs;

        private long m_hostEvents;
        private long m_hostCellsSent;
        private long m_hostClientsSent;
        private long m_hostOutsideWindow;
        private long m_hostNoWindowClients;
        private long m_hostSendFailed;
        private long m_hostIdle;
        private long m_clientReceived;
        private long m_clientPresented;
        private long m_clientRejected;
        private long m_clientMalformed;
        private long m_clientPresentFailed;

        public BlockEffectSyncModule(BlockEffectReplicaEngine engine)
        {
            m_engine = engine ?? throw new ArgumentNullException(nameof(engine));
            m_descriptor = new ModuleDescriptor("block-effect-sync",
                "方块破坏表现同步（碎屑/落叶粒子，纯表现）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "effects.blockdestroy" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;
        public string ModuleId => m_descriptor.Id;

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口 → 方块破坏表现同步不启用");
                return;
            }
            context.TryGetPort(out m_control);
            context.TryGetPort(out m_window);
            if (context.TryGetPort(out IMessageRegistryPort messages) && messages != null)
            {
                m_messages = messages;
                messages.Registry.RegisterModule(this);
            }
            m_engine.HostBlockDestroyed += OnHostBlockDestroyed;
            m_attached = true;
            Log("方块破坏表现同步就绪：主机 = 按各端兴趣窗口逐端投递；客户端 = 按 old_value " +
                "重建碎屑/落叶粒子（不写格）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.BlockDestroyEffect, PB.Parser,
                new DelegateMessageHandler<PB>(OnBlockDestroyEffect), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_engine.HostBlockDestroyed -= OnHostBlockDestroyed;
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_attached = false;
            Log("方块破坏表现同步下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            long nowMs = (long)(tick.Now * 1000.0);
            if (nowMs - m_lastSummaryMs < SummaryLogMs) return;
            if (m_hostEvents == 0 && m_clientReceived == 0) return;
            m_lastSummaryMs = nowMs;
            Log(Summary());
        }

        public string StatusLine() => "block-effect: " + Summary();

        private void OnHostBlockDestroyed(BlockDestroyEffectRecord record)
        {
            m_hostEvents++;
            if (m_control?.IsHosting != true)
            {
                m_hostIdle++;
                return;
            }
            if (m_window == null)
            {
                m_hostSendFailed++;
                LogProblem("no-window", "有方块破坏表现但没有兴趣窗口投递端口 → 无人收到（明确失败）");
                return;
            }
            var message = new PB
            {
                EventId = (uint)(++m_nextEventId),
                X = record.X,
                Y = record.Y,
                Z = record.Z,
                OldValue = (uint)record.OldValue,
                NewValue = (uint)record.NewValue,
                Flags = record.Flags
            };
            byte[] payload = message.ToByteArray();
            m_windows.Clear();
            int candidates = m_window.CaptureClientWindows(m_windows);
            if (candidates <= 0)
            {
                m_hostNoWindowClients++;
                LogProblem("no-clients", "方块破坏表现 (" + record.X + "," + record.Y + "," +
                    record.Z + ")：此刻没有在线玩家端上报兴趣窗口 → 无法下发表现");
                return;
            }
            var center = new Vector3(record.X + 0.5f, record.Y + 0.5f, record.Z + 0.5f);
            int sent = 0;
            for (int i = 0; i < m_windows.Count; i++)
            {
                ClientWindowView window = m_windows[i];
                if (record.SourceClientId > 0 && window.ClientId == record.SourceClientId)
                    continue; // 发起者本地已经有同一份特效，不重复
                if (!window.Contains(center, 0))
                {
                    m_hostOutsideWindow++;
                    continue;
                }
                if (!m_window.SendToClient(window.ClientId, MessageType.BlockDestroyEffect,
                        payload))
                {
                    m_hostSendFailed++;
                    LogProblem("send", "方块破坏表现发给客户端 " + window.ClientId +
                        " 失败（离线/背压）→ 这个端没有收到");
                    continue;
                }
                sent++;
                m_hostClientsSent++;
            }
            m_hostCellsSent += sent;
        }

        private void OnBlockDestroyEffect(MessageContext context, PB message)
        {
            m_clientReceived++;
            if (m_control?.IsHosting == true)
            {
                m_clientRejected++;
                LogProblem("on-host", "主机收到了 BlockDestroyEffect（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            if (message == null || message.OldValue == 0 || message.Flags == 0 ||
                message.Y < 1 || message.Y > 255)
            {
                m_clientMalformed++;
                LogProblem("malformed", "BlockDestroyEffect 载荷非法（old=" +
                    (message?.OldValue ?? 0) + " flags=" + (message?.Flags ?? 0) + "）→ 丢弃");
                return;
            }
            if (!m_engine.Present(message.X, message.Y, message.Z, (int)message.OldValue,
                    message.Flags, out string reason))
            {
                // 距离外 / 本端尚未加载该地形：不是协议错误，但计数必须可见。
                m_clientPresentFailed++;
                LogProblem("present:" + reason, "BlockDestroyEffect (" + message.X + "," +
                    message.Y + "," + message.Z + ") 未表现：" + reason);
                return;
            }
            m_clientPresented++;
        }

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long now = Environment.TickCount64;
            if (m_lastProblemMs != 0 && now - m_lastProblemMs < ProblemLogMs) return;
            m_lastProblemMs = now;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        private string Summary() => "host blockEffectEvents=" + m_hostEvents +
            " clientsSent=" + m_hostClientsSent +
            " cellsSent=" + m_hostCellsSent +
            " outsideWindow=" + m_hostOutsideWindow +
            " noWindowClients=" + m_hostNoWindowClients +
            " sendFailed=" + m_hostSendFailed +
            " idle=" + m_hostIdle +
            " · client received=" + m_clientReceived +
            " presented=" + m_clientPresented +
            " rejected=" + m_clientRejected +
            " malformed=" + m_clientMalformed +
            " presentFailed=" + m_clientPresentFailed;
    }
}

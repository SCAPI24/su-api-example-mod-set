using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol.Ambient;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 循环环境音（<c>Audio/Fire</c> / <c>Audio/Water</c> / <c>Audio/Magma</c>）同步模块
    /// （主机权威计算，客户端纯表现）。
    ///
    /// 为什么需要独立消息：
    ///   · 三条循环音由引擎 <c>SubsystemAmbientSounds</c> 用 <c>SubsystemAudio.CreateSound</c>
    ///     创建（Source: Survivalcraft/Game/SubsystemAmbientSounds.cs:69-77），**从不进入**
    ///     <c>m_queuedSounds</c>；全局声音中继（RemoteAudioSyncModule）只做队列差集，
    ///     因此循环低鸣完全不在它的覆盖范围（随机的 <c>Audio/Sizzles</c> 仍在队列里，照旧中继）；
    ///   · 驱动音量的方块行为 / <c>ComponentOnFire</c> 在客户端副本上被闸门挡住
    ///     （Engine/ClientReplicaSubsystems.cs），客户端本地字段恒为 0 ⇒ 客户端听不到循环音。
    ///
    /// 主机：
    ///   · 每 200 ms（5 Hz）为每个在线玩家端用**该端化身位置**做一次有界只读扫描
    ///     （Engine/AmbientSoundReplicaEngine.cs，流体半径 8 / 火与营火半径 12），
    ///     换算成 4 个最终音量；
    ///   · 只有任一音量变化超过 <see cref="AmbientSoundPolicy.ChangeEpsilon"/> 才发一条
    ///     <c>AMBIENT_SOUNDS_STATE</c>（id 93）给该端；没有客户端时完全不扫描；
    ///   · **不修改**主机自己的 SubsystemAmbientSounds 字段 —— 主机听感不变。
    ///
    /// 客户端：
    ///   · 严格解码（目标 clientId / revision / 有限非负值；水 / 岩浆 / 营火 ≤1，
    ///     火焰保留引擎原始值可 &gt;1）→ 防重放 / 越序 →
    ///     交给 <see cref="IAmbientSoundPort.ApplyClientVolumes"/> 存进引擎适配层；
    ///   · <c>SuSubsystemAmbientSounds</c> 在自己的 Update 前施加，引擎继续拥有循环 /
    ///     淡入淡出 / 暂停 / 随机 Sizzles；
    ///   · 断线 / 重连清空音量（循环暂停），绝不写格 / 生成实体。
    ///
    /// 计数口径（10 秒限流日志）：
    ///   ambientSent          主机成功发出的状态消息数
    ///   ambientRecv          客户端收到的状态消息数（含畸形）
    ///   ambientApplied       客户端成功施加的消息数
    ///   ambientSkipped       主机"变化不显著"跳过 + 客户端重复 / 越序 / 目标不匹配
    ///   ambientNoAvatar      主机端读不到化身位置的次数（含没有客户端时的空转轮）
    ///   ambientScanFailures  主机扫描失败次数（含异常 / 缺子系统 / 缺地形）
    ///   ambientMalformed     客户端解码失败（非法载荷 / 非有限值 / 越界）次数
    /// </summary>
    public sealed class AmbientSoundSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IAmbientSoundPort m_port;
        private readonly Dictionary<int, AmbientVolumes> m_lastSent =
            new Dictionary<int, AmbientVolumes>();
        private readonly List<AmbientSoundPeer> m_peers = new List<AmbientSoundPeer>(8);
        private readonly List<int> m_staleClients = new List<int>(8);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_wasConnected;
        private bool m_clientFirstApplyLogged;
        private bool m_clientNoDataWarned;
        private long m_connectedAtMs;
        private uint m_nextRevision;
        private uint m_lastClientRevision;
        private long m_lastRoundMs;
        private long m_lastSummaryMs;
        private int m_lastClientCount;

        // ---------------------------------------------------------------- 任务口径计数
        private long m_sent;
        private long m_recv;
        private long m_applied;
        private long m_skipped;
        private long m_noAvatar;
        private long m_scanFailures;
        private long m_malformed;

        // ---------------------------------------------------------------- 其它明确失败 / 诊断
        private long m_sendFailures;
        private long m_encodeFailures;
        private long m_applyFailures;
        private long m_directionErrors;
        private long m_idleRounds;
        private long m_rounds;
        private long m_resyncs;
        private long m_noHostData;
        private long m_earlyBeforeWorld;

        public AmbientSoundSyncModule(IAmbientSoundPort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("ambient-sound",
                "循环环境音同步（主机按客户端位置计算 / 客户端施加）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "effects.ambient-sound" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "ambient-sound: " + Summary() + " · " + m_port.Describe();

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 环境音同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "环境音同步不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到环境音状态");
            }
            m_transport.ClientJoined += OnClientJoined;
            m_transport.ClientLeft += OnClientLeft;
            m_lastSent.Clear();
            m_peers.Clear();
            m_staleClients.Clear();
            m_lastClientRevision = 0;
            m_nextRevision = 0;
            m_clientFirstApplyLogged = false;
            m_clientNoDataWarned = false;
            m_connectedAtMs = 0;
            m_lastRoundMs = 0;
            m_lastSummaryMs = 0;
            m_wasConnected = false;
            m_attached = true;
            Log("循环环境音同步就绪：主机 = 每 " + AmbientSoundPolicy.SendIntervalMs +
                " ms 按每个客户端化身位置扫描（流体 " + AmbientSoundPolicy.FluidRadius +
                " 格 / 火与营火 " + AmbientSoundPolicy.FireRadius + " 格，变化 >" +
                AmbientSoundPolicy.ChangeEpsilon.ToString("F2") + " 才发）；客户端 = 解码 / 防重放 / " +
                "施加到本机 SubsystemAmbientSounds（不跑方块行为、不写格）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.AmbientSoundsState, P.AmbientSoundsState.Parser,
                new DelegateMessageHandler<P.AmbientSoundsState>(OnAmbientSounds), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            try
            {
                if (m_transport != null)
                {
                    m_transport.ClientJoined -= OnClientJoined;
                    m_transport.ClientLeft -= OnClientLeft;
                }
            }
            catch (Exception) { }
            if (m_port.IsClientReplica)
            {
                try { m_port.ResetClientVolumes("module-detach:" + reason); }
                catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_lastSent.Clear();
            m_peers.Clear();
            m_staleClients.Clear();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("循环环境音同步下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            bool connected = m_transport != null && m_transport.IsConnected;
            bool hosting = m_control != null && m_control.IsHosting;
            long nowMs = (long)(tick.Now * 1000.0);

            if (connected && !m_wasConnected)
            {
                m_lastSent.Clear();
                m_nextRevision = 0;
                m_clientFirstApplyLogged = false;
                m_clientNoDataWarned = false;
                m_connectedAtMs = nowMs;
                // 连接建立后的第一轮立即扫描 / 发送，不额外等 200 ms。
                m_lastRoundMs = nowMs - AmbientSoundPolicy.SendIntervalMs;
                if (!hosting)
                {
                    // 断线分支已经把 revision 清 0；这里若仍为 0 才清音量。
                    // 若本帧的消息派发早于本模块 Tick（真实时序可能发生），revision 已 > 0，
                    // 绝不能把刚收到的首条音量再清掉（否则要等下一次"变化"才有声音）。
                    if (m_lastClientRevision == 0)
                    {
                        m_port.ResetClientVolumes("connected");
                        Log("ambient-sound: 连接已建立 → 等待主机首条状态（旧会话音量已清零）");
                    }
                    else
                    {
                        Log("ambient-sound: 连接已建立且已收到首条音量（revision=" +
                            m_lastClientRevision + "）→ 保留本会话状态，等待后续变化");
                    }
                }
                else
                {
                    Log("ambient-sound: 连接已建立 → 所有玩家端将在本轮重新同步");
                }
            }
            else if (!connected && m_wasConnected)
            {
                m_lastSent.Clear();
                if (!hosting)
                {
                    m_lastClientRevision = 0;
                    m_clientFirstApplyLogged = false;
                    m_clientNoDataWarned = false;
                    m_port.ResetClientVolumes("disconnected");
                    Log("ambient-sound: 连接断开 → 客户端三条循环清零 / 暂停");
                }
                else
                {
                    Log("ambient-sound: 连接断开 → 主机待发状态清空（重连后逐端重发）");
                }
            }
            m_wasConnected = connected;

            if (hosting && connected)
            {
                PumpHost(nowMs);
            }
            else if (!hosting && m_port.IsClientReplica && !connected)
            {
                // 已在断开分支清零；这里不重复调用，避免每帧写日志。
            }

            if (!hosting && connected && m_port.IsClientReplica && !m_clientNoDataWarned &&
                m_lastClientRevision == 0 && m_connectedAtMs > 0 &&
                nowMs - m_connectedAtMs >= 5000)
            {
                m_clientNoDataWarned = true;
                m_noHostData++;
                LogProblem("client-no-data",
                    "客户端已连接 5 s 仍未收到主机环境音状态（主机版本较旧 / ambient-sound 模块未装配）" +
                    "→ 三条循环保持静音（明确计数 ambientNoHostData；不伪造本地音量）");
            }

            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            m_lastSent.Remove(clientId);
            m_resyncs++;
            Log("ambient-sound: clientId=" + clientId + "（" + name + "）" +
                (rejoin ? "重连" : "加入") + " → 下一轮无条件重发该端音量（resync）");
        }

        private void OnClientLeft(int clientId, string reason)
        {
            m_lastSent.Remove(clientId);
            Log("ambient-sound: clientId=" + clientId + " 离开（" + reason + "）→ 清掉它的已发状态");
        }

        // ================================================================= 主机：5 Hz 逐端状态

        private void PumpHost(long nowMs)
        {
            if (nowMs - m_lastRoundMs < AmbientSoundPolicy.SendIntervalMs) return;
            m_lastRoundMs = nowMs;
            m_rounds++;
            m_peers.Clear();
            bool collected = m_port.TryCollectClients(m_peers, out string collectReason);
            if (!collected)
            {
                PruneLastSent();
                m_lastClientCount = 0;
                if (string.Equals(collectReason, "no-clients", StringComparison.Ordinal))
                {
                    // 主机 0 客户端：完全空转，不扫描、不访问地形。
                    m_idleRounds++;
                    return;
                }
                m_noAvatar++;
                LogProblem("collect:" + collectReason,
                    "收集玩家端位置失败（" + collectReason + "）→ 本轮不扫描 / 不发送" +
                    "（累计 " + m_noAvatar + " 次；不会往原点猜位置）");
                return;
            }

            m_lastClientCount = m_peers.Count;
            PruneLastSent();
            for (int i = 0; i < m_peers.Count; i++)
            {
                AmbientSoundPeer peer = m_peers[i];
                if (!peer.HasPosition)
                {
                    m_noAvatar++;
                    // 化身还没建好 / 已移除：如果此前发过非零音量，明确发一条 0 把循环停掉。
                    SendZeroIfNeeded(peer.ClientId, "no-avatar");
                    continue;
                }
                if (!m_port.TryComputeVolumes(peer.ClientId, peer.Position, out AmbientVolumes volumes,
                        out string scanReason))
                {
                    m_scanFailures++;
                    LogProblem("scan-failed:" + scanReason,
                        "环境音扫描失败（clientId=" + peer.ClientId + " pos=(" +
                        peer.Position.X.ToString("F1") + "," + peer.Position.Y.ToString("F1") + "," +
                        peer.Position.Z.ToString("F1") + "）：" + scanReason +
                        "）→ 该端本轮按 0 发送（累计 " + m_scanFailures + " 次，不静默保留旧音量）");
                    SendZeroIfNeeded(peer.ClientId, "scan-failed");
                    continue;
                }

                if (m_lastSent.TryGetValue(peer.ClientId, out AmbientVolumes previous) &&
                    !volumes.IsMeaningfulChange(previous, AmbientSoundPolicy.ChangeEpsilon))
                {
                    m_skipped++;
                    continue;
                }
                Send(peer.ClientId, in volumes, nowMs);
            }
        }

        private void SendZeroIfNeeded(int clientId, string why)
        {
            if (!m_lastSent.TryGetValue(clientId, out AmbientVolumes previous)) return;
            if (previous.IsZero) return;
            if (Send(clientId, AmbientVolumes.Zero, (long)(Time.RealTime * 1000.0)))
            {
                LogProblem("zero:" + why + ":" + clientId,
                    "ambient-sound: clientId=" + clientId + " 因 " + why + " 无法计算 → 已下发 0 音量" +
                    "（三条循环暂停，不保留旧音量）");
            }
        }

        private bool Send(int clientId, in AmbientVolumes volumes, long nowMs)
        {
            uint revision = ++m_nextRevision;
            if (revision == 0) revision = ++m_nextRevision;
            if (!AmbientSoundCodec.TryEncode((uint)clientId, revision, (uint)nowMs, in volumes,
                    out P.AmbientSoundsState message, out string encodeReason))
            {
                m_encodeFailures++;
                LogProblem("encode:" + encodeReason,
                    "环境音状态编码失败（clientId=" + clientId + " " + volumes.Describe() + "）：" +
                    encodeReason + " → 该端本轮没有更新（累计 " + m_encodeFailures + " 次）");
                return false;
            }
            byte[] payload = message.ToByteArray();
            if (!m_transport.TrySendTo(clientId, MessageType.AmbientSoundsState, payload))
            {
                m_sendFailures++;
                LogProblem("send:" + clientId,
                    "环境音状态发送失败（clientId=" + clientId + " " + volumes.Describe() +
                    "）→ 该端听不到本次变化（下一轮会重试，累计 " + m_sendFailures + " 次）");
                return false;
            }
            m_sent++;
            m_lastSent[clientId] = volumes;
            return true;
        }

        private void PruneLastSent()
        {
            if (m_lastSent.Count == 0) return;
            m_staleClients.Clear();
            foreach (KeyValuePair<int, AmbientVolumes> pair in m_lastSent)
            {
                bool present = false;
                for (int i = 0; i < m_peers.Count; i++)
                {
                    if (m_peers[i].ClientId == pair.Key)
                    {
                        present = true;
                        break;
                    }
                }
                if (!present) m_staleClients.Add(pair.Key);
            }
            for (int i = 0; i < m_staleClients.Count; i++) m_lastSent.Remove(m_staleClients[i]);
        }

        // ================================================================= 客户端：施加

        private void OnAmbientSounds(MessageContext context, P.AmbientSoundsState message)
        {
            m_recv++;
            if (m_control?.IsHosting == true)
            {
                m_directionErrors++;
                LogProblem("event-on-host",
                    "主机收到了 AMBIENT_SOUNDS_STATE（方向错误，clientId=" + context.ClientId +
                    "）→ 丢弃（主机音频由本地 setter 驱动）");
                return;
            }
            if (!m_port.IsClientReplica)
            {
                // 客户端可能在世界副本安装完成前就收到首条状态（主机握手 / 世界传输与
                // 模块 Tick 的时序）。此时先缓存，世界副本就绪后 SuSubsystemAmbientSounds
                // 会施加；主机方向已经在上面被拒掉。
                m_earlyBeforeWorld++;
                LogProblem("event-before-world",
                    "客户端在世界副本建立前收到 AMBIENT_SOUNDS_STATE → 先缓存，世界建立后施加" +
                    "（累计 " + m_earlyBeforeWorld + " 条）");
            }
            if (!AmbientSoundCodec.TryDecode(message, out AmbientVolumes volumes, out string reason))
            {
                m_malformed++;
                LogProblem("malformed:" + reason,
                    "AMBIENT_SOUNDS_STATE 载荷非法（" + reason + "，clientId=" + context.ClientId +
                    "）→ 整条丢弃");
                return;
            }
            int localClientId = m_transport?.LocalClientId ?? -1;
            if (message.TargetClientId != 0 && localClientId > 0 &&
                message.TargetClientId != (uint)localClientId)
            {
                m_skipped++;
                LogProblem("target-mismatch",
                    "AMBIENT_SOUNDS_STATE 目标 clientId=" + message.TargetClientId +
                    " 与本机 " + localClientId + " 不一致 → 丢弃（不施加别人的环境音）");
                return;
            }
            if (message.Revision <= m_lastClientRevision)
            {
                m_skipped++;
                LogProblem("stale",
                    "AMBIENT_SOUNDS_STATE revision=" + message.Revision + " 不大于上次 " +
                    m_lastClientRevision + " → 丢弃（重复 / 越序 / 重放）");
                return;
            }
            if (!m_port.ApplyClientVolumes(message.Revision, in volumes, out string applyReason))
            {
                m_applyFailures++;
                LogProblem("apply:" + applyReason,
                    "环境音施加失败（revision=" + message.Revision + " " + volumes.Describe() +
                    "）：" + applyReason + " → 本机三条循环保持上一状态（累计 " +
                    m_applyFailures + " 次）");
                return;
            }
            m_lastClientRevision = message.Revision;
            m_applied++;
            if (!m_clientFirstApplyLogged)
            {
                m_clientFirstApplyLogged = true;
                Log("ambient-sound: 已收到主机首条音量 " + volumes.Describe() +
                    " → 本机 SubsystemAmbientSounds 每帧施加");
            }
        }

        // ================================================================= 日志 / 摘要

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        private string Summary() =>
            "ambientSent=" + m_sent +
            " ambientRecv=" + m_recv +
            " ambientApplied=" + m_applied +
            " ambientSkipped=" + m_skipped +
            " ambientNoAvatar=" + m_noAvatar +
            " ambientScanFailures=" + m_scanFailures +
            " ambientMalformed=" + m_malformed +
            " ambientSendFailures=" + m_sendFailures +
            " ambientEncodeFailures=" + m_encodeFailures +
            " ambientApplyFailures=" + m_applyFailures +
            " ambientDirectionErrors=" + m_directionErrors +
            " ambientIdleRounds=" + m_idleRounds +
            " ambientRounds=" + m_rounds +
            " ambientClients=" + m_lastClientCount +
            " ambientResyncs=" + m_resyncs +
            " ambientNoHostData=" + m_noHostData +
            " ambientEarlyBeforeWorld=" + m_earlyBeforeWorld +
            " ambientScanCapped=" + m_port.ScanCapped;
    }
}

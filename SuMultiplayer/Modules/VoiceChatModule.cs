using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using SuMultiplayer.Voice;
using V = SuMultiplayer.Protocol.Voice;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 实时语音（**纯逻辑 + 端口**，不引用 Game / OpenAL）：
    ///
    ///   客户端：采集 → VAD（能量 + 过零率，260 ms 挂起）→ 20 ms 帧 → 40 ms 包
    ///           → 4-bit IMA ADPCM（163 B）→ VoicePacket → 主机。
    ///           只在说话时发送（静音 / 未按 PTT 完全不发）。
    ///   主机：  校验发送者已协商 supported + mic_enabled、包格式、sequence 单调；
    ///           本机播放后按 VoiceRelayPolicy fan-out（不回发给说话者、跳过静音 /
    ///           不支持的监听者），每条中继只序列化一次。
    ///   接收：  严格解码 → 每说话者 jitter 缓冲（预灌 2 包 / 缺包等 70 ms / 迟到丢弃
    ///           / 最多 8 包）→ OpenAL 流队列播放。
    ///
    /// 协商：  VoiceState（客户端 → 主机）声明 supported / mic_enabled / reason；
    ///           VoiceRoster（主机 → 客户端）是权威名册；VoiceMuteRequest/Result
    ///           让监听者静音某个说话者，主机**真的不再中继**（省带宽）。
    ///
    /// 无静态状态；所有失败显式计数 + 限流日志（10 s）。
    /// </summary>
    public sealed class VoiceChatModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const string ModuleKey = "voice-chat";

        private sealed class SpeakerStream
        {
            public int ClientId;
            public int StreamId;
            public VoiceJitterBuffer Jitter;
            public long LastPacketMs;
            public long LastPlayedMs;
            public int Level;
            public long SeenLate;
            public long SeenGap;
            public long SeenOverflow;
            public long SeenDuplicate;
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly IVoiceAudioPort m_audio;
        private readonly VoiceOptions m_options;
        private readonly Func<string> m_localName;
        private readonly VoicePeerRoster m_roster = new VoicePeerRoster();
        private readonly VoiceMuteTable m_muteTable = new VoiceMuteTable();
        private readonly VoiceCounters m_counters = new VoiceCounters();
        private readonly VoiceActivityDetector m_vad;
        // 右下角图标的 5 格音量条：采集帧 → dBFS 归一化 → 平滑 0..1 → 0..5 格（纯逻辑，见 Voice/VoiceMicMeter.cs）。
        private readonly VoiceMicMeter m_micMeter = new VoiceMicMeter();
        // 10 s 电平窗口：mean / level 的 min·avg·max + 采集帧 vs 电平计帧的对账（诊断标尺用）。
        private readonly VoiceMicMeterWindow m_micWindow = new VoiceMicMeterWindow();
        private readonly VoicePacketizer m_packetizer = new VoicePacketizer();
        private readonly Dictionary<int, SpeakerStream> m_streams =
            new Dictionary<int, SpeakerStream>();
        private readonly HashSet<int> m_mutedSpeakers = new HashSet<int>();
        private readonly Dictionary<int, uint> m_lastSequence = new Dictionary<int, uint>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<int> m_acknowledgedStates = new HashSet<int>();
        // 声明了未知档位的端：主机明确拒绝、不再给它发普通 ack（直到它改成合法档位）。
        private readonly HashSet<int> m_rejectedFormats = new HashSet<int>();
        private readonly List<int> m_streamKeys = new List<int>(16);

        private readonly short[] m_captureScratch =
            new short[VoiceFormat.FrameSamples * 8];
        private readonly short[] m_captureRing = new short[VoiceFormat.SampleRate];
        private readonly short[] m_frameScratch = new short[VoiceFormat.FrameSamples];
        private readonly short[] m_preRoll =
            new short[VoiceFormat.FrameSamples * VoiceFormat.VadPreRollFrames];
        private readonly byte[] m_encodeBuffer = new byte[VoiceFormat.MaxEncodedBytes];
        private readonly short[] m_decodeScratch = new short[VoiceFormat.PacketSamples];

        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;

        private bool m_attached;
        private bool m_hosting;
        private bool m_wasConnected;
        // 语音输入开关（HUD / U 键的唯一状态）：进房间默认 OFF，离开房间自动 OFF。
        private bool m_voiceOn;
        // 用户已经在等权限结果（按了 ON，但系统对话框还没答复）；结果回来前重复按不叠弹窗。
        private bool m_voiceRequested;
        private bool m_permissionPending;
        private bool m_permissionGranted;
        private VoicePermissionState m_permissionState = VoicePermissionState.Unknown;
        private string m_permissionDetail = string.Empty;
        private string m_voiceNotice = string.Empty;
        private int m_permissionRequestCount;
        private bool m_pttLatch;
        private bool m_talking;
        private bool m_packetStartPending;
        private bool m_muteAll;
        private bool m_captureActive;
        private string m_captureStatus = "idle";
        private string m_captureReason = string.Empty;
        private string m_sessionVoiceReason = string.Empty;
        private bool m_sessionVoiceEnabled = true;
        private bool m_rosterDirty;
        private uint m_rosterRevision;
        private int m_sequence;
        private int m_localClientId = -1;
        private int m_captureRingCount;
        private int m_preRollCount;
        private int m_micLevel;
        private int m_sentInWindow;
        private long m_nowMs;
        private long m_nextCaptureAttemptMs;
        private long m_nextVoiceStateMs;
        private long m_lastSendWindowMs;
        private long m_lastSummaryMs;
        private long m_rateWindowStartMs;
        private long m_rateWindowSent;
        private long m_rateWindowRecv;
        private long m_rateWindowSentPackets;
        private long m_rateWindowRecvPackets;
        private string m_lastSentState;
        // 上一次向主机声明的档位（线上 id）：运行期切档时先发 VoiceState，
        // 再发新档位的包 —— 否则主机会先收到"未声明的档位"而拒绝。
        private int m_lastSentQuality = -1;

        public VoiceChatModule(IVoiceAudioPort audio, VoiceOptions options,
            Func<string> localName = null)
        {
            m_audio = audio ?? throw new ArgumentNullException(nameof(audio));
            m_options = options ?? throw new ArgumentNullException(nameof(options));
            m_localName = localName;
            m_vad = new VoiceActivityDetector(hangoverFrames: options.VadHangoverFrames);
            m_counters.SetQuality(m_options.Quality);
            // 进房间默认 OFF：不采集、不占麦克风；只有用户按 U / 点 HUD 才打开。
            m_voiceOn = false;
            m_permissionState = m_audio.IsPermissionRequired
                ? VoicePermissionState.Unknown
                : VoicePermissionState.NotRequired;
            m_descriptor = new ModuleDescriptor(ModuleKey,
                "实时语音（VAD + 可配置 4/3/2-bit ADPCM / 1-bit CVSD）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "voice.chat" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public bool IsAttached => m_attached;

        public bool IsHosting => m_hosting;

        public int LocalClientId => m_localClientId;

        public VoiceOptions Options => m_options;

        public VoiceCounters Counters => m_counters;

        public VoicePeerRoster Roster => m_roster;

        public VoiceMuteTable MuteTable => m_muteTable;

        public string CaptureStatus => m_captureStatus;

        public string CaptureReason => m_captureReason;

        /// <summary>语音输入是否打开（HUD 的 ON/OFF 状态；Android 权限未授予时保持 false）。</summary>
        public bool IsVoiceOn => m_voiceOn;

        /// <summary>兼容旧调用：等价于 <see cref="IsVoiceOn"/>（语音输入开关）。</summary>
        public bool IsMicEnabled => m_voiceOn;

        /// <summary>Android 权限请求已发出、正在等系统对话框结果。</summary>
        public bool IsPermissionPending => m_permissionPending;

        public VoicePermissionState PermissionState => m_permissionState;

        public string PermissionDetail => m_permissionDetail;

        /// <summary>HUD 的明确提示（拒绝 / 永久拒绝 / 无 Activity 等）。空 = 没有提示。</summary>
        public string VoiceNotice => m_voiceNotice;

        public bool IsCapturing => m_captureActive;

        public bool IsPushToTalk => m_options.PushToTalk;

        public bool IsPttActive => m_pttLatch;

        public bool IsConnected => m_wasConnected;

        public bool IsTalking => m_talking;

        public bool IsMutedAll => m_muteAll;

        public bool SessionVoiceEnabled => m_sessionVoiceEnabled;

        public int MicLevel => m_micLevel;

        /// <summary>
        /// 当前采集电平 0..1（按 20 ms 帧 attack/release 平滑；麦克风关 / 停采时 0）。
        /// 数据来源：每帧 <c>VoiceActivityDetector.LastLevel</c>（该帧 mean(|sample|)）→ 纯逻辑
        /// <see cref="VoiceMicMeter"/>。HUD 的 5 格音量条只读它，不参与任何发送决策。
        /// </summary>
        public float MicLevelNormalized => m_micMeter.Level;

        /// <summary>
        /// 5 格音量条点亮的格数 = round(level × 5)（0 = 静音，5 = 满格）。
        /// 语音关闭 / 采集没开时为 0：图标在 OFF 状态只显示灰底 + 红斜杠（2026-10-21 产品要求）。
        /// </summary>
        public int MicLevelBars => m_voiceOn && m_captureActive ? m_micMeter.Bars : 0;

        /// <summary>电平计本身（帧数 / 拒绝数 / 停滞衰减计数；状态行与 HUD 日志用）。</summary>
        public VoiceMicMeter MicMeter => m_micMeter;

        /// <summary>10 s 电平窗口（mean / level 的 min·avg·max + 采集帧对账；日志与自检用）。</summary>
        public VoiceMicMeterWindow MicWindow => m_micWindow;

        /// <summary>
        /// 累计：采集帧到了但**没有**推进电平计的帧数（闸门跳过 / 对账缺失）。
        /// 与 voiceCaptureFrames 一起看：它涨而 micMeter.frames 不涨时，这里会给出差多少。
        /// </summary>
        public long MeterFramesNotPushed => m_micWindow.FramesNotPushed;

        /// <summary>
        /// 累计：电平计推进的帧数**多于**采集帧的帧数（正常永远 0；出现即说明有帧绕过了采集回调）。
        /// </summary>
        public long MeterPushedWithoutCapture => m_micWindow.PushedWithoutCapture;

        public int StreamCount => m_streams.Count;

        public IReadOnlyCollection<int> MutedSpeakers => m_mutedSpeakers;

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log.Warning("[SuMP][voice] 没有传输端口（transport）→ 语音模块不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log.Warning("[SuMP][voice] 没有通道控制端口（ITransportControlPort）→ 无法判断" +
                    "主机/客户端，语音不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                try
                {
                    m_messages.Registry.RegisterModule(this);
                }
                catch (Exception e)
                {
                    Log.Error("[SuMP][voice] 注册语音消息失败：" + e.GetType().Name + " " +
                        e.Message + " → 语音不启用");
                    return;
                }
            }
            else
            {
                Log.Warning("[SuMP][voice] 没有消息注册表（IMessageRegistryPort）→ " +
                    "收不到任何语音消息，语音不启用");
                return;
            }

            m_transport.ClientJoined += OnClientJoined;
            m_transport.ClientLeft += OnClientLeft;
            m_attached = true;
            if (!m_audio.IsPlaybackAvailable)
            {
                // 模块启动时就会 Attach，即使单机/没有房间也会走到这里。
                // 播放上下文是否可用要到真正联机语音时才影响玩家；Attach 阶段只写角色日志，
                // 不把单机启动警告写进 Game.log。
                SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] playback context unavailable at attach (no multiplayer session): " +
                    m_audio.Describe());
            }
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 就绪：" + m_options.Describe() + " · " +
                VoiceFormat.SampleRate + " Hz mono16 · " +
                VoiceProfiles.Describe(m_options.Quality) + " · " + m_audio.Describe());
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.VoicePacket, V.VoicePacket.Parser,
                new DelegateMessageHandler<V.VoicePacket>(OnVoicePacket), ModuleId);
            registry.Register((int)MessageType.VoiceState, V.VoiceState.Parser,
                new DelegateMessageHandler<V.VoiceState>(OnVoiceState), ModuleId);
            registry.Register((int)MessageType.VoiceRoster, V.VoiceRoster.Parser,
                new DelegateMessageHandler<V.VoiceRoster>(OnVoiceRoster), ModuleId);
            registry.Register((int)MessageType.VoiceMuteRequest, V.VoiceMuteRequest.Parser,
                new DelegateMessageHandler<V.VoiceMuteRequest>(OnVoiceMuteRequest), ModuleId);
            registry.Register((int)MessageType.VoiceMuteResult, V.VoiceMuteResult.Parser,
                new DelegateMessageHandler<V.VoiceMuteResult>(OnVoiceMuteResult), ModuleId);
            registry.Register((int)MessageType.VoiceControl, V.VoiceControl.Parser,
                new DelegateMessageHandler<V.VoiceControl>(OnVoiceControl), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = StatusLine();
            StopPipeline();
            m_voiceOn = false;
            m_voiceRequested = false;
            m_permissionPending = false;
            if (m_transport != null)
            {
                m_transport.ClientJoined -= OnClientJoined;
                m_transport.ClientLeft -= OnClientLeft;
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); }
                catch (Exception) { }
                m_messages = null;
            }
            m_roster.Clear();
            m_muteTable.Clear();
            m_mutedSpeakers.Clear();
            m_lastSequence.Clear();
            m_rejectedFormats.Clear();
            m_streams.Clear();
            m_attached = false;
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_nowMs = (long)(tick.Now * 1000.0);
            // 音量条停滞衰减：采集停了 / 设备挂住时按时间落回 0（新帧到达会重置停滞计时）。
            m_micMeter.Advance(m_nowMs);
            // 运行期切换档位：编码按 m_options.Quality，计数与主机名册跟着更新，
            // 下一包就带新的 format 字节（接收端逐包解码）。
            if (m_counters.Quality != m_options.Quality) m_counters.SetQuality(m_options.Quality);
            bool connected = m_transport != null && m_transport.IsConnected;
            bool hosting = m_control != null && m_control.IsHosting;
            if (connected != m_wasConnected || hosting != m_hosting)
                OnConnectionChanged(connected, hosting);

            if (!connected)
            {
                UpdateRatesAndLogs();
                return;
            }

            // 运行期切档：必须在采集 / 发包之前把新档位声明给主机（TCP 有序，
            // 主机先更新该说话者的 format 才接受新档位的包）。
            if (m_lastSentQuality != (int)m_options.Quality)
            {
                m_lastSentQuality = (int)m_options.Quality;
                SendVoiceState(force: true);
                SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 编码档位切换 → " +
                    VoiceProfiles.Describe(m_options.Quality));
            }

            // Android 权限对话框的答复先落地：只有 Granted 才会真的把语音打开。
            PumpPermissionResult();

            if (!m_audio.IsPlaybackAvailable && !m_hosting && m_sessionVoiceEnabled)
            {
                m_sessionVoiceEnabled = false;
                m_sessionVoiceReason = "no-playback";
                m_counters.NoteNoPlayback();
                StopPipeline();
                SendVoiceState(force: true);
                Log.Error("[SuMP][voice] 播放上下文不可用 → 本端语音关闭（reason=no-playback）；" +
                    "这不是静默降级：HUD 与日志都会一直显示该原因");
            }

            if (m_options.Enabled && m_sessionVoiceEnabled)
            {
                m_audio.Pump();
                EnsureCapture();
                PumpCapture();
                PumpPlayback();
            }
            // 主机名册里的 host mic 必须等于"采集真的开着"，而不是用户的开关意图。
            // 只在值真的变了才置 dirty（SetHostMic 每次调用都返回 true，直接拿它当
            // "变了"会每帧广播一次名册）。
            if (m_hosting && m_roster.TryGet(0, out VoicePeerState hostPeer) &&
                hostPeer.MicEnabled != m_captureActive)
            {
                m_roster.SetHostMic(m_captureActive);
                m_rosterDirty = true;
            }
            if (m_hosting && m_roster.TryGet(0, out VoicePeerState hostFormatPeer) &&
                hostFormatPeer.Format != (int)m_options.Quality)
            {
                m_roster.SetHostFormat((int)m_options.Quality);
                m_rosterDirty = true;
            }
            // VoiceState 必须在 EnsureCapture 之后发（mic_enabled 才是真实的），
            // 且 disabled / 无权限时也要发（supported=false + reason），否则主机会一直
            // 以为本端支持语音。
            SendVoiceState(force: false);
            if (m_hosting && m_rosterDirty)
            {
                m_rosterDirty = false;
                BroadcastRoster();
            }
            UpdateRatesAndLogs();
        }

        public string StatusLine()
        {
            return "voice: " + (m_hosting ? "host" : "client") +
                " connected=" + (m_wasConnected ? "yes" : "no") +
                " session=" + (m_sessionVoiceEnabled ? "on" : "off(" + m_sessionVoiceReason + ")") +
                " voice=" + (m_voiceOn ? "on" : "off") +
                " permission=" + m_permissionState +
                (m_permissionPending ? "(pending)" : "") +
                (m_permissionDetail.Length > 0 ? "(" + m_permissionDetail + ")" : "") +
                " capture=" + m_captureStatus +
                (m_captureReason.Length > 0 ? "(" + m_captureReason + ")" : "") +
                " talking=" + (m_talking ? "yes" : "no") +
                " streams=" + m_streams.Count + " muted=" + m_mutedSpeakers.Count +
                " bars=" + MicLevelBars + "/" + VoiceMicMeter.BarCount +
                " " + m_micMeter.Describe() +
                " meterFramesNotPushed=" + m_micWindow.FramesNotPushed +
                " meterPushedWithoutCapture=" + m_micWindow.PushedWithoutCapture + " " +
                m_counters.Describe() + " · " + m_roster.Describe();
        }

        public string Describe() => StatusLine() + " · audio=" + m_audio.Describe();

        // ================================================================= 连接 / 名册

        private void OnConnectionChanged(bool connected, bool hosting)
        {
            m_wasConnected = connected;
            m_hosting = hosting;
            StopPipeline();
            m_roster.Clear();
            m_muteTable.Clear();
            m_mutedSpeakers.Clear();
            m_lastSequence.Clear();
            m_rejectedFormats.Clear();
            m_sequence = 0;
            m_packetStartPending = false;
            m_lastSentState = null;
            m_lastSentQuality = (int)m_options.Quality;
            m_nextVoiceStateMs = 0;
            m_acknowledgedStates.Clear();
            m_rosterDirty = false;
            m_muteAll = false;
            // 进 / 出房间都默认 OFF：StopPipeline 已经停采集、释放麦克风。
            m_voiceOn = false;
            m_voiceRequested = false;
            m_permissionPending = false;
            m_voiceNotice = string.Empty;
            m_captureStatus = "off";
            m_captureReason = string.Empty;

            if (!connected)
            {
                SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 连接断开 → 语音已自动 OFF，采集已停止 / 麦克风已释放，" +
                    "播放流与名册已清空");
                return;
            }

            m_localClientId = m_transport.LocalClientId;
            m_sessionVoiceEnabled = m_options.Enabled;
            m_sessionVoiceReason = string.Empty;
            m_captureStatus = "off";
            m_captureReason = string.Empty;
            m_nextCaptureAttemptMs = 0;
            if (m_hosting)
            {
                m_roster.SetHost(m_localName != null ? m_localName() : "Host");
                m_roster.SetHostMic(false);
                m_roster.SetHostFormat((int)m_options.Quality);
                m_rosterDirty = true;
            }
            // 客户端 VoiceState 由下一次 Tick 发送：默认 OFF → mic_enabled=false reason=voice-off。
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 连接建立：role=" + (m_hosting ? "host" : "client") +
                " localClientId=" + m_localClientId +
                " · 语音输入默认 OFF（按 U 或点 HUD 的 VOICE 打开）" +
                (m_permissionGranted ? " · 本会话录音权限已授权" : "") +
                " · " + m_roster.Describe());
        }

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_hosting || clientId <= 0) return;
            m_roster.Upsert(clientId, name, supported: false, micEnabled: false);
            m_lastSequence.Remove(clientId);   // 重连 / id 复用：sequence 从 1 重新开始
            m_rosterDirty = true;
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 玩家端加入：" + clientId + " name=\"" + name +
                "\" → 等待 VoiceState（未声明支持前不会收到语音包）");
        }

        private void OnClientLeft(int clientId, string reason)
        {
            if (!m_hosting || clientId <= 0) return;
            m_roster.Remove(clientId);
            m_muteTable.RemoveListener(clientId);
            m_muteTable.RemoveSpeaker(clientId);
            m_lastSequence.Remove(clientId);
            RemoveStream(clientId);
            m_acknowledgedStates.Remove(clientId);
            m_rejectedFormats.Remove(clientId);
            m_rosterDirty = true;
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 玩家端离开：" + clientId + " reason=" + reason +
                " → 名册 / 静音表 / jitter 已清");
        }

        private void BroadcastRoster()
        {
            if (!m_hosting) return;
            VoicePeerState[] peers = m_roster.Snapshot();
            var message = new V.VoiceRoster { Revision = ++m_rosterRevision };
            for (int i = 0; i < peers.Length; i++)
            {
                // 未声明支持的端不出现在客户端名册里（它们也收不到语音包）。
                if (peers[i].ClientId != 0 && !peers[i].Supported) continue;
                message.Peers.Add(new V.VoicePeer
                {
                    ClientId = peers[i].ClientId,
                    Name = peers[i].Name ?? string.Empty,
                    Supported = peers[i].Supported,
                    MicEnabled = peers[i].MicEnabled
                });
            }
            byte[] payload = message.ToByteArray();
            int wire = VoicePacketPolicy.WireBytes(payload.Length);
            for (int i = 0; i < peers.Length; i++)
            {
                VoicePeerState peer = peers[i];
                if (peer.ClientId <= 0 || !peer.Supported) continue;
                if (m_transport.TrySendTo(peer.ClientId, MessageType.VoiceRoster, payload))
                    m_counters.NoteControl(wire);
                else
                    m_counters.NoteSendFailure();
            }
        }

        // ================================================================= 采集

        private void EnsureCapture()
        {
            if (!m_options.Enabled || !m_sessionVoiceEnabled || !m_voiceOn)
            {
                if (m_audio.IsCapturing) m_audio.StopCapture();
                m_captureActive = false;
                // 没有采集就没有电平：音量条归 0（麦克风关 / 本端语音被会话关闭）。
                m_micMeter.Reset();
                if (!m_voiceOn) m_captureStatus = "off";
                return;
            }
            // ON 一定意味着权限已就绪（EnableVoice 的门槛）；这里再挡一次，
            // 保证任何路径都不会在未授权时去 open 采集设备。
            if (m_audio.IsPermissionRequired && !m_permissionGranted)
            {
                if (m_audio.IsCapturing) m_audio.StopCapture();
                m_captureActive = false;
                m_micMeter.Reset();
                m_captureStatus = "permission-required";
                return;
            }
            if (m_audio.IsCapturing)
            {
                m_captureActive = true;
                if (m_captureStatus != "capturing") m_captureStatus = "capturing";
                return;
            }
            if (m_nextCaptureAttemptMs != 0 && m_nowMs < m_nextCaptureAttemptMs) return;

            bool started = m_audio.TryStartCapture(out VoiceCaptureFailure failure,
                out string reason);
            if (!started)
            {
                m_captureActive = false;
                m_captureReason = reason ?? "unknown";
                switch (failure)
                {
                    case VoiceCaptureFailure.NoPermission:
                        m_captureStatus = "no-permission";
                        m_counters.NoteNoPermission();
                        // 权限在运行期被撤销 / 被系统拒绝：关掉开关并让下一次 ON 重新走权限流程。
                        m_voiceOn = false;
                        m_permissionGranted = false;
                        m_permissionState = VoicePermissionState.Denied;
                        m_voiceNotice = "mic permission lost — press U to retry";
                        m_nextCaptureAttemptMs = m_nowMs + 15000;
                        LogProblem("capture-no-permission",
                            "语音采集没有权限（Android 需要 RECORD_AUDIO 声明 + 运行时授权）：" +
                            m_captureReason + " → 语音保持关闭，再按 U 重新请求");
                        break;
                    case VoiceCaptureFailure.NoDevice:
                        m_captureStatus = "no-device";
                        m_counters.NoteNoDevice();
                        m_nextCaptureAttemptMs = m_nowMs + 5000;
                        LogProblem("capture-no-device",
                            "没有可用的录音设备：" + m_captureReason + " → 每 5 s 重试");
                        break;
                    case VoiceCaptureFailure.Busy:
                        m_captureStatus = "busy";
                        m_nextCaptureAttemptMs = m_nowMs + 5000;
                        LogProblem("capture-busy", "录音设备打开失败：" + m_captureReason);
                        break;
                    default:
                        m_captureStatus = "disabled";
                        m_nextCaptureAttemptMs = m_nowMs + 5000;
                        LogProblem("capture-disabled", "语音采集未启动：" + m_captureReason);
                        break;
                }
                SendVoiceState(force: true);
                return;
            }

            m_captureActive = true;
            m_captureStatus = "capturing";
            m_captureReason = string.Empty;
            m_nextCaptureAttemptMs = 0;
            m_captureRingCount = 0;
            m_preRollCount = 0;
            m_vad.Reset();
            // 采集（重新）启动：音量条从 0 格开始，上一段会话的旧电平不残留。
            m_micMeter.Reset();
            m_packetizer.Reset();
            m_talking = false;
            m_packetStartPending = false;
            if (m_hosting)
            {
                m_roster.SetHostMic(true);
                m_rosterDirty = true;
            }
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 采集已启动（" + VoiceFormat.SampleRate +
                " Hz mono16，VAD 挂起 " + m_options.VadHangoverFrames * VoiceFormat.VadFrameMs +
                " ms，档位 " + VoiceProfiles.Name(m_options.Quality) + "）");
            SendVoiceState(force: true);
        }

        private void PumpCapture()
        {
            if (!m_captureActive) return;
            int read = m_audio.ReadSamples(m_captureScratch, 0, m_captureScratch.Length,
                out string reason);
            if (reason != null)
            {
                m_captureActive = false;
                m_captureStatus = "read-failed";
                m_captureReason = reason;
                // 读失败 = 没采到任何样本：音量条归 0，不让上一帧的电平贴在图标上。
                m_micMeter.Reset();
                m_audio.StopCapture();   // 设备可能已失效：关掉后按 5 s 重试重开
                m_nextCaptureAttemptMs = m_nowMs + 5000;
                LogProblem("capture-read", "语音采集读取失败：" + reason + " → 5 s 后重开");
                SendVoiceState(force: true);
                return;
            }
            for (int i = 0; i < read; i++)
            {
                if (m_captureRingCount >= m_captureRing.Length)
                {
                    int drop = m_captureRing.Length / 4;
                    Array.Copy(m_captureRing, drop, m_captureRing, 0,
                        m_captureRingCount - drop);
                    m_captureRingCount -= drop;
                    m_counters.NoteCaptureDropped(drop);
                }
                m_captureRing[m_captureRingCount++] = m_captureScratch[i];
            }

            while (m_captureRingCount >= VoiceFormat.FrameSamples)
            {
                Array.Copy(m_captureRing, 0, m_frameScratch, 0, VoiceFormat.FrameSamples);
                Array.Copy(m_captureRing, VoiceFormat.FrameSamples, m_captureRing, 0,
                    m_captureRingCount - VoiceFormat.FrameSamples);
                m_captureRingCount -= VoiceFormat.FrameSamples;
                m_counters.NoteCaptureFrame();
                ProcessCapturedFrame();
            }
        }

        private void ProcessCapturedFrame()
        {
            VoiceFrameClass kind = m_vad.Classify(m_frameScratch, 0,
                VoiceFormat.FrameSamples);
            m_micLevel = m_vad.LastLevel;
            // 5 格音量条的唯一数据入口：只有 voice=on 且 capture=capturing 才更新电平计。
            // 权限被永久拒绝（平板 PermanentlyDenied）/ 停采时电平保持 0，Push 一次都不调，
            // micMeter 的 frames / rejected / staleDecays 也保持不变（不伪造电平）。
            // 若真的出现"有采集帧但闸门不允许"，显式记到窗口的 skipped，由 10 s 对账报出来。
            if (m_voiceOn && m_captureActive)
            {
                long framesBefore = m_micMeter.Frames;
                m_micMeter.Push(m_micLevel, m_nowMs);
                if (m_micMeter.Frames > framesBefore)
                    m_micWindow.Add(m_micLevel, m_micMeter.Level, m_micMeter.Bars);
            }
            else
            {
                m_micWindow.NoteSkipped();
            }

            bool gate = !m_options.PushToTalk || IsPttActive;
            if (!gate)
            {
                if (kind != VoiceFrameClass.Silence) m_counters.NotePttBlocked();
                EndTalkIfNeeded();
                StorePreRoll();
                return;
            }

            if (kind == VoiceFrameClass.Silence)
            {
                m_counters.NoteSilenceSkipped();
                EndTalkIfNeeded();
                StorePreRoll();
                return;
            }

            if (!m_talking)
            {
                m_talking = true;
                m_packetStartPending = true;
                m_counters.NoteStartTalk();
                // 预卷：把 speech 之前的 2 帧补进去，首音节不被切。
                for (int i = 0; i < m_preRollCount; i++)
                {
                    int offset = i * VoiceFormat.FrameSamples;
                    PushFrame(m_preRoll, offset);
                }
                m_preRollCount = 0;
            }
            PushFrame(m_frameScratch, 0);
        }

        private void StorePreRoll()
        {
            if (m_preRollCount >= VoiceFormat.VadPreRollFrames)
            {
                Array.Copy(m_preRoll, VoiceFormat.FrameSamples, m_preRoll, 0,
                    VoiceFormat.FrameSamples * (VoiceFormat.VadPreRollFrames - 1));
                m_preRollCount = VoiceFormat.VadPreRollFrames - 1;
            }
            Array.Copy(m_frameScratch, 0, m_preRoll,
                m_preRollCount * VoiceFormat.FrameSamples, VoiceFormat.FrameSamples);
            m_preRollCount++;
        }

        private void PushFrame(short[] pcm, int offset)
        {
            if (m_packetizer.Push(pcm, offset, VoiceFormat.FrameSamples))
            {
                SendPacket(m_packetizer.Buffer, m_packetStartPending, false);
                m_packetStartPending = false;
                m_packetizer.Reset();
            }
        }

        private void EndTalkIfNeeded()
        {
            if (!m_talking) return;
            m_talking = false;
            int valid = m_packetizer.FlushPadded();
            if (valid > 0)
            {
                SendPacket(m_packetizer.Buffer, m_packetStartPending, true);
                m_counters.NoteEndTalk();
            }
            m_packetizer.Reset();
            m_packetStartPending = false;
        }

        // ================================================================= 发送 / 中继

        private void SendPacket(short[] pcm, bool talkStart, bool talkEnd)
        {
            if (m_nowMs - m_lastSendWindowMs >= 1000)
            {
                m_lastSendWindowMs = m_nowMs;
                m_sentInWindow = 0;
            }
            if (m_sentInWindow >= VoiceFormat.MaxSendPacketsPerSecond)
            {
                m_counters.NoteQueueDropped();
                return;
            }
            m_sentInWindow++;

            VoiceQuality quality = m_options.Quality;
            int expectedBytes = VoiceFormat.EncodedBytes(quality);
            if (!VoiceCodec.TryEncode(quality, pcm, 0, VoiceFormat.PacketSamples,
                    m_encodeBuffer, 0, out int written, out string reason) ||
                written != expectedBytes)
            {
                m_counters.NoteEncodeFail();
                LogProblem("encode", "语音编码失败（档位 " + VoiceProfiles.Name(quality) +
                    "）：" + (reason ?? ("voice-encode-written-" + written + "/" + expectedBytes)));
                return;
            }
            m_sequence++;
            if (m_sequence <= 0) m_sequence = 1;
            var message = new V.VoicePacket
            {
                SourceClientId = 0,
                Sequence = (uint)m_sequence,
                Flags = VoicePacketPolicy.BuildFlags(talkStart, talkEnd),
                Format = VoiceProfiles.WireId(quality),
                Data = ByteString.CopyFrom(m_encodeBuffer, 0, written)
            };
            byte[] payload = message.ToByteArray();
            int wire = VoicePacketPolicy.WireBytes(payload.Length);
            if (m_hosting)
                SendHostOwnPacket(payload, wire);
            else
                SendClientPacket(payload, wire);
        }

        private void SendClientPacket(byte[] payload, int wire)
        {
            if (m_transport.TrySend(MessageType.VoicePacket, payload))
                m_counters.NoteSent(wire);
            else
                m_counters.NoteSendFailure();
        }

        private void SendHostOwnPacket(byte[] payload, int wire)
        {
            if (!m_options.RelayEnabled) return;
            VoicePeerState[] peers = m_roster.Snapshot();
            for (int i = 0; i < peers.Length; i++)
            {
                VoicePeerState peer = peers[i];
                if (peer.ClientId <= 0 || !peer.Supported) continue;
                if (m_muteTable.IsMuted(peer.ClientId, 0))
                {
                    m_counters.NoteRelayMuted();
                    continue;
                }
                if (m_transport.TrySendTo(peer.ClientId, MessageType.VoicePacket, payload))
                    m_counters.NoteRelayed(wire);
                else
                    m_counters.NoteSendFailure();
            }
        }

        private void OnVoicePacket(MessageContext context, V.VoicePacket message)
        {
            m_counters.NoteReceived(VoicePacketPolicy.WireBytes(context.PayloadBytes));
            if (m_hosting)
                HandleHostPacket(context, message);
            else
                HandleClientPacket(context, message);
        }

        private void HandleHostPacket(MessageContext context, V.VoicePacket message)
        {
            if (!m_options.Enabled)
            {
                m_counters.NoteRelayDisabled();
                return;   // 主机配置关闭语音：明确不中继（客户端会收到 VoiceControl）
            }
            int sender = context.ClientId;
            if (sender <= 0)
            {
                m_counters.NoteDirectionError();
                return;
            }
            if (!VoicePacketPolicy.TryValidate(message, fromClient: true, out string reason))
            {
                if (VoicePacketPolicy.IsFormatFailure(reason)) m_counters.NoteFormatRejected();
                else m_counters.NoteMalformed();
                LogProblem("malformed", "主机收到畸形语音包（clientId=" + sender + "）：" +
                    reason + " → 整包丢弃");
                return;
            }
            if (!m_roster.TryGet(sender, out VoicePeerState peer))
            {
                m_counters.NoteRelayDisabled();
                LogProblem("unknown-speaker-" + sender,
                    "主机收到未在名册里的语音包（clientId=" + sender + "）→ 丢弃（先完成协商）");
                return;
            }
            if (!VoiceRelayPolicy.CanAcceptSpeaker(peer))
            {
                m_counters.NoteRelayDisabled();
                LogProblem("speaker-disabled-" + sender,
                    "主机丢弃 clientId=" + sender + " 的语音包：该端未声明支持或麦克风已关闭");
                return;
            }
            // 档位必须与说话者 VoiceState 里声明的一致：主机虽然是中继方，
            // 也必须拒绝"用另一种档位编码的包"，否则监听端会解出错音。
            if (!VoiceProfiles.TryFromWireId(message.Format, out VoiceQuality packetQuality))
            {
                m_counters.NoteFormatRejected();
                LogProblem("format-unknown-" + sender,
                    "主机丢弃 clientId=" + sender + " 的语音包：未知档位 id=" + message.Format +
                    "（不猜默认档位）");
                return;
            }
            if (peer.Format == 0)
            {
                m_counters.NoteFormatRejected();
                LogProblem("format-unannounced-" + sender,
                    "主机丢弃 clientId=" + sender + " 的语音包：该端还没在 VoiceState 里声明档位");
                return;
            }
            if (peer.Format != (int)message.Format)
            {
                m_counters.NoteFormatRejected();
                LogProblem("format-mismatch-" + sender,
                    "主机丢弃 clientId=" + sender + " 的语音包：声明档位=" + peer.Format +
                    " 包内档位=" + message.Format + "（档位不一致）");
                return;
            }
            m_lastSequence.TryGetValue(sender, out uint last);
            if (!VoicePacketPolicy.IsNewer(message.Sequence, last))
            {
                m_counters.NoteLateDropped();
                return;
            }
            m_lastSequence[sender] = message.Sequence;

            if (!TryDecode(message, packetQuality, out short[] pcm, out reason))
            {
                m_counters.NoteDecodeFail();
                LogProblem("decode", "主机语音解码失败（clientId=" + sender + "）：" + reason);
                return;
            }
            EnqueueForPlayback(sender, message, pcm);

            if (!m_options.RelayEnabled) return;
            var relay = new V.VoicePacket
            {
                SourceClientId = sender,
                Sequence = message.Sequence,
                Flags = message.Flags,
                Format = message.Format,
                Data = message.Data
            };
            byte[] payload = relay.ToByteArray();
            int wire = VoicePacketPolicy.WireBytes(payload.Length);
            VoicePeerState[] peers = m_roster.Snapshot();
            for (int i = 0; i < peers.Length; i++)
            {
                VoicePeerState listener = peers[i];
                if (listener.ClientId <= 0 || listener.ClientId == sender) continue;
                if (!listener.Supported)
                {
                    m_counters.NoteRelayDisabled();
                    continue;
                }
                if (m_muteTable.IsMuted(listener.ClientId, sender))
                {
                    m_counters.NoteRelayMuted();
                    continue;
                }
                if (m_transport.TrySendTo(listener.ClientId, MessageType.VoicePacket, payload))
                    m_counters.NoteRelayed(wire);
                else
                    m_counters.NoteSendFailure();
            }
        }

        private void HandleClientPacket(MessageContext context, V.VoicePacket message)
        {
            if (!VoicePacketPolicy.TryValidate(message, fromClient: false, out string reason))
            {
                if (VoicePacketPolicy.IsFormatFailure(reason)) m_counters.NoteFormatRejected();
                else m_counters.NoteMalformed();
                LogProblem("malformed", "客户端收到畸形语音包：" + reason + " → 整包丢弃");
                return;
            }
            // TryValidate 已保证是已知档位；这里显式解析（不把 uint 强转成 enum）。
            if (!VoiceProfiles.TryFromWireId(message.Format, out VoiceQuality packetQuality))
            {
                m_counters.NoteFormatRejected();
                LogProblem("format-unknown-" + message.Format,
                    "客户端收到未知档位 id=" + message.Format +
                    " 的语音包 → 整包丢弃（不猜默认档位）");
                return;
            }
            int sender = message.SourceClientId;
            if (sender == m_localClientId)
            {
                m_counters.NoteDirectionError();
                return;
            }
            if (m_mutedSpeakers.Contains(sender))
            {
                m_counters.NoteMutedDropped();
                return;
            }
            if (sender != 0 && !m_roster.Contains(sender))
            {
                // 名册还没到 / 主机刚转发：用占位名继续播（音频本身是合法的），
                // 名册到达后会被主机权威信息覆盖。
                m_roster.Upsert(sender, "Player " + sender, supported: true, micEnabled: true);
            }
            if (!TryDecode(message, packetQuality, out short[] pcm, out reason))
            {
                m_counters.NoteDecodeFail();
                LogProblem("decode", "客户端语音解码失败（source=" + sender + "）：" + reason);
                return;
            }
            EnqueueForPlayback(sender, message, pcm);
        }

        private bool TryDecode(V.VoicePacket message, VoiceQuality quality, out short[] pcm,
            out string reason)
        {
            byte[] data = message.Data.ToByteArray();
            bool ok = VoiceCodec.TryDecode(quality, data, 0, data.Length, m_decodeScratch, 0,
                VoiceFormat.PacketSamples, out reason);
            pcm = m_decodeScratch;
            return ok;
        }

        private void EnqueueForPlayback(int speakerId, V.VoicePacket message, short[] pcm)
        {
            if (!m_audio.IsPlaybackAvailable) return;   // 无头主机：只中继，不假装能播
            if (m_mutedSpeakers.Contains(speakerId)) return;   // 本机静音：不排队、不占用流
            SpeakerStream stream = GetOrCreateStream(speakerId, out string reason);
            if (stream == null)
            {
                m_counters.NoteNoPlayback();
                LogProblem("open-stream", "打开语音播放流失败（speaker=" + speakerId + "）：" +
                    reason);
                return;
            }
            if ((message.Flags & VoicePacketFlags.TalkStart) != 0) stream.Jitter.Restart();
            VoiceJitterResult result = stream.Jitter.Enqueue(message.Sequence, pcm, 0,
                VoiceFormat.PacketSamples, m_nowMs);
            switch (result)
            {
                case VoiceJitterResult.Queued:
                    stream.LastPacketMs = m_nowMs;
                    stream.Level = ComputeLevel(pcm, VoiceFormat.PacketSamples);
                    break;
                case VoiceJitterResult.Late:
                    m_counters.NoteLateDropped();
                    break;
                case VoiceJitterResult.Duplicate:
                    m_counters.NoteDuplicate();
                    break;
                case VoiceJitterResult.Overflow:
                    m_counters.NoteOverflow();
                    break;
                default:
                    m_counters.NoteMalformed();
                    break;
            }
        }

        private SpeakerStream GetOrCreateStream(int speakerId, out string reason)
        {
            if (m_streams.TryGetValue(speakerId, out SpeakerStream existing))
            {
                reason = null;
                return existing;
            }
            int streamId = m_audio.OpenStream(out reason);
            if (streamId <= 0) return null;
            var stream = new SpeakerStream
            {
                ClientId = speakerId,
                StreamId = streamId,
                Jitter = new VoiceJitterBuffer(),
                LastPacketMs = m_nowMs,
                LastPlayedMs = -1000000
            };
            m_streams[speakerId] = stream;
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 打开播放流 speaker=" + speakerId + " stream=" +
                streamId + "（jitter 预灌 " + VoiceFormat.JitterPrefillPackets +
                " 包 / 缺包等 " + VoiceFormat.JitterGapWaitMs + " ms）");
            return stream;
        }

        private void RemoveStream(int speakerId)
        {
            if (!m_streams.TryGetValue(speakerId, out SpeakerStream stream)) return;
            m_streams.Remove(speakerId);
            m_audio.CloseStream(stream.StreamId);
        }

        // ================================================================= 播放

        private void PumpPlayback()
        {
            m_streamKeys.Clear();
            foreach (int key in m_streams.Keys) m_streamKeys.Add(key);
            for (int i = 0; i < m_streamKeys.Count; i++)
            {
                int key = m_streamKeys[i];
                if (!m_streams.TryGetValue(key, out SpeakerStream stream)) continue;
                int guard = 0;
                while (guard++ < 2 &&
                       stream.Jitter.TryDequeue(m_nowMs, out short[] pcm, out int samples))
                {
                    float gain = Math.Max(0f, Math.Min(1f, m_options.PlaybackVolume));
                    if (m_audio.TrySubmit(stream.StreamId, pcm, 0, samples, gain,
                            out string reason))
                    {
                        m_counters.NotePlayed();
                        stream.LastPlayedMs = m_nowMs;
                        stream.Level = ComputeLevel(pcm, samples);
                    }
                    else
                    {
                        m_counters.NoteQueueDropped();
                        LogProblem("submit", "语音播放提交失败（speaker=" + key + "）：" +
                            reason + " → 丢弃该流，下一包重开（上下文丢失 / 队列满）");
                        m_streams.Remove(key);
                        m_audio.CloseStream(stream.StreamId);
                        break;
                    }
                }
                SyncJitterCounters(stream);
                if (m_nowMs - stream.LastPacketMs > 5000 && stream.Jitter.Count == 0)
                {
                    m_streams.Remove(key);
                    m_audio.CloseStream(stream.StreamId);
                }
            }
        }

        private void SyncJitterCounters(SpeakerStream stream)
        {
            long late = stream.Jitter.LateDropped;
            if (late > stream.SeenLate)
            {
                m_counters.NoteLateDropped(late - stream.SeenLate);
                stream.SeenLate = late;
            }
            long gap = stream.Jitter.GapSkipped;
            if (gap > stream.SeenGap)
            {
                m_counters.NoteGapSkipped(gap - stream.SeenGap);
                stream.SeenGap = gap;
            }
            long overflow = stream.Jitter.OverflowDropped;
            if (overflow > stream.SeenOverflow)
            {
                m_counters.NoteOverflowBy(overflow - stream.SeenOverflow);
                stream.SeenOverflow = overflow;
            }
            long duplicate = stream.Jitter.DuplicateDropped;
            if (duplicate > stream.SeenDuplicate)
            {
                m_counters.NoteDuplicateBy(duplicate - stream.SeenDuplicate);
                stream.SeenDuplicate = duplicate;
            }
        }

        private static int ComputeLevel(short[] pcm, int count)
        {
            if (pcm == null || count <= 0) return 0;
            long sum = 0;
            for (int i = 0; i < count; i++)
            {
                int sample = pcm[i];
                sum += sample < 0 ? -sample : sample;
            }
            return (int)(sum / count);
        }

        // ================================================================= 状态 / 静音

        private void SendVoiceState(bool force)
        {
            if (m_hosting || m_transport == null) return;
            bool mic = m_options.Enabled && m_sessionVoiceEnabled && m_voiceOn &&
                m_captureActive;
            string reason;
            if (!m_options.Enabled) reason = "disabled";
            else if (!m_sessionVoiceEnabled) reason = m_sessionVoiceReason;
            else if (m_permissionPending) reason = "permission-pending";
            else if (!m_voiceOn) reason = m_voiceNotice.Length > 0 ? m_voiceNotice : "voice-off";
            else if (mic) reason = string.Empty;
            else reason = m_captureStatus;
            string signature = (m_options.Enabled ? "1" : "0") +
                (m_sessionVoiceEnabled ? "1" : "0") + (m_voiceOn ? "1" : "0") +
                (m_captureActive ? "1" : "0") + (m_options.PushToTalk ? "1" : "0") +
                (m_permissionPending ? "1" : "0") +
                ((int)m_options.Quality).ToString(CultureInfo.InvariantCulture) + "|" + reason;
            if (!force && signature == m_lastSentState && m_nowMs < m_nextVoiceStateMs)
                return;
            m_lastSentState = signature;
            m_nextVoiceStateMs = m_nowMs + 5000;
            var message = new V.VoiceState
            {
                Supported = m_options.Enabled && m_sessionVoiceEnabled,
                MicEnabled = mic,
                PushToTalk = m_options.PushToTalk,
                Reason = reason ?? string.Empty,
                Format = VoiceProfiles.WireId(m_options.Quality)
            };
            byte[] payload = message.ToByteArray();
            if (m_transport.TrySend(MessageType.VoiceState, payload))
                m_counters.NoteControl(VoicePacketPolicy.WireBytes(payload.Length));
            else
                m_counters.NoteSendFailure();
        }

        private void OnVoiceState(MessageContext context, V.VoiceState message)
        {
            if (!m_hosting)
            {
                m_counters.NoteDirectionError();
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0)
            {
                m_counters.NoteDirectionError();
                return;
            }
            m_roster.TryGet(clientId, out VoicePeerState previous);
            string name = previous.Name;
            if (!VoiceProfiles.TryFromWireId(message.Format, out VoiceQuality announcedQuality))
            {
                // 未知 / 未声明档位：明确拒绝这一个说话者（不是整场语音），
                // 并把原因回给客户端；绝不按默认档位解码它的包。
                m_counters.NoteFormatRejected();
                m_rejectedFormats.Add(clientId);
                m_acknowledgedStates.Add(clientId);   // 抑制普通 ack，避免覆盖拒绝原因
                m_roster.Upsert(clientId, name, supported: false, micEnabled: false,
                    format: (int)message.Format);
                m_rosterDirty = true;
                LogProblem("format-announce-" + clientId,
                    "VoiceState 的档位 id=" + message.Format + " 未知（clientId=" + clientId +
                    "）→ 拒绝该端的语音；回 VoiceControl 说明原因");
                var rejected = new V.VoiceControl
                {
                    Enabled = false,
                    Reason = "voice-format-unsupported-" + message.Format
                };
                SendToPeer(clientId, MessageType.VoiceControl, rejected.ToByteArray());
                return;
            }
            if (m_rejectedFormats.Remove(clientId))
                m_acknowledgedStates.Remove(clientId);   // 改成合法档位 → 重新发正常 ack
            bool changed = !previous.Supported || previous.MicEnabled != message.MicEnabled ||
                message.Supported != previous.Supported ||
                previous.Format != (int)message.Format;
            m_roster.Upsert(clientId, name, message.Supported,
                message.Supported && message.MicEnabled, (int)message.Format);
            if (changed)
            {
                m_rosterDirty = true;
                SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] VoiceState clientId=" + clientId +
                    " supported=" + message.Supported + " mic=" + message.MicEnabled +
                    " ptt=" + message.PushToTalk + " quality=" +
                    VoiceProfiles.Name(announcedQuality) +
                    (string.IsNullOrEmpty(message.Reason) ? "" : " reason=" + message.Reason));
            }
            if (m_acknowledgedStates.Add(clientId))
            {
                var control = new V.VoiceControl
                {
                    Enabled = m_options.Enabled,
                    Reason = m_options.Enabled ? string.Empty : "host-voice-disabled"
                };
                SendToPeer(clientId, MessageType.VoiceControl, control.ToByteArray());
            }
        }

        private void OnVoiceRoster(MessageContext context, V.VoiceRoster message)
        {
            if (m_hosting)
            {
                m_counters.NoteDirectionError();
                return;
            }
            m_roster.Clear();
            for (int i = 0; i < message.Peers.Count; i++)
            {
                V.VoicePeer peer = message.Peers[i];
                if (peer.ClientId < 0) continue;
                m_roster.Upsert(peer.ClientId, peer.Name, peer.Supported, peer.MicEnabled);
            }
            m_rosterRevision = message.Revision;
            m_localClientId = m_transport.LocalClientId;
            var stale = new List<int>(4);
            foreach (int muted in m_mutedSpeakers)
                if (!m_roster.Contains(muted)) stale.Add(muted);
            for (int i = 0; i < stale.Count; i++) m_mutedSpeakers.Remove(stale[i]);
        }

        private void OnVoiceMuteRequest(MessageContext context, V.VoiceMuteRequest message)
        {
            if (!m_hosting)
            {
                m_counters.NoteDirectionError();
                return;
            }
            m_counters.NoteMuteRequest();
            int listener = context.ClientId;
            int target = message.TargetClientId;
            bool accepted;
            string reason;
            if (!m_roster.Contains(target))
            {
                accepted = false;
                reason = "voice-mute-unknown-" + target;
            }
            else
            {
                accepted = m_muteTable.Set(listener, target, message.Muted, out reason);
            }
            if (!accepted) m_counters.NoteMuteRejected();
            var result = new V.VoiceMuteResult
            {
                TargetClientId = target,
                Muted = message.Muted,
                Accepted = accepted,
                Reason = reason ?? string.Empty
            };
            SendToPeer(listener, MessageType.VoiceMuteResult, result.ToByteArray());
            if (accepted)
                SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 静音表：listener=" + listener + " speaker=" +
                    target + " muted=" + message.Muted + "（" +
                    (message.Muted ? "不再中继" : "恢复中继") + "）");
        }

        private void OnVoiceMuteResult(MessageContext context, V.VoiceMuteResult message)
        {
            if (m_hosting)
            {
                m_counters.NoteDirectionError();
                return;
            }
            if (message.Accepted)
            {
                if (message.Muted) m_mutedSpeakers.Add(message.TargetClientId);
                else m_mutedSpeakers.Remove(message.TargetClientId);
                return;
            }
            m_counters.NoteMuteRejected();
            // 乐观更新失败 → 回滚（UI 不会显示"已静音"却还在响）。
            if (message.Muted) m_mutedSpeakers.Remove(message.TargetClientId);
            else m_mutedSpeakers.Add(message.TargetClientId);
            LogProblem("mute-rejected", "主机拒绝了静音请求：target=" +
                message.TargetClientId + " reason=" + message.Reason);
        }

        private void OnVoiceControl(MessageContext context, V.VoiceControl message)
        {
            if (m_hosting)
            {
                m_counters.NoteDirectionError();
                return;
            }
            m_sessionVoiceEnabled = message.Enabled && m_options.Enabled;
            m_sessionVoiceReason = m_sessionVoiceEnabled
                ? string.Empty
                : (string.IsNullOrEmpty(message.Reason) ? "host-disabled" : message.Reason);
            if (!m_sessionVoiceEnabled)
            {
                StopPipeline();
                SendVoiceState(force: true);
                Log.Warning("[SuMP][voice] 主机禁用语音（" + m_sessionVoiceReason +
                    "）→ 本端停止采集 / 播放");
            }
        }

        private void SendToPeer(int clientId, MessageType type, byte[] payload)
        {
            if (m_transport.TrySendTo(clientId, type, payload))
                m_counters.NoteControl(VoicePacketPolicy.WireBytes(payload.Length));
            else
                m_counters.NoteSendFailure();
        }

        // ================================================================= HUD / 输入

        /// <summary>
        /// 唯一的语音 ON/OFF 开关（HUD 的 VOICE 按钮 / Windows U 键 / 触摸都走这里）。
        /// 调用方保证"一次按键 = 一次调用"（U 键的边沿在 VoiceToggleGate 里判定）。
        ///
        /// 打开时**必须先过 Android 录音权限**：
        ///   · 本会话已授权 → 直接开，不再弹窗；
        ///   · 未授权 → 发一次系统请求并等结果；被拒绝则保持 OFF（不启动采集），
        ///     用户再按一次会重新请求（不会永久锁死）；
        ///   · 永久拒绝（"不再询问"）→ 明确提示 + 打开应用设置页；
        ///   · Windows（不需要运行时权限）→ 直接开。
        /// 关闭时立刻停采集 / 释放麦克风。
        /// </summary>
        public void ToggleVoice()
        {
            if (!m_attached) return;
            if (!m_options.Enabled)
            {
                LogProblem("voice-toggle-disabled",
                    "语音在配置里关闭（voice=false）→ 语音开关不生效");
                return;
            }
            if (!m_wasConnected)
            {
                LogProblem("voice-toggle-not-in-room",
                    "语音开关忽略：当前不在联机房间内（进房间默认 OFF）");
                return;
            }
            if (m_voiceOn)
            {
                DisableVoice("user-off");
                return;
            }
            EnableVoice();
        }

        /// <summary>把语音切到 ON 的入口：权限已就绪才真正打开。</summary>
        private void EnableVoice()
        {
            if (m_permissionPending)
            {
                LogProblem("voice-permission-pending",
                    "录音权限对话框还在等结果 → 本次按键不叠加请求（避免重复弹窗）");
                return;
            }
            if (m_audio.IsPermissionRequired && !m_permissionGranted)
            {
                VoicePermissionState state = m_audio.QueryPermission(out string detail);
                m_permissionDetail = detail ?? string.Empty;
                m_permissionState = state;
                switch (state)
                {
                    case VoicePermissionState.Granted:
                        m_permissionGranted = true;
                        break;
                    case VoicePermissionState.PermanentlyDenied:
                        m_counters.NotePermissionPermanentlyDenied();
                        m_voiceNotice = "mic permission blocked — enable it in Settings";
                        LogProblem("voice-permission-permanent",
                            "录音权限被永久拒绝（系统不会再弹窗；" + m_permissionDetail +
                            "）→ 语音保持 OFF，打开应用设置页；开启后按 U 即可");
                        OpenSettingsForPermission();
                        SendVoiceState(force: true);
                        return;
                    case VoicePermissionState.Unavailable:
                        m_counters.NotePermissionUnavailable();
                        m_voiceNotice = "mic permission unavailable — " + m_permissionDetail;
                        LogProblem("voice-permission-unavailable",
                            "录音权限不可用（拿不到 Activity / 权限对象）：" + m_permissionDetail +
                            " → 语音保持 OFF（明确失败，不假装能录音）");
                        SendVoiceState(force: true);
                        return;
                    default:
                        StartPermissionRequest();
                        SendVoiceState(force: true);
                        return;
                }
            }
            TurnVoiceOnNow();
        }

        /// <summary>发起一次系统权限请求（异步；结果在 Tick 里由 PumpPermissionResult 取）。</summary>
        private void StartPermissionRequest()
        {
            if (!m_audio.RequestPermission(out string reason))
            {
                m_permissionState = VoicePermissionState.Unavailable;
                m_permissionDetail = reason ?? "request-failed";
                m_counters.NotePermissionUnavailable();
                m_voiceNotice = "mic permission unavailable — " + m_permissionDetail;
                LogProblem("voice-permission-request-failed",
                    "发起录音权限请求失败：" + m_permissionDetail + " → 语音保持 OFF");
                return;
            }
            m_permissionPending = true;
            m_voiceRequested = true;
            m_permissionRequestCount++;
            m_captureStatus = "permission-required";
            m_voiceNotice = "requesting mic permission…";
            m_counters.NotePermissionRequested();
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 已请求 Android 录音权限（本次会话第 " +
                m_permissionRequestCount + " 次）；授权前不会启动采集");
        }

        /// <summary>取一次权限结果并落到显式状态（拒绝 / 永久拒绝 / 授权各有计数与日志）。</summary>
        private void PumpPermissionResult()
        {
            if (!m_audio.TryTakePermissionResult(out VoicePermissionState state,
                    out string detail))
                return;
            m_permissionPending = false;
            m_permissionState = state;
            m_permissionDetail = detail ?? string.Empty;
            switch (state)
            {
                case VoicePermissionState.Granted:
                    m_permissionGranted = true;
                    m_counters.NotePermissionGranted();
                    m_voiceNotice = string.Empty;
                    SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 录音权限已授权（本会话后续开关不再弹窗）");
                    if (m_voiceRequested) TurnVoiceOnNow(sendState: false);
                    break;
                case VoicePermissionState.PermanentlyDenied:
                    m_voiceRequested = false;
                    m_counters.NotePermissionPermanentlyDenied();
                    m_voiceNotice = "mic permission blocked — enable it in Settings";
                    LogProblem("voice-permission-permanent",
                        "录音权限被永久拒绝（" + m_permissionDetail +
                        "）→ 语音保持 OFF；打开应用设置页，开启麦克风权限后按 U 即可");
                    OpenSettingsForPermission();
                    break;
                default:
                    m_voiceRequested = false;
                    m_permissionGranted = false;
                    m_counters.NotePermissionDenied();
                    m_voiceNotice = "mic permission denied — press U to retry";
                    LogProblem("voice-permission-denied",
                        "录音权限被拒绝（" + m_permissionDetail +
                        "）→ 语音保持 OFF，不启动采集；再按 U 会重新请求（不永久锁死）");
                    break;
            }
            SendVoiceState(force: true);
        }

        private void TurnVoiceOnNow(bool sendState = true)
        {
            m_voiceOn = true;
            m_voiceRequested = false;
            m_permissionPending = false;
            m_voiceNotice = string.Empty;
            m_captureStatus = "idle";
            m_captureReason = string.Empty;
            m_nextCaptureAttemptMs = 0;
            if (sendState) SendVoiceState(force: true);
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 语音输入 ON（权限=" + m_permissionState +
                "）→ 下一拍启动采集");
        }

        private void DisableVoice(string reason)
        {
            m_voiceOn = false;
            m_voiceRequested = false;
            m_voiceNotice = string.Empty;
            EndTalkIfNeeded();
            if (m_audio.IsCapturing) m_audio.StopCapture();
            m_captureActive = false;
            m_captureStatus = "off";
            m_captureReason = string.Empty;
            m_nextCaptureAttemptMs = 0;
            if (m_hosting)
            {
                m_roster.SetHostMic(false);
                m_rosterDirty = true;
            }
            SendVoiceState(force: true);
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 语音输入 OFF（" + reason +
                "）：采集已停止 / 麦克风已释放" +
                (m_permissionPending
                    ? "（系统权限对话框仍由 Android 管理；结果返回后只记录状态，不会自动开麦）"
                    : ""));
        }

        /// <summary>永久拒绝时打开本应用的系统设置页（用户在那边开启权限后按 U 即可）。</summary>
        private void OpenSettingsForPermission()
        {
            if (m_audio.OpenAppSettings(out string reason))
            {
                m_counters.NotePermissionSettingsOpened();
                LogProblem("voice-permission-settings",
                    "已打开应用设置页（ACTION_APPLICATION_DETAILS_SETTINGS）：开启麦克风权限后按 U 重新打开语音");
            }
            else
            {
                m_counters.NotePermissionUnavailable();
                LogProblem("voice-permission-settings-failed",
                    "打开应用设置页失败：" + (reason ?? "unknown") +
                    " → 只能让用户手动到系统设置里给本应用开启麦克风权限");
            }
        }

        /// <summary>
        /// 触摸设备的 TALK 按钮：点一下开始 / 再点一下结束（没有按住语义）。
        /// **没有键盘按住路径**：V 键不绑定任何语音功能（产品要求，2026-10-19）。
        /// </summary>
        public void TogglePttLatch()
        {
            m_pttLatch = !m_pttLatch;
            if (!IsPttActive) EndTalkIfNeeded();
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] TALK " + (m_pttLatch ? "开" : "关") +
                "（PTT 模式=" + m_options.PushToTalk + "）");
        }

        public void ToggleMuteAll()
        {
            m_muteAll = !m_muteAll;
            VoicePeerState[] peers = m_roster.Snapshot();
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i].ClientId == m_localClientId) continue;
                if (m_hosting && peers[i].ClientId == 0) continue;
                SetMutePeer(peers[i].ClientId, m_muteAll);
            }
            SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] " + (m_muteAll ? "静音所有远端" : "取消静音所有远端") +
                "（muted=" + m_mutedSpeakers.Count + "）");
        }

        public bool IsPeerMuted(int clientId) => m_mutedSpeakers.Contains(clientId);

        public void TogglePeerMute(int clientId) =>
            SetMutePeer(clientId, !m_mutedSpeakers.Contains(clientId));

        public void SetMutePeer(int clientId, bool muted)
        {
            if (clientId < 0) return;
            if (clientId == m_localClientId) return;
            if (m_hosting && clientId == 0) return;
            if (muted)
            {
                m_mutedSpeakers.Add(clientId);
                // 立即生效：丢掉该说话者已排队的包 / 流（否则静音后还会响完缓冲）。
                RemoveStream(clientId);
            }
            else
            {
                m_mutedSpeakers.Remove(clientId);
            }
            if (!m_hosting)
            {
                var request = new V.VoiceMuteRequest
                {
                    TargetClientId = clientId,
                    Muted = muted
                };
                m_counters.NoteMuteRequest();
                SendToHost(MessageType.VoiceMuteRequest, request);
            }
        }

        private void SendToHost<T>(MessageType type, T message) where T : IMessage<T>
        {
            byte[] payload = message.ToByteArray();
            if (m_transport.TrySend(type, payload))
                m_counters.NoteControl(VoicePacketPolicy.WireBytes(payload.Length));
            else
                m_counters.NoteSendFailure();
        }

        /// <summary>
        /// 状态文字（诊断 / 日志用；不分配大字符串，只在 ON/OFF 切换时调一次）。
        /// **右上角状态面板的状态行与 SPEAKING 行都已按产品要求删除（2026-10-21）**，
        /// 这段文字只出现在 `[SuMP][ui] 语音图标状态更新` 日志里，HUD 上只剩图标 + TALK（仅 ptt）。
        /// </summary>
        public string HudText()
        {
            if (!m_attached) return "VOICE: off";
            if (!m_wasConnected) return "VOICE: offline";
            if (!m_options.Enabled) return "VOICE: disabled (config)";
            if (!m_sessionVoiceEnabled)
                return "VOICE: off (" + m_sessionVoiceReason + ")";
            if (m_permissionPending)
                return "VOICE: off · requesting mic permission…";
            if (!m_voiceOn)
                return m_voiceNotice.Length > 0
                    ? "VOICE: off · " + m_voiceNotice
                    : "VOICE: off · press U";
            string mic = m_captureActive
                ? "MIC ON" + (m_talking ? " ●" : "")
                : "MIC " + m_captureStatus;
            return "VOICE: on · " + mic + " · " + (m_hosting ? "host" : "client") +
                " · peers " + Math.Max(0, m_roster.Count - 1) +
                (m_muteAll ? " · ALL MUTED" : "");
        }

        /// <summary>
        /// SPEAKING 状态文字（含本机 "You" / 远端名字）：HUD 不再显示这一行（产品要求 2026-10-21），
        /// 只由 <c>VoiceHud.UpdateSpeakingLog</c> 按 1 s 采样写日志 / speakingChanges 计数。
        /// </summary>
        public string SpeakingText(long nowMs)
        {
            string text = string.Empty;
            if (m_talking) text = "You";
            foreach (KeyValuePair<int, SpeakerStream> pair in m_streams)
            {
                if (nowMs - pair.Value.LastPlayedMs > 400) continue;
                if (m_mutedSpeakers.Contains(pair.Key)) continue;
                string name = NameOf(pair.Key);
                text = text.Length == 0 ? name : text + ", " + name;
            }
            return text.Length == 0 ? string.Empty : "SPEAKING: " + text;
        }

        private string NameOf(int clientId)
        {
            if (m_roster.TryGet(clientId, out VoicePeerState peer) &&
                !string.IsNullOrEmpty(peer.Name))
                return peer.Name;
            return clientId == 0 ? "Host" : "Player " + clientId;
        }

        // ================================================================= 速率 / 日志

        private void UpdateRatesAndLogs()
        {
            m_counters.UpdateRates(m_nowMs, ref m_rateWindowStartMs, ref m_rateWindowSent,
                ref m_rateWindowRecv, ref m_rateWindowSentPackets,
                ref m_rateWindowRecvPackets);
            if (m_nowMs - m_lastSummaryMs >= VoiceFormat.SummaryLogMs)
            {
                m_lastSummaryMs = m_nowMs;
                // 没有联机会话 / 没开语音时不写 10s 状态行：否则单机 Game.log 每 10 秒
                // 都出现一条巨长的 voice=off 计数器行，用户会以为联机模块在持续工作。
                bool worthLogging = m_wasConnected || m_voiceOn || m_permissionPending;
                if (worthLogging)
                {
                    SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] " + StatusLine());
                    LogMicWindow();
                }
            }
        }

        /// <summary>
        /// 10 s 电平窗口日志（与状态行同一节奏，绝不每帧打）：
        /// 原始采集均值 mean(min/avg/max) + 平滑电平 level(min/avg/max) + 格数(min/max)，
        /// 外加 dBFS 标尺常量与采集帧 / 电平计帧的对账计数 —— 下次调标尺直接看这一行。
        /// 本窗口没有任何采集帧时不写（没开麦不刷日志），但照样推进对账基线。
        /// </summary>
        private void LogMicWindow()
        {
            m_micWindow.Reconcile(m_counters.CaptureFrames, m_micMeter.Frames,
                m_micMeter.Rejected);
            bool active = m_micWindow.HasSamples || m_micWindow.LastCaptureDelta != 0 ||
                m_micWindow.LastMeterDelta != 0 || m_micWindow.LastRejectedDelta != 0 ||
                m_micWindow.LastSkippedDelta != 0;
            if (active)
            {
                SuMultiplayer.Engine.ModLog.Info("[SuMP][voice] 电平窗口 " + m_micWindow.DescribeWindow() +
                    " captureDelta=" + m_micWindow.LastCaptureDelta +
                    " meterDelta=" + m_micWindow.LastMeterDelta +
                    " rejectedDelta=" + m_micWindow.LastRejectedDelta +
                    " gateSkipped=" + m_micWindow.LastSkippedDelta +
                    " meterFramesNotPushed=" + m_micWindow.FramesNotPushed +
                    " meterPushedWithoutCapture=" + m_micWindow.PushedWithoutCapture +
                    " floorDbfs=" + DbfsLabel(VoiceMicMeter.FloorDbfs) +
                    " fullDbfs=" + DbfsLabel(VoiceMicMeter.FullDbfs));
            }
            m_micWindow.ResetWindow();
        }

        /// <summary>dBFS 常量写进日志时固定用不变文化（不受机器区域的小数点影响）。</summary>
        private static string DbfsLabel(double value) =>
            value.ToString("0.#", CultureInfo.InvariantCulture);

        private void StopPipeline()
        {
            if (m_audio.IsCapturing) m_audio.StopCapture();
            m_captureActive = false;
            m_captureStatus = "off";
            m_captureRingCount = 0;
            m_preRollCount = 0;
            m_talking = false;
            m_packetStartPending = false;
            m_packetizer.Reset();
            m_vad.Reset();
            // 停采 = 没有电平：5 格音量条立刻归 0（不把旧电平留在图标上）。
            m_micMeter.Reset();
            m_pttLatch = false;
            m_streamKeys.Clear();
            foreach (KeyValuePair<int, SpeakerStream> pair in m_streams)
                m_audio.CloseStream(pair.Value.StreamId);
            m_streams.Clear();
        }

        private void LogProblem(string key, string message)
        {
            long last;
            if (m_lastProblemMs.TryGetValue(key, out last) &&
                m_nowMs - last < VoiceFormat.ProblemLogMs)
                return;
            m_lastProblemMs[key] = m_nowMs;
            Log.Warning("[SuMP][voice] " + message);
        }
    }
}

using System;
using System.Collections.Generic;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using Proto = SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 自定义皮肤资源同步模块：**主机权威**的资源登记 + 广播，客户端只读副本缓存。
    ///
    /// 需求（用户口径逐条对应）：
    ///   ① 协议：客户端 → 主机上传自定义皮肤资源（有界、DEFLATE、分片），主机 → 其他客户端广播；
    ///      另有"皮肤已变化"通知携带新皮肤名 / 哈希 / 明确结果（accepted / rejected / missing）。
    ///   ② 首次加入：角色创建屏把自定义皮肤资源随资料一起交上来；客户端在握手后上传，
    ///      主机写进**世界作用域**的角色记录（SuMultiplayerPlayers.xml），再广播给其他客户端。
    ///   ③ 中途换肤：本机立刻应用 → 上传 → 主机校验（大小/格式/哈希）→ 广播 → 其他客户端应用；
    ///      主机拒绝时给所有者明确原因，所有者回退到主机确认过的资料（不是静默失败）。
    ///   ④ 防御：超限 / 结构非法 / 哈希不符 / 未知发送方 / 记录写入失败全部显式拒绝并计数；
    ///      主机只在**确实无法提供**资源时（主机自己的皮肤库缺文件 / 角色记录缺资源）发 MISSING，
    ///      其他客户端显示**确定性占位**并计数（宿主知道没发出去）。
    ///   ⑤ 不写全局皮肤库：本模块只通过 <see cref="IPlayerSkinPort"/> 读本机皮肤文件；
    ///      收到的资源只在内存副本缓存里（客户端重装 / 新客户端不会被污染）。
    ///   ⑥ 计数器（10 秒限流日志）：skinAssetSent / Recv / Applied / Rejected(reason) /
    ///      Malformed / Oversize / HashMismatch / PlaceholderShown，以及产品口径 2026-10-21 的
    ///      uploadAccepted / uploadRejected / assetForwarded / forwardExcludedOrigin / ackSent /
    ///      ackFailures / tooLarge（见 <see cref="SkinAssetCounters"/>）。
    ///   ⑦ 用户口径 2026-10-21（主机广播皮肤）：
    ///      · **不接受"主机没有这个皮肤就拒绝"**：客户端上传的 `.scskin` 只要通过 SkinAssetPolicy
    ///        校验，主机就接受、写 PlayerSkinStore + 身份记录，再转发给其他客户端；
    ///      · **资源绝不回传给换皮肤的那个客户端**：转发走
    ///        <see cref="IPeerBroadcastPort.BroadcastToPlayersExcept"/>，发起者只收
    ///        <see cref="ITransportPort.TrySendTo"/> 单发的一条回执（accepted / rejected + 原因）；
    ///      · 哈希已存在 → 不重传资源，只给发起者 accepted 回执 + 给其他端一条引用该 sha256 的
    ///        皮肤变化通知；
    ///      · 主机自己换皮肤 = 没有"发起者"例外，广播给所有客户端（excludeClientId = -1）。
    ///
    /// 模块不引用 Game：引擎动作（读文件 / 解码 / 注入贴图 / 写记录）都在端口实现里，
    /// 因此自检可以直接编译本文件并用真实 TCP 跑完整流程。
    /// </summary>
    public sealed class PlayerSkinModule : IMultiplayerModule, IEditPlayerSkinSink, IProfileSkinSyncPort
    {
        private const int HostSlotKey = 0;

        private sealed class Peer
        {
            public int ClientId;
            public string Identity = string.Empty;
            public string Name = string.Empty;
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly SkinAssetCounters m_counters;
        private readonly LocalPlayerProfileStore m_profileStore;
        private readonly Dictionary<int, Peer> m_peers = new Dictionary<int, Peer>();
        /// <summary>主机：clientId + ":" + transferId → 上传装配器。</summary>
        private readonly Dictionary<string, SkinAssetAssembler> m_uploads =
            new Dictionary<string, SkinAssetAssembler>(StringComparer.Ordinal);
        /// <summary>客户端：ownerClientId + ":" + transferId → 广播装配器。</summary>
        private readonly Dictionary<string, SkinAssetAssembler> m_downloads =
            new Dictionary<string, SkinAssetAssembler>(StringComparer.Ordinal);
        /// <summary>本连接上已经成功发过的资源（localId + ":" + hash）。</summary>
        private readonly HashSet<string> m_sentUploads = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>本端收到过的主机广播资源哈希（用于判断"主机说已有但本端没收到"）。</summary>
        private readonly HashSet<string> m_receivedSelfHashes = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>已经给某个玩家端发过 MISSING 的 (clientId:hash)，避免每帧刷。</summary>
        private readonly HashSet<string> m_missingSent = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// 主机：**上传路径刚刚接受过**的 (clientId:slot:hash) → 接受时刻（Environment.TickCount64）。
        /// 同一次换肤先上传资源、再发变化通知；上传路径已经回过 accepted 回执并转发 / 通知过，
        /// 变化通知不能再回一条（"发起者只收一条回执"）也不能再广播一次。
        /// </summary>
        private readonly Dictionary<string, long> m_acceptedUploads =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> m_reportedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastReasonLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>上传路径接受后，变化通知在此时限内视为同一次换肤（不重复回执 / 广播）。</summary>
        private const long AcceptedUploadDedupeMs = 10000;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private IPeerBroadcastPort m_broadcast;
        private IPlayerSkinPort m_skin;
        private IPlayerSkinRecordPort m_records;
        private IPlayerProfileHandshakePort m_handshake;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private bool m_isHost;
        private int m_localClientId = -1;
        private int m_transferCounter;
        private long m_lastLogMs;
        private long m_lastCounterTotal;
        private long m_lastLocalPollMs;
        private long m_handshakeMs;
        private string m_localSkinSignature = string.Empty;
        private string m_confirmedHash = string.Empty;
        private PlayerProfileSnapshot m_confirmedProfile;
        private string m_pendingChangeHash = string.Empty;
        private string m_hostSkinHash = string.Empty;
        private string m_hostSkinName = string.Empty;
        private bool m_hostMissingBroadcast;
        private string m_localIdentity = string.Empty;
        private long m_retryMissingMs;

        public PlayerSkinModule(SkinAssetCounters counters,
            LocalPlayerProfileStore profileStore = null)
        {
            m_counters = counters ?? throw new ArgumentNullException(nameof(counters));
            m_profileStore = profileStore;
            m_descriptor = new ModuleDescriptor("player-skin", "联机皮肤资源")
            {
                // 资源必须走传输通道；角色记录 / 名册由联机角色模块提供（player.avatars）。
                RequiresCapabilities = new[] { "transport", "player.avatars" },
                ProvidesCapabilities = new[] { "player.skins" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public SkinAssetCounters Counters => m_counters;

        public void Attach(IModuleContext context)
        {
            m_context = context;
            m_attached = true;
            m_localIdentity = string.Empty;
            m_transferCounter = 0;
            m_lastCounterTotal = 0;
            m_lastLogMs = 0;
            if (!context.TryGetPort(out m_transport))
                Error("没有拿到传输端口（transport）→ 自定义皮肤资源无法收发（不做本地假同步）");
            else
            {
                m_transport.ClientJoined += OnClientJoined;
                m_transport.ClientLeft += OnClientLeft;
            }
            if (!context.TryGetPort(out m_broadcast))
                Error("没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把皮肤资源发给其他客户端");
            if (!context.TryGetPort(out m_skin))
                Error("没有拿到引擎皮肤端口（IPlayerSkinPort）→ 无法读本机皮肤 / 应用贴图");
            if (!context.TryGetPort(out m_records))
                Error("没有拿到角色记录端口（IPlayerSkinRecordPort）→ 主机无法把皮肤写进世界记录");
            context.TryGetPort(out m_handshake);
            if (context.TryGetPort(out m_messages))
                RegisterMessages();
            else
                Error("没有拿到消息注册表（IMessageRegistryPort）→ 皮肤资源消息无法收发");
            Info("自定义皮肤资源模块就绪：" + SkinAssetPolicy.DescribeLimit() +
                "；客户端上传 → 主机写世界角色记录并广播；客户端只保留会话副本缓存");
        }

        private void RegisterMessages()
        {
            MessageRegistry registry = m_messages.Registry;
            registry.UnregisterOwner(m_descriptor.Id);
            registry.Register((int)Proto.MessageType.PlayerSkinAsset, Proto.PlayerSkinAsset.Parser,
                new DelegateMessageHandler<Proto.PlayerSkinAsset>(OnPlayerSkinAssetMessage),
                m_descriptor.Id);
            registry.Register((int)Proto.MessageType.PlayerSkinChanged, Proto.PlayerSkinChanged.Parser,
                new DelegateMessageHandler<Proto.PlayerSkinChanged>(OnPlayerSkinChangedMessage),
                m_descriptor.Id);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_isHost = IsHostingNow;
            m_localClientId = m_transport?.LocalClientId ?? -1;
            long nowMs = (long)(tick.Now * 1000.0);
            ExpireTransfers(nowMs);
            if (m_isHost) TickHost(nowMs);
            else TickClient(nowMs);
            LogCounters(nowMs);
        }

        // ------------------------------------------------------------ 本端会话状态

        /// <summary>本端是否处在联机会话里（主机建房 / 客户端已连上）。UI 用它决定是否显示换肤入口。</summary>
        public bool IsInSession => m_attached && (IsHostingNow || m_transport?.IsConnected == true);

        /// <summary>本端此刻是不是主机（消息处理器不能依赖上一帧 Tick 的缓存值）。</summary>
        private bool IsHostingNow => m_broadcast?.IsHosting == true;

        /// <summary>当前本机皮肤（给换肤屏做初始值）。</summary>
        public bool TryCaptureCurrentSkin(out string skinName, out PlayerSex sex, out string reason)
        {
            skinName = string.Empty;
            sex = PlayerSex.Male;
            reason = null;
            if (m_records == null)
            {
                reason = "角色记录端口不可用";
                return false;
            }
            if (!m_records.TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out reason))
                return false;
            skinName = profile?.Appearance?.SkinName ?? string.Empty;
            sex = profile?.Appearance?.Sex ?? PlayerSex.Male;
            if (skinName.Length == 0)
            {
                reason = "本机角色没有皮肤名";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 中途换肤（本机立刻应用，然后把上传 / 请求交给主机）。
        /// 返回 false = 本机就没通过校验 / 没发出去；<paramref name="reason"/> 必须写进日志。
        /// </summary>
        public bool ApplyLocalSkinChange(PlayerSkinChangeRequest request, out string reason)
        {
            reason = null;
            if (!m_attached || m_records == null || m_skin == null)
            {
                reason = "皮肤模块未就绪";
                return false;
            }
            if (request == null)
            {
                reason = "换肤请求为空";
                return false;
            }
            if (!PlayerEditSync.TryValidateSlot(request.Slot, out reason)) return false;
            if (!m_records.TryCaptureLocalProfile(request.Slot, out PlayerProfileSnapshot current,
                    out reason))
                return false;
            if (!BuildChangedProfile(current, request, out PlayerProfileSnapshot next,
                    out PlayerSkinAssetRecord record, out reason))
                return false;

            if (request.Slot == 0)
            {
                // 本机资料存储只有一份（槽位 0）；其余槽位以主机记录为准，不覆盖槽位 0 的资料。
                m_profileStore?.Set(next,
                    string.IsNullOrEmpty(request.Source) ? "skin-change" : request.Source);
            }
            if (record != null && !record.HasData)
            {
                reason = "自定义皮肤没有资源字节";
                return false;
            }
            if (record != null &&
                !m_skin.StoreAsset(record, SkinAssetOrigin.Owned, out _, out reason))
                return false;

            // ① 本机立刻应用（用户口径：自己的客户端不等主机回执）。
            if (!m_skin.TryApplyLocalSkin(request.Slot, record ?? BuiltInRecord(next),
                    allowPlaceholder: true, out bool placeholder, out string applyReason))
            {
                reason = "应用本机皮肤失败：" + applyReason;
                return false;
            }
            if (placeholder) m_counters.OnPlaceholderShown();
            m_localSkinSignature = SkinSignature(next);
            m_pendingChangeHash = record?.HashHex ?? string.Empty;

            // ② 主机：本地就是权威 → 直接广播；客户端：上传资源 + 发皮肤变化通知。
            if (IsHostingNow)
            {
                if (record != null)
                {
                    if (request.Slot == 0)
                    {
                        m_hostSkinHash = record.HashHex;
                        m_hostSkinName = record.SkinName;
                        m_hostMissingBroadcast = false;
                    }
                    if (!BroadcastAsset(record, excludeClientId: -1, ownerClientId: HostSlotKey,
                            identity: LocalIdentity(), isHost: true, ownerSlot: request.Slot,
                            out reason))
                        return false;
                }
                else
                {
                    if (request.Slot == 0)
                    {
                        m_hostSkinName = next.Appearance?.SkinName ?? string.Empty;
                        m_hostSkinHash = string.Empty;
                        m_hostMissingBroadcast = false;
                    }
                    BroadcastChanged(HostSlotKey, LocalIdentity(), next, record, isHost: true,
                        status: Proto.SkinChangeStatus.Accepted, hasAsset: false,
                        ownerSlot: request.Slot);
                }
                return true;
            }

            if (!SendSkinChangeToHost(record, next, asChange: true, request.Slot, out reason))
                return false;
            return true;
        }

        /// <summary>
        /// 资料同步（108）检测到本机皮肤变化后的入口（<see cref="IProfileSkinSyncPort"/>）：
        /// 与编辑屏退出共用**同一条**资源链（读取本机皮肤库 → 校验 → 缓存 → 本机应用 →
        /// 客户端分片上传 + 变化通知 / 主机写记录 + 广播），不复制第二套上传逻辑。
        /// <paramref name="isHost"/> 必须与本端真实角色一致：方向不对就明确拒绝（不静默走错）。
        /// </summary>
        public bool TrySyncDetectedSkin(PlayerProfileSnapshot profile, int slot, bool isHost,
            out string reason)
        {
            bool hosting = IsHostingNow;
            if (isHost != hosting)
            {
                reason = "调用方角色不一致（isHost=" + isHost + "，本端 IsHosting=" + hosting +
                    "）→ 拒绝走错方向的皮肤链";
                return false;
            }
            return TrySyncEditedSkin(profile, slot, isHost, out reason);
        }

        /// <summary>
        /// 编辑玩家屏退出时的自定义皮肤同步入口（<see cref="IEditPlayerSkinSink"/>）：
        ///   · 内置皮肤：什么都不做（名字/性别/内置皮肤 id 由 PlayerProfile 资料消息携带）；
        ///   · 自定义皮肤：走既有资源链（读取本机皮肤库 → 校验 → 缓存 → 本机应用 →
        ///     客户端分片上传 + 皮肤变化通知 / 主机写记录 + 广播）。
        ///
        /// 返回 false + reason = 明确失败（资源缺失 / 校验失败 / 写记录失败 / 通道不可用）。
        /// 调用方会计数 missingAsset / profileUpdateRejected；**不会**换成内置皮肤。
        /// </summary>
        public bool TrySyncEditedSkin(PlayerProfileSnapshot profile, int slot, bool isHost,
            out string reason)
        {
            reason = null;
            if (!m_attached || m_records == null || m_skin == null)
            {
                reason = "皮肤模块未就绪";
                return false;
            }
            if (profile?.Appearance == null)
            {
                reason = "资料里没有外观";
                return false;
            }
            if (!PlayerEditSync.TryValidateSlot(slot, out reason)) return false;
            string skinName = (profile.Appearance.SkinName ?? string.Empty).Trim();
            if (!SkinAssetPolicy.TryValidateSkinName(skinName, allowBuiltIn: true, out reason))
                return false;
            if (!SkinAssetPolicy.IsCustomSkinName(skinName))
            {
                // 内置皮肤：只需要本机应用（引擎 PlayerScreen 已经改过 PlayerData）；
                // 资料消息负责让主机/其他客户端看到新的内置皮肤 id。
                var builtIn = new PlayerSkinAssetRecord
                {
                    SkinName = skinName,
                    Sex = profile.Appearance.Sex,
                    Sha256 = Array.Empty<byte>(),
                    Data = Array.Empty<byte>()
                };
                if (!m_skin.TryApplyLocalSkin(slot, builtIn, allowPlaceholder: false,
                        out _, out string builtInReason))
                {
                    reason = "应用内置皮肤到本机槽位 " + slot + " 失败：" + builtInReason;
                    return false;
                }
                return true;
            }
            PlayerSkinAssetRecord record = EnsureOwnedAsset(profile, out bool missing);
            if (record == null)
            {
                if (missing) m_counters.OnMissing("编辑玩家屏：本机皮肤库没有 \"" + skinName + "\" 的资源");
                reason = missing
                    ? "本机皮肤库没有 \"" + skinName + "\" 的可读资源（已计数；接收端会显示确定性占位）"
                    : "无法取得自定义皮肤 \"" + skinName + "\" 的资源";
                return false;
            }
            if (!m_skin.StoreAsset(record, SkinAssetOrigin.Owned, out _, out reason)) return false;
            if (!m_skin.TryApplyLocalSkin(slot, record, allowPlaceholder: true,
                    out bool placeholder, out string applyReason))
            {
                reason = "应用自定义皮肤到本机槽位 " + slot + " 失败：" + applyReason;
                return false;
            }
            if (placeholder) m_counters.OnPlaceholderShown();
            // 编辑屏已经显式走过上传/广播：把 TickHost/TickClient 的"变化签名"同步到同一份值，
            // 避免下一拍把同一次编辑当成新变化再发一遍（不是靠轮询检测这次编辑）。
            if (slot == 0)
            {
                m_localSkinSignature = SkinSignature(profile);
                m_pendingChangeHash = record.HashHex;
                if (isHost)
                {
                    m_hostSkinHash = record.HashHex;
                    m_hostSkinName = record.SkinName;
                    m_hostMissingBroadcast = false;
                }
            }
            if (!isHost && m_localClientId >= 0)
                m_sentUploads.Add(m_localClientId + ":" + record.HashHex);
            if (isHost)
            {
                if (!m_records.StoreRecordSkin(LocalIdentity(), profile.Name, 0, record, slot,
                        out string storeReason))
                {
                    reason = "写角色记录里的自定义皮肤失败（槽位 " + slot + "）：" + storeReason;
                    return false;
                }
                if (!BroadcastAsset(record, excludeClientId: -1, ownerClientId: HostSlotKey,
                        identity: LocalIdentity(), isHost: true, ownerSlot: slot, out reason))
                    return false;
                Info("编辑玩家屏：主机槽位 " + slot + " 的自定义皮肤已写记录并广播（\"" +
                    record.SkinName + "\" · " + SkinAssetPolicy.HashHex(record.Sha256) + "）");
                return true;
            }
            if (!SendSkinChangeToHost(record, profile, asChange: true, slot, out reason))
                return false;
            Info("编辑玩家屏：本机槽位 " + slot + " 的自定义皮肤已上传并请求主机确认（\"" +
                record.SkinName + "\" · " + SkinAssetPolicy.HashHex(record.Sha256) + "）");
            return true;
        }

        // ------------------------------------------------------------ 主机 Tick

        private void TickHost(long nowMs)
        {
            ExpireAcceptedUploads();
            if (m_records == null || m_skin == null) return;
            if (!m_records.TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out string reason))
            {
                ErrorOnce("skin-local-profile", "主机本机资料取不到（" + reason +
                    "）→ 本帧不广播主机皮肤资源（不伪造）");
                return;
            }
            string skinName = profile.Appearance?.SkinName ?? string.Empty;
            bool custom = SkinAssetPolicy.IsCustomSkinName(skinName);
            if (!custom)
            {
                if (m_hostSkinHash.Length > 0)
                {
                    Info("主机换成内置皮肤 \"" + skinName + "\" → 客户端按资料里的内置皮肤应用");
                    m_hostSkinHash = string.Empty;
                    m_hostMissingBroadcast = false;
                }
                if (!string.Equals(m_hostSkinName, skinName, StringComparison.Ordinal))
                {
                    m_hostSkinName = skinName;
                    BroadcastChanged(HostSlotKey, LocalIdentity(), profile, null, isHost: true,
                        status: Proto.SkinChangeStatus.Accepted, hasAsset: false);
                }
                return;
            }

            PlayerSkinAssetRecord record = EnsureOwnedAsset(profile, out bool missing);
            if (record != null)
            {
                if (string.Equals(record.HashHex, m_hostSkinHash, StringComparison.Ordinal)) return;
                m_hostSkinHash = record.HashHex;
                m_hostSkinName = record.SkinName;
                m_hostMissingBroadcast = false;
                if (m_skin.TryApplyLocalSkin(record, allowPlaceholder: true, out bool placeholder,
                        out string applyReason))
                {
                    if (placeholder) m_counters.OnPlaceholderShown();
                }
                else
                {
                    ErrorOnce("host-apply-skin", "主机应用自己的自定义皮肤失败：" + applyReason);
                }
                if (!BroadcastAsset(record, excludeClientId: -1, ownerClientId: HostSlotKey,
                        identity: LocalIdentity(), isHost: true, out string sendReason))
                {
                    ErrorOnce("host-broadcast-skin", "主机广播自己的皮肤资源失败：" + sendReason);
                }
                return;
            }

            if (missing && !m_hostMissingBroadcast)
            {
                m_hostMissingBroadcast = true;
                m_counters.OnMissing("主机本机没有皮肤资源：" + skinName);
                BroadcastMissing(HostSlotKey, LocalIdentity(), profile, isHost: true,
                    reason: "主机本机皮肤库没有 \"" + skinName + "\" 的资源");
                ErrorOnce("host-skin-missing:" + skinName,
                    "主机本机皮肤 \"" + skinName + "\" 没有可读资源 → 已向客户端发 MISSING；" +
                    "其他端会显示确定性占位并计数（不静默换皮肤）");
            }
        }

        // ------------------------------------------------------------ 客户端 Tick

        private void TickClient(long nowMs)
        {
            if (m_transport == null || !m_transport.IsConnected)
            {
                ResetConnectionState();
                return;
            }
            if (m_handshake == null)
            {
                ErrorOnce("skin-handshake", "没有握手资料端口 → 无法判断主机是否已有皮肤资源");
                return;
            }
            if (m_handshake.ProfileRequired) return;

            if (m_localClientId != m_transport.LocalClientId)
            {
                m_localClientId = m_transport.LocalClientId;
                m_sentUploads.Clear();
                m_handshakeMs = nowMs;
                m_localSkinSignature = string.Empty;
            }
            if (m_confirmedProfile == null && m_handshake.AssignedProfile != null &&
                m_handshake.AssignedProfile.HasName)
            {
                m_confirmedProfile = m_handshake.AssignedProfile.Clone();
                m_confirmedHash = SkinAssetPolicy.HashHex(
                    m_confirmedProfile.Appearance?.SkinSha256);
            }
            if (nowMs - m_lastLocalPollMs < 1000) return;
            m_lastLocalPollMs = nowMs;

            if (!m_records.TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out string reason))
            {
                ErrorOnce("skin-local-profile-client", "客户端本机资料取不到（" + reason +
                    "）→ 本帧不上传皮肤资源");
                return;
            }
            string skinName = profile.Appearance?.SkinName ?? string.Empty;
            string signature = SkinSignature(profile);
            bool changed = m_localSkinSignature.Length > 0 &&
                !string.Equals(signature, m_localSkinSignature, StringComparison.Ordinal);

            if (SkinAssetPolicy.IsCustomSkinName(skinName))
            {
                PlayerSkinAssetRecord record = EnsureOwnedAsset(profile, out bool missing);
                if (record == null)
                {
                    if (missing)
                    {
                        ErrorOnce("client-skin-asset:" + skinName,
                            "本机皮肤 \"" + skinName + "\" 没有可读资源 → 无法上传；" +
                            "若主机记录也没有，其他端会显示占位并计数");
                    }
                    m_localSkinSignature = signature;
                    return;
                }
                if (m_skin.TryApplyLocalSkin(record, allowPlaceholder: true, out bool placeholder,
                        out string applyReason))
                {
                    if (placeholder) m_counters.OnPlaceholderShown();
                }
                else
                {
                    ErrorOnce("client-apply-skin", "客户端应用自己的皮肤失败：" + applyReason);
                }

                string hashHex = record.HashHex;
                bool isChange = m_confirmedHash.Length > 0 &&
                    !string.Equals(hashHex, m_confirmedHash, StringComparison.Ordinal);
                string key = m_localClientId + ":" + hashHex;
                bool hostHasIt = m_handshake.AssignedSkinAssetPresent;
                bool repairResend = hostHasIt && !isChange && !changed &&
                    nowMs - m_handshakeMs >= 3000 &&
                    !m_sentUploads.Contains(key) &&
                    !m_receivedSelfHashes.Contains(hashHex);
                if (!m_sentUploads.Contains(key) &&
                    (isChange || changed || !hostHasIt || repairResend))
                {
                    if (SendSkinChangeToHost(record, profile, isChange || changed, out string sendReason))
                    {
                        m_sentUploads.Add(key);
                        if (repairResend)
                        {
                            Info("主机声称已有皮肤资源但本端没有收到 → 显式重发一次（repair resend）");
                        }
                    }
                    else
                    {
                        ErrorOnce("client-skin-send", "上传皮肤资源失败：" + sendReason);
                    }
                }
            }
            else if (changed)
            {
                var builtIn = new PlayerSkinAssetRecord
                {
                    SkinName = skinName,
                    Sex = profile.Appearance?.Sex ?? PlayerSex.Male,
                    Sha256 = Array.Empty<byte>(),
                    Data = Array.Empty<byte>()
                };
                m_skin.TryApplyLocalSkin(builtIn, allowPlaceholder: false, out _, out string applyReason);
                if (!SendSkinChangeToHost(null, profile, asChange: true, out string sendReason))
                    ErrorOnce("client-skin-builtin", "发送内置皮肤变化通知失败：" + sendReason);
            }
            m_localSkinSignature = signature;
        }

        private void ResetConnectionState()
        {
            if (m_localSkinSignature.Length == 0 && m_sentUploads.Count == 0) return;
            m_sentUploads.Clear();
            m_receivedSelfHashes.Clear();
            m_downloads.Clear();
            m_localSkinSignature = string.Empty;
            m_pendingChangeHash = string.Empty;
            m_localClientId = -1;
            m_handshakeMs = 0;
            m_confirmedProfile = null;
            m_confirmedHash = string.Empty;
        }

        // ------------------------------------------------------------ 资源读取 / 构建

        /// <summary>
        /// 取本机这份皮肤的资源：先查会话缓存（自己之前上传/接收过的），再只读本机皮肤库。
        /// 返回 null + missing=true = 明确没有资源（调用方必须写原因，不换别的皮肤）。
        /// </summary>
        private PlayerSkinAssetRecord EnsureOwnedAsset(PlayerProfileSnapshot profile, out bool missing)
        {
            missing = false;
            if (profile?.Appearance == null) return null;
            string skinName = profile.Appearance.SkinName ?? string.Empty;
            if (!SkinAssetPolicy.IsCustomSkinName(skinName)) return null;
            if (m_skin == null)
            {
                missing = true;
                return null;
            }
            if (m_skin.TryGetCachedAsset(profile.Appearance.SkinSha256, skinName,
                    out PlayerSkinAssetRecord cached) && cached.HasData)
            {
                return cached;
            }
            if (!m_skin.TryReadLocalSkinAsset(skinName, out byte[] data, out string readReason))
            {
                missing = true;
                ErrorOnce("skin-read:" + skinName, "读取本机皮肤 \"" + skinName + "\" 失败：" +
                    readReason + " → 不上传、不替换成别的皮肤");
                return null;
            }
            if (!SkinAssetPolicy.TryValidateAssetData(data, out string validateReason))
            {
                bool tooLarge = data != null && data.Length > SkinAssetPolicy.MaxAssetBytes;
                CountReject(SkinAssetRejectReason.Malformed, validateReason, tooLarge);
                ErrorOnce("skin-invalid:" + skinName, "本机皮肤 \"" + skinName +
                    "\" 资源非法：" + validateReason);
                missing = true;
                return null;
            }
            if (!m_skin.TryDecodeAssetData(data, out int width, out int height, out string decodeReason))
            {
                CountReject(SkinAssetRejectReason.Malformed, decodeReason);
                ErrorOnce("skin-decode:" + skinName, "本机皮肤 \"" + skinName +
                    "\" 无法解码：" + decodeReason);
                missing = true;
                return null;
            }
            byte[] hash = SkinAssetPolicy.Sha256(data);
            var record = new PlayerSkinAssetRecord
            {
                SkinName = skinName,
                Sex = profile.Appearance.Sex,
                Sha256 = hash,
                Data = data
            };
            if (!m_skin.StoreAsset(record, SkinAssetOrigin.Owned, out _, out string storeReason))
            {
                CountReject(SkinAssetRejectReason.Malformed, storeReason);
                ErrorOnce("skin-store-owned:" + skinName, "缓存本机皮肤资源失败：" + storeReason);
                missing = true;
                return null;
            }
            if (profile.Appearance.SkinSha256 != null && profile.Appearance.SkinSha256.Length > 0 &&
                !SkinAssetPolicy.BytesEqual(profile.Appearance.SkinSha256, hash))
            {
                Warn("本机皮肤 \"" + skinName + "\" 的文件哈希 " + SkinAssetPolicy.HashHex(hash) +
                    " 与资料里的 " + SkinAssetPolicy.HashHex(profile.Appearance.SkinSha256) +
                    " 不一致 → 以本机文件为准显式重发（不静默沿用旧哈希）");
            }
            Info("已读取本机自定义皮肤资源：\"" + skinName + "\" · " + width + "x" + height +
                " · " + data.Length + "B · " + SkinAssetPolicy.HashHex(hash));
            return record;
        }

        private bool BuildChangedProfile(PlayerProfileSnapshot current, PlayerSkinChangeRequest request,
            out PlayerProfileSnapshot next, out PlayerSkinAssetRecord record, out string reason)
        {
            next = null;
            record = null;
            reason = null;
            if (current == null || !current.HasName)
            {
                reason = "本机没有可用的角色资料";
                return false;
            }
            string skinName = (request.SkinName ?? string.Empty).Trim();
            if (!SkinAssetPolicy.TryValidateSkinName(skinName, allowBuiltIn: true, out reason))
                return false;
            bool custom = SkinAssetPolicy.IsCustomSkinName(skinName);
            if (custom)
            {
                if (!request.HasCustomAsset)
                {
                    reason = "自定义皮肤 \"" + skinName + "\" 没有资源字节（屏幕应从本机皮肤库读取）";
                    return false;
                }
                if (!SkinAssetPolicy.TryValidateAssetData(request.SkinAsset, out reason)) return false;
                if (!m_skin.TryDecodeAssetData(request.SkinAsset, out _, out _, out reason)) return false;
                byte[] hash = SkinAssetPolicy.Sha256(request.SkinAsset);
                record = new PlayerSkinAssetRecord
                {
                    SkinName = skinName,
                    Sex = request.Sex,
                    Sha256 = hash,
                    Data = (byte[])request.SkinAsset.Clone()
                };
            }
            else if (!m_records.IsKnownBuiltInSkin(skinName, out reason))
            {
                return false;
            }
            string name = string.IsNullOrWhiteSpace(request.Name)
                ? current.Name
                : request.Name.Trim();
            next = new PlayerProfileSnapshot
            {
                Name = name,
                Appearance = new PlayerAppearance
                {
                    Sex = request.Sex,
                    SkinName = skinName,
                    SkinSha256 = record != null ? (byte[])record.Sha256.Clone() : Array.Empty<byte>(),
                    Clothes = PlayerAppearance.CloneClothes(current.Appearance?.Clothes)
                }
            };
            return true;
        }

        private static PlayerSkinAssetRecord BuiltInRecord(PlayerProfileSnapshot profile) =>
            new PlayerSkinAssetRecord
            {
                SkinName = profile?.Appearance?.SkinName ?? string.Empty,
                Sex = profile?.Appearance?.Sex ?? PlayerSex.Male,
                Sha256 = Array.Empty<byte>(),
                Data = Array.Empty<byte>()
            };

        // ------------------------------------------------------------ 发送

        private bool SendSkinChangeToHost(PlayerSkinAssetRecord record, PlayerProfileSnapshot profile,
            bool asChange, out string reason) =>
            SendSkinChangeToHost(record, profile, asChange, 0, out reason);

        /// <summary>
        /// 上传（自定义皮肤）+ 皮肤变化通知。槽位 0..3 决定主机把这次变化记到哪个
        /// (身份,槽位) 记录 / 名册条目；旧调用方使用槽位 0，线上与旧版兼容。
        /// </summary>
        private bool SendSkinChangeToHost(PlayerSkinAssetRecord record, PlayerProfileSnapshot profile,
            bool asChange, int slot, out string reason)
        {
            reason = null;
            if (!PlayerEditSync.TryValidateSlot(slot, out reason)) return false;
            if (m_messages == null)
            {
                reason = "没有消息注册表";
                return false;
            }
            if (record != null)
            {
                if (!SendUpload(record, slot, out reason)) return false;
            }
            if (asChange)
            {
                Proto.PlayerSkinChanged changed = ChangedMessage(HostSlotKey,
                    LocalIdentity(), profile, record, Proto.SkinChangeStatus.Pending,
                    hasAsset: record != null, isHost: false, reason: null, slot: slot);
                if (!Send(0, changed))
                {
                    reason = "皮肤变化通知发送失败（通道不可用 / 队列满）";
                    m_counters.OnChangeFailed(reason);
                    return false;
                }
            }
            return true;
        }

        private bool SendUpload(PlayerSkinAssetRecord record, out string reason) =>
            SendUpload(record, 0, out reason);

        private bool SendUpload(PlayerSkinAssetRecord record, int slot, out string reason)
        {
            reason = null;
            if (m_skin == null || m_messages == null)
            {
                reason = "皮肤端口 / 消息层不可用";
                return false;
            }
            if (!PlayerEditSync.TryValidateSlot(slot, out reason)) return false;
            SkinAssetCheck check = SkinAssetCodec.TryEncode(record.SkinName, record.Sex, record.Data,
                ownerClientId: 0, identity: LocalIdentity(), transferId: NextTransferId(),
                out List<Proto.PlayerSkinAsset> messages, out byte[] sha256);
            if (!check.Ok)
            {
                CountReject(check.Kind, check.Reason,
                    tooLarge: check.Kind == SkinAssetRejectReason.Oversize);
                reason = check.Reason;
                return false;
            }
            if (!SkinAssetPolicy.BytesEqual(sha256, record.Sha256))
            {
                CountReject(SkinAssetRejectReason.HashMismatch, "编码后的哈希与缓存记录不一致");
                reason = "编码后的哈希与缓存记录不一致";
                return false;
            }
            for (int i = 0; i < messages.Count; i++)
            {
                messages[i].Slot = (uint)slot;
                if (Send(0, messages[i])) continue;
                reason = "第 " + i + "/" + messages.Count + " 片发送失败（通道不可用 / 队列满）";
                m_counters.OnSendFailed(reason);
                return false;
            }
            m_counters.OnSent();
            Info("已上传自定义皮肤资源：\"" + record.SkinName + "\" · 槽位 " + slot + " · " +
                record.Data.Length + "B · " + messages.Count + " 片 · " +
                SkinAssetPolicy.HashHex(record.Sha256));
            return true;
        }

        /// <summary>
        /// 主机：把一份资源广播给除 <paramref name="excludeClientId"/> 外的玩家端
        /// （资源走独立分块通道，随后给这些端一条 accepted 变化通知）。
        /// excludeClientId &lt; 0 = 主机自己的皮肤 → 没有"发起者"例外，发给所有玩家端。
        /// </summary>
        private bool BroadcastAsset(PlayerSkinAssetRecord record, int excludeClientId,
            int ownerClientId, string identity, bool isHost, out string reason) =>
            BroadcastAsset(record, excludeClientId, ownerClientId, identity, isHost, 0, out reason);

        private bool BroadcastAsset(PlayerSkinAssetRecord record, int excludeClientId,
            int ownerClientId, string identity, bool isHost, int ownerSlot, out string reason)
        {
            if (!ForwardAssetChunks(excludeClientId, record, ownerClientId, identity, ownerSlot,
                    out reason))
                return false;
            NotifyChangedExcept(excludeClientId, ownerClientId, identity, record, isHost,
                Proto.SkinChangeStatus.Accepted, hasAsset: true, ownerSlot);
            return true;
        }

        /// <summary>
        /// 主机：把一份自定义皮肤资源编码成分片，经**独立资源通道**转发给除
        /// <paramref name="excludeClientId"/> 外的所有玩家端（
        /// <see cref="IPeerBroadcastPort.BroadcastToPlayersExcept"/>；绝不塞进主同步报文）。
        /// excludeClientId &lt; 0 = 发给所有玩家端（主机自己的皮肤）。
        ///
        /// 计数：skinAssetForwarded = 成功入队的分片次数（分片 × 目标端）；
        ///       skinForwardExcludedOrigin = 这次转发排除了发起者。
        /// 返回 false + reason = 编码失败 / 没有广播端口 / 没有任何端收到任一分片。
        /// </summary>
        private bool ForwardAssetChunks(int excludeClientId, PlayerSkinAssetRecord record,
            int ownerClientId, string identity, int ownerSlot, out string reason)
        {
            reason = null;
            if (record == null || !record.HasData)
            {
                reason = "没有资源可转发";
                return false;
            }
            if (m_broadcast == null)
            {
                reason = "没有广播端口（IPeerBroadcastPort）→ 无法把资源转发给其他客户端";
                return false;
            }
            if (!PlayerEditSync.TryValidateSlot(ownerSlot, out reason)) return false;
            SkinAssetCheck check = SkinAssetCodec.TryEncode(record.SkinName, record.Sex, record.Data,
                ownerClientId, identity, NextTransferId(),
                out List<Proto.PlayerSkinAsset> messages, out byte[] sha256);
            if (!check.Ok)
            {
                CountReject(check.Kind, check.Reason, tooLarge: check.Kind == SkinAssetRejectReason.Oversize);
                reason = check.Reason;
                return false;
            }
            if (!SkinAssetPolicy.BytesEqual(sha256, record.Sha256))
            {
                CountReject(SkinAssetRejectReason.HashMismatch, "编码后的哈希与缓存记录不一致");
                reason = "编码后的哈希与缓存记录不一致";
                return false;
            }
            if (excludeClientId >= 0) m_counters.OnForwardExcludedOrigin();
            int expected = ExpectedForwardTargets(excludeClientId);
            int queuedChunks = 0;
            int lastQueued = -1;
            for (int i = 0; i < messages.Count; i++)
            {
                messages[i].Slot = (uint)ownerSlot;
                int queued = m_broadcast.BroadcastToPlayersExcept(excludeClientId,
                    Proto.MessageType.PlayerSkinAsset, messages[i].ToByteArray());
                queuedChunks += queued;
                if (queued <= 0 && expected > 0)
                {
                    reason = "第 " + i + "/" + messages.Count + " 片没有入队到任何玩家端（期望 " +
                        expected + " 端）";
                    m_counters.OnSendFailed("皮肤转发：" + reason);
                    continue;
                }
                if (lastQueued >= 0 && queued != lastQueued)
                {
                    // 同一次转发的各分片入队端数不一致 = 有端只收到一部分分片
                    // （接收端装配器会按 15s 超时显式丢弃并计数，不会得到半份皮肤）。
                    m_counters.OnSendFailed("皮肤转发分片入队端数不一致：第 " + i + " 片 " + queued +
                        " ≠ 上一片 " + lastQueued);
                }
                lastQueued = queued;
            }
            m_counters.OnAssetForwarded(queuedChunks);
            if (queuedChunks > 0) m_counters.OnSent();
            if (queuedChunks <= 0)
            {
                if (expected <= 0)
                {
                    Info("皮肤资源暂无可转发的玩家端（\"" + record.SkinName +
                        "\"）；资源已落库，新客户端加入时会从角色记录补发");
                    return true;
                }
                reason = "没有任何玩家端收到皮肤资源分片";
                return false;
            }
            Info("已转发自定义皮肤资源：" + (excludeClientId >= 0
                    ? "跳过发起者 clientId=" + excludeClientId
                    : "主机自己的皮肤，发给所有玩家端") +
                " · \"" + record.SkinName + "\" · " + messages.Count + " 片 · 入队 " +
                queuedChunks + " 次");
            return true;
        }

        /// <summary>这次转发期望的玩家端数（m_peers = 已完成加入握手的玩家端）。</summary>
        private int ExpectedForwardTargets(int excludeClientId)
        {
            int count = 0;
            foreach (int peerId in m_peers.Keys)
                if (peerId != excludeClientId) count++;
            return count;
        }

        /// <summary>
        /// 主机：把"皮肤变化"通知发给除 <paramref name="excludeClientId"/> 外的玩家端。
        /// 资源本身只走 <see cref="ForwardAssetChunks"/>；这里只带皮肤名 / sha256 / 结果。
        /// </summary>
        private void NotifyChangedExcept(int excludeClientId, int ownerClientId, string identity,
            PlayerSkinAssetRecord record, bool isHost, Proto.SkinChangeStatus status, bool hasAsset,
            int ownerSlot)
        {
            foreach (int peerId in new List<int>(m_peers.Keys))
            {
                if (peerId == excludeClientId) continue;
                SendChanged(peerId, ownerClientId, identity, null, record, status, hasAsset, isHost,
                    reason: null, slot: ownerSlot);
            }
        }

        private void BroadcastMissing(int ownerClientId, string identity, PlayerProfileSnapshot profile,
            bool isHost, string reason) =>
            BroadcastMissing(ownerClientId, identity, profile, isHost, reason, 0);

        private void BroadcastMissing(int ownerClientId, string identity, PlayerProfileSnapshot profile,
            bool isHost, string reason, int ownerSlot)
        {
            foreach (int peerId in new List<int>(m_peers.Keys))
            {
                SendChanged(peerId, ownerClientId, identity, profile, null,
                    Proto.SkinChangeStatus.Missing, hasAsset: false, isHost: isHost,
                    reason: reason, slot: ownerSlot);
            }
        }

        private void BroadcastChanged(int ownerClientId, string identity, PlayerProfileSnapshot profile,
            PlayerSkinAssetRecord record, bool isHost, Proto.SkinChangeStatus status, bool hasAsset) =>
            BroadcastChanged(ownerClientId, identity, profile, record, isHost, status, hasAsset, 0);

        private void BroadcastChanged(int ownerClientId, string identity, PlayerProfileSnapshot profile,
            PlayerSkinAssetRecord record, bool isHost, Proto.SkinChangeStatus status, bool hasAsset,
            int ownerSlot)
        {
            foreach (int peerId in new List<int>(m_peers.Keys))
            {
                SendChanged(peerId, ownerClientId, identity, profile, record, status, hasAsset, isHost,
                    reason: null, slot: ownerSlot);
            }
        }

        private void SendChanged(int targetClientId, int ownerClientId, string identity,
            PlayerProfileSnapshot profile, PlayerSkinAssetRecord record,
            Proto.SkinChangeStatus status, bool hasAsset, bool isHost, string reason = null) =>
            SendChanged(targetClientId, ownerClientId, identity, profile, record, status, hasAsset,
                isHost, reason, 0);

        private void SendChanged(int targetClientId, int ownerClientId, string identity,
            PlayerProfileSnapshot profile, PlayerSkinAssetRecord record,
            Proto.SkinChangeStatus status, bool hasAsset, bool isHost, string reason, int slot)
        {
            if (m_messages == null || targetClientId <= 0) return;
            Proto.PlayerSkinChanged message = ChangedMessage(ownerClientId, identity, profile, record,
                status, hasAsset, isHost, reason, slot);
            if (Send(targetClientId, message)) return;
            m_counters.OnSendFailed("skin-changed -> clientId=" + targetClientId);
            Warn("皮肤变化通知发给 clientId=" + targetClientId + " 失败（通道不可用）");
        }

        private static Proto.PlayerSkinChanged ChangedMessage(int ownerClientId, string identity,
            PlayerProfileSnapshot profile, PlayerSkinAssetRecord record,
            Proto.SkinChangeStatus status, bool hasAsset, bool isHost, string reason = null,
            int slot = 0)
        {
            var message = new Proto.PlayerSkinChanged
            {
                ClientId = ownerClientId,
                Identity = identity ?? string.Empty,
                SkinName = record?.SkinName ?? profile?.Appearance?.SkinName ?? string.Empty,
                PlayerClass = (uint)(record?.Sex ?? profile?.Appearance?.Sex ?? PlayerSex.Male),
                Status = (uint)status,
                HasAsset = hasAsset,
                IsHost = isHost,
                Reason = reason ?? string.Empty,
                Slot = (uint)slot
            };
            byte[] hash = record?.Sha256 ?? profile?.Appearance?.SkinSha256;
            if (hash != null && hash.Length > 0) message.Sha256 = ByteString.CopyFrom(hash);
            return message;
        }

        private bool Send<T>(int targetClientId, T message) where T : class, IMessage<T> =>
            m_messages != null && m_messages.Sender.Send(targetClientId, message);

        /// <summary>
        /// 主机 → 发起者：一条**明确回执**（accepted / rejected + 原因），走
        /// <see cref="ITransportPort.TrySendTo"/> **单发**。回执里 has_asset=false —
        /// 发起者绝不会再收到自己的皮肤资源（用户口径 2026-10-21）。
        /// 回执内容：client_id = 发起者、identity、skin_name、sha256、status、reason、slot。
        /// 返回 false = 通道不可用 / 背压 / 没有目标（已计数 + 一行明确原因，不假装已送达）。
        /// </summary>
        private bool SendAck(int targetClientId, int ownerClientId, string identity,
            PlayerSkinAssetRecord record, Proto.SkinChangeStatus status, string reason, int slot,
            bool isHost = false)
        {
            if (targetClientId <= 0)
            {
                m_counters.OnAckFailure("回执没有目标连接（clientId=" + targetClientId + "）");
                return false;
            }
            if (m_transport == null)
            {
                m_counters.OnAckFailure("没有传输端口 → 回执发不到 clientId=" + targetClientId);
                return false;
            }
            Proto.PlayerSkinChanged message = ChangedMessage(ownerClientId, identity, null, record,
                status, hasAsset: false, isHost: isHost, reason: reason, slot: slot);
            byte[] payload = message.ToByteArray();
            if (m_transport.TrySendTo(targetClientId, Proto.MessageType.PlayerSkinChanged, payload))
            {
                m_counters.OnAckSent();
                return true;
            }
            string failure = "回执发送失败（通道不可用 / 背压）：clientId=" + targetClientId +
                " · status=" + status + " · \"" + message.SkinName + "\"";
            m_counters.OnAckFailure(failure);
            ErrorOnce("skin-ack:" + targetClientId + ":" + status + ":" + message.Slot, failure);
            return false;
        }

        /// <summary>上传路径接受后的去重键（同一 clientId / 槽位 / 哈希 = 同一次换肤）。</summary>
        private static string AcceptedUploadKey(int clientId, int slot, byte[] hash) =>
            clientId + ":" + slot + ":" + SkinAssetPolicy.HashHex(hash);

        /// <summary>清理超过 10 秒的上传接受记录（变化通知只在紧随其后的同一拍里需要去重）。</summary>
        private void ExpireAcceptedUploads()
        {
            if (m_acceptedUploads.Count == 0) return;
            long nowMs = NowMs();
            List<string> stale = null;
            foreach (KeyValuePair<string, long> item in m_acceptedUploads)
            {
                if (nowMs - item.Value <= AcceptedUploadDedupeMs) continue;
                stale ??= new List<string>();
                stale.Add(item.Key);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) m_acceptedUploads.Remove(stale[i]);
        }

        private void CountReject(SkinAssetRejectReason kind, string reason, bool tooLarge = false)
        {
            m_counters.OnRejected(kind, reason);
            if (tooLarge) m_counters.OnTooLarge(reason);
        }

        private int NextTransferId()
        {
            m_transferCounter++;
            if (m_transferCounter <= 0 || m_transferCounter > 1000000) m_transferCounter = 1;
            return m_transferCounter;
        }

        private string LocalIdentity()
        {
            if (m_localIdentity.Length > 0) return m_localIdentity;
            if (m_records != null && m_records.TryGetLocalIdentity(out string identity, out _))
                m_localIdentity = identity ?? string.Empty;
            return m_localIdentity;
        }

        // ------------------------------------------------------------ 接收：资源

        private void OnPlayerSkinAssetMessage(MessageContext context, Proto.PlayerSkinAsset message)
        {
            SkinAssetCheck check = SkinAssetCodec.TryRead(message, out int ownerClientId,
                out int transferId, out string skinName, out string identity, out PlayerSex sex,
                out byte[] sha256, out int rawLength, out int compression, out int chunkIndex,
                out int chunkCount, out byte[] data);
            if (!check.Ok)
            {
                Reject(-1, check.Kind, check.Reason, context.ClientId);
                return;
            }
            int slot = (int)message.Slot;
            if (!PlayerEditSync.TryValidateSlot(slot, out string slotReason))
            {
                Reject(context.ClientId, SkinAssetRejectReason.Malformed,
                    "皮肤资源的槽位非法：" + slotReason, context.ClientId, upload: IsHostingNow,
                    skinName: skinName, sha256: sha256, identity: identity);
                return;
            }
            if (IsHostingNow) HandleUpload(context.ClientId, ownerClientId, transferId, skinName,
                identity, sex, sha256, rawLength, compression, chunkIndex, chunkCount, data, slot);
            else HandleDownload(ownerClientId, transferId, skinName, identity, sex, sha256,
                rawLength, compression, chunkIndex, chunkCount, data, slot);
        }

        private void HandleUpload(int senderClientId, int claimedOwner, int transferId,
            string skinName, string identity, PlayerSex sex, byte[] sha256, int rawLength,
            int compression, int chunkIndex, int chunkCount, byte[] data, int slot)
        {
            if (senderClientId <= 0)
            {
                Reject(senderClientId, SkinAssetRejectReason.UnknownClient,
                    "主机收到的上传没有来源连接", senderClientId, upload: true,
                    skinName: skinName, sha256: sha256, slot: slot);
                return;
            }
            if (!m_peers.TryGetValue(senderClientId, out Peer peer))
            {
                Reject(senderClientId, SkinAssetRejectReason.UnknownClient,
                    "clientId=" + senderClientId + " 还没有完成加入（不能上传皮肤资源）",
                    senderClientId, upload: true, skinName: skinName, sha256: sha256, slot: slot);
                return;
            }
            if (claimedOwner != 0 && claimedOwner != senderClientId)
            {
                Reject(senderClientId, SkinAssetRejectReason.NotAllowed,
                    "clientId=" + senderClientId + " 冒领 owner=" + claimedOwner, senderClientId,
                    upload: true, skinName: skinName, sha256: sha256, identity: peer.Identity,
                    slot: slot);
                return;
            }
            string effectiveIdentity = peer.Identity;
            if (effectiveIdentity.Length == 0) effectiveIdentity = identity;

            string key = senderClientId + ":" + slot + ":" + transferId;
            if (!m_uploads.TryGetValue(key, out SkinAssetAssembler assembler))
            {
                if (CountUploads(senderClientId) >= SkinAssetPolicy.MaxConcurrentTransfers)
                {
                    Reject(senderClientId, SkinAssetRejectReason.Oversize,
                        "clientId=" + senderClientId + " 的并发传输超过 " +
                        SkinAssetPolicy.MaxConcurrentTransfers, senderClientId, upload: true,
                        skinName: skinName, sha256: sha256, identity: effectiveIdentity, slot: slot);
                    return;
                }
                assembler = new SkinAssetAssembler();
                m_uploads[key] = assembler;
            }
            long nowMs = NowMs();
            SkinAssetCheck check = assembler.TryAccept(senderClientId, transferId, skinName,
                effectiveIdentity, sex, sha256, rawLength, compression, chunkIndex, chunkCount, data,
                nowMs, out byte[] complete);
            if (!check.Ok)
            {
                m_uploads.Remove(key);
                Reject(senderClientId, check.Kind, check.Reason, senderClientId, upload: true,
                    tooLarge: check.Kind == SkinAssetRejectReason.Oversize, skinName: skinName,
                    sha256: sha256, identity: effectiveIdentity, slot: slot);
                return;
            }
            if (complete == null) return;    // 还在等其余分片
            m_uploads.Remove(key);
            m_counters.OnReceived();

            if (m_skin == null || m_records == null)
            {
                Reject(senderClientId, SkinAssetRejectReason.StoreFailed,
                    "皮肤端口 / 角色记录端口不可用", senderClientId, upload: true,
                    skinName: skinName, sha256: sha256, identity: effectiveIdentity, slot: slot);
                return;
            }
            if (!m_skin.TryDecodeAssetData(complete, out _, out _, out string decodeReason))
            {
                Reject(senderClientId, SkinAssetRejectReason.Malformed,
                    "引擎无法解码这份皮肤：" + decodeReason, senderClientId, upload: true,
                    skinName: skinName, sha256: sha256, identity: effectiveIdentity, slot: slot);
                return;
            }
            byte[] actualHash = SkinAssetPolicy.Sha256(complete);
            var record = new PlayerSkinAssetRecord
            {
                SkinName = skinName,
                Sex = sex,
                Sha256 = actualHash,
                Data = complete
            };
            if (!m_skin.StoreAsset(record, SkinAssetOrigin.HostRecord, out bool duplicate,
                    out string storeReason))
            {
                Reject(senderClientId, SkinAssetRejectReason.HashMismatch, storeReason,
                    senderClientId, upload: true, skinName: skinName, sha256: actualHash,
                    identity: effectiveIdentity, slot: slot);
                return;
            }
            if (!m_records.StoreRecordSkin(effectiveIdentity, peer.Name, senderClientId, record,
                    slot, out string recordReason))
            {
                Reject(senderClientId, SkinAssetRejectReason.StoreFailed,
                    "写角色记录失败（槽位 " + slot + "）：" + recordReason, senderClientId,
                    upload: true, skinName: skinName, sha256: actualHash,
                    identity: effectiveIdentity, slot: slot);
                return;
            }
            m_records.UpdateSkinAppearance(senderClientId, effectiveIdentity, peer.Name, record, slot);
            if (m_skin.TryApplyAvatarSkin(new PlayerKey(senderClientId, slot), record,
                    allowPlaceholder: true, out bool placeholder, out string applyReason))
            {
                m_counters.OnApplied();
                if (placeholder) m_counters.OnPlaceholderShown();
            }
            else
            {
                Warn("把皮肤资源应用到玩家键 " + new PlayerKey(senderClientId, slot) +
                    " 的化身失败（" + applyReason + "）；资源已存进角色记录，化身建好后会再应用");
            }

            // 上传路径是"主机接受未知皮肤"的**唯一权威入口**（用户口径 2026-10-21）：
            // 只要字节通过 SkinAssetPolicy，就接受 + 落库（PlayerSkinStore + 身份记录），
            // 不因为"主机之前没有这个皮肤"而拒绝。
            m_counters.OnUploadAccepted();
            m_acceptedUploads[AcceptedUploadKey(senderClientId, slot, actualHash)] = NowMs();

            if (duplicate)
            {
                m_counters.OnDeduped();
                // 哈希已存在 → **不重传资源**；发起者只收 accepted 回执，其他端只收引用该 sha256
                // 的变化通知（用户口径 #3）。
                SendAck(senderClientId, senderClientId, effectiveIdentity, record,
                    Proto.SkinChangeStatus.Accepted, reason: null, slot: slot);
                NotifyChangedExcept(senderClientId, senderClientId, effectiveIdentity, record,
                    isHost: false, status: Proto.SkinChangeStatus.Accepted, hasAsset: true,
                    ownerSlot: slot);
                Info("收到重复的皮肤资源（同一哈希）→ 不重传资源：只给发起者 accepted 回执 + " +
                    "给其他玩家端一条变化通知 \"" + skinName + "\" · " +
                    SkinAssetPolicy.HashHex(actualHash));
                return;
            }

            // 新资源：资源**只**转发给其他客户端（发起者绝不收到自己的皮肤资源），
            // 再给发起者一条 accepted 回执（单发，has_asset=false）。
            if (!ForwardAssetChunks(senderClientId, record, senderClientId, effectiveIdentity, slot,
                    out string forwardReason))
            {
                ErrorOnce("upload-forward:" + senderClientId + ":" + slot + ":" +
                    SkinAssetPolicy.HashHex(actualHash),
                    "已接受并落库 clientId=" + senderClientId + " 的皮肤资源，但转发给其他客户端失败：" +
                    forwardReason + "（已计数；资源在角色记录里，新客户端加入时会补发）");
            }
            NotifyChangedExcept(senderClientId, senderClientId, effectiveIdentity, record,
                isHost: false, status: Proto.SkinChangeStatus.Accepted, hasAsset: true,
                ownerSlot: slot);
            SendAck(senderClientId, senderClientId, effectiveIdentity, record,
                Proto.SkinChangeStatus.Accepted, reason: null, slot: slot);
            Info("主机接受客户端自定义皮肤：玩家键 " + new PlayerKey(senderClientId, slot) +
                " · \"" + skinName + "\" · " + complete.Length + "B · " +
                SkinAssetPolicy.HashHex(actualHash) +
                " → 已写世界角色记录；资源只转发给其他客户端，发起者只收回执");
        }

        private void HandleDownload(int ownerClientId, int transferId, string skinName,
            string identity, PlayerSex sex, byte[] sha256, int rawLength, int compression,
            int chunkIndex, int chunkCount, byte[] data, int slot)
        {
            if (ownerClientId < 0)
            {
                Reject(-1, SkinAssetRejectReason.Malformed, "广播的 owner clientId 非法", -1);
                return;
            }
            string key = ownerClientId + ":" + slot + ":" + transferId;
            if (!m_downloads.TryGetValue(key, out SkinAssetAssembler assembler))
            {
                if (m_downloads.Count >= SkinAssetPolicy.MaxConcurrentTransfers)
                {
                    Reject(-1, SkinAssetRejectReason.Oversize, "客户端并发传输过多", -1);
                    return;
                }
                assembler = new SkinAssetAssembler();
                m_downloads[key] = assembler;
            }
            SkinAssetCheck check = assembler.TryAccept(ownerClientId, transferId, skinName,
                identity, sex, sha256, rawLength, compression, chunkIndex, chunkCount, data,
                NowMs(), out byte[] complete);
            if (!check.Ok)
            {
                m_downloads.Remove(key);
                Reject(-1, check.Kind, check.Reason, -1,
                    tooLarge: check.Kind == SkinAssetRejectReason.Oversize);
                return;
            }
            if (complete == null) return;
            m_downloads.Remove(key);
            m_counters.OnReceived();

            if (m_skin == null)
            {
                Reject(-1, SkinAssetRejectReason.StoreFailed, "没有皮肤端口", -1);
                return;
            }
            if (!m_skin.TryDecodeAssetData(complete, out _, out _, out string decodeReason))
            {
                Reject(-1, SkinAssetRejectReason.Malformed,
                    "收到的皮肤无法解码：" + decodeReason, -1);
                return;
            }
            byte[] actualHash = SkinAssetPolicy.Sha256(complete);
            var record = new PlayerSkinAssetRecord
            {
                SkinName = skinName,
                Sex = sex,
                Sha256 = actualHash,
                Data = complete
            };
            if (!m_skin.StoreAsset(record, SkinAssetOrigin.Replica, out bool duplicate,
                    out string storeReason))
            {
                Reject(-1, SkinAssetRejectReason.HashMismatch, storeReason, -1);
                return;
            }
            // 去重只跳过"重复缓存"，**不跳过应用**：两个玩家可能用同一份资源，
            // 后到的那个 owner 仍需要把它应用到自己的化身 / 本机玩家。
            if (duplicate) m_counters.OnDeduped();
            bool ownedBySelf = ownerClientId == m_localClientId;
            if (ownedBySelf) m_receivedSelfHashes.Add(actualHash != null ? SkinAssetPolicy.HashHex(actualHash) : string.Empty);
            if (ownedBySelf)
            {
                if (m_skin.TryApplyLocalSkin(slot, record, allowPlaceholder: true,
                        out bool placeholder, out string applyReason))
                {
                    m_counters.OnApplied();
                    if (placeholder) m_counters.OnPlaceholderShown();
                }
                else Warn("应用收到的主人皮肤失败（槽位 " + slot + "）：" + applyReason);
            }
            else if (m_skin.TryApplyAvatarSkin(new PlayerKey(ownerClientId, slot), record,
                         allowPlaceholder: true, out bool placeholder, out string applyReason))
            {
                m_counters.OnApplied();
                if (placeholder) m_counters.OnPlaceholderShown();
            }
            else
            {
                Info("收到玩家键 " + new PlayerKey(ownerClientId, slot) +
                    " 的皮肤资源，但化身还没建好（" + applyReason + "）→ 已缓存，化身建好后自动应用");
            }
            Info("已接收自定义皮肤资源：owner=" + new PlayerKey(ownerClientId, slot) + " · \"" +
                skinName + "\" · " + complete.Length + "B · " +
                SkinAssetPolicy.HashHex(actualHash));
        }

        // ------------------------------------------------------------ 接收：皮肤变化通知

        private void OnPlayerSkinChangedMessage(MessageContext context, Proto.PlayerSkinChanged message)
        {
            if (message == null)
            {
                ErrorOnce("skin-changed-null", "收到空皮肤变化通知 → 丢弃");
                return;
            }
            if (IsHostingNow) HandleHostSkinChanged(context.ClientId, message);
            else HandleClientSkinChanged(message);
        }

        private void HandleHostSkinChanged(int senderClientId, Proto.PlayerSkinChanged message)
        {
            if (message.Status != (uint)Proto.SkinChangeStatus.Pending)
            {
                ErrorOnce("skin-changed-host-status:" + message.Status,
                    "主机收到非 PENDING 的皮肤变化通知（status=" + message.Status + "）→ 忽略");
                return;
            }
            if (senderClientId <= 0 || !m_peers.TryGetValue(senderClientId, out Peer peer))
            {
                Reject(senderClientId, SkinAssetRejectReason.UnknownClient,
                    "皮肤变化通知来源 clientId=" + senderClientId + " 未加入", senderClientId);
                return;
            }
            if (message.ClientId != 0 && message.ClientId != senderClientId)
            {
                Reject(senderClientId, SkinAssetRejectReason.NotAllowed,
                    "皮肤变化通知冒领 clientId=" + message.ClientId, senderClientId);
                return;
            }
            int slot = (int)message.Slot;
            if (!PlayerEditSync.TryValidateSlot(slot, out string slotReason))
            {
                RejectChange(senderClientId, peer, message.SkinName, message,
                    "皮肤变化通知的槽位非法：" + slotReason, SkinAssetRejectReason.Malformed);
                return;
            }
            PlayerKey ownerKey = new PlayerKey(senderClientId, slot);
            string skinName = message.SkinName ?? string.Empty;
            if (!SkinAssetPolicy.TryValidateSkinName(skinName, allowBuiltIn: true, out string nameReason))
            {
                RejectChange(senderClientId, peer, skinName, message, nameReason,
                    SkinAssetRejectReason.InvalidName);
                return;
            }
            string identity = peer.Identity;
            PlayerSex sex = message.PlayerClass == (uint)PlayerSex.Female ? PlayerSex.Female : PlayerSex.Male;
            if (SkinAssetPolicy.IsCustomSkinName(skinName))
            {
                byte[] hash = message.Sha256?.Length > 0 ? message.Sha256.ToByteArray() : Array.Empty<byte>();
                if (!SkinAssetPolicy.IsValidHash(hash))
                {
                    RejectChange(senderClientId, peer, skinName, message,
                        "自定义皮肤缺少 32 字节哈希", SkinAssetRejectReason.Malformed);
                    return;
                }
                bool cached = m_skin.TryGetCachedAsset(hash, skinName,
                        out PlayerSkinAssetRecord record) && record != null && record.HasData &&
                    SkinAssetPolicy.BytesEqual(record.Sha256, hash);
                if (!cached &&
                    m_records.TryGetRecordSkin(identity, peer.Name, slot,
                        out PlayerSkinAssetRecord fromRecord, out _) &&
                    fromRecord != null && fromRecord.HasData &&
                    SkinAssetPolicy.BytesEqual(fromRecord.Sha256, hash))
                {
                    // 主机缓存可能是空的（换了世界 / 重启后），但世界作用域角色记录里还有资源。
                    m_skin.StoreAsset(fromRecord, SkinAssetOrigin.HostRecord, out _, out _);
                    record = fromRecord;
                    cached = true;
                }
                if (!cached)
                {
                    // 用户口径 2026-10-21：**不接受"主机没有这个皮肤就拒绝"**。
                    // 资源字节由上传路径（HandleUpload）负责接受 + 落库 + 转发；这里没有字节，
                    // 既不伪造记录、也不回 MISSING / REJECTED。明确计数 + 限流一行原因；
                    // 上传完成后由上传路径给发起者一条 accepted 回执。
                    m_counters.OnChangeWithoutAsset("clientId=" + senderClientId + " 槽位 " + slot +
                        " 的 \"" + skinName + "\" 资源还没到（" + SkinAssetPolicy.HashHex(hash) + "）");
                    ErrorOnce("host-change-no-asset:" + senderClientId + ":" + slot + ":" +
                        SkinAssetPolicy.HashHex(hash),
                        "玩家键 " + ownerKey + " 请求换成 \"" + skinName + "\"，但主机此刻没有资源 → " +
                        "按口径不拒绝、也不发 MISSING；等上传路径接受（上传完成会给发起者回执；" +
                        "若上传已失败，客户端已单独收到 rejected 原因）");
                    return;
                }
                if (!m_records.StoreRecordSkin(identity, peer.Name, senderClientId, record,
                        slot, out string recordReason))
                {
                    RejectChange(senderClientId, peer, skinName, message,
                        "写角色记录失败（槽位 " + slot + "）：" + recordReason,
                        SkinAssetRejectReason.StoreFailed, slot);
                    return;
                }
                m_records.UpdateSkinAppearance(senderClientId, identity, peer.Name, record, slot);
                if (m_skin.TryApplyAvatarSkin(ownerKey, record, allowPlaceholder: true,
                        out bool placeholder, out string applyReason))
                {
                    m_counters.OnApplied();
                    if (placeholder) m_counters.OnPlaceholderShown();
                }
                else Warn("应用玩家键 " + ownerKey + " 的新皮肤失败：" + applyReason);

                // 上传路径刚刚接受过同一次换肤：它已经发过 accepted 回执 + 转发 / 变化通知，
                // 这里不能重复（"发起者只收一条回执"）。
                string acceptedKey = AcceptedUploadKey(senderClientId, slot, hash);
                if (m_acceptedUploads.TryGetValue(acceptedKey, out long acceptedMs) &&
                    NowMs() - acceptedMs <= AcceptedUploadDedupeMs)
                {
                    Info("玩家键 " + ownerKey + " 的皮肤变化已由上传路径接受（不重复回执 / 不重复转发）");
                    return;
                }

                // 哈希已存在（主机之前就有这份资源）→ **不重传资源**：
                // 发起者只收 accepted 回执，其他端只收一条引用该 sha256 的变化通知。
                SendAck(senderClientId, senderClientId, identity, record,
                    Proto.SkinChangeStatus.Accepted, reason: null, slot: slot);
                NotifyChangedExcept(senderClientId, senderClientId, identity, record, isHost: false,
                    status: Proto.SkinChangeStatus.Accepted, hasAsset: record.HasData, ownerSlot: slot);
                Info("主机接受中途换肤（资源已存在，不重传）：玩家键 " + ownerKey + " → \"" +
                    skinName + "\" · " + SkinAssetPolicy.HashHex(record.Sha256));
                return;
            }

            // 内置皮肤：主机只需确认名称合法、写记录并广播（资源为空）。
            if (!m_records.IsKnownBuiltInSkin(skinName, out string builtInReason))
            {
                RejectChange(senderClientId, peer, skinName, message, builtInReason,
                    SkinAssetRejectReason.InvalidName);
                return;
            }
            var builtIn = new PlayerSkinAssetRecord
            {
                SkinName = skinName,
                Sex = sex,
                Sha256 = Array.Empty<byte>(),
                Data = Array.Empty<byte>()
            };
            if (!m_records.StoreRecordSkin(identity, peer.Name, senderClientId, builtIn,
                    slot, out string recordFailure))
            {
                RejectChange(senderClientId, peer, skinName, message,
                    "写角色记录失败（槽位 " + slot + "）：" + recordFailure,
                    SkinAssetRejectReason.StoreFailed, slot);
                return;
            }
            m_records.UpdateSkinAppearance(senderClientId, identity, peer.Name, builtIn, slot);
            m_skin.TryApplyAvatarSkin(ownerKey, builtIn, allowPlaceholder: false, out _,
                out string builtInApplyReason);
            if (builtInApplyReason != null)
            {
                Info("应用内置皮肤到玩家键 " + ownerKey + " 失败（" + builtInApplyReason +
                    "）；资料已写记录，化身建好后会再应用");
            }
            // 内置皮肤：发起者只收一条 accepted 回执；其他端只收变化通知（没有资源分片）。
            NotifyChangedExcept(senderClientId, senderClientId, identity, builtIn, isHost: false,
                status: Proto.SkinChangeStatus.Accepted, hasAsset: false, ownerSlot: slot);
            SendAck(senderClientId, senderClientId, identity, builtIn,
                Proto.SkinChangeStatus.Accepted, reason: null, slot: slot);
            Info("主机接受中途换内置皮肤：玩家键 " + ownerKey + " → \"" + skinName + "\"");
        }

        private void RejectChange(int senderClientId, Peer peer, string skinName,
            Proto.PlayerSkinChanged request, string reason, SkinAssetRejectReason kind,
            int slot = 0)
        {
            CountReject(kind, reason, tooLarge: kind == SkinAssetRejectReason.Oversize);
            m_counters.OnChangeRejected(reason);
            var evidence = new PlayerSkinAssetRecord
            {
                SkinName = skinName ?? string.Empty,
                Sex = request != null && request.PlayerClass == (uint)PlayerSex.Female
                    ? PlayerSex.Female
                    : PlayerSex.Male,
                Sha256 = request?.Sha256?.Length > 0
                    ? request.Sha256.ToByteArray()
                    : Array.Empty<byte>(),
                Data = Array.Empty<byte>()
            };
            SendAck(senderClientId, senderClientId, peer?.Identity ?? string.Empty, evidence,
                Proto.SkinChangeStatus.Rejected, reason, slot);
            LogReject(kind, "clientId=" + senderClientId + " 换肤 \"" + skinName + "\" 被拒：" + reason,
                senderClientId);
        }

        private void HandleClientSkinChanged(Proto.PlayerSkinChanged message)
        {
            int owner = message.ClientId;
            Proto.SkinChangeStatus status = (Proto.SkinChangeStatus)message.Status;
            string skinName = message.SkinName ?? string.Empty;
            if (status == Proto.SkinChangeStatus.Pending)
            {
                ErrorOnce("skin-changed-client-pending", "客户端收到 PENDING 皮肤变化通知（方向错误）→ 忽略");
                return;
            }
            int slot = (int)message.Slot;
            if (!PlayerEditSync.TryValidateSlot(slot, out string slotReason))
            {
                ErrorOnce("skin-changed-client-slot:" + owner + ":" + slot,
                    "客户端收到槽位非法的皮肤变化通知（" + slotReason + "）→ 丢弃（不夹紧）");
                return;
            }
            PlayerKey ownerKey = new PlayerKey(owner, slot);
            PlayerSkinAssetRecord record = null;
            if (SkinAssetPolicy.IsCustomSkinName(skinName))
            {
                byte[] hash = message.Sha256?.Length > 0 ? message.Sha256.ToByteArray() : Array.Empty<byte>();
                record = new PlayerSkinAssetRecord
                {
                    SkinName = skinName,
                    Sex = message.PlayerClass == (uint)PlayerSex.Female ? PlayerSex.Female : PlayerSex.Male,
                    Sha256 = hash,
                    Data = Array.Empty<byte>()
                };
            }
            else
            {
                record = new PlayerSkinAssetRecord
                {
                    SkinName = skinName,
                    Sex = message.PlayerClass == (uint)PlayerSex.Female ? PlayerSex.Female : PlayerSex.Male,
                    Sha256 = Array.Empty<byte>(),
                    Data = Array.Empty<byte>()
                };
            }

            bool isSelf = owner == m_localClientId;
            if (status == Proto.SkinChangeStatus.Accepted)
            {
                if (isSelf)
                {
                    if (slot == 0)
                    {
                        m_confirmedHash = record.HasHash ? record.HashHex : string.Empty;
                        if (m_profileStore?.Profile != null)
                        {
                            PlayerProfileSnapshot updated = m_profileStore.Profile;
                            updated.Appearance.SkinName = skinName;
                            updated.Appearance.SkinSha256 = record.Sha256 != null
                                ? (byte[])record.Sha256.Clone()
                                : System.Array.Empty<byte>();
                            m_profileStore.Set(updated, "host-accepted-skin");
                            m_confirmedProfile = updated.Clone();
                        }
                        m_pendingChangeHash = string.Empty;
                    }
                    Info("主机已接受本机皮肤变化（槽位 " + slot + "）：\"" + skinName + "\" · " +
                        (record.HasHash ? SkinAssetPolicy.HashHex(record.Sha256) : "内置皮肤"));
                    return;
                }
                if (m_skin == null) return;
                if (record.HasHash &&
                    m_skin.TryGetCachedAsset(record.Sha256, skinName, out PlayerSkinAssetRecord cached) &&
                    cached.HasData && SkinAssetPolicy.BytesEqual(cached.Sha256, record.Sha256))
                {
                    if (m_skin.TryApplyAvatarSkin(ownerKey, cached, allowPlaceholder: true,
                            out bool placeholder, out string applyReason))
                    {
                        m_counters.OnApplied();
                        if (placeholder) m_counters.OnPlaceholderShown();
                    }
                    else
                    {
                        Info("应用玩家键 " + ownerKey + " 的新皮肤失败（" + applyReason +
                            "）；资源已缓存");
                    }
                }
                else
                {
                    // 资源可能还在路上：先显示确定性占位（store 计数），资源到了会自动替换。
                    m_skin.TryApplyAvatarSkin(ownerKey, record, allowPlaceholder: true,
                        out bool placeholder, out _);
                    if (placeholder) m_counters.OnPlaceholderShown();
                }
                return;
            }

            if (status == Proto.SkinChangeStatus.Rejected)
            {
                string reason = message.Reason ?? string.Empty;
                if (isSelf)
                {
                    m_counters.OnChangeRejected(reason);
                    ErrorOnce("skin-change-rejected:" + ownerKey,
                        "主机拒绝了本机槽位 " + slot + " 的换肤（\"" + skinName + "\"）：" + reason +
                        (slot == 0 ? " → 回退到主机确认过的资料（不假装成功）" : "（非 0 槽位不回退本机槽位 0）"));
                    if (slot == 0) RevertToConfirmed(reason);
                }
                else
                {
                    ErrorOnce("skin-change-peer-rejected:" + ownerKey,
                        "玩家键 " + ownerKey + " 的换肤被主机拒绝：" + reason);
                }
                return;
            }

            if (status == Proto.SkinChangeStatus.Missing)
            {
                string reason = message.Reason ?? string.Empty;
                if (isSelf)
                {
                    m_counters.OnMissing(reason);
                    if (slot == 0 && m_pendingChangeHash.Length > 0 && m_skin != null &&
                        m_skin.TryGetCachedAsset(record.Sha256, skinName,
                            out PlayerSkinAssetRecord owned) && owned.HasData &&
                        NowMs() - m_retryMissingMs >= 5000)
                    {
                        m_retryMissingMs = NowMs();
                        if (SendSkinChangeToHost(owned, m_profileStore?.Profile, asChange: true,
                                slot, out string retryReason))
                        {
                            Info("主机缺资源 → 显式重传一次：" + reason);
                            return;
                        }
                        ErrorOnce("skin-missing-retry", "重传皮肤资源失败：" + retryReason);
                    }
                    ErrorOnce("skin-missing:" + ownerKey,
                        "主机没有本机槽位 " + slot + " 新皮肤的资源：" + reason +
                        " → 其他客户端会显示占位；请检查本机皮肤文件是否可读");
                }
                else if (m_skin != null)
                {
                    m_counters.OnMissing(reason);
                    m_skin.TryApplyAvatarSkin(ownerKey, record, allowPlaceholder: true,
                        out bool placeholder, out _);
                    if (placeholder) m_counters.OnPlaceholderShown();
                }
                return;
            }

            ErrorOnce("skin-changed-client-status:" + message.Status,
                "客户端收到未知皮肤变化状态 status=" + message.Status + " → 忽略");
        }

        private void RevertToConfirmed(string reason)
        {
            m_pendingChangeHash = string.Empty;
            if (m_confirmedProfile == null || m_skin == null)
            {
                ErrorOnce("skin-revert-none", "主机拒绝了换肤，但本机没有主机确认过的资料可回退：" +
                    reason + "（保持当前皮肤并明确记录）");
                return;
            }
            m_profileStore?.Set(m_confirmedProfile, "host-rejected-skin");
            PlayerSkinAssetRecord record = new PlayerSkinAssetRecord
            {
                SkinName = m_confirmedProfile.Appearance?.SkinName ?? string.Empty,
                Sex = m_confirmedProfile.Appearance?.Sex ?? PlayerSex.Male,
                Sha256 = m_confirmedProfile.Appearance?.SkinSha256 != null
                    ? (byte[])m_confirmedProfile.Appearance.SkinSha256.Clone()
                    : Array.Empty<byte>(),
                Data = Array.Empty<byte>()
            };
            if (!m_skin.TryApplyLocalSkin(record, allowPlaceholder: true, out bool placeholder,
                    out string applyReason))
                ErrorOnce("skin-revert-apply", "回退本机皮肤失败：" + applyReason);
            else if (placeholder)
                m_counters.OnPlaceholderShown();
            Info("已回退本机皮肤到主机确认过的资料：\"" + record.SkinName + "\"");
        }

        // ------------------------------------------------------------ 加入 / 离开

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!IsHostingNow || clientId <= 0) return;
            m_peers[clientId] = new Peer
            {
                ClientId = clientId,
                Identity = (identity ?? string.Empty).Trim(),
                Name = name ?? string.Empty
            };
            m_missingSent.RemoveWhere(key => key.StartsWith(clientId + ":", StringComparison.Ordinal));

            // ① 主机自己的自定义皮肤：新客户端必须拿到（否则它只能显示占位）。
            if (m_hostSkinHash.Length > 0 && m_skin != null)
            {
                string hostSkinName = string.Empty;
                if (m_records.TryCaptureLocalProfile(out PlayerProfileSnapshot hostProfile, out _))
                    hostSkinName = hostProfile.Appearance?.SkinName ?? string.Empty;
                if (hostSkinName.Length > 0 &&
                    m_skin.TryGetCachedAsset(null, hostSkinName, out PlayerSkinAssetRecord hostRecord) &&
                    hostRecord.HasData)
                {
                    SendAssetToClient(clientId, hostRecord, HostSlotKey, LocalIdentity(), isHost: true);
                }
                else
                {
                    m_counters.OnMissing("主机本机皮肤资源不在缓存（" + hostSkinName + "）");
                    SendChanged(clientId, HostSlotKey, LocalIdentity(), null, null,
                        Proto.SkinChangeStatus.Missing, hasAsset: false, isHost: true,
                        reason: "主机本机皮肤资源不可用");
                }
            }

            // ② 房间里**已有的其他玩家**的自定义皮肤：新客户端也要拿到（否则只能显示占位）。
            //    数据源是主机角色记录（世界作用域），所以即使主机重启过、缓存是空的也能补发。
            foreach (KeyValuePair<int, Peer> existing in new List<KeyValuePair<int, Peer>>(m_peers))
            {
                if (existing.Key == clientId) continue;
                if (!m_records.TryGetRecordSkin(existing.Value.Identity, existing.Value.Name,
                        out PlayerSkinAssetRecord peerSkin, out _) || peerSkin == null)
                    continue;
                if (!SkinAssetPolicy.IsCustomSkinName(peerSkin.SkinName) || !peerSkin.HasData)
                    continue;
                m_skin?.StoreAsset(peerSkin, SkinAssetOrigin.HostRecord, out _, out _);
                SendAssetToClient(clientId, peerSkin, existing.Key, existing.Value.Identity,
                    isHost: false);
                Info("新客户端加入：补发已有玩家 clientId=" + existing.Key + " 的自定义皮肤 \"" +
                    peerSkin.SkinName + "\"");
            }

            // ③ 这个客户端的角色记录里保存的资源：重连时直接补发，客户端不需要重新上传。
            if (m_records.TryGetRecordSkin(identity, name, out PlayerSkinAssetRecord record,
                    out string recordReason) && record != null &&
                SkinAssetPolicy.IsCustomSkinName(record.SkinName))
            {
                if (record.HasData && m_skin != null)
                {
                    m_skin.StoreAsset(record, SkinAssetOrigin.HostRecord, out _, out _);
                    SendAssetToClient(clientId, record, clientId, identity, isHost: false);
                    // 这个玩家自己的资源也要补发给**房间里已经在的其他端**（他们可能在它
                    // 之前加入、当时记录里还没有这份资源）。owner 自己的连接跳过。
                    foreach (int peerId in new List<int>(m_peers.Keys))
                    {
                        if (peerId == clientId) continue;
                        SendAssetToClient(peerId, record, clientId, identity, isHost: false);
                    }
                    Info("重连 / 新加入：从主机角色记录补发自定义皮肤给 clientId=" + clientId +
                        " · \"" + record.SkinName + "\"（并同步给已有玩家端；客户端无需重新上传）");
                }
                else
                {
                    m_counters.OnMissing("角色记录缺少资源：" + record.SkinName);
                    m_missingSent.Add(clientId + ":" + record.Key);
                    SendChanged(clientId, clientId, identity, null, record,
                        Proto.SkinChangeStatus.Missing, hasAsset: false, isHost: false,
                        reason: "主机角色记录里没有这份皮肤资源：" + recordReason);
                    Warn("clientId=" + clientId + " 的角色记录声明了自定义皮肤 \"" +
                        record.SkinName + "\"，但记录里没有资源 → 已告诉它 MISSING；" +
                        "其他端显示占位（客户端可显式重传）");
                }
            }
            else if (recordReason != null && recordReason.Length > 0)
            {
                Info("clientId=" + clientId + " 没有可补发的皮肤记录（" + recordReason + "）");
            }
        }

        private void OnClientLeft(int clientId, string reason)
        {
            m_peers.Remove(clientId);
            var stale = new List<string>();
            foreach (string key in m_uploads.Keys)
                if (key.StartsWith(clientId + ":", StringComparison.Ordinal)) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) m_uploads.Remove(stale[i]);
            stale.Clear();
            foreach (string key in m_acceptedUploads.Keys)
                if (key.StartsWith(clientId + ":", StringComparison.Ordinal)) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) m_acceptedUploads.Remove(stale[i]);
            m_missingSent.RemoveWhere(key => key.StartsWith(clientId + ":", StringComparison.Ordinal));
            m_skin?.ForgetAvatar(clientId);
        }

        private bool SendAssetToClient(int clientId, PlayerSkinAssetRecord record, int ownerClientId,
            string identity, bool isHost)
        {
            if (record == null || !record.HasData || m_messages == null) return false;
            SkinAssetCheck check = SkinAssetCodec.TryEncode(record.SkinName, record.Sex, record.Data,
                ownerClientId, identity, NextTransferId(),
                out List<Proto.PlayerSkinAsset> messages, out byte[] sha256);
            if (!check.Ok)
            {
                CountReject(check.Kind, check.Reason,
                    tooLarge: check.Kind == SkinAssetRejectReason.Oversize);
                return false;
            }
            for (int i = 0; i < messages.Count; i++)
            {
                if (Send(clientId, messages[i])) continue;
                m_counters.OnSendFailed("补发皮肤给 clientId=" + clientId + " 第 " + i + " 片");
                return false;
            }
            m_counters.OnSent();
            SendChanged(clientId, ownerClientId, identity, null, record,
                Proto.SkinChangeStatus.Accepted, hasAsset: true, isHost: isHost);
            return true;
        }

        // ------------------------------------------------------------ 过期 / 计数 / 日志

        private void ExpireTransfers(long nowMs)
        {
            var expired = new List<string>();
            foreach (KeyValuePair<string, SkinAssetAssembler> item in m_uploads)
                if (item.Value.IsExpired(nowMs)) expired.Add("up:" + item.Key);
            for (int i = 0; i < expired.Count; i++)
            {
                string key = expired[i].Substring(3);
                m_uploads.Remove(key);
                m_counters.OnRejected(SkinAssetRejectReason.Malformed,
                    "上传传输超时（" + SkinAssetPolicy.TransferTimeoutMs + " ms 未收齐）：" + key);
                Warn("丢弃超时的皮肤上传传输：" + key + "（已计数；发送方可显式重发）");
            }
            expired.Clear();
            foreach (KeyValuePair<string, SkinAssetAssembler> item in m_downloads)
                if (item.Value.IsExpired(nowMs)) expired.Add("down:" + item.Key);
            for (int i = 0; i < expired.Count; i++)
            {
                string key = expired[i].Substring(5);
                m_downloads.Remove(key);
                m_counters.OnRejected(SkinAssetRejectReason.Malformed,
                    "接收传输超时（" + SkinAssetPolicy.TransferTimeoutMs + " ms 未收齐）：" + key);
                Warn("丢弃超时的皮肤接收传输：" + key + "（已计数）");
            }
        }

        private int CountUploads(int clientId)
        {
            int count = 0;
            string prefix = clientId + ":";
            foreach (string key in m_uploads.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) count++;
            return count;
        }

        private void Reject(int senderClientId, SkinAssetRejectReason kind, string reason,
            int targetClientId, bool upload = false, bool tooLarge = false,
            string skinName = null, byte[] sha256 = null, string identity = null, int slot = 0)
        {
            CountReject(kind, reason, tooLarge);
            if (upload) m_counters.OnUploadRejected(reason);
            LogReject(kind, reason, senderClientId);
            if (targetClientId <= 0) return;
            var evidence = new PlayerSkinAssetRecord
            {
                SkinName = skinName ?? string.Empty,
                Sex = PlayerSex.Male,
                Sha256 = sha256 != null ? (byte[])sha256.Clone() : Array.Empty<byte>(),
                Data = Array.Empty<byte>()
            };
            SendAck(targetClientId, senderClientId > 0 ? senderClientId : targetClientId,
                identity ?? string.Empty, evidence, Proto.SkinChangeStatus.Rejected, reason, slot);
        }

        private void LogReject(SkinAssetRejectReason kind, string reason, int senderClientId)
        {
            string key = kind + ":" + reason;
            long now = NowMs();
            if (m_lastReasonLogMs.TryGetValue(key, out long last) &&
                now - last < SkinAssetPolicy.LogIntervalMs)
                return;
            m_lastReasonLogMs[key] = now;
            Error("皮肤资源被拒绝（" + SkinCountersKind(kind) + (senderClientId > 0
                ? " · clientId=" + senderClientId : string.Empty) + "）：" + reason);
        }

        private static string SkinCountersKind(SkinAssetRejectReason kind) =>
            SkinAssetCounters.KindName(kind);

        private void LogCounters(long nowMs)
        {
            long total = m_counters.Total;
            if (nowMs - m_lastLogMs < SkinAssetPolicy.LogIntervalMs) return;
            if (total == m_lastCounterTotal) return;
            m_lastLogMs = nowMs;
            m_lastCounterTotal = total;
            Info("皮肤资源计数：" + m_counters.Describe());
            m_counters.ResetReasonDetails();
        }

        private static long NowMs() => Environment.TickCount64;

        private static string SkinSignature(PlayerProfileSnapshot profile) =>
            (profile?.Name ?? string.Empty) + "\n" + (profile?.Appearance?.Signature ?? string.Empty);

        // ------------------------------------------------------------ 生命周期

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                try { m_transport.ClientJoined -= OnClientJoined; } catch (Exception) { }
                try { m_transport.ClientLeft -= OnClientLeft; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(m_descriptor.Id); } catch (Exception) { }
            }
            m_skin?.ForgetAll();
            m_uploads.Clear();
            m_downloads.Clear();
            m_sentUploads.Clear();
            m_receivedSelfHashes.Clear();
            m_missingSent.Clear();
            m_acceptedUploads.Clear();
            m_reportedOnce.Clear();
            m_peers.Clear();
            m_confirmedProfile = null;
            m_confirmedHash = string.Empty;
            m_pendingChangeHash = string.Empty;
            m_localSkinSignature = string.Empty;
            m_localIdentity = string.Empty;
            m_hostSkinHash = string.Empty;
            m_hostSkinName = string.Empty;
            m_handshake = null;
            m_broadcast = null;
            m_skin = null;
            m_records = null;
            m_transport = null;
            m_messages = null;
            m_attached = false;
            Info("皮肤资源模块下线（" + reason + "）：只清理本模块自己的状态（会话副本缓存之外不落盘）");
        }

        public string StatusLine() =>
            "player-skin: host=" + m_isHost +
            " peers=" + m_peers.Count +
            " uploads=" + m_uploads.Count +
            " downloads=" + m_downloads.Count +
            " cached=" + (m_skin?.Describe() ?? "n/a") +
            " " + m_counters.Describe();

        // ------------------------------------------------------------ 日志

        private void Info(string message) => m_context?.Diagnostics.Info(m_descriptor.Id, message);

        private void Warn(string message) => m_context?.Diagnostics.Warn(m_descriptor.Id, message);

        private void Error(string message) => m_context?.Diagnostics.Error(m_descriptor.Id, message, null);

        private void ErrorOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            m_context?.Diagnostics.Error(m_descriptor.Id, message, null);
        }
    }
}

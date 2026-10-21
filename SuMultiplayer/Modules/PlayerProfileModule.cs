using System;
using System.Collections.Generic;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using Proto = SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 玩家资料运行时同步（主机权威）：名字 / 职业 / 皮肤 / 衣服。
    ///
    /// 产品问题（2026-10-20）：客户端在 Edit Players 里把自己的名字改了，本机 PlayerData
    /// 确实变了，但主机和其他玩家看到的还是旧名字。旧路径只在 <c>PlayerScreen</c> 退出时
    /// 比较快照（<see cref="PlayerEditWatcher"/> → PLAYER_PROFILE 27），没有经过这次屏幕切换
    /// 的变化（或 TextBox 的 FocusLost 没来得及写回）永远到不了主机。
    ///
    /// 本模块（PLAYER_PROFILE_SYNC = 108，append-only）补的是**运行时变化通道**：
    ///   · 客户端每 750 ms 比较本机每个槽位的资料，**变化才发**（带 sequence/revision/session）；
    ///     上线 / 重连后强制发一次完整资料；旧路径（27）是否发过不影响这里 ——
    ///     重复资料由主机返回 Unchanged 吸收，绝不因为"旧路径发过"而放弃 108；
    ///   · 主机只接受 <c>client_id == 来源连接编号</c> 的上报（owner 严格校验），
    ///     校验名字（引擎同名规则）/ 职业 / 皮肤 / 衣服 → 交给联机角色模块写记录 + 更新化身
    ///     （不广播 27），然后由本模块把权威资料以 108 广播给**所有**客户端（含改名人自己作为确认）；
    ///   · 客户端收到 108：自己 → 写本机资料存储 + 本机 PlayerData；
    ///     主机（client_id=0）/ 其他客户端 → 更新名册与远端化身；旧 revision 一律忽略。
    ///
    /// 不做兜底：名字 / 皮肤 / 职业 / 衣服非法 → 明确拒绝并保留旧值；发送 / 广播失败 → 计数 +
    /// 10 秒限流日志，不静默。
    ///
    /// 无静态状态；引擎读写全部经 <see cref="IPlayerProfileAuthorityPort"/>（实现方 = PlayerAvatarModule）。
    /// </summary>
    public sealed class PlayerProfileModule : IMultiplayerModule
    {
        public const string ModuleIdText = "player-profile";

        private readonly ModuleDescriptor m_descriptor;
        private readonly IPlayerProfileAuthorityPort m_authority;
        private readonly IProfileSkinCatalog m_skinCatalog;
        private readonly Func<double> m_now;
        private readonly ProfileSyncCounters m_counters = new ProfileSyncCounters();
        private readonly ProfileSyncLogLimiter m_logs = new ProfileSyncLogLimiter();
        private readonly ProfileChangeDetector m_detector = new ProfileChangeDetector();
        private readonly ProfileSequenceBook m_hostBooks = new ProfileSequenceBook();
        private readonly ProfileSequenceBook m_clientBooks = new ProfileSequenceBook();
        private readonly Dictionary<PlayerKey, uint> m_hostRevisions = new Dictionary<PlayerKey, uint>();
        private readonly Dictionary<int, uint> m_clientRevisions = new Dictionary<int, uint>();
        /// <summary>客户端：槽位 → 最近一次**抓到**的资料签名（用于 profileLocalChanges 计数）。</summary>
        private readonly Dictionary<int, string> m_lastCapturedSignatures =
            new Dictionary<int, string>();
        /// <summary>槽位 → 最近一次本机皮肤键（性别 | 皮肤名 | 自定义哈希：变化才触发皮肤资源链）。</summary>
        private readonly Dictionary<int, string> m_lastSkinKeys = new Dictionary<int, string>();

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private IMessageRegistryPort m_messages;
        private IPeerBroadcastPort m_broadcast;
        private IProfileSkinSyncPort m_skinSync;
        private bool m_attached;
        private bool m_portsReported;

        private uint m_hostSession;
        private uint m_hostSequence;
        private uint m_clientSession;
        private uint m_clientSequence;
        private uint m_sessionCounter;
        private int m_lastLocalClientId = -1;
        private bool m_wasConnected;
        private bool m_forceInitialSend;
        private long m_lastCheckMs;
        private long m_lastHostCheckMs;

        public PlayerProfileModule(IPlayerProfileAuthorityPort authority,
            IProfileSkinCatalog skinCatalog, Func<double> now)
        {
            m_authority = authority ?? throw new ArgumentNullException(nameof(authority));
            m_skinCatalog = skinCatalog;
            m_now = now ?? throw new ArgumentNullException(nameof(now));
            m_descriptor = new ModuleDescriptor(ModuleIdText, "玩家资料运行时同步（主机权威）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "player.profile" },
                HotPluggable = true
            };
            m_hostSession = NewSession();
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public ProfileSyncCounters Counters => m_counters;

        public string StatusLine() =>
            "player-profile: " + m_counters.Describe() +
            " profileSlots=" + (m_authority?.LocalPlayerSlotCount ?? 0) +
            " profileHostSession=" + m_hostSession +
            " profileClientSession=" + m_clientSession +
            " profileHostSeq=" + m_hostSequence +
            " profileClientSeq=" + m_clientSequence;

        // ---------------------------------------------------------------- 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context ?? throw new ArgumentNullException(nameof(context));
            if (!context.TryGetPort(out m_transport))
            {
                Error("没有拿到传输端口（ITransportPort）→ 资料同步无法收发；名字/皮肤只会留在本机");
                return;
            }
            if (!context.TryGetPort(out m_messages))
            {
                Error("没有拿到消息注册表（IMessageRegistryPort）→ 无法注册 PLAYER_PROFILE_SYNC（108）");
            }
            if (!context.TryGetPort(out m_broadcast))
            {
                Warn("没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把权威资料广播给所有客户端");
            }
            // 皮肤资源端口（IProfileSkinSyncPort）由组合根在皮肤模块建好后注册；这里的注册顺序
            // 可能还没有它 —— 检测到本机换肤时再懒取（拿不到就明确计数 + 限流写原因，不静默）。
            context.TryGetPort(out m_skinSync);
            if (m_skinCatalog == null)
            {
                Error("没有皮肤目录（IProfileSkinCatalog）→ 内置皮肤无法校验；资料同步会明确拒绝" +
                    "（不会换成默认皮肤）");
            }
            if (m_messages != null)
            {
                MessageRegistry registry = m_messages.Registry;
                registry.UnregisterOwner(ModuleIdText);
                registry.Register((int)Proto.MessageType.PlayerProfileSync,
                    Proto.PlayerProfileSync.Parser,
                    new DelegateMessageHandler<Proto.PlayerProfileSync>(OnProfileSyncMessage),
                    ModuleIdText);
            }
            m_transport.ClientLeft += OnClientLeft;
            m_hostSession = NewSession();
            m_attached = true;
            Info("玩家资料运行时同步就绪：客户端 750 ms 比较本机资料（变化才发 108）；" +
                "主机按 owner 严格校验后写记录/化身并广播 108（旧 PLAYER_PROFILE 27 保留给编辑屏/加入）");
        }

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                try { m_transport.ClientLeft -= OnClientLeft; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleIdText); } catch (Exception) { }
            }
            m_detector.Reset();
            m_hostBooks.Clear();
            m_clientBooks.Clear();
            m_hostRevisions.Clear();
            m_clientRevisions.Clear();
            m_lastCapturedSignatures.Clear();
            m_lastSkinKeys.Clear();
            Info("玩家资料运行时同步下线（" + reason + "）：" + m_counters.Describe());
            m_transport = null;
            m_messages = null;
            m_broadcast = null;
            m_skinSync = null;
            m_context = null;
            m_attached = false;
            m_portsReported = false;
        }

        // ---------------------------------------------------------------- Tick

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            long nowMs = (long)(tick.Now * 1000.0);
            if (tick.IsHost)
            {
                // 主机自己的角色换肤：广播给所有客户端（客户端换肤走 TickClient 的上报通道）。
                TickHostLocal(nowMs);
            }
            else
            {
                TickClient(nowMs);
            }
            LogSummaryIfDue(nowMs);
        }

        /// <summary>
        /// 主机：750 ms 低频检查**本机角色槽位 0** 的皮肤是否变化，变化才交给皮肤资源链
        /// （自定义皮肤 → 写记录 + 分片广播给所有客户端；内置皮肤 → 一条 accepted 变化通知）。
        /// 名字/衣服由编辑屏退出通道广播，这里只处理换肤。
        /// </summary>
        private void TickHostLocal(long nowMs)
        {
            if (nowMs - m_lastHostCheckMs < ProfileSyncProtocol.CheckIntervalMs) return;
            m_lastHostCheckMs = nowMs;
            if (!m_authority.TryCaptureLocalProfile(0, out PlayerProfileSnapshot profile,
                    out string reason))
            {
                WarnRateLimited("host-capture",
                    "资料同步：主机本机槽位 0 的资料取不到（" + reason + "）→ 本帧不检测主机换肤");
                return;
            }
            DetectLocalProfileChange(0, profile);
            DetectLocalSkinChange(0, profile, isHost: true);
        }

        private void TickClient(long nowMs)
        {
            if (m_transport == null || m_messages == null) return;
            if (!m_transport.IsConnected)
            {
                if (m_wasConnected)
                {
                    m_wasConnected = false;
                    m_lastLocalClientId = -1;
                    ResetClientState();
                    Info("客户端连接断开 → 清空资料同步账本（重连从新 session 重新发完整资料）");
                }
                return;
            }
            int localClientId = m_transport.LocalClientId;
            if (localClientId < 0)
            {
                if (m_wasConnected)
                {
                    m_wasConnected = false;
                    m_lastLocalClientId = -1;
                    ResetClientState();
                    Info("重新握手（LocalClientId=-1）→ 清空资料同步账本，等新编号后重发完整资料");
                }
                return;
            }
            if (!m_wasConnected || localClientId != m_lastLocalClientId)
            {
                m_wasConnected = true;
                m_lastLocalClientId = localClientId;
                ResetClientState();
                m_clientSession = NewSession();
                m_forceInitialSend = true;
                Info("资料同步：本机通道编号 " + localClientId + " · session=" + m_clientSession +
                    " → 上线/重连先发一次完整资料，之后只在资料变化时发");
            }
            if (!m_authority.IsSelfProfileReady) return;
            if (nowMs - m_lastCheckMs < ProfileSyncProtocol.CheckIntervalMs) return;
            m_lastCheckMs = nowMs;

            int slots = m_authority.LocalPlayerSlotCount;
            if (slots <= 0)
            {
                WarnRateLimited("no-local-slots",
                    "资料同步：本机没有可同步的角色（世界未就绪 / PlayersData 为空）→ 本帧不发");
                return;
            }
            bool attempted = false;
            for (int slot = 0; slot < slots; slot++)
            {
                if (!m_authority.TryCaptureLocalProfile(slot, out PlayerProfileSnapshot profile,
                        out string captureReason))
                {
                    WarnRateLimited("capture:" + slot,
                        "资料同步：本机槽位 " + slot + " 的资料取不到（" + captureReason + "）→ 该槽位本帧跳过");
                    continue;
                }
                if (slot > 0 && !m_authority.WasLocalSlotAnnounced(slot))
                {
                    // 主机对未公告槽位的资料会拒绝；先等 LOCAL_PLAYER_SLOTS 发成功再发 108。
                    WarnRateLimited("slot-unannounced:" + slot,
                        "资料同步：本机槽位 " + slot + " 还没有被 LOCAL_PLAYER_SLOTS 公告 → 本帧不发" +
                        "（主机不会接受未公告槽位的资料）");
                    continue;
                }
                string signature = profile.Signature;
                // 变化检测的数据源是**本机引擎角色**（IPlayerProfileAuthorityPort 实现 = 联机角色模块的
                // TryGetLocalPlayerAtSlot → PlayersData 里的本机 PlayerData.Name / CharacterSkinName /
                // PlayerClass / ComponentClothing），不是 PlayersData 记录表或本机资料存储的副本 ——
                // 编辑玩家屏（PlayerScreen）改的就是这条 PlayerData，只有直接读它才能看到真实修改。
                DetectLocalProfileChange(slot, profile);
                DetectLocalSkinChange(slot, profile, isHost: false);
                // 变化才发：同一签名不再发；旧路径（27）是否发过不影响这里 ——
                // 主机对重复资料会返回 Unchanged（不重复广播），但绝不能因为"旧路径发过"
                // 就跳过 108：旧路径漏发/被拒时，108 是主机拿到新名字的唯一机会。
                if (!m_detector.ShouldSend(slot, signature, nowMs, m_forceInitialSend)) continue;
                attempted = true;
                SendReport(slot, profile, signature, nowMs);
            }
            // 首份完整资料只在这一轮尝试一次；失败的槽位由 detector 在 750 ms 后继续重试。
            if (m_forceInitialSend && slots > 0) m_forceInitialSend = false;
            if (attempted && !m_portsReported)
            {
                m_portsReported = true;
                Info("资料同步：已开始发送本机资料（客户端 → 主机，PLAYER_PROFILE_SYNC=108）");
            }
        }

        private void SendReport(int slot, PlayerProfileSnapshot profile, string signature, long nowMs)
        {
            m_clientSequence++;
            m_clientRevisions.TryGetValue(slot, out uint revision);
            revision++;
            m_clientRevisions[slot] = revision;
            var key = new PlayerKey(m_lastLocalClientId, slot);
            Proto.PlayerProfileSync message = PlayerProfileSyncCodec.ToMessage(key,
                m_authority.LocalIdentity, profile, m_clientSequence, revision, m_clientSession);
            if (!m_messages.Sender.Send(0, message))
            {
                m_counters.SendFailed++;
                m_detector.MarkAttempt(slot, nowMs);
                WarnRateLimited("send:" + slot,
                    "资料同步：上报本机槽位 " + slot + "（名字=" + profile.Name +
                    "）发送失败（通道不可用 / 队列满）→ 计数 profileSendFailed，750 ms 后重试");
                return;
            }
            m_counters.Sent++;
            m_detector.MarkSent(slot, signature);
            m_detector.MarkAttempt(slot, nowMs);
            InfoRateLimited("sent:" + slot,
                "资料同步：已上报本机槽位 " + slot + "（" + key + " · 名字=" + profile.Name +
                " · 性别=" + profile.Appearance.Sex + " · 皮肤=" + profile.Appearance.SkinName +
                " · seq=" + m_clientSequence + " rev=" + revision + " session=" + m_clientSession + "）");
        }

        private void ResetClientState()
        {
            m_detector.Reset();
            m_clientBooks.Clear();
            m_clientRevisions.Clear();
            m_lastCapturedSignatures.Clear();
            m_lastSkinKeys.Clear();
            m_clientSequence = 0;
            m_forceInitialSend = false;
            m_lastCheckMs = 0;
        }

        /// <summary>
        /// 计数"抓到的本机资料签名变化"（名字 / 皮肤 / 衣服 / 职业任一变化）。
        /// 只做统计：发送由 <see cref="ProfileChangeDetector"/> 决定（失败会重试，不重复计变化）。
        /// </summary>
        private void DetectLocalProfileChange(int slot, PlayerProfileSnapshot profile)
        {
            string signature = profile?.Signature ?? string.Empty;
            if (m_lastCapturedSignatures.TryGetValue(slot, out string previous) &&
                string.Equals(previous, signature, StringComparison.Ordinal))
                return;
            m_lastCapturedSignatures[slot] = signature;
            if (previous == null) return;     // 首次抓取只建立基线，不算"变化"
            m_counters.LocalChanges++;
            InfoRateLimited("local-change:" + slot,
                "资料同步：检测到本机槽位 " + slot + " 的资料变化（名字=" +
                (profile?.Name ?? string.Empty) + " · 皮肤=" +
                (profile?.Appearance?.SkinName ?? string.Empty) + "）→ 750 ms 内上报主机");
        }

        /// <summary>
        /// 皮肤变化检测（与资料 750 ms 检测同频）：皮肤键（性别 | 皮肤名 | 自定义哈希）变化才触发
        /// 皮肤资源链。内置皮肤只做本机应用（皮肤名由 108 资料消息携带）；自定义 .scskin 走既有
        /// 分片上传 + 主机校验 + 转发链（该链路由 PlayerSkinModule 实现，本模块不复制）。
        /// </summary>
        private void DetectLocalSkinChange(int slot, PlayerProfileSnapshot profile, bool isHost)
        {
            string key = ProfileSyncDiff.SkinKey(profile);
            bool hasPrevious = m_lastSkinKeys.TryGetValue(slot, out string previous);
            if (hasPrevious && string.Equals(previous, key, StringComparison.Ordinal)) return;
            m_lastSkinKeys[slot] = key;
            if (!hasPrevious) return;          // 首次抓取只建立基线（上线首份资料不是换肤）
            m_counters.LocalSkinChanges++;
            TriggerSkinSync(slot, profile, isHost);
        }

        private void TriggerSkinSync(int slot, PlayerProfileSnapshot profile, bool isHost)
        {
            if (m_skinSync == null && m_context != null)
                m_context.TryGetPort(out m_skinSync);   // 端口在组合根装配皮肤模块后注册（懒取）
            if (m_skinSync == null)
            {
                m_counters.SkinSyncFailures++;
                ErrorRateLimited("skin-sync-port:" + slot,
                    "资料同步：检测到槽位 " + slot + " 换肤，但没有皮肤资源端口（IProfileSkinSyncPort）" +
                    "→ 换肤不会上传 / 广播（计数 profileSkinSyncFailures）；检查组合根是否注册了皮肤模块");
                return;
            }
            m_counters.SkinSyncRequests++;
            if (m_skinSync.TrySyncDetectedSkin(profile, slot, isHost, out string reason)) return;
            m_counters.SkinSyncFailures++;
            ErrorRateLimited("skin-sync:" + slot + ":" + reason,
                "资料同步：槽位 " + slot + " 的换肤没有走通皮肤资源链：" + reason +
                " → 计数 profileSkinSyncFailures（不换默认皮肤、不静默）");
        }

        private static string DescribeChanges(bool nameChanged, bool skinChanged, bool clothesChanged)
        {
            string text = (nameChanged ? "名字 " : string.Empty) +
                (skinChanged ? "皮肤 " : string.Empty) +
                (clothesChanged ? "衣服" : string.Empty);
            return text.Length > 0 ? text.TrimEnd() : "无";
        }

        // ---------------------------------------------------------------- 入站

        private void OnProfileSyncMessage(MessageContext context, Proto.PlayerProfileSync message)
        {
            if (!m_attached) return;
            if (message == null)
            {
                m_counters.Malformed++;
                ErrorRateLimited("null", "资料同步：收到空消息 → 丢弃（不猜任何字段）");
                return;
            }
            bool hosting = m_broadcast != null && m_broadcast.IsHosting;
            if (hosting) HandleHostReport(context, message);
            else HandleClientBroadcast(context, message);
        }

        /// <summary>主机：一条客户端上报。owner / 身份 / 名字 / 皮肤 / 衣服全部通过才应用与广播。</summary>
        private void HandleHostReport(MessageContext context, Proto.PlayerProfileSync message)
        {
            m_counters.Recv++;
            int sourceClientId = context.ClientId;
            if (!ProfileOwnerRule.TryValidateClientReport(sourceClientId, message.ClientId,
                    out string ownerReason))
            {
                m_counters.RejectedBy++;
                ErrorRateLimited("owner:" + sourceClientId + ":" + message.ClientId,
                    "资料同步：拒绝 clientId=" + sourceClientId + " 的上报 —— " + ownerReason);
                return;
            }
            if (!PlayerProfileSyncCodec.TryDecode(message, out ProfileSyncPayload payload,
                    out string decodeReason))
            {
                CountDecodeFailure(decodeReason);
                ErrorRateLimited("decode:" + sourceClientId,
                    "资料同步：clientId=" + sourceClientId + " 的消息结构非法：" + decodeReason +
                    " → 丢弃（保留旧资料）");
                return;
            }
            if (!m_hostBooks.TryAcceptReport(payload.Key, payload.Session, payload.Sequence,
                    payload.Revision, out bool stale, out string sequenceReason))
            {
                if (stale) m_counters.Stale++;
                else m_counters.RejectedBy++;
                WarnRateLimited("seq:" + payload.Key,
                    "资料同步：玩家键 " + payload.Key + " 的旧/非法包被忽略（" + sequenceReason + "）");
                return;
            }
            string identity = payload.Identity;
            if (identity.Length > 0 &&
                m_authority.TryGetPeerIdentity(sourceClientId, out string helloIdentity) &&
                helloIdentity.Length > 0 &&
                !string.Equals(identity, helloIdentity, StringComparison.Ordinal))
            {
                m_counters.RejectedBy++;
                ErrorRateLimited("identity:" + sourceClientId,
                    "资料同步：clientId=" + sourceClientId + " 上报的身份与 Hello 身份不一致 → 拒绝" +
                    "（不允许冒领别的身份）");
                return;
            }
            if (identity.Length == 0 && m_authority.TryGetPeerIdentity(sourceClientId, out string knownIdentity))
                identity = knownIdentity;

            if (!ProfileSyncValidation.TryValidateName(payload.Profile.Name, out string nameReason))
            {
                m_counters.NameInvalid++;
                ErrorRateLimited("name:" + payload.Key,
                    "资料同步：玩家键 " + payload.Key + " 的名字 \"" + payload.Profile.Name +
                    "\" 非法：" + nameReason + " → 保留旧名字");
                return;
            }
            if (!ProfileSyncValidation.TryValidateSkin(payload.Profile.Appearance.SkinName,
                    payload.Profile.Appearance.Sex, payload.Profile.Appearance.SkinSha256,
                    m_skinCatalog, out string skinReason))
            {
                m_counters.SkinRejected++;
                ErrorRateLimited("skin:" + payload.Key,
                    "资料同步：玩家键 " + payload.Key + " 的皮肤未通过校验：" + skinReason +
                    " → 保留旧皮肤（不换默认皮肤、不改职业）");
                return;
            }
            if (!m_authority.TryValidateProfileForSync(payload.Profile, payload.Key.Slot,
                    out string profileReason))
            {
                m_counters.ProfileRejected++;
                ErrorRateLimited("profile:" + payload.Key,
                    "资料同步：玩家键 " + payload.Key + " 的资料未通过主机校验：" + profileReason +
                    " → 保留旧资料（不换默认皮肤、不改职业）");
                return;
            }
            if (!payload.HasClothes)
            {
                // 空衣服 = 这次不上报衣服（不覆盖）；用已知资料补齐，拿不到就明确拒绝。
                if (!m_authority.TryGetKnownProfile(payload.Key,
                        out PlayerProfileSnapshot knownProfile) || knownProfile?.Appearance == null)
                {
                    m_counters.ClothesRejected++;
                    ErrorRateLimited("clothes-known:" + payload.Key,
                        "资料同步：玩家键 " + payload.Key +
                        " 没有带衣服且主机没有已知资料 → 拒绝（空列表不能把已有衣服清空）");
                    return;
                }
                payload.Profile.Appearance.Clothes =
                    PlayerAppearance.CloneClothes(knownProfile.Appearance.Clothes);
            }
            // 应用前抓一份已知资料：应用成功后据此分类统计名字 / 皮肤 / 衣服变化
            // （profileApplied 之外还要能看出"到底哪一项变了"；失败分支各自有独立计数）。
            m_authority.TryGetKnownProfile(payload.Key, out PlayerProfileSnapshot beforeProfile);
            ProfileSyncApplyOutcome outcome = m_authority.ApplyProfileSyncOnHost(sourceClientId,
                payload.Key, identity, payload.Profile, out string applyReason);
            if (outcome == ProfileSyncApplyOutcome.Rejected)
            {
                m_counters.ApplyFailed++;
                ErrorRateLimited("apply:" + payload.Key,
                    "资料同步：应用玩家键 " + payload.Key + " 的资料失败：" + applyReason +
                    " → 保留旧资料（不广播）");
                return;
            }
            if (outcome == ProfileSyncApplyOutcome.Unchanged)
            {
                m_counters.Unchanged++;
                InfoRateLimited("unchanged:" + payload.Key,
                    "资料同步：玩家键 " + payload.Key + " 的资料与记录一致 → 不重复广播" +
                    (string.IsNullOrEmpty(applyReason) ? string.Empty : "（" + applyReason + "）"));
                return;
            }
            m_counters.Applied++;
            bool nameChanged = false;
            bool skinChanged = false;
            bool clothesChanged = false;
            if (beforeProfile != null)
            {
                ProfileSyncDiff.Compare(beforeProfile, payload.Profile, out nameChanged,
                    out skinChanged, out clothesChanged);
            }
            if (nameChanged) m_counters.AppliedNameChanges++;
            if (skinChanged) m_counters.AppliedSkinChanges++;
            if (clothesChanged) m_counters.AppliedClothesChanges++;
            uint revision = NextHostRevision(payload.Key);
            uint sequence = m_hostSequence++;
            Proto.PlayerProfileSync broadcast = PlayerProfileSyncCodec.ToMessage(payload.Key,
                identity, payload.Profile, sequence, revision, m_hostSession);
            InfoRateLimited("applied:" + payload.Key,
                "资料同步：主机已应用并准备广播 " + payload.Key + "（名字=" + payload.Profile.Name +
                " · 性别=" + payload.Profile.Appearance.Sex + " · 皮肤=" +
                payload.Profile.Appearance.SkinName + " · rev=" + revision +
                " · 已知资料=" + (beforeProfile == null ? "无（首次登记）" : "有") +
                " · 变化=" + DescribeChanges(nameChanged, skinChanged, clothesChanged) + "）");
            Broadcast(broadcast);
        }

        /// <summary>客户端：主机广播的权威资料。旧 revision / 方向错误 / 非法内容都明确丢弃。</summary>
        private void HandleClientBroadcast(MessageContext context, Proto.PlayerProfileSync message)
        {
            m_counters.Recv++;
            if (!ProfileOwnerRule.TryValidateBroadcast(context.ClientId, message.ClientId,
                    out string directionReason))
            {
                m_counters.DirectionErrors++;
                ErrorRateLimited("direction:" + context.ClientId,
                    "资料同步：丢弃方向错误的消息（" + directionReason + "）");
                return;
            }
            if (!PlayerProfileSyncCodec.TryDecode(message, out ProfileSyncPayload payload,
                    out string decodeReason))
            {
                CountDecodeFailure(decodeReason);
                ErrorRateLimited("client-decode",
                    "资料同步：主机广播的消息结构非法：" + decodeReason + " → 丢弃（不部分应用）");
                return;
            }
            if (!m_clientBooks.TryAcceptBroadcast(payload.Key, payload.Session, payload.Revision,
                    out bool stale, out string sequenceReason))
            {
                if (stale) m_counters.Stale++;
                else m_counters.RejectedBy++;
                WarnRateLimited("client-seq:" + payload.Key,
                    "资料同步：丢弃旧/非法的权威广播（玩家键 " + payload.Key + " · " +
                    sequenceReason + "）");
                return;
            }
            if (!ProfileSyncValidation.TryValidateName(payload.Profile.Name, out string nameReason))
            {
                m_counters.NameInvalid++;
                ErrorRateLimited("client-name:" + payload.Key,
                    "资料同步：权威广播里的名字 \"" + payload.Profile.Name + "\" 非法：" +
                    nameReason + " → 保留旧名字");
                return;
            }
            if (!ProfileSyncValidation.TryValidateSkin(payload.Profile.Appearance.SkinName,
                    payload.Profile.Appearance.Sex, payload.Profile.Appearance.SkinSha256,
                    m_skinCatalog, out string skinReason))
            {
                m_counters.SkinRejected++;
                ErrorRateLimited("client-skin:" + payload.Key,
                    "资料同步：权威广播里的皮肤未通过校验：" + skinReason + " → 保留旧皮肤");
                return;
            }
            if (!m_authority.TryValidateProfileForSync(payload.Profile, payload.Key.Slot,
                    out string profileReason))
            {
                m_counters.ProfileRejected++;
                ErrorRateLimited("client-profile:" + payload.Key,
                    "资料同步：权威广播里的资料未通过校验：" + profileReason + " → 保留旧资料");
                return;
            }
            if (!payload.HasClothes)
            {
                if (!m_authority.TryGetKnownProfile(payload.Key,
                        out PlayerProfileSnapshot knownProfile) || knownProfile?.Appearance == null)
                {
                    m_counters.ClothesRejected++;
                    ErrorRateLimited("client-clothes:" + payload.Key,
                        "资料同步：广播没有带衣服且本机没有已知资料 → 拒绝（不能让空列表清空衣服）");
                    return;
                }
                payload.Profile.Appearance.Clothes =
                    PlayerAppearance.CloneClothes(knownProfile.Appearance.Clothes);
            }
            ProfileSyncApplyOutcome outcome = m_authority.ApplyProfileSyncBroadcast(payload.Key,
                payload.Identity, payload.Profile, payload.Key.ClientId == 0, out string applyReason);
            if (outcome == ProfileSyncApplyOutcome.Rejected)
            {
                m_counters.ApplyFailed++;
                ErrorRateLimited("client-apply:" + payload.Key,
                    "资料同步：应用权威资料失败（玩家键 " + payload.Key + "）：" + applyReason +
                    " → 保留旧资料");
                return;
            }
            if (outcome == ProfileSyncApplyOutcome.Unchanged)
            {
                m_counters.Unchanged++;
                InfoRateLimited("client-unchanged:" + payload.Key,
                    "资料同步：权威资料与本地一致（玩家键 " + payload.Key + "）→ 不重复应用");
                return;
            }
            m_counters.Applied++;
            InfoRateLimited("client-applied:" + payload.Key,
                "资料同步：已应用权威资料（玩家键 " + payload.Key + " · 名字=" +
                payload.Profile.Name + " · 皮肤=" + payload.Profile.Appearance.SkinName +
                " · rev=" + payload.Revision + "）");
        }

        private void CountDecodeFailure(string reason)
        {
            if (!string.IsNullOrEmpty(reason) && reason.Contains("衣服"))
                m_counters.ClothesRejected++;
            else
                m_counters.Malformed++;
        }

        private void Broadcast(Proto.PlayerProfileSync message)
        {
            if (m_broadcast == null)
            {
                m_counters.BroadcastFailures++;
                ErrorRateLimited("no-broadcast",
                    "资料同步：没有广播端口 → 无法把权威资料发给客户端（计数 profileBroadcastFailures）");
                return;
            }
            if (message == null)
            {
                m_counters.BroadcastFailures++;
                ErrorRateLimited("null-broadcast", "资料同步：广播消息为空 → 不发送（计数）");
                return;
            }
            byte[] payload = message.ToByteArray();
            int sent = m_broadcast.BroadcastToPlayers(Proto.MessageType.PlayerProfileSync, payload);
            m_counters.Sent += sent;
            int expected = m_broadcast.PlayerPeerCount;
            if (sent < expected)
            {
                m_counters.BroadcastFailures += expected - sent;
                ErrorRateLimited("broadcast:" + message.ClientId + ":" + message.Slot,
                    "资料同步：权威广播只成功入队 " + sent + "/" + expected + " 个玩家端（" +
                    (expected - sent) + " 个失败）→ 计数 profileBroadcastFailures");
            }
            InfoRateLimited("broadcast-ok:" + message.ClientId + ":" + message.Slot,
                "资料同步：已广播权威资料（玩家键 " + message.ClientId + ":" + message.Slot +
                " · 名字=" + message.Name + " · rev=" + message.Revision + " · 入队 " +
                sent + "/" + expected + "）");
        }

        private void OnClientLeft(int clientId, string reason)
        {
            if (!m_attached) return;
            m_hostBooks.RemoveClient(clientId);
            var remove = new List<PlayerKey>(4);
            foreach (KeyValuePair<PlayerKey, uint> pair in m_hostRevisions)
                if (pair.Key.ClientId == clientId) remove.Add(pair.Key);
            for (int i = 0; i < remove.Count; i++) m_hostRevisions.Remove(remove[i]);
            Info("资料同步：clientId=" + clientId + " 离开（" + (reason ?? string.Empty) +
                "）→ 清掉它的 sequence/revision 账本");
        }

        private uint NextHostRevision(PlayerKey key)
        {
            m_hostRevisions.TryGetValue(key, out uint revision);
            revision++;
            if (revision == 0) revision = 1;
            m_hostRevisions[key] = revision;
            return revision;
        }

        private uint NewSession()
        {
            m_sessionCounter++;
            uint value = (uint)(m_now() * 1000.0) ^ (m_sessionCounter * 2654435761u);
            return value == 0 ? 1u : value;
        }

        // ---------------------------------------------------------------- 日志

        private void LogSummaryIfDue(long nowMs)
        {
            if (!m_logs.ShouldLog("summary", nowMs, ProfileSyncProtocol.LogIntervalMs)) return;
            Info(StatusLine());
        }

        private void InfoRateLimited(string key, string message)
        {
            if (m_logs.ShouldLog("info:" + key, NowMs())) Info(message);
        }

        private void WarnRateLimited(string key, string message)
        {
            if (m_logs.ShouldLog("warn:" + key, NowMs())) Warn(message);
        }

        private void ErrorRateLimited(string key, string message)
        {
            if (m_logs.ShouldLog("error:" + key, NowMs())) Error(message);
        }

        private long NowMs() => (long)(m_now() * 1000.0);

        private void Info(string message) => m_context?.Diagnostics.Info(ModuleIdText, message);

        private void Warn(string message) => m_context?.Diagnostics.Warn(ModuleIdText, message);

        private void Error(string message) => m_context?.Diagnostics.Error(ModuleIdText, message, null);
    }
}

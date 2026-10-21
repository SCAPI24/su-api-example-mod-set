using System;
using System.Collections.Generic;
using System.Text;
using Google.Protobuf;
using SuMultiplayer.Players;
using Proto = SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 玩家资料运行时同步（PLAYER_PROFILE_SYNC = 108）的**唯一纯逻辑**：
    /// 编解码 / owner 校验 / 名字与皮肤校验 / sequence+revision 防旧包 / 变化检测与节流 / 计数。
    ///
    /// 为什么需要这条链（产品反馈 2026-10-20）：
    ///   · 引擎 Edit Players 屏把名字 / 职业 / 皮肤 / 衣服写进本机 <c>PlayerData</c>
    ///     （Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen ctor 的 FocusLost）；
    ///   · 旧的 PLAYER_PROFILE（27）+ PlayerEditWatcher 只在屏幕引用变化时动作，
    ///     没有经过这次屏幕切换、或 TextBox 的 FocusLost 来不及跑的变化不会送到主机；
    ///   · 主机没更新它的客户端化身，其他玩家看到的还是旧资料 —— 表现就是"改了没生效"。
    ///
    /// 本文件不引用 Game（自检直接编译同一份源码）；引擎名字规则的**唯一自写实现**在
    /// <see cref="PlayerEditSync.TryValidateName"/>（同属不引用 Game 的层，逐条锚定
    /// <c>PlayerData.VerifyName</c>），本文件只做转发、不再写第二份规则；
    /// 内置皮肤列表由 <see cref="IProfileSkinCatalog"/> 端口提供（实现方 = 联机角色模块）。
    ///
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:728-890
    ///         （SynchronizePlayerProfiles / HandlePlayerProfileMessage 的校验与广播口径）
    /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.VerifyName（2..14、字母/数字/空格、首尾非空格）
    /// </summary>
    public static class ProfileSyncProtocol
    {
        /// <summary>新协议 id：当前最大 107（selfdamage），append-only 取 108。</summary>
        public const int MessageId = 108;

        /// <summary>客户端检查本机资料的周期（用户口径 0.5..1 s；同一资料变化最多 0.75 s 内发出）。</summary>
        public const int CheckIntervalMs = 750;

        /// <summary>日志限流间隔（用户口径 10 s；计数不受影响）。</summary>
        public const int LogIntervalMs = 10000;
    }

    /// <summary>
    /// 主机 / 客户端共用的皮肤目录端口：只回答"这个内置皮肤存在吗、它属于哪个职业"。
    /// 自定义 <c>.scskin</c> 不走这里（由 <see cref="SkinAssetPolicy"/> 校验名称 / 哈希 / 职业提示）。
    /// </summary>
    public interface IProfileSkinCatalog
    {
        /// <summary>
        /// true = <paramref name="skinName"/> 在引擎 <c>CharacterSkinsManager.CharacterSkinsNames</c> 里；
        /// <paramref name="playerClass"/> 为 null 表示这个皮肤不限定职业（引擎 GetPlayerClass 返回 null）。
        /// </summary>
        bool TryGetBuiltInClass(string skinName, out PlayerSex? playerClass);
    }

    /// <summary>
    /// 资料同步计数（状态行 / 10 s 汇总日志）。计数每次真实累加，日志由
    /// <see cref="ProfileSyncLogLimiter"/> 单独限流，不静默。
    /// </summary>
    public sealed class ProfileSyncCounters
    {
        public long Sent;
        public long Recv;
        public long Applied;
        /// <summary>owner（clientId != source）/ 身份冲突 / 方向错误等"来源不可信"的拒绝数。</summary>
        public long RejectedBy;
        public long NameInvalid;
        public long SkinRejected;
        public long Stale;
        public long BroadcastFailures;
        public long SendFailed;
        public long ApplyFailed;
        public long Unchanged;
        public long ClothesRejected;
        public long DirectionErrors;
        public long Malformed;
        /// <summary>主机最终校验（TryValidateProfileForSync：引擎 VerifyName / 职业 / 槽位）拒绝数。</summary>
        public long ProfileRejected;
        /// <summary>客户端：750 ms 变化检测到**本机资料签名变化**的次数（含名字/皮肤/衣服/职业）。</summary>
        public long LocalChanges;
        /// <summary>客户端 / 主机：检测到**本机皮肤字段变化**（皮肤名或自定义哈希）的次数。</summary>
        public long LocalSkinChanges;
        /// <summary>把检测到的皮肤变化交给皮肤资源链（IProfileSkinSyncPort）的尝试次数。</summary>
        public long SkinSyncRequests;
        /// <summary>皮肤资源链明确失败（资源缺失 / 校验失败 / 通道不可用）的次数。</summary>
        public long SkinSyncFailures;
        /// <summary>主机：已应用的资料里名字确实变化的次数。</summary>
        public long AppliedNameChanges;
        /// <summary>主机：已应用的资料里皮肤（名 / 哈希 / 性别）确实变化的次数。</summary>
        public long AppliedSkinChanges;
        /// <summary>主机：已应用的资料里衣服确实变化的次数。</summary>
        public long AppliedClothesChanges;

        public string Describe()
        {
            var text = new StringBuilder(420);
            text.Append("profileSent=").Append(Sent)
                .Append(" profileRecv=").Append(Recv)
                .Append(" profileApplied=").Append(Applied)
                .Append(" profileRejectedBy=").Append(RejectedBy)
                .Append(" profileNameInvalid=").Append(NameInvalid)
                .Append(" profileSkinRejected=").Append(SkinRejected)
                .Append(" profileStale=").Append(Stale)
                .Append(" profileBroadcastFailures=").Append(BroadcastFailures)
                .Append(" profileSendFailed=").Append(SendFailed)
                .Append(" profileApplyFailed=").Append(ApplyFailed)
                .Append(" profileUnchanged=").Append(Unchanged)
                .Append(" profileClothesRejected=").Append(ClothesRejected)
                .Append(" profileDirectionErrors=").Append(DirectionErrors)
                .Append(" profileMalformed=").Append(Malformed)
                .Append(" profileProfileRejected=").Append(ProfileRejected)
                .Append(" profileLocalChanges=").Append(LocalChanges)
                .Append(" profileLocalSkinChanges=").Append(LocalSkinChanges)
                .Append(" profileSkinSyncRequests=").Append(SkinSyncRequests)
                .Append(" profileSkinSyncFailures=").Append(SkinSyncFailures)
                .Append(" profileAppliedNameChanges=").Append(AppliedNameChanges)
                .Append(" profileAppliedSkinChanges=").Append(AppliedSkinChanges)
                .Append(" profileAppliedClothesChanges=").Append(AppliedClothesChanges);
            return text.ToString();
        }
    }

    /// <summary>同一个 key 的日志 10 秒限流（计数不受影响；测试可直接用毫秒推进）。</summary>
    public sealed class ProfileSyncLogLimiter
    {
        private readonly Dictionary<string, long> m_lastMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public bool ShouldLog(string key, long nowMs, long intervalMs = ProfileSyncProtocol.LogIntervalMs)
        {
            string normalized = string.IsNullOrEmpty(key) ? "(default)" : key;
            if (m_lastMs.TryGetValue(normalized, out long last) &&
                nowMs - last < intervalMs)
                return false;
            m_lastMs[normalized] = nowMs;
            return true;
        }

        public void Reset() => m_lastMs.Clear();
    }

    /// <summary>
    /// 每个玩家键的 (session, sequence, revision) 账本：
    ///   · 新 session（重连）重置并接受；
    ///   · 旧 session 一律 stale；
    ///   · 同 session 内 report 要求 sequence 与 revision 都严格变大；
    ///   · 广播只要求 revision 严格变大（sequence 是发送端连接序号，广播不依赖它）。
    /// 非法 / 旧包只返回 false，不修改账本（绝不把旧包当成新值）。
    /// </summary>
    public sealed class ProfileSequenceBook
    {
        private sealed class State
        {
            public uint Session;
            public uint Sequence;
            public uint Revision;
            public bool Seen;
        }

        private readonly Dictionary<PlayerKey, State> m_states =
            new Dictionary<PlayerKey, State>();

        public void Clear() => m_states.Clear();

        public void RemoveClient(int clientId)
        {
            var remove = new List<PlayerKey>(4);
            foreach (KeyValuePair<PlayerKey, State> pair in m_states)
                if (pair.Key.ClientId == clientId) remove.Add(pair.Key);
            for (int i = 0; i < remove.Count; i++) m_states.Remove(remove[i]);
        }

        /// <summary>客户端 → 主机的资料上报：sequence + revision 都必须严格变大。</summary>
        public bool TryAcceptReport(PlayerKey key, uint session, uint sequence, uint revision,
            out bool stale, out string reason)
        {
            stale = false;
            reason = null;
            if (session == 0)
            {
                reason = "session = 0（发送端必须给出非 0 的会话号）";
                return false;
            }
            if (!m_states.TryGetValue(key, out State state) || !state.Seen)
            {
                m_states[key] = new State
                {
                    Session = session,
                    Sequence = sequence,
                    Revision = revision,
                    Seen = true
                };
                return true;
            }
            if (session < state.Session)
            {
                stale = true;
                reason = "旧 session（" + session + " < 已接受 " + state.Session + "）";
                return false;
            }
            if (session > state.Session)
            {
                state.Session = session;
                state.Sequence = sequence;
                state.Revision = revision;
                return true;
            }
            if (sequence <= state.Sequence)
            {
                stale = true;
                reason = "旧 sequence（" + sequence + " <= 已接受 " + state.Sequence + "）";
                return false;
            }
            if (revision <= state.Revision)
            {
                stale = true;
                reason = "旧 revision（" + revision + " <= 已接受 " + state.Revision + "）";
                return false;
            }
            state.Sequence = sequence;
            state.Revision = revision;
            return true;
        }

        /// <summary>主机 → 客户端的权威广播：只比较 revision（同 session 必须严格变大）。</summary>
        public bool TryAcceptBroadcast(PlayerKey key, uint session, uint revision,
            out bool stale, out string reason)
        {
            stale = false;
            reason = null;
            if (session == 0)
            {
                reason = "session = 0（主机必须给出非 0 的会话号）";
                return false;
            }
            if (!m_states.TryGetValue(key, out State state) || !state.Seen)
            {
                m_states[key] = new State
                {
                    Session = session,
                    Revision = revision,
                    Seen = true
                };
                return true;
            }
            if (session < state.Session)
            {
                stale = true;
                reason = "旧 session（" + session + " < 已接受 " + state.Session + "）";
                return false;
            }
            if (session > state.Session)
            {
                state.Session = session;
                state.Revision = revision;
                return true;
            }
            if (revision <= state.Revision)
            {
                stale = true;
                reason = "旧 revision（" + revision + " <= 已接受 " + state.Revision + "）";
                return false;
            }
            state.Revision = revision;
            return true;
        }
    }

    /// <summary>
    /// 客户端本机资料的变化检测 + 节流：
    ///   · 只有签名变化才返回 true（相同签名永不重复发）；
    ///   · 变化后同一槽位至少间隔 <see cref="MinIntervalMs"/> 才再次尝试；
    ///   · <c>force = true</c> 用于上线 / 重连后的首份完整资料（不受"签名相同"限制）。
    /// 发送失败只 <see cref="MarkAttempt"/>，不 <see cref="MarkSent"/> —— 下一轮会重试（显式失败，不假装成功）。
    /// </summary>
    public sealed class ProfileChangeDetector
    {
        public const long MinIntervalMs = ProfileSyncProtocol.CheckIntervalMs;

        private sealed class SlotState
        {
            public string Signature = string.Empty;
            public long LastAttemptMs = long.MinValue;
            public bool HasAttempt;
        }

        private readonly Dictionary<int, SlotState> m_slots = new Dictionary<int, SlotState>();

        public void Reset() => m_slots.Clear();

        public bool ShouldSend(int slot, string signature, long nowMs, bool force)
        {
            string normalized = signature ?? string.Empty;
            if (!m_slots.TryGetValue(slot, out SlotState state))
            {
                state = new SlotState();
                m_slots[slot] = state;
            }
            if (!force && string.Equals(state.Signature, normalized, StringComparison.Ordinal))
                return false;
            if (!force && state.HasAttempt && nowMs - state.LastAttemptMs < MinIntervalMs)
                return false;
            return true;
        }

        public void MarkAttempt(int slot, long nowMs)
        {
            if (!m_slots.TryGetValue(slot, out SlotState state))
            {
                state = new SlotState();
                m_slots[slot] = state;
            }
            state.LastAttemptMs = nowMs;
            state.HasAttempt = true;
        }

        /// <summary>发送成功：记住已发出的签名（之后相同签名不再发）。</summary>
        public void MarkSent(int slot, string signature)
        {
            if (!m_slots.TryGetValue(slot, out SlotState state))
            {
                state = new SlotState();
                m_slots[slot] = state;
            }
            state.Signature = signature ?? string.Empty;
        }

        /// <summary>旧路径（PLAYER_PROFILE 27 / 编辑屏）已经上报过同一签名：只记基线，不重复发。</summary>
        public void MarkReported(int slot, string signature) => MarkSent(slot, signature);

        public bool HasSignature(int slot, out string signature)
        {
            if (m_slots.TryGetValue(slot, out SlotState state))
            {
                signature = state.Signature;
                return true;
            }
            signature = null;
            return false;
        }
    }

    /// <summary>
    /// owner 规则（照旧实现）：主机只接受"载荷 client_id == 来源连接编号"的资料；
    /// 客户端只接受"来源 = 主机（context.ClientId = 0）"的广播。0 不是客户端的合法申报值
    /// （主机自己用 0 是广播方向，不是 client → host 方向）。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:763-800
    /// </summary>
    public static class ProfileOwnerRule
    {
        public static bool TryValidateClientReport(int sourceClientId, int claimedClientId,
            out string reason)
        {
            reason = null;
            if (sourceClientId <= 0)
            {
                reason = "来源连接编号 " + sourceClientId + " 非法（主机只接受玩家端上报）";
                return false;
            }
            if (claimedClientId != sourceClientId)
            {
                reason = "载荷 client_id=" + claimedClientId + " != 来源 " + sourceClientId +
                    "（不允许冒领别人的资料）";
                return false;
            }
            return true;
        }

        public static bool TryValidateBroadcast(int sourceClientId, int claimedClientId,
            out string reason)
        {
            reason = null;
            if (sourceClientId != 0)
            {
                reason = "广播来源 " + sourceClientId + " 不是主机（客户端只接受主机广播）";
                return false;
            }
            if (claimedClientId < 0)
            {
                reason = "载荷 client_id=" + claimedClientId + " 非法（主机 = 0，客户端 > 0）";
                return false;
            }
            return true;
        }
    }

    /// <summary>名字 / 职业 / 皮肤 / 衣服的最小校验（主机与客户端共用同一份）。</summary>
    public static class ProfileSyncValidation
    {
        /// <summary>
        /// 引擎名字规则的**唯一自写实现**在 <see cref="PlayerEditSync.TryValidateName"/>
        /// （规则锚定 Survivalcraft/Game/PlayerData.cs:353-371；不 Trim）。本方法只转发，
        /// 保证纯逻辑层 / 资料同步链只有一份规则。
        /// </summary>
        public static bool TryValidateName(string name, out string reason) =>
            PlayerEditSync.TryValidateName(name, out reason);

        public static bool TryValidatePlayerClass(uint value, out PlayerSex sex, out string reason)
        {
            sex = PlayerSex.Male;
            reason = null;
            if (value == (uint)PlayerSex.Male)
            {
                sex = PlayerSex.Male;
                return true;
            }
            if (value == (uint)PlayerSex.Female)
            {
                sex = PlayerSex.Female;
                return true;
            }
            reason = "player_class=" + value + " 非法（引擎只有 Male=0 / Female=1；不夹紧）";
            return false;
        }

        /// <summary>
        /// 皮肤合法性：
        ///   · 内置 <c>$...</c> 必须存在（<see cref="IProfileSkinCatalog"/> = CharacterSkinsNames），
        ///     并且名字携带的职业提示与 player_class 一致；
        ///   · 自定义 <c>.scskin</c> 必须有合法的 32 字节 SHA-256，名称里的职业提示与 player_class 一致；
        ///   · 空皮肤 / 非法名称一律拒绝（保留旧值，不换默认皮肤）。
        /// </summary>
        public static bool TryValidateSkin(string skinName, PlayerSex sex, byte[] sha256,
            IProfileSkinCatalog catalog, out string reason)
        {
            reason = null;
            string skin = (skinName ?? string.Empty).Trim();
            if (!SkinAssetPolicy.TryValidateSkinName(skin, allowBuiltIn: true, out reason))
                return false;
            if (SkinAssetPolicy.IsCustomSkinName(skin))
            {
                if (!SkinAssetPolicy.IsValidHash(sha256))
                {
                    reason = "自定义皮肤 \"" + skin + "\" 缺少合法的 32 字节 SHA-256";
                    return false;
                }
                if (SkinAssetPolicy.TryGetClassHint(skin, out PlayerSex hint) && hint != sex)
                {
                    reason = "自定义皮肤 \"" + skin + "\" 名称暗示性别 " + hint +
                        "，资料却是 " + sex + "（不静默改性别）";
                    return false;
                }
                return true;
            }
            if (catalog == null)
            {
                reason = "没有皮肤目录（无法校验内置皮肤 \"" + skin + "\"）";
                return false;
            }
            if (!catalog.TryGetBuiltInClass(skin, out PlayerSex? builtInClass))
            {
                reason = "内置皮肤 \"" + skin + "\" 不在 CharacterSkinsNames 里";
                return false;
            }
            if (builtInClass.HasValue && builtInClass.Value != sex)
            {
                reason = "内置皮肤 \"" + skin + "\" 属于 " + builtInClass.Value +
                    "，资料却是 " + sex + "（不静默改性别）";
                return false;
            }
            return true;
        }

        public static bool TryValidateClothes(int[][] clothes, out string reason) =>
            PlayerEditSync.TryValidateClothes(clothes, out reason);

        /// <summary>
        /// 读衣服：空列表 = "这次不上报衣服/不覆盖"，<paramref name="hasClothes"/> = false；
        /// 有列表时必须恰好 4 个槽位且每个槽位合法（多/少都拒绝，不猜、不夹紧）。
        /// </summary>
        public static bool TryReadClothes(Proto.PlayerProfileSync message, int[][] into,
            out bool hasClothes, out string reason)
        {
            hasClothes = message != null && message.Clothes.Count > 0;
            reason = null;
            if (!hasClothes) return true;
            if (message.Clothes.Count != PlayerAppearance.ClothingSlotCount)
            {
                reason = "衣服槽位 " + message.Clothes.Count + " != 引擎上限 " +
                    PlayerAppearance.ClothingSlotCount;
                return false;
            }
            for (int slot = 0; slot < PlayerAppearance.ClothingSlotCount; slot++)
            {
                Proto.ClothingSlot clothing = message.Clothes[slot];
                if (clothing == null)
                {
                    into[slot] = Array.Empty<int>();
                    continue;
                }
                int[] values = new int[clothing.Values.Count];
                for (int i = 0; i < values.Length; i++) values[i] = clothing.Values[i];
                into[slot] = values;
            }
            return TryValidateClothes(into, out reason);
        }
    }

    /// <summary>
    /// 本机资料变化检测的**字段级**比较（名字 / 皮肤 / 衣服分开）：
    ///   · 108 模块用它统计"检测到的本机资料变化"并只在皮肤真的变化时触发皮肤资源链；
    ///   · 主机应用资料后用它分类统计名字 / 皮肤 / 衣服变化（调不到具体分支时也能定位）。
    /// 皮肤键包含性别 / 皮肤名 / 自定义哈希 —— 三者任一变化都意味着化身外观需要重新应用。
    /// </summary>
    public static class ProfileSyncDiff
    {
        public static string SkinKey(PlayerProfileSnapshot profile)
        {
            PlayerAppearance appearance = profile?.Appearance;
            if (appearance == null) return string.Empty;
            return ((int)appearance.Sex).ToString() + "|" +
                (appearance.SkinName ?? string.Empty).Trim() + "|" +
                SkinAssetPolicy.HashHex(appearance.SkinSha256);
        }

        public static void Compare(PlayerProfileSnapshot before, PlayerProfileSnapshot after,
            out bool nameChanged, out bool skinChanged, out bool clothesChanged)
        {
            nameChanged = !string.Equals(before?.Name ?? string.Empty,
                after?.Name ?? string.Empty, StringComparison.Ordinal);
            skinChanged = !string.Equals(SkinKey(before), SkinKey(after), StringComparison.Ordinal);
            clothesChanged = !ClothesEqual(before?.Appearance?.Clothes, after?.Appearance?.Clothes);
        }

        private static bool ClothesEqual(int[][] left, int[][] right)
        {
            for (int slot = 0; slot < PlayerAppearance.ClothingSlotCount; slot++)
            {
                int[] leftSlot = left != null && slot < left.Length ? left[slot] : null;
                int[] rightSlot = right != null && slot < right.Length ? right[slot] : null;
                int leftLength = leftSlot?.Length ?? 0;
                int rightLength = rightSlot?.Length ?? 0;
                if (leftLength != rightLength) return false;
                for (int i = 0; i < leftLength; i++)
                    if (leftSlot[i] != rightSlot[i]) return false;
            }
            return true;
        }
    }

    /// <summary>PLAYER_PROFILE_SYNC 的线上值与 <see cref="PlayerProfileSnapshot"/> 的转换。</summary>
    public sealed class ProfileSyncPayload
    {
        public PlayerKey Key;
        public string Identity = string.Empty;
        public PlayerProfileSnapshot Profile;
        public uint Sequence;
        public uint Revision;
        public uint Session;
        public bool HasClothes;

        public string Describe() =>
            "玩家键 " + Key + " · 名字=" + (Profile?.Name ?? string.Empty) +
            " · 性别=" + (Profile?.Appearance?.Sex.ToString() ?? "?") +
            " · 皮肤=" + (Profile?.Appearance?.SkinName ?? string.Empty) +
            " · session=" + Session + " seq=" + Sequence + " rev=" + Revision +
            (HasClothes ? " · 带衣服" : " · 衣服不覆盖");
    }

    public static class PlayerProfileSyncCodec
    {
        /// <summary>把资料值编成 108 消息；4 个衣服槽位总是完整写出（空槽位 = 空 values）。</summary>
        public static Proto.PlayerProfileSync ToMessage(PlayerKey key, string identity,
            PlayerProfileSnapshot profile, uint sequence, uint revision, uint session)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            PlayerAppearance appearance = profile.Appearance ?? new PlayerAppearance();
            var message = new Proto.PlayerProfileSync
            {
                ClientId = key.ClientId,
                Slot = (uint)key.Slot,
                Identity = identity ?? string.Empty,
                Name = profile.Name ?? string.Empty,
                PlayerClass = (uint)appearance.Sex,
                SkinName = appearance.SkinName ?? string.Empty,
                SkinSha256 = ByteString.CopyFrom(appearance.SkinSha256 ?? Array.Empty<byte>()),
                Sequence = sequence,
                Revision = revision,
                Session = session
            };
            int[][] clothes = appearance.Clothes ?? PlayerAppearance.CreateEmptyClothes();
            for (int slot = 0; slot < PlayerAppearance.ClothingSlotCount; slot++)
            {
                var clothing = new Proto.ClothingSlot();
                int[] values = slot < clothes.Length ? clothes[slot] : null;
                if (values != null) clothing.Values.Add(values);
                message.Clothes.Add(clothing);
            }
            return message;
        }

        /// <summary>
        /// 解码 + 结构校验（玩家键 / 职业 / 衣服）。名字与皮肤的业务校验由调用方用
        /// <see cref="ProfileSyncValidation"/> 决定（先生成 reason 再决定是否应用；不部分应用）。
        /// </summary>
        public static bool TryDecode(Proto.PlayerProfileSync message, out ProfileSyncPayload payload,
            out string reason)
        {
            payload = null;
            reason = null;
            if (message == null)
            {
                reason = "资料同步消息为空";
                return false;
            }
            if (!PlayerKey.TryCreate(message.ClientId, (int)message.Slot, out PlayerKey key,
                    out string keyError))
            {
                reason = "玩家键非法：" + keyError;
                return false;
            }
            if (!ProfileSyncValidation.TryValidatePlayerClass(message.PlayerClass,
                    out PlayerSex sex, out reason))
                return false;
            int[][] clothes = PlayerAppearance.CreateEmptyClothes();
            if (!ProfileSyncValidation.TryReadClothes(message, clothes, out bool hasClothes,
                    out reason))
                return false;
            payload = new ProfileSyncPayload
            {
                Key = key,
                Identity = (message.Identity ?? string.Empty).Trim(),
                Sequence = message.Sequence,
                Revision = message.Revision,
                Session = message.Session,
                HasClothes = hasClothes,
                Profile = new PlayerProfileSnapshot
                {
                    // 名字原样解码（不 Trim）：合法性由调用方按原始字符串校验，
                    // 首尾空格必须被拒绝而不是被静默改写。
                    Name = message.Name ?? string.Empty,
                    Appearance = new PlayerAppearance
                    {
                        Sex = sex,
                        SkinName = (message.SkinName ?? string.Empty).Trim(),
                        SkinSha256 = message.SkinSha256 != null && message.SkinSha256.Length > 0
                            ? message.SkinSha256.ToByteArray()
                            : Array.Empty<byte>(),
                        Clothes = clothes
                    }
                }
            };
            return true;
        }
    }
}

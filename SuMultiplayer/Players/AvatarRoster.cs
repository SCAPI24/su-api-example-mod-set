using System;
using System.Collections.Generic;

namespace SuMultiplayer.Players
{
    /// <summary>客户端化身集合的一次变化。</summary>
    public enum AvatarRosterAction
    {
        Add = 0,
        Update = 1,
        Remove = 2
    }

    /// <summary>一次需要的引擎动作（建 / 更新 / 摘除远端化身）。</summary>
    public sealed class AvatarRosterChange
    {
        public AvatarRosterAction Action;
        /// <summary>玩家键（clientId + 本机槽位）。</summary>
        public PlayerKey Key;
        /// <summary>本机化身槽位键里的传输编号（主机 = 0）。旧字段，等价于 <c>Key.ClientId</c>。</summary>
        public int ClientId;
        /// <summary>该设备上的本机角色槽位（0..3）。旧构建的消息没有 slot = 0。</summary>
        public int Slot;
        public string Identity = string.Empty;
        public string Name = string.Empty;
        public PlayerAppearance Appearance;
        public bool IsHost;
        public string Reason = string.Empty;
    }

    /// <summary>roster 里的一条身份（客户端已知的远端玩家角色）。</summary>
    public sealed class AvatarRosterEntry
    {
        /// <summary>玩家键（clientId + 槽位）。</summary>
        public PlayerKey Key;
        /// <summary>传输编号（= <c>Key.ClientId</c>；旧字段）。</summary>
        public int ClientId;
        /// <summary>本机角色槽位（= <c>Key.Slot</c>；0 = 旧版单角色）。</summary>
        public int Slot;
        /// <summary>身份（UserId.dat）；为空时用 "client:&lt;id&gt;" 作为去重键。</summary>
        public string Identity = string.Empty;
        internal string IdentityKey = string.Empty;
        public string Name = string.Empty;
        public PlayerAppearance Appearance;
        public bool IsHost;
        /// <summary>是不是本机自己的资料（不建远端化身，应用到本机玩家）。</summary>
        public bool IsSelf;
        public string Signature = string.Empty;
    }

    /// <summary>
    /// 客户端**远端化身名册**：主机通过 PlayerProfile / AvatarRemove / AvatarRoster 告诉每个客户端
    /// "现在房间里有哪些人、各自长什么样、每个设备上有几个本机角色"。
    ///
    /// 这里只做纯逻辑：
    ///   · 按**玩家键 (clientId, slot)** 去重。同一个设备可以有最多 4 个本机角色，各有自己的
    ///     名字 / 性别 / 皮肤；同一个身份（UserId）+ 同一个槽位换了 clientId（断线重连）
    ///     → 先摘旧键再加新键，**绝不留两个化身**；
    ///   · 只有资料真的变了才产生 Update（避免每帧重建）；
    ///   · 本机自己的资料（<c>key.ClientId == LocalClientId</c>，任意槽位）只登记、不产生
    ///     Avatar 动作（调用方把它应用到本机玩家）。
    ///
    /// 引擎动作由联机角色模块按 <see cref="AvatarRosterChange"/> 执行，本类不引用 Game。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:840-890
    ///         （客户端把每个 clientId 的 profile 应用到 remotePlayer / 必要时重建）
    /// </summary>
    public sealed class AvatarRoster
    {
        private readonly Dictionary<PlayerKey, AvatarRosterEntry> m_byKey =
            new Dictionary<PlayerKey, AvatarRosterEntry>();
        private readonly Dictionary<string, PlayerKey> m_keyByIdentity =
            new Dictionary<string, PlayerKey>(StringComparer.Ordinal);

        /// <summary>本机在主机通道里的编号（HelloAck.ClientId）；未握手 = -1。</summary>
        public int LocalClientId { get; set; } = -1;

        public int Count => m_byKey.Count;

        public IReadOnlyList<AvatarRosterEntry> Entries
        {
            get
            {
                var list = new List<AvatarRosterEntry>(m_byKey.Values);
                list.Sort((left, right) => left.ClientId != right.ClientId
                    ? left.ClientId.CompareTo(right.ClientId)
                    : left.Slot.CompareTo(right.Slot));
                return list;
            }
        }

        public bool TryGet(PlayerKey key, out AvatarRosterEntry entry) =>
            m_byKey.TryGetValue(key, out entry);

        /// <summary>旧版单角色访问：等价于槽位 0。</summary>
        public bool TryGet(int clientId, out AvatarRosterEntry entry) =>
            m_byKey.TryGetValue(new PlayerKey(clientId, 0), out entry);

        public bool TryGetByIdentity(string identity, out AvatarRosterEntry entry) =>
            TryGetByIdentity(identity, 0, out entry);

        /// <summary>按 (身份, 槽位) 找条目；槽位 0 与旧版语义一致。</summary>
        public bool TryGetByIdentity(string identity, int slot, out AvatarRosterEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(identity)) return false;
            string identityKey = identity.Trim() + "#" + slot;
            return m_keyByIdentity.TryGetValue(identityKey, out PlayerKey key) &&
                m_byKey.TryGetValue(key, out entry);
        }

        public void Clear()
        {
            m_byKey.Clear();
            m_keyByIdentity.Clear();
        }

        /// <summary>旧版单角色入口：clientId 的槽位 0。</summary>
        public IReadOnlyList<AvatarRosterChange> Apply(int clientId, string identity, string name,
            PlayerAppearance appearance, bool isHost, string reason) =>
            Apply(new PlayerKey(clientId, 0), identity, name, appearance, isHost, reason);

        /// <summary>
        /// 应用一条身份资料。返回本次需要的引擎动作（可能为空 = 无变化）。
        /// 同一个 (身份, 槽位) 换了 clientId（断线重连）→ 先 Remove 旧键，再 Add 新键。
        /// </summary>
        public IReadOnlyList<AvatarRosterChange> Apply(PlayerKey key, string identity, string name,
            PlayerAppearance appearance, bool isHost, string reason)
        {
            var changes = new List<AvatarRosterChange>(2);
            string identityKey = key.IdentityKey(identity);
            string signature = (name ?? string.Empty) + "\n" + (appearance?.Signature ?? string.Empty);

            // 同一个 (身份, 槽位) 换了连接：旧键必须先摘掉（否则重连后世界里会多出一个复制体）。
            if (m_keyByIdentity.TryGetValue(identityKey, out PlayerKey previousKey) &&
                previousKey != key)
            {
                RemoveCore(previousKey, "同一身份+槽位换了连接（重连）：先摘掉旧键", changes);
            }
            // 同一个键换了身份（防御性；正常不会发生）。
            if (m_byKey.TryGetValue(key, out AvatarRosterEntry existingSlot) &&
                !string.Equals(existingSlot.IdentityKey, identityKey, StringComparison.Ordinal))
            {
                RemoveCore(key, "槽位被另一个身份复用：先摘掉旧的身份", changes);
            }

            AvatarRosterEntry entry;
            bool isNew;
            if (m_byKey.TryGetValue(key, out entry))
            {
                isNew = false;
                bool changed = !string.Equals(entry.Signature, signature, StringComparison.Ordinal);
                entry.Identity = identityKey.StartsWith("client:", StringComparison.Ordinal)
                    ? string.Empty
                    : (identity ?? string.Empty).Trim();
                entry.Name = name ?? string.Empty;
                entry.Appearance = appearance?.Clone();
                entry.IsHost = isHost;
                entry.IsSelf = key.ClientId == LocalClientId;
                entry.Signature = signature;
                if (!changed) return changes;
            }
            else
            {
                isNew = true;
                entry = new AvatarRosterEntry
                {
                    Key = key,
                    ClientId = key.ClientId,
                    Slot = key.Slot,
                    Identity = identityKey.StartsWith("client:", StringComparison.Ordinal)
                        ? string.Empty
                        : (identity ?? string.Empty).Trim(),
                    IdentityKey = identityKey,
                    Name = name ?? string.Empty,
                    Appearance = appearance?.Clone(),
                    IsHost = isHost,
                    IsSelf = key.ClientId == LocalClientId,
                    Signature = signature
                };
                m_byKey[key] = entry;
                m_keyByIdentity[identityKey] = key;
            }

            // 自己的资料：只登记（调用方应用到本机玩家），不建远端化身。
            if (entry.IsSelf) return changes;
            changes.Add(new AvatarRosterChange
            {
                Action = isNew ? AvatarRosterAction.Add : AvatarRosterAction.Update,
                Key = key,
                ClientId = key.ClientId,
                Slot = key.Slot,
                Identity = entry.Identity,
                Name = entry.Name,
                Appearance = entry.Appearance,
                IsHost = entry.IsHost,
                Reason = reason ?? string.Empty
            });
            return changes;
        }

        /// <summary>按玩家键摘掉一条身份（主机广播 AvatarRemove / 本地判定离开）。</summary>
        public IReadOnlyList<AvatarRosterChange> Remove(PlayerKey key, string reason)
        {
            var changes = new List<AvatarRosterChange>(1);
            RemoveCore(key, reason, changes);
            return changes;
        }

        /// <summary>旧版单角色入口：摘掉 clientId 的槽位 0。</summary>
        public IReadOnlyList<AvatarRosterChange> Remove(int clientId, string reason) =>
            Remove(new PlayerKey(clientId, 0), reason);

        /// <summary>
        /// 摘掉一个连接上的**所有槽位**（客户端离开 / 链路失效）。
        /// 旧版每个 clientId 只有一个角色，等价于 Remove(clientId)。
        /// </summary>
        public IReadOnlyList<AvatarRosterChange> RemoveClient(int clientId, string reason)
        {
            var changes = new List<AvatarRosterChange>(PlayerKey.SlotCount);
            for (int slot = 0; slot <= PlayerKey.MaxSlot; slot++)
                RemoveCore(new PlayerKey(clientId, slot), reason, changes);
            return changes;
        }

        /// <summary>按身份摘掉一条身份（重连 / 身份冲突时用；槽位 0，与旧版一致）。</summary>
        public IReadOnlyList<AvatarRosterChange> RemoveByIdentity(string identity, string reason)
        {
            var changes = new List<AvatarRosterChange>(1);
            if (string.IsNullOrWhiteSpace(identity)) return changes;
            if (m_keyByIdentity.TryGetValue(identity.Trim() + "#0", out PlayerKey key))
                RemoveCore(key, reason, changes);
            return changes;
        }

        private void RemoveCore(PlayerKey key, string reason, List<AvatarRosterChange> changes)
        {
            if (!m_byKey.TryGetValue(key, out AvatarRosterEntry entry)) return;
            m_byKey.Remove(key);
            if (m_keyByIdentity.TryGetValue(entry.IdentityKey, out PlayerKey mapped) && mapped == key)
                m_keyByIdentity.Remove(entry.IdentityKey);
            if (entry.IsSelf) return;
            changes.Add(new AvatarRosterChange
            {
                Action = AvatarRosterAction.Remove,
                Key = key,
                ClientId = key.ClientId,
                Slot = key.Slot,
                Identity = entry.Identity,
                Name = entry.Name,
                Appearance = entry.Appearance,
                IsHost = entry.IsHost,
                Reason = reason ?? string.Empty
            });
        }
    }
}

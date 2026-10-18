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
        /// <summary>本机化身槽位键 == 主机侧传输编号（主机 = 0）。</summary>
        public int ClientId;
        public string Identity = string.Empty;
        public string Name = string.Empty;
        public PlayerAppearance Appearance;
        public bool IsHost;
        public string Reason = string.Empty;
    }

    /// <summary>roster 里的一条身份（客户端已知的远端玩家）。</summary>
    public sealed class AvatarRosterEntry
    {
        public int ClientId;
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
    /// 客户端**远端化身名册**：主机通过 PlayerProfile / AvatarRemove 告诉每个客户端
    /// "现在房间里有哪些人、各自长什么样"。这里只做纯逻辑：
    ///   · 按身份去重（重连换了 clientId 时先摘旧槽位再加新槽位，**绝不留两个化身**）；
    ///   · 只有资料真的变了才产生 Update（避免每帧重建）；
    ///   · 本机自己的资料只登记、不产生 Avatar 动作（调用方把它应用到本机玩家）。
    ///
    /// 引擎动作由联机角色模块按 <see cref="AvatarRosterChange"/> 执行，本类不引用 Game。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:840-890
    ///         （客户端把每个 clientId 的 profile 应用到 remotePlayer / 必要时重建）
    /// </summary>
    public sealed class AvatarRoster
    {
        private readonly Dictionary<int, AvatarRosterEntry> m_byClientId =
            new Dictionary<int, AvatarRosterEntry>();
        private readonly Dictionary<string, int> m_clientIdByIdentity =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>本机在主机通道里的编号（HelloAck.ClientId）；未握手 = -1。</summary>
        public int LocalClientId { get; set; } = -1;

        public int Count => m_byClientId.Count;

        public IReadOnlyList<AvatarRosterEntry> Entries
        {
            get
            {
                var list = new List<AvatarRosterEntry>(m_byClientId.Values);
                list.Sort((left, right) => left.ClientId.CompareTo(right.ClientId));
                return list;
            }
        }

        public bool TryGet(int clientId, out AvatarRosterEntry entry) =>
            m_byClientId.TryGetValue(clientId, out entry);

        public bool TryGetByIdentity(string identity, out AvatarRosterEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(identity)) return false;
            return m_clientIdByIdentity.TryGetValue(identity.Trim(), out int clientId) &&
                m_byClientId.TryGetValue(clientId, out entry);
        }

        public void Clear()
        {
            m_byClientId.Clear();
            m_clientIdByIdentity.Clear();
        }

        /// <summary>
        /// 应用一条身份资料。返回本次需要的引擎动作（可能为空 = 无变化）。
        /// 同一个身份换了 clientId（断线重连）→ 先 Remove 旧槽位，再 Add 新槽位。
        /// </summary>
        public IReadOnlyList<AvatarRosterChange> Apply(int clientId, string identity, string name,
            PlayerAppearance appearance, bool isHost, string reason)
        {
            var changes = new List<AvatarRosterChange>(2);
            string identityKey = string.IsNullOrWhiteSpace(identity)
                ? "client:" + clientId
                : identity.Trim();
            string signature = (name ?? string.Empty) + "\n" + (appearance?.Signature ?? string.Empty);

            // 同一身份换了连接：旧槽位必须先摘掉（否则重连后世界里会多出一个复制体）。
            if (m_clientIdByIdentity.TryGetValue(identityKey, out int previousClientId) &&
                previousClientId != clientId)
            {
                RemoveCore(previousClientId, "同一身份换了连接（重连）：先摘掉旧槽位", changes);
            }
            // 同一槽位换了身份（防御性；正常不会发生）。
            if (m_byClientId.TryGetValue(clientId, out AvatarRosterEntry existingSlot) &&
                !string.Equals(existingSlot.IdentityKey, identityKey, StringComparison.Ordinal))
            {
                RemoveCore(clientId, "槽位被另一个身份复用：先摘掉旧的身份", changes);
            }

            AvatarRosterEntry entry;
            bool isNew;
            if (m_byClientId.TryGetValue(clientId, out entry))
            {
                isNew = false;
                bool changed = !string.Equals(entry.Signature, signature, StringComparison.Ordinal);
                entry.Identity = identityKey.StartsWith("client:", StringComparison.Ordinal)
                    ? string.Empty
                    : identityKey;
                entry.Name = name ?? string.Empty;
                entry.Appearance = appearance?.Clone();
                entry.IsHost = isHost;
                entry.IsSelf = clientId == LocalClientId;
                entry.Signature = signature;
                if (!changed) return changes;
            }
            else
            {
                isNew = true;
                entry = new AvatarRosterEntry
                {
                    ClientId = clientId,
                    Identity = identityKey.StartsWith("client:", StringComparison.Ordinal)
                        ? string.Empty
                        : identityKey,
                    IdentityKey = identityKey,
                    Name = name ?? string.Empty,
                    Appearance = appearance?.Clone(),
                    IsHost = isHost,
                    IsSelf = clientId == LocalClientId,
                    Signature = signature
                };
                m_byClientId[clientId] = entry;
                m_clientIdByIdentity[identityKey] = clientId;
            }

            // 自己的资料：只登记（调用方应用到本机玩家），不建远端化身。
            if (entry.IsSelf) return changes;
            changes.Add(new AvatarRosterChange
            {
                Action = isNew ? AvatarRosterAction.Add : AvatarRosterAction.Update,
                ClientId = clientId,
                Identity = entry.Identity,
                Name = entry.Name,
                Appearance = entry.Appearance,
                IsHost = entry.IsHost,
                Reason = reason ?? string.Empty
            });
            return changes;
        }

        /// <summary>按传输编号摘掉一条身份（主机广播 AvatarRemove / 本地判定离开）。</summary>
        public IReadOnlyList<AvatarRosterChange> Remove(int clientId, string reason)
        {
            var changes = new List<AvatarRosterChange>(1);
            RemoveCore(clientId, reason, changes);
            return changes;
        }

        /// <summary>按身份摘掉一条身份（重连 / 身份冲突时用；找不到返回空）。</summary>
        public IReadOnlyList<AvatarRosterChange> RemoveByIdentity(string identity, string reason)
        {
            var changes = new List<AvatarRosterChange>(1);
            if (string.IsNullOrWhiteSpace(identity)) return changes;
            if (m_clientIdByIdentity.TryGetValue(identity.Trim(), out int clientId))
                RemoveCore(clientId, reason, changes);
            return changes;
        }

        private void RemoveCore(int clientId, string reason, List<AvatarRosterChange> changes)
        {
            if (!m_byClientId.TryGetValue(clientId, out AvatarRosterEntry entry)) return;
            m_byClientId.Remove(clientId);
            if (m_clientIdByIdentity.TryGetValue(entry.IdentityKey, out int mapped) && mapped == clientId)
                m_clientIdByIdentity.Remove(entry.IdentityKey);
            if (entry.IsSelf) return;
            changes.Add(new AvatarRosterChange
            {
                Action = AvatarRosterAction.Remove,
                ClientId = clientId,
                Identity = entry.Identity,
                Name = entry.Name,
                Appearance = entry.Appearance,
                IsHost = entry.IsHost,
                Reason = reason ?? string.Empty
            });
        }
    }
}

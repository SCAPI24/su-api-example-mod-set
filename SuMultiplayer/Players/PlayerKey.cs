using System;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 一个联机角色的**稳定玩家键**：<c>(clientId, slot)</c>。
    ///
    /// 为什么需要它（Phase 1 的核心）：
    ///   · 旧实现把"玩家"等同于"连接"：一个 clientId 只能有一个角色。引擎本身支持
    ///     最多 4 个本机角色（<c>SubsystemPlayers.MaxPlayers = 4</c> / <c>SubsystemGameWidgets.MaxGameWidgets = 4</c>），
    ///     分屏世界里一个设备会有多个本机角色，只用 clientId 表达不了第二个角色。
    ///   · 本键的第一段仍是**本机传输编号**（主机 = 0），第二段是该设备上本机角色的槽位 0..3
    ///     （主机自己的本机角色 = clientId 0 的槽位 0..3；一个客户端的本机角色 = 它的 clientId 的槽位 0..3）。
    ///
    /// 序列化 / 兼容规则（append-only）：
    ///   · 线上消息继续用已有的 <c>client_id</c> 字段，另加**追加字段** <c>slot</c>；
    ///     旧构建没有 slot 字段 → 解码为 0（单角色兼容，线上行为与旧版逐字节一致）。
    ///   · 槽位 0 的角色记录键与旧版完全一致（<see cref="PlayerIdentity.RecordKey"/>）；
    ///     槽位 1..3 追加 <c>#&lt;slot&gt;</c>，旧记录文件无需迁移。
    ///   · 本类型不持有任何静态可变状态，只是值。
    ///
    /// 身份（UserId.dat）仍然只由 <see cref="PlayerIdentity"/> 负责；本键只是**本机索引**，
    /// 绝不能当身份用（同一设备上的多个本机角色共享同一个 UserId，靠 slot 区分）。
    /// </summary>
    public readonly struct PlayerKey : IEquatable<PlayerKey>
    {
        /// <summary>一个设备最多 4 个本机角色（引擎硬上限）。</summary>
        public const int SlotCount = 4;

        /// <summary>合法槽位范围 0..3。</summary>
        public const int MaxSlot = SlotCount - 1;

        /// <summary>主机自己的角色键（clientId=0 的槽位 0）—— 旧实现里它就是 0。</summary>
        public static readonly PlayerKey Host = new PlayerKey(0, 0);

        public PlayerKey(int clientId, int slot)
        {
            ClientId = clientId;
            Slot = slot;
        }

        /// <summary>本机传输编号（主机 = 0，客户端 = 主机在 HelloAck 里分配的编号）。</summary>
        public int ClientId { get; }

        /// <summary>该设备上的本机角色槽位（0..3）。</summary>
        public int Slot { get; }

        /// <summary>客户端编号合法（非负）且槽位在 0..3。</summary>
        public bool IsValid => ClientId >= 0 && Slot >= 0 && Slot <= MaxSlot;

        /// <summary>是不是主机自己的角色（任意槽位；主机 = clientId 0）。</summary>
        public bool IsHost => ClientId == 0;

        /// <summary>
        /// 显式构造：非法键返回 false 并给出可直接定位的原因（不静默夹紧/不猜槽位）。
        /// </summary>
        public static bool TryCreate(int clientId, int slot, out PlayerKey key, out string error)
        {
            key = new PlayerKey(clientId, slot);
            error = null;
            if (clientId < 0)
            {
                error = "clientId=" + clientId + " 非法（必须 >= 0；主机 = 0）";
                return false;
            }
            if (slot < 0 || slot > MaxSlot)
            {
                error = "槽位 " + slot + " 非法（合法范围 0.." + MaxSlot + "）";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 槽位键的身份去重键：同一个设备（身份）的同一个槽位才算同一个角色。
        /// 身份为空时退到 <c>client:&lt;id&gt;</c>（身份不可用的退化路径，日志会写明原因）。
        /// </summary>
        public string IdentityKey(string identity) =>
            (string.IsNullOrWhiteSpace(identity)
                ? "client:" + ClientId
                : identity.Trim()) + "#" + Slot;

        /// <summary>日志 / 状态行口径：<c>clientId:slot</c>（旧的单角色键就是 <c>clientId:0</c>）。</summary>
        public override string ToString() => ClientId + ":" + Slot;

        public bool Equals(PlayerKey other) => ClientId == other.ClientId && Slot == other.Slot;

        public override bool Equals(object obj) => obj is PlayerKey other && Equals(other);

        public override int GetHashCode() => (ClientId * 397) ^ Slot;

        public static bool operator ==(PlayerKey left, PlayerKey right) => left.Equals(right);

        public static bool operator !=(PlayerKey left, PlayerKey right) => !left.Equals(right);
    }
}

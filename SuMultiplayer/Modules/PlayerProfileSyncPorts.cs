using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>一次资料同步应用的结果（主机 / 客户端共用；不把"没变化"当成"失败"）。</summary>
    public enum ProfileSyncApplyOutcome
    {
        /// <summary>校验 / 身份 / 记录写盘失败：旧资料保持不变，调用方必须计数并写原因。</summary>
        Rejected = 0,
        /// <summary>资料与当前记录 / 名册一致：不广播、不重复应用。</summary>
        Unchanged = 1,
        /// <summary>已写入记录 / 化身：调用方需要广播权威资料。</summary>
        Applied = 2
    }

    /// <summary>
    /// 资料同步（PLAYER_PROFILE_SYNC = 108）的**权威端动作**，实现方 = <see cref="PlayerAvatarModule"/>：
    /// 它拥有角色记录（SuMultiplayerPlayers.xml）、联机化身与客户端名册。
    ///
    /// 为什么用端口而不是让新模块直接改引擎：
    ///   · 新模块只做协议 / 校验 / 排序 / 计数（不引用 Game，自检可编译同一份源码）；
    ///   · "写 PlayerData / 摘重建化身 / 写角色记录 / 更新名册" 全部复用已存在的联机角色模块，
    ///     不复制第二套应用逻辑（避免 27 与 108 两条路结果不一致）。
    /// </summary>
    public interface IPlayerProfileAuthorityPort
    {
        /// <summary>客户端：本机资料已经应用成自己的资料（下载世界的主机角色不能当自己上报）。</summary>
        bool IsSelfProfileReady { get; }

        /// <summary>本机身份（UserId.dat）；为空 = 身份不可用（上报时明确记录，不猜）。</summary>
        string LocalIdentity { get; }

        /// <summary>本机角色槽位数（0..4；没有世界 / 没有本机玩家时返回 0 并给出原因）。</summary>
        int LocalPlayerSlotCount { get; }

        /// <summary>抓本机槽位的真实资料（名字 / 职业 / 皮肤 / 衣服）。</summary>
        bool TryCaptureLocalProfile(int slot, out PlayerProfileSnapshot profile, out string reason);

        /// <summary>
        /// 本机槽位是否已经通过 LOCAL_PLAYER_SLOTS 公告给主机（槽位 &gt; 0 的先决条件；
        /// 主机对未公告槽位的资料会明确拒绝，客户端不能假装发成功）。
        /// </summary>
        bool WasLocalSlotAnnounced(int slot);

        /// <summary>
        /// 主机最终校验（名字 = PlayerData.VerifyName；内置皮肤 = CharacterSkinsNames；
        /// 自定义皮肤哈希 / 职业；衣服边界）。返回 false + reason 时保留旧资料。
        /// </summary>
        bool TryValidateProfileForSync(PlayerProfileSnapshot profile, int slot, out string reason);

        /// <summary>已知的权威资料（主机端 = 已登记的对端资料；客户端 = 名册 / 化身）。</summary>
        bool TryGetKnownProfile(PlayerKey key, out PlayerProfileSnapshot profile);

        /// <summary>主机侧已从 Hello 得到的身份（UserId.dat）；没有时返回 false。</summary>
        bool TryGetPeerIdentity(int clientId, out string identity);

        /// <summary>
        /// 主机：应用一条已通过校验的客户端资料（写记录 / 更新或重建化身），**不广播**。
        /// 返回 Applied 时由调用方广播 108；Unchanged 时不广播。
        /// </summary>
        ProfileSyncApplyOutcome ApplyProfileSyncOnHost(int sourceClientId, PlayerKey key,
            string identity, PlayerProfileSnapshot profile, out string reason);

        /// <summary>
        /// 客户端：应用主机广播的权威资料（本机 → 本机 PlayerData；主机 / 其他客户端 → 名册 + 化身）。
        /// </summary>
        ProfileSyncApplyOutcome ApplyProfileSyncBroadcast(PlayerKey key, string identity,
            PlayerProfileSnapshot profile, bool isHost, out string reason);
    }

    /// <summary>
    /// 资料同步检测到**本机皮肤变化**后交给皮肤资源链（实现方 = PlayerSkinModule）：
    ///   · 内置皮肤 → 只需本机应用（皮肤名由 108 资料消息携带，主机/其他端按内置皮肤 id 应用）；
    ///   · 自定义 .scskin → 走既有的分片上传 + 主机校验 + 转发链（不复制第二套上传逻辑）。
    /// 返回 false + reason = 明确失败（资源缺失 / 校验失败 / 通道不可用），调用方必须计数并限流写原因。
    /// </summary>
    public interface IProfileSkinSyncPort
    {
        /// <summary>
        /// 把一次检测到的本机皮肤变化交给皮肤模块；<paramref name="isHost"/> 必须与本端真实角色一致
        /// （主机 = 写记录并广播给所有客户端；客户端 = 上传 / 请求主机确认）。
        /// </summary>
        bool TrySyncDetectedSkin(PlayerProfileSnapshot profile, int slot, bool isHost,
            out string reason);
    }
}

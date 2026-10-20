using System;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>资源来源：只影响日志与"是否允许覆盖"，不改变内存缓存语义。</summary>
    public enum SkinAssetOrigin
    {
        /// <summary>本机自己拥有的资源（本机皮肤库里的文件，只读；或角色创建屏交来的字节）。</summary>
        Owned = 0,
        /// <summary>主机世界作用域角色记录里的资源（重连时不需要客户端再上传）。</summary>
        HostRecord = 1,
        /// <summary>从主机广播收到的副本（客户端只放内存，绝不落全局皮肤库）。</summary>
        Replica = 2
    }

    /// <summary>
    /// 引擎侧"自定义皮肤资源 / 贴图"端口（实现方 = Engine/PlayerSkinStore.cs，组合根注入）。
    ///
    /// 模块层（Modules/）保持不依赖 Game：
    ///   · 校验 / 分片 / 计数 / 协议在 Modules/ 与 Players/（自检直接编译）；
    ///   · 读本机皮肤文件、解码 PNG、把贴图注入 ComponentClothing、把资源写进角色记录
    ///     都是引擎动作，只在这个端口后面。
    ///
    /// **只读**用户文件：本端口没有任何写 data:/CharacterSkins 的方法；客户端收到的资源
    /// 只在内存里（replica-scoped cache）。
    /// </summary>
    public interface IPlayerSkinPort
    {
        /// <summary>
        /// 读本机皮肤库里的自定义皮肤文件（只读，大小有界）。false + reason = 文件不存在 /
        /// 过大 / 非法；调用方据此明确失败，绝不换成别的皮肤。
        /// </summary>
        bool TryReadLocalSkinAsset(string skinName, out byte[] data, out string reason);

        /// <summary>引擎解码校验（Image.Load + 尺寸）。false + reason = 不能作为皮肤贴图。</summary>
        bool TryDecodeAssetData(byte[] data, out int width, out int height, out string reason);

        /// <summary>
        /// 放进本次会话的内存缓存（按哈希去重）。duplicate=true 表示同一哈希、同一字节已存在。
        /// 同一哈希但字节不同 → false + reason（哈希冲突，绝不覆盖）。
        /// </summary>
        bool StoreAsset(PlayerSkinAssetRecord record, SkinAssetOrigin origin, out bool duplicate,
            out string reason);

        /// <summary>按哈希（优先）或皮肤名取缓存里的资源。</summary>
        bool TryGetCachedAsset(byte[] sha256, string skinName, out PlayerSkinAssetRecord record);

        /// <summary>皮肤名 → 当前缓存里的哈希（抓资料 / 广播签名用）。</summary>
        bool TryGetHashForSkin(string skinName, out byte[] sha256);

        /// <summary>
        /// 把资源（或确定性占位贴图）应用到某个远端化身。false + reason = 没有这个化身 /
        /// 组件缺失；placeholder=true 表示资源缺失、已显示占位并计数。
        /// </summary>
        bool TryApplyAvatarSkin(int clientId, PlayerSkinAssetRecord record, bool allowPlaceholder,
            out bool placeholder, out string reason);

        /// <summary>
        /// 按玩家键 <c>(clientId, slot)</c> 应用（Phase 1 多本机角色）。默认实现退到槽位 0，
        /// 旧实现 / 自检替身无需改动；PlayerSkinStore 会覆盖成真正按槽位找化身。
        /// </summary>
        bool TryApplyAvatarSkin(PlayerKey key, PlayerSkinAssetRecord record, bool allowPlaceholder,
            out bool placeholder, out string reason) =>
            TryApplyAvatarSkin(key.ClientId, record, allowPlaceholder, out placeholder, out reason);

        /// <summary>把资源（或占位）应用到本机玩家（槽位 0，旧口径）。</summary>
        bool TryApplyLocalSkin(PlayerSkinAssetRecord record, bool allowPlaceholder,
            out bool placeholder, out string reason);

        /// <summary>
        /// 把资源（或占位）应用到本机指定槽位的角色（Phase 1 多本机角色）。
        /// 默认实现退到槽位 0；PlayerSkinStore 覆盖成真正按槽位找本机角色。
        /// </summary>
        bool TryApplyLocalSkin(int slot, PlayerSkinAssetRecord record, bool allowPlaceholder,
            out bool placeholder, out string reason) =>
            TryApplyLocalSkin(record, allowPlaceholder, out placeholder, out reason);

        /// <summary>化身被移除：丢掉本端为它保存的贴图引用（ComponentClothing 自己释放贴图）。</summary>
        void ForgetAvatar(int clientId);

        /// <summary>换世界 / 模块下线：丢掉全部贴图引用与已应用记录（资源缓存本身由模块清）。</summary>
        void ForgetAll();

        /// <summary>状态行：缓存资源数 / 已应用化身数 / 占位次数（计数器在模块里）。</summary>
        string Describe();
    }

    /// <summary>
    /// 主机侧"角色记录 + 本机资料"端口（实现方 = Modules/PlayerAvatarModule.cs）。
    ///
    /// 自定义皮肤资源必须由**主机**写进这个世界的作用域（&lt;世界目录&gt;/SuMultiplayerPlayers.xml），
    /// 而不是客户端的全局皮肤库。模块只通过这个端口读写记录，不直接引用记录表。
    /// </summary>
    public interface IPlayerSkinRecordPort
    {
        bool IsHosting { get; }

        /// <summary>按身份键取该玩家的角色记录里保存的皮肤（名称 / 性别 / 哈希 / 资源）。</summary>
        bool TryGetRecordSkin(string identity, string name, out PlayerSkinAssetRecord record,
            out string reason);

        /// <summary>按 (身份, 槽位) 取角色记录里的皮肤（Phase 1 多本机角色）。</summary>
        bool TryGetRecordSkin(string identity, string name, int slot,
            out PlayerSkinAssetRecord record, out string reason) =>
            TryGetRecordSkin(identity, name, out record, out reason);

        /// <summary>
        /// 主机：把校验通过的皮肤写进该身份的角色记录并落盘（世界作用域），然后更新
        /// 资料广播（其他客户端据此看到新哈希）。false + reason = 没有记录 / 写盘失败。
        /// </summary>
        bool StoreRecordSkin(string identity, string name, int clientId,
            PlayerSkinAssetRecord record, out string reason);

        /// <summary>按 (身份, 槽位) 写角色记录里的皮肤（Phase 1 多本机角色）。</summary>
        bool StoreRecordSkin(string identity, string name, int clientId,
            PlayerSkinAssetRecord record, int slot, out string reason) =>
            StoreRecordSkin(identity, name, clientId, record, out reason);

        /// <summary>抓本机角色的真实资料（名字 / 性别 / 皮肤 / 衣服）。</summary>
        bool TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out string reason);

        /// <summary>抓本机指定槽位的真实资料（Phase 1 多本机角色）。</summary>
        bool TryCaptureLocalProfile(int slot, out PlayerProfileSnapshot profile, out string reason) =>
            TryCaptureLocalProfile(out profile, out reason);

        /// <summary>本机身份（UserId.dat）；空 + reason = 拿不到。</summary>
        bool TryGetLocalIdentity(out string identity, out string reason);

        /// <summary>内置皮肤是否在本机皮肤列表里（中途换内置皮肤时的主机校验）。</summary>
        bool IsKnownBuiltInSkin(string skinName, out string reason);

        /// <summary>主机：资料广播用——把这条身份的皮肤哈希/名称同步进名册并广播。</summary>
        void UpdateSkinAppearance(int clientId, string identity, string name,
            PlayerSkinAssetRecord record);

        /// <summary>主机：按 (身份, 槽位) 同步皮肤哈希/名称到名册并广播（Phase 1 多本机角色）。</summary>
        void UpdateSkinAppearance(int clientId, string identity, string name,
            PlayerSkinAssetRecord record, int slot) =>
            UpdateSkinAppearance(clientId, identity, name, record);
    }
}

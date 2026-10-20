using System;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 一次"选择 / 创建皮肤"的纯值结果：角色创建屏（首次加入）与中途换肤屏共用。
    ///
    /// 自定义皮肤把**原始资产字节**一起带出来（屏幕从本机皮肤库只读取得并校验过），
    /// 这样模块可以立刻算哈希、放进本机缓存并随首次握手 / 换肤请求发送；
    /// 内置皮肤 <see cref="SkinAsset"/> 为空。
    ///
    /// 不引用 Game：屏幕负责把 PlayerClass 转成 <see cref="PlayerSex"/>。
    /// </summary>
    public sealed class PlayerSkinChangeRequest
    {
        /// <summary>角色名（中途换肤时为空 = 保持当前名字）。</summary>
        public string Name = string.Empty;

        /// <summary>性别（引擎 PlayerClass 的数值口径：0=男 / 1=女）。</summary>
        public PlayerSex Sex = PlayerSex.Male;

        /// <summary>引擎皮肤名（内置 "$Male3" 或自定义 "Foo.scskin"）。</summary>
        public string SkinName = string.Empty;

        /// <summary>自定义皮肤的原始 `.scskin` 字节；内置皮肤为空。</summary>
        public byte[] SkinAsset = Array.Empty<byte>();

        /// <summary>
        /// 本机角色槽位 0..3（Phase 1 多本机角色）。旧调用方默认 0，行为与单角色完全一致。
        /// 编辑玩家屏退出时按实际编辑的槽位填。
        /// </summary>
        public int Slot;

        /// <summary>来源（日志用：character-screen / edit-player-screen）。</summary>
        public string Source = string.Empty;

        public bool HasCustomAsset => SkinAsset != null && SkinAsset.Length > 0;

        public PlayerSkinChangeRequest Clone() => new PlayerSkinChangeRequest
        {
            Name = Name ?? string.Empty,
            Sex = Sex,
            SkinName = SkinName ?? string.Empty,
            SkinAsset = SkinAsset != null ? (byte[])SkinAsset.Clone() : Array.Empty<byte>(),
            Slot = Slot,
            Source = Source ?? string.Empty
        };

        public string Describe() =>
            "槽位=" + Slot + " 名字=\"" + (Name ?? string.Empty) + "\" · 性别=" + Sex +
            " · 皮肤=\"" + (SkinName ?? string.Empty) + "\" · " +
            (HasCustomAsset ? "自定义资源 " + SkinAsset.Length + "B" : "内置皮肤");
    }
}

using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 编辑玩家屏退出后，**自定义皮肤资源**的同步入口（实现方 = PlayerSkinModule，
    /// 组合根在装配时注入给联机角色模块）。
    ///
    /// 为什么需要这个端口：
    ///   · 名字 / 性别 / 内置皮肤 / 衣服 只需要一条 <c>PlayerProfile</c> 资料消息；
    ///   · 自定义 <c>.scskin</c> 还必须走既有的"资源读取 → 校验 → 分片上传 → 主机写记录 →
    ///     广播 → 客户端应用/占位"链，这条链归皮肤模块所有（资源缓存 / 计数 / 协议）。
    ///
    /// 返回 false + reason = 明确失败（资源缺失 / 校验失败 / 通道不可用）。
    /// 调用方必须计数并写原因，**绝不**把自定义皮肤静默换成内置皮肤。
    /// </summary>
    public interface IEditPlayerSkinSink
    {
        /// <summary>
        /// 把编辑玩家屏选中的皮肤（<paramref name="profile"/> 里的 SkinName/Sha256）同步出去。
        /// <paramref name="slot"/> = 本机角色槽位 0..3；<paramref name="isHost"/> = 本端是否主机。
        /// </summary>
        bool TrySyncEditedSkin(PlayerProfileSnapshot profile, int slot, bool isHost,
            out string reason);
    }
}

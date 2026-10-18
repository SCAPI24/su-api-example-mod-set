using System;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 引擎侧"动作 / 动画状态 / 手持物 / 背包"适配端口（实现方 = Engine/AvatarActionBridge.cs，
    /// 组合根注入）。模块层（Modules/）保持不依赖 Game，所以这些编解码/策略能被
    /// P:\tmp\tsverify 直接编译、用真实 TCP 跑；引擎动作只在组合根装配。
    ///
    /// 与旧实现的对应：
    ///   · 发送端抓引擎事实（PokingPhase / DigCellFace / m_aim / 统计计数器 / 手持物 / 背包）
    ///     Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:38-135
    ///   · 接收端把表现写进远端化身（相位 / 攻击 / 瞄准姿势 / 划船 / 受伤闪红 / 火）
    ///     Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:78-160
    /// </summary>
    public interface IPlayerActionPort
    {
        /// <summary>
        /// 这个 clientId 是不是**本机玩家自己**（主机 = 0；客户端 = 握手分配的编号）。
        /// 自己那一条绝不写进引擎（旧实现实机缺陷：把本机矿工相位钉在主机那份上 → 松手不收回）。
        /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:110-129
        /// </summary>
        bool IsLocalSlot(int clientId);

        /// <summary>本机当前有没有这个 clientId 的远端化身（没建好 / 已摘除 → 不写表现）。</summary>
        bool HasAvatar(int clientId);

        /// <summary>
        /// 抓本机玩家一帧的引擎事实（纯值）。false + reason = 拿不到（没进世界 / 没本机玩家 /
        /// 关键组件缺失）：调用方计数并限流写日志，**绝不猜**。
        /// </summary>
        bool TryCaptureLocalSignals(out LocalPlayerSignals signals, out string reason);

        /// <summary>
        /// 抓本机玩家可用背包的槽位值/数量/活跃槽（前 <see cref="PlayerInventoryMath.MaxSyncSlots"/> 槽）。
        /// Source: Survivalcraft/Game/ComponentInventoryBase.cs（GetSlotValue/GetSlotCount/ActiveSlotIndex）
        /// </summary>
        bool TryCaptureLocalInventory(out int[] values, out int[] counts, out int activeSlotIndex,
            out string reason);

        /// <summary>抓某个远端化身的背包（主机：把客户端上报落地后再读回来做权威广播）。</summary>
        bool TryCaptureInventory(int clientId, out int[] values, out int[] counts,
            out int activeSlotIndex, out string reason);

        /// <summary>
        /// 应用手持物（远端化身）：写活跃槽 + 该槽的值/数量（引擎的 ComponentHumanModel.DrawExtras
        /// 直接读 ComponentMiner.ActiveBlockValue 画手持物）。
        /// Source: Survivalcraft/Game/ComponentHumanModel.cs:DrawExtras
        /// </summary>
        bool TryApplyHeldItem(int clientId, in PlayerHeldItemSnapshot item, out string reason);

        /// <summary>
        /// 应用背包变更（远端化身；主机侧也用它把客户端上报落地）。
        /// 只按"变更槽位"写，容量不足的槽位跳过并回传实际值（调用方随后读回权威值广播）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:580-604（ApplyEquipmentSnapshot）
        /// </summary>
        bool TryApplyInventory(int clientId, in PlayerInventoryDelta delta, out string reason);

        /// <summary>
        /// 主机：从**主机权威化身背包**回读某个玩家端的手持物并立即广播给所有端。
        /// 开火后调用：把弓 / 弩 / 火枪的 draw=0（箭 / 弹已清）复位同步给开火者与其他客户端，
        /// 避免远端化身一直停在"满弓"姿势。
        /// false + reason = 不是主机 / 没有化身 / 回读失败 / 没有广播端口。
        /// </summary>
        bool TryBroadcastHeldItem(int clientId, HeldItemAimKind aimKind, out string reason);

        /// <summary>
        /// 每帧把动画表现写进远端化身（强制字段，见 <see cref="RemoteAvatarPresentation"/>）。
        /// 必须在引擎模型 Update 之后调用（组合根在 Frame.Update 里 tick）。
        /// </summary>
        bool TryApplyPresentation(int clientId, in RemoteAvatarPresentation presentation,
            out string reason);

        /// <summary>状态行用：引擎侧计数/诊断文本（不改变行为）。</summary>
        string Describe();
    }

    /// <summary>
    /// 主机侧背包落盘端口（实现方 = Engine/AvatarInventoryStore.cs，写 SuMultiplayerPlayers.xml）。
    /// 只有主机持有角色记录表；客户端没有这份文件的所有权。
    ///
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:576-618（RestorePlayerRecordInventory）
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:376-467（CapturePlayerRecord）
    /// </summary>
    public interface IPlayerInventoryStore
    {
        /// <summary>
        /// 把该 clientId 的权威背包写进角色记录（内存）并落盘（写盘按间隔节流）。
        /// 记录数组比同步槽位长时（创造仓库）只覆盖前 N 槽 —— 不截断记录里其余槽位。
        /// </summary>
        bool StoreInventory(int clientId, int[] values, int[] counts, int activeSlotIndex,
            int inventoryVersion, out string reason);

        /// <summary>
        /// 按身份读回角色记录里的背包（加入 / 重连 / 追帧的关键帧来源）。
        /// false + reason = 没有记录 / 记录里没有背包（首次加入）。**绝不造一份空背包当已同步**。
        /// </summary>
        bool TryLoadInventory(string identity, string name, out int[] values, out int[] counts,
            out int activeSlotIndex, out string reason);
    }
}

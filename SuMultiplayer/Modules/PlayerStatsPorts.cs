using System;
using System.Collections.Generic;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>客户端本机统计槽位的归属状态（见 <see cref="IPlayerStatsPort.EnsureLocalStatsOwnership"/>）。</summary>
    public enum LocalStatsOwnership
    {
        /// <summary>世界 / 本机角色还没就绪（正常加载时序，不是错误）。</summary>
        NotReady = 0,
        /// <summary>本机槽位只属于本机身份（下载世界带来的主机统计已搬走）。</summary>
        Owned = 1,
        /// <summary>无法分离（没有 SubsystemPlayerStats 等）——调用方必须明确记录。</summary>
        Failed = 2
    }

    /// <summary>角色记录读取结果：**区分"没有记录"与"读失败"**（调用方处理不同）。</summary>
    public enum PlayerStatsRecordLoad
    {
        /// <summary>读到了记录（统计 / 成就可能为空）。</summary>
        Loaded = 0,
        /// <summary>该身份还没有角色记录（新玩家 / 旧记录）——不是错误。</summary>
        Missing = 1,
        /// <summary>记录文件损坏 / 读取失败（明确失败，本会话拒绝写盘）。</summary>
        Failed = 2
    }

    /// <summary>
    /// 主机 / 客户端侧一个玩家的**引擎统计视图**。
    /// 纯值（不持有 Game 对象）：模块只通过端口读写引擎，自己不认识 PlayerData。
    /// </summary>
    public sealed class PlayerStatsWorldView
    {
        public PlayerStatsWorldView(int clientId, string identity, string name, string recordKey,
            PlayerStatSet stats)
        {
            ClientId = clientId;
            Identity = identity ?? string.Empty;
            Name = name ?? string.Empty;
            RecordKey = recordKey ?? string.Empty;
            Stats = stats;
        }

        /// <summary>主机侧：0 = 主机自己，&gt;0 = 对端传输编号。客户端侧恒为 0。</summary>
        public int ClientId { get; }

        /// <summary>稳定身份键（user:&lt;UserId&gt;；身份不可用时为退化键，端口会明确记录）。</summary>
        public string Identity { get; }

        /// <summary>展示名（非权威；只用于 UI / 日志）。</summary>
        public string Name { get; }

        /// <summary>世界作用域角色记录键（主机侧；客户端为空）。</summary>
        public string RecordKey { get; }

        /// <summary>引擎当前统计（主机 = 主机自己；客户端 = 本机引擎值，也是上报源）。</summary>
        public PlayerStatSet Stats { get; }
    }

    /// <summary>
    /// 统计 / 成就的**引擎侧端口**（实现 = Engine/PlayerStatsStore.cs）。
    ///
    /// 边界（与其它模块一致）：模块层不引用 Game；引擎字段的读写、角色记录落盘、
    /// 本机玩家定位全部由适配器完成。所有方法失败必须给明确原因（不静默兜底）。
    /// </summary>
    public interface IPlayerStatsPort
    {
        /// <summary>本机身份（UserId.dat；空 = 身份不可用）。</summary>
        string LocalIdentity { get; }

        /// <summary>客户端：抓本机玩家（本机 PlayerData）的引擎统计。</summary>
        bool TryCaptureLocal(out PlayerStatsWorldView view, out string reason);

        /// <summary>
        /// 客户端：**把下载世界带来的统计从本机玩家的槽位分出去**（一次 / 世界，幂等）。
        ///
        /// 为什么必须分：客户端的 Project.xml 是从主机整包下载的，其中
        /// <c>Players/Stats/&lt;index&gt;</c> 是**主机角色**的统计（键 = 主机本地 PlayerIndex）。
        /// 客户端沿着同一个本地索引进世界，引擎统计面板（GameMenuDialog 读
        /// <c>GetPlayerStats(本机 PlayerIndex)</c>）就会显示主机角色的数据，之后本机的行为
        /// 还继续往这份主机数据里累加。这里把那份统计搬回主机角色（客户端本地的 clientId=0 化身）
        /// 的槽位，本机槽位换成引擎默认值 —— 统计面板从此只显示本机身份自己的数据。
        ///
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:1488-1519
        ///         （SeparateDownloadedPlayerStats / TryAttachDownloadedHostStats，同一语义）
        /// </summary>
        LocalStatsOwnership EnsureLocalStatsOwnership(out string reason);

        /// <summary>
        /// 客户端：把主机**本机身份**记录里的权威统计写进本机玩家的 <c>PlayerStats</c>
        /// （加入 / 重连时的一次性恢复；引擎统计面板读同一对象）。
        /// <paramref name="applied"/> = 本引擎确实认识的字段子集（未知字段被跳过，模块计数）。
        /// </summary>
        bool TryApplyLocal(PlayerStatSet stats, out PlayerStatSet applied, out string reason);

        /// <summary>
        /// 主机：抓**主机自己**（clientId=0）当前的权威统计。
        /// 联机角色（clientId&gt;0）的统计由该玩家端自己上报，绝不从主机侧的化身上抓
        /// （主机的化身不跑客户端的挖掘 / 移动逻辑，PlayerIndex 还会被复用）。
        /// </summary>
        bool TryCaptureHost(out PlayerStatsWorldView view, out string reason);

        /// <summary>主机：读世界作用域角色记录里的统计 + 成就。</summary>
        PlayerStatsRecordLoad TryLoadRecord(string recordKey, out PlayerStatSet stats,
            out AchievementLedger achievements, out int rejectedAchievements, out string reason);

        /// <summary>主机：把统计 + 成就写进角色记录（先 Put，随后由 <see cref="FlushRecords"/> 落盘）。</summary>
        bool StoreRecord(string recordKey, PlayerStatSet stats, AchievementLedger achievements,
            out string reason);

        /// <summary>主机：有改动就落盘（失败必须返回 false + 原因）。</summary>
        bool FlushRecords(out string reason);

        /// <summary>记录键（= 身份稳定键；身份不可用时按名字绑定，调用方必须记录原因）。</summary>
        string BuildRecordKey(string identity, string name);
    }

    /// <summary>
    /// 只读名册视图里的一份玩家状态（**仅供视图 / 诊断**；绝不写回本机 PlayerStats）。
    /// 客户端自己的统计由本机计算并上报，主机按身份存储；他人数据只在这里展示。
    /// </summary>
    public sealed class SyncedPlayerState
    {
        public SyncedPlayerState(string identity, string name, bool isSelf, bool isHost,
            PlayerStatSet stats, IReadOnlyList<AchievementUnlock> achievements)
        {
            Identity = identity ?? string.Empty;
            Name = name ?? string.Empty;
            IsSelf = isSelf;
            IsHost = isHost;
            Stats = stats;
            Achievements = achievements ?? Array.Empty<AchievementUnlock>();
        }

        public string Identity { get; }
        public string Name { get; }
        public bool IsSelf { get; }
        public bool IsHost { get; }
        public PlayerStatSet Stats { get; }
        public IReadOnlyList<AchievementUnlock> Achievements { get; }
    }

    /// <summary>
    /// 统计 / 成就屏（引擎自带统计面板 + 本模组的只读成就视图）的**只读数据源**。
    /// 由统计模块实现；UI 只读，绝不写回权威值。
    /// </summary>
    public interface ISyncedPlayerStatsView
    {
        /// <summary>当前已知的全部玩家（主机在前）。快照，调用方可以安全遍历。</summary>
        IReadOnlyList<SyncedPlayerState> SnapshotPlayers();

        /// <summary>本机玩家自己的同步状态（没有收到权威快照 / 不在会话里 → false + 原因）。</summary>
        bool TryGetSelf(out SyncedPlayerState self, out string reason);

        /// <summary>给 UI 的纯文本报告（统计 + 每个人已解锁的成就；未知 id 原样显示）。</summary>
        string BuildReport();
    }
}

using System;
using System.Linq;
using Game;
using GameEntitySystem;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 联机**身份**：唯一来源是引擎的 UserId.dat
    /// （<c>Game.UserManager.ActiveUser.UniqueId</c>，见 <c>Survivalcraft/Game/UserManager.cs</c> /
    /// <c>SettingsManager.UserId</c>）。
    ///
    /// **任何地方都不允许用 PlayerIndex / clientId 当身份**：
    ///   主机上的"角色 0"是主机自己，客户端上的"角色 0"是客户端自己 ——
    ///   索引只是本机引擎的槽位号，跨机器没有任何意义。绑定、角色记录、
    ///   客户端 → 主机的角色映射一律走本类的 <see cref="RecordKey"/>。
    ///
    /// 旧实现对应处：
    ///   Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:183-186
    ///       public static string GetLocalPlayerIdentity() => UserManager.ActiveUser?.UniqueId ?? string.Empty;
    ///   Source: Mod/ScMultiplayer/Modules/Player/PlayerRecordKeyResolver.cs:10-17
    ///       GetPlayerRecordKey(identity, name) = identity 非空 ? identity.Trim() : "name:" + name.Trim()
    ///       GetNetworkRecordKey(clientId)     = "network:" + clientId   ← **本重写版刻意废弃**：
    ///           旧实现在客户端建"主机角色"时用它（CreateNetworkPlayer(0, "Host", GetNetworkRecordKey(0))），
    ///           那是按 index 兜底绑定，两端的"角色 0"根本不是同一个人。新版用握手里收到的主机身份。
    /// </summary>
    public static class PlayerIdentity
    {
        /// <summary>身份键前缀：绑定的是 UserId.dat。</summary>
        public const string UserKeyPrefix = "user:";

        /// <summary>名字键前缀：身份不可用时按名字绑定（必须在日志里写明原因）。</summary>
        public const string NameKeyPrefix = "name:";

        /// <summary>日志里只允许出现身份的前 8 位（GUID 全值属私有信息，禁止进日志/被跟踪文件）。</summary>
        private const int LogIdentityLength = 8;

        /// <summary>
        /// 本机身份 = UserId.dat（引擎已把它读进 <c>UserManager.ActiveUser.UniqueId</c>）。
        /// 未登录 / 读不到时返回空串 —— **不猜**，由调用方按"身份不可用"明确处理。
        /// </summary>
        public static string LocalIdentity() =>
            (UserManager.ActiveUser?.UniqueId ?? string.Empty).Trim();

        /// <summary>
        /// 本机显示名。
        ///
        /// **必须优先取本机角色（PlayerData.Name）**：引擎的 <c>UserManager.ActiveUser.DisplayName</c>
        /// 在现有引擎里是固定值 "Android User"（Source: Survivalcraft/Game/UserManager.cs:45），
        /// 它不是玩家名字。旧实现的 GetLocalPlayerName 先取 DisplayName，于是联机时主机/客户端的
        /// 名字永远显示成 "Android User"（实机缺陷：客户端看到主机的名字是 "Android User"）。
        /// 这里改成"真实角色名优先，DisplayName 只作为没有世界时的最后来源"。
        ///
        /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:157-181
        /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Name（角色创建界面写入的真实名字）
        /// Source: Survivalcraft/Game/UserManager.cs:45（DisplayName = "Android User"）
        /// </summary>
        public static string LocalName()
        {
            Project project = GameManager.Project;
            PlayerData local = project?.FindSubsystem<SubsystemPlayers>(false)?.PlayersData
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(local?.Name)) return local.Name.Trim();
            string name = UserManager.ActiveUser?.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        }

        /// <summary>
        /// 记录键 = 绑定用的唯一键。身份可用 → <c>user:&lt;UserId&gt;</c>；否则 <c>name:&lt;名字&gt;</c>。
        /// Source: Mod/ScMultiplayer/Modules/Player/PlayerRecordKeyResolver.cs:10-15
        /// </summary>
        public static string RecordKey(string identity, string fallbackName) =>
            RecordKey(identity, fallbackName, 0);

        /// <summary>
        /// 记录键（多本机角色版，append-only）：槽位 0 与旧版**完全相同**；
        /// 槽位 1..3 追加 <c>#&lt;slot&gt;</c>。同一设备上的多个本机角色共享 UserId，
        /// 只有加上槽位才能各存一条互不覆盖的角色记录。
        /// 旧记录文件无需迁移：新键只是新的 Identity 属性值，旧构建读到了也不会用。
        /// </summary>
        public static string RecordKey(string identity, string fallbackName, int slot)
        {
            string baseKey = !string.IsNullOrWhiteSpace(identity)
                ? UserKeyPrefix + identity.Trim()
                : NameKeyPrefix + (fallbackName ?? string.Empty).Trim();
            return slot <= 0 ? baseKey : baseKey + "#" + slot;
        }

        /// <summary>
        /// 这个名字是不是**引擎自带的默认显示名**（现有引擎固定为 "Android User"，见
        /// Survivalcraft/Game/UserManager.cs:45）。旧构建把 Hello.name 直接当角色名，
        /// 于是联机角色记录里存下了这个默认值 —— 它不代表玩家真实名字，需要一次显式迁移。
        /// </summary>
        public static bool IsEngineDefaultName(string name)
        {
            string displayName = UserManager.ActiveUser?.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(displayName)) return false;
            return string.Equals(name.Trim(), displayName.Trim(), StringComparison.Ordinal);
        }

        /// <summary>该记录键是不是"按身份绑定"的（false = 按名字绑定的退化键）。</summary>
        public static bool IsUserKey(string recordKey) =>
            recordKey != null && recordKey.StartsWith(UserKeyPrefix, StringComparison.Ordinal);

        /// <summary>日志用短身份：只打前 8 位，绝不打全值。</summary>
        public static string Shorten(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) return "(empty)";
            string text = identity.Trim();
            return text.Length <= LogIdentityLength
                ? text
                : text.Substring(0, LogIdentityLength) + "...";
        }

        /// <summary>日志用记录键描述：身份键只显示前 8 位身份（槽位后缀 <c>#n</c> 保留）。</summary>
        public static string Describe(string recordKey)
        {
            if (string.IsNullOrEmpty(recordKey)) return "(none)";
            if (!IsUserKey(recordKey)) return recordKey;
            string body = recordKey.Substring(UserKeyPrefix.Length);
            int hash = body.LastIndexOf('#');
            int slot = 0;
            if (hash > 0 && int.TryParse(body.Substring(hash + 1), out int parsedSlot))
            {
                slot = parsedSlot;
                body = body.Substring(0, hash);
            }
            return UserKeyPrefix + Shorten(body) + (slot > 0 ? "#" + slot : string.Empty);
        }
    }
}

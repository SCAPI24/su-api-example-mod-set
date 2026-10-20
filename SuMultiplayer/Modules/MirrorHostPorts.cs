using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 联机调试功能「镜像宿主」的配置（**默认开启**，配置键 <c>mirrorcharacterauto</c>）：
    ///   · <c>mirrorcharacterauto</c>（bool，默认 true）：主机是否为每个加入的客户端
    ///     **自动创建一个真实的本地角色**（= 引擎里的第二个 / 第三 / 第四个分屏），
    ///     并把这个分屏的相机绑到该客户端的第一人称；
    ///   · <c>mirrorcharacter</c>（复用，**显式名字优先**）：世界里已经有这个本地角色 →
    ///     直接把它当"镜像宿主"，不新建；没有 **且** 槽位还有空 → 才按 <c>mirrorhostnameprefix</c> 自动新建；
    ///   · <c>mirrorcharactermode</c>（复用，默认 <c>replace</c>）：绑定方式（replace = 相机 = 客户端 +
    ///     宿主身体隐藏不可碰撞；copy = 把客户端状态逐拍复制到宿主身上）；
    ///   · <c>mirrorhostnameprefix</c>（默认 <c>"Mirror"</c>）：自动角色命名 <c>&lt;前缀&gt;#&lt;clientId&gt;</c>。
    ///
    /// 语义（显式、不猜）：
    ///   · 关闭 = 一个引擎调用都不会发生（正常玩法零影响）；
    ///   · 前缀空 = 用默认 <c>Mirror</c>；带控制字符 / 超长 ⇒ **明确拒绝**（功能保持关闭 + 日志）；
    ///   · <c>mirrorcharactermode</c> 写错 ⇒ 功能保持关闭 + 明确日志（与 MirrorCharacterModes 同一套解析）。
    ///
    /// 本类型是纯值，不含静态可变状态。
    /// </summary>
    public readonly struct MirrorHostSettings
    {
        /// <summary>缺省自动角色名前缀（产品口径）。</summary>
        public const string DefaultNamePrefix = "Mirror";

        /// <summary>前缀上限（超过就不是一个可读的角色名了；引擎角色名也是短名字）。</summary>
        public const int MaxNamePrefixLength = 16;

        private MirrorHostSettings(bool enabled, string namePrefix, MirrorCharacterMode mode,
            string explicitCharacterName)
        {
            Enabled = enabled;
            NamePrefix = namePrefix ?? DefaultNamePrefix;
            Mode = mode;
            ExplicitCharacterName = explicitCharacterName ?? string.Empty;
        }

        /// <summary>是否启用（<c>mirrorcharacterauto=true</c> 且前缀 / 模式配置合法）。</summary>
        public bool Enabled { get; }

        /// <summary>自动角色名前缀（已 Trim；非空）。</summary>
        public string NamePrefix { get; }

        /// <summary>绑定方式（默认 <see cref="MirrorCharacterMode.Replace"/>）。</summary>
        public MirrorCharacterMode Mode { get; }

        /// <summary>
        /// 复用的 <c>mirrorcharacter</c>（已 Trim；空 = 没有显式角色名）。
        /// 非空时**显式名字优先**：世界里已有该名字的本地角色就直接用它，不新建。
        /// </summary>
        public string ExplicitCharacterName { get; }

        public static MirrorHostSettings Disabled =>
            new MirrorHostSettings(false, DefaultNamePrefix, MirrorCharacterModes.Default,
                string.Empty);

        /// <summary>
        /// 解析四个键。返回 false = **配置写错**（<paramref name="settings"/> 保持关闭）；
        /// 返回 true 且 <c>settings.Enabled == false</c> = 显式关闭（合法，不是错误）。
        /// </summary>
        public static bool TryParse(bool enabled, string namePrefixValue, string modeValue,
            string explicitCharacterValue, out MirrorHostSettings settings, out string error)
        {
            settings = Disabled;
            error = null;
            // 关闭就是关闭：前缀 / 模式 / 名字都不再校验，也不产生任何引擎动作。
            if (!enabled) return true;
            string prefix = namePrefixValue == null ? string.Empty : namePrefixValue.Trim();
            if (prefix.Length == 0) prefix = DefaultNamePrefix;
            if (prefix.Length > MaxNamePrefixLength)
            {
                error = "mirrorhostnameprefix=\"" + prefix + "\" 太长（上限 " +
                    MaxNamePrefixLength + " 个字符）：自动角色名 = <前缀>#<clientId>";
                return false;
            }
            for (int i = 0; i < prefix.Length; i++)
            {
                if (!char.IsControl(prefix[i])) continue;
                error = "mirrorhostnameprefix 含控制字符：自动角色名必须是一行可读文本";
                return false;
            }
            if (!MirrorCharacterModes.TryParse(modeValue, out MirrorCharacterMode mode,
                    out string modeError))
            {
                error = modeError;
                return false;
            }
            string explicitName = (explicitCharacterValue ?? string.Empty).Trim();
            settings = new MirrorHostSettings(true, prefix, mode, explicitName);
            return true;
        }

        /// <summary>
        /// 自动角色的基准名 = <c>&lt;前缀&gt;#&lt;clientId&gt;</c>（重名时由
        /// <see cref="MirrorHostNaming.TryResolve"/> 追加序号）。
        /// </summary>
        public string AutoNameFor(int clientId) =>
            NamePrefix + "#" + clientId.ToString(CultureInfo.InvariantCulture);

        /// <summary>日志 / 状态行口径。</summary>
        public override string ToString()
        {
            if (!Enabled) return "off";
            return "on prefix=\"" + NamePrefix + "\" mode=" + MirrorCharacterModes.Name(Mode) +
                (ExplicitCharacterName.Length == 0
                    ? " explicit=none"
                    : " explicit=\"" + ExplicitCharacterName + "\"");
        }
    }

    /// <summary>
    /// 镜像宿主"能不能建"的判定结果（纯值；<see cref="Reason"/> 为空 = 可以建）。
    /// 拒绝原因与计数键一一对应：
    ///   <c>not-hosting</c> / <c>no-client</c> / <c>max-players</c> / <c>cruel</c> /
    ///   <c>adventure</c> / <c>duplicate-name</c>。
    /// </summary>
    public readonly struct MirrorHostGate
    {
        public const string NotHosting = "not-hosting";
        public const string NoClient = "no-client";
        public const string MaxPlayers = "max-players";
        public const string Cruel = "cruel";
        public const string Adventure = "adventure";
        public const string DuplicateName = "duplicate-name";

        private MirrorHostGate(bool allowed, string reason, int freeSlots, string detail)
        {
            Allowed = allowed;
            Reason = reason;
            FreeSlots = freeSlots;
            Detail = detail;
        }

        public bool Allowed { get; }

        /// <summary>拒绝原因（<see cref="Allowed"/> 为 true 时是 null）。</summary>
        public string Reason { get; }

        /// <summary>判定时算出的"同屏可用池"（= 4 − 主机真实本地角色数；不合法输入时为 0）。</summary>
        public int FreeSlots { get; }

        /// <summary>给日志用的细节（为什么拒绝；可直接定位）。</summary>
        public string Detail { get; }

        public static MirrorHostGate Ok(int freeSlots) =>
            new MirrorHostGate(true, null, freeSlots, null);

        public static MirrorHostGate Refuse(string reason, int freeSlots, string detail) =>
            new MirrorHostGate(false, reason, freeSlots, detail);

        public string Describe() =>
            Allowed
                ? "allowed freeSlots=" + FreeSlots
                : "blocked reason=" + Reason + " freeSlots=" + FreeSlots +
                  (string.IsNullOrEmpty(Detail) ? string.Empty : " (" + Detail + ")");
    }

    /// <summary>世界模式（引擎 <c>WorldSettings.GameMode</c> 的**子集**，纯值，不依赖 Game）。</summary>
    public enum MirrorHostWorldMode
    {
        /// <summary>Harmless / Creative / Survival / Challenging 等可加角色的模式。</summary>
        Normal = 0,

        /// <summary>Cruel：引擎拒绝加角色（Source: Survivalcraft/Game/PlayersScreen.cs:48-51）。</summary>
        Cruel = 1,

        /// <summary>Adventure：引擎拒绝加角色（Source: Survivalcraft/Game/PlayersScreen.cs:52-55）。</summary>
        Adventure = 2
    }

    /// <summary>
    /// 镜像宿主的**纯逻辑池计算 / 闸门**（无引擎依赖，可直接自检）。
    ///
    /// 核心规则（用户定，务必与引擎事实一致）：
    ///   · 引擎上限 <c>SubsystemPlayers.MaxPlayers = 4</c>、<c>SubsystemGameWidgets.MaxGameWidgets
    ///     = 4</c>（Source: Survivalcraft/Game/SubsystemPlayers.cs:20、SubsystemGameWidgets.cs:12）；
    ///   · **同屏可用池 = 4 − 主机实际本地角色数**，而"主机实际本地角色数" =
    ///     <c>PlayersData.Count</c>：远端化身已在 NetworkAvatarSpawner.TryCreate 里被移出
    ///     <c>PlayersData</c>（也摘掉了 GameWidget），所以它们**不占池**；
    ///   · 例：主机 h1/h2 → 池 2 → 客户端 1 占第 3 个本地角色、客户端 2 占第 4 个；
    ///   · 池满再加就是明确拒绝（<c>mirrorHostBlocked(max-players)</c>），不崩、不静默、不顶掉已有角色；
    ///   · 自动创建的宿主**也**在 <c>PlayersData</c> 里（它必须在那儿才能有 GameWidget），
    ///     所以它同样占池 —— 4 个本地角色的上限不会因为"自动"而放宽。
    /// </summary>
    public static class MirrorHostPool
    {
        /// <summary>引擎硬上限：
        /// Source: Survivalcraft/Game/SubsystemPlayers.cs:20 / SubsystemGameWidgets.cs:12。</summary>
        public const int MaxLocalPlayers = 4;

        /// <summary>池 = 4 − 主机真实本地角色数（负数 / 非法输入按 0 处理）。</summary>
        public static int FreeSlots(int hostLocalPlayers) =>
            Math.Max(0, MaxLocalPlayers - Math.Max(0, hostLocalPlayers));

        /// <summary>
        /// 能否为 <paramref name="clientId"/> 新建一个本地角色。判定顺序固定（原因只报第一个命中的）：
        /// 非主机 → <c>not-hosting</c>；clientId ≤ 0 → <c>no-client</c>；
        /// Cruel → <c>cruel</c>；Adventure → <c>adventure</c>；池空 → <c>max-players</c>。
        /// </summary>
        public static MirrorHostGate Evaluate(bool hosting, int clientId, int hostLocalPlayers,
            MirrorHostWorldMode mode)
        {
            int free = FreeSlots(hostLocalPlayers);
            if (!hosting)
            {
                return MirrorHostGate.Refuse(MirrorHostGate.NotHosting, free,
                    "本端没有主持房间（只有主机为客户端创建本地角色）");
            }
            if (clientId <= 0)
            {
                return MirrorHostGate.Refuse(MirrorHostGate.NoClient, free,
                    "clientId=" + clientId.ToString(CultureInfo.InvariantCulture) +
                    " 非法（主机自己 0 不是联机客户端）");
            }
            if (mode == MirrorHostWorldMode.Cruel)
            {
                return MirrorHostGate.Refuse(MirrorHostGate.Cruel, free,
                    "Cruel 模式引擎不允许加角色（Source: PlayersScreen.cs:48-51）");
            }
            if (mode == MirrorHostWorldMode.Adventure)
            {
                return MirrorHostGate.Refuse(MirrorHostGate.Adventure, free,
                    "Adventure 模式引擎不允许加角色（Source: PlayersScreen.cs:52-55）");
            }
            if (free <= 0)
            {
                return MirrorHostGate.Refuse(MirrorHostGate.MaxPlayers, free,
                    "本地角色已满 " + Math.Max(0, hostLocalPlayers) + "/" + MaxLocalPlayers);
            }
            return MirrorHostGate.Ok(free);
        }

        /// <summary>状态行口径：<c>hostLocals=n used=n/4 free=n</c>。</summary>
        public static string Describe(int hostLocalPlayers)
        {
            int locals = Math.Max(0, hostLocalPlayers);
            return "hostLocals=" + locals + " used=" + locals + "/" + MaxLocalPlayers +
                " free=" + FreeSlots(locals);
        }

        /// <summary>引擎 <c>GameMode</c> 的名字 → 纯值模式（只认 Cruel / Adventure，其余 = Normal）。</summary>
        public static MirrorHostWorldMode FromGameModeName(string gameModeName)
        {
            if (string.Equals(gameModeName, "Cruel", StringComparison.OrdinalIgnoreCase))
                return MirrorHostWorldMode.Cruel;
            if (string.Equals(gameModeName, "Adventure", StringComparison.OrdinalIgnoreCase))
                return MirrorHostWorldMode.Adventure;
            return MirrorHostWorldMode.Normal;
        }
    }

    /// <summary>
    /// 自动角色命名（纯函数）：
    ///   · 基准名 = <c>&lt;前缀&gt;#&lt;clientId&gt;</c>（例 <c>Mirror#2</c>）；
    ///   · 世界里已有同名（大小写不敏感，与引擎按名字查找口径一致）→ 依次追加 <c>-2</c>、<c>-3</c>…；
    ///   · 全部候选都被占用 → 返回 false + 原因（<c>duplicate-name</c>），绝不覆盖已有角色。
    /// </summary>
    public static class MirrorHostNaming
    {
        /// <summary>重名时最多尝试的候选数（含基准名）。</summary>
        public const int MaxCandidates = 32;

        /// <summary>
        /// 解析 <c>&lt;前缀&gt;#&lt;N&gt;</c> 形式的名字 → N（N &gt; 0）；不是这种形式返回 -1。
        /// 用于"复用"：世界存档里已经有 <c>Mirror#2</c> 时，客户端 2 再进来直接复用它。
        /// </summary>
        public static int ParseClientId(string name, string namePrefix)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(namePrefix)) return -1;
            string text = name.Trim();
            string prefix = namePrefix.Trim();
            if (text.Length <= prefix.Length) return -1;
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return -1;
            if (text[prefix.Length] != '#') return -1;
            string tail = text.Substring(prefix.Length + 1);
            if (tail.Length == 0) return -1;
            int digits = 0;
            while (digits < tail.Length && tail[digits] >= '0' && tail[digits] <= '9') digits++;
            if (digits == 0) return -1;
            // 追加的序号后缀（"-2"）允许；其余字符一律不算。
            if (digits < tail.Length && tail[digits] != '-') return -1;
            if (!int.TryParse(tail.Substring(0, digits), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int clientId))
                return -1;
            return clientId > 0 ? clientId : -1;
        }

        /// <summary>
        /// 给 <paramref name="clientId"/> 挑一个**未被占用**的名字。
        /// <paramref name="existingNames"/> = 世界里全部主机本地角色的名字。
        /// </summary>
        public static bool TryResolve(string namePrefix, int clientId,
            IReadOnlyList<string> existingNames, out string name, out string reason)
        {
            name = null;
            reason = null;
            string prefix = (namePrefix ?? string.Empty).Trim();
            if (prefix.Length == 0)
            {
                reason = "mirrorhostnameprefix 为空（自动角色名前缀必须有值）";
                return false;
            }
            if (clientId <= 0)
            {
                reason = "clientId=" + clientId.ToString(CultureInfo.InvariantCulture) + " 非法";
                return false;
            }
            string baseName = prefix + "#" + clientId.ToString(CultureInfo.InvariantCulture);
            for (int attempt = 0; attempt < MaxCandidates; attempt++)
            {
                string candidate = attempt == 0
                    ? baseName
                    : baseName + "-" + (attempt + 1).ToString(CultureInfo.InvariantCulture);
                if (!Contains(existingNames, candidate))
                {
                    name = candidate;
                    return true;
                }
            }
            reason = "名字 \"" + baseName + "\"（含 " + MaxCandidates +
                " 个序号候选）全部被其它本机角色占用";
            return false;
        }

        private static bool Contains(IReadOnlyList<string> names, string candidate)
        {
            if (names == null) return false;
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals((names[i] ?? string.Empty).Trim(), candidate,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 镜像宿主计数器（实例持有，无静态状态）。字段名与需求逐项对应，状态行 / 10 秒限流日志全部显式：
    ///   <c>mirrorHostCreated</c> / <c>mirrorHostReused</c> / <c>mirrorHostRemoved</c> /
    ///   <c>mirrorHostPending</c> / <c>mirrorHostBind</c> / <c>mirrorHostRestore</c> /
    ///   <c>mirrorHostBlocked(max-players|cruel|adventure|duplicate-name|not-hosting|no-client)</c> /
    ///   <c>mirrorHostFailures</c> / <c>mirrorHostFreeSlots</c>（+ <c>mirrorHostActive</c>）
    ///   —— 以及自动宿主维护的四项：
    ///   <c>mirrorHostImmune</c>（成功写入整套免疫字段的宿主·拍数）/
    ///   <c>mirrorHostImmunityFailures</c>（免疫字段 / 状态机读失败次数）/
    ///   <c>mirrorHostStuckDead</c>（命中卡死条件的累计次数）/
    ///   <c>mirrorHostRevived</c>（触发引擎复活的次数）。
    /// 口径：
    ///   · Created / Reused / Removed / Bind / Restore = **事件**次数（不是每帧）；
    ///   · Pending = "宿主还没就绪（IsReadyForPlaying / ComponentPlayer 未满足）"的**帧**数；
    ///   · Blocked = **事件**次数（同一客户端被拒绝后 1 秒内不重复尝试，避免每帧刷计数）；
    ///   · FreeSlots = 当前值（池 = 4 − 主机真实本地角色数）；ActiveHosts = 当前自动宿主数；
    ///   · Immune / StuckDead / Revived / ImmunityFailures = 引擎端口每拍返回的增量之和
    ///     （只统计自动宿主，手动角色与客户端化身永不进入）。
    /// </summary>
    public sealed class MirrorHostCounters
    {
        private readonly Dictionary<string, long> m_blocked =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public long Created { get; private set; }
        public long Reused { get; private set; }
        public long Removed { get; private set; }
        public long Pending { get; private set; }
        public long Bind { get; private set; }
        public long Restore { get; private set; }
        public long Failures { get; private set; }
        public int FreeSlots { get; private set; }
        public int ActiveHosts { get; private set; }

        /// <summary>成功写入整套免疫字段的宿主·拍数。</summary>
        public long Immune { get; private set; }

        /// <summary>免疫字段写失败 / PlayerData 状态机读失败的次数。</summary>
        public long ImmunityFailures { get; private set; }

        /// <summary>命中卡死条件（health≤0 / DeathCamera / PlayerData=PlayerDead）的累计次数。</summary>
        public long StuckDead { get; private set; }

        /// <summary>触发引擎复活链路（RemoveEntity）的累计次数。</summary>
        public long Revived { get; private set; }

        public long BlockedTotal
        {
            get
            {
                long total = 0;
                foreach (long value in m_blocked.Values) total += value;
                return total;
            }
        }

        public long Blocked(string reason) =>
            m_blocked.TryGetValue(reason ?? string.Empty, out long value) ? value : 0;

        public void NoteCreated() => Created++;

        public void NoteReused() => Reused++;

        public void NoteRemoved() => Removed++;

        public void NotePending() => Pending++;

        public void NoteBind() => Bind++;

        public void NoteRestore() => Restore++;

        public void NoteFailure() => Failures++;

        public void NoteBlocked(string reason)
        {
            string key = string.IsNullOrEmpty(reason) ? "unknown" : reason;
            m_blocked.TryGetValue(key, out long value);
            m_blocked[key] = value + 1;
        }

        /// <summary>累加一拍自动宿主维护结果（只加正增量；手动角色 / 客户端化身不进入统计）。</summary>
        public void NoteMaintenance(in MirrorHostMaintenance maintenance)
        {
            if (maintenance.Immune > 0) Immune += maintenance.Immune;
            if (maintenance.ImmunityFailures > 0)
                ImmunityFailures += maintenance.ImmunityFailures;
            if (maintenance.StuckDetected > 0) StuckDead += maintenance.StuckDetected;
            if (maintenance.Revived > 0) Revived += maintenance.Revived;
            if (maintenance.ReviveFailures > 0) ImmunityFailures += maintenance.ReviveFailures;
        }

        /// <summary>端口整拍失败（世界没加载 / 找不到 SubsystemPlayers）计一次免疫失败。</summary>
        public void NoteImmunityFailure() => ImmunityFailures++;

        public void SetFreeSlots(int freeSlots) => FreeSlots = Math.Max(0, freeSlots);

        public void SetActiveHosts(int hosts) => ActiveHosts = Math.Max(0, hosts);

        /// <summary>
        /// 计数摘要。Blocked 写在括号里按原因列出（需求口径 <c>mirrorHostBlocked(max-players)</c>）；
        /// 没有拒绝时也输出 <c>mirrorHostBlocked=0(0)</c>，状态行不会缺键。
        /// </summary>
        public string Describe()
        {
            var text = new StringBuilder("mirrorHostCreated=");
            text.Append(Created).Append(" mirrorHostReused=").Append(Reused)
                .Append(" mirrorHostRemoved=").Append(Removed)
                .Append(" mirrorHostPending=").Append(Pending)
                .Append(" mirrorHostBind=").Append(Bind)
                .Append(" mirrorHostRestore=").Append(Restore)
                .Append(" mirrorHostBlocked=").Append(BlockedTotal).Append('(');
            if (m_blocked.Count == 0)
            {
                text.Append('0');
            }
            else
            {
                bool first = true;
                foreach (KeyValuePair<string, long> pair in m_blocked)
                {
                    if (!first) text.Append(',');
                    first = false;
                    text.Append(pair.Key).Append('=').Append(pair.Value);
                }
            }
            text.Append(") mirrorHostFailures=").Append(Failures)
                .Append(" mirrorHostFreeSlots=").Append(FreeSlots)
                .Append(" mirrorHostActive=").Append(ActiveHosts)
                .Append(" mirrorHostImmune=").Append(Immune)
                .Append(" mirrorHostImmunityFailures=").Append(ImmunityFailures)
                .Append(" mirrorHostStuckDead=").Append(StuckDead)
                .Append(" mirrorHostRevived=").Append(Revived);
            return text.ToString();
        }
    }

    /// <summary>
    /// 主机上**一个本地角色**的纯值快照（引擎端口的输出，纯逻辑据此决定"复用还是新建"）。
    /// </summary>
    public readonly struct MirrorHostLocalPlayer
    {
        public MirrorHostLocalPlayer(string name, int playerIndex, bool ready,
            bool hasComponentPlayer, bool autoCreated)
        {
            Name = name ?? string.Empty;
            PlayerIndex = playerIndex;
            Ready = ready;
            HasComponentPlayer = hasComponentPlayer;
            AutoCreated = autoCreated;
        }

        public string Name { get; }

        public int PlayerIndex { get; }

        /// <summary>
        /// 就绪标记：引擎状态机 <c>Playing/PlayerDead</c>（<c>IsReadyForPlaying</c>）**并且**
        /// 已经有 <c>ComponentPlayer</c>（Source: Survivalcraft/Game/PlayerData.cs:124-134）。
        /// </summary>
        public bool Ready { get; }

        public bool HasComponentPlayer { get; }

        /// <summary>是否本功能自动创建的（只有这种角色才允许被本功能移除）。</summary>
        public bool AutoCreated { get; }

        public override string ToString() =>
            "\"" + Name + "\"(PlayerIndex=" +
            PlayerIndex.ToString(CultureInfo.InvariantCulture) +
            (AutoCreated ? ",auto" : ",manual") + (Ready ? ",ready" : ",pending") + ")";
    }

    /// <summary>自动创建的镜像宿主（引擎端口的输出）。</summary>
    public readonly struct MirrorHostAutoHost
    {
        public MirrorHostAutoHost(int clientId, string name, int playerIndex, bool ready)
        {
            ClientId = clientId;
            Name = name ?? string.Empty;
            PlayerIndex = playerIndex;
            Ready = ready;
        }

        public int ClientId { get; }

        public string Name { get; }

        public int PlayerIndex { get; }

        /// <summary>宿主 PlayerData 是否已就绪
        /// （<c>IsReadyForPlaying &amp;&amp; ComponentPlayer != null</c>）。</summary>
        public bool Ready { get; }

        public override string ToString() =>
            "client" + ClientId.ToString(CultureInfo.InvariantCulture) + "=\"" + Name + "\"" +
            "(PlayerIndex=" + PlayerIndex.ToString(CultureInfo.InvariantCulture) +
            (Ready ? ",ready" : ",pending") + ")";
    }

    /// <summary>
    /// 镜像宿主的**引擎端口**。纯逻辑（池计算 / 生命周期 / 复用 / 计数 / 限流日志）只依赖本接口；
    /// 引擎侧实现 = <c>Engine/MirrorHostEngine.cs</c>：
    ///   · 添加本地角色只走公开链路 <c>new PlayerData(project)</c> → 设 Name / InputDevice
    ///     → <c>SubsystemPlayers.AddPlayerData</c>
    ///     （Source: Survivalcraft/Game/PlayerScreen.cs:121,223；SubsystemPlayers.cs:62-75；
    ///     SubsystemGameWidgets.cs:85-100 自动建 GameWidget + 分屏布局切换）；
    ///   · 移除只走 <c>SubsystemPlayers.RemovePlayerData</c>（Source: SubsystemPlayers.cs:77-90）；
    ///   · 绑定只走 <c>MirrorCharacterEngine</c>（replace = 相机 = 客户端 + 宿主身体隐藏不可碰撞）。
    ///
    /// 实现方硬约束：
    ///   · **只移除自己 auto 创建的角色**（<see cref="MirrorHostLocalPlayer.AutoCreated"/>），
    ///     手动创建的角色（h1/h2/H3…）绝不调用 RemovePlayerData；
    ///   · 绝不为远端化身建本地角色（本功能只做"客户端画面宿主"，不做游戏逻辑代理）；
    ///   · 未就绪（<c>!Ready</c>）时绝不绑定（计数 <c>mirrorHostPending</c>）。
    /// </summary>
    public interface IMirrorHostPort
    {
        /// <summary>世界里主机本地角色数 = <c>SubsystemPlayers.PlayersData.Count</c>（远端化身不在其中）。</summary>
        int HostLocalPlayerCount { get; }

        /// <summary>当前世界里自动创建的镜像宿主数。</summary>
        int AutoHostCount { get; }

        /// <summary>当前自动宿主绑定的镜像（未绑定 = -1）。</summary>
        int BoundClientId { get; }

        /// <summary>世界模式（Cruel / Adventure 按引擎规则不可加角色）。</summary>
        MirrorHostWorldMode WorldMode { get; }

        /// <summary>
        /// 列出世界里全部主机本地角色（按 PlayerIndex 升序）。失败返回 false（原因可直接定位）。
        /// </summary>
        bool TryListLocalPlayers(List<MirrorHostLocalPlayer> players, out string reason);

        /// <summary>列出本功能自动创建的镜像宿主（按 clientId 升序）。</summary>
        bool TryListAutoHosts(List<MirrorHostAutoHost> hosts, out string reason);

        /// <summary>
        /// 为一个客户端创建一个**新的本地角色**（auto 标记），并记到
        /// <c>clientId → PlayerData</c> 表。失败返回 false，<paramref name="reason"/>
        /// 写清原因（引擎抛异常也在这里转成原因）。
        /// </summary>
        bool TryCreate(int clientId, string name, out MirrorHostAutoHost host, out string reason);

        /// <summary>
        /// 把一个**已存在**的本地角色交给自动宿主账本（复用：存档里已有同前缀 / 显式名字的角色）。
        /// 不新建、不改名字；成功后它归入 auto 账本（离开时会连同它的实体一起移除）。
        /// </summary>
        bool TryAdopt(int clientId, string name, out MirrorHostAutoHost host, out string reason);

        /// <summary>只移除本功能自动创建的那个角色（手动角色返回 false + 原因，绝不移除）。</summary>
        bool TryRemove(int clientId, out string reason);

        /// <summary>
        /// 把宿主绑定到客户端的第一人称（镜像）。宿主必须已就绪；未就绪返回 false
        /// （<paramref name="pending"/> = true，调用方计数 <c>mirrorHostPending</c>）。
        /// </summary>
        bool TryBind(int clientId, string name, MirrorCharacterMode mode, out bool pending,
            out string reason);

        /// <summary>解绑镜像并写回宿主绑定前快照（可重复调用）。</summary>
        bool TryRestore(string reason, out string failureReason);

        /// <summary>每拍校验已绑定的镜像（宿主还在 / 客户端化身还在 / 相机还对），必要时重绑相机。</summary>
        bool ValidateBound(out string reason);

        /// <summary>
        /// 每拍维护**自动镜像宿主**：
        ///   · 免疫（免伤 / 免病 / 着火 / 体征归位，清单见 <see cref="MirrorHostImmunityFields"/>）；
        ///   · 卡死自动修复（三条条件任一命中 → 引擎 <c>Project.RemoveEntity</c> 复活链路，
        ///     见 <see cref="MirrorHostStuck"/>）。
        ///
        /// 只作用于**自动宿主**（<see cref="MirrorHostImmunityTarget"/> 三道闸门：宿主账本 /
        /// 自动命名 / 不是联机化身）；手动角色（h1/h2/H3）与客户端化身一个字段都不写。
        ///
        /// 返回 false = 引擎侧根本维护不了（世界没加载 / 找不到 <c>SubsystemPlayers</c>），
        /// <paramref name="reason"/> 写清原因；**单宿主单字段写失败不算 false**，
        /// 记在 <see cref="MirrorHostMaintenance.ImmunityFailures"/> 里。
        /// </summary>
        bool TryMaintainAutoHosts(out MirrorHostMaintenance outcome, out string reason);
    }

    /// <summary>
    /// 镜像宿主端口的**可选扩展**：客户端列表
    /// （与 <c>IMirrorCharacterPort.TryListClientIds</c> 同一口径）。
    /// 纯逻辑只用 <see cref="IMirrorHostPort"/>；引擎实现额外实现本接口。
    /// </summary>
    public interface IMirrorHostClientSource
    {
        /// <summary>当前有联机化身的客户端编号
        /// （clientId &gt; 0，升序；没有客户端 = true + 空列表）。</summary>
        bool TryListClientIds(List<int> clientIds, out string reason);
    }

    /// <summary>
    /// 镜像宿主的**纯逻辑**（无 Game 依赖，可直接自检）：
    ///   池计算（<see cref="MirrorHostPool"/>）/ 闸门 / 复用还是新建 / 命名 / 生命周期 /
    ///   10 秒限流日志 / 计数。
    ///
    /// 生命周期（需求逐项）：
    ///   ① 主机 + 世界内 + <c>mirrorcharacterauto</c> + 该客户端还没有宿主 + 池未满 →
    ///      新建（或复用）并绑定；
    ///   ② 宿主未就绪（<c>!IsReadyForPlaying || !HasComponentPlayer</c>）→
    ///      只计数 <c>mirrorHostPending</c>，**绝不提前绑定**；
    ///   ③ 客户端离开（/ 离开房间 / 世界卸载 / 配置关闭 / 模块卸载）→ 解绑 + **只移除 auto 角色**；
    ///   ④ 再次进房 / 新客户端：世界里已有同前缀的 auto 角色 → **复用**，不无限新建；
    ///   ⑤ 显式名字（<c>mirrorcharacter</c>）优先：世界里已有该角色 → 复用它
    ///      （多个同名则拒绝，不猜）。手动创建的角色**绝不**归入 auto 账本（离开时不会删），
    ///      继续按前缀自动命名。
    ///
    /// 本类**无静态可变状态**：时间 / 日志 / 引擎动作都由组合根注入。
    /// </summary>
    public sealed class MirrorHostController
    {
        /// <summary>所有日志的最小间隔（需求口径 = 10 秒）。</summary>
        private const double LogIntervalSeconds = 10.0;

        /// <summary>被拒绝 / 失败后同一客户端的重试间隔（秒），避免每帧刷计数与日志。</summary>
        private const double RetryIntervalSeconds = 1.0;

        private readonly IMirrorHostPort m_port;
        private readonly Action<string> m_info;
        private readonly Action<string> m_warn;
        private readonly Func<double> m_clock;
        private readonly MirrorHostSettings m_settings;
        private readonly bool m_configured;
        private readonly string m_configError;
        private readonly MirrorHostCounters m_counters = new MirrorHostCounters();
        private readonly Dictionary<string, double> m_lastLogTime =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<int, double> m_nextAttemptTime =
            new Dictionary<int, double>();
        private readonly List<MirrorHostLocalPlayer> m_locals =
            new List<MirrorHostLocalPlayer>();
        private readonly List<int> m_clientIds = new List<int>();
        private readonly List<MirrorHostAutoHost> m_autoHosts = new List<MirrorHostAutoHost>();
        private readonly List<string> m_names = new List<string>();

        private bool m_shutdown;

        public MirrorHostController(bool enabled, string namePrefixValue, string modeValue,
            string explicitCharacterValue, IMirrorHostPort port, Action<string> info,
            Action<string> warn, Func<double> clock)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_info = info;
            m_warn = warn;
            m_clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (!MirrorHostSettings.TryParse(enabled, namePrefixValue, modeValue,
                    explicitCharacterValue, out MirrorHostSettings settings, out string error))
            {
                // 配置写错：功能保持关闭，但日志里给出可直接定位的原因（不静默）。
                m_settings = MirrorHostSettings.Disabled;
                m_configured = false;
                m_configError = error;
                WarnRateLimited("config",
                    "[SuMP][mirror-host] 配置解析失败，功能保持关闭：" + error);
                return;
            }
            m_settings = settings;
            m_configured = settings.Enabled;
        }

        /// <summary>配置里是否启用了自动镜像宿主（默认 true；配错 = false）。</summary>
        public bool IsConfigured => m_configured;

        /// <summary>配置写错时保留的原因（未写错 = null）。</summary>
        public string ConfigError => m_configError;

        public MirrorHostSettings Settings => m_settings;

        public MirrorHostCounters Counters => m_counters;

        /// <summary>当前绑定的客户端（未绑定 = -1；自动客户端视图据此让位）。</summary>
        public int BoundClientId => m_port.BoundClientId;

        /// <summary>
        /// 这个客户端是否已有镜像宿主（自动宿主 / 已绑定的镜像）——
        /// 自动客户端建视图（clientview）据此让位（显式镜像优先）。
        /// </summary>
        public bool HasHostFor(int clientId)
        {
            if (clientId <= 0) return false;
            if (m_port.BoundClientId == clientId) return true;
            m_autoHosts.Clear();
            if (m_port.TryListAutoHosts(m_autoHosts, out _))
            {
                for (int i = 0; i < m_autoHosts.Count; i++)
                    if (m_autoHosts[i].ClientId == clientId) return true;
            }
            return false;
        }

        /// <summary>
        /// 每帧驱动。参数由组合根给：世界是否加载、本端是否正在主持房间
        /// （<paramref name="hosting"/> 与 <c>m_sessionModule.IsHostingRoom</c> 同一口径）。
        /// 顺序：配置关闭 / 非主机 / 世界外 → 全部拆除；
        /// 否则 → 校验现有宿主 → 为缺宿主的客户端补一个 → 绑定。
        /// </summary>
        public void Tick(bool inWorld, bool hosting)
        {
            if (m_shutdown) return;
            if (!m_configured)
            {
                if (m_port.AutoHostCount > 0) RemoveEveryHost("配置 mirrorcharacterauto=false");
                return;
            }
            if (!inWorld)
            {
                RemoveEveryHost("世界已卸载");
                m_counters.SetFreeSlots(0);
                return;
            }
            if (!hosting)
            {
                RemoveEveryHost("已离开房间（不再是主机）");
                m_counters.SetFreeSlots(0);
                return;
            }
            int hostLocals = m_port.HostLocalPlayerCount;
            m_counters.SetFreeSlots(MirrorHostPool.FreeSlots(hostLocals));
            m_counters.SetActiveHosts(m_port.AutoHostCount);

            // ① 客户端列表先抓好（"客户端是否还在"只能按它判断；抓不到就本拍不删，下一拍重来）。
            if (!TryCollectClientIds(out string clientReason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("list-clients",
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：列不出已加入的客户端：" + (clientReason ?? "原因未知"));
                return;
            }

            // ② 校验现有自动宿主：客户端离开 / 宿主被引擎删掉 → 解绑 + 只移除 auto 角色。
            if (!m_port.TryListAutoHosts(m_autoHosts, out string listReason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("list-hosts",
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：列不出自动镜像宿主：" + (listReason ?? "原因未知"));
                return;
            }
            for (int i = 0; i < m_autoHosts.Count; i++)
            {
                MirrorHostAutoHost host = m_autoHosts[i];
                if (IsClientPresent(host.ClientId)) continue;
                RemoveHost(host, "客户端 " + host.ClientId + " 已离开房间");
                m_nextAttemptTime.Remove(host.ClientId);
            }

            // ③ 自动宿主维护：免疫（免伤 / 免病 / 着火 / 体征归位）+ 卡死自动修复。
            //    必须在 ValidateBound 之前：卡死修复会先解绑再移除实体，同一拍就不会再拿
            //    "已销毁的宿主"去校验相机；随后 ⑤ 在宿主重新出生后重新绑定（HUD 桥接随之重建）。
            MaintainHosts(hostLocals);

            // ④ 已绑定的镜像：先校验（客户端化身还在 / 相机还对），失效就解绑并等下一轮。
            if (m_port.BoundClientId > 0 && !m_port.ValidateBound(out string invalidReason))
            {
                WarnRateLimited("invalid",
                    "[SuMP][mirror-host] 镜像绑定失效（client " + m_port.BoundClientId + "）：" +
                    invalidReason + " → 解绑并等待 · " + m_counters.Describe());
                Restore("绑定失效：" + invalidReason);
            }

            // ⑤ 为"有联机角色但还没有宿主"的客户端补宿主（按 clientId 升序；每拍最多建一个）。
            bool acted = false;
            for (int i = 0; i < m_clientIds.Count; i++)
            {
                int clientId = m_clientIds[i];
                if (HasAutoHost(clientId)) continue;
                if (m_port.BoundClientId == clientId) continue;
                double now = m_clock();
                if (m_nextAttemptTime.TryGetValue(clientId, out double next) && now < next)
                    continue;
                bool ok = PrepareHost(clientId, hostLocals, out bool stop);
                acted = true;
                hostLocals = m_port.HostLocalPlayerCount;
                m_counters.SetFreeSlots(MirrorHostPool.FreeSlots(hostLocals));
                // 被拒绝 / 失败：同一客户端 2 秒内不再重试（不刷计数与日志；客户端离开时上面的清理会移除门控）。
                if (!ok) m_nextAttemptTime[clientId] = now + RetryIntervalSeconds;
                if (ok || stop) break;
            }

            // ⑥ 已就绪但还没绑定的宿主 → 绑定（未就绪只计数 pending，绝不提前绑）。
            if (m_port.TryListAutoHosts(m_autoHosts, out _))
            {
                for (int i = 0; i < m_autoHosts.Count; i++)
                {
                    MirrorHostAutoHost host = m_autoHosts[i];
                    if (m_port.BoundClientId == host.ClientId) continue;
                    if (!host.Ready)
                    {
                        m_counters.NotePending();
                        InfoRateLimited("pending-" + host.ClientId,
                            "[SuMP][mirror-host] mirrorHostPending=" + m_counters.Pending +
                            "：客户端 " + host.ClientId + " 的宿主「" + host.Name +
                            "」还没就绪（等 IsReadyForPlaying + ComponentPlayer）→ 不提前绑定 · " +
                            DescribeLive(hostLocals));
                        continue;
                    }
                    BindHost(host);
                }
            }
            m_counters.SetActiveHosts(m_port.AutoHostCount);

            // ⑦ 10 秒一条状态行（池 / 自动宿主 / 计数 / 免疫 + 卡死修复；排查时一眼看到镜像宿主）。
            InfoRateLimited("status",
                "[SuMP][mirror-host] " + DescribeLive(hostLocals) + " bound=" +
                (m_port.BoundClientId > 0
                    ? "client" + m_port.BoundClientId.ToString(CultureInfo.InvariantCulture)
                    : "none") +
                " hosts=[" + DescribeHosts() + "]" + (acted ? " (本拍有动作)" : string.Empty));
        }

        /// <summary>组合根卸载时调用：解绑 + 只移除 auto 角色（明确原因），随后停止驱动。</summary>
        public void Shutdown(string reason)
        {
            RemoveEveryHost(reason ?? "模块卸载");
            m_shutdown = true;
        }

        /// <summary>状态行（加载日志 / 5 秒周期日志 / 报告用）。</summary>
        public string StatusLine()
        {
            string config = m_configured
                ? m_settings.ToString()
                : (m_configError == null ? "off(默认)" : "invalid(" + m_configError + ")");
            string state;
            if (!m_configured) state = "off";
            else if (m_port.AutoHostCount > 0) state = "on";
            else state = "waiting";
            return "mirror-host: config=" + config + " state=" + state + " " +
                MirrorHostPool.Describe(m_port.HostLocalPlayerCount) + " " +
                m_counters.Describe();
        }

        public string DescribeCounters() => m_counters.Describe();

        // ------------------------------------------------------------------ 内部

        /// <summary>
        /// 每拍自动宿主维护（免疫 + 卡死修复）。三条硬约束：
        ///   · **只对自动宿主**：判定与写入都在引擎端口里（<see cref="MirrorHostImmunityTarget"/>），
        ///     这里只汇总计数，不碰任何玩家角色；
        ///   · 卡死修复**只在引擎复活链路成功时**计 mirrorHostRevived（失败计
        ///     mirrorHostImmunityFailures，并带原因写日志）；
        ///   · 日志全部 10 秒限流（热路径每帧都会调用）。
        /// </summary>
        private void MaintainHosts(int hostLocals)
        {
            if (!m_port.TryMaintainAutoHosts(out MirrorHostMaintenance maintenance,
                    out string reason))
            {
                m_counters.NoteImmunityFailure();
                WarnRateLimited("maintain",
                    "[SuMP][mirror-host] mirrorHostImmunityFailures=" +
                    m_counters.ImmunityFailures + "：自动宿主维护失败（免疫与卡死修复本拍都没做）：" +
                    (reason ?? "原因未知"));
                return;
            }
            m_counters.NoteMaintenance(maintenance);
            if (maintenance.StuckDetected > 0 || maintenance.Revived > 0 ||
                maintenance.ReviveFailures > 0)
            {
                InfoRateLimited("maintain-revive",
                    "[SuMP][mirror-host] mirrorHostStuckDead=" + m_counters.StuckDead +
                    " mirrorHostRevived=" + m_counters.Revived +
                    "：自动宿主卡死自动修复（" + maintenance.Describe() +
                    "）→ 先解绑镜像再走引擎 RemoveEntity 复活链路，重新出生后由 ValidateBound / 绑定步骤重绑相机与 HUD · " +
                    DescribeLive(hostLocals));
            }
            if (maintenance.ImmunityFailures > 0)
            {
                WarnRateLimited("maintain-fields",
                    "[SuMP][mirror-host] mirrorHostImmunityFailures=" +
                    m_counters.ImmunityFailures + "：自动宿主免疫字段没写全（" +
                    maintenance.Describe() + "）：" + (reason ?? "原因未知"));
            }
        }

        private bool TryCollectClientIds(out string reason)
        {
            m_clientIds.Clear();
            if (m_port is IMirrorHostClientSource source)
                return source.TryListClientIds(m_clientIds, out reason);
            reason = "引擎端口没有实现 IMirrorHostClientSource（列不出已加入的客户端）";
            return false;
        }

        private bool HasAutoHost(int clientId)
        {
            if (m_port.BoundClientId == clientId) return true;
            m_autoHosts.Clear();
            if (!m_port.TryListAutoHosts(m_autoHosts, out _)) return false;
            for (int i = 0; i < m_autoHosts.Count; i++)
                if (m_autoHosts[i].ClientId == clientId) return true;
            return false;
        }

        private bool IsClientPresent(int clientId)
        {
            if (clientId <= 0) return false;
            for (int i = 0; i < m_clientIds.Count; i++)
                if (m_clientIds[i] == clientId) return true;
            return false;
        }

        /// <summary>
        /// 为 <paramref name="clientId"/> 准备宿主：复用（已有显式名字 / 同前缀 auto 角色）或新建。
        /// <paramref name="stop"/> = 这一拍不要再处理别的客户端（池满 / 名字用尽，必然同样失败）。
        /// </summary>
        private bool PrepareHost(int clientId, int hostLocals, out bool stop)
        {
            stop = false;
            if (!m_port.TryListLocalPlayers(m_locals, out string localReason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("list-locals",
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：列不出主机本地角色：" + (localReason ?? "原因未知"));
                return false;
            }
            m_names.Clear();
            for (int i = 0; i < m_locals.Count; i++) m_names.Add(m_locals[i].Name);

            // 复用 ①：显式名字（mirrorcharacter）优先 —— 世界里已经有它。
            string explicitName = m_settings.ExplicitCharacterName;
            if (explicitName.Length > 0)
            {
                var found = default(MirrorHostLocalPlayer);
                int matches = 0;
                for (int i = 0; i < m_locals.Count; i++)
                {
                    if (!string.Equals(m_locals[i].Name.Trim(), explicitName,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    matches++;
                    found = m_locals[i];
                }
                if (matches > 1)
                {
                    // 多个同名：复制到哪一个？拒绝（与 MirrorCharacterController 的
                    // multipleMatches 同一口径）。
                    stop = true;
                    m_counters.NoteBlocked(MirrorHostGate.DuplicateName);
                    WarnRateLimited("duplicate-explicit",
                        "[SuMP][mirror-host] mirrorHostBlocked(" + MirrorHostGate.DuplicateName +
                        ")=" + m_counters.Blocked(MirrorHostGate.DuplicateName) + "：有 " + matches +
                        " 个本地角色都叫「" + explicitName + "」→ 拒绝选宿主（不猜是哪一个）· " +
                        m_counters.Describe());
                    return false;
                }
                if (matches == 1)
                {
                    if (found.AutoCreated)
                    {
                        if (m_port.TryAdopt(clientId, found.Name, out MirrorHostAutoHost adopted,
                                out string adoptReason))
                        {
                            m_counters.NoteReused();
                            InfoRateLimited("reuse-explicit",
                                "[SuMP][mirror-host] mirrorHostReused=" + m_counters.Reused +
                                "：显式名字「" + found.Name +
                                "」已存在（auto 角色）→ 直接作为客户端 " + clientId +
                                " 的镜像宿主（不新建）· " + DescribeLive(hostLocals));
                            return true;
                        }
                        m_counters.NoteFailure();
                        WarnRateLimited("adopt-explicit",
                            "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                            "：复用显式角色「" + found.Name + "」失败：" +
                            (adoptReason ?? "原因未知"));
                        return false;
                    }
                    // 手动创建的角色（例 h1/h2/H3）：**绝不**归入 auto 账本（离开时不会删）。
                    // 让它当"镜像目标"是 mirrorcharacter 的产品口径，不是本功能的；这里明确跳过。
                    InfoRateLimited("explicit-manual",
                        "[SuMP][mirror-host] 显式名字「" + explicitName +
                        "」是手动创建的本机角色 → 不作为自动宿主（离开时不会删除它）；继续按前缀 \"" +
                        m_settings.NamePrefix + "\" 自动命名 · 需要 H3 式显式镜像请用 mirrorcharacter");
                }
            }

            // 复用 ②：世界里已有本前缀的 auto 角色，且它是为同一个客户端建的
            // （名字 <前缀>#<clientId>）。
            for (int i = 0; i < m_locals.Count; i++)
            {
                MirrorHostLocalPlayer local = m_locals[i];
                if (!local.AutoCreated) continue;
                if (MirrorHostNaming.ParseClientId(local.Name, m_settings.NamePrefix) != clientId)
                    continue;
                if (m_port.TryAdopt(clientId, local.Name, out MirrorHostAutoHost adopted,
                        out string adoptReason))
                {
                    m_counters.NoteReused();
                    InfoRateLimited("reuse-" + clientId,
                        "[SuMP][mirror-host] mirrorHostReused=" + m_counters.Reused +
                        "：世界里已有自动角色「" + local.Name + "」（世界存档）→ 作为客户端 " +
                        clientId + " 的镜像宿主（不新建）· " + DescribeLive(hostLocals));
                    return true;
                }
                m_counters.NoteFailure();
                WarnRateLimited("adopt-" + clientId,
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：复用自动角色「" + local.Name + "」失败：" + (adoptReason ?? "原因未知"));
                return false;
            }

            // 新建：先过闸门（非主机 / 无客户端 / Cruel / Adventure / 池满）。
            MirrorHostGate gate = MirrorHostPool.Evaluate(hosting: true, clientId, m_locals.Count,
                m_port.WorldMode);
            if (!gate.Allowed)
            {
                stop = gate.Reason == MirrorHostGate.MaxPlayers;
                m_counters.NoteBlocked(gate.Reason);
                WarnRateLimited("blocked-" + gate.Reason + "-" + clientId,
                    "[SuMP][mirror-host] mirrorHostBlocked(" + gate.Reason + ")=" +
                    m_counters.Blocked(gate.Reason) + "：客户端 " + clientId +
                    " 无法获得镜像宿主 · " + gate.Detail + " · " +
                    MirrorHostPool.Describe(m_locals.Count) +
                    "（不崩、不顶掉已有角色）· " + m_counters.Describe());
                return false;
            }

            // 命名：<前缀>#<clientId>，重名追加序号。
            if (!MirrorHostNaming.TryResolve(m_settings.NamePrefix, clientId, m_names,
                    out string name, out string nameReason))
            {
                stop = true;
                m_counters.NoteBlocked(MirrorHostGate.DuplicateName);
                WarnRateLimited("blocked-duplicate-" + clientId,
                    "[SuMP][mirror-host] mirrorHostBlocked(" + MirrorHostGate.DuplicateName +
                    ")=" + m_counters.Blocked(MirrorHostGate.DuplicateName) + "：客户端 " +
                    clientId + " 拿不到可用的自动角色名 · " + nameReason + "（现有角色：[" +
                    DescribeNames(m_names) + "]）· " + m_counters.Describe());
                return false;
            }

            if (!m_port.TryCreate(clientId, name, out MirrorHostAutoHost created,
                    out string createReason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("create-" + clientId,
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：为客户端 " + clientId + " 创建本地角色「" + name + "」失败：" +
                    (createReason ?? "原因未知") + " · " + DescribeLive(hostLocals));
                return false;
            }
            m_counters.NoteCreated();
            InfoRateLimited("created-" + clientId,
                "[SuMP][mirror-host] mirrorHostCreated=" + m_counters.Created + "：客户端 " +
                clientId + " → 新建本地角色「" + created.Name + "」（PlayerIndex=" +
                created.PlayerIndex +
                "；GameWidget 由引擎自动创建；等它就绪再绑定）· " +
                DescribeLive(hostLocals + 1));
            return true;
        }

        private void BindHost(MirrorHostAutoHost host)
        {
            if (!m_port.TryBind(host.ClientId, host.Name, m_settings.Mode, out bool pending,
                    out string reason))
            {
                if (pending)
                {
                    m_counters.NotePending();
                    InfoRateLimited("bind-pending-" + host.ClientId,
                        "[SuMP][mirror-host] mirrorHostPending=" + m_counters.Pending +
                        "：宿主「" + host.Name + "」还没就绪 → 不提前绑定（" + reason + "）");
                    return;
                }
                m_counters.NoteFailure();
                WarnRateLimited("bind-" + host.ClientId,
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：把宿主「" + host.Name + "」绑定到客户端 " + host.ClientId + " 失败：" +
                    (reason ?? "原因未知"));
                return;
            }
            m_counters.NoteBind();
            InfoRateLimited("bind-ok-" + host.ClientId,
                "[SuMP][mirror-host] mirrorHostBind=" + m_counters.Bind + "：「" + host.Name +
                "」← client " + host.ClientId + " · mirrorMode=" +
                MirrorCharacterModes.Name(m_settings.Mode) + "（" +
                (m_settings.Mode == MirrorCharacterMode.Replace
                    ? "该分屏 = 客户端第一人称；宿主身体不渲染、不可碰撞"
                    : "每拍把客户端状态复制到宿主身上") + "）· " + m_counters.Describe());
        }

        private void Restore(string reason)
        {
            if (!m_port.TryRestore(reason, out string failure))
            {
                m_counters.NoteFailure();
                WarnRateLimited("restore-fail",
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：解绑镜像失败（宿主写回快照）：" + (failure ?? "原因未知") + " · " + reason);
                return;
            }
            m_counters.NoteRestore();
            InfoRateLimited("restore",
                "[SuMP][mirror-host] mirrorHostRestore=" + m_counters.Restore + "：" + reason +
                " · " + m_counters.Describe());
        }

        /// <summary>解绑 + **只移除 auto 角色**（手动角色绝不 RemovePlayerData）。</summary>
        private void RemoveHost(MirrorHostAutoHost host, string reason)
        {
            if (m_port.BoundClientId == host.ClientId) Restore(reason);
            if (!m_port.TryRemove(host.ClientId, out string removeReason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("remove-" + host.ClientId,
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：移除自动角色「" + host.Name + "」失败（" + reason + "）：" +
                    (removeReason ?? "原因未知"));
                return;
            }
            m_counters.NoteRemoved();
            InfoRateLimited("removed-" + host.ClientId,
                "[SuMP][mirror-host] mirrorHostRemoved=" + m_counters.Removed +
                "：auto 角色「" + host.Name + "」（client " + host.ClientId + "）· " + reason +
                " · 手动创建的角色不受影响 · " + m_counters.Describe());
            m_counters.SetActiveHosts(m_port.AutoHostCount);
            m_counters.SetFreeSlots(MirrorHostPool.FreeSlots(m_port.HostLocalPlayerCount));
        }

        private void RemoveEveryHost(string reason)
        {
            if (!m_port.TryListAutoHosts(m_autoHosts, out string listReason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("list-hosts",
                    "[SuMP][mirror-host] mirrorHostFailures=" + m_counters.Failures +
                    "：" + reason + "：列不出自动镜像宿主：" + (listReason ?? "原因未知"));
                return;
            }
            if (m_autoHosts.Count == 0 && m_port.BoundClientId > 0) Restore(reason);
            for (int i = 0; i < m_autoHosts.Count; i++) RemoveHost(m_autoHosts[i], reason);
            m_nextAttemptTime.Clear();
        }

        private string DescribeLive(int hostLocals) =>
            MirrorHostPool.Describe(hostLocals) + " " + m_counters.Describe();

        private string DescribeHosts()
        {
            m_autoHosts.Clear();
            if (!m_port.TryListAutoHosts(m_autoHosts, out _)) return "?";
            if (m_autoHosts.Count == 0) return "none";
            var text = new StringBuilder();
            for (int i = 0; i < m_autoHosts.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(m_autoHosts[i]);
            }
            return text.ToString();
        }

        private static string DescribeNames(List<string> names)
        {
            if (names == null || names.Count == 0) return "none";
            var text = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(names[i] ?? string.Empty);
            }
            return text.ToString();
        }

        private void InfoRateLimited(string key, string message)
        {
            if (!ShouldLog(key)) return;
            m_info?.Invoke(message);
        }

        private void WarnRateLimited(string key, string message)
        {
            if (!ShouldLog(key)) return;
            m_warn?.Invoke(message);
        }

        private bool ShouldLog(string key)
        {
            double now = m_clock();
            if (m_lastLogTime.TryGetValue(key, out double last) &&
                now - last < LogIntervalSeconds)
                return false;
            m_lastLogTime[key] = now;
            return true;
        }
    }
}

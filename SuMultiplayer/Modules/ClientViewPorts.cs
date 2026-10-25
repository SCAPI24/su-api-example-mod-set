using System;
using System.Collections.Generic;
using System.Globalization;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 自动客户端分屏（**默认开启**）的配置：
    ///   · <c>clientview</c>（bool，默认 true）：主机是否为加入的客户端自动建一个分屏视图；
    ///   · <c>clientviewclient</c>（可选过滤器）：留空 / auto / all / first = 每个有角色的客户端
    ///     各一个视图；<c>&lt;clientId&gt;</c>（&gt; 0）= 只为这个客户端建视图。
    ///
    /// 语义（显式、不猜）：
    ///   · <c>clientview=false</c> ⇒ 一个引擎调用都不会发生（关闭时零影响）；
    ///   · 过滤器写错（<c>client2</c> / 负数 / 0 / 多个编号）⇒ **功能保持关闭** + 明确日志，
    ///     绝不静默当成"全部客户端"（否则配错会意外开出一堆视图）。
    ///
    /// 本类型是纯值，不含静态可变状态。
    /// </summary>
    public readonly struct ClientViewSettings
    {
        private ClientViewSettings(bool enabled, bool allClients, int clientId)
        {
            Enabled = enabled;
            AllClients = allClients;
            ClientId = clientId;
        }

        /// <summary>是否启用自动客户端分屏。</summary>
        public bool Enabled { get; }

        /// <summary>true = 所有已加入客户端；false = 只给 <see cref="ClientId"/> 建视图。</summary>
        public bool AllClients { get; }

        /// <summary>过滤器指定的客户端编号（<see cref="AllClients"/> 时为 0）。</summary>
        public int ClientId { get; }

        public static ClientViewSettings Disabled => new ClientViewSettings(false, false, 0);

        /// <summary>
        /// 解析 <c>clientview</c> + <c>clientviewclient</c>。
        /// 返回 false = **配置写错**（<paramref name="settings"/> 保持关闭）；
        /// 返回 true 且 <c>settings.Enabled == false</c> = 显式关闭（合法，不是错误）。
        /// </summary>
        public static bool TryParse(bool enabled, string clientValue,
            out ClientViewSettings settings, out string error)
        {
            settings = Disabled;
            error = null;
            if (!enabled) return true; // 关闭就是关闭：过滤器不再校验，也不产生任何引擎动作。
            string text = (clientValue ?? string.Empty).Trim();
            if (text.Length == 0 || IsAllWord(text))
            {
                settings = new ClientViewSettings(true, true, 0);
                return true;
            }
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int clientId))
            {
                if (clientId <= 0)
                {
                    error = "clientviewclient=\"" + text +
                        "\" 非法：clientId 必须 > 0（0 是主机自己；留空 / auto 表示所有客户端）";
                    return false;
                }
                settings = new ClientViewSettings(true, false, clientId);
                return true;
            }
            error = "clientviewclient=\"" + text +
                "\" 无法识别（写法：留空 / auto / all / first / <clientId>；clientId 必须 > 0）";
            return false;
        }

        /// <summary>该客户端是否应该拥有自动视图（关闭时永远 false；主机 clientId 0 永远 false）。</summary>
        public bool Allows(int clientId)
        {
            if (!Enabled || clientId <= 0) return false;
            return AllClients || clientId == ClientId;
        }

        private static bool IsAllWord(string text) =>
            string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "first", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
            text == "0" || text == "*";

        /// <summary>日志 / 状态行口径：<c>off</c>、<c>all</c> 或 <c>client#2</c>。</summary>
        public override string ToString()
        {
            if (!Enabled) return "off";
            return AllClients ? "all" : "client#" + ClientId.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 主机上一个可考虑的客户端角色（纯值，引擎端口的输出）。
    /// <see cref="Ready"/> = 该客户端的联机化身的 <c>ComponentPlayer.ComponentBody</c> 已就绪
    /// （没就绪时只计数 + 限流日志，下一拍再看，绝不拿别的角色顶替）。
    /// </summary>
    public readonly struct ClientViewCandidate
    {
        public ClientViewCandidate(int clientId, int slot, bool ready, int playerIndex)
        {
            ClientId = clientId;
            Slot = slot;
            Ready = ready;
            PlayerIndex = playerIndex;
        }

        public int ClientId { get; }
        public int Slot { get; }
        public bool Ready { get; }
        /// <summary>该联机化身在引擎里占用的 PlayerIndex（-1 = 未知；用于日志与槽位账本）。</summary>
        public int PlayerIndex { get; }

        public override string ToString() =>
            ClientId.ToString(CultureInfo.InvariantCulture) + "#" +
            Slot.ToString(CultureInfo.InvariantCulture) + (Ready ? "" : "(pending)");
    }

    /// <summary>
    /// 分屏槽位账本（**纯函数**，可直接自检）。
    ///
    /// 引擎硬上限：
    ///   · <c>SubsystemGameWidgets.MaxGameWidgets = 4</c>（Source: Survivalcraft/Game/SubsystemGameWidgets.cs:12）；
    ///   · <c>SubsystemPlayers.MaxPlayers = 4</c>（Source: Survivalcraft/Game/SubsystemPlayers.cs:20）。
    /// 一个远端联机化身虽然会把 GameWidget 从界面里摘掉，但它**仍然占用一个 PlayerIndex**
    /// （NetworkAvatarSpawner.TryCreate 用 <c>PlayerIndex</c> 建 PlayerData）；
    /// 自动客户端视图也同时占一个 PlayerIndex + 一个 GameWidget。因此：
    ///
    ///     hostLocals + avatars + views ≤ 4
    ///
    /// 这才是"还有没有空槽"的正确账本（只看 GameWidget 数会误判：3 本机 + 1 化身时
    /// GameWidget 只有 3 个，但 PlayerIndex 已经用满 4 个）。
    /// </summary>
    public static class ClientViewSlots
    {
        /// <summary>引擎硬上限（PlayerIndex 与 GameWidget 都是 4）。</summary>
        public const int MaxSlots = 4;

        public static int UsedSlots(int hostLocalPlayers, int avatars, int views) =>
            Math.Max(0, hostLocalPlayers) + Math.Max(0, avatars) + Math.Max(0, views);

        public static bool HasCapacity(int hostLocalPlayers, int avatars, int views) =>
            UsedSlots(hostLocalPlayers, avatars, views) < MaxSlots;

        /// <summary>
        /// 在 <c>0..3</c> 里挑第一个没被占用的 PlayerIndex。
        /// <paramref name="usedPlayerIndexes"/> 必须包含：本机玩家、远端化身、已有的自动视图。
        /// 没有空位时返回 false（调用方据此明确报 clientViewNoSlot，绝不重用索引）。
        /// </summary>
        public static bool TryAllocate(IReadOnlyList<int> usedPlayerIndexes, out int playerIndex)
        {
            playerIndex = -1;
            if (usedPlayerIndexes == null) return false;
            for (int index = 0; index < MaxSlots; index++)
            {
                bool used = false;
                for (int i = 0; i < usedPlayerIndexes.Count; i++)
                {
                    if (usedPlayerIndexes[i] != index) continue;
                    used = true;
                    break;
                }
                if (used) continue;
                playerIndex = index;
                return true;
            }
            return false;
        }

        /// <summary>状态行 / 日志口径：本机角色 + 联机化身 + 自动视图 = 已用 / 4，剩余几个。</summary>
        public static string Describe(int hostLocalPlayers, int avatars, int views)
        {
            int used = UsedSlots(hostLocalPlayers, avatars, views);
            return "hostLocals=" + Math.Max(0, hostLocalPlayers) + " avatars=" + Math.Max(0, avatars) +
                " views=" + Math.Max(0, views) + " used=" + used + "/" + MaxSlots +
                " free=" + Math.Max(0, MaxSlots - used);
        }
    }

    /// <summary>
    /// 自动客户端分屏计数器（实例持有，无静态状态）。字段名与需求逐项对应：
    ///   clientViewCreated / clientViewRemoved / clientViewNoSlot / clientViewFailed /
    ///   clientViewTargetMissing / clientViewFrames / clientViewSlots。
    /// 口径：
    ///   · Created / Removed：成功的创建 / 完整拆除**次数**（不是每帧）；
    ///   · NoSlot / Failed：尝试创建失败的**事件**（同一客户端 1 秒内不重复尝试，避免每帧刷计数）；
    ///   · TargetMissing：**每帧**如实累加（客户端已加入但角色还没就绪 / 目标暂时消失）；
    ///   · Frames：每个存活视图每帧 +1（= 视图·帧）；
    ///   · Slots：当前存活视图数（= 状态行里的 clientViewSlots），PeakSlots 记录峰值。
    /// </summary>
    public sealed class ClientViewCounters
    {
        public long Created { get; private set; }
        public long Removed { get; private set; }
        public long NoSlot { get; private set; }
        public long Failed { get; private set; }
        public long TargetMissing { get; private set; }
        public long Frames { get; private set; }
        public long Slots { get; private set; }
        public int PeakSlots { get; private set; }

        public void NoteCreated(int slots)
        {
            Created++;
            SetSlots(slots);
        }

        public void NoteRemoved(int slots)
        {
            Removed++;
            SetSlots(slots);
        }

        public void NoteNoSlot() => NoSlot++;

        public void NoteFailed() => Failed++;

        public void NoteTargetMissing() => TargetMissing++;

        public void NoteFrame(int slots)
        {
            Frames++;
            SetSlots(slots);
        }

        /// <summary>把当前视图数同步进 clientViewSlots（每帧 / 每次创建拆除后调用）。</summary>
        public void SetSlots(int slots)
        {
            Slots = Math.Max(0, slots);
            if (Slots > PeakSlots) PeakSlots = (int)Slots;
        }

        /// <summary>
        /// 需求口径"显式镜像优先"的计数：自动客户端视图因为同一客户端已有显式镜像 /
        /// 镜像宿主（<c>mirrorcharacter</c> / <c>mirrorcharacterauto</c>）而让位的**事件**次数。
        /// 只在让位发生的那一拍 +1，不是每帧。
        /// </summary>
        public long YieldToExplicit { get; private set; }

        /// <summary>命中"显式镜像优先"时调用（组合根在让位判定里调用）。</summary>
        public void NoteYieldToExplicit() => YieldToExplicit++;

        /// <summary>镜像宿主（mirrorcharacterauto）接管时让位（与 <see cref="NoteYieldToExplicit"/> 同一口径）。</summary>
        public void NoteYieldToMirrorHost() => NoteYieldToExplicit();

        public string Describe() =>
            "created=" + Created + " removed=" + Removed + " noSlot=" + NoSlot +
            " failed=" + Failed + " targetMissing=" + TargetMissing + " frames=" + Frames +
            " slots=" + Slots + " peakSlots=" + PeakSlots +
            " yieldExplicit=" + YieldToExplicit;
    }

    /// <summary>
    /// 自动客户端分屏的**引擎端口**：纯逻辑（配置 / 生命周期 / 计数 / 限流日志）只依赖本接口；
    /// 引擎侧实现 = <c>Engine/ClientViewEngine.cs</c>（camera-only 临时 PlayerData + GameWidget，
    /// 与 <c>Engine/DebugMirrorClient.cs</c> 同一套"实体不加入 Project"的配方）。
    ///
    /// 实现方硬约束：
    ///   · 视图实体**绝不调用 Project.AddEntity** → 不存盘、不碰撞、不被攻击、不跑输入/物理；
    ///   · 视图 PlayerData 建完立刻从 <c>SubsystemPlayers.m_playersData</c> 摘掉；
    ///   · GameWidget 的相机 Target 绑到**远端化身的 ComponentPlayer**（第一人称完全一致）；
    ///   · 只读 NetworkAvatarSpawner 的公开 API，不写客户端身份 / 记录 / 统计。
    /// </summary>
    public interface IClientViewPort
    {
        /// <summary>当前存活的自动客户端视图数（= clientViewSlots）。</summary>
        int ViewCount { get; }

        /// <summary>当前主机本地角色数（PlayersData 里不是联机化身的角色）。</summary>
        int HostLocalPlayerCount { get; }

        /// <summary>当前远端联机化身数（每个化身占一个 PlayerIndex）。</summary>
        int AvatarCount { get; }

        bool IsAlive(int clientId);

        /// <summary>列出当前所有客户端角色（含还没就绪的）；失败时 reason 写明原因。</summary>
        bool TryListCandidates(List<ClientViewCandidate> candidates, out string reason);

        /// <summary>列出当前已建视图的客户端编号（按升序）。</summary>
        void ListViews(List<int> clientIds);

        /// <summary>
        /// 为客户端建一个 camera-only 分屏视图。失败返回 false；
        /// <paramref name="noSlot"/> = 引擎 4 PlayerIndex / 4 GameWidget 上限（clientViewNoSlot）。
        /// </summary>
        bool TryCreate(int clientId, out bool noSlot, out int playerIndex,
            out string failureReason);

        /// <summary>
        /// 每帧校验：世界还在、客户端化身还在、相机 Target 还是那个 ComponentPlayer。
        /// <paramref name="targetMissing"/> = 客户端离开 / 化身暂不可用（计数 clientViewTargetMissing）。
        /// </summary>
        bool Validate(int clientId, out bool targetMissing, out string reason);

        /// <summary>拆除一个客户端的视图（可重复调用；不存在时 no-op）。</summary>
        void Destroy(int clientId, string reason);

        /// <summary>拆除全部视图（配置关闭 / 离开房间 / 世界卸载 / 模块卸载）。</summary>
        void DestroyAll(string reason);
    }

    /// <summary>
    /// 自动客户端分屏的**纯逻辑**：配置闸门 / 候选筛选 / 槽位账本 / 生命周期 / 计数 / 10 秒限流日志。
    ///
    /// 需求（主机自动为加入的客户端开分屏，默认开启）在这里落地：
    ///   · 只在**主机端 + 世界内**创建；离开房间 / 世界卸载 / 配置关闭 → 立刻拆除；
    ///   · 有空槽（hostLocals + avatars + views &lt; 4）才建；没空槽只计数 + 明确
    ///     <c>clientViewNoSlot</c>，绝不崩溃、绝不重用 PlayerIndex；
    ///   · 客户端离开（化身的 PlayerKey 消失）→ 拆视图、槽位立即释放；
    ///   · **显式 mirrorcharacter 优先**：H3 已绑定到同一客户端时，不再为它建自动视图；
    ///     如果自动视图先建了、H3 后绑定，则本拍拆除自动视图（让位给 H3 的本地分屏）；
    ///   · 目标缺失只计数 + 限流日志，不拿别的客户端顶替。
    ///
    /// 本类**无静态可变状态**：时间 / 日志 / 引擎动作都由组合根注入。
    /// </summary>
    public sealed class ClientViewController
    {
        /// <summary>所有日志的最小间隔（需求口径 = 10 秒）。</summary>
        private const double LogIntervalSeconds = 10.0;

        /// <summary>创建失败 / 没空槽后同一客户端的重试间隔（秒），避免每帧刷计数与日志。</summary>
        private const double RetryIntervalSeconds = 1.0;

        private readonly IClientViewPort m_port;
        private readonly Action<string> m_info;
        private readonly Action<string> m_warn;
        private readonly Func<double> m_clock;
        private readonly Func<int, bool> m_isExplicitViewForClient;
        private ClientViewSettings m_settings;
        private bool m_configured;
        private readonly ClientViewCounters m_counters = new ClientViewCounters();
        private readonly Dictionary<string, double> m_lastLogTime =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<int, double> m_nextAttemptTime = new Dictionary<int, double>();
        private readonly List<ClientViewCandidate> m_candidates = new List<ClientViewCandidate>();
        private readonly List<int> m_views = new List<int>();

        private bool m_shutdown;

        public ClientViewController(bool enabled, string clientFilter, IClientViewPort port,
            Action<string> info, Action<string> warn, Func<double> clock,
            Func<int, bool> isExplicitViewForClient = null)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_info = info;
            m_warn = warn;
            m_clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_isExplicitViewForClient = isExplicitViewForClient;
            if (!ClientViewSettings.TryParse(enabled, clientFilter,
                    out ClientViewSettings settings, out string error))
            {
                // 配错 = 功能保持关闭 + 一条可定位的告警（绝不半开 / 绝不静默当成 all）。
                m_settings = ClientViewSettings.Disabled;
                m_configured = false;
                WarnRateLimited("config",
                    "[SuMP][client-view] 配置解析失败，功能保持关闭：" + error);
                return;
            }
            m_settings = settings;
            m_configured = settings.Enabled;
        }

        /// <summary>
        /// 世界作用域重新配置（调试开关只在 &lt;世界目录&gt;/SuMultiplayerDebug.json 里）：
        /// 关闭时立刻拆掉本端所有自动客户端分屏；解析失败 = 保持关闭并写清原因。
        /// </summary>
        public void ApplyConfiguration(bool enabled, string clientFilter)
        {
            if (m_shutdown) return;
            if (!ClientViewSettings.TryParse(enabled, clientFilter,
                    out ClientViewSettings settings, out string error))
            {
                bool hadViews = m_port.ViewCount > 0;
                m_settings = ClientViewSettings.Disabled;
                m_configured = false;
                if (hadViews) DestroyEveryView("配置解析失败");
                WarnRateLimited("config",
                    "[SuMP][client-view] 配置解析失败，功能保持关闭：" + error);
                return;
            }
            bool wasEnabled = m_configured;
            m_settings = settings;
            m_configured = settings.Enabled;
            if (wasEnabled && !m_configured && m_port.ViewCount > 0)
                DestroyEveryView("配置关闭 clientview=false");
            if (!wasEnabled && m_configured)
                InfoRateLimited("enable",
                    "[SuMP][client-view] 自动客户端分屏已启用（世界调试配置）：" +
                    (string.IsNullOrEmpty(clientFilter) ? "all" : clientFilter));
        }

        /// <summary>配置里是否启用了自动客户端分屏（**默认关闭**；只有世界调试配置显式打开才启用；配错 = false）。</summary>
        public bool IsConfigured => m_configured;

        public ClientViewSettings Settings => m_settings;

        public ClientViewCounters Counters => m_counters;

        /// <summary>
        /// 每帧驱动。参数由组合根给：世界是否加载、本端是否正在主持房间。
        /// 顺序：配置关闭 → 非主机/世界外 → 校验现有视图 → 给新加入的客户端补视图。
        /// </summary>
        public void Tick(bool inWorld, bool hosting)
        {
            if (m_shutdown) return;
            if (!m_configured)
            {
                if (m_port.ViewCount > 0) DestroyEveryView("配置 clientview=false");
                return;
            }
            bool hostingNow = inWorld && hosting;
            if (!hostingNow)
            {
                // 离开房间 / 世界卸载：需求要求"自动移除"，槽位立即释放。
                if (m_port.ViewCount > 0)
                    DestroyEveryView(inWorld ? "已离开房间（不再是主机）" : "世界已卸载");
                return;
            }

            // ① 校验现有视图：客户端离开 / 世界换了 / 显式镜像接管 → 拆除（释放槽位）。
            m_views.Clear();
            m_port.ListViews(m_views);
            for (int i = 0; i < m_views.Count; i++)
            {
                int clientId = m_views[i];
                if (m_isExplicitViewForClient != null && m_isExplicitViewForClient(clientId))
                {
                    m_port.Destroy(clientId,
                        "显式镜像（mirrorcharacter / debugmirror）已接管客户端 " + clientId +
                        " → 自动客户端视图让位（显式配置优先）");
                    m_counters.NoteRemoved(m_port.ViewCount);
                    m_nextAttemptTime.Remove(clientId);
                    InfoRateLimited("yield-" + clientId,
                        "[SuMP][client-view] clientViewRemoved：客户端 " + clientId +
                        " 已有显式镜像视图 → 自动视图让位（precedence=explicit）· " +
                        DescribeLive());
                    continue;
                }
                if (!m_port.Validate(clientId, out bool targetMissing, out string invalidReason))
                {
                    if (targetMissing) m_counters.NoteTargetMissing();
                    m_port.Destroy(clientId, invalidReason ?? "视图目标失效");
                    m_counters.NoteRemoved(m_port.ViewCount);
                    m_nextAttemptTime.Remove(clientId);
                    WarnRateLimited("invalid-" + clientId,
                        "[SuMP][client-view] clientView" +
                        (targetMissing ? "TargetMissing" : "Removed") + "：客户端 " + clientId +
                        " 的视图目标失效：" + (invalidReason ?? "原因未知") + " → 拆除 · " +
                        DescribeLive());
                    continue;
                }
                m_counters.NoteFrame(m_port.ViewCount);
            }

            // ② 给还没视图的客户端补视图（按 clientId 升序；过滤器不在其中就跳过）。
            m_candidates.Clear();
            if (!m_port.TryListCandidates(m_candidates, out string listReason))
            {
                m_counters.NoteTargetMissing();
                WarnRateLimited("list",
                    "[SuMP][client-view] clientViewTargetMissing：列不出客户端角色：" +
                    (listReason ?? "原因未知") + " → 本帧不创建 · " + DescribeLive());
                return;
            }
            bool attempted = false;
            for (int i = 0; i < m_candidates.Count; i++)
            {
                ClientViewCandidate candidate = m_candidates[i];
                if (!m_settings.Allows(candidate.ClientId)) continue;
                if (m_port.IsAlive(candidate.ClientId)) continue;
                if (m_isExplicitViewForClient != null && m_isExplicitViewForClient(candidate.ClientId))
                    continue; // 显式 mirrorcharacter 已绑定这个客户端：H3 的本地分屏就是它的视图。
                if (!candidate.Ready)
                {
                    m_counters.NoteTargetMissing();
                    WarnRateLimited("pending-" + candidate.ClientId,
                        "[SuMP][client-view] clientViewTargetMissing：客户端 " + candidate.ClientId +
                        " 已加入，但角色 " + candidate + " 的 ComponentPlayer/ComponentBody 还没就绪" +
                        " → 本帧不创建（不拿别的客户端顶替）· " + DescribeLive());
                    continue;
                }
                double now = m_clock();
                if (m_nextAttemptTime.TryGetValue(candidate.ClientId, out double next) && now < next)
                    continue;
                attempted = true;
                if (!m_port.TryCreate(candidate.ClientId, out bool noSlot, out int playerIndex,
                        out string failureReason))
                {
                    m_nextAttemptTime[candidate.ClientId] = now + RetryIntervalSeconds;
                    if (noSlot)
                    {
                        m_counters.NoteNoSlot();
                        WarnRateLimited("noslot",
                            "[SuMP][client-view] clientViewNoSlot：客户端 " + candidate.ClientId +
                            " 没有空槽（" + DescribeSlots() + "；引擎上限 4 = 本机角色 + 联机化身 + 视图）" +
                            " → 不创建、不崩溃；关掉一个本机角色 / 等客户端离开后会自动补上 · " +
                            DescribeLive());
                    }
                    else
                    {
                        m_counters.NoteFailed();
                        WarnRateLimited("create-" + candidate.ClientId,
                            "[SuMP][client-view] clientViewFailed：为客户端 " + candidate.ClientId +
                            " 创建分屏视图失败：" + (failureReason ?? "原因未知") + " · " +
                            DescribeLive());
                    }
                    continue;
                }
                m_nextAttemptTime.Remove(candidate.ClientId);
                m_counters.NoteCreated(m_port.ViewCount);
                InfoRateLimited("created-" + candidate.ClientId,
                    "[SuMP][client-view] clientViewCreated：客户端 " + candidate.ClientId +
                    "（角色 " + candidate + " · PlayerIndex=" + playerIndex +
                    " · GameWidget 已建）→ 主机分屏显示它的第一人称视角 · " + DescribeLive());
            }
            if (!attempted && m_port.ViewCount > 0) m_counters.SetSlots(m_port.ViewCount);

            // ③ 10 秒一条状态行（计数 / 槽位账本都在里面；排查时一眼看到 clientViewSlots）。
            InfoRateLimited("status",
                "[SuMP][client-view] clientViewSlots=" + m_port.ViewCount +
                " clientViewFrames=" + m_counters.Frames + " · " + DescribeLive());
        }

        /// <summary>状态行（加载日志 / 10 秒周期日志 / 报告用）。</summary>
        public string StatusLine()
        {
            string config = m_configured ? m_settings.ToString() : "off(invalid/disabled)";
            string state;
            if (!m_configured) state = "off";
            else if (m_port.ViewCount > 0) state = "on";
            else state = "waiting";
            return "client-view: config=" + config + " state=" + state + " " + DescribeLive();
        }

        public string DescribeCounters() => m_counters.Describe();

        /// <summary>组合根卸载时调用：拆掉全部视图（明确原因），随后停止驱动。</summary>
        public void Shutdown(string reason)
        {
            DestroyEveryView(reason ?? "模块卸载");
            m_shutdown = true;
        }

        private string DescribeSlots() =>
            ClientViewSlots.Describe(m_port.HostLocalPlayerCount, m_port.AvatarCount,
                m_port.ViewCount);

        /// <summary>逐个拆除全部视图：clientViewRemoved 按**视图条数**累加（不是按"一次清理"）。</summary>
        private void DestroyEveryView(string reason)
        {
            m_views.Clear();
            m_port.ListViews(m_views);
            for (int i = 0; i < m_views.Count; i++)
            {
                int clientId = m_views[i];
                m_port.Destroy(clientId, reason);
                m_counters.NoteRemoved(m_port.ViewCount);
                m_nextAttemptTime.Remove(clientId);
                InfoRateLimited("removed-" + clientId,
                    "[SuMP][client-view] clientViewRemoved：客户端 " + clientId + " · " + reason +
                    " · " + DescribeLive());
            }
            m_nextAttemptTime.Clear();
        }

        private string DescribeLive() =>
            "views=" + m_port.ViewCount + " " + DescribeSlots() + " " + m_counters.Describe();

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

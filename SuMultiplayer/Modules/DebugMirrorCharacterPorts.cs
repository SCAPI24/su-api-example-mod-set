using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 调试"镜像角色"的工作模式（配置键 <c>mirrorcharactermode</c>）。
    /// </summary>
    public enum MirrorCharacterMode
    {
        /// <summary>
        /// 默认：**用 H3 的第三个分屏视图显示客户端角色本身**。
        /// H3 的身体从世界表现里摘掉（模型不渲染 + 碰撞摘除 + updateable 停掉），
        /// 把 H3 那个 GameWidget 的相机绑到客户端角色的 ComponentPlayer（第一人称），
        /// **不复制任何状态**（背包 / 生命 / 动作都不动）。
        /// </summary>
        Replace = 0,

        /// <summary>旧行为：每拍把客户端角色的位置 / 体征 / 背包 / 动作复制到 H3 身上。</summary>
        Copy = 1
    }

    /// <summary>
    /// 模式配置的解析 / 名称（纯函数，无静态可变状态）。
    /// </summary>
    public static class MirrorCharacterModes
    {
        /// <summary>配置键名（与 <c>mirrorcharacter</c> / <c>mirrorcharacterclient</c> 同级）。</summary>
        public const string ConfigKey = "mirrorcharactermode";

        public const string ReplaceText = "replace";

        public const string CopyText = "copy";

        /// <summary>缺省模式 = <see cref="MirrorCharacterMode.Replace"/>（产品口径）。</summary>
        public static MirrorCharacterMode Default => MirrorCharacterMode.Replace;

        /// <summary>
        /// 解析配置值：空 = 默认 replace；<c>replace</c> / <c>copy</c>（大小写不敏感）。
        /// 其它文本**明确拒绝**（返回 false + 可直接定位的原因），调用方据此让功能保持关闭。
        /// </summary>
        public static bool TryParse(string value, out MirrorCharacterMode mode, out string error)
        {
            mode = Default;
            error = null;
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) return true;
            if (string.Equals(text, ReplaceText, StringComparison.OrdinalIgnoreCase))
            {
                mode = MirrorCharacterMode.Replace;
                return true;
            }
            if (string.Equals(text, CopyText, StringComparison.OrdinalIgnoreCase))
            {
                mode = MirrorCharacterMode.Copy;
                return true;
            }
            error = "mirrorcharactermode=\"" + text +
                "\" 无法识别（写法：replace（默认）= 第三分屏显示客户端角色本身、H3 的身体从世界表现里摘掉；" +
                "copy = 旧行为，把客户端状态逐拍复制到 H3 身上）";
            return false;
        }

        public static string Name(MirrorCharacterMode mode) =>
            mode == MirrorCharacterMode.Copy ? CopyText : ReplaceText;
    }

    /// <summary>
    /// 绑定计划：纯逻辑决定"绑定时要做什么"，引擎适配器逐项落地。
    /// 两个模式互斥：
    ///   · replace → <see cref="HideBody"/> + <see cref="BindView"/>，**不复制状态**（<see cref="CopyState"/> = false）；
    ///   · copy    → <see cref="CopyState"/>，不摘身体、不动相机（旧行为）。
    /// </summary>
    public readonly struct MirrorCharacterPlan
    {
        private MirrorCharacterPlan(MirrorCharacterMode mode, bool hideBody, bool bindView,
            bool copyState)
        {
            Mode = mode;
            HideBody = hideBody;
            BindView = bindView;
            CopyState = copyState;
        }

        public MirrorCharacterMode Mode { get; }

        /// <summary>
        /// 把 H3 的身体从世界表现里摘掉：模型不渲染 + 从 SubsystemBodies 摘除（不可碰撞）+
        /// 停掉会改状态 / 推挤的 updateable。**不改坐标**（坐标会被广播给客户端 / 进存档 / 上实时地图）。
        /// </summary>
        public bool HideBody { get; }

        /// <summary>把 H3 的 GameWidget 相机绑定到客户端角色的 ComponentPlayer（第一人称）。</summary>
        public bool BindView { get; }

        /// <summary>每拍复制客户端状态到 H3（仅 copy 模式）。</summary>
        public bool CopyState { get; }

        /// <summary>
        /// replace：把第三屏的 HUD 数据源桥接到客户端（蹲伏状态、快捷栏背包、生命/饱食/温度数值）。
        /// 这些组件（ComponentHealth / ComponentVitalStats）在 replace 下**保留 Update**，
        /// 由镜像每拍灌入客户端权威值让引擎自己刷新 HUD（避免两个写入方互相覆盖 → 数值抖动）。
        /// </summary>
        public bool ReplaceHudBridge => BindView && !CopyState;

        public bool IsEmpty => !HideBody && !BindView && !CopyState;

        /// <summary>replace 模式计划：摘身体 + 绑相机，不复制状态。</summary>
        public static MirrorCharacterPlan Replace =>
            new MirrorCharacterPlan(MirrorCharacterMode.Replace, hideBody: true, bindView: true,
                copyState: false);

        /// <summary>copy 模式计划（旧行为）：只复制状态。</summary>
        public static MirrorCharacterPlan Copy =>
            new MirrorCharacterPlan(MirrorCharacterMode.Copy, hideBody: false, bindView: false,
                copyState: true);

        public static MirrorCharacterPlan None => default;

        /// <summary>
        /// 目标（H3）与来源（客户端化身）都就绪时才给出计划；任一缺失返回 <see cref="None"/>
        /// （调用方据此明确失败，不做半套改动）。
        /// </summary>
        public static MirrorCharacterPlan For(MirrorCharacterMode mode, bool targetReady,
            bool sourceReady) =>
            targetReady && sourceReady
                ? (mode == MirrorCharacterMode.Copy ? Copy : Replace)
                : None;

        public string Describe()
        {
            if (IsEmpty) return "none";
            return "mode=" + MirrorCharacterModes.Name(Mode) + " hideBody=" +
                (HideBody ? "1" : "0") + " bindView=" + (BindView ? "1" : "0") +
                " copyState=" + (CopyState ? "1" : "0");
        }
    }

    /// <summary>
    /// 调试"镜像角色"的配置（**默认关闭**；产品口径 = 主机本地角色名 <c>H3</c>）。
    ///
    /// 配置键（<c>data:/Mods/SuMultiplayer.json</c>）：
    ///   · <c>mirrorcharacter</c>：主机本地角色的 <c>PlayerData.Name</c>（大小写不敏感）。
    ///     空 / false / off / no / 0 / none / disabled = 关闭；其它文本按名字处理（例 "H3"）。
    ///   · <c>mirrorcharacterclient</c>：要镜像的客户端编号。
    ///     空 / auto / first / true / on / yes / 0 = 第一个加入的客户端（最小 clientId &gt; 0）；
    ///     <c>&lt;clientId&gt;</c>（&gt; 0）= 指定连接。槽位固定 0（<c>PlayerKey(clientId, 0)</c>）；
    ///     写 <c>2#0</c> / <c>client2</c> / 负数一律**明确拒绝**（不做模糊匹配，也不静默改成 auto）。
    ///   · <c>mirrorcharactermode</c>：<c>replace</c>（默认）= 用 H3 的第三个分屏显示客户端角色本身
    ///     （H3 的身体从世界里摘掉、不复制状态）；<c>copy</c> = 旧行为（把客户端状态复制到 H3 身上）。
    ///
    /// 解析失败 = 功能保持关闭，但日志里给出可直接定位的原因（绝不半开）。
    /// 本类型是纯值，不含任何静态可变状态。
    /// </summary>
    public readonly struct MirrorCharacterSettings
    {
        private MirrorCharacterSettings(bool enabled, string characterName, bool autoClient,
            int clientId, MirrorCharacterMode mode)
        {
            Enabled = enabled;
            CharacterName = characterName ?? string.Empty;
            AutoClient = autoClient;
            ClientId = clientId;
            Mode = mode;
        }

        /// <summary>是否真正启用（名字非空且 client / mode 配置合法）。</summary>
        public bool Enabled { get; }

        /// <summary>主机本地角色名（已 Trim；大小写不敏感匹配）。</summary>
        public string CharacterName { get; }

        /// <summary>true = 自动选第一个加入的客户端；false = 用 <see cref="ClientId"/>。</summary>
        public bool AutoClient { get; }

        /// <summary>指定的客户端编号（<see cref="AutoClient"/> 时为 0）。</summary>
        public int ClientId { get; }

        /// <summary>工作模式（默认 <see cref="MirrorCharacterMode.Replace"/>）。</summary>
        public MirrorCharacterMode Mode { get; }

        public static MirrorCharacterSettings Disabled =>
            new MirrorCharacterSettings(false, string.Empty, true, 0,
                MirrorCharacterModes.Default);

        /// <summary>两个键的旧重载：<c>mirrorcharactermode</c> 走缺省（replace）。</summary>
        public static bool TryParse(string characterValue, string clientValue,
            out MirrorCharacterSettings settings, out string error) =>
            TryParse(characterValue, clientValue, null, out settings, out error);

        /// <summary>
        /// 解析三个配置键。返回 false 表示**配置写错**（<paramref name="settings"/> 保持关闭）；
        /// 返回 true 且 <c>settings.Enabled == false</c> 表示"显式关闭"。
        /// </summary>
        public static bool TryParse(string characterValue, string clientValue, string modeValue,
            out MirrorCharacterSettings settings, out string error)
        {
            settings = Disabled;
            error = null;
            string name = (characterValue ?? string.Empty).Trim();
            if (IsOffWord(name))
                return true; // 缺省 / 显式关闭：client / mode 值不再校验（关闭就是关闭）
            if (string.Equals(name, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "yes", StringComparison.OrdinalIgnoreCase))
            {
                // 这个键要的是"主机本地角色名"，不是开关。写 true 的人通常以为它是布尔开关；
                // 明确拒绝，避免把某个恰好叫 true 的角色当成镜像目标（也避免功能半开）。
                error = "mirrorcharacter=\"" + name +
                    "\" 不是角色名：请写主机本地角色的名字（例 \"H3\"），空 = 关闭";
                return false;
            }

            string clientText = (clientValue ?? string.Empty).Trim();
            bool autoClient;
            int clientId = 0;
            if (clientText.Length == 0 || IsAutoWord(clientText))
            {
                autoClient = true;
            }
            else if (int.TryParse(clientText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                         out clientId))
            {
                if (clientId <= 0)
                {
                    error = "mirrorcharacterclient=\"" + clientText +
                        "\" 非法：0 表示关闭/自动，clientId 0 是主机自己；要指定远端请写 >0 的客户端编号或留空（第一个加入的客户端）";
                    return false;
                }
                autoClient = false;
            }
            else
            {
                error = "mirrorcharacterclient=\"" + clientText +
                    "\" 无法识别（写法：留空 / auto / first / <clientId>，clientId 必须 > 0；槽位固定 0）";
                return false;
            }
            if (!MirrorCharacterModes.TryParse(modeValue, out MirrorCharacterMode mode,
                    out string modeError))
            {
                error = modeError;
                return false;
            }
            settings = new MirrorCharacterSettings(true, name, autoClient,
                autoClient ? 0 : clientId, mode);
            return true;
        }

        private static bool IsOffWord(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            return string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "disabled", StringComparison.OrdinalIgnoreCase) ||
                   text == "0";
        }

        private static bool IsAutoWord(string text) =>
            string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "first", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
            text == "0";

        /// <summary>日志 / 状态行口径：<c>off</c> 或 <c>"H3" client=auto|2 mode=replace|copy</c>。</summary>
        public override string ToString()
        {
            if (!Enabled) return "off";
            return "\"" + CharacterName + "\" client=" +
                (AutoClient ? "auto" : ClientId.ToString(CultureInfo.InvariantCulture)) +
                " mode=" + MirrorCharacterModes.Name(Mode);
        }
    }

    /// <summary>
    /// 每拍要复制哪些类别。纯逻辑只决定"哪些类别可用"，引擎适配器负责逐项落地。
    /// 顺序固定：body → vitals → inventory → actions（inventory 必须先于 actions，
    /// 否则手持物会有一帧旧值；见 <see cref="MirrorCharacterController"/> 的复制调用）。
    /// </summary>
    public readonly struct MirrorCopyPlan
    {
        private readonly bool m_body;
        private readonly bool m_vitals;
        private readonly bool m_inventory;
        private readonly bool m_actions;

        public MirrorCopyPlan(bool body, bool vitals, bool inventory, bool actions)
        {
            m_body = body;
            m_vitals = vitals;
            m_inventory = inventory;
            m_actions = actions;
        }

        public bool Body => m_body;
        public bool Vitals => m_vitals;
        public bool Inventory => m_inventory;
        public bool Actions => m_actions;

        public static MirrorCopyPlan Full => new MirrorCopyPlan(true, true, true, true);

        public static MirrorCopyPlan None => default;

        public bool IsEmpty => !m_body && !m_vitals && !m_inventory && !m_actions;

        /// <summary>
        /// 只有目标（H3）与来源（客户端化身）都就绪时才返回 <see cref="Full"/>；
        /// 任一缺失返回 <see cref="None"/>（调用方据此明确失败，不复制半套状态）。
        /// </summary>
        public static MirrorCopyPlan Compute(bool targetReady, bool sourceReady) =>
            targetReady && sourceReady ? Full : None;

        public string Describe()
        {
            var text = new StringBuilder();
            if (m_body) text.Append("body,");
            if (m_vitals) text.Append("vitals,");
            if (m_inventory) text.Append("inventory,");
            if (m_actions) text.Append("actions,");
            if (text.Length == 0) return "none";
            text.Length--;
            return text.ToString();
        }
    }

    /// <summary>
    /// 一拍复制的结果（引擎适配器填写）。只用于计数 / 日志口径，不参与任何权威逻辑。
    /// </summary>
    public readonly struct MirrorCopyOutcome
    {
        public MirrorCopyOutcome(bool bodyCopied, bool vitalsCopied, bool inventoryCopied,
            bool actionsCopied, bool collisionDisabled, bool riding, int inventorySlotsChanged)
        {
            BodyCopied = bodyCopied;
            VitalsCopied = vitalsCopied;
            InventoryCopied = inventoryCopied;
            ActionsCopied = actionsCopied;
            CollisionDisabled = collisionDisabled;
            Riding = riding;
            InventorySlotsChanged = inventorySlotsChanged;
        }

        public bool BodyCopied { get; }
        public bool VitalsCopied { get; }
        public bool InventoryCopied { get; }
        public bool ActionsCopied { get; }

        /// <summary>绑定期间是否已把 H3 的 AABB 碰撞从引擎里摘掉（<c>mirrorCollisionDisabled</c>）。</summary>
        public bool CollisionDisabled { get; }

        /// <summary>来源是否处于骑乘状态（仅状态行 / 日志口径；位置/朝向已由 body 复制跟随）。</summary>
        public bool Riding { get; }

        /// <summary>本拍实际改写的背包槽数（0 = 内容本来一致）。</summary>
        public int InventorySlotsChanged { get; }
    }

    /// <summary>
    /// 绑定的结果（引擎适配器填写）。只用于计数 / 日志口径，不参与任何权威逻辑。
    /// </summary>
    public readonly struct MirrorBindOutcome
    {
        public MirrorBindOutcome(MirrorCharacterMode mode, bool viewBound, bool bodyHidden,
            bool collisionDisabled, bool stateCopied)
        {
            Mode = mode;
            ViewBound = viewBound;
            BodyHidden = bodyHidden;
            CollisionDisabled = collisionDisabled;
            StateCopied = stateCopied;
        }

        public MirrorCharacterMode Mode { get; }

        /// <summary>相机是否已绑到客户端角色的 ComponentPlayer（replace 模式）。</summary>
        public bool ViewBound { get; }

        /// <summary>H3 的模型是否已不再渲染 / 身体是否已摘出碰撞（replace 模式）。</summary>
        public bool BodyHidden { get; }

        /// <summary>绑定期间是否已把 H3 的 AABB 碰撞从引擎里摘掉（两种模式都做）。</summary>
        public bool CollisionDisabled { get; }

        /// <summary>绑定时是否顺手复制了一拍（仅 copy 模式；replace 模式恒 false）。</summary>
        public bool StateCopied { get; }

        public string Describe() =>
            "mode=" + MirrorCharacterModes.Name(Mode) + " viewBound=" + (ViewBound ? "1" : "0") +
            " bodyHidden=" + (BodyHidden ? "1" : "0") + " collisionDisabled=" +
            (CollisionDisabled ? "1" : "0") + " stateCopied=" + (StateCopied ? "1" : "0");
    }

    /// <summary>
    /// 解绑 / 写回快照的结果（引擎适配器填写）。只用于计数 / 日志口径。
    /// </summary>
    public readonly struct MirrorRestoreOutcome
    {
        public MirrorRestoreOutcome(bool restored, bool viewRestored, bool bodyRestored)
        {
            Restored = restored;
            ViewRestored = viewRestored;
            BodyRestored = bodyRestored;
        }

        /// <summary>绑定前的引擎字段是否真的写回了（位置 / 输入 / updateable 等）。</summary>
        public bool Restored { get; }

        /// <summary>相机是否已回到 H3 自己（replace 模式）。</summary>
        public bool ViewRestored { get; }

        /// <summary>H3 的模型渲染 / 碰撞是否已恢复（replace 模式）。</summary>
        public bool BodyRestored { get; }

        public string Describe() =>
            "restored=" + (Restored ? "1" : "0") + " viewRestored=" +
            (ViewRestored ? "1" : "0") + " bodyRestored=" + (BodyRestored ? "1" : "0");
    }

    /// <summary>
    /// 调试镜像角色的**引擎端口**。纯逻辑（配置闸门 / 生命周期 / 计数 / 限流日志）只依赖本接口；
    /// 引擎侧实现 = <c>Engine/MirrorCharacterEngine.cs</c>（只读客户端化身、只写主机本地角色 H3）。
    ///
    /// 硬约束（实现方必须遵守）：
    ///   · 只从 <c>NetworkAvatarSpawner.TryGet(PlayerKey(clientId, 0))</c> **读**客户端化身；
    ///   · 绝不写客户端的角色记录 / 身份 / 统计（没有 "写回客户端" 的入口）；
    ///   · 绑定前必须抓 H3 的完整快照，解绑时逐项写回（replace 模式含相机 / 模型渲染）；
    ///   · 引擎 API 缺失时明确失败并给出原因（不静默降级）。
    /// </summary>
    public interface IMirrorCharacterPort
    {
        /// <summary>当前是否绑定了一个镜像角色（快照 + 引擎改动都已生效）。</summary>
        bool IsBound { get; }

        /// <summary>当前绑定的客户端编号（未绑定 = -1）。</summary>
        int BoundClientId { get; }

        /// <summary>当前绑定的主机本地角色名（未绑定 = 空）。</summary>
        string BoundCharacterName { get; }

        /// <summary>
        /// 第三分屏客户端反馈镜像的计数器一行（replace 模式；未接入时全 0）。
        /// 键：<c>viewDamageFlash</c> / <c>viewHealthBarFlash</c> / <c>viewHudNumbers</c> /
        /// <c>viewDeathOverlay</c> / <c>viewFeedbackFailures</c> / <c>viewFeedbackSkipped</c>
        /// （+ <c>viewSkippedReason</c> / <c>viewFailureReason</c> + <c>viewIceWrites</c> /
        /// <c>viewRespawnClears</c> / <c>viewFrames</c>）。状态行与 10 秒限流日志用它。
        /// </summary>
        string DescribeViewFeedback();

        /// <summary>
        /// 列出主机上的**本机角色名**（多角色感知：与 <c>NetworkAvatarSpawner.TryCollectLocalPlayers</c>
        /// 完全同一口径，按 PlayerIndex 升序）。失败时 reason 写明原因。
        /// </summary>
        bool TryListLocalCharacterNames(List<string> names, out string reason);

        /// <summary>
        /// 列出当前有联机化身的**客户端编号**（clientId &gt; 0，升序；主机 0 不在此列）。
        /// 没有客户端时返回 true + 空列表（不是错误，调用方只等待）。
        /// </summary>
        bool TryListClientIds(List<int> clientIds, out string reason);

        /// <summary>
        /// 绑定：按名字（大小写不敏感）找唯一的主机本机角色，抓快照，然后按
        /// <paramref name="plan"/> 落地（replace = 摘身体 + 绑相机、不复制；copy = 摘碰撞/输入 + 立刻复制一拍）。
        /// 失败返回 false（已回滚到快照）。
        /// </summary>
        bool TryBind(string characterName, int clientId, MirrorCharacterPlan plan,
            out MirrorBindOutcome outcome, out string reason);

        /// <summary>每拍校验：世界还在、H3 还在、客户端化身还在；replace 模式还会把相机重新对到最新目标。</summary>
        bool ValidateBound(out string reason);

        /// <summary>按 <paramref name="plan"/> 复制一拍（copy 模式专用）。失败返回 false 且 reason 可直接定位。</summary>
        bool TryCopy(MirrorCopyPlan plan, out MirrorCopyOutcome outcome, out string reason);

        /// <summary>
        /// 解绑并写回绑定前快照（可重复调用）。返回 false = 解绑/恢复失败（原因在 reason 里）。
        /// </summary>
        bool TryRestore(out MirrorRestoreOutcome outcome, out string reason);
    }

    /// <summary>
    /// 调试镜像角色计数器（实例持有，无静态状态）。字段名与需求逐项对应：
    ///   mirrorMode（在状态行 / 日志里，不随拍累加）、
    ///   mirrorBind / mirrorUnbind / mirrorNotFound / mirrorMultipleMatches /
    ///   mirrorViewBound / mirrorViewRestored / mirrorBodyParked / mirrorParkedRestored /
    ///   mirrorPosCopy / mirrorVitalsCopy / mirrorInvCopy / mirrorActionCopy /
    ///   mirrorFailures / mirrorCollisionDisabled / mirrorRestored。
    /// 计数**每拍如实累加**；日志按 10 秒限流（见控制器）。
    /// </summary>
    public sealed class MirrorCharacterCounters
    {
        public long Bind { get; private set; }
        public long Unbind { get; private set; }
        public long NotFound { get; private set; }
        public long MultipleMatches { get; private set; }
        public long PosCopy { get; private set; }
        public long VitalsCopy { get; private set; }
        public long InvCopy { get; private set; }
        public long ActionCopy { get; private set; }
        public long Failures { get; private set; }
        public long CollisionDisabled { get; private set; }
        public long Restored { get; private set; }

        /// <summary>replace 模式：相机成功绑到客户端角色的次数。</summary>
        public long ViewBound { get; private set; }

        /// <summary>replace 模式：相机成功回到 H3 自己的次数（解绑）。</summary>
        public long ViewRestored { get; private set; }

        /// <summary>replace 模式：H3 的身体被摘出世界表现（模型不渲染 + 不可碰撞）的次数。</summary>
        public long BodyParked { get; private set; }

        /// <summary>replace 模式：H3 的身体恢复渲染 / 碰撞的次数（解绑）。</summary>
        public long ParkedRestored { get; private set; }

        public void NoteBind(MirrorBindOutcome outcome)
        {
            Bind++;
            if (outcome.CollisionDisabled) CollisionDisabled++;
            if (outcome.BodyHidden) BodyParked++;
            if (outcome.ViewBound) ViewBound++;
        }

        public void NoteUnbind(MirrorRestoreOutcome outcome)
        {
            Unbind++;
            if (outcome.ViewRestored) ViewRestored++;
            if (outcome.BodyRestored) ParkedRestored++;
        }

        public void NoteNotFound() => NotFound++;

        public void NoteMultipleMatches() => MultipleMatches++;

        public void NoteFailure() => Failures++;

        public void NoteRestored() => Restored++;

        public void NoteCopy(MirrorCopyOutcome outcome)
        {
            if (outcome.BodyCopied) PosCopy++;
            if (outcome.VitalsCopied) VitalsCopy++;
            if (outcome.InventoryCopied) InvCopy++;
            if (outcome.ActionsCopied) ActionCopy++;
        }

        public string Describe() =>
            "bind=" + Bind + " unbind=" + Unbind + " notFound=" + NotFound +
            " multipleMatches=" + MultipleMatches + " viewBound=" + ViewBound +
            " viewRestored=" + ViewRestored + " bodyParked=" + BodyParked +
            " parkedRestored=" + ParkedRestored + " posCopy=" + PosCopy +
            " vitalsCopy=" + VitalsCopy + " invCopy=" + InvCopy +
            " actionCopy=" + ActionCopy + " failures=" + Failures +
            " collisionDisabled=" + CollisionDisabled + " restored=" + Restored;
    }

    /// <summary>
    /// 调试镜像角色的**纯逻辑**（无 Game 依赖，可在自检里直接编译）：
    /// 配置闸门（名字 / 客户端 / 模式三个键）/ 名字查找（大小写不敏感、多重匹配拒绝）/
    /// 目标选择（首个客户端或指定 clientId）/ 绑定计划（replace = 摘身体 + 绑相机、不复制状态；
    /// copy = 每拍复制）/ 生命周期（主机 + 世界内自动绑定，离开房间 / 卸载 / 客户端离开 /
    /// 配置关闭自动解绑）/ 10 秒限流日志 / 计数。
    ///
    /// 明确的"不做"：
    ///   · 名字找不到：只计数 + 限流日志，按角色列表变化或 1 秒重试，不拿别的角色顶替；
    ///   · 名字匹配到多个：<c>mirrorMultipleMatches</c> 并拒绝绑定（不猜槽位）；
    ///   · 指定的 clientId 不在线：只等待（日志写清当前客户端列表），不改成 auto；
    ///   · copy 模式复制失败：<c>mirrorFailures</c> + 解绑恢复（绝不让 H3 停在半套状态）；
    ///   · replace 模式：**绝不复制**背包 / 生命 / 动作（那是 copy 模式的行为）。
    ///
    /// 本类**无静态可变状态**：时间 / 日志 / 引擎动作都由组合根注入。
    /// </summary>
    public sealed class MirrorCharacterController
    {
        /// <summary>名字列表 / 客户端列表都没变化时的重试间隔（秒）。</summary>
        private const double RetryIntervalSeconds = 1.0;

        /// <summary>所有日志的最小间隔（毫秒口径的需求 = 10 秒）。</summary>
        private const double LogIntervalSeconds = 10.0;

        private readonly IMirrorCharacterPort m_port;
        private readonly Action<string> m_info;
        private readonly Action<string> m_warn;
        private readonly Func<double> m_clock;
        private MirrorCharacterSettings m_settings;
        private bool m_configured;
        private string m_configError;
        private readonly MirrorCharacterCounters m_counters = new MirrorCharacterCounters();
        private readonly Dictionary<string, double> m_lastLogTime =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly List<string> m_localNames = new List<string>();
        private readonly List<int> m_clientIds = new List<int>();

        private string m_lastLocalSignature;
        private string m_lastClientSignature;
        private double m_lastAttemptTime = double.NegativeInfinity;
        private bool m_shutdown;

        public MirrorCharacterController(string characterValue, string clientValue,
            IMirrorCharacterPort port, Action<string> info, Action<string> warn,
            Func<double> clock)
            : this(characterValue, clientValue, null, port, info, warn, clock)
        {
        }

        public MirrorCharacterController(string characterValue, string clientValue,
            string modeValue, IMirrorCharacterPort port, Action<string> info, Action<string> warn,
            Func<double> clock)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_info = info;
            m_warn = warn;
            m_clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (!MirrorCharacterSettings.TryParse(characterValue, clientValue, modeValue,
                    out MirrorCharacterSettings settings, out string error))
            {
                // 配置写错：功能保持关闭，但日志里给出可直接定位的原因（不静默）。
                m_settings = MirrorCharacterSettings.Disabled;
                m_configured = false;
                m_configError = error;
                WarnRateLimited("config",
                    "[SuMP][mirror-character] 配置解析失败，功能保持关闭：" + error);
                return;
            }
            m_settings = settings;
            m_configured = settings.Enabled;
        }

        /// <summary>配置里是否显式启用了镜像角色（默认 false）。</summary>
        public bool IsConfigured => m_configured;

        /// <summary>配置写错时保留的原因（未写错 = null；状态行 / 报告用）。</summary>
        public string ConfigError => m_configError;

        /// <summary>
        /// 应用**世界作用域**的调试配置（进世界 / 换世界时由组合根调用）。
        /// 旧配置若已绑定会先解绑（换角色名 / 换模式 / 关闭都必须先还原宿主）。
        /// </summary>
        public void ApplyConfiguration(string characterValue, string clientValue,
            string modeValue)
        {
            if (!MirrorCharacterSettings.TryParse(characterValue, clientValue, modeValue,
                    out MirrorCharacterSettings settings, out string error))
            {
                if (m_port.IsBound) Unbind("世界调试配置解析失败");
                m_settings = MirrorCharacterSettings.Disabled;
                m_configured = false;
                m_configError = error;
                WarnRateLimited("config",
                    "[SuMP][mirror-character] 世界调试配置解析失败，功能保持关闭：" + error);
                ResetAttemptGate();
                return;
            }
            bool wasBound = m_port.IsBound;
            bool changed = !m_configured ||
                !string.Equals(m_settings.CharacterName, settings.CharacterName,
                    StringComparison.Ordinal) ||
                m_settings.Mode != settings.Mode;
            if (wasBound && (!settings.Enabled || changed)) Unbind("世界调试配置变化");
            m_settings = settings;
            m_configured = settings.Enabled;
            m_configError = null;
            ResetAttemptGate();
        }

        public MirrorCharacterCounters Counters => m_counters;

        public MirrorCharacterSettings Settings => m_settings;

        /// <summary>
        /// 本端按配置要用的绑定计划（replace = 摘身体 + 绑相机；copy = 每拍复制）。
        /// 未启用时是 <see cref="MirrorCharacterPlan.None"/>。
        /// </summary>
        public MirrorCharacterPlan Plan => m_configured
            ? MirrorCharacterPlan.For(m_settings.Mode, targetReady: true, sourceReady: true)
            : MirrorCharacterPlan.None;

        /// <summary>当前是否已绑定（直接读引擎端口；纯逻辑不维护第二份绑定状态）。</summary>
        public bool IsBound => m_port.IsBound;

        /// <summary>当前绑定的客户端编号（未绑定 = -1；自动客户端分屏的"显式镜像优先"读它）。</summary>
        public int BoundClientId => m_port.IsBound ? m_port.BoundClientId : -1;

        /// <summary>当前绑定的主机本地角色名（未绑定 = 空）。</summary>
        public string BoundCharacterName => m_port.IsBound ? m_port.BoundCharacterName : string.Empty;

        /// <summary>
        /// 这个客户端是否已被显式镜像接管（replace = H3 的第三个分屏就是它的视图；
        /// copy = H3 是它的替身）。自动客户端分屏据此让位，避免同一客户端出现两个视图。
        /// </summary>
        public bool IsBoundToClient(int clientId) =>
            clientId > 0 && m_port.IsBound && m_port.BoundClientId == clientId;

        /// <summary>
        /// 每帧驱动。参数由组合根给：世界是否加载、本端是否正在主持房间。
        /// 顺序：→ 世界外/非主机：先解绑；→ 已绑定：校验（+ copy 模式每拍复制 / replace 模式只校验相机）；
        /// → 未绑定：按配置查找并绑定。配置关闭时**一个引擎调用都不会发生**（正常玩法零影响）。
        /// </summary>
        public void Tick(bool inWorld, bool hosting)
        {
            if (m_shutdown) return;
            if (!m_configured)
            {
                // 世界调试配置关闭：已绑定则先还原宿主（否则身体保持隐藏 / 相机停在旧目标）。
                if (m_port.IsBound) Unbind("世界调试配置关闭");
                ResetAttemptGate();
                return;
            }
            bool hostingNow = inWorld && hosting;
            if (!hostingNow)
            {
                if (m_port.IsBound)
                    Unbind(inWorld ? "已离开房间（不再是主机）" : "世界已卸载");
                // 下次进房 / 客户端列表变化时立即重试（不沿用上一次的签名）。
                ResetAttemptGate();
                return;
            }
            if (m_port.IsBound)
            {
                if (!m_port.ValidateBound(out string invalidReason))
                {
                    WarnRateLimited("invalid",
                        "[SuMP][mirror-character] 绑定失效：mirrorMode=" +
                        MirrorCharacterModes.Name(m_settings.Mode) + " · " + invalidReason +
                        " → 自动解绑并等待目标回来 · " + m_counters.Describe());
                    Unbind("绑定失效：" + invalidReason);
                    return;
                }
                if (m_settings.Mode == MirrorCharacterMode.Copy)
                {
                    if (!m_port.TryCopy(MirrorCopyPlan.Full, out MirrorCopyOutcome outcome,
                            out string copyReason))
                    {
                        m_counters.NoteFailure();
                        WarnRateLimited("copy-fail",
                            "[SuMP][mirror-character] mirrorFailures=" + m_counters.Failures +
                            "：每拍复制失败（mirrorMode=copy）：" + copyReason +
                            " → 解绑并写回快照 · " + m_counters.Describe());
                        // 复制失败通常是来源组件暂时不完整；不要每帧重建绑定（下一拍起按 1 秒重试）。
                        Unbind("复制失败：" + copyReason, retryImmediately: false);
                        return;
                    }
                    m_counters.NoteCopy(outcome);
                    InfoRateLimited("copy",
                        "[SuMP][mirror-character] mirrorMode=copy mirrorPosCopy=" +
                        m_counters.PosCopy + " mirrorVitalsCopy=" + m_counters.VitalsCopy +
                        " mirrorInvCopy=" + m_counters.InvCopy +
                        " mirrorActionCopy=" + m_counters.ActionCopy +
                        " slotsChanged=" + outcome.InventorySlotsChanged +
                        " riding=" + outcome.Riding + " · " + m_counters.Describe());
                    return;
                }
                // replace：不复制任何状态，只确认相机还绑在客户端角色上（ValidateBound 已负责重绑），
                // 并把第三分屏客户端反馈镜像的计数一并输出（状态行口径）。
                InfoRateLimited("view",
                    "[SuMP][mirror-character] mirrorMode=replace viewBound=" +
                    m_counters.ViewBound + " bodyParked=" + m_counters.BodyParked +
                    "：第三分屏 = 客户端 " + m_port.BoundClientId + " 的第一人称" +
                    "（H3「" + m_port.BoundCharacterName + "」的身体不在世界里渲染 / 不可碰撞）· " +
                    m_counters.Describe() + " · " + m_port.DescribeViewFeedback());
                return;
            }
            AttemptBindIfDue();
        }

        /// <summary>
        /// 组合根卸载时调用：已绑定就解绑并写回快照（明确原因），随后停止驱动。
        /// </summary>
        public void Shutdown(string reason)
        {
            if (m_port.IsBound) Unbind(reason ?? "模块卸载");
            m_shutdown = true;
        }

        /// <summary>状态行（加载日志 / 5 秒周期日志 / 设备排查用）。</summary>
        public string StatusLine()
        {
            string config = m_configured
                ? m_settings.ToString()
                : (m_configError == null ? "off(默认)" : "invalid(" + m_configError + ")");
            string state;
            if (!m_configured) state = "off";
            else if (m_port.IsBound)
                state = "bound(\"" + m_port.BoundCharacterName + "\"←client " +
                    m_port.BoundClientId + ")";
            else state = "waiting";
            return "mirror-character: config=" + config + " mirrorMode=" +
                MirrorCharacterModes.Name(m_settings.Mode) + " state=" + state + " " +
                m_counters.Describe() + " " + m_port.DescribeViewFeedback();
        }

        /// <summary>HUD / 日志用的计数摘要。</summary>
        public string DescribeCounters() => m_counters.Describe();

        private void AttemptBindIfDue()
        {
            m_localNames.Clear();
            if (!m_port.TryListLocalCharacterNames(m_localNames, out string localReason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("list-local",
                    "[SuMP][mirror-character] mirrorFailures=" + m_counters.Failures +
                    "：列不出主机本机角色：" + (localReason ?? "原因未知"));
                return;
            }
            m_clientIds.Clear();
            if (!m_port.TryListClientIds(m_clientIds, out string clientReason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("list-client",
                    "[SuMP][mirror-character] mirrorFailures=" + m_counters.Failures +
                    "：列不出已加入的客户端：" + (clientReason ?? "原因未知"));
                return;
            }
            string localSignature = Signature(m_localNames);
            string clientSignature = Signature(m_clientIds);
            double now = m_clock();
            bool changed = !string.Equals(localSignature, m_lastLocalSignature, StringComparison.Ordinal) ||
                !string.Equals(clientSignature, m_lastClientSignature, StringComparison.Ordinal);
            if (!changed && now - m_lastAttemptTime < RetryIntervalSeconds) return;
            m_lastAttemptTime = now;
            m_lastLocalSignature = localSignature;
            m_lastClientSignature = clientSignature;

            int matchIndex = -1;
            int matches = 0;
            for (int i = 0; i < m_localNames.Count; i++)
            {
                if (!string.Equals(m_localNames[i] ?? string.Empty, m_settings.CharacterName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                matches++;
                if (matchIndex < 0) matchIndex = i;
            }
            if (matches == 0)
            {
                m_counters.NoteNotFound();
                WarnRateLimited("notfound",
                    "[SuMP][mirror-character] mirrorNotFound=" + m_counters.NotFound +
                    "：主机本机角色里没有名为「" + m_settings.CharacterName + "」的角色（当前：[" +
                    Join(m_localNames) + "]）→ 等角色列表变化 / 1 秒后重试");
                return;
            }
            if (matches > 1)
            {
                m_counters.NoteMultipleMatches();
                WarnRateLimited("multiple",
                    "[SuMP][mirror-character] mirrorMultipleMatches=" +
                    m_counters.MultipleMatches + "：有 " + matches + " 个本机角色都叫「" +
                    m_settings.CharacterName + "」（当前：[" + Join(m_localNames) +
                    "]）→ 拒绝绑定（不猜是哪一个）");
                return;
            }

            int clientId = ResolveClientId();
            if (clientId <= 0)
            {
                InfoRateLimited("waiting-client",
                    m_settings.AutoClient
                        ? "[SuMP][mirror-character] 已找到本机角色「" +
                          m_settings.CharacterName + "」，但当前没有已加入的客户端 → 等待客户端加入"
                        : "[SuMP][mirror-character] 已找到本机角色「" +
                          m_settings.CharacterName + "」，但 mirrorcharacterclient 指定的 clientId=" +
                          m_settings.ClientId + " 不在线（当前客户端：[" + Join(m_clientIds) +
                          "]）→ 等待它加入（绝不改成 auto）");
                return;
            }
            MirrorCharacterPlan plan = MirrorCharacterPlan.For(m_settings.Mode,
                targetReady: true, sourceReady: true);
            if (!m_port.TryBind(m_localNames[matchIndex], clientId, plan,
                    out MirrorBindOutcome outcome, out string reason))
            {
                m_counters.NoteFailure();
                WarnRateLimited("bind-fail",
                    "[SuMP][mirror-character] mirrorFailures=" + m_counters.Failures +
                    "：绑定「" + m_localNames[matchIndex] + "」← client " + clientId +
                    " 失败（mirrorMode=" + MirrorCharacterModes.Name(m_settings.Mode) +
                    " · " + plan.Describe() + "）：" + reason);
                return;
            }
            m_counters.NoteBind(outcome);
            InfoRateLimited("bind",
                "[SuMP][mirror-character] mirrorBind：本地角色「" + m_localNames[matchIndex] +
                "」← 客户端 " + clientId + "（槽位 0）· mirrorMode=" +
                MirrorCharacterModes.Name(m_settings.Mode) + " · " + outcome.Describe() +
                " · plan=" + plan.Describe() + " · " + m_counters.Describe() +
                (m_settings.Mode == MirrorCharacterMode.Replace
                    ? "（第三分屏 = 客户端第一人称；H3 模型不再渲染、碰撞/updateable 已摘除；" +
                      "替换模式不复制背包/生命/动作）"
                    : "（copy：每拍把客户端状态复制到 H3 身上）"));
        }

        private int ResolveClientId()
        {
            if (m_settings.AutoClient)
            {
                int best = 0;
                for (int i = 0; i < m_clientIds.Count; i++)
                {
                    int id = m_clientIds[i];
                    if (id > 0 && (best == 0 || id < best)) best = id;
                }
                return best;
            }
            for (int i = 0; i < m_clientIds.Count; i++)
                if (m_clientIds[i] == m_settings.ClientId) return m_settings.ClientId;
            return 0;
        }

        private void Unbind(string reason, bool retryImmediately = true)
        {
            MirrorRestoreOutcome outcome;
            string restoreReason;
            bool ok;
            try
            {
                ok = m_port.TryRestore(out outcome, out restoreReason);
            }
            catch (Exception e)
            {
                ok = false;
                outcome = default;
                restoreReason = e.GetType().Name + " " + e.Message;
            }
            if (!ok)
            {
                m_counters.NoteFailure();
                WarnRateLimited("unbind-fail",
                    "[SuMP][mirror-character] mirrorFailures=" + m_counters.Failures +
                    "：解绑 / 写回快照失败（mirrorMode=" +
                    MirrorCharacterModes.Name(m_settings.Mode) + "）：" +
                    (restoreReason ?? "原因未知") +
                    "（H3 可能停在半套镜像状态，下一次解绑会重试）");
                return;
            }
            m_counters.NoteUnbind(outcome);
            if (outcome.Restored) m_counters.NoteRestored();
            else m_counters.NoteFailure();
            InfoRateLimited("unbind",
                "[SuMP][mirror-character] mirrorUnbind：" + reason + " · mirrorMode=" +
                MirrorCharacterModes.Name(m_settings.Mode) + " · restored=" + outcome.Restored +
                " viewRestored=" + outcome.ViewRestored + " bodyRestored=" +
                outcome.BodyRestored + " · " + m_counters.Describe());
            if (retryImmediately) ResetAttemptGate();
        }

        /// <summary>下一次 Tick 立即重新评估绑定（用于"目标离开 / 重新进房"）。</summary>
        private void ResetAttemptGate()
        {
            m_lastLocalSignature = null;
            m_lastClientSignature = null;
            m_lastAttemptTime = double.NegativeInfinity;
        }

        private static string Signature(List<string> values)
        {
            if (values.Count == 0) return "0";
            var text = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) text.Append('\u001f');
                text.Append(values[i] ?? string.Empty);
            }
            return text.ToString();
        }

        private static string Signature(List<int> values)
        {
            if (values.Count == 0) return "0";
            var text = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        private static string Join(List<string> values)
        {
            if (values.Count == 0) return "none";
            var text = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(values[i] ?? string.Empty);
            }
            return text.ToString();
        }

        private static string Join(List<int> values)
        {
            if (values.Count == 0) return "none";
            var text = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(values[i].ToString(CultureInfo.InvariantCulture));
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

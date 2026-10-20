using System;
using System.Collections.Generic;
using System.Globalization;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 调试镜像的**目标**：一个远端联机角色的稳定玩家键 <c>(clientId, slot)</c>。
    ///
    /// 语义（与 <c>Players/PlayerKey.cs</c> 的 <c>(clientId, slot)</c> 完全相同，本类型只是
    /// 加了一个"自动"哨兵值，因为配置键需要表达 <c>true</c> = "随便挑第一个远端角色"）：
    ///   · <see cref="AutoClientId"/> = 自动：主机上第一个有角色的远端连接、槽位 0；
    ///   · <c>clientId &gt; 0</c> + <c>0..3</c> = 指定某个远端的某个本机角色槽位（未来多角色）。
    /// 主机自己的 clientId = 0 不是合法的镜像目标（主机角色本来就在本机分屏里）。
    /// </summary>
    public readonly struct DebugMirrorTarget : IEquatable<DebugMirrorTarget>
    {
        /// <summary>"自动"哨兵：配置里写 <c>true</c> 时用它。</summary>
        public const int AutoClientId = -1;

        /// <summary>一个设备最多 4 个本机角色（引擎硬上限，与 PlayerKey.SlotCount 一致）。</summary>
        public const int SlotCount = 4;

        public const int MaxSlot = SlotCount - 1;

        public DebugMirrorTarget(int clientId, int slot)
        {
            ClientId = clientId;
            Slot = slot;
        }

        public int ClientId { get; }

        public int Slot { get; }

        public bool IsAuto => ClientId == AutoClientId;

        /// <summary>已解析的具体目标（不是"自动"、clientId &gt; 0、槽位合法）。</summary>
        public bool IsResolved => ClientId > 0 && Slot >= 0 && Slot <= MaxSlot;

        public static DebugMirrorTarget Auto => new DebugMirrorTarget(AutoClientId, 0);

        /// <summary>
        /// 解析配置键 <c>debugmirrorclient</c>。默认关闭：空串 / false / off / 0 都表示关闭。
        ///
        /// 接受的写法（显式、不猜）：
        ///   · <c>false</c> / <c>off</c> / <c>0</c> / 空 → 关闭（<paramref name="enabled"/> = false）；
        ///   · <c>true</c> / <c>on</c>        → 开启，目标自动（第一个远端角色、槽位 0）；
        ///   · <c>2</c>                      → 开启，clientId 2 的槽位 0；
        ///   · <c>2#1</c>                    → 开启，clientId 2 的槽位 1（未来多角色）；
        ///   · <c>client2</c> / <c>2:1</c>   → **拒绝**（不做模糊匹配，日志里写明可用写法）。
        /// 解析失败时返回 false 且 <paramref name="enabled"/> = false（功能保持关闭，绝不半开）。
        /// </summary>
        public static bool TryParse(string value, out DebugMirrorTarget target, out bool enabled,
            out string error)
        {
            target = default;
            enabled = false;
            error = null;
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
                return true; // 缺省 = 关闭
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) ||
                text == "0")
            {
                return true; // 显式关闭
            }
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
            {
                target = Auto;
                enabled = true;
                return true;
            }
            int hash = text.IndexOf('#');
            if (hash >= 0)
            {
                string clientText = text.Substring(0, hash).Trim();
                string slotText = text.Substring(hash + 1).Trim();
                if (!int.TryParse(clientText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int clientId))
                {
                    error = "debugmirrorclient=\"" + text + "\" 的 clientId 部分不是整数（写法：true | <clientId> | <clientId>#<slot>）";
                    return false;
                }
                if (!int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int slot))
                {
                    error = "debugmirrorclient=\"" + text + "\" 的 slot 部分不是整数（写法：true | <clientId> | <clientId>#<slot>）";
                    return false;
                }
                if (clientId <= 0)
                {
                    error = "debugmirrorclient=\"" + text + "\" 的 clientId 必须 > 0（clientId 0 是主机自己的角色，不能当镜像目标）";
                    return false;
                }
                if (slot < 0 || slot > MaxSlot)
                {
                    error = "debugmirrorclient=\"" + text + "\" 的 slot " + slot +
                        " 非法（合法范围 0.." + MaxSlot + "）";
                    return false;
                }
                target = new DebugMirrorTarget(clientId, slot);
                enabled = true;
                return true;
            }
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int plainClientId))
            {
                if (plainClientId <= 0)
                {
                    error = "debugmirrorclient=" + plainClientId +
                        " 非法：0 表示关闭、clientId 0 是主机自己；要指定远端请写 <clientId>（>0）或 true";
                    return false;
                }
                target = new DebugMirrorTarget(plainClientId, 0);
                enabled = true;
                return true;
            }
            error = "debugmirrorclient=\"" + text +
                "\" 无法识别（写法：false | true | <clientId> | <clientId>#<slot>）";
            return false;
        }

        public bool Equals(DebugMirrorTarget other) => ClientId == other.ClientId && Slot == other.Slot;

        public override bool Equals(object obj) => obj is DebugMirrorTarget other && Equals(other);

        public override int GetHashCode() => (ClientId * 397) ^ Slot;

        public static bool operator ==(DebugMirrorTarget left, DebugMirrorTarget right) =>
            left.Equals(right);

        public static bool operator !=(DebugMirrorTarget left, DebugMirrorTarget right) =>
            !left.Equals(right);

        /// <summary>日志 / HUD 口径：<c>auto</c> 或 <c>clientId#slot</c>。</summary>
        public override string ToString() => IsAuto ? "auto" : ClientId + "#" + Slot;
    }

    /// <summary>
    /// 调试镜像计数器（实例持有，无静态状态）。日志按 5 秒限流，计数每帧如实累加。
    /// 口径与需求里的名字一一对应：
    ///   debugMirrorEnabled / debugMirrorDisabled / debugMirrorTargetMissing /
    ///   debugMirrorFrames / debugMirrorCreateFailures / debugMirrorNoSlot。
    /// </summary>
    public sealed class DebugMirrorCounters
    {
        /// <summary>成功创建镜像视图的次数（不是"想开"的次数）。</summary>
        public long Enabled { get; private set; }

        /// <summary>把存在的镜像视图完整拆除的次数。</summary>
        public long Disabled { get; private set; }

        /// <summary>已开启但目标暂不可用（远端角色没建好 / 已离开 / 世界已卸载）的帧数。</summary>
        public long TargetMissing { get; private set; }

        /// <summary>镜像视图存活的帧数（相机由引擎每帧更新；这里只证明它一直在跟）。</summary>
        public long Frames { get; private set; }

        /// <summary>创建失败次数（不含"没有空位"，那一条单独计数）。</summary>
        public long CreateFailures { get; private set; }

        /// <summary>因引擎 4 GameWidget / 4 PlayerIndex 上限而创建失败的次数。</summary>
        public long NoSlot { get; private set; }

        public void NoteEnabled() => Enabled++;

        public void NoteDisabled() => Disabled++;

        public void NoteTargetMissing() => TargetMissing++;

        public void NoteFrame() => Frames++;

        public void NoteCreateFailure(bool noSlot)
        {
            CreateFailures++;
            if (noSlot) NoSlot++;
        }

        public string Describe() =>
            "enabled=" + Enabled + " disabled=" + Disabled + " targetMissing=" + TargetMissing +
            " frames=" + Frames + " createFailures=" + CreateFailures + " noSlot=" + NoSlot;
    }

    /// <summary>
    /// 调试镜像的**引擎端口**：纯逻辑只依赖这个接口，引擎侧适配器
    /// （<c>Engine/DebugMirrorClient.cs</c>）负责建 / 拆 GameWidget 与临时 PlayerData。
    /// 所有方法都返回明确的失败原因（不做静默兜底）。
    /// </summary>
    public interface IDebugMirrorPort
    {
        /// <summary>当前是否有一个完整的镜像视图在运行（GameWidget + 临时 PlayerData 都在）。</summary>
        bool IsAlive { get; }

        /// <summary>当前引擎里的 GameWidget 数量（状态行用；4 是引擎上限）。</summary>
        int ViewCount { get; }

        /// <summary>
        /// 列出主机上**当前可镜像**的远端角色（clientId &gt; 0），按 (clientId, slot) 升序。
        /// 返回 false 时 reason 写明为什么一个都没有（不猜目标）。
        /// </summary>
        bool TryListTargets(List<DebugMirrorTarget> targets, out string reason);

        /// <summary>
        /// 为指定目标建镜像视图。失败返回 false；<paramref name="noSlot"/> 表示失败原因是
        /// 引擎 4 GameWidget / 4 PlayerIndex 上限（需求里的 <c>debugMirrorNoSlot</c>）。
        /// </summary>
        bool TryCreate(DebugMirrorTarget target, out bool noSlot, out string failureReason);

        /// <summary>
        /// 每帧校验：世界还在、远端角色还在、相机的 Target 还是那个角色。
        /// 返回 false 时 reason 写明原因（调用方据此拆除镜像）。
        /// </summary>
        bool Validate(out string reason);

        /// <summary>拆除镜像视图（可重复调用；没有镜像时是 no-op）。</summary>
        void Destroy(string reason);
    }

    /// <summary>
    /// 调试镜像**纯逻辑**：配置闸门 / 目标选择 / 生命周期 / 限流日志 / 计数。
    ///
    /// 需求（debug-only host-side split-screen mirror）的硬约束在这里落地：
    ///   · 配置 <c>debugmirrorclient</c> 默认关闭 → 一个镜像玩家都不创建；
    ///   · 只在**主机端 + 世界内**创建；离开房间 / 世界卸载 / 开关关闭 → 立刻拆除；
    ///   · 目标默认是所选客户端槽位 0，可循环（HUD 按钮），未来 clientId#slot；
    ///   · 目标缺失时只计数 + 限流日志，绝不拿别的角色 / 默认点顶替；
    ///   · 本类无静态状态；时间 / 日志 / 引擎动作都由组合根注入。
    /// </summary>
    public sealed class DebugMirrorController
    {
        private readonly IDebugMirrorPort m_port;
        private readonly Action<string> m_info;
        private readonly Action<string> m_warn;
        private readonly Func<double> m_clock;
        private readonly bool m_configured;
        private readonly DebugMirrorTarget m_configuredTarget;
        private readonly DebugMirrorCounters m_counters = new DebugMirrorCounters();
        private readonly Dictionary<string, double> m_lastLogTime =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly List<DebugMirrorTarget> m_targets = new List<DebugMirrorTarget>();

        private bool m_enabled;
        private bool m_wasHosting;
        private bool m_hasActiveTarget;
        private DebugMirrorTarget m_requested;
        private DebugMirrorTarget m_activeTarget;

        public DebugMirrorController(string configValue, IDebugMirrorPort port,
            Action<string> info, Action<string> warn, Func<double> clock)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_info = info;
            m_warn = warn;
            m_clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (!DebugMirrorTarget.TryParse(configValue, out DebugMirrorTarget target,
                    out bool enabled, out string error))
            {
                // 配置写错：功能保持关闭，但日志里给出可直接定位的原因（不静默）。
                m_configured = false;
                m_configuredTarget = default;
                WarnRateLimited("config", "[SuMP][debug-mirror] 配置解析失败，功能保持关闭：" + error);
                return;
            }
            m_configured = enabled;
            m_configuredTarget = target;
            m_requested = target;
            // 配置开启 = 进入主机房间后自动开始（HUD 只是运行时的开关 / 换目标）。
            m_enabled = enabled;
        }

        /// <summary>配置里是否显式开启了调试镜像（默认 false）。</summary>
        public bool IsConfigured => m_configured;

        /// <summary>运行时是否处于"想要镜像"的状态（配置开启且没被 HUD 关掉）。</summary>
        public bool IsEnabled => m_enabled;

        public DebugMirrorCounters Counters => m_counters;

        /// <summary>配置解析出的目标（未配置时是 default）。</summary>
        public DebugMirrorTarget ConfiguredTarget => m_configuredTarget;

        /// <summary>当前实际在镜像的目标；没有镜像视图时返回 false。</summary>
        public bool TryGetActiveTarget(out DebugMirrorTarget target)
        {
            target = m_activeTarget;
            return m_hasActiveTarget;
        }

        /// <summary>
        /// HUD 按钮：小按钮只做两件事 —— 关闭状态下点一次 = 开启（用配置目标）；
        /// 开启状态下点一次 = 循环到下一个可用远端角色，循环完 = 关闭。
        /// </summary>
        public bool CycleFromHud()
        {
            if (!m_configured)
            {
                WarnRateLimited("hud-unavailable",
                    "[SuMP][debug-mirror] 配置 debugmirrorclient=false（默认）→ MIR 按钮不生效；" +
                    "要启用请在 SuMultiplayer.json 写 \"debugmirrorclient\": true（或 <clientId>[#<slot>]）");
                return false;
            }
            if (!m_enabled)
            {
                m_enabled = true;
                m_requested = m_configuredTarget;
                InfoRateLimited("hud-on", "[SuMP][debug-mirror] MIR 按钮开启：目标=" +
                    m_configuredTarget + "（等待主机房间与远端角色就绪）");
                return true;
            }
            m_targets.Clear();
            if (!m_port.TryListTargets(m_targets, out string listReason) || m_targets.Count == 0)
            {
                WarnRateLimited("hud-cycle-empty",
                    "[SuMP][debug-mirror] MIR 按钮：当前没有可镜像的远端角色（" +
                    (listReason ?? "列表为空") + "）→ 保持当前目标");
                return false;
            }
            DebugMirrorTarget current = m_hasActiveTarget ? m_activeTarget : m_requested;
            int index = current.IsAuto ? -1 : m_targets.FindIndex(item => item == current);
            int next = index + 1;
            if (next >= m_targets.Count)
            {
                m_enabled = false;
                if (m_port.IsAlive) DestroyMirror("MIR 按钮关闭（循环结束）");
                else InfoRateLimited("hud-off", "[SuMP][debug-mirror] MIR 按钮关闭");
                return true;
            }
            m_requested = m_targets[next];
            if (m_port.IsAlive) DestroyMirror("MIR 按钮切换目标 → " + m_requested);
            InfoRateLimited("hud-cycle", "[SuMP][debug-mirror] MIR 按钮切换目标 → " + m_requested);
            return true;
        }

        /// <summary>
        /// 每帧驱动。参数由组合根给：世界是否加载、本端是否正在主持房间。
        /// 顺序很重要：先处理"离开房间 / 世界卸载"，再处理开关，最后才是创建 / 跟随。
        /// </summary>
        public void Tick(bool inWorld, bool hosting)
        {
            if (!m_configured)
            {
                if (m_port.IsAlive) DestroyMirror("配置 debugmirrorclient 未开启");
                return;
            }
            bool hostingNow = inWorld && hosting;
            if (m_wasHosting && !hostingNow)
            {
                // 离开房间 / 离开世界：需求要求"自动移除"，并且下次进房按配置重新开始。
                if (m_port.IsAlive)
                    DestroyMirror(inWorld ? "已离开房间（不再是主机）" : "世界已卸载");
                m_enabled = m_configured;
                m_requested = m_configuredTarget;
                m_hasActiveTarget = false;
            }
            m_wasHosting = hostingNow;
            if (!hostingNow)
            {
                if (m_port.IsAlive) DestroyMirror("不是主机 / 不在世界内（镜像只在主机端）");
                return;
            }
            if (!m_enabled)
            {
                if (m_port.IsAlive) DestroyMirror("调试开关已关闭");
                return;
            }
            if (m_port.IsAlive)
            {
                if (!m_port.Validate(out string invalidReason))
                {
                    m_counters.NoteTargetMissing();
                    WarnRateLimited("target-invalid",
                        "[SuMP][debug-mirror] 镜像目标失效：" + invalidReason + " → 拆除并等待目标回来");
                    DestroyMirror("目标失效：" + invalidReason);
                    return;
                }
                m_counters.NoteFrame();
                InfoRateLimited("frames", "[SuMP][debug-mirror] debugMirrorFrames：跟随中：" + DescribeLive());
                return;
            }
            m_targets.Clear();
            if (!m_port.TryListTargets(m_targets, out string reason) || m_targets.Count == 0)
            {
                m_counters.NoteTargetMissing();
                WarnRateLimited("target-missing",
                    "[SuMP][debug-mirror] debugMirrorTargetMissing：没有可镜像的远端角色（" +
                    (reason ?? "列表为空") + "）→ 本帧不创建，等待客户端加入");
                return;
            }
            DebugMirrorTarget chosen;
            if (m_requested.IsAuto)
            {
                chosen = m_targets[0];
            }
            else
            {
                int index = m_targets.FindIndex(item => item == m_requested);
                if (index < 0)
                {
                    m_counters.NoteTargetMissing();
                    WarnRateLimited("target-gone",
                        "[SuMP][debug-mirror] debugMirrorTargetMissing：目标 " + m_requested +
                        " 不在可用列表 [" + DescribeTargets() + "] → 本帧不创建（不拿别的角色顶替）");
                    return;
                }
                chosen = m_targets[index];
            }
            if (!m_port.TryCreate(chosen, out bool noSlot, out string failureReason))
            {
                m_counters.NoteCreateFailure(noSlot);
                WarnRateLimited(noSlot ? "create-noslot" : "create-failure",
                    "[SuMP][debug-mirror] debugMirrorCreateFailures：创建镜像视图失败" +
                    (noSlot ? "（debugMirrorNoSlot：4 分屏上限）" : "") +
                    "：" + failureReason + " · " + m_counters.Describe());
                return;
            }
            m_activeTarget = chosen;
            m_hasActiveTarget = true;
            m_counters.NoteEnabled();
            InfoRateLimited("enabled", "[SuMP][debug-mirror] debugMirrorEnabled：目标=" + chosen +
                " · " + DescribeLive());
        }

        /// <summary>状态行（5 秒日志 / 报告用）。没有配置时也给出明确的一行。</summary>
        public string StatusLine()
        {
            string config = m_configured ? m_configuredTarget.ToString() : "off(默认)";
            string state;
            if (!m_configured) state = "off";
            else if (!m_enabled) state = "off(hud)";
            else if (m_port.IsAlive) state = "on";
            else state = "waiting";
            return "debug-mirror: config=" + config + " state=" + state + " active=" +
                (m_hasActiveTarget ? m_activeTarget.ToString() : "-") + " views=" +
                m_port.ViewCount + " " + m_counters.Describe();
        }

        /// <summary>
        /// 调试 HUD 按钮文字（小、显式）：<c>MIR</c> = 关闭 / 未启用；
        /// <c>MIR2</c> = 正在镜像 clientId 2 的槽位 0；<c>MIR2#1</c> = 槽位 1；
        /// <c>MIR?</c> = 已开启但目标暂时不可用（等客户端加入）。
        /// </summary>
        public string HudText()
        {
            if (!m_configured || !m_enabled) return "MIR";
            if (!m_hasActiveTarget) return "MIR?";
            return m_activeTarget.Slot == 0
                ? "MIR" + m_activeTarget.ClientId.ToString(CultureInfo.InvariantCulture)
                : "MIR" + m_activeTarget.ClientId.ToString(CultureInfo.InvariantCulture) + "#" +
                    m_activeTarget.Slot.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>HUD / 状态行用的计数摘要。</summary>
        public string DescribeCounters() => m_counters.Describe();

        /// <summary>组合根卸载时调用：有镜像就拆掉（明确原因），并把运行时开关复位。</summary>
        public void Shutdown(string reason)
        {
            if (m_port.IsAlive) DestroyMirror(reason ?? "模块卸载");
            m_enabled = false;
        }

        private string DescribeLive() =>
            (m_hasActiveTarget ? m_activeTarget.ToString() : "-") + " views=" + m_port.ViewCount +
            " " + m_counters.Describe();

        private string DescribeTargets()
        {
            if (m_targets.Count == 0) return "none";
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < m_targets.Count; i++)
            {
                if (text.Length > 0) text.Append(',');
                text.Append(m_targets[i]);
            }
            return text.ToString();
        }

        private void DestroyMirror(string reason)
        {
            try
            {
                m_port.Destroy(reason);
            }
            finally
            {
                m_hasActiveTarget = false;
                m_activeTarget = default;
                m_counters.NoteDisabled();
            }
            InfoRateLimited("disabled", "[SuMP][debug-mirror] debugMirrorDisabled：" + reason +
                " · " + m_counters.Describe());
        }

        private void InfoRateLimited(string key, string message)
        {
            if (!ShouldLog(key, 5000)) return;
            m_info?.Invoke(message);
        }

        private void WarnRateLimited(string key, string message)
        {
            if (!ShouldLog(key, 5000)) return;
            m_warn?.Invoke(message);
        }

        private bool ShouldLog(string key, int intervalMs)
        {
            double now = m_clock();
            if (m_lastLogTime.TryGetValue(key, out double last) && now - last < intervalMs / 1000.0)
                return false;
            m_lastLogTime[key] = now;
            return true;
        }
    }
}

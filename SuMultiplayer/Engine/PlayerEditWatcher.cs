using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎**编辑玩家屏**的进入 / 退出钩子（唯一检测点）。
    ///
    /// 为什么这样接（用户口径 2026-10-20）：
    ///   · 引擎自己的 Edit Players 流程就是 ESC → More → Edit Players →
    ///     <c>PlayersScreen</c> → 点角色 → <c>PlayerScreen</c>（Mode.Edit），
    ///     名字 / 性别 / 皮肤 / 衣服都写进本机 <c>PlayerData</c>；
    ///   · 产品要求**不再轮询玩家资料**：只在 <c>ScreensManager.CurrentScreen</c> 的
    ///     **引用**变化时动作 —— 进入 PlayerScreen 抓一份快照，退出时再抓一份并比较；
    ///   · 帧驱动只比较"当前屏幕引用"（一个字段），不读 PlayerData、不遍历角色。
    ///
    /// 退出时还会把屏幕上的**名字文本框 / 皮肤预览**与 <c>PlayerData</c> 对齐一次：
    /// 引擎 <c>PlayerScreen</c> 的名字是在 TextBox 的 FocusLost 里写回 PlayerData 的
    /// （Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen ctor 的 FocusLost 处理器）；
    /// 直接从 Back 按钮离开时焦点可能来不及丢，名字会留在文本框里没写回 ——
    /// 这里按用户看到的最终值补一次写入（仍然先过 <see cref="PlayerData.VerifyName"/>，
    /// 非法值不碰 PlayerData，也不产生网络消息）。
    ///
    /// 没有静态状态：观察到的屏幕引用 / 进入快照 / 已记过的原因都是实例字段。
    /// </summary>
    public sealed class PlayerEditWatcher
    {
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModParentField m_parentField;
        private readonly PlayerEditCounters m_counters;
        private readonly Action<PlayerEditSnapshot, PlayerEditSnapshot> m_onExited;
        private readonly Action<string> m_info;
        private readonly Action<string> m_warn;
        private readonly Action<string> m_error;

        /// <summary>上一帧观察到的屏幕引用（只比较引用；Screen 对象由 ScreensManager 长期持有）。</summary>
        private Screen m_observedScreen;
        /// <summary>进入 PlayerScreen 时的本机角色快照（退出时与它比较）。</summary>
        private PlayerEditSnapshot m_enterSnapshot;
        /// <summary>进入时 PlayerScreen 正在编辑的 PlayerData（退出时用来对齐 UI 值）。</summary>
        private PlayerData m_editedPlayer;
        /// <summary>m_playerData 反射入口绑定失败的原因（只报一次，不每帧抛）。</summary>
        private bool m_playerDataLookupFailed;
        private string m_lastCounterText = string.Empty;
        private readonly Dictionary<string, long> m_lastReasonLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public PlayerEditWatcher(NetworkAvatarSpawner spawner, IModParentField parentField,
            PlayerEditCounters counters, Action<PlayerEditSnapshot, PlayerEditSnapshot> onExited,
            Action<string> info = null, Action<string> warn = null, Action<string> error = null)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_parentField = parentField;
            m_counters = counters ?? throw new ArgumentNullException(nameof(counters));
            m_onExited = onExited;
            m_info = info;
            m_warn = warn;
            m_error = error;
            // 构造时把当前屏幕记为"已观察"：启动时停在 PlayerScreen（主菜单初始化）
            // 不能被误算成一次"进入编辑"。
            m_observedScreen = ScreensManager.CurrentScreen;
        }

        public PlayerEditCounters Counters => m_counters;

        /// <summary>
        /// 每帧调用（组合根 Frame.Update）。只做一次引用比较；屏幕没变就直接返回。
        /// **不读玩家资料、不遍历 PlayersData** —— 快照只在屏幕切换的那一帧抓。
        /// </summary>
        public void Tick()
        {
            Screen current = ScreensManager.CurrentScreen;
            if (ReferenceEquals(current, m_observedScreen))
            {
                // 进入那一帧 PlayerScreen.Enter 可能还没跑（转屏动画中段才调 Enter）：
                // 屏幕没变的帧只补 m_playerData 引用（一个私有字段，不读玩家资料），
                // 保证退出时能把文本框最终值写回正确的那条 PlayerData。
                if (m_observedScreen is PlayerScreen editing)
                {
                    PlayerData editedNow = TryGetEditedPlayer(editing);
                    // Add Another / 引擎复用同一个 PlayerScreen 对象换了一条 PlayerData：
                    // 引用没变，但编辑对象变了 → 先把上一条按"退出"结算，再把新的一条当成进入。
                    if (m_editedPlayer != null && editedNow != null &&
                        !ReferenceEquals(editedNow, m_editedPlayer))
                    {
                        HandleExit(editing, commitUiValues: false);
                        HandleEnter(editing);
                        LogCountersIfDue();
                        return;
                    }
                    if (m_editedPlayer == null) m_editedPlayer = editedNow;
                }
                LogCountersIfDue();
                return;
            }
            Screen previous = m_observedScreen;
            m_observedScreen = current;
            bool wasEditing = previous is PlayerScreen;
            bool isEditing = current is PlayerScreen;
            if (wasEditing && isEditing)
            {
                // PlayerScreen → PlayerScreen（Add Another / 重新打开编辑同一个世界的角色）：
                // 先按退出结算上一屏，再把新屏当成一次进入（两次快照不会互相污染）。
                HandleExit((PlayerScreen)previous);
                HandleEnter((PlayerScreen)current);
                LogCountersIfDue();
                return;
            }
            if (!wasEditing && !isEditing)
            {
                // 两个都不是 PlayerScreen → 不是编辑屏转换。
                LogCountersIfDue();
                return;
            }
            if (isEditing)
                HandleEnter((PlayerScreen)current);
            else
                HandleExit((PlayerScreen)previous);
            LogCountersIfDue();
        }

        /// <summary>状态行片段（5 秒状态行 / 自检读取）。</summary>
        public string DescribeCounters() => m_counters.Describe();

        private void HandleEnter(PlayerScreen screen)
        {
            m_counters.OnEditScreenEntered();
            m_editedPlayer = TryGetEditedPlayer(screen);
            m_enterSnapshot = CaptureSnapshot("edit-screen-enter");
            string detail = m_enterSnapshot == null
                ? "（快照为空）"
                : "slots=" + m_enterSnapshot.Slots.Count +
                  " 身份=" + PlayerIdentity.Shorten(m_enterSnapshot.Identity);
            string slots = DescribeSlots(m_enterSnapshot);
            if (slots.Length > 0) detail += " · " + slots;
            Info("编辑玩家屏已进入（" + detail + "）→ 退出时比较并同步名字/皮肤");
        }

        private void HandleExit(PlayerScreen screen, bool commitUiValues = true)
        {
            m_counters.OnEditScreenExited();
            // 进入那一帧 PlayerScreen.Enter 可能还没跑（转屏动画中段才调 Enter），
            // 退出时再取一次 m_playerData：此刻它一定已经指向被编辑的那条 PlayerData。
            if (m_editedPlayer == null) m_editedPlayer = TryGetEditedPlayer(screen);
            // ① 把用户看到的最终值补写回 PlayerData（只改名字 / 皮肤，且先过引擎同名规则）。
            // 同一个 PlayerScreen 对象换了一条 PlayerData 时不能补写：屏幕上显示的是新角色。
            if (commitUiValues) CommitScreenValuesToPlayerData(screen, m_editedPlayer);
            // ② 退出快照（此刻编辑结果已经在 PlayerData 里；世界还在就有本机角色）。
            PlayerEditSnapshot exitSnapshot = CaptureSnapshot("edit-screen-exit");
            PlayerEditSnapshot enterSnapshot = m_enterSnapshot;
            m_enterSnapshot = null;
            m_editedPlayer = null;
            PlayerEditDiff diff = PlayerEditSync.Compare(enterSnapshot, exitSnapshot);
            if (diff.AnyChanged)
                Info("编辑玩家屏退出：检测到资料变化 → " + diff.Describe());
            else
                Info("编辑玩家屏退出：没有资料变化（仍会做一次本机角色对齐，不发资料更新）");
            try
            {
                m_onExited?.Invoke(enterSnapshot, exitSnapshot);
            }
            catch (Exception e)
            {
                // 组合根还会隔离一次；这里把上下文留住（屏幕切换点抛异常不能让 HUD 每帧刷）。
                m_error?.Invoke("编辑玩家屏退出处理异常（已隔离）：" + e.GetType().Name + " " +
                    e.Message);
            }
        }

        /// <summary>
        /// 抓本机槽位快照。只在屏幕切换时调用（不是每帧）。
        /// 没有世界 / 世界还没装好时返回带明确原因的空快照。
        /// </summary>
        private PlayerEditSnapshot CaptureSnapshot(string source)
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                LogReasonRateLimited("edit-no-world",
                    "编辑玩家屏切换时没有加载中的 Project（主菜单 / 世界还没装好）→ " +
                    "本次不产生资料更新（不伪造槽位）");
                return PlayerEditSnapshot.Empty(source, hasWorld: false, "没有加载中的 Project");
            }
            var players = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_spawner.TryCollectLocalPlayers(project, players, out bool tooMany,
                    out string reason))
            {
                LogReasonRateLimited("edit-no-local:" + reason,
                    "编辑玩家屏切换时取不到本机角色（" + (reason ?? "原因未知") +
                    "）→ 本次不产生资料更新（不伪造槽位）");
                return PlayerEditSnapshot.Empty(source, hasWorld: true, reason);
            }
            var profiles = new List<PlayerProfileSnapshot>(players.Count);
            for (int slot = 0; slot < players.Count; slot++)
            {
                PlayerProfileSnapshot profile = m_spawner.CaptureProfile(players[slot],
                    out string captureReason);
                if (profile == null)
                {
                    LogReasonRateLimited("edit-profile:" + slot,
                        "编辑玩家屏切换时槽位 " + slot + " 的资料取不到（" + captureReason +
                        "）→ 该槽位不参与本次同步（不伪造名字/皮肤）");
                }
                profiles.Add(profile);
            }
            if (tooMany)
            {
                LogReasonRateLimited("edit-too-many-slots",
                    "本机角色数超过引擎上限 " + PlayerKey.SlotCount +
                    "，只同步 PlayerIndex 最小的 " + PlayerKey.SlotCount + " 个槽位");
            }
            return PlayerEditSync.Capture(PlayerIdentity.LocalIdentity(), profiles, source,
                hasWorld: true, reason);
        }

        /// <summary>
        /// 引擎 <see cref="PlayerScreen"/> 的 <c>m_playerData</c>（私有字段）：
        /// 走 SuAPI 的官方父类反射入口，用来在退出时把 UI 最终值补写回**正确的那一条** PlayerData。
        /// 用**非泛型**读取：泛型版在字段为 null 时会抛 InvalidCastException —— 屏幕切换那一帧
        /// <c>PlayerScreen.Enter</c> 还没跑（m_playerData 还是 null），泛型版会误报"不是 PlayerData"，
        /// 之后 m_editedPlayer 绑定失败，退出时补写 UI 值整条路径失效（实机 2026-10-21 的报错）。
        /// 绑定失败只报一次并继续（不崩溃）；快照仍按整组本机槽位比较。
        /// </summary>
        private PlayerData TryGetEditedPlayer(PlayerScreen screen)
        {
            if (m_parentField == null || screen == null) return null;
            try
            {
                object value = m_parentField.GetParentField(screen, "m_playerData");
                return value as PlayerData;      // null = 屏幕还没进入 / 已经 Leave（正常时序，不是绑定失败）
            }
            catch (Exception e)
            {
                if (!m_playerDataLookupFailed)
                {
                    m_playerDataLookupFailed = true;
                    m_warn?.Invoke("读不到 PlayerScreen.m_playerData（" + e.GetType().Name + " " +
                        e.Message + "）→ 退出时不会补写 UI 值；仍按本机槽位快照比较" +
                        "（名字已在 PlayerData 里时不受影响）");
                }
                return null;
            }
        }

        /// <summary>
        /// 把 PlayerScreen 上用户看到的最终名字 / 皮肤写回 PlayerData。
        /// 只处理**编辑中的那一条** PlayerData；名字先过 <see cref="PlayerData.VerifyName"/>，
        /// 不合法就保持原值并计数（绝不把非法名字写进世界或发给主机）。
        /// </summary>
        private void CommitScreenValuesToPlayerData(PlayerScreen screen, PlayerData playerData)
        {
            if (screen == null || playerData == null)
            {
                m_counters.OnUiCommitFailure();
                LogReasonRateLimited("edit-commit-no-player",
                    "编辑玩家屏退出：拿不到被编辑的那条 PlayerData → 名字/皮肤的最终值无法补写" +
                    "（只按 PlayerData 里已有的值比较；请检查 Screen 切换时序 / m_playerData 反射）");
                return;
            }
            // TextBoxWidget 在引擎里是 internal（Survivalcraft/Game/TextBoxWidget.cs:9），
            // 所以按控件名取 Widget，再走 SuAPI 的父类反射入口读它的 public Text 属性
            // （ModParentField.GetParentField 先找字段再找属性，TextBoxWidget.Text 是属性）。
            Widget nameBox = screen.Children.Find<Widget>("Name", false);
            if (nameBox != null && m_parentField != null)
            {
                string text = null;
                try
                {
                    text = m_parentField.GetParentField(nameBox, "Text") as string;
                }
                catch (Exception e)
                {
                    m_counters.OnUiCommitFailure();
                    LogReasonRateLimited("edit-namebox-read",
                        "读 PlayerScreen 名字文本框失败（" + e.GetType().Name + " " + e.Message +
                        "）→ 只同步 PlayerData 里已有的名字");
                }
                string name = (text ?? string.Empty).Trim();
                if (name.Length > 0 && !string.Equals(name, playerData.Name, StringComparison.Ordinal))
                {
                    if (PlayerData.VerifyName(name))
                    {
                        playerData.Name = name;
                        m_counters.OnUiNameCommitted();
                        Info("编辑玩家屏：名字文本框的最终值已写回 PlayerData（\"" + name +
                            "\"，引擎 FocusLost 可能来不及触发）");
                    }
                    else
                    {
                        m_counters.OnUiCommitFailure();
                        LogReasonRateLimited("edit-invalid-name:" + name,
                            "编辑玩家屏：名字文本框的最终值 \"" + name +
                            "\" 未通过 PlayerData.VerifyName → 保持原名字 \"" +
                            (playerData.Name ?? string.Empty) + "\"（不写非法名字）");
                    }
                }
            }
            PlayerModelWidget model = screen.Children.Find<PlayerModelWidget>("Model", false);
            if (model != null)
            {
                string skin = (model.CharacterSkinName ?? string.Empty).Trim();
                if (skin.Length > 0 &&
                    !string.Equals(skin, playerData.CharacterSkinName, StringComparison.Ordinal))
                {
                    playerData.CharacterSkinName = skin;
                    m_counters.OnUiSkinCommitted();
                    Info("编辑玩家屏：皮肤预览的最终值已写回 PlayerData（\"" + skin + "\"）");
                }
            }
        }

        private void LogCountersIfDue()
        {
            long nowMs = Environment.TickCount64;
            string text = m_counters.Describe();
            if (string.Equals(text, m_lastCounterText, StringComparison.Ordinal)) return;
            if (!m_counters.ShouldLog("edit-counters", nowMs)) return;
            m_lastCounterText = text;
            Info("编辑玩家屏计数：" + text);
        }

        private void LogReasonRateLimited(string key, string message)
        {
            long nowMs = Environment.TickCount64;
            if (m_lastReasonLogMs.TryGetValue(key, out long last) &&
                nowMs - last < PlayerEditCounters.LogIntervalMs)
                return;
            m_lastReasonLogMs[key] = nowMs;
            m_warn?.Invoke(message);
        }

        private void Info(string message)
        {
            if (!string.IsNullOrEmpty(message)) m_info?.Invoke(message);
        }

        private static string DescribeSlots(PlayerEditSnapshot snapshot)
        {
            if (snapshot?.Slots == null || snapshot.Slots.Count == 0) return string.Empty;
            var text = new System.Text.StringBuilder(64);
            for (int i = 0; i < snapshot.Slots.Count; i++)
            {
                if (i > 0) text.Append("；");
                text.Append(snapshot.Slots[i].Describe());
            }
            return text.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using Game;
using GameEntitySystem;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 主机本机角色"更新窗口"的接收端（实现 = Engine/RemoteActionAudioReplica.cs）。
    /// 只在主机 + 全局捕获已启用时存在；窗口内新增的引擎音频队列条目由实现方归属到该槽位。
    /// </summary>
    internal interface ILocalPlayerAudioWindowSink
    {
        /// <summary>某个本机角色的一个组件 Update 即将开始（此时记录队列长度）。</summary>
        void BeginLocalAudioWindow(int slot, int playerIndex, ComponentPlayer player);

        /// <summary>该组件 Update 结束（异常也要走到）：把窗口内新增条目归属到该角色。</summary>
        void EndLocalAudioWindow(int slot, int playerIndex);

        /// <summary>安装 / 还原窗口失败（实现方负责 10 s 限流 + 明确计数）。</summary>
        void NoteLocalPlayerWindowProblem(string key, string message);
    }

    /// <summary>
    /// 把**每个本机角色实体的每个 <see cref="IUpdateable"/> 组件**包一层捕获窗口。
    ///
    /// 为什么需要包组件而不是只包 <c>ComponentPlayer.Update</c>：
    ///   · 引擎的 <c>SubsystemUpdate</c> 逐个调用 <c>IUpdateable.Update</c>
    ///     （Source: Survivalcraft/Game/SubsystemUpdate.cs:84-93），玩家的声音分散在**多个**
    ///     组件里：脚步声在 ComponentHumanModel.Update（:196-204）→
    ///     SubsystemSoundMaterials.PlayFootstepSound（:34-58）与 ComponentLocomotion.Update
    ///     （:317 / :537-587）；喊声在 ComponentPlayer.Update（:100-117）；
    ///     呻吟 / 打喷嚏 / 咳嗽在 ComponentVitalStats.Update / ComponentFlu.Update /
    ///     ComponentSickness.Update；着火 Sizzle 在 ComponentOnFire.Update（:76）；
    ///     挥手 / 放置声在 ComponentMiner.Update（:238 / :323）；落水 / 压碎在 ComponentBody
    ///     Update 的碰撞回调；
    ///   · 只有"在这一个组件 Update 的调用栈内"做队列差集，才能把新增条目绑定到**唯一**
    ///     的角色（同一帧里多个角色都会产生声音，按时间分不出来）。
    ///
    /// 实现方式：通过 SuAPI.RegisterHostUpdateScope 围住真实宿主的整轮附加 + 原版 + After。
    /// 不替换组件，不安装另一个 IUpdateable 代理，也不直接调用宿主 Update。
    /// finally 关闭声音归属窗口；唯一调度代理及原排序键由 SuAPI 统一维护。
    ///   · 每帧由适配层 <c>Pump</c> 对账（角色增删 / 重生 / 关捕获 / 换世界）；
    ///   · 远端化身（NetworkAvatarSpawner 管理的 PlayerData）**不包**：它们的动作声音由
    ///     远端动作窗口的精确锚点负责，不能算到主机本机角色头上。
    ///
    /// 只读：不写格 / 不生成实体 / 不调 AudioManager；代理只是原组件 Update 的一层包装。
    /// </summary>
    internal sealed class LocalPlayerAudioWindows
    {
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly ILocalPlayerAudioWindowSink m_sink;
        private readonly List<SlotState> m_slots = new List<SlotState>(4);
        private readonly List<DesiredSlot> m_desired = new List<DesiredSlot>(4);
        private Project m_project;
        private SubsystemUpdate m_update;
        private int m_localSlots;
        private long m_installFailures;
        private long m_teardownRestores;
        private bool m_missingLogged;
        private bool m_enabled;

        public LocalPlayerAudioWindows(NetworkAvatarSpawner spawner,
            ILocalPlayerAudioWindowSink sink)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        /// <summary>当前安装了窗口的本机角色数（诊断用）。</summary>
        public int SlotCount => m_slots.Count;
        /// <summary>本轮对账看到的本机角色数（可能还没装上窗口）。</summary>
        public int LocalSlots => m_localSlots;
        public long InstallFailures => m_installFailures;
        public long RestoredComponents => m_teardownRestores;

        private struct DesiredSlot
        {
            public int Slot;
            public int PlayerIndex;
            public PlayerData Data;
            public ComponentPlayer Player;
        }

        private sealed class SlotState
        {
            public int Slot;
            public int PlayerIndex;
            public PlayerData Data;
            public ComponentPlayer Player;
            public Entity Entity;
            public SubsystemUpdate Update;
            public readonly List<IUpdateable> Originals = new List<IUpdateable>();
            public readonly List<IDisposable> ScopeTokens = new List<IDisposable>();
            public readonly List<LocalUpdateWindowScope> Scopes =
                new List<LocalUpdateWindowScope>();
        }

        /// <summary>
        /// 每帧对账本机角色的声音作用域；停止捕获或卸载时立即注销本 Mod 的 token。
        /// allowRestore 保留调用签名，但不再阻挡注销；SuAPI 负责安全的延后调度交接，
        /// 不会把本 Mod 的关闭变成直接 Add 原宿主，也不会恢复其他 owner 的暂停。
        /// Source: UpdateExtensionRegistry.cs:RegisterHostUpdateScope
        /// </summary>
        public void Pump(Project project, bool enabled, bool allowRestore)
        {
            m_enabled = enabled && project != null;
            if (!ReferenceEquals(project, m_project))
            {
                // 换世界：旧 Project 的实体已卸载，绝不把旧组件重新塞回旧 update 表。
                TeardownAll(restore: false);
                m_project = project;
                m_update = null;
                m_missingLogged = false;
            }
            if (project == null)
            {
                TeardownAll(restore: false);
                m_update = null;
                m_localSlots = 0;
                m_missingLogged = false;
                return;
            }
            if (!enabled)
            {
                TeardownAll(restore: true);
                m_localSlots = 0;
                m_missingLogged = false;
                return;
            }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (players == null || update == null)
            {
                TeardownAll(restore: true);
                m_update = null;
                m_localSlots = 0;
                if (!m_missingLogged)
                {
                    m_missingLogged = true;
                    m_sink.NoteLocalPlayerWindowProblem("slot-no-players",
                        "找不到 SubsystemPlayers / SubsystemUpdate（世界还没装好）→ 本机角色声音" +
                        "暂时按全局声音处理（下次对账重试）");
                }
                return;
            }
            m_missingLogged = false;
            if (!ReferenceEquals(update, m_update))
            {
                // SubsystemUpdate 换了（换世界 / 世界重载）：旧代理全部作废（不还原到旧表）。
                TeardownAll(restore: false);
                m_update = update;
            }

            m_desired.Clear();
            int slot = 0;
            foreach (PlayerData data in players.PlayersData)
            {
                if (data == null) continue;
                // 远端化身不是本机角色：它由 NetworkAvatarSpawner 管理，动作声音走精确锚点。
                if (m_spawner.Contains(data)) continue;
                ComponentPlayer player = data.ComponentPlayer;
                if (player == null || player.Entity == null ||
                    !player.Entity.IsAddedToProject) continue;
                m_desired.Add(new DesiredSlot
                {
                    Slot = slot,
                    PlayerIndex = data.PlayerIndex,
                    Data = data,
                    Player = player
                });
                slot++;
            }
            m_localSlots = m_desired.Count;

            // 1) 摘掉不再需要的槽位（角色移除 / 重生换了实体 / 变成远端化身）。
            for (int i = m_slots.Count - 1; i >= 0; i--)
            {
                SlotState state = m_slots[i];
                if (FindDesiredIndex(state, out _)) continue;
                bool restore = CanRestore(state);
                m_slots.RemoveAt(i);
                Teardown(state, restore: restore);
            }

            // 2) 安装 / 重建需要的槽位。
            for (int i = 0; i < m_desired.Count; i++)
            {
                DesiredSlot desired = m_desired[i];
                SlotState state = FindState(desired.Player);
                if (state != null && state.Entity == desired.Player.Entity &&
                    ReferenceEquals(state.Update, update))
                {
                    // 组件集合在实体创建时就固定；同一实体 / 同一 update 表无需重建。
                    state.Slot = desired.Slot;
                    state.PlayerIndex = desired.PlayerIndex;
                    for (int p = 0; p < state.Scopes.Count; p++)
                        state.Scopes[p].SetSlot(desired.Slot, desired.PlayerIndex);
                    continue;
                }
                if (state != null)
                {
                    bool restore = CanRestore(state);
                    m_slots.Remove(state);
                    Teardown(state, restore: restore);
                }
                Install(desired, update);
            }
        }

        // Source: UpdateExtensionRegistry.cs:RegisterHostUpdateScope
        // SuAPI owns the only scheduler proxy. This object opens attribution and never calls Host.Update.
        private sealed class LocalUpdateWindowScope
        {
            private readonly ILocalPlayerAudioWindowSink m_sink;
            private readonly ComponentPlayer m_player;
            private readonly Func<bool> m_isEnabled;
            private int m_slot;
            private int m_playerIndex;

            public LocalUpdateWindowScope(ILocalPlayerAudioWindowSink sink, ComponentPlayer player, int slot, int playerIndex, Func<bool> isEnabled)
            {
                m_sink = sink ?? throw new ArgumentNullException(nameof(sink));
                m_player = player;
                m_isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
                SetSlot(slot, playerIndex);
            }

            public void SetSlot(int slot, int playerIndex) { m_slot = slot; m_playerIndex = playerIndex; }
            // null explicitly means capture is disabled for this update; it is not an exception fallback.
            public IDisposable Enter(float dt) => m_isEnabled() ? new Capture(m_sink, m_player, m_slot, m_playerIndex) : null;

            private sealed class Capture : IDisposable
            {
                private readonly ILocalPlayerAudioWindowSink m_sink;
                private readonly int m_slot, m_playerIndex;
                private bool m_closed;
                public Capture(ILocalPlayerAudioWindowSink sink, ComponentPlayer player, int slot, int playerIndex)
                {
                    m_sink = sink;
                    m_slot = slot;
                    m_playerIndex = playerIndex;
                    sink.BeginLocalAudioWindow(slot, playerIndex, player);
                }
                public void Dispose()
                {
                    if (m_closed) return;
                    m_closed = true;
                    m_sink.EndLocalAudioWindow(m_slot, m_playerIndex);
                }
            }
        }

        private SlotState FindState(ComponentPlayer player)
        {
            if (player == null) return null;
            for (int i = 0; i < m_slots.Count; i++)
            {
                if (ReferenceEquals(m_slots[i].Player, player)) return m_slots[i];
            }
            return null;
        }

        private bool FindDesiredIndex(SlotState state, out int index)
        {
            index = -1;
            for (int i = 0; i < m_desired.Count; i++)
            {
                DesiredSlot desired = m_desired[i];
                if (ReferenceEquals(desired.Player, state.Player) &&
                    ReferenceEquals(desired.Data, state.Data) &&
                    state.Entity == desired.Player.Entity &&
                    ReferenceEquals(state.Update, m_update))
                {
                    index = i;
                    return true;
                }
            }
            return false;
        }

        private void Install(DesiredSlot desired, SubsystemUpdate update)
        {
            var state = new SlotState
            {
                Slot = desired.Slot,
                PlayerIndex = desired.PlayerIndex,
                Data = desired.Data,
                Player = desired.Player,
                Entity = desired.Player.Entity,
                Update = update
            };
            try
            {
                foreach (IUpdateable original in state.Entity.FindComponents<IUpdateable>())
                {
                    if (original == null) continue;
                    var scope = new LocalUpdateWindowScope(m_sink, state.Player, state.Slot, state.PlayerIndex, () => m_enabled);
                    IDisposable token = MultiplayerUpdateScheduling.RegisterScope("SuMultiplayer.LocalAudio", original, scope.Enter);
                    state.Originals.Add(original);
                    state.Scopes.Add(scope);
                    state.ScopeTokens.Add(token);
                }
            }
            catch (Exception e)
            {
                m_installFailures++;
                m_sink.NoteLocalPlayerWindowProblem("slot-install:" + e.GetType().Name,
                    "安装本机角色声音窗口失败（playerIndex=" + desired.PlayerIndex + " " +
                    e.GetType().Name + " " + e.Message + "）→ 撤销本次声音作用域登记，明确失败");
                Teardown(state, restore: true);
                throw;
            }
            m_slots.Add(state);
        }

        private bool CanRestore(SlotState state)
        {
            if (state == null || state.Entity == null) return false;
            if (!state.Entity.IsAddedToProject) return false;
            for (int i = 0; i < state.Originals.Count; i++)
            {
                if (state.Originals[i] is Component component && component.Entity == null)
                    return false;
            }
            return true;
        }

        private void TeardownAll(bool restore)
        {
            List<Exception> errors = null;
            for (int i = m_slots.Count - 1; i >= 0; i--)
            {
                try { Teardown(m_slots[i], restore); }
                catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            }
            m_slots.Clear();
            if (errors != null) throw new AggregateException("Local audio scope cleanup failed.", errors);
        }

        // Source: UpdateExtensionRegistry.cs:RegisterHostUpdateScope (token disposal preserves other owners)
        private void Teardown(SlotState state, bool restore)
        {
            if (state == null) return;
            List<Exception> errors = null;
            foreach (IDisposable token in state.ScopeTokens)
            {
                try { token.Dispose(); if (restore) m_teardownRestores++; }
                catch (Exception error)
                {
                    m_sink.NoteLocalPlayerWindowProblem("scope-remove:" + error.GetType().Name,
                        "撤销本机声音作用域失败（playerIndex=" + state.PlayerIndex + "）：" + error);
                    (errors ??= new List<Exception>()).Add(error);
                }
            }
            state.ScopeTokens.Clear();
            state.Originals.Clear();
            state.Scopes.Clear();
            if (errors != null) throw new AggregateException("Local audio scope teardown failed.", errors);
        }
    }
}

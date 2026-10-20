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
    /// 实现方式（不改引擎源码）：
    ///   · 组合根没有替换任何组件类；这里运行时调用 <c>SubsystemUpdate.RemoveUpdateable(组件)</c>
    ///     并把 <see cref="LocalUpdateWindowProxy"/> 加进去；
    ///   · 代理的 <see cref="IUpdateable.UpdateOrder"/> 透传原组件，
    ///     <see cref="LocalUpdateWindowProxy.GetHashCode"/> 也返回原组件的哈希 ——
    ///     <c>SubsystemUpdate.Comparer</c> 的排序键 (UpdateOrder, GetHashCode) 与原状完全一致，
    ///     **不改变任何组件的执行顺序**；
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
            public readonly List<LocalUpdateWindowProxy> Proxies =
                new List<LocalUpdateWindowProxy>();
        }

        /// <summary>
        /// 每帧对账（模块 Tick 调用；此时本帧的 Project.Update 已结束，新增 / 移除的
        /// updateable 会在下一帧派发开始时生效，因此绝不会有一帧同时跑原组件和代理）。
        ///
        /// <paramref name="allowRestore"/> = 本帧是否已经过一个完整的
        /// <c>SubsystemUpdate.Update</c> 派发（只有 <see cref="RemoteActionAudioReplica"/>
        /// 的每帧 <c>Pump</c> 传 true；同一 Tick 里的 <c>SetHostCaptureEnabled</c> 传 false）。
        /// 原因：刚安装的代理在本帧派发前就还原本组件会把"移除 / 添加"两次登记压成
        /// <c>Add(已经存在的组件)</c>（SubsystemUpdate 的 Add 会抛异常）。传 false 时
        /// 先不还原（窗口在关闭捕获后本来就不采集），下一帧再安全地还。
        /// </summary>
        public void Pump(Project project, bool enabled, bool allowRestore)
        {
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
                if (allowRestore) TeardownAll(restore: true);
                m_localSlots = 0;
                m_missingLogged = false;
                return;
            }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (players == null || update == null)
            {
                if (allowRestore) TeardownAll(restore: true);
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
                if (restore && !allowRestore) continue;   // 下一帧安全还原
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
                    for (int p = 0; p < state.Proxies.Count; p++)
                        state.Proxies[p].SetSlot(desired.Slot, desired.PlayerIndex);
                    continue;
                }
                if (state != null)
                {
                    bool restore = CanRestore(state);
                    if (restore && !allowRestore) continue;   // 下一帧安全还原 / 重建
                    m_slots.Remove(state);
                    Teardown(state, restore: restore);
                }
                Install(desired, update);
            }
        }

        private sealed class LocalUpdateWindowProxy : IUpdateable
        {
            private readonly IUpdateable m_inner;
            private readonly ILocalPlayerAudioWindowSink m_sink;
            private readonly ComponentPlayer m_player;
            private int m_slot;
            private int m_playerIndex;

            public LocalUpdateWindowProxy(IUpdateable inner, ILocalPlayerAudioWindowSink sink,
                ComponentPlayer player, int slot, int playerIndex)
            {
                m_inner = inner ?? throw new ArgumentNullException(nameof(inner));
                m_sink = sink;
                m_player = player;
                m_slot = slot;
                m_playerIndex = playerIndex;
            }

            public UpdateOrder UpdateOrder => m_inner.UpdateOrder;

            /// <summary>
            /// 与引擎排序键保持一致：SubsystemUpdate.Comparer 用 (UpdateOrder, GetHashCode)
            /// 排序（Source: Survivalcraft/Game/SubsystemUpdate.cs:19-27）。返回原组件的哈希，
            /// 代理与原组件在排序里的位置完全相同 —— 不改变同序组件的相对执行顺序。
            /// </summary>
            public override int GetHashCode() => m_inner.GetHashCode();

            public void SetSlot(int slot, int playerIndex)
            {
                m_slot = slot;
                m_playerIndex = playerIndex;
            }

            public void Update(float dt)
            {
                bool opened = false;
                try
                {
                    try
                    {
                        m_sink.BeginLocalAudioWindow(m_slot, m_playerIndex, m_player);
                        opened = true;
                    }
                    catch (Exception)
                    {
                        // 归属失败不能影响角色本身的 Update。
                    }
                    m_inner.Update(dt);
                }
                finally
                {
                    if (opened)
                    {
                        try
                        {
                            m_sink.EndLocalAudioWindow(m_slot, m_playerIndex);
                        }
                        catch (Exception)
                        {
                            // 同上：中继侧异常绝不冒泡到引擎的更新派发。
                        }
                    }
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
                    var proxy = new LocalUpdateWindowProxy(original, m_sink, state.Player,
                        state.Slot, state.PlayerIndex);
                    // 先记账再动 update 表：中途异常时 Teardown(restore:true) 才能把
                    // 已经摘掉的组件原样还回去（否则它再也不会被更新）。
                    state.Originals.Add(original);
                    update.RemoveUpdateable(original);
                    state.Proxies.Add(proxy);
                    update.AddUpdateable(proxy);
                }
            }
            catch (Exception e)
            {
                m_installFailures++;
                m_sink.NoteLocalPlayerWindowProblem("slot-install:" + e.GetType().Name,
                    "安装本机角色声音窗口失败（playerIndex=" + desired.PlayerIndex + " " +
                    e.GetType().Name + " " + e.Message + "）→ 回滚该角色，声音按全局路径处理");
                Teardown(state, restore: true);
                return;
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
            for (int i = m_slots.Count - 1; i >= 0; i--)
            {
                Teardown(m_slots[i], restore);
            }
            m_slots.Clear();
        }

        private void Teardown(SlotState state, bool restore)
        {
            if (state == null) return;
            SubsystemUpdate update = state.Update;
            if (update != null)
            {
                for (int i = 0; i < state.Proxies.Count; i++)
                {
                    try { update.RemoveUpdateable(state.Proxies[i]); }
                    catch (Exception e)
                    {
                        m_sink.NoteLocalPlayerWindowProblem("slot-remove:" + e.GetType().Name,
                            "移除本机角色声音窗口代理失败（playerIndex=" + state.PlayerIndex +
                            " " + e.GetType().Name + " " + e.Message + "）→ 明确计数");
                    }
                }
                if (restore)
                {
                    for (int i = 0; i < state.Originals.Count; i++)
                    {
                        try
                        {
                            update.AddUpdateable(state.Originals[i]);
                            m_teardownRestores++;
                        }
                        catch (Exception e)
                        {
                            m_sink.NoteLocalPlayerWindowProblem(
                                "slot-restore:" + e.GetType().Name,
                                "还原本机角色组件 Update 失败（playerIndex=" + state.PlayerIndex +
                                " " + e.GetType().Name + " " + e.Message + "）→ 该组件本帧起" +
                                "不再更新（明确计数）");
                        }
                    }
                }
            }
            state.Originals.Clear();
            state.Proxies.Clear();
        }
    }
}

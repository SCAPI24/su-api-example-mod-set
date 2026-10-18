using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuAPI;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 拾取物子系统的**副本闸门**（新文件，不复制引擎模拟算法）。
    ///
    /// 引擎 <c>SubsystemPickables.Update</c> 同时做三件事：
    ///   ① 物理 / 碰撞 / 水面 / 火焰（表现与位置）；
    ///   ② 到期删除；
    ///   ③ **玩家拾取结算**（1.75m 吸引 → 1.0m 内 AcquireItems）。
    /// 客户端只能有 ①②（副本要能渲染、要能动），③ 必须由主机权威决定；主机上远端化身的
    /// 化身背包是客户端背包的镜像，也不能让引擎替客户端“本地拾取”（否则客户端看不到背包变化，
    /// 也没有拾取回执）。因此：
    ///   · 客户端：base.Update 期间把玩家列表清空（与旧实现相同的做法）；
    ///   · 主机：base.Update 期间只摘掉**远端化身**（本机玩家仍按引擎原生方式拾取）。
    ///
    /// 旧实现：
    /// Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemPickables.cs:SuSubsystemPickables.Update
    ///         （客户端 m_componentPlayers.Clear() / 主机 P7b 领地处理）
    /// Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
    /// </summary>
    public sealed class SuSubsystemPickables : SubsystemPickables, IUpdateable
    {
        private readonly Func<ComponentPlayer, bool> m_suppressAll;
        private readonly List<ComponentPlayer> m_removedPlayers = new List<ComponentPlayer>();
        private readonly List<int> m_removedIndices = new List<int>();
        private List<ComponentPlayer> m_componentPlayers;
        private bool m_bindFailureLogged;
        private long m_filterFailures;

        public SuSubsystemPickables()
        {
            m_suppressAll = delegate { return true; };
        }

        /// <summary>
        /// 主机侧“这个玩家是远端化身”过滤器（由 Engine/PickableReplicaEngine.cs 每帧写入）。
        /// null = 没有联机会话（单机 / 主机还没装配）：完全走原生行为。
        /// </summary>
        public Func<ComponentPlayer, bool> IsRemotePlayer { get; set; }

        /// <summary>反射入口由组合根注入（不在子系统里访问全局单例）。</summary>
        public IModParentField ParentField { get; set; }

        /// <summary>客户端复制适配器（适配器 Pump 注入；主机为 null 也可）。</summary>
        public PickableReplicaEngine Replica { get; set; }

        /// <summary>拿不到玩家列表导致“过滤失败”的次数（状态行 / 诊断可审计）。</summary>
        public long PickupFilterFailures => m_filterFailures;

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        void IUpdateable.Update(float dt)
        {
            SubsystemTerrain terrain = Project?.FindSubsystem<SubsystemTerrain>(false);
            bool clientReplica = ReplicaGates.IsClient(terrain);
            Func<ComponentPlayer, bool> filter = clientReplica ? m_suppressAll : IsRemotePlayer;
            if (filter == null)
            {
                base.Update(dt);
                return;
            }
            if (ParentField == null || !EnsurePlayersBound())
            {
                m_filterFailures++;
                if (!m_bindFailureLogged)
                {
                    m_bindFailureLogged = true;
                    if (ParentField == null)
                    {
                        // 反射入口由适配器在第一次 Pump 时注入；这里只是启动顺序，不是失败。
                        Log.Information("[SuMP] SuSubsystemPickables 的反射入口尚未注入 → " +
                            (clientReplica
                                ? "本帧不跑 base.Update（绝不本地拾取）"
                                : "本帧按原生行为跑 base.Update"));
                    }
                    else
                    {
                        Log.Warning("[SuMP] SuSubsystemPickables 拿不到 " +
                            "SubsystemPlayers.m_componentPlayers（反射失败）→ " +
                            (clientReplica
                                ? "客户端不跑 base.Update（宁可副本不动，也不让客户端本地拾取主机物品）"
                                : "主机继续跑 base.Update（原生拾取会绕过请求链，请检查引擎版本）"));
                    }
                }
                if (!clientReplica) base.Update(dt);
                return;
            }
            // 客户端：整段不跑玩家拾取；主机：只摘远端化身。其余物理/到期/水面表现照常。
            m_removedPlayers.Clear();
            m_removedIndices.Clear();
            for (int i = m_componentPlayers.Count - 1; i >= 0; i--)
            {
                ComponentPlayer player = m_componentPlayers[i];
                if (player == null || !filter(player)) continue;
                m_removedPlayers.Add(player);
                m_removedIndices.Add(i);
                m_componentPlayers.RemoveAt(i);
            }
            try
            {
                if (clientReplica)
                {
                    // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemPickables.cs:136-145
                    // 远端副本的水面进入音效/粒子不应被本端重复触发（主机已经播过）。
                    ReadOnlyList<Pickable> pickables = Pickables;
                    for (int i = 0; i < pickables.Count; i++)
                        if (pickables[i] != null) pickables[i].SplashGenerated = true;
                }
                base.Update(dt);
                if (clientReplica) Replica?.CullForeignClientPickables();
            }
            finally
            {
                // 按原下标升序插回，保持 SubsystemPlayers 的玩家顺序（就近选择等逻辑依赖顺序）。
                for (int i = m_removedIndices.Count - 1; i >= 0; i--)
                {
                    int index = m_removedIndices[i];
                    if (index > m_componentPlayers.Count) index = m_componentPlayers.Count;
                    m_componentPlayers.Insert(index, m_removedPlayers[i]);
                }
                m_removedPlayers.Clear();
                m_removedIndices.Clear();
            }
        }

        private bool EnsurePlayersBound()
        {
            if (m_componentPlayers != null) return true;
            if (ParentField == null) return false;
            try
            {
                SubsystemPlayers players = Project?.FindSubsystem<SubsystemPlayers>(false);
                if (players == null) return false;
                m_componentPlayers = ParentField.GetParentField<List<ComponentPlayer>>(players,
                    "m_componentPlayers", typeof(SubsystemPlayers));
            }
            catch (Exception)
            {
                m_componentPlayers = null;
            }
            return m_componentPlayers != null;
        }
    }

    /// <summary>
    /// 投射物子系统的**副本闸门**：客户端**完全不跑** <c>SubsystemProjectiles.Update</c>。
    ///
    /// 为什么不能只摘玩家：引擎这一支还会
    ///   · 命中地形时 <c>SubsystemTerrain.DestroyCell</c>（客户端会把它当成玩家编辑上报！）；
    ///   · 命中生物时 <c>ComponentMiner.AttackBody</c>（客户端本地扣血）；
    ///   · 停止时 <c>AddPickable</c>（客户端本地多出一份主机不知道的掉落物）；
    ///   · 岩浆 / 火焰里 <c>TryExplodeBlock</c>（客户端本地爆炸）。
    /// 这些都是主机权威的副作用。客户端只需要位置 / 旋转 / 拖尾用于渲染，由
    /// <see cref="PickableReplicaEngine"/> 按主机快照插值写入。
    ///
    /// 旧实现参考：Mod/ScMultiplayer/Func/Subsystem/SuSubsystemProjectiles.cs
    /// （旧版保留了 base.Update 并摘 Owner；重写版为“客户端绝不模拟”直接停掉整段）。
    /// </summary>
    public sealed class SuSubsystemProjectiles : SubsystemProjectiles, IUpdateable
    {
        private bool m_loggedClientSkip;

        /// <summary>客户端复制适配器（组合根 / 适配器 Pump 注入；主机为 null 也可）。</summary>
        public PickableReplicaEngine Replica { get; set; }

        void IUpdateable.Update(float dt)
        {
            SubsystemTerrain terrain = Project?.FindSubsystem<SubsystemTerrain>(false);
            if (ReplicaGates.IsClient(terrain))
            {
                if (!m_loggedClientSkip)
                {
                    m_loggedClientSkip = true;
                    Log.Information("[SuMP] 客户端投射物子系统：不跑原生 Update（不写地形 / 不伤人 / " +
                        "不生成掉落物 / 不触发爆炸），位置由主机快照插值；" +
                        "客户端武器本地预测的投射物会被清除（开火必须由主机创建）");
                }
                Replica?.CullForeignClientProjectiles();
                return;
            }
            base.Update(dt);
        }
    }
}

using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuAPI;
using SuMultiplayer.Modules;

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
        /// <summary>主机拾取窗口内注入玩家列表的化身（窗口结束逐个移除，恢复引擎自己的列表）。</summary>
        private readonly List<ComponentPlayer> m_injectedPlayers = new List<ComponentPlayer>();
        private List<ComponentPlayer> m_componentPlayers;
        private bool m_bindFailureLogged;
        private bool m_phantomFaultLogged;
        private long m_filterFailures;

        public SuSubsystemPickables()
        {
            m_suppressAll = delegate { return true; };
            // 客户端 UI 拖出安全网：AddPickable 是**所有**本地拾取物创建的唯一漏斗
            // （ViewWidget.DragDrop / 方块行为 / 引擎预测都走它）。正常路径由
            // Engine/UiDropInterception.cs 在 ViewWidget.DragDrop 之前拦下，本地不会创建；
            // 若某个路径漏过拦截器，这里按"落点 == 相机视点 + 背包差分唯一匹配"撤销幽灵
            // （物品放回背包后仍走请求链）。事件挂在实例上，无静态状态。
            PickableAdded += OnPickableAdded;
        }

        private void OnPickableAdded(Pickable pickable)
        {
            try
            {
                Replica?.TryHandleLocalPickableAdded(pickable);
            }
            catch (Exception e)
            {
                if (!m_phantomFaultLogged)
                {
                    m_phantomFaultLogged = true;
                    Log.Error("[SuMP] UI 拖出安全网异常：" + e.GetType().Name + " " +
                        e.Message + " → 本帧不接管（该拾取物会按既有外来清理处理；只记录一次）");
                }
            }
        }

        /// <summary>
        /// 主机侧化身枚举（由 Engine/PickableReplicaEngine.cs 每帧写入）。
        ///
        /// **为什么要注入**（2026-10-26 实测定案）：SuMultiplayer 建好远端 avatar 后会把它的
        /// PlayerData 从 <c>SubsystemPlayers.m_playersData</c> 移除（CmdBridgeMod 的
        /// AllPlayersObserver 注释同样记载了这一点），而引擎的
        /// <c>UpdateComponentPlayers</c> 每次实体增删都从 m_playersData **清空重建**玩家列表 →
        /// 化身不在 <c>SubsystemPickables.Update</c> 的玩家表里，引擎磁吸对化身根本不生效
        /// （站在掉落物上也不吸，真机复现）。所以主机在拾取窗口内把化身**注入**玩家列表
        /// （追加在本机玩家之后 = 分屏同款顺序），窗口结束移除——只影响这一段 Update，
        /// 引擎自己的列表在其他系统眼里保持原样。
        /// null = 没有联机会话 / 适配层未挂接：完全走原生行为。
        /// </summary>
        public Func<IEnumerable<ComponentPlayer>> AvatarPlayersProvider { get; set; }

        /// <summary>反射入口由组合根注入（不在子系统里访问全局单例）。</summary>
        public IModParentField ParentField { get; set; }

        /// <summary>客户端复制适配器（适配器 Pump 注入；主机为 null 也可）。</summary>
        public PickableReplicaEngine Replica { get; set; }

        /// <summary>
        /// 主机水花捕获窗口（Engine/WaterSplashReplicaEngine.cs 的 Pump 挂接）。窗口只包住
        /// <c>base.Update</c>：引擎在落水分支里创建的 <c>WaterSplashParticleSystem</c> 由适配层
        /// 做粒子表差集取出。客户端不打开窗口（引擎原生水花由主机下发，见下方 SplashGenerated）。
        /// Source: Survivalcraft/Game/SubsystemPickables.cs:325-330
        /// </summary>
        internal IWaterSplashHostSink SplashSink { get; set; }

        /// <summary>
        /// 主机拾取捕获窗口（PickableReplicaEngine 的 Pump 挂接，接口见 Modules/PickablePorts.cs）。
        /// 窗口只包住 host 分支的 <c>base.Update</c>：引擎原生替化身拾取后背包被静默改变，
        /// 适配层在窗口两端做快照差分，把增量作为 <c>PickableAcquireResult(sequence=0)</c>
        /// 通知对应客户端（见 entities.proto）。
        /// </summary>
        internal IPickupHostWindowSink PickupWindowSink { get; set; }

        /// <summary>拿不到玩家列表导致“过滤失败”的次数（状态行 / 诊断可审计）。</summary>
        public long PickupFilterFailures => m_filterFailures;

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        void IUpdateable.Update(float dt)
        {
            SubsystemTerrain terrain = Project?.FindSubsystem<SubsystemTerrain>(false);
            bool clientReplica = ReplicaGates.IsClient(terrain);
            if (clientReplica)
            {
                // 客户端：整段不跑玩家拾取（摘掉全部玩家）；水面音效/粒子由主机播过，
                // 副本不重复触发；外来拾取物在 base.Update 后清理。
                if (ParentField == null || !EnsurePlayersBound())
                {
                    m_filterFailures++;
                    if (!m_bindFailureLogged)
                    {
                        m_bindFailureLogged = true;
                        Log.Warning("[SuMP] SuSubsystemPickables 客户端拿不到 " +
                            "SubsystemPlayers.m_componentPlayers（反射失败 / ParentField 未注入）" +
                            "→ 本帧不跑 base.Update（宁可副本不动，也不让客户端本地拾取主机物品）");
                    }
                    return;
                }
                m_removedPlayers.Clear();
                m_removedIndices.Clear();
                for (int i = m_componentPlayers.Count - 1; i >= 0; i--)
                {
                    ComponentPlayer player = m_componentPlayers[i];
                    if (player == null) continue;
                    m_removedPlayers.Add(player);
                    m_removedIndices.Add(i);
                    m_componentPlayers.RemoveAt(i);
                }
                try
                {
                    // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemPickables.cs:136-145
                    ReadOnlyList<Pickable> pickables = Pickables;
                    for (int i = 0; i < pickables.Count; i++)
                        if (pickables[i] != null) pickables[i].SplashGenerated = true;
                    base.Update(dt);
                    Replica?.CullForeignClientPickables();
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
                return;
            }
            // 主机：把远端化身注入玩家列表（引擎 UpdateComponentPlayers 的重建会清掉化身，
            // 见 AvatarPlayersProvider 注释），窗口结束移除——引擎拾取循环因此对化身生效，
            // 与主机玩家同一段代码：磁吸 1.75m、飞行、1.0m 收取、背包入格、拾取音全在主机。
            if (AvatarPlayersProvider == null)
            {
                RunHostBaseUpdate(dt);
                return;
            }
            if (ParentField == null || !EnsurePlayersBound())
            {
                m_filterFailures++;
                if (!m_bindFailureLogged)
                {
                    m_bindFailureLogged = true;
                    Log.Warning("[SuMP] SuSubsystemPickables 主机拿不到 " +
                        "SubsystemPlayers.m_componentPlayers（反射失败 / ParentField 未注入）→ " +
                        "本帧按原生行为跑 base.Update（化身不在玩家表里，拾取不会替它们发生）");
                }
                RunHostBaseUpdate(dt);
                return;
            }
            m_injectedPlayers.Clear();
            try
            {
                foreach (ComponentPlayer player in AvatarPlayersProvider())
                {
                    if (player == null || m_componentPlayers.Contains(player)) continue;
                    // 不做额外的"掉落者宽限"：引擎自己的 0.5s 创建宽限对所有人一致
                    // （2026-10-26 用户口径：客户端丢出→吸回的节奏必须与主机玩家相同；
                    //   旧 1.5s 宽限是请求链时代为防"刚丢即回吸"加的，原生路径不需要）。
                    // 追加在本机玩家之后（分屏同款顺序：本机先判定，化身后判定）。
                    m_componentPlayers.Add(player);
                    m_injectedPlayers.Add(player);
                }
                // 窗口内新增的 WaterSplashParticleSystem 由 WaterSplashReplicaEngine 取出并广播；
                // 拾取窗口（PickupWindowSink）差分化身背包发通知；引擎原生表现不受影响。
                RunHostBaseUpdate(dt);
            }
            finally
            {
                for (int i = 0; i < m_injectedPlayers.Count; i++)
                    m_componentPlayers.Remove(m_injectedPlayers[i]);
                m_injectedPlayers.Clear();
            }
        }

        /// <summary>
        /// 主机 base.Update 包一层水花捕获窗口 + 拾取捕获窗口。捕获开始失败（适配层未启用 /
        /// 反射失败）时仍然执行 base.Update：主机原生水花 / 原生拾取表现绝不因为网络表现失败而消失。
        /// </summary>
        private void RunHostBaseUpdate(float dt)
        {
            IWaterSplashHostSink splashSink = SplashSink;
            bool capture = splashSink != null && splashSink.BeginSplashCapture(out _);
            IPickupHostWindowSink pickupSink = PickupWindowSink;
            pickupSink?.BeginPickupWindow();
            try
            {
                base.Update(dt);
            }
            finally
            {
                if (capture) splashSink.EndSplashCapture();
                pickupSink?.EndPickupWindow();
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

        /// <summary>
        /// 主机水花捕获窗口（Engine/WaterSplashReplicaEngine.cs 的 Pump 挂接）。窗口只包住
        /// <c>base.Update</c>：投射物入水时引擎创建的 <c>WaterSplashParticleSystem</c> 由适配层
        /// 做粒子表差集取出。客户端整段不跑 base.Update，因此不会打开窗口。
        /// Source: Survivalcraft/Game/SubsystemProjectiles.cs:383-390
        /// </summary>
        internal IWaterSplashHostSink SplashSink { get; set; }

        /// <summary>
        /// 主机投射物命中捕获窗口（Engine/ProjectileHitReplicaEngine.cs 的 Pump 挂接）。
        /// 窗口同样只包住 <c>base.Update</c>：引擎在这一段里执行 AttackBody（伤害数字 +
        /// 实际扣血），适配层用"命中前血量 vs 命中后血量"取出真实伤害。
        /// 客户端整段不跑 base.Update，因此永远不会打开窗口。
        /// Source: Survivalcraft/Game/SubsystemProjectiles.cs:221-240
        /// Source: Survivalcraft/Game/ComponentMiner.cs:506-526
        /// </summary>
        internal IProjectileHitHostSink HitSink { get; set; }

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
            // 主机：窗口内新增的 WaterSplashParticleSystem 由 WaterSplashReplicaEngine
            // 取出并广播；引擎原生表现（本机看到 / 听到的水花）不受影响。
            IWaterSplashHostSink sink = SplashSink;
            bool capture = sink != null && sink.BeginSplashCapture(out _);
            // 主机：命中伤害由 ProjectileHitReplicaEngine 在窗口前后做血量差取出，
            // 再按每个客户端的兴趣窗口中继（世界分区：远处命中不发给没载入那片地形的端）。
            IProjectileHitHostSink hitSink = HitSink;
            hitSink?.BeginHostHitCapture(dt);
            try
            {
                base.Update(dt);
            }
            finally
            {
                if (capture) sink.EndSplashCapture();
                hitSink?.EndHostHitCapture();
            }
        }
    }
}

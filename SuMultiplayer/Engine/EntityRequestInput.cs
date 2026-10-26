using System;
using Engine;
using Game;
using GameEntitySystem;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端丢弃输入的接收方（实现方 = <see cref="PickableReplicaEngine"/>）。
    /// 返回 true = 丢弃请求已入队发送（调用方随后必须吞掉引擎原生掉落）。
    /// </summary>
    public interface IEntityDropInputSink
    {
        /// <summary>
        /// 在 <c>ComponentInput.Update</c> 之后、<c>ComponentPlayer.Update</c> 之前采集丢弃意图。
        /// 这里**不扣物品**：扣物品只发生在主机回执之后。
        /// </summary>
        bool TryAcceptDrop(ComponentInput input, ComponentPlayer player);
    }

    /// <summary>
    /// 近战攻击输入的接收方（实现方 = <see cref="MeleeReplicaEngine"/>）。
    /// 返回 true = 这次攻击意图已被接管（调用方已经吞掉 <c>PlayerInput.Hit</c>）。
    ///
    /// 为什么必须在这里拦（与丢弃 / 交互同一条纪律）：引擎的攻击分支在
    /// <c>ComponentPlayer.Update</c> 里直接调 <c>ComponentMiner.Hit</c>（本地 roll 命中 +
    /// 本地 Injure），没有可覆写入口。客户端不允许本地决定命中 / 本地扣血，
    /// 所以：
    ///   · <c>ComponentInput.Update</c>（UpdateOrder.Input=-10）先把 Hit 置位；
    ///   · 本外壳在 base.Update 之后交给 sink；
    ///   · sink 只在**引擎等价身体射线命中身体**时采集目标 / 预测表现 / 上行请求，
    ///     并清空 <c>m_playerInput.Hit</c>；
    ///   · 随后 <c>ComponentPlayer.Update</c>（Default=0）读到 Hit = null，原生攻击整段不执行。
    ///
    /// 没有身体目标时 sink 必须返回 false 且**不得**清 Hit：左键按下同一帧还置了 Dig
    /// （Source: ComponentInput.cs:183-184），此时原生攻击分支本来就 no-op，紧接着的
    /// Dig 分支必须原样执行（Source: ComponentPlayer.cs:230-250）。这条规则对主机本机
    /// 角色与客户端完全一致，也是"挖掘时先响攻击声"回归的修复点。
    ///
    /// Source: Survivalcraft/Game/ComponentInput.cs:183-184（Dig / Hit 的射线）
    /// Source: Survivalcraft/Game/ComponentPlayer.cs:230-250（原生攻击 / 挖掘分支）
    /// </summary>
    public interface IHitInputSink
    {
        /// <summary>
        /// 在 <c>ComponentPlayer.Update</c> 之前采集一次攻击意图。
        /// 返回 false = 本帧不接管（模块未就绪 / 不是本机角色 / 没有射线 /
        /// 没有身体目标）→ 原生行为（挖掘照常）。
        /// </summary>
        bool TryAcceptHit(ComponentInput input, ComponentPlayer player);
    }

    /// <summary>
    /// 本地玩家输入组件的**薄外壳**（新文件，不改引擎源码；由组合根通过
    /// <c>Game.ComponentInput → SuMultiplayer.Engine.SuComponentInput</c> 的数据库注入替换）。
    ///
    /// 为什么必须在这里拦：引擎的丢弃分支在 <c>ComponentPlayer.Update</c> 里，
    /// 它先 <c>inventory.RemoveSlotItems(active, count)</c>（本地扣除）再
    /// <c>SubsystemPickables.AddPickable</c>（本地生成）。客户端不允许本地扣除 / 本地生成实体，
    /// 而在 ComponentPlayer.Update 里没有可覆写的入口（非 virtual）。因此：
    ///
    ///   · UpdateOrder.Input(-10) 的 ComponentInput.Update 先跑完，把 <c>PlayerInput.Drop</c> 置位；
    ///   · 本外壳在 base.Update 之后把 Drop 意图交给 <see cref="IEntityDropInputSink"/>；
    ///   · sink 发送 PickableDropRequest 成功后，把 <c>ComponentInput.m_playerInput.Drop</c> 清掉；
    ///   · 随后 UpdateOrder.Default(0) 的 ComponentPlayer.Update 读到的 Drop = false，
    ///     原生掉落分支整段不执行 → 客户端既不扣物品、也不生成会被清理的幽灵拾取物。
    ///
    /// 无静态状态：sink 是实例属性，由适配器在每帧 Pump 时按本机玩家写入。
    /// Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.Update（UpdateOrder.Input）
    /// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update（Drop 分支）
    /// </summary>
    public sealed class SuComponentInput : ComponentInput, IUpdateable
    {
        private bool m_faultLogged;
        private bool m_useFaultLogged;
        private bool m_hitFaultLogged;

        /// <summary>本帧攻击输入是否已被接管（诊断 / 自检用；不改变行为）。</summary>
        private bool m_hitAccepted;

        /// <summary>最近一次 Update 是否接管了攻击输入（只读诊断口径）。</summary>
        public bool HitInputAcceptedLastFrame => m_hitAccepted;

        /// <summary>
        /// 丢弃意图接收方（Engine/PickableReplicaEngine.cs 每帧写给本机玩家）。
        /// null = 没有联机会话 / 本端是主机 / 模块未上线：完全走原生行为。
        /// </summary>
        public IEntityDropInputSink DropSink { get; set; }

        /// <summary>
        /// 方块使用 / 交互意图接收方（Engine/ContainerReplicaEngine.cs 每帧写给本机玩家）。
        ///
        /// 与丢弃同一条纪律：客户端**不**在本地执行交互。sink 会提交 BlockUseRequest 并
        /// 通过 <c>ModFieldRef&lt;ComponentInput, PlayerInput&gt;</c> 把
        /// <c>m_playerInput.Interact</c> 清空，随后 UpdateOrder.Default 的
        /// <c>ComponentPlayer.Update</c> 读到 Interact = null → 原生 Use / Interact / Place
        /// 整段不执行。null = 没有联机会话 / 本端是主机 / 模块未上线：完全走原生行为。
        /// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update（Interact 分支）
        /// </summary>
        public IEntityUseInputSink UseSink { get; set; }

        /// <summary>
        /// 近战攻击意图接收方（Engine/MeleeReplicaEngine.cs 每帧写给本机玩家的输入组件）。
        /// 与 UseSink 同一条纪律：客户端**不**在本地执行攻击（不 roll 命中、不扣血），
        /// 只做预测表现并上行请求；主要求由主机回执确认 / 回滚。
        /// null = 没有联机会话 / 模块未上线：完全走原生行为。
        /// </summary>
        public IHitInputSink HitSink { get; set; }

        void IUpdateable.Update(float dt)
        {
            base.Update(dt);
            m_hitAccepted = false;
            ComponentPlayer player = Entity?.FindComponent<ComponentPlayer>();
            if (player == null) return;
            IEntityDropInputSink dropSink = DropSink;
            if (dropSink != null && this.PlayerInput.Drop)
            {
                try
                {
                    dropSink.TryAcceptDrop(this, player);
                }
                catch (Exception e)
                {
                    if (!m_faultLogged)
                    {
                        m_faultLogged = true;
                        Log.Error("[SuMP] 丢弃输入拦截异常：" + e.GetType().Name + " " + e.Message +
                            " → 本帧按原生行为处理（客户端可能本地扣除并生成被清理的拾取物；只记录一次）");
                    }
                }
            }
            // 引擎语义（ComponentPlayer.cs:151-242）：同一帧的 tap 同时携带 Interact + Hit，
            // **Use 优先**——手持物 OnUse 命中（马鞍装马）时 flag=true 挡住 Hit。镜像顺序必须
            // 一致：UseSink 在 HitSink 之前，UseSink 消费掉使用意图时会连 Hit 一起清。
            IEntityUseInputSink useSink = UseSink;
            if (useSink != null && this.PlayerInput.Interact.HasValue)
            {
                try
                {
                    useSink.TryAcceptInteract(this, player);
                }
                catch (Exception e)
                {
                    if (!m_useFaultLogged)
                    {
                        m_useFaultLogged = true;
                        Log.Error("[SuMP] 方块使用输入拦截异常：" + e.GetType().Name + " " +
                            e.Message + " → 本帧按原生行为处理（客户端可能本地执行一次交互；只记录一次）");
                    }
                }
            }
            IHitInputSink hitSink = HitSink;
            if (hitSink != null && this.PlayerInput.Hit.HasValue)
            {
                try
                {
                    if (hitSink.TryAcceptHit(this, player)) m_hitAccepted = true;
                }
                catch (Exception e)
                {
                    if (!m_hitFaultLogged)
                    {
                        m_hitFaultLogged = true;
                        Log.Error("[SuMP] 攻击输入拦截异常：" + e.GetType().Name + " " +
                            e.Message + " → 本帧按原生行为处理（本机可能本地 roll 一次命中；只记录一次）");
                    }
                }
            }
            // 世界控制已改由 SuComponentGui（Injector 替换 ComponentGui）同帧拦截：
            // 这里在 -10 抢点击会晚一帧（widget 的 IsClicked 每帧由 input.Click 现算），
            // 实测导致客户端本地先执行一次（2026-10-26 双重爆炸根因）。
        }
    }
}

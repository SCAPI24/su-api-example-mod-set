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

        void IUpdateable.Update(float dt)
        {
            base.Update(dt);
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
        }
    }
}

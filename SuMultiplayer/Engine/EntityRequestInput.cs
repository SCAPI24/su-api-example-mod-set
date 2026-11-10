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
    /// Shared-host input extension. The real ComponentInput runs once before network intent capture.
    /// Source: Survivalcraft/Game/ComponentInput.cs:Update; ComponentPlayer.cs:Update
    /// </summary>
    public sealed class SuComponentInputExtension : SuAPI.UpdateExtension<ComponentInput>
    {
        private readonly MultiplayerComponentExtensions m_owner;
        private bool m_hitAccepted;
        public bool HitInputAcceptedLastFrame => m_hitAccepted;
        public bool IsAttached { get; private set; }
        public IEntityDropInputSink DropSink { get; set; }
        public IEntityUseInputSink UseSink { get; set; }
        public IHitInputSink HitSink { get; set; }

        internal SuComponentInputExtension(ComponentInput host, MultiplayerComponentExtensions owner) : base(host)
        {
            m_owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public override void Attached() => IsAttached = true;

        public override void Update(float dt)
        {
            m_hitAccepted = false;
            // Bind the local player's sinks before its first actual simulation update, not one Frame.Update later.
            if (!m_owner.PrepareInput(Host)) ClearSinks();
            base.Update(dt);
        }

        public override void AfterUpdate(float dt, SuAPI.InvocationResult result)
        {
            // A veto leaves no newly produced input to send. Never replay last frame's PlayerInput.
            if (!result.HostExecuted) return;
            ComponentPlayer player = Host.Entity?.FindComponent<ComponentPlayer>();
            if (player == null) return;
            try
            {
                if (DropSink != null && Host.PlayerInput.Drop) DropSink.TryAcceptDrop(Host, player);
                // Preserve native Use-before-Hit ownership: a saddle/item use can consume Hit itself.
                if (UseSink != null && Host.PlayerInput.Interact.HasValue) UseSink.TryAcceptInteract(Host, player);
                if (HitSink != null && Host.PlayerInput.Hit.HasValue)
                    m_hitAccepted = HitSink.TryAcceptHit(Host, player);
            }
            catch (Exception error)
            {
                m_owner.ClearFailedInput(Host);
                throw new InvalidOperationException("[SuMP] Network input interception failed; native Drop/Interact/Hit were cleared for this update.", error);
            }
        }

        internal void ClearSinks() { DropSink = null; UseSink = null; HitSink = null; }

        public override void Detached()
        {
            IsAttached = false;
            m_owner.Release(this);
        }
    }
}

using System;
using Engine;
using Game;
using GameEntitySystem;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 世界控制意图的接收方（实现方 = <see cref="WorldControlEngine"/>）。
    /// 返回 true = 意图已提交为主机请求，并且本帧的按钮点击 / 键盘标志已被消费
    ///（<c>ComponentGui.HandleInput</c> 随后读到的全是 false，原生本地分支不执行）。
    /// </summary>
    public interface IWorldControlGuiSink
    {
        bool TryAcceptWorldControl(ComponentGui gui, ComponentPlayer player);
    }

    /// <summary>
    /// 乘骑意图的接收方（实现方 = Engine/MountReplicaEngine.cs，2026-10-26）。
    /// 返回 true = 乘骑边沿已采集并发为 MountRequest，本帧的
    /// <c>m_mountButtonWidget.IsClicked / playerInput.ToggleMount</c> 已被消费
    /// —— 原生 <c>ComponentGui.Update:582-589</c> 的本地 StartMounting/StartDismounting
    /// 整段不执行。客户端**绝不**本地挂 ParentBody：化身是否骑上由主机的 MountState 决定。
    /// </summary>
    public interface IMountGuiSink
    {
        bool TryAcceptMount(ComponentGui gui, ComponentPlayer player);
    }

    /// <summary>
    /// Independent controller for the real GUI Host; intent consumption precedes Host.Update.
    /// Source: Survivalcraft/Game/ComponentGui.cs:Update,HandleInput; ClickableWidget.cs:Update
    /// </summary>
    public sealed class SuComponentGuiExtension : SuAPI.UpdateExtension<ComponentGui>
    {
        private readonly MultiplayerComponentExtensions m_owner;
        public bool IsAttached { get; private set; }
        public IWorldControlGuiSink WorldControlSink { get; set; }
        public IMountGuiSink MountSink { get; set; }

        internal SuComponentGuiExtension(ComponentGui host, MultiplayerComponentExtensions owner) : base(host)
        {
            m_owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public override void Attached() => IsAttached = true;

        public override void Update(float dt)
        {
            if (!m_owner.PrepareGui(Host)) ClearSinks();
            ComponentPlayer player = Host.Entity?.FindComponent<ComponentPlayer>();
            WorldControlSink?.TryAcceptWorldControl(Host, player);
            MountSink?.TryAcceptMount(Host, player);
            // Request the real Host only after button/keyboard intent has been consumed.
            // Exceptions propagate to the strict runner instead of executing local client actions.
            base.Update(dt);
        }

        internal void ClearSinks() { WorldControlSink = null; MountSink = null; }

        public override void Detached()
        {
            IsAttached = false;
            m_owner.Release(this);
        }
    }
}

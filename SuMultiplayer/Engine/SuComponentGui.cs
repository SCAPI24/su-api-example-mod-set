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
    /// <c>Game.ComponentGui</c> 的**薄外壳**（由组合根通过 SuAPI Injector 的类名映射注入：
    /// <c>Game.ComponentGui → SuMultiplayer.Engine.SuComponentGui</c>，与
    /// <c>SuComponentInput</c> 替换 <c>ComponentInput</c> 同款；不改引擎源码）。
    ///
    /// 为什么必须替换 ComponentGui、而不是在 ComponentInput（-10）抢点击：
    /// <c>ClickableWidget.Update</c> 每帧**先清零再从当帧 input.Click 现算** IsClicked
    ///（Source: Survivalcraft/Game/ClickableWidget.cs:29-41），而组件更新顺序是
    /// ComponentInput(-10) → 组件树 Update（置位）→ ComponentGui.Update（读）。
    /// 在 -10 读到的是**上一帧**的残留 → 提交请求晚一帧，且消费不掉**当帧**的置位
    /// —— 实测（2026-10-26）：客户端本地先执行一次（本地全尺寸爆炸），主机又按请求
    /// 执行一次，客户端看到双份特效。替换点放在 ComponentGui 自己的 Update 里、
    /// <c>base.Update</c>（内部 <c>HandleInput</c> 读四按钮）**之前**，同帧闭环：
    /// widget 刚置位 → 采集 + 发请求 → 写回 false → HandleInput 读到 false →
    /// 本地分支整段不执行。
    ///
    /// 无 sink（主机 / 非客户端 / 模块未上线）时纯粹透传 base.Update，行为与原版一致。
    /// Source: Survivalcraft/Game/ComponentGui.cs:209-214（Update = HandleInput + UpdateWidgets）
    /// Source: Survivalcraft/Game/ComponentGui.cs:679-737（四按钮的引擎行为）
    /// </summary>
    public sealed class SuComponentGui : ComponentGui, IUpdateable
    {
        private bool m_faultLogged;
        private bool m_mountFaultLogged;

        /// <summary>
        /// 世界控制意图接收方（Engine/WorldControlEngine.cs 每帧写给本机客户端玩家）。
        /// null = 主机 / 非客户端 / 模块未上线：完全走原生行为。
        /// </summary>
        public IWorldControlGuiSink WorldControlSink { get; set; }

        /// <summary>
        /// 乘骑意图接收方（Engine/MountReplicaEngine.cs 每帧写给本机客户端玩家）。
        /// null = 主机 / 非客户端 / 模块未上线：完全走原生行为。
        /// </summary>
        public IMountGuiSink MountSink { get; set; }

        void IUpdateable.Update(float dt)
        {
            IWorldControlGuiSink sink = WorldControlSink;
            if (sink != null)
            {
                try
                {
                    sink.TryAcceptWorldControl(this, Entity?.FindComponent<ComponentPlayer>());
                }
                catch (Exception e)
                {
                    if (!m_faultLogged)
                    {
                        m_faultLogged = true;
                        Log.Error("[SuMP] 世界控制 GUI 拦截异常：" + e.GetType().Name + " " + e.Message +
                            " → 本帧按原生行为处理（客户端本地改动会被快照拉回；只记录一次）");
                    }
                }
            }
            IMountGuiSink mountSink = MountSink;
            if (mountSink != null)
            {
                try
                {
                    mountSink.TryAcceptMount(this, Entity?.FindComponent<ComponentPlayer>());
                }
                catch (Exception e)
                {
                    if (!m_mountFaultLogged)
                    {
                        m_mountFaultLogged = true;
                        Log.Error("[SuMP] 乘骑 GUI 拦截异常：" + e.GetType().Name + " " + e.Message +
                            " → 本帧按原生行为处理（本机可能本地挂一次 ParentBody；只记录一次）");
                    }
                }
            }
            base.Update(dt);
        }
    }
}

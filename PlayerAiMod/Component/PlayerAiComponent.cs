using Engine;
using Game;
using GameEntitySystem;
using System;
using TemplatesDatabase;

namespace PlayerAiMod
{
    /// <summary>
    /// 挂在 Player 实体上的「玩家 AI」组件（组件模板注册见 Plug/PlayerAiMod.cs）。
    ///
    /// 职责只有"装配与生命周期"：
    ///   找到玩家 → 确认可接管 → 建立 <see cref="AiActor"/>（模式层 + 行为树运行时 + 传感器/执行器）
    ///   → 注册到 <see cref="PlayerAiRuntime"/>。
    ///
    /// P0-8 起**这里不再启动任何示例行为**：接管只是把模式层从"未接管"推到"待机"，
    /// 具体行为由行为树包决定（`PlayerAiRuntime` 在世界就绪后装载 `StartupTreePackage`）。
    ///
    /// 接管条件（本阶段）：
    ///   · <see cref="PlayerAiConfig.OnlyLocalPlayer"/> 为真时**只接管本端玩家**（有 GameWidget 视图的那个）。
    ///     远端角色的操作必须在远端执行、本端复现；在主机端直接改远端角色会两边不同步。
    ///   · <see cref="PlayerAiConfig.AutoEnableLocalPlayer"/> 为真时，世界加载后自动接管本端玩家；
    ///     接管只把模式层推到"待机/运行树"，**具体行为由行为树包决定**
    ///     （`PlayerAiRuntime` 在世界就绪后装载 `PlayerAiConfig.StartupTreePackage`）。
    ///
    /// 注意：不会给"不打算接管的玩家"建 Actor —— 执行器共享 CmdBridgeMod 的注入器，
    /// 多一个不干活的 Actor 去 ReleaseAll 会把真正干活那个 Actor 按住的键放掉。
    ///
    /// Source: Mod/WatchMod/Subsystem/SuWatchComponent.cs（Component + IUpdateable 挂载模式）
    /// Source: Mod/../AGENTS.md —— 延迟初始化：Load 阶段世界与控件树可能还没就绪，检查放到 Update
    /// Source: Survivalcraft/Game/ComponentPlayer.cs:35（GameWidget => PlayerData.GameWidget）
    /// </summary>
    public class PlayerAiComponent : Component, IUpdateable
    {
        private bool m_enabledOnce;

        public UpdateOrder UpdateOrder
        {
            // 决策 tick 走 PlayerAiRuntime 的帧首调度，本组件只做装配，顺序无所谓。
            get { return UpdateOrder.Default; }
        }

        /// <summary>本组件所属的玩家。</summary>
        public ComponentPlayer Player { get; private set; }

        /// <summary>该玩家的 AI 角色（未接管时为 null）。</summary>
        public AiActor Actor { get; private set; }

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            Player = Entity.FindComponent<ComponentPlayer>(false);
        }

        void IUpdateable.Update(float dt)
        {
            if (Player == null)
                return;

            if (!PlayerAiConfig.Enabled)
            {
                if (Actor != null)
                    Release();
                return;
            }

            if (GameManager.Project == null)
                return;

            AiActor actor = EnsureActor();
            if (actor == null)
                return;

            if (PlayerAiConfig.AutoEnableLocalPlayer && actor.IsLocalPlayer && !m_enabledOnce)
            {
                m_enabledOnce = true;
                actor.Enable("component:auto");
            }
        }

        /// <summary>停用 AI：释放输入、停止状态机，但保留组件（下次 Update 还能再接管）。</summary>
        public void Disable()
        {
            if (Actor != null)
                Actor.Disable();
        }

        /// <summary>彻底移除角色（从运行时注销）。</summary>
        public void Release()
        {
            if (Actor == null)
                return;

            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime != null)
                runtime.Unregister(Actor);

            Actor = null;
        }

        private AiActor EnsureActor()
        {
            if (Actor != null)
                return Actor;

            if (PlayerAiConfig.OnlyLocalPlayer && !PlayerAiRuntime.IsLocalPlayerOf(Player))
                return null;

            var actor = new AiActor(Player, null, null,
                owner => PlayerAiBehaviour.Create("player:" + (owner.Name ?? "?")));
            Actor = actor;

            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime != null)
                runtime.Register(actor);

            Log.Information("[PlayerAi] actor attached: " + actor);
            return actor;
        }

    }
}

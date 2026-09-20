using System;
using Engine;

namespace PlayerAiMod
{
    /// <summary>
    /// **用手上的物品**（吃 / 喝 / 使用）：按住鼠标右键若干秒再松开，可以重复几次。
    ///
    /// 为什么它必须是一个独立任务，而不是拿 `Task.Interact` / `Task.PlaceBlock` 凑：
    ///   · `Task.PlaceBlock` 是"右键放在你正看的那个面上"——**没有面就放不出来**；
    ///   · `Task.Interact` 的判据是"对着某个格子或某个角色点"，**必须有目标**；
    ///   · 而吃东西 / 喝药水 / 用手上的工具**没有目标**：SC 的 `ComponentMiner.Use` 在没有命中
    ///     方块时走的就是"使用手上的物品"。硬塞给前两者要么给不出目标，要么判据不成立。
    ///
    /// **不猜"吃了有没有用"**：本节点的成功判据是"这一段按住做完了"（输入动作完成即成功，
    /// 和 `Task.SelectSlot` 一个道理）。想知道食物真涨了没，在它后面接一个条件节点去看
    /// `self.Food`（`Service.UpdateSelf` + 比较类装饰器）—— "输入动作负责做、只读观察负责判断"
    /// 是这套节点的分工，不把两件事混进同一个节点里。
    /// </summary>
    public sealed class BtUseItemTask : BtInteractionTaskBase
    {
        /// <summary>按住的时长（秒）。SC 里吃东西要按住一小会儿才生效，所以不是"点一下"。</summary>
        public float HoldSeconds { get; set; } = 1.2f;

        /// <summary>重复几次（背包里有好几份食物时用得上）。</summary>
        public int Repeat { get; set; } = 1;

        /// <summary>两次之间的间隔（秒）。</summary>
        public float Interval { get; set; } = 0.35f;

        private int m_done;

        public override string NodeType
        {
            get { return "Task.UseItem"; }
        }

        /// <summary>按住的这一小段必须跨帧，所以是潜在任务。</summary>
        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;

            Button = string.IsNullOrEmpty(Button) ? "right" : Button;
            m_done = 0;
            return Step(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            return Step(context);
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            Release(context);
        }

        private BtResult Step(BtContext context)
        {
            int total = Math.Max(1, Repeat);
            if (m_done >= total)
                return BtResult.Succeeded;

            if (context.Sensors != null && !context.Sensors.IsInputAccepted)
            {
                context.Warn("UseItem: input is not accepted right now (window unfocused?) - the hold will do nothing");
                return BtResult.Failed;
            }

            float hold = Math.Max(0.05f, HoldSeconds);
            float cycle = hold + Math.Max(0f, Interval);
            float elapsed = ActiveTime - m_done * cycle;

            // 按住阶段：一直保持按下（游戏看的是"键还按着"，不是"刚按了一下"）
            if (elapsed < hold)
            {
                Press(context, true);
                return BtResult.InProgress;
            }

            Release(context);                                  // 松开这一下，本轮的"使用"就在这里生效
            m_done++;
            context.Log("UseItem: hold " + m_done + "/" + total + " done (" + hold.ToString("0.00") + "s)");
            return m_done >= total ? BtResult.Succeeded : BtResult.InProgress;
        }
    }

    /// <summary>
    /// **跳**：脉冲一下跳跃键。可选"边按前进键边跳"（跳半格台阶 / 跳一道沟）。
    ///
    /// 为什么不能只靠 `Task.NavigateTo`：寻路里那一下跳是**跟着航点自动补的**，
    /// 而"原地跳上面前的台阶""跳过脚下的沟"这种动作，玩家是看着地形手动做的 ——
    /// 树里也得能手动表达一次跳跃（常常和 `Task.Mine` / `Task.PlaceBlock` 组合使用）。
    ///
    /// 成功判据同样是"动作做完了"（跳过没跳过、跳多高都是游戏说了算，AI 不去猜）。
    /// </summary>
    public sealed class BtJumpTask : BtTaskNode
    {
        /// <summary>跳跃键（`Keyboard` 名字）。</summary>
        public string Key { get; set; } = "space";

        /// <summary>跳几次。</summary>
        public int Times { get; set; } = 1;

        /// <summary>两次之间的间隔（秒）。SC 的跳跃有落地判定，间隔太小等于按了没用。</summary>
        public float Interval { get; set; } = 0.45f;

        /// <summary>跳的同时按住的前进键。</summary>
        public string ForwardKey { get; set; } = "w";

        /// <summary>是否同时按住前进键（跳台阶/跳沟用；原地跳用不上）。</summary>
        public bool AlsoForward { get; set; }

        private int m_done;

        public override string NodeType
        {
            get { return "Task.Jump"; }
        }

        public override bool IsLatent
        {
            get { return Times > 1 || AlsoForward; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;

            m_done = 0;
            if (AlsoForward && !string.IsNullOrEmpty(ForwardKey))
                context.Actuators.HoldKey(ForwardKey, true);
            return Step(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            return Step(context);
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            Release(context);
        }

        private BtResult Step(BtContext context)
        {
            if (m_done >= Math.Max(1, Times))
            {
                Release(context);
                context.Log("Jump: " + m_done + " jump(s) done");
                return BtResult.Succeeded;
            }

            if (context.Sensors != null && !context.Sensors.IsInputAccepted)
            {
                context.Warn("Jump: input is not accepted right now (window unfocused?)");
                Release(context);
                return BtResult.Failed;
            }

            if (m_done > 0 && ActiveTime < m_done * Math.Max(0f, Interval))
                return BtResult.InProgress;

            context.Actuators.PulseKey(Key, 80);
            m_done++;
            if (m_done >= Math.Max(1, Times))
            {
                Release(context);
                context.Log("Jump: " + m_done + " jump(s) done");
                return BtResult.Succeeded;
            }
            return BtResult.InProgress;
        }

        private void Release(BtContext context)
        {
            if (context.Actuators == null)
                return;
            if (AlsoForward && !string.IsNullOrEmpty(ForwardKey))
                context.Actuators.HoldKey(ForwardKey, false);
            context.Actuators.ReleaseAll();
        }
    }
}

using System;
using Engine;

namespace PlayerAiMod
{
    /// <summary>
    /// **世界交互任务族**：挖方块 / 攻击 / 交互 / 放方块 / 选快捷栏。
    ///
    /// 为什么这些不能用"直接改世界"实现：铁律是 AI 只能通过玩家控制器操作 ——
    /// 所以这里的每一步都是**真人做得到的动作**：转视角（`LookAt` 会同时算 yaw 与俯仰）、
    /// 按住/点鼠标左右键、按数字键与滚轮。区别只在于 AI 做得又快又准。
    ///
    /// 共同的"做完了吗"判据都取自**只读观察**，不去猜：
    ///   · 挖方块 → 再读一次那个格子，变成空气才算成功；
    ///   · 攻击 → 黑板里的目标键消失（生物服务找不到目标时会清键）才算成功。
    /// 这样"按了键但游戏没采纳"（窗口失焦、动作冷却）不会被误判成成功。
    /// </summary>
    public abstract class BtInteractionTaskBase : BtTaskNode
    {
        /// <summary>
        /// 鼠标键名（`left` / `right`，与 `IAiActuator.MouseButton` 一致）。
        ///
        /// **故意不给默认值**：各任务的默认键不一样（挖/攻击是左键，交互/放方块是右键），
        /// 基类先填个 `left` 会让子类的"空则取右键"判断永远不成立 —— 实测踩过：
        /// `Task.Interact` 点了左键（自检里 `button=left` 抓到的）。
        /// </summary>
        public string Button { get; set; }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected void Press(BtContext context, bool down)
        {
            if (context.Actuators != null)
                context.Actuators.MouseButton(Button, down);
        }

        protected void Release(BtContext context)
        {
            if (context.Actuators == null)
                return;
            context.Actuators.MouseButton(Button, false);
            context.Actuators.ReleaseAll();
        }

        /// <summary>读一个 int 黑板键（没有就是 default）。</summary>
        protected static int IntKey(BtContext context, string key, int fallback)
        {
            int value;
            return !string.IsNullOrEmpty(key) && context.Blackboard.TryGet(key, out value)
                ? value : fallback;
        }
    }

    /// <summary>
    /// **挖方块**：看向目标格子的中心，按住左键，直到那一格变成空气。
    ///
    /// 目标来自黑板里的三个 int 键（默认 `mineX/Y/Z`）—— 由 `Service.UpdateBlockAhead`
    /// （或后续的"找最近矿石"服务）写进去，于是"找 → 挖"能像搭积木一样拼起来。
    ///
    /// 为什么判据是"格子变空气"而不是"按了多久"：挖坚硬方块要好几秒，
    /// 而"按了键"不等于"挖掉了"（工具不对、方块在射程外、窗口失焦都会被游戏忽略）。
    /// </summary>
    public sealed class BtMineBlockTask : BtInteractionTaskBase
    {
        /// <summary>目标格子的三个坐标键。</summary>
        public string XKey { get; set; } = "mineX";
        public string YKey { get; set; } = "mineY";
        public string ZKey { get; set; } = "mineZ";

        /// <summary>超时判失败（秒）。</summary>
        public float TimeoutSeconds { get; set; } = 20f;

        /// <summary>方块已经在射程外时是否仍继续（一般不需要）。</summary>
        public bool RequireBlockPresent { get; set; } = true;

        private int m_x;
        private int m_y;
        private int m_z;
        private bool m_haveTarget;

        public override string NodeType
        {
            get { return "Task.Mine"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;

            Button = string.IsNullOrEmpty(Button) ? "left" : Button;
            m_x = IntKey(context, XKey, int.MinValue);
            m_y = IntKey(context, YKey, int.MinValue);
            m_z = IntKey(context, ZKey, int.MinValue);
            m_haveTarget = m_x != int.MinValue && m_y != int.MinValue && m_z != int.MinValue;
            if (!m_haveTarget)
            {
                context.Warn("Mine: blackboard has no target cell (" + XKey + "/" + YKey + "/" + ZKey + ") -> Failed");
                return BtResult.Failed;
            }
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
            IAiWorldSensor world = context.Sensors as IAiWorldSensor;
            int contents;
            string name;

            // 挖掉了没？读一次那一格（只读）
            if (world != null && world.TryPeekBlock(m_x, m_y, m_z, out contents, out name))
            {
                if (contents == 0)
                {
                    context.Log("Mine: (" + m_x + "," + m_y + "," + m_z + ") is now air -> Succeeded");
                    return BtResult.Succeeded;
                }
            }
            else if (RequireBlockPresent)
            {
                context.Warn("Mine: cannot read the target cell (world not ready?)");
                return BtResult.Failed;
            }

            if (ActiveTime > TimeoutSeconds)
            {
                context.Warn("Mine: timeout after " + ActiveTime.ToString("0.0") + "s at ("
                    + m_x + "," + m_y + "," + m_z + ")");
                return BtResult.Failed;
            }

            // 看向格子中心：LookAt 会连俯仰一起算好，游戏自己的挖掘射线才对准这一格
            context.Actuators.LookAt(new Vector3(m_x + 0.5f, m_y + 0.5f, m_z + 0.5f));
            Press(context, true);
            return BtResult.InProgress;
        }
    }

    /// <summary>
    /// **攻击**：面朝黑板里的目标 actor，按住左键；目标从黑板消失（死亡/走远被清键）就算成功。
    ///
    /// 和挖方块同一个套路：判据取自观察，而不是"按了多久"。
    /// </summary>
    public sealed class BtAttackTask : BtInteractionTaskBase
    {
        public string TargetKey { get; set; } = "target";

        /// <summary>超出这个距离就先靠近（0 = 不管距离，站原地打）。</summary>
        public float Range { get; set; } = 3.5f;

        /// <summary>看目标哪个高度（米）。</summary>
        public float EyeHeight { get; set; } = 1.2f;

        /// <summary>超时判失败（秒）。**&lt;= 0 = 不限时** —— 见 <see cref="HealthProbe"/>。</summary>
        public float TimeoutSeconds { get; set; } = 20f;

        /// <summary>
        /// 连点间隔（秒，0 = 一直按住）。
        ///
        /// ⚠️ **必须大于引擎的攻击冷却 0.66 秒**（`ComponentMiner.cs:295`：
        /// `if (!(GameTime - m_lastHitTime > 0.66f)) return;` —— 冷却内的挥击**整个被丢掉**，
        /// 不是"少打一点"）。默认原来写 0.6，正好每次都落在冷却里：2026-09-26 实测
        /// "打了半天动物血量恒为 1"，看着像没命中，其实是**一次都没生效**。
        /// Source: ComponentMiner.cs:295（冷却）、311（伤害 = 威力 × 攻击力 ÷ 韧性）
        /// </summary>
        public float ClickInterval { get; set; } = 0.75f;

        /// <summary>
        /// **边打边采目标血量**（0..1）：这是"打死没有"的唯一可信判据。
        ///
        /// 为什么不能只看"目标键没了"：键没了也可能是它跑出了搜索半径（或换目标）。
        /// 目标一死实体就被移除、血量就取不到了，所以必须在打的过程中一直采样，
        /// 收尾时用**最后一次采到的血量**判定（`ComponentHealth.Health` 到 0 即死亡）。
        /// Source: ComponentHealth.cs:40,267
        /// </summary>
        public bool HealthProbe { get; set; } = true;

        /// <summary>把"打死了吗"写进哪个黑板键（bool）。空 = 不写（报告交给 `Task.HuntReport`）。</summary>
        public string KilledKey { get; set; }

        private float m_nextClick;
        private float m_lastHealth = -1f;
        private bool m_sawHealth;
        private bool m_sawDamage;
        private double m_lastDamageTime;
        private string m_targetName;
        private float m_nextHealthLog;

        public override string NodeType
        {
            get { return "Task.Attack"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;
            Button = string.IsNullOrEmpty(Button) ? "left" : Button;
            m_nextClick = 0f;
            m_lastHealth = -1f;
            m_sawHealth = false;
            m_sawDamage = false;
            m_lastDamageTime = 0f;
            m_targetName = null;
            m_nextHealthLog = 0f;
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
            AiActorView target;
            if (context.Blackboard == null || !context.Blackboard.TryGet(TargetKey, out target))
                return Finish(context);                       // 目标没了：判"打死"还是"跑了"

            m_targetName = target.Name;
            if (HealthProbe)
            {
                // 目标还活着时每 tick 采一次；死了就再也采不到，所以最后采到的值就是死前的值
                IAiWorldSensor extras = BtSensorServices.Extras(context);
                float health;
                if (extras != null && extras.TryGetActorHealth(target, out health))
                {
                    bool dropped = m_sawHealth && health < m_lastHealth - 0.0005f;
                    if (dropped)
                    {
                        m_sawDamage = true;
                        m_lastDamageTime = context.Time;
                    }
                    m_lastHealth = health;
                    m_sawHealth = true;

                    // **血量归零就当场收工**：实体（尸体）可能还留在世界里好几秒，
                    // 只等"目标键消失"会一直对着尸体挥（2026-09-26 实测：血量 0 空挥 25 秒）。
                    if (health <= 0.001f)
                    {
                        context.Log("Attack: '" + (target.Name ?? TargetKey) + "' health reached 0 -> KILLED");
                        if (!string.IsNullOrEmpty(KilledKey) && context.Blackboard != null)
                            context.Blackboard.Set(new AiBlackboardKey<bool>(KilledKey), true);
                        return BtResult.Succeeded;
                    }

                    // 限频血量回传（最多每秒一条）：**看得出"到底打中没有"**。
                    // 没有这条，"打了半天不死"就分不清是"伤害太低"还是"根本没挥中"（2026-09-26 实测踩到）。
                    if (ActiveTime >= m_nextHealthLog)
                    {
                        m_nextHealthLog = ActiveTime + 1f;
                        context.Log("Attack: '" + (target.Name ?? TargetKey) + "' health="
                            + health.ToString("0.###") + (dropped ? " (dropping)" : string.Empty)
                            + " dist=" + target.Distance.ToString("0.00"));
                    }
                }
            }

            // `<= 0` = 不限时：打猎时"打没打死"由血量判定，不由钟表判定（用户明确要求）
            if (TimeoutSeconds > 0f && ActiveTime > TimeoutSeconds)
            {
                context.Warn("Attack: timeout after " + ActiveTime.ToString("0.0") + "s, distance="
                    + target.Distance.ToString("0.00") + ", health="
                    + (m_sawHealth ? m_lastHealth.ToString("0.00") : "?"));
                return BtResult.Failed;
            }

            context.Actuators.LookAt(target.Position + new Vector3(0f, EyeHeight, 0f));

            if (Range > 0f && target.Distance > Range)
            {
                // 够不着：先往前走（真正"追上去"由 Task.NavigateTo 负责，这里只保证不会站着空挥）
                context.Actuators.HoldKey("w", true);
                return BtResult.InProgress;
            }

            context.Actuators.HoldKey("w", false);

            if (ClickInterval <= 0f)
            {
                Press(context, true);
                return BtResult.InProgress;
            }

            // 连点：SC 的攻击是"按一下挥一次"，按住并不会连续挥
            if (ActiveTime >= m_nextClick)
            {
                m_nextClick = ActiveTime + ClickInterval;
                context.Actuators.MouseClick(Button, 40);
            }
            return BtResult.InProgress;
        }

        /// <summary>
        /// 目标从黑板消失时的收尾：**用最后采到的血量判定打死了没有**，写进 `KilledKey`。
        ///
        /// ⚠️ 为什么不能只判"血量 == 0"：**致命那一击落地时实体就已经被移除了**，
        /// 于是我们最后一次采到的值是"死前的血量"（比如 0.15），永远等不到 0。
        /// 所以分两级：
        ///   · **确定**：亲眼采到血量归零；
        ///   · **推定**：确实打掉过它的血（`m_sawDamage`）且最后血量已经很低、掉血还在 2.5 秒内
        ///     —— 致命一击就在眼前，实体随即消失，这只能是死了。
        /// 两者都不是（没采到样本 / 血量还很高就消失）才报"没确认打死"（它跑出了搜索半径）。
        ///
        /// 三种结局都算 `Succeeded`（"打完了"不是失败），日志把话说清楚。
        /// </summary>
        private BtResult Finish(BtContext context)
        {
            bool certain = m_sawHealth && m_lastHealth <= 0.02f;
            bool probable = m_sawHealth && m_sawDamage && m_lastHealth <= 0.35f
                && (context.Time - m_lastDamageTime) <= 2.5;
            bool killed = HealthProbe && (certain || probable);
            if (!string.IsNullOrEmpty(KilledKey) && context.Blackboard != null)
                context.Blackboard.Set(new AiBlackboardKey<bool>(KilledKey), killed);

            string name = string.IsNullOrEmpty(m_targetName) ? TargetKey : m_targetName;
            if (killed && certain)
                context.Log("Attack: '" + name + "' health reached 0 -> KILLED");
            else if (killed)
                context.Log("Attack: '" + name + "' went down right after taking damage (last health "
                    + m_lastHealth.ToString("0.###") + ") -> KILLED");
            else if (m_sawHealth)
                context.Log("Attack: '" + name + "' is gone (last health " + m_lastHealth.ToString("0.###")
                    + ", no recent damage) -> not confirmed dead");
            else
                context.Log("Attack: target '" + TargetKey + "' is gone -> Succeeded");
            return BtResult.Succeeded;
        }
    }

    /// <summary>
    /// **交互**（右键）：对黑板坐标上的方块或黑板里的目标 actor 点一次右键 ——
    /// 开门 / 用工作台 / 上马 / 喂动物 / 收容器 都是这个动作。
    /// 点完即成功（游戏侧对"能不能交互"自有裁决；真要等结果就用后面的条件装饰器去判断）。
    /// </summary>
    public sealed class BtInteractTask : BtInteractionTaskBase
    {
        /// <summary>用哪个来源：`cell`（黑板坐标）或 `actor`（黑板里的目标）。</summary>
        public string Source { get; set; } = "cell";

        public string TargetKey { get; set; } = "target";

        public string XKey { get; set; } = "interactX";
        public string YKey { get; set; } = "interactY";
        public string ZKey { get; set; } = "interactZ";

        /// <summary>看向 actor 时的高度（米）。</summary>
        public float EyeHeight { get; set; } = 1.0f;

        /// <summary>点几次（有些交互要双击，例如上马）。</summary>
        public int Repeat { get; set; } = 1;

        /// <summary>两次点击之间的间隔（秒）。</summary>
        public float Interval { get; set; } = 0.35f;

        private int m_done;

        public override string NodeType
        {
            get { return "Task.Interact"; }
        }

        public override bool IsLatent
        {
            get { return Repeat > 1 || Interval > 0f; }
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

        private BtResult Step(BtContext context)
        {
            if (m_done >= Math.Max(1, Repeat))
                return BtResult.Succeeded;

            if (Source == "actor")
            {
                AiActorView target;
                if (context.Blackboard == null || !context.Blackboard.TryGet(TargetKey, out target))
                    return BtResult.Failed;
                context.Actuators.LookAt(target.Position + new Vector3(0f, EyeHeight, 0f));
            }
            else
            {
                int x = IntKey(context, XKey, int.MinValue);
                int y = IntKey(context, YKey, int.MinValue);
                int z = IntKey(context, ZKey, int.MinValue);
                if (x == int.MinValue || y == int.MinValue || z == int.MinValue)
                {
                    context.Warn("Interact: blackboard has no target cell (" + XKey + "/" + YKey + "/" + ZKey + ")");
                    return BtResult.Failed;
                }
                context.Actuators.LookAt(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            }

            if (m_done > 0 && ActiveTime < m_done * Math.Max(Interval, 0f))
                return BtResult.InProgress;                   // 还没到下一次点击的时间

            context.Actuators.MouseClick(Button, 40);
            m_done++;
            return m_done >= Math.Max(1, Repeat) ? BtResult.Succeeded : BtResult.InProgress;
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            Release(context);
        }
    }

    /// <summary>
    /// **放方块**：看向目标格子的中心，点右键（游戏会把方块放在你正看的那个面上）。
    /// 手上得有方块 —— 用 `Task.SelectSlot` 先切到放方块的那一格。
    ///
    /// **游戏侧的动作冷却 = 0.33 秒**（`ComponentPlayer.cs:151/230/243` 用同一个
    /// `m_lastActionTime` 管住"交互/攻击/挖掘"）：所以连着两个 `PlaceBlock` 节点在同一帧里
    /// 各点一次右键时，**第二下会被直接忽略** —— 真机验收里"放 3 块墙只立起来 1 块"就是这么来的
    /// （§9.5.43）。要么把 `repeat` 配好，要么在节点之间插 `Task.Wait`（≥0.4s），
    /// 别指望连续点击会被游戏照单全收。
    /// </summary>
    public sealed class BtPlaceBlockTask : BtInteractionTaskBase
    {
        public string XKey { get; set; } = "placeX";
        public string YKey { get; set; } = "placeY";
        public string ZKey { get; set; } = "placeZ";

        public int Repeat { get; set; } = 1;

        /// <summary>两次之间的间隔（秒）。默认 0.4 &gt; 游戏的 0.33s 动作冷却。</summary>
        public float Interval { get; set; } = 0.4f;

        private int m_done;

        public override string NodeType
        {
            get { return "Task.PlaceBlock"; }
        }

        public override bool IsLatent
        {
            get { return Repeat > 1; }
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
            if (m_done >= Math.Max(1, Repeat))
                return BtResult.Succeeded;

            int x = IntKey(context, XKey, int.MinValue);
            int y = IntKey(context, YKey, int.MinValue);
            int z = IntKey(context, ZKey, int.MinValue);
            if (x == int.MinValue || y == int.MinValue || z == int.MinValue)
            {
                context.Warn("PlaceBlock: blackboard has no target cell (" + XKey + "/" + YKey + "/" + ZKey + ")");
                return BtResult.Failed;
            }

            context.Actuators.LookAt(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            if (m_done > 0 && ActiveTime < m_done * Math.Max(Interval, 0f))
                return BtResult.InProgress;

            context.Actuators.MouseClick(Button, 40);
            m_done++;
            return m_done >= Math.Max(1, Repeat) ? BtResult.Succeeded : BtResult.InProgress;
        }
    }

    /// <summary>
    /// **选快捷栏**：按数字键切到第 N 格，或用滚轮滚。放方块/吃东西/换工具之前都要先做它。
    /// 纯输入动作，成功与否游戏侧说了算，所以点完即成功。
    /// </summary>
    public sealed class BtSelectSlotTask : BtTaskNode
    {
        /// <summary>快捷栏槽位（1..9）；0 = 用滚轮。</summary>
        public int Slot { get; set; } = 1;

        /// <summary>滚轮格数（>0 向上/向左，<0 向下/向右）。</summary>
        public int Scroll { get; set; }

        public override string NodeType
        {
            get { return "Task.SelectSlot"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;

            if (Slot > 0)
                context.Actuators.PulseKey(Slot.ToString(System.Globalization.CultureInfo.InvariantCulture), 40);
            if (Scroll != 0)
                context.Actuators.Wheel(Scroll);

            context.Log("SelectSlot: slot=" + Slot + " scroll=" + Scroll);
            return BtResult.Succeeded;
        }
    }
}

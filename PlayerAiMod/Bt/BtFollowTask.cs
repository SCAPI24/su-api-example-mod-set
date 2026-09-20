using System;
using Engine;

namespace PlayerAiMod
{
    /// <summary>
    /// **跟随某个角色**：先靠近，然后"尽量踩着他走过的格子"，走不通就借游戏 A* 绕过去，
    /// 全程保持 <see cref="KeepDistance"/> 格**水平**间隔，**镜头一直锁在他身上**。
    ///
    /// 为什么是**一个**任务而不是三个（靠近 / 踩脚印 / 绕过去）：这三种其实是同一个闭环在不同
    /// 条件下的分支，拆开就得靠树去拼状态，反而更难保证"保持 2 格"这种连续约束：
    ///
    ///   · 目标还没有足迹（刚出现、或在远处）→ 走 A* 过去（这就是"先靠近"）；
    ///   · 目标在自己前面走过一串格子 → **追下一个还没踩过的脚印**（走他走的路，最省事也最像人）；
    ///   · 脚印太旧 / 追着追着走不动（卡住）→ **丢掉脚印，改走 A***（这就是"不行就绕过去"）；
    ///   · 水平距离已经 ≤ <see cref="KeepDistance"/> → **站住**（照样盯着他），不往人身上挤。
    ///
    /// 用户对"跟随"的要求原话："跟随角色走，只需要镜头一直锁在身上就行了" —— 所以
    /// **相机朝向与走路方向彻底解耦**：视线每 tick 都落在目标身上（<see cref="WalkToward"/>），
    /// 走路方向由 W/A/S/D 在**身体基**上的投影决定，而不是靠转视角。镜头因此不会出现
    /// "盯目标 ↔ 盯脚印"来回切造成的抖动。
    ///
    /// 铁律不变：A* 只是**只读查询**（借游戏自己的寻路器），跟随只靠**注入输入**
    /// （转视角 + 按住方向键 + 必要时跳一下）。目标丢失即失败，由树去决定怎么办。
    ///
    /// 为什么没有直接复用 `Task.NavigateTo` 的内部实现：跟随的终点**每一帧都在动**，
    /// 重算时机、到达判据（水平距离）、以及"脚印优先"这一层都和"走到某个固定点"不同；
    /// 两边共用的是同一套传感器 API（`TryRequestPath/GetPathStatus/CopyPath/ClearPath`）。
    /// </summary>
    public sealed class BtFollowEntityTask : BtTaskNode
    {
        // ---------------------------------------------------------------- 参数

        /// <summary>跟谁（黑板 actor 键）。</summary>
        public string TargetKey { get; set; } = "target";

        /// <summary>保持的水平间隔（米/格）。到了这个距离就站住，不再往前挤。</summary>
        public float KeepDistance { get; set; } = 2f;

        /// <summary>
        /// **站↔走的迟滞**（米）：从"站住"转成"走"要求超过 `keepDistance + 这个值`。
        ///
        /// 为什么必须有：没有迟滞时，距离在 `keepDistance` 上下抖一下就会每 tick 在
        /// "站住"和"走"之间来回跳 —— 走路键每帧按下/松开会让**步伐动画与视点起伏**
        /// （`ComponentHumanModel.CalculateEyePosition` 里的 `2f * Bob`）反复重启，人看起来在抽搐。
        /// （画面朝向不再受影响：镜头**永远**锁在目标身上，见 <see cref="WalkToward"/>。）
        /// </summary>
        public float StandHysteresis { get; set; } = 0.75f;

        /// <summary>
        /// 跟随方式：`auto`（默认，脚印优先、不行走 A*）/ `trail`（只用脚印，没脚印就朝目标直走）
        /// / `path`（只用 A*，始终绕过去）。
        /// </summary>
        public string Mode { get; set; } = "auto";

        /// <summary>足迹链最多记多少个格子（环形缓冲）。</summary>
        public int TrailLength { get; set; } = 64;

        /// <summary>脚印的有效期（秒）：太旧就不用（他早就走远了，照着旧脚印走会南辕北辙）。</summary>
        public float TrailMaxAge { get; set; } = 3f;

        /// <summary>走到脚印多近算"踩上了"（米）。</summary>
        public float TrailCellRadius { get; set; } = 0.9f;

        /// <summary>追脚印时多久没挪窝算卡住（秒）→ 丢脚印改走 A*。</summary>
        public float TrailStuckSeconds { get; set; } = 1.5f;

        /// <summary>卡住判定的位移阈值（米）。</summary>
        public float TrailStuckDistance { get; set; } = 0.3f;

        public string ForwardKey { get; set; } = "w";

        /// <summary>后退键（走路方向在身体后方时用；SC 里按 S 速度打 6 折）。</summary>
        public string BackKey { get; set; } = "s";

        /// <summary>左平移键（走路方向在身体左侧时用：镜头锁着人，脚下横着走）。</summary>
        public string LeftKey { get; set; } = "a";

        /// <summary>右平移键。</summary>
        public string RightKey { get; set; } = "d";

        public string JumpKey { get; set; } = "space";

        /// <summary>脚印/航点比脚下高多少才跳（米）。</summary>
        public float JumpHeight { get; set; } = 0.6f;

        public float EyeHeight { get; set; } = 1.35f;

        /// <summary>A* 重算间隔（秒）。</summary>
        public float RepathSeconds { get; set; } = 0.75f;

        /// <summary>目标移动超过这个距离立刻重算（米）。</summary>
        public float RepathMoveThreshold { get; set; } = 1.5f;

        public int MaxPositionsToCheck { get; set; } = 500;

        /// <summary>跟随超时（秒）；0 = 不超时（跟随本来就是持续行为）。</summary>
        public float TimeoutSeconds { get; set; }

        public override string NodeType
        {
            get { return "Task.FollowEntity"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        /// <summary>
        /// **这一 tick 实际在往哪走**（脚印 / 航点 / 目标本身）。
        /// 与"视线落点"是两件事：镜头永远盯目标，脚下走的可能是脚印 —— 自检靠它钉住走路语义。
        /// </summary>
        internal Vector3 LastAim { get; private set; }

        // ---------------------------------------------------------------- 状态

        private const int MaxWaypoints = 256;

        /// <summary>两次 A* 请求之间的硬下限（秒）——防"空路径"时每帧重算把游戏寻路队列打满。</summary>
        private const float MinRequestInterval = 0.25f;

        /// <summary>把方向量化到 8 个罗盘方向的门限：sin(22.5°) = 0.38268。单位向量必有一个分量超过它。</summary>
        private const float KeyAxisThreshold = 0.38268f;

        /// <summary>
        /// 走路键的重发间隔（秒）。游戏的按下状态本来是持久的
        /// （Source: Engine/Engine/Input/Keyboard.cs:128-130 `BeforeFrame` 是空的，
        /// 只有 `AfterFrame` 清 `downOnce`），但**真人**在游戏窗口里按/放同一个键、
        /// 或输入层被 `Keyboard.Clear()` 清掉时，按住状态会丢 —— 定期重发就能自愈。
        /// </summary>
        private const float KeyReassertSeconds = 1f;

        private readonly Vector3[] m_waypoints = new Vector3[MaxWaypoints];
        private readonly Vector3[] m_trailCells = new Vector3[256];
        private readonly double[] m_trailTimes = new double[256];

        private int m_waypointCount;
        private int m_waypointIndex;
        private float m_timeSinceRequest = float.MaxValue;
        private Vector3 m_requestedTarget;
        private float m_lastStuckCheckTime;
        private Vector3 m_lastStuckCheckPosition;
        private bool m_warnedUnfocused;
        private bool m_usedPath;                                // 这一段时间走的是 A* 还是脚印
        private bool m_legNoted;
        private bool m_trailDroppedForStuck;                    // 本轮卡住已经丢过脚印并报过警
        private bool m_walking;                                 // 站↔走的迟滞状态
        private int m_trailTail;                                // 最旧的一格
        private int m_trailCount;
        private int m_trailNext;                                // 下一个还没踩过的脚印（相对 tail 的偏移）

        /// <summary>四个移动键**已经下发**的状态（只在下发变化时才碰执行器）。顺序 0=前 1=后 2=左 3=右。</summary>
        private readonly bool[] m_moveKeyDown = new bool[4];

        /// <summary>下一次触碰移动键时强制重发一遍（自愈用，见 <see cref="KeyReassertSeconds"/>）。</summary>
        private bool m_forceMoveKeys = true;

        private float m_keyReassertTimer;

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;

            m_waypointCount = 0;
            m_waypointIndex = 0;
            m_timeSinceRequest = float.MaxValue;
            m_requestedTarget = Vector3.Zero;
            m_warnedUnfocused = false;
            m_trailTail = 0;
            m_trailCount = 0;
            m_trailNext = 0;
            m_walking = false;
            m_trailDroppedForStuck = false;
            m_lastStuckCheckTime = 0f;
            m_lastStuckCheckPosition = context.Sensors.Position;
            for (int i = 0; i < m_moveKeyDown.Length; i++)
                m_moveKeyDown[i] = false;
            m_forceMoveKeys = true;
            m_keyReassertTimer = KeyReassertSeconds;
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
            if (context.Blackboard == null
                || !context.Blackboard.TryGet(TargetKey, out target))
            {
                context.Warn("FollowEntity: blackboard target '" + TargetKey + "' missing -> Failed");
                return BtResult.Failed;
            }

            if (TimeoutSeconds > 0f && ActiveTime > TimeoutSeconds)
            {
                context.Warn("FollowEntity: timeout after " + ActiveTime.ToString("0.0") + "s");
                return BtResult.Failed;
            }

            if (context.Sensors != null && !context.Sensors.IsInputAccepted && !m_warnedUnfocused)
            {
                m_warnedUnfocused = true;
                context.Warn("FollowEntity: input is not accepted right now (window unfocused?) - "
                    + "movement may not happen");
            }

            target = RefreshTarget(context, target);

            m_timeSinceRequest += context.DeltaTime;
            Vector3 self = context.Sensors.Position;
            Vector3 targetPosition = target.Position;

            // 卡住判定放在**记脚印之前**：卡住且已经丢过脚印时就不再记，
            // 否则"丢掉 → 下一 tick 又记回来 → 再丢"会变成每帧一次告警
            //（真机实测：Game.log 里这条告警刷了 2692 行）。
            bool stuck = IsStuck(self, TrailStuckSeconds, TrailStuckDistance);
            if (!stuck)
                m_trailDroppedForStuck = false;
            if (!(stuck && m_trailDroppedForStuck))
            {
                RecordTrail(context, targetPosition);
                ExpireTrail(context);
            }

            // ---- 水平间隔：够近了就站住（不看路，但照样盯着他）
            // 站↔走之间加**迟滞**：否则距离在 keepDistance 上抖一下就会每 tick 翻转，
            // 走路键每帧按下/松开会让步伐动画与视点起伏反复重启（画面抽搐）。
            float horizontal = HorizontalDistance(self, targetPosition);
            bool walk = m_walking
                ? horizontal > KeepDistance
                : horizontal > KeepDistance + Math.Max(0f, StandHysteresis);
            m_walking = walk;

            if (!walk)
            {
                LastAim = self;                                 // 站住 = 不走（自检据此断言"没在走"）
                ReleaseMoveKeys(context);
                context.Actuators.LookAt(targetPosition + new Vector3(0f, EyeHeight, 0f));
                return BtResult.InProgress;
            }

            // ---- 脚印优先（auto / trail）
            if (!string.Equals(Mode, "path", StringComparison.OrdinalIgnoreCase))
            {
                Vector3 footstep;
                if (!stuck && TryNextFootstep(self, out footstep))
                {
                    NoteLeg(context, false, "walking his footsteps");
                    WalkToward(context, footstep, targetPosition, self);
                    return BtResult.InProgress;
                }

                if (stuck && m_trailCount > 0)
                {
                    // 追脚印追不动了 —— **丢掉脚印改走 A***（"不行的时候绕着过去"）。
                    // 告警**每次卡住只报一次**（热路径不许刷日志）。
                    if (!m_trailDroppedForStuck)
                    {
                        m_trailDroppedForStuck = true;
                        context.Warn("FollowEntity: stuck while walking the trail -> drop it and use A*");
                    }
                    m_trailCount = 0;
                    m_trailNext = 0;
                }

                if (string.Equals(Mode, "trail", StringComparison.OrdinalIgnoreCase))
                {
                    // 只用脚印：没脚印（或走不动）就朝目标直走 —— 用户明确选了这种方式，不借 A*
                    NoteLeg(context, false, "no footstep left, walking straight at him (mode=trail)");
                    WalkToward(context, targetPosition, targetPosition, self);
                    return BtResult.InProgress;
                }
            }

            // ---- A* 那条腿（靠近 / 绕过去）
            FollowPath(context, targetPosition, self);
            return BtResult.InProgress;
        }

        // ---------------------------------------------------------------- 目标快照刷新

        /// <summary>
        /// 把黑板里的目标**快照**换成"这一帧的真位置"。
        ///
        /// 为什么必须做：黑板里存的是 `AiActorView`（**值类型快照**），它由树上的
        /// `Service.UpdateNearestPlayer` 按 `interval` 写入 —— 树里写的是 0.2s 的话，
        /// 跟随看到的永远是 0.2 秒前的位置，而 `LookAt(那个位置)` 就只能**一顿一顿地追**
        /// （用户实测原话："视角跟踪有些不连贯，一段一段地在跳"）。
        /// 任务自己每 tick 用传感器按名字重取一次，就把"服务间隔"这个坑从跟随里摘掉了 ——
        /// 树怎么写都不会再让镜头跟着阶梯走。
        ///
        /// 取不到（玩家下线 / 超出范围）时**保留快照**：让外层（黑板装饰器 / `timeout`）去决定放弃，
        /// 这里不做"目标消失"的判断。
        /// </summary>
        private static AiActorView RefreshTarget(BtContext context, AiActorView snapshot)
        {
            IAiSensor sensors = context.Sensors;
            if (sensors == null || !sensors.IsReady || string.IsNullOrEmpty(snapshot.Name))
                return snapshot;

            AiActorView live;
            if (snapshot.IsPlayer || string.Equals(snapshot.Kind, "player", StringComparison.Ordinal))
                return sensors.TryFindPlayer(snapshot.Name, out live) ? live : snapshot;

            // 非玩家（生物）：按"快照位置附近 + 同名"再取一次 —— 拿不到就继续用快照
            IAiWorldSensor world = sensors as IAiWorldSensor;
            if (world != null
                && world.TryFindNearestCreature(snapshot.CategoryMask, snapshot.Distance + 4f, out live)
                && string.Equals(live.Name, snapshot.Name, StringComparison.Ordinal))
            {
                return live;
            }
            return snapshot;
        }

        // ---------------------------------------------------------------- 足迹链

        /// <summary>把目标当前所在格子记进足迹链（格子换了才记一条）。</summary>
        private void RecordTrail(BtContext context, Vector3 targetPosition)
        {
            Vector3 cell = CellCentre(targetPosition);
            if (m_trailCount > 0)
            {
                int last = (m_trailTail + m_trailCount - 1) % m_trailCells.Length;
                if (SameCell(m_trailCells[last], cell))
                    return;                                     // 还在同一格里，不重复记
            }

            int capacity = Math.Min(m_trailCells.Length, Math.Max(4, TrailLength));
            if (m_trailCount >= capacity)
            {
                // 满了：丢最旧的一格（环形推进）
                m_trailTail = (m_trailTail + 1) % m_trailCells.Length;
                m_trailCount--;
                if (m_trailNext > 0)
                    m_trailNext--;
            }

            int slot = (m_trailTail + m_trailCount) % m_trailCells.Length;
            m_trailCells[slot] = cell;
            m_trailTimes[slot] = context.Time;
            m_trailCount++;
        }

        /// <summary>丢掉过期的脚印（他早就走远了，照旧脚印走会南辕北辙）。</summary>
        private void ExpireTrail(BtContext context)
        {
            double oldestAllowed = context.Time - Math.Max(0f, TrailMaxAge);
            while (m_trailCount > 0)
            {
                if (m_trailTimes[m_trailTail] >= oldestAllowed)
                    break;
                m_trailTail = (m_trailTail + 1) % m_trailCells.Length;
                m_trailCount--;
                if (m_trailNext > 0)
                    m_trailNext--;
            }
        }

        /// <summary>取"下一个还没踩过的脚印"：已经踩到的（在半径内）先消费掉。</summary>
        private bool TryNextFootstep(Vector3 self, out Vector3 footstep)
        {
            footstep = Vector3.Zero;
            while (m_trailNext < m_trailCount)
            {
                Vector3 cell = m_trailCells[(m_trailTail + m_trailNext) % m_trailCells.Length];
                if (HorizontalDistance(self, cell) <= TrailCellRadius)
                {
                    m_trailNext++;                              // 这一格踩过了，往后挪
                    continue;
                }
                footstep = cell;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 两条腿

        /// <summary>
        /// 朝 <paramref name="aim"/> 走，**镜头每 tick 都锁在目标身上**（用户明确要求：
        /// "跟随角色走，只需要镜头一直锁在身上就行了"）。
        ///
        /// 为什么"脚下走 A、眼睛看 B"是可行的 —— SC 里走路的按键方向是**身体朝向**，不是相机朝向：
        ///   · 走路方向 = `WalkOrder.X * 身体右 + WalkOrder.Y * 身体前`（都取水平分量）
        ///     Source: Survivalcraft/Game/ComponentLocomotion.cs:400-401,452
        ///   · 相机 = 身体朝向 × 头部偏航 `LookAngles.X`，而玩家的 `LookAngles.X` 恒为 0
        ///     Source: ComponentCreatureModel.cs:206-209 / ComponentHumanModel.cs:442-449
        /// 所以把"该往世界哪个方向走"投影到**身体基**（前/右）上，再按 8 个罗盘方向里最近的那个
        /// 按住 W/A/S/D 组合，就能在相机完全不动的前提下走出任意方向。
        ///
        /// 为什么不"身体朝走路方向、只把视线偏过去"：玩家的水平视线**就是**身体朝向
        /// （Source: ComponentLocomotion.cs:302-309 玩家按鼠标 X 转的是身体 yaw），
        /// 而头部偏航 `LookAngles.X` 会被**自动回正** —— 玩家模板只关了 `LookAutoLevelY`，
        /// `LookAutoLevelX` 仍是默认 true，每帧按 10/s 往 0 拉（Source: ComponentLocomotion.cs:352
        /// + Pak/Database.xml:389-398）。写它等于每帧跟引擎拔河，画面只会更抖。
        ///
        /// 代价：走路方向最多差 22.5°（8 方向量化），且方向在身体**后方**时得按 S（SC 里速度 6 折）。
        /// 这两点都只影响走位，不影响镜头 —— 而用户要的正是镜头稳。
        /// </summary>
        private void WalkToward(BtContext context, Vector3 aim, Vector3 targetPosition, Vector3 self)
        {
            LastAim = aim;
            Vector3 targetPoint = targetPosition + new Vector3(0f, EyeHeight, 0f);

            // 镜头锁死：每 tick 重新对准目标（目标位置本身是逐帧插值的，所以画面上是平滑跟随，
            // 不会像"盯航点"那样在格心之间跳）。
            context.Actuators.LookAt(targetPoint);

            Vector3 direction = Flatten(aim - self);
            if (direction.LengthSquared() < 1e-4f)
            {
                ReleaseMoveKeys(context);
                return;
            }
            direction = Vector3.Normalize(direction);

            Vector3 forward;
            Vector3 right;
            BodyBasis(context, out forward, out right);
            float f = Vector3.Dot(direction, forward);
            float r = Vector3.Dot(direction, right);

            HoldMoveKeys(context, f > KeyAxisThreshold, f < -KeyAxisThreshold,
                r < -KeyAxisThreshold, r > KeyAxisThreshold);

            if (!string.IsNullOrEmpty(JumpKey) && aim.Y - self.Y > JumpHeight
                && Vector3.Distance(self, aim) < 1.6f)
            {
                context.Actuators.PulseKey(JumpKey, 60);
            }
        }

        /// <summary>
        /// 身体朝向的水平基（前 / 右）—— 引擎自己的矩阵算出来的，和"按 W 往前走"用的是同一套约定。
        ///
        /// 为什么可以从 yaw 精确重建：角色的身体旋转**永远是绕 Y 的纯旋转**
        /// （Source: ComponentLocomotion.cs:309 `CreateFromAxisAngle(Vector3.UnitY, yaw)` 是唯一写入点），
        /// 所以 `CreateFromAxisAngle(UnitY, yaw)` 就是那块 `ComponentBody.Matrix` 的水平部分。
        /// </summary>
        private static void BodyBasis(BtContext context, out Vector3 forward, out Vector3 right)
        {
            Matrix body = Matrix.CreateFromAxisAngle(Vector3.UnitY, context.Sensors.YawRadians);
            forward = Flatten(body.Forward);
            right = Flatten(body.Right);
            forward = forward.LengthSquared() > 1e-6f
                ? Vector3.Normalize(forward) : new Vector3(0f, 0f, 1f);
            right = right.LengthSquared() > 1e-6f
                ? Vector3.Normalize(right) : new Vector3(1f, 0f, 0f);
        }

        // ---------------------------------------------------------------- 移动键

        /// <summary>按住"往世界某个方向走"对应的那组键（前/后/左/右四个键的期望状态）。</summary>
        private void HoldMoveKeys(BtContext context, bool forward, bool back, bool left, bool right)
        {
            m_keyReassertTimer -= context.DeltaTime;
            if (m_keyReassertTimer <= 0f)
            {
                m_keyReassertTimer = KeyReassertSeconds;
                m_forceMoveKeys = true;                         // 定期重发，防"按住状态被真人/引擎清掉"
            }

            SetMoveKey(context, 0, ForwardKey, forward);
            SetMoveKey(context, 1, BackKey, back);
            SetMoveKey(context, 2, LeftKey, left);
            SetMoveKey(context, 3, RightKey, right);
            m_forceMoveKeys = false;
        }

        /// <summary>四个移动键全部松开（站住 / 收尾时用）。</summary>
        private void ReleaseMoveKeys(BtContext context)
        {
            SetMoveKey(context, 0, ForwardKey, false);
            SetMoveKey(context, 1, BackKey, false);
            SetMoveKey(context, 2, LeftKey, false);
            SetMoveKey(context, 3, RightKey, false);
        }

        /// <summary>
        /// 下发一个移动键。**只在下发变化（或强制重发）时才碰执行器**：
        /// 引擎的按下状态是持久的（Source: Engine/Engine/Input/Keyboard.cs:128-130
        /// `BeforeFrame` 为空，只有 `AfterFrame` 清 `downOnce`），每帧重写没有意义。
        /// </summary>
        private void SetMoveKey(BtContext context, int slot, string key, bool down)
        {
            if (string.IsNullOrEmpty(key))
            {
                m_moveKeyDown[slot] = false;
                return;
            }
            if (!m_forceMoveKeys && m_moveKeyDown[slot] == down)
                return;
            m_moveKeyDown[slot] = down;
            context.Actuators.HoldKey(key, down);
        }

        private static Vector3 Flatten(Vector3 value)
        {
            return new Vector3(value.X, 0f, value.Z);
        }

        /// <summary>借游戏 A* 走到目标附近（到达判据 = 水平距离 &lt;= KeepDistance）。</summary>
        private void FollowPath(BtContext context, Vector3 targetPosition, Vector3 self)
        {
            IAiWorldSensor paths = context.Sensors as IAiWorldSensor;
            NoteLeg(context, true, "using the game's A* (approach / go around)");

            if (paths != null)
            {
                AiPathStatus status = paths.GetPathStatus();
                bool needRequest = m_waypointCount == 0
                    || status == AiPathStatus.Idle
                    || status == AiPathStatus.Failed
                    || (RepathSeconds > 0f && m_timeSinceRequest >= RepathSeconds)
                    || Vector3.Distance(m_requestedTarget, targetPosition) > RepathMoveThreshold;

                // **硬下限**：寻路返回空路径时航点数一直是 0，上面那条会**每帧**成立 ——
                // 而游戏的寻路是"队列上限 10、后台每 250ms 处理一条"，每帧重算会把队列打满、
                // 把别的生物（和玩家自己）的寻路挤掉。所以两条请求之间至少隔这么久。
                if (needRequest && m_timeSinceRequest >= MinRequestInterval)
                {
                    string error;
                    paths.ClearPath();
                    if (paths.TryRequestPath(targetPosition, Math.Min(KeepDistance, 2f),
                        MaxPositionsToCheck, out error))
                    {
                        m_timeSinceRequest = 0f;
                        m_requestedTarget = targetPosition;
                        m_waypointCount = 0;
                        m_waypointIndex = 0;
                    }
                    else
                    {
                        context.Warn("FollowEntity: path request rejected (" + error + ") -> 直线走");
                        m_waypointCount = 0;
                        m_timeSinceRequest = 0f;                // 别每帧都撞一次请求
                    }
                }

                if (paths.GetPathStatus() == AiPathStatus.Ready && m_waypointCount == 0)
                {
                    m_waypointCount = paths.CopyPath(m_waypoints);
                    m_waypointIndex = 0;
                }
            }

            Vector3 aim;
            if (!TryCurrentWaypoint(self, out aim))
                aim = targetPosition;                           // 等待寻路期间也不站桩

            WalkToward(context, aim, targetPosition, self);
        }

        /// <summary>取当前航点；走到足够近就往后挪（一次 tick 最多挪一个，避免跳过头）。</summary>
        private bool TryCurrentWaypoint(Vector3 self, out Vector3 aim)
        {
            aim = Vector3.Zero;
            while (m_waypointIndex < m_waypointCount)
            {
                Vector3 candidate = m_waypoints[m_waypointIndex];
                if (HorizontalDistance(self, candidate) <= TrailCellRadius)
                {
                    m_waypointIndex++;
                    continue;
                }
                aim = candidate;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 卡住 / 收尾

        /// <summary>
        /// 卡住判定（与 `Task.NavigateTo` 同一套语义）：每 `seconds` 秒量一次位移，
        /// 挪得不够就是卡住；**没卡住才更新基线**（卡住时基线保持不动，于是会一直判卡住，
        /// 直到真的走起来为止 —— 这样"丢脚印改走 A*"之后不会立刻又被误判）。
        /// </summary>
        private bool IsStuck(Vector3 self, float seconds, float distance)
        {
            if (m_lastStuckCheckTime <= 0f)
            {
                m_lastStuckCheckTime = ActiveTime;
                m_lastStuckCheckPosition = self;
                return false;
            }

            if (ActiveTime - m_lastStuckCheckTime < seconds)
                return false;

            bool stuck = Vector3.Distance(self, m_lastStuckCheckPosition) < distance;
            if (!stuck)
            {
                m_lastStuckCheckTime = ActiveTime;
                m_lastStuckCheckPosition = self;
            }
            return stuck;
        }

        /// <summary>
        /// 记一下"这一段时间走的是哪条腿"，**只在切换时写一条日志**（调试跟随最有用的信息：
        /// 是在踩脚印，还是已经改成 A* 绕了）。不写日志的话这个字段就是纯装饰。
        /// </summary>
        private void NoteLeg(BtContext context, bool usingPath, string reason)
        {
            if (m_usedPath == usingPath && m_legNoted)
                return;
            m_usedPath = usingPath;
            m_legNoted = true;
            context.Log("FollowEntity: " + reason);
        }

        private void Release(BtContext context)
        {
            if (context.Actuators == null)
                return;
            ReleaseMoveKeys(context);
            context.Actuators.ReleaseAll();
            if (context.Sensors is IAiWorldSensor paths)
                paths.ClearPath();                              // 别把队列名额占着
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
        }

        private static Vector3 CellCentre(Vector3 position)
        {
            return new Vector3((float)Math.Floor(position.X) + 0.5f, position.Y,
                (float)Math.Floor(position.Z) + 0.5f);
        }

        private static bool SameCell(Vector3 a, Vector3 b)
        {
            return (int)Math.Floor(a.X) == (int)Math.Floor(b.X)
                && (int)Math.Floor(a.Z) == (int)Math.Floor(b.Z);
        }
    }
}

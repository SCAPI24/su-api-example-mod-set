using System;
using Engine;

namespace PlayerAiMod
{
    /// <summary>
    /// **寻路移动**：借用游戏自己的 A*（`SubsystemPathfinding`）算路径，再用**注入输入**跟着走。
    ///
    /// 与 `Task.MoveTo` 的区别，一句话：`MoveTo` 是直线走（撞墙就卡在那顶着），
    /// `NavigateTo` 会绕。为什么值得单独一个任务而不是给 MoveTo 加开关：
    ///
    ///   · 寻路是**异步**的（请求进队列，后台线程每 250ms 处理一条），要轮询、要在等待期间
    ///     继续朝目标走、要在走不动时重算 —— 这些状态机和"按住 W"完全不是一回事；
    ///   · 直线走法的语义（"朝目标按 W"）在很多场合仍然更合适（短距离、看得见目标），
    ///     把它改造成带寻路的版本会让简单场景也背上队列延迟。
    ///
    /// 铁律仍然只有一条：**路径计算是只读查询，路径跟随只用输入层**。
    /// 也就是说 AI 依然只是一个"会看地图、走位很准的玩家"，不能瞬移、不能直接改位置。
    ///
    /// 自己实现的跟随细节（都是真人会做的动作）：
    ///   · 每个航点：转向前方 → 按住前进键 → 走到 <see cref="WaypointRadius"/> 内就换下一个；
    ///   · 航点明显更高（> <see cref="JumpHeight"/>）且已靠近 → 按一下跳跃键（跳上半格台阶）；
    ///   · 一段时间内位移不足 → 判定卡住，**丢掉重算**（而不是继续顶着墙）；
    ///   · 目标移动超过阈值 / 路径过期 → 重新请求（默认 2 秒一次）。
    /// </summary>
    public sealed class BtNavigateToTask : BtTaskNode
    {
        /// <summary>路径缓冲：够装下 500 个 A* 位置的常见路径；不足就截断（截断也只影响精度）。</summary>
        private const int PathCapacity = 256;

        private readonly Vector3[] m_waypoints = new Vector3[PathCapacity];

        private int m_waypointCount;
        private int m_waypointIndex;
        private float m_timeSinceRequest = float.MaxValue;
        private Vector3 m_requestedTarget;
        private Vector3 m_lastStuckCheckPosition;
        private float m_lastStuckCheckTime;
        private bool m_warnedUnfocused;

        /// <summary>黑板里存放目标 actor 的键（`source=actor` 时用）。</summary>
        public string TargetKey { get; set; } = "target";

        /// <summary>
        /// 目标来源：`actor`（跟着黑板里的角色/生物走）或 `cell`（走到一组坐标上）。
        ///
        /// 坐标模式不是顺手加的：它是**唯一能确定性地验收寻路**的方式 ——
        /// 跟活物时目标一直在动，"走没走到"说不清；给一组坐标才能一眼看出
        /// "它绕过了障碍、走到了那个点"。日常用法也很实在：巡逻点、回家、走到矿脉。
        /// </summary>
        public string Source { get; set; } = "actor";

        /// <summary>坐标模式下目标格子的三个 int 键（与 `Service.UpdateSelf` 的自身坐标是两回事）。</summary>
        public string XKey { get; set; } = "goalX";
        public string YKey { get; set; } = "goalY";
        public string ZKey { get; set; } = "goalZ";

        /// <summary>到这个距离内就算到达（米）。</summary>
        public float AcceptableRadius { get; set; } = 2.5f;

        /// <summary>超时判失败（秒）。</summary>
        public float TimeoutSeconds { get; set; } = 60f;

        /// <summary>前进键（Keyboard 名字）。</summary>
        public string ForwardKey { get; set; } = "w";

        /// <summary>跳跃键（空格）。</summary>
        public string JumpKey { get; set; } = "space";

        /// <summary>航点比脚下高多少才跳（米）。</summary>
        public float JumpHeight { get; set; } = 0.6f;

        /// <summary>判定"走到这个航点了"的半径（米）。</summary>
        public float WaypointRadius { get; set; } = 0.7f;

        /// <summary>重新请求路径的间隔（秒）。0 = 只在必要时请求。</summary>
        public float RepathSeconds { get; set; } = 2f;

        /// <summary>目标移动超过这个距离就立刻重算（米）。</summary>
        public float RepathMoveThreshold { get; set; } = 1.5f;

        /// <summary>A* 的搜索上限（越大越能找到远路、越费 CPU）。</summary>
        public int MaxPositionsToCheck { get; set; } = 500;

        /// <summary>看航点时的眼睛高度（米）。</summary>
        public float EyeHeight { get; set; } = 1.35f;

        /// <summary>卡住判定：这段时间内位移不足 <see cref="StuckDistance"/> 就重算（秒）。</summary>
        public float StuckSeconds { get; set; } = 2f;

        /// <summary>卡住的位移阈值（米）。</summary>
        public float StuckDistance { get; set; } = 0.35f;

        public override string NodeType
        {
            get { return "Task.NavigateTo"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;

            Reset();
            m_lastStuckCheckPosition = context.Sensors.Position;
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

        private void Reset()
        {
            m_waypointCount = 0;
            m_waypointIndex = 0;
            m_timeSinceRequest = float.MaxValue;
            m_warnedUnfocused = false;
            m_lastStuckCheckTime = 0f;
            m_requestedTarget = Vector3.Zero;
        }

        private BtResult Step(BtContext context)
        {
            m_timeSinceRequest += context.DeltaTime;

            // 目标点：actor 模式取黑板里的角色位置，cell 模式取三个 int 坐标
            Vector3 destination;
            if (string.Equals(Source, "cell", StringComparison.OrdinalIgnoreCase))
            {
                int x;
                int y;
                int z;
                if (context.Blackboard == null
                    || !context.Blackboard.TryGet(XKey, out x)
                    || !context.Blackboard.TryGet(YKey, out y)
                    || !context.Blackboard.TryGet(ZKey, out z))
                {
                    context.Warn("NavigateTo: blackboard has no goal cell (" + XKey + "/" + YKey
                        + "/" + ZKey + ") -> Failed");
                    return BtResult.Failed;
                }
                destination = new Vector3(x + 0.5f, y, z + 0.5f);
            }
            else
            {
                AiActorView target;
                if (context.Blackboard == null || !context.Blackboard.TryGet(TargetKey, out target))
                {
                    context.Warn("NavigateTo: blackboard target '" + TargetKey + "' missing -> Failed");
                    return BtResult.Failed;
                }
                destination = target.Position;
            }

            Vector3 self = context.Sensors.Position;
            float distance = Vector3.Distance(self, destination);

            if (distance <= AcceptableRadius)
            {
                context.Log("NavigateTo: arrived, distance=" + distance.ToString("0.00"));
                return BtResult.Succeeded;
            }

            if (ActiveTime > TimeoutSeconds)
            {
                context.Warn("NavigateTo: timeout after " + ActiveTime.ToString("0.0")
                    + "s, distance=" + distance.ToString("0.00"));
                return BtResult.Failed;
            }

            if (context.Sensors != null && !context.Sensors.IsInputAccepted && !m_warnedUnfocused)
            {
                m_warnedUnfocused = true;
                context.Warn("NavigateTo: input is not accepted right now (window unfocused?) - movement may not happen");
            }

            IAiWorldSensor paths = context.Sensors as IAiWorldSensor;

            // ---- 该重算了吗？（没请求过 / 上次失败 / 过期 / 目标跑远了 / 判定卡住）
            bool stuck = IsStuck(context, self);
            if (stuck)
            {
                context.Warn("NavigateTo: stuck (moved < " + StuckDistance.ToString("0.0")
                    + "m in " + StuckSeconds.ToString("0.0") + "s) -> repath");
            }

            if (paths != null)
            {
                AiPathStatus status = paths.GetPathStatus();
                bool needRequest = m_waypointCount == 0
                    || status == AiPathStatus.Idle
                    || status == AiPathStatus.Failed
                    || stuck
                    || (RepathSeconds > 0f && m_timeSinceRequest >= RepathSeconds)
                    || Vector3.Distance(m_requestedTarget, destination) > RepathMoveThreshold;

                if (needRequest)
                {
                    string error;
                    paths.ClearPath();
                    if (paths.TryRequestPath(destination, Math.Min(AcceptableRadius, 2f),
                        MaxPositionsToCheck, out error))
                    {
                        m_timeSinceRequest = 0f;
                        m_requestedTarget = destination;
                        m_waypointCount = 0;
                        m_waypointIndex = 0;
                    }
                    else
                    {
                        context.Warn("NavigateTo: path request rejected (" + error + ") -> 直线走");
                        m_waypointCount = 0;
                        m_timeSinceRequest = 0f;      // 别每帧都撞一次请求
                    }
                }

                if (paths.GetPathStatus() == AiPathStatus.Ready && m_waypointCount == 0)
                {
                    m_waypointCount = paths.CopyPath(m_waypoints);
                    m_waypointIndex = 0;
                    context.Log("NavigateTo: path ready, " + m_waypointCount + " waypoints");
                }
            }

            // ---- 跟随：有航点就朝航点走，没有就朝目标直线走（等待寻路期间也不站桩）
            Vector3 aim;
            if (!TryCurrentWaypoint(self, out aim))
                aim = destination;

            context.Actuators.LookAt(aim + new Vector3(0f, EyeHeight, 0f));
            context.Actuators.HoldKey(ForwardKey, true);

            // 半格台阶：航点明显在脚上方且已经走近了才跳（提前跳会撞到天花板/掉进水里）
            if (!string.IsNullOrEmpty(JumpKey) && aim.Y - self.Y > JumpHeight
                && Vector3.Distance(self, aim) < 1.6f)
            {
                context.Actuators.PulseKey(JumpKey, 60);
            }

            MarkStuckCheck(context, self);
            return BtResult.InProgress;
        }

        /// <summary>取当前航点；走到足够近就往后挪（一次 tick 最多挪一个，避免跳过头）。</summary>
        private bool TryCurrentWaypoint(Vector3 self, out Vector3 aim)
        {
            aim = Vector3.Zero;
            while (m_waypointIndex < m_waypointCount)
            {
                Vector3 candidate = m_waypoints[m_waypointIndex];
                if (Vector3.Distance(self, candidate) <= WaypointRadius)
                {
                    m_waypointIndex++;
                    continue;
                }
                aim = candidate;
                return true;
            }
            return false;
        }

        private bool IsStuck(BtContext context, Vector3 self)
        {
            if (ActiveTime - m_lastStuckCheckTime < StuckSeconds)
                return false;

            bool stuck = Vector3.Distance(self, m_lastStuckCheckPosition) < StuckDistance;
            m_lastStuckCheckPosition = self;
            m_lastStuckCheckTime = ActiveTime;
            return stuck;
        }

        private void MarkStuckCheck(BtContext context, Vector3 self)
        {
            if (m_lastStuckCheckTime <= 0f)
            {
                m_lastStuckCheckTime = ActiveTime;
                m_lastStuckCheckPosition = self;
            }
        }

        private void Release(BtContext context)
        {
            if (context.Actuators == null)
                return;
            context.Actuators.HoldKey(ForwardKey, false);
            context.Actuators.ReleaseAll();
            if (context.Sensors is IAiWorldSensor paths)
                paths.ClearPath();                            // 别把队列名额占着
            context.Log("NavigateTo: released forward key");
        }
    }
}

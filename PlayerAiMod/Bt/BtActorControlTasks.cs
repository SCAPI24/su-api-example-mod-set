using System;
using Engine;

namespace PlayerAiMod
{
    /// <summary>
    /// **面向目标**（计划 §3.1 里与 `LookAt` 并列的那个）：把视角转到黑板上那个 actor 的方向，
    /// 转到位就成功；超时判失败。
    ///
    /// 与 `Task.LookAt` 的分工：
    ///   · `LookAt` 是"看向某个点/实体并**保持**若干秒"（KPI 是"看了一会儿"）；
    ///   · `FaceEntity` 是"**转到朝向正确**"（KPI 是"转到位了"）——攻击/交互/上马这类动作前置
    ///     需要的正是后者：转到位才动手，没转到位就继续等，而不是瞎等固定时长。
    ///
    /// 只看 yaw（水平朝向）：抬头低头由 `Task.LookAt` 负责，混在一起会让"面向"判定变得难调。
    /// </summary>
    public sealed class BtFaceEntityTask : BtTaskNode
    {
        /// <summary>黑板里存放目标 actor 的键。</summary>
        public string TargetKey { get; set; } = "target";

        /// <summary>允许的角度误差（度）。</summary>
        public float ToleranceDegrees { get; set; } = 12f;

        /// <summary>超时判失败（秒）。</summary>
        public float Timeout { get; set; } = 3f;

        /// <summary>每帧最多转多少（弧度）；0 = 用执行器的瞬时转向（AI 的优势）。</summary>
        public float MaxTurnPerSecond { get; set; }

        public override string NodeType
        {
            get { return "Task.FaceEntity"; }
        }

        public override bool IsLatent
        {
            get { return true; }                              // 可能要转好几帧才到位
        }

        protected override BtResult OnExecute(BtContext context)
        {
            return Face(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            return Face(context);
        }

        private BtResult Face(BtContext context)
        {
            AiActorView target;
            if (!context.Blackboard.TryGet(new AiBlackboardKey<AiActorView>(TargetKey), out target))
                return BtResult.Failed;                       // 目标不在（服务会清键）→ 失败，让树走备用分支

            Vector3 toTarget = target.Position - context.Sensors.Position;
            if (toTarget.LengthSquared() < 1e-6f)
                return BtResult.Succeeded;                    // 站在同一格：朝向无所谓了

            float desiredYaw = MathUtils.Atan2(toTarget.X, toTarget.Z);
            float currentYaw = context.Sensors.YawRadians;
            float error = NormalizeAngle(desiredYaw - currentYaw);

            if (Math.Abs(error) <= MathUtils.DegToRad(ToleranceDegrees))
                return BtResult.Succeeded;

            if (ActiveTime > Timeout)
                return BtResult.Failed;

            float step = error;
            if (MaxTurnPerSecond > 0f)
            {
                float limit = MaxTurnPerSecond * Math.Max(context.DeltaTime, 1e-4f);
                step = MathUtils.Clamp(error, -limit, limit);
            }

            // 只改 yaw：俯仰保持当前值（执行器 Look 的第二个参数就是俯仰）
            float pitch = 0f;
            context.Sensors.TryGetPitch(out pitch);
            context.Actuators.Look(currentYaw + step, pitch);
            return BtResult.InProgress;
        }

        /// <summary>把角度归一化到 (-π, π]，避免"差 359°其实是差 1°"这种判定错误。</summary>
        internal static float NormalizeAngle(float radians)
        {
            // Engine 的 MathUtils 只有 PI 一个常量，这里自己写两倍的（不引 System.Math 的 double 版本，
            // 免得在每次 tick 的热路径上做 double↔float 往返）
            const float twoPi = 2f * MathUtils.PI;
            while (radians > MathUtils.PI)
                radians -= twoPi;
            while (radians <= -MathUtils.PI)
                radians += twoPi;
            return radians;
        }
    }

    /// <summary>
    /// **触发控制面事件**（计划 §3.1 的 `Emit`）：往 AI 事件日志里写一条，
    /// 并可顺手把一个布尔键置位。
    ///
    /// 用途：让"树跑到哪一步了"对外可见 —— 编辑器实时监视、`ai.logs`、
    /// `<实例根>/PlayerAi/Logs/PlayerAi.log` 都能看到，而不用去猜。
    /// 没接日志（纯逻辑自检/录制环境）时退化为 `Engine.Log`，绝不因为"没有日志"失败。
    /// </summary>
    public sealed class BtEmitTask : BtTaskNode
    {
        /// <summary>事件分类（写进日志的中括号里，例如 `task` / `milestone`）。</summary>
        public string Category { get; set; } = "emit";

        /// <summary>事件内容。</summary>
        public string Message { get; set; }

        /// <summary>顺手置位的布尔键（留空则只写日志）。</summary>
        public string Key { get; set; }

        /// <summary>是否同时写游戏日志（`Engine.Log`）——默认只在接了事件日志时写事件日志。</summary>
        public bool AlsoEngineLog { get; set; }

        public override string NodeType
        {
            get { return "Task.Emit"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            string message = string.IsNullOrEmpty(Message) ? "(emit)" : Message;
            AiEventLog log = context.Runtime != null ? context.Runtime.EventLog : null;
            if (log != null)
                log.Write(string.IsNullOrEmpty(Category) ? "emit" : Category, message);
            else if (AlsoEngineLog)
                context.Log(message);
            else
                context.Warn("emit " + message + " (no event log attached)");

            if (!string.IsNullOrEmpty(Key) && context.Blackboard != null)
                context.Blackboard.Set(new AiBlackboardKey<bool>(Key), true);

            return BtResult.Succeeded;                        // 写完即成功：它没有"做到一半"的状态
        }
    }
}

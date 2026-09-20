using System;
using System.Collections.Generic;
using Engine;

namespace PlayerAiMod
{
    /// <summary>
    /// **传感器服务族**：周期性把"世界里的事实"写进黑板，让行为树能做条件判断。
    ///
    /// 为什么需要一族而不是一个：`Service.UpdateNearestPlayer` 只能回答"最近的玩家在哪"，
    /// 而真正的 AI 还需要知道"我快饿死了吗""附近有狼吗""前面是不是墙""看得见目标吗"。
    /// 这些都由**同一个模式**完成：读传感器（只读）→ 写黑板（不是游戏状态）→ 行为树用
    /// `Blackboard` / `CompareBBEntries` 装饰器做判断。
    ///
    /// 两条共同约定：
    ///   ① 找不到时**清掉上次的值**（`clearWhenMissing`，默认 true）—— 否则"狼已经走了"
    ///      但黑板里还留着旧坐标，树会一直朝空地跑（这个坑在 `UpdateNearestPlayer` 上踩过）；
    ///   ② 传感器不支持扩展观察（纯逻辑自检的桩）时**静默跳过**，不报错 —— 所以同一棵包
    ///      在自检环境里能装能跑，只是这些键不会被写。
    /// </summary>
    public static class BtSensorServices
    {
        /// <summary>取扩展观察层；拿不到就说明"这台机器只能读基础信息"。</summary>
        public static IAiWorldSensor Extras(BtContext context)
        {
            return context != null ? context.Sensors as IAiWorldSensor : null;
        }
    }

    /// <summary>
    /// 刷新**自身状态**：生命/氧气/饥饿/耐力/睡眠/体温/湿度 + 坐标/朝向/是否落地。
    ///
    /// 坐标写成三个 float（`&lt;prefix&gt;X/Y/Z`）而不是一个向量：黑板只有 bool/int/float/string/actor
    /// 五种值，拆成三个数既能被比较、也能直接喂给"走到某坐标"这类后续任务。
    /// </summary>
    public sealed class BtUpdateSelfService : BtService
    {
        /// <summary>键名前缀（默认 `self.`）。</summary>
        public string Prefix { get; set; } = "self.";

        /// <summary>是否写坐标与朝向（不需要时可关掉，少写几格黑板）。</summary>
        public bool WritePosition { get; set; } = true;

        public override string NodeType
        {
            get { return "Service.UpdateSelf"; }
        }

        protected override void OnTick(BtContext context)
        {
            IAiWorldSensor extras = BtSensorServices.Extras(context);
            AiSelfState state;
            if (extras == null || !extras.TryGetSelfState(out state))
                return;

            string prefix = string.IsNullOrEmpty(Prefix) ? "self." : Prefix;
            AiBlackboard board = context.Blackboard;
            if (board == null)
                return;

            board.Set(new AiBlackboardKey<float>(prefix + "Health"), state.Health);
            board.Set(new AiBlackboardKey<float>(prefix + "Air"), state.Air);
            board.Set(new AiBlackboardKey<float>(prefix + "Food"), state.Food);
            board.Set(new AiBlackboardKey<float>(prefix + "Stamina"), state.Stamina);
            board.Set(new AiBlackboardKey<float>(prefix + "Sleep"), state.Sleep);
            board.Set(new AiBlackboardKey<float>(prefix + "Temperature"), state.Temperature);
            board.Set(new AiBlackboardKey<float>(prefix + "Wetness"), state.Wetness);
            board.Set(new AiBlackboardKey<bool>(prefix + "OnGround"), state.IsOnGround);

            if (!WritePosition)
                return;

            board.Set(new AiBlackboardKey<float>(prefix + "X"), state.Position.X);
            board.Set(new AiBlackboardKey<float>(prefix + "Y"), state.Position.Y);
            board.Set(new AiBlackboardKey<float>(prefix + "Z"), state.Position.Z);
            board.Set(new AiBlackboardKey<float>(prefix + "Yaw"), state.YawRadians);
        }
    }

    /// <summary>
    /// 刷新**最近的生物**（不含玩家）：写 actor 键 + 一个距离键。
    /// `categoryMask` 用 <c>CreatureCategory</c> 位掩码过滤（1=陆地掠食者 …），0 = 不限。
    /// </summary>
    public sealed class BtUpdateNearestCreatureService : BtService
    {
        /// <summary>把找到的生物写进哪个黑板键。</summary>
        public string TargetKey { get; set; } = "creature";

        /// <summary>类别掩码（0 = 不限）。</summary>
        public int CategoryMask { get; set; }

        /// <summary>搜索半径（米）。</summary>
        public float MaxDistance { get; set; } = 32f;

        /// <summary>找不到时是否清掉键（默认清，免得上一次的目标一直留着）。</summary>
        public bool ClearWhenMissing { get; set; } = true;

        public override string NodeType
        {
            get { return "Service.UpdateNearestCreature"; }
        }

        protected override void OnTick(BtContext context)
        {
            IAiWorldSensor extras = BtSensorServices.Extras(context);
            AiBlackboard board = context.Blackboard;
            if (extras == null || board == null || string.IsNullOrEmpty(TargetKey))
                return;

            AiActorView view;
            if (extras.TryFindNearestCreature(CategoryMask, MaxDistance, out view))
            {
                board.Set(new AiBlackboardKey<AiActorView>(TargetKey), view);
                board.Set(new AiBlackboardKey<float>(TargetKey + "Distance"), view.Distance);
                board.Set(new AiBlackboardKey<string>(TargetKey + "Name"), view.Name);
                board.Set(new AiBlackboardKey<bool>(TargetKey + "IsSet"), true);
                return;
            }

            if (ClearWhenMissing)
                Clear(board, TargetKey);
        }

        internal static void Clear(AiBlackboard board, string key)
        {
            board.Remove(new AiBlackboardKey<AiActorView>(key));
            board.Remove(new AiBlackboardKey<float>(key + "Distance"));
            board.Remove(new AiBlackboardKey<string>(key + "Name"));
            board.Remove(new AiBlackboardKey<bool>(key + "IsSet"));
            board.Remove(new AiBlackboardKey<int>(key + "Count"));
        }
    }

    /// <summary>刷新**最近的掉落物**（Pickable）：写 actor 键 + 距离 + 数量。</summary>
    public sealed class BtUpdateNearestPickableService : BtService
    {
        public string TargetKey { get; set; } = "pickable";

        public float MaxDistance { get; set; } = 24f;

        public bool ClearWhenMissing { get; set; } = true;

        public override string NodeType
        {
            get { return "Service.UpdateNearestPickable"; }
        }

        protected override void OnTick(BtContext context)
        {
            IAiWorldSensor extras = BtSensorServices.Extras(context);
            AiBlackboard board = context.Blackboard;
            if (extras == null || board == null || string.IsNullOrEmpty(TargetKey))
                return;

            AiActorView view;
            if (extras.TryFindNearestPickable(MaxDistance, out view))
            {
                board.Set(new AiBlackboardKey<AiActorView>(TargetKey), view);
                board.Set(new AiBlackboardKey<float>(TargetKey + "Distance"), view.Distance);
                board.Set(new AiBlackboardKey<string>(TargetKey + "Name"), view.Name);
                board.Set(new AiBlackboardKey<int>(TargetKey + "Count"), view.Count);
                board.Set(new AiBlackboardKey<bool>(TargetKey + "IsSet"), true);
                return;
            }

            if (ClearWhenMissing)
                BtUpdateNearestCreatureService.Clear(board, TargetKey);
        }
    }

    /// <summary>
    /// 刷新**眼睛正前方/视线方向上的第一个方块**。
    ///
    /// 用途：判断"前面是不是墙"（决定要不要跳/绕）、"脚下是不是悬崖"、
    /// 以及挖/放任务在动手前先确认目标格子里有什么。
    /// `pitchOffset` 用来把射线略微朝下（0.35 = 看向脚前方一格）或朝上。
    ///
    /// 写出的键：`&lt;key&gt;`（bool 有没有命中）、`&lt;key&gt;Distance`、`&lt;key&gt;Contents`、
    /// `&lt;key&gt;X` / `&lt;key&gt;Y` / `&lt;key&gt;Z`（int，拿去喂 `Task.Mine` / `Task.PlaceBlock`）。
    /// **默认前缀 `mine`**，正好与 `Task.Mine` 的默认坐标键（`mineX`/`mineY`/`mineZ`）配对 ——
    /// "探测前方方块 → 挖掉它"这条最常见的链路不用改任何键名就能拼起来。
    /// </summary>
    public sealed class BtUpdateBlockAheadService : BtService
    {
        /// <summary>键名前缀（默认 `mine`，与 `Task.Mine` 的默认键对齐）。</summary>
        public string Key { get; set; } = "mine";

        /// <summary>射线长度（米）。</summary>
        public float MaxDistance { get; set; } = 3f;

        /// <summary>俯仰偏移（弧度；负值朝下）。默认略微朝下，看"脚前那格"。</summary>
        public float PitchOffset { get; set; } = -0.35f;

        /// <summary>命中的方块名写进哪个键（为空则不写）。</summary>
        public string NameKey { get; set; } = "mineName";

        public override string NodeType
        {
            get { return "Service.UpdateBlockAhead"; }
        }

        protected override void OnTick(BtContext context)
        {
            IAiWorldSensor extras = BtSensorServices.Extras(context);
            AiBlackboard board = context.Blackboard;
            if (extras == null || board == null || string.IsNullOrEmpty(Key))
                return;

            Vector3 direction = Direction(context.Sensors.YawRadians, PitchOffset);
            AiBlockHit hit;
            if (extras.TryRaycastBlock(direction, MaxDistance, out hit))
            {
                board.Set(new AiBlackboardKey<bool>(Key), true);
                board.Set(new AiBlackboardKey<float>(Key + "Distance"), hit.Distance);
                board.Set(new AiBlackboardKey<int>(Key + "Contents"), hit.Contents);
                board.Set(new AiBlackboardKey<int>(Key + "X"), hit.X);
                board.Set(new AiBlackboardKey<int>(Key + "Y"), hit.Y);
                board.Set(new AiBlackboardKey<int>(Key + "Z"), hit.Z);
                if (!string.IsNullOrEmpty(NameKey))
                    board.Set(new AiBlackboardKey<string>(NameKey), hit.Name);
                return;
            }

            board.Set(new AiBlackboardKey<bool>(Key), false);
            board.Remove(new AiBlackboardKey<float>(Key + "Distance"));
            board.Remove(new AiBlackboardKey<int>(Key + "Contents"));
            board.Remove(new AiBlackboardKey<int>(Key + "X"));
            board.Remove(new AiBlackboardKey<int>(Key + "Y"));
            board.Remove(new AiBlackboardKey<int>(Key + "Z"));
            if (!string.IsNullOrEmpty(NameKey))
                board.Remove(new AiBlackboardKey<string>(NameKey));
        }

        /// <summary>由 yaw/pitch 算朝向前方单位向量（与游戏里的朝向约定一致）。</summary>
        internal static Vector3 Direction(float yawRadians, float pitchRadians)
        {
            float cosPitch = MathUtils.Cos(pitchRadians);
            return new Vector3(
                cosPitch * MathUtils.Sin(yawRadians),
                MathUtils.Sin(pitchRadians),
                cosPitch * MathUtils.Cos(yawRadians));
        }
    }

    /// <summary>
    /// 刷新**到目标（actor 键）的视线是否通畅**：写一个布尔键 + 距离。
    /// 用来决定"能不能直接打/交互，还是要先绕过去"。
    ///
    /// **瞄哪里**（2026-09-13 用户实测报的问题："客户端只露出头的时候，本地看不到"）：
    /// 第一版直接拿 `target.Position` 打射线 —— 那是角色的**脚底**。于是"躲在 1 格高的墙后
    /// 只露出头"时，到脚的射线被墙挡住 → `canSee=false` → 盯人树把视线冻住，
    /// 可人明明就在眼前露着头。真人判断"看不看得见"看的是**能看到他哪一块**，不是脚。
    ///
    /// 所以默认打**两条**射线：头（`TargetEyeHeight`，默认 1.55 = 眼睛高度，与
    /// `Task.LookAt` 的 `eyeHeight` 对齐）和胸口（`BodyHeight`，默认 0.9）；
    /// **任一通畅就算看得见**（`AlsoCheckBody=false` 可退回单条）。
    /// 代价是每 tick 两次体素射线 —— 与游戏自己每帧做的那点射线比可以忽略。
    ///
    /// 注意约定：`&lt;key&gt;Distance` 写的仍然是**到脚底**的距离（旧语义不变，
    /// 用它做"走近点"的判断不会被这次改动影响）。
    /// </summary>
    public sealed class BtUpdateLineOfSightService : BtService
    {
        /// <summary>要看的目标（actor 键，通常是 `target` / `creature`）。</summary>
        public string TargetKey { get; set; } = "target";

        /// <summary>结果写进哪个布尔键。</summary>
        public string Key { get; set; } = "canSee";

        /// <summary>超过这个距离就不再看（省一次射线）。</summary>
        public float MaxDistance { get; set; } = 48f;

        /// <summary>第一条第射线瞄多高（米，相对目标脚底）。默认 1.55 = 眼睛高度。</summary>
        public float TargetEyeHeight { get; set; } = 1.55f;

        /// <summary>是否加打一条"胸口"射线（任一通畅即算看得见）。</summary>
        public bool AlsoCheckBody { get; set; } = true;

        /// <summary>胸口射线的高度（米，相对目标脚底）。</summary>
        public float BodyHeight { get; set; } = 0.9f;

        public override string NodeType
        {
            get { return "Service.UpdateLineOfSight"; }
        }

        protected override void OnTick(BtContext context)
        {
            IAiWorldSensor extras = BtSensorServices.Extras(context);
            AiBlackboard board = context.Blackboard;
            if (extras == null || board == null || string.IsNullOrEmpty(TargetKey)
                || string.IsNullOrEmpty(Key))
                return;

            AiActorView target;
            if (!board.TryGet(new AiBlackboardKey<AiActorView>(TargetKey), out target))
            {
                board.Set(new AiBlackboardKey<bool>(Key), false);
                return;
            }

            float distance = Vector3.Distance(context.Sensors.EyePosition, target.Position);
            if (distance > MaxDistance)
            {
                board.Set(new AiBlackboardKey<bool>(Key), false);
                board.Set(new AiBlackboardKey<float>(Key + "Distance"), distance);
                return;
            }

            float measured;
            bool visible = extras.HasLineOfSight(
                target.Position + new Vector3(0f, Math.Max(0f, TargetEyeHeight), 0f), out measured);
            if (!visible && AlsoCheckBody)
            {
                visible = extras.HasLineOfSight(
                    target.Position + new Vector3(0f, Math.Max(0f, BodyHeight), 0f), out measured);
            }

            board.Set(new AiBlackboardKey<bool>(Key), visible);
            board.Set(new AiBlackboardKey<float>(Key + "Distance"), distance);
        }
    }

    /// <summary>
    /// **通用射线探测**：从"我身上的某个点"朝"某个端点"打一条射线（只读），把结果写进黑板。
    ///
    /// 为什么要有它：之前的探测服务各自写死了一种用法（`UpdateBlockAhead` 只会"朝前看脚下"、
    /// `UpdateLineOfSight` 只会"看某个 actor 的固定高度"）。用户要的是**通用件**：
    /// "能不能看到他的头""头顶有没有天花板""脚下有没有地""前面 2 米有墙吗"都应该是**属性**，
    /// 而不是每换一个问法就得改一次 C#（改 C# 就要重新部署 + 重启游戏）。
    ///
    /// | 属性 | 取值 | 含义 |
    /// |---|---|---|
    /// | `from` | `eye`（默认）\| `body` \| `point` | 起点：自己眼睛 / 自己身体 / 黑板三浮点键 |
    /// | `to` | `target`（默认）\| `point` \| `ahead` \| `down` | 终点：黑板 actor 键（+ `targetHeight`）/ 黑板三浮点键 / 自己朝向（+ `pitchOffset`）/ 正下方 |
    ///
    /// 写什么（`key` 默认 `probe`）—— **两种读数都写**，省得每棵树都在脑子里取反：
    ///   · bool `&lt;key&gt;Blocked` = 射线**打到了方块**（被挡）；
    ///   · bool `&lt;key&gt;Reached` = 射线**通畅到达终点**（没打到，或打到的比终点更远）；
    ///   · float `&lt;key&gt;Distance`、int `&lt;key&gt;X/Y/Z`、int `&lt;key&gt;Contents`、string `&lt;key&gt;Block`。
    /// `mode` 决定**主键** `&lt;key&gt;` 是哪一个：`blocked`（默认，和 `UpdateBlockAhead` 同口径）
    /// 或 `clear`（"看得见吗/前面通不通"用这个，`seeHead == true` 就是看得见）。
    /// 打空时布尔写 false/true 如实反映，并**清掉**格子/名字（不留上一次的旧读数）。
    ///
    /// "头 **或** 胸口看得见"就挂两个实例再用 `Selector` 组合 —— 组合语义交给树。
    /// </summary>
    public sealed class BtProbeService : BtService
    {
        public string From { get; set; } = "eye";

        public string To { get; set; } = "target";

        /// <summary>主键口径：`blocked`（默认）| `clear`。</summary>
        public string Mode { get; set; } = "blocked";

        /// <summary>结果布尔键（另外还有 Distance / X / Y / Z / Contents / Block 后缀）。</summary>
        public string Key { get; set; } = "probe";

        /// <summary>`to=target` 时的 actor 键。</summary>
        public string TargetKey { get; set; } = "target";

        /// <summary>`to=target` 时在目标脚底之上多少米打（要精确的点就用 `to=point` + 模型节点）。</summary>
        public float TargetHeight { get; set; } = 1.55f;

        /// <summary>`to=ahead` 时的俯仰偏移（弧度；负 = 朝下）。</summary>
        public float PitchOffset { get; set; }

        public string FromXKey { get; set; } = "point.X";
        public string FromYKey { get; set; } = "point.Y";
        public string FromZKey { get; set; } = "point.Z";
        public string ToXKey { get; set; } = "point.X";
        public string ToYKey { get; set; } = "point.Y";
        public string ToZKey { get; set; } = "point.Z";

        /// <summary>射线长度（米）。</summary>
        public float MaxDistance { get; set; } = 16f;

        public override string NodeType
        {
            get { return "Service.Probe"; }
        }

        protected override void OnTick(BtContext context)
        {
            IAiWorldSensor extras = BtSensorServices.Extras(context);
            AiBlackboard board = context.Blackboard;
            if (extras == null || board == null || context.Sensors == null
                || string.IsNullOrEmpty(Key))
                return;

            Vector3 origin;
            if (string.Equals(From, "point", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryPoint(board, FromXKey, FromYKey, FromZKey, out origin))
                {
                    Clear(board);
                    return;                                    // 起点还没有：如实写"没测到"
                }
            }
            else if (string.Equals(From, "body", StringComparison.OrdinalIgnoreCase))
            {
                origin = context.Sensors.Position;
            }
            else
            {
                origin = context.Sensors.EyePosition;
            }

            Vector3 aim;
            if (string.Equals(To, "point", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryPoint(board, ToXKey, ToYKey, ToZKey, out aim))
                {
                    Clear(board);
                    return;
                }
            }
            else if (string.Equals(To, "ahead", StringComparison.OrdinalIgnoreCase))
            {
                float pitch;
                if (!context.Sensors.TryGetPitch(out pitch))
                    pitch = 0f;
                aim = origin + Forward(context.Sensors.YawRadians, pitch + PitchOffset) * MaxDistance;
            }
            else if (string.Equals(To, "down", StringComparison.OrdinalIgnoreCase))
            {
                aim = origin - new Vector3(0f, MaxDistance, 0f);
            }
            else
            {
                AiActorView target;
                if (!board.TryGet(new AiBlackboardKey<AiActorView>(TargetKey), out target))
                {
                    Clear(board);
                    return;
                }
                aim = target.Position + new Vector3(0f, TargetHeight, 0f);
            }

            Vector3 direction = aim - origin;
            if (direction.LengthSquared() < 1e-6f)
                direction = Forward(context.Sensors.YawRadians, 0f);

            AiBlockHit hit;
            bool blocked = extras.TryRaycastBlockFrom(origin, direction, MaxDistance, out hit);

            // "是否通畅到达终点"：没打到 = 通；打到了但比终点更远（留 5 cm 容差）= 也通
            float toEnd = Math.Min(MaxDistance, Vector3.Distance(origin, aim));
            bool reached = !blocked || hit.Distance >= toEnd - 0.05f;

            bool primary = string.Equals(Mode, "clear", StringComparison.OrdinalIgnoreCase)
                ? reached : blocked;

            board.Set(new AiBlackboardKey<bool>(Key), primary);
            board.Set(new AiBlackboardKey<bool>(Key + "Blocked"), blocked);
            board.Set(new AiBlackboardKey<bool>(Key + "Reached"), reached);
            if (blocked)
            {
                board.Set(new AiBlackboardKey<float>(Key + "Distance"), hit.Distance);
                board.Set(new AiBlackboardKey<int>(Key + "X"), hit.X);
                board.Set(new AiBlackboardKey<int>(Key + "Y"), hit.Y);
                board.Set(new AiBlackboardKey<int>(Key + "Z"), hit.Z);
                board.Set(new AiBlackboardKey<int>(Key + "Contents"), hit.Contents);
                if (!string.IsNullOrEmpty(hit.Name))
                    board.Set(new AiBlackboardKey<string>(Key + "Block"), hit.Name);
            }
            else
            {
                board.Set(new AiBlackboardKey<float>(Key + "Distance"), MaxDistance);
                board.Remove(new AiBlackboardKey<int>(Key + "X"));
                board.Remove(new AiBlackboardKey<int>(Key + "Y"));
                board.Remove(new AiBlackboardKey<int>(Key + "Z"));
                board.Remove(new AiBlackboardKey<int>(Key + "Contents"));
                board.Remove(new AiBlackboardKey<string>(Key + "Block"));
            }
        }

        private static bool TryPoint(AiBlackboard board, string xKey, string yKey, string zKey,
            out Vector3 point)
        {
            point = Vector3.Zero;
            float x;
            float y;
            float z;
            if (!board.TryGet(xKey, out x) || !board.TryGet(yKey, out y) || !board.TryGet(zKey, out z))
                return false;
            point = new Vector3(x, y, z);
            return true;
        }

        /// <summary>
        /// 端点取不到时不撒谎：主键与 `Reached` 都写 false，并把格子/名字那几个键**清掉**。
        /// 为什么必须清：留着上一 tick 的 `seeHead=true`，树会继续以为"还看得见"并接着盯人 ——
        /// 和 `clearWhenMissing` 那条约定是同一个道理（旧读数比没有读数更危险）。
        /// </summary>
        private void Clear(AiBlackboard board)
        {
            board.Set(new AiBlackboardKey<bool>(Key), false);
            board.Set(new AiBlackboardKey<bool>(Key + "Blocked"), false);
            board.Set(new AiBlackboardKey<bool>(Key + "Reached"), false);
            board.Set(new AiBlackboardKey<float>(Key + "Distance"), MaxDistance);
            board.Remove(new AiBlackboardKey<int>(Key + "X"));
            board.Remove(new AiBlackboardKey<int>(Key + "Y"));
            board.Remove(new AiBlackboardKey<int>(Key + "Z"));
            board.Remove(new AiBlackboardKey<int>(Key + "Contents"));
            board.Remove(new AiBlackboardKey<string>(Key + "Block"));
        }

        /// <summary>
        /// 由 yaw/pitch 求方向。约定与 `InputInjector.LookAt`（`Engine.Matrix.Forward`）一致：
        /// `Forward(θ) = (-sinθ, 0, -cosθ)` —— 朝 -Z 是 yaw=0、朝 +X 是 yaw=-90°（实测标定过）。
        /// </summary>
        internal static Vector3 Forward(float yaw, float pitch)
        {
            float cosPitch = (float)Math.Cos(pitch);
            return new Vector3(-(float)Math.Sin(yaw) * cosPitch, (float)Math.Sin(pitch),
                -(float)Math.Cos(yaw) * cosPitch);
        }
    }

    /// <summary>
    /// **模型节点（骨骼）跟踪**：把某个 actor 的某个模型节点的**世界坐标**写进黑板。
    ///
    /// 为什么需要它：`AiActorView.Position` 是**脚底**，而"他露出头了吗""他的手在哪"问的是
    /// **具体节点**。人形模型的标准节点名（Source: ComponentHumanModel.cs:282-287）：
    /// `Body` / `Head` / `Leg1` / `Leg2` / `Hand1` / `Hand2`；
    /// 四足 / 鸟 / 鱼：`Body` / `Neck` / `Head` / `Leg1..4` / `Wing1..2` / `Tail1..2` / `Jaw`。
    ///
    /// 写出的键（`prefix` 默认 `node.`）：float `node.X/Y/Z`、bool `node.Exists`、
    /// float `node.Distance`。名字写错时 `node.Exists=false`，并把该模型**可用的骨骼名**
    /// 写进 AI 事件日志 —— 在游戏里就能查到正确名字，不用翻源码。
    ///
    /// 与另外两个新件配合：`Service.Probe(to=point, toXKey=node.X…)` = "**我的眼睛看得见他的头吗**"；
    /// `Task.LookAtPoint(point.X…)` = "**眼睛锁在他的头**上"。
    /// </summary>
    public sealed class BtUpdateModelNodeService : BtService
    {
        public string TargetKey { get; set; } = "target";

        /// <summary>模型节点名（默认 `Head`）。</summary>
        public string NodeName { get; set; } = "Head";

        /// <summary>写出去的键前缀（默认 `node.`）。</summary>
        public string Prefix { get; set; } = "node.";

        /// <summary>节点不存在时是否清掉坐标键（默认 true：宁可判"没有"也别留旧坐标）。</summary>
        public bool ClearWhenMissing { get; set; } = true;

        private string m_reportedNode;

        public override string NodeType
        {
            get { return "Service.UpdateModelNode"; }
        }

        protected override void OnTick(BtContext context)
        {
            IAiWorldSensor extras = BtSensorServices.Extras(context);
            AiBlackboard board = context.Blackboard;
            if (extras == null || board == null || string.IsNullOrEmpty(Prefix))
                return;

            AiActorView target;
            if (!board.TryGet(new AiBlackboardKey<AiActorView>(TargetKey), out target))
            {
                Clear(board);
                return;
            }

            Vector3 world;
            if (!extras.TryGetBoneWorldPosition(target, NodeName, out world))
            {
                Clear(board);
                if (m_reportedNode != NodeName)                // 每个名字只报一次，别刷日志
                {
                    m_reportedNode = NodeName;
                    List<string> names;
                    List<Vector3> positions;
                    if (extras.TryListBones(target, out names, out positions))
                        context.Warn("ModelNode: '" + NodeName + "' not found on " + target.Name
                            + "; available: " + string.Join(", ", names));
                    else
                        context.Warn("ModelNode: '" + NodeName + "' not found (no model available)");
                }
                return;
            }

            m_reportedNode = null;
            board.Set(new AiBlackboardKey<float>(Prefix + "X"), world.X);
            board.Set(new AiBlackboardKey<float>(Prefix + "Y"), world.Y);
            board.Set(new AiBlackboardKey<float>(Prefix + "Z"), world.Z);
            board.Set(new AiBlackboardKey<bool>(Prefix + "Exists"), true);
            board.Set(new AiBlackboardKey<float>(Prefix + "Distance"),
                Vector3.Distance(context.Sensors.EyePosition, world));
        }

        private void Clear(AiBlackboard board)
        {
            board.Set(new AiBlackboardKey<bool>(Prefix + "Exists"), false);
            if (!ClearWhenMissing)
                return;
            board.Remove(new AiBlackboardKey<float>(Prefix + "X"));
            board.Remove(new AiBlackboardKey<float>(Prefix + "Y"));
            board.Remove(new AiBlackboardKey<float>(Prefix + "Z"));
            board.Remove(new AiBlackboardKey<float>(Prefix + "Distance"));
        }
    }
}

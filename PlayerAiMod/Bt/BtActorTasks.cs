using Engine;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// **看向黑板里的一个点**（三个 float 键）—— `Task.LookAt` 盯的是"actor 位置 + 固定高度"，
    /// 而这条盯的是**任意点**，于是可以盯模型节点：`Service.UpdateModelNode` 把 `Head` 的世界坐标
    /// 写进 `node.X/Y/Z`，这里就用它当视线目标。
    ///
    /// 语义与 `Task.LookAt` 完全一致：潜在任务，在 `seconds` 期间**每个 tick 都重新对准**
    /// （所以目标是动的也没关系），点取不到即失败。
    /// </summary>
    public sealed class BtLookAtPointTask : BtTaskNode
    {
        public string XKey { get; set; } = "point.X";
        public string YKey { get; set; } = "point.Y";
        public string ZKey { get; set; } = "point.Z";

        /// <summary>保持时长的下限（朝向本身是瞬时完成的，这里用来"停一下"）。</summary>
        public float Seconds { get; set; } = 0.1f;

        public override string NodeType
        {
            get { return "Task.LookAtPoint"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;
            return Look(context) ? BtResult.InProgress : BtResult.Failed;
        }

        protected override BtResult OnTick(BtContext context)
        {
            if (!Look(context))
                return BtResult.Failed;
            return ActiveTime >= Seconds ? BtResult.Succeeded : BtResult.InProgress;
        }

        private bool Look(BtContext context)
        {
            AiBlackboard board = context.Blackboard;
            float x;
            float y;
            float z;
            if (board == null
                || !board.TryGet(XKey, out x) || !board.TryGet(YKey, out y)
                || !board.TryGet(ZKey, out z))
            {
                return false;
            }

            context.Actuators.LookAt(new Vector3(x, y, z));
            return true;
        }
    }

    /// <summary>
    /// 看向黑板里的目标（示例树与后续交互行为的公共前置）。
    /// 跨帧任务：保持 <see cref="Seconds"/> 秒后成功；目标丢失即失败。
    /// </summary>
    public sealed class BtLookAtTargetTask : BtTaskNode
    {
        public string TargetKey { get; set; } = "target";

        public float EyeHeight { get; set; } = 1.35f;

        /// <summary>保持时长的下限（朝向是瞬时完成的，这里用来"停一下"）。</summary>
        public float Seconds { get; set; } = 0.25f;

        public override string NodeType
        {
            get { return "Task.LookAt"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;
            return Look(context) ? BtResult.InProgress : BtResult.Failed;
        }

        protected override BtResult OnTick(BtContext context)
        {
            if (!Look(context))
                return BtResult.Failed;

            return ActiveTime >= Seconds ? BtResult.Succeeded : BtResult.InProgress;
        }

        private bool Look(BtContext context)
        {
            AiActorView target;
            if (context.Blackboard == null
                || !context.Blackboard.TryGet(TargetKey, out target))
            {
                return false;
            }

            context.Actuators.LookAt(target.Position + new Vector3(0f, EyeHeight, 0f));
            return true;
        }
    }

    /// <summary>
    /// 走向黑板里的目标，直到距离小于 <see cref="AcceptableRadius"/>（或超时/目标丢失判失败）。
    ///
    /// 安全要点：**OnExit 必须释放前进键** —— 被中断、超时、异常都要走这里，
    /// 否则会留下"以为自己还在走路"的坏状态（与状态机版本同一条铁律）。
    /// 目前是直走（没有寻路），撞墙会走不动，属于已知限制。
    /// </summary>
    public sealed class BtMoveToTargetTask : BtTaskNode
    {
        private bool m_warnedUnfocused;

        public string TargetKey { get; set; } = "target";

        public float AcceptableRadius { get; set; } = 3f;

        public float TimeoutSeconds { get; set; } = 25f;

        public string ForwardKey { get; set; } = "w";

        public float EyeHeight { get; set; } = 1.35f;

        public override string NodeType
        {
            get { return "Task.MoveTo"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (context.Actuators == null || !context.Actuators.IsReady)
                return BtResult.Failed;

            m_warnedUnfocused = false;
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
            {
                context.Warn("MoveTo: blackboard target '" + TargetKey + "' missing -> Failed");
                return BtResult.Failed;
            }

            if (target.Distance <= AcceptableRadius)
            {
                context.Log("MoveTo: arrived, distance=" + target.Distance.ToString("0.00"));
                return BtResult.Succeeded;
            }

            if (ActiveTime > TimeoutSeconds)
            {
                context.Warn("MoveTo: timeout after " + ActiveTime.ToString("0.0")
                    + "s, distance=" + target.Distance.ToString("0.00"));
                return BtResult.Failed;
            }

            if (context.Sensors != null && !context.Sensors.IsInputAccepted && !m_warnedUnfocused)
            {
                m_warnedUnfocused = true;
                context.Warn("MoveTo: input is not accepted right now (window unfocused?) - movement may not happen");
            }

            // 边走边修正朝向：目标一动方向就更新（每帧一次，等价于人手的连续转向）。
            context.Actuators.LookAt(target.Position + new Vector3(0f, EyeHeight, 0f));
            context.Actuators.HoldKey(ForwardKey, true);
            return BtResult.InProgress;
        }

        private void Release(BtContext context)
        {
            if (context.Actuators == null)
                return;
            context.Actuators.HoldKey(ForwardKey, false);
            context.Actuators.ReleaseAll();
            context.Log("MoveTo: released forward key");
        }
    }

    /// <summary>
    /// 等待黑板里出现目标（由服务周期刷新）。
    /// 典型用法：`Sequence[ WaitForTarget, LookAt, MoveTo ]` 的入口守卫。
    /// </summary>
    public sealed class BtWaitForTargetTask : BtTaskNode
    {
        public string TargetKey { get; set; } = "target";

        public float TimeoutSeconds { get; set; } = 10f;

        public override string NodeType
        {
            get { return "Task.WaitForTarget"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            return Step(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            return Step(context);
        }

        private BtResult Step(BtContext context)
        {
            AiActorView target;
            if (context.Blackboard != null && context.Blackboard.TryGet(TargetKey, out target))
            {
                context.Log("WaitForTarget: found " + target);
                return BtResult.Succeeded;
            }

            if (ActiveTime > TimeoutSeconds)
            {
                context.Warn("WaitForTarget: no target within " + TimeoutSeconds.ToString("0.0") + "s -> Failed");
                return BtResult.Failed;
            }

            return BtResult.InProgress;
        }
    }

    /// <summary>
    /// 嵌套子树任务：tick 另一个包（`.scbtpak`）的某棵子树 —— 这就是"多个包组成一棵复杂行为树"的落点。
    /// <see cref="Subtree"/> 由包加载器在编译期解析后注入（P0-4）；未解析时判失败并给出明确原因。
    /// </summary>
    public sealed class BtSubtreeTask : BtTaskNode
    {
        /// <summary>引用的包 id（manifest.references 里的 id）。</summary>
        public string PackageId { get; set; }

        /// <summary>入口节点 id（包内某棵子树的根）；为空表示引用该包的入口。</summary>
        public string EntryId { get; set; }

        /// <summary>由加载器注入的子树根。</summary>
        public BtNode Subtree { get; set; }

        public override string NodeType
        {
            get { return "Task.Subtree"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (Subtree == null)
            {
                context.Warn("Subtree: unresolved reference '" + (PackageId ?? "?")
                    + "#" + (EntryId ?? "entry") + "' -> Failed");
                return BtResult.Failed;
            }

            Subtree.ResetSubtreeState();
            return TickSubtree(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            if (Subtree == null)
                return BtResult.Failed;
            return TickSubtree(context);
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            if (Subtree != null && Subtree.IsActive)
                Subtree.AbortSubtree(context, "subtree task exited");
        }

        private BtResult TickSubtree(BtContext context)
        {
            BtResult result = Subtree.TickWithDecorators(context);
            if (result == BtResult.InProgress)
                return BtResult.InProgress;

            // 子树跑完一轮：成功/失败原样向上报，同时把子树重置以便下次重新开始
            Subtree.ResetSubtreeState();
            return result;
        }

        public override string ToString()
        {
            return base.ToString() + " -> " + (PackageId ?? "?") + "#" + (EntryId ?? "entry");
        }
    }

    /// <summary>
    /// 播放动作包（`.scatpak`）—— P1 的真回放（计划 §6.3）。
    ///
    /// 行为：
    ///   · `packages` 按顺序播放（Sequence）；每个包按**录制节奏**把逐帧输入交给同一个执行器；
    ///   · `repeat` 重复整段；`abortOnFail` 决定某个包失败时是否直接判本节点失败；
    ///   · 漂移超限 / 输入不可用 / 包不可回放 → **Failed**，交回行为树决策（可重试、可换分支）；
    ///   · 结束/失败/被中断都会释放输入（OnExit 铁律）。
    ///
    /// 包文件按"与树包同路径"解析（计划 §4.4）：`<树包目录>/<名字>.scatpak`。
    /// </summary>
    public sealed class BtPlayActionPackageTask : BtTaskNode
    {
        private ScatPlayer m_player;
        private int m_index;
        private int m_loop;

        /// <summary>要播放的动作包（一条或多条；相对树包同路径的文件名）。</summary>
        public List<string> Packages { get; } = new List<string>();

        /// <summary>Sequence = 依次播完；SingleOne = 只播第一个；RandomOne = 随机播一个。</summary>
        public string Mode { get; set; } = "Sequence";

        public int Repeat { get; set; } = 1;

        public bool AbortOnFail { get; set; } = true;

        /// <summary>最近一次失败原因（日志与 ai.status 用）。</summary>
        public string LastError { get; private set; }

        /// <summary>已经播完的包数（观测用）。</summary>
        public int CompletedCount { get; private set; }

        public override string NodeType
        {
            get { return "Task.PlayActionPackage"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            StopPlayer("restart");

            if (Packages.Count == 0)
            {
                LastError = "no action package is configured";
                context.Warn("PlayActionPackage: " + LastError + " -> Failed");
                return BtResult.Failed;
            }

            if (string.Equals(Mode, "Parallel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Mode, "RaceFirstSuccess", StringComparison.OrdinalIgnoreCase))
            {
                // 并行/竞速留到后续（要多个执行器会话互不干扰，不能半吊子实现）
                LastError = "mode '" + Mode + "' is not implemented yet (Sequence/SingleOne/RandomOne are)";
                context.Warn("PlayActionPackage: " + LastError + " -> Failed");
                return BtResult.Failed;
            }

            m_index = 0;
            m_loop = 0;
            CompletedCount = 0;

            if (string.Equals(Mode, "RandomOne", StringComparison.OrdinalIgnoreCase)
                && Packages.Count > 1)
            {
                m_index = s_random.Next(Packages.Count);
            }

            return StartCurrent(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            if (m_player == null)
                return StartCurrent(context);

            BtResult result = m_player.Tick(context.DeltaTime);
            if (result == BtResult.InProgress)
                return BtResult.InProgress;

            if (result == BtResult.Failed)
            {
                LastError = m_player.LastError ?? "replay failed";
                context.Warn("PlayActionPackage: " + LastError + " (drift="
                    + m_player.LastDrift.Describe() + ")");
                return AbortOnFail ? BtResult.Failed : NextPackage(context);
            }

            return NextPackage(context);
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            StopPlayer(result == BtResult.Aborted ? "aborted" : "finished");
        }

        private BtResult NextPackage(BtContext context)
        {
            CompletedCount++;
            StopPlayer("package done");

            m_index++;
            bool sequenceDone = string.Equals(Mode, "SingleOne", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Mode, "RandomOne", StringComparison.OrdinalIgnoreCase)
                || m_index >= Packages.Count;

            if (!sequenceDone)
                return StartCurrent(context);

            m_loop++;
            if (Repeat > 0 && m_loop >= Repeat)
            {
                context.Log("PlayActionPackage: done (" + CompletedCount + " package run(s))");
                return BtResult.Succeeded;
            }

            m_index = 0;
            return StartCurrent(context);
        }

        private BtResult StartCurrent(BtContext context)
        {
            if (m_index < 0 || m_index >= Packages.Count)
                return BtResult.Failed;

            string path = ResolvePackagePath(context, Packages[m_index]);
            if (path == null)
            {
                LastError = "action package not found: " + Packages[m_index];
                context.Warn("PlayActionPackage: " + LastError + " (looked next to the tree package: "
                    + (context.Runtime != null ? context.Runtime.SourcePackage : "?") + ")");
                return AbortOnFail ? BtResult.Failed : NextPackage(context);
            }

            ScatActionPackage package;
            PackageReport report;
            if (!ScatValidator.TryLoad(path, out package, out report) || !package.CanReplay)
            {
                LastError = "action package is not replayable: "
                    + (report.FirstError != null ? report.FirstError.Describe() : report.Summary());
                context.Warn("PlayActionPackage: " + LastError);
                return AbortOnFail ? BtResult.Failed : NextPackage(context);
            }

            m_player = new ScatPlayer(package, context.Actuators, context.Sensors)
            {
                Repeat = 1
            };
            m_player.Start();
            if (m_player.State != ScatPlayState.Playing)
            {
                LastError = m_player.LastError ?? "replay could not start";
                context.Warn("PlayActionPackage: " + LastError);
                return AbortOnFail ? BtResult.Failed : NextPackage(context);
            }

            context.Log("PlayActionPackage: playing " + package.Describe());
            return BtResult.InProgress;
        }

        /// <summary>按"与树包同路径"解析动作包（计划 §4.4）。</summary>
        public static string ResolvePackagePath(BtContext context, string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath))
                return null;

            string fileName = nameOrPath;
            if (!fileName.EndsWith(ScatManifest.Extension, StringComparison.OrdinalIgnoreCase))
                fileName += ScatManifest.Extension;

            if (System.IO.Path.IsPathRooted(fileName))
                return System.IO.File.Exists(fileName) ? fileName : null;

            string sourcePath = context != null && context.Runtime != null
                ? context.Runtime.SourcePackage : null;
            if (!string.IsNullOrEmpty(sourcePath))
            {
                string directory = System.IO.Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    string candidate = System.IO.Path.Combine(directory, fileName);
                    if (System.IO.File.Exists(candidate))
                        return candidate;
                }
            }
            return null;
        }

        private void StopPlayer(string reason)
        {
            if (m_player != null)
                m_player.Stop(reason);
            m_player = null;
        }

        private static readonly System.Random s_random = new System.Random();

        public override string ToString()
        {
            string list = Packages.Count > 0 ? string.Join(",", Packages.ToArray()) : "<none>";
            return base.ToString() + " [" + list + " mode=" + Mode + " repeat=" + Repeat + "]";
        }
    }

    /// <summary>
    /// `Task.UiClick`：按**语义目标**点一个 UI 元素。
    ///
    /// 用户要求（原话）："把相应的方法做成 CmdBridgeMod 能提供的服务，在行为树编辑器中，
    /// 要能够使用来获取坐标或点击对象。避免硬编码由于分辨率变化或窗口尺寸变化导致无法使用"。
    /// 所以这里**不存坐标**：只写目标，点在哪个像素由 CmdBridge 的 UI 服务在**点击那一刻**现算。
    ///
    /// 目标写法（`target`）：
    ///   · `Play` / `Content` —— 控件名或文本（当前屏幕里唯一匹配才行，歧义会如实报错）
    ///   · `[MainMenuScreen#0]/…/Play` —— 完整路径（最精确）
    ///   · `list:WorldsList@Rebritish` —— 列表里文字含 `Rebritish` 的那一行
    ///   · `list:WorldsList#0` —— 列表第 0 行
    ///
    /// 点法（`mode`）：
    ///   · `direct`（默认）：单帧合成"按下→抬起"，引擎自己派生 `Tap`+`Click`，
    ///     控件自己的逻辑（`IsClicked`、列表选中、点击音）照常跑；
    ///   · `input`：多帧软光标会话；
    ///   · `invoke`：直接触发控件自己的"按下事件"（能触发才有效；**绕过输入层**，明确要这么用时才选）。
    ///
    /// `waitSeconds`（默认 3）：目标现在还不存在时（屏幕切场动画中间、列表还在填）**等它就绪再点**，
    /// 等不到就 Failed —— 这正是"回放里选世界那一步悄悄丢掉"的根因对策。
    /// </summary>
    public sealed class BtUiClickTask : BtTaskNode
    {
        /// <summary>语义目标（必填）。</summary>
        public string Target { get; set; }

        /// <summary>`direct` / `input` / `invoke`。</summary>
        public string Mode { get; set; } = "direct";

        /// <summary>目标还没出现时最多等多久（秒）；0 = 不等待，立刻按"点不到"处理。</summary>
        public float WaitSeconds { get; set; } = 3f;

        /// <summary>连点几次（同一个目标）。</summary>
        public int Repeat { get; set; } = 1;

        private int m_done;
        private float m_nextWarnTime;

        public override string NodeType
        {
            get { return "Task.UiClick"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            m_done = 0;
            m_nextWarnTime = 0f;
            if (string.IsNullOrEmpty(Target))
            {
                context.Warn("UiClick: no target is configured -> Failed");
                return BtResult.Failed;
            }
            return Step(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            return Step(context);
        }

        private BtResult Step(BtContext context)
        {
            if (context.Actuators == null)
            {
                context.Warn("UiClick: no actuator (CmdBridgeMod is not loaded?) -> Failed");
                return BtResult.Failed;
            }

            if (context.Actuators.UiClick(Target, string.IsNullOrEmpty(Mode) ? "direct" : Mode))
            {
                m_done++;
                context.Log("UiClick: clicked '" + Target + "' (" + m_done + "/"
                    + Math.Max(1, Repeat) + ")");
                return m_done >= Math.Max(1, Repeat) ? BtResult.Succeeded : BtResult.InProgress;
            }

            if (ActiveTime >= WaitSeconds)
            {
                context.Warn("UiClick: '" + Target + "' never became clickable within "
                    + WaitSeconds.ToString("0.0") + "s -> Failed (没有被假装成点过)");
                return BtResult.Failed;
            }

            if (ActiveTime >= m_nextWarnTime)
            {
                m_nextWarnTime = ActiveTime + 1f;
                context.Log("UiClick: '" + Target + "' not clickable yet, waiting…");
            }
            return BtResult.InProgress;
        }

        public override string ToString()
        {
            return base.ToString() + " [" + (Target ?? "<none>") + " mode=" + Mode
                + " wait=" + WaitSeconds.ToString("0.0") + "s repeat=" + Repeat + "]";
        }
    }
}

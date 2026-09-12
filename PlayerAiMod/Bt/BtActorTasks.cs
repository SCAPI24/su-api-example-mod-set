using Engine;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
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
}

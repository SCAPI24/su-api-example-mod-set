using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树任务：**把池里的一个包当"子行为"跑一遍**（plan §4.8 的树内落点）。
    ///
    /// 与运行时换树的区别（这一点必须写清，否则会用错）：
    ///   · **本节点不换活动树的根** —— 它把池项编译好的根挂在自己下面 tick，
    ///     所以**父树正在跑的那一步不会丢**（`TreeLibrary.Switch` 用的是
    ///     `ReplaceRoot(migrate:false)`，跨树不迁移运行态；从树内部切根等于把父树截断）。
    ///   · **整树切换**（`PoolScheduler` 的 `Run` 决策）必须发生在**树的步骤边界**，
    ///     由运行时执行 —— 那是"换一整套流程"，与本节点的"调用一段子行为"是两件事。
    ///
    /// 参数化：`packages` 支持多个（`mode`）：
    ///   `SingleOne`（默认，取第一条）/ `Sequence`（依次跑完）/
    ///   `RandomOne`（随机一条）/ `FromBlackboard`（从 `packageKey` 读**运行时决定**的包名 ——
    ///   Laya 判定的落点）。
    ///
    /// 失败语义：包找不到 / 编译不过 → **Failed + 稳定错误码**，可选写黑板失败标记（供异常分支）。
    /// </summary>
    public sealed class BtPoolCallTask : BtTaskNode
    {
        /// <summary>池项（包名或路径；可多条，语义由 <see cref="Mode"/> 决定）。</summary>
        public List<string> Packages { get; } = new List<string>();

        /// <summary>`SingleOne` / `Sequence` / `RandomOne` / `FromBlackboard`。</summary>
        public string Mode { get; set; } = "SingleOne";

        /// <summary>`FromBlackboard` 时从哪个黑板 string 键取包名（Laya / 其它节点写的）。</summary>
        public string PackageKey { get; set; }

        /// <summary>整段重复几次（≥1）。</summary>
        public int Repeat { get; set; } = 1;

        /// <summary>失败时写失败标记（错误码 / 原因）到黑板。</summary>
        public bool WriteFailKey { get; set; } = true;
        public string FailKey { get; set; } = "pool.fail";
        public string ReasonKey { get; set; } = "pool.reason";

        public string LastErrorCode { get; private set; }
        public string LastError { get; private set; }

        /// <summary>当前正在跑/刚跑完的池项（观测）。</summary>
        public string CurrentPackage { get; private set; }

        private static readonly Random s_random = new Random();
        private BtNode m_subtree;
        private int m_index;
        private int m_loop;

        public override string NodeType
        {
            get { return "Task.PoolCall"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            LastErrorCode = null;
            LastError = null;
            m_loop = 0;
            m_index = 0;

            string first;
            if (!ResolveNextPackage(context, true, out first, out string resolveError))
                return Fail(context, "pool_no_package", resolveError);

            if (!LoadSubtree(first, out string loadError))
                return Fail(context, "pool_load_failed", loadError);

            context.Log("PoolCall: running '" + first + "' (mode=" + Mode + ")");
            return TickSubtree(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            if (m_subtree == null)
                return BtResult.Failed;
            return TickSubtree(context);
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            // 铁律：任何结束路径都要收尾子行为（否则它的输入/状态会漏到下一段）
            if (m_subtree != null && m_subtree.IsActive)
                m_subtree.AbortSubtree(context, "pool call exited");
            m_subtree = null;
        }

        public override void ResetState()
        {
            base.ResetState();
            m_subtree = null;
            m_index = 0;
            m_loop = 0;
            LastErrorCode = null;
            LastError = null;
        }

        public override string ToString()
        {
            string list = Packages.Count > 0 ? string.Join(",", Packages.ToArray()) : "<none>";
            return base.ToString() + " pool[" + Mode + "]=" + list + (CurrentPackage != null ? " now=" + CurrentPackage : string.Empty);
        }

        // ---------------------------------------------------------------- 内部

        /// <summary>
        /// 解析"这一段该跑哪个包"。返回 false 时 <paramref name="error"/> 给原因。
        /// `first==true` 表示这是本次激活的第一次（Sequence 模式从 0 开始）。
        /// </summary>
        private bool ResolveNextPackage(BtContext context, bool first, out string package, out string error)
        {
            error = null;
            package = null;

            string mode = string.IsNullOrEmpty(Mode) ? "SingleOne" : Mode.Trim();

            if (string.Equals(mode, "FromBlackboard", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(PackageKey) || context.Blackboard == null)
                {
                    error = "mode=FromBlackboard needs 'packageKey' (a blackboard string key)";
                    return false;
                }
                string fromBoard;
                if (!context.Blackboard.TryGet<string>(PackageKey, out fromBoard)
                    || string.IsNullOrEmpty(fromBoard))
                {
                    // 计划里的包名还没定 —— 明确失败，别猜（与 Task.RunActionScript 的 scriptKey 同规矩）
                    error = "blackboard key '" + PackageKey + "' has no package name yet";
                    return false;
                }
                package = fromBoard.Trim();
                return true;
            }

            if (Packages.Count == 0)
            {
                error = "node has no 'packages' configured";
                return false;
            }

            if (string.Equals(mode, "Sequence", StringComparison.OrdinalIgnoreCase))
            {
                if (m_index >= Packages.Count)
                {
                    error = "the sequence finished";
                    return false;
                }
                package = Packages[m_index];
                return true;
            }

            if (string.Equals(mode, "RandomOne", StringComparison.OrdinalIgnoreCase) && Packages.Count > 1)
            {
                package = Packages[s_random.Next(Packages.Count)];
                return true;
            }

            // SingleOne（默认）：永远取第一条
            package = Packages[0];
            return true;
        }

        private bool LoadSubtree(string package, out string error)
        {
            error = null;
            TreeLibrary library = PoolRuntimeHost.Library;
            if (library == null)
            {
                error = "the tree library is not available (package folders not ready)";
                return false;
            }

            CompiledTree compiled;
            if (!library.TryGetPreparedRoot(package, out compiled, out error))
                return false;

            m_subtree = compiled.Root;
            CurrentPackage = package;
            return true;
        }

        private BtResult TickSubtree(BtContext context)
        {
            if (m_subtree == null)
                return BtResult.Failed;

            BtResult result = m_subtree.TickWithDecorators(context);
            if (result == BtResult.InProgress)
                return BtResult.InProgress;

            // 这一段跑完：重置子行为（常驻对象图必须清运行态，S2），
            // 然后按 mode 决定"还有下一段吗"。
            m_subtree.ResetSubtreeState();
            m_subtree = null;

            if (result == BtResult.Failed)
                return Fail(context, "pool_item_failed", "pool item '" + CurrentPackage + "' failed");

            string mode = string.IsNullOrEmpty(Mode) ? "SingleOne" : Mode.Trim();
            if (string.Equals(mode, "Sequence", StringComparison.OrdinalIgnoreCase))
            {
                m_index++;
                if (m_index < Packages.Count)
                {
                    if (!LoadSubtree(Packages[m_index], out string nextError))
                        return Fail(context, "pool_load_failed", nextError);
                    return TickSubtree(context);
                }
            }

            m_loop++;
            if (m_loop < Math.Max(1, Repeat))
            {
                m_index = 0;
                string again;
                if (!ResolveNextPackage(context, true, out again, out string againError))
                    return Fail(context, "pool_no_package", againError);
                if (!LoadSubtree(again, out string reloadError))
                    return Fail(context, "pool_load_failed", reloadError);
                return TickSubtree(context);
            }

            context.Log("PoolCall: done (last='" + (CurrentPackage ?? "?") + "' loops=" + m_loop + ")");
            return BtResult.Succeeded;
        }

        private BtResult Fail(BtContext context, string code, string reason)
        {
            LastErrorCode = string.IsNullOrEmpty(code) ? "pool_failed" : code;
            LastError = reason ?? LastErrorCode;
            if (context != null)
                context.Warn("PoolCall: " + LastErrorCode + " - " + LastError + " -> Failed");

            if (WriteFailKey && context != null && context.Blackboard != null)
            {
                if (!string.IsNullOrEmpty(FailKey))
                    context.Blackboard.Set(new AiBlackboardKey<string>(FailKey), LastErrorCode);
                if (!string.IsNullOrEmpty(ReasonKey))
                    context.Blackboard.Set(new AiBlackboardKey<string>(ReasonKey), LastError);
            }
            return BtResult.Failed;
        }
    }
}

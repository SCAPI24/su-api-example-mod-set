using Engine;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树运行时：每帧 tick 一次根节点，负责观察者中断、帧预算、异常隔离与快照。
    ///
    /// 设计要点（对齐 UE 的 `UBehaviorTreeComponent`，但按本项目的需要做了务实简化）：
    ///   1. 每帧一次 <see cref="Tick"/>，在帧首于游戏线程调用（见 PlayerAiRuntime 的调度）。
    ///   2. **观察者中断**：对全树求值带中断模式的装饰器 —— `Self` 在条件不再满足时中断自己，
    ///      `LowerPriority` 在条件满足时抢占右侧兄弟（先抢占则高优先级分支立刻接管）。
    ///      UE 用"值变化通知"做优化，这里每帧对全树求值：语义等价、实现简单，节点数不大时开销可忽略。
    ///   3. **帧预算**：单帧访问节点数有上限（默认 256），组合节点遇到预算耗尽就返回 InProgress，
    ///      把剩余工作推迟到下一帧，避免大树吃满一帧。
    ///   4. **异常隔离**：任何节点抛异常 → 中断整棵树 + 记录 LastError，不让游戏崩。
    ///   5. 整棵树跑完（Succeeded/Failed）后自动重跑（与 UE 一致）。
    ///   6. **运行态与包解耦**：内存里的树可以被 AI 改写（`IsDirty` 标记），但**不落盘**；
    ///      原包字节不变（见 doc/player-ai-plan.md §4.5）。
    /// </summary>
    public sealed class BtRuntime
    {
        private readonly AiBlackboard m_blackboard;
        private readonly BtContext m_context;

        public BtRuntime(AiBlackboard blackboard, IAiSensor sensors, IAiActuator actuators)
        {
            m_blackboard = blackboard ?? new AiBlackboard();
            m_context = new BtContext(this, m_blackboard, sensors, actuators);

            // 空树：放一个"永远失败"的占位根，避免到处判空。
            Root = new BtSequenceNode { Id = "empty", Name = "empty tree" };
        }

        /// <summary>根节点。没有载入树时是空的 Sequence（永远 Succeeded）。</summary>
        public BtNode Root { get; private set; }

        public BtContext Context
        {
            get { return m_context; }
        }

        public AiBlackboard Blackboard
        {
            get { return m_blackboard; }
        }

        // ---------------------------------------------------------------- 来源与状态元信息

        /// <summary>树标识（通常来自包 manifest 的 id）。</summary>
        public string TreeId { get; private set; }

        /// <summary>来源包路径（用于显示"这棵树是从哪来的"）。</summary>
        public string SourcePackage { get; private set; }

        /// <summary>来源包的哈希（重载比对用）。</summary>
        public string SourceHash { get; private set; }

        /// <summary>
        /// 事件日志（可选）：`Task.Emit` 往这里写"控制面事件"，人可以在
        /// `ai.logs` / `<实例根>/PlayerAi/Logs/PlayerAi.log` 里看到。
        ///
        /// 用可写属性而不是构造函数参数：纯逻辑自检里大量 `new BtRuntime(黑板, 桩, 桩)`，
        /// 为了一个可选日志去改所有调用点不值得；没接日志时 `Emit` 退化成 `Engine.Log.Information`。
        /// </summary>
        public AiEventLog EventLog { get; set; }

        /// <summary>内存里是否被改写过（AI 运行时改写；原包不影响）。</summary>
        public bool IsDirty { get; set; }

        public bool IsRunning { get; private set; }

        public BtResult LastResult { get; private set; } = BtResult.Failed;

        public long TickCount { get; private set; }

        /// <summary>树启动以来的累计时间（秒）。</summary>
        public double Time { get; private set; }

        /// <summary>单帧节点访问上限。</summary>
        public int TickBudgetPerFrame { get; set; } = 256;

        public int LastNodesVisited { get; private set; }

        /// <summary>观察者中断累计次数。</summary>
        public int AbortCount { get; private set; }

        /// <summary>整棵树跑完的次数（跑完会自动重跑）。</summary>
        public int CompletedLoops { get; private set; }

        /// <summary>根替换（热重载/切换）次数。</summary>
        public int MigrationCount { get; private set; }

        /// <summary>最近一次根替换的运行态迁移统计（计划 §5.4）。</summary>
        public BtMigrationReport LastMigration { get; private set; }

        public string LastError { get; private set; }

        // ---------------------------------------------------------------- 生命周期

        /// <summary>
        /// 换根（载入新包、热重载、AI 改写后重新编译）。
        /// 旧树会被完整收尾（释放输入），然后重置运行态。
        /// </summary>
        public void SetRoot(BtNode root, string treeId = null, string sourcePackage = null, string sourceHash = null)
        {
            if (Root != null && Root.IsActive)
                Root.AbortSubtree(m_context, "root replaced");

            Root = root ?? new BtSequenceNode { Id = "empty", Name = "empty tree" };
            TreeId = treeId;
            SourcePackage = sourcePackage;
            SourceHash = sourceHash;
            IsDirty = false;
            ResetRunState();
        }

        public void Start()
        {
            if (IsRunning)
                return;
            IsRunning = true;
            Root.ResetSubtreeState();
        }

        /// <summary>
        /// 换根并**按"来源包/id"迁移运行态**（热重载 / `ai.tree.switch` 共用，计划 §5.3）。
        ///
        /// 顺序（每一步都有原因）：
        ///   1. 收集新旧两树的运行态与键集合；
        ///   2. 旧树里"新树没有对应节点（或类型变了）"的活动分支**先正常中断收尾**
        ///      —— `OnExit` 会释放输入，不留"以为自己还在走路"的坏状态；
        ///   3. 换根 + 清空新树运行态；
        ///   4. 把键 + 类型一致、且**父链也被保留**的节点运行态套回去。
        ///      只保留连通路径，避免"父节点没跑、子节点却 active"的幽灵任务（那种任务会永远按着键）。
        ///
        /// 已知取舍（P0）：装饰器/服务内部的计时（冷却锁定、循环计数、服务下次触发时间）不迁移，
        /// 重载后从头算；要迁移得给它们也配一套状态结构，留到 P3。
        /// </summary>
        public BtMigrationReport ReplaceRoot(BtNode newRoot, bool migrate = true, string treeId = null,
            string sourcePackage = null, string sourceHash = null)
        {
            var report = new BtMigrationReport { Migrated = migrate && IsRunning && Root != null };
            newRoot = newRoot ?? new BtSequenceNode { Id = "empty", Name = "empty tree" };

            if (Root != null && report.Migrated)
            {
                var oldStates = new Dictionary<string, BtNodeRuntimeState>(StringComparer.Ordinal);
                var newStates = new Dictionary<string, BtNodeRuntimeState>(StringComparer.Ordinal);
                var oldKeys = new HashSet<string>(StringComparer.Ordinal);
                var newKeys = new HashSet<string>(StringComparer.Ordinal);
                BtMigration.Capture(Root, oldStates);
                BtMigration.CollectIds(Root, oldKeys);
                BtMigration.Capture(newRoot, newStates);
                BtMigration.CollectIds(newRoot, newKeys);

                foreach (string key in newKeys)
                {
                    if (!oldKeys.Contains(key))
                        report.AddedNodes++;
                }
                foreach (string key in oldKeys)
                {
                    if (!newKeys.Contains(key))
                        report.RemovedNodes++;
                }

                var preservable = new HashSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, BtNodeRuntimeState> pair in oldStates)
                {
                    BtNodeRuntimeState other;
                    if (newStates.TryGetValue(pair.Key, out other)
                        && string.Equals(other.NodeType, pair.Value.NodeType, StringComparison.Ordinal))
                    {
                        preservable.Add(pair.Key);
                    }
                }

                AbortUnpreserved(Root, preservable, report);
                Root = newRoot;
                Root.ResetSubtreeState();
                RestorePreserved(Root, oldStates, report, true);
            }
            else
            {
                if (Root != null && Root.IsActive)
                    Root.AbortSubtree(m_context, "root replaced");
                Root = newRoot;
                Root.ResetSubtreeState();
            }

            TreeId = treeId;
            SourcePackage = sourcePackage;
            SourceHash = sourceHash;
            IsDirty = false;

            // 换根 = 重新开始：清掉上一次的故障标记。
            // 不清的话，一次 tick 异常会让角色永远停在"故障"模式，连热重载都救不回来。
            LastError = null;

            // **没有迁移** = 这是一次"从头开始"（`ai.tree.stop` 之后再 switch、或装载新树）：
            // 运行计数（tick / 时间 / 循环）也要归零，否则编辑器上显示的还是上一轮的数字，
            // 用户会以为"没重置"（实测就是这么发现的：停止后重播，ticks 还是 6）。
            // 热重载走的是 migrate=true 那条路，进度照旧保留。
            if (!report.Migrated)
                ResetRunState();

            LastMigration = report;
            MigrationCount++;
            return report;
        }

        /// <summary>
        /// 中断旧树里"新树不保留"的活动分支。
        /// 自顶向下：活动节点一旦不保留就整棵中断收尾（`AbortSubtree` 会连带子树），并统计其内部活动节点数，
        /// 不再往下重复处理；不活动的分支继续下探（它下面可能挂着活动子节点）。
        /// </summary>
        private void AbortUnpreserved(BtNode node, HashSet<string> preservable, BtMigrationReport report)
        {
            if (node == null)
                return;

            string key = BtMigration.Key(node);
            bool preserved = key != null && preservable.Contains(key);

            if (preserved || !node.IsActive)
            {
                foreach (BtNode child in node.Children)
                    AbortUnpreserved(child, preservable, report);

                BtSubtreeTask subtree = node as BtSubtreeTask;
                if (subtree != null)
                    AbortUnpreserved(subtree.Subtree, preservable, report);
                return;
            }

            report.AbortedNodes += BtMigration.CountActive(node);
            node.AbortSubtree(m_context, "reload: node not present in the new tree");
        }

        /// <summary>
        /// 把运行态套回新树：只有"父链一路上都被保留"的节点才恢复
        /// （否则会出现父节点不在活动路径、子节点却 active 的幽灵任务）。
        /// </summary>
        private void RestorePreserved(BtNode node, Dictionary<string, BtNodeRuntimeState> oldStates,
            BtMigrationReport report, bool parentPreserved)
        {
            if (node == null)
                return;

            bool preserved = false;
            string key = BtMigration.Key(node);
            BtNodeRuntimeState state;
            if (parentPreserved && key != null && oldStates.TryGetValue(key, out state)
                && string.Equals(state.NodeType, node.NodeType, StringComparison.Ordinal))
            {
                node.RestoreRuntimeState(state);
                report.PreservedNodes++;
                preserved = true;
            }

            foreach (BtNode child in node.Children)
                RestorePreserved(child, oldStates, report, preserved);

            BtSubtreeTask subtree = node as BtSubtreeTask;
            if (subtree != null)
                RestorePreserved(subtree.Subtree, oldStates, report, preserved);
        }

        public void Stop(string reason)
        {
            if (!IsRunning)
                return;
            IsRunning = false;
            try
            {
                Root.AbortSubtree(m_context, reason ?? "stopped");
            }
            catch (Exception exception)
            {
                LastError = exception.GetType().Name + ": " + exception.Message;
            }
            Root.ResetSubtreeState();
        }

        public void ResetRunState()
        {
            Root.ResetSubtreeState();
            LastResult = BtResult.Failed;
            TickCount = 0;
            Time = 0.0;
            LastNodesVisited = 0;
            AbortCount = 0;
            CompletedLoops = 0;
            LastError = null;
        }

        // ---------------------------------------------------------------- 每帧

        public void Tick(float deltaTime)
        {
            if (!IsRunning || Root == null)
                return;

            if (deltaTime < 0f)
                deltaTime = 0f;
            if (deltaTime > 0.5f)
                deltaTime = 0.5f; // 卡顿/断点后不要一次补太多时间

            try
            {
                TickCount++;
                Time += deltaTime;
                m_context.DeltaTime = deltaTime;
                m_context.Time = Time;
                m_context.TickIndex = TickCount;
                m_context.NodesVisited = 0;

                // 1) 观察者中断：条件满足的高优先级分支抢占右侧；条件失效的自身分支被中断
                ApplyObserverAborts(Root);

                // 2) 正常 tick（预算从这一趟开始算）
                m_context.NodesVisited = 0;
                BtResult result = Root.TickWithDecorators(m_context);
                LastResult = result;
                LastNodesVisited = m_context.NodesVisited;

                // 3) 跑完整棵树 → 重新开始（UE 行为：行为树循环执行）。
                //    例外：Root 的 `loop=false`（一次性树，例如"进游戏"这种菜单宏）——
                //    完成后直接把树停掉，而不是每隔几秒再做一遍同样的事。
                if (result != BtResult.InProgress)
                {
                    CompletedLoops++;
                    BtRootNode root = Root as BtRootNode;
                    if (root != null && !root.Loop)
                    {
                        Root.ResetSubtreeState();
                        IsRunning = false;
                        return;
                    }
                    Root.ResetSubtreeState();
                }
            }
            catch (Exception exception)
            {
                LastError = exception.GetType().Name + ": " + exception.Message;
                Engine.Log.Warning("[PlayerAi][BT] tick failed: " + LastError);
                try
                {
                    Root.AbortSubtree(m_context, "tick exception");
                }
                catch (Exception)
                {
                    // 收尾本身再失败就只能重置了。
                }
                Root.ResetSubtreeState();
            }
        }

        /// <summary>
        /// 观察者中断（对齐 UE 的 Observer Aborts，见 doc/player-ai-plan.md §3.3）。
        /// 全树求值：`Self` 条件失效 → 中断自己；`LowerPriority` 条件成立 → 抢占右侧兄弟。
        /// </summary>
        private void ApplyObserverAborts(BtNode node)
        {
            if (m_context.BudgetExhausted)
                return;

            m_context.Visit();

            for (int i = 0; i < node.Decorators.Count; i++)
            {
                BtDecorator decorator = node.Decorators[i];
                if (decorator.Abort == BtAbortMode.None)
                    continue;

                bool satisfied = decorator.CheckCondition(m_context);

                if (!satisfied
                    && (decorator.Abort == BtAbortMode.Self || decorator.Abort == BtAbortMode.Both)
                    && node.IsActive)
                {
                    node.AbortSubtree(m_context, "observer abort (self): " + decorator.Describe());
                    AbortCount++;
                }

                if (satisfied
                    && (decorator.Abort == BtAbortMode.LowerPriority || decorator.Abort == BtAbortMode.Both))
                {
                    BtCompositeNode parent = node.ParentNode;
                    if (parent != null && node.IndexInParent >= 0 && HasActiveSiblingAfter(parent, node.IndexInParent))
                    {
                        parent.AbortChildrenFrom(m_context, node.IndexInParent + 1,
                            "observer abort (lower priority): " + decorator.Describe());
                        AbortCount++;
                    }
                }
            }

            foreach (BtNode child in node.Children)
                ApplyObserverAborts(child);
        }

        private static bool HasActiveSiblingAfter(BtCompositeNode parent, int index)
        {
            for (int i = index + 1; i < parent.ChildNodes.Count; i++)
            {
                if (parent.ChildNodes[i].IsActive)
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 观测

        public BtSnapshot Snapshot()
        {
            var active = new List<BtNodeSnapshot>();
            CollectActive(Root, active);

            return new BtSnapshot
            {
                TreeId = TreeId,
                SourcePackage = SourcePackage,
                SourceHash = SourceHash,
                IsRunning = IsRunning,
                IsDirty = IsDirty,
                LastResult = LastResult,
                TickCount = TickCount,
                Time = Time,
                LastNodesVisited = LastNodesVisited,
                AbortCount = AbortCount,
                CompletedLoops = CompletedLoops,
                LastError = LastError,
                ActivePath = active
            };
        }

        private static void CollectActive(BtNode node, List<BtNodeSnapshot> into)
        {
            if (node.IsActive)
            {
                into.Add(new BtNodeSnapshot
                {
                    Id = node.Id,
                    NodeType = node.NodeType,
                    Name = node.Name,
                    IsActive = true,
                    LastResult = node.LastResult,
                    ActiveTime = node.ActiveTime,
                    ActivationCount = node.ActivationCount
                });
            }

            foreach (BtNode child in node.Children)
                CollectActive(child, into);
        }

        public override string ToString()
        {
            return "BtRuntime(" + (TreeId ?? "<none>") + ") ticks=" + TickCount
                + " last=" + LastResult + (IsDirty ? " dirty" : string.Empty);
        }
    }

    /// <summary>行为树运行快照（只读视图，供控制面/编辑器显示）。</summary>
    public sealed class BtSnapshot
    {
        public string TreeId;
        public string SourcePackage;
        public string SourceHash;
        public bool IsRunning;
        public bool IsDirty;
        public BtResult LastResult;
        public long TickCount;
        public double Time;
        public int LastNodesVisited;
        public int AbortCount;
        public int CompletedLoops;
        public string LastError;
        public List<BtNodeSnapshot> ActivePath = new List<BtNodeSnapshot>();

        /// <summary>活动路径的紧凑文本（"Selector#root > Sequence#seq > Task.Wait#w1"）。</summary>
        public string DescribeActivePath()
        {
            if (ActivePath.Count == 0)
                return "<none>";
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < ActivePath.Count; i++)
            {
                if (i > 0)
                    builder.Append(" > ");
                builder.Append(ActivePath[i].NodeType).Append('#').Append(ActivePath[i].Id ?? "?");
            }
            return builder.ToString();
        }

        public override string ToString()
        {
            return "tree=" + (TreeId ?? "<none>")
                + " running=" + IsRunning
                + " last=" + LastResult
                + " path=" + DescribeActivePath()
                + (IsDirty ? " [dirty]" : string.Empty);
        }
    }

    /// <summary>活动路径上单个节点的状态。</summary>
    public sealed class BtNodeSnapshot
    {
        public string Id;
        public string NodeType;
        public string Name;
        public bool IsActive;
        public BtResult LastResult;
        public float ActiveTime;
        public long ActivationCount;
    }
}

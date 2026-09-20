using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树节点基类（对齐 UE 的 `UBTNode` + `UBTAuxiliaryNode` 的组合）。
    ///
    /// 生命周期（与 UE 的 ExecuteTask/TickTask/AbortTask 对应）：
    ///   获得焦点(OnEnter) → OnExecute（首次） → [InProgress] → 每帧 OnTick → OnExit(结果)
    ///   中途被外部打断 → OnAbort(原因) → OnExit(Aborted)
    ///
    /// **必须在这里收尾**：任何 OnExit 都要释放本节点造成的输入/意图（例如 MoveTo 松开前进键），
    /// 否则中断后会留下"以为自己还在走路"的坏状态（与状态机那边同一条铁律）。
    ///
    /// 装饰器挂在**节点上**（与 UE 一致、也是包格式里 `decorators` 数组的语义），
    /// 由 <see cref="TickWithDecorators"/> 统一在 tick 前后套用条件与结果改写。
    /// </summary>
    public abstract class BtNode
    {
        private readonly List<BtDecorator> m_decorators = new List<BtDecorator>();

        /// <summary>包内唯一 id（编辑器与运行态迁移按它对齐）。</summary>
        public string Id { get; set; }

        /// <summary>显示名（日志/编辑器）。</summary>
        public string Name { get; set; }

        /// <summary>
        /// 本节点来自哪个包（加载器编译时写入）。
        /// id 只在**单个包内**唯一，嵌套多个包后同名 id 会撞车，所以运行态迁移与日志都用"包/id"定位节点。
        /// </summary>
        public string SourcePackageId { get; set; }

        /// <summary>节点类型标识（供包格式与编辑器注册表使用，例如 "Selector" / "Task.Wait"）。</summary>
        public abstract string NodeType { get; }

        internal BtCompositeNode ParentNode { get; set; }

        internal int IndexInParent { get; set; } = -1;

        /// <summary>挂在它上面的装饰器。</summary>
        public IReadOnlyList<BtDecorator> Decorators
        {
            get { return m_decorators; }
        }

        public BtNode AddDecorator(BtDecorator decorator)
        {
            if (decorator == null)
                throw new ArgumentNullException(nameof(decorator));
            decorator.Host = this;
            m_decorators.Add(decorator);
            return this;
        }

        /// <summary>是否处于"获得焦点"状态（还没结束）。</summary>
        public bool IsActive { get; private set; }

        public BtResult LastResult { get; private set; } = BtResult.Failed;

        /// <summary>本次获得焦点后经过的时间（秒）。</summary>
        public float ActiveTime { get; private set; }

        /// <summary>累计获得焦点次数（调试用）。</summary>
        public long ActivationCount { get; private set; }

        internal BtResult TickWithDecorators(BtContext context)
        {
            // 1) 条件装饰器：任一不满足 → 本节点本次判失败（若正在运行则先中断，保证收尾）
            for (int i = 0; i < m_decorators.Count; i++)
            {
                BtDecorator decorator = m_decorators[i];
                if (decorator.CheckCondition(context))
                    continue;

                if (IsActive)
                    AbortSubtree(context, "decorator condition failed: " + decorator.Describe());

                return BtResult.Failed;
            }

            // 2) 正常 tick
            BtResult result = Tick(context);

            // 3) 结果改写（ForceSuccess / Inverter 之类），逆序套用更符合"由内到外"的直觉
            for (int i = m_decorators.Count - 1; i >= 0; i--)
            {
                BtResult modified = m_decorators[i].ModifyResult(context, result);
                if (modified != result && modified != BtResult.InProgress && IsActive)
                {
                    // 结果被改写且节点仍在运行 → 需要收尾，避免留下半开状态
                    Finish(context, modified);
                }
                result = modified;
            }

            return result;
        }

        internal BtResult Tick(BtContext context)
        {
            context.Visit();

            if (!IsActive)
            {
                IsActive = true;
                ActiveTime = 0f;
                ActivationCount++;
                LastResult = BtResult.InProgress;
                OnEnter(context);

                BtResult immediate = OnExecute(context);
                if (immediate != BtResult.InProgress)
                    return Finish(context, immediate);

                return BtResult.InProgress;
            }

            ActiveTime += context.DeltaTime;
            BtResult result = OnTick(context);
            return result == BtResult.InProgress ? BtResult.InProgress : Finish(context, result);
        }

        private BtResult Finish(BtContext context, BtResult result)
        {
            LastResult = result;
            IsActive = false;
            OnExit(context, result);
            return result;
        }

        /// <summary>外部中断：先中断子树（深度优先），再中断自己。</summary>
        public void AbortSubtree(BtContext context, string reason)
        {
            foreach (BtNode child in Children)
                child.AbortSubtree(context, reason);

            if (!IsActive)
                return;

            OnAbort(context, reason);
            LastResult = BtResult.Aborted;
            IsActive = false;
            OnExit(context, BtResult.Aborted);
        }

        /// <summary>清空运行态（用于重载/重启，不动配置）。</summary>
        public virtual void ResetState()
        {
            IsActive = false;
            LastResult = BtResult.Failed;
            ActiveTime = 0f;
            for (int i = 0; i < m_decorators.Count; i++)
                m_decorators[i].ResetState();
        }

        // ---------------------------------------------------------------- 运行态迁移（热重载用）

        /// <summary>取出本节点的运行态（计划 §5.3：改参数不掐断正在跑的任务）。</summary>
        public virtual BtNodeRuntimeState CaptureRuntimeState()
        {
            return new BtNodeRuntimeState
            {
                NodeType = NodeType,
                IsActive = IsActive,
                ActiveTime = ActiveTime,
                ActivationCount = ActivationCount,
                LastResult = LastResult,
                CurrentChildIndex = -1
            };
        }

        /// <summary>把运行态套到本节点上（只在"id + 类型一致且父链也被保留"时调用）。</summary>
        public virtual void RestoreRuntimeState(BtNodeRuntimeState state)
        {
            IsActive = state.IsActive;
            ActiveTime = state.ActiveTime;
            ActivationCount = state.ActivationCount;
            LastResult = state.LastResult;
        }

        // ---------------------------------------------------------------- 派生类重写点

        protected virtual void OnEnter(BtContext context) { }

        /// <summary>首次获得焦点时执行；返回 InProgress 表示跨帧任务。</summary>
        protected virtual BtResult OnExecute(BtContext context)
        {
            return BtResult.Succeeded;
        }

        /// <summary>跨帧任务的每帧推进。</summary>
        protected virtual BtResult OnTick(BtContext context)
        {
            return BtResult.Succeeded;
        }

        /// <summary>离开焦点（正常结束或被中断都要走这里收尾）。</summary>
        protected virtual void OnExit(BtContext context, BtResult result) { }

        /// <summary>被中断时的额外通知（一般不需要重写，OnExit 里统一收尾即可）。</summary>
        protected virtual void OnAbort(BtContext context, string reason) { }

        // ---------------------------------------------------------------- 树结构工具

        public virtual IEnumerable<BtNode> Children
        {
            get { yield break; }
        }

        /// <summary>深度优先遍历（含自身）。</summary>
        public IEnumerable<BtNode> Walk()
        {
            yield return this;
            foreach (BtNode child in Children)
            {
                foreach (BtNode node in child.Walk())
                    yield return node;
            }
        }

        public BtNode Find(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (BtNode node in Walk())
            {
                if (string.Equals(node.Id, id, StringComparison.Ordinal))
                    return node;
            }
            return null;
        }

        /// <summary>把整棵树重置成"未运行"状态。</summary>
        public void ResetSubtreeState()
        {
            ResetState();
            foreach (BtNode child in Children)
                child.ResetSubtreeState();
        }

        public override string ToString()
        {
            return NodeType + "#" + (Id ?? "?") + (string.IsNullOrEmpty(Name) ? string.Empty : "(" + Name + ")");
        }
    }
}

using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 组合节点基类（对齐 UE 的 `UBTCompositeNode`）：
    /// 持有子节点、记忆"当前活跃子节点下标"（不每帧从头重选，避免抖动）、并驱动挂载的服务。
    /// </summary>
    public abstract class BtCompositeNode : BtNode
    {
        private readonly List<BtNode> m_children = new List<BtNode>();
        private readonly List<BtService> m_services = new List<BtService>();

        public IReadOnlyList<BtNode> ChildNodes
        {
            get { return m_children; }
        }

        public IReadOnlyList<BtService> Services
        {
            get { return m_services; }
        }

        public override IEnumerable<BtNode> Children
        {
            get { return m_children; }
        }

        /// <summary>当前活跃/正在尝试的子节点下标；-1 表示需要从头开始。</summary>
        public int CurrentChildIndex { get; protected set; } = -1;

        public BtNode CurrentChild
        {
            get
            {
                return CurrentChildIndex >= 0 && CurrentChildIndex < m_children.Count
                    ? m_children[CurrentChildIndex]
                    : null;
            }
        }

        public BtCompositeNode AddChild(BtNode child)
        {
            if (child == null)
                throw new ArgumentNullException(nameof(child));
            child.ParentNode = this;
            child.IndexInParent = m_children.Count;
            m_children.Add(child);
            return this;
        }

        public BtCompositeNode AddChildren(params BtNode[] children)
        {
            if (children != null)
            {
                for (int i = 0; i < children.Length; i++)
                    AddChild(children[i]);
            }
            return this;
        }

        /// <summary>
        /// 在指定位置插入子节点（P0-12 运行时改写用；<paramref name="index"/> 越界则追加）。
        /// 会同步维护 <see cref="BtNode.IndexInParent"/> 与 <see cref="CurrentChildIndex"/>：
        /// 后者是"本组合当前正在推进哪个孩子"的记忆，插在前面就得往后挪，否则会指错孩子。
        /// </summary>
        public bool InsertChild(int index, BtNode child)
        {
            if (child == null)
                return false;

            int clamped = index < 0 || index > m_children.Count ? m_children.Count : index;
            child.ParentNode = this;
            m_children.Insert(clamped, child);
            Reindex();

            if (CurrentChildIndex >= clamped && CurrentChildIndex >= 0)
                CurrentChildIndex++;

            return true;
        }

        /// <summary>摘掉指定子节点（调用方负责先给被摘的子树收尾，例如 AbortSubtree）。</summary>
        public bool RemoveChild(BtNode child)
        {
            if (child == null)
                return false;

            int index = m_children.IndexOf(child);
            if (index < 0)
                return false;

            DetachAt(index);
            return true;
        }

        public bool RemoveChildAt(int index)
        {
            if (index < 0 || index >= m_children.Count)
                return false;

            DetachAt(index);
            return true;
        }

        /// <summary>把某个子节点移到新位置（同一父节点内重排）。</summary>
        public bool MoveChild(BtNode child, int index)
        {
            if (child == null)
                return false;

            int from = m_children.IndexOf(child);
            if (from < 0)
                return false;

            int to = index < 0 ? 0 : (index > m_children.Count - 1 ? m_children.Count - 1 : index);
            if (from == to)
                return true;

            m_children.RemoveAt(from);
            m_children.Insert(to, child);
            Reindex();

            // 运行态记忆跟着孩子走：只关心"当前活跃下标"指向的那个孩子有没有移动
            if (CurrentChildIndex == from)
            {
                CurrentChildIndex = to;
            }
            else if (from < CurrentChildIndex && to >= CurrentChildIndex)
            {
                CurrentChildIndex--;
            }
            else if (from > CurrentChildIndex && to <= CurrentChildIndex)
            {
                CurrentChildIndex++;
            }
            return true;
        }

        /// <summary>重排所有子节点的 <see cref="BtNode.IndexInParent"/>（增删移之后必须调）。</summary>
        public void Reindex()
        {
            for (int i = 0; i < m_children.Count; i++)
                m_children[i].IndexInParent = i;
        }

        private void DetachAt(int index)
        {
            BtNode child = m_children[index];
            child.ParentNode = null;
            child.IndexInParent = -1;
            m_children.RemoveAt(index);
            Reindex();

            // 活跃下标失效就回到"重新选择"（-1），否则组合会指向错位的孩子
            if (CurrentChildIndex == index)
                CurrentChildIndex = -1;
            else if (CurrentChildIndex > index)
                CurrentChildIndex--;
        }

        public BtCompositeNode AddService(BtService service)
        {
            if (service == null)
                throw new ArgumentNullException(nameof(service));
            m_services.Add(service);
            return this;
        }

        public BtNode GetChild(int index)
        {
            return index >= 0 && index < m_children.Count ? m_children[index] : null;
        }

        public override void ResetState()
        {
            base.ResetState();
            CurrentChildIndex = -1;
            for (int i = 0; i < m_children.Count; i++)
                m_children[i].ResetState();
            for (int i = 0; i < m_services.Count; i++)
                m_services[i].ResetState();
        }

        /// <summary>组合节点的运行态多一项："当前活跃子节点下标"（记忆了就不必从头重选）。</summary>
        public override BtNodeRuntimeState CaptureRuntimeState()
        {
            BtNodeRuntimeState state = base.CaptureRuntimeState();
            state.CurrentChildIndex = CurrentChildIndex;
            return state;
        }

        public override void RestoreRuntimeState(BtNodeRuntimeState state)
        {
            base.RestoreRuntimeState(state);
            CurrentChildIndex = state.CurrentChildIndex;
        }

        protected override BtResult OnExecute(BtContext context)
        {
            CurrentChildIndex = 0;
            TickServices(context);
            return TickChildren(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            TickServices(context);
            return TickChildren(context);
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            // 组合节点离开焦点时，活动中的子节点必须收尾（释放输入/撤销意图）。
            for (int i = 0; i < m_children.Count; i++)
            {
                if (m_children[i].IsActive)
                    m_children[i].AbortSubtree(context, "composite exited: " + NodeType);
            }
            CurrentChildIndex = -1;
        }

        /// <summary>子节点选择与推进（由具体组合语义实现）。</summary>
        protected abstract BtResult TickChildren(BtContext context);

        /// <summary>
        /// 中断指定下标及其右侧的兄弟（`LowerPriority` 语义：更高优先级的分支抢走执行权）。
        /// </summary>
        public void AbortChildrenFrom(BtContext context, int index, string reason)
        {
            int from = Math.Max(0, index);
            for (int i = from; i < m_children.Count; i++)
                m_children[i].AbortSubtree(context, reason);

            if (CurrentChildIndex >= from)
                CurrentChildIndex = -1; // 让下一次 tick 重新选择
        }

        protected void TickServices(BtContext context)
        {
            for (int i = 0; i < m_services.Count; i++)
                m_services[i].Tick(context);
        }
    }

    /// <summary>
    /// Selector：从左到右取第一个"非 Failed"的子节点；Running 则记住下标继续。
    /// （对齐 UE 的 `UBTComposite_Selector`）
    /// </summary>
    public sealed class BtSelectorNode : BtCompositeNode
    {
        public override string NodeType
        {
            get { return "Selector"; }
        }

        protected override BtResult TickChildren(BtContext context)
        {
            while (CurrentChildIndex >= 0 && CurrentChildIndex < ChildNodes.Count)
            {
                if (context.BudgetExhausted)
                    return BtResult.InProgress;

                BtNode child = ChildNodes[CurrentChildIndex];
                BtResult result = child.TickWithDecorators(context);

                if (result == BtResult.InProgress)
                    return BtResult.InProgress; // 记住下标，下帧继续

                if (result == BtResult.Succeeded)
                {
                    CurrentChildIndex = -1;
                    return BtResult.Succeeded;
                }

                if (result == BtResult.Aborted)
                {
                    CurrentChildIndex = -1;
                    return BtResult.Aborted;
                }

                CurrentChildIndex++; // Failed → 试下一个
            }

            CurrentChildIndex = -1;
            return BtResult.Failed;
        }
    }

    /// <summary>
    /// Sequence：从左到右依次执行，任一 Failed 立即失败；Running 则记住下标继续。
    /// （对齐 UE 的 `UBTComposite_Sequence`）
    /// </summary>
    public sealed class BtSequenceNode : BtCompositeNode
    {
        public override string NodeType
        {
            get { return "Sequence"; }
        }

        protected override BtResult TickChildren(BtContext context)
        {
            while (CurrentChildIndex >= 0 && CurrentChildIndex < ChildNodes.Count)
            {
                if (context.BudgetExhausted)
                    return BtResult.InProgress;

                BtNode child = ChildNodes[CurrentChildIndex];
                BtResult result = child.TickWithDecorators(context);

                if (result == BtResult.InProgress)
                    return BtResult.InProgress;

                if (result == BtResult.Failed)
                {
                    CurrentChildIndex = -1;
                    return BtResult.Failed;
                }

                if (result == BtResult.Aborted)
                {
                    CurrentChildIndex = -1;
                    return BtResult.Aborted;
                }

                CurrentChildIndex++; // Succeeded → 下一个
            }

            CurrentChildIndex = -1;
            return BtResult.Succeeded;
        }
    }

    /// <summary>
    /// SimpleParallel：第一个子节点是"主任务"，其余是后台子树；主任务结束时后台被中断。
    /// （P0 先做最小可用版本：主任务 + 后台子树并存；更细的结束模式留到 P3）
    /// </summary>
    public sealed class BtSimpleParallelNode : BtCompositeNode
    {
        public override string NodeType
        {
            get { return "SimpleParallel"; }
        }

        protected override BtResult TickChildren(BtContext context)
        {
            BtNode main = GetChild(0);
            if (main == null)
                return BtResult.Failed;

            BtResult mainResult = main.TickWithDecorators(context);

            // 主任务运行期间，后台子树继续跑；主任务一结束就中断它们。
            for (int i = 1; i < ChildNodes.Count; i++)
            {
                BtNode background = ChildNodes[i];
                if (mainResult == BtResult.InProgress || background.IsActive)
                    background.TickWithDecorators(context);
            }

            if (mainResult != BtResult.InProgress)
            {
                for (int i = 1; i < ChildNodes.Count; i++)
                    ChildNodes[i].AbortSubtree(context, "SimpleParallel main finished");
            }

            CurrentChildIndex = mainResult == BtResult.InProgress ? 0 : -1;
            return mainResult;
        }
    }

    /// <summary>
    /// 树根（对齐 UE 的 `UBTComposite_Root`）：按顺序执行子节点，全部成功才算成功。
    /// 语义与 Sequence 相同，单独成类只为了两件事：
    ///   1. 包格式里树的顶层节点有明确的类型（`"type": "Root"`）；
    ///   2. 校验器能对"根只应有一个子节点"给出提示（UE 也是如此）。
    /// </summary>
    public sealed class BtRootNode : BtCompositeNode
    {
        /// <summary>
        /// 跑完整棵树之后要不要从头再来（默认 true = UE 的循环语义）。
        ///
        /// 设成 false 就是"一次性"：完成后 <see cref="BtRuntime"/> 会把树停掉
        /// （`IsRunning=false`）。菜单宏（「进入游戏」这类"做一次就完事"的树）必须这么写 ——
        /// 否则它会每隔几秒再点一次 Play（实测就是这么发现的一直在点菜单）。
        /// </summary>
        public bool Loop { get; set; } = true;

        public override string NodeType
        {
            get { return "Root"; }
        }

        protected override BtResult TickChildren(BtContext context)
        {
            while (CurrentChildIndex >= 0 && CurrentChildIndex < ChildNodes.Count)
            {
                if (context.BudgetExhausted)
                    return BtResult.InProgress;

                BtNode child = ChildNodes[CurrentChildIndex];
                BtResult result = child.TickWithDecorators(context);

                if (result == BtResult.InProgress)
                    return BtResult.InProgress;

                if (result == BtResult.Failed)
                {
                    CurrentChildIndex = -1;
                    return BtResult.Failed;
                }

                if (result == BtResult.Aborted)
                {
                    CurrentChildIndex = -1;
                    return BtResult.Aborted;
                }

                CurrentChildIndex++;
            }

            CurrentChildIndex = -1;
            return BtResult.Succeeded;
        }
    }
}

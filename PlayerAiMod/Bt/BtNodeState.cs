using System;
using System.Collections.Generic;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 一个节点的**运行态**（不含配置）—— 热重载时按节点 id 迁移的就是这些字段。
    ///
    /// 为什么需要它：改一个参数（`acceptableRadius` 3→6）后，正在跑的"走向目标"任务不应该被掐断重来
    /// （那会松开前进键、丢掉已走的进度）。计划 §5.3 的"按节点 id 尽量保留"就落在这个结构上。
    /// </summary>
    public struct BtNodeRuntimeState
    {
        /// <summary>类型也必须一致才迁移（id 相同但类型换了 = 另一个节点）。</summary>
        public string NodeType;

        public bool IsActive;

        public float ActiveTime;

        public long ActivationCount;

        public BtResult LastResult;

        /// <summary>组合节点的活跃子节点下标（-1 = 尚未选择）。</summary>
        public int CurrentChildIndex;

        public override string ToString()
        {
            return (NodeType ?? "?") + (IsActive ? " active" : " idle")
                + " t=" + ActiveTime.ToString("0.000") + " child=" + CurrentChildIndex;
        }
    }

    /// <summary>
    /// 一次根替换（热重载 / 切换树）的运行态迁移统计 —— 计划 §5.4 要求它出现在日志与 `ai.status` 里。
    /// </summary>
    public sealed class BtMigrationReport
    {
        /// <summary>新旧树中 id + 类型一致、且**父链也被保留**的运行中节点数。</summary>
        public int PreservedNodes;

        /// <summary>旧树里在运行、但新树里没了（或类型变了）而被正常中断收尾的节点数。</summary>
        public int AbortedNodes;

        /// <summary>新树里新增的节点 id 数（相对旧树）。</summary>
        public int AddedNodes;

        /// <summary>旧树里存在、新树里消失的节点 id 数。</summary>
        public int RemovedNodes;

        /// <summary>是否做了迁移（false = 整树重启）。</summary>
        public bool Migrated;

        public string Describe()
        {
            if (!Migrated)
                return "restarted (no migration)";
            return "preserved=" + PreservedNodes + " aborted=" + AbortedNodes
                + " added=" + AddedNodes + " removed=" + RemovedNodes;
        }

        public override string ToString()
        {
            return "BtMigrationReport(" + Describe() + ")";
        }
    }

    /// <summary>迁移用到的集合工具（新旧树的 id 对照）。</summary>
    internal static class BtMigration
    {
        /// <summary>
        /// 迁移键 = "来源包 id / 节点 id"（没有来源标记时退化为节点 id）。
        /// 用复合键是因为 id 只在单包内唯一：两个嵌套包各有一个 `root` 是完全合法的。
        /// </summary>
        public static string Key(BtNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Id))
                return null;
            return string.IsNullOrEmpty(node.SourcePackageId)
                ? node.Id
                : node.SourcePackageId + "/" + node.Id;
        }

        /// <summary>
        /// 收集节点运行态（含 `Task.Subtree` 挂进来的嵌套子树 —— 嵌套包里的任务同样要迁移）。
        /// </summary>
        public static void Capture(BtNode node, Dictionary<string, BtNodeRuntimeState> into)
        {
            if (node == null || string.IsNullOrEmpty(node.Id))
                return;

            string key = Key(node);
            if (key != null && !into.ContainsKey(key))
                into.Add(key, node.CaptureRuntimeState());

            foreach (BtNode child in node.Children)
                Capture(child, into);

            BtSubtreeTask subtree = node as BtSubtreeTask;
            if (subtree != null)
                Capture(subtree.Subtree, into);
        }

        /// <summary>收集 id 集合（判断新增/删除用）。</summary>
        public static void CollectIds(BtNode node, HashSet<string> into)
        {
            if (node == null)
                return;

            string key = Key(node);
            if (key != null)
                into.Add(key);

            foreach (BtNode child in node.Children)
                CollectIds(child, into);

            BtSubtreeTask subtree = node as BtSubtreeTask;
            if (subtree != null)
                CollectIds(subtree.Subtree, into);
        }

        /// <summary>某个节点子树里"正在运行"的节点数（中断统计用）。</summary>
        public static int CountActive(BtNode node)
        {
            if (node == null)
                return 0;

            int count = node.IsActive ? 1 : 0;
            foreach (BtNode child in node.Children)
                count += CountActive(child);

            BtSubtreeTask subtree = node as BtSubtreeTask;
            if (subtree != null)
                count += CountActive(subtree.Subtree);
            return count;
        }
    }
}

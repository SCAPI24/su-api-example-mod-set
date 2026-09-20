using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>一次内存改写的种类（日志与控制面要能区分）。</summary>
    public enum TreeEditKind
    {
        Set,
        Insert,
        Remove,
        Move
    }

    /// <summary>一次内存改写的结果。</summary>
    public sealed class TreeEditResult
    {
        public bool Applied;

        public TreeEditKind Kind;

        public string NodeId;

        public string Reason;

        public string Detail;

        /// <summary>改写后活动树的节点数（含嵌套子树）。</summary>
        public int NodeCount;

        public readonly List<string> Issues = new List<string>();

        public string Describe()
        {
            if (!Applied)
                return Kind.ToString().ToLowerInvariant() + "#" + (NodeId ?? "?") + " failed: "
                    + (Reason ?? "?") + (Issues.Count > 0 ? " (" + Issues[0] + ")" : string.Empty);
            return Kind.ToString().ToLowerInvariant() + "#" + (NodeId ?? "?") + " ok"
                + (string.IsNullOrEmpty(Detail) ? string.Empty : " " + Detail)
                + " nodes=" + NodeCount;
        }

        public override string ToString()
        {
            return "TreeEditResult(" + Describe() + ")";
        }
    }

    /// <summary>
    /// 内存态改写（P0-12，计划 §4.5）。
    ///
    /// 铁律：**只改内存里那棵活树**。
    ///   · 不写回任何 `.scbtpak`（原包整局游戏字节不变，验收标准 7 就是比这个哈希）；
    ///   · 不产生任何隐式落盘；
    ///   · 想固化？只有人通过 `ai.tree.export` 另存成**新包**。
    ///
    /// 为什么不需要 undo：内存改写随时可以用 `ai.tree.reload` 从原包重新装载来丢弃
    /// （这也是"原包不动"的价值 —— 它永远是可回退的基线）。
    ///
    /// 线程/时序：改写会动运行中的对象图，**必须**在游戏线程的 tick 边界执行
    /// （控制面命令天然满足：扩展命令默认在游戏线程执行）。
    /// </summary>
    public static class TreeMutation
    {
        /// <summary>按节点 id 找节点（会走 `Task.Subtree` 里的嵌套子树）。</summary>
        public static BtNode Find(BtNode root, string id)
        {
            if (root == null || string.IsNullOrEmpty(id))
                return null;

            if (string.Equals(root.Id, id, StringComparison.Ordinal))
                return root;

            foreach (BtNode child in root.Children)
            {
                BtNode found = Find(child, id);
                if (found != null)
                    return found;
            }

            BtSubtreeTask subtree = root as BtSubtreeTask;
            if (subtree != null)
                return Find(subtree.Subtree, id);

            return null;
        }

        /// <summary>改一个已有节点的参数（最常用：改 `acceptableRadius` 3 → 6）。</summary>
        public static TreeEditResult SetProperty(BtRuntime runtime, string nodeId, string name,
            PackageValue value, PackageValue propertiesOverride = null)
        {
            var result = new TreeEditResult { Kind = TreeEditKind.Set, NodeId = nodeId };
            if (runtime == null || runtime.Root == null)
            {
                result.Reason = "no live tree";
                return result;
            }

            BtNode node = Find(runtime.Root, nodeId);
            if (node == null)
            {
                result.Reason = "node not found";
                return result;
            }
            if (string.IsNullOrEmpty(name))
            {
                result.Reason = "a property name is required";
                return result;
            }

            // 以"节点当前的全部属性"为底再改一个 —— 这样单改一个参数不会把别的参数打回默认值
            PackageValue properties = propertiesOverride ?? TreeWriter.CaptureProperties(node);
            if (properties == null || !properties.IsObject)
            {
                result.Reason = "node type '" + node.NodeType + "' has no editable properties";
                return result;
            }

            properties.Set(name, value);

            var report = new PackageReport();
            bool ok = TreeCompiler.TryApplyProperties(node, properties, report, out string detail);
            result.Detail = detail;
            result.Issues.AddRange(report.Summarize(4));
            if (!ok)
            {
                result.Reason = "property could not be applied";
                return result;
            }

            runtime.IsDirty = true;
            result.Applied = true;
            result.NodeCount = Count(runtime.Root);
            return result;
        }

        /// <summary>在父节点下插入一个新节点（节点定义按包格式给：`type`/`id`/`properties`）。</summary>
        public static TreeEditResult Insert(BtRuntime runtime, string parentId, int index,
            ScbtNodeDoc doc, LoadedPackage owner, PackageReport report = null)
        {
            var result = new TreeEditResult { Kind = TreeEditKind.Insert, NodeId = doc != null ? doc.Id : null };
            if (runtime == null || runtime.Root == null)
            {
                result.Reason = "no live tree";
                return result;
            }
            if (doc == null)
            {
                result.Reason = "a node definition is required";
                return result;
            }

            BtNode parent = Find(runtime.Root, parentId);
            if (parent == null)
            {
                result.Reason = "parent node not found";
                return result;
            }

            var composite = parent as BtCompositeNode;
            if (composite == null)
            {
                result.Reason = "parent '" + parent.NodeType + "' cannot have children";
                return result;
            }

            var localReport = new PackageReport();
            BtNode node = TreeCompiler.CompileNodeDocument(doc, owner, localReport);
            if (node == null)
            {
                result.Reason = "node definition is not valid";
                result.Issues.AddRange(localReport.Summarize(4));
                return result;
            }

            if (!string.IsNullOrEmpty(node.Id) && Find(runtime.Root, node.Id) != null)
            {
                result.Reason = "node id '" + node.Id + "' already exists (ids must be unique)";
                return result;
            }

            // 插入位置空出来时，父节点原有的"活跃下标"必须跟着挪（InsertChild 里已处理）
            composite.InsertChild(index, node);

            runtime.IsDirty = true;
            result.Applied = true;
            result.Detail = "into " + parentId + " at " + (index < 0 ? "end" : index.ToString())
                + " type=" + node.NodeType;
            result.NodeCount = Count(runtime.Root);
            if (report != null)
                report.AddRange(localReport);
            return result;
        }

        /// <summary>摘掉一个节点（先给子树正常收尾，避免"被摘掉却还按着键"）。</summary>
        public static TreeEditResult Remove(BtRuntime runtime, string nodeId, bool force = false)
        {
            var result = new TreeEditResult { Kind = TreeEditKind.Remove, NodeId = nodeId };
            if (runtime == null || runtime.Root == null)
            {
                result.Reason = "no live tree";
                return result;
            }

            BtNode node = Find(runtime.Root, nodeId);
            if (node == null)
            {
                result.Reason = "node not found";
                return result;
            }
            if (ReferenceEquals(node, runtime.Root))
            {
                result.Reason = "the root cannot be removed (switch or reload instead)";
                return result;
            }

            var parent = node.ParentNode;
            if (parent == null)
            {
                result.Reason = "node has no parent";
                return result;
            }
            if (parent.ChildNodes.Count <= 1 && !force)
            {
                result.Reason = "removing it would leave '" + parent.Id
                    + "' without children; pass force=true if that is intended";
                return result;
            }

            // 收尾：被摘掉的子树若正在跑，先走一次正常中断（OnExit 释放输入）
            if (node.IsActive)
                node.AbortSubtree(runtime.Context, "removed by ai.edit.remove");

            if (!parent.RemoveChild(node))
            {
                result.Reason = "parent refused the removal";
                return result;
            }

            runtime.IsDirty = true;
            result.Applied = true;
            result.Detail = "from " + (parent.Id ?? "?");
            result.NodeCount = Count(runtime.Root);
            return result;
        }

        /// <summary>换父 / 换位置（"换分支"的最小可用形式）。</summary>
        public static TreeEditResult Move(BtRuntime runtime, string nodeId, string newParentId, int index)
        {
            var result = new TreeEditResult { Kind = TreeEditKind.Move, NodeId = nodeId };
            if (runtime == null || runtime.Root == null)
            {
                result.Reason = "no live tree";
                return result;
            }

            BtNode node = Find(runtime.Root, nodeId);
            if (node == null)
            {
                result.Reason = "node not found";
                return result;
            }
            if (ReferenceEquals(node, runtime.Root))
            {
                result.Reason = "the root cannot be moved";
                return result;
            }

            BtNode target = Find(runtime.Root, newParentId);
            var targetComposite = target as BtCompositeNode;
            if (targetComposite == null)
            {
                result.Reason = "target parent not found or cannot have children";
                return result;
            }

            // 不允许把节点挪进自己的子树（否则形成环，tick 会无限递归）
            for (BtNode walk = target; walk != null; walk = walk.ParentNode)
            {
                if (ReferenceEquals(walk, node))
                {
                    result.Reason = "cannot move a node into its own subtree";
                    return result;
                }
            }

            var oldParent = node.ParentNode;
            if (ReferenceEquals(oldParent, targetComposite))
            {
                if (!targetComposite.MoveChild(node, index))
                {
                    result.Reason = "reorder refused";
                    return result;
                }
            }
            else
            {
                if (node.IsActive)
                    node.AbortSubtree(runtime.Context, "moved by ai.edit.move");

                if (oldParent != null)
                    oldParent.RemoveChild(node);
                targetComposite.InsertChild(index, node);
            }

            runtime.IsDirty = true;
            result.Applied = true;
            result.Detail = "to " + newParentId + " at " + (index < 0 ? "end" : index.ToString());
            result.NodeCount = Count(runtime.Root);
            return result;
        }

        /// <summary>活动树节点数（含嵌套子树）—— 控制面与日志用。</summary>
        public static int Count(BtNode root)
        {
            if (root == null)
                return 0;

            int count = 1;
            foreach (BtNode child in root.Children)
                count += Count(child);

            BtSubtreeTask subtree = root as BtSubtreeTask;
            if (subtree != null)
                count += Count(subtree.Subtree);
            return count;
        }
    }
}

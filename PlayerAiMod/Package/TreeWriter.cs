using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 把**内存里的活树**写回包格式（P0-13 导出 / P0-12 改单个参数都要用它）。
    ///
    /// 它是 <see cref="TreeCompiler"/> 属性映射的**反向实现**：
    ///   · <see cref="CaptureProperties"/> 把一个节点当前的全部属性读成 JSON（供"改一个参数后原样套用"）；
    ///   · <see cref="NodeToValue"/> 把节点（含装饰器/服务/子树引用）写成 tree.json 的节点对象。
    ///
    /// 两条刻意的约定：
    ///   1. `Task.Subtree` 只写 `package`/`entry` 引用，**不把被引用包的节点摊平** ——
    ///      嵌套关系是包之间的引用，导出要保留这种结构，否则一导出就"炸开"了；
    ///   2. 导出只写"能表达的部分"：运行态（跑到哪、计时）不属于包格式，自然不会出现在产物里。
    /// </summary>
    public static class TreeWriter
    {
        /// <summary>把一个节点的当前属性读成 JSON 对象（缺省值也会写全，便于人工阅读与再编辑）。</summary>
        public static PackageValue CaptureProperties(BtNode node)
        {
            PackageValue properties = PackageValue.Object();
            if (node == null)
                return properties;

            switch (node.NodeType)
            {
                case "Task.Wait":
                {
                    var task = (BtWaitTask)node;
                    properties.Set("seconds", PackageValue.Number(task.Seconds));
                    break;
                }
                case "Task.SetBlackboard":
                {
                    var task = (BtSetBlackboardTask)node;
                    properties.Set("key", PackageValue.Str(task.Key));
                    properties.Set("valueKind", PackageValue.Str(task.ValueKind ?? "float"));
                    switch ((task.ValueKind ?? "float").Trim().ToLowerInvariant())
                    {
                        case "bool":
                            properties.Set("value", PackageValue.Bool(task.BoolValue));
                            break;
                        case "int":
                            properties.Set("value", PackageValue.Number(task.IntValue));
                            break;
                        case "string":
                            properties.Set("value", PackageValue.Str(task.StringValue));
                            break;
                        default:
                            properties.Set("value", PackageValue.Number(task.FloatValue));
                            break;
                    }
                    break;
                }
                case "Task.Log":
                {
                    var task = (BtLogTask)node;
                    properties.Set("message", PackageValue.Str(task.Message));
                    properties.Set("succeed", PackageValue.Bool(task.Succeed));
                    break;
                }
                case "Task.LookAt":
                {
                    var task = (BtLookAtTargetTask)node;
                    properties.Set("targetKey", PackageValue.Str(task.TargetKey));
                    properties.Set("eyeHeight", PackageValue.Number(task.EyeHeight));
                    properties.Set("seconds", PackageValue.Number(task.Seconds));
                    break;
                }
                case "Task.MoveTo":
                {
                    var task = (BtMoveToTargetTask)node;
                    properties.Set("targetKey", PackageValue.Str(task.TargetKey));
                    properties.Set("acceptableRadius", PackageValue.Number(task.AcceptableRadius));
                    properties.Set("timeout", PackageValue.Number(task.TimeoutSeconds));
                    properties.Set("forwardKey", PackageValue.Str(task.ForwardKey));
                    properties.Set("eyeHeight", PackageValue.Number(task.EyeHeight));
                    break;
                }
                case "Task.WaitForTarget":
                {
                    var task = (BtWaitForTargetTask)node;
                    properties.Set("targetKey", PackageValue.Str(task.TargetKey));
                    properties.Set("timeout", PackageValue.Number(task.TimeoutSeconds));
                    break;
                }
                case "Task.Subtree":
                {
                    var task = (BtSubtreeTask)node;
                    properties.Set("package", PackageValue.Str(task.PackageId));
                    if (!string.IsNullOrEmpty(task.EntryId))
                        properties.Set("entry", PackageValue.Str(task.EntryId));
                    break;
                }
                case "Task.PlayActionPackage":
                {
                    var task = (BtPlayActionPackageTask)node;
                    PackageValue packages = PackageValue.Array();
                    for (int i = 0; i < task.Packages.Count; i++)
                        packages.Add(PackageValue.Str(task.Packages[i]));
                    properties.Set("packages", packages);
                    properties.Set("mode", PackageValue.Str(task.Mode ?? "Sequence"));
                    properties.Set("repeat", PackageValue.Number(task.Repeat));
                    properties.Set("abortOnFail", PackageValue.Bool(task.AbortOnFail));
                    break;
                }
            }

            return properties;
        }

        /// <summary>把装饰器的类型专属属性读成 JSON（base 字段单独写）。</summary>
        public static PackageValue CaptureDecoratorProperties(BtDecorator decorator)
        {
            PackageValue properties = PackageValue.Object();
            if (decorator == null)
                return properties;

            switch (decorator.NodeType)
            {
                case "Blackboard":
                {
                    var target = (BtBlackboardDecorator)decorator;
                    properties.Set("key", PackageValue.Str(target.Key));
                    properties.Set("query", PackageValue.Str(target.Query ?? "IsSet"));
                    properties.Set("operator", PackageValue.Str(target.Operator ?? "=="));
                    properties.Set("valueKind", PackageValue.Str(target.ValueKind ?? "bool"));
                    properties.Set("value", CaptureValue(target.ValueKind, target.BoolValue,
                        target.IntValue, target.FloatValue, target.StringValue));
                    break;
                }
                case "Cooldown":
                    properties.Set("cooldownSeconds",
                        PackageValue.Number(((BtCooldownDecorator)decorator).CooldownSeconds));
                    break;
                case "TimeLimit":
                    properties.Set("limitSeconds",
                        PackageValue.Number(((BtTimeLimitDecorator)decorator).LimitSeconds));
                    break;
                case "Loop":
                {
                    var target = (BtLoopDecorator)decorator;
                    properties.Set("numLoops", PackageValue.Number(target.NumLoops));
                    properties.Set("infiniteLoop", PackageValue.Bool(target.InfiniteLoop));
                    properties.Set("infiniteLoopTimeoutSeconds",
                        PackageValue.Number(target.InfiniteLoopTimeoutSeconds));
                    break;
                }
            }

            return properties;
        }

        /// <summary>把服务的类型专属属性读成 JSON。</summary>
        public static PackageValue CaptureServiceProperties(BtService service)
        {
            PackageValue properties = PackageValue.Object();
            var target = service as BtUpdateNearestPlayerService;
            if (target != null)
            {
                properties.Set("targetKey", PackageValue.Str(target.TargetKey));
                if (!string.IsNullOrEmpty(target.NameFilter))
                    properties.Set("nameFilter", PackageValue.Str(target.NameFilter));
                properties.Set("clearWhenMissing", PackageValue.Bool(target.ClearWhenMissing));
            }
            return properties;
        }

        private static PackageValue CaptureValue(string valueKind, bool boolValue, int intValue,
            float floatValue, string stringValue)
        {
            switch ((valueKind ?? "float").Trim().ToLowerInvariant())
            {
                case "bool":
                    return PackageValue.Bool(boolValue);
                case "int":
                    return PackageValue.Number(intValue);
                case "string":
                    return PackageValue.Str(stringValue);
                default:
                    return PackageValue.Number(floatValue);
            }
        }

        /// <summary>把一个节点（含装饰器/服务/子节点）写成 tree.json 的节点对象。</summary>
        public static PackageValue NodeToValue(BtNode node)
        {
            if (node == null)
                return PackageValue.Object();

            PackageValue value = PackageValue.Object();
            value.Set("id", PackageValue.Str(node.Id));
            value.Set("type", PackageValue.Str(node.NodeType));
            if (!string.IsNullOrEmpty(node.Name))
                value.Set("name", PackageValue.Str(node.Name));

            PackageValue properties = CaptureProperties(node);
            if (properties.Count > 0)
                value.Set("properties", properties);

            if (node.Decorators.Count > 0)
            {
                PackageValue decorators = PackageValue.Array();
                for (int i = 0; i < node.Decorators.Count; i++)
                {
                    BtDecorator decorator = node.Decorators[i];
                    PackageValue item = PackageValue.Object();
                    item.Set("id", PackageValue.Str(decorator.Id));
                    item.Set("type", PackageValue.Str(decorator.NodeType));
                    if (decorator.Inverse)
                        item.Set("inverse", PackageValue.Bool(true));
                    if (decorator.Abort != BtAbortMode.None)
                        item.Set("observerAborts", PackageValue.Str(decorator.Abort.ToString()));

                    PackageValue decoratorProperties = CaptureDecoratorProperties(decorator);
                    if (decoratorProperties.Count > 0)
                        item.Set("properties", decoratorProperties);
                    decorators.Add(item);
                }
                value.Set("decorators", decorators);
            }

            BtCompositeNode composite = node as BtCompositeNode;
            if (composite != null && composite.Services.Count > 0)
            {
                PackageValue services = PackageValue.Array();
                for (int i = 0; i < composite.Services.Count; i++)
                {
                    BtService service = composite.Services[i];
                    PackageValue item = PackageValue.Object();
                    item.Set("id", PackageValue.Str(service.Id));
                    item.Set("type", PackageValue.Str(service.NodeType));
                    item.Set("interval", PackageValue.Number(service.Interval));
                    if (service.RandomDeviation != 0f)
                        item.Set("randomDeviation", PackageValue.Number(service.RandomDeviation));
                    if (!service.TickOnActivation)
                        item.Set("tickOnActivation", PackageValue.Bool(false));

                    PackageValue serviceProperties = CaptureServiceProperties(service);
                    if (serviceProperties.Count > 0)
                        item.Set("properties", serviceProperties);
                    services.Add(item);
                }
                value.Set("services", services);
            }

            if (node.Children != null)
            {
                PackageValue children = PackageValue.Array();
                foreach (BtNode child in node.Children)
                    children.Add(NodeToValue(child));
                if (children.Count > 0)
                    value.Set("children", children);
            }

            return value;
        }

        /// <summary>
        /// 导出成一棵 tree.json 的根对象。
        /// <paramref name="entryId"/> 为空时用根节点的 id（导出产物的 manifest.entry 应写同一个值）。
        /// </summary>
        public static PackageValue TreeToValue(BtNode root, out string entryId)
        {
            entryId = root != null ? root.Id : null;
            return NodeToValue(root);
        }

        /// <summary>
        /// 导出活动树为**新包**（P0-13）。
        ///
        /// 铁律（计划 §4.5）：**只写新文件，绝不碰原包**；文件名与目录都由调用方给，
        /// 目标目录固定是实例包目录（用户可写）。写入走原子写。
        /// </summary>
        public static bool TryExportPackage(BtRuntime runtime, ScbtManifest sourceManifest,
            string fileNameOrId, string targetDirectory, out string path, out string error,
            out int nodeCount)
        {
            path = null;
            error = null;
            nodeCount = 0;

            if (runtime == null || runtime.Root == null)
            {
                error = "there is no live tree to export";
                return false;
            }
            if (string.IsNullOrEmpty(targetDirectory))
            {
                error = "no writable folder for packages (PlayerAi/BehaviorTrees)";
                return false;
            }

            string safeName = AiRecordingSession.SanitizeName(fileNameOrId);
            if (string.IsNullOrEmpty(safeName))
            {
                error = "a file name is required (letters/digits/_/-/., max 64)";
                return false;
            }

            string entryId;
            PackageValue tree = TreeToValue(runtime.Root, out entryId);

            // manifest 以来源包为底（保留 references 等字段），只改身份与入口
            ScbtManifest manifest = sourceManifest != null ? CloningManifest(sourceManifest) : new ScbtManifest();
            manifest.Id = safeName;
            manifest.Name = safeName;
            manifest.Entry = entryId;

            path = System.IO.Path.Combine(targetDirectory, safeName + PackageRoots.Extension);
            byte[] bytes = PackageWriter.ToBytes(manifest.ToValue(), tree, null);
            if (!PackageWriter.TryWriteFile(path, bytes, out error))
            {
                path = null;
                return false;
            }

            nodeCount = runtime.Root.Walk() != null ? CountNodes(runtime.Root) : 0;
            return true;
        }

        private static ScbtManifest CloningManifest(ScbtManifest source)
        {
            var clone = new ScbtManifest
            {
                Raw = source.Raw,
                Format = source.Format,
                Version = source.Version,
                Id = source.Id,
                Name = source.Name,
                Entry = source.Entry
            };
            for (int i = 0; i < source.Blackboard.Count; i++)
                clone.Blackboard.Add(source.Blackboard[i]);
            for (int i = 0; i < source.References.Count; i++)
                clone.References.Add(source.References[i]);
            return clone;
        }

        private static int CountNodes(BtNode node)
        {
            int count = 1;
            foreach (BtNode child in node.Children)
                count += CountNodes(child);

            BtSubtreeTask subtree = node as BtSubtreeTask;
            if (subtree != null && subtree.Subtree != null)
                count += CountNodes(subtree.Subtree);
            return count;
        }
    }
}

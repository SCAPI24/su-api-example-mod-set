using System;
using System.Collections.Generic;
using System.Text;

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
                case "Task.FollowEntity":
                {
                    var task = (BtFollowEntityTask)node;
                    properties.Set("targetKey", PackageValue.Str(task.TargetKey));
                    properties.Set("mode", PackageValue.Str(task.Mode));
                    properties.Set("keepDistance", PackageValue.Number(task.KeepDistance));
                    properties.Set("standHysteresis", PackageValue.Number(task.StandHysteresis));
                    properties.Set("trailLength", PackageValue.Number(task.TrailLength));
                    properties.Set("trailMaxAge", PackageValue.Number(task.TrailMaxAge));
                    properties.Set("trailCellRadius", PackageValue.Number(task.TrailCellRadius));
                    properties.Set("trailStuckSeconds", PackageValue.Number(task.TrailStuckSeconds));
                    properties.Set("forwardKey", PackageValue.Str(task.ForwardKey));
                    properties.Set("backKey", PackageValue.Str(task.BackKey));
                    properties.Set("leftKey", PackageValue.Str(task.LeftKey));
                    properties.Set("rightKey", PackageValue.Str(task.RightKey));
                    properties.Set("jumpKey", PackageValue.Str(task.JumpKey));
                    properties.Set("jumpHeight", PackageValue.Number(task.JumpHeight));
                    properties.Set("eyeHeight", PackageValue.Number(task.EyeHeight));
                    properties.Set("repathSeconds", PackageValue.Number(task.RepathSeconds));
                    properties.Set("repathMoveThreshold", PackageValue.Number(task.RepathMoveThreshold));
                    properties.Set("maxPositionsToCheck", PackageValue.Number(task.MaxPositionsToCheck));
                    properties.Set("timeout", PackageValue.Number(task.TimeoutSeconds));
                    break;
                }
                case "Task.LookAtPoint":
                {
                    var task = (BtLookAtPointTask)node;
                    properties.Set("xKey", PackageValue.Str(task.XKey));
                    properties.Set("yKey", PackageValue.Str(task.YKey));
                    properties.Set("zKey", PackageValue.Str(task.ZKey));
                    properties.Set("seconds", PackageValue.Number(task.Seconds));
                    break;
                }
                case "Task.FaceEntity":
                {
                    var task = (BtFaceEntityTask)node;
                    properties.Set("targetKey", PackageValue.Str(task.TargetKey));
                    properties.Set("toleranceDegrees", PackageValue.Number(task.ToleranceDegrees));
                    properties.Set("timeout", PackageValue.Number(task.Timeout));
                    properties.Set("maxTurnPerSecond", PackageValue.Number(task.MaxTurnPerSecond));
                    break;
                }
                case "Task.NavigateTo":
                {
                    var task = (BtNavigateToTask)node;
                    properties.Set("source", PackageValue.Str(task.Source));
                    properties.Set("targetKey", PackageValue.Str(task.TargetKey));
                    properties.Set("xKey", PackageValue.Str(task.XKey));
                    properties.Set("yKey", PackageValue.Str(task.YKey));
                    properties.Set("zKey", PackageValue.Str(task.ZKey));
                    properties.Set("acceptableRadius", PackageValue.Number(task.AcceptableRadius));
                    properties.Set("timeout", PackageValue.Number(task.TimeoutSeconds));
                    properties.Set("forwardKey", PackageValue.Str(task.ForwardKey));
                    properties.Set("jumpKey", PackageValue.Str(task.JumpKey));
                    properties.Set("jumpHeight", PackageValue.Number(task.JumpHeight));
                    properties.Set("waypointRadius", PackageValue.Number(task.WaypointRadius));
                    properties.Set("repathSeconds", PackageValue.Number(task.RepathSeconds));
                    properties.Set("repathMoveThreshold", PackageValue.Number(task.RepathMoveThreshold));
                    properties.Set("maxPositionsToCheck", PackageValue.Number(task.MaxPositionsToCheck));
                    properties.Set("eyeHeight", PackageValue.Number(task.EyeHeight));
                    properties.Set("stuckSeconds", PackageValue.Number(task.StuckSeconds));
                    properties.Set("stuckDistance", PackageValue.Number(task.StuckDistance));
                    break;
                }
                case "Task.UseItem":
                {
                    var task = (BtUseItemTask)node;
                    properties.Set("button", PackageValue.Str(task.Button));
                    properties.Set("holdSeconds", PackageValue.Number(task.HoldSeconds));
                    properties.Set("repeat", PackageValue.Number(task.Repeat));
                    properties.Set("interval", PackageValue.Number(task.Interval));
                    break;
                }
                case "Task.Jump":
                {
                    var task = (BtJumpTask)node;
                    properties.Set("key", PackageValue.Str(task.Key));
                    properties.Set("times", PackageValue.Number(task.Times));
                    properties.Set("interval", PackageValue.Number(task.Interval));
                    properties.Set("alsoForward", PackageValue.Bool(task.AlsoForward));
                    properties.Set("forwardKey", PackageValue.Str(task.ForwardKey));
                    break;
                }
                case "Task.Emit":
                {
                    var task = (BtEmitTask)node;
                    properties.Set("category", PackageValue.Str(task.Category ?? "emit"));
                    if (!string.IsNullOrEmpty(task.Message))
                        properties.Set("message", PackageValue.Str(task.Message));
                    if (!string.IsNullOrEmpty(task.Key))
                        properties.Set("key", PackageValue.Str(task.Key));
                    properties.Set("alsoEngineLog", PackageValue.Bool(task.AlsoEngineLog));
                    break;
                }
                case "Task.Mine":
                {
                    var task = (BtMineBlockTask)node;
                    properties.Set("xKey", PackageValue.Str(task.XKey));
                    properties.Set("yKey", PackageValue.Str(task.YKey));
                    properties.Set("zKey", PackageValue.Str(task.ZKey));
                    properties.Set("button", PackageValue.Str(task.Button ?? "left"));
                    properties.Set("timeout", PackageValue.Number(task.TimeoutSeconds));
                    properties.Set("requireBlockPresent", PackageValue.Bool(task.RequireBlockPresent));
                    break;
                }
                case "Task.Attack":
                {
                    var task = (BtAttackTask)node;
                    properties.Set("targetKey", PackageValue.Str(task.TargetKey));
                    properties.Set("range", PackageValue.Number(task.Range));
                    properties.Set("eyeHeight", PackageValue.Number(task.EyeHeight));
                    properties.Set("timeout", PackageValue.Number(task.TimeoutSeconds));
                    properties.Set("clickInterval", PackageValue.Number(task.ClickInterval));
                    properties.Set("button", PackageValue.Str(task.Button ?? "left"));
                    break;
                }
                case "Task.Interact":
                {
                    var task = (BtInteractTask)node;
                    properties.Set("source", PackageValue.Str(task.Source ?? "cell"));
                    properties.Set("targetKey", PackageValue.Str(task.TargetKey));
                    properties.Set("xKey", PackageValue.Str(task.XKey));
                    properties.Set("yKey", PackageValue.Str(task.YKey));
                    properties.Set("zKey", PackageValue.Str(task.ZKey));
                    properties.Set("repeat", PackageValue.Number(task.Repeat));
                    properties.Set("interval", PackageValue.Number(task.Interval));
                    properties.Set("button", PackageValue.Str(task.Button ?? "right"));
                    break;
                }
                case "Task.PlaceBlock":
                {
                    var task = (BtPlaceBlockTask)node;
                    properties.Set("xKey", PackageValue.Str(task.XKey));
                    properties.Set("yKey", PackageValue.Str(task.YKey));
                    properties.Set("zKey", PackageValue.Str(task.ZKey));
                    properties.Set("repeat", PackageValue.Number(task.Repeat));
                    properties.Set("interval", PackageValue.Number(task.Interval));
                    properties.Set("button", PackageValue.Str(task.Button ?? "right"));
                    break;
                }
                case "Task.SelectSlot":
                {
                    var task = (BtSelectSlotTask)node;
                    properties.Set("slot", PackageValue.Number(task.Slot));
                    properties.Set("scroll", PackageValue.Number(task.Scroll));
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
                case "CompareBBEntries":
                {
                    var target = (BtCompareBlackboardDecorator)decorator;
                    properties.Set("keyA", PackageValue.Str(target.KeyA));
                    properties.Set("keyB", PackageValue.Str(target.KeyB));
                    properties.Set("operator", PackageValue.Str(target.Operator ?? "=="));
                    break;
                }
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
                return properties;
            }

            // ---- 传感器服务族（BtSensorServices.cs）
            var self = service as BtUpdateSelfService;
            if (self != null)
            {
                properties.Set("prefix", PackageValue.Str(self.Prefix));
                properties.Set("writePosition", PackageValue.Bool(self.WritePosition));
                return properties;
            }

            var creature = service as BtUpdateNearestCreatureService;
            if (creature != null)
            {
                properties.Set("targetKey", PackageValue.Str(creature.TargetKey));
                properties.Set("categoryMask", PackageValue.Number(creature.CategoryMask));
                properties.Set("maxDistance", PackageValue.Number(creature.MaxDistance));
                properties.Set("clearWhenMissing", PackageValue.Bool(creature.ClearWhenMissing));
                return properties;
            }

            var pickable = service as BtUpdateNearestPickableService;
            if (pickable != null)
            {
                properties.Set("targetKey", PackageValue.Str(pickable.TargetKey));
                properties.Set("maxDistance", PackageValue.Number(pickable.MaxDistance));
                properties.Set("clearWhenMissing", PackageValue.Bool(pickable.ClearWhenMissing));
                return properties;
            }

            var blockAhead = service as BtUpdateBlockAheadService;
            if (blockAhead != null)
            {
                properties.Set("key", PackageValue.Str(blockAhead.Key));
                properties.Set("maxDistance", PackageValue.Number(blockAhead.MaxDistance));
                properties.Set("pitchOffset", PackageValue.Number(blockAhead.PitchOffset));
                if (!string.IsNullOrEmpty(blockAhead.NameKey))
                    properties.Set("nameKey", PackageValue.Str(blockAhead.NameKey));
                return properties;
            }

            var lineOfSight = service as BtUpdateLineOfSightService;
            if (lineOfSight != null)
            {
                properties.Set("targetKey", PackageValue.Str(lineOfSight.TargetKey));
                properties.Set("key", PackageValue.Str(lineOfSight.Key));
                properties.Set("maxDistance", PackageValue.Number(lineOfSight.MaxDistance));
                properties.Set("targetEyeHeight", PackageValue.Number(lineOfSight.TargetEyeHeight));
                properties.Set("alsoCheckBody", PackageValue.Bool(lineOfSight.AlsoCheckBody));
                properties.Set("bodyHeight", PackageValue.Number(lineOfSight.BodyHeight));
                return properties;
            }

            var probe = service as BtProbeService;
            if (probe != null)
            {
                properties.Set("from", PackageValue.Str(probe.From));
                properties.Set("to", PackageValue.Str(probe.To));
                properties.Set("mode", PackageValue.Str(probe.Mode));
                properties.Set("key", PackageValue.Str(probe.Key));
                properties.Set("targetKey", PackageValue.Str(probe.TargetKey));
                properties.Set("targetHeight", PackageValue.Number(probe.TargetHeight));
                properties.Set("pitchOffset", PackageValue.Number(probe.PitchOffset));
                properties.Set("fromXKey", PackageValue.Str(probe.FromXKey));
                properties.Set("fromYKey", PackageValue.Str(probe.FromYKey));
                properties.Set("fromZKey", PackageValue.Str(probe.FromZKey));
                properties.Set("toXKey", PackageValue.Str(probe.ToXKey));
                properties.Set("toYKey", PackageValue.Str(probe.ToYKey));
                properties.Set("toZKey", PackageValue.Str(probe.ToZKey));
                properties.Set("maxDistance", PackageValue.Number(probe.MaxDistance));
                return properties;
            }

            var modelNode = service as BtUpdateModelNodeService;
            if (modelNode != null)
            {
                properties.Set("targetKey", PackageValue.Str(modelNode.TargetKey));
                properties.Set("nodeName", PackageValue.Str(modelNode.NodeName));
                properties.Set("prefix", PackageValue.Str(modelNode.Prefix));
                properties.Set("clearWhenMissing", PackageValue.Bool(modelNode.ClearWhenMissing));
                return properties;
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

            if (string.IsNullOrEmpty(targetDirectory))
            {
                error = "no writable folder for packages (PlayerAi/BehaviorTrees)";
                return false;
            }

            byte[] bytes;
            string contentHash;
            if (!TrySerializePackage(runtime, sourceManifest, fileNameOrId, out bytes, out contentHash,
                out error))
            {
                return false;
            }

            string safeName = AiRecordingSession.SanitizeName(fileNameOrId);
            path = System.IO.Path.Combine(targetDirectory, safeName + PackageRoots.Extension);
            if (!PackageWriter.TryWriteFile(path, bytes, out error))
            {
                path = null;
                return false;
            }

            nodeCount = runtime.Root.Walk() != null ? CountNodes(runtime.Root) : 0;
            return true;
        }

        /// <summary>
        /// 把**活树序列化成包字节，但不落盘**（P6 内存库 / 自动缓存的落点）。
        ///
        /// 与 <see cref="TryExportPackage"/> 共用同一段身份处理（manifest 以来源包为底、
        /// 只改 id/name/entry），区别只有一个：这里返回字节。这样"导出的包"与"缓存的包"
        /// **格式永远一致** —— 不存在"缓存能恢复但导出打不开"这种鬼故事。
        ///
        /// ⚠️ <paramref name="contentHash"/> 是**语义哈希**（对规范化 JSON 算的），
        /// **不能**拿 <paramref name="bytes"/> 的哈希当"变了没有"的判据：
        /// SuAPI 的 `ZipArchive.AddStream` 会给条目打 `DateTime.Now`，
        /// 于是**同一棵树每次序列化出的字节都不一样**。用它判"有没有改动"会导致
        /// 每 5 秒都当成新改动重写一次缓存（实测踩过：`.autosave/` 序号一路涨到 .4 还在涨）。
        /// </summary>
        public static bool TrySerializePackage(BtRuntime runtime, ScbtManifest sourceManifest,
            string nameOrId, out byte[] bytes, out string contentHash, out string error)
        {
            bytes = null;
            contentHash = null;
            error = null;

            if (runtime == null || runtime.Root == null)
            {
                error = "there is no live tree to capture";
                return false;
            }

            string safeName = AiRecordingSession.SanitizeName(nameOrId);
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

            PackageValue manifestValue = manifest.ToValue();
            try
            {
                // 语义哈希：规范化的 manifest + tree 文本（顺序固定、无时间戳）
                string canonical = manifestValue.ToJson(true) + "\n" + tree.ToJson(true);
                contentHash = PackageLoader.ComputeHash(new UTF8Encoding(false).GetBytes(canonical));
                bytes = PackageWriter.ToBytes(manifestValue, tree, null);
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                bytes = null;
                contentHash = null;
                return false;
            }
            return true;
        }

        /// <summary>只取语义哈希（不需要字节时用；内部仍然会序列化一次）。</summary>
        public static bool TryGetContentHash(BtRuntime runtime, ScbtManifest sourceManifest,
            string nameOrId, out string contentHash, out string error)
        {
            byte[] ignored;
            return TrySerializePackage(runtime, sourceManifest, nameOrId, out ignored, out contentHash,
                out error);
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

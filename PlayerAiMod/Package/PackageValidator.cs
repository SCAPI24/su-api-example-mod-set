using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 包校验器 —— **只有一份实现**（计划 §5.2）。
    ///
    /// 加载器、控制面（推送重载前的预检）、编辑器（保存前）都调用这里，
    /// 于是"什么算合法包"永远只有一个答案。规则来源：
    ///   · 形状与属性表来自 <see cref="BtNodeRegistry"/>（单一事实来源，不在这里重复写类型清单）；
    ///   · 结构性约束来自 doc/player-ai-plan.md §4.1 的"约束"段。
    ///
    /// 判定标准很简单：**Error 一律不允许装载**。能装进去但跑出怪行为，比装不上更糟。
    /// </summary>
    public static class PackageValidator
    {
        /// <summary>单包节点数上限（超过就说明该拆包，或者写错了递归）。</summary>
        public const int MaxNodes = 4096;

        /// <summary>树深上限（嵌套在嵌套之外还有单包内部的深度）。</summary>
        public const int MaxDepth = 32;

        public const int MaxDecoratorsPerNode = 16;

        public const int MaxServicesPerNode = 8;

        public const int MaxChildrenPerNode = 64;

        /// <summary>
        /// 校验 tree.json 的节点图。需要 manifest 来判断 Subtree 引用是否命中 references。
        /// </summary>
        public static void ValidateTree(ScbtTree tree, ScbtManifest manifest, string where,
            PackageReport report)
        {
            if (tree == null)
            {
                report.Error(PackageCodes.TreeMissing, where, "tree document is missing");
                return;
            }

            if (tree.NodeCount > MaxNodes)
            {
                report.Error(PackageCodes.TreeLimits, where,
                    "tree has " + tree.NodeCount + " nodes (limit " + MaxNodes + ")");
            }
            if (tree.MaxDepth > MaxDepth)
            {
                report.Error(PackageCodes.TreeLimits, where,
                    "tree depth is " + tree.MaxDepth + " (limit " + MaxDepth + ")");
            }

            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            var helperIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (ScbtNodeDoc node in tree.Walk())
            {
                ValidateNode(node, manifest, nodeIds, helperIds, report);
            }

            // 入口：manifest.entry 必须能在文档里找到
            if (manifest != null && !string.IsNullOrEmpty(manifest.Entry)
                && tree.Find(manifest.Entry) == null)
            {
                report.Error(PackageCodes.TreeEntryNotFound, where,
                    "manifest.entry '" + manifest.Entry + "' does not exist in " + ScbtTree.FileName);
            }
        }

        private static void ValidateNode(ScbtNodeDoc node, ScbtManifest manifest,
            HashSet<string> nodeIds, HashSet<string> helperIds, PackageReport report)
        {
            string where = node.Where;

            // ---- id
            if (string.IsNullOrEmpty(node.Id))
            {
                report.Error(PackageCodes.TreeIdMissing, where, "node id is required");
            }
            else if (!PackageJson.IsValidIdentifier(node.Id))
            {
                report.Error(PackageCodes.TreeIdPattern, where,
                    "node id must be 1-64 characters of [A-Za-z0-9._-], found '" + node.Id + "'");
            }
            else if (!nodeIds.Add(node.Id))
            {
                report.Error(PackageCodes.TreeIdDuplicate, where,
                    "duplicate node id '" + node.Id + "' (ids must be unique inside a package)");
            }

            // ---- 类型
            BtNodeInfo info = null;
            bool typeKnown = false;
            if (string.IsNullOrEmpty(node.Type))
            {
                report.Error(PackageCodes.TypeMissing, where, "node type is required");
            }
            else
            {
                string canonical;
                if (BtNodeRegistry.TryResolveNodeTypeId(node.Type, out canonical)
                    && BtNodeRegistry.TryGetNodeInfo(canonical, out info))
                {
                    typeKnown = true;
                    if (!info.PackageSerializable)
                    {
                        report.Error(PackageCodes.TypeNotSerializable, where,
                            "node type '" + canonical + "' can only be created by code (it needs a runtime delegate)");
                    }
                }
                else
                {
                    report.Error(PackageCodes.TypeUnknown, where,
                        "unknown node type '" + node.Type + "'");
                }
            }

            // ---- 形状：children / services
            if (typeKnown)
            {
                if (!info.AllowsChildren && node.Children.Count > 0)
                {
                    report.Error(PackageCodes.ShapeChildren, where,
                        "node type '" + info.TypeId + "' is a " + info.Shape.ToString().ToLowerInvariant()
                        + " and cannot have children (" + node.Children.Count + " given)");
                }
                if (info.AllowsChildren && node.Children.Count == 0)
                {
                    report.Error(PackageCodes.ShapeChildren, where,
                        "node type '" + info.TypeId + "' needs at least one child");
                }
                if (!info.AllowsServices && node.Services.Count > 0)
                {
                    report.Error(PackageCodes.ShapeServices, where,
                        "node type '" + info.TypeId + "' cannot carry services ("
                        + node.Services.Count + " given; services attach to composites)");
                }
                if (info.Shape == BtNodeShape.Root && node.Children.Count != 1)
                {
                    report.Warn(PackageCodes.ShapeRootChildren, where,
                        "a Root node normally has exactly one child (" + node.Children.Count + " given)");
                }
                if (node.Children.Count > MaxChildrenPerNode)
                {
                    report.Warn(PackageCodes.TreeLimits, where,
                        node.Children.Count + " children (limit " + MaxChildrenPerNode + ")");
                }
            }

            // ---- 属性（按注册表里的属性表逐个核对）
            if (typeKnown)
                ValidateProperties(node.Properties, where, info.Properties, null, report);

            // ---- 装饰器
            if (node.Decorators.Count > MaxDecoratorsPerNode)
            {
                report.Warn(PackageCodes.TreeLimits, where,
                    node.Decorators.Count + " decorators (limit " + MaxDecoratorsPerNode + ")");
            }
            for (int i = 0; i < node.Decorators.Count; i++)
            {
                ValidateDecorator(node.Decorators[i], helperIds, report);
            }

            // ---- 服务
            if (node.Services.Count > MaxServicesPerNode)
            {
                report.Warn(PackageCodes.TreeLimits, where,
                    node.Services.Count + " services (limit " + MaxServicesPerNode + ")");
            }
            for (int i = 0; i < node.Services.Count; i++)
            {
                ValidateService(node.Services[i], helperIds, report);
            }

            // ---- Subtree 引用必须命中 manifest.references
            if (typeKnown && string.Equals(info.TypeId, "Task.Subtree", StringComparison.Ordinal))
            {
                string package = node.Properties.Get("package").AsString(null);
                if (!string.IsNullOrEmpty(package))
                {
                    int separator = package.IndexOf('#');
                    string referenceId = separator >= 0 ? package.Substring(0, separator) : package;
                    ScbtReference reference;
                    if (manifest == null || !manifest.TryGetReference(referenceId, out reference))
                    {
                        report.Error(PackageCodes.SubtreeReference, where + ".properties.package",
                            "reference '" + referenceId + "' is not declared in manifest.references");
                    }
                }
            }
        }

        private static void ValidateDecorator(ScbtDecoratorDoc doc, HashSet<string> helperIds,
            PackageReport report)
        {
            string where = doc.Where;

            if (string.IsNullOrEmpty(doc.Id))
            {
                report.Warn(PackageCodes.TreeIdMissing, where,
                    "decorator has no id (a stable id is convenient for the editor; one will be assigned)");
            }
            else if (!PackageJson.IsValidIdentifier(doc.Id))
            {
                report.Error(PackageCodes.TreeIdPattern, where,
                    "decorator id must be 1-64 characters of [A-Za-z0-9._-], found '" + doc.Id + "'");
            }
            else if (!helperIds.Add(doc.Id))
            {
                report.Error(PackageCodes.TreeIdDuplicate, where,
                    "duplicate decorator/service id '" + doc.Id + "'");
            }

            BtDecoratorInfo info;
            if (string.IsNullOrEmpty(doc.Type))
            {
                report.Error(PackageCodes.TypeMissing, where, "decorator type is required");
                return;
            }
            string canonical;
            if (!BtNodeRegistry.TryResolveDecoratorTypeId(doc.Type, out canonical)
                || !BtNodeRegistry.TryGetDecoratorInfo(canonical, out info))
            {
                report.Error(PackageCodes.TypeUnknown, where,
                    "unknown decorator type '" + doc.Type + "'");
                return;
            }
            if (!info.PackageSerializable)
            {
                report.Error(PackageCodes.TypeNotSerializable, where,
                    "decorator type '" + canonical + "' can only be created by code");
                return;
            }

            if (doc.Properties.Count > 0)
                ValidateProperties(doc.Properties, where, info.Properties, ScbtDecoratorDoc.BasePropertyNames, report);
        }

        private static void ValidateService(ScbtServiceDoc doc, HashSet<string> helperIds,
            PackageReport report)
        {
            string where = doc.Where;

            if (string.IsNullOrEmpty(doc.Id))
            {
                report.Warn(PackageCodes.TreeIdMissing, where,
                    "service has no id (one will be assigned)");
            }
            else if (!PackageJson.IsValidIdentifier(doc.Id))
            {
                report.Error(PackageCodes.TreeIdPattern, where,
                    "service id must be 1-64 characters of [A-Za-z0-9._-], found '" + doc.Id + "'");
            }
            else if (!helperIds.Add(doc.Id))
            {
                report.Error(PackageCodes.TreeIdDuplicate, where,
                    "duplicate decorator/service id '" + doc.Id + "'");
            }

            BtServiceInfo info;
            if (string.IsNullOrEmpty(doc.Type))
            {
                report.Error(PackageCodes.TypeMissing, where, "service type is required");
                return;
            }
            string canonical;
            if (!BtNodeRegistry.TryResolveServiceTypeId(doc.Type, out canonical)
                || !BtNodeRegistry.TryGetServiceInfo(canonical, out info))
            {
                report.Error(PackageCodes.TypeUnknown, where,
                    "unknown service type '" + doc.Type + "'");
                return;
            }
            if (!info.PackageSerializable)
            {
                report.Error(PackageCodes.TypeNotSerializable, where,
                    "service type '" + canonical + "' can only be created by code");
                return;
            }

            if (doc.Interval < 0f)
            {
                report.Error(PackageCodes.PropertyValue, where + ".interval",
                    "interval must be >= 0, found " + PackageValue.FormatNumber(doc.Interval));
            }
            if (doc.RandomDeviation < 0f)
            {
                report.Error(PackageCodes.PropertyValue, where + ".randomDeviation",
                    "randomDeviation must be >= 0, found " + PackageValue.FormatNumber(doc.RandomDeviation));
            }

            if (doc.Properties.Count > 0)
                ValidateProperties(doc.Properties, where, info.Properties, null, report);
        }

        /// <summary>
        /// 通用属性核对：必填、类型、枚举候选、未识别字段提示。
        /// 类型专属的"取值是否合理"（例如 radius 必须为正）由编译期读取器在读取时判定，
        /// 因为只有它知道每个属性的语义。
        /// </summary>
        private static void ValidateProperties(PackageValue properties, string where,
            IReadOnlyList<BtPropertySpec> specs, string[] ignoreNames, PackageReport report)
        {
            if (properties == null || !properties.IsObject)
                return;

            for (int i = 0; i < specs.Count; i++)
            {
                BtPropertySpec spec = specs[i];
                if (!properties.Has(spec.Name))
                {
                    if (spec.Required)
                    {
                        report.Error(PackageCodes.PropertyRequired, where + ".properties." + spec.Name,
                            "required property '" + spec.Name + "' is missing (" + spec.Describe() + ")");
                    }
                    continue;
                }

                PackageValue value = properties.Get(spec.Name);
                string fieldWhere = where + ".properties." + spec.Name;
                switch (spec.Kind)
                {
                    case BtPropertyKind.Bool:
                        if (!value.IsBool)
                            report.Error(PackageCodes.PropertyKind, fieldWhere, DescribeMismatch("bool", value));
                        break;
                    case BtPropertyKind.Int:
                    case BtPropertyKind.Float:
                        if (!value.IsNumber)
                            report.Error(PackageCodes.PropertyKind, fieldWhere, DescribeMismatch("number", value));
                        break;
                    case BtPropertyKind.String:
                        if (!value.IsString)
                            report.Error(PackageCodes.PropertyKind, fieldWhere, DescribeMismatch("string", value));
                        break;
                    case BtPropertyKind.StringList:
                        if (!value.IsArray && !value.IsString)
                            report.Error(PackageCodes.PropertyKind, fieldWhere,
                                DescribeMismatch("string array", value));
                        break;
                    case BtPropertyKind.Enum:
                        if (!value.IsString)
                        {
                            report.Error(PackageCodes.PropertyKind, fieldWhere,
                                DescribeMismatch("enum string", value));
                            break;
                        }
                        if (!IsAllowed(value.AsString(), spec.AllowedValues))
                        {
                            report.Error(PackageCodes.PropertyEnum, fieldWhere,
                                "'" + value.AsString() + "' is not one of "
                                + string.Join("|", spec.AllowedValues));
                        }
                        break;
                }
            }

            foreach (string name in properties.MemberNames)
            {
                if (IsIgnored(name, ignoreNames))
                    continue;
                if (specs != null && HasSpec(specs, name))
                    continue;
                report.Warn(PackageCodes.PropertyUnknown, where + ".properties." + name,
                    "unknown property '" + name + "' (ignored; check spelling)");
            }
        }

        private static string DescribeMismatch(string expected, PackageValue value)
        {
            return "expected " + expected + ", found " + value.DescribeKind()
                + " (" + value.Preview(40) + ")";
        }

        private static bool IsIgnored(string name, string[] ignoreNames)
        {
            if (ignoreNames == null)
                return false;
            for (int i = 0; i < ignoreNames.Length; i++)
            {
                if (string.Equals(ignoreNames[i], name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool HasSpec(IReadOnlyList<BtPropertySpec> specs, string name)
        {
            for (int i = 0; i < specs.Count; i++)
            {
                if (string.Equals(specs[i].Name, name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool IsAllowed(string value, string[] allowed)
        {
            if (allowed == null || allowed.Length == 0)
                return true;
            for (int i = 0; i < allowed.Length; i++)
            {
                if (string.Equals(allowed[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}

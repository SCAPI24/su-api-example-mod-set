using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>tree.json 里挂在节点上的装饰器（对应 <see cref="BtDecorator"/>）。</summary>
    public sealed class ScbtDecoratorDoc
    {
        /// <summary>base 字段名：包格式里这两个键既可以写在 properties 里，也可以写在装饰器对象上。</summary>
        public static readonly string[] BasePropertyNames = { "inverse", "observerAborts", "abort" };

        public string Id;

        /// <summary>原始类型文本（校验/编译时按注册表归一化）。</summary>
        public string Type;

        public BtAbortMode Abort = BtAbortMode.None;

        public bool Inverse;

        /// <summary>类型专属属性。</summary>
        public PackageValue Properties = PackageValue.Object();

        /// <summary>原始 JSON（导出时保底）。</summary>
        public PackageValue Raw;

        public string Where = string.Empty;

        public override string ToString()
        {
            return Type + "#" + (Id ?? "?");
        }
    }

    /// <summary>tree.json 里挂在组合节点上的服务（对应 <see cref="BtService"/>）。</summary>
    public sealed class ScbtServiceDoc
    {
        public string Id;

        public string Type;

        public float Interval = 0.25f;

        public float RandomDeviation;

        public bool TickOnActivation = true;

        public PackageValue Properties = PackageValue.Object();

        public PackageValue Raw;

        public string Where = string.Empty;

        public override string ToString()
        {
            return Type + "#" + (Id ?? "?") + " interval=" + PackageValue.FormatNumber(Interval);
        }
    }

    /// <summary>tree.json 里的一个节点（纯数据，不含运行态）。</summary>
    public sealed class ScbtNodeDoc
    {
        public string Id;

        public string Type;

        public string Name;

        public PackageValue Properties = PackageValue.Object();

        public PackageValue Raw;

        public string Where = string.Empty;

        public ScbtNodeDoc Parent;

        public int IndexInParent = -1;

        public int Depth;

        public readonly List<ScbtNodeDoc> Children = new List<ScbtNodeDoc>();

        public readonly List<ScbtDecoratorDoc> Decorators = new List<ScbtDecoratorDoc>();

        public readonly List<ScbtServiceDoc> Services = new List<ScbtServiceDoc>();

        public override string ToString()
        {
            return (Type ?? "?") + "#" + (Id ?? "?")
                + (Children.Count > 0 ? " children=" + Children.Count : string.Empty);
        }
    }

    /// <summary>
    /// `.scbtpak` 的 tree.json：一棵树的**节点图文档**（嵌套 children，每个节点有唯一 id）。
    ///
    /// 文档层只负责"忠实地读出来"（含类型不符上报与原 JSON 保留）；
    /// 语义检查在 <see cref="PackageValidator"/>，编译成可执行节点在 <see cref="TreeCompiler"/>。
    /// </summary>
    public sealed class ScbtTree
    {
        public const string FileName = "tree.json";

        /// <summary>原始 JSON（导出时保底）。</summary>
        public PackageValue Raw;

        public ScbtNodeDoc Root;

        public string Where = FileName;

        public int NodeCount { get; private set; }

        public int DecoratorCount { get; private set; }

        public int ServiceCount { get; private set; }

        public int MaxDepth { get; private set; }

        public static ScbtTree Parse(PackageValue rootValue, string where, PackageReport report)
        {
            if (rootValue == null || !rootValue.IsObject)
            {
                report.Error(PackageCodes.TreeNotObject, where,
                    "tree must be a JSON object, found "
                    + (rootValue == null ? "null" : rootValue.DescribeKind()));
                return null;
            }

            var tree = new ScbtTree
            {
                Raw = rootValue.DeepClone(),
                Where = where
            };

            tree.Root = ParseNode(rootValue, where, report, tree, null, 0);
            return tree;
        }

        private static ScbtNodeDoc ParseNode(PackageValue value, string where, PackageReport report,
            ScbtTree tree, ScbtNodeDoc parent, int depth)
        {
            if (!value.IsObject)
            {
                report.Error(PackageCodes.TreeNodeNotObject, where,
                    "node must be a JSON object, found " + value.DescribeKind());
                return null;
            }

            var node = new ScbtNodeDoc
            {
                Raw = value.DeepClone(),
                Parent = parent,
                Depth = depth,
                IndexInParent = parent != null ? parent.Children.Count : -1
            };

            var reader = new PackageReader(value, where, report);
            node.Id = reader.Str("id", null);
            node.Type = reader.Str("type", null);
            node.Name = reader.Str("name", null);
            node.Properties = reader.ObjectField("properties");
            node.Where = where + "#" + (node.Id ?? "?");

            PackageValue decorators = reader.ArrayField("decorators");
            for (int i = 0; i < decorators.Count; i++)
            {
                PackageValue item = decorators.Item(i);
                string itemWhere = node.Where + ".decorators[" + i + "]";
                if (!item.IsObject)
                {
                    report.Error(PackageCodes.PropertyKind, itemWhere,
                        "decorator must be a JSON object, found " + item.DescribeKind());
                    continue;
                }
                node.Decorators.Add(ParseDecorator(item, itemWhere, report, tree));
            }

            PackageValue services = reader.ArrayField("services");
            for (int i = 0; i < services.Count; i++)
            {
                PackageValue item = services.Item(i);
                string itemWhere = node.Where + ".services[" + i + "]";
                if (!item.IsObject)
                {
                    report.Error(PackageCodes.PropertyKind, itemWhere,
                        "service must be a JSON object, found " + item.DescribeKind());
                    continue;
                }
                node.Services.Add(ParseService(item, itemWhere, report, tree));
            }

            PackageValue children = reader.ArrayField("children", PackageCodes.TreeChildrenNotArray);
            for (int i = 0; i < children.Count; i++)
            {
                ScbtNodeDoc child = ParseNode(children.Item(i),
                    node.Where + ".children[" + i + "]", report, tree, node, depth + 1);
                if (child != null)
                    node.Children.Add(child);
            }

            tree.NodeCount++;
            tree.DecoratorCount += node.Decorators.Count;
            tree.ServiceCount += node.Services.Count;
            if (depth > tree.MaxDepth)
                tree.MaxDepth = depth;

            // 注意：这里不 ReportUnknown —— 节点文档的字段集是固定且封闭的，
            // 未识别字段在解析阶段就以 warning 报出，避免"编辑器存了某字段但加载器不知道"时静默丢失。
            reader.ReportUnknown();
            return node;
        }

        private static ScbtDecoratorDoc ParseDecorator(PackageValue value, string where,
            PackageReport report, ScbtTree tree)
        {
            var doc = new ScbtDecoratorDoc
            {
                Raw = value.DeepClone(),
                Where = where
            };

            var reader = new PackageReader(value, where, report);
            doc.Id = reader.Str("id", null);
            doc.Type = reader.Str("type", null);
            doc.Properties = reader.ObjectField("properties");
            doc.Abort = ReadAbort(reader, doc.Properties, where, report);
            doc.Inverse = reader.Bool("inverse", doc.Properties.Get("inverse").AsBool(false));
            reader.ReportUnknown();
            return doc;
        }

        private static ScbtServiceDoc ParseService(PackageValue value, string where,
            PackageReport report, ScbtTree tree)
        {
            var doc = new ScbtServiceDoc
            {
                Raw = value.DeepClone(),
                Where = where
            };

            var reader = new PackageReader(value, where, report);
            doc.Id = reader.Str("id", null);
            doc.Type = reader.Str("type", null);
            doc.Interval = reader.Float("interval", 0.25f);
            doc.RandomDeviation = reader.Float("randomDeviation", 0f);
            doc.TickOnActivation = reader.Bool("tickOnActivation", true);
            doc.Properties = reader.ObjectField("properties");
            reader.ReportUnknown();
            return doc;
        }

        /// <summary>
        /// 中断模式：优先读对象顶层的 <c>observerAborts</c>（UE 的叫法），其次 <c>abort</c>，
        /// 最后落到 properties 里的同名键 —— 三种写法都接受，免得手写包时来回查文档。
        /// </summary>
        private static BtAbortMode ReadAbort(PackageReader reader, PackageValue properties,
            string where, PackageReport report)
        {
            PackageValue value = PackageValue.Null;
            string field = null;
            if (reader.Has("observerAborts"))
            {
                value = reader.Raw("observerAborts");
                field = "observerAborts";
            }
            else if (reader.Has("abort"))
            {
                value = reader.Raw("abort");
                field = "abort";
            }
            else if (properties.Has("observerAborts"))
            {
                value = properties.Get("observerAborts");
                field = "properties.observerAborts";
            }
            else if (properties.Has("abort"))
            {
                value = properties.Get("abort");
                field = "properties.abort";
            }

            if (value.IsNull)
                return BtAbortMode.None;
            if (!value.IsString)
            {
                report.Error(PackageCodes.PropertyKind, where + "." + field,
                    "expected enum string, found " + value.DescribeKind());
                return BtAbortMode.None;
            }

            string text = value.AsString();
            for (int i = 0; i < BtSchema.AbortModes.Length; i++)
            {
                if (string.Equals(BtSchema.AbortModes[i], text, StringComparison.OrdinalIgnoreCase))
                    return (BtAbortMode)Enum.Parse(typeof(BtAbortMode), BtSchema.AbortModes[i], true);
            }

            report.Error(PackageCodes.PropertyEnum, where + "." + field,
                "'" + text + "' is not one of " + string.Join("|", BtSchema.AbortModes));
            return BtAbortMode.None;
        }

        // ---------------------------------------------------------------- 查询

        public IEnumerable<ScbtNodeDoc> Walk()
        {
            if (Root == null)
                yield break;
            foreach (ScbtNodeDoc node in Walk(Root))
                yield return node;
        }

        private static IEnumerable<ScbtNodeDoc> Walk(ScbtNodeDoc node)
        {
            yield return node;
            for (int i = 0; i < node.Children.Count; i++)
            {
                foreach (ScbtNodeDoc child in Walk(node.Children[i]))
                    yield return child;
            }
        }

        public ScbtNodeDoc Find(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (ScbtNodeDoc node in Walk())
            {
                if (string.Equals(node.Id, id, StringComparison.Ordinal))
                    return node;
            }
            return null;
        }

        /// <summary>入口节点：manifest.entry 指定就取它，否则取文档根。</summary>
        public ScbtNodeDoc ResolveEntry(string entryId)
        {
            if (string.IsNullOrEmpty(entryId))
                return Root;
            return Find(entryId);
        }

        /// <summary>写回 JSON（导出与编辑器保存用；节点文档保底原字段）。</summary>
        public PackageValue ToValue()
        {
            return Root != null ? NodeToValue(Root) : PackageValue.Object();
        }

        private static PackageValue NodeToValue(ScbtNodeDoc node)
        {
            PackageValue value = node.Raw != null ? node.Raw.DeepClone() : PackageValue.Object();
            if (!value.IsObject)
                value = PackageValue.Object();

            value.Set("id", PackageValue.Str(node.Id));
            value.Set("type", PackageValue.Str(node.Type));
            if (!string.IsNullOrEmpty(node.Name))
                value.Set("name", PackageValue.Str(node.Name));
            else
                value.Remove("name");

            if (node.Properties.Count > 0)
                value.Set("properties", node.Properties.DeepClone());
            else
                value.Remove("properties");

            if (node.Decorators.Count > 0)
            {
                PackageValue list = PackageValue.Array();
                for (int i = 0; i < node.Decorators.Count; i++)
                {
                    ScbtDecoratorDoc decorator = node.Decorators[i];
                    PackageValue item = decorator.Raw != null
                        ? decorator.Raw.DeepClone() : PackageValue.Object();
                    if (!item.IsObject)
                        item = PackageValue.Object();
                    item.Set("id", PackageValue.Str(decorator.Id));
                    item.Set("type", PackageValue.Str(decorator.Type));
                    if (decorator.Inverse)
                        item.Set("inverse", PackageValue.Bool(true));
                    if (decorator.Abort != BtAbortMode.None)
                        item.Set("observerAborts", PackageValue.Str(decorator.Abort.ToString()));
                    if (decorator.Properties.Count > 0)
                        item.Set("properties", decorator.Properties.DeepClone());
                    list.Add(item);
                }
                value.Set("decorators", list);
            }
            else
            {
                value.Remove("decorators");
            }

            if (node.Services.Count > 0)
            {
                PackageValue list = PackageValue.Array();
                for (int i = 0; i < node.Services.Count; i++)
                {
                    ScbtServiceDoc service = node.Services[i];
                    PackageValue item = service.Raw != null
                        ? service.Raw.DeepClone() : PackageValue.Object();
                    if (!item.IsObject)
                        item = PackageValue.Object();
                    item.Set("id", PackageValue.Str(service.Id));
                    item.Set("type", PackageValue.Str(service.Type));
                    item.Set("interval", PackageValue.Number(service.Interval));
                    if (service.RandomDeviation != 0f)
                        item.Set("randomDeviation", PackageValue.Number(service.RandomDeviation));
                    if (!service.TickOnActivation)
                        item.Set("tickOnActivation", PackageValue.Bool(false));
                    if (service.Properties.Count > 0)
                        item.Set("properties", service.Properties.DeepClone());
                    list.Add(item);
                }
                value.Set("services", list);
            }
            else
            {
                value.Remove("services");
            }

            if (node.Children.Count > 0)
            {
                PackageValue list = PackageValue.Array();
                for (int i = 0; i < node.Children.Count; i++)
                    list.Add(NodeToValue(node.Children[i]));
                value.Set("children", list);
            }
            else
            {
                value.Remove("children");
            }

            return value;
        }

        public override string ToString()
        {
            return "ScbtTree(nodes=" + NodeCount + " decorators=" + DecoratorCount
                + " services=" + ServiceCount + " depth=" + MaxDepth + ")";
        }
    }
}

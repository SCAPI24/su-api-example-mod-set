using Engine;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PlayerAiMod
{
    /// <summary>
    /// 编译产物：一棵可以交给 <see cref="BtRuntime.SetRoot"/> 的**内存树**。
    ///
    /// 编译期把包里的字符串全部解析成对象引用（类型、子节点、装饰器、服务、黑板键、动作包路径），
    /// 运行期只沿着对象引用走：**不解压、不查表、不解析字符串**（计划 §4.2）。
    /// 实现说明：这里编译成"对象图"而不是下标数组 —— 对象引用之间的跳转与数组下标等价，
    /// 少一层间接层反而更快，也避免了"数组与节点表不同步"这类错误。
    /// </summary>
    public sealed class CompiledTree
    {
        public BtNode Root;

        public string PackageId;

        public string EntryId;

        public string SourcePath;

        public string SourceHash;

        public int NodeCount;

        public int DecoratorCount;

        public int ServiceCount;

        public double CompileMilliseconds;

        public readonly PackageReport Report = new PackageReport();

        public bool HasErrors
        {
            get { return Report.HasErrors; }
        }

        public string Describe()
        {
            return "tree " + (PackageId ?? "?") + "#" + (EntryId ?? "entry")
                + " nodes=" + NodeCount + " decorators=" + DecoratorCount
                + " services=" + ServiceCount
                + " compile=" + CompileMilliseconds.ToString("0.0") + "ms"
                + (HasErrors ? " ERRORS=" + Report.ErrorCount : string.Empty);
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// 树编译器：<c>tree.json</c>（<see cref="ScbtTree"/>）→ <see cref="BtNode"/> 对象图。
    ///
    /// 三件事：
    ///   1. 按 <see cref="BtNodeRegistry"/> 造节点、读属性（读的同时把"类型不符/越界"记进报告）；
    ///   2. 挂装饰器与服务；
    ///   3. 解析 `Task.Subtree` 的嵌套引用 —— **每个引用点编译一份独立实例**
    ///      （节点带运行态，共享实例会让两处互相改状态）。
    /// </summary>
    public static class TreeCompiler
    {
        /// <summary>嵌套编译深度上限（与加载器的引用深度上限一致）。</summary>
        public const int MaxCompileDepth = 8;

        public static CompiledTree Compile(ScbtPackageSet set, LoadedPackage package,
            string entryId = null, PackageReport report = null)
        {
            var compiled = new CompiledTree();
            if (report != null)
                compiled.Report.AddRange(report);

            if (set == null || package == null)
            {
                compiled.Report.Error(PackageCodes.CompileFailed, "compile", "no package to compile");
                return compiled;
            }

            compiled.PackageId = package.Manifest != null ? package.Manifest.Id : package.FileName;
            compiled.SourcePath = package.Path;
            compiled.SourceHash = package.Hash;

            var stopwatch = Stopwatch.StartNew();
            var stack = new List<string>();
            BtNode root = CompileEntry(package, entryId, compiled, 0, stack);
            stopwatch.Stop();
            compiled.CompileMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            if (root == null)
            {
                compiled.Report.Error(PackageCodes.CompileFailed, package.FileName,
                    "entry could not be compiled (see earlier errors)");
                return compiled;
            }

            compiled.Root = root;
            if (compiled.Report.HasErrors)
                Engine.Log.Warning("[PlayerAi][pkg] compiled with errors: " + compiled.Report.Summary());
            return compiled;
        }

        private static BtNode CompileEntry(LoadedPackage package, string entryId,
            CompiledTree compiled, int depth, List<string> stack)
        {
            if (package.Tree == null)
                return null;

            string wanted = entryId;
            if (string.IsNullOrEmpty(wanted) && package.Manifest != null)
                wanted = package.Manifest.Entry;

            ScbtNodeDoc entry = package.Tree.ResolveEntry(wanted);
            if (entry == null)
            {
                compiled.Report.Error(PackageCodes.TreeEntryNotFound, package.FileName,
                    "entry node '" + wanted + "' not found");
                return null;
            }

            stack.Add(package.Path);
            BtNode node = CompileNode(entry, package, compiled, depth, stack);
            stack.RemoveAt(stack.Count - 1);
            return node;
        }

        private static BtNode CompileNode(ScbtNodeDoc doc, LoadedPackage package,
            CompiledTree compiled, int depth, List<string> stack)
        {
            if (!BtNodeRegistry.TryResolveNodeTypeId(doc.Type, out string canonical)
                || !BtNodeRegistry.TryGetNodeInfo(canonical, out BtNodeInfo info))
            {
                return null; // 校验器已经报过 type.unknown，这里不重复刷屏
            }

            BtNode node;
            if (!BtNodeRegistry.TryCreateNode(canonical, out node) || node == null)
            {
                compiled.Report.Error(PackageCodes.CompileFailed, doc.Where,
                    "failed to create node of type '" + canonical + "'");
                return null;
            }

            node.Id = doc.Id;
            node.Name = doc.Name;
            node.SourcePackageId = package.PackageId ?? package.FileName;
            compiled.NodeCount++;

            var reader = new PackageReader(doc.Properties, doc.Where + ".properties", compiled.Report);
            ApplyProperties(node, reader, doc.Where);
            reader.ReportUnknown();

            for (int i = 0; i < doc.Decorators.Count; i++)
            {
                BtDecorator decorator = CompileDecorator(doc.Decorators[i], compiled);
                if (decorator != null)
                    node.AddDecorator(decorator);
            }

            BtCompositeNode composite = node as BtCompositeNode;
            if (composite != null)
            {
                for (int i = 0; i < doc.Services.Count; i++)
                {
                    BtService service = CompileService(doc.Services[i], compiled);
                    if (service != null)
                        composite.AddService(service);
                }
            }

            for (int i = 0; i < doc.Children.Count; i++)
            {
                BtNode child = CompileNode(doc.Children[i], package, compiled, depth, stack);
                if (child == null)
                    continue;
                if (composite == null)
                {
                    // 形状错误已由校验器报出；这里不能再挂（否则运行态出现"孤儿子树"）
                    continue;
                }
                composite.AddChild(child);
            }

            BtSubtreeTask subtree = node as BtSubtreeTask;
            if (subtree != null)
                ResolveSubtree(subtree, doc, package, compiled, depth, stack);

            return node;
        }

        private static BtDecorator CompileDecorator(ScbtDecoratorDoc doc, CompiledTree compiled)
        {
            if (!BtNodeRegistry.TryResolveDecoratorTypeId(doc.Type, out string canonical)
                || !BtNodeRegistry.TryCreateDecorator(canonical, out BtDecorator decorator)
                || decorator == null)
            {
                return null;
            }

            decorator.Id = doc.Id;
            decorator.Abort = doc.Abort;
            decorator.Inverse = doc.Inverse;
            compiled.DecoratorCount++;

            var reader = new PackageReader(doc.Properties, doc.Where + ".properties", compiled.Report);
            for (int i = 0; i < ScbtDecoratorDoc.BasePropertyNames.Length; i++)
                reader.Raw(ScbtDecoratorDoc.BasePropertyNames[i]); // base 字段在文档层已读，别当未知属性

            ApplyDecoratorProperties(decorator, reader, doc.Where);
            reader.ReportUnknown();
            return decorator;
        }

        private static BtService CompileService(ScbtServiceDoc doc, CompiledTree compiled)
        {
            if (!BtNodeRegistry.TryResolveServiceTypeId(doc.Type, out string canonical)
                || !BtNodeRegistry.TryCreateService(canonical, out BtService service)
                || service == null)
            {
                return null;
            }

            service.Id = doc.Id;
            service.Interval = doc.Interval;
            service.RandomDeviation = doc.RandomDeviation;
            service.TickOnActivation = doc.TickOnActivation;
            compiled.ServiceCount++;

            var reader = new PackageReader(doc.Properties, doc.Where + ".properties", compiled.Report);
            ApplyServiceProperties(service, reader, doc.Where);
            reader.ReportUnknown();
            return service;
        }

        /// <summary>
        /// 嵌套引用：`properties.package` 写引用 id（可带 `#节点id`）。
        /// 引用解析的**归属包是当前正在编译的那个包**（嵌套包的引用是它自己的 references，
        /// 不是根包的），引用的目标在加载阶段已经解析好（<see cref="LoadedPackage.FindReference"/>）。
        /// </summary>
        private static void ResolveSubtree(BtSubtreeTask task, ScbtNodeDoc doc, LoadedPackage owner,
            CompiledTree compiled, int depth, List<string> stack)
        {
            string package = task.PackageId;
            if (string.IsNullOrEmpty(package))
                return; // 校验器已报 subtree.reference

            string entryId = task.EntryId;
            int separator = package.IndexOf('#');
            if (separator >= 0)
            {
                if (separator + 1 < package.Length)
                    entryId = package.Substring(separator + 1);
                package = package.Substring(0, separator);
            }

            LoadedPackage referenced = owner != null ? owner.FindReference(package) : null;
            if (referenced == null)
            {
                compiled.Report.Error(PackageCodes.ReferenceUnresolved, doc.Where,
                    "subtree reference '" + package + "' did not resolve to a loaded package");
                return;
            }

            if (depth + 1 > MaxCompileDepth)
            {
                compiled.Report.Error(PackageCodes.ReferenceDepth, doc.Where,
                    "subtree nesting deeper than " + MaxCompileDepth + " levels");
                return;
            }

            for (int i = 0; i < stack.Count; i++)
            {
                if (string.Equals(stack[i], referenced.Path, PackageRoots.PathComparison))
                {
                    compiled.Report.Error(PackageCodes.ReferenceCycle, doc.Where,
                        "subtree reference cycle: " + referenced.FileName + " is already being compiled");
                    return;
                }
            }

            task.Subtree = CompileEntry(referenced, entryId, compiled, depth + 1, stack);
        }

        // ---------------------------------------------------------------- 运行时改写（P0-12）用的入口

        /// <summary>
        /// 把一份属性表套到**已存在的节点**上（内存改写用）。
        ///
        /// 与编译期的差别：这里**未识别的属性名算错误**（人工/AI 现场改参数时，"名字打错"必须当场说清楚，
        /// 而不是像装载包那样只提示）。返回 false 表示有错误，调用方应把该次改写视为失败。
        /// </summary>
        public static bool TryApplyProperties(BtNode node, PackageValue properties,
            PackageReport report, out string detail)
        {
            detail = null;
            if (node == null)
            {
                report.Error(PackageCodes.PropertyValue, "edit", "no node to apply properties to");
                return false;
            }

            BtNodeInfo info;
            if (!BtNodeRegistry.TryGetNodeInfo(node.NodeType, out info))
            {
                report.Error(PackageCodes.TypeUnknown, "edit", "unknown node type '" + node.NodeType + "'");
                return false;
            }

            var reader = new PackageReader(properties, "edit#" + (node.Id ?? "?") + ".properties",
                report, PackageCodes.PropertyUnknown);
            ApplyProperties(node, reader, "edit#" + (node.Id ?? "?"));

            // 改写路径：未知属性一律视为错误（拼错一个名字就不该悄悄生效）
            foreach (string name in properties.MemberNames)
            {
                if (!info.TryGetProperty(name, out BtPropertySpec spec))
                {
                    report.Error(PackageCodes.PropertyUnknown,
                        "edit#" + (node.Id ?? "?") + ".properties." + name,
                        "unknown property '" + name + "' for " + node.NodeType
                        + " (check the spelling; see the node schema)");
                }
            }

            foreach (string name in properties.MemberNames)
            {
                if (!info.TryGetProperty(name, out BtPropertySpec spec))
                    continue;
                if (!reader.Has(name))
                    continue;

                PackageValue value = reader.Owner.Get(name);
                if (!Matches(value, spec.Kind))
                {
                    report.Error(PackageCodes.PropertyKind,
                        "edit#" + (node.Id ?? "?") + ".properties." + name,
                        "expected " + spec.Kind.ToString().ToLowerInvariant() + ", found "
                        + value.DescribeKind() + " (" + value.Preview(40) + ")");
                }
            }

            if (!report.HasErrors)
                detail = node.NodeType + "#" + (node.Id ?? "?");

            return !report.HasErrors;
        }

        private static bool Matches(PackageValue value, BtPropertyKind kind)
        {
            switch (kind)
            {
                case BtPropertyKind.Bool:
                    return value.IsBool;
                case BtPropertyKind.Int:
                case BtPropertyKind.Float:
                    return value.IsNumber;
                case BtPropertyKind.String:
                    return value.IsString;
                case BtPropertyKind.StringList:
                    return value.IsArray || value.IsString;
                case BtPropertyKind.Enum:
                    return value.IsString;
                default:
                    return true;
            }
        }

        /// <summary>
        /// 按 tree.json 的节点文档编译**一个节点**（含装饰器/服务/子节点）。
        /// 内存改写要"插入一个新节点"时走这里，于是新增节点与包里的节点走**同一条**校验与映射路径。
        /// <paramref name="owner"/> 用于解析 `Task.Subtree` 的引用（为空则该类节点插不进来）。
        /// </summary>
        public static BtNode CompileNodeDocument(ScbtNodeDoc doc, LoadedPackage owner,
            PackageReport report)
        {
            if (doc == null)
            {
                report.Error(PackageCodes.TreeNodeNotObject, "edit", "node definition is required");
                return null;
            }

            var compiled = new CompiledTree();
            compiled.Report.AddRange(report);
            var stack = new List<string>();
            BtNode node = CompileNode(doc, owner, compiled, 0, stack);
            report.AddRange(compiled.Report);

            if (node == null && !report.HasErrors)
                report.Error(PackageCodes.CompileFailed, doc.Where ?? "edit", "node could not be compiled");
            return node;
        }

        // ---------------------------------------------------------------- 属性映射
        //
        // 这里是"包格式里的属性名 → 节点类里的字段"的**唯一映射点**。
        // 属性名与注册表里的 BtPropertySpec 一致；越界/负数一类语义检查也在这里做
        // （只有读取点知道每个属性的含义，校验器只做类型与必填）。

        private static void ApplyProperties(BtNode node, PackageReader reader, string where)
        {
            switch (node.NodeType)
            {
                case "Root":
                    // `loop`（默认 true）：跑完整棵树之后要不要从头再来。
                    // UE 的语义是循环执行（BtRuntime.Tick 里就是"完成 → 重开"），
                    // 但"进游戏"这种菜单宏必须能**只做一次** —— 否则它会每隔几秒再点一次 Play。
                    ((BtRootNode)node).Loop = reader.Bool("loop", true);
                    break;

                case "Task.Wait":
                    ((BtWaitTask)node).Seconds = NonNegative(reader, "seconds", 1f, where);
                    break;

                case "Task.SetBlackboard":
                {
                    var task = (BtSetBlackboardTask)node;
                    task.Key = reader.Str("key", null);
                    task.ValueKind = reader.Enum("valueKind", "float", BtSchema.ValueKinds);
                    ReadTypedValue(reader, task.ValueKind, where,
                        out bool boolValue, out int intValue, out float floatValue, out string stringValue);
                    task.BoolValue = boolValue;
                    task.IntValue = intValue;
                    task.FloatValue = floatValue;
                    task.StringValue = stringValue;
                    break;
                }

                case "Task.Log":
                {
                    var task = (BtLogTask)node;
                    task.Message = reader.Str("message", "(log)");
                    task.Succeed = reader.Bool("succeed", true);
                    break;
                }

                case "Task.LookAt":
                {
                    var task = (BtLookAtTargetTask)node;
                    task.TargetKey = reader.Str("targetKey", "target");
                    task.EyeHeight = reader.Float("eyeHeight", 1.35f);
                    task.Seconds = NonNegative(reader, "seconds", 0.25f, where);
                    break;
                }

                case "Task.MoveTo":
                {
                    var task = (BtMoveToTargetTask)node;
                    task.TargetKey = reader.Str("targetKey", "target");
                    task.AcceptableRadius = NonNegative(reader, "acceptableRadius", 3f, where);
                    task.TimeoutSeconds = NonNegative(reader, "timeout", 25f, where);
                    task.ForwardKey = reader.Str("forwardKey", "w");
                    task.EyeHeight = reader.Float("eyeHeight", 1.35f);
                    break;
                }

                case "Task.WaitForTarget":
                {
                    var task = (BtWaitForTargetTask)node;
                    task.TargetKey = reader.Str("targetKey", "target");
                    task.TimeoutSeconds = NonNegative(reader, "timeout", 10f, where);
                    break;
                }

                case "Task.Subtree":
                {
                    var task = (BtSubtreeTask)node;
                    task.PackageId = reader.Str("package", null);
                    task.EntryId = reader.Str("entry", null);
                    break;
                }

                case "Task.PlayActionPackage":
                {
                    var task = (BtPlayActionPackageTask)node;
                    task.Packages.Clear();
                    task.Packages.AddRange(reader.StringList("packages"));
                    task.Mode = reader.Enum("mode", "Sequence", BtSchema.ActionPackageModes);
                    task.Repeat = reader.Int("repeat", 1);
                    if (task.Repeat < 0)
                    {
                        reader.Report.Error(PackageCodes.PropertyValue, where + ".properties.repeat",
                            "repeat must be >= 0, found " + task.Repeat);
                        task.Repeat = 0;
                    }
                    task.AbortOnFail = reader.Bool("abortOnFail", true);
                    break;
                }

                case "Task.UiClick":
                {
                    var task = (BtUiClickTask)node;
                    task.Target = reader.Str("target", null);
                    if (string.IsNullOrEmpty(task.Target))
                    {
                        reader.Report.Error(PackageCodes.PropertyValue, where + ".properties.target",
                            "Task.UiClick needs a target (control name/path, or list:<list>@<text>)");
                    }
                    task.Mode = reader.Enum("mode", "direct", BtSchema.UiClickModes);
                    task.WaitSeconds = NonNegative(reader, "waitSeconds", 3f, where);
                    task.Repeat = reader.Int("repeat", 1);
                    if (task.Repeat < 1)
                    {
                        reader.Report.Error(PackageCodes.PropertyValue, where + ".properties.repeat",
                            "repeat must be >= 1, found " + task.Repeat);
                        task.Repeat = 1;
                    }
                    break;
                }
            }
        }

        private static void ApplyDecoratorProperties(BtDecorator decorator, PackageReader reader,
            string where)
        {
            switch (decorator.NodeType)
            {
                case "Blackboard":
                {
                    var target = (BtBlackboardDecorator)decorator;
                    target.Key = reader.Str("key", null);
                    target.Query = reader.Enum("query", "IsSet", BtSchema.BlackboardQueries);
                    target.Operator = reader.Enum("operator", "==", BtSchema.CompareOperators);
                    target.ValueKind = reader.Enum("valueKind", "bool", BtSchema.ValueKinds);
                    ReadTypedValue(reader, target.ValueKind, where,
                        out bool boolValue, out int intValue, out float floatValue, out string stringValue);
                    target.BoolValue = boolValue;
                    target.IntValue = intValue;
                    target.FloatValue = floatValue;
                    target.StringValue = stringValue;
                    break;
                }

                case "Cooldown":
                    ((BtCooldownDecorator)decorator).CooldownSeconds =
                        NonNegative(reader, "cooldownSeconds", 5f, where);
                    break;

                case "TimeLimit":
                {
                    var target = (BtTimeLimitDecorator)decorator;
                    target.LimitSeconds = reader.Float("limitSeconds", 5f);
                    if (target.LimitSeconds <= 0f)
                    {
                        reader.Report.Error(PackageCodes.PropertyValue, where + ".properties.limitSeconds",
                            "limitSeconds must be > 0");
                        target.LimitSeconds = 5f;
                    }
                    break;
                }

                case "Loop":
                {
                    var target = (BtLoopDecorator)decorator;
                    target.NumLoops = reader.Int("numLoops", 1);
                    target.InfiniteLoop = reader.Bool("infiniteLoop", false);
                    target.InfiniteLoopTimeoutSeconds = reader.Float("infiniteLoopTimeoutSeconds", 10f);
                    if (!target.InfiniteLoop && target.NumLoops < 1)
                    {
                        reader.Report.Error(PackageCodes.PropertyValue, where + ".properties.numLoops",
                            "numLoops must be >= 1 when infiniteLoop is false, found " + target.NumLoops);
                        target.NumLoops = 1;
                    }
                    break;
                }
            }
        }

        private static void ApplyServiceProperties(BtService service, PackageReader reader, string where)
        {
            if (service.NodeType == "Service.UpdateNearestPlayer")
            {
                var target = (BtUpdateNearestPlayerService)service;
                target.TargetKey = reader.Str("targetKey", "target");
                target.NameFilter = reader.Str("nameFilter", null);
                target.ClearWhenMissing = reader.Bool("clearWhenMissing", false);
            }
        }

        private static float NonNegative(PackageReader reader, string name, float fallback, string where)
        {
            float value = reader.Float(name, fallback);
            if (value < 0f)
            {
                reader.Report.Error(PackageCodes.PropertyValue, where + ".properties." + name,
                    name + " must be >= 0, found " + PackageValue.FormatNumber(value));
                return fallback;
            }
            return value;
        }

        /// <summary>按 valueKind 读出 SetBlackboard / Blackboard 装饰器的比较值。</summary>
        private static void ReadTypedValue(PackageReader reader, string valueKind, string where,
            out bool boolValue, out int intValue, out float floatValue, out string stringValue)
        {
            boolValue = false;
            intValue = 0;
            floatValue = 0f;
            stringValue = null;

            PackageValue value = reader.Raw("value");
            if (value.IsNull)
                return;

            switch ((valueKind ?? "float").Trim().ToLowerInvariant())
            {
                case "bool":
                    if (value.IsBool)
                        boolValue = value.AsBool();
                    else
                        reader.Report.Error(PackageCodes.PropertyKind, where + ".properties.value",
                            "expected bool for valueKind 'bool', found " + value.DescribeKind());
                    break;
                case "int":
                    if (value.IsNumber)
                        intValue = value.AsInt();
                    else
                        reader.Report.Error(PackageCodes.PropertyKind, where + ".properties.value",
                            "expected number for valueKind 'int', found " + value.DescribeKind());
                    break;
                case "string":
                    if (value.IsString)
                        stringValue = value.AsString();
                    else
                        reader.Report.Error(PackageCodes.PropertyKind, where + ".properties.value",
                            "expected string for valueKind 'string', found " + value.DescribeKind());
                    break;
                default:
                    if (value.IsNumber)
                        floatValue = value.AsFloat();
                    else
                        reader.Report.Error(PackageCodes.PropertyKind, where + ".properties.value",
                            "expected number for valueKind 'float', found " + value.DescribeKind());
                    break;
            }
        }
    }
}

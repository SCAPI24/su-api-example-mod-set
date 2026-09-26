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
        ///
        /// `banks` 非空时额外做**问题库引用校验**（plan G4）；为 null 时对"引用了问题库的节点"
        /// 报 `bank.unverified` 警告 —— 见 <see cref="QuestionBankReferenceRules.Check"/>。
        /// </summary>
        public static void ValidateTree(ScbtTree tree, ScbtManifest manifest, string where,
            PackageReport report, IQuestionBankSource banks = null)
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
            // 收集"引用了问题库"的事实（LayaAsk 的绑定 + 拿答案做分支的比较），
            // 等整棵树走完再一次性核对 —— 因为选项 key 闭集校验需要先知道"黑板键对应哪个问题"。
            var refs = new ReferenceCollector();

            foreach (ScbtNodeDoc node in tree.Walk())
            {
                ValidateNode(node, manifest, nodeIds, helperIds, report, refs);
            }

            QuestionBankReferenceRules.Check(refs.Asks, refs.Compares, banks, where, report);

            // 入口：manifest.entry 必须能在文档里找到
            if (manifest != null && !string.IsNullOrEmpty(manifest.Entry)
                && tree.Find(manifest.Entry) == null)
            {
                report.Error(PackageCodes.TreeEntryNotFound, where,
                    "manifest.entry '" + manifest.Entry + "' does not exist in " + ScbtTree.FileName);
            }

            // 黑板键：schema 标成 BlackboardKey 的属性，值应当在 manifest.blackboard 里声明过（计划 §3.4）
            ValidateDeclaredBlackboardKeys(tree, manifest, report);
        }

        /// <summary>
        /// 黑板键引用检查：注册表里标成 <see cref="BtPropertyKind.BlackboardKey"/> 的属性，
        /// 其值应当在 `manifest.blackboard` 里声明过。
        ///
        /// 为什么是**警告**而不是错误：键完全可能来自**被引用的父包**（子树用的是根包的黑板 ——
        /// 例如 `common.scbtpak` 的 `target` 是由 `demo.greet` 的服务写进去的），
        /// 也可能是有意"先写后读"的键。硬判错会把本来能跑的包拦下来，那就成了校验器挡路。
        /// 但警告必须给：键名打错（`targt`）在运行期表现为"条件永远不成立"，极难查。
        /// </summary>
        private static void ValidateDeclaredBlackboardKeys(ScbtTree tree, ScbtManifest manifest,
            PackageReport report)
        {
            if (tree == null || manifest == null)
                return;

            var declared = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < manifest.Blackboard.Count; i++)
                declared.Add(manifest.Blackboard[i].Name);

            foreach (ScbtNodeDoc node in tree.Walk())
            {
                CheckBlackboardKeys(node.Properties, NodeSpecs(node.Type), node.Where,
                    declared, report);
                for (int i = 0; i < node.Decorators.Count; i++)
                {
                    ScbtDecoratorDoc decorator = node.Decorators[i];
                    CheckBlackboardKeys(decorator.Properties, DecoratorSpecs(decorator.Type),
                        decorator.Where, declared, report);
                }
                for (int i = 0; i < node.Services.Count; i++)
                {
                    ScbtServiceDoc service = node.Services[i];
                    CheckBlackboardKeys(service.Properties, ServiceSpecs(service.Type),
                        service.Where, declared, report);
                }
            }
        }

        private static void CheckBlackboardKeys(PackageValue properties,
            IReadOnlyList<BtPropertySpec> specs, string where, HashSet<string> declared,
            PackageReport report)
        {
            if (properties == null || !properties.IsObject || specs == null)
                return;

            for (int i = 0; i < specs.Count; i++)
            {
                BtPropertySpec spec = specs[i];
                if (spec.Kind != BtPropertyKind.BlackboardKey || !properties.Has(spec.Name))
                    continue;
                PackageValue value = properties.Get(spec.Name);
                if (!value.IsString)
                    continue;                     // 类型错已经在 ValidateProperties 里报过了
                string key = value.AsString(null);
                if (string.IsNullOrEmpty(key) || declared.Contains(key))
                    continue;
                report.Warn(PackageCodes.PropertyUnknown, where + ".properties." + spec.Name,
                    "blackboard key '" + key + "' is not declared in manifest.blackboard"
                    + " (ok if the root package declares it; otherwise the key is never set)");
            }
        }

        private static IReadOnlyList<BtPropertySpec> NodeSpecs(string type)
        {
            string canonical;
            BtNodeInfo info;
            return !string.IsNullOrEmpty(type)
                && BtNodeRegistry.TryResolveNodeTypeId(type, out canonical)
                && BtNodeRegistry.TryGetNodeInfo(canonical, out info) ? info.Properties : null;
        }

        private static IReadOnlyList<BtPropertySpec> DecoratorSpecs(string type)
        {
            string canonical;
            BtDecoratorInfo info;
            return !string.IsNullOrEmpty(type)
                && BtNodeRegistry.TryResolveDecoratorTypeId(type, out canonical)
                && BtNodeRegistry.TryGetDecoratorInfo(canonical, out info) ? info.Properties : null;
        }

        private static IReadOnlyList<BtPropertySpec> ServiceSpecs(string type)
        {
            string canonical;
            BtServiceInfo info;
            return !string.IsNullOrEmpty(type)
                && BtNodeRegistry.TryResolveServiceTypeId(type, out canonical)
                && BtNodeRegistry.TryGetServiceInfo(canonical, out info) ? info.Properties : null;
        }

        private static void ValidateNode(ScbtNodeDoc node, ScbtManifest manifest,
            HashSet<string> nodeIds, HashSet<string> helperIds, PackageReport report,
            ReferenceCollector refs)
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
                ValidateDecorator(node.Decorators[i], helperIds, report, refs);
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

            // ---- 动作脚本 / Laya 节点的**语义校验**（P1/P3）
            //
            // 为什么放在这里而不是等编译器：`answerKeys` 写错类型、`script` 与 `scriptKey` 都没写
            // 这类问题**编译期才会报**，而编辑器保存前只跑校验器 —— 于是"编辑器说没问题、
            // 进游戏装载失败"。实机与编辑器都实测过这两条漏网（plan A13）。
            if (typeKnown && string.Equals(info.TypeId, "Task.RunActionScript", StringComparison.Ordinal))
            {
                string script = node.Properties.Get("script").AsString(null);
                string scriptKey = node.Properties.Get("scriptKey").AsString(null);
                if (string.IsNullOrEmpty(script) && string.IsNullOrEmpty(scriptKey))
                {
                    report.Error(PackageCodes.PropertyRequired, where + ".properties.script",
                        "Task.RunActionScript needs 'script' (a .aeact name) or 'scriptKey' (a blackboard string key)");
                }
            }

            if (typeKnown && string.Equals(info.TypeId, "Task.LayaAsk", StringComparison.Ordinal))
            {
                string questions = node.Properties.Get("questions").AsString(null);
                string questionsKey = node.Properties.Get("questionsKey").AsString(null);
                if (string.IsNullOrEmpty(questions) && string.IsNullOrEmpty(questionsKey))
                {
                    report.Error(PackageCodes.PropertyRequired, where + ".properties.questions",
                        "Task.LayaAsk needs 'questions' (a .qbank name) or 'questionsKey' (a blackboard string key)");
                }

                string answerKeys = node.Properties.Get("answerKeys").AsString(null);
                if (string.IsNullOrEmpty(answerKeys))
                {
                    report.Error(PackageCodes.PropertyRequired, where + ".properties.answerKeys",
                        "Task.LayaAsk needs 'answerKeys' (blackboardKey:questionId:type, comma separated)");
                }
                else
                {
                    List<string> bindingIssues = ValidateAnswerKeys(answerKeys, where + ".properties.answerKeys");
                    for (int i = 0; i < bindingIssues.Count; i++)
                        report.Error(PackageCodes.PropertyValue, where + ".properties.answerKeys", bindingIssues[i]);
                }

                // 收集引用事实（G4）。形状错了也照收：`ParseBindings` 会丢掉坏项，
                // 库引用检查因此只看得到"能解析的那些"，不会因为一条坏绑定就整体静默。
                if (refs != null)
                {
                    var ask = new LayaAskReference
                    {
                        Where = where,
                        BankName = string.IsNullOrEmpty(questions) ? null : questions,
                        QuestionsKey = string.IsNullOrEmpty(questionsKey) ? null : questionsKey
                    };
                    string only = node.Properties.Get("only").AsString(null);
                    if (!string.IsNullOrEmpty(only))
                    {
                        string[] ids = only.Split(',');
                        for (int i = 0; i < ids.Length; i++)
                        {
                            string id = ids[i].Trim();
                            if (id.Length > 0)
                                ask.Only.Add(id);
                        }
                    }

                    string parseError;
                    List<BtLayaAskTask.AnswerBinding> bindings = BtLayaAskTask.ParseBindings(answerKeys, out parseError);
                    if (bindings != null)
                    {
                        for (int i = 0; i < bindings.Count; i++)
                        {
                            ask.Bindings.Add(new LayaAnswerBindingReference
                            {
                                BlackboardKey = bindings[i].BlackboardKey,
                                QuestionId = bindings[i].QuestionId,
                                Kind = bindings[i].Kind
                            });
                        }
                    }
                    refs.Asks.Add(ask);
                }
            }
        }

        /// <summary>
        /// 校验过程中顺手收集的"问题库引用"事实。做成一个可变对象往后传，
        /// 而不是给每一层都加一串 out 参数 —— 校验器本来就已经在逐节点递归了。
        /// </summary>
        private sealed class ReferenceCollector
        {
            public readonly List<LayaAskReference> Asks = new List<LayaAskReference>();
            public readonly List<AnswerCompareReference> Compares = new List<AnswerCompareReference>();
        }

        /// <summary>
        /// `answerKeys` 的形状校验（`黑板键:问题id:类型`）。
        /// 类型只认 `str|bool|int|float` —— 与 <c>BtLayaAskTask</c> 的折算规则同源：
        /// 写错类型在运行时是"节点失败"，在这里应当是**装载期就报错**。
        /// </summary>
        private static List<string> ValidateAnswerKeys(string spec, string where)
        {
            var issues = new List<string>();
            string[] parts = spec.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                    continue;

                string[] fields = part.Split(':');
                if (fields.Length < 3)
                {
                    issues.Add("'" + part + "' must be written as blackboardKey:questionId:type");
                    continue;
                }

                string kind = fields[2].Trim().ToLowerInvariant();
                if (kind != "str" && kind != "string" && kind != "bool" && kind != "int" && kind != "float")
                {
                    issues.Add("'" + part + "' has unknown type '" + fields[2]
                        + "' (str / bool / int / float)");
                }
            }
            return issues;
        }

        private static void ValidateDecorator(ScbtDecoratorDoc doc, HashSet<string> helperIds,
            PackageReport report, ReferenceCollector refs)
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

            // 拿答案做分支的写法（出厂树）：`Blackboard` 装饰器 + query=Compare + valueKind=string。
            // 收集起来交给 G4 校验：字面量必须是那个问题的**选项 key**，
            // 否则"库里把选项改个名"就会让这条分支永远不成立，而树看上去完好无损。
            if (refs != null && string.Equals(info.TypeId, "Blackboard", StringComparison.Ordinal))
            {
                string query = doc.Properties.Get("query").AsString(null);
                string kind = doc.Properties.Get("valueKind").AsString(null);
                string op = doc.Properties.Get("operator").AsString(null);
                if (string.Equals(query, "Compare", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(kind, "string", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(op, "==", StringComparison.Ordinal) || string.Equals(op, "!=", StringComparison.Ordinal)))
                {
                    refs.Compares.Add(new AnswerCompareReference
                    {
                        Where = where,
                        BlackboardKey = doc.Properties.Get("key").AsString(null),
                        Operator = op,
                        Value = doc.Properties.Get("value").AsString(null)
                    });
                }
            }
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
                    case BtPropertyKind.BlackboardKey:
                        // 黑板键本质就是字符串，类型检查一样；"有没有声明"的检查在下面单独做
                        // （需要 manifest.blackboard，这里拿不到）
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

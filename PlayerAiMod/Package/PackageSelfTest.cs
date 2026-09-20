using Engine;
using SuAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 包格式与加载器自检（P0-3 / P0-4）：**纯逻辑、不落盘、不依赖游戏**。
    ///
    /// 覆盖：manifest/tree 解析、结构校验的每一条错误路径、包目录白名单与路径穿越、
    /// 嵌套引用（含 `#节点id`）、环检测、深度/数量上限、编译结果（类型/属性/装饰器/服务/
    /// 每处引用独立实例）、以及"编译出来的树真的能跑"（接 <see cref="BtRuntime"/> 跑几帧看黑板）。
    ///
    /// 用法：<c>PackageSelfTest.Run()</c>；将来由控制面命令 `bt.selftest` 一并调用（P0-7）。
    /// </summary>
    public static class PackageSelfTest
    {
        private const string InstanceDir = "C:/pai-selftest";

        private const float Dt = 1f / 60f;

        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "PackageSelfTest" };
            try
            {
                ValidPackage(result);
                CompileProperties(result);
                NestedReferences(result);
                RuntimeSmoke(result);
                NestedRuntimeSmoke(result);
                ValidationErrors(result);
                ReferenceSafety(result);
                RootWhitelist(result);
                RoundTrip(result);
                LoadAndCompileGate(result);
                TemplatesAndReload(result);
                LibrarySwitching(result);
            }
            catch (Exception exception)
            {
                result.Check("PackageSelfTest no unexpected exception", false,
                    exception.GetType().Name + ": " + exception.Message);
            }
            return result;
        }

        // ---------------------------------------------------------------- 用例

        private static void ValidPackage(BtSelfTest.TestResult result)
        {
            MemoryPackageSource source = DemoSource();
            ScbtPackageSet set = Load(source, "demo.greet");

            result.Check("valid package loads without errors", !set.HasErrors,
                set.ErrorCount + " error(s): " + FirstIssue(set));
            result.Check("valid package loads both packages", set.Packages.Count == 2,
                "count=" + set.Packages.Count);
            result.Check("root package is the demo package",
                set.Root != null && set.Root.FileName == "demo.greet.scbtpak",
                set.Root != null ? set.Root.FileName : "<null>");
            result.Check("manifest id and references parsed",
                set.Root != null && set.Root.Manifest != null && set.Root.Manifest.Id == "demo.greet"
                && set.Root.Manifest.References.Count == 1,
                set.Root != null && set.Root.Manifest != null
                    ? set.Root.Manifest.ToString() : "<null>");
            result.Check("reference resolved to the common package",
                set.Root != null && set.Root.ReferenceTargets.Count == 1
                && set.Root.ReferenceTargets[0] != null
                && set.Root.ReferenceTargets[0].PackageId == "common",
                set.Root != null && set.Root.ReferenceTargets.Count > 0
                    && set.Root.ReferenceTargets[0] != null
                    ? set.Root.ReferenceTargets[0].PackageId : "<unresolved>");
            result.Check("blackboard declaration parsed",
                set.Root != null && set.Root.Manifest.Blackboard.Count == 2
                && set.Root.Manifest.Blackboard[0].Name == "target"
                && set.Root.Manifest.Blackboard[0].Type == "actor"
                && set.Root.Manifest.Blackboard[1].Readonly,
                set.Root != null && set.Root.Manifest != null
                    ? set.Root.Manifest.Blackboard.Count.ToString() : "?");
            result.Check("tree document node count",
                set.Root != null && set.Root.Tree != null && set.Root.Tree.NodeCount == 9,
                set.Root != null && set.Root.Tree != null ? set.Root.Tree.NodeCount.ToString() : "?");
            result.Check("package hash is a sha256 hex string",
                set.Root != null && set.Root.Hash != null && set.Root.Hash.Length == 64,
                set.Root != null ? set.Root.Hash : "<null>");
        }

        private static void CompileProperties(BtSelfTest.TestResult result)
        {
            MemoryPackageSource source = DemoSource();
            ScbtPackageSet set = Load(source, "demo.greet");
            CompiledTree compiled = TreeCompiler.Compile(set, set.Root);

            result.Check("compiled tree has no errors", !compiled.HasErrors, FirstIssue(compiled.Report));
            result.Check("compiled root is a Root node",
                compiled.Root != null && compiled.Root.NodeType == "Root",
                compiled.Root != null ? compiled.Root.NodeType : "<null>");
            result.Check("compiled node count includes nested packages",
                compiled.NodeCount == 16, "nodes=" + compiled.NodeCount);
            result.Check("compiled decorator count", compiled.DecoratorCount == 1,
                "decorators=" + compiled.DecoratorCount);
            result.Check("compiled service count", compiled.ServiceCount == 1,
                "services=" + compiled.ServiceCount);

            var moveTo = compiled.Root.Find("t2") as BtMoveToTargetTask;
            result.Check("Task.MoveTo properties applied",
                moveTo != null && moveTo.TargetKey == "target"
                && Math.Abs(moveTo.AcceptableRadius - 3f) < 1e-4f
                && Math.Abs(moveTo.TimeoutSeconds - 25f) < 1e-4f,
                moveTo == null ? "<missing>" : moveTo.ToString());

            var lookAt = compiled.Root.Find("t1") as BtLookAtTargetTask;
            result.Check("Task.LookAt properties applied",
                lookAt != null && Math.Abs(lookAt.EyeHeight - 1.35f) < 1e-4f,
                lookAt == null ? "<missing>" : lookAt.ToString());

            var action = compiled.Root.Find("t3") as BtPlayActionPackageTask;
            result.Check("Task.PlayActionPackage properties applied",
                action != null && action.Packages.Count == 2
                && action.Packages[0] == "greet_wave.scatpak" && action.Mode == "Sequence"
                && action.AbortOnFail,
                action == null ? "<missing>" : action.ToString());

            BtNode selector = compiled.Root.Find("sel");
            result.Check("service attached to the selector",
                selector != null && selector is BtCompositeNode
                && ((BtCompositeNode)selector).Services.Count == 1,
                selector == null ? "<missing>" : selector.ToString());

            if (selector is BtCompositeNode composite && composite.Services.Count == 1)
            {
                var service = composite.Services[0] as BtUpdateNearestPlayerService;
                result.Check("service properties applied (name resolves without prefix)",
                    service != null && service.TargetKey == "target" && service.NameFilter == "basil"
                    && Math.Abs(service.Interval - 0.25f) < 1e-4f,
                    service == null ? "<wrong type>" : service.ToString());
            }

            BtNode sequence = compiled.Root.Find("seq");
            result.Check("decorator attached with observer abort mode",
                sequence != null && sequence.Decorators.Count == 1
                && sequence.Decorators[0].NodeType == "Blackboard"
                && sequence.Decorators[0].Abort == BtAbortMode.LowerPriority,
                sequence == null || sequence.Decorators.Count == 0
                    ? "<missing>" : sequence.Decorators[0].Describe());

            if (sequence != null && sequence.Decorators.Count == 1)
            {
                var decorator = sequence.Decorators[0] as BtBlackboardDecorator;
                result.Check("decorator properties applied",
                    decorator != null && decorator.Key == "target" && decorator.Query == "IsSet"
                    && !decorator.Inverse,
                    decorator == null ? "<wrong type>" : decorator.Describe());
            }

            var wait = compiled.Root.Find("t4") as BtWaitTask;
            result.Check("Task.Wait properties applied",
                wait != null && Math.Abs(wait.Seconds - 1f) < 1e-4f,
                wait == null ? "<missing>" : wait.ToString());
        }

        private static void NestedReferences(BtSelfTest.TestResult result)
        {
            MemoryPackageSource source = DemoSource();
            ScbtPackageSet set = Load(source, "demo.greet");
            CompiledTree compiled = TreeCompiler.Compile(set, set.Root);

            var byReference = compiled.Root.Find("t5") as BtSubtreeTask;
            var byNodeId = compiled.Root.Find("t6") as BtSubtreeTask;

            result.Check("subtree by reference id resolved",
                byReference != null && byReference.Subtree != null,
                byReference == null ? "<missing>" : byReference.ToString());
            result.Check("subtree by '#nodeId' resolved to that node",
                byNodeId != null && byNodeId.Subtree != null && byNodeId.Subtree.Id == "sub",
                byNodeId == null || byNodeId.Subtree == null
                    ? "<unresolved>" : byNodeId.Subtree.ToString());
            result.Check("each subtree reference compiles its own instance",
                byReference != null && byNodeId != null && byReference.Subtree != null
                && byNodeId.Subtree != null && !ReferenceEquals(byReference.Subtree, byNodeId.Subtree),
                "same instance would leak runtime state between branches");
            result.Check("nested subtree keeps its own decorators/services",
                byReference != null && byReference.Subtree != null
                && byReference.Subtree.Find("c1") != null,
                "nested node c1 not found");
        }

        private static void RuntimeSmoke(BtSelfTest.TestResult result)
        {
            var source = new MemoryPackageSource();
            source.Add(P("run.pkg.scbtpak"), Zip(
                ScbtManifest.FileName, ManifestJson("run.pkg", "root"),
                ScbtTree.FileName, @"{
  ""id"": ""root"", ""type"": ""Root"", ""children"": [{
    ""id"": ""seq"", ""type"": ""Sequence"", ""children"": [
      { ""id"": ""w1"", ""type"": ""Task.SetBlackboard"",
        ""properties"": { ""key"": ""ran"", ""valueKind"": ""bool"", ""value"": true } },
      { ""id"": ""w2"", ""type"": ""Task.Wait"", ""properties"": { ""seconds"": 0.02 } }
    ] }]
}"));

            ScbtPackageSet set = Load(source, "run.pkg");
            CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
            result.Check("minimal runtime package compiles", !compiled.HasErrors && compiled.Root != null,
                FirstIssue(compiled.Report));

            var blackboard = new AiBlackboard();
            var runtime = new BtRuntime(blackboard, new BtTestSensor(), new BtTestActuator());
            runtime.SetRoot(compiled.Root, compiled.PackageId, compiled.SourcePath, compiled.SourceHash);
            runtime.Start();

            bool ran = false;
            for (int i = 0; i < 240 && !ran; i++)
            {
                runtime.Tick(Dt);
                ran = blackboard.TryGet(new AiBlackboardKey<bool>("ran"), out bool value) && value;
            }

            result.Check("compiled tree runs and writes the blackboard", ran,
                "ticks=" + runtime.TickCount + " last=" + runtime.LastResult);
            result.Check("compiled tree has no runtime error", runtime.LastError == null,
                runtime.LastError);
        }

        private static void NestedRuntimeSmoke(BtSelfTest.TestResult result)
        {
            MemoryPackageSource source = DemoSource();
            source.Add(P("nest.root.scbtpak"), Zip(
                ScbtManifest.FileName,
                ManifestJson("nest.root", "root", @"[{ ""id"": ""common"", ""path"": ""common.scbtpak"" }]"),
                ScbtTree.FileName, @"{
  ""id"": ""root"", ""type"": ""Root"", ""children"": [
    { ""id"": ""sub"", ""type"": ""Task.Subtree"", ""properties"": { ""package"": ""common"" } }
  ]
}"));

            ScbtPackageSet set = Load(source, "nest.root");
            CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
            result.Check("nested runtime package compiles", !compiled.HasErrors && compiled.Root != null,
                FirstIssue(compiled.Report));

            var blackboard = new AiBlackboard();
            var runtime = new BtRuntime(blackboard, new BtTestSensor(), new BtTestActuator());
            runtime.SetRoot(compiled.Root, compiled.PackageId, compiled.SourcePath, compiled.SourceHash);
            runtime.Start();

            bool nestedRan = false;
            for (int i = 0; i < 120 && !nestedRan; i++)
            {
                runtime.Tick(Dt);
                nestedRan = blackboard.TryGet(new AiBlackboardKey<bool>("nested"), out bool value) && value;
            }

            result.Check("nested subtree executes at runtime", nestedRan,
                "ticks=" + runtime.TickCount + " last=" + runtime.LastResult);
        }

        private static void ValidationErrors(BtSelfTest.TestResult result)
        {
            // 未知节点类型
            CheckCase(result, "unknown node type is an error",
                PackageCodes.TypeUnknown,
                ManifestJson("bad.type", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.Nope"" }] }");

            // 代码专用节点
            CheckCase(result, "code-only node type is rejected",
                PackageCodes.TypeNotSerializable,
                ManifestJson("bad.lambda", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.Lambda"" }] }");

            // 重复节点 id
            CheckCase(result, "duplicate node id is an error",
                PackageCodes.TreeIdDuplicate,
                ManifestJson("bad.dup", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""same"", ""type"": ""Task.Wait"" },
                     { ""id"": ""same"", ""type"": ""Task.Wait"" }] }");

            // 任务带 children
            CheckCase(result, "task with children is an error",
                PackageCodes.ShapeChildren,
                ManifestJson("bad.taskchild", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.Wait"", ""children"": [
                        { ""id"": ""y"", ""type"": ""Task.Wait"" }] }] }");

            // 组合没有 children
            CheckCase(result, "composite without children is an error",
                PackageCodes.ShapeChildren,
                ManifestJson("bad.emptyseq", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Sequence"" }] }");

            // 服务挂在任务上
            CheckCase(result, "service on a task is an error",
                PackageCodes.ShapeServices,
                ManifestJson("bad.svc", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.Wait"",
                       ""services"": [{ ""id"": ""s"", ""type"": ""UpdateNearestPlayer"" }] }] }");

            // 必填属性缺失
            CheckCase(result, "missing required property is an error",
                PackageCodes.PropertyRequired,
                ManifestJson("bad.req", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.SetBlackboard"",
                       ""properties"": { ""value"": 1 } }] }");

            // 属性类型不符
            CheckCase(result, "property type mismatch is an error",
                PackageCodes.PropertyKind,
                ManifestJson("bad.kind", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.Wait"",
                       ""properties"": { ""seconds"": ""soon"" } }] }");

            // 枚举取值不符
            CheckCase(result, "bad enum value is an error",
                PackageCodes.PropertyEnum,
                ManifestJson("bad.enum", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.PlayActionPackage"",
                       ""properties"": { ""mode"": ""Chaos"" } }] }");

            // 未识别的属性只是提示
            CheckCase(result, "unknown property is a warning",
                PackageCodes.PropertyUnknown,
                ManifestJson("warn.prop", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.Wait"",
                       ""properties"": { ""second"": 3 } }] }",
                false);

            // Subtree 引用未声明
            CheckCase(result, "subtree reference not declared is an error",
                PackageCodes.SubtreeReference,
                ManifestJson("bad.subref", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.Subtree"",
                       ""properties"": { ""package"": ""ghost"" } }] }");

            // 格式版本不支持
            CheckCase(result, "unsupported format version is an error",
                PackageCodes.ManifestVersion,
                @"{ ""format"": ""scbt"", ""version"": 99, ""id"": ""bad.version"", ""entry"": ""root"" }",
                SimpleTree());

            // 非法包 id
            CheckCase(result, "invalid manifest id is an error",
                PackageCodes.ManifestId,
                @"{ ""format"": ""scbt"", ""version"": 1, ""id"": ""bad id"", ""entry"": ""root"" }",
                SimpleTree());

            // 入口不存在
            CheckCase(result, "missing entry node is an error",
                PackageCodes.TreeEntryNotFound,
                ManifestJson("bad.entry", "nope"),
                SimpleTree());

            // 表达式过深
            CheckCase(result, "too deep tree is an error",
                PackageCodes.TreeLimits,
                ManifestJson("bad.deep", "root"),
                DeepTreeJson(PackageValidator.MaxDepth + 2));

            // 根节点多个子节点（提示，不拦）
            CheckCase(result, "root with several children is only a warning",
                PackageCodes.ShapeRootChildren,
                ManifestJson("warn.root", "root"),
                @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""a"", ""type"": ""Task.Wait"" },
                     { ""id"": ""b"", ""type"": ""Task.Wait"" }] }",
                false);

            // 负值参数（编译期判定）
            {
                var source = new MemoryPackageSource();
                source.Add(P("bad.value.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("bad.value", "root"),
                    ScbtTree.FileName, @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                         { ""id"": ""x"", ""type"": ""Task.MoveTo"",
                           ""properties"": { ""acceptableRadius"": -1 } }] }"));
                ScbtPackageSet set = Load(source, "bad.value");
                CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
                result.Check("negative radius fails at compile time",
                    compiled.Report.HasCode(PackageCodes.PropertyValue), FirstIssue(compiled.Report));
            }

            // 缺少 tree.json
            {
                var source = new MemoryPackageSource();
                source.Add(P("no.tree.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("no.tree", "root")));
                ScbtPackageSet set = Load(source, "no.tree");
                result.Check("package without tree.json is an error",
                    set.ErrorCount > 0 && ReportOf(set).HasCode(PackageCodes.ZipEntryMissing),
                    FirstIssue(set));
            }

            // 非法 JSON
            {
                var source = new MemoryPackageSource();
                source.Add(P("bad.json.scbtpak"), Zip(
                    ScbtManifest.FileName, "{ this is not json",
                    ScbtTree.FileName, SimpleTree()));
                ScbtPackageSet set = Load(source, "bad.json");
                result.Check("invalid json is an error",
                    set.ErrorCount > 0 && ReportOf(set).HasCode(PackageCodes.JsonInvalid),
                    FirstIssue(set));
            }

            // 不是 zip
            {
                var source = new MemoryPackageSource();
                source.Add(P("not.zip.scbtpak"), new UTF8Encoding(false).GetBytes("hello"));
                ScbtPackageSet set = Load(source, "not.zip");
                result.Check("non-zip package is an error",
                    set.ErrorCount > 0 && ReportOf(set).HasCode(PackageCodes.ZipInvalid),
                    FirstIssue(set));
            }

            // 文件不存在
            {
                ScbtPackageSet set = Load(new MemoryPackageSource(), "ghost");
                result.Check("missing package reports file.missing",
                    set.ErrorCount > 0 && set.Report.HasCode(PackageCodes.FileMissing),
                    FirstIssue(set));
            }

            // 两个包用了同一个 id（通过引用被一起加载）
            {
                var source = new MemoryPackageSource();
                source.Add(P("dup.a.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("dup", "root",
                        References("other", "dup.b.scbtpak")),
                    ScbtTree.FileName, SubtreeTree("other")));
                source.Add(P("dup.b.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("dup", "root"),
                    ScbtTree.FileName, SimpleTree()));
                ScbtPackageSet set = Load(source, "dup.a");
                result.Check("two packages cannot share one id",
                    set.HasErrors && set.Packages.Count == 2
                    && set.Packages[1].Report.HasCode(PackageCodes.ManifestIdDuplicate),
                    "packages=" + set.Packages.Count + " :: " + FirstIssue(set));
            }
        }

        private static void ReferenceSafety(BtSelfTest.TestResult result)
        {
            // 环引用
            {
                var source = new MemoryPackageSource();
                source.Add(P("cyc.a.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("cyc.a", "root", References("cyc.b", "cyc.b.scbtpak")),
                    ScbtTree.FileName, SubtreeTree("cyc.b")));
                source.Add(P("cyc.b.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("cyc.b", "root", References("cyc.a", "cyc.a.scbtpak")),
                    ScbtTree.FileName, SubtreeTree("cyc.a")));
                ScbtPackageSet set = Load(source, "cyc.a");
                result.Check("reference cycle is detected",
                    set.HasErrors && set.Report.HasCode(PackageCodes.ReferenceCycle),
                    FirstIssue(set));
            }

            // 菱形引用：同一个包被两个引用点用到，合法且只加载一次
            {
                var source = DemoSource();
                source.Add(P("dia.root.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("dia.root", "root",
                        @"[{ ""id"": ""c1"", ""path"": ""common.scbtpak"" },
                           { ""id"": ""c2"", ""path"": ""common.scbtpak"" }]"),
                    ScbtTree.FileName, @"{
  ""id"": ""root"", ""type"": ""Root"", ""children"": [
    { ""id"": ""a"", ""type"": ""Task.Subtree"", ""properties"": { ""package"": ""c1"" } },
    { ""id"": ""b"", ""type"": ""Task.Subtree"", ""properties"": { ""package"": ""c2"" } }
  ]
}"));
                ScbtPackageSet set = Load(source, "dia.root");
                result.Check("diamond reference loads the shared package once",
                    !set.HasErrors && set.Packages.Count == 2,
                    "packages=" + set.Packages.Count + " " + FirstIssue(set));

                CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
                var a = compiled.Root.Find("a") as BtSubtreeTask;
                var b = compiled.Root.Find("b") as BtSubtreeTask;
                result.Check("diamond references still compile separate instances",
                    !compiled.HasErrors && a != null && b != null && a.Subtree != null
                    && b.Subtree != null && !ReferenceEquals(a.Subtree, b.Subtree),
                    FirstIssue(compiled.Report));
            }

            // 路径穿越
            {
                var source = new MemoryPackageSource();
                source.Add(P("esc.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("esc", "root",
                        References("up", "../../evil.scbtpak")),
                    ScbtTree.FileName, SubtreeTree("up")));
                ScbtPackageSet set = Load(source, "esc");
                result.Check("reference path traversal is rejected",
                    set.HasErrors && set.Root != null
                    && set.Root.Report.HasCode(PackageCodes.ManifestReferencePath),
                    FirstIssue(set));
            }

            // 引用绝对路径
            {
                var source = new MemoryPackageSource();
                source.Add(P("abs.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("abs", "root",
                        References("abs", "C:/windows/system32/evil.scbtpak")),
                    ScbtTree.FileName, SubtreeTree("abs")));
                ScbtPackageSet set = Load(source, "abs");
                result.Check("reference absolute path is rejected",
                    set.HasErrors && set.Root != null
                    && set.Root.Report.HasCode(PackageCodes.ManifestReferencePath),
                    FirstIssue(set));
            }

            // 引用文件不存在
            {
                var source = new MemoryPackageSource();
                source.Add(P("miss.scbtpak"), Zip(
                    ScbtManifest.FileName, ManifestJson("miss", "root",
                        References("gone", "gone.scbtpak")),
                    ScbtTree.FileName, SubtreeTree("gone")));
                ScbtPackageSet set = Load(source, "miss");
                result.Check("unresolved reference is reported",
                    set.HasErrors && set.Root != null
                    && set.Root.Report.HasCode(PackageCodes.ReferenceUnresolved),
                    FirstIssue(set));
            }

            // 引用落在**包目录的另一个文件**上（不是引用者旁边）：导出/另存出来的包引用的
            // `common.scbtpak` 还在同一个包目录里，必须找得到（单一目录，不再有第二来源）。
            {
                var source = new MemoryPackageSource();
                PackageTemplate demo = PackageTemplates.Demo();
                PackageTemplate common = PackageTemplates.Common();
                source.Add(Path.Combine(InstanceDir, "sub", PackageTemplates.DemoFile),
                    demo.ToBytes());
                source.Add(Path.Combine(InstanceDir, PackageTemplates.CommonFile),
                    common.ToBytes());

                var roots = new PackageRoots(InstanceDir);
                var options = new PackageLoadOptions { Source = source, Roots = roots };
                ScbtPackageSet set = PackageLoader.Load(
                    Path.Combine(InstanceDir, "sub", PackageTemplates.DemoFile), options);

                result.Check("a package in a subfolder still finds its reference in the package folder",
                    !set.HasErrors && set.Root != null && set.Root.ReferenceTargets.Count == 1
                    && set.Root.ReferenceTargets[0] != null,
                    FirstIssue(set));
                result.Check("that reference resolved inside the package folder",
                    set.Root != null && set.Root.ReferenceTargets.Count == 1
                    && set.Root.ReferenceTargets[0] != null
                    && set.Root.ReferenceTargets[0].Path.Replace('\\', '/')
                        .StartsWith(InstanceDir + "/", StringComparison.OrdinalIgnoreCase),
                    set.Root != null && set.Root.ReferenceTargets.Count > 0
                    && set.Root.ReferenceTargets[0] != null
                        ? set.Root.ReferenceTargets[0].Path : "<unresolved>");
            }

            // 引用深度：链式引用到第 10 层（默认上限 8）
            {
                var source = new MemoryPackageSource();
                for (int i = 0; i < 10; i++)
                {
                    string id = "chain" + i;
                    string next = "chain" + (i + 1);
                    bool last = i == 9;
                    source.Add(P(id + ".scbtpak"), Zip(
                        ScbtManifest.FileName,
                        last ? ManifestJson(id, "root") : ManifestJson(id, "root",
                            References(next, next + ".scbtpak")),
                        ScbtTree.FileName,
                        last ? SimpleTree() : SubtreeTree(next)));
                }
                ScbtPackageSet set = Load(source, "chain0");
                result.Check("reference depth limit is enforced",
                    set.HasErrors && set.Report.HasCode(PackageCodes.ReferenceDepth),
                    FirstIssue(set));
            }
        }

        private static void RootWhitelist(BtSelfTest.TestResult result)
        {
            var roots = new PackageRoots(InstanceDir);

            result.Check("whitelist accepts the package folder",
                roots.IsAllowed(InstanceDir + "/x.scbtpak"), roots.Describe());
            result.Check("whitelist rejects a sibling folder",
                !roots.IsAllowed("C:/pai-selftest-evil/x.scbtpak"), roots.Describe());
            result.Check("whitelist rejects prefix tricks",
                !roots.IsAllowed(InstanceDir + "-evil/x.scbtpak"), roots.Describe());
            result.Check("whitelist identifies the owner folder",
                roots.OwnerOf(InstanceDir + "/x.scbtpak") == roots.InstanceRoot,
                roots.Describe());

            var source = new MemoryPackageSource();
            source.Add(P("ok.scbtpak"), Zip(
                ScbtManifest.FileName, ManifestJson("ok", "root"),
                ScbtTree.FileName, SimpleTree()));
            var options = new PackageLoadOptions { Source = source, Roots = roots };
            ScbtPackageSet inside = PackageLoader.Load("ok", options);
            result.Check("loader finds a package by bare name", inside.Root != null, FirstIssue(inside));

            MemoryPackageSource outside = new MemoryPackageSource();
            outside.Add("C:/pai-selftest-evil/evil.scbtpak", Zip(
                ScbtManifest.FileName, ManifestJson("evil", "root"),
                ScbtTree.FileName, SimpleTree()));
            var outsideOptions = new PackageLoadOptions { Source = outside, Roots = roots };
            ScbtPackageSet escaped = PackageLoader.Load("C:/pai-selftest-evil/evil.scbtpak",
                outsideOptions);
            result.Check("loader refuses a path outside the whitelist",
                escaped.Root == null && escaped.ErrorCount > 0, FirstIssue(escaped));
        }

        private static void RoundTrip(BtSelfTest.TestResult result)
        {
            MemoryPackageSource source = DemoSource();
            ScbtPackageSet set = Load(source, "demo.greet");
            ScbtManifest manifest = set.Root.Manifest;
            ScbtTree tree = set.Root.Tree;

            PackageValue manifestValue = manifest.ToValue();
            PackageValue treeValue = tree.ToValue();

            var report = new PackageReport();
            PackageValue parsedManifest;
            PackageValue parsedTree;
            bool manifestOk = PackageJson.TryParse(manifestValue.ToJson(true), "roundtrip:manifest.json",
                report, out parsedManifest);
            bool treeOk = PackageJson.TryParse(treeValue.ToJson(true), "roundtrip:tree.json",
                report, out parsedTree);
            result.Check("exported json parses back", manifestOk && treeOk && !report.HasErrors,
                report.Summary());

            ScbtManifest again = ScbtManifest.Parse(parsedManifest, "roundtrip:manifest.json", report);
            ScbtTree treeAgain = ScbtTree.Parse(parsedTree, "roundtrip:tree.json", report);

            result.Check("manifest round-trips",
                again != null && again.Id == manifest.Id && again.Entry == manifest.Entry
                && again.References.Count == manifest.References.Count
                && again.Blackboard.Count == manifest.Blackboard.Count,
                again != null ? again.ToString() : "<null>");
            result.Check("tree round-trips node/decorator/service counts",
                treeAgain != null && treeAgain.NodeCount == tree.NodeCount
                && treeAgain.DecoratorCount == tree.DecoratorCount
                && treeAgain.ServiceCount == tree.ServiceCount,
                treeAgain != null ? treeAgain.ToString() : "<null>");
            result.Check("tree round-trip keeps node ids and types",
                treeAgain != null && treeAgain.Find("t2") != null
                && treeAgain.Find("t2").Type == tree.Find("t2").Type
                && treeAgain.Find("t5").Properties.Get("package").AsString() == "common",
                "t2/t5 mismatch");

            CompiledTree recompiled = TreeCompiler.Compile(set, set.Root);
            result.Check("recompiled tree matches the first compile",
                !recompiled.HasErrors && recompiled.NodeCount == 16,
                "nodes=" + recompiled.NodeCount + " " + FirstIssue(recompiled.Report));
        }

        private static void LoadAndCompileGate(BtSelfTest.TestResult result)
        {
            var source = new MemoryPackageSource();
            source.Add(P("gate.scbtpak"), Zip(
                ScbtManifest.FileName, ManifestJson("gate", "root"),
                ScbtTree.FileName, @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                     { ""id"": ""x"", ""type"": ""Task.Nope"" }] }"));
            var options = new PackageLoadOptions
            {
                Source = source,
                Roots = new PackageRoots(InstanceDir)
            };

            ScbtPackageSet set;
            CompiledTree compiled = PackageLoader.LoadAndCompile("gate", out set, options);
            result.Check("LoadAndCompile refuses a package with errors",
                compiled.HasErrors && compiled.Root == null,
                "root=" + (compiled.Root != null ? compiled.Root.NodeType : "<null>"));

            MemoryPackageSource good = DemoSource();
            var goodOptions = new PackageLoadOptions
            {
                Source = good,
                Roots = new PackageRoots(InstanceDir)
            };
            CompiledTree ok = PackageLoader.LoadAndCompile("demo.greet", out set, goodOptions);
            result.Check("LoadAndCompile returns a runnable tree for a good package",
                !ok.HasErrors && ok.Root != null && ok.NodeCount == 16,
                ok.Describe());
        }


        // ---------------------------------------------------------------- 出厂模板 + 推送式热重载（P0-5 / P0-6）

        private static void TemplatesAndReload(BtSelfTest.TestResult result)
        {
            string directory = Path.Combine(Path.GetTempPath(), "pai-selftest-packages");
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // 上一次残留清不掉也不影响本次（文件会被覆盖写）
            }
            Directory.CreateDirectory(directory);

            var roots = new PackageRoots(Path.Combine(directory, "instance"));

            List<string> installed;
            string error;
            int count = PackageTemplates.Install(roots, out installed, out error);
            // 期望值**从模板清单算**，不写死数字：加一个出厂示例就不该让内核自检红一次
            // （2026-09-13 加 su.watch 时就因为写死的 4 红了）。
            int expected = PackageTemplates.All().Count + PackageTemplates.ExtraFiles().Count;
            result.Check("factory templates install into the package folder",
                count == expected && error == null,
                error ?? ("count=" + count + " expected=" + expected));
            result.Check("template install is idempotent (never overwrites)",
                PackageTemplates.Install(roots, out installed, out error) == 0,
                error ?? ("second call wrote " + installed.Count));

            string demoPath = Path.Combine(roots.InstanceRoot.Path, PackageTemplates.DemoFile);
            string commonPath = Path.Combine(roots.InstanceRoot.Path, PackageTemplates.CommonFile);
            result.Check("installed template files exist",
                File.Exists(demoPath) && File.Exists(commonPath), roots.Describe());

            var options = new PackageLoadOptions { Roots = roots };
            var reloader = new PackageReloader(roots, options);
            var actuator = new BtTestActuator();
            var sensor = new BtTestSensor();
            var blackboard = new AiBlackboard();
            var runtime = new BtRuntime(blackboard, sensor, actuator);

            TreeReloadResult load = reloader.Load("demo.greet", runtime);
            result.Check("factory demo package loads from disk",
                load.Replaced && !load.Rejected && reloader.IsLoaded, load.Describe());
            result.Check("factory demo package is issue-free",
                load.Issues.Count == 0, string.Join(" | ", load.Issues.ToArray()));
            result.Check("factory demo compiled node count (main + nested)",
                runtime.Root != null && load.NodeCount == 8, "nodes=" + load.NodeCount);
            result.Check("loader picked up both packages of the demo",
                reloader.LoadedPackageCount == 2, reloader.Describe());

            // 铁律：装载后**不许残留文件句柄** —— 立刻用"独占写"打开验证
            string lockError = null;
            bool exclusive = true;
            try
            {
                using (var stream = new FileStream(demoPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None))
                {
                    stream.Flush();
                }
            }
            catch (Exception exception)
            {
                exclusive = false;
                lockError = exception.GetType().Name + ": " + exception.Message;
            }
            result.Check("package file is not locked after loading", exclusive,
                "exclusive open failed: " + (lockError ?? "<none>"));

            // 让服务把目标写进黑板 → 观察者抢占 → 走向目标（按住 w）
            sensor.SetTarget(new AiActorView
            {
                Name = "basil",
                Position = new Vector3(0f, 0f, 10f),
                Distance = 10f
            });
            for (int i = 0; i < 90; i++)
                runtime.Tick(Dt);

            BtNode greet = runtime.Root.Find("greet");
            result.Check("observer preemption selects the greet branch when a target appears",
                greet != null && greet.IsActive, "greet active=" + (greet != null && greet.IsActive));
            BtNode moveBefore = runtime.Root.Find("move");
            result.Check("factory demo reaches the MoveTo task",
                moveBefore != null && moveBefore.IsActive, "move=" + (moveBefore != null));
            result.Check("factory demo drives movement (forward key held)",
                actuator.HeldKeys.Contains("w"), "keys=" + actuator.HeldKeys.Count);
            float activeBefore = moveBefore != null ? moveBefore.ActiveTime : -1f;

            // ---- 热重载 1：哈希不符 → 忽略（对方还在写）
            reloader.Notify(demoPath, "sha256:00000000000000000000000000000000");
            bool wrongHash = reloader.ApplyPending(runtime);
            result.Check("notify with a wrong hash is ignored",
                !wrongHash && reloader.LastResult.Ignored && reloader.IgnoredCount == 1,
                reloader.LastResult.Describe());

            // ---- 热重载 2：哈希正确 → 换树 + 运行态迁移
            PackageTemplate demo = PackageTemplates.Demo();
            PackageValue changedTree = demo.Tree.DeepClone();
            PackageValue moveNode = FindNode(changedTree, "move");
            result.Check("template tree exposes the move node", moveNode != null, "<missing>");
            if (moveNode != null)
                moveNode.Get("properties").Set("acceptableRadius", PackageValue.Number(8));

            byte[] changed = PackageWriter.ToBytes(demo.Manifest, changedTree);
            string writeError;
            PackageWriter.TryWriteFile(demoPath, changed, out writeError);
            reloader.Notify(demoPath, "sha256:" + PackageLoader.ComputeHash(changed));
            bool hotReloaded = reloader.ApplyPending(runtime);

            result.Check("notify with the right hash hot-reloads the tree",
                hotReloaded && reloader.ReloadCount == 2, reloader.LastResult.Describe());
            result.Check("hot reload preserved the running path",
                reloader.LastResult.Migration != null
                && reloader.LastResult.Migration.PreservedNodes >= 3
                && reloader.LastResult.Migration.AbortedNodes == 0,
                reloader.LastResult.Migration != null
                    ? reloader.LastResult.Migration.Describe() : "<no migration>");

            var moveAfter = runtime.Root.Find("move") as BtMoveToTargetTask;
            result.Check("changed parameter is in effect after reload",
                moveAfter != null && Math.Abs(moveAfter.AcceptableRadius - 8f) < 1e-4f,
                moveAfter == null ? "<missing>" : moveAfter.AcceptableRadius.ToString());
            result.Check("preserved task kept its progress across reload",
                moveAfter != null && moveAfter.ActiveTime >= activeBefore,
                "before=" + activeBefore.ToString("0.000") + " after="
                + (moveAfter != null ? moveAfter.ActiveTime.ToString("0.000") : "?"));
            result.Check("hot reload released no input", actuator.ReleaseAllCalls == 0,
                "releaseAll=" + actuator.ReleaseAllCalls);

            // ---- 热重载 3：半截文件 → 拒绝 + 保留旧树
            byte[] half = new UTF8Encoding(false).GetBytes("PK\u0003\u0004 half written package");
            PackageWriter.TryWriteFile(demoPath, half, out writeError);
            reloader.RequestReload(demoPath);
            bool brokenReload = reloader.ApplyPending(runtime);
            result.Check("half-written package is rejected and the old tree is kept",
                !brokenReload && reloader.LastResult.Rejected && reloader.IsLoaded,
                reloader.LastResult.Describe());
            result.Check("rejected reload explains why",
                reloader.LastResult.Issues.Count > 0, "<no issues recorded>");
            result.Check("old tree is still the active one after a rejected reload",
                runtime.Root.Find("move") != null && reloader.ActiveTree != null
                && reloader.ActivePath != null
                && reloader.ActivePath.EndsWith(PackageTemplates.DemoFile, StringComparison.OrdinalIgnoreCase),
                reloader.Describe());

            // ---- 热重载 4：文件恢复 → 手动重载成功（兜底通道）
            PackageWriter.TryWriteFile(demoPath, changed, out writeError);
            reloader.RequestReload("demo.greet");
            bool manualReload = reloader.ApplyPending(runtime);
            result.Check("manual reload recovers once the file is valid again",
                manualReload && reloader.ReloadCount == 3, reloader.LastResult.Describe());

            // ---- 热重载 5：改**被引用**的包 → 活动树一起刷新（验收标准 4）
            PackageTemplate common = PackageTemplates.Common();
            PackageValue commonTree = common.Tree.DeepClone();
            PackageValue lookNode = FindNode(commonTree, "look");
            if (lookNode != null)
                lookNode.Get("properties").Set("seconds", PackageValue.Number(0.75));
            byte[] changedCommon = PackageWriter.ToBytes(common.Manifest, commonTree);
            PackageWriter.TryWriteFile(commonPath, changedCommon, out writeError);

            reloader.Notify(commonPath, PackageLoader.ComputeHash(changedCommon));
            bool nestedReload = reloader.ApplyPending(runtime);
            var subtree = runtime.Root.Find("sub_look") as BtSubtreeTask;
            var look = subtree != null && subtree.Subtree != null
                ? subtree.Subtree.Find("look") as BtLookAtTargetTask : null;
            result.Check("changing a referenced package reloads the whole active tree",
                nestedReload && look != null && Math.Abs(look.Seconds - 0.75f) < 1e-4f,
                "reloaded=" + nestedReload + " seconds="
                + (look != null ? look.Seconds.ToString("0.###") : "<missing>"));

            // ---- 热重载 6：与活动树无关的包 → 拒绝（不能把别人的包塞进当前运行）
            string otherPath = Path.Combine(roots.InstanceRoot.Path, "other.tree.scbtpak");
            PackageValue otherManifest = demo.Manifest.DeepClone();
            otherManifest.Set("id", PackageValue.Str("other.tree"));
            byte[] otherBytes = PackageWriter.ToBytes(otherManifest, demo.Tree);
            PackageWriter.TryWriteFile(otherPath, otherBytes, out writeError);
            reloader.Notify(otherPath, PackageLoader.ComputeHash(otherBytes));
            bool unrelated = reloader.ApplyPending(runtime);
            result.Check("notify for an unrelated package is rejected",
                !unrelated && reloader.LastResult.Rejected && reloader.LastResult.Reason != null
                && reloader.LastResult.Reason.Contains("not part"),
                reloader.LastResult.Describe());

            // ---- 预检接口：编辑器保存前就能问"这个包行不行"
            PackageReport goodReport = reloader.Validate("demo.greet");
            result.Check("Validate() passes a good package", !goodReport.HasErrors,
                goodReport.Summary());
            PackageWriter.TryWriteFile(commonPath, new UTF8Encoding(false).GetBytes("not a zip at all"),
                out writeError);
            PackageReport badReport = reloader.Validate(PackageTemplates.CommonFile);
            result.Check("Validate() reports a broken package before it is loaded",
                badReport.HasErrors && badReport.HasCode(PackageCodes.ZipInvalid),
                badReport.Summary() + " :: " + (badReport.FirstError != null
                    ? badReport.FirstError.Describe() : "<none>"));
            PackageWriter.TryWriteFile(commonPath, changedCommon, out writeError);

            // ---- 可选低频扫描兜底（默认关闭；打开后能发现"手改文件但没人通知"）
            reloader.PackageWatchSeconds = 0.05;
            PackageValue watchedTree = demo.Tree.DeepClone();
            PackageValue watchedMove = FindNode(watchedTree, "move");
            if (watchedMove != null)
                watchedMove.Get("properties").Set("acceptableRadius", PackageValue.Number(5));
            PackageWriter.TryWriteFile(demoPath, PackageWriter.ToBytes(demo.Manifest, watchedTree),
                out writeError);

            reloader.Tick(0.2);
            result.Check("watch scan enqueues a reload when the file changed", reloader.PendingCount > 0,
                "pending=" + reloader.PendingCount);
            bool watched = reloader.ApplyPending(runtime);
            var watchedMoveNode = runtime.Root.Find("move") as BtMoveToTargetTask;
            result.Check("watch scan reloads without an editor notification",
                watched && watchedMoveNode != null
                && Math.Abs(watchedMoveNode.AcceptableRadius - 5f) < 1e-4f,
                "radius=" + (watchedMoveNode != null
                    ? watchedMoveNode.AcceptableRadius.ToString("0.###") : "<missing>"));
        }

        /// <summary>在文档里按 id 找节点（测试里改模板参数用）。</summary>
        private static PackageValue FindNode(PackageValue node, string id)
        {
            if (node == null || !node.IsObject)
                return null;
            if (string.Equals(node.Get("id").AsString(null), id, StringComparison.Ordinal))
                return node;

            PackageValue children = node.Get("children");
            for (int i = 0; i < children.Count; i++)
            {
                PackageValue found = FindNode(children.Item(i), id);
                if (found != null)
                    return found;
            }
            return null;
        }


        // ---------------------------------------------------------------- 树库与毫秒级切换（P0-11）

        private static void LibrarySwitching(BtSelfTest.TestResult result)
        {
            string directory = Path.Combine(Path.GetTempPath(), "pai-lib-selftest");
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception)
            {
            }
            Directory.CreateDirectory(directory);

            var roots = new PackageRoots(Path.Combine(directory, "instance"));
            List<string> installed;
            string installError;
            PackageTemplates.Install(roots, out installed, out installError);

            var options = new PackageLoadOptions { Roots = roots };
            var reloader = new PackageReloader(roots, options);
            var library = new TreeLibrary(reloader, 2);
            var host = new AiTestHost("library-host");
            var runtime = host.Tree;

            // ---- 预编译：把成本挪到切换之前
            var report = new PackageReport();
            PreparedTree prepared = library.Prepare("demo.greet", null, report);
            result.Check("prepare compiles a tree into the resident library",
                prepared.Ready && prepared.NodeCount == 8 && report.ErrorCount == 0,
                prepared.Describe() + " :: " + report.Summary());
            result.Check("prepare records a source hash for staleness checks",
                prepared.Hash != null && prepared.Hash.Length == 64,
                prepared.Hash ?? "<null>");
            result.Check("prepared tree is listed by the library",
                library.Prepared.Count == 1 && library.PrepareCount == 1, library.Describe());

            // ---- 切换：应该完全走常驻副本
            TreeSwitchResult first = library.Switch("demo.greet", host);
            result.Check("switch uses the prepared copy (no compile)",
                first.Switched && first.UsedPrepared && !first.Recompiled
                && first.NodeCount == 8,
                first.Describe());
            result.Check("switch is fast (pointer swap, not a compile)",
                first.SwitchMilliseconds < first.CompileMilliseconds,
                "switch=" + first.SwitchMilliseconds.ToString("0.000") + "ms compile="
                + first.CompileMilliseconds.ToString("0.000") + "ms");
            result.Check("switch leaves the host running the tree",
                host.HasTree && runtime.IsRunning && host.Enabled, first.Describe());
            result.Check("switch adopts the tree for hot reload",
                reloader.ActivePath != null
                && reloader.ActivePath.EndsWith(PackageTemplates.DemoFile, StringComparison.OrdinalIgnoreCase)
                && reloader.IsLoaded,
                reloader.Describe());

            // ---- 切换后热重载仍然认得这棵活动树（Adopt 的意义）
            string demoPath = Path.Combine(roots.InstanceRoot.Path, PackageTemplates.DemoFile);
            reloader.RequestReload("demo.greet");
            bool reloaded = reloader.ApplyPending(runtime);
            result.Check("hot reload still applies to the switched tree",
                reloaded && reloader.ReloadCount == 1, reloader.LastResult.Describe());

            // ---- 切回同一棵树：仍然走常驻副本，节点数稳定
            TreeSwitchResult second = library.Switch("demo.greet", host);
            result.Check("switching back reuses the resident copy",
                second.Switched && second.UsedPrepared && second.NodeCount == 8,
                second.Describe());
            result.Check("switch counter tracks usage",
                prepared.SwitchCount == 2 && library.SwitchCount == 2, library.Describe());

            // ---- 文件被改过 → 副本过期 → 重编译（并把这件事记下来）
            PackageTemplate demo = PackageTemplates.Demo();
            PackageValue changedTree = demo.Tree.DeepClone();
            PackageValue moveNode = FindNode(changedTree, "move");
            if (moveNode != null)
                moveNode.Get("properties").Set("acceptableRadius", PackageValue.Number(7));
            string writeError;
            PackageWriter.TryWriteFile(demoPath, PackageWriter.ToBytes(demo.Manifest, changedTree),
                out writeError);

            TreeSwitchResult stale = library.Switch("demo.greet", host);
            result.Check("a changed file makes the resident copy stale and recompiles",
                stale.Switched && stale.Recompiled && library.StaleRecompiles == 1,
                stale.Describe() + " " + library.Describe());
            var moveAfter = runtime.Root.Find("move") as BtMoveToTargetTask;
            result.Check("switching to the changed file picks up the new value",
                moveAfter != null && Math.Abs(moveAfter.AcceptableRadius - 7f) < 1e-4f,
                moveAfter == null ? "<missing>" : moveAfter.AcceptableRadius.ToString());

            // ---- 坏包：切换失败但**不动**当前运行
            TreeSwitchResult missing = library.Switch("no.such.tree", host);
            result.Check("switching to a missing package fails cleanly",
                !missing.Switched && missing.Reason != null
                && missing.Reason.Contains("not found"),
                missing.Describe());
            result.Check("a failed switch keeps the current tree",
                runtime.Root.Find("move") != null && runtime.IsRunning,
                "tree was disturbed");

            // ---- 常驻上限：装不下就淘汰（保留常用的）
            library.Prepare("common.scbtpak");
            library.Prepare("demo.greet");
            result.Check("library capacity evicts old entries",
                library.Prepared.Count <= library.Capacity,
                "prepared=" + library.Prepared.Count + " capacity=" + library.Capacity);

            // ---- 预编译目录里的全部包（prepare all）
            // 容量必须**够装下所有出厂包**，否则数出来的是"容量"而不是"目录里的包数"
            // （生产环境的容量是 8，见 ai.status 的 library.capacity）
            var all = new TreeLibrary(reloader, PackageTemplates.All().Count + 4);
            var allReport = new PackageReport();
            List<PreparedTree> preparedAll = all.PrepareAll(0, allReport);
            // 这里数的是**目录里的行为树包**（.scbtpak）：出厂模板里的动作包是另一种扩展名，不算
            int expectedTrees = PackageTemplates.All().Count;
            result.Check("prepare all compiles every package in the folders",
                preparedAll.Count == expectedTrees && allReport.ErrorCount == 0,
                "count=" + preparedAll.Count + " expected=" + expectedTrees + " :: "
                + allReport.Summary());

            // ---- 状态描述（控制面读它）
            List<Dictionary<string, object>> described = library.DescribePrepared();
            result.Check("library describes prepared trees for ai.status",
                described.Count > 0 && described[0].ContainsKey("ready")
                && described[0].ContainsKey("compileMs"),
                "entries=" + described.Count);
        }

        // ---------------------------------------------------------------- 工具

        private static void CheckCase(BtSelfTest.TestResult result, string name, string code,
            string manifestJson, string treeJson, bool isError = true)
        {
            var source = new MemoryPackageSource();
            source.Add(P("case.scbtpak"), Zip(
                ScbtManifest.FileName, manifestJson,
                ScbtTree.FileName, treeJson));

            ScbtPackageSet set = Load(source, "case");
            PackageReport report = ReportOf(set);
            bool codePresent = report.HasCode(code);

            if (isError)
            {
                result.Check(name, codePresent && set.HasErrors,
                    "code=" + code + " present=" + codePresent + " errors=" + report.ErrorCount
                    + " :: " + FirstIssue(set));
            }
            else
            {
                result.Check(name, codePresent && report.ErrorCount == 0,
                    "code=" + code + " present=" + codePresent + " errors=" + report.ErrorCount
                    + " :: " + FirstIssue(set));
            }
        }

        private static MemoryPackageSource DemoSource()
        {
            var source = new MemoryPackageSource();
            source.Add(P("demo.greet.scbtpak"), Zip(
                ScbtManifest.FileName, ManifestJson("demo.greet", "root",
                    References("common", "common.scbtpak"),
                    @"[{ ""name"": ""target"", ""type"": ""actor"" },
                       { ""name"": ""targetDistance"", ""type"": ""float"", ""readonly"": true }]"),
                ScbtTree.FileName, DemoTree()));
            source.Add(P("common.scbtpak"), Zip(
                ScbtManifest.FileName, ManifestJson("common", "root"),
                ScbtTree.FileName, CommonTree()));
            return source;
        }

        private static ScbtPackageSet Load(MemoryPackageSource source, string name)
        {
            var options = new PackageLoadOptions
            {
                Source = source,
                Roots = new PackageRoots(InstanceDir)
            };
            return PackageLoader.Load(name, options);
        }

        private static string P(string fileName)
        {
            return Path.Combine(InstanceDir, fileName);
        }

        private static PackageReport ReportOf(ScbtPackageSet set)
        {
            if (set.Root != null)
                return set.Root.Report;
            if (set.Packages.Count > 0)
                return set.Packages[0].Report;
            return set.Report;
        }

        private static string FirstIssue(ScbtPackageSet set)
        {
            if (set.ErrorCount == 0 && set.WarningCount == 0)
                return "no issues";
            var lines = set.Summarize(4);
            return string.Join(" | ", lines.ToArray());
        }

        private static string FirstIssue(PackageReport report)
        {
            if (report == null || report.IsEmpty)
                return "no issues";
            var lines = report.Summarize(3);
            return string.Join(" | ", lines.ToArray());
        }

        private static string ManifestJson(string id, string entry, string references = null,
            string blackboard = null)
        {
            var builder = new StringBuilder();
            builder.Append("{\"format\":\"scbt\",\"version\":1,\"id\":\"").Append(id).Append('"');
            if (!string.IsNullOrEmpty(entry))
                builder.Append(",\"entry\":\"").Append(entry).Append('"');
            if (!string.IsNullOrEmpty(blackboard))
                builder.Append(",\"blackboard\":").Append(blackboard);
            if (!string.IsNullOrEmpty(references))
                builder.Append(",\"references\":").Append(references);
            builder.Append('}');
            return builder.ToString();
        }

        private static string References(string id, string path)
        {
            return "[{ \"id\": \"" + id + "\", \"path\": \"" + path + "\" }]";
        }

        private static string SimpleTree()
        {
            return @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                       { ""id"": ""w"", ""type"": ""Task.Wait"", ""properties"": { ""seconds"": 0 } }] }";
        }

        private static string SubtreeTree(string referenceId)
        {
            return @"{ ""id"": ""root"", ""type"": ""Root"", ""children"": [
                       { ""id"": ""sub"", ""type"": ""Task.Subtree"",
                         ""properties"": { ""package"": """ + referenceId + @""" } }] }";
        }

        private static string DeepTreeJson(int depth)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < depth; i++)
            {
                builder.Append("{ \"id\": \"n").Append(i)
                    .Append("\", \"type\": \"Sequence\", \"children\": [");
            }
            builder.Append("{ \"id\": \"leaf\", \"type\": \"Task.Wait\", \"properties\": { \"seconds\": 0 } }");
            for (int i = 0; i < depth; i++)
                builder.Append("] }");
            return builder.ToString();
        }

        private static string DemoTree()
        {
            return @"{
  ""id"": ""root"", ""type"": ""Root"",
  ""children"": [{
    ""id"": ""sel"", ""type"": ""Selector"",
    ""services"": [{ ""id"": ""svc1"", ""type"": ""UpdateNearestPlayer"", ""interval"": 0.25,
                     ""properties"": { ""targetKey"": ""target"", ""nameFilter"": ""basil"" } }],
    ""children"": [
      {
        ""id"": ""seq"", ""type"": ""Sequence"",
        ""decorators"": [{ ""id"": ""d1"", ""type"": ""Blackboard"", ""observerAborts"": ""LowerPriority"",
                          ""properties"": { ""key"": ""target"", ""query"": ""IsSet"" } }],
        ""children"": [
          { ""id"": ""t1"", ""type"": ""Task.LookAt"",
            ""properties"": { ""targetKey"": ""target"", ""eyeHeight"": 1.35 } },
          { ""id"": ""t2"", ""type"": ""MoveTo"",
            ""properties"": { ""targetKey"": ""target"", ""acceptableRadius"": 3.0, ""timeout"": 25.0 } },
          { ""id"": ""t3"", ""type"": ""Task.PlayActionPackage"",
            ""properties"": { ""packages"": [""greet_wave.scatpak"", ""wave_slow.scatpak""],
                              ""mode"": ""Sequence"", ""abortOnFail"": true } }
        ]
      },
      { ""id"": ""t4"", ""type"": ""Task.Wait"", ""properties"": { ""seconds"": 1.0 } },
      { ""id"": ""t5"", ""type"": ""Task.Subtree"", ""properties"": { ""package"": ""common"" } },
      { ""id"": ""t6"", ""type"": ""Task.Subtree"", ""properties"": { ""package"": ""common#sub"" } }
    ]
  }]
}";
        }

        private static string CommonTree()
        {
            return @"{
  ""id"": ""root"", ""type"": ""Root"",
  ""children"": [{
    ""id"": ""sub"", ""type"": ""Sequence"",
    ""children"": [
      { ""id"": ""c1"", ""type"": ""Task.SetBlackboard"",
        ""properties"": { ""key"": ""nested"", ""valueKind"": ""bool"", ""value"": true } },
      { ""id"": ""c2"", ""type"": ""Task.Log"", ""properties"": { ""message"": ""nested ran"" } }
    ]
  }]
}";
        }

        /// <summary>把若干 (条目名, 文本) 打成内存 zip（与 .scmod 同样的"ZIP 改名"哲学）。</summary>
        private static byte[] Zip(params string[] namesAndContents)
        {
            using (var stream = new MemoryStream())
            {
                using (var archive = ZipArchive.Create(stream, keepStreamOpen: true))
                {
                    var utf8 = new UTF8Encoding(false);
                    for (int i = 0; i + 1 < namesAndContents.Length; i += 2)
                    {
                        byte[] data = utf8.GetBytes(namesAndContents[i + 1]);
                        using (var source = new MemoryStream(data))
                        {
                            archive.AddStream(namesAndContents[i], source);
                        }
                    }
                }
                return stream.ToArray();
            }
        }
    }
}

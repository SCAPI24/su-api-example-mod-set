using Engine;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 内存态改写与导出自检（P0-12 / P0-13）：**纯逻辑、不依赖游戏**（用临时目录里的真包）。
    ///
    /// 最重要的一条断言：**改过内存树之后，原 `.scbtpak` 的字节数/哈希完全不变**（验收标准 7）。
    /// 另外覆盖：改参数（且不把别的参数打回默认）、未知属性名算错误、插入/删除/搬移的边界
    /// （重复 id、非组合父节点、把节点挪进自己子树、删空组合需要 force）、
    /// 被摘掉的正在运行的任务要收尾（释放输入）、`reload` 丢弃内存改动，
    /// 以及导出成**新包**后能重新装载编译、且原包不动。
    /// </summary>
    public static class TreeEditSelfTest
    {
        private const float Dt = 1f / 60f;

        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "TreeEditSelfTest" };
            try
            {
                EditParameters(result);
                EditStructure(result);
                EditSafety(result);
                Export(result);
            }
            catch (Exception exception)
            {
                result.Check("TreeEditSelfTest no unexpected exception", false,
                    exception.GetType().Name + ": " + exception.Message);
            }
            return result;
        }

        // ---------------------------------------------------------------- 夹具

        private sealed class Fixture
        {
            public string Directory;
            public PackageRoots Roots;
            public PackageReloader Reloader;
            public AiTestHost Host;
            public string DemoPath;
            public string OriginalHash;
        }

        private static Fixture Create(string name)
        {
            var fixture = new Fixture();
            fixture.Directory = Path.Combine(Path.GetTempPath(), name);
            try
            {
                if (Directory.Exists(fixture.Directory))
                    Directory.Delete(fixture.Directory, true);
            }
            catch (Exception)
            {
            }
            Directory.CreateDirectory(fixture.Directory);

            fixture.Roots = new PackageRoots(Path.Combine(fixture.Directory, "instance"));
            List<string> installed;
            string installError;
            PackageTemplates.Install(fixture.Roots, out installed, out installError);

            fixture.DemoPath = Path.Combine(fixture.Roots.InstanceRoot.Path, PackageTemplates.DemoFile);
            fixture.OriginalHash = PackageLoader.ComputeHash(File.ReadAllBytes(fixture.DemoPath));

            var options = new PackageLoadOptions { Roots = fixture.Roots };
            fixture.Reloader = new PackageReloader(fixture.Roots, options);
            fixture.Host = new AiTestHost("edit-host");

            TreeReloadResult load = fixture.Reloader.Load("demo.greet", fixture.Host.Tree);
            if (!load.Replaced)
                throw new InvalidOperationException("fixture could not load the demo package: "
                    + load.Describe());

            return fixture;
        }

        private static string HashOf(string path)
        {
            return File.Exists(path)
                ? PackageLoader.ComputeHash(File.ReadAllBytes(path))
                : null;
        }

        private static ScbtNodeDoc ParseNodeDoc(string json, PackageReport report)
        {
            PackageValue value;
            if (!PackageJson.TryParse(json, "edit.selftest", report, out value))
                return null;

            ScbtTree tree = ScbtTree.Parse(value, "edit.selftest", report);
            return tree != null ? tree.Root : null;
        }

        // ---------------------------------------------------------------- 用例

        private static void EditParameters(BtSelfTest.TestResult result)
        {
            Fixture fixture = Create("pai-edit-params");
            BtRuntime runtime = fixture.Host.Tree;
            LoadedPackage owner = fixture.Reloader.ActiveSet.Root;

            var move = runtime.Root.Find("move") as BtMoveToTargetTask;
            result.Check("fixture loaded the demo tree", move != null && move.TimeoutSeconds > 0f,
                move == null ? "<missing move>" : move.ToString());

            TreeEditResult set = TreeMutation.SetProperty(runtime, "move", "acceptableRadius",
                PackageValue.Number(6));
            result.Check("ai.edit.set changes a parameter in the live tree",
                set.Applied && Math.Abs(move.AcceptableRadius - 6f) < 1e-4f, set.Describe());

            result.Check("editing marks the live tree dirty (source no longer matches)",
                runtime.IsDirty, "IsDirty=false");

            result.Check("*** the original package bytes are untouched ***",
                string.Equals(HashOf(fixture.DemoPath), fixture.OriginalHash,
                    StringComparison.OrdinalIgnoreCase),
                "source hash changed after an in-memory edit");

            result.Check("setting one parameter does not reset the others",
                move.TargetKey == "target" && Math.Abs(move.TimeoutSeconds - 25f) < 1e-4f,
                move.ToString());

            TreeEditResult unknown = TreeMutation.SetProperty(runtime, "move", "timeOut",
                PackageValue.Number(9));
            result.Check("an unknown property name is rejected (typos must not silently apply)",
                !unknown.Applied && unknown.Issues.Count > 0, unknown.Describe());
            result.Check("a rejected edit leaves the node untouched",
                Math.Abs(move.TimeoutSeconds - 25f) < 1e-4f,
                "timeout=" + move.TimeoutSeconds.ToString("0.0"));

            TreeEditResult kind = TreeMutation.SetProperty(runtime, "move", "acceptableRadius",
                PackageValue.Str("far"));
            result.Check("a wrong value type is rejected", !kind.Applied, kind.Describe());

            TreeEditResult ghost = TreeMutation.SetProperty(runtime, "no.such.node", "seconds",
                PackageValue.Number(1));
            result.Check("editing a missing node reports node not found",
                !ghost.Applied && ghost.Reason != null && ghost.Reason.Contains("not found"),
                ghost.Describe());

            // reload 丢弃全部内存改动：原包就是那条永远可回退的基线
            fixture.Reloader.RequestReload("demo.greet");
            bool reloaded = fixture.Reloader.ApplyPending(runtime);
            var reloadedMove = runtime.Root.Find("move") as BtMoveToTargetTask;
            result.Check("reload discards in-memory edits (the package is the baseline)",
                reloaded && reloadedMove != null
                && Math.Abs(reloadedMove.AcceptableRadius - 3f) < 1e-4f && !runtime.IsDirty,
                reloadedMove == null ? "<missing>" : reloadedMove.AcceptableRadius.ToString());

            result.Check("original bytes still untouched after reload",
                string.Equals(HashOf(fixture.DemoPath), fixture.OriginalHash,
                    StringComparison.OrdinalIgnoreCase),
                "source hash changed");

            // 导出的属性快照要能表达当前节点（改参数用的就是它）
            var moveNode = runtime.Root.Find("move") as BtMoveToTargetTask;
            PackageValue captured = TreeWriter.CaptureProperties(moveNode);
            result.Check("property capture round-trips the node's parameters",
                captured.Get("targetKey").AsString(null) == "target"
                && Math.Abs(captured.Get("acceptableRadius").AsFloat() - 3f) < 1e-4f
                && Math.Abs(captured.Get("timeout").AsFloat() - 25f) < 1e-4f,
                captured.Preview(120));

            // 捕获 → 改 → 套用，等价于 set（验证两个入口一致）
            captured.Set("acceptableRadius", PackageValue.Number(4.5f));
            var applyReport = new PackageReport();
            string detail;
            bool applied = TreeCompiler.TryApplyProperties(moveNode, captured, applyReport,
                out detail);
            result.Check("captured properties can be applied back to the node",
                applied && Math.Abs(((BtMoveToTargetTask)runtime.Root.Find("move")).AcceptableRadius
                    - 4.5f) < 1e-4f,
                detail + " :: " + applyReport.Summary());
        }

        private static void EditStructure(BtSelfTest.TestResult result)
        {
            Fixture fixture = Create("pai-edit-structure");
            BtRuntime runtime = fixture.Host.Tree;
            LoadedPackage owner = fixture.Reloader.ActiveSet.Root;

            var report = new PackageReport();
            ScbtNodeDoc waitDoc = ParseNodeDoc(
                @"{ ""id"": ""w_new"", ""type"": ""Task.Wait"", ""properties"": { ""seconds"": 2.5 } }",
                report);
            result.Check("edit node definitions parse with the package format",
                waitDoc != null && report.ErrorCount == 0, report.Summary());

            BtNode sequence = runtime.Root.Find("greet");
            int childrenBefore = sequence != null ? ((BtCompositeNode)sequence).ChildNodes.Count : -1;
            int nodesBefore = TreeMutation.Count(runtime.Root);

            TreeEditResult insert = TreeMutation.Insert(runtime, "greet", -1, waitDoc, owner);
            result.Check("ai.edit.insert adds a node to a composite",
                insert.Applied && runtime.Root.Find("w_new") != null
                && ((BtCompositeNode)runtime.Root.Find("greet")).ChildNodes.Count == childrenBefore + 1,
                insert.Describe());
            result.Check("inserted node counts toward the live tree",
                TreeMutation.Count(runtime.Root) == nodesBefore + 1,
                "nodes=" + TreeMutation.Count(runtime.Root));
            result.Check("insert marks the tree dirty", runtime.IsDirty, "IsDirty=false");

            TreeEditResult duplicate = TreeMutation.Insert(runtime, "greet", -1, waitDoc, owner);
            result.Check("inserting a duplicate node id is rejected",
                !duplicate.Applied && duplicate.Reason != null && duplicate.Reason.Contains("already exists"),
                duplicate.Describe());

            var badReport = new PackageReport();
            ScbtNodeDoc badDoc = ParseNodeDoc(
                @"{ ""id"": ""bad"", ""type"": ""Task.Nope"" }", badReport);
            TreeEditResult badType = TreeMutation.Insert(runtime, "greet", -1, badDoc, owner);
            result.Check("inserting an unknown node type is rejected", !badType.Applied,
                badType.Describe() + " :: " + badReport.Summary());

            TreeEditResult wrongParent = TreeMutation.Insert(runtime, "w_new", -1, waitDoc, owner);
            result.Check("inserting under a task is rejected",
                !wrongParent.Applied && wrongParent.Reason != null
                && wrongParent.Reason.Contains("cannot have children"),
                wrongParent.Describe());

            TreeEditResult missingParent = TreeMutation.Insert(runtime, "ghost", -1, waitDoc, owner);
            result.Check("inserting under a missing parent is rejected", !missingParent.Applied,
                missingParent.Describe());

            // 删除
            TreeEditResult remove = TreeMutation.Remove(runtime, "w_new");
            result.Check("ai.edit.remove takes the node out",
                remove.Applied && runtime.Root.Find("w_new") == null,
                remove.Describe());

            TreeEditResult rootRemove = TreeMutation.Remove(runtime, "root");
            result.Check("the root cannot be removed",
                !rootRemove.Applied && rootRemove.Reason != null && rootRemove.Reason.Contains("root"),
                rootRemove.Describe());

            // 删空组合：默认拒绝，force=true 才允许
            var single = runtime.Root.Find("greet") != null
                ? (BtCompositeNode)runtime.Root.Find("greet") : null;
            if (single != null)
            {
                while (single.ChildNodes.Count > 1)
                    TreeMutation.Remove(runtime, single.ChildNodes[0].Id);
                string lastChildId = single.ChildNodes[0].Id;
                int childCount = single.ChildNodes.Count;

                TreeEditResult emptyRefused = TreeMutation.Remove(runtime, lastChildId);
                TreeEditResult emptyForced = childCount == 1
                    ? TreeMutation.Remove(runtime, lastChildId, true) : null;

                result.Check("removing the last child needs force=true",
                    emptyForced != null && !emptyRefused.Applied && emptyForced.Applied
                    && single.ChildNodes.Count == 0,
                    "refused=" + emptyRefused.Applied + " forced="
                    + (emptyForced != null && emptyForced.Applied));
            }

            // 搬移
            Fixture second = Create("pai-edit-move");
            BtRuntime secondRuntime = second.Host.Tree;
            LoadedPackage secondOwner = second.Reloader.ActiveSet.Root;

            var moveReport = new PackageReport();
            ScbtNodeDoc waitDoc2 = ParseNodeDoc(
                @"{ ""id"": ""w_move"", ""type"": ""Task.Wait"", ""properties"": { ""seconds"": 1 } }",
                moveReport);
            TreeMutation.Insert(secondRuntime, "greet", -1, waitDoc2, secondOwner);

            TreeEditResult moved = TreeMutation.Move(secondRuntime, "w_move", "sel", -1);
            result.Check("ai.edit.move re-parents a node",
                moved.Applied && ((BtCompositeNode)secondRuntime.Root.Find("root")) != null
                && secondRuntime.Root.Find("w_move") != null
                && ReferenceEquals(secondRuntime.Root.Find("w_move").ParentNode,
                    secondRuntime.Root.Find("sel")),
                moved.Describe());

            TreeEditResult intoSelf = TreeMutation.Move(secondRuntime, "greet", "greet", -1);
            result.Check("moving a node into itself is rejected", !intoSelf.Applied,
                intoSelf.Describe());

            TreeEditResult intoTask = TreeMutation.Move(secondRuntime, "w_move", "move", -1);
            result.Check("moving under a task is rejected", !intoTask.Applied,
                intoTask.Describe());

            TreeEditResult moveRoot = TreeMutation.Move(secondRuntime, "root", "greet", -1);
            result.Check("the root cannot be moved", !moveRoot.Applied, moveRoot.Describe());
        }

        private static void EditSafety(BtSelfTest.TestResult result)
        {
            Fixture fixture = Create("pai-edit-safety");
            BtRuntime runtime = fixture.Host.Tree;

            // 让树真跑起来：给服务一个目标 → 观察者抢占 → MoveTo 按住 w
            fixture.Host.TestSensor.SetTarget(new AiActorView
            {
                Name = "basil",
                Position = new Vector3(0f, 0f, 10f),
                Distance = 10f
            });
            runtime.Start();
            for (int i = 0; i < 90; i++)
                runtime.Tick(Dt);

            var move = runtime.Root.Find("move") as BtMoveToTargetTask;
            int releases = fixture.Host.TestActuator.ReleaseAllCalls;
            result.Check("setup: the live tree is running and holding the forward key",
                move != null && move.IsActive && fixture.Host.TestActuator.HeldKeys.Contains("w"),
                "active=" + (move != null && move.IsActive));

            TreeEditResult removed = TreeMutation.Remove(runtime, "move");
            result.Check("removing a running node is allowed (with a working parent)",
                removed.Applied, removed.Describe());
            result.Check("removing a running node releases its input (no ghost walking)",
                !fixture.Host.TestActuator.HeldKeys.Contains("w")
                && fixture.Host.TestActuator.ReleaseAllCalls > releases,
                "keys=" + fixture.Host.TestActuator.HeldKeys.Count + " releaseAll="
                + (fixture.Host.TestActuator.ReleaseAllCalls - releases));

            result.Check("the tree keeps ticking after an edit",
                runtime.IsRunning && runtime.LastError == null,
                "running=" + runtime.IsRunning + " error=" + runtime.LastError);
        }

        private static void Export(BtSelfTest.TestResult result)
        {
            Fixture fixture = Create("pai-edit-export");
            BtRuntime runtime = fixture.Host.Tree;

            TreeMutation.SetProperty(runtime, "move", "acceptableRadius", PackageValue.Number(9));
            int liveNodes = TreeMutation.Count(runtime.Root);

            string directory = fixture.Roots.InstanceRoot.Path;
            Directory.CreateDirectory(directory);

            string path;
            string error;
            int nodes;
            bool ok = TreeWriter.TryExportPackage(runtime, fixture.Reloader.ActiveSet.Root.Manifest,
                "exported_demo", directory, out path, out error, out nodes);
            result.Check("ai.tree.export writes a new package",
                ok && path != null && File.Exists(path), error ?? "<no path>");
            result.Check("export counts the live nodes",
                nodes == liveNodes, "nodes=" + nodes + " live=" + liveNodes);
            result.Check("*** export does not touch the source package ***",
                string.Equals(HashOf(fixture.DemoPath), fixture.OriginalHash,
                    StringComparison.OrdinalIgnoreCase),
                "source hash changed after export");

            // 导出的新包必须能被正常装载 + 编译，且带着运行时改动
            var options = new PackageLoadOptions { Roots = fixture.Roots };
            ScbtPackageSet set = PackageLoader.Load(path, options);
            result.Check("the exported package loads without errors",
                set.Root != null && !set.HasErrors, set.Describe() + " :: " + string.Join(" | ", set.Summarize(4).ToArray()));

            if (set.Root != null)
            {
                result.Check("exported manifest takes the new identity",
                    set.Root.Manifest.Id == "exported_demo" && set.Root.Manifest.Entry == "root",
                    set.Root.Manifest.ToString());
                result.Check("exported manifest keeps the nested references",
                    set.Root.Manifest.References.Count == 1
                    && set.Root.Manifest.References[0].Path == PackageTemplates.CommonFile,
                    "refs=" + set.Root.Manifest.References.Count);

                CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
                result.Check("the exported package compiles", !compiled.HasErrors
                    && compiled.Root != null, compiled.Report.Summary());

                var exportedMove = compiled.Root.Find("move") as BtMoveToTargetTask;
                result.Check("the export carries the in-memory edit",
                    exportedMove != null && Math.Abs(exportedMove.AcceptableRadius - 9f) < 1e-4f,
                    exportedMove == null ? "<missing>" : exportedMove.AcceptableRadius.ToString());
                result.Check("the export keeps nested subtrees as references (not flattened)",
                    compiled.Root.Find("sub_look") is BtSubtreeTask
                    && ((BtSubtreeTask)compiled.Root.Find("sub_look")).Subtree != null,
                    "subtree reference was lost");
                result.Check("the export keeps decorators and services",
                    compiled.Root.Find("greet").Decorators.Count == 1
                    && ((BtCompositeNode)compiled.Root.Find("sel")).Services.Count == 1,
                    "decorator or service lost in export");
            }

            // 再导出一次到同名（writer 允许覆盖；命令层负责 already_exists 的询问）
            bool again = TreeWriter.TryExportPackage(runtime, fixture.Reloader.ActiveSet.Root.Manifest,
                "exported_demo", directory, out path, out error, out nodes);
            result.Check("re-exporting to the same name is allowed at the writer level",
                again && error == null, error ?? "<ok>");
        }
    }
}

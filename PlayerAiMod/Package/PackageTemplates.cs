using Engine;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>一个出厂模板包。</summary>
    public sealed class PackageTemplate
    {
        public PackageTemplate(string fileName, string name, string description, PackageValue manifest,
            PackageValue tree)
        {
            FileName = fileName;
            Name = name;
            Description = description;
            Manifest = manifest;
            Tree = tree;
        }

        public string FileName { get; }

        public string Name { get; }

        public string Description { get; }

        public PackageValue Manifest { get; }

        public PackageValue Tree { get; }

        public byte[] ToBytes()
        {
            return PackageWriter.ToBytes(Manifest, Tree, Description);
        }

        public override string ToString()
        {
            return FileName + " (" + Name + ")";
        }
    }

    /// <summary>
    /// 出厂示例包（P0-6）：**随 Mod 分发**的只读模板。
    ///
    /// 约定（计划 §4.4）：模板装到 `<实例根>/Mods/PlayerAiMod/PlayerAi/BehaviorTrees/`；
    /// 用户想改就把它复制到 `<实例根>/PlayerAi/BehaviorTrees/`（实例目录优先）。
    /// 安装**从不覆盖已有文件** —— 用户改过的包不会被 Mod 更新冲掉。
    ///
    /// 示例内容（等价原来的手写 FSM 示例）：
    /// <code>
    /// Root
    /// └─ Selector  [服务 UpdateNearestPlayer(nameFilter=basil) → 黑板 target/targetDistance]
    ///    ├─ Sequence  [装饰器 Blackboard(target, IsSet) + LowerPriority 抢占]
    ///    │  ├─ Subtree  → common.scbtpak#greet.look（看向目标，演示**嵌套引用**）
    ///    │  └─ MoveTo   走到 3 m 以内（超时 25 s）
    ///    └─ Wait 1 s     （待机：没有 basil 时就在这儿循环）
    /// </code>
    /// </summary>
    public static class PackageTemplates
    {
        public const string DemoFile = "demo.greet.scbtpak";
        public const string CommonFile = "common.scbtpak";

        /// <summary>示例动作包文件名（P1）。</summary>
        public const string SampleActionFile = PlayerAiPackages.SampleActionName + ScatManifest.Extension;

        /// <summary>测试行为树文件名（播放示例动作包）。</summary>
        public const string TestTreeFile = PlayerAiPackages.TestTreeName + PackageRoots.Extension;

        /// <summary>出厂模板列表（顺序即安装顺序）。</summary>
        public static List<PackageTemplate> All()
        {
            return new List<PackageTemplate>
            {
                BuildCommon(),
                BuildDemo(),
                PlayerAiPackages.BuildTestTreeTemplate()
            };
        }

        /// <summary>出厂内容里**不是包**的那些文件（示例动作包是二进制轨道，单独写）。</summary>
        public static List<string> ExtraFiles()
        {
            return new List<string> { SampleActionFile };
        }

        public static PackageTemplate Demo()
        {
            return BuildDemo();
        }

        public static PackageTemplate Common()
        {
            return BuildCommon();
        }

        /// <summary>
        /// 把出厂模板装进 Mod 只读分发目录（缺什么补什么，绝不覆盖已有文件）。
        /// 返回新写入的文件数；<paramref name="installed"/> 里是实际写入的路径。
        /// </summary>
        public static int Install(PackageRoots roots, out List<string> installed, out string error)
        {
            installed = new List<string>();
            error = null;

            if (roots == null)
            {
                error = "no package roots";
                return 0;
            }

            PackageRoot target = roots.ModRoot;
            if (target == null)
            {
                error = "no Mod distribution folder is configured";
                return 0;
            }

            try
            {
                Directory.CreateDirectory(target.Path);
            }
            catch (Exception exception)
            {
                error = "cannot create " + target.Path + ": " + exception.Message;
                return 0;
            }

            int written = 0;

            // 示例动作包（`.scatpak`：二进制轨道，不是 .scbtpak 那种文本包）
            string actionPath = System.IO.Path.Combine(target.Path, SampleActionFile);
            if (!File.Exists(actionPath))
            {
                string actionError;
                if (PackageWriter.TryWriteFile(actionPath,
                    PlayerAiPackages.BuildSampleActionPackage(), out actionError))
                {
                    installed.Add(actionPath);
                    written++;
                }
                else
                {
                    error = "cannot write " + actionPath + ": " + actionError;
                }
            }

            List<PackageTemplate> templates = All();
            for (int i = 0; i < templates.Count; i++)
            {
                PackageTemplate template = templates[i];
                string path = System.IO.Path.Combine(target.Path, template.FileName);
                if (File.Exists(path))
                    continue;

                string writeError;
                if (!PackageWriter.TryWritePackage(path, template.Manifest, template.Tree,
                    template.Description, out writeError))
                {
                    error = "cannot write " + path + ": " + writeError;
                    continue;
                }
                installed.Add(path);
                written++;
            }

            if (written > 0)
                Log.Information("[PlayerAi][pkg] installed " + written + " template package(s) into "
                    + target.Path);
            return written;
        }

        // ---------------------------------------------------------------- 模板内容

        private static PackageTemplate BuildDemo()
        {
            PackageValue blackboard = Arr(
                Obj("name", Str("target"), "type", Str("actor"), "readonly", Bool(true),
                    "description", Str("最近的其它玩家（由服务 UpdateNearestPlayer 写入）")),
                Obj("name", Str("targetDistance"), "type", Str("float"), "readonly", Bool(true),
                    "description", Str("到目标的距离（米），同样由服务写入")));

            PackageValue references = Arr(
                Obj("id", Str("common"), "path", Str(CommonFile)));

            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str("demo.greet"),
                "name", Str("示例·打招呼"),
                "entry", Str("root"),
                "blackboard", blackboard,
                "references", references);

            PackageValue tree = Node("root", "Root", null, null, null, Arr(
                Node("sel", "Selector", null, null,
                    Arr(ServiceEntry("svc_target", "UpdateNearestPlayer", Obj(
                        "interval", Num(0.25),
                        "properties", Obj(
                            "targetKey", Str("target"),
                            "nameFilter", Str("basil"),
                            "clearWhenMissing", Bool(true))))),
                    Arr(
                        Node("greet", "Sequence", null,
                            Arr(Obj("id", Str("d_target"), "type", Str("Blackboard"),
                                "observerAborts", Str("LowerPriority"),
                                "properties", Obj("key", Str("target"), "query", Str("IsSet")))), null,
                            Arr(
                                Node("sub_look", "Task.Subtree",
                                    Obj("package", Str("common#greet.look"))),
                                Node("move", "Task.MoveTo", Obj(
                                    "targetKey", Str("target"),
                                    "acceptableRadius", Num(3),
                                    "timeout", Num(25))))),
                        Node("idle", "Task.Wait", Obj("seconds", Num(1)))))));

            return new PackageTemplate(DemoFile, "示例·打招呼", DemoDescription, manifest, tree);
        }

        private static PackageTemplate BuildCommon()
        {
            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str("common"),
                "name", Str("公共子树"),
                "entry", Str("greet.look"),
                "blackboard", Arr(
                    Obj("name", Str("target"), "type", Str("actor"), "readonly", Bool(true))));

            // 入口就是文档根：一个只做"看向目标"的小子树，供别的包用 Task.Subtree 引用。
            PackageValue tree = Node("greet.look", "Sequence", null, null, null, Arr(
                Node("look", "Task.LookAt", Obj(
                    "targetKey", Str("target"),
                    "eyeHeight", Num(1.35),
                    "seconds", Num(0.25)))));

            return new PackageTemplate(CommonFile, "公共子树", CommonDescription, manifest, tree);
        }

        /// <summary>示例动作包与测试树的说明（写进模板的 meta/description.md）。</summary>
        public const string SampleActionNote =
            "sample_walk.scatpak —— 出厂示例动作包（P1）：走 1s → 边走边左转 0.4s → 走+跳 0.2s → "
            + "侧移 0.2s → 停 0.2s，共 2s / 120 帧，固定 60 FPS，数据完全确定（可直接当回归基准）。"
            + "样例**只有起点这一条关键帧**：中途关键帧记的是绝对世界坐标，回放时会逐条比对"
            + "（容差 3m，超限即失败且不瞬移纠偏），样例不带虚构坐标才能在任意世界/任意位置播放；"
            + "真实录制照旧每 0.5s 记一条。"
            + "由 test.action.scbtpak 播放，也可以 sccmd ai action play sample_walk 直接回放。";

        private const string DemoDescription =
            "# 示例·打招呼（demo.greet.scbtpak）\n" +
            "\n" +
            "出厂示例包，等价于早期手写 FSM 的那条链：**看向目标 → 走近 3 m → 待机循环**。\n" +
            "\n" +
            "- `Selector.sel` 上挂了服务 `UpdateNearestPlayer`：每 0.25 s 把最近的其它玩家\n" +
            "  （只认名字 `basil`）写进黑板 `target` / `targetDistance`。\n" +
            "- `Sequence.greet` 的条件装饰器 `Blackboard(target, IsSet)` 配 `observerAborts: LowerPriority`：\n" +
            "  一旦目标出现就抢占后面那条 `Wait` 分支（这就是“看到人就动”的抢占语义）。\n" +
            "- `Task.Subtree(sub_look)` 引用 `common.scbtpak#greet.look`，演示**包嵌套**。\n" +
            "- `Task.MoveTo(move)` 按住前进键走到 3 m 内；被抢占时会在 `OnExit` 里松开按键。\n" +
            "\n" +
            "改法：把它复制到 `<实例根>/PlayerAi/BehaviorTrees/` 再改（实例目录优先于 Mod 目录），\n" +
            "或直接用编辑器打开本文件（保存后编辑器会通知游戏热重载）。\n";

        private const string CommonDescription =
            "# 公共子树（common.scbtpak）\n" +
            "\n" +
            "被别的包用 `Task.Subtree` 引用的最小子树：只做“看向黑板里的目标”。\n" +
            "入口是 `greet.look`，所以引用写法是 `\"package\": \"common#greet.look\"`。\n";

        // ---------------------------------------------------------------- JSON 构造小工具

        private static PackageValue Obj(params object[] namesAndValues)
        {
            PackageValue value = PackageValue.Object();
            for (int i = 0; i + 1 < namesAndValues.Length; i += 2)
                value.Set((string)namesAndValues[i], (PackageValue)namesAndValues[i + 1]);
            return value;
        }

        private static PackageValue Arr(params PackageValue[] items)
        {
            return PackageValue.Array(items);
        }

        private static PackageValue Str(string text)
        {
            return PackageValue.Str(text);
        }

        private static PackageValue Num(double number)
        {
            return PackageValue.Number(number);
        }

        private static PackageValue Bool(bool value)
        {
            return PackageValue.Bool(value);
        }

        private static PackageValue Node(string id, string type)
        {
            return Node(id, type, null, null, null, null);
        }

        private static PackageValue Node(string id, string type, PackageValue properties)
        {
            return Node(id, type, properties, null, null, null);
        }

        private static PackageValue Node(string id, string type, PackageValue properties,
            PackageValue decorators, PackageValue services, PackageValue children)
        {
            PackageValue node = Obj("id", Str(id), "type", Str(type));
            if (properties != null && properties.Count > 0)
                node.Set("properties", properties);
            if (decorators != null && decorators.Count > 0)
                node.Set("decorators", decorators);
            if (services != null && services.Count > 0)
                node.Set("services", services);
            if (children != null && children.Count > 0)
                node.Set("children", children);
            return node;
        }

        /// <summary>服务条目（id/type + 间隔 + properties）：服务不是节点，单独一个构造入口。</summary>
        private static PackageValue ServiceEntry(string id, string type, PackageValue serviceBody)
        {
            PackageValue service = Obj("id", Str(id), "type", Str(type));
            if (serviceBody != null)
            {
                foreach (string name in new List<string>(serviceBody.MemberNames))
                    service.Set(name, serviceBody.Get(name));
            }
            return service;
        }
    }
}

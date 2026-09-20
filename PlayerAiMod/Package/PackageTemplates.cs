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
    /// 出厂示例包（P0-6）：**随 Mod 分发**的模板。
    ///
    /// 约定（计划 §4.4，2026-09-12 简化为单一目录）：模板装到
    /// `<实例根>/PlayerAi/BehaviorTrees/` —— 就是唯一的那个包目录，
    /// 用户要改也改在同一份上（编辑器直接改，不再有"复制到实例目录"这一步）。
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

        /// <summary>
        /// 示例：**盯住最近的玩家**（反应式树的标准写法）。
        ///
        /// 名字用 `su.` 前缀（用户要求）：`su.watch.scbtpak`。
        /// </summary>
        public const string WatchFile = "su.watch.scbtpak";

        /// <summary>示例：**跟随最近的玩家**（先靠近 → 踩他走过的格子 → 不行就 A* 绕）。</summary>
        public const string FollowFile = "su.follow.scbtpak";

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
                BuildWatch(),
                BuildFollow(),
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

        public static PackageTemplate Watch()
        {
            return BuildWatch();
        }

        /// <summary>
        /// **示例·跟随最近的玩家**（`su.follow.scbtpak`）：先靠近 → 尽量踩他走过的格子 →
        /// 走不通就借游戏 A* 绕过去 → 全程保持 2 格**水平**间隔。
        ///
        /// <code>
        /// Root(loop=true)
        /// └─ Sequence.seq        services: UpdateNearestPlayer(targetKey=player, interval=0)
        ///    └─ Selector.sel
        ///       ├─ Sequence.follow [装饰器 Blackboard(player IsSet)]
        ///       │  └─ Task.FollowEntity  targetKey=player  mode=auto  keepDistance=2
        ///       └─ Task.Wait 0.2s     ← 没人在附近就待机（别每帧报"目标没了"）
        /// </code>
        ///
        /// **`interval` 必须是 0（每帧）**：黑板里存的是 `AiActorView` **值类型快照**，
        /// 服务多久写一次，跟随看到的就多久之前的目标位置 —— 0.2s 的间隔配上 4.5 m/s 的跑动
        /// 就是"镜头一顿一顿地追"（用户实测："视角跟踪有些不连贯，一段一段地在跳"）。
        ///
        /// `mode` 三种取值对应"跟随分两种（外加自动）"：
        /// `trail` 只踩脚印、`path` 只借 A* 绕、`auto`（默认）脚印优先、走不通自动切 A*。
        /// </summary>
        private static PackageTemplate BuildFollow()
        {
            PackageValue blackboard = Arr(
                Obj("name", Str("player"), "type", Str("actor"), "readonly", Bool(true),
                    "description", Str("最近的其它玩家（服务每帧刷新，interval=0）")));

            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str("su.follow"),
                "name", Str("示例·跟随最近的玩家"),
                "entry", Str("root"),
                "blackboard", blackboard);

            PackageValue followBranch = Node("branch", "Sequence",
                null,
                Arr(Obj("id", Str("d_player"), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj("key", Str("player"), "query", Str("IsSet")))),
                null,
                Arr(Node("follow", "Task.FollowEntity", Obj(
                    "targetKey", Str("player"),
                    "mode", Str("auto"),
                    "keepDistance", Num(2),
                    "trailMaxAge", Num(3),
                    "timeout", Num(0)))));

            PackageValue tree = Node("root", "Root", Obj("loop", Bool(true)), null, null, Arr(
                Node("seq", "Sequence", null, null,
                    Arr(ServiceEntry("svc_player", "UpdateNearestPlayer", Obj(
                        "interval", Num(0),
                        "properties", Obj(
                            "targetKey", Str("player"),
                            "clearWhenMissing", Bool(true))))),
                    Arr(
                        Node("sel", "Selector", null, null, null, Arr(
                            followBranch,
                            Node("idle", "Task.Wait", Obj("seconds", Num(0.2)))))))));

            return new PackageTemplate(FollowFile, "示例·跟随最近的玩家", FollowDescription,
                manifest, tree);
        }

        /// <summary>
        /// 把出厂模板装进**包目录**（`&lt;实例根&gt;/PlayerAi/BehaviorTrees`；缺什么补什么，
        /// **绝不覆盖已有文件**）。返回新写入的文件数；<paramref name="installed"/> 里是实际写入的路径。
        ///
        /// 这是游戏侧唯一的"写新包"动作之一，边界与 `ai.tree.export` 一致：
        /// 只创建不存在的文件；想改已有包只能由编辑器改（或游戏侧显式 overwrite=true 的那两条命令）。
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

            PackageRoot target = roots.InstanceRoot;
            if (target == null)
            {
                error = "no package folder is configured";
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

        /// <summary>
        /// **示例·盯住最近的玩家**（`su.watch.scbtpak`）：反应式行为树的标准写法。
        ///
        /// <code>
        /// Root(loop=true)
        /// └─ Sequence                      ← 服务挂在这里（Interval=0 = 每帧刷新）
        ///    │  UpdateNearestPlayer  targetKey=player  clearWhenMissing=true
        ///    │  UpdateLineOfSight    targetKey=player  key=canSee  maxDistance=48
        ///    └─ Selector
        ///       ├─ Sequence [装饰器 Blackboard(canSee == true)]
        ///       │  └─ Task.LookAt  targetKey=player  eyeHeight=1.55  seconds=0.1
        ///       └─ Task.Wait 0.1s
        /// </code>
        ///
        /// 为什么这么拼（三个事实各自对应一条设计）：
        ///   · 玩家会动 → 位置必须在**每帧**是新的：服务 `interval=0`（默认 0.25s 会让眼睛
        ///     追着 0.25 秒前的位置跑，看起来一顿一顿）；
        ///   · `Task.LookAt` 是潜在任务，在 `seconds` 期间**每个 tick 都重新对准**，
        ///     所以它一挂上就是"持续盯着"，不需要每帧重进树；
        ///   · "看不见就不动"**不需要任何节点**：写视角的入口只有 LookAt/Look/LookDelta，
        ///     条件不成立时这一帧没有任何写入 → 视角停在原处（也不会被归位）。
        /// </summary>
        private static PackageTemplate BuildWatch()        {
            PackageValue blackboard = Arr(
                Obj("name", Str("player"), "type", Str("actor"), "readonly", Bool(true),
                    "description", Str("最近的其它玩家（网络玩家也算，服务每帧刷新）")),
                Obj("name", Str("node.X"), "type", Str("float"), "readonly", Bool(true),
                    "description", Str("目标模型节点（默认 Head）的世界坐标 X")),
                Obj("name", Str("node.Y"), "type", Str("float"), "readonly", Bool(true)),
                Obj("name", Str("node.Z"), "type", Str("float"), "readonly", Bool(true)),
                Obj("name", Str("node.Exists"), "type", Str("bool"), "readonly", Bool(true),
                    "description", Str("模型上有没有这个节点（名字写错时是 false）")),
                Obj("name", Str("seeHead"), "type", Str("bool"), "readonly", Bool(true),
                    "description", Str("从我的眼睛到**他的头骨骼**这条射线通不通（这只判地形）")),
                Obj("name", Str("canSee"), "type", Str("bool"), "readonly", Bool(true),
                    "description", Str("旧口径：到「脚底 + 1.55 m」的视线是否通畅，留着做对照")),
                Obj("name", Str("canSeeDistance"), "type", Str("float"), "readonly", Bool(true)));

            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str("su.watch"),
                "name", Str("示例·盯住最近的玩家"),
                "entry", Str("root"),
                "blackboard", blackboard);

            // 看得见的那条分支：装饰器当门（seeHead == true，射线打到他的头），门里把视线锁在头骨骼上
            PackageValue watchBranch = Node("watch", "Sequence",
                null,
                Arr(Obj("id", Str("d_seehead"), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj(
                        "key", Str("seeHead"),
                        "query", Str("Compare"),
                        "valueKind", Str("bool"),
                        "operator", Str("=="),
                        "value", Bool(true)))),
                null,
                Arr(
                    // 首选：瞄**头骨骼**（模型节点跟踪给出的真实坐标）
                    Node("look", "Task.LookAtPoint", Obj(
                        "xKey", Str("node.X"),
                        "yKey", Str("node.Y"),
                        "zKey", Str("node.Z"),
                        "seconds", Num(0.1)))));

            PackageValue tree = Node("root", "Root", Obj("loop", Bool(true)), null, null, Arr(
                Node("seq", "Sequence", null, null,
                    Arr(ServiceEntry("svc_player", "UpdateNearestPlayer", Obj(
                            "interval", Num(0),
                            "properties", Obj(
                                "targetKey", Str("player"),
                                "clearWhenMissing", Bool(true)))),
                        // 模型节点：把"他的头"在世界里的精确坐标写进 node.X/Y/Z
                        ServiceEntry("svc_head", "UpdateModelNode", Obj(
                            "interval", Num(0),
                            "properties", Obj(
                                "targetKey", Str("player"),
                                "nodeName", Str("Head"),
                                "prefix", Str("node.")))),
                        // 通用射线：**从我的眼睛打向他的头骨骼** —— 这就是"看得见他的头吗"。
                        // mode=clear 时主键的含义是"通畅到达终点"，所以 seeHead == true 就是看得见
                        // （默认的 blocked 口径是反的，用它写条件容易读错）。
                        ServiceEntry("svc_probe", "Probe", Obj(
                            "interval", Num(0),
                            "properties", Obj(
                                "from", Str("eye"),
                                "to", Str("point"),
                                "toXKey", Str("node.X"), "toYKey", Str("node.Y"),
                                "toZKey", Str("node.Z"),
                                "mode", Str("clear"),
                                "key", Str("seeHead"),
                                "maxDistance", Num(48)))),
                        // 旧口径留着做对照（固定高度 1.55 的地形视线）
                        ServiceEntry("svc_los", "UpdateLineOfSight", Obj(
                            "interval", Num(0),
                            "properties", Obj(
                                "targetKey", Str("player"),
                                "key", Str("canSee"),
                                "maxDistance", Num(48),
                                "targetEyeHeight", Num(1.55),
                                "alsoCheckBody", Bool(true),
                                "bodyHeight", Num(0.9))))),
                    Arr(
                        Node("sel", "Selector", null, null, null, Arr(
                            watchBranch,
                            // 看不见：什么都不做（视角保持不动）。这 0.1s 同时是"别空转"的限流
                            Node("idle", "Task.Wait", Obj("seconds", Num(0.1)))))))));

            return new PackageTemplate(WatchFile, "示例·盯住最近的玩家", WatchDescription,
                manifest, tree);
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

        private const string FollowDescription =
            "# 示例·跟随最近的玩家（su.follow.scbtpak）\n" +
            "\n" +
            "一个任务干完三件事：**先靠近 → 尽量踩他走过的格子 → 走不通就借游戏 A\\* 绕过去**，\n" +
            "全程保持 **2 格水平间隔**（到了就站住，不往人身上挤）。\n" +
            "\n" +
            "| 情况 | `Task.FollowEntity` 怎么做 |\n" +
            "|---|---|\n" +
            "| 目标还没有足迹（刚出现/在远处） | 借 A\\* 走过去 —— 这就是「先靠近」 |\n" +
            "| 目标前面走过一串格子 | 追**下一个还没踩过的脚印**（走他走的路，最省事也最像人）|\n" +
            "| 脚印太旧 / 追着追着走不动了（卡住）| **丢掉脚印改走 A\\*** —— 这就是「不行就绕过去」 |\n" +
            "| 水平距离已经 ≤ `keepDistance` | **站住**（只转向不前进）|\n" +
            "\n" +
            "`mode` 就是「跟随分两种」那个开关：\n" +
            "\n" +
            "- `auto`（默认）：脚印优先，走不通自动切 A\\*；\n" +
            "- `trail`：只走脚印（没脚印就朝目标直走）；\n" +
            "- `path`：只用 A\\*（始终绕过去）。\n" +
            "\n" +
            "铁律不变：A\\* 只是**只读查询**（借游戏自己的寻路器），跟随只靠**注入输入**\n" +
            "（转视角 + 按住前进键 + 上台阶时跳一下）。目标从黑板消失即失败，由树决定怎么办。\n";

        private const string WatchDescription =
            "# 示例·盯住最近的玩家（su.watch.scbtpak）\n" +
            "\n" +
            "**反应式**行为树的标准写法：目标（另一个玩家）一直在动，可见性还会被地形挡住。\n" +
            "整棵树只有 6 个节点，靠三件事成立：\n" +
            "\n" +
            "1. **服务每帧刷新黑板**（挂在 `Sequence.seq` 上，`interval=0`）：\n" +
            "   `UpdateNearestPlayer` 把**最近的其它玩家**写进 `player`（自动排除自己，\n" +
            "   网络玩家只是一个没有控制器的角色，位置照读），\n" +
            "   `UpdateLineOfSight` 把“地形挡不挡”写进 `canSee`。\n" +
            "   间隔必须小：默认 0.25 s 会让眼睛追着 0.25 秒前的位置跑，看起来一顿一顿。\n" +
            "2. **装饰器当门**：`Sequence.watch` 上的 `Blackboard(canSee == true)`（配\n" +
            "   `observerAborts: LowerPriority`）—— 看得见才进这条分支。\n" +
            "3. **`Task.LookAt` 一挂上就是持续盯着**：它是潜在任务，在 `seconds` 期间**每个\n" +
            "   tick 都重新对准**黑板里那个 actor 的新位置，所以不需要每帧重进树。\n" +
            "\n" +
            "三种状态（这就是本示例要演示的语义）：\n" +
            "\n" +
            "| 情况 | 结果 |\n" +
            "|---|---|\n" +
            "| 看得见（他在跑动） | 身体与视线一直跟着他转（每 tick 对准一次）|\n" +
            "| 看不见（墙/山挡住，或他走远超过 48 m） | 只有 `Task.Wait` 在跑 —— **这一帧没有任何视角写入，视线停在原地不动**（不会归位、不会漂）|\n" +
            "| 他重新回到可观察位置 | `canSee` 变 true，下一个 tick 立刻重新锁定（≤1 帧）|\n" +
            "\n" +
            "“看不见就不动”**不需要专门的节点**：写视角的入口只有 `LookAt/Look/LookDelta`，\n" +
            "没人调用就没人改，而 `ReleaseAll()` 只放按键和鼠标、不碰视角。\n" +
            "\n" +
            "改法：复制到 `<实例根>/PlayerAi/BehaviorTrees/` 再改（想盯特定的人就给服务填\n" +
            "`nameFilter=<玩家名>`；想更像真人转头，可以让 `Task.LookAt` 换成带转向速度限制的\n" +
            "`Task.FaceEntity` 做身体、`Task.LookAt` 做眼睛）。\n";

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
